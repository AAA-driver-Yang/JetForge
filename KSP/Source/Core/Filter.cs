using System;

namespace JetForge.Core
{
    /// <summary>
    /// 一阶滞后（指数平滑）。玩家报的"数据抖动、声音跟着抖"，
    /// 根子不在循环里——稳态下同一个油门解出来的数是同一个数；
    /// 抖的是输入（高度/马赫每帧都带着刚体求解的微小振荡）和
    /// 求解器的 30 Hz 闸门（同一个值重复两次再跳一格）。
    ///
    /// 这里只做两件事：把输入抹平、把输出抹平。**不动物理**：
    /// 衰减率、喘振拍子、加力瞬态都还是原来的时间常数，
    /// 只是显示与音效用一条时间常数 0.2 秒的管道去读它。
    /// </summary>
    public sealed class Lag
    {
        private double _v;
        private bool _init;

        public double Value { get { return _v; } }
        public bool Primed { get { return _init; } }

        public void Reset()
        {
            _init = false;
            _v = 0.0;
        }

        /// <summary>喂一个目标值，返回抹平之后的值。第一次直接对齐（别让玩家看见从 0 爬上来）。</summary>
        public double Step(double target, double dt, double tau)
        {
            if (double.IsNaN(target) || double.IsInfinity(target)) return _v;
            if (!_init)
            {
                _init = true;
                _v = target;
                return _v;
            }
            if (dt <= 0.0) return _v;
            if (tau <= 1e-5)
            {
                _v = target;
                return _v;
            }
            double k = 1.0 - Math.Exp(-dt / tau);
            _v += (target - _v) * k;
            return _v;
        }
    }

    /// <summary>
    /// 死区保持：变化小于 band 就不动。给"没必要每帧重写"的地方用
    /// （材质颜色、羽流强度、音效音量）。
    /// </summary>
    public static class Held
    {
        public static double Of(double target, ref double held, double band, bool force)
        {
            if (force || Math.Abs(target - held) >= band) held = target;
            return held;
        }
    }
}