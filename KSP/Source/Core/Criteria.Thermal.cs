using System;
using System.Collections.Generic;
using System.Linq;

namespace JetForge.Core
{
    /// <summary>
    /// 热力判据：温度上限、油气比、燃烧室形状、加力点火。
    ///
    /// 从 `Criteria.cs` 拆出来的：判据一共十七组，全挤在一个文件里时
    /// 温度在哪、轴在哪、堵塞在哪只能靠滚；拆开之后一族一个文件，
    /// 加一条判据只需要看那一族的前后文。
    /// </summary>
    public static partial class Criteria
    {
        public static List<Finding> CheckTemperatures(ResultView v, TechLevel tech)
        {
            List<Finding> o = new List<Finding>();
            double t3 = v.Get("T3"), t4 = v.Get("T4"), t5 = v.Get("T5"), t7 = v.Get("T7");

            if (t3 > 0.0)
            {
                string lvl = t3 > tech.T3Limit * 1.05 ? Level.Fail
                           : (t3 > tech.T3Limit ? Level.Warn : Level.Ok);
                o.Add(new Finding("T3", lvl,
                    string.Format(Loc.T("压气机出口 {0:F0} K（盘/叶片材料限 {1:F0} K）", "Compressor exit {0:F0} K (disc and blade material limit {1:F0} K)"), t3, tech.T3Limit),
                    t3, tech.T3Limit, "K"));
            }
            if (t4 > 0.0)
            {
                double lim = tech.T4Limit;
                string lvl = t4 > lim * 1.02 ? Level.Fail : (t4 > lim ? Level.Warn : Level.Ok);
                o.Add(new Finding("T4", lvl,
                    string.Format(Loc.T("涡轮入口 {0:F0} K（上限 {1:F0} K）", "Turbine inlet {0:F0} K (limit {1:F0} K)"), t4, lim), t4, lim, "K"));
            }
            double egtLim = tech.EgtRatio * tech.T4Limit;
            if (t5 > 0.0)
            {
                string lvl = t5 > egtLim * 1.05 ? Level.Fail : (t5 > egtLim ? Level.Warn : Level.Ok);
                o.Add(new Finding("EGT", lvl,
                    string.Format(Loc.T("排气温度 {0:F0} K（上限 {1:F0} K）", "Exhaust gas {0:F0} K (limit {1:F0} K)"), t5, egtLim), t5, egtLim, "K"));
            }
            if (t7 > 0.0 && v.Get("far_ab") > 0.0)
            {
                double lim = Cycle.AbMaxTemp(tech);
                string lvl = t7 > lim * 1.02 ? Level.Fail : (t7 > lim ? Level.Warn : Level.Ok);
                o.Add(new Finding("T7", lvl,
                    string.Format(Loc.T("加力出口 {0:F0} K（上限 {1:F0} K）", "Afterburner exit {0:F0} K (limit {1:F0} K)"), t7, lim), t7, lim, "K"));
            }
            return o;
        }

        public static List<Finding> CheckCombustion(ResultView v, TechLevel tech)
        {
            List<Finding> o = new List<Finding>();
            double far = v.Get("far");
            if (far > 0.0)
            {
                if (far < tech.FarLbo)
                    o.Add(new Finding("LBO", Level.Fail,
                        string.Format(Loc.T("主燃油气比 {0:F4} 低于贫油熄火线 {1:F4}：会熄火", "Core fuel/air {0:F4} is below the lean blowout line {1:F4}: it will flame out"), far, tech.FarLbo),
                        far, tech.FarLbo, "-"));
                else if (far > tech.FarRich)
                    o.Add(new Finding("rich", Level.Fail,
                        string.Format(Loc.T("主燃油气比 {0:F4} 超过富油线 {1:F4}：冒烟/熄火", "Core fuel/air {0:F4} is past the rich line {1:F4}: smoke, then flameout"), far, tech.FarRich),
                        far, tech.FarRich, "-"));
                else
                    o.Add(new Finding("far", Level.Ok,
                        string.Format(Loc.T("主燃油气比 {0:F4}（熄火区间 {1:F4}~{2:F4}）", "Core fuel/air {0:F4} (blowout band {1:F4} to {2:F4})"), far, tech.FarLbo, tech.FarRich),
                        far, tech.FarRich, "-"));
            }
            return o;
        }

        /// <summary>
        /// 燃烧室形状（m00001 原文里的「燃烧室类型和形状」）。类型决定基准压损/效率与缺省形状，
        /// 形状再在上面加减：火焰筒太短烧不完，太长压损高，罐式出口温度不均。
        /// </summary>
        public static List<Finding> CheckBurnerShape(EngineDesignData d)
        {
            List<Finding> o = new List<Finding>();
            if (d == null || d.Burner == null) return o;
            Cycle.BurnerPerf bp = Cycle.BurnerPerformance(d.Burner);

            double defLd = d.Burner.Kind == BurnerKind.Can ? 3.0
                         : d.Burner.Kind == BurnerKind.Cannular ? 2.6
                         : d.Burner.Kind == BurnerKind.ShortAnnular ? 1.6 : 2.2;

            if (bp.Ld < 1.6)
                o.Add(new Finding("burner.liner", Level.Warn, string.Format(
                    Loc.T("火焰筒长径比 {0:F2}（缺省 {1:F1}）：筒太短，油滴来不及烧完，效率只剩 {2:F3}", "Liner length/diameter {0:F2} (default {1:F1}): too short for the fuel to burn out, efficiency down to {2:F3}"),
                    bp.Ld, defLd, bp.Eta)));
            else if (bp.Ld > 4.5)
                o.Add(new Finding("burner.liner", Level.Warn, string.Format(
                    Loc.T("火焰筒长径比 {0:F2} 太长：摩擦与掺混损失叠加后总压损失 {1:P1}，比环形机该有的高", "Liner length/diameter {0:F2} too long: friction plus mixing push total pressure loss to {1:P1}, above what an annular chamber needs"),
                    bp.Ld, 1.0 - bp.Sigma)));
            else
                o.Add(new Finding("burner.liner", Level.Ok, string.Format(
                    Loc.T("火焰筒长径比 {0:F2}，效率 {1:F3}，总压损失 {2:P1}", "Liner length/diameter {0:F2}, efficiency {1:F3}, total pressure loss {2:P1}"), bp.Ld, bp.Eta, 1.0 - bp.Sigma)));

            if (bp.PatternFactor > 0.16)
                o.Add(new Finding("burner.pattern", Level.Warn, string.Format(
                    Loc.T("出口温度不均匀度估计 {0:F2}（罐式常见 0.12~0.18）：涡轮进口热点会比平均温高约 {1:F0} K，", "Exit temperature non-uniformity {0:F2} (can combustors run 0.12 to 0.18): the turbine inlet hot spot sits about {1:F0} K above the mean,") +
                    Loc.T("真实发动机要按热点定涡轮寿命", "real engines size turbine life against that hot spot"), bp.PatternFactor, bp.PatternFactor * d.Burner.ExitTemp)));

            if (d.Burner.Kind == BurnerKind.Annular || d.Burner.Kind == BurnerKind.ShortAnnular)
            {
                if (d.Burner.CanCount > 0)
                    o.Add(new Finding("burner.shape", Level.Warn, string.Format(
                        Loc.T("环形燃烧室没有「罐」，罐数 {0} 被忽略（罐式/罐环才需要）", "An annular combustor has no cans, so can count {0} is ignored (only can and can-annular need it)"), d.Burner.CanCount)));
            }
            else if (bp.Cans < 4 || bp.Cans > 18)
                o.Add(new Finding("burner.cans", Level.Warn, string.Format(
                    Loc.T("{0} 罐数 {1}：真机一般 6~16 罐，太多装不下、太少火焰太长", "{0} can count {1}: real engines run 6 to 16 cans; more will not fit, fewer makes the flame too long"),
                    d.Burner.Kind == BurnerKind.Can ? Loc.T("罐式", "can") : Loc.T("罐环式", "can-annular"), bp.Cans)));
            return o;
        }

        /// <summary>
        /// 加力点火边界。此前模型里只要配了加力就一定点着，玩家一路拉到 25 km 按加力照样推；
        /// 真机在高空点不着、油气比太大还会被吹熄，这两件事得在设计阶段就说清楚。
        /// </summary>
        public static List<Finding> CheckAfterburnerLight(EngineDesignData d)
        {
            List<Finding> o = new List<Finding>();
            if (d == null || d.Afterburner == null) return o;
            if (!d.Afterburner.Enabled)
            {
                o.Add(new Finding("ab.none", Level.Ok, Loc.T("这台没配加力", "no afterburner fitted")));
                return o;
            }

            double far = AfterburnerLight.EffectiveFar(d.Afterburner);
            double designAlt = d.DesignAlt;
            double designMach = d.DesignMach;
            // 判据要按这台机器**实际会装的**控制系统算点火边界：1950s 的老点火器与数字机
// 能点着的高度不是一回事，拿老口径判数字机会把"点不着"报成 OK（或反过来）。
            AbResult at = AfterburnerLight.Check(d, designAlt, designMach, true, ControlSystem.Effective(d));

            if (at.Lit)
                o.Add(new Finding("ab.light", Level.Ok, string.Format(
                    Loc.T("设计点能点着：{0}", "Lights at the design point: {0}"), at.Note)));
            else
                o.Add(new Finding("ab.light", Level.Warn, string.Format(
                    Loc.T("设计点加力点不着：{0}", "Afterburner will not light at the design point: {0}"), at.Note)));

            if (far < AfterburnerLight.FarLean * 1.5)
                o.Add(new Finding("ab.far_lean", Level.Warn, string.Format(
                    Loc.T("加力油气比 {0:F4} 偏低（贫油熄火极限 {1:F3}）：再小一点就点不着，正常做法是 0.03~0.05", "Afterburner fuel/air {0:F4} is low (lean blowout at {1:F3}): a bit less and it will not light; normal is 0.03 to 0.05"),
                    far, AfterburnerLight.FarLean)));
            else if (far > AfterburnerLight.FarRich * 0.85)
                o.Add(new Finding("ab.far_rich", Level.Warn, string.Format(
                    Loc.T("加力油气比 {0:F4} 偏高（富油吹熄极限 {1:F3}）：真机这个量级会放炮再熄火", "Afterburner fuel/air {0:F4} is high (rich blowout at {1:F3}): a real engine would bang and flame out at this level"),
                    far, AfterburnerLight.FarRich)));

            double ceil = AfterburnerLight.CeilingMeters(d, designMach);
            o.Add(new Finding("ab.ceiling", Level.Ok, string.Format(
                Loc.T("按 M{0:F2} 算，加力能点着的高度上限约 {1:F0} m（再高压力不够）", "At M{0:F2} the afterburner lights up to about {1:F0} m (pressure runs out above that)"), designMach, ceil)));
            return o;
        }
    }
}
