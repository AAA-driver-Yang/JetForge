using System;

namespace JetForge.Core
{
    /// <summary>
    /// 核心压气机的"工作线"——不变量：转速定了，压比就定了。
    ///
    /// 为什么需要它：现在的求解器把核心压比当成自由变量，让它去"吃掉"涡轮剩余功。
    /// 于是低功率时风扇少要的那份功全塞进核心机，核心压比虚高，SFC 谷底被拖到慢车。
    /// 真机上核心压比是它自己工作线上的点：转速掉多少，压比掉多少，
    /// 剩下的功不要也得不要（涡轮会自己落到更低的落压比上）。
    ///
    /// 这条线是**参数化的近似**，不是真机手册的特性图。形式取
    ///     PR / PRd = 1 + (PRd - 1) * nc^k
    /// 即压比增量按 nc^k 掉：多级轴流取 k≈2.45（级数越多越"陡"），
    /// 离心取 k≈2.0（单级离心在低转速还能守住更多压比）。
    /// 设计点 nc=1 时严格等于设计压比，不碰设计点一分。
    /// </summary>
    public static class CompressorMap
    {
        /// <summary>轴流多级压气机的工作线指数。</summary>
        public const double AxialExponent = 2.45;

        /// <summary>离心压气机的工作线指数。</summary>
        public const double CentrifugalExponent = 2.00;

        /// <summary>给定换算转速比例，返回核心压气机在工作线上应有的压比。</summary>
        public static double PressureRatio(double cprDesign, double nc, CompressorKind kind)
        {
            if (cprDesign <= 1.0) return 1.0;
            double k = kind == CompressorKind.Centrifugal ? CentrifugalExponent : AxialExponent;
            double x = nc < 0.02 ? 0.02 : nc;
            if (x > 1.15) x = 1.15;
            return 1.0 + (cprDesign - 1.0) * Math.Pow(x, k);
        }

        /// <summary>
        /// 同一转速下的流量系数：定通道面积时 ṁ ∝ U ∝ N。
        /// 跟 OffDesign 里 WcCapacityFrac 的线性口径同源，这里只是给测试与判据一个入口。
        /// </summary>
        public static double FlowFrac(double nc)
        {
            double x = nc < 0.02 ? 0.02 : nc;
            return x > 1.15 ? 1.15 : x;
        }

        /// <summary>
        /// 设计点自洽检查：nc = 1 必须正好给出设计压比。
        /// 这是这类"乘上一个比例"的函数最容易写错的地方——写错的话设计点会悄悄移位。
        /// </summary>
        public static bool DesignPointSelfCheck(double cprDesign, CompressorKind kind, out string msg)
        {
            double pr = PressureRatio(cprDesign, 1.0, kind);
            double f = FlowFrac(1.0);
            bool ok = Math.Abs(pr - cprDesign) < 1e-6 && Math.Abs(f - 1.0) < 1e-9;
            msg = string.Format("设计压比 {0:F4} → 工作线给 {1:F4}，流量系数 {2:F4}", cprDesign, pr, f);
            return ok;
        }
    }
}