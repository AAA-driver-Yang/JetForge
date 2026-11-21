using System;
using System.Collections.Generic;
using System.Text;

namespace JetForge.Core
{
    /// <summary>
    /// 模型的证据等级（Phase3 文档 §9）。分类**按实际实现定，不许为了宣传往高里写**：
    ///
    /// - A：守恒方程 + 明确边界条件（质量/动量/能量都能对账）。
    /// - B：工程关联式或经验模型（有公式，但系数是拟合的）。
    /// - C：用公开真机数据标定过（数对得上，机理是近似的）。
    /// - D：为游戏体验服务的简化近似（能玩，但别当物理）。
    ///
    /// 这张表存在的唯一目的：防止后来的开发把 B 级经验式当成 A 级守恒解，
    /// 也防止玩家把 D 级的估算当成图纸。
    /// </summary>
    public enum ModelGrade
    {
        A = 0,
        B = 1,
        C = 2,
        D = 3,
    }

    /// <summary>一条模型档案。</summary>
    public sealed class ModelEntry
    {
        /// <summary>代码里用来对账的键，改成别的要同步改引用处。</summary>
        public string Key = "";
        /// <summary>玩家能看懂的名字。</summary>
        public string Name = "";
        public ModelGrade Grade = ModelGrade.D;
        /// <summary>主要方程或经验式（一句话，写到能照着复现）。</summary>
        public string Equations = "";
        /// <summary>假设与边界条件。</summary>
        public string Assumptions = "";
        /// <summary>适用范围与失效方式。</summary>
        public string Limits = "";
        /// <summary>数据来源或标定方法。</summary>
        public string Source = "";
        /// <summary>已知误差（有数就给数，没数就说没验证过）。</summary>
        public string KnownError = "";

        public string GradeName()
        {
            switch (Grade)
            {
                case ModelGrade.A: return Loc.T("A 守恒解", "A  conservation exact");
                case ModelGrade.B: return Loc.T("B 经验式", "B  empirical fit");
                case ModelGrade.C: return Loc.T("C 真机标定", "C  calibrated to real engines");
                default: return Loc.T("D 游戏近似", "D  game approximation");
            }
        }

        public string Describe()
        {
            return string.Format(Loc.T("{0}｜{1}｜误差 {2}", "{0}, {1}, error {2}"), Name, GradeName(), KnownError);
        }

        public string DescribeFull()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Name).Append("（").Append(GradeName()).Append("）").Append('\n');
            sb.Append(Loc.T("  算法：", "  Method:")).Append(Equations).Append('\n');
            sb.Append(Loc.T("  假设：", "  Assumes:")).Append(Assumptions).Append('\n');
            sb.Append(Loc.T("  边界：", "  Bounds:")).Append(Limits).Append('\n');
            sb.Append(Loc.T("  来源：", "  Source:")).Append(Source).Append('\n');
            sb.Append(Loc.T("  误差：", "  Error:")).Append(KnownError);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 全部物理模型的档案表。**新增一个会输出数字的模型，就要在这里登记一条**——
    /// 回归第 98 段会检查必需的键都在，漏登记会红，这样"这个数是怎么来的"不会丢。
    /// </summary>
    public static class ModelLedger
    {
        private static List<ModelEntry> _entries;

        public static IList<ModelEntry> Entries
        {
            get
            {
                if (_entries == null) Build();
                return _entries;
            }
        }

        public static ModelEntry Get(string key)
        {
            IList<ModelEntry> all = Entries;
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].Key, key, StringComparison.OrdinalIgnoreCase)) return all[i];
            return null;
        }

        /// <summary>某一级的条数（给读数栏那一行用）。</summary>
        public static int CountOf(ModelGrade g)
        {
            int n = 0;
            IList<ModelEntry> all = Entries;
            for (int i = 0; i < all.Count; i++) if (all[i].Grade == g) n++;
            return n;
        }

        /// <summary>一句话汇总：A 几条、B 几条……</summary>
        public static string Summary()
        {
            return string.Format(Loc.T("A {0} 条｜B {1} 条｜C {2} 条｜D {3} 条", "A {0}, B {1}, C {2}, D {3}"),
                CountOf(ModelGrade.A), CountOf(ModelGrade.B),
                CountOf(ModelGrade.C), CountOf(ModelGrade.D));
        }

        /// <summary>回归与自检要求的键：少一个就说明有新模型没登记。</summary>
        public static readonly string[] RequiredKeys =
        {
            "intake.recovery", "intake.ductloss", "intake.distortion", "compressor.map",
            "compressor.work", "compressor.bleed", "burner.heat", "turbine.work",
            "nozzle.flow", "nozzle.area", "thrust.split", "mass.cost", "length.estimate",
            "control.spool", "fuel.schedule", "gearbox", "variable.geometry", "jet.mixing",
            // 第 82 轮补登的四条：它们在算数，但档案里一直没有位置
            "compressor.stage.margin", "turbine.capacity", "relight.window", "intake.facemach",
            // 第 111 轮补登：齿轮箱从「只算重量」变成「还算柔度」，档位 C
            "gearbox.torsion",
        };

        private static void Build()
        {
            _entries = new List<ModelEntry>();

            // ---- 进气道 ----
            Add("intake.recovery", Loc.T("进气道总压恢复（激波模式）", "Intake total pressure recovery (shock model)"), ModelGrade.B,
                Loc.T("逐级斜激波 θ-β-M 弱解 + 结尾正激波；锥体用等偏角斜激波并乘 1.25 损失", "Oblique shocks by the weak θ-β-M solution stage by stage, then one normal shock; cones reuse the same-deflection oblique solution with losses times 1.25"),
                Loc.T("来流均匀、无粘附面层、单锥/单楔，忽略溢流与边界层抽吸的细节", "Uniform inflow, no boundary layer, one cone or one ramp; spillage and bleed details ignored"),
                Loc.T("Ma>1 才有意义；超过 θmax 判脱体按正激波算；Ma>3 的离解与真实气体效应未建模", "Only meaningful above Ma 1; past θmax it is treated as detached and scored as a normal shock; dissociation and real-gas effects above Ma 3 are not modelled"),
                Loc.T("斜激波关系式取自标准气动教材；正激波总压比与 M2 与教科书数值逐位核对（回归第 61 段）", "Oblique shock relations come from standard aerodynamics texts; normal shock pressure ratio and M2 are checked digit by digit against textbook values (regression section 61)"),
                Loc.T("经验模式 ±5%（真机 0.93~0.97），激波模式比经验值低 5~20%，属机理更严、数更保守", "Empirical mode is good to 5% (real engines 0.93 to 0.97); shock mode reads 5 to 20% lower, which is a stricter mechanism and a more conservative number"));

            Add("intake.ductloss", Loc.T("进气道管道损失", "Intake duct loss"), ModelGrade.B,
                Loc.T("0.0060·L/(2R) + 0.030·(√(捕获/喉道)−1)，夹 [0.80,1.0]", "0.0060 L/(2R) plus 0.030 (sqrt(capture/throat) minus 1), clamped to 0.80 to 1.0"),
                Loc.T("圆管摩擦 + 突然扩张，忽略弯头分离与二次流", "Round duct friction plus a sudden expansion; bend separation and secondary flow ignored"),
                Loc.T("短扩压器（L/D<4）可用；大弯角（>45°）会低估损失", "Fine for short diffusers (L/D under 4); large bends (over 45 deg) underestimate the loss"),
                Loc.T("内流管道损失的量级取自工程手册的摩擦系数与扩压效率", "The magnitude comes from handbook friction factors and diffuser efficiency"),
                Loc.T("±2 个百分点；L/D 越大越接近上界", "Within 2 points; the longer the L/D the closer to the upper bound"));

            Add("intake.distortion", Loc.T("进气畸变与迎角侧滑", "Intake distortion, angle of attack and sideslip"), ModelGrade.D,
                Loc.T("横向角 √(α²+0.7β²)，每度 0.35%（压缩型式）/0.12%（亚声速），夹 [0.80,1.0]", "Crossflow angle is sqrt(alpha squared plus 0.7 beta squared); 0.35% per degree for compression-type intakes, 0.12% for subsonic ones, clamped to 0.80 to 1.0"),
                Loc.T("把畸变折算成一个等效总压损失，不区分周向/径向分布", "Distortion is folded into an equivalent total pressure loss; circumferential and radial patterns are not separated"),
                Loc.T("±20° 以内；再大就是真失速，本模型只会继续掉恢复", "Within plus or minus 20 deg; beyond that it is real stall, and this model just keeps losing recovery"),
                Loc.T("系数是按「真机迎角试飞掉 1~2% 总压」的量级取的，没有逐台标定", "The coefficient is sized from flight test data where a few degrees of incidence costs 1 to 2% total pressure; not calibrated per engine"),
                Loc.T("没有实验对照，属游戏化简化；只影响裕度与推力 1~3%", "No test data behind it, a deliberate simplification; it moves margin and thrust by 1 to 3%"));

            // ---- 压气机 ----
            Add("compressor.map", Loc.T("压气机特性图（合成，无真机数据）", "Compressor map (synthesised, no engine data)"), ModelGrade.D,
                Loc.T("等转速线 Nc 0.50~1.05 每 0.05 一条；沿线的压比由相对流量系数 φ 线性给定，ψ 斜率由「喘振点压比 = 设计压比 × 1.20」反解；喘振线 φ=0.90、堵塞线 φ=1.18（都相对设计点 φ=1）",
                      "One speed line every 0.05 of corrected speed from 0.50 to 1.05; pressure ratio along a line is linear in the relative flow coefficient phi with a psi slope solved back from 'surge point = design pressure ratio times 1.20'; surge line at phi 0.90 and choke line at phi 1.18, both relative to design phi 1"),
                Loc.T("整机压气机合成一条等转速线；φ 是相对值不是绝对流量系数；两条边界是常数，不随技术代、级数、叶片几何、可调静子位置连续变化（静子只有一档加减益）",
                      "The whole compressor collapses into one speed line; phi is relative rather than absolute; both boundaries are constants that do not vary with era, stage count or blading, and the stator only switches between two gains"),
                Loc.T("Nc 0.50~1.05，超出按端点外推并置 NcBelowMap / NcAboveMap 标记；单级机器与整机同口径；这一路径不建图的机器（涡喷某些分支）报 −1 表示不知道",
                      "Corrected speed 0.50 to 1.05, extrapolated past the ends with NcBelowMap / NcAboveMap flags; single-stage machines reduce to the whole-machine figure; machines that build no chart on this path report -1 for unknown"),
                Loc.T("没有真机数据。两条边界取工程惯例（严重失速前 φ 降到设计值约 0.9 倍、堵塞时约 1.18 倍），斜率锚在 1.20 的喘振压比惯例上",
                      "No engine data at all. The two boundaries follow engineering practice (phi falls to about 0.9 of design before deep stall, about 1.18 at choke) and the slope is anchored on the usual 1.20 surge pressure ratio"),
                Loc.T("这里给不出相对真机的误差带——它不是拟合出来的，所以「喘振边界 ±10%」这类话没有依据。内部自洽：设计点裕度恒为 33.3%，可用于横向比较（改级数、静子、放气之后往哪边走），不可当绝对判据。J79 读数 33.3% 与真机 25~35% 撞上是常数选得巧，不是核对过",
                      "No error bar against real engines can be given here: it was never fitted to data, so statements like 'surge boundary within 10%' would be invented. It is internally consistent (design margin is always 33.3%) and good for relative comparisons such as which way a change moves the margin, but not as an absolute criterion. The J79 reading of 33.3% landing inside the real 25 to 35% is a lucky constant, not a validated match"));

            Add("compressor.work", Loc.T("压气机压缩功", "Compressor work"), ModelGrade.A,
                Loc.T("逐级 w = ṁ·¼(cp(T1)+2cp(T2)+cp(T2'))·ΔT/η_mech（中点比热）", "Per stage: w = mdot times a quarter of (cp(T1) plus 2 cp(T2) plus cp(T2 prime)) times dT over mechanical efficiency (midpoint specific heat)"),
                Loc.T("级间无换热、比热按温度多项式、等熵效率由技术代给定", "No interstage heat transfer, specific heat from a polynomial, isentropic efficiency set by the era"),
                Loc.T("Tt 到 1100 K 内比热多项式精度足够；再高误差上升", "The specific heat polynomial holds to 1100 K; error grows above that"),
                Loc.T("能量守恒；比热取 Gas 模块的同一份多项式（全项目共用）", "Energy is conserved and the polynomial is the same one the Gas module uses everywhere"),
                Loc.T("对设计点功率的记账误差 <0.3%", "Bookkeeping error on design point power is under 0.3%"));

            Add("compressor.bleed", Loc.T("中间级放气", "Interstage bleed"), ModelGrade.B,
                Loc.T("在第 n/2 级后扣掉 ṁ·b，出口按剩余流量继续压", "Air is removed after the middle stage and the rest is compressed on"),
                Loc.T("放气从级间抽走，不影响前面已完成的压缩功", "Bleed leaves between stages; work already done upstream is unaffected"),
                Loc.T("只在高压压气机生效（低压抽气会让预设标定整体漂移，是校准不是物理）", "Only the high-pressure compressor bleeds; bleeding the low-pressure one would shift every preset, so that is calibration, not physics"),
                Loc.T("真机放气位置与比例来自公开手册的典型值（3~8%）", "Real bleed stations and rates come from published handbook values (3 to 8%)"),
                Loc.T("放气对裕度的好处本模型不给（如实承认），只算流量与功率", "This model does not credit bleed with extra surge margin; it only accounts for flow and work"));

            // ---- 燃烧与涡轮 ----
            Add("burner.heat", Loc.T("燃烧室加热", "Combustor heat addition"), ModelGrade.B,
                Loc.T("按目标 T4 反解油气比：far = cp(T4−T3)/(η_b·LHV − cp·T4)", "Fuel/air is solved from the target T4: far = cp(T4 minus T3) over (burner efficiency times LHV minus cp times T4)"),
                Loc.T("定压燃烧、LHV 与效率按技术代给定、忽略离解与不完全燃烧", "Constant pressure combustion, LHV and efficiency from the era, dissociation and incomplete combustion ignored"),
                Loc.T("T4 到 2000 K 内可用；贫油下限由贫油熄火比约束", "Valid to 2000 K; the lean side is bounded by the blowout ratio"),
                Loc.T("LHV 取 43.0 MJ/kg（煤油公开值）；燃烧效率按技术代 0.96~0.995", "LHV is 43.0 MJ/kg, the published kerosene value; efficiency runs 0.96 to 0.995 by era"),
                Loc.T("油气比 ±2%；T4 由玩家设定，不引入模型误差", "Fuel/air good to 2%; T4 is whatever the player set, so it adds no error"));

            Add("turbine.work", Loc.T("涡轮膨胀与功率提取", "Turbine expansion and power extraction"), ModelGrade.A,
                Loc.T("Tt_out = Tt_in − w/(cp·ṁ)；落压比由涡轮效率与功需求反解（等熵关系）", "Exit total temperature is inlet minus work over cp times flow; pressure ratio is solved back from efficiency and the work demanded"),
                Loc.T("绝热膨胀、单级落压比上限按技术代、冷却气不单独建模", "Adiabatic expansion, a per-stage pressure ratio cap by era, cooling air not tracked separately"),
                Loc.T("落压比超单级上限会报错（级数不足），不会硬算", "A pressure ratio past the per-stage cap is reported as too few stages instead of being forced"),
                Loc.T("能量守恒 + 等熵关系；与压气机功率逐轴对账（TorqueBalance）", "Energy conservation plus the isentropic relation, reconciled with compressor power shaft by shaft (TorqueBalance)"),
                Loc.T("功率记账误差 <0.5%；涡轮效率本身是 B 级输入", "Work bookkeeping is good to 0.5%; turbine efficiency itself is a B-level input"));

            // ---- 喷管与推力 ----
            Add("nozzle.flow", Loc.T("喷管流动与壅塞", "Nozzle flow and choking"), ModelGrade.A,
                Loc.T("一维等熵 + 正激波：npr ≥ ((γ+1)/2)^(γ/(γ−1)) 且喉道真被灌满才算壅塞；出口面积由几何（面积比×A8）给", "1D isentropic flow: choked only when the pressure ratio is past the critical value AND the throat is actually full; exit area comes from geometry (area ratio times A8)"),
                Loc.T("一维定常、γ=1.4、无粘；分离按压力比判据（不是激波分离）", "1D steady, gamma 1.4, inviscid; separation is judged on pressure ratio, not on shock-induced separation"),
                Loc.T("落压比 1~12；过膨胀分离的精确位置不预测，只用「出口面积退回喉道」近似", "Pressure ratio 1 to 12; the exact separation point is not predicted, only approximated by the exit collapsing back to the throat"),
                Loc.T("质量/动量/能量守恒；喉道灌满率 = ṁ/ChokedFlow(A8) 直接可查（回归第 90 段）", "Mass, momentum and energy are conserved; throat fill ratio is flow over choked flow, directly readable (regression section 90)"),
                Loc.T("壅塞流量 ±1%（γ 取常数带来的偏差）；分离工况推力 ±5%", "Choked flow good to 1% (the cost of a constant gamma); separated thrust good to 5%"));

            Add("nozzle.area", Loc.T("可变喷口喉道面积", "Variable nozzle throat area"), ModelGrade.C,
                Loc.T("A8 = A8_design·f(Nc)·√(T7/T7_design)，作动筒位置与需求分开报", "A8 = design A8 times f(Nc) times sqrt(T7 over design T7); actuator position and demand are reported separately"),
                Loc.T("作动筒按加力指令开环动作；需求只用于诊断，不闭环", "The actuator moves open loop on the afterburner command; demand is diagnostic only"),
                Loc.T("行程 0.3~1.36 倍设计面积；喷口开得比需要大时如实告警", "Travel is 0.3 to 1.36 times design area; opening wider than needed raises a warning"),
                Loc.T("真机 A8 行程与全加力开度量级取自公开资料（1.3~1.5 倍）", "Real travel and full afterburner opening come from published data (1.3 to 1.5 times)"),
                Loc.T("开度 ±10%；气动上只是「过不过得去」的门槛，不改压比", "Opening good to 10%; aerodynamically it is only a gate for flow, not a pressure ratio knob"));

            Add("thrust.split", Loc.T("推力分项", "Thrust split"), ModelGrade.A,
                Loc.T("F = ṁ·Ve + (Pe−Pa)·Ae；核心与涵道分开算再相加", "F = mdot times Ve plus (Pe minus Pa) times Ae; core and bypass are computed separately and added"),
                Loc.T("无安装损失、无外流阻力（进气溢流阻力单独记）", "No installation loss and no external drag (intake spillage drag is booked separately)"),
                Loc.T("整机净推力；安装推力与机体干扰未建模", "This is bare net thrust; installed thrust and airframe interference are not modelled"),
                Loc.T("动量与压力两项相加必须等于总推力（回归第 90 段钉 split_err）", "The two terms must add up to total thrust (regression section 90 pins split_err)"),
                Loc.T("分项记账误差 <1e-9 相对", "Split bookkeeping error under 1e-9 relative"));

            // ---- 整机 ----
            Add("mass.cost", Loc.T("质量与造价估算", "Mass and cost estimate"), ModelGrade.D,
                Loc.T("按各部件的湿面积/直径/长度乘单位重量系数，加齿轮箱、加力、作动、加强", "Wetted area, diameter and length per component times a unit weight factor, plus gearbox, afterburner, actuators and reinforcement"),
                Loc.T("同一技术代用同一组密度；不含紧固件、管路、线束、附件机匣细节", "One density set per era; fasteners, plumbing, wiring and accessory gearbox detail are excluded"),
                Loc.T("1~200 kN 量级；更小会被下限压住，更大线性外推", "Good from 1 to 200 kN; smaller is held by a floor, larger is extrapolated linearly"),
                Loc.T("系数按五台真机（J79/J57/J85/RD-33/PW1100G）的干重反推", "Factors are backed out from the dry mass of five real engines: J79, J57, J85, RD-33 and PW1100G"),
                Loc.T("±15%；RD-33 干态 1177 kg 对真机 1055 kg（+11.6%）", "Within 15%; RD-33 reads 1177 kg dry against 1055 kg (11.6% high)"));

            Add("length.estimate", Loc.T("整机长度估算", "Overall length estimate"), ModelGrade.C,
                Loc.T("逐段叠长：压气机 0.06D+0.08 每级、涡轮每级按直径、燃烧室按停留时间", "Lengths stack stage by stage: 0.06D plus 0.08 per compressor stage, per-stage turbine length by diameter, combustor by residence time"),
                Loc.T("各段同轴直排，不含中介机匣与齿轮箱的转角", "All sections are straight and coaxial; intermediate cases and gearbox offsets are not included"),
                Loc.T("0.5~8 m；超过 6.5 m 后管道损失显著", "0.5 to 8 m; past 6.5 m duct loss starts to bite"),
                Loc.T("四台真机标定（J79/J57/J85/RD-33 误差 ±8%）", "Calibrated on four real engines (J79, J57, J85, RD-33, within 8%)"),
                Loc.T("±10%；加齿轮箱会额外加一节（GTF 已计入）", "Within 10%; a gearbox adds a section (already counted for the GTF)"));

            // ---- 控制与燃油 ----
            Add("control.spool", Loc.T("转速控制与加减速限制", "Speed control and acceleration limits"), ModelGrade.D,
                Loc.T("一阶惯性追目标转速；液压 0.90/1.40、FADEC 2.20/3.50 的加减速不对称；超转切油并锁存 0.6 s", "First-order lag toward the target speed; asymmetric accel and decel, 0.90 and 1.40 for hydromechanical, 2.20 and 3.50 for FADEC; overspeed cuts fuel and latches for 0.6 s"),
                Loc.T("把执行机构与燃油系统合成一个时间常数", "Actuator and fuel system are collapsed into one time constant"),
                Loc.T("慢车以上；起动过程未建模", "Above idle only; starting is not modelled"),
                Loc.T("时间常数取自公开的真机加速时间（慢车到全加力约 3~5 s）", "Time constants come from published acceleration times (idle to full afterburner in 3 to 5 s)"),
                Loc.T("加速时间 ±30%；属体验优先的简化", "Acceleration time good to 30%; a simplification that favours feel"));

            Add("fuel.schedule", Loc.T("油量-转速表", "Fuel-to-speed schedule"), ModelGrade.D,
                Loc.T("9 点查表（油量↔转速），强制单调；无控模式油门直给油量，有控模式油门是推力指令、燃油由控制器闭环找", "Nine-point table between fuel flow and speed, forced monotonic; uncontrolled mode feeds fuel straight from the throttle, controlled modes treat the throttle as a thrust demand and close the loop on fuel"),
                Loc.T("稳态查表 + 一阶滤波，不含燃烧动态（啸叫、熄火边界）", "Steady table plus a first-order filter; combustion dynamics such as howl and blowout edges are not included"),
                Loc.T("慢车以上；低于表底按 0 处理（于是转速自发衰减）", "Above idle; below the bottom of the table fuel goes to zero and the spool decays on its own"),
                Loc.T("按各预设的慢车/额定/最大三点反推九点表", "The nine points are backed out from each preset's idle, rated and max conditions"),
                Loc.T("±10%；熄火判定是「转速掉光」，不是真实的贫油熄火边界", "Within 10%; flameout here means the spool has died, not a real lean blowout boundary"));

            // ---- 结构与机构 ----
            Add("gearbox", Loc.T("齿轮传动风扇（GTF）", "Geared turbofan"), ModelGrade.B,
                Loc.T("风扇转速 = 低压轴转速/比；重量 40+0.0055·风扇功率·比；功率多扣 1/η；轴向长一节", "Fan speed is shaft speed over the ratio; mass is 40 plus 0.0055 times fan power times ratio; power is charged 1/eta; one more axial section"),
                Loc.T("单级行星齿轮、效率按 0.99/级，不建模滑油系统", "A single planetary stage at 0.99 efficiency; the oil system is not modelled"),
                Loc.T("比 2.0~3.5（真机 3.0 左右）", "Ratio 2.0 to 3.5 (real engines sit near 3.0)"),
                Loc.T("PW1100G 的公开数据标定（推力 102.6/110 kN、BPR 6.8）", "Calibrated to published PW1100G data (thrust 102.6 over 110 kN, BPR 6.8)"),
                Loc.T("风扇直径 ±8%；BPR 6.8 对真机 12.5 的差异来自核心通流面积口径，已注明", "Fan diameter good to 8%; the BPR 6.8 against a real 12.5 comes from the core flow area convention and is documented"));

            Add("gearbox.torsion", Loc.T("齿轮箱柔度（扭角与本体扭振）", "Gearbox flexibility (twist and body torsion)"), ModelGrade.C,
                Loc.T("箱体当薄壁筒：质量装进筒里反解壁厚，Jp = π/2(ro⁴−ri⁴)，k = G·Jp/L，扭角 = τ/k，本体扭振 f = √(k/J)/2π", "Housing as a thin-walled tube: wall thickness solved from its mass, Jp and torsional stiffness from the section, twist from torque over stiffness, body torsion frequency from stiffness over inertia"),
                Loc.T("只给静扭矩下的扭角与一阶本体扭振；齿面接触柔度、轴承游隙、多级行星均载、齿频激励全不建模", "Only the static twist and the first body torsion mode; tooth contact, bearing clearance, planetary load sharing and mesh excitation are not modelled"),
                Loc.T("凡用齿轮传动风扇的机型；直驱与涡喷不给数", "Any geared-fan engine; direct-drive and turbojet designs get no number"),
                Loc.T("弹性力学里薄壁圆筒的闭式扭转解，材料取钢（G = 79.3 GPa、ρ = 7850）", "Closed-form torsion of a thin-walled tube with steel properties (G 79.3 GPa, rho 7850)"),
                Loc.T("量级判断，不给误差带：扭角 0.005° 量级说明「纸面上不软」，真机模态要有限元", "Order-of-magnitude only, no error band: a twist near 0.005 deg says the paper design is not soft; real modes need finite elements"));

            Add("variable.geometry", Loc.T("可调压缩面与可调中心锥", "Variable ramp and variable centre cone"), ModelGrade.C,
                Loc.T("Ma 1.20 起按行程收窄喉道，到 2.60 收 18%；可调锥另给喉道系数", "The throat narrows from Ma 1.20 along the travel, 18% by Ma 2.60; the variable cone adds its own throat factor"),
                Loc.T("作动是即时的（速率未建模），机构重量按捕获面积加", "Actuation is instantaneous (rate not modelled) and the mechanism is charged by capture area"),
                Loc.T("斜板/锥体；皮托与轴对称上这个开关不起作用（会告警）", "Ramp and cone only; on a pitot or an axisymmetric intake the switch does nothing and says so"),
                Loc.T("真机（F-15 斜板、SR-71 锥）的行程与收益量级取自公开资料", "Travel and payoff follow published F-15 ramp and SR-71 cone data"),
                Loc.T("收益 ±20%；机构重量 ±30%", "Payoff good to 20%, mechanism mass to 30%"));

            Add("jet.mixing", Loc.T("喷流掺混与外流干扰", "Jet mixing and external interference"), ModelGrade.D,
                Loc.T("不建模：喷流按一维出口速度离开，不与环境气流掺混", "Not modelled: the jet leaves at the 1D exit velocity and does not mix with ambient air"),
                Loc.T("推力全部由喷管出口状态给出", "Thrust comes entirely from the nozzle exit state"),
                Loc.T("任何工况；低速时影响小，高速时真实射流会有一点冲压恢复", "Any condition; small at low speed, and a real jet picks up a little ram recovery at high speed"),
                Loc.T("无来源，是明确的简化", "No source; this is a deliberate simplification"),
                Loc.T("未验证；属已知缺口，写在 §9 的未完成清单里", "Unverified and a known gap, listed under section 9 of the unfinished list"));

            // ---- 第 82 轮补登的四条：都是"已经在算数，但档案里没有"的模型 ----
            Add("compressor.stage.margin", Loc.T("逐级喘振余量（级间负荷摊分）", "Per-stage surge margin (stage loading split)"), ModelGrade.C,
                Loc.T("φ_i = φ + spread·(2i/(n−1)−1)，spread = 0.30·max(0,1−nc)，可调静子再乘 0.35；单级边界低转速放宽 0.10·(1−nc)；余量 = (φ_i/stall−1)·100", "phi_i = phi + spread (2i/(n-1) - 1) with spread = 0.30 max(0, 1-nc), times 0.35 with a variable stator; the per-stage stall line relaxes by 0.10 (1-nc) at part speed; margin = (phi_i/stall - 1) times 100"),
                Loc.T("前级重、后级轻的斜面是假设（真实负荷分配要看设计），静子按统一系数掰平", "The front-heavy slope is an assumption (real loading follows the blading) and the stator flattens it by a single factor"),
                Loc.T("只在有特性图的机器上有数；单级机器等于整机口径；涡喷类这一路径不建图，报 −1 表示不知道", "Only engines with a chart get numbers; single-stage machines reduce to the whole-machine figure; turbojets do not build a chart on this path, so -1 means unknown"),
                Loc.T("斜面方向取自轴流压气机级间负荷的常识；低转速放宽是让「常数喘振线」这个简化不自相矛盾", "The slope direction follows standard axial-compressor stage loading; the part-speed relaxation keeps the constant stall line from contradicting itself"),
                Loc.T("未与任何真机的逐级数据核对过；它回答的是「哪一级先出事」，不是「这一级的余量是百分之几」", "Not checked against per-stage data from any real engine; it answers which stage goes first, not what the margin of that stage really is"));

            Add("turbine.capacity", Loc.T("涡轮容量的两头上限", "Turbine capacity limited on both ends"), ModelGrade.B,
                Loc.T("逐级取 min(叶尖速度给的温降, 单级落压比上限给的温降)，后者 Δt = T·(1−PR^(−(γ−1)η/γ))", "Per stage take the smaller of the tip-speed temperature drop and the stage pressure-ratio limit, the latter being delta-T = T (1 - PR^(-(gamma-1) eta/gamma))"),
                Loc.T("单级落压比上限按技术年代给常数，不随级数/叶片冷却细节变化", "The stage pressure-ratio limit is a per-era constant and does not vary with cooling details"),
                Loc.T("落压比触顶时 prBound 置位，判据报「涡轮膨胀到单级极限」；只在设计点与非设计点的高转速端触发", "prBound is set when the limit binds and the checks report the turbine hitting the single-stage limit; it only triggers at the high-speed end"),
                Loc.T("叶尖速度限来自材料强度，落压比限来自单级焓降的工程惯例（本模型取 TechLevel.TurbStagePrMax）", "The tip-speed limit comes from material strength and the pressure-ratio limit from engineering practice for single-stage enthalpy drop (TechLevel.TurbStagePrMax)"),
                Loc.T("落压比上限是常数，真机随冷却与负荷分布浮动；触发时给的是「做不到」，不给替代工况的推力", "The limit is a constant while real engines vary with cooling and loading; when it binds the model says it cannot do it rather than inventing a thrust"));

            Add("relight.window", Loc.T("空中再点火窗口", "In-flight relight window"), ModelGrade.C,
                Loc.T("转速窗口 12%~70%；点火器进气总压门槛按控制档次 45/35/28/20 kPa；高度上限由门槛与 ISA 二分反解", "Spool window 12% to 70%; igniter inlet total-pressure thresholds by control class (45/35/28/20 kPa); the altitude ceiling comes from bisecting ISA against that threshold"),
                Loc.T("点燃成功只看窗口与门槛，不看点火能量、油气比与燃烧稳定性", "Lighting depends only on the window and the threshold, not on ignition energy, fuel-air ratio or flame stability"),
                Loc.T("窗口外点火一律判失败并给理由；只有数字控制档自动重试", "Outside the window ignition always fails with a reason; only the FADEC tier retries automatically"),
                Loc.T("转速窗口与点火器压力门槛按真机量级取值（离心/轴流起动机、点火器类别），未逐台标定", "The spool window and igniter thresholds are order-of-magnitude values from real practice, not calibrated per engine"),
                Loc.T("未验证；实际拦人的往往不是转速而是高空进气压力，这一点与真机一致", "Unverified; in practice the inlet pressure at altitude is what blocks a relight, which matches real engines"));

            Add("intake.facemach", Loc.T("发动机迎面马赫（按面积比反解）", "Engine-face Mach from the area ratio"), ModelGrade.B,
                Loc.T("等熵面积比 A/A* = (1/M)·[(2/(γ+1))·(1+0.5(γ−1)M²)]^((γ+1)/(2(γ−1)))，在 M∈[0.05,0.999] 上二分；面积比≤1 取 0.95", "Isentropic area ratio A/A* solved by bisection on M in [0.05, 0.999]; an area ratio of 1 or less falls back to 0.95"),
                Loc.T("扩压段内等熵、无摩擦；γ 固定 1.4", "Isentropic diffuser with no friction and a fixed gamma of 1.4"),
                Loc.T("只在有型线时用；整机兜底口径与原版件仍走设计迎面马赫，保持老读数", "Only used when a profile exists; the fallback path and stock intakes keep the design face Mach so old readings stay put"),
                Loc.T("一维等熵关系，标准气动教材", "One-dimensional isentropic relations from standard texts"),
                Loc.T("把摩擦与附面层忽略掉了，实际面马赫会略高；面积比≤1 时给 0.95 是「别在这里算出一个 1.3 马赫骗人」", "Friction and boundary layers are ignored so the real face Mach runs a little higher; the 0.95 fallback exists so a bad area ratio cannot invent a fake Mach"));
        }

        private static void Add(string key, string name, ModelGrade grade,
                                string eq, string assume, string limits, string src, string err)
        {
            _entries.Add(new ModelEntry
            {
                Key = key, Name = name, Grade = grade,
                Equations = eq, Assumptions = assume, Limits = limits,
                Source = src, KnownError = err,
            });
        }
    }
}