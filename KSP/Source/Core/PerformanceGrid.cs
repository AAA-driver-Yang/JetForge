using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace JetForge.Core
{
    /// <summary>
    /// 预解性能网格：油门 × 高度 × 马赫。飞行中不再跑求解器，只在这里做三线性内插。
    ///
    /// 为什么要预解：残酷模式单点约 0.7 ms，看着不慢，但 KSP 每帧每台发动机都要问一次推力，
    /// 十台发动机就是 7 ms/帧——帧率比物理精度重要。节流阀离散成 5~9 档也符合玩家直觉。
    /// </summary>
    public class PerformanceGrid
    {
        public double[] Throttle = new double[0];
        public double[] Alt = new double[0];
        public double[] Mach = new double[0];

        // 平铺数组，索引 = (ti * na + ai) * nm + mi
        public double[] Thrust = new double[0];
        public double[] Sfc = new double[0];
        public double[] MassFlow = new double[0];
        /// <summary>核心机流量（不含涵道）。羽流要用它：加力只烧核心那一路，
        /// 大涵道比的机器拿总流量去算尾焰速度与直径，尾焰会粗得离谱。</summary>
        public double[] MdotCore = new double[0];
        /// <summary>核心机那一路自己出的净推力（不含涵道）。加力羽流要用它算射流速度：
        /// 拿总推力除核心流量会把尾焰吹得比真机快一倍。</summary>
        public double[] ThrustCore = new double[0];
        public double[] FuelFlow = new double[0];
        public double[] N1 = new double[0];
        public double[] N2 = new double[0];
        /// <summary>第三根轴的转速（只有三轴机才非零）。数组长度等于该列定义的格点数。</summary>
        public double[] N3 = new double[0];
        public double[] Egt = new double[0];
        /// <summary>排气总温（加力开着时是加力出口温度，关着时等于 Egt）。</summary>
        public double[] T7 = new double[0];
        public bool[] Valid = new bool[0];

        public double BuildSeconds;
        public string BuiltAt = "";

        public int Na { get { return Alt.Length; } }
        public int Nm { get { return Mach.Length; } }
        public int Nt { get { return Throttle.Length; } }

        public int Index(int ti, int ai, int mi)
        {
            return (ti * Na + ai) * Nm + mi;
        }

        public sealed class Sample
        {
            public double Thrust, Sfc, MassFlow, MdotCore, ThrustCore, FuelFlow, N1, N2, N3, Egt, T7;
            /// <summary>
            /// 涡轮前温度（K）。控制系统的限温闸要读它，所以它必须跟着采样点一起出来。
            /// 以前 Sample 里没有这个数，控制器只能拿 EGT 当替身——那差着 300 K，限温会限错地方。
            /// </summary>
            public double T4;
            /// <summary>压气机在这点吃的功（W）。转子加速要跟它比。</summary>
            public double WComp;
            /// <summary>涡轮还能多给出来的功（W）：逐台"能提供"减"必须提供"。0 = 一点余量都没有。</summary>
            public double SurplusW;
            public bool Valid;
        }

        /// <summary>在网格内三线性内插。越界按边界夹（不做外推——外推出来的推力会骗人）。</summary>
        public Sample Interp(double throttle, double alt, double mach)
        {
            Sample s = new Sample();
            if (Nt == 0 || Na == 0 || Nm == 0) return s;

            AxisW wT = AxisWeights(Throttle, throttle);
            AxisW wA = AxisWeights(Alt, alt);
            AxisW wM = AxisWeights(Mach, mach);

            double thrust = 0, sfc = 0, mdot = 0, mcore = 0, tcore = 0, ff = 0, n1 = 0, n2 = 0, n3 = 0, egt = 0, t7 = 0, wsum = 0;
            for (int a = 0; a < 2; a++)
                for (int b = 0; b < 2; b++)
                    for (int c = 0; c < 2; c++)
                    {
                        double w = wT.W[a] * wA.W[b] * wM.W[c];
                        if (w <= 0.0) continue;
                        int i = Index(wT.I[a], wA.I[b], wM.I[c]);
                        if (i < 0 || i >= Thrust.Length) continue;
                        // 推力这一列**不按有效点归一化**：格点无效的意思就是"这个状态下烧不出推力"，
                        // 它自己就是 0（GridFactory 存的就是 0）。以前把无效点整条跳过再除以剩下的权重，
                        // 等于拿隔壁几公里外那个有效点的推力冒充当前点——实测最坏偏 3000%。
                        thrust += w * Thrust[i];
                        if (!Valid[i]) continue;
                        sfc += w * Sfc[i];
                        mdot += w * MassFlow[i];
                        if (MdotCore.Length == MassFlow.Length) mcore += w * MdotCore[i]; else mcore += w * MassFlow[i];
                        if (ThrustCore.Length == MassFlow.Length) tcore += w * ThrustCore[i];
                        ff += w * FuelFlow[i];
                        n1 += w * N1[i];
                        n2 += w * N2[i];
                        if (N3.Length == N2.Length) n3 += w * N3[i];
                        egt += w * Egt[i];
                        if (T7.Length == Egt.Length) t7 += w * T7[i]; else t7 += w * Egt[i];
                        wsum += w;
                    }
            // 诊断量（SFC/流量/转速）仍然只在有效点里归一化：无效点的 SFC 是 0，
            // 混进来会算出"零油耗无限比冲"，比没有读数更坏。
            if (wsum <= 1e-9)
            {
                s.Thrust = thrust;
                s.Valid = false;
                return s;
            }
            s.Thrust = thrust;
            s.Sfc = sfc / wsum; s.MassFlow = mdot / wsum; s.MdotCore = mcore / wsum;
            s.ThrustCore = tcore / wsum;
            s.FuelFlow = ff / wsum; s.N1 = n1 / wsum; s.N2 = n2 / wsum; s.N3 = n3 / wsum;
            s.Egt = egt / wsum; s.T7 = t7 / wsum;
            // 有效权重过半才算"这台机器在这一带站得住"。边界上的格子会平滑退到 0，
            // 而不是像以前那样在某个格点突然从有推力跳到没推力。
            s.Valid = wsum >= 0.5 - 1e-9;
            return s;
        }

        private struct AxisW
        {
            public int[] I;
            public double[] W;
        }

        private static AxisW AxisWeights(double[] axis, double x)
        {
            AxisW r = new AxisW { I = new int[2], W = new double[2] };
            int n = axis.Length;
            if (n == 0) { r.I[0] = r.I[1] = 0; r.W[0] = 1; return r; }
            if (n == 1) { r.I[0] = r.I[1] = 0; r.W[0] = 1; return r; }
            if (x <= axis[0]) { r.I[0] = r.I[1] = 0; r.W[0] = 1; return r; }
            if (x >= axis[n - 1]) { r.I[0] = r.I[1] = n - 1; r.W[0] = 1; return r; }
            int i = 0;
            while (i < n - 2 && x > axis[i + 1]) i++;
            double span = axis[i + 1] - axis[i];
            double f = span > 1e-9 ? (x - axis[i]) / span : 0.0;
            r.I[0] = i; r.I[1] = i + 1;
            r.W[0] = 1.0 - f; r.W[1] = f;
            return r;
        }

        public string Summary()
        {
            int valid = 0;
            double peak = 0.0, bestSfc = double.PositiveInfinity;
            for (int i = 0; i < Thrust.Length; i++)
            {
                if (!Valid[i]) continue;
                valid++;
                if (Thrust[i] > peak) peak = Thrust[i];
                if (Sfc[i] > 0.0 && Sfc[i] < bestSfc) bestSfc = Sfc[i];
            }
            return string.Format(
                "{0} 油门 × {1} 高度 × {2} 马赫 = {3} 点；有效 {4}；峰值推力 {5:F1} kN；最低 SFC {6:F4} kg/(N·h)；构建 {7:F2} s",
                Nt, Na, Nm, Thrust.Length, valid, peak / 1000.0,
                double.IsInfinity(bestSfc) ? 0.0 : bestSfc, BuildSeconds);
        }

        // ---- 持久化 ------------------------------------------------------

        public void ToCsv(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("throttle,alt_m,mach,thrust_n,sfc,mass_flow,fuel_flow,n1_pct,n2_pct,egt_k,valid,t7_k,n3_pct,mdot_core,thrust_core");
            CultureInfo ci = CultureInfo.InvariantCulture;
            for (int t = 0; t < Nt; t++)
                for (int a = 0; a < Na; a++)
                    for (int m = 0; m < Nm; m++)
                    {
                        int i = Index(t, a, m);
                        sb.AppendLine(string.Format(ci,
                            "{0:F3},{1:F1},{2:F3},{3:F1},{4:F6},{5:F3},{6:F4},{7:F2},{8:F2},{9:F1},{10},{11:F1},{12:F2},{13:F3},{14:F1}",
                            Throttle[t], Alt[a], Mach[m], Thrust[i], Sfc[i], MassFlow[i],
                            FuelFlow[i], N1[i], N2[i], Egt[i], Valid[i] ? 1 : 0,
                            T7.Length == Egt.Length ? T7[i] : Egt[i],
                            N3.Length == N2.Length ? N3[i] : N2[i],
                            MdotCore.Length == MassFlow.Length ? MdotCore[i] : MassFlow[i],
                            ThrustCore.Length == MassFlow.Length ? ThrustCore[i] : Thrust[i]));
                    }
            File.WriteAllText(path, sb.ToString());
        }

        public static PerformanceGrid FromCsv(string path)
        {
            PerformanceGrid g = new PerformanceGrid();
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2) return g;
            List<double[]> rows = new List<double[]>();
            List<bool> val = new List<bool>();
            bool hasN3 = lines[0].IndexOf("n3_pct", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasT7 = lines[0].IndexOf("t7_k", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasCore = lines[0].IndexOf("mdot_core", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasFcore = lines[0].IndexOf("thrust_core", StringComparison.OrdinalIgnoreCase) >= 0;
            CultureInfo ci = CultureInfo.InvariantCulture;
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] p = lines[i].Split(',');
                if (p.Length < 11) continue;
                double[] r = new double[15];
                for (int k = 0; k < 10; k++) double.TryParse(p[k], NumberStyles.Float, ci, out r[k]);
                if (hasN3 && p.Length > 12) double.TryParse(p[12], NumberStyles.Float, ci, out r[10]);
                if (hasT7 && p.Length > 11) double.TryParse(p[11], NumberStyles.Float, ci, out r[11]);
                if (hasCore && p.Length > 13) double.TryParse(p[13], NumberStyles.Float, ci, out r[13]);
                if (hasFcore && p.Length > 14) double.TryParse(p[14], NumberStyles.Float, ci, out r[14]);
                rows.Add(r);
                val.Add(p[10].Trim() == "1");
            }
            if (rows.Count == 0) return g;
            g.Throttle = DistinctCol(rows, 0); g.Alt = DistinctCol(rows, 1); g.Mach = DistinctCol(rows, 2);
            int n = g.Nt * g.Na * g.Nm;
            g.Thrust = new double[n]; g.Sfc = new double[n]; g.MassFlow = new double[n]; g.MdotCore = new double[n];
            g.ThrustCore = new double[n];
            g.FuelFlow = new double[n]; g.N1 = new double[n]; g.N2 = new double[n];
            g.N3 = new double[n]; g.Egt = new double[n]; g.T7 = new double[n]; g.Valid = new bool[n];
            foreach (double[] r in rows)
            {
                int t = ArrayIndex(g.Throttle, r[0]), a = ArrayIndex(g.Alt, r[1]), m = ArrayIndex(g.Mach, r[2]);
                int i = g.Index(t, a, m);
                g.Thrust[i] = r[3]; g.Sfc[i] = r[4]; g.MassFlow[i] = r[5];
                g.FuelFlow[i] = r[6]; g.N1[i] = r[7]; g.N2[i] = r[8]; g.Egt[i] = r[9];
                // 老 CSV 没有 T7 列：退回涡轮出口温度（干态两者相等，加力态才有差别）
                g.T7[i] = hasT7 && r[11] > 1.0 ? r[11] : r[9];
                g.N3[i] = hasN3 ? r[10] : r[8];   // 老 CSV 没有 N3 列，退回 N2（双轴机本来就该一样）
                // 老 CSV 没有核心流量列：退回总流量（涡喷上两者相同，涡扇上会把羽流算粗，
                // 所以宁可标注出来也不假装有值）
                g.MdotCore[i] = hasCore && r[13] > 0.0 ? r[13] : r[5];
                // 老 CSV 没有核心推力列：退回总推力（干态涡扇会略微高估核心射流速度）
                g.ThrustCore[i] = hasFcore && r[14] > 0.0 ? r[14] : r[3];
                g.Valid[i] = true;
            }
            return g;
        }

        private static double[] DistinctCol(List<double[]> rows, int col)
        {
            List<double> v = new List<double>();
            foreach (double[] r in rows) if (!v.Contains(Math.Round(r[col], 4))) v.Add(Math.Round(r[col], 4));
            v.Sort();
            return v.ToArray();
        }

        private static int ArrayIndex(double[] axis, double x)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < axis.Length; i++)
            {
                double d = Math.Abs(axis[i] - x);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }
    }
}