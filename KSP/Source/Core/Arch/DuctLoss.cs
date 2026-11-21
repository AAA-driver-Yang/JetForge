using System;

namespace JetForge.Core
{
    /// <summary>
    /// 管道摩擦损失。
    ///
    /// 以前整台机器只有"部件效率"和"燃烧室压损"，**管道长度一分钱不值**——
    /// 把 J79 从 5.2 m 拉到 8 m，除了质量和外形什么都没有，读数一模一样。
    /// 真实机器不是这样：气流一路贴着壁面走，边界层一直在吃总压，
    /// 管道越长、截面越细，吃得越多。这就是"整机长度影响性能"那条的物理来源。
    ///
    /// 用 Darcy-Weisbach 的压降换到总压份额：
    ///   Δp = f·(L/Dh)·(ρv²/2)
    ///   Δpt/pt = f·(L/Dh)·(q/pt)，q/pt = (γM²/2)/(1+(γ−1)/2·M²)
    /// f 取 0.018：不锈钢/钛内壁、Re 1e6~1e7、充分发展湍流的那一段。
    /// 不做"管件局部损失"（弯头/台阶），那部分在本模型里由进气道零件的弯曲角
    /// 与燃烧室 sigma 各自承担，重复计一次会变成"越改越亏"的笑话。
    /// </summary>
    public static class DuctLoss
    {
        /// <summary>达西摩擦因子。钢/钛内壁充分发展湍流的典型值。</summary>
        public const double Friction = 0.018;

        /// <summary>单段损失的硬上限。真到 12% 不是管道长，是这台的几何根本不该存在。</summary>
        public const double MaxFraction = 0.12;

        /// <summary>一段管道的总压损失比例（0~MaxFraction）。马赫数取该截面。</summary>
        public static double Fraction(double mach, double length, double hydraulicDiameter)
        {
            if (!GasState.IsFinite(mach) || !GasState.IsFinite(length) || !GasState.IsFinite(hydraulicDiameter)) return 0.0;
            if (mach <= 1e-6 || length <= 1e-6 || hydraulicDiameter <= 1e-3) return 0.0;
            double m2 = mach * mach;
            double qOverPt = 0.7 * m2 / (1.0 + 0.2 * m2);     // (γM²/2)/(1+(γ−1)/2·M²)，γ=1.4
            double f = Friction * (length / hydraulicDiameter) * qOverPt;
            if (f < 0.0) return 0.0;
            return f > MaxFraction ? MaxFraction : f;
        }

        /// <summary>
        /// 按"面积 + 流量 + 总参数"反解这一段的马赫，然后把损失从总压里扣掉。
        /// 扣完静态量一律作废（它们是按旧总压算的），谁要谁重算 —— 留着旧静压
        /// 比留 NaN 更坏：没人会想到它已经过期了。
        /// </summary>
        /// <returns>损失比例（0 = 没扣）。</returns>
        public static double Apply(GasState g, double length, double hydraulicDiameter)
        {
            return Apply(g, length, hydraulicDiameter, 0.0);
        }

        /// <summary>
        /// 与上面同一条，但额外给一个"真实流通面积"。
        ///
        /// 核心段是**环形通道**，不是圆管：拿叶尖直径当管径算面积会多出二三十个点，
        /// 反解出来的马赫就接近零，损失永远是 0.00%（这一版真踩到了：J79 核心段
        /// 一直显示 0.00%，看着像没接线）。摩擦长度按水力直径 Dh = D_外 − D_轮毂，
        /// 马赫按环形面积反解，两头各自用对的口径。
        /// </summary>
        public static double Apply(GasState g, double length, double hydraulicDiameter, double flowAreaOverride)
        {
            if (g == null || !g.IsValid) return 0.0;
            if (!(length > 1e-6) || !(hydraulicDiameter > 0.05)) return 0.0;
            double area = flowAreaOverride > 1e-9
                ? flowAreaOverride
                : Math.PI * 0.25 * hydraulicDiameter * hydraulicDiameter;
            GasState probe = g.Clone();
            probe.SolveStaticFromArea(area);
            if (!GasState.IsFinite(probe.Mach)) return 0.0;
            double f = Fraction(probe.Mach, length, hydraulicDiameter);
            if (f <= 0.0) return 0.0;
            g.TotalPressure *= (1.0 - f);
            g.StaticPressure = double.NaN;
            g.StaticTemperature = double.NaN;
            g.Mach = double.NaN;
            g.Velocity = double.NaN;
            g.FlowArea = double.NaN;
            return f;
        }

        // ---- 四段管道：长度都按整机长度的比例给，直径取该段的实际截面 ----

        /// <summary>进气道法兰 → 风扇/压气机面。这一段在机匣里，是最短的一截。</summary>
        public static void Inlet(EngineDesignData d, out double length, out double diameter)
        {
            length = 0.10 * Math.Max(Len(d), 0.4);
            diameter = Math.Max(Dia(d), 0.10) * 0.92;
        }

        /// <summary>压气机出口 → 燃烧室 → 涡轮进口。核心机最细、最长的一段。</summary>
        public static void Core(EngineDesignData d, out double length, out double diameter)
        {
            double area;
            Core(d, out length, out diameter, out area);
        }

        /// <summary>
        /// 核心段：压气机出口 → 燃烧室 → 涡轮进口。
        ///
        /// 它是**环形**通道（中间是轴），所以水力直径取"外径 − 轮毂径"、
        /// 流通面积取环形面积。以前拿叶尖直径当圆管径：面积大出三成，
        /// 反解的马赫低到 0.05，损失被算成 0.00%——不是没算，是算得太客气。
        /// </summary>
        public static void Core(EngineDesignData d, out double length, out double diameter, out double flowArea)
        {
            length = 0.12 * Math.Max(Len(d), 0.4);
            double dt = 0.0, hr = 0.45;
            if (d != null && d.Hpc != null)
            {
                dt = d.Hpc.TipDiameter;
                if (d.Hpc.HubTipRatio > 0.0) hr = d.Hpc.HubTipRatio;
            }
            if (!(dt > 0.0)) dt = Math.Max(Dia(d) * 0.60, 0.06);
            hr = Math.Min(Math.Max(hr, 0.20), 0.85);
            diameter = Math.Max(dt * (1.0 - hr), 0.05);
            flowArea = Math.PI * 0.25 * dt * dt * (1.0 - hr * hr);
        }

        /// <summary>涡轮出口 → 喷管喉道。排气锥把气流往外送，这一截往往比人想的长。</summary>
        public static void Exhaust(EngineDesignData d, out double length, out double diameter)
        {
            length = 0.25 * Math.Max(Len(d), 0.4);
            diameter = Math.Max(Dia(d), 0.10) * 0.80;
        }

        /// <summary>风扇出口 → 外涵喷口。涡扇的外涵道是整台机器里最长的一条管道。</summary>
        public static void Bypass(EngineDesignData d, out double length, out double diameter)
        {
            length = 0.55 * Math.Max(Len(d), 0.4);
            diameter = Math.Max(d != null && d.Fan != null ? d.Fan.Diameter * 0.55 : 0.0, 0.06);
        }

        private static double Len(EngineDesignData d) { return d != null ? d.EffectiveLength : 0.0; }

        private static double Dia(EngineDesignData d)
        {
            if (d == null) return 0.0;
            if (d.InletDiameter > 0.0) return d.InletDiameter;
            return d.Fan != null && d.Fan.Diameter > 0.0 ? d.Fan.Diameter : 0.5;
        }

        /// <summary>
        /// 面板/自检用的一行字：三段各掉多少。
        ///
        /// 注意这是**手算估计**：马赫由调用方给，不是求解器反解出来的那一个。
        /// 求解器里的权威值是 `CycleResult.DuctInletLoss / DuctCoreLoss / DuctExhaustLoss`，
        /// 面板读数认那三个。这里只在"手上没有解"的场合用（例如面板还没解算时）。
        /// </summary>
        public static string Describe(EngineDesignData d, double machInlet, double machCore, double machExhaust)
        {
            double li, di, lc, dc, ca, le, de;
            Inlet(d, out li, out di); Core(d, out lc, out dc, out ca); Exhaust(d, out le, out de);
            double fi = Fraction(machInlet, li, di);
            double fc = Fraction(machCore, lc, dc);
            double fe = Fraction(machExhaust, le, de);
            return string.Format("管道损失（手算估计）：进气段 {0:P2}｜核心段 {1:P2}｜排气段 {2:P2}（合计 {3:P2}，按 L={4:F2} m）",
                fi, fc, fe, 1.0 - (1.0 - fi) * (1.0 - fc) * (1.0 - fe), Len(d));
        }
    }
}