using System;
using System.Collections.Generic;
using System.Linq;

namespace JetForge.Core
{
    /// <summary>
    /// 进气道与整体流路判据：迎角假设、溢流、风车、气路走向。
    ///
    /// 从 `Criteria.cs` 拆出来的：判据一共十七组，全挤在一个文件里时
    /// 温度在哪、轴在哪、堵塞在哪只能靠滚；拆开之后一族一个文件，
    /// 加一条判据只需要看那一族的前后文。
    /// </summary>
    public static partial class Criteria
    {
        /// <summary>
        /// 进气道：型式与设计点配不配、喉道比在不在合理区、抽气抽了多少。
        ///
        /// 只报警，不改数——"这台机器的进气道不合理"是设计判断，不是解算错误。
        /// 真正的损失已经通过 `Intake.Recovery()` 进了循环。
        /// </summary>
        public static List<Finding> CheckIntake(EngineDesignData d)
        {
            List<Finding> o = new List<Finding>();
            if (d == null) return o;
            IntakeSpec sp = d.Intake != null ? d.Intake.Resolved() : new IntakeSpec().Resolved();
            TechLevel tech = d.Tech ?? new TechLevel();

            double md = d.DesignMach;
            double tprD = Intake.Recovery(sp, tech, md);
            double tprSub = Intake.Recovery(sp, tech, 0.9);

            string warn = Intake.Mismatch(d, sp);
            if (!string.IsNullOrEmpty(warn))
                o.Add(new Finding("intake.kind", Level.Warn, warn));
            else
                o.Add(new Finding("intake.kind", Level.Ok, string.Format(
                    Loc.T("{0}进气道：Ma {1:F2} 设计点恢复 {2:F3}（亚声速段 {3:F3}）", "{0} intake: recovery {2:F3} at the Ma {1:F2} design point (subsonic {3:F3})"),
                    IntakeSpec.KindName(sp.Kind), md, tprD, tprSub)));

            double throat = Intake.ThroatAreaOf(d);
            double capture = Intake.CaptureAreaOf(d);
            o.Add(new Finding("intake.area", Level.Ok, string.Format(
                Loc.T("喉道 ⌀{0:F2} m（{1:F3} m²）｜捕捉 ⌀{2:F2} m｜喉道/捕获 {3:F2}", "Throat dia {0:F2} m ({1:F3} m2), capture dia {2:F2} m, throat/capture {3:F2}"),
                Math.Sqrt(4.0 * throat / Math.PI), throat,
                Math.Sqrt(4.0 * capture / Math.PI), sp.ThroatRatio)));

            if (sp.BleedFrac > 0.0)
            {
                string lv = sp.BleedFrac > 0.10 ? Level.Warn : Level.Ok;
                o.Add(new Finding("intake.bleed", lv, string.Format(
                    Loc.T("抽气 {0:P0}：这部分气不参与做功，进气道喘振裕度会好一些（本模型只折流量、不给裕度好处）", "Bleed {0:P0}: that air does no work, and a real intake gains surge margin from it (this model only discounts the flow)"),
                    sp.BleedFrac)));
            }

            // 可调压缩面：这是"多了一个能在飞行中动的机构"，两头都要说清楚——
            // 一头是它真能收窄喉道（性能收益），一头是它要付作动系统的重量与钱，
            // 而这两样在任何按年代配的技术门槛下都不是免费的（§165）。
            if (sp.VariableGeometry)
            {
                int kindOk = sp.Kind == IntakeKind.Ramp || sp.Kind == IntakeKind.Cone ? 1 : 0;
                bool eraOk = d != null && d.Tech != null && d.Tech.Year >= 1960.0;
                if (kindOk == 0)
                {
                    o.Add(new Finding("intake.vg", Level.Warn,
                        Loc.T("可调几何只对二维斜板与锥体有意义：皮托与轴对称没有可动压缩面，这个开关现在不起作用", "Variable geometry only means something for a 2D ramp or a cone: a pitot or an axisymmetric intake has no moving compression surface, so this switch does nothing")));
                }
                else
                {
                    o.Add(new Finding("intake.vg", eraOk ? Level.Ok : Level.Warn, eraOk
                        ? string.Format(
                            Loc.T("可调压缩面：Ma {0:F2} 起收窄喉道，全行程收 {1:P0}；作动系统按捕获面积计入重量与造价", "Variable ramp: the throat narrows from Ma {0:F2}, {1:P0} over the full travel; actuators are counted in mass and cost by capture area"),
                            Intake.VariableGeometryOn, Intake.VariableGeometryThroatLoss)
                        : string.Format(
                            Loc.T("{0} 造不出可调压缩面：这个年头只有固定式进气口，飞行中动的机构要液压与作动筒，重量与工艺都不到。设计里留着它不会报错，但别指望那个年代真有这本事", "{0} could not build a variable ramp: that era had fixed intakes only, and a mechanism that moves in flight needs hydraulics and actuators that mass and tooling could not deliver. Leaving it in raises no error, but do not expect that era to have it"),
                            d != null && d.Tech != null ? d.Tech.Name : Loc.T("这个年代", "this era"))));
                }
            }
            return o;
        }

        /// <summary>
        /// 设计马赫落在超声速时的诚实提醒。
        /// 流量算的是 A·f(面马赫=0.5)·总压/√总温，等价于"换算流量恒定"，
        /// 也就是假定进气道能把来流减速到面马赫 0.5。这个假定对亚声速进气道成立，
        /// 对超声速进气道就不成立了——真实进气道受捕获面积与喉道限制，
        /// 而且低空超声速时总压很高，模型会把流量算成静压口径的好几倍。
        /// 进气道造型是后期的事，这里只把假设的边界说出来，不改数。
        /// </summary>
        public static List<Finding> CheckInletAssumption(EngineDesignData design)
        {
            List<Finding> o = new List<Finding>();
            double m = design != null ? design.DesignMach : 0.0;
            if (m > 1.2)
            {
                o.Add(new Finding("inlet.supersonic", Level.Warn, string.Format(
                    Loc.T("设计马赫 {0:F2} 属于超声速：本模型按面马赫固定 0.5 算流量，只适用于亚声速进气道，", "Design Mach {0:F2} is supersonic: this model fixes face Mach at 0.5 to get flow, which only holds for subsonic intakes,") +
                    Loc.T("超声速时进气道捕获面积与喉道会限制进气，这一点的流量和推力偏乐观", "at supersonic speed capture area and throat limit the flow, so this flow and thrust read optimistic"), m)));
            }
            else if (design != null && design.DesignAlt > 0.0 && m > 0.0)
            {
                o.Add(new Finding("inlet.subsonic", Level.Ok,
                    string.Format(Loc.T("设计点马赫 {0:F2}，面马赫固定 0.5 的换算流量假设成立", "Design Mach {0:F2}: the fixed 0.5 face Mach flow assumption holds"), m)));
            }
            return o;
        }

        /// <summary>
        /// 沿流路逐部件检查转子：风扇 → LPC → HPC。
        /// 站位优先用求解器给的逐级数据（StageRow.TIn/PIn），那才是真实站位；
        /// 只有非设计点这种没有逐级记录的结果才退回 fpr/opr 反推。
        /// </summary>
        public static List<Finding> CheckFlowPath(EngineDesignData design, ResultView v, TechLevel tech)
        {
            List<Finding> o = new List<Finding>();
            Dictionary<int, double> rpm = new Dictionary<int, double>();
            double mdotCore = v.Get("mdot_core"), mdotAir = v.Get("mdot_air");
            double fpr = v.Get("fpr") > 0.0 ? v.Get("fpr") : 1.0;
            double opr = v.Get("opr") > 0.0 ? v.Get("opr") : 1.0;
            double t1 = v.Get("T1") > 0.0 ? v.Get("T1") : 288.0;
            double p3 = v.Get("P3");
            double pt1 = p3 > 0.0 ? p3 / Math.Max(opr, 1e-6) : 0.0;

            Dictionary<string, List<StageRow>> byPart = new Dictionary<string, List<StageRow>>();
            foreach (StageRow row in v.Stages)
            {
                if (!byPart.ContainsKey(row.Part)) byPart[row.Part] = new List<StageRow>();
                byPart[row.Part].Add(row);
            }
            // 转速从叶尖速度反推不便，直接用判据需要的 N%（设计点全是 100）
            Func<int, double> nOf = s =>
            {
                double n; return v.Npct.TryGetValue(s, out n) ? n : 100.0;
            };

            if (design.HasFan && design.Fan.Diameter > 0.0)
            {
                // 风扇叶尖速度：用设计转速 × 当前 N%
                double fanRpm = DesignRpm(design, design.Fan.Shaft) * (nOf(design.Fan.Shaft) / 100.0);
                double u = Math.PI * design.Fan.Diameter * fanRpm / 60.0;
                o.AddRange(CheckRotor(Loc.T("风扇", "fan"), mdotAir, t1, pt1, design.Fan.Diameter,
                    design.Fan.HubTipRatio, u, nOf(design.Fan.Shaft), tech, "fan"));
            }

            int nl = Math.Max(design.Lpc.Stages, 0), nh = Math.Max(design.Hpc.Stages, 0);
            double corePr = Math.Max(opr / Math.Max(fpr, 1e-6), 1.0);
            double share = (nl + nh) > 0 ? nl / (double)(nl + nh) : 0.0;

            CompressorSpec[] specs = { design.Lpc, design.Hpc };
            string[] names = { "LPC", "HPC" };
            for (int k = 0; k < 2; k++)
            {
                CompressorSpec spec = specs[k];
                string part = names[k];
                if (spec.Stages <= 0) continue;
                double tIn, pIn;
                List<StageRow> rows;
                bool hasRows = byPart.TryGetValue(part, out rows) && rows.Count > 0;
                if (hasRows)
                {
                    tIn = rows[0].TIn; pIn = rows[0].PIn;
                }
                else if (k == 0)
                {
                    tIn = v.Get("T2") > 0.0 ? v.Get("T2") : t1;
                    pIn = pt1 * fpr;
                }
                else
                {
                    tIn = (v.Get("T3") > 0.0 ? v.Get("T3") : t1) * 0.85;
                    pIn = pt1 * fpr * Math.Pow(corePr, share);
                }
                double rpmSpec = DesignRpm(design, spec.Shaft) * (nOf(spec.Shaft) / 100.0);
                double u = Math.PI * spec.TipDiameter * rpmSpec / 60.0;
                o.AddRange(CheckRotor(part, mdotCore, tIn, pIn, spec.TipDiameter,
                    spec.HubTipRatio, u, nOf(spec.Shaft), tech));
            }
            return o;
        }

        /// <summary>风车：停车状态下只报整流阻力量级。</summary>
        public static List<Finding> CheckWindmill(ResultView v)
        {
            List<Finding> o = new List<Finding>();
            if (!v.Num.ContainsKey("windmill") || v.Get("windmill") < 0.5) return o;
            double f = v.Get("thrust");
            o.Add(new Finding("windmill", Level.Ok,
                string.Format(Loc.T("停车风车：{0:F1} kN（负值即阻力）", "Windmilling at cutoff: {0:F1} kN (negative is drag)"), f / 1000.0), f / 1000.0));
            return o;
        }
    }
}
