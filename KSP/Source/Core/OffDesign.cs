using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>非设计点结果。字段与 Python 原型 OffDesignResult 对应。</summary>
    public class OffDesignResult
    {
        public bool Ok = true;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();

        public double Alt, Mach, Throttle;
        public double N2Pct, N1Pct, N3Pct;
        /// <summary>轴号 → 该轴此刻占自己设计转速的百分比（三轴机的 N2/N3 就是这里分别取的）。</summary>
        public Dictionary<int, double> SpeedPct = new Dictionary<int, double>();
        /// <summary>轴号 → 该轴上所有叶轮里最大的叶尖线速度 m/s（叶尖马赫的分母随级变化，所以这里报线速度）。</summary>
        public Dictionary<int, double> TipSpeed = new Dictionary<int, double>();
        /// <summary>轴号 → 供/需不足导致的掉转因子（1.0 = 涡轮供得上）。</summary>
        public Dictionary<int, double> SpoolShortfall = new Dictionary<int, double>();
        /// <summary>按真机命名映射好的转速读数：N1/N2/N3 与 N1%/N2%/N3%。</summary>
        public Dictionary<string, double> Spool = new Dictionary<string, double>();
        public double MdotAir, MdotCore, Bpr, Opr, Fpr, Ttr;
        public double T1, T2, T3, T4, T5, T7;
        public double Far, FarAb;
        public double P3, P5;
        public Dictionary<int, double> Rpm = new Dictionary<int, double>();
        public double Thrust, ThrustCore, ThrustBypass, RamDrag, NetThrust;
        public double FuelFlow, Sfc;
        public double VJetCore, VJetBypass;
        public double WComp, WFan;
        public bool Converged;
        public int Iterations;
        public bool ChokedTurbine, ChokedNozzle;
        public bool NozzleSeparated, NozzleBackPressure;
        public double NozzleAreaRatio = 1.0, NozzleExitArea, NozzleMachE;
        /// <summary>出口面积用的是几何值（A9 = 面积比 × A8）时，与"流量要求的面积"的相对偏差。
        /// 相差大说明喉道没壅塞或上游限流，此时压力项按几何面积算才是对的。</summary>
        public double NozzleAeMismatch;
        /// <summary>这一遍真正用上的喉道面积与面积档位（排故障/台架读数用：面积比想象的小，
        /// 先看这里是不是被限流或设计反解改过）。</summary>
        public double A8Used, A8FracUsed;
        public double A8Need;               // 这股气需要多大的喉道（A8_干·√(T7/T7干)）
        public double A8OpenRatio;          // 实际装上 / 需要：>1 是开大了，<1 是开小了
        public bool SelfSustaining, Windmill, TurbineNozzleLimited, FanSpeedLimited, Governed;

        /// <summary>排气喷管面积撑不住这么多流量，进气量被压下来重解过。</summary>
        public bool NozzleCapped;
        public double T4Command;
        public double NCorrected, NCorrectedLimit, WcFrac;
        // ---- 多轴逐涡轮功平衡（三轴机的 IPT 独立核对）----
        public double WHpt, WIpt, WLpt;        // 各涡轮"必须"提供的功（W）
        public double CapHpt, CapIpt, CapLpt;  // 各涡轮在当前转速/叶尖速度下"最多"能提供的功（W）
        public int LimitSpool;                 // 带不动自己压气机的那根轴号；0 = 都能带动
        // ---- 进气道（独立部件）：这一档工况下它能给多少气 ----
        public double IntakeTpr;               // 总压恢复（含管道弯曲损失）
        public double IntakeCapacity;          // 喉道+捕获决定的可用进气量（kg/s）
        public bool IntakeLimited;
        /// <summary>这一帧的进气道是不是来自独立进气道零件（面板/日志要能说清楚到底接的哪一套）。</summary>
        public bool IntakeFromPart;             // 需求超过喉道能力，进气量被压下来了

        /// <summary>供判据模块使用的扁平视图。</summary>
        public Dictionary<string, double> AsView()
        {
            Dictionary<string, double> v = new Dictionary<string, double>();
            v["mdot_air"] = MdotAir; v["mdot_core"] = MdotCore;
            v["bpr"] = Bpr; v["opr"] = Opr; v["fpr"] = Fpr; v["ttr"] = Ttr;
            v["T1"] = T1; v["T2"] = T2; v["T3"] = T3; v["T4"] = T4; v["T5"] = T5; v["T7"] = T7;
            v["egt"] = T5;
            v["P3"] = P3; v["P5"] = P5;
            v["far"] = Far; v["far_ab"] = FarAb;
            v["thrust"] = NetThrust; v["thrust_gross"] = Thrust;
            v["thrust_core"] = ThrustCore; v["thrust_bypass"] = ThrustBypass;
            v["fuel_flow"] = FuelFlow; v["sfc"] = Sfc;
            v["W_comp"] = WComp; v["W_fan"] = WFan;
            v["n1_pct"] = N1Pct; v["n2_pct"] = N2Pct;
            v["n_corrected"] = NCorrected;
            v["w_hpt"] = WHpt; v["w_ipt"] = WIpt; v["w_lpt"] = WLpt;
            v["cap_hpt"] = CapHpt; v["cap_ipt"] = CapIpt; v["cap_lpt"] = CapLpt;
            v["spool_limit"] = LimitSpool;
            v["self_sustaining"] = SelfSustaining ? 1.0 : 0.0;
            v["windmill"] = Windmill ? 1.0 : 0.0;
            v["governed"] = Governed ? 1.0 : 0.0;
            v["throttle"] = Throttle;
            v["alt"] = Alt; v["mach"] = Mach;
            v["intake_tpr"] = IntakeTpr;
            v["intake_capacity"] = IntakeCapacity;
            v["intake_limited"] = IntakeLimited ? 1.0 : 0.0;
            return v;
        }
    }

    /// <summary>设计点派生常数：一次算好，非设计点反复用。</summary>
    public class DesignConstants
    {
        public double Cpr = 1.0;            // 设计总压比
        public double Ttr = 0.85;           // 涡轮温比 T5/T4
        public double FanWorkConstant;      // W_fan / W_comp
        public double WTurbDesign;          // 设计点每公斤核心气流的涡轮功（J/kg）
        /// <summary>附件/引气/间隙/风阻这类"不随油门缩的固定开销"占设计点涡轮功的比例。</summary>
        public double AccessoryFrac = 0.0;
        public double ATurb;                // 涡轮导向器喉道面积（由设计点反解）
        public double A8 = 0.0;             // 核心喷管喉道面积（由设计点反解）
        public double T7D = 1085.0;         // 干态设计点的涡轮后总温（喉道开度的基准，见 NozzleSchedule）
        public double NozzleAreaRatio = 1.0; // 收扩喷管 A9/A8（设计点完全膨胀）
        public double Bpr;
        public double FprD = 1.0;           // 设计点风扇压比（风扇流量函数要拿它做基准）
        public double T4Max = 1500.0;
        public double T4Idle = 1100.0;
        /// <summary>
        /// 慢车工况的油气比。**低于 <see cref="LeanFar"/> 就是贫油：真机会熄火，不是"推力小一点"。**
        ///
        /// 玩家可以自己设慢车推力占比（`IdleThrustFrac`），设得太低时二分还是会给出一个
        /// T4，但那团火在物理上根本维不住——Flyout 就是在这儿熄火的。我们以前只会
        /// "尽量给到能自持的最低值"，等于替玩家兜了底，反而不符合他要的手感。
        /// </summary>
        public double IdleFar = 0.02;
        /// <summary>贫油熄火下限（油气比）。真机燃烧室在 0.004~0.005 附近就吹熄了。</summary>
        public double LeanFar = 0.0050;
        /// <summary>
        /// 富油上限（油气比）。超过它火焰会拖到涡轮里继续烧——不是立刻熄火，
        /// 而是把热损伤往上推。设计点正常运行时离这条线还远得很，它专门抓"玩家把喷油调疯了"。
        /// </summary>
        public double RichFar = 0.040;
        /// <summary>慢车这一档到底烧不烧得住。</summary>
        public bool IdleSustainable = true;
        /// <summary>
        /// 慢车"最低可用喷油量"（kg/s）：这台机器不靠来流、自己转着要烧的油。
        /// 玩家油门到底就是这一档，再少就是往燃烧室里滴油——火留不住，转速往下掉。
        /// 由校准阶段算一次存下来（见 `DeriveDesignConstants`），面板与自检直接读。
        /// </summary>
        public double IdleFuelFlow = 0.0;
        /// <summary>
        /// 反解出来的慢车推力占满油门的比例——**这是读数，不是输入**。
        /// 以前它是玩家填的设计参数，于是"慢车"变成一个旋钮：填 20% 就把 T4 顶到
        /// 能烧出 20% 推力的地方。真机上慢车只有一个位置：火还留得住的最低那一档。
        /// </summary>
        public double IdleThrustFrac = 0.0;
        /// <summary>
        /// 慢车油气比相对贫油下限的余量。真机慢车离吹熄不会贴着红线停，
        /// 1.6 倍是"再收一点油门还烧得住、但已经不能再少"的位置。
        /// </summary>
        public const double IdleLeanMargin = 1.60;
        /// <summary>
        /// 玩家要的慢车比这台机器能给的最低档还低——校准只好停在能自持的下沿。
        ///
        /// 这不是"调到极限"，是"这一档根本不存在"：真机推到那一带，火就吹了。
        /// 我们以前把结果悄悄夹回来，玩家看到的是"设 2% 也照样转"，与 Flyout 的手感相反。
        /// </summary>
        public bool IdleClamped = false;
        /// <summary>
        /// 燃烧室总压恢复系数（含火焰筒形状的影响），由设计点的焚烧器配置反算。
        ///
        /// 为什么不能继续用 <c>tech.BurnerDpRatio</c>：那只是"这一代技术的典型值"。
        /// 玩家把燃烧室从环形换成罐式、或把火焰筒 L/H 调到 1.0 时，设计点与判据都按形状罚了，
        /// 非设计点却仍用代数常数——同一台机器在两个模块里表现不一致，飞行中等于没罚。
        /// </summary>
        public double BurnerSigma = 0.955;
        /// <summary>
        /// 三段管道损失的乘子（1 = 不损失）。由设计点按几何长度与截面马赫算出来后抄进来。
        ///
        /// 为什么不在这里按实时马赫重算：管道截面与长度是几何，工况变化对 f 的影响是二阶
        /// （M²/(1+0.2M²) 在 M0.3~0.6 之间只变十几个点），而每次迭代都反解一遍马赫要多算三次
        /// 静态量。长度对整机的影响已经通过设计点进了这三个乘子——把发动机拉长，设计点先亏，
        /// 这里的乘子跟着变小，非设计点整条包线一起变差，这就是想要的效果。
        /// </summary>
        public double DuctEtaIn = 1.0, DuctEtaCore = 1.0, DuctEtaExhaust = 1.0;
        /// <summary>燃烧效率（同样按形状取，不是按年代取）。</summary>
        public double BurnerEta = 0.985;
        /// <summary>
        /// 设计点反解出来的"等效核心压气机效率"。
        ///
        /// 设计点是逐级算的：风扇、LPC、HPC 各走自己的 <c>ScaleEta</c>（雷诺数与叶尖间隙修正），
        /// 离心式还有自己的效率罚。非设计点若直接用 <c>tech.HpcEtaPol</c>，就等于
        /// 把雷诺数修正、间隙修正、离心罚和整台 LPC 全都白送了——于是同一台 J79
        /// 在它自己的设计工况满油门能多跑出 6% 压比、2.8% 推力（见 §60）。
        /// 这里从设计点反解一个等效效率，让两边至少在设计点上对得上。
        /// </summary>
        public double EtaCoreDesign = 0.0;
        /// <summary>同上，风扇的等效效率。</summary>
        public double EtaFanDesign = 0.0;
        public Dictionary<int, double> RpmDesign = new Dictionary<int, double>();
        public double NcDesign = 1.0;
        public double MdotDAir, MdotDCore;
        // 设计点标定出的"每台涡轮占总涡轮功的份额"，非设计点按它分摊需求
        public double ShareHpt = 1.0, ShareIpt = 0.0, ShareLpt = 0.0;
        public bool Ok = true;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// 非设计点求解：任意高度/马赫/油门下的稳态性能。
    /// 结构与 AJE 的 SolverJet 一致（迭代压比使涡轮功=压气机功），
    /// 但流量走换算流量律、并加了换算转速调节器——这两条才是真实发动机
    /// "高空推不满油门"的原因。
    /// </summary>
    public static partial class OffDesign
    {
        public const double T0Sl = 288.15;
        public const double P0Sl = 101325.0;
        public const double FarRichLimit = 0.070;
        public const double NacCd = 0.08;

        public static double ChokedMassFlow(double area, double pt, double tt, double gamma = 0.0)
        {
            if (area <= 0.0 || pt <= 0.0 || tt <= 0.0) return 0.0;
            double g = gamma > 0.0 ? gamma : Gas.Gamma(tt);
            return area * pt / Math.Sqrt(tt) * Math.Sqrt(g / Gas.R)
                   * Math.Pow(2.0 / (g + 1.0), (g + 1.0) / (2.0 * (g - 1.0)));
        }

        public static double AnnulusArea(double tipDiameter, double hubTipRatio = 0.75)
        {
            return Math.PI / 4.0 * tipDiameter * tipDiameter * (1.0 - hubTipRatio * hubTipRatio);
        }

        // 涡轮喉道面积在这里没有单列函数：喉道由 `Sections` 与设计常量反解，
        // 曾经那个 `TurbineThroatArea`（环形面积 ×0.8）从来没被调用，已删。

        // ------------------------------------------------------------------

        public static DesignConstants DeriveDesignConstants(EngineDesignData d, IntakeInstall install = null)
        {
            DesignConstants c = new DesignConstants();
            CycleResult r = Cycle.SolveDesignPoint(d, install);
            c.Errors.AddRange(r.Errors);
            c.Warnings.AddRange(r.Warnings);
            c.Ok = r.Ok;
            c.Cpr = Math.Max(r.Opr, 1.001);
            c.Bpr = r.Bpr;
            c.FprD = r.Fpr > 0.0 ? r.Fpr : 1.0;
            c.T4Max = Math.Min(d.Burner.ExitTemp, d.Tech.T4Limit);
            c.MdotDAir = r.MdotAir;
            c.MdotDCore = r.MdotCore;

            if (d.Hpt.Stages > 0 && r.MdotCore > 0.0 && r.P3 > 0.0 && r.T4 > 0.0)
            {
                double sigma = Cycle.BurnerPerformance(d.Burner).Sigma;
                double pt4 = r.P3 * sigma;
                double choke = ChokedMassFlow(1.0, pt4, r.T4);
                c.ATurb = r.MdotCore / Math.Max(choke, 1e-9);
            }

            if (r.T4 > 0.0 && r.T5 > 0.0)
                c.Ttr = Math.Max(Math.Min(r.T5 / r.T4, 0.98), 0.5);

            // 核心喷管喉道面积与（收扩时的）面积比，都由设计点反解。
            //
            // 关键口径：A8 是「干态喉道」，不是「加力喉道」。真机的喉道是有实体尺寸的，
            // 设计师按干态巡航定它，加力时靠液压把它撑开（A8_加力 ≈ 1.2~1.4·A8_干）。
            // 如果这里按"设计点（含加力）"反解，等于给每台机白送一个可变喷口——
            // 玩家配固定收敛喷管也永远不挨罚，喷管型式这一维就成了摆设。
            // 所以配了加力时，A8 用干态设计点定尺寸；加力那侧的放大交给 NozzleSchedule。
            double a8Mdot = r.MdotCore, a8P = r.P5, a8T = r.T7, a8Far = r.Far;
            if (d.Afterburner.Enabled)
            {
                EngineDesignData dry = d.Clone();
                dry.Afterburner.Enabled = false;
                CycleResult rd = Cycle.SolveDesignPoint(dry, install);
                if (rd.Ok && rd.MdotCore > 0.0 && rd.P5 > 0.0 && rd.T7 > 0.0)
                {
                    a8Mdot = rd.MdotCore; a8P = rd.P5; a8T = rd.T7; a8Far = rd.Far;
                }
            }
            if (a8Mdot > 0.0 && a8P > 0.0 && a8T > 0.0)
            {
                double mExh = a8Mdot * (1.0 + a8Far);
                double choke = ChokedMassFlow(1.0, a8P, a8T);
                c.A8 = mExh / Math.Max(choke, 1e-9);
                // 干态设计点的涡轮后总温。可变喷口的喉道该开多大，只能拿它当基准：
                // 同样的落压比下能过的流量 ∝ 1/√T7，所以 A8 的加力那一份是 √(T7/T7D)。
                // 存下来是让喷管调度去比，而不是"档位到 1 就按满加力的 1.36 撑开"（§157）。
                c.T7D = a8T;
                double p0d, t0d;
                Gas.Isa(d.DesignAlt, out p0d, out t0d);
                c.NozzleAreaRatio = Gas.DesignAreaRatio(a8P, p0d, a8T);
            }
            if (r.WComp > 0.0)
                c.FanWorkConstant = Math.Max(r.WFan / r.WComp, 0.0);
            if (r.T4 > 0.0 && r.T5 > 0.0 && r.T5 < r.T4)
                c.WTurbDesign = Gas.Cp(r.T4) * (r.T4 - r.T5);
            // 固定开销的标定系数。0 会让部分功率的 SFC 一路探底（谷底跑到 10% 油门），
            // 6% 把谷底推到大功率段；再大就压得动慢车自持。见 §44。
            c.AccessoryFrac = 0.03;

            // 每台涡轮在设计点承担的份额（三轴机才有意义，单/双轴退化为 1/0/0 或分摊）
            double wTurbTotal = r.WTurbHpt + r.WTurbIpt + r.WTurbLpt;
            if (wTurbTotal > 0.0)
            {
                c.ShareHpt = r.WTurbHpt / wTurbTotal;
                c.ShareIpt = r.WTurbIpt / wTurbTotal;
                c.ShareLpt = r.WTurbLpt / wTurbTotal;
            }

            c.RpmDesign = Cycle.DesignShaftSpeeds(d, T0Sl);

            // ---- 把设计点的"等效效率"与"燃烧室形状"抄进非设计点用的常数里 ----
            // 这两样以前是各算各的：设计点走 ScaleEta + BurnerPerformance，
            // 非设计点走 tech.XxxEtaPol + tech.BurnerDpRatio。同一台机器两套数，
            // 表现就是"设计工况满油门能跑出比设计点更高的压比"（J79 +5.9%，J57 +8.1%）。
            Cycle.BurnerPerf bpd = Cycle.BurnerPerformance(d.Burner);
            c.BurnerSigma = Math.Max(Math.Min(bpd.Sigma, 0.99), 0.60);
            c.BurnerEta = Math.Max(Math.Min(bpd.Eta, 0.999), 0.60);

            // 管道损失：设计点已经把长度折算成三段损失，这里只是把它变成乘子。
            // 夹到 [0.70, 1.0]：真机管道不可能吃掉三成总压，超过说明几何本身不合理。
            c.DuctEtaIn = Math.Max(Math.Min(1.0 - r.DuctInletLoss, 1.0), 0.70);
            c.DuctEtaCore = Math.Max(Math.Min(1.0 - r.DuctCoreLoss, 1.0), 0.70);
            c.DuctEtaExhaust = Math.Max(Math.Min(1.0 - r.DuctExhaustLoss, 1.0), 0.70);

            {
                double p0d, t0d;
                Gas.Isa(d.DesignAlt, out p0d, out t0d);
                // 基准必须用"进气道出口总温"（有马赫时含冲压加热），不是静温。
                // 设计点如果写成 M0.85，用静温反解出来的效率会被冲压温升吃掉十几个点。
                double tt1d = r.T1 > 1.0 ? r.T1 : t0d;
                // 核心压气机的入口是"风扇出口"，不是进气道出口：
                // 涡扇的 T3 里已经含了风扇那一段温升，拿进气道出口温度当基准，
                // 反解出来的效率会被风扇的温升整个吃掉（RD-33 会反解到 0.46，
                // 夹到下限之后非设计点整台机器掉到一半压比）。
                double tt2d = r.T2 > 1.0 ? r.T2 : tt1d;
                double corePrD = r.Opr / Math.Max(r.Fpr, 1.0);
                if (r.T3 > tt2d * 1.001 && corePrD > 1.001)
                {
                    double g = Gas.Gamma(r.T3);
                    double eta = Math.Log(corePrD) * (g - 1.0) / g / Math.Log(r.T3 / tt2d);
                    // 只许在设计效率的 0.85~1.02 倍之间挪。
                    // 反解值离代数基准太远（多级低压+高压串起来时会出现）说明这台设计的
                    // 级间参数本身不"典型"，此时照抄反解值会让非设计点的效率衰减回路
                    // 自己往下滑（压比低 → 换算转速低 → 效率更低 → 压比更低），
                    // 二十几轮迭代后整台机器掉到一个假的低功率解上。
                    double lo = d.Tech.HpcEtaPol * 0.85, hi = d.Tech.HpcEtaPol * 1.02;
                    c.EtaCoreDesign = Math.Max(Math.Min(eta, hi), lo);
                }
                if (r.Fpr > 1.001 && r.T2 > tt1d * 1.001)
                {
                    double g = Gas.Gamma(r.T2);
                    double eta = Math.Log(r.Fpr) * (g - 1.0) / g / Math.Log(r.T2 / tt1d);
                    double lo = d.Tech.FanEtaPol * 0.85, hi = d.Tech.FanEtaPol * 1.02;
                    c.EtaFanDesign = Math.Max(Math.Min(eta, hi), lo);
                }
            }

            double corePrDesign = r.Opr / Math.Max(r.Fpr, 1.0);
            double ncDesign = InvertSpeed(d, corePrDesign, T0Sl, c.RpmDesign);
            c.NcDesign = ncDesign > 0.05 ? ncDesign : 1.0;

            // 慢车点不再让玩家填：它是"这台机器自己转着、火还留得住"的最低那一档，
            // 由求解器反解（见 CalibrateIdleAuto）。
            //
            // 为什么撤掉"慢车推力占满油门多少"：那是把结果当输入。玩家填 20%，
            // 求解器就得把 T4 推到能烧出 20% 推力的地方——于是"慢车"变成一个旋钮，
            // 而真机上慢车只有一个位置。填 2% 更糟：那团火维不住，模型只好替玩家兜底
            // （IdleClamped 一类的静默夹取），读数于是永远说"都挺好"。
            c.T4Idle = CalibrateIdleAuto(d, c);

            // 慢车烧不烧得住：拿反解出来的那一点 T4 真跑一遍，看油气比。
            // 这一步不能省——T4 是"温度目标"，油气比才是"这团火有没有得烧"。
            {
                OffDesignResult idle = Solve(d, 0.0, 0.0, 0.0, 0.0, c, 30, c.T4Idle, 0.0);
                // 慢车时核心机流量才是分母：FuelFlow 是整台（含涵道）的油，别拿总流量去除。
                // 单位别乘 1000：FuelFlow 已经是 kg/s（SFC = FuelFlow*3600/N 的写法就是这么定的），
                // 第一版乘了个 1000，慢车油气比算成 10.7，富油线跟着飞到 42。
                c.IdleFar = idle.Ok && idle.MdotCore > 1e-6
                    ? idle.FuelFlow / idle.MdotCore
                    : (idle.Ok ? idle.Far : 0.0);

                // 慢车"最低可用喷油量"（kg/s）。这是"这台机器不靠来流、自己转着"要烧的油，
                // 玩家把它当成油门到底的那一档：再少就是往燃烧室里滴油，火留不住，转速往下掉。
                //
                // 存在这里而不是每次现算，是因为它要出现在编辑器读数里（面板每帧都会读），
                // 而反解一次要跑一遍非设计点求解。
                c.IdleFuelFlow = idle.Ok ? Math.Max(idle.FuelFlow, 0.0) : 0.0;
                c.IdleSustainable = !(idle.Ok && c.IdleFar > 0.0 && c.IdleFar < c.LeanFar);

                // 玩家要的比能给的低：校准停在"能自持的下沿"，那不是他要的那一档。
                // 真到那一档火留不住——如实标出来，模块据此在油门到底时熄火。
                // 分母要拿**满油门净推力**，而 `r` 是设计点的循环解（CycleResult，没有净推力），
                // 所以这里多跑一次满油门——设计期一次 0.2 ms，换来"到底能不能给到"这件事有据可依。
                // t4Override 必须给 -1（"用求解器的"），给 0 会被当成"目标温度 0 K"。
                OffDesignResult full = Solve(d, 0.0, 0.0, 1.0, 0.0, c, 30, -1.0, 0.0);
                double got = full.Ok && full.NetThrust > 1.0 ? idle.NetThrust / full.NetThrust : 0.0;
                c.IdleThrustFrac = got;
                c.IdleClamped = false;   // 慢车档不再由玩家指定，也就不存在"要不起"这回事
            }

            // 富油上限按设计点油气比拉开 1.9 倍（真机富油吹熄也在正常工作点之上一个身位），
            // 但地板 0.040：设计点烧得很稀的机器不该因此"一推油门就富油"。
            {
                double designFar = r.MdotCore > 1e-6 ? r.FuelFlow / r.MdotCore : 0.0;
                c.RichFar = Math.Max(0.040, designFar * 1.9);
            }

            if (d.ShaftCount >= 3 || d.Ipt.Stages > 0)
                c.Warnings.Add(Loc.T("三轴非设计点：各轴共用一个转速比例（缺少压气机特性线时，", "Three-shaft off-design: all shafts share one speed ratio (without compressor maps")
                               + Loc.T("三根轴的独立转速是欠定的）；但每台涡轮的需/供已逐台核对，", "the independent speeds are under-determined); demand and supply are still checked per turbine,")
                               + Loc.T("见结果里的 spool_limit 与 w_*/cap_*。", "see spool_limit and w_*/cap_* in the result."));
            return c;
        }

        /// <summary>反解慢车 T4：让海平面静止的 N2 落在 60% 左右。</summary>
        public static double CalibrateIdle(EngineDesignData d, DesignConstants c, double targetN2)
        {
            return CalibrateIdle(d, c, targetN2, 0.0);
        }

        /// <summary>
        /// 反解"这台机器自己转着、火还留得住"的最低慢车点（T4）。
        ///
        /// 判据不是推力、也不是 N2，而是**油气比**：T4 是温度目标，油气比才是这团火
        /// 有没有得烧。油气比随 T4 单调升，所以在 [t3*1.02, T4Max*0.98] 上二分找
        /// "刚够烧住"的那一点，留 <see cref="IdleLeanMargin"/> 倍余量。
        ///
        /// 为什么不再问玩家要"慢车推力占几成"：那是把结果当输入。玩家填的数一旦
        /// 不是这台机器自己撑得住的那一点，模型就只能二选一——要么静默夹取（于是读数
        /// 骗人），要么照填的数烧一个维不住的 T4（于是慢车变成"随时会熄火"的旋钮）。
        /// 两者都不是慢车。
        /// </summary>
        public static double CalibrateIdleAuto(EngineDesignData d, DesignConstants c)
        {
            CycleResult r = Cycle.SolveDesignPoint(d);
            double t3Design = r.T3 > 0.0 ? r.T3 : 400.0;
            double lo = t3Design * 1.02;
            double hi = c.T4Max * 0.98;
            if (hi <= lo) return c.T4Max * 0.75;

            double lean = c.LeanFar > 1e-6 ? c.LeanFar : 0.0050;
            double floor = lean * DesignConstants.IdleLeanMargin;
            double best = -1.0;
            for (int i = 0; i < 24; i++)
            {
                double mid = 0.5 * (lo + hi);
                OffDesignResult res = Solve(d, 0.0, 0.0, 0.0, 0.0, c, 30, mid, 0.0);
                double far = res.Ok
                    ? (res.MdotCore > 1e-6 ? res.FuelFlow / res.MdotCore : res.Far)
                    : 0.0;
                if (res.Ok && far >= floor) { best = mid; hi = mid; }
                else lo = mid;
                if (hi - lo < 2.0) break;
            }
            if (best > 0.0) return best;
            // 连"刚够烧住"都找不到：宁可把慢车放高一点，也不能报一个烧不住的点。
            return c.T4Idle > 0.0 ? c.T4Idle : Math.Min(c.T4Max * 0.72, 1000.0);
        }

        /// <summary>
        /// 反解慢车 T4。
        /// 给了 <paramref name="targetThrustFrac"/> 就按"慢车推力占满油门多少"来定，
        /// 否则按 N2 百分比定。
        ///
        /// 为什么改口径：慢车不是热力学必然，是燃油调节器的一个设计选择。真机选它的依据
        /// 是"既能稳住燃烧、又能快速加速"，落在推力 6~8% 这个区间。原来我们按 N2=60% 定，
        /// 结果慢车推力占了满油门的 14~17%（真机 6~8%）——N2 对了不等于推力对了：
        /// 两台发动机可以在同一个 N2 上给出差一倍的推力，取决于低压转子转得多快。
        /// </summary>
        public static double CalibrateIdle(EngineDesignData d, DesignConstants c,
                                           double targetN2, double targetThrustFrac)
        {
            CycleResult r = Cycle.SolveDesignPoint(d);
            double t3Design = r.T3 > 0.0 ? r.T3 : 400.0;
            double lo = t3Design * 1.02;
            double hi = c.T4Max * 0.98;
            if (hi <= lo) return c.T4Max * 0.75;

            double fullThrust = 0.0;
            if (targetThrustFrac > 0.0)
            {
                OffDesignResult full = Solve(d, 0.0, 0.0, 1.0, 0.0, c, 40);
                fullThrust = full.Ok ? full.NetThrust : 0.0;
                if (fullThrust <= 0.0) targetThrustFrac = 0.0;   // 算不出满推力就退回 N2 口径
            }

            // 二分求的是"推力占满油门 targetThrustFrac"，但小发动机的最低可用推力可能
            // 已经高于这个目标（J85 就是这样），这时二分一路走到区间下沿，
            // 返回一个核心机根本转不起来的 T4。所以要记住"能自持的最低 T4"兜底。
            double lowestOk = 0.0;
            for (int i = 0; i < 18; i++)
            {
                double mid = 0.5 * (lo + hi);
                OffDesignResult res = Solve(d, 0.0, 0.0, 0.0, 0.0, c, 30, mid, 0.0);
                if (res.Ok && (lowestOk <= 0.0 || mid < lowestOk)) lowestOk = mid;
                double metric;
                if (targetThrustFrac > 0.0)
                    metric = res.Ok ? res.NetThrust / fullThrust : 0.0;
                else
                    metric = res.Ok ? res.N2Pct / 100.0 : 0.0;
                double target = targetThrustFrac > 0.0 ? targetThrustFrac : targetN2;
                if (metric < target) lo = mid; else hi = mid;
                if (Math.Abs(hi - lo) < 5.0) break;
            }
            double t4Idle = 0.5 * (lo + hi);
            OffDesignResult final = Solve(d, 0.0, 0.0, 0.0, 0.0, c, 30, t4Idle, 0.0);
            if (!final.Ok && lowestOk > 0.0) return lowestOk;
            return t4Idle;
        }

        /// <summary>把整台压气机按给定转速比例走一遍，返回总压比。</summary>
        public static double OprAtSpeed(EngineDesignData d, double speedFrac, double ttIn,
                                        Dictionary<int, double> rpmDesign)
        {
            TechLevel tech = d.Tech;
            double pt = 1.0, tt = ttIn;
            for (int k = 0; k < 2; k++)
            {
                CompressorSpec spec = k == 0 ? d.Lpc : d.Hpc;
                double eta = (k == 0 ? tech.LpcEtaPol : tech.HpcEtaPol)
                             + CompressorPhysics.EtaDelta(spec.Kind);
                eta = CompressorPhysics.OffDesignEta(eta, speedFrac);
                if (spec.Stages <= 0) continue;
                double rpm; rpmDesign.TryGetValue(spec.Shaft, out rpm);
                // 逐级：每级自己的叶尖直径与 ψ（空表时与"全机一个直径"完全等价）
                for (int i = 0; i < spec.Stages; i++)
                {
                    double ui = Cycle.TipSpeed(spec.TipOf(i), rpm * speedFrac);
                    if (ui <= 0.0) continue;
                    double psii = tech.CompressorPsi * spec.PsiOf(i) * CompressorPhysics.PsiFactor(spec.Kind);
                    pt *= Cycle.StagePi(ui, tt, eta, psii);
                    tt += Cycle.StageDTComp(ui, tt, psii);
                }
            }
            return pt;
        }

        public static double OprAtCorrectedSpeed(EngineDesignData d, double nc,
                                                 Dictionary<int, double> rpmDesign)
        {
            return OprAtSpeed(d, nc, T0Sl, rpmDesign);
        }

        public static double InvertSpeed(EngineDesignData d, double targetOpr, double ttIn,
                                         Dictionary<int, double> rpmDesign)
        {
            if (targetOpr <= 1.001) return 0.0;
            double lo = 0.05, hi = 1.45;
            for (int i = 0; i < 44; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (OprAtSpeed(d, mid, ttIn, rpmDesign) < targetOpr) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>压气机换算流量能力（相对设计转速的比例，粗拟合特性线）。</summary>
        public static double WcCapacityFrac(double nc)
        {
            if (nc <= 0.0) return 0.0;
            // 低于设计转速：线性。理由是第一性的——同一圈通道面积、轴向速度比 Ca/U 基本不变时，
            // ṁ = ρ·A·Ca ∝ Ca ∝ U ∝ N。原来这里写的是 nc²，等于让 90% 转速只吃 81% 流量，
            // 高空时换算转速从 0.93 抬到 1.05 就凭空多出 16% 流量（详见 phase2-mod §23）。
            if (nc <= 1.0) return Math.Max(nc, 0.02);
            return Math.Min(1.0 + 0.55 * (nc - 1.0), 1.12);
        }

        // internal 而不是 private：两轴拆分的原型求解器在测试工程里，要复用同一段涡轮落压比逻辑，
        // 否则原型和正式模型算的涡轮不是同一台机器，比出来的差值没有意义。
        internal static void TurbineExit(double t4, double pt4, double pAmb, double ttr,
                                        double etaT, out double t5, out double piT, out bool limited)
        {
            double g = Gas.Gamma(t4);
            double piReq = Math.Pow(ttr, -g / ((g - 1.0) * etaT));
            double piMax = Math.Max(pt4 / Math.Max(pAmb, 1.0), 1.0);
            if (piReq <= piMax)
            {
                t5 = t4 * ttr; piT = piReq; limited = false;
                return;
            }
            double tr = Math.Pow(piMax, -(g - 1.0) * etaT / g);
            t5 = t4 * tr; piT = piMax; limited = true;
        }

        // ------------------------------------------------------------------

        private class Sol
        {
            public double Prat3, T3, T4, Far, WT, PiT;
            public bool TurbineLimited, Converged;
            public int Iters;
        }

        public static OffDesignResult Solve(EngineDesignData d, double alt, double mach,
                                            double throttle, double abFrac = 0.0,
                                            DesignConstants consts = null,
                                            int maxIter = 40,
                                            double t4Override = -1.0,
                                            double abFuelRatio = 0.0,
                                            double flowCapScale = 1.0,
                                            int capPasses = 0,
                                            IntakeInstall install = null,
                                            double a8Frac = -1.0)
        {
            OffDesignResult res = new OffDesignResult { Alt = alt, Mach = mach, Throttle = throttle };
            TechLevel tech = d.Tech;
            if (consts == null) consts = DeriveDesignConstants(d);
            if (!consts.Ok)
            {
                res.Ok = false;
                res.Errors.AddRange(consts.Errors);
                return res;
            }
            if (consts.ATurb <= 0.0)
            {
                res.Ok = false;
                res.Errors.Add(Loc.T("没有高压涡轮：核心机没有功率来源", "No high-pressure turbine: the core has no source of power"));
                return res;
            }

            double thr = Math.Min(Math.Max(throttle, 0.0), 1.0);
            res.Ttr = consts.Ttr;

            // ---- 来流 ----
            double p0, t0;
            Gas.Isa(alt, out p0, out t0);
            // 进气道走对象：型式决定恢复系数（皮托吃正激波、斜板/锥体在 M1.5 以上回血），
            // 抽气比例在这里直接折流量。缺省轴对称与老代码逐位相同。
            IntakeState inl = Intake.Evaluate(d, mach, 0.0);
            // 装了独立进气道零件：总压恢复与可用流量都由零件合计给，不再看发动机自带的那份 IntakeSpec。
            // 没装（install 为 null / 没解出来）时这一行不改任何数，老存档逐位相同。
            bool fromPart = install != null && install.Present;
            // 零件求一次，恢复/流量全用这一份；没装或没解出来就退回发动机自带口径。
            IntakeSolution partSol = fromPart ? install.ToSolution(alt, mach) : null;
            if (partSol != null && partSol.Valid)
            {
                inl.Tpr = partSol.Tpr;
                inl.Ok = true;
                res.IntakeFromPart = true;
            }
            else
            {
                fromPart = false;
            }
            double pt0, tt0;
            double tpr = inl.Tpr;
            if (mach > 0.0)
            {
                Gas.Total(p0, t0, mach, out pt0, out tt0);
            }
            else
            {
                pt0 = p0; tt0 = t0;
            }
            double pt1 = pt0 * tpr * consts.DuctEtaIn, tt1 = tt0;
            double v0 = mach * Gas.SoundSpeed(t0);
            res.T1 = tt1;

            // ---- 油门 → T4 指令 ----
            double t4Cmd = t4Override >= 0.0 ? t4Override
                : consts.T4Idle + thr * (consts.T4Max - consts.T4Idle);

            double etaC = consts.EtaCoreDesign > 0.0 ? consts.EtaCoreDesign : tech.HpcEtaPol;
            double etaF = consts.EtaFanDesign > 0.0 ? consts.EtaFanDesign : tech.FanEtaPol;
            double bpr = consts.Bpr;
            double fc = consts.FanWorkConstant;

            Func<double, double, double> fprOf = (prat3v, wt) =>
            {
                if (bpr <= 0.0 || fc <= 0.0) return 1.0;
                double accF = consts.AccessoryFrac * consts.WTurbDesign
                              * Math.Max(0.0, 1.0 - wt / Math.Max(consts.WTurbDesign, 1e-9));
                double wtF = Math.Max(wt - accF, 0.0);
                double wFanTotal = wtF * fc / (1.0 + fc);
                double dTFan = wFanTotal / ((1.0 + bpr) * Gas.Cp(tt1));
                if (dTFan <= 0.0) return 1.0;
                double gf = Gas.Gamma(tt1);
                return Math.Pow(1.0 + dTFan / tt1, gf * etaF / (gf - 1.0));
            };

            Func<double, double, int, Sol> balance = (t4In, prat3Guess, iters) =>
            {
                double prat3 = Math.Max(prat3Guess, 1.001);
                Sol s = new Sol { T3 = tt1, T4 = t4In, WT = 0.0, PiT = 1.0 };
                for (int it = 1; it <= iters; it++)
                {
                    s.Iters = it;
                    double g1 = Gas.Gamma(tt1);
                    double t3 = tt1 * Math.Pow(prat3, ((g1 - 1.0) / g1 / etaC));
                    double pt4v = pt1 * prat3 * consts.BurnerSigma * consts.DuctEtaCore;
                    double dTBurn = t4In - t3;
                    double far, t4v;
                    if (dTBurn <= 0.0)
                    {
                        t4v = t3; far = 0.0;
                    }
                    else
                    {
                        double cp3 = Gas.Cp(t3), cp4 = Gas.Cp(t4In);
                        double den = Gas.LhvJet * consts.BurnerEta - cp4 * dTBurn;
                        if (den <= 0.0) return null;
                        far = Math.Min(cp3 * dTBurn / den, FarRichLimit);
                        t4v = t4In;
                    }
                    double t5v, piTv;
                    bool lim;
                    TurbineExit(t4v, pt4v, p0, consts.Ttr, tech.HptEtaPol, out t5v, out piTv, out lim);
                    double wt = Gas.Cp(t4v) * (t4v - t5v);
                    // 固定开销：油泵、发电机、液压、滑油、抽气、叶尖间隙与风阻。
                    // 设计点约占涡轮功的百分之几，但它不随油门缩——慢车时核心机功只剩两成，
                    // 这一项就吃掉一成多。真机"部分功率更费油"有一半出在这里。
                    // 写成 (1 - wt/设计功) 的形式，设计点严格为 0，不动设计点一分。
                    double acc = consts.AccessoryFrac * consts.WTurbDesign
                                 * Math.Max(0.0, 1.0 - wt / Math.Max(consts.WTurbDesign, 1e-9));
                    double wtNet = Math.Max(wt - acc, 0.0);
                    double wComp = wtNet / (1.0 + fc);
                    double wFan = wtNet - wComp;
                    double dTFan = wFan / ((1.0 + bpr) * Gas.Cp(tt1));
                    double t3New = tt1 + dTFan + wComp / Gas.Cp(tt1);
                    double prat3New = t3New <= tt1 ? 1.0
                        : Math.Pow(t3New / tt1, g1 * etaC / (g1 - 1.0));

                    s.T3 = t3; s.T4 = t4v; s.Far = far; s.WT = wt;
                    s.PiT = piTv; s.TurbineLimited = lim;

                    if (Math.Abs(prat3New - prat3) < 0.01 * Math.Max(prat3, 1.0))
                    {
                        prat3 = prat3New; s.Prat3 = prat3; s.Converged = true;
                        return s;
                    }
                    prat3 = it > 1 ? 0.5 * (prat3 + prat3New) : prat3New;
                    s.Prat3 = prat3;
                }
                return s;
            };

            double t4CmdRaw = t4Cmd;
            Sol sol = balance(t4Cmd, consts.Cpr, maxIter);
            if (sol == null)
            {
                res.Ok = false;
                res.Errors.Add(Loc.T("燃烧室温升过高，能量不守恒", "Combustor temperature rise too high; energy does not balance"));
                return res;
            }

            // ---- 非设计点效率衰减（特性线的等价替代）----
            // 效率低 → 同样涡轮功下压比低 → 换算转速低 → 效率更低。这个回路是真的，
            // 迭代到收敛为止；半途截断会让相邻油门的效率跳一格，推力因此不再单调。
            // 上限五轮，超出就用手上这版（方向总是对的）。
            for (int pass = 0; pass < 5; pass++)
            {
                double ncPass = InvertSpeed(d, sol.Prat3 / Math.Max(fprOf(sol.Prat3, sol.WT), 1.0),
                                            T0Sl, consts.RpmDesign);
                double etaBase = consts.EtaCoreDesign > 0.0 ? consts.EtaCoreDesign : tech.HpcEtaPol;
                // 衰减要"相对于设计转速"归一：设计点的换算转速未必正好是 100%
                // （涡扇转速是被风扇的功反推出来的），拿 nc=1 当锚点会让设计工况
                // 白白挨一刀效率罚，压比与流量一起偏低。
                double etaRef = CompressorPhysics.OffDesignEta(etaBase, Math.Max(consts.NcDesign, 0.05));
                double etaNew = etaRef > 1e-6
                    ? etaBase * CompressorPhysics.OffDesignEta(etaBase, ncPass) / etaRef
                    : CompressorPhysics.OffDesignEta(etaBase, ncPass);
                etaNew = Math.Max(Math.Min(etaNew, etaBase * 1.02), etaBase * 0.60);
                double step = etaNew - etaC;
                if (Math.Abs(step) < 0.0003) break;
                // 阻尼：直接代入在慢车附近会来回摆，权重 0.6 两三轮就到位。
                etaC += 0.6 * step;
                Sol s2 = balance(t4Cmd, sol.Prat3, maxIter);
                if (s2 == null) break;
                sol = s2;
            }

            // ---- 风扇效率也跟着转速掉（和压气机同一个道理，但回路是反的）----
            // 风扇的转速不是独立变量：它由"低压涡轮给了多少功"反推出来（欧拉功 u=√(ΔT·cp/ψ)），
            // 而效率又是转速的函数。所以只能定点迭代：当前压比 → 转速 → 效率 → 压比。
            // 方向是先验的：效率掉 → 同样轴功给出更低的压比 → 外涵速度低 → 部分功率更费油。
            // 设计点 n1=1 时 OffDesignEta 严格返回设计值，所以设计点一分不动。
            if (bpr > 0.0 && fc > 0.0 && d.Fan.Diameter > 0.0)
            {
                double fanRpmD; consts.RpmDesign.TryGetValue(d.Fan.Shaft, out fanRpmD);
                for (int pass = 0; pass < 5; pass++)
                {
                    double gfv = Gas.Gamma(tt1);
                    double fprNow = fprOf(sol.Prat3, sol.WT);
                    double uFan = Math.Sqrt(Math.Max(
                        (Math.Pow(fprNow, (gfv - 1.0) / (gfv * etaF)) - 1.0)
                        * Gas.Cp(tt1) * tt1 / (tech.FanPsi * Math.Max(d.Fan.Stages, 1)), 0.0));
                    double nFanNow = uFan * 60.0 / (Math.PI * d.Fan.Diameter);
                    double n1Frac = fanRpmD > 0.0 ? nFanNow / fanRpmD : 1.0;
                    double etaFanBase = consts.EtaFanDesign > 0.0 ? consts.EtaFanDesign : tech.FanEtaPol;
                    double etaFanNew = CompressorPhysics.OffDesignEta(etaFanBase, n1Frac);
                    double stepF = etaFanNew - etaF;
                    if (Math.Abs(stepF) < 0.0003) break;
                    etaF += 0.6 * stepF;
                    Sol s3 = balance(t4Cmd, sol.Prat3, maxIter);
                    if (s3 == null) break;
                    sol = s3;
                }
            }

            double theta = tt1 / T0Sl;
            double sqrtTheta = Math.Sqrt(Math.Max(theta, 1e-3));

            Func<Sol, double> tt2Of = s =>
            {
                if (bpr <= 0.0 || fc <= 0.0) return tt1;
                double wFanV = s.WT * fc / (1.0 + fc);
                return tt1 + Math.Max(wFanV, 0.0) / ((1.0 + bpr) * Gas.Cp(tt1));
            };
            Func<Sol, double> corePrOf = s => s.Prat3 / Math.Max(fprOf(s.Prat3, s.WT), 1.0);
            Func<Sol, double> ncOf = s => InvertSpeed(d, corePrOf(s), T0Sl, consts.RpmDesign);
            Func<Sol, double> nphysOf = s =>
            {
                double th = tt2Of(s) / T0Sl;
                return ncOf(s) * Math.Sqrt(Math.Max(th, 1e-3));
            };
            Func<double, double> ncLimitOfTt2 = tt2v =>
            {
                double th = Math.Max(tt2v / T0Sl, 1e-3);
                return Math.Min(tech.OverspeedLimit, tech.OverspeedLimit / Math.Sqrt(th));
            };

            double nc = ncOf(sol);
            double ncLimit = ncLimitOfTt2(tt2Of(sol));
            res.NCorrectedLimit = ncLimit;

            // ---- 换算转速调节器 ----
            if (nc > ncLimit * 1.002)
            {
                double oprLimCore = OprAtCorrectedSpeed(d, ncLimit, consts.RpmDesign);
                double lo = sol.T3 * 1.001, hi = t4Cmd;
                double guess = oprLimCore * Math.Max(fprOf(sol.Prat3, sol.WT), 1.0);
                Sol best = sol;
                for (int i = 0; i < 22; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    Sol s = balance(mid, guess, maxIter);
                    if (s == null) { hi = mid; continue; }
                    best = s;
                    guess = s.Prat3;
                    if (corePrOf(s) > oprLimCore) hi = mid; else lo = mid;
                }
                sol = best;
                res.Governed = true;
                res.T4Command = t4CmdRaw;
                res.Warnings.Add(string.Format(
                    Loc.T("换算转速顶到 {0:F0}% 上限：燃油量被转速调节器压到 T4={1:F0}K（指令 {2:F0}K），高空/大马赫下推不满", "Corrected speed hit the {0:F0}% ceiling: the governor pulled fuel back to T4={1:F0} K (asked {2:F0} K); high and fast, it cannot deliver full thrust"),
                    ncLimit * 100.0, sol.T4, t4CmdRaw));
                nc = ncOf(sol);
                ncLimit = ncLimitOfTt2(tt2Of(sol));
            }

            res.Iterations = sol.Iters;
            res.Converged = sol.Converged;
            if (!sol.Converged)
                res.Warnings.Add(string.Format(Loc.T("压比迭代 {0} 次未收敛（prat3={1:F2}）", "Pressure ratio did not converge in {0} sweeps (prat3={1:F2})"), sol.Iters, sol.Prat3));

            if (sol.Prat3 <= 1.001)
            {
                res.Ok = false;
                res.Windmill = true;
                res.Errors.Add(Loc.T("压比降到 1 以下：这个油门点核心机转不起来（风车状态）", "Pressure ratio fell below 1: the core will not turn at this throttle setting (windmilling)"));
                return res;
            }

            double t3v = sol.T3, t4v2 = sol.T4, farv = sol.Far, wtv = sol.WT;
            double p3 = pt1 * sol.Prat3;
            double pt4 = p3 * consts.BurnerSigma * consts.DuctEtaCore;
            double t5 = t4v2 - wtv / Math.Max(Gas.Cp(t4v2), 1e-6);
            res.Ttr = t4v2 > 0.0 ? t5 / t4v2 : consts.Ttr;
            res.Opr = sol.Prat3; res.T3 = t3v; res.P3 = p3;
            res.T4 = t4v2; res.Far = farv; res.T5 = t5;

            // 富油：用未截断的油气比判断
            if (t4v2 > t3v + 1.0)
            {
                double cp3 = Gas.Cp(t3v), cp4 = Gas.Cp(t4v2);
                double dT = t4v2 - t3v;
                double den = Gas.LhvJet * consts.BurnerEta - cp4 * dT;
                if (den > 0.0)
                {
                    double farRaw = cp3 * dT / den;
                    if (farRaw > FarRichLimit)
                    {
                        res.Warnings.Add(string.Format(
                            Loc.T("油气比 f={0:F3} 超过可燃上限 {1:F3}：这段温升实际烧不出来，T4 被截断", "Fuel/air ratio f={0:F3} is past the flammable limit {1:F3}: that much temperature rise cannot be burned, so T4 was capped"),
                            farRaw, FarRichLimit));
                        // 以前这里只给 Warning 就把 Ok 置 false：面板说"不成立"，
                        // 却一条错误都列不出来，玩家/回归都只能对着空气猜。理由必须落在 Errors 里。
                        res.Errors.Add(string.Format(Loc.T("富油：油气比 f={0:F3} 超过上限 {1:F3}，这段温升烧不出来", "Rich: fuel/air ratio f={0:F3} is past the limit {1:F3}; that temperature rise cannot be burned"),
                            farRaw, FarRichLimit));
                        res.Ok = false;
                    }
                }
            }

            // ---- 流量：换算流量律，涡轮导向器只当上限 ----
            double delta = pt1 / P0Sl;
            double wcFrac = WcCapacityFrac(nc) / Math.Max(WcCapacityFrac(consts.NcDesign), 1e-3);
            double mdotAirCorr = consts.MdotDAir * (delta / sqrtTheta) * wcFrac * flowCapScale
                * inl.FlowFactor;   // 抽气：抽掉的气不参与做功，流量直接打折（缺省 1.0）
            if (mdotAirCorr <= 0.0)
            {
                res.Ok = false;
                res.Errors.Add(Loc.T("换算流量为零：进不去气", "Corrected flow is zero: no air is getting in"));
                return res;
            }

            // ---- 进气道：喉道能过多快就是多快 ----
            // 喉道是实体尺寸，低速时面马赫低、需求流量小，喉道根本不是瓶颈；
            // 冲压把总压顶上去以后需求流量涨，超过喉道壅塞流量时只能按壅塞值走，
            // 多出来的空气从唇口溢流出去（真实机就是靠这个溢流阻力拖住飞机的）。
            // 这个夹子只在超限时生效：缺省轴对称构型在常用包线内一律不触发，
            // 所以老存档的每一档读数都不会变。
            IntakeSolution intk = fromPart
                ? partSol
                : IntakeSolver.SolveAt(d.Intake, d, alt, mach, 0.0, double.NaN);
            res.IntakeTpr = intk.Tpr;
            res.IntakeCapacity = intk.AvailableMassFlow;
            if (intk.Valid && intk.AvailableMassFlow > 0.0 && mdotAirCorr > intk.AvailableMassFlow * 1.0005)
            {
                res.IntakeLimited = true;
                res.Warnings.Add(string.Format(
                    Loc.T("进气道喉道限流：这个工况想吞 {0:F1} kg/s，喉道只放过 {1:F1} kg/s（M={2:F2}，面马赫 {3:F2}，溢流 {4:F2} kg/s）", "Intake throat limits the flow: this condition wants {0:F1} kg/s, the throat passes {1:F1} kg/s (M={2:F2}, face Mach {3:F2}, spillage {4:F2} kg/s)"),
                    mdotAirCorr, intk.AvailableMassFlow, mach, intk.FaceMach, intk.SpillageMassFlow));
                mdotAirCorr = intk.AvailableMassFlow;
            }
            double mdotCore = mdotAirCorr / (1.0 + bpr);
            double mdotCoreChoke = ChokedMassFlow(consts.ATurb, pt4, t4v2);
            if (mdotCoreChoke > 0.0 && mdotCore > mdotCoreChoke)
            {
                double ratio = mdotCore / mdotCoreChoke;
                mdotCore = mdotCoreChoke;
                mdotAirCorr = mdotCore * (1.0 + bpr);
            }
            if (mdotCore <= 0.0)
            {
                res.Ok = false;
                res.Errors.Add(Loc.T("涡轮流量为零", "Turbine flow is zero"));
                return res;
            }
            double mdotAir = mdotAirCorr;
            double bprNow = bpr;
            res.MdotCore = mdotCore; res.MdotAir = mdotAir; res.Bpr = bprNow; res.WcFrac = wcFrac;

            // ---- 风扇 ----
            double fpr = 1.0, tt2 = tt1, pt2 = pt1;
            double bprUse = bpr;
            if (bpr > 0.0)
            {
                double wFanTotal = wtv * fc / (1.0 + fc);
                double gf = Gas.Gamma(tt1);
                // 这里以前是"涡轮分给风扇多少功、除以设计涵道比"，等于假设低油门时
                // 旁路流量一点不变。§46.3 量出来的缺口就是这个：10% 油门时风扇转速只有
                // 74.8%，流量函数说该少吹 21%，我们却仍按 100% 吹。
                // 改成分两步走，让压比、转速、流量三者互相咬合：
                //   功 → 温升 → 压比 → 反查转速 → 流量函数 → 新的涵道比 → 再算温升
                // 转三轮就够（第一轮用设计涵道比起步，阻尼 0.7 防摆）。
                for (int k = 0; k < 3; k++)
                {
                    double dTFan = wFanTotal / ((1.0 + bprUse) * Gas.Cp(tt1));
                    tt2 = tt1 + dTFan;
                    fpr = Math.Pow(tt2 / tt1, gf * tech.FanEtaPol / (gf - 1.0));
                    double n1Map = FanMap.SpeedForPressureRatio(consts.FprD, fpr);
                    double bprNew = bpr * FanMap.FlowFrac(n1Map);
                    if (Math.Abs(bprNew - bprUse) < 0.01) { bprUse = bprNew; break; }
                    bprUse = bprUse + 0.7 * (bprNew - bprUse);
                }
                {
                    double dTFan = wFanTotal / ((1.0 + bprUse) * Gas.Cp(tt1));
                    tt2 = tt1 + dTFan;
                    fpr = Math.Pow(tt2 / tt1, gf * tech.FanEtaPol / (gf - 1.0));
                }

                double fanRpmD; consts.RpmDesign.TryGetValue(d.Fan.Shaft, out fanRpmD);
                if (fanRpmD > 0.0 && d.Fan.Diameter > 0.0)
                {
                    double n1Lim = tech.OverspeedLimit * sqrtTheta;
                    double uLimFan = Math.PI * d.Fan.Diameter * fanRpmD * n1Lim / 60.0;
                    double fprMax = Math.Pow(
                        1.0 + tech.FanPsi * d.Fan.Stages * uLimFan * uLimFan / (Gas.Cp(tt1) * tt1),
                        gf * tech.FanEtaPol / (gf - 1.0));
                    if (fpr > fprMax)
                    {
                        fpr = Math.Max(fprMax, 1.0);
                        tt2 = tt1 * Math.Pow(fpr, (gf - 1.0) / (gf * tech.FanEtaPol));
                        res.FanSpeedLimited = true;
                        res.Warnings.Add(string.Format(
                            Loc.T("风扇换算转速顶到 {0:F0}% 上限：风扇压比被夹到 {1:F2}，旁路射流速度下降", "Fan corrected speed hit the {0:F0}% ceiling: fan pressure ratio clipped to {1:F2}, so the bypass jet slows down"),
                            tech.OverspeedLimit * 100.0, fpr));
                    }
                }
                pt2 = pt1 * fpr;
            }
            if (bpr > 0.0)
            {
                // 旁路流量按风扇自己的流量函数定：转速低就少吹空气。
                // 核心流量不动（它由压气机容量与涡轮喉道定），所以涵道比会随油门变——
                // 这是真机的行为，也是 §46.3 量出来"低油门多吹 26% 空气"的修法。
                double n1Fin = FanMap.SpeedForPressureRatio(consts.FprD, fpr);
                bprUse = bpr * FanMap.FlowFrac(n1Fin);
                mdotAir = mdotCore * (1.0 + bprUse);
                bprNow = bprUse;
                res.MdotAir = mdotAir; res.Bpr = bprNow;
            }
            res.Fpr = fpr; res.T2 = tt2;

            res.WComp = mdotCore * Gas.Cp(tt1) * (t3v - tt1);
            res.WFan = mdotAir * Gas.Cp(tt1) * (tt2 - tt1);

            // ---- 加力 ----
            double t7 = t5, fuelAb = 0.0, farAbUsed = 0.0;
            if (d.Afterburner.Enabled && (abFrac > 0.0 || abFuelRatio > 0.0))
            {
                double tMax = Math.Min(Math.Max(tech.T4Limit + 400.0, 1800.0), 2100.0);
                if (d.Afterburner.ExitTemp > 0.0) tMax = Math.Min(tMax, d.Afterburner.ExitTemp);
                if (abFuelRatio > 0.0)
                {
                    double farAb = Math.Min(abFuelRatio, 0.045);
                    t7 = t5 + farAb * Gas.LhvJet * 0.95 / Gas.Cp(t5) / (1.0 + farAb);
                    t7 = Math.Min(t7, tMax);
                }
                else
                {
                    t7 = t5 + Math.Min(Math.Max(abFrac, 0.0), 1.0) * (tMax - t5);
                }
                double dT7 = t7 - t5;
                if (dT7 > 0.0)
                {
                    double den = Gas.LhvJet * 0.95 - Gas.Cp(t7) * dT7;
                    if (den > 0.0)
                    {
                        farAbUsed = Gas.Cp(t5) * dT7 / den;
                        fuelAb = mdotCore * (1.0 + farv) * farAbUsed;
                    }
                }
            }
            res.FarAb = farAbUsed;
            res.T7 = t7;
            res.FuelFlow = mdotCore * farv + fuelAb;

            // ---- 喷管与推力 ----
            // 涡轮后 → 喷管喉道：排气锥那一截。长度在这台机器上第三次吃到亏
            // （设计点里扣过一次，非设计点按同一个乘子扣）。
            double p5 = pt4 / Math.Max(sol.PiT, 1.0) * consts.DuctEtaExhaust;
            res.P5 = p5;
            res.TurbineNozzleLimited = sol.TurbineLimited;

            double mCoreExh = mdotCore * (1.0 + farv);
            // 加力一开，涡轮后总温从 900 K 级升到 1900 K 级——同样的喉道过不了这么多流量。
            // 真机靠液压把 A8 撑开 20~35%，固定收敛喷管没有这个能力，只能憋着烧。
            // 注意加力档位有两个来源：abFrac（按温度插值）和 abFuelRatio（按油气比定）。
            // 喷口调度只认前者会让"按油气比开的加力"看起来不需要撑开喷口，两边要统一。
            //
            // 第三个来源是 a8Frac：喉道作动筒跟不上燃油。真机按下加力，燃油几百毫秒就建立，
            // A8 要撑开要一到两秒——中间那一段是"富油+喷口还小"，涡轮后压力憋高、推力先冲一下
            // 再回落到稳态。所以面积这一份允许由调用方单独给（`-1` = 跟燃油同档）。
            double abFracArea = a8Frac >= 0.0 ? a8Frac : abFrac;
            if (abFracArea <= 0.0 && farAbUsed > 0.0)
                abFracArea = Math.Min(1.0,
                    farAbUsed / Math.Max(AfterburnerLight.EffectiveFar(d.Afterburner), 1e-9));
            double a8 = NozzleSchedule.ThroatAreaInstalled(d, consts, abFracArea, nc, t7,
                ControlSystem.Effective(d));
            // 装上去的面积由作动筒位置定；这股气"需要"多大是另一件事（A8_干·√(T7/T7干)）。
            // 两者差得多就说明喷口开得不对——开大了落压比掉、压力推力白丢，
            // 开小了喉道过不去这股气。这里把比值报出来，飞行时能看见、审计也能查。
            double a8Need = NozzleSchedule.ThroatAreaNeed(d, consts, nc, t7);
            res.A8Used = a8; res.A8FracUsed = abFracArea;
            res.A8Need = a8Need;
            res.A8OpenRatio = a8Need > 1e-9 ? a8 / a8Need : 0.0;
            if (NozzleSchedule.CanVary(d.Nozzle) && res.A8OpenRatio > 1.15)
                res.Warnings.Add(string.Format(
                    Loc.T("喷口开得比需要的大 {0:P0}（A8 {1:F4} 对需要 {2:F4}）：落压比掉了，压力推力白丢。加力油气比没跟上时就会这样", "The nozzle is {0:P0} open beyond what is needed (A8 {1:F4} against {2:F4}): pressure ratio drops and pressure thrust is thrown away. Happens when the afterburner fuel does not keep up"),
                    res.A8OpenRatio - 1.0, a8, a8Need));
            Gas.NozzleState nz = Gas.NozzleFlow(mCoreExh, p5, t7, p0, tech.NozzleEta,
                                                d.Nozzle, a8, consts.NozzleAreaRatio);
            double vCore = nz.Ve, fCoreGross = nz.Thrust;
            double fCore = fCoreGross - mdotCore * v0;
            res.ChokedNozzle = nz.Choked;
            res.NozzleSeparated = nz.Separated;
            res.NozzleBackPressure = nz.BackPressure;
            res.NozzleAreaRatio = consts.NozzleAreaRatio;
            res.NozzleExitArea = nz.Ae;
            res.NozzleMachE = nz.MachE;
            res.NozzleAeMismatch = nz.AeMismatch;

            // ---- 排气喷管的通过能力，也是一道限流 ----
            // 这里的顺序换过一次。原来是"只让涡轮导向器限流，喷管面积只决定排气速度"，
            // 结果把 A8 撑开对我们毫无影响（Variable 与 Converging 推力一模一样）。
            // 真机上撑开 A8 有用，是因为它回压到涡轮、把整台机推到更大的流量点。
            // 所以现在补上：喷管过不了这么多流量，就把进气量压下去重解一次。
            // 容差 2%：设计点按 A8 反解时 cp/γ 的取法差一点，喷管"刚好匹配"会落在 1~2% 这个量级。
            // 拿 0.5% 当门会连设计点都判"过不去"，然后满包线反复重解。
            double a8Cap = Gas.ChokedFlow(a8, p5, t7, 0.0);
            if (capPasses < 2 && a8Cap > 1e-6 && mCoreExh > a8Cap * 1.02)
            {
                double need = a8Cap / mCoreExh;
                // 阻尼 0.8：喷管容量又依赖解出来的 p5/T7，一把踩到底会来回跳。
                double step = 1.0 + (need - 1.0) * 0.8;
                if (step < 0.5) step = 0.5;
                OffDesignResult capped = Solve(d, alt, mach, throttle, abFrac, consts, maxIter,
                                               t4Override, abFuelRatio,
                                               flowCapScale * step, capPasses + 1, null, a8Frac);
                capped.NozzleCapped = true;
                capped.Warnings.Insert(0, string.Format(
                    Loc.T("排气喷管限流：要过 {0:F1} kg/s，喉道 {1:F4} m² 在 p5={2:F0} kPa、T7={3:F0} K 下只能过 {4:F1} kg/s，进气量压到 {5:P0} 重解", "Exhaust nozzle limits the flow: asked for {0:F1} kg/s, a {1:F4} m2 throat passes {4:F1} kg/s at p5={2:F0} kPa and T7={3:F0} K; inlet flow was cut to {5:P0} and resolved"),
                    mCoreExh, a8, p5 / 1000.0, t7, a8Cap, flowCapScale * step));
                return capped;
            }
            if (nz.BackPressure)
                res.Warnings.Add(string.Format(
                    Loc.T("固定收敛喷管憋住了：要过 {0:F1} kg/s，喉道 {1:F4} m² 只能过 {2:F1} kg/s。加力时推力会被低估，应选可变面积或收扩喷管。", "A fixed converging nozzle blocks the flow: it must pass {0:F1} kg/s but a {1:F4} m2 throat takes {2:F1} kg/s. Thrust is understated with the afterburner lit; use a variable or converging-diverging nozzle."),
                    mCoreExh, a8,
                    Gas.ChokedFlow(a8, p5, t7, 0.0)));

            double vByp = 0.0, fByp = 0.0;
            if (bprNow > 0.0)
            {
                double mByp = mdotAir * (bprNow / (1.0 + bprNow));
                double vB, aeB, peB, fBypGross;
                bool cb;
                Gas.Nozzle(mByp, pt2, tt2, p0, tech.NozzleEta,
                           out vB, out aeB, out peB, out fBypGross, out cb);
                vByp = vB;
                fByp = fBypGross - mByp * v0;
            }

            res.VJetCore = vCore; res.VJetBypass = vByp;
            res.ThrustCore = fCore; res.ThrustBypass = fByp;
            res.Thrust = fCore + fByp;

            double dFront = d.Fan.Diameter > 0.0 ? d.Fan.Diameter : d.InletDiameter;
            double aFront = Math.PI * Math.Pow(dFront / 2.0, 2);
            res.RamDrag = 0.5 * (p0 / (Gas.R * t0)) * v0 * v0 * aFront * NacCd;
            res.NetThrust = res.Thrust - res.RamDrag;
            // 净推力 ≤0（风车、或冲压阻力吃掉全部推力）时 SFC 没有定义：
            // 给 0 而不是 Infinity——Infinity 会顺着 realIsp = 3600/(sfc*g0) 变成 0，
            // 还会污染网格的 JSON/CSV。是否"没推力"由 NetThrust 自己表达。
            res.Sfc = res.NetThrust > 0.0
                ? res.FuelFlow * 3600.0 / Math.Max(Math.Abs(res.NetThrust), 1e-9)
                : 0.0;

            // ---- 转速（解完再反推）----
            double nphys = nc * Math.Sqrt(Math.Max(tt2Of(sol) / T0Sl, 1e-3));
            res.NCorrected = nc;
            res.Rpm = new Dictionary<int, double>();
            foreach (KeyValuePair<int, double> kv in consts.RpmDesign)
                res.Rpm[kv.Key] = kv.Value * nphys;
            if (bpr > 0.0 && d.Fan.Diameter > 0.0)
            {
                double g = Gas.Gamma(tt1);
                double uFan = Math.Sqrt(Math.Max(
                    (Math.Pow(fpr, (g - 1.0) / (g * etaF)) - 1.0)
                    * Gas.Cp(tt1) * tt1 / (tech.FanPsi * Math.Max(d.Fan.Stages, 1)), 0.0));
                double nFan = uFan * 60.0 / (Math.PI * d.Fan.Diameter);
                res.Rpm[d.Fan.Shaft] = nFan;
                double fanRpmDesign; consts.RpmDesign.TryGetValue(d.Fan.Shaft, out fanRpmDesign);
            }

            // ---- 逐轴涡轮功平衡核对（每根轴都要问一句"带得动吗"）----
            // 三根轴共用一个转速比例是已知简化：没有压气机特性线时，三根轴的独立转速欠定。
            // 但"每根轴要多少功"和"每根轴最多能挤出多少功"这两件事都能算准，
            // 于是可以逐台核对——某一根轴带不动时，真实发动机的表现就是掉转、掉功率。
            // **以前这段只在三轴机或有中压涡轮时才跑**，理由是"三轴机的 IPT 不是装饰品"，
            // 结果双轴机（绝大多数涡喷/涡扇）在飞行时压根不问这句话：§195 修好的容量上限
            // 只写进了设计点，飞行那套数里等于没有。轴数不是要不要核对的前提。
            CheckSpoolBalance(d, res, tech, consts, mdotCore * (1.0 + farv), t4v2);

            // ---- 逐轴转速读数：N1/N2/N3 ----
            // 核心轴（拖压气机的那几根）在模型里共用同一个换算转速比例，但**物理转速不同**
            // （每根轴的设计转速由自己叶轮的最大叶尖马赫定出来）。带不动的那根轴再按
            // 「功 ∝ U² ∝ N²」掉转：因子 = sqrt(供/需)。扇轴不叠加这个因子——它的转速
            // 本来就是从风扇欧拉功反推的，LPT 供不上已经体现在 fpr 里了，再乘一次是重复扣。
            if (res.Rpm == null) res.Rpm = new Dictionary<int, double>();
            if (res.SpoolShortfall == null) res.SpoolShortfall = new Dictionary<int, double>();
            res.SpeedPct = new Dictionary<int, double>();
            res.TipSpeed = new Dictionary<int, double>();
            int fanShaftNow = d.HasFan ? d.Fan.Shaft : -1;
            // 先取键的快照再回写：Dictionary 的枚举器盯着 version，边枚举边给已有键赋值
            // 会当场抛 InvalidOperationException（"Collection was modified"）。
            List<int> shaftKeys = new List<int>(res.Rpm.Keys);
            foreach (int sh in shaftKeys)
            {
                double rpmD; consts.RpmDesign.TryGetValue(sh, out rpmD);
                double rpm = res.Rpm[sh];
                if (sh != fanShaftNow && res.SpoolShortfall.ContainsKey(sh))
                    rpm *= res.SpoolShortfall[sh];
                res.Rpm[sh] = rpm;
                res.SpeedPct[sh] = rpmD > 0.0 ? 100.0 * rpm / rpmD : 0.0;
                res.TipSpeed[sh] = ShaftTipSpeed(d, sh, rpm);
            }
            Dictionary<string, double> named = Cycle.SpoolReadout(d, res.Rpm, res.SpeedPct);
            res.Spool = named;
            res.N1Pct = ReadNamed(named, "N1%");
            res.N2Pct = ReadNamed(named, "N2%");
            res.N3Pct = ReadNamed(named, "N3%");

            // 物理转速超 105% 时把话说清楚（不改数）：部件特性按换算转速限，
            // 高空高速来流总温高，物理转速本来就会高过 100%。但真机还有一道
            // 物理超转限油，本模型没有——所以要明确告诉玩家"这不是限住了"。
            foreach (KeyValuePair<int, double> kv in res.SpeedPct)
            {
                if (kv.Value > 105.0 && GasState.IsFinite(kv.Value))
                    res.Warnings.Add(string.Format(
                        Loc.T("轴 {0} 物理转速 {1:F1}%（设计点 100%）：模型限的是换算转速 N/√θ，真机会限油；长时间这样飞等于超转", "Shaft {0} physical speed {1:F1}% (100% at the design point): what this model limits is corrected speed N/sqrt(theta); a real engine cuts fuel instead. Flying like this for long is overspeeding."),
                        kv.Key, kv.Value));
            }

            // ---- 判据 ----
            if (t4v2 > tech.T4Limit + 1.0)
            {
                res.Errors.Add(string.Format(Loc.T("需要 T4={0:F0}K，超过材料上限 {1:F0}K", "T4={0:F0} K is needed, past the material limit {1:F0} K"), t4v2, tech.T4Limit));
                res.Ok = false;
            }
            if (p5 < p0 * 0.99)
            {
                res.Warnings.Add(string.Format(
                    Loc.T("涡轮出口总压 {0:F1} kPa 低于环境 {1:F1} kPa：喷管倒流，该油门点维持不住", "Turbine exit total pressure {0:F1} kPa is below ambient {1:F1} kPa: the nozzle reverses and this throttle setting cannot hold"),
                    p5 / 1000.0, p0 / 1000.0));
                // 同样的问题：只给告警就置 Ok=false，结果"不成立"却列不出理由。
                res.Errors.Add(string.Format(Loc.T("喷管倒流：涡轮出口总压 {0:F1} kPa 低于环境 {1:F1} kPa", "Nozzle reversed: turbine exit total pressure {0:F1} kPa is below ambient {1:F1} kPa"),
                    p5 / 1000.0, p0 / 1000.0));
                res.Ok = false;
            }
            else if (p5 < p0)
            {
                // 涡轮出口已经压到环境压力（TurbineExit 的 piMax 就是 pt4/p0），喷管里再没有
                // 压差可用。但这不代表喷流消失：固定喉道下，"这么多流量从这么大面积出去"
                // 本身就带着速度。原来这里按压力比 1 算，慢车时核心机给 0 m/s，
                // 推力全靠外涵——那是模型算没了，不是物理没了。
                res.P5 = p0;
                Gas.NozzleState nz2 = Gas.NozzleFlow(mCoreExh, p0, t7, p0, tech.NozzleEta,
                                                     d.Nozzle, a8, consts.NozzleAreaRatio);
                res.VJetCore = nz2.Ve;
                res.ThrustCore = nz2.Thrust - mdotCore * v0;
                res.ChokedNozzle = nz2.Choked;
                res.NozzleSeparated = nz2.Separated;
                res.NozzleMachE = nz2.MachE;
                res.Thrust = res.ThrustCore + fByp;
                res.NetThrust = res.Thrust - res.RamDrag;
                res.Sfc = res.NetThrust > 0.0
                    ? res.FuelFlow * 3600.0 / Math.Max(Math.Abs(res.NetThrust), 1e-9)
                    : 0.0;
            }
            if (res.NetThrust < 0.0)
                res.Warnings.Add(Loc.T("净推力为负：进气道冲压阻力超过可用推力", "Net thrust is negative: intake ram drag exceeds available thrust"));

            res.SelfSustaining = res.Ok && sol.Converged && res.NetThrust > 0.0
                                 && t4v2 <= tech.T4Limit && !res.Windmill;
            return res;
        }
    }
}