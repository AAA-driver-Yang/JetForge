using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    public class StageRow
    {
        public string Part;
        public int Index;
        public double Pi;
        public double TIn;
        public double TOut;
        public double PIn;
        /// <summary>这一级的流量（kg/s）。中间级放气之后，后面几级看到的比进口少——
        /// 功 ∝ 流量，所以放气的位置决定了省下多少功，不能等压气机算完再从总量里扣。</summary>
        public double MassFlow;
    }

    /// <summary>设计点结果。字段与 Python 原型 CycleResult 一一对应。</summary>
    public class CycleResult
    {
        public bool Ok = true;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();

        public double MdotAir;
        public double MdotCore;
        public double Bpr;
        public double Opr;
        public double Fpr;
        public Dictionary<int, double> Rpm = new Dictionary<int, double>();
        public Dictionary<string, double> Spool = new Dictionary<string, double>();

        public double T1, T2, T3, T4, T5, T7;
        public double P3, P5;
        public double NozzleAreaRatio = 1.0, NozzleMachE, NozzleExitArea;
        public double Far, FarAb;
        public double WLpc, WHpc, WComp, WFan;
        public double WTurbHpt, WTurbIpt, WTurbLpt;
        public double PiHpt = 1.0, PiIpt = 1.0, PiLpt = 1.0;

        public double Thrust, ThrustCore, ThrustBypass;
        public double FuelFlow, Sfc;
        public double VJetCore, VJetBypass, FnPerMdot;
        public bool SelfSustaining;

        /// <summary>三段管道的总压损失比例（0~1，越大越亏）。长度变长时这里会跟着长。</summary>
        public double DuctInletLoss, DuctCoreLoss, DuctExhaustLoss, DuctBypassLoss;

        /// <summary>
        /// 进气道供气不足、核心机被真实限流了。true 时 <see cref="InletAvailableMassFlow"/>
        /// 就是这一刻能吃到的上限，下游的流量/温度/功率都是按它重算的——
        /// 不是"推力乘个折减系数"那种假匹配。
        /// </summary>
        public bool InletFlowLimited;
        /// <summary>进气道这一刻能提供的最大质量流量（kg/s）。没被限流时也会填，方便对比。</summary>
        public double InletAvailableMassFlow;
        /// <summary>被限流掉的那部分（kg/s）：需求 − 可用。</summary>
        public double InletMassFlowShortfall;
        /// <summary>
        /// 高压压气机工作点在**它自己的特性图**上的喘振裕度（工业口径，%）。
        /// -1 = 这张图没建起来（没有高压压气机、或进出口不可用），不是"裕度为负"。
        /// 与 `Criteria` 里那个 φ/φ_失速 不是一把尺：那个问"这条流道对这么多流量是不是太窄"，
        /// 这个问"工作点离图上的喘振线还有多远"。两个都报，不互相折算。
        /// </summary>
        public double HpcSurgeMargin = -1.0;
        /// <summary>喘振裕度低于 15% 时置真（与告警文案同一个阈值，改一处即可）。</summary>
        public bool HpcSurgeRisk;

        /// <summary>
        /// 低压压气机的喘振裕度，口径与 <see cref="HpcSurgeMargin"/> 相同（-1 = 读数不可用、
        /// 一般就是这台机器没有低压压气机）。涡扇的 LPC 压比不高，但低转速下它比高压那边
        /// 更容易先到边界——两轴机两个数都要看，不能拿高压那张图替低压说话。
        /// </summary>
        public double LpcSurgeMargin = -1.0;
        /// <summary>低压压气机喘振裕度低于 15% 时置真。</summary>
        public bool LpcSurgeRisk;

        /// <summary>
        /// 堵塞侧余量（%）：工作点的换算流量离这条等转速线的堵塞流量还有多远。
        /// 与喘振裕度是两把尺子：那边问"再收油门能不能转"，这边问"再加油门还有没有压比"。
        /// -1 = 读数不可用（没有这张图）。
        /// </summary>
        public double HpcChokeMargin = -1.0;
        /// <summary>低压压气机的堵塞侧余量，口径同 <see cref="HpcChokeMargin"/>。</summary>
        public double LpcChokeMargin = -1.0;
        /// <summary>工作点已经越过堵塞线（塞进去的气比这条线能过的多）。</summary>
        public bool HpcChoked;
        /// <summary>低压压气机越过堵塞线。</summary>
        public bool LpcChoked;

        /// <summary>
        /// 逐级喘振余量里最差的那一级（%）：整机 33% 不代表每一级都还有 33%，
        /// 真机先喘的总是前级。口径只按流量系数（不含压比），因此不能与
        /// `HpcSurgeMargin` 直接比大小，只能用来找"哪一级最危险"。
        /// -1 = 读数不可用（没有图、或者级数为 0）。
        /// </summary>
        public double HpcStageSurgeMargin = -1.0;
        /// <summary>上面那一级是第几级（1 起，0 = 不可用）。</summary>
        public int HpcStageSurgeStage;
        /// <summary>低压压气机逐级喘振余量里最差的那一级，口径同 <see cref="HpcStageSurgeMargin"/>。</summary>
        public double LpcStageSurgeMargin = -1.0;
        /// <summary>低压压气机最差的那一级（1 起，0 = 不可用）。</summary>
        public int LpcStageSurgeStage;

        /// <summary>
        /// 图在这个工作点上说的压比（`CompressorChart.Query`）。与实测压比一起看才有意义：
        /// 两个数差得远，说明这台机器此刻的状态不在这张图上，别把裕度当真（§175）。
        /// 0 = 没有图。
        /// </summary>
        public double HpcChartPr;
        /// <summary>图在这个工作点上说的效率（同上，0 = 没有图）。</summary>
        public double HpcChartEta;
        /// <summary>查图时的越界标志（`CompressorChart.ChartFlags` 的整数值，0 = 在图里面）。</summary>
        public int HpcChartFlags;

        /// <summary>
        /// 风扇叶尖线速度（m/s）。齿轮比 >1 时它等于"轴转速/齿轮比"算出来的那一个，
        /// 与轴的叶尖速度（`TipSpeed`）不同——GTF 的全部意义就在这个差上。
        /// 0 = 没有风扇（涡喷）。
        /// </summary>
        public double FanTipSpeed;
        /// <summary>风扇物理转速（rpm）。同上，齿轮比 >1 时是轴的 1/齿轮比。</summary>
        public double FanRpm;

        /// <summary>齿轮箱在设计点有没有柔度读数（直驱/涡喷为 false，下面的三个量保持 0）。</summary>
        public bool GearboxTorsionPresent;
        /// <summary>风扇轴上的设计扭矩（N·m）。</summary>
        public double GearboxTorque;
        /// <summary>该扭矩下齿轮箱输出轴的扭转角（度）。</summary>
        public double GearboxTwistDeg;
        /// <summary>齿轮箱本体扭振频率（Hz）。</summary>
        public double GearboxFreqHz;

        /// <summary>
        /// 中间级放气抽走了核心流量的多少（0~0.12）。抽走的那股从旁路排掉、不做功，
        /// 所以它是"拿推力换喘振裕度"的那笔账：设计点恒为 0，低转速才开。
        /// </summary>
        public double HpcBleedFraction;
        /// <summary>放气抽走的流量（kg/s）。与 <see cref="HpcBleedFraction"/> 一个意思，换个量纲方便对账。</summary>
        public double HpcBleedFlow;
        /// <summary>
        /// 压气机吞不下这么多气（前排级壅塞）。与 <see cref="InletFlowLimited"/> 是两回事：
        /// 那个是"进气道供不上"，这个是"进气道供上了，压气机吃不下"——转速越低越明显。
        /// </summary>
        public bool CompressorFlowLimited;
        /// <summary>当前转速下压气机前排级能吞的流量上限（kg/s）。</summary>
        public double CompressorSwallowLimit;

        /// <summary>
        /// 高压压气机特性图（由设计点反推，见 `Core/Arch/CompressorChart.cs`）。
        /// 只做读数与面板展示，不参与推力计算；可能为 null（没压气机/求不出设计点）。
        /// 放在这里是因为模块侧只拿得到 CycleResult，拿不到链路站表。
        /// </summary>
        public CompressorChart Chart;

        public List<StageRow> Stages = new List<StageRow>();
        public Dictionary<string, ScaleInfo> Scale = new Dictionary<string, ScaleInfo>();
    }

    public class ScaleInfo
    {
        public double Eta, EtaBase, Re, ClrOverH, DClr, DRe, H;
    }

    /// <summary>
    /// 设计点求解：从几何出发，逐级算到喷管。这是 mod 的核心算法。
    /// 与 Python 原型 jetforge.solve_design_point 逐步对应，改一边必须改另一边，
    /// 并跑 JetForge.Test 的回归对比。
    /// </summary>
    public static class Cycle
    {
        public struct BurnerPerf
        {
            public double Sigma;
            public double Eta;
            /// <summary>涡轮进口温度不均匀度（pattern factor，无量纲）：出口最高温与平均温之差 / 平均温升。</summary>
            public double PatternFactor;
            /// <summary>实际用到的罐数（环形为 0）。</summary>
            public int Cans;
            /// <summary>实际用到的火焰筒长径比。</summary>
            public double Ld;
        }

        // 每行：总压损失 ΔP/P、燃烧效率、缺省罐数、缺省火焰筒长径比 L/H
        private static readonly Dictionary<BurnerKind, double[]> BurnerTypes =
            new Dictionary<BurnerKind, double[]>
            {
                { BurnerKind.Can, new[] { 0.075, 0.970, 8.0, 3.0 } },
                { BurnerKind.Cannular, new[] { 0.060, 0.980, 10.0, 2.6 } },
                { BurnerKind.Annular, new[] { 0.045, 0.985, 0.0, 2.2 } },
                { BurnerKind.ShortAnnular, new[] { 0.055, 0.975, 0.0, 1.6 } },
            };

        /// <summary>
        /// 形状对性能的影响。全是量级拟合，不是从火焰筒内部流场算的：
        /// · 火焰筒太短 → 驻留时间不够，燃烧效率掉（L/H &lt; 1.6 开始罚，1.0 时约 0.83 倍）
        /// · 火焰筒太长 → 摩擦与掺混损失上来（L/H &gt; 3.5 之后每 0.5 加 0.6 点压损）
        /// · 罐式/罐环 → 各罐之间存在温度不均，涡轮进口热点更明显（pattern factor 更大）
        /// 实现在下方 `BurnerPerformance`。（原先还有一层 `BurnerPerfOf` 纯转发，已删。）
        /// </summary>

        private static readonly Dictionary<AfterburnerKind, double[]> AbTypes =
            new Dictionary<AfterburnerKind, double[]>
            {
                { AfterburnerKind.Ring, new[] { 0.930, 0.030 } },
                { AfterburnerKind.SprayRing, new[] { 0.950, 0.040 } },
                { AfterburnerKind.Dump, new[] { 0.900, 0.055 } },
            };

        public static double AbMaxTemp(TechLevel tech)
        {
            return Math.Min(Math.Max(tech.T4Limit + 400.0, 1800.0), 2100.0);
        }

        /// <summary>加力燃烧室形式的（效率, 总压损失）。部件求解器与设计点走同一张表。</summary>
        public static void AbPerformance(AfterburnerKind kind, out double eta, out double dp)
        {
            double[] def = AbTypes.ContainsKey(kind) ? AbTypes[kind] : AbTypes[AfterburnerKind.Ring];
            eta = def[0];
            dp = def[1];
        }

        /// <summary>由燃烧室形式与长度得到（总压恢复系数, 燃烧效率）。</summary>
        public static BurnerPerf BurnerPerformance(BurnerSpec b)
        {
            double[] def = BurnerTypes.ContainsKey(b.Kind) ? BurnerTypes[b.Kind] : BurnerTypes[BurnerKind.Annular];
            double dp = def[0], eta = def[1];
            if (b.DpRatio >= 0.0) dp = b.DpRatio;
            if (b.EtaComb > 0.0) eta = b.EtaComb;
            // 短燃烧室驻留时间不够，效率要打折
            if (b.Length < 0.30) eta *= Math.Max(0.80, 0.55 + 1.5 * Math.Max(b.Length, 0.0));

            // 形状：火焰筒长径比与罐数
            double defCans = def.Length > 2 ? def[2] : 0.0;
            double defLd = def.Length > 3 ? def[3] : 2.2;
            int cans = b.Kind == BurnerKind.Annular || b.Kind == BurnerKind.ShortAnnular
                ? 0
                : (b.CanCount > 0 ? b.CanCount : (int)defCans);
            double ld = b.LinerLd > 0.0 ? b.LinerLd : defLd;

            if (ld < 1.6) eta *= Math.Max(0.45, 0.55 + 0.28 * ld);   // 烧不完
            if (ld > 3.5) dp += 0.012 * (ld - 3.5);                   // 太长，摩擦与掺混
            if (dp > 0.20) dp = 0.20;

            double pf = 0.09;
            if (cans > 0) pf += 0.045;                                // 罐与罐之间的不均匀
            if (cans > 12) pf += 0.02;
            pf += 0.05 * Math.Max(0.0, 2.2 - ld) / 2.2;               // 越短越不均匀
            if (pf > 0.40) pf = 0.40;

            return new BurnerPerf { Sigma = 1.0 - dp, Eta = eta, PatternFactor = pf, Cans = cans, Ld = ld };
        }

        public static double TipSpeed(double diameter, double rpm)
        {
            return Math.PI * diameter * rpm / 60.0;
        }

        /// <summary>
        /// 每个轴的设计转速 = 该轴上最严的那个叶轮马赫限制。
        /// 叶轮越靠冷端，当地音速越低，同样马赫对应的转速越高。
        /// </summary>
        public static Dictionary<int, double> DesignShaftSpeeds(EngineDesignData d, double tRef)
        {
            TechLevel tech = d.Tech;
            double a = Gas.SoundSpeed(tRef);
            Dictionary<int, List<double>> lim = new Dictionary<int, List<double>>();
            for (int i = 1; i <= Math.Max(d.ShaftCount, 1); i++) lim[i] = new List<double>();

            Action<int, double, double> add = (shaft, diam, mach) =>
            {
                if (diam <= 0.0 || mach <= 0.0) return;
                if (!lim.ContainsKey(shaft)) lim[shaft] = new List<double>();
                lim[shaft].Add(mach * a * 60.0 / (Math.PI * diam));
            };

            if (d.HasFan) add(d.Fan.Shaft, d.Fan.Diameter, tech.MaxTipMachFan);
            if (d.Lpc.Stages > 0) add(d.Lpc.Shaft, d.Lpc.MaxTip(), tech.MaxTipMachComp);
            if (d.Hpc.Stages > 0) add(d.Hpc.Shaft, d.Hpc.MaxTip(), tech.MaxTipMachComp);
            if (d.Hpt.Stages > 0) add(d.Hpt.Shaft, d.Hpt.MaxTip(), tech.MaxTipMachTurb);
            if (d.Ipt.Stages > 0) add(d.Ipt.Shaft, d.Ipt.MaxTip(), tech.MaxTipMachTurb);
            if (d.Lpt.Stages > 0) add(d.Lpt.Shaft, d.Lpt.MaxTip(), tech.MaxTipMachTurb);

            Dictionary<int, double> outv = new Dictionary<int, double>();
            foreach (KeyValuePair<int, List<double>> kv in lim)
                outv[kv.Key] = kv.Value.Count > 0 ? Min(kv.Value) : 0.0;
            return outv;
        }

        private static double Min(List<double> v)
        {
            double m = v[0];
            for (int i = 1; i < v.Count; i++) if (v[i] < m) m = v[i];
            return m;
        }

        public static double AirViscosity(double t)
        {
            return 1.458e-6 * Math.Pow(t, 1.5) / (t + 110.4);
        }

        public static double AnnulusHeight(double tipDiameter, double hubTipRatio)
        {
            return Math.Max(0.5 * tipDiameter * (1.0 - hubTipRatio), 0.0);
        }

        /// <summary>
        /// 尺度效应折减：叶尖间隙占比 + 叶片弦长雷诺数。
        /// 大机器两项都饱和不罚；做小了效率就是要差，SFC 就是这么涨上去的。
        /// </summary>
        public static double ScaleEta(double tipDiameter, double hubTipRatio, double u,
                                      double t, double p, double etaPol, TechLevel tech,
                                      double growth, out ScaleInfo info)
        {
            info = null;
            double hIn = AnnulusHeight(tipDiameter, hubTipRatio);
            if (hIn <= 0.0 || u <= 0.0) return etaPol;
            double hRep = Math.Max(hIn * growth, 1e-4);

            double clr = Math.Max(tech.ClearanceM, tech.ClearanceRefFrac * hRep);
            double dClr = Math.Max(tech.ClearanceSens * (clr / hRep - tech.ClearanceRefFrac), 0.0);

            double rho = p / (Gas.R * t);
            double re = rho * u * (hRep / 2.5) / AirViscosity(t);
            double dRe = 0.0;
            if (re < tech.ReRef && re > 1e-6)
                dRe = (1.0 - etaPol) * (Math.Pow(tech.ReRef / re, tech.ReExponent) - 1.0);

            double eta = Math.Min(Math.Max(etaPol - dClr - dRe, 0.50), 0.97);
            info = new ScaleInfo
            {
                Eta = eta, EtaBase = etaPol, Re = re, ClrOverH = clr / hRep,
                DClr = dClr, DRe = dRe, H = hRep
            };
            return eta;
        }

        public static double StagePi(double u, double tIn, double etaPol, double psi)
        {
            double cp = Gas.Cp(tIn);
            double g = Gas.Gamma(tIn);
            double x = psi * u * u / (cp * tIn);
            if (x <= 0.0) return 1.0;
            return Math.Pow(1.0 + x, g * etaPol / (g - 1.0));
        }

        public static double StageDTComp(double u, double tIn, double psi)
        {
            return psi * u * u / Gas.Cp(tIn);
        }

        public static double TurbineDT(double u, double etaPol, double psi)
        {
            return psi * u * u / Gas.CpAir / etaPol;
        }

        /// <summary>
        /// 一台涡轮在设计转速下最多能提供多少温降。**两条天花板取小**：
        /// 一是叶尖速度能榨出的焓降（<see cref="TurbineDT"/>），二是单级落压比的物理上限
        /// （<paramref name="tech"/>.TurbStagePrMax：1970s 2.4、1990s 2.7、2010s 3.0）。
        /// 以前只有第一条在算，第二条仅在事后拿日志报一句错，于是出现"日志写落压比超限、功照给"。
        /// 抽成一个函数是为了让设计点（部件求解）与非设计点（逐轴功平衡）吃同一份数——
        /// 两处各写一遍，迟早会分叉（管弯损失踩过一次）。
        /// 逐级用上一级出口温度算允许温降：涡轮前几级温降大，全用进口温度会高估后面。
        /// </summary>
        public static double TurbineCapacityDt(TurbineSpec spec, TechLevel tech, double rpm, double tIn,
                                               double etaPol, double psiBase, out bool prBound)
        {
            prBound = false;
            if (spec == null || spec.Stages <= 0 || !(tIn > 0.0) || !(etaPol > 0.05)) return 0.0;
            double sum = 0.0;
            double tStage = tIn;
            for (int i = 0; i < spec.Stages; i++)
            {
                double ui = TipSpeed(spec.TipOf(i), rpm);
                double dt = TurbineDT(ui, etaPol, psiBase * spec.PsiOf(i));

                double g = Gas.Gamma(tStage);
                double trLim = Math.Pow(tech.TurbStagePrMax, -(g - 1.0) * etaPol / g);
                double dtPr = tStage * (1.0 - trLim);
                if (dtPr < dt)
                {
                    dt = dtPr;
                    prBound = true;
                }
                sum += dt;
                tStage = Math.Max(0.25 * tIn, tStage - dt);
            }
            return sum;
        }

        public static double PrFromTr(double tIn, double tOut, double etaPol)
        {
            if (tOut >= tIn) return 1.0;
            double g = Gas.Gamma(tIn);
            double tr = Math.Max(tOut / tIn, 1e-6);
            return Math.Pow(tr, -g / ((g - 1.0) * etaPol));
        }

        public static double MassFlow(double area, double pt, double tt, double mach)
        {
            double g = Gas.Gamma(tt);
            return area * pt / Math.Sqrt(tt) * Math.Sqrt(g / Gas.R) * mach
                   * Math.Pow(1.0 + 0.5 * (g - 1.0) * mach * mach, -(g + 1.0) / (2.0 * (g - 1.0)));
        }

        /// <summary>把轴号映射成 N1/N2/N3 读数（与真机命名一致）。</summary>
        public static Dictionary<string, double> SpoolReadout(EngineDesignData d,
            Dictionary<int, double> rpm, Dictionary<int, double> nPct)
        {
            Dictionary<string, double> o = new Dictionary<string, double>();
            if (rpm == null || rpm.Count == 0) return o;
            List<int> shafts = new List<int>(rpm.Keys);
            shafts.Sort();
            Dictionary<int, string> names = ShaftNames(d, shafts);
            foreach (KeyValuePair<int, double> kv in rpm)
                o[names.ContainsKey(kv.Key) ? names[kv.Key] : "N" + kv.Key] = kv.Value;
            if (nPct != null)
                foreach (KeyValuePair<int, double> kv in nPct)
                    o[(names.ContainsKey(kv.Key) ? names[kv.Key] : "N" + kv.Key) + "%"] = kv.Value;
            return o;
        }

        /// <summary>
        /// 轴号 → 驾驶舱里的名字（N1/N2/N3）。
        /// 单转子机那根唯一的轴就叫 N1（J79/J85 手册第一页写的也是 N1）；
        /// 双转子把带风扇的那根叫 N1；三转子按叶尖直径从大到小排 N1/N2/N3。
        /// 设计点、非设计点、瞬态三条算路共用这一份命名，不许各写一套。
        /// </summary>
        public static Dictionary<int, string> ShaftNames(EngineDesignData d, List<int> shafts)
        {
            Dictionary<int, string> names = new Dictionary<int, string>();
            if (shafts == null || shafts.Count == 0 || d == null) return names;
            List<int> sorted = new List<int>(shafts);
            sorted.Sort();
            int fanShaft = d.HasFan ? d.Fan.Shaft : sorted[0];
            if (sorted.Count == 1)
            {
                names[sorted[0]] = "N1";
            }
            else if (sorted.Count == 2)
            {
                int low = sorted.Contains(fanShaft) ? fanShaft : sorted[0];
                foreach (int s in sorted) names[s] = s == low ? "N1" : "N2";
            }
            else
            {
                if (!sorted.Contains(fanShaft)) fanShaft = sorted[0];
                names[fanShaft] = "N1";
                List<int> rest = new List<int>();
                foreach (int s in sorted) if (s != fanShaft) rest.Add(s);
                rest.Sort((x, y) => DiameterOn(d, y).CompareTo(DiameterOn(d, x)));
                for (int i = 0; i < rest.Count; i++) names[rest[i]] = "N" + (i + 2);
            }
            return names;
        }

        private static double DiameterOn(EngineDesignData d, int shaft)
        {
            double m = 0.0;
            if (d.Lpc.Stages > 0 && d.Lpc.Shaft == shaft) m = Math.Max(m, d.Lpc.MaxTip());
            if (d.Hpc.Stages > 0 && d.Hpc.Shaft == shaft) m = Math.Max(m, d.Hpc.MaxTip());
            return m;
        }

        // ------------------------------------------------------------------

        /// <param name="install">机上装的独立进气道合计口径；null = 吃设计自带的那份 IntakeSpec。</param>
        public static CycleResult SolveDesignPoint(EngineDesignData d, IntakeInstall install = null)
        {
            // 架构升级：设计点不再是一个 311 行的函数，而是
            // 气路模板（GasPathTemplate）→ 逐部件求解（Components.cs）→ 汇总（EngineNetwork）。
            // 公式一行没改，所以设计点数值与改造前逐位相同——回归第 1 段就是钉这个的。
            return EngineNetwork.SolveDesign(d, install).Result;
        }

    }
}