using System;
using JetForge.Core;
using UnityEngine;

namespace JetForge.UI
{
    /// <summary>
    /// 进气道型线的直接操作视图（侧视 / 俯视两页）。
    ///
    /// 为什么要有这个：以前改进气道是拖六根滑杆，每根滑杆各自改一个"比例"，彼此之间没有几何关系——
    /// 玩家没法回答"我这个唇口到底长什么样"。现在改成：图上就是这条气道，**拖哪个点改哪件事**——
    ///   拖上下边缘的点 / 整条边 → 改这几个截面的高
    ///   拖中心点                 → 改这个截面的上下偏移（俯视页是左右偏移，S 弯靠它）
    ///   拖截面的竖线（或它附近）  → 改这个截面的轴向位置
    ///
    /// 实现上用的是"光栅化到一张贴图"而不是 GL 画线：窗口 OnGUI 里 GL 的裁剪与窗口不在同一坐标系，
    /// 一旦窗口拖动就会画到别处去；贴图方式与窗口天然同坐标系，且只要变了才重画。
    /// </summary>
    public sealed class IntakeProfileView
    {
        public int Selected = 0;

        /// <summary>正在拖什么：0 无、1 轴向、3 中心（改偏移）、5 上边（改高）、6 下边（改高）。</summary>
        public int Drag = 0;

        /// <summary>false = 侧视（上下），true = 俯视（左右）。</summary>
        public bool TopView = false;

        /// <summary>鼠标悬停在哪颗手柄上（画个高亮环，省得玩家猜哪里能抓）。-1 = 没有。</summary>
        public int HoverIndex = -1;

        /// <summary>悬停的手柄种类（同 <see cref="Drag"/> 编号）。</summary>
        public int HoverKind = 0;

        private Texture2D _tex;
        private Color32[] _flip;
        private bool _dirty = true;
        private int _tw = 360, _th = 170;
        private IntakeProfile _last;

        /// <summary>按下时锁住的截面：拖动期间一直改它，松手清 -1。</summary>
        private int _dragIndex = -1;

        /// <summary>边拖动时的第二个截面（上/下边是"两点之间那一段"，牵着两个截面）。</summary>
        private int _dragIndexB = -1;

        // 按下时的模特坐标与各初值。**拖动只认增量、不认绝对位置**：
        // 以前是"按下就把截面 X 设成鼠标 X"，于是"点一下"和"拖一下"没有区别，
        // 手指一抖截面就跳到指针底下——表现就是"点下去就移动"。
        private double _pressX, _pressY;
        private double _initXa, _initXb, _initHa, _initHb, _initOa, _initOb;

        /// <summary>按下那一刻这一段的倾角（本视角那一轴）。拖动算的是增量，所以要记初值。</summary>
        private double _initTilt;

        private const int PadX = 10;
        private const int PadY = 12;

        /// <summary>手柄命中半径（像素）。12 太小、18 会抢旁边的点，14 是试出来的手感。</summary>
        private const float GrabRadius = 14f;

        /// <summary>
        /// 截面竖线的可抓半宽（像素）。**画多宽就抓多宽**：`Redraw` 里那条带子
        /// 用的就是这个数，命中判定也用这个数。22 px 是玩家反馈的"20~24 都行"的中间值，
        /// 再宽下去相邻两段的带子会连成一片，反而分不清抓的是哪一根。
        /// </summary>
        private const int VLineHalf = 11;

        public void MarkDirty() { _dirty = true; }

        /// <summary>
        /// 在 <paramref name="rect"/> 里画型线并处理鼠标。返回 true 表示这一帧改过型线（调用方据此重建网格）。
        /// </summary>
        public bool Draw(Rect rect, IntakeProfile p)
        {
            if (p == null || p.Sections == null || p.Sections.Count == 0)
            {
                GUI.Label(rect, "  型线还没有截面——按「加截面」先来两段");
                return false;
            }
            if (Selected >= p.Sections.Count) Selected = p.Sections.Count - 1;
            if (Selected < 0) Selected = 0;

            bool changed = HandleMouse(rect, p);
            if (changed) MarkDirty();
            if (_dirty || _last != p || _tex == null) Redraw(p);

            GUI.DrawTexture(rect, _tex);
            GUI.Label(new Rect(rect.x + 4f, rect.y + 2f, 320f, 18f),
                (TopView ? "俯视（左右）×  截面 " : "侧视（上下）×  截面 ") + (Selected + 1) + "/" + p.Sections.Count +
                "   拖中心改偏移、拖竖线改轴向、边缘点横着拖改倾角");
            return changed;
        }

        /// <summary>
        /// 拖动时冻结的视野。为什么要冻结：`Map`/`Unmap` 的量程是"整条型线里最大那个半尺寸"，
        /// 而这个最大值**正被手上这个点改变**——你往上拖边缘，量程跟着变大，同一个屏幕位置
        /// 对应的世界坐标就变小，于是算出来的尺寸反而往回缩，下一帧再反过来……
        /// 表现就是"拖动时数值诡异乱跳"。冻结之后，拖动期间坐标换算是常数，
        /// 手在哪里就是多少，松手再按新的量程重画。
        /// </summary>
        private bool _boxFrozen;
        private double _boxX0, _boxSpan, _boxHalf;
        private double _spanSeen;

        /// <summary>
        /// 视野量程。三件事都得在这里交代，不然玩家看到的是"改了但没动"：
        ///
        /// ① **左边界不再取"第一段的 X"**：原来 `x0 = Sections[0].X`，而右边界取最后一段，
        ///    于是把第一段往右拖时整个框跟着右移，图上像"动了、又弹回原位"——玩家报的
        ///    「第一个截面改不了水平位置」就是这么来的；最后一段往左拖同理钉在右边。
        ///    现在左边界锚在 0（零件前端），量程只增不减（`_spanSeen`），只有 `Refit()` 才重新量。
        /// ② **倾角要算进框里**：斜过来的那一圈最前/最后那点会跑出 `X` 之外，
        ///    不算进来就会被画到框外面——看着像被裁了，那颗点也就点不着了。
        /// ③ 半高仍按"半个身子 + 偏移"取最大值，跟原来一致。
        /// </summary>
        private void ComputeBox(IntakeProfile p, out double x0, out double span, out double half)
        {
            x0 = 0.0;
            double xMax = 0.05;
            half = 0.05;
            for (int i = 0; i < p.Sections.Count; i++)
            {
                IntakeSection s = p.Sections[i];
                double h = 0.5 * (TopView ? s.Width : s.Height);
                double ext = h + Math.Abs(TopView ? s.OffsetZ : s.OffsetY);
                if (ext > half) half = ext;
                double du = s.AxialShift(TopView ? h : 0.0, TopView ? 0.0 : h);
                double dw = s.AxialShift(TopView ? -h : 0.0, TopView ? 0.0 : -h);
                double a = s.X + Math.Min(du, dw);
                double b = s.X + Math.Max(du, dw);
                if (a < x0) x0 = a;
                if (b > xMax) xMax = b;
            }
            span = Math.Max(xMax - x0, 0.05);
            if (span > _spanSeen) _spanSeen = span; else span = _spanSeen;
            half *= 1.15;
        }

        private void FreezeBox(IntakeProfile p)
        {
            ComputeBox(p, out _boxX0, out _boxSpan, out _boxHalf);
            _boxFrozen = true;
        }

        /// <summary>
        /// 扔掉冻结的量程、按当前型线重新取框。
        /// 换视角（侧视↔俯视）时必须调它：两个视角量的是不同的尺寸
        /// （侧视看 `Height`，俯视看 `Width`），沿用旧框会把图压成一条。
        /// </summary>
        public void Refit()
        {
            _boxFrozen = false;
            _spanSeen = 0.0;      // 重新量一次：换了型线/换了视角之后，之前那个"只增不减"的量程不算数了
            _dirty = true;
        }

        /// <summary>
        /// 竖直（侧视）/ 横向（俯视）方向的显示符号。
        ///
        /// **侧视要翻一下**：网格里 `OffsetY` 落在零件局部 Z 轴（见 `JetForgeIntakeProfileMesh.RingPoints`
        /// 的 `(OffsetZ+u, X, OffsetY+w)`），而零件在机库里是横躺的——`OffsetY` 变大，模型往下走。
        /// 视图如果不翻，就会出现"鼠标往上拖、模型上那段往下沉"。
        /// 俯视页没这个约定（`OffsetZ` 就落在局部 X），保持原样。
        /// </summary>
        private double AxisSign { get { return TopView ? -1.0 : 1.0; } }

        /// <summary>
        /// 世界坐标 → 屏幕。**横竖同一个比例**：X 与 Y 各用各的比例会把 45° 的压缩面画成 30°，
        /// 玩家照着自己画的角度去选压缩角就永远对不上。所以取两方向里更紧的那个比例，
        /// 图在框里居中——宁可两边留白，也不让形状骗人。
        /// </summary>
        private void Box(IntakeProfile p, Rect r, out double x0, out double span, out double half, out double k)
        {
            if (_boxFrozen) { x0 = _boxX0; span = _boxSpan; half = _boxHalf; }
            else ComputeBox(p, out x0, out span, out half);
            double kw = (r.width - 2f * PadX) / Math.Max(span, 1e-6);
            double kh = (r.height - 2f * PadY) / Math.Max(2.0 * half, 1e-6);
            k = Math.Min(kw, kh);
            if (k < 0.0 || double.IsNaN(k)) k = 0.0;
        }

        private void Map(IntakeProfile p, double x, double y, Rect r, out float sx, out float sy)
        {
            double x0, span, half, k;
            Box(p, r, out x0, out span, out half, out k);
            double drawW = span * k;
            sx = r.x + (float)((r.width - drawW) * 0.5) + (float)((x - x0) * k);
            sy = r.y + r.height * 0.5f + (float)(y * k * AxisSign);
        }

        private void Unmap(IntakeProfile p, float px, float py, Rect r, out double x, out double y)
        {
            double x0, span, half, k;
            Box(p, r, out x0, out span, out half, out k);
            if (k <= 1e-9) { x = x0; y = 0.0; return; }
            double drawW = span * k;
            x = x0 + (px - (r.x + (r.width - drawW) * 0.5)) / k;
            y = (py - (r.y + r.height * 0.5f)) / (k * AxisSign);
        }

        private double CenterOf(IntakeSection s) { return TopView ? s.OffsetZ : s.OffsetY; }
        private double SizeOf(IntakeSection s) { return TopView ? s.Width : s.Height; }

        private void SetCenter(IntakeSection s, double v)
        {
            v = Math.Min(Math.Max(v, -1.5), 1.5);
            if (TopView) s.OffsetZ = v; else s.OffsetY = v;
        }

        private void SetSize(IntakeSection s, double v)
        {
            v = Math.Min(Math.Max(v, 0.02), 3.0);
            if (TopView) s.Width = v; else s.Height = v;
        }

        /// <summary>这个视角在管哪一根倾角：侧视管俯仰（截面上下歪），俯视管偏航（截面左右歪）。</summary>
        private double TiltOf(IntakeSection s) { return TopView ? s.TiltYawDeg : s.TiltPitchDeg; }

        private void SetTilt(IntakeSection s, double deg)
        {
            deg = IntakeSection.ClampTilt(deg);
            if (TopView) s.TiltYawDeg = deg; else s.TiltPitchDeg = deg;
        }

        /// <summary>
        /// 这一圈截面画在视图里是一条线，端点（上缘 / 下缘）的轴向位置。倾角越大，这条线越斜。
        ///
        /// 侧视看的是"上下歪"：边缘点在局部竖向 ±半高，轴向偏 = `AxialShift(0, ±半高)`；
        /// 俯视看的是"左右歪"：边缘点在局部横向 ±半宽，轴向偏 = `AxialShift(±半宽, 0)`。
        /// **两处不同轴**，所以不能只写一个 ±。
        /// </summary>
        private void SectionLine(IntakeSection s, out double xTop, out double xBot, out double half)
        {
            half = 0.5 * SizeOf(s);
            double u = TopView ? half : 0.0;
            double w = TopView ? 0.0 : half;
            xTop = s.X + s.AxialShift(u, w);
            xBot = s.X + s.AxialShift(-u, -w);
        }

        /// <summary>
        /// 找出指针底下那颗手柄。返回 true 时 <paramref name="kind"/> 是手柄种类、
        /// <paramref name="ia"/>/<paramref name="ib"/> 是它牵着的截面（边牵引两段，点只牵一段）。
        ///
        /// 优先顺序：点上（14 px）→ 边（8 px）→ 截面的竖线（水平 8 px）→ 什么都没有。
        /// **没有兜底**：以前点空白也会"选中最近的段"，于是玩家在空白处想取消选中，
        /// 结果把某一段拖走了。
        /// </summary>
        private bool HitTest(IntakeProfile p, Rect r, Vector2 m, out int kind, out int ia, out int ib)
        {
            kind = 0; ia = -1; ib = -1;
            float bestD = GrabRadius;
            int bestI = -1, bestKind = 0;

            for (int i = 0; i < p.Sections.Count; i++)
            {
                IntakeSection s = p.Sections[i];
                double c = CenterOf(s), xTop, xBot, h;
                SectionLine(s, out xTop, out xBot, out h);
                float px, py;
                Map(p, xTop, c + h, r, out px, out py);
                float d = Vector2.Distance(m, new Vector2(px, py));
                if (d < bestD) { bestD = d; bestI = i; bestKind = 5; }
                Map(p, s.X, c, r, out px, out py);
                d = Vector2.Distance(m, new Vector2(px, py));
                if (d < bestD) { bestD = d; bestI = i; bestKind = 3; }
                Map(p, xBot, c - h, r, out px, out py);
                d = Vector2.Distance(m, new Vector2(px, py));
                if (d < bestD) { bestD = d; bestI = i; bestKind = 6; }
            }
            if (bestI >= 0) { kind = bestKind; ia = bestI; return true; }

            // 截面的竖线（改轴向位置）：**排在边之前**。
            // 以前排在最后，而相邻两段的轮廓线在截面竖线附近总会穿过 8 px 判定圈，
            // 于是"想左右拖这段、结果抓到了边"——也就是玩家说的"侧视里点不中"。
            // 竖线是整条可抓的，判据只有水平距离，上下随便点。
            float bestX = VLineHalf + 1;
            int lineI = -1;
            for (int i = 0; i < p.Sections.Count; i++)
            {
                IntakeSection s = p.Sections[i];
                double c = CenterOf(s), xTop, xBot, h;
                SectionLine(s, out xTop, out xBot, out h);
                float ax, ay, bx, by;
                Map(p, xTop, c + h, r, out ax, out ay);
                Map(p, xBot, c - h, r, out bx, out by);
                // 有倾角时这条带子是斜的：还按"到 X 那一竖的距离"判，玩家会点在带子边上却抓不住。
                float d = DistanceToSegment(m, new Vector2(ax, ay), new Vector2(bx, by));
                if (d < bestX) { bestX = d; lineI = i; }
            }
            if (lineI >= 0) { kind = 1; ia = lineI; return true; }

            // 边：相邻两段之间的上/下轮廓线，8 px 内算抓住
            float bestE = 8f;
            for (int i = 0; i + 1 < p.Sections.Count; i++)
            {
                IntakeSection a = p.Sections[i], b = p.Sections[i + 1];
                for (int side = 0; side < 2; side++)
                {
                    double os = side == 0 ? 0.5 : -0.5;
                    float x1, y1, x2, y2;
                    Map(p, a.X, CenterOf(a) + os * SizeOf(a), r, out x1, out y1);
                    Map(p, b.X, CenterOf(b) + os * SizeOf(b), r, out x2, out y2);
                    float dd = DistanceToSegment(m, new Vector2(x1, y1), new Vector2(x2, y2));
                    if (dd < bestE) { bestE = dd; kind = side == 0 ? 5 : 6; ia = i; ib = i + 1; }
                }
            }
            if (kind != 0) return true;
            return false;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-6f) return Vector2.Distance(p, a);
            float t = Vector2.Dot(p - a, ab) / len2;
            t = Mathf.Clamp01(t);
            return Vector2.Distance(p, a + ab * t);
        }

        private bool HandleMouse(Rect r, IntakeProfile p)
        {
            Event e = Event.current;
            if (e == null) return false;
            if (!r.Contains(e.mousePosition) && Drag == 0)
            {
                if (HoverIndex != -1 || HoverKind != 0) { HoverIndex = -1; HoverKind = 0; MarkDirty(); }
                return false;
            }

            double mx, my;
            Unmap(p, e.mousePosition.x, e.mousePosition.y, r, out mx, out my);

            if (e.type == EventType.MouseMove || (e.type == EventType.MouseDown) || e.type == EventType.MouseDrag)
            {
                if (Drag == 0)
                {
                    int hk, ia, ib;
                    bool hit = HitTest(p, r, e.mousePosition, out hk, out ia, out ib);
                    int ni = hit ? ia : -1, nk = hit ? hk : 0;
                    if (ni != HoverIndex || nk != HoverKind) { HoverIndex = ni; HoverKind = nk; MarkDirty(); }
                }
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                FreezeBox(p);
                int kd, ia, ib;
                if (!HitTest(p, r, e.mousePosition, out kd, out ia, out ib))
                {
                    // 点空白：什么都不选、什么都不拖。这是玩家"取消选中"的手势。
                    Drag = 0;
                    _dragIndex = -1;
                    e.Use();
                    return false;
                }
                Drag = kd;
                Selected = ia;
                _dragIndex = ia;
                _dragIndexB = ib;
                _pressX = mx;
                _pressY = my;
                _initXa = p.Sections[ia].X;
                _initOa = CenterOf(p.Sections[ia]);
                _initHa = SizeOf(p.Sections[ia]);
                _initTilt = TiltOf(p.Sections[ia]);
                _initXb = ib >= 0 ? p.Sections[ib].X : _initXa;
                _initOb = ib >= 0 ? CenterOf(p.Sections[ib]) : _initOa;
                _initHb = ib >= 0 ? SizeOf(p.Sections[ib]) : _initHa;
                e.Use();
                return false;
            }

            if (e.type == EventType.MouseDrag && Drag != 0 && p.Sections.Count > 0)
            {
                int ia = _dragIndex >= 0 && _dragIndex < p.Sections.Count ? _dragIndex : Selected;
                int ib = _dragIndexB >= 0 && _dragIndexB < p.Sections.Count ? _dragIndexB : -1;
                IntakeSection A = p.Sections[ia];
                IntakeSection B = ib >= 0 ? p.Sections[ib] : null;
                bool fine = e.shift;                 // 按住 Shift 走 1/5 步，微调用
                double kk = fine ? 0.2 : 1.0;
                bool touched = false;

                if (Drag == 1)
                {
                    // 轴向：只改这一段自己的位置，且不许越过前后邻居（越过去截面顺序就乱了）。
                    // **第一段原先被按死在 X ≥ 0**：那个 0 不是几何约束，只是"第一段原来恰好在 0"，
                    // 于是往里拖（想让唇口往前伸）一律没反应。最后一段一直能往后拖 2 m，
                    // 第一段凭什么不能往前拖——两头对称给 ±2 m。
                    double nx = _initXa + (mx - _pressX) * kk;
                    double lo = ia > 0 ? p.Sections[ia - 1].X + 0.02 : _initXa - 2.0;
                    double hi = ia < p.Sections.Count - 1 ? p.Sections[ia + 1].X - 0.02 : _initXa + 2.0;
                    nx = Math.Min(Math.Max(nx, lo), hi);
                    if (Math.Abs(nx - A.X) > 1e-4) { A.X = nx; touched = true; }
                }
                else if (Drag == 3)
                {
                    double ny = _initOa + (my - _pressY) * kk;
                    SetCenter(A, ny);
                    touched = true;
                }
                else if (Drag == 5 || Drag == 6)
                {
                    // 边缘：高度是"往两边各长一半"的量，所以位移要乘 2 才是高度的变化。
                    // 边牵着两个截面时，两段的高一起变——这正是一整条轮廓线该有的行为。
                    double dh = 2.0 * (my - _pressY) * kk * (Drag == 5 ? 1.0 : -1.0) * AxisSign;
                    SetSize(A, _initHa + dh);
                    if (B != null) SetSize(B, _initHb + dh);

                    // 单颗边缘点还能**横着拖**：把这一圈截面斜过来（F-15 那种前掠唇口）。
                    // 拖整条边时不做——一条边牵着两段，"横着拖"说不清是让谁歪，
                    // 硬要猜就会变成"想改倾斜、结果两段一起歪"。
                    if (B == null)
                    {
                        double half = Math.Max(0.5 * _initHa, 1e-3);
                        // 这一点在截面局部坐标里的位置：侧视是竖向 ±半高，俯视是横向 ±半宽
                        double edge = (Drag == 5 ? 1.0 : -1.0) * half;
                        double dux = (mx - _pressX) * kk;
                        // 点的轴向位置 = X + tan(倾角)·edge ⇒ 拖了 dux 就有 tan 增量 dux/edge
                        double tanNew = Math.Tan(_initTilt * Math.PI / 180.0) + dux / edge;
                        SetTilt(A, Math.Atan(tanNew) * 180.0 / Math.PI);
                    }
                    touched = true;
                }

                if (touched) { e.Use(); return true; }
            }

            if (e.type == EventType.MouseUp)
            {
                if (Drag != 0)
                {
                    Drag = 0;
                    _dragIndex = -1;
                    _dragIndexB = -1;
                    _boxFrozen = false;      // 松手才按新的量程重画
                    MarkDirty();
                    e.Use();
                }
            }
            return false;
        }

        /// <summary>把这条气道画进贴图：半透明填充 + 上下边缘线 + 中轴线 + 每个截面的手柄。</summary>
        private void Redraw(IntakeProfile p)
        {
            if (_tex == null)
            {
                _tex = new Texture2D(_tw, _th, TextureFormat.RGBA32, false);
                _tex.filterMode = FilterMode.Bilinear;
            }
            Color32 bg = new Color32(22, 26, 32, 255);
            Color32 edge = new Color32(130, 200, 235, 255);
            Color32 axis = new Color32(70, 84, 96, 255);
            Color32 handle = new Color32(255, 190, 90, 255);
            Color32 handleSel = new Color32(255, 240, 200, 255);
            Color32 hover = new Color32(120, 255, 180, 255);

            Color32[] px = new Color32[_tw * _th];
            for (int i = 0; i < px.Length; i++) px[i] = bg;

            Rect r = new Rect(0f, 0f, _tw, _th);
            int N = 40;
            double x0 = p.Sections[0].X;
            double x1 = p.Sections[p.Sections.Count - 1].X;
            float[] topX = new float[N + 1], topY = new float[N + 1];
            float[] botX = new float[N + 1], botY = new float[N + 1];
            for (int i = 0; i <= N; i++)
            {
                IntakeSection s = p.Sample(x0 + (x1 - x0) * i / N);
                double c = CenterOf(s), xTop, xBot, h;
                SectionLine(s, out xTop, out xBot, out h);
                Map(p, xTop, c + h, r, out topX[i], out topY[i]);
                Map(p, xBot, c - h, r, out botX[i], out botY[i]);
            }
            for (int i = 0; i < N; i++)
            {
                FillBetween(px, topX[i], topY[i], botX[i], botY[i]);
                Line(px, topX[i], topY[i], topX[i + 1], topY[i + 1], edge);
                Line(px, botX[i], botY[i], botX[i + 1], botY[i + 1], edge);
            }
            // 中轴线
            float ax0, ay0, ax1, ay1;
            Map(p, x0, 0.0, r, out ax0, out ay0);
            Map(p, x1, 0.0, r, out ax1, out ay1);
            Line(px, ax0, ay0, ax1, ay1, axis);

            // 每一段的竖线：拖它就是改这一段的轴向位置（前后）。
            // 首尾两根单独上色——那是唇口与发动机面这两条"特征线"，玩家最常调的就是它们。
            Color32 lip = new Color32(120, 230, 150, 255);
            Color32 face = new Color32(255, 140, 110, 255);
            Color32 mid = new Color32(90, 150, 190, 255);
            for (int i = 0; i < p.Sections.Count; i++)
            {
                IntakeSection s = p.Sections[i];
                double c = CenterOf(s), xTop, xBot, h;
                SectionLine(s, out xTop, out xBot, out h);
                float xa, ya, xb, yb;
                Map(p, xTop, c + h, r, out xa, out ya);
                Map(p, xBot, c - h, r, out xb, out yb);
                bool hot = HoverIndex == i && HoverKind == 1;
                Color32 lc = hot ? hover : (i == 0 ? lip : (i == p.Sections.Count - 1 && i != 0 ? face : mid));
                // 画成一条**带**而不是一根线，宽度就是能抓的范围（22 px）：看得见的都能点中。
                // 以前画 1 px、命中 8 px，"看得见"和"点得着"两回事，玩家自然觉得拖不动。
                for (int k = -VLineHalf; k <= VLineHalf; k++)
                {
                    Color32 c2 = lc;
                    int a = 60 + (VLineHalf - Math.Abs(k)) * 14;
                    if (a > lc.a) a = lc.a;
                    c2.a = (byte)a;
                    Line(px, xa + k, ya, xb + k, yb, c2);
                }
                Line(px, xa, ya, xb, yb, lc);                       // 中间一根实线，边界清楚
                if (i == 0 || i == p.Sections.Count - 1 || hot)
                    Line(px, xa + 1f, ya, xb + 1f, yb, lc);
            }

            // 手柄：每个截面上边缘、中心、下边缘（悬停那颗画个绿环，按下时也算"你能抓这里"）
            for (int i = 0; i < p.Sections.Count; i++)
            {
                IntakeSection s = p.Sections[i];
                double c = CenterOf(s);
                double xTop, xBot, h;
                SectionLine(s, out xTop, out xBot, out h);
                for (int side = 0; side < 3; side++)
                {
                    double w = side == 0 ? c + h : (side == 1 ? c : c - h);
                    double xx = side == 0 ? xTop : (side == 1 ? s.X : xBot);
                    int kindOf = side == 0 ? 5 : (side == 1 ? 3 : 6);
                    float hx, hy;
                    Map(p, xx, w, r, out hx, out hy);
                    Color32 hc = (HoverIndex == i && HoverKind == kindOf) ? hover : (i == Selected ? handleSel : handle);
                    int rad = (HoverIndex == i && HoverKind == kindOf) ? 5 : (i == Selected ? 4 : 3);
                    Dot(px, hx, hy, hc, rad);
                }
            }

            // Unity 纹理行序自下而上，而这里坐标按"屏幕 y 向下"算 —— 不翻这一下，
            // 整张图上下颠倒，手柄看着在下、实际能点在上的位置（同一根因见 IntakeSectionView.Upload）。
            if (_flip == null || _flip.Length != px.Length) _flip = new Color32[px.Length];
            for (int y = 0; y < _th; y++)
                System.Array.Copy(px, y * _tw, _flip, (_th - 1 - y) * _tw, _tw);
            _tex.SetPixels32(_flip);
            _tex.Apply(false);
            _dirty = false;
            _last = p;
        }

        // ---------- 光栅化小工具（都做边界裁剪，参数再离谱也不越界） ----------

        private void Px(Color32[] px, float fx, float fy, Color32 c)
        {
            int x = Mathf.RoundToInt(fx), y = Mathf.RoundToInt(fy);
            if (x < 0 || y < 0 || x >= _tw || y >= _th) return;
            px[y * _tw + x] = c;
        }

        private void Dot(Color32[] px, float cx, float cy, Color32 c, int rad)
        {
            for (int dy = -rad; dy <= rad; dy++)
                for (int dx = -rad; dx <= rad; dx++)
                {
                    if (dx * dx + dy * dy > rad * rad + 1) continue;
                    Px(px, cx + dx, cy + dy, c);
                }
        }

        private void Line(Color32[] px, float x0, float y0, float x1, float y1, Color32 c)
        {
            int steps = (int)Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1;
            if (steps <= 0) return;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                Px(px, x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, c);
            }
        }

        /// <summary>两条竖线之间的竖条填充（截面本身就画成竖条，这样"里面是空的"一眼看得出）。</summary>
        private void FillBetween(Color32[] px, float xa, float ya, float xb, float yb)
        {
            float lo = Math.Min(ya, yb), hi = Math.Max(ya, yb);
            if (hi - lo < 0.5f) return;
            Color32 fill = new Color32(40, 66, 86, 255);
            // 有截面倾角时这条竖条是**斜的**（上下两端的轴向位置不同），
            // 所以按 y 在两点之间插值取 x；以前只在两端 x 差不多时才填，斜过来就整条不填了。
            for (float y = lo; y <= hi; y += 1f)
            {
                float t = Math.Abs(yb - ya) < 1e-4f ? 0f : (y - ya) / (yb - ya);
                if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                Px(px, xa + (xb - xa) * t, y, fill);
            }
        }
    }
}