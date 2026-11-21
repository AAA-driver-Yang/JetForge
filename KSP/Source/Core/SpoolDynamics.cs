using System;

namespace JetForge.Core
{
    /// <summary>
    /// 转子加速快慢由"涡轮剩余功率"决定，而不是按直径拍一个时间常数。
    ///
    /// 真机的物理很直白：轴上多出来的功率 P 全部用来给转子加速——
    ///     P = J·ω·(dω/dt)
    /// 所以加速时间 ∝ 转子惯量 J × 转速² ÷ 剩余功率。惯量大、剩余功率小，就是"推油门半天不走"
    /// （GE90 那种三级风扇大机器）；惯量小、剩余功率足，就是 J85 那种一点就窜。
    ///
    /// 我们手里有三个能算的量：
    ///   ① 惯量 J：按质量分布估算（`MassCost` 给出风扇/压气机/涡轮/轴的质量，半径取质量平均半径）；
    ///   ② 设计转速 ω：由叶尖马赫上限反解（`Mtip·a / R`），跟设计里真实的转速限制同源；
    ///   ③ 剩余功率：非设计点解算已经逐台算过"涡轮必需功 vs 当前转速能提供的功"，
    ///      两者之差就是这一刻能拿来加速的功率（`CapHpt/CapIpt/CapLpt` 减 `WHpt/WIpt/WLpt`）。
    ///
    /// 一个整体系数 `Calibration` 是把 JF-1 标到 1.2 s 得到的——**别的数字都是算出来的，
    /// 只有这个系数是拍的**。它吸收了我们没建模的东西（燃油调节器的加速计划、附件功率、
    /// 叶尖间隙随温度变化等）。
    /// </summary>
    public static class SpoolDynamics
    {
        /// <summary>
        /// 标定系数。它吸收的都是我们没建模的东西：引气、附件功率、效率随转速的掉落、叶尖间隙。
        /// 取值 1.66 是把五个真机预设（J85/J79/J57/RD-33/JF-1）的 0.2→0.95 加速时间一起拟合出来的，
        /// 拟合后最大偏差 19%（原来按直径拍时间常数，五个预设里有两个偏一倍）。
        /// 单看一个数它没有物理意义；跟五个真机放一起才说明"量级对了"。
        /// </summary>
        public const double Calibration = 1.66;

        /// <summary>剩余功率为零时的兜底下限（秒）。没有剩余功率不等于永远不动——真机靠燃油计划硬推。</summary>
        public static readonly double TauCeiling = 6.0;

        /// <summary>
        /// 等效转子惯量（kg·m²）。每个部件用自己的叶轮直径算半径，而不是拿整机直径套：
        /// 以前核心机半径拍成 `InletDiameter·0.28`，小机（J85）算出来 J 只有 0.5 kg·m²，
        /// 比真机小一个量级，于是"小机加速飞快"这条结论就是假的。
        /// 质量平均半径取 0.55×叶尖半径（鼓筒偏外），风扇是薄盘取 0.45。
        /// </summary>
        public static double Inertia(EngineDesignData d)
        {
            if (d == null) return 20.0;
            MassBreakdown m;
            try { m = MassCost.Estimate(d); }
            catch { return 20.0; }
            if (m == null) return 20.0;

            double rFan = d.HasFan ? d.Fan.Diameter * 0.5 : d.InletDiameter * 0.5;
            double rComp = SpecRadius(d.Lpc, d.Hpc, d.InletDiameter * 0.24);
            double rTurb = SpecRadius(d.Lpt, d.Hpt, d.InletDiameter * 0.26);
            // 压气机与涡轮是两种 Spec 类型，但两者都有 Stages/TipDiameter——分成两个重载即可。

            double j = Math.Max(0.0, m.Fan) * Sq(0.45 * rFan)
                     + Math.Max(0.0, m.Compressor) * Sq(0.55 * rComp)
                     + Math.Max(0.0, m.Turbine) * Sq(0.55 * rTurb)
                     + Math.Max(0.0, m.Shaft) * Sq(0.60 * Math.Min(rComp, rTurb));
            return j > 0.05 ? j : 0.05;
        }

        private static double SpecRadius(CompressorSpec primary, CompressorSpec secondary, double fallback)
        {
            if (primary != null && primary.Stages > 0 && primary.TipDiameter > 0.0) return primary.TipDiameter * 0.5;
            if (secondary != null && secondary.Stages > 0 && secondary.TipDiameter > 0.0) return secondary.TipDiameter * 0.5;
            return Math.Max(0.02, fallback);
        }

        private static double SpecRadius(TurbineSpec primary, TurbineSpec secondary, double fallback)
        {
            if (primary != null && primary.Stages > 0 && primary.TipDiameter > 0.0) return primary.TipDiameter * 0.5;
            if (secondary != null && secondary.Stages > 0 && secondary.TipDiameter > 0.0) return secondary.TipDiameter * 0.5;
            return Math.Max(0.02, fallback);
        }

        private static double Sq(double v) { return v * v; }

        /// <summary>设计转速（rad/s）：按该轴叶尖马赫上限反解，而不是查表。</summary>
        public static double DesignOmega(EngineDesignData d)
        {
            if (d == null) return 700.0;
            TechLevel tech = d.Tech;
            double r = d.HasFan ? d.Fan.Diameter * 0.5 : d.InletDiameter * 0.5;
            if (r < 0.02) r = 0.02;
            double mTip = d.HasFan ? tech.MaxTipMachFan : tech.MaxTipMachComp;
            double a = 340.0;                       // 设计点进气道静温对应的声速量级
            double omega = mTip * a / r;
            return omega > 50.0 ? omega : 50.0;
        }

        /// <summary>
        /// 这一刻能拿来加速的功率（W）：逐级涡轮"能提供"减"必须提供"。
        /// 只取正值——供不应求时加速功率就是 0，减速另有路径（收油）。
        /// </summary>
        public static double HeadroomW(OffDesignResult r)
        {
            if (r == null || !r.Ok) return 0.0;
            double cap = r.CapHpt + r.CapIpt + r.CapLpt;
            double need = r.WHpt + r.WIpt + r.WLpt;
            if (cap <= 0.0 || need <= 0.0) return 0.0;
            double h = cap - need;
            return h > 0.0 ? h : 0.0;
        }


        /// <summary>
        /// 按功率积分推进转子。指数滞后只是近似——它假设"越接近目标越慢"是因为时间常数，
        /// 而真机上是因为剩余功率本身随转速下降（`P ∝ x²(1−x)`）。
        /// 两者在开环时长得像，但在"半油门推到底"和"大机器/小机器"上差得很多。
        ///
        /// 功率差：`P = W0 · x² · (target − x)`
        ///   · x² 来自涡轮/压气机功率对转速的二次依赖；
        ///   · (target − x) 来自"燃油已经压到目标的水平了，而转速还差这么多"；
        ///   · target &gt; x 时是加速，target &lt; x 时是减速（收油不需要额外功率，减得快）。
        /// 积分用 `dω/dt = P/(J·ω)`，所以转速分数每秒的增量是 `P/(J·x·ω_design²)`——
        /// 少除一个 ω_design 会让加速快几千倍（我第一版就是这么错的，一帧就冲到头）。
        /// W0 取当前解算点压气机吃的功，所以它是个真实量级，不是随手写的数。
        /// </summary>
        public sealed class Law
        {
            public double W0;             // 压气机功（W），功率标度
            public double Inertia;        // J，kg·m²
            public double OmegaDesign;    // 设计转速 rad/s
            public double Calibration = SpoolDynamics.Calibration;

            /// <summary>子步上限（秒）。dt 很大时（时间加速）也不会一步冲过去。</summary>
            public const double MaxSubStep = 0.02;

            public bool Valid { get { return W0 > 0.0 && Inertia > 0.0 && OmegaDesign > 0.0; } }

            public double Step(double current, double target, double dt)
            {
                if (double.IsNaN(current) || double.IsNaN(target)) return 0.0;
                target = Clamp01(target);
                if (double.IsNaN(dt) || dt <= 0.0) return Clamp01(current);
                if (!Valid) return current;
                if (Math.Abs(target - current) < 1e-4) return target;

                int steps = (int)Math.Ceiling(dt / MaxSubStep);
                if (steps < 1) steps = 1;
                if (steps > 400) steps = 400;
                double h = dt / steps;
                double x = Clamp(current, 0.02, 1.06);
                for (int i = 0; i < steps; i++)
                {
                    double p = W0 * x * x * (target - x);
                    // dω/dt = P/(J·ω)，而 ω = x·ω_design ⇒ 转速分数对时间的导数要再除一个 ω_design。
                    double rate = Calibration * p / (Inertia * x * OmegaDesign * OmegaDesign);
                    x += rate * h;
                    if (x < 0.02) x = 0.02;
                    if (x > 1.06) x = 1.06;
                    if (Math.Abs(target - x) < 1.5e-4) { x = target; break; }
                }
                return Clamp01(x);
            }
        }

        /// <summary>从 from 爬到 to 需要多久（秒）。回归和自检都用它对着真机的时间比。</summary>
        public static double SettleTime(Law law, double from, double to, double limitSeconds = 30.0)
        {
            if (law == null || !law.Valid) return double.NaN;
            double x = from;
            const double dt = 1.0 / 60.0;
            for (double t = 0.0; t < limitSeconds; t += dt)
            {
                x = law.Step(x, to, dt);
                if (x >= to - 1e-3) return t + dt;
                // 一步都不动的场合（功率差符号反了）别死循环
                if (Math.Abs(x - from) < 1e-9 && t > 1.0) return double.NaN;
            }
            return double.NaN;
        }

        /// <summary>
        /// 加速时间常数（秒）。剩余功率给 0 时退回 `TauCeiling`（相当于"按最慢的算"），
        /// 免得一刀切成无穷大把转速冻住。
        /// </summary>
        public static double TauUp(EngineDesignData d, double surplusW)
        {
            double j = Inertia(d);
            double w = DesignOmega(d);
            double energy = 0.5 * j * w * w;        // 从 0 加到设计转速需要的能量（量级）
            if (surplusW <= 0.0 || double.IsNaN(surplusW)) return TauCeiling;
            double tau = Calibration * energy / surplusW;
            return Clamp(tau, 0.25, TauCeiling);
        }

        /// <summary>减速比加速快：收油没有"剩余功率"帮忙，靠风车与摩擦。</summary>
        public static double TauDown(double tauUp)
        {
            return Clamp(0.55 * tauUp, 0.20, 3.0);
        }

        /// <summary>一行自检/日志文字。</summary>
        public static string Describe(EngineDesignData d, double surplusW)
        {
            double j = Inertia(d);
            double w = DesignOmega(d);
            double tau = TauUp(d, surplusW);
            return string.Format("惯量 {0:F1} kg·m²｜设计转速 {1:F0} rad/s｜剩余功率 {2:F1} MW｜时间常数 {3:F2} s",
                j, w, surplusW / 1.0e6, tau);
        }

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v)) return 0.0;
            if (v < 0.0) return 0.0;
            if (v > 1.0) return 1.0;
            return v;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return hi;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}