using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 一次整机求解的完整现场：每个测站上的气流、每个部件的产物、进气道解、
    /// 以及给老接口用的 <see cref="CycleResult"/>。
    ///
    /// 以前"设计点算完了"只有一个扁平的 CycleResult：你只能知道 T3=..., OPR=...，
    /// 想知道"这股气是从哪一步过来的"只能回去读 671 行。现在站表是全的。
    /// </summary>
    public sealed class NetworkSolution
    {
        public CycleResult Result = new CycleResult();
        public GasPathTemplate Path;
        public Dictionary<int, GasState> StationTable = new Dictionary<int, GasState>();
        public Dictionary<string, ComponentResult> Parts = new Dictionary<string, ComponentResult>();
        public IntakeSolution Intake;
        /// <summary>几何接口表：每个部件进出口的截面与相邻两截面的面积比。只描述几何，不参与热力计算。</summary>
        public List<PortLink> Ports;
        /// <summary>高压压气机特性图（由设计点反推，见 <see cref="CompressorChart"/>）。可能为 null。</summary>
        public CompressorChart Chart;
        /// <summary>全链路是否真的走完了（进气道→喷管）。</summary>
        public bool Complete;

        /// <summary>
        /// 求解过程中"算得出来但不对劲"的事（限流、失配、退化）。
        /// 这跟 <c>ComponentResult.Warnings</c> 不同：那些是部件自己的抱怨，
        /// 这里是整条链路层面的判断——单看任何一个部件都合规，连起来才出问题的那种。
        /// </summary>
        public List<string> Warnings = new List<string>();

        public void Warn(string text)
        {
            if (!string.IsNullOrEmpty(text)) Warnings.Add(text);
        }

        public GasState Station(int id)
        {
            GasState g;
            return StationTable.TryGetValue(id, out g) ? g : null;
        }

        public ComponentResult Of(string part)
        {
            ComponentResult r;
            return Parts.TryGetValue(part.ToUpperInvariant(), out r) ? r : null;
        }

        public double Value(string part, string key, double fallback = 0.0)
        {
            ComponentResult r = Of(part);
            return r == null ? fallback : r.Get(key, fallback);
        }

        /// <summary>站表一行一站，给自检日志与界面读数用。</summary>
        public string DescribeStations()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            List<int> keys = new List<int>(StationTable.Keys);
            keys.Sort();
            foreach (int k in keys)
            {
                GasState g = StationTable[k];
                sb.AppendLine(string.Format("{0,-10} Pt={1,10:F0}Pa Tt={2,7:F1}K mdot={3,7:F2}kg/s",
                    Stations.Label(k), g.TotalPressure, g.TotalTemperature, g.MassFlow));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 整机网络：按气路模板逐站求解，把部件产物汇总成老的 <see cref="CycleResult"/>。
    ///
    /// 这一段是" архитектура 升级"的落点——以前 `Cycle.SolveDesignPoint` 里那串
    /// 语句顺序，现在变成 `GasPathTemplate` 的一张表 + 这里的一个 switch。
    /// 新插一个部件（比如中压压气机）只需要改模板，不用改这里。
    /// </summary>
    public static class EngineNetwork
    {
        /// <summary>
        /// 部件级的规格校验：进气道几何、压气机级数、燃烧室温度与长度、涡轮挂轴。
        ///
        /// 这些检查一直写在部件类的 `Validate` 里，但**全仓没有任何地方调用它们**
        /// （`grep -rn "\.Validate(" KSP/Source` 只有 `GasState.Validate`、
        /// `GasPathTemplate.Validate`、进气道型线 `IntakeProfile.Validate` 三种）。
        /// 后果是设计里写错的东西不会变成错误：级数填 −3 时 `GasPathTemplate.ForDesign`
        /// 用 `Stages > 0` 判"有没有这一级"，那一级被当成不存在，整台机器悄悄换成另一种构型，
        /// 推力照给、判据照过。**沉默的错比报错的错难查**，所以这里统一问一遍。
        /// </summary>
        private static void ValidateSpecs(EngineDesignData d, CycleResult res)
        {
            List<string> errs = new List<string>();
            if (d.Intake != null) new IntakeComponent().Validate(d, errs);
            if (d.Lpc != null) new CompressorComponent(d.Lpc, "LPC", Stations.LpcExit, true).Validate(d, errs);
            if (d.Hpc != null) new CompressorComponent(d.Hpc, "HPC", Stations.HpcExit, false).Validate(d, errs);
            if (d.Burner != null) new BurnerComponent().Validate(d, errs);
            if (d.Hpt != null) new TurbineComponent(d.Hpt, "HPT", Stations.HptExit).Validate(d, errs);
            if (d.Ipt != null && d.Ipt.Stages > 0) new TurbineComponent(d.Ipt, "IPT", Stations.IptExit).Validate(d, errs);
            if (d.Lpt != null) new TurbineComponent(d.Lpt, "LPT", Stations.TurbineExit).Validate(d, errs);
            if (errs.Count == 0) return;
            for (int i = 0; i < errs.Count; i++) res.Errors.Add(errs[i]);
            res.Ok = false;
        }
        /// <summary>
        /// 设计点求解：环境 → 进气道 → 风扇 → 分流 → 压气机 → 燃烧室 → 涡轮 → 加力 → 喷管。
        ///
        /// <paramref name="install"/> 是机上装的**独立进气道零件**的合计口径；没装就传 null，
        /// 整条链路照旧吃设计里那份 IntakeSpec（老存档/单件装机一字不动）。
        /// </summary>
        public static NetworkSolution SolveDesign(EngineDesignData d, IntakeInstall install = null,
                                                  double flowScale = 1.0, int chokePass = 0)
        {
            return SolveAt(d, double.NaN, double.NaN, 1.0, install, flowScale, chokePass);
        }

        /// <summary>
        /// 同一条部件链路，换一个工况点跑。
        ///
        /// 以前这条链只会算设计点（`d.DesignAlt` / `d.DesignMach` / 转速比例 1.0），
        /// 非设计点走的是 `OffDesign.Solve` 里另一套顺序代码——同一个物理量在两处各写一遍，
        /// 迟早有一处先改、另一处没跟上。这个入口把"点"变成参数，链路本身不动：
        /// 传 NaN 就退回设计点，所以 `SolveDesign` 的行为与以前逐位相同。
        ///
        /// 注意它**只解决"这个转速、这个高度、这个马赫下这条链路给出什么"**，
        /// 不负责找平衡转速（那是 `OffDesign` 的活）：燃油仍按设计点那份烧，
        /// 所以拿它做非设计点的正式读数之前，进气道/燃烧室还得接上真正的油量控制。
        /// </summary>
        public static NetworkSolution SolveAt(EngineDesignData d, double alt, double mach, double speedFrac,
                                              IntakeInstall install = null,
                                              double flowScale = 1.0, int chokePass = 0)
        {
            NetworkSolution sol = new NetworkSolution();
            CycleResult res = sol.Result;
            if (d == null)
            {
                res.Ok = false;
                res.Errors.Add(Loc.T("没有设计", "no design"));
                return sol;
            }

            double altUse = GasState.IsFinite(alt) ? alt : d.DesignAlt;
            double machUse = GasState.IsFinite(mach) ? mach : d.DesignMach;
            double fracUse = speedFrac > 0.0 && GasState.IsFinite(speedFrac) ? speedFrac : 1.0;

            TechLevel tech = d.Tech;
            double p0, t0;
            Gas.Isa(altUse, out p0, out t0);

            double pt0, tt0;
            if (machUse > 0.0) Gas.Total(p0, t0, machUse, out pt0, out tt0);
            else { pt0 = p0; tt0 = t0; }
            double v0 = machUse * Gas.SoundSpeed(t0);
            res.T1 = tt0;

            SolverContext ctx = new SolverContext
            {
                Design = d, Tech = tech,
                Altitude = altUse, Mach = machUse,
                AmbientPressure = p0, AmbientTemperature = t0,
                FlightVelocity = v0, SpeedFrac = fracUse,
                RpmDesign = Cycle.DesignShaftSpeeds(d, tt0),
            };

            // 独立进气道：有就整段换掉，没有就 null（IntakeComponent 自己走老路）
            IntakeSolution intakePreset = (install != null && install.Present && install.Valid)
                ? install.ToSolution(altUse, machUse)
                : null;
            res.Rpm = ctx.RpmDesign;

            // ---- 拓扑：先校验，再照表走 ----
            sol.Path = GasPathTemplate.ForDesign(d);
            List<string> topo = new List<string>();
            sol.Path.Validate(d, topo, res.Warnings);
            foreach (string e in topo) { res.Errors.Add(e); res.Ok = false; }

            // ---- 部件规格：负级数、温度没设这类"写错了但气路还能算"的东西 ----
            // 必须在拓扑之前问，不能等循环走到那个部件再问：`GasPathTemplate.ForDesign`
            // 判"有没有这一级"用的是 `Stages > 0`，级数写成负数时那一级**根本不会进节点表**，
            // 于是整台机器被悄悄换成另一种构型（涡喷照算、推力照给），谁也看不出哪里错了。
            ValidateSpecs(d, res);

            GasState cur = GasState.FromTotal(Stations.Ambient, pt0, tt0, 0.0, FlowRole.Core);
            sol.StationTable[Stations.Ambient] = cur;

            GasState bypass = null;
            double mdotAir = 0.0, mdotCore = 0.0, mdotByp = 0.0;
            double fpr = 1.0, tpr = 1.0;
            double fuelMain = 0.0, fuelAb = 0.0, far = 0.0, farAb = 0.0;
            double veCore = 0.0, veByp = 0.0, pExit = 0.0;
            double grossCore = 0.0, grossByp = 0.0;
            double wFan = 0.0, wLpc = 0.0, wHpc = 0.0;
            bool demandChecked = false;
            bool hasCoreNozzle = false;

            foreach (PathNode n in sol.Path.Core)
            {
                if (cur == null) break;

                switch (n.Part)
                {
                    case "INTAKE":
                    {
                        IntakeComponent c = new IntakeComponent { Installed = intakePreset };
                        ComponentResult r = c.Solve(cur, ctx);
                        sol.Intake = c.Last;
                        if (!Absorb(sol, n, r, ref res)) return sol;
                        cur = r.Outlet;
                        tpr = r.PressureRatio;
                        // 机匣内那一截进气管道：法兰 → 风扇/压气机面。整机长度在这里第一次吃到亏。
                        double dl, dd;
                        DuctLoss.Inlet(d, out dl, out dd);
                        res.DuctInletLoss = DuctLoss.Apply(cur, dl, dd);
                        mdotAir = cur.MassFlow;
                        res.MdotAir = mdotAir;
                        res.T1 = cur.TotalTemperature;

                        // ── 流量匹配（交接文档 A §六 那条禁令）─────────────────────────
                        // 进气道能供多少，核心机就只能吃多少。以前这里只把 available 塞进
                        // 诊断表，下游没人读——于是"进气道小到吞不下"这件事只体现在一个没人看的
                        // 数字上，压气机照样按满流量算叶尖速度和温升，喷管再按那些数出推力。
                        // 现在的做法是：把流量按可用值封顶，让下游所有部件**按受限流量重算一遍**，
                        // strong 一点说，宁可让这台机器算出一台"被憋住的机器"，也不给它一个假推力。
                        if (sol.Intake != null && GasState.IsFinite(sol.Intake.AvailableMassFlow) && sol.Intake.AvailableMassFlow > 0.0)
                        {
                            double avail = sol.Intake.AvailableMassFlow;
                            res.InletAvailableMassFlow = avail;
                            if (mdotAir > avail * 1.001)
                            {
                                res.InletFlowLimited = true;
                                res.InletMassFlowShortfall = mdotAir - avail;
                                cur.MassFlow = avail;
                                mdotAir = avail;
                                res.MdotAir = avail;
                                sol.Warn(string.Format(
                                    Loc.T("进气道限流：需求 {0:F3} kg/s > 可用 {1:F3} kg/s（缺 {2:F3}），下游按可用流量重算", "Intake flow limited: demanded {0:F3} kg/s > available {1:F3} kg/s (short {2:F3}); downstream re-solved at the available flow"),
                                    avail + res.InletMassFlowShortfall, avail, res.InletMassFlowShortfall));
                            }
                        }

                        // ── 堵塞侧"把气压回去"这件事为什么没做 ────────────────────────
                        // 上一版这里有一段按 `flowScale` 压缩进气流量的钳位，配着下面的递归重解，
                        // 想把跑出堵塞线的工作点压回线上。实测（§173）它**改不动气路**：
                        // 把这个比例一路压到 0.10，进气道口只剩 7.55 kg/s，可风扇、分流器、
                        // 压气机的出流仍是 37.75 / 25.34 / 22.30 kg/s，堵塞余量钉在 −29.5%
                        // 一丝不动。原因是从风扇往下每个部件都从自己那份局部流量重建状态
                        // （`GasState.FromTotal`），网络这一层改 `cur.MassFlow` 只是在改一本账。
                        // 也就是说：要真限流，得让风扇与压气机的**入口流量**可以外部指定，
                        // 那是把部件接口改成"吃给定流量"的结构活，不是这里加两行能成的。
                        // 所以这段钳位与递归一起删了——留着它只会让报表说"已限流"而物理没限。
                        // 现在越界只有告警（下面 `chart.Choked` 那段），结论照实说：这台机器
                        // 此刻的流量超出了它这条等转速线能过的量，推力是按"能过"算的。

                        // ── 压气机能吞多少 ────────────────────────────────────────────
                        // 多级压气机的前排级在大部分工况下是壅塞的，所以换算流量基本正比于换算转速：
                        // 转速掉到 80%，能吞的流量也大致只剩 80%，而不是进气道给多少就吃多少。
                        // 以前这里不看转速，于是 70% 转速还想吃满设计点的流量，压气机照样算温升、
                        // 涡轮照样按满流量做功，推力反着涨。
                        //
                        // 设计点（fracUse = 1）时这条上限恰好等于进气道那份数（同是 LegacyMassFlow 口径），
                        // 所以设计点一个数都不动——这是"新机制只在新工况点上生效"的写法，
                        // 与 `SolveAt(NaN, NaN, 1.0)` 逐位等于 `SolveDesign` 那条断言是同一件事。
                        //
                        // 一阶近似：真正的做法是让特性图给出工作点（修正流量 × 压比 × 效率三者联立），
                        // 这里只用了"前排壅塞 ⇒ 流量 ∝ 转速"这一条，够把"转速不掉流量"掰过来。
                        if (fracUse < 0.999 && sol.Intake != null
                            && GasState.IsFinite(sol.Intake.LegacyMassFlow) && sol.Intake.LegacyMassFlow > 0.0)
                        {
                            // 注意：放气**不提高**这条上限。真机上放气之所以能救喘振，是因为
// "前排多吞、后排少要"，那是逐级的事；我们这台压气机是一个整体，只有进口气流一个数，
// 拿 (1+b) 把上限抬起来等于凭空多给流量——那是编数据，不是建模。
                            double swallow = sol.Intake.LegacyMassFlow * fracUse;
                            res.CompressorSwallowLimit = swallow;
                            if (mdotAir > swallow * 1.001)
                            {
                                res.CompressorFlowLimited = true;
                                double extra = mdotAir - swallow;
                                sol.Warn(string.Format(
                                    Loc.T("压气机吞不下：转速 {0:F0}% 的前排级最多 {1:F3} kg/s，进气道给了 {2:F3}，多出来的 {3:F3} 只能憋在进气道里", "Compressor cannot swallow it: at {0:F0}% speed the front stages take at most {1:F3} kg/s but the intake delivers {2:F3}, and the extra {3:F3} has nowhere to go but back into the intake"),
                                    fracUse * 100.0, swallow, mdotAir, extra));
                                cur.MassFlow = swallow;
                                mdotAir = swallow;
                                res.MdotAir = swallow;
                            }
                        }

                        if (!d.HasFan)
                        {
                            // 涡喷没有风扇：进气道出口就是压气机进口，测站 2 = 测站 1。
                            // 老 Cycle 里这是"tt2=tt1、pt2=pt1"两行，这里必须显式补上——
                            // 否则 res.MdotCore 留在 0，判据算流量系数时 φ=0，会误判"已进失速区"，
                            // 然后 FitSurge 把叶尖直径一路缩到 0.10 m（J79 就是这么塌的）。
                            mdotCore = mdotAir;
                            mdotByp = 0.0;
                            res.MdotCore = mdotAir;
                            res.Bpr = 0.0;
                            res.T2 = cur.TotalTemperature;
                            res.Fpr = 1.0;
                        }
                        break;
                    }

                    case "FAN":
                    {
                        FanComponent c = new FanComponent();
                        ComponentResult r = c.Solve(cur, ctx);
                        if (!Absorb(sol, n, r, ref res)) return sol;
                        cur = r.Outlet;
                        fpr = r.PressureRatio;
                        wFan = r.PowerRequired;
                        ctx.AddDemand(d.Fan.Shaft, wFan);
                        res.Fpr = fpr;
                        res.T2 = cur.TotalTemperature;
                        // 风扇叶尖速度与轴的叶尖速度是两件事：齿轮比 >1 时风扇按"轴转速/齿轮比"转。
                        // 以前结果里没有这两个量，非设计点想说"齿轮生效"只能靠旁路推力间接猜，
                        // 所以这里如实报出来（§204）。
                        res.FanTipSpeed = r.Get("fan_tip");
                        res.FanRpm = r.Get("fan_rpm");
                        // 齿轮箱柔度（§209.7 遗留项）：风扇功率与风扇转速此刻都在手上，顺手算掉。
                        // 特意不走 MassCost——那条路会回头调 Cycle.SolveDesignPoint，在设计点求解内部就是死循环。
                        TorsionResult torque = Torsion.Evaluate(d, wFan, res.FanRpm);
                        if (torque.Present)
                        {
                            res.GearboxTorsionPresent = true;
                            res.GearboxTorque = torque.Torque;
                            res.GearboxTwistDeg = torque.TwistDeg;
                            res.GearboxFreqHz = torque.FreqHz;
                        }
                        break;
                    }

                    case "SPLIT":
                    {
                        SplitterComponent c = new SplitterComponent();
                        ComponentResult r = c.Solve(cur, ctx);
                        if (!Absorb(sol, n, r, ref res)) return sol;
                        cur = r.Outlets[0];
                        if (r.Outlets.Count > 1) bypass = r.Outlets[1];
                        mdotCore = r.Get("mdot_core");
                        mdotByp = r.Get("mdot_bypass");
                        res.Bpr = r.Get("bpr");
                        res.MdotCore = mdotCore;
                        break;
                    }

                    case "LPC":
                    case "HPC":
                    {
                        bool low = n.Part == "LPC";
                        CompressorSpec spec = low ? d.Lpc : d.Hpc;
                        GasState before = cur;
                        CompressorComponent c = new CompressorComponent(spec, n.Part, n.OutletStation, low);
                        ComponentResult r = c.Solve(cur, ctx);
                        if (!Absorb(sol, n, r, ref res)) return sol;
                        cur = r.Outlet;
                        if (low) wLpc = r.PowerRequired; else wHpc = r.PowerRequired;
                        ctx.AddDemand(spec.Shaft, r.PowerRequired);

                        // ── 特性图参与工作点 ──────────────────────────────────────
                        // 前面那道"流量 ∝ 转速"是堵在进气道那头的上限，它只知道
                        // "转速低的时候前排级只能吞这么多"，不知道这个工作点**在图上落在哪**。
                        // 这里把算出来的流量与压比放回这张压气机自己的图上，读出喘振裕度——
                        // 图上的工作点与喘振线的距离才是判据该看的数，不是某个经验阈值。
                        //
                        // 高压、低压各有一张自己的图（`ForHighPressureCompressor` /
                        // `ForLowPressureCompressor`）。风扇那张还没做——涡扇的风扇压比
                        // 只有 1.1~1.6，用它来判喘振的意义不大，先不假装有。
                        if (before != null && cur != null && before.TotalPressure > 0.0)
                        {
                            try
                            {
                                // 图必须来自**设计点**（缓存），不能拿这一刻的进/出口现建：
                                // 现建等于每次都宣布"此刻就是设计点"，φ 恒为 1/nc，
                                // 50% 转速就报出 −29.5% 的假堵塞（§174）。
                                CompressorChart chart = CompressorChart.DesignChart(d, low, ctx.Tech);
                                if (chart != null && chart.DesignWc > 0.0 && cur.MassFlow > 0.0)
                                {
                                    double th = before.TotalTemperature / CompressorChart.T0Ref;
                                    double de = before.TotalPressure / CompressorChart.P0Ref;
                                    double wc = before.MassFlow * Math.Sqrt(th) / Math.Max(de, 1e-6);
                                    double nc = fracUse > 1e-3 ? fracUse : 1e-3;
                                    // 转速跑到图外面：按最近的转速线限幅，并且说出来（§175）。
                                    // 静默限幅等于编一条不存在的转速线，调用方会以为那是量出来的。
                                    CompressorChart.ChartFlags offMap = chart.FlagsOf(nc, wc);
                                    if ((offMap & (CompressorChart.ChartFlags.NcBelowMap | CompressorChart.ChartFlags.NcAboveMap)) != 0
                                        && !sol.Warnings.Exists(delegate (string w) { return w.StartsWith(Loc.T("换算转速在特性图范围外", "converted speed outside the map")); }))
                                        sol.Warn(string.Format(
                                            Loc.T("换算转速在特性图范围外：Nc {0:F2} 不在图上（{1:F2}~{2:F2}），按最近的转速线限幅——这一段压比与效率只有形状，没有精度", "Converted speed outside the map: Nc {0:F2} is off the map ({1:F2} to {2:F2}), clamped to the nearest speed line — pressure ratio and efficiency here have shape but no accuracy"),
                                            nc, chart.NcMin, chart.NcMax));
                                    double phi = wc / Math.Max(chart.DesignWc * nc, 1e-9);
                                    double prAct = cur.TotalPressure / before.TotalPressure;
                                    // 流量律与图的口径必须对得上：本模型的流量按"设计流量 × 转速"给，
                                    // 而图的转速线在同一个 Nc 上只过一个换算流量。设计点带冲压
                                    // （J79：11 km 与地面同一个 Nc，换算流量 143.3 对 78.1，差 1.8 倍），
                                    // 两个点在图上是同一条转速线上的两个流量——那是图之外的口径差，
                                    // 既不是喘振也不是堵塞。这种时候宁可报"不可用"（-1），
                                    // 也不要报一个看着像真的负数（§174）。
                                    if (phi < 0.75 || phi > 1.30)
                                    {
                                        if (low) { res.LpcSurgeMargin = -1.0; res.LpcChokeMargin = -1.0; }
                                        else { res.HpcSurgeMargin = -1.0; res.HpcChokeMargin = -1.0; }
                                        if (!sol.Warnings.Exists(delegate (string w) { return w.StartsWith(Loc.T("压气机特性图不适用", "compressor map not applicable")); }))
                                            sol.Warn(string.Format(
                                                Loc.T("压气机特性图不适用：换算流量 {0:F1} kg/s 是设计转速线（Nc {1:F2}）的 {2:F0}%。流量按转速给、图按换算流量给，两者只在设计工况对齐，这一点的裕度不报数", "Compressor map not applicable: the converted flow {0:F1} kg/s is {2:F0}% of the design speed line (Nc {1:F2}). Flow follows speed while the map follows converted flow; they agree only at the design point, so no margin is reported here"),
                                                wc, nc, phi * 100.0));
                                    }
                                    else
                                    {
                                    // 用实测压比定位，而不是用图上按 ψ 算回来的压比：
                                    // 图是我们自己造的，实测点才是这台机器这一刻的真身。
                                    MapPoint op = new MapPoint(nc, wc, prAct, chart.DesignEta);
                                    // 图对同一个位置该说什么（压比与效率），与实测并列摆出来：
                                    // 两个数差得远就是"这台机器不在这张图上"，比藏起来好（§175）。
                                    CompressorChart.ChartFlags qf;
                                    MapPoint said = chart.Query(nc, wc, out qf);
                                    if (!low)
                                    {
                                        res.HpcChartPr = said.Pr;
                                        res.HpcChartEta = said.Eta;
                                        res.HpcChartFlags = (int)qf;
                                    }
                                    double sm = chart.SurgeMargin(op);
                                    if (low) res.LpcSurgeMargin = sm; else res.HpcSurgeMargin = sm;

                                    // 堵塞侧：流量塞得比这条等转速线能过的还多。
                                    // 以前这里只看喘振一侧，于是"过不去的气"被当成正常点算下去——
                                    // 那样算出来的压比不是这台机器能给的，是这条线撑不住了。
                                    double cm = chart.ChokeMargin(op);
                                    if (low) res.LpcChokeMargin = cm; else res.HpcChokeMargin = cm;

                                    // 逐级：整机余量说的是"这台压气机离喘振多远"，
                                    // 但真机先喘的是前级。级间负荷斜面（前级 φ 低、后级 φ 高）
                                    // 随转速下降变陡，可调静子把它压掉约 2/3（§212）。
                                    double[] stageSm = chart.StageSurgeMargins(op, spec.Stages, spec.Vigv);
                                    if (stageSm.Length > 0)
                                    {
                                        int worst = 0;
                                        for (int si = 1; si < stageSm.Length; si++)
                                            if (stageSm[si] < stageSm[worst]) worst = si;
                                        if (low) { res.LpcStageSurgeMargin = stageSm[worst]; res.LpcStageSurgeStage = worst + 1; }
                                        else { res.HpcStageSurgeMargin = stageSm[worst]; res.HpcStageSurgeStage = worst + 1; }

                                        // 会进判据栏（Criteria 把 sol.Warnings 逐条转成 solver.warn 结论），
                                        // 所以这句话必须双语；纯日志才留中文。
                                        if (stageSm[worst] < 3.0 && !sol.Warnings.Exists(delegate (string w) { return w.StartsWith(Loc.T(Loc.T("级间负荷不均", "stage load uneven"), "Uneven stage loading")); }))
                                            sol.Warn(string.Format(
                                                Loc.T(Loc.T("级间负荷不均：{0}第 {1} 级只剩 {2:F1}% 喘振余量（整机 {3:F0}%）。前级在前、后级偏堵塞，这是可调静子与级间放气要解决的问题", "Uneven stage load: stage {1} of the {0} is down to {2:F1}% surge margin (engine {3:F0}%). Front stages run forward, rear stages sit near choke — that is what variable stators and interstage bleed are for"),
                                                    "Uneven stage loading: {0} stage {1} has only {2:F1}% surge margin of its own (whole compressor {3:F0}%). Front stages lead and rear stages run near choke, which is what variable stators and interstage bleed are for"),
                                                low ? Loc.T(Loc.T("低压压气机", "LPC"), "low-pressure compressor") : Loc.T(Loc.T("高压压气机", "HPC"), "high-pressure compressor"),
                                                worst + 1, stageSm[worst],
                                                low ? res.LpcSurgeMargin : res.HpcSurgeMargin));
                                    }
                                    if (chart.Choked(op))
                                    {
                                        if (low) res.LpcChoked = true; else res.HpcChoked = true;
                                        sol.Warn(string.Format(
                                            Loc.T("{0}越过堵塞边界：换算流量 {1:F2} kg/s 超过 Nc {2:F2} 这条线能过的 {3:F2}（多 {4:F0}%），再挤只有流量没有压比", "{0} past the choke boundary: converted flow {1:F2} kg/s exceeds the {3:F2} that the Nc {2:F2} line can pass (by {4:F0}%); squeezing further adds flow without pressure ratio"),
                                            low ? Loc.T("低压压气机", "LPC") : Loc.T("压气机", "compressor"), wc, nc,
                                            chart.ChokeAt(nc).Wc, -cm));

                                        // 越界了却不能把流量压回去——原因见上面 INTAKE 分支那段注释与 §173：
                                        // 部件各自从自己的局部流量重建状态，网络层改流量改不到气路。
                                        // 这里只把话说清楚，不假装限流。触发条件实测：**低转速时
                                        // 会触发**（吞气上限取 `LegacyMassFlow × 转速`，而堵塞线的
                                        // 换算流量不经过原点，低转速掉得比转速快，RD-33 在 50% 转速
                                        // 时余量 −29.5%）；1.0 转速下把冲压顶到三倍反而碰不到，那时
                                        // wc 与 Nc 同比例走，图上位置一动不动。两条不矛盾，问的不是
                                        // 同一件事。
                                    }
                                    // 判据只管"离喘振线够不够远"，负裕度是**已经越过**，
                                    // 那比"快到了"更该报。`-1` 是"读数不可用"的记号（图没建起来），
                                    // 用它当"越过喘振线"会把没读数说成有读数，所以先把它排掉。
                                    if (Math.Abs(sm + 1.0) > 1e-6 && sm < 15.0)
                                    {
                                        if (low) res.LpcSurgeRisk = true; else res.HpcSurgeRisk = true;
                                        sol.Warn(string.Format(
                                            Loc.T("{0}贴近喘振：换算转速 {1:F2} 的工作点离喘振线只剩 {2:F0}% 裕度（换算流量 {3:F2} kg/s）", "{0} close to surge: at Nc {1:F2} the operating point has only {2:F0}% margin to the surge line (converted flow {3:F2} kg/s)"),
                                            low ? Loc.T("低压压气机", "LPC") : Loc.T("压气机", "compressor"), nc, sm, wc));

                                    }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // 以前这里是空的 catch：文案里少一个参数引发的 FormatException
                                // 会被它吞掉，紧跟其后的喘振风险标记也不执行，日志一个字没有
                                // （§167 踩过，排查方向被带偏一整轮）。现在把异常本身说出来。
                                sol.Warn(string.Format(
                                    Loc.T("{0}特性图那一步没算成（{1}：{2}），这个工况的喘振/堵塞裕度是空的", "{0} map step failed ({1}: {2}); surge and choke margins are blank at this operating point"),
                                    low ? Loc.T("低压", "low pressure") : Loc.T("高压", "high pressure"), ex.GetType().Name, ex.Message));
                            }
                        }

                        // ── 中间级放气：已经由压气机自己在级间扣掉了 ──────────────
                        // 位置很关键：特性图那一步用的是**放气前**的流量——前排级看到的就是
                        // 进气道送进来的全部空气，φ 因此变大、工作点离喘振线更远。
                        // 抽气发生在级间（`CompressorComponent` 的第 bleedStage 级之后），
                        // 所以后面几级的压缩功是按剩下的流量算的，这里**不能再扣一次质量**：
                        // 以前两头都扣，等于把那份空气的压缩功算了又白拿走一份流量。
                        if (!low)
                        {
                            double bf = r.Get("bleed_frac");
                            if (bf > 1e-9)
                            {
                                res.HpcBleedFraction = bf;
                                res.HpcBleedFlow = r.Get("bleed_flow");
                            }
                        }
                        break;
                    }

                    case "BURNER":
                    {
                        // 压气机出口 → 燃烧室入口：核心机里最细最长的一段。
                        // 放在这里而不是压气机分支里，是为了让 HpcExit 站保持"压气机出口"的原义——
                        // OPR 是拿 HpcExit/IntakeExit 两个站算的，先扣管道损失会把 OPR 也扣小，
                        // 判据那边读到的"总增压比"就不再是压气机的了。
                        double cl, cd, ca;
                        DuctLoss.Core(d, out cl, out cd, out ca);
                        res.DuctCoreLoss = DuctLoss.Apply(cur, cl, cd, ca);
                        BurnerComponent c = new BurnerComponent();
                        ComponentResult r = c.Solve(cur, ctx);
                        // 燃烧室失败仍带出口，接着往下算（老 Cycle 同样继续）
                        Absorb(sol, n, r, ref res);
                        if (r.Outlet == null) return sol;
                        cur = r.Outlet;
                        far = r.Get("far");
                        fuelMain = r.FuelFlow;
                        res.T4 = r.Get("t4", cur.TotalTemperature);
                        res.Far = far;
                        res.Ok = res.Ok && r.Success;

                        // 轴功需求 vs 驱动：这一步必须在涡轮之前做完，
                        // 否则"轴上有负载没涡轮"会一路算到喷管才露出怪数。
                        CheckShaftDemand(d, ctx, res);
                        demandChecked = true;
                        break;
                    }

                    case "HPT":
                    case "IPT":
                    case "LPT":
                    {
                        TurbineSpec spec = n.Part == "HPT" ? d.Hpt : (n.Part == "IPT" ? d.Ipt : d.Lpt);
                        if (spec == null || spec.Stages <= 0) { cur = cur.Clone(); break; }
                        TurbineComponent c = new TurbineComponent(spec, n.Part, n.OutletStation);
                        ComponentResult r = c.Solve(cur, ctx);
                        if (!Absorb(sol, n, r, ref res)) return sol;
                        cur = r.Outlet;
                        res.Opr = sol.Station(Stations.HpcExit) != null && sol.Station(Stations.IntakeExit) != null
                            ? sol.Station(Stations.HpcExit).TotalPressure / sol.Station(Stations.IntakeExit).TotalPressure
                            : res.Opr;
                        if (n.Part == "HPT") { res.PiHpt = r.PressureRatio; res.WTurbHpt = r.PowerProduced; }
                        else if (n.Part == "IPT") { res.PiIpt = r.PressureRatio; res.WTurbIpt = r.PowerProduced; }
                        else { res.PiLpt = r.PressureRatio; res.WTurbLpt = r.PowerProduced; }
                        res.T5 = cur.TotalTemperature;
                        res.P5 = cur.TotalPressure;
                        // 涡轮后总温就是喷管进口温度；开加力时 AFTERBURNER 分支会覆盖它。
                        // 必须写：DeriveDesignConstants 拿 T7 反解干态喉道 A8 与喷管面积比，
                        // 涡喷/未开加力时 T7 恒 0 → A8=0、面积比卡在 1.0，喷管型式这一维就废了。
                        res.T7 = cur.TotalTemperature;
                        break;
                    }

                    case "AFTERBURNER":
                    {
                        if (d.Afterburner.Enabled && d.Nozzle == NozzleKind.Converging)
                            res.Warnings.Add(Loc.T("配了加力却用固定收敛喷管：加力后落压比升到 5–6，欠膨胀损失会吃掉不少推力，真实加力机都用可变面积喷管",
                                "Afterburner with a fixed convergent nozzle: with the burner lit the pressure ratio climbs to 5-6, "
                              + "and underexpansion eats a good share of the thrust; real afterburning engines all use a variable-area nozzle"));
                        AfterburnerComponent c = new AfterburnerComponent();
                        ComponentResult r = c.Solve(cur, ctx);
                        if (!Absorb(sol, n, r, ref res)) return sol;
                        cur = r.Outlet;
                        farAb = r.Get("far_ab");
                        fuelAb = r.FuelFlow;
                        res.T7 = r.Get("t7", cur.TotalTemperature);
                        res.FarAb = farAb;
                        break;
                    }

                    case "NOZZLE":
                    {
                        // 涡轮/加力出口 → 喷管喉道：排气锥那一截。长度同样按整机长度走，
                        // 所以"把发动机拉长"在这里第二次吃到亏（第一次在进气段）。
                        double el, ed;
                        DuctLoss.Exhaust(d, out el, out ed);
                        res.DuctExhaustLoss = DuctLoss.Apply(cur, el, ed);
                        pExit = cur.TotalPressure;
                        NozzleComponent c = new NozzleComponent(false);
                        ComponentResult r = c.Solve(cur, ctx);
                        if (!Absorb(sol, n, r, ref res)) return sol;
                        cur = r.Outlet;
                        grossCore = r.Get("gross");
                        veCore = r.Get("ve");
                        hasCoreNozzle = true;
                        if (d.Nozzle == NozzleKind.ConvergingDiverging)
                        {
                            res.NozzleAreaRatio = r.Get("area_ratio", 1.0);
                            res.NozzleMachE = r.Get("mach_e");
                        }
                        break;
                    }

                    default:
                        // 未知部件：不静默跳过（静默跳过等于悄悄少算一段物理）
                        res.Warnings.Add(string.Format(Loc.T("模板里有未知部件 {0}，已跳过", "unknown component {0} in the template, skipped"), n.Part));
                        break;
                }

                if (n.OutletStation >= 0 && cur != null) sol.StationTable[n.OutletStation] = cur.Clone();
            }

            if (!demandChecked) CheckShaftDemand(d, ctx, res);

            // ---- 压气机总压比/出口状态（与老 Cycle 同一口径）----
            GasState st1 = sol.Station(Stations.IntakeExit);
            GasState st3 = sol.Station(Stations.HpcExit) ?? sol.Station(Stations.LpcExit);
            if (st1 != null && st3 != null)
            {
                res.Opr = st3.TotalPressure / st1.TotalPressure;
                res.T3 = st3.TotalTemperature;
                res.P3 = st3.TotalPressure;
            }
            if (d.Hpc.Stages == 0 && d.Lpc.Stages == 0)
            {
                res.Errors.Add(Loc.T("没有任何压气机级：这只是一台冲压发动机，不是涡喷/涡扇", "No compressor stages at all: this is a ramjet, not a turbojet or turbofan"));
                res.Ok = false;
            }

            // ---- 外涵喷管（只有涡扇才有）----
            if (bypass != null && sol.Path.Find("BYPASS_NOZZLE") != null)
            {
                SolverContext bctx = ctx;
                NozzleComponent bc = new NozzleComponent(true);
                // 口径：外涵喷管进口总压 = p0 × fpr × tpr —— 与老 Cycle 逐位相同
                // （老代码注释原话："fpr*tpr 是压比，要乘 p0 才是指定压"）。
                // 更"物理"的做法是直接用风扇出口总压 pt2（含冲压压升），但那会抬高全包线巡航推力，
                // 属于改物理而不是搬架构，留到进气道子系统那一轮统一改、统一标定。
                GasState bypIn = GasState.FromTotal(Stations.BypassExit,
                                                    p0 * res.Fpr * Intake.Recovery(d.Intake, tech, machUse),
                                                    res.T2, bypass.MassFlow, FlowRole.Bypass);
                // 外涵道是整台机器里最长的一条管道（风扇出口一路绕到喷口）。
                double bl, bd;
                DuctLoss.Bypass(d, out bl, out bd);
                res.DuctBypassLoss = DuctLoss.Apply(bypIn, bl, bd);
                ComponentResult br = bc.Solve(bypIn, bctx);
                Absorb(sol, sol.Path.Find("BYPASS_NOZZLE"), br, ref res);
                sol.StationTable[Stations.BypassNozzleExit] = br.Outlet;
                grossByp = br.Get("gross");
                veByp = br.Get("ve");
            }

            // ---- 推力合成（与老 Cycle 逐项相同）----
            double fCore = grossCore - mdotCore * v0;
            double fByp = grossByp - mdotByp * v0;
            res.VJetCore = veCore;
            res.VJetBypass = veByp;
            res.ThrustCore = fCore;
            res.ThrustBypass = fByp;
            res.Thrust = fCore + fByp;
            res.WComp = wLpc + wHpc;
            res.WFan = wFan;
            res.WLpc = wLpc;
            res.WHpc = wHpc;

            if (p0 > 0.0 && pExit / p0 < 1.0)
            {
                res.Errors.Add(Loc.T("涡轮出口总压低于环境压力：喷管会倒流，设计不可行", "Turbine exit total pressure is below ambient: the nozzle would reverse flow, the design is not flyable"));
                res.Ok = false;
            }
            if (res.Thrust <= 0.0 && hasCoreNozzle)
            {
                res.Errors.Add(string.Format(
                    Loc.T("设计点净推力 {0:F1} kN ≤ 0：排气速度比来流还慢，这组参数飞不动（喷口 Vj={1:F0} m/s，来流 {2:F0} m/s）", "Design-point net thrust {0:F1} kN <= 0: the exhaust is slower than the incoming stream, these numbers cannot fly (Vj={1:F0} m/s vs {2:F0} m/s incoming)"),
                    res.Thrust / 1000.0, veCore, v0));
                res.Ok = false;
            }

            double fuelTotal = fuelMain + fuelAb;
            res.FuelFlow = fuelTotal;
            res.Sfc = fuelTotal * 3600.0 / Math.Max(res.Thrust, 1e-9);
            res.FnPerMdot = res.Thrust / Math.Max(mdotAir, 1e-9);
            res.SelfSustaining = res.Ok && res.T4 <= tech.T4Limit && res.Thrust > 0.0;

            Dictionary<int, double> pct = new Dictionary<int, double>();
            foreach (KeyValuePair<int, double> kv in ctx.RpmDesign) pct[kv.Key] = 100.0;
            res.Spool = Cycle.SpoolReadout(d, ctx.RpmDesign, pct);

            sol.Complete = true;

            // 几何接口校核：热力学那边有站表兜着，几何这边一直没人管。
            // 这里只做"报出来"，不改任何状态——面积跳变不一定是错（燃烧室本来就要扩压），
            // 但轮毂比≥1、直径≤0 这种数据坏了必须有人知道。
            try
            {
                List<PortLink> ports = Ports.Chain(d);
                sol.Ports = ports;
                string pm;
                int bad = Ports.Problems(ports, out pm);
                if (bad > 0) sol.Warn(Loc.T("截面接口：", "Section interfaces: ") + pm);
                int loose = Ports.Loose(ports, Ports.DefaultTolerancePct);
                if (loose > 0)
                    sol.Warn(string.Format(Loc.T("截面接口：{0} 处面积跳变超过 {1:F0}%（几何上该看一眼，不是算错）", "Section interfaces: {0} places where the area jumps by more than {1:F0}% (worth a look at the geometry, not a calculation error)"), loose, Ports.DefaultTolerancePct));
            }
            catch (Exception e)
            {
                sol.Warn(string.Format(Loc.T("截面接口校核异常：{0}", "Section interface check failed: {0}"), e.Message));
            }

            // 压气机特性图：热力学只给了一个设计点，而"这个点离喘振线多远"必须看图。
            // 图是从这台机器自己的设计点生成的（见 CompressorChart 的说明——坐标是
            // 相对流量系数，设计点恒在 f=1），所以它与几何误差脱钩；
            // 但**不要**拿它跟判据里的 φ/φ_失速 混比：那两个问题不是同一把尺。
            try
            {
                GasState hpcIn = null, hpcOut = null;
                sol.StationTable.TryGetValue(Stations.HpcExit, out hpcOut);
                // 涡扇：LPC 出口；涡喷：进气口直接进压气机（站表里根本没有 LPC/FAN 那两站）。
                if (!sol.StationTable.TryGetValue(Stations.LpcExit, out hpcIn)
                    && !sol.StationTable.TryGetValue(Stations.FanExit, out hpcIn))
                    sol.StationTable.TryGetValue(Stations.IntakeExit, out hpcIn);
                sol.Chart = CompressorChart.DesignChart(d, false, tech);
                // 顺手挂到总账上：模块侧只拿得到 CycleResult（它没有站表），
                // 面板要显示"裕度多少"就得靠这个引用，而不是在 UI 里重算一遍图。
                if (sol.Result != null) sol.Result.Chart = sol.Chart;
            }
            catch (Exception e)
            {
                sol.Warn(string.Format(Loc.T("特性图异常：{0}", "Compressor map error: {0}"), e.Message));
            }

            return sol;
        }

        /// <summary>
        /// 把部件产物并进总账：站表、站号、软错误、告警、逐级表、尺度修正。
        /// 返回 false = 硬失败（没有出口，链路断了）。
        /// </summary>
        private static bool Absorb(NetworkSolution sol, PathNode n, ComponentResult r, ref CycleResult res)
        {
            if (n != null && !string.IsNullOrEmpty(n.Part))
                sol.Parts[n.Part] = r;

            foreach (string w in r.Warnings) res.Warnings.Add(w);
            foreach (string e in r.Errors) { res.Errors.Add(e); res.Ok = false; }
            if (!r.Success)
            {
                res.Errors.Add(r.FailureCode + ": " + r.FailureMessage);
                res.Ok = false;
            }
            foreach (KeyValuePair<string, ScaleInfo> kv in r.Scale) res.Scale[kv.Key] = kv.Value;
            foreach (StageRow s in r.Stages) res.Stages.Add(s);

            if (r.Outlet != null && n != null && n.OutletStation >= 0)
                sol.StationTable[n.OutletStation] = r.Outlet;

            // 出口状态合不合格，在**这里**统一查一次。
            // 放到各个部件里去查，总有一个部件会忘——而忘了的那个正好是会把 0 压力
            // 一路传下去、最后让"推力 0"看着像设计问题的地方。
            if (r.Outlet != null)
            {
                string why;
                GasValidity v = r.Outlet.Validate(out why);
                if (v != GasValidity.Valid)
                {
                    string who = n != null && !string.IsNullOrEmpty(n.Part) ? n.Part
                             : (n != null ? n.Kind.ToString() : "部件");
                    sol.Warn(string.Format(Loc.T("{0} 出口状态不合格（{1}）：{2}", "{0} exit state invalid ({1}): {2}"), who, v, why));
                }
            }

            return r.Outlet != null;
        }

        private static void CheckShaftDemand(EngineDesignData d, SolverContext ctx, CycleResult res)
        {
            HashSet<int> driven = new HashSet<int>();
            if (d.Hpt != null && d.Hpt.Stages > 0) driven.Add(d.Hpt.Shaft);
            if (d.Ipt != null && d.Ipt.Stages > 0) driven.Add(d.Ipt.Shaft);
            if (d.Lpt != null && d.Lpt.Stages > 0) driven.Add(d.Lpt.Shaft);

            foreach (KeyValuePair<int, double> kv in ctx.ShaftDemand)
            {
                if (kv.Value > 1.0 && !driven.Contains(kv.Key))
                {
                    res.Errors.Add(string.Format(
                        Loc.T("轴 {0} 上有 {1:F2} MW 的压气机负载，但没有涡轮驱动它 → 转不起来", "Shaft {0} carries {1:F2} MW of compressor load but no turbine drives it, so it cannot spin up"),
                        kv.Key, kv.Value / 1e6));
                    res.Ok = false;
                }
            }
        }
    }
}