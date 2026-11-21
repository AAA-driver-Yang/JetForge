using System;

namespace JetForge.Core
{
    /// <summary>
    /// 熄火／掉到慢车以下那一档的残余推力。
    ///
    /// 历史上这里走过两个极端，两次都被玩家抓出来：
    /// ① 一帧归零——推力表"啪"一下没了，玩家以为是 mod 崩了；
    /// ② 单独给推力一个 0.22 s 的指数衰减——推力是溜下去的，但那是"火灭后余气排完"的
    ///    时间尺度，跟转子的十几秒完全脱节。玩家看到的是"推力没了、转速还在 90%"，
    ///    也就是 Flyout 里绝不会出现的那一幕。
    ///
    /// 现在按物理来：火没了，**推力只剩转子还能带给气流的那点增压**。
    /// 定压加热停了，能拿出来的功只剩转子储的动能；转速往下溜的过程中，
    /// 流量 ∝ ω，排气速度也 ∝ ω（增压比 ∝ ω²、喷速 ∝ √增压比），于是
    ///
    ///     F ∝ ω²
    ///
    /// 这一条的好处是它与转速表同源：转速掉到风车，推力自然归零；
    /// 转速还有一半，推力就还剩四分之一。玩家看到的两根指针终于说的是同一件事。
    ///
    /// 之所以要抽成纯函数：它是"该不该一帧抹平"这个争论的唯一可断言处，
    /// 命令行回归（没有 Unity）也要能验。
    /// </summary>
    public static class CoastThrust
    {
        /// <summary>推力随转速的幂次。流量 ∝ ω、排气速度 ∝ ω → 推力 ∝ ω²。</summary>
        public const double SpoolExponent = 2.0;

        /// <summary>低于这个推力（kN）就当没有了——再往下玩家也看不见，留着还白喂油。</summary>
        public const double FloorKN = 0.02;

        /// <summary>低于这个转速比例就当转子停了（风车档以下不再算推力）。</summary>
        public const double MinSpoolFraction = 0.02;

        /// <summary>旧口径：单指数衰减的时间常数。现在只给"余气排空"那条腿用，不再决定整条曲线。</summary>
        public const double TauSeconds = 0.22;

        /// <summary>
        /// 着火时最后一帧的推力，按"现在转速 / 熄火那一刻转速"的平方折。
        ///
        /// 边界：熄火前转速 ≤ 0（本来就没转）→ 直接 0；转速比夹在 0~1，
        /// 不给"转速反倒涨了"这种情况凭空多出推力（那是解算器抖，不是物理）。
        /// </summary>
        public static double Slide(double thrustAtCutKN, double spoolAtCut, double spoolNow)
        {
            if (double.IsNaN(thrustAtCutKN) || double.IsNaN(spoolAtCut) || double.IsNaN(spoolNow)) return 0.0;
            if (thrustAtCutKN <= 0.0) return 0.0;
            if (spoolAtCut <= MinSpoolFraction) return 0.0;
            if (spoolNow <= MinSpoolFraction) return 0.0;

            double v = thrustAtCutKN * Ratio(spoolAtCut, spoolNow);
            return v < FloorKN ? 0.0 : v;
        }

        /// <summary>
        /// 与推力同步衰减的油耗（t/s）。KSP 的推力只能由「油量 × Isp × g」算出来，
        /// 不给油就一点推力也显示不出来，所以残余推力必须带一股残余油。
        /// 按推力同一条曲线折，保证"SFC 不突然变成 0 或者无穷"。
        ///
        /// **地板用的是油量自己的量级，不是推力的**：推力地板 0.02 kN 比正常油耗
        /// （零点几 g/s）大两个数量级，套过来会把残余油直接判成 0——那就是"有推力、
        /// 不烧油"的永动机读数。
        /// </summary>
        public static double SlideFuel(double fuelAtCutTps, double spoolAtCut, double spoolNow)
        {
            if (double.IsNaN(fuelAtCutTps) || double.IsNaN(spoolAtCut) || double.IsNaN(spoolNow)) return 0.0;
            if (fuelAtCutTps <= 0.0) return 0.0;
            if (spoolAtCut <= MinSpoolFraction) return 0.0;
            if (spoolNow <= MinSpoolFraction) return 0.0;
            return fuelAtCutTps * Ratio(spoolAtCut, spoolNow);
        }

        /// <summary>转速比夹在 0~1 再取幂：转速反倒涨了不许凭空多出推力。</summary>
        private static double Ratio(double spoolAtCut, double spoolNow)
        {
            double ratio = spoolNow / spoolAtCut;
            if (ratio > 1.0) ratio = 1.0;
            if (ratio < 0.0) ratio = 0.0;
            return Math.Pow(ratio, SpoolExponent);
        }

        /// <summary>
        /// 旧口径：单指数衰减。留着是给"火灭后头 0.2 s 余气排空"这类短尺度用的，
        /// 整条惰转曲线请用 <see cref="Slide"/>。
        /// </summary>
        public static double Step(double prevKN, double dt)
        {
            if (double.IsNaN(prevKN) || prevKN <= 0.0) return 0.0;
            if (double.IsNaN(dt) || dt <= 0.0) return prevKN;
            double v = prevKN * Math.Exp(-dt / TauSeconds);
            return v < FloorKN ? 0.0 : v;
        }

        public static string Describe(double fromKN)
        {
            return string.Format(Loc.T("残余推力 {0:F2} kN 起，按 (转速/熄火转速)^{1:F1} 随转速一起溜，低于 {2:F2} kN 归零", "Residual thrust starts at {0:F2} kN and falls as (speed/flameout speed)^{1:F1} with spool, reaching zero below {2:F2} kN"),
                fromKN, SpoolExponent, FloorKN);
        }
    }
}