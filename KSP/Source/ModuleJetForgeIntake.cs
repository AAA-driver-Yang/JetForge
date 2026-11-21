using System;
using JetForge.Core;
using JetForge.UI;
using UnityEngine;

namespace JetForge.KSP
{
    /// <summary>
    /// 独立的进气道零件。
    ///
    /// 玩家要的是"能装在飞机上的、不同于发动机的部件"：它有自己的零件、自己的质量造价、
    /// 自己的存档节点、自己的设计面板，**不是**发动机里的一个字段。
    ///
    /// 与发动机的联系走 <see cref="IntakeLink"/>：发动机每帧按距离把附近的进气道合起来算
    /// （总面积、加权总压恢复、最差畸变、合计可用流量），进气道自己完全不知道被谁在用。
    /// 所以拆掉进气道发动机能飞（退回自带口径），拆掉发动机进气道也还在。
    /// </summary>
    public class ModuleJetForgeIntake : PartModule, IPartMassModifier, IPartCostModifier
    {
        /// <summary>存档节点名。与发动机的 JETFORGE_DESIGN 是**两个不同的**节点。</summary>
        public const string NodeName = "JETFORGE_INTAKE";

        [KSPField(isPersistant = true)]
        public string intakeName = "JF 进气道";

        /// <summary>这件进气道出口（发动机面）直径。0 = 按自己的喉道。</summary>
        [KSPField(isPersistant = true)]
        public double exitDiameter = 0.0;

        /// <summary>进气道长度（米）。外形和"唇口到发动机面"的轴长都由它定。</summary>
        [KSPField(isPersistant = true)]
        public double length = 1.2;

        /// <summary>技术代：决定恢复系数曲线（与发动机同代才谈得上匹配）。</summary>
        [KSPField(isPersistant = true)]
        public string techName = "1970s";

        /// <summary>这件进气道的设计。与发动机设计里的 IntakeSpec 是两份独立数据。</summary>
        public IntakeSpec Spec = new IntakeSpec();

        public TechLevel Tech { get { return TechLevel.Get(techName); } }

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(intakeName) ? "JF 进气道" : intakeName; }
        }

        /// <summary>补齐缺省之后的规格（玩家没填的项按型式给）。</summary>
        public IntakeSpec ResolvedSpec
        {
            get { return Spec != null ? Spec.Resolved() : new IntakeSpec().Resolved(); }
        }

        /// <summary>捕获直径。零件没填就按 0.6 m（不与发动机外形挂钩——它本来就不在发动机里）。</summary>
        public double CaptureDiameter
        {
            get
            {
                double d = ResolvedSpec.CaptureDiameter;
                return d > 0.0 ? d : 0.6;
            }
        }

        public double CaptureArea
        {
            get { double d = CaptureDiameter; return Math.PI * 0.25 * d * d; }
        }

        public double ThroatArea
        {
            get
            {
                double ratio = Math.Min(Math.Max(ResolvedSpec.ThroatRatio, 0.2), 1.2);
                return CaptureArea * ratio;
            }
        }

        /// <summary>发动机面直径：没填就按喉道（一件管子只送气，不逼玩家填第二个直径）。</summary>
        public double FaceDiameter
        {
            get
            {
                if (exitDiameter > 0.0 && !double.IsNaN(exitDiameter)) return exitDiameter;
                double a = ThroatArea;
                return a > 1e-9 ? Math.Sqrt(4.0 * a / Math.PI) : 0.2;
            }
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (Spec == null) Spec = new IntakeSpec();
            RefreshMesh();
            _stamp = CurrentStamp();
        }

        /// <summary>
        /// 上一次建网格时的三层指纹。分层的意义：几何没变就别重建网格，
        /// 性能没变就别重算性能网格，表现没变就别重分材质槽。
        /// 见 <see cref="RefreshPlan"/>。
        /// </summary>
        private RefreshStamp _stamp;
        private int _stampAge;

        private RefreshStamp CurrentStamp()
        {
            return new RefreshStamp(
                RefreshStamps.IntakeGeometry(Spec, FaceDiameter, length),
                RefreshStamps.IntakePerformance(Spec, Tech),
                RefreshStamps.IntakePresentation(Spec, "5slot"));
        }

        /// <summary>
        /// 按指纹决定要不要重建。返回这次的结论（面板与自检读它，看得见"为什么没重建"）。
        /// 只读操作：几何变了才动网格，其余层这里不碰（本零件没有自己的性能网格）。
        /// </summary>
        public RefreshPlan RefreshIfNeeded(bool force = false)
        {
            RefreshStamp now = CurrentStamp();
            RefreshPlan plan = force ? new RefreshPlan { Layers = RefreshLayer.All, Why = "强制" } : RefreshPlan.Compare(_stamp, now);
            if (plan.Geometry)
            {
                if (part != null) JetForgeMeshBuilder.RefreshIntakeMesh(part, Spec, FaceDiameter, length);
            }
            _stamp = now;
            return plan;
        }

        /// <summary>
        /// 迎角与侧滑由**飞行状态**给，不是设计参数：这是"这一刻气流从哪个方向灌进来"。
        /// 零件自己的轴线与来流速度的夹角即迎角/侧滑——飞机要升力就有迎角，
        /// 进气道几乎从不正对来流，所以这两个数必须每帧算、不能让人填。
        /// 零件局部系：+Y 是唇口朝前，X 横向，Z 上下（与网格同约定）。
        /// 地面上/悬停时来流速度接近 0，角度无意义，清零。
        /// </summary>
        public void UpdateFlightAngles()
        {
            if (Spec == null || part == null || vessel == null) return;
            Vector3d v = vessel.srf_velocity;
            if (v.sqrMagnitude < 1.0) { Spec.AlphaDeg = 0.0; Spec.BetaDeg = 0.0; return; }
            Vector3d lv = part.transform.InverseTransformDirection(v);
            double axial = lv.y;          // 沿唇口朝前的速度分量（正常前飞时为正）
            if (Math.Abs(axial) < 1e-6 && Math.Abs(lv.x) < 1e-6 && Math.Abs(lv.z) < 1e-6) return;
            // 气流相对零件的横向/上下分量：来流从右前方来 → 侧滑为正，从左前方来 → 为负。
            double side = -lv.x;
            double vert = -lv.z;
            double ref0 = Math.Abs(axial) > 1e-6 ? axial : 1e-6;
            double alpha = Math.Atan2(vert, ref0) * 180.0 / Math.PI;
            double beta = Math.Atan2(side, ref0) * 180.0 / Math.PI;
            Spec.AlphaDeg = Math.Min(Math.Max(alpha, -20.0), 20.0);
            Spec.BetaDeg = Math.Min(Math.Max(beta, -20.0), 20.0);
        }

        /// <summary>
        /// KSP 的 PartModule 不是 MonoBehaviour：Unity 的 FixedUpdate 根本不会被调到，
        /// 要按 KSP 反射的那套名字写 OnFixedUpdate，否则迎角侧滑永远停在 0（那正是本文件
        /// 之前那个方法名的问题）。
        /// </summary>
        public override void OnFixedUpdate()
        {
            UpdateFlightAngles();
            // 几何只能从编辑器面板改：飞行中没有面板，比指纹是白花的——那一步还要拼
            // 整条型线的字符串（每条截面几十个点），飞行中每秒三次纯属给 GC 添活。
            // 所以指纹只在编辑器里比。
            if (!HighLogic.LoadedSceneIsEditor) return;
            // 每 15 帧比一次：迎角侧滑每帧都在改 Spec，但它们不在指纹里，
            // 所以正常情况下这里一直返回"三层都没变"，什么都不重建。
            if (++_stampAge < 15) return;
            _stampAge = 0;
            RefreshIfNeeded();
        }

        /// <summary>
        /// 编辑器兜底：ALT 复制 / 对称放置出来的新件会**共用原件那一份网格**（克隆零件时
        /// MeshFilter 直接抄了同一个 Mesh 实例），或者干脆没有我们的节点。
        /// 每 0.5 s 看一眼——节点不在、或者手里那份是借来的，就补建一份自己的。
        /// 只在编辑器里做（飞行中零件不会凭空复制，没必要每架飞机每秒查一遍）。
        /// </summary>
        public override void OnUpdate()
        {
            if (part == null || !HighLogic.LoadedSceneIsEditor) return;
            Transform vis = part.FindModelTransform(JetForgeMeshBuilder.IntakeVisualName);
            if (vis != null && JetForgeMeshBuilder.OwnsVisual(part, vis, JetForgeMeshBuilder.IntakeVisualName)) return;
            RefreshMesh();
        }

        /// <summary>
        /// 按当前参数重建外形。面板每次「应用」都调它——拖滑杆就是边拖边长出来，
        /// 这是"3D 化编辑"的全部意义：改喉道比能看见喉道真的收紧了。
        /// </summary>
        public void RefreshMesh()
        {
            if (part == null) return;
            JetForgeMeshBuilder.RefreshIntakeMesh(part, Spec, FaceDiameter, length);
        }

        /// <summary>几何（型线）——面板与自检读它，和网格同一份数。</summary>
        public IntakeShape Shape
        {
            get { return IntakeGeometry.OfPart(Spec, FaceDiameter, length); }
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            ConfigNode n = node.GetNode(NodeName);
            if (n == null) return;
            if (Spec == null) Spec = new IntakeSpec();
            IntakeSpecCodec.Load(n, Spec);
            // 自定义进气道**只走激波解**：老存档里那一份 `recoveryModel=Empirical` 在这一步被改掉。
            // 经验曲线是"从一堆真机读数拟合出来的平均线"，它回答不了"我这个锥角在这个马赫下会不会脱体"，
            // 而这恰恰是自己画出来的进气道最该知道的事。型式（Kind）留给原版件，自定义件的几何从模型算。
            Spec.Model = RecoveryModel.Shock;

            // 有型线就以型线为准：网格、质量、求解器读的必须是**同一条气道**。
            // 型线里的轴长是玩家拖出来的，而 cfg 里的 `length` 是上一次存盘时的旧值；
            // 两者不一致时，模型长 1.4 m、质量按 1.2 m 算、解又按 1.2 m 算——
            // 这种"三个地方各说各话"的错最难查，因为每一处单独看都合理。
            if (Spec.Shape != null && Spec.Shape.Sections != null
                && Spec.Shape.Sections.Count >= IntakeProfile.MinSections)
            {
                double pl = Spec.Shape.Length;
                if (pl > 0.05) length = pl;
            }
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            // 先删再写：同一个 module 节点会被反复存档，不删会越存越多份。
            node.RemoveNodes(NodeName);
            ConfigNode n = node.AddNode(NodeName);
            IntakeSpecCodec.Save(n, Spec ?? new IntakeSpec());
        }

        /// <summary>在给定高度/马赫下这件进气道自己的解。</summary>
        public IntakeSolution Solve(double alt, double mach)
        {
            return IntakeSolver.SolvePart(Spec, FaceDiameter, Tech, alt, mach, 0.0, double.NaN, length);
        }

        // ---------- 质量与造价：按自己的尺寸算，不跟发动机分摊 ----------

        /// <summary>
        /// 这件进气道的干重（公斤）。
        ///
        /// 以前这里是 `40*捕获面积 + 60*喉道面积 + 55*捕获面积*长度` 直接当**吨**返回了——
        /// 一个 Ø0.8 m 的进气道算出 80 t，比整台发动机重六十倍，编辑器里一挂上去
        /// 火箭的质心就飞了。错在单位：KSP 的质量是吨，公式里那些系数是按公斤写的。
        ///
        /// 现在的口径按薄壁管件估：
        /// · 蒙皮：湿表面积 × 9 kg/m²（1.2 mm 铝锂合金 + 环形加强框）
        /// · 唇口与对接法兰：按周长各 2.5 kg/m
        /// · 压缩面（斜板/锥体才有）：捕获面积 × 6 kg/m²，那是实心件不是薄壁
        /// · 作动器与传感器底座：固定 6 kg
        /// </summary>
        public double MassKg()
        {
            double dCap = CaptureDiameter;
            double dThroat = Math.Sqrt(4.0 * ThroatArea / Math.PI);
            double dExit = Math.Max(FaceDiameter, 0.05);
            double dMean = 0.25 * (dCap + dThroat + dExit + 0.5 * (dCap + dExit));
            double wetted = Math.PI * dMean * Math.Max(length, 0.1);

            double kg = 9.0 * wetted;
            kg += 2.5 * (Math.PI * dCap + Math.PI * dExit);          // 唇口环 + 对接法兰
            IntakeKind kind = ResolvedSpec.Kind;
            if (kind == IntakeKind.Ramp || kind == IntakeKind.Cone)
                kg += 6.0 * CaptureArea;                             // 压缩斜面/锥体
            kg += 6.0;                                               // 作动器底座
            // 可调压缩面不是白调的（§165）：作动筒、滑轨、铰链、液压管路都要钱要重量，
            // 而且作动筒的推力随捕获面积涨——0.45 m² 的进气道大约 38 kg，
            // 一台摩托车的量级。此前这个开关只改行程、重量一分不涨，等于免费调节。
            if (CanVary() && ResolvedSpec.VariableGeometry)
                kg += Intake.VariableGeometryMassKg(CaptureArea);
            return Math.Max(kg, 12.0);
        }

        /// <summary>这个型式能不能做可调压缩面（皮托与轴对称没有可动面）。</summary>
        public bool CanVary()
        {
            IntakeKind k = ResolvedSpec.Kind;
            return k == IntakeKind.Ramp || k == IntakeKind.Cone;
        }

        /// <summary>造价（funds）。薄壁件贵在成形与检测，按湿面积与尺寸给，不按质量。</summary>
        public double CostFunds()
        {
            double dCap = CaptureDiameter;
            double dExit = Math.Max(FaceDiameter, 0.05);
            double wetted = Math.PI * 0.5 * (dCap + dExit) * Math.Max(length, 0.1);
            double funds = 420.0 + 900.0 * wetted + 600.0 * CaptureArea;
            // 可调几何的作动系统按零件算：液压缸、滑轨、铰链、传感器，贵在精度不在材料。
            if (CanVary() && ResolvedSpec.VariableGeometry)
                funds += Intake.VariableGeometryCost(CaptureArea);
            return funds;
        }

        /// <summary>
        /// 这两个接口返回的是**增量**（吨 / funds），不是总量：Part.UpdateMass 会把返回值
        /// 加到 cfg 的 `mass` 上。所以必须"算出来的总量 − 默认值"，否则同一个数会被加两遍
        /// （发动机那边踩过一次：cfg 1.400 + 返回 1.477 = 2.877 t）。
        ///
        /// 用 CONSTANTLY 而不是 FIXED：玩家在面板里拖捕获直径/长度，质量要跟着动。
        /// FIXED 只在零件被增删时刷新，拖完滑杆零件还是旧质量——这也是"质量异常"的一半。
        /// </summary>
        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit)
        {
            return (float)(MassKg() / 1000.0 - defaultMass);
        }

        public ModifierChangeWhen GetModuleMassChangeWhen() { return ModifierChangeWhen.CONSTANTLY; }

        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit)
        {
            return (float)(CostFunds() - defaultCost);
        }

        public ModifierChangeWhen GetModuleCostChangeWhen() { return ModifierChangeWhen.CONSTANTLY; }

        // ---------- 面板 ----------

        [KSPEvent(guiName = "#autoLOC_JF_e_intake_designer", guiActiveEditor = true, guiActive = false,
                  guiActiveUnfocused = true, unfocusedRange = 8f)]
        public void OpenDesigner()
        {
            // 和发动机同一个理由：飞行中改进气道的设计没有意义（面积变了但已经吸进去的气不会重组），
            // 编辑器里改才是设计。
            if (!HighLogic.LoadedSceneIsEditor)
            {
                ScreenMessages.PostScreenMessage("进气道设计器只能在机库里用。飞行中改设计不会重建外形。",
                                                 4f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            JetForgeIntakeWindow.Open(this);
        }

        public override string GetInfo()
        {
            IntakeSpec sp = Spec != null ? Spec.Resolved() : new IntakeSpec().Resolved();
            string kind = Intake.KindNameOf(sp.Kind);
            return string.Format(
                "进气道（独立部件）\n{0}\n捕获 Ø{1:F2} m｜喉道 {2:F3} m²（喉道/捕获 {3:F2}）\n出口 Ø{4:F2} m｜技术代 {5}\n干重 {6:F0} kg｜造价 {7:F0}", 
                kind, CaptureDiameter, ThroatArea, sp.ThroatRatio, FaceDiameter,
                techName, MassKg(), CostFunds());
        }
    }
}