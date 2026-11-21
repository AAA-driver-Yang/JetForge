using System;

namespace JetForge.Core
{
    /// <summary>
    /// 把"我们要的推力与油耗"翻译成 KSP 原版能听懂的参数。
    ///
    /// 为什么需要这一层：反汇编 ModuleEngines 之后确认，原版在飞行里的推力链路是
    ///
    ///     requestedMassFlow = maxFuelFlow × currentThrottle × ModifyFlow() × multFlow
    ///     realIsp          = atmosphereCurve.Evaluate(海平面静压) ×（可选的各种曲线）
    ///     resultingThrust  = requestedMassFlow × realIsp × g × multIsp
    ///     finalThrust      = resultingThrust × EnginePower（推力限幅）
    ///
    /// 也就是说：**推力是"流量 × 比冲"算出来的，不是我们写进 finalThrust 的那个数**。
    /// ThrustUpdate 每一物理帧都会把 finalThrust 覆盖掉。所以要让我们算的推力真的出现在
    /// 飞行中，只能反过来喂它两个数：
    ///
    ///     maxFuelFlow = 我们算的推进剂流量 / 当前油门
    ///     Isp         = 我们算的推力 / (流量 × g0)
    ///
    /// 两者相乘正好回到我们的推力，而且油耗也是我们算的那个。
    ///
    /// 单位：推力 kN、流量 t/s、g0 = 9.80665。
    /// </summary>
    public struct FuelBridge
    {
        /// <summary>喂给 maxFuelFlow 的值（t/s 每单位油门）。</summary>
        public double MaxFuelFlow;

        /// <summary>压平后的比冲曲线值（s）。</summary>
        public double Isp;

        /// <summary>这台机器在这一帧是否"基本不出力"（关车/停车）。</summary>
        public bool Idle;

        public string Note;

        /// <summary>
        /// 原版拿这两个数算回来的推力（kN）。应当等于喂进去的推力——
        /// 注意它自带那层油门重映射，所以乘的是 <see cref="StockBridge.EffectiveThrottle"/>，
        /// 而不是玩家输入。这条往返用于回归自检，证明换算没写反。
        /// </summary>
        public double StockThrust(double throttle)
        {
            return MaxFuelFlow * StockBridge.EffectiveThrottle(throttle) * Isp * Gas.G0;
        }

        /// <summary>原版拿这两个数算回来的推进剂流量（t/s）。</summary>
        public double StockFlow(double throttle)
        {
            return MaxFuelFlow * StockBridge.EffectiveThrottle(throttle);
        }
    }

    public static class StockBridge
    {
        /// <summary>原版认为"这台机器在工作"的最小油门。低于它就不用再补偿了。</summary>
        public const double MinThrottle = 0.02;

        /// <summary>关车时喂给原版的流量：给一点点点，让原版走"点火但基本不烧"的正常分支。</summary>
        public const double IdleFlow = 1e-4;

        /// <summary>
        /// 原版油门的有效下界。
        ///
        /// 反汇编出来的事实：RequiredPropellantMass 里只有
        ///     requestedMassFlow = maxFuelFlow × currentThrottle
        /// 后面乘 ModifyFlow()、multFlow，**全程没有任何下限钳制**；CalculateThrust 也不看
        /// minThrust。所以油门 0 在原版里就是"零流量、零推力"——对火箭是对的，对喷气发动机
        /// 是错的：真机油门 0 是慢车（战斗机涡扇约 8~13% 满推力），关了才是关车。
        ///
        /// 好在原版自带重映射，UpdateThrottle 的 IL 明写着：
        ///     if (requestedThrottle &gt; 0) requestedThrottle = Mathf.Lerp(throttleMin, 1, requestedThrottle)
        /// 只要把 throttleUseAlternate 打开、throttleMin 设成这个值，玩家手里的 0~1
        /// 就变成 0.02~1，慢车推力才真的存在。
        /// </summary>
        public const double InputFloor = 0.02;

        /// <summary>把玩家的油门命令抬到原版能工作的下界（编辑器与地面测试时也用同一个口径）。</summary>
        public static double FloorInput(double input)
        {
            if (double.IsNaN(input) || input < 0.0) input = 0.0;
            return input < InputFloor ? InputFloor : input;
        }

        /// <summary>
        /// 原版**真正用来乘流量**的那个油门。
        ///
        /// 这是"实飞推力比热力图大"的真根因：我们打开 `throttleUseAlternate`、
        /// `throttleMin = InputFloor` 之后，`UpdateThrottle` 会把玩家给的 0~1 重映射成
        /// `Lerp(0.02, 1, input)`。而 `Compute` 以前是拿**玩家的原始 input** 当分母去反解
        /// `maxFuelFlow = 油量 / input`，原版再乘回 `Lerp(0.02,1,input)`，于是
        ///   实飞推力 = 我们的推力 × (0.02 + 0.98·input) / input。
        /// 满油门时两者相等（比值 1），半油门 1.02，10% 油门 **1.18**，5% 油门 **1.38**——
        /// 越往低油门差得越多，正好是玩家说的"实际推力明显大于热力图"，而且引擎限流
        /// （进气道给不出那么多流量）压低的是我们那个数，原版乘回来又给抬上去了。
        /// 现在分母改用这个函数，往返恰好等于 1。
        /// </summary>
        public static double EffectiveThrottle(double input)
        {
            if (double.IsNaN(input) || input < 0.0) input = 0.0;
            if (input > 1.0) input = 1.0;
            return InputFloor + (1.0 - InputFloor) * input;
        }

        public static FuelBridge Compute(double thrustKN, double fuelTps, double throttle)
        {
            if (throttle < MinThrottle || fuelTps <= 1e-9 || thrustKN <= 1e-9)
            {
                return new FuelBridge
                {
                    MaxFuelFlow = IdleFlow,
                    Isp = 1.0,
                    Idle = true,
                    Note = "关车/停车"
                };
            }

            double isp = thrustKN / (fuelTps * Gas.G0);
            return new FuelBridge
            {
                // 分母是**原版实际乘的那个油门**（Lerp 之后），不是玩家输入。
                MaxFuelFlow = fuelTps / EffectiveThrottle(throttle),
                Isp = isp,
                Idle = false,
                Note = ""
            };
        }

        /// <summary>换算之后的相对误差（用于诊断，正常应当近 0）。</summary>
        public static double RoundTripError(double thrustKN, double fuelTps, double throttle, FuelBridge b)
        {
            if (b.Idle) return 0.0;
            double got = b.StockThrust(throttle);
            double want = thrustKN;
            if (Math.Abs(want) < 1e-9) return 0.0;
            return (got - want) / want;
        }
    }
}