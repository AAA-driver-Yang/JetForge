using System;

namespace JetForge.Core
{
    /// <summary>
    /// 风扇特性线（第一版：工作线形式，不建完整压比-流量图）。
    ///
    /// 为什么需要它：此前"风扇功 = 压气机功 × 常数 fc"，等于把风扇钉在核心机上。
    /// 结果是涡扇的油耗谷底跑到 10% 油门（真机在 85~95%）——因为油门一收，
    /// 风扇压比与涵道流量都跟着按同一条曲线往下掉，推进效率不降反升。
    ///
    /// 真机上这几个量是绑在一起的，一个转速对应一组（压比, 流量）：
    ///   1. 工作线：ψ(Nc) —— 压比随转速的走向（(π-1) ∝ ψ·Nc²）
    ///   2. 换算流量：ṁ√θ/δ 沿工作线随 Nc 涨落
    ///   3. 低压轴功率平衡：LPT 产出的功 = 风扇吃掉的功
    /// 三个一起解，n1 才是未知数；只解一个必然会像前两次那样把别的量顶出边界。
    ///
    /// 本文件现在只放前两条（纯函数，可离线验证），第三条等函数形态被回归钉住再接进 OffDesign。
    /// </summary>
    public static class FanMap
    {
        /// <summary>设计点转速。</summary>
        public const double NcDesign = 1.0;

        /// <summary>
        /// 工作线的压比系数 ψ(Nc)/ψ(1)。
        /// 依据：轴流风扇在 70% 转速附近压比大约只剩设计值的一半到六成，
        /// 50% 转速时基本只比 1 高一点点；超转到 105% 时压比还能再长几个点。
        /// </summary>
        public static double PsiRatio(double nc)
        {
            if (nc <= 0.0) return 0.0;
            if (nc < NcDesign)
            {
                double d = NcDesign - nc;
                double r = 1.0 - 0.62 * d * d;
                return r < 0.02 ? 0.02 : r;
            }
            // 超转段给一点正斜率，但别让压比无界——真机超转 5% 也就多三五个点。
            double up = 1.0 + 0.05 * (nc - NcDesign);
            return up > 1.25 ? 1.25 : up;
        }

        /// <summary>
        /// 风扇压比：fpr = 1 + (fpr设计 - 1) · ψ(Nc) · Nc²。
        /// Nc=1 时严格等于设计值。
        /// </summary>
        public static double PressureRatio(double fprDesign, double nc)
        {
            if (fprDesign <= 1.0) return 1.0;
            double f = 1.0 + (fprDesign - 1.0) * PsiRatio(nc) * nc * nc;
            return f < 1.0 ? 1.0 : f;
        }

        /// <summary>
        /// 反查转速：给定压比，工作线上对应的换算转速。
        /// 压比对转速是单调的（ψ(Nc)·Nc² 在整个区间上单调增），所以直接二分。
        /// 求解器每点要调一次，不能挂迭代——这里固定 40 次二分，全是四则运算。
        /// </summary>
        public static double SpeedForPressureRatio(double fprDesign, double fpr)
        {
            if (fprDesign <= 1.0001) return 1.0;
            if (fpr <= 1.0) return 0.05;
            if (fpr >= fprDesign) return 1.0;
            double lo = 0.05, hi = 1.0;
            for (int i = 0; i < 40; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (PressureRatio(fprDesign, mid) < fpr) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>
        /// 沿工作线的换算流量系数（相对设计点）。
        /// 设计点附近接近 1；转速降下来时风扇"吃不满"通道，换算流量跟着掉。
        /// 指数取 0.8 而不是 1：真机风扇低速端流量掉得比转速慢（后面还有开式转子）。
        /// </summary>
        public static double FlowFrac(double nc)
        {
            if (nc <= 0.0) return 0.0;
            if (nc > 1.15) return 1.15;
            double f = Math.Pow(nc, 0.80);
            return f < 0.02 ? 0.02 : f;
        }

        /// <summary>
        /// 设计点自检：Nc=1 必须原样返回设计值，否则接进求解器后设计点会漂。
        /// 命令行第 36 段会断言这一条。
        /// </summary>
        public static bool DesignPointSelfCheck(double fprDesign, out string msg)
        {
            double fpr = PressureRatio(fprDesign, NcDesign);
            double flow = FlowFrac(NcDesign);
            bool ok = Math.Abs(fpr - fprDesign) < 1e-9 && Math.Abs(flow - 1.0) < 1e-9;
            msg = string.Format("fpr 设计点 {0:F4}（应 {1:F4}）｜流量系数 {2:F4}（应 1）", fpr, fprDesign, flow);
            return ok;
        }

        /// <summary>
        /// 一个转速下风扇需要多少功率（每公斤空气）。
        /// w = cp·T2·(fpr^((γ-1)/γ·η) - 1)，与 Cycle 里风扇段的算法一致。
        /// </summary>
        public static double FanWorkPerKg(double t2, double fpr, double etaPol, double gamma, double cp)
        {
            if (fpr <= 1.0) return 0.0;
            double e = (gamma - 1.0) / gamma * etaPol;
            return cp * t2 * (Math.Pow(fpr, e) - 1.0);
        }
    }
}