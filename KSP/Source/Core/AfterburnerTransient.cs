using System;

namespace JetForge.Core
{
    /// <summary>加力段这 1~2 秒里走到了哪一步。</summary>
    public enum AbPhase
    {
        /// <summary>没开，或者已经熄干净。</summary>
        Off,
        /// <summary>开关亮了、油气也在建立，但推力还没到位。</summary>
        Lighting,
        /// <summary>火建立完成，完整加力推力。</summary>
        Lit,
        /// <summary>开关关了，余焰在退。</summary>
        Extinguishing
    }

    /// <summary>加力瞬态的跨帧状态。模块每帧传同一个实例进来。</summary>
    public sealed class AbState
    {
        /// <summary>0 = 纯干态，1 = 完整加力。</summary>
        public double Frac;

        /// <summary>
        /// 喉道面积这一份走到了哪（0~1）。和 <see cref="Frac"/> 是两个量：
        /// 燃油建立是泵的事（1.3 s 那条曲线），A8 撑开是作动筒的事，慢得多。
        /// 中间那一段"油到了、喷口还没开"才是真机会经历的：涡轮后压力憋高，
        /// 推力先冲一下再回落，EGT 也会短暂偏高。
        /// </summary>
        public double AreaFrac;

        public AbPhase Phase = AbPhase.Off;
    }

    /// <summary>
    /// 加力点火不是开关：真机从按下到推力到位要一到两秒（喷口先放大、燃油建立、点火、
    /// 火焰充满加力段），关掉以后余焰还要烧一会儿。这一段的"半开着"在推力表上看不出来，
    /// 但玩家在座舱里能感觉到——一架加速中的飞机按下加力，推力是爬上去的。
    ///
    /// 这里不建模火焰传播，只给一条线性建立曲线：真机 F100 大约 1.5 s 到满加力，
    /// 熄火 0.5 s 上下。数字取这个量级，够玩家感觉到差异，也不会拖成两秒钟的假动作。
    /// </summary>
    public static class AfterburnerTransient
    {
        /// <summary>从点火命令到推力到位。真机 1~2 s，取中间。</summary>
        public const double LightSeconds = 1.3;

        /// <summary>关掉以后余焰退干净的时间。</summary>
        public const double ExtinguishSeconds = 0.6;

        /// <summary>点了火但点不着（贫油/富油/压力不够）时，残余推力退掉的时间。</summary>
        public const double FailSeconds = 0.4;

        /// <summary>
        /// 喉道作动筒撑开/收回 A8 的时间常数。<see cref="LightSeconds"/> 说的是燃油，
        /// 这个是喷口：真机液压作动筒全行程 1.5~2.5 s，取 2.2 s——比燃油明显慢。
        ///
        /// 这个不对称是有后果的，也是它存在的理由：加力刚点亮那一段，油已经烧起来了
        /// 而喷口还没撑开，涡轮后压力憋高、EGT 偏高，推力爬升比燃油曲线慢一截，
        /// 等 A8 追上才到稳态。两边取同一个时间常数就等于把这段抹掉了。
        /// </summary>
        public const double AreaSeconds = 2.2;

        /// <summary>
        /// 推进一步。<paramref name="target"/> 是想要的加力比例（0~1，AJE 式油门过线就按比例给），
        /// <paramref name="canLight"/> 是点火边界（<see cref="AfterburnerLight"/>）说这一状态能不能烧。
        /// 开关亮着但点不着时，残余的加力推力要退掉——不能让"点不着"变成"半加力"。
        ///
        /// 燃油与喷口面积是两个速率，各自走各自的：燃油按 <see cref="LightSeconds"/>，
        /// A8 按 <see cref="AreaSeconds"/> 追着燃油走。
        /// </summary>
        public static void Step(AbState st, double dt, double target, bool canLight)
        {
            StepFrac(st, dt, target, canLight);
            if (st == null) return;
            if (dt <= 0.0) { st.AreaFrac = st.Frac; return; }
            if (dt > 1.0) dt = 1.0;
            // 面积只追着"已经烧起来的比例"走，不直接看命令：燃油还没建立时喷口不该先张。
            double target2 = st.Frac;
            if (st.AreaFrac < target2)
            {
                st.AreaFrac += dt / AreaSeconds;
                if (st.AreaFrac > target2) st.AreaFrac = target2;
            }
            else if (st.AreaFrac > target2)
            {
                // 收的时候也慢，但没往下慢那么多：关加力时燃油先断、喷口随后收，
            // 这一段里喷口偏大，排气落压比掉下来，推力先掉一小截。
            st.AreaFrac -= dt / (AreaSeconds * 0.6);
                if (st.AreaFrac < target2) st.AreaFrac = target2;
            }
        }

        private static void StepFrac(AbState st, double dt, double target, bool canLight)
        {
            if (st == null) return;

            double tgt = target;
            if (double.IsNaN(tgt) || tgt < 0.0) tgt = 0.0;
            if (tgt > 1.0) tgt = 1.0;
            if (!canLight) tgt = 0.0;

            // dt <= 0 是"同步"信号（刚进飞行、或从时间加速回来）：直接到位，别让玩家白等一次爬升。
            // 第一次进来且 dt 正常时不清零——那样"从地面上按下加力"就永远建立不起来。
            if (dt <= 0.0)
            {
                st.Frac = tgt;
                st.Phase = st.Frac >= 0.999 ? AbPhase.Lit : (st.Frac > 0.0 ? AbPhase.Lighting : AbPhase.Off);
                return;
            }
            if (dt > 1.0) dt = 1.0;

            if (tgt > st.Frac + 1e-9)
            {
                // 往上建立：从 0 到 1 走 LightSeconds，所以"从 30% 爬到 100%"只用七成时间，
                // 与真机"加力段燃油建立是同一套泵"的行为一致。
                st.Frac += dt / LightSeconds;
                if (st.Frac > tgt) st.Frac = tgt;
                st.Phase = st.Frac >= 0.999 ? AbPhase.Lit : AbPhase.Lighting;
                return;
            }

            if (tgt < st.Frac - 1e-9)
            {
                // 往下退：点不着用最快的速率（FailSeconds），主动收油门/关加力用 ExtinguishSeconds。
                double rate = canLight ? ExtinguishSeconds : FailSeconds;
                st.Frac -= dt / rate;
                if (st.Frac < tgt) st.Frac = tgt;
                if (st.Frac <= 1e-6)
                {
                    st.Frac = 0.0;
                    st.Phase = AbPhase.Off;
                }
                else
                {
                    // 收到一个还在燃着的中间比例，不是"熄火中"——只是加力小了点。
                    st.Phase = tgt > 0.0 ? AbPhase.Lighting : AbPhase.Extinguishing;
                }
                return;
            }

            // 正好在目标上：只维护相位。
            if (st.Frac <= 1e-6) st.Phase = AbPhase.Off;
            else st.Phase = st.Frac >= 0.999 ? AbPhase.Lit : AbPhase.Lighting;
        }

        /// <summary>
        /// 这一帧油门要烧多满的加力（0~1）。
        ///
        /// AJE 的做法：油门过 <paramref name="lightThrottle"/> 以后按比例注入，
        /// 100% 油门才是满加力；手动把开关按下去则无条件满加力，
        /// 这样"我就想在低油门点亮加力"这件事仍然做得到。
        /// 放在 Core 而不是模块里，是为了它能在命令行回归里被断言（第 38 段⑤）。
        /// </summary>
        public static double Target(double throttle, double lightThrottle, bool autoLight, bool manual)
        {
            double t = 0.0;
            if (autoLight && lightThrottle < 0.995)
                t = (throttle - lightThrottle) / (1.0 - lightThrottle);
            if (manual) t = Math.Max(t, 1.0);
            if (double.IsNaN(t) || t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            return t;
        }

        /// <summary>状态栏/自检用的一句话。</summary>
        public static string Describe(AbState st)
        {
            if (st == null) return "";
            switch (st.Phase)
            {
                case AbPhase.Lighting: return string.Format(Loc.T("加力建立中 {0:F0}%", "Afterburner lighting {0:F0}%"), st.Frac * 100.0);
                case AbPhase.Extinguishing: return string.Format(Loc.T("加力退出中 {0:F0}%", "Afterburner shutting down {0:F0}%"), st.Frac * 100.0);
                case AbPhase.Lit: return Loc.T("加力", "Afterburner");
                default: return Loc.T("加力关", "Afterburner off");
            }
        }
    }

    /// <summary>加力的三档。玩家按钮上就这三句话，没有第四种含义。</summary>
    public enum AbModeKind
    {
        /// <summary>关：无论油门多高都不烧加力。</summary>
        Off,
        /// <summary>自动：油门过线以后按比例注入。</summary>
        Auto,
        /// <summary>强制开：无视油门，点亮就烧满。</summary>
        Manual
    }

    /// <summary>
    /// 加力模式的三态与切换。
    ///
    /// 之前把"自动"和"强制开"挤进同一个布尔 <c>afterburnerLit</c>：玩家点了"自动"，
    /// 实际是把开关置 false，于是点火边界判定拿到"没有命令"，火永远不亮；
    /// 再点一下又变成"强制开"，面板上的字和实际行为对不上。这就是"切换模式后逻辑混乱"。
    /// 两个布尔拆开、切换写成纯函数，命令行就能把它按步数断言（第 38 段⑥）。
    /// </summary>
    public static class AbModes
    {
        /// <summary>由两个持久化布尔还原出档位。</summary>
        public static AbModeKind Of(bool manual, bool auto)
        {
            if (manual) return AbModeKind.Manual;
            return auto ? AbModeKind.Auto : AbModeKind.Off;
        }

        /// <summary>关 → 自动 → 强制开 → 关。只有这一条路，不存在"跳档"。</summary>
        public static void Next(bool manual, bool auto, out bool nextManual, out bool nextAuto)
        {
            if (manual) { nextManual = false; nextAuto = false; return; }   // 强制开 → 关
            if (auto) { nextManual = true; nextAuto = false; return; }      // 自动 → 强制开
            nextManual = false; nextAuto = true;                            // 关 → 自动
        }

        /// <summary>按钮/读数上的字。带上门槛，玩家才知道"自动"从哪开始。</summary>
        public static string Label(AbModeKind kind, double lightThrottle)
        {
            switch (kind)
            {
                case AbModeKind.Manual: return Loc.T("强制开", "Forced on");
                case AbModeKind.Auto: return string.Format(Loc.T("自动（油门 {0:P0} 起）", "Auto (from {0:P0} throttle)"), lightThrottle);
                default: return Loc.T("关", "Off");
            }
        }

        /// <summary>档位 + 油门 → 这一帧要烧多满的加力比例（0~1）。</summary>
        public static double Target(AbModeKind kind, double throttle, double lightThrottle)
        {
            switch (kind)
            {
                case AbModeKind.Manual: return 1.0;
                case AbModeKind.Auto: return AfterburnerTransient.Target(throttle, lightThrottle, true, false);
                default: return 0.0;
            }
        }
    }
}