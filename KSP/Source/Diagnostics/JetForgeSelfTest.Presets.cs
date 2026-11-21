using System;
using System.Collections.Generic;
using System.Reflection;
using JetForge.Core;
using JetForge.KSP;

namespace JetForge.Diagnostics
{
    /// <summary>
    /// 自检里按主题分出来的部分：预设机型、压气机形式、燃烧室形状、喘振链路、PAW 清单。
    /// 这几段各自建自己的设计、只往日志里写字，所以能整段搬走。
    /// </summary>
    public partial class JetForgeSelfTest
    {
        private void SectionPresetsFormsAndPaw(EngineDesignData design)
        {
            // ---- 真机预设：设计器顶部那四个按钮背后是什么 ----
            // 与命令行第 23 段逐位对照：几何照手册抄，涡轮 psi 二分拟合到真机 OPR，
            // 进气口径按真机流量反解。拟合是确定性的，所以两边必须给出同一串数。
            try
            {
                IList<PresetInfo> all = Presets.All;
                Section("预设机型：每一台算什么数，和真机手册差多少");
                Log(string.Format("真机预设：{0} 台，点一下换整套参数（几何照手册抄，psi 拟合到真机 OPR）", all.Count));
                for (int i = 0; i < all.Count; i++)
                {
                    PresetInfo p = all[i];
                    EngineDesignData pd = Presets.Build(p.Key);
                    CycleResult pr = Cycle.SolveDesignPoint(pd);
                    double dry = MassCost.Estimate(pd).TotalDry;
                    Log(string.Format("  {0,-6} OPR {1:F2}/{2:F1}　推力 {3:F1}/{4:F1} kN　流量 {5:F1}/{6:F1} kg/s　SFC {7:F3}/{8:F3}　干重 {9:F0}/{10:F0} kg　设计点 {11}",
                        p.Key, pr.Opr, p.RealOpr, pr.Thrust / 1000.0, p.RealThrustKN,
                        pr.MdotAir, p.RealMdot, pr.Sfc, p.RealSfc, dry, p.RealDryKg, pr.Ok ? "成立" : "不成立"));
                    Log(string.Format("    进气口径 {0:F2} m（等效捕获面积，不是真机口径）｜{1}", pd.InletDiameter, p.Note));
                    Log("    " + JetForgeMeshBuilder.DescribeProportions(pd));
                }
            }
            catch (Exception e) { Log("真机预设异常: " + e); }

            SectionSpoolReadout();


            // ---- 压气机形式：轴流 vs 离心 ----
            // 同一个欧拉框架，离心叶轮的 ψ 是轴流的两倍上下（叶轮把气流甩向径向，
            // D 那一项乘得更狠），所以两级离心能顶十几级轴流；代价是通流面积小、
            // 效率低 1.5 点、真机很少连排超过两级。
            try
            {
                EngineDesignData ax = new EngineDesignData();
                ax.Hpc.Kind = CompressorKind.Axial; ax.Hpc.Stages = 9; ax.Hpc.PsiScale = 1.0;
                EngineDesignData ax2 = ax.Clone(); ax2.Hpc.Stages = 2;
                EngineDesignData ce2 = ax.Clone(); ce2.Hpc.Kind = CompressorKind.Centrifugal; ce2.Hpc.Stages = 2;
                EngineDesignData ce3 = ax.Clone(); ce3.Hpc.Kind = CompressorKind.Centrifugal; ce3.Hpc.Stages = 3;

                CycleResult ra = Cycle.SolveDesignPoint(ax);
                CycleResult ra2 = Cycle.SolveDesignPoint(ax2);
                CycleResult rc2 = Cycle.SolveDesignPoint(ce2);
                Log(string.Format("压气机形式：9 级轴流 OPR {0:F2}（{1}）｜2 级轴流 OPR {2:F2}｜2 级离心 OPR {3:F2}（{4}）",
                    ra.Opr, ra.Ok ? "成立" : "不成立", ra2.Opr, rc2.Opr, rc2.Ok ? "成立" : "不成立"));
                Log(string.Format("  2 级离心 / 2 级轴流 = {0:F2} 倍（离心 ψ 系数 {1:F2}、效率罚 {2:F3}）",
                    rc2.Opr / Math.Max(ra2.Opr, 0.01), CompressorPhysics.CentrifugalPsiFactor,
                    CompressorPhysics.EtaDelta(CompressorKind.Centrifugal)));

                string kindWarn = "无";
                CheckReport rep3 = Criteria.Evaluate(ce3, Criteria.ViewOf(Cycle.SolveDesignPoint(ce3)));
                foreach (Finding fd in rep3.Findings)
                    if (fd.Key == "comp.hpc.stages" && fd.LevelValue == Level.Warn) kindWarn = fd.Message;
                Log("  3 级离心判据：" + kindWarn);

                // 外形：离心级会鼓出一圈盘
                int nAx = JetForgeMeshBuilder.BuildComponents(ax).Count;
                int nCe = JetForgeMeshBuilder.BuildComponents(ce2).Count;
                Log(string.Format("  分部件数：轴流 {0} 个 → 离心 {1} 个（离心盘鼓出核心机一圈）", nAx, nCe));
            }
            catch (Exception e) { Log("压气机形式异常: " + e); }

            // ---- 燃烧室形状 ----
            // 类型给基准压损/效率，形状再加减：火焰筒太短烧不完（效率掉、油耗涨），
            // 太长摩擦与掺混损失叠加（压损涨），罐式出口温度不均（涡轮热点）。
            try
            {
                EngineDesignData bc = new EngineDesignData();
                bc.Burner.Kind = BurnerKind.Can;
                EngineDesignData ba = bc.Clone();
                ba.Burner.Kind = BurnerKind.Annular;
                EngineDesignData bs = ba.Clone();
                bs.Burner.LinerLd = 1.0;
                EngineDesignData bl = ba.Clone();
                bl.Burner.LinerLd = 5.0;

                Cycle.BurnerPerf pc = Cycle.BurnerPerformance(bc.Burner);
                Cycle.BurnerPerf pa = Cycle.BurnerPerformance(ba.Burner);
                Cycle.BurnerPerf ps = Cycle.BurnerPerformance(bs.Burner);
                Cycle.BurnerPerf pl = Cycle.BurnerPerformance(bl.Burner);
                Log(string.Format("燃烧室形状：罐式 {0} 罐 L/H {1:F2} η {2:F3} ΔP/P {3:F3} 不均 {4:F2}",
                    pc.Cans, pc.Ld, pc.Eta, 1.0 - pc.Sigma, pc.PatternFactor));
                Log(string.Format("  环形 L/H {0:F2} η {1:F3} ΔP/P {2:F3} 不均 {3:F2}｜短 L/H1.0 η {4:F3}｜长 L/H5.0 ΔP/P {5:F3}",
                    pa.Ld, pa.Eta, 1.0 - pa.Sigma, pa.PatternFactor, ps.Eta, 1.0 - pl.Sigma));

                CycleResult rga = Cycle.SolveDesignPoint(ba);
                CycleResult rgs = Cycle.SolveDesignPoint(bs);
                Log(string.Format("  设计点（锁 T4）：L/H 缺省 {0:F2} kN SFC {1:F4}｜L/H 1.0 {2:F2} kN SFC {3:F4}（{4:+0.0;-0.0}%）",
                    rga.Thrust / 1000.0, rga.Sfc, rgs.Thrust / 1000.0, rgs.Sfc,
                    (rgs.Sfc / Math.Max(rga.Sfc, 1e-9) - 1.0) * 100.0));

                string ln = "无", sh = "无", cn = "无";
                CheckReport rb3 = Criteria.Evaluate(bs, Criteria.ViewOf(Cycle.SolveDesignPoint(bs)));
                foreach (Finding fd in rb3.Findings)
                    if (fd.LevelValue == Level.Warn)
                    {
                        if (fd.Key == "burner.liner") ln = fd.Message;
                        if (fd.Key == "burner.shape") sh = fd.Message;
                        if (fd.Key == "burner.cans") cn = fd.Message;
                    }
                Log("  火焰筒太短判据：" + ln);
                EngineDesignData bc2 = bc.Clone();
                bc2.Burner.CanCount = 2;
                CheckReport rc3 = Criteria.Evaluate(bc2, Criteria.ViewOf(Cycle.SolveDesignPoint(bc2)));
                foreach (Finding fd in rc3.Findings)
                    if (fd.Key == "burner.cans" && fd.LevelValue == Level.Warn) cn = fd.Message;
                Log("  罐数太少判据：" + cn);

                int nCan = JetForgeMeshBuilder.BuildComponents(bc).Count;
                int nAnn = JetForgeMeshBuilder.BuildComponents(ba).Count;
                Log(string.Format("  半径：罐式机匣段 {0:F2}×RCore，环形 {1:F2}×RCore（隆起在核心机轮廓里，部件数都是 {2}/{3} 不变）",
                    JetForgeMeshBuilder.BurnerShellFactor(bc), JetForgeMeshBuilder.BurnerShellFactor(ba), nCan, nAnn));
            }
            catch (Exception e) { Log("燃烧室形状异常: " + e); }

            SectionMyDesign();


            SectionAfterburnerLight();


            SectionRelightWindow();


            SectionDefectChain(design);


            // ---- 第五阶段⑤：喘振是"逼出来的"，不是"设计里注定一直抖" ----
            // 三层要分开证：缓推不喘、猛推必喘、同样一手油门坏设计会喘好设计不喘。
            try
            {
                // ① 缓推：4 秒把 0.20 推到 1.00（0.2/s），转子滞后 1.2 s 跟着走
                SurgeRuntime slow = new SurgeRuntime();
                double thrS = 0.20, sp = 0.20;
                for (int i = 0; i < 200; i++)
                {
                    double dt = 0.02;
                    double next = Math.Min(1.0, thrS + 0.20 * dt);
                    double rate = Surge.Rate(thrS, next, dt);
                    sp += (next - sp) * Math.Min(1.0, dt / 1.2);
                    Surge.Step(slow, next, rate, sp, 0.30, 0.10, dt);
                    thrS = next;
                }
                Log(string.Format("  缓推 0.20→1.00（0.20/s）：{0}｜风险 {1:F2}｜喘 {2} 次",
                    slow.Active ? "喘振中" : "平稳", slow.Risk, slow.Events));

                // ② 猛推：一帧 0.02 秒从 0.20 推到 1.00（40/s），转子还停在 0.25
                SurgeRuntime slam = new SurgeRuntime();
                double rateSlam = Surge.Rate(0.20, 1.00, 0.02);
                Log("  猛推风险：" + Surge.Explain(1.00, rateSlam, 0.25, 0.30, 0.10));
                Surge.Step(slam, 1.00, rateSlam, 0.25, 0.30, 0.10, 0.02);
                Log(string.Format("  猛推 0.20→1.00（{0:F0}/s）：{1}｜喘 {2} 次｜叶片磨损 {3:P1}",
                    rateSlam, slam.Active ? "喘振中" : "平稳", slam.Events, slam.Wear));

                // ③ 喘振中的拍子：推力在 0.35 与 0.92 之间来回，排气温度上冲
                double lo = 9.0, hi = 0.0, egtMax = 0.0;
                for (int i = 0; i < 60; i++)
                {
                    Surge.Step(slam, 1.00, 0.0, 0.95, 0.30, 0.10, 1.0 / 60.0);
                    lo = Math.Min(lo, slam.ThrustFactor);
                    hi = Math.Max(hi, slam.ThrustFactor);
                    egtMax = Math.Max(egtMax, slam.EgtKick);
                }
                Log(string.Format("  喘振拍子：推力 ×{0:F2}~×{1:F2}｜排气温度上冲 {2:F0} K｜{3}",
                    lo, hi, egtMax, Surge.Describe(slam)));

                // ④ 同样的中等推（0.55/s）——坏设计喘、好设计不喘
                SurgeRuntime weak = new SurgeRuntime();
                SurgeRuntime good = new SurgeRuntime();
                for (int i = 0; i < 40; i++)
                {
                    Surge.Step(weak, 0.90, 0.55, 0.70, 0.30, 0.60, 1.0 / 30.0);
                    Surge.Step(good, 0.90, 0.55, 0.70, 0.30, 0.10, 1.0 / 30.0);
                }
                Log(string.Format("  中等推 0.55/s：坏设计（裕度 0.60）喘 {0} 次｜好设计（裕度 0.10）喘 {1} 次",
                    weak.Events, good.Events));

                bool okSlow = slow.Events == 0;
                bool okSlam = slam.Events > 0;
                bool okBeat = lo < 0.45 && hi > 0.85 && egtMax > 100.0;
                bool okDesign = weak.Events > 0 && good.Events == 0;
                Log(string.Format("  断言：缓推不喘：{0}｜猛推必喘：{1}｜拍子与温升都对：{2}｜只有坏设计才喘：{3}",
                    YN(okSlow), YN(okSlam), YN(okBeat), YN(okDesign)));
            }
            catch (Exception e) { Log("喘振链路异常: " + e); }

            // ---- 第五阶段⑥：PAW 上到底还挂着几行 ----
            // 玩家点开零件只想看推力、转速、油耗；排查用的口径（羽流模板名、RSE 分工、解算器自述）
            // 一律退到日志。这一行把"给玩家看的东西"固定成一份清单，改字段时能立刻看出多了什么。
            try
            {
                int shown = 0, pruned = 0;
                List<string> names = new List<string>();
                FieldInfo[] fis = typeof(ModuleJetForgeEngine).GetFields(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < fis.Length; i++)
                {
                    object[] at = fis[i].GetCustomAttributes(typeof(KSPField), true);
                    if (at.Length == 0) continue;
                    KSPField f = (KSPField)at[0];
                    if (!f.guiActive) continue;
                    // 原版自带、OnStart 里会关掉的那几行：自检跑在 prefab 上，得自己扣掉。
                    if (Array.IndexOf(ModuleJetForgeEngine.StockPawKeys, fis[i].Name) >= 0)
                    {
                        pruned++;
                        continue;
                    }
                    shown++;
                    names.Add(f.guiName);
                }
                Log(string.Format("PAW：{0} 项——{1}", shown, string.Join(" / ", names.ToArray())));
                Log(string.Format("  原版重复项已关掉 {0} 个：同一块面板上挂着两组口径不同的数，比少一行更难查",
                    pruned));
            }
            catch (Exception e) { Log("PAW 清单异常: " + e); }
        }
    }
}
