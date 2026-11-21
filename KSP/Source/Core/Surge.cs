using System;

namespace JetForge.Core
{
    /// <summary>
    /// 喘振的实时模拟。
    ///
    /// `Defects` 那一层管的是"设计本身有缺陷，于是它时不时犯病"——静态的。
    /// 这一层管的是另一件事：**你把它逼到了工作线外面**。真机上最常见的两条路，
    /// 一是猛推/猛收油门（转子还没跟上，压气机压比先上去了，流量不够），
    /// 二是进气畸变（超音速时进气道出口流场不匀，某几片叶片先失速）。
    /// 设计裕度小的时候，同一手油门的余量更少，所以"剧烈操控"和"设计缺陷"
    /// 在这一个模型里是同一根轴上的两段，不是两套逻辑。
    ///
    /// 三条取舍：
    /// 1) 不用随机数：喘振用相位计时器，"砰—砰"的节奏是确定的，命令行能断言；
    /// 2) 不碰解算器：喘振时推力按拍子掉、排气温度上冲、叶片磨损失掉一点永久推力，
    ///    但压气机特性线本身不改——把喘振塞进解算器会让它自己越算越喘，也不可复现；
    /// 3) 必须能收住：推力掉到底就该松油门，模型给一条明确的"活路"，而不是无限抖。
    /// </summary>
    public sealed class SurgeRuntime
    {
        /// <summary>正在喘。</summary>
        public bool Active;
        /// <summary>本次喘振剩余时间（秒）。</summary>
        public double Timer;
        /// <summary>"砰—砰"的相位（秒）。</summary>
        public double Phase;
        /// <summary>两次喘振之间的静默（秒），防止一松一推就无限续杯。</summary>
        public double Cooldown;
        /// <summary>当前风险（1.0 = 触发线）。</summary>
        public double Risk;
        /// <summary>这一趟里最高的一次风险。</summary>
        public double PeakRisk;
        /// <summary>喘了几次。</summary>
        public int Events;
        /// <summary>本帧推力乘子（1 = 不喘）。</summary>
        public double ThrustFactor = 1.0;
        /// <summary>本帧排气温度上冲（K）。</summary>
        public double EgtKick;
        /// <summary>压气机叶片磨损 0~1（喘一次多一点，永久）。</summary>
        public double Wear;
        /// <summary>本帧的说明串。</summary>
        public string Note = "";

        public void Reset()
        {
            Active = false; Timer = 0.0; Phase = 0.0; Cooldown = 0.0;
            Risk = 0.0; PeakRisk = 0.0; Events = 0;
            ThrustFactor = 1.0; EgtKick = 0.0; Wear = 0.0; Note = "";
        }
    }

    public static class Surge
    {
        /// <summary>猛推油门的分界：每秒油门变化超过这个数就算"剧烈操控"。</summary>
        public const double SlamRate = 1.10;
        /// <summary>风险到 1.0 就喘。</summary>
        public const double TriggerRisk = 1.00;
        /// <summary>慢车附近不喘：低压比、大裕度，真机在那儿怎么抖都不喘。</summary>
        public const double MinThrottle = 0.55;
        /// <summary>两次喘振之间的静默（秒）。</summary>
        public const double CooldownSeconds = 1.80;
        /// <summary>一次喘振多长（秒），按风险加长。</summary>
        public const double MinSeconds = 0.60;
        public const double MaxSeconds = 1.60;
        /// <summary>"砰—砰"的周期与占空比。</summary>
        public const double BangPeriod = 0.22;
        public const double BangDuty = 0.45;
        /// <summary>喘的那一下推力还剩多少 / 喘过去的间隙恢复多少。</summary>
        public const double BangLow = 0.35;
        public const double BangHigh = 0.92;
        /// <summary>喘振瞬间的排气温度上冲（K）。真机喘振时涡轮进口温度会窜一下，这是它伤涡轮的方式。</summary>
        public const double EgtKickK = 150.0;
        /// <summary>每喘一次磨掉多少推力（0~1 的磨损量）。</summary>
        public const double WearPerEvent = 0.012;
        /// <summary>磨满之后永久掉多少推力。</summary>
        public const double WearDerate = 0.10;
        /// <summary>
        /// 畸变基线：进气道模型给出的最小畸变（0.02），低于它不算额外风险。
        /// 拿"超出基线的部分"计风险，是为了让没填进气道参数的机器读数一动不动。
        /// </summary>
        public const double DistortionBaseline = 0.02;
        /// <summary>
        /// 畸变→风险的权重。取值不是凑的：进气道给的畸变在 M1.6 约 0.23，
        /// 超出基线 0.21，乘 0.57 ≈ 0.12，和原来那句 `(M-1.2)*0.30` 的代理项刚好打平；
        /// 换到真实畸变以后，M1.0~1.2 之间不再凭空冒风险（那里本来畸变就小），
        /// 而弯管的额外代价这回真的能算进去。
        /// </summary>
        public const double DistortionWeight = 0.57;

        private static double Clamp01(double v) { return v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v); }

        /// <summary>油门变化率（每秒）。dt 太小时不放大噪声。</summary>
        public static double Rate(double prev, double now, double dt)
        {
            if (dt <= 1e-4) return 0.0;
            return Math.Abs(now - prev) / dt;
        }

        /// <summary>
        /// 畸变那一项。给了真实畸变（≥0）就用它减基线，没给（NaN）才退回按马赫估的代理项——
        /// 代理项是"还不知道进气道长什么样"时的占位，不是第二条物理。
        /// </summary>
        private static double PoorTerm(double mach, double distortion)
        {
            if (double.IsNaN(distortion) || distortion < 0.0)
                return mach > 1.2 ? (mach - 1.2) * 0.30 : 0.0;
            double excess = distortion - DistortionBaseline;
            return excess > 0.0 ? excess * DistortionWeight : 0.0;
        }

        /// <summary>
        /// 风险 = 设计裕度不足 + 猛推（转子滞后）+ 超音速进气畸变。
        /// 三项都是"把工作点往喘振边界推"，所以在同一根轴上相加。
        /// </summary>
        public static double RiskOf(double throttle, double rate, double spoolFrac, double mach, double designRisk)
        {
            return RiskOf(throttle, rate, spoolFrac, mach, designRisk, double.NaN);
        }

        /// <summary>
        /// 同上，但用进气道实际给出的畸变（`IntakeSolver.DistortionOf`）代替马赫代理项。
        /// distortion 传 NaN 时行为与旧版逐位相同。
        /// </summary>
        public static double RiskOf(double throttle, double rate, double spoolFrac, double mach, double designRisk,
                                    double distortion)
        {
            double slam = Clamp01(rate / SlamRate);
            // 命令跑在转速前面多少：猛推的那一瞬间 throttle 已经上去了，spoolFrac 还没动。
            double lag = Clamp01(throttle - spoolFrac - 0.02);
            double poor = PoorTerm(mach, distortion);
            double baseRisk = designRisk < 0.0 ? 0.0 : (designRisk > 1.0 ? 1.0 : designRisk);
            // 权重是先算再定的：设计裕度满值(1.0)的机器稳态巡航就挂在 1.00 附近，
            // 稍一推就喘；健康机(0.10)要真猛推（变化率 >1.1/s 且转速落后 0.7）才够。
            // 三条权重这么排的理由：裕度是设计给定的底数（全额兑现），
            // 猛推是玩家当场造出来的（0.70），转速滞后只是猛推的伴随现象（0.45），
            // 不能重复计功，否则一次猛推会被算三遍。
            return baseRisk * 1.00 + 0.70 * slam + 0.45 * lag + poor;
        }

        /// <summary>推进一帧。designRisk 来自 `Defects.Read(...).Surge`（0~1）。</summary>
        public static void Step(SurgeRuntime rt, double throttle, double rate, double spoolFrac,
                                double mach, double designRisk, double dt)
        {
            Step(rt, throttle, rate, spoolFrac, mach, designRisk, dt, double.NaN);
        }

        /// <summary>推进一帧，畸变由进气道给（distortion 传 NaN = 旧行为）。</summary>
        public static void Step(SurgeRuntime rt, double throttle, double rate, double spoolFrac,
                                double mach, double designRisk, double dt, double distortion)
        {
            if (rt == null) return;
            double h = dt > 0.0 ? dt : 0.0;

            rt.ThrustFactor = 1.0;
            rt.EgtKick = 0.0;
            rt.Risk = RiskOf(throttle, rate, spoolFrac, mach, designRisk, distortion);
            if (rt.Risk > rt.PeakRisk) rt.PeakRisk = rt.Risk;

            if (rt.Cooldown > 0.0) rt.Cooldown -= h;

            if (!rt.Active)
            {
                bool canLight = throttle > MinThrottle && rt.Cooldown <= 0.0;
                if (canLight && rt.Risk >= TriggerRisk)
                {
                    rt.Active = true;
                    rt.Events++;
                    rt.Phase = 0.0;
                    double over = rt.Risk - TriggerRisk;
                    rt.Timer = Math.Min(MaxSeconds, MinSeconds + 0.55 * over);
                    rt.Wear = Math.Min(1.0, rt.Wear + WearPerEvent * (1.0 + over));
                    rt.Note = string.Format(Loc.T("喘振（第 {0} 次）", "Surge (event {0})"), rt.Events);
                }
                else
                {
                    // 没喘：只在风险确实接近边界时提一句，别让状态栏常年挂着警告。
                    rt.Note = rt.Risk > 0.80 ? Loc.T("接近喘振边界", "near the surge line") : "";
                    ApplyWear(rt);
                    return;
                }
            }

            // 喘着：按拍子掉推力，间隙回一点。温度上冲只在那一下。
            rt.Timer -= h;
            rt.Phase += h;
            bool bang = (rt.Phase % BangPeriod) < BangPeriod * BangDuty;
            rt.ThrustFactor = bang ? BangLow : BangHigh;
            rt.EgtKick = bang ? EgtKickK : EgtKickK * 0.25;
            rt.Note = string.Format(Loc.T("喘振（第 {0} 次，{1:F1} s）", "Surge (event {0}, {1:F1} s)"), rt.Events, Math.Max(0.0, rt.Timer));

            if (rt.Timer <= 0.0)
            {
                rt.Active = false;
                rt.Timer = 0.0;
                rt.Cooldown = CooldownSeconds;
                rt.Note = Loc.T("喘振刚停", "surge just ended");
            }

            ApplyWear(rt);
        }

        private static void ApplyWear(SurgeRuntime rt)
        {
            if (rt.Wear > 0.0) rt.ThrustFactor *= 1.0 - WearDerate * rt.Wear;
        }

        /// <summary>
        /// PAW 上那一行：玩家只需要知道"现在喘没喘、叶片还完好吗"。
        /// 风险数值与峰值留给日志——那是调参用的，挂在面板上只会让人问"0.83 是什么概念"。
        /// </summary>
        public static string Short(SurgeRuntime rt)
        {
            if (rt == null) return Loc.T("未启用", "not enabled");
            if (rt.Active) return string.Format(Loc.T("喘振中（第 {0} 次）", "Surging (event {0})"), rt.Events);
            if (rt.Wear > 0.001) return string.Format(Loc.T("平稳｜叶片磨损 {0:P0}", "Steady | blade wear {0:P0}"), rt.Wear);
            return rt.Risk > 0.80 ? Loc.T("接近喘振边界", "near the surge line") : Loc.T("平稳", "steady");
        }

        /// <summary>状态栏/日志那句。</summary>
        public static string Describe(SurgeRuntime rt)
        {
            if (rt == null) return Loc.T("喘振：未启用", "Surge: not enabled");
            string wear = rt.Wear > 0.001
                ? string.Format(Loc.T("叶片磨损 {0:P0}（永久掉 {1:P1}）", "blade wear {0:P0} (permanently loses {1:P1})"), rt.Wear, WearDerate * rt.Wear)
                : Loc.T("叶片完好", "blades intact");
            return string.Format(Loc.T("喘振：风险 {0:F2}｜{1}｜已喘 {2} 次｜峰值风险 {3:F2}｜{4}",
                "Surge: risk {0:F2} | {1} | {2} events | peak risk {3:F2} | {4}"),
                rt.Risk, rt.Active ? Loc.T("喘振中", "surging") : (rt.Risk > 0.80 ? Loc.T("接近边界", "near the line") : Loc.T("平稳", "steady")),
                rt.Events, rt.PeakRisk, wear);
        }

        /// <summary>风险从哪来（给自检与调试用）。</summary>
        public static string Explain(double throttle, double rate, double spoolFrac, double mach, double designRisk)
        {
            return Explain(throttle, rate, spoolFrac, mach, designRisk, double.NaN);
        }

        /// <summary>风险从哪来，畸变由进气道给（NaN = 用马赫代理项）。</summary>
        public static string Explain(double throttle, double rate, double spoolFrac, double mach, double designRisk,
                                     double distortion)
        {
            double slam = Clamp01(rate / SlamRate);
            double lag = Clamp01(throttle - spoolFrac - 0.02);
            double poor = PoorTerm(mach, distortion);
            string src = double.IsNaN(distortion) || distortion < 0.0 ? "马赫代理" : string.Format("畸变 {0:F3}", distortion);
            return string.Format("油门 {0:P0}｜变化率 {1:F2}/s（猛推项 {2:F2}）｜转速滞后 {3:F2}｜{4}（项 {5:F2}）｜设计裕度 {6:F2}｜合计 {7:F2}",
                throttle, rate, slam, lag, src, poor, designRisk,
                RiskOf(throttle, rate, spoolFrac, mach, designRisk, distortion));
        }
    }
}