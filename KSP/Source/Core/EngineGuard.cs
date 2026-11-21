using System;

namespace JetForge.Core
{
    /// <summary>
    /// 油门到底那一段的守卫：慢车档到底给不给得出。
    ///
    /// 为什么单独抽出来：这条判断以前散在模块里，条件还写成了
    /// "`!IdleSustainable` 且 `r.Running`"——**要求"这一帧还烧得着"，而油门到底烧不着
    /// 正是要抓的现象**。条件自相矛盾，于是玩家把慢车设成 2% 什么都不会发生。
    /// 抽到 Core 以后它可以进命令行回归，被断言钉住，而不是等玩家实飞来发现。
    ///
    /// 物理只有一条：慢车供的那点油留不住火。两种走法——
    /// ① 玩家设的推力占比低于这台机器能稳住的最低档（`IdleClamped`）：
    ///    求解器把 T4 停在最低可维持温度（JF-1 是 816 K ≈ 12.8% 推力）。
    ///    玩家以为"设 2% 就是 2%"，其实是模型偷偷多给了油。
    /// ② 反解出的慢车油气比低于贫油下限（`IdleFar &lt; LeanFar`）：直接吹熄。
    /// </summary>
    public static class EngineGuard
    {
        /// <summary>命令值落在抬底下界之上多窄的一条带里算"油门到底"。</summary>
        public const double BottomBand = 0.012;

        /// <summary>这一帧的油门命令是不是"到底了"。命令值已经被抬底到 <see cref="StockBridge.InputFloor"/>。</summary>
        public static bool AtBottom(double command)
        {
            if (double.IsNaN(command)) return false;
            return command <= StockBridge.InputFloor + BottomBand;
        }

        /// <summary>慢车档是否留不住火（与"当前是否正在转"无关——转不住正是后果）。</summary>
        public static bool IdleStarves(DesignConstants c, double command)
        {
            return c != null && AtBottom(command) && !c.IdleSustainable;
        }

        /// <summary>一行话说清这台机器的慢车档：设定值、油气比、贫油下限、能否稳住。</summary>
        public static string Describe(EngineDesignData d, DesignConstants c)
        {
            if (c == null) return "";
            string verdict = c.IdleSustainable ? Loc.T("可稳定", "Stable") : Loc.T("贫油熄火", "Lean blowout");
            return string.Format(Loc.T("推力 {0:P1}｜油气比 {1:F4}｜贫油 {2:F4}｜{3}", "Thrust {0:P1} | fuel/air {1:F4} | lean limit {2:F4} | {3}"),
                c.IdleThrustFrac, c.IdleFar, c.LeanFar, verdict);
        }
    }
}