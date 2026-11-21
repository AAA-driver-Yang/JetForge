using System;
using JetForge.Core;

namespace JetForge.Test
{
    /// <summary>
    /// 两轴拆分的原型：**只用来量，不参与正式求解**。
    ///
    /// 要回答的问题：如果把 N1 从"永远等于 N2 的换算转速比例"里解放出来，
    /// 让它按真机的经验相关式 N1 ≈ N2^k 掉下去（大涵道比涡扇慢车 N1 20~25%、N2 约 60%，
    /// 反解 k ≈ 3；这里取 2.8），那么：
    ///   · 风扇会转到多快
    ///   · 涵道流量会掉到多少（现在恒定在设计的 94%）
    ///   · 核心那根轴要多转多少才能把燃油烧掉（能量守恒只有一个自由变量）
    ///
    /// 做法：给定 T4（跟正式求解器同一个油门→T4 口径），二分核心换算转速 nc，
    /// 让"涡轮能给的功" = "核心压气机要的功 + 风扇要的功"。风扇的功由它自己在 n1 上的
    /// 压比（FanMap 工作线）给出，不再按设计点冻结的比例 fc 分。
    ///
    /// 为什么这是原型而不是正式实现：
    ///   · N1 = N2^2.8 是经验相关式，不是解出来的；
    ///   · 完全没碰喷管、推力与涵道流量的耦合，所以不产出推力与 SFC；
    ///   · 正式实现要按"低压轴力矩平衡 + 涡轮特性"来解，那要另起一套方程。
    /// </summary>
    internal static class TwoSpoolProbe
    {
        /// <summary>N1 ≈ N2^k 的经验指数（大涵道比涡扇）。单轴机不进这张表。</summary>
        public const double N1Exponent = 2.8;

        /// <summary>在给定 T4 下解出 (nc, n1)，返回 false 表示这条能量平衡在区间内无解。</summary>
        public static bool SolveSpool(EngineDesignData d, DesignConstants c, double t4,
                                      out double nc, out double n1, out double corePr,
                                      out double fanPr, out double residualOut)
        {
            nc = 0.0; n1 = 0.0; corePr = 1.0; fanPr = 1.0; residualOut = 0.0;
            TechLevel tech = d.Tech;
            CycleResult r0 = Cycle.SolveDesignPoint(d);
            if (!r0.Ok) return false;
            double fprD = r0.Fpr > 0.0 ? r0.Fpr : 1.0;
            double cprD = r0.Opr > 0.0 ? r0.Opr : c.Cpr;

            double p0, t0;
            Gas.Isa(0.0, out p0, out t0);
            double pt1 = p0 * tech.IntakeTprSub;
            double tt1 = t0;
            double g1 = Gas.Gamma(tt1);
            double etaC = tech.HpcEtaPol;
            double etaF = tech.FanEtaPol;

            Func<double, double> residualOf = x =>
            {
                double pr = CompressorMap.PressureRatio(cprD, x, d.Hpc.Kind);
                double t3 = tt1 * Math.Pow(pr, (g1 - 1.0) / g1 / etaC);
                if (t4 <= t3) return double.NaN;   // 这个转速下涡轮出口温度撑不住燃烧室温升
                double pt4 = pt1 * pr * (1.0 - tech.BurnerDpRatio);
                double t5, piT;
                bool lim;
                OffDesign.TurbineExit(t4, pt4, p0, c.Ttr, tech.HptEtaPol, out t5, out piT, out lim);
                double wt = Gas.Cp(t4) * (t4 - t5);
                double acc = c.AccessoryFrac * c.WTurbDesign
                             * Math.Max(0.0, 1.0 - wt / Math.Max(c.WTurbDesign, 1e-9));
                wt = Math.Max(wt - acc, 0.0);
                double wComp = Gas.Cp(tt1) * (Math.Pow(pr, (g1 - 1.0) / (g1 * etaC)) - 1.0);
                double n1v = Math.Pow(x, N1Exponent);
                double fpr = FanMap.PressureRatio(fprD, n1v);
                double gf = Gas.Gamma(tt1);
                double wFanKg = Gas.Cp(tt1) * (Math.Pow(fpr, (gf - 1.0) / (gf * etaF)) - 1.0);
                // 涵道与核心的流量比随各自的转速走：ṁ ∝ 转速（定通道面积）。
                double bprEff = c.Bpr * (n1v / Math.Max(x, 1e-6));
                return wt - wComp - bprEff * wFanKg;
            };

            // 先扫一遍找符号变化，再二分。直接二分会因为 NaN 区间来回打转。
            double lo = 0.20, hi = 1.05, rLo = double.NaN, rHi = double.NaN;
            bool bracket = false;
            double prevX = lo, prevR = double.NaN;
            for (int i = 0; i <= 60; i++)
            {
                double x = lo + (hi - lo) * i / 60.0;
                double r = residualOf(x);
                if (double.IsNaN(r)) { prevR = double.NaN; prevX = x; continue; }
                if (!double.IsNaN(prevR) && prevR * r <= 0.0)
                {
                    lo = prevX; hi = x; rLo = prevR; rHi = r; bracket = true;
                    break;
                }
                prevX = x; prevR = r;
            }
            if (!bracket)
            {
                residualOut = double.IsNaN(prevR) ? 0.0 : prevR;
                return false;
            }

            for (int it = 0; it < 60; it++)
            {
                double mid = 0.5 * (lo + hi);
                double rm = residualOf(mid);
                if (double.IsNaN(rm)) { lo = mid; continue; }
                if (rLo * rm <= 0.0) { hi = mid; rHi = rm; }
                else { lo = mid; rLo = rm; }
                if (hi - lo < 0.0005) break;
            }

            nc = 0.5 * (lo + hi);
            n1 = Math.Pow(nc, N1Exponent);
            corePr = CompressorMap.PressureRatio(cprD, nc, d.Hpc.Kind);
            fanPr = FanMap.PressureRatio(fprD, n1);
            residualOut = residualOf(nc);
            return true;
        }
    }
}