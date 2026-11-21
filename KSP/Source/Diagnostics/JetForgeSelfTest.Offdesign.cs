using System;
using System.Collections;
using JetForge.Core;

namespace JetForge.Diagnostics
{
    public partial class JetForgeSelfTest
    {
        /// <summary>
        /// 参数空间的角落：滑块拖到底会不会崩、有没有负推力、超声速设计点怎么说。
        /// 这一段不读主流程的局部变量（只有自己的 fd / cr / gd），所以整段搬得出来。
        /// </summary>
        private void SectionCorners()
        {
            // ---- 参数空间的角落：滑块拖到底会不会崩、有没有负推力、超声速设计点怎么说 ----
            try
            {
                EngineDesignData fd = new EngineDesignData();
                fd.InletDiameter = 3.0;
                fd.CoreInletDiameter = 1.0;
                fd.Length = 4.0;
                fd.Burner.ExitTemp = 2200.0;
                fd.Afterburner.Enabled = true;
                fd.Afterburner.FuelRatio = 0.02;
                fd.Afterburner.ExitTemp = 2200.0;
                CycleResult cr = Cycle.SolveDesignPoint(fd);
                Log(string.Format("极端构型（进气 3.0 m、涡轮前 2200 K、开加力）：成立 {0} 推力 {1:F2} kN 进气 {2:F1} kg/s 理由={3}",
                    YN(cr.Ok), cr.Thrust / 1000.0, cr.MdotAir, cr.Errors.Count > 0 ? cr.Errors[0] : "无"));

                // 参数空间体检：和第 19 段同一条 LCG、同样的值域。
                // 只数四件事：崩没崩、有没有荒唐数、不成立有没有理由、负推力判据拦下几个。
                Section("边界：随机撒 240 组极端参数，看会不会出荒唐数");
                {
                    uint seed = 20261114u;
                    int okN = 0, errN = 0, badN = 0, negN = 0;
                    string negSample = "";
                    for (int i = 0; i < 240; i++)
                    {
                        seed = seed * 1664525u + 1013904223u; double u1 = (seed >> 8) / 16777216.0;
                        seed = seed * 1664525u + 1013904223u; double u2 = (seed >> 8) / 16777216.0;
                        seed = seed * 1664525u + 1013904223u; double u3 = (seed >> 8) / 16777216.0;
                        seed = seed * 1664525u + 1013904223u; double u4 = (seed >> 8) / 16777216.0;
                        seed = seed * 1664525u + 1013904223u; double u5 = (seed >> 8) / 16777216.0;
                        seed = seed * 1664525u + 1013904223u; double u6 = (seed >> 8) / 16777216.0;
                        seed = seed * 1664525u + 1013904223u; double u7 = (seed >> 8) / 16777216.0;
                        seed = seed * 1664525u + 1013904223u; double u8 = (seed >> 8) / 16777216.0;

                        EngineDesignData gd = new EngineDesignData();
                        gd.InletDiameter = 0.2 + 2.8 * u1;
                        gd.Length = 0.5 + 9.5 * u2;
                        gd.CoreInletDiameter = u3 < 0.3 ? 0.0 : 0.1 + 1.9 * u3;
                        gd.DesignMach = 2.5 * u4;
                        gd.DesignAlt = 20000.0 * u5;
                        gd.Fan.Diameter = 0.2 + 2.8 * u6;
                        gd.Lpc.Stages = (int)(9 * u7);
                        gd.Hpc.Stages = 1 + (int)(12 * u8);
                        gd.Burner.ExitTemp = 700.0 + 1500.0 * ((seed >> 8) / 16777216.0);

                        try
                        {
                            CycleResult gr = Cycle.SolveDesignPoint(gd);
                            if (gr.Ok) okN++; else errN++;
                            if (!gr.Ok && gr.Errors.Count > 0 && gr.Errors[0].StartsWith("设计点净推力"))
                            {
                                negN++;
                                if (negSample.Length == 0) negSample = gr.Errors[0];
                            }
                            if (double.IsNaN(gr.Thrust) || double.IsInfinity(gr.Thrust) ||
                                double.IsNaN(gr.MdotAir) || double.IsInfinity(gr.MdotAir)) badN++;
                        }
                        catch (Exception)
                        {
                            badN++;
                        }
                    }
                    Log(string.Format("参数空间体检 240 例：设计点成立 {0}、不成立 {1}、异常或荒唐数 {2}｜负推力判据拦下 {3} 例",
                        okN, errN, badN, negN));
                    if (negSample.Length > 0) Log("  其中一例：" + negSample);
                }

                // 负推力：排气速度比来流还慢时，必须判不成立而不是给一个负数。
                // 这个构型来自第 19 段随机体检里被抓到的那一台（改之前它标着"成立"、推力 -703 kN）。
                try
                {
                    EngineDesignData nd = new EngineDesignData();
                    nd.InletDiameter = 1.36f; nd.Fan.Diameter = 2.60f; nd.CoreInletDiameter = 1.16f; nd.Length = 8.23f;
                    nd.Lpc.Stages = 0; nd.Hpc.Stages = 6; nd.Hpt.Stages = 1; nd.Ipt.Stages = 2; nd.Lpt.Stages = 1;
                    nd.Lpc.TipDiameter = 0.36f; nd.Hpc.TipDiameter = 0.39f;
                    nd.Hpt.TipDiameter = 1.05f; nd.Ipt.TipDiameter = 1.43f; nd.Lpt.TipDiameter = 1.36f;
                    nd.Burner.ExitTemp = 1486.0;
                    nd.DesignMach = 2.35; nd.DesignAlt = 14493.0;
                    CycleResult nc = Cycle.SolveDesignPoint(nd);
                    Log(string.Format("负推力判据：成立 {0} 推力 {1:F1} kN｜{2}",
                        YN(nc.Ok), nc.Thrust / 1000.0, nc.Errors.Count > 0 ? nc.Errors[0] : "无"));
                }
                catch (Exception e) { Log("负推力判据异常: " + e); }

                // 面马赫 0.5 这个假设的边界：超声速设计点必须自己说出来
                EngineDesignData sd = new EngineDesignData();
                sd.DesignMach = 2.0;
                sd.DesignAlt = 11000.0;
                DesignConstants sc = OffDesign.DeriveDesignConstants(sd);
                OffDesignResult sr = OffDesign.Solve(sd, 11000.0, 2.0, 1.0, 0.0, sc);
                CheckReport rep = Criteria.Evaluate(sd, Criteria.ViewOf(sr, sd.HasFan ? sd.Fan.Shaft : 0));
                string sup = "-";
                string sub = "-";
                for (int i = 0; i < rep.Findings.Count; i++)
                {
                    if (rep.Findings[i].Key == "inlet.supersonic") sup = rep.Findings[i].Message;
                    if (rep.Findings[i].Key == "inlet.subsonic") sub = rep.Findings[i].Message;
                }
                Log("超声速设计点判据：" + sup);

                EngineDesignData sd2 = new EngineDesignData();
                sd2.DesignMach = 0.85;
                sd2.DesignAlt = 11000.0;
                DesignConstants sc2 = OffDesign.DeriveDesignConstants(sd2);
                OffDesignResult sr2 = OffDesign.Solve(sd2, 11000.0, 0.85, 1.0, 0.0, sc2);
                CheckReport rep2 = Criteria.Evaluate(sd2, Criteria.ViewOf(sr2, sd2.HasFan ? sd2.Fan.Shaft : 0));
                for (int i = 0; i < rep2.Findings.Count; i++)
                    if (rep2.Findings[i].Key == "inlet.subsonic") sub = rep2.Findings[i].Message;
                Log("亚声速设计点判据：" + sub);
            }
            catch (Exception e) { Log("参数角落异常: " + e); }
        }

        /// <summary>
        /// 转速变化有多快：由压气机/涡轮的功率与转子惯量决定，不是一个拍出来的时间常数。
        /// 前一半对照真机预置（不碰主流程），后一半拿主流程那台 `design` 做自洽性检查，
        /// 所以这一段带一个形参。
        /// </summary>
        private void SectionSpoolSpeed(EngineDesignData design)
        {
            // ---- 转速变化有多快：由压气机/涡轮的功率与转子惯量决定，不是一个拍出来的时间常数 ----
            //
            // 老版本 `Spool.cs` 拿 `0.5·(D/0.76)^0.5` 拍时间常数，直径一变就变、机种之间只差 30%，
            // 而真机 J85（0.2→0.95）约 2.9 s、J57 约 5.2 s，差 1.8 倍。差的那部分就是惯量与功率。
            try
            {
                string[] names = { "J85", "J79", "J57", "RD-33" };
                double[] reals = { 2.90, 5.00, 5.20, 3.60 };
                double worst = 0.0;
                for (int i = 0; i < names.Length && i < reals.Length; i++)
                {
                    EngineDesignData pd = Presets.Build(names[i]);
                    if (pd == null) continue;
                    DesignConstants pc = OffDesign.DeriveDesignConstants(pd);
                    if (pc == null || !pc.Ok) { Log(string.Format("  {0}：设计点不成立，跳过", names[i])); continue; }
                    OffDesignResult pr = OffDesign.Solve(pd, 0.0, 0.0, 1.0, 0.0, pc);
                    SpoolDynamics.Law law = new SpoolDynamics.Law();
                    law.W0 = pr.WComp;
                    law.Inertia = SpoolDynamics.Inertia(pd);
                    law.OmegaDesign = SpoolDynamics.DesignOmega(pd);
                    double t = SpoolDynamics.SettleTime(law, 0.20, 0.95);
                    double err = Math.Abs(t - reals[i]) / reals[i];
                    if (err > worst && !double.IsNaN(err)) worst = err;
                    Log(string.Format("  {0}：压气机功 {1:F1} MW｜惯量 {2:F1} kg·m²｜设计转速 {3:F0} rad/s" +
                                      "｜0.2→0.95 用 {4:F2} s（真机约 {5:F2} s，偏差 {6:P0}）",
                        names[i], pr.WComp / 1.0e6, law.Inertia, law.OmegaDesign, t, reals[i], err));
                }
                Log(string.Format("  标定系数 {0:F2}｜四个预设最大偏差 {1:P0}",
                    SpoolDynamics.Calibration, worst));

                // 三条不靠真机数据的自洽性：功率翻倍时间减半、惯量翻倍时间翻倍、转速不会自己跳。
                DesignConstants jc = OffDesign.DeriveDesignConstants(design);
                OffDesignResult jr = OffDesign.Solve(design, 0.0, 0.0, 1.0, 0.0, jc);
                SpoolDynamics.Law baseLaw = new SpoolDynamics.Law();
                baseLaw.W0 = jr.WComp;
                baseLaw.Inertia = SpoolDynamics.Inertia(design);
                baseLaw.OmegaDesign = SpoolDynamics.DesignOmega(design);
                double t1 = SpoolDynamics.SettleTime(baseLaw, 0.20, 0.95);
                SpoolDynamics.Law twicePower = new SpoolDynamics.Law();
                twicePower.W0 = baseLaw.W0 * 2.0;
                twicePower.Inertia = baseLaw.Inertia;
                twicePower.OmegaDesign = baseLaw.OmegaDesign;
                double t2 = SpoolDynamics.SettleTime(twicePower, 0.20, 0.95);
                SpoolDynamics.Law twiceInertia = new SpoolDynamics.Law();
                twiceInertia.W0 = baseLaw.W0;
                twiceInertia.Inertia = baseLaw.Inertia * 2.0;
                twiceInertia.OmegaDesign = baseLaw.OmegaDesign;
                double t3 = SpoolDynamics.SettleTime(twiceInertia, 0.20, 0.95);
                Log(string.Format("  自洽：{0:F2} s｜功率翻倍 {1:F2} s（应减半）｜惯量翻倍 {2:F2} s（应翻倍）",
                    t1, t2, t3));
                // 一帧只走一点点：dt=0.02 s 从 0.20 起，推进量必须小于 0.05，否则"一帧冲到头"
                double oneFrame = baseLaw.Step(0.20, 0.95, 0.02);
                Log(string.Format("  一帧（0.02 s）从 0.20 推到 {0:F3}（一帧冲到头就是量纲错了）", oneFrame));

                bool okTime = worst < 0.25;
                bool okHalf = Math.Abs(t2 / t1 - 0.5) < 0.02;
                bool okDouble = Math.Abs(t3 / t1 - 2.0) < 0.06;
                bool okFrame = oneFrame > 0.20 && oneFrame < 0.26;
                Log(string.Format("  断言：与真机同量级（±25%） {0}｜功率翻倍减半 {1}｜惯量翻倍翻倍 {2}｜不是一帧冲到头 {3}",
                    okTime ? "对" : "不对", okHalf ? "对" : "不对",
                    okDouble ? "对" : "不对", okFrame ? "对" : "不对"));
            }
            catch (Exception e) { Log("  加速律异常: " + e.Message); }
        }
    }
}
