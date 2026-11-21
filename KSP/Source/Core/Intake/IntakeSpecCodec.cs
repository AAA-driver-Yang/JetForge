using System;
using System.Collections.Generic;
using System.Globalization;

namespace JetForge.Core
{
    /// <summary>
    /// <see cref="IntakeSpec"/> 的 ConfigNode 读写。
    ///
    /// 为什么单独拆一份：进气道现在能装成**独立的零件**，它自己也要存一份 IntakeSpec。
    /// 要是发动机存档和进气道零件各写一遍键名，总有一天会出现"发动机读 `throatRatio`、
    /// 零件读 `throat`"这种谁也没报错、只是读出来是缺省的坑。所以键名只有这一份。
    ///
    /// 键名沿用 §85 定下来的那套（`kind`/`captureDiameter`/`throatRatio`/`faceMach`/`bleed`/
    /// `lipRadius`/`ductBend`/`alpha`/`beta`），老存档照样读得回来。
    /// </summary>
    public static class IntakeSpecCodec
    {
        /// <summary>设计节点名。发动机存档里是 JETFORGE_DESIGN 下的子节点 INTAKE。</summary>
        public const string NodeName = "INTAKE";

        public static void Save(ConfigNode node, IntakeSpec spec)
        {
            if (node == null) return;
            IntakeSpec sp = spec != null ? spec : new IntakeSpec();
            node.SetValue("kind", sp.Kind.ToString(), true);
            node.SetValue("captureDiameter", F(sp.CaptureDiameter), true);
            node.SetValue("throatRatio", F(sp.ThroatRatio), true);
            node.SetValue("faceMach", F(sp.FaceMachDesign), true);
            node.SetValue("bleed", F(sp.BleedFrac), true);
            node.SetValue("lipRadius", F(sp.LipRadius), true);
            // 锥尖位置：缺省 0 = 老外形（锥尖离唇口 2% 长度），老存档读回来一个顶点都不动
            node.SetValue("coneTipShift", F(sp.ConeTipShift), true);
            node.SetValue("ductBend", F(sp.DuctBendDeg), true);
            node.SetValue("alpha", F(sp.AlphaDeg), true);
            node.SetValue("beta", F(sp.BetaDeg), true);
            // 新键：老存档里没有它 = 走 Empirical（缺省），所以读回来还是老曲线，读数不变
            node.SetValue("recoveryModel", sp.Model.ToString(), true);
            // 可调几何：缺省关。老存档没有这个键 → 读回来 false，喉道因子恒 1，读数不变
            node.SetValue("variableGeometry", sp.VariableGeometry ? "1" : "0", true);
            // 3D 型线：一个字符串搞定，读不回来就整条作废（见 ParseProfile）——
            // 半条型线比没有型线更糟：它会让"捕获面积"来自文件、"喉道"来自缺省，两头都对不上。
            string prof = ProfileToString(sp.Shape);
            // 没有型线就写空串而不是删键：ConfigNode 的 RemoveValue 在不同 KSP 版本里时有时无，
            // 而空串读回来同样得到 null（ParseProfile 头一行就判空），少一个版本分支。
            node.SetValue("profile", string.IsNullOrEmpty(prof) ? "" : prof, true);
        }

        /// <summary>
        /// 读盘。<paramref name="fallback"/> 原样返回（不克隆）：调用点已经持有它，
        /// 老存档没有这个节点时"保持原样"比"用 new 覆盖"安全——后者会悄悄清掉玩家刚填的值。
        /// </summary>
        public static IntakeSpec Load(ConfigNode node, IntakeSpec fallback)
        {
            if (node == null) return fallback;
            IntakeSpec sp = fallback != null ? fallback : new IntakeSpec();
            sp.Kind = ParseKind(node.GetValue("kind"), sp.Kind);
            sp.CaptureDiameter = D(node, "captureDiameter", sp.CaptureDiameter);
            sp.ThroatRatio = D(node, "throatRatio", sp.ThroatRatio);
            sp.FaceMachDesign = D(node, "faceMach", sp.FaceMachDesign);
            sp.BleedFrac = D(node, "bleed", sp.BleedFrac);
            sp.LipRadius = D(node, "lipRadius", sp.LipRadius);
            sp.ConeTipShift = D(node, "coneTipShift", sp.ConeTipShift);
            sp.DuctBendDeg = D(node, "ductBend", sp.DuctBendDeg);
            sp.AlphaDeg = D(node, "alpha", sp.AlphaDeg);
            sp.BetaDeg = D(node, "beta", sp.BetaDeg);
            sp.Model = ParseModel(node.GetValue("recoveryModel"), sp.Model);
            sp.VariableGeometry = B(node, "variableGeometry", sp.VariableGeometry);
            sp.Shape = ParseProfile(node.GetValue("profile"));
            return sp;
        }

        /// <summary>
        /// 型线 → 字符串。格式：
        /// `x,w,h,oy,oz,n,shape,wall;...` 每段一个截面，最后用 `|spinner:len,ratio,angle,tip` 收尾（可省）。
        /// 为什么不用子节点：型线会被面板每帧重建（拖手柄），
        /// 一行字符串的拷贝成本与"深拷贝一个 List"完全不是一个量级，
        /// 而且存档 diff 时人眼能看出改的是第几段。
        /// </summary>
        public static string ProfileToString(IntakeProfile p)
        {
            if (p == null || p.Sections == null || p.Sections.Count < IntakeProfile.MinSections) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < p.Sections.Count; i++)
            {
                IntakeSection s = p.Sections[i];
                if (s == null) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(F(s.X)).Append(',').Append(F(s.Width)).Append(',').Append(F(s.Height))
                  .Append(',').Append(F(s.OffsetY)).Append(',').Append(F(s.OffsetZ))
                  .Append(',').Append(F(s.Exponent)).Append(',')
                  .Append(((int)s.Shape).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(F(s.Wall)).Append(',')
                  .Append(F(s.TiltPitchDeg)).Append(',').Append(F(s.TiltYawDeg));
            }
            if (p.HasSpinner)
            {
                sb.Append("|spinner:").Append(F(p.SpinnerLength)).Append(',').Append(F(p.SpinnerRadiusRatio))
                  .Append(',').Append(F(p.SpinnerHalfAngleDeg)).Append(',').Append(F(p.SpinnerTipX));
            }
            // 自由多边形：|poly:段号:u,w;u,w;...  —— 段号在前，"哪一段是自由形状"才不会串。
            for (int i = 0; i < p.Sections.Count; i++)
            {
                IntakeSection s = p.Sections[i];
                if (s == null || !s.HasPolygon) continue;
                sb.Append("|poly:").Append(i.ToString(CultureInfo.InvariantCulture)).Append(':');
                for (int k = 0; k < s.Polygon.Count; k++)
                {
                    if (k > 0) sb.Append(';');
                    sb.Append(F(s.Polygon[k].U)).Append(',').Append(F(s.Polygon[k].W));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 字符串 → 型线。任何一段解不出来就返回 null（整条作废），原因见 <see cref="ProfileToString"/> 的注释。
        /// </summary>
        public static IntakeProfile ParseProfile(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string body = s.Trim();
            // 段格式：<截面表>|spinner:...|poly:<段号>:u,w;u,w ...
            string[] segs = body.Split('|');
            body = segs.Length > 0 ? segs[0].Trim() : "";
            if (body.Length == 0) return null;
            IntakeProfile p = new IntakeProfile();
            string[] parts = body.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string[] f = parts[i].Split(',');
                if (f.Length < 3) return null;
                IntakeSection sec = new IntakeSection();
                if (!TryD(f[0], out sec.X) || !TryD(f[1], out sec.Width) || !TryD(f[2], out sec.Height)) return null;
                if (f.Length > 3) TryD(f[3], out sec.OffsetY);
                if (f.Length > 4) TryD(f[4], out sec.OffsetZ);
                if (f.Length > 5) TryD(f[5], out sec.Exponent);
                if (f.Length > 6)
                {
                    double sh;
                    if (TryD(f[6], out sh)) sec.Shape = (IntakeSectionShape)(int)Math.Round(sh);
                }
                if (f.Length > 7) TryD(f[7], out sec.Wall);
                // 第 9/10 个字段是后加的截面倾角：老存档只有 8 个字段，读出来当然还是 0 度，
                // 所以这里是"有就带、没有就算 0"，不需要版本号也不需要迁移。
                if (f.Length > 8) TryD(f[8], out sec.TiltPitchDeg);
                if (f.Length > 9) TryD(f[9], out sec.TiltYawDeg);
                p.Sections.Add(sec);
            }
            for (int si = 1; si < segs.Length; si++)
            {
                string seg = segs[si].Trim();
                if (seg.Length == 0) continue;
                if (seg.StartsWith("spinner", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = seg;
                    int colon = rest.IndexOf(':');
                    if (colon >= 0) rest = rest.Substring(colon + 1);
                    string[] f = rest.Split(',');
                    if (f.Length >= 2)
                    {
                        TryD(f[0], out p.SpinnerLength);
                        TryD(f[1], out p.SpinnerRadiusRatio);
                        if (f.Length > 2) TryD(f[2], out p.SpinnerHalfAngleDeg);
                        if (f.Length > 3) TryD(f[3], out p.SpinnerTipX);
                    }
                }
                else if (seg.StartsWith("poly", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = seg.IndexOf(':');
                    if (colon < 0) continue;
                    int second = seg.IndexOf(':', colon + 1);
                    if (second < 0) continue;
                    double idxD;
                    if (!TryD(seg.Substring(colon + 1, second - colon - 1), out idxD)) continue;
                    int idx = (int)Math.Round(idxD);
                    if (idx < 0 || idx >= p.Sections.Count) continue;
                    List<SectionPoint> poly = new List<SectionPoint>();
                    string[] pts = seg.Substring(second + 1).Split(';');
                    bool ok = true;
                    for (int k = 0; k < pts.Length; k++)
                    {
                        string[] f = pts[k].Split(',');
                        if (f.Length < 2) { ok = false; break; }
                        double u, w;
                        if (!TryD(f[0], out u) || !TryD(f[1], out w)) { ok = false; break; }
                        poly.Add(new SectionPoint(u, w));
                    }
                    if (ok && poly.Count >= 3) p.Sections[idx].Polygon = poly;
                }
            }
            string why;
            return p.Validate(out why) ? p : null;
        }

        private static bool TryD(string s, out double v)
        {
            v = 0.0;
            if (string.IsNullOrEmpty(s)) return false;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                   && !double.IsNaN(v) && !double.IsInfinity(v);
        }

        public static RecoveryModel ParseModel(string s, RecoveryModel fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            try { return (RecoveryModel)Enum.Parse(typeof(RecoveryModel), s.Trim(), true); }
            catch { return fallback; }
        }

        public static IntakeKind ParseKind(string s, IntakeKind fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            try { return (IntakeKind)Enum.Parse(typeof(IntakeKind), s.Trim(), true); }
            catch { return fallback; }
        }

        private static string F(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0.0;
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static double D(ConfigNode n, string key, double def)
        {
            string s = n.GetValue(key);
            if (string.IsNullOrEmpty(s)) return def;
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return def;
            if (double.IsNaN(v) || double.IsInfinity(v)) return def;
            return v;
        }

        /// <summary>
        /// 布尔键。**"0" 与 "false" 都要认**——我们写盘写的是 "1"/"0"，
        /// 但老存档里 KSP 自己写的 bool 是 "True"/"False"。只认一种的话，
        /// 另一种会被静默当成缺省，玩家改过的开关看起来像"没生效"。
        /// </summary>
        private static bool B(ConfigNode n, string key, bool def)
        {
            string s = n.GetValue(key);
            if (string.IsNullOrEmpty(s)) return def;
            s = s.Trim();
            if (s == "1") return true;
            if (s == "0") return false;
            bool v;
            if (bool.TryParse(s, out v)) return v;
            return def;
        }
    }
}