using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 技术年代档：把"哪一年造得出来什么"打包成一组系数。
    /// 玩家永远不直接填效率，效率 = 技术年代基准 − 尺度效应折减。
    /// 数值与 Python 原型 jetforge.py 的 TechLevel 一致。
    /// </summary>
    public class TechLevel
    {
        public string Name = "1970s";

        /// <summary>
        /// 这个技术档位对应的年代（从 <see cref="Name"/> 里的数字抓，如 "1970s" → 1970）。
        ///
        /// 单独开一个属性而不是让调用方各自解析字符串：故障门槛要按"这机器是哪一年的"
        /// 算抗造程度，年代本来就是这个档位的属性。
        /// </summary>
        public double Year
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return 1960.0;
                int i = 0;
                while (i < Name.Length && (Name[i] < '0' || Name[i] > '9')) i++;
                int start = i;
                while (i < Name.Length && Name[i] >= '0' && Name[i] <= '9') i++;
                if (i <= start) return 1960.0;
                double y;
                if (!double.TryParse(Name.Substring(start, i - start), out y)) return 1960.0;
                if (y < 100.0) y += 1900.0;      // "60s" 这种两位数按 19xx 补
                return y;
            }
        }

        public double FanEtaPol = 0.88;
        public double LpcEtaPol = 0.89;
        public double HpcEtaPol = 0.90;
        public double HptEtaPol = 0.86;
        public double LptEtaPol = 0.89;
        public double BurnerEta = 0.99;
        public double BurnerDpRatio = 0.04;
        public double NozzleEta = 0.975;
        public double IntakeTprSub = 0.99;
        public double IntakeTprSuper = 0.93;

        /// <summary>
        /// 进气道总压恢复系数（随马赫连续）。
        ///
        /// 原来三个调用点都写成 `mach &gt; 1.3 ? IntakeTprSuper : IntakeTprSub`——那是个台阶：
        /// M1.28→1.32 之间总压凭空少 6%，进气量和推力跟着掉 24%，而这一段明明是平滑的。
        /// 现实里亚声速进气道几乎无损失，超声速要靠激波系减速，恢复系数随马赫平缓劣化
        /// （MIL-E-5008B：η = 1 − 0.075(M−1)^1.35）。这里用同一幂次把两端接起来：
        /// M≤1 取亚声速值，M≥2 取超声速值，中间平滑过渡。
        /// </summary>
        public double IntakeTpr(double mach)
        {
            if (mach <= 1.0) return IntakeTprSub;
            double x = mach - 1.0;
            if (x >= 1.0) return IntakeTprSuper;
            return IntakeTprSub - (IntakeTprSub - IntakeTprSuper) * System.Math.Pow(x, 1.35);
        }

        public double MaxTipMachFan = 1.25;
        public double MaxTipMachComp = 1.05;
        public double MaxTipMachTurb = 0.95;

        public double FanPsi = 0.26;
        public double CompressorPsi = 0.38;
        public double TurbinePsi = 2.4;
        public double TurbStagePrMax = 2.4;

        public double T4Limit = 1600.0;
        public double MechEfficiency = 0.99;

        // 尺度效应
        public double ClearanceM = 0.00035;
        public double ClearanceRefFrac = 0.008;
        public double ClearanceSens = 1.30;
        public double ReRef = 3.0e5;
        public double ReExponent = 0.20;

        // 判据参数
        public double StallPhi = 0.38;
        public double StallPhiFan = 0.32;
        public double PhiHighComp = 0.75;
        public double PhiHighFan = 0.70;
        public double SurgeMarginReq = 0.15;
        public double OverspeedLimit = 1.05;
        public double T3Limit = 900.0;
        public double EgtRatio = 0.68;
        public double FarLbo = 0.0045;
        public double FarRich = 0.070;
        // 这里曾经有一个 `T4UncooledLimit = 1250`——"没有冷却叶片的涡轮不能超过这个温度"。
        // 全仓没有一处读它，所以 1960s 的机器照样按 T4Limit = 1390 算，那 1250 只是一行字。
        // 删掉的道理见 §235：不参与计算的上限比没有上限更糟，它会让后来的人以为这道闸存在。
        public double NozzleNprFixedMax = 6.0;

        public static readonly string[] Names = { "1940s", "1960s", "1970s", "1990s", "2010s" };

        private static readonly Dictionary<string, TechLevel> Presets =
            new Dictionary<string, TechLevel>
            {
                {
                    "1940s", new TechLevel
                    {
                        Name = "1940s",
                        FanEtaPol = 0.80, LpcEtaPol = 0.80, HpcEtaPol = 0.80,
                        HptEtaPol = 0.78, LptEtaPol = 0.80, BurnerEta = 0.96,
                        BurnerDpRatio = 0.08, NozzleEta = 0.95,
                        MaxTipMachFan = 0.90, MaxTipMachComp = 0.90, MaxTipMachTurb = 0.85,
                        FanPsi = 0.20, CompressorPsi = 0.28, TurbinePsi = 1.4,
                        TurbStagePrMax = 1.8, T4Limit = 1110.0, MechEfficiency = 0.97
                    }
                },
                {
                    "1960s", new TechLevel
                    {
                        Name = "1960s",
                        FanEtaPol = 0.84, LpcEtaPol = 0.85, HpcEtaPol = 0.86,
                        HptEtaPol = 0.82, LptEtaPol = 0.85, BurnerEta = 0.98,
                        BurnerDpRatio = 0.06, NozzleEta = 0.96,
                        MaxTipMachFan = 1.05, MaxTipMachComp = 1.00, MaxTipMachTurb = 0.90,
                        FanPsi = 0.22, CompressorPsi = 0.32, TurbinePsi = 1.9,
                        TurbStagePrMax = 2.1, T4Limit = 1390.0, MechEfficiency = 0.98
                    }
                },
                { "1970s", new TechLevel { Name = "1970s" } },
                {
                    "1990s", new TechLevel
                    {
                        Name = "1990s",
                        FanEtaPol = 0.90, LpcEtaPol = 0.91, HpcEtaPol = 0.92,
                        HptEtaPol = 0.88, LptEtaPol = 0.91, BurnerEta = 0.995,
                        BurnerDpRatio = 0.04, NozzleEta = 0.98,
                        MaxTipMachFan = 1.30, MaxTipMachComp = 1.10, MaxTipMachTurb = 0.95,
                        FanPsi = 0.28, CompressorPsi = 0.42, TurbinePsi = 3.0,
                        TurbStagePrMax = 2.7, T4Limit = 1780.0, MechEfficiency = 0.99
                    }
                },
                {
                    "2010s", new TechLevel
                    {
                        Name = "2010s",
                        FanEtaPol = 0.91, LpcEtaPol = 0.92, HpcEtaPol = 0.93,
                        HptEtaPol = 0.90, LptEtaPol = 0.92, BurnerEta = 0.996,
                        BurnerDpRatio = 0.035, NozzleEta = 0.985,
                        MaxTipMachFan = 1.35, MaxTipMachComp = 1.12, MaxTipMachTurb = 0.97,
                        FanPsi = 0.30, CompressorPsi = 0.45, TurbinePsi = 3.4,
                        TurbStagePrMax = 3.0, T4Limit = 2000.0, MechEfficiency = 0.995
                    }
                },
            };

        public static TechLevel Get(string name)
        {
            TechLevel t;
            return name != null && Presets.TryGetValue(name, out t) ? t : Presets["1970s"];
        }

        public TechLevel Clone()
        {
            return (TechLevel)MemberwiseClone();
        }
    }
}