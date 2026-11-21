using System;

namespace JetForge.Core
{
    /// <summary>
    /// 把飞控指令磨成喷口能用的三个数：死区、限幅、限速率。
    ///
    /// 为什么需要这一层：`vessel.ctrlState.yaw` 不是干净的玩家输入，它是
    /// "玩家输入 + SAS 修正 + 气动配平"叠加出来的，稳态下会在 ±0.02 上下抖。
    /// 以前这个抖直接进 <see cref="ThrustVector.Direction"/> 当偏航指令使，
    /// 于是喷口每帧都在很小地来回摆——玩家报的"偏航抖动"就是它：
    /// 抖动幅度不大，但花瓣一直颤，而且侧向力一直在给机尾换向，手感是"糊的"。
    ///
    /// 三条处理，顺序有意义：
    /// ① 死区：|指令| &lt; <see cref="Deadzone"/> 直接当 0——SAS 噪声落在里面，被吃掉；
    /// ② 轴开关：玩家可以把不想要的轴关掉（偏航抖就关偏航）；
    /// ③ 限速率：真机作动筒有最大角速度，指令不能一帧从 0 跳到 1。
    ///
    /// 死区放在限速率**之前**：先判"这是不是噪声"，再决定要不要动；
    /// 反过来会先把噪声积分成一个小斜坡，等于死区白设。
    ///
    /// 抽成纯类是为了命令行能断言——抖动这种东西，只有把"输入 → 输出"变成
    /// 无 Unity 的纯函数，才验得动"给它一串噪声，输出会不会跟着跳"。
    /// </summary>
    public sealed class VectorCommand
    {
        /// <summary>死区：小于这个数的指令当 0。SAS 稳态噪声约 ±0.02。</summary>
        public const double Deadzone = 0.04;

        /// <summary>最大指令速率（每秒）。真机作动筒全行程约 0.4~0.6 s。</summary>
        public const double RatePerSecond = 2.2;

        /// <summary>输出的一阶平滑时间常数（秒）。限速率之后再抹一下，去残余锯齿。</summary>
        public const double TauSeconds = 0.06;

        /// <summary>当前（已磨过的）指令，-1~1。</summary>
        public double Pitch, Yaw, Roll;

        /// <summary>最近一次输入被判成噪声（死区内）的轴，供状态栏说明用。</summary>
        public bool PitchDead, YawDead, RollDead;

        /// <summary>平滑后的目标（还没过限速率），限速率要拿它跟当前输出比。</summary>
        private double _lagPitch, _lagYaw, _lagRoll;

        public void Reset()
        {
            Pitch = 0.0; Yaw = 0.0; Roll = 0.0;
            _lagPitch = 0.0; _lagYaw = 0.0; _lagRoll = 0.0;
            PitchDead = false; YawDead = false; RollDead = false;
        }

        private static double Sanitize(double v)
        {
            if (double.IsNaN(v)) return 0.0;
            if (v > 1.0) return 1.0;
            if (v < -1.0) return -1.0;
            return v;
        }

        /// <summary>死区 + 轴开关：返回这一轴的目标指令（0 表示这轴不动）。</summary>
        private static double Shape(double raw, bool allow, out bool dead)
        {
            double v = Sanitize(raw);
            dead = false;
            if (!allow) { dead = Math.Abs(v) > Deadzone; return 0.0; }
            if (Math.Abs(v) < Deadzone) { dead = true; return 0.0; }
            // 死区外把台阶抹平：0.04 处从 0 连续长出来，不然一出死区就是一次 4% 的跳变。
            double sign = v < 0.0 ? -1.0 : 1.0;
            double mag = Math.Abs(v);
            double eased = (mag - Deadzone) / (1.0 - Deadzone);
            if (eased < 0.0) eased = 0.0;
            return sign * eased;
        }

        /// <summary>
        /// 走一步。`dt` 是这一帧的步长；`allowX` 是三个轴开关。
        /// 三个轴都关掉时输出会平缓回 0（不是硬切），喷口慢慢回中。
        /// </summary>
        public void Step(double rawPitch, double rawYaw, double rawRoll, double dt,
                         bool allowPitch, bool allowYaw, bool allowRoll)
        {
            bool dp, dy, dr;
            double tp = Shape(rawPitch, allowPitch, out dp);
            double ty = Shape(rawYaw, allowYaw, out dy);
            double tr = Shape(rawRoll, allowRoll, out dr);
            PitchDead = dp; YawDead = dy; RollDead = dr;

            if (double.IsNaN(dt) || dt <= 0.0)
            {
                Pitch = tp; Yaw = ty; Roll = tr;
                _lagPitch = tp; _lagYaw = ty; _lagRoll = tr;
                return;
            }

            // 先把目标抹一道（去锯齿），**再**限速率。
            // 反过来（限速率之后再朝目标拉）会让平滑项直接绕开速率上限：
            // 第一帧 0.0367 的速率台阶，被 0.24 的平滑系数一拉就成了 0.27。
            double k = 1.0 - Math.Exp(-dt / Math.Max(TauSeconds, 1e-4));
            _lagPitch += (tp - _lagPitch) * k;
            _lagYaw += (ty - _lagYaw) * k;
            _lagRoll += (tr - _lagRoll) * k;

            Pitch = RateLimit(Pitch, _lagPitch, dt);
            Yaw = RateLimit(Yaw, _lagYaw, dt);
            Roll = RateLimit(Roll, _lagRoll, dt);
        }

        /// <summary>限速率：一帧最多走 <see cref="RatePerSecond"/> × dt。</summary>
        private static double RateLimit(double current, double target, double dt)
        {
            double maxStep = RatePerSecond * dt;
            double d = target - current;
            if (d > maxStep) d = maxStep;
            if (d < -maxStep) d = -maxStep;
            return current + d;
        }

        /// <summary>状态栏用：三个轴各自现在是多少，哪几个被判成噪声。</summary>
        public string Describe()
        {
            return string.Format("俯仰 {0:F2}{1}｜偏航 {2:F2}{3}｜滚转 {4:F2}{5}",
                Pitch, PitchDead ? "(噪声)" : "",
                Yaw, YawDead ? "(噪声)" : "",
                Roll, RollDead ? "(噪声)" : "");
        }
    }
}