using System;
using System.Diagnostics;
using System.Threading;

namespace JetForge.Core
{
    /// <summary>
    /// "在这个油门/高度/马赫/加力比例下，这台发动机出多少推力"——查这一次的抽象。
    ///
    /// 为什么要抽出来：以前飞行链路只认 <see cref="PerformanceGrid"/>，于是所有状态
    /// 都被钉死在预解网格的格点上。可是玩家想看的恰恰是格点之间的东西——
    /// 喘振边缘、叶尖刚过音速、加力刚点着那半秒。网格插值在这些地方只会给出
    /// "两个格点之间的一条直线"，而真实曲线在那里是折的。
    ///
    /// 现在有两种实现：老的 GridSampler（预解 + 插值）与 RealtimeSampler（每次现算）。
    /// 飞行链路不认识"哪一种"，它只会问 Sample。
    /// </summary>
    public interface ICycleSampler
    {
        /// <param name="a8Frac">
        /// 喉道面积那一份的加力比例（0~1）。燃油与喷口是两个速率，面积要单独给；
        /// `-1`（缺省）表示"跟 abFrac 同档"——不关心作动筒延迟的调用方不用管这个参数。
        /// </param>
        PerformanceGrid.Sample Sample(double throttle, double alt, double mach, double abFrac, double a8Frac = -1.0);

        /// <summary>自检/状态栏用：这一路是怎么来的。</summary>
        string Describe();
    }

    /// <summary>
    /// 老路子：预解两张表，干湿之间按加力比例线性混合。
    /// 留着它有两个用处：①简单模式还在用网格；②回归里要能对照"新旧两条路差多少"。
    /// </summary>
    public sealed class GridSampler : ICycleSampler
    {
        public PerformanceGrid Dry;
        public PerformanceGrid Wet;

        public GridSampler(PerformanceGrid dry, PerformanceGrid wet = null)
        {
            Dry = dry;
            Wet = wet;
        }

        public PerformanceGrid.Sample Sample(double throttle, double alt, double mach, double abFrac, double a8Frac = -1.0)
        {
            PerformanceGrid.Sample s = Dry != null ? Dry.Interp(throttle, alt, mach) : new PerformanceGrid.Sample();
            if (Wet == null || abFrac <= 0.0 || !s.Valid) return s;
            PerformanceGrid.Sample w = Wet.Interp(throttle, alt, mach);
            if (!w.Valid) return s;
            // 加力只多烧油、多一个推力增量，进气量不动（涡轮前那一段没变）。
            s.Thrust += (w.Thrust - s.Thrust) * abFrac;
            s.FuelFlow += (w.FuelFlow - s.FuelFlow) * abFrac;
            s.T7 += (w.T7 - s.T7) * abFrac;
            s.Sfc = s.Thrust > 1e-6 ? s.FuelFlow * 3600.0 / s.Thrust : s.Sfc;
            return s;
        }

        public string Describe()
        {
            if (Dry == null) return "网格：没有";
            return string.Format("网格：{0}×{1}×{2} 点{3}（预解插值）",
                Dry.Nt, Dry.Na, Dry.Nm, Wet != null ? "，另有一张加力表" : "");
        }
    }

    /// <summary>
    /// 现算：每个刷新周期直接跑一次 <see cref="OffDesign.Solve"/>。
    ///
    /// 两条硬约束：
    /// ① **不能每帧算**。一次 Solve 在 12 次迭代量级、几十次气体性质调用，每帧跑会吃掉帧率。
    ///    所以按 <see cref="Hz"/> 节流，两次之间返回上一次的结果。
    /// ② **能在别的线程算就别的线程算**。Core 里没有 UnityEngine 调用，是纯数值，
    ///    所以这条链路天生可以搬。搬过去的代价是"结果会晚一到两帧"，
    ///    而发动机转子的时间常数是秒级——晚两帧完全听不出来。
    ///
    /// 线程挂了怎么办：任何异常都记下来并把 <see cref="Threaded"/> 关掉，
    /// 退回同步算。**不允许"线程死了以后就再也没有读数"这种静默失败。**
    /// </summary>
    public sealed class RealtimeSampler : ICycleSampler
    {
        private readonly EngineDesignData _design;
        private readonly DesignConstants _consts;
        private readonly int _maxIter;
        /// <summary>机上装的独立进气道合计口径（没装为 null）。</summary>
        public IntakeInstall Install;
        private readonly object _lock = new object();

        private PerformanceGrid.Sample _last = new PerformanceGrid.Sample();
        private bool _hasLast;
        private double _lastKeyT = double.NaN, _lastKeyA, _lastKeyM, _lastKeyAb, _lastKeyA8;

        /// <summary>刷新频率（Hz）。0 或负数 = 每帧都算（默认）。</summary>
        public double Hz = 0.0;
        /// <summary>
        /// 用"变化不大就不重算"的容差缓存。
        ///
        /// 教训：这个缓存一开，读数就不是连续的——值在容差里被冻住，出界那一刻跳一下。
        /// 玩家看到的"数据抖动"有一半是它造成的：alt 容差 15 m、马赫 0.02，
        /// 飞起来大概每秒跳几次，振幅按推力对不同量的敏感度算得出好几个百分点。
        /// 现在默认关掉：单次 Solve 只要 0.1~0.25 ms，每帧算百分之一点几的帧时间，
        /// 比"跳"划算得多。留着这个开关是为了给极端低端机一个退路。
        /// </summary>
        public bool UseToleranceCache = false;
        /// <summary>两次计算之间允许的油门/高度/马赫变化：小到不用重算就不重算。</summary>
        public double TolThrottle = 0.01, TolAlt = 15.0, TolMach = 0.02, TolAb = 0.02;
        /// <summary>算过多少次、最近一次耗时（毫秒）——自检与状态栏都用这个数。</summary>
        public int SolveCount;
        public double LastMs;
        public string LastError = "";

        public RealtimeSampler(EngineDesignData d, DesignConstants c, int maxIter = 24,
                               IntakeInstall install = null)
        {
            _design = d;
            Install = install;
            _consts = c;
            _maxIter = maxIter;
        }

        public PerformanceGrid.Sample Sample(double throttle, double alt, double mach, double abFrac, double a8Frac = -1.0)
        {
            PerformanceGrid.Sample hit;
            if (TryGetCached(throttle, alt, mach, abFrac, a8Frac, out hit)) return hit;

            // 刷新率在这里生效：值变了但还没到下次该算的时候，就先给上一次的数。
            // 这一句是"能不能每帧算"的闸门——没有它，一条油门拉杆能把帧率拉下来。
            double nowMs = Clock.Elapsed.TotalMilliseconds;
            if (Hz > 0.0 && _hasLast && nowMs - _lastSolveMs < 1000.0 / Hz)
            {
                lock (_lock) { return _last; }
            }

            PerformanceGrid.Sample fresh = SolveNow(throttle, alt, mach, abFrac, a8Frac);
            lock (_lock) { _last = fresh; _hasLast = true; }
            return fresh;
        }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private double _lastSolveMs;

        private bool TryGetCached(double throttle, double alt, double mach, double abFrac, double a8Frac, out PerformanceGrid.Sample s)
        {
            s = null;
            if (!UseToleranceCache) return false;
            lock (_lock)
            {
                if (!_hasLast || double.IsNaN(_lastKeyT)) return false;
                if (Math.Abs(throttle - _lastKeyT) > TolThrottle) return false;
                if (Math.Abs(alt - _lastKeyA) > TolAlt) return false;
                if (Math.Abs(mach - _lastKeyM) > TolMach) return false;
                if (Math.Abs(abFrac - _lastKeyAb) > TolAb) return false;
                if (a8Frac >= 0.0 && Math.Abs(a8Frac - _lastKeyA8) > TolAb) return false;
                s = _last;
                return true;
            }
        }

        private PerformanceGrid.Sample SolveNow(double throttle, double alt, double mach, double abFrac, double a8Frac = -1.0)
        {
            var sw = Stopwatch.StartNew();
            PerformanceGrid.Sample s = new PerformanceGrid.Sample();
            try
            {
                OffDesignResult o = OffDesign.Solve(_design, alt, mach, throttle, abFrac, _consts, _maxIter,
                                                    -1.0, 0.0, 1.0, 0, Install, a8Frac);
                s.Thrust = o.Ok ? o.NetThrust : 0.0;
                s.Sfc = o.Ok ? o.Sfc : 0.0;
                s.MassFlow = o.MdotAir;
                s.MdotCore = o.MdotCore;
                s.ThrustCore = o.ThrustCore;
                s.FuelFlow = o.FuelFlow;
                s.N1 = o.N1Pct;
                s.N2 = o.N2Pct;
                s.N3 = o.N3Pct;
                s.Egt = o.T5;
                s.T7 = o.T7;
                s.T4 = o.T4;
                // 剩余功率：推力变化的快慢由它决定（见 SpoolDynamics），所以跟着采样点一起带出去。
                s.WComp = o.WComp;
                s.SurplusW = SpoolDynamics.HeadroomW(o);
                s.Valid = o.Ok && o.NetThrust > 0.0;
                if (!o.Ok && o.Errors.Count > 0) LastError = o.Errors[0];
                else LastError = "";
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                s.Valid = false;
            }
            sw.Stop();
            LastMs = sw.Elapsed.TotalMilliseconds;
            _lastSolveMs = Clock.Elapsed.TotalMilliseconds;
            SolveCount++;
            lock (_lock) { _lastKeyT = throttle; _lastKeyA = alt; _lastKeyM = mach; _lastKeyAb = abFrac; _lastKeyA8 = a8Frac; }
            return s;
        }

        /// <summary>设计换了（玩家在 VAB 里改了参数）：把手里的缓存作废。</summary>
        public void Invalidate()
        {
            lock (_lock) { _hasLast = false; _lastKeyT = double.NaN; }
        }

        public string Describe()
        {
            return string.Format("实时：{0}｜已算 {1} 次｜最近 {2:F2} ms{3}",
                Hz > 0.0 ? string.Format("{0:F0} Hz", Hz) : "每帧算", SolveCount, LastMs,
                string.IsNullOrEmpty(LastError) ? "" : "｜上一处不成立：" + LastError);
        }
    }
}