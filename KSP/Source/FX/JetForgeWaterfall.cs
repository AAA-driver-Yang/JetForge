using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using JetForge.Core;

namespace JetForge.FX
{
    /// <summary>
    /// 羽流构型。原版 AJE 那套的分组依据就是"实际架构"；我们又多切了一刀：
    /// **同一台机器，加力温度不同，火色不是一个东西**。1900 K 的老涡喷是暖白黄，
    /// 2100 K 的三代机是蓝白甚至发紫——这个差别玩家一眼就能看见，所以按温度分三档。
    /// </summary>
    public enum PlumeClass
    {
        DryLow = 0,     // 低涵道 / 涡喷的干态
        DryHigh = 1,    // 高涵道（民航那类）的干态：羽流近不可见、更淡
        DryOld = 2,     // 早期涡喷干态：偏琥珀灰，带烟
        WetJetWarm = 3, // 涡喷加力，T7 < 1850 K
        WetJetMid = 4,  // 涡喷加力，1850~2000 K
        WetJetHot = 5,  // 涡喷加力，> 2000 K
        WetFanWarm = 6, // 涡扇加力，T7 < 1900 K
        WetFanMid = 7,  // 涡扇加力，1900~2050 K
        WetFanHot = 8,  // 涡扇加力，> 2050 K
    }

    /// <summary>
    /// 把 Waterfall 挂到每台发动机上，尺寸按这台机器的喷口算，强度按加力比例算。
    ///
    /// 为什么不在这里现造模块：`Part.AddModule(ConfigNode, forceAwake)` 在游戏里返回 null
    /// （自检实测"造模块：失败（AddModule 返回空）"）。所以模块写在
    /// `GameData/JetForge/Waterfall.cfg` 的 `@PART[JetForgeEngine]:NEEDS[Waterfall]` 里，
    /// 一次挂四个（两种干态、两种加力）。KSP 会为**每个零件实例**克隆一份模块，
    /// 所以我们依然能给每台机器不同的羽流。
    ///
    /// 两个关键教训写在这里，别再犯：
    /// 1. 原版模板的 tStrength 是**按 KSP 油门**写的，过 0.699 就跳到峰值——直接用就会
    ///    "油门 70% 以下也起火"。我们的模板（WaterfallTemplates.cfg）改成了按"加力比例"
    ///    写曲线，再用 controller override 把比例灌进去。
    /// 2. 原版模板的 tScale 让直径随油门变。我们的模板把它改成恒 1.0——否则调油门时
    ///    整条羽流会变细（玩家报的第二个故障）。
    ///
    /// 依赖关系：不写 csproj 引用、不往 Waterfall 的安装目录里塞文件（用户规矩）。
    /// 没装 Waterfall 时 cfg 补丁被 NEEDS 挡掉，这里安静地退回自绘尾焰。
    /// </summary>
    public sealed partial class JetForgeWaterfall
    {
        /// <summary>九套模板的 moduleID（顺序与 <see cref="PlumeClass"/> 的值一一对应，索引就是枚举值）。</summary>
        private static readonly string[] ModuleIds =
        {
            "jetforge_dry_low",
            "jetforge_dry_high",
            "jetforge_dry_old",
            "jetforge_wet_jet_warm",
            "jetforge_wet_jet_mid",
            "jetforge_wet_jet_hot",
            "jetforge_wet_fan_warm",
            "jetforge_wet_fan_mid",
            "jetforge_wet_fan_hot",
        };

        /// <summary>九套模板名，与 ModuleIds 同序。</summary>
        private static readonly string[] TemplateNames =
        {
            "JetForge-Dry-LowBypass",
            "JetForge-Dry-HighBypass",
            "JetForge-Dry-OldJet",
            "JetForge-Wet-Jet-Warm",
            "JetForge-Wet-Jet-Mid",
            "JetForge-Wet-Jet-Hot",
            "JetForge-Wet-Fan-Warm",
            "JetForge-Wet-Fan-Mid",
            "JetForge-Wet-Fan-Hot",
        };

        /// <summary>加力温度分档：涡喷暖/中分界 1850 K、中/热分界 2000 K。</summary>
        public const double JetWarmCut = 1850.0;
        public const double JetHotCut = 2000.0;
        /// <summary>涡扇比涡喷整体高 50 K——风扇的涵道冷气掺进来，同样推力下尾温略高。</summary>
        public const double FanWarmCut = 1900.0;
        public const double FanHotCut = 2050.0;

        public const int ClassCount = 9;

        /// <summary>高涵道的分界。2.5 以上（CFM56 那类 5~9 的除外）都已经算"风扇占主导"。</summary>
        public const double HighBypassCut = 2.5;

        /// <summary>
        /// 参考喷口半径：原版的模板就是按这个尺寸 authored 的。
        /// 标定来自 StockWaterfallEffects 自己的配置——Panther/Rapier/Whiplash/Goliath
        /// 全是 `scale = 1,1,1`（喷口半径约 0.35 m），Juno 0.33（约 0.11 m），
        /// Ant 0.0595（约 0.02 m）。三点连成一条线：缩放 ≈ 半径 / 0.35，三轴一致。
        /// </summary>
        public const double RefNozzleRadius = 0.35;

        public const double MinScale = 0.12;
        public const double MaxScale = 1.60;

        /// <summary>
        /// 羽流半径相对喷口半径再收一点。
        ///
        /// 玩家报的是"可变喷管直径小了，会被尾焰挡住，并且穿模"——模板按 0.35 m 的机器
        /// authored，等比缩到我们的尺寸后，羽流外缘正好压在喷口唇口上：从侧后方看，
        /// 火焰把喷管那圈花瓣整个包住，还能看到羽流的圆柱面从喷管外壁里穿出来。
        /// 真机的加力火焰根部是从喷管**内**出来的，比出口直径细一圈。
        /// 0.88 是让外缘退到唇口内侧的值；三轴仍然一致（缩半径，不是只缩某一轴）。
        /// 又收到 0.84：玩家说"缩小一点，略微存在穿模"——0.88 还有一点点压住花瓣边缘，
        /// 从正后方看能看到羽流圆柱面从花瓣缝里露出来。0.84 对应出口半径的 84%，
        /// 真机加力火焰根部大约就是这个量级（火焰是被喷口"挤"出来的，不是贴着壁面走）。
        /// </summary>
        public const double PlumeRadiusFactor = 0.84;

        /// <summary>
        /// 热浪外缘相对**核心喷口**出口半径的倍数。真机的热浪是喷口外缘内侧那一圈
        /// 上升的热气，比喷口略细；它跟外涵风扇直径无关。
        /// 实测玩家还在报穿模，从 0.90 收到 0.85。
        /// </summary>
        public const double HazeRadiusFactor = 0.85;

        /// <summary>
        /// 折射柱在模板里 authored 的半径（模板单位）。**干湿两套不一样**：
        /// `JetForge-Wet-*` 是从原版加力模板派生的 0.60，`JetForge-Dry-*` 是 0.70。
        /// 上一版统一按 0.60 归一，干态热浪就胖了 17%；更要命的是玩家报的
        /// "直径等于风扇直径"——那是我们算的缩放**根本没落到折射柱上**，
        /// 它显示的还是模板原值 0.6 单位（≈1.2 m 直径，正好是 JF-1 的风扇直径）。
        /// </summary>
        public const double WetHazeAuthoredR = 0.60;
        public const double DryHazeAuthoredR = 0.70;

        /// <summary>热浪模板缩放（模板里折射柱半径 authored 0.6 单位）。自检要读。</summary>
        public double HazeScale = 1.0;

        /// <summary>热浪要落到多粗（米）。自检拿它跟喷口半径对账。</summary>
        public double HazeRadius = 0.0;

        /// <summary>
        /// 喷口半径 → 模板缩放。
        ///
        /// **上一版这里错得离谱**：按"半径 × 6.5 / × 10"给三个轴，等于把模板里
        /// 25 单位长的加力折射柱拉成 98 m。玩家看到的"巨大光墙"不是贴图问题，
        /// 是站在一根近百米长的加法圆柱里面看它的内壁。
        /// 原版从来不单独拉长某一轴——长度是模板自带的，我们只该按尺寸等比缩。
        /// </summary>
        public static double PlumeScale(double nozzleRadius)
        {
            if (nozzleRadius <= 0.001) return 0.0;
            double s = nozzleRadius * PlumeRadiusFactor / RefNozzleRadius;
            if (s < MinScale) s = MinScale;
            if (s > MaxScale) s = MaxScale;
            return s;
        }

        public static string ModuleIdOf(PlumeClass c)
        {
            int i = (int)c;
            if (i < 0 || i >= ClassCount) return ModuleIds[0];
            return ModuleIds[i];
        }

        public static string TemplateOf(PlumeClass c)
        {
            int i = (int)c;
            if (i < 0 || i >= ClassCount) return TemplateNames[0];
            return TemplateNames[i];
        }

        /// <summary>按"这一刻"的加力出口温度挑湿态模板。
        ///
        /// 设计期那套 `Classify` 只回答"这台机器最热能烧到多少"，它不知道玩家现在烧到多少。
        /// 同一条管子，加力 0.3 和 1.0 的出口温度差好几百 K，颜色和透明度不该一样。
        /// 每帧调用，只有模板真的换了才动模块。
        /// </summary>
        public void SetHotState(double t7)
        {
            if (!Attached || !_allowWet) return;
            if (t7 < 400.0) return;
            PlumeClass want = ClassifyHot(_isTurbojet, t7);
            if (want == WetClass) return;
            WetClass = want;
            EnableOnly(DryClass, want);
            _wetPushed = false;
            _dryPushed = false;
            _wetLevel = -1.0;
            _throttleCtrl[(int)want] = null;
            try { Debug.Log("[JetForge] 羽流换档：" + TemplateOf(want) + "（实际加力出口 " + t7.ToString("F0") + " K）"); }
            // 探测豁免：换档日志打不出来不影响换档本身
            catch (Exception) { }
        }

        /// <summary>给定出口温度和涡喷/涡扇，落到具体那套湿态模板。</summary>
        public static PlumeClass ClassifyHot(bool turbojet, double t7)
        {
            if (turbojet)
            {
                if (t7 < JetWarmCut) return PlumeClass.WetJetWarm;
                if (t7 < JetHotCut) return PlumeClass.WetJetMid;
                return PlumeClass.WetJetHot;
            }
            if (t7 < FanWarmCut) return PlumeClass.WetFanWarm;
            if (t7 < FanHotCut) return PlumeClass.WetFanMid;
            return PlumeClass.WetFanHot;
        }

        /// <summary>设计 → 羽流构型。判据全部来自设计本身，不是玩家勾的：
        /// ① 涡喷还是涡扇（有没有风扇）；② 干态看涵道比（2.5 以上算高涵道）；
        /// ③ 湿态看这台机器自己的加力出口温度 `Afterburner.ExitTemp`。
        /// </summary>
        public static PlumeClass Classify(EngineDesignData d, bool wantWet)
        {
            bool turbojet = d == null || d.Kind == EngineKind.Turbojet;
            if (wantWet)
            {
                double t7 = 1900.0;
                // 探测豁免：取不到加力出口温度就用 1900 K 兜底
                try { if (d != null && d.Afterburner != null) t7 = d.Afterburner.ExitTemp; } catch { }
                if (turbojet)
                {
                    if (t7 < JetWarmCut) return PlumeClass.WetJetWarm;
                    if (t7 < JetHotCut) return PlumeClass.WetJetMid;
                    return PlumeClass.WetJetHot;
                }
                if (t7 < FanWarmCut) return PlumeClass.WetFanWarm;
                if (t7 < FanHotCut) return PlumeClass.WetFanMid;
                return PlumeClass.WetFanHot;
            }
            if (turbojet)
            {
                // 早期涡喷（Jumo 004 / J35 那代）干态是有烟的琥珀灰，不能和 CFM56 那种
                // 近无色的尾流用同一套贴图。技术级是这台机器自带的数据，不用玩家勾。
                if (EraYear(d) <= OldJetEraCut) return PlumeClass.DryOld;
                return PlumeClass.DryLow;
            }
            double bpr = 0.0;
            // 探测豁免：涵道比算不出就当涡喷一类羽流
            try { bpr = d.BypassRatio(); } catch { }
            return bpr > HighBypassCut ? PlumeClass.DryHigh : PlumeClass.DryLow;
        }

        /// <summary>1960 年以前算早期涡喷（Jumo 004 1944、J35 1947、J57 1950 都还在冒烟）。</summary>
        public const int OldJetEraCut = 1958;

        /// <summary>从技术级名字里抠出年代，抠不出按 1970 处理（宁可少用老贴图）。</summary>
        private static int EraYear(EngineDesignData d)
        {
            try
            {
                if (d == null || d.Tech == null || string.IsNullOrEmpty(d.Tech.Name)) return 1970;
                string n = d.Tech.Name;
                string digits = "";
                for (int i = 0; i < n.Length && digits.Length < 4; i++)
                    if (n[i] >= '0' && n[i] <= '9') digits += n[i];
                int y;
                if (digits.Length == 4 && int.TryParse(digits, out y)) return y;
                if (digits.Length == 2 && int.TryParse(digits, out y)) return 1900 + y;
            }
            // 探测豁免：模板年代解析失败就当 1970 年代那套
            catch { }
            return 1970;
        }

        public bool Attached;
        public string Note = "";
        /// <summary>cfg 里真找到了几个模块（自检用，应该是 9）。</summary>
        public int FoundModules;
        /// <summary>三轴统一缩放（对应模板的 authored 尺寸）。</summary>
        public double ScaleR;
        /// <summary>湿态单独一套缩放（跟核心机/加力段出口半径，而不是外涵出口）。</summary>
        public double ScaleRWet;
        /// <summary>兼容旧读数用：羽流等效长度 = 半径 × 缩放 × 25（加力模板最长那根）。</summary>
        public double ScaleL;
        /// <summary>这台机器实际选中的构型。</summary>
        public PlumeClass DryClass = PlumeClass.DryLow;
        public PlumeClass WetClass = PlumeClass.WetJetMid;

        private readonly PartModule[] _mods = new PartModule[ClassCount];
        private readonly object[] _throttleCtrl = new object[ClassCount];
        private bool _routed;
        /// <summary>这台是涡喷还是涡扇（运行时换档要它，不能每次去问设计）。</summary>
        private bool _isTurbojet;
        /// <summary>花瓣开度：收起来时实际出口比唇口小，羽流必须跟着变细。</summary>
        private float _openFrac = 1f;
        private float _scaleOpen = -1f;

        /// <summary>同一件零件上的引擎模块（缓存）：花瓣开度要吃它的换算转速。</summary>
        private KSP.ModuleJetForgeEngine _engine;
        private EngineDesignData _design;
        private bool _scaleApplied;
        /// <summary>上一次真正写缩放的时刻。Waterfall 自己每帧也会重新套模板偏移，
        /// 我们写进去的被它按原值刷回去过一次（玩家看到的"直径还是风扇那么大"），
        /// 所以隔一秒重写一次——便宜，且能把"被刷回去"这件事关在门外。</summary>
        private float _scaleTick;
        /// <summary>热浪这一帧该不该亮（关车/慢车到底就该没有）。</summary>
        private bool _hazeOn = true;
        private string _sig = "";
        private double _wetLevel = -1.0;
        private double _dryLevel = -1.0;
        /// <summary>这两路的比例是不是真的写进模板控制器了（写不进去就每帧重试）。</summary>
        private bool _wetPushed;
        private bool _dryPushed;
        private int _pushState = -1;
        private float _noPush;
        private bool _noPushWarned;
        private bool _wetOn = true;
        /// <summary>这台机器的设计里到底有没有加力。没有就一个湿模块都不许开。</summary>
        private bool _allowWet = true;
        /// <summary>九个模块各自"想要开/已经开"。FX 表是模块 Start() 后才有的，得重试。</summary>
        private readonly bool[] _wantOn = new bool[ClassCount];
        private readonly bool[] _isOn = new bool[ClassCount];
        /// <summary>这个模块的开关状态有没有确认过（没确认过就必须动手，不能拿默认 false 当"已关"）。</summary>
        private readonly bool[] _known = new bool[ClassCount];
        /// <summary>没选中的模块有没有把油门控制器钉过 0（SetEnabled 万一失效，它们也不能亮）。</summary>
        private readonly bool[] _darkPushed = new bool[ClassCount];
        private float _darkTick;
        private bool _enableLogged;
        /// <summary>羽流挂在哪个零件上（由 `Sync` 记下）：`SyncScale` 要顺着它问引擎模块的换算转速。</summary>
        private Part _part;

        /// <summary>这台机器有没有加力燃烧室。没有的机器不许有喷流层，只留热浪。</summary>
        private bool _hasAb = true;

        /// <summary>喷流层现在是开着的还是关着的（只在真的变了才动手）。</summary>
        private bool _plumeShown = true;

        private static bool _probed;
        private static bool _available;

        /// <summary>AssemblyLoader 里有没有 Waterfall。</summary>
        public static bool Available()
        {
            if (_probed) return _available;
            _probed = true;
            try
            {
                for (int i = 0; i < AssemblyLoader.loadedAssemblies.Count; i++)
                {
                    var la = AssemblyLoader.loadedAssemblies[i];
                    if (la == null || la.assembly == null) continue;
                    if (la.assembly.GetName().Name == "Waterfall") { _available = true; break; }
                }
            }
            catch { _available = false; }
            return _available;
        }
    }
}
