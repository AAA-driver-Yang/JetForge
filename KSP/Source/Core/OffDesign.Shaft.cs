using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    // partial：主解算在 Core/OffDesign.cs 里。
    public static partial class OffDesign
    {
        // ------------------------------------------------------------------------------------
        // 轴系校核与工况扫掠。
        //
        // 这一段在主解算之外：主解算算"这一刻各站气体怎么了"，这里算"这根轴转得动吗"，
        // 以及"把油门推一遍会怎样"。它们读 OffDesignResult 的字段，不参与迭代。
        // ------------------------------------------------------------------------------------


        /// <summary>
        /// 逐台涡轮的"需 / 供"核对。需 = 该轴压气机的功（设计点标定的份额分摊），
        /// 供 = 级数 × 单级落温差（由当前叶尖速度、进口温度、效率反算）。
        /// 单级落温差上不去就是带不动：真实发动机此时会掉转，让压气机需求落到能供的水平，
        /// 本模型不迭代那一步（转速解耦的欠定性），所以只报数字并标出是哪根轴。
        /// </summary>
        private static double ReadNamed(Dictionary<string, double> named, string key)
        {
            double v;
            return named != null && named.TryGetValue(key, out v) ? v : 0.0;
        }

        private static void AddShaftLoad(Dictionary<int, double> dem, Dictionary<int, double> cap,
            TurbineSpec spec, double wDemand, double wCapacity)
        {
            if (spec.Stages <= 0) return;
            if (wDemand <= 0.0 && wCapacity <= 0.0) return;
            int sh = spec.Shaft;
            if (!dem.ContainsKey(sh)) { dem[sh] = 0.0; cap[sh] = 0.0; }
            dem[sh] += wDemand;
            cap[sh] += wCapacity;
        }

        /// <summary>某根轴上所有叶轮里最大的叶尖线速度（m/s）。</summary>
        private static double ShaftTipSpeed(EngineDesignData d, int shaft, double rpm)
        {
            double best = 0.0;
            if (d.HasFan && d.Fan.Shaft == shaft) best = Math.Max(best, Cycle.TipSpeed(d.Fan.Diameter, rpm));
            if (d.Lpc.Stages > 0 && d.Lpc.Shaft == shaft) best = Math.Max(best, Cycle.TipSpeed(d.Lpc.MaxTip(), rpm));
            if (d.Hpc.Stages > 0 && d.Hpc.Shaft == shaft) best = Math.Max(best, Cycle.TipSpeed(d.Hpc.MaxTip(), rpm));
            if (d.Hpt.Stages > 0 && d.Hpt.Shaft == shaft) best = Math.Max(best, Cycle.TipSpeed(d.Hpt.MaxTip(), rpm));
            if (d.Ipt.Stages > 0 && d.Ipt.Shaft == shaft) best = Math.Max(best, Cycle.TipSpeed(d.Ipt.MaxTip(), rpm));
            if (d.Lpt.Stages > 0 && d.Lpt.Shaft == shaft) best = Math.Max(best, Cycle.TipSpeed(d.Lpt.MaxTip(), rpm));
            return best;
        }

        private static void CheckSpoolBalance(EngineDesignData d, OffDesignResult res, TechLevel tech,
            DesignConstants consts, double mTurb, double t4)
        {
            if (mTurb <= 0.0 || t4 <= 0.0) return;

            double wTotal = res.WComp + Math.Max(res.WFan, 0.0);
            if (wTotal <= 0.0) return;

            double wHpt = wTotal * consts.ShareHpt;
            double wIpt = wTotal * consts.ShareIpt;
            double wLpt = wTotal * consts.ShareLpt;

            double tIn = t4;
            double capHpt = CapDemand("HPT", d.Hpt, wHpt, tech.HptEtaPol, tech.TurbinePsi * d.Hpt.PsiScale,
                                      consts, res, mTurb, tech, ref tIn);
            double capIpt = CapDemand("IPT", d.Ipt, wIpt, tech.HptEtaPol, tech.TurbinePsi * d.Ipt.PsiScale,
                                      consts, res, mTurb, tech, ref tIn);
            double capLpt = CapDemand("LPT", d.Lpt, wLpt, tech.LptEtaPol, tech.TurbinePsi * d.Lpt.PsiScale,
                                      consts, res, mTurb, tech, ref tIn);

            res.WHpt = wHpt; res.WIpt = wIpt; res.WLpt = wLpt;
            res.CapHpt = capHpt; res.CapIpt = capIpt; res.CapLpt = capLpt;

            // 逐轴累计需/供 → 掉转因子。一台涡轮带不动，掉的是它那根轴的转速，
            // 不是别的轴；所以按轴号累加，而不是取最大那台。
            Dictionary<int, double> demByShaft = new Dictionary<int, double>();
            Dictionary<int, double> capByShaft = new Dictionary<int, double>();
            AddShaftLoad(demByShaft, capByShaft, d.Hpt, wHpt, capHpt);
            AddShaftLoad(demByShaft, capByShaft, d.Ipt, wIpt, capIpt);
            AddShaftLoad(demByShaft, capByShaft, d.Lpt, wLpt, capLpt);
            res.SpoolShortfall = new Dictionary<int, double>();
            bool anyShort = false;
            foreach (KeyValuePair<int, double> kv in demByShaft)
            {
                double dem = kv.Value, cap = capByShaft[kv.Key];
                if (dem <= 1.0 || cap <= 1.0) continue;
                double f = Math.Sqrt(cap / dem);
                if (f >= 0.995) continue;
                res.SpoolShortfall[kv.Key] = Math.Max(f, 0.40);
                anyShort = true;
            }
            if (anyShort)
            {
                foreach (KeyValuePair<int, double> kv in res.SpoolShortfall)
                    res.Warnings.Add(string.Format(
                        Loc.T("轴 {0} 掉转：涡轮供不上压气机需求，转速降到设计值的 {1:P0}（真实发动机就是这样自己找回平衡的）", "Shaft {0} sags: the turbine cannot feed the compressor, so speed drops to {1:P0} of design (that is how a real engine finds its own balance)"),
                        kv.Key, kv.Value));
            }

            // 找出最紧张的那根轴：需/供比最大且超过 1
            double rHpt = capHpt > 1.0 ? wHpt / capHpt : 0.0;
            double rIpt = capIpt > 1.0 ? wIpt / capIpt : 0.0;
            double rLpt = capLpt > 1.0 ? wLpt / capLpt : 0.0;
            double worst = Math.Max(rHpt, Math.Max(rIpt, rLpt));
            res.LimitSpool = 0;

            if (worst > 1.01)
            {
                string name;
                int shaft;
                if (rHpt >= worst) { name = "HPT"; shaft = d.Hpt.Shaft; }
                else if (rIpt >= worst) { name = "IPT"; shaft = d.Ipt.Shaft; }
                else { name = "LPT"; shaft = d.Lpt.Shaft; }
                res.LimitSpool = shaft;
                res.Warnings.Add(string.Format(
                    Loc.T("{0} 带不动轴 {1}：需要 {2:F2} MW，当前转速下只能挤出 {3:F2} MW（差 {4:F0}%）；", "The {0} cannot drive shaft {1}: it needs {2:F2} MW, at this speed it can only make {3:F2} MW ({4:F0}% short);")
                    + Loc.T("真实发动机会掉转直到压气机需求降到能供的水平", "A real engine sags in speed until the compressor demand falls to what the turbine can supply"),
                    name, shaft, (rHpt >= worst ? wHpt : (rIpt >= worst ? wIpt : wLpt)) / 1e6,
                    (rHpt >= worst ? capHpt : (rIpt >= worst ? capIpt : capLpt)) / 1e6,
                    (worst - 1.0) * 100.0));
            }
        }

        /// <summary>填一台涡轮的"最多能提供多少功"，并按"能供多少就降多少"推进涡轮进口温度。</summary>
        private static double CapDemand(string name, TurbineSpec spec, double wDemand, double etaPol,
            double psi, DesignConstants consts, OffDesignResult res, double mTurb, TechLevel tech, ref double tIn)
        {
            if (spec.Stages <= 0) return 0.0;

            double rpmD;
            consts.RpmDesign.TryGetValue(spec.Shaft, out rpmD);
            double rpm = rpmD;
            if (res.Rpm != null && res.Rpm.ContainsKey(spec.Shaft)) rpm = res.Rpm[spec.Shaft];
            double u = Cycle.TipSpeed(spec.MaxTip(), rpm);

            double eta = etaPol;
            if (spec.MaxTip() > 0.0 && u > 0.0)
            {
                ScaleInfo si;
                eta = Cycle.ScaleEta(spec.MaxTip(), spec.HubTipRatio, u, Math.Max(tIn, 120.0),
                    Math.Max(res.P5, 1000.0), etaPol, tech, 1.40, out si);
            }
            if (!(eta > 0.05)) eta = etaPol;

            // 容量两头上限取小（叶尖速度 + 单级落压比）。这里以前只算叶尖速度，
            // 于是"落地 Ma1.6 落压比 3.54 超了 2.40 的极限"这件事在**飞行时那套数**里根本看不到：
            // 它只在设计点报一句红字，掉转因子却按偏大的容量算，等于白报。
            bool prBound;
            double capDT = Cycle.TurbineCapacityDt(spec, tech, rpm, Math.Max(tIn, 120.0), eta, tech.TurbinePsi, out prBound);
            double cp = Gas.Cp(Math.Max(tIn, 120.0));
            double capWork = capDT * cp * mTurb;

            // 实际落的温降以可提供为上限
            double dTNeed = wDemand / Math.Max(mTurb * cp, 1e-9);
            double dTUse = Math.Min(dTNeed, capDT);

            tIn = Math.Max(tIn - dTUse, 0.4 * tIn);
            return capWork;
        }
    }
}
