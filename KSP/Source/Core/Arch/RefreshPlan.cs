using System;

namespace JetForge.Core
{
    /// <summary>
    /// 要重建哪几层。文档 B 的原话是"几何重建 / 性能重算 / 声音 / 尾焰 / 动画 **分开触发**"——
    /// 因为现在改一个参数会把整条链全撸一遍：拖一下喉道比，网格重建、性能网格重算、
    /// 材质重新分槽、羽流重新挂一遍。在 30 Hz 的飞行里这不算便宜，在编辑器里连续拖滑杆更是。
    ///
    /// 三层的包含关系不是随意定的：
    /// - **几何**变了，气道面积就变了，性能必然要重算（性能依赖几何）；
    /// - **性能**变了，尾焰长度/声音音调也变（表现依赖性能）；
    /// - 反过来不成立：改科技年代不会改变外形，改贴图不会改变推力。
    /// 所以这是一条单向的依赖链，`RefreshLayer` 用位标志表达，`Compare` 负责往上补齐。
    /// </summary>
    [Flags]
    public enum RefreshLayer
    {
        None = 0,
        Geometry = 1,
        Performance = 2,
        Presentation = 4,
        All = Geometry | Performance | Presentation
    }

    /// <summary>
    /// 一份"三层各自的指纹"。指纹只用来说明"变没变"，不参与计算——
    /// 拿它按位比字符串，比逐字段写比较函数可靠：加了字段忘了加比较，是这类代码最常见的错。
    /// </summary>
    public struct RefreshStamp
    {
        public string Geometry;
        public string Performance;
        public string Presentation;

        public RefreshStamp(string geometry, string performance, string presentation)
        {
            Geometry = geometry ?? "";
            Performance = performance ?? "";
            Presentation = presentation ?? "";
        }

        /// <summary>三层全空 = 刚建出来的对象，什么都还没算。</summary>
        public bool IsEmpty
        {
            get { return Geometry.Length == 0 && Performance.Length == 0 && Presentation.Length == 0; }
        }
    }

    /// <summary>一次刷新的结论：要重建哪几层、为什么。</summary>
    public sealed class RefreshPlan
    {
        public RefreshLayer Layers = RefreshLayer.None;
        /// <summary>人话理由，直接进日志/面板（"型线变了 → 几何+性能+表现"）。</summary>
        public string Why = "";

        public bool Geometry { get { return (Layers & RefreshLayer.Geometry) != 0; } }
        public bool Performance { get { return (Layers & RefreshLayer.Performance) != 0; } }
        public bool Presentation { get { return (Layers & RefreshLayer.Presentation) != 0; } }
        public bool Anything { get { return Layers != RefreshLayer.None; } }

        public static readonly RefreshPlan Nothing = new RefreshPlan();

        /// <summary>
        /// 比较两份指纹，得出该重建哪几层。
        /// 旧指纹为空（首次）→ 全建；只有几何变 → 三层全建；只有性能变 → 性能+表现；
        /// 只有表现变（贴图、材质槽）→ 只重建表现。
        /// </summary>
        public static RefreshPlan Compare(RefreshStamp oldStamp, RefreshStamp now)
        {
            RefreshPlan p = new RefreshPlan();
            if (now.IsEmpty)
            {
                p.Layers = RefreshLayer.None;
                p.Why = "指纹为空，没什么可建的";
                return p;
            }
            if (oldStamp.IsEmpty)
            {
                p.Layers = RefreshLayer.All;
                p.Why = "首次建立";
                return p;
            }

            bool geo = !string.Equals(oldStamp.Geometry, now.Geometry, StringComparison.Ordinal);
            bool perf = !string.Equals(oldStamp.Performance, now.Performance, StringComparison.Ordinal);
            bool pres = !string.Equals(oldStamp.Presentation, now.Presentation, StringComparison.Ordinal);

            if (geo)
                p.Layers = RefreshLayer.All;              // 外形变了：气道→性能→尾焰一条线全变
            else if (perf)
                p.Layers = RefreshLayer.Performance | RefreshLayer.Presentation;
            else if (pres)
                p.Layers = RefreshLayer.Presentation;

            if (p.Layers == RefreshLayer.None) { p.Why = "三层都没变，不用重建"; return p; }

            string what = geo ? "几何" : (perf ? "性能" : "表现");
            p.Why = string.Format("{0}变了 → {1}", what, Names(p.Layers));
            return p;
        }

        public static string Names(RefreshLayer l)
        {
            if (l == RefreshLayer.None) return "无";
            string s = "";
            if ((l & RefreshLayer.Geometry) != 0) s += "几何";
            if ((l & RefreshLayer.Performance) != 0) s += (s.Length > 0 ? "+" : "") + "性能";
            if ((l & RefreshLayer.Presentation) != 0) s += (s.Length > 0 ? "+" : "") + "表现";
            return s;
        }

        public override string ToString()
        {
            return Names(Layers) + (Why.Length > 0 ? "（" + Why + "）" : "");
        }
    }

    /// <summary>
    /// 进气道三层指纹的具体取法。放在核心层是为了它能进回归——
    /// Unity 侧那半段（真正重建网格）没法在命令行里跑。
    /// </summary>
    public static class RefreshStamps
    {
        /// <summary>几何：型线（每条截面）+ 发动机面直径 + 轴长。这三样决定网格顶点。</summary>
        public static string IntakeGeometry(IntakeSpec sp, double faceDiameter, double length)
        {
            string shape = "无型线";
            if (sp != null && sp.Shape != null && sp.Shape.Sections != null && sp.Shape.Sections.Count >= IntakeProfile.MinSections)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("n").Append(sp.Shape.Sections.Count);
                for (int i = 0; i < sp.Shape.Sections.Count; i++)
                {
                    IntakeSection s = sp.Shape.Sections[i];
                    sb.Append('|').Append(s.X.ToString("R")).Append(',').Append(s.Width.ToString("R"))
                      .Append(',').Append(s.Height.ToString("R")).Append(',').Append(s.OffsetY.ToString("R"))
                      .Append(',').Append(s.OffsetZ.ToString("R")).Append(',').Append(s.Exponent.ToString("R")).Append(',').Append(s.Wall.ToString("R"))
                      .Append(',').Append((int)s.Shape);
                    if (s.Polygon != null && s.Polygon.Count > 0)
                    {
                        sb.Append(",p").Append(s.Polygon.Count);
                        for (int k = 0; k < s.Polygon.Count; k++)
                            sb.Append('(').Append(s.Polygon[k].U.ToString("R")).Append(':')
                              .Append(s.Polygon[k].W.ToString("R")).Append(')');
                    }
                }
                shape = sb.ToString();
            }
            return string.Format("G|{0}|d{1:F4}|L{2:F4}", shape, faceDiameter, length);
        }

        /// <summary>
        /// 性能：型式、超声速模型、喉道比、抽气、管道弯曲、唇口半径、科技年代。
        /// 注意**不含迎角/侧滑**——那两个数每帧都在变，把它们算进指纹等于每帧重算性能；
        /// 它们由求解器的实时输入给出，不属于"设计"。
        /// </summary>
        public static string IntakePerformance(IntakeSpec sp, TechLevel tech)
        {
            if (sp == null) return "P|空";
            IntakeSpec r = sp.Resolved();
            return string.Format("P|{0}|{1}|tr{2:F4}|b{3:F4}|bend{4:F2}|lip{5:F4}|t{6}",
                r.Kind, r.Model, r.ThroatRatio, r.BleedFrac, r.DuctBendDeg, r.ResolvedLipRadius(0.0),
                tech != null ? tech.Year.ToString("F0") : "-");
        }

        /// <summary>
        /// 表现：材质槽怎么分（哪几套贴图在用）、羽流按冷端还是热端。
        /// `materialKey` 由 Unity 侧给的槽位/贴图键，核心层不猜它长什么样。
        /// </summary>
        public static string IntakePresentation(IntakeSpec sp, string materialKey)
        {
            return string.Format("V|{0}|{1}", materialKey ?? "",
                sp != null && sp.Model == RecoveryModel.Shock ? "shock" : "emp");
        }

        /// <summary>
        /// 发动机几何：**只有会改变顶点的那几个数**。
        /// 压比、效率、涡轮前温度都不在这里——它们改的是性能，不该让网格重建。
        /// 齿轮比要算进来：它决定齿轮箱那一段有多长。
        /// </summary>
        public static string EngineGeometry(EngineDesignData d)
        {
            if (d == null) return "G|空";
            string fan = d.HasFan && d.Fan != null
                ? string.Format("{0:F4}/{1}/{2:F4}/{3}/{4:F3}", d.Fan.Diameter, d.Fan.Stages,
                    d.Fan.HubTipRatio, d.Fan.BladeCount, d.Fan.GearRatio)
                : "-";
            return string.Format("G|{0}|{1}|{2}|{3}|{4:F3}|{5:F3}|{6}|{7}",
                d.Kind, fan, CompOf(d.Lpc), CompOf(d.Hpc),
                d.NacelleDiameter, d.EffectiveLength, d.Nozzle,
                d.Afterburner != null ? "ab" : "dry");
        }

        private static string CompOf(CompressorSpec c)
        {
            if (c == null) return "-";
            return string.Format("{0}/{1}/{2:F4}/{3:F4}/{4}", c.Stages, c.Kind, c.TipDiameter,
                c.HubTipRatio, c.Shaft);
        }

        /// <summary>发动机性能：改了这些要重算性能网格与尾焰，但不必重建顶点。</summary>
        public static string EnginePerformance(EngineDesignData d)
        {
            if (d == null) return "P|空";
            return string.Format("P|{0}|{1:F3}|{2:F3}|{3:F3}|{4:F1}|{5:F3}|{6}|{7}|{8:F3}",
                d.Kind,
                d.Fan != null ? d.Fan.PressureRatio : 0.0,
                d.Lpc != null ? d.Lpc.PsiScale : 0.0,
                d.Hpc != null ? d.Hpc.PsiScale : 0.0,
                d.Burner != null ? d.Burner.ExitTemp : 0.0,
                d.DesignMach,
                d.Hpc != null && d.Hpc.Vigv ? 1 : 0,
                d.Tech != null ? d.Tech.Year.ToString("F0") : "-",
                d.Fan != null ? d.Fan.GearEta : 0.0);
        }

        /// <summary>表现：贴图槽这一层，核心层不猜它长什么样，只比原样字符串。</summary>
        public static string EnginePresentation(EngineDesignData d, string materialKey)
        {
            return string.Format("V|{0}|{1}", materialKey ?? "", d != null ? d.Kind.ToString() : "-");
        }
    }
}