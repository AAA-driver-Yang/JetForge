using System;
using System.Collections.Generic;
using System.Globalization;

namespace JetForge.Core
{
    /// <summary>这台发动机是干什么用的。不是评分标签，只决定起手用哪台真机当模板、默认要不要加力。</summary>
    public enum WizardRole
    {
        Fighter = 0,      // 战斗机：要马赫也要加力
        Interceptor = 1,  // 截击机：马赫优先，航程次要
        Bomber = 2,       // 轰炸机：亚声速大推力，耐久优先
        Trainer = 3,      // 教练机：小而简单
        Transport = 4     // 运输机：亚声速、省油
    }

    /// <summary>向导的输入：玩家只填目标，不填部件参数。</summary>
    public sealed class WizardInput
    {
        public WizardRole Role = WizardRole.Fighter;
        public double DesignMach = 2.0;
        public double DesignAlt = 11000.0;
        /// <summary>设计点推力（kN），有加力时指加力推力。</summary>
        public double ThrustKN = 80.0;
        public string Tech = "1980s";
        /// <summary>留空（null）时按用途自动决定：战斗机/截击机要，运输机/教练机不要。</summary>
        public bool? Afterburner = null;
    }

    /// <summary>两次输入是不是同一个目标（同一份初稿还作不作数）。</summary>
    /// <remarks>
    /// 界面上"参数改过没有"原来是用 <c>Math.Abs(a-b) &gt; 1e-9</c> 判的，那是个坑：
    /// <c>GUILayout.HorizontalSlider</c> 把值量化到像素步长再返回，喂回去下一帧可能
    /// 差一个 ULP 甚至一个像素台阶，于是"参数变了"每帧都成立——初稿刚生成出来就被
    /// 自己清掉，玩家看到的是「还没有可以交的初稿」而屏幕中央还飘着「初稿好了」（§280）。
    /// 判据改成按**显示精度**比：马赫 0.001、高度 1 m、推力 0.05 kN，比这更小的差别
    /// 本来也印不到屏幕上，玩家也做不出这个决定。
    /// </remarks>
    public static class WizardTarget
    {
        public static bool SameTarget(WizardInput a, WizardInput b)
        {
            if (a == null || b == null) return false;
            if (Math.Abs(a.DesignMach - b.DesignMach) >= 1e-3) return false;
            if (Math.Abs(a.DesignAlt - b.DesignAlt) >= 1.0) return false;
            if (Math.Abs(a.ThrustKN - b.ThrustKN) >= 0.05) return false;
            if (a.Role != b.Role) return false;
            if (a.Tech != b.Tech) return false;
            return a.Afterburner == b.Afterburner;
        }
    }

    /// <summary>向导的输出：一台能直接编辑的设计，加上"每个初值是从哪来的"。</summary>
    public sealed class WizardResult
    {
        public bool Ok;
        /// <summary>失败原因（Ok=false 时非空）。</summary>
        public string Failure = "";
        public EngineDesignData Design;
        public string Basis = "";
        public double ThrustKN;
        public double TargetKN;
        /// <summary>每个初始参数的来源。</summary>
        public readonly List<string> Notes = new List<string>();
        /// <summary>这个组合先天的矛盾（不阻止生成，但要说清楚）。</summary>
        public readonly List<string> Conflicts = new List<string>();
    }

    /// <summary>
    /// 设计向导：从目标反推一台初稿。
    ///
    /// 做法很朴素，也不打算装聪明：**从一台标定过的真机起手，按"推力 ∝ 通流面积"整体缩放**，
    /// 再用设计点求解器把实际推力量回来，不达标就改缩放系数重解，二分到 1% 以内。
    /// 这样每个初值都能说出来源（"起手自 RD-33，按推力比放大 1.23 倍"），
    /// 而不是凭一张喜欢的数字表。缩放只动几何尺寸，不动压比、效率、温度这些经验参数——
    /// 那些是标定过的，动一个要把整台机重新标一遍。
    /// </summary>
    public static class DesignWizard
    {
        private const double MinThrustKN = 3.0;
        private const double MaxThrustKN = 400.0;
        private const double MinMach = 0.2;
        private const double MaxMach = 3.5;
        private const double MaxAlt = 25000.0;

        /// <summary>按用途与年代挑一台最接近的真机当模板。</summary>
        public static string PickBasis(WizardRole role, double mach, string tech)
        {
            int decade = DecadeOf(tech);
            switch (role)
            {
                case WizardRole.Transport:
                    return decade >= 1990 ? "GTF1100" : "J57";
                case WizardRole.Trainer:
                    return "J85";
                case WizardRole.Bomber:
                    return mach > 1.5 ? "J79" : "J57";
                case WizardRole.Interceptor:
                    return decade >= 1970 ? "RD-33" : "J79";
                default: // Fighter
                    if (mach > 1.8) return decade >= 1970 ? "RD-33" : "J79";
                    return decade >= 1980 ? "RD-33" : "J79";
            }
        }

        /// <summary>用途决定的默认加力开关。</summary>
        public static bool DefaultAfterburner(WizardRole role)
        {
            return role == WizardRole.Fighter || role == WizardRole.Interceptor
                || role == WizardRole.Bomber;
        }

        private static int DecadeOf(string tech)
        {
            if (string.IsNullOrEmpty(tech)) return 1970;
            string t = tech.Trim();
            int d;
            if (int.TryParse(t.Length >= 4 ? t.Substring(0, 4) : t, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out d)) return d;
            return 1970;
        }

        public static WizardResult Build(WizardInput input)
        {
            WizardResult r = new WizardResult();
            if (input == null) { r.Failure = Loc.T("没有输入", "No input"); return r; }
            r.TargetKN = input.ThrustKN;

            // ---- 1. 先查输入本身 ----
            if (!GasState.IsFinite(input.ThrustKN) || input.ThrustKN < MinThrustKN || input.ThrustKN > MaxThrustKN)
            {
                r.Failure = string.Format(Loc.T("目标推力 {0} kN 超出范围（{1:F0}~{2:F0} kN）", "Target thrust {0} kN is out of range ({1:F0}-{2:F0} kN)"), input.ThrustKN, MinThrustKN, MaxThrustKN);
                return r;
            }
            if (!GasState.IsFinite(input.DesignMach) || input.DesignMach < MinMach || input.DesignMach > MaxMach)
            {
                r.Failure = string.Format(Loc.T("设计马赫 {0} 超出范围（{1:F1}~{2:F1}）", "Design Mach {0} is out of range ({1:F1}-{2:F1})"), input.DesignMach, MinMach, MaxMach);
                return r;
            }
            if (!GasState.IsFinite(input.DesignAlt) || input.DesignAlt < 0.0 || input.DesignAlt > MaxAlt)
            {
                r.Failure = string.Format(Loc.T("设计高度 {0:F0} m 超出范围（0~{1:F0} m）", "Design altitude {0:F0} m is out of range (0-{1:F0} m)"), input.DesignAlt, MaxAlt);
                return r;
            }
            TechLevel tech = TechLevel.Get(input.Tech);
            if (tech == null)
            {
                r.Failure = string.Format(Loc.T("不认识的技术年代「{0}」", "Unknown technology era 「{0}」"), input.Tech);
                return r;
            }

            // ---- 2. 起手模板 ----
            string key = PickBasis(input.Role, input.DesignMach, input.Tech);
            EngineDesignData d = Presets.Build(key);
            if (d == null)
            {
                r.Failure = string.Format(Loc.T("找不到起手模板「{0}」", "No starting template for 「{0}」"), key);
                return r;
            }
            r.Basis = key;
            PresetInfo pi = Presets.Get(key);
            r.Notes.Add(string.Format(Loc.T("起手模板：{0}（{1}）", "Starting template: {0} ({1})"), pi != null ? pi.Label : key, pi != null ? pi.Note : ""));
            r.Notes.Add(Loc.T("为什么不用空白设计：压比、效率、涡轮前温度这些经验参数是逐台标定过的，只有尺寸可以随便缩", "Why not start from a blank design: pressure ratio, efficiency and turbine inlet temperature are calibrated per engine; only the dimensions scale freely."));

            // ---- 3. 摊开设计点与加力 ----
            d.DesignMach = input.DesignMach;
            d.DesignAlt = input.DesignAlt;
            d.TechName = tech.Name;
            bool ab = input.Afterburner.HasValue ? input.Afterburner.Value : DefaultAfterburner(input.Role);
            d.Afterburner.Enabled = ab;
            r.Notes.Add(string.Format(Loc.T("设计点：{0:F0} m / Ma{1:F2}，{2}", "Design point: {0:F0} m / Ma{1:F2}, {2}"),
                input.DesignAlt, input.DesignMach, ab ? Loc.T("开加力", "afterburner on") : Loc.T("干态", "dry")));
            r.Notes.Add(ab
                ? Loc.T("加力：按用途默认打开（战斗机不打开力等于白给）", "Afterburner: on by default for this role (a fighter without it is a waste of weight)")
                : Loc.T("加力：按用途默认关闭（运输/教练机装加力只是白白增重）", "Afterburner: off by default for this role (on a transport or trainer it is just dead weight)"));

            // ---- 4. 按推力缩放：推力 ∝ 通流面积，所以尺寸 ∝ √推力 ----
            double scale = 1.0;
            double abFrac = ab ? 1.0 : 0.0;
            OffDesignResult probe = SolveAt(d, abFrac);

            // 换了设计点之后，模板原来的级数可能撑不住新的落压比
            // （RD-33 的 1 级高压涡轮在 11 km/M2.0 上会被要求 3.1 的落压比，单级上限 2.4）。
            // 这不是错，是缺件：加一级、并把这个决定写进说明。向导不替玩家猜材料，
            // 但它可以把"级数不够"这件事说出来，而不是抛一个算不出来的设计。
            //
            // 还有一种是"巡航马赫下排气比来流还慢"：风扇压比 1.10 是静推标定值，
            // 拿它去 M0.8 巡航，涵道气流的速度比飞机还慢，那 6.8 倍涵道比全在拖后腿。
            // 真机风扇压比是跟着设计马赫涨的，所以这里也让它涨——每轮 +0.05，上限 1.85。
            for (int round = 0; round < 10 && (probe == null || !probe.Ok); round++)
            {
                if (!RepairOnce(probe, d, r)) break;
                probe = SolveAt(d, abFrac);
            }

            if (probe == null || !probe.Ok)
            {
                r.Failure = probe != null && probe.Errors.Count > 0
                    ? Loc.T("起手模板在这个设计点上算不出来：", "The template does not solve at this design point: ") + probe.Errors[0]
                    : Loc.T("起手模板在这个设计点上算不出来", "The template does not solve at this design point");
                return r;
            }
            double baseThrust = probe.NetThrust / 1000.0;
            if (baseThrust <= 0.0)
            {
                r.Failure = string.Format(Loc.T("起手模板在这个设计点上推力 {0:F1} kN（不是正数），没法和目标比", "The template gives {0:F1} kN at this design point (not positive), so there is nothing to compare with the target"), baseThrust);
                return r;
            }
            if (Math.Abs(baseThrust - input.ThrustKN) > input.ThrustKN * 0.005)
                scale = Math.Sqrt(input.ThrustKN / baseThrust);

            // 二分量回来：缩放没法完全按 √ 走（轮毂比、放气、管道损失都会吃掉一点）。
            double lo = scale * 0.55, hi = scale * 1.8;
            EngineDesignData best = null;
            double bestErr = double.MaxValue;
            for (int i = 0; i < 22; i++)
            {
                double s = 0.5 * (lo + hi);
                EngineDesignData trial = Scaled(d, s);
                OffDesignResult res = SolveAt(trial, abFrac);
                if (res == null || !res.Ok || res.NetThrust <= 0.0)
                {
                    // 缩过头会算不出来（流量太小、迭代不收敛）：把下限抬起来
                    lo = s;
                    continue;
                }
                double err = Math.Abs(res.NetThrust / 1000.0 - input.ThrustKN);
                if (err < bestErr) { bestErr = err; best = trial; scale = s; }
                if (err <= input.ThrustKN * 0.01) break;
                if (res.NetThrust / 1000.0 < input.ThrustKN) lo = s; else hi = s;
            }
            if (best == null)
            {
                r.Failure = Loc.T("按推力缩放时求解器一直算不出可用状态：目标推力可能超出了这个年代能做到的范围", "Scaling to the thrust target never reached a usable state: the target may exceed what this era can build.");
                return r;
            }
            d = best;

            // ---- 5. 量回来 ----
            OffDesignResult final = SolveAt(d, abFrac);
            if (final == null || !final.Ok)
            {
                r.Failure = Loc.T("缩放后的设计点算不出来：", "The scaled design point does not solve: ") + (final != null && final.Errors.Count > 0 ? final.Errors[0] : Loc.T("未知", "unknown"));
                return r;
            }
            r.ThrustKN = final.NetThrust / 1000.0;
            r.Design = d;
            double pct = 100.0 * (r.ThrustKN - input.ThrustKN) / input.ThrustKN;
            r.Notes.Add(string.Format(Loc.T("几何整体缩放 {0:F3} 倍（推力 ∝ 面积，先按 √ 估，再用求解器二分）", "Whole geometry scaled by {0:F3} (thrust goes with area: estimate with a square root first, then bisect on the solver)"), scale));
            r.Notes.Add(string.Format(Loc.T("风扇直径 {0:F2} m｜高压压气机叶尖 {1:F2} m｜进气捕获 {2:F2} m", "Fan dia {0:F2} m | HPC tip {1:F2} m | intake capture {2:F2} m"),
                d.Fan.Diameter, d.Hpc.TipDiameter, d.Intake.CaptureDiameter));
            r.Notes.Add(string.Format(Loc.T("实算推力 {0:F1} kN（目标 {1:F1} kN，差 {2:+0.0;-0.0;0.0}%）", "Solved thrust {0:F1} kN (target {1:F1} kN, off by {2:+0.0;-0.0;0.0}%)"),
                r.ThrustKN, input.ThrustKN, pct));
            r.Notes.Add(string.Format(Loc.T("涡前温度 {0:F0} K（上限 {1:F0} K）｜总压比 {2:F2}｜耗油率 {3:F4} kg/(N·h)", "Turbine inlet {0:F0} K (limit {1:F0} K) | overall pressure ratio {2:F2} | SFC {3:F4} kg/(N h)"),
                final.T4, tech.T4Limit, final.Opr, final.Sfc));

            // ---- 6. 这个组合先天的矛盾 ----
            if (pct > 5.0 || pct < -5.0)
                r.Conflicts.Add(string.Format(Loc.T("实算推力与目标差 {0:F1}%：缩放已经到头，这个年代的部件口径撑不到这个推力", "Thrust misses the target by {0:F1}%: the scaling ran out - parts of this era cannot be pushed further."), pct));
            if (input.DesignMach > 2.6 && DecadeOf(input.Tech) < 1970)
                r.Conflicts.Add(string.Format(Loc.T("{0} 的材料在 Ma{1:F1} 下扛不住进气总温：进气道恢复会掉到 {2:F2}，这不是 bug", "Materials of the {0} cannot take the inlet total temperature at Ma{1:F1}: recovery drops to {2:F2}. This is not a bug."),
                    input.Tech, input.DesignMach, Intake.Recovery(d.Intake, tech, input.DesignMach, d.DesignMach)));
            if (ab && input.DesignMach < 0.85)
                r.Conflicts.Add(Loc.T("亚声速机装加力：结构与油耗都要付出代价，只在短时爬升时划算", "An afterburner on a subsonic machine: structure and fuel burn both pay for it; only worth it for short climbs."));
            if (final.T4 > tech.T4Limit * 0.98)
                r.Conflicts.Add(string.Format(Loc.T("涡前温度 {0:F0} K 已经贴住上限 {1:F0} K：寿命与可靠性的账还没算进来", "Turbine inlet {0:F0} K sits on the {1:F0} K limit: life and reliability are not in the bill yet."),
                    final.T4, tech.T4Limit));
            if (final.Opr < 6.0 && input.ThrustKN > 60.0)
                r.Conflicts.Add(string.Format(Loc.T("总压比只有 {0:F2}：大推力配低压比，耗油率会很差", "Overall pressure ratio is only {0:F2}: big thrust on a low ratio burns a lot of fuel."), final.Opr));
            if (!d.HasFan && input.ThrustKN > 90.0)
                r.Conflicts.Add(Loc.T("涡喷在这条路线上已经到顶：再加推力只能堆尺寸，耗油率会继续恶化", "A turbojet has hit its ceiling on this path: more thrust now only means more size, and SFC keeps getting worse."));
            if (d.Fan.Diameter > 3.0)
                r.Conflicts.Add(string.Format(
                    Loc.T("风扇 Ø{0:F2} m 偏大：本模型在 Ma0.8 巡航上给的推力密度偏低（静推标定的几何直接飞马赫，涵道排气速度跟不上），", "Fan dia {0:F2} m is on the large side: this model gives low thrust density at Ma0.8 cruise (geometry calibrated on static thrust flown straight at Mach leaves the bypass exhaust too slow),") +
                    Loc.T("真实同级发动机的风扇要小一截。这个尺寸能飞，但别拿它当图纸", "real engines of the same class use a smaller fan. This size flies, but do not treat it as a drawing."), d.Fan.Diameter));

            r.Ok = true;
            return r;
        }

        private static OffDesignResult SolveAt(EngineDesignData d, double abFrac)
        {
            try
            {
                return OffDesign.Solve(d, d.DesignAlt, d.DesignMach, 1.0, abFrac, null, 80, -1.0, 0.0, 1.0, 0, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 求解器说"某某的落压比超过单级上限"时，加一级并返回 true。
        ///
        /// 这是向导唯一允许自己动部件参数的地方，理由很窄：**级数不是经验参数，是几何**，
        /// 加一级就是把流道拉长一截，代价自然记在长度与质量上，不会凭空变出性能。
        /// 压比、效率、温度这三类一个都不许动——那些是标定过的，改一个要把整台机重标一遍。
        /// </summary>
        private static bool NeedsMoreStages(OffDesignResult res, EngineDesignData d)
        {
            if (res == null || d == null) return false;
            for (int i = 0; i < res.Errors.Count; i++)
            {
                string e = res.Errors[i];
                if (e == null) continue;
                bool limit = e.Contains(Loc.T("极限", "limit")) || e.Contains(Loc.T("超过", "exceeds"));
                if (!limit) continue;
                if (e.Contains("HPT") && d.Hpt.Stages < 3) { d.Hpt.Stages++; return true; }
                if (e.Contains("IPT") && d.Ipt != null && d.Ipt.Stages < 3) { d.Ipt.Stages++; return true; }
                if (e.Contains("LPT") && d.Lpt.Stages < 6) { d.Lpt.Stages++; return true; }
                if (e.Contains("LPC") && d.Lpc.Stages < 6) { d.Lpc.Stages++; return true; }
                if (e.Contains("HPC") && d.Hpc.Stages < 14) { d.Hpc.Stages++; return true; }
            }
            return false;
        }

        /// <summary>一次修补：级数不够就加级；巡航马赫下排气比来流还慢就抬风扇压比。</summary>
        private static bool RepairOnce(OffDesignResult res, EngineDesignData d, WizardResult r)
        {
            if (res == null || d == null) return false;
            if (NeedsMoreStages(res, d))
            {
                r.Notes.Add(Loc.T("级数不够：换到新的设计点后原模板的落压比超了单级上限，加了一级（流道变长，代价记在长度与质量上）", "Not enough stages: at the new design point the template's pressure ratio broke the per-stage limit, so one stage was added (a longer duct, paid for in length and mass)"));
                return true;
            }
            for (int i = 0; i < res.Errors.Count; i++)
            {
                string e = res.Errors[i];
                if (e == null || !e.Contains(Loc.T("排气速度比来流还慢", "Exhaust is slower than the incoming air"))) continue;
                if(!d.HasFan || d.Fan.PressureRatio >= 1.85) continue;
                double before = d.Fan.PressureRatio;
                d.Fan.PressureRatio = Math.Min(1.85, Math.Max(1.12, before + 0.05));
                if (d.Fan.PressureRatio > before + 1e-6)
                {
                    r.Notes.Add(string.Format(
                        Loc.T("风扇压比 {0:F2} → {1:F2}：Ma{2:F2} 巡航要让涵道排气快过来流，静态标定值在这个马赫上只会拖后腿", "Fan pressure ratio {0:F2} -> {1:F2}: at Ma{2:F2} cruise the bypass exhaust has to beat the incoming flow; the static calibration only drags here."),
                        before, d.Fan.PressureRatio, d.DesignMach));
                    return true;
                }
            }
            return false;
        }

        /// <summary>整体几何缩放：只动尺寸，不动压比/效率/温度。</summary>
        public static EngineDesignData Scaled(EngineDesignData src, double s)
        {
            EngineDesignData d = src.Clone();
            ApplyScale(d, s);
            return d;
        }

        private static void ApplyScale(EngineDesignData d, double s)
        {
            d.Fan.Diameter *= s;
            d.Lpc.TipDiameter *= s;
            d.Hpc.TipDiameter *= s;
            d.Hpt.TipDiameter *= s;
            d.Lpt.TipDiameter *= s;
            if (d.Ipt != null) d.Ipt.TipDiameter *= s;
            if (d.Intake.CaptureDiameter > 0.0) d.Intake.CaptureDiameter *= s;
            d.LengthAuto = true;
        }
    }
}