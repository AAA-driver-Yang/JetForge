using System;

namespace JetForge.Core
{
    /// <summary>
    /// 空中再起动的判定结果。
    ///
    /// 这一块以前是空白：熄火之后转子按惯性往风车转速溜，玩家按一下点火，
    /// 原版就把 <c>EngineIgnited</c> 置上、火"啪"地着了——不管在 3 km 还是 20 km。
    /// 真机的高空再起动是个窗口：转速太低带不起来、太高点火会富油超温、
    /// 进气压力太低点火器打不着。这里把这三条墙写出来。
    /// </summary>
    public struct RelightResult
    {
        /// <summary>这一刻能不能点着（三条墙全过）。</summary>
        public bool CanLight;

        /// <summary>风车转速（占额定，0~1）：来流吹着转子转到的那个值，也是再点火的最低可能转速。</summary>
        public double WindmillSpool;

        /// <summary>给了玩家读数用的当前转速（占额定，0~1）。</summary>
        public double Spool;

        /// <summary>这一档点火器要求的最低进气总压（kPa）。</summary>
        public double MinPressureKPa;

        /// <summary>当前工况的进气总压（kPa，进气道出口口径）。</summary>
        public double FacePressureKPa;

        /// <summary>转速落在再点火窗口里（12%~70%）。</summary>
        public bool InSpoolWindow;

        /// <summary>不能点的第一个原因；能点时为空串。</summary>
        public string Reason;

        /// <summary>一句话读数，面板与日志用。</summary>
        public string Note;
    }

    /// <summary>
    /// 高空再起动（windmill relight）窗口。
    ///
    /// 三条墙的来由，按真机：
    ///   ① **转速下限**：转得太慢，压气机压不出足够的压比、燃油雾化也差，
    ///      点着了也站不住火。起动机（或风车）至少得把 N2 带到 10~15%。
    ///   ② **转速上限**：转速高的时候点火，燃油在燃烧室里停留时间短、局部富油，
    ///      涡轮前温度会瞬间冲过限制。所以真机的空中再起动是先等转速掉进窗口，
    ///      不是"越高越好"。
    ///   ③ **点火能量 vs 压力**：点火器给的是固定能量，燃烧室压力越低越难点着。
    ///      这一条与加力点火同源（§177），但核心机的要求更低一点——它就在压气机后面，
    ///      而加力段还要经过涡轮与加力筒体。
    ///
    /// 诚实的边界：这是**窗口式标定模型**，不是从点火能量与化学反应动力学算的。
    /// 三档门槛（45/35/28/20 kPa 与 12%~70%）是按公开的再起动包线量级挑的，
    /// 不是某台真机的图纸。要更准需要那台机的再起动包线图。
    /// </summary>
    public static class Relight
    {
        /// <summary>再点火转速下限：低于它起动机/风车带不到自持。</summary>
        public const double MinSpoolFrac = 0.12;

        /// <summary>再点火转速上限：高于它点火会富油、涡轮前温冲顶。</summary>
        public const double MaxSpoolFrac = 0.70;

        /// <summary>这一档控制系统的最低再点火进气压力（kPa）。</summary>
        public static double MinPressureKPaOf(ControlKind control)
        {
            switch (control)
            {
                case ControlKind.None:
                case ControlKind.Mechanical: return 45.0;
                case ControlKind.Hydro: return 35.0;
                case ControlKind.Analog: return 28.0;
                case ControlKind.Fadec: return 20.0;
                default: return 35.0;
            }
        }

        /// <summary>数字机有自动再点火（熄火后自己重试）；模拟及以下要人动手。</summary>
        public static bool AutoRelight(ControlKind control)
        {
            return ControlSystem.Rank(control) >= ControlSystem.Rank(ControlKind.Fadec);
        }

        /// <summary>当前马赫下的进气道出口总压（kPa）。没有设计就按纯冲压算。</summary>
        public static double FacePressureKPa(EngineDesignData d, double alt, double mach)
        {
            double p, t;
            Gas.Isa(alt, out p, out t);
            double m = mach > 0.0 ? mach : 0.0;
            double pt = p * Math.Pow(1.0 + 0.2 * m * m, 3.5);
            if (d != null)
            {
                try { pt *= Intake.Recovery(d.Intake, d.Tech, m); }
                catch (Exception) { /* 探测豁免：没有进气道参数时按纯冲压算，不因为读不到设计就判"点不着" */ }
            }
            return pt / 1000.0;
        }

        /// <summary>
        /// 判定这一刻能不能点着。
        /// <paramref name="spool"/> 传当前实际转速比例（0~1）；传负数表示"用风车转速"。
        /// </summary>
        public static RelightResult Window(EngineDesignData d, double alt, double mach, double spool)
        {
            ControlKind ctl = d != null ? ControlSystem.Effective(d) : ControlKind.Hydro;
            RelightResult r = new RelightResult();
            r.MinPressureKPa = MinPressureKPaOf(ctl);

            WindmillState w = Windmill.Compute(d, alt, mach, 1.0);
            r.WindmillSpool = w.Valid ? Math.Max(0.0, Math.Min(1.0, w.SpoolStart)) : 0.0;
            r.Spool = spool >= 0.0 ? spool : r.WindmillSpool;

            r.InSpoolWindow = r.Spool >= MinSpoolFrac && r.Spool <= MaxSpoolFrac;
            r.FacePressureKPa = FacePressureKPa(d, alt, mach);

            r.CanLight = true;
            r.Reason = "";
            if (r.Spool < MinSpoolFrac)
            {
                r.CanLight = false;
                r.Reason = string.Format(Loc.T("转速 {0:F0}% 低于再点火下限 {1:F0}%：压气机压不出压比、燃油也雾化不了，"
                    + "点着了也站不住火。俯冲把风车转速吹起来（这一高度风车约 {2:F0}%）再试",
                    "Spool {0:F0}% is below the relight floor {1:F0}%: the compressor makes no pressure ratio and fuel will not atomise, "
                    + "so even if it lights the flame will not hold. Dive to spin the windmill up (about {2:F0}% at this altitude) and try again"),
                    r.Spool * 100.0, MinSpoolFrac * 100.0, r.WindmillSpool * 100.0);
            }
            else if (r.Spool > MaxSpoolFrac)
            {
                r.CanLight = false;
                r.Reason = string.Format(Loc.T("转速 {0:F0}% 高于再点火上限 {1:F0}%：这个转速点火会富油、涡轮前温冲顶。"
                    + "等转速掉进窗口再点（风车约 {2:F0}%）",
                    "Spool {0:F0}% is above the relight ceiling {1:F0}%: lighting here runs rich and spikes turbine inlet temperature. "
                    + "Let the spool fall into the window (windmill is about {2:F0}%) and try again"),
                    r.Spool * 100.0, MaxSpoolFrac * 100.0, r.WindmillSpool * 100.0);
            }
            else if (r.FacePressureKPa < r.MinPressureKPa)
            {
                r.CanLight = false;
                r.Reason = string.Format(Loc.T("进气总压 {0:F1} kPa 低于这一档点火器的 {1:F0} kPa：空气太稀，点火能量不够。"
                    + "降到 {2:F0} m 以下再试",
                    "Inlet total pressure {0:F1} kPa is below this igniter class requirement of {1:F0} kPa: the air is too thin for the ignition energy. "
                    + "Descend below {2:F0} m and try again"),
                    r.FacePressureKPa, r.MinPressureKPa, CeilingMeters(d, mach, ctl));
            }

            r.Note = string.Format(Loc.T("{0}｜转速 {1:F0}%｛窗口 {2:F0}%~{3:F0}%｝｜进气总压 {4:F1} kPa（门槛 {5:F0}）",
                "{0} | spool {1:F0}% (window {2:F0}%-{3:F0}%) | inlet total pressure {4:F1} kPa (threshold {5:F0})"),
                ctl == ControlKind.None ? Loc.T("无控", "no control") : ControlSystem.KindName(ctl),
                r.Spool * 100.0, MinSpoolFrac * 100.0, MaxSpoolFrac * 100.0,
                r.FacePressureKPa, r.MinPressureKPa);
            return r;
        }

        /// <summary>这一档在这个马赫下的再点火高度上限（进气压力掉到门槛的高度）。</summary>
        public static double CeilingMeters(EngineDesignData d, double mach, ControlKind control)
        {
            double need = MinPressureKPaOf(control);
            double lo = 0.0, hi = 30000.0;
            // 每 1 kPa 的刻度够了：高度差 1 m 对应不到 0.1 kPa。
            for (int i = 0; i < 40 && hi - lo > 1.0; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (FacePressureKPa(d, mid, mach) >= need) lo = mid; else hi = mid;
            }
            return lo;
        }
    }
}