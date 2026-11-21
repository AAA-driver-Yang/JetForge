using System;

namespace JetForge.Core
{
    /// <summary>
    /// 喷口面积随加力档位怎么走。
    ///
    /// 真机的加力不是"往涡轮后面多喷油"就完了：加力一开，涡轮后总温从 900 K 级升到
    /// 1800~2000 K，同样的喉道能过的流量是原来的 1.3~1.5 倍。所以带加力的发动机
    /// 必须能把喷口撑开——F100 的 A8 从干态到满加力大约放大 25~40%。
    /// 固定收敛喷管没有这个能力，加力只能靠"憋着"烧，推力上不去，
    /// 这就是我们一直报的那句"固定收敛喷管憋住了"。
    ///
    /// 这里给的是一条调度曲线：A8(加力档位) = A8_干态 · (1 + k·档位)，
    /// k 取 0.24（真机 0.2~0.35 的中间偏保守）。可变与收扩喷管都按这条走；
    /// 固定收敛喷管折不折——它只能保持原面积，物理上就是憋住。
    /// </summary>
    public static class NozzleSchedule
    {
        /// <summary>满加力时喉道面积的增量。
        /// 物理线：同样的落压比下能过的流量 ∝ 1/sqrt(T7)，干态 T7≈1085 K、满加力≈2000 K，
        /// 所以要过同样的流量，喉道得开 sqrt(2000/1085) ≈ 1.36 倍——也就是 +36%。
        /// 真机 F100 的 A8 从干态到满加力放大 25~40%，正落在这个值附近。</summary>
        public const double AbAreaGain = 0.36;

        /// <summary>
        /// 这一档控制系统的 A8 闭环权重：0 = 纯开环（杆到哪儿口到哪儿），1 = 纯闭环（按 T7 需求走）。
        ///
        /// 真机上这两件事差得很远：
        /// - 机械/液压档：飞行员扳加力杆，杆通过钢索/液压直接拉作动筒，**没人管涡轮后温度**。
        ///   所以喷口位置只认档位。点火失败、油气比没跟上，喷口照样张着——推力白丢，
        ///   飞行员能从仪表上看出来（T7 不涨、落压比掉），但机器自己不会纠正。
        /// - 数字 FADEC：A8 是**调度**出来的，控制律里就有"喉道能力要装得下这股气"，
        ///   实际按 T7 走。点火失败时它会把喷口收回去。
        /// - 模拟 FADEC：有温度补偿但没闭环，取一半。
        ///
        /// 这条权重决定了"喷口开得比需要的大"这个警告会不会频繁出现：
        /// 开环档在高空/低油气比下确实该报，闭环档不该报。
        /// </summary>
        public static double ClosedLoopWeight(ControlKind control)
        {
            int rk = ControlSystem.Rank(control);
            if (rk >= ControlSystem.Rank(ControlKind.Fadec)) return 1.0;
            if (rk >= ControlSystem.Rank(ControlKind.Analog)) return 0.5;
            return 0.0;
        }

        /// <summary>
        /// 实际装上去的喉道面积：开环那一份（作动筒位置）与闭环那一份（T7 需求）按档次掺。
        ///
        /// 注意这是**面积**，不是需求。`ThroatAreaNeed` 仍然单独报出来供判据比较——
        /// 掺过之后两者只差"控制器的偏差"，正好是 A8OpenRatio 该量的东西。
        /// </summary>
        public static double ThroatAreaInstalled(EngineDesignData d, DesignConstants c,
            double abFrac, double nc, double t7, ControlKind control)
        {
            double pos = ThroatArea(d, c, abFrac, nc);
            double w = ClosedLoopWeight(control);
            if (w <= 0.0) return pos;
            double need = ThroatAreaNeed(d, c, nc, t7);
            if (need <= 0.0 || pos <= 0.0) return pos;
            if (w >= 1.0) return need;
            return pos * (1.0 - w) + need * w;
        }

        public static bool CanVary(NozzleKind kind)
        {
            return kind == NozzleKind.Variable || kind == NozzleKind.ConvergingDiverging;
        }

        /// <summary>当前加力档位下的喉道面积（按设计转速算）。</summary>
        public static double ThroatArea(EngineDesignData d, DesignConstants c, double abFrac)
        {
            return ThroatArea(d, c, abFrac, 1.0);
        }

        /// <summary>干态（或当前）喉道面积：加力那一份 + 转速那一份。
        /// 加力那一份是"要把 1800 K 的燃气排出去"，转速那一份是"低转速时少一点气，
        /// 把它收回来才能维持压比"——两件事，别合成一个系数。</summary>
        public static double ThroatArea(EngineDesignData d, DesignConstants c, double abFrac, double nc)
        {
            if (d == null || c == null) return 0.0;
            double f = abFrac;
            if (f < 0.0) f = 0.0;
            if (f > 1.0) f = 1.0;
            bool vary = CanVary(d.Nozzle);
            if (!vary) f = 0.0;
            return c.A8 * DryFactor(nc, vary) * (1.0 + AbAreaGain * f);
        }

        /// <summary>知道涡轮后总温时的喉道面积：加力那一份按温度给，不按档位硬撑。
        ///
        /// 原来的写法是"档位到 1 就把喉道撑到 1.36 倍"，那个 1.36 本身就是从
        /// `√(2000/1085)` 抄来的——只不过它假设了"满档位 = 2000 K"。实际油气比
        /// 0.030 时 T7 只从 1085 涨到 1111 K，喉道却按 1.36 撑开，容量凭空多三成，
        /// 喷管落进亚声速，`(Pe-pAmb)·Ae` 那一项白丢（§157 就是这件事）。
        /// 现在直接比温度：A8 = A8_干 · √(T7/T7干)，满加力仍回到 1.36，部分加力自动插值。
        ///
        /// 比干态还冷时不往回缩：干态那一份已经含了转速项，这里只负责"加热"那部分。
        /// </summary>
        /// <summary>这股气"需要"多大的喉道：A8_干 · 转速项 · √(T7/T7干)。
        ///
        /// 注意它是**需求**，不是装上去的面积。装上去的面积由作动筒位置决定
        /// （`ThroatArea` 那条 1+增益·档位），两者差得多就说明喷口开得不对：
        /// 开大了落压比掉、压力推力白丢，开小了喉道过不去这股气。
        /// 上一版把需求当成了面积本身（§158），于是作动筒位置被乘进一个越来越小的
        /// 增益里，档位旋钮在求解器里几乎不留痕迹——位置与需求是两件事，不能互相顶替。
        /// </summary>
        public static double ThroatAreaNeed(EngineDesignData d, DesignConstants c, double nc, double t7)
        {
            if (d == null || c == null || !CanVary(d.Nozzle)) return 0.0;
            if (c.T7D <= 0.0 || t7 <= 0.0 || double.IsNaN(t7) || double.IsInfinity(t7))
                return c.A8 * DryFactor(nc, true);
            double gain = Math.Sqrt(t7 / c.T7D);
            if (gain < 1.0) gain = 1.0;
            double maxGain = 1.0 + AbAreaGain * 1.15;
            if (gain > maxGain) gain = maxGain;
            return c.A8 * DryFactor(nc, true) * gain;
        }

        /// <summary>低转速时把喷口收一点（只对能动的喷管）。1.0 = 设计转速，0.94 = 慢车。
        /// 固定收敛喷管恒 1.0：它没有作动筒，收不了也放不了，这一点必须如实写出来，
        /// 否则视觉上"慢车时喷口变小"会被当成真的。</summary>
        public static double DryFactor(double nc, bool canVary)
        {
            if (!canVary) return 1.0;
            double n = nc;
            if (double.IsNaN(n) || n < 0.0) return 1.0;
            if (n > 1.0) n = 1.0;
            return DryIdleFactor + (1.0 - DryIdleFactor) * n;
        }

        /// <summary>慢车转速下的喉道收缩比。真机慢车 A8 比设计点小 5~8%。</summary>
        public const double DryIdleFactor = 0.94;

        // 这里曾有一个 `AreaRatio(d, c, abFrac)`：签名带 `abFrac` 却完全不看它，
// 永远返回 `max(NozzleAreaRatio, 1)`。名字像"随加力变的面积比"，实际是个常量 getter，
// 谁调谁误会，已删。要看面积比直接读 `DesignConstants.NozzleAreaRatio`。

        /// <summary>给判据/状态栏用的一句话。</summary>
        public static string Describe(EngineDesignData d)
        {
            if (d == null) return "";
            if (!CanVary(d.Nozzle)) return Loc.T("固定收敛喷管：加力时喷口不能放大，推力会被憋掉一部分", "Fixed converging nozzle: it cannot open up with the afterburner lit, so some thrust is choked away");
            return string.Format(Loc.T("可变喷口：满加力时喉道放大约 {0:P0}", "Variable nozzle: at full afterburner the throat opens about {0:P0}"), AbAreaGain);
        }
    }
}