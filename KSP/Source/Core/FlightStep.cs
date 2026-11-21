using System;

namespace JetForge.Core
{
    /// <summary>这一物理帧发动机处在哪种状态。玩家看到的都是数字，但该改的东西完全不同。</summary>
    public enum FlightMode
    {
        /// <summary>正常出力。</summary>
        Running,
        /// <summary>没点火（关车）。</summary>
        NotIgnited,
        /// <summary>点火了但熄火保护触发。</summary>
        Flameout,
        /// <summary>点着、有进气，但这一状态烧不出推力（风车/停车）。</summary>
        NotSustaining,
        /// <summary>
        /// 燃烧已经停了，但转子还在带着惯性往下溜。
        /// 真机停车不是断电：N2 从满转速掉到风车要好几秒到十几秒，
        /// 这期间油门推到底也不会重新点着（没有火），只能重新点火。
        ///
        /// 这一档一直走到转速自己掉进风车转速为止——**没有"再点火下限"这种判据**。
        /// 转速会不会回来，由每一帧的解算器回答：低于某个转速时涡轮功就是带不动压气机，
        /// 那是求解出来的结果，不是我们先划一条线再宣布"停了"。
        /// </summary>
        Coasting
    }

    /// <summary>一帧的结算结果。模块只负责把它写进 KSP 的字段里。</summary>
    public struct FlightStepResult
    {
        public FlightMode Mode;

        /// <summary>抬底之后的命令油门。</summary>
        public double Command;

        /// <summary>转子滞后之后的实际油门（查表用的那个）。</summary>
        public double Throttle;

        /// <summary>模型推力（牛顿）。</summary>
        public double ThrustN;

        /// <summary>推进剂流量（t/s），喂给原版的那个。</summary>
        public double FuelTps;

        /// <summary>当前效率下的耗油率（kg/(N·h)）。</summary>
        public double Sfc;

        public double MassFlow, N1, N2, N3, Egt, T7;

        /// <summary>当前加力比例 0~1（0 = 纯干态，1 = 完整加力）。</summary>
        public double AbFrac;

        /// <summary>
        /// 喉道面积那一份的加力比例 0~1。与 <see cref="AbFrac"/> 是两个速率：
        /// 燃油先到、A8 后撑。推力按面积算，所以加力刚点着那一段推力是憋着的。
        /// </summary>
        public double AbAreaFrac;

        public PerformanceGrid.Sample Sample;

        /// <summary>熄火/未点火时的风车状态；运行中为默认（Valid=false）。</summary>
        public WindmillState Windmill;

        /// <summary>喂给原版的 maxFuelFlow / Isp 换算结果。</summary>
        public FuelBridge Bridge;

        public bool Running { get { return Mode == FlightMode.Running; } }

        /// <summary>这一帧是不是"火已经灭了、转子还在转"。</summary>
        public bool Coasting { get { return Mode == FlightMode.Coasting; } }

        /// <summary>转子转速（0~1，按换算转速比例）。停车时它往下掉，不是一下变 0。</summary>
        public double Spool;
    }

    /// <summary>
    /// 飞行中每一帧的数值链路，从玩家的油门一路算到"喂给原版的两个数"。
    ///
    /// 这一段为什么要单独放在 Core 里：它以前只存在于 <c>ModuleJetForgeEngine.UpdateThrottle</c>
    /// 里，而那个方法依赖 Part、vessel、Time，命令行一行都跑不了——于是"飞行链路对不对"
    /// 从项目开始到第 17 轮都只能靠实飞验证，而实飞需要人。搬到这里之后，同一条链路
    /// 可以整段进回归（第 21 段），顺序错了、闸门放错了位置都会被当场抓住。
    ///
    /// 顺序是有物理含义的，不能调换：
    ///   ① 抬底：原版油门 0 = 零流量 = 关车，真机油门 0 是慢车（StockBridge.InputFloor）
    ///   ② 熄火/未点火：直接进风车，且把转子起点设成风车转速（空中点火的 N2 不是 0）
    ///   ③ 转子滞后：命令值 → 实际转速，推力、流量、EGT 全跟着这一维走
    ///   ④ 查表：加力表/干态表由调用方选好传进来
    ///   ⑤ 自持闸：模型说烧不出正推力就真的给 0，不偷偷给一点
    ///   ⑥ 库存桥：把推力与油耗翻译成 maxFuelFlow + Isp（原版只认这两个数）
    /// </summary>
    public static class FlightStep
    {
        /// <summary>低于这个推力就当"没推力"（1 N 只是浮点噪声的量级）。</summary>
        public const double MinThrustN = 1.0;

        /// <summary>
        /// 旧签名（直接给网格）。留着不是兼容包袱，是回归需要：
        /// 第 21 段那批"飞行链路"用例都是拿网格写的，而它们验证的是
        /// 抬底/风车/滞后/自持闸/库存桥这五步的顺序，跟"数据从哪来"无关。
        /// 新代码一律走带 <see cref="ICycleSampler"/> 的那个。
        /// </summary>
        public static FlightStepResult Compute(
            PerformanceGrid grid,
            double inputThrottle, double dt, double alt, double mach,
            bool ignited, bool flameout,
            bool designOk, bool designSelfSustaining,
            ref double spool, ref bool spoolInit,
            double tauUp, double tauDown,
            double frontArea, double fallbackSfc,
            PerformanceGrid gridWet = null, AbState ab = null,
            double abTarget = 0.0, bool abCanLight = true,
            SpoolDynamics.Law spoolLaw = null)
        {
            return Compute(new GridSampler(grid, gridWet),
                inputThrottle, dt, alt, mach, ignited, flameout, designOk, designSelfSustaining,
                ref spool, ref spoolInit, tauUp, tauDown, frontArea, fallbackSfc,
                ab, abTarget, abCanLight, spoolLaw);
        }

        public static FlightStepResult Compute(
            ICycleSampler sampler,
            double inputThrottle, double dt, double alt, double mach,
            bool ignited, bool flameout,
            bool designOk, bool designSelfSustaining,
            ref double spool, ref bool spoolInit,
            double tauUp, double tauDown,
            double frontArea, double fallbackSfc,
            AbState ab = null, double abTarget = 0.0, bool abCanLight = true,
            SpoolDynamics.Law spoolLaw = null)
        {
            FlightStepResult r = new FlightStepResult();
            r.Command = StockBridge.FloorInput(inputThrottle);

            // ② 熄火/未点火：风车。转子的起点是"被气流吹着转的那几成转速"，
            // 不是 0——这就是真机空中点火的 N2 为什么不是 0。地面上 v=0，风车转速也就接近 0。
            //
            // 但**转速不能一下跳到风车值**：火苗灭了以后转子带着惯性继续转，
            // 满转速掉到风车要好几十秒（真机十几个秒到一分钟，看转子惯量）。
            // 以前这里直接把 spool 赋成风车转速、推力瞬间归零，玩家听到的是"啪一下没了"，
            // 看到的是转速表瞬间归零——那不是停车，那是断电。
            // 现在按转子的减速规律一帧一帧往下溜：油门推到底也没用（火已灭），
            // 要重新起飞只能重新点火。
            if (!ignited || flameout)
            {
                r.Windmill = Windmill.Compute(null, alt, mach, frontArea);
                double coastSpool = r.Windmill.Valid ? r.Windmill.SpoolStart : 0.0;
                if (!spoolInit)
                {
                    spool = Math.Max(spool, coastSpool);
                    spoolInit = true;
                }
                // 减速的下限是风车转速：气流还能吹着它转，不会比"被吹着"更慢。
                //
                // 时间常数不能直接用 tauDown（收油门用的那个，0.83 s 量级）：
                // 收油门是"少给油、火还在烧"，停车是"火没了、只剩惯性和风阻"，
                // 后者慢得多——真机从满转速掉到风车要十几秒。按 tauDown 算，
                // 一秒掉到 33%，玩家看到的还是"啪一下没了"。
                double tauCoast = Math.Max(2.5, Math.Max(tauUp, 0.5) * 3.0);
                spool = Spool.Step(spool, coastSpool, dt, tauCoast, tauCoast);
                if (spool < coastSpool) spool = coastSpool;

                bool coasting = spool > coastSpool + 0.01;
                // 这里不再判定"转速掉到多少就算停车"。
                // 以前有一档 Shutdown：低于 12% 就摘掉 EngineIgnited，等于替物理宣布"完蛋了"。
                // 问题是那个 12% 是我们挑的，而真正决定"还转不转得动"的是涡轮功与压气机功的比较——
                // 那是解算器每一帧都在算的东西。现在转子就按惯性一路往下溜到风车转速为止，
                // 中途如果玩家重新点火、或者来流把转速吹起来了，它自己就回到正常运行。
                r.Mode = coasting ? FlightMode.Coasting
                                  : (ignited ? FlightMode.Flameout : FlightMode.NotIgnited);
                r.Throttle = spool;
                r.Spool = spool;
                r.Bridge = StockBridge.Compute(0.0, 0.0, r.Command);
                r.ThrustN = 0.0;
                r.FuelTps = 0.0;
                r.Sfc = 0.0;
                // 表针还要跟着转：没有它，玩家眼里的转速是"瞬间归零"，
                // 而这正是我们说"停车不要太突然"时最该动的那根指针。
                // 只是拿解算器在同一转速上取一次数，推力与油耗不用（火已经灭了）。
                if (sampler != null)
                {
                    PerformanceGrid.Sample cs = sampler.Sample(spool, alt, mach, 0.0);
                    if (cs.Valid)
                    {
                        r.Sample = cs;
                        r.N1 = cs.N1; r.N2 = cs.N2; r.N3 = cs.N3;
                        r.MassFlow = cs.MassFlow;
                        // 燃烧停了，排温跟着掉；留一点残热，别让 EGT 立刻归零。
                        r.Egt = cs.Egt * 0.45;
                        r.T7 = cs.Egt * 0.30;
                    }
                }
                return r;
            }

            // ③ 转子滞后。第一次进来（刚点火、或从时间加速回来）直接同步，
            // 别让玩家在起飞前白等一次"爬升"。
            if (!spoolInit)
            {
                spool = r.Command;
                spoolInit = true;
            }
            // 有功率律就按功率积分（加速慢是"剩余功率不够"，不是"时间常数大"）；
            // 没解算器（旧网格/简单模式）时退回指数滞后，那条路本来就给不出压气机功。
            spool = spoolLaw != null && spoolLaw.Valid
                ? spoolLaw.Step(spool, r.Command, dt)
                : Spool.Step(spool, r.Command, dt, tauUp, tauDown);
            r.Throttle = spool;
            r.Spool = spool;

            // ④ 问一次循环。谁被问、是查表还是现算，这一层不管：
            // 加了加力就先推进瞬态拿到 0~1 的比例，再带着这个比例去问。
            // 网格实现会自己把干湿两表混起来；实时实现直接按这个比例解一次——
            // 后者才是玩家要的"加力刚点着那半秒"。
            double abFrac = 0.0;
            double a8Frac = 0.0;
            if (ab != null)
            {
                AfterburnerTransient.Step(ab, dt, abTarget, abCanLight);
                abFrac = ab.Frac;
                a8Frac = ab.AreaFrac;
            }
            PerformanceGrid.Sample s = sampler != null
                ? sampler.Sample(spool, alt, mach, abFrac, a8Frac)
                : new PerformanceGrid.Sample();
            r.AbFrac = abFrac;
            r.AbAreaFrac = a8Frac;
            r.Sample = s;
            r.MassFlow = s.MassFlow;
            r.N1 = s.N1;
            r.N2 = s.N2;
            r.N3 = s.N3;
            r.Egt = s.Egt;
            r.T7 = s.T7;

            // ⑤ 自持闸。设计不成立、或者这一状态烧不住，就真的 0——不是"打个折给一点"。
            double thrustN = s.Valid ? s.Thrust : 0.0;
            bool sustaining = thrustN > MinThrustN && designOk && designSelfSustaining;
            if (sustaining)
            {
                r.Mode = FlightMode.Running;
            }
            else
            {
                thrustN = 0.0;
                r.Mode = FlightMode.NotSustaining;
            }

            r.Sfc = s.Sfc > 1e-9 ? s.Sfc : fallbackSfc;
            if (r.Sfc <= 1e-9) r.Sfc = 0.06;
            r.ThrustN = thrustN;

            // ⑥ 库存桥。原版飞行中不用我们写的 finalThrust，它自己算
            // requestedMassFlow × Isp × g；所以推力与油耗必须换成这两个数喂回去。
            r.FuelTps = s.FuelFlow > 0.0 ? s.FuelFlow / 1000.0 : thrustN * r.Sfc / 3600.0 / 1000.0;
            r.Bridge = StockBridge.Compute(thrustN / 1000.0, r.FuelTps, r.Command);
            return r;
        }
    }
}