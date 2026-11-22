using System;
using System.Collections.Generic;
using JetForge.Core;
using JetForge.KSP;
using UnityEngine;

namespace JetForge.UI
{
    /// <summary>
    /// 独立进气道的**自己的**设计面板。
    ///
    /// 为什么不是发动机面板里加一页：那是"发动机的一个部件"。玩家要的是可以单独装、
    /// 单独调、单独拆的进气系统——一架飞机挂两件，各自型式不同、直径不同，这在一块
    /// 只能编辑"当前选中那台发动机"的面板里表达不出来。
    ///
    /// 面板改的是一份草稿（<see cref="_edit"/>），"应用"才写回零件；
    /// 取消/关闭会把草稿丢掉，与发动机设计器同一套规矩。
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class JetForgeIntakeWindow : MonoBehaviour
    {
        private const int WindowId = 0x4A51;
        private static JetForgeIntakeWindow _instance;

        private ModuleJetForgeIntake _target;
        private IntakeSpec _edit;
        private Rect _rect = new Rect(230f, 90f, 720f, 680f);
        private Vector2 _scroll;
        /// <summary>两页：0 = 改几何与机构，1 = 算出来的读数与包线。
        /// 一整条列（四十多行）挤在 600×660 里，玩家得滚三屏才能看到"我这件到底是什么样"——
        /// 而改一个滑杆又要滚回去。分成"改"和"看"两页，各自一屏放得下。</summary>
        private int _page;
        private bool _visible;
        private string _status = "";
        /// <summary>编辑中的长度（还没有应用）。</summary>
        private double _editLength = 1.2;
        /// <summary>边调边看：拖动滑杆就重建外形，不用非按「应用」。编辑器里才有意义。</summary>
        private bool _liveShape = true;
        private string _shapeStamp = "";
        /// <summary>型线的直接操作视图（侧视/俯视两页，拖点改几何）。</summary>
        private readonly IntakeProfileView _view = new IntakeProfileView();

        // 「轴对称/皮托/斜板/锥体」这张型式表原本挂在面板上让玩家选，
        // 现在自定义件的几何来自型线，型式只剩原版件内部的反推口径（StockIntakeBridge），
        // 面板这里不再需要它——留着只会让人以为"选型式"还是件要做的事。
        private static readonly double[] ProbeMachs = { 0.0, 0.6, 0.9, 1.2, 1.6, 2.0 };

        public static void Open(ModuleJetForgeIntake target)
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("JetForgeIntakeWindow");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<JetForgeIntakeWindow>();
            }
            _instance.Bind(target);
        }

        private void Bind(ModuleJetForgeIntake target)
        {
            _target = target;
            _edit = target != null && target.Spec != null ? target.Spec.Clone() : new IntakeSpec();
            _editLength = target != null && target.length > 0.05 ? target.length : 1.2;
            _shapeStamp = "";
            _visible = true;
        }

        private void Update()
        {
            // 零件被拆掉/飞船换掉时窗口不该继续指向一只空引用。
            if (_target == null || _target.part == null) { _visible = false; }
        }

        private void OnGUI()
        {
            if (!_visible || _target == null) return;
            if (!HighLogic.LoadedSceneIsEditor && !HighLogic.LoadedSceneIsFlight) return;
            _rect = GUILayout.Window(WindowId, _rect, Draw, Loc.T("JetForge · 进气道设计器", "JetForge · Intake Designer"),
                                     HighLogic.Skin != null ? HighLogic.Skin.window : GUI.skin.window,
                                     GUILayout.MinWidth(640f));
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
        }

        private void Draw(int id)
        {
            IntakeSpec sp = _edit;
            TechLevel tech = _target.Tech;

            GUILayout.BeginVertical();
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUILayout.ExpandHeight(true));

            Row(string.Format(Loc.T("零件：{0}｜技术代 {1}｜出口 Ø{2:F2} m", "Part: {0} | era {1} | exit Ø{2:F2} m"), _target.DisplayName,
                _target.techName, _target.FaceDiameter));

            // ---- 型式：自定义件没有型式可选 ----
            // 「皮托/斜板/锥体/轴对称」是**原版进气道**的分类口径：原版件的形状是零件模型定死的，
            // 我们只能从它的 machCurve 与外形反推它属于哪一类。
            // 自定义件的形状由下面那条型线直接给出——有型线就不需要"我是哪一类"这个问题，
            // 压缩角、级数、喉道比全是量出来的。所以这里只留一行说明，不再给四个开关。
            sp.Model = RecoveryModel.Shock;
            if (_edit.Shape != null && _edit.Shape.Sections.Count >= IntakeProfile.MinSections)
            {
                Row(string.Format(Loc.T("  几何来自型线（压缩角 {0:F1}°、{1} 级、弯曲 {2:F1}°），不选型式", "  Geometry comes from the profile (compression {0:F1}°, {1} stages, bend {2:F1}°) — no type to choose"),
                    _edit.Shape.CompressionDeg, _edit.Shape.CompressionStages, _edit.Shape.BendDeg));
            }
            else
            {
                Row(Loc.T("  还没有型线：下面按老口径兜底，建了型线之后这几项全部由型线量出来", "  No profile yet: the numbers below use the legacy formula; once you build a profile every one of them is measured from it"));
            }
            double dm = Intake.DesignMachOf(sp, double.NaN);
            Row(string.Format(Loc.T("  真激波（设计 M{0:F2}）：M2 的 θmax {1:F1}°、M1.5 只有 {2:F1}°，超过压缩角就脱体（恢复按正激波算）", "  Real shocks (design M{0:F2}): θmax is {1:F1}° at M2 but only {2:F1}° at M1.5; past that ramp angle the shock detaches and recovery falls back to a normal shock"),
                dm, Shock.MaxDeflectionDeg(2.0), Shock.MaxDeflectionDeg(1.5)));

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_page == 0, Loc.T("设计", "Design"), GUI.skin.button, GUILayout.Width(72f)) && _page != 0)
            {
                _page = 0;
                _scroll = Vector2.zero;
            }
            if (GUILayout.Toggle(_page == 1, Loc.T("读数", "Readouts"), GUI.skin.button, GUILayout.Width(72f)) && _page != 1)
            {
                _page = 1;
                _scroll = Vector2.zero;
            }
            GUILayout.Label(_page == 0
                ? Loc.T("改型线、中心体、机构与出口", "Profile, centre body, mechanism and exit")
                : Loc.T("算出来的数、海平面包线与装机情况", "Computed numbers, sea-level envelope and the craft"),
                GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            GUILayout.Space(2f);

            if (_page == 0)
            {

            // ---- 型线：拖点改几何（多自由度，不再是滑杆） ----
            //
            // 这里以前是七根滑杆（捕获直径/喉道比/弯曲角/…），每根各改一个"比例"，
            // 彼此之间没有几何关系——玩家没法回答"我这个唇口到底长什么样"。
            // 现在换成一条截面序列：拖边缘改粗细、拖中心改上下、横向拖改位置，
            // 捕获面积、喉道比、压缩角、弯曲角全部由这条型线**量**出来（IntakeProfile）。
            GUILayout.Space(6f);
            Row(Loc.T("—— 型线：截面摆在哪儿 ——", "— Profile: where the sections sit —"));
            Row(Loc.T("拖中心改偏移｜横向拖改轴向｜边缘点横着拖改倾角（能做出斜的进气口）｜Shift 微调；截面长什么样，在「编辑截面轮廓」窗口里改", "Drag the centre to offset it | drag sideways to move it axially | drag an edge dot sideways to tilt the section (that is how you build a slanted intake) | hold Shift for fine steps. The shape of a section is edited in its own section-outline window"));
            EnsureProfile();
            IntakeProfile prof = _edit.Shape;
            if (prof != null)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_view.TopView ? Loc.T("看侧视", "Side view") : Loc.T("看俯视", "Top view"), GUILayout.Width(78f)))
                    _view.TopView = !_view.TopView;
                if (GUILayout.Button(Loc.T("加截面", "Add"), GUILayout.Width(66f))) AddSection(prof);
                bool canDelete = prof.Sections.Count > IntakeProfile.MinSections;
                if (GUILayout.Button(Loc.T("删截面", "Delete"), GUILayout.Width(66f)) && canDelete)
                {
                    DeleteSection(prof);
                    SyncProfile(prof);
                }
                if (GUILayout.Button(Loc.T("重置型线", "Reset"), GUILayout.Width(76f)))
                {
                    _edit.Shape = IntakeProfile.Default(Math.Max(_editLength, 0.4), CaptureBase());
                    prof = _edit.Shape;
                    _view.Selected = 0;
                    SyncProfile(prof);
                }
                GUILayout.Label(Loc.T("选中哪段就编辑哪段", "Whichever section is selected is the one you edit"), GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                // 骨架拖动搬去自己的窗口了（见 JetForgeProfileWindow）。
                // 以前这里嵌一张 360×210 的缩略图，进了滚动区就被压扁：
                // 横向是"整条型线的长度/像素"，纵向是"最大半高度/像素"，两套比例。
                // 于是往上拖与往左拖走的不是同一个换算，量程还随着手上的那个点在变——
                // 玩家看到的"拖一下就乱跳"就是这么来的。窗口里横竖同一比例，问题从根上没有了。
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Loc.T("打开型线骨架窗口…", "Open profile window…"), GUILayout.Width(280f)))
                {
                    int openAt = Mathf.Clamp(_view.Selected, 0, prof.Sections.Count - 1);
                    IntakeProfile opened = prof;
                    JetForgeProfileWindow.Open(opened, openAt,
                        delegate { SyncProfile(opened); },
                        delegate { return _edit.Shape == opened; },
                        delegate (int i) { _view.Selected = i; });
                }
                GUILayout.Label(JetForgeProfileWindow.IsOpenFor(prof) ? Loc.T("（窗口已开，拖完回来看读数）", "(window open — come back here for the readouts when you are done)") : Loc.T("（还没开）", "(not open)"),
                    GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                int sel = Mathf.Clamp(_view.Selected, 0, prof.Sections.Count - 1);
                IntakeSection sec = prof.Sections[sel];

                // 截面本身长什么样，在它自己的窗口里改（正方形画布、逐点拖）。
                // 这里只留一句话加一个入口：主面板本来就很长，再塞一张缩略图进去，
                // 放进滚动区会被连文字一起压扁，圆截面画成椭圆、珠子还拖不动。
                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Loc.T("‹ 上一段", "‹ Prev"), GUILayout.Width(70f)))
                {
                    _view.Selected = Mathf.Clamp(_view.Selected - 1, 0, prof.Sections.Count - 1);
                    _view.MarkDirty();
                }
                if (GUILayout.Button(Loc.T("下一段 ›", "Next ›"), GUILayout.Width(70f)))
                {
                    _view.Selected = Mathf.Clamp(_view.Selected + 1, 0, prof.Sections.Count - 1);
                    _view.MarkDirty();
                }
                if (GUILayout.Button(string.Format(Loc.T("编辑截面 {0}/{1} 的轮廓…", "Edit outline of section {0}/{1}…"), sel + 1, prof.Sections.Count),
                        GUILayout.Width(170f)))
                {
                    int openAt = sel;
                    IntakeProfile opened = prof;
                    JetForgeSectionWindow.Open(opened, openAt, delegate
                    {
                        SyncProfile(opened);
                    },
                    delegate { return _edit.Shape == opened; });
                }
                GUILayout.Label(string.Format(Loc.T("（宽 {0:F3} × 高 {1:F3} m，中心 {2:F3}, {3:F3}）", "(w {0:F3} × h {1:F3} m, centre {2:F3}, {3:F3})"),
                    sec.Width, sec.Height, sec.OffsetZ, sec.OffsetY));
                GUILayout.EndHorizontal();

                Row(string.Format(Loc.T("  量出来：捕获 Ø{0:F2} → 喉道 Ø{1:F2}（比 {2:F2}）→ 出口 Ø{3:F2}｜轴长 {4:F2} m", "  Measured: capture Ø{0:F2} → throat Ø{1:F2} (ratio {2:F2}) → exit Ø{3:F2} | length {4:F2} m"),
                    prof.EquivalentCaptureDiameter,
                    2.0 * Math.Sqrt(Math.Max(prof.ThroatArea, 1e-9) / Math.PI), prof.ThroatRatio,
                    2.0 * Math.Sqrt(Math.Max(prof.ExitArea, 1e-9) / Math.PI), prof.Length));
                Row(string.Format(Loc.T("  压缩角 {0:F1}°（{1} 级）｜弯曲 {2:F1}°｜湿面积 {3:F2} m²\n{4}", "  Compression {0:F1}° ({1} stages) | bend {2:F1}° | wetted area {3:F2} m²\n{4}"),
                    prof.CompressionDeg, prof.CompressionStages, prof.BendDeg, prof.WettedArea,
                    prof.HasSpinner ? string.Format(Loc.T("中心体 L{0:F2} m、半锥 {1:F1}°", "centre body L{0:F2} m, half-cone {1:F1}°"), prof.SpinnerLength, prof.SpinnerHalfAngleDeg)
                                    : Loc.T("无中心体", "no centre body")));

                GUILayout.BeginHorizontal();
                Slider(Loc.T("中心体长度 m", "Cone length m"), 0.0, 3.0, ref prof.SpinnerLength, "F2", Loc.T("（0 = 没有中心体）", "(0 = no centre body)"));
                Slider(Loc.T("中心体半锥 °", "Cone angle °"), 2.0, 30.0, ref prof.SpinnerHalfAngleDeg, "F1", Loc.T("（锥面压缩角，进激波模型）", "(the cone ramp angle the shock model uses)"));
                GUILayout.EndHorizontal();
                // 锥尖位置单独一行：中心体那一行已经两根滑杆，再塞第三根就挤成一条缝了。
                // 这一项**只改外形**——压缩角由型线的喉道段定，锥尖挪位置不改激波也不改面积。
                Slider(Loc.T("锥尖前伸 m", "Tip offset m"), -0.5, 0.5, ref prof.SpinnerTipX, "F2", Loc.T("（相对第一个截面，正=往前伸；只改外形）", "(from the first section, positive = forward; outline only)"));
                SyncProfile(prof);
            }

            // ---- 独立于几何的几项 ----
            GUILayout.Space(6f);
            Slider(Loc.T("抽气比例", "Bleed fraction"), 0.0, 0.15, ref sp.BleedFrac, "F3", Loc.T("（送给座舱/冷却，直接少这些流量）", "(bled to cabin or cooling — that much less air through the engine)"));
            Slider(Loc.T("唇口半径 m", "Lip radius m"), 0.0, 0.20, ref sp.LipRadius, "F3", Loc.T("（0 = 按捕获口径自动；只影响外形与超声速钝唇）", "(0 = automatic from the capture diameter; changes the outline and the blunt supersonic lip only)"));
            // 锥尖位置：模板外形（锥体/斜板）才有中心体，别的型式这根滑杆没有意义。
            if (sp.Kind == IntakeKind.Cone || sp.Kind == IntakeKind.Ramp)
            {
                double shift = sp.ConeTipShift;
                Slider(Loc.T("锥尖位置", "Cone tip"), IntakeGeometry.ConeTipShiftMin, IntakeGeometry.ConeTipShiftMax, ref shift, "P0", Loc.T("（占长度，正 = 伸出唇口；只改外形，不改流量）", "(of length, positive = ahead of the lip; outline only, no flow change)"));
                sp.ConeTipShift = shift;
            }
            // 迎面马赫、迎角、侧滑角不在这里设（挪到「读数」页去说了）：
            //  迎面马赫由**喉道/发动机面的面积比**反解（扩压器关系），是几何与求解器的结果；
            //  迎角侧滑由**飞行状态**给出（零件按自己轴线与来流的夹角算），地面上就是 0。
            // 以前这三根滑杆让人手填，等于让玩家替物理做选择——填错了读数也跟着错。
            // 可调几何是**机构**，不是状态：装不装由设计者定，装上了锥体位置由马赫自己走。
            bool vg = sp.VariableGeometry;
            GUILayout.BeginHorizontal();
            bool vgNew = GUILayout.Toggle(vg, Loc.T(" 可调中心锥", " Variable cone"), GUILayout.Width(300f));
            GUILayout.EndHorizontal();
            if (vgNew != vg)
            {
                sp.VariableGeometry = vgNew;
                ApplyToTarget();
            }
            if (sp.VariableGeometry)
            {
                double machNow = _target != null && _target.vessel != null
                    ? _target.vessel.mach : 0.0;
                double f = Intake.ThroatFactor(sp, machNow);
                Row(string.Format(Loc.T("  当前 M{0:F2}：喉道收到 {1:P0}（M{2:F1} 开始作动、M{3:F1} 走完全行程）", "  At M{0:F2}: throat narrowed to {1:P0} (starts moving at M{2:F1}, full travel at M{3:F1})"),
                    machNow, f, Intake.VariableGeometryOn, Intake.VariableGeometryFull));
            }
            GUILayout.BeginHorizontal();
            _liveShape = GUILayout.Toggle(_liveShape, Loc.T(" 边调边看", " Live shape"), GUILayout.Width(240f));
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            Row(Loc.T("出口（发动机面）", "Exit (engine face)"));
            Slider(Loc.T("出口直径 m", "Exit diameter m"), 0.0, 3.0, ref _target.exitDiameter, "F2", Loc.T("（0 = 按自己的喉道）", "(0 = derived from its own throat)"));

            }
            else
            {

            // ---- 实时读数：按机库里的设计高度/马赫算 ----
            GUILayout.Space(2f);
            IntakeSpec live = _target != null ? _target.ResolvedSpec : sp;
            Row(string.Format(Loc.T("  迎面马赫按喉道/发动机面面积比反解、迎角 {0:F1}° 侧滑 {1:F1}° 由飞行状态给（{2}）——这三项不在这里设",
                "  Face Mach is back-solved from the throat / engine-face area ratio, AoA {0:F1}° and sideslip {1:F1}° come from the flight state ({2}) — none of the three is set here"),
                live.AlphaDeg, live.BetaDeg, _target != null && _target.vessel != null ? Loc.T("飞行状态实时", "live from the flight state") : Loc.T("地面 0°", "0° on the ground")));

            GUILayout.Space(6f);
            Row(Loc.T("—— 这件进气道现在是什么样 ——", "— What this intake looks like right now —"));
            IntakeSolution s = IntakeSolver.SolvePart(_edit, FaceDiameterOf(0.0), tech, 0.0, 0.0, 0.0, double.NaN, _editLength);
            Row(string.Format(Loc.T("  捕获 Ø{0:F2} m（{1:F3} m²）｜喉道 {2:F3} m²｜出口 {3:F3} m²", "  Capture Ø{0:F2} m ({1:F3} m²) | throat {2:F3} m² | exit {3:F3} m²"),
                s.CaptureArea > 1e-9 ? Math.Sqrt(4.0 * s.CaptureArea / Math.PI) : 0.0,
                s.CaptureArea, s.ThroatArea, s.FaceArea));
            Row(string.Format(Loc.T("  总压恢复 {0:F3}｜面马赫 {1:F2}｜畸变 {2:F3}", "  Recovery {0:F3} | face Mach {1:F2} | distortion {2:F3}"), s.Tpr, s.FaceMach, s.DistortionIndex));
            Row(string.Format(Loc.T("  需求 {0:F1} kg/s｜可用 {1:F1} kg/s｜溢流 {2:F1} kg/s", "  Demand {0:F1} kg/s | available {1:F1} kg/s | spillage {2:F1} kg/s"),
                s.RequiredMassFlow, s.AvailableMassFlow, s.SpillageMassFlow));
            // 这三个数以前算完就扔掉：喉道余量（负 = 喉道跟不上需求）、
            // 喉道/出口面积比（面马赫是被它卡住的）、进气道阻力（溢流按动量估的）。
            // 玩家看到"推力掉了"，答案多半在这三行里。
            Row(string.Format(Loc.T("  喉道余量 {0:P1}（{1}）｜喉道/出口 {2:F2}｜溢流阻力 {3:F0} N", "  Throat margin {0:P1} ({1}) | throat/exit {2:F2} | spillage drag {3:F0} N"),
                s.ThroatMargin, s.ThroatChoked ? Loc.T("壅塞", "choked") : Loc.T("不壅塞", "not choked"),
                s.AreaRatioThroatFace, s.InletDrag));
            Row(Loc.T("  状态：", "  State: ") + StateText(s));
            for (int i = 0; i < s.Warnings.Count; i++) Row("  · " + s.Warnings[i]);

            // ---- 全包线 ----
            GUILayout.Space(6f);
            Row(Loc.T("—— 海平面全包线（静态）——", "— Sea-level envelope (static) —"));
            Row(Loc.T("  马赫   总压恢复   可用流量   面马赫   状态", "  Mach   Recovery   Available   Face Mach   State"));
            for (int i = 0; i < ProbeMachs.Length; i++)
            {
                double m = ProbeMachs[i];
                IntakeSolution p = IntakeSolver.SolvePart(_edit, FaceDiameterOf(0.0), tech, 0.0, m, 0.0, double.NaN, _editLength);
                Row(string.Format("  M{0,-4:F1} {1,8:F3} {2,10:F1} {3,8:F2}   {4}",
                    m, p.Tpr, p.AvailableMassFlow, p.FaceMach, StateText(p)));
            }

            // ---- 与发动机的联系 ----
            GUILayout.Space(6f);
            Row(Loc.T("—— 装在机上之后 ——", "— Once it is on the craft —"));
            int fed = IntakeLink.EnginesFedBy(_target.part);
            if (HighLogic.LoadedSceneIsFlight && _target.vessel != null)
            {
                double p0, t0;
                Gas.Isa((double)_target.vessel.altitude, out p0, out t0);
                double mach = _target.vessel.mach;
                IntakeSolution cur = IntakeSolver.SolvePart(_edit, FaceDiameterOf(0.0), tech,
                                                            (double)_target.vessel.altitude, mach,
                                                            0.0, double.NaN, _editLength);
                Row(string.Format(Loc.T("  当前 {0:F0} m / M{1:F2}：恢复 {2:F3}｜可用 {3:F1} kg/s\n{4}", "  Now {0:F0} m / M{1:F2}: recovery {2:F3} | available {3:F1} kg/s\n{4}"),
                    _target.vessel.altitude, mach, cur.Tpr, cur.AvailableMassFlow, StateText(cur)));
            }
            Row(fed > 0
                ? string.Format(Loc.T("  正在给 {0} 台发动机供气（{1:F0} m 以内算接上）", "  Feeding {0} engine(s) ({1:F0} m or closer counts as connected)"), fed, IntakeLink.MaxDistance)
                : string.Format(Loc.T("  附近没有发动机：它现在只是一根管子（{0:F0} m 以内才算接上）", "  No engine nearby: right now it is just a duct ({0:F0} m or closer counts as connected)"), IntakeLink.MaxDistance));

            }

            SyncLiveShape();

            GUILayout.EndScrollView();

            // ---- 底部按钮 ----
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_liveShape ? Loc.T("应用（外形已同步）", "Apply (shape already synced)") : Loc.T("应用", "Apply"), GUILayout.Width(_liveShape ? 180f : 100f)))
            {
                ApplyToTarget();
                _status = Loc.T("已应用到这件进气道", "Applied to this intake");
            }
            if (GUILayout.Button(Loc.T("还原", "Revert"), GUILayout.Width(80f)))
            {
                _edit = _target.Spec != null ? _target.Spec.Clone() : new IntakeSpec();
                _status = Loc.T("已还原", "Reverted");
            }
            if (GUILayout.Button(Loc.T("同款一起改", "Sync same type"), GUILayout.Width(110f)))
            {
                ApplyToSameModel();
                _status = Loc.T("同款进气道已一起改", "Every intake of this model was changed too");
            }
            if (GUILayout.Button(Loc.T("关闭", "Close"), GUILayout.Width(70f))) _visible = false;
            GUILayout.Label(_status, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        /// <summary>
        /// 同款一起改：对称放出来的每一件都是独立零件、各有各的 `Spec`，
        /// 不一起改的话玩家眼里就是"左右两个进气道一个粗一个细"。
        /// </summary>
        /// <summary>把草稿写回零件并重建外形。</summary>
        private void ApplyToTarget()
        {
            if (_target == null) return;
            _target.Spec = _edit.Clone();
            _target.length = _editLength;
            _target.RefreshMesh();
            // 对称放置的另一件也得跟着改。不抄的话，一对里改一台另一台还停在旧值上，
            // 机库里两件并排，看着就像"改的这台被另一台顶回去了"。
            SymmetryCopy.Intake(_target);
        }

        /// <summary>
        /// 草稿里还没有型线时按当前长度/口径建一条两段的默认型线。
        /// 老存档只有七个比例参数，第一次打开面板就是"把它变成一条看得见的气道"。
        /// </summary>
        private void EnsureProfile()
        {
            if (_edit.Shape != null && _edit.Shape.Sections.Count >= IntakeProfile.MinSections) return;
            _edit.Shape = IntakeProfile.Default(Math.Max(_editLength, 0.4), CaptureBase());
            _view.Selected = 0;
        }

        /// <summary>老比例参数算出来的捕获口径，用来做默认型线的起点。</summary>
        private double CaptureBase()
        {
            IntakeSpec sp = _edit.Resolved();
            if (sp.CaptureDiameter > 0.0) return sp.CaptureDiameter;
            if (_target != null && _target.exitDiameter > 0.0) return _target.exitDiameter / 0.9;
            return 0.6;
        }

        /// <summary>型线改完之后把派生量同步回草稿（长度、捕获口径、喉道比、弯曲角都由型线量）。</summary>
        private void SyncProfile(IntakeProfile prof)
        {
            if (prof == null || prof.Sections.Count < IntakeProfile.MinSections) return;
            // 拖动顶点有可能把某一段拖成顺时针（形状没变、面全朝里），这里顺手掰回来：
            // 放样网格按逆时针算法线与绕序，绕向反了模型就会被背面剔除。
            for (int i = 0; i < prof.Sections.Count; i++) prof.Sections[i].EnsureCounterClockwise();
            string why;
            if (!prof.Validate(out why))
            {
                // 型线在中间状态下可能短暂非法（比如刚加了一段、位置还没调），
                // 这时候不要把垃圾写进草稿的性能参数，等它合法再说。
                _status = Loc.T("型线还不能用：", "Profile not usable yet: ") + why;
                return;
            }
            _edit.Shape = prof;
            _editLength = prof.Length;
        }

        /// <summary>在前两段之间插一段（插在选中段之后，位置取中值），插完尺寸沿两邻居插值。</summary>
        private void AddSection(IntakeProfile prof)
        {
            if (prof == null || prof.Sections.Count >= IntakeProfile.MaxSections) return;
            int i = Mathf.Clamp(_view.Selected, 0, prof.Sections.Count - 2);
            IntakeSection a = prof.Sections[i];
            IntakeSection b = prof.Sections[i + 1];
            IntakeSection n = new IntakeSection(0.5 * (a.X + b.X), 0.5 * (a.Width + b.Width), 0.5 * (a.Height + b.Height));
            n.OffsetY = 0.5 * (a.OffsetY + b.OffsetY);
            n.OffsetZ = 0.5 * (a.OffsetZ + b.OffsetZ);
            n.Shape = a.Shape;
            n.Exponent = a.Exponent;
            prof.Sections.Insert(i + 1, n);
            _view.Selected = i + 1;
            SyncProfile(prof);
        }

        /// <summary>删掉选中段；至少要留两段，否则气道就连不起来了。</summary>
        private void DeleteSection(IntakeProfile prof)
        {
            if (prof == null || prof.Sections.Count <= IntakeProfile.MinSections) return;
            int i = Mathf.Clamp(_view.Selected, 0, prof.Sections.Count - 1);
            prof.Sections.RemoveAt(i);
            if (_view.Selected >= prof.Sections.Count) _view.Selected = prof.Sections.Count - 1;
            SyncProfile(prof);
        }

        /// <summary>
        /// 边调边看：参数一动就重建这件零件的外形。
        /// 只在编辑器里做——飞行中重建网格没有意义（已经吸进去的气不会重组）。
        /// </summary>
        private void SyncLiveShape()
        {
            if (!_liveShape || _target == null || !HighLogic.LoadedSceneIsEditor) return;
            IntakeSpec sp = _edit;
            string stamp = string.Format("{0}|{1:R}|{2:R}|{3:R}|{4:R}|{5:R}|{6:R}|{7:R}|{8}", // 可读性豁免：草稿指纹串，不显示给玩家，竖线是数据分隔符
                (int)sp.Kind, sp.CaptureDiameter, sp.ThroatRatio, sp.FaceMachDesign,
                sp.LipRadius, sp.DuctBendDeg, _editLength, _target.FaceDiameter,
                IntakeSpecCodec.ProfileToString(sp.Shape));
            if (stamp == _shapeStamp) return;
            _shapeStamp = stamp;
            ApplyToTarget();
        }

        private void ApplyToSameModel()
        {
            if (_target == null || _target.part == null || _target.part.vessel == null) return;
            List<Part> parts = _target.part.vessel.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                ModuleJetForgeIntake m = parts[i].FindModuleImplementing<ModuleJetForgeIntake>();
                if (m == null) continue;
                if (m.part.partInfo.name != _target.part.partInfo.name) continue;
                m.Spec = _edit.Clone();
                m.exitDiameter = _target.exitDiameter;
                m.techName = _target.techName;
                // 长度也算"同款"的一部分：只抄型线不抄长度，网格会按旧长度重新放样，
                // 看着就是"粗细改了、长短没改"。
                m.length = _editLength;
                // 抄完必须重建网格。这句以前漏了：字段都改了、面板读数也变了，
                // 但机库里那几件外形还是旧的——玩家看到的就是"同款一起改没生效"，
                // 连同顺手报的"对称件不跟着镜像变"其实是同一个毛病：
                // 对偶件走 `SymmetryCopy.Intake`（它重建），同名件走这里（没重建）。
                m.RefreshMesh();
            }
        }

        private double FaceDiameterOf(double fallback)
        {
            if (_target == null) return fallback > 0.0 ? fallback : 0.5;
            // 出口直径是编辑中的值（还没应用），用它算读数才是"改完会变成什么样"。
            if (_target.exitDiameter > 0.0) return _target.exitDiameter;
            IntakeSpec sp = _edit.Resolved();
            double capD = sp.CaptureDiameter > 0.0 ? sp.CaptureDiameter : 0.6;
            double ratio = Math.Min(Math.Max(sp.ThroatRatio, 0.2), 1.2);
            double throat = Math.PI * 0.25 * capD * capD * ratio;
            return Math.Sqrt(4.0 * throat / Math.PI);
        }

        private static string StateText(IntakeSolution s)
        {
            if (s == null || !s.Valid) return Loc.T("解不出来", "no solution");
            if (s.Unstarted) return Loc.T("不起动", "unstarted");
            if (s.ThroatChoked) return Loc.T("喉道壅塞", "throat choked");
            if (s.Spilling) return Loc.T("有溢流", "spilling");
            return Loc.T("正常", "normal");
        }

        private static string HintOf(IntakeKind k)
        {
            switch (k)
            {
                case IntakeKind.Pitot: return Loc.T("皮托：亚声速最省事，超声速靠正激波，M1.6 以上明显吃亏。", "Pitot: the simplest at subsonic speed, only a normal shock at supersonic, and clearly worse above M1.6.");
                case IntakeKind.Ramp: return Loc.T("斜板：二维外压缩，M1.2 以上开始回血，迎角敏感。", "Ramp: two-dimensional external compression, pays off above M1.2, sensitive to angle of attack.");
                case IntakeKind.Cone: return Loc.T("锥体：轴对称外压缩，外形干净，激波损失略大于斜板。", "Cone: axisymmetric external compression, clean outline, slightly more shock loss than a ramp.");
                default: return Loc.T("轴对称：什么都不靠，恢复系数按年代给，最容易配平。", "Axial: nothing clever, recovery comes from the era, easiest to balance.");
            }
        }

        // ---- 小工具（与主机设计器同风格，但不共享状态）----

        private static void Row(string text)
        {
            GUILayout.Label(text, GUILayout.ExpandWidth(true));
        }

        private static void Slider(string label, double min, double max, ref double v, string fmt, string hint)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(110f));
            double nv = GUILayout.HorizontalSlider((float)v, (float)min, (float)max, GUILayout.Width(190f));
            if (Math.Abs(nv - v) > 1e-9) v = nv;
            GUILayout.Label(v.ToString(fmt), GUILayout.Width(62f));
            GUILayout.Label(hint, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }
    }
}