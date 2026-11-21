using System;
using System.Collections.Generic;
using System.Linq;

namespace JetForge.Core
{
    /// <summary>
    /// 喷管判据：压比、几何配合、花瓣。
    ///
    /// 从 `Criteria.cs` 拆出来的：判据一共十七组，全挤在一个文件里时
    /// 温度在哪、轴在哪、堵塞在哪只能靠滚；拆开之后一族一个文件，
    /// 加一条判据只需要看那一族的前后文。
    /// </summary>
    public static partial class Criteria
    {
        public static List<Finding> CheckNozzle(ResultView v, EngineDesignData design, TechLevel tech)
        {
            List<Finding> o = new List<Finding>();
            double p0 = v.Get("p_amb"), p5 = v.Get("P5");
            if (p0 > 0.0 && p5 > 0.0)
            {
                double npr = p5 / p0;
                if (npr < 1.0)
                {
                    o.Add(new Finding("nozzle.reverse", Level.Fail,
                        string.Format(Loc.T("核心喷管压比 {0:F2} < 1：出口倒流", "Core nozzle pressure ratio {0:F2} below 1: the exit flows backwards"), npr), npr, 1.0, "-"));
                }
                else
                {
                    double lim = design.Nozzle == NozzleKind.Converging ? tech.NozzleNprFixedMax : 1e9;
                    string lvl = npr > lim ? Level.Warn : Level.Ok;
                    string msg = string.Format(Loc.T("核心喷管压比 {0:F2}", "Core nozzle pressure ratio {0:F2}"), npr);
                    if (lvl == Level.Warn)
                        msg += string.Format(Loc.T("（固定收敛喷管超过 {0:F0} 后欠膨胀损失明显，需要可变面积）", "(a fixed converging nozzle loses a lot past {0:F0}: it needs variable area)"), lim);
                    o.Add(new Finding("nozzle.npr", lvl, msg, npr, lim, "-"));
                }
            }
            return o;
        }

        /// <summary>喷管与加力的匹配：加力会同时抬流量与总温，固定收敛喉道最先撑不住。</summary>
        public static List<Finding> CheckNozzleGeometry(EngineDesignData d, ResultView v)
        {
            List<Finding> o = new List<Finding>();
            if (d == null) return o;

            double farAb = v.Get("far_ab");
            bool abOn = d.Afterburner.Enabled || farAb > 1e-6;
            double ar = v.Get("nozzle_arearatio");
            double me = v.Get("nozzle_mache");

            if (d.Nozzle == NozzleKind.ConvergingDiverging)
            {
                if (v.Get("nozzle_separated") > 0.5)
                    o.Add(new Finding("nozzle.separated", Level.Warn,
                        string.Format(Loc.T("收扩喷管面积比 {0:F2} 在当前压比下过膨胀，喷流会在管内分离（真实表现为抖动与推力损失）", "C-D nozzle area ratio {0:F2} is over-expanded here and the jet separates inside the nozzle (shudder and thrust loss in reality)"), ar),
                        ar));
                else
                    o.Add(new Finding("nozzle.cd", Level.Ok,
                        string.Format(Loc.T("收扩喷管面积比 {0:F2}，出口马赫 {1:F2}", "C-D nozzle area ratio {0:F2}, exit Mach {1:F2}"), ar, me), ar));
            }

            if (v.Get("nozzle_backpressure") > 0.5)
                o.Add(new Finding("nozzle.backpressure", Level.Warn,
                    Loc.T("固定收敛喷管喉道吃不下当前流量：真实加力推力会低于本模型读数", "A fixed converging throat cannot pass this flow: real afterburner thrust would be lower than this reading"), v.Get("mdot_core")));

            // 喷管容量进了求解器之后，症状从"憋住"变成"流量被压下来"。
            // 这比憋住更狠：同样的油门，推力反而比干态低——真机上这就是"加力配了固定喷口"的下场。
            //
            // 注意这里必须自己算一次加力工况：设计点本身是干态的，永远撞不上这个限制，
            // 只查 v 的话这条判据就是个摆设（本轮先写成查 v，差点又交一条永不通过的判据）。
            if (d.Afterburner.Enabled)
            {
                try
                {
                    double abFrac = 1.0;
                    OffDesignResult abr = OffDesign.Solve(d, d.DesignAlt, d.DesignMach, 1.0, abFrac);
                    if (abr != null && abr.Ok && abr.NozzleCapped)
                        o.Add(new Finding("nozzle.capacity", Level.Warn,
                            string.Format(Loc.T("满加力时排气喷管吃不下这么多流量（{0}）：求解器把进气量压到 {1:F1} kg/s 重解，这一点加力推力因此低于干态。真机靠液压把 A8 撑开 20~35%，固定收敛喷口做不到", "Full afterburner asks for more flow than the nozzle can pass ({0}): the solver re-ran with intake flow capped at {1:F1} kg/s, so this afterburner thrust sits below dry. A real engine opens A8 by 20-35% with hydraulics; a fixed converging nozzle cannot"),
                                d.Nozzle == NozzleKind.Converging ? Loc.T("固定收敛", "fixed converging") : Loc.T("可变面积", "variable area"),
                                abr.MdotAir),
                            abr.MdotAir));
                }
                // 探测豁免：满加力复算失败时宁可不给这一条判据，也不让整个判据面板崩掉；
                // 复算本身的失败已经在 OffDesign 的告警里说过一次了。
                catch (Exception) { /* 判据不该让窗口崩 */ }
            }

            if (abOn && d.Nozzle == NozzleKind.Converging && v.Get("nozzle_backpressure") < 0.5)
                o.Add(new Finding("nozzle.ab_fixed", Level.Warn,
                    Loc.T("加力配固定收敛喷管：干态没问题，加力段推力被喉道上限压着，通常应改成可变面积或收扩", "Afterburner on a fixed converging nozzle: fine dry, but the throat caps afterburner thrust; normally you switch to variable area or a converging-diverging nozzle")));

            o.AddRange(CheckVectoring(d));

            return o;
        }

        /// <summary>
        /// 花瓣几何本身合不合理。它不改喉道面积，所以不算气动，只算机构：
        /// 片少了叶片之间的缝漏气（真机那圈缝是冷气膜，靠喷口压力差压住的），
        /// 片多了每片太窄，薄板在压差下会颤振；叶片太长则根部弯矩与重量都撑不住。
        /// </summary>
        private static List<Finding> CheckPetals(EngineDesignData d)
        {
            List<Finding> o = new List<Finding>();
            if (d == null) return o;
            if (d.Nozzle == NozzleKind.Converging)
            {
                o.Add(new Finding("nozzle.petals", Level.Ok, Loc.T("固定收敛喷管：没有花瓣", "Fixed converging nozzle: no petals")));
                return o;
            }
            int n = NozzleVisual.CountOf(d);
            // 先说机构上真正的毛病，再说"你填的数被我改了"——顺序决定玩家先看到什么，
            // 而"只有 8 片"比"你填 6 我做 8"严重得多。
            if (n < 12)
                o.Add(new Finding("nozzle.petals", Level.Warn,
                    d.PetalCount > 0 && n != d.PetalCount
                        ? string.Format(Loc.T("填了 {0} 片，按 {1} 片做（8~32 之间取偶）：这么少每片缝大得能看穿，真机至少 12 片", "Asked for {0} petals, built {1} (even, 8 to 32): that few leaves gaps you can see through; real engines use at least 12"), d.PetalCount, n)
                        : string.Format(Loc.T("只有 {0} 片：每片缝大得能看穿，真机至少 12 片", "Only {0} petals: the gaps are see-through; real engines use at least 12"), n)));
            else if (d.PetalCount > 0 && n != d.PetalCount)
                o.Add(new Finding("nozzle.petals", Level.Ok,
                    string.Format(Loc.T("花瓣数填 {0}，实际按 {1} 片做（8~32 之间取偶：奇数片会让对面的缝错位）", "Petal count {0} becomes {1} (even, 8 to 32: an odd count misaligns the opposite gaps)"), d.PetalCount, n)));
            double f = d.PetalLengthFrac > 0.0 ? d.PetalLengthFrac : 1.0;
            if (f > 1.5)
                o.Add(new Finding("nozzle.petals", Level.Warn,
                    string.Format(Loc.T("叶片长到 {0:F2}×基准：根部弯矩与颤振风险都上来了，真机很少超过 1.5×", "Petals at {0:F2} times baseline: root bending and flutter risk climb together, real engines rarely pass 1.5x"), f)));
            else if (f < 0.7)
                o.Add(new Finding("nozzle.petals", Level.Warn,
                    string.Format(Loc.T("叶片只有 {0:F2}×基准：撑不开足够的出口面积，加力全开时喉道与出口挤在一起", "Petals only {0:F2} times baseline: not enough exit area, so at full afterburner throat and exit crowd together"), f)));
            if (o.Count == 0)
                o.Add(new Finding("nozzle.petals", Level.Ok,
                    string.Format(Loc.T("{0} 片 × {1:F2}×基准长度：机构上没问题", "{0} petals at {1:F2} times baseline length: mechanically fine"), n, f)));
            return o;
        }
    }
}
