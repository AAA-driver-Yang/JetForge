using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 一次进气道求解的完整产出。**这是进气道作为一个独立部件的对外接口**：
    /// 三块面积各自的、实际流量、溢流、抽气、总压恢复、限流状态、告警。
    ///
    /// 以前 `IntakeState` 只有 Tpr/FaceMach/ThroatArea/CaptureArea/FlowFactor 五个数，
    /// 也从不判失败；于是"进气道到底通过了多少气、有没有被喉道憋住、溢流去哪儿了"
    /// 这三个问题在模型里没有答案——下游拿到的只是一个打折系数。
    /// </summary>
    public sealed class IntakeSolution
    {
        public bool Valid = true;
        public string FailureCode = "";
        public List<string> Warnings = new List<string>();

        // ---- 来流 ----
        public double Altitude, Mach;
        public double AmbientPressure, AmbientTemperature;
        public double FlightVelocity;
        public double Pt0, Tt0;

        // ---- 发动机进口（进气道出口）----
        public double Tpr;              // Pt2/Pt0
        public double Pt2, Tt2;
        public double FaceMach;
        public double FaceArea;         // 发动机进口截面 m²

        // ---- 三块面积：唇口 / 喉道 / 发动机进口 ----
        public double CaptureArea;      // 唇口捕获面积
        public double ThroatArea;       // 流道最小截面（限流）
        public double AreaRatioThroatFace;

        // ---- 流量 ----
        public double CapturedMassFlow;    // 从唇口进到管道里的
        public double SpillageMassFlow;    // 进不来、从唇口外溢掉的
        public double BleedMassFlow;       // 抽走（不参与推力）
        /// <summary>迎角与侧滑合成出的横向角（度）。0 = 正对来流。</summary>
        public double CrossflowDeg;
        public double AvailableMassFlow;   // 给发动机的
        public double RequiredMassFlow;    // 按面马赫算"发动机要吃的"
        /// <summary>这台发动机这一面在本次工况能吃多少（多件进气道时的上限，也是"需求"那个数）。</summary>
        public double DemandMassFlow;
        /// <summary>老口径给设计点用的流量：喉道按 **来流总压** 算，缺省逐位与旧代码相同。</summary>
        public double LegacyMassFlow;

        /// <summary>
        /// 这个流量是不是被"发动机进口面"削过（§284）。
        /// 独立进气道做得比发动机进口还大时，多出来的气只能在唇口溢流——
        /// 这个标志让界面能说清楚"你把它做大了，但你并没有因此多吃到空气"。
        /// </summary>
        public bool FlowCappedByEngineFace;

        /// <summary>上面那个上限是多少（kg/s）。0 = 这次没算上限（调用方没给发动机面）。</summary>
        public double EngineFaceCapMassFlow;

        public double ThroatMargin;     // 1 - Required/ThroatCap
        public bool ThroatChoked;
        public bool Spilling;
        public bool Started = true;
        public bool Unstarted;
        public double InletDrag;         // 溢流阻力 N（动量上界）
        public double DistortionIndex;   // 0~0.35，经验值

        /// <summary>旧 FiveField 口径的五个数：老接口还得能拿到。</summary>
        public double FlowFactor { get { return CapturedMassFlow > 1e-9 ? AvailableMassFlow / CapturedMassFlow : 1.0; } }

        public void Warn(string s) { Warnings.Add(s); }

        public bool Ok { get { return Valid && FailureCode.Length == 0; } }

        public string Describe()
        {
            return string.Format(
                Loc.T("M{0:F2} TPR={1:F4} 面M={2:F2} 面积[唇{3:F4} 喉{4:F4} 面{5:F4}] 流量[捕获{6:F2} 可用{7:F2} 溢流{8:F2}] {9}{10}", "M{0:F2} | recovery {1:F4} | face Mach {2:F2} | areas lip {3:F4} / throat {4:F4} / engine face {5:F4} m2 | flows captured {6:F2} / available {7:F2} / spilled {8:F2} kg/s | {9}{10}"),
                Mach, Tpr, FaceMach, CaptureArea, ThroatArea, FaceArea,
                CapturedMassFlow, AvailableMassFlow, SpillageMassFlow,
                ThroatChoked ? Loc.T("喉道壅塞", "throat choked") : (Spilling ? Loc.T("溢流", "spilling") : Loc.T("正常", "nominal")),
                CrossflowDeg > 0.01 ? string.Format(Loc.T("｜横向角 {0:F0}°", "｜crossflow {0:F0} deg"), CrossflowDeg) : "");
        }
    }

    /// <summary>
    /// 进气道求解器。**完全独立**：不读 Cycle/OffDesign 的任何中间量，
    /// 只吃"型式 + 三块面积 + 来流 + 面马赫 + 抽气"，吐一个 IntakeSolution。
    ///
    /// 三块面积不许互相顶替（这是上一轮最容易埋的坑）：
    /// · `CaptureArea` 唇口投影面积——决定"能兜住多少来流"，只进外形/溢流；
    /// · `ThroatArea` 流道最小截面——决定"能过多快"，是限流的唯一位置；
    /// · `FaceArea` 发动机进口截面——决定"发动机想吃多少"。
    ///
    /// 缺省口径（老存档）下 `LegacyMassFlow` 与旧 `Intake.Evaluate` + `Cycle.MassFlow`
    /// 逐位相同，所以设计点一分不动；新口径 `AvailableMassFlow` 给非设计点做限流与诊断。
    /// </summary>
    public static class IntakeSolver
    {
        /// <summary>面马赫的物理范围。低于 0.2 说明玩家把"减速增压"调成了"堵住"。</summary>
        public const double FaceMachMin = 0.20;
        public const double FaceMachMax = 1.00;

        /// <summary>
        /// 这台发动机自己那张面想把气减到多少马赫：填了 `FaceMachDesign` 就用它，
        /// 没填按型式给缺省，再夹进可解区间。
        ///
        /// 抽成一个函数是因为"发动机这一面能通过多少流量"有两处要问：一处是它自带进气道，
        /// 一处是**装独立进气道零件时给流量封顶**。两处各写一遍表达式，迟早一处改了另一处没改，
        /// 于是同一台发动机换个进气道就换了个面马赫——那种账查起来最费劲。
        /// </summary>
        public static double FaceMachOf(IntakeSpec spec)
        {
            double m = spec != null && spec.FaceMachDesign > 0.0
                ? spec.FaceMachDesign
                : IntakeSpec.DefaultFaceMach(spec != null ? spec.Kind : IntakeKind.Axial);
            return Math.Min(Math.Max(m, FaceMachMin), FaceMachMax);
        }

        /// <summary>由整机设计取来流条件求解（设计点/非设计点/界面读数共用这一个入口）。</summary>
        public static IntakeSolution Solve(EngineDesignData d, double mach, double faceMachOverride,
                                           double altOverride = double.NaN, double bleedOverride = double.NaN)
        {
            if (d == null)
            {
                IntakeSolution bad = new IntakeSolution();
                bad.Valid = false;
                bad.FailureCode = "NO_DESIGN";
                return bad;
            }
            double alt = GasState.IsFinite(altOverride) ? altOverride : d.DesignAlt;
            IntakeSpec spec = d.Intake != null ? d.Intake.Resolved() : new IntakeSpec().Resolved();
            double bleed = GasState.IsFinite(bleedOverride) ? bleedOverride : spec.BleedFrac;
            return SolveAt(spec, d, alt, mach, faceMachOverride, bleed);
        }

        public static IntakeSolution SolveAt(IntakeSpec spec, EngineDesignData d, double alt, double mach,
                                             double faceMachOverride, double bleed,
                                             double captureAreaOverride = double.NaN,
                                             double throatAreaOverride = double.NaN,
                                             double faceAreaOverride = double.NaN,
                                             TechLevel techOverride = null,
                                             double ductLoss = 1.0)
        {
            IntakeSolution s = new IntakeSolution();
            if (spec == null) { spec = new IntakeSpec().Resolved(); }
            // 抽气比例没给（NaN）就按型式缺省：以前不挡这一下，NaN 会一路传染成"可用流量 NaN"，
            // 于是限流判据永远不成立——面板上看着一切正常，实际上那条物理链断了。
            if (!GasState.IsFinite(bleed)) bleed = spec.BleedFrac;

            // ---- 输入合法性：进气道以前无条件 Ok，非法值会一路污染到推力 ----
            if (!GasState.IsFinite(alt) || alt < -500.0 || alt > 40000.0)
            {
                s.Valid = false; s.FailureCode = "ALT_RANGE";
                s.Warn(Loc.T("高度超出进气道模型范围（-0.5~40 km）", "Altitude is outside the intake model range (-0.5 to 40 km)"));
                return s;
            }
            if (!GasState.IsFinite(mach) || mach < 0.0 || mach > 4.0)
            {
                s.Valid = false; s.FailureCode = "MACH_RANGE";
                s.Warn(Loc.T("马赫数超出进气道模型范围（0~4）", "Mach is outside the intake model range (0 to 4)"));
                return s;
            }

            s.Altitude = alt;
            s.Mach = mach;

            double p0, t0;
            Gas.Isa(alt, out p0, out t0);
            s.AmbientPressure = p0;
            s.AmbientTemperature = t0;
            s.FlightVelocity = mach * Gas.SoundSpeed(t0);

            double pt0, tt0;
            if (mach > 0.0) Gas.Total(p0, t0, mach, out pt0, out tt0);
            else { pt0 = p0; tt0 = t0; }
            s.Pt0 = pt0; s.Tt0 = tt0;

            // ---- 恢复系数：老口径原封不动，只在这里叠一层"管道弯了多少"的损失 ----
            // 设计马赫要递给恢复函数：真激波模式下压缩角由**设计**马赫定（几何是死的），
            // 不能拿当前马赫当几何角，否则同一台机器在不同速度上会"变形"。
            double designMach = Intake.DesignMachOf(spec, d != null ? d.DesignMach : double.NaN);
            double tpr = Intake.Recovery(spec, techOverride != null ? techOverride
                                                           : (d != null ? d.Tech : TechLevel.Get("1970s")), mach, designMach);
            double bend = Math.Min(Math.Max(spec.DuctBendDeg, 0.0), 60.0);
            // 每 10° 弯约 0.8% 总压损失（S 形进气道是真实存在的坑，不是凑数）
            double bendLoss = BendLoss(bend);
            tpr *= bendLoss;
            // ---- 横向角：机翼要升力就有迎角，进气道几乎从不正对来流 ----
            // 缺省 0° 时 crossLoss 正好是 1.0，乘一下读数不变（下面畸变那条同理）。
            double cross = Intake.CrossflowDeg(spec.AlphaDeg, spec.BetaDeg);
            double crossLoss = Intake.IncidenceLossFactor(spec.Kind, cross);
            tpr *= crossLoss;
            // ---- 管道本身的损失：又长又细的进气道真的更吃亏 ----
            // 只有独立零件会给这个数（它知道自己多长）；整机设计里长度是外形参数，
            // 传 1.0 就是老口径，一个数都不动。
            if (GasState.IsFinite(ductLoss) && ductLoss > 0.0 && ductLoss < 1.0) tpr *= ductLoss;
            tpr = Math.Min(Math.Max(tpr, 0.35), 0.999);
            s.CrossflowDeg = cross;
            s.Tpr = tpr;
            s.Pt2 = pt0 * tpr;
            s.Tt2 = tt0;

            // ---- 三块面积：默认由整机设计推；独立进气道零件会把自己的三块面积直接递进来 ----
            s.ThroatArea = GasState.IsFinite(throatAreaOverride) ? throatAreaOverride : Intake.ThroatAreaOf(d);
            // ---- 可调几何：锥体前移把喉道环收窄 ----
            // 真机的可调中心锥就是干这个的：超声速把锥往前推，喉道小一点、
            // 激波位置对得上，进气道才起得动。不打开这个开关时因子恒为 1，
            // 所有老读数一个数都不动。
            double vgFactor = Intake.ThroatFactor(spec, mach);
            if (vgFactor < 0.9999) s.ThroatArea *= vgFactor;
            s.CaptureArea = GasState.IsFinite(captureAreaOverride) ? captureAreaOverride : Intake.CaptureAreaOf(d);
            double faceD = d != null ? d.EngineFaceDiameter() : 1.0;
            s.FaceArea = GasState.IsFinite(faceAreaOverride) ? faceAreaOverride : Math.PI * 0.25 * faceD * faceD;
            s.AreaRatioThroatFace = s.FaceArea > 1e-9 ? s.ThroatArea / s.FaceArea : 0.0;

            // ---- 面马赫：进气道的作用就是把它减速到 0.5 上下 ----
            // 有型线（自定义进气道零件）时不再由人填：喉道到发动机面就是一段扩压器，
            // 面积比定下来、出口马赫就定了（亚声速支反解）。以前这里读一根"设计迎面马赫"滑杆，
            // 等于让玩家替物理做选择——填 0.30 就是"我认为它减速到 0.30"，跟几何没关系。
            // 所以顺序必须是：先有面积，再反解面马赫。
            double faceMach;
            if (faceMachOverride > 0.0) faceMach = faceMachOverride;
            else if (spec.Shape != null && spec.Shape.Sections != null
                     && spec.Shape.Sections.Count >= IntakeProfile.MinSections)
                faceMach = FaceMachFromAreaRatio(s.FaceArea / Math.Max(s.ThroatArea, 1e-9));
            else faceMach = FaceMachOf(spec);
            faceMach = Math.Min(Math.Max(faceMach, FaceMachMin), FaceMachMax);
            s.FaceMach = faceMach;

            // ---- 流量：发动机要吃的 / 喉道能过的 / 唇口能兜的 ----
            s.RequiredMassFlow = MassFlowAt(s.FaceArea, s.Pt2, s.Tt2, faceMach);
            double throatCap = Gas.ChokedFlow(s.ThroatArea, s.Pt2, s.Tt2);
            s.ThroatMargin = throatCap > 1e-9 ? 1.0 - s.RequiredMassFlow / throatCap : 0.0;
            s.ThroatChoked = s.RequiredMassFlow > throatCap * 1.001;

            s.CapturedMassFlow = Math.Min(s.RequiredMassFlow, throatCap);
            s.BleedMassFlow = s.CapturedMassFlow * Math.Min(Math.Max(bleed, 0.0), 0.25);
            // 可用流量按**喉道能过的上限**算，不按面马赫折算：
            // 设计面马赫只是"设计点想把气减到多慢"，发动机要吃更多的时候面马赫会自己升上去，
            // 进气道真正的硬上限只有喉道壅塞。按面马赫算会把 11 km 静推力压掉 9.6%（实测），
            // 那不是物理，那是把设计选择当成了运行约束。
            s.AvailableMassFlow = Math.Max(throatCap - s.BleedMassFlow, 0.0);

            // 溢流：来流管里"这台机器吃不下"的那部分。静止时没有来流管，不产生溢流。
            double tubeFlow = p0 / (Gas.R * t0) * s.FlightVelocity * s.CaptureArea;
            s.SpillageMassFlow = Math.Max(0.0, tubeFlow - s.CapturedMassFlow);
            s.Spilling = s.SpillageMassFlow > 1e-6;
            s.InletDrag = s.SpillageMassFlow * s.FlightVelocity;
            if (s.Spilling && mach > 0.6)
                s.Warn(string.Format(Loc.T("捕获不足：溢流 {0:F2} kg/s（MFR={1:F2}），进气道阻力约 {2:F0} N", "Captured short of demand: spill {0:F2} kg/s (MFR={1:F2}), intake drag about {2:F0} N"),
                    s.SpillageMassFlow,
                    tubeFlow > 1e-9 ? s.CapturedMassFlow / tubeFlow : 0.0,
                    s.InletDrag));

            if (s.ThroatChoked)
                s.Warn(string.Format(Loc.T("喉道壅塞：要 {0:F1} kg/s，喉道只能过 {1:F1} kg/s（面马赫 {2:F2} 太高或喉道比太小）", "Throat choked: it needs {0:F1} kg/s but the throat can pass only {1:F1} kg/s (face Mach {2:F2} too high, or the throat ratio too small)"),
                    s.RequiredMassFlow, throatCap, faceMach));

            // ---- 起动状态 ----
            // 真激波模式用几何判据：压缩角超过 θmax 就是脱体（这才是"未起动"的物理含义）。
            // 经验模式保留老口径（M>2 且皮托/锥体且恢复低于 0.72），一个字都不改——
            // 老存档的每一条读数都得跟 381182e 逐位相同，包括这条保守判定。
            s.Started = spec.Model == RecoveryModel.Shock
                ? Intake.Started(spec, mach, designMach)
                : !(mach > 2.0 && (spec.Kind == IntakeKind.Pitot || spec.Kind == IntakeKind.Cone) && tpr < 0.72);
            s.Unstarted = !s.Started;
            if (s.Unstarted)
                s.Warn(string.Format(Loc.T("进气道未起动：几何压缩角 {0:F1}° 超过 M{1:F2} 的 θmax {2:F1}°，激波脱体（恢复按正激波 {3:F3}）", "Intake not started: the geometric compression angle {0:F1} deg exceeds theta-max {2:F1} deg at M{1:F2}, the shock has detached (recovery taken as a normal shock, {3:F3})"),
                    Intake.RampHalfAngleDeg(spec.Kind, designMach), mach, Shock.MaxDeflectionDeg(mach), tpr));
            string incWarn = Intake.IncidenceWarning(spec.Kind, cross, mach);
            if (incWarn != null) s.Warn(incWarn);

            // ---- 畸变：经验指数，只给喘振裕度当输入，不直接改推力 ----
            s.DistortionIndex = DistortionOf(spec, mach);

            // ---- 老口径：喉道 + 来流总压（与旧 Cycle.MassFlow 逐位相同）----
            s.LegacyMassFlow = MassFlowAt(s.ThroatArea, pt0, tt0, faceMach)
                               * (1.0 - Math.Min(Math.Max(bleed, 0.0), 0.25));

            if (s.RequiredMassFlow <= 0.0 || s.ThroatArea <= 0.0 || s.CaptureArea <= 0.0 || s.FaceArea <= 0.0)
            {
                s.Valid = false;
                s.FailureCode = "AREA_INVALID";
                s.Warn(Loc.T("进气道面积非法（喉道/唇口/进口截面必须为正）", "Intake areas are illegal (throat, lip and engine face must all be positive)"));
            }

            return s;
        }

        /// <summary>
        /// 独立进气道零件：三块面积由**它自己**的设计给，不看整机设计。
        ///
        /// 为什么要有这个入口：进气道装成独立零件以后，它的捕获直径、喉道比跟发动机的
        /// 外形尺度没有关系（一架飞机可以挂两个 Ø1.2 的大进气道去喂一台 Ø0.6 的核心机）。
        /// 硬拿整机设计去推面积，等于给独立零件又套了一层"其实还是发动机的一部分"。
        /// </summary>
        public static IntakeSolution SolvePart(IntakeSpec spec, double faceDiameter, TechLevel tech,
                                               double alt, double mach, double faceMachOverride = 0.0,
                                               double bleed = double.NaN, double length = 0.0)
        {
            IntakeSpec sp = spec != null ? spec.Resolved() : new IntakeSpec().Resolved();
            double faceD = GasState.IsFinite(faceDiameter) && faceDiameter > 0.0 ? faceDiameter : 0.5;
            double capD = sp.CaptureDiameter > 0.0 ? sp.CaptureDiameter : faceD * 1.10;
            double ratio = Math.Min(Math.Max(sp.ThroatRatio, 0.2), 1.2);
            double capture = Math.PI * 0.25 * capD * capD;
            double throat = capture * ratio;
            double face = Math.PI * 0.25 * faceD * faceD;
            double bl = GasState.IsFinite(bleed) ? bleed : sp.BleedFrac;
            // 长度 > 0 才谈管道损失：零件知道自己多长，整机设计里没有这个概念。
            double ductLoss = 1.0;
            if (GasState.IsFinite(length) && length > 0.01)
                ductLoss = IntakeGeometry.DuctLossFactor(IntakeGeometry.OfPart(sp, faceD, length));
            return SolveAt(sp, null, alt, mach, faceMachOverride, bl, capture, throat, face, tech, ductLoss);
        }

        /// <summary>
        /// 亚声速扩压器：给定 A/A*（发动机面 / 喉道），反解出口马赫。
        /// 等熵面积关系 A/A* = (1/M)·[(2/(γ+1))·(1+(γ−1)/2·M²)]^((γ+1)/(2(γ−1)))，
        /// 在亚声速支上 A/A* 随 M 单调减（M→0 时 →∞，M→1 时 →1），所以二分一定能收敛。
        /// 面积比 ≤1（面比喉道还窄）不是扩压器，这时给 0.95——真正的问题由壅塞告警去说，
        /// 不该在这里悄悄算出一个 1.3 马赫骗人。
        /// </summary>
        public static double FaceMachFromAreaRatio(double areaRatio)
        {
            if (!GasState.IsFinite(areaRatio) || areaRatio <= 1.0) return 0.95;
            const double gamma = 1.4;
            double lo = 0.05, hi = 0.999;
            // 100 次二分足够到 1e-12，但没必要——这里精度取到 1e-6 就停。
            for (int i = 0; i < 100; i++)
            {
                double mid = 0.5 * (lo + hi);
                double ratio = AreaRatioOf(mid, gamma);
                if (ratio > areaRatio) lo = mid; else hi = mid;
                if (hi - lo < 1e-6) break;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>等熵面积比 A/A*（γ=1.4）：出口马赫 → 所需面积比。</summary>
        private static double AreaRatioOf(double mach, double gamma)
        {
            double m = Math.Min(Math.Max(mach, 1e-6), 0.9999);
            double term = (2.0 / (gamma + 1.0)) * (1.0 + 0.5 * (gamma - 1.0) * m * m);
            return (1.0 / m) * Math.Pow(term, (gamma + 1.0) / (2.0 * (gamma - 1.0)));
        }

        /// <summary>管道弯曲的总压损失系数（0~60°，每 10° 约 0.8%）。
        /// 抽成函数是为了让残酷模式与简单模式吃同一份数——两处各写一遍迟早会分叉。
        /// </summary>
        public static double BendLoss(double ductBendDeg)
        {
            if (!GasState.IsFinite(ductBendDeg)) return 1.0;
            double bend = Math.Min(Math.Max(ductBendDeg, 0.0), 60.0);
            return 1.0 - 0.0008 * bend;
        }

        /// <summary>
        /// 进气畸变指数（0~0.35 经验值）。畸变只给喘振裕度当输入，不直接改推力——
        /// 真机上是"某几片叶片先失速"，不是"整台机器均匀变差"，所以它该进的是喘振风险那一项。
        /// 单独抽成函数的原因：飞行中每帧只要这一个数，没必要为了它把整条进气道解一遍。
        /// </summary>
        public static double DistortionOf(IntakeSpec spec, double mach)
        {
            if (!GasState.IsFinite(mach) || mach <= 0.0) return 0.0;
            double bend = spec != null ? Math.Min(Math.Max(spec.DuctBendDeg, 0.0), 60.0) : 0.0;
            // 横向角也进畸变：气流斜着进来，风扇迎风面那一圈先吃到不匀的流场。
            // 缺省 0° 时为 0，加不加这一项读数一样。
            double cross = spec != null ? Intake.CrossflowDeg(spec.AlphaDeg, spec.BetaDeg) : 0.0;
            return Math.Min(Math.Max(0.02 + 0.35 * Math.Max(0.0, mach - 1.0) + 0.004 * bend + 0.010 * cross, 0.0), 0.35);
        }

        /// <summary>按整机设计取畸变（型式和弯曲都从 `IntakeSpec` 来）。</summary>
        public static double DistortionOf(EngineDesignData d, double mach)
        {
            IntakeSpec spec = d != null && d.Intake != null ? d.Intake.Resolved() : new IntakeSpec().Resolved();
            return DistortionOf(spec, mach);
        }

        /// <summary>可压缩流量函数：给定面积、总参数、马赫，算质量流量。</summary>
        public static double MassFlowAt(double area, double pt, double tt, double mach)
        {
            if (area <= 0.0 || pt <= 0.0 || tt <= 0.0) return 0.0;
            double g = Gas.Gamma(tt);
            if (mach <= 0.0) return 0.0;
            return area * pt / Math.Sqrt(tt) * Math.Sqrt(g / Gas.R) * mach
                   * Math.Pow(1.0 + 0.5 * (g - 1.0) * mach * mach, -(g + 1.0) / (2.0 * (g - 1.0)));
        }

        /// <summary>
        /// 全包线扫描：给性能网格/界面画曲线用。返回按马赫递增的点列。
        /// 逐点都是独立求解，不共享任何中间量——这样"某个马赫下进气道说了什么"
        /// 可以单独核对。
        /// </summary>
        public static List<IntakeSolution> Sweep(EngineDesignData d, double alt, double machFrom, double machTo, int steps)
        {
            List<IntakeSolution> o = new List<IntakeSolution>();
            if (steps < 2) steps = 2;
            for (int i = 0; i <= steps; i++)
            {
                double m = machFrom + (machTo - machFrom) * i / steps;
                o.Add(Solve(d, m, 0.0, alt));
            }
            return o;
        }
    }
}