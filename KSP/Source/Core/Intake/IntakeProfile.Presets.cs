using System;
using System.Collections.Generic;

namespace JetForge.Core
{

    /// <summary>
    /// partial：截面形状的预设轮廓（皮托、斜板、锥体、椭圆…）。
    /// 这些预设只是往截面里灌一组初始点，玩家拖完之后就和它没关系了。
    /// </summary>
    public sealed partial class IntakeSection
    {

        /// <summary>
        /// 换成某个常用截面。前五个只改参数化（形状+指数+长宽比），后四个直接给多边形。
        /// 注意"宽椭圆/竖椭圆"会动 Height——不然它跟"椭圆"是同一个东西，预设就没意义了。
        /// </summary>
        public void ApplyPreset(SectionPreset preset)
        {
            double w = Math.Max(Width, 0.02);
            switch (preset)
            {
                case SectionPreset.Circle:
                    Shape = IntakeSectionShape.Circle; Exponent = 2.0; Polygon = null;
                    Height = w;
                    break;
                case SectionPreset.WideEllipse:
                    Shape = IntakeSectionShape.Ellipse; Exponent = 2.0; Polygon = null;
                    Height = Math.Max(0.62 * w, 0.02);
                    break;
                case SectionPreset.TallEllipse:
                    Shape = IntakeSectionShape.Ellipse; Exponent = 2.0; Polygon = null;
                    Height = 1.60 * w;
                    break;
                case SectionPreset.RoundedRect:
                    Shape = IntakeSectionShape.Superellipse; Exponent = 6.0; Polygon = null;
                    break;
                case SectionPreset.Rect:
                    Shape = IntakeSectionShape.Superellipse; Exponent = 12.0; Polygon = null;
                    break;
                case SectionPreset.Diamond:
                    Polygon = new List<SectionPoint>
                    {
                        new SectionPoint { U = 0.0, W = 1.0 },
                        new SectionPoint { U = 1.0, W = 0.0 },
                        new SectionPoint { U = 0.0, W = -1.0 },
                        new SectionPoint { U = -1.0, W = 0.0 },
                    };
                    break;
                case SectionPreset.Trapezoid:
                    // 上窄下宽：机腹进气道那种"越往里越宽"的截面
                    Polygon = new List<SectionPoint>
                    {
                        new SectionPoint { U = -0.60, W = 1.0 },
                        new SectionPoint { U = 0.60, W = 1.0 },
                        new SectionPoint { U = 1.00, W = -1.0 },
                        new SectionPoint { U = -1.00, W = -1.0 },
                    };
                    break;
                case SectionPreset.Lens:
                    Polygon = LensPoints(14);
                    break;
                case SectionPreset.Barrel:
                    Polygon = BarrelPoints(24);
                    break;
            }
            // 预设里那几个手写的多边形按"左上→右上→右下"顺着写的，是顺时针；
            // 统一掰成逆时针，3D 法线才朝外（见 SignedArea 的注释）。
            EnsureCounterClockwise();
            // 起点也要规整：放样按"从第 0 个顶点走的周长比例"取样，起点不在正右，
            // 相邻两环就错开一段连起来，壳面上是一道螺旋状的拧（见 CanonicalStart 的注释）。
            CanonicalStart();
            // 换预设等于换了个形状，镜像基准跟着换，不然勾着的镜像会把它翻回去。
            BakeMirrorBase();
        }

        /// <summary>双凸透镜：上下各一段抛物线，两端收成尖角。</summary>
        private static List<SectionPoint> LensPoints(int n)
        {
            if (n < 3) n = 3;
            List<SectionPoint> pts = new List<SectionPoint>(n * 2);
            for (int i = 0; i < n; i++)
            {
                double t = -1.0 + 2.0 * i / (n - 1);
                pts.Add(new SectionPoint { U = t, W = 0.55 * (1.0 - t * t) });
            }
            // 回程跳过两个端点：t=±1 处上下两段重合，加上去就是零长度边
            // （顶点数比预期多两个、法线在尖角处退化成 0，模型上看着是一条亮线）。
            for (int i = n - 2; i >= 1; i--)
            {
                double t = -1.0 + 2.0 * i / (n - 1);
                pts.Add(new SectionPoint { U = t, W = -0.55 * (1.0 - t * t) });
            }
            return pts;
        }

        /// <summary>鼓形：两侧外凸到 ±1，上下收窄到约 ±0.79（径向函数 r = 1 + 0.12·cos2θ 再归一）。</summary>
        private static List<SectionPoint> BarrelPoints(int n)
        {
            if (n < 6) n = 6;
            List<SectionPoint> pts = new List<SectionPoint>(n);
            const double bulge = 0.12;
            for (int i = 0; i < n; i++)
            {
                double th = 2.0 * Math.PI * i / n;
                double r = (1.0 + bulge * Math.Cos(2.0 * th)) / (1.0 + bulge);
                pts.Add(new SectionPoint { U = r * Math.Cos(th), W = r * Math.Sin(th) });
            }
            return pts;
        }
    }
}
