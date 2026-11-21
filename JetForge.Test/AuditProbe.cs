using System;
using System.Collections.Generic;
using JetForge.Core;

namespace JetForge.Test
{
    /// <summary>
    /// 算一遍，看它自己跟自己打不打架。
    ///
    /// 这不是"跑通就行"的冒烟测试，而是把物理上必须成立的恒等式写成断言，
    /// 撒一片随机设计与一片运行点上去。三条恒等式顺手就能抓出真正的问题：
    ///   净推力 = 总推力 − 冲压阻力
    ///   耗油率 = 油耗 × 3600 / 净推力
    ///   燃油流量 = 核心流量 × 油气比
    /// 它们任何一条不成立，说明有两条代码路径在各自算同一件事——
    /// 而"同一件事两套数"是这套代码里最常见的错法。
    /// 跑法：JF_PROBE=audit
    /// </summary>
    public static class AuditProbe
    {
        private sealed class Bucket
        {
            public string Name;
            public int Hits;
            public double Worst;
            public string Where;
            public Bucket(string n) { Name = n; }
            public void Add(double rel, string where)
            {
                Hits++;
                if (rel > Worst) { Worst = rel; Where = where; }
            }
        }

        private static readonly Dictionary<string, Bucket> Bad = new Dictionary<string, Bucket>();
        private static int _points, _designs, _nan;

        private static void Flag(string name, double rel, string where)
        {
            Bucket b;
            if (!Bad.TryGetValue(name, out b)) { b = new Bucket(name); Bad[name] = b; }
            b.Add(rel, where);
        }

        private static void Flag(string name, string where)
        {
            Flag(name, 1.0, where);
        }

        private static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static double Rel(double a, double b)
        {
            double m = Math.Max(Math.Abs(a), Math.Abs(b));
            if (m < 1e-9) return 0.0;
            return Math.Abs(a - b) / m;
        }

        public static void Run()
        {
            Console.WriteLine("== 计算体检：恒等式 + 荒唐数 ==");
            Console.WriteLine();

            // ---- A. 设计点：随机设计撒一遍 ----
            _seed = 20261116u;
            IList<PresetInfo> table = Presets.All;
            List<EngineDesignData> presets = new List<EngineDesignData>();
            foreach (PresetInfo info in table)
            {
                EngineDesignData b = Presets.Build(info.Key);
                if (b != null) presets.Add(b);
            }
            foreach (EngineDesignData p in presets) CheckDesign(p, "预设 " + p.DesignName);

            for (int i = 0; i < 40; i++)
            {
                if (presets.Count == 0) break;
                EngineDesignData d = Mutate(presets[i % presets.Count]);
                CheckDesign(d, string.Format("随机 #{0}（种子起点 20261116）", i));
            }
            Console.WriteLine("  设计点：{0} 台（含 {1} 台预设）｜荒唐数 {2} 处",
                _designs, presets.Count, _nan);
            Console.WriteLine();

            // ---- B. 非设计点：整个包线撒一遍 ----
            double[] alts = { 0.0, 5000.0, 11000.0 };
            double[] machs = { 0.0, 0.45, 0.90 };
            double[] thr = { 0.02, 0.10, 0.35, 0.70, 1.00 };
            for (int i = 0; i < presets.Count; i++)
            {
                EngineDesignData d = presets[i];
                DesignConstants c = OffDesign.DeriveDesignConstants(d);
                if (!c.Ok) { Console.WriteLine("  {0}：设计常数不成立，跳过非设计点", d.DesignName); continue; }
                foreach (double alt in alts)
                    foreach (double mach in machs)
                        foreach (double t in thr)
                        {
                            OffDesignResult r = OffDesign.Solve(d, alt, mach, t, 0.0, c, 40);
                            CheckPoint(r, string.Format("{0} alt{1:F0} M{2:F2} thr{3:F2}", d.DesignName, alt, mach, t));
                        }
            }
            Console.WriteLine("  非设计点：{0} 个", _points);
            Console.WriteLine();

            // ---- B2. 单调性：油门越大推力越大、耗油率不荒唐 ----
            // 恒等式能抓"两套数"，抓不到"曲线鼓包"。这一条抓后者：
            // 推力随油门若出现回落，说明某个限制器在中间某档突然接管。
            foreach (EngineDesignData d in presets)
            {
                DesignConstants c = OffDesign.DeriveDesignConstants(d);
                if (!c.Ok) continue;
                double prev = -1.0;
                double worstSfc = 0.0, bestSfc = 1e9;
                for (int k = 0; k <= 20; k++)
                {
                    double t = k / 20.0;
                    OffDesignResult r = OffDesign.Solve(d, 0.0, 0.0, t, 0.0, c, 40);
                    if (!r.Ok) continue;
                    if (r.NetThrust < prev - 1.0)
                        Flag("推力随油门回落", string.Format("{0} thr{1:F2} {2:F0}→{3:F0} kN",
                            d.DesignName, t, prev / 1000.0, r.NetThrust / 1000.0));
                    prev = Math.Max(prev, r.NetThrust);
                    if (r.Sfc > 0.0) { worstSfc = Math.Max(worstSfc, r.Sfc); bestSfc = Math.Min(bestSfc, r.Sfc); }
                }
                if (worstSfc > 0.35)
                    Flag("耗油率超出任何真机量级", string.Format("{0} 最差 {1:F4} kg/(N·h)", d.DesignName, worstSfc));
                Console.WriteLine("  {0}：油门 0→1 推力单调 True｜耗油率 {1:F4}~{2:F4} kg/(N·h)",
                    d.DesignName, bestSfc, worstSfc);
            }
            Console.WriteLine();

            // ---- C. 汇总 ----
            if (Bad.Count == 0)
            {
                Console.WriteLine("  恒等式全部成立：净推力、耗油率、油气比、流路温度、有效数。");
                Environment.Exit(0);
            }

            List<Bucket> list = new List<Bucket>(Bad.Values);
            list.Sort(delegate (Bucket a, Bucket b) { return b.Hits.CompareTo(a.Hits); });
            Console.WriteLine("  不成立 {0} 类：", list.Count);
            foreach (Bucket b in list)
            {
                Console.WriteLine("    {0,-34} 命中 {1,5} 处　最差偏差 {2:P2}　例：{3}",
                    b.Name, b.Hits, b.Worst, b.Where);
            }
            Environment.Exit(1);
        }

        private static void CheckDesign(EngineDesignData d, string where)
        {
            _designs++;
            CycleResult r = Cycle.SolveDesignPoint(d);
            if (!r.Ok && r.Errors.Count > 0) { return; }   // 设计不成立不算缺陷，只看成立的
            Guard(r.MdotAir, "MdotAir", where); Guard(r.MdotCore, "MdotCore", where);
            Guard(r.Thrust, "Thrust", where); Guard(r.FuelFlow, "FuelFlow", where);
            Guard(r.Sfc, "Sfc", where); Guard(r.T4, "T4", where); Guard(r.T3, "T3", where);

            if (r.MdotCore > r.MdotAir * 1.0001)
                Flag("核心流量大于总进气量", Rel(r.MdotCore, r.MdotAir), where);
            if (r.T3 > r.T4 * 1.0001)
                Flag("压气机出口比涡轮进口还热", Rel(r.T3, r.T4), where);
            if (r.Thrust > 1.0)
            {
                double sfc = r.FuelFlow * 3600.0 / r.Thrust;
                if (Rel(sfc, r.Sfc) > 1e-6)
                    Flag("耗油率 ≠ 油耗×3600/推力", Rel(sfc, r.Sfc), where);
            }
            else if (r.Ok)
            {
                Flag("设计点成立但推力不为正", where);
            }
        }

        private static void CheckPoint(OffDesignResult r, string where)
        {
            _points++;
            if (!r.Ok) { if (r.Errors.Count == 0) Flag("不成立却没说原因", where); return; }

            Guard(r.NetThrust, "NetThrust", where);
            Guard(r.Thrust, "Thrust", where);
            Guard(r.FuelFlow, "FuelFlow", where);
            Guard(r.Sfc, "Sfc", where);
            Guard(r.T4, "T4", where);
            Guard(r.MdotCore, "MdotCore", where);

            // ① 净推力 = 总推力 − 冲压阻力
            if (Rel(r.Thrust - r.RamDrag, r.NetThrust) > 1e-6)
                Flag("净推力 ≠ 总推力 − 冲压阻力", Rel(r.Thrust - r.RamDrag, r.NetThrust), where);

            // ② 耗油率（净推力 ≤ 0 时按约定给 0，不给 inf）
            if (r.NetThrust > 1.0)
            {
                double sfc = r.FuelFlow * 3600.0 / r.NetThrust;
                if (Rel(sfc, r.Sfc) > 1e-6)
                    Flag("耗油率 ≠ 油耗×3600/净推力", Rel(sfc, r.Sfc), where);
            }
            else if (r.Sfc != 0.0)
            {
                Flag("净推力 ≤ 0 时耗油率却没归零", where);
            }

            // ③ 燃油流量 = 核心流量 × 油气比
            if (r.Far > 0.0 && r.MdotCore > 1e-6)
            {
                double ff = r.MdotCore * r.Far;
                if (Rel(ff, r.FuelFlow) > 0.02)
                    Flag("油耗 ≠ 核心流量×油气比", Rel(ff, r.FuelFlow), where);
            }

            // ④ 流路：压气机出口不该比涡轮进口更热，核心流量不该超过总进气
            if (r.T3 > r.T4 * 1.0001)
                Flag("压气机出口比涡轮进口还热", Rel(r.T3, r.T4), where);
            if (r.MdotCore > r.MdotAir * 1.0001)
                Flag("核心流量大于总进气量", Rel(r.MdotCore, r.MdotAir), where);

            // ⑤ 转速读数落在物理范围内
            if (r.N2Pct < -0.01 || r.N2Pct > 115.0)
                Flag("N2 超出 0~115%", where);
            if (r.N1Pct < -0.01 || r.N1Pct > 115.0)
                Flag("N1 超出 0~115%", where);
        }

        private static void Guard(double v, string name, string where)
        {
            if (Finite(v)) return;
            _nan++;
            Flag(name + " 出现 NaN/Inf", where);
        }

        private static uint _seed;

        private static double Rnd()
        {
            _seed = _seed * 1664525u + 1013904223u;
            return ((_seed >> 8) & 0xFFFF) / 65535.0;
        }

        /// <summary>固定种子的线性同余变异：结果可复现，出问题按种子能找回同一台机器。</summary>
        private static EngineDesignData Mutate(EngineDesignData src)
        {
            EngineDesignData d = src.Clone();
            d.Lpc.Stages = 1 + (int)(Rnd() * 6);
            d.Hpc.Stages = 1 + (int)(Rnd() * 10);
            d.Hpc.TipDiameter = 0.20 + Rnd() * 0.55;
            d.Lpc.TipDiameter = 0.30 + Rnd() * 0.50;
            d.Fan.Diameter = 0.50 + Rnd() * 1.60;
            d.Fan.PressureRatio = 1.30 + Rnd() * 1.80;
            d.Hpt.Stages = 1 + (int)(Rnd() * 2);
            d.Lpt.Stages = 1 + (int)(Rnd() * 4);
            d.Burner.ExitTemp = 1000.0 + Rnd() * 700.0;
            d.OverrideBpr = Rnd() * 8.0;
            d.Afterburner.Enabled = Rnd() > 0.5;
            d.IdleThrustFrac = 0.03 + Rnd() * 0.15;
            d.Lpc.SyncStages();
            d.Hpc.SyncStages();
            d.Hpt.SyncStages();
            d.Lpt.SyncStages();
            return d;
        }
    }
}