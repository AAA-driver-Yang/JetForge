using System;

namespace JetForge.Core
{
    /// <summary>
    /// "烧这些油，转速会自己落到哪儿"——把解算器的稳态油量-转速关系抽成一张可按油量反查的表。
    ///
    /// 为什么需要它：以前油门直接当转速目标，于是"油门到底"和"油量下限"是两件事，
    /// 玩家没法表达"我就要少给油"。真机上飞行员推的是燃油计量阀，转速是**结果**：
    /// 油给多少，涡轮就产多少功，压气机吃不完的那部分才拿去加速转子。
    ///
    /// 这张表就是解算器在回答"给定转速，这台机器稳态要烧多少油"：
    /// 沿转速扫若干点各解一次，得到 `(转速, 油量)` 的单调曲线，反查就是
    /// "烧 X kg/s 时平衡在哪个转速"。低于表底的油量说明**没有任何转速能用这么点油维持**，
    /// 此时返回 0——让转子按惯量往下溜，掉到哪儿由解算器接着说。
    ///
    /// 这张表不进存档、不参与设计：它是运行期的查表，跟着高度/马赫/加力比例重新算。
    /// </summary>
    public sealed class FuelSchedule
    {
        /// <summary>扫多少个转速点。9 个够把曲线形状抓住，再多就只是让建表变慢。</summary>
        public const int Points = 9;

        public const double LoSpool = 0.15;
        public const double HiSpool = 1.00;

        public double Alt, Mach, AbFrac;

        /// <summary>扫掠用的转速点（换算转速比例 0~1）。</summary>
        public readonly double[] Spool = new double[Points];

        /// <summary>对应稳态油量（kg/s）。强制单调不减——见 <see cref="Build"/> 里的说明。</summary>
        public readonly double[] Fuel = new double[Points];

        /// <summary>满转速推力（N）。FADEC 要拿它把推力误差归一化。</summary>
        public double FullThrust;

        public bool Valid;

        /// <summary>表底油量：再少就没有稳态了（这是"最低可用喷油量"的运行期口径）。</summary>
        public double MinFuel { get { return Valid ? Fuel[0] : 0.0; } }

        /// <summary>表顶油量：满转速稳态油量。</summary>
        public double MaxFuel { get { return Valid ? Fuel[Points - 1] : 0.0; } }

        /// <summary>
        /// 建表。`sampler` 就是飞行中用的那个（网格或实时解算都行）。
        ///
        /// 单调化不是洁癖：低转速段解算器可能给出"转速更低反而更费油"的点
        /// （风车摩擦、燃烧效率极低时油气比乱飘）。带这种点的表反查会给出两个答案，
        /// 于是转速在两点之间来回跳——表现成"油量没变、转速自己抖"。
        /// 取前缀最大值之后，"油越多转速越高"这条基本事实就是硬约束。
        /// </summary>
        public static FuelSchedule Build(ICycleSampler sampler, double alt, double mach, double abFrac)
        {
            FuelSchedule s = new FuelSchedule();
            s.Alt = alt;
            s.Mach = mach;
            s.AbFrac = abFrac;
            if (sampler == null) return s;

            double maxFuel = 0.0;
            bool any = false;
            for (int i = 0; i < Points; i++)
            {
                double x = LoSpool + (HiSpool - LoSpool) * i / (Points - 1.0);
                s.Spool[i] = x;
                PerformanceGrid.Sample smp = sampler.Sample(x, alt, mach, abFrac);
                double f = smp.Valid && smp.FuelFlow > 0.0 ? smp.FuelFlow : 0.0;
                if (!smp.Valid || f <= 0.0)
                {
                    s.Fuel[i] = double.NaN;         // 这一档解不出来，插值时跳过
                    continue;
                }
                if (f < maxFuel) f = maxFuel;       // 单调化
                maxFuel = f;
                s.Fuel[i] = f;
                if (i == Points - 1) s.FullThrust = smp.Thrust;
                any = true;
            }
            s.Valid = any && maxFuel > 0.0;
            return s;
        }

        /// <summary>这个转速下稳态要烧多少油（kg/s）。解不出来的点按邻近点线性插。</summary>
        public double FuelAt(double spool)
        {
            if (!Valid) return 0.0;
            if (spool <= Spool[0]) return Fuel[0];
            if (spool >= Spool[Points - 1]) return Fuel[Points - 1];
            for (int i = 0; i < Points - 1; i++)
            {
                if (spool > Spool[i + 1]) continue;
                double f0 = Fuel[i], f1 = Fuel[i + 1];
                if (double.IsNaN(f0) && double.IsNaN(f1)) return 0.0;
                if (double.IsNaN(f0)) return f1;
                if (double.IsNaN(f1)) return f0;
                double t = (spool - Spool[i]) / Math.Max(Spool[i + 1] - Spool[i], 1e-6);
                return f0 + (f1 - f0) * t;
            }
            return Fuel[Points - 1];
        }

        /// <summary>
        /// 反查：烧这么多油，稳态转速落在哪儿。返回 0 表示"这个油量维持不了任何转速"——
        /// 调用方据此让转子往下溜，而不是硬塞一个转速给它。
        /// </summary>
        public double SpoolFor(double fuelKgPerSec)
        {
            if (!Valid || double.IsNaN(fuelKgPerSec)) return 0.0;
            if (fuelKgPerSec < Fuel[0]) return 0.0;

            for (int i = 0; i < Points - 1; i++)
            {
                double f0 = Fuel[i], f1 = Fuel[i + 1];
                if (double.IsNaN(f0) || double.IsNaN(f1)) continue;
                if (fuelKgPerSec > f1) continue;
                if (f1 - f0 < 1e-9) return Spool[i + 1];
                double t = (fuelKgPerSec - f0) / (f1 - f0);
                return Spool[i] + (Spool[i + 1] - Spool[i]) * t;
            }
            return Spool[Points - 1];
        }

        /// <summary>一行自检/日志文字。</summary>
        public string Describe()
        {
            if (!Valid) return Loc.T("油量-转速表：建不起来（解算器没给出有效点）", "Fuel-to-speed table: cannot be built (the solver gave no valid point)");
            return string.Format(Loc.T("油量-转速表：{0:F0} m / M{1:F2}｜油量 {2:F3}~{3:F3} kg/s｜", "Fuel-to-speed table: {0:F0} m / M{1:F2} | fuel {2:F3}~{3:F3} kg/s |")
                + "0.15→{4:F3}、0.60→{5:F3}、1.00→{6:F3}",
                Alt, Mach, MinFuel, MaxFuel, FuelAt(0.15), FuelAt(0.60), FuelAt(1.00));
        }
    }
}