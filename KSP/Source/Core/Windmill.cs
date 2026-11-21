using System;

namespace JetForge.Core
{
    /// <summary>
    /// 风车状态（发动机熄火、被来流吹着转）。
    ///
    /// 为什么值得单独算：以前的处理是"压比降到 1 以下 → 判成风车 → 推力 0"。
    /// 推力 0 意味着关车之后这台发动机对飞机没有任何影响，玩家可以关车滑翔白嫖。
    /// 真机相反：风车的发动机是**阻力源**，而且是相当显著的一个（迎风面的大风车），
    /// 一台大涵道比发动机风车阻力能到满推力的 15~25%，战斗机涡扇 5~12%。
    ///
    /// 建模方式（诚实声明）：**这是标定模型，不是从叶片几何算出来的。**
    /// 用远场动压 × 迎风面积 × 风车阻力系数，系数随马赫上升（唇口溢流与风扇大攻角损失）。
    /// 想用第一性原理算，需要叶片稠度、风扇攻角、唇口几何——那是另一期的事。
    /// 转子转速同理：取额定的 25%（真机风车 20~30%），只用于读数与再点火起点。
    /// </summary>
    public struct WindmillState
    {
        public bool Valid;          // 有没有算出来（面积/马赫缺失时为 false）
        public double DragN;        // 阻力（正值 = 阻力，方向与来流相反）
        public double N1Pct, N2Pct; // 风车转速（占额定百分比）
        public double Cd;           // 用掉的风车阻力系数
        public double Q;            // 远场动压 Pa
        public string Note;

        /// <summary>再点火起点：风车转速折算成"等效油门"，让分轴惯性从 25% 开始爬而不是从 0。</summary>
        public double SpoolStart
        {
            get { return Valid ? Math.Max(0.0, Math.Min(N2Pct / 100.0, 0.95)) : 0.0; }
        }
    }

    public static class Windmill
    {
        /// <summary>
        /// 风车转速占额定的比例，随来流速度上升。
        ///
        /// 不能写成常数：停在地面上（V=0）风扇是静止的，真机风车 20~30% 说的是
        /// "在空中以巡航速度飞着、发动机熄火"那个状态。用 v0 线性过渡到 180 m/s，
        /// 之后饱和——再快也不会转得更明显（叶片攻角已经全分离）。
        /// </summary>
        public static double SpeedFrac(double v0)
        {
            double f = 0.06 + 0.28 * (v0 / 180.0);
            return Math.Max(0.06, Math.Min(f, 0.34));
        }

        /// <summary>
        /// 风车阻力系数（参考面积 = 迎风面积 π·R进²）。
        ///
        /// 标定锚点是"风车阻力 ≈ 满推力的 5~15%"（真机涡扇的公开量级），
        /// 也就是风车阻力大致等于穿过发动机那部分气流的动量损失。
        /// 光滑短舱本身的 Cd 只有 0.06~0.12，这里高出一倍多，全是"发动机在风车里"的那部分：
        /// 风扇全叶大攻角分离（低速）、唇口溢流（跨声速）。
        /// </summary>
        public static double DragCoefficient(double mach)
        {
            double m = Math.Max(0.0, mach);
            if (m <= 0.6) return 0.08 + 0.05 * (m / 0.6);
            if (m <= 0.95) return 0.13 + 0.05 * ((m - 0.6) / 0.35);
            if (m <= 1.30) return 0.18 + 0.06 * ((m - 0.95) / 0.35);
            return Math.Min(0.24 + 0.05 * (m - 1.30), 0.40);
        }

        /// <summary>
        /// 算风车状态。frontArea 是迎风面积（m²），由调用方按外形给（模块里用 π·R进²）。
        /// </summary>
        public static WindmillState Compute(EngineDesignData d, double alt, double mach, double frontArea)
        {
            WindmillState s = new WindmillState();
            if (frontArea <= 1e-6)
            {
                s.Note = "没有迎风面积，风车阻力按 0 处理";
                return s;
            }

            double p0, t0;
            Gas.Isa(alt, out p0, out t0);
            double v0 = mach * Gas.SoundSpeed(t0);

            // 远场动压。静止时动压为 0 —— 风车在静止地面不产生阻力，这是对的：
            // 没有来流就没有流量，也就没有冲压阻力。
            double rho = p0 / (Gas.R * Math.Max(t0, 1.0));
            double q = 0.5 * rho * v0 * v0;

            s.Q = q;
            s.Cd = DragCoefficient(mach);
            s.DragN = q * frontArea * s.Cd;
            double sf = SpeedFrac(v0);
            s.N2Pct = sf * 100.0;
            s.N1Pct = Math.Min(sf * 1.15, 0.95) * 100.0;   // 风扇走风扇轴，风车里通常比核心转得快
            s.Valid = true;
            s.Note = string.Format("风车：q={0:F0} Pa Cd={1:F2} A={2:F2} m² 转速 {3:F0}%", q, s.Cd, frontArea, s.N2Pct);
            return s;
        }

        /// <summary>风车阻力相当于满推力的百分比，给判据/日志用。</summary>
        public static double DragPercentOfMax(WindmillState s, double maxThrustN)
        {
            if (!s.Valid || maxThrustN <= 1.0) return 0.0;
            return s.DragN / maxThrustN * 100.0;
        }
    }
}