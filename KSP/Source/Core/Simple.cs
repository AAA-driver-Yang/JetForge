using System;
using System.Collections.Generic;
using System.Globalization;

namespace JetForge.Core
{
    /// <summary>
    /// 简单模式的全部输入：没有几何，只有目标。
    /// 与 <see cref="EngineDesignData"/> 的关系是"两档喂同一台机器"——
    /// 简单模式给玩家一个五分钟就能出结果的入口，残酷模式给愿意调级数的人。
    /// </summary>
    public class SimpleDesign
    {
        public const string NodeName = "JETFORGE_SIMPLE";

        public string Name = "simple engine";
        public TechLevel Tech = TechLevel.Get("1970s");
        public EngineKind Kind = EngineKind.Turbofan;
        public double Diameter = 1.2;      // 最大直径 m（涡扇=风扇，涡喷=进气口）
        public double Bpr = 5.0;           // 涵道比（涡喷自动归零）
        public double Opr = 25.0;          // 总增压比
        public double T4 = 1450.0;         // 燃烧室出口温度 K
        public double TExhaust = 0.0;      // 排气温度 K：无加力时作 EGT 上限，有加力时就是 T7
        public double AbFuelRatio = 0.0;   // 加力喷油量 far_ab，>0 即开启加力
        public double DesignMach = 0.0;
        public double DesignAlt = 0.0;
        public double Fpr = 0.0;           // 风扇压比；0 = 由涵道比统计式反推
        public NozzleKind Nozzle = NozzleKind.Converging;
        public double T4Idle = 0.0;        // 慢车 T4；0 = 自动取 55% 设计 T4
        public double AbThrottle = 0.85;   // 加力油门门槛
        public double EtaOffdesignK = 0.50; // 非设计点部件效率折减系数
        /// <summary>
        /// 进气道型式。简单模式没有几何，只有这一个选项；缺省轴对称，与老行为逐位相同。
        /// 想要捕获直径/喉道比/抽气这些细项，得走残酷模式（那边才有 IntakeSpec）。
        /// </summary>
        public IntakeKind IntakeKind = IntakeKind.Axial;
        /// <summary>
        /// 喉道/捕获面积比。**0 = 不按喉道限流**（老行为）。
        /// 简单模式没有独立捕获直径——最大直径就是捕获直径，喉道比是它唯一能表达"进气道会憋住"的旋钮：
        /// 真机在高马赫下喉道壅塞，再想多吞气也吞不进去，靠的就是这个小截面。
        /// </summary>
        public double IntakeThroatRatio = 0.0;
        /// <summary>抽气比例 0~0.15：压气机引气去冷却涡轮，这部分空气不产生推力。</summary>
        public double IntakeBleedFrac = 0.0;
        /// <summary>进气道管道弯曲角 0~60°：每 10° 掉约 0.8% 总压。</summary>
        public double IntakeDuctBendDeg = 0.0;

        /// <summary>把简单模式那几项翻译成一份 IntakeSpec（残酷模式的零件），两边共用同一份物理。</summary>
        public IntakeSpec ToIntakeSpec()
        {
            return new IntakeSpec
            {
                Kind = IntakeKind,
                ThroatRatio = IntakeThroatRatio,
                BleedFrac = IntakeBleedFrac,
                DuctBendDeg = IntakeDuctBendDeg
            };
        }

        public void Normalize()
        {
            if (Kind == EngineKind.Turbojet) Bpr = 0.0;
            if (Bpr < 0.0) Bpr = 0.0;
            if (Opr < 1.0) Opr = 1.0;
        }

        public SimpleDesign Clone()
        {
            return (SimpleDesign)MemberwiseClone();
        }

        public double GetT4Idle()
        {
            return T4Idle > 0.0 ? T4Idle : T4 * 0.55;
        }
    }

    public class SimpleResult
    {
        public bool Ok = true;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();

        public double Alt, Mach, Throttle = 1.0;

        public double Bpr, Opr, Fpr = 1.0, Cpr = 1.0;

        public double N1Pct = 100.0, N2Pct = 100.0;
        /// <summary>简单模式只有一到两根轴，第三根轴不存在，恒为 0（字段留着是为了和完整模式共用表格）。</summary>
        public double N3Pct = 0.0;
        public double N1Rpm, N2Rpm;

        public double MdotAir, MdotCore;
        public double T1, T2, T3, T4, T5, T7;
        public double P3, P5;
        public double Far, FarAb, Egt;

        public double Thrust, ThrustCore, ThrustBypass, RamDrag, NetThrust;
        public double FuelFlow, Sfc;
        public double VJetCore, VJetBypass, FnPerMdot;

        public bool SelfSustaining;
        public bool TurbineLimited;
        public readonly Dictionary<int, double> Spool = new Dictionary<int, double>();

        public ResultView AsView()
        {
            var v = new ResultView();
            v.Ok = Ok;
            v.Num["mdot_air"] = MdotAir; v.Num["mdot_core"] = MdotCore;
            v.Num["bpr"] = Bpr; v.Num["opr"] = Opr; v.Num["fpr"] = Fpr;
            v.Num["T1"] = T1; v.Num["T2"] = T2; v.Num["T3"] = T3;
            v.Num["T4"] = T4; v.Num["T5"] = T5; v.Num["T7"] = T7;
            v.Num["P3"] = P3; v.Num["P5"] = P5;
            v.Num["far"] = Far; v.Num["far_ab"] = FarAb;
            v.Num["thrust"] = NetThrust;
            v.Num["thrust_gross"] = Thrust;
            v.Num["sfc"] = Sfc; v.Num["fuel_flow"] = FuelFlow;
            v.Num["egt"] = Egt;
            v.Num["n1_pct"] = N1Pct; v.Num["n2_pct"] = N2Pct;
            v.Num["alt"] = Alt;
            v.Num["self_sustaining"] = SelfSustaining ? 1.0 : 0.0;
            v.HasTurbineSplit = false;   // 简单模式没有逐级功分量
            foreach (var e in Errors) v.Errors.Add(e);
            foreach (var w in Warnings) v.Warnings.Add(w);
            return v;
        }
    }

    /// <summary>设计点解出来、非设计点要冻结住的那几个量。</summary>
    public class SimpleConstants
    {
        public double FprD = 1.0;      // 设计点风扇压比（仅回显）
        public double Fc;              // W_fan / W_comp，非设计点真正的锚
        public double Ttr = 0.7;       // T5/T4
        public double OprD = 1.0;
        public double Bpr;
        public double Tt0D = 288.15;
        public double Pt0D = 101325.0;
        public double MdotD;
        public double T2D, T3D, T5D;
        public bool Ok = true;
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>
    /// 简单模式求解器：输入只有涵道比、增压比、燃烧室温度、排气温度、直径。
    /// 与残酷模式的根本区别是**没有几何**——所以转速是名义值，流量走换算流量守恒，
    /// 增压比不是输入而是能量平衡的涌现量。
    /// </summary>
    public static class Simple
    {
        public const double FaceMachStatic = 0.5;
        public const double T0Sl = 288.15;
        public const double TtrIdle = 0.78;
        public const double TtrIdleExp = 1.5;
        public const double T4ThrottleExp = 2.0;

        static double TExhaustCap(SimpleDesign sd)
        {
            double cap = JetForge.Core.Cycle.AbMaxTemp(sd.Tech);
            if (sd.TExhaust > 0.0) cap = Math.Min(cap, sd.TExhaust);
            return cap;
        }

        /// <summary>
        /// 风扇压比的经验统计式：fpr = 1.32 + 0.66/(1 + 0.38·BPR)。
        /// 只保证单调 + 落在真实机带宽内（CF6/JT9D 实测 1.6~1.67，GE90 1.43），精度 ±10%。
        /// </summary>
        public static double FprFromBpr(double bpr)
        {
            if (bpr <= 1e-6) return 1.0;
            return 1.32 + 0.66 / (1.0 + 0.38 * bpr);
        }

        /// <summary>台架口径的面流量：把"海平面静止"当成面马赫 0.5 的可积分状态。</summary>
        public static double FaceMassFlow(double area, double pt, double tt, double mach)
        {
            double g = Gas.Gamma(tt);
            double tr = 1.0 + 0.5 * (g - 1.0) * mach * mach;
            double pStat = pt / Math.Pow(tr, g / (g - 1.0));
            double rho = pStat / (Gas.R * (tt / tr));
            return rho * mach * Math.Sqrt(g * Gas.R * (tt / tr)) * area;
        }

        public static SimpleConstants DesignConstants(SimpleDesign sd)
        {
            sd.Normalize();
            var c = new SimpleConstants { Bpr = sd.Bpr, OprD = sd.Opr };
            c.FprD = sd.Fpr > 0.0 ? sd.Fpr : FprFromBpr(sd.Bpr);
            var tech = sd.Tech;

            double p0, t0;
            Gas.Isa(sd.DesignAlt, out p0, out t0);
            double pt0 = p0, tt0 = t0;
            if (sd.DesignMach > 0.0) Gas.Total(p0, t0, sd.DesignMach, out pt0, out tt0);
            c.Tt0D = tt0; c.Pt0D = pt0;
            c.MdotD = FaceMassFlow(Math.PI * Math.Pow(sd.Diameter / 2.0, 2.0), p0, t0, FaceMachStatic);

            double gam = Gas.Gamma(tt0);
            double etaF = tech.FanEtaPol, etaC = tech.HpcEtaPol;
            double t2 = tt0 * Math.Pow(c.FprD, (gam - 1.0) / (gam * etaF));
            double t3 = tt0 * Math.Pow(c.OprD, (gam - 1.0) / (gam * etaC));
            c.T2D = t2; c.T3D = t3;

            if (t3 >= sd.T4)
            {
                c.Ok = false;
                c.Errors.Add(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("设计点压气机出口 {0:F0} K 已不低于 T4={1:F0} K：增压比 {2:F1} 与燃烧室温度不相容", "Design-point compressor exit {0:F0} K is not below T4={1:F0} K: pressure ratio {2:F1} and burner temperature are incompatible"),
                    t3, sd.T4, sd.Opr));
                return c;
            }

            double wComp = Gas.Cp(t3) * (t3 - t2);
            double wFan = Gas.Cp(t2) * (t2 - tt0) * (1.0 + c.Bpr);
            double wTurb = (wComp + wFan) / tech.MechEfficiency;
            double t5 = sd.T4 - wTurb / Gas.Cp(sd.T4);
            if (t5 <= 200.0)
            {
                c.Ok = false;
                c.Errors.Add(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("设计点涡轮出口温度 {0:F0} K 不合理：增压比 {1:F1} 配 T4={2:F0} K 时轴功率不平衡", "Design-point turbine exit temperature {0:F0} K does not add up: shaft power does not balance at pressure ratio {1:F1} with T4={2:F0} K"),
                    t5, sd.Opr, sd.T4));
                return c;
            }
            c.T5D = t5; c.Ttr = t5 / sd.T4;
            c.Fc = wFan / Math.Max(wComp, 1e-6);
            return c;
        }

        static SimpleResult Cycle(SimpleDesign sd, double alt, double mach, double t4Cmd,
                                  SimpleConstants consts, double mdotScale = 1.0,
                                  double abScale = 1.0, double speedFrac = 1.0,
                                  double ttrOverride = double.NaN)
        {
            var res = new SimpleResult { Alt = alt, Mach = mach };
            var tech = sd.Tech;
            double p0, t0;
            Gas.Isa(alt, out p0, out t0);

            double etaPen = 1.0 - sd.EtaOffdesignK * Math.Pow(1.0 - Clamp(speedFrac, 0.0, 1.0), 2.0);

            double pt0 = p0, tt0 = t0;
            // 进气道型式在简单模式里也生效（只有型式，没有几何）：皮托在超声速吃亏、
            // 斜板/锥体在 M1.5 以上回血。缺省轴对称 = tech.IntakeTpr()，与老代码一致。
            double tprSimple = Intake.Recovery(sd.ToIntakeSpec(), tech, mach)
                               * IntakeSolver.BendLoss(sd.IntakeDuctBendDeg);
            if (mach > 0.0)
            {
                Gas.Total(p0, t0, mach, out pt0, out tt0);
                pt0 *= tprSimple;
            }
            res.T1 = tt0;   // 站 1 记总温，与残酷模式同口径

            // 换算流量 ṁ√θ/δ 在等转速下守恒。旧写法按面马赫积分，
            // M0.85 海平面会把流量算成设计值的 1.95 倍（真实约 1.48），
            // 因为把总压当成了可吸入的静压、还让迎面马赫直接进面积。
            double mdotAir = consts.MdotD * (pt0 / Math.Max(consts.Pt0D, 1e-6))
                             * Math.Sqrt(consts.Tt0D / Math.Max(tt0, 1.0)) * mdotScale;

            // 抽气：压气机引走的那部分不产生推力（残酷模式同一口径，见 IntakeSolver）。
            double bleed = Clamp(sd.IntakeBleedFrac, 0.0, 0.15);
            if (bleed > 0.0) mdotAir *= 1.0 - bleed;

            // 喉道壅塞：这条是简单模式唯一能表达"进气道被憋住"的机制。
            // 0 = 玩家没填 = 不限流（老存档一个数都不动）；填了才按最小截面卡流量。
            if (sd.IntakeThroatRatio > 0.0)
            {
                double throatArea = Math.PI * Math.Pow(sd.Diameter / 2.0, 2.0)
                                    * Clamp(sd.IntakeThroatRatio, 0.2, 1.2);
                double cap = Gas.ChokedFlow(throatArea, pt0, Math.Max(tt0, 1.0));
                if (cap > 0.0 && mdotAir > cap * 1.0005)
                {
                    res.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        Loc.T("进气道喉道壅塞：想要 {0:F1} kg/s，喉道只放过 {1:F1} kg/s（喉道/捕获 {2:F2}）", "Intake throat choked: it wants {0:F1} kg/s but the throat passes only {1:F1} kg/s (throat/capture {2:F2})"),
                        mdotAir, cap, sd.IntakeThroatRatio));
                    mdotAir = cap;
                }
            }
            double bpr = sd.Bpr;
            double mdotCore = mdotAir / (1.0 + bpr);
            res.MdotAir = mdotAir; res.MdotCore = mdotCore; res.Bpr = bpr;

            double etaB = tech.BurnerEta, dpB = tech.BurnerDpRatio;
            double etaC = tech.HpcEtaPol * etaPen;
            double etaF = tech.FanEtaPol * etaPen;
            double etaT = tech.HptEtaPol * etaPen;
            double gam = Gas.Gamma(tt0);
            double cp4 = Gas.Cp(t4Cmd);
            double mec = tech.MechEfficiency;
            double fc = consts.Fc;
            bool limited = false;
            double ttrUse = double.IsNaN(ttrOverride) ? consts.Ttr : ttrOverride;

            double t2 = 0.0, t3 = 0.0, opr = 1.0, wComp = 0.0, wTurb = 0.0;

            // 由涡轮出口温度反解 (T2, T3, opr, w_comp, w_turb)。
            // opr 不进这个式子：涡轮功只由 T4/T5 定，压气机分到的就是 w_turb/(1+fc)，
            // 所以给定 T5 时增压比唯一确定、**不需要迭代**。
            // 把 opr 当迭代变量会踩双不动点：低分支里涡轮受限把 T5 抬上去，
            // 又反过来压低 opr，慢车会被吸到 3.8 而真解在 5.3。
            Action<double> balance = t5in =>
            {
                wTurb = cp4 * (t4Cmd - t5in) * mec;
                wComp = wTurb / (1.0 + fc);
                double wFanAll = wTurb - wComp;
                t2 = tt0 + wFanAll / ((1.0 + bpr) * Gas.Cp(t2 > 0 ? t2 : tt0));
                t3 = t2 + wComp / Gas.Cp(t3 > 0 ? t3 : t2);
                opr = Math.Pow(t3 / tt0, gam * etaC / (gam - 1.0));
            };

            double t5 = t4Cmd * ttrUse;
            t2 = Math.Max(consts.T2D * (tt0 / Math.Max(consts.Tt0D, 1.0)), tt0 + 1.0);
            t3 = Math.Max(consts.T3D * (tt0 / Math.Max(consts.Tt0D, 1.0)), t2 + 1.0);
            balance(t5);
            if (wComp <= 0.0)
            {
                res.Ok = false;
                res.Errors.Add(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("T4={0:F0} K 时涡轮功 {1:F1} kJ/kg 扣掉风扇后没有剩余驱动压气机：这台机器在这个状态下转不起来", "At T4={0:F0} K the turbine work {1:F1} kJ/kg leaves nothing for the compressor after the fan: the engine cannot run in this state"),
                    t4Cmd, wTurb / 1000.0));
                return res;
            }

            // 涡轮出口不能低于环境压强。若冻结温比要求的膨胀比超过可用值，
            // 说明这台机器的涡轮在这个功率下抽太狠：按可用落压比抬高 T5，重解功平衡。
            for (int it = 0; it < 8; it++)
            {
                double pt4 = pt0 * opr * (1.0 - dpB);
                double g5 = Gas.Gamma(t5);
                double piT = Math.Pow(t4Cmd / t5, g5 / ((g5 - 1.0) * etaT));
                double piAvail = Math.Max(pt4 / p0, 1.0001);
                if (piT <= piAvail) break;
                limited = true;
                double t5New = t4Cmd * Math.Pow(piAvail, -(g5 - 1.0) * etaT / g5);
                if (Math.Abs(t5New - t5) < 1.0) { t5 = t5New; break; }
                t5 = t5New;
                balance(t5);
                if (wComp <= 0.0)
                {
                    res.Ok = false;
                    res.Errors.Add(string.Format(CultureInfo.InvariantCulture,
                        Loc.T("T4={0:F0} K 时涡轮已无剩余功率驱动压气机", "At T4={0:F0} K the turbine has no power left to drive the compressor"), t4Cmd));
                    return res;
                }
            }

            double pt4f = pt0 * opr * (1.0 - dpB);
            double g5f = Gas.Gamma(t5);
            double piTf = Math.Pow(t4Cmd / t5, g5f / ((g5f - 1.0) * etaT));
            double piTfClamped = Math.Min(piTf, Math.Max(pt4f / p0, 1.0001));
            if (piTf > piTfClamped)
                piTf = piTfClamped;

            res.T2 = t2; res.T3 = t3; res.Opr = opr;
            res.T4 = t4Cmd; res.T5 = t5;
            res.Fpr = t2 > tt0 ? Math.Pow(t2 / tt0, gam * etaF / (gam - 1.0)) : 1.0;
            res.Cpr = Math.Max(opr / Math.Max(res.Fpr, 1e-6), 1.0);
            res.P3 = pt0 * opr;
            res.TurbineLimited = limited;
            if (limited)
                res.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("涡轮可用落压比只有 {0:F2}，低于冻结温比要求的 {1:F2}：出口温度被抬到 {2:F0} K（冻结值 {3:F0} K）", "The turbine has only {0:F2} available pressure ratio, below the {1:F2} the frozen temperature ratio needs: exit temperature raised to {2:F0} K (frozen {3:F0} K)"),
                    pt4f / p0, piTfClamped, t5, t4Cmd * ttrUse));

            // 燃烧室
            double cp3 = Gas.Cp(t3);
            double dT = t4Cmd - t3;
            if (dT <= 0.0)
            {
                res.Ok = false;
                res.Errors.Add(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("T4={0:F0} K 不高于压气机出口 {1:F0} K：无法加热", "T4={0:F0} K is not above the compressor exit {1:F0} K: nothing to heat"), t4Cmd, t3));
                return res;
            }
            double denom = Gas.LhvJet * etaB - cp4 * dT;
            if (denom <= 0.0)
            {
                res.Ok = false;
                res.Errors.Add(Loc.T("燃烧室温升过高，能量不守恒", "Burner temperature rise is too high; energy does not balance"));
                return res;
            }
            res.Far = cp3 * dT / denom;
            res.P5 = pt4f / Math.Max(piTf, 1e-6);
            res.Egt = t5;

            // 加力
            double tNoz = t5, pNoz = res.P5, farAb = 0.0, mdotNoz = mdotCore;
            bool wantAb = (sd.AbFuelRatio > 0.0 || sd.TExhaust > 0.0) && abScale > 0.0;
            if (wantAb)
            {
                double cap = TExhaustCap(sd);
                if (sd.AbFuelRatio > 0.0)
                {
                    farAb = Math.Min(sd.AbFuelRatio * abScale, 0.045);
                    double dTab = farAb * Gas.LhvJet * 0.90 / Gas.Cp(t5) / (1.0 + farAb);
                    tNoz = Math.Min(t5 + dTab, cap);
                }
                else
                {
                    double target = Math.Min(cap, Math.Max(sd.TExhaust, t5));
                    tNoz = t5 + (target - t5) * Math.Min(abScale, 1.0);
                    farAb = Math.Max((tNoz - t5) * Gas.Cp(t5) / (Gas.LhvJet * 0.90), 0.0);
                }
                if (tNoz > t5 + 1.0)
                {
                    pNoz = res.P5 * (1.0 - 0.03);
                    mdotNoz = mdotCore * (1.0 + farAb);
                }
            }
            res.T7 = tNoz; res.FarAb = farAb;

            // 喷管
            double etaN = tech.NozzleEta;
            double veC, aeC, peC, fC; bool chokedC;
            Gas.Nozzle(mdotNoz, pNoz, tNoz, p0, etaN, out veC, out aeC, out peC, out fC, out chokedC);
            double veB = 0.0, fB = 0.0;
            if (bpr > 0.0)
            {
                double pt2 = pt0 * Math.Max(res.Fpr, 1.0);
                double aeB, peB; bool chokedB;
                Gas.Nozzle(mdotAir - mdotCore, pt2, res.T2, p0, etaN,
                           out veB, out aeB, out peB, out fB, out chokedB);
            }
            res.VJetCore = veC; res.VJetBypass = veB;
            res.ThrustCore = fC; res.ThrustBypass = fB;

            double v0 = mach * Gas.SoundSpeed(t0);
            double gross = fC + fB;
            double aFront = Math.PI * Math.Pow(sd.Diameter / 2.0, 2.0);
            // 净推力 = 总推力 − 冲压阻力 − 短舱型面阻力。
            // 冲压阻力是 ṁ·V0 那一项，型面阻力只占前者的百分之几，别拿它顶替前者。
            res.RamDrag = mdotAir * v0 + 0.5 * (p0 / (Gas.R * t0)) * v0 * v0 * aFront * 0.08;
            res.Thrust = gross;
            res.NetThrust = gross - res.RamDrag;

            res.FuelFlow = mdotCore * res.Far + mdotNoz * farAb;
            // 净推力 ≤0 时 SFC 无定义，给 0：给 Infinity 会顺着 realIsp=3600/(sfc·g0) 变成 0，
            // 拿分母夹 1e-6 又会得到 1e9 量级的荒唐值。"没推力"由 NetThrust 自己表达。
            res.Sfc = res.NetThrust > 0.0
                ? res.FuelFlow * 3600.0 / Math.Max(res.NetThrust, 1e-9)
                : 0.0;
            res.FnPerMdot = res.NetThrust / Math.Max(mdotAir, 1e-6);

            double n1, n2;
            NominalRpm(sd, tt0, out n1, out n2);
            res.N1Rpm = n1; res.N2Rpm = n2;
            res.Spool[1] = n1; res.Spool[2] = n2;

            res.SelfSustaining = res.Ok && res.NetThrust > 0.0 && t4Cmd > res.T3;
            return res;
        }

        static void NominalRpm(SimpleDesign sd, double tt0, out double n1, out double n2)
        {
            var tech = sd.Tech;
            double a = Gas.SoundSpeed(tt0);
            double uF = 0.95 * tech.MaxTipMachFan * a;
            double uC = 0.95 * tech.MaxTipMachComp * a;
            double dCore = sd.Diameter / Math.Sqrt(1.0 + sd.Bpr);
            n1 = sd.Diameter > 0 ? 60.0 * uF / (Math.PI * sd.Diameter) : 0.0;
            n2 = dCore > 0 ? 60.0 * uC / (Math.PI * dCore) : 0.0;
        }

        public static SimpleResult DesignPoint(SimpleDesign sd)
        {
            var c = DesignConstants(sd);
            if (!c.Ok)
            {
                var bad = new SimpleResult { Alt = sd.DesignAlt, Mach = sd.DesignMach, Bpr = sd.Bpr, Opr = sd.Opr, Fpr = c.FprD };
                bad.Ok = false;
                bad.Errors.AddRange(c.Errors);
                return bad;
            }
            var r = Cycle(sd, sd.DesignAlt, sd.DesignMach, sd.T4, c, 1.0, 1.0, 1.0, double.NaN);
            r.Throttle = 1.0;
            return r;
        }

        /// <summary>
        /// 非设计点。只调度三件事：油门 → T4、油门 → 转速（流量与效率）、加力门槛。
        /// 总增压比不是调度出来的——它由能量平衡涌现。
        /// </summary>
        public static SimpleResult Solve(SimpleDesign sd, double alt, double mach, double throttle,
                                         double abFuelRatio = -1.0, SimpleConstants consts = null)
        {
            sd.Normalize();
            var tech = sd.Tech;
            double thr = Clamp(throttle, 0.0, 1.0);
            double nFrac = 0.60 + 0.40 * thr;

            // 风扇换算转速上限：物理 100% 在 11 km 意味着换算 115%，
            // FADEC 会按换算值限速，这就是高空推力掉得比 ρ 更快的那一截。
            double pSl, tAmb;
            Gas.Isa(alt, out pSl, out tAmb);
            double ptIn, ttIn;
            Gas.Total(pSl, tAmb, mach, out ptIn, out ttIn);
            double thetaFan = ttIn / T0Sl;
            double nFracLim = tech.OverspeedLimit * Math.Sqrt(thetaFan);
            bool spoolLimited = nFrac > nFracLim + 1e-6;
            if (spoolLimited) nFrac = nFracLim;

            double t4Idle = sd.GetT4Idle();
            // 油门 → T4 取平方：线性时 15% 油门就把 T4 顶到 750 K、推力跳到 23%，
            // 而 J79 在 N2 66% 只有 8% 左右。
            double t4Cmd = t4Idle + (sd.T4 - t4Idle) * Math.Pow(thr, T4ThrottleExp);

            if (consts == null) consts = DesignConstants(sd);
            if (!consts.Ok)
            {
                var bad = new SimpleResult { Alt = alt, Mach = mach, Throttle = thr, Bpr = sd.Bpr, Opr = sd.Opr, Fpr = consts.FprD };
                bad.Ok = false;
                bad.Errors.AddRange(consts.Errors);
                return bad;
            }

            double ab = abFuelRatio >= 0.0 ? abFuelRatio : sd.AbFuelRatio;
            double abScale;
            if (ab > 0.0 && sd.AbThrottle < 1.0)
                abScale = Clamp((thr - sd.AbThrottle) / (1.0 - sd.AbThrottle), 0.0, 1.0);
            else
                abScale = ab > 0.0 ? 1.0 : 0.0;

            double saved = sd.AbFuelRatio;
            sd.AbFuelRatio = ab;
            SimpleResult r;
            try
            {
                // 涡轮温比不能真的冻结到慢车：冻结时慢车要抽掉全部压头、喷管拿不到压力，
                // 推力会精确掉到 0。物理上慢车的膨胀比只有设计值的 1/3~1/2。
                double ttrCmd = consts.Ttr + (TtrIdle - consts.Ttr) * Math.Pow(1.0 - thr, TtrIdleExp);
                ttrCmd = Math.Max(ttrCmd, consts.Ttr);
                r = Cycle(sd, alt, mach, t4Cmd, consts, nFrac, abScale, nFrac, ttrCmd);
            }
            finally
            {
                sd.AbFuelRatio = saved;
            }

            r.Throttle = thr;
            r.N1Pct = nFrac * 100.0;
            r.N2Pct = nFrac * 100.0;
            r.N1Rpm *= nFrac;
            r.N2Rpm *= nFrac;
            if (sd.Kind == EngineKind.Turbojet)
                r.Warnings.Add(Loc.T("单轴机：N1/N2 为等效读数，实际同轴同步", "Single-shaft engine: N1/N2 are equivalent readouts; the shafts actually turn together"));
            if (spoolLimited)
                r.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("风扇换算转速顶到 {0:F0}% 上限：物理转速被限到 {1:F0}%（θ={2:F3}）", "Fan corrected speed is at its {0:F0}% ceiling: physical spool limited to {1:F0}% (theta={2:F3})"),
                    tech.OverspeedLimit * 100.0, nFrac * 100.0, thetaFan));
            if (r.N1Pct > tech.OverspeedLimit * 100.0)
                r.Warnings.Add(string.Format(CultureInfo.InvariantCulture, Loc.T("N1 {0:F0}% 超过超转线", "N1 {0:F0}% is past the overspeed line"), r.N1Pct));
            return r;
        }

        static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}