using System;
using System.Collections.Generic;
using JetForge.Core;
using JetForge.KSP;
using UnityEngine;

namespace JetForge.UI
{
    /// <summary>
    /// 单个截面的 2D 轮廓编辑器——**独立窗口**。
    ///
    /// 为什么不再挤在主面板里：进气道设计器本来就长，中间再塞一张 240×200 的缩略图，
    /// 放进滚动区就连同文字一起被压扁，圆截面被拉成椭圆，珠子和鼠标位置也对不上
    /// （画用一套比例、判命中用另一套，点偏十几像素就"拖不动点"）。
    /// 这里单开一扇窗，画布是**正方形**、横竖同一个米每像素，形状才是它本来的样子。
    ///
    /// 改的是主面板那份草稿里的同一个 <see cref="IntakeSection"/> 对象（它是 class，
    /// 就地改），改完调 onChanged 让主面板重算网格与读数。
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class JetForgeSectionWindow : MonoBehaviour
    {
        private const int WindowId = 0x4A52;
        private static JetForgeSectionWindow _instance;

        private IntakeProfile _prof;
        private int _index;
        private Action _onChanged;
        /// <summary>这条型线还在草稿里吗？主面板"重置型线/还原"会换掉对象，那时这里得停手。</summary>
        private Func<bool> _alive;
        private Rect _rect = new Rect(820f, 120f, 460f, 620f);
        private bool _visible;
        private string _status = "";
        /// <summary>1/4 对称开关：开着时每拖一下顶点就按第一象限重新生成四份。</summary>
        private bool _symmetry;   // XY 轴对称（取代原来的 1/4 对称）

        private readonly IntakeSectionView _view = new IntakeSectionView();
        /// <summary>数值框里"玩家打到一半"的原文，见 <see cref="Field"/>。</summary>
        private readonly Dictionary<string, string> _buf = new Dictionary<string, string>();

        public static void Open(IntakeProfile prof, int index, Action onChanged, Func<bool> alive = null)
        {
            if (prof == null || prof.Sections.Count == 0) return;
            if (_instance == null)
            {
                GameObject go = new GameObject("JetForgeSectionWindow");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<JetForgeSectionWindow>();
            }
            _instance._prof = prof;
            _instance._onChanged = onChanged;
            _instance._alive = alive;
            _instance._index = Mathf.Clamp(index, 0, prof.Sections.Count - 1);
            _instance._visible = true;
            _instance._view.MarkDirty();
            _instance._buf.Clear();
        }

        private void OnGUI()
        {
            if (!_visible || _prof == null) return;
            _rect = GUILayout.Window(WindowId, _rect, Draw, Loc.T("JetForge · 截面轮廓", "JetForge - Section profile"),
                GUILayout.MinWidth(380f));
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
        }

        private void Draw(int id)
        {
            // 主面板"还原 / 重置型线"会换成另一条型线对象。那时这里手上的引用已经不在草稿里，
            // 再改下去就是"改了没反应、还不吭声"——必须当面说清楚。
            if (_alive != null && !_alive())
            {
                GUILayout.Label(Loc.T("这条型线已经不在草稿里了（主面板重置/还原过）。", "This profile is no longer in the draft (the main panel was reset or reverted)."));
                if (GUILayout.Button(Loc.T("关闭", "Close"), GUILayout.Width(80f))) _visible = false;
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
                return;
            }

            if (_prof == null || _index >= _prof.Sections.Count)
            {
                GUILayout.Label(Loc.T("截面已经没了（型线被改短或被还原）。", "That section is gone (the profile was shortened or reverted)."));
                if (GUILayout.Button(Loc.T("关闭", "Close"))) _visible = false;
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
                return;
            }

            IntakeSection sec = _prof.Sections[_index];

            // ---- 选哪一段 ----
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀", GUILayout.Width(28f))) Switch(-1);
            GUILayout.Label(string.Format(Loc.T("截面 {0}/{1}", "Section {0}/{1}"), _index + 1, _prof.Sections.Count),
                GUILayout.ExpandWidth(true));
            if (GUILayout.Button("▶", GUILayout.Width(28f))) Switch(1);
            if (GUILayout.Button(Loc.T("+ 截面", "+ Section"), GUILayout.Width(64f))) AddSection();
            if (GUILayout.Button(Loc.T("− 截面", "- Section"), GUILayout.Width(64f))) DeleteSection();
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("拖边改宽高、拖中心改偏移；多边形可以逐点拖。按住 Shift 走细步。", "Drag a side to set width or height, drag the centre to offset; on a polygon you can drag each point. Hold Shift for fine steps."));

            // ---- 画布：固定正方形 ----
            // 不给 ExpandWidth：一旦被窗口拉宽拉扁，圆就变成椭圆，手上的点和画出来的点也不在同一处。
            GUILayout.Space(2f);
            Rect canvas = GUILayoutUtility.GetRect(360f, 360f, GUILayout.ExpandWidth(false));
            if (_view.Draw(canvas, sec))
            {
                if (sec.Shape == IntakeSectionShape.Circle) sec.Height = sec.Width;
                if (_symmetry && sec.HasPolygon) RefitQuarter(sec);
                _buf.Clear();
                Changed();
            }

            // ---- 数值 ----
            GUILayout.BeginHorizontal();
            Field("x", Loc.T("轴向 m", "Axial m"), ref sec.X);
            Field("w", Loc.T("宽 m", "Width m"), ref sec.Width, 0.02);
            Field("h", Loc.T("高 m", "Height m"), ref sec.Height, 0.02);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            Field("y", Loc.T("上下 m", "Up/down m"), ref sec.OffsetY);
            Field("z", Loc.T("左右 m", "Left/right m"), ref sec.OffsetZ);
            if (GUILayout.Button(ShapeName(sec), GUILayout.Width(72f)))
            {
                sec.Shape = (IntakeSectionShape)(((int)sec.Shape + 1) % 3);
                if (sec.Shape == IntakeSectionShape.Circle) { sec.Height = sec.Width; sec.Exponent = 2.0; }
                _view.MarkDirty();
                _buf.Clear();
                Changed();
            }
            if (sec.Shape == IntakeSectionShape.Superellipse) Field("n", Loc.T("指数", "Exponent"), ref sec.Exponent);
            GUILayout.EndHorizontal();

            // ---- 预设轮廓 ----
            GUILayout.Space(3f);
            GUILayout.Label(Loc.T("预设轮廓：", "Presets:"));
            GUILayout.BeginHorizontal();
            PresetButton(sec, SectionPreset.Circle, Loc.T("圆", "Circle"));
            PresetButton(sec, SectionPreset.WideEllipse, Loc.T("宽椭圆", "Wide ellipse"));
            PresetButton(sec, SectionPreset.TallEllipse, Loc.T("竖椭圆", "Tall ellipse"));
            PresetButton(sec, SectionPreset.RoundedRect, Loc.T("圆角矩形", "Rounded rect"));
            PresetButton(sec, SectionPreset.Rect, Loc.T("矩形", "Rectangle"));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            PresetButton(sec, SectionPreset.Diamond, Loc.T("菱形", "Diamond"));
            PresetButton(sec, SectionPreset.Trapezoid, Loc.T("梯形", "Trapezoid"));
            PresetButton(sec, SectionPreset.Lens, Loc.T("双凸透镜", "Biconvex"));
            PresetButton(sec, SectionPreset.Barrel, Loc.T("鼓形", "Barrel"));
            GUILayout.EndHorizontal();

            // ---- 镜像 ----
            GUILayout.Space(2f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("镜像：", "Mirror"), GUILayout.Width(42f));
            if (!sec.HasPolygon)
            {
                // 参数化形状（圆/椭圆/超椭圆）本来就上下左右对称，镜像它等于什么都没做。
                GUILayout.Label(Loc.T("先把截面转成自由多边形（参数化形状本来就对称）。", "Convert the section to a free polygon first (parametric shapes are already symmetric)."));
            }
            else
            {
                // 两个镜像改成**独立的开关**：勾上就是"这一轴翻过来"，取消就是翻回去。
                // 以前是按钮直接对顶点取反，按完第一个再按第二个，第一个的效果会被"翻转 + 旋转"
                // 悄悄顶掉（玩家说"两个按钮叠在一起"）。现在两个开关都以同一份基准轮廓为起点，
                // 四种组合（都不翻 / 只上下 / 只左右 / 都翻）各自确定，互不干扰。
                bool mx = GUILayout.Toggle(sec.MirrorXOn, Loc.T(" X 轴（上下翻）", " X axis (flip UD)"), GUILayout.Width(126f));
                if (mx != sec.MirrorXOn)
                {
                    sec.SetMirror(mx, sec.MirrorYOn);
                    Apply(mx ? Loc.T("已上下翻", "Flipped up-down") : Loc.T("已取消上下翻", "Up-down flip cancelled"));
                }
                bool my = GUILayout.Toggle(sec.MirrorYOn, Loc.T(" Y 轴（左右翻）", " Y axis (flip LR)"), GUILayout.Width(126f));
                if (my != sec.MirrorYOn)
                {
                    sec.SetMirror(sec.MirrorXOn, my);
                    Apply(my ? Loc.T("已左右翻", "Flipped left-right") : Loc.T("已取消左右翻", "Left-right flip cancelled"));
                }
                if (GUILayout.Button(Loc.T("XY 轴对称", " XY symmetric"), GUILayout.Width(96f)))
                {
                    // 先做一遍：做不成就别把开关打开——以前是先置位再试，
                    // 失败了开关还亮着，下一次任意拖动都会再拼一次，形状就一点点被抹平。
                    if (RefitQuarter(sec)) { _symmetry = true; Apply(Loc.T("已按第一象限生成四象限对称", "Four-quadrant symmetry generated from the first quadrant")); }
                }
                bool q = GUILayout.Toggle(_symmetry, Loc.T(" XY 轴对称", " XY symmetric"), GUILayout.Width(186f));
                if (q != _symmetry)
                {
                    if (!q) { _symmetry = false; _status = Loc.T("已退出轴对称（形状保留）", "Symmetry off (shape kept)"); }
                    else if (RefitQuarter(sec)) { _symmetry = true; Apply(Loc.T("已按第一象限生成四象限对称", "Four-quadrant symmetry generated from the first quadrant")); }
                    else _symmetry = false;   // RefitQuarter 已经把失败原因写进 _status
                }
                _view.LockQuarter = _symmetry;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("X 轴镜像＝上下翻，Y 轴镜像＝左右翻（可同时开）｜XY 轴对称＝只画第一象限，四象限自动补齐", "X mirror flips up-down, Y mirror flips left-right (both at once is fine) | XY symmetry means you draw the first quadrant only and the other three follow."));

            // ---- 自由多边形 ----
            GUILayout.Space(2f);
            GUILayout.BeginHorizontal();
            if (!sec.HasPolygon)
            {
                if (GUILayout.Button(Loc.T("转为自由多边形", "Free polygon"), GUILayout.Width(140f)))
                {
                    sec.Polygon = sec.ToPolygon(24);
                    _view.MarkDirty();
                    _buf.Clear();
                    Changed();
                }
                GUILayout.Label(Loc.T("参数化形状；转成多边形后可逐点拖动。", "Parametric shape; once it is a polygon you can drag each point."));
            }
            else
            {
                if (GUILayout.Button(Loc.T("+ 顶点", "+ Point"), GUILayout.Width(64f)))
                {
                    sec.InsertPolygonPoint(Math.Max(0, _view.Vertex));
                    _view.MarkDirty();
                    _buf.Clear();
                    Changed();
                }
                if (GUILayout.Button(Loc.T("− 顶点", "- Point"), GUILayout.Width(64f)))
                {
                    if (sec.RemovePolygonPoint(Math.Max(0, _view.Vertex)))
                    {
                        _view.Vertex = -1;
                        _view.MarkDirty();
                        _buf.Clear();
                        Changed();
                    }
                }
                if (GUILayout.Button(Loc.T("退回参数化", "To parametric"), GUILayout.Width(96f)))
                {
                    sec.Polygon = null;
                    _view.Vertex = -1;
                    _view.MarkDirty();
                    _buf.Clear();
                    Changed();
                }
                GUILayout.Label(string.Format(Loc.T("{0} 个顶点", "{0} points"), sec.Polygon.Count));
            }
            GUILayout.EndHorizontal();

            // ---- 这一段的读数 ----
            double eq = 2.0 * Math.Sqrt(Math.Max(sec.Area, 1e-9) / Math.PI);
            Row(string.Format(Loc.T("  面积 {0:F4} m²｜周长 {1:F3} m｜等面积圆 Ø{2:F3} m", "  Area {0:F4} m2 | perimeter {1:F3} m | equal-area circle dia {2:F3} m"), sec.Area, sec.Perimeter, eq));
            Row(string.Format(Loc.T("  全型线：捕获 Ø{0:F3} → 喉道 Ø{1:F3}（比 {2:F3}）→ 出口 Ø{3:F3}\n轴长 {4:F3} m", "  Whole profile: capture dia {0:F3} -> throat dia {1:F3} (ratio {2:F3}) -> exit dia {3:F3}\naxis length {4:F3} m"),
                _prof.EquivalentCaptureDiameter,
                2.0 * Math.Sqrt(Math.Max(_prof.ThroatArea, 1e-9) / Math.PI), _prof.ThroatRatio,
                2.0 * Math.Sqrt(Math.Max(_prof.ExitArea, 1e-9) / Math.PI), _prof.Length));
            Row(string.Format(Loc.T("  压缩角 {0:F1}°（{1} 级）｜弯曲 {2:F1}°｜湿面积 {3:F2} m²", "  Compression angle {0:F1} deg ({1} stages) | bend {2:F1} deg | wetted area {3:F2} m2"),
                _prof.CompressionDeg, _prof.CompressionStages, _prof.BendDeg, _prof.WettedArea));

            string why;
            if (!_prof.Validate(out why))
                GUILayout.Label("⚠ " + why);
            else if (_status.Length > 0)
                GUILayout.Label(_status);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("关闭", "Close"), GUILayout.Width(80f))) _visible = false;
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        private void Switch(int delta)
        {
            int n = _prof.Sections.Count;
            if (n == 0) return;
            _index = (_index + delta + n) % n;
            _view.Vertex = -1;
            _view.MarkDirty();
            _buf.Clear();
        }

        /// <summary>在选中段与下一段中间插一段，尺寸取两端插值——新段一开始就"在这条线里"。</summary>
        private void AddSection()
        {
            if (_prof.Sections.Count >= IntakeProfile.MaxSections)
            {
                _status = Loc.T("截面上限 ", "Section limit ") + IntakeProfile.MaxSections + Loc.T(" 段", " sections");
                return;
            }
            int i = Mathf.Clamp(_index, 0, _prof.Sections.Count - 2);
            IntakeSection a = _prof.Sections[i];
            IntakeSection b = _prof.Sections[i + 1];
            IntakeSection n = new IntakeSection(0.5 * (a.X + b.X), 0.5 * (a.Width + b.Width), 0.5 * (a.Height + b.Height));
            n.OffsetY = 0.5 * (a.OffsetY + b.OffsetY);
            n.OffsetZ = 0.5 * (a.OffsetZ + b.OffsetZ);
            n.Shape = a.Shape;
            n.Exponent = a.Exponent;
            _prof.Sections.Insert(i + 1, n);
            _index = i + 1;
            Changed();
        }

        private void DeleteSection()
        {
            if (_prof.Sections.Count <= IntakeProfile.MinSections)
            {
                _status = Loc.T("至少留 ", "Keep at least ") + IntakeProfile.MinSections + Loc.T(" 段，不然气道连不起来", " sections or the duct cannot connect");
                return;
            }
            _prof.Sections.RemoveAt(_index);
            if (_index >= _prof.Sections.Count) _index = _prof.Sections.Count - 1;
            Changed();
        }

        private void Changed()
        {
            _view.MarkDirty();
            if (_onChanged != null)
            {
                try { _onChanged(); }
                catch (Exception e) { _status = Loc.T("改不了：", "Cannot change: ") + e.Message; }
            }
        }

        private static string ShapeName(IntakeSection s)
        {
            switch (s.Shape)
            {
                case IntakeSectionShape.Circle: return Loc.T("圆", "Circle");
                case IntakeSectionShape.Ellipse: return Loc.T("椭圆", "Ellipse");
                default: return Loc.T("超椭圆", "Superellipse");
            }
        }

        private void Apply(string note)
        {
            _view.Vertex = -1;
            _status = note;
            _buf.Clear();
            Changed();
        }

        private void PresetButton(IntakeSection sec, SectionPreset preset, string label)
        {
            if (!GUILayout.Button(label, GUILayout.Width(84f))) return;
            sec.ApplyPreset(preset);
            if (_symmetry && sec.HasPolygon) RefitQuarter(sec);
            Apply(Loc.T("换成 ", "Switch to ") + label);
        }

        /// <summary>
        /// XY 轴对称：把顶点收回第一象限再生成四份。拖动时顶点下标会变，
        /// 所以按"拖完之后那个顶点的位置"重新找回它的新下标，不然松手瞬间会跳到别的点上。
        /// 形状会走样（梯形这类不对称的）时由 `MirrorQuarter` 回滚并把原因交回来。
        /// </summary>
        private bool RefitQuarter(IntakeSection sec)
        {
            if (sec == null || !sec.HasPolygon) { _status = Loc.T("这一截面不是自由多边形", "This section is not a free polygon"); return false; }
            int keep = _view.Vertex;
            double ku = 0.0, kw = 0.0;
            bool hasKeep = keep >= 0 && keep < sec.Polygon.Count;
            if (hasKeep) { ku = Math.Abs(sec.Polygon[keep].U); kw = Math.Abs(sec.Polygon[keep].W); }

            string why;
            if (!sec.MirrorQuarter(out why))
            {
                _status = why;
                return false;
            }

            if (hasKeep)
            {
                int best = -1;
                double bestD = double.MaxValue;
                for (int i = 0; i < sec.Polygon.Count; i++)
                {
                    SectionPoint p = sec.Polygon[i];
                    if (p.U < -1e-9 || p.W < -1e-9) continue;
                    double d = (p.U - ku) * (p.U - ku) + (p.W - kw) * (p.W - kw);
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best >= 0) _view.Vertex = best;
            }
            return true;
        }

        private static void Row(string text)
        {
            GUILayout.Label(text);
        }

        /// <summary>
        /// 一格数值输入。原文留在 <c>_buf</c> 里，只有解析成功才写回——
        /// 否则玩家打的 "1." 会被下一帧格式化回去，表现成"怎么也打不进小数点"。
        ///
        /// <paramref name="min"/> 是"这一步打字的过程值不算数"的下限：宽/高打到 0 的时候
        /// **不写回、也不动模型**，只把框标红。以前直接写 0，截面当场退化成一条线，
        /// 放样出一堆退化三角形，再改回来还得靠撤销——打字打到一半不该毁模型。
        /// </summary>
        private void Field(string key, string label, ref double value, double min = double.NegativeInfinity)
        {
            GUILayout.BeginVertical(GUILayout.Width(112f));
            GUILayout.Label(label);
            string cur;
            if (!_buf.TryGetValue(key, out cur)) cur = value.ToString("F3");
            Color old = GUI.color;
            bool pending = _buf.ContainsKey(key);
            if (pending) GUI.color = new Color(1f, 0.72f, 0.35f);   // 还没生效的输入：橙色，模型保持最后合法状态
            string typed = GUILayout.TextField(cur, GUILayout.Width(106f));
            GUI.color = old;
            if (typed != cur)
            {
                double v;
                if (double.TryParse(typed, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out v)
                    && !double.IsNaN(v) && !double.IsInfinity(v)
                    && v >= min)
                {
                    value = v;
                    _buf.Remove(key);
                    _view.MarkDirty();
                    Changed();
                    return;
                }
                // 解析不出（打到一半的 "1." 或 "-"）或者低于下限：留在缓冲里，模型一个数都不动。
                _buf[key] = typed;
            }
            GUILayout.EndVertical();
        }
    }
}