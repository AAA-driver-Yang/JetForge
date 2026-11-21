using System;

namespace JetForge.Core
{
    /// <summary>
    /// 激波：这道题终于不是"查一条经验曲线"了。
    ///
    /// 以前超声速段的总压恢复是 `TechLevel.IntakeTpr(mach)` 那条 MIL-E-5008B 幂次曲线
    /// （`1 − 0.075(M−1)^1.35`），再按型式加减几个系数。它的问题是**分不出型式**：
    /// 皮托（只吃一道正激波）和斜板（两道斜激波 + 一道正激波）在同一张表上只差几个百分点，
    /// 而真机差一倍——M2 时皮托只剩 0.72，两级斜板外压缩还能有 0.85 以上。
    ///
    /// 这里换成真解：
    ///   · 正激波：解析式（斜激波的极限情况，也是皮托进气道唯一能用的那道）
    ///   · 斜激波：θ-β-M 关系二分求解，取弱解（弱解才是稳定、能起动的那个）
    ///   · 锥形激波：没有解析解，用等偏角斜激波解，损失乘 1.25
    ///     （公开的锥激波图表里，同偏角的锥激波总压损失比二维斜激波高两到三成）
    ///
    /// 所有公式用同一套 γ。空气在 200~600 K、常压下的 γ 是 1.39~1.40，
    /// 这里固定 1.40，与 `Gas.SoundSpeed()` 用的是同一个数——不然进气道和喷管会各说各话。
    /// </summary>
    public static class Shock
    {
        /// <summary>比热比。与 `Gas.GammaAir` 保持一致。</summary>
        public const double Gamma = 1.4;

        /// <summary>
        /// 正激波后的总压比 pt2/pt1。M1&lt;=1 时返回 1（没有激波，也就没有损失）。
        ///
        /// pt2/pt1 = [ ((γ+1)M²) / ((γ−1)M²+2) ]^(γ/(γ−1)) · [ (γ+1) / (2γM²−(γ−1)) ]^(1/(γ−1))
        /// </summary>
        public static double NormalShockPtRatio(double m1, double gamma = Gamma)
        {
            if (!GasState.IsFinite(m1) || m1 <= 1.0) return 1.0;
            double g = gamma, m2 = m1 * m1;
            double a = ((g + 1.0) * m2) / ((g - 1.0) * m2 + 2.0);
            double b = (g + 1.0) / (2.0 * g * m2 - (g - 1.0));
            double r = Math.Pow(a, g / (g - 1.0)) * Math.Pow(b, 1.0 / (g - 1.0));
            return GasState.IsFinite(r) ? Math.Min(Math.Max(r, 1e-6), 1.0) : 1.0;
        }

        /// <summary>正激波后的马赫数（永远 &lt;1）。M1&lt;=1 原样返回。</summary>
        public static double NormalShockMach(double m1, double gamma = Gamma)
        {
            if (!GasState.IsFinite(m1) || m1 <= 1.0) return Math.Max(m1, 0.0);
            double g = gamma, m2 = m1 * m1;
            double r = (1.0 + (g - 1.0) * 0.5 * m2) / (g * m2 - (g - 1.0) * 0.5);
            return GasState.IsFinite(r) ? Math.Sqrt(Math.Max(r, 0.0)) : 0.0;
        }

        /// <summary>
        /// 斜激波角随偏转角的关系（θ-β-M），返回"给定 β 时的偏转角"（度）。
        /// tanθ = 2·cotβ·(M²sin²β − 1) / (M²(γ + cos2β) + 2)
        /// </summary>
        private static double DeflectionForWaveAngle(double m1, double betaRad, double gamma)
        {
            double s = Math.Sin(betaRad), c2 = Math.Cos(2.0 * betaRad);
            double m2 = m1 * m1;
            double num = 2.0 * (c2 == 0.0 ? 0.0 : Math.Cos(betaRad) / Math.Sin(betaRad)) * (m2 * s * s - 1.0);
            double den = m2 * (gamma + c2) + 2.0;
            if (Math.Abs(den) < 1e-12) return 90.0;
            double t = num / den;
            return Math.Atan(t) * 180.0 / Math.PI;
        }

        /// <summary>
        /// 这道马赫下斜激波能撑住的最大偏转角（度）。超过它激波脱体（不进气道了），
        /// 进气道就是"未起动"——所以这个数是超声速进气道真正的门槛，不是拍脑袋的告警阈值。
        /// </summary>
        public static double MaxDeflectionDeg(double m1, double gamma = Gamma)
        {
            if (!GasState.IsFinite(m1) || m1 <= 1.0) return 0.0;
            double lo = Math.Asin(1.0 / m1) + 1e-4;
            double hi = Math.PI * 0.5 - 1e-6;
            double best = 0.0;
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                double prev = DeflectionForWaveAngle(m1, mid, gamma);
                double next = DeflectionForWaveAngle(m1, mid + 1e-4, gamma);
                if (next >= prev) { best = next; lo = mid; }
                else hi = mid;
            }
            return GasState.IsFinite(best) ? Math.Max(best, 0.0) : 0.0;
        }

        /// <summary>
        /// 斜激波（弱解）。偏转角超过 θmax 时返回 false —— 那时候激波已经脱体，
        /// 该走"未起动"分支，不该硬套弱解（会算出比正激波还高的恢复）。
        /// </summary>
        public static bool ObliqueShock(double m1, double deflectionDeg, out double waveAngleDeg,
            out double m2, out double ptRatio, double gamma = Gamma)
        {
            waveAngleDeg = 90.0; m2 = Math.Max(m1, 0.0); ptRatio = 1.0;
            if (!GasState.IsFinite(m1) || m1 <= 1.0 || !GasState.IsFinite(deflectionDeg) || deflectionDeg <= 1e-6)
                return true;                      // 没有转折就没有斜激波，损失 1
            double th = Math.Min(deflectionDeg, 89.0);
            if (th > MaxDeflectionDeg(m1, gamma)) return false;

            // 二分：f(β) = θ(β) − θ目标，弱解在 [asin(1/M), β峰] 上单调增
            double lo = Math.Asin(1.0 / m1) + 1e-6;
            double hi = Math.PI * 0.5 - 1e-6;
            double target = th;
            for (int i = 0; i < 120; i++)
            {
                double mid = 0.5 * (lo + hi);
                double d = DeflectionForWaveAngle(m1, mid, gamma);
                if (d < target) lo = mid; else hi = mid;
            }
            double beta = 0.5 * (lo + hi);
            double mn = m1 * Math.Sin(beta);
            if (mn <= 1.0) return false;          // 收到的不是斜激波
            waveAngleDeg = beta * 180.0 / Math.PI;
            double mn2 = NormalShockMach(mn, gamma);
            double sinBehind = Math.Sin(beta - th * Math.PI / 180.0);
            m2 = sinBehind > 1e-6 ? mn2 / sinBehind : mn2;
            ptRatio = NormalShockPtRatio(mn, gamma);
            return GasState.IsFinite(m2) && GasState.IsFinite(ptRatio);
        }

        /// <summary>
        /// 锥形激波的总压比。锥激波没有解析解（要解 Taylor-Maccoll 常微分方程），
        /// 这里用等偏角的二维斜激波解，损失按公开图表的偏差乘 1.25（锥形流场的损失更高）。
        /// 注释写死这一点，是为了将来谁拿了实测数据能直接换掉这一行。
        /// </summary>
        public static double ConeShockPtRatio(double m1, double halfAngleDeg, out double m2, double gamma = Gamma)
        {
            m2 = Math.Max(m1, 0.0);
            double beta, outM, pt;
            if (!ObliqueShock(m1, halfAngleDeg, out beta, out outM, out pt, gamma))
                return -1.0;                                   // 脱体，交给调用方算未起动
            m2 = outM;
            double loss = Math.Max(0.0, 1.0 - pt);
            double coneLoss = Math.Min(loss * 1.25, 0.95);
            return Math.Min(Math.Max(1.0 - coneLoss, 0.05), 1.0);
        }

        /// <summary>
        /// 压缩面（若干级斜激波 + 结尾一道正激波）的总压比。
        /// 进气道再起动之后气流总得在喉道附近被逼到亚声速，那道正激波跑不掉——
        /// 所以"多级斜激波"省下来的损失，最后要按出口马赫再交一次税。
        ///
        /// 返回 false = 某一级的偏转角超过该马赫的 θmax（激波脱体、未起动）。
        /// </summary>
        public static bool Compressions(double m1, double perStageDeflectionDeg, int stages,
            out double mAtThroat, out double ptRatio, double gamma = Gamma)
        {
            mAtThroat = Math.Max(m1, 1.0);
            ptRatio = 1.0;
            if (!GasState.IsFinite(m1)) return false;
            if (m1 <= 1.0) return true;
            double m = m1;
            int n = Math.Max(1, stages);
            for (int i = 0; i < n; i++)
            {
                double beta, mOut, pt;
                if (!ObliqueShock(m, perStageDeflectionDeg, out beta, out mOut, out pt, gamma))
                    return false;
                ptRatio *= pt;
                m = mOut;
                if (m <= 1.0) break;          // 已经亚声速，后面不用再斜了
            }
            mAtThroat = m;
            // 结尾正激波（喉道/唇口之后必须进亚声速）
            ptRatio *= NormalShockPtRatio(m, gamma);
            return GasState.IsFinite(ptRatio);
        }

        /// <summary>
        /// 按**总转角**均分的多级压缩。
        ///
        /// 一个"13° 两级斜板"指的是这两道斜面合起来把气流拐了 13°，
        /// 所以每级 6.5°，而不是每级都拐 13°（那总转角就是 26°，M2 上第二级直接脱体）。
        /// 第一版按"每级都转 half"算，M2 两级 13° 的第二级正好压在 θmax 上，
        /// 二分失败 → 整个压缩链返回 false → 退化成脱体正激波，
        /// 于是"斜板在 M2 反而比皮托还差"——这不是物理，是参数用错了量纲。
        /// </summary>
        public static bool CompressionsTotal(double m1, double totalDeflectionDeg, int stages,
            out double mAtThroat, out double ptRatio, double gamma = Gamma)
        {
            int n = Math.Max(1, stages);
            return Compressions(m1, totalDeflectionDeg / n, n, out mAtThroat, out ptRatio, gamma);
        }
    }
}