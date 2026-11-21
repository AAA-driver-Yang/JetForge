using System;

namespace JetForge.Core
{
    /// <summary>
    /// 油门 → 喷油量 →（解算器反查）目标转速。两种控制方式共用这一层。
    ///
    /// **两种口径（这是模型的门面，别写反）**：
    ///
    ///   · **没有控制系统**：杆位直接就是油量。推多少烧多少，超温超转全靠飞行员自己躲。
    ///     杆到底 = 最低可用喷油量（慢车），这就是"再少就留不住火"的那一档。
    ///   · **有控制系统**：杆位是**目标推力**。控制器自己决定烧多少油去凑这个推力，
    ///     油多油少只是手段，转速与推力是那个手段的结果。
    ///
    /// 为什么要把"油门"和"转速"之间的这层显式写出来：以前油门直接就是转速目标，
    /// 于是"慢车"成了"推力占满油门多少"这种间接口径，玩家想表达"我就给这么多油"却说不出话。
    ///
    /// 关键点：**转速永远不是这里给的数**。这里只给油量，转速由 <see cref="FuelSchedule"/>
    /// 反查平衡点、再交给转子功率积分逼近。低于表底油量时反查返回 0，
    /// 转子就按惯量一路往下溜——掉多快、掉到哪儿，全由解算器每帧回答。
    ///
    /// **为什么推力指令要限速**（§270）：玩家把杆一把到底，真机上的推力也不会在一帧之内跳完——
    /// 那要看转子加速、涡轮前温度、压气机喘振裕度。以前的目标推力跟着杆位当帧就跟上了，
    /// 于是"推杆 → 推力指令满格 → 燃油猛加"这三步并成了一步，屏幕上看到的就是推力阶跃。
    /// 现在指令按 <see cref="TargetSlewPerSec"/> 爬，剩下的交给转子与限幅。
    /// </summary>
    public sealed class FuelLoop
    {
        /// <summary>推力指令的爬升率（满油门推力量/秒）：从慢车到满推约 2 秒。</summary>
        public const double TargetSlewPerSec = 0.50;

        /// <summary>比例增益：归一化推力误差 → 归一化油量。负误差直接压油量，不等积分。</summary>
        public const double Kp = 0.80;

        /// <summary>积分增益（1/s）：消掉比例项留下的稳态余差。</summary>
        public const double Ki = 1.50;

        /// <summary>积分量的上限。有了它，俯冲加速时积分不会攒到"松杆半天还满油"。</summary>
        public const double IntegralClamp = 0.30;

        /// <summary>满油门油量相对于慢车的倍数地板。表建不起来时的兜底。</summary>
        public const double FallbackFuelRatio = 8.0;

        /// <summary>有控：杆位是目标推力。无控：杆位是油量。</summary>
        public bool Fadec;

        /// <summary>最低可用喷油量（kg/s）——慢车档，来自求解器（`DesignConstants.IdleFuelFlow`）。</summary>
        public double IdleFuel;

        /// <summary>满转速稳态油量（kg/s），来自 <see cref="FuelSchedule.MaxFuel"/>。</summary>
        public double MaxFuel;

        /// <summary>归一化油量 0~1（这一帧真正给出的那一份）。</summary>
        public double Trim;

        /// <summary>这一帧实际给出的油量（kg/s）。</summary>
        public double Fuel;

        /// <summary>给油低于最低可用量——不是错误，是"火可能留不住"的如实标记。</summary>
        public bool BelowIdle;

        /// <summary>反查出来的目标转速（0 = 这个油量维持不了任何转速）。</summary>
        public double TargetSpool;

        /// <summary>限速之后的杆位 0~1（有控时它对应目标推力）。</summary>
        public double ThrottleRamp;

        /// <summary>目标推力（N）。只在有控时是真值。</summary>
        public double TargetThrustN;

        /// <summary>上一帧实测推力（N），只做读数。</summary>
        public double ActualThrustN;

        /// <summary>积分量（归一化油量）。</summary>
        public double Integral;

        /// <summary>油量已经顶到 0 或满格——控制器给不出更多了。</summary>
        public bool Saturated;

        /// <summary>这一帧的限温/限转/喘振闸在起作用：积分不再往加大的方向攒。</summary>
        public bool Held;

        /// <summary>第一帧用杆位直接对齐，不要让玩家先在跑道上等两秒。</summary>
        public bool Primed;

        public void Reset()
        {
            // 初始油量落在慢车档：刚点火就该是慢车，而不是满油。
            Trim = MaxFuel > 0.0 ? Clamp(IdleFuel / MaxFuel, 0.0, 1.0) : 0.3;
            Fuel = IdleFuel;
            BelowIdle = false;
            TargetSpool = 0.0;
            ThrottleRamp = Trim;
            TargetThrustN = 0.0;
            ActualThrustN = 0.0;
            Integral = 0.0;
            Saturated = false;
            Held = false;
            Primed = false;
        }

        /// <summary>
        /// 推进一帧。
        /// </summary>
        /// <param name="throttle">抬底后的杆位 0~1。有控时它是目标推力占满推力的比例。</param>
        /// <param name="dt">这一帧的物理步长（秒）。</param>
        /// <param name="sched">油量-转速表；为 null 或无效时退回"杆位即转速"的老口径。</param>
        /// <param name="demandThrustN">（保留）外部推力指令；为 0 时按杆位算。</param>
        /// <param name="actualThrustN">上一帧的实际推力（N）。只在使用有控时读。</param>
        /// <param name="fullThrustN">满油门推力（N），把推力误差归一化用。</param>
        /// <param name="limiterActive">限温/限转/喘振闸这一帧是否在压油门（防积分饱和用）。</param>
        /// <returns>目标转速 0~1；返回 0 表示"这点油维持不了转速，让它自己掉"。</returns>
        public double Step(double throttle, double dt, FuelSchedule sched,
                           double demandThrustN = 0.0, double actualThrustN = 0.0, double fullThrustN = 0.0,
                           bool limiterActive = false)
        {
            if (double.IsNaN(throttle)) throttle = 0.0;
            throttle = Clamp(throttle, 0.0, 1.0);
            if (double.IsNaN(dt) || dt < 0.0) dt = 0.0;

            double maxFuel = MaxFuel > 0.0 ? MaxFuel : (IdleFuel > 0.0 ? IdleFuel * FallbackFuelRatio : 0.0);
            if (maxFuel < IdleFuel) maxFuel = IdleFuel;
            double idleFrac = maxFuel > 0.0 ? Clamp(IdleFuel / maxFuel, 0.0, 1.0) : 0.0;

            double fuel;
            Saturated = false;
            Held = false;
            ActualThrustN = Math.Max(actualThrustN, 0.0);

            if (!Fadec)
            {
                // 无控：杆位就是油量，杆到底就是慢车油量（不是 0）。
                fuel = IdleFuel + (maxFuel - IdleFuel) * throttle;
                Trim = maxFuel > IdleFuel ? (fuel - IdleFuel) / (maxFuel - IdleFuel) : 0.0;
                ThrottleRamp = throttle;
                TargetThrustN = fullThrustN > 0.0 ? fullThrustN * Trim : 0.0;
                Integral = 0.0;
            }
            else
            {
                // ---- 有控：杆位是目标推力 ----
                //
                // 杆位映射到 [慢车推力, 满推力]：杆放在底上不是"要 0 推力"（那等于
                // 让地面待机的发动机自己饿死），而是"要慢车推力"——真机的控制器
                // 也只在慢车与最大之间选点。
                double nmax = Math.Max(fullThrustN, 1.0);
                double loN = nmax * idleFrac;
                double wantThrust = loN + Math.Max(nmax - loN, 0.0) * throttle;
                double wantRamp = throttle;

                // ① 指令限速：杆一把到底，推力指令也得爬过去。这一步就是"防止推力突变"。
                if (!Primed)
                {
                    ThrottleRamp = wantRamp;
                    TargetThrustN = demandThrustN > 0.0 ? demandThrustN : wantThrust;
                    Primed = true;
                }
                else
                {
                    double slew = TargetSlewPerSec * dt;
                    ThrottleRamp += Clamp(wantRamp - ThrottleRamp, -slew, slew);
                    double stepMax = TargetSlewPerSec * nmax * dt;
                    double goal = demandThrustN > 0.0 ? demandThrustN : wantThrust;
                    TargetThrustN += Clamp(goal - TargetThrustN, -stepMax, stepMax);
                }

                // ② PI：误差按满推力归一化。比例项负责"立刻少烧一点"，积分负责"最后那一两个百分点"。
                double err = (TargetThrustN - ActualThrustN) / nmax;
                double pTerm = Kp * err;

                // ③ 抗积分饱和：油量顶到边、或者限温限转正在压油门时，别继续往同一个方向攒积分。
                double uRaw = idleFrac + (1.0 - idleFrac) * ThrottleRamp + pTerm + Integral;
                bool satHigh = uRaw >= 1.0;
                bool satLow = uRaw <= 0.0;
                Held = limiterActive && err > 0.0;
                if (!satHigh && !satLow && !Held)
                {
                    Integral = Clamp(Integral + Ki * err * dt, -IntegralClamp, IntegralClamp);
                    uRaw = idleFrac + (1.0 - idleFrac) * ThrottleRamp + pTerm + Integral;
                }
                Saturated = uRaw >= 1.0 || uRaw <= 0.0;

                double u = Clamp(uRaw, 0.0, 1.0);
                Trim = u;
                fuel = maxFuel * u;
            }

            Fuel = fuel;
            BelowIdle = IdleFuel > 0.0 && fuel < IdleFuel * 0.999;

            if (sched == null || !sched.Valid) { TargetSpool = throttle; return TargetSpool; }

            TargetSpool = sched.SpoolFor(fuel);
            return TargetSpool;
        }

        /// <summary>一行状态文字，面板与自检都读它。</summary>
        public string Describe()
        {
            if (!Fadec)
            {
                return string.Format(Loc.T("{0}｜油量 {1:F3} kg/s（慢车 {2:F3}）｜目标转速 {3:F1}%{4}", "{0} | fuel {1:F3} kg/s (idle {2:F3}) | target speed {3:F1}%{4}"),
                    Loc.T("无控 杆位即油量", "no control: throttle feeds fuel"),
                    Fuel, IdleFuel, 100.0 * TargetSpool,
                    BelowIdle ? Loc.T("｜低于最低油量", " | below minimum fuel flow") : "");
            }
            return string.Format(Loc.T("有控 目标推力 {0:F1} kN / 实测 {1:F1} kN｜油量 {2:F3} kg/s（满 {3:F3}）｜目标转速 {4:F1}%{5}{6}", "controlled: target thrust {0:F1} kN / actual {1:F1} kN | fuel {2:F3} kg/s (max {3:F3}) | target speed {4:F1}%{5}{6}"),
                TargetThrustN / 1000.0, ActualThrustN / 1000.0, Fuel, MaxFuel, 100.0 * TargetSpool,
                BelowIdle ? Loc.T("｜低于最低油量", " | below minimum fuel flow") : "",
                Saturated ? Loc.T("｜油量已经到头", " | fuel at its limit") : (Held ? Loc.T("｜限温限转在压油门", " | temperature/speed limiter is holding") : ""));
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}