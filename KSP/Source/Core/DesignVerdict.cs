using System;
using System.Collections.Generic;
using System.Text;

namespace JetForge.Core
{
    /// <summary>
    /// 设计评价（Phase3 §6.3）：把一堆判据换成"这台机器现在能不能用、差在哪"。
    ///
    /// 和判据的分工：判据逐条查（28 条），回答"哪个数越界了"；评价回答"那我该怎么办"。
    /// 所以它不说"喘振裕度 12%"，而说"喘振裕度只剩 12%，先把高压压气机加一级或者降点压比"。
    ///
    /// 不做评分。一个 0~100 的分既不能告诉你哪里不行，又会被拿去比大小——
    /// 而这里每一个数背后都是不同的物理量，本来就不能相加。
    /// </summary>
    public sealed class DesignVerdict
    {
        /// <summary>一句话结论：能不能飞。</summary>
        public string Headline = "";

        /// <summary>分组说明，每行一件玩家真正能动手改的事。</summary>
        public List<string> Lines = new List<string>();

        /// <summary>最差等级（Ok / Warn / Fail），与判据同一套词。</summary>
        public string Worst = Level.Ok;

        /// <summary>按"性能 → 热力 → 气路 → 尺寸 → 判据"的顺序拼出来。
        /// `paperThrust` 是设计闭式算出来的纸面推力（设计页那一栏），给了就顺手把
        /// "纸上"和"满油门实飞"的差说清楚——两个数差 8% 而不解释，比不显示更糟。</summary>
        public static DesignVerdict Of(EngineDesignData d, OffDesignResult p, CheckReport rep, double paperThrust = double.NaN)
        {
            DesignVerdict v = new DesignVerdict();
            if (d == null)
            {
                v.Headline = Loc.T("还没有设计", "No design yet");
                v.Worst = Level.Warn;
                return v;
            }
            if (p == null || !p.Ok)
            {
                v.Headline = Loc.T("这台机器算不出来，先看左边那几条错误", "This engine does not solve; start with the errors on the left");
                v.Worst = Level.Fail;
                if (p != null)
                    foreach (string e in p.Errors) v.Lines.Add(e);
                return v;
            }
            if (rep != null) v.Worst = rep.Worst;

            // 算得"通过"但推力和进气都是 0，等于什么都没设计。这种情况以前会
            // 落进"能飞，判据全过"那一支——最容易被信的一句错话。手册第 0 页就写着：
            // 空对象算出来的 0 不是性能，是没填。
            if (!(p.NetThrust > 0.0) || !(p.MdotAir > 0.0))
            {
                v.Headline = Loc.T("这台机器还没有成形：推力或进气是 0，先填风扇直径与设计点", "This engine is not formed yet: thrust or inlet flow is zero, so set the fan diameter and the design point first");
                v.Worst = Level.Fail;
                v.Lines.Add(string.Format(Loc.T("实算推力 {0:F1} kN｜进气 {1:F2} kg/s：这两个是 0 就说明没有做功能量", "Solved thrust {0:F1} kN | inlet flow {1:F2} kg/s: zero in either means there is no working energy"),
                    p.NetThrust / 1000.0, p.MdotAir));
                return v;
            }

            int fails = rep != null ? rep.Fails.Count : 0;
            int warns = rep != null ? rep.Warns.Count : 0;
            if (fails > 0)
                v.Headline = string.Format(Loc.T("现在飞不起来：{0} 条不合格、{1} 条要留意", "It will not fly: {0} failures, {1} warnings"), fails, warns);
            else if (warns > 0)
                v.Headline = string.Format(Loc.T("能飞，但有 {0} 处要留意", "It flies, with {0} things to watch"), warns);
            else
                v.Headline = Loc.T("能飞，判据全过", "It flies; every criterion passes");

            // ---- 性能 ----
            v.Lines.Add(string.Format(Loc.T("性能：设计点 {0:F1} kN｜耗油率 {1:F4}｜总压比 {2:F2}", "Performance: design point {0:F1} kN | SFC {1:F4} | overall pressure ratio {2:F2}"),
                p.NetThrust / 1000.0, p.Sfc, p.Opr));
            // 纸面推力来自设计闭式，满油门那个数来自部件地图迭代。两条路差得明显时
            // 必须说出来，否则玩家按纸面数配飞机，飞起来差 8% 会以为哪里坏了。
            if (paperThrust > 0.0 && GasState.IsFinite(paperThrust))
            {
                double gap = (p.NetThrust - paperThrust) / paperThrust;
                if (Math.Abs(gap) > 0.03)
                    v.Lines.Add(string.Format(
                        Loc.T("纸面 {0:F1} kN，满油门实飞 {1:F1} kN（{2:+0.0;-0.0}%）：差在满油门这条路上喉道或风扇先到限，不是算错", "Paper {0:F1} kN, full-throttle in flight {1:F1} kN ({2:+0.0;-0.0}%): on the full-throttle path the throat or the fan runs out first; this is not a miscalculation"),
                        paperThrust / 1000.0, p.NetThrust / 1000.0, 100.0 * gap));
            }
            if (!d.HasFan && p.NetThrust < 20e3)
                v.Lines.Add(Loc.T("推力偏小：涡喷靠加大直径最有效，加级数只加长不加直径", "Thrust too low: on a turbojet a larger diameter helps most; more stages only add length, not diameter"));

            // ---- 热力 ----
            // 涡前温度的上限在 TechLevel.T4Limit（材料的账记在年代上），不在燃烧室规格里
            double t4Lim = d.Tech != null ? d.Tech.T4Limit : 0.0;
            string t4Note = t4Lim > 0.0 && p.T4 > t4Lim * 0.98 ? Loc.T("（贴上限）", " (on the limit)") : "";
            v.Lines.Add(string.Format(Loc.T("热力：涡前 {0:F0} K{1}｜涡轮后 {2:F0} K｜转速 {3:F0}%", "Thermal: turbine inlet {0:F0} K{1} | turbine exit {2:F0} K | speed {3:F0}%"),
                p.T4, t4Note, p.T5, p.N1Pct));
            if (t4Note.Length > 0)
                v.Lines.Add(string.Format(Loc.T("涡前温度贴着材料上限（{0:F0} K）：想再多就换代（材料）或减级数，别硬推", "Turbine inlet sits on the material limit ({0:F0} K): to go higher, change era (materials) or drop stages; do not force it"), t4Lim));

            // ---- 气路 ----
            // 喘振裕度只有特性图算得出来；设计点这条路径上没有图，就跳过这一行，
            // 不写"裕度未知"这种没用的字——判据里本来就有喘振那一条。
            if (p.IntakeLimited)
                v.Lines.Add(Loc.T("进气道供不上这么多气：进气量被喉道压下来了，加大捕获直径或提高喉道比", "The intake cannot feed that much air: the throat is capping the flow, so enlarge the capture diameter or raise the throat ratio"));
            if (p.NozzleCapped)
                v.Lines.Add(Loc.T("喷管喉道过不去这股气：加大 A8 或减少加力油气比", "The nozzle throat cannot pass this flow: open A8 or lean the afterburner"));
            if (p.A8OpenRatio > 1.15)
                v.Lines.Add(string.Format(Loc.T("喷口开得比需要的大 {0:P0}：落压比白掉，压力推力丢了", "The nozzle is {0:P0} open beyond what is needed: pressure ratio is wasted and pressure thrust is lost"), p.A8OpenRatio - 1.0));

            // ---- 尺寸与重量 ----
            double mass = 0.0;
            try { MassBreakdown m = MassCost.Estimate(d); mass = m.TotalDry; } catch { mass = 0.0; }
            double len = d.EffectiveLength;
            double dia = d.HasFan && d.Fan != null ? d.Fan.Diameter : d.InletDiameter;
            if (mass > 0.0 && dia > 0.0)
                v.Lines.Add(string.Format(Loc.T("尺寸：长 {0:F2} m｜最大 Ø{1:F2} m｜{2:F0} kg", "Size: {0:F2} m long | max dia {1:F2} m | {2:F0} kg"), len, dia, mass));
            if (len > 0.0 && dia > 0.0 && len / dia > 8.0)
                v.Lines.Add(string.Format(Loc.T("长径比 {0:F1}：细长得离谱，减级数或加大直径", "Length/diameter {0:F1}: absurdly slender; drop stages or enlarge the diameter"), len / dia));

            // ---- 判据 ----
            if (rep != null && rep.Findings.Count > 0)
            {
                List<Finding> bad = new List<Finding>();
                foreach (Finding f in rep.Findings)
                    if (f.LevelValue != Level.Ok) bad.Add(f);
                if (bad.Count == 0)
                    v.Lines.Add(Loc.T("判据：全部通过", "Criteria: all pass"));
                else
                {
                    // 只报最严重的三条：一屏说三件事比说十条有用
                    int n = 0;
                    foreach (Finding f in bad)
                    {
                        v.Lines.Add(Loc.T("要留意：", "Watch:") + f.Message);
                        if (++n >= 3) break;
                    }
                    if (bad.Count > 3)
                        v.Lines.Add(string.Format(Loc.T("……还有 {0} 条，见判据页", "... {0} more on the criteria page"), bad.Count - 3));
                }
            }
            return v;
        }

        /// <summary>给日志/自检用的一行版。</summary>
        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Headline);
            if (Lines.Count > 0) sb.Append("｜").Append(Lines[0]);
            return sb.ToString();
        }
    }
}