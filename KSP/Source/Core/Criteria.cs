using System;
using System.Collections.Generic;
using System.Linq;

namespace JetForge.Core
{
    public static class Level
    {
        public const string Ok = "OK";
        public const string Warn = "WARN";
        public const string Fail = "FAIL";
        public static int Rank(string s)
        {
            if (s == Fail) return 2;
            if (s == Warn) return 1;
            return 0;
        }
    }

    public class Finding
    {
        public string Key;
        public string LevelValue = JetForge.Core.Level.Ok;
        public string Message = "";
        public double Value;
        public double Limit;
        public string Unit = "";
        public string Part = "";

        public Finding() { }

        public Finding(string key, string lvl, string msg,
                       double value = 0.0, double limit = 0.0, string unit = "", string part = "")
        {
            Key = key; LevelValue = lvl; Message = msg;
            Value = value; Limit = limit; Unit = unit; Part = part;
        }

        /// <summary>剩余裕度（相对限值，正数=还有余地）。</summary>
        public double MarginFrac
        {
            get { return Math.Abs(Limit) > 1e-12 ? (Limit - Value) / Limit : 0.0; }
        }

        public string Line()
        {
            return string.Format("[{0}] {1}", LevelValue, Message);
        }
    }

    public class CheckReport
    {
        public List<Finding> Findings = new List<Finding>();

        public void Add(Finding f) { Findings.Add(f); }
        public void AddRange(IEnumerable<Finding> fs) { Findings.AddRange(fs); }

        public string Worst
        {
            get
            {
                string w = JetForge.Core.Level.Ok;
                foreach (Finding f in Findings)
                    if (JetForge.Core.Level.Rank(f.LevelValue) > JetForge.Core.Level.Rank(w))
                        w = f.LevelValue;
                return w;
            }
        }

        public bool Ok { get { return Worst != JetForge.Core.Level.Fail; } }

        public List<Finding> Fails
        {
            get { return Findings.Where(f => f.LevelValue == JetForge.Core.Level.Fail).ToList(); }
        }

        public List<Finding> Warns
        {
            get { return Findings.Where(f => f.LevelValue == JetForge.Core.Level.Warn).ToList(); }
        }

        public List<string> Lines(bool showOk = true)
        {
            List<string> o = new List<string>();
            foreach (Finding f in Findings)
                if (showOk || f.LevelValue != JetForge.Core.Level.Ok) o.Add(f.Line());
            return o;
        }

        /// <summary>
        /// 按严重度排好的副本：Fail 在前、Warn 次之、Ok 最后，同级保持原本的先后。
        ///
        /// 为什么要排：判据是按"温度→燃烧→喷管→轴→堵塞→级间…"的物理顺序逐条跑出来的，
        /// 那个顺序对写代码的人有意义（从上游往下游查），对玩家没有——玩家只想知道
        /// "最该修的是哪一条"。不排的话，一条 FAIL 会被八条 WARN 埋在第六屏。
        ///
        /// 用稳定排序（LINQ 的 OrderByDescending 就是稳定的），同级之间仍是物理顺序，
        /// 所以同一个设计两次打开，看到的排列一样。
        /// </summary>
        public List<Finding> Ordered(bool problemsOnly = false)
        {
            IEnumerable<Finding> src = Findings;
            if (problemsOnly) src = src.Where(f => f.LevelValue != JetForge.Core.Level.Ok);
            return src.OrderByDescending(f => JetForge.Core.Level.Rank(f.LevelValue)).ToList();
        }

        /// <summary>通过的有几条。判据栏会把它们藏起来，但得说清藏了几条——
        /// 藏起来不说话，看起来就像"只跑了两条判据"。</summary>
        public int OkCount
        {
            get
            {
                int n = 0;
                foreach (Finding f in Findings) if (f.LevelValue == JetForge.Core.Level.Ok) n++;
                return n;
            }
        }
    }

    /// <summary>求解结果的扁平视图：判据模块只认这个，不关心结果是什么类型。</summary>
    public class ResultView
    {
        public Dictionary<string, double> Num = new Dictionary<string, double>();
        public Dictionary<int, double> Npct = new Dictionary<int, double>();
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<StageRow> Stages = new List<StageRow>();
        public bool Ok = true;
        public bool HasTurbineSplit;
        public int FanShaft;

        public double Get(string k) { double v; return Num.TryGetValue(k, out v) ? v : 0.0; }
        public void Set(string k, double v) { Num[k] = v; }
    }

    /// <summary>
    /// 设计可行性判据。这里不重复求解器的功平衡，只回答"这个设计能不能造、能不能飞"：
    /// 叶尖速度、超转、喘振裕度、材料温度、熄火边界、喷管压比、轴平衡。
    ///
    /// 诚实声明：喘振裕度用的是流量系数 φ 与失速 φ 的比值做代理指标，
    /// 不是真正的压气机特性线。它能排相对好坏、能抓"流道开得太窄/太宽"，
    /// 但不要拿它当"裕度 18%"这种绝对数字去写论文。
    /// </summary>
    public static partial class Criteria
    {
        public static double AxialVelocity(double mdot, double t, double p,
                                           double tipDiameter, double hubTipRatio)
        {
            double area = Math.PI / 4.0 * tipDiameter * tipDiameter
                          * Math.Max(1.0 - hubTipRatio * hubTipRatio, 1e-6);
            if (area <= 0.0 || mdot <= 0.0) return 0.0;
            double rho = p / (Gas.R * Math.Max(t, 1.0));
            return mdot / Math.Max(rho * area, 1e-9);
        }

        public static double FlowCoefficient(double mdot, double t, double p,
                                             double tipDiameter, double hubTipRatio, double u)
        {
            if (u <= 0.0) return 0.0;
            return AxialVelocity(mdot, t, p, tipDiameter, hubTipRatio) / u;
        }

        /// <summary>
        /// 把压气机功分到 LPC/HPC。有分量用分量——按级数平均分是错的：
        /// 逐级温升沿流路递增，LPC 实际功占比远低于级数占比
        /// （Demo 2-spool：LPC 2/10 级却只占 36/291 K ≈ 12%）。
        /// </summary>
        public static double Share(EngineDesignData design, ResultView v, string which)
        {
            double wl = v.Get("W_lpc"), wh = v.Get("W_hpc");
            if (wl > 0.0 || wh > 0.0) return which == "lpc" ? wl : wh;
            int nl = Math.Max(design.Lpc.Stages, 0), nh = Math.Max(design.Hpc.Stages, 0);
            int tot = nl + nh;
            if (tot == 0) return 0.0;
            return v.Get("W_comp") * ((which == "lpc" ? nl : nh) / (double)tot);
        }

        public static ResultView ViewOf(CycleResult r)
        {
            ResultView v = new ResultView
            {
                Ok = r.Ok, HasTurbineSplit = true, Stages = r.Stages
            };
            v.Errors.AddRange(r.Errors);
            v.Warnings.AddRange(r.Warnings);
            v.Set("mdot_air", r.MdotAir); v.Set("mdot_core", r.MdotCore);
            v.Set("bpr", r.Bpr); v.Set("opr", r.Opr); v.Set("fpr", r.Fpr);
            v.Set("T1", r.T1); v.Set("T2", r.T2); v.Set("T3", r.T3);
            v.Set("T4", r.T4); v.Set("T5", r.T5); v.Set("T7", r.T7);
            v.Set("P3", r.P3); v.Set("P5", r.P5);
            v.Set("far", r.Far); v.Set("far_ab", r.FarAb);
            v.Set("thrust", r.Thrust); v.Set("thrust_gross", r.Thrust);
            v.Set("sfc", r.Sfc); v.Set("fuel_flow", r.FuelFlow);
            v.Set("W_comp", r.WComp); v.Set("W_lpc", r.WLpc); v.Set("W_hpc", r.WHpc);
            v.Set("W_fan", r.WFan);
            v.Set("W_turb_hpt", r.WTurbHpt); v.Set("W_turb_ipt", r.WTurbIpt); v.Set("W_turb_lpt", r.WTurbLpt);
            foreach (KeyValuePair<int, double> kv in r.Rpm) v.Npct[kv.Key] = 100.0;
            double n1 = 100.0, n2 = 100.0;
            if (r.Spool != null)
            {
                double x;
                if (r.Spool.TryGetValue("N1", out x)) n1 = x;
                if (r.Spool.TryGetValue("N2", out x)) n2 = x;
            }
            v.Set("n1_pct", n1); v.Set("n2_pct", n2);
            v.Set("self_sustaining", r.SelfSustaining ? 1.0 : 0.0);
            v.Set("hpc_surge", r.HpcSurgeMargin); v.Set("lpc_surge", r.LpcSurgeMargin);
            // 只留下面这两个键：hpc_surge / lpc_surge 由 CheckStageMargin 用 "pre + _surge" 组合读取，
// 而 *_surge_risk 从来没被任何判据读过——风险已经写进 CycleResult.Warnings 与模块状态行，
// 再往视图里塞一份就是没人读的账（删掉时确认过 grep 不到消费者）。
            v.Set("hpc_choke", r.HpcChokeMargin); v.Set("lpc_choke", r.LpcChokeMargin);
            v.Set("hpc_choked", r.HpcChoked ? 1.0 : 0.0);
            v.Set("lpc_choked", r.LpcChoked ? 1.0 : 0.0);
            v.Set("hpc_stage_sm", r.HpcStageSurgeMargin);
            v.Set("hpc_stage_idx", r.HpcStageSurgeStage);
            v.Set("lpc_stage_sm", r.LpcStageSurgeMargin);
            v.Set("lpc_stage_idx", r.LpcStageSurgeStage);
            // 齿轮箱柔度：判据与读数都从视图取，所以统一在这里搬一次。
            v.Set("gearbox_present", r.GearboxTorsionPresent ? 1.0 : 0.0);
            v.Set("gearbox_torque", r.GearboxTorque);
            v.Set("gearbox_twist", r.GearboxTwistDeg);
            v.Set("gearbox_freq", r.GearboxFreqHz);
            v.Set("fan_rpm", r.FanRpm);
            return v;
        }

        public static ResultView ViewOf(OffDesignResult r, int fanShaft)
        {
            ResultView v = new ResultView
            {
                Ok = r.Ok, HasTurbineSplit = false, FanShaft = fanShaft
            };
            v.Errors.AddRange(r.Errors);
            v.Warnings.AddRange(r.Warnings);
            v.Set("mdot_air", r.MdotAir); v.Set("mdot_core", r.MdotCore);
            v.Set("bpr", r.Bpr); v.Set("opr", r.Opr); v.Set("fpr", r.Fpr);
            v.Set("T1", r.T1); v.Set("T2", r.T2); v.Set("T3", r.T3);
            v.Set("T4", r.T4); v.Set("T5", r.T5); v.Set("T7", r.T7);
            v.Set("P3", r.P3); v.Set("P5", r.P5);
            v.Set("far", r.Far); v.Set("far_ab", r.FarAb);
            // 净推力优先：总推力扣完冲压阻力才是飞起来那份
            v.Set("thrust_gross", r.Thrust);
            v.Set("thrust", r.NetThrust != 0.0 ? r.NetThrust : r.Thrust);
            v.Set("sfc", r.Sfc); v.Set("fuel_flow", r.FuelFlow);
            v.Set("W_comp", r.WComp); v.Set("W_fan", r.WFan);
            v.Set("n1_pct", r.N1Pct); v.Set("n2_pct", r.N2Pct);
            v.Set("alt", r.Alt); v.Set("mach", r.Mach);
            v.Set("throttle", r.Throttle);
            v.Set("self_sustaining", r.SelfSustaining ? 1.0 : 0.0);
            v.Set("windmill", r.Windmill ? 1.0 : 0.0);
            v.Set("nozzle_backpressure", r.NozzleBackPressure ? 1.0 : 0.0);
            v.Set("nozzle_capped", r.NozzleCapped ? 1.0 : 0.0);
            v.Set("nozzle_separated", r.NozzleSeparated ? 1.0 : 0.0);
            v.Set("nozzle_arearatio", r.NozzleAreaRatio);
            v.Set("nozzle_mache", r.NozzleMachE);
            v.Set("w_hpt", r.WHpt); v.Set("w_ipt", r.WIpt); v.Set("w_lpt", r.WLpt);
            v.Set("cap_hpt", r.CapHpt); v.Set("cap_ipt", r.CapIpt); v.Set("cap_lpt", r.CapLpt);
            v.Set("spool_limit", r.LimitSpool);
            return v;
        }

        private static void CheckOne(List<Finding> o, CompressorSpec c, string name, EngineDesignData d)
        {
            if (c == null || c.Stages <= 0) return;
            string id = "comp." + name.ToLowerInvariant();
            int limit = CompressorPhysics.PracticalStageLimit(c.Kind);
            if (c.Stages > limit)
            {
                o.Add(new Finding(id + ".stages", Level.Warn, string.Format(
                    Loc.T("{0} 设成 {1} 级{2}压气机：真机这种形式很少超过 {3} 级，", "{0} set to a {1}-stage {2} compressor: real engines of this form rarely pass {3} stages,") +
                    Loc.T("超过之后每级的通流面积小到装不下，读数只能当趋势看", "past that each stage gets too small to pass the flow, so read it as a trend only"),
                    name, c.Stages, c.Kind == CompressorKind.Centrifugal ? Loc.T("离心", "centrifugal") : Loc.T("轴流", "axial"), limit)));
            }
            else if (c.Kind == CompressorKind.Centrifugal)
            {
                o.Add(new Finding(id + ".kind", Level.Ok, string.Format(
                    Loc.T("{0} 为离心压气机（{1} 级，载荷系数按轴流的 {2:F2} 倍算）", "{0} is a centrifugal compressor ({1} stages, load coefficient counted as {2:F2} times axial)"),
                    name, c.Stages, CompressorPhysics.CentrifugalPsiFactor)));
            }

            // 单级压比：轮盘强度与相对马赫决定它有个天花板
            if (d != null)
            {
                CycleResult dp = Cycle.SolveDesignPoint(d);
                if (dp != null && dp.Stages != null)
                {
                    double worst = 0.0;
                    int worstIdx = 0;
                    foreach (StageRow row in dp.Stages)
                    {
                        if (row.Part != name) continue;
                        if (row.Pi > worst) { worst = row.Pi; worstIdx = row.Index; }
                    }
                    double cap = CompressorPhysics.MaxStagePi(c.Kind);
                    if (worst > cap)
                        o.Add(new Finding(id + ".stagepi", Level.Warn, string.Format(
                            Loc.T("{0} 第 {1} 级压比 {2:F2} 超过{3}压气机的合理上限 {4:F2}：叶轮转速太高了", "{0} stage {1} pressure ratio {2:F2} is past the sane limit {4:F2} for a {3} compressor: the impeller is spinning too fast"),
                            name, worstIdx, worst,
                            c.Kind == CompressorKind.Centrifugal ? Loc.T("离心", "centrifugal") : Loc.T("轴流", "axial"), cap)));
                }
            }
        }

        /// <summary>对一次求解做全套判据。</summary>
        public static CheckReport Evaluate(EngineDesignData design, ResultView v,
                                           TechLevel tech = null, double pAmb = 0.0, double alt = -1.0)
        {
            if (tech == null) tech = design.Tech;
            if (pAmb <= 0.0)
            {
                double a = alt >= 0.0 ? alt : (v.Num.ContainsKey("alt") ? v.Get("alt") : -1.0);
                if (a >= 0.0)
                {
                    double pp, tt;
                    Gas.Isa(a, out pp, out tt);
                    pAmb = pp;
                }
            }
            if (pAmb > 0.0) v.Set("p_amb", pAmb);
            v.FanShaft = design.HasFan ? design.Fan.Shaft : 0;

            CheckReport rep = new CheckReport();
            foreach (string e in v.Errors) rep.Add(new Finding("solver", Level.Fail, e));
            foreach (string w in v.Warnings) rep.Add(new Finding("solver.warn", Level.Warn, w));

            // 进气道假设的边界要在"求解成立与否"之前说：
            // 设计马赫 > 1.2 时流量口径本身就有问题，求解失败时更需要这句话。
            rep.AddRange(CheckInletAssumption(design));
            // 这三条只看设计本身，不看循环解出来没有。挂在 if (!v.Ok) return rep; 之后
            // 等于"设计解不出来就不告诉你为什么点不着"——上次 inlet 判据就是这么丢的。
            rep.AddRange(CheckBurnerShape(design));
            rep.AddRange(CheckCompressorKind(design));
            rep.AddRange(CheckIntake(design));
            rep.AddRange(CheckAfterburnerLight(design));
            rep.AddRange(CheckRelight(design));

            if (!v.Ok) return rep;

            rep.AddRange(CheckTemperatures(v, tech));
            rep.AddRange(CheckCorrectedSpeed(v, tech));
            rep.AddRange(CheckCombustion(v, tech));
            rep.AddRange(CheckNozzle(v, design, tech));
            rep.AddRange(CheckNozzleGeometry(design, v));
            rep.AddRange(CheckPetals(design));
            rep.AddRange(CheckControl(design));
            rep.AddRange(CheckSustain(v));
            rep.AddRange(CheckWindmill(v));
            rep.AddRange(CheckShafts(design, v));
            rep.AddRange(CheckSpoolBalance(design, v));
            rep.AddRange(CheckFlowPath(design, v, tech));
            rep.AddRange(CheckStageLoad(v));
            rep.AddRange(CheckChoke(v));
            rep.AddRange(CheckStageMargin(v));
            rep.AddRange(CheckTorsion(design, v));

            double thrust = v.Get("thrust");
            if (thrust <= 0.0)
                rep.Add(new Finding("thrust", Level.Fail, Loc.T("净推力 ≤ 0：这台发动机推不动自己", "Net thrust is at or below zero: this engine cannot push itself")));
            else
                rep.Add(new Finding("thrust", Level.Ok,
                    string.Format(Loc.T("净推力 {0:F1} kN，SFC {1:F4} kg/(N·h)", "Net thrust {0:F1} kN, SFC {1:F4} kg/(N·h)"), thrust / 1000.0, v.Get("sfc")),
                    thrust / 1000.0));
            return rep;
        }

        /// <summary>
        /// 简单模式的判据入口。简单模式**没有几何**（没级数、没叶尖直径），
        /// 所以叶轮/流道/轴平衡三类判据在这里没有意义，明确跳过并说明——
        /// 不假装全绿，也不凭空 FAIL。
        /// </summary>
        public static CheckReport EvaluateSimple(SimpleDesign sd, ResultView v, double alt = -1.0)
        {
            TechLevel tech = sd != null && sd.Tech != null ? sd.Tech : TechLevel.Get("1970s");
            double pAmb = 0.0;
            double a = alt >= 0.0 ? alt : (v.Num.ContainsKey("alt") ? v.Get("alt") : -1.0);
            if (a >= 0.0)
            {
                double pp, tt;
                Gas.Isa(a, out pp, out tt);
                pAmb = pp;
            }
            if (pAmb > 0.0) v.Set("p_amb", pAmb);

            CheckReport rep = new CheckReport();
            foreach (string e in v.Errors) rep.Add(new Finding("solver", Level.Fail, e));
            foreach (string w in v.Warnings) rep.Add(new Finding("solver.warn", Level.Warn, w));
            if (!v.Ok) return rep;

            rep.AddRange(CheckTemperatures(v, tech));
            rep.AddRange(CheckCorrectedSpeed(v, tech));
            rep.AddRange(CheckCombustion(v, tech));
            if (pAmb > 0.0)
            {
                double p5 = v.Get("P5");
                if (p5 > 0.0)
                {
                    double npr = p5 / pAmb;
                    if (npr < 1.0)
                        rep.Add(new Finding("nozzle.reverse", Level.Fail,
                            string.Format(Loc.T("核心喷管压比 {0:F2} < 1：出口倒流", "Core nozzle pressure ratio {0:F2} below 1: the exit flows backwards"), npr), npr, 1.0, "-"));
                    else
                    {
                        NozzleKind nk = sd != null ? sd.Nozzle : NozzleKind.Converging;
                        double lim = nk == NozzleKind.Converging ? tech.NozzleNprFixedMax : 1e9;
                        rep.Add(new Finding("nozzle.npr", npr > lim ? Level.Warn : Level.Ok,
                            string.Format(Loc.T("核心喷管压比 {0:F2}", "Core nozzle pressure ratio {0:F2}"), npr), npr, lim, "-"));
                    }
                }
            }
            rep.Add(new Finding("geometry.skipped", Level.Ok,
                Loc.T("简单模式只给目标不给几何：叶轮叶尖速度、流道流量系数、轴功率平衡三类判据跳过", "Simple mode gives targets, not geometry, so tip speed, flow coefficient and shaft power checks are skipped")
                + Loc.T("（想验这些请切到残酷模式）", "(switch to full mode to check those)")));

            double thrust = v.Get("thrust");
            if (thrust <= 0.0)
                rep.Add(new Finding("thrust", Level.Fail, Loc.T("净推力 ≤ 0：这台发动机推不动自己", "Net thrust is at or below zero: this engine cannot push itself")));
            else
                rep.Add(new Finding("thrust", Level.Ok,
                    string.Format(Loc.T("净推力 {0:F1} kN，SFC {1:F4} kg/(N·h)", "Net thrust {0:F1} kN, SFC {1:F4} kg/(N·h)"), thrust / 1000.0, v.Get("sfc")),
                    thrust / 1000.0));
            return rep;
        }

        private static double DesignRpm(EngineDesignData d, int shaft)
        {
            Dictionary<int, double> r = Cycle.DesignShaftSpeeds(d, OffDesign.T0Sl);
            double v; return r.TryGetValue(shaft, out v) ? v : 0.0;
        }
    }
}