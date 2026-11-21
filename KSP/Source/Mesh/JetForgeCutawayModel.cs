using System;
using System.Collections.Generic;
using JetForge.Core;
using UnityEngine;

namespace JetForge
{
    /// <summary>
    /// 二维剖视模型（设计器的可视化编辑用）。
    ///
    /// 和三维网格共用同一个 `Measure()` 和各段剖面——**尺寸不是"照着模型再画一遍"，
    /// 是同一份数**。玩家在剖视图上量到风扇占机长多少，模型上就是多少；
    /// 以后改了几何，两张图一起变，不会一张对一张错。
    ///
    /// 每个部件都带一个 `Tab`：点它就跳到那一页去改。
    /// </summary>
    public static partial class JetForgeMeshBuilder
    {
        /// <summary>
        /// 剖视图里的一个部件。`Poly` 是 (轴向位置 y, 半径 r) 的闭合多边形，
        /// 渲染时上下各画一份（发动机是回转体，剖面左右对称）。
        /// </summary>
        public sealed class CutawayShape
        {
            public string Name;
            /// <summary>点击后跳到设计器的哪一页（-1 = 不可点）。</summary>
            public int Tab;
            public Color32 Fill;
            /// <summary>悬停时显示的一行尺寸。</summary>
            public string Info;
            public readonly List<Vector2> Poly = new List<Vector2>();

            public float YFront
            {
                get { float y = -1e9f; for (int i = 0; i < Poly.Count; i++) if (Poly[i].x > y) y = Poly[i].x; return y; }
            }
            public float YBack
            {
                get { float y = 1e9f; for (int i = 0; i < Poly.Count; i++) if (Poly[i].x < y) y = Poly[i].x; return y; }
            }
            public float RMax
            {
                get { float r = 0f; for (int i = 0; i < Poly.Count; i++) if (Poly[i].y > r) r = Poly[i].y; return r; }
            }
        }

        /// <summary>把一条剖面线加厚成"板"：外沿正走、内沿倒走，围成闭合多边形。</summary>
        private static List<Vector2> Band(Profile p, float t)
        {
            List<Vector2> poly = new List<Vector2>(p.Points.Count * 2);
            for (int i = 0; i < p.Points.Count; i++)
                poly.Add(new Vector2(p.Points[i].x, p.Points[i].y));
            for (int i = p.Points.Count - 1; i >= 0; i--)
                poly.Add(new Vector2(p.Points[i].x, Mathf.Max(p.Points[i].y - t, 0.004f)));
            return poly;
        }

        /// <summary>轴向一段 × 径向一圈的方块（叶片排、级、火焰筒都是这个形状）。</summary>
        private static List<Vector2> Box(float yFront, float yBack, float rIn, float rOut)
        {
            List<Vector2> poly = new List<Vector2>(4);
            poly.Add(new Vector2(yFront, rOut));
            poly.Add(new Vector2(yBack, rOut));
            poly.Add(new Vector2(yBack, rIn));
            poly.Add(new Vector2(yFront, rIn));
            return poly;
        }

        private static CutawayShape Shape(string name, int tab, Color32 fill, string info, List<Vector2> poly)
        {
            CutawayShape s = new CutawayShape();
            s.Name = name;
            s.Tab = tab;
            s.Fill = fill;
            s.Info = info;
            s.Poly.AddRange(poly);
            return s;
        }

        private static string CompressorName(CompressorSpec c)
        {
            if (c == null) return Loc.T("无", "none");
            switch (c.Kind)
            {
                case CompressorKind.Centrifugal: return Loc.T("离心", "centrifugal");
                default: return Loc.T("轴流", "axial");
            }
        }

        /// <summary>
        /// 生成剖视图的部件表。顺序 = 绘制顺序（后面的压在前面的上面）。
        ///
        /// 只画"能看懂的简化形"：叶片画成一片环带、压气机画成一段带轮毂的环带、
        /// 涡轮每级一环、燃烧室罐式就画一圈罐子、加力段画筒体 + 三道稳焰环。
        /// **但每个尺寸都是真值**（叶尖直径、级数、机长比例、加力段长度都取自设计与站位表），
        /// 所以"各部件的尺寸占比"是对的。
        /// </summary>
        public static List<CutawayShape> Cutaway(EngineDesignData d)
        {
            List<CutawayShape> list = new List<CutawayShape>();
            if (d == null) return list;
            Dims m = Measure(d);
            float t = Mathf.Max(m.L * 0.010f, 0.004f);

            IntakeSpec sp = d.Intake != null ? d.Intake.Resolved() : new IntakeSpec().Resolved();

            // ① 外壳：涡扇是短舱，涡喷是一根筒子
            list.Add(Shape(Loc.T("机匣外壳", "Nacelle shell"), DesignTabs.General, new Color32(158, 161, 166, 255),
                string.Format(Loc.T("长 {0:F2} m｜外径 {1:F2} m", "length {0:F2} m | OD {1:F2} m"), m.L, m.RNac * 2f),
                Band(NacelleProfile(m), t)));

            // ② 进气道：唇口 → 喉道 → 发动机面，型线从 IntakeGeometry 来（和网格同一份）
            //
            // 这一截是**发动机自带兜底外形**：真正的进气道是独立零件，装在哪、多大口径都由
            // 零件自己决定。所以点它跳"总体"页（说明兜底口径），不再跳一个已经不存在的进气道页。
            {
                IntakeShape ish = IntakeGeometry.Of(d);
                Profile ip = new Profile();
                for (int i = 0; i < ish.Duct.Count; i++)
                    ip.Add((float)ish.Duct[i].Y, Mathf.Max((float)ish.Duct[i].R, 0.004f));
                list.Add(Shape(Loc.T("进气道（兜底外形）", "Intake (fallback shape)"), DesignTabs.General, new Color32(122, 128, 136, 255),
                    string.Format(Loc.T("{0}｜捕获半径 {1:F3} m｜喉道半径 {2:F3} m｜发动机面半径 {3:F3} m｜装了进气道零件时以零件为准",
                        "{0} | capture R {1:F3} m | throat R {2:F3} m | engine face R {3:F3} m | an intake part overrides this"),
                        IntakeSpec.KindName(sp.Kind), ish.RCapture, ish.RThroat, ish.RFace),
                    Band(ip, t)));
                if (ish.HasCenterBody)
                {
                    List<Vector2> cb = new List<Vector2>();
                    for (int i = 0; i < ish.CenterBody.Count; i++)
                        cb.Add(new Vector2((float)ish.CenterBody[i].Y, Mathf.Max((float)ish.CenterBody[i].R, 0.004f)));
                    cb.Add(new Vector2(cb[cb.Count - 1].x, 0.004f));
                    list.Add(Shape(ish.Kind == IntakeKind.Ramp ? Loc.T("进气斜板", "Intake ramp") : Loc.T("进气中心锥", "Intake cone"),
                        DesignTabs.General, new Color32(150, 132, 116, 255),
                        string.Format(Loc.T("{0:F1}° 压缩角｜长 {1:F2} m", "{0:F1} deg ramp | length {1:F2} m"), ish.RampAngleDeg,
                            Math.Abs(ish.CenterBody[0].Y - ish.CenterBody[ish.CenterBody.Count - 1].Y)),
                        cb));
                }
            }

            // ③ 核心机匣（压气机 → 涡轮那一整段的外壳）
            list.Add(Shape(Loc.T("核心机匣", "Core casing"), DesignTabs.Compressor, new Color32(132, 136, 142, 255),
                string.Format(Loc.T("核心半径 {0:F3} m｜燃烧室处 ×{1:F2}", "core radius {0:F3} m | x{1:F2} at the combustor"), m.RCore, BurnerShellFactor(d)),
                Band(CoreProfile(m, d), t * 0.9f)));

            // ④ 风扇：叶根到叶尖的一排叶片
            if (m.HasFan)
            {
                float chord = Mathf.Clamp(m.L * 0.070f, 0.05f, 0.20f);
                list.Add(Shape(Loc.T("风扇", "Fan"), DesignTabs.Fan, new Color32(92, 122, 152, 255),
                    string.Format(Loc.T("直径 {0:F2} m｜{1} 片｜压比 {2:F2}", "diameter {0:F2} m | {1} blades | pressure ratio {2:F2}"),
                        m.RFan * 2f, BladesOf(d), d.Fan.PressureRatio),
                    Box(m.YFan + chord * 0.5f, m.YFan - chord * 0.5f,
                        m.RFan * Mathf.Clamp((float)d.Fan.HubTipRatio, 0.2f, 0.75f), m.RFan * 0.962f)));
            }

            // ⑤ 压气机：LPC / HPC 各占一段，段内按叶尖直径画
            float yCompEnd = Mathf.Lerp(m.YCoreStart, m.YAb, 0.45f);
            float yBurn = Mathf.Lerp(m.YCoreStart, m.YAb, 0.60f);
            float split = d.Lpc.Stages > 0 && d.Hpc.Stages > 0
                ? Mathf.Lerp(m.YCoreStart, yCompEnd, 0.45f) : m.YCoreStart;
            if (d.Lpc.Stages > 0)
                AddCompShape(list, m, Loc.T("低压压气机 LPC", "LP compressor LPC"), d.Lpc, m.YCoreStart, split, new Color32(88, 118, 148, 255));
            if (d.Hpc.Stages > 0)
                AddCompShape(list, m, Loc.T("高压压气机 HPC", "HP compressor HPC"), d.Hpc, split, yCompEnd, new Color32(104, 134, 164, 255));

            // ⑥ 燃烧室：罐式画一圈罐子，环形/罐环/短环形画一圈筒
            {
                float rShell = m.RCore * BurnerShellFactor(d) * 0.95f;
                float yFront = yBurn + m.L * 0.030f, yBack = yBurn - m.L * 0.030f;
                int cans = BurnerCans(d);
                string info = string.Format(Loc.T("{0}｜L/H {1:F2}｜ΔP/P {2:F3}", "{0} | L/H {1:F2} | dP/P {2:F3}"),
                    BurnerTypeName(d),
                    d.Burner.LinerLd > 0.0 ? d.Burner.LinerLd : 0.0,
                    0.0);
                if (d.Burner.Kind == BurnerKind.Can && cans > 1)
                {
                    // 剖面上只看得见"一圈罐子里被切到的两个"。画成两个椭圆块，
                    // 比画一整圈环诚实：罐式的外径起伏就是这个样子。
                    float rc = (rShell + m.RCore * 0.42f) * 0.5f;
                    float rr = (rShell - m.RCore * 0.42f) * 0.45f;
                    for (int k = 0; k < 2; k++)
                    {
                        List<Vector2> can = new List<Vector2>();
                        int seg = 16;
                        float rMid = k == 0 ? rc + rr : rc - rr * 0.6f;
                        for (int i = 0; i < seg; i++)
                        {
                            float a = Mathf.PI * 2f * i / seg;
                            can.Add(new Vector2(
                                Mathf.Lerp(yFront, yBack, 0.5f + 0.5f * Mathf.Cos(a)),
                                rMid + rr * Mathf.Sin(a)));
                        }
                        list.Add(Shape(string.Format(Loc.T("燃烧室（{0} 罐）", "Combustor ({0} cans)"), cans), DesignTabs.Burner, new Color32(178, 110, 66, 255),
                            string.Format(Loc.T("{0}｜{1} 罐｜L/H {2:F2}", "{0} | {1} cans | L/H {2:F2}"), Loc.T("罐式", "can"), cans,
                                d.Burner.LinerLd > 0.0 ? d.Burner.LinerLd : 3.0), can));
                    }
                }
                else
                {
                    list.Add(Shape(string.Format(Loc.T("燃烧室（{0}）", "Combustor ({0})"), BurnerTypeName(d)), DesignTabs.Burner, new Color32(178, 110, 66, 255), info,
                        Box(yFront, yBack, m.RCore * 0.42f, rShell)));
                }
            }

            // ⑦ 涡轮：每级一环，级数看得出来
            AddTurbShape(list, m, Loc.T("高压涡轮 HPT", "HP turbine HPT"), d.Hpt, 0.755f, new Color32(160, 96, 92, 255));
            if (m.HasIpt) AddTurbShape(list, m, Loc.T("中压涡轮 IPT", "IP turbine IPT"), d.Ipt, 0.815f, new Color32(150, 92, 96, 255));
            AddTurbShape(list, m, Loc.T("低压涡轮 LPT", "LP turbine LPT"), d.Lpt, 0.875f, new Color32(142, 88, 100, 255));

            // ⑧ 加力段：筒体 + 三道火焰稳定器
            if (m.HasAb)
            {
                list.Add(Shape(Loc.T("加力燃烧室", "Afterburner"), DesignTabs.Afterburner, new Color32(136, 112, 96, 255),
                    string.Format(Loc.T("长 {0:F2} m｜{1}｜限温 {2:F0} K", "length {0:F2} m | {1} | temp limit {2:F0} K"), m.AbLen,
                        AfterburnerTypeName(d), d.Afterburner.ExitTemp),
                    Band(AbDuctProfile(m, d), t * 0.9f)));

                float rHold = AbRadius(m, d) * 0.80f;
                for (int i = 0; i < 3; i++)
                {
                    float y = m.YAb - m.AbLen * (0.26f + 0.16f * i);
                    list.Add(Shape(Loc.T("火焰稳定器", "Flame holder"), DesignTabs.Afterburner, new Color32(190, 158, 122, 255),
                        Loc.T("加力段的 V 形环：燃气在这里二次燃烧", "V-shaped ring in the afterburner where the gas burns again"),
                        Box(y + m.AbLen * 0.015f, y - m.AbLen * 0.015f, rHold * 0.94f, rHold)));
                }
            }

            // ⑨ 尾喷管
            list.Add(Shape(Loc.T("尾喷管", "Exhaust nozzle"), DesignTabs.General, new Color32(120, 106, 100, 255),
                string.Format(Loc.T("{0}｜出口 R{1:F3} m", "{0} | exit R{1:F3} m"), NozzleTypeName(d), NozzleExitR(m, d)),
                Band(NozzleProfile(m, d), t * 0.85f)));

            // ⑩ 排气锥（实心，从内通道底收到尖）
            {
                Profile tc = TailConeProfile(m, d);
                List<Vector2> poly = new List<Vector2>();
                for (int i = 0; i < tc.Points.Count; i++) poly.Add(tc.Points[i]);
                poly.Add(new Vector2(tc.Points[tc.Points.Count - 1].x, 0.004f));
                list.Add(Shape(Loc.T("排气锥", "Tail cone"), DesignTabs.General, new Color32(104, 96, 92, 255),
                    Loc.T("把内通道封底，燃气只能从外圈走", "Closes off the inner duct so the gas can only leave through the outer ring"), poly));
            }
            return list;
        }

        private static void AddCompShape(List<CutawayShape> list, Dims m, string name, CompressorSpec c,
            float yFront, float yBack, Color32 fill)
        {
            if (c == null || c.Stages <= 0) return;
            if (yFront - yBack < 1e-4f) return;
            float rTip = Mathf.Max((float)c.TipDiameter * 0.5f, m.RCore * 0.30f);
            float rHub = rTip * Mathf.Clamp((float)c.HubTipRatio, 0.25f, 0.85f);
            list.Add(Shape(name, DesignTabs.Compressor, fill,
                string.Format(Loc.T("{0} 级｜叶尖 R{1:F3}｜轮毂比 {2:F2}｜{3}", "{0} stages | tip R{1:F3} | hub/tip {2:F2} | {3}"),
                    c.Stages, rTip, c.HubTipRatio, CompressorName(c)),
                Box(yFront, yBack, rHub, rTip)));
        }

        private static void AddTurbShape(List<CutawayShape> list, Dims m, string name, TurbineSpec t,
            float frac, Color32 fill)
        {
            if (t == null || t.Stages <= 0) return;
            float y = Mathf.Lerp(m.YCoreStart, m.YAb, frac);
            float rTip = Mathf.Max((float)t.TipDiameter * 0.5f, m.RCore * 0.45f);
            float rHub = rTip * Mathf.Clamp((float)t.HubTipRatio, 0.25f, 0.85f);
            int rows = Mathf.Min(t.Stages, 4);
            float pitch = Mathf.Max(m.L * 0.011f, 0.006f);
            for (int i = 0; i < rows; i++)
            {
                float yy = y + (rows - 1) * pitch * 0.5f - i * pitch;
                list.Add(Shape(name, DesignTabs.Turbine, fill,
                    string.Format(Loc.T("{0} 级｜叶尖 R{1:F3}｜ψ {2:F2}", "{0} stages | tip R{1:F3} | psi {2:F2}"), t.Stages, rTip, t.PsiScale),
                    Box(yy + pitch * 0.32f, yy - pitch * 0.32f, rHub, rTip)));
            }
        }

        private static int BurnerCans(EngineDesignData d)
        {
            if (d == null || d.Burner == null) return 0;
            if (d.Burner.Kind != BurnerKind.Can) return 0;
            return d.Burner.CanCount > 0 ? d.Burner.CanCount : 8;
        }

        public static string BurnerTypeName(EngineDesignData d)
        {
            if (d == null || d.Burner == null) return Loc.T("环形", "annular");
            switch (d.Burner.Kind)
            {
                case BurnerKind.Can: return Loc.T("罐式", "can");
                case BurnerKind.Cannular: return Loc.T("罐环", "can-annular");
                case BurnerKind.ShortAnnular: return Loc.T("短环形", "short annular");
                default: return Loc.T("环形", "annular");
            }
        }

        public static string AfterburnerTypeName(EngineDesignData d)
        {
            if (d == null || d.Afterburner == null) return Loc.T("无", "none");
            switch (d.Afterburner.Kind)
            {
                case AfterburnerKind.Ring: return Loc.T("径向环", "radial ring");
                case AfterburnerKind.SprayRing: return Loc.T("喷油环", "spray ring");
                default: return Loc.T("筒式", "barrel");
            }
        }

        public static string NozzleTypeName(EngineDesignData d)
        {
            if (d == null) return Loc.T("固定收敛", "fixed convergent");
            switch (d.Nozzle)
            {
                case NozzleKind.Variable: return Loc.T("可变面积", "variable area");
                case NozzleKind.ConvergingDiverging: return Loc.T("收扩 C-D", "converging-diverging");
                default: return Loc.T("固定收敛", "fixed convergent");
            }
        }

        /// <summary>
        /// 剖视模型自检：部件数、页面映射、轴向与径向范围是否与站位表一致。
        /// 这几条数就是"尺寸占比正确"的证据——图上的机长和机匣半径必须等于模型上的。
        /// </summary>
        public static string DescribeCutaway(EngineDesignData d)
        {
            List<CutawayShape> list = Cutaway(d);
            if (list.Count == 0) return Loc.T("剖视：没有部件", "Cutaway: no parts");
            float yFront = -9e9f, yBack = 9e9f, rMax = 0f;
            int badTab = 0, degenerate = 0;
            for (int i = 0; i < list.Count; i++)
            {
                CutawayShape s = list[i];
                if (s.Tab < 0 || s.Tab > DesignTabs.Cutaway) badTab++;
                if (s.Poly.Count < 3) degenerate++;
                if (s.YFront > yFront) yFront = s.YFront;
                if (s.YBack < yBack) yBack = s.YBack;
                if (s.RMax > rMax) rMax = s.RMax;
            }
            Dims m = Measure(d);
            return string.Format(
                Loc.T("剖视：画了 {0} 个部件｜占轴向 {1:F2}~{2:F2} m（机长 {3:F2} m）｜最粗处半径 {4:F3} m（进气口 {5:F3} m）｜落到图纸外面的部件 {6} 个｜形状压成一条线的 {7} 个",
                    "Cutaway: {0} parts | axial {1:F2} to {2:F2} m (engine {3:F2} m) | fattest radius {4:F3} m (inlet {5:F3} m) | off-page parts {6} | flattened shapes {7}"),
                list.Count, yBack, yFront, m.L, rMax, m.RIn, badTab, degenerate);
        }
    }
}