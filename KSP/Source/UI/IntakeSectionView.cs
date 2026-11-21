using System;
using JetForge.Core;
using UnityEngine;

namespace JetForge.UI
{
    /// <summary>
    /// 单个截面的 2D 直视编辑页——**这是改"这个截面长什么样"的地方**。
    ///
    /// 为什么要把职责拆开：侧视/俯视两页画的是整条气道的骨架，那里拖一个点，
    /// 改的是"这一段摆在轴的哪儿"（轴向位置、上下/左右偏移）；而截面的宽高、圆角、形状
    /// 属于它自己，在那里拖会让人分不清"我是把截面挪远了，还是把它变胖了"。
    /// 拆成两页之后每页只有一件事：**骨架页摆位置，截面页改形状。**
    ///
    /// 画的是这个截面朝下游看的正面（跟风洞观察窗一样）：外框是超椭圆轮廓，
    /// 中心十字是这一段的实际中心（偏移之后它可能不在原点，这正是 S 弯的来源）。
    /// 四个手柄分别改左右宽与上下高，中间那个改偏移。
    ///
    /// 坐标换算的冻结逻辑跟 <see cref="IntakeProfileView"/> 一样：拖动时把视野钉住，
    /// 不然"量程跟着手指变"会让数值来回跳。
    /// </summary>
    public sealed class IntakeSectionView
    {
        /// <summary>正在拖什么：0 无、1 宽、2 高、3 偏移、4 多边形顶点。</summary>
        public int Drag = 0;

        /// <summary>正在拖的那个多边形顶点（Drag == 4 时有效）。</summary>
        public int Vertex = -1;

        /// <summary>开着 XY 轴对称时只让顶点动第一象限（U≥0、W≥0）。</summary>
        public bool LockQuarter;

        // 按下那一刻的指针与初值。拖动只认增量，不然"按下"这一帧就把形状拽到指针底下。
        private double _pressU, _pressW;
        private double _width0, _height0, _offsetZ0, _offsetY0, _polyU0, _polyW0;

        private Texture2D _tex;
        private Color32[] _flip;
        private bool _dirty = true;
        private int _tw = 360, _th = 360;

        private bool _frozen;
        private double _half;
        private double _cx, _cy;

        private const int Pad = 12;

        public void MarkDirty() { _dirty = true; }

        public bool Draw(Rect rect, IntakeSection s)
        {
            if (s == null)
            {
                GUI.Label(rect, "  没有选中截面");
                return false;
            }
            // 正方形画布：宽高比被窗口拉变形的话，圆截面会画成椭圆，
            // 更要命的是"画在哪儿"和"点在哪儿"用的是两套比例——珠子就在眼前却拖不动。
            Rect sq = Fit(rect);
            bool changed = HandleMouse(sq, s);
            if (changed) MarkDirty();
            if (_dirty || _tex == null) Redraw(s);
            GUI.DrawTexture(sq, _tex);
            // 读数报**真实外形**（多边形的包围盒），并在不等于名义框时把框也写出来。
            // 以前这里只报 Width/Height，于是"宽 0.600 × 高 0.600"和"面积 0.2242 m²"
            // 摆在同一个窗口里，谁也解释不了这俩为什么对不上——实际外形是 0.600 × 0.471。
            double rw, rh;
            s.RealExtent(out rw, out rh);
            bool nominal = Math.Abs(rw - s.Width) > 5e-4 || Math.Abs(rh - s.Height) > 5e-4;
            string head = nominal
                ? string.Format("{0} 实际 {1:F3} × {2:F3} m（框 {3:F3} × {4:F3}）\n中心 {5:F3}, {6:F3}",
                    ShapeName(s), rw, rh, s.Width, s.Height, s.OffsetZ, s.OffsetY)
                : string.Format("{0} 宽 {1:F3} × 高 {2:F3} m｜中心 {3:F3}, {4:F3}",
                    ShapeName(s), rw, rh, s.OffsetZ, s.OffsetY);
            GUI.Label(new Rect(sq.x + 4f, sq.y + 2f, sq.width - 8f, 18f), head);
            return changed;
        }

        /// <summary>从给的矩形里取一个居中的正方形（边长取短边）。</summary>
        public static Rect Fit(Rect r)
        {
            float side = Mathf.Min(r.width, r.height);
            if (side < 40f) side = 40f;
            return new Rect(r.x + (r.width - side) * 0.5f, r.y + (r.height - side) * 0.5f, side, side);
        }

        private static string ShapeName(IntakeSection s)
        {
            if (s.HasPolygon) return string.Format("自由多边形（{0} 点）", s.Polygon.Count);
            switch (s.Shape)
            {
                case IntakeSectionShape.Circle: return "圆";
                case IntakeSectionShape.Ellipse: return "椭圆";
                default: return string.Format("超椭圆 n={0:F1}", s.EffectiveExponent);
            }
        }

        private void ComputeBox(IntakeSection s)
        {
            // 视野要同时装下"最大那个半尺寸"和"偏移之后中心跑到哪儿"，
            // 否则把截面推到边上会被裁掉，看起来像消失。
            double ext = 0.5 * Math.Max(s.Width, s.Height);
            double ox = Math.Abs(s.OffsetZ), oy = Math.Abs(s.OffsetY);
            _half = Math.Max(Math.Max(ext + Math.Max(ox, oy), 0.05) * 1.15, 0.06);
            _cx = 0.0; _cy = 0.0;
        }

        private void Map(double u, double w, Rect r, out float sx, out float sy)
        {
            // 注意 u 是横向（型线 Z）、w 是上下（型线 Y）——跟网格里 RingPoints 的约定一致：
            // 局部 X 是横向、Z 是上下、Y 是轴。视图里横屏画，所以 screenX ← u、screenY ← −w。
            sx = r.x + r.width * 0.5f + (float)(u / _half) * (r.width * 0.5f - Pad);
            sy = r.y + r.height * 0.5f - (float)(w / _half) * (r.height * 0.5f - Pad);
        }

        private void Unmap(float px, float py, Rect r, out double u, out double w)
        {
            u = (px - (r.x + r.width * 0.5f)) / Math.Max(r.width * 0.5f - Pad, 1f) * _half;
            w = (r.y + r.height * 0.5f - py) / Math.Max(r.height * 0.5f - Pad, 1f) * _half;
        }

        private bool HandleMouse(Rect r, IntakeSection s)
        {
            Event e = Event.current;
            if (e == null) return false;
            if (!r.Contains(e.mousePosition) && Drag == 0) return false;

            if (!_frozen) ComputeBox(s);

            double u, w;
            Unmap(e.mousePosition.x, e.mousePosition.y, r, out u, out w);
            double halfW = 0.5 * s.Width, halfH = 0.5 * s.Height;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                ComputeBox(s);
                _frozen = true;
                // 记下"按下那一刻"的指针位置与全部初值。拖动只认增量——
                // 以前是拿指针的绝对位置直接算新尺寸/新顶点，于是"按下"这一帧就把形状
                // 拽到指针底下：手柄画在顶点旁边几像素，点下去顶点就跳几像素，看着像乱跳。
                _pressU = u; _pressW = w;
                _width0 = s.Width; _height0 = s.Height;
                _offsetZ0 = s.OffsetZ; _offsetY0 = s.OffsetY;
                _polyU0 = 0.0; _polyW0 = 0.0;
                Vector2 m = e.mousePosition;
                const float tol = 10f;

                // 自由多边形优先判顶点：形状是玩家自己摆的，拖哪颗珠子就动哪个点。
                if (s.HasPolygon)
                {
                    int best = -1;
                    float bestD = tol;
                    for (int i = 0; i < s.Polygon.Count; i++)
                    {
                        float px2, py2;
                        Map(s.OffsetZ + s.Polygon[i].U * halfW, s.OffsetY + s.Polygon[i].W * halfH, r, out px2, out py2);
                        float d = Vector2.Distance(m, new Vector2(px2, py2));
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    if (best >= 0)
                    {
                        Drag = 4;
                        Vertex = best;
                        _polyU0 = s.Polygon[best].U;
                        _polyW0 = s.Polygon[best].W;
                        e.Use();
                        return false;
                    }
                }

                float rx, ry, tx, ty, mx, my;
                Map(s.OffsetZ + halfW, s.OffsetY, r, out rx, out ry);      // 右侧
                Map(s.OffsetZ, s.OffsetY + halfH, r, out tx, out ty);      // 上缘
                Map(s.OffsetZ, s.OffsetY, r, out mx, out my);              // 中心
                Vector2 m2 = e.mousePosition;
                if (Vector2.Distance(m2, new Vector2(rx, ry)) < tol) Drag = 1;
                else if (Vector2.Distance(m, new Vector2(tx, ty)) < tol) Drag = 2;
                else if (Vector2.Distance(m, new Vector2(mx, my)) < tol) Drag = 3;
                else Drag = 0;
                e.Use();
                return false;
            }

            if (e.type == EventType.MouseDrag && Drag != 0)
            {
                double k = e.shift ? 0.2 : 1.0;
                double du = (u - _pressU) * k;
                double dw = (w - _pressW) * k;
                if (Drag == 4)
                {
                    // 顶点存的是归一化坐标：这里先换算成"相对这一截面半宽/半高"的比例，
                    // 于是以后改宽高时形状不变、只是整体缩放。
                    if (s.HasPolygon && Vertex >= 0 && Vertex < s.Polygon.Count)
                    {
                        double nu = _polyU0 + du / Math.Max(halfW, 1e-6);
                        double nw = _polyW0 + dw / Math.Max(halfH, 1e-6);
                        nu = Math.Min(Math.Max(nu, -1.6), 1.6);
                        nw = Math.Min(Math.Max(nw, -1.6), 1.6);
                        // 开着 XY 轴对称时只让动第一象限：点在别的象限会被镜像过来变样，
                        // 与其生成一个玩家没画过的形状，不如把点顶在轴上。
                        if (LockQuarter)
                        {
                            nu = Math.Max(nu, 0.0);
                            nw = Math.Max(nw, 0.0);
                        }
                        s.Polygon[Vertex].U = nu;
                        s.Polygon[Vertex].W = nw;
                        // 手工改过的形状就是新的"镜像基准"：不重设的话，
                        // 勾着的镜像开关下次会拿旧基准重算，刚拖的点被抹掉。
                        s.BakeMirrorBase();
                        e.Use(); return true;
                    }
                    return false;
                }
                if (Drag == 1)
                {
                    // 拖右缘：宽变两倍"中心到手指"的距离。中心不动，所以左边跟着对称走——
                    // 这就是"改尺寸"，不是"挪位置"。
                    double target = _width0 + 2.0 * du;
                    s.Width = Math.Min(Math.Max(target, 0.04), 3.0);
                    e.Use(); return true;
                }
                if (Drag == 2)
                {
                    double target = _height0 + 2.0 * dw;
                    s.Height = Math.Min(Math.Max(target, 0.04), 3.0);
                    e.Use(); return true;
                }
                if (Drag == 3)
                {
                    s.OffsetZ = Math.Min(Math.Max(_offsetZ0 + du, -1.5), 1.5);
                    s.OffsetY = Math.Min(Math.Max(_offsetY0 + dw, -1.5), 1.5);
                    e.Use(); return true;
                }
            }

            if (e.type == EventType.MouseUp)
            {
                if (Drag != 0)
                {
                    Drag = 0;
                    _frozen = false;
                    MarkDirty();
                    e.Use();
                }
            }
            return false;
        }

        /// <summary>把截面画进贴图：外轮廓 + 中心十字 + 三个手柄。</summary>
        private void Redraw(IntakeSection s)
        {
            if (_tex == null)
            {
                _tex = new Texture2D(_tw, _th, TextureFormat.RGBA32, false);
                _tex.filterMode = FilterMode.Bilinear;
            }
            ComputeBox(s);

            Color32 bg = new Color32(22, 26, 32, 255);
            Color32 fill = new Color32(40, 66, 86, 255);
            Color32 edge = new Color32(130, 200, 235, 255);
            Color32 axis = new Color32(70, 84, 96, 255);
            Color32 handle = new Color32(255, 190, 90, 255);

            Color32[] px = new Color32[_tw * _th];
            for (int i = 0; i < px.Length; i++) px[i] = bg;

            Rect r = new Rect(0f, 0f, _tw, _th);

            // 填充：按 OutlinePoint 逐行扫（与面积公式同源，看起来就是它算出来的那个形状）
            for (int iy = 0; iy < _th; iy++)
            {
                double w = (r.height * 0.5f - (iy + 0.5f)) / Math.Max(r.height * 0.5f - Pad, 1f) * _half;
                if (Math.Abs(w - s.OffsetY) > 0.5 * s.Height) continue;
                int x0 = int.MaxValue, x1 = int.MinValue;
                for (int ix = 0; ix < _tw; ix++)
                {
                    double u = ((ix + 0.5f) - r.width * 0.5f) / Math.Max(r.width * 0.5f - Pad, 1f) * _half;
                    if (Inside(s, u, w)) { if (ix < x0) x0 = ix; if (ix > x1) x1 = ix; }
                }
                if (x1 >= x0)
                    for (int ix = x0; ix <= x1; ix++) px[iy * _tw + ix] = fill;
            }

            // 轮廓：多边形直接连顶点；参数化形状 θ 采样 96 点（超椭圆与圆/椭圆同一条公式）
            if (s.HasPolygon)
            {
                double hw = 0.5 * s.Width, hh = 0.5 * s.Height;
                float prevX = 0f, prevY = 0f;
                for (int i = 0; i <= s.Polygon.Count; i++)
                {
                    SectionPoint p = s.Polygon[i % s.Polygon.Count];
                    float sx, sy;
                    Map(s.OffsetZ + p.U * hw, s.OffsetY + p.W * hh, r, out sx, out sy);
                    if (i > 0) Line(px, prevX, prevY, sx, sy, edge);
                    prevX = sx; prevY = sy;
                }
                for (int i = 0; i < s.Polygon.Count; i++)
                {
                    SectionPoint p = s.Polygon[i];
                    float sx, sy;
                    Map(s.OffsetZ + p.U * hw, s.OffsetY + p.W * hh, r, out sx, out sy);
                    Color32 c = i == Vertex && Drag == 4 ? new Color32(255, 240, 200, 255) : handle;
                    Dot(px, sx, sy, 3, c);
                }
            }
            else
            {
                int N = 96;
                float prevX = 0f, prevY = 0f;
                for (int i = 0; i <= N; i++)
                {
                    double th = i / (double)N * Math.PI * 2.0;
                    double u, w;
                    s.OutlinePoint(th, out u, out w);
                    float sx, sy;
                    Map(s.OffsetZ + u, s.OffsetY + w, r, out sx, out sy);
                    if (i > 0) Line(px, prevX, prevY, sx, sy, edge);
                    prevX = sx; prevY = sy;
                }
            }

            // 中轴与手柄
            float ccx, ccy;
            Map(s.OffsetZ, s.OffsetY, r, out ccx, out ccy);
            Line(px, Pad, ccy, _tw - Pad, ccy, axis);
            Line(px, ccx, Pad, ccx, _th - Pad, axis);
            Dot(px, ccx, ccy, 4, handle);
            float hx, hy;
            Map(s.OffsetZ + 0.5 * s.Width, s.OffsetY, r, out hx, out hy);
            Dot(px, hx, hy, 3, handle);
            Map(s.OffsetZ, s.OffsetY + 0.5 * s.Height, r, out hx, out hy);
            Dot(px, hx, hy, 3, handle);

            Upload(px);
            _dirty = false;
        }

        /// <summary>
        /// 上传前把行序翻过来。这里所有绘制坐标都按"屏幕 y 向下"算（第 0 行在顶上），
        /// 而 Unity 的 Texture2D 行序是自下而上（索引 0 = 最下一行）、GUI.DrawTexture 也把
        /// v=0 画在矩形底部。不翻这一下的三个症状是同一个根：
        /// 图形上下颠倒、"看着的珠子"与"能拖的珠子"互为竖直镜像、竖直拖动感觉是反的。
        /// </summary>
        private void Upload(Color32[] px)
        {
            if (_flip == null || _flip.Length != px.Length) _flip = new Color32[px.Length];
            int row = _tw;
            for (int y = 0; y < _th; y++)
                System.Array.Copy(px, y * row, _flip, (_th - 1 - y) * row, row);
            _tex.SetPixels32(_flip);
            _tex.Apply();
        }

        private static bool Inside(IntakeSection s, double u, double w)
        {
            double a = 0.5 * s.Width, b = 0.5 * s.Height;
            if (a <= 1e-9 || b <= 1e-9) return false;
            double du = (u - s.OffsetZ) / a, dw = (w - s.OffsetY) / b;
            if (s.HasPolygon)
            {
                // 射线法：填充必须跟"面积用鞋带公式算的那个多边形"是同一个，
                // 否则图上看着实心、面积却是空的（或者反过来）。
                bool inside = false;
                for (int i = 0, j = s.Polygon.Count - 1; i < s.Polygon.Count; j = i++)
                {
                    double ui = s.Polygon[i].U, wi = s.Polygon[i].W;
                    double uj = s.Polygon[j].U, wj = s.Polygon[j].W;
                    if ((wi > dw) != (wj > dw) &&
                        du < (uj - ui) * (dw - wi) / (wj - wi) + ui)
                        inside = !inside;
                }
                return inside;
            }
            double n = s.EffectiveExponent;
            if (n <= 2.0001) return du * du + dw * dw <= 1.0;
            return Math.Pow(Math.Abs(du), n) + Math.Pow(Math.Abs(dw), n) <= 1.0;
        }

        private void Dot(Color32[] px, float x, float y, int rad, Color32 c)
        {
            int xi = Mathf.RoundToInt(x), yi = Mathf.RoundToInt(y);
            for (int dy = -rad; dy <= rad; dy++)
            {
                for (int dx = -rad; dx <= rad; dx++)
                {
                    if (dx * dx + dy * dy > rad * rad) continue;
                    int xx = xi + dx, yy = yi + dy;
                    if (xx < 0 || yy < 0 || xx >= _tw || yy >= _th) continue;
                    px[yy * _tw + xx] = c;
                }
            }
        }

        private void Line(Color32[] px, float x0, float y0, float x1, float y1, Color32 c)
        {
            float dx = x1 - x0, dy = y1 - y0;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
            if (steps < 1) steps = 1;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                int x = Mathf.RoundToInt(x0 + dx * t);
                int y = Mathf.RoundToInt(y0 + dy * t);
                if (x < 0 || y < 0 || x >= _tw || y >= _th) continue;
                px[y * _tw + x] = c;
            }
        }
    }
}