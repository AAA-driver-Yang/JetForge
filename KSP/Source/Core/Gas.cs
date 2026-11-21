using System;

namespace JetForge.Core
{
    /// <summary>
    /// 气体物性与标准大气。全部 SI 单位（K、Pa、m、kg/s、N）。
    /// 系数与 Python 原型 jetforge.py 逐行一致，改一边必须改另一边。
    /// </summary>
    public static class Gas
    {
        public const double R = 287.05;      // J/(kg·K)
        public const double G0 = 9.80665;
        public const double CpAir = 1004.5;
        public const double GammaAir = 1.4;
        public const double LhvJet = 43.0e6; // J/kg —— 煤油
        public const double NprCrit = 1.893; // 收敛喷管临界压比 (γ=1.4)
        public const double T0 = 288.15;
        public const double P0 = 101325.0;

        public static double Gamma(double t, double far = 0.0)
        {
            double g = GammaAir - 0.1 * Math.Max((t - 300.0) * 0.0005, 0.0) * (1.0 + far);
            return Math.Min(GammaAir, Math.Max(1.1, g));
        }

        public static double Cp(double t, double far = 0.0)
        {
            double cp = CpAir + 250.0 * Math.Max((t - 300.0) * 0.0005, 0.0) * (1.0 + 10.0 * far);
            return Math.Min(1404.5, Math.Max(CpAir, cp));
        }

        /// <summary>ISA 标准大气，返回 (压力 Pa, 温度 K)。</summary>
        public static void Isa(double altM, out double p, out double t)
        {
            if (altM < 11000.0)
            {
                t = 288.15 - 0.0065 * altM;
                p = 101325.0 * Math.Pow(t / 288.15, G0 / (0.0065 * R));
            }
            else
            {
                t = 216.65;
                p = 22632.06 * Math.Exp(-G0 * (altM - 11000.0) / (R * t));
            }
        }

        public static double SoundSpeed(double t)
        {
            return Math.Sqrt(GammaAir * R * t);
        }

        /// <summary>静参数 → 总参数。</summary>
        public static void Total(double p, double t, double mach, out double pt, out double tt)
        {
            tt = t * (1.0 + 0.5 * (GammaAir - 1.0) * mach * mach);
            pt = p * Math.Pow(tt / t, GammaAir / (GammaAir - 1.0));
        }

        /// <summary>壅塞质量流量（mach=1 时该面积的通过量）。</summary>
        public static double ChokedFlow(double area, double pt, double tt, double gamma = 0.0)
        {
            if (area <= 0.0 || pt <= 0.0 || tt <= 0.0) return 0.0;
            double g = gamma > 0.0 ? gamma : Gamma(tt);
            double cp = Cp(tt);
            return area * pt * Math.Sqrt(g / (R * tt))
                   * Math.Pow(1.0 + 0.5 * (g - 1.0), -0.5 * (g + 1.0) / (g - 1.0));
        }

        /// <summary>
        /// 喷管。毛推力**必须**含压力项 (pe-pa)·Ae——漏掉它单位推力会低 25~30%。
        /// 出口面积用**理想**速度反算（不是带 η 的实际速度），否则面积偏大、压力推力偏大。
        /// </summary>
        public static void Nozzle(double mdot, double pt, double tt, double pAmb, double etaN,
                                  out double ve, out double ae, out double pe,
                                  out double thrust, out bool choked)
        {
            ve = 0.0; ae = 0.0; pe = pAmb; thrust = 0.0; choked = false;
            if (mdot <= 0.0 || pt <= 0.0 || tt <= 0.0) return;
            double g = Gamma(tt);
            double npr = pt / Math.Max(pAmb, 1.0);
            double tr;
            if (npr >= NprCrit)
            {
                tr = 2.0 / (g + 1.0);
                pe = pt / NprCrit;
                choked = true;
            }
            else
            {
                tr = Math.Pow(1.0 / npr, (g - 1.0) / g);
                pe = pAmb;
            }
            double cp = Cp(tt);
            double vIdeal = Math.Sqrt(Math.Max(2.0 * cp * tt * (1.0 - tr), 0.0));
            ve = vIdeal * Math.Sqrt(Math.Max(etaN, 0.0));
            double te = tt * tr;
            double rhoE = pe / (R * te);
            if (rhoE > 0.0 && vIdeal > 0.0) ae = mdot / (rhoE * vIdeal);
            thrust = mdot * ve + (pe - pAmb) * ae;
        }

        /// <summary>喷管求解结果（含几何信息）。</summary>
        public struct NozzleState
        {
            public double Ve, Ae, Pe, Te, Thrust, MachE;
            /// <summary>动量项 ṁ·Ve（喷管推力的大头）。</summary>
            public double Momentum;
            /// <summary>压力项 (Pe−Pa)·Ae（收扩喷管欠膨胀时才是正的，收敛喷管基本为 0）。</summary>
            public double Pressure;
            public bool Choked, Supersonic, Separated, BackPressure;
            /// <summary>出口面积是几何给的（A9 = 面积比 × A8），不是从流量反解的。</summary>
            public bool AeGeometric;
            /// <summary>几何出口面积装不下这么多流量（或大得离谱）时的相对偏差。</summary>
            public double AeMismatch;
        }

        /// <summary>由面积比反解马赫数（面积-马赫关系），branch: true=超声速支。</summary>
        public static double MachFromAreaRatio(double areaRatio, double g, bool supersonic)
        {
            if (areaRatio <= 1.0 + 1e-9) return 1.0;
            double e = (g + 1.0) / (2.0 * (g - 1.0));
            Func<double, double> ar = m =>
                (1.0 / m) * Math.Pow((2.0 / (g + 1.0)) * (1.0 + 0.5 * (g - 1.0) * m * m), e);
            double lo = supersonic ? 1.0 : 1e-4, hi = supersonic ? 12.0 : 1.0;
            for (int i = 0; i < 80; i++)
            {
                double mid = 0.5 * (lo + hi);
                double v = ar(mid);
                if (supersonic)
                {
                    if (v < areaRatio) lo = mid; else hi = mid;
                }
                else
                {
                    if (v > areaRatio) lo = mid; else hi = mid;
                }
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>设计工况下的理想面积比 A9/A8（完全膨胀到环境压力）。</summary>
        public static double DesignAreaRatio(double pt, double pAmb, double tt)
        {
            double g = Gamma(tt);
            double npr = pt / Math.Max(pAmb, 1.0);
            if (npr <= NprCrit) return 1.0;
            double me = Math.Sqrt(Math.Max(2.0 / (g - 1.0) * (Math.Pow(npr, (g - 1.0) / g) - 1.0), 0.0));
            if (me < 1.0) return 1.0;
            double e = (g + 1.0) / (2.0 * (g - 1.0));
            return (1.0 / me) * Math.Pow((2.0 / (g + 1.0)) * (1.0 + 0.5 * (g - 1.0) * me * me), e);
        }

        /// <summary>
        /// 带喷管型式的求解。Converging=固定收敛（喉道=出口，壅塞时欠膨胀）；
        /// Variable=可调收敛（喉道跟着流量走，压力推力不变但不会被憋住）；
        /// ConvergingDiverging=收扩（按固定面积比膨胀，低工况会过膨胀分离）。
        /// </summary>
        public static NozzleState NozzleFlow(double mdot, double pt, double tt, double pAmb,
                                             double etaN, NozzleKind kind,
                                             double aThroat, double areaRatio)
        {
            var s = new NozzleState();
            s.Pe = pAmb;
            if (mdot <= 0.0 || pt <= 0.0 || tt <= 0.0) return s;

            double g = Gamma(tt);
            double cp = Cp(tt);
            double npr = pt / Math.Max(pAmb, 1.0);
            bool choked = npr >= NprCrit;
            s.Choked = choked;

            // "压力比够大"只是壅塞的必要条件。真要说喉道壅塞，还得看这股气够不够把喉道灌满：
            // 上游限流时压力比照样很高，可 mdot 小于喉道的流量上限——那时喉道是亚声速的，
            // 拿超声速解去算它会凭空多出一截推力（11 km/M0.9 实测多 24%）。
            double throatCap = aThroat > 1e-12 ? ChokedFlow(aThroat, pt, tt, 0.0) : 0.0;
            bool throatChoked = aThroat <= 1e-12 || mdot >= throatCap * 0.98;
            if (!throatChoked) s.Choked = false;

            // 收扩喷管：优先走超声速膨胀解
            if (kind == NozzleKind.ConvergingDiverging && choked && throatChoked && areaRatio > 1.0 + 1e-6)
            {
                double me = MachFromAreaRatio(areaRatio, g, true);
                double pe = pt / Math.Pow(1.0 + 0.5 * (g - 1.0) * me * me, g / (g - 1.0));
                if (pe >= pAmb)
                {
                    double te = tt / (1.0 + 0.5 * (g - 1.0) * me * me);
                    double vIdeal = Math.Sqrt(Math.Max(2.0 * cp * (tt - te), 0.0));
                    s.MachE = me; s.Supersonic = me > 1.0;
                    s.Pe = pe; s.Te = te;
                    s.Ve = vIdeal * Math.Sqrt(Math.Max(etaN, 0.0));
                    double rhoE = pe / (R * te);
                    // 出口面积必须由**几何**决定：A9 = 面积比 · A8。喷管的扩张段是钢材，
                    // 不会因为流量少了就自己变小。以前这里写 `s.Ae = mdot/(rhoE*vIdeal)`，
                    // 等于让面积去迁就流量——于是推力永远像"恰好完全膨胀"，A8 动了 A9 也不动。
                    // 现在几何面积优先，再把"流量要求的面积"记下来做一致性检查：
                    // 两者差得多说明喉道实际没在壅塞（或上游限流），那时压力项必须按几何算。
                    double aeMech = areaRatio > 0.0 ? areaRatio * aThroat : 0.0;
                    double aeFlow = 0.0;
                    if (rhoE > 0.0 && vIdeal > 0.0) aeFlow = mdot / (rhoE * vIdeal);
                    // 还有一道物理门槛：这股气得撑得满扩张段。若按超声速解算下来，
                    // 需要的出口面积明显小于几何出口面积，说明流量根本填不满喷管——
                    // 真机那时会分离/出斜激波，不会凭空给出一个超声速出口速度。
                    // 以前没有这道检查（反正 Ae 是从流量反解的，永远自洽），
                    // 于是高空小流量工况会多算出一截推力。
                    bool fills = aeMech <= 0.0 || aeFlow <= 0.0 || aeFlow >= aeMech * 0.98;
                    if (fills)
                    {
                        if (aeMech > 0.0)
                        {
                            s.Ae = aeMech;
                            s.AeGeometric = true;
                            s.AeMismatch = aeFlow > 0.0 ? (aeFlow - aeMech) / aeMech : 0.0;
                        }
                        else
                        {
                            s.Ae = aeFlow;
                        }
                        s.Momentum = mdot * s.Ve;
                        s.Pressure = (s.Pe - pAmb) * s.Ae;
                        s.Thrust = s.Momentum + s.Pressure;
                        return s;
                    }
                    // 撑不满：退回收敛解，出口面积按喉道算（见下方统一处理）
                    s.Separated = true;
                    s.AeMismatch = (aeFlow - aeMech) / aeMech;
                }
                // 过膨胀：真实喷流会分离，退回收敛解
                s.Separated = true;
            }

            double trExit = choked ? 2.0 / (g + 1.0) : Math.Pow(1.0 / npr, (g - 1.0) / g);

            // 未窒息 + 固定面积收敛喷管：出口状态由"这么多流量必须从这么大面积挤出去"决定。
            // 原来这里也按 pt/pAmb 等熵膨胀算，pt 只比环境高一点点时给出 Ve≈0——
            // 慢车时核心机就是这个结果（推力全靠外涵），可是固定面积下喷流必须靠速度走掉
            // 这么多质量流，不可能凭空消失。改成按喉道面积反解出口马赫数。
            if (!choked && kind == NozzleKind.Converging && aThroat > 1e-12)
            {
                double ge = (g + 1.0) / (2.0 * (g - 1.0));
                double flowCoef = aThroat * pt / Math.Sqrt(tt) * Math.Sqrt(g / R);
                double lo = 1e-5, hi = 1.0;
                for (int i = 0; i < 60; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    double cap = flowCoef * mid * Math.Pow(1.0 + 0.5 * (g - 1.0) * mid * mid, -ge);
                    if (cap < mdot) lo = mid; else hi = mid;
                }
                double me = 0.5 * (lo + hi);
                double trM = 1.0 / (1.0 + 0.5 * (g - 1.0) * me * me);
                double teM = tt * trM;
                double peM = pt * Math.Pow(trM, g / (g - 1.0));
                if (peM < pAmb)
                {
                    // 过膨胀：真实喷流在出口以外分离，出口压力不会低于环境。
                    // 压力顶回环境压力后，同样的质量流只能靠更低的速度走掉。
                    s.Separated = true;
                    s.BackPressure = true;
                    peM = pAmb;
                    double rhoSep = pAmb / (R * teM);
                    s.Ve = (mdot / (rhoSep * aThroat)) * Math.Sqrt(Math.Max(etaN, 0.0));
                }
                else
                {
                    s.Ve = me * Math.Sqrt(Math.Max(g * R * teM, 0.0)) * Math.Sqrt(Math.Max(etaN, 0.0));
                }
                s.MachE = me;
                s.Te = teM;
                s.Pe = peM;
                s.Ae = aThroat;
                s.Momentum = mdot * s.Ve;
                s.Pressure = (s.Pe - pAmb) * s.Ae;
                s.Thrust = s.Momentum + s.Pressure;
                if (kind == NozzleKind.Converging && choked && aThroat > 0.0)
                {
                    double cap2 = ChokedFlow(aThroat, pt, tt, g);
                    if (mdot > cap2 * 1.02) s.BackPressure = true;
                }
                return s;
            }

            double vExit = Math.Sqrt(Math.Max(2.0 * cp * tt * (1.0 - trExit), 0.0));
            s.Ve = vExit * Math.Sqrt(Math.Max(etaN, 0.0));
            s.Te = tt * trExit;
            s.Pe = choked ? pt / NprCrit : pAmb;
            s.MachE = choked ? 1.0 : Math.Sqrt(Math.Max(2.0 / (g - 1.0) * (Math.Pow(npr, (g - 1.0) / g) - 1.0), 0.0));
            double rho = s.Pe / (R * s.Te);
            // 出口面积同样是几何的：收敛喷管出口就是喉道；收扩喷管一旦过膨胀分离，
            // 实际排气截面也退到喉道附近（分离点之后的扩张段不再做功）。
            // 以前这里一律按 `mdot/(rho*v)` 反解，等于让面积迁就流量——慢车时它会算出一个
            // 比喉道还小的出口面积（RD-33 是 0.1168 对喉道 0.1482），物理上说不通。
            if (aThroat > 1e-12)
            {
                s.Ae = aThroat;
                s.AeGeometric = true;
                if (rho > 0.0 && vExit > 0.0)
                    s.AeMismatch = (mdot / (rho * vExit) - aThroat) / aThroat;
            }
            else if (rho > 0.0 && vExit > 0.0)
            {
                s.Ae = mdot / (rho * vExit);
            }
            s.Momentum = mdot * s.Ve;
            s.Pressure = (s.Pe - pAmb) * s.Ae;
            s.Thrust = s.Momentum + s.Pressure;

            // 固定收敛喷管的流量容量检查：喉道吃不下就是被憋住了
            if (kind == NozzleKind.Converging && choked && aThroat > 0.0)
            {
                double cap = ChokedFlow(aThroat, pt, tt, g);
                if (mdot > cap * 1.02) s.BackPressure = true;
            }
            return s;
        }
    }
}