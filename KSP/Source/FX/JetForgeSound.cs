using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using JetForge.Core;
using UnityEngine;

namespace JetForge.FX
{
    /// <summary>
    /// 喷气发动机音效。
    ///
    /// 上一版是"三层 × 一条线性公式"，实飞听着就是一层噪声，因为真实喷气声是**很多条不同机理的
    /// 声音叠起来的**：外涵风扇的通过频率那一级"呜——"、核心机共振、低频轰鸣、加力的爆燃与啸叫，
    /// 每一条都跟不同的物理量走、也各有自己的变调范围。
    ///
    /// 所以这里改成 RocketSoundEnhancement 那种做法（RSE 为 GPL-3.0，用户已授权）：
    /// **一层 = 一个音源 + 一条音量曲线 + 一条音高曲线**，曲线横轴是油门或换算转速；
    /// 每层的横轴还带自己的时间滞后（RSE 的 spoolSpeed），所以收放油门时各层不是一起变，
    /// 这就是"细"的来源——它们错开动。
    ///
    /// 不是复制 RSE 的代码，是把它的**结构**搬过来，参数按我们自己的物理量标定。
    /// </summary>
    public sealed class JetForgeSound
    {
        /// <summary>
        /// 一条音层的定义。曲线都是"键,值,键,值…"的扁平数组，和 RSE 的 volumeFC/pitchFC 一个意思。
        /// 抽成静态表是为了自检能在没有零件的情况下把曲线算出来看——不然"声音对不对"只能靠耳朵。
        /// </summary>
        public sealed class Spec
        {
            public string Clip;
            public string Name;
            public bool UseSpool;   // 横轴用换算转速（false = 用喷流状态；两者都由转子推出，不再看油门）
            public float Lag;       // 每层自己的跟随速度，越小越迟钝
            public float Scale;     // 这一层在总混音里的分量
            public float[] VolX, VolY, PitchX, PitchY;
            public bool OnlyHighBypass;
            public bool OnlyAfterburner;
        }

        // 素材全部由 tools/gen_sounds.py 从 RSE 的 JetsTurbines 音源派生（GPL-3.0）。
        private static readonly Spec[] Specs = BuildSpecs();

        private static Spec[] BuildSpecs()
        {
            var list = new List<Spec>();
            // 尾喷/外涵的宽带气流声：油门一推先起来的就是它
            list.Add(Mk("JetForge/Sounds/jf_air", "air", false, 1.00f, 0.55f, false, false,
                Keys(0f, 0.03f, 0.35f, 0.52f, 0.75f, 0.92f, 1f, 1.00f), Keys(0f, 0.76f, 1f, 1.04f)));
            list.Add(Mk("JetForge/Sounds/jf_air2", "air2", false, 0.60f, 0.40f, false, false,
                Keys(0f, 0f, 0.25f, 0.50f, 0.70f, 0.82f, 1f, 0.88f), Keys(0f, 0.84f, 1f, 1.16f)));
            // 共振层是"呜——"的主体，音高跨 4 倍，转速一变就听得出在变调
            list.Add(Mk("JetForge/Sounds/jf_res250", "res250", true, 1.60f, 0.50f, false, false,
                Keys(0f, 0f, 0.25f, 1.00f, 0.50f, 0.34f, 0.75f, 0.50f, 1f, 0.62f),
                Keys(0f, 0.55f, 0.5f, 1.15f, 1f, 2.40f)));
            list.Add(Mk("JetForge/Sounds/jf_res50", "res50", true, 1.20f, 0.55f, false, false,
                Keys(0f, 0f, 0.50f, 0.92f, 1f, 0.55f), Keys(0f, 0.50f, 1f, 1.30f)));
            list.Add(Mk("JetForge/Sounds/jf_big180", "big180", true, 0.80f, 0.45f, false, false,
                Keys(0f, 0.02f, 0.10f, 0.34f, 0.50f, 0.62f, 1f, 0.30f),
                Keys(0f, 0.35f, 0.5f, 0.90f, 1f, 1.90f)));
            // 2.1 kHz 那一层是"细"的来源：窄带高音，要有一定转速才出来
            list.Add(Mk("JetForge/Sounds/jf_whine", "whine", true, 0.30f, 0.30f, false, false,
                Keys(0f, 0.00f, 0.35f, 0.34f, 0.75f, 0.55f, 1f, 0.60f),
                Keys(0f, 0.30f, 0.5f, 0.62f, 1f, 0.95f)));
            // 风扇层只在大涵道机器上出现
            list.Add(Mk("JetForge/Sounds/jf_fan", "fan", false, 0.24f, 0.90f, true, false,
                Keys(0f, 0f, 0.40f, 0.34f, 1f, 0.55f), Keys(0f, 0.62f, 1f, 1.38f)));
            // 加力两层：低频爆燃 + 高频啸叫，横轴是"烧到什么程度"
            list.Add(Mk("JetForge/Sounds/jf_ab_roar", "ab_roar", false, 0.75f, 0.75f, false, true,
                Keys(0f, 0f, 1f, 0.80f), Keys(0f, 0.86f, 1f, 1.12f)));
            list.Add(Mk("JetForge/Sounds/jf_ab_screech", "ab_screech", false, 0.34f, 0.34f, false, true,
                Keys(0f, 0f, 0.6f, 0.22f, 1f, 0.38f), Keys(0f, 0.98f, 1f, 1.22f)));
            return list.ToArray();
        }

        private static Spec Mk(string clip, string name, bool useSpool, float lag, float scale,
                               bool hbOnly, bool abOnly, float[] vol, float[] pitch)
        {
            return new Spec
            {
                Clip = clip, Name = name, UseSpool = useSpool, Lag = lag, Scale = scale,
                OnlyHighBypass = hbOnly, OnlyAfterburner = abOnly,
                VolX = TakeX(vol), VolY = TakeY(vol), PitchX = TakeX(pitch), PitchY = TakeY(pitch)
            };
        }

        public static int SpecCount { get { return Specs.Length; } }

        /// <summary>
        /// 一台机器"听起来是什么样"的参数。以前这些全是常数：不管你做的是 60 cm 的涡喷
        /// 还是 2.6 m 的大涵道，放出来一个音色——玩家说"不同设计听不出区别"，就是这个。
        /// 现在全部从设计几何推：出口半径定音高、级数定高频成分、机型定宽带成分、
        /// 叶尖马赫定超音速那一下的轰鸣。
        /// </summary>
        public struct Voice
        {
            public float Pitch;      // 整机音高倍率（1 = 参考机）
            public float Gain;       // 整机增益倍率
            public float Whine;      // 高频窄带权重（叶片通过频率那一类）
            public float Roar;       // 宽带低频权重（喷流噪声）
            /// <summary>
            /// 风扇层权重（0 = 这台机器没有风扇，1 = 参考大涵道）。
            /// 只由风扇直径与涵道比决定。涡喷的风扇段不存在，这一层必须是 0——
            /// 玩家在涡喷上听见"明显的风扇声"，就是因为它以前只按"是不是高涵道"开关，
            /// 而窄带层（whine）又跟着核心级数一路涨到封顶，听着就像一台风扇。
            /// </summary>
            public float Fan;
            public float TipMach;    // 设计点叶尖马赫（后面乘换算转速就是当前叶尖马赫）
            public float NozzleR;
            public int CoreStages;

            public static Voice Default()
            {
                return new Voice { Pitch = 1f, Gain = 1f, Whine = 1f, Roar = 1f, Fan = 0f, TipMach = 1.0f, NozzleR = 0.35f, CoreStages = 9 };
            }
        }

        private static bool _specWarned;

        /// <summary>
        /// 声音参数算不全时报一次。为什么只报一次：这条路径每帧都会走，报多了就是刷屏，
        /// 而"这一层声音按缺省值播"这件事只需要说一次——玩家要的是"知道哪里不对"，不是日志。
        /// </summary>
        private static void WarnSpecOnce(string what, Exception e)
        {
            if (_specWarned) return;
            _specWarned = true;
            Debug.LogWarning("[JetForge] 声音参数没算全（" + what + "）：" + e.GetType().Name + "：" + e.Message
                + "——这一层按缺省值播，不影响推力");
        }

        /// <summary>按设计算嗓音。所有指数都是为了"量级对"，不是拟合出来的。</summary>
        public static Voice VoiceOf(EngineDesignData d)
        {
            Voice v = Voice.Default();
            if (d == null) return v;

            float rNoz = 0.35f;
            try { rNoz = JetForgeMeshBuilder.NozzleExitRadius(d); }
            // 这一层算不出来声音就会按缺省尺寸播——玩家没法知道为什么声音不对，所以要说一句
            catch (Exception e) { WarnSpecOnce("喷管出口半径", e); }
            v.NozzleR = Mathf.Max(rNoz, 0.05f);

            // 音高：出口越大越沉。0.35 m 参考，半径翻倍降约 0.79。
            // 这个指数别乱调——真机上"大机听着低"主要来自喷流噪声的斯特劳哈尔数，不是随便的比喻。
            v.Pitch = Mathf.Clamp(Mathf.Pow(0.35f / v.NozzleR, 0.32f), 0.70f, 1.45f);

            // 三轴机的高频更"细"：多一根轴就多一组转速不同的叶片通过频率
            int shafts = 1;
            // 探测豁免：没有 ShaftCount 就按单轴算音高
            try { shafts = Mathf.Clamp(d.ShaftCount, 1, 3); } catch (Exception) { }
            v.Pitch += 0.05f * (shafts - 1);

            // 响度：出口面积开方——面积翻 4 倍，声压大致翻倍，主观听着"大一圈"
            float areaRef = Mathf.PI * 0.35f * 0.35f;
            float area = Mathf.PI * v.NozzleR * v.NozzleR;
            v.Gain = Mathf.Clamp(Mathf.Sqrt(area / areaRef), 0.45f, 1.75f);

            // 风扇层权重：只认风扇直径与涵道比。涡喷没有风扇段，这一层必须是 0，
            // 否则一台按 17 级核心走的涡喷会整出"风扇声"——那不是它的声音。
            float fanR = 0f;
            double bpr = 0.0;
            try
            {
                if (d.HasFan) fanR = Mathf.Max(0.05f, (float)(d.Fan.Diameter * 0.5));
                bpr = d.BypassRatio();
            }
            // 这两样决定风扇层与宽带层，算不出来声音会凭空少一层，得说一次
            catch (Exception e) { WarnSpecOnce("风扇直径与涵道比", e); }
            v.Fan = fanR <= 0.001f ? 0f
                : Mathf.Clamp((fanR / 0.5f) * Mathf.Clamp((float)(bpr / 3.0), 0.10f, 1.50f), 0f, 1.60f);

            // 高频成分：压气机级数越多，窄带高频越重；但**风扇占比越小，这一层越该收敛**。
            // 上一版只按级数涨、封顶 1.70，于是一台 17 级涡喷拿到满值——玩家听着就是风扇，
            // 因为风扇声与压气机啸叫在中高频本来就分不开，只能靠这一条把它压回去。
            int core = 0;
            // 探测豁免：取不到级数就当没有压气机啸叫这一层
            try { core = d.Lpc.Stages + d.Hpc.Stages; } catch (Exception) { }
            v.CoreStages = core;
            v.Whine = Mathf.Clamp((0.55f + 0.055f * core) * (0.62f + 0.38f * Mathf.Min(1f, v.Fan)), 0.40f, 1.45f);

            // 宽带成分：涡喷的喷流是裸的，涡扇有一圈冷流包着，听着闷而低
            if (d.Kind == EngineKind.Turbojet) v.Roar = 1.30f;
            else v.Roar = Mathf.Clamp((float)(1.05 - 0.12 * bpr), 0.62f, 1.15f);

            // 设计点叶尖马赫：设计时就是把该轴锁在技术等级给的马赫上限上
            try
            {
                TechLevel tech = d.Tech;
                if (tech != null)
                {
                    double lim = d.HasFan ? tech.MaxTipMachFan : tech.MaxTipMachComp;
                    if (d.Kind == EngineKind.Turbojet && d.Lpc.Stages > 0) lim = tech.MaxTipMachComp;
                    v.TipMach = (float)Math.Max(lim, 0.8);
                }
            }
            // 叶尖马赫只影响高频的"薄厚"，缺省 1.0 不会出错，所以这句话说得轻一点
            catch (Exception e) { WarnSpecOnce("设计叶尖马赫", e); }
            return v;
        }

        /// <summary>
        /// 叶尖超音速那一耳朵。0.95 马赫起，1.40 满：满值 2.9 倍。
        /// 真机上这是"激波从叶尖拖出来"的宽带噪声，频率成分很宽、听起来像撕裂，
        /// 不是简单把音量推大——所以同时降音高（见 Update 里的 -12%）。
        /// </summary>
        /// <summary>
        /// 相对**设计点**的超音速增益。上一版这里拿的是"技术等级允许的叶尖马赫上限"，
        /// 而设计点本来就顶在这个上限上（风机被锁在马赫上限），于是任何一台 1990s
        /// 之后的机器一踩满油门就白拿 2.6 倍音量——玩家说"叶尖超音速的声音太大"，
        /// 说的就是这个：**它把设计点当成了超速点**。
        ///
        /// 现在：设计点 = 1.0（顶多带一点点激波底噪），超过设计之后才涨，
        /// 0.12 马赫一个台阶，涨到 1.85 倍封顶。
        /// </summary>
        public static float ShockGain(double tipMach, double designTipMach)
        {
            if (designTipMach <= 0.2) designTipMach = 1.0;
            double over = (tipMach - designTipMach) / 0.12;
            if (over < 0.0) over = 0.0;
            if (over > 3.0) over = 3.0;
            double excess = 1.0 + 0.55 * Math.Pow(over, 1.4);
            // 设计点自己就在音速以上的机器，本来就更"撕裂"一点——但那是一条平底，
            // 不是每踩一次油门就往上翻一倍。
            double baseShock = designTipMach > 1.05
                ? 1.0 + 0.22 * Math.Min(1.0, (designTipMach - 1.05) / 0.30) : 1.0;
            double g = baseShock * excess;
            return (float)Math.Min(g, 1.85);
        }

        /// <summary>不知道设计马赫时的兜底（老代码/自检用）。</summary>
        public static float ShockGain(double tipMach)
        {
            return ShockGain(tipMach, 1.0);
        }

        /// <summary>某层吃多少"超音速增益"：宽带层全吃，窄带层只吃三成。</summary>
        private static float ShockShare(string name)
        {
            if (name == "air" || name == "air2" || name == "big180") return 1.00f;
            if (name == "fan") return 0.70f;
            if (name == "whine") return 0.35f;
            return 0.25f;
        }

        private static float RoleGain(string name, Voice v)
        {
            if (name == "whine" || name == "res250") return v.Whine;
            if (name == "air" || name == "air2" || name == "big180") return v.Roar;
            return 1f;
        }

        /// <summary>自检/状态栏用：把嗓音算成可读的一行。</summary>
        public static string DescribeVoice(EngineDesignData d)
        {
            Voice v = VoiceOf(d);
            float m100 = v.TipMach;
            float m115 = v.TipMach * 1.15f;
            return string.Format(
                "音高 {0:F2}｜增益 {1:F2}｜高频 {2:F2}｜宽带 {3:F2}｜风扇 {4:F2}｜出口 {5:F2} m｜核心级数 {6}｜" +
                "叶尖马赫 100%={7:F2} 115%={8:F2}｜轰鸣倍率 {9:F2} → {10:F2}",
                v.Pitch, v.Gain, v.Whine, v.Roar, v.Fan, v.NozzleR, v.CoreStages,
                m100, m115, ShockGain(m100, v.TipMach), ShockGain(m115, v.TipMach));
        }

        private sealed class Layer
        {
            public Spec S;
            public AudioSource Src;
            public float X;   // 平滑后的横轴
        }

        private readonly List<Layer> _layers = new List<Layer>();
        private Part _part;
        private float _gain = 1f;
        private float _size = 1f;
        private bool _highBypass;
        private float _lowpassCut = 20000f;
        private bool _attached;
        /// <summary>
        /// 干态让位系数：RSE 真在放声时我们**完全不响**——干态那几层交给它。
        ///
        /// 上一版是"两边都放，我们降到 0.35"，玩家听出来的却是"有一层声音没被 RSE 处理"：
        /// 我们这几层是自己 AudioSource 直出的，不走 RSE 的多普勒/空气吸收/梳状滤波，
        /// 两套叠在一起时那点"没处理的毛边"反而最扎耳。现在分工明确：
        /// 干态是 RSE 的，加力是我们的（原版喷气的声音组里根本没有加力层）。
        /// </summary>
        private bool _hasAb = true;
        private bool _wasRseOn;
        private float _duckDry = 1f;
        private float _duckAb = 1f;
        public const float RseDryDuck = 0f;
        /// <summary>按设计算出来的"嗓音"：音高/增益/高频权重/宽带权重/叶尖马赫。</summary>
        private Voice _voice = Voice.Default();
        private bool _rseVoiced;
        private int _rseLayersTouched;
        private string _status = "未挂";

        public bool Attached { get { return _attached; } }
        public string Status { get { return _status; } }

        /// <summary>
        /// PAW 上只留"几层在响"。RSE 让位比例、改了几条曲线这些是排查口径，走日志。
        /// 玩家点开零件不需要知道 RSE 的分工——那是我们的活，不是他的读数。
        /// </summary>
        public string Short
        {
            get
            {
                if (!_attached) return "没有声音素材";
                return string.Format("{0} 层｜音量 {1:F2}", _layers.Count, _gain);
            }
        }

        public static bool ClipExists(string path)
        {
            try { return GameDatabase.Instance != null && GameDatabase.Instance.ExistsAudioClip(path); }
            catch (Exception) { return false; }
        }

        /// <summary>RSE 装了没有。装了也不再依赖它——只写进状态栏供排查。</summary>
        public static bool RseInstalled()
        {
            try
            {
                var list = AssemblyLoader.loadedAssemblies;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (a == null) continue;
                    string n = a.name;
                    if (n != null && n.IndexOf("RocketSoundEnhancement", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            // 探测豁免：RSE 组名探测，取不到即未安装
            catch (Exception) { }
            return false;
        }

        public static bool RseOnPart(Part part)
        {
            if (part == null || part.Modules == null) return false;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule m = part.Modules[i];
                if (m == null) continue;
                string n = m.moduleName ?? "";
                if (n.IndexOf("RSE_Engines", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// RSE 在本零件上到底在不在放声。
        ///
        /// 只看"模块在不在"是不够的：RSE_Engines 的 `OnStart` 会按
        /// `SoundLayerGroups[ModuleEngines.engineID]` 取声音组，取不到就整个模块死掉——
        /// 表现正是"装了 RSE 反而一点声都没有"。所以判据取它自己的两个状态位：
        /// `Initialized`（OnStart 走完了）和 `AudioParent`（音源挂在哪个物体上，没挂就是没声）。
        /// </summary>
        public static string RseState(Part part)
        {
            if (part == null || part.Modules == null) return "无零件";
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule m = part.Modules[i];
                if (m == null) continue;
                string n = m.moduleName ?? "";
                if (n.IndexOf("RSE_Engines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    Type t = m.GetType();
                    bool init = false;
                    FieldInfo fi = t.GetField("Initialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi != null) init = (bool)fi.GetValue(m);
                    bool hasParent = false;
                    PropertyInfo pp = t.GetProperty("AudioParent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pp != null) hasParent = pp.GetValue(m, null) != null;
                    if (init && hasParent) return "在放声";
                    if (init) return "已初始化但没有音源";
                    // 未初始化时，唯一能判的就是"组解析出来没有"：OnStart 取不到组会抛异常死掉，
                    // 而组是 OnLoad 建的，所以这里读得到组 = cfg 对，剩下只是 prefab 不跑 Start。
                    string groups = RseGroups(part);
                    return "未启动（" + groups + "）";
                }
                catch (Exception e) { return "读状态失败：" + e.Message; }
            }
            return "本零件没有 RSE_Engines";
        }

        /// <summary>
        /// RSE 在这个零件上解析出了哪些声音组。
        ///
        /// 光看 Initialize 不够：prefab 上 `Start()` 不会跑，`Initialized` 永远是 false。
        /// 但 `OnLoad` 是会跑的，所以 `SoundLayerGroups` 里有没有 `JetForge` 这个键，
        /// 能直接回答"我们的 cfg 写对了没有"——`RSE_Engines.OnStart` 就是拿 engineID 去这里取组。
        /// </summary>
        public static string RseGroups(Part part)
        {
            if (part == null || part.Modules == null) return "无零件";
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule m = part.Modules[i];
                if (m == null) continue;
                string n = m.moduleName ?? "";
                if (n.IndexOf("RSE_Engines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    FieldInfo fi = m.GetType().GetField("SoundLayerGroups", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi == null) return "找不到 SoundLayerGroups 字段";
                    var dict = fi.GetValue(m) as System.Collections.IDictionary;
                    if (dict == null) return "SoundLayerGroups 是空引用";
                    var keys = new StringBuilder();
                    foreach (object k in dict.Keys)
                    {
                        if (keys.Length > 0) keys.Append('/');
                        keys.Append(k as string ?? "?");
                    }
                    return string.Format("{0} 个组[{1}]", dict.Count, keys.Length > 0 ? keys.ToString() : "空");
                }
                catch (Exception e) { return "读组失败：" + e.Message; }
            }
            return "本零件没有 RSE_Engines";
        }

        /// <summary>RSE 真的在放声吗——只有这时才值得让我们让一点音量。</summary>
        public static bool RseActive(Part part)
        {
            return RseState(part) == "在放声";
        }

        /// <summary>
        /// 直接复刻 `RSE_Engines.OnStart` 那一步查表：拿本零件每个 `ModuleEngines` 的
        /// `engineID` 去 `SoundLayerGroups` 里取组。
        ///
        /// 这一步能测，而"在放声"不能测——因为 `OnLoad` 会跑、`Start()` 不会，
        /// 所以 `SoundLayerGroups` 在 prefab 上就是齐的。只要键全部命中，
        /// `OnStart` 里那句 `get_Item` 就不会抛 `KeyNotFoundException`，
        /// 模块不会死在启动路径上。这是"RSE 会不会接管我们"能在主菜单断言的极限。
        /// </summary>
        public static string RseGroupMatch(Part part)
        {
            if (part == null || part.Modules == null) return "无零件";
            object rse = null;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule m = part.Modules[i];
                if (m == null) continue;
                if ((m.moduleName ?? "").IndexOf("RSE_Engines", StringComparison.OrdinalIgnoreCase) >= 0) { rse = m; break; }
            }
            if (rse == null) return "本零件没有 RSE_Engines";
            try
            {
                FieldInfo fi = rse.GetType().GetField("SoundLayerGroups", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var dict = fi == null ? null : fi.GetValue(rse) as System.Collections.IDictionary;
                if (dict == null) return "SoundLayerGroups 读不到";
                var sb = new StringBuilder();
                bool allHit = true;
                int engines = 0;
                for (int i = 0; i < part.Modules.Count; i++)
                {
                    ModuleEngines eng = part.Modules[i] as ModuleEngines;
                    if (eng == null) continue;
                    engines++;
                    string id = eng.engineID ?? "";
                    bool hit = dict.Contains(id);
                    if (!hit) allHit = false;
                    if (sb.Length > 0) sb.Append('｜');
                    sb.AppendFormat("engineID={0} → {1}", id.Length > 0 ? id : "(空)", hit ? "命中" : "缺组");
                }
                if (engines == 0) return "本零件没有 ModuleEngines";
                return sb.ToString() + "｜OnStart 不会抛 = " + (allHit ? "对" : "不对");
            }
            catch (Exception e) { return "查组失败：" + e.Message; }
        }

        private static object FindRseModule(Part part)
        {
            if (part == null || part.Modules == null) return null;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule m = part.Modules[i];
                if (m == null) continue;
                if ((m.moduleName ?? "").IndexOf("RSE_Engines", StringComparison.OrdinalIgnoreCase) >= 0) return m;
            }
            return null;
        }

        private static object MemberOf(object o, string name)
        {
            if (o == null) return null;
            try
            {
                Type t = o.GetType();
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(o);
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return p != null ? p.GetValue(o, null) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 把嗓音灌进 RSE 自己的音层曲线里。
        ///
        /// 为什么是"改曲线"而不是"改 AudioSource"：RSE 每帧都用 `pitchFC.Evaluate(转速)`
        /// 覆盖 AudioSource.pitch，我们直接写会被下一帧冲掉；改曲线是改它每帧要读的那张表，
        /// 写一次管到底，而且改动经过 RSE 的多普勒/空气吸收/梳状滤波——玩家要的就是这个。
        ///
        /// 必须克隆曲线：`SoundLayer` 可能是 `sharedSoundLayers` 里的共享实例，
        /// 原地改会连累别的零件（同一型号另一台、甚至原版零件）。
        /// </summary>
        public void PushVoiceToRse()
        {
            if (_rseVoiced || _part == null) return;
            object rse = FindRseModule(_part);
            if (rse == null) return;
            if (MemberOf(rse, "Initialized") is bool && !(bool)MemberOf(rse, "Initialized")) return;
            var layers = MemberOf(rse, "SoundLayers") as System.Collections.IList;
            if (layers == null || layers.Count == 0) return;

            int touched = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                object L = layers[i];
                if (L == null) continue;
                string nm = MemberOf(L, "name") as string ?? "";
                float pSc, vSc;
                RseScaleOf(nm, out pSc, out vSc);

                // 没有加力燃烧室的设计：RSE 组里的加力层必须**真的按到零**，
                // 否则一台纯涡喷开满干推力（这时推力比到 1.0）就会听见加力轰鸣——
                // 玩家上一轮报的"无加力的发动机还有奇怪的声音"就是这个。
                bool abLayer = IsAbLayer(nm);
                bool force = false;
                if (abLayer && !_hasAb) { vSc = 0f; pSc = 1f; force = true; }

                bool a = ScaleCurveField(L, "pitchFC", pSc, force);
                bool b = ScaleCurveField(L, "volumeFC", vSc, force);
                if (a || b) touched++;
            }
            _rseLayersTouched = touched;
            _rseVoiced = touched > 0;
        }

        /// <summary>我们在 RSE 组里给加力叫这两个名字（见 GameData/JetForge/RSE.cfg）。</summary>
        public static bool IsAbLayer(string name)
        {
            return name != null && name.StartsWith("AB_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 自检/排查用：RSE 组里那两条加力层现在是什么样。
        ///
        /// 两条路都要能答：
        ///   ① 运行时 —— `SoundLayers` 已经建好（RSE 的 `Start()` 跑过），直接读曲线峰值，
        ///      用它可以验"没有加力燃烧室的设计真的被按到零"。
        ///   ② 配置层 —— 主菜单里 RSE 还没醒，`SoundLayers` 是空表（跟 Waterfall 的
        ///      `Controllers` 一个毛病），这时读 `partConfig` 里的 cfg，验"我们真把层写进去了"。
        ///
        /// 只报"找到了几条"是不够的：上一版就是"找到了但没压"，
        /// 一台纯涡喷满油门也会有加力轰鸣。
        /// </summary>
        public static string RseAbReport(Part part, EngineDesignData d)
        {
            if (part == null || d == null) return "没有零件";
            bool hasAb = d.Afterburner != null && d.Afterburner.Enabled;
            string cfgTxt;
            try { cfgTxt = AbLayersFromConfig(part); }
            catch (Exception e) { cfgTxt = "读 cfg 失败：" + e.Message; }

            try
            {
                JetForgeSound probe = new JetForgeSound();
                probe._part = part;
                probe._voice = VoiceOf(d);
                probe._hasAb = hasAb;
                probe._rseVoiced = false;
                probe.PushVoiceToRse();

                object rse = FindRseModule(part);
                var layers = rse != null ? MemberOf(rse, "SoundLayers") as System.Collections.IList : null;
                if (layers == null || layers.Count == 0)
                    return string.Format("运行时还没建（RSE_Engines 未启动）｜cfg 里：{0}", cfgTxt);

                int n = 0;
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < layers.Count; i++)
                {
                    object L = layers[i];
                    if (L == null) continue;
                    string nm = MemberOf(L, "name") as string ?? "";
                    if (!IsAbLayer(nm)) continue;
                    float peak = CurvePeak(L, "volumeFC");
                    n++;
                    if (sb.Length > 0) sb.Append("｜");
                    sb.Append(string.Format("{0} 峰值 {1:F2}", nm, peak));
                }
                string tail = hasAb ? "开着（按推力分数开门）" : "按到零（这台没有加力）";
                return string.Format("运行时 {0} 条 {1}：{2}｜cfg 里：{3}", n, tail, sb.ToString(), cfgTxt);
            }
            catch (Exception e) { return "读运行时加力层失败：" + e.Message + "｜cfg 里：" + cfgTxt; }
        }

        /// <summary>从零件 cfg 里数出 AB_ 开头的音层，并报它们的音量曲线形状（不依赖 RSE 启动）。</summary>
        public static string AbLayersFromConfig(Part part)
        {
            if (part == null || part.partInfo == null || part.partInfo.partConfig == null) return "没有 cfg";
            ConfigNode root = part.partInfo.partConfig;
            int layers = 0;
            StringBuilder sb = new StringBuilder();
            ConfigNode[] mods = root.GetNodes("MODULE");
            for (int i = 0; i < mods.Length; i++)
            {
                string mn = mods[i].GetValue("name") ?? "";
                if (mn != "RSE_Engines") continue;
                ConfigNode[] groups = mods[i].GetNodes("SOUNDLAYERGROUP");
                for (int g = 0; g < groups.Length; g++)
                {
                    ConfigNode[] ls = groups[g].GetNodes("SOUNDLAYER");
                    for (int l = 0; l < ls.Length; l++)
                    {
                        string ln = ls[l].GetValue("name") ?? "";
                        if (!IsAbLayer(ln)) continue;
                        layers++;
                        string clip = ls[l].GetValue("audioClip") ?? "(无)";
                        ConfigNode vol = ls[l].GetNode("volumeFC");
                        string keys = vol != null ? (vol.GetValues("key").Length + " 个键") : "无曲线";
                        if (sb.Length > 0) sb.Append("｜");
                        sb.Append(string.Format("{0}→{1}（{2}）", ln, clip, keys));
                    }
                }
            }
            return layers > 0 ? string.Format("{0} 条：{1}", layers, sb.ToString()) : "一条都没有";
        }

        private static float CurvePeak(object layer, string fieldName)
        {
            try
            {
                FieldInfo fi = layer.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi == null) return -1f;
                var fc = fi.GetValue(layer) as FloatCurve;
                if (fc == null || fc.Curve == null || fc.Curve.keys == null) return -1f;
                float m = 0f;
                Keyframe[] ks = fc.Curve.keys;
                for (int i = 0; i < ks.Length; i++) if (ks[i].value > m) m = ks[i].value;
                return m;
            }
            catch (Exception) { return -1f; }
        }

        private void RseScaleOf(string name, out float pitchScale, out float volScale)
        {
            float whine = 1f, roar = 1f;
            string n = name ?? "";
            if (n.IndexOf("Whine", StringComparison.OrdinalIgnoreCase) >= 0) whine = _voice.Whine;
            else if (n.IndexOf("Resonance", StringComparison.OrdinalIgnoreCase) >= 0) whine = _voice.Whine;
            else roar = _voice.Roar;
            pitchScale = _voice.Pitch;
            volScale = _voice.Gain * (whine != 1f ? whine : roar);
            // 曲线是倍率表，整体放大到离谱就不像"同一台机器"了
            if (pitchScale < 0.65f) pitchScale = 0.65f;
            if (pitchScale > 1.55f) pitchScale = 1.55f;
            if (volScale < 0.35f) volScale = 0.35f;
            if (volScale > 1.90f) volScale = 1.90f;
        }

        private static bool ScaleCurveField(object layer, string fieldName, float scale)
        {
            return ScaleCurveField(layer, fieldName, scale, false);
        }

        /// <summary>
        /// 按倍率改写一条 `FloatCurve`。
        /// `force = true` 时倍率 1.0 也照写——把加力层的音量按到 0 的时候，
        /// 那个 0 必须真的落到曲线上，"没改动"和"改成零"是两件事。
        /// </summary>
        private static bool ScaleCurveField(object layer, string fieldName, float scale, bool force)
        {
            if (!force && Mathf.Abs(scale - 1f) < 0.01f) return false;
            FieldInfo fi = layer.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi == null) return false;
            var fc = fi.GetValue(layer) as FloatCurve;
            if (fc == null) return false;
            AnimationCurve src = fc.Curve;
            if (src == null || src.keys == null || src.keys.Length == 0) return false;
            FloatCurve nf = new FloatCurve();
            Keyframe[] ks = src.keys;
            for (int i = 0; i < ks.Length; i++)
                nf.Add(ks[i].time, Mathf.Clamp01(ks[i].value * scale), ks[i].inTangent * scale, ks[i].outTangent * scale);
            fi.SetValue(layer, nf);
            return true;
        }

        public void Attach(Part part, EngineDesignData d)
        {
            Detach();
            _gain = Mathf.Clamp((float)d.SoundGain, 0f, 2f);
            float rNoz = 0.35f;
            try { rNoz = JetForgeMeshBuilder.NozzleExitRadius(d); }
            // 这一层算不出来声音就会按缺省尺寸播——玩家没法知道为什么声音不对，所以要说一句
            catch (Exception e) { WarnSpecOnce("喷管出口半径", e); }
            _size = Mathf.Clamp(rNoz / 0.35f, 0.6f, 1.6f);
            _highBypass = d.Kind == EngineKind.Turbofan && d.BypassRatio() > 1.6;
            _hasAb = d.Afterburner != null && d.Afterburner.Enabled;

            for (int i = 0; i < Specs.Length; i++)
            {
                Spec s = Specs[i];
                if (!ClipExists(s.Clip)) continue;
                AudioClip ac = null;
                try { ac = GameDatabase.Instance.GetAudioClip(s.Clip); }
                // 探测豁免：音频片段取不到就跳过这一层声音
                catch (Exception) { }
                if (ac == null) continue;

                GameObject go = new GameObject("JetForgeSound_" + s.Name);
                if (_part != null) go.transform.parent = _part.transform;
                go.transform.localPosition = Vector3.zero;
                AudioSource src = go.AddComponent<AudioSource>();
                src.clip = ac;
                src.loop = true;
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.minDistance = 3f * _size;
                src.maxDistance = 600f;
                src.dopplerLevel = 0.3f;
                src.volume = 0f;
                // 空气对高频的吸收：高度越高、密度越低，听到的越闷
                AudioLowPassFilter lp = go.AddComponent<AudioLowPassFilter>();
                lp.cutoffFrequency = 20000f;
                lp.lowpassResonanceQ = 0.9f;

                _layers.Add(new Layer { S = s, Src = src, X = 0f });
                src.Play();
            }

            _attached = _layers.Count > 0;
            _voice = VoiceOf(d);
            _rseVoiced = false;
            _rseLayersTouched = 0;
            // 分工改过一次，现在是**全让位**：RSE 真在放声时我们九层全部静音（干态、加力都让），
            // 加力那两层搬进了 RSE 组（RSE.cfg 里的 AB_Roar / AB_Screech）。
            //
            // 上一版留着加力层自己播，理由是"原版声音组里没有加力层"。玩家给的判词更准：
            // 那就是"没经过 RSE 处理的声音"——不过它的空气吸收/多普勒，也不跟着转速走，
            // 只会按加力比例在两种响度之间跳。所以现在宁可绕道 RSE，也不自己播。
            // 我们这套退化成**RSE 缺装时的后备**。
            // RSE 只是"模块在"却没初始化（取不到声音组）时**不让位**——
            // 上一版无条件让位，结果装了 RSE 反而一点声都没有。
            bool rseOn = RseActive(part);
            _wasRseOn = rseOn;
            _duckDry = rseOn ? RseDryDuck : 1f;
            _duckAb = rseOn ? RseDryDuck : 1f;
            PushVoiceToRse();
            _status = _attached
                ? string.Format("分层 {0}/{1} 条｜音量 {2:F2}｜尺寸 {3:F2}｜RSE {4}｜干态让位 {5:P0}｜RSE 音层改 {6} 条",
                    _layers.Count, Specs.Length, _gain, _size, RseState(part), 1f - _duckDry, _rseLayersTouched)
                : "素材全缺（GameDatabase 里找不到 JetForge/Sounds/*）";
        }

        /// <summary>曲线取值：分段线性，超出两端就取端点。</summary>
        public static float Eval(float[] xs, float[] ys, float x)
        {
            if (xs == null || xs.Length == 0 || ys == null || ys.Length == 0) return 0f;
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
            {
                if (x <= xs[i])
                {
                    float span = xs[i] - xs[i - 1];
                    float t = span > 1e-6f ? (x - xs[i - 1]) / span : 1f;
                    return ys[i - 1] + (ys[i] - ys[i - 1]) * t;
                }
            }
            return ys[ys.Length - 1];
        }

        /// <summary>某条音层在给定横轴上的音量与音高（含加力层的特殊处理）。</summary>
        public static void LayerAt(int index, float x, float ab, double spoolFrac,
                                   out float volume, out float pitch)
        {
            volume = 0f; pitch = 1f;
            if (index < 0 || index >= Specs.Length) return;
            Spec s = Specs[index];
            if (s.OnlyAfterburner)
            {
                volume = Eval(s.VolX, s.VolY, ab) * s.Scale * (float)(0.45 + 0.55 * spoolFrac);
                pitch = Mathf.Clamp(Eval(s.PitchX, s.PitchY, ab), 0.4f, 2.6f);
                return;
            }
            volume = Eval(s.VolX, s.VolY, x) * s.Scale;
            pitch = Mathf.Clamp(Eval(s.PitchX, s.PitchY, x), 0.4f, 2.6f);
        }

        public static string DescribePlan(float x, float ab)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("分层表（油门输入 {0:F2}、加力 {1:F2}）——每项是「音量@音高」，音量 0~1、音高 1.0 为原调：", x, ab);
            for (int i = 0; i < Specs.Length; i++)
            {
                float v, p;
                LayerAt(i, x, ab, x, out v, out p);
                sb.AppendFormat(" {0}={1:F2}@{2:F2}", Specs[i].Name, v, p);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 转速权重（纯函数，自检直接验）：0 转几乎没声、满转 1.0。
        /// 喷流噪声声功率约按速度 8 次方走，换算到音量用 2.4 次方已经算保守
        /// （再陡就会"慢车像根本没开车"，玩家分不清是关车还是在慢车）。
        /// </summary>
        public static float SpoolWeight(double spoolFrac)
        {
            float sp = Mathf.Clamp((float)spoolFrac, 0f, 1f);
            return 0.06f + 0.94f * Mathf.Pow(sp, 2.4f);
        }

        /// <summary>音层名（自检按名字找，避免依赖数组下标）。</summary>
        public static int IndexOf(string name)
        {
            for (int i = 0; i < Specs.Length; i++) if (Specs[i].Name == name) return i;
            return -1;
        }

        public static string ClipOf(int index)
        {
            if (index < 0 || index >= Specs.Length) return null;
            return Specs[index].Clip;
        }

        /// <summary>
        /// 每帧更新。spool = 换算转速 0~1，abFrac = 加力比例 0~1，running = 火在不在烧。
        /// <paramref name="throttle"/> 保留只是为了不改调用点：**声音不再跟油门走**，
        /// 它跟的是转子（spool）、加力（abFrac）与当地空气密度。
        /// </summary>
        public void Update(double throttle, double spool, double abFrac, bool running, double altitude)
        {
            if (!_attached) return;
            float dt = Time.deltaTime;
            float sp = Mathf.Clamp01((float)spool);
            float ab = Mathf.Clamp01((float)abFrac);
            // throttle 还在签名里只是为了让调用点不用改；这一层**一格都不许读它**。
            // 读过油门的那些曲线，推杆瞬间就到满音量，而转子还在后面慢慢爬——
            // 玩家听到的是"声音先飞、发动机后追"。声音该由转子、加力、气流决定。
            // 火灭了不等于没声音：转子还在带着劲往下溜，风车声、气流声都在，
            // 让横轴跟着转速自己掉，掉到风车才停。
            if (!running) ab = 0f;

            // 响度跟转速走，而且**不是线性的**：真机慢车（N2 60%）比满油门轻得多，
            // 不是"小 40%"，是听感上小一大截。喷流噪声的声功率约按速度 8 次方走，
            // 换算到音量上用 2.4 次方已经算保守（再猛就会"慢车像没开车"）。
            // 地面静止时的气动噪声本来就小，但没有它整台机器像是"贴着耳朵"。
            float staticAir = SpoolWeight(sp);

            // 空气吸收：海平面几乎不切，10 km 砍掉一半高频，20 km 只剩窄带
            float dens = 1.225f;
            try
            {
                // 用零件所在处的静压/静温自己算密度：FlightGlobals 只有
                // getAtmDensity(pressure, temperature, body) 这一个入口，没有无参版本。
                Vessel v = _part != null ? _part.vessel : null;
                if (v != null && v.mainBody != null)
                    dens = (float)FlightGlobals.getAtmDensity(v.staticPressurekPa * 1000.0,
                        v.externalTemperature + 273.15, v.mainBody);
            }
            // 探测豁免：大气密度取不到时用海平面值，只影响音量不影响力
            catch (Exception) { }
            dens = Mathf.Clamp(dens / 1.225f, 0.02f, 1.2f);
            float cut = Mathf.Clamp(700f + 19000f * Mathf.Sqrt(dens), 700f, 20000f);
            _lowpassCut = Mathf.MoveTowards(_lowpassCut, cut, dt * 6000f);

            // 叶尖马赫 = 设计点限制 × 换算转速。0.95 以上叶尖开始拖激波，
            // 那声音不是"大一点"，是宽带撕裂声（所以下面同时把音高往下压 12%）。
            float tipMach = _voice.TipMach * sp;
            float shock = ShockGain(tipMach, _voice.TipMach);
            float shockN = Mathf.Clamp01((shock - 1f) / 1.9f);
            // RSE 的音层是 Start() 之后才建齐的，头几帧拿不到，得一直重试到灌进去
            // 让位要每帧复核：Attach 发生在 EnsureReady，那时 RSE_Engines 多半还没 Start，
            // `RseActive` 为假 → 一次性判定的结果是"我们满音量"，之后 RSE 醒了也不会改，
            // 两套声音就一直叠着响。玩家听到的"没有层次"就是叠出来的。
            if (_part != null)
            {
                bool rseNow = RseActive(_part);
                if (rseNow != _wasRseOn)
                {
                    _wasRseOn = rseNow;
                    _duckDry = rseNow ? RseDryDuck : 1f;
                    _duckAb = rseNow ? RseDryDuck : 1f;
                    if (rseNow) _rseVoiced = false;   // RSE 刚醒，曲线还得再灌一遍
                }
            }
            if (!_rseVoiced) PushVoiceToRse();

            for (int i = 0; i < _layers.Count; i++)
            {
                Layer L = _layers[i];
                if (L.Src == null) continue;
                Spec s = L.S;
                float duck = s.OnlyAfterburner ? _duckAb : _duckDry;

                bool dark = (s.OnlyHighBypass && !_highBypass) || (s.OnlyAfterburner && !(running && ab > 0.01f));
                // 风扇层：涡喷上这一层必须是死的。它以前只按"是不是高涵道"开关，
                // 而涡喷在 `_highBypass` 判定里本来就不该举旗——真正的漏子是下面的
                // 窄带层在涡喷上封顶，听着像风扇。两层一起收，才是"涡喷没有风扇声"。
                if (s.Name == "fan" && _voice.Fan <= 0.02f) dark = true;
                float target = 0f;
                if (!dark)
                {
                    // 每层横轴带独立滞后：RSE 的 spoolSpeed 就是干这个的，收放油门时各层错开动
                    // 横轴只认运行状态，**绝不认油门**。
                    //
                    // 以前 air/air2/fan 三层是拿油门当横轴的：推油门那一帧，声音立刻到满，
                    // 而转子还要几秒才跟上——听着就是"声音在前面飞、发动机在后面追"。
                    // 真机的声音是气流与转子发出来的：油门只是"下命令"，转子才是"执行"。
                    // 所以宽带层改用喷流状态（转速推出，开加力时略高），风扇层用转速原值。
                    float jetX = Mathf.Clamp01(sp * (1f + 0.10f * ab));
                    float inX = s.UseSpool ? sp : jetX;
                    float tau = Mathf.Clamp(0.35f / Mathf.Max(s.Lag, 0.05f), 0.15f, 6f);
                    float k = 1f - Mathf.Exp(-dt / tau);
                    L.X += (inX - L.X) * k;
                    float v, p;
                    LayerAt(i, L.X, ab, sp, out v, out p);
                    L.Src.pitch = Mathf.Clamp(p * _voice.Pitch * (1f - 0.12f * shockN), 0.35f, 2.8f);
                    float sh = 1f + (shock - 1f) * ShockShare(s.Name);
                    target = v * _gain * _size * _voice.Gain * RoleGain(s.Name, _voice) * sh
                             * 0.42f * duck * staticAir;
                    // 风扇层的响度跟着"风扇直径 × 涵道比"连续变，不再是开/关两态：
                    // 2.6 m 的大涵道和 1.2 m 的小涡扇，风扇声本来就差一截。
                    if (s.Name == "fan") target *= Mathf.Clamp(_voice.Fan, 0f, 1.3f);
                }
                else if (s.OnlyAfterburner)
                {
                    float v, p;
                    LayerAt(i, L.X, ab, sp, out v, out p);
                    L.Src.pitch = Mathf.Clamp(p * _voice.Pitch * (1f - 0.12f * shockN), 0.35f, 2.8f);
                }

                L.Src.volume = Mathf.MoveTowards(L.Src.volume, Mathf.Clamp01(target), dt * 2.2f);
                AudioLowPassFilter lp = L.Src.GetComponent<AudioLowPassFilter>();
                if (lp != null) lp.cutoffFrequency = _lowpassCut;
                if (L.Src.volume <= 0.0005f && L.Src.isPlaying) L.Src.Pause();
                else if (L.Src.volume > 0.0005f && !L.Src.isPlaying) L.Src.UnPause();
            }
            _status = Describe(sp, ab);
            _tipMach = tipMach;
            _shock = shock;
        }

        private float _tipMach = 1f;
        private float _shock = 1f;

        private string Describe(float sp, float ab)
        {
            int live = 0;
            float loud = 0f;
            for (int i = 0; i < _layers.Count; i++)
            {
                if (_layers[i].Src == null) continue;
                if (_layers[i].Src.volume > 0.01f) live++;
                if (_layers[i].Src.volume > loud) loud = _layers[i].Src.volume;
            }
            // 这里以前印"油门"：声音已经不吃油门了，再印它只会让人以为它还在吃。
            return string.Format("分层 {0}/{1} 条在响｜最大 {2:F2}｜转速 {3:F2} 加力 {4:F2}｜叶尖 M{5:F2} 轰鸣 {6:F2}×｜干态 {7:P0}",
                live, _layers.Count, loud, sp, ab, _tipMach, _shock, _duckDry);
        }

        public void Detach()
        {
            for (int i = 0; i < _layers.Count; i++)
            {
                Layer L = _layers[i];
                if (L.Src == null) continue;
                try
                {
                    L.Src.Stop();
                    UnityEngine.Object.Destroy(L.Src.gameObject);
                }
                // 探测豁免：销毁音源失败时这些对象本来就在拆除中
                catch (Exception) { }
                L.Src = null;
            }
            _layers.Clear();
            _attached = false;
            _status = "未挂";
        }

        private static float[] Keys(params float[] kv) { return kv; }
        private static float[] TakeX(float[] kv)
        {
            if (kv == null || kv.Length < 2) return new float[0];
            float[] x = new float[kv.Length / 2];
            for (int i = 0; i < x.Length; i++) x[i] = kv[i * 2];
            return x;
        }
        private static float[] TakeY(float[] kv)
        {
            if (kv == null || kv.Length < 2) return new float[0];
            float[] y = new float[kv.Length / 2];
            for (int i = 0; i < y.Length; i++) y[i] = kv[i * 2 + 1];
            return y;
        }
    }
}