using System;
using System.Collections.Generic;
using System.Text;
using JetForge.Core;
using JetForge.FX;
using JetForge.UI;
using UnityEngine;

namespace JetForge.KSP
{
    /// <summary>
    /// 自定义喷气发动机的 KSP 模块。
    ///
    /// 它继承原版 ModuleEngines，但不用原版的性能曲线：推力、油耗、转速全部来自
    /// JetForge 的物理求解器（预解成网格，飞行中三线性内插）。之所以继承 ModuleEngines
    /// 而不是从头写：进气道、整流通、FX、级分离音频这些原版都做好了，
    /// 我们只需要接管「推力是多少、烧多少油」。
    ///
    /// 与 SolverEngines 的关系：不用它。GameData 里没有这个 dll，硬依赖会让 mod 装不上。
    /// 我们自己复刻了它的结构（引擎模块 / 求解器分离），物理内核在 JetForge.Core 里，
    /// 不引用任何 KSP 类型，所以同一份内核能在命令行里跑回归测试。
    ///
    /// 两种模式喂同一台机器：
    ///  - 简单模式只调目标（涵道比/增压比/燃烧室温度/排气温度/直径），没有几何；
    ///  - 残酷模式调几何（级数/叶尖直径/轴连接），是 mod 的主方向。
    /// 飞行中的网格内插两条路共用，所以切换模式不影响性能开销。
    /// </summary>
    public partial class ModuleJetForgeEngine : ModuleEngines, IPartMassModifier, IPartCostModifier
    {
        // ---------- 持久化 ----------
        /// <summary>残酷模式的设计参数，存在 part 的 module 节点里（见 OnSave/OnLoad）。</summary>
        public EngineDesignData Design = new EngineDesignData();
        public DesignConstants Consts;
        public PerformanceGrid Grid;
        public CycleResult DesignPoint;
        public CheckReport LastReport;

        /// <summary>质量与造价估算。外形独一无二，重量也必须独一无二。</summary>
        public MassBreakdown Mass;

        // ---------- 简单模式 ----------
        public bool UseSimple;
        public SimpleDesign SimpleCfg = new SimpleDesign();
        public SimpleConstants SimpleConsts;
        public SimpleResult SimplePoint;

        /// <summary>设计变了就把网格标脏，编辑器与飞行中都生效。</summary>
        public bool GridDirty = true;

        /// <summary>现在这具外形是按哪套设计建的（指纹）。对不上就重建，见 RefreshPartMeshIfNeeded。</summary>
        private string _meshSig;

        /// <summary>外形自愈的节流计时（见 OnUpdate）。</summary>
        private double _healTimer;

        /// <summary>
        /// 编辑器里的兜底自愈（KSP 会按名字反射调用 OnUpdate 的 PartModule 方法）。
        ///
        /// 为什么需要它：ALT 复制零件、对称放置零件，KSP 是**克隆整个零件**——新件既不会
        /// 再走一次我们的 OnStart（有些路径会，有些不会），而且它跟原来那件**共用同一份网格**。
        /// 靠"设计变了才重建"这套判断兜不住"我的网格节点没了"这种状态，玩家看到的就是
        /// 一个透明零件，只有重读载具才恢复。
        ///
        /// 这里的成本压到最低：先看一眼自己的节点在不在，在就直接返回；只有当它不在、
        /// 或者指纹对不上时才算指纹、才重建。
        /// </summary>
        public override void OnUpdate()
        {
            if (part == null) return;
            // 只在编辑器里做：ALT 复制 / 对称放置只发生在编辑器；飞行中的零件都是
            // 读档新建的，不存在共享网格，也就没有什么要自愈的。以前这里飞行中也跑，
            // 每 0.5 s 一次 FindModelTransform + GetComponent，纯属白花。
            if (!HighLogic.LoadedSceneIsEditor) return;
            _healTimer -= TimeWarp.deltaTime;
            if (_healTimer > 0.0) return;
            _healTimer = 0.5;

            Transform vis = part.FindModelTransform(JetForgeMeshBuilder.VisualObjectName);
            if (vis != null && _meshSig != null && JetForgeMeshBuilder.OwnsVisual(part, vis, JetForgeMeshBuilder.VisualObjectName)) return;

            // 走到这里有两种情况：外形节点没了，或者手里那份网格是**从别人那儿继承来的**
            // （ALT 复制/对称克隆共用同一个 Mesh 实例，名字里盖的不是我的 instanceID）。
            // 后者必须强制重建一份自己的，否则原件一重建，这件的网格就跟着变透明。
            _meshSig = null;
            RefreshPartMeshIfNeeded();
        }

        /// <summary>
        /// 设计指纹的短打印形式。指纹本身是整个设计对象的逐字段文本（几千字符），
        /// 直接写进日志会把 KSP.log 灌满，而调试要看的是"两台是不是同一套"，一个 8 位哈希就够。
        /// </summary>
        private static string ShortSig(string sig)
        {
            if (string.IsNullOrEmpty(sig)) return "无";
            unchecked { return ((uint)sig.GetHashCode()).ToString("X8"); }
        }

        // ---- 独立进气道零件（气从哪来）----
        //
        // 进气道是**另一个零件**：它有自己的 cfg、存档、质量、造价和面板。这里只做一件事——
        // 每隔一会儿看看"我附近那几件进气道是谁、参数是什么"，合成一份合计口径喂进解算器。
        // 一件都没装时 Install 为 null，解算器退回发动机自己设计里那份 IntakeSpec，
        // 也就是老存档一个数都不动。
        private IntakeInstall _install;
        private string _installStamp = null;

        // ---------- 可调 ----------
        [KSPField] public bool autoRebuild = true;
        // 原来这里有个 gridBuildBudgetSeconds，从来没被读过——全表构建实测 0.20 s，
        // 任何预算都拦不住它，留着就是一份假承诺。删掉了。

        // ---------- 读数（会出现在右键菜单里）----------
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_thrust", guiUnits = "kN", guiFormat = "F2")]
        public float readThrust;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_fuel_flow", guiUnits = "kg/s", guiFormat = "F3")]
        public float readFuelFlow;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "SFC", guiUnits = "kg/(N·h)", guiFormat = "F4")]
        public float readSfc;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_pressure_ratio_opr", guiFormat = "F2")]
        public float readOpr;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_bypass_ratio_bpr", guiFormat = "F2")]
        public float readBpr;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "N1", guiUnits = "%", guiFormat = "F1")]
        public float readN1;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "N2", guiUnits = "%", guiFormat = "F1")]
        public float readN2;

        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "N3", guiUnits = "%", guiFormat = "F1")]
        public float readN3;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "EGT", guiUnits = "K", guiFormat = "F0")]
        public float readEgt;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_intake_flow", guiUnits = "kg/s", guiFormat = "F1")]
        public float readMassFlow;

        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_dry_mass", guiUnits = "t", guiFormat = "F3")]
        public float readMass;

        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_thrust_to_weight", guiFormat = "F2")]
        public float readTwr;

        [KSPField(isPersistant = false, guiActiveEditor = true, guiName = "#autoLOC_JF_f_cost", guiUnits = "funds", guiFormat = "N0")]
        public float readCost;

        /// <summary>
        /// 这台发动机此刻为什么出力/不出力，一句话。
        /// 没有它的时候，玩家推着油门却一点推力都没有，只能猜自己哪一页调错了。
        /// </summary>
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_status")]
        public string readState = "";

        /// <summary>
        /// 设计缺陷此刻的后果：喘振、涡轮超温、贫油熄火、叶尖超速、烧蚀降额。
        /// 判据只告诉你"哪一项不合格"，这一行告诉你"所以这趟飞成什么样"。
        /// </summary>
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_JF_f_defect")]
        public string readDefect = "";

        /// <summary>
        /// 三级台账的一句话摘要：设计警告 / 运行风险 / 故障事件各有多少条、最重的是什么。
        /// 与"缺陷"那行的区别：那行说"此刻什么后果"，这行说"这台机器身上一共有几笔账"。
        /// </summary>
        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = false, guiName = "#autoLOC_JF_f_defect_ledger")]
        public string readFaultBook = "";

        /// <summary>同一个缺陷被设计期与运行期都罚过时，这里写明放过了哪一次（自检与调试用）。</summary>
        public string readDefectDedup = "";

        /// <summary>
        /// 燃烧室油气比与它的可燃窗口。
        ///
        /// 玩家问"喷油量到底怎么影响状态"，光给推力不够——油气比是把喷油量、
        /// 进气量、当前工况连成一条线的那一个数：低了吹熄、高了富油熄火、
        /// 中间才是火焰待得住的地方。这行就是那条线的刻度。
        /// </summary>
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_JF_f_fuel_air_ratio")]
        public string readMix = "";

        /// <summary>
        /// 慢车档：玩家设的推力占比、反解出来的慢车油气比、贫油下限，以及"这一档到底给不给得出"。
        /// 玩家报"设低慢车不熄火"时，先看这一行——游戏里看到的数比我在命令行算的更可信。
        /// </summary>
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_JF_f_idle_setting")]
        public string readIdle = "";

        /// <summary>
        /// 慢车供油不足的锁存。
        ///
        /// 不能只靠原版 `flameout` 字段：我们每帧调 `base.UpdateThrottle()`，原版有机会把它清掉，
        /// 而状态栏那一行又会被后面的"正常"分支覆盖——玩家看到的就是"什么都没发生"。
        /// 所以这一条由模块自己记住，直到玩家真的关车（`!EngineIgnited`）才复位。
        /// </summary>
        private bool _idleStarved;

        // 空中再起动的三根小账：现在是不是被窗口挡住了、挡的理由是什么（变了才喊一次），
        // 以及数字机自动再点火攒到哪一帧。
        private bool _relightBlocked;
        private string _relightReason = "";
        private int _relightFrame;

        // 惰转让残余推力跟着转子一起溜：火没了，能吹出来的就只剩转子储的那点动能，
        // 所以 F ∝ ω²（见 Core/CoastThrust.cs）。这里只记住"熄火那一刻"的起点，
        // 之后每一帧按当时的转速比例折——推力表与转速表从此说的是同一件事。
        //
        // 上一版给推力单独一个 0.22 s 的指数衰减：推力两百毫秒就没了，转速却还在 90%，
        // 玩家报的"油门到底推力啪一下归零、转速表还在高位"就是它。
        private double _coastThrustKN;
        private double _coastFuelTps;
        private double _coastThrust0KN;
        private double _coastFuel0Tps;
        private double _coastSpoolAtCut;
        private bool _wasCoasting;

        /// <summary>上一帧真正交给原版链路的推力（kN）与油耗（t/s），突发熄火时当惰转起点。</summary>
        private double _lastOutKN;
        private double _lastOutFuelTps;

        /// <summary>这一次熄火的原因（惰转段状态栏用）。</summary>
        private string _idleNote = "";

        /// <summary>
        /// 彻底停车的原因（转速掉到再点火下限以下时写一次）。
        /// 与 `_idleNote` 分开：那个是"为什么会熄火"，这个是"为什么已经救不回来了"，
        /// 玩家要改的东西不一样（前者改慢车供油，后者重新点火并查为什么会掉到这么低）。
        /// </summary>

        /// <summary>热端累计损伤 0~1（1 = 涡轮烧蚀，永久降额 15%）。</summary>
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_JF_f_heat_damage", guiUnits = "%", guiFormat = "F0")]
        public float readDamage;

        /// <summary>喘振的实时状态。与"缺陷"分开：缺陷是设计里带着的，喘振是这一手油门逼出来的。</summary>
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_JF_f_surge")]
        public string readSurge = "";

        /// <summary>控制系统这一帧干了什么（限温/限转/限幅/抑制喘振）。无控的机器这行永远是"无控"。</summary>
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_JF_f_control")]
        public string readControl = Loc.T("无控", "no control");

        private readonly DefectRuntime _defect = new DefectRuntime();
        private readonly SurgeRuntime _surge = new SurgeRuntime();
        private double _lastThrottleSurge = -1.0;

        /// <summary>控制系统的运行状态：它是唯一记得"上一帧油门到过哪里"的东西，加速度限幅靠它。</summary>
        private readonly JetForge.Core.ControlRuntime _control = new JetForge.Core.ControlRuntime();
        /// <summary>上一帧的涡轮前温度（K）与最高转速（%）。控制器的传感器读数永远慢一帧。</summary>
        private double _lastT4;
        private double _lastNPct;
        // 上一帧解出来的涡轮剩余功率（W）。用它定这一帧的转子时间常数。
        private double _surplusW;
        // 按功率积分的转子律。惯量与设计转速只跟设计有关，换设计时重建。
        private JetForge.Core.SpoolDynamics.Law _spoolLaw;
        private double _lastWComp;

        // 油量控制：杆位 → 喷油量 →（解算器反查）目标转速。
        private readonly JetForge.Core.FuelLoop _fuelLoop = new JetForge.Core.FuelLoop();
        private JetForge.Core.FuelSchedule _fuelSched;
        private double _fuelSchedAlt = double.NaN, _fuelSchedMach = double.NaN, _fuelSchedAb = double.NaN;

        /// <summary>上一帧的净推力（N）。FADEC 的闭环误差要用它。</summary>
        private double _lastThrustN;
        private bool _fuelLoopInit;
        private int _fuelSchedRetryFrame;
        private Defects.Risk _risk;
        private string _riskSig = "";
        private bool _riskReady;
        /// <summary>判据不成立时的推力降额（1 = 无惩罚）。缺陷有后果，但不把推力写成 0。</summary>
        private double _designDerate = 1.0;

        private PerformanceGrid.Sample _sample;

        /// <summary>加力表。没配加力时是 null；配了就与 <see cref="Grid"/> 并存，飞行中按开关切换。</summary>
        public PerformanceGrid GridAb;

        /// <summary>
        /// 飞行链路真正的数据源：网格或实时。
        /// 它替掉的是"飞行中只会 Interp"这件事——玩家想看的恰是格点之间的折线。
        /// </summary>
        public ICycleSampler Sampler;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_live_solve")]
        public bool UseRealtime = true;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_refresh_rate", guiUnits = "Hz", guiFormat = "F0")]
        public double RealtimeHz = 0.0;

        [KSPField(guiActive = false, guiName = "#autoLOC_JF_f_solver_state")]
        public string readSolver = "";

        /// <summary>
        /// 「强制开」：无视油门位置，点亮就烧满加力。
        /// 以前这个字段兼任"加力开关"，于是"自动"和"强制"挤在同一个布尔里——
        /// 玩家点了自动，实际只是把开关关上，火永远不亮。三态必须拆成两个布尔。
        /// </summary>
        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = false, guiName = "#autoLOC_JF_a_afterburner")]
        public bool afterburnerLit;

        /// <summary>「自动」：油门越过 <c>LightThrottle</c> 以后按比例注入，100% 油门才是满加力。</summary>
        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = false, guiName = "#autoLOC_JF_f_afterburner_auto")]
        public bool abAuto = true;

        /// <summary>三态读数。面板上要能一眼看出现在到底是关、自动还是强制开。</summary>
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_JF_f_afterburner_mode")]
        public string readAbMode = "";

        /// <summary>
        /// 按一下换一档：关 → 自动 → 强制开 → 关。
        /// 只有这台机器真的配了加力、而且加力表建出来了，才允许点亮——
        /// 否则按钮看着能按却什么也不发生，比没有按钮更糟。
        /// </summary>
        [KSPAction("#autoLOC_JF_a_afterburner_switch")]
        public void ToggleAfterburnerAction(KSPActionParam p)
        {
            CycleAbMode();
        }

        [KSPEvent(guiActive = true, guiName = "#autoLOC_JF_f_afterburner_auto", active = true)]
        public void ToggleAfterburnerEvent()
        {
            CycleAbMode();
        }

        /// <summary>三态循环。顺序固定，按钮上的字与当前状态永远一致。</summary>
        public void CycleAbMode()
        {
            if (!HasAfterburner()) return;
            bool nl, na;
            AbModes.Next(afterburnerLit, abAuto, out nl, out na);
            afterburnerLit = nl;
            abAuto = na;
            RefreshAbButton();
        }

        /// <summary>老的二值入口（存档/自检/外部代码还在用）。<c>true</c> = 强制开。</summary>
        public void SetAfterburner(bool on)
        {
            if (on && Sampler == null && !GridDirty) RebuildGrid();
            if (!HasAfterburner())
            {
                afterburnerLit = false;
                abAuto = false;
                RefreshAbButton();
                return;
            }
            afterburnerLit = on;
            if (on) abAuto = false;
            RefreshAbButton();
        }

        // internal 而不是 private：自检要当着预制件的面调一次，
        // 因为它的名字是**运行期**拼出来的，加载时扫的那一遍扫不到（§273）。
        internal void RefreshAbButton()
        {
            if (Events == null || !Events.Contains("ToggleAfterburnerEvent")) return;
            if (!HasAfterburner())
            {
                Events["ToggleAfterburnerEvent"].guiName = "#autoLOC_JF_f_afterburner_not_fitted";
                Events["ToggleAfterburnerEvent"].active = false;
                readAbMode = Loc.T("未配置", "not fitted");
                return;
            }
            EngineDesignData sd = _plumeDesign ?? Design;
            double lt = sd != null ? sd.Afterburner.LightThrottle : 0.70;
            AbModeKind kind = AbModes.Of(afterburnerLit, abAuto);
            Events["ToggleAfterburnerEvent"].active = true;
            // 先让 LocKsp 把整条键翻成话，再拼模式后缀。
            // 反过来（键和后缀一起交给 KSP）得到的是一个不存在的键，游戏里原样显示
            // 「#autoLOC_JF_f_afterburner 自动（油门 70% 起）」——见 §273。
            Events["ToggleAfterburnerEvent"].guiName = JetForge.Core.LocKsp.Gui("#autoLOC_JF_f_afterburner") + AbModes.Label(kind, lt);
            readAbMode = AbModes.Label(kind, lt);
        }

        // 尾焰句柄。存着而不是每帧去场景里找物体，省掉查找开销。
        // 自绘尾焰（JetForgePlume）已删：羽流现在硬依赖 Waterfall。
        private EngineDesignData _plumeDesign;

        // Waterfall 句柄。装了 Waterfall 就由它出羽流，我们自绘的那套就关掉——
        // 两条尾焰叠在一起不是"更漂亮"，是 bug 的观感。
        private JetForgeWaterfall _waterfall;
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "#autoLOC_JF_f_plume")]
        public string readPlume = "";
        // 羽流缩放读回：证明"按喷口直径缩"这件事真的落到模型上了。
        // 只写 TemplateScaleOffset 不调 ApplyTemplateOffsets 时，这里会读出 0，
        // 而屏幕上的火焰仍是模板原始大小——那正是"火焰比喷口粗、穿模"的来源。
        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = false, guiName = "#autoLOC_JF_f_plume_scaling")]
        public string readPlumeScale = "";

        // 矢量喷口的当前偏转角。没装矢量喷口时这里写"固定"，别写 0°——0 和"没有这个功能"是两件事。
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_JF_f_vectoring")]
        public string readVector = Loc.T("固定", "fixed");

        // 羽流火色读回：T7 落在哪一档、乘了几倍亮度、真写到几个渲染体。
        // 写材质是静默的——"写了但没生效"和"没写"在屏幕上一样。
        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = false, guiName = "#autoLOC_JF_f_plume_colour_temperature")]
        public string readPlumeColor = "";

        // 音效句柄：三个 AudioSource（气流轰鸣 / 压气机啸叫 / 加力轰爆）。
        // 装了 RSE 就只补加力层——同一个声音叠两遍比没声音更难听。
        private FX.JetForgeSound _sound;
        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = false, guiName = "#autoLOC_JF_f_sound")]
        public string readSound = "";

        // 转子惯性：这是"已经转到多少"，不是"油门开到多少"。两者之间差着几秒。
        private double _spoolThr;
        private bool _spoolInit;
        private FloatCurve _ispCurve;
        private float _ispLast = float.NaN;

        // 风车状态。熄火以后这台机器不是"不存在"，是一台被来流吹着转的大风扇——它是阻力。
        private WindmillState _windmill;

        /// <summary>加力瞬态：0~1 的建立比例，跨帧保存。</summary>
        private readonly AbState _abState = new AbState();

        /// <summary>上一帧实际达到的加力比例（0~1）。羽流与读数都用它，不用开关状态。</summary>
        private double _abFrac;

        // ---------- 抹平层 ----------
        //
        // 玩家报的两件事：飞行中读数一直在抖、声音跟着抖得难听。
        // 抖的来源有两个，都不在循环里：
        //   ① 输入抖：高度与马赫每帧都带着刚体求解的微小振荡，而推力对马赫极敏感（进气道冲压）；
        //   ② 输出抖：实时解算有 30 Hz 闸门，同一个值重复两帧再跳一格，加上 spool 的指数逼近
        //      永远差最后一点点，推力就永远在"最后一点点"里抖。
        // 所以这里分两条：输入先抹平（0.30 s），输出再抹平（0.20 s）。
        // **物理解算一步没改**——衰减率、喘振拍子、加力瞬态还是原来的时间常数，
        // 只是读数、音效、羽流和喂给原版的推力都从这条管道上读。
        private readonly Lag _lagAlt = new Lag();
        private readonly Lag _lagMach = new Lag();
        private readonly Lag _lagThrust = new Lag();
        private readonly Lag _lagMdot = new Lag();
        private readonly Lag _lagN1 = new Lag();
        private readonly Lag _lagN2 = new Lag();
        private readonly Lag _lagN3 = new Lag();
        private readonly Lag _lagEgt = new Lag();
        private readonly Lag _lagMax = new Lag();
        private readonly Lag _lagFuel = new Lag();
        private readonly Lag _lagThr = new Lag();
        private readonly Lag _lagAb = new Lag();
        private readonly Lag _lagPetal = new Lag();
        private readonly Lag _lagVecP = new Lag();
        private readonly Lag _lagVecY = new Lag();

        /// <summary>矢量喷口当前偏转角（度）。正负号由 ThrustVector 的力学解算出，不是猜的。</summary>
        private float _vecPitch, _vecYaw;

        /// <summary>磨过的三轴指令：死区、轴开关、限速率都在里面（Core/VectorCommand.cs）。</summary>
        private readonly JetForge.Core.VectorCommand _vecCmd = new JetForge.Core.VectorCommand();

        /// <summary>这一帧实际偏了多少度（解算结果，含滚转项）。</summary>
        private double _vecAngleDeg;

        /// <summary>花瓣开度的抹平结果（0~1）。</summary>
        private float _petalOpen = -1f;

        /// <summary>喷口偏转的响应时间（秒）。真机液压作动筒跟不上飞控，约 0.2~0.4 s。</summary>
        public const double VectorTau = 0.28;

        /// <summary>喷口开度的响应时间（秒），比偏转快一点。</summary>
        public const double PetalTau = 0.15;

        /// <summary>输入抹平的时间常数（秒）。太大会让爬升时读数追不上，太小又抹不掉抖。</summary>
        public const double InputTau = 0.30;

        /// <summary>
        /// 输出抹平的时间常数（秒）。
        /// 0.20 太钝：抹平层自己就带出"读数慢慢爬"的迟滞，玩家看到的是另一种不自然。
        /// 真正的抖来自解算器给的数在跳（容差缓存 + 刷新闸），那条已经改成默认每帧算，
        /// 这里只需要一层很薄的去毛刺。
        /// </summary>
        public const double OutputTau = 0.08;

        /// <summary>清掉抹平层（换设计、关车、从时间加速回来都要清，否则会看见旧值慢慢爬）。</summary>
        private void ResetLags()
        {
            _lagAlt.Reset(); _lagMach.Reset();
            _lagThrust.Reset(); _lagMdot.Reset();
            _lagN1.Reset(); _lagN2.Reset(); _lagN3.Reset();
            _lagEgt.Reset(); _lagMax.Reset(); _lagFuel.Reset();
            _lagThr.Reset(); _lagAb.Reset();
            _lagPetal.Reset(); _lagVecP.Reset(); _lagVecY.Reset();
            _petalOpen = -1f; _vecPitch = 0f; _vecYaw = 0f; _vecAngleDeg = 0.0;
            _vecCmd.Reset();
            // 控制系统也要清：否则换一台机器/重新点火时，限幅会从上一台留下的 Command 往下走，
            // 表现是"刚点火就自己收了一下油"。
            _control.Reset();
            _lastT4 = 0.0;
            _lastNPct = 0.0;
        }

        /// <summary>
        /// 出声。三层横轴各用各的，而且**必须用不同的量**：
        /// 油门（气流声，抹平过的输入）、物理转速（啸叫与共振，转子积分出来的连续量）、
        /// 加力比例（轰鸣）。
        ///
        /// 教训：上一版把三个横轴全换成同一批抹平后的读数，结果九层被"拉平"成一层——
        /// 油门一动所有层一起动，层级感就没了。声音的层次来自"油门已经推满、
        /// 转速还在爬"这种差异，所以转速这一路必须是没被油门抹平层拖住的那个量。
        /// </summary>
        private void UpdateSound(double throttle, double spoolPct, double abFrac, bool running, double alt)
        {
            if (_sound == null) return;
            _sound.Update(throttle, spoolPct, abFrac, running, alt);
        }

        /// <summary>
        /// 这一帧油门要烧多满的加力（0~1）。AJE 的做法：油门过 <c>LightThrottle</c> 以后
        /// 按比例注入，100% 油门才是满加力；手动把开关按下去则无条件满加力，
        /// 这样"我就想在低油门点亮加力"这件事仍然做得到。
        /// </summary>
        public double AbTarget(double throttle, EngineDesignData d)
        {
            if (d == null || !d.Afterburner.Enabled) return 0.0;
            return AbModes.Target(AbModes.Of(afterburnerLit, abAuto), throttle,
                d.Afterburner.LightThrottle);
        }
        [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = false, guiName = "#autoLOC_JF_f_windmill_drag")]
        public string readWindmill = "";

        // ---------- 生命周期 ----------

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (Design == null) Design = new EngineDesignData();
            if (SimpleCfg == null) SimpleCfg = new SimpleDesign();
            // 设计参数、常数、网格这一串依赖关系要先联动一遍再对外报数。
            EnsureReady();
            // 与 OnLoad 那行配对：读档时是什么、开始运行时还是不是同一个，两行一比就知道
            // 设计是"根本没读进来"还是"读进来又被谁改了"。
            try
            {
                string sig = Design != null ? DesignSignature.Of(Design) : "空";
                Debug.Log("[JetForge] 起飞准备 " + (part != null && part.partInfo != null ? part.partInfo.name : "?") +
                          "｜" + (HighLogic.LoadedSceneIsFlight ? "飞行" : "编辑器") +
                          "｜设计名 " + (Design != null ? Design.DesignName : "空") +
                          "｜指纹 " + ShortSig(sig));
            }
            // 这一行是"设计到底有没有读进来"的唯一现场证据。它打不出来，说明日志本身就坏了，
            // 那就换一条更简单的日志把它说出来——静默失败的日志比没有日志更坏。
            catch (Exception e) { Debug.LogWarning("[JetForge] 启动信息没打全：" + e.GetType().Name + "：" + e.Message); }
            // 硬依赖：Waterfall（羽流）+ RSE（干态声音）。缺了不崩，但要说清楚缺什么。
            Dependencies.LogOnce();
            // 语言：界面文字中英双语，语言从游戏设置读（Loc.cfg 可手动覆盖）。
            JetForge.Core.LocKsp.Install();
            PruneStockPaw();
            // 存档里可能记着「加力开着」，但这台机器已经改成没加力的设计了——那就别亮着。
            SetAfterburner(afterburnerLit);
            if (HighLogic.LoadedSceneIsEditor)
            {
                LastReport = EvaluateReport();
                // 装进 VAB 就先说清楚它到底行不行，别等玩家推上发射台才发现推力是 0。
                // 判据不成立不再等于"飞不了"：说清楚降额多少，让玩家自己决定要不要硬上。
                string why = FirstError();
                double derate = Defects.DesignDerate(LastReport);
                SetState(!string.IsNullOrEmpty(why)
                        ? string.Format(Loc.T("设计不成立：{0}（推力 ×{1:F2}，仍可点火试飞）", "Design does not hold up: {0} (thrust x{1:F2}, you can still light it and fly)"), why, derate)
                        : (DesignOk() ? Loc.T("正常", "OK") : Loc.T("此状态烧不出推力（风车/停车）", "No thrust in this state (windmilling or shut down)")));
            }
        }

        /// <summary>
        /// 原版 ModuleEngines 自带的那几行 PAW：燃料流向、推力限制、特征比冲、状态、节流阀……
        /// 它们和我们的面板重复，而且是**原版口径**——燃料流量按它自己的 Isp 算、状态写 Nominal，
        /// 于是同一块面板上会出现两组互相矛盾的数（比如我们的"油耗 0.35 kg/s"和它的"燃料流向 0.07"）。
        /// 玩家分不清哪个是真的，能做的只有关掉重复的那一组。
        ///
        /// 只关显示，不动功能：原版的节流限制器、燃料流向计算照旧参与（我们靠 StockBridge 喂它）。
        /// 用 `ContainsKey` 兜底——字段名是原版内部的，升级 KSP 后可能不在了，不能因此抛异常。
        /// </summary>
        /// 这些字段在 OnStart 里会被关掉显示（见 <see cref="PruneStockPaw"/>）。
        /// 公开出来是给自检用的：自检在 prefab 上跑，OnStart 还没执行，
        /// 不把这份名单说出来，自检报的"PAW 有几项"就会比玩家实际看到的多。
        public static readonly string[] StockPawKeys =
        {
            "fuelFlowGui", "realIsp", "status", "throttleSetting", "currentThrottle",
            "thrustPercentage", "upgradedThrustPercentage",
            // 这三个是自检把 field 名打出来才认出来的（面板上分别叫"推力限制""节流阀""节流阀"）：
            // finalThrust 是原版那个"最终推力"读数，和我们自己的"推力"是同一件事两种口径；
            // independentThrottle 与 independentThrottlePercentage 是原版的独立油门控制。
            "finalThrust", "independentThrottle", "independentThrottlePercentage"
        };

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            ConfigNode d = node.GetNode(EngineDesignData.NodeName);
            if (d != null)
            {
                Design = EngineDesignData.Load(d);
                GridDirty = true;
            }
            ConfigNode sm = node.GetNode(SimpleDesign.NodeName);
            if (sm != null)
            {
                SimpleCfg = SimpleDesignPersistence.Load(sm);
                GridDirty = true;
            }
            string mode = node.GetValue("useSimple");
            if (!string.IsNullOrEmpty(mode)) UseSimple = mode == "True";

            // 读档时把"这一台读到了什么"写一行日志。
            // 现场症状是"同款两台，进飞行后有一台回到初始设计"：那种情况下日志里会出现
            // 一台"设计节点 有"、另一台"设计节点 没有"，一眼就能分清是存档没写全还是被后来的代码盖掉。
            // 注意这段必须在最外层兜住：OnLoad 阶段 `part`/`partInfo` 可能还没挂好，
            // 日志自己抛异常会把 OnLoad 后面的事全废掉（第一次就踩了：NRE，被 catch 吃掉，什么也没写）。
            string whose, sig;
            try { whose = part != null && part.partInfo != null ? part.partInfo.name : "?"; }
            catch { whose = "?"; }
            try { sig = Design != null ? DesignSignature.Of(Design) : "空"; }
            catch { sig = "(指纹算不出来)"; }
            try
            {
                Debug.Log("[JetForge] 读档 " + whose +
                          "｜设计节点 " + (d != null ? "有" : "没有") +
                          "｜简单模式 " + (UseSimple ? "是" : "否") +
                          "｜设计名 " + (Design != null ? Design.DesignName : "空") +
                          "｜指纹 " + ShortSig(sig));
            }
            catch (Exception e) { Debug.LogWarning("[JetForge] 读档信息没打全：" + e.GetType().Name + "：" + e.Message); }
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            node.SetValue("useSimple", UseSimple ? "True" : "False", true);
            // 先删再写：OnSave 拿到的是同一份 module 节点，直接 AddNode 会让存档每存一次
            // 多出一个 JETFORGE_DESIGN（读的时候永远读第一个，但文件会越存越大）。
            node.RemoveNodes(EngineDesignData.NodeName);
            node.RemoveNodes(SimpleDesign.NodeName);
            if (Design != null)
            {
                ConfigNode d = node.AddNode(EngineDesignData.NodeName);
                Design.Save(d);
            }
            if (SimpleCfg != null)
            {
                ConfigNode sm = node.AddNode(SimpleDesign.NodeName);
                SimpleDesignPersistence.Save(SimpleCfg, sm);
            }
        }

        /// <summary>这台发动机现在接的是哪些进气道（合成后的合计口径；没装就是 null）。</summary>
        public IntakeInstall Install { get { return _install; } }

        /// <summary>这一刻有没有接上的进气道（自带参数的件不算——那是设计说明书，不是硬件）。</summary>
        public bool HasIntake { get { return _install != null; } }

        private bool? _editorIntake;
        private int _editorIntakeFrame = -1000;

        /// <summary>
        /// 编辑器里的静态尾焰预览。VAB 里发动机不转，原版也不会画火，
        /// 但设计外形时少了尾焰就少一半判断依据（长度、口径、会不会穿进机体）。
        /// 这里用设计点工况画一束固定的焰，跟飞行里那套完全同一条代码路径。
        /// </summary>
        /// <summary>尾焰此刻是否可见（给飞行探针与自检用）。现在只由 Waterfall 决定。</summary>
        public bool PlumeVisible
        {
            get { return _waterfall != null && _waterfall.Attached; }
        }

        /// <summary>
        /// 原版那条"编辑器里画一束静态尾焰"的预览已经删掉：羽流现在完全由 Waterfall 出，
        /// 而 Waterfall 的 Effect 在编辑器里不跑积分器——画出来的会是一束不动的死火。
        /// 看外形用网格本身，看火去飞。
        /// </summary>
        public bool PlumePreview { get { return false; } }

        // ---------- 两种模式的统一读数 ----------
        // 简单模式没有 CycleResult，残酷模式没有 SimpleResult。与其在每处调用都写 if，
        // 不如把「设计点的那几个数字」收成一组访问器，上层就不必知道现在是哪一档。

        /// <summary>
        /// 这一帧网格算出的推力（牛）。跟 DpThrust() 不是一回事：那个是海平面静止的设计点推力，
        /// 设计一改就不动，拿它跟飞行中的推力对照会得出"少了一半"的错误结论。
        /// </summary>
        public double modelThrustN;

        // ---------- 风车 ----------

        // ---------- 性能：接管原版 ----------

        /// <summary>
        /// 原版的 UpdateThrottle 负责把 thrustPercentage、油门响应、需求曲线揉成 currentThrottle。
        /// 我们让它先跑完，然后拿 currentThrottle 去查网格，这样「油门限制器」「独立油门」都还可用。
        /// </summary>
        public override void UpdateThrottle()
        {
            base.UpdateThrottle();

            // 判"有没有解算器"，不是判"有没有网格"：实时解算下 Grid 永远是 null。
            if (Sampler == null)
            {
                finalThrust = 0f;
                resultingThrust = 0f;
                modelThrustN = 0.0;
                readThrust = 0f;
                readSfc = 0f;
                readMassFlow = 0f;
                readFuelFlow = 0f;
                readEgt = 0f;
                readN1 = 0f;
                readN2 = 0f;
                readN3 = 0f;
                _abFrac = 0.0;
                _surge.Reset();
                _lastThrottleSurge = -1.0;
                readSurge = Loc.T("没有解算器", "no solver");

                // 桥接也必须归零。原版 ThrustUpdate 是
                //   requestedMassFlow = maxFuelFlow × 油门，推力 = requestedMassFlow × Isp × g，
                // 它根本不看我们的 finalThrust。设计一坏就不再喂它，它会拿上一帧（或者 cfg 里那份
                // 0.00096 t/s）继续算——于是"设计不成立"的机器照样推得动飞机，而 N1/N2 是 0。
                // 这就是"不成立的设计仍能工作"的来源：不是模型算了推力，是没人把桥断电。
                ApplyStockBridge(0.0, 0.0, 0.0);

                // 抹平层必须跟着清：否则下次有解算器时，读数会从上一段飞行留下的旧值慢慢爬回来。
                ResetLags();
                if (_sound != null) _sound.Update(0.0, 0.0, 0.0, false, 0.0);
                string why = FirstError();
                SetState(string.IsNullOrEmpty(why) ? Loc.T("没有性能表（设计未生效）", "no performance table (design not applied)") : Loc.T("设计不成立：", "design does not close: ") + why);
                return;
            }

            // 时间步先算出来：抹平层要用它，转子滞后也要用它。
            double spoolDt = TimeWarp.fixedDeltaTime;
            // 装/拆/改了进气道零件 → 性能网格与设计常数都得重算（每 30 帧查一次指纹）。
            // 以前这里只置 GridDirty、没人来重建：装了原版进气道也不喂气，飞行里用的还是
            // 上飞船那一刻的旧 install，读数自然跟热力图对不上。现在当场重来一遍。
            if (CheckIntakeStamp()) EnsureReady();
            if (spoolDt <= 0.0 || spoolDt > 1.0) spoolDt = Time.fixedDeltaTime;

            // 输入先抹平：高度与马赫是刚体物理解出来的，帧与帧之间有微小振荡，
            // 而推力对马赫的敏感度很高（冲压进气道），直接喂进去就是读数的抖动。
            // 真机的进气道对来流也有响应延迟，这一步不算凭空加惯性。
            double alt = _lagAlt.Step(FlightAltitude(), spoolDt, InputTau);
            double mach = _lagMach.Step(FlightMach(), spoolDt, InputTau);

            // 加力亮着就查加力表。两张表结构完全一样，只有单点求解的加力档不同，
            // 所以内插代码不用分叉——这也是为什么它值得单独建一张表而不是改公式。
            //
            // 但"开关亮着"不等于"火点着了"：压力太低会点不着，油气比太贫/太富会被吹熄。
            // 这里先问一次点火边界，不着就退回干态表，并把原因写进状态栏。
            EngineDesignData abD = _plumeDesign ?? Design;

            // AJE 式：加力不是一个开关，是油门最后那三成。真机推过加力起点以后，
            // 加力段的喷油量、推力、火焰亮度都跟着油门继续涨——玩家拧的是油门，不是开关。
            // 这里算出的 abTarget 就是"这一段要烧多满"，瞬态（1.3 s）再把它爬上去。
            double abTarget = AbTarget(StockBridge.FloorInput(currentThrottle), abD);

            // 自动模式也必须过点火边界这一关：以前拿 afterburnerLit 当"命令"，
            // 自动模式下它是 false，于是 Check 直接判不点着，火永远不亮。
            bool abWanted = afterburnerLit || abTarget > 0.02;
            // 点火边界按这台机器的**控制系统档次**算：老点火器高空点不着（最低可点压力更高），
            // 数字机则会在边界外当帧断油而不是喷着油等它着。
            AbResult abNow = AfterburnerLight.Check(abD, alt, mach, abWanted,
                JetForge.Core.ControlSystem.Effective(abD));
            bool abBurning = abNow.Lit && HasAfterburner();
            // 加力比例由 FlightStep 里的瞬态推进，这里只告诉它"能不能点着"。
            // 以前是"两张表插值"，现在是"带着比例问一次"——实时解算下这就是真解。

            // 数值链路全在 Core/FlightStep.cs：抬底 → 熄火风车 → 转子滞后 → 查表 → 自持闸 → 库存桥。
            // 搬到 Core 是为了它能整段进命令行回归——这条链路以前只有实飞才能验。
            EngineDesignData spoolD = _plumeDesign ?? Design;

            // 判据不成立不再等于"这台机器不存在"：降额照飞，状态栏一直骂。
            // 玩家要的是"缺陷有后果"，不是"一按点火就零推力"——后者连试飞都试不了，
            // 也就没法判断到底是哪个参数配坏了。
            _designDerate = JetForge.Core.Defects.DesignDerate(LastReport);

            // 慢车档那几个数摆出来：玩家报"设 2% 不熄火"时，第一个要看的就是这一行。
            readIdle = JetForge.Core.EngineGuard.Describe(Design, Consts);

            // 加速快慢由"涡轮剩余功率"决定，不再按直径拍一个时间常数。
            // 功率取上一帧解出来的值：这一帧的解算要先知道转速，转速要先知道功率差——
            // 一帧的滞后，30 Hz 下 33 ms，肉眼看不出来。
            if (_spoolLaw == null)
            {
                _spoolLaw = new JetForge.Core.SpoolDynamics.Law();
                try
                {
                    _spoolLaw.Inertia = JetForge.Core.SpoolDynamics.Inertia(spoolD);
                    _spoolLaw.OmegaDesign = JetForge.Core.SpoolDynamics.DesignOmega(spoolD);
                }
                catch { _spoolLaw = null; }
            }
            if (_spoolLaw != null) _spoolLaw.W0 = _lastWComp;
            double tauUpNow = JetForge.Core.SpoolDynamics.TauUp(spoolD, _surplusW);
            double tauDownNow = JetForge.Core.SpoolDynamics.TauDown(tauUpNow);

            // ---- 控制系统：在玩家的油门之外，再按当下的转速、涡轮前温度、喘振裕度决定能烧多少油 ----
            // 用的是上一帧的读数（真机的传感器也永远比指令慢一帧），Command 跨帧保存，用来做加速度限幅。
            // 无控的机器这一步就是原样透传——它的油门是"烧多少油"，不是"要多快"。
            JetForge.Core.ControlKind ctlKind = JetForge.Core.ControlSystem.Effective(spoolD);
            double thrCtl = JetForge.Core.ControlSystem.Step(
                _control, ctlKind, currentThrottle, _spoolThr, _lastNPct, _lastT4,
                spoolD.Tech != null ? spoolD.Tech.T4Limit : 1600.0,
                _risk != null ? _risk.Surge : 0.0, spoolDt);
            readControl = JetForge.Core.ControlSystem.KindName(ctlKind) + "｜" + _control.Note;

            // ---- 慢车供油不足：判定必须发生在"解算之前" ----
            // 旧版在解算之后才拦：这一帧先按"还在转"解出 10 kN，再把它抹成 0 然后 return。
            // 于是转速一个数都没往下走（FlightStep 拿到的 flameout 一直是 false），
            // 推力却每帧跳一次——玩家看到的就是"转速不降、推力突变"。
            // 判定用的命令值从 thrCtl 就地算：它进 FlightStep 之前要被抬底到 InputFloor，
            // 而"是不是到底"只能按抬底后的数说，不能自己拍一个 0.03。
            // ---- 慢车供油不足：**不再判熄火** ----
            //
            // 以前这里是"杆到底 + 慢车油气比算不出来 → 判熄火"，那是替物理做决定：
            // 一帧之内把吹熄这件事从连续过程压成了一个跳变。玩家看到的正是
            // "转速不降、推力突变"——因为转速那一路根本没被允许往下走。
            //
            // 现在杆位就是油量，油量低于最低需求时 <see cref="JetForge.Core.FuelLoop"/>
            // 反查出的目标转速会低过当前转速，转子按惯量一路往下溜，直到风车转速。
            // 停车是这条曲线自己走到头的结果，不是任何一行判据按出来的。
            bool idleStarvedNow = false;
            if (!EngineIgnited) _idleStarved = false;
            // ---- 没有进气道就不吸气 ----
            // 以前 `_install` 只影响预解网格（可用流量封顶），飞行时进气道不在场也照样按设计流量解出推力，
            // 于是"什么都不装也能飞"。物理上没有进气就没有质量流，烧不出推力——这条不是判据，是边界条件。
            bool noIntakeNow = IntakeMissingNow();
            bool flameoutNow = flameout || idleStarvedNow || noIntakeNow;

            // ---- 空中再起动窗口（§179） ----
            //
            // 熄火之后"按一下就能着"是原版的行为，真机不是这样：
            // 转速太低带不动、太高会富油超温、空气太稀点火器打不着。
            // 这里只做两件事，都不替玩家做决定：
            //   ① 点火窗口之外，把火重新判灭并说清楚为什么（不再"啪一下着了"）；
            //   ② 数字机有自动再点火：熄火后自己重试，窗口内隔一小会儿恢复。
            // 机械/液压/模拟档没有 ②——真机上它们也没有，得飞行员自己按。
            if (vessel != null && !vessel.LandedOrSplashed && Design != null && EngineIgnited && flameoutNow)
            {
                ControlKind ctlNow = JetForge.Core.ControlSystem.Effective(Design);
                JetForge.Core.RelightResult rel = JetForge.Core.Relight.Window(Design, alt, mach, -1.0);
                if (!rel.CanLight)
                {
                    // 窗口外：火判灭，理由只在状态变了的时候说一次。
                    if (!_relightBlocked || _relightReason != rel.Reason)
                    {
                        _relightBlocked = true;
                        _relightReason = rel.Reason;
                        SetState(Loc.T("点不着：", "will not light: ") + rel.Reason);
                    }
                    flameout = true;
                    flameoutNow = true;
                }
                else if (_relightBlocked)
                {
                    _relightBlocked = false;
                    _relightReason = "";
                    SetState(Loc.T("转速与进气回到再点火窗口里，可以重新点火", "spool and inlet are back inside the relight window"));
                }

                if (JetForge.Core.Relight.AutoRelight(ctlNow) && rel.CanLight && flameout)
                {
                    if (_relightFrame <= 0) _relightFrame = Time.frameCount + 90;
                    else if (Time.frameCount >= _relightFrame)
                    {
                        _relightFrame = 0;
                        flameout = false;
                        flameoutNow = false;
                        SetState(Loc.T("数字控制自动再点火：转速 ", "FADEC auto-relight: spool ") + (rel.Spool * 100.0).ToString("F0")
                                 + Loc.T("%、进气 ", "%, inlet ") + rel.FacePressureKPa.ToString("F1") + Loc.T(" kPa 都在窗口里", " kPa both inside the window"));
                    }
                }
                else _relightFrame = 0;
            }
            else _relightFrame = 0;

            // ---- 油量控制：杆位不是转速，是油量 ----
            //
            // 以前杆位直接当转速目标，"慢车"只能用"推力占满油门多少"这种间接口径表达，
            // 于是玩家说不出"我就给这么多油"。现在这一层把杆位翻译成喷油量：
            //   无控制系统 → 杆位直给油量（杆到底 = 慢车油量，不是 0）
            //   有 FADEC   → 杆位是目标推力，控制器自己找油量
            // 然后把油量交给 <see cref="JetForge.Core.FuelSchedule"/> 反查"烧这点油平衡在哪个转速"。
            // 关键：**目标转速低于当前转速时，转子就按惯量往下溜，没有第二条判据**。
            // 油少到维持不了任何转速时反查返回 0，转速一路掉到风车——那是解算器算出来的结果。
            try
            {
                bool rebuild = _fuelSched == null || !_fuelSched.Valid
                    || double.IsNaN(_fuelSchedAlt)
                    || Math.Abs(alt - _fuelSchedAlt) > 500.0
                    || Math.Abs(mach - _fuelSchedMach) > 0.05
                    || Math.Abs(_abFrac - _fuelSchedAb) > 0.10;
                // 建表失败时别每帧重试：9 个点就是 9 次整解，解不出来说明这台机器现在
                // 哪个马赫都算不动，隔一秒再试一次就够了。
                if (rebuild && Time.frameCount < _fuelSchedRetryFrame) rebuild = false;
                if (rebuild && Sampler != null)
                {
                    _fuelSched = JetForge.Core.FuelSchedule.Build(Sampler, alt, mach, _abFrac);
                    _fuelSchedAlt = alt;
                    _fuelSchedMach = mach;
                    _fuelSchedAb = _abFrac;
                    if (_fuelSched == null || !_fuelSched.Valid)
                        _fuelSchedRetryFrame = Time.frameCount + 30;
                }
                if (_fuelSched != null && _fuelSched.Valid && _fuelSched.FullThrust > 1.0)
                {
                    // 有控 = 杆位是目标推力，由控制器决定烧多少油（§270 的口径）。
                    // 液压系统也够格：它读得出推力偏差（转速＋涡轮前温度＋喷口压比足够反推），
                    // 只是限幅慢、不认喘振裕度——那些差异交给 ControlSystem 那几道闸。
                    // 纯机械调速器没有推力传感器，它只认"转速到了没有"，所以不闭环。
                    JetForge.Core.ControlKind effKind = JetForge.Core.ControlSystem.Effective(Design);
                    _fuelLoop.Fadec = JetForge.Core.ControlSystem.Rank(effKind)
                                      >= JetForge.Core.ControlSystem.Rank(JetForge.Core.ControlKind.Hydro);
                    _fuelLoop.IdleFuel = Consts != null ? Consts.IdleFuelFlow : 0.0;
                    _fuelLoop.MaxFuel = _fuelSched.MaxFuel;
                    // 重置的时机：换设计/换表重建时别把 FADEC 的积分带过去，
                    // 但也不要每帧重置——那等于没有积分。
                    if (!_fuelLoopInit || rebuild)
                    {
                        _fuelLoop.Reset();
                        _fuelLoopInit = true;
                    }
                    // 杆位在 FuelLoop 里映射成推力目标（慢车推力 → 满推力），这里只给满推力作归一化用。
                    // 限温/限转/喘振闸这一帧在压油门时，积分不再往加大的方向攒（防积分饱和）。
                    bool limiterNow = _control.LimitingTemp || _control.LimitingSpeed || _control.LimitingSurge;
                    thrCtl = _fuelLoop.Step(thrCtl, spoolDt, _fuelSched,
                        0.0, _lastThrustN, _fuelSched.FullThrust * _designDerate, limiterNow);
                }
            }
            // 控制器抛异常时不能静默：这一帧会沿用上一帧的油量继续飞，玩家看到的是
            // "油门推了没反应"。每台机只报一次，避免每帧刷屏把日志挤爆。
            catch (Exception e)
            {
                if (!_ctlErrorLogged)
                {
                    _ctlErrorLogged = true;
                    Debug.LogWarning("[JetForge] 油量控制那一步没算成，本帧沿用上次油量：" + e.GetType().Name + "：" + e.Message);
                }
            }

            FlightStepResult r = FlightStep.Compute(
                Sampler, thrCtl, spoolDt, alt, mach,
                EngineIgnited, flameoutNow, SolverOk(),
                UseSimple ? SimplePoint.SelfSustaining : DesignPoint.SelfSustaining,
                ref _spoolThr, ref _spoolInit,
                tauUpNow, tauDownNow,
                WindmillArea(), DpSfc(),
                _abState, abTarget, abNow.Lit, _spoolLaw);

            // 这一帧解出来的剩余功率与压气机功，留给下一帧定加速的快慢。
            _surplusW = r.Sample != null ? r.Sample.SurplusW : 0.0;
            _lastWComp = r.Sample != null ? r.Sample.WComp : 0.0;
            _lastThrustN = double.IsNaN(r.ThrustN) ? 0.0 : r.ThrustN;

            // 状态栏那一行：现在这位玩家能不能看见"我们在现算"以及算得多快。
            readSolver = Sampler != null ? Sampler.Describe() : Loc.T("没有解算器", "no solver");

            // 原版拿 currentThrottle 乘我们喂的 maxFuelFlow。把它对齐到抬底后的命令值，
            // 桥接的分母与分子才是同一个数（飞行中两者本来就相等，这里只是把话说明白）。
            currentThrottle = (float)r.Command;
            _windmill = r.Windmill;
            _abFrac = r.AbFrac;

            // 慢车喷油过低 → 真的熄火（Flyout 的手感）。
            //
            // 物理上只有一条：**慢车供的那点油留不住火**。表现分两种——
            // ① 玩家设的推力占比低于这台机器能稳住的最低档（`IdleClamped`）：
            //    求解器会把 T4 停在最低可维持的那个温度（J-1 是 816 K / 12.8% 推力）。
            //    玩家以为"设 2% 就是 2%"，其实是我们偷偷多给了油。真机不是这样：
            //    你给 2% 的油，火就灭给你看。
            // ② 反解出来的慢车油气比低于贫油下限：直接吹熄。
            //
            // 旧版这一段带 `&& r.Running`——它要求"这一帧还烧得着"，而油门到底烧不着
            // 正是我们要抓的现象，条件自相矛盾，于是永远不成立，玩家什么都没看到。
            //
            // 还要记住自己（`_idleStarved`）：原版每帧都有机会把 `flameout` 清掉，
            // 而状态栏会被后面的"正常"分支覆盖——不锁存就等于没做。
            // 熄火原因记在锁存上：解算已经按"灭了"走过一遍，这里只负责说清为什么。
            if (idleStarvedNow && r.Coasting)
            {
                _idleNote = Consts != null && Consts.IdleClamped
                    ? string.Format(Loc.T("慢车熄火：设定 {0:P0} 这一档这台机器烧不住（能稳住的最低档比它高）", "idle flameout: this engine cannot hold {0:P0} (the lowest stable notch is higher)"),
                                    Design.IdleThrustFrac)
                    : string.Format(Loc.T("慢车熄火：慢车油气比 {0:F4} 低于贫油下限 {1:F4}｜把慢车推力占比调高", "idle flameout: idle FAR {0:F4} is below the lean limit {1:F4} | raise the idle thrust fraction"),
                                    Consts != null ? Consts.IdleFar : 0.0,
                                    Consts != null ? Consts.LeanFar : 0.0);
            }

            // 音效跟物理量走：油门定"有没有气"、N2 定啸叫音调、加力比例多加一层轰鸣。
            // 这里不再直接出声——出声的那一下挪到读数之后，用抹平过的值（见下面 UpdateSound 那段）。

            if (!r.Running)
            {
                finalThrust = 0f;
                resultingThrust = 0f;
                readThrust = 0f;

                // ---- 惯性停车：火灭了，转子还带着劲往下溜 ----
                // 真机从满转速掉到风车要十几秒，这期间仪表在动、声音在往下沉、
                // 而油门推到底也没用（没有火），只能重新点火。
                if (r.Coasting)
                {
                    ApplyWindmillDrag();
                    _surge.Reset();
                    _lastThrottleSurge = -1.0;
                    readSurge = Loc.T("已停车", "shut down");

                    // 残余推力：进惰转的第一帧记住"熄火前那一帧"的推力、油耗与转速，
                    // 之后按 (当前转速 / 熄火转速)² 折下去——推力不再是"啪一下没了"，
                    // 而是跟着转速表一起往下溜。燃料在这里其实已经断了，我们仍然喂一小股
                    // 衰减中的油——因为 KSP 的推力只能由「油量 × Isp × g」算出来。
                    if (!_wasCoasting)
                    {
                        _coastThrust0KN = _lastOutKN;
                        _coastFuel0Tps = _lastOutFuelTps;
                        _coastSpoolAtCut = Math.Max(r.Spool, JetForge.Core.CoastThrust.MinSpoolFraction);
                        _coastThrustKN = _coastThrust0KN;
                        _coastFuelTps = _coastFuel0Tps;
                        _wasCoasting = true;
                    }
                    _coastThrustKN = JetForge.Core.CoastThrust.Slide(_coastThrust0KN, _coastSpoolAtCut, r.Spool);
                    _coastFuelTps = _coastThrustKN > 0.0
                        ? JetForge.Core.CoastThrust.SlideFuel(_coastFuel0Tps, _coastSpoolAtCut, r.Spool)
                        : 0.0;

                    finalThrust = (float)_coastThrustKN;
                    resultingThrust = (float)(_coastThrustKN * 1000.0);
                    readThrust = (float)_coastThrustKN;
                    readN1 = (float)_lagN1.Step(r.N1, spoolDt, 0.30);
                    readN2 = (float)_lagN2.Step(r.N2, spoolDt, 0.30);
                    readN3 = (float)_lagN3.Step(r.N3, spoolDt, 0.30);
                    readEgt = (float)_lagEgt.Step(r.Egt, spoolDt, 0.60);
                    readMassFlow = (float)_lagMdot.Step(r.MassFlow, spoolDt, 0.30);
                    readFuelFlow = (float)(_coastFuelTps * 1000.0);
                    readSfc = 0f;
                    readWindmill = r.Windmill.Valid && r.Windmill.DragN > 1.0
                        ? string.Format(Loc.T("{0:F2} kN（风车 N2 {1:F0}%）", "{0:F2} kN (windmilling, N2 {1:F0}%)"), r.Windmill.DragN / 1000.0, r.Windmill.N2Pct)
                        : "";
                    ApplyStockBridge(_coastThrustKN, _coastFuelTps, r.Command);
                    // 声音跟着转子走：音量随转速往下掉，而不是切掉。
                    UpdateSound(r.Spool, r.Spool, 0.0, false, alt);
                    UpdatePlume(0.0, _coastThrustKN * 1000.0, r.Sample, 0.0);
                    SetState(string.Format(Loc.T("{0}转速 {1:F0}%（在掉，掉到风车才停）｜油门推不回来了，要重新点火", "{0}spool {1:F0}% (decaying, stops at windmill) | throttle will not bring it back, relight required"),
                        string.IsNullOrEmpty(_idleNote) ? Loc.T("停车惯性：", "coasting down: ") : _idleNote + "｜",
                        Math.Max(readN2, readN1)));
                    return;
                }

                if (r.Mode != FlightMode.NotSustaining)
                {
                    // 关车/熄火：转子停在"被气流吹着转"的那一档（再点火从这里往上爬），
                    // 同时把风车阻力真的加到刚体上——关掉发动机的飞机应该掉得更快。
                    ApplyWindmillDrag();
                    // 停车了就别留着上一秒的喘振读数：状态栏写"喘振中"而推力是 0，玩家会以为卡了。
                    _surge.Reset();
                    _lastThrottleSurge = -1.0;
                    readSurge = Loc.T("已停车", "shut down");
                    if (r.Windmill.Valid && r.Windmill.DragN > 1.0)
                    {
                        readWindmill = string.Format(Loc.T("{0:F2} kN（N2 {1:F0}%）", "{0:F2} kN (N2 {1:F0}%)"),
                            r.Windmill.DragN / 1000.0, r.Windmill.N2Pct);
                        SetState(r.Mode == FlightMode.NotIgnited ? Loc.T("未点火（风车）", "not lit (windmilling)") : Loc.T("熄火（风车）", "flameout (windmilling)"));
                    }
                    else
                    {
                        readWindmill = "";
                        if (noIntakeNow) SetState(Loc.T("缺进气道（不吸气，点不着）", "no intake (no airflow, will not light)"));
                        else SetState(r.Mode == FlightMode.NotIgnited ? Loc.T("未点火", "not lit") : Loc.T("熄火", "flameout"));
                    }
                    ApplyStockBridge(0.0, 0.0, r.Command);
                    // 关车必须让声音跟着停。以前这个分支直接 return，出声那一行在下面，
                    // 于是 AudioSource 保持着最后一次的音量——停车了还在响。
                    UpdateSound(0.0, 0.0, 0.0, false, alt);
                    return;
                }

                // 点着了、也有进气，但这一状态烧不出推力。三种原因要说清楚：
                // 设计本身不成立、这个高度/速度烧不住、还是根本没点火。
                // 玩家看到的都是推力 0，但该改的东西完全不同。
                readWindmill = "";
                string why = FirstError();
                if (!string.IsNullOrEmpty(why)) SetState(Loc.T("设计不成立：", "design does not close: ") + why);
                else SetState(Loc.T("此状态烧不出推力（风车/停车）", "No thrust in this state (windmilling or shut down)"));
                ApplyStockBridge(0.0, 0.0, r.Command);
                UpdateSound(0.0, 0.0, 0.0, false, alt);
            }
            else
            {
                readWindmill = "";
                string abTxt = AfterburnerTransient.Describe(_abState);
                if (abBurning && _abState.Phase == AbPhase.Lighting) SetState(Loc.T("正常（", "OK (") + abTxt + "）");
                else SetState(abBurning ? Loc.T("正常（加力）", "OK (afterburner)")
                    : (abWanted && abNow.Status != AbLightStatus.Off ? Loc.T("加力点不着：", "afterburner will not light: ") + abNow.Note
                       : (_abState.Phase == AbPhase.Extinguishing ? Loc.T("正常（", "OK (") + abTxt + "）" : Loc.T("正常", "OK"))));
            }

            PerformanceGrid.Sample s = r.Sample;
            _sample = s;
            double thrustN = r.ThrustN;
            double thr = r.Throttle;

            // 缺陷兑现：判据说"级负荷过高"，这里就该听见喘振；说"超温"，这里就该开始烧涡轮。
            // 放在查表之后、桥接之前——推力与油耗必须是同一套数，否则 SFC 会自相矛盾。
            double t4Lim = (Design != null && Design.Tech != null) ? Design.Tech.T4Limit : 1200.0;
            double far = s.MassFlow > 1e-6 ? (r.FuelTps * 1000.0) / s.MassFlow : 0.0;
            // 过载也算进去：大幅机动（回杆、拉起）是"用坏了"的一条独立路径，
            // 与判据无关——一台判据全绿的好机器，连续 12 g 回杆也一样会断。
            double gNow = 0.0;
            try { gNow = vessel != null ? vessel.geeForce : 0.0; }
            // 探测豁免：拿不到过载就当 0——损伤模型里"没有 g 数据"与"没拉过载"后果相同，
            // 这里不值得为一次容器 API 失败编一个数。
            catch (Exception) { }
            Defects.Step(CurrentRisk(), _defect, spoolDt, thr, s.Egt, t4Lim, far, alt, gNow, _surge.Active);

            // 同一个缺陷只罚一次：设计期判据 FAIL 已经给了 ×0.78 的降额，
            // 运行期同一个缺陷又被记成"风险"再罚一遍，玩家算不出推力为什么只剩四成。
            // 判据不成立时先落台账（设计警告），再看运行期有没有同码的风险在罚——
            // 有的话就放过设计期那次降额，留着运行期这条更具体、会自己停的。
            if (_defect.Book != null)
            {
                _defect.Book.BookFindings(LastReport);
                string overlap;
                if (_designDerate < 1.0 && _defect.Book.DesignOverlapsRuntime(LastReport, out overlap))
                {
                    _designDerate = 1.0;
                    readDefectDedup = Loc.T("同一缺陷已按运行风险罚过：", "same defect already counted as a runtime risk: ") + overlap;
                }
                else readDefectDedup = "";
            }

            // 喷油量 ↔ 进气量 ↔ 状态：油气比是这三件事的共同刻度。
            // 低了吹熄（火焰站不住），高了富油（烧不完、冒黑烟、涡轮挨烤）。
            if (Consts != null && s.MassFlow > 1e-6)
            {
                readMix = string.Format(Loc.T("{0:F4}｜贫油 {1:F4} 富油 {2:F3}", "{0:F4} | lean {1:F4} rich {2:F3}"),
                                        far, Consts.LeanFar, Consts.RichFar);
                bool idleBand = currentThrottle < 0.05f;
                if (!_defect.Failed && !flameout && EngineIgnited
                    && far > 0.0 && far < Consts.LeanFar && !idleBand)
                {
                    // 空中贫油熄火：不是慢车那一档的"设定太低"，是这一手油门配这点进气量
                    // 根本留不住火。给玩家留一句能照着改的话。
                    flameout = true;
                    SetState(string.Format(Loc.T("贫油熄火：油气比 {0:F4} 低于下限 {1:F4}（进气 {2:F1} kg/s，喷油 {3:F4} kg/s）", "lean flameout: FAR {0:F4} is below the limit {1:F4} (air {2:F1} kg/s, fuel {3:F4} kg/s)"),
                        far, Consts.LeanFar, s.MassFlow, r.FuelTps));
                }
                else if (!_defect.Failed && !flameout && EngineIgnited && far > Consts.RichFar)
                {
                    // 富油不立刻熄火，但火焰出了燃烧室还在烧——这一步交给热损伤去收拾。
                    _defect.Damage = Math.Min(1.0, _defect.Damage + spoolDt * 0.02);
                }
            }

            // 判据不成立：降额，不是断电。油耗不变、推力打折 → SFC 跟着涨，
            // 玩家在面板上看到的是"这机器能飞，但亏"。
            if (_designDerate < 0.999 && thrustN > 1.0)
            {
                double fuelKgD = thrustN * r.Sfc / 3600.0;
                thrustN *= _designDerate;
                r.Sfc = thrustN > 1.0 ? fuelKgD * 3600.0 / thrustN : 0.0;
                r.FuelTps = fuelKgD / 1000.0;
            }

            if (_defect.ThrustScale != 1.0 || _defect.FuelScale != 1.0)
            {
                double fuelKg = thrustN * r.Sfc / 3600.0 * _defect.FuelScale;
                thrustN *= _defect.ThrustScale;
                r.Sfc = thrustN > 1.0 ? fuelKg * 3600.0 / thrustN : 0.0;
                r.FuelTps = fuelKg / 1000.0;
            }
            if (_defect.JustBlownOut)
            {
                // 贫油熄火：真的把原版的 flameout 打开，玩家得自己重新点火。
                flameout = true;
            }
            if (_defect.Failed)
            {
                // 故障与熄火不是一回事：熄火能重点，故障这一趟到头了。
                // 仍然走 flameout 字段——原版据此把推力清零，这是唯一该清零的场合。
                flameout = true;
                SetState(Loc.T("故障：", "fault: ") + _defect.FailNote);
            }
            readDefect = Defects.Describe(_risk, _defect);
            if (_defect.Book != null && _defect.Book.Count > 0)
            {
                readFaultBook = _defect.Book.Describe(4);
                if (readDefectDedup.Length > 0) readFaultBook += "｜" + readDefectDedup;
            }
            else readFaultBook = "";
            readDamage = (float)(_defect.Damage * 100.0);
            if (_defect.State != Loc.T("正常", "OK")) SetState(_defect.State);

            // 喘振：与上面那层分工不同——上面是"设计里带着的病"，这里是"这一手油门逼出来的"。
            // 猛推油门时命令跑在转速前面（thr - _spoolThr），压比先上去流量没跟上，就是喘振的入口；
            // 超音速再来一笔进气畸变——这个数现在由进气道自己给（含管道弯曲），不再按马赫估。
            // 设计裕度不够的机器，同一手油门余量更少。
            double surgeRate = _lastThrottleSurge < 0.0 ? 0.0 : Surge.Rate(_lastThrottleSurge, thr, spoolDt);
            _lastThrottleSurge = thr;
            Surge.Step(_surge, thr, surgeRate, _spoolThr, mach, _risk != null ? _risk.Surge : 0.0, spoolDt,
                       IntakeSolver.DistortionOf(Design, mach));
            if (_surge.ThrustFactor < 1.0)
            {
                // 喘振不掉油：燃烧没停，推力没了 → 油耗相对暴涨，这才是真机上 SFC 尖起来的样子。
                double fuelKgS = thrustN * r.Sfc / 3600.0;
                thrustN *= _surge.ThrustFactor;
                r.Sfc = thrustN > 1.0 ? fuelKgS * 3600.0 / thrustN : 0.0;
                r.FuelTps = fuelKgS / 1000.0;
            }
            if (_surge.EgtKick > 0.0) s.Egt += _surge.EgtKick;
            readSurge = Surge.Short(_surge);
            if (_surge.Active) SetState(Loc.T("喘振（压气机失速）", "surge (compressor stall)"));

            // 把这一帧的温度与转速留给下一帧的控制器。传感器的读数永远比指令慢一拍，
            // 所以控制律读到的必须是"上一帧解出来的结果"，不能拿这一帧的去控制这一帧——那是作弊。
            _lastT4 = s.T4;
            _lastNPct = Math.Max(readN1, Math.Max(readN2, readN3));

            // 输出抹平。放在缺陷与喘振之后、写进原版之前：
            // 玩家看见的、听见的、飞机拿到的，都必须是同一个"抹平过"的数。
            // 油耗与推力一起抹（SFC 是这两个数相除，只抹一个会让油耗凭空变），
            // N1~N3/EGT/流量同理——它们本来就是同一时刻的同一批读数。
            double thrustOut = _lagThrust.Step(thrustN, spoolDt, OutputTau);
            double fuelOutTps = _lagFuel.Step(r.FuelTps * 1000.0, spoolDt, OutputTau) / 1000.0;
            double sfcOut = thrustOut > 1.0 ? fuelOutTps * 1000.0 * 3600.0 / thrustOut : 0.0;
            double thrOut = _lagThr.Step(thr, spoolDt, 0.12);
            double abOut = _lagAb.Step(_abFrac, spoolDt, 0.12);

            // 求解器内部一律用牛顿，KSP 这边一律用千牛。这条边界只在这里跨一次。
            finalThrust = (float)(thrustOut / 1000.0);
            resultingThrust = finalThrust;

            // 记住这一帧交出去的推力与油耗：突发熄火时惰转段的衰减就从这个值起步。
            _lastOutKN = thrustOut / 1000.0;
            _lastOutFuelTps = fuelOutTps;
            if (_wasCoasting)
            {
                // 重新点火成功（或从惰转里恢复）：残余推力与熄火原因都清掉，
                // 不然下一次熄火会从上一次的残值接着往下溜。
                _wasCoasting = false;
                _coastThrustKN = 0.0;
                _coastFuelTps = 0.0;
                _idleNote = "";
            }
            // 原版用 maxThrust 做面板显示与 dv 估算，给它「当前状态下满油门」的值，
            // 这样大气层里推重比不会显示成海平面那个数。maxThrust 同样是千牛。
            // 满油门推力：向解算器再要一次。实时解算下这一问走的是缓存，
            // 同一次 Solve 的容差（油门 0.01）刚好覆盖 1.0 与当前油门之间的差距以外的情况，
            // 所以一次额外 Solve 每秒最多多十几次——可以接受，换来的是 maxThrust 不再骗人。
            double maxNowN = thrustN;
            if (s.Valid && Sampler != null)
            {
                PerformanceGrid.Sample top = Sampler.Sample(1.0, alt, mach, r.AbFrac, r.AbAreaFrac);
                if (top.Valid && top.Thrust > 1.0) maxNowN = top.Thrust;
            }
            maxNowN = _lagMax.Step(maxNowN, spoolDt, OutputTau);
            if (maxNowN > 1.0) maxThrust = (float)(maxNowN / 1000.0);

            // 油耗：反汇编 ModuleEngines 之后才知道，原版根本不用我们写的 finalThrust——
            // ThrustUpdate 每帧都会拿 requestedMassFlow × realIsp × g 重算一遍并覆盖。
            // 所以这里必须反过来喂它：把我们的推力与燃油流量翻译成 maxFuelFlow 与 Isp。
            // 这一帧网格算出来的推力（含缺陷降额之后）。飞行探针以前打的是 DpThrust()——
            // 那是海平面静止的设计点推力，跟飞行中的值差着好几倍，被当成"推力只有模型的
            // 43%"查过一回。量错东西比没量更贵。
            modelThrustN = thrustOut;

            ApplyStockBridge(thrustOut / 1000.0, fuelOutTps, r.Command);

            readThrust = (float)(thrustOut / 1000.0);
            readSfc = (float)sfcOut;
            readMassFlow = (float)_lagMdot.Step(s.MassFlow, spoolDt, OutputTau);
            readFuelFlow = (float)(sfcOut > 0.0 ? thrustOut * sfcOut / 3600.0 : 0.0);
            readN1 = (float)_lagN1.Step(s.N1, spoolDt, OutputTau);
            readN2 = (float)_lagN2.Step(s.N2, spoolDt, OutputTau);
            readN3 = (float)_lagN3.Step(s.N3, spoolDt, OutputTau);
            readEgt = (float)_lagEgt.Step(s.Egt, spoolDt, OutputTau);

            // 音效在这里出声：读的是抹平过的油门/转速/加力比例。
            // 以前直接喂瞬时值，于是每帧的音量与音高都在跳，听感是"沙沙的抖"。
            UpdateSound(thrOut, _spoolThr, abOut, r.Running, alt);

            UpdatePlume(thrOut, thrustOut, s, abOut);

            // 可变喷管的花瓣跟着油门/加力张开或收拢；装了矢量喷口时还会跟着飞控歪。
            // 重建是有缓存的重建（开度与偏角都没变就直接返回），每帧调不会重算网格。
            UpdateVectoring(spoolDt, thrOut, abOut);
        }

        /// <summary>
        /// 矢量喷口：每帧把飞控指令变成"推力往哪偏"，同时把花瓣一起歪过去。
        ///
        /// 力方向由 <see cref="ThrustVector"/> 用 τ = r × F 解出来——只要知道施力点
        /// 相对质心的位置和控制轴，倒装、侧装都不用改符号。这里只做三件事：
        /// 取指令、转成世界方向、写回施力体与花瓣。
        ///
        /// 偏转只改方向不改大小：偏 15° 轴向推力少 3.4%、侧向多 26%，
        /// 这部分由原版施力自己产生（它施的是 -forward × finalThrust）。
        /// </summary>
        /// <summary>最近一次算出的换算转速（N/√θ，0~1 量级）。喷管花瓣与求解器共用它。</summary>
        private bool _ctlErrorLogged;

        public double ConvertedSpeedNow
        {
            get { return _ncVis > 0.0 ? _ncVis : 1.0; }
        }

        private double _ncVis = 1.0;

        /// <summary>
        /// 原版 GetCurrentThrust/GetMaxThrust 在 1.12 里被 sealed 掉了，改不了。
        /// 好在它们内部读的就是 finalThrust / maxThrust 这两个字段，所以只要在
        /// UpdateThrottle 里把它们写好，推力、UI 读数、dv 估算就都跟着模型走。
        /// 这两个属性供设计器和外部代码取用。
        /// </summary>
        public float ModelCurrentThrust { get { return finalThrust; } }

        // ---------- 判据 ----------

        // ---------- 交互 ----------

        // ---------- 环境量 ----------

    }
}