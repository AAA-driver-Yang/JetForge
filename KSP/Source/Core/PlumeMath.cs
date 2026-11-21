using System;

namespace JetForge.Core
{
    /// <summary>
    /// 尾焰的几何与颜色，纯算术、不碰 Unity。
    /// 放在 Core 里是为了能被命令行回归直接测——尾焰是唯一样子货，
    /// 但它背后的映射关系（推力→长度、温度→颜色）可以拿数字验证。
    ///
    /// 这里刻意不追求"喷流光学正确"，只保证两件事：
    /// 1. 输入全是求解器算出来的真量（出口速度、出口温度、出口马赫数、油门），不是随手给的装饰参数；
    /// 2. 单调性合理——推力大就更长更亮，温度高就从橙黄转向青白，加力再明显一档。
    /// </summary>
    public static class PlumeMath
    {
        public struct PlumeSpec
        {
            public bool Visible;
            public double Length;      // 喷流长度（m），沿 -Y 方向
            public double RadiusStart; // 出口处半径（m）
            public double RadiusEnd;   // 尾端半径（m）
            public double CoreLength;  // 亮核长度（m），约为总长的这个比例
            public double CoreRadius;  // 亮核半径（m）
            public float R, G, B, A;   // 基础颜色与不透明度
            public string Note;        // 人看的说明，进日志用
        }

        /// <summary>
        /// 由求解器输出的喷管出口状态推出尾焰外形。
        /// </summary>
        /// <param name="exitRadius">喷管出口半径 m（几何给的）</param>
        /// <param name="exitVelocity">出口速度 m/s（含压力推力时的等效速度）</param>
        /// <param name="exitTemp">出口静温 K</param>
        /// <param name="exitMach">出口马赫数，1 表示正好壅塞</param>
        /// <param name="throttle">油门 0~1</param>
        /// <param name="afterburner">是否在开加力</param>
        public static PlumeSpec Compute(double exitRadius, double exitVelocity, double exitTemp,
                                        double exitMach, double throttle, bool afterburner)
        {
            PlumeSpec p = new PlumeSpec();
            p.Note = "";

            if (exitRadius <= 1e-4 || exitVelocity <= 1.0 || exitTemp <= 100.0)
            {
                p.Visible = false;
                p.Note = "无喷流：出口速度或温度不成立";
                return p;
            }

            double thr = Clamp(throttle, 0.0, 1.0);
            double mach = Clamp(exitMach, 0.0, 4.0);
            if (thr < 0.03)
            {
                p.Visible = false;
                p.Note = "无喷流：油门过低";
                return p;
            }

            double d = 2.0 * exitRadius;

            // 长度：基准 3 倍直径，超声速过度膨胀会拉长，加力再加一截，低油门收缩。
            // 真机上涡喷地面全加力的焰长约 3~6 倍直径，这里落在同一量级。
            double len = d * (3.0 + 1.2 * Math.Max(0.0, mach - 1.0) + (afterburner ? 1.5 : 0.0))
                         * (0.55 + 0.45 * thr);

            // 尾端张开：欠膨胀喷流会鼓出去，出口马赫越高鼓得越明显。
            double rEnd = exitRadius * (1.05 + 0.22 * mach + (afterburner ? 0.18 : 0.0));

            // 亮核：喉道到马赫盘那一段最亮，长度随出口马赫增长但有上限。
            double coreLen = len * Clamp(0.28 + 0.10 * mach, 0.25, 0.55);
            double coreR = exitRadius * 0.72;

            p.Visible = true;
            p.Length = len;
            p.RadiusStart = exitRadius;
            p.RadiusEnd = rEnd;
            p.CoreLength = coreLen;
            p.CoreRadius = coreR;

            // 颜色：按黑体直觉把出口温度映射成色相。600K 暗红 → 900K 橙 → 1300K 黄 →
            // 1700K 以上转向青白（真实加力尾焰的蓝紫芯子），再叠加不透明度。
            double x = Clamp((exitTemp - 600.0) / 1100.0, 0.0, 1.0);
            float r, g, b;
            if (x < 0.5)
            {
                double k = x / 0.5;
                r = (float)Lerp(1.00, 1.00, k);
                g = (float)Lerp(0.30, 0.72, k);
                b = (float)Lerp(0.08, 0.28, k);
            }
            else
            {
                double k = (x - 0.5) / 0.5;
                r = (float)Lerp(1.00, 0.72, k);
                g = (float)Lerp(0.72, 0.86, k);
                b = (float)Lerp(0.28, 1.00, k);
            }

            p.R = r;
            p.G = g;
            p.B = b;
            p.A = (float)(0.30 + 0.38 * x) * (float)Math.Sqrt(thr);

            p.Note = string.Format("长 {0:F2} m / 出口半径 {1:F3} m / 出口 {2:F0} m/s {3:F0} K Ma{4:F2}{5}",
                                   len, exitRadius, exitVelocity, exitTemp, mach,
                                   afterburner ? " / 加力" : "");
            return p;
        }

        private static double Clamp(double x, double lo, double hi)
        {
            return x < lo ? lo : (x > hi ? hi : x);
        }

        private static double Lerp(double a, double b, double k)
        {
            return a + (b - a) * k;
        }
    }
}