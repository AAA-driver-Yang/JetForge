using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 设计缺陷在飞行中的兑现。
    ///
    /// 到这里之前，判据只是设计器里的一行字：玩家看到"高压压气机级负荷过高"，
    /// 然后照样满油门飞。真机不是这样——级负荷高了会喘振，涡轮超温了会烧，
    /// 燃烧室太短会在高空熄火。这一层把判据翻译成"你今天这趟飞得成什么样"。
    ///
    /// 三条原则：
    /// 1) 只兑现能感知的后果（推力掉、油耗涨、熄火、降额），不做隐藏系数；
    /// 2) 确定性：不用随机数，喘振用相位计时器，这样命令行回归能断言；
    /// 3) 不掩盖原因：状态栏必须说出是哪一条判据在咬人。
    /// </summary>
    public sealed class DefectRuntime
    {
        /// <summary>热端累计损伤 0~1，1 = 烧蚀降额。</summary>
        public double Damage;
        /// <summary>本轮喘振剩余时间（秒）。</summary>
        public double SurgeTimer;
        /// <summary>喘振相位（秒），用于周期性起伏。</summary>
        public double SurgePhase;
        /// <summary>刚触发熄火（模块读一次就清）。</summary>
        public bool JustBlownOut;
        /// <summary>本次评估的推力乘子。</summary>
        public double ThrustScale = 1.0;
        /// <summary>本次评估的油耗乘子（喘振时燃烧效率掉，油耗相对上升）。</summary>
        public double FuelScale = 1.0;
        /// <summary>状态栏一句话。</summary>
        public string State = Loc.T("正常", "normal");
        /// <summary>最严重的那条判据的键。</summary>
        public string Cause = "";
        /// <summary>是否处于喘振中。</summary>
        public bool Surging;

        // ---------- 故障：不是降额，是彻底坏了 ----------
        // 玩家要的是"缺陷会有后果"，但"后果"不等于"一设计就推力为零"。
        // 所以分成两层：判据不成立 → 降额照飞（还带有警告）；
        // 用坏了 / 拉爆了 → 才算故障，这台机器这一趟就到这儿了。

        /// <summary>已经彻底故障（这一趟不可恢复，回编辑器重造才算修）。</summary>
        public bool Failed;
        /// <summary>故障原因那一句话。</summary>
        public string FailNote = "";
        /// <summary>累计喘振次数（每起一次算一次）。</summary>
        public int SurgeEvents;
        /// <summary>累计过载应变量（g·s，超过 9 g 的部分才计）。</summary>
        public double GStrain;
        /// <summary>峰值过载（g），给状态栏与日志。</summary>
        public double GPeak;
        /// <summary>烧穿之后还在继续超温的累计秒数。</summary>
        public double BurnSeconds;
        /// <summary>超速累计秒数。</summary>
        public double SpeedSeconds;
        /// <summary>上一帧是否在喘振。数的是「起了几次」，不是「喘了多久」。</summary>
        internal bool WasSurging;

        /// <summary>
        /// 缺陷台账。设计警告、运行风险、故障事件共用这一本，
        /// 同一个缺陷只有一条记录——这样"设计期已经降额、运行期又按风险再罚一遍"才拦得住。
        /// </summary>
        public FaultBook Book = new FaultBook();

        /// <summary>
        /// 这台机器的故障门槛。**不再写死**：一台设计裕度大、年代新的机器
        /// 该比一台勉强成立的老机器更耐造。由 <see cref="Defects.For"/> 按设计与判据推。
        /// </summary>
        public FailureLimits Limits = FailureLimits.Default;

        public void Reset()
        {
            Damage = 0.0; SurgeTimer = 0.0; SurgePhase = 0.0;
            JustBlownOut = false; ThrustScale = 1.0; FuelScale = 1.0;
            State = Loc.T("正常", "normal"); Cause = ""; Surging = false;
            Failed = false; FailNote = ""; SurgeEvents = 0;
            GStrain = 0.0; GPeak = 0.0; BurnSeconds = 0.0; SpeedSeconds = 0.0;
            WasSurging = false;
            if (Book == null) Book = new FaultBook(); else Book.Reset();
        }
    }

    /// <summary>
    /// 故障门槛。四个数一起给，因为它们的量级来自同一件事：这台机器的"结实程度"。
    ///
    /// 为什么不能写死：玩家报的是"故障层限制不要特别死板，也应该由发动机设计进行判定"。
    /// 一台裕度全满、1990 年代的机器，和一台勉强成立的老机器，同样猛推十次不该一样下场。
    /// </summary>
    public struct FailureLimits
    {
        /// <summary>累计多少次喘振算叶片疲劳断裂。</summary>
        public int SurgeEvents;
        /// <summary>超过这个过载才开始累积应变（g）。</summary>
        public double GBreak;
        /// <summary>应变累计到这个值（g·s）算轴承过载断裂。</summary>
        public double GBreakStrain;
        /// <summary>烧穿之后继续超温多少秒算叶片报废。</summary>
        public double BurnSeconds;
        /// <summary>持续超速多少秒算叶盘爆裂。</summary>
        public double SpeedSeconds;
        /// <summary>过载损伤开不开。关掉之后 <see cref="GBreak"/> 完全不计（不是调到无穷大——
        /// 无穷大会让"最差门槛"那行读数永远正常，看着像其实在计）。</summary>
        public bool GDamageEnabled;
        /// <summary>加强系统的比例（0~1），用于读数里说明这门槛为什么变高了。</summary>
        public double GReinforce;
        /// <summary>这套门槛是怎么来的，一行字。</summary>
        public string Basis;

        public static FailureLimits Default
        {
            get
            {
                FailureLimits f = new FailureLimits();
                f.SurgeEvents = 6;
                f.GBreak = 9.0;
                f.GBreakStrain = 1.2;
                f.BurnSeconds = 20.0;
                f.SpeedSeconds = 25.0;
                f.GDamageEnabled = true;
                f.GReinforce = 0.0;
                f.Basis = Loc.T("缺省门槛", "default threshold");
                return f;
            }
        }
    }

    public static class Defects
    {
        /// <summary>喘振一个周期多长（秒）。真机喘振是间歇性的"砰—砰"，不是持续抖。</summary>
        public const double SurgePeriod = 0.55;
        /// <summary>一个周期里喘振占多久。</summary>
        public const double SurgeDuty = 0.55;
        /// <summary>喘振时推力还剩多少。</summary>
        public const double SurgeThrust = 0.45;
        /// <summary>涡轮超温多少倍开始累积损伤。</summary>
        public const double DamageGrace = 1.00;
        /// <summary>满超温（=限值 12%）下多久把涡轮烧到降额（秒）。</summary>
        public const double DamageSeconds = 90.0;
        /// <summary>烧蚀后的永久降额。</summary>
        public const double BurntDerate = 0.85;

        // ---------- 故障门槛（都是"能看见征兆之后才坏"，不做随机） ----------

        // 这四个门槛（喘振次数 / 过载 / 应变 / 烧穿秒数 / 超速秒数）以前是写死的常量。
        // 现在由 FailureLimits.For(design, report) 从设计裕度与年代推出来，
        // 写死的那一份留在这里只会让人以为它还在起作用——删掉。
        /// <summary>
        /// 判据不成立时给多少推力。
        ///
        /// 以前这里直接是"推力 = 0"（FlightMode.NotSustaining），玩家配出一台
        /// 判据 FAIL 的机器，点火后一点劲都没有，只能回编辑器——那不是缺陷的后果，
        /// 那是"这台发动机不存在"。所以改成降额：能飞，但明显亏，而且状态栏一直在骂。
        ///
        /// 0.78 是"能起飞但爬不上去"的量级；再往下就接近不能飞了，那时该由故障层接手。
        /// </summary>
        public const double DerateFail = 0.78;
        /// <summary>只有 WARN 时的降额（踩线，能用但不宽裕）。</summary>
        public const double DerateWarn = 0.94;

        /// <summary>根据判据报告算这一帧的推力降额。没有报告就按 1.0（还没评估过）。</summary>
        public static double DesignDerate(CheckReport rep)
        {
            if (rep == null) return 1.0;
            if (rep.Fails != null && rep.Fails.Count > 0)
            {
                // 越多条 FAIL 越差，但地板压在 0.60：再低就该由故障层出手了。
                double d = DerateFail - 0.05 * Math.Min(3, rep.Fails.Count - 1);
                return d < 0.60 ? 0.60 : d;
            }
            if (rep.Warns != null && rep.Warns.Count > 0) return DerateWarn;
            return 1.0;
        }

        public static string DescribeDerate(double derate, CheckReport rep)
        {
            if (rep == null) return Loc.T("判据未评估", "checks not evaluated");
            string worst = "";
            if (rep.Fails != null && rep.Fails.Count > 0) worst = rep.Fails[0].Message;
            else if (rep.Warns != null && rep.Warns.Count > 0) worst = rep.Warns[0].Message;
            if (worst.Length == 0) return string.Format(Loc.T("判据通过｜推力 ×{0:F2}", "checks pass | thrust x{0:F2}"), derate);
            return string.Format(Loc.T("判据{0}｜推力 ×{1:F2}｜{2}", "checks {0} | thrust x{1:F2} | {2}"),
                (rep.Fails != null && rep.Fails.Count > 0) ? Loc.T("不成立（仍可运转）", "fails (still runs)") : Loc.T("有警告", "has warnings"), derate, worst);
        }

        /// <summary>把判据列表折成一个"风险档案"：哪一类缺陷、有多严重（0~1）。</summary>
        public sealed class Risk
        {
            public double Surge;      // 喘振/失速
            public double OverTemp;   // 超温
            public double Blowout;    // 贫油熄火
            public double Overspeed;  // 叶尖超速
            public string SurgeCause = "";
            public string TempCause = "";
            public string BlowCause = "";
            public string SpeedCause = "";
        }

        public static Risk Read(List<Finding> findings)
        {
            Risk r = new Risk();
            if (findings == null) return r;
            for (int i = 0; i < findings.Count; i++)
            {
                Finding f = findings[i];
                if (f == null) continue;
                if (f.LevelValue == Level.Ok) continue;
                // 余量越负越严重：-10% 给 0.5，-30% 给 1.0
                double sev = Math.Max(0.0, Math.Min(1.0, -f.MarginFrac * 3.0));
                // 已经是 FAIL 的判据至少算"实打实的风险"——哪怕数字上只差一点点，
                // 它也是设计者不该忽略的那一行。以前按余量线性算，一条 FAIL 只拿到 0.05，
                // 飞行里等于没有后果，判据就白写了。
                if (f.LevelValue == Level.Fail) sev = Math.Max(sev, 0.30);
                // WARN 封顶：踩线的设计可以飞，只是没余量。给它 0.30 以上就等于
                // 让一台"能用但不宽裕"的发动机天天喘——玩家会把这当成 bug。
                else sev = Math.Min(sev, 0.25);
                if (sev <= 0.0) sev = 0.05;   // 只是Loc.T("踩线", "on the line")，给一点底噪
                string k = f.Key ?? "";
                if (k.EndsWith(".surge", StringComparison.Ordinal)
                    || k.EndsWith(".phi_high", StringComparison.Ordinal)
                    || k.EndsWith(".stagepi", StringComparison.Ordinal)
                    || k.EndsWith(".stages", StringComparison.Ordinal))
                {
                    if (sev > r.Surge) { r.Surge = sev; r.SurgeCause = f.Message; }
                }
                else if (k == "T4" || k == "EGT" || k == "T3" || k.EndsWith(".temp", StringComparison.Ordinal))
                {
                    if (sev > r.OverTemp) { r.OverTemp = sev; r.TempCause = f.Message; }
                }
                else if (k == "LBO" || k == "burner.liner" || k == "rich" || k == "far")
                {
                    if (sev > r.Blowout) { r.Blowout = sev; r.BlowCause = f.Message; }
                }
                else if (k.EndsWith(".tip_speed", StringComparison.Ordinal)
                         || k.EndsWith(".overspeed", StringComparison.Ordinal)
                         || k.EndsWith(".ncor", StringComparison.Ordinal))
                {
                    if (sev > r.Overspeed) { r.Overspeed = sev; r.SpeedCause = f.Message; }
                }
            }
            return r;
        }

        /// <summary>
        /// 推进一步。dt ≤ 0 当作"初始化"，只算静态度、不推进计时。
        ///
        /// gLoad / surging 有默认值：老调用点（回归、自检里的单点探针）不用改，
        /// 它们本来也不关心过载与喘振计数。
        /// </summary>
        /// <summary>
        /// 按这台机器的设计与判据推故障门槛。
        ///
        /// 两个输入：①判据风险（喘振裕度、超温、超速各有多紧）——裕度紧的机器，
        /// 同样一次猛推就先坏；②年代——晚期的机器有余量、也有更好的材料与轴承。
        /// 结果落在：喘振 3~13 次、过载门槛 6.5~12 g、烧穿 8~40 s、超速 10~50 s。
        ///
        /// 这不是"调参"，是把原来那四个写死的数换成"从设计里量出来的数"：
        /// 玩家把裕度做厚、把年代往上调，他就真的更抗造——而不是恰好都一样。
        /// </summary>
        public static FailureLimits For(EngineDesignData d, CheckReport rep)
        {
            double robust = 1.0;
            if (rep != null)
            {
                Risk rk = Read(rep.Findings);
                robust = 1.0 - Math.Max(rk.Surge, Math.Max(rk.OverTemp, rk.Overspeed));
            }
            if (robust < 0.0) robust = 0.0;
            if (robust > 1.0) robust = 1.0;
            // 年代红利最多 0.35：再新的机器也不会因为年代新就不怕 15 g。
            double years = d != null && d.Tech != null ? (d.Tech.Year - 1950.0) / 50.0 : 0.5;
            if (years < 0.0) years = 0.0;
            if (years > 1.0) years = 1.0;
            double q = 0.70 * robust + 0.30 * years;
            if (q < 0.0) q = 0.0;
            if (q > 1.0) q = 1.0;

            FailureLimits f = new FailureLimits();
            f.SurgeEvents = (int)Math.Round(3.0 + 10.0 * q);
            f.GDamageEnabled = d == null || d.GDamage;
            double reinforce = d != null ? Math.Min(Math.Max(d.GReinforce, 0.0), 1.0) : 0.0;
            f.GReinforce = reinforce;
            f.GBreak = (6.5 + 5.5 * q) * (1.0 + 1.0 * reinforce);
            f.GBreakStrain = f.GBreak * 0.15;
            f.BurnSeconds = 8.0 + 32.0 * q;
            f.SpeedSeconds = 10.0 + 40.0 * q;
            f.Basis = string.Format(Loc.T("裕度 {0:P0}｜年代 {1:F0} → 喘振 {2} 次、过载 {3:F1} g{4}、烧穿 {5:F0} s、超速 {6:F0} s", "margin {0:P0} | era {1:F0} gives {2} surges, {3:F1} g overload{4}, {5:F0} s burn-through, {6:F0} s overspeed"),
                                    robust, d != null && d.Tech != null ? d.Tech.Year : 1960.0,
                                    f.SurgeEvents, f.GBreak,
                                    f.GDamageEnabled ? (reinforce > 0.001 ? string.Format(Loc.T("（已加强 {0:P0}）", "(reinforced {0:P0})"), reinforce) : "") : Loc.T("（过载损伤已关）", "(overload damage off)"),
                                    f.BurnSeconds, f.SpeedSeconds);
            return f;
        }

        /// <summary>把门槛交给运行时（模块在 EnsureReady 里调一次，设计变了就再调）。</summary>
        public static void Configure(DefectRuntime rt, FailureLimits limits)
        {
            if (rt != null) rt.Limits = limits;
        }

        public static void Step(Risk risk, DefectRuntime rt, double dt,
                                double throttle, double t4, double t4Limit,
                                double far, double alt,
                                double gLoad = 0.0, bool surging = false)
        {
            if (rt == null) return;
            rt.JustBlownOut = false;
            rt.ThrustScale = 1.0;
            rt.FuelScale = 1.0;
            rt.Surging = false;
            rt.Cause = "";
            rt.State = Loc.T("正常", "normal");

            // 已经坏了就不再重算：故障是"这一趟到底了"，不是每帧重新判一次。
            // 每帧重判会让状态栏在"故障"和"正常"之间闪，玩家以为能修好。
            if (rt.Failed)
            {
                rt.ThrustScale = 0.0;
                rt.FuelScale = 0.0;
                rt.State = Loc.T("故障：", "Failure:") + rt.FailNote;
                return;
            }

            if (risk == null) return;

            // 台账每帧先清掉"上一帧还成立、这一帧要重新报"的运行风险，
            // 再由下面各分支重新登记；帧末统一加这一帧的持续时间。
            // 注意不能用 ExpireRisks 每帧清 Active —— 那样 Count 会按帧暴涨。
            FaultBook book = rt.Book;

            // ① 喘振：只在高功率区间犯病。真机慢车不会喘（低压比、大裕度），
            //    所以这里用一个"功率门槛"而不是全程概率——否则玩家慢车也在抖，没法开。
            bool highPower = throttle > 0.55;
            bool hotEnough = t4 > t4Limit * 0.80;
            if (risk.Surge > 0.05 && highPower && hotEnough)
            {
                if (rt.SurgeTimer > 0.0)
                {
                    rt.SurgeTimer -= Math.Max(dt, 0.0);
                }
                else
                {
                    // 相位推进：风险越高，喘振占空比越大，但周期不变（这是"砰—砰"的节奏）
                    rt.SurgePhase += Math.Max(dt, 0.0);
                    if (rt.SurgePhase >= SurgePeriod * (1.0 - SurgeDuty))
                    {
                        rt.SurgePhase = 0.0;
                        if (risk.Surge > 0.25)
                            rt.SurgeTimer = SurgePeriod * SurgeDuty * Math.Min(1.0, risk.Surge + 0.2);
                    }
                }

                if (rt.SurgeTimer > 0.0)
                {
                    rt.Surging = true;
                    rt.ThrustScale = SurgeThrust;
                    // 喘振时燃烧没停，但推力没了——油照样烧，所以 SFC 会暴涨。
                    rt.FuelScale = 1.0 / SurgeThrust;
                    rt.State = Loc.T("喘振", "surge");
                    rt.Cause = risk.SurgeCause;
                    if (book != null)
                        book.Raise(CauseCode(risk.SurgeCause, Loc.T("喘振", "surge")),
                                   FaultTier.Risk,
                                   risk.Surge >= 0.60 ? FaultSeverity.Major : FaultSeverity.Minor,
                                   Loc.T("喘振强度", "surge severity"), risk.Surge, 1.0, "",
                                   string.Format(Loc.T("推力 ×{0:F2}、油耗 ×{1:F2}，叶片受一次疲劳", "thrust x{0:F2}, fuel flow x{1:F2}, one fatigue cycle on the blades"), SurgeThrust, 1.0 / SurgeThrust),
                                   Loc.T("收油门/转速回落到 55% 以下", "ease the throttle, spool back below 55%"));
                }
                else
                {
                    // 只有风险确实不小才报"边缘"。否则一条无关痛痒的 WARN 就让状态栏
                    // 常年挂着"喘振边缘"，玩家会把它当装饰看，真喘起来反而不信了。
                    if (risk.Surge > 0.30)
                    {
                        rt.State = Loc.T("喘振边缘", "surge edge");
                        if (risk.SurgeCause.Length > 0) rt.Cause = risk.SurgeCause;
                    }
                }
            }

            // ② 涡轮超温：不是"立刻烧穿"，是累计损伤。烧穿了就永久降额。
            bool burntBefore = rt.Damage >= 1.0;
            if (risk.OverTemp > 0.05 || (t4Limit > 0.0 && t4 > t4Limit * DamageGrace))
            {
                double over = t4Limit > 0.0 ? Math.Max(0.0, t4 / t4Limit - DamageGrace) : 0.0;
                double rate = over / 0.12 / DamageSeconds;   // 超温 12% → 90 秒烧穿
                if (rate > 0.0)
                {
                    rt.Damage = Math.Min(1.0, rt.Damage + rate * Math.Max(dt, 0.0));
                    if (rt.Damage < 1.0)
                    {
                        rt.State = rt.Surging ? Loc.T("喘振", "surge") : Loc.T("涡轮超温", "turbine overtemperature");
                        rt.Cause = rt.Cause.Length > 0 ? rt.Cause : risk.TempCause;
                        if (book != null)
                            book.Raise(CauseCode(risk.TempCause, Loc.T("涡轮超温", "turbine overtemperature")), FaultTier.Risk,
                                       over > 0.06 ? FaultSeverity.Major : FaultSeverity.Minor,
                                       Loc.T("涡轮进口温度", "turbine inlet temperature"), t4, t4Limit, "K",
                                       string.Format(Loc.T("热端累计损伤 {0:P0}，满了就烧穿降额", "hot section damage {0:P0}; at full it burns through and derates"), rt.Damage),
                                       Loc.T("收油门让 T4 回到 1.00 倍限值以下", "ease the throttle to bring T4 back under 1.00 of the limit"));
                    }
                }
            }

            // ③ 烧穿之后：永久降额，且再也不会回到满推力
            if (rt.Damage >= 1.0)
            {
                rt.ThrustScale *= BurntDerate;
                rt.State = rt.Surging ? Loc.T("喘振（涡轮已烧蚀）", "surge (turbine already burned)") : Loc.T("涡轮烧蚀降额", "turbine burn derate");
                if (risk.TempCause.Length > 0) rt.Cause = risk.TempCause;
                if (book != null && !burntBefore)
                    book.Raise(Loc.T("涡轮烧蚀降额", "turbine burn derate"), FaultTier.Event, FaultSeverity.Critical,
                               Loc.T("温端累计损伤", "hot section cumulative damage"), rt.Damage, 1.0, "",
                               string.Format(Loc.T("推力 ×{0:F2} 且不可恢复", "thrust x{0:F2} and not recoverable"), BurntDerate),
                               Loc.T("回编辑器重造（这一趟到此为止）", "rebuild it in the editor (this flight is done)"));
            }

            // ④ 贫油熄火：只有"燃烧室太短"或"设计点贫油"这类缺陷才会在高空犯。
            //    不用随机数——用"高度 + 贫油度"算一个确定性判据，玩家能学会避开。
            bool leanEnough = far > 0.0 && far < 0.0055;
            bool thinAir = alt > 9000.0;
            if (risk.Blowout > 0.05 && leanEnough && thinAir && throttle < 0.75)
            {
                rt.JustBlownOut = true;
                rt.ThrustScale = 0.0;
                rt.State = Loc.T("贫油熄火", "lean blowout");
                rt.Cause = risk.BlowCause;
                if (book != null)
                    book.Raise(CauseCode(risk.BlowCause, Loc.T("贫油熄火", "lean blowout")), FaultTier.Risk, FaultSeverity.Major,
                               Loc.T("余气系数", "excess air ratio"), far, 0.0055, "",
                               Loc.T("推力间断、要重新点火", "thrust stumbles; relight needed"),
                               Loc.T("推油门到 75% 以上或下降高度", "push the throttle past 75% or descend"));
            }

            // ⑤ 叶尖超速：不熄火，但效率和寿命都掉——推力打个折，油耗升。
            if (risk.Overspeed > 0.05 && !rt.Surging && rt.Damage < 1.0 && !rt.JustBlownOut)
            {
                rt.ThrustScale *= 1.0 - 0.10 * risk.Overspeed;
                rt.FuelScale *= 1.0 + 0.10 * risk.Overspeed;
                if (book != null)
                    book.Raise(CauseCode(risk.SpeedCause, Loc.T("叶尖超速", "tip overspeed")), FaultTier.Risk,
                               risk.Overspeed > 0.5 ? FaultSeverity.Major : FaultSeverity.Minor,
                               Loc.T("超速强度", "overspeed severity"), risk.Overspeed, 1.0, "",
                               string.Format(Loc.T("推力 ×{0:F2}、油耗 ×{1:F2}，叶盘寿命折损", "thrust x{0:F2}, fuel flow x{1:F2}, disc life shortened"), 1.0 - 0.10 * risk.Overspeed, 1.0 + 0.10 * risk.Overspeed),
                               Loc.T("收油门，让叶尖马赫回落到设计值", "ease the throttle to bring tip Mach back to design"));
                if (rt.State == Loc.T("正常", "normal"))
                {
                    rt.State = Loc.T("叶尖超速", "tip overspeed");
                    rt.Cause = risk.SpeedCause;
                }
            }

            // ⑥ 故障：把"用坏了"和"设计不好"分开。
            //    上面那几条都是可恢复的（降额、喘振、熄火能重点），这里一旦成立就到底了。
            //    全部用累计量判定，没有随机——玩家能学会"别这么飞"。
            double sdt = Math.Max(dt, 0.0);
            if (surging && !rt.WasSurging) rt.SurgeEvents++;
            rt.WasSurging = surging;

            FailureLimits lim = rt.Limits;

            if (gLoad > rt.GPeak) rt.GPeak = gLoad;
            // 过载损伤关掉时连应变都不攒：不然关掉开关只是"现在不炸"，
            // 累积量还挂在存档里，重新打开就立刻炸——那叫请假不叫做完。
            if (lim.GDamageEnabled && gLoad > lim.GBreak) rt.GStrain += (gLoad - lim.GBreak) * sdt;

            if (rt.Damage >= 1.0 && t4Limit > 0.0 && t4 > t4Limit) rt.BurnSeconds += sdt;
            if (risk.Overspeed > 0.50) rt.SpeedSeconds += sdt;

            string fail = null;
            if (rt.SurgeEvents >= lim.SurgeEvents)
                fail = string.Format(Loc.T("压气机叶片疲劳断裂（累计 {0} 次喘振）", "compressor blade fatigue fracture ({0} surges accumulated)"), rt.SurgeEvents);
            else if (rt.BurnSeconds >= lim.BurnSeconds)
                fail = string.Format(Loc.T("涡轮叶片烧穿（烧蚀后仍超温 {0:F0} 秒）", "turbine blade burn-through (still over temperature {0:F0} s after burning)"), rt.BurnSeconds);
            else if (rt.GStrain >= lim.GBreakStrain)
                fail = string.Format(Loc.T("转子轴承过载断裂（{0:F1} g 累计应变）", "rotor bearing overload fracture ({0:F1} g accumulated strain)"), rt.GStrain);
            else if (rt.SpeedSeconds >= lim.SpeedSeconds)
                fail = string.Format(Loc.T("叶盘超速爆裂（持续 {0:F0} 秒）", "disc burst from overspeed ({0:F0} s sustained)"), rt.SpeedSeconds);

            if (fail != null)
            {
                rt.Failed = true;
                rt.FailNote = fail;
                rt.ThrustScale = 0.0;
                rt.FuelScale = 0.0;
                rt.Cause = fail;
                rt.State = Loc.T("故障：", "Failure:") + fail;
                if (book != null)
                    book.Raise(fail, FaultTier.Event, FaultSeverity.Critical,
                               rt.State, 1.0, 1.0, "", Loc.T("推力已断，这一趟不可恢复", "thrust is gone; this flight is not recoverable"),
                               Loc.T("回编辑器重造", "rebuild it in the editor"));
            }

            // 这一帧的持续时间统一记进台账（运行风险靠 LastSeen 判还在不在，见 FaultBook.Step）
            if (book != null) book.Step(Math.Max(dt, 0.0));
        }

        /// <summary>
        /// 缺陷的识别串：优先用判据给出的那一句说明（设计期与运行期是同一句，才能去重），
        /// 没有说明就用一个固定的类别名。
        /// </summary>
        private static string CauseCode(string cause, string fallback)
        {
            return string.IsNullOrEmpty(cause) ? fallback : cause;
        }

        /// <summary>给状态栏和日志用的一句话。</summary>
        public static string Describe(Risk risk, DefectRuntime rt)
        {
            if (rt == null) return Loc.T("缺陷：未评估", "Defect: not evaluated");
            if (rt.Failed)
                return string.Format(Loc.T("故障：{0}｜推力已断｜损伤 {1:P0}", "Failure: {0} | thrust cut | damage {1:P0}"), rt.FailNote, rt.Damage);
            string s = string.Format(Loc.T("缺陷：{0}｜推力 ×{1:F2}｜油耗 ×{2:F2}｜损伤 {3:P0}", "Defect: {0} | thrust x{1:F2} | fuel flow x{2:F2} | damage {3:P0}"),
                rt.State, rt.ThrustScale, rt.FuelScale, rt.Damage);
            if (rt.SurgeEvents > 0) s += string.Format(Loc.T("｜喘振 {0} 次", " | {0} surges"), rt.SurgeEvents);
            if (rt.GPeak > rt.Limits.GBreak) s += string.Format(Loc.T("｜峰值 {0:F1} g（应变 {1:F2}）", " | peak {0:F1} g (strain {1:F2})"), rt.GPeak, rt.GStrain);
            if (rt.Cause.Length > 0) s += Loc.T("｜起因：", " | Cause:") + rt.Cause;
            return s;
        }
    }
}