using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace JetForge.Core
{
    /// <summary>
    /// 网格构建器：把一台设计在"油门 × 高度 × 马赫"三维上预解成表。
    ///
    /// 默认轴的取法有讲究：
    ///  - 高度取 0~22 km，因为 KSP 的 Kerbin 大气层顶在 70 km，但 20 km 以上空气密度已经
    ///    低到喷气机基本没推力，再细分是浪费格点。低空按 1 km 一格——推力对高度是指数的，
    ///    2 km 一格时线性内插在格子中央会偏 5%。
    ///  - 马赫取 0~2.2，亚声速与跨声速区加密（0.8/0.9/1.0 是进气道和风扇最难受的地方）。
    ///  - 油门 21 档（0.05 一步）。看着密，其实单点只要 ~36 µs，全表 0.2 s；
    ///    油门太稀时，玩家推杆的手感是"阶梯"而不是"连续"。
    /// </summary>
    public static class GridFactory
    {
        public static readonly double[] DefaultThrottles = Step(0.0, 1.0, 0.05);

        public static readonly double[] DefaultAlts =
        {
            0.0, 1000.0, 2000.0, 3000.0, 4000.0, 5000.0, 6000.0, 7000.0, 8000.0, 9000.0,
            10000.0, 11000.0, 12000.0, 14000.0, 16000.0, 18000.0, 20000.0, 22000.0
        };

        public static readonly double[] DefaultMachs =
        {
            0.0, 0.2, 0.4, 0.6, 0.75, 0.85, 0.95, 1.05, 1.2, 1.4, 1.6, 1.8, 2.0, 2.2
        };

        private static double[] Step(double from, double to, double step)
        {
            int n = (int)Math.Round((to - from) / step) + 1;
            double[] a = new double[n];
            for (int i = 0; i < n; i++) a[i] = from + i * step;
            a[n - 1] = to;
            return a;
        }

        /// <summary>
        /// 构表。耗时 = 点数 × 单点求解时间（残酷模式约 36 µs）。
        /// <para>
        /// <paramref name="abFrac"/> 是加力档位：0 = 干态，1 = 满加力。
        /// 之所以要当参数传进来，是因为加力不是一个"温度上限"，它会同时抬高总温、流量和落压比，
        /// 干态与加力态是两张不同的表，飞行中靠开关切换（原来这里写死 0.0，导致配了加力的
        /// 发动机飞行中一点加力也没有）。
        /// </para>
        /// </summary>
        public static PerformanceGrid Build(EngineDesignData d, DesignConstants c,
            double[] throttles = null, double[] alts = null, double[] machs = null, double abFrac = 0.0,
            IntakeInstall install = null)
        {
            double[] ths = throttles ?? DefaultThrottles;
            double[] als = alts ?? DefaultAlts;
            double[] mcs = machs ?? DefaultMachs;

            PerformanceGrid g = new PerformanceGrid { Throttle = ths, Alt = als, Mach = mcs };
            int n = ths.Length * als.Length * mcs.Length;
            g.Thrust = new double[n]; g.Sfc = new double[n]; g.MassFlow = new double[n];
            g.MdotCore = new double[n]; g.ThrustCore = new double[n];
            g.FuelFlow = new double[n]; g.N1 = new double[n]; g.N2 = new double[n]; g.N3 = new double[n];
            g.Egt = new double[n]; g.T7 = new double[n]; g.Valid = new bool[n];

            Stopwatch sw = Stopwatch.StartNew();
            for (int t = 0; t < ths.Length; t++)
                for (int a = 0; a < als.Length; a++)
                    for (int m = 0; m < mcs.Length; m++)
                    {
                        OffDesignResult o = OffDesign.Solve(d, als[a], mcs[m], ths[t], abFrac, c, 40, -1.0, 0.0, 1.0, 0, install);
                        int i = g.Index(t, a, m);
                        g.Thrust[i] = o.Ok ? o.NetThrust : 0.0;
                        g.Sfc[i] = o.Ok ? o.Sfc : 0.0;
                        g.MassFlow[i] = o.MdotAir;
                        g.MdotCore[i] = o.MdotCore;
                        g.ThrustCore[i] = o.ThrustCore;
                        g.FuelFlow[i] = o.FuelFlow;
                        g.N1[i] = o.N1Pct;
                        g.N2[i] = o.N2Pct;
                        g.N3[i] = o.N3Pct;
                        g.Egt[i] = o.T5;
                        g.T7[i] = o.T7;
                        g.Valid[i] = o.Ok && o.NetThrust > 0.0;
                    }
            sw.Stop();
            g.BuildSeconds = sw.Elapsed.TotalMilliseconds / 1000.0;
            g.BuiltAt = DateTime.Now.ToString("s");
            return g;
        }

        /// <summary>只构一张"单层"表，用于飞行中做备份或编辑器里快速预览。</summary>
        public static PerformanceGrid BuildCoarse(EngineDesignData d, DesignConstants c)
        {
            return Build(d, c,
                new[] { 0.0, 0.5, 1.0 },
                new[] { 0.0, 4000.0, 8000.0, 12000.0, 16000.0, 20000.0 },
                new[] { 0.0, 0.4, 0.8, 1.2, 2.0 });
        }

        /// <summary>
        /// 简单模式构表。同一套轴、同一张表结构，只是单点求解换成 <see cref="Simple"/>——
        /// 所以飞行中的内插代码两种模式完全共用，不需要为模式分叉。
        /// 单点耗时约 0.02 ms，270 点的表基本是瞬时的。
        /// </summary>
        public static PerformanceGrid BuildSimple(SimpleDesign sd, SimpleConstants c,
            double[] throttles = null, double[] alts = null, double[] machs = null, double abScale = -1.0)
        {
            double[] ths = throttles ?? DefaultThrottles;
            double[] als = alts ?? DefaultAlts;
            double[] mcs = machs ?? DefaultMachs;

            PerformanceGrid g = new PerformanceGrid { Throttle = ths, Alt = als, Mach = mcs };
            int n = ths.Length * als.Length * mcs.Length;
            g.Thrust = new double[n]; g.Sfc = new double[n]; g.MassFlow = new double[n]; g.MdotCore = new double[n]; g.ThrustCore = new double[n];
            g.FuelFlow = new double[n]; g.N1 = new double[n]; g.N2 = new double[n]; g.N3 = new double[n];
            g.Egt = new double[n]; g.T7 = new double[n]; g.Valid = new bool[n];

            Stopwatch sw = Stopwatch.StartNew();
            for (int t = 0; t < ths.Length; t++)
                for (int a = 0; a < als.Length; a++)
                    for (int m = 0; m < mcs.Length; m++)
                    {
                        SimpleResult o = Simple.Solve(sd, als[a], mcs[m], ths[t], abScale, c);
                        int i = g.Index(t, a, m);
                        g.Thrust[i] = o.Ok ? o.NetThrust : 0.0;
                        g.Sfc[i] = o.Ok ? o.Sfc : 0.0;
                        g.MassFlow[i] = o.MdotAir;
                        g.MdotCore[i] = o.MdotCore;
                        g.ThrustCore[i] = o.ThrustCore;
                        g.FuelFlow[i] = o.FuelFlow;
                        g.N1[i] = o.N1Pct;
                        // 简单模式涡喷只有一根轴：显示一个"N2 100%"会让人以为还有根轴
                        g.N2[i] = sd.Kind == EngineKind.Turbojet ? 0.0 : o.N2Pct;
                        g.N3[i] = o.N3Pct;
                        g.Egt[i] = o.T5;
                        g.T7[i] = o.T7;
                        g.Valid[i] = o.Ok && o.NetThrust > 0.0;
                    }
            sw.Stop();
            g.BuildSeconds = sw.Elapsed.TotalMilliseconds / 1000.0;
            g.BuiltAt = DateTime.Now.ToString("s");
            return g;
        }

        // ---------- 表缓存 ----------
        //
        // 为什么需要：一张全表 0.2 s。玩家在一艘火箭上装 4 台同样的发动机，载入存档时
        // 就是 0.8 s 的硬卡顿（每台的 PartModule 各建一张一模一样的表）；而在 VAB 里
        // 连续改参数时，每次 ApplyDesign 都要重算一遍。
        //
        // 缓存键必须是**设计本身的指纹**，不是零件实例——两台参数完全相同的发动机
        // 本来就该共用一张表，这也是"同型号发动机性能一致"这个直觉的实现。
        //
        // 返回的是同一个实例，所以调用方不许改它（现在没有调用方改，只有 Build 内部写
        // BuildSeconds/BuiltAt 两个诊断字段）。
        private const int CacheMax = 8;
        private static readonly Dictionary<string, PerformanceGrid> Cache = new Dictionary<string, PerformanceGrid>();
        private static readonly Dictionary<string, int> CacheStamp = new Dictionary<string, int>();
        private static int _stamp;

        /// <summary>命中次数（给自检和文档取证）。</summary>
        public static int CacheHits;
        /// <summary>未命中次数。</summary>
        public static int CacheMisses;

        public static int CacheCount { get { return Cache.Count; } }

        public static void ClearCache()
        {
            Cache.Clear();
            CacheStamp.Clear();
            CacheHits = 0;
            CacheMisses = 0;
            _stamp = 0;
        }

        private static string KeyOf(string tag, object design, object consts, double extra, string extra2 = "")
        {
            return tag + "|" + DesignSignature.Of(design) + "|" + DesignSignature.Of(consts)
                + "|" + extra.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + "|" + (extra2 ?? "");
        }

        private static PerformanceGrid Cached(string key, Func<PerformanceGrid> build)
        {
            PerformanceGrid g;
            if (Cache.TryGetValue(key, out g))
            {
                CacheHits++;
                CacheStamp[key] = ++_stamp;
                return g;
            }

            CacheMisses++;
            g = build();

            // 满了就淘汰最久没用的。8 张表 × ~340 KB ≈ 2.7 MB，换个存档也不会顶到内存。
            if (Cache.Count >= CacheMax)
            {
                string oldest = null;
                int best = int.MaxValue;
                foreach (KeyValuePair<string, int> kv in CacheStamp)
                    if (kv.Value < best) { best = kv.Value; oldest = kv.Key; }
                if (oldest != null) { Cache.Remove(oldest); CacheStamp.Remove(oldest); }
            }
            Cache[key] = g;
            CacheStamp[key] = ++_stamp;
            return g;
        }

        /// <summary>带缓存的残酷模式构表（同设计 + 同加力档位只算一次）。</summary>
        public static PerformanceGrid BuildCached(EngineDesignData d, DesignConstants c, double abFrac = 0.0,
            IntakeInstall install = null)
        {
            // 进气道零件的几何也要进缓存键：同一台发动机接两件大进气道与不接进气道，
            // 是两张不同的表（捕获面积、恢复系数都不一样）。
            string key = KeyOf("D", d, c, abFrac, install != null ? install.Signature : "");
            return Cached(key, delegate { return Build(d, c, null, null, null, abFrac, install); });
        }

        /// <summary>带缓存的简单模式构表。</summary>
        public static PerformanceGrid BuildSimpleCached(SimpleDesign sd, SimpleConstants c, double abScale = -1.0)
        {
            string key = KeyOf("S", sd, c, abScale);
            return Cached(key, delegate { return BuildSimple(sd, c, null, null, null, abScale); });
        }
    }
}