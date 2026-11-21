using System;
using System.Collections.Generic;

namespace JetForge.Core
{

    /// <summary>
    /// partial：截面的镜像与旋转。拖动画点之后要按轴翻、要转四分之一圈，
    /// 都在这里；它直接改 <see cref="IntakeSection.Points"/>，不碰预设也不碰量出来的几何。
    /// </summary>
    public sealed partial class IntakeSection
    {

        private List<SectionPoint> _mirrorBase;
        private bool _mirrorX;
        private bool _mirrorY;

        /// <summary>X 轴镜像（上下翻）现在是"勾选"状态：勾上=W 取反，取消=回到基准。</summary>
        public bool MirrorXOn { get { return _mirrorX; } }

        /// <summary>Y 轴镜像（左右翻）的勾选状态。</summary>
        public bool MirrorYOn { get { return _mirrorY; } }

        /// <summary>
        /// 把当前轮廓记成"镜像基准"：之后两个镜像开关都以它为起点重新组合。
        ///
        /// **为什么要有基准**：以前每次按镜像都直接在顶点上取反，按第一次 W 反了、顺手把顶点顺序
        /// 掰回逆时针，按第二次 U 又反一次——两次下来不是"各自翻一次"，而是"翻转 + 旋转"，
        /// 玩家看到的是第一个开关被悄悄改掉了（"两个按钮叠在一起"）。
        /// 有基准之后，X、Y 是**两个独立的开关**：想要哪种组合，就把基准按那组符号重算一遍。
        /// 手工拖点、加减顶点、套预设之后要再调一次，否则勾着的镜像会把手改的形状抹掉。
        /// </summary>
        public void BakeMirrorBase()
        {
            _mirrorBase = HasPolygon ? ClonePolygon(Polygon) : null;
            _mirrorX = false;
            _mirrorY = false;
        }

        /// <summary>按两个开关重算轮廓；两个都关就等于回到基准。返回是否动作。</summary>
        public bool SetMirror(bool xAxis, bool yAxis)
        {
            if (!HasPolygon) return false;
            if (_mirrorBase == null || _mirrorBase.Count != Polygon.Count) BakeMirrorBase();
            if (_mirrorBase == null) return false;
            List<SectionPoint> p = new List<SectionPoint>(_mirrorBase.Count);
            for (int i = 0; i < _mirrorBase.Count; i++)
            {
                // xAxis = 绕 X 轴翻（上下翻）→ W 取反；yAxis = 绕 Y 轴翻（左右翻）→ U 取反。
                // 这里写反过一次：翻转的是"垂直于转轴"的那个坐标，X 轴镜像动的是 W。
                SectionPoint b = _mirrorBase[i];
                p.Add(new SectionPoint { U = yAxis ? -b.U : b.U, W = xAxis ? -b.W : b.W });
            }
            Polygon = p;
            EnsureCounterClockwise();
            CanonicalStart();
            _mirrorX = xAxis;
            _mirrorY = yAxis;
            return true;
        }

        /// <summary>X 轴镜像（上下翻）：把"上下翻"这个开关切一下，另一轴保持原样。</summary>
        public bool MirrorUpDown()
        {
            return SetMirror(!_mirrorX, _mirrorY);
        }

        /// <summary>Y 轴镜像（左右翻）：切"左右翻"，上下那个开关不受影响。</summary>
        public bool MirrorLeftRight()
        {
            return SetMirror(_mirrorX, !_mirrorY);
        }

        /// <summary>
        /// 1/4 镜像：只留第一象限（U≥0 且 W≥0）的顶点，再生成四份对称副本。
        /// 点数不够（象限里少于 2 个点）、或者生成的形状闭合不了，返回 false 且不动原形状。
        /// </summary>
        public bool MirrorQuarter()
        {
            string why;
            return MirrorQuarter(out why);
        }

        /// <summary>
        /// 1/4 镜像：只留第一象限（U≥0 且 W≥0）的顶点，再生成四份对称副本。
        ///
        /// **不只是"点数够不够"，还要问"拼回去还是原来那个形状吗"**：
        /// 梯形这种上下不对称的形状，折进第一象限再四份展开会变成一个近似的矩形——
        /// 面积变化不大，形状却已经不是玩家画的那个了（截图里那圈"看着还行、其实变了"就是这么来的）。
        /// 所以拼完先在 24 个方向上比一遍半径，走样超过 12% 就整体回滚、返回 false 并说明理由。
        /// </summary>
        public bool MirrorQuarter(out string why)
        {
            why = "";
            if (!HasPolygon) { why = "这不是自由多边形（圆/椭圆/超矩形本来就对称，不用拼）"; return false; }
            List<SectionPoint> before = new List<SectionPoint>(Polygon);
            bool usedFold = false;
            List<SectionPoint> basePts = new List<SectionPoint>();
            for (int i = 0; i < Polygon.Count; i++)
            {
                SectionPoint p = Polygon[i];
                if (p.U < -1e-6 || p.W < -1e-6) continue;
                double u = Math.Max(p.U, 0.0), w = Math.Max(p.W, 0.0);
                // 原点（型线中心）在四象限拼起来之后是内部点，留着会让轮廓凹进去一块，
                // 表现是"镜像完面积不是四倍，反而更小"。
                if (u < 1e-9 && w < 1e-9) continue;
                basePts.Add(new SectionPoint { U = u, W = w });
            }
            if (basePts.Count < 2)
            {
                // 第一象限里几乎没有点（比如用户只在下半平面画过）：把整条轮廓折进来再试一次。
                // 以前这里直接返回 false，界面上只是"点了没反应"，玩家会以为按钮坏了。
                List<SectionPoint> folded = new List<SectionPoint>(Polygon.Count);
                for (int i = 0; i < Polygon.Count; i++)
                {
                    SectionPoint p = Polygon[i];
                    double fu = Math.Abs(p.U), fw = Math.Abs(p.W);
                    if (fu < 1e-9 && fw < 1e-9) continue;
                    AddUnique(folded, fu, fw);
                }
                if (folded.Count < 2)
                {
                    why = "第一象限里的点不够（至少要 2 个），而且整圈折进来也不够";
                    return false;
                }
                basePts = folded;
                usedFold = true;
            }

            List<SectionPoint> pts = new List<SectionPoint>(basePts.Count * 4);
            for (int i = 0; i < basePts.Count; i++)
            {
                SectionPoint b = basePts[i];
                AddUnique(pts, b.U, b.W);
                AddUnique(pts, -b.U, b.W);
                AddUnique(pts, b.U, -b.W);
                AddUnique(pts, -b.U, -b.W);
            }
            if (pts.Count < 3) return false;

            // 按极角排序：绕一圈的顶点顺序才是闭合不自交的（乱序会让鞋带面积算成负数或零）。
            pts.Sort(delegate (SectionPoint a, SectionPoint b2)
            {
                return Math.Atan2(a.W, a.U).CompareTo(Math.Atan2(b2.W, b2.U));
            });

            // 同一个方向上如果有两个点（一个在外圈、一个在里圈），只能留外圈那个：
            // 沿射线来回跳会让轮廓自交，面积算出来偏小。
            List<SectionPoint> ring = new List<SectionPoint>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                SectionPoint p = pts[i];
                double ang = Math.Atan2(p.W, p.U);
                double rad = p.U * p.U + p.W * p.W;
                if (ring.Count > 0)
                {
                    SectionPoint last = ring[ring.Count - 1];
                    if (Math.Abs(Math.Atan2(last.W, last.U) - ang) < 1e-6)
                    {
                        if (rad > last.U * last.U + last.W * last.W) ring[ring.Count - 1] = p;
                        continue;
                    }
                }
                ring.Add(p);
            }
            if (ring.Count < 3) { Polygon = before; why = "拼出来的轮廓闭合不了（点数不够），没动形状"; return false; }
            Polygon = ring;
            // 极角升序本来就是逆时针，这里再兜一次底：以后有人改了排序方式也不会把绕向弄反。
            EnsureCounterClockwise();

            // 形状比对：四象限拼回来的轮廓，在 24 个方向上半径都要和原来差不多。
            // 只比面积会放过梯形这类"面积凑巧接近、形状已经变了"的情况。
            //
            // **走了折象限那条路就不比**：折进来的点集本来就只保留 |U|、|W|，
            // 拿它去跟折之前的原始轮廓（可能整条都在下半平面）比半径，必然差一大截，
            // 结果是"合法的折象限被自己判成不对称"。折象限的产物按构造就是四份对称的。
            if (!usedFold)
            {
                double worst = 0.0;
                for (int k = 0; k < 24; k++)
                {
                    double th = k * (2.0 * Math.PI / 24.0);
                    double r0 = RingRadius(before, th);
                    double r1 = RingRadius(Polygon, th);
                    if (r0 <= 1e-6) continue;
                    worst = Math.Max(worst, Math.Abs(r1 - r0) / r0);
                }
                if (worst > 0.12)
                {
                    Polygon = before;
                    why = string.Format("这个形状上下/左右不对称：拼成四象限后外轮廓最大偏了 {0:P0}，" +
                                        "已经不是原来那条线了。要么先用对称的预设，要么别开 XY 轴对称", worst);
                    return false;
                }
            }
            CanonicalStart();
            BakeMirrorBase();
            return true;
        }

        /// <summary>从原点沿 <paramref name="theta"/> 方向打到轮廓上的距离（找不到返回 0）。</summary>
        private static double RingRadius(List<SectionPoint> ring, double theta)
        {
            if (ring == null || ring.Count < 3) return 0.0;
            double du = Math.Cos(theta), dw = Math.Sin(theta);
            double best = double.MaxValue;
            for (int i = 0; i < ring.Count; i++)
            {
                SectionPoint a = ring[i], b = ring[(i + 1) % ring.Count];
                double eu = b.U - a.U, ew = b.W - a.W;
                // 联立 a + t·e = s·d，解出 s（t∈[0,1] 且 s>0 才算打到边上）
                double den = -eu * dw + du * ew;
                if (Math.Abs(den) < 1e-12) continue;
                double t = (a.U * dw - du * a.W) / den;
                double s = (-eu * a.W + a.U * ew) / den;
                if (t < -1e-9 || t > 1.0 + 1e-9 || s <= 1e-9) continue;
                if (s < best) best = s;
            }
            return best == double.MaxValue ? 0.0 : best;
        }

        private static void AddUnique(List<SectionPoint> into, double u, double w)
        {
            for (int i = 0; i < into.Count; i++)
                if (Math.Abs(into[i].U - u) < 1e-6 && Math.Abs(into[i].W - w) < 1e-6) return;
            into.Add(new SectionPoint { U = u, W = w });
        }

        /// <summary>湿周长（米，内壁一圈）。超椭圆用等面积椭圆的 Ramanujan 近似，误差 &lt;1%，够算摩擦与质量。</summary>
        public double Perimeter
        {
            get
            {
                double w = Math.Max(Width, 1e-4) * 0.5;
                double h = Math.Max(Height, 1e-4) * 0.5;
                if (HasPolygon)
                {
                    // 多边形：直接把每段斜边加起来（乘回米制）。
                    double sum = 0.0;
                    for (int i = 0; i < Polygon.Count; i++)
                    {
                        SectionPoint p = Polygon[i];
                        SectionPoint q = Polygon[(i + 1) % Polygon.Count];
                        double du = (q.U - p.U) * w, dw = (q.W - p.W) * h;
                        sum += Math.Sqrt(du * du + dw * dw);
                    }
                    return sum;
                }
                double n = EffectiveExponent;
                if (n > 2.0001)
                {
                    // 同面积椭圆：a·b = w·h/√(1/π·4·factor) —— 先把超椭圆折成等面积椭圆再量周长
                    double k = Math.Sqrt(SuperellipseFactor(n) * 4.0 / Math.PI);
                    w *= k; h *= k;
                }
                double a = Math.Max(w, h), b = Math.Min(w, h);
                double t = (a - b) / (a + b);
                return Math.PI * (a + b) * (1.0 + 3.0 * t * t / (10.0 + Math.Sqrt(4.0 - 3.0 * t * t)));
            }
        }

        /// <summary>壁厚：没填按截面平均尺寸的 3%，再夹到 1 mm~5 cm。</summary>
        public double EffectiveWall
        {
            get
            {
                if (Wall > 0.0) return Math.Min(Wall, 0.20);
                double mean = 0.5 * (Width + Height);
                return Math.Min(Math.Max(mean * 0.03, 0.001), 0.05);
            }
        }

        /// <summary>
        /// 超椭圆面积系数：area = 4ab·Γ(1+1/n)²/Γ(1+2/n) ÷ (4ab) —— 这里直接给 Γ 那一项。
        /// n=2 时 = π/4。写成静态是为了让 <see cref="Area"/> 与测试能各自调用。
        /// </summary>
        internal static double SuperellipseFactor(double n)
        {
            double g1 = Gamma(1.0 + 1.0 / n);
            double g2 = Gamma(1.0 + 2.0 / n);
            return g1 * g1 / g2;
        }

        /// <summary>Lanczos 近似的 Γ(x)（x&gt;0），7 项、双精度误差 &lt;1e-12，够用。</summary>
        internal static double Gamma(double x)
        {
            if (x <= 0.0) return double.NaN;
            double[] c =
            {
                0.99999999999980993, 676.5203681218851, -1259.1392167224028,
                771.32342877765313, -176.61502916214059, 12.507343278686905,
                -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
            };
            if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * Gamma(1.0 - x));
            x -= 1.0;
            double a = c[0];
            double t = x + 7.5;
            for (int i = 1; i < 9; i++) a += c[i] / (x + i);
            return Math.Sqrt(2.0 * Math.PI) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * a;
        }
    }
}
