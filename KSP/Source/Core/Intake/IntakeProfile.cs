using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 进气道的截面形状。
    ///
    /// 以前一件进气道只有"型式"（皮托/轴对称/斜板/锥体）+ 三个比例数字，全是圆截面的变体——
    /// 想画一个"下唇包着机身、上唇是斜板、中段被压成扁椭圆"的进气口，模型里根本没有词。
    /// 这里改成：进气道是一条沿轴排开的截面序列，每个截面自己带形状、尺寸、偏移。
    /// 各截面之间双线性插值出事曲面，所以扁的、方的、偏到一边去的、拐弯的 S 形都能画出来。
    /// </summary>
    public enum IntakeSectionShape
    {
        /// <summary>正圆（宽=高时就是圆）。</summary>
        Circle = 0,

        /// <summary>椭圆（宽高不等）。</summary>
        Ellipse = 1,

        /// <summary>超椭圆：|x/a|^n+|y/b|^n=1。n=4 是"方中带圆"，n 越大越方。</summary>
        Superellipse = 2,
    }

    /// <summary>截面预设轮廓（截面编辑页的一排按钮）。</summary>
    public enum SectionPreset
    {
        Circle = 0,
        WideEllipse = 1,
        TallEllipse = 2,
        RoundedRect = 3,
        Rect = 4,
        Diamond = 5,
        Trapezoid = 6,
        Lens = 7,
        Barrel = 8,
    }

    /// <summary>
    /// 自由多边形的一个顶点，归一化坐标：U 横向、W 垂向，各自 ∈ [-1,1]，
    /// 乘半宽/半高才是米。归一化的好处是改宽高时形状不变、只是整体缩放。
    /// </summary>
    public sealed class SectionPoint
    {
        public double U;
        public double W;

        public SectionPoint() { }

        public SectionPoint(double u, double w) { U = u; W = w; }

        public SectionPoint Clone() { return new SectionPoint(U, W); }
    }

    /// <summary>
    /// 进气道的一个截面（气道的**内壁**，不含外罩：外罩按壁厚往外长）。
    ///
    /// 坐标：X 沿轴向后（0 = 进气口平面），Y 向上，Z 向右（和 KSP 一致，Y 是上下）。
    /// 六自由度都在这：X（轴向位置）、Y/Z（横向偏移，摆出 S 形或侧置进气口）、
    /// 宽/高（截面尺寸，各自独立 → 扁口）、圆角指数（方的还是圆的）。
    /// </summary>
    public sealed partial class IntakeSection
    {
        /// <summary>轴向位置（米，从进气口平面的中心算）。必须严格递增。</summary>
        public double X = 0.0;

        /// <summary>截面全宽（米，Z 方向）。</summary>
        public double Width = 0.6;

        /// <summary>截面全高（米，Y 方向）。</summary>
        public double Height = 0.6;

        /// <summary>垂直偏移（米，向上为正）。</summary>
        public double OffsetY = 0.0;

        /// <summary>横向偏移（米，向右为正）。</summary>
        public double OffsetZ = 0.0;

        /// <summary>圆角指数（2 = 椭圆，4 = 方中带圆，8 = 接近矩形）。</summary>
        public double Exponent = 2.0;

        /// <summary>形状选择：Circle/Ellipse 按宽高画椭圆，Superellipse 用 <see cref="Exponent"/>。</summary>
        public IntakeSectionShape Shape = IntakeSectionShape.Circle;

        /// <summary>壁厚（米）。外罩 = 内壁往法向长出去一个壁厚；不填按截面尺寸的 3%。</summary>
        public double Wall = 0.0;

        /// <summary>
        /// 截面倾角：侧视里这一圈截面平面绕横向轴歪过来多少度（F-15 那种前掠唇口就靠它）。
        /// 正 = 上缘往 +X（往后）挪、下缘往前挪。0 = 截面正对气流，每段都一样粗细。
        ///
        /// **只改外形，不改流量**：进气量按截面在轴向的投影面积算（`Intake.ThroatAreaOf` 那一套），
        /// 把截面斜过来并不改变它挡住气流的那块投影，所以推力、耗油一个数都不动。
        /// 真正会变的只有蒙皮与内部流道画出来的样子——这正是玩家想要的"斜的进气道"。
        /// </summary>
        public double TiltPitchDeg = 0.0;

        /// <summary>同上，俯视里绕竖直轴歪（正 = 右缘往后挪）。两轴可以同时歪，截面就是一个斜的平面。</summary>
        public double TiltYawDeg = 0.0;

        /// <summary>倾角的硬上限（度）。再歪下去相邻两段会自身相交，网格翻面——不是审美问题，是拓扑问题。</summary>
        public const double MaxTiltDeg = 45.0;

        /// <summary>
        /// 自由多边形轮廓（归一化坐标：U/W ∈ [-1,1]，各自乘半宽/半高才是米）。
        ///
        /// 归一化是为了"改宽高而不改形状"：玩家在 2D 页里拖的是形状，宽高那两个数
        /// 仍然可以整体缩放它。为空表示用参数化圆/椭圆/超椭圆。
        /// </summary>
        public List<SectionPoint> Polygon = null;

        public bool HasPolygon
        {
            get { return Polygon != null && Polygon.Count >= 3; }
        }

        public IntakeSection() { }

        /// <summary>
        /// 真实外形宽高（米）。
        ///
        /// **和 Width/Height 不是一回事**：那两个数是归一化框，多边形只用到框的一部分。
        /// 鼓形 `r=(1+0.12cos2θ)/1.12` 的 W 最远只到 0.786，双凸透镜只到 0.55——
        /// 于是"宽 0.600 × 高 0.600 m"报出来的其实是那个框，量出来却是 0.471 m 高。
        /// 玩家看到"我的圆怎么面积对不上"就是这么来的：读数口径与实际外形脱了节。
        /// </summary>
        public void RealExtent(out double widthM, out double heightM)
        {
            if (!HasPolygon) { widthM = Width; heightM = Height; return; }
            double uMin = 1.0, uMax = -1.0, wMin = 1.0, wMax = -1.0;
            for (int i = 0; i < Polygon.Count; i++)
            {
                SectionPoint p = Polygon[i];
                if (p.U < uMin) uMin = p.U;
                if (p.U > uMax) uMax = p.U;
                if (p.W < wMin) wMin = p.W;
                if (p.W > wMax) wMax = p.W;
            }
            widthM = 0.5 * Width * (uMax - uMin);
            heightM = 0.5 * Height * (wMax - wMin);
        }

        public IntakeSection(double x, double width, double height)
        {
            X = x; Width = width; Height = height;
        }

        public IntakeSection Clone()
        {
            return new IntakeSection
            {
                X = X, Width = Width, Height = Height,
                OffsetY = OffsetY, OffsetZ = OffsetZ,
                Exponent = Exponent, Shape = Shape, Wall = Wall,
                TiltPitchDeg = TiltPitchDeg, TiltYawDeg = TiltYawDeg,
                Polygon = ClonePolygon(Polygon),
            };
        }

        /// <summary>把倾角夹进 ±<see cref="MaxTiltDeg"/>；NaN 当 0（NaN 一旦进网格，整圈顶点都成了 NaN）。</summary>
        public static double ClampTilt(double deg)
        {
            if (double.IsNaN(deg)) return 0.0;
            if (deg > MaxTiltDeg) return MaxTiltDeg;
            if (deg < -MaxTiltDeg) return -MaxTiltDeg;
            return deg;
        }

        /// <summary>
        /// 截面上局部坐标 (u, w) 处的点，因为倾角要沿轴挪多远（米）。
        ///
        /// 截面被歪成一个平面：局部横向 u、局部竖向 w 的点，轴向位置 = X + 这个数。
        /// 写成 Core 里的一个函数（而不是直接塞进网格构造函数）是为了**这个数能被回归钉住**：
        /// Mesh 在 KSP 程序集里，测试工程编不进去，只能钉 Core。
        /// </summary>
        public double AxialShift(double u, double w)
        {
            const double D2R = Math.PI / 180.0;
            return Math.Tan(ClampTilt(TiltYawDeg) * D2R) * u
                 + Math.Tan(ClampTilt(TiltPitchDeg) * D2R) * w;
        }

        /// <summary>
        /// 多边形一圈绕向的符号面积（归一化单位）。正 = 逆时针、负 = 顺时针。
        ///
        /// **为什么必须管这个**：放样网格的做法是"轴向差 × 周向差"求法线，再按 `rev` 决定三角绕序——
        /// 两者都默认顶点是逆时针排的。菱形/梯形/双凸透镜这些手写的预设偏偏是顺时针，
        /// 于是同一个截面在图上看着对、在 3D 里法线朝内、面朝里（外表面被背面剔除，看着像破洞）；
        /// 镜像（X/Y 取反）也会把绕向翻过来，所以"镜像之后形状就坏了"。
        /// 这里把绕向当成不变量：所有改顶点的入口最后都调一次，顺手把顺时针掰回逆时针。
        /// </summary>
        public double SignedArea
        {
            get
            {
                if (!HasPolygon) return 0.0;
                double sum = 0.0;
                for (int i = 0; i < Polygon.Count; i++)
                {
                    SectionPoint a = Polygon[i];
                    SectionPoint b = Polygon[(i + 1) % Polygon.Count];
                    sum += a.U * b.W - b.U * a.W;
                }
                return sum * 0.5;
            }
        }

        /// <summary>把绕向统一成逆时针（右上角看，顶点按逆时针排）。已经是逆时针就不动，返回 false。</summary>
        public bool EnsureCounterClockwise()
        {
            if (!HasPolygon || Polygon.Count < 3) return false;
            if (SignedArea >= 0.0) return false;
            Polygon.Reverse();
            return true;
        }

        /// <summary>
        /// 把多边形的**起点**转到"最右侧（U 最大），同样靠右时取更靠上"的那个顶点。
        ///
        /// **为什么非有不可**：放样采样是按"从第 0 个顶点起走过的周长比例"取的
        /// （`PolygonPoint`），参数化截面（圆/椭圆）却是从 θ=0（正右）开始。
        /// 两个截面的第 0 个点如果不在同一方位，相邻两环就是**错开一段**连起来的——
        /// 壳面上出现一道螺旋状的拧、中心体和内壁更明显；镜像会把顶点顺序倒过来，
        /// 于是"镜像一下反而正常了"。这正是玩家报的"预设最后四个 + X/Y 镜像时扭曲"。
        ///
        /// 把所有入口的起点都规整到同一处，环与环才对得上位。
        /// </summary>
        public void CanonicalStart()
        {
            if (!HasPolygon || Polygon.Count < 3) return;
            int best = 0;
            for (int i = 1; i < Polygon.Count; i++)
            {
                SectionPoint p = Polygon[i], b = Polygon[best];
                if (p.U > b.U + 1e-9 || (Math.Abs(p.U - b.U) <= 1e-9 && p.W > b.W)) best = i;
            }
            if (best == 0) return;
            List<SectionPoint> rot = new List<SectionPoint>(Polygon.Count);
            for (int i = 0; i < Polygon.Count; i++) rot.Add(Polygon[(best + i) % Polygon.Count]);
            Polygon = rot;
        }

        public static List<SectionPoint> ClonePolygon(List<SectionPoint> src)
        {
            if (src == null) return null;
            List<SectionPoint> dst = new List<SectionPoint>(src.Count);
            for (int i = 0; i < src.Count; i++)
                dst.Add(new SectionPoint { U = src[i].U, W = src[i].W });
            return dst;
        }

        /// <summary>把当前的参数化轮廓固化成多边形（32 点，逆时针）。</summary>
        public List<SectionPoint> ToPolygon(int points = 32)
        {
            List<SectionPoint> p = new List<SectionPoint>(Math.Max(8, points));
            double hw = Math.Max(Width, 1e-3) * 0.5;
            double hh = Math.Max(Height, 1e-3) * 0.5;
            for (int i = 0; i < points; i++)
            {
                double th = 2.0 * Math.PI * i / points;
                double u, w;
                OutlinePoint(th, out u, out w);
                p.Add(new SectionPoint { U = u / hw, W = w / hh });
            }
            return p;
        }

        /// <summary>实际用的圆角指数：Circle/Ellipse 一律 2。</summary>
        public double EffectiveExponent
        {
            get
            {
                if (Shape != IntakeSectionShape.Superellipse) return 2.0;
                return Math.Min(Math.Max(Exponent, 2.0), 16.0);
            }
        }

        /// <summary>这块截面的流通面积（平方米）。超椭圆有闭式解，不用数值积分。</summary>
        public double Area
        {
            get
            {
                double w = Math.Max(Width, 1e-4) * 0.5;
                double h = Math.Max(Height, 1e-4) * 0.5;
                if (HasPolygon)
                {
                    // 鞋带公式。多边形是归一化的，最后乘 hw·hh 换成平方米。
                    double sum = 0.0;
                    for (int i = 0; i < Polygon.Count; i++)
                    {
                        SectionPoint a = Polygon[i];
                        SectionPoint b = Polygon[(i + 1) % Polygon.Count];
                        sum += a.U * b.W - b.U * a.W;
                    }
                    return Math.Abs(sum) * 0.5 * w * h;
                }
                double n = EffectiveExponent;
                if (n <= 2.0001) return Math.PI * w * h;          // 椭圆
                return 4.0 * w * h * SuperellipseFactor(n);
            }
        }

        /// <summary>
        /// 截面上角度 θ 处的轮廓点（相对截面中心的横向/垂向偏移）。网格与面板都走这一个入口，
        /// 所以"图上那圈线"和"算面积用的那个形状"必然是同一个形状——两处各写一遍参数化，
        /// 迟早会出现图上是超椭圆、面积按椭圆算的错，而且从读数上看不出来。
        ///
        /// 超椭圆参数化：|cosθ|^(2/n)、|sinθ|^(2/n)。n=2 时指数正好是 1，退化成椭圆。
        /// </summary>
        public void OutlinePoint(double theta, out double u, out double w)
        {
            double a = Math.Max(Width, 1e-3) * 0.5;
            double b = Math.Max(Height, 1e-3) * 0.5;
            if (HasPolygon)
            {
                // 多边形按"周长比例"取点：θ 在这里是参数不是极角，采样沿轮廓均匀，
                // 放样网格不会在尖角处挤成一团。
                double t = theta / (2.0 * Math.PI);
                t -= Math.Floor(t);
                PolygonPoint(t, out u, out w);
                u *= a;
                w *= b;
                return;
            }
            double e = 2.0 / EffectiveExponent;
            double ct = Math.Cos(theta), st = Math.Sin(theta);
            u = Math.Sign(ct) * Math.Pow(Math.Abs(ct), e) * a;
            w = Math.Sign(st) * Math.Pow(Math.Abs(st), e) * b;
        }

        /// <summary>多边形轮廓一圈的长度（归一化单位，未乘半宽半高）。</summary>
        public double PolygonPerimeter()
        {
            double sum = 0.0;
            for (int i = 0; i < Polygon.Count; i++)
            {
                SectionPoint p = Polygon[i];
                SectionPoint q = Polygon[(i + 1) % Polygon.Count];
                double du = q.U - p.U, dw = q.W - p.W;
                sum += Math.Sqrt(du * du + dw * dw);
            }
            return Math.Max(sum, 1e-6);
        }

        /// <summary>多边形上按归一化弧长 s（0~1）取点，输出归一化坐标。</summary>
        public void PolygonPoint(double s, out double u, out double w)
        {
            double total = PolygonPerimeter();
            s -= Math.Floor(s);
            double want = s * total;
            double acc = 0.0;
            for (int i = 0; i < Polygon.Count; i++)
            {
                SectionPoint p = Polygon[i];
                SectionPoint q = Polygon[(i + 1) % Polygon.Count];
                double du = q.U - p.U, dw = q.W - p.W;
                double len = Math.Sqrt(du * du + dw * dw);
                if (len < 1e-12) continue;
                if (acc + len >= want || i == Polygon.Count - 1)
                {
                    double f = Math.Min(Math.Max((want - acc) / len, 0.0), 1.0);
                    u = p.U + du * f;
                    w = p.W + dw * f;
                    return;
                }
                acc += len;
            }
            u = Polygon[0].U;
            w = Polygon[0].W;
        }

        /// <summary>在第 index 条边的中点插一个顶点（面板上的"+ 顶点"）。</summary>
        public void InsertPolygonPoint(int index)
        {
            if (!HasPolygon) return;
            if (index < 0 || index >= Polygon.Count) index = Polygon.Count - 1;
            SectionPoint p = Polygon[index];
            SectionPoint q = Polygon[(index + 1) % Polygon.Count];
            Polygon.Insert(index + 1, new SectionPoint { U = 0.5 * (p.U + q.U), W = 0.5 * (p.W + q.W) });
            BakeMirrorBase();
        }

        /// <summary>删掉第 index 个顶点；少于 3 个就拒绝（三角形是最少的闭合形状）。</summary>
        public bool RemovePolygonPoint(int index)
        {
            if (!HasPolygon || Polygon.Count <= 3) return false;
            if (index < 0 || index >= Polygon.Count) return false;
            Polygon.RemoveAt(index);
            BakeMirrorBase();
            return true;
        }
    }

    public sealed partial class IntakeProfile
    {
        public const int MinSections = 2;
        public const int MaxSections = 24;

        public List<IntakeSection> Sections = new List<IntakeSection>();

        /// <summary>中心体从前端面往后延伸的长度（米）。0 = 没有中心体（皮托/轴对称壳）。</summary>
        public double SpinnerLength = 0.0;

        /// <summary>中心体最大半径 / 进气口半高。1 = 把口堵满。</summary>
        public double SpinnerRadiusRatio = 0.0;

        /// <summary>中心体半锥角（度）。实际量出来的压角用它的一半（锥面转半角、侧板再转半角）。</summary>
        public double SpinnerHalfAngleDeg = 8.0;

        /// <summary>中心体顶点到口的轴向距离（米）：0 = 顶点就在口平面上。</summary>
        public double SpinnerTipX = 0.0;

        /// <summary>一件缺省的进气道：直筒 + 一点收缩，形状跟老"轴对称"差不多。</summary>
        public static IntakeProfile Default(double length, double captureDiameter)
        {
            double d = captureDiameter > 0.0 ? captureDiameter : 0.6;
            double L = length > 0.0 ? length : 1.0;
            IntakeProfile p = new IntakeProfile();
            // 四段而不是两段：唇口 → 喉道 → 扩压 → 发动机面。
            // 两段放样只有一个锥面，收扩的分界落在半路上、看上去就是"一根直筒外加一道折"；
            // 玩家自己加到四段才能得到的样子，没有理由让他每次手点两下。
            // 喉道放在 32% 处、收到 0.88，后面缓慢扩到发动机面——这是亚声速皮托扩压器的常见比例。
            p.Sections.Add(new IntakeSection(0.0, d, d));
            p.Sections.Add(new IntakeSection(0.32 * L, 0.88 * d, 0.88 * d));
            p.Sections.Add(new IntakeSection(0.66 * L, 0.96 * d, 0.96 * d));
            p.Sections.Add(new IntakeSection(L, d, d));
            return p;
        }

        // 只要给了长度就算有中心体：半径比没填时按 0.60 兜底。
        // 反过来（长度 0 而半径比非 0）不算——那是一条长度为 0 的锥，等于没有。
        // 面板上玩家拖的是「中心体长度」，拖出来的东西必须看得见，
        // 否则会出现"数值动了、模型没动"这种最难查的抱怨。
        public bool HasSpinner { get { return SpinnerLength > 1e-4; } }

        /// <summary>中心体半径比（半径 / 首段半高），没填按 0.60。</summary>
        public double EffectiveSpinnerRatio
        {
            get { return SpinnerRadiusRatio > 1e-3 ? Math.Min(SpinnerRadiusRatio, 0.95) : 0.60; }
        }

        public IntakeProfile Clone()
        {
            IntakeProfile p = new IntakeProfile
            {
                SpinnerLength = SpinnerLength,
                SpinnerRadiusRatio = SpinnerRadiusRatio,
                SpinnerHalfAngleDeg = SpinnerHalfAngleDeg,
                SpinnerTipX = SpinnerTipX,
            };
            for (int i = 0; i < Sections.Count; i++) p.Sections.Add(Sections[i].Clone());
            return p;
        }

        /// <summary>合法性检查。返回 false 时 <paramref name="why"/> 里是给人看的中文原因。</summary>
        public bool Validate(out string why)
        {
            if (Sections == null || Sections.Count < MinSections)
            {
                why = string.Format("型线至少要 {0} 个截面（现在 {1} 个），一个截面的气道没有长度", MinSections,
                    Sections == null ? 0 : Sections.Count);
                return false;
            }
            if (Sections.Count > MaxSections)
            {
                why = string.Format("截面最多 {0} 个（现在 {1} 个）", MaxSections, Sections.Count);
                return false;
            }
            for (int i = 0; i < Sections.Count; i++)
            {
                IntakeSection s = Sections[i];
                if (s == null) { why = string.Format("第 {0} 个截面是空的", i); return false; }
                if (!Finite(s.X) || !Finite(s.Width) || !Finite(s.Height) ||
                    !Finite(s.OffsetY) || !Finite(s.OffsetZ))
                {
                    why = string.Format("第 {0} 个截面有非有限数（NaN/Inf）", i);
                    return false;
                }
                if (s.Width <= 0.0 || s.Height <= 0.0)
                {
                    why = string.Format("第 {0} 个截面尺寸不是正数（{1:F2}×{2:F2} m），截面面积为 0 → 气道断开了", i, s.Width, s.Height);
                    return false;
                }
                if (s.Polygon != null)
                {
                    if (s.Polygon.Count < 3)
                    {
                        why = string.Format("第 {0} 个截面的多边形只有 {1} 个顶点，闭合不了", i, s.Polygon.Count);
                        return false;
                    }
                    for (int k = 0; k < s.Polygon.Count; k++)
                    {
                        SectionPoint p = s.Polygon[k];
                        if (p == null || !Finite(p.U) || !Finite(p.W))
                        {
                            why = string.Format("第 {0} 个截面的第 {1} 个顶点有非有限数", i, k);
                            return false;
                        }
                    }
                }
                if (i > 0 && s.X <= Sections[i - 1].X + 1e-6)
                {
                    why = string.Format("第 {0} 个截面的轴向位置 {1:F3} m 没有比前一个（{2:F3} m）更靠后：截面必须按顺序排",
                        i, s.X, Sections[i - 1].X);
                    return false;
                }
            }
            if (!Finite(SpinnerLength) || SpinnerLength < 0.0)
            {
                why = "中心体长度不是非负数";
                return false;
            }
            why = "";
            return true;
        }

        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
    }
}
