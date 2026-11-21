using System;
using System.Collections.Generic;

namespace JetForge.Core
{

    /// <summary>
    /// partial：从截面序列里量出来的几何——长度、捕获面积、喉道位置、收缩角、压缩级数。
    /// 它们全是派生量：改截面不需要更新任何缓存，读的时候现算。
    /// </summary>
    public sealed partial class IntakeProfile
    {

        public double Length
        {
            get
            {
                if (Sections == null || Sections.Count < 2) return 0.0;
                return Sections[Sections.Count - 1].X - Sections[0].X;
            }
        }

        /// <summary>捕获面积（第一个截面）。</summary>
        public double CaptureArea { get { return Sections != null && Sections.Count > 0 ? Sections[0].Area : 0.0; } }

        /// <summary>出口面积（最后一个截面）。</summary>
        public double ExitArea
        {
            get { return Sections != null && Sections.Count > 0 ? Sections[Sections.Count - 1].Area : 0.0; }
        }

        /// <summary>喉道截面下标（面积最小的那个）。</summary>
        public int ThroatIndex
        {
            get
            {
                if (Sections == null || Sections.Count == 0) return 0;
                int k = 0;
                double a = double.MaxValue;
                for (int i = 0; i < Sections.Count; i++)
                {
                    double s = Sections[i].Area;
                    if (s < a) { a = s; k = i; }
                }
                return k;
            }
        }

        public double ThroatArea
        {
            get
            {
                if (Sections == null || Sections.Count == 0) return 0.0;
                return Sections[ThroatIndex].Area;
            }
        }

        /// <summary>喉道/捕获。这个数直接就是老模型里的 <c>ThroatRatio</c>。</summary>
        public double ThroatRatio
        {
            get
            {
                double c = CaptureArea;
                return c > 1e-9 ? ThroatArea / c : 0.0;
            }
        }

        /// <summary>等面积圆直径（米）——给"口径"这种只认单个数字的地方用。</summary>
        public double EquivalentCaptureDiameter
        {
            get
            {
                double a = CaptureArea;
                return a > 0.0 ? 2.0 * Math.Sqrt(a / Math.PI) : 0.0;
            }
        }

        /// <summary>
        /// 压缩角：从捕获截面走到喉道，气道内壁转过多少度（折成"总转角"，与激波模型的口径一致）。
        ///
        /// 轴对称进气道这是外压缩锥的半锥角；扁口二维进气道这是上下唇的斜板角；
        /// 两者相加就是那道斜激波要吃掉的转角。没有中心体时只算内壁收缩。
        /// </summary>
        public double CompressionDeg
        {
            get
            {
                if (Sections == null || Sections.Count < 2) return 0.0;
                int k = ThroatIndex;
                if (k == 0) k = 1;
                IntakeSection a = Sections[0], b = Sections[k];
                double dx = b.X - a.X;
                if (dx <= 1e-6) return 0.0;
                // 用"半高"当特征半径：扁口看的是上下唇的收缩，不是等效圆
                double r0 = Math.Max(a.Height * 0.5, 1e-4);
                double r1 = Math.Max(b.Height * 0.5, 1e-4);
                double cowl = Math.Atan2(Math.Max(r0 - r1, 0.0), dx) * 180.0 / Math.PI;
                double spin = HasSpinner ? Math.Min(Math.Abs(SpinnerHalfAngleDeg), 30.0) : 0.0;
                return Math.Min(cowl + spin, 45.0);
            }
        }

        /// <summary>
        /// 压缩级数：内壁转角在某段上明显变陡的地方算一级。
        /// 为什么要分级：两级 6.5° 比一级 13° 省总压（激波弱、熵增小），
        /// 把"内壁有几个折点"直接变成"有几道斜激波"是最省事的物理映射。
        /// </summary>
        public int CompressionStages
        {
            get
            {
                if (Sections == null || Sections.Count < 3) return 1;
                int k = ThroatIndex;
                if (k < 2) return 1;
                int stages = 1;
                double prev = SegmentAngle(0, 1);
                for (int i = 1; i < k; i++)
                {
                    double cur = SegmentAngle(i, i + 1);
                    if (cur > prev * 1.35 + 0.75) stages++;   // 变陡 35%（且至少多半度）算一个折点
                    prev = cur;
                }
                return Math.Min(stages, 3);
            }
        }

        /// <summary>第 [i, i+1] 段内壁的收缩角（度，正值表示往里收）。</summary>
        public double SegmentAngle(int i, int j)
        {
            if (Sections == null || j >= Sections.Count || i < 0) return 0.0;
            IntakeSection a = Sections[i], b = Sections[j];
            double dx = b.X - a.X;
            if (dx <= 1e-6) return 0.0;
            double r0 = Math.Max(a.Height * 0.5, 1e-4);
            double r1 = Math.Max(b.Height * 0.5, 1e-4);
            return Math.Atan2(Math.Max(r0 - r1, 0.0), dx) * 180.0 / Math.PI;
        }

        /// <summary>管道弯曲角（度）：各截面中心连线转过的角度之和。</summary>
        public double BendDeg
        {
            get
            {
                if (Sections == null || Sections.Count < 3) return 0.0;
                double total = 0.0;
                for (int i = 1; i < Sections.Count - 1; i++)
                {
                    double ax = Sections[i].X - Sections[i - 1].X;
                    double ay = Sections[i].OffsetY - Sections[i - 1].OffsetY;
                    double az = Sections[i].OffsetZ - Sections[i - 1].OffsetZ;
                    double bx = Sections[i + 1].X - Sections[i].X;
                    double by = Sections[i + 1].OffsetY - Sections[i].OffsetY;
                    double bz = Sections[i + 1].OffsetZ - Sections[i].OffsetZ;
                    double la = Math.Sqrt(ax * ax + ay * ay + az * az);
                    double lb = Math.Sqrt(bx * bx + by * by + bz * bz);
                    if (la < 1e-6 || lb < 1e-6) continue;
                    double cos = (ax * bx + ay * by + az * bz) / (la * lb);
                    cos = Math.Min(Math.Max(cos, -1.0), 1.0);
                    total += Math.Acos(cos) * 180.0 / Math.PI;
                }
                return Math.Min(total, 90.0);
            }
        }

        /// <summary>湿面积（平方米）：各段按截头棱台侧面积算，再加中心体表面。质量与摩擦要用。</summary>
        public double WettedArea
        {
            get
            {
                double A = 0.0;
                if (Sections != null)
                {
                    for (int i = 1; i < Sections.Count; i++)
                    {
                        IntakeSection a = Sections[i - 1], b = Sections[i];
                        double dx = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.OffsetY - a.OffsetY, 2) +
                                              Math.Pow(b.OffsetZ - a.OffsetZ, 2));
                        A += 0.5 * (a.Perimeter + b.Perimeter) * dx;
                    }
                }
                if (HasSpinner)
                {
                    double r = SpinnerRadius * 0.5 * (Sections.Count > 0 ? Sections[0].Height : 0.6);
                    double l = Math.Sqrt(SpinnerLength * SpinnerLength + r * r);
                    A += Math.PI * r * l;      // 圆锥侧面（底面开口）
                }
                return A;
            }
        }

        /// <summary>中心体最大半径（米）。</summary>
        public double SpinnerRadius
        {
            get
            {
                if (!HasSpinner || Sections == null || Sections.Count == 0) return 0.0;
                return EffectiveSpinnerRatio * Sections[0].Height * 0.5;
            }
        }

        /// <summary>在 x 处插值出截面（给网格用）。超出范围夹到端点。</summary>
        public IntakeSection Sample(double x)
        {
            if (Sections == null || Sections.Count == 0) return new IntakeSection(0, 0.6, 0.6);
            if (Sections.Count == 1 || x <= Sections[0].X) return Sections[0].Clone();
            int last = Sections.Count - 1;
            if (x >= Sections[last].X) return Sections[last].Clone();
            for (int i = 1; i <= last; i++)
            {
                if (x <= Sections[i].X)
                {
                    IntakeSection a = Sections[i - 1], b = Sections[i];
                    double dx = b.X - a.X;
                    double t = dx > 1e-9 ? (x - a.X) / dx : 0.0;
                    return new IntakeSection
                    {
                        X = x,
                        Width = a.Width + (b.Width - a.Width) * t,
                        Height = a.Height + (b.Height - a.Height) * t,
                        OffsetY = a.OffsetY + (b.OffsetY - a.OffsetY) * t,
                        OffsetZ = a.OffsetZ + (b.OffsetZ - a.OffsetZ) * t,
                        Exponent = a.Exponent + (b.Exponent - a.Exponent) * t,
                        Shape = t < 0.5 ? a.Shape : b.Shape,
                        Wall = a.Wall + (b.Wall - a.Wall) * t,
                        // 倾角跟着段与段之间线性过渡：两段都歪 30° 时中间那些环也得歪 30°，
                        // 否则放样会在中间"拧"一下（玩家看到的是麻花状的机匣）。
                        TiltPitchDeg = a.TiltPitchDeg + (b.TiltPitchDeg - a.TiltPitchDeg) * t,
                        TiltYawDeg = a.TiltYawDeg + (b.TiltYawDeg - a.TiltYawDeg) * t,
                    };
                }
            }
            return Sections[last].Clone();
        }

        /// <summary>
        /// 中心体在 x 处的半径（米）。x 从 <see cref="SpinnerTipX"/> 起算，超过 <see cref="SpinnerLength"/> 就没了。
        /// 形状是"先锥后柱"：锥段按半锥角长半径，长到最大就变直。
        /// </summary>
        public double SpinnerRadiusAt(double x)
        {
            if (!HasSpinner) return 0.0;
            double t = x - SpinnerTipX;
            if (t <= 0.0 || t > SpinnerLength) return 0.0;
            double rMax = SpinnerRadius;
            double tip = Math.Tan(SpinnerHalfAngleDeg * Math.PI / 180.0) * t;
            // 锥长不够长到最大半径时就是一段锥；够长之后是柱
            return Math.Min(tip, rMax);
        }
    }
}
