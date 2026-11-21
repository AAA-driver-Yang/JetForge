using System;

namespace JetForge.Core
{
    /// <summary>
    /// 发动机控制系统档次。按年代解锁：1940s 只有"无控"，1990s 之后才有全权限数字。
    ///
    /// **数值不许动 0/1/2**：存档里 `control` 存的就是这个整数，改一下老飞机的控制律就换了。
    /// 后来补的两档（`Mechanical = 3`、`Analog = 4`）排在后面，靠 `Rank` 表达能力高低——
    /// 枚举数值顺序与能力顺序**不一致**，凡是要比"谁更强"的地方都必须走 `Rank`。
    /// </summary>
    public enum ControlKind
    {
        None = 0,        // 无控：油门直接给燃油，飞行员/玩家推多少就是多少
        Hydro = 1,       // 机械液压：限转、限温、加速度限幅；不认识喘振裕度
        Fadec = 2,       // 全权限数字：闭环限温限转、加速度调度、主动抑制喘振
        Mechanical = 3,  // 纯机械调速器：只有离心配重压转速，没有热电偶反馈（更早的年代）
        Analog = 4       // 模拟式 FADEC：限温限转接近数字机，但没有喘振抑制与超转切油
    }

    /// <summary>
    /// 控制系统档次的能力排序见 <see cref="ControlSystem.Rank"/>。
    /// </summary>
    public sealed class ControlRuntime
    {
        /// <summary>上一帧真正送进解算器的油门。限幅要从"上一次到过哪里"算，不是从指令算。</summary>
        public double Command = 1.0;
        public bool Primed;

        public bool LimitingSpeed, LimitingTemp, LimitingSurge, RateLimited;
        /// <summary>超转保护正在切油（锁存到转速掉回 100% 以下）。</summary>
        public bool OverspeedTripped;
        /// <summary>超转保护的保持时间（秒）。切油不是一帧的事：收油后转子还要几秒才降下来。</summary>
        public double OverspeedHold;
        public double Trim;
        public string Note = Loc.T("无控", "No control");

        public void Reset()
        {
            Command = 1.0;
            Primed = false;
            LimitingSpeed = LimitingTemp = LimitingSurge = RateLimited = false;
            OverspeedTripped = false;
            OverspeedHold = 0.0;
            Trim = 0.0;
            Note = Loc.T("无控", "No control");
        }
    }

    /// <summary>
    /// 发动机控制系统。
    ///
    /// 这里最容易写错的是"控制系统到底在管什么"。它不产生推力，它只做一件事：
    /// **在玩家的油门指令之外，再按当下的转速、涡轮前温度、喘振风险，决定这一刻允许烧多少油。**
    /// 无控的发动机没有这层，所以猛推油门会直接压进喘振区、超温也不会自己收油；
    /// 有控的发动机把这三道闸串在油门后面，代价是响应变慢（这就是"年代差异"的手感来源）。
    ///
    /// 上面这段是"闸"，还有一道**口径**（§270）：无控时杆位就是油量；
    /// 有控时杆位是**目标推力**，油量由 <see cref="FuelLoop"/> 闭环去找。
    /// 两道东西不冲突：口径决定"杆位是什么意思"，闸决定"这一刻最多允许烧多少"。
    /// </summary>
    public static class ControlSystem
    {
        // 加速度限幅：液压系统反应慢，数字系统快。
        // 数值不是拍脑袋：0.9/s 意味着从慢车推满要 1.1 s 的"指令"时间，
        // 加上转子本身的惯性（SpoolDynamics 给的 3.6 s），总响应约 4.7 s，和 J79 实测同量级。
        public const double HydroSlewPerSec = 0.90;
        public const double FadecSlewPerSec = 2.20;

        // 收油比推油快得多，这一条以前是"对称的"。
        //
        // 真机收油几乎不看加速度调度：飞行员松杆，燃油计量阀立刻关小，几秒内转子就掉下来。
        // 推油才要看喘振裕度、要看涡轮前温度，所以慢。对称的限幅造成的是"松油门像踩刹车、
        // 推油门也像踩刹车"——两头都钝，手感不像喷气机，像一艘船。
        // 液压系统收油快一点（1.4/s），数字系统更快（3.5/s），但都不能一帧收到底：
        // 燃油阀突变会让燃烧室直接吹熄，那才是"一松杆就熄火"的来路。
        public const double HydroDecelPerSec = 1.40;
        public const double FadecDecelPerSec = 3.50;

        // 超转保护：数字控制独有的一道硬闸。
        // 液压系统靠离心配重/机械调速器，它只能在油门上"压住"，不会切油；
        // FADEC 读到转速过线直接砍油，砍到转子回来为止。门槛放在 102%：
        // 100% 是设计转速，真机允许短时间 101~102%，再往上就是叶片飞出来的事了。
        public const double OverspeedTripPct = 102.0;
        public const double OverspeedCutFrac = 0.45;   // 触发时把指令砍到 45%
        public const double OverspeedHoldSec = 0.60;   // 锁存这么久，防止一帧一帧地抖

        // 限温/限转的削油增益：超多少就收多少（每 1% 超量收 3~4% 油门）
        public const double TempGain = 3.0;
        public const double SpeedGain = 4.0;

        // 喘振抑制门槛：风险超过它就别再加车了（数字控制才会做这件事）
        public const double SurgeHoldRisk = 0.80;
        public const double SurgeCutRisk = 1.05;   // 已经喘起来了：直接收 15%

        // 纯机械调速器的速率：离心配重直接带动油量杆，没有放大器，只能慢给。
        public const double MechanicalSlewPerSec = 0.40;
        public const double MechanicalDecelPerSec = 0.70;
        // 模拟式 FADEC：分立元件时代，比液压快，但没有数字机的总线调度。
        public const double AnalogSlewPerSec = 2.20;
        public const double AnalogDecelPerSec = 3.00;

        /// <summary>
        /// 能力排序：0 无控 → 1 机械 → 2 液压 → 3 模拟 FADEC → 4 数字 FADEC。
        /// 判"设计里选的档位够不够年代"、判某道闸该不该有，一律用它——**不许拿枚举数值比大小**。
        /// </summary>
        public static int Rank(ControlKind k)
        {
            switch (k)
            {
                case ControlKind.Mechanical: return 1;
                case ControlKind.Hydro: return 2;
                case ControlKind.Analog: return 3;
                case ControlKind.Fadec: return 4;
                default: return 0;
            }
        }

        /// <summary>推油速率（每秒）。机械最慢、数字最快。</summary>
        public static double SlewOf(ControlKind k)
        {
            switch (k)
            {
                case ControlKind.Mechanical: return MechanicalSlewPerSec;
                case ControlKind.Hydro: return HydroSlewPerSec;
                case ControlKind.Analog: return AnalogSlewPerSec;
                case ControlKind.Fadec: return FadecSlewPerSec;
                default: return HydroSlewPerSec;
            }
        }

        /// <summary>收油速率（每秒）。收油永远比推油快，但都不能一帧到底。</summary>
        public static double DecelOf(ControlKind k)
        {
            switch (k)
            {
                case ControlKind.Mechanical: return MechanicalDecelPerSec;
                case ControlKind.Hydro: return HydroDecelPerSec;
                case ControlKind.Analog: return AnalogDecelPerSec;
                case ControlKind.Fadec: return FadecDecelPerSec;
                default: return HydroDecelPerSec;
            }
        }

        public static string KindName(ControlKind k)
        {
            switch (k)
            {
                case ControlKind.Mechanical: return Loc.T("纯机械调速器", "Mechanical governor");
                case ControlKind.Hydro: return Loc.T("机械液压", "Hydromechanical");
                case ControlKind.Analog: return Loc.T("模拟式 FADEC", "Analog FADEC");
                case ControlKind.Fadec: return Loc.T("全权限数字", "Full-authority digital");
                default: return Loc.T("无控", "No control");
            }
        }

        /// <summary>
        /// 这个年代最多能做出来什么。
        ///
        /// `TechLevel.Names` 只有 1940s/1960s/1970s/1990s/2010s 五档，所以年代上限也只落在这五档上；
        /// 「纯机械调速器」不设年代上限（1940s 之后都能做），它是**手动降级**才有的选项——
        /// 玩家想让一台 2010s 的机器过 1950s 的日子，就得自己选它。
        /// 模拟式 FADEC 对应 1970s：那代机器（F100、JT9D 后期）已经有电子式燃油/涡轮温度调节器，
        /// 但还是分立元件、没有全权限数字总线；真正"全权限数字"是 1990s 以后的事。
        /// </summary>
        public static ControlKind Allowed(TechLevel tech)
        {
            string n = tech != null && tech.Name != null ? tech.Name : "1970s";
            if (n.StartsWith("1940")) return ControlKind.None;
            if (n.StartsWith("1960")) return ControlKind.Hydro;
            if (n.StartsWith("1970")) return ControlKind.Analog;
            if (n.StartsWith("1990")) return ControlKind.Fadec;
            return ControlKind.Fadec;
        }

        /// <summary>设计里写的控制系统，按年代封顶。写超了不会报错，但判据会说话。</summary>
        public static ControlKind Effective(EngineDesignData d)
        {
            if (d == null) return ControlKind.None;
            ControlKind want = d.Control;
            ControlKind max = Allowed(d.Tech);
            return Rank(want) > Rank(max) ? max : want;
        }

        // 这儿曾有个 `OutOfEra(d)`：`d.Control > Allowed(d.Tech)`。判据里 `Criteria.CheckControl`
// 早就自己写成同一个比较（还顺手给出人话解释），这个副本永远没人调，删掉省得两处各走各的。

        /// <summary>
        /// 走一步控制。返回真正送进解算器的油门。
        ///
        /// 参数是"上一帧的结果"（nPct/t4/risk），这在真实系统里也是一样的：
        /// 调节器读的是传感器，传感器永远比指令慢一帧。
        /// </summary>
        public static double Step(ControlRuntime rt, ControlKind kind, double cmd,
                                  double spool, double nPct, double t4, double t4Limit,
                                  double risk, double dt)
        {
            if (rt == null) return Clamp01(cmd);
            rt.LimitingSpeed = rt.LimitingTemp = rt.LimitingSurge = rt.RateLimited = false;
            rt.Trim = 0.0;
            cmd = Clamp01(cmd);

            if (kind == ControlKind.None)
            {
                // 无控：油门就是油量。没有限幅、没有限温、没有喘振抑制。
                // 这不是"简化"，这是 1940~50 年代真机的样子：飞行员推杆就是推油。
                rt.Command = cmd;
                rt.Primed = true;
                rt.Note = Loc.T("无控（油门直接给油）", "No control (throttle feeds fuel directly)");
                return cmd;
            }

            double dtSafe = dt > 1e-6 ? dt : 0.02;
            double prev = rt.Primed ? rt.Command : cmd;
            double c = cmd;

            // 这一档到底有几道闸。按 Rank 判，不按枚举数值——数值顺序是存档兼容的产物。
            int rk = Rank(kind);
            bool canTemp = rk >= 2;    // 机械调速器没有热电偶反馈：它只认转速
            bool canSurge = rk >= 4;   // 喘振抑制要数字机：读裕度、算工作点、主动压油门
            bool hardTrip = rk >= 4;   // 超转切油要数字机：模拟电路不会"砍油再放开"

            // ① 加速度限幅：从"上一次到过的地方"往指令走，一帧不超过 rate·dt
            // 加速与减速用两个不同的速率：推油要看喘振裕度与 T4，所以慢；收油只是关阀。
            double want = cmd - prev;
            double rate = want >= 0.0 ? SlewOf(kind) : DecelOf(kind);
            double maxStep = rate * dtSafe;

            // ①b 软限：**在真正碰到限值之前就先把加速度收掉**。
            // 只靠下面那两条"超过了再减"的比例闸，稳态误差是跑不掉的：
            // 一帧加多少、一帧减多少会停在 TempGain·(t4/limit − band) = rate·dt 的地方，
            // 实测就停在限值上方 12 K、连着 9 秒——数字控制顶着红线飞，那还不如没有。
            double tempBand = rk >= 3 ? 0.995 : 0.98;
            double nLimit = rk >= 4 ? 100.0 : (rk >= 3 ? 99.5 : 98.5);
            if (canTemp && t4Limit > 1.0)
            {
                double softT = (t4Limit * tempBand - t4) / Math.Max(1.0, t4Limit * 0.06);
                if (softT < 1.0) maxStep *= softT > 0.0 ? softT : 0.0;
            }
            {
                double softN = (nLimit - nPct) / 4.0;      // 最后 4 个百分点的转速
                if (softN < 1.0) maxStep *= softN > 0.0 ? softN : 0.0;
            }

            if (Math.Abs(want) > maxStep)
            {
                c = prev + Math.Sign(want) * maxStep;
                rt.RateLimited = true;
            }
            // ② 喘振抑制：只剩数字控制会做。
            // 液压系统靠机械放气阀与"飞行员别猛推"来避免失速，它自己不知道喘振裕度；
            // 模拟式 FADEC 能读转速与温度，但算不出裕度——那需要一个在线工作点模型。
            if (canSurge)
            {
                if (risk > SurgeCutRisk)
                {
                    c -= 0.15;
                    rt.LimitingSurge = true;
                }
                else if (risk > SurgeHoldRisk && c > prev)
                {
                    c = prev;                      // 别再加车，等裕度回来
                    rt.LimitingSurge = true;
                }
            }

            // ③ 限转：超过红线就收油（液压系统也有，靠离心配重或液压调速器）
            if (nPct > nLimit)
            {
                c -= SpeedGain * (nPct - nLimit) / 100.0;
                rt.LimitingSpeed = true;
            }

            // ③b 超转保护（数字控制独有）：转速过 102% 直接砍油，锁存 0.6 s。
            //
            // 上面那条比例限转是"贴着红线慢慢挪"，它挡不住突然的超转——
            // 高空高速、来流总温很高时换算转速不高而物理转速能冲到 110%，
            // 比例闸每帧只减一点点，转子照样往前冲。真机这道闸是硬的：切油，别讲道理。
            if (hardTrip)
            {
                if (nPct > OverspeedTripPct)
                {
                    rt.OverspeedTripped = true;
                    rt.OverspeedHold = OverspeedHoldSec;
                }
                else if (rt.OverspeedHold > 0.0)
                {
                    rt.OverspeedHold = Math.Max(0.0, rt.OverspeedHold - dtSafe);
                    // 锁存期满且转速已经落回红线以内才解除，否则继续切
                    if (rt.OverspeedHold <= 0.0 && nPct < nLimit) rt.OverspeedTripped = false;
                }
                else if (nPct < nLimit)
                {
                    rt.OverspeedTripped = false;
                }

                if (rt.OverspeedTripped)
                {
                    c = Math.Min(c, c * OverspeedCutFrac);
                    rt.LimitingSpeed = true;
                }
            }

            // ④ 限温：涡轮前温度逼近限值就收油。
            // 机械调速器没有这道闸：它没有热电偶反馈，"别超温"全靠飞行员看表和记性。
            // 液压系统的限温是"热电偶+机械放大"的粗活，所以给它留 2% 的余地；
            // 模拟/数字系统会真的把 T4 闭环顶在限值上（0.5% 余地）。
            if (canTemp && t4Limit > 1.0 && t4 > t4Limit * tempBand)
            {
                c -= TempGain * (t4 / t4Limit - tempBand);
                rt.LimitingTemp = true;
            }

            c = Clamp01(c);
            rt.Trim = c - cmd;
            rt.Command = c;
            rt.Primed = true;

            if (rt.OverspeedTripped) rt.Note = Loc.T("超转保护：切油（转速过 ", "Overspeed: cutting fuel (speed past ") + OverspeedTripPct.ToString("F0") + "%）";
            else if (rt.LimitingSurge) rt.Note = Loc.T("喘振抑制：压住油门", "Surge suppression: holding the throttle back");
            else if (rt.LimitingTemp) rt.Note = Loc.T("限温：收到 T4 上限", "Over-temp: capped at the T4 limit");
            else if (rt.LimitingSpeed) rt.Note = Loc.T("限转：收到 ", "Over-speed: capped at ") + nLimit.ToString("F1") + "%";
            else if (rt.RateLimited) rt.Note = string.Format(Loc.T("加速度限幅（{0:F2}/s 加／{1:F2}/s 减）", "Acceleration limited ({0:F2}/s up, {1:F2}/s down)"),
                rate, DecelOf(kind));
            else rt.Note = KindName(kind) + Loc.T("：跟随油门", ": following the throttle");

            return c;
        }

        public static string Describe(ControlRuntime rt, ControlKind kind)
        {
            if (rt == null) return Loc.T("控制系统：没有状态", "Control system: no state");
            if (kind == ControlKind.None) return Loc.T("控制系统：无控（油门直给油，无限制）", "Control system: none (throttle feeds fuel directly, no limits)");
            return string.Format(Loc.T("控制系统：{0}｜油门 {1:F3}（削 {2:+0.00;-0.00;0}）｜{3}", "Control system: {0} | throttle {1:F3} (trimmed {2:+0.00;-0.00;0}) | {3}"),
                KindName(kind), rt.Command, rt.Trim, rt.Note);
        }

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
            if (v < 0.0) return 0.0;
            if (v > 1.0) return 1.0;
            return v;
        }
    }
}