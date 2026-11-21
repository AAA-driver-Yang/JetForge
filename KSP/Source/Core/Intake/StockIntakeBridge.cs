using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 原版进气道（<c>ModuleResourceIntake</c>）与缩放 mod 的换算。
    ///
    /// 原版那套和我们的进气道模型不是同一个口径，而且**不是差一点，是三种单位都不一样**：
    /// · <c>area</c> 不是物理面积。原版某件零件的 cfg 里自己写着注释
    ///   「frontal area is only .39m^2 but there's a conic ram effect」而它的 <c>area = 0.0061</c>——
    ///   0.39 / 0.0061 ≈ 64。也就是说 cfg 里的数是 Squad 的**游戏口径**，
    ///   和零件看上去多大没有固定关系。<see cref="KspAreaToPhysical"/> 就是那个 64，
    ///   只在量不到零件外形时兜底用。
    /// · <c>intakeSpeed</c> **不是速度上限**。原版环形进气道是 15、冲压是 10、激波锥是 5，
    ///   这些数当上限用的话飞机 15 m/s 就该断气了——它实际是低速时的吸气流速
    ///   （原版按 area × max(速度, intakeSpeed) 算进气量）。
    ///   早先这里把它当马赫上限读（15 m/s → M0.044），装机表现是**一离地就断气**，
    ///   这才是「原版进气道适配没做对」的根子。
    /// · <c>machCurve</c> 才是原版真正的性能曲线（乘子，不是上限）：
    ///   环形进气道 M1→1.0、M1.5→0.9、M2.5→0.45、M3.5→0.1。以前这条曲线被完全无视，
    ///   现在按曲线折算捕获面积（见 <see cref="IntakeUnit.SupplyAt"/>）。
    ///
    /// 这个文件只做算术，不碰 Unity / KSP 类型，所以能在回归里直接验。
    /// </summary>
    public static class StockIntakeBridge
    {
        /// <summary>原版进气道在捕获面积之外的喉道收缩比。</summary>
        public const double DefaultThroatRatio = 0.90;

        /// <summary>缺省迎面马赫（原版不填，取我们皮托式的那个 0.45 太保守，用 0.50）。</summary>
        public const double DefaultFaceMach = 0.50;

        /// <summary>海平面标准声速（m/s）。原版给的是 m/s，我们比的是马赫，只能在这里换算。</summary>
        public const double SeaLevelSoundSpeed = 340.29;

        /// <summary>
        /// 量不到零件外形时，cfg <c>area</c> 还原成物理面积的经验系数。
        /// 由原版零件自己 cfg 里的对照算出：物理迎风面 0.39 m² ↔ 声明 area 0.0061。
        /// 只当兜底——正常路径是 <see cref="PhysicalCaptureArea"/> 量出的真实口径。
        /// </summary>
        public const double KspAreaToPhysical = 64.0;

        /// <summary>小于这个口径（米）就不认为量到了外形，退回 cfg 面积。</summary>
        public const double MinMouthDiameter = 0.05;

        /// <summary>
        /// m/s → 马赫。**只给修过 cfg 的 mod 用**（它们真填了 m/s 为单位的上限字段）。
        /// 原版的 <c>intakeSpeed</c> 千万别往这里喂，它根本不是上限，见类注释。
        /// </summary>
        public static double SpeedToMach(double speedMS)
        {
            if (double.IsNaN(speedMS) || speedMS <= 0.0) return 0.0;
            return speedMS / SeaLevelSoundSpeed;
        }

        /// <summary>
        /// 换算后的捕获面积（m²）。两个口径都算出来，**取小的那个**。
        ///
        /// <paramref name="mouthDiameter"/> 是**已经算过缩放**的唇口直径（外形包围盒量出来的
        /// 就是这个数）；<paramref name="areaKps"/> 是 cfg 里那个游戏口径的面积，
        /// 不乘缩放平方（原版件的 rescaleFactor 已经含在它对应的尺寸里，见下）。
        /// </summary>
        public static double PhysicalCaptureArea(double areaKps, double scale, double mouthDiameter)
        {
            double fromMouth = 0.0;
            if (mouthDiameter > MinMouthDiameter && !double.IsNaN(mouthDiameter))
                fromMouth = Math.PI * 0.25 * mouthDiameter * mouthDiameter;

            double fromCfg = 0.0;
            if (!double.IsNaN(areaKps) && areaKps > 0.0)
            {
                // **不再乘缩放系数的平方**。`area` 是 cfg 里对"这个零件在游戏里的尺寸"写的数，
                // 零件的 rescaleFactor（原版件普遍 1.25）已经含在它对应的那个尺寸里了；
                // 再乘一遍 s² 就是重复计入：实测 ramAirIntake 会被算成 Ø0.97 m，
                // 而它自己的 cfg 折算只有 Ø0.78 m。真接了缩放 mod 的件，量出来的口径
                // 会跟着变大，那时取 min 会选 cfg 那侧——偏保守，不会凭空空出一截推力。
                fromCfg = areaKps * KspAreaToPhysical;
            }

            // 两边都有的时候取**小的那个**。
            //
            // 量外形本来是更可信的路子（cfg 的 area 是游戏口径），但它有个已知的坑：
            // 某些原版件的网格包围盒带着 prefab 缩放，量出来会大一圈——实测 `ramAirIntake`
            // 与 `shockConeIntake` 都量成 Ø1.25 m（面积比 cfg 折算值大 2.6 倍），
            // 而 CircularIntake 只差 0.9 倍。按量出来的算，那两件就成了"小件给大流量"：
            // 玩家装上原版进气道，推力比不装还离谱。取小值即"谁也不许比本体声明的能力更大"，
            // 偏保守——保守的进气道只会让推力低一点，不会让飞机凭空多出氧气。
            if (fromMouth > 0.0 && fromCfg > 0.0) return Math.Min(fromMouth, fromCfg);
            if (fromMouth > 0.0) return fromMouth;
            return fromCfg;
        }

        /// <summary>换算后的捕获直径（m）。</summary>
        public static double CaptureDiameterOf(double areaKps, double scale, double mouthDiameter)
        {
            double a = PhysicalCaptureArea(areaKps, scale, mouthDiameter);
            return a > 0.0 ? Math.Sqrt(4.0 * a / Math.PI) : 0.0;
        }

        /// <summary>把一件原版进气道换成我们的 <see cref="IntakeSpec"/>。</summary>
        public static IntakeSpec SpecOf(double areaKps, double scale, double mouthDiameter)
        {
            IntakeSpec sp = new IntakeSpec();
            sp.Kind = IntakeKind.Axial;
            sp.CaptureDiameter = CaptureDiameterOf(areaKps, scale, mouthDiameter);
            sp.ThroatRatio = DefaultThroatRatio;
            sp.FaceMachDesign = DefaultFaceMach;
            sp.BleedFrac = 0.0;
            return sp;
        }

        /// <summary>
        /// 一件原版进气道 → 合成口径要的最小信息。
        /// <paramref name="machLimit"/> 只有修过 cfg 的 mod 才有（0 = 不限，原版全是 0）。
        /// </summary>
        public static IntakeUnit UnitOf(double areaKps, double scale, string name, double faceDiameter,
                                        double mouthDiameter = 0.0,
                                        Func<double, double> supply = null, string supplyStamp = "",
                                        double machLimit = 0.0)
        {
            IntakeSpec sp = SpecOf(areaKps, scale, mouthDiameter);
            double face = faceDiameter > 0.0 ? faceDiameter : Math.Max(sp.CaptureDiameter * DefaultThroatRatio, 0.05);
            return new IntakeUnit(sp, face, string.IsNullOrEmpty(name) ? Loc.T("原版进气道", "Stock intake") : name,
                                  machLimit > 0.0 ? machLimit : 0.0, 0.0, supply, supplyStamp);
        }

        /// <summary>这件原版进气道在这个马赫下还算不算在供气。</summary>
        public static bool SuppliesAt(double machLimit, double mach)
        {
            if (machLimit <= 0.0) return true;           // 没填上限 = 不限
            if (double.IsNaN(mach)) return false;
            return mach <= machLimit * 1.01;             // 留 1% 余量，别在边界上抖
        }

        /// <summary>
        /// 把 cfg 里 <c>machCurve</c> 的节点做成一个纯算术函数（线性插值）。
        ///
        /// 不直接用 Unity 的 <c>FloatCurve</c> 有两个原因：一是回归工程不引用 UnityEngine，
        /// 二是这条曲线是 Squad 手填的节点，节点之间本来接近直线；用同一个插值口径
        /// 也能让"cfg 里写了什么"和"我们算了什么"对得上。范围外按端点值取。
        /// </summary>
        public static Func<double, double> CurveOf(IList<double> machs, IList<double> values)
        {
            if (machs == null || values == null || machs.Count == 0 || values.Count != machs.Count)
                return null;

            double[] xs = new double[machs.Count];
            double[] ys = new double[machs.Count];
            for (int i = 0; i < machs.Count; i++) { xs[i] = machs[i]; ys[i] = values[i]; }
            for (int i = 1; i < xs.Length; i++)         // 节点保证升序，坏输入就丢掉
                if (xs[i] <= xs[i - 1]) return null;

            return delegate (double m)
            {
                if (double.IsNaN(m)) return ys[0];
                if (m <= xs[0]) return ys[0];
                if (m >= xs[xs.Length - 1]) return ys[ys.Length - 1];
                for (int i = 1; i < xs.Length; i++)
                {
                    if (m > xs[i]) continue;
                    double t = (m - xs[i - 1]) / (xs[i] - xs[i - 1]);
                    return ys[i - 1] + (ys[i] - ys[i - 1]) * t;
                }
                return ys[ys.Length - 1];
            };
        }

        /// <summary>曲线的指纹（存进 <see cref="IntakeUnit.SupplyStamp"/>，用来判缓存）。</summary>
        public static string CurveStamp(IList<double> machs, IList<double> values)
        {
            if (machs == null || values == null || machs.Count == 0 || values.Count != machs.Count) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder("curve");
            for (int i = 0; i < machs.Count; i++)
                sb.Append(':').Append(machs[i].ToString("R")).Append('=').Append(values[i].ToString("R"));
            return sb.ToString();
        }

        /// <summary>面板/日志用的一句话。</summary>
        public static string Describe(double areaKps, double scale, double mouthDiameter, string supplyStamp)
        {
            double capA = PhysicalCaptureArea(areaKps, scale, mouthDiameter);
            if (capA <= 0.0) return Loc.T("原版进气道：面积非法", "Stock intake: illegal area");
            string how = mouthDiameter > MinMouthDiameter ? Loc.T("量外形", "measured from the mesh") : string.Format(Loc.T("按 cfg×{0:F0}", "stock cfg x {0:F0}"), KspAreaToPhysical);
            return string.Format(Loc.T("原版进气道：cfg area {0:F4} × 缩放 {1:F2}｜{2} → 捕获 Ø{3:F3} m（{4:F3} m²）｜喉道比 {5:F2}｜{6}", "Stock intake: cfg area {0:F4} x scale {1:F2} | {2} -> capture D{3:F3} m ({4:F3} m2) | throat ratio {5:F2} | {6}"),
                areaKps, scale <= 1e-6 ? 1.0 : scale, how, CaptureDiameterOf(areaKps, scale, mouthDiameter), capA,
                DefaultThroatRatio, string.IsNullOrEmpty(supplyStamp) ? Loc.T("性能曲线：恒 1.0", "curve: constant 1.0") : Loc.T("性能曲线：按 cfg machCurve", "curve: per the stock machCurve"));
        }
    }
}