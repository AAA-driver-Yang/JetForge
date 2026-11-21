using System;
using JetForge.Core;
using JetForge.KSP;
using UnityEngine;

namespace JetForge.UI
{
    /// <summary>
    /// 型线骨架（侧视 / 俯视）——**独立窗口**。
    ///
    /// 为什么单开一扇：主面板本来就很长，骨架画布塞在滚动区里会被连文字一起压扁，
    /// 横竖比例不一致 → 拖动时"鼠标往上、截面往别的方向走"，量出来的形状也不对。
    /// 这里给一块固定尺寸的画布，横竖同一比例，和截面编辑窗口是同一套思路：
    /// **拖动在它自己的一扇窗里，读数留在主面板**。
    ///
    /// 改的是主面板草稿里的同一个 <see cref="IntakeProfile"/> 对象（class，就地改），
    /// 改完调 onChanged 让主面板重算网格与读数；选中哪一段通过 onSelect 回传，
    /// 免得两边各记一个"当前截面"然后互相覆盖。
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class JetForgeProfileWindow : MonoBehaviour
    {
        private const int WindowId = 0x4A53;
        private static JetForgeProfileWindow _instance;

        private IntakeProfile _prof;
        private Action _onChanged;
        private Action<int> _onSelect;
        private Func<bool> _alive;
        private Rect _rect = new Rect(120f, 90f, 660f, 470f);
        private bool _visible;
        private string _status = "";

        private readonly IntakeProfileView _view = new IntakeProfileView();

        public static void Open(IntakeProfile prof, int index, Action onChanged,
            Func<bool> alive = null, Action<int> onSelect = null)
        {
            if (prof == null || prof.Sections.Count == 0) return;
            if (_instance == null)
            {
                GameObject go = new GameObject("JetForgeProfileWindow");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<JetForgeProfileWindow>();
            }
            _instance._prof = prof;
            _instance._onChanged = onChanged;
            _instance._alive = alive;
            _instance._onSelect = onSelect;
            _instance._view.Selected = Mathf.Clamp(index, 0, prof.Sections.Count - 1);
            _instance._view.MarkDirty();
            _instance._visible = true;
        }

        /// <summary>已经在看这条型线了吗？主面板用它决定按钮上写"打开"还是"带过来"。</summary>
        public static bool IsOpenFor(IntakeProfile prof)
        {
            return _instance != null && _instance._visible && _instance._prof == prof;
        }

        private void OnGUI()
        {
            if (!_visible || _prof == null) return;
            _rect = GUILayout.Window(WindowId, _rect, Draw, Loc.T("JetForge · 型线骨架", "JetForge - Profile skeleton"),
                GUILayout.MinWidth(520f));
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
        }

        private void Draw(int id)
        {
            if (_alive != null && !_alive())
            {
                GUILayout.Label(Loc.T("这条型线已经不在草稿里了（主面板重置/还原过）。", "This profile is no longer in the draft (the main panel was reset or reverted)."));
                if (GUILayout.Button(Loc.T("关闭", "Close"), GUILayout.Width(80f))) _visible = false;
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
                return;
            }
            if (_prof == null || _prof.Sections.Count == 0)
            {
                GUILayout.Label(Loc.T("型线已经没了。", "The profile is gone."));
                if (GUILayout.Button(Loc.T("关闭", "Close"))) _visible = false;
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
                return;
            }

            int n = _prof.Sections.Count;
            int sel = Mathf.Clamp(_view.Selected, 0, n - 1);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_view.TopView ? Loc.T("看侧视", "Side view") : Loc.T("看俯视", "Top view"), GUILayout.Width(76f)))
            {
                _view.TopView = !_view.TopView;
                _view.Refit();   // 换视角就得重新算量程，否则还是上一个视角的框
            }
            if (GUILayout.Button("◀", GUILayout.Width(28f))) Select(sel - 1);
            GUILayout.Label(string.Format(Loc.T("截面 {0}/{1}", "Sec {0}/{1}"), sel + 1, n), GUILayout.Width(76f));
            if (GUILayout.Button("▶", GUILayout.Width(28f))) Select(sel + 1);
            if (GUILayout.Button(Loc.T("+ 截面", "+ Section"), GUILayout.Width(64f))) AddSection();
            if (GUILayout.Button(Loc.T("− 截面", "- Section"), GUILayout.Width(64f))) DeleteSection();
            if (GUILayout.Button(Loc.T("重置型线", "Reset"), GUILayout.Width(76f))) ResetProfile();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("编辑这个截面的轮廓…", "Edit this outline..."), GUILayout.Width(160f)))
            {
                JetForgeProfileWindow self = this;
                JetForgeSectionWindow.Open(_prof, sel,
                    delegate { if (self._onChanged != null) self._onChanged(); },
                    delegate { return self._alive == null || self._alive(); });
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(_view.TopView
                ? Loc.T("俯视：左右是横向位置，上下是轴向位置。拖中心块改偏移，拖斜线改轴向，边缘那颗点横着拖 = 让这一圈斜过来（倾角），Shift 走细步。", "Top view: left-right is lateral, up-down is axial. Drag the centre block to offset, drag the slanted line to move axially, drag an edge dot sideways to tilt that section, Shift for fine steps.")
                : Loc.T("侧视：左右是轴向，上下是高度。拖中心块改上下，拖上下边改高度，边缘那颗点横着拖 = 让这一圈斜过来（倾角），Shift 走细步。", "Side view: left-right is axial, up-down is height. Drag the centre block to move it, drag the top or bottom edge to change the height, drag an edge dot sideways to tilt that section, Shift for fine steps."));

            Rect canvas = GUILayoutUtility.GetRect(600f, 300f, GUILayout.ExpandWidth(true));
            bool changed = _view.Draw(canvas, _prof);
            if (changed && _onChanged != null) _onChanged();

            IntakeSection sec = _prof.Sections[Mathf.Clamp(_view.Selected, 0, n - 1)];
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format(Loc.T("这段：宽 {0:F3} × 高 {1:F3} m｜中心 {2:F3}, {3:F3}\n轴向 {4:F3} m｜倾角 侧视 {5:F1}° / 俯视 {6:F1}°", "This section: {0:F3} wide x {1:F3} m high | centre {2:F3}, {3:F3}\naxial {4:F3} m | tilt {5:F1} side / {6:F1} top"),
                sec.Width, sec.Height, sec.OffsetZ, sec.OffsetY, sec.X, sec.TiltPitchDeg, sec.TiltYawDeg), GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Loc.T("让这段等于上一段", "Copy previous"), GUILayout.Width(150f)) && sel > 0)
            {
                IntakeSection prev = _prof.Sections[sel - 1];
                sec.Width = prev.Width; sec.Height = prev.Height;
                sec.OffsetY = prev.OffsetY; sec.OffsetZ = prev.OffsetZ;
                sec.TiltPitchDeg = prev.TiltPitchDeg; sec.TiltYawDeg = prev.TiltYawDeg;
                if (prev.HasPolygon) sec.Polygon = new System.Collections.Generic.List<SectionPoint>(prev.Polygon);
                sec.Shape = prev.Shape;
                Sync(sec);
            }
            GUILayout.EndHorizontal();

            // 倾角也可以用按钮改：拖那颗边缘点能改，但第一段的点就贴在画布最左边，
            // 手一抖就点到空白处（玩家报过"最前面那段的倾角改不了"）。按钮是保底那条路。
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("倾角：侧视", "Tilt (side)"), GUILayout.Width(96f));
            if (GUILayout.Button("-5°", GUILayout.Width(48f))) { sec.TiltPitchDeg = IntakeSection.ClampTilt(sec.TiltPitchDeg - 5.0); Sync(sec); }
            GUILayout.Label(string.Format("{0:F1}°", sec.TiltPitchDeg), GUILayout.Width(56f));
            if (GUILayout.Button("+5°", GUILayout.Width(48f))) { sec.TiltPitchDeg = IntakeSection.ClampTilt(sec.TiltPitchDeg + 5.0); Sync(sec); }
            GUILayout.Space(16f);
            GUILayout.Label(Loc.T("俯视", "top"), GUILayout.Width(44f));
            if (GUILayout.Button("-5°", GUILayout.Width(48f))) { sec.TiltYawDeg = IntakeSection.ClampTilt(sec.TiltYawDeg - 5.0); Sync(sec); }
            GUILayout.Label(string.Format("{0:F1}°", sec.TiltYawDeg), GUILayout.Width(56f));
            if (GUILayout.Button("+5°", GUILayout.Width(48f))) { sec.TiltYawDeg = IntakeSection.ClampTilt(sec.TiltYawDeg + 5.0); Sync(sec); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(string.Format(Loc.T("全型线：捕获 Ø{0:F2} → 喉道 Ø{1:F2}（比 {2:F3}）→ 出口 Ø{3:F2}｜轴长 {4:F2} m\n{5}", "Whole profile: capture dia {0:F2} -> throat dia {1:F2} (ratio {2:F3}) -> exit dia {3:F2} | length {4:F2} m\n{5}"),
                _prof.EquivalentCaptureDiameter,
                2.0 * Math.Sqrt(Math.Max(_prof.ThroatArea, 1e-9) / Math.PI), _prof.ThroatRatio,
                2.0 * Math.Sqrt(Math.Max(_prof.ExitArea, 1e-9) / Math.PI), _prof.Length,
                _prof.HasSpinner ? string.Format(Loc.T("中心体 L{0:F2} m", "Centre body L{0:F2} m"), _prof.SpinnerLength) : Loc.T("无中心体", "No centre body")));

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);
            if (GUILayout.Button(Loc.T("关闭", "Close"), GUILayout.Width(80f))) _visible = false;
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        private void Select(int i)
        {
            if (_prof == null) return;
            _view.Selected = Mathf.Clamp(i, 0, _prof.Sections.Count - 1);
            _view.MarkDirty();
            if (_onSelect != null) _onSelect(_view.Selected);
        }

        private void Sync(IntakeSection sec)
        {
            _view.MarkDirty();
            if (_onChanged != null) _onChanged();
        }

        private void AddSection()
        {
            if (_prof == null || _prof.Sections.Count == 0) return;
            int at = Mathf.Clamp(_view.Selected, 0, _prof.Sections.Count - 1);
            IntakeSection src = _prof.Sections[at];
            IntakeSection copy = src.Clone();
            double half = 0.5 * (_prof.Length > 1e-6 ? _prof.Length : 1.0);
            double nx = 0.5 * (src.X + (at + 1 < _prof.Sections.Count ? _prof.Sections[at + 1].X : half));
            copy.X = Mathf.Min((float)nx, (float)half);
            _prof.Sections.Insert(at + 1, copy);
            for (int i = 0; i < _prof.Sections.Count; i++) _prof.Sections[i].EnsureCounterClockwise();
            _view.Selected = at + 1;
            _status = string.Format(Loc.T("在第 {0} 段后面插了一段（{1}/{2}）", "Inserted a section after #{0} ({1}/{2})"), at + 1, at + 2, _prof.Sections.Count);
            _view.Refit();   // 段数变了，量程重新量
            Sync(copy);
        }

        private void DeleteSection()
        {
            if (_prof == null || _prof.Sections.Count <= IntakeProfile.MinSections) return;
            int at = Mathf.Clamp(_view.Selected, 0, _prof.Sections.Count - 1);
            _prof.Sections.RemoveAt(at);
            for (int i = 0; i < _prof.Sections.Count; i++) _prof.Sections[i].EnsureCounterClockwise();
            _view.Selected = Mathf.Clamp(at, 0, _prof.Sections.Count - 1);
            _status = string.Format(Loc.T("删掉了第 {0} 段，剩 {1} 段", "Deleted section #{0}; {1} left"), at + 1, _prof.Sections.Count);
            _view.Refit();
            if (_onChanged != null) _onChanged();
        }

        private void ResetProfile()
        {
            if (_prof == null) return;
            double length = _prof.Length > 0.4 ? _prof.Length : 0.4;
            double cap = _prof.EquivalentCaptureDiameter > 0.05 ? _prof.EquivalentCaptureDiameter : 0.5;
            IntakeProfile fresh = IntakeProfile.Default(length, cap);
            _prof.Sections.Clear();
            for (int i = 0; i < fresh.Sections.Count; i++) _prof.Sections.Add(fresh.Sections[i]);
            _prof.SpinnerLength = fresh.SpinnerLength;
            _prof.SpinnerHalfAngleDeg = fresh.SpinnerHalfAngleDeg;
            _view.Selected = 0;
            _status = Loc.T("型线重置成两段直筒（0.92 收缩）", "Profile reset to two straight sections (0.92 contraction)");
            _view.Refit();
            if (_onChanged != null) _onChanged();
        }
    }
}