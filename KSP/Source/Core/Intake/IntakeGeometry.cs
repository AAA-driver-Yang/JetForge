using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>进气道型线上的一个点（发动机坐标系：y 向前为正，r 是半径）。</summary>
    public struct IntakePoint
    {
        public double Y;
        public double R;
        public string Label;

        public IntakePoint(double y, double r, string label)
        {
            Y = y;
            R = r;
            Label = label;
        }
    }

    /// <summary>
    /// 进气道的几何（和性能分开的第二种"真相"）。
    ///
    /// 为什么必须单独一份：性能要的是"喉道面积 / 捕获面积 / 面马赫"，
    /// 外形要的是"唇口在哪儿、收多少、中心锥多长"。以前这两件事都从
    /// `InletDiameter` 一个数推出来，所以玩家把喉道比调到 0.7，性能掉了、
    /// 模型却一点没变——图上和账上说的不是同一台发动机。
    ///
    /// 这份几何是**唯一来源**：网格（`JetForgeMeshBuilder`）和剖视（`Cutaway`）
    /// 都只读它，不许各自再从直径推一遍。
    /// </summary>
    public sealed class IntakeShape
    {
        public IntakeKind Kind = IntakeKind.Axial;

        /// <summary>唇口（最前）轴向位置。</summary>
        public double YFront;
        /// <summary>喉道轴向位置（唇口往后）。</summary>
        public double YThroat;
        /// <summary>压气机面（或核心机进口）轴向位置。</summary>
        public double YFace;

        /// <summary>唇口捕获半径。</summary>
        public double RCapture;
        /// <summary>画图时捕获半径被短舱夹住了（面积口径的账没变，只是外形不许比短舱粗）。</summary>
        public bool CaptureClamped;
        /// <summary>喉道半径。</summary>
        public double RThroat;
        /// <summary>发动机面半径。</summary>
        public double RFace;

        public double CaptureDiameter { get { return RCapture * 2.0; } }
        public double ThroatDiameter { get { return RThroat * 2.0; } }
        public double FaceDiameter { get { return RFace * 2.0; } }

        public double CaptureArea;
        public double ThroatArea;
        public double FaceArea;

        /// <summary>唇口倒圆半径（锐唇接近 0）。</summary>
        public double LipRadius;
        /// <summary>唇口到发动机面的轴向长度。</summary>
        public double Length;

        /// <summary>有没有中心锥 / 斜板（锥体、斜板才有）。</summary>
        public bool HasCenterBody;

        /// <summary>锥尖相对老位置的轴向偏移（占长度的比例，正 = 伸出唇口）。0 = 老外形。</summary>
        public double ConeTipShift;
        /// <summary>压缩角（度）：斜板/锥体的第一道斜面。</summary>
        public double RampAngleDeg;

        /// <summary>气道内壁型线：唇口内缘 → 喉道 → 发动机面。</summary>
        public readonly List<IntakePoint> Duct = new List<IntakePoint>();
        /// <summary>唇口外缘型线：往后再交给短舱。</summary>
        public readonly List<IntakePoint> Cowl = new List<IntakePoint>();
        /// <summary>中心锥 / 斜板型线（从尖端到喉道），没有就是空表。</summary>
        public readonly List<IntakePoint> CenterBody = new List<IntakePoint>();

        /// <summary>喉道相对发动机面收了多少（&gt;1 = 收缩，&lt;1 = 扩散）。</summary>
        public double ContractionRatio
        {
            get { return RFace > 1e-6 ? RThroat / RFace : 0.0; }
        }

        public string Describe()
        {
            // 锥尖位置只在玩家真挪过时才说一句：0 是老外形，屏幕上不该多出「锥尖 0% 长」这种废话。
            string tip = Math.Abs(ConeTipShift) > 1e-9
                ? string.Format(Loc.T("，锥尖 {0:F0}% 长", ", tip {0:F0}% of length"), ConeTipShift * 100.0)
                : "";
            return string.Format(
                "进气道 {0}：捕获 Ø{1:F2}（A{2:F3}）→ 喉道 Ø{3:F2}（A{4:F3}）→ 面 Ø{5:F2}（A{6:F3}）｜长 {7:F2} m｜唇口 R{8:F3}{9}{10}",
                IntakeSpec.KindName(Kind), CaptureDiameter, CaptureArea, ThroatDiameter, ThroatArea,
                FaceDiameter, FaceArea, Length, LipRadius,
                HasCenterBody ? "｜中心锥 " + RampAngleDeg.ToString("F1") + "°" + tip : "",
                HasCenterBody ? "（回转近似）" : "");
        }
    }

    /// <summary>
    /// 从设计参数生成进气道几何。
    ///
    /// 三个面积各管各的，不许互相顶替：
    ///   捕获面积（唇口）——决定"能吞多少气"，溢流就是它吞不进的那部分
    ///   喉道面积（最小截面）——决定"最多过多少气"，壅塞就是卡在这儿
    ///   发动机面面积——压气机自己开口多大，和进气道的活是两件事
    /// </summary>
    public static class IntakeGeometry
    {
        /// <summary>喉道在唇口到发动机面这段里的相对位置（0=唇口，1=面）。</summary>
        public const double ThroatFrac = 0.42;

        public static IntakeShape Of(EngineDesignData d)
        {
            IntakeShape s = new IntakeShape();
            if (d == null) return s;

            IntakeSpec sp = (d.Intake ?? new IntakeSpec()).Resolved();
            EngineStations st = EngineGeometry.Of(d);
            s.Kind = sp.Kind;

            double capD = Intake.CaptureDiameterOf(d);

            // 面积以 `Intake.ThroatAreaOf` / `CaptureAreaOf` 为准，半径由面积反推：
            // 这样"性能算的那个喉道"和"网格画的那个喉道"永远是一个数，
            // 不会出现账上按 0.85 喉道比、图里画成 1.0 的情况。
            s.ThroatArea = Intake.ThroatAreaOf(d);
            s.CaptureArea = Intake.CaptureAreaOf(d);
            s.RThroat = Math.Max(Math.Sqrt(s.ThroatArea / Math.PI), 0.02);
            // 面积口径的捕获半径偶尔会比短舱粗一点点（缺省喉道比 0.85 折算出来的结果），
            // 直接画就是唇口捅到短舱外面、像穿模。图里按"唇口外缘不许超过短舱"夹一下，
            // 面积口径的账一个字不动——性能仍然照 ThroatArea / CaptureArea 算。
            double rcArea = Math.Max(Math.Sqrt(s.CaptureArea / Math.PI), 0.02);
            double rcMax = Math.Max(st.RIn * 0.985, 0.02);
            bool autoCapture = d.Intake == null || d.Intake.CaptureDiameter <= 0.0;
            s.RCapture = autoCapture ? Math.Min(rcArea, rcMax) : rcArea;
            s.CaptureClamped = autoCapture && rcArea > rcMax;

            double faceD = d.EngineFaceDiameter();
            s.RFace = Math.Max(faceD * 0.5, 0.02);

            s.FaceArea = Math.PI * s.RFace * s.RFace;

            // 唇口到压气机面：站表里 YIn 是机头，YFan 是风扇面。
            s.YFront = st.YIn;
            s.Length = Math.Max(st.YIn - st.YFan, 0.10);
            s.YThroat = s.YFront - s.Length * ThroatFrac;
            s.YFace = s.YFront - s.Length;

            // 唇口倒圆：填 0 时按捕获直径 4% 自动。
            double lip = sp.ResolvedLipRadius(capD);
            // 超声速唇口必须薄：钝唇在 Ma1.5 以上就是一块挡板。
            if (sp.Kind != IntakeKind.Axial) lip = Math.Min(lip, capD * 0.015);
            s.LipRadius = Math.Max(lip, 0.002);

            BuildDuct(s, sp);
            BuildCowl(s, sp, st.RIn);
            BuildCenterBody(s, sp, d.DesignMach);
            return s;
        }

        /// <summary>
        /// 独立零件用的几何：没有整机，只有它自己的参数。
        ///
        /// 与 <see cref="Of(EngineDesignData)"/> 是同一套型线生成，
        /// 区别只在于"短舱半径"和"设计马赫"由调用方给：
        /// 零件自己就是外壳，所以外罩半径按捕获半径算，长度由面板给。
        /// 性能那边（<see cref="IntakeSolver.SolvePart"/>）用的是同一组三块面积，不许各推一套。
        /// </summary>
        public static IntakeShape OfPart(IntakeSpec spec, double faceDiameter, double length)
        {
            IntakeShape s = new IntakeShape();
            IntakeSpec sp = (spec ?? new IntakeSpec()).Resolved();
            s.Kind = sp.Kind;

            double faceD = faceDiameter > 0.0 ? faceDiameter : 0.5;
            double capD = sp.CaptureDiameter > 0.0 ? sp.CaptureDiameter : faceD * 1.10;
            double ratio = Math.Max(0.2, Math.Min(1.2, sp.ThroatRatio > 0.0 ? sp.ThroatRatio : IntakeSpec.DefaultThroatRatio(sp.Kind)));

            s.CaptureArea = Math.PI * 0.25 * capD * capD;
            s.ThroatArea = s.CaptureArea * ratio;
            s.RCapture = Math.Max(Math.Sqrt(s.CaptureArea / Math.PI), 0.02);
            s.RThroat = Math.Max(Math.Sqrt(s.ThroatArea / Math.PI), 0.02);
            s.RFace = Math.Max(faceD * 0.5, 0.02);
            s.FaceArea = Math.PI * s.RFace * s.RFace;

            s.Length = Math.Max(length, 0.10);
            s.YFront = s.Length * 0.5;          // 零件坐标系：以零件原点为中心
            s.YThroat = s.YFront - s.Length * ThroatFrac;
            s.YFace = s.YFront - s.Length;

            double lip = sp.ResolvedLipRadius(capD);
            if (sp.Kind != IntakeKind.Axial) lip = Math.Min(lip, capD * 0.015);
            s.LipRadius = Math.Max(lip, 0.002);

            double outerR = Math.Max(s.RCapture + s.LipRadius * 1.2, s.RFace * 0.98);
            BuildDuct(s, sp);
            BuildCowl(s, sp, outerR);
            BuildCenterBody(s, sp, Intake.DesignMachOf(sp, double.NaN));
            return s;
        }

        /// <summary>
        /// 气道内壁：唇口内缘收进喉道，再从喉道放到发动机面。
        ///
        /// 涡喷/涡扇都要先"收"一下（否则唇口一钝就溢流），
        /// 收得太狠会在喉道壅塞——那是 `IntakeSolver` 的事，这里只画形状。
        /// </summary>
        private static void BuildDuct(IntakeShape s, IntakeSpec sp)
        {
            double rIn = Math.Max(s.RCapture - s.LipRadius * 0.55, s.RThroat * 0.90);
            s.Duct.Add(new IntakePoint(s.YFront, rIn, "唇口内缘"));
            // 唇口往后一点点才开始真正收缩：真机唇口后面还有一小段基本等直。
            double yKnee = s.YFront - s.Length * 0.06;
            s.Duct.Add(new IntakePoint(yKnee, Math.Min(rIn, s.RCapture * 0.995), "唇口后"));
            s.Duct.Add(new IntakePoint(s.YThroat, s.RThroat, "喉道"));
            double yDiff = s.YThroat - (s.YThroat - s.YFace) * 0.55;
            double rDiff = s.RThroat + (s.RFace - s.RThroat) * 0.55;
            s.Duct.Add(new IntakePoint(yDiff, rDiff, "扩压段"));
            s.Duct.Add(new IntakePoint(s.YFace, s.RFace, "发动机面"));
        }

        /// <summary>外罩：唇口外缘往后交给短舱（短舱自己的型线在网格那边）。</summary>
        private static void BuildCowl(IntakeShape s, IntakeSpec sp, double outerRadius)
        {
            double rOut = s.RCapture + s.LipRadius;
            double rTube = Math.Max(outerRadius, 0.02);
            s.Cowl.Add(new IntakePoint(s.YFront, rOut, "唇口外缘"));
            s.Cowl.Add(new IntakePoint(s.YFront - s.Length * 0.08, Math.Max(rOut * 1.004, rTube), "外罩肩"));
            s.Cowl.Add(new IntakePoint(s.YFront - s.Length * 0.45, Math.Max(rOut * 0.99, rTube), "外罩中段"));
            s.Cowl.Add(new IntakePoint(s.YFace, rTube, "外罩后端"));
        }

        /// <summary>
        /// 中心锥 / 斜板。
        ///
        /// 锥体是轴对称的锥，斜板是二维的板——但网格是回转体，
        /// 板子只能画成"一圈斜板"（回转近似）。这一点在说明里写清楚，
        /// 免得玩家以为模型和真机一样是半边的。
        ///
        /// 压缩角由设计马赫定：Ma1 附近 ~8°，Ma2 附近 ~18°，
        /// 太小的角度压不出激波、太大角度激波太强总压亏得多。
        /// </summary>
        /// <summary>锥尖最多往前伸出唇口多少（占进气道长度）。再往前就不是"锥尖在唇口前"，是一条长矛。</summary>
        public const double ConeTipShiftMax = 0.25;

        /// <summary>锥尖最多缩进管内多少（占进气道长度）。缩过头锥体整个落在喉道后面，就没有压缩面了。</summary>
        public const double ConeTipShiftMin = -0.25;

        /// <summary>把玩家填的锥尖位置夹进量程。NaN 当 0（= 老外形）。</summary>
        public static double ClampConeTipShift(double shift)
        {
            if (!GasState.IsFinite(shift)) return 0.0;
            return Math.Min(Math.Max(shift, ConeTipShiftMin), ConeTipShiftMax);
        }

        private static void BuildCenterBody(IntakeShape s, IntakeSpec sp, double designMach)
        {
            if (sp.Kind != IntakeKind.Cone && sp.Kind != IntakeKind.Ramp) return;

            double m = designMach > 0.0 ? designMach : 0.9;
            double theta = Math.Max(6.0, Math.Min(20.0, 8.0 + 5.0 * (m - 1.0)));
            s.RampAngleDeg = theta;
            s.HasCenterBody = true;

            double rad = theta * Math.PI / 180.0;
            double len = Math.Max(s.Length * 0.55, 0.05);
            // 锥尖位置：缺省离唇口 2% 长度（真机锥尖几乎顶在唇口平面上），
            // 玩家可以把它往前伸到唇口外面、也可以缩进管内——可调中心锥的行程就是这么走的。
            s.ConeTipShift = ClampConeTipShift(sp.ConeTipShift);
            double yTip = s.YFront - s.Length * 0.02 + s.ConeTipShift * s.Length;
            double rMax = Math.Max(s.RThroat * (sp.Kind == IntakeKind.Cone ? 0.42 : 0.55), 0.02);

            s.CenterBody.Add(new IntakePoint(yTip, 0.004, "锥尖"));
            if (sp.Kind == IntakeKind.Cone)
            {
                // 单锥：一条直线到喉道前
                s.CenterBody.Add(new IntakePoint(yTip - len, rMax, "锥面"));
                s.CenterBody.Add(new IntakePoint(s.YFace, rMax * 0.92, "锥后"));
            }
            else
            {
                // 双斜板：第一道斜面更陡，第二道缓一点（真机是"折一下"而不是一条直线）
                double y1 = yTip - len * 0.55;
                double r1 = rMax * 0.62;
                s.CenterBody.Add(new IntakePoint(y1, r1, "第一道斜板"));
                s.CenterBody.Add(new IntakePoint(yTip - len, rMax, "第二道斜板"));
                s.CenterBody.Add(new IntakePoint(s.YFace, rMax * 0.95, "斜板后"));
            }
        }

        /// <summary>
        /// 管道总压损失系数（&lt;1）。这是"形状 → 性能"那条链的另一半：
        /// 面积决定能吃多少气，形状决定这些气到发动机面还剩多少压力。
        ///
        /// 两项，都是公开文献里的量级，没有拿任何一台真机标定过：
        ///   摩擦 —— 按 L/(2R_喉道) 算；同样的长度，喉道细一倍损失翻倍。
        ///   扩压 —— 面/喉道面积比越大，气流减速越多，附面层越容易顶不住。
        /// 下限 0.80：再烂的进气道也不至于丢掉两成总压，真到那一步该看的是溢流和畸变。
        /// </summary>
        public static double DuctLossFactor(IntakeShape s)
        {
            if (s == null) return 1.0;
            double rThroat = Math.Max(s.RThroat, 0.01);
            double ld = Math.Max(s.Length, 0.0) / (2.0 * rThroat);
            double friction = 0.0060 * ld;
            double areaRatio = s.FaceArea / Math.Max(s.ThroatArea, 1e-6);
            double diffuser = 0.030 * Math.Max(0.0, Math.Sqrt(areaRatio) - 1.0);
            return Math.Min(1.0, Math.Max(0.80, 1.0 - friction - diffuser));
        }

        /// <summary>型线在某个轴向位置的半径（线性插值，两端夹住）。</summary>
        public static double RadiusAt(List<IntakePoint> line, double y)
        {
            if (line == null || line.Count == 0) return 0.0;
            if (y >= line[0].Y) return line[0].R;
            for (int i = 1; i < line.Count; i++)
            {
                IntakePoint a = line[i - 1], b = line[i];
                if (y <= a.Y && y >= b.Y)
                {
                    double dy = a.Y - b.Y;
                    if (Math.Abs(dy) < 1e-9) return b.R;
                    return a.R + (b.R - a.R) * ((a.Y - y) / dy);
                }
            }
            return line[line.Count - 1].R;
        }

        /// <summary>喉道面积（几何口径，和 `Intake.ThroatAreaOf` 必须一致）。</summary>
        public static double ThroatArea(EngineDesignData d)
        {
            IntakeShape s = Of(d);
            return Math.PI * s.RThroat * s.RThroat;
        }
    }
}