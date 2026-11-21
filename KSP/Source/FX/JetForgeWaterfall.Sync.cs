using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using JetForge.Core;

namespace JetForge.FX
{

    /// <summary>
    /// partial：把九套羽流模块挂到这台机器上——按构型选哪几个、按喷口半径改尺寸、
    /// 按换算转速与加力比例给强度。这里全是"每帧都要跑但不该每帧重做"的东西，
    /// 重活在字段里缓存。
    /// </summary>
    public sealed partial class JetForgeWaterfall
    {
        /// <summary>
        /// 按这台机器的设计同步（构型选择 / 尺寸）。每帧调用都行，重活有缓存。
        /// <paramref name="wantWet"/> 只表示"这台的构型允许加力"，真正的开关在
        /// `_allowWet`（取自设计里的 `Afterburner.Enabled`）——两者都成立才开湿模块。
        /// </summary>
        public void Sync(Part part, EngineDesignData d, bool wantWet)
        {
            if (part == null || d == null) return;
            // 记下零件：`SyncScale` 要顺着它去问发动机模块的换算转速。这里每帧都会走，
            // 赋值一次 string 级的开销，比在 SetLevel 里加一个可能为空的参数干净。
            _part = part;
            if (!Available()) { Note = "未装 Waterfall，用自绘尾焰"; return; }

            if (!_routed)
            {
                int found = 0;
                for (int i = 0; i < ClassCount; i++)
                {
                    _mods[i] = FindModule(part, ModuleIds[i]);
                    if (_mods[i] != null) found++;
                }
                if (found == 0)
                {
                    Note = "cfg 里没有 ModuleWaterfallFX（补丁没生效或 Waterfall 版本不认 TEMPLATE？）";
                    return;
                }
                Attached = true;
                _routed = true;
                _scaleApplied = false;
                FoundModules = found;
                _part = part;
            }

            DryClass = Classify(d, false);
            WetClass = Classify(d, true);
            _isTurbojet = d.Kind == EngineKind.Turbojet;
            _design = d;
            _hasAb = d.Afterburner != null && d.Afterburner.Enabled;

            // 这台机器压根没有加力：湿模块一个都不许开。
            //
            // 玩家报的"没配备加力燃烧室的发动机也产生了长长的尾焰"就是这个——
            // `EnableOnly` 原先无条件把选中的湿模块打开，而它的 throttle 控制器
            // 只要读到 KSP 油门就会被点亮（我们灌 0 才有用，灌不进去时满油门就是全亮）。
            // 所以这里先看设计，没加力就把整组湿模块按死。
            bool allowWet = wantWet && d.Afterburner != null && d.Afterburner.Enabled;
            if (allowWet != _allowWet)
            {
                _allowWet = allowWet;
                _wetOn = !allowWet;           // 强制下一帧走一次开关
                _wetLevel = -1.0;
                _wetPushed = false;
            }

            string sig;
            try { sig = DesignSignature.Of(d); } catch { sig = ""; }
            if (!_scaleApplied || sig != _sig)
            {
                ApplyScale(d);
                EnableOnly(DryClass, WetClass);
                _sig = sig;
                _wetLevel = -1.0;
                _dryLevel = -1.0;
                // 换了构型 = 换了模块，之前的"已灌进去"作废
                _wetPushed = false;
                _dryPushed = false;
                _throttleCtrl[(int)WetClass] = null;
                _throttleCtrl[(int)DryClass] = null;
            }
        }

        private void ApplyScale(EngineDesignData d)
        {
            try
            {
                // 干态与湿态两套直径：干态大涵道机最外圈是风扇气流（外涵出口），
                // 加力火光只从核心喷口/加力段出口出来。以前九个模块共用同一个缩放，
                // 于是"加力尾焰比核心机粗一大圈"——玩家一眼就看出来不对。
                //
                // 可变喷管还要再压一道：花瓣收起来时实际出口比唇口小（0.88 倍），
                // 羽流按唇口算就会从叶片缝里钻出去——那是"穿模"的另一半原因。
                double rDry = JetForgeMeshBuilder.PlumeRadiusLimited(d, false, _openFrac);
                double rWet = JetForgeMeshBuilder.PlumeRadiusLimited(d, true, _openFrac);
                if (rDry <= 0.001) rDry = rWet;
                if (rWet <= 0.001) rWet = rDry;
                if (rWet <= 0.001) return;
                ScaleR = PlumeScale(rDry);
                ScaleRWet = PlumeScale(rWet);
                if (ScaleR <= 0.0) return;

                // 热浪按**核心喷口**出口半径定，与外涵风扇直径无关。
                // 归一除的必须是"这一档模板自己 authored 的半径"，否则干态会胖 17%。
                double rCoreExit = JetForgeMeshBuilder.NozzleExitRadius(d);
                double hazeR = rCoreExit * HazeRadiusFactor;
                HazeRadius = hazeR;
                double hazeScale = hazeR > 0.001 ? hazeR / 0.6 : ScaleR;
                HazeScale = hazeScale;
                // 三轴一致。原版也是三轴一致的——单独拉长某一轴会把圆柱拉成一块板。
                bool ok = false;
                for (int i = 0; i < ClassCount; i++)
                {
                    if (_mods[i] == null) continue;
                    double s = IsWetClass((PlumeClass)i) ? ScaleRWet : ScaleR;
                    double authored = IsWetClass((PlumeClass)i) ? WetHazeAuthoredR : DryHazeAuthoredR;
                    double hz = hazeR > 0.001 ? hazeR / authored : s;
                    ok |= ApplyOffsets(_mods[i], new Vector3((float)s, (float)s, (float)s),
                                       new Vector3((float)hz, (float)hz, (float)hz), false);
                }
                // FX 还没初始化时写不进去，下一帧再来（Waterfall 在 Start 里才建效果）
                _scaleApplied = ok;
                _scaleTick = Time.time;
                if (ok) _scaleOpen = _openFrac;
                Note = string.Format("干喷口 {0:F2} m / 加力出口 {1:F2} m｜模板缩放 {2:F2} / {3:F2}"
                    + "｜热浪半径 {4:F2} m（按核心喷口×{5:F2}，干/湿各按自己模板的 {6:F2}/{7:F2} 归一）",
                    rDry, rWet, ScaleR, ScaleRWet, hazeR, HazeRadiusFactor,
                    DryHazeAuthoredR, WetHazeAuthoredR);
            }
            catch (Exception e) { Note = "缩放失败：" + e.Message; }
        }

        /// <summary>花瓣开度变了就重新算一次缩放（收起来时羽流要跟着变细）。</summary>
        /// <remarks>
        /// 换算转速是模块内的状态，羽流自己算不出来，得顺着零件去问发动机模块。
        /// 零件引用在 `Sync` 里记下来（`_part`），不从这里传参：`SetLevel` 是外部
        /// 调用方每帧都敲的公开口子，为这一个数改它的签名不值。
        /// </remarks>
        private void SyncScale(EngineDesignData d, double throttle, double abFrac)
        {
            if (d == null) return;
            if (d.Nozzle == NozzleKind.Converging) return;
            // 开度必须与花瓣网格同一个来源。以前这里用的是 NozzleOpenFrac（0.30+0.45·油门+0.35·加力
            // 的退役近似），网格走的是 NozzleVisual.OpenFor —— 两套数不同步时，
            // 喷管张开了羽流还卡在旧直径，玩家报的"穿模"就是这个。
            //
            // 传的也必须是换算转速：干态开度吃的是 `DryFactor(N/√θ)`，拿油门喂进去
            // 在高空会各说各话（求解器限换算转速、羽流按油门算）。这里去问同一件
            // 零件上的引擎模块要那个数；要不到才退回油门——那是"只能这么办"，不是等价。
            double nc = throttle;
            KSP.ModuleJetForgeEngine eng = _engine;
            if (eng == null && _part != null)
            {
                eng = _part.FindModuleImplementing<KSP.ModuleJetForgeEngine>();
                _engine = eng;
            }
            if (eng != null) nc = eng.ConvertedSpeedNow;
            float open = (float)JetForge.Core.NozzleVisual.OpenFor(
                JetForgeMeshBuilder.NozzleExitRadius(d), nc, abFrac,
                d.Nozzle == NozzleKind.ConvergingDiverging,
                d.Nozzle != NozzleKind.Converging, d.PetalLengthFrac);
            // 开度没变也要周期性重写一次缩放：Waterfall 会在自己的 Update 里重新套模板
            // 偏移，我们写进去的值会被按原值刷回去——那正是"热浪直径还是风扇那么大"。
            bool stale = Time.time - _scaleTick > 1.0f;
            if (Math.Abs(open - _scaleOpen) < 0.02 && !stale && _scaleApplied) return;
            _openFrac = open;
            ApplyScale(d);
        }

        /// <summary>湿态那六类（WetJetWarm 起）——它们的直径要跟核心机/加力段出口。</summary>
        public static bool IsWetClass(PlumeClass c)
        {
            return c >= PlumeClass.WetJetWarm;
        }

        /// <summary>
        /// 只留选中的干/湿两个模块，其余七个关掉——九个模块一起渲染是白烧帧。
        ///
        /// 这里**只记"想要什么"，不保证已经办到**：`ModuleWaterfallFX.FX` 那串效果是
        /// 它自己 `Start()` 里建的，我们在 `Sync` 的帧上拿到的是空表，`SetEnabled` 一个
        /// 都没调到（也不报错）。上一版就死在这儿：玩家看到的是**九个模板一起亮**——
        /// 所以"没加力的机器也有尾焰"，而且"颜色都差不多"（混在一起了）。
        /// 真正的开关交给 `EnsureEnabled()`，每帧重试，直到那个模块的 FX 表不为空。
        /// </summary>
        private void EnableOnly(PlumeClass dry, PlumeClass wet)
        {
            for (int i = 0; i < ClassCount; i++)
            {
                _wantOn[i] = (PlumeClass)i == dry || ((PlumeClass)i == wet && _allowWet);
                _known[i] = false;          // 换了构型：旧的"已确认"作废，重新落一遍
                _darkPushed[i] = false;
            }
        }

        /// <summary>
        /// 把"想要开/关"落到模块上。办到了才记账，办不到下一帧再来。
        /// 返回是否这一帧还有没落地的。
        /// </summary>
        private bool EnsureEnabled()
        {
            bool pending = false;
            for (int i = 0; i < ClassCount; i++)
            {
                PartModule mod = _mods[i];
                if (mod == null) continue;
                if (_known[i] && _isOn[i] == _wantOn[i]) continue;
                if (ApplyEnabled(mod, _wantOn[i]))
                {
                    _known[i] = true;
                    _isOn[i] = _wantOn[i];
                    _darkPushed[i] = false;
                }
                else pending = true;
            }
            return pending;
        }

        /// <summary>
        /// 把"当前该有多强的羽流"灌进控制器。干态给油门、湿态给加力比例。
        ///
        /// 这两个数之所以必须由我们说了算：原版模板是拿 KSP 油门当曲线的横轴，
        /// 而我们这边"加力开没开"跟油门不是一回事（AJE 式是 70% 以上按比例，
        /// 但手动强制开时低油门也是满加力）。override 掉 throttle 控制器之后，
        /// 模板曲线的横轴就变成我们喂的数——加力没点亮时它真的是 0。
        ///
        /// **干态要跟着加力比例退掉**：加力点着以后主喷流被它顶掉，干羽流再按满油门
        /// 亮着就是"底下还挂着一根"。乘 (1 − 0.85·加力比例) 让两者交叉淡出，
        /// 过渡的那 1.3 秒里两套同时在，但不打架。
        /// </summary>
        public void SetLevel(double abFrac, double throttle)
        {
            if (!Attached) return;
            if (abFrac < 0.0) abFrac = 0.0;
            if (abFrac > 1.0) abFrac = 1.0;
            if (throttle < 0.0) throttle = 0.0;
            if (throttle > 1.0) throttle = 1.0;

            double dry = DryLevel(_design, throttle, abFrac);

            // 没有加力燃烧室的机器：干羽流层本来就被关掉了（见 SetPlumeLayers），
            // 但热浪那一层要靠这个数才动得起来。以前这里给 0（因为"没加力就不该有火"），
            // 于是热浪被钉在一个固定强度上——玩家看到的是"关车了还在、也不随油门变"。
            bool abFitted = _design != null && _design.Afterburner != null && _design.Afterburner.Enabled;
            if (!abFitted) dry = HazeLevel(throttle);

            // 再加一道硬闸：加力没点亮（比例 <0.02）就把湿模块整块关掉。
            // 曲线本身在 0 处是不透明的，但模板里还有 `core`/`edgeCore` 这类由 mach
            // 驱动的 EFFECT，它们在油门 0 也能亮一点——玩家看到的就是"没开加力却有火"。
            bool wetOn = _allowWet && abFrac > 0.02;
            _wetOn = wetOn;
            _wantOn[(int)WetClass] = wetOn;
            _wantOn[(int)DryClass] = true;

            // 开关状态每帧确认一次：FX 表是模块 Start() 之后才有的，
            // 早几帧办不到的，这里会一直重试到办到为止。
            bool pendingEnable = EnsureEnabled();
            if (pendingEnable && !_enableLogged)
            {
                _enableLogged = true;
                Debug.Log("[JetForge] 羽流模块开关还没落地（FX 表为空＝还没 Start），继续重试｜" + Diag());
            }

            // 花瓣开度变了就重算缩放：可变喷管收起来时实际出口比唇口小，
            // 羽流按唇口算就会从叶片缝里钻出去。
            SyncScale(_design, throttle, abFrac);

            // 没选中的七个模块：把它们的油门控制器钉在 0。
            // 光靠 SetEnabled 不够——它一旦失效（反射找不到方法、FX 表刚建好还没挂上），
            // 那些模板就会拿 KSP 油门当横轴自己亮起来，九套颜色叠在一起。
            if (Time.time - _darkTick > 0.5f)
            {
                _darkTick = Time.time;
                for (int i = 0; i < ClassCount; i++)
                {
                    if (_wantOn[i] || _mods[i] == null) continue;
                    if (_darkPushed[i]) continue;
                    if (PushLevel(_mods[i], (PlumeClass)i, 0.0)) _darkPushed[i] = true;
                }
            }
            if (!_allowWet)
            {
                // 没加力：湿模块已关，这里再把它的强度钉在 0。
                // 只关模块不钉强度是不够的——模板里还有 core/edgeCore 这类由 mach
                // 驱动的 EFFECT，它们在模块重新被别的路径打开时会自己亮。
                if (_wetLevel != 0.0 || !_wetPushed)
                {
                    _wetLevel = 0.0;
                    _wetPushed = PushLevel(_mods[(int)WetClass], WetClass, 0.0);
                }
            }

            // 死区只配给"写成功了"的那一次。
            //
            // 上一版这里是 `if (值变了) PushLevel(...)`，结果：控制器是 ModuleWaterfallFX
            // 的 Start() 才建的，头几帧 FindController 必然返回 null，PushLevel 直接 return
            // （不抛错、不报警）。而加力比例一旦稳定（比如一直满油门 1.0），"值变了"就永远
            // 不成立 → 我们再没重试过 → override 一次都没灌进去 → 模板照样读 KSP 油门。
            // 玩家看到的就是"加力尾焰跟着油门 0~100% 变"，我改了两版都没动到它。
            if (_allowWet && (Math.Abs(abFrac - _wetLevel) > 0.01 || !_wetPushed))
            {
                _wetLevel = abFrac;
                _wetPushed = PushLevel(_mods[(int)WetClass], WetClass, abFrac);
            }
            if (Math.Abs(dry - _dryLevel) > 0.01 || !_dryPushed)
            {
                _dryLevel = dry;
                _dryPushed = PushLevel(_mods[(int)DryClass], DryClass, dry);
            }

            // 灌进去没有，必须有话说——上一版就是"没灌进去也不吭声"，
            // 玩家只能看见尾焰跟着油门跑。
            int st = (_wetPushed ? 1 : 0) | (_dryPushed ? 2 : 0);
            if (st != _pushState)
            {
                _pushState = st;
                Debug.Log("[JetForge] 羽流灌入 湿=" + (_wetPushed ? "OK" : "未生效")
                          + " 干=" + (_dryPushed ? "OK" : "未生效") + "｜" + Diag());
            }
            if (wetOn && !_wetPushed)
            {
                _noPush += Time.deltaTime;
                if (_noPush > 1.5 && !_noPushWarned)
                {
                    _noPushWarned = true;
                    Debug.LogWarning("[JetForge] 加力羽流 1.5 秒还没灌进控制器｜" + Diag()
                                     + "｜" + ConfControllerNames(_part, ModuleIdOf(WetClass)));
                }
            }
        }

        /// <summary>
        /// 喷流层开关。没有加力的机器只留 `refraction`（热浪），其余喷流层全关。
        ///
        /// 为什么光把强度灌 0 不够：我们的模板是从原版**加力**模板派生的，
        /// 十层里 `shock1~5`/`core`/`edgeCore` 这几层的横轴是马赫而不是油门——
        /// 我们灌的是 throttle 控制器，它们照样按空速自己亮一点。
        /// 玩家看到的就是"没配加力燃烧室的发动机也拖着一条尾焰"。
        ///
        /// 关掉的场合要每帧重申：Waterfall 自己的 Update 会按强度测试重新点亮渲染体，
        /// 我们只在变化时关一次，两帧后它就自己回来了。
        /// </summary>
        public bool SetPlumeLayers(bool show)
        {
            return SetPlumeLayers(show, true);
        }

        /// <summary>
        /// 同上，但热浪单独给一个开关。
        ///
        /// `hazeOn = false` 用于"发动机没在转"或"油门在慢车以下"：热浪是**喷口里
        /// 那股热气的折射**，关车以后喷口温度会往下掉，尾流也断了，不该还有一根
        /// 一直在抖的柱子挂在后面——玩家报的"关闭引擎依然存在"就是这个。
        /// </summary>
        public bool SetPlumeLayers(bool show, bool hazeOn)
        {
            if (!Attached) return false;
            if (show == _plumeShown && hazeOn == _hazeOn) return true;

            bool wrote = ApplyPlumeLayers(_mods[(int)DryClass], show, hazeOn);
            if (_allowWet || !show) wrote |= ApplyPlumeLayers(_mods[(int)WetClass], show, hazeOn);
            if (wrote)
            {
                _plumeShown = show;
                _hazeOn = hazeOn;
            }
            return wrote;
        }

        private static bool ApplyPlumeLayers(PartModule mod, bool show)
        {
            return ApplyPlumeLayers(mod, show, true);
        }

        private static bool ApplyPlumeLayers(PartModule mod, bool show, bool hazeOn)
        {
            IList list = FxList(mod);
            if (list == null || list.Count == 0) return false;
            bool wrote = false;
            for (int i = 0; i < list.Count; i++)
            {
                object fx = list[i];
                if (fx == null) continue;
                try
                {
                    // 热浪那层单独开关：发动机没转、或者油门低于慢车，它就该消失。
                    bool on = show || (FxName(fx) == "refraction" && hazeOn);
                    MethodInfo m = fx.GetType().GetMethod("SetEnabled",
                        BindingFlags.Instance | BindingFlags.Public);
                    if (m != null) { m.Invoke(fx, new object[] { on }); wrote = true; }
                }
                // 探测豁免：第三方反射：没有这个开关就是没有
                catch { }
            }
            return wrote;
        }

        private static string FxName(object fx)
        {
            try
            {
                FieldInfo f = fx.GetType().GetField("name",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f != null ? f.GetValue(fx) as string : null;
            }
            catch { return null; }
        }
    }
}
