using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    public enum EngineKind { Turbojet, Turbofan }
    /// <summary>压气机形式：轴流 / 离心。</summary>
    public enum CompressorKind { Axial, Centrifugal }

    /// <summary>
    /// 轴流级与离心级在同一个欧拉框架下的差别。全是系数，不是新公式：
    /// 单级做功 ΔT ∝ ψU²，离心叶轮的 ψ 是轴流的两倍上下（0.6~0.7 对 0.3~0.35）；
    /// 代价是通流面积小、多变效率低一点，而且真机很少连排超过 2 级。
    /// </summary>
    public static class CompressorPhysics
    {
        public const double CentrifugalPsiFactor = 1.90;
        public const double CentrifugalEtaDelta = -0.015;

        public static double PsiFactor(CompressorKind k)
        {
            return k == CompressorKind.Centrifugal ? CentrifugalPsiFactor : 1.0;
        }

        public static double EtaDelta(CompressorKind k)
        {
            return k == CompressorKind.Centrifugal ? CentrifugalEtaDelta : 0.0;
        }

        /// <summary>
        /// 真机上离心压气机很少超过 2 级（每级压比 3~4，但通流面积小）。
        /// 轴流的上限放宽到 17：J79 就是单转子 17 级（真机），原来写 16 会把它判成"超限"。
        /// 17 以上确实没有真机用——再往上每级的通流面积小到装不下流量。
        /// </summary>
        public static int PracticalStageLimit(CompressorKind k)
        {
            return k == CompressorKind.Centrifugal ? 2 : 17;
        }

        /// <summary>单级压比的合理上限，超过就是"这级叶轮转得太快了"。</summary>
        public static double MaxStagePi(CompressorKind k)
        {
            return k == CompressorKind.Centrifugal ? 5.2 : 1.60;
        }

        /// <summary>
        /// 换算转速偏离设计点后的多变效率。真机压气机特性线上效率是驼峰形：
        /// 设计点最高，转速掉到 70% 大约掉 3 点，掉到 50% 大约掉 8 点。
        /// 二次型过这两点：Δη = η_d · 0.33 · (1-x)²。
        /// 只给非设计点用；x=1 时严格等于设计效率，不碰 Cycle 的任何结果。
        /// </summary>
        public static double OffDesignEta(double etaDesign, double speedFrac)
        {
            double x = speedFrac;
            if (x <= 0.0) return etaDesign;
            double d = 1.0 - x;
            // 二次型减速衰减。系数 0.55 对的是真机压气机特性线：
            // 90% 转速掉约 1 点、70% 掉约 5 点、60% 掉约 9 点（早年用 0.33，60% 只掉 5 点，
            // 结果慢车推力占了满油门的 10.7%——真机只有 5~7%，说明部分功率的损失给少了）。
            return etaDesign * (1.0 - 0.55 * d * d);
        }
    }

    public enum BurnerKind { Can, Cannular, Annular, ShortAnnular }
    public enum AfterburnerKind { Ring, SprayRing, Dump }
    public enum NozzleKind { Converging, Variable, ConvergingDiverging }

    public class CompressorSpec
    {
        public string Name = "LPC";
        public int Stages = 2;
        public double TipDiameter = 0.62;
        public double PsiScale = 1.0;
        public int Shaft = 1;
        public double HubTipRatio = 0.75;
        /// <summary>压气机形式。离心级靠叶轮做功，每级压比高得多，但通流面积小、级数上不去。</summary>
        public CompressorKind Kind = CompressorKind.Axial;

        /// <summary>
        /// 有没有可调静子（VIGV / VSV）。
        ///
        /// 这不是装饰件：低换算转速下，第一级叶片的进气冲角会大到接近失速，
        /// 可调静子把前几级叶片转一个角度，把气流重新掰到设计冲角上，
        /// 于是**流量能压得更低而喘振线往左退**——正因如此真机才能在慢车附近稳定工作。
        /// 关掉它，模型会诚实地告诉你低转速已经越过喘振线（见 §141 那个 −12.4%）。
        ///
        /// 缺省 true，但只在"轴流、级数 ≥ 4"的机器上生效（`CompressorChart` 里判）：
        /// 离心级与两级小压气机一般没有这套作动机构，给它们装上等于凭空多出裕度。
        /// </summary>
        public bool Vigv = true;

        /// <summary>
        /// 有没有中间级放气。
        ///
        /// 它和可调静子不是一件事，也不能互相代替：静子改的是**喘振边界在哪**，
        /// 放气改的是**流道里到底有多少流量**。真机低转速时两样一起用——
        /// 静子把边界往左拽，放气把前几级的流量卸掉一截，让前面几级不至于先失速。
        ///
        /// 抽走的那股气从旁路排掉，不进核心：核心流量只剩 (1 − b)，
        /// 涡轮与喷管都按这个数算，所以放气是"拿推力换稳定"。
        /// </summary>
        public bool Bleed = true;

        /// <summary>
        /// 放气比例：0 = 按转速自动（<see cref="Bleed"/> 关掉时不用）。
        /// 大于 0 就是固定比例（0.02 就是抽 2%），夹在 [0, 0.25]。
        /// </summary>
        public double BleedFrac = 0.0;

        /// <summary>
        /// 逐级叶尖直径（m），下标 0 起。空表 = 全用 <see cref="TipDiameter"/>，
        /// 某一项 &lt;= 0 = 这一级也用缺省值。
        ///
        /// 为什么允许逐级不同：真机的流道是**收敛**的——气流被压一次体积就小一次，
        /// 每级都得更细一圈，否则叶片顶上就顶到机匣外面去了。以前一台机器只有一个
        /// 叶尖直径，等于假设所有级的轮盘一样大，那只有第一级是对的。
        /// </summary>
        public List<double> StageTip = new List<double>();

        /// <summary>逐级 ψ 倍率（0 = 用 <see cref="PsiScale"/>）。前面的级负荷低、后面高，是分级设计的常态。</summary>
        public List<double> StagePsi = new List<double>();

        public double TipOf(int stage)
        {
            if (stage >= 0 && stage < StageTip.Count && StageTip[stage] > 0.0) return StageTip[stage];
            return TipDiameter;
        }

        public double PsiOf(int stage)
        {
            if (stage >= 0 && stage < StagePsi.Count && StagePsi[stage] > 0.0) return StagePsi[stage];
            return PsiScale;
        }

        /// <summary>该部件里最大的叶尖直径——轴转速由它锁在叶尖马赫上限上。</summary>
        public double MaxTip()
        {
            double m = TipDiameter;
            for (int i = 0; i < StageTip.Count; i++)
                if (StageTip[i] > m) m = StageTip[i];
            return m;
        }

        public bool HasStageEdit()
        {
            return (StageTip.Count > 0) || (StagePsi.Count > 0);
        }

        /// <summary>逐级表跟级数对齐：级数加了补缺省，减了截断。改级数时要调。</summary>
        public void SyncStages()
        {
            while (StageTip.Count < Stages) StageTip.Add(0.0);
            while (StageTip.Count > Stages) StageTip.RemoveAt(StageTip.Count - 1);
            while (StagePsi.Count < Stages) StagePsi.Add(0.0);
            while (StagePsi.Count > Stages) StagePsi.RemoveAt(StagePsi.Count - 1);
        }

        public CompressorSpec Clone()
        {
            CompressorSpec c = (CompressorSpec)MemberwiseClone();
            c.StageTip = new List<double>(StageTip);
            c.StagePsi = new List<double>(StagePsi);
            return c;
        }
    }

    public class TurbineSpec
    {
        public string Name = "LPT";
        public int Stages = 3;
        public double TipDiameter = 0.62;
        public double PsiScale = 1.0;
        public int Shaft = 1;
        public double HubTipRatio = 0.75;

        /// <summary>逐级叶尖直径（m），空表 = 全用 <see cref="TipDiameter"/>。涡轮流道也是外扩的。</summary>
        public List<double> StageTip = new List<double>();

        /// <summary>逐级 ψ 倍率（0 = 用 <see cref="PsiScale"/>）。</summary>
        public List<double> StagePsi = new List<double>();

        public double TipOf(int stage)
        {
            if (stage >= 0 && stage < StageTip.Count && StageTip[stage] > 0.0) return StageTip[stage];
            return TipDiameter;
        }

        public double PsiOf(int stage)
        {
            if (stage >= 0 && stage < StagePsi.Count && StagePsi[stage] > 0.0) return StagePsi[stage];
            return PsiScale;
        }

        public double MaxTip()
        {
            double m = TipDiameter;
            for (int i = 0; i < StageTip.Count; i++)
                if (StageTip[i] > m) m = StageTip[i];
            return m;
        }

        public bool HasStageEdit()
        {
            return (StageTip.Count > 0) || (StagePsi.Count > 0);
        }

        public void SyncStages()
        {
            while (StageTip.Count < Stages) StageTip.Add(0.0);
            while (StageTip.Count > Stages) StageTip.RemoveAt(StageTip.Count - 1);
            while (StagePsi.Count < Stages) StagePsi.Add(0.0);
            while (StagePsi.Count > Stages) StagePsi.RemoveAt(StagePsi.Count - 1);
        }

        public TurbineSpec Clone()
        {
            TurbineSpec t = (TurbineSpec)MemberwiseClone();
            t.StageTip = new List<double>(StageTip);
            t.StagePsi = new List<double>(StagePsi);
            return t;
        }

        public bool IsPresent { get { return Stages > 0; } }
    }

    public class FanSpec
    {
        public double Diameter = 1.2;
        public int Stages = 1;
        public double HubTipRatio = 0.42;
        /// <summary>玩家给定风扇压比；&lt;=0 表示由叶尖速度与级功自动推。</summary>
        public double PressureRatio = 0.0;
        public int Shaft = 1;
        /// <summary>
        /// 风扇叶片数。0 = 按直径自动估（宽弦风扇大致 每米 28 片）。
        /// 真机这个数跟"宽弦"设计强相关：CFM56-7B 只有 22 片宽弦叶，PW4000 是 38 片薄叶，
        /// 直径一样、叶片数差 70%——所以它必须能改，不能由直径唯一决定。
        /// </summary>
        public int BladeCount = 0;

        /// <summary>
        /// 风扇减速齿轮比 = 低压轴转速 / 风扇转速。1 = 风扇直接坐在低压轴上（传统涡扇）；
        /// 大于 1 = 低压轴经行星齿轮减速后带动风扇，也就是 GTF（齿轮传动涡扇）。
        ///
        /// 这东西为什么值得单独一个参数：风扇能不能高效工作，取决于**叶尖速度**。
        /// 想让风扇转得慢（叶尖不过声速、级压比低、级数少、涵道比大），传统做法是把
        /// 低压涡轮做成五六级去凑低速；齿轮的作用就是让"低压涡轮按它喜欢的转速转、
        /// 风扇按它喜欢的转速转"。PW1100G 的比值约 3.0（低压轴 ~9000 rpm、风扇 ~3000 rpm）。
        /// </summary>
        public double GearRatio = 1.0;

        /// <summary>减速齿轮箱效率；&lt;=0 表示按 0.995（单级行星齿轮，真机 0.99~0.997）。</summary>
        public double GearEta = 0.0;
    }

    public class BurnerSpec
    {
        public BurnerKind Kind = BurnerKind.Annular;
        public double Length = 0.4;
        public double ExitTemp = 1500.0;
        /// <summary>&lt;0 表示由 kind 查表。</summary>
        public double DpRatio = -1.0;
        public double EtaComb = -1.0;
        /// <summary>
        /// 火焰筒长径比 L/H（长度 / 环形通道高度）。&lt;=0 表示按形式取缺省：
        /// 罐式 3.0、罐环 2.6、环形 2.2、短环形 1.6。
        /// 太短烧不完，太长摩擦与掺混损失上来——两头都要罚。
        /// </summary>
        public double LinerLd = 0.0;
        /// <summary>罐数。&lt;=0 表示按形式取缺省（罐式 8、罐环 10、环形 0 = 没有罐）。</summary>
        public int CanCount = 0;
    }

    public class AfterburnerSpec
    {
        public bool Enabled = false;
        public double ExitTemp = 1900.0;
        /// <summary>玩家直接拧"喷油量"时用这个（far_ab，相对主流空气质量）；&lt;=0 表示用 ExitTemp。</summary>
        public double FuelRatio = 0.0;
        public AfterburnerKind Kind = AfterburnerKind.Ring;

        /// <summary>
        /// 油门过线自动点亮（AJE 的做法）。真机加力是飞行员另推一段、
        /// 而且推力随油门继续往上加，不是"到 100% 再按一个开关"。
        /// </summary>
        public bool AutoLight = true;

        /// <summary>自动点亮的油门起点。0.70 = 70% 以下纯军推，以上加力按比例注入。</summary>
        public double LightThrottle = 0.70;

        /// <summary>
        /// 加力段自己的长度（米）。&lt;=0 表示按核心机直径自动取（约 1.6 倍核心机半径 ×2）。
        /// 加力段不是一个"稍胖的筒子"，它是一整段带火焰稳定器和筒衬的燃烧段：
        /// 涡轮出口到喷管之间要真的空出一段，排气锥才能收在它前面而不是露在喷口上。
        /// </summary>
        public double Length = 0.0;
    }

    /// <summary>
    /// 一台发动机的完整设计输入。存档里只存它——性能全部算出来，改一个参数全部重算。
    /// 这个类**不依赖 KSP**，持久化在 EngineDesignData.Persistence.cs 里，
    /// 这样同一份物理内核既能给游戏用，也能在命令行里做回归测试。
    /// </summary>
    public partial class EngineDesignData
    {
        public string DesignName = "JF-1";
        public EngineKind Kind = EngineKind.Turbofan;
        public string TechName = "1970s";

        public double InletDiameter = 1.0;
        /// <summary>
        /// 外形最大直径（整流罩口径）。&lt;=0 表示就用进气口径。
        ///
        /// 为什么要跟 <c>InletDiameter</c> 分开：那个数是按**流量反解出来的等效捕获面积**，
        /// 不是用尺子量的外径（J79 反解出来 0.74 m，真机外径 0.99 m）。拿它当几何口径，
        /// 整台机器会细 25%——所谓"像根棍"。所以外形另有一个口径，只影响建模。
        /// </summary>
        public double NacelleDiameter = 0.0;
        /// <summary>
        /// 手工指定的整机长度（米）。**只在 <see cref="LengthAuto"/> = false 时生效。**
        ///
        /// 以前这是玩家拧的主参数，可它跟各部件（压气机几级、涡轮几级、燃烧室多长）毫无关系：
        /// 把 17 级压气机改成 9 级，长度还写着 5.2 m，于是凭空多出两米管道、推力凭空掉一个点。
        /// 现在缺省由 <see cref="EngineLength.Estimate"/> 按部件加出来，
        /// 这个字段退化成"存档里那份旧值"与"玩家确实想硬指定"两种用途。
        /// </summary>
        public double Length = 3.0;

        /// <summary>
        /// 长度是否按部件自动算（缺省 true）。关掉它才会用 <see cref="Length"/> 里的手填值——
        /// 留给"我知道真机多长、懒得按级数标定"的场合，但界面上不再给滑杆。
        /// </summary>
        public bool LengthAuto = true;

        /// <summary>
        /// **真正生效的整机长度。** 全工程算物理、算质量、算网格、算管道摩擦都该读它，
        /// 而不是直接读 <see cref="Length"/>——后者只是一份可能过期的存档值。
        /// </summary>
        public double EffectiveLength
        {
            get
            {
                if (!LengthAuto) return Length > EngineLength.MinLength ? Length : EngineLength.MinLength;
                double v = EngineLength.Estimate(this);
                return v < EngineLength.MinLength ? EngineLength.MinLength : v;
            }
        }
        /// <summary>&lt;=0 表示由压气机叶尖直径推。</summary>
        public double CoreInletDiameter = 0.0;

        public FanSpec Fan = new FanSpec();
        public CompressorSpec Lpc = new CompressorSpec { Name = "LPC", Stages = 2, TipDiameter = 0.62, Shaft = 1 };
        public CompressorSpec Hpc = new CompressorSpec { Name = "HPC", Stages = 8, TipDiameter = 0.50, Shaft = 2 };
        public TurbineSpec Hpt = new TurbineSpec { Name = "HPT", Stages = 1, TipDiameter = 0.55, Shaft = 2 };
        public TurbineSpec Ipt = new TurbineSpec { Name = "IPT", Stages = 0, TipDiameter = 0.58, Shaft = 2 };
        public TurbineSpec Lpt = new TurbineSpec { Name = "LPT", Stages = 3, TipDiameter = 0.62, Shaft = 1 };
        public BurnerSpec Burner = new BurnerSpec();
        public AfterburnerSpec Afterburner = new AfterburnerSpec();
        /// <summary>
        /// 进气道。以前这个零件在内核里根本不存在——总压恢复是 `TechLevel.IntakeTpr()` 一个函数、
        /// 迎面马赫是写死的一句 0.5、捕获面积直接拿风扇直径算。要做自定义进气道，
        /// 先得让它变成一个能描述、能存盘、能被判据检查的对象。
        /// 缺省（轴对称、不给捕获直径）与老行为逐位相同。
        /// </summary>
        public IntakeSpec Intake = new IntakeSpec();

        public int ShaftCount = 2;
        public double DesignMach = 0.0;
        public double DesignAlt = 0.0;
        /// <summary>&lt;=0 表示由几何推涵道比。</summary>
        public double OverrideBpr = 0.0;
        public NozzleKind Nozzle = NozzleKind.Converging;
        /// <summary>
        /// 矢量喷口：整个喷口段能跟着飞控指令偏转（F-22/F-35 那种）。
        ///
        /// 物理上它不是"多给一点推力"，而是把同一股推力换个方向：
        /// 偏转 15° 时轴向分量少 3.4%、侧向多 26%。所以这里只改方向，不改大小。
        /// 收敛喷管没有可动段，构型上就没法偏——判据会点出来。
        /// </summary>
        public bool ThrustVectoring = false;
        /// <summary>喷口最大偏转角（度）。真机 15~20°；超过 25° 的只有试验机。</summary>
        public double GimbalMaxDeg = 15.0;
        /// <summary>
        /// 花瓣数（0 = 按出口尺度自动，见 `NozzleVisual.CountOf`）。
        /// 真机 12~24 片：片太少每片太宽、叶片之间的缝漏气；片太多每片太窄，
        /// 薄板刚不住压差。它只改外形与作动系统，不改喉道面积——面积是气动的账。
        /// </summary>
        public int PetalCount = 0;
        /// <summary>
        /// 花瓣长度相对出口半径的倍数（1.0 = 基准 0.48·R，见 `NozzleVisual.LengthFactor`）。
        /// 长叶片能把同一喉道撑开更大的出口，但叶片更重、根部弯矩更大、也更容易颤振。
        /// </summary>
        public double PetalLengthFrac = 1.0;
        /// <summary>
        /// 矢量的三个方向开关。偏航抖就关偏航——不是"修好了抖动"，而是把那一轴的选择权
        /// 交给装机的人：同一台发动机装在长机身、短机身、双发差动的机器上，
        /// 对哪一轴需要灵敏、哪一轴需要稳，答案不一样。
        /// </summary>
        public bool VectorPitch = true;
        public bool VectorYaw = true;
        /// <summary>
        /// 滚转。物理上只有喷口**不在质心中轴线上**（r 有横向分量）时才滚得动机身，
        /// 所以缺省关闭——轴线上装一台、按了滚转没反应，比按了有反应但要解释半天更好。
        /// </summary>
        public bool VectorRoll = false;
        /// <summary>
        /// 发动机控制系统档次。按年代解锁：1940s 只有无控，1990s 之后才有全权限数字。
        ///
        /// **两种口径（§270）**：
        /// 无控的机器里，油门就是油量——推多少烧多少，超温、超转、喘振全靠飞行员自己躲；
        /// 有控的机器里，油门是**目标推力**，控制器自己决定烧多少油去凑它，凑不到就减油，
        /// 减到留不住火为止。同样是 100% 油门，无控的起步猛、上限高、容易喘，
        /// 有控的起步温吞（推力指令本身爬得慢）但推不坏。
        /// 与档次无关的那一层是"限温/限转/加速度/喘振裕度"这几道闸，串在油量后面。
        /// </summary>
        public ControlKind Control = ControlKind.None;
        /// <summary>
        /// 细节等级：叶片排、静子、机匣法兰、附件机匣、点火器加多少。
        ///
        /// 这些东西一个都不参与气动，只会让三角数往上翻——高档一台一万多三角，
        /// 二十台同款零件就是几十万。所以它必须是一个可以调下来的选项，不是硬编码的"尽量多"。
        /// </summary>
        public DetailLevel Detail = DetailLevel.Medium;
        /// <summary>
        /// 音效音量倍率（0~2）。喷气发动机在 KSP 里本来就吵，玩家总得有个地方拧一下；
        /// 只影响声音，不碰任何物理量。
        /// </summary>
        public double SoundGain = 1.0;

        /// <summary>（已停用）曾经用来"装了 RSE 也不让位"。现在音效不再给 RSE 让位，
        /// 三层永远自己放，这个开关没有意义了；字段留着只为读旧存档不报错。</summary>
        public bool ForceOwnBaseSound = false;


        /// <summary>（已停用）慢车推力占比与慢车 N2 目标——这两个旋钮撤掉了。
        ///
        /// 慢车不是设计选择，是"这台机器自己转着、火还留得住"的最低那一档，由求解器反解
        /// （`OffDesign.CalibrateIdleAuto`）。玩家填的份额一旦不是那个点，模型只能静默夹取
        /// 或烧一个维不住的 T4——前者让读数骗人，后者让慢车变成"随时熄火"的旋钮。
        /// 字段留着只为读旧存档不报错，不再有任何效果。</summary>
        public double IdleThrustFrac = 0.0;

        /// <summary>（已停用）见 <see cref="IdleThrustFrac"/>。</summary>
        public double IdleN2Pct = 0.0;

        public TechLevel Tech { get { return TechLevel.Get(TechName); } }

        public bool HasFan { get { return Kind == EngineKind.Turbofan && Fan.Diameter > 0.0; } }

        /// <summary>核心机进口直径：显式给定优先，否则取压气机叶尖直径。</summary>
        public double CoreInletD
        {
            get
            {
                if (CoreInletDiameter > 0.0) return CoreInletDiameter;
                double a = Lpc.Stages > 0 ? Lpc.TipDiameter : 0.0;
                double b = Hpc.Stages > 0 ? Hpc.TipDiameter : 0.0;
                return Math.Max(a, b);
            }
        }

        /// <summary>涵道比：由风扇面积 / 核心机进口面积给出，不是玩家输入。涡喷恒为 0。</summary>
        public double BypassRatio()
        {
            if (!HasFan) return 0.0;
            if (OverrideBpr > 0.0) return OverrideBpr;
            double dFan = Fan.Diameter;
            double dCore = Math.Min(CoreInletD, dFan * 0.95);
            if (dCore <= 0.0) return 0.0;
            return Math.Max((dFan * dFan - dCore * dCore) / (dCore * dCore), 0.0);
        }

        /// <summary>
        /// 发动机进口截面当量直径——进气道输出这一头对着的那个面的直径。
        /// 有风扇就是风扇直径（外涵也要吃这股气），没风扇就是核心机进口口径。
        /// 进气道三块面积里的"面"用这个，不是唇口也不是喉道。
        /// </summary>
        public double EngineFaceDiameter()
        {
            if (HasFan) return Fan.Diameter;
            double dc = CoreInletD;
            return dc > 0.0 ? dc : Math.Max(InletDiameter * 0.7, 0.05);
        }

        /// <summary>
        /// 发动机进口那张面的面积（m²）。装独立进气道零件时，它是"这台机器一次能吃多少空气"的硬上限
        /// 的分子——见 `Core/Intake/IntakeInstall.cs` 的 `ToSolution(…, engineFaceArea, …)`（§284）。
        /// </summary>
        public double EngineFaceArea()
        {
            double dia = EngineFaceDiameter();
            return Math.PI * 0.25 * dia * dia;
        }

        /// <summary>
        /// 发动机进口那张面的设计面马赫：它自带进气道怎么把气减速，独立进气道就用同一个数封顶。
        /// 口径只有一处（`IntakeSolver.FaceMachOf`），这里只是把"我这台机器的那份 spec"喂进去。
        /// </summary>
        public double EngineFaceMach()
        {
            return IntakeSolver.FaceMachOf(Intake != null ? Intake.Resolved() : null);
        }

        // `MaxTipDiameterOnShaft(shaft)` 曾放在这里：逐部件取同轴最大直径。
        // 现在这件事由 `Core/Arch/ShaftSet.cs` 与 `Core/EngineGeometry.cs` 各按自己的口径做，
        // 这个副本没人调用（也没覆盖分流环/加力段），留着只会让人以为它才是准的，已删。

        public EngineDesignData Clone()
        {
            EngineDesignData c = (EngineDesignData)MemberwiseClone();
            c.Fan = new FanSpec
            {
                Diameter = Fan.Diameter, Stages = Fan.Stages,
                HubTipRatio = Fan.HubTipRatio, PressureRatio = Fan.PressureRatio, Shaft = Fan.Shaft,
                BladeCount = Fan.BladeCount, GearRatio = Fan.GearRatio, GearEta = Fan.GearEta
            };
            c.Lpc = Lpc.Clone();
            c.Hpc = Hpc.Clone();
            c.Hpt = Hpt.Clone();
            c.Ipt = Ipt.Clone();
            c.Lpt = Lpt.Clone();
            // 逐字段抄，抄漏了不会报错——只会让"预设里改过的值"在 Clone 之后悄悄回到缺省。
            // LinerLd/CanCount 就是漏在这里：预设写了罐环 8 罐，进游戏读到的是 0 罐。
            c.Burner = new BurnerSpec
            {
                Kind = Burner.Kind, Length = Burner.Length, ExitTemp = Burner.ExitTemp,
                DpRatio = Burner.DpRatio, EtaComb = Burner.EtaComb,
                LinerLd = Burner.LinerLd, CanCount = Burner.CanCount
            };
            c.Afterburner = new AfterburnerSpec
            {
                Enabled = Afterburner.Enabled, ExitTemp = Afterburner.ExitTemp,
                FuelRatio = Afterburner.FuelRatio, Kind = Afterburner.Kind,
                AutoLight = Afterburner.AutoLight, LightThrottle = Afterburner.LightThrottle,
                Length = Afterburner.Length
            };
            // 进气道：五个字段，同样逐字段抄（`IntakeSpec.Clone()` 是 MemberwiseClone，
            // 不会漏；但这里显式列出来，将来加字段时编译器不会提醒，得靠它自己长眼睛）。
            c.Intake = Intake != null ? Intake.Clone() : new IntakeSpec();
            return c;
        }
    }
}