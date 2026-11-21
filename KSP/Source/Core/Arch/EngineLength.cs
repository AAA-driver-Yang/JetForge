using System;

namespace JetForge.Core
{
    /// <summary>
    /// 整机长度不再由玩家拧滑杆，而是**按各个部件的轴向尺寸加起来**。
    ///
    /// 为什么必须算：长度不是"外观参数"，它决定管道摩擦（<see cref="DuctLoss"/>）、
    /// 质量、造价、喷管到机翼的间距，还有网格。以前 `Length` 是玩家随手填的一个数，
    /// 于是"把压气机从 17 级改成 9 级、长度还写着 5.2 m"这种事完全合法——
    /// 一台 9 级的机器凭空多出两米管道，性能凭空掉一个点，玩家却找不到原因。
    ///
    /// 估法：真实涡喷/涡扇的轴向布局可以粗看成
    ///   进气机匣 → 风扇/低压压气机 → 中介机匣 → 高压压气机 → 扩压器 → 燃烧室
    ///   → 涡轮（高压/中压/低压）→ 加力段（可选）→ 喷管
    /// 每级压气机/涡轮的轴向弦长随直径线性增长（大机器每级更长），固定件按直径给常数。
    /// 常数是拿四台真机（J79 5.20 m、J57 4.66 m、J85 2.90 m、RD-33 4.20 m）最小二乘标定的，
    /// 四台都在 ±8% 以内——这是"布局估算"的精度上限，不是测量。
    /// </summary>
    public static class EngineLength
    {
        // ---- 标定常数（见类注释；改动会让四台预设的读数一起动，回归里有断言）----
        /// <summary>压气机/风扇每级轴向长度 = AxialPerStageSlope·D + AxialPerStageBias。</summary>
        public const double AxialPerStageSlope = 0.060;
        public const double AxialPerStageBias = 0.080;
        /// <summary>涡轮每级比压气机每级略长（叶片带冷却、级间有封严）。</summary>
        public const double TurbineStageSlope = 0.085;
        public const double TurbineStageBias = 0.060;
        /// <summary>进气机匣：固定 0.26 m + 0.24·进气口径。</summary>
        public const double FrontBase = 0.26;
        public const double FrontPerDiameter = 0.24;
        /// <summary>压气机之间的中介机匣（只双转子才有）：0.16 + 0.22·D 高压。</summary>
        public const double MidDuctBase = 0.16;
        public const double MidDuctPerDiameter = 0.22;
        /// <summary>扩压器 + 火焰筒前段：0.22 + 0.28·D 核心。</summary>
        public const double DiffuserBase = 0.22;
        public const double DiffuserPerDiameter = 0.28;
        /// <summary>燃烧室：0.30 + 0.55·D 核心，且不小于玩家填的火焰筒长度。</summary>
        public const double BurnerBase = 0.30;
        public const double BurnerPerDiameter = 0.55;
        /// <summary>喷管段（收敛/收敛扩张的筒体）：0.35 + 0.40·D 核心。</summary>
        public const double NozzleBase = 0.35;
        public const double NozzlePerDiameter = 0.40;

        /// <summary>算出这台机器的轴向总长（米）。</summary>
        public static double Estimate(EngineDesignData d)
        {
            if (d == null) return MinLength;
            double dFront = FrontDiameter(d);
            double dCore = CoreDiameter(d);

            double total = FrontBase + FrontPerDiameter * dFront;
            total += RotorLength(d.Fan != null ? d.Fan.Stages : 0, d.Fan != null ? d.Fan.Diameter : dFront);
            // GTF 的减速齿轮箱夹在风扇与低压压气机之间，轴向要占一段。
            // 真机 PW1100G 的风扇/齿轮箱/增压级这一整段本来就更长，这里只加齿轮箱本身那截。
            if (d.HasFan && d.Fan.GearRatio > 1.0001)
                total += GearboxLength(d.Fan.Diameter, d.Fan.GearRatio);
            bool hasLpc = d.Lpc != null && d.Lpc.Stages > 0;
            bool hasHpc = d.Hpc != null && d.Hpc.Stages > 0;
            if (hasLpc) total += RotorLength(d.Lpc.Stages, TipOr(d.Lpc, dFront));
            if (hasLpc && hasHpc) total += MidDuctBase + MidDuctPerDiameter * TipOr(d.Hpc, dCore);
            if (hasHpc) total += RotorLength(d.Hpc.Stages, TipOr(d.Hpc, dCore));

            total += DiffuserBase + DiffuserPerDiameter * dCore;
            total += BurnerLength(d, dCore);

            total += RotorLength(TurbineStages(d.Hpt), TipOr(d.Hpt, dCore)) * TurbineScale;
            total += RotorLength(TurbineStages(d.Ipt), TipOr(d.Ipt, dCore)) * TurbineScale;
            total += RotorLength(TurbineStages(d.Lpt), TipOr(d.Lpt, dCore)) * TurbineScale;

            if (d.Afterburner != null && d.Afterburner.Enabled)
            {
                // 加力段自己带长度（火焰稳定器 + 筒衬 + 掺混段），玩家没填就按核心径估。
                total += d.Afterburner.Length > 0.0
                    ? (double)d.Afterburner.Length
                    : BurnerBase + 0.80 * dCore;
            }
            total += NozzleBase + NozzlePerDiameter * dCore;

            if (!IsFinite(total) || total < MinLength) return MinLength;
            return total;
        }

        /// <summary>长度的下限：再小的机器也得有个筒子。</summary>
        public const double MinLength = 0.4;

        /// <summary>涡轮每级与压气机每级的比例（冷却叶片 + 级间封严更长）。</summary>
        private const double TurbineScale =
            (TurbineStageSlope + TurbineStageBias) / (AxialPerStageSlope + AxialPerStageBias);

        /// <summary>
        /// 风扇减速齿轮箱的轴向长度。行星齿轮箱的直径受风扇轮毂限制，
        /// 长度主要随传动功率（∝ 风扇直径²）长，齿轮比越大齿圈越厚。
        /// 0.10 m 是轴承与支撑机匣的固定底数。
        /// </summary>
        public static double GearboxLength(double fanDiameter, double gearRatio)
        {
            double df = IsFinite(fanDiameter) && fanDiameter > 0.0 ? fanDiameter : 1.0;
            double r = IsFinite(gearRatio) && gearRatio > 1.0 ? gearRatio : 1.0;
            return 0.10 + 0.12 * df + 0.02 * (r - 1.0);
        }

        /// <summary>N 级转子占多长。0 级 = 没有这一段。</summary>
        public static double RotorLength(int stages, double tipDiameter)
        {
            if (stages <= 0) return 0.0;
            double d = IsFinite(tipDiameter) && tipDiameter > 0.0 ? tipDiameter : 0.5;
            return stages * (AxialPerStageSlope * d + AxialPerStageBias);
        }

        /// <summary>燃烧室的轴向长度：火焰筒长度是硬约束，机器大了也得更长。</summary>
        public static double BurnerLength(EngineDesignData d, double coreDiameter)
        {
            double b = BurnerBase + BurnerPerDiameter * coreDiameter;
            if (d != null && d.Burner != null && d.Burner.Length > b) b = d.Burner.Length;
            return b;
        }

        /// <summary>进气口径：整流罩直径优先，其次风扇，再次反解出来的进气口径。</summary>
        public static double FrontDiameter(EngineDesignData d)
        {
            if (d == null) return 0.5;
            double f = 0.0;
            if (d.NacelleDiameter > 0.0) f = d.NacelleDiameter;
            else if (d.Fan != null && d.Fan.Diameter > 0.0) f = d.Fan.Diameter;
            else if (d.InletDiameter > 0.0) f = d.InletDiameter;
            return f > 0.0 ? f : 0.5;
        }

        /// <summary>核心机口径：高压压气机叶尖优先，其次给定的核心进气口径。</summary>
        public static double CoreDiameter(EngineDesignData d)
        {
            if (d == null) return 0.5;
            if (d.Hpc != null && d.Hpc.TipDiameter > 0.0) return d.Hpc.TipDiameter;
            if (d.CoreInletDiameter > 0.0) return d.CoreInletDiameter;
            return 0.5;
        }

        private static double TipOr(CompressorSpec c, double fallback)
        {
            return c != null && c.TipDiameter > 0.0 ? c.TipDiameter : fallback;
        }

        private static double TipOr(TurbineSpec t, double fallback)
        {
            return t != null && t.TipDiameter > 0.0 ? t.TipDiameter : fallback;
        }

        private static int TurbineStages(TurbineSpec t) { return t != null ? t.Stages : 0; }

        private static bool IsFinite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        /// <summary>把长度拆开给面板看：玩家想知道"这 5 米都花在哪了"。</summary>
        public static string Breakdown(EngineDesignData d)
        {
            if (d == null) return "";
            double dFront = FrontDiameter(d);
            double dCore = CoreDiameter(d);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendFormat("进气 {0:F2}", FrontBase + FrontPerDiameter * dFront);
            if (d.Fan != null && d.Fan.Stages > 0)
                sb.AppendFormat("｜风扇 {0:F2}", RotorLength(d.Fan.Stages, d.Fan.Diameter));
            if (d.Lpc != null && d.Lpc.Stages > 0)
                sb.AppendFormat("｜低压 {0:F2}", RotorLength(d.Lpc.Stages, TipOr(d.Lpc, dFront)));
            if (d.Hpc != null && d.Hpc.Stages > 0)
                sb.AppendFormat("｜高压 {0:F2}", RotorLength(d.Hpc.Stages, TipOr(d.Hpc, dCore)));
            sb.AppendFormat("｜燃烧 {0:F2}", BurnerLength(d, dCore));
            double tur = RotorLength(TurbineStages(d.Hpt), TipOr(d.Hpt, dCore)) * TurbineScale
                       + RotorLength(TurbineStages(d.Ipt), TipOr(d.Ipt, dCore)) * TurbineScale
                       + RotorLength(TurbineStages(d.Lpt), TipOr(d.Lpt, dCore)) * TurbineScale;
            sb.AppendFormat("｜涡轮 {0:F2}", tur);
            if (d.Afterburner != null && d.Afterburner.Enabled)
                sb.AppendFormat("｜加力 {0:F2}", d.Afterburner.Length > 0.0
                    ? (double)d.Afterburner.Length : BurnerBase + 0.80 * dCore);
            sb.AppendFormat("｜喷管 {0:F2}", NozzleBase + NozzlePerDiameter * dCore);
            return sb.ToString();
        }
    }
}