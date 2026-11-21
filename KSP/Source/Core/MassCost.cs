using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>质量分解。每一项都对得上一个真实的物理对象，不是拍脑袋的比例。</summary>
    public sealed class MassBreakdown
    {
        public double Fan, Compressor, Turbine, Burner, Casing, Shaft, Nozzle, Accessory, AccessoryGearbox;
        /// <summary>加力段：筒体 + 隔热屏 + 火焰稳定器 + 喷油环。没有加力就是 0。</summary>
        public double Afterburner;
        /// <summary>可调喷管的作动系统（作动筒/液压管路/同步环）。固定收敛喷管为 0。</summary>
        public double NozzleActuator;
        /// <summary>风扇减速齿轮箱（GTF）。直驱机型这一项是 0，老存档读数一个数都不会变。</summary>
        public double Gearbox;
        public double TotalDry;          // 干重 kg
        public double ThrustToWeight;    // 推重比（用设计点推力）
        public double Cost;              // 造价（KSP funds）
        public double EntryCost;         // 解锁费
        public string Note = "";
        public List<string> Lines = new List<string>();
        /// <summary>
        /// 逐转子的质量 kg（键 "FAN"/"LPC"/"HPC"/"HPT"/"IPT"/"LPT"）。
        /// 各轴惯量要按"这台轴上到底挂了哪些转子"分开算，只有汇总数是不够的。
        /// </summary>
        public Dictionary<string, double> RotorParts = new Dictionary<string, double>();
    }

    /// <summary>
    /// 质量与造价估算。
    ///
    /// 思路：把发动机拆成"转子 / 机匣 / 燃烧室 / 喷管 / 轴 / 附件"六块，
    /// 每块都用几何去算——转子按环形通道面积×等效金属厚度，机匣按圆柱壳面积×壁厚，
    /// 所以**级数变多、直径变大、长度变长，重量必然跟着变**，而不是查表。
    ///
    /// 系数（RotorT、Taper、TurbineFactor…）是用 J79 / J57 / J85 / RD-33 / CFM56 的
    /// 公开干重反标定出来的，改系数前先跑 JetForge.Test 的 `RunMassCases`，那里有真值。
    /// </summary>
    public static class MassCost
    {
        // ---- 转子 ----
        const double RotorT = 0.075;        // 单级转子等效金属轴向厚度 m（叶片+盘的实心当量）
        const double RotorDensity = 2600;   // 等效密度 kg/m³（钛合金盘 + 空心叶片）
        const double Taper = 0.62;          // 级平均通道面积 / 首级面积（流道后段变窄）
        const double TurbTaper = 0.85;      // 涡轮流道收缩没压气机那么狠
        const double TurbineFactor = 2.00;  // 涡轮转子比压气机重（叶片冷却、实心、叶根厚）
        const double DiskKComp = 10.0;      // 轮盘项 kg/m²：盘重 ∝ D²，小发动机也躲不掉
        const double DiskKTurb = 16.0;
        const double DiskKFan = 30.0;
        const double FanT = 0.090;          // 风扇/宽弦叶片等效厚度
        const double FanDensity = 2200;

        // ---- 静子 ----
        const double CasingT = 0.008;       // 核心机匣壁厚 m
        const double FanCaseT = 0.011;      // 风扇机匣更厚（包容性要求）
        const double CasingDensity = 8100;  // 高温合金/钢
        const double BurnerFactor = 2.6;    // 燃烧室：火焰筒 + 双层壁
        const double NozzleT = 0.005;

        // ---- 其它 ----
        const double ShaftFrac = 0.045;     // 主轴
        const double AccessoryFrac = 0.14;  // 附件、齿轮箱、燃油/滑油系统、线束、控制系统
        const double CostPerKg = 1.8;       // KSP funds / kg（对标原版涡喷约 1.5 funds/kg）
        const double EntryCostFactor = 12.0;
        const double ScaleRefD = 0.76;      // 尺度参考直径 m（J79 级）：小发动机的叶高与壁厚相对其直径更薄
        const double ScaleExp = 0.60;       // 厚度随直径的幂次（质量 ∝ D^(2+ScaleExp)）

        public static MassBreakdown Estimate(EngineDesignData d)
        {
            MassBreakdown m = new MassBreakdown();
            if (d == null) { m.Note = Loc.T("没有设计", "No design"); return m; }

            double g = 9.80665;

            // ---------- 转子 ----------
            if (d.HasFan && d.Fan != null)
            {
                double a = Annulus(d.Fan.Diameter, d.Fan.HubTipRatio);
                double per0 = a * FanT * FanDensity / Math.Sqrt(Math.Max(d.Fan.Stages, 1));
                double per = per0 + DiskKFan * d.Fan.Diameter * d.Fan.Diameter;
                m.Fan = per * Math.Max(d.Fan.Stages, 1);
                m.RotorParts["FAN"] = m.Fan;
            }
            foreach (CompressorSpec c in new[] { d.Lpc, d.Hpc })
            {
                if (c == null || c.Stages <= 0) continue;
                double a = Annulus(c.TipDiameter, c.HubTipRatio);
                double per = a * Taper * RotorT * RotorDensity * ScaleF(c.TipDiameter) + DiskKComp * c.TipDiameter * c.TipDiameter;
                m.Compressor += per * c.Stages;
                m.RotorParts[c == d.Lpc ? "LPC" : "HPC"] = per * c.Stages;
            }
            foreach (TurbineSpec t in new[] { d.Hpt, d.Ipt, d.Lpt })
            {
                if (t == null || t.Stages <= 0) continue;
                double a = Annulus(t.TipDiameter, t.HubTipRatio);
                double per = a * TurbTaper * RotorT * RotorDensity * TurbineFactor * ScaleF(t.TipDiameter)
                             + DiskKTurb * t.TipDiameter * t.TipDiameter;
                m.Turbine += per * t.Stages;
                m.RotorParts[t == d.Hpt ? "HPT" : (t == d.Ipt ? "IPT" : "LPT")] = per * t.Stages;
            }

            // ---------- 静子 ----------
            // 机匣直径跟着压气机走，不是跟着进气道走——大发动机的机匣长度与直径是重量的主项
            double dCase = Math.Max(d.CoreInletD, 0.1);
            for (int i = 0; i < 2; i++)
            {
                CompressorSpec c = i == 0 ? d.Lpc : d.Hpc;
                if (c != null && c.Stages > 0) dCase = Math.Max(dCase, c.TipDiameter);
            }
            double rCore = dCase * 0.5;
            double lCore = Math.Max(d.EffectiveLength, 0.4) * (d.HasFan ? 0.55 : 0.70);
            m.Casing = Math.PI * (2.0 * rCore) * lCore * CasingT * ScaleFWall(dCase) * CasingDensity;
            if (d.HasFan && d.Fan != null)
            {
                double rFan = d.Fan.Diameter * 0.5;
                double lFan = Math.Max(d.EffectiveLength, 0.4) * 0.30;
                m.Casing += Math.PI * (2.0 * rFan) * lFan * FanCaseT * ScaleFWall(d.Fan.Diameter) * CasingDensity;
            }

            // 燃烧室：按型式给的相对体积与壁面倍数
            double[] bf = BurnerVolumeFactor(d.Burner);
            double lBurner = Math.Max(d.EffectiveLength, 0.4) * (d.Burner != null && d.Burner.Length > 0 ? d.Burner.Length / Math.Max(d.EffectiveLength, 0.4) : 0.18);
            double rBurner = rCore * bf[1];
            m.Burner = Math.PI * (2.0 * rBurner) * lBurner * 0.0045 * BurnerFactor * ScaleFWall(rBurner * 2.0) * CasingDensity * bf[0];

            // 喷管
            double rNoz = Math.Max(d.CoreInletD * 0.5 * 0.9, 0.05);
            double lNoz = Math.Max(d.EffectiveLength, 0.4) * 0.12;
            if (d.Nozzle == NozzleKind.ConvergingDiverging) lNoz *= 1.35;
            m.Nozzle = Math.PI * (2.0 * rNoz) * lNoz * NozzleT * CasingDensity;
            // 加力段单独列出来：筒体、隔热屏、火焰稳定器与喷油环都在这一段。
            // 以前它藏在喷管那一项里，玩家改了加力只看到"喷管重了几十公斤"，
            // 看不出这是加力段的代价（§5.3 要求取舍由参数关系驱动，先得让它看得见）。
            if (d.Afterburner != null && d.Afterburner.Enabled)
                m.Afterburner = Math.PI * (2.0 * rCore * 1.08) * Math.Max(d.EffectiveLength, 0.4) * 0.16 * CasingT * CasingDensity;

            // 花瓣：片数与长度都是看得见的几何，就得付材料与机构。
            // 基准是 16 片 × 0.48·R（老口径），所以缺省值下这两个因子正好都是 1.0，
            // 老存档的质量一分不变。
            int petals = NozzleVisual.CountOf(d);
            double lenFrac = NozzleSchedule.CanVary(d.Nozzle)
                ? Math.Max(0.6, Math.Min(1.8, d.PetalLengthFrac > 0.0 ? d.PetalLengthFrac : 1.0))
                : 1.0;
            // 每片都要一套铰链与连杆，多一片多一组；板材随长度线性涨。
            m.NozzleActuator = NozzleSchedule.CanVary(d.Nozzle)
                ? (10.0 + 0.30 * (m.Nozzle + m.Afterburner)) * (1.0 + 0.02 * (petals - 16))
                : 0.0;
            m.Nozzle *= 1.0 + 0.18 * (lenFrac - 1.0);

            // ---------- 轴与附件 ----------
            double rotor = m.Fan + m.Compressor + m.Turbine;
            m.Shaft = rotor * ShaftFrac;
            double sub = rotor + m.Casing + m.Burner + m.Nozzle + m.Afterburner + m.NozzleActuator + m.Shaft;
            m.Accessory = sub * AccessoryFrac;
            m.AccessoryGearbox = m.Accessory * 0.45;
            m.TotalDry = sub + m.Accessory;

            // ---------- 加强系统 ----------
            // 想扛更高的 g 就得让机匣/轴粗一圈：满配按结构重量的 12% 计价。
            // 这 12% 是"看得见的代价"——玩家调高抗过载时必须能在重量读数里看到它变重了，
            // 否则那个滑条就变成"免费的强度"。
            double reinforce = d.GReinforce;
            if (reinforce < 0.0) reinforce = 0.0;
            if (reinforce > 1.0) reinforce = 1.0;
            if (reinforce > 0.0) m.TotalDry += sub * 0.12 * reinforce;

            // ---------- 推重比与造价 ----------
            CycleResult r = Cycle.SolveDesignPoint(d);

            // ---------- 风扇减速齿轮箱（GTF） ----------
            // 放在这里而不是上面：齿轮箱的质量吃**传递的扭矩**，扭矩要用设计点算出来的
            // 风扇功率倒推（扭矩 = P / ω），所以只能等 r 出来再算。
            // 而且它必须计入干重与造价——不然"加个齿轮比"就变成免费的推力增益。
            // 量级校核：10 MW 级风扇、比值 3 的箱子落在百公斤（PW1100G 公开值约 120 kg）。
            if (d.HasFan && d.Fan != null && d.Fan.GearRatio > 1.0001)
            {
                double fankW = Math.Max(r.WFan, 0.0) / 1000.0;
                double ratio = d.Fan.GearRatio;
                m.Gearbox = 40.0 + 0.0055 * fankW * ratio;
                m.TotalDry += m.Gearbox;
            }

            double thrustN = r.Ok ? r.Thrust : 0.0;
            m.ThrustToWeight = m.TotalDry > 1.0 ? thrustN / (m.TotalDry * g) : 0.0;

            double techFactor = TechCostFactor(d.Tech);
            m.Cost = Math.Round(m.TotalDry * CostPerKg * techFactor / 10.0) * 10.0;
            m.EntryCost = Math.Round(m.Cost * EntryCostFactor / 100.0) * 100.0;
            if (!r.Ok) m.Note = Loc.T("设计点没解出来，推重比按 0 计", "Design point did not solve; thrust/weight counted as zero");

            m.Lines.Add(string.Format(Loc.T("风扇 {0:F0} kg · 压气机 {1:F0} kg · 涡轮 {2:F0} kg", "Fan {0:F0} kg · compressor {1:F0} kg · turbine {2:F0} kg"),
                m.Fan, m.Compressor, m.Turbine));
            m.Lines.Add(string.Format(Loc.T("机匣 {0:F0} kg · 燃烧室 {1:F0} kg · 喷管 {2:F0} kg", "Casing {0:F0} kg · combustor {1:F0} kg · nozzle {2:F0} kg"),
                m.Casing, m.Burner, m.Nozzle));
            if (m.Afterburner > 0.5 || m.NozzleActuator > 0.5)
                m.Lines.Add(string.Format(Loc.T("加力段 {0:F0} kg · 喷管作动 {1:F0} kg", "Afterburner {0:F0} kg · nozzle actuators {1:F0} kg"),
                    m.Afterburner, m.NozzleActuator));
            m.Lines.Add(string.Format(Loc.T("主轴 {0:F0} kg · 附件 {1:F0} kg · 干重 {2:F0} kg", "Shafts {0:F0} kg · accessories {1:F0} kg · dry mass {2:F0} kg"),
                m.Shaft, m.Accessory, m.TotalDry));
            if (m.Gearbox > 0.5)
                m.Lines.Add(string.Format(Loc.T("风扇减速齿轮箱 {0:F0} kg（比值 {1:F2}）", "Fan gearbox {0:F0} kg (ratio {1:F2})"), m.Gearbox, d.Fan.GearRatio));
            m.Lines.Add(string.Format(Loc.T("推重比 {0:F2} · 造价 {1:N0} funds", "Thrust/weight {0:F2} · cost {1:N0} funds"),
                m.ThrustToWeight, m.Cost));
            return m;
        }

        /// <summary>厚度随直径缩放的倍率。真实叶片弦长与机匣壁厚都随尺寸变，不是常数。</summary>
        private static double ScaleF(double diameter)
        {
            if (diameter <= 0.0) return 1.0;
            double f = Math.Pow(diameter / ScaleRefD, ScaleExp);
            return Math.Min(Math.Max(f, 0.55), 1.9);
        }

        /// <summary>
        /// 机匣壁厚按压力容器规律走：t = pD/(2σ)，厚径比基本恒定，所以壁厚 ∝ D。
        /// 这一条对推力室、喷管壳体同样成立，用同一个倍率。
        /// </summary>
        private static double ScaleFWall(double diameter)
        {
            if (diameter <= 0.0) return 1.0;
            double f = Math.Pow(diameter / ScaleRefD, 1.0);
            return Math.Min(Math.Max(f, 0.42), 1.9);
        }

        private static double Annulus(double tipDiameter, double hubTipRatio)
        {
            if (tipDiameter <= 0.0) return 0.0;
            double h = Math.Min(Math.Max(hubTipRatio, 0.2), 0.95);
            return Math.PI * 0.25 * tipDiameter * tipDiameter * (1.0 - h * h);
        }

        /// <summary>燃烧室的体积倍数与外径倍数：环形的短而细，单管的长而粗。</summary>
        private static double[] BurnerVolumeFactor(BurnerSpec b)
        {
            BurnerKind k = b == null ? BurnerKind.Annular : b.Kind;
            switch (k)
            {
                case BurnerKind.Can: return new double[] { 1.35, 1.10 };
                case BurnerKind.Cannular: return new double[] { 1.15, 1.04 };
                case BurnerKind.ShortAnnular: return new double[] { 0.80, 0.98 };
                default: return new double[] { 1.00, 1.00 };
            }
        }

        /// <summary>年代越新，材料与工艺越贵。</summary>
        private static double TechCostFactor(TechLevel t)
        {
            string n = t == null ? "1970s" : t.Name;
            if (n.StartsWith("1940")) return 0.75;
            if (n.StartsWith("1960")) return 0.95;
            if (n.StartsWith("1970")) return 1.15;
            if (n.StartsWith("1990")) return 1.55;
            return 2.10;
        }
    }
}