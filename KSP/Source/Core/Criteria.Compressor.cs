using System;
using System.Collections.Generic;
using System.Linq;

namespace JetForge.Core
{
    /// <summary>
    /// 压气机与流道判据：转子、换算转速、逐轴功、堵塞、级间负荷。
    ///
    /// 从 `Criteria.cs` 拆出来的：判据一共十七组，全挤在一个文件里时
    /// 温度在哪、轴在哪、堵塞在哪只能靠滚；拆开之后一族一个文件，
    /// 加一条判据只需要看那一族的前后文。
    /// </summary>
    public static partial class Criteria
    {
        /// <summary>一片转子的转速/叶尖速度/喘振裕度。kind: comp / fan / turb</summary>
        public static List<Finding> CheckRotor(string name, double mdot, double t, double p,
                                               double tipDiameter, double hubTipRatio, double u,
                                               double nPct, TechLevel tech, string kind = "comp")
        {
            List<Finding> o = new List<Finding>();
            if (u <= 0.0 || tipDiameter <= 0.0) return o;

            double mTip = u / Gas.SoundSpeed(t);
            double mLim = kind == "fan" ? tech.MaxTipMachFan
                        : kind == "turb" ? tech.MaxTipMachTurb : tech.MaxTipMachComp;
            // 判据用叶尖**线速度**而非当地马赫：设计转速是按海平面声速定的，
            // 11 km 声速掉到 295 m/s，同一个 U 的当地马赫自然涨到 1.4——那是真实的，
            // 不是设计缺陷。用当地马赫比限值只会在高空刷出一片假报警。
            double uLim = mLim * Gas.SoundSpeed(OffDesign.T0Sl);
            string lvl = u > uLim * 1.10 ? Level.Fail : (u > uLim ? Level.Warn : Level.Ok);
            o.Add(new Finding(string.Format("{0}.tip_speed", name), lvl,
                string.Format(Loc.T("{0} 叶尖线速度 {1:F0} m/s（设计上限 {2:F0}，当地叶尖马赫 {3:F2}）", "{0} tip speed {1:F0} m/s (limit {2:F0}, local tip Mach {3:F2})"),
                    name, u, uLim, mTip), u, uLim, "m/s", name));

            if (nPct > 0.0)
            {
                string l2 = nPct > tech.OverspeedLimit * 100.0 * 1.02 ? Level.Fail
                          : (nPct > tech.OverspeedLimit * 100.0 ? Level.Warn : Level.Ok);
                o.Add(new Finding(string.Format("{0}.overspeed", name), l2,
                    string.Format(Loc.T("{0} 转速 {1:F0}%（超转线 {2:F0}%）", "{0} speed {1:F0}% (overspeed line {2:F0}%)"), name, nPct, tech.OverspeedLimit * 100.0),
                    nPct, tech.OverspeedLimit * 100.0, "%", name));
            }

            if (kind == "turb") return o;

            double phi = FlowCoefficient(mdot, t, p, tipDiameter, hubTipRatio, u);            double phiStall = kind == "fan" ? tech.StallPhiFan : tech.StallPhi;
            double sm = phi / phiStall - 1.0;
            double need = tech.SurgeMarginReq;
            string lvl3, note;
            if (sm < 0.0) { lvl3 = Level.Fail; note = Loc.T("已进失速区", "into the stall region"); }
            else if (sm < need) { lvl3 = Level.Warn; note = Loc.T("裕度不足", "margin too thin"); }
            else { lvl3 = Level.Ok; note = ""; }
            o.Add(new Finding(string.Format("{0}.surge", name), lvl3,
                string.Format(Loc.T("{0} 喘振裕度 {1:+0;-0}%（φ={2:F2}，失速 φ≈{3:F2}，要求 ≥{4:F0}%）{5}", "{0} surge margin {1:+0;-0}% (phi={2:F2}, stall phi about {3:F2}, need at least {4:F0}%){5}"),
                    name, sm * 100.0, phi, phiStall, need * 100.0, note),
                sm * 100.0, need * 100.0, "%", name));

            // 反向也查：φ 过大说明流道相对流量太富，轴向速度高、相对马赫高、攻角偏负
            double phiHi = kind == "fan" ? tech.PhiHighFan : tech.PhiHighComp;
            if (phi > phiHi)
            {
                double mAx = AxialVelocity(mdot, t, p, tipDiameter, hubTipRatio) / Gas.SoundSpeed(t);
                o.Add(new Finding(string.Format("{0}.phi_high", name), Level.Warn,
                    string.Format(Loc.T("{0} 流量系数 φ={1:F2} 偏大（常见 {2:F2}~{3:F2}），轴向马赫 {4:F2}：流道对这个流量偏窄", "{0} flow coefficient phi={1:F2} is high (typical {2:F2} to {3:F2}), axial Mach {4:F2}: the annulus is narrow for this flow"),
                        name, phi, phiStall, phiHi, mAx),
                    phi, phiHi, "-", name));
            }
            return o;
        }

        /// <summary>
        /// 压气机形式的边界。离心级每级压比高，但通流面积小、级数上不去，
        /// 而且真机的离心叶轮叶尖速度有上限（轮盘强度）。这里只报事实，不改算法：
        /// 数字是求解器按 ψU² 真算出来的，判据的作用是提醒"这组参数能不能造出来"。
        /// </summary>
        public static List<Finding> CheckCompressorKind(EngineDesignData d)
        {
            List<Finding> o = new List<Finding>();
            if (d == null) return o;
            CheckOne(o, d.Lpc, "LPC", d);
            CheckOne(o, d.Hpc, "HPC", d);
            return o;
        }

        /// <summary>
        /// 换算转速 N/√θ 判据。压气机特性线横轴是换算转速，物理 100% 在高原就是超标。
        /// **θ 用哪个温度很要紧**：风扇用进口总温 T1，核心用风扇加热后的 T2。
        /// 拿静温算会把 11 km 的换算转速虚报成 115%，凭空刷出 FAIL。
        /// </summary>
        public static List<Finding> CheckCorrectedSpeed(ResultView v, TechLevel tech)
        {
            List<Finding> o = new List<Finding>();
            double tIn = v.Get("T1"), tCore = v.Get("T2");
            if (tIn <= 0.0) return o;
            if (tCore <= 0.0) tCore = tIn;
            double thFan = tIn / OffDesign.T0Sl;
            double thCore = tCore / OffDesign.T0Sl;
            if (thFan <= 0.05) return o;

            double lim = tech.OverspeedLimit * 100.0;
            List<KeyValuePair<string, double>> pairs = new List<KeyValuePair<string, double>>();
            if (v.Npct.Count > 0)
            {
                // 轴号不是 N 号：双轴机风扇在 1 轴 → N1，另一根叫 N2（不是 N3）
                List<int> ordered = new List<int>();
                if (v.Npct.ContainsKey(v.FanShaft)) ordered.Add(v.FanShaft);
                ordered.AddRange(v.Npct.Keys.Where(k => k != v.FanShaft).OrderBy(k => k));
                for (int i = 0; i < ordered.Count; i++)
                    pairs.Add(new KeyValuePair<string, double>("N" + (i + 1), v.Npct[ordered[i]]));
            }
            else
            {
                pairs.Add(new KeyValuePair<string, double>("N1", v.Get("n1_pct")));
                pairs.Add(new KeyValuePair<string, double>("N2", v.Get("n2_pct")));
            }

            for (int i = 0; i < pairs.Count; i++)
            {
                double n = pairs[i].Value;
                if (n <= 0.0) continue;
                double theta = i == 0 ? thFan : thCore;
                double ncor = n / Math.Sqrt(theta);
                string lvl = ncor > lim * 1.05 ? Level.Fail : (ncor > lim ? Level.Warn : Level.Ok);
                string msg = string.Format(Loc.T("{0} {1:F0}% 物理转速 → 换算 {2:F0}%（θ={3:F3}，上限 {4:F0}%）", "{0} {1:F0}% physical speed becomes {2:F0}% corrected (theta={3:F3}, limit {4:F0}%)"),
                    pairs[i].Key, n, ncor, theta, lim);
                if (lvl != Level.Ok) msg += Loc.T("：超出压气机特性线，流量与压比都可能是高估", ": off the compressor map, both flow and pressure ratio may be overestimated");
                o.Add(new Finding(string.Format("{0}.ncor", pairs[i].Key), lvl, msg, ncor, lim, "%", pairs[i].Key));
            }
            return o;
        }

        /// <summary>
        /// 多轴功平衡：每根轴上的涡轮要能挤出该轴上压气机吃的功。
        /// 三轴机的 IPT 是独立的一条流路，最容易在这里露馅——
        /// IPT 级数不够时，中压压气机在中低转速下就带不动了。
        /// </summary>
        public static List<Finding> CheckSpoolBalance(EngineDesignData d, ResultView v)
        {
            List<Finding> o = new List<Finding>();
            if (d == null) return o;
            if (!v.Num.ContainsKey("w_hpt")) return o;

            string[] names = { "HPT", "IPT", "LPT" };
            string[] wKeys = { "w_hpt", "w_ipt", "w_lpt" };
            string[] cKeys = { "cap_hpt", "cap_ipt", "cap_lpt" };
            int[] shafts = { d.Hpt.Shaft, d.Ipt.Shaft, d.Lpt.Shaft };
            int[] stages = { d.Hpt.Stages, d.Ipt.Stages, d.Lpt.Stages };

            for (int i = 0; i < 3; i++)
            {
                if (stages[i] <= 0) continue;
                double w = v.Get(wKeys[i]);
                double cap = v.Get(cKeys[i]);
                if (cap <= 1.0 || w <= 1.0) continue;
                double load = w / cap;
                Finding f = new Finding("shaft." + names[i].ToLowerInvariant(),
                    load > 1.01 ? Level.Warn : Level.Ok,
                    string.Format(Loc.T("{0}（轴 {1}）需 {2:F2} MW / 可提供 {3:F2} MW（负荷 {4:P0}）", "{0} (shaft {1}) needs {2:F2} MW, supply is {3:F2} MW (load {4:P0})"),
                        names[i], shafts[i], w / 1e6, cap / 1e6, load),
                    load, 1.0, "", names[i]);
                o.Add(f);
            }

            int lim = (int)Math.Round(v.Get("spool_limit"));
            if (lim > 0)
            {
                o.Add(new Finding("shaft.limited", Level.Warn,
                    string.Format(Loc.T("轴 {0} 带不动自己那级的压气机：真实发动机会掉转，", "Shaft {0} cannot drive its own compressor: a real engine would spool down,")
                                  + Loc.T("本模型按共用转速比例算，这一点的读数偏乐观", "this model holds one speed ratio for everything, so this reading is optimistic"), lim),
                    lim, 0.0, "", "shaft"));
            }
            return o;
        }

        /// <summary>
        /// 堵塞侧：工作点的换算流量离这条等转速线的堵塞流量还有多远。
        ///
        /// 与喘振判据配对。喘振是"再收油门就转不动"，堵塞是"再加油门只有流量、没有压比"——
        /// 两条都是墙，方向相反。以前只查了喘振一侧，于是越过堵塞线的工作点被当成正常点，
        /// 算出来的压比不是这台机器能给的。
        ///
        /// -1 是"读数不可用"的记号（没有这张图），必须先排掉，否则会把没有读数说成越界。
        /// </summary>
        public static List<Finding> CheckChoke(ResultView v)
        {
            List<Finding> o = new List<Finding>();
            bool any = false;
            any |= ChokeOne(o, v, "hpc", Loc.T("高压压气机", "high-pressure compressor"));
            any |= ChokeOne(o, v, "lpc", Loc.T("低压压气机", "low-pressure compressor"));
            if (!any)
                o.Add(new Finding("comp.choke", Level.Ok, Loc.T("堵塞侧：这台机器没有可判的压气机图，这条不评价", "Choke side: this engine has no usable compressor map, so this is not judged")));
            return o;
        }

        private static bool ChokeOne(List<Finding> o, ResultView v, string pre, string name)
        {
            if (!v.Num.ContainsKey(pre + "_choke")) return false;
            double cm = v.Get(pre + "_choke");
            if (Math.Abs(cm + 1.0) < 1e-9) return false;
            bool over = v.Get(pre + "_choked") > 0.5;
            if (over)
                o.Add(new Finding("comp.choke." + pre, Level.Warn,
                    string.Format(Loc.T("{0}已经越过堵塞线（换算流量比这条转速线能过的多 {1:F0}%）：再加油门只有流量没有压比", "{0} is past the choke line (corrected flow {1:F0}% above what this speed line can pass): more throttle buys flow, not pressure ratio"),
                        name, -cm), cm, 0.0, "%", name));
            else if (cm < 3.0)
                o.Add(new Finding("comp.choke." + pre, Level.Warn,
                    string.Format(Loc.T("{0}贴着堵塞线（只剩 {1:F1}% 横向余量）：油门再往上推只会把流量推出去", "{0} is hugging the choke line ({1:F1}% lateral margin left): pushing the throttle further only spills flow"),
                        name, cm), cm, 3.0, "%", name));
            else
                o.Add(new Finding("comp.choke." + pre, Level.Ok,
                    string.Format(Loc.T("{0}离堵塞线还有 {1:F0}% 横向余量", "{0} has {1:F0}% lateral margin to the choke line"), name, cm), cm, 3.0, "%", name));
            return true;
        }

        /// <summary>
        /// 逐级负荷分配。
        ///
        /// 整机喘振裕度是一个数，而失速总是某一级先转不动。级间温升分得越不均，
        /// 最重那一级离失速越近，整机裕度就越不能代表它。这里不用逐级特性图
        /// （那要有每一级的图，本模型没有），只用已有的逐级温升与流量说三件事：
        /// 谁吃得最重、重到平均级的几倍、级间放气落在第几级。
        ///
        /// 阈值取真机的配平惯例：最重一级在平均级 1.6 倍以内算正常，
        /// 超过 2.2 倍说"没配平"——这个数不是为了好看，是为了别把
        /// 均匀设计的正常机器报成警告（审计那次的 65 条假警报就是这么来的）。
        /// </summary>
        public static List<Finding> CheckStageLoad(ResultView v)
        {
            List<Finding> o = new List<Finding>();
            StageLoadReport rep = StageLoadReport.Of(v.Stages);
            if (!rep.Ok) return o;
            string lvl = rep.WorstVsMean > 2.2 ? Level.Warn : Level.Ok;
            o.Add(new Finding("comp.stageload", lvl, rep.Describe(),
                rep.WorstVsMean, 2.2, "×", rep.WorstStage));
            return o;
        }

        /// <summary>
        /// 逐级喘振余量：整机余量是平均数，真机先喘的是前级。
        /// 口径只按流量系数，与整机口径不同，所以只在"最差那一级"低于门槛时说话，
        /// 不拿它去替代整机裕度。
        /// </summary>
        public static List<Finding> CheckStageMargin(ResultView v)
        {
            List<Finding> o = new List<Finding>();
            bool any = false;
            any |= StageOne(o, v, "hpc", Loc.T("高压压气机", "high-pressure compressor"));
            any |= StageOne(o, v, "lpc", Loc.T("低压压气机", "low-pressure compressor"));
            if (!any) return o;
            return o;
        }

        private static bool StageOne(List<Finding> o, ResultView v, string pre, string name)
        {
            if (!v.Num.ContainsKey(pre + "_stage_sm")) return false;
            double sm = v.Get(pre + "_stage_sm");
            int idx = (int)Math.Round(v.Get(pre + "_stage_idx"));
            if (Math.Abs(sm + 1.0) < 1e-9 || idx <= 0) return false;
            double whole = v.Num.ContainsKey(pre + "_surge") ? v.Get(pre + "_surge") : -1.0;
            if (sm < 0.0)
                o.Add(new Finding("comp.stage." + pre, Level.Warn,
                    string.Format(Loc.T("{0}第 {1} 级已经越过单级喘振边界（{2:F1}%）：整机还报 {3:F0}%，但前级先喘，这时候该动可调静子或级间放气", "{0} stage {1} is past its own surge boundary ({2:F1}%): the whole compressor still reads {3:F0}%, but front stages stall first, which is what variable stators and interstage bleed are for"),
                        name, idx, sm, whole), sm, 0.0, "%", name));
            else if (sm < 3.0)
                o.Add(new Finding("comp.stage." + pre, Level.Warn,
                    string.Format(Loc.T("{0}第 {1} 级只剩 {2:F1}% 单级喘振余量（整机 {3:F0}%）：级间负荷不均，前级在前、后级偏堵塞", "{0} stage {1} has only {2:F1}% margin of its own (whole compressor {3:F0}%): the load is uneven, front stages lead and rear stages run near choke"),
                        name, idx, sm, whole), sm, 3.0, "%", name));
            else
                o.Add(new Finding("comp.stage." + pre, Level.Ok,
                    string.Format(Loc.T("{0}最差的一级是第 {1} 级，单级余量 {2:F0}%", "{0} worst stage is stage {1} at {2:F0}% margin"),
                        name, idx, sm), sm, 3.0, "%", name));
            return true;
        }
    }
}
