using System;

namespace JetForge.Core
{
    /// <summary>
    /// 转子惯性：油门动一下，推力不是立刻就到。
    ///
    /// 真机上这条滞后来自转子惯量与燃油调节器：油门推到底，N2 要几秒才爬到新平衡点，
    /// 推力跟着爬。J85 这类小涡喷约 2.5–3 s，J79 慢到 4–6 s（B-58 的飞行员对此有意见），
    /// F110 约 3 s，GE90 这种三级风扇大机器要 5 s 以上。
    ///
    /// 这里不假装能算出惯量——我们手里没有转子质量分布。用的是一阶滞后，
    /// 时间常数按风扇直径做标度：τ = 1.2 s × (D/0.76)^0.5。
    /// 标定出来的量级：0.45 m（J85 那个级别）0.92 s，0.76 m 1.20 s，
    /// 1.18 m（F110）1.50 s，3.25 m（GE90）2.48 s。
    /// 真机的"慢车到最大"总时长大约是 3τ 到 4τ，也就是小机 3 秒、大风扇 8 秒左右——
    /// 跟飞行员手册里的说法对得上，但这是个量级拟合，不是从惯量算出来的。
    /// 减速比加速快，因为减速靠收油，不靠涡轮剩余功率。
    /// </summary>
    public static class Spool
    {
        /// <summary>加速时间常数（秒）。</summary>
        public static double TauUp(EngineDesignData d)
        {
            double fanD = d != null && d.HasFan ? d.Fan.Diameter : (d != null ? d.InletDiameter : 0.76);
            double tau = 1.2 * Math.Pow(Math.Max(fanD, 0.3) / 0.76, 0.5);
            return Clamp(tau, 0.5, 4.0);
        }

        /// <summary>减速时间常数（秒）。真机收油比加油快。</summary>
        public static double TauDown(EngineDesignData d)
        {
            return 0.55 * TauUp(d);
        }

        /// <summary>
        /// 走一步一阶滞后。dt 用物理帧时长；dt ≤ 0 或输入有 NaN 时原样返回，
        /// 免得一次异常把油门状态写成 NaN 之后再也回不来。
        /// </summary>
        public static double Step(double current, double target, double dt, double tauUp, double tauDown)
        {
            if (double.IsNaN(current) || double.IsNaN(target)) return 0.0;
            target = Clamp(target, 0.0, 1.0);
            if (double.IsNaN(dt) || dt <= 0.0) return Clamp(current, 0.0, 1.0);

            // 已经在目标上就别再抖：一阶滞后会无限接近但永不相等，白算。
            if (Math.Abs(target - current) < 1e-6) return target;

            double tau = target > current ? tauUp : tauDown;
            if (tau < 1e-3) tau = 1e-3;

            // 指数解，不是欧拉步进：dt 大（时间加速）时也不会冲过头。
            double k = 1.0 - Math.Exp(-dt / tau);
            double next = current + (target - current) * k;
            return Clamp(next, 0.0, 1.0);
        }

        /// <summary>
        /// 到目标的 95% 需要多久（秒）。回归里拿它对真机的加速时间。
        /// 一阶滞后 tau 到 95% 是 3·tau，所以这条不是公式，是把 3 这个数写在一个地方。
        /// </summary>
        public static double Settle95(double tauUp)
        {
            return 3.0 * tauUp;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}