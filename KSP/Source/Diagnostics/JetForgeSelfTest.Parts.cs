using System;
using System.Collections.Generic;
using JetForge.Core;
using JetForge.FX;
using JetForge.KSP;
using JetForge.UI;

namespace JetForge.Diagnostics
{
    /// <summary>
    /// 自检里按主题分出来的部分：每段原本是主文件里一串自成一体的 try/catch，
    /// 只往日志里写字、不读主流程的局部变量，搬出来是为了主文件能一眼看完「跑了几件事」。
    /// </summary>
    public partial class JetForgeSelfTest
    {
        /// <summary>高空推力衰减、慢车对满油门、风车阻力。</summary>
        private void SectionAltitudeAndWindmill()
        {
            // ---- 高空推力衰减：两档模式是不是同一条曲线 ----
            // 老账说"残酷模式高原偏乐观约 15%"。这里量出来，并且把机制拆给人看：
            // 差值全部来自"简单模式没有换算流量特性线"（残酷靠压气机特性把高空换算流量抬上去）。
            try
            {
                EngineDesignData la = new EngineDesignData();
                DesignConstants lc = OffDesign.DeriveDesignConstants(la);
                SimpleDesign ls = new SimpleDesign();
                SimpleConstants lsc = Simple.DesignConstants(ls);
                double slF = OffDesign.Solve(la, 0.0, 0.0, 1.0, 0.0, lc).NetThrust;
                double slS = Simple.DesignPoint(ls).NetThrust;

                double[] alts = { 6000.0, 11000.0, 15000.0 };
                for (int i = 0; i < alts.Length; i++)
                {
                    double p, t;
                    Gas.Isa(alts[i], out p, out t);
                    double delta = p / 101325.0;
                    OffDesignResult r = OffDesign.Solve(la, alts[i], 0.0, 1.0, 0.0, lc);
                    SimpleResult s = Simple.Solve(ls, alts[i], 0.0, 1.0, -1.0, lsc);
                    double fh = r.NetThrust / Math.Max(slF, 1.0);
                    double fs = s.NetThrust / Math.Max(slS, 1.0);
                    Log(string.Format("{0:F0} m 高空静止：气压只有海平面的 {1:P0}；残酷模式 {2:F2} kN，是海平面的 {3:P0}（即气压比的 {4:F2} 倍，流量系数 {5:F3}）；简单模式 {6:F2} kN，是海平面的 {7:P0}（气压比的 {8:F2} 倍）；两档差 {9:+0.0;-0.0}%",
                        alts[i], delta, r.NetThrust / 1000.0, fh, fh / Math.Max(delta, 1e-6), r.WcFrac,
                        s.NetThrust / 1000.0, fs, fs / Math.Max(delta, 1e-6), (fh - fs) / Math.Max(fs, 1e-9) * 100.0));
                }

                OffDesignResult idl = OffDesign.Solve(la, 0.0, 0.0, 0.0, 0.0, lc);
                OffDesignResult ful = OffDesign.Solve(la, 0.0, 0.0, 1.0, 0.0, lc);
                Log(string.Format("慢车对满油门：进气 {0:F1} 对 {1:F1} kg/s（{2:P0}），推力只剩 {3:P0}，换算转速 {4:F3}（换算流量按转速线性走；改前是按转速平方走）",
                    idl.MdotAir, ful.MdotAir, idl.MdotAir / Math.Max(ful.MdotAir, 1e-9),
                    idl.NetThrust / Math.Max(ful.NetThrust, 1e-9), idl.NCorrected));
            }
            catch (Exception e) { Log("高空衰减异常: " + e); }

            // ---- 风车：熄火以后这台机器是阻力，不是零 ----
            try
            {
                EngineDesignData wd = new EngineDesignData();
                double rIn = JetForgeMeshBuilder.InletRadius(wd);
                double wire = Math.PI * rIn * rIn;
                double wsl = OffDesign.Solve(wd, 0.0, 0.0, 1.0, 0.0, OffDesign.DeriveDesignConstants(wd)).NetThrust;
                Log(string.Format("风车迎风面积：进气口半径 {0:F3} m → A={1:F3} m²", rIn, wire));

                double[] wAlt = { 0.0, 5000.0, 11000.0 };
                double[] wMa = { 0.0, 0.4, 0.8, 0.95 };
                for (int i = 0; i < wAlt.Length; i++)
                {
                    for (int j = 0; j < wMa.Length; j++)
                    {
                        WindmillState wm = Windmill.Compute(wd, wAlt[i], wMa[j], wire);
                        double pct = Windmill.DragPercentOfMax(wm, wsl);
                        Log(string.Format("  风车 {0,5:F0} m M{1:F2}：阻力 {2,6:F2} kN　Cd {3:F2}　占海平面满推力 {4,5:F1}%　N2 {5:F0}%",
                            wAlt[i], wMa[j], wm.DragN / 1000.0, wm.Cd, pct, wm.N2Pct));
                    }
                }

                WindmillState still = Windmill.Compute(wd, 0.0, 0.0, wire);
                Log(string.Format("风车静止：阻力 {0:F3} kN（应为 0，没有来流）｜再点火起点 {1:P0}",
                    still.DragN / 1000.0, still.SpoolStart));
            }
            catch (Exception e) { Log("风车异常: " + e); }
        }
        /// <summary>设计指纹与表缓存：四台同型号只该建一张表。</summary>
        private void SectionDesignTables()
        {
            // ---- 设计指纹与表缓存：四台同型号只该建一张表 ----
            // 这一段的理由是一艘装了 4 台发动机的火箭：每台 PartModule 各建一张 0.2 s 的表，
            // 载入存档就是 0.8 s 的硬卡顿。缓存键是设计指纹（55 个公有字段全进去），
            // 所以"改了一个参数却拿到旧表"这种事物理上不可能。
            try
            {
                GridFactory.ClearCache();
                EngineDesignData cd = new EngineDesignData();
                DesignConstants cdc = OffDesign.DeriveDesignConstants(cd);

                System.Diagnostics.Stopwatch csw = System.Diagnostics.Stopwatch.StartNew();
                PerformanceGrid cold = GridFactory.BuildCached(cd, cdc);
                double coldMs = csw.Elapsed.TotalMilliseconds;

                csw.Restart();
                for (int i = 0; i < 4; i++)
                {
                    EngineDesignData unit = cd.Clone();
                    GridFactory.BuildCached(unit, OffDesign.DeriveDesignConstants(unit));
                }
                double fleetMs = csw.Elapsed.TotalMilliseconds;

                Log(string.Format("表缓存：冷建一张 {0:F0} ms；四台同型号再取 4 次 {1:F1} ms（未命中 {2} 次，命中 {3} 次）",
                    coldMs, fleetMs, GridFactory.CacheMisses, GridFactory.CacheHits));
                Log(string.Format("  若没有缓存，四台各建一张约 {0:F0} ms —— 载入存档时的卡顿就是这么多",
                    cold.BuildSeconds * 1000.0 * 4.0));

                EngineDesignData tweak = cd.Clone();
                tweak.Fan.Diameter += 0.05;
                PerformanceGrid rt = GridFactory.BuildCached(tweak, OffDesign.DeriveDesignConstants(tweak));
                Log(string.Format("  改了风扇直径之后是另一张表：{0}（推力 {1:F2} → {2:F2} kN）",
                    YN(!ReferenceEquals(rt, cold)),
                    cold.Interp(1.0, 0.0, 0.0).Thrust / 1000.0, rt.Interp(1.0, 0.0, 0.0).Thrust / 1000.0));
                GridFactory.ClearCache();
            }
            catch (Exception e) { Log("表缓存异常: " + e); }
        }

        /// <summary>整机长度真的进物理（管道损失）。</summary>
        private void SectionEngineLength()
        {
            // ---- 整机长度 → 管道损失（目标④）：长度是真的进物理，不是只改外形 ----
            try
            {
                EngineDesignData shortD = Presets.Build("J79");
                EngineDesignData longD = Presets.Build("J79");
                longD.Length = Math.Min(longD.Length * 1.7, 8.5);
                CycleResult rs = Cycle.SolveDesignPoint(shortD);
                CycleResult rl = Cycle.SolveDesignPoint(longD);
                Log(string.Format("整机长度：{0:F2} m → 推力 {1:F2} kN｜{2:F2} m → 推力 {3:F2} kN（{4:+0.00;-0.00}%）",
                    shortD.Length, rs.Thrust / 1000.0, longD.Length, rl.Thrust / 1000.0,
                    (rl.Thrust - rs.Thrust) / Math.Max(rs.Thrust, 1.0) * 100.0));
                Log("  " + string.Format("设计点管道损失：进气 {0:P2}｜核心 {1:P2}｜排气 {2:P2}｜外涵 {3:P2}（整机 {4:F2} m）",
                    rs.DuctInletLoss, rs.DuctCoreLoss, rs.DuctExhaustLoss, rs.DuctBypassLoss, shortD.Length));
            }
            catch (Exception e) { Log("长度影响异常: " + e); }
        }

        /// <summary>转速读数的命名按轴数走。</summary>
        private void SectionSpoolReadout()
        {
            // ---- 转速读数：单转子叫 N1，双轴有 N1/N2，三轴才轮得到 N3 ----
            // 真机手册第一页就写 N1，所以单转子机报 "N2 100%" 是错的命名。
            // 另外三条轴的转速必须互不相同：设计转速由各自叶轮的最大叶尖马赫定出来。
            try
            {
                EngineDesignData s1 = Presets.Build("J85");
                OffDesignResult r1 = OffDesign.Solve(s1, 0.0, 0.0, 1.0, 0.0, OffDesign.DeriveDesignConstants(s1));
                Log(string.Format("转速读数 单转子 J85：N1 {0:F1}%　N2 {1:F1}%　N3 {2:F1}%（真机只有 N1）",
                    r1.N1Pct, r1.N2Pct, r1.N3Pct));

                EngineDesignData s2d = new EngineDesignData();
                OffDesignResult r2 = OffDesign.Solve(s2d, 0.0, 0.0, 1.0, 0.0, OffDesign.DeriveDesignConstants(s2d));
                Log(string.Format("转速读数 双轴 JF-1：N1 {0:F1}%　N2 {1:F1}%　N3 {2:F1}%",
                    r2.N1Pct, r2.N2Pct, r2.N3Pct));

                EngineDesignData s3d = new EngineDesignData();
                s3d.ShaftCount = 3;
                s3d.Fan.Shaft = 1;
                s3d.Lpc.Stages = 3; s3d.Lpc.TipDiameter = 0.78; s3d.Lpc.Shaft = 2;
                s3d.Hpc.Stages = 6; s3d.Hpc.TipDiameter = 0.52; s3d.Hpc.Shaft = 3;
                s3d.Hpt.Stages = 1; s3d.Hpt.Shaft = 3;
                s3d.Ipt.Stages = 1; s3d.Ipt.TipDiameter = 0.70; s3d.Ipt.Shaft = 2;
                s3d.Lpt.Stages = 3; s3d.Lpt.Shaft = 1;
                DesignConstants c3d = OffDesign.DeriveDesignConstants(s3d);
                OffDesignResult r3 = OffDesign.Solve(s3d, 0.0, 0.0, 1.0, 0.0, c3d);
                Log(string.Format("转速读数 三轴：N1 {0:F1}%　N2 {1:F1}%　N3 {2:F1}%（N2/N3 同比例是共用换算转速的简化，物理转速不同）",
                    r3.N1Pct, r3.N2Pct, r3.N3Pct));

                double gap = double.MaxValue;
                int[] ks = new int[r3.Rpm.Count]; r3.Rpm.Keys.CopyTo(ks, 0);
                for (int i = 0; i < ks.Length; i++)
                    for (int j = i + 1; j < ks.Length; j++)
                        gap = Math.Min(gap, Math.Abs(r3.Rpm[ks[i]] - r3.Rpm[ks[j]]));
                double tipMax = 0.0;
                foreach (KeyValuePair<int, double> kv in r3.TipSpeed) tipMax = Math.Max(tipMax, kv.Value);
                Log(string.Format("  三根轴最小转速差 {0:F0} rpm　最大叶尖线速度 {1:F0} m/s", gap, tipMax));

                // 涡轮刚好够用的那一档：非设计点会掉转，转速按 sqrt(供/需) 缩
                EngineDesignData edge = s3d.Clone();
                edge.Ipt.PsiScale = 0.45;
                DesignConstants ce = OffDesign.DeriveDesignConstants(edge);
                OffDesignResult fullB = OffDesign.Solve(edge, 0.0, 0.0, 1.0, 0.0, ce);
                string sf = "无";
                if (fullB.SpoolShortfall.Count > 0)
                {
                    sf = "";
                    foreach (KeyValuePair<int, double> kv in fullB.SpoolShortfall)
                        sf += string.Format("轴{0}×{1:P0} ", kv.Key, kv.Value);
                }
                Log(string.Format("  边缘涡轮（IPT psi 0.45）：满油门 掉转 {0}｜IPT 需/供 {1:F2}｜N2 {2:F1}% < N3 {3:F1}%",
                    sf.Trim(), fullB.WIpt / Math.Max(fullB.CapIpt, 1.0), fullB.N2Pct, fullB.N3Pct));
            }
            catch (Exception e) { Log("转速读数异常: " + e); }
        }
        /// <summary>我的设计：存进真的零件目录、再读回来。</summary>
        private void SectionMyDesign()
        {
            // ---- 我的设计：存文件、读回来（用真的零件目录，验完删掉）----
            try
            {
                string dir = KSPUtil.ApplicationRootPath + "GameData/JetForge/UserDesigns/_selftest";
                if (System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, true);

                EngineDesignData ul = LoadDesignFromGameDatabase();
                ul.DesignName = "自检/三轴 加力";   // 名字里带斜杠，顺手验净化
                ul.ShaftCount = 3;
                ul.Afterburner.Enabled = true;
                ul.Nozzle = NozzleKind.ConvergingDiverging;

                string path = UserLibrary.Save(dir, ul.DesignName, ul);
                Log("我的设计：存到 " + (path ?? "(失败)"));

                List<UserDesignEntry> list = UserLibrary.List(dir);
                Log(string.Format("  目录里 {0} 个｜净化后的文件名 {1}", list.Count,
                    list.Count > 0 ? System.IO.Path.GetFileName(list[0].Path) : "(无)"));

                string err;
                EngineDesignData back = UserLibrary.Load(path, out err);
                if (back == null)
                {
                    Log("  读不回来：" + err);
                }
                else
                {
                    List<string> diffs = new List<string>();
                    CmpFields(ul, back, "design", diffs);
                    Log(string.Format("  读回来字段不一致 {0} 处｜轴数 {1}｜燃烧室 L/H {2:F2}｜加力 {3}",
                        diffs.Count, back.ShaftCount, back.Burner.LinerLd, back.Afterburner.Enabled ? "有" : "没有"));
                    if (diffs.Count > 0) Log("    第一处：" + diffs[0]);
                }

                EngineDesignData broken = UserLibrary.Load(dir + "/不存在.cfg", out err);
                Log("  坏文件：设计=" + (broken == null ? "null" : "有") + "｜理由=" + err);

                Log("  删除这份设计：" + (UserLibrary.Delete(path) ? "删掉了" : "没删掉") + "｜删完还剩 " + UserLibrary.List(dir).Count + " 个");
                if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            }
            catch (Exception e) { Log("我的设计异常: " + e); }
        }

        /// <summary>加力点火边界：高空点不着、油多会吹熄。</summary>
        private void SectionAfterburnerLight()
        {
            // ---- 加力点火边界：高空点不着、油多会吹熄 ----
            try
            {
                EngineDesignData ab = LoadDesignFromGameDatabase();
                ab.Afterburner.Enabled = true;
                ab.Afterburner.FuelRatio = 0.035;
                ab.Afterburner.ExitTemp = 0.0;

                AbResult a1 = AfterburnerLight.Check(ab, 0.0, 0.0, true);
                AbResult a2 = AfterburnerLight.Check(ab, 11000.0, 0.90, true);
                AbResult a3 = AfterburnerLight.Check(ab, 20000.0, 0.90, true);
                AbResult a4 = AfterburnerLight.Check(ab, 20000.0, 2.00, true);
                Log(string.Format("加力点火：SL {0}/{1:F0}kPa｜11km {2}/{3:F0}｜20km M0.9 {4}/{5:F0}｜20km M2 {6}/{7:F0}",
                    a1.Status, a1.DuctKPa, a2.Status, a2.DuctKPa, a3.Status, a3.DuctKPa, a4.Status, a4.DuctKPa));
                Log("  点不着的原因：" + a3.Note);
                Log(string.Format("  加力高度上限：M0.90 ≈ {0:F0} m", AfterburnerLight.CeilingMeters(ab, 0.90)));

                EngineDesignData leanD = ab.Clone();
                leanD.Afterburner.FuelRatio = 0.002;
                AbResult lr = AfterburnerLight.Check(leanD, 0.0, 0.0, true);
                EngineDesignData richD = ab.Clone();
                richD.Afterburner.FuelRatio = 0.080;
                AbResult rr = AfterburnerLight.Check(richD, 0.0, 0.0, true);
                Log(string.Format("  贫油 0.002 → {0}｜富油 0.080 → {1}", lr.Status, rr.Status));

                EngineDesignData highD = ab.Clone();
                highD.DesignAlt = 20000.0;
                highD.DesignMach = 0.90;
                bool warned = false;
                foreach (Finding f in Criteria.Evaluate(highD, Criteria.ViewOf(Cycle.SolveDesignPoint(highD))).Findings)
                    if (f.Key == "ab.light" && f.LevelValue == Level.Warn) warned = true;
                Log("  设计点定在 20km M0.9，判据标出「点不着」：" + (warned ? "是" : "不是"));
            }
            catch (Exception e) { Log("加力点火异常: " + e); }
        }

        /// <summary>空中再起动窗口：三条墙分开报。</summary>
        private void SectionRelightWindow()
        {
            // ---- 空中再起动窗口：三条墙分开报，四档点火器的高度上限各不相同 ----
            try
            {
                EngineDesignData re = LoadDesignFromGameDatabase();
                ControlKind ctlNow = ControlSystem.Effective(re);
                RelightResult w1 = Relight.Window(re, 3000.0, 0.90, 0.40);
                RelightResult w2 = Relight.Window(re, 3000.0, 0.90, 0.06);
                RelightResult w3 = Relight.Window(re, 22000.0, 0.90, 0.40);
                Log(string.Format("再起动：3km 转速 40% {0}｜3km 转速 6% {1}｜22km 转速 40% {2}",
                    w1.CanLight ? "能点" : "点不着", w2.CanLight ? "能点" : "点不着", w3.CanLight ? "能点" : "点不着"));
                Log("  转速太低的理由：" + w2.Reason);
                Log("  空气太稀的理由：" + w3.Reason);
                Log(string.Format("  点火高度上限（Ma0.9）：机械 {0:F0} m｜液压 {1:F0} m｜模拟 {2:F0} m｜数字 {3:F0} m｜本机（{4}）自动再点火 {5}",
                    Relight.CeilingMeters(re, 0.90, ControlKind.Mechanical),
                    Relight.CeilingMeters(re, 0.90, ControlKind.Hydro),
                    Relight.CeilingMeters(re, 0.90, ControlKind.Analog),
                    Relight.CeilingMeters(re, 0.90, ControlKind.Fadec),
                    ControlSystem.KindName(ctlNow), Relight.AutoRelight(ctlNow) ? "有" : "没有"));
            }
            catch (Exception e) { Log("再起动窗口异常: " + e); }
        }

        /// <summary>设计缺陷在飞行中兑现的那条链路。</summary>
        private void SectionDefectChain(EngineDesignData design)
        {
            // ---- 第四阶段①：设计缺陷在飞行中真的兑现了吗 ----
            try
            {
                EngineDesignData dd = LoadDesignFromGameDatabase();
                CheckReport rep = Criteria.Evaluate(dd, Criteria.ViewOf(Cycle.SolveDesignPoint(dd)));
                Defects.Risk rk = Defects.Read(rep.Findings);
                Log(string.Format("缺陷档案：喘振 {0:F2}｜超温 {1:F2}｜熄火 {2:F2}｜超速 {3:F2}",
                    rk.Surge, rk.OverTemp, rk.Blowout, rk.Overspeed));

                // 一台健康机满油门不该有任何后果：判据全是 OK 时推力系数必须还是 1。
                // 这里温度给 900 K、上限 1200 K——以前我喂了 1500/1200，那是"超温 25%"，
                // 测出来当然会烧，却把结论写成"健康机也超温"，差点自己去改没坏的代码。
                DefectRuntime healthy = new DefectRuntime();
                Defects.Risk none = new Defects.Risk();
                for (int i = 0; i < 120; i++) Defects.Step(none, healthy, 1.0 / 30.0, 1.0, 900.0, 1200.0, 0.020, 0.0);
                Log(string.Format("  健康机满油门 4 秒（EGT 900/上限 1200）：{0}｜推力 ×{1:F3}｜损伤 {2:F0}%",
                    healthy.State, healthy.ThrustScale, healthy.Damage * 100.0));

                // 真实判据跑出来的风险（JF-1 本身会带流道偏窄的 WARN）
                DefectRuntime live = new DefectRuntime();
                double minThr = 1.0, maxThr = 0.0;
                for (int i = 0; i < 120; i++)
                {
                    // EGT 1150 / 上限 1200：干态正常工况，不该烧蚀
                    Defects.Step(rk, live, 1.0 / 30.0, 0.92, 1150.0, 1200.0, 0.012, 8000.0);
                    minThr = Math.Min(minThr, live.ThrustScale);
                    maxThr = Math.Max(maxThr, live.ThrustScale);
                }
                Log(string.Format("  实战风险 4 秒：{0}（{1}）｜推力 ×{2:F2}~×{3:F2}｜损伤 {4:F0}%",
                    live.State, Defects.Describe(rk, live), minThr, maxThr, live.Damage * 100.0));

                // ---- 判据不成立时：**降额，不是停机** ----
                // 以前判据 FAIL 会把推力直接写成 0，玩家搭出来的东西"能点着火但不走动"，
                // 而屏幕上什么错都没说清楚。现在按缺陷严重度打折，还能飞，SFC 自己变差。
                double derateFail = Defects.DesignDerate(rep);
                CheckReport fakeBad = new CheckReport();
                foreach (Finding f in rep.Findings) fakeBad.Findings.Add(f);
                fakeBad.Findings.Add(new Finding("test.fail", Level.Fail, "测试用 FAIL"));
                double derateBad = Defects.DesignDerate(fakeBad);
                double derateOk = Defects.DesignDerate(new CheckReport());
                Log(string.Format("  降额：本机 {0:F2}（{1} 条 FAIL）｜造一条 FAIL {2:F2}｜全 OK {3:F2}｜{4}",
                    derateFail, rep.Fails.Count, derateBad, derateOk,
                    Defects.DescribeDerate(derateBad, fakeBad)));
                Log(string.Format("  断言：全 OK 不降额 {0}｜有 FAIL 落到 0.78 以下 {1}｜降到地板也只到 0.60 {2}",
                    Math.Abs(derateOk - 1.0) < 1e-9 ? "对" : "不对",
                    derateBad < 0.781 ? "对" : "不对",
                    derateBad >= 0.60 ? "对" : "不对"));

                // ---- 故障：只剩两条路会真的断推力（喘振 / 烧穿）----
                // 过载那条已按玩家要求整条撤掉：g 值只记峰值，不进故障门槛。
                DefectRuntime surgeRt = new DefectRuntime();
                for (int i = 0; i < 40; i++)
                    Defects.Step(new Defects.Risk(), surgeRt, 1.0 / 30.0, 1.0, 900.0, 1400.0, 0.02, 0.0,
                                 0.0, i % 3 == 0);          // 每三帧一次喘振起跳
                DefectRuntime gRt = new DefectRuntime();
                for (int i = 0; i < 400; i++)
                    Defects.Step(new Defects.Risk(), gRt, 1.0 / 30.0, 1.0, 900.0, 1400.0, 0.02, 0.0, 12.0);
                DefectRuntime burnRt = new DefectRuntime();
                Defects.Risk hot = new Defects.Risk { OverTemp = 1.0 };
                // 烧穿是两步：先把损伤烧到 1（超温 12% → 90 秒），再在损伤状态下继续超温 20 秒。
                // 我第一版只跑了 40 秒，看到"未断"还以为代码坏了——先量一下速率再定循环长度。
                for (int i = 0; i < 4000; i++) Defects.Step(hot, burnRt, 1.0 / 30.0, 1.0, 1350.0, 1200.0, 0.02, 0.0);
                Log(string.Format("  故障：喘振 {0} 次 → {1}｜12 g 拉 400 帧 → {2}（峰值 {3:F1} g）｜烧蚀后超温 133 s → {4}",
                    surgeRt.SurgeEvents, surgeRt.Failed ? surgeRt.FailNote : "未断",
                    gRt.Failed ? gRt.FailNote : "未断", gRt.GPeak,
                    burnRt.Failed ? burnRt.FailNote : "未断"));
                Log(string.Format("  断言：喘振与烧穿都断推 {0}｜断了以后推力为 0 {1}｜12 g 不断推、推力不打折 {2}",
                    (surgeRt.Failed && burnRt.Failed) ? "对" : "不对",
                    (surgeRt.ThrustScale < 1e-9 && burnRt.ThrustScale < 1e-9) ? "对" : "不对",
                    (!gRt.Failed && gRt.ThrustScale > 0.99) ? "对" : "不对"));

                // ---- 门槛由设计推，不是写死 ----
                // 同一套断法，差机器应该更早坏。这里造一台"级负荷紧、裕度小"的机器，
                // 判据给它的风险更高，门槛必须全面更严；好机器则落在合理区间里。
                EngineDesignData rough = design.Clone();
                rough.Hpc.Stages = (int)Math.Max(4, Math.Round(design.Hpc.Stages * 0.55));
                rough.Hpc.TipDiameter = design.Hpc.TipDiameter * 0.86;
                rough.Fan.Diameter = design.Fan.Diameter * 0.90;
                CheckReport roughRep = Criteria.Evaluate(rough, Criteria.ViewOf(Cycle.SolveDesignPoint(rough)),
                                                         null, Gas.P0, 0.0);
                FailureLimits goodLim = Defects.For(design, rep);
                FailureLimits roughLim = Defects.For(rough, roughRep);
                DefectRuntime roughRt = new DefectRuntime();
                Defects.Configure(roughRt, roughLim);
                Log(string.Format("  故障门槛（按设计推）：本机 {0}", goodLim.Basis));
                Log(string.Format("  差机器（HPC {0} 级 × {1:F2} m）：{2}",
                    rough.Hpc.Stages, rough.Hpc.TipDiameter, roughLim.Basis));
                Log(string.Format("  断言：好机器门槛不比差机器松 {0}｜门槛都落在 3~13 次 / 8~40 s {1}",
                    (goodLim.SurgeEvents >= roughLim.SurgeEvents && goodLim.BurnSeconds >= roughLim.BurnSeconds - 1e-9) ? "对" : "不对",
                    (goodLim.SurgeEvents >= 3 && goodLim.SurgeEvents <= 13
                     && goodLim.BurnSeconds >= 8.0 - 1e-9 && goodLim.BurnSeconds <= 40.0 + 1e-9) ? "对" : "不对"));
                // 门槛松不得：同样的喘振频率去揍差机器，先断的必须是它
                for (int i = 0; i < 400 && !roughRt.Failed; i++)
                    Defects.Step(new Defects.Risk(), roughRt, 1.0 / 30.0, 1.0, 900.0, 1400.0, 0.02, 0.0,
                                 0.0, i % 3 == 0);
                Log(string.Format("  同样每三帧一次喘振：差机器 {0}", roughRt.Failed ? roughRt.FailNote : "未断"));
                Log(string.Format("  断言：差机器先断 {0}", roughRt.Failed ? "对" : "不对"));

                // ---- 缺陷三级台账：同一缺陷不重复罚 ----
                // 这条规矩是"玩家的体验问题"，不是数字问题：同一个缺陷被设计期与运行期各罚一次，
                // 推力只剩四成，而报表上只说了一遍理由——只能靠断言钉住。
                CheckReport fbRep = new CheckReport();
                fbRep.Findings.Add(new Finding("hpc.stage_load", Level.Fail, "高压压气机级负荷过高（自检例）", 0.62, 0.55, ""));
                FaultBook fb = new FaultBook();
                fb.BookFindings(fbRep);
                bool fbNewRisk = fb.Raise("高压压气机级负荷过高（自检例）", FaultTier.Risk, FaultSeverity.Major,
                                          "喘振强度", 0.55, 1.0, "", "推力 ×0.45", "收油门");
                string fbOverlap;
                bool fbOverlapHit = fb.DesignOverlapsRuntime(fbRep, out fbOverlap);
                fb.Step(1.0 / 30.0);
                Log(string.Format("  缺陷台账（造例）：{0}", fb.Describe(3).Replace("\n", " / ")));
                Log(string.Format("  实战台账：{0}", live.Book.Count > 0 ? live.Book.Describe(2).Replace("\n", " / ") : "空（这一趟没犯病）"));
                Log(string.Format("  断言：同码跨级不算新缺陷 {0}｜认得出设计判据与运行风险重叠 {1}｜重叠的是那条判据 {2}",
                    fbNewRisk ? "不对" : "对",
                    fbOverlapHit ? "对" : "不对",
                    fbOverlap == "高压压气机级负荷过高（自检例）" ? "对" : "不对"));
            }
            catch (Exception e) { Log("缺陷链路异常: " + e); }
        }
    }
}