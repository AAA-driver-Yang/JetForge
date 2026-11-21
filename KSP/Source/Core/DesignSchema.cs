using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace JetForge.Core
{
    /// <summary>编辑器要画成什么控件。可视化编辑器只认这几种，别每加一个字段就加一种控件。</summary>
    public enum FieldKind
    {
        Number = 0,
        Integer = 1,
        Toggle = 2,
        Choice = 3,
        Text = 4,
    }

    /// <summary>
    /// 一个可编辑字段的元数据：它在哪一页、叫什么名、什么单位、范围多大、画成什么。
    ///
    /// `Path` 是唯一标识（如 `Fan.Diameter`、`Hpc.Stages`），可视化编辑器用它回写字段；
    /// 现在设计器窗口里那些滑杆也是这套路径，只是硬编码在 `DrawXxx()` 里。
    /// </summary>
    public sealed class FieldSpec
    {
        public string Path;
        public string Group;
        public string Label;
        public string Unit;
        public FieldKind Kind;
        public double Min;
        public double Max;
        public double Step;
        public string Hint;
        /// <summary>
        /// 这个选项字段对应的枚举类型。以前这里是 `string[] Choices`——一份和枚举平行的手写名单。
        /// 那份名单是第二份真相：`NozzleKind` 的枚举顺序是 Converging/Variable/ConvergingDiverging，
        /// 名单却写成了 Converging/ConvergingDiverging/Variable。谁要是按名单下标去解释数值，
        /// 就会把"可变"读成"收扩"，而且编译、闸门、回归全都不会响。
        /// 现在只留类型，个数由 `Enum.GetValues` 数出来，能翻的措辞留在界面那一侧。
        /// </summary>
        public Type ChoiceType;

        public FieldSpec(string path, string group, string label, FieldKind kind,
                         double min, double max, double step, string unit, string hint)
        {
            Path = path; Group = group; Label = label; Kind = kind;
            Min = min; Max = max; Step = step; Unit = unit; Hint = hint;
        }
    }

    /// <summary>
    /// 设计参数的元数据层。
    ///
    /// 为什么值得单独一层：设计器现在是"每个标签页手写一遍滑杆"，加一个字段要改三处
    /// （结构体、存档、界面），而**存档漏字段不会报错**——`Burner.LinerLd` 就是这么丢过一轮的。
    /// 有了这张表，界面可以从表里自动铺，存档漏抄能靠自检对账，将来做可视化编辑器
    /// （点模型上的零件改参数）也只需要认路径。
    ///
    /// 另一条职责是 `Uncovered()`：反射扫 `EngineDesignData` 的公有字段，把**没有元数据**的
    /// 列出来。可视化编辑器绝不能"表里没有就当不存在"——那样新字段会静默消失，
    /// 而玩家只会觉得"这个选项怎么没了"。
    /// </summary>
    public static class DesignSchema
    {
        private static List<FieldSpec> _all;

        public static List<FieldSpec> All()
        {
            if (_all != null) return _all;
            List<FieldSpec> o = new List<FieldSpec>();

            // ---- 整机 ----
            o.Add(N("ShaftCount", Loc.T("整机", "Whole engine"), Loc.T("轴数", "Shafts"), 1, 3, 1, "", Loc.T("1 单轴 / 2 双轴 / 3 三轴。多一根轴就多一组涡轮-压气机配对", "1 single spool / 2 twin / 3 triple. Each extra shaft pairs another turbine with a compressor.")));
            o.Add(N("DesignMach", Loc.T("整机", "Whole engine"), Loc.T("设计马赫", "Design Mach"), 0.0, 3.0, 0.01, "Ma", Loc.T("设计点飞行马赫。它只通过冲压总压/总温进循环，不当迎面马赫用", "Flight Mach at the design point. It only enters the cycle through ram total pressure/temperature; it is not the face Mach.")));
            o.Add(N("DesignAlt", Loc.T("整机", "Whole engine"), Loc.T("设计高度", "Design altitude"), 0.0, 25000.0, 100.0, "m", Loc.T("设计点高度，决定环境压力温度", "Design-point altitude. Sets ambient pressure and temperature.")));
            o.Add(N("OverrideBpr", Loc.T("整机", "Whole engine"), Loc.T("涵道比覆盖", "Bypass ratio override"), 0.0, 12.0, 0.05, "", Loc.T("0 = 由几何反推；大于 0 时按这个数算", "0 = derive from geometry; above 0 = use this number as-is.")));
            o.Add(C("Kind", Loc.T("整机", "Whole engine"), Loc.T("发动机类型", "Engine type"), typeof(EngineKind), Loc.T("涡喷或涡扇。改这个会连带改涵道比的有效值", "Turbojet or turbofan. Changing this also changes the effective bypass ratio.")));
            o.Add(C("Nozzle", Loc.T("整机", "Whole engine"), Loc.T("喷管型式", "Nozzle type"), typeof(NozzleKind), Loc.T("收敛 / 收扩 / 可变。收扩只在落压比够高时才划算", "Converging / converging-diverging / variable. A C-D nozzle only pays off when the pressure ratio is high enough.")));
            o.Add(N("SoundGain", Loc.T("整机", "Whole engine"), Loc.T("音量倍率", "Volume scale"), 0.0, 2.0, 0.05, "×", Loc.T("只影响声音，不碰物理量", "Affects sound only; no physics.")));
            o.Add(T("ThrustVectoring", Loc.T("整机", "Whole engine"), Loc.T("矢量喷口", "Thrust vectoring"), Loc.T("喷口能跟着飞控偏转。只把推力换个方向，不增加大小；收敛喷管没有可动段", "The nozzle can follow flight controls. It turns thrust, it does not add any; a converging nozzle has no moving section.")));
            o.Add(N("GimbalMaxDeg", Loc.T("整机", "Whole engine"), Loc.T("最大偏转角", "Max deflection"), 0.0, 25.0, 1.0, "°", Loc.T("真机 15~20°。偏 15° 时轴向推力少 3.4%、侧向多 26%", "Real engines use 15-20 deg. At 15 deg you get 3.4% less axial and 26% more lateral thrust.")));
            o.Add(N("PetalCount", Loc.T("整机", "Whole engine"), Loc.T("花瓣数", "Petals"), 0.0, 32.0, 2.0, "片",
                Loc.T("0 = 自动 16 片（奇数会取偶）。真机 12~24 片：片少缝大漏气，片多每片太薄刚不住压差。只改外形与作动系统，喉道面积是气动的账，不随它动", "0 = 16 petals automatically (odd counts rounded to even). Real engines run 12-24: fewer petals leak through wider gaps, more petals are each too thin to hold the pressure difference. Shape and actuation only - throat area is the gas path's business.")));
            o.Add(N("PetalLengthFrac", Loc.T("整机", "Whole engine"), Loc.T("花瓣长度", "Petal length"), 0.6, 1.8, 0.05, "×",
                Loc.T("1.0 = 0.48×出口半径。叶片拉长能把同一喉道撑出更大的出口，代价是重量、根部弯矩与颤振风险", "1.0 = 0.48x the exit radius. Longer petals give a larger exit for the same throat, paid for in weight, root bending and flutter risk.")));
            o.Add(T("VectorPitch", Loc.T("整机", "Whole engine"), Loc.T("矢量·俯仰", "Vector · pitch"), Loc.T("俯仰跟着飞控偏。抖就把这一轴关掉，喷口只做偏航", "Pitch follows flight controls. Turn this axis off if it shakes; the nozzle then only yaws.")));
            o.Add(T("VectorYaw", Loc.T("整机", "Whole engine"), Loc.T("矢量·偏航", "Vector · yaw"), Loc.T("偏航跟着飞控偏。SAS 在这一轴上最吵，玩家报的抖动出在这里；有死区与限速率，实在不行就关掉", "Yaw follows flight controls. SAS is noisiest on this axis, which is where the reported shaking comes from; there is a deadband and a rate limit, and turning it off always works.")));
            o.Add(T("VectorRoll", Loc.T("整机", "Whole engine"), Loc.T("矢量·滚转", "Vector · roll"), Loc.T("绕推力轴滚。只有喷口不在质心中轴线上时才真滚得动（力臂不为零），装在轴线上的单发按了没反应", "Roll about the thrust axis. It only does something when the nozzle is off the mass-centre axis (non-zero moment arm); a single engine on the centreline will not respond.")));
            o.Add(C("Control", Loc.T("整机", "Whole engine"), Loc.T("控制系统", "Control system"), typeof(ControlKind),
                Loc.T("按年代解锁：1940s 只有无控，1960~70s 是液压，1990s 之后才有数字。油门在有控的机器上是「要多少推力」，在无控的机器上是「烧多少油」", "Unlocked by era: 1940s uncontrolled only, 1960-70s hydro-mechanical, digital from the 1990s. On a controlled engine the throttle means 'how much thrust'; on an uncontrolled one it means 'how much fuel'.")));
            o.Add(C("Detail", Loc.T("整机", "Whole engine"), Loc.T("细节等级", "Detail level"), typeof(DetailLevel),
                Loc.T("叶片排、静子、法兰、附件加多少。不参与气动，只影响三角数与观感；高档一台一万多三角，同一台机器摆二十个就要算这笔账", "How much blading, stators, flanges and accessories to add. Not part of the gas path; it only costs triangles and looks. A high-detail engine is over ten thousand triangles, so twenty of them is a real bill.")));

            // ---- 外形 ----
            o.Add(N("InletDiameter", Loc.T("外形", "External"), Loc.T("进气口径", "Inlet diameter"), 0.2, 2.5, 0.01, "m", Loc.T("没有风扇时用它算喉道面积", "Used for throat area when there is no fan.")));
            o.Add(N("CoreInletDiameter", Loc.T("外形", "External"), Loc.T("核心机进气口径", "Core inlet diameter"), 0.0, 1.5, 0.01, "m", Loc.T("0 = 按核心机直径推。它只影响外形，不碰性能", "0 = derived from the core diameter. Affects shape only, not performance.")));
            o.Add(N("NacelleDiameter", Loc.T("外形", "External"), Loc.T("短舱直径", "Nacelle diameter"), 0.0, 4.0, 0.01, "m",
                Loc.T("外壳最粗处的外径；0 = 按风扇罩再留一圈蒙皮。只改外形，不碰性能", "Widest point of the outer skin; 0 = the fan cowl plus skin. Shape only, never performance.")));
            // 整机长度**不是一个可设的项**：它由风扇/压气机级数、涡轮级数、燃烧室长度、
            // 加力段一起算出来（Core/Arch/EngineLength.cs）。这里只留一个逃生开关，不摆滑杆——
            // 摆出来就会有人去拧它，然后疑惑"为什么把级数改了推力没变"。
            o.Add(T("LengthAuto", Loc.T("外形", "External"), Loc.T("长度按部件自动算", "Length from parts"),
                Loc.T("缺省开。关掉之后长度改用存档里的手填值（Length）：那是给「真机多长我清楚」的老手留的口子。一般别关——手填的数字跟部件对不上，就会出现凭空多出来的管道，推力莫名其妙地掉一截",
                    "On by default. Off means you supply the length by hand, kept for people who know the real engine to the centimetre. Normally leave it on: a hand-typed length that disagrees with the parts leaves phantom ducting and quietly costs thrust.")));
            o.Add(T("ForceOwnBaseSound", Loc.T("外形", "External"), Loc.T("强制自带基础声", "Force own base sound"),
                Loc.T("RSE 在场时默认把基础声让给 RSE；打开它则两边都响（调试用，正常别开）", "With RSE present the base sound is normally handed over to it. Turn this on and both play (debug only; normally leave it off).")));
            o.Add(T("DesignName", Loc.T("整机", "Whole engine"), Loc.T("备注名", "Label"), Loc.T("只是名字，出现在存档与预设列表里", "A name only; it shows up in saves and the preset list.")));

            // ---- 结构强度 ----
            o.Add(T("GDamage", Loc.T("结构", "Structure"), Loc.T("过载损伤", "G damage"),
                Loc.T("关掉之后机动过载不再累积轴承应变（喘振/超温/超速照样会弄坏机器）。缺省开——真机的转子确实怕持续高 g",
                    "Off means manoeuvre g no longer accumulates bearing strain (surge, overtemperature and overspeed still break things). On by default - real rotors really do dislike sustained high g.")));
            o.Add(N("GReinforce", Loc.T("结构", "Structure"), Loc.T("加强系统", "Reinforcement"), 0.0, 1.0, 0.05, "",
                Loc.T("0 = 没有，1 = 满配。过载耐受 ×(1+r)，代价是结构重量 +12%×r：想拿高 g 机动就得付重量，想轻就得老实飞",
                    "0 = none, 1 = full. G tolerance x(1+r) for +12%xr structural mass: high-g manoeuvres cost weight, staying light means flying gently.")));

            // ---- 进气道：不给选项 ----
            //
            // 进气道现在是**独立零件**，口径/喉道/型式/迎角全在它自己的面板里改
            // （UI/JetForgeIntakeWindow.cs）。发动机设计器再留一份"自带进气道"就是同一个
            // 物理量两个真相：装了两件进气道时两边打架，装了一件时玩家改这边发现不起作用。
            // 因此这里整组字段从 schema 里删掉 —— 老存档里的 INTAKE 节点仍会被读进来，
            // 那是"一件进气道都没装"时的兜底口径，只是不再暴露给玩家调。

            // ---- 风扇 ----
            o.Add(N("Fan.Diameter", Loc.T("风扇", "Fan"), Loc.T("直径", "Diameter"), 0.2, 3.0, 0.01, "m", Loc.T("决定喉道面积与外形，也决定叶尖线速度", "Sets throat area and shape, and the tip speed.")));
            o.Add(N("Fan.Stages", Loc.T("风扇", "Fan"), Loc.T("级数", "Stages"), 0, 3, 1, "", Loc.T("0 = 涡喷（没有风扇）", "0 = turbojet (no fan).")));
            o.Add(N("Fan.HubTipRatio", Loc.T("风扇", "Fan"), Loc.T("轮毂比", "Hub/tip ratio"), 0.2, 0.75, 0.01, "", Loc.T("轮毂半径/叶尖半径。太小叶片根部装不下，太大气流通道窄", "Hub radius over tip radius. Too small and the blade roots do not fit; too large and the annulus is narrow.")));
            o.Add(N("Fan.PressureRatio", Loc.T("风扇", "Fan"), Loc.T("压比", "Pressure ratio"), 0.0, 3.0, 0.01, "", Loc.T("0 = 由叶尖速度与载荷系数推", "0 = derived from tip speed and loading coefficient.")));
            o.Add(N("Fan.BladeCount", Loc.T("风扇", "Fan"), Loc.T("叶片数", "Blades"), 0, 48, 1, "片", Loc.T("0 = 按直径自动（r×28，夹 12~38）", "0 = automatic by diameter (r x 28, clamped 12-38).")));
            o.Add(N("Fan.Shaft", Loc.T("风扇", "Fan"), Loc.T("所在轴", "Shaft"), 1, 3, 1, "轴", Loc.T("通常是低压轴（N1）", "Normally the low-pressure shaft (N1).")));
            o.Add(N("Fan.GearRatio", Loc.T("风扇", "Fan"), Loc.T("减速齿轮比", "Gear ratio"), 1.0, 4.0, 0.05, "",
                Loc.T("低压轴转速 / 风扇转速。1 = 直驱；3 左右就是 PW1100G 那类齿轮传动涡扇（GTF）。它让低压涡轮按自己的高转速转、风扇按自己的低叶尖速度转：代价是齿轮箱重量与 1/η 的功率损失",
                    "LP shaft speed over fan speed. 1 = direct drive; around 3 is the geared turbofan class (PW1100G). It lets the LP turbine spin at its own high speed while the fan runs at its own low tip speed, paid for in gearbox weight and 1/eta power loss.")));
            o.Add(N("Fan.GearEta", Loc.T("风扇", "Fan"), Loc.T("齿轮效率", "Gear efficiency"), 0.0, 1.0, 0.001, "",
                Loc.T("0 = 按 0.995（单级行星齿轮，真机 0.99~0.997）。齿轮比 = 1 时这一项不参与计算", "0 = 0.995 (single-stage planetary; real engines 0.99-0.997). With a gear ratio of 1 this term is not used.")));

            // ---- 压气机 ----
            o.Add(N("Lpc.Stages", Loc.T("压气机", "Compressor"), Loc.T("低压级数", "LP stages"), 0, 12, 1, "级", Loc.T("0 = 没有低压压气机", "0 = no low-pressure compressor.")));
            o.Add(N("Lpc.TipDiameter", Loc.T("压气机", "Compressor"), Loc.T("低压叶尖直径", "LP tip diameter"), 0.1, 1.5, 0.01, "m", Loc.T("与转速一起决定叶尖马赫", "Together with speed this sets tip Mach.")));
            o.Add(C("Lpc.Kind", Loc.T("压气机", "Compressor"), Loc.T("低压型式", "LP type"), typeof(CompressorKind), Loc.T("轴流能吃级数，离心一级顶两三级但压比上限低", "Axial takes many stages; one centrifugal stage does the work of two or three but has a lower pressure-ratio ceiling.")));
            o.Add(N("Hpc.Stages", Loc.T("压气机", "Compressor"), Loc.T("高压级数", "HP stages"), 0, 20, 1, "级", Loc.T("真机一般 6~16 级", "Real engines are usually 6-16 stages.")));
            o.Add(N("Hpc.TipDiameter", Loc.T("压气机", "Compressor"), Loc.T("高压叶尖直径", "HP tip diameter"), 0.1, 1.5, 0.01, "m", ""));
            o.Add(C("Hpc.Kind", Loc.T("压气机", "Compressor"), Loc.T("高压型式", "HP type"), typeof(CompressorKind), ""));
            o.Add(N("Hpc.PsiScale", Loc.T("压气机", "Compressor"), Loc.T("载荷系数系数", "Loading coefficient"), 0.4, 1.6, 0.01, "×", Loc.T("1.0 = 按技术代缺省。它决定每级能干多少活", "1.0 = technology-era default. It sets how much work each stage can do.")));
            o.Add(T("Hpc.Vigv", Loc.T("压气机", "Compressor"), Loc.T("可调静子（VIGV/VSV）", "Variable stators"),
                Loc.T("低转速时把前几级叶片转个角度，喘振线往左退。真机几乎都有；关掉它，低转速会诚实地报「已越过喘振线」。只在轴流、级数 ≥ 4 时生效", "At low speed the front stages are rotated, moving the surge line left. Almost every real engine has it; turn it off and low speed will honestly report 'surge line crossed'. Only active on axial compressors with 4 or more stages.")));
            o.Add(T("Hpc.Bleed", Loc.T("压气机", "Compressor"), Loc.T("中间级放气", "Interstage bleed"),
                Loc.T("低转速时从流道中段抽走一截气（从旁路排掉，不进核心），让前几级不先失速。它改的是流量，可调静子改的是边界，两回事。抽走的越多推力越少", "At low speed a slice of air is taken from the middle of the duct (dumped overboard, not into the core) so the front stages stall later. It changes flow; variable stators change the boundary - two different things. More bleed, less thrust.")));
            o.Add(N("Hpc.BleedFrac", Loc.T("压气机", "Compressor"), Loc.T("放气比例", "Bleed fraction"), 0.0, 0.25, 0.005, "",
                Loc.T("0 = 按转速自动（低于 92% 转速开阀，最多 12%）。填了就是固定比例，不管转速", "0 = automatic by speed (valve opens below 92% speed, up to 12%). A number here is a fixed fraction regardless of speed.")));

            // ---- 燃烧室 ----
            o.Add(C("Burner.Kind", Loc.T("燃烧室", "Burner"), Loc.T("型式", "Type"), typeof(BurnerKind),
                Loc.T("罐式 / 罐环 / 环形 / 短环形。型式决定压损、效率、缺省长度与罐数", "Can / can-annular / annular / short annular. The type sets pressure loss, efficiency, default length and can count.")));
            o.Add(N("Burner.Length", Loc.T("燃烧室", "Burner"), Loc.T("长度", "Length"), 0.0, 1.5, 0.01, "m", Loc.T("0 = 按型式与长径比推", "0 = derived from type and liner L/D.")));
            o.Add(N("Burner.LinerLd", Loc.T("燃烧室", "Burner"), Loc.T("火焰筒长径比", "Liner L/D"), 0.0, 6.0, 0.05, "L/H", Loc.T("0 = 按型式。太短来不及烧完（效率掉），太长压不动", "0 = by type. Too short and it cannot burn out (efficiency drops); too long and it cannot push the gas through.")));
            o.Add(N("Burner.CanCount", Loc.T("燃烧室", "Burner"), Loc.T("罐数", "Can count"), 0, 20, 1, "罐", Loc.T("0 = 按型式。环形没有罐", "0 = by type. An annular burner has no cans.")));
            o.Add(N("Burner.ExitTemp", Loc.T("燃烧室", "Burner"), Loc.T("出口温度", "Exit temperature"), 600.0, 2200.0, 10.0, "K", Loc.T("设计点 T4", "Design-point T4.")));

            // ---- 涡轮 ----
            o.Add(N("Hpt.Stages", Loc.T("涡轮", "Turbine"), Loc.T("高压级数", "HP stages"), 1, 4, 1, "级", Loc.T("高压涡轮，通常 1~2 级", "High-pressure turbine, usually 1-2 stages.")));
            o.Add(N("Hpt.TipDiameter", Loc.T("涡轮", "Turbine"), Loc.T("高压叶尖直径", "HP tip diameter"), 0.1, 1.5, 0.01, "m", ""));
            o.Add(N("Hpt.PsiScale", Loc.T("涡轮", "Turbine"), Loc.T("载荷系数系数", "Loading coefficient"), 0.4, 1.6, 0.01, "×", Loc.T("削弱涡轮能力要改这个或级数，改直径没用（转速会跟着叶尖马赫走）", "To weaken the turbine change this or the stage count; diameter will not do it (speed follows tip Mach).")));
            o.Add(N("Ipt.Stages", Loc.T("涡轮", "Turbine"), Loc.T("中压级数", "IP stages"), 0, 4, 1, "级", Loc.T("0 = 没有中压涡轮（双轴机常态）", "0 = no intermediate-pressure turbine (the usual twin-spool case).")));
            o.Add(N("Lpt.Stages", Loc.T("涡轮", "Turbine"), Loc.T("低压级数", "LP stages"), 0, 8, 1, "级", ""));
            o.Add(N("Lpt.TipDiameter", Loc.T("涡轮", "Turbine"), Loc.T("低压叶尖直径", "LP tip diameter"), 0.1, 1.5, 0.01, "m", ""));

            // ---- 加力 ----
            o.Add(T("Afterburner.Enabled", Loc.T("加力", "Afterburner"), Loc.T("开关", "Enabled"), Loc.T("关掉就没有加力段，也没有加力羽流与加力声", "Off means no afterburner section, and no afterburner plume or sound.")));
            o.Add(N("Afterburner.ExitTemp", Loc.T("加力", "Afterburner"), Loc.T("加力温度", "Afterburner temperature"), 1200.0, 2400.0, 10.0, "K", Loc.T("T7。它决定羽流颜色偏橙还是偏蓝紫", "T7. It decides whether the plume leans orange or blue-violet.")));
            o.Add(N("Afterburner.FuelRatio", Loc.T("加力", "Afterburner"), Loc.T("喷油比", "Fuel ratio"), 0.0, 0.08, 0.001, "f/a", Loc.T("0 = 按温升反推", "0 = derived from the temperature rise.")));
            o.Add(N("Afterburner.LightThrottle", Loc.T("加力", "Afterburner"), Loc.T("点火油门门槛", "Light-up throttle"), 0.0, 1.0, 0.01, "", Loc.T("自动模式下推到这里才点火", "In automatic mode, this is the throttle where it lights.")));
            o.Add(N("Afterburner.Length", Loc.T("加力", "Afterburner"), Loc.T("加力段长度", "Afterburner length"), 0.0, 2.5, 0.01, "m", Loc.T("0 = 自适应", "0 = adaptive.")));

            _all = o;
            return _all;
        }

        private static FieldSpec N(string path, string group, string label, double min, double max, double step, string unit, string hint)
        {
            return new FieldSpec(path, group, label, FieldKind.Number, min, max, step, unit, hint);
        }

        /// <summary>
        /// 选项字段：取值范围由枚举数出来（0 … n−1），不再另抄一份标签名单。
        /// 存档里存的是整数下标，所以**枚举成员的顺序就是存档格式**，改顺序等于改存档语义。
        /// </summary>
        private static FieldSpec C(string path, string group, string label, Type choiceType, string hint)
        {
            int n = choiceType != null ? Enum.GetValues(choiceType).Length : 1;
            FieldSpec f = new FieldSpec(path, group, label, FieldKind.Choice, 0, n - 1, 1, "", hint);
            f.ChoiceType = choiceType;
            return f;
        }

        private static FieldSpec T(string path, string group, string label, string hint)
        {
            return new FieldSpec(path, group, label, FieldKind.Toggle, 0, 1, 1, "", hint);
        }

        public static List<string> Groups()
        {
            List<string> g = new List<string>();
            foreach (FieldSpec f in All())
                if (!g.Contains(f.Group)) g.Add(f.Group);
            return g;
        }

        public static List<FieldSpec> Of(string group)
        {
            List<FieldSpec> o = new List<FieldSpec>();
            foreach (FieldSpec f in All())
                if (f.Group == group) o.Add(f);
            return o;
        }

        public static FieldSpec Find(string path)
        {
            foreach (FieldSpec f in All())
                if (f.Path == path) return f;
            return null;
        }

        /// <summary>
        /// 反射扫一遍设计对象，把**表里没有、也没写明理由**的公有字段列出来。
        ///
        /// 这是这层存在的第二个理由：可视化编辑器按表铺控件，表漏了字段没人会报错，
        /// 只有玩家发现"这个选项不见了"。所以必须有地方能把差值算出来、打出来。
        ///
        /// 以前这个函数把"按设计不给入口"的字段也一起报（自检里那句"3 个没元数据：…"），
        /// 读起来像三处失职。真正需要区分的是两种：**没人想起要登记**（该红）与
        /// **有意不给入口**（该有一句话）。前者进这个返回值，后者进 `NotInEditor`。
        /// </summary>
        public static List<string> Uncovered()
        {
            List<string> miss = new List<string>();
            Walk(typeof(EngineDesignData), "", 0, miss);
            return miss;
        }

        /// <summary>
        /// 有意不给编辑器入口的字段，以及为什么。键是路径。
        /// 每一条都要写理由——理由是这条记录存在的全部意义，空着就等于没登记。
        /// </summary>
        public static readonly Dictionary<string, string> NotInEditor = new Dictionary<string, string>
        {
            {
                "Length",
                "手填长度的兜底值。只在「长度按部件自动算」关掉时才有意义，摆成滑杆会被拧，" +
                "然后有人问「为什么改级数推力没变」——长度的账在 EngineLength 里（§94）。"
            },
            {
                "IdleThrustFrac",
                "慢车推力占比是**读数**，不是输入：设计器按 T4 与贫油线自动反解（CalibrateIdleAuto），" +
                "给入口就等于允许两个互相矛盾的真值。"
            },
            {
                "IdleN2Pct",
                "同上，慢车转速也是反解出来的读数。"
            },
        };

        private static readonly string[] Skipped =
        {
            "Name", "Tech", "TechName", "Intake", "Fan", "Lpc", "Hpc", "Hpt", "Ipt", "Lpt", "Burner", "Afterburner",
        };

        private static void Walk(Type t, string prefix, int depth, List<string> miss)
        {
            if (t == null || depth > 2) return;
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string path = prefix.Length == 0 ? fi.Name : prefix + "." + fi.Name;
                if (prefix.Length == 0 && Array.IndexOf(Skipped, fi.Name) >= 0) continue;
                if (NotInEditor.ContainsKey(path)) continue;
                Type ft = fi.FieldType;
                if (ft.IsPrimitive || ft == typeof(string) || ft.IsEnum)
                {
                    if (Find(path) == null) miss.Add(path);
                }
                else if (!ft.IsArray && !ft.IsGenericType)
                {
                    Walk(ft, path, depth + 1, miss);
                }
            }
        }

        /// <summary>给自检/日志用的一行摘要。</summary>
        public static string Report()
        {
            List<FieldSpec> all = All();
            List<string> miss = Uncovered();
            int num = 0, ch = 0, tg = 0;
            foreach (FieldSpec f in all)
            {
                if (f.Kind == FieldKind.Number) num++;
                else if (f.Kind == FieldKind.Choice) ch++;
                else if (f.Kind == FieldKind.Toggle) tg++;
            }
            string m = miss.Count == 0
                ? (NotInEditor.Count == 0
                    ? "全部有元数据"
                    : string.Format("全部有元数据（另有 {0} 个按设计不给入口）", NotInEditor.Count))
                : miss.Count + " 个没元数据：" + string.Join("、", miss.ToArray());
            return string.Format("字段元数据 {0} 条（数 {1} / 选 {2} / 开关 {3}）｜分组 {4} 个｜{5}",
                all.Count, num, ch, tg, Groups().Count, m);
        }

        /// <summary>按路径读一个字段（可视化编辑器回写时用它，不写死反射代码）。</summary>
        public static double Read(EngineDesignData d, string path)
        {
            // 注意：Resolve 只走到"末段的容器"，真正的值要再取一步字段。
            // 第一版就在这里错成"把 CompressorSpec 当值返回"，Read 永远给 0，
            // 而 Write 是对的——这种"写进去读不回"的错只有真跑一遍才看得见。
            FieldInfo fi = FieldAt(d, path);
            if (fi == null) return 0.0;
            object o = Resolve(d, path);
            if (o == null) return 0.0;
            o = fi.GetValue(o);
            if (o == null) return 0.0;
            if (o is double) return (double)o;
            if (o is int) return (int)o;
            if (o is float) return (float)o;
            if (o is bool) return ((bool)o) ? 1.0 : 0.0;
            if (o.GetType().IsEnum) return Convert.ToDouble(o, CultureInfo.InvariantCulture);
            return 0.0;
        }

        /// <summary>按路径写一个字段，返回是否写成功（写不进去不抛异常，让界面自己显示"这条存不了"）。</summary>
        public static bool Write(EngineDesignData d, string path, double v)
        {
            object o = Resolve(d, path, true);
            if (o == null) return false;
            FieldInfo fi = FieldAt(d, path);
            if (fi == null) return false;
            try
            {
                if (fi.FieldType == typeof(double)) fi.SetValue(o, v);
                else if (fi.FieldType == typeof(int)) fi.SetValue(o, (int)Math.Round(v));
                else if (fi.FieldType == typeof(float)) fi.SetValue(o, (float)v);
                else if (fi.FieldType == typeof(bool)) fi.SetValue(o, v > 0.5);
                else if (fi.FieldType.IsEnum) fi.SetValue(o, Enum.ToObject(fi.FieldType, (int)Math.Round(v)));
                else return false;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static object Resolve(EngineDesignData d, string path) { return Resolve(d, path, false); }

        private static object Resolve(EngineDesignData d, string path, bool unused)
        {
            string[] parts = path.Split('.');
            object cur = d;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (cur == null) return null;
                FieldInfo fi = cur.GetType().GetField(parts[i], BindingFlags.Public | BindingFlags.Instance);
                if (fi == null) return null;
                cur = fi.GetValue(cur);
            }
            return cur;
        }

        private static FieldInfo FieldAt(EngineDesignData d, string path)
        {
            string[] parts = path.Split('.');
            object cur = Resolve(d, path);
            if (cur == null) return null;
            return cur.GetType().GetField(parts[parts.Length - 1], BindingFlags.Public | BindingFlags.Instance);
        }
    }
}