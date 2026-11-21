using System;

namespace JetForge.Core
{
    /// <summary>
    /// 进气道的型式。
    ///
    /// 以前整套内核里**没有"进气道"这个东西**：总压恢复是 `TechLevel.IntakeTpr(mach)` 一个函数，
    /// 迎面马赫是写在 `Cycle` 里的一句 `const double FaceMach = 0.5`，捕获面积直接拿风扇直径算。
    /// 玩家现在要做自定义进气道，那就得先把这个"隐形的零件"变成一个能描述、能存盘、
    /// 能被判据检查的数据对象——不然可视化编辑器连个能画的框都没有。
    ///
    /// 四种型式对应真机的四条路子：
    ///   Pitot  皮托式（机头一个圆口，亚声速为主，超声速靠一道正激波）
    ///   Axial  轴对称（外压缩锥/中心体，超声速 M1.5~2.5 的常规选择）
    ///   Ramp   二维斜板（F-15/F-22 那种，把激波系摊在矩形管道里）
    ///   Cone   锥体（MiG-21 式的可调中心锥）
    /// </summary>
    public enum IntakeKind
    {
        Axial = 0,
        Pitot = 1,
        Ramp = 2,
        Cone = 3,
    }

    /// <summary>超声速段的总压恢复模型。</summary>
    public enum RecoveryModel
    {
        /// <summary>MIL-E-5008B 经验曲线 + 型式增益。老口径，缺省。</summary>
        Empirical = 0,

        /// <summary>真激波解：正激波 / 斜激波 θ-β-M / 锥激波，含脱体（未起动）判定。</summary>
        Shock = 1,
    }

    /// <summary>
    /// 一台发动机的进气道参数。
    ///
    /// 缺省值就是**现在这套行为**（轴对称、迎面马赫 0.5、按风扇直径取捕获面积），
    /// 所以老存档、老预设读进来数字一分不变——这是有意设计的：先立数据模型，再谈新物理。
    /// </summary>
    public sealed class IntakeSpec
    {
        public IntakeKind Kind = IntakeKind.Axial;

        /// <summary>捕获直径（米）。&lt;=0 表示按风扇直径（没有风扇就按核心机进气口径）。</summary>
        public double CaptureDiameter = 0.0;

        /// <summary>
        /// 喉道面积 / 捕获面积。&lt;=0 按型式取缺省（皮托 0.95、轴对称 0.85、斜板 0.90、锥体 0.88）。
        /// 真机这个数在 0.6~1.0：小于 0.6 是"憋气"（流量上不去、唇口易分离），
        /// 大于 1.05 是"喇叭口"（超声速时唇口溢流、阻力大）。
        /// </summary>
        public double ThroatRatio = 0.0;

        /// <summary>设计迎面马赫。&lt;=0 按型式取缺省。它只影响"流量按多大迎面马赫算"，不改总压恢复。</summary>
        public double FaceMachDesign = 0.0;

        /// <summary>附面层抽吸 / 放气比例（0~0.15）。抽掉的气不参与做功，等于进气量打个折。</summary>
        public double BleedFrac = 0.0;

        /// <summary>
        /// 唇口倒圆半径（米）。&lt;=0 按捕获直径自动（直径的 4%）。
        /// **只进外形**：唇口是圆钝还是薄刃，决定网格好不好看、也决定大迎角时唇口会不会分离，
        /// 但不改设计点流量——那是喉道的事。
        /// </summary>
        public double LipRadius = 0.0;

        /// <summary>
        /// 中心锥/斜板的轴向位置：占进气道长度的比例，**正 = 往前伸（可以伸出唇口）**，负 = 缩进管内。
        ///
        /// 0 就是老外形（锥尖离唇口 2% 长度），所以老存档、老预设读进来一个顶点都不动。
        /// 量程按 `IntakeGeometry.ConeTipShiftMin/Max` 夹住。
        ///
        /// **只进外形，不改性能**：压缩角由设计马赫定（`Intake.RampHalfAngleDeg`），
        /// 喉道面积由喉道比定（`Intake.ThroatAreaOf`）。锥体挪个位置在这套模型里
        /// 既不改激波角也不改通流面积——真机那几个量是一起变的，但本工程没有
        /// 那份"锥位 → 环面积"的标定数据，硬编一条只会变成假旋钮（§270 的口径）。
        /// </summary>
        public double ConeTipShift = 0.0;

        /// <summary>
        /// 进气道管道弯曲角（度，0~60）。0 = 直的（皮托/轴对称常见），
        /// 30° 以上是 S 形进气道。每 10° 弯约 0.8% 总压损失 + 一点畸变，
        /// 隐身机那种"拐个弯把风扇藏起来"的代价就记在这儿。
        /// </summary>
        public double DuctBendDeg = 0.0;

        /// <summary>
        /// 迎角（度，抬机头为正，-20~20）。0 = 正对来流。
        ///
        /// 真实飞行里进气道几乎从不正对来流：机翼要产生升力就得有迎角，
        /// 而进气道对着斜着来的气流，捕获管是斜的、唇口内壁一侧分离、总压恢复掉。
        /// 这里是经验系数，不是求解出来的：亚声速型式每度约 0.12%，压缩型式每度约 0.35%
        /// （斜板/锥体的激波系对来流方向更敏感）。
        /// </summary>
        public double AlphaDeg = 0.0;

        /// <summary>侧滑角（度，-20~20）。同迎角，但权重打个七折——侧滑对流场的不对称影响小一些。</summary>
        public double BetaDeg = 0.0;

        /// <summary>
        /// 超声速段用哪套模型算总压恢复。
        ///
        /// Empirical（缺省）是 MIL-E-5008B 那条经验曲线 + 型式增益，**老存档、老预设的全包线读数靠它保持一分不变**。
        /// Shock 是 `Core/Intake/Shock.cs` 里的真激波解：正激波、斜激波 θ-β-M、锥激波。
        ///
        /// 为什么不直接把 Shock 设成缺省：两套模型在 M2 上差得很多（皮托 0.72 对 0.93），
        /// 一换，所有超声速预设的推力会集体掉一截。这种"改物理"的事得让玩家自己按下按钮。
        /// </summary>
        /// <summary>
        /// 超声速段用哪套模型算总压恢复。
        ///
        /// **自定义进气道（有 <see cref="Shape"/> 的）一律走 Shock**——这是 <see cref="Resolved"/> 里强制的，
        /// 因为经验曲线回答不了"我这个锥角在这个马赫下会不会脱体"。
        /// 剩下的 Empirical（缺省）只服务两种历史对象：没装进气道零件时的兜底口径、以及原版件
        /// （原版件的性能模型是它自己的 machCurve，走 `StockIntakeBridge`）。
        /// </summary>
        public RecoveryModel Model = RecoveryModel.Empirical;

        /// <summary>
        /// 可调几何（真机上的可调中心锥 / 可调斜板）：超声速时把锥体往前推，
        /// 喉道环面积收窄，让激波位置跟马赫对得上。
        ///
        /// 缺省 **关**。理由和 `Model` 一样：老存档里没有这个键，打开就等于改了物理，
        /// 所有超声速读数会一起变。要不要装这套机构，让设计者自己决定。
        /// 关掉时因子恒为 1，读数与以前逐位相同。
        /// </summary>
        public bool VariableGeometry = false;

        /// <summary>
        /// 3D 型线（自定义进气道才有；原版件与老存档为 null）。
        ///
        /// 给了它，捕获面积/喉道比/压缩角/弯曲角**全部从型线上量**，<see cref="Kind"/> 不再参与计算——
        /// 型式是"原版件的老说法"，自己画出来的进气道不该再被塞进四个模板里。
        /// </summary>
        public IntakeProfile Shape = null;

        public IntakeSpec Clone()
        {
            IntakeSpec copy = (IntakeSpec)MemberwiseClone();
            // 型线是可变对象：只做浅拷贝的话，面板里拖一个点会直接改到零件上那一条，
            // 「还原」按回原样也回不去（原件已经被拖过了）。所以这里必须深拷贝。
            if (Shape != null) copy.Shape = Shape.Clone();
            return copy;
        }

        /// <summary>唇口倒圆的实际半径：没填就按捕获直径的 4%。</summary>
        public double ResolvedLipRadius(double captureDiameter)
        {
            if (LipRadius > 0.0) return LipRadius;
            return Math.Max(captureDiameter * 0.04, 0.004);
        }

        /// <summary>没填的项按型式补缺省，返回"实际用到的"那一份（不改自己）。</summary>
        public IntakeSpec Resolved()
        {
            IntakeSpec r = Clone();
            // 有型线就以型线为准：捕获面积、喉道比、弯曲角全从几何量出来，型式和那几个比例栏都不再算数。
            // 等价口径取"等面积圆"，这样 `ThroatAreaOf` 那套 πr²·喉道比 的老口径仍然算出同一块喉道面积，
            // 下游（流量、壅塞判据、质量）一行都不用改，而读数会跟着型线走。
            if (r.Shape != null && r.Shape.Sections != null && r.Shape.Sections.Count >= IntakeProfile.MinSections)
            {
                // 型线是玩家拖出来的，也可能是存档里被改坏的。量出来的量一律先过一遍 Validate：
                // 不过关就当"没有型线"处理，让老口径接住，而不是把一个 NaN 的弯曲角塞进损失公式
                // ——那种错在下游表现成"总压恢复 NaN → 推力 0"，离病根隔着七八层调用。
                string why;
                bool ok = r.Shape.Validate(out why);
                double cap = ok ? r.Shape.EquivalentCaptureDiameter : double.NaN;
                double tr = ok ? r.Shape.ThroatRatio : double.NaN;
                double bend = ok ? r.Shape.BendDeg : double.NaN;
                if (GasState.IsFinite(cap) && cap > 1e-4) r.CaptureDiameter = cap;
                if (GasState.IsFinite(tr) && tr > 0.05) r.ThroatRatio = Math.Min(tr, 1.2);
                if (GasState.IsFinite(bend) && bend > 0.0) r.DuctBendDeg = bend;
                if (ok) r.Model = RecoveryModel.Shock;
            }
            if (r.FaceMachDesign <= 0.0) r.FaceMachDesign = DefaultFaceMach(r.Kind);
            if (r.ThroatRatio <= 0.0) r.ThroatRatio = DefaultThroatRatio(r.Kind);
            return r;
        }

        public static double DefaultFaceMach(IntakeKind k)
        {
            switch (k)
            {
                case IntakeKind.Pitot: return 0.45;
                case IntakeKind.Ramp: return 0.55;
                case IntakeKind.Cone: return 0.55;
                default: return 0.50;
            }
        }

        public static double DefaultThroatRatio(IntakeKind k)
        {
            switch (k)
            {
                case IntakeKind.Pitot: return 0.95;
                case IntakeKind.Ramp: return 0.90;
                case IntakeKind.Cone: return 0.88;
                default: return 0.85;
            }
        }

        public static string KindName(IntakeKind k)
        {
            switch (k)
            {
                case IntakeKind.Pitot: return Loc.T("皮托", "Pitot");
                case IntakeKind.Ramp: return Loc.T("二维斜板", "Two-dimensional ramp");
                case IntakeKind.Cone: return Loc.T("锥体", "Cone");
                default: return Loc.T("轴对称", "Axisymmetric");
            }
        }
    }

    /// <summary>进气道算出来的东西：总压恢复、迎面马赫、捕获面积、以及"这台机器的进气道配不配"。</summary>
    public struct IntakeState
    {
        public bool Ok;
        public IntakeKind Kind;     // 这份结果对应哪种型式（以前是静态字段，交错评估会串型）
        public RecoveryModel Model; // 这份结果的超声速段用的是哪套模型
        public bool Started;        // 激波挂住了没有（Empirical 模式恒 true）
        public double Tpr;          // 总压恢复系数
        public double FaceMach;     // 迎面马赫（决定流量）
        public double ThroatArea;   // 喉道面积 m²（决定流量）
        public double CaptureArea;  // 唇口捕获面积 m²（只进外形/报表）
        public double FlowFactor;   // 抽气之后还剩多少流量
        public string Note;
        public string Warning;      // 空 = 没问题

        public string Describe()
        {
            return string.Format(Loc.T("进气道 {0}｜恢复 {1:F3}｜迎面 Ma {2:F2}｜喉道 ⌀{3:F2} m｜捕获 ⌀{4:F2} m｜流量 ×{5:F2}{6}{7}", "Intake {0} | recovery {1:F3} | face Ma {2:F2} | throat dia {3:F2} m | capture dia {4:F2} m | flow x{5:F2}{6}{7}"),
                Intake.KindNameOf(Kind), Tpr, FaceMach,
                Math.Sqrt(4.0 * ThroatArea / Math.PI), Math.Sqrt(4.0 * CaptureArea / Math.PI), FlowFactor,
                Started ? "" : Loc.T("｜未起动", " | not started"),
                string.IsNullOrEmpty(Warning) ? "" : "｜" + Warning);
        }
    }

    /// <summary>
    /// 进气道求解：给定参数、来流，算总压恢复与迎面马赫。
    ///
    /// 物理口径（都要能对着真机讲清理由，不是凑出来的曲线）：
    ///
    ///  · 亚声速段：四种型式都几乎无损失（0.96~0.99）。差别在**管道转了多少弯**：
    ///    皮托是直管（0.985），轴对称要绕中心体（0.975），斜板/锥体要把气流掰进矩形/环形管道（0.965）。
    ///
    ///  · 超声速段：损失来自激波系。同样是 M2，一道正激波（皮托）比"两道斜激波 + 一道正激波"
    ///    （斜板/锥体）损失大得多——这就是为什么超声速一定要外压缩。
    ///    这里用 MIL-E-5008B 的幂次 `1 − 0.075(M−1)^1.35` 当基准（轴对称），
    ///    再按型式给一个"外压缩做得好不好"的增益/惩罚：
    ///      皮托  −0.06·(M−1)^1.5   只能吃正激波，越往上越糟
    ///      斜板  +0.05·(M−1.5)^0.9 M>1.5 才开始吃到好处（低速时斜板只是多绕的弯）
    ///      锥体  +0.04·(M−1.5)^0.9 比斜板略差（锥面激波在非设计点更敏感）
    ///    系数都是量级估计，写在这里而不是埋在调用点，就是为了以后有了真机数据只改这一处。
    ///
    ///  · 抽气：`FlowFactor = 1 − BleedFrac`。抽掉的气不参与做功，但它换来的是进气道不喘——
    ///    这一层现在只体现为"流量打折"，还没做喘振裕度收益（等自定义进气道真正接进解算再说）。
    /// </summary>
    public static class Intake
    {
        public static string KindNameOf(IntakeKind k) { return IntakeSpec.KindName(k); }

        /// <summary>
        /// 喉道面积（决定流量）。
        ///
        /// 口径说明：老代码里 `aIn = π(D/2)²` 一直就是**风扇迎面 = 喉道**，不是唇口捕获面积。
        /// 所以这里分成两个数：
        ///   玩家填了捕获直径 → 喉道 = 捕获 × 喉道比（他真有唇口图纸）
        ///   没填            → 喉道 = π(D/2)²，与老代码逐位相同（老存档数字一分不动）
        /// </summary>
        public static double ThroatAreaOf(EngineDesignData d)
        {
            if (d == null) return 0.0;
            IntakeSpec sp = d.Intake != null ? d.Intake.Resolved() : new IntakeSpec().Resolved();
            double dia = sp.CaptureDiameter;
            if (dia <= 0.0)
            {
                dia = d.HasFan ? d.Fan.Diameter : d.InletDiameter;
                if (dia <= 0.0) dia = 0.5;
                return Math.PI * Math.Pow(dia / 2.0, 2);      // 老口径：迎面就是喉道
            }
            return Math.PI * Math.Pow(dia / 2.0, 2) * Math.Max(0.2, Math.Min(1.2, sp.ThroatRatio));
        }

        /// <summary>捕获直径：玩家填了就用；没填按风扇/进气口径（与 ThroatAreaOf 老口径同一个直径）。</summary>
        public static double CaptureDiameterOf(EngineDesignData d)
        {
            if (d == null) return 0.0;
            IntakeSpec sp = d.Intake != null ? d.Intake.Resolved() : new IntakeSpec().Resolved();
            if (sp.CaptureDiameter > 0.0) return sp.CaptureDiameter;
            double dia = d.HasFan ? d.Fan.Diameter : d.InletDiameter;
            return dia > 0.0 ? dia : 0.5;
        }

        /// <summary>唇口捕获面积（只进外形与报表，不直接决定流量）：喉道 / 喉道比。</summary>
        public static double CaptureAreaOf(EngineDesignData d)
        {
            if (d == null) return 0.0;
            IntakeSpec sp = d.Intake != null ? d.Intake.Resolved() : new IntakeSpec().Resolved();
            double ratio = Math.Max(0.2, Math.Min(1.2, sp.ThroatRatio));
            return ThroatAreaOf(d) / ratio;
        }

        /// <summary>
        /// 横向角（度）：迎角与侧滑合成一个角度。侧滑打七折——侧滑的流场不对称度比抬头小。
        /// 0 = 正对来流，这也是缺省，所以老存档这条路一个数都不动。
        /// </summary>
        public static double CrossflowDeg(double alphaDeg, double betaDeg)
        {
            double a = GasState.IsFinite(alphaDeg) ? alphaDeg : 0.0;
            double b = GasState.IsFinite(betaDeg) ? betaDeg : 0.0;
            double x = Math.Sqrt(a * a + 0.7 * b * b);
            return Math.Min(Math.Max(x, 0.0), MaxIncidenceDeg);
        }

        /// <summary>横向角的量程上限（度）。再往上不是"性能下降"，是"这台发动机在飞一个进气道不该飞的姿势"。</summary>
        public const double MaxIncidenceDeg = 24.0;

        /// <summary>
        /// 横向角带来的总压恢复折减。**0° 必须返回正好 1.0**（乘 1 是精确的，读数逐位不变）。
        /// 亚声速型式每度 0.12%（薄唇口还能兜住斜着来的气流）；
        /// 斜板/锥体每度 0.35%（激波系跟来流方向走，斜一点就撞车）。
        /// </summary>
        public static double IncidenceLossFactor(IntakeKind kind, double crossflowDeg)
        {
            double x = GasState.IsFinite(crossflowDeg) ? Math.Max(crossflowDeg, 0.0) : 0.0;
            if (x <= 0.0) return 1.0;
            double perDeg = (kind == IntakeKind.Ramp || kind == IntakeKind.Cone) ? 0.0035 : 0.0012;
            return Math.Min(Math.Max(1.0 - perDeg * x, 0.80), 1.0);
        }

        /// <summary>横向角过大时的提示。只在真的会咬人的时候说，别每条都念一遍。</summary>
        public static string IncidenceWarning(IntakeKind kind, double crossflowDeg, double mach)
        {
            double x = GasState.IsFinite(crossflowDeg) ? crossflowDeg : 0.0;
            bool compress = kind == IntakeKind.Ramp || kind == IntakeKind.Cone;
            double limit = compress ? 12.0 : 18.0;
            if (x < limit) return null;
            if (mach > 1.2 && compress)
                return string.Format(Loc.T("横向角 {0:F0}°：斜板/锥体的激波系已经对不上来流，唇口一侧会分离——这个姿态下别指望恢复系数", "Crossflow {0:F0} deg: the ramp or cone shock system no longer matches the flow and one side of the lip separates - do not count on recovery in this attitude"), x);
            return string.Format(Loc.T("横向角 {0:F0}°：喉道一侧的唇口内壁开始分离，进气道在吞自己的尾流", "Crossflow {0:F0} deg: the lip inner wall on one side of the throat starts to separate and the intake is swallowing its own wake"), x);
        }

        /// <summary>亚声速（含 M=1.0 那一点）的管道恢复。四型式共用一份，Shock 模式也拿它当管道项。</summary>
        private static double DuctRecovery(IntakeSpec sp, TechLevel tech)
        {
            switch (sp.Kind)
            {
                case IntakeKind.Pitot: return tech.IntakeTprSub * 0.995;   // 0.985 左右
                case IntakeKind.Ramp: return tech.IntakeTprSub * 0.975;
                case IntakeKind.Cone: return tech.IntakeTprSub * 0.975;
                default: return tech.IntakeTprSub;                          // 轴对称：旧行为
            }
        }

        /// <summary>
        /// 压缩面每级的偏转角（度）。由**设计马赫**定，与 `IntakeGeometry.BuildCenterBody` 画外形用的是同一个公式——
        /// 图上画的锥角必须就是性能里算的那个角，否则"按形状算性能"是假的。
        /// </summary>
        public static double RampHalfAngleDeg(IntakeKind kind, double designMach)
        {
            if (kind == IntakeKind.Pitot) return 0.0;      // 皮托没有压缩面
            // 0 或非有限 = 没给设计马赫：与 BuildCenterBody 一样按 0.9 处理，
            // 不在这里另编一个缺省值，否则图和数就是两个角。
            double md = GasState.IsFinite(designMach) && designMach > 0.0 ? designMach : 0.9;
            return Math.Min(Math.Max(8.0 + 5.0 * (md - 1.0), 6.0), 20.0);
        }

        /// <summary>可调几何开始作动的马赫（低于它锥体不动）。</summary>
        public const double VariableGeometryOn = 1.20;
        /// <summary>可调几何走完全行程的马赫。</summary>
        public const double VariableGeometryFull = 2.60;
        /// <summary>全行程时喉道面积收窄的比例（0.18 = 收掉 18%）。</summary>
        public const double VariableGeometryThroatLoss = 0.18;

        /// <summary>
        /// 可调几何在某个马赫下把喉道环收窄到原来的几成（1.0 = 没动）。
        ///
        /// 只对锥体/斜板有效——皮托与轴对称没有可动压缩面。
        /// 1.20 以下不动（亚声速锥体前移只会多挡路），1.20~2.60 线性走完全行程，
        /// 2.60 以上保持最小喉道。这是"机构"的行为，不是逼近某台真机：
        /// 真机的行程是按自家激波系配的，本工程没有那份数据。
        /// </summary>
        public static double ThroatFactor(IntakeSpec spec, double mach)
        {
            if (spec == null || !spec.VariableGeometry) return 1.0;
            if (spec.Kind != IntakeKind.Cone && spec.Kind != IntakeKind.Ramp) return 1.0;
            if (!GasState.IsFinite(mach) || mach <= VariableGeometryOn) return 1.0;
            double t = (mach - VariableGeometryOn) / (VariableGeometryFull - VariableGeometryOn);
            if (t > 1.0) t = 1.0;
            return 1.0 - VariableGeometryThroatLoss * t;
        }

        /// <summary>
        /// 可调压缩面的作动系统质量（kg）：作动筒 + 滑轨 + 铰链 + 液压管路。
        ///
        /// 公式放在 Core 而不是零件模块里，是为了让回归能直接断言它——
        /// 零件模块引用 UnityEngine，测试工程编不进去（§104.3 踩过）。
        /// 系数 18 kg 是两套作动筒与安装座的底数，45 kg/m² 是"作动筒推力随捕获面积涨"。
        /// 0.45 m² 的进气道落在 38 kg 左右，一台摩托车的量级。
        /// </summary>
        public static double VariableGeometryMassKg(double captureArea)
        {
            double a = GasState.IsFinite(captureArea) && captureArea > 0.0 ? captureArea : 0.0;
            return 18.0 + 45.0 * a;
        }

        /// <summary>可调压缩面的作动系统造价（funds）：贵在精度与检测，不在材料。</summary>
        public static double VariableGeometryCost(double captureArea)
        {
            double a = GasState.IsFinite(captureArea) && captureArea > 0.0 ? captureArea : 0.0;
            return 250.0 + 700.0 * a;
        }

        /// <summary>
        /// 这台机器该按哪个马赫定压缩角。整机用 `DesignMach`；**独立零件没有设计马赫**，
        /// 就只能拿它自己的设计迎面马赫反推一个（`1 + FaceMach`）——这样画的锥角和算的锥角
        /// 是同一个数，不会出现"图上 12°、性能里当 15°"。
        /// </summary>
        public static double DesignMachOf(IntakeSpec spec, double engineDesignMach)
        {
            // 整机把真实的设计马赫原样传进来（哪怕是 0 = 没设），RampHalfAngleDeg 自己会落到 0.9。
            if (GasState.IsFinite(engineDesignMach)) return engineDesignMach;
            IntakeSpec sp = (spec ?? new IntakeSpec()).Resolved();
            double face = sp.FaceMachDesign > 0.0 ? sp.FaceMachDesign : 0.5;
            return Math.Min(Math.Max(1.0 + face, 1.05), 2.6);
        }

        /// <summary>
        /// 激波挂住了没有。挂不住 = 未起动/亚临界：气流在唇口外就怂了，多余的那部分流量只能溢掉。
        /// Empirical 模式恒 true（那条曲线里没有"起动"这个概念，别凭空给老口径加戏）。
        /// </summary>
        /// <summary>有自己画的型线吗（自定义进气道）。有的话型式、几个比例栏都不再参与计算。</summary>
        public static bool HasProfile(IntakeSpec spec)
        {
            if (spec == null || spec.Shape == null || spec.Shape.Sections == null ||
                spec.Shape.Sections.Count < IntakeProfile.MinSections) return false;
            // 量不出来就别装作有：坏型线（NaN、顺序乱、截面 0 宽）必须退回老口径，
            // 否则压缩角/级数会带着 NaN 一路走进激波解。
            string why;
            return spec.Shape.Validate(out why);
        }

        /// <summary>
        /// 这套进气道的压缩角（度，总转角）。有型线就从内壁量出来，没有才退回"按型式与设计马赫估"。
        /// 性能与起动判定都必须走这一个入口——两处各算一个角，就会出现"面板说没脱体、解算器说脱体"。
        /// </summary>
        public static double CompressionAngle(IntakeSpec spec, double designMach)
        {
            if (HasProfile(spec)) return spec.Shape.CompressionDeg;
            return RampHalfAngleDeg(spec != null ? spec.Kind : IntakeKind.Axial, designMach);
        }

        /// <summary>压缩级数（=几道斜激波）。型线看内壁折点，老口径看型式（斜板两级、别的单级）。</summary>
        public static int CompressionStageCount(IntakeSpec spec)
        {
            if (HasProfile(spec)) return Math.Min(Math.Max(spec.Shape.CompressionStages, 1), 3);
            if (spec == null) return 1;
            return spec.Kind == IntakeKind.Ramp ? 2 : 1;
        }

        public static bool Started(IntakeSpec spec, double mach, double designMach)
        {
            IntakeSpec sp = (spec ?? new IntakeSpec()).Resolved();
            if (sp.Model != RecoveryModel.Shock || mach <= 1.0) return true;
            if (!HasProfile(sp) && sp.Kind == IntakeKind.Pitot) return true;   // 只吃正激波，没有"脱体"这回事
            double half = CompressionAngle(sp, designMach);
            if (half <= 1e-6) return true;                                     // 没有压缩面就没有脱体
            double thetaMax = Shock.MaxDeflectionDeg(mach);
            return thetaMax > 1e-3 && half <= thetaMax;
        }

        /// <summary>
        /// 真激波解。流程：
        ///   皮托   → 一道正激波
        ///   轴对称 → 单锥激波 + 结尾正激波
        ///   锥体   → 锥激波 + 唇口再压一次（0.6 倍角）+ 结尾正激波
        ///   斜板   → 两级斜激波 + 结尾正激波
        /// 最后统一乘亚声速管道项，这样 M=1.0 两侧严丝合缝（M=1 时激波损失为 1）。
        /// 几何角挂不住（`half > θmax`）→ 按脱体处理：正激波 + 额外 8% 损失。
        /// </summary>
        public static double ShockRecovery(IntakeSpec spec, TechLevel tech, double mach, double designMach)
        {
            IntakeSpec sp = (spec ?? new IntakeSpec()).Resolved();
            if (tech == null) tech = new TechLevel();
            double duct = DuctRecovery(sp, tech);
            if (!GasState.IsFinite(mach) || mach <= 1.0) return duct;

            double md = GasState.IsFinite(designMach) && designMach > 1.0 ? designMach : mach;
            double half = CompressionAngle(sp, md);
            int stages = CompressionStageCount(sp);
            // 脱体：正激波 + 溢流/分离的额外损失。
            // 那个额外损失必须随马赫长出来：M→1 时正激波本身几乎没有损失，
            // 写死 0.92 会让 0.999→1.001 之间凭空跳掉 8%（面板上滑一下马赫就掉一格恢复）。
            double bowPenalty = 1.0 - 0.10 * Math.Min(Math.Max((mach - 1.0) / 1.0, 0.0), 1.0);
            double bow = Shock.NormalShockPtRatio(mach) * bowPenalty;
            double pt;

            if (HasProfile(sp))
            {
                // 自己画的进气道：形状说了算，型式不再参与。
                // 有中心体且只有一个折点 → 按锥激波算（轴对称件就是一根锥）；
                // 多折点或扁口（上下唇各一道斜板）→ 按多级斜压缩算。
                if (!Started(sp, mach, md)) pt = bow;
                else if (sp.Shape.HasSpinner && stages <= 1)
                {
                    double m2, cone = Shock.ConeShockPtRatio(mach, half, out m2);
                    pt = cone < 0.0 ? bow : cone * Shock.NormalShockPtRatio(m2);
                }
                else
                {
                    double mAt, ratio;
                    pt = Shock.CompressionsTotal(mach, half, stages, out mAt, out ratio) ? ratio : bow;
                }
            }
            else if (sp.Kind == IntakeKind.Pitot)
            {
                pt = Shock.NormalShockPtRatio(mach);
            }
            else if (!Started(sp, mach, md))
            {
                pt = bow;
            }
            else if (sp.Kind == IntakeKind.Ramp)
            {
                double mAt, ratio;
                // half 是这套压缩面的**总**转角，两级按级均分（每级 6.5° 而不是每级 13°）
                pt = Shock.CompressionsTotal(mach, half, 2, out mAt, out ratio) ? ratio : bow;
            }
            else if (sp.Kind == IntakeKind.Cone)
            {
                double m2, cone = Shock.ConeShockPtRatio(mach, half, out m2);
                if (cone < 0.0) { pt = bow; }
                else
                {
                    // 唇口那道二级压缩：锥后流场还没完全对正，再压 0.6 倍角
                    double beta, m3, pt3;
                    if (m2 > 1.05 && Shock.ObliqueShock(m2, half * 0.6, out beta, out m3, out pt3))
                    {
                        cone *= pt3; m2 = m3;
                    }
                    pt = cone * Shock.NormalShockPtRatio(m2);
                }
            }
            else   // Axial：一根固定中心锥，单级
            {
                double m2, cone = Shock.ConeShockPtRatio(mach, half, out m2);
                pt = cone < 0.0 ? bow : cone * Shock.NormalShockPtRatio(m2);
            }

            double tpr = pt * duct;
            if (tpr > 0.99) tpr = 0.99;
            if (tpr < 0.30) tpr = 0.30;      // 比 0.30 低已经不是"恢复差"，是这台机器根本不该在这里飞
            return tpr;
        }

        /// <summary>总压恢复系数（随马赫连续）。`null` 的 spec 走轴对称缺省，等价于旧行为。</summary>
        public static double Recovery(IntakeSpec spec, TechLevel tech, double mach)
        {
            return Recovery(spec, tech, mach, double.NaN);
        }

        /// <summary>
        /// 同上，额外给一个**设计马赫**：Shock 模式要靠它定压缩角（几何是死的，来流是活的）。
        /// 传 NaN = 按当前马赫当设计点，等于"这台机器就是为这个马赫设计的"（零件没有设计马赫时用这个）。
        /// </summary>
        public static double Recovery(IntakeSpec spec, TechLevel tech, double mach, double designMach)
        {
            IntakeSpec sp = (spec ?? new IntakeSpec()).Resolved();
            if (tech == null) tech = new TechLevel();

            if (mach <= 1.0) return DuctRecovery(sp, tech);
            if (sp.Model == RecoveryModel.Shock) return ShockRecovery(sp, tech, mach, designMach);

            double baseRec = tech.IntakeTpr(mach);
            double x = mach - 1.0;
            double tpr;
            switch (sp.Kind)
            {
                case IntakeKind.Pitot:
                    tpr = baseRec - 0.06 * Math.Pow(x, 1.5);
                    break;
                case IntakeKind.Ramp:
                    tpr = baseRec + 0.05 * (mach > 1.5 ? Math.Pow(mach - 1.5, 0.9) : 0.0);
                    break;
                case IntakeKind.Cone:
                    tpr = baseRec + 0.04 * (mach > 1.5 ? Math.Pow(mach - 1.5, 0.9) : 0.0);
                    break;
                default:
                    tpr = baseRec;
                    break;
            }
            if (tpr > 0.99) tpr = 0.99;
            if (tpr < 0.55) tpr = 0.55;     // 再低已经不是"恢复差"，是根本点不着
            return tpr;
        }

        /// <summary>
        /// 主入口。`faceMachOverride &gt; 0` 时用它（简单模式要固定的 0.5 迎面马赫）。
        /// </summary>
        public static IntakeState Evaluate(EngineDesignData d, double mach, double faceMachOverride)
        {
            IntakeState st = new IntakeState();
            IntakeSpec sp = d != null && d.Intake != null ? d.Intake.Resolved() : new IntakeSpec().Resolved();
            TechLevel tech = d != null && d.Tech != null ? d.Tech : new TechLevel();
            double md = d != null ? d.DesignMach : double.NaN;

            st.Kind = sp.Kind;
            st.Model = sp.Model;
            st.Tpr = Recovery(sp, tech, mach, md);
            st.FaceMach = faceMachOverride > 0.0 ? faceMachOverride : sp.FaceMachDesign;
            st.ThroatArea = d != null ? ThroatAreaOf(d) : 0.0;
            st.CaptureArea = d != null ? CaptureAreaOf(d) : 0.0;
            double bleed = sp.BleedFrac < 0.0 ? 0.0 : (sp.BleedFrac > 0.15 ? 0.15 : sp.BleedFrac);
            st.FlowFactor = 1.0 - bleed;
            st.Started = Started(sp, mach, md);
            st.Note = string.Format(Loc.T("{0}｜喉道/捕获 {1:F2}{2}", "{0} | throat/capture {1:F2}{2}"), IntakeSpec.KindName(sp.Kind), sp.ThroatRatio,
                sp.Model == RecoveryModel.Shock
                    ? string.Format(Loc.T("｜激波 {0:F1}° / θmax {1:F1}°", " | shock {0:F1} deg / theta max {1:F1} deg"), RampHalfAngleDeg(sp.Kind, md), Shock.MaxDeflectionDeg(mach))
                    : Loc.T("｜经验曲线", " | empirical curve"));

            // 合法性：以前这里无条件 Ok=true，于是"面积算成 0"和"一切都好"长得一模一样，
            // 下游拿着 0 面积去算流量，算出 0 推力，报表上只说"飞不动"。现在把话说清楚。
            st.Ok = true;
            if (d != null)
            {
                if (!GasState.IsFinite(st.ThroatArea) || st.ThroatArea <= 0.0)
                {
                    st.Ok = false;
                    st.Warning = string.Format(Loc.T("喉道面积 {0:F3} m²（不是正数），进气道吞不了任何流量", "Throat area {0:F3} m2 (not positive): the intake cannot swallow any flow"), st.ThroatArea);
                }
                else if (!GasState.IsFinite(st.CaptureArea) || st.CaptureArea <= 0.0)
                {
                    st.Ok = false;
                    st.Warning = string.Format(Loc.T("捕获面积 {0:F3} m²（不是正数），唇口没张开", "Capture area {0:F3} m2 (not positive): the lip never opened"), st.CaptureArea);
                }
                else if (!GasState.IsFinite(st.Tpr) || st.Tpr <= 0.0 || st.Tpr > 1.0)
                {
                    st.Ok = false;
                    st.Warning = string.Format(Loc.T("总压恢复 {0:F3} 不在 (0,1] 里，这是个算错而不是性能差", "Total pressure recovery {0:F3} is outside (0,1] - that is a miscalculation, not poor performance"), st.Tpr);
                }
                else if (!GasState.IsFinite(st.FaceMach) || st.FaceMach <= 0.0)
                {
                    st.Ok = false;
                    st.Warning = string.Format(Loc.T("迎面马赫 {0:F3}（不是正数），流量公式没法用", "Face Mach {0:F3} (not positive): the flow equation cannot be used"), st.FaceMach);
                }
            }
            if (st.Ok)
            {
                st.Warning = Mismatch(d, sp);
                if (!st.Started)
                    st.Warning = string.Format(Loc.T("进气道未起动：几何压缩角 {0:F1}° 超过这个马赫的 θmax {1:F1}°，激波脱体，恢复按正激波算（{2:F3}）", "Intake not started: the geometric compression angle {0:F1} deg exceeds theta max {1:F1} deg at this Mach, the shock detaches and recovery falls back to a normal shock ({2:F3})"),
                        RampHalfAngleDeg(sp.Kind, md), Shock.MaxDeflectionDeg(mach), st.Tpr);
            }
            return st;
        }

        /// <summary>
        /// 型式与设计马赫配不配。
        ///
        /// 这不是"性能好不好"的判断，是"这东西装在这儿不合理"：亚音速机装斜板只是死重，
        /// 超声速机装皮托等于拿一道正激波去换流量。所以只报警，不改数。
        /// </summary>
        public static string Mismatch(EngineDesignData d, IntakeSpec sp)
        {
            if (d == null || sp == null) return null;
            double md = d.DesignMach;
            if ((sp.Kind == IntakeKind.Ramp || sp.Kind == IntakeKind.Cone) && md < 1.2)
                return string.Format(Loc.T("{0}进气道在 Ma {1:F2} 的设计点上吃得亏：斜板/锥体要到 Ma 1.5 以上才开始划算", "A {0} intake loses at a Ma {1:F2} design point: ramps and cones only pay off above about Ma 1.5"),
                    IntakeSpec.KindName(sp.Kind), md);
            if (sp.Kind == IntakeKind.Pitot && md > 1.6)
                return string.Format(Loc.T("皮托进气道在 Ma {0:F2} 的设计点上只剩正激波可用：恢复系数会掉到 {1:F2} 以下", "At a Ma {0:F2} design point a Pitot intake has only the normal shock left: recovery drops below {1:F2}"),
                    md, Recovery(sp, d.Tech, md));
            if (sp.ThroatRatio > 0.0 && (sp.ThroatRatio < 0.60 || sp.ThroatRatio > 1.05))
                return string.Format(Loc.T("喉道/捕获 = {0:F2}：小于 0.60 会憋流量，大于 1.05 是喇叭口", "Throat/capture = {0:F2}: below 0.60 it chokes the flow, above 1.05 it is a bell mouth"), sp.ThroatRatio);
            return null;
        }
    }
}