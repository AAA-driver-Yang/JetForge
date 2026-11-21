using System;

namespace JetForge.Core
{
    /// <summary>
    /// 只是一组数，不引 UnityEngine —— 这样命令行回归（net10，没有 Unity）也能算它。
    /// </summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0.0, 0.0, 0.0);

        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }

        public Vec3 Normalized
        {
            get
            {
                double l = Length;
                if (l < 1e-12) return Zero;
                return new Vec3(X / l, Y / l, Z / l);
            }
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator *(Vec3 a, double k) { return new Vec3(a.X * k, a.Y * k, a.Z * k); }

        public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y,
                            a.Z * b.X - a.X * b.Z,
                            a.X * b.Y - a.Y * b.X);
        }

        public override string ToString()
        {
            return string.Format("{0:F3}/{1:F3}/{2:F3}", X, Y, Z);
        }
    }

    /// <summary>
    /// 矢量喷口的几何：给定"推力轴、力作用点、控制轴、指令"，解出该往哪边偏、偏多少度。
    ///
    /// 为什么不直接抄原版 ModuleGimbal 的正负号：那套是针对它自己的
    /// gimbalTransform 和本地坐标系写的，我们的施力体是自己造的、喷口在 -Y、
    /// 零件还可能被旋转 180° 装在机尾。抄正负号会在"倒装"的零件上反向。
    ///
    /// 这里用的是力学的定义：
    ///     力矩 τ = r × F
    /// 想让飞行器绕 τ̂ 转，就得让侧向力 F 满足 r × F ∝ τ̂。
    /// 当 F ⟂ r 时，F̂ = τ̂ × r̂（因为 r̂ × (τ̂ × r̂) = τ̂）。
    /// 这样倒装、侧装、任意安装角都自动对，正负号不需要猜。
    /// </summary>
    public static class ThrustVector
    {
        /// <summary>控制指令为正时对应的力矩方向要不要取反。真机可调，留一个开关。</summary>
        public static bool InvertPitch = false;
        public static bool InvertYaw = false;
        public static bool InvertRoll = false;

        /// <summary>
        /// 偏转后的推力方向（单位向量）。两轴版：滚转按 0 处理，三个轴开关全开。
        /// </summary>
        /// <param name="thrustAxis">当前推力方向（已含安装角），单位向量。</param>
        /// <param name="r">力作用点相对质心的位置。</param>
        /// <param name="pitchAxis">俯仰力矩轴（一般是飞控参考系的 right）。</param>
        /// <param name="yawAxis">偏航力矩轴（一般是飞控参考系的 up）。</param>
        /// <param name="pitch">俯仰指令 -1~1。</param>
        /// <param name="yaw">偏航指令 -1~1。</param>
        /// <param name="maxDeg">最大偏转角（度）。</param>
        public static Vec3 Direction(Vec3 thrustAxis, Vec3 r, Vec3 pitchAxis, Vec3 yawAxis,
                                     double pitch, double yaw, double maxDeg, out double angleDeg)
        {
            return Direction(thrustAxis, r, pitchAxis, yawAxis, pitch, yaw, 0.0,
                             true, true, true, maxDeg, out angleDeg);
        }

        /// <summary>
        /// 三轴版：俯仰、偏航、滚转，外加三个方向的开关。
        ///
        /// 滚转在物理上是成立的，但**有条件**：力矩 τ = r × F 沿推力轴 â 的分量是
        ///     (r × F)·â = |F|·(r 在侧向力方向上的投影)
        /// 所以只有当喷口**不在质心中轴线上**（r 有横向分量）时，单台喷口才滚得动机身。
        /// 真机单发斜置喷口、或双发差动，都是这个道理。发动机正好装在轴线上时
        /// 侧向力穿过轴线、力臂为零，滚转开关就只能是"没效果"——这一点不假装。
        /// </summary>
        public static Vec3 Direction(Vec3 thrustAxis, Vec3 r, Vec3 pitchAxis, Vec3 yawAxis,
                                     double pitch, double yaw, double roll,
                                     bool allowPitch, bool allowYaw, bool allowRoll,
                                     double maxDeg, out double angleDeg)
        {
            angleDeg = 0.0;
            Vec3 axis = thrustAxis.Normalized;
            if (axis.Length < 0.5)
            {
                angleDeg = 0.0;
                return thrustAxis;
            }
            if (maxDeg <= 0.0) return axis;

            if (InvertPitch) pitch = -pitch;
            if (InvertYaw) yaw = -yaw;
            if (InvertRoll) roll = -roll;

            // 轴开关：关掉的那一轴当 0 处理。死区与限速率在 VectorCommand 里，
            // 这里只负责"这一轴听不听话"。
            if (!allowPitch) pitch = 0.0;
            if (!allowYaw) yaw = 0.0;
            if (!allowRoll) roll = 0.0;

            if (pitch > 1.0) pitch = 1.0;
            if (pitch < -1.0) pitch = -1.0;
            if (yaw > 1.0) yaw = 1.0;
            if (yaw < -1.0) yaw = -1.0;
            if (roll > 1.0) roll = 1.0;
            if (roll < -1.0) roll = -1.0;

            double mag = Math.Sqrt(pitch * pitch + yaw * yaw + roll * roll);
            if (mag < 1e-4)
            {
                angleDeg = 0.0;
                return axis;
            }
            // 指令超过 1 时按合向量截断，不要出现"斜着推比正着推偏得多"
            if (mag > 1.0) { pitch /= mag; yaw /= mag; roll /= mag; mag = 1.0; }

            // 想要的力矩方向。
            //
            // **俯仰要带负号**：KSP 里 `ctrlState.pitch` 为正 = 抬头（按 S）。而 Unity
            // 绕机体系 +X（right）正转是把机头压下去（`Quaternion.Euler(+θ,0,0)` 让
            // forward 转向 -up）。所以"抬头"对应的力矩是 **-right**。
            // 第一版这里写的是 `pitchAxis * pitch`，玩家报的"矢量喷口控制方向反了"
            // 就是它——力偏了、花瓣也偏了，但整机往反方向转。
            //
            // 偏航也带负号——**两轴同一个规则：指令为正，力矩沿该控制轴的负向。**
            // 上一版偏航这里没带负号（理由写的是"绕 +up 正转正好是右偏"），玩家飞出来
            // 报"偏航方向反了"。俯仰那一处早带负号，所以看着像"就偏航特殊"，其实
            // 不对称的那一个才是错的。这条是**实测**定的，不是推出来的：再有人想按
            // "正转=右偏"把它改回去，先让他拿一架真机飞一次。
            //
            // 滚转绕推力轴本身：τ = â * roll。侧向力要取在"垂直于轴、也垂直于力臂"的
            // 切向上，才给得出绕轴的力矩（推导见上）。喷口在轴线上时 r × â = 0，
            // 这一项自然解不出方向 → 不偏，这是正确结果，不是 bug。
            Vec3 tau = (pitchAxis.Normalized * (-pitch) + yawAxis.Normalized * (-yaw) + axis * roll).Normalized;
            if (tau.Length < 0.5)
            {
                angleDeg = 0.0;
                return axis;
            }

            // 力臂就是 r 本身，**不要**先把 r 里平行于推力轴的分量减掉。
            // 第一版减了，结果最常见的装机（喷口在中轴线上、质心也在中轴线上）里
            // r 与推力轴平行、rPerp 变成零，直接掉进兜底分支：力确实偏了 15°，
            // 但力矩方向是错的——测试里三条"力矩同号"全红。
            // 平行于轴线的那一段正是侧向力的力臂，减掉等于把杠杆锯了。
            //
            // 侧向力方向的物理解：要让 (r × F)·τ̂ 最大，解是 F̂ ∝ τ̂ × r
            // （三乘积： (r×F)·τ̂ = F·(τ̂ × r) ），再投到"垂直于推力轴"的平面上——
            // 喷口只能把气流往横向掰，不能凭空多出一股沿轴的气流。
            //
            // **但这一解在 τ̂ ∥ r 时退化**：叉积模长趋零，方向由末位噪声决定。
            // 火箭尾装发动机的常见姿态正是这种：推力轴、偏航轴、力臂三根都重合，
            // 偏航指令要的力矩恰好沿力臂——而 r × F 永远垂直于 r，这个力矩**本来
            // 就给不出来**。老版本于是每帧从 1e-9 量级的叉积里取方向，花瓣在偏航
            // 方向上抽搐（玩家报的那条）。现在按能换到多少力矩分权：
            //   能换到（sin 夹角 > 0.15）→ 听物理解；
            //   换不到 → 渐渐让位给"指令给的方向"（俯仰靠竖直控制轴、偏航靠横向），
            // 退化时方向稳在指令上，姿态抖 0.5° 也不会翻个儿。
            // 退化时听指令的方向。两根轴各差一个符号，理由与上面 τ 的两处负号是同一笔账：
            //   俯仰抬头 → 尾巴上的力要往下（-up）；偏航右偏 → 尾巴上的力要往左（-right）。
            // 第一版偏航这里写的是 `pitchAxis * yaw`（往右），玩家报的"偏航方向反了"就是它。
            // 只在退化装机（τ̂ ∥ r）上露头：那种姿态里没有物理解，方向全靠这一行。
            // 断言也要钉**方向**，不能只钉"偏了 15°"——绝对值相等，反了也通过。
            Vec3 latCmd = (yawAxis.Normalized * (-pitch) + pitchAxis.Normalized * (-yaw));
            latCmd = (latCmd - axis * Vec3.Dot(latCmd, axis)).Normalized;

            Vec3 lat = Vec3.Zero;
            double latMag = Math.Sqrt(pitch * pitch + yaw * yaw);
            if (latMag > 1e-9)
            {
                Vec3 u = Vec3.Cross(tau, r);
                Vec3 uLat = (u - axis * Vec3.Dot(u, axis)).Normalized;
                // 只有轴向力矩（纯滚转指令）时 τ̂ ∥ 轴、u 与轴叉乘为零：那是滚转的活，这里不管。
                if (uLat.Length > 0.5)
                {
                    double alpha = 1.0;
                    if (r.Length > 1e-9)
                    {
                        // |τ̂ × r| / |r| = sin(τ̂ 与 r 的夹角)，几何量，与力的量纲无关
                        double sinT = Math.Min(1.0, u.Length / r.Length);
                        const double Full = 0.15;
                        alpha = sinT >= Full ? 1.0 : sinT / Full;
                        alpha = alpha * alpha * (3.0 - 2.0 * alpha);   // 平滑过渡，别在门槛上打摆
                    }
                    else alpha = 0.0;

                    Vec3 dirPhys = uLat;
                    Vec3 dirCmd = latCmd;
                    if (dirCmd.Length > 0.5 && Vec3.Dot(dirPhys, dirCmd) < 0.0) dirCmd = dirCmd * -1.0;
                    if (dirCmd.Length > 0.5)
                        lat = (dirPhys * alpha + dirCmd * (1.0 - alpha)).Normalized * latMag;
                    else
                        lat = dirPhys * latMag;
                }
                else if (latCmd.Length > 0.5)
                {
                    // τ̂ 与力臂严格平行：叉积是零向量，物理上"这个力矩给不出来"。
                    // 气流还是要掰，方向听指令的——稳定、可复现，而且是玩家按的那个方向。
                    lat = latCmd * latMag;
                }
            }

            // 滚转：要的是绕推力轴的力矩，只有"垂直于轴的力臂"给得出来。
            // 喷口在轴线上时 rPerp = 0 → 这一项没有力，正确结果，不假装。
            if (Math.Abs(roll) > 1e-9)
            {
                Vec3 rPerp = r - axis * Vec3.Dot(r, axis);
                if (rPerp.Length > 1e-6)
                {
                    Vec3 latR = Vec3.Cross(axis, rPerp).Normalized;
                    if (Vec3.Dot(Vec3.Cross(r, latR), axis) * roll < 0.0) latR = latR * -1.0;
                    lat = lat + latR * Math.Abs(roll);
                }
            }

            lat = (lat - axis * Vec3.Dot(lat, axis)).Normalized;
            if (lat.Length < 0.5)
            {
                angleDeg = 0.0;
                return axis;
            }

            angleDeg = maxDeg * mag;
            return Tilt(axis, lat, angleDeg * Math.PI / 180.0);
        }

        /// <summary>
        /// 这次偏转换来的力矩，与飞控要的那个方向对不对得上：返回 0~1（1 = 正对）。
        ///
        /// 为什么要报这个而不是"偏没偏"：三根轴重合的装机（推力轴、偏航轴、力臂）
        /// 上气流照样掰得过去，可 r × F 永远垂直于 r，玩家要的那个力矩**根本给不出来**，
        /// 实际得到的是绕另一根轴的力矩——按偏航，机身往别的方向转。
        /// 这不是 bug，是几何；但状态栏得说得出，不然玩家只能以为模组坏了。
        /// </summary>
        public static double TorqueAlign(Vec3 r, Vec3 thrustAxis, Vec3 dir, Vec3 goalTorque)
        {
            Vec3 axis = thrustAxis.Normalized;
            Vec3 lat = (dir.Normalized - axis * Vec3.Dot(dir.Normalized, axis)).Normalized;
            if (lat.Length < 0.5) return 0.0;
            Vec3 g = goalTorque.Normalized;
            if (g.Length < 0.5) return 0.0;
            Vec3 t = Vec3.Cross(r, lat);
            if (t.Length < 1e-9) return 0.0;
            double d = Vec3.Dot(t.Normalized, g);
            return d < 0.0 ? 0.0 : d;
        }

        /// <summary>把 axis 朝 lat 方向倾斜 angle（弧度）。</summary>
        private static Vec3 Tilt(Vec3 axis, Vec3 lat, double angle)
        {
            return (axis * Math.Cos(angle) + lat * Math.Sin(angle)).Normalized;
        }

        /// <summary>
        /// 给日志/自检用的一句话。d 是偏转前后方向的夹角（度）。
        /// </summary>
        public static string Describe(Vec3 before, Vec3 after, Vec3 r, Vec3 pitchAxis, Vec3 yawAxis)
        {
            Vec3 b = before.Normalized;
            Vec3 a = after.Normalized;
            double cos = Math.Max(-1.0, Math.Min(1.0, Vec3.Dot(b, a)));
            double deg = Math.Acos(cos) * 180.0 / Math.PI;
            Vec3 f = a;                     // 推力方向即力方向
            Vec3 tau = Vec3.Cross(r, f);    // 实际产生的力矩
            return string.Format("偏转 {0:F2}°｜推力 {1}｜力矩 {2}",
                                 deg, a.ToString(), tau.ToString());
        }
    }
}