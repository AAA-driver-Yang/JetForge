using System;

namespace JetForge.Core
{
    /// <summary>
    /// 可调喷管花瓣的几何与开度——**一处算、两处用**。
    ///
    /// 以前有两份互相独立的实现：网格里写死 `tilt = Lerp(-24°, +10°, open)`，
    /// 羽流那边写死 `PetalExitFactor(open) = 0.88 + 0.16·open`。这两条曲线从第一次
    /// 提交起就不一致（网格在 open=0 时是 0.853 倍出口半径，羽流以为是 0.88），
    /// 只是差得不多没人发现。真正的代价是：改网格的倾角范围，羽流不会跟着改，
    /// 于是"喷管开了但羽流还卡在旧直径上"——玩家报的穿模，有一半是这么来的。
    ///
    /// 现在唯一的几何是：叶片铰在 `0.975·rExit` 的出口唇上，长度按 rExit 定，
    /// 倾角在 [TiltMin, TiltMax] 之间转。任何想知道"这一刻出口多大"的地方
    /// 都调 <see cref="ExitFactor"/>，不允许再自己写一条。
    /// </summary>
    public static class NozzleVisual
    {
        /// <summary>收到底时的倾角：叶片往里扣，喉道最小（干态慢车）。</summary>
        public const double TiltMin = -26.0;
        /// <summary>开到最大时的倾角：叶片接近水平略外张（满加力）。</summary>
        public const double TiltMax = 20.0;
        /// <summary>铰点所在的出口唇半径系数（与网格同一常数）。</summary>
        public const double HingeFactor = 0.975;
        /// <summary>叶片长度按出口半径的比例。取 0.48 是因为要容得下 +36% 的面积：
        /// 半径要长 16.6%，而倾角从 0° 转到 20° 在 0.48 的长度上正好给出这 16.4%。</summary>
        public const double LengthFactor = 0.48;
        /// <summary>小喷管的下限（米）。半径 0.1 m 的喷口上叶片不能只有 4.8 cm。</summary>
        public const double MinLength = 0.055;

        public static double PetalLength(double rExit)
        {
            if (rExit <= 0.0) return MinLength;
            return Math.Max(MinLength, LengthFactor * rExit);
        }

        /// <summary>带长度系数的叶片长度（米）。长度系数夹在 0.6~1.8：再短撑不开，再长根部弯矩压不住。</summary>
        public static double PetalLength(double rExit, double lenFrac)
        {
            double f = lenFrac > 0.0 ? lenFrac : 1.0;
            if (f < 0.6) f = 0.6;
            if (f > 1.8) f = 1.8;
            if (rExit <= 0.0) return MinLength;
            return Math.Max(MinLength, LengthFactor * rExit * f);
        }

        /// <summary>
        /// 实际花瓣数：0 = 自动 16（老口径）。10~32 之间取偶——
        /// 奇数片会让"对面的缝"错位，真机不做奇数；小于 8 时每片缝大得能看穿。
        /// </summary>
        public static int CountOf(EngineDesignData d)
        {
            int n = d != null ? d.PetalCount : 0;
            if (n <= 0) return 16;
            if (n < 8) n = 8;
            if (n > 32) n = 32;
            if ((n & 1) == 1) n -= 1;      // 取偶：向上取偶会多一片，宁可少
            return n;
        }

        /// <summary>开度 0~1 → 倾角（度）。</summary>
        public static double TiltOf(double openFrac)
        {
            double o = openFrac < 0.0 ? 0.0 : (openFrac > 1.0 ? 1.0 : openFrac);
            return TiltMin + (TiltMax - TiltMin) * o;
        }

        public static double OpenOfTilt(double tiltDeg)
        {
            return Clamp01((tiltDeg - TiltMin) / (TiltMax - TiltMin));
        }

        /// <summary>某个开度下的叶片末端半径相对出口半径的倍数。</summary>
        public static double ExitFactor(double openFrac, double rExit)
        {
            return ExitFactor(openFrac, rExit, 1.0);
        }

        /// <summary>带叶片长度系数的出口倍数：网格把叶片拉长了，羽流也得跟着长，否则穿模。</summary>
        public static double ExitFactor(double openFrac, double rExit, double lenFrac)
        {
            double len = PetalLength(rExit, lenFrac);
            double tilt = TiltOf(openFrac) * Math.PI / 180.0;
            if (rExit <= 1e-6) return HingeFactor;
            return HingeFactor + len * Math.Sin(tilt) / rExit;
        }

        /// <summary>想把末端半径做成 rTarget 时该用多大开度。夹在 [0,1]，不做外推。</summary>
        public static double OpenForRadius(double rExit, double rTarget)
        {
            return OpenForRadius(rExit, rTarget, 1.0);
        }

        /// <summary>带叶片长度系数的反解：叶片长了，撑到同一个半径要的倾角就小。</summary>
        public static double OpenForRadius(double rExit, double rTarget, double lenFrac)
        {
            if (rExit <= 1e-6) return 0.0;
            double len = PetalLength(rExit, lenFrac);
            double s = (rTarget / rExit - HingeFactor) * rExit / len;
            if (s < -1.0) s = -1.0;
            if (s > 1.0) s = 1.0;
            double tilt = Math.Asin(s) * 180.0 / Math.PI;
            return OpenOfTilt(tilt);
        }

        /// <summary>
        /// 这一刻花瓣该张多大。
        ///
        /// 两条来源，乘在一起：
        ///  · <b>加力那一份是真的</b>：`NozzleSchedule.ThroatArea` 给出的 A8 放大多少，
        ///    半径就按 √ 放大多少——面积 ∝ 半径²，所以 36% 的面积 = 16.6% 的半径。
        ///    这一份与求解器用的是同一个数，不是另拍一条曲线。
        ///  · <b>转速那一份现在也是真的</b>：`NozzleSchedule.DryFactor` 与求解器共用同一个
        ///    函数（求解器用它收 A8，这里用它收半径 = √面积）。以前这里写的是
        ///    `0.90 + 0.10*油门`，那是照着油门画的曲线——求解器根本不随油门改 A8，
        ///    于是"网格张开多少"和"物理上喉道多大"各说各话。现在两边吃同一个数。
        ///  · 传进来的 `throttle` 现在实际是**换算转速**（N/√θ）：引擎与羽流两侧
        ///    都先问 `ModuleJetForgeEngine.ConvertedSpeedNow`（§151/§152），
        ///    拿不到引擎时才退回油门——那种情况下面这条曲线只是近似，不是等价。
        /// </summary>
        public static double RadiusScale(double throttle, double abFrac)
        {
            return RadiusScale(throttle, abFrac, true);
        }

        /// <summary>带"这个喷管能不能动"的版本：固定收敛喷管没有作动筒，半径恒等于基准。</summary>
        public static double RadiusScale(double throttle, double abFrac, bool canVary)
        {
            double f = abFrac;
            if (f < 0.0) f = 0.0;
            if (f > 1.0) f = 1.0;
            if (!canVary) f = 0.0;
            double t = throttle;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            // DryFactor 是**面积**因子（求解器拿它乘 A8），网格要的是半径，所以取 √。
            double abArea = 1.0 + NozzleSchedule.AbAreaGain * f;
            double dry = Math.Sqrt(NozzleSchedule.DryFactor(t, canVary));
            return Math.Sqrt(abArea) * dry;
        }

        /// <summary>设计点（干态满油门）的基准半径：叶片略内扣一点点，看着才是"收敛"。</summary>
        public static double NominalFactor { get { return HingeFactor; } }

        public static double OpenFor(EngineDesignData d, double throttle, double abFrac)
        {
            return OpenFor(1.0, throttle, abFrac, false);
        }

        /// <summary>名义出口半径下的开度。收扩喷管会略大一些（它的出口本来就更粗）。</summary>
        public static double OpenFor(double rExit, double throttle, double abFrac, bool cd)
        {
            return OpenFor(rExit, throttle, abFrac, cd, true);
        }

        /// <summary>名义出口半径下的开度，附带"这个喷管能不能动"。
        /// 固定收敛喷管传 false：它既不会随油门收，也不会随加力放。</summary>
        public static double OpenFor(double rExit, double throttle, double abFrac, bool cd, bool canVary)
        {
            return OpenFor(rExit, throttle, abFrac, cd, canVary, 1.0);
        }

        /// <summary>同上，外加叶片长度系数（花瓣被设计成更长时，羽流也得跟着走同一条几何）。</summary>
        public static double OpenFor(double rExit, double throttle, double abFrac, bool cd, bool canVary, double lenFrac)
        {
            double target = rExit * NominalFactor * RadiusScale(throttle, abFrac, canVary) * (cd ? 1.04 : 1.0);
            return OpenForRadius(rExit, target, lenFrac);
        }

        /// <summary>给自检/状态栏用：一句话把"加力开与不开"的几何差别说清楚。</summary>
        public static string Describe(double rExit)
        {
            double f0 = ExitFactor(0.0, rExit);
            double f1 = ExitFactor(1.0, rExit);
            double ab = Math.Sqrt(1.0 + NozzleSchedule.AbAreaGain);
            return string.Format(
                "花瓣：铰在 {0:F3}·r，长度 {1:F3} m｜倾角 {2:F0}°~{3:F0}°｜出口 {4:F3}→{5:F3} rExit｜满加力按 √(1+{6:F2}) = {7:F3} 倍张开",
                HingeFactor, PetalLength(rExit), TiltMin, TiltMax, f0, f1,
                NozzleSchedule.AbAreaGain, ab);
        }

        private static double Clamp01(double v)
        {
            if (v < 0.0) return 0.0;
            if (v > 1.0) return 1.0;
            return v;
        }
    }
}