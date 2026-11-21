using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    // ==========================================================================
    // 部件求解器。每个部件只干一件事：吃一个 GasState，吐一个 GasState，
    // 外加"我吃了多少轴功 / 放了多少热 / 我把气流加了多少温"。
    //
    // 这里的公式**逐行搬自 Cycle.SolveDesignPoint**——设计点数值必须一分不差，
    // 否则这一整轮重构就只是把 671 行的函数拆成了 8 个 671 行的函数。
    // 新物理（进气道三面积、逐轴转速、流量匹配）以后只能**新增**路径，
    // 不许顺手改这里的算式。
    // ==========================================================================

    /// <summary>进气道。输出站 1 的气流（总压恢复后），并把三面积/限流/溢流一起报出来。</summary>
    public sealed class IntakeComponent : EngineComponentBase
    {
        /// <summary>最近一次求解的完整结果（界面与诊断直接读它，不用再求一遍）。</summary>
        public IntakeSolution Last;

        /// <summary>
        /// 装在上游的独立进气道（没有就 null）。给了它，进气道这一段就不看整机设计里的
        /// IntakeSpec，改吃零件合计出来的那份解——这是"独立部件但仍有联系"的接线点。
        /// </summary>
        public IntakeSolution Installed;

        public IntakeComponent()
        {
            ComponentId = "INTAKE";
            Kind = ComponentKind.Intake;
            Port(new ComponentPort("ram", PortRole.Inlet, FlowRole.Core, Stations.Ambient));
            Port(new ComponentPort("face", PortRole.Outlet, FlowRole.Core, Stations.IntakeExit));
        }

        public override ComponentResult Solve(GasState inlet, SolverContext ctx)
        {
            IntakeSolution s = Installed != null
                ? Installed
                : IntakeSolver.Solve(ctx.Design, ctx.Mach, 0.0, ctx.Altitude);
            Last = s;
            if (!s.Valid)
                return ComponentResult.Failed(s.FailureCode, s.Describe());

            ComponentResult r = new ComponentResult();
            if (!GuardInlet(inlet, r)) return r;
            foreach (string w in s.Warnings) r.Warn(w);

            // 设计点走老口径（喉道 + 来流总压），与改造前逐位相同。
            double mdot = s.LegacyMassFlow;
            GasState o = GasState.FromTotal(Stations.IntakeExit, s.Pt2, s.Tt2, mdot, FlowRole.Core);
            o.Mach = s.FaceMach;
            o.SolveStaticFromArea(s.FaceArea);
            r.Outlets.Add(o);

            r.PressureRatio = s.Tpr;
            r.TemperatureRise = 0.0;
            r.Put("tpr", s.Tpr);
            r.Put("face_mach", s.FaceMach);
            r.Put("capture_area", s.CaptureArea);
            r.Put("throat_area", s.ThroatArea);
            r.Put("face_area", s.FaceArea);
            r.Put("captured", s.CapturedMassFlow);
            r.Put("available", s.AvailableMassFlow);
            r.Put("spillage", s.SpillageMassFlow);
            r.Put("inlet_drag", s.InletDrag);
            r.Put("distortion", s.DistortionIndex);
            r.Put("legacy_mdot", s.LegacyMassFlow);
            return r;
        }

        public override void Validate(EngineDesignData d, List<string> errors)
        {
            if (d.Intake == null) return;
            IntakeSpec sp = d.Intake.Resolved();
            if (sp.ThroatRatio < 0.35 || sp.ThroatRatio > 1.20)
                errors.Add(string.Format(Loc.T("进气道喉道比 {0:F2} 超出可行范围 0.35~1.20", "Intake throat ratio {0:F2} is outside the feasible 0.35-1.20"), sp.ThroatRatio));
            if (sp.BleedFrac < 0.0 || sp.BleedFrac > 0.25)
                errors.Add(string.Format(Loc.T("进气道抽气比例 {0:P0} 超出 0~25%", "Intake bleed fraction {0:P0} is outside 0-25%"), sp.BleedFrac));
            if (sp.DuctBendDeg < 0.0 || sp.DuctBendDeg > 60.0)
                errors.Add(string.Format(Loc.T("进气道管道弯曲 {0:F0}° 超出 0~60°", "Intake duct bend {0:F0} deg is outside 0-60 deg"), sp.DuctBendDeg));
        }
    }

    /// <summary>风扇/第一级压气机（外涵那台）。没有风扇时退化为直通，fpr=1。</summary>
    public sealed class FanComponent : EngineComponentBase
    {
        public FanComponent()
        {
            ComponentId = "FAN";
            Kind = ComponentKind.Fan;
            Port(new ComponentPort("core_in", PortRole.Inlet, FlowRole.Core, Stations.IntakeExit));
            Port(new ComponentPort("core_out", PortRole.Outlet, FlowRole.Core, Stations.FanExit));
        }

        public override ComponentResult Solve(GasState inlet, SolverContext ctx)
        {
            EngineDesignData d = ctx.Design;
            TechLevel tech = ctx.Tech;
            ComponentResult r = new ComponentResult();
            if (!GuardInlet(inlet, r)) return r;

            double tt1 = inlet.TotalTemperature, pt1 = inlet.TotalPressure;
            double fpr = 1.0, etaFan = tech.FanEtaPol;
            double gear = 1.0, gearEta = 1.0;
            if (d.HasFan)
            {
                gear = d.Fan.GearRatio > 1.0 ? d.Fan.GearRatio : 1.0;
                gearEta = d.Fan.GearEta > 0.0 ? Math.Min(Math.Max(d.Fan.GearEta, 0.90), 1.0) : 0.995;
                if (gear <= 1.0001) gearEta = 1.0;
                // 齿轮减速：风扇叶尖速度按"轴转速 / 齿轮比"算。GTF 的意义全在这一行——
                // 低压涡轮按它喜欢的 9000 rpm 转，风扇按它喜欢的 3000 rpm 转。
                double rpm = ctx.ShaftRpm(d.Fan.Shaft) / gear;
                double u = Cycle.TipSpeed(d.Fan.Diameter, rpm);
                ScaleInfo si;
                etaFan = Cycle.ScaleEta(d.Fan.Diameter, d.Fan.HubTipRatio, u, tt1, pt1,
                                        tech.FanEtaPol, tech, 0.80, out si);
                if (si != null) r.Scale["FAN"] = si;
                double piF = Math.Pow(Cycle.StagePi(u, tt1, etaFan, tech.FanPsi), d.Fan.Stages);
                fpr = d.Fan.PressureRatio > 0.0 ? d.Fan.PressureRatio : piF;
                r.Put("fan_rpm", rpm);
                r.Put("fan_tip", u);
                r.Put("gear", gear);
                r.Put("gear_eta", gearEta);
            }

            double g1 = Gas.Gamma(tt1);
            double tt2 = tt1 + (Math.Pow(fpr, (g1 - 1.0) / g1) - 1.0) * tt1 / etaFan;
            double pt2 = pt1 * fpr;

            GasState o = GasState.FromTotal(Stations.FanExit, pt2, tt2, inlet.MassFlow, FlowRole.Core);
            r.Outlets.Add(o);
            r.PressureRatio = fpr;
            r.TemperatureRise = tt2 - tt1;

            if (d.HasFan && fpr > 1.0001)
                r.PowerRequired = inlet.MassFlow * Gas.Cp(tt2) * (tt2 - tt1) / (tech.MechEfficiency * gearEta);

            r.Put("fpr", fpr);
            r.Put("eta", etaFan);
            r.Put("work", r.PowerRequired);
            return r;
        }
    }

    /// <summary>轴流/离心压气机。逐级算，级数 0 时直通且不耗功。</summary>
    public sealed class CompressorComponent : EngineComponentBase
    {
        private readonly CompressorSpec _spec;
        private readonly string _name;      // "LPC" / "HPC"
        private readonly int _outStation;
        private readonly bool _lowPressure;

        public CompressorComponent(CompressorSpec spec, string name, int outStation, bool lowPressure)
        {
            _spec = spec; _name = name; _outStation = outStation; _lowPressure = lowPressure;
            ComponentId = name;
            Kind = ComponentKind.Compressor;
            Port(new ComponentPort("in", PortRole.Inlet, FlowRole.Core, -1));
            Port(new ComponentPort("out", PortRole.Outlet, FlowRole.Core, outStation));
        }

        /// <summary>
        /// 这一刻该从中间级放掉多少比例。
        ///
        /// 真机这么做是因为低转速时前面几级的流量已经快掉到失速边界，
        /// 而后面几级还在按原来的角度咬气——把那一段抽走一截，前面几级就能继续喘气。
        /// 所以这条曲线是"低转速才开、开到底也才一成左右"，设计点一分不放。
        /// </summary>
        public static double BleedOf(CompressorSpec spec, double nc)
        {
            if (spec == null || !spec.Bleed) return 0.0;
            if (spec.Stages < 4 || spec.Kind != CompressorKind.Axial) return 0.0;
            if (spec.BleedFrac > 1e-6) return spec.BleedFrac > 0.25 ? 0.25 : spec.BleedFrac;
            if (nc >= 0.92) return 0.0;
            double b = 0.10 * (0.92 - nc) / 0.30;
            if (b > 0.12) b = 0.12;
            return b < 0.0 ? 0.0 : b;
        }

        public override ComponentResult Solve(GasState inlet, SolverContext ctx)
        {
            EngineDesignData d = ctx.Design;
            TechLevel tech = ctx.Tech;
            ComponentResult r = new ComponentResult();
            if (!GuardInlet(inlet, r)) return r;
            double pt = inlet.TotalPressure, tt = inlet.TotalTemperature;
            double tIn = tt;
            double mdot = inlet.MassFlow;
            double etaPol = (_lowPressure ? tech.LpcEtaPol : tech.HpcEtaPol)
                            + CompressorPhysics.EtaDelta(_spec.Kind);
            double bleed = 0.0;

            if (_spec.Stages > 0)
            {
                double rpm = ctx.ShaftRpm(_spec.Shaft);
                double u = Cycle.TipSpeed(_spec.MaxTip(), rpm);
                ScaleInfo si;
                etaPol = Cycle.ScaleEta(_spec.MaxTip(), _spec.HubTipRatio, u, tt, pt,
                                        etaPol, tech, 0.55, out si);
                if (si != null) r.Scale[_name] = si;

                // 可调静子把冲角掰回设计值，代价是静子作动与叶片端隙：
                // 每偏离设计转速 10%，等熵效率再掉约 0.2 个百分点。设计点一分不扣
                // （`frac = 1` 时整项为 0），所以五台真机预设的读数不会被这条改动碰到。
                double frac = ctx.SpeedFrac > 1e-6 ? ctx.SpeedFrac : 1.0;
                if (frac < 0.999 && _spec.Vigv && _spec.Stages >= 4 && _spec.Kind == CompressorKind.Axial)
                    etaPol *= 1.0 - 0.02 * (1.0 - frac);

                // 中间级放气带来的那点代价（级间腔抽气扰流）。
                // 大头不在压气机自己身上，而在下游：核心只剩 (1 − b) 的流量。
                bleed = BleedOf(_spec, frac);
                if (bleed > 0.0) etaPol *= 1.0 - 0.01 * bleed;
            }

            // 从第几级后面抽：真机多在四五级之后（前几级要尽力吞气，后几级才需要放）。
            // 级数少的时候至少留一级在后面，否则等于在进口抽气。
            // 只给高压压气机抽：低压级抽气的孔在真机上也存在，但这五台预设是拿
            // "只从高压抽" 标定出来的，一改整批推力就要重标（这里不是物理问题，是校准问题）。
            int bleedStage = bleed > 0.0 && !_lowPressure && _spec.Stages >= 2 ? _spec.Stages / 2 : 0;
            double wSum = 0.0;

            for (int i = 0; i < _spec.Stages; i++)
            {
                double rpm = ctx.ShaftRpm(_spec.Shaft);
                double u = Cycle.TipSpeed(_spec.TipOf(i), rpm);
                double psi = tech.CompressorPsi * _spec.PsiOf(i) * CompressorPhysics.PsiFactor(_spec.Kind);
                double piS = Cycle.StagePi(u, tt, etaPol, psi);
                double tPrev = tt;
                tt = tPrev + Cycle.StageDTComp(u, tPrev, psi);
                // 功 = ∫ mdot·Cp(T)·dT。按级取进口/出口温度的中点比热（中点法），
                // 而不是拿压气机出口那一级的高温比热去乘总温升——后者等于把每一级
                // 的功都按最热的那一级算，整台机虚高百分之几。
                wSum += mdot * 0.5 * (Gas.Cp(tPrev) + Gas.Cp(tt)) * (tt - tPrev) / tech.MechEfficiency;
                r.Stages.Add(new StageRow
                {
                    Part = _name, Index = i + 1, Pi = piS, TIn = tPrev, TOut = tt, PIn = pt, MassFlow = mdot
                });
                pt *= piS;
                // 抽气在**这一级之后**发生：这一级的功用的是抽气前的流量，
                // 后面每一级都少这一份。以前是先按全流量压完再扣质量，
                // 于是被抽走的那份空气也被算了一份压缩功。
                if (bleedStage > 0 && i + 1 == bleedStage)
                {
                    double dumped = mdot * bleed;
                    mdot -= dumped;
                    r.Put("bleed_flow", dumped);
                    r.Put("bleed_stage", bleedStage);
                    r.Put("bleed_frac", bleed);
                }
            }

            GasState o = GasState.FromTotal(_outStation, pt, tt, mdot, inlet.Role);
            r.Outlets.Add(o);
            r.PressureRatio = inlet.TotalPressure > 1e-9 ? pt / inlet.TotalPressure : 1.0;
            r.TemperatureRise = tt - tIn;
            r.PowerRequired = wSum;
            r.Put("opr", r.PressureRatio);
            r.Put("work", r.PowerRequired);
            return r;
        }

        public override void Validate(EngineDesignData d, List<string> errors)
        {
            if (_spec.Stages < 0) errors.Add(_name + Loc.T(" 级数为负", " has a negative stage count"));
            if (_spec.Stages > 20) errors.Add(_name + Loc.T(" 级数超过 20（不是发动机，是一台风机）", " has more than 20 stages (that is a fan, not an engine)"));
        }
    }

    /// <summary>风扇出口的分流器：按涵道比把一股气切成核心 + 涵道两股。</summary>
    public sealed class SplitterComponent : EngineComponentBase
    {
        public SplitterComponent()
        {
            ComponentId = "SPLITTER";
            Kind = ComponentKind.Splitter;
            Port(new ComponentPort("in", PortRole.Inlet, FlowRole.Core, Stations.FanExit));
            Port(new ComponentPort("core", PortRole.Outlet, FlowRole.Core, Stations.FanExit));
            Port(new ComponentPort("bypass", PortRole.Outlet, FlowRole.Bypass, Stations.BypassExit, double.NaN));
        }

        public override ComponentResult Solve(GasState inlet, SolverContext ctx)
        {
            ComponentResult r = new ComponentResult();
            if (!GuardInlet(inlet, r)) return r;
            double bpr = Math.Max(ctx.Design.BypassRatio(), 0.0);
            double mdotCore = inlet.MassFlow / (1.0 + bpr);
            double mdotByp = inlet.MassFlow - mdotCore;

            GasState core = GasState.FromTotal(Stations.FanExit, inlet.TotalPressure, inlet.TotalTemperature,
                                               mdotCore, FlowRole.Core);
            GasState byp = GasState.FromTotal(Stations.BypassExit, inlet.TotalPressure, inlet.TotalTemperature,
                                              mdotByp, FlowRole.Bypass);
            r.Outlets.Add(core);
            r.Outlets.Add(byp);
            r.Put("bpr", bpr);
            r.Put("mdot_core", mdotCore);
            r.Put("mdot_bypass", mdotByp);
            // 只有"有气进来、核心却分不到"才是设计坏（涵道比算成无穷大之类）。
            // 进口本来就是 0 流量（关车、风车）时核心流量当然是 0——那不是错，
            // 以前这里一律判失败，于是停车状态的求解会被标成"设计有问题"。
            if (inlet.MassFlow > 0.0 && mdotCore <= 0.0)
                return ComponentResult.Failed("CORE_FLOW_ZERO", Loc.T("核心机流量为零：检查风扇轮毂比 / 涵道比", "Core flow is zero: check the fan hub/tip ratio and the bypass ratio"));
            return r;
        }
    }

    /// <summary>燃烧室：总压损失 + 加热。油气比由能量平衡反解，不是玩家填的。</summary>
    public sealed class BurnerComponent : EngineComponentBase
    {
        public BurnerComponent()
        {
            ComponentId = "BURNER";
            Kind = ComponentKind.Burner;
            Port(new ComponentPort("in", PortRole.Inlet, FlowRole.Core, Stations.HpcExit));
            Port(new ComponentPort("out", PortRole.Outlet, FlowRole.Core, Stations.BurnerExit));
        }

        public override ComponentResult Solve(GasState inlet, SolverContext ctx)
        {
            EngineDesignData d = ctx.Design;
            TechLevel tech = ctx.Tech;
            ComponentResult r = new ComponentResult();
            if (!GuardInlet(inlet, r)) return r;

            Cycle.BurnerPerf bp = Cycle.BurnerPerformance(d.Burner);
            double pt4 = inlet.TotalPressure * bp.Sigma;
            double t4 = Math.Min(d.Burner.ExitTemp, tech.T4Limit);
            if (d.Burner.ExitTemp > tech.T4Limit)
                r.Warn(string.Format(Loc.T("设定 T4={0:F0} K 超过技术上限 {1:F0} K，已按上限", "Requested T4={0:F0} K is above the {1:F0} K technology limit, clamped to the limit"),
                    d.Burner.ExitTemp, tech.T4Limit));

            r.Put("sigma", bp.Sigma);
            r.Put("eta", bp.Eta);
            r.Put("pattern", bp.PatternFactor);
            r.Put("t4", t4);

            if (t4 <= inlet.TotalTemperature)
            {
                ComponentResult bad = ComponentResult.Failed("T4_BELOW_COMPRESSOR",
                    string.Format(Loc.T("T4={0:F0}K 不高于压气机出口 {1:F0}K：无法加热", "T4={0:F0} K is not above the compressor exit {1:F0} K: nothing to heat"), t4, inlet.TotalTemperature));
                foreach (string w in r.Warnings) bad.Warn(w);
                bad.Put("t4", t4);
                bad.Put("far", 0.0);
                // 仍给出口（未加热）——老 Cycle 在报错之后也照样往下算喷管，
                // 设计器要靠这份结果告诉玩家"错在哪一步"，而不是一片空白。
                bad.Outlets.Add(GasState.FromTotal(Stations.BurnerExit, pt4, t4, inlet.MassFlow,
                                                   FlowRole.Core, inlet.FuelAirRatio));
                return bad;
            }

            double cp3 = Gas.Cp(inlet.TotalTemperature), cp4 = Gas.Cp(t4);
            double dT = t4 - inlet.TotalTemperature;
            double den = Gas.LhvJet * bp.Eta - cp4 * dT;
            if (den <= 0.0)
            {
                ComponentResult bad = ComponentResult.Failed("BURNER_ENERGY",
                    Loc.T("燃烧室温升过高，能量不守恒（检查 T4 与 LHV 模型）", "Burner temperature rise is too high and the energy does not balance (check T4 and the LHV model)"));
                foreach (string w in r.Warnings) bad.Warn(w);
                bad.Put("t4", t4);
                bad.Put("far", 0.0);
                bad.Outlets.Add(GasState.FromTotal(Stations.BurnerExit, pt4, t4, inlet.MassFlow,
                                                   FlowRole.Core, inlet.FuelAirRatio));
                return bad;
            }

            double far = cp3 * dT / den;
            if (far > 0.08)
                r.Warn(string.Format(Loc.T("油气比 f={0:F3} 偏高（典型上限 ~0.068），燃烧室可能熄火", "Fuel/air ratio f={0:F3} is high (typical ceiling ~0.068); the burner may blow out"), far));

            double fuel = inlet.MassFlow * far;
            GasState o = GasState.FromTotal(Stations.BurnerExit, pt4, t4, inlet.MassFlow + fuel,
                                            FlowRole.Core, far);
            r.Outlets.Add(o);
            r.PressureRatio = bp.Sigma;
            r.TemperatureRise = dT;
            r.FuelFlow = fuel;
            r.Put("far", far);
            r.Put("fuel", fuel);
            r.Put("mdot_out", o.MassFlow);
            return r;
        }

        public override void Validate(EngineDesignData d, List<string> errors)
        {
            if (d.Burner.ExitTemp <= 0.0) errors.Add(Loc.T("燃烧室出口温度未设定（必须 > 0）", "Burner exit temperature is not set (must be > 0)"));
            if (d.Burner.Length <= 0.0) errors.Add(Loc.T("燃烧室长度必须为正", "Burner length must be positive"));
        }
    }

    /// <summary>
    /// 涡轮。它**不自己决定能发多少功**：轴功需求由轴上的负载（风扇/压气机）给出，
    /// 涡轮只负责"把气流降到能满足这份需求的温度"；降不下来就是级数不足。
    /// 这正是"轴系"存在的地方——`ctx.ShaftDemand[shaft]` 是轴的总账。
    /// </summary>
    public sealed class TurbineComponent : EngineComponentBase
    {
        private readonly TurbineSpec _spec;
        private readonly string _name;   // HPT / IPT / LPT
        private readonly int _outStation;

        public TurbineComponent(TurbineSpec spec, string name, int outStation)
        {
            _spec = spec; _name = name; _outStation = outStation;
            ComponentId = name;
            Kind = ComponentKind.Turbine;
            Port(new ComponentPort("in", PortRole.Inlet, FlowRole.Core, -1));
            Port(new ComponentPort("out", PortRole.Outlet, FlowRole.Core, outStation));
        }

        public override ComponentResult Solve(GasState inlet, SolverContext ctx)
        {
            TechLevel tech = ctx.Tech;
            ComponentResult r = new ComponentResult();
            if (!GuardInlet(inlet, r)) return r;

            if (_spec.Stages <= 0)
            {
                // 没这台涡轮：直通，不取功。网络靠"需/供"表判它该不该存在。
                r.Outlets.Add(GasState.FromTotal(_outStation, inlet.TotalPressure, inlet.TotalTemperature,
                                                 inlet.MassFlow, inlet.Role, inlet.FuelAirRatio));
                r.PressureRatio = 1.0;
                return r;
            }

            double rpm = ctx.ShaftRpm(_spec.Shaft);
            double u = Cycle.TipSpeed(_spec.MaxTip(), rpm);
            double etaT = _name == "LPT" ? tech.LptEtaPol : tech.HptEtaPol;
            ScaleInfo si;
            etaT = Cycle.ScaleEta(_spec.MaxTip(), _spec.HubTipRatio, u,
                                  inlet.TotalTemperature, inlet.TotalPressure, etaT, tech, 1.40, out si);
            if (si != null) r.Scale[_name] = si;

            // 容量两头上限取小：叶尖速度能榨出的 ΔT，和单级落压比对应的 ΔT。
            // 公式在 Cycle.TurbineCapacityDt——非设计点的逐轴功平衡吃的是同一个函数，
            // 两处各写一遍迟早会分叉（管弯损失踩过一次）。
            bool prBound;
            double maxDT = Cycle.TurbineCapacityDt(_spec, tech, rpm, inlet.TotalTemperature, etaT,
                                                   tech.TurbinePsi, out prBound);

            double wDem = ctx.ShaftDemand.ContainsKey(_spec.Shaft) ? ctx.ShaftDemand[_spec.Shaft] : 0.0;
            double tCur = inlet.TotalTemperature, pCur = inlet.TotalPressure;
            double dTNeed = wDem / Math.Max(inlet.MassFlow * Gas.Cp(tCur), 1e-9);

            r.Put("demand", wDem);
            r.Put("capacity", maxDT);
            r.Put("dT_need", dTNeed);
            if (prBound)
            {
                // 把"为什么只能这么多"写清楚：不是叶尖速度不够，是单级压比顶到天花板了。
                r.Put("pr_bound", 1.0);
                r.Warn(string.Format(Loc.T(
                    Loc.T("{0} 的单级落压比顶到 {1:F2} 的极限：这一级的功被压比卡住，不是叶尖速度不够", "{0} is at the {1:F2} per-stage pressure limit: this stage is capped by pressure ratio, not by tip speed"),
                    "{0} is at the {1:F2} per-stage pressure limit: this stage is capped by pressure ratio, not by tip speed"),
                    _name, tech.TurbStagePrMax));
            }

            if (dTNeed > maxDT * 1.02)
            {
                // 报错但**夹到容量上限继续算**：老 Cycle 就是这么干的，
                // 设计器要显示"这台机器大概能到哪儿"，不是显示 NaN。
                r.Fail(string.Format(
                    prBound
                        ? Loc.T("{0} 级数不足（单级落压比已顶到 {2:F2} 的极限）：需要 ΔT={1:F1}K，{3} 级只能提供 {4:F1}K——要更多功得加级",
                                "{0} has too few stages (a single stage is already at the {2:F2} pressure limit): needs dT={1:F1} K, {3} stage(s) give only {4:F1} K - add a stage for more work")
                        : Loc.T("{0} 级数不足：需要 ΔT={1:F1}K，{2} 级只能提供 {3:F1}K",
                                "{0} has too few stages: needs dT={1:F1} K, {2} stage(s) give only {3:F1} K"),
                    _name, dTNeed, tech.TurbStagePrMax, _spec.Stages, maxDT));
                dTNeed = maxDT;
                r.Put("stage_short", 1.0);
            }

            double tNext = tCur - dTNeed;
            double piT = Cycle.PrFromTr(tCur, tNext, etaT);
            double piMax = Math.Pow(tech.TurbStagePrMax, _spec.Stages);
            if (piT > piMax)
            {
                r.Fail(string.Format(
                    Loc.T("{0} 落压比 {1:F2} 超过 {2} 级的极限 {3:F2}", "{0} pressure ratio {1:F2} exceeds the {2}-stage limit {3:F2}"), _name, piT, _spec.Stages, piMax));
            }
            if (tNext < 0.4 * tCur)
                r.Warn(string.Format(Loc.T("{0} 温降过大（{1:F0}→{2:F0}K），涡轮可能超负荷", "{0} temperature drop is too large ({1:F0} to {2:F0} K); the turbine may be overloaded"), _name, tCur, tNext));

            GasState o = GasState.FromTotal(_outStation, pCur / Math.Max(piT, 1e-9), tNext,
                                            inlet.MassFlow, inlet.Role, inlet.FuelAirRatio);
            r.Outlets.Add(o);
            r.PowerProduced = wDem;
            r.PressureRatio = piT;
            r.TemperatureRise = tNext - tCur;
            r.Put("pi", piT);
            r.Put("t_out", tNext);
            return r;
        }

        public override void Validate(EngineDesignData d, List<string> errors)
        {
            if (_spec.Stages > 0 && _spec.Shaft < 1)
                errors.Add(_name + Loc.T(" 没有挂在任何轴上", " is not mounted on any shaft"));
        }
    }

    /// <summary>加力燃烧室。未启用时直通。</summary>
    public sealed class AfterburnerComponent : EngineComponentBase
    {
        public AfterburnerComponent()
        {
            ComponentId = "AFTERBURNER";
            Kind = ComponentKind.Afterburner;
            Port(new ComponentPort("in", PortRole.Inlet, FlowRole.Core, Stations.TurbineExit));
            Port(new ComponentPort("out", PortRole.Outlet, FlowRole.Core, Stations.AfterburnerExit));
        }

        public override ComponentResult Solve(GasState inlet, SolverContext ctx)
        {
            EngineDesignData d = ctx.Design;
            TechLevel tech = ctx.Tech;
            ComponentResult r = new ComponentResult();
            if (!GuardInlet(inlet, r)) return r;
            AfterburnerSpec ab = d.Afterburner;

            if (!ab.Enabled)
            {
                r.Outlets.Add(GasState.FromTotal(Stations.AfterburnerExit, inlet.TotalPressure,
                                                 inlet.TotalTemperature, inlet.MassFlow, inlet.Role,
                                                 inlet.FuelAirRatio));
                r.PressureRatio = 1.0;
                r.Put("far_ab", 0.0);
                return r;
            }

            double etaAb, dpAb;
            Cycle.AbPerformance(ab.Kind, out etaAb, out dpAb);

            double tCur = inlet.TotalTemperature, pCur = inlet.TotalPressure;
            double t7 = tCur, farAb = 0.0;
            if (ab.FuelRatio > 0.0)
            {
                double fAb = Math.Min(ab.FuelRatio, 0.045);
                double added = fAb * Gas.LhvJet * etaAb;
                t7 = tCur + added / Gas.Cp(tCur) / Math.Max(1.0 + fAb, 1e-6);
                t7 = Math.Min(t7, Cycle.AbMaxTemp(tech));
                farAb = fAb;
            }
            else if (ab.ExitTemp > tCur)
            {
                t7 = Math.Min(ab.ExitTemp, Cycle.AbMaxTemp(tech));
                double dT7 = t7 - tCur;
                double den = Gas.LhvJet * etaAb - Gas.Cp(t7) * dT7;
                farAb = den > 0.0 ? Gas.Cp(tCur) * dT7 / den : 0.0;
            }

            double fuelAb = 0.0;
            if (farAb > 0.0)
            {
                fuelAb = inlet.MassFlow * farAb;
                pCur = pCur * (1.0 - dpAb);
            }

            GasState o = GasState.FromTotal(Stations.AfterburnerExit, pCur, t7, inlet.MassFlow + fuelAb,
                                            inlet.Role, inlet.FuelAirRatio + farAb);
            r.Outlets.Add(o);
            r.PressureRatio = (1.0 - dpAb);
            r.TemperatureRise = t7 - tCur;
            r.FuelFlow = fuelAb;
            r.Put("far_ab", farAb);
            r.Put("eta", etaAb);
            r.Put("dp", dpAb);
            r.Put("t7", t7);
            return r;
        }
    }

    /// <summary>喷管。核心与涵道共用：压比不同、落压比不同，公式是同一套。</summary>
    public sealed class NozzleComponent : EngineComponentBase
    {
        private readonly bool _bypass;

        public NozzleComponent(bool bypass)
        {
            _bypass = bypass;
            ComponentId = bypass ? "BYPASS_NOZZLE" : "NOZZLE";
            Kind = ComponentKind.Nozzle;
            Port(new ComponentPort("in", PortRole.Inlet, bypass ? FlowRole.Bypass : FlowRole.Core,
                                   bypass ? Stations.BypassExit : Stations.AfterburnerExit));
            Port(new ComponentPort("out", PortRole.Outlet, bypass ? FlowRole.Bypass : FlowRole.Core,
                                   bypass ? Stations.BypassNozzleExit : Stations.NozzleExit));
        }

        public override ComponentResult Solve(GasState inlet, SolverContext ctx)
        {
            EngineDesignData d = ctx.Design;
            TechLevel tech = ctx.Tech;
            ComponentResult r = new ComponentResult();
            if (!GuardInlet(inlet, r)) return r;

            double pAmb = ctx.AmbientPressure;
            double pt = inlet.TotalPressure, tt = inlet.TotalTemperature;
            double mdot = inlet.MassFlow;
            double ve, ae, pe, gross;
            bool choked;
            double machE = 0.0, areaRatio = 1.0;

            if (!_bypass && d.Nozzle == NozzleKind.ConvergingDiverging)
            {
                areaRatio = Gas.DesignAreaRatio(pt, pAmb, tt);
                double aThroat = mdot / Math.Max(Gas.ChokedFlow(1.0, pt, tt, 0.0), 1e-9);
                Gas.NozzleState ns = Gas.NozzleFlow(mdot, pt, tt, pAmb, tech.NozzleEta,
                                                    NozzleKind.ConvergingDiverging, aThroat, areaRatio);
                ve = ns.Ve; ae = ns.Ae; pe = ns.Pe; gross = ns.Thrust; choked = ns.Choked;
                machE = ns.MachE;
            }
            else
            {
                Gas.Nozzle(mdot, pt, tt, pAmb, tech.NozzleEta,
                           out ve, out ae, out pe, out gross, out choked);
            }

            int st = _bypass ? Stations.BypassNozzleExit : Stations.NozzleExit;
            GasState o = GasState.FromTotal(st, pt, tt, mdot, inlet.Role, inlet.FuelAirRatio);
            o.Mach = machE > 0.0 ? machE : Math.Sqrt(Math.Max(mdot * mdot / Math.Max(pt * pt * ae * ae, 1e-12) - 1.0, 0.0) * 2.0);
            r.Outlets.Add(o);

            r.Put("ve", ve);
            r.Put("ae", ae);
            r.Put("pe", pe);
            r.Put("gross", gross);
            // 推力的两项分开记：动量 ṁ·Ve 是主项，压力 (Pe−Pa)·Ae 只在收扩喷管欠膨胀时才明显。
            // 以前只有 gross 一个数，没法回答"这段推力是气流冲出来的还是背压顶出来的"，
            // 也没法验证 F = ṁVe + (Pe−Pa)Ae 这条式子在每个分支里都没被偷偷改掉。
            double momT = mdot * ve;
            double presT = (pe - pAmb) * ae;
            r.Put("momentum", momT);
            r.Put("pressure", presT);
            r.Put("split_err", Math.Abs(momT + presT - gross));
            r.Put("choked", choked ? 1.0 : 0.0);
            r.Put("mach_e", machE);
            r.Put("area_ratio", areaRatio);
            r.Put("pt", pt);
            return r;
        }
    }

    /// <summary>管道：直通（外涵道）。面积/弯曲以后再挂损失，现在保号。</summary>
    public sealed class DuctComponent : EngineComponentBase
    {
        private readonly int _in, _out;
        private readonly FlowRole _flow;

        public DuctComponent(string id, int stationIn, int stationOut, FlowRole flow)
        {
            ComponentId = id;
            Kind = ComponentKind.Duct;
            _in = stationIn; _out = stationOut; _flow = flow;
            Port(new ComponentPort("in", PortRole.Inlet, flow, stationIn));
            Port(new ComponentPort("out", PortRole.Outlet, flow, stationOut));
        }

        public override ComponentResult Solve(GasState inlet, SolverContext ctx)
        {
            ComponentResult r = new ComponentResult();
            if (!GuardInlet(inlet, r)) return r;
            r.Outlets.Add(GasState.FromTotal(_out, inlet.TotalPressure, inlet.TotalTemperature,
                                             inlet.MassFlow, _flow, inlet.FuelAirRatio));
            r.PressureRatio = 1.0;
            return r;
        }
    }
}