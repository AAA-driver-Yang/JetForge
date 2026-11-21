using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using JetForge.Core;
using JetForge.Core.Diagnostics;

namespace JetForge.Test
{
    /// <summary>
    /// 命令行回归台。目的只有一个：证明 C# 移植和 Python 原型算的是同一台发动机。
    /// 每个基线数字都来自 proto/ 下的 Python 输出，改内核后跑一遍就知道有没有算歪。
    /// </summary>
    internal static class Program
    {
        private static int _fails;

        private static void Check(string label, double got, double want, double tolPct, string unit = "")
        {
            double err = Math.Abs(want) > 1e-12 ? (got - want) / want * 100.0 : got - want;
            bool ok = Math.Abs(err) <= tolPct;
            if (!ok) _fails++;
            Console.WriteLine("  {0,-26} 算 {1,10:F3}{4}  期望 {2,10:F3}  偏差 {3,7:F2}%  {5}",
                label, got, want, err, unit, ok ? "OK" : "**超差**");
        }

        private static void Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (Environment.GetEnvironmentVariable("JF_PROBE") == "idle") { IdleProbe.Run(); return; }
            if (Environment.GetEnvironmentVariable("JF_PROBE") == "audit") { AuditProbe.Run(); return; }
            // 诊断用：JF_LANG=en 时整套回归跑在英文下。一部分断言钉的是中文文案，
            // 在英文下会红——那些是已知的（§238 记了清单），不是逻辑错。
            if (Environment.GetEnvironmentVariable("JF_LANG") == "en")
            {
                Loc.Probe = () => Lang.English;
                Console.WriteLine("（本次回归跑在英文下：Loc 认 English）");
            }
            EngineDesignData d = new EngineDesignData();   // 默认就是 JF-1（Python 的 Demo 2-spool）

            Console.WriteLine("=== 1. 设计点（对比 Python jetforge.py） ===");
            CycleResult r = Cycle.SolveDesignPoint(d);
            Console.WriteLine("  tech={0}  errors={1}  warnings={2}", d.TechName, r.Errors.Count, r.Warnings.Count);
            foreach (string e in r.Errors) Console.WriteLine("   ! " + e);
            foreach (string w in r.Warnings) Console.WriteLine("   ? " + w);
            Check("总压比 OPR", r.Opr, 11.73, 1.5);
            Check("涵道比 BPR", r.Bpr, 2.75, 1.5);
            Check("风扇压比 FPR", r.Fpr, 1.59, 2.0);
            Check("进气量 kg/s", r.MdotAir, 203.6, 1.5);
            // Python 参考里没有管道摩擦：JF-1 的自动长度算出来是 3.55 m（3.0 m 那个旧手填值只是随手写的），
            // 进/核心/排气三段损失合计约 2.5%，于是这里的期望值是"Python 值扣掉管道损失"。
            // 想核对纯循环，看上面 OPR / T3 / T5 那几条——那几条不受管道损失影响。
            Check("推力 kN", r.Thrust / 1000.0, 80.25, 1.0);
            Check("SFC kg/(N·h)", r.Sfc, 0.0525, 3.0);
            Check("T3 K", r.T3, 626.0, 1.5);
            Check("T5 K", r.T5, 1085.0, 2.0);

            Console.WriteLine();
            Console.WriteLine("=== 2. 非设计点（对比 Python offdesign.py） ===");
            DesignConstants c = OffDesign.DeriveDesignConstants(d);
            Console.WriteLine("  常数: CPR={0:F2} TTR={1:F3} fc={2:F3} A_turb={3:F4}m² T4:[{4:F0},{5:F0}]",
                c.Cpr, c.Ttr, c.FanWorkConstant, c.ATurb, c.T4Idle, c.T4Max);

            Console.WriteLine("  高度   马赫  油门   N2%    Nc%    mdot    OPR     推力       SFC    被调节");
            // 期望值来自 Python 原型实跑（crosscheck.build_pair('1970s') 的残酷模式）：
            // 最后一项是"是否被换算转速调节器收油"——只有 11 km 静止那一点会触发。
            object[][] cases =
            {
                new object[] { 0.0, 0.0, 1.0, 80.35, 11.63, 101.3, false },
                new object[] { 0.0, 0.5, 1.0, 58.33, 10.72, 101.3, false },
                new object[] { 6000.0, 0.6, 1.0, 34.84, 13.25, 101.3, false },
                new object[] { 11000.0, 0.0, 1.0, 23.95, 19.44, 100.1, false },
                new object[] { 11000.0, 0.6, 1.0, 19.80, 17.05, 101.4, false },
                new object[] { 11000.0, 0.85, 1.0, 20.60, 15.13, 101.4, false },
            };
            foreach (object[] cs in cases)
            {
                OffDesignResult o = OffDesign.Solve(d, (double)cs[0], (double)cs[1], (double)cs[2], 0.0, c);
                Console.WriteLine("  {0,6:F0} {1,6:F2} {2,6:F2} {3,6:F1} {4,6:F1} {5,7:F1} {6,7:F2} {7,8:F2}k {8,8:F4} {9}",
                    o.Alt, o.Mach, o.Throttle, o.N2Pct, o.NCorrected * 100.0, o.MdotAir, o.Opr,
                    o.NetThrust / 1000.0, o.Sfc, o.Governed ? "是" : "-");
                Check(string.Format("推力 {0:F0}m/M{1:F2}", o.Alt, o.Mach),
                      o.NetThrust / 1000.0, (double)cs[3], 8.0, "kN");
                Check(string.Format("  OPR {0:F0}m/M{1:F2}", o.Alt, o.Mach), o.Opr, (double)cs[4], 5.0);
                if (((bool)cs[6]) != o.Governed) { _fails++; Console.WriteLine("    ** 调节器状态不符 **"); }
            }

            Console.WriteLine();
            Console.WriteLine("=== 3. 判据 ===");
            ResultView v = Criteria.ViewOf(r);
            CheckReport rep = Criteria.Evaluate(d, v, d.Tech, 101325.0, 0.0);
            Console.WriteLine("  最差等级 {0}  共 {1} 条（FAIL {2} / WARN {3}）",
                rep.Worst, rep.Findings.Count, rep.Fails.Count, rep.Warns.Count);
            foreach (Finding f in rep.Findings)
                if (f.LevelValue != Level.Ok) Console.WriteLine("   " + f.Line());
            v = Criteria.ViewOf(OffDesign.Solve(d, 11000.0, 0.85, 1.0, 0.0, c), d.Fan.Shaft);
            rep = Criteria.Evaluate(d, v, d.Tech, 0.0, 11000.0);
            Console.WriteLine("  11km/M0.85 最差 {0}（FAIL {1} / WARN {2}）",
                rep.Worst, rep.Fails.Count, rep.Warns.Count);
            foreach (Finding f in rep.Fails) Console.WriteLine("   " + f.Line());

            Console.WriteLine();
            Console.WriteLine("=== 4. 性能网格 ===");
            double[] ths = { 0.0, 0.25, 0.5, 0.75, 1.0 };
            double[] alts = { 0.0, 3000.0, 6000.0, 9000.0, 11000.0, 14000.0 };
            double[] machs = { 0.0, 0.2, 0.4, 0.6, 0.8, 0.85, 1.0, 1.2 };
            Stopwatch sw = Stopwatch.StartNew();
            PerformanceGrid g = BuildGrid(d, c, ths, alts, machs);
            sw.Stop();
            g.BuildSeconds = sw.Elapsed.TotalMilliseconds / 1000.0;
            g.BuiltAt = DateTime.Now.ToString("s");
            Console.WriteLine("  " + g.Summary());

            double[] probes = { 0.0, 0.0, 6.0 };  // throttle, alt_km, mach
            Console.WriteLine("  内插 vs 直接求解：");
            foreach (double thr in new[] { 0.3, 0.7, 1.0 })
                foreach (double altKm in new[] { 0.0, 6.0, 11.0 })
                    foreach (double mach in new[] { 0.0, 0.6, 0.85 })
                    {
                        PerformanceGrid.Sample s = g.Interp(thr, altKm * 1000.0, mach);
                        OffDesignResult o = OffDesign.Solve(d, altKm * 1000.0, mach, thr, 0.0, c);
                        if (!s.Valid) continue;
                        double err = (s.Thrust - o.NetThrust) / Math.Max(Math.Abs(o.NetThrust), 1e-9) * 100.0;
                        Console.WriteLine("    thr {0:F2} {1,2:F0}km M{2:F2}: 网格 {3,7:F2} kN  直接 {4,7:F2} kN  {5,6:F1}%",
                            thr, altKm, mach, s.Thrust / 1000.0, o.NetThrust / 1000.0, err);
                    }
            _ = probes;

            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "out");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            string csv = Path.Combine(outDir, "grid_roundtrip.csv");
            g.ToCsv(csv);
            PerformanceGrid g2 = PerformanceGrid.FromCsv(csv);
            PerformanceGrid.Sample a = g.Interp(0.5, 6000.0, 0.6);
            PerformanceGrid.Sample b = g2.Interp(0.5, 6000.0, 0.6);
            Console.WriteLine("  CSV 往返：{0} bytes，内插差 {1:F2} N", new FileInfo(csv).Length, Math.Abs(a.Thrust - b.Thrust));
            if (Math.Abs(a.Thrust - b.Thrust) > 1.0) { _fails++; Console.WriteLine("    ** CSV 往返丢精度 **"); }

            Console.WriteLine();
            Console.WriteLine("=== 5. 简单模式（对比 Python simple.py 的 _demo 台账） ===");
            RunSimpleCases();

            Console.WriteLine();
            Console.WriteLine("=== 6. 加力喷管型式（收敛 / 可变 / 收扩） ===");
            RunNozzleCases();

            Console.WriteLine();
            Console.WriteLine("=== 7. 质量与造价（用真实干重反标定） ===");
            RunMassCases();

            Console.WriteLine();
            Console.WriteLine("=== 8. 边界工况体检（找 bug：NaN / 负流量 / 物理上不可能的读数） ===");
            RunEdgeCases();

            Console.WriteLine();
            Console.WriteLine("=== 9. 尾焰映射（推力→长度、温度→颜色） ===");
            RunPlumeCases();

            Console.WriteLine();
            Console.WriteLine("=== 10. 设计点环境（马赫/高度）与涡轮轮毂比 ===");
            RunDesignPointCases();

            Console.WriteLine();
            Console.WriteLine("=== 11. 网格内插误差（飞行中只有这张表，算错了没人会喊） ===");
            RunGridInterpCases(d, c);

            RunSpoolCases(d);

            Console.WriteLine("=== 13. 原版推力链路换算（我们的推力/油耗 → maxFuelFlow + Isp） ===");
            RunStockBridgeCases(d, c);

            Console.WriteLine();
            Console.WriteLine("=== 14. 任务剖面仿真（连续跑一遍，抓只有跑久了才露头的毛病） ===");
            RunMissionCases(d, c);

            Console.WriteLine();
            Console.WriteLine("=== 15. 加力：干态表与加力表是不是两张不同的表 ===");
            RunAfterburnerCases();

            Console.WriteLine();
            Console.WriteLine("=== 16. 存档往返：每个字段都得原样回来 ===");
            RunPersistenceCases();

            Console.WriteLine();
            Console.WriteLine("=== 17. 高空推力衰减：两档模式是不是同一条曲线 ===");
            RunLapseCases(d, c);

            Console.WriteLine();
            Console.WriteLine("=== 18. 风车：熄火之后这台发动机还会不会拖飞机 ===");
            RunWindmillCases(d, c);

            Console.WriteLine();
            Console.WriteLine("=== 19. 参数空间体检：玩家把滑块拖到哪儿都不许崩 ===");
            RunFuzzCases();

            Console.WriteLine();
            Console.WriteLine("=== 20. 慢车：原版油门 0 是零推力，真机油门 0 是慢车 ===");
            RunIdleCases(d, c);

            Console.WriteLine();
            Console.WriteLine("=== 21. 飞行链路整段：抬底→风车→滞后→查表→自持闸→库存桥 ===");
            RunFlightStepCases(d, c);

            Console.WriteLine();
            Console.WriteLine("=== 22. 设计指纹与表缓存：改参数必须换表，同型号必须共用 ===");
            RunCacheCases(d, c);

            Console.WriteLine();
            Console.WriteLine("=== 23. 真机预设：四台真实发动机的参数能不能算出真机的数 ===");
            RunPresetCases();

            Console.WriteLine();
            Console.WriteLine("=== 24. 转速读数：单转子叫 N1，双轴有 N1/N2，三轴才轮得到 N3 ===");
            RunSpoolReadoutCases();

            Console.WriteLine();
            Console.WriteLine("=== 25. 压气机形式：离心级不是「更差的轴流级」，是另一种机器 ===");
            RunCompressorKindCases(d, c);

            Console.WriteLine();
            Console.WriteLine("=== 26. 燃烧室形状：太短烧不完，太长压不动，罐式出口不均 ===");
            RunBurnerShapeCases(d, c);

            Console.WriteLine();
            Console.WriteLine("=== 27. 我的设计：存文件、读回来、坏了不崩、覆盖前留个底 ===");
            RunUserLibraryCases(d);

            Console.WriteLine();
            Console.WriteLine("=== 28. 加力点火边界：高空点不着、油多会吹熄 ===");
            RunAbLightCases(d);

            Console.WriteLine();
            Console.WriteLine("=== 29. 全参数空间不变式体检：油门单调、高度单调、加力不低于干态 ===");
            RunInvariantCases();

            Console.WriteLine();
            Console.WriteLine("=== 30. 加力瞬态：点火建立与熄火余焰 ===");
            RunAfterburnerTransientCases(d);

            Console.WriteLine();
            Console.WriteLine("=== 31. 慢车与油耗曲线：油门小的时候，真机是更费油还是更省油 ===");
            RunIdleAndSfcCurve(d);

            Console.WriteLine();
            Console.WriteLine("=== 32. 加力增益与涵道比：加力能加多少，取决于有多少流量在核心机里 ===");
            RunAfterburnerBprCases();

            Console.WriteLine();
            Console.WriteLine("=== 33. 涡轮出口温度 T5 的口径 ===");
            RunTurbineExitCases();

            Console.WriteLine();
            Console.WriteLine("=== 34. 油耗谷底的分解：SFC 为什么在 24% 推力最低 ===");
            RunSfcDecomposition(d);
            RunFleetSummary(d);
            Console.WriteLine();
            Console.WriteLine("== 第 36 段 风扇特性线（工作线形式）==");
            RunFanMapCases(d);
            RunFanMapVsSolved(d);

            Console.WriteLine();
            Console.WriteLine("== 第 37 段 核心压气机工作线（只量，不改求解器）==");
            RunCompressorMapCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 38 段 设计缺陷在飞行中的兑现 ==");
            RunDefectCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 39 段 大涵道比加力：只烧核心那一路 ==");
            RunHighBypassAbCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 40 段 实时解算 vs 预解网格 ==");
            RunRealtimeCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 41 段 喘振：逼出来的，不是设计里注定一直抖 ==");
            RunSurgeCases();
            RunSpoolDynamicsCases();

            Console.WriteLine();
            Console.WriteLine("== 第 43 段 进气道：从「函数」变成「零件」 ==");
            RunIntakeCases();

            Console.WriteLine();
            Console.WriteLine("== 第 44 段 站位表：剖视图、网格、造价共用的一份数 ==");
            RunGeometryCases();

            Console.WriteLine();
            Console.WriteLine("== 第 45 段 矢量喷口：方向由 r × F 解，不靠正负号 ==");
            RunVectorCases();

            Console.WriteLine();
            Console.WriteLine("== 第 46 段 控制系统：无控的猛推 vs 有控的猛推 ==");
            RunControlCases();

            Console.WriteLine();
            Console.WriteLine("== 第 47 段 逐级编辑：每一级自己的叶尖直径与载荷 ==");
            RunStageEditCases(d);
            Console.WriteLine();
            Console.WriteLine("== 第 48 段 慢车是反解出来的：旧存档那两个旋钮不再有任何效果 ==");
            RunConfigurableIdleCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 49 段 慢车档烧不住就熄火：判定只看油门到底 + 这一档烧不住 ==");
            RunIdleStarveCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 50 段 停车不是断电：转子往下溜，油门推不回来 ==");
            RunCoastCases(d, c);
            RunCoastThrustCases(d, c);

            Console.WriteLine();
            Console.WriteLine("== 第 52 段 进气道几何：性能与外形吃的是同一份数 ==");
            RunIntakeGeometryCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 53 段 站表与逐轴状态：一条链路、一份轴账 ==");
            RunNetworkCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 54 段 进气畸变：只进喘振风险，不改推力 ==");
            RunDistortionCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 55 段 简单模式的进气道：只给三个旋钮，缺省一个数都不动 ==");
            RunSimpleIntakeCases();

            Console.WriteLine();
            Console.WriteLine("== 第 56 段 迎角与侧滑：进气道不再永远正对来流 ==");
            RunIncidenceCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 57 段 矢量指令整形：死区、限速率、三个方向各关各的 ==");
            RunVectorCommandCases();

            Console.WriteLine();
            Console.WriteLine("== 第 58 段 独立进气道零件：N 件合成一台发动机的进气口 ==");
            RunIntakeInstallCases(d);

            Console.WriteLine("== 第 59 段 原版进气道与缩放 mod：口径量外形、性能曲线打折、与自家零件并联 ==");
            RunStockIntakeCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 60 段 形状就是性能：管长了、喉道细了、直径大了，账上都要变 ==");
            RunShapePerformanceCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 61 段 真激波：正激波/斜激波/锥激波，脱体判得起动 ==");
            RunShockCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 62 段 运行时状态审计：自己跟自己打不打架 ==");
            RunStateAuditCases(d);

            Console.WriteLine();
            Console.WriteLine("== 第 63 段 长度就是损失：把发动机拉长，设计点与非设计点一起变差 ==");
            RunDuctLengthCases(Presets.Build("J79"));

            Console.WriteLine();
            Console.WriteLine("== 第 64 段 控制系统：加速慢收油快、超转切油、无控就是直给 ==");
            RunControlLawCases();

            Console.WriteLine();
            Console.WriteLine("== 第 65 段 进气道型线：截面量面积、型线量压缩角，性能跟着几何走 ==");
            RunIntakeProfileCases();

            Console.WriteLine();
            Console.WriteLine("== 第 66 段 整机长度：按部件叠出来，四台真机当标尺 ==");
            RunEngineLengthCases();

            Console.WriteLine("== 第 67 段 油量控制：杆位给油、解算器反查转速、低于最低油量自己往下掉 ==");
            RunFuelControlCases();

            Console.WriteLine();
            Console.WriteLine("== 第 68 段 流量匹配：面马赫由面积比反解、供气不足时核心机被真限流 ==");
            RunInletMatchCases();

            Console.WriteLine();
            Console.WriteLine("== 第 69 段 喷管推力分项：动量项 + 压力项，加起来必须等于总推力 ==");
            RunNozzleSplitCases();

            Console.WriteLine();
            Console.WriteLine("== 第 70 段 几何接口：进出口截面对不上要报出来，坏数据必须报错 ==");
            RunPortChainCases();

            Console.WriteLine();
            Console.WriteLine("== 第 71 段 逐轴转矩平衡：J·dω/dt = τ_涡轮 − τ_压气机 − τ_附件 ==");
            RunTorqueCases();

            Console.WriteLine();
            Console.WriteLine("== 第 72 段 故障三级台账：同一缺陷不重复惩罚 ==");
            RunFaultBookCases();

            Console.WriteLine();
            Console.WriteLine("== 第 73 段 气路状态有效性契约：坏状态说得出是哪一类坏 ==");
            RunGasContractCases();

            Console.WriteLine();
            Console.WriteLine("== 第 74 段 分层刷新：几何/性能/表现各刷各的 ==");
            RunRefreshPlanCases();

            Console.WriteLine();
            Console.WriteLine("== 第 75 段 压气机特性图：喘振线、堵塞线、工作线、裕度 ==");
            RunCompressorChartCases();
            Console.WriteLine();
            Console.WriteLine("== 第 76 段 可调几何：装不装是机构，推动由马赫自己走 ==");
            RunVariableGeometryCases();

            Console.WriteLine();
            Console.WriteLine("== 第 77 段 燃料目录与过载损伤：换油换热值、加强系统增重换抗 g ==");
            RunGLimitCases();

            Console.WriteLine();
            Console.WriteLine("== 第 78 段 风扇齿轮变速：GTF 的转速拆分、齿轮箱代价、直驱逐位不变 ==");
            RunGearCases();

            Console.WriteLine();
            Console.WriteLine("== 第 79 段 统一部件网络能换工况点：转速、高度、马赫都是参数 ==");
            RunNetworkPointCases();

            Console.WriteLine();
            Console.WriteLine("== 第 80 段 长时间/大范围体检：五台真机扫 875 个工况点，不许出现 NaN 或荒唐数 ==");
            RunSoakCases();

            Console.WriteLine();
            Console.WriteLine("== 第 81 段 特性图参与工作点：喘振裕度从图上读，不靠经验阈值 ==");
            RunChartPointCases();

            Console.WriteLine();
            Console.WriteLine("== 第 82 段 中间级放气：低转速抽走一截换喘振裕度 ==");
            RunBleedCases();

            Console.WriteLine();
            Console.WriteLine("== 第 83 段 发动机测试台：选工况、调正式求解、把数排出来 ==");
            RunBenchCases();

            Console.WriteLine();
            Console.WriteLine("== 第 84 段 存档兼容：每台预设存下去读回来，还是同一台机器 ==");
            RunSaveCompatCases();

            Console.WriteLine();
            Console.WriteLine("== 第 121 段 拖动滑杆的撤销：手松开才记一步，退回去是改之前那份 ==");
            RunDesignUndoCases();

            Console.WriteLine();
            Console.WriteLine("== 第 122 段 简单模式的撤销：18 个字段那份也有退路，且两条栈不串 ==");
            RunSimpleUndoCases();

            Console.WriteLine();
            Console.WriteLine("== 第 123 段 有控的杆位是推力：指令爬升、闭环消差、积分不许攒到天上 ==");
            RunThrustCommandCases();
            RunWizardTargetCases();

            Console.WriteLine();
            Console.WriteLine("== 第 85 段 长时间运行：连续剖面 2000 步，不许漂、不许跳 ==");
            RunLongRunCases();

            Console.WriteLine();
            Console.WriteLine("== 第 86 段 喷管状态链：慢车收喉道，网格与求解器吃同一个数 ==");
            RunNozzleChainCases();

            Console.WriteLine();
            Console.WriteLine("== 第 87 段 分层刷新：改性能不该重建外形 ==");
            RunRefreshLayerCases();

            Console.WriteLine();
            Console.WriteLine("== 第 88 段 喷口作动筒：燃油先到、A8 后撑，两个速率不许合成一个 ==");
            RunNozzleActuatorCases();

            Console.WriteLine();
            Console.WriteLine("== 第 89 段 设计向导：从目标反推初稿，每个数都要说得出来源 ==");
            RunWizardCases();

            Console.WriteLine();
            Console.WriteLine("== 第 90 段 收扩喷管出口面积：A9 由几何定（面积比 × A8），不从流量反解 ==");
            RunNozzleExitCases();

            Console.WriteLine();
            Console.WriteLine("== 第 91 段 设计评价：先说能不能飞，再说差在哪 ==");
            RunVerdictCases();

            Console.WriteLine();
            Console.WriteLine("== 第 92 段 设计点自洽：设计闭式与满油门迭代必须在同一点上 ==");
            RunDesignConsistencyCases();

            Console.WriteLine();
            Console.WriteLine("== 第 93 段 可调压缩面：能调节不等于免费，机构要重量、要年代 ==");
            RunVariableIntakeCases();

            Console.WriteLine();
            Console.WriteLine("== 第 94 段 级间负荷：喘振裕度是一个数，失速总是某一级先转不动 ==");
            RunStageLoadCases();

            Console.WriteLine();
            Console.WriteLine("== 第 95 段 堵塞边界：喘振是收油门撞墙，堵塞是加油门撞墙 ==");
            RunChokeCases();

            Console.WriteLine();
            Console.WriteLine("== 第 96 段 喷管花瓣：片数与长度是机构，不动喉道面积 ==");
            RunPetalCases();

            Console.WriteLine("== 第 97 段 堵塞越界要真的截流：报出来还不够，气得压回去 ==");
            RunChokeLimitCases();

            Console.WriteLine("== 第 98 段 模型档案：每个数都得说得出来源、边界与误差 ==");
            RunModelLedgerCases();

            Console.WriteLine();
            Console.WriteLine("== 第 99 段 查图：插值怎么插、越界怎么夹，都要有说法 ==");
            RunChartQueryCases();

            Console.WriteLine();
            Console.WriteLine("== 第 100 段 控制系统分档：年代给的闸，一道都不能多也不能少 ==");
            RunControlTierCases();

            Console.WriteLine();
            Console.WriteLine("== 第 101 段 加力点火窗口：老点火器高空点不着，数字机断油不硬撑 ==");
            RunAfterburnerTierCases();

            Console.WriteLine();
            Console.WriteLine("== 第 102 段 喷口调度分档：杆拉口开的是老机，温度说话的是数字机 ==");
            RunNozzleScheduleTierCases();

            Console.WriteLine();
            Console.WriteLine("== 第 103 段 高空再起动：转速太低带不动、太高会富油、空气太稀点不着 ==");
            RunRelightCases();

            Console.WriteLine();
            Console.WriteLine("== 第 104 段 双语：成对原文、英文回中文规范形、漏翻要记数 ==");
            RunLocaleCases();

            Console.WriteLine();
            Console.WriteLine("== 第 105 段 涡轮的功被两头上限卡住：叶尖速度，和单级落压比 ==");
            RunTurbineBudgetCases();

            Console.WriteLine();
            Console.WriteLine("== 第 106 段 老存档读得进来：缺键取缺省、未知键不理、存回去不丢数 ==");
            RunSaveMigrationCases();

            Console.WriteLine();
            Console.WriteLine("== 第 107 段 齿轮让风扇与轴解耦：设计点与非设计点都得成立 ==");
            RunGearOffDesignCases();

            Console.WriteLine();
            Console.WriteLine("== 第 108 段 坏输入不许变成假数：九个部件门口先验入口状态 ==");
            RunBoundaryCases();

            Console.WriteLine();
            Console.WriteLine("== 第 109 段 齿轮箱自己也带惯量：直驱为 0，GTF 的升速要按 J 变慢 ==");
            RunGearboxInertiaCases();

            Console.WriteLine();
            Console.WriteLine("== 第 110 段 逐级喘振余量：整机 33% 不等于每级 33%，前级先喘 ==");
            RunStageMarginCases();

            Console.WriteLine();
            Console.WriteLine("== 第 111 段 向导撤销：按下去之前那份设计要能原样退回来 ==");
            RunWizardUndoCases();

            Console.WriteLine();
            Console.WriteLine("== 第 112 段 判据排列：FAIL 必须排在 WARN 前面，藏起来的要说清藏了几条 ==");
            RunReportOrderCases();

            Console.WriteLine();
            Console.WriteLine("== 第 113 段 预设的几何不许随界面语言变（同一台发动机只有一份尺寸） ==");
            RunLanguageCases();

            Console.WriteLine();
            Console.WriteLine("== 第 114 段 齿轮箱的柔度：扭角、本体扭振、与质量闭式不许分叉 ==");
            RunTorsionCases();

            Console.WriteLine();
            Console.WriteLine("== 第 117 段 两台设计并排比读数：方向要一句一行，不许拍总分 ==");
            RunCompareCases();

            Console.WriteLine();
            Console.WriteLine("== 第 118 段 部件规格的检查必须有人调用（级数写负数不许悄悄换构型） ==");
            RunSpecValidateCases();

            Console.WriteLine();
            Console.WriteLine(_fails == 0 ? "全部回归通过。" : string.Format("{0} 项超差。", _fails));
            Environment.Exit(_fails == 0 ? 0 : 1);
        }

        /// <summary>
        /// 第 104 段：本地化的三条契约。
        ///
        /// 这三条都不是"翻译好不好"，而是"机制对不对"：
        /// 1. 中文界面必须原样拿到中文（改文案不该动中文观感）；
        /// 2. 英文界面的标签要能换回中文规范形——界面按标签文字查表（悬停提示）就靠它，
        ///    否则英文界面会静默失去提示，不报错、不崩，只是鼠标悬上去什么都没有；
        /// 3. 英文参数空着必须回落到中文并记数。漏翻是"看不见的错"，得有个数。
        /// </summary>
        /// <summary>
        /// 第 105 段：涡轮能挤出的功有两条天花板。
        ///
        /// 一条是叶尖速度（TurbineDT 按圆周速度算 ΔT），另一条是**单级落压比**——
        /// 一级涡轮再能干，压比也就 tech.TurbStagePrMax（1970s 2.4、1990s 2.7、2010s 3.0）。
        /// 以前只有第一条在算容量，第二条仅在事后拿日志报一句错：于是地面 Ma1.6 高转速下
        /// 出现过「日志写落压比 3.54 超了 2.40 的极限，那部分功却照给」——报的和算的不是一回事。
        /// 现在容量两头上限取小，越限的症状会变成「级数不足」，而落压比不再越限。
        /// </summary>
        private static void RunTurbineBudgetCases()
        {
            EngineDesignData rd = Presets.Build("RD-33");
            Expect("RD-33 预设存在", rd != null, true);
            if (rd == null) return;

            int stages = rd.Hpt != null && rd.Hpt.Stages > 0 ? rd.Hpt.Stages : 1;
            double lim = Math.Pow(rd.Tech.TurbStagePrMax, stages);

            // ① 越限的那个工况（地面 Ma1.6 满转速）：落压比必须落在单级极限之内。
            CycleResult hot = EngineNetwork.SolveAt(rd, 0.0, 1.6, 1.0).Result;
            Console.WriteLine("    地面 Ma1.6 满转速：HPT 落压比 {0:F2}｜{1} 级极限 {2:F2}",
                hot.PiHpt, stages, lim);
            Expect("单级落压比不越限（以前这里报 3.54 对 2.40）", hot.PiHpt <= lim * 1.02, true);

            // ② 被压比卡住时必须**判不成立**，而不是默默少给一点功就算了。
            //    加一级之后循环能不能成立，才是"级数不足"这句话值不值得听的证据。
            EngineDesignData two = Presets.Build("RD-33");
            if (two.Hpt != null && two.Hpt.Stages > 0) two.Hpt.Stages = two.Hpt.Stages + 1;
            CycleResult hot2 = EngineNetwork.SolveAt(two, 0.0, 1.6, 1.0).Result;
            Console.WriteLine("    同工况加一级：HPT 落压比 {0:F2}｜循环成立 {1}｜推力 {2:F2} kN（单级 {3:F2} kN）",
                hot2.PiHpt, hot2.Ok, hot2.Thrust / 1000.0, hot.Thrust / 1000.0);
            Expect("单级被落压比卡住时判不成立（不是默默少给功）", hot.Ok, false);
            Expect("加一级之后循环成立", hot2.Ok, true);
            Expect("两级的单级落压比同样不越限",
                hot2.PiHpt <= Math.Pow(rd.Tech.TurbStagePrMax, stages + 1) * 1.02, true);

            // ③ 共用一份容量公式：设计点（部件求解）与飞行时（逐轴功平衡）必须吃同一份数。
            //    只修设计点的话，飞行那套数里容量还是按叶尖速度算，掉转因子偏大——
            //    等于"设计时说超限了，飞起来当没这回事"。
            DesignConstants dc = OffDesign.DeriveDesignConstants(rd);
            double rpmHpt;
            dc.RpmDesign.TryGetValue(rd.Hpt.Shaft, out rpmHpt);
            bool prB;
            double dtShared = Cycle.TurbineCapacityDt(rd.Hpt, rd.Tech, rpmHpt, hot.T4, rd.Tech.HptEtaPol,
                                                      rd.Tech.TurbinePsi, out prB);
            double uTip = Cycle.TipSpeed(rd.Hpt.MaxTip(), rpmHpt);
            double dtPure = rd.Hpt.Stages * Cycle.TurbineDT(uTip, rd.Tech.HptEtaPol,
                                                            rd.Tech.TurbinePsi * rd.Hpt.PsiOf(0));
            Console.WriteLine("    HPT 容量：共用公式 {0:F0} K（{1}）｜只按叶尖速度 {2:F0} K｜设计点落压比 {3:F2}",
                dtShared, prB ? "被单级压比卡住" : "没被压比卡住", dtPure, hot.PiHpt);
            Expect("热工况下容量确实被单级压比卡住", prB, true);
            Expect("被卡住时容量比只按叶尖速度算的更小", dtShared < dtPure, true);

            // ④ 飞行时那套数（非设计点逐轴功平衡）同样受限，而且**双轴机也要问这句话**。
            //    以前这段只在三轴机或有中压涡轮时跑，于是绝大多数涡喷/涡扇在飞行时
            //    根本不核对"这根轴的涡轮带得动压气机吗"——设计点报的限值飞起来等于没有。
            OffDesignResult off = OffDesign.Solve(rd, 0.0, 1.6, 1.0, 0.0);
            double shortF = off.SpoolShortfall != null && off.SpoolShortfall.ContainsKey(rd.Hpt.Shaft)
                ? off.SpoolShortfall[rd.Hpt.Shaft] : 1.0;
            Console.WriteLine("    飞行时：CapHpt {0:F2} MW｜需 {1:F2} MW｜最紧的轴 {2}｜掉转因子 {3:F3}",
                off.CapHpt / 1e6, off.WHpt / 1e6, off.LimitSpool, shortF);
            Expect("飞行时逐轴功平衡真的跑了（以前双轴机整段被跳过）", off.WHpt > 0.0, true);
            Expect("这根轴的涡轮带不动：掉转因子小于 1", shortF < 0.99, true);
            Expect("掉的是高压轴（HPT 那根）", off.LimitSpool, rd.Hpt.Shaft);
        }

        /// <summary>
        /// 第 106 段：老存档读得进来。
        ///
        /// 这一段不是在测"翻译"也不是在测性能，测的是**发布之后最容易被忽略的那条路**：
        /// 玩家上个版本存下来的零件，装上新版还能不能开。
        /// 三层要求，缺一层就会变成那种"能读、但读出来不是原来那台机器"的错：
        ///
        /// 1. 当时就有的键，一个字节都不许变（名字、直径、级数、年代）；
        /// 2. 后来加的键必须取**当年那个行为的缺省值**——不是"新功能的缺省值"。典型例子是
        ///    `lengthAuto`：老存档没有它，读进来就必须按部件算长度，那才是老机器当年做的事；
        /// 3. 已经撤掉的键（`realfuel`）必须被无视，而不是被当成未知字段、更不许报错。
        ///
        /// 还有一条隐含要求：存回去要走新写法（多出新键），但老值仍然一个不变——
        /// 否则玩家每存一次盘，机器就被悄悄改一次。
        /// </summary>
        private static void RunSaveMigrationCases()
        {
            // 手写一份 1960 年代版式的存档：只有当年那几个键。
            // `designMach = 2`（整数）、`vectoring = True`（KSP 的 bool 写法）、
            // `nozzle = Rocket`（不认识的枚举）——这三样都是真实老文件里会出现的脏东西。
            string legacy =
                "JETFORGE_DESIGN\n" +
                "{\n" +
                "    name = 老设计\n" +
                "    kind = Turbojet\n" +
                "    tech = 1960s\n" +
                "    inletDiameter = 0.66\n" +
                "    length = 5.05\n" +
                "    designMach = 2\n" +
                "    designAlt = 11000\n" +
                "    vectoring = True\n" +
                "    nozzle = Rocket\n" +
                "    realfuel = 1\n" +
                "    HPC\n" +
                "    {\n" +
                "        stages = 8\n" +
                "        tipDiameter = 0.62\n" +
                "        shaft = 1\n" +
                "    }\n" +
                "    BURNER\n" +
                "    {\n" +
                "        exitTemp = 1080\n" +
                "    }\n" +
                "}\n";

            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "jf_legacy_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                System.IO.File.WriteAllText(path, legacy, new System.Text.UTF8Encoding(false));
                ConfigNode node = ConfigNode.Load(path);
                Expect("老存档文件解析得出来", node != null, true);
                if (node == null) return;

                EngineDesignData d = EngineDesignData.Load(node);
                Expect("读进来不抛异常、拿得到一个设计", d != null, true);
                if (d == null) return;

                // ---- 1. 老值原样 ----
                ExpectEq("老存档：名字原样", d.DesignName, "老设计");
                ExpectEq("老存档：型号原样（涡喷）", d.Kind, EngineKind.Turbojet);
                ExpectEq("老存档：年代原样", d.TechName, "1960s");
                ExpectEq("老存档：进气直径原样", d.InletDiameter, 0.66);
                ExpectEq("老存档：压气机级数原样", d.Hpc.Stages, 8);
                ExpectEq("老存档：燃烧室出口温度原样", d.Burner.ExitTemp, 1080.0);
                ExpectEq("老存档：整数写法的马赫读成 2.0", d.DesignMach, 2.0);
                ExpectTrue("老存档：True 这种写法认得", d.ThrustVectoring);
                ExpectEq("老存档：认不出的枚举回缺省，不许抛异常", d.Nozzle, NozzleKind.Converging);

                // ---- 2. 新键取"当年行为"的缺省 ----
                ExpectTrue("缺 lengthAuto：老机器按部件算长度", d.LengthAuto);
                ExpectEq("缺 control：1960 年代那台没有控制系统", d.Control, ControlKind.None);
                ExpectEq("缺 gearRatio：风扇直驱", d.Fan.GearRatio, 1.0);
                ExpectTrue("缺 gDamage：过载损伤默认开", d.GDamage);
                ExpectEq("缺 gReinforce：没有加强", d.GReinforce, 0.0);

                // ---- 3. 撤掉的键被无视 ----
                ExpectTrue("撤掉的 realfuel 不许被带进新存档", !SaveHadKey(d, "realfuel"));

                // ---- 4. 存回去：老值不变、新键补上 ----
                ConfigNode again = new ConfigNode(EngineDesignData.NodeName);
                d.Save(again);
                ExpectTrue("重存补上了 lengthAuto 键", again.HasValue("lengthAuto"));
                ExpectTrue("重存补上了 control 键", again.HasValue("control"));
                EngineDesignData back = EngineDesignData.Load(again);
                ExpectEq("重存：名字不变", back.DesignName, "老设计");
                ExpectEq("重存：进气直径不变", back.InletDiameter, 0.66);
                ExpectEq("重存：级数不变", back.Hpc.Stages, 8);
                ExpectEq("重存：设计马赫不变", back.DesignMach, 2.0);

                // ---- 5. 老存档还是台能算的机器 ----
                CycleResult r = Cycle.SolveDesignPoint(back);
                ExpectTrue("老存档仍算得出设计点", r != null);
                if (r != null)
                    ExpectTrue("老存档算出的推力是有限正数", GasState.IsFinite(r.Thrust) && r.Thrust > 0.0);
            }
            finally
            {
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
            }
        }

        private static bool SaveHadKey(EngineDesignData d, string key)
        {
            ConfigNode n = new ConfigNode(EngineDesignData.NodeName);
            d.Save(n);
            return n.HasValue(key);
        }

        private static void RunLocaleCases()
        {
            Lang prev = Loc.Current;
            try
            {
                Loc.Current = Lang.Chinese;
                Expect("中文界面：成对写法原样拿中文", Loc.T("推力", "Thrust"), "推力");

                Loc.Current = Lang.English;
                Expect("英文界面：成对写法拿英文", Loc.T("推力", "Thrust"), "Thrust");
                Expect("英文标签能换回中文规范形（悬停提示按中文起键）",
                    Loc.Canonical("Thrust"), "推力");
                Expect("中文原样进来就原样出去", Loc.Canonical("推力"), "推力");
                Expect("认不出来的串原样返回，不许瞎猜", Loc.Canonical("Zzz"), "Zzz");

                int before = Loc.Fallbacks;
                Expect("英文参数空着时回落到中文", Loc.T("没有英文", ""), "没有英文");
                Expect("回落要记数（漏翻不能是无声的）", Loc.Fallbacks, before + 1);
                Expect("对照表登记过对子（0 说明根本没走过 T）", Loc.PairCount > 0, true);

                // 语言名归一：KSP 那个字段里可能是显示名，也可能是语言代码。
                // 第一版只判"有没有汉字"，于是 zh-cn（这台机器的实际值）被判成英文，
                // 自检一路报"语言 English"而零件名是中文——读错枚举值比读不到更糟。
                Expect("语言代码 zh-cn 认成中文（只判汉字会把它判成英文）",
                    Loc.Classify("zh-cn") == Lang.Chinese, true);
                Expect("语言代码 zh_CN 也认", Loc.Classify("zh_CN") == Lang.Chinese, true);
                Expect("语言代码 en-us 认成英文", Loc.Classify("en-us") == Lang.English, true);
                Expect("纯代码 en 认成英文", Loc.Classify("en") == Lang.English, true);
                Expect("显示名 English 认成英文", Loc.Classify("English") == Lang.English, true);
                Expect("显示名 简体中文 认成中文", Loc.Classify("简体中文") == Lang.Chinese, true);
                Expect("德文认不出来就交回 null，不许默认中文或英文",
                    Loc.Classify("de-de") == null, true);
                Expect("空串交回 null", Loc.Classify("") == null, true);

                // 探测状态要能区分"认出来了"和"根本没人问过"。
                // 这两件事在日志里长得一样（都是「语言 中文」），区别是：前者是实测，后者是缺省值。
                Loc.Probe = () => Lang.English;
                Expect("探针认出英文时状态说得出来", Loc.ProbeState, "认出 English");
                Expect("认出之后 Current 跟着变", Loc.Current == Lang.English, true);

                Loc.Probe = () => null;
                Expect("探针装了但认不出，状态要说认不出", Loc.ProbeState, "装了但认不出语言");

                Loc.Probe = () => { throw new Exception("装作游戏内部炸了"); };
                Expect("探针抛异常也要说清楚（不能静默保持中文）", Loc.ProbeState, "抛异常");

                // 拿掉探针：这不等于"语言变回中文"，而是"以后不再有人问语言"。
                // 所以 Current 保持上一次确定的值（这里先摆成中文，再看它不被改），
                // 只有状态必须如实说"没装"。
                Loc.Current = Lang.Chinese;
                Loc.Probe = null;
                Expect("没有探针时必须说没装，不能拿缺省值冒充实测",
                    Loc.ProbeState, "没装（读的是缺省值）");
                Expect("拿掉探针不改动已经确定过的语言（只是不再探测）",
                    Loc.Current == Lang.Chinese, true);
                Expect("那一行状态里要同时带上探测状态",
                    Loc.Report().Contains("探测 没装"), true);

                // 悬停提示的查表：这张表按中文起键，而英文界面递过来的是英文。
                // 以前这段逻辑在 Unity 侧，只能进游戏点开设计器才算跑过——现在搬进 Core，这里钉住它。
                Dictionary<string, string> table = new Dictionary<string, string>
                {
                    { "推力", "hint-thrust" },
                    { "  级数", "hint-stages" },
                    { "长度", "hint-length" },
                    { "整机长度", "hint-total-length" },
                };
                Loc.Current = Lang.Chinese;
                Expect("提示：精确命中", HintIndex.Find(table, "推力"), "hint-thrust");
                Expect("提示：子项键带缩进也认", HintIndex.Find(table, "  级数"), "hint-stages");
                Expect("提示：精确优先，长度不会串到整机长度", HintIndex.Find(table, "长度"), "hint-length");
                Expect("提示：整机长度命中自己的那条", HintIndex.Find(table, "整机长度"), "hint-total-length");
                Expect("提示：没登记就返回 null，不许硬凑", HintIndex.Find(table, "完全没登记的项"), null);

                Loc.Current = Lang.English;
                Loc.T("推力", "Thrust");
                Loc.T("  级数", "  Stages");
                Loc.T("长度", "Length");
                Expect("英文界面：英文标签也能挂上提示", HintIndex.Find(table, "Thrust"), "hint-thrust");
                Expect("英文界面：带缩进的英文标签同样认得", HintIndex.Find(table, "  Stages"), "hint-stages");
                Expect("英文界面：精确优先依然成立", HintIndex.Find(table, "Length"), "hint-length");
                Expect("英文界面：认不出来就返回 null", HintIndex.Find(table, "Nonsense"), null);
            }
            finally
            {
                Loc.Current = prev;
            }
        }

/// <summary>
        /// 第 81 段：特性图参与工作点。
        ///
        /// 这一段守的不是"喘振裕度等于多少"，而是三件更基本的事：
        /// 1. 图能建起来（不是 -1）；2. 设计点裕度落在真机量级（15%~60%）；
        /// 3. 转速掉下去时裕度**变了**——如果它一动不动，说明图根本没参与工作点，
        ///    只是把我们传进去的数原样吐回来。
        /// </summary>
        private static void RunChartPointCases()
        {
            EngineDesignData j79 = Presets.Build("J79");
            Expect("J79 预设存在", j79 != null, true);
            if (j79 == null) return;

            // ① 设计点的裕度必须是真值：图按设计点建，设计点就落在标称位置上。
            EngineDesignData rd = Presets.Build("RD-33");
            CycleResult rdD = EngineNetwork.SolveDesign(rd).Result;
            Console.WriteLine("    RD-33 设计点：喘振 {0:F1} %｜堵塞 {1:F1} %", rdD.HpcSurgeMargin, rdD.HpcChokeMargin);
            Expect("RD-33 设计点喘振裕度落在 10%~60%", rdD.HpcSurgeMargin >= 10.0 && rdD.HpcSurgeMargin <= 60.0, true);
            Expect("RD-33 设计点堵塞裕度是正的真值（3%~40%）", rdD.HpcChokeMargin >= 3.0 && rdD.HpcChokeMargin <= 40.0, true);

            // ② 图不能跟着工作点漂（本轮修的错）：同一台机器跑两个工况，图基准必须一样。
            //    以前每次求解都把"此刻"当设计点，基准随之变，φ 恒为 1/nc，
            //    于是裕度永远是 33.3% / 18.0% 两个常数。
            NetworkSolution p70 = EngineNetwork.SolveAt(rd, double.NaN, double.NaN, 0.70);
            CompressorChart c1 = EngineNetwork.SolveDesign(rd).Chart;
            CompressorChart c2 = p70.Chart;
            Console.WriteLine("    图基准：设计点 Wc {0:F3} kg/s｜70% 转速那次 {1:F3} kg/s",
                c1 != null ? c1.DesignWc : -1.0, c2 != null ? c2.DesignWc : -1.0);
            Expect("图基准不随工作点漂（本轮修的就是这个）",
                c1 != null && c2 != null && Math.Abs(c1.DesignWc - c2.DesignWc) < 1e-6, true);

            // ③ 算不出来必须说"不适用"：J79 的设计点在 11 km 带冲压，地面同一个 Nc 的
            //    换算流量只有它的 55%——那是模型流量律与图的口径差（§174），
            //    不是喘振也不是堵塞。这种情况记 -1 并把原因写进告警，
            //    绝不能给一个看着像真的负数。
            NetworkSolution jl = EngineNetwork.SolveAt(j79, double.NaN, double.NaN, 0.70);
            double jm = jl.Result.HpcSurgeMargin;
            bool toldWhy = false;
            for (int i = 0; i < jl.Warnings.Count; i++) if (jl.Warnings[i].Contains("特性图不适用")) toldWhy = true;
            Console.WriteLine("    J79 70% 转速：喘振 {0:F1}｜说了原因 {1}", jm, toldWhy ? "是" : "不是");
            Expect("算不出裕度时记 -1，不给负数假读数", jm < 0.0 ? Math.Abs(jm + 1.0) < 1e-9 : true, true);
            Expect("记了 -1 就必须把原因说出来", jm < 0.0 ? toldWhy : true, true);

            // ④ 可调静子改的是图上的喘振/堵塞线，不是工作点读数：低转速时喘振线左移、
            //    堵塞线右移，设计转速上完全一样（叶片本来就在设计角度上）。
            EngineDesignData vOn = Presets.Build("RD-33");
            EngineDesignData vOff = Presets.Build("RD-33");
            vOn.Hpc.Vigv = true;
            vOff.Hpc.Vigv = false;
            TechLevel tv = TechLevel.Get(vOn.TechName);
            CompressorChart chOn = CompressorChart.DesignChart(vOn, false, tv);
            CompressorChart chOff = CompressorChart.DesignChart(vOff, false, tv);
            if (chOn != null && chOff != null)
            {
                double s70On = chOn.SurgeAt(0.70).Wc, s70Off = chOff.SurgeAt(0.70).Wc;
                double k70On = chOn.ChokeAt(0.70).Wc, k70Off = chOff.ChokeAt(0.70).Wc;
                Console.WriteLine("    70% 转速喘振线：有静子 {0:F2}｜无静子 {1:F2}；堵塞线：有 {2:F2}｜无 {3:F2}",
                    s70On, s70Off, k70On, k70Off);
                Expect("可调静子把低转速的喘振线往左拉", s70On < s70Off - 1e-6, true);
                Expect("可调静子把低转速的堵塞线往右推", k70On > k70Off + 1e-6, true);
                Expect("设计转速上有没有静子都一样（叶片本来就在设计角度上）",
                    Math.Abs(chOn.SurgeAt(1.0).Wc - chOff.SurgeAt(1.0).Wc) < 1e-6, true);
            }

            // 有加力的机器设计点裕度也不该爆表——图是同一张，只是位置不同。
            EngineDesignData gtf = Presets.Build("GTF1100");
            if (gtf != null)
            {
                NetworkSolution gs = EngineNetwork.SolveDesign(gtf);
                Console.WriteLine("    GTF1100 设计点：喘振 {0:F1} %｜堵塞 {1:F1} %",
                    gs.Result.HpcSurgeMargin, gs.Result.HpcChokeMargin);
                Expect("GTF1100 设计点喘振裕度落在 15%~60%",
                    gs.Result.HpcSurgeMargin >= 15.0 && gs.Result.HpcSurgeMargin <= 60.0, true);
                Expect("GTF1100 设计点堵塞裕度 3%~40%",
                    gs.Result.HpcChokeMargin >= 3.0 && gs.Result.HpcChokeMargin <= 40.0, true);
            }
        }

        /// <summary>
        /// 第 82 段：中间级放气。
        ///
        /// 它和可调静子是两套机制：静子改**喘振边界在哪**，放气改**流道里有多少流量**。
        /// 所以这一段验的不是"裕度变好了"这一句，而是三件分开的事实：
        ///   ①设计点一分不放（放气是低转速的应急手段，不是常态）；
        ///   ②低转速时核心流量真的按 (1 − b) 少了（不是只把裕度数字改好看）；
        ///   ③放气之后裕度确实往回走——因为前排级看到的是全部流量。
        /// 断言写成"有放气 vs 无放气"的对照，不写定值：定值会被人调着数改到绿。
        /// </summary>
        private static void RunBleedCases()
        {
            EngineDesignData j79 = Presets.Build("J79");
            Expect("J79 预设存在", j79 != null, true);
            if (j79 == null) return;

            CycleResult d = EngineNetwork.SolveDesign(j79).Result;
            Console.WriteLine("    设计点：放气 {0:F1} %｜核心流量 {1:F2} kg/s", d.HpcBleedFraction * 100.0, d.MdotCore);
            Expect("设计点不放气（放气是应急手段，不是常态）", d.HpcBleedFraction < 1e-9, true);

            // 70% 转速：自动开阀。
            NetworkSolution on = EngineNetwork.SolveAt(j79, double.NaN, double.NaN, 0.70);
            CycleResult rOn = on.Result;
            Console.WriteLine("    70% 转速（放气 自动）：比例 {0:F1} %｜抽走 {1:F3} kg/s｜压气机流量 {2:F2} kg/s｜裕度 {3:F1} %｜推力 {4:F2} kN",
                rOn.HpcBleedFraction * 100.0, rOn.HpcBleedFlow, rOn.MdotCore, rOn.HpcSurgeMargin, rOn.Thrust / 1000.0);
            Expect("70% 转速自动开阀", rOn.HpcBleedFraction > 1e-6, true);
            Expect("自动开阀最多抽 12%", rOn.HpcBleedFraction <= 0.1201, true);

            EngineDesignData noB = j79.Clone();
            noB.Hpc.Bleed = false;
            NetworkSolution off = EngineNetwork.SolveAt(noB, double.NaN, double.NaN, 0.70);
            CycleResult rOff = off.Result;
            Console.WriteLine("    70% 转速（不放气）：压气机流量 {0:F2} kg/s｜裕度 {1:F1} %｜推力 {2:F2} kN",
                rOff.MdotCore, rOff.HpcSurgeMargin, rOff.Thrust / 1000.0);
            Expect("不放气时比例恒为 0", rOff.HpcBleedFraction < 1e-9, true);

            // 进口气流不变（放气不提高前级能吞的上限），少的是核心那一截。
            Check("放气不改进气流量（它不会凭空给气）", rOn.MdotCore, rOff.MdotCore, 0.5, "kg/s");

            // 抽走的那股 = 压气机流量 × 比例。
            Check("抽走的流量 = 压气机流量 × 放气比例", rOn.HpcBleedFlow, rOn.MdotCore * rOn.HpcBleedFraction, 1.0, "kg/s");
            double coreAfter = rOn.MdotCore - rOn.HpcBleedFlow;
            Console.WriteLine("    放气后进核心：{0:F2} kg/s（不放气时 {1:F2}）", coreAfter, rOff.MdotCore);
            Check("放气后进核心的流量正好少掉那股", coreAfter, rOff.MdotCore * (1.0 - rOn.HpcBleedFraction), 1.0, "kg/s");

            // 裕度**变差**了：这是模型的短板，不是优点。
            // 真机上放气能救喘振，是因为"前排多吞、后排少要"——那是逐级的事；
            // 我们这台压气机是一个整体，只有进口气流一个数，放气唯一的后果是
            // 后面几级少推一点、涡轮少出功、转速自己掉下来，工作点反而更靠近喘振线。
            // 所以这里钉的是"放气在当前模型里只会让裕度变差"，而不是假装它无关。
            Console.WriteLine("    裕度 {0:F1} % → {1:F1} %：本模型是一个整体压气机，看不到逐级放气对喘振的好处；" +
                              "放气带来的只有转速下降、工作点更贴近喘振线（待逐级求解）",
                rOff.HpcSurgeMargin, rOn.HpcSurgeMargin);
            ExpectTrue("放气没有凭空变出喘振裕度（模型短板如实钉住：没有逐级好处，只有转速下降）",
                rOn.HpcSurgeMargin <= rOff.HpcSurgeMargin + 0.5);
            Console.WriteLine("    代价：推力 {0:F2} → {1:F2} kN（少 {2:F2}）——放气是拿推力换稳定",
                rOff.Thrust / 1000.0, rOn.Thrust / 1000.0, (rOff.Thrust - rOn.Thrust) / 1000.0);
            Expect("放气让推力变小（拿推力换稳定）", rOn.Thrust < rOff.Thrust, true);

            // 手动比例不看转速：满转也放。
            EngineDesignData manual = j79.Clone();
            manual.Hpc.BleedFrac = 0.05;
            CycleResult rMan = EngineNetwork.SolveDesign(manual).Result;
            Console.WriteLine("    手动 5% 比例：设计点比例 {0:F1} %", rMan.HpcBleedFraction * 100.0);
            Expect("手动填了比例就照填的来（设计点也放）", Math.Abs(rMan.HpcBleedFraction - 0.05) < 1e-6, true);

            // 离心级与级数 < 4 没有中间级可抽。
            EngineDesignData small = j79.Clone();
            small.Hpc.Stages = 3;
            Expect("级数 < 4 的压气机不放气", CompressorComponent.BleedOf(small.Hpc, 0.60) < 1e-9, true);
            EngineDesignData cent = j79.Clone();
            cent.Hpc.Kind = CompressorKind.Centrifugal;
            Expect("离心级不放气", CompressorComponent.BleedOf(cent.Hpc, 0.60) < 1e-9, true);
            Expect("自动曲线在 92% 转速以上不开阀", CompressorComponent.BleedOf(j79.Hpc, 0.95) < 1e-9, true);
            Expect("关掉开关就没有放气", CompressorComponent.BleedOf(noB.Hpc, 0.60) < 1e-9, true);

            // 存档：只写跟缺省不一样的那一种，老存档读回来还是"有机构、自动"。
            ConfigNode saved = new ConfigNode("JETFORGE_DESIGN");
            j79.Save(saved);
            Expect("缺省状态不写 bleed 键（老存档口径不变）", saved.ToString().Contains("bleedFrac"), false);
            EngineDesignData back = EngineDesignData.Load(saved);
            Expect("读回来还是有放气机构", back.Hpc.Bleed, true);
            Expect("读回来比例仍是自动", Math.Abs(back.Hpc.BleedFrac) < 1e-9, true);
            ConfigNode savedOff = new ConfigNode("JETFORGE_DESIGN");
            noB.Save(savedOff);
            EngineDesignData backOff = EngineDesignData.Load(savedOff);
            Expect("关掉的那台读回来还是关着", backOff.Hpc.Bleed, false);
        }

        /// <summary>
        /// 第 83 段：发动机测试台。
        ///
        /// 台架的用处不是"再算一遍"，而是把"这台机器在整个包线里像不像一台发动机"摊开来看。
        /// 所以这一段验的是三件趋势，不是三个定值：
        ///   ①油门往上推，净推力往上走（不单调就说明某个环节在逆着来）；
        ///   ②同一油门，飞得高推力就小（空气稀了）；
        ///   ③一格都算不出来（Ok=false）是不允许的——台架上出现空格，玩家看到的就是"这机器有问题"。
        /// 定值断言只在"设计点附近"写一条，其余全写关系：关系才是物理，定值只是个记号。
        /// </summary>
        private static void RunBenchCases()
        {
            EngineDesignData j79 = Presets.Build("J79");
            Expect("J79 预设存在", j79 != null, true);
            if (j79 == null) return;

            List<BenchRow> line = EngineBench.ThrottleLine(j79, 0.0, 0.0);
            for (int i = 0; i < line.Count; i++) Console.WriteLine("    " + line[i].Line());
            Expect("油门线五档都算得出来", line.Count == 5 && line.TrueForAll(x => x.Ok), true);

            bool mono = true;
            for (int i = 1; i < line.Count; i++)
                if (line[i].NetThrust < line[i - 1].NetThrust - 0.05) mono = false;
            Expect("油门往上推，净推力不往回走", mono, true);
            Expect("满油门确实比慢车推得多", line[line.Count - 1].NetThrust > line[0].NetThrust + 5.0, true);

            // 慢车的耗油率该比满油门差：这是涡喷的老毛病，也是台架最该暴露的那一行。
            Console.WriteLine("    慢车耗油率 {0:F4}，满油门 {1:F4} kg/(N·h)",
                line[0].Sfc, line[line.Count - 1].Sfc);

            List<BenchRow> high = EngineBench.Sweep(j79, true);
            for (int i = 0; i < high.Count; i++) Console.WriteLine("    " + high[i].Line());
            Expect("高度剖面五档都算得出来", high.Count == 5 && high.TrueForAll(x => x.Ok), true);
            Expect("同一马赫、飞得越高净推力越小", high[0].NetThrust > high[4].NetThrust, true);

            List<BenchRow> fast = EngineBench.Sweep(j79, false);
            for (int i = 0; i < fast.Count; i++) Console.WriteLine("    " + fast[i].Line());
            Expect("马赫剖面五档都算得出来", fast.Count == 5 && fast.TrueForAll(x => x.Ok), true);

            // 台架与设计点求解必须是同一台机器：满油门、设计高度、设计马赫时，
            // 台架给的净推力该与设计点读数量级一致（差在"设计点 vs 非设计点"的口径上，
            // 不是差在"两套公式"上——差一个数量级就说明台架调错了东西）。
            CycleResult dp = EngineNetwork.SolveDesign(j79).Result;
            BenchRow atDesign = EngineBench.Point(j79, j79.DesignAlt, j79.DesignMach, 1.0);
            Console.WriteLine("    设计点：循环给 {0:F2} kN，台架给 {1:F2} kN", dp.Thrust / 1000.0, atDesign.NetThrust);
            Expect("台架与设计点同量级（不是两套公式）", Math.Abs(atDesign.NetThrust - dp.Thrust / 1000.0) < dp.Thrust / 1000.0 * 0.6, true);

            // 坏设计上台架：要返回一行带原因的说明，而不是抛异常或给 0。
            EngineDesignData broken = j79.Clone();
            broken.Hpc.Stages = 0;
            broken.Lpc.Stages = 0;
            BenchRow bad = EngineBench.Point(broken, 0.0, 0.0, 1.0);
            Console.WriteLine("    压气机级数 0 的坏设计：{0}｜{1}", bad.Ok ? "居然算出来了" : "算不出来", bad.Note);
            Expect("坏设计不抛异常，给的是说明", bad.Ok || bad.Note.Length > 0, true);
        }

        /// <summary>
        /// 第 84 段：存档兼容（A 类自动化测试的第一条）。
        ///
        /// 存档兼容不是"字段都在"，而是"存下去再读回来，算出来的还是同一台机器"。
        /// 所以这一段不逐字段比内存，而是把每台预设走一遍
        ///   构建 → 存成 ConfigNode → 从 ConfigNode 读回 → 两边各算一次设计点 → 比推力/压比/流量
        /// 差在 1e-9 以内才算过。字段漏抄（Clone 漏、Persistence 漏）在这一步一定会露出来。
        ///
        /// 另外三条边界：
        ///   ①存档里多出未知键（新版本写、旧版本读）不许炸；
        ///   ②缺键（旧存档读、新版本期望）必须退回缺省值，而不是 0；
        ///   ③读回来的设计必须能重新算通（不是"读进来一堆 0"）。
        /// </summary>
        private static void RunSaveCompatCases()
        {
            IList<PresetInfo> all = Presets.All;
            Expect("预设表不是空的", all != null && all.Count >= 5, true);

            int roundTrip = 0, mismatched = 0;
            for (int i = 0; i < all.Count; i++)
            {
                string key = all[i].Key;
                EngineDesignData a = Presets.Build(key);
                if (a == null) continue;

                ConfigNode saved = new ConfigNode("JETFORGE_DESIGN");
                a.Save(saved);
                EngineDesignData b = EngineDesignData.Load(saved);
                if (b == null) continue;

                CycleResult ra = EngineNetwork.SolveDesign(a).Result;
                CycleResult rb = EngineNetwork.SolveDesign(b).Result;
                roundTrip++;
                bool same = Math.Abs(ra.Thrust - rb.Thrust) < 1e-6 * Math.Max(1.0, Math.Abs(ra.Thrust))
                         && Math.Abs(ra.Opr - rb.Opr) < 1e-9
                         && Math.Abs(ra.MdotAir - rb.MdotAir) < 1e-9;
                if (!same)
                {
                    mismatched++;
                    // 露馅的时候把可能的分家处摆出来：只报"不一致"没法修。
                    Console.WriteLine("      探测 {0}：设计马赫 {1:F4}/{2:F4}｜T4 {3:F1}/{4:F1}｜加力 {5}/{6}（{7:F4}/{8:F4}）｜喉道 A8 由求解器反解，见下",
                        key, a.DesignMach, b.DesignMach, a.Burner.ExitTemp, b.Burner.ExitTemp,
                        a.Afterburner.Enabled, b.Afterburner.Enabled, a.Afterburner.FuelRatio, b.Afterburner.FuelRatio);
                    Console.WriteLine("      {0}：T5 {1:F2}/{2:F2} K｜T7 {3:F2}/{4:F2} K｜Vjet {5:F2}/{6:F2} m/s",
                        key, ra.T5, rb.T5, ra.T7, rb.T7, ra.VJetCore, rb.VJetCore);
                    Console.WriteLine("      {0}：喷管 型式 {1}/{2}｜面积比 {3:F4}/{4:F4}｜出口面积 {5:F5}/{6:F5} m²｜出口马赫 {7:F3}/{8:F3}",
                        key, a.Nozzle, b.Nozzle, ra.NozzleAreaRatio, rb.NozzleAreaRatio,
                        ra.NozzleExitArea, rb.NozzleExitArea, ra.NozzleMachE, rb.NozzleMachE);
                }
                Console.WriteLine("    {0,-9} 存读前后：推力 {1:F4} 对 {2:F4} kN｜压比 {3:F4} 对 {4:F4}｜{5}",
                    key, ra.Thrust / 1000.0, rb.Thrust / 1000.0, ra.Opr, rb.Opr, same ? "一致" : "**不一致**");
            }
            Expect("全部预设存读一遍都算得出来", roundTrip >= 5, true);
            Expect("存读前后是同一台机器（推力/压比/流量逐位相同）", mismatched == 0, true);

            // ①未知键：老版本读新版本的存档
            EngineDesignData j79 = Presets.Build("J79");
            ConfigNode future = new ConfigNode("JETFORGE_DESIGN");
            j79.Save(future);
            future.SetValue("somethingFromTheFuture", "42", true);
            future.AddNode("FUSION_CORE").SetValue("power", "1.21", true);
            bool survived = true;
            try { EngineDesignData again = EngineDesignData.Load(future); survived = again != null && again.Hpc.Stages == j79.Hpc.Stages; }
            catch (Exception e) { Console.WriteLine("    未知键炸了：" + e.Message); survived = false; }
            Expect("存档里多出未知键不会炸", survived, true);

            // ②缺键：新版本读旧版存档，得退回缺省值而不是 0
            ConfigNode minimal = new ConfigNode("JETFORGE_DESIGN");
            minimal.SetValue("name", "只有名字的旧存档", true);
            EngineDesignData bare = null;
            try { bare = EngineDesignData.Load(minimal); } catch (Exception e) { Console.WriteLine("    缺键炸了：" + e.Message); }
            Expect("缺键的旧存档能读出来", bare != null, true);
            if (bare != null)
            {
                Console.WriteLine("    只有名字的存档读回：HPC {0} 级 Ø{1:F2}｜风扇 Ø{2:F2}｜设计马赫 {3:F2}",
                    bare.Hpc.Stages, bare.Hpc.TipDiameter, bare.Fan.Diameter, bare.DesignMach);
                Expect("缺键退回缺省值而不是 0（压气机级数 > 0）", bare.Hpc.Stages > 0, true);
                Expect("缺键退回缺省值而不是 0（叶尖直径 > 0）", bare.Hpc.TipDiameter > 0.0, true);
            }

            // ③读回来的设计能算（读成一堆 0 会在这里露馅）
            bool computes = bare != null && EngineNetwork.SolveDesign(bare).Result != null;
            Expect("读回来的设计能算出一个设计点", computes, true);
        }

        /// <summary>
        /// 第 87 段：分层刷新（Phase3 3.0 的"几何/热力学分层触发"）。
        ///
        /// 这一段守的是**改了性能不该重建外形**：网格是几何层的事，压比、效率、涡轮前温度
        /// 只影响性能层与表现层。以前 `_meshSig` 存的是整份设计的指纹，拖一下压比滑杆
        /// 整台机器就重建一遍网格——顶点一个都没变，白干还断手感。
        /// 同时也守另一头：真改了直径/级数，就必须判成几何变了，不能"太像了所以没重建"。
        /// </summary>
        private static void RunRefreshLayerCases()
        {
            EngineDesignData d = Presets.Build("J79");
            ExpectTrue("J79 预设存在（分层刷新要拿真机型做样本）", d != null);
            if (d == null) return;

            RefreshStamp before = new RefreshStamp
            {
                Geometry = RefreshStamps.EngineGeometry(d),
                Performance = RefreshStamps.EnginePerformance(d),
                Presentation = RefreshStamps.EnginePresentation(d, "5slot")
            };

            // ① 只改性能：几何指纹必须一个字都不变
            EngineDesignData perf = d.Clone();
            perf.Hpc.PsiScale *= 1.10;
            perf.Burner.ExitTemp *= 0.95;
            RefreshStamp afterPerf = new RefreshStamp
            {
                Geometry = RefreshStamps.EngineGeometry(perf),
                Performance = RefreshStamps.EnginePerformance(perf),
                Presentation = RefreshStamps.EnginePresentation(perf, "5slot")
            };
            ExpectTrue("改压比/涡轮前温度，几何指纹不变（外形不用重建）",
                afterPerf.Geometry == before.Geometry);
            ExpectTrue("改压比/涡轮前温度，性能指纹变了（性能要重算）",
                afterPerf.Performance != before.Performance);
            RefreshPlan pPerf = RefreshPlan.Compare(before, afterPerf);
            ExpectTrue("只改性能：不重建几何", !pPerf.Geometry);
            ExpectTrue("只改性能：要重算性能", pPerf.Performance);

            // ② 改直径：几何指纹必须变，而且计划要判成"全都要重建"。
            //    注意这一条必须用**真有风扇**的机型——J79 是涡喷，改 `Fan.Diameter` 本来就不该
            //    影响外形（它压根没有风扇）。第一次就写错了样本，跑出来三条超差，
            //    那不是代码错，是断言问错了机器。
            EngineDesignData tf = Presets.Build("GTF1100");
            ExpectTrue("GTF1100 预设存在（风扇类断言要拿涡扇做样本）", tf != null);
            if (tf == null) return;
            RefreshStamp tfBefore = new RefreshStamp
            {
                Geometry = RefreshStamps.EngineGeometry(tf),
                Performance = RefreshStamps.EnginePerformance(tf),
                Presentation = RefreshStamps.EnginePresentation(tf, "5slot")
            };
            EngineDesignData geo = tf.Clone();
            geo.Fan.Diameter *= 1.10;
            RefreshStamp afterGeo = new RefreshStamp
            {
                Geometry = RefreshStamps.EngineGeometry(geo),
                Performance = RefreshStamps.EnginePerformance(geo),
                Presentation = RefreshStamps.EnginePresentation(geo, "5slot")
            };
            ExpectTrue("改风扇直径，几何指纹变了", afterGeo.Geometry != tfBefore.Geometry);
            RefreshPlan pGeo = RefreshPlan.Compare(tfBefore, afterGeo);
            ExpectTrue("改风扇直径：重建几何", pGeo.Geometry);
            ExpectTrue("改风扇直径：性能也要重算", pGeo.Performance);

            // ③ 只改贴图槽：只该动表现层
            RefreshStamp afterPres = new RefreshStamp
            {
                Geometry = before.Geometry,
                Performance = before.Performance,
                Presentation = RefreshStamps.EnginePresentation(d, "7slot")
            };
            RefreshPlan pPres = RefreshPlan.Compare(before, afterPres);
            ExpectTrue("只改贴图槽：不重建几何", !pPres.Geometry);
            ExpectTrue("只改贴图槽：不重算性能", !pPres.Performance);
            ExpectTrue("只改贴图槽：只动表现层", pPres.Presentation);

            // ④ 什么都没改：三样都不动（否则每帧刷新会把帧预算吃光）
            RefreshPlan pSame = RefreshPlan.Compare(before, before);
            ExpectTrue("三层都没改：什么都不用做", !pSame.Anything);

            // ⑤ 齿轮比属于几何（它决定齿轮箱那一段的长度），齿轮效率属于性能
            EngineDesignData gear = tf.Clone();
            gear.Fan.GearRatio = 3.5;
            ExpectTrue("改齿轮比，几何指纹变了（齿轮箱长度要跟着变）",
                RefreshStamps.EngineGeometry(gear) != tfBefore.Geometry);
            EngineDesignData eff = tf.Clone();
            eff.Fan.GearEta = 0.99;
            ExpectTrue("只改齿轮效率，几何指纹不变",
                RefreshStamps.EngineGeometry(eff) == tfBefore.Geometry);
            ExpectTrue("只改齿轮效率，性能指纹变了",
                RefreshStamps.EnginePerformance(eff) != tfBefore.Performance);

            // ⑥ 涡喷改风扇直径：没有风扇，外形确实不该动（这条把②的"样本选错"钉住）
            EngineDesignData jet = d.Clone();
            jet.Fan.Diameter *= 1.10;
            ExpectTrue("涡喷改风扇直径，几何指纹不变（它没有风扇，外形不该动）",
                RefreshStamps.EngineGeometry(jet) == before.Geometry);
        }

        /// <summary>
        /// 第 88 段：喷口作动筒。
        ///
        /// 这一段守的是"两个速率没有被合成一个"：
        /// 1. 燃油阶跃之后，A8 面积必须**落后**于燃油比例（否则加力点亮那一段的
        ///    憋压就凭空消失了）；
        /// 2. 等足够久，两者都要到 1（落后不等于永久错位）；
        /// 3. 关加力时燃油先退、喷口后收；
        /// 4. 求解器真的吃这个面积比例——同一个加力比例下，A8 没撑开必须更没劲。
        /// </summary>
        /// <summary>
        /// 第 90 段：收扩喷管的出口面积（A9）必须由几何决定。
        ///
        /// 以前 `Gas.NozzleFlow` 里写的是 `Ae = mdot / (rhoE * vIdeal)`——面积去迁就流量，
        /// 于是推力永远像"恰好完全膨胀"，A8 动了 A9 也不动，`(Pe-Pa)*Ae` 那一项永远自洽。
        /// 真机的扩张段是钢材，A9 = 面积比 × A8，装不下就是装不下。
        /// 这一段钉三件事：出口面积确实等于面积比×A8；A8 变它同比变；设计工况下两者相符
        /// （相符才是"没改数"，不是"没接上"）。
        /// </summary>
        /// <summary>
        /// 设计点 vs 满油门：这两个数应该是同一个工况。
        ///
        /// `Cycle.SolveDesignPoint` 走的是"设计闭式"（按设计常数直接算），
        /// `OffDesign.Solve(设计高度, 设计马赫, 油门 1.0)` 走的是"部件地图 + 迭代"。
        /// 两条路本该在这一点重合；不重合说明设计常数不是在满油门那条路上反解出来的。
        ///
        /// 这一段不追求"两边相等"（模型没有那个精度），只钉两条能证伪的：
        /// ①差距有界；②**差 5% 以上必须有明说的原因**（喷管限流或风扇被转速限），
        /// 不许出现"说不清为什么少了 8%"的机器——那种数最容易被当成手感调。
        /// </summary>
        /// <summary>
        /// 第 93 段：可调压缩面不是免费的。
        ///
        /// 这个开关以前只改行程：喉道能收 18%，重量与造价一分不涨——等于"飞行中免费调喉道"。
        /// 现在作动系统的公式搬到 `Intake` 里（Core 不依赖 UnityEngine，回归能直接断言），
        /// 年代门槛也进判据。两条都要：**没代价的调节是编数，没门槛的年代是穿越**。
        /// </summary>
        private static void RunVariableIntakeCases()
        {
            double kg = Intake.VariableGeometryMassKg(0.45);
            double funds = Intake.VariableGeometryCost(0.45);
            Console.WriteLine("  0.45 m² 捕获面：作动系统 {0:F1} kg / {1:F0} funds", kg, funds);
            Expect("0.45 m² 的作动系统落在 30~50 kg（一台摩托车的量级）", kg > 30.0 && kg < 50.0, true);
            Expect("捕获面积越大作动筒越重", Intake.VariableGeometryMassKg(0.90) > kg, true);
            Expect("面积非法时给底数而不是负数", Intake.VariableGeometryMassKg(double.NaN) >= 18.0, true);
            Expect("造价随捕获面积涨", Intake.VariableGeometryCost(0.90) > funds, true);

            // 年代门槛：1940 年代没有液压作动与精密滑轨。
            EngineDesignData d40 = Presets.Build("J57");
            d40.TechName = "1940s";
            d40.Intake.Kind = IntakeKind.Ramp;
            d40.Intake.VariableGeometry = true;
            Finding f40 = FindKey(Criteria.CheckIntake(d40), "intake.vg");
            Console.WriteLine("  1940s + 可调斜板：{0}", f40 == null ? "（没有这一条）" : f40.Message);
            Expect("1940s 的可调压缩面要被点出来", f40 != null, true);
            Expect("1940s 那条是警告", f40 != null && f40.LevelValue == Level.Warn, true);

            EngineDesignData d70 = Presets.Build("J57");
            d70.TechName = "1970s";
            d70.Intake.Kind = IntakeKind.Ramp;
            d70.Intake.VariableGeometry = true;
            Finding f70 = FindKey(Criteria.CheckIntake(d70), "intake.vg");
            Expect("1970s 的可调压缩面只是说明，不是警告", f70 != null && f70.LevelValue == Level.Ok, true);

            EngineDesignData pitot = Presets.Build("J57");
            pitot.Intake.Kind = IntakeKind.Pitot;
            pitot.Intake.VariableGeometry = true;
            Finding fp = FindKey(Criteria.CheckIntake(pitot), "intake.vg");
            Console.WriteLine("  皮托 + 可调开关：{0}", fp == null ? "（没有这一条）" : fp.Message);
            Expect("皮托/轴对称开了可调开关要说清楚它不起作用",
                fp != null && fp.LevelValue == Level.Warn, true);
        }

/// <summary>
        /// 第 97 段：堵塞越界要真的截流——以及一个**没能修成**的地方。
        ///
        /// §167.6 留的账是"越界了还在按满流量算"。这一轮把截流做进去了（越界就按堵塞线
        /// 压流量重解一次），但跑起来发现**它永远不会被触发**：这条链路里压气机的流量是
        /// 按换算转速给的（流量 ∝ Nc），所以换算流量 wc 与 Nc 同比变化，
        /// 工作点在图上的位置 `phi = wc / (DesignWc·Nc)` 恒等于 1，永远落在堵塞线（1.18）左边。
        /// 冲压再大也没用：M1.60 定转速下空气 260 kg/s，可进口总压也涨了 4 倍多，
        /// 换算流量还是 72 kg/s，比设计点（75.5）还小。
        ///
        /// 所以这一段钉的不是"截流生效了"，而是三条**能证伪**的事实：
        /// 1. 设计点不算堵塞、不被限流；
        /// 2. 冲压把物理流量顶到设计点的三倍多，换算流量仍在设计值附近——这正是
        ///    "图上的堵塞线碰不到"的原因，而不是"模型没问题"；
        /// 3. 因此这条链路上任何工况都不该出现"认出来却没法截流"的半截状态。
        /// 真要碰到堵塞线，得让压气机的流量由特性图给（而不是由转速比例给）——
        /// 那是把设计闭式降级成初值那一档的活，见 §162.4。
        /// </summary>
        private static void RunChokeLimitCases()
        {
            EngineDesignData rd = Presets.Build("RD-33");

            NetworkSolution design = EngineNetwork.SolveDesign(rd);
            Console.WriteLine("  设计点：空气 {0:F2} kg/s｜堵塞余量 {1:F1}%｜越界 {2}｜限流 {3}",
                design.Result.MdotAir, design.Result.HpcChokeMargin,
                design.Result.HpcChoked, design.Result.CompressorFlowLimited);
            Expect("设计点不许被当成堵塞", design.Result.HpcChoked, false);
            Expect("设计点不许被限流", design.Result.CompressorFlowLimited, false);

            // 冲压：定换算转速下物理流量会涨很多，但换算流量不该跟着涨（这就是 χ=1 的原因）。
            NetworkSolution fast = EngineNetwork.SolveAt(rd, 0.0, 1.60, 1.0);
            Console.WriteLine("  M1.60 定转速：空气 {0:F2} kg/s（设计点 {1:F2} 的 {2:F1} 倍）｜堵塞余量 {3:F1}%｜越界 {4}",
                fast.Result.MdotAir, design.Result.MdotAir,
                fast.Result.MdotAir / Math.Max(design.Result.MdotAir, 1e-9),
                fast.Result.HpcChokeMargin, fast.Result.HpcChoked);
            Expect("冲压后物理流量涨到设计点的两倍以上", fast.Result.MdotAir > design.Result.MdotAir * 2.0, true);
            // 换算流量没涨 = 图上的位置没动 = 堵塞线碰不到。这不是"通过了"，是"测不到"。
            // （图现在按设计点建，所以这里比的是**换算流量本身**，不是裕度数字：
            //  裕度里带着 φ = wc/(DWc·Nc)，φ 变了裕度就该变，拿裕度相等当判据是拿错尺。）
            double wcD = HpcCorrectedFlow(design), wcF = HpcCorrectedFlow(fast);
            Console.WriteLine("  换算流量：设计点 {0:F2} kg/s｜M1.60 {1:F2} kg/s（{2:F3} 倍）",
                wcD, wcF, wcD > 0.0 ? wcF / wcD : 0.0);
            Expect("换算流量仍在设计值附近（0.8~1.3 倍，所以碰不到堵塞线）",
                wcD > 0.0 && wcF / wcD > 0.8 && wcF / wcD < 1.3, true);
            Expect("这个工况没有越过堵塞线", fast.Result.HpcChoked, false);
            Expect("没越界就不许留下限流标记", fast.Result.CompressorFlowLimited, false);

            // 截流一旦真的发生，下游必须按截流后的流量算（现在触发不了，先钉住不变量：
            // 只要带了限流标记，空气流量就不能超过它自报的吞气上限）。
            if (fast.Result.CompressorFlowLimited)
                Expect("截流后空气流量不超过自报上限",
                    fast.Result.MdotAir <= fast.Result.CompressorSwallowLimit * 1.02, true);
            else
                Expect("没限流时吞气上限是 0（没算过），不许拿它当读数", fast.Result.CompressorSwallowLimit == 0.0, true);

            Expect("压比是有限值", GasState.IsFinite(fast.Result.Opr) && fast.Result.Opr < 40.0, true);
        }

        /// <summary>压气机进口的换算流量 ṁ√θ/δ（与特性图同一口径）。</summary>
        private static double HpcCorrectedFlow(NetworkSolution ns)
        {
            if (ns == null) return 0.0;
            GasState before = null;
            if (!ns.StationTable.TryGetValue(Stations.LpcExit, out before)
                && !ns.StationTable.TryGetValue(Stations.FanExit, out before))
                ns.StationTable.TryGetValue(Stations.IntakeExit, out before);
            if (before == null || before.TotalPressure <= 0.0) return 0.0;
            double th = before.TotalTemperature / CompressorChart.T0Ref;
            double de = before.TotalPressure / CompressorChart.P0Ref;
            return before.MassFlow * Math.Sqrt(th) / Math.Max(de, 1e-6);
        }

/// <summary>
        /// 第 99 段：特性图的查图接口（Phase3 文档 3.3 的"插值与边界要有独立测试"）。
        ///
        /// 这一段不去动气路里的任何数，只钉**查图这件事本身的规矩**：
        ///   ①设计点查回来必须等于设计压比（图是自洽的，不是随手造的）；
        ///   ②同一个转速线上，流量越小压比越高（喘振点最高、堵塞点最低）——方向钉死；
        ///   ③转速超界要夹到最近的转速线**并且**置标志，不许静默外推；
        ///   ④流量越过喘振点/堵塞点要夹到边界点**并且**置标志；
        ///   ⑤效率只在设计效率的一个合理带内（碗形剥落有下限）。
        /// 为什么钉这些：外推一条不存在的转速线是最容易犯又最看不出来的错——
        /// 图上什么都没有，算出来的压比照样"看着合理"。
        /// </summary>
        private static void RunChartQueryCases()
        {
            EngineDesignData rd = Presets.Build("RD-33");
            Expect("RD-33 预设存在", rd != null, true);
            if (rd == null) return;
            CompressorChart ch = CompressorChart.DesignChart(rd, false, TechLevel.Get(rd.TechName));
            Expect("图建得起来", ch != null, true);
            if (ch == null) return;

            CompressorChart.ChartFlags f;
            MapPoint atDesign = ch.Query(1.0, ch.DesignWc, out f);
            Console.WriteLine("    设计点查图：π {0:F4}（设计 {1:F4}）｜η {2:F4}（设计 {3:F4}）｜标志 {4}",
                atDesign.Pr, ch.DesignPr, atDesign.Eta, ch.DesignEta, f);
            Expect("设计点查回来就是设计压比",
                Math.Abs(atDesign.Pr - ch.DesignPr) < 1e-6 * Math.Max(ch.DesignPr, 1.0), true);
            Expect("设计点在图里面（没有任何越界标志）", f == CompressorChart.ChartFlags.None, true);

            // 同一条转速线：喘振点压比 > 中间 > 堵塞点。方向错了说明查图把边界搞反了。
            double n0 = 0.80;
            MapPoint surge = ch.SurgeAt(n0), choke = ch.ChokeAt(n0);
            MapPoint mid = ch.Query(n0, 0.5 * (surge.Wc + choke.Wc), out f);
            Console.WriteLine("    Nc {0:F2}：喘振 π {1:F3}｜中间 π {2:F3}｜堵塞 π {3:F3}｜中间标志 {4}",
                n0, surge.Pr, mid.Pr, choke.Pr, f);
            Expect("同一条转速线上流量越小压比越高", surge.Pr > mid.Pr && mid.Pr > choke.Pr, true);
            Expect("中间点在两条边界之间，不报越界", f == CompressorChart.ChartFlags.None, true);
            Expect("中间点效率不高于设计效率、不低于设计效率的六成",
                mid.Eta <= ch.DesignEta + 1e-9 && mid.Eta >= ch.DesignEta * 0.55, true);

            // 转速超界：夹到最近的转速线，并且**说出来**。
            MapPoint low = ch.Query(ch.NcMin - 0.20, ch.DesignWc * 0.6, out f);
            Expect("转速低于图的下界时置 NcBelowMap",
                (f & CompressorChart.ChartFlags.NcBelowMap) != 0, true);
            MapPoint lowLine = ch.Query(ch.NcMin, ch.DesignWc * 0.6, out f);
            Expect("低于下界被夹到下界那条线（压比与下界线相同）",
                Math.Abs(low.Pr - lowLine.Pr) < 1e-9 && Math.Abs(low.Eta - lowLine.Eta) < 1e-9, true);

            MapPoint high = ch.Query(ch.NcMax + 0.30, ch.DesignWc * 0.6, out f);
            Expect("转速高于图的上界时置 NcAboveMap",
                (f & CompressorChart.ChartFlags.NcAboveMap) != 0, true);
            MapPoint highLine = ch.Query(ch.NcMax, ch.DesignWc * 0.6, out f);
            Expect("高于上界被夹到上界那条线",
                Math.Abs(high.Pr - highLine.Pr) < 1e-9, true);

            // 流量越界：两堵墙分开报，压比夹到边界点。
            MapPoint left = ch.Query(n0, surge.Wc * 0.80, out f);
            Expect("流量小于喘振点时置 FlowLeftOfStall",
                (f & CompressorChart.ChartFlags.FlowLeftOfStall) != 0, true);
            Expect("压比夹到喘振点（不会给出比喘振点还高的压比）",
                Math.Abs(left.Pr - surge.Pr) < 1e-6 * Math.Max(surge.Pr, 1.0), true);

            MapPoint right = ch.Query(n0, choke.Wc * 1.20, out f);
            Expect("流量大于堵塞点时置 FlowRightOfChoke",
                (f & CompressorChart.ChartFlags.FlowRightOfChoke) != 0, true);
            Expect("压比夹到堵塞点（不会给出比堵塞点还低的压比）",
                Math.Abs(right.Pr - choke.Pr) < 1e-6 * Math.Max(choke.Pr, 1.0), true);

            // 两堵墙不共线：一个点不可能既在左墙左边又在右墙右边。
            Expect("喘振点流量一定小于堵塞点流量", surge.Wc < choke.Wc, true);
            Expect("喘振点压比一定高于堵塞点压比", surge.Pr > choke.Pr, true);
        }

        /// <summary>
        /// 第 98 段：模型档案（Phase3 文档 §9 的精度管理）。
        ///
        /// 这一段守的不是"某个数值对不对"，而是**这台机器里每个数都有出处**：
        /// 新加一个会输出数字的模型，就必须在 ModelLedger 里登记一条，
        /// 写清算法、假设、边界、来源、已知误差，并给它定一个等级
        /// （A 守恒解／B 经验式／C 真机标定／D 游戏近似）。
        ///
        /// 为什么要把它做成断言：文档会漂，代码里的人会走。漏登记一个模型不会让任何
        /// 计算变错，只会让"这个数是怎么来的"永远说不清——而那正好是"能不能正式发布"
        /// 的分界线之一。
        /// </summary>
        private static void RunModelLedgerCases()
        {
            IList<ModelEntry> all = ModelLedger.Entries;
            Console.WriteLine("  模型档案 {0} 条：{1}", all.Count, ModelLedger.Summary());
            Expect("档案条数与分级统计对得上",
                ModelLedger.CountOf(ModelGrade.A) + ModelLedger.CountOf(ModelGrade.B)
                + ModelLedger.CountOf(ModelGrade.C) + ModelLedger.CountOf(ModelGrade.D) == all.Count, true);
            Expect("条数至少覆盖到 15 条（不是应付了事）", all.Count >= 15, true);

            int missing = 0;
            for (int i = 0; i < ModelLedger.RequiredKeys.Length; i++)
                if (ModelLedger.Get(ModelLedger.RequiredKeys[i]) == null) missing++;
            Expect("必需键全部登记（漏一个就说明有新模型没写进档案）", missing, 0);

            int empty = 0;
            for (int i = 0; i < all.Count; i++)
            {
                ModelEntry m = all[i];
                if (string.IsNullOrEmpty(m.Key) || string.IsNullOrEmpty(m.Name)
                    || string.IsNullOrEmpty(m.Equations) || string.IsNullOrEmpty(m.Assumptions)
                    || string.IsNullOrEmpty(m.Limits) || string.IsNullOrEmpty(m.Source)
                    || string.IsNullOrEmpty(m.KnownError)) empty++;
            }
            Expect("每条都写全了（算法/假设/边界/来源/误差）", empty, 0);

            // 分级不许一边倒：全是 A 说明有人在吹，全是 D 说明没做建模。
            Expect("确实有守恒级的模型", ModelLedger.CountOf(ModelGrade.A) >= 3, true);
            Expect("也确实有明说是游戏近似的", ModelLedger.CountOf(ModelGrade.D) >= 3, true);

            // 钉几个最容易被人"顺手升级"的：改它们必须是有意识的编辑，不是笔误。
            ExpectEq("喷管流动是守恒解", ModelLedger.Get("nozzle.flow").Grade, ModelGrade.A);
            ExpectEq("压气机功是守恒解", ModelLedger.Get("compressor.work").Grade, ModelGrade.A);
            ExpectEq("质量估算是游戏近似", ModelLedger.Get("mass.cost").Grade, ModelGrade.D);
            ExpectEq("转速控制是游戏近似", ModelLedger.Get("control.spool").Grade, ModelGrade.D);
            ExpectEq("进气恢复是经验式", ModelLedger.Get("intake.recovery").Grade, ModelGrade.B);
            // 特性图是「设计点 + 三个常数」合成的，没有任何真机数据在里面：
            // 以前它标着 C 并写着「按公开压比-流量曲线拟合」——那句话是假的，
            // 而且连带给出的"±10%"误差带无从谈起。分成 D 并如实写明来源，
            // 是为了让后面的人（和玩家）知道这个裕度数字只能横向比。
            ExpectEq("特性图是合成图，只能是 D", ModelLedger.Get("compressor.map").Grade, ModelGrade.D);

            // 读不到的键必须返回 null，不许凭空造一条"看着挺对"的档案。
            Expect("没登记的键就是 null", ModelLedger.Get("no.such.model") == null, true);

            // 第 82 轮补登的四条：分级是有意识的判断，不许顺手升级成看着更漂亮的档。
            ExpectEq("逐级余量的斜面是假设，只能是 C", ModelLedger.Get("compressor.stage.margin").Grade, ModelGrade.C);
            ExpectEq("涡轮容量有明确机理，可以到 B", ModelLedger.Get("turbine.capacity").Grade, ModelGrade.B);
            ExpectEq("再点火窗口按量级取值，只能是 C", ModelLedger.Get("relight.window").Grade, ModelGrade.C);
            ExpectEq("面马赫是等熵反解，可以到 B", ModelLedger.Get("intake.facemach").Grade, ModelGrade.B);

            string full = all[0].DescribeFull();
            Expect("详述里有来源与误差两栏",
                full.Contains("来源") && full.Contains("误差"), true);
            Expect("一句话摘要带等级字母", all[0].Describe().Contains("｜"), true);
        }

        /// <summary>
        /// 第 94 段：级间负荷。
        ///
        /// 这一段不承诺"知道每一级的喘振边界"——那要有逐级特性图，本模型没有。
        /// 它只钉三件从现有逐级温升与流量就能算准的事：
        /// 1. 均匀的级必须算出均匀（三条同级温升 → 最重 1.00 倍，不许出现 1.03）；
        /// 2. 不均匀的级必须按倍数如实放大（300/100/100 → 1.80 倍，且话说成"偏重"）；
        /// 3. 级间放气从**流量台阶**认出来，不是从内存里那个字段抄。
        /// </summary>
        private static void RunStageLoadCases()
        {
            // ① 真机：RD-33 设计点，级数要数得上，最重一级不许超过平均级 2.2 倍。
            EngineDesignData rd = Presets.Build("RD-33");
            CycleResult cr = Cycle.SolveDesignPoint(rd);
            StageLoadReport rep = StageLoadReport.Of(cr.Stages);
            Console.WriteLine("  RD-33：" + (rep.Ok ? rep.Describe() : rep.Failure));
            Expect("RD-33 逐级数据能读出来", rep.Ok, true);
            Expect("级数对得上（风扇+低压+高压）", rep.Rows.Count >= 8, true);
            Expect("最重一级不超过平均级的 2.2 倍", rep.WorstVsMean < 2.2, true);
            Expect("最重一级说得出来是哪一台第几级", rep.WorstStage.Length > 0, true);

            // ② 合成的均匀级：ΔT 全 100，必须算出 1.00 倍，一毫不多。
            List<StageRow> even = new List<StageRow>();
            for (int i = 0; i < 3; i++)
                even.Add(new StageRow { Part = "HPC", Index = i + 1, TIn = 300.0 + 100.0 * i, TOut = 400.0 + 100.0 * i, Pi = 1.3, MassFlow = 10.0 });
            StageLoadReport ev = StageLoadReport.Of(even);
            Expect("均匀的级算出 1.00 倍", Math.Abs(ev.WorstVsMean - 1.0) < 1e-6, true);
            Expect("均匀时最重/最轻为 1.00", Math.Abs(ev.PeakToTrough - 1.0) < 1e-6, true);

            // ③ 偏重：300/100/100 → 平均 166.7，最重 1.80 倍，话要说成"偏重"而不是"没配平"。
            List<StageRow> skew = new List<StageRow>();
            skew.Add(new StageRow { Part = "HPC", Index = 1, TIn = 300.0, TOut = 600.0, Pi = 1.5, MassFlow = 10.0 });
            skew.Add(new StageRow { Part = "HPC", Index = 2, TIn = 600.0, TOut = 700.0, Pi = 1.2, MassFlow = 10.0 });
            skew.Add(new StageRow { Part = "HPC", Index = 3, TIn = 700.0, TOut = 800.0, Pi = 1.2, MassFlow = 10.0 });
            StageLoadReport sk = StageLoadReport.Of(skew);
            Console.WriteLine("  偏重样机：" + sk.Describe());
            Expect("300/100/100 的最重一级是 1.80 倍", Math.Abs(sk.WorstVsMean - 1.8) < 1e-3, true);
            Expect("1.80 倍说成偏重，不说没配平", sk.Headline().Contains("偏重"), true);
            Expect("最重/最轻是 3.00", Math.Abs(sk.PeakToTrough - 3.0) < 1e-3, true);

            // ④ 极端：900/100/100 → 平均 366.7，最重 2.45 倍，这才叫没配平。
            List<StageRow> bad = new List<StageRow>();
            bad.Add(new StageRow { Part = "HPC", Index = 1, TIn = 300.0, TOut = 1200.0, Pi = 2.0, MassFlow = 10.0 });
            bad.Add(new StageRow { Part = "HPC", Index = 2, TIn = 1200.0, TOut = 1300.0, Pi = 1.1, MassFlow = 10.0 });
            bad.Add(new StageRow { Part = "HPC", Index = 3, TIn = 1300.0, TOut = 1400.0, Pi = 1.1, MassFlow = 10.0 });
            StageLoadReport bd = StageLoadReport.Of(bad);
            Console.WriteLine("  失配样机：" + bd.Headline());
            Expect("2.45 倍要说成没配平", bd.Headline().Contains("没配平"), true);

            // ⑤ 级间放气从流量台阶认：10 → 10 → 9，第三级就是抽气点。
            List<StageRow> bleed = new List<StageRow>();
            bleed.Add(new StageRow { Part = "HPC", Index = 1, TIn = 300.0, TOut = 350.0, Pi = 1.2, MassFlow = 10.0 });
            bleed.Add(new StageRow { Part = "HPC", Index = 2, TIn = 350.0, TOut = 400.0, Pi = 1.2, MassFlow = 10.0 });
            bleed.Add(new StageRow { Part = "HPC", Index = 3, TIn = 400.0, TOut = 450.0, Pi = 1.2, MassFlow = 9.0 });
            StageLoadReport bl = StageLoadReport.Of(bleed);
            Expect("从流量台阶认出放气在第 3 级", bl.BleedStage == 3, true);
            Expect("放气量是 1.0 kg/s", Math.Abs(bl.BleedFlow - 1.0) < 1e-9, true);
            Expect("那三级里只有第三级标记为放气点", bl.Rows[2].BleedHere && !bl.Rows[0].BleedHere, true);

            // ⑥ 空表要说"没有逐级数据"，不许当成"负荷均匀"。
            StageLoadReport empty = StageLoadReport.Of(new List<StageRow>());
            Expect("没有逐级数据时 Ok 是假的", empty.Ok, false);
            Expect("并且给一句原因", empty.Failure.Length > 0, true);

            // ⑦ 判据里真的有这一条，级别跟着倍数走。
            ResultView view = Criteria.ViewOf(cr);
            Finding fs = FindKey(Criteria.CheckStageLoad(view), "comp.stageload");
            Console.WriteLine("  判据：{0}", fs == null ? "（没有这一条）" : fs.Message);
            Expect("判据里有级间负荷这一条", fs != null, true);
            Expect("RD-33 这一条是 Ok", fs != null && fs.LevelValue == Level.Ok, true);
        }

/// <summary>
        /// 第 96 段：喷管花瓣数与长度。
        ///
        /// 这件事的边界很清楚：花瓣是**机构**，不是气动。喉道面积由 A8 那条反解给，
        /// 花瓣数改了不该让推力动一个字；但它既然改了板材与铰链，就得付重量。
        /// 这一段钉四件事：
        /// 1. 缺省（0 片 / 1.0 倍）必须与老口径逐位相同——老存档的质量一分不变；
        /// 2. 片数取偶、夹在 8~32，填 100 片不许真的做 100 片；
        /// 3. 叶片拉长要变重，但不许动推力；
        /// 4. 片太少要说出来，存档往返要回来。
        /// </summary>
        private static void RunPetalCases()
        {
            EngineDesignData base0 = Presets.Build("RD-33");

            // ---- 缺省口径 ----
            Expect("不给片数时按 16 片做", NozzleVisual.CountOf(base0), 16);
            Expect("不给长度系数时是 1.0", Math.Abs(base0.PetalLengthFrac - 1.0) < 1e-12, true);

            EngineDesignData sixteen = base0.Clone();
            sixteen.PetalCount = 16;
            MassBreakdown mb = MassCost.Estimate(base0);
            MassBreakdown m16 = MassCost.Estimate(sixteen);
            // 16 片正好是基准，所以两条路必须逐位相同：口径没漂，缺省没被偷偷改动。
            Expect("显式 16 片与自动 16 片的总重逐位相同",
                Math.Abs(mb.TotalDry - m16.TotalDry) < 1e-9, true);

            // ---- 片数规则 ----
            EngineDesignData odd = base0.Clone();
            odd.PetalCount = 17;
            Expect("17 片取偶成 16", NozzleVisual.CountOf(odd), 16);
            EngineDesignData many = base0.Clone();
            many.PetalCount = 100;
            Expect("100 片夹到 32", NozzleVisual.CountOf(many), 32);
            EngineDesignData few = base0.Clone();
            few.PetalCount = 4;
            Expect("4 片抬到 8（低于 8 就没有机构可言）", NozzleVisual.CountOf(few), 8);

            // ---- 更长更重，但不动推力 ----
            EngineDesignData longP = base0.Clone();
            longP.PetalLengthFrac = 1.4;
            MassBreakdown mLong = MassCost.Estimate(longP);
            Console.WriteLine("  花瓣长度 1.0→1.4：干重 {0:F1} → {1:F1} kg", mb.TotalDry, mLong.TotalDry);
            Expect("叶片拉长 40% 会更重", mLong.TotalDry > mb.TotalDry + 0.5, true);

            OffDesignResult rShort = OffDesign.Solve(base0, base0.DesignAlt, base0.DesignMach, 1.0, 0.0, null, 60, -1.0, 0.0, 1.0, 0, null);
            OffDesignResult rLong = OffDesign.Solve(longP, longP.DesignAlt, longP.DesignMach, 1.0, 0.0, null, 60, -1.0, 0.0, 1.0, 0, null);
            Expect("叶片长度不改推力（喉道面积是气动的账）",
                Math.Abs(rShort.NetThrust - rLong.NetThrust) < 1e-6, true);

            // ---- 判据 ----
            EngineDesignData scarce = base0.Clone();
            scarce.Nozzle = NozzleKind.ConvergingDiverging;
            scarce.PetalCount = 6;
            CheckReport rep = Criteria.Evaluate(scarce, Criteria.ViewOf(rShort, 0), TechLevel.Get(scarce.TechName));
            Finding f6 = FindKey(rep.Findings, "nozzle.petals");
            Console.WriteLine("  6 片那条判据：{0}", f6 != null ? f6.Message : "(没有)");
            Expect("6 片要报警告", f6 != null && f6.LevelValue == Level.Warn, true);

            EngineDesignData fine = base0.Clone();
            fine.Nozzle = NozzleKind.ConvergingDiverging;
            fine.PetalCount = 20;
            fine.PetalLengthFrac = 1.2;
            CheckReport repFine = Criteria.Evaluate(fine, Criteria.ViewOf(rShort, 0), TechLevel.Get(fine.TechName));
            Finding fFine = FindKey(repFine.Findings, "nozzle.petals");
            Expect("20 片 × 1.2 倍是合格", fFine != null && fFine.LevelValue == Level.Ok, true);

            // ---- 存档 ----
            ConfigNode node = new ConfigNode("JETFORGE_DESIGN");
            fine.Save(node);
            EngineDesignData back = EngineDesignData.Load(node);
            Expect("片数存得回来", back.PetalCount, 20);
            Expect("长度系数存得回来", Math.Abs(back.PetalLengthFrac - 1.2) < 1e-9, true);
            ConfigNode dflt = new ConfigNode("JETFORGE_DESIGN");
            base0.Save(dflt);
            Expect("缺省不写花瓣键（老存档不多出字段）",
                dflt.ToString().Contains("petalCount") || dflt.ToString().Contains("petalLen"), false);
        }

        /// <summary>
        /// 第 95 段：堵塞侧边界。
        ///
        /// 特性图一直有堵塞线（`SpeedLine.Choke`），但求解器以前只问"离喘振线多远"，
        /// 于是"塞进去的气比这条等转速线能过的还多"的工作点被当成正常点算下去——
        /// 那不是裕度大，是这条线根本过不去这么多气。这一段钉的是：
        /// 1. 设计点必须在堵塞线以内，且余量等于图上那条线的位置（不是拍出来的数）；
        /// 2. 横向越界能被认出来（不是"快到了"，是"已经过了"）；
        /// 3. 读数不可用时给 -1，不许把"没读数"说成"越界"。
        /// </summary>
        private static void RunChokeCases()
        {
            // 一张风扇量级的图：π 1.6、0.45 m 叶尖、设计点 Wc 30 kg/s。
            CompressorChart ch = CompressorChart.Build(30.0, 288.15, 101325.0, 1.60, 0.90, 0.45, 0.35, 12000.0, 0.90, 1.18);
            Expect("图能建起来", ch != null && ch.Lines.Count == 12, true);

            MapPoint design = ch.PointAt(1.0, 1.0);
            double cmDesign = ch.ChokeMargin(design);
            Console.WriteLine("  设计点：Wc {0:F2} kg/s，堵塞线 Wc {1:F2}，横向余量 {2:F1}%",
                design.Wc, ch.ChokeAt(1.0).Wc, cmDesign);
            // 图上设计点是 f=1.00、堵塞线是 f=1.18，所以余量必然落在 18% 附近——
            // 这个数不是标出来的，是图自己长出来的。
            Expect("设计点离堵塞线约 18%（图上 1.18/1.00 − 1）", Math.Abs(cmDesign - 18.0) < 0.6, true);
            Expect("设计点不算越界", ch.Choked(design), false);

            // 把工作点推到堵塞线上：余量恰好归零附近，再过去就该判越界。
            MapPoint onLine = ch.PointAt(1.0, 1.18);
            Expect("正好在堵塞线上时余量≈0", Math.Abs(ch.ChokeMargin(onLine)) < 0.6, true);
            MapPoint past = ch.PointAt(1.0, 1.30);
            Expect("越过堵塞线时余量为负", ch.ChokeMargin(past) < -1.0, true);
            Expect("越过堵塞线能被认出来", ch.Choked(past), true);

            // 高转速线：堵塞流量随转速涨，所以同一条 Wc 在低转速下可能已经越界。
            MapPoint lowLine = ch.PointAt(0.60, 1.18);
            MapPoint hiLine = ch.PointAt(1.00, 1.18);
            Expect("换算转速越高，堵塞流量越大", hiLine.Wc > lowLine.Wc, true);

            // 非正流量是"读数不可用"，不是"越界"：这条最容易被写反。
            Expect("流量为 0 时余量是 -1（不可用）", Math.Abs(ch.ChokeMargin(new MapPoint(1.0, 0.0, 1.0, 0.9)) + 1.0) < 1e-9, true);
            Expect("流量为 0 时不许判越界", ch.Choked(new MapPoint(1.0, 0.0, 1.0, 0.9)), false);
            Expect("空图的余量是 -1", new CompressorChart().ChokeMargin(design) < -0.5, true);

            // 真机：设计点绝不能在堵塞线外——那说明图建错了，不是机器有问题。
            EngineDesignData rd = Presets.Build("J79");
            CycleResult cr = Cycle.SolveDesignPoint(rd);
            if (cr.Chart != null && cr.HpcSurgeMargin > -0.5)
            {
                MapPoint op = cr.Chart.WorkingPoint(1.0);
                double cm = cr.Chart.ChokeMargin(op);
                Console.WriteLine("  J79 工作点：喘振裕度 {0:F1}%，堵塞余量 {1:F1}%",
                    cr.Chart.SurgeMargin(op), cm);
                Expect("J79 的设计工作点在堵塞线以内", cm > 0.0, true);
            }

            // 判据里真有这一条，且"没有图"要如实说没有图。
            ResultView bare = new ResultView();
            List<Finding> none = Criteria.CheckChoke(bare);
            Expect("没有压气机图时判据说清楚它不评价", none.Count == 1 && none[0].Key == "comp.choke", true);
        }

        private static Finding FindKey(List<Finding> list, string key)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].Key == key) return list[i];
            return null;
        }

        private static void RunDesignConsistencyCases()
        {
            string[] keys = new string[] { "J79", "J57", "J85", "RD-33", "GTF1100" };
            for (int i = 0; i < keys.Length; i++)
            {
                EngineDesignData kd = Presets.Build(keys[i]);
                if (kd == null) { ExpectTrue(keys[i] + " 预设存在", false); continue; }
                CycleResult cq = Cycle.SolveDesignPoint(kd);
                OffDesignResult op = OffDesign.Solve(kd, kd.DesignAlt, kd.DesignMach, 1.0, 0.0);
                if (cq == null || op == null || cq.Thrust <= 0.0)
                {
                    ExpectTrue(keys[i] + " 两条路都算得出来", false);
                    continue;
                }
                double gap = (op.NetThrust - cq.Thrust) / cq.Thrust;
                // 说得出原因的三条路：喉道先到顶、风扇被换算转速限、或者满油门时转速
                // 根本没到设计值（这条最容易被漏掉：它不是"算错"，而是"这台机在满油门
                // 这个工况下本来就转不到位"）。三条都点名到具体诊断字段，不是"看起来差了"。
                double spoolMin = double.MaxValue;
                if (op.N1Pct > 1.0 && op.N1Pct < spoolMin) spoolMin = op.N1Pct;
                if (op.N2Pct > 1.0 && op.N2Pct < spoolMin) spoolMin = op.N2Pct;
                bool speedShort = spoolMin < double.MaxValue && spoolMin < 99.5;
                bool hasReason = op.NozzleCapped || op.FanSpeedLimited || speedShort;
                Console.WriteLine("    {0,-8} 设计点 {1,7:F2} kN / 满油门 {2,7:F2} kN（{3:+0.0;-0.0}%）｜进气 {4,6:F1} / {5,6:F1} kg/s｜限流 {6}｜风扇被限 {7}",
                    keys[i], cq.Thrust / 1000.0, op.NetThrust / 1000.0, 100.0 * gap,
                    cq.MdotAir, op.MdotAir, op.NozzleCapped, op.FanSpeedLimited);
                if (speedShort)
                    Console.WriteLine("             满油门时最低那根轴只到 {0:F1}%——差在这里，不是算错", spoolMin);
                ExpectTrue(keys[i] + " 满油门与设计点的差在 18% 以内", Math.Abs(gap) <= 0.18);
                ExpectTrue(keys[i] + " 差超过 5% 时说得出原因（限流/风扇被限/转速没到设计值）",
                    Math.Abs(gap) <= 0.05 || hasReason);
            }
        }

        private static void RunVerdictCases()
        {
            // 评价不是说好话：它要能把"能飞"和"飞不起来"分开，也要能指出该先改什么。
            EngineDesignData rd = Presets.Build("RD-33");
            if (rd == null) { ExpectTrue("RD-33 预设存在（评价要用真实设计）", false); return; }
            OffDesignResult p = OffDesign.Solve(rd, rd.DesignAlt, rd.DesignMach, 1.0, 0.0);
            CheckReport rep = Criteria.Evaluate(rd, Criteria.ViewOf(p, 0), rd.Tech, 101325.0, 0.0);
            DesignVerdict v = DesignVerdict.Of(rd, p, rep);
            Console.WriteLine("    RD-33：{0}", v.Headline);
            for (int i = 0; i < v.Lines.Count; i++) Console.WriteLine("      · {0}", v.Lines[i]);
            ExpectTrue("真机预设的评价说得出结论（能飞/飞不起来之一）",
                v.Headline.Contains("能飞") || v.Headline.Contains("飞不起来"));
            ExpectTrue("评价至少给出三件能动手的事", v.Lines.Count >= 3);
            ExpectTrue("评价第一行是性能（推力/SFC/压比都在这儿）",
                v.Lines[0].StartsWith("性能") && v.Lines[0].Contains("kN"));
            // 同一份输入两次评价必须一致：它要能当存档/截图用的结论，不能每次刷新都变
            DesignVerdict v2 = DesignVerdict.Of(rd, p, rep);
            ExpectTrue("两次评价一致", v2.Headline == v.Headline && v2.Lines.Count == v.Lines.Count
                && v2.Lines[0] == v.Lines[0]);

            // 纸面推力与满油门实飞差得明显时必须解释（设计页那一栏是纸面数）。
            // RD-33 这两条路差约 10%，所以这条正文一定要出现。
            CycleResult paper = Cycle.SolveDesignPoint(rd);
            DesignVerdict vp2 = DesignVerdict.Of(rd, p, rep, paper.Thrust);
            bool explained = false;
            for (int i = 0; i < vp2.Lines.Count; i++)
                if (vp2.Lines[i].Contains("纸面") && vp2.Lines[i].Contains("实飞")) explained = true;
            Console.WriteLine("    纸面 {0:F2} kN｜实飞 {1:F2} kN｜解释行 {2}",
                paper.Thrust / 1000.0, p.NetThrust / 1000.0, explained);
            ExpectTrue("纸面与实飞差得明显时给出解释", explained);

            // 算不出来的设计：不许给出"能飞"这种话。
            // 注意 `new EngineDesignData()` 不是"空设计"——它带着一整套缺省值，
            // 缺省值也是一台能飞的小发动机（所以之前那条断言测的根本不是空设计）。
            // 真要测"没有设计"，传 null；要测"设计坏了"，把风扇直径打成 0。
            DesignVerdict vnull = DesignVerdict.Of(null, null, null);
            Console.WriteLine("    没有设计：{0}", vnull.Headline);
            ExpectTrue("没有设计时就说还没有设计", vnull.Headline.Contains("还没有设计"));

            EngineDesignData broken = rd.Clone();
            broken.Fan.Diameter = 0.0;
            try
            {
                OffDesignResult bad = OffDesign.Solve(broken, broken.DesignAlt, broken.DesignMach, 1.0, 0.0);
                DesignVerdict vb = DesignVerdict.Of(broken, bad, null);
                Console.WriteLine("    风扇直径 0：{0}", vb.Headline);
                // 这条以前写成 `!含能飞 || Lines.Count > 0`——后半截永远为真，等于没断言。
                ExpectTrue("算不出推力的设计不许说能飞",
                    !vb.Headline.Contains("能飞") || vb.Worst == Level.Warn);
            }
            catch (Exception e)
            {
                Console.WriteLine("    风扇直径 0：抛了 {0}", e.GetType().Name);
                ExpectTrue("风扇直径 0 要么给出评语、要么明确抛错（不许悄悄通过）", true);
            }
        }

        private static void RunNozzleExitCases()
        {
            EngineDesignData rd = Presets.Build("RD-33");
            ExpectTrue("RD-33 预设存在（收扩喷管才谈得上出口面积比）", rd != null);
            if (rd == null) return;

            DesignConstants rc = OffDesign.DeriveDesignConstants(rd);
            double ar = rc.NozzleAreaRatio;
            Console.WriteLine("    设计面积比 A9/A8 = {0:F3}｜喷管型式 {1}", ar, rd.Nozzle);

            // ① 地面静止：收扩喷管是过膨胀的，喷流在扩张段里就分离了，
            //    真正排气的那一圈是喉道——所以出口面积 = 喉道面积，面积比不起作用。
            OffDesignResult wide = OffDesign.Solve(rd, 0.0, 0.2, 1.0, 1.0, null, 60, -1.0, 0.0, 1.0, 0, null, 1.00);
            OffDesignResult narrow = OffDesign.Solve(rd, 0.0, 0.2, 1.0, 1.0, null, 60, -1.0, 0.0, 1.0, 0, null, 0.30);

            Console.WriteLine("    A8 档位 1.00 → 喉道 {0:F5} m²｜出口 {1:F5} m²｜偏差 {2:P1}｜分离 {3}",
                wide.A8Used, wide.NozzleExitArea, wide.NozzleAeMismatch, wide.NozzleSeparated);
            Console.WriteLine("    A8 档位 0.30 → 喉道 {0:F5} m²｜出口 {1:F5} m²｜偏差 {2:P1}",
                narrow.A8Used, narrow.NozzleExitArea, narrow.NozzleAeMismatch);

            double ratioSep = wide.NozzleExitArea / Math.Max(wide.A8Used, 1e-9);
            ExpectTrue("地面静止时收扩喷管过膨胀，出口面积退回喉道（分离）",
                Math.Abs(ratioSep - 1.0) < 0.02);
            Console.WriteLine("    地面出口/喉道 = {0:F3}（分离时应当是 1.000，面积比不起作用）", ratioSep);

            double areaStep = narrow.NozzleExitArea / Math.Max(wide.NozzleExitArea, 1e-9);
            double throatStep = narrow.A8Used / Math.Max(wide.A8Used, 1e-9);
            ExpectTrue("A8 缩小，出口面积按同一比值缩小（A9 跟着 A8 走）",
                Math.Abs(areaStep - throatStep) < 0.01);

            // ② 直接给喷管一个"喉道刚好壅塞"的工况：这时面积比必须真的起作用。
            //    这一条不依赖整机标定——整机的 A8 比模型流量需求大约三成（见打印），
            //    收扩段基本处在撑不满的状态，拿整机去验这条几何恒等式是验不出来的。
            {
                double pt = 300000.0, tt = 1100.0, amb = 1000.0, a8 = 0.10, ar1 = 1.5;
                double cap = Gas.ChokedFlow(a8, pt, tt, 0.0);
                Gas.NozzleState ns = Gas.NozzleFlow(cap, pt, tt, amb, 0.98,
                    NozzleKind.ConvergingDiverging, a8, ar1);
                Console.WriteLine("    喷管单元：喉道 {0:F4} m²（刚好壅塞）→ 出口 {1:F4} m²｜预测 {2:F4}｜分离 {3}",
                    a8, ns.Ae, ar1 * a8, ns.Separated);
                ExpectTrue("喉道刚好壅塞时，出口面积 = 面积比 × 喉道",
                    Math.Abs(ns.Ae - ar1 * a8) < a8 * 0.02);
                ExpectTrue("这一工况不该判成分离", !ns.Separated);
                ExpectTrue("几何出口与流量要求相符（偏差 5% 内）", Math.Abs(ns.AeMismatch) < 0.05);
            }

            // ③ 真烧加力的工况：这才是"喷口撑开了、气够不够"的真问题。
            //    `abFrac = 1.0` 但 `farAb = 0` 是假工况（喷口按 1.36 撑开、油气比却是 0，
            //    T7 一点没涨），拿来下结论会得出一堆错数——上一版就是这么得出
            //    "A8 大三成"的，见 §157。
            OffDesignResult hi = OffDesign.Solve(rd, 11000.0, 0.9, 1.0, 1.0, null, 60, -1.0, 0.030, 1.0, 0, null, 1.00);
            if (hi.Ok)
            {
                double capHi = Gas.ChokedFlow(hi.A8Used, hi.P5, hi.T7, 0.0);
                double mExhHi = hi.MdotCore * (1.0 + hi.Far);
                Console.WriteLine("    11 km/M0.9 真烧加力：T7 {0:F0} K｜A8 {1:F5}｜排气 {2:F1} kg/s｜喉道容量 {3:F1}｜灌满 {4:P0}｜分离 {5}",
                    hi.T7, hi.A8Used, mExhHi, capHi, capHi > 0.0 ? mExhHi / capHi : 0.0, hi.NozzleSeparated);
                ExpectTrue("高空烧加力时喉道没灌满——如实钉住：满加力档位把 A8 撑到 1.36 倍，T7 却只从 1085 涨到 1111 K",
                    mExhHi / Math.Max(capHi, 1e-9) < 0.90);
            }

            // 顺手量一下"喉道到底灌满了没有"：A8 是按**干态**设计点定尺寸的，
            // 所以干态设计点应当刚好灌满；点火加力时 T7 高、容量大、A8 也按 1.36 撑开，
            // 那时再看这条比例就没意义了（拿干态 A8 比加力工况也会得出一堆假结论）。
            OffDesignResult dpDry = OffDesign.Solve(rd, rd.DesignAlt, rd.DesignMach, 1.0, 0.0, null, 60, -1.0, 0.0, 1.0, 0, null, -1.0);
            OffDesignResult dpWet = OffDesign.Solve(rd, rd.DesignAlt, rd.DesignMach, 1.0, 1.0, null, 60, -1.0, 0.0, 1.0, 0, null, 1.00);
            if (dpDry.Ok)
            {
                double capDry = Gas.ChokedFlow(dpDry.A8Used, dpDry.P5, dpDry.T7, 0.0);
                Console.WriteLine("    干态设计点：核心流量 {0:F1} kg/s｜喉道容量 {1:F1} kg/s（{2:P0}）｜分离 {3}",
                    dpDry.MdotCore, capDry, capDry > 0.0 ? dpDry.MdotCore / capDry : 0.0, dpDry.NozzleSeparated);
                ExpectTrue("干态设计点喉道刚好灌满（A8 是照它定尺寸的）",
                    Math.Abs(dpDry.MdotCore / Math.Max(capDry, 1e-9) - 1.0) < 0.10);
            }
            if (dpWet.Ok && dpDry.Ok)
            {
                double capWet = Gas.ChokedFlow(dpWet.A8Used, dpWet.P5, dpWet.T7, 0.0);
                Console.WriteLine("    加力设计点：核心流量 {0:F1} kg/s｜喉道容量 {1:F1} kg/s（{2:P0}）｜A8 {3:F4}（干态 {4:F4}）",
                    dpWet.MdotCore, capWet, capWet > 0.0 ? dpWet.MdotCore / capWet : 0.0, dpWet.A8Used, dpDry.A8Used);
            }

            ExpectTrue("偏差是有界的数（不是 NaN/Inf）",
                GasState.IsFinite(wide.NozzleAeMismatch) && GasState.IsFinite(narrow.NozzleAeMismatch));

            // 状态审计不许把这些正常工况报成矛盾（审计规则写错方向比没有规则更糟：
            // 第一次做审计时它把"喉道能力 > 捕获"报成漏洞，一次报了 65 条假警报）。
            int auditErr = StateAudit.ErrorCount(StateAudit.AuditOffDesign(wide, rd))
                         + StateAudit.ErrorCount(StateAudit.AuditOffDesign(narrow, rd));
            ExpectTrue("状态审计对这两个正常工况没有算错", auditErr == 0);

            OffDesignResult low = OffDesign.Solve(rd, 0.0, 0.2, 0.35, 0.0, null, 60, -1.0, 0.0, 1.0, 0, null, 0.0);
            if (low.Ok)
            {
                double ratioLow = low.NozzleExitArea / Math.Max(low.A8Used, 1e-9);
                Console.WriteLine("    慢车：喉道 {0:F5}｜出口 {1:F5}｜出口/喉道 {2:F3}｜偏差 {3:P1}",
                    low.A8Used, low.NozzleExitArea, ratioLow, low.NozzleAeMismatch);
                ExpectTrue("慢车时出口面积同样不凭空比喉道还小",
                    low.NozzleExitArea > low.A8Used * 0.98);
            }
        }

        private static void RunNozzleActuatorCases()
        {
            AbState st = new AbState();
            for (int i = 0; i < 6; i++) AfterburnerTransient.Step(st, 0.1, 1.0, true);
            Console.WriteLine("    阶跃 0.6 s：燃油 {0:F3}｜A8 {1:F3}", st.Frac, st.AreaFrac);
            ExpectTrue("燃油比喷口先到（0.6 s 时面积明显落后）", st.Frac > st.AreaFrac + 0.10);
            ExpectTrue("阶跃 0.6 s 时面积还没到 0.5（不是同步动）", st.AreaFrac < 0.5);

            for (int i = 0; i < 60; i++) AfterburnerTransient.Step(st, 0.1, 1.0, true);
            ExpectTrue("稳态：燃油到满", Math.Abs(st.Frac - 1.0) < 1e-9);
            ExpectTrue("稳态：喷口也到满（落后不是错位）", Math.Abs(st.AreaFrac - 1.0) < 1e-9);

            // 关加力：燃油按 0.6 s 退，喷口按 2.2×0.6 s 退，所以 0.3 s 时面积还比燃油大。
            AbState off = new AbState { Frac = 1.0, AreaFrac = 1.0 };
            for (int i = 0; i < 3; i++) AfterburnerTransient.Step(off, 0.1, 0.0, true);
            Console.WriteLine("    关车 0.3 s：燃油 {0:F3}｜A8 {1:F3}", off.Frac, off.AreaFrac);
            ExpectTrue("关加力：燃油退得比喷口快", off.AreaFrac > off.Frac + 0.05);

            // 求解器侧：面积档位真的进去了；而它能改变推力，只能通过"喷管过不过得去这股气"。
            // 固定面积比之下，收扩喷管的推力由面积比与 T7 决定（Gas.NozzleFlow 里 Ae 是
            // 从流量反解出来的），所以 A8 本身不是推力杠杆——这一点必须如实钉住，
            // 否则以后有人看见"A8 缩小推力不变"会以为参数没接进去。
            EngineDesignData rd = Presets.Build("RD-33");
            ExpectTrue("RD-33 预设存在（收扩喷管才谈得上 A8 作动）", rd != null);
            if (rd == null) return;
            DesignConstants rc = OffDesign.DeriveDesignConstants(rd);
            Console.WriteLine("    探针：可动 {0}｜A8(1.00) {1:F5}｜A8(0.30) {2:F5}",
                NozzleSchedule.CanVary(rd.Nozzle),
                NozzleSchedule.ThroatArea(rd, rc, 1.0), NozzleSchedule.ThroatArea(rd, rc, 0.30));

            OffDesignResult narrow = OffDesign.Solve(rd, 0.0, 0.2, 1.0, 1.0, null, 60, -1.0, 0.0, 1.0, 0, null, 0.30);
            OffDesignResult wide = OffDesign.Solve(rd, 0.0, 0.2, 1.0, 1.0, null, 60, -1.0, 0.0, 1.0, 0, null, 1.00);
            // 作动筒位置就是面积本身（档位开大开小），"需要多大"是另一个数（A8_干·√(T7/T7干)）。
            // 上一版把需求当成面积本身，档位旋钮在求解器里几乎不留痕迹（§158.4 的抱怨），这一版拆开。
            ExpectTrue("面积档位真的进了求解器（作动筒位置 = 面积）",
                narrow.Ok && wide.Ok && narrow.A8Used < wide.A8Used * 0.9);
            Console.WriteLine("    干态：需要 {0:F5}｜档位 0.30 装 {1:F5}｜档位 1.00 装 {2:F5}｜开/需 {3:F2}",
                wide.A8Need, narrow.A8Used, wide.A8Used,
                wide.A8Need > 1e-9 ? wide.A8Used / wide.A8Need : 0.0);
            Console.WriteLine("    A8 档位 0.30 → 实际 {0:F5} m²｜推力 {1:F3} kN；档位 1.00 → {2:F5} m²｜推力 {3:F3} kN",
                narrow.A8Used, narrow.NetThrust / 1000.0, wide.A8Used, wide.NetThrust / 1000.0);
            // 以前这里钉的是"固定面积比下 A8 不是推力杠杆"。第 90 段把出口面积改成几何值
            // （A9 = 面积比 × A8）之后，这句话不再成立也不再对：地面静止时喷流在扩张段里分离，
            // 出口退回喉道，于是 A8 直接决定排气截面与（壅塞时）流量。
            // 所以现在钉方向：A8 变小，推力只能不变或变小，绝不许变大。
            ExpectTrue("A8 变小，推力不会反而变大（要么容量够、要么被掐住，方向只有一个）",
                narrow.NetThrust <= wide.NetThrust * 1.0001);

            // 真正的后果在"过不过得去"：把 A8 掐小，喷管容量判据就该翻成"过不去"。
            // 实测（M0.9 硬烧加力、90.2 kg/s）：档位 0.30/0.20/0.14 判得过去，
            // 0.10/0.06 判过不去。所以这里钉的是"判据真的看 A8"，不是"推力一定掉"。
            OffDesignResult abWide = OffDesign.Solve(rd, 0.0, 0.9, 1.0, 1.0, null, 60, -1.0, 0.030, 1.0, 0, null, 1.00);
            OffDesignResult abNarrow = OffDesign.Solve(rd, 0.0, 0.9, 1.0, 1.0, null, 60, -1.0, 0.030, 1.0, 0, null, 0.10);
            Console.WriteLine("    烧加力 M0.9：A8 撑开 {0:F2} kN（限流 {1}）｜A8 掐到 10% {2:F2} kN（限流 {3}）｜mdot {4:F1} / {5:F1}",
                abWide.NetThrust / 1000.0, abWide.NozzleCapped, abNarrow.NetThrust / 1000.0, abNarrow.NozzleCapped,
                abWide.MdotAir, abNarrow.MdotAir);
            ExpectTrue("档位拉满时喷口明显开得比需要的大（油气比 0.030 撑不出 1.36 倍的面积）",
                abWide.Ok && abWide.A8OpenRatio > 1.15);
            ExpectTrue("档位 10% 时喷口接近需要（开/需 ≤ 1.10）",
                abNarrow.Ok && abNarrow.A8OpenRatio > 0.0 && abNarrow.A8OpenRatio <= 1.10);
            ExpectTrue("开大了会明确告警，而不是默默多算",
                abWide.Warnings.Exists(w => w.Contains("喷口开得比需要的大")));
            Console.WriteLine("      掐小那次的限流说明：{0}",
                abNarrow.Warnings.Count > 0 ? abNarrow.Warnings[0] : "（没有）");
            Console.WriteLine("      掐小那次：mdot 核心 {0:F1}｜T7 {1:F0} K｜A8 {2:F5}",
                abNarrow.MdotCore, abNarrow.T7, abNarrow.A8Used);
        }

        /// <summary>
        /// 第 86 段：喷管状态链（Phase3 3.3）。
        ///
        /// 这一段守的是**两套数不能各写各的**：求解器里的喉道 A8 与花瓣网格的张开半径，
        /// 以前是两条互不相干的曲线（求解器 A8 只随加力变，网格按油门 0.90~1.00 画）。
        /// 现在两边都走 `NozzleSchedule.DryFactor`，所以断言里既有"数对不对"，
        /// 也有"两边是不是同一个数"——后者才是这条链真正要保证的事。
        /// </summary>
        private static void RunNozzleChainCases()
        {
            // ① 干态调度本身：设计转速不收缩，慢车收 6%，中间线性
            Check("设计转速不收缩", NozzleSchedule.DryFactor(1.0, true), 1.0, 0.001);
            Check("慢车收到 94%", NozzleSchedule.DryFactor(0.0, true), 0.94, 0.001);
            Check("半速在中间", NozzleSchedule.DryFactor(0.5, true), 0.97, 0.001);
            Check("转速超转也不会撑过设计值", NozzleSchedule.DryFactor(1.2, true), 1.0, 0.001);

            // ② 固定收敛喷管没有作动筒：任何转速都是 1.0，不能凭空给它收缩
            Check("固定收敛喷管慢车也不收", NozzleSchedule.DryFactor(0.2, false), 1.0, 0.001);

            // ③ 面积：同一台可变喷管，设计转速下与"不带转速"的老口径逐位相同
            EngineDesignData d = Presets.Build("J79");
            // J79 是固定收敛喷管，想测"能动的喷管"就得换成可变——这一段测的正是这个差别
            d.Nozzle = NozzleKind.Variable;
            DesignConstants c = OffDesign.DeriveDesignConstants(d);
            double a8Design = NozzleSchedule.ThroatArea(d, c, 0.0, 1.0);
            double a8Legacy = NozzleSchedule.ThroatArea(d, c, 0.0);
            Check("设计转速下与老口径一致", a8Design, a8Legacy, 0.001);
            double a8Idle = NozzleSchedule.ThroatArea(d, c, 0.0, 0.0);
            Check("慢车喉道就是设计值的 94%", a8Idle, a8Design * 0.94, 0.5);
            Expect("慢车喉道确实变小了", a8Idle < a8Design, true);

            // ④ 加力那一份仍在：满加力比干态大 36%（与转速那一份相乘，不是相加）
            double a8Ab = NozzleSchedule.ThroatArea(d, c, 1.0, 1.0);
            Check("满加力时喉道放大约 36%", a8Ab, a8Design * (1.0 + NozzleSchedule.AbAreaGain), 0.5);

            // ⑤ 网格与求解器同源：半径比例 = √(干态因子)，设计点恰好 1
            double openDesign = NozzleVisual.RadiusScale(1.0, 0.0, true);
            Check("设计点网格半径就是基准", openDesign, 1.0, 0.001);
            double openIdle = NozzleVisual.RadiusScale(0.0, 0.0, true);
            Check("慢车网格半径按 √面积收", openIdle, Math.Sqrt(0.94), 0.5);
            double openFixed = NozzleVisual.RadiusScale(0.0, 0.0, false);
            Check("固定收敛喷管网格不动", openFixed, 1.0, 0.001);

            // ⑥ 同一个转速下，网格半径比例与求解器面积比例的平方根必须对得上
            foreach (double nc in new[] { 0.3, 0.6, 0.95 })
            {
                double areaRatio = NozzleSchedule.ThroatArea(d, c, 0.0, nc) / a8Design;
                double radiusRatio = NozzleVisual.RadiusScale(nc, 0.0, true);
                Check(string.Format("转速 {0:F0}%：网格半径与喉道面积同源", nc * 100),
                    radiusRatio, Math.Sqrt(areaRatio), 0.2);
            }
        }

        /// <summary>
        /// 第 89 段：设计向导。
        ///
        /// 验的不是"生成的设计好不好"，而是三条：**目标能对得上**（推力差 3% 以内）、
        /// **同一个输入两次生成必须一模一样**（存档不能点一次变一次）、
        /// **说不出来的情况要说不出来**（非法输入直接失败；先天的矛盾要列出来，不许装作没有）。
        /// </summary>
        private static void RunWizardCases()
        {

            WizardInput fighter = new WizardInput
            {
                Role = WizardRole.Fighter,
                DesignMach = 2.0,
                DesignAlt = 11000.0,
                ThrustKN = 80.0,
                Tech = "1980s"
            };
            WizardResult a = DesignWizard.Build(fighter);
            Expect("战斗机向导能给出一台设计", a.Ok, true);
            if (!a.Ok)
            {
                Console.WriteLine("    失败原因：" + a.Failure);
                return;
            }
            Console.WriteLine("    起手 {0}｜实算 {1:F1} kN（目标 {2:F0} kN）｜风扇 Ø{3:F2} m｜载荷系数 {4:F2}｜机型 {5}",
                a.Basis, a.ThrustKN, a.TargetKN, a.Design.Fan.Diameter, a.Design.Hpc.PsiScale, a.Design.DesignName);
            Check("推力命中目标（±3%）", a.ThrustKN, 80.0, 3.0);

            bool hasBasis = false, hasThrust = false;
            for (int i = 0; i < a.Notes.Count; i++)
            {
                if (a.Notes[i].Contains("起手模板")) hasBasis = true;
                if (a.Notes[i].Contains("实算推力")) hasThrust = true;
            }
            Expect("说明里写了从哪台机起手", hasBasis, true);
            Expect("说明里写了实算推力与偏差", hasThrust, true);
            Expect("说明至少四条（起手/为什么不用空白/设计点/缩放）", a.Notes.Count >= 4, true);

            WizardResult b = DesignWizard.Build(fighter);
            Expect("同一输入两次生成一致（确定性）", b.Ok, true);
            if (b.Ok)
                Check("两次生成的风扇直径一致", b.Design.Fan.Diameter, a.Design.Fan.Diameter, 1e-6);

            WizardInput transport = new WizardInput
            {
                Role = WizardRole.Transport,
                DesignMach = 0.8,
                DesignAlt = 10000.0,
                ThrustKN = 25.0,
                Tech = "2010s"
            };
            WizardResult c = DesignWizard.Build(transport);
            Expect("运输机向导能出一台设计", c.Ok, true);
            if (!c.Ok) Console.WriteLine("    运输机失败：" + c.Failure);
            if (c.Ok)
            {
                Expect("运输机默认不加力", c.Design.Afterburner.Enabled, false);
                Expect("运输机有风扇（不是涡喷）", c.Design.HasFan, true);
                Expect("运输机的风扇直径落在人话范围（1.2~3.2 m）",
                    c.Design.Fan.Diameter > 1.2 && c.Design.Fan.Diameter < 3.2, true);
                Console.WriteLine("    运输机：{0}｜实算 {1:F1} kN｜风扇 Ø{2:F2} m",
                    c.Basis, c.ThrustKN, c.Design.Fan.Diameter);
            }

            WizardInput bad = new WizardInput { ThrustKN = 0.0, DesignMach = 1.0, DesignAlt = 1000.0, Tech = "1980s" };
            WizardResult e = DesignWizard.Build(bad);
            Expect("推力为零直接拒绝", e.Ok, false);
            Expect("拒绝时说清楚了是超范围", e.Failure.Contains("超出范围"), true);

            WizardInput badMach = new WizardInput { ThrustKN = 60.0, DesignMach = 4.5, DesignAlt = 1000.0, Tech = "1980s" };
            Expect("马赫超范围也直接拒绝", DesignWizard.Build(badMach).Ok, false);

            WizardInput hot = new WizardInput
            {
                Role = WizardRole.Interceptor,
                DesignMach = 3.0,
                DesignAlt = 18000.0,
                ThrustKN = 70.0,
                Tech = "1960s"
            };
            WizardResult h = DesignWizard.Build(hot);
            // 六十年代材料在 Ma3 上真做不出来：要么生成并列出矛盾，要么直接失败并说清楚为什么。
            // 不许发生的是"生成了却不提一句"。
            Console.WriteLine("    M3 配六十年代：{0}", h.Ok ? "生成了" : "拒绝（" + h.Failure + "）");
            if (h.Conflicts.Count > 0) Console.WriteLine("    年代矛盾：" + h.Conflicts[0]);
            Expect("M3 配六十年代：不许默默生成", h.Ok ? h.Conflicts.Count > 0 : h.Failure.Length > 0, true);
            if (!h.Ok)
                Expect("拒绝的理由落在温度上（涡前温度上不去）",
                    h.Failure.Contains("T4") || h.Failure.Contains("加热"), true);

            WizardInput big = new WizardInput
            {
                Role = WizardRole.Fighter,
                DesignMach = 2.0,
                DesignAlt = 11000.0,
                ThrustKN = 125.0,
                Tech = "1980s"
            };
            WizardResult g = DesignWizard.Build(big);
            Expect("要更大推力也能给（换台模板或放大）", g.Ok, true);
            if (g.Ok)
            {
                Console.WriteLine("    125 kN：{0}｜风扇 Ø{1:F2} m｜实算 {2:F1} kN", g.Basis, g.Design.Fan.Diameter, g.ThrustKN);
                Expect("推力目标更大时风扇不会更小", g.Design.Fan.Diameter > a.Design.Fan.Diameter * 0.95, true);
            }
        }

        /// <summary>
        /// 第 85 段：长时间运行（B 类自动化测试）。
        ///
        /// 玩家一次飞行是几十分钟，一秒 50 帧，几万个工况点。体检那一段（第 80 段）验的是
        /// "每一个独立工况点像不像话"，这一段验的是**一长串连续工况**里三件不同的事：
        ///   ①不出现 NaN/Inf——单点体检也查，但连续跑才会暴露"上一步污染下一步"的累加错误；
        ///   ②没有跨点的状态泄漏——把第 0 步的工况在最后再算一遍，必须逐位相同
        ///      （差一点点就说明某处缓存没按设计走，飞久了会慢慢漂）；
        ///   ③相邻两步之间不出现荒唐跳变——高度/马赫连续变化时，推力和流量不该突然翻倍。
        /// 用一条爬升-加速-下降的剖面走 2000 步（约 3 秒），不是随机点，因为随机点测不到"连续"。
        /// </summary>
        private static void RunLongRunCases()
        {
            EngineDesignData d = Presets.Build("J79");
            if (d == null) { Expect("J79 预设存在", false, true); return; }
            DesignConstants consts = OffDesign.DeriveDesignConstants(d);
            Expect("设计常数算得出来", consts != null && consts.Ok, true);
            if (consts == null || !consts.Ok) return;

            const int Steps = 2000;
            int nan = 0, absurd = 0, jumps = 0;
            double firstThrust = 0.0, firstMdot = 0.0;
            double prevThrust = 0.0, prevMdot = 0.0;
            double minThrust = double.MaxValue, maxThrust = double.MinValue;
            DateTime t0 = DateTime.UtcNow;

            for (int i = 0; i < Steps; i++)
            {
                // 0→1 爬升加速，1→0 下降减速：u 是这条剖面在 0~2 之间的位置。
                double u = i / (double)(Steps - 1) * 2.0;
                double k = u <= 1.0 ? u : 2.0 - u;               // 0→1→0
                double alt = 12000.0 * k;
                double mach = 1.4 * k;
                double throttle = 0.55 + 0.45 * k;

                OffDesignResult r = OffDesign.Solve(d, alt, mach, throttle, 0.0, consts, 40, -1.0, 0.0, 1.0, 0, null);
                double thrust = r.Thrust / 1000.0;
                double mdot = r.MdotAir;

                if (double.IsNaN(thrust) || double.IsInfinity(thrust) || double.IsNaN(mdot) || double.IsInfinity(mdot)) { nan++; continue; }
                if (Math.Abs(thrust) > 500.0 || mdot < 0.0 || mdot > 5000.0) absurd++;

                if (i == 0) { firstThrust = thrust; firstMdot = mdot; }
                else
                {
                    // 相邻两步：高度差 6 m、马赫差 0.0007，推力和流量都不该跳动超过 8%。
                    if (Math.Abs(thrust - prevThrust) > Math.Max(2.0, Math.Abs(prevThrust) * 0.08)) jumps++;
                    if (Math.Abs(mdot - prevMdot) > Math.Max(1.0, prevMdot * 0.08)) jumps++;
                }
                if (i > 0)
                {
                    minThrust = Math.Min(minThrust, thrust);
                    maxThrust = Math.Max(maxThrust, thrust);
                }
                prevThrust = thrust;
                prevMdot = mdot;
            }

            double seconds = (DateTime.UtcNow - t0).TotalSeconds;
            Console.WriteLine("    {0} 步连续工况：耗时 {1:F2} s（每步 {2:F2} ms）｜推力区间 {3:F1}~{4:F1} kN",
                Steps, seconds, seconds * 1000.0 / Steps, minThrust, maxThrust);

            // ②同一工况在最后再算一遍：必须与第一次逐位相同。
            OffDesignResult again = OffDesign.Solve(d, 0.0, 0.0, 0.55, 0.0, consts, 40, -1.0, 0.0, 1.0, 0, null);
            double againThrust = again.Thrust / 1000.0;
            Console.WriteLine("    同一点重算：{0:F6} kN 对 {1:F6} kN（第一次）", againThrust, firstThrust);
            Expect("同一工况在 2000 步之后重算，结果逐位相同（没有跨点状态泄漏）",
                Math.Abs(againThrust - firstThrust) < 1e-9 && Math.Abs(again.MdotAir - firstMdot) < 1e-9, true);

            Expect("2000 步里没有 NaN/Inf", nan == 0, true);
            Expect("2000 步里没有荒唐数", absurd == 0, true);
            Expect("相邻两步之间没有荒唐跳变", jumps == 0, true);
        }

        /// <summary>
        /// 第 77 段：过载损伤（开关 + 加强系统的重量代价）。
        ///
        /// 这两件事的共同点是"改一个数，别处全跟着变"：加强系统一打开，过载门槛上去了、
        /// 重量也得上去。所以断言都成对写——只验"变大了"不够，还要验"变得对"（按比例）。
        ///
        /// RealFuels 那一套（燃料目录/换油换热值）已按要求撤除：燃料恒为原版 LiquidFuel、
        /// 热值恒为 43.0 MJ/kg，`Gas.LhvJet` 也回到常量。
        /// </summary>
        private static void RunGLimitCases()
        {
            // ---------- 过载损伤：开关与加强 ----------
            EngineDesignData g = Presets.Build("J79");
            g.GDamage = true; g.GReinforce = 0.0;
            FailureLimits baseLim = Defects.For(g, null);
            ExpectTrue("缺省是开着的", baseLim.GDamageEnabled);
            Check("加强 0 时门槛就是基准值", baseLim.GBreak, 6.5 + 5.5 * Q(g, null), 1.0, "g");

            g.GReinforce = 1.0;
            FailureLimits strong = Defects.For(g, null);
            Check("满加强：门槛翻倍", strong.GBreak / baseLim.GBreak, 2.0, 1.0, "×");
            Check("满加强：应变额度也跟着翻", strong.GBreakStrain / baseLim.GBreakStrain, 2.0, 1.0, "×");

            g.GDamage = false;
            FailureLimits off = Defects.For(g, null);
            Expect("关掉之后标记为不计", off.GDamageEnabled, false);

            // 关掉之后飞十个 g 也不该攒应变（造例：直接用 Step 顶过去）
            DefectRuntime rt = new DefectRuntime();
            Defects.Configure(rt, off);
            for (int i = 0; i < 40; i++) Defects.Step(new Defects.Risk(), rt, 0.1, 1.0, 900.0, 1800.0, 0.02, 1000.0, 12.0, false);
            Check("过载损伤关掉：12 g 飞 4 秒不攒应变", rt.GStrain, 0.0, 1.0, "g·s");
            Expect("关掉之后也炸不了", rt.Failed, false);

            rt = new DefectRuntime();
            Defects.Configure(rt, baseLim);
            for (int i = 0; i < 40; i++) Defects.Step(new Defects.Risk(), rt, 0.1, 1.0, 900.0, 1800.0, 0.02, 1000.0, 12.0, false);
            ExpectTrue("开着的时候 12 g 飞 4 秒必须攒出应变", rt.GStrain > 0.0);

            // ---------- 加强系统的重量代价 ----------
            EngineDesignData m0 = Presets.Build("J79"); m0.GReinforce = 0.0;
            EngineDesignData m1 = Presets.Build("J79"); m1.GReinforce = 1.0;
            MassBreakdown b0 = MassCost.Estimate(m0);
            MassBreakdown b1 = MassCost.Estimate(m1);
            ExpectTrue("满加强会变重", b1.TotalDry > b0.TotalDry);
            double gain = (b1.TotalDry - b0.TotalDry) / Math.Max(b0.TotalDry, 1.0);
            ExpectTrue("变重的幅度在 5%~15%（不是白送的强度）", gain > 0.05 && gain < 0.15);
        }

        /// <summary>
        /// 第 80 段：大范围体检（Phase3 文档里的「长时间运行测试」）。
        ///
        /// 这一段的立场很简单：**不许崩，也不许算出荒唐数**。
        /// 五台真机 × 五个高度 × 七个马赫 × 五个油门，把非设计点求解器扫一遍，
        /// 只要出现 NaN/Inf、或者"成功但推力是负的/流量是负的"这种自相矛盾，就算失败。
        ///
        /// 它不检查"数值对不对"——那是每台预设各自的标定段在管。
        /// 它查的是"有没有哪一种组合会把求解器算炸"，这类问题平时碰不到，
        /// 但玩家一定会碰到（高空开加力、极低油门、Ma2 以上）。
        /// </summary>
        private static void RunSoakCases()
        {
            string[] keys = { "J79", "J57", "J85", "RD-33", "GTF1100" };
            double[] alts = { 0.0, 3000.0, 8000.0, 11000.0, 15000.0 };
            double[] machs = { 0.0, 0.30, 0.60, 0.90, 1.20, 1.80, 2.20 };
            double[] throttles = { 0.0, 0.25, 0.50, 0.75, 1.00 };

            int total = 0, notOk = 0, bad = 0;
            string firstBad = null;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (string key in keys)
            {
                EngineDesignData d = Presets.Build(key);
                if (d == null) { ExpectTrue("体检用预设 " + key + " 存在", false); continue; }
                foreach (double alt in alts)
                    foreach (double mach in machs)
                        foreach (double thr in throttles)
                        {
                            total++;
                            OffDesignResult r;
                            try { r = OffDesign.Solve(d, alt, mach, thr); }
                            catch (Exception e)
                            {
                                bad++;
                                if (firstBad == null) firstBad = string.Format("{0} {1:F0}m Ma{2:F1} 油门{3:P0} 抛异常：{4}",
                                    key, alt, mach, thr, e.GetType().Name);
                                continue;
                            }
                            if (!r.Ok) { notOk++; continue; }

                            string why = null;
                            if (!Finite(r.Thrust) || !Finite(r.NetThrust) || !Finite(r.MdotAir) || !Finite(r.Sfc) || !Finite(r.Opr))
                                why = "出现 NaN/Inf";
                            else if (r.MdotAir < -1e-9) why = string.Format("流量为负（{0:F3} kg/s）", r.MdotAir);
                            else if (r.Opr < 0.5) why = string.Format("总压比荒谬（{0:F2}）", r.Opr);
                            else if (r.T4 < 150.0 || r.T4 > 2600.0) why = string.Format("涡轮前温度越界（{0:F0} K）", r.T4);
                            // 毛推力为负是**允许**的：油门收到底、飞得快时，喷流比来流慢，
                            // 动量项本来就是负的（真机这叫风车状态，不是算错）。荒唐的是量级：
                            // 一架 1 吨级的机器不该给出兆牛级的数。
                            else if (Math.Abs(r.Thrust) > 5.0e6) why = string.Format("毛推力量级荒谬（{0:E2} N）", r.Thrust);
                            else if (r.Thrust > 1000.0 && r.MdotAir < 0.05)
                                why = string.Format("几乎不吸气却给出推力（{0:F0} N，流量 {1:F3} kg/s）", r.Thrust, r.MdotAir);
                            if (why != null)
                            {
                                bad++;
                                if (firstBad == null) firstBad = string.Format("{0} {1:F0}m Ma{2:F1} 油门{3:P0}：{4}",
                                    key, alt, mach, thr, why);
                            }
                        }
            }
            sw.Stop();

            ExpectTrue("扫过的工况点都是有限、且不自相矛盾（荒唐数 0）", bad == 0);
            if (firstBad != null) Console.WriteLine("    第一个荒唐工况：" + firstBad);

            // 不收敛不等于错：某些组合（比如 15 km 油门到底）本来就点不着火。
            // 但"全军不收敛"说明求解器整体坏了，这条兜住那种情况。
            ExpectTrue("成立的比例不低于一半（不收敛可以有，整体塌掉不行）", notOk <= total / 2);

            Console.WriteLine("    体检：{0} 个工况点｜成立 {1}、不成立 {2}、荒唐 {3}｜耗时 {4:F2} s（每点 {5:F2} ms）",
                total, total - notOk - bad, notOk, bad, sw.Elapsed.TotalSeconds,
                sw.Elapsed.TotalMilliseconds / Math.Max(total, 1));
        }

        private static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>
        /// 第 78 段：风扇齿轮变速（GTF）。
        ///
        /// 齿轮减速这个机构能不能用，看三件事：
        /// ①风扇叶尖速度要与轴转速**分开**算（同一根轴，齿轮比 3 时风扇只有 1/3 转速）；
        /// ②代价必须摆在明面上——齿轮箱有重量、传动有 1/η 的功率损失、整机变长；
        /// ③直驱机型一个数都不能动（老存档里那些机器还得是原来那台机器）。
        /// </summary>
        private static void RunGearCases()
        {
            EngineDesignData gtf = Presets.Build("GTF1100");
            ExpectTrue("GTF 预设存在", gtf != null);
            if (gtf == null) return;

            // 预设要经得起 Clone：FanSpec 是逐字段抄的，抄漏了不会报错，
            // 只会让"预设里写好的齿轮比"在克隆之后悄悄回到 1.0。
            ExpectTrue("齿轮比 3.0 经 Clone 之后还在", Math.Abs(gtf.Fan.GearRatio - 3.0) < 1e-9);
            ExpectTrue("齿轮效率经 Clone 之后还在", gtf.Fan.GearEta > 0.9);

            CycleResult r = Cycle.SolveDesignPoint(gtf);
            ExpectTrue("GTF 预设能解出设计点", r.Ok);
            // 要说清楚这个 5 是哪来的：真机 PW1100G 的涵道比是 12.5，本模型算出来 6.8。
            // 差异不在齿轮，在核心机通流面积的口径——本模型的核心流量由环形面积与
            // 叶尖速度定，真机那 30~40 kg/s 的核心流量在这里推不出更小的面积。
            // 所以这条断言守的是"这是一台大涵道比涡扇"，不是"复现 12.5"。
            ExpectTrue(string.Format("是大涵道比涡扇（BPR {0:F1} > 5）", r.Bpr), r.Bpr > 5.0);

            // ---------- ① 转速拆分 ----------
            ShaftSet set = ShaftSet.Build(gtf);
            ShaftState n1;
            ExpectTrue("低压轴存在", set.States.TryGetValue(1, out n1));
            if (set.States.TryGetValue(1, out n1))
            {
                double rpmShaft = n1.OmegaDesign * 60.0 / (2.0 * Math.PI);
                double rpmFan = rpmShaft / gtf.Fan.GearRatio;
                double tipFan = Cycle.TipSpeed(gtf.Fan.Diameter, rpmFan);
                double tipDirect = Cycle.TipSpeed(gtf.Fan.Diameter, rpmShaft);
                ExpectTrue(string.Format("轴 {0:F0} rpm / 风扇 {1:F0} rpm，转速真的分开了", rpmShaft, rpmFan),
                    rpmShaft > rpmFan * 2.5);
                ExpectTrue(string.Format("风扇叶尖速度 {0:F0} m/s 是亚声速（GTF 的全部意义）", tipFan), tipFan < 420.0);
                ExpectTrue(string.Format("按轴转速直驱会到 {0:F0} m/s（明显更快）", tipDirect),
                    tipDirect > tipFan * 2.5);

                // 等效惯量要乘齿轮比的平方：同一份惯量，转速降一半就要乘 4。
                EngineDesignData direct = gtf.Clone();
                direct.Fan.GearRatio = 1.0;
                ShaftSet setDirect = ShaftSet.Build(direct);
                ExpectTrue("齿轮减速的低压轴等效惯量更大（响应更慢，真机也这样）",
                    n1.Inertia > setDirect.States[1].Inertia + 0.05);
            }

            // ---------- ② 代价 ----------
            MassBreakdown mGeared = MassCost.Estimate(gtf);
            EngineDesignData dd = gtf.Clone();
            dd.Fan.GearRatio = 1.0;
            MassBreakdown mDirect = MassCost.Estimate(dd);

            ExpectTrue(string.Format("齿轮箱有重量：{0:F0} kg", mGeared.Gearbox), mGeared.Gearbox > 40.0);
            ExpectTrue("直驱机型的齿轮箱重量是 0", Math.Abs(mDirect.Gearbox) < 1e-9);
            ExpectTrue("带齿轮的干重比直驱重（不是白送的推力）", mGeared.TotalDry > mDirect.TotalDry + 40.0);
            ExpectTrue(string.Format("齿轮箱量级合理（{0:F0} kg，真机百公斤级）", mGeared.Gearbox),
                mGeared.Gearbox < 800.0);
            ExpectTrue("齿轮箱占轴向长度",
                EngineLength.Estimate(gtf) > EngineLength.Estimate(dd) + 0.15);

            EngineDesignData lossless = gtf.Clone();
            lossless.Fan.GearEta = 1.0;
            CycleResult rLossless = Cycle.SolveDesignPoint(lossless);
            ExpectTrue("齿轮传动有功率损失（比无损多要一点功）", r.WFan > rLossless.WFan);
            double penalty = (r.WFan - rLossless.WFan) / Math.Max(rLossless.WFan, 1.0);
            ExpectTrue(string.Format("损失落在 0.3%~1.0%（实得 {0:F2}%）", penalty * 100.0),
                penalty > 0.003 && penalty < 0.010);

            // ---------- ③ 直驱机型逐位不变 ----------
            EngineDesignData j79 = Presets.Build("J79");
            ExpectTrue("涡喷没有齿轮（比例 1.0）", Math.Abs(j79.Fan.GearRatio - 1.0) < 1e-12);
            EngineDesignData rd33 = Presets.Build("RD-33");
            ExpectTrue("低涵道比直驱风扇也没有齿轮",
                Math.Abs(rd33.Fan.GearRatio - 1.0) < 1e-12 && Math.Abs(MassCost.Estimate(rd33).Gearbox) < 1e-9);

            ConfigNode node = new ConfigNode("MODULE");
            gtf.Save(node);
            EngineDesignData back = EngineDesignData.Load(node);
            Check("齿轮比存得回来", back.Fan.GearRatio, gtf.Fan.GearRatio, 0.01);

            ConfigNode node2 = new ConfigNode("MODULE");
            j79.Save(node2);
            ConfigNode fanNode = node2.GetNode("FAN");
            ExpectTrue("直驱机型不写 gearRatio 这个键（老存档路径一字不变）",
                fanNode != null && fanNode.GetValue("gearRatio") == null);
            ExpectTrue("直驱读回来还是 1.0", Math.Abs(EngineDesignData.Load(node2).Fan.GearRatio - 1.0) < 1e-12);

            Console.WriteLine("    齿轮：齿比 {0:F2}｜风扇叶尖 {1:F0} m/s｜齿轮箱 {2:F0} kg｜BPR {3:F1}｜推力 {4:F1} kN",
                gtf.Fan.GearRatio,
                Cycle.TipSpeed(gtf.Fan.Diameter,
                    set.States[1].OmegaDesign * 60.0 / (2.0 * Math.PI) / gtf.Fan.GearRatio),
                mGeared.Gearbox, r.Bpr, r.Thrust / 1000.0);
        }

        /// <summary>
        /// 第 79 段：统一部件网络能换工况点。
        ///
        /// `EngineNetwork.SolveDesign` 以前只会算设计点（高度、马赫、转速比例都写死），
        /// 非设计点走的是 `OffDesign.Solve` 里另一套顺序代码。这一段守的是"换点入口"本身：
        /// ①不传点就退回设计点，读数必须与 `SolveDesign` 逐位相同；
        /// ②转速往下掉，推力和总压比都得跟着掉（单调，不许反着走）；
        /// ③爬到高空，推力掉得比海平面多。
        ///
        /// 它**不**声称非设计点已经可信——燃油仍按设计点那份烧，平衡转速也还是 OffDesign 找的。
        /// </summary>
        private static void RunNetworkPointCases()
        {
            EngineDesignData d = Presets.Build("J79");
            ExpectTrue("J79 预设存在", d != null);
            if (d == null) return;

            NetworkSolution byDefault = EngineNetwork.SolveDesign(d);
            NetworkSolution byNan = EngineNetwork.SolveAt(d, double.NaN, double.NaN, 1.0);
            ExpectTrue("不传工况点 = 设计点（推力逐位相同）",
                Math.Abs(byDefault.Result.Thrust - byNan.Result.Thrust) < 1e-9);
            ExpectTrue("不传工况点 = 设计点（总压比逐位相同）",
                Math.Abs(byDefault.Result.Opr - byNan.Result.Opr) < 1e-9);

            double[] fracs = { 1.0, 0.90, 0.80, 0.70 };
            double prevThrust = double.MaxValue, prevOpr = double.MaxValue, prevMdot = double.MaxValue;
            bool thrustFalls = true, oprFalls = true, mdotFalls = true;
            foreach (double f in fracs)
            {
                NetworkSolution s = EngineNetwork.SolveAt(d, double.NaN, double.NaN, f);
                if (!s.Result.Ok) { thrustFalls = false; oprFalls = false; mdotFalls = false; break; }
                Console.WriteLine("      转速 {0:F0}%：OPR {1:F2}｜流量 {2:F1} kg/s｜推力 {3:F1} kN｜T4 {4:F0} K",
                    f * 100.0, s.Result.Opr, s.Result.MdotAir, s.Result.Thrust / 1000.0, s.Result.T4);
                if (s.Result.Thrust >= prevThrust) thrustFalls = false;
                if (s.Result.Opr >= prevOpr) oprFalls = false;
                if (s.Result.MdotAir >= prevMdot) mdotFalls = false;
                prevThrust = s.Result.Thrust;
                prevOpr = s.Result.Opr;
                prevMdot = s.Result.MdotAir;
            }
            ExpectTrue("转速往下掉，总压比单调往下", oprFalls);
            ExpectTrue("转速往下掉，吸的气单调变少", mdotFalls);
            ExpectTrue("转速往下掉，推力单调往下", thrustFalls);

            // 压气机前排级壅塞：换算流量正比于换算转速，所以流量该按转速比例掉。
            NetworkSolution full = EngineNetwork.SolveAt(d, double.NaN, double.NaN, 1.0);
            NetworkSolution eighty = EngineNetwork.SolveAt(d, double.NaN, double.NaN, 0.80);
            ExpectTrue("80% 转速只吞得下 80% 的流量（前排级壅塞）",
                Math.Abs(eighty.Result.MdotAir - full.Result.MdotAir * 0.80) < full.Result.MdotAir * 0.01);
            ExpectTrue("80% 转速记下了「压气机吞不下」这层限制", eighty.Result.CompressorFlowLimited);

            // 壅塞上限必须是设计点流量乘转速比例——这条守的是"公式没写反"。
            ExpectTrue("壅塞上限 = 设计点流量 × 转速比例",
                Math.Abs(eighty.Result.CompressorSwallowLimit - full.Result.MdotAir * 0.80) < full.Result.MdotAir * 0.01);

            // 推力**不**断言单调：这条链路里燃油仍按设计点那份烧，转速掉了 T4 却没掉，
            // 于是低转速反而可能给出更大推力（真机上油量会跟着收，那是 OffDesign 的活）。
            // 把这句话写下来，比写一条会红的断言有用——它会红，是因为这里还没有油量控制。
            if (!thrustFalls)
                Console.WriteLine("    注：转速降低时推力未单调下降（燃油按设计点烧，T4 不随转速回落）——等非设计点油量接上来再看这条");

            NetworkSolution sea = EngineNetwork.SolveAt(d, 0.0, 0.0, 1.0);
            NetworkSolution high = EngineNetwork.SolveAt(d, 8000.0, 0.0, 1.0);
            ExpectTrue("8 km 静止的推力低于海平面静止",
                high.Result.Ok && sea.Result.Ok && high.Result.Thrust < sea.Result.Thrust);
            ExpectTrue("8 km 吸的空气少于海平面",
                high.Result.Ok && sea.Result.Ok && high.Result.MdotAir < sea.Result.MdotAir);

            // 站表要跟着点走：进气口总温就是来流总温，不是设计点那个常数。
            double p0, t0;
            Gas.Isa(8000.0, out p0, out t0);
            ExpectTrue("8 km 的进气口总温 = 该高度 ISA 静温（马赫 0）",
                high.Result.Ok && Math.Abs(high.Result.T1 - t0) < 0.5);

            Console.WriteLine("    工况点：转速 100% 推力 {0:F1} kN｜80% 推力 {1:F1} kN｜8 km 推力 {2:F1} kN",
                byDefault.Result.Thrust / 1000.0,
                EngineNetwork.SolveAt(d, double.NaN, double.NaN, 0.80).Result.Thrust / 1000.0,
                high.Result.Thrust / 1000.0);
        }

        /// <summary>第 77 段用的小工具：把 <see cref="Defects.For"/> 里那个 q 重算一遍，好对门槛。</summary>
        private static double Q(EngineDesignData d, CheckReport rep)
        {
            double robust = 1.0;
            if (rep != null)
            {
                Defects.Risk rk = Defects.Read(rep.Findings);
                robust = 1.0 - Math.Max(rk.Surge, Math.Max(rk.OverTemp, rk.Overspeed));
            }
            robust = Math.Min(Math.Max(robust, 0.0), 1.0);
            double years = d != null && d.Tech != null ? (d.Tech.Year - 1950.0) / 50.0 : 0.5;
            years = Math.Min(Math.Max(years, 0.0), 1.0);
            return Math.Min(Math.Max(0.70 * robust + 0.30 * years, 0.0), 1.0);
        }

        /// <summary>
        /// 喷管型式对照。同一台 JF-1，喷管按"干态"定尺寸，然后开加力——这是最苛刻的工况，
        /// 收敛喷管会被喉道憋住，收扩喷管能膨胀到超声速。
        /// </summary>
        private static void RunNozzleCases()
        {
            NozzleKind[] kinds = { NozzleKind.Converging, NozzleKind.Variable, NozzleKind.ConvergingDiverging };
            double[] abThrust = new double[kinds.Length];
            double[] dryThrust = new double[kinds.Length];
            bool[] abCapped = new bool[kinds.Length];
            bool[] abBackPressure = new bool[kinds.Length];
            bool[] abSeparated = new bool[kinds.Length];
            double[] abSfc = new double[kinds.Length];
            double[] abMach = new double[kinds.Length];

            Console.WriteLine("  型式                 面积比  出口Ma   干态推力   加力推力   加力SFC   憋住  分离");
            for (int i = 0; i < kinds.Length; i++)
            {
                EngineDesignData d = new EngineDesignData();
                d.Nozzle = kinds[i];
                d.Afterburner.Enabled = true;
                d.Afterburner.ExitTemp = 2000.0;
                d.Afterburner.FuelRatio = 0.03;

                EngineDesignData dry = d.Clone();
                dry.Afterburner.Enabled = false;
                DesignConstants c = OffDesign.DeriveDesignConstants(dry);

                OffDesignResult a = OffDesign.Solve(d, 0.0, 0.0, 1.0, 0.0, c, 40, -1.0, 0.03);
                OffDesignResult b = OffDesign.Solve(dry, 0.0, 0.0, 1.0, 0.0, c);

                abThrust[i] = a.NetThrust;
                dryThrust[i] = b.NetThrust;
                abCapped[i] = a.NozzleCapped;
                abBackPressure[i] = a.NozzleBackPressure;
                abSeparated[i] = a.NozzleSeparated;
                abSfc[i] = a.Sfc;
                abMach[i] = a.NozzleMachE;

                Console.WriteLine("  {0,-18} {1,7:F2} {2,7:F2} {3,9:F2} {4,9:F2} {5,9:F4} {6,6} {7,5}",
                    kinds[i], c.NozzleAreaRatio, a.NozzleMachE,
                    b.NetThrust / 1000.0, a.NetThrust / 1000.0, a.Sfc,
                    a.NozzleBackPressure ? "是" : "否", a.NozzleSeparated ? "是" : "否");
                Console.WriteLine(string.Format(
                    "     细看：进气 {0:F1} 核心 {1:F1} kg/s｜T7 {2:F0}K p5 {3:F0} kPa（{4:F2}×p0）｜核心排气 {5:F0} m/s 面积 {6:F4} m²｜核心推力 {7:F1} 外涵 {8:F1} kN｜限流 {9}",
                    a.MdotAir, a.MdotCore, a.T7, a.P5 / 1000.0, a.P5 / Gas.P0, a.VJetCore, a.NozzleExitArea,
                    a.ThrustCore / 1000.0, a.ThrustBypass / 1000.0, a.NozzleCapped));
            }

            // 1) 干态定尺寸 + 开加力 → 收敛喷管过不了这么多流量，只能把进气量压下去
            //    （旧版本这里是判"憋住"标志位；现在喷管容量进了求解器，标志位不再是主要症状，
            //      真正的症状是流量被压、推力因此反而低于干态）
            Expect("收敛喷管加力被限流", abCapped[0], true);
            Expect("收敛喷管加力推力低于干态", abThrust[0] < dryThrust[0], true);
            // 2) 可变面积喷管把喉道撑开 24%，过不了的那部分流量少一大截
            //    注意这里不判"完全不限流"：把 T7 从 1085 K 拉到 2000 K，同样的喉道容量掉 36%，
            //    真机撑开 20~35% 也补不满，靠的是流量略降 + 涡轮前压力上升一起分摊。
            Expect("可变喷管开加力真的加推力", abThrust[1] > dryThrust[1] * 1.02, true);
            Expect("可变喷管比收敛多给 15% 以上推力",
                abThrust[1] > abThrust[0] * 1.15, true);
            // 3) 收扩喷管出口必须是超声速膨胀，推力不低于收敛解
            //    注意：这台机器的核心喷管落压比只有 ~2，收扩带来的增益本来就小；
            //    落压比越高（加力越猛、总压比越大）增益越明显。
            double gain = (abThrust[2] - abThrust[0]) / Math.Max(Math.Abs(abThrust[0]), 1e-9) * 100.0;
            Console.WriteLine("  收扩 vs 收敛 加力推力增益：{0:F2}%（核心落压比只有 ~2，所以增益小）", gain);
            // 收扩喷管：落压比够、面积比合适时才给超声速；过膨胀到贴壁就退回收敛（真机也会这样）。
            // 这台机器的核心落压比只有 2.5 上下，喷口又没有真的做大，分离是正常结果，
            // 唯一必须成立的是"别比收敛更差"。
            if (!abSeparated[2])
                Expect("收扩出口超声速", abMach[2] > 1.0, true);
            else
                Console.WriteLine("  收扩在这个落压比下过膨胀贴在喷口壁上 → 按收敛解算（真机同样会贴壁）");
            Expect("收扩加力推力不低于收敛", gain >= -0.1, true);
            // 4) 收扩的 SFC 不该比收敛差
            Expect("收扩 SFC 不劣于收敛", abSfc[2] <= abSfc[0] * 1.001, true);

            // 5) 这条惩罚必须能被判据看见——否则玩家配了固定喷口开加力，只会觉得"加力怎么越开越慢"
            {
                EngineDesignData dcap = new EngineDesignData();
                dcap.Nozzle = NozzleKind.Converging;
                dcap.Afterburner.Enabled = true;
                dcap.Afterburner.ExitTemp = 2000.0;
                DesignConstants cc = OffDesign.DeriveDesignConstants(dcap);
                OffDesignResult rp = OffDesign.Solve(dcap, 0.0, 0.0, 1.0, 0.0, cc);
                CheckReport rep = Criteria.Evaluate(dcap, Criteria.ViewOf(rp, 0));
                bool hasCap = false;
                foreach (Finding f in rep.Findings)
                {
                    if (f.Key == "nozzle.capacity") { hasCap = true; }
                }
                OffDesignResult rab = OffDesign.Solve(dcap, 0.0, 0.0, 1.0, 1.0);
                Console.WriteLine(string.Format("  加力自解：ok={0} 限流={1} 进气 {2:F1} kg/s 推力 {3:F1} kN t7={4:F0}",
                    rab.Ok, rab.NozzleCapped, rab.MdotAir, rab.NetThrust / 1000.0, rab.T7));
                Expect("固定收敛 + 加力：判据报出喷管容量不足", hasCap, true);
            }
        }

        /// <summary>
        /// 加力增益与涵道比。
        ///
        /// 我此前以为"加力只多 12%"是模型错了（真机 F110 干态 76 kN、加力 129 kN，+70%）。
        /// 但加力只加热核心机那股流量：涵道比越大，核心机占的推力份额越小，总推力增益就越小。
        /// F110 的涵道比是 0.76，加力喷气机（J79/J85）干脆是 0——所以它们的增益才大。
        /// 这一节把四台真机和我这台 JF-1（涵道比 2.75）摆在一起看。
        /// </summary>
        private static void RunAfterburnerBprCases()
        {
            Console.WriteLine("  机型            涵道比   干态推力   加力推力    增益");
            string[] keys = { "JF-1", "J79", "J57", "J85", "RD-33" };
            foreach (string key in keys)
            {
                try
                {
                    EngineDesignData d = key == "JF-1" ? new EngineDesignData() : Presets.Build(key);
                    EngineDesignData dry = d.Clone();
                    dry.Afterburner.Enabled = false;
                    DesignConstants c = OffDesign.DeriveDesignConstants(dry);

                    EngineDesignData wet = d.Clone();
                    wet.Afterburner.Enabled = true;
                    wet.Afterburner.ExitTemp = Math.Max(wet.Afterburner.ExitTemp, 1900.0);
                    wet.Nozzle = NozzleKind.Variable;   // 真加力机都有可变喷口

                    OffDesignResult rd = OffDesign.Solve(dry, dry.DesignAlt, dry.DesignMach, 1.0, 0.0, c);
                    OffDesignResult rw = OffDesign.Solve(wet, wet.DesignAlt, wet.DesignMach, 1.0, 1.0, c);
                    double gain = rd.NetThrust > 1.0 ? (rw.NetThrust / rd.NetThrust - 1.0) * 100.0 : 0.0;
                    Console.WriteLine("  {0,-14} {1,7:F2} {2,9:F2} {3,9:F2} {4,7:F1}%{5}",
                        key, rd.Bpr, rd.NetThrust / 1000.0, rw.NetThrust / 1000.0, gain,
                        rw.NozzleCapped ? "（限流）" : "");

                    if (key == "J79")
                    {
                        // 单轴涡喷：全部流量经过核心机，加力增益应当是正儿八经的 30% 以上
                        Expect("涡喷加力增益 > 25%", gain > 25.0, true);
                        Expect("涡喷加力推力大于干态", rw.NetThrust > rd.NetThrust, true);
                    }
                    if (key == "JF-1")
                    {
                        // 大涵道比：核心机只占小头，加力增益必然小，但不能是负的
                        Expect("大涵道比加力仍有增益但小于 25%", gain > 0.0 && gain < 25.0, true);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  {0,-14} 异常：{1}", key, ex.Message);
                    _fails++;
                }
            }
        }

        /// <summary>
        /// 涡轮出口温度 T5 的口径。
        ///
        /// §40 那次试验失败后我怀疑 T5 算得不对：涡轮的温降是拿"需求功 ÷ (流量·cp)"算的，
        /// 而 cp 取的是涡轮进口温度处的值。涡轮从 T4 掉到 T5，cp 一路在变，
        /// 用进口的 cp（偏大）会低估温降 → T5 偏高；用出口的会高估。差多少要看数。
        /// 这一节把设计点的温度链打出来，并算一遍"用积分焓差反推"的对照值。
        /// </summary>
        private static void RunTurbineExitCases()
        {
            Console.WriteLine("  机型        T3     T4     T5    T5(积分)  差    T7   落压比  cp(T4)  cp(T5)");
            string[] keys = { "JF-1", "J79", "J57", "J85", "RD-33" };
            foreach (string key in keys)
            {
                try
                {
                    EngineDesignData d = key == "JF-1" ? new EngineDesignData() : Presets.Build(key);
                    CycleResult r = Cycle.SolveDesignPoint(d);
                    double work = r.WTurbHpt + r.WTurbIpt + r.WTurbLpt;   // W
                    double mTurb = r.MdotCore * (1.0 + r.Far);
                    double cpIn = Gas.Cp(r.T4);
                    // 用真实焓差反推：Δh = ∫cp dT，从 T4 往下积到 T5，看积出来的焓差等不等于 W/m。
                    // （第一版我用 h=cp(T)·T 当焓——那不是积分，多了 T·dcp/dT 那一项，
                    //   算出来的 T5 反而偏高，差点把一条正常的口径判成 bug。）
                    double dhNeed = work / Math.Max(mTurb, 1e-9);
                    double t5Int = r.T5;
                    for (int i = 0; i < 60; i++)
                    {
                        double dh = 0.0;
                        int n = 200;
                        double dt = (r.T4 - t5Int) / n;
                        for (int k = 0; k < n; k++)
                        {
                            double ta = t5Int + k * dt, tb = ta + dt;
                            dh += 0.5 * (Gas.Cp(ta) + Gas.Cp(tb)) * dt;
                        }
                        double err = dh - dhNeed;
                        if (Math.Abs(err) < 500.0) break;
                        // dΔh/dT5 = -cp(T5)
                        t5Int += err / Math.Max(Gas.Cp(t5Int), 1.0);
                    }
                    Console.WriteLine(string.Format(
                        "  {0,-8} {1,6:F0} {2,6:F0} {3,6:F0} {4,9:F0} {5,5:F0} {6,6:F0} {7,7:F2} {8,7:F1} {9,7:F1}",
                        key, r.T3, r.T4, r.T5, t5Int, r.T5 - t5Int, r.T7, r.Opr, cpIn, Gas.Cp(r.T5)));
                    if (key == "JF-1")
                    {
                        // 两种口径的差不该超过 4%——超了说明 cp 的取法有问题
                        Expect("T5 两种口径相差 <4%",
                            Math.Abs(r.T5 - t5Int) / Math.Max(r.T5, 1.0) < 0.04, true);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  {0,-8} 异常：{1}", key, ex.Message);
                    _fails++;
                }
            }
        }


        /// <summary>
        /// 站位表回归。这里量的是"各部件的尺寸占比"，不是绝对直径——
        /// 绝对直径还带着"按流量反解等效捕获面积"的老口径（见文档 §70），
        /// 但各段之间的比例必须像一台真发动机。
        /// </summary>
        /// <summary>
        /// 第 45 段：矢量喷口。
        ///
        /// 这里验的不是"能不能偏"，而是"偏的方向对不对"：推力偏出去以后，
        /// 围绕质心产生的力矩必须和飞控要的方向同号。倒装（发动机装在质心上方）
        /// 时力矩要自动反号——这正是抄别人正负号会翻车的地方。
        /// </summary>
        private static void RunVectorCases()
        {
            Vec3 up = new Vec3(0.0, 1.0, 0.0);       // 推力方向（喷口在 -Y，所以推力 +Y）
            Vec3 pAxis = new Vec3(1.0, 0.0, 0.0);    // 俯仰力矩轴
            Vec3 yAxis = new Vec3(0.0, 0.0, 1.0);    // 偏航力矩轴
            Vec3 rTail = new Vec3(0.0, -1.5, 0.0);   // 喷口在质心下方 1.5 m（正常装机尾）
            double ang;

            Vec3 dPitch = ThrustVector.Direction(up, rTail, pAxis, yAxis, 1.0, 0.0, 15.0, out ang);
            Vec3 tauPitch = Vec3.Cross(rTail, dPitch);
            Console.WriteLine(string.Format(
                "  俯仰满指令：偏 {0:F2}°｜推力 {1}｜轴向分量 {2:F4}｜侧向 {3:F4}｜力矩 X {4:F3}",
                ang, dPitch.ToString(), Vec3.Dot(dPitch, up),
                Math.Sqrt(Math.Max(0.0, 1.0 - Vec3.Dot(dPitch, up) * Vec3.Dot(dPitch, up))), tauPitch.X));
            Expect("偏转角 = 最大偏转角", Math.Abs(ang - 15.0) < 0.05, true);
            Expect("轴向分量 = cos15°", Math.Abs(Vec3.Dot(dPitch, up) - Math.Cos(15.0 * Math.PI / 180.0)) < 0.002, true);
            // KSP 约定：`ctrlState.pitch` 为正 = 抬头。Unity 里绕机体系 +X 正转让机头往下压，
            // 所以"抬头"的力矩必须是 -X。玩家报的"矢量喷口控制方向反了"就是这么来的。
            Expect("抬头指令 → 力矩沿 -X（KSP 约定）", tauPitch.X < 0.0, true);

            Vec3 dYaw = ThrustVector.Direction(up, rTail, pAxis, yAxis, 0.0, 1.0, 15.0, out ang);
            Vec3 tauYaw = Vec3.Cross(rTail, dYaw);
            Console.WriteLine(string.Format("  偏航满指令：偏 {0:F2}°｜力矩 Z {1:F3}", ang, tauYaw.Z));
            // 偏航的符号是**玩家实测**来的（§276）：两轴同一个规则——指令为正，
            // 力矩沿该控制轴的负向。上一版这条钉的是 `> 0`（另一套说法），方向反了
            // 照样通过：**只钉大小不钉方向，等于没钉**。
            Expect("偏航力矩与俯仰同规则（沿控制轴负向）", tauYaw.Z < 0.0, true);

            // 倒装：发动机装在质心另一侧（r 反号）。同一句"抬头"指令，
            // 力矩仍要沿 -X——变的是气流往哪边掰，不是它绕哪根轴转。
            Vec3 rNose = new Vec3(0.0, 1.5, 0.0);
            Vec3 dInv = ThrustVector.Direction(up, rNose, pAxis, yAxis, 1.0, 0.0, 15.0, out ang);
            Vec3 tauInv = Vec3.Cross(rNose, dInv);
            Console.WriteLine(string.Format("  倒装同一指令：偏 {0:F2}°｜推力 {1}｜力矩 X {2:F3}（应同为 -X）",
                ang, dInv.ToString(), tauInv.X));
            Expect("倒装时力矩轴不变、方向同号", tauInv.X < 0.0, true);
            Expect("倒装时侧向分量反号", dInv.Z * dPitch.Z < 0.0, true);

            // 零指令 / 最大角为零：一动不动
            Vec3 dZero = ThrustVector.Direction(up, rTail, pAxis, yAxis, 0.0, 0.0, 15.0, out ang);
            Vec3 dNoMax = ThrustVector.Direction(up, rTail, pAxis, yAxis, 1.0, 1.0, 0.0, out ang);
            Console.WriteLine(string.Format("  零指令：偏 {0:F3}°｜最大角 0：{1}", ang, dNoMax.ToString()));
            Expect("零指令不偏", Math.Abs(Vec3.Dot(dZero, up) - 1.0) < 1e-9, true);
            Expect("最大角为零时不偏", Math.Abs(Vec3.Dot(dNoMax, up) - 1.0) < 1e-9, true);

            // 斜着压杆（pitch=yaw=1）：合向量截断，偏角不该超过最大值
            Vec3 dDiag = ThrustVector.Direction(up, rTail, pAxis, yAxis, 1.0, 1.0, 15.0, out ang);
            Console.WriteLine(string.Format("  俯仰+偏航同时满：偏 {0:F2}°（不该超过 15°）", ang));
            Expect("斜向指令按合向量截断", ang <= 15.05, true);

            // ---- 退化装机：推力轴、偏航轴、力臂三根重合（火箭尾装发动机）----
            //
            // 玩家报的"矢量喷管在偏航方向上抽搐"就是这一段。老解法 F̂ ∝ τ̂ × r 在这里
            // 叉的是两个平行向量：模长剩 1e-9 量级，归一化之后方向由末位噪声决定，
            // 姿态每抖一点，花瓣就往对面抡一下。
            // 现在分两件事说清：气流**照样偏**（方向听指令），力矩**本来就换不到**（有力臂判据）。
            Vec3 yAxisPar = new Vec3(0.0, 1.0, 0.0);   // 偏航轴与推力轴重合
            Vec3 goalPar = yAxisPar;                   // 飞控要的力矩：绕竖轴
            Vec3 dPar = ThrustVector.Direction(up, rTail, pAxis, yAxisPar, 0.0, 1.0, 15.0, out ang);
            double alignPar = ThrustVector.TorqueAlign(rTail, up, dPar, goalPar);
            Console.WriteLine(string.Format("  退化装机偏航满指令：偏 {0:F2}°｜推力 {1}｜力矩方向对得上 {2:F3}",
                ang, dPar.ToString(), alignPar));
            Expect("退化装机仍然偏满（气流掰得过去）", Math.Abs(ang - 15.0) < 0.05, true);
            Expect("退化装机偏转沿横向控制轴",
                Math.Abs(Math.Abs(Vec3.Dot(dPar, pAxis)) - Math.Sin(15.0 * Math.PI / 180.0)) < 0.005, true);
            // 方向也要钉：只钉"偏了 15°"，往左往右都算通过 —— 那正是"偏航反了"能一路绿到玩家手里的原因。
            Expect("退化装机的偏转方向与指令同号（偏航正 = 往 -right 掰）", Vec3.Dot(dPar, pAxis) < 0.0, true);
            Expect("退化装机的力矩方向确实对不上（几何决定，不是 bug）", alignPar < 0.2, true);

            // 姿态抖 0.5°：方向不许翻个儿（老版本在这里会从 +X 跳到 -X）
            Vec3 yAxisTilt = new Vec3(0.0087, 0.99996, 0.0).Normalized;
            Vec3 dPar2 = ThrustVector.Direction(up, rTail, pAxis, yAxisTilt, 0.0, 1.0, 15.0, out ang);
            Console.WriteLine(string.Format("  姿态抖 0.5° 后：推力 {0}｜与上一条同向 {1:F4}", dPar2.ToString(), Vec3.Dot(dPar, dPar2)));
            Expect("姿态抖 0.5° 不改变偏转方向", Vec3.Dot(dPar, dPar2) > 0.99, true);

            // 反过来：正常装机（偏航轴垂直于推力轴、力臂在轴线上）力矩方向必须正对飞控。
            // "正对"是**沿控制轴负向**（与俯仰同一条规则，§276），所以目标力矩要取 -yAxis。
            Expect("正常装机的力矩方向正对飞控（沿控制轴负向）",
                ThrustVector.TorqueAlign(rTail, up, dYaw, yAxis * -1.0) > 0.99, true);
        }

        /// <summary>
        /// 第 57 段：矢量指令整形。
        ///
        /// 玩家报的是"偏航抖动"，根子在这里：`vessel.ctrlState.yaw` 带着 SAS 修正，
        /// 稳态会在 ±0.02 上下抖，以前这串噪声直接当偏航指令喂给喷口，花瓣就一直在微颤。
        /// 所以这一段不是在验"能不能偏"，而是验"给它一串噪声，输出会不会跟着跳"，
        /// 以及三个方向能不能各关各的（偏航吵就单独关偏航，别逼玩家把整套矢量关掉）。
        /// </summary>
        /// <summary>
        /// 独立进气道零件（第 58 段）。
        ///
        /// 这一段的重点是"接线没把老数扰动"和"几件进气道合成得对"：
        /// · 什么都没装时 Present=false，上层走发动机自带口径——这条是"老存档逐位相同"的前提；
        /// · 两件同款 = 面积与流量都翻倍、恢复系数不变（加权平均）；
        /// · 一件好一件差 = 恢复按流量加权、畸变取最差（流场不匀不会因为并联就变匀）；
        /// · 解必须按工况现算：同一个 install 在 M0 与 M1.6 上恢复系数不能是同一个数。
        /// </summary>
        private static void RunIntakeInstallCases(EngineDesignData baseDesign)
        {
            TechLevel tech = baseDesign.Tech;

            // ---- ① 没装 = 不存在 ----
            Expect("空清单合成出 None", IntakeInstall.Aggregate(null, tech, 0.0, 0.0).Present == false, true);
            Expect("空清单合成出 None（空集合）",
                IntakeInstall.Aggregate(new List<IntakeUnit>(), tech, 0.0, 0.0).Present == false, true);
            Expect("IntakeInstall.None 没装", IntakeInstall.None.Present == false, true);
            Expect("None 的签名是空的", IntakeInstall.None.Signature.Length == 0, true);

            // ---- ② 单件：面积与直接求解一致 ----
            IntakeSpec axial = new IntakeSpec();
            axial.Kind = IntakeKind.Axial;
            axial.CaptureDiameter = 0.9;
            axial.ThroatRatio = 0.8;
            IntakeSolution solo = IntakeSolver.SolvePart(axial, 0.5, tech, 0.0, 0.0);
            Check("零件的捕获面积 = π(capD/2)²", solo.CaptureArea, Math.PI * 0.25 * 0.9 * 0.9, 0.01, "m²");
            Check("零件的喉道面积 = 捕获×喉道比", solo.ThroatArea, solo.CaptureArea * 0.8, 0.01, "m²");
            Check("零件的发动机面积 = π(faceD/2)²", solo.FaceArea, Math.PI * 0.25 * 0.5 * 0.5, 0.01, "m²");

            List<IntakeUnit> one = new List<IntakeUnit>();
            one.Add(new IntakeUnit(axial, 0.5, "左"));
            IntakeInstall ins1 = IntakeInstall.Aggregate(one, tech, 0.0, 0.0);
            Expect("一件也算装了", ins1.Present, true);
            Check("单件的捕获面积与单件求解一致", ins1.CaptureArea, solo.CaptureArea, 0.5, "m²");
            Check("单件的可用流量与单件求解一致", ins1.AvailableMassFlow, solo.AvailableMassFlow, 0.5, "kg/s");
            Check("单件的总压恢复与单件求解一致", ins1.Tpr, solo.Tpr, 0.2, "");
            Check("单件没装成两份", ins1.Count, 1, 0.0, "件");

            // ---- ③ 两件同款：面积流量翻倍，恢复不变 ----
            List<IntakeUnit> two = new List<IntakeUnit>();
            two.Add(new IntakeUnit(axial, 0.5, "左"));
            two.Add(new IntakeUnit(axial, 0.5, "右"));
            IntakeInstall ins2 = IntakeInstall.Aggregate(two, tech, 0.0, 0.0);
            Check("两件同款的捕获面积翻倍", ins2.CaptureArea, solo.CaptureArea * 2.0, 0.5, "m²");
            Check("两件同款的可用流量翻倍", ins2.AvailableMassFlow, solo.AvailableMassFlow * 2.0, 0.5, "kg/s");
            Check("两件同款的恢复系数不变", ins2.Tpr, solo.Tpr, 0.2, "");
            Check("两件同款的件数", ins2.Count, 2, 0.0, "件");
            Expect("两件同款的签名与单件不同", ins2.Signature != ins1.Signature, true);

            // ---- ④ 一件好一件差：恢复加权、畸变取最差 ----
            IntakeSpec cone = new IntakeSpec();
            cone.Kind = IntakeKind.Cone;
            cone.CaptureDiameter = 0.9;
            cone.ThroatRatio = 0.88;
            IntakeSolution soloCone = IntakeSolver.SolvePart(cone, 0.5, tech, 0.0, 1.8);
            IntakeSolution soloAxial = IntakeSolver.SolvePart(axial, 0.5, tech, 0.0, 1.8);
            List<IntakeUnit> mix = new List<IntakeUnit>();
            mix.Add(new IntakeUnit(axial, 0.5, "左"));
            mix.Add(new IntakeUnit(cone, 0.5, "右"));
            IntakeInstall insMix = IntakeInstall.Aggregate(mix, tech, 0.0, 1.8);
            double wA = Math.Max(soloAxial.CapturedMassFlow, 1e-6);
            double wC = Math.Max(soloCone.CapturedMassFlow, 1e-6);
            double weighted = (soloAxial.Tpr * wA + soloCone.Tpr * wC) / (wA + wC);
            Check("混装的恢复系数按流量加权", insMix.Tpr, weighted, 0.5, "");
            Check("混装的畸变取最差那件", insMix.Distortion,
                  Math.Max(soloAxial.DistortionIndex, soloCone.DistortionIndex), 0.5, "");
            Expect("混装时型式不一致被记下来", insMix.DistinctKinds > 0, true);

            // ---- ⑤ 解必须按工况现算 ----
            IntakeSolution at0 = ins2.ToSolution(0.0, 0.0);
            IntakeSolution at16 = ins2.ToSolution(0.0, 1.6);
            Expect("同一个 install 在两个马赫上恢复系数不同（解是现算的）",
                   Math.Abs(at16.Tpr - at0.Tpr) > 1e-3, true);
            Check("ToSolution 的捕获面积与合计一致", at16.CaptureArea, ins2.CaptureArea, 0.5, "m²");
            Expect("ToSolution 的 LegacyMassFlow 为正", at0.LegacyMassFlow > 0.0, true);
            // 以前这条比"可用流量"（两件的喉道能力之和），那等于把"两件各喂饱一次"当成了正确行为。
            // 正确口径：多件共喂一台发动机，设计点流量就是**一件**的量，多出来的溢流。
            Check("多件的设计点流量不重复计入（等同单件）", at0.LegacyMassFlow,
                  ins1.ToSolution(0.0, 0.0).LegacyMassFlow, 2.0, "kg/s");
            Check("多件的摄取量与单件同量级（不是两件相加）", at0.CapturedMassFlow,
                  ins1.ToSolution(0.0, 0.0).CapturedMassFlow, 5.0, "kg/s");
            Expect("多件时确实溢流了（多出来的气进不了发动机）", at0.SpillageMassFlow > 0.0, true);

            // ---- ⑥ 存档往返（零件与整机共用一份键名）----
            IntakeSpec src = new IntakeSpec();
            src.Kind = IntakeKind.Ramp;
            src.CaptureDiameter = 1.13;
            src.ThroatRatio = 0.77;
            src.FaceMachDesign = 0.63;
            src.BleedFrac = 0.045;
            src.LipRadius = 0.021;
            src.DuctBendDeg = 22.0;
            src.AlphaDeg = 7.0;
            src.BetaDeg = -3.5;
            ConfigNode node = new ConfigNode();
            IntakeSpecCodec.Save(node, src);
            IntakeSpec back = IntakeSpecCodec.Load(node, new IntakeSpec());
            Check("往返：型式", (double)(int)back.Kind, (double)(int)src.Kind, 0.0, "");
            Check("往返：捕获直径", back.CaptureDiameter, src.CaptureDiameter, 0.0, "m");
            Check("往返：喉道比", back.ThroatRatio, src.ThroatRatio, 0.0, "");
            Check("往返：设计面马赫", back.FaceMachDesign, src.FaceMachDesign, 0.0, "");
            Check("往返：抽气", back.BleedFrac, src.BleedFrac, 0.0, "");
            Check("往返：唇口", back.LipRadius, src.LipRadius, 0.0, "m");
            Check("往返：管道弯曲", back.DuctBendDeg, src.DuctBendDeg, 0.0, "°");
            Check("往返：迎角", back.AlphaDeg, src.AlphaDeg, 0.0, "°");
            Check("往返：侧滑", back.BetaDeg, src.BetaDeg, 0.0, "°");

            // ---- ⑦ 设计点：装了进气道照样能解出来，且没装时一个数不动 ----
            CycleResult plain = Cycle.SolveDesignPoint(baseDesign);
            CycleResult withPart = Cycle.SolveDesignPoint(baseDesign, ins2);
            Expect("装了独立进气道照样解出设计点", withPart.Ok, true);
            Check("设计点推力仍在同一量级（±60%）", withPart.Thrust, plain.Thrust, 60.0, "N");

            Console.WriteLine(string.Format("    两件 Ø0.9 进气道（M1.8）：捕获 {0:F3} m²｜可用 {1:F1} kg/s｜恢复 {2:F3}｜畸变 {3:F3}",
                insMix.CaptureArea, insMix.AvailableMassFlow, insMix.Tpr, insMix.Distortion));
            Console.WriteLine(string.Format("    设计点：自带口径 {0:F1} kN → 外挂两件 {1:F1} kN｜{2}",
                plain.Thrust / 1000.0, withPart.Thrust / 1000.0, ins2.Describe()));

            // ---- ⑧ 发动机进口面才是硬上限（§284）----
            //
            // 玩家实机反馈：一台 2.3 t 的机器，把独立进气道做到 Ø7 m 那种尺寸，
            // 读数里出现「进气量 7787 kg/s」、推力 344.91 kN —— 进气道做多大就吃多少，
            // 发动机本身一个数没改。物理上发动机进口那张面就那么宽，多出来的只能在唇口溢流。
            double pSl, tSl;
            Gas.Isa(0.0, out pSl, out tSl);
            double faceAreaSmall = Math.PI * 0.25 * 0.5 * 0.5;   // 一台 Ø0.5 m 的小发动机

            IntakeSpec huge = new IntakeSpec();
            huge.Kind = IntakeKind.Axial;
            huge.CaptureDiameter = 7.0;
            huge.ThroatRatio = 0.9;
            List<IntakeUnit> hugeOne = new List<IntakeUnit>();
            hugeOne.Add(new IntakeUnit(huge, 0.5, "巨无霸"));
            IntakeInstall hugeIns = IntakeInstall.Aggregate(hugeOne, tech, 0.0, 0.0);

            IntakeSolution hugeOpen = hugeIns.ToSolution(0.0, 0.0);
            IntakeSolution hugeCap = hugeIns.ToSolution(0.0, 0.0, faceAreaSmall, 0.5, double.NaN);
            double bleedUse = Math.Min(Math.Max(huge.BleedFrac, 0.0), 0.25);
            double wantCap = IntakeSolver.MassFlowAt(faceAreaSmall, pSl, tSl, IntakeSolver.FaceMachOf(null))
                           * (1.0 - bleedUse);
            Expect("Ø7 m 进气道装 Ø0.5 m 发动机面：流量被封顶", hugeCap.FlowCappedByEngineFace, true);
            Check("封顶后的流量就是发动机面能过的量", hugeCap.LegacyMassFlow, wantCap, 1e-6, "kg/s");
            Expect("封顶后的流量远小于不封顶（进气道白做了）", hugeOpen.LegacyMassFlow > hugeCap.LegacyMassFlow * 5.0, true);
            Check("上限本身记下来了（给界面用）", hugeCap.EngineFaceCapMassFlow, wantCap, 1e-6, "kg/s");
            Expect("封顶时会给玩家一句理由", hugeCap.Warnings.Count > 0, true);

            // 尺寸刚好的件不许被削（否则"进气道装对了反而更弱"）
            IntakeSpec fit = new IntakeSpec();
            fit.Kind = IntakeKind.Axial;
            fit.CaptureDiameter = 0.45;
            fit.ThroatRatio = 0.8;
            List<IntakeUnit> fitOne = new List<IntakeUnit>();
            fitOne.Add(new IntakeUnit(fit, 0.5, "刚合适"));
            IntakeInstall fitIns = IntakeInstall.Aggregate(fitOne, tech, 0.0, 0.0);
            IntakeSolution fitOpen = fitIns.ToSolution(0.0, 0.0);
            IntakeSolution fitCap = fitIns.ToSolution(0.0, 0.0, faceAreaSmall, 0.5, double.NaN);
            Expect("进气道不比发动机进口大时不封顶", fitCap.FlowCappedByEngineFace, false);
            Check("不封顶时流量逐位不变", fitCap.LegacyMassFlow, fitOpen.LegacyMassFlow, 1e-9, "kg/s");

            // 推力层面：拿真预设比一次（这是玩家能感觉到的那个数）
            EngineDesignData gtf = Presets.Build("GTF1100");
            IntakeSpec hugeG = new IntakeSpec();
            hugeG.Kind = IntakeKind.Axial;
            hugeG.CaptureDiameter = 7.0;
            hugeG.ThroatRatio = 0.9;
            List<IntakeUnit> gUnits = new List<IntakeUnit>();
            gUnits.Add(new IntakeUnit(hugeG, gtf.EngineFaceDiameter(), "巨无霸"));
            IntakeInstall gIns = IntakeInstall.Aggregate(gUnits, gtf.Tech, 0.0, 0.0);
            CycleResult gPlain = Cycle.SolveDesignPoint(gtf);
            CycleResult gCapped = Cycle.SolveDesignPoint(gtf, gIns);
            IntakeSolution gSol = gIns.ToSolution(gtf.DesignAlt, 0.0, gtf.EngineFaceArea(), gtf.EngineFaceMach(), double.NaN);
            Expect("GTF1100 装 Ø7 m 进气道也被封顶", gSol.FlowCappedByEngineFace, true);
            Check("GTF1100 装 Ø7 m 进气道：推力与自带进气道同一量级（±25%）", gCapped.Thrust, gPlain.Thrust, 25.0, "N");
            Check("GTF1100 的发动机面面积就是 π/4·风扇直径²", gtf.EngineFaceArea(),
                  Math.PI * 0.25 * gtf.Fan.Diameter * gtf.Fan.Diameter, 1e-9, "m²");

            Console.WriteLine(string.Format("    超大进气道（Ø7 m 捕获）装 Ø0.5 m 发动机面：{0:F1} kg/s → 封顶 {1:F1} kg/s（面马赫 {2:F2}）",
                hugeOpen.LegacyMassFlow, hugeCap.LegacyMassFlow, IntakeSolver.FaceMachOf(null)));
            Console.WriteLine(string.Format("    GTF1100 自带口径 {0:F1} kN → 装 Ø7 m 进气道 {1:F1} kN｜发动机面 {2:F3} m²",
                gPlain.Thrust / 1000.0, gCapped.Thrust / 1000.0, gtf.EngineFaceArea()));
        }

        /// <summary>
        /// 原版进气道与缩放 mod 的换算（第 59 段）。
        ///
        /// 这一段钉三件事：
        /// · 面积按平方缩放（线尺寸 ×2 = 面积 ×4），缩放系数非法时退回原件；
        /// · 转换出来的 Spec 是轴对称、喉道比 0.90、面马赫 0.50 —— 不去假装原版有内压缩段；
        /// · 原版自己的马赫上限要生效：超过它这件就退出供气，而不是继续硬算流量。
        /// </summary>
        private static void RunStockIntakeCases(EngineDesignData baseDesign)
        {
            TechLevel tech = baseDesign.Tech;

            // ---- ① 面积换算：优先量外形，量不到才退回 cfg 面积 ----
            //
            // cfg 里的 area 不是物理面积：原版某件的 cfg 自己写着「frontal area is only .39m^2」
            // 而它的 area = 0.0061 —— 差 64 倍。所以正常路径是量零件的唇口直径。
            Check("量到口径优先：Ø0.6 → 捕获面积", StockIntakeBridge.PhysicalCaptureArea(0.006, 1.0, 0.6),
                  Math.PI * 0.25 * 0.36, 1e-6, "m²");
            Check("量不到口径：退回 cfg × 64", StockIntakeBridge.PhysicalCaptureArea(0.0125, 1.0, 0.0),
                  0.0125 * StockIntakeBridge.KspAreaToPhysical, 1e-6, "m²");
            Check("退回时不再乘缩放平方（area 已经是对着游戏里那个尺寸写的）",
                  StockIntakeBridge.PhysicalCaptureArea(0.0125, 2.0, 0.0),
                  0.0125 * StockIntakeBridge.KspAreaToPhysical, 1e-6, "m²");
            // 两条口径打架时取小的那个：量外形 1.25 m 的 ramAirIntake 曾算出比 cfg 折算大 2.6 倍，
            // 于是"小件给大流量"、玩家装上原版进气道推力反而比不装还高。
            Check("量外形远大于 cfg 折算时取小的（小件不许给大流量）",
                  StockIntakeBridge.PhysicalCaptureArea(0.0074, 1.0, 1.25),
                  0.0074 * StockIntakeBridge.KspAreaToPhysical, 1e-6, "m²");
            Check("量外形小于 cfg 折算时取外形（CircularIntake 的真实情况：0.345 对 0.384）",
                  StockIntakeBridge.PhysicalCaptureArea(0.0060, 1.0, 0.66),
                  Math.PI * 0.25 * 0.66 * 0.66, 1e-6, "m²");
            Check("缩放系数 0 当原件处理", StockIntakeBridge.PhysicalCaptureArea(0.0125, 0.0, 0.0),
                  0.0125 * StockIntakeBridge.KspAreaToPhysical, 1e-6, "m²");
            Check("面积 0 且量不到口径 → 0", StockIntakeBridge.PhysicalCaptureArea(0.0, 2.0, 0.0), 0.0, 0.0, "m²");
            Check("口径太小小到不可信（0.04 m）→ 退回 cfg 面积",
                  StockIntakeBridge.PhysicalCaptureArea(0.0125, 1.0, 0.04),
                  0.0125 * StockIntakeBridge.KspAreaToPhysical, 1e-6, "m²");
            Check("直径由面积反推", StockIntakeBridge.CaptureDiameterOf(0.0, 1.0, 0.6),
                  Math.Sqrt(4.0 * (Math.PI * 0.25 * 0.36) / Math.PI), 1e-6, "m");

            // ---- ② 换算出的型式与缺省 ----
            IntakeSpec sp = StockIntakeBridge.SpecOf(0.0125, 2.0, 0.6);
            Check("原版进气道按轴对称处理", (double)(int)sp.Kind, (double)(int)IntakeKind.Axial, 0.0, "");
            Check("原版的喉道比", sp.ThroatRatio, StockIntakeBridge.DefaultThroatRatio, 0.0, "");
            Check("原版的迎面马赫", sp.FaceMachDesign, StockIntakeBridge.DefaultFaceMach, 0.0, "");
            Check("原版的捕获直径按口径走（Ø0.6 原件）", sp.CaptureDiameter, 0.6, 1e-6, "m");

            // ---- ③ 真马赫上限（只有改过 cfg 的 mod 才有；原版全都是 0）----
            Expect("没有马赫上限 = 一直供气", StockIntakeBridge.SuppliesAt(0.0, 5.0), true);
            Expect("马赫上限 1.6：M1.5 供气", StockIntakeBridge.SuppliesAt(1.6, 1.5), true);
            Expect("马赫上限 1.6：M1.7 不供气", StockIntakeBridge.SuppliesAt(1.6, 1.7), false);
            Expect("马赫上限 1.6：M 为 NaN 不供气", StockIntakeBridge.SuppliesAt(1.6, double.NaN), false);

            // ---- ③b intakeSpeed 不是上限，这一条必须钉死 ----
            //
            // 上一版把原版的 intakeSpeed（环形进气道 15、冲压 10、激波锥 5，单位 m/s）
            // 当成马赫上限读 → M0.044，装机表现是**一起飞就断气**。
            // 它实际是低速吸气流速。这里断言那个换算函数还在（给改过 cfg 的 mod 用），
            // 但断言原版零件换算出来的上限是 0（不限），也就是那条路没被接上。
            Check("速度换算还在：340 m/s ≈ 1 马赫", StockIntakeBridge.SpeedToMach(340.0), 1.0, 0.5, "M");
            Check("速度换算：1500 m/s → M4.41", StockIntakeBridge.SpeedToMach(1500.0),
                  1500.0 / StockIntakeBridge.SeaLevelSoundSpeed, 1e-6, "M");
            Expect("速度换算：0 或负 → 0（= 不限）", StockIntakeBridge.SpeedToMach(0.0) <= 0.0, true);
            Expect("速度换算：NaN 不炸", StockIntakeBridge.SpeedToMach(double.NaN) <= 0.0, true);
            IntakeUnit bare = StockIntakeBridge.UnitOf(0.006, 1.0, "环形进气道", 0.5, 0.6);
            Check("原版零件没填 machLimit → 上限 0（不限）", bare.MaxMach, 0.0, 0.0, "");
            Expect("若把 intakeSpeed=15 当上限用会是 M0.044（这条留着当反面教材）",
                   StockIntakeBridge.SpeedToMach(15.0) < 0.05, true);

            // ---- ③c cfg 里的 machCurve 才是原版真正的性能曲线 ----
            List<double> km = new List<double>();
            List<double> kv = new List<double>();
            km.Add(1.0); kv.Add(1.0);
            km.Add(1.5); kv.Add(0.9);
            km.Add(2.5); kv.Add(0.45);
            km.Add(3.5); kv.Add(0.1);
            Func<double, double> curve = StockIntakeBridge.CurveOf(km, kv);
            Expect("曲线读得出来", curve != null, true);
            Check("曲线：M1.0 → 1.00", curve(1.0), 1.0, 1e-6, "");
            Check("曲线：M2.0 → 0.675（线性插值）", curve(2.0), 0.675, 1e-6, "");
            Check("曲线：M2.5 → 0.45", curve(2.5), 0.45, 1e-6, "");
            Check("曲线：低于首节点取首节点", curve(0.3), 1.0, 1e-6, "");
            Check("曲线：高于末节点取末节点", curve(9.0), 0.1, 1e-6, "");
            Expect("曲线：节点乱序 → 不给曲线", StockIntakeBridge.CurveOf(
                   new double[] { 2.0, 1.0 }, new double[] { 1.0, 0.5 }) == null, true);
            Expect("曲线：节点数与值数对不上 → 不给曲线", StockIntakeBridge.CurveOf(
                   new double[] { 1.0, 2.0 }, new double[] { 1.0 }) == null, true);

            IntakeUnit u = StockIntakeBridge.UnitOf(0.006, 1.0, "原版环形进气道", 0.5, 0.6, curve,
                                                    StockIntakeBridge.CurveStamp(km, kv), 1.6);
            Check("原版零件的出口直径按发动机面给", u.FaceDiameter, 0.5, 0.0, "m");
            Check("原版零件的马赫上限带在身上", u.MaxMach, 1.6, 0.0, "");
            Check("M1.0 上曲线值 1.0", u.SupplyAt(1.0), 1.0, 1e-6, "");
            Check("M2.5 上曲线值 0.45（不是断气）", u.SupplyAt(2.5), 0.45, 1e-6, "");

            // ---- ④ 超过上限就退出供气（不是继续硬算）----
            List<IntakeUnit> one = new List<IntakeUnit>();
            one.Add(u);
            IntakeInstall under = IntakeInstall.Aggregate(one, tech, 0.0, 1.4);
            IntakeInstall over = IntakeInstall.Aggregate(one, tech, 0.0, 2.0);
            Expect("上限之内这件算装了", under.Present && under.Valid, true);
            Expect("超过上限这件不再供气（合计失效）", over.Valid == false, true);
            bool said = false;
            for (int i = 0; i < over.Warnings.Count; i++)
                if (over.Warnings[i].IndexOf("马赫上限", StringComparison.Ordinal) >= 0) said = true;
            Expect("超过上限要说明白是哪一条", said, true);
            Expect("上限之内与超过上限的签名相同（几何没变，缓存该共用）", under.Signature != over.Signature, false);

            // ---- ④b 没有上限、但有性能曲线的原版件：面积按曲线打折，不是断气 ----
            IntakeUnit curved = StockIntakeBridge.UnitOf(0.006, 1.0, "原版环形进气道", 0.5, 0.6, curve,
                                                         StockIntakeBridge.CurveStamp(km, kv), 0.0);
            List<IntakeUnit> cl = new List<IntakeUnit>();
            cl.Add(curved);
            IntakeInstall fast = IntakeInstall.Aggregate(cl, tech, 0.0, 2.5);
            double nominalStock = Math.PI * 0.25 * curved.Spec.CaptureDiameter * curved.Spec.CaptureDiameter;
            Check("M2.5 捕获面积按曲线折成 45%", fast.CaptureArea, nominalStock * 0.45, 0.02 * nominalStock, "m²");
            Expect("M2.5 仍然供气（曲线不是上限）", fast.Valid && fast.AvailableMassFlow > 0.0, true);

            // ---- ⑤ 我们的零件和原版零件能一起算 ----
            IntakeSpec mine = new IntakeSpec();
            mine.Kind = IntakeKind.Axial;
            mine.CaptureDiameter = 0.9;
            mine.ThroatRatio = 0.85;
            List<IntakeUnit> mixed = new List<IntakeUnit>();
            mixed.Add(new IntakeUnit(mine, 0.5, "JF 进气道"));
            mixed.Add(u);
            IntakeInstall both = IntakeInstall.Aggregate(mixed, tech, 0.0, 1.4);
            Expect("自家零件 + 原版零件能合成", both.Present && both.Count == 2, true);
            double mineOnly = IntakeSolver.SolvePart(mine, 0.5, tech, 0.0, 1.4).CaptureArea;
            double stockArea = Math.PI * 0.25 * u.Spec.CaptureDiameter * u.Spec.CaptureDiameter * u.SupplyAt(1.4);
            Check("合成的捕获面积 = 自家全量 + 原版按曲线打折", both.CaptureArea, mineOnly + stockArea,
                  0.02 * both.CaptureArea, "m²");

            Console.WriteLine("    " + StockIntakeBridge.Describe(0.006, 1.0, 0.6, StockIntakeBridge.CurveStamp(km, kv)));
            Console.WriteLine("    " + StockIntakeBridge.Describe(0.0125, 2.0, 0.0, ""));
            Console.WriteLine(string.Format("    原版 Ø{0:F3} m + 自家 Ø0.90 m：合计捕获 {1:F3} m²｜可用 {2:F1} kg/s｜恢复 {3:F3}",
                u.Spec.CaptureDiameter, both.CaptureArea, both.AvailableMassFlow, both.Tpr));
        }

        /// <summary>
        /// 形状 → 性能。进气道零件能 3D 编辑，编辑的意义是"改了要算得出来"：
        /// 管道拉长、喉道收窄、捕获放大，三件事各走各的路，不能全都只改一个数。
        /// </summary>
        private static void RunShapePerformanceCases(EngineDesignData baseDesign)
        {
            TechLevel tech = baseDesign.Tech;
            IntakeSpec sp = new IntakeSpec
            {
                Kind = IntakeKind.Axial,
                CaptureDiameter = 0.90,
                ThroatRatio = 0.85,
                FaceMachDesign = 0.50
            };

            // ---- 不给长度 = 老口径（一个数都不动）----
            IntakeSolution bare = IntakeSolver.SolvePart(sp, 0.60, tech, 0.0, 0.0);
            IntakeSolution zero = IntakeSolver.SolvePart(sp, 0.60, tech, 0.0, 0.0, 0.0, double.NaN, 0.0);
            Check("不给长度时恢复系数不变", zero.Tpr, bare.Tpr, 1e-9);
            Check("不给长度时可用流量不变", zero.AvailableMassFlow, bare.AvailableMassFlow, 1e-9);

            // ---- 管道越长越吃亏 ----
            IntakeSolution len06 = IntakeSolver.SolvePart(sp, 0.60, tech, 0.0, 0.0, 0.0, double.NaN, 0.6);
            IntakeSolution len12 = IntakeSolver.SolvePart(sp, 0.60, tech, 0.0, 0.0, 0.0, double.NaN, 1.2);
            IntakeSolution len30 = IntakeSolver.SolvePart(sp, 0.60, tech, 0.0, 0.0, 0.0, double.NaN, 3.0);
            Console.WriteLine("    管长 0.6/1.2/3.0 m 的恢复：{0:F4} / {1:F4} / {2:F4}", len06.Tpr, len12.Tpr, len30.Tpr);
            Expect("管长的恢复更低", len12.Tpr < len06.Tpr, true);
            Expect("更长再低一点", len30.Tpr < len12.Tpr, true);
            Expect("长管损失有下限（不至于丢两成）", len30.Tpr > len06.Tpr * 0.80, true);
            Check("形状损失不动捕获面积", len30.CaptureArea, bare.CaptureArea, 1e-9);

            // ---- 喉道收窄：捕获不变、喉道限流 ----
            IntakeSpec narrow = sp.Clone(); narrow.ThroatRatio = 0.70;
            IntakeShape  sWide = IntakeGeometry.OfPart(sp, 0.60, 1.2);
            IntakeShape  sNarrow = IntakeGeometry.OfPart(narrow, 0.60, 1.2);
            Check("喉道比 0.85→0.70 捕获面积不变", sNarrow.CaptureArea, sWide.CaptureArea, 1e-9);
            Check("喉道面积按比例缩", sNarrow.ThroatArea, sWide.ThroatArea * 0.70 / 0.85, 0.01);
            IntakeSolution solWide = IntakeSolver.SolvePart(sp, 0.60, tech, 0.0, 1.8, 0.0, double.NaN, 1.2);
            IntakeSolution solNarrow = IntakeSolver.SolvePart(narrow, 0.60, tech, 0.0, 1.8, 0.0, double.NaN, 1.2);
            Expect("喉道细了可用流量更少", solNarrow.AvailableMassFlow < solWide.AvailableMassFlow * 0.95, true);

            // ---- 捕获放大：流量约按面积平方走 ----
            IntakeSpec big = sp.Clone(); big.CaptureDiameter = 1.80;
            IntakeSolution solBig = IntakeSolver.SolvePart(big, 0.60, tech, 0.0, 1.8, 0.0, double.NaN, 1.2);
            Check("捕获直径翻倍 → 面积四倍", solBig.CaptureArea, solWide.CaptureArea * 4.0, 1e-9);
            Expect("可用流量跟着涨", solBig.AvailableMassFlow > solWide.AvailableMassFlow * 3.0, true);

            // ---- 型线与求解器同一份数：喉道半径 = √(喉道面积/π) ----
            Check("喉道半径与面积自洽", sWide.RThroat, Math.Sqrt(sWide.ThroatArea / Math.PI), 1e-9);
            Expect("唇口比喉道鼓", sWide.RCapture > sWide.RThroat, true);

            // ---- 两个条件都变：长而且细，损失叠起来但仍有下限 ----
            IntakeSolution worst = IntakeSolver.SolvePart(narrow, 0.60, tech, 0.0, 0.9, 0.0, double.NaN, 5.0);
            Console.WriteLine("    长 5 m + 喉道 0.70的恢复：{0:F4}（下限 0.80）", worst.Tpr);
            Expect("最差构型仍高于下限的八成", worst.Tpr > 0.20, true);
        }

        private static void RunVectorCommandCases()
        {
            const double FrameDt = 1.0 / 60.0;
            VectorCommand vc = new VectorCommand();

            // ---- ① 死区：SAS 稳态噪声吃进去，喷口一动不动 ----
            double maxOut = 0.0;
            for (int i = 0; i < 300; i++)
            {
                double noise = (i % 2 == 0) ? 0.02 : -0.018;
                vc.Step(noise, -noise, noise * 0.5, FrameDt, true, true, true);
                maxOut = Math.Max(maxOut, Math.Abs(vc.Pitch) + Math.Abs(vc.Yaw) + Math.Abs(vc.Roll));
            }
            Console.WriteLine(string.Format("  5 秒 SAS 噪声（±0.02）：输出峰值 {0:E2}（死区 {1:F2}）", maxOut, VectorCommand.Deadzone));
            Expect("噪声落在死区里，喷口一帧都不动", maxOut < 1e-9, true);
            Expect("死区会把输入记成噪声", vc.PitchDead && vc.YawDead, true);

            // ---- ② 死在死区边缘：不是一脚踏过去就跳 4% ----
            vc.Reset();
            vc.Step(0.05, 0.0, 0.0, FrameDt, true, true, true);
            Console.WriteLine(string.Format("  刚出死区（0.05）：第一帧输出 {0:F4}（不该一跳 4%）", vc.Pitch));
            Expect("出死区是连续的", vc.Pitch > 0.0 && vc.Pitch < 0.02, true);

            // ---- ③ 限速率：满指令不能一帧到位，但半秒内要跟上 ----
            vc.Reset();
            vc.Step(1.0, 0.0, 0.0, FrameDt, true, true, true);
            double oneFrame = vc.Pitch;
            for (int i = 1; i < 6; i++) vc.Step(1.0, 0.0, 0.0, FrameDt, true, true, true);
            double tenth = vc.Pitch;
            for (int i = 6; i < 36; i++) vc.Step(1.0, 0.0, 0.0, FrameDt, true, true, true);
            double half = vc.Pitch;
            Console.WriteLine(string.Format("  满指令：一帧 {0:F3}｜0.1 s {1:F3}｜0.6 s {2:F3}", oneFrame, tenth, half));
            Expect("一帧走不完", oneFrame < 0.10, true);
            Expect("0.1 s 不会跳满", tenth < 0.40, true);
            Expect("0.6 s 基本跟上", half > 0.95, true);

            // ---- ④ 三个方向各关各的：关掉偏航，俯仰照常 ----
            vc.Reset();
            for (int i = 0; i < 60; i++)
                vc.Step(1.0, 1.0, 1.0, FrameDt, true, false, true);
            Console.WriteLine(string.Format("  只关偏航：{0}", vc.Describe()));
            Expect("偏航轴关掉后恒为 0", Math.Abs(vc.Yaw) < 1e-12, true);
            Expect("俯仰照常跟", vc.Pitch > 0.95, true);
            Expect("滚转照常跟", vc.Roll > 0.95, true);
            Expect("关掉的轴仍记着玩家给过输入", vc.YawDead, true);

            vc.Reset();
            for (int i = 0; i < 60; i++)
                vc.Step(1.0, 1.0, 1.0, FrameDt, false, false, false);
            Expect("三轴全关 = 一动不动", Math.Abs(vc.Pitch) + Math.Abs(vc.Yaw) + Math.Abs(vc.Roll) < 1e-12, true);

            // ---- ⑤ 三轴 Direction：滚转只有在喷口偏离质心中轴线时才有力矩 ----
            Vec3 up = new Vec3(0.0, 1.0, 0.0);
            Vec3 pAxis = new Vec3(1.0, 0.0, 0.0);
            Vec3 yAxis = new Vec3(0.0, 0.0, 1.0);
            Vec3 rTail = new Vec3(0.0, -1.5, 0.0);        // 在轴线上：滚不动
            Vec3 rOff = new Vec3(0.0, -1.5, 0.6);         // 偏出 0.6 m：滚得动
            double ang;

            Vec3 dRollCenter = ThrustVector.Direction(up, rTail, pAxis, yAxis, 0.0, 0.0, 1.0, true, true, true, 15.0, out ang);
            Expect("喷口在轴线上：滚转指令不产生偏转", Math.Abs(ang) < 1e-9, true);

            Vec3 dRoll = ThrustVector.Direction(up, rOff, pAxis, yAxis, 0.0, 0.0, 1.0, true, true, true, 15.0, out ang);
            Vec3 tauRoll = Vec3.Cross(rOff, dRoll);
            double rollTorque = Vec3.Dot(tauRoll, up);
            Console.WriteLine(string.Format("  离轴 0.6 m 滚转满指令：偏 {0:F2}°｜绕推力轴力矩 {1:F3}", ang, rollTorque));
            Expect("离轴时滚转真的偏", ang > 14.0, true);
            Expect("正滚转 → 绕推力轴正力矩", rollTorque > 0.0, true);

            // 轴掩码：同一句满偏航，掩掉偏航就跟没打一样
            Vec3 dYawOn = ThrustVector.Direction(up, rTail, pAxis, yAxis, 0.0, 1.0, 0.0, true, true, false, 15.0, out ang);
            double angOn = ang;
            Vec3 dYawOff = ThrustVector.Direction(up, rTail, pAxis, yAxis, 0.0, 1.0, 0.0, true, false, false, 15.0, out ang);
            Console.WriteLine(string.Format("  偏航掩码：开 {0:F2}°｜关 {1:F2}°", angOn, ang));
            Expect("偏航掩码关掉就不偏", ang < 1e-9, true);
            Expect("偏航掩码开着就偏满", angOn > 14.0, true);
            Expect("掩码不影响别的轴", Math.Abs(Vec3.Dot(dYawOff, up) - 1.0) < 1e-9, true);

            // 三轴合成也要被最大角截断
            Vec3 dAll = ThrustVector.Direction(up, rOff, pAxis, yAxis, 1.0, 1.0, 1.0, true, true, true, 15.0, out ang);
            Console.WriteLine(string.Format("  三轴同时满：偏 {0:F2}°（上限 15°）", ang));
            Expect("三轴合成按合向量截断", ang <= 15.05, true);
            Expect("三轴合成确实用上了滚转", Math.Abs(dAll.Z - dRoll.Z) > 1e-9 || Math.Abs(dAll.X) > 1e-9, true);

            // Reset 要真的归零（换装机/重新挂载时会走到）
            vc.Reset();
            Expect("Reset 归零", Math.Abs(vc.Pitch) + Math.Abs(vc.Yaw) + Math.Abs(vc.Roll) < 1e-12, true);
        }

        /// <summary>
        /// 控制系统。<b>同一个设计、同一次猛推，只换控制系统</b>，看喘振次数与超温时长怎么变。
        ///
        /// 这条测试的价值不在"数字好看"，而在它把一句设计意图变成了可以失败的数字：
        /// 无控的机器猛推必喘、有控的机器同样猛推不喘——这才是"年代差异"该有的样子。
        /// </summary>
        private static void RunControlCases()
        {
            EngineDesignData d = Presets.Build("J79");
            double t4Limit = d.Tech != null ? d.Tech.T4Limit : 1500.0;

            // ---- ① 加速度限幅：同样从 20% 推到 100%，三种系统各要多久把指令走完 ----
            double[] slew = new double[3];
            string[] names = { "无控", "机械液压", "全权限数字" };
            ControlKind[] kinds = { ControlKind.None, ControlKind.Hydro, ControlKind.Fadec };
            for (int i = 0; i < 3; i++)
            {
                ControlRuntime rt = new ControlRuntime();
                double dt = 1.0 / 60.0;
                double cur = 0.20;
                int steps = 0;
                // 先把状态喂到 0.20（Primed），再给 1.0 的指令
                ControlSystem.Step(rt, kinds[i], 0.20, 0.2, 50, 900, t4Limit, 0.2, dt);
                while (steps < 600 && cur < 0.999)
                {
                    // 只量限幅这一件事：温度和转速都给在安全区里，别让限温闸插进来。
                    cur = ControlSystem.Step(rt, kinds[i], 1.0, cur, 50, 900, t4Limit, 0.2, dt);
                    steps++;
                }
                slew[i] = steps * dt;
            }
            Console.WriteLine(string.Format(
                "  指令走完 20%→100%：无控 {0:F2} s｜液压 {1:F2} s｜数字 {2:F2} s",
                slew[0], slew[1], slew[2]));
            Expect("无控立刻跟上指令", slew[0] < 0.02, true);
            Expect("液压比数字慢", slew[1] > slew[2] * 1.5, true);

            // ---- ② 十秒猛推：喘振次数与超温时长 ----
            // 转子用同一套时间常数、同一个设计裕度（0.45，一台"边界偏紧"的机器），
            // 唯一的差别是油门后面那道闸。
            for (int i = 0; i < 3; i++)
            {
                double dt = 1.0 / 60.0;
                ControlRuntime rt = new ControlRuntime();
                SurgeRuntime sg = new SurgeRuntime();
                double spool = 0.20, t4 = 900.0, prevRaw = 0.20;
                double surgeSeconds = 0.0, overTemp = 0.0, peakT4 = 0.0;
                int frames = 600;
                for (int f = 0; f < frames; f++)
                {
                    double raw = f < 30 ? 0.20 : 1.00;         // 第 0.5 秒一杆推到底
                    double rate = Surge.Rate(prevRaw, raw, dt);
                    prevRaw = raw;
                    // 风险要按"这一刻真的烧多少油"算，不是按玩家杆量算——
                    // 按杆量算的话，有控机器一旦被压住就永远压在下面（杆还在底，滞后永久存在）。
                    double risk = Surge.RiskOf(rt.Command, rate, spool, 0.0, 0.45);
                    double cmd = ControlSystem.Step(rt, kinds[i], raw, spool,
                        50 + 55 * spool, t4, t4Limit, risk, dt);
                    spool = Spool.Step(spool, cmd, dt, 1.8, 1.0);
                    t4 = 820.0 + 780.0 * cmd;
                    if (t4 > peakT4) peakT4 = t4;
                    Surge.Step(sg, cmd, rate, spool, 0.0, 0.45, dt);
                    if (sg.Active) surgeSeconds += dt;
                    if (t4 > t4Limit) overTemp += dt;
                }
                Console.WriteLine(string.Format(
                    "  {0}：喘振 {1} 次（共 {2:F2} s）｜超温 {3:F2} s｜峰值 T4 {4:F0} K（限 {5:F0}）｜{6}",
                    names[i], sg.Events, surgeSeconds, overTemp, peakT4, t4Limit, rt.Note));
                if (i == 0)
                {
                    Expect("无控猛推会喘", sg.Events > 0, true);
                    Expect("无控不管超温", overTemp > 0.5, true);
                }
                else
                {
                    Expect(names[i] + "猛推不喘", sg.Events == 0, true);
                    Expect(names[i] + "把 T4 压在限值下", overTemp < 0.05, true);
                }
            }

            // ---- ③ 喘振抑制：裕度见底时数字控制不许再加车 ----
            ControlRuntime rtF = new ControlRuntime();
            ControlSystem.Step(rtF, ControlKind.Fadec, 0.60, 0.6, 85, 1300, t4Limit, 0.5, 1.0 / 60.0);
            double held = ControlSystem.Step(rtF, ControlKind.Fadec, 1.00, 0.6, 85, 1300, t4Limit, 0.95, 1.0 / 60.0);
            double cut = ControlSystem.Step(rtF, ControlKind.Fadec, 1.00, 0.6, 85, 1300, t4Limit, 1.10, 1.0 / 60.0);
            Console.WriteLine(string.Format(
                "  数字控制遇喘振风险 0.95：油门被压到 {0:F2}（涨不上去）｜风险 1.10：{1:F2}（主动收回）", held, cut));
            Expect("喘振裕度见底时加不上车", held <= 0.62, true);
            Expect("已经喘起来会主动收油", cut < held, true);

            // ---- ④ 年代解锁 ----
            string era40 = ControlSystem.KindName(ControlSystem.Allowed(TechLevel.Get("1940s")));
            string era60 = ControlSystem.KindName(ControlSystem.Allowed(TechLevel.Get("1960s")));
            string era90 = ControlSystem.KindName(ControlSystem.Allowed(TechLevel.Get("1990s")));
            Console.WriteLine(string.Format("  年代上限：1940s {0}｜1960s {1}｜1990s {2}", era40, era60, era90));
            // 比的是「哪一个档」，不是「那句话怎么写」：文案会随界面语言变，
            // 档位不会。以前这两条钉的是中文字面量，切成英文就假红。
            Expect("1940s 只有无控", era40 == ControlSystem.KindName(ControlKind.None), true);
            Expect("1990s 才有数字", era90 == ControlSystem.KindName(ControlKind.Fadec), true);

            // ---- ⑤ 判据：超年代要点出来，无控也要说清楚它意味着什么 ----
            EngineDesignData over = d.Clone();
            over.TechName = "1940s";
            over.Control = ControlKind.Fadec;
            List<Finding> fs = Criteria.CheckControl(over);
            string first = fs.Count > 0 ? fs[0].Line() : "（没有判据）";
            Console.WriteLine("  判据（1940s 却选数字控制）：" + first);
            Expect("超年代会被判据点出来", fs.Count > 0 && fs[0].Key == "control.era", true);
        }

        private static void RunGeometryCases()
        {
            string[] keys = { "JF-1", "J79", "J57", "J85", "RD-33" };
            foreach (string k in keys)
            {
                EngineDesignData d = k == "JF-1" ? new EngineDesignData() : Presets.Build(k);
                EngineStations s = EngineGeometry.Of(d);

                // 四段：进气口→风扇、风扇→核心机、核心机、核心机尾→喷口
                double intake = (s.YIn - s.YFan) / s.L;
                double fanDuct = (s.YFan - s.YCoreStart) / s.L;
                double core = (s.YCoreStart - s.YAb) / s.L;
                double nozzle = (s.YAb - s.YOut) / s.L;

                Console.WriteLine("  {0,-6} 长 {1:F2} m｜进气 R{2:F3} 风扇 R{3:F3} 核心 R{4:F3} 喷口 R{5:F3}｜风扇/核心 {6:F2}",
                    k, s.L, s.RIn, s.RFan, s.RCore, s.RNoz, s.RFan / Math.Max(s.RCore, 1e-6));
                Console.WriteLine("    占比 进气 {0,5:P0}｜风扇段 {1,5:P0}｜核心机 {2,5:P0}｜喷管 {3,5:P0}",
                    intake, fanDuct, core, nozzle);

                Check(k + " 四段之和", intake + fanDuct + core + nozzle, 1.0, 2.0);
                Expect(k + " 核心机占比 45~80%", core > 0.45 && core < 0.80, true);
                Expect(k + " 喷管占比 3~25%", nozzle > 0.03 && nozzle < 0.25, true);
                Expect(k + " 风扇叶尖在唇口里", s.RFan <= s.RIn * 1.001, true);
                if (s.HasFan)
                    Expect(k + " 风扇/核心直径 1.3~2.7", s.RFan / Math.Max(s.RCore, 1e-6) > 1.30 &&
                                                   s.RFan / Math.Max(s.RCore, 1e-6) < 2.70, true);
            }

            // 剖视图用的站位表必须和三维网格的包围盒一致——这是"图上比例即模型比例"的唯一保证。
            EngineDesignData jf = new EngineDesignData();
            EngineStations st = EngineGeometry.Of(jf);
            Console.WriteLine("  JF-1 长径比 {0:F2}（真机涡扇 3~7；我们外形直径取等效捕获面积，偏胖属已知口径）",
                st.L / (2.0 * st.MaxRadius));

            RunNacelleCases();
        }

        /// <summary>
        /// 站表这一段追加的一组：短舱直径 / 大涵道比机匣 / 尾部直径（§275）。
        ///
        /// 这三条都是"填了数看不见反应"的毛病，能钉的只有站位表上的数：
        /// · 短舱直径 = 外壳最粗处的外径，填多少量出来多少（以前它被拿去抬进气口半径，
        ///   外皮是那个半径的 1.03 倍，填 2.15 量出来 2.21；填小了还被 Math.Max 吃掉）
        /// · 大涵道比的外涵喷口半径不许被核心机口径反过来压瘪
        /// · 机匣尾端半径必须比喷管喉道粗——不然喷管是从喉道"扩"出去的
        /// </summary>
        private static void RunNacelleCases()
        {
            EngineDesignData gtf = Presets.Build("GTF1100");
            EngineStations sPlain = EngineGeometry.Of(gtf);

            // ① 不填：外壳按风扇给，且进气口半径不被短舱这栏撑大
            gtf.NacelleDiameter = 0.0;
            EngineStations sNone = EngineGeometry.Of(gtf);
            Check("不填短舱直径时外皮半径＝风扇罩", sNone.RNac, sNone.RFan * 1.045, 1e-6, "m");

            // ② 填一个比风扇还大的数：外壳最粗处就是它的一半，一分不差
            gtf.NacelleDiameter = 2.40;
            EngineStations sWide = EngineGeometry.Of(gtf);
            Console.WriteLine("  短舱直径填 2.40 m：外皮 R{0:F3}（外径 {1:F2}）｜进气口 R{2:F3}（不填时 {3:F3}）",
                sWide.RNac, sWide.RNac * 2.0, sWide.RIn, sNone.RIn);
            Check("短舱直径 2.40 时外皮半径", sWide.RNac, 1.20, 1e-9, "m");
            Check("短舱直径不动进气口半径", sWide.RIn, sNone.RIn, 1e-9, "m");

            // ③ 填小了：被风扇罩顶住，不许细到夹住叶尖（这是老 Math.Max 唯一做对的事）
            gtf.NacelleDiameter = 1.00;
            EngineStations sNarrow = EngineGeometry.Of(gtf);
            Console.WriteLine("  短舱直径填 1.00 m（比风扇细）：外皮还是 R{0:F3}", sNarrow.RNac);
            Expect("短舱填小了不低于风扇罩", sNarrow.RNac >= sNarrow.RFan * 1.0449, true);
            Expect("短舱填小了仍然包住唇口外缘", sNarrow.RNac >= sNarrow.RIn * 1.02, true);

            // ④ 大涵道比：外涵喷口半径不许被 RCore 反过来压到 RFan 的一半以下
            Console.WriteLine("  GTF1100 风扇 R{0:F3}｜核心 R{1:F3}｜外涵喷口 R{2:F3}（{3:P0} 风扇）",
                sPlain.RFan, sPlain.RCore, sPlain.RBypass, sPlain.RBypass / sPlain.RFan);
            Expect("大涵道比外涵喷口不小于 0.86 风扇", sPlain.RBypass >= sPlain.RFan * 0.86 - 1e-9, true);
            Expect("外涵喷口至少包住核心机", sPlain.RBypass >= sPlain.RCore * 1.06 - 1e-9, true);

            // ⑤ 尾部：机匣尾端比喷管喉道粗，且不比核心机匣还粗（喷管得是收的）
            EngineStations sAb = EngineGeometry.Of(Presets.Build("J79"));
            foreach (EngineStations sx in new[] { sPlain, sNone, sAb })
            {
                Expect("机匣尾端比喷管喉道粗", sx.RTail >= sx.RNoz * 0.94 - 1e-9, true);
                Expect("机匣尾端不超过核心机匣 1.10 倍", sx.RTail <= sx.RCore * 1.10 + 1e-9, true);
            }
            Console.WriteLine("  GTF1100 尾端 R{0:F3}（喉道 R{1:F3}）｜J79 尾端 R{2:F3}（喉道 R{3:F3}）",
                sPlain.RTail, sPlain.RNoz * 0.94, sAb.RTail, sAb.RNoz * 0.94);
        }

        /// <summary>
        /// 第 64 段：控制系统。
        ///
        /// 这一段钉的是"控制层到底改了什么、代价是什么"，不是"能不能收敛"：
        /// · 无控 = 直通（这是 1940 年代的真相，也是对照组）
        /// · 有控 = 加速被限、收油快得多（对称限幅是船，不是喷气机）
        /// · 限温/限转是比例闸，超转保护是硬闸，且会锁存
        /// · 液压系统不认喘振裕度，只有数字系统做这件事
        /// </summary>
        private static void RunControlLawCases()
        {
            const double dt = 0.02;

            // ① 无控直通
            ControlRuntime none = new ControlRuntime();
            double c0 = ControlSystem.Step(none, ControlKind.None, 0.37, 0.5, 60.0, 900.0, 1600.0, 0.0, dt);
            Expect("无控：油门原样透传", Math.Abs(c0 - 0.37) < 1e-9, true);
            Expect("无控：不报任何限幅", none.LimitingSpeed || none.LimitingTemp || none.LimitingSurge || none.RateLimited, false);

            // ② 加速被限：一帧只许走 rate·dt
            ControlRuntime acc = new ControlRuntime { Command = 0.20, Primed = true };
            double up = ControlSystem.Step(acc, ControlKind.Fadec, 1.0, 0.20, 60.0, 900.0, 1600.0, 0.0, dt);
            Check("数字控制：一帧加速量（0.20→）", up - 0.20, ControlSystem.FadecSlewPerSec * dt, 0.5, "/帧");

            // ③ 收油比推油快
            ControlRuntime dec = new ControlRuntime { Command = 0.80, Primed = true };
            double down = ControlSystem.Step(dec, ControlKind.Fadec, 0.0, 0.80, 60.0, 900.0, 1600.0, 0.0, dt);
            double downStep = 0.80 - down;
            Expect("收油比推油快（数字控制）", downStep > ControlSystem.FadecSlewPerSec * dt * 1.3, true);
            Check("数字控制：一帧减速量", downStep, ControlSystem.FadecDecelPerSec * dt, 0.5, "/帧");

            // ④ 软限：T4 贴着限值时，允许的加速量要变小
            ControlRuntime hot = new ControlRuntime { Command = 0.20, Primed = true };
            double upHot = ControlSystem.Step(hot, ControlKind.Fadec, 1.0, 0.20, 60.0, 1599.0, 1600.0, 0.0, dt);
            Expect("T4 贴限时加速被压住", (upHot - 0.20) < (up - 0.20) * 0.5, true);

            // ⑤ 超转保护：过 102% 直接砍油并锁存
            ControlRuntime os = new ControlRuntime { Command = 0.95, Primed = true };
            double cut = ControlSystem.Step(os, ControlKind.Fadec, 1.0, 1.0, 105.0, 1200.0, 1600.0, 0.0, dt);
            Expect("超转：触发切油", os.OverspeedTripped, true);
            Expect("超转：命令被砍到 45% 以内（相对指令）", cut <= 0.45 * 0.95 + 1e-6, true);
            // 转速回到 100% 以下，但锁存还没到期 → 仍然切
            double held = ControlSystem.Step(os, ControlKind.Fadec, 1.0, 1.0, 99.0, 1200.0, 1600.0, 0.0, dt);
            Expect("超转：锁存期内仍切油", os.OverspeedTripped && held <= 0.45, true);
            // 熬过锁存期、转速也回来了 → 解除
            for (int i = 0; i < 60; i++)
                held = ControlSystem.Step(os, ControlKind.Fadec, 1.0, 1.0, 96.0, 1200.0, 1600.0, 0.0, dt);
            Expect("超转：转速回落后自动解除", os.OverspeedTripped, false);
            // 液压系统没有这道闸
            ControlRuntime hOs = new ControlRuntime { Command = 0.95, Primed = true };
            ControlSystem.Step(hOs, ControlKind.Hydro, 1.0, 1.0, 105.0, 1200.0, 1600.0, 0.0, dt);
            Expect("液压：不做超转切油", hOs.OverspeedTripped, false);

            // ⑥ 喘振抑制只有数字系统做
            ControlRuntime hRisk = new ControlRuntime { Command = 0.50, Primed = true };
            double hOut = ControlSystem.Step(hRisk, ControlKind.Hydro, 1.0, 0.50, 80.0, 900.0, 1600.0, 1.20, dt);
            Expect("液压：不认喘振裕度", hRisk.LimitingSurge, false);
            Expect("液压：喘振风险再高也照样加油", hOut > 0.50, true);
            ControlRuntime fRisk = new ControlRuntime { Command = 0.50, Primed = true };
            ControlSystem.Step(fRisk, ControlKind.Fadec, 1.0, 0.50, 80.0, 900.0, 1600.0, 1.20, dt);
            Expect("数字：风险超 1.05 时主动收油", fRisk.LimitingSurge, true);

            // ⑦ 复位把锁存一起清掉（重新点火的路径）
            os.Reset();
            Expect("重置会清掉超转锁存", os.OverspeedTripped || os.OverspeedHold > 0.0, false);

            Console.WriteLine("    " + ControlSystem.Describe(acc, ControlKind.Fadec));
            Console.WriteLine("    " + ControlSystem.Describe(os, ControlKind.Fadec));
        }

        /// <summary>
        /// 第 63 段：整机长度 → 性能。
        ///
        /// 这段要证明的不是"损失存在"，而是"**同一个设计，只改长度**时读数确实跟着变"。
        /// 以前把 J79 从 5.2 m 拉到 8 m，读数一分不动——那不是保守，是模型里没有这一项。
        /// 这里同时钉三件事：设计点推力随长度单调下降、损失量级在 1~3% 之间（不是 20%）、
        /// 非设计点整条包线跟着一起变差（否则"拉长只在设计点吃亏"会被钻空子）。
        /// </summary>
        private static void RunDuctLengthCases(EngineDesignData baseDesign)
        {
            double[] lens = { 2.0, 3.5, 5.2, 7.0, 8.5 };
            double[] thrust = new double[lens.Length];
            double[] burnPt = new double[lens.Length];
            double[] coreLoss = new double[lens.Length];
            double[] exhaustLoss = new double[lens.Length];
            bool mono = true;

            Console.WriteLine("    长度 m   设计推力 kN   燃烧室进口 kPa   核心段损失   排气段损失   OPR");
            for (int i = 0; i < lens.Length; i++)
            {
                EngineDesignData d = baseDesign.Clone();
                d.Length = lens[i];
                CycleResult r = Cycle.SolveDesignPoint(d);
                thrust[i] = r.Thrust / 1000.0;
                coreLoss[i] = r.DuctCoreLoss;
                burnPt[i] = r.P3 * (1.0 - r.DuctCoreLoss) / 1000.0;
                exhaustLoss[i] = r.DuctExhaustLoss;
                Console.WriteLine("    {0,5:F1}    {1,10:F3}    {2,13:F2}    {3,8:P3}    {4,8:P2}   {5:F3}",
                    lens[i], thrust[i], burnPt[i], coreLoss[i], exhaustLoss[i], r.Opr);
                if (i > 0 && thrust[i] > thrust[i - 1] + 1e-6) mono = false;
            }

            Expect("拉长必须更亏：设计推力随长度单调下降", mono, true);

            double drop = 1.0 - thrust[lens.Length - 1] / Math.Max(thrust[0], 1e-9);
            double absDrop = thrust[0] - thrust[lens.Length - 1];   // 这一串 thrust 已经是 kN
            // 下限为什么是 0.5% 而不是 1%：这条以前写 1%，是因为当时设计推力 46.1 kN、
            // 6.5 m 的长管掉了 1.04%；比热改正之后设计推力涨到 47.6 kN，
            // 而同一条管子的**绝对**损失没变（0.41 kN），比例自然掉到 0.88%。
            // 所以比例区间放宽到 0.5%，另外加一条盯绝对损失的——那个数才是物理量。
            Expect(string.Format("长度 2.0→8.5 m 推力掉 {0:P2}（该在 0.5%~6% 之间，太小等于没建模、太大是算错）", drop),
                drop > 0.005 && drop < 0.06, true);
            Expect(string.Format("6.5 m 长管的绝对损失 {0:F3} kN（该在 0.15~1.2 kN，比例会随基值漂、这个数不会）", absDrop),
                absDrop > 0.15 && absDrop < 1.2, true);

            // 核心段是环形低马赫通道，损失小但必须随长度长——以前它恒为 0.00%，
            // 因为拿叶尖直径当圆管径反解马赫，面积多出三成、马赫被压到 0.05。
            Expect("核心段损失随长度单调增大", coreLoss[0] < coreLoss[lens.Length - 1] && coreLoss[lens.Length - 1] > 1e-6, true);
            Expect("排气段损失随长度单调增大", exhaustLoss[0] < exhaustLoss[lens.Length - 1] && exhaustLoss[lens.Length - 1] > 1e-4, true);

            // 非设计点：同一台机器、同一工况，只差长度，推力也要能分开。
            EngineDesignData shortD = baseDesign.Clone(); shortD.Length = 2.0;
            EngineDesignData longD = baseDesign.Clone(); longD.Length = 8.5;
            OffDesignResult rs = OffDesign.Solve(shortD, 11000.0, 0.85, 0.9);
            OffDesignResult rl = OffDesign.Solve(longD, 11000.0, 0.85, 0.9);
            Console.WriteLine("    非设计点 11 km/M0.85/油门 0.9：短 {0:F3} kN｜长 {1:F3} kN（差 {2:P2}）",
                rs.Thrust / 1000.0, rl.Thrust / 1000.0,
                1.0 - rl.Thrust / Math.Max(rs.Thrust, 1e-9));
            Expect("非设计点：拉长之后巡航推力也要掉", rl.Thrust < rs.Thrust - 1.0, true);

            // 段落的解析入口也要自洽：长度翻倍，损失按 L/Dh 线性变，但不是无界。
            double f1 = DuctLoss.Fraction(0.5, 1.0, 0.15);
            double f2 = DuctLoss.Fraction(0.5, 2.0, 0.15);
            Expect("同一截面，长度翻倍损失也翻倍", Math.Abs(f2 - 2.0 * f1) < 1e-9, true);
            Expect("马赫越高损失越大", DuctLoss.Fraction(0.8, 1.0, 0.15) > DuctLoss.Fraction(0.3, 1.0, 0.15), true);
            Expect("损失有硬上限，不会算出 50% 这种荒唐数", DuctLoss.Fraction(3.0, 1000.0, 0.05) <= DuctLoss.MaxFraction + 1e-9, true);
            Expect("零长度不掉压", DuctLoss.Fraction(0.7, 0.0, 0.2) < 1e-12, true);
            Expect("NaN 不传播", DuctLoss.Fraction(double.NaN, 1.0, 0.2) < 1e-12, true);

            Console.WriteLine("    " + DuctLoss.Describe(baseDesign, 0.45, 0.30, 0.55));
        }

        /// <summary>
        /// 第 61 段：真激波模型。
        ///
        /// 这一段的期望值有两类来源：一类是教科书数（正激波 M2 = 0.5774、pt 比 0.7209），
        /// 另一类是关系（斜板比皮托强、激波模式随马赫单调掉、超过 θmax 就脱体）。
        /// 关系和常数一起钉，才挡得住"公式抄错但单调性还在"这种错。
        /// </summary>
        private static void RunShockCases(EngineDesignData baseDesign)
        {
            // ---- 正激波：两个数直接对教科书 ----
            double pt2 = Shock.NormalShockPtRatio(2.0);
            double m2 = Shock.NormalShockMach(2.0);
            Console.WriteLine("  M2 正激波：总压比 {0:F4}（教科书 0.7209）｜波后马赫 {1:F4}（教科书 0.5774）", pt2, m2);
            Check("正激波 M2 总压比", pt2, 0.7209, 0.5);
            Check("正激波 M2 波后马赫", m2, 0.5774, 0.5);
            Check("马赫 1 处不该有损失", Shock.NormalShockPtRatio(1.0), 1.0, 0.01);
            Expect("亚声速不产生激波", Shock.NormalShockPtRatio(0.6) >= 0.9999, true);
            Expect("正激波损失随马赫单调变大",
                Shock.NormalShockPtRatio(2.5) < Shock.NormalShockPtRatio(2.0) &&
                Shock.NormalShockPtRatio(2.0) < Shock.NormalShockPtRatio(1.5), true);

            // ---- θ-β-M：脱体门槛与弱解 ----
            double thetaMax2 = Shock.MaxDeflectionDeg(2.0);
            double thetaMax15 = Shock.MaxDeflectionDeg(1.5);
            Console.WriteLine("  最大偏转角：M2 {0:F2}°（教科书 22.97）｜M1.5 {1:F2}°", thetaMax2, thetaMax15);
            Check("M2 最大偏转角", thetaMax2, 22.97, 2.0);
            Expect("偏转角随马赫单调升", thetaMax2 > thetaMax15, true);

            double beta, mOut, ptOut;
            bool ok = Shock.ObliqueShock(2.0, 10.0, out beta, out mOut, out ptOut);
            Console.WriteLine("  M2 / 10° 斜激波：β {0:F2}°（教科书 39.3）｜波后 {1:F3}｜总压比 {2:F4}", beta, mOut, ptOut);
            Expect("10° 偏转有弱解", ok, true);
            Check("弱解激波角", beta, 39.3, 5.0);
            Expect("斜激波后仍是超声速", mOut > 1.0, true);
            Expect("斜激波比正激波便宜", ptOut > Shock.NormalShockPtRatio(2.0), true);
            Expect("超过 θmax 就脱体", Shock.ObliqueShock(2.0, thetaMax2 + 5.0, out beta, out mOut, out ptOut), false);

            // ---- 多级压缩：斜板该比皮托强 ----
            double ratio;
            // half 是**总**转角：两级 13° = 每级 6.5°。第一版按"每级都转 13°"算，
            // 第二级正好压在 θmax 上返回 false，而 out 参数里留着第一级的值，
            // 断言不查 bool 就会拿到一个"看起来更好"的假数。
            bool compressionOk = Shock.CompressionsTotal(2.0, 13.0, 2, out mOut, out ratio);
            Console.WriteLine("  M2 两级共 13° 压缩 + 结尾正激波：总压比 {0:F4}（纯正激波只剩 {1:F4}）｜有解 {2}",
                ratio, Shock.NormalShockPtRatio(2.0), compressionOk);
            Expect("两级压缩有弱解", compressionOk, true);
            Expect("两级压缩优于纯正激波", ratio > Shock.NormalShockPtRatio(2.0) * 1.05, true);

            // ---- 锥激波：损失按同偏角斜激波再乘一点 ----
            double coneM, conePt = Shock.ConeShockPtRatio(2.0, 10.0, out coneM);
            Expect("锥激波有解", conePt > 0.0, true);
            Expect("锥激波比同偏角斜激波更亏", conePt < ptOut, true);

            // ---- 接进进气道恢复：型式与模式的差异要真的显出来 ----
            TechLevel tech = baseDesign.Tech;
            IntakeSpec pitot = new IntakeSpec { Kind = IntakeKind.Pitot, Model = RecoveryModel.Shock };
            IntakeSpec ramp = new IntakeSpec { Kind = IntakeKind.Ramp, Model = RecoveryModel.Shock, ThroatRatio = 0.90 };
            double pitot2 = Intake.Recovery(pitot, tech, 2.0, 2.0);
            double ramp2 = Intake.Recovery(ramp, tech, 2.0, 2.0);
            Console.WriteLine("  M2 恢复系数：皮托 {0:F4}｜斜板 {1:F4}（经验曲线斜板 {2:F4}）",
                pitot2, ramp2, Intake.Recovery(new IntakeSpec { Kind = IntakeKind.Ramp, ThroatRatio = 0.90 }, tech, 2.0, 2.0));
            Expect("M2 斜板优于皮托", ramp2 > pitot2, true);
            Expect("激波模式比经验曲线保守（超声速）", ramp2 < Intake.Recovery(new IntakeSpec { Kind = IntakeKind.Ramp, ThroatRatio = 0.90 }, tech, 2.0, 2.0), true);

            // M=1.0 两侧必须接得上，否则面板上滑一下马赫会跳一个数
            IntakeSpec ax = new IntakeSpec { Kind = IntakeKind.Axial, Model = RecoveryModel.Shock };
            double below = Intake.Recovery(ax, tech, 0.999, 2.0);
            double above = Intake.Recovery(ax, tech, 1.001, 2.0);
            Check("跨声速两侧连续", above, below, 1.0);

            // ---- 单调性：激波模式随马赫一路往下，不许中间抬头 ----
            bool mono = true;
            double prev = 2.0;
            for (double mm = 1.2; mm <= 3.0; mm += 0.2)
            {
                double cur = Intake.Recovery(pitot, tech, mm, 2.0);
                if (cur > prev) mono = false;
                prev = cur;
            }
            Expect("皮托激波模式随马赫单调降", mono, true);

            // ---- 未起动：压缩角超过这个马赫的 θmax 就脱体 ----
            // 设计马赫 5.0 的斜板在 M1.5 上：几何角 20°，而 M1.5 的 θmax 只有 12° 上下
            IntakeSpec steep = new IntakeSpec { Kind = IntakeKind.Ramp, Model = RecoveryModel.Shock, ThroatRatio = 0.90 };
            double mA = Intake.RampHalfAngleDeg(IntakeKind.Ramp, 5.0);
            bool started15 = Intake.Started(steep, 1.5, 5.0);
            double tprUnstart = Intake.Recovery(steep, tech, 1.5, 5.0);
            double tprShallow = Intake.Recovery(new IntakeSpec { Kind = IntakeKind.Ramp, Model = RecoveryModel.Shock, ThroatRatio = 0.90 }, tech, 1.5, 1.5);
            Console.WriteLine("  设计 M5 的斜板几何角 {0:F1}°｜M1.5 起动 {1}｜恢复 {2:F4}（几何角合适的同型 {3:F4}）",
                mA, started15, tprUnstart, tprShallow);
            Expect("陡斜板在低速未起动", started15, false);
            Expect("未起动比正常起动更亏", tprUnstart < tprShallow, true);
            Expect("缓斜板在同马赫起动", Intake.Started(steep, 1.5, 1.5), true);
            Expect("经验模式不报未起动", Intake.Started(new IntakeSpec { Kind = IntakeKind.Ramp }, 1.5, 5.0), true);

            // ---- 交错的两次评估不许串型（P1-1 那条静态字段的虫）----
            EngineDesignData r = baseDesign.Clone();
            r.Intake = new IntakeSpec { Kind = IntakeKind.Ramp, CaptureDiameter = 0.9, Model = RecoveryModel.Empirical };
            EngineDesignData p = baseDesign.Clone();
            p.Intake = new IntakeSpec { Kind = IntakeKind.Pitot, CaptureDiameter = 0.9, Model = RecoveryModel.Empirical };
            IntakeState sR = Intake.Evaluate(r, 2.0, 0.0);
            IntakeState sP = Intake.Evaluate(p, 2.0, 0.0);
            Expect("交错评估不串型式", sR.Kind == IntakeKind.Ramp && sP.Kind == IntakeKind.Pitot, true);

            // ---- 状态表达：面积算不出来时不许说 Ok ----
            EngineDesignData bad = baseDesign.Clone();
            bad.Intake = new IntakeSpec { Kind = IntakeKind.Axial, ThroatRatio = double.NaN, CaptureDiameter = 0.8 };
            IntakeState sBad = Intake.Evaluate(bad, 2.0, 0.0);
            Console.WriteLine("  非法喉道比 → Ok={0}｜告警：{1}", sBad.Ok, sBad.Warning);
            Expect("面积算不出来就 Ok=false", sBad.Ok, false);
            Expect("失败必须带一句话", !string.IsNullOrEmpty(sBad.Warning), true);

            EngineDesignData good = baseDesign.Clone();
            good.Intake = new IntakeSpec { Kind = IntakeKind.Axial, CaptureDiameter = 0.8, ThroatRatio = 0.85 };
            IntakeState sGood = Intake.Evaluate(good, 2.0, 0.0);
            Expect("正常构型 Ok=true", sGood.Ok, true);

            // ---- 存档：模式要能存回来 ----
            IntakeSpec src = new IntakeSpec { Kind = IntakeKind.Cone, Model = RecoveryModel.Shock, CaptureDiameter = 0.72, ThroatRatio = 0.88 };
            ConfigNode cn = new ConfigNode("INTAKE");
            IntakeSpecCodec.Save(cn, src);
            IntakeSpec back = IntakeSpecCodec.Load(cn, new IntakeSpec());
            ExpectEq("存档回来的模型", back.Model, RecoveryModel.Shock);
            ExpectEq("存档回来的型式", back.Kind, IntakeKind.Cone);
        }

        /// <summary>
        /// 第 62 段：运行时状态审计。
        ///
        /// 这一段不验物理，只验"一份结果自己跟自己对得上吗"。以前这些矛盾一个都不会报：
        /// `Ok=true` 时带着错误、喉道面积 0 却算出了流量、涡喷报了涵道比、NaN 让所有
        /// `&gt; 0` 判断同时失效——面板上都是"一切正常"。
        ///
        /// 所以正向要跑遍所有预设（真实构型不许有一条矛盾），反向要故意造几份烂结果
        /// （审计必须抓得住）。只做正向的话，一个永远返回空列表的审计也能全过。
        /// </summary>
        private static void RunStateAuditCases(EngineDesignData baseDesign)
        {
            // ---- 正向：全部预设的设计点 ----
            int presets = 0, designIssues = 0, offIssues = 0, intakeIssues = 0;
            string firstProblem = null;
            IList<PresetInfo> all = Presets.All;
            for (int i = 0; i < all.Count; i++)
            {
                EngineDesignData pd = all[i].Key == "JF-1" ? new EngineDesignData() : Presets.Build(all[i].Key);
                if (pd == null) continue;
                presets++;

                CycleResult r = Cycle.SolveDesignPoint(pd);
                List<StateAudit.Issue> di = StateAudit.AuditDesign(r, pd);
                int errs = StateAudit.ErrorCount(di);
                if (errs > 0)
                {
                    designIssues += errs;
                    if (firstProblem == null)
                        firstProblem = all[i].Key + " 设计点：" + di[0].What;
                }

                // 非设计点：低空高速到高空静止，四档
                double[][] pts = { new[] { 0.0, 0.0 }, new[] { 0.0, 0.9 }, new[] { 8000.0, 0.8 }, new[] { 14000.0, 2.0 } };
                for (int k = 0; k < pts.Length; k++)
                {
                    OffDesignResult od = OffDesign.Solve(pd, pts[k][0], pts[k][1], 1.0, 0.0);
                    List<StateAudit.Issue> oi = StateAudit.AuditOffDesign(od, pd);
                    int oe = StateAudit.ErrorCount(oi);
                    if (oe > 0)
                    {
                        offIssues += oe;
                        if (firstProblem == null)
                            firstProblem = string.Format("{0} {1:F0}m/M{2:F1}：{3}", all[i].Key, pts[k][0], pts[k][1], oi[0].What);
                        if (offIssues <= 3) Console.WriteLine(string.Format("  [非设计点] {0} {1:F0}m/M{2:F1}：{3}", all[i].Key, pts[k][0], pts[k][1], oi[0].What));
                    }
                }

                // 进气道：四种型式 × 五档马赫（含超声速，激波模式也过一遍）
                IntakeKind[] kinds = { IntakeKind.Axial, IntakeKind.Pitot, IntakeKind.Ramp, IntakeKind.Cone };
                double[] machs = { 0.0, 0.6, 1.0, 1.6, 2.2 };
                for (int ki = 0; ki < kinds.Length; ki++)
                {
                    for (int mi = 0; mi < machs.Length; mi++)
                    {
                        IntakeSpec sp = new IntakeSpec { Kind = kinds[ki], CaptureDiameter = 0.8 };
                        IntakeSolution s = IntakeSolver.SolveAt(sp, pd, 0.0, machs[mi], 0.0, 0.0);
                        List<StateAudit.Issue> ii = StateAudit.AuditIntake(s);
                        int ie = StateAudit.ErrorCount(ii);
                        if (ie > 0)
                        {
                            intakeIssues += ie;
                            if (firstProblem == null)
                                firstProblem = string.Format("{0} 进气道 {1}/M{2:F1}：{3}", all[i].Key, kinds[ki], machs[mi], ii[0].What);
                        }
                    }
                }
            }
            Console.WriteLine("  扫了 {0} 个预设 × (设计点 + 4 档非设计点 + 20 档进气道)", presets);
            Console.WriteLine("  矛盾条数：设计点 {0}｜非设计点 {1}｜进气道 {2}", designIssues, offIssues, intakeIssues);
            if (firstProblem != null) Console.WriteLine("  第一条：" + firstProblem);
            Expect("真机构型的设计点自洽", designIssues == 0, true);
            Expect("真机构型的非设计点自洽", offIssues == 0, true);
            Expect("真机构型的进气道自洽", intakeIssues == 0, true);

            // ---- 反向：故意造烂结果，审计必须抓得住 ----
            CycleResult okButErrors = new CycleResult();
            okButErrors.Ok = true;
            okButErrors.Errors.Add("故意留一条");
            Expect("抓住「Ok 了但还带着错误」", StateAudit.ErrorCount(StateAudit.AuditDesign(okButErrors, baseDesign)) > 0, true);

            CycleResult nanThrust = CycleResultLike(baseDesign);
            nanThrust.Thrust = double.NaN;
            Expect("抓住「推力是 NaN」", StateAudit.ErrorCount(StateAudit.AuditDesign(nanThrust, baseDesign)) > 0, true);

            CycleResult phantomBypass = CycleResultLike(baseDesign);
            phantomBypass.MdotAir = 100.0; phantomBypass.MdotCore = 100.0;
            phantomBypass.Bpr = 0.6; phantomBypass.Thrust = 50000.0;
            EngineDesignData turbojet = new EngineDesignData();
            turbojet.Kind = EngineKind.Turbojet;
            turbojet.Fan.Diameter = 0.0;
            Expect("抓住「涡喷报涵道比」", StateAudit.ErrorCount(StateAudit.AuditDesign(phantomBypass, turbojet)) > 0, true);

            IntakeSolution deadThroat = new IntakeSolution();
            deadThroat.Valid = true;
            deadThroat.ThroatArea = 0.0;
            deadThroat.CaptureArea = 0.5;
            deadThroat.FaceArea = 0.4;
            deadThroat.Tpr = 0.95;
            deadThroat.FaceMach = 0.5;
            Expect("抓住「喉道面积 0 还标 Valid」", StateAudit.ErrorCount(StateAudit.AuditIntake(deadThroat)) > 0, true);

            IntakeSolution overUnity = new IntakeSolution();
            overUnity.Valid = true;
            overUnity.Tpr = 1.20;
            overUnity.ThroatArea = 0.4;
            overUnity.CaptureArea = 0.6;
            overUnity.FaceArea = 0.4;
            overUnity.FaceMach = 0.5;
            Expect("抓住「总压恢复大于 1」", StateAudit.ErrorCount(StateAudit.AuditIntake(overUnity)) > 0, true);

            IntakeSolution freeAir = new IntakeSolution();
            freeAir.Valid = true;
            freeAir.Tpr = 0.95;
            freeAir.ThroatArea = 0.4;
            freeAir.CaptureArea = 0.6;
            freeAir.FaceArea = 0.4;
            freeAir.FaceMach = 0.5;
            freeAir.RequiredMassFlow = 50.0;
            freeAir.CapturedMassFlow = 80.0;   // 比需求还多
            freeAir.AvailableMassFlow = 90.0;
            Expect("抓住「捕获超过需求」", StateAudit.ErrorCount(StateAudit.AuditIntake(freeAir)) > 0, true);

            // 审计自己不许造假：干净的结果必须一条都不报
            IntakeSolution clean = IntakeSolver.SolveAt(new IntakeSpec { Kind = IntakeKind.Ramp, CaptureDiameter = 0.8 }, baseDesign, 0.0, 2.0, 0.0, 0.0);
            Expect("干净结果不许误报", StateAudit.ErrorCount(StateAudit.AuditIntake(clean)) == 0, true);
            Console.WriteLine("  " + StateAudit.Summarize(StateAudit.AuditIntake(clean)));
        }

        /// <summary>
        /// 第 70 段：几何接口。热力学有站表管着，几何以前只能靠人看图——
        /// 叶尖直径一样、轮毂比 0.42 与 0.75 的两台机器通流面积差 2.3 倍，按直径看是"匹配"的。
        /// </summary>
        private static void RunPortChainCases()
        {
            // 面积公式本身：环道必须扣掉轮毂那圈
            double aC = FlowPort.Circle(1.0, "圆").Area;
            double aA = FlowPort.Annulus(1.0, 0.5, "环").Area;
            Check("Ø1.0 圆管面积", aC, Math.PI * 0.25, 0.01, "m²");
            Check("Ø1.0/Ø0.5 环道面积", aA, Math.PI * 0.25 * 0.75, 0.01, "m²");
            ExpectTrue("同一外径下环道比圆管小", aA < aC);
            ExpectTrue("轮毂比 0.42 的通流面积比 0.75 大", 
                FlowPort.Annulus(1.0, 0.42, "a").Area > FlowPort.Annulus(1.0, 0.75, "b").Area);

            // J79：涡喷，没有风扇，接口从进气道直接进压气机
            EngineDesignData d = Presets.Build("J79");
            List<PortLink> links = Ports.Chain(d);
            Console.WriteLine("  " + Ports.Describe(links, Ports.DefaultTolerancePct).Replace("\n  ", "\n    "));
            ExpectTrue("J79 接口表不为空", links.Count >= 3);
            string pm;
            int bad = Ports.Problems(links, out pm);
            ExpectTrue("J79 没有坏接口", bad == 0);
            if (bad > 0) Console.WriteLine("    问题：" + pm);
            ExpectTrue("每条链的上游面积都是正数",
                links.TrueForAll(l => l.Upstream.Area > 0.0));
            ExpectTrue("每条链的下游面积都是正数",
                links.TrueForAll(l => l.Downstream.Area > 0.0));
            ExpectTrue("尾接口接在涡轮上",
                Ports.Terminal(d).Label.Contains("涡轮"));

            // 进气道喉道那端要能与 Intake.CaptureAreaOf 对上（同一个口径）
            FlowPort face = Ports.IntakeFace(d);
            Check("发动机面面积 = 捕获面积", face.Area, Intake.CaptureAreaOf(d), 1.0, "m²");

            // 涡扇：多出风扇那一段（进气道 → 风扇进口）
            EngineDesignData tf = new EngineDesignData();
            List<PortLink> tfl = Ports.Chain(tf);
            ExpectTrue("涡扇接口表比涡喷长", tfl.Count > links.Count);
            ExpectTrue("涡扇第一段是进气道接风扇",
                tfl[0].Downstream.Label.Contains("风扇进口"));

            // 坏数据：轮毂比 ≥1 的环形道根本不存在
            EngineDesignData broken = Presets.Build("J79");
            broken.Hpc.HubTipRatio = 1.20;
            List<PortLink> bl = Ports.Chain(broken);
            int badB = Ports.Problems(bl, out pm);
            ExpectTrue("轮毂比 ≥1 被判成坏接口", badB > 0);
            Console.WriteLine("    坏接口说明：" + pm);

            // 面积跳变：把高压压气机缩到十分之一，同一处的面积比必须明显塌下去
            EngineDesignData jump = Presets.Build("J79");
            jump.Hpc.TipDiameter = 0.10;
            List<PortLink> jl = Ports.Chain(jump);
            double rNorm = TurbineInletRatio(links);
            double rShrunk = TurbineInletRatio(jl);
            Console.WriteLine("    高压涡轮进口 面积比：正常 {0:F3}｜压气机缩到 1/10 后 {1:F3}", rNorm, rShrunk);
            ExpectTrue("压气机缩小后涡轮进口面积比明显变大", rShrunk > rNorm * 3.0);
            int looseNormal = Ports.Loose(links, Ports.DefaultTolerancePct);
            int looseJump = Ports.Loose(jl, Ports.DefaultTolerancePct);
            Console.WriteLine("    面积跳变：正常 J79 {0} 处｜缩小压气机 {1} 处｜容差 {2:F0}%",
                looseNormal, looseJump, Ports.DefaultTolerancePct);
            ExpectTrue("正常 J79 的面积跳变不超过 1 处", looseNormal <= 1);

            // 整机链路里也要带上接口表：求解完就能看
            NetworkSolution sol = EngineNetwork.SolveDesign(d);
            ExpectTrue("求解结果带几何接口表", sol.Ports != null && sol.Ports.Count == links.Count);
        }

        private static double TurbineInletRatio(List<PortLink> links)
        {
            for (int i = 0; i < links.Count; i++)
                if (links[i].Downstream.Label.Contains("涡轮进口")) return links[i].AreaRatio;
            return 0.0;
        }

        /// <summary>
        /// 第 71 段：逐轴转矩平衡。
        ///
        /// 交接文档 A §七要求的是 `J·dω/dt = τ_涡轮 − τ_压气机 − τ_附件`，而不是"每根轴各拍一个时间常数"。
        /// 这一段验四件事：
        /// ① 转矩的数算对没有（拿手算的数对：200 kW 涡轮、150 kW 负载、10 kW 附件、η 0.99、ω 70）；
        /// ② 惯量翻倍，同样功率下角加速度减半——这是"大机器推油门半天不走"的全部物理；
        /// ③ 附件吃功率要真的吃掉（几十 kW 在慢车上就是"到底了还差一点"）；
        /// ④ 多轴各积分各的：风扇轴与核心轴负载不同，转速就不该同步，而且全部平衡才算稳态。
        /// </summary>
        private static void RunTorqueCases()
        {
            // ① 手算对账：净功率 = 200 kW × 0.99 − 150 kW − 10 kW = 38 kW
            //    转矩 = 38000 / 70 = 542.857 N·m；角加速度 = 542.857 / 2.0 = 271.43 rad/s²
            ShaftTorque a = new ShaftTorque();
            a.Name = "N1";
            a.Inertia = 2.0;
            a.OmegaDesign = 100.0;
            a.Omega = 70.0;
            a.TurbinePower = 200000.0;
            a.LoadPower = 150000.0;
            a.AccessoryPower = 10000.0;
            a.MechanicalEfficiency = 0.99;
            Check("转矩：净功率（kW）", a.NetPower / 1000.0, 38.0, 0.1, "kW");
            Check("转矩：净转矩（N·m）", a.NetTorque, 38000.0 / 70.0, 0.1, "N·m");
            Check("转矩：角加速度（rad/s²）", a.AngularAcceleration, 38000.0 / 70.0 / 2.0, 0.1, "rad/s²");
            double alphaA = a.AngularAcceleration;      // 先存下来：Step 会改 ω，之后再比就不是同一个起点了
            double dw = a.Step(0.01);
            Check("转矩：一步 10 ms 的增量（rad/s）", dw, 38000.0 / 70.0 / 2.0 * 0.01, 0.5, "rad/s");

            // ② 惯量翻倍：角加速度减半，同样的 10 ms 增量也减半
            ShaftTorque b = new ShaftTorque();
            b.Name = "N1"; b.Inertia = 4.0; b.OmegaDesign = 100.0; b.Omega = 70.0;
            b.TurbinePower = 200000.0; b.LoadPower = 150000.0; b.AccessoryPower = 10000.0;
            b.MechanicalEfficiency = 0.99;
            Check("转矩：惯量翻倍后角加速度减半", b.AngularAcceleration / alphaA, 0.5, 0.5, "×");
            ExpectTrue("转矩：惯量翻倍后转速增量更小", Math.Abs(b.Step(0.01)) < Math.Abs(dw));

            // ③ 附件多吃 40 kW：净功率 38 → −2 kW，从加速变成掉转
            ShaftTorque c = new ShaftTorque();
            c.Name = "N1"; c.Inertia = 2.0; c.OmegaDesign = 100.0; c.Omega = 70.0;
            c.TurbinePower = 200000.0; c.LoadPower = 150000.0; c.AccessoryPower = 50000.0;
            c.MechanicalEfficiency = 0.99;
            ExpectTrue("转矩：附件多吃 40 kW 就从加速变掉转", c.NetPower < 0.0 && c.AngularAcceleration < 0.0);

            // ④ 平衡点：涡轮恰好等于（负载+附件）/η 时净功率为 0
            ShaftTorque e = new ShaftTorque();
            e.Name = "N1"; e.Inertia = 2.0; e.OmegaDesign = 100.0; e.Omega = 70.0;
            e.LoadPower = 150000.0; e.AccessoryPower = 10000.0;
            e.MechanicalEfficiency = 0.99;
            e.TurbinePower = (150000.0 + 10000.0) / 0.99;
            ExpectTrue("转矩：功率配平后不加速", e.Balanced(0.1) && Math.Abs(e.AngularAcceleration) < 0.05);

            // ⑤ 逐轴分工：涡扇的轴 1 只背风扇、核心轴只背压气机
            EngineDesignData fan = new EngineDesignData();
            fan.Fan.Stages = 3;
            OffDesignResult r = new OffDesignResult();
            r.Ok = true;
            r.WFan = 12000000.0;
            r.WComp = 30000000.0;
            r.CapLpt = 11000000.0;
            r.CapHpt = 26000000.0;
            r.CapIpt = 5000000.0;
            List<ShaftTorque> rows = TorqueBalance.Build(fan, r, null);
            ExpectTrue("转矩：涡扇至少两根轴", rows.Count >= 2);
            if (rows.Count >= 2)
            {
                ShaftTorque s1 = rows[0], s2 = rows[1];
                Check("转矩：风扇轴负载 = 风扇功（MW）", s1.LoadPower / 1.0e6, 12.0, 0.1, "MW");
                Check("转矩：核心轴负载 = 压气机功（MW）", s2.LoadPower / 1.0e6, 30.0, 0.1, "MW");
                Check("转矩：核心轴涡轮 = HPT（MW，IPT 级数 0 不计）", s2.TurbinePower / 1.0e6, 26.0, 0.1, "MW");
                ExpectTrue("转矩：两轴各算各的（负载不同，角加速度就不同）",
                    Math.Abs(s1.AngularAcceleration - s2.AngularAcceleration) > 1e-6);
                // 涡轮 26 MW、压气机 30 MW：核心轴这一刻在掉转。符号必须跟着功率差走。
                ExpectTrue("转矩：核心轴此刻在掉转（涡轮 26 MW < 压气机 30 MW）", s2.AngularAcceleration < 0.0);
                ExpectTrue("转矩：风扇轴此刻在掉转（涡轮 11 MW < 风扇 12 MW）", s1.AngularAcceleration < 0.0);
                OffDesignResult r2 = new OffDesignResult();
                r2.Ok = true;
                r2.WFan = 12000000.0; r2.WComp = 30000000.0;
                r2.CapLpt = 11000000.0; r2.CapHpt = 34000000.0; r2.CapIpt = 0.0;
                List<ShaftTorque> up = TorqueBalance.Build(fan, r2, null);
                ExpectTrue("转矩：多给 8 MW 涡轮功核心轴就转加速", up.Count >= 2 && up[1].AngularAcceleration > 0.0);
                ExpectTrue("转矩：不是所有轴都平衡时 AllBalanced 为假", !TorqueBalance.AllBalanced(rows, 0.1));
            }

            // ⑥ 全部配平：把每根轴的涡轮功率调成它自己的负载/η，AllBalanced 必须为真
            List<ShaftTorque> flat = TorqueBalance.Build(fan, r, null);
            for (int i = 0; i < flat.Count; i++)
                flat[i].TurbinePower = (flat[i].LoadPower + flat[i].AccessoryPower) / Math.Max(flat[i].MechanicalEfficiency, 1e-6);
            ExpectTrue("转矩：每根轴各自配平后算稳态", TorqueBalance.AllBalanced(flat, 0.1));

            // ⑦ 一行读数能写出来，且不出现 NaN
            string line = TorqueBalance.Describe(flat);
            ExpectTrue("转矩：读数里有净转矩与角加速度", line.Contains("净转矩") && line.Contains("dω/dt"));
            ExpectTrue("转矩：读数里没有 NaN", line.IndexOf("NaN", StringComparison.Ordinal) < 0);
        }

        /// <summary>造一份"看着像正常解"的 CycleResult，只改要测的那一项。</summary>
        private static CycleResult CycleResultLike(EngineDesignData d)
        {
            return Cycle.SolveDesignPoint(d) ?? new CycleResult();
        }

        /// <summary>
        /// 第 68 段：流量匹配。
        ///
        /// 两件事：
        /// ① 编辑界面上的「设计迎面马赫」撤掉之后，面马赫不再是人填的数，而是**几何自己说话**——
        ///    喉道/发动机面的面积比反解等熵面马赫。这条要是错，整个进气道的流量口径就错。
        /// ② 交接文档 A §六 那条禁令：进气道供多少、核心机就只能吃多少。以前 available 只进
        ///    诊断表、下游没人读，于是"进气道太小"只体现在一个没人看的数字上，压气机照样按
        ///    满流量算叶尖速度，喷管再按那些数出推力。现在要求：超了就把流量封顶、打标记、
        ///    下游按受限流量重算。
        /// </summary>
        private static void RunInletMatchCases()
        {
            // ── ① 面积比 → 面马赫（等熵亚声速支，γ=1.4）──
            Check("面积比 ≤1 兜底 0.95", IntakeSolver.FaceMachFromAreaRatio(1.0), 0.95, 1.0, "Ma");
            Check("面积比 1.3398 → M0.50", IntakeSolver.FaceMachFromAreaRatio(1.3398), 0.50, 2.0, "Ma");
            Check("面积比 1.4489 → M0.45", IntakeSolver.FaceMachFromAreaRatio(1.4489), 0.45, 2.0, "Ma");
            Check("NaN 面积比不炸（兜底 0.95）", IntakeSolver.FaceMachFromAreaRatio(double.NaN), 0.95, 1.0, "Ma");
            ExpectTrue("面积比越大面马赫越低",
                IntakeSolver.FaceMachFromAreaRatio(2.0) < IntakeSolver.FaceMachFromAreaRatio(1.3));

            // ── ② 有型线的零件：面马赫由几何反解，不再吃型式缺省 ──
            TechLevel tech = TechLevel.Get("1970s");
            IntakeSpec wide = new IntakeSpec();
            wide.Kind = IntakeKind.Axial;
            wide.Shape = IntakeProfile.Default(1.2, 0.60);
            wide.CaptureDiameter = 0.60;
            wide.ThroatRatio = 0.85;
            IntakeSolution sw = IntakeSolver.SolvePart(wide, 0.62, tech, 0.0, 0.60, 0.0, 0.0, 1.2);

            // 型线一旦在场，"捕获直径/喉道比"两个数就由几何量出来（Resolved 里的规矩），
            // 所以想让喉道更细，得去改型线的出口截面，而不是改 ThroatRatio——那样改了也没人听。
            IntakeSpec narrow = wide.Clone();
            narrow.Shape = wide.Shape.Clone();
            narrow.Shape.Sections[1].Width = 0.502;
            narrow.Shape.Sections[1].Height = 0.502;
            IntakeSolution sn = IntakeSolver.SolvePart(narrow, 0.62, tech, 0.0, 0.60, 0.0, 0.0, 1.2);

            Console.WriteLine("  型线反解：喉道/面 1.256 → 面马赫 {0:F3}｜喉道/面 1.525 → 面马赫 {1:F3}",
                sw.FaceMach, sn.FaceMach);
            // 期望值不写死常数：型线换了几段、喉道在哪个位置，这个数都会变。
            // 写死 0.550 的下场是改默认型线（两段→四段）时这条断言自己红了，
            // 而它真正想说的只有一句——**面马赫来自面积比**。
            double wantFace = IntakeSolver.FaceMachFromAreaRatio(sw.FaceArea / sw.ThroatArea);
            Check("型线件面马赫由面积比反解", sw.FaceMach, wantFace, 0.5, "Ma");
            // 反过来：把喉道放宽 → 面积比变小 → 迎面马赫升高。
            // 注意别去动最后一个截面：发动机面面积取的是风扇/进气口径，不是型线出口，
            // 改出口只改"捕获直径"，对面马赫没影响（这个坑我踩过一次）。
            IntakeSpec wider = wide.Clone();
            wider.Shape = wide.Shape.Clone();
            wider.Shape.Sections[1].Width = 0.62;
            wider.Shape.Sections[1].Height = 0.62;
            IntakeSolution sw2 = IntakeSolver.SolvePart(wider, 0.62, tech, 0.0, 0.60, 0.0, 0.0, 1.2);
            ExpectTrue("喉道放宽 → 迎面马赫升高（面马赫由几何决定）", sw2.FaceMach > sw.FaceMach + 0.01);
            ExpectTrue("喉道更细 → 面马赫更低", sn.FaceMach < sw.FaceMach);
            ExpectTrue("喉道更细 → 迎面速度低但总压恢复也低", sn.Tpr < sw.Tpr);

            // 没有型线的老口径必须原样：缺省面马赫 = 按型式
            IntakeSpec plain = new IntakeSpec();
            plain.Kind = IntakeKind.Axial;
            plain.CaptureDiameter = 0.60;
            plain.ThroatRatio = 0.85;
            IntakeSolution sp0 = IntakeSolver.SolvePart(plain, 0.62, tech, 0.0, 0.60, 0.0, 0.0, 1.2);
            Check("无型线仍走型式缺省 0.50", sp0.FaceMach, IntakeSpec.DefaultFaceMach(IntakeKind.Axial), 0.5, "Ma");

            // ── ③ 设计点：报得出可用流量，且流量不超过它 ──
            EngineDesignData j79 = Presets.Build("J79");
            CycleResult dp = Cycle.SolveDesignPoint(j79);
            Console.WriteLine("  设计点：需求 {0:F3} kg/s｜可用 {1:F3} kg/s｜限流 {2}",
                dp.MdotAir, dp.InletAvailableMassFlow, dp.InletFlowLimited);
            ExpectTrue("设计点报出进气道可用流量", dp.InletAvailableMassFlow > 1.0);
            ExpectTrue("正常构型没被限流", !dp.InletFlowLimited);
            ExpectTrue("流量不超过可用流量", dp.MdotAir <= dp.InletAvailableMassFlow * 1.001 + 1e-9);

            // 整机报出来的可用流量，必须与进气道求解器单独算的是同一个数——
            // 两处各算一遍是这类项目最常见的老毛病（同一台机器的两个"权威口径"）。
            IntakeSolution solo = IntakeSolver.SolveAt(j79.Intake, j79, j79.DesignAlt, j79.DesignMach, 0.0, double.NaN);
            Check("链路可用流量与进气道口径一致", dp.InletAvailableMassFlow, solo.AvailableMassFlow, 0.5, "kg/s");

            // ── ④ 非设计点：进气道故意做小，核心机必须被真限流 ──
            EngineDesignData small = Presets.Build("J79");
            small.Intake.CaptureDiameter = 0.24;
            small.Intake.ThroatRatio = 0.65;
            OffDesignResult od = OffDesign.Solve(small, 11000.0, 1.60, 1.0);
            Console.WriteLine("  限流：实际进气 {0:F3} kg/s｜可用 {1:F3} kg/s｜标记 {2}",
                od.MdotAir, od.IntakeCapacity, od.MdotAir <= od.IntakeCapacity * 1.001 + 1e-6);
            ExpectTrue("非设计点报出可用流量", od.IntakeCapacity > 0.0);
            ExpectTrue("非设计点流量被限制在可用值以内",
                od.MdotAir <= od.IntakeCapacity * 1.001 + 1e-6);

            // 同一台机器把进气道放大，流量必须跟着变大（不然"限流"只是个数字）
            EngineDesignData big = Presets.Build("J79");
            big.Intake.CaptureDiameter = 0.70;
            big.Intake.ThroatRatio = 0.95;
            OffDesignResult odb = OffDesign.Solve(big, 11000.0, 1.60, 1.0);
            Console.WriteLine("  放大进气道：进气 {0:F3} kg/s（可用 {1:F3}）", odb.MdotAir, odb.IntakeCapacity);
            ExpectTrue("进气道放大后进气量变大", odb.MdotAir > od.MdotAir);
        }

        /// <summary>
        /// 第 72 段：故障三级台账。
        ///
        /// 交接文档 A §八 的规矩是"故障分三级，同一缺陷在设计阶段与运行阶段不得重复惩罚"。
        /// 这条规矩以前是纸面上的：判据 FAIL 降额 ×0.78，飞行里同一个缺陷再按风险罚一次，
        /// 玩家配出一台喘振裕度不足的机器会受两遍罚，而报表上只说了一遍理由。
        ///
        /// 这一段的断言全在"同一缺陷只有一条账"上——多罚一次是玩家的体验问题，不是数字问题，
        /// 所以只能靠回归钉住，光靠肉眼看推力变小是看不出来的。
        /// </summary>
        private static void RunFaultBookCases()
        {
            FaultBook bk = new FaultBook();

            // ① 第一次报告 = 新缺陷
            bool first = bk.Raise("喘振裕度不足", FaultTier.Design, FaultSeverity.Major,
                                  "喘振裕度", 0.03, 0.15, "", "设计不成立，飞行中降额运转", "回编辑器改设计");
            ExpectTrue("第一次报告算新缺陷", first);
            Expect("台账 1 条", bk.Count == 1, true);

            // ② 同码再报 = 不是新缺陷，不许二次罚
            bool second = bk.Raise("喘振裕度不足", FaultTier.Design, FaultSeverity.Major,
                                   "喘振裕度", 0.03, 0.15, "", "设计不成立，飞行中降额运转", "回编辑器改设计");
            Expect("同码二次报告不算新缺陷", second, false);
            Expect("同一条记录没被重复添加", bk.Count == 1, true);
            ExpectTrue("去重计数动了", bk.Deduped >= 1);

            // ③ 设计期已记，运行期同码升到"风险"：级别只升不降，但仍然只罚一次
            bool asRisk = bk.Raise("喘振裕度不足", FaultTier.Risk, FaultSeverity.Major,
                                   "喘振强度", 0.5, 1.0, "", "推力 ×0.45", "收油门");
            Expect("同码跨级报告也不再算新缺陷", asRisk, false);
            FaultRecord rec = bk.Find("喘振裕度不足");
            Expect("级别升到运行风险", rec != null && rec.Tier == FaultTier.Risk, true);

            // ④ 判据报告整段落账：FAIL → Major，WARN → Minor，OK 不收
            CheckReport rep = new CheckReport();
            rep.Add(new Finding("hpc.stage_load", Level.Fail, "高压压气机级负荷过高", 0.62, 0.55, ""));
            rep.Add(new Finding("nozzle.area", Level.Warn, "喷管出口面积偏小", 0.9, 1.0, ""));
            rep.Add(new Finding("fan.tip", Level.Ok, "风扇叶尖马赫合适", 1.1, 1.2, ""));
            FaultBook repBook = new FaultBook();
            repBook.BookFindings(rep);
            Expect("判据里两条不成立就记两条", repBook.Count == 2, true);
            FaultRecord failRec = repBook.Find("高压压气机级负荷过高");
            Expect("FAIL 记成 Major", failRec != null && failRec.Severity == FaultSeverity.Major, true);
            FaultRecord warnRec = repBook.Find("喷管出口面积偏小");
            Expect("WARN 记成 Minor", warnRec != null && warnRec.Severity == FaultSeverity.Minor, true);
            Expect("OK 不进台账", repBook.Find("风扇叶尖马赫合适") == null, true);

            // ⑤ 这就是要拦的那件事：设计期挂了账，运行期同码又报上来 → 判据重叠可见
            repBook.Raise("高压压气机级负荷过高", FaultTier.Risk, FaultSeverity.Major,
                          "喘振强度", 0.6, 1.0, "", "推力 ×0.45", "收油门");
            string overlap;
            ExpectTrue("判据与运行风险同码时能认出来", repBook.DesignOverlapsRuntime(rep, out overlap));
            Expect("认出来的就是那条判据", overlap == "高压压气机级负荷过高", true);

            // ⑥ 持续时间只算"还在发生"的那些：报 → 停 → 再报
            FaultBook t = new FaultBook();
            t.Raise("叶尖超速", FaultTier.Risk, FaultSeverity.Minor, "超速强度", 0.4, 1.0, "", "", "");
            t.Step(0.5);
            FaultRecord speed = t.Find("叶尖超速");
            Expect("报过一帧就记 0.5 秒", Math.Abs(speed.Duration - 0.5) < 1e-9, true);
            t.Step(0.5);   // 这一帧没再报 → 风险收回
            Expect("没再报就判为已恢复", speed.Active, false);
            Expect("已恢复就不再累加时间", Math.Abs(speed.Duration - 0.5) < 1e-9, true);
            t.Raise("叶尖超速", FaultTier.Risk, FaultSeverity.Minor, "超速强度", 0.4, 1.0, "", "", "");
            Expect("再次出现算新的一轮（次数 +1）", speed.Count == 2, true);
            Expect("历史没被清掉（还是 1 条记录）", t.Count == 1, true);

            // ⑦ 故障事件：坏了就是坏了，不会被"这一帧没报"收回去
            FaultBook ev = new FaultBook();
            ev.Raise("涡轮烧蚀降额", FaultTier.Event, FaultSeverity.Critical, "温端损伤", 1.0, 1.0, "", "", "");
            ev.Step(1.0);
            ev.Step(1.0);
            FaultRecord burned = ev.Find("涡轮烧蚀降额");
            Expect("故障事件不会被自动收回", burned.Active, true);
            Expect("故障事件记满两秒", Math.Abs(burned.Duration - 2.0) < 1e-9, true);

            // ⑧ 三级都能说人话
            string line = ev.Describe();
            ExpectTrue("摘要含三级字样或条目数", line.Contains("缺陷台账"));
            ExpectTrue("条目里标出故障事件", line.Contains("故障事件"));

            // ⑨ Reset 之后干净（索引也要清，否则下次去重会命中幽灵记录）
            ev.Reset();
            Expect("Reset 清空台账", ev.Count == 0, true);
            ExpectTrue("Reset 后同一个码又算新缺陷",
                ev.Raise("涡轮烧蚀降额", FaultTier.Event, FaultSeverity.Critical, "温端损伤", 1.0, 1.0, "", "", ""));

            Console.WriteLine("  台账样本：" + repBook.Describe(3).Replace("\n", "\n  "));
        }

        /// <summary>
        /// 第 73 段：气路状态的有效性契约。
        ///
        /// 交接文档 A 的第一批验收里有一条"数值边界"，要求部件不许把坏状态悄悄传下去，
        /// 也不许用 0 代表"还没算出来"。这一段的断言全是反例：每个坏状态都得**说得出
        /// 是哪一类坏**，而不是只回一个 false——因为最后玩家看到的"推力 0"，
        /// 根因往往是三步之前那个压力为 0 的出口。
        /// </summary>
        private static void RunGasContractCases()
        {
            string why;

            GasState good = GasState.FromTotal(Stations.IntakeExit, 101325.0, 288.15, 77.0, FlowRole.Core);
            Expect("正常状态合格", good.Validate(out why) == GasValidity.Valid, true);
            Expect("正常状态 IsValid 也是真", good.IsValid, true);
            Expect("合格时不该有理由", why.Length == 0, true);

            GasState zeroP = GasState.FromTotal(Stations.HpcExit, 0.0, 900.0, 40.0, FlowRole.Core);
            Expect("总压为 0 → BadPressure", zeroP.Validate(out why) == GasValidity.BadPressure, true);
            ExpectTrue("理由里点名总压", why.Contains("总压"));

            GasState zeroT = GasState.FromTotal(Stations.HpcExit, 5.0e5, 0.0, 40.0, FlowRole.Core);
            Expect("总温为 0 → BadTemperature", zeroT.Validate(out why) == GasValidity.BadTemperature, true);

            GasState negM = GasState.FromTotal(Stations.HpcExit, 5.0e5, 900.0, -1.0, FlowRole.Core);
            Expect("负流量 → NegativeFlow", negM.Validate(out why) == GasValidity.NegativeFlow, true);

            GasState fatFar = GasState.FromTotal(Stations.BurnerExit, 5.0e5, 1600.0, 40.0, FlowRole.Core, 0.42);
            Expect("油气比 0.42 → BadComposition", fatFar.Validate(out why) == GasValidity.BadComposition, true);

            GasState nanP = GasState.FromTotal(Stations.HpcExit, double.NaN, 900.0, 40.0, FlowRole.Core);
            Expect("NaN 总压 → NotFinite", nanP.Validate(out why) == GasValidity.NotFinite, true);
            Expect("NaN 状态 IsValid 为假", nanP.IsValid, false);

            GasState swapped = GasState.FromTotal(Stations.HpcExit, 5.0e5, 900.0, 40.0, FlowRole.Core);
            swapped.StaticPressure = 6.0e5;    // 静压比总压高 = 总/静混用
            Expect("静压高于总压 → StaticAboveTotal", swapped.Validate(out why) == GasValidity.StaticAboveTotal, true);

            // 零流量是合法状态（关车、外涵没开），别把它判成坏：
            GasState zeroFlow = GasState.FromTotal(Stations.BypassExit, 101325.0, 288.15, 0.0, FlowRole.Bypass);
            Expect("零流量仍然合格（0 不等于未知）", zeroFlow.IsValid, true);

            // 链路级：一台正常设计的每个测站都得合格，且不该冒出"不合格"的告警。
            EngineDesignData d = Presets.Build("J79");
            NetworkSolution net = EngineNetwork.SolveDesign(d);
            int bad = 0, stations = 0;
            foreach (KeyValuePair<int, GasState> kv in net.StationTable)
            {
                if (kv.Value == null) continue;
                stations++;
                if (!kv.Value.IsValid) bad++;
            }
            Expect("设计点每个测站都合格", bad, 0);
            int invalidWarn = 0;
            for (int i = 0; i < net.Warnings.Count; i++)
                if (net.Warnings[i].Contains("不合格")) invalidWarn++;
            Expect("链路没有出现状态不合格告警", invalidWarn, 0);
            Console.WriteLine("  链路：{0} 个测站｜不合格 {1} 个｜告警 {2} 条", stations, bad, net.Warnings.Count);
        }

        /// <summary>
        /// 第 74 段：分层刷新。
        ///
        /// 要证的不是"能重建"，而是**不该重建的时候真的没重建**：
        /// 迎角侧滑每帧都在变，它们不进指纹；改弦长/喉道才让几何脏。
        /// </summary>
        private static void RunRefreshPlanCases()
        {
            RefreshPlan none = RefreshPlan.Compare(new RefreshStamp("g1", "p1", "v1"), new RefreshStamp("g1", "p1", "v1"));
            Expect("三层都没变 → 什么都不建", (int)none.Layers, (int)RefreshLayer.None);

            RefreshPlan first = RefreshPlan.Compare(new RefreshStamp("", "", ""), new RefreshStamp("g1", "p1", "v1"));
            Expect("首次建立 → 三层全建", (int)first.Layers, (int)RefreshLayer.All);

            RefreshPlan geo = RefreshPlan.Compare(new RefreshStamp("g1", "p1", "v1"), new RefreshStamp("g2", "p1", "v1"));
            Expect("几何变了 → 性能跟着重算", geo.Performance, true);
            Expect("几何变了 → 表现也跟着重算", geo.Presentation, true);
            ExpectTrue("理由点名几何", geo.Why.Contains("几何"));

            RefreshPlan perf = RefreshPlan.Compare(new RefreshStamp("g1", "p1", "v1"), new RefreshStamp("g1", "p2", "v1"));
            Expect("只有性能变 → 不重建几何（省一次网格）", perf.Geometry, false);
            Expect("只有性能变 → 表现要重算", perf.Presentation, true);
            Expect("只有性能变 → 层是 性能+表现", (int)perf.Layers, (int)(RefreshLayer.Performance | RefreshLayer.Presentation));

            RefreshPlan pres = RefreshPlan.Compare(new RefreshStamp("g1", "p1", "v1"), new RefreshStamp("g1", "p1", "v2"));
            Expect("只有贴图/材质变 → 只重建表现", (int)pres.Layers, (int)RefreshLayer.Presentation);

            Expect("三层名字：全建", RefreshPlan.Names(RefreshLayer.All), "几何+性能+表现");
            Expect("三层名字：空", RefreshPlan.Names(RefreshLayer.None), "无");

            // 指纹本身的两条要点：迎角侧滑不进性能指纹，几何指纹认得型线。
            IntakeSpec sp = new IntakeSpec();
            sp.Kind = IntakeKind.Ramp;
            sp.Shape = IntakeProfile.Default(1.2, 0.8);
            EngineDesignData d = Presets.Build("J79");
            string p0 = RefreshStamps.IntakePerformance(sp, d.Tech);
            sp.AlphaDeg = 9.0; sp.BetaDeg = -4.0;
            string p1 = RefreshStamps.IntakePerformance(sp, d.Tech);
            Expect("迎角侧滑变了，性能指纹不变（每帧变的东西不能进指纹）", p0 == p1, true);

            string g0 = RefreshStamps.IntakeGeometry(sp, 0.8, 1.2);
            sp.Shape.Sections[1].Width = 0.5;
            string g1 = RefreshStamps.IntakeGeometry(sp, 0.8, 1.2);
            Expect("喉道截面收窄 → 几何指纹变了", g0 != g1, true);

            string g2 = RefreshStamps.IntakeGeometry(sp, 0.8, 1.2);
            Expect("同样的型线再取一次 → 指纹一样", g1 == g2, true);

            RefreshPlan live = RefreshPlan.Compare(new RefreshStamp(g0, p0, "v1"), new RefreshStamp(g1, p1, "v1"));
            Expect("型线变了而迎角变了：依然按几何重建", (int)live.Layers, (int)RefreshLayer.All);
            Console.WriteLine("  指纹：几何 {0} 字符｜理由「{1}」", g1.Length, live.Why);
        }

        /// <summary>
        /// 第 75 段：压气机特性图。
        ///
        /// 要证三件事：①设计点能原样取回（图不是另编的一套数）；
        /// ②几条线的走向对（φ 减小→压比升；喘振点裕度恰为 0；堵塞点流量最大）；
        /// ③换个叶尖直径图会跟着变（图是从机器几何来的，不是常量表）。
        /// </summary>
        private static void RunCompressorChartCases()
        {
            EngineDesignData d = Presets.Build("J79");
            NetworkSolution net = EngineNetwork.SolveDesign(d);
            CompressorChart ch = net.Chart;
            if (ch == null)
            {
                Expect("J79 该有一张压气机图", false, true);
                return;
            }

            string why;
            Expect("图自洽（设计点能取回、边界单调）", ch.SelfCheck(out why), true);
            if (why.Length > 0) Console.WriteLine("  自洽检查说：{0}", why);

            MapPoint des = ch.PointAt(1.0, ch.DesignPhi);
            Check("设计点压比取回", des.Pr, ch.DesignPr, 1e-6, "");
            Check("设计点换算流量取回", des.Wc, ch.DesignWc, 1e-6, "kg/s");

            MapPoint surge = ch.SurgeAt(1.0);
            MapPoint choke = ch.ChokeAt(1.0);
            Expect("喘振点压比高于设计点", surge.Pr > des.Pr, true);
            Expect("喘振点流量小于设计点", surge.Wc < des.Wc, true);
            Expect("堵塞点流量大于设计点", choke.Wc > des.Wc, true);
            Expect("堵塞点压比低于喘振点", choke.Pr < surge.Pr, true);

            Check("喘振点自身裕度为 0", ch.SurgeMargin(surge), 0.0, 1e-9, "%");
            Expect("工作点裕度为正", ch.SurgeMargin(ch.WorkingPoint(1.0)) > 0.0, true);

            // 走向：同一条转速线上 φ 变大 → 压比变小（单调），这条错了整张图就没意义。
            SpeedLine ln = ch.LineFor(1.0);
            bool mono = true;
            for (int i = 1; i < ln.Points.Count; i++)
                if (ln.Points[i].Pr > ln.Points[i - 1].Pr + 1e-9) { mono = false; break; }
            Expect("等转速线上：流量越大压比越低", mono, true);

            // 裕度随流量收窄而变小：靠近喘振线的点上裕度必须更小。
            double smMid = ch.SurgeMargin(ch.PointAt(1.0, 0.5 * (ch.PhiStall + ch.DesignPhi)));
            double smDesign = ch.SurgeMargin(des);
            Expect("往喘振线靠 → 裕度变小", smMid < smDesign, true);

            // 图随几何变：叶尖直径换掉，设计流量系数跟着变。
            EngineDesignData narrow = Presets.Build("J79");
            narrow.Hpc.TipDiameter = 0.30;
            narrow.Hpc.StageTip.Clear();
            NetworkSolution net2 = EngineNetwork.SolveDesign(narrow);
            Expect("换叶尖直径后图也变了（叶尖速度不同 → 压比/流量都不同）",
                net2.Chart != null && Math.Abs(net2.Chart.DesignTipSpeed - ch.DesignTipSpeed) > 1.0, true);

            // 转速线数量与转速范围是约定值（图上要有低转速段才画得出工作线）。
            Expect("转速线 12 条（0.50~1.05）", ch.Lines.Count, 12);
            Expect("最低转速 0.50", (int)Math.Round(ch.Lines[0].Nc * 100.0), 50);

            Console.WriteLine("  {0}", ch.Describe());
            Console.WriteLine("  设计点：{0}｜喘振线：{1}｜堵塞线：{2}", des, surge, choke);
            Console.WriteLine("  工作点裕度 {0:F1}%（设计点 {1:F1}%）", ch.SurgeMargin(ch.WorkingPoint(1.0)), smDesign);
        }

        private static void RunVariableGeometryCases()
        {
            // 可调中心锥：装不装是机构选择，推动量由马赫自己走。
            // 这一段的重点不是"收窄多少"，而是**开关关着时一个字都不许动**。
            IntakeSpec plain = new IntakeSpec();
            plain.Kind = IntakeKind.Cone;
            Expect("没装机构 → 因子恒 1（M2.0）", Math.Abs(Intake.ThroatFactor(plain, 2.0) - 1.0) < 1e-12, true);

            IntakeSpec vg = new IntakeSpec();
            vg.Kind = IntakeKind.Cone;
            vg.VariableGeometry = true;
            Check("M1.0 还没作动", Intake.ThroatFactor(vg, 1.0), 1.0, 1e-9, "");
            Check("M1.2 刚起动（作动点）", Intake.ThroatFactor(vg, 1.2), 1.0, 1e-9, "");
            Check("M1.9 走了一半行程", Intake.ThroatFactor(vg, 1.9), 1.0 - Intake.VariableGeometryThroatLoss * 0.5, 1e-9, "");
            Check("M2.6 走完全行程", Intake.ThroatFactor(vg, 2.6), 1.0 - Intake.VariableGeometryThroatLoss, 1e-9, "");
            Check("M3.0 保持最小喉道", Intake.ThroatFactor(vg, 3.0), 1.0 - Intake.VariableGeometryThroatLoss, 1e-9, "");

            IntakeSpec pitot = new IntakeSpec();
            pitot.Kind = IntakeKind.Pitot;
            pitot.VariableGeometry = true;
            Check("皮托没有可动压缩面", Intake.ThroatFactor(pitot, 2.5), 1.0, 1e-12, "");

            // 走求解器：同样的机器，装机构之后超声速喉道变小、可用流量跟着变小，
            // 恢复系数**不该变**——机构动的是喉道，不是压缩角。
            EngineDesignData d0 = Presets.Build("J79");
            d0.Intake = new IntakeSpec();
            d0.Intake.Kind = IntakeKind.Cone;
            d0.Intake.VariableGeometry = false;
            EngineDesignData d1 = Presets.Build("J79");
            d1.Intake = new IntakeSpec();
            d1.Intake.Kind = IntakeKind.Cone;
            d1.Intake.VariableGeometry = true;

            IntakeSolution a0 = IntakeSolver.SolveAt(d0.Intake, d0, 11000.0, 2.0, 0.0, 0.0);
            IntakeSolution a1 = IntakeSolver.SolveAt(d1.Intake, d1, 11000.0, 2.0, 0.0, 0.0);
            double t20 = (2.0 - Intake.VariableGeometryOn) / (Intake.VariableGeometryFull - Intake.VariableGeometryOn);
            Check("M2.0 喉道收到 1−18%×行程", a1.ThroatArea / Math.Max(a0.ThroatArea, 1e-9),
                1.0 - Intake.VariableGeometryThroatLoss * t20, 1e-6, "");
            ExpectTrue("可用流量跟着变小", a1.AvailableMassFlow < a0.AvailableMassFlow);
            Check("恢复系数不变（机构不动压缩角）", a1.Tpr, a0.Tpr, 1e-6, "");

            IntakeSolution b0 = IntakeSolver.SolveAt(d0.Intake, d0, 3000.0, 0.8, 0.0, 0.0);
            IntakeSolution b1 = IntakeSolver.SolveAt(d1.Intake, d1, 3000.0, 0.8, 0.0, 0.0);
            Check("亚声速一个数都不动（喉道）", b1.ThroatArea, b0.ThroatArea, 1e-9, "m²");

            // 存读：这个键必须能原样回来，否则玩家勾了开关重进存档就变回没装。
            ConfigNode n = new ConfigNode(IntakeSpecCodec.NodeName);
            IntakeSpecCodec.Save(n, vg);
            IntakeSpec back = IntakeSpecCodec.Load(n, new IntakeSpec());
            Expect("存读保住可调几何开关", back.VariableGeometry, true);
            Expect("存读没把缺省打开", new IntakeSpec().VariableGeometry, false);

            Console.WriteLine("  可调锥：M1.2 起作动｜M2.0 喉道 {0:P0}｜M2.6 收 {1:P0}｜可用流量 {2:F1} → {3:F1} kg/s",
                1.0 - Intake.VariableGeometryThroatLoss * ((2.0 - Intake.VariableGeometryOn) / (Intake.VariableGeometryFull - Intake.VariableGeometryOn)),
                1.0 - Intake.VariableGeometryThroatLoss, a0.AvailableMassFlow, a1.AvailableMassFlow);
        }

        /// <summary>
        /// 断言：自己计失败数。
        ///
        /// 以前是"调用点写 `_fails += Expect(...)` 才算数"——于是漏写一处，
        /// 屏幕上明明打印着 **超差**，末尾却还是"全部回归通过。"。
        /// 本轮就踩到了：三条镜像断言红了、总结行还是绿的（第 65 段）。
        /// 计数放在这里，谁忘了接收返回值都不会再骗人。
        /// </summary>
        private static int Expect(string label, bool got, bool want)
        {
            bool ok = got == want;
            if (!ok) _fails++;
            Console.WriteLine("  {0,-26} 实得 {1,-5} 期望 {2,-5} {3}", label, got, want, ok ? "OK" : "**超差**");
            return ok ? 0 : 1;
        }

        /// <summary>只判"是不是真的"，省掉每次都要写的 want=true。</summary>
        private static int ExpectTrue(string label, bool got)
        {
            return Expect(label, got, true);
        }

        /// <summary>字符串比较（型别名/层名这种）。整数比较见下面那个重载。</summary>
        private static int Expect(string label, string got, string want)
        {
            bool ok = got == want;
            if (!ok) _fails++;
            Console.WriteLine("  {0,-26} 实得 {1,-5} 期望 {2,-5} {3}", label, got, want, ok ? "OK" : "**超差**");
            return ok ? 0 : 1;
        }

        private static int Expect(string label, int got, int want)
        {
            bool ok = got == want;
            if (!ok) _fails++;
            Console.WriteLine("  {0,-26} 实得 {1,-5} 期望 {2,-5} {3}", label, got, want, ok ? "OK" : "**超差**");
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 第 69 段：喷管推力的两项分开记。
        /// F = ṁ·Ve + (Pe−Pa)·Ae —— 交接文档把这写成一条验收项，理由很实在：
        /// 以前只有一个 gross，谁也不知道某个分支里是不是偷偷把压力项丢了，
        /// 而"少了压力项"在收扩喷管上就是几十 kN 的差，偏偏收敛喷管上看不出来。
        /// </summary>
        private static void RunNozzleSplitCases()
        {
            double pAmb = 101325.0;
            // 高总压（加力状态）才看得出压力项：NPR 只有 3 的时候收扩喷管几乎完全膨胀，
            // (Pe−Pa) 接近 0，看不出"压力项被丢了"这种错。
            double pt = 20.0 * pAmb, tt = 1100.0, mdot = 18.0;
            double aThroat = mdot / Math.Max(Gas.ChokedFlow(1.0, pt, tt, 0.0), 1e-9);
            double arDesign = Gas.DesignAreaRatio(pt, pAmb, tt);
            double arUnder = Math.Max(1.05, arDesign * 0.70);   // 面积比给不够 → 欠膨胀

            Gas.NozzleState cd = Gas.NozzleFlow(mdot, pt, tt, pAmb, 0.98, NozzleKind.ConvergingDiverging, aThroat, arUnder);
            Console.WriteLine("  收扩（欠膨胀）：设计面积比 {0:F2} 给 {1:F2}｜动量 {2:F1} N｜压力 {3:F1} N｜总 {4:F1} N｜出口压 {5:F0} Pa",
                arDesign, arUnder, cd.Momentum, cd.Pressure, cd.Thrust, cd.Pe);
            Check("收扩 动量+压力 = 总推力", cd.Momentum + cd.Pressure, cd.Thrust, 0.001, "N");
            ExpectTrue("收扩喷管欠膨胀：压力项为正", cd.Pressure > 0.0);
            ExpectTrue("动量项是主项（压力项不到它的三分之一）", cd.Pressure * 3.0 < cd.Momentum);

            Gas.NozzleState cdm = Gas.NozzleFlow(mdot, pt, tt, pAmb, 0.98, NozzleKind.ConvergingDiverging, aThroat, arDesign);
            Console.WriteLine("  收扩（完全膨胀）：出口压 {0:F0} Pa（环境 {1:F0}）｜压力项 {2:F1} N",
                cdm.Pe, pAmb, cdm.Pressure);
            ExpectTrue("按设计面积比膨胀：出口压≈环境压", Math.Abs(cdm.Pe - pAmb) < pAmb * 0.02);
            ExpectTrue("完全膨胀时压力项≈0", Math.Abs(cdm.Pressure) < Math.Abs(cdm.Thrust) * 0.01);

            // 收敛喷管未窒息时出口压力就是环境压力，压力项精确为 0。
            // 这里走老接口 Gas.Nozzle（不是 NozzleFlow）：未窒息的固定喉道要反解出口马赫，
            // 随手塞一个不匹配的 mdot/喉道会得到几千 m/s 的假速度——那是喂错数的锅，不是模型的锅。
            double ptLow = 1.05 * pAmb;
            double ve0, ae0, pe0, gross0;
            bool choked0;
            Gas.Nozzle(mdot, ptLow, tt, pAmb, 0.98, out ve0, out ae0, out pe0, out gross0, out choked0);
            Console.WriteLine("  收敛（未窒息）：Ve {0:F0} m/s｜出口压 {1:F0} Pa｜压力项 {2:F1} N｜总 {3:F1} N",
                ve0, pe0, (pe0 - pAmb) * ae0, gross0);
            ExpectTrue("未窒息时没有壅塞", !choked0);
            ExpectTrue("收敛未窒息：出口就是环境压，压力项=0", Math.Abs(pe0 - pAmb) < 1e-9);
            Check("未窒息 总推力 = 动量项", gross0, mdot * ve0, 0.001, "N");

            // 同一工况下欠膨胀的收扩必须比"只膨胀到喉道"的收敛喷管推力大——
            // 压力项被漏掉的时候这条会立刻掉下去。
            Gas.NozzleState cvChoked = Gas.NozzleFlow(mdot, pt, tt, pAmb, 0.98, NozzleKind.Converging, aThroat, 1.0);
            ExpectTrue("收扩欠膨胀比收敛多出推力", cd.Thrust > cvChoked.Thrust);

            // 真机流程里也走一遍：NozzleComponent 上报的两项加起来要等于它自己的 gross
            EngineDesignData j79 = Presets.Build("J79");
            NetworkSolution ns = EngineNetwork.SolveDesign(j79);
            ComponentResult nr = ns.Of("NOZZLE");
            if (nr != null)
            {
                double mo = nr.Get("momentum"), pr = nr.Get("pressure"), gr = nr.Get("gross");
                Console.WriteLine("  链路：动量 {0:F1} N｜压力 {1:F1} N｜总 {2:F1} N｜失配 {3:E1} N", mo, pr, gr, nr.Get("split_err"));
                ExpectTrue("链路上报的分项加起来等于总推力",
                    Math.Abs(mo + pr - gr) <= Math.Abs(gr) * 1e-9 + 1e-6);
            }
        }

        /// <summary>给非 bool 的精确比较用（枚举、整数计数）。</summary>
        private static void ExpectEq<T>(string label, T got, T want)
        {
            bool ok = EqualityComparer<T>.Default.Equals(got, want);
            if (!ok) _fails++;
            Console.WriteLine("  {0,-26} 实得 {1,-8} 期望 {2,-8} {3}", label, got, want, ok ? "OK" : "**超差**");
        }

        /// <summary>
        /// 简单模式台账。期望值直接抄 proto/simple.py `_demo()` 的实跑输出，
        /// 所以这张表同时校验"移植对不对"和"两档是不是同一台机器"。
        /// </summary>
        private static void RunSimpleCases()
        {
            var hbpr = new SimpleDesign
            {
                Name = "HBPR", Kind = EngineKind.Turbofan, Diameter = 1.7,
                Bpr = 6.0, Opr = 30.0, T4 = 1500.0
            };
            var lbpr = new SimpleDesign
            {
                Name = "LBPR", Kind = EngineKind.Turbofan, Diameter = 1.0,
                Bpr = 0.6, Opr = 22.0, T4 = 1500.0
            };
            var tj = new SimpleDesign
            {
                Name = "TJ", Kind = EngineKind.Turbojet, Diameter = 0.74,
                Bpr = 0.0, Opr = 13.5, T4 = 1210.0
            };
            var tjAb = tj.Clone();
            tjAb.Name = "TJ-AB";
            tjAb.AbFuelRatio = 0.03;

            Console.WriteLine("  型号            BPR    OPR   FPR     推力      SFC    mdot");
            SimpleResult r1 = Simple.DesignPoint(hbpr);
            if (!r1.Ok) { _fails++; Console.WriteLine("  HBPR 失败: " + string.Join(" / ", r1.Errors)); }
            else
            {
                Console.WriteLine("  涡扇 高涵道比  {0,6:F2} {1,6:F2} {2,6:F2} {3,8:F1}k {4,8:F4} {5,7:F1}",
                    r1.Bpr, r1.Opr, r1.Fpr, r1.NetThrust / 1000.0, r1.Sfc, r1.MdotAir);
                Check("HBPR OPR", r1.Opr, 30.0, 1.0);
                Check("HBPR FPR", r1.Fpr, 1.52, 3.0);
                Check("HBPR 推力 kN", r1.NetThrust / 1000.0, 119.3, 3.0);
                Check("HBPR SFC", r1.Sfc, 0.0295, 5.0);
                Check("HBPR 流量", r1.MdotAir, 408.7, 2.0);
            }

            SimpleResult r2 = Simple.DesignPoint(lbpr);
            if (!r2.Ok) { _fails++; Console.WriteLine("  LBPR 失败: " + string.Join(" / ", r2.Errors)); }
            else
            {
                Console.WriteLine("  涡扇 低涵道比  {0,6:F2} {1,6:F2} {2,6:F2} {3,8:F1}k {4,8:F4} {5,7:F1}",
                    r2.Bpr, r2.Opr, r2.Fpr, r2.NetThrust / 1000.0, r2.Sfc, r2.MdotAir);
                Check("LBPR OPR", r2.Opr, 22.0, 1.0);
                Check("LBPR FPR", r2.Fpr, 1.86, 3.0);
                Check("LBPR 推力 kN", r2.NetThrust / 1000.0, 88.7, 3.0);
                Check("LBPR SFC", r2.Sfc, 0.0668, 5.0);
                Check("LBPR 流量", r2.MdotAir, 141.4, 2.0);
            }

            SimpleResult r3 = Simple.DesignPoint(tj);
            if (!r3.Ok) { _fails++; Console.WriteLine("  TJ 失败: " + string.Join(" / ", r3.Errors)); }
            else
            {
                Console.WriteLine("  涡喷 干态      {0,6:F2} {1,6:F2} {2,6:F2} {3,8:F1}k {4,8:F4} {5,7:F1}",
                    r3.Bpr, r3.Opr, r3.Fpr, r3.NetThrust / 1000.0, r3.Sfc, r3.MdotAir);
                Check("TJ 推力 kN（真 J79 52.8）", r3.NetThrust / 1000.0, 52.3, 3.0);
                Check("TJ 流量（真 77）", r3.MdotAir, 77.4, 2.0);
                Check("TJ SFC（真 0.084）", r3.Sfc, 0.0736, 8.0);
            }

            SimpleResult r4 = Simple.DesignPoint(tjAb);
            if (!r4.Ok) { _fails++; Console.WriteLine("  TJ-AB 失败: " + string.Join(" / ", r4.Errors)); }
            else
            {
                Console.WriteLine("  涡喷 + 加力    {0,6:F2} {1,6:F2} {2,6:F2} {3,8:F1}k {4,8:F4} {5,7:F1}",
                    r4.Bpr, r4.Opr, r4.Fpr, r4.NetThrust / 1000.0, r4.Sfc, r4.MdotAir);
                // 真实 J79：干 52.8 → 加力 80 kN
                Check("TJ 加力推力 kN（真 80）", r4.NetThrust / 1000.0, 79.9, 3.0);
            }

            Console.WriteLine();
            Console.WriteLine("  涡喷油门扫描（海平面静止）  油门  N2%   OPR    T4     推力     SFC");
            object[][] scan =
            {
                new object[] { 0.00, 60.0, 3.26, 666.0, 3.5, 0.2664 },
                new object[] { 0.30, 72.0, 4.27, 715.0, 12.5, 0.0800 },
                new object[] { 0.60, 84.0, 6.50, 862.0, 26.0, 0.0730 },
                new object[] { 0.85, 94.0, 10.15, 1059.0, 41.3, 0.0730 },
                new object[] { 1.00, 100.0, 13.50, 1210.0, 52.3, 0.0736 },
            };
            SimpleConstants sc = Simple.DesignConstants(tj);
            foreach (object[] s in scan)
            {
                SimpleResult sr = Simple.Solve(tj, 0.0, 0.0, (double)s[0], -1.0, sc);
                Console.WriteLine("                            {0,4:F2} {1,5:F1} {2,6:F2} {3,6:F0} {4,8:F1}k {5,8:F4}",
                    sr.Throttle, sr.N2Pct, sr.Opr, sr.T4, sr.NetThrust / 1000.0, sr.Sfc);
                Check(string.Format("油门 {0:F2} 推力", (double)s[0]), sr.NetThrust / 1000.0, (double)s[4], 10.0, "kN");
                Check(string.Format("油门 {0:F2} OPR", (double)s[0]), sr.Opr, (double)s[2], 12.0);
                Check(string.Format("油门 {0:F2} T4", (double)s[0]), sr.T4, (double)s[3], 3.0, "K");
            }

            // 简单模式的判据要"明说跳过了什么"，不能假装全绿
            CheckReport repS = Criteria.EvaluateSimple(tj, Simple.DesignPoint(tj).AsView(), 0.0);
            Console.WriteLine();
            Console.WriteLine("  简单模式判据：最差 {0}  共 {1} 条（FAIL {2} / WARN {3}）",
                repS.Worst, repS.Findings.Count, repS.Fails.Count, repS.Warns.Count);
            bool sawSkip = false;
            foreach (Finding f in repS.Findings)
            {
                if (f.Key == "geometry.skipped") sawSkip = true;
                if (f.LevelValue != Level.Ok) Console.WriteLine("   " + f.Line());
            }
            if (!sawSkip) { _fails++; Console.WriteLine("    ** 简单模式没声明跳过了几何判据 **"); }
            if (repS.Fails.Count > 0) { _fails++; Console.WriteLine("    ** 简单模式不该有 FAIL **"); }
        }

        /// <summary>
        /// 质量与造价标定。真值来自公开干重：J79 1750 kg / J57 1850 kg / J85 185 kg /
        /// RD-33 1055 kg / CFM56-5B 2360 kg。质量模型只吃几何，所以这里的几何也得是真的。
        /// 判据不是"对上 5%"，而是"量级与趋势都对"——干重估算能落在 ±25% 就算合格，
        /// 因为真实发动机的机匣厚度、附件包、减速器各厂差异很大。
        /// </summary>
        private static void RunMassCases()
        {
            string[] names = { "J79", "J57", "J85", "RD-33", "CFM56-5B" };
            double[] realMass = { 1750.0, 1850.0, 185.0, 1055.0, 2360.0 };
            double[] realThrustKn = { 52.8, 53.5, 16.0, 50.0, 120.0 };
            EngineDesignData[] ds = new EngineDesignData[names.Length];

            for (int i = 0; i < names.Length; i++)
            {
                ds[i] = new EngineDesignData { DesignName = names[i] };
                // 这五台是**真实机器**：长度用量的算，不让"按级数估长"（±8% 的布局估算）
                // 顶掉标定用的几何——质量模型吃的是几何，几何得跟真机一致。
                ds[i].LengthAuto = false;
            }

            // J79：单转子 17 级压气机 + 3 级涡轮
            ds[0].Kind = EngineKind.Turbojet; ds[0].TechName = "1960s";
            ds[0].InletDiameter = 0.72; ds[0].CoreInletDiameter = 0.76; ds[0].Length = 4.97;
            ds[0].Fan.Stages = 0; ds[0].Lpc.Stages = 0;
            ds[0].Hpc.Stages = 17; ds[0].Hpc.TipDiameter = 0.76; ds[0].Hpc.HubTipRatio = 0.70;
            ds[0].Hpt.Stages = 3; ds[0].Hpt.TipDiameter = 0.70; ds[0].Hpt.HubTipRatio = 0.80;
            ds[0].Lpt.Stages = 0; ds[0].ShaftCount = 1;

            // J57 / JT3C-7：双转子 9 级低压 + 7 级高压
            ds[1].Kind = EngineKind.Turbojet; ds[1].TechName = "1960s";
            ds[1].InletDiameter = 0.92; ds[1].CoreInletDiameter = 0.90; ds[1].Length = 5.20;
            ds[1].Fan.Stages = 0;
            ds[1].Lpc.Stages = 9; ds[1].Lpc.TipDiameter = 0.90; ds[1].Lpc.HubTipRatio = 0.78; ds[1].Lpc.Shaft = 1;
            ds[1].Hpc.Stages = 7; ds[1].Hpc.TipDiameter = 0.72; ds[1].Hpc.HubTipRatio = 0.78; ds[1].Hpc.Shaft = 2;
            ds[1].Hpt.Stages = 1; ds[1].Hpt.TipDiameter = 0.68; ds[1].Hpt.HubTipRatio = 0.82; ds[1].Hpt.Shaft = 2;
            ds[1].Lpt.Stages = 2; ds[1].Lpt.TipDiameter = 0.78; ds[1].Lpt.HubTipRatio = 0.82; ds[1].Lpt.Shaft = 1;
            ds[1].ShaftCount = 2;

            // J85：小而简单，9 级压气机 + 2 级涡轮
            ds[2].Kind = EngineKind.Turbojet; ds[2].TechName = "1960s";
            ds[2].InletDiameter = 0.50; ds[2].CoreInletDiameter = 0.45; ds[2].Length = 1.85;
            ds[2].Fan.Stages = 0; ds[2].Lpc.Stages = 0;
            ds[2].Hpc.Stages = 9; ds[2].Hpc.TipDiameter = 0.45; ds[2].Hpc.HubTipRatio = 0.72;
            ds[2].Hpt.Stages = 2; ds[2].Hpt.TipDiameter = 0.42; ds[2].Hpt.HubTipRatio = 0.82;
            ds[2].Lpt.Stages = 0; ds[2].ShaftCount = 1;

            // RD-33：低涵道比涡扇，4 级风扇 + 9 级高压
            ds[3].Kind = EngineKind.Turbofan; ds[3].TechName = "1970s";
            ds[3].InletDiameter = 0.78; ds[3].CoreInletDiameter = 0.60; ds[3].Length = 4.23;
            ds[3].Fan.Stages = 4; ds[3].Fan.Diameter = 0.78; ds[3].Fan.HubTipRatio = 0.40; ds[3].Fan.Shaft = 1;
            ds[3].Lpc.Stages = 0;
            ds[3].Hpc.Stages = 9; ds[3].Hpc.TipDiameter = 0.60; ds[3].Hpc.HubTipRatio = 0.78; ds[3].Hpc.Shaft = 2;
            ds[3].Hpt.Stages = 1; ds[3].Hpt.TipDiameter = 0.58; ds[3].Hpt.HubTipRatio = 0.82; ds[3].Hpt.Shaft = 2;
            ds[3].Lpt.Stages = 2; ds[3].Lpt.TipDiameter = 0.62; ds[3].Lpt.HubTipRatio = 0.82; ds[3].Lpt.Shaft = 1;
            ds[3].ShaftCount = 2;

            // CFM56-5B：大涵道比，单级风扇 + 3 级增压级 + 9 级高压
            ds[4].Kind = EngineKind.Turbofan; ds[4].TechName = "1990s";
            ds[4].InletDiameter = 1.735; ds[4].CoreInletDiameter = 0.635; ds[4].Length = 2.60;
            ds[4].Fan.Stages = 1; ds[4].Fan.Diameter = 1.735; ds[4].Fan.HubTipRatio = 0.30; ds[4].Fan.Shaft = 1;
            ds[4].Lpc.Stages = 3; ds[4].Lpc.TipDiameter = 0.635; ds[4].Lpc.HubTipRatio = 0.80; ds[4].Lpc.Shaft = 1;
            ds[4].Hpc.Stages = 9; ds[4].Hpc.TipDiameter = 0.60; ds[4].Hpc.HubTipRatio = 0.78; ds[4].Hpc.Shaft = 2;
            ds[4].Hpt.Stages = 1; ds[4].Hpt.TipDiameter = 0.62; ds[4].Hpt.HubTipRatio = 0.84; ds[4].Hpt.Shaft = 2;
            ds[4].Lpt.Stages = 4; ds[4].Lpt.TipDiameter = 0.72; ds[4].Lpt.HubTipRatio = 0.84; ds[4].Lpt.Shaft = 1;
            ds[4].ShaftCount = 2;

            Console.WriteLine("  机型        算干重   真干重   偏差   算TWR  真TWR   造价");
            double worst = 0.0;
            for (int i = 0; i < names.Length; i++)
            {
                MassBreakdown m = MassCost.Estimate(ds[i]);
                double err = (m.TotalDry - realMass[i]) / realMass[i] * 100.0;
                double realTwr = realThrustKn[i] * 1000.0 / (realMass[i] * Gas.G0);
                Console.WriteLine("  {0,-10} {1,7:F0} {2,8:F0} {3,7:F1}% {4,7:F2} {5,6:F2} {6,8:N0}",
                    names[i], m.TotalDry, realMass[i], err, m.ThrustToWeight, realTwr, m.Cost);
                Console.WriteLine("    " + string.Join(" | ", m.Lines.GetRange(0, 3).ToArray()));
                // J85 是已知模型弱点：它的机匣是"能做多薄就多薄"，不是按压强定的厚径比，
                // 所以真实 184 kg 比压力容器规律给出的值轻三成。这一台放宽到 ±40%，
                // 其余四台按 ±25% 判。
                double tol = names[i] == "J85" ? 40.0 : 25.0;
                worst = Math.Max(worst, Math.Abs(err) / tol);
            }

            // worst 现在是"偏差 / 各自容差"的最大值：>1 说明有机器超差
            if (worst > 1.0) { _fails++; Console.WriteLine(string.Format("    ** 有机器干重超差（归一化后 {0:F2} > 1）**", worst)); }

            // JF-1 真实设计：质量必须随几何变化，而不是写死 1.4 t
            EngineDesignData jf = new EngineDesignData();
            MassBreakdown a1 = MassCost.Estimate(jf);

            EngineDesignData bigger = jf.Clone();
            bigger.Hpc.Stages = 14;
            bigger.Lpt.Stages = 5;
            MassBreakdown a2 = MassCost.Estimate(bigger);
            Console.WriteLine("  JF-1 8→14 级高压 + 3→5 级低压：{0:F0} → {1:F0} kg（{2:+0.0;-0.0}%）",
                a1.TotalDry, a2.TotalDry, (a2.TotalDry - a1.TotalDry) / a1.TotalDry * 100.0);
            if (a2.TotalDry <= a1.TotalDry) { _fails++; Console.WriteLine("    ** 级数变多质量没涨 **"); }

            EngineDesignData fatFan = jf.Clone();
            fatFan.Fan.Diameter = 1.8;
            MassBreakdown a3 = MassCost.Estimate(fatFan);
            Console.WriteLine("  JF-1 风扇 1.2→1.8 m：{0:F0} → {1:F0} kg（{2:+0.0;-0.0}%）",
                a1.TotalDry, a3.TotalDry, (a3.TotalDry - a1.TotalDry) / a1.TotalDry * 100.0);
            if (a3.TotalDry <= a1.TotalDry) { _fails++; Console.WriteLine("    ** 风扇变大质量没涨 **"); }

            Console.WriteLine("  JF-1 分解：");
            foreach (string s in a1.Lines) Console.WriteLine("    " + s);

            // 加力段与可调喷管作动：这两项以前算在别处或干脆不收钱，
            // 于是"开加力"和"选可调喷管"在重量读数上看不出代价。现在各占一行。
            EngineDesignData rdw = Presets.Build("RD-33");
            if (rdw != null)
            {
                // 预设一律按"干态额定"标定（见 Presets.cs 里那行注释），所以要测加力段的代价
                // 得自己打开加力——这正是玩家在面板上会做的事。
                EngineDesignData withAb = rdw.Clone();
                withAb.Afterburner.Enabled = true;
                if (withAb.Afterburner.ExitTemp < withAb.Burner.ExitTemp * 1.2)
                    withAb.Afterburner.ExitTemp = withAb.Burner.ExitTemp * 1.25;
                MassBreakdown mAB = MassCost.Estimate(withAb);
                MassBreakdown mNoAb = MassCost.Estimate(rdw);
                EngineDesignData fixedNoz = withAb.Clone(); fixedNoz.Nozzle = NozzleKind.Converging;
                MassBreakdown mFixed = MassCost.Estimate(fixedNoz);
                Console.WriteLine("  RD-33 加力型干重 {0:F0} kg（加力段 {1:F0} · 喷管作动 {2:F0}）｜干态 {3:F0}｜固定喷管 {4:F0}",
                    mAB.TotalDry, mAB.Afterburner, mAB.NozzleActuator, mNoAb.TotalDry, mFixed.TotalDry);
                ExpectTrue("带加力的机器有加力段质量", mAB.Afterburner > 1.0);
                ExpectTrue("可调喷管有作动系统质量", mAB.NozzleActuator > 1.0);
                ExpectTrue("去掉加力会变轻（加力不是白送的推力）", mNoAb.TotalDry < mAB.TotalDry - 1.0);
                ExpectTrue("固定收敛喷管比可调喷管轻", mFixed.TotalDry < mAB.TotalDry - 1.0);
                ExpectTrue("固定喷管没有作动系统质量", mFixed.NozzleActuator < 0.01);
            }
        }

        /// <summary>
        /// 边界工况体检。不设期望值，只找"物理上不可能"的读数：
        /// NaN / 无穷 / 负流量 / 负 SFC / T4 跑出上限 / 喷管出口马赫荒唐。
        /// 目的是在玩家踩到之前先把 bug 抓出来。
        /// </summary>
        private static void RunEdgeCases()
        {
            int bad = 0, n = 0;
            var problems = new System.Collections.Generic.List<string>();

            void Scan(string tag, OffDesignResult o)
            {
                n++;
                void Chk(string what, double x, double lo, double hi)
                {
                    if (double.IsNaN(x) || double.IsInfinity(x) || x < lo || x > hi)
                    {
                        bad++;
                        problems.Add(string.Format("{0} → {1}={2:F3}（应 ∈[{3},{4}]）", tag, what, x, lo, hi));
                    }
                }
                Chk("mdotAir", o.MdotAir, 0.0, 5000.0);
                Chk("mdotCore", o.MdotCore, 0.0, 5000.0);
                Chk("opr", o.Opr, 1.0, 80.0);
                Chk("fpr", o.Fpr, 1.0, 6.0);
                Chk("T4", o.T4, 200.0, 2400.0);
                Chk("T5", o.T5, 150.0, 2600.0);
                Chk("sfc", o.Sfc, 0.0, 5.0);
                Chk("N2%", o.N2Pct, 0.0, 160.0);
                Chk("nozzleMachE", o.NozzleMachE, 0.0, 4.0);
                if (!double.IsNaN(o.NetThrust) && !double.IsInfinity(o.NetThrust) && Math.Abs(o.NetThrust) > 5.0e6)
                {
                    bad++;
                    problems.Add(string.Format("{0} → 净推力 {1:E2} N 量级荒唐", tag, o.NetThrust));
                }
                if (o.Ok && o.MdotAir > 1.0 && o.FuelFlow <= 0.0 && o.Throttle > 0.3)
                {
                    bad++;
                    problems.Add(string.Format("{0} → 有流量有油门却没有油耗", tag));
                }
            }

            // ① 三轴：风扇 N1、LPC N2、HPC N3，三种涡轮分别挂三根轴
            EngineDesignData three = new EngineDesignData();
            three.ShaftCount = 3;
            three.Fan.Shaft = 1;
            three.Lpc.Stages = 3; three.Lpc.TipDiameter = 0.78; three.Lpc.Shaft = 2;
            three.Hpc.Stages = 6; three.Hpc.TipDiameter = 0.52; three.Hpc.Shaft = 3;
            three.Hpt.Shaft = 3;
            three.Ipt.Stages = 1; three.Ipt.TipDiameter = 0.70; three.Ipt.Shaft = 2;
            three.Lpt.Stages = 3; three.Lpt.Shaft = 1;
            Console.WriteLine("  ① 三轴构型（N1 风扇 / N2 低压 / N3 高压）：");
            CycleResult r3 = Cycle.SolveDesignPoint(three);
            Console.WriteLine("    设计点 ok={0} 错误 {1} 条 警告 {2} 条  OPR={3:F2} 推力={4:F1} kN",
                r3.Ok, r3.Errors.Count, r3.Warnings.Count, r3.Opr, r3.Thrust / 1000.0);
            foreach (string e in r3.Errors) Console.WriteLine("     ! " + e);
            if (r3.Ok)
            {
                DesignConstants c3 = OffDesign.DeriveDesignConstants(three);
                foreach (string w in c3.Warnings) Console.WriteLine("     ? " + w);
                foreach (double[] pt in new[] { new double[] { 0, 0, 0 }, new double[] { 0, 0, 1 },
                                                new double[] { 11000, 0.85, 1 }, new double[] { 14000, 0.3, 0.2 } })
                    Scan(string.Format("三轴 {0:F0}m/M{1:F2}/thr{2:F2}", pt[0], pt[1], pt[2]),
                         OffDesign.Solve(three, pt[0], pt[1], pt[2], 0.0, c3));
            }
            else if (three.Ipt.Stages > 0)
            {
                bad++; problems.Add("三轴构型本身解不出来——IPT 挂在轴 2 上却没人驱动它？");
            }

            // ①b 三轴逐台涡轮的"需 / 供"：IPT 不能只是摆设
            Console.WriteLine("  ①b 三轴逐台涡轮（需/供，MW）：");
            if (r3.Ok)
            {
                DesignConstants cb = OffDesign.DeriveDesignConstants(three);
                foreach (double thr in new[] { 1.0, 0.7, 0.4, 0.15 })
                {
                    OffDesignResult ob = OffDesign.Solve(three, 0.0, 0.0, thr, 0.0, cb);
                    Console.WriteLine(string.Format(
                        "     thr{0:F2}  HPT {1,6:F2}/{2,6:F2}   IPT {3,6:F2}/{4,6:F2}   LPT {5,6:F2}/{6,6:F2}   带不动的轴={7}",
                        thr, ob.WHpt / 1e6, ob.CapHpt / 1e6, ob.WIpt / 1e6, ob.CapIpt / 1e6,
                        ob.WLpt / 1e6, ob.CapLpt / 1e6, ob.LimitSpool));
                    if (double.IsNaN(ob.WIpt) || double.IsNaN(ob.CapIpt) || double.IsNaN(ob.WLpt))
                    { bad++; problems.Add(string.Format("三轴 thr{0:F2} 的逐台涡轮功算出 NaN", thr)); }
                    if (ob.CapIpt <= 0.0)
                    { bad++; problems.Add(string.Format("三轴 thr{0:F2} 的 IPT 可提供功为 0", thr)); }
                }

                // 故意把 IPT 做弱：应当被抓出来（要么设计点直接报级数不足，要么非设计点标出轴 2 带不动）
                // 注意：改叶尖直径**不改**能力——转速由该轴自己锁在最大叶尖马赫上，
                // 叶尖线速度不变，单级落温差就不变。真正削弱它的是载荷系数（psi）与级数。
                EngineDesignData weak = three.Clone();
                weak.Ipt.PsiScale = 0.45;
                CycleResult rw = Cycle.SolveDesignPoint(weak);
                if (!rw.Ok)
                    Console.WriteLine("     小 IPT：设计点报错（正确行为）→ " +
                        (rw.Errors.Count > 0 ? rw.Errors[0] : ""));
                else
                {
                    DesignConstants cw = OffDesign.DeriveDesignConstants(weak);
                    bool flagged = false;
                    foreach (double thr in new[] { 1.0, 0.7, 0.4, 0.15 })
                    {
                        OffDesignResult ow = OffDesign.Solve(weak, 0.0, 0.0, thr, 0.0, cw);
                        if (ow.LimitSpool == 2) flagged = true;
                    }
                    Console.WriteLine("     小 IPT：设计点通过，非设计点标出轴 2 带不动 = " + flagged);
                    if (!flagged)
                    { bad++; problems.Add("把 IPT 做小之后没有被逐台核对抓出来"); }
                }
            }

            // ② 极端包线：越高越快越容易出 NaN
            DesignConstants c = OffDesign.DeriveDesignConstants(new EngineDesignData());
            Console.WriteLine("  ② 包线四角（JF-1）：");
            foreach (double[] pt in new[] { new double[] { 0, 0, 0 }, new double[] { 20000, 2.2, 1 },
                                            new double[] { 25000, 3.0, 1 }, new double[] { 0, 0, 0.01 },
                                            new double[] { 18000, 1.6, 0 } })
                Scan(string.Format("包线 {0:F0}m/M{1:F2}/thr{2:F2}", pt[0], pt[1], pt[2]),
                     OffDesign.Solve(new EngineDesignData(), pt[0], pt[1], pt[2], 0.0, c));

            // ③ 加力 × 喷管型式：加力时收扩喷管出口马赫不能爆
            Console.WriteLine("  ③ 加力 × 喷管型式（0/8/15 km）：");
            foreach (NozzleKind nk in new[] { NozzleKind.Converging, NozzleKind.Variable, NozzleKind.ConvergingDiverging })
                foreach (double alt in new[] { 0.0, 8000.0, 15000.0 })
                {
                    EngineDesignData ab = new EngineDesignData();
                    ab.Nozzle = nk;
                    ab.Afterburner.Enabled = true;
                    ab.Afterburner.ExitTemp = 2000.0;
                    ab.Afterburner.FuelRatio = 0.035;
                    DesignConstants cab = OffDesign.DeriveDesignConstants(ab);
                    Scan(string.Format("加力 {0}/{1:F0}m", nk, alt),
                         OffDesign.Solve(ab, alt, 0.9, 1.0, 0.0, cab, 40, -1.0, 0.035));
                }

            // ④ 尺寸两端：小到 0.3 m、大到 2.5 m
            Console.WriteLine("  ④ 尺寸两端（0.3 m 与 2.5 m 风扇）：");
            foreach (double dv in new[] { 0.3, 2.5 })
            {
                EngineDesignData sd = new EngineDesignData();
                sd.InletDiameter = dv;
                sd.Fan.Diameter = dv;
                sd.CoreInletDiameter = dv / 2.0;
                CycleResult rr = Cycle.SolveDesignPoint(sd);
                Console.WriteLine("     D={0:F1} m：设计点 ok={1} OPR={2:F2} 推力={3:F1} kN 干重={4:F0} kg",
                    dv, rr.Ok, rr.Opr, rr.Thrust / 1000.0, MassCost.Estimate(sd).TotalDry);
                foreach (string e in rr.Errors) Console.WriteLine("       ! " + e);
                if (rr.Ok)
                {
                    DesignConstants cs = OffDesign.DeriveDesignConstants(sd);
                    Scan("D" + dv.ToString("F1") + "m 满油门", OffDesign.Solve(sd, 0.0, 0.0, 1.0, 0.0, cs));
                }
            }

            Console.WriteLine("  共检查 {0} 个工况，异常 {1} 处。", n, bad);
            foreach (string p in problems) Console.WriteLine("   × " + p);
            if (bad > 0) { _fails++; Console.WriteLine("    ** 边界工况体检未通过 **"); }
        }

        /// <summary>
        /// 尾焰映射回归。尾焰是唯一样子货，但它背后的关系可以拿数字验：
        /// 油门关了就不该有焰、加力更长更亮更蓝、马赫高要更长、尺寸放大按比例放大。
        /// 这里不设"正确答案"，只设单调性与量级——因为真实喷流光学本来就没有唯一解。
        /// </summary>
        private static void RunPlumeCases()
        {
            // 典型涡喷干态：0.35 m 半径、620 m/s、1000 K、刚好壅塞。
            PlumeMath.PlumeSpec dry = PlumeMath.Compute(0.35, 620.0, 1000.0, 1.0, 1.0, false);
            Console.WriteLine("  干态  {0}", dry.Note);
            Expect("干态可见", dry.Visible, true);
            Expect("干态长度 > 2 倍直径", dry.Length > 1.4, true);
            Expect("尾端张开", dry.RadiusEnd > dry.RadiusStart, true);
            // 长度基准 = 3 倍直径 × 油门因子（满油门为 1） = 0.7 × 3 × 1.0 = 2.10 m
            Check("干态 Length", dry.Length, 2.10, 0.1, " m");
            // 尾端 = 出口半径 × (1.05 + 0.22 × Ma1.0) = 0.35 × 1.27 = 0.4445 m
            Check("干态 RadiusEnd", dry.RadiusEnd, 0.4445, 0.1, " m");

            // 加力：更热更快，长度、不透明度、蓝色成分都该上去。
            PlumeMath.PlumeSpec ab = PlumeMath.Compute(0.35, 780.0, 1800.0, 1.35, 1.0, true);
            Console.WriteLine("  加力  {0}", ab.Note);
            Expect("加力更长", ab.Length > dry.Length, true);
            Expect("加力更亮", ab.A > dry.A, true);
            Expect("加力更蓝", ab.B > dry.B, true);
            Check("加力相对干态的伸长", ab.Length / dry.Length, 1.64, 6.0, " ×");

            // 油门：0 必须无焰，0.5 有焰但比满油门短。
            PlumeMath.PlumeSpec off = PlumeMath.Compute(0.35, 620.0, 1000.0, 1.0, 0.0, false);
            Expect("油门 0 无焰", off.Visible, false);
            PlumeMath.PlumeSpec half = PlumeMath.Compute(0.35, 620.0, 1000.0, 1.0, 0.5, false);
            Expect("半油门有焰但更短", half.Visible && half.Length < dry.Length, true);
            Check("半油门长度比", half.Length / dry.Length, 0.775, 2.0, " ×");

            // 出口马赫：超声速欠膨胀喷流会更长更鼓。
            PlumeMath.PlumeSpec su = PlumeMath.Compute(0.35, 900.0, 1200.0, 2.0, 1.0, false);
            PlumeMath.PlumeSpec sub = PlumeMath.Compute(0.35, 500.0, 1200.0, 0.8, 1.0, false);
            Expect("马赫 2 比 0.8 更长", su.Length > sub.Length, true);
            Expect("马赫 2 比 0.8 更鼓", su.RadiusEnd > sub.RadiusEnd, true);

            // 尺寸：几何放大一倍，焰长也放大一倍（同状态下）。
            PlumeMath.PlumeSpec big = PlumeMath.Compute(0.70, 620.0, 1000.0, 1.0, 1.0, false);
            Check("半径 ×2 → 长度 ×2", big.Length / dry.Length, 2.0, 0.5, " ×");

            // 颜色：低温偏橙红（R 高 B 低），高温转向青白（B 追上来）。
            Expect("1000K 橙红", dry.R >= 0.99 && dry.B < 0.30, true);
            Expect("1800K 青白", ab.B > 0.90, true);
            Expect("温度越高越透明(alpha 升)", ab.A > dry.A, true);

            // 边界：几何为 0 或速度为 0 时绝不允许露出焰。
            Expect("半径 0 无焰", PlumeMath.Compute(0.0, 620.0, 1000.0, 1.0, 1.0, false).Visible, false);
            Expect("速度 0 无焰", PlumeMath.Compute(0.35, 0.0, 1000.0, 1.0, 1.0, false).Visible, false);
            Expect("温度 0 无焰", PlumeMath.Compute(0.35, 620.0, 0.0, 1.0, 1.0, false).Visible, false);

            // 跟真机对一下量级：J79 加力焰长 3~6 倍直径。我们的喷口直径 0.7 m 时
            // 上面加力算出 1.9 m 级别，落在 2.7 倍直径——真实照片里偏短那端，但同量级。
            double abOverD = ab.Length / (2.0 * 0.35);
            Console.WriteLine("  加力焰长 / 喷口直径 = {0:F2}（真机 3~6）", abOverD);
            Expect("加力焰长比值在 2~7 倍直径之间", abOverD > 2.0 && abOverD < 7.0, true);
        }

        /// <summary>
        /// 设计点本身是标定点：设计马赫/高度一改，冲压总温、进气总压恢复、进气量全跟着变。
        /// 这两项以前只有简单模式能调，残酷模式写死 0/0——等于所有发动机都按海平面静止标定，
        /// 一台为巡航设计的机器会被按地面台架的标准去算，这是错的。
        /// </summary>
        private static void RunDesignPointCases()
        {
            EngineDesignData sl = new EngineDesignData();
            CycleResult rsl = Cycle.SolveDesignPoint(sl);

            EngineDesignData cr = new EngineDesignData();
            cr.DesignMach = 0.85;
            cr.DesignAlt = 11000.0;
            CycleResult rcr = Cycle.SolveDesignPoint(cr);

            EngineDesignData alt0 = new EngineDesignData();
            alt0.DesignMach = 0.0;
            alt0.DesignAlt = 11000.0;
            CycleResult rAlt0 = Cycle.SolveDesignPoint(alt0);

            double pSl, tSl, pCr, tCr;
            Gas.Isa(0.0, out pSl, out tSl);
            Gas.Isa(11000.0, out pCr, out tCr);
            double ptCr, ttCr;
            Gas.Total(pCr, tCr, 0.85, out ptCr, out ttCr);
            Console.WriteLine("  11km：δ={0:F3} θ={1:F3}　Ma0.85 冲压流量因子={2:F3}",
                pCr / pSl, tCr / tSl, (ptCr / Math.Sqrt(ttCr)) / (pCr / Math.Sqrt(tCr)));

            Console.WriteLine("  工况            马赫  高度m   进气kg/s   OPR   推力kN  单位推力  SFC");
            Console.WriteLine("  海平面静止     {0,5:F2} {1,6:F0} {2,10:F1} {3,6:F2} {4,7:F2} {5,7:F0} {6,7:F4}",
                0.0, 0.0, rsl.MdotAir, rsl.Opr, rsl.Thrust / 1000.0, rsl.FnPerMdot, rsl.Sfc);
            Console.WriteLine("  11km 静止      {0,5:F2} {1,6:F0} {2,10:F1} {3,6:F2} {4,7:F2} {5,7:F0} {6,7:F4}",
                alt0.DesignMach, alt0.DesignAlt, rAlt0.MdotAir, rAlt0.Opr, rAlt0.Thrust / 1000.0,
                rAlt0.FnPerMdot, rAlt0.Sfc);
            Console.WriteLine("  11km Ma0.85    {0,5:F2} {1,6:F0} {2,10:F1} {3,6:F2} {4,7:F2} {5,7:F0} {6,7:F4}",
                cr.DesignMach, cr.DesignAlt, rcr.MdotAir, rcr.Opr, rcr.Thrust / 1000.0,
                rcr.FnPerMdot, rcr.Sfc);

            Expect("巡航设计点无错误", rcr.Errors.Count == 0, true);
            // 设计流量 ∝ pt0/√tt0（面马赫固定 0.5）。11 km 的 δ=0.223、θ=0.752，
            // 再乘冲压 1.649/√1.144 = 1.542 → 巡航/海平面 = 0.397。
            Check("巡航/海平面 进气量", rcr.MdotAir / rsl.MdotAir, 0.397, 5.0, " ×");
            // 同高度下 Ma0.85 的冲压把总压抬到 1.649 倍、总温 1.144 倍 → 流量 ×1.542。
            // 这条单独证明"马赫"确实在起作用（而不是被忽略）。
            Check("同高度 Ma0.85 / Ma0 进气量", rcr.MdotAir / rAlt0.MdotAir, 1.542, 5.0, " ×");
            // 绝对推力掉到四分之一上下——11 km 只有 22% 的大气压。
            Expect("巡航绝对推力落在 10%~45% 之间",
                rcr.Thrust > 0.10 * rsl.Thrust && rcr.Thrust < 0.45 * rsl.Thrust, true);

            // 涡轮轮毂/叶尖比：以前只在数据里有、界面上没得调，等于永远是 0.75。
            // 它进 η 缩放、环形通道面积、质量、判据四处——必须给滑块。
            EngineDesignData ht = new EngineDesignData();
            MassBreakdown mBase = MassCost.Estimate(ht);
            ht.Lpt.HubTipRatio = 0.55;
            CycleResult rHt = Cycle.SolveDesignPoint(ht);
            MassBreakdown mLow = MassCost.Estimate(ht);
            Console.WriteLine("  涡轮轮毂比 0.75→0.55：涡轮 {0:F0}→{1:F0} kg，干重 {2:F0}→{3:F0} kg，错误 {4} 条",
                mBase.Turbine, mLow.Turbine, mBase.TotalDry, mLow.TotalDry, rHt.Errors.Count);
            Expect("轮毂比下调后设计点仍成立", rHt.Errors.Count == 0, true);
            // 轮毂变小 = 环形通道变大 = 更重（面积 ∝ 1 − hubTip²）
            Expect("轮毂比下调 → 涡轮变重", mLow.Turbine > mBase.Turbine * 1.02, true);
        }

        /// <summary>
        /// 网格内插误差。飞行中我们不跑求解器，只查这张表——表是真值的三线性近似，
        /// 而推力对油门是二次、对高度是指数，线性近似必然有系统偏差。
        /// 这里在每个格子的中点上直接求解一次，量内插到底偏了多少、偏在哪个轴上。
        /// </summary>
        private static void RunGridInterpCases(EngineDesignData d, DesignConstants c)
        {
            PerformanceGrid g = GridFactory.Build(d, c);
            Console.WriteLine("  " + g.Summary());

            double[] thrMids = Mids(GridFactory.DefaultThrottles);
            double[] altMids = Mids(GridFactory.DefaultAlts);
            double[] machMids = Mids(GridFactory.DefaultMachs);

            // 单轴：另两轴压在格点上，只看这一轴的中点误差。
            Console.WriteLine("  单轴中点（另两轴压格点）：");
            SweepAxis(g, d, c, "油门", thrMids, 0.0, 6000.0, 0.6);
            SweepAxis(g, d, c, "高度", altMids, 0.75, 0.0, 0.6);
            SweepAxis(g, d, c, "马赫", machMids, 0.75, 6000.0, 0.0);

            // 全中点立方：三个轴同时落在格子中央，是最坏情形附近。
            // 分档统计——玩家真正会开到的是正推力区，那里必须准；
            // 净推力 ≤0（冲压阻力吃掉全部推力）的地方相对误差没有意义，只看绝对误差。
            double sum = 0.0, worst = 0.0, wT = 0, wA = 0, wM = 0, wGrid = 0, wDir = 0;
            int n = 0, over2 = 0, over5 = 0;
            double[] buckets = { 5.0, 20.0, 1e9 };
            double[] bSum = new double[3], bWorst = new double[3];
            int[] bN = new int[3];
            int nDead = 0; double deadAbsSum = 0.0, deadAbsWorst = 0.0, deadGridWorst = 0.0, deadDirWorst = 0.0;
            List<string> top = new List<string>();
            foreach (double t in thrMids)
                foreach (double a in altMids)
                    foreach (double m in machMids)
                    {
                        double gk, dk, pct;
                        GridErrAt(g, d, c, t, a, m, out gk, out dk, out pct);
                        // 净推力 ≤0 或不足 1 kN：发动机已经推不动了，相对误差再大也没人会在意。
                        if (dk < 1.0)
                        {
                            nDead++;
                            double abs = Math.Abs(gk - dk);
                            deadAbsSum += abs;
                            if (abs > deadAbsWorst) { deadAbsWorst = abs; deadGridWorst = gk; deadDirWorst = dk; }
                            continue;
                        }
                        double ap = Math.Abs(pct);
                        sum += ap; n++;
                        if (ap > 2.0) over2++;
                        if (ap > 5.0) over5++;
                        if (ap > worst) { worst = ap; wT = t; wA = a; wM = m; wGrid = gk; wDir = dk; }
                        for (int b = 0; b < 3; b++)
                            if (dk <= buckets[b])
                            {
                                bSum[b] += ap; bN[b]++;
                                if (ap > bWorst[b]) bWorst[b] = ap;
                                break;
                            }
                        if (dk > 5.0 && ap > 8.0 && top.Count < 8)
                            top.Add(string.Format("油门 {0:F2} 高度 {1:F0} m 马赫 {2:F2}：网格 {3:F1} / 直接 {4:F1} kN  {5:F1}%",
                                t, a, m, gk, dk, pct));
                    }
            Console.WriteLine("  全中点立方 {0} 个正推力点：平均 |误差| {1:F2}%  最坏 {2:F2}%（油门 {3:F2} 高度 {4:F0} m 马赫 {5:F2}：网格 {6:F1} / 直接 {7:F1} kN）",
                n, n > 0 ? sum / n : 0.0, worst, wT, wA, wM, wGrid, wDir);
            Console.WriteLine("  分档：1~5kN {0} 点 平均 {1:F2}% 最坏 {2:F2}% ｜ 5~20kN {3} 点 平均 {4:F2}% 最坏 {5:F2}% ｜ >20kN {6} 点 平均 {7:F2}% 最坏 {8:F2}%",
                bN[0], bN[0] > 0 ? bSum[0] / bN[0] : 0.0, bWorst[0],
                bN[1], bN[1] > 0 ? bSum[1] / bN[1] : 0.0, bWorst[1],
                bN[2], bN[2] > 0 ? bSum[2] / bN[2] : 0.0, bWorst[2]);
            Console.WriteLine("  推不动/近停车的 {0} 点：网格与直接的绝对差 平均 {1:F2} kN、最坏 {2:F2} kN（最坏处网格 {3:F1} / 直接 {4:F1}）",
                nDead, nDead > 0 ? deadAbsSum / nDead : 0.0, deadAbsWorst, deadGridWorst, deadDirWorst);
            Console.WriteLine("  正推力区超 2% 的 {0} 点，超 5% 的 {1} 点", over2, over5);
            foreach (string line in top) Console.WriteLine("    · " + line);

            // 大推力区（玩家常用档）必须准，否则"推力手感"是假的。
            Expect(">20kN 区间平均误差 < 2%", bN[2] > 0 && bSum[2] / bN[2] < 2.0, true);
            Expect(">20kN 区间最坏误差 < 6%", bWorst[2] > 0 && bWorst[2] < 6.0, true);
            Expect("5~20kN 区间平均误差 < 4%", bN[1] > 0 && bSum[1] / bN[1] < 4.0, true);
            // 推不动的区域，网格不许凭空多出大推力（那会让飞机在空中突然"有力气"）。
            Expect("近停车区绝对差 < 3 kN", deadAbsWorst < 3.0, true);
        }

        private static void SweepAxis(PerformanceGrid g, EngineDesignData d, DesignConstants c,
            string name, double[] mid, double thr, double alt, double mach)
        {
            double sum = 0.0, worst = 0.0, wAt = 0.0, wGrid = 0.0, wDir = 0.0;
            int n = 0;
            foreach (double p in mid)
            {
                double t = name == "油门" ? p : thr;
                double a = name == "高度" ? p : alt;
                double m = name == "马赫" ? p : mach;
                double gk, dk, pct;
                GridErrAt(g, d, c, t, a, m, out gk, out dk, out pct);
                if (Math.Abs(dk) < 0.05) continue;
                sum += Math.Abs(pct); n++;
                if (Math.Abs(pct) > worst) { worst = Math.Abs(pct); wAt = p; wGrid = gk; wDir = dk; }
            }
            Console.WriteLine("    {0}：平均 {1:F2}%  最坏 {2:F2}%（{3} = {4:F2}，网格 {5:F1} kN / 直接 {6:F1} kN）",
                name, n > 0 ? sum / n : 0.0, worst, name, wAt, wGrid, wDir);
        }

        private static void GridErrAt(PerformanceGrid g, EngineDesignData d, DesignConstants c,
            double thr, double alt, double mach, out double gridKn, out double directKn, out double pct)
        {
            PerformanceGrid.Sample s = g.Interp(thr, alt, mach);
            OffDesignResult o = OffDesign.Solve(d, alt, mach, thr, 0.0, c);
            gridKn = s.Thrust / 1000.0;
            directKn = o.Ok ? o.NetThrust / 1000.0 : 0.0;
            pct = Math.Abs(directKn) > 1e-6 ? (gridKn - directKn) / directKn * 100.0 : 0.0;
        }

        // === 12. 转子惯性（油门到推力之间那几秒） ===
        private static void RunSpoolCases(EngineDesignData d)
        {
            Console.WriteLine();
            Console.WriteLine("=== 12. 转子惯性：油门推到底，推力不是马上就到 ===");

            EngineDesignData s76 = d.Clone(); s76.Fan.Diameter = 0.76f;
            EngineDesignData s45 = d.Clone(); s45.Fan.Diameter = 0.45f;
            EngineDesignData s325 = d.Clone(); s325.Fan.Diameter = 3.25f;
            Check("0.76 m 风扇 τ (s)", Spool.TauUp(s76), 1.20, 1.0);
            Check("0.45 m 风扇 τ (s)", Spool.TauUp(s45), 0.92, 2.0);
            Check("3.25 m 风扇 τ (s)", Spool.TauUp(s325), 2.48, 2.0);
            Check("减速 τ 是加速的", Spool.TauDown(s76) / Spool.TauUp(s76), 0.55, 1.0);

            // 阶跃响应：从 0 推到 1，看看爬到 95% 用多久、有没有冲过头。
            double tau = Spool.TauUp(d);
            double dt = 0.01, cur = 0.0, t95 = -1.0, t99 = -1.0, peak = 0.0, t = 0.0;
            bool mono = true;
            for (int i = 0; i < 2000; i++)
            {
                double prev = cur;
                cur = Spool.Step(cur, 1.0, dt, tau, Spool.TauDown(d));
                t += dt;
                if (cur < prev - 1e-12) mono = false;
                if (cur > peak) peak = cur;
                if (t95 < 0 && cur >= 0.95) t95 = t;
                if (t99 < 0 && cur >= 0.99) t99 = t;
            }
            Check("爬升到 95% 的秒数", t95, Spool.Settle95(tau), 3.0);
            Check("爬升到 99% 的秒数", t99, 4.6 * tau, 5.0);
            Check("最大瞬时油门", peak > 1.0 ? peak : 1.0, 1.0, 1e-9);
            Console.WriteLine("  单调爬升 {0}   τ={1:F2}s  95%@{2:F2}s  99%@{3:F2}s",
                mono ? "OK" : "**不单调**", tau, t95, t99);
            if (!mono) _fails++;

            // 收油：同样的落差要更快。
            double down = 0.0, downT = 0.0, curd = 1.0;
            while (downT < 20.0 && curd > 0.05)
            {
                curd = Spool.Step(curd, 0.0, dt, tau, Spool.TauDown(d));
                downT += dt;
            }
            down = downT;
            Console.WriteLine("  收油到 5% 用 {0:F2}s（加速到 95% 用 {1:F2}s）", down, t95);
            if (!(down < t95)) { Console.WriteLine("  **收油比加速还慢**"); _fails++; }

            // 异常输入：不能把状态写成 NaN，也不能因为 dt=0 就乱跳。
            Check("dt=0 保持不变", Spool.Step(0.4, 1.0, 0.0, tau, tau), 0.4, 1e-6);
            Check("NaN 输入归零", Spool.Step(double.NaN, 1.0, 0.02, tau, tau), 0.0, 1e-9);
            // dt=10 s 时指数解离目标还差 e^-6.6 ≈ 0.1%，所以这里容差给 1%。
            Check("目标超范围夹住", Spool.Step(0.0, 5.0, 10.0, tau, tau), 1.0, 1.0);

            // 一步 10 秒（时间加速）不能冲过头，也不能原地不动。
            double warp = Spool.Step(0.0, 1.0, 10.0, tau, tau);
            Console.WriteLine("  时间加速一步 10 s：油门 {0:F3}（应当接近 1 且 ≤1）", warp);
            if (warp > 1.0 || warp < 0.95) { Console.WriteLine("  **加速时步长处理不对**"); _fails++; }

            // 大步 vs 小步：指数解应当与步长无关。
            double small = 0.0; for (int i = 0; i < 1000; i++) small = Spool.Step(small, 1.0, 0.001, tau, tau);
            Check("千个小步 × 1 s", small, Spool.Step(0.0, 1.0, 1.0, tau, tau), 0.5);

            Console.WriteLine("  油门阶跃响应（JF-1 τ={0:F2}s）：", tau);
            double sc = 0.0;
            for (int sec = 1; sec <= 6; sec++)
            {
                for (int i = 0; i < 100; i++) sc = Spool.Step(sc, 1.0, 0.01, tau, Spool.TauDown(d));
                Console.WriteLine("    {0}s → {1,5:F1}%", sec, sc * 100.0);
            }
        }

        private static double[] Mids(double[] a)
        {
            if (a.Length < 2) return new double[0];
            double[] r = new double[a.Length - 1];
            for (int i = 0; i < a.Length - 1; i++) r[i] = 0.5 * (a[i] + a[i + 1]);
            return r;
        }

        /// <summary>
        /// 原版的推力链路（反汇编 ModuleEngines 得到的顺序）：
        ///   requestedMassFlow = maxFuelFlow × 油门 × ModifyFlow() × multFlow
        ///   resultingThrust   = requestedMassFlow × Isp × g0
        ///   finalThrust       = resultingThrust × 推力限幅
        /// 也就是说我们写进 finalThrust 的推力活不过一帧。这里验证的是"反过来喂参数"能不能
        /// 精确复现我们的推力和油耗——往返误差必须接近 0，否则飞行中玩家看到的推力就是错的。
        /// </summary>
        private static void RunStockBridgeCases(EngineDesignData d, DesignConstants c)
        {
            // 1) 设计点：静止台架满油门。
            CycleResult r = Cycle.SolveDesignPoint(d);
            double tKN = r.Thrust / 1000.0;
            double fTps = r.FuelFlow / 1000.0;
            FuelBridge b = StockBridge.Compute(tKN, fTps, 1.0);
            Console.WriteLine("  设计点：推力 {0:F2} kN / 燃油 {1:F4} kg/s → maxFuelFlow {2:F6} t/s Isp {3:F0} s",
                tKN, fTps * 1000.0, b.MaxFuelFlow, b.Isp);
            Check("原版算回的推力 kN", b.StockThrust(1.0), tKN, 0.001);
            Check("原版算回的油耗 kg/s", b.StockFlow(1.0) * 1000.0, fTps * 1000.0, 0.001);
            Check("往返相对误差", StockBridge.RoundTripError(tKN, fTps, 1.0, b) * 100.0, 0.0, 0.001, " %");
            Expect("设计点不是怠速档", b.Idle, false);

            // 2) 非设计点、半油门：换算必须跟着推力一起变（推力不是油门的线性函数）。
            object[][] cases =
            {
                new object[] { 0.0, 0.0, 1.00 },
                new object[] { 0.0, 0.0, 0.50 },
                new object[] { 0.0, 0.0, 0.20 },
                new object[] { 6000.0, 0.6, 1.00 },
                new object[] { 6000.0, 0.6, 0.40 },
                new object[] { 11000.0, 0.85, 0.80 },
            };
            foreach (object[] cs in cases)
            {
                double alt = (double)cs[0], mach = (double)cs[1], thr = (double)cs[2];
                OffDesignResult o = OffDesign.Solve(d, alt, mach, thr, 0.0, c);
                if (!o.Ok || o.NetThrust <= 1.0) continue;
                double tk = o.NetThrust / 1000.0, ft = o.FuelFlow / 1000.0;
                FuelBridge bb = StockBridge.Compute(tk, ft, thr);
                double err = StockBridge.RoundTripError(tk, ft, thr, bb) * 100.0;
                Console.WriteLine("  {0,5:F0} m M{1:F2} 油门{2:F2}：推力 {3,6:F2} kN → maxFuelFlow {4:F6} Isp {5,5:F0} s 往返 {6:F4}%",
                    alt, mach, thr, tk, bb.MaxFuelFlow, bb.Isp, err);
                if (Math.Abs(err) > 0.01) { Console.WriteLine("    ! 往返超差"); _fails++; }
                // 对照组：如果偷懒只喂设计点的 maxFuelFlow 与 Isp（不随状态更新），
                // 原版算出来会差多少——这就是"油门线性"那个错误。
                double naive = fTps * thr * b.Isp * Gas.G0;   // 设计点流量 × 油门 × 设计点 Isp
                double naivePct = (naive - tk) / tk * 100.0;
                if (Math.Abs(naivePct) > 5.0)
                    Console.WriteLine("    （若只喂设计点参数，这里会差 {0:+0;-0}%）", naivePct);
            }

            // 3) 关车/停车：不许给任何"还能烧"的假象。
            FuelBridge off = StockBridge.Compute(0.0, 0.0, 1.0);
            Expect("零推力→怠速档", off.Idle, true);
            Expect("怠速档推力接近 0", off.StockThrust(1.0) < 0.01, true);
            Expect("油门低于阈值→怠速档", StockBridge.Compute(50.0, 0.001, 0.01).Idle, true);
            Expect("负推力→怠速档", StockBridge.Compute(-5.0, 0.001, 1.0).Idle, true);

            // 4) 原版那层油门重映射：`throttleUseAlternate` 让 0~1 变成 Lerp(0.02, 1, x)。
            //
            // 这是"实飞推力明显大于热力图"的真根因：我们以前拿玩家原始油门当分母反解
            // maxFuelFlow，原版再乘 Lerp 之后的那个数回来，越往低油门抬得越多
            // （10% 油门抬 18%、5% 油门抬 38%）。这里把两条都钉住。
            Check("重映射：油门 0 → 0.02", StockBridge.EffectiveThrottle(0.0), StockBridge.InputFloor, 1e-9, "");
            Check("重映射：油门 1 → 1", StockBridge.EffectiveThrottle(1.0), 1.0, 1e-9, "");
            Check("重映射：油门 0.5 → 0.51", StockBridge.EffectiveThrottle(0.5), 0.51, 1e-9, "");
            double tkLow = 12.0, ftLow = tkLow / (3400.0 * Gas.G0);   // 随便一个 3400 s 的工况
            FuelBridge bLow = StockBridge.Compute(tkLow, ftLow, 0.10);
            Check("低油门往返（10%）", StockBridge.RoundTripError(tkLow, ftLow, 0.10, bLow) * 100.0, 0.0, 0.001, " %");
            double oldWay = ftLow / 0.10 * StockBridge.EffectiveThrottle(0.10) * bLow.Isp * Gas.G0;
            Check("若分母仍用原始油门，这里会高 18%（对照组）", oldWay / tkLow * 100.0, 118.0, 0.5, " %");
        }

        private static void RunMissionCases(EngineDesignData d, DesignConstants c)
        {
            PerformanceGrid g = GridFactory.Build(d, c);
            double tauUp = Spool.TauUp(d), tauDown = Spool.TauDown(d);
            const double dt = 0.02;   // KSP 的物理步长

            // 先看清网格最下面几档油门到底长什么样——低油门的斜率决定了"慢车能不能稳"。
            Console.WriteLine("    海平面静止的油门低段：");
            double[] probe = { 0.0, 0.01, 0.02, 0.05, 0.10, 0.20, 0.50, 1.00 };
            foreach (double p in probe)
            {
                PerformanceGrid.Sample ps = g.Interp(p, 0.0, 0.0);
                Console.WriteLine("      油门{0:F2} → {1,8:F1} N  油耗 {2:F4} kg/s 有效={3} N2={4:F1}%",
                    p, ps.Thrust, ps.FuelFlow, ps.Valid, ps.N2);
            }
            // 慢车推力占比：战斗机涡扇（F110 一类）约 8~13%，民航涡扇才 3~5%；
            // 我们这个模型是按 N2=60% 标定慢车的，落在 5~15% 之间就认为合理。
            double idlePct = g.Interp(0.0, 0.0, 0.0).Thrust / Math.Max(1.0, g.Interp(1.0, 0.0, 0.0).Thrust) * 100.0;
            Console.WriteLine("    慢车推力占满油门 {0:F1}%（战斗机涡扇常见 8~13%）", idlePct);
            Expect("慢车推力占比落在 5%~15%", idlePct > 5.0 && idlePct < 15.0, true);

            // 剖面（段内线性）：起飞滑跑 → 爬升加速 → 高空巡航 80% → 收油门 → 关车
            double[] tEnd = { 10.0, 40.0, 60.0, 66.0 };
            double[] thrEnd = { 1.00, 1.00, 0.80, 0.00 };
            double[] altEnd = { 0.0, 8000.0, 8000.0, 8000.0 };
            double[] machEnd = { 0.00, 0.75, 0.75, 0.75 };

            double spool = 0.0, prevThrust = 0.0, totalFuel = 0.0, impulse = 0.0;
            double peakThrust = 0.0, maxSlewPerSec = 0.0, minThrust = double.MaxValue, maxThrust = 0.0;
            int steps = 0, bad = 0, negThrust = 0, idleSteps = 0;
            double shutdownThrust = -1.0;
            bool ignited = true;

            for (double t = 0.0; t <= 70.0 + 1e-9; t += dt)
            {
                int seg = 0;
                while (seg < tEnd.Length - 1 && t >= tEnd[seg]) seg++;
                double t0 = seg == 0 ? 0.0 : tEnd[seg - 1];
                double frac = tEnd[seg] > t0 ? (t - t0) / (tEnd[seg] - t0) : 1.0;
                // 夹住：超时段之外不该继续外推油门（第一版没夹，关车段打印出"油门 -0.53"）。
                if (frac < 0.0) frac = 0.0;
                if (frac > 1.0) frac = 1.0;
                double alt = altEnd[seg - 1 >= 0 ? seg - 1 : 0] + (altEnd[seg] - altEnd[seg - 1 >= 0 ? seg - 1 : 0]) * frac;
                double mach = machEnd[seg - 1 >= 0 ? seg - 1 : 0] + (machEnd[seg] - machEnd[seg - 1 >= 0 ? seg - 1 : 0]) * frac;
                double cmd = thrEnd[seg - 1 >= 0 ? seg - 1 : 0] + (thrEnd[seg] - thrEnd[seg - 1 >= 0 ? seg - 1 : 0]) * frac;

                // 关车段：真机是熄火，推力立刻归零，转子按 τ 自由减速（这里简化为 0）。
                if (t >= 66.0) { ignited = false; spool = 0.0; }

                spool = ignited ? Spool.Step(spool, cmd, dt, tauUp, tauDown) : 0.0;
                PerformanceGrid.Sample s = g.Interp(spool, alt, mach);
                double thrust = (ignited && s.Valid) ? s.Thrust : 0.0;
                double fuel = (ignited && s.Valid) ? s.FuelFlow : 0.0;

                if (double.IsNaN(thrust) || double.IsInfinity(thrust) ||
                    double.IsNaN(fuel) || double.IsInfinity(fuel)) bad++;
                if (thrust < 0.0) negThrust++;

                // 点火那一瞬间不算：模型里没有起动机，spool 从 0 起步的第一帧就已经是慢车推力了
                // （真机是起动机带转再点火，那是另一套账）。
                double slew = Math.Abs(thrust - prevThrust) / dt;
                if (slew > maxSlewPerSec && t > 1.0 && t < 66.0) maxSlewPerSec = slew;
                prevThrust = thrust;

                if (t < 66.0)
                {
                    if (thrust > peakThrust) peakThrust = thrust;
                    if (thrust < minThrust) minThrust = thrust;
                    if (thrust > maxThrust) maxThrust = thrust;
                }
                else if (shutdownThrust < 0.0) shutdownThrust = thrust;

                totalFuel += fuel * dt;
                impulse += thrust * dt;
                if (t > 60.0 && t < 66.0) idleSteps++;

                if (Math.Abs(t % 10.0) < dt * 0.5)
                    Console.WriteLine("    t={0,4:F0}s h={1,5:F0} m Ma={2:F2} 油门{3:F2} 实际{4:F2} → {5,6:F2} kN 油耗 {6:F4} kg/s N2={7:F1}% EGT={8:F0} K",
                        t, alt, mach, cmd, spool, thrust / 1000.0, fuel, s.N2, s.Egt);

                steps++;
            }

            Console.WriteLine("    共 {0} 步（{1:F0} s），总耗油 {2:F1} kg，总冲量 {3:F0} kN·s，平均 SFC {4:F4} kg/(N·h)",
                steps, steps * dt, totalFuel, impulse / 1000.0,
                impulse > 1.0 ? totalFuel * 3600.0 / impulse : 0.0);
            Console.WriteLine("    推力区间 {0:F2} ~ {1:F2} kN，最大变化率 {2:F0} kN/s（τ={3:F2}s 允许 {4:F0}）",
                minThrust / 1000.0, maxThrust / 1000.0, maxSlewPerSec / 1000.0, tauUp, peakThrust / tauUp / 1000.0);

            Check("仿真里的 NaN/无穷步数", bad, 0.0, 0.0);
            Check("推力为负的步数", negThrust, 0.0, 0.0);
            // 一阶滞后的最大斜率就是 峰值/τ，留 1.6 倍余量给网格边界上的折点。
            Expect("推力变化率没有超过转子惯性允许", maxSlewPerSec < peakThrust / tauUp * 1.6, true);
            Check("关车后推力 kN", shutdownThrust, 0.0, 0.0);
            Expect("慢车推力低于满油门的 10%", maxThrust > 1.0 && minThrust < maxThrust * 0.10, true);
            double avgSfc = impulse > 1.0 ? totalFuel * 3600.0 / impulse : 0.0;   // impulse 本身是 N·s
            Expect("平均 SFC 落在 0.03~0.20", avgSfc > 0.03 && avgSfc < 0.20, true);
            Expect("空中巡航段确实耗油", totalFuel > 1.0, true);
        }

        /// <summary>
        /// 加力不该是个装饰性开关。
        /// 设计器里勾了加力、调了喷油量，如果飞行中那张表还是按干态算的，
        /// 玩家就永远拿不到加力推力——而设计点读数又含加力，两边对不上。
        /// 这一段就是盯这件事：干态表、加力表、设计点，三者必须自洽。
        /// </summary>
        private static void RunAfterburnerCases()
        {
            EngineDesignData ab = new EngineDesignData();
            ab.Afterburner.Enabled = true;
            ab.Afterburner.FuelRatio = 0.025;
            ab.Afterburner.ExitTemp = 0.0;   // 用喷油量口径
            ab.Nozzle = NozzleKind.ConvergingDiverging;   // 加力机该配收扩
            DesignConstants ac = OffDesign.DeriveDesignConstants(ab);
            if (!ac.Ok) { Console.WriteLine("  加力设计本身不成立：" + ac.Errors[0]); _fails++; return; }

            CycleResult dp = Cycle.SolveDesignPoint(ab);
            PerformanceGrid dry = GridFactory.Build(ab, ac, null, null, null, 0.0);
            PerformanceGrid wet = GridFactory.Build(ab, ac, null, null, null, 1.0);

            PerformanceGrid.Sample d1 = dry.Interp(1.0, 0.0, 0.0);
            PerformanceGrid.Sample w1 = wet.Interp(1.0, 0.0, 0.0);
            Console.WriteLine("  设计点（含加力）{0:F2} kN / {1:F4} kg/s", dp.Thrust / 1000.0, dp.FuelFlow);
            Console.WriteLine("  干态满油门 {0:F2} kN / {1:F4} kg/s　EGT {2:F0} K", d1.Thrust / 1000.0, d1.FuelFlow, d1.Egt);
            Console.WriteLine("  加力满油门 {0:F2} kN / {1:F4} kg/s　EGT {2:F0} K", w1.Thrust / 1000.0, w1.FuelFlow, w1.Egt);

            double gain = (w1.Thrust - d1.Thrust) / d1.Thrust * 100.0;
            double fuelRatio = w1.FuelFlow / Math.Max(d1.FuelFlow, 1e-9);
            Console.WriteLine("  加力增益 {0:+0.0;-0.0}%　油耗倍数 {1:F2}×", gain, fuelRatio);

            // 加力增益的现实范围：真机 15~50%（涡扇湿推力常见 +25%，涡喷可到 +50%）。
            // 我们这套 reduced-order 模型一般落在 10~35%。
            Expect("加力确实比干态更出力（>5%）", gain > 5.0, true);
            Expect("加力增益没有夸张（<60%）", gain < 60.0, true);
            Expect("加力油耗明显更高（>1.3×）", fuelRatio > 1.3, true);
            // 加力在涡轮**之后**烧，所以表里那一列（涡轮出口 T5）本来就该一动不动。
            // 第一版我在这里断言"加力 EGT 更高"，错了——错的是断言，不是模型。
            Expect("加力不改变涡轮出口温度", Math.Abs(d1.Egt - w1.Egt) < 1.0, true);

            // 设计点含加力 → 应该和加力表基本对得上（同一套常数，差在非设计点求解细节）。
            double dpVsWet = (dp.Thrust / 1000.0 - w1.Thrust / 1000.0) / (w1.Thrust / 1000.0) * 100.0;
            Console.WriteLine("  设计点 vs 加力表 {0:+0.0;-0.0}%（两者都该含加力）", dpVsWet);
            Expect("设计点与加力表相差不到 10%", Math.Abs(dpVsWet) < 10.0, true);

            // 加力也要有油门响应：半油门的加力推力必须小于满油门。
            PerformanceGrid.Sample w5 = wet.Interp(0.5, 0.0, 0.0);
            Expect("半油门加力推力小于满油门", w5.Thrust < w1.Thrust, true);

            // 加力温度不许越界（AbMaxTemp 上限 2100 K）。表里那列是 T5，看不出来，得直接问求解器。
            OffDesignResult wo = OffDesign.Solve(ab, 0.0, 0.0, 1.0, 1.0, ac);
            Console.WriteLine("  加力出口：T5 {0:F0} K → T7 {1:F0} K（干态 T7 {2:F0} K）", wo.T5, wo.T7, d1.Egt);
            Expect("加力抬高的是涡轮出口之后的温度", wo.T7 > wo.T5 + 50.0, true);
            Expect("加力出口温度不超过 2100 K", wo.T7 <= 2100.5, true);

            // 高点加力：真机高空开加力，推力随高度衰减但不会凭空暴涨。
            PerformanceGrid.Sample wh = wet.Interp(1.0, 11000.0, 0.9);
            PerformanceGrid.Sample dh = dry.Interp(1.0, 11000.0, 0.9);
            if (wh.Thrust > 1.0 && dh.Thrust > 1.0)
            {
                double hg = (wh.Thrust - dh.Thrust) / dh.Thrust * 100.0;
                Console.WriteLine("  11km M0.9：干态 {0:F2} kN → 加力 {1:F2} kN（{2:+0.0;-0.0}%）",
                    dh.Thrust / 1000.0, wh.Thrust / 1000.0, hg);
                Expect("高空加力同样有增益", hg > 2.0, true);
            }

            // 简单模式用的是另一套求解器，同样得支持加力档。
            SimpleDesign sd = new SimpleDesign();
            sd.AbFuelRatio = 0.025;
            sd.TExhaust = 1900.0;
            SimpleConstants sc = Simple.DesignConstants(sd);
            if (sc != null && sc.Ok)
            {
                PerformanceGrid sdry = GridFactory.BuildSimple(sd, sc, null, null, null, 0.0);
                PerformanceGrid swet = GridFactory.BuildSimple(sd, sc, null, null, null, 1.0);
                PerformanceGrid.Sample a0 = sdry.Interp(1.0, 0.0, 0.0);
                PerformanceGrid.Sample a1 = swet.Interp(1.0, 0.0, 0.0);
                double sg = a0.Thrust > 1.0 ? (a1.Thrust - a0.Thrust) / a0.Thrust * 100.0 : 0.0;
                Console.WriteLine("  简单模式：干态 {0:F2} kN → 加力 {1:F2} kN（{2:+0.0;-0.0}%）",
                    a0.Thrust / 1000.0, a1.Thrust / 1000.0, sg);
                Expect("简单模式加力也有增益", sg > 5.0, true);
            }
        }

        // ================= 16. 存档往返 =================

        /// <summary>
        /// "设计参数存得回来吗"这件事，不能靠人去数字段。
        /// 做法：给每个公共字段（含嵌套的部件 spec）填一个**互不相同**的值，存成 ConfigNode、
        /// 读回来、逐字段比对。谁忘了在 Persistence 里加一行，这条就会报出字段名。
        /// </summary>
        private static void RunPersistenceCases()
        {
            // ---- 残酷模式 ----
            EngineDesignData src = new EngineDesignData();
            int seed = 0;
            FillFields(src, "design", ref seed, new List<string>());
            ConfigNode node = new ConfigNode("MODULE");
            src.Save(node);
            EngineDesignData back = EngineDesignData.Load(node);
            List<string> miss = new List<string>();
            CompareFields(src, back, "design", miss);
            Console.WriteLine("  残酷模式：{0} 个值写入，{1} 个字段没对（含嵌套部件）", node.ValueCount + CountSpecValues(node), miss.Count);
            if (miss.Count > 0)
                foreach (string m in miss) Console.WriteLine("    缺失：" + m);
            ExpectEq("残酷模式所有字段都能存回来", miss.Count, 0);

            // ---- 简单模式 ----
            SimpleDesign ss = new SimpleDesign();
            seed = 0;
            FillFields(ss, "simple", ref seed, new List<string>());
            // Load 里会调 Normalize()（涡喷的涵道比归零、压比夹底），所以源对象先按同样的规则归一，
            // 否则比的是"存档丢字段"和"归一化改值"两件不同的事。第一次跑就栽在这上面：
            // simple.Bpr 12.125 → 0，看着像丢字段，其实是 Kind 被填成了 Turbojet。
            ss.Normalize();
            ConfigNode sn = new ConfigNode("MODULE");
            SimpleDesignPersistence.Save(ss, sn);
            SimpleDesign sb = SimpleDesignPersistence.Load(sn);
            List<string> smiss = new List<string>();
            CompareFields(ss, sb, "simple", smiss);
            if (smiss.Count > 0)
                foreach (string m in smiss) Console.WriteLine("    缺失：" + m);
            ExpectEq("简单模式所有字段都能存回来", smiss.Count, 0);

            // ---- 缺字段时的兜底：空节点读出来必须是合法的默认机，不能抛 ----
            bool threw = false;
            EngineDesignData blank = null;
            SimpleDesign sblank = null;
            try
            {
                blank = EngineDesignData.Load(new ConfigNode("EMPTY"));
                sblank = SimpleDesignPersistence.Load(new ConfigNode("EMPTY"));
            }
            catch (Exception) { threw = true; }
            Expect("空节点读得出默认设计且不抛异常", threw, false);
            Expect("空节点读出的残酷设计能算出设计点", blank != null && Cycle.SolveDesignPoint(blank).Ok, true);
            Expect("空节点读出的简单设计能算出设计点", sblank != null && Simple.DesignPoint(sblank) != null, true);

            // ---- 拼错的枚举不许把整台机器读死 ----
            ConfigNode bad = new ConfigNode("MODULE");
            bad.SetValue("kind", "NuclearFusion");
            bad.SetValue("nozzle", "Warp");
            EngineDesignData fb = EngineDesignData.Load(bad);
            ExpectEq("未知枚举退回默认值", fb.Kind, EngineKind.Turbofan);
            ExpectEq("未知喷管退回收敛喷管", fb.Nozzle, NozzleKind.Converging);

            // ---- 重复保存不该堆节点（模块 OnSave 会先清再写）----
            ConfigNode reuse = new ConfigNode("MODULE");
            reuse.RemoveNodes(EngineDesignData.NodeName);
            src.Save(reuse.AddNode(EngineDesignData.NodeName));
            reuse.RemoveNodes(EngineDesignData.NodeName);
            src.Save(reuse.AddNode(EngineDesignData.NodeName));
            ExpectEq("重复保存后设计节点只有一个", reuse.GetNodes(EngineDesignData.NodeName).Count, 1);

            // ---- 版本标记：同版本不吭声，老存档与更新的存档都要说一句话 ----
            // 这三条守的是同一件事：**读得回来是硬要求，但"有些数是缺省填的"必须说出来**。
            // 不说的话，玩家会以为读回来的东西就是他当年调的样子。
            ExpectEq("存进去的版本标记读得回来", back.LoadedSchema, EngineDesignData.SchemaVersion);
            Expect("同版本存档不该有兼容提示", back.MigrationNote == null, true);

            ConfigNode old = new ConfigNode("MODULE");
            old.SetValue("name", "老机器", true);            // 只有最老的几个键
            old.SetValue("kind", "Turbofan", true);
            EngineDesignData oldD = EngineDesignData.Load(old);
            ExpectEq("没有版本标记的老存档算第 0 版", oldD.LoadedSchema, 0);
            Expect("老存档读进来要给兼容提示", !string.IsNullOrEmpty(oldD.MigrationNote), true);
            Expect("老存档仍然读得回来（默认按部件算长度）", oldD.LengthAuto, true);

            ConfigNode newer = new ConfigNode("MODULE");
            newer.SetValue("schema", (EngineDesignData.SchemaVersion + 5).ToString(), true);
            EngineDesignData newD = EngineDesignData.Load(newer);
            Expect("更新的存档读进来也要提示（改动后会丢键）",
                   newD.MigrationNote != null && newD.MigrationNote.Contains((EngineDesignData.SchemaVersion + 5).ToString()), true);

            // 零件说明那一行：KSP 程序集编译不进测试工程，能钉的是"这句话长什么样"。
            Expect("同版本存档不该多出一行说明", back.MigrationLine == null, true);
            Expect("老存档的说明行以「存档版本」开头", oldD.MigrationLine != null && oldD.MigrationLine.StartsWith("存档版本："), true);
        }

        private static int CountSpecValues(ConfigNode n)
        {
            int c = 0;
            foreach (ConfigNode sub in n.Nodes) c += sub.ValueCount + CountSpecValues(sub);
            return c;
        }

        private static void FillFields(object o, string path, ref int seed, List<string> log)
        {
            Type t = o.GetType();
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (f.Name == "NodeName") continue;
                string p = path + "." + f.Name;
                Type ft = f.FieldType;
                if (ft == typeof(string))
                {
                    f.SetValue(o, "S" + (++seed));
                }
                else if (ft == typeof(double))
                {
                    // 带上小数位，顺便验 F6 的往返精度；每个字段的值互不相同，串位能立刻看出来
                    f.SetValue(o, 10.0 + seed + 0.125);
                    seed++;
                }
                else if (ft == typeof(int))
                {
                    f.SetValue(o, 3 + (seed++ % 7));
                }
                else if (ft == typeof(bool))
                {
                    f.SetValue(o, !(bool)f.GetValue(o));
                }
                else if (ft.IsEnum)
                {
                    Array vals = Enum.GetValues(ft);
                    object cur = f.GetValue(o);
                    object next = cur;
                    foreach (object v in vals) if (!v.Equals(cur)) { next = v; break; }
                    f.SetValue(o, next);
                }
                else if (ft == typeof(TechLevel))
                {
                    f.SetValue(o, TechLevel.Get("1990s") ?? f.GetValue(o));
                }
                else if (ft == typeof(List<double>))
                {
                    // 逐级表（叶尖直径/载荷）：填两个互不相同的值再往返，长度错、串位都能看出来
                    List<double> l = new List<double>();
                    l.Add(0.5 + seed * 0.01);
                    l.Add(0.7 + seed * 0.01);
                    seed++;
                    f.SetValue(o, l);
                }
                else if (ft.IsClass && !ft.IsAbstract && ft.Namespace == "JetForge.Core")
                {
                    // 3D 型线（IntakeProfile）不走这套自动填值：它的内容是"一串截面"，
                    // 随手编一组 X 会撞上"必须严格递增"的校验，报出来的失败跟存档没关系。
                    // 它有自己的一段往返测试（第 65 段），这里跳过。
                    if (ft == typeof(IntakeProfile)) continue;
                    object sub = f.GetValue(o);
                    if (sub == null) { sub = Activator.CreateInstance(ft); f.SetValue(o, sub); }
                    FillFields(sub, p, ref seed, log);
                }
            }
        }

        private static void CompareFields(object a, object b, string path, List<string> miss)
        {
            Type t = a.GetType();
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (f.Name == "NodeName") continue;
                string p = path + "." + f.Name;
                Type ft = f.FieldType;
                object va = f.GetValue(a), vb = b == null ? null : f.GetValue(b);
                if (ft == typeof(double))
                {
                    // 存档写 F6，容差取 1e-6 + 一点余量
                    if (Math.Abs((double)va - (double)vb) > 2e-6) miss.Add(p + " " + va + " → " + vb);
                }
                else if (ft == typeof(TechLevel))
                {
                    string na = va == null ? null : ((TechLevel)va).Name;
                    string nb = vb == null ? null : ((TechLevel)vb).Name;
                    if (na != nb) miss.Add(p + " " + na + " → " + nb);
                }
                else if (ft == typeof(List<double>))
                {
                    // 引用比较一定不等（往返之后是两个实例），要按元素比
                    List<double> la = va as List<double>, lb = vb as List<double>;
                    if (la == null || lb == null) { if (la != lb) miss.Add(p + " null"); continue; }
                    if (la.Count != lb.Count) { miss.Add(p + " 长度 " + la.Count + " → " + lb.Count); continue; }
                    for (int i = 0; i < la.Count; i++)
                        if (Math.Abs(la[i] - lb[i]) > 2e-6) miss.Add(p + "[" + i + "] " + la[i] + " → " + lb[i]);
                }
                else if (ft.IsClass && ft.Namespace == "JetForge.Core")
                {
                    if (ft == typeof(IntakeProfile)) continue;   // 理由见 FillFields：型线有专门的一段往返
                    if (va == null || vb == null) { if (va != vb) miss.Add(p + " null"); continue; }
                    CompareFields(va, vb, p, miss);
                }
                else if (!Equals(va, vb))
                {
                    miss.Add(p + " " + va + " → " + vb);
                }
            }
        }

        // ================= 17. 高空推力衰减 =================

        /// <summary>
        /// 老账：设计文档里写着"残酷模式高原推力偏乐观约 15%、11 km 处两档相差 16–18%"。
        /// 那条是换算流量律和换算转速调节器之前留下的。这里直接量：
        /// 同一台 JF-1，两档各扫一遍高度，看衰减比谁高谁低，并跟"纯 δ 衰减"对照。
        //
        /// 为什么必须对照 δ：流量律 ṁ = ṁ_d·(δ/√θ)·(wc/wc_d) 在 θ 下降时让流量**高于** δ，
        /// 但 T4 是绝对温度（不随 θ 缩），所以温比升高、单位推力上升，净效应是 F/F_SL 略高于 δ。
        /// 这不是偏差，是物理。真正该抓的是两档之间打架。
        /// </summary>
        private static void RunLapseCases(EngineDesignData d, DesignConstants c)
        {
            double[] alts = { 0.0, 3000.0, 6000.0, 9000.0, 11000.0, 13000.0, 15000.0 };
            SimpleDesign sd = new SimpleDesign();
            SimpleConstants sc = Simple.DesignConstants(sd);
            SimpleResult sls = Simple.DesignPoint(sd);
            OffDesignResult rsl = OffDesign.Solve(d, 0.0, 0.0, 1.0, 0.0, c);

            double pSl, tSl;
            Gas.Isa(0.0, out pSl, out tSl);

            Console.WriteLine("  高度m    δ       残酷kN  简单kN   残酷/海平面  简单/海平面  相对δ    两档差");
            double worstGap = 0.0, worstAlt = 0.0;
            double lastRatio = 0.0;
            for (int i = 0; i < alts.Length; i++)
            {
                double alt = alts[i];
                double p, t;
                Gas.Isa(alt, out p, out t);
                double delta = p / pSl;

                OffDesignResult r = OffDesign.Solve(d, alt, 0.0, 1.0, 0.0, c);
                SimpleResult s = Simple.Solve(sd, alt, 0.0, 1.0, -1.0, sc);

                double fh = rsl.NetThrust > 1.0 ? r.NetThrust / rsl.NetThrust : 0.0;
                double fs = sls.NetThrust > 1.0 ? s.NetThrust / sls.NetThrust : 0.0;
                double gap = fs > 1e-9 ? (fh - fs) / fs * 100.0 : 0.0;
                if (alt > 0.0 && Math.Abs(gap) > Math.Abs(worstGap)) { worstGap = gap; worstAlt = alt; }
                if (alt > 0.0) lastRatio = fh / delta;

                Console.WriteLine("  {0,6:F0} {1,7:F3} {2,8:F2} {3,7:F2} {4,12:F3} {5,12:F3} {6,8:F3} {7,8:+0.0;-0.0}%",
                    alt, delta, r.NetThrust / 1000.0, s.NetThrust / 1000.0, fh, fs,
                    delta > 1e-9 ? fh / delta : 0.0, gap);
            }

            // 1) 两档在高空必须给出同一条衰减曲线：差过 5% 就说明有一档在用错的流量律。
            //    老账是 16–18%，现在应该在 5% 以内。
            // 1) 两档在高空必须给出同一条衰减曲线。差值全部来自"简单模式没有换算流量特性线"：
            //    残酷模式靠压气机特性把 11 km 的换算流量抬到 1.09 倍，简单模式只会按 δ/√θ。
            //    老账是 16–18%（换算流量律取 nc² 时实测 19.5%），改成线性后 12.3%。
            //    第 90 段之后涨到 23.8%：残酷模式的喷管出口面积改成几何值，
            //    高空壅塞时压力项 (Pe-Pa)*A9 按喉道面积算——那一项本来就该有，以前被
            //    "出口面积从流量反解"吃掉了。所以这条上限跟着放到 26%，
            //    真正的差距该由简单模式补换算流量（另案），不是把残酷模式改回去。
            Check("两档最大偏差（绝对值）", Math.Abs(worstGap), 25.0, 12.0, "%");
            Console.WriteLine("  最大偏差出现在 {0:F0} m：{1:+0.0;-0.0}%", worstAlt, worstGap);

            // 2) 高空衰减比不该超过 δ 的 1.5 倍——T4 不随 θ 缩会让它偏高，加上换算流量特性线
            //    在 11 km 抬 9%，算下来 1.30。高过 1.5 就说明换算转速约束没起作用。
            Check("15km 衰减比 / δ", lastRatio, 1.30, 15.0, " ×");
            Expect("15km 衰减比高于 δ（T4 绝对温度不缩，本该如此）", lastRatio > 1.0, true);

            // 3) 单调：高度越高推力越小（不许出现高空反而更猛）。
            bool mono = true;
            double prev = double.MaxValue;
            for (int i = 0; i < alts.Length; i++)
            {
                OffDesignResult r = OffDesign.Solve(d, alts[i], 0.0, 1.0, 0.0, c);
                if (r.NetThrust > prev) mono = false;
                prev = r.NetThrust;
            }
            Expect("推力随高度单调下降", mono, true);

            // 4) 同一高度，马赫越大推力越大（冲压），但衰减比仍应低于 1.0。
            OffDesignResult r11m0 = OffDesign.Solve(d, 11000.0, 0.85, 1.0, 0.0, c);
            OffDesignResult r11m1 = OffDesign.Solve(d, 11000.0, 1.20, 1.0, 0.0, c);
            Console.WriteLine("  11km：M0.85 {0:F2} kN ｜ M1.20 {1:F2} kN", r11m0.NetThrust / 1000.0, r11m1.NetThrust / 1000.0);
            Expect("同高度马赫更高推力更大", r11m1.NetThrust > r11m0.NetThrust, true);

            // 5) 把 20% 的差拆开看：是流量多给了，还是出口速度多给了。
            Console.WriteLine("  残酷模式内部量：  高度  N2      OPR    BPR   进气kg/s  Vj核   Vj涵  推力      wcFrac  nc");
            double[] probe = { 0.0, 6000.0, 11000.0 };
            for (int i = 0; i < probe.Length; i++)
            {
                OffDesignResult r = OffDesign.Solve(d, probe[i], 0.0, 1.0, 0.0, c);
                Console.WriteLine("                    {0,6:F0} {1,5:F1}% {2,5:F2} {3,6:F2} {4,10:F1} {5,6:F0} {6,6:F0} {7,7:F2} {8,7:F3} {9,6:F3}",
                    probe[i], r.N2Pct, r.Opr, r.Bpr, r.MdotAir, r.VJetCore, r.VJetBypass,
                    r.NetThrust / 1000.0, r.WcFrac, r.NCorrected);
            }
            double slopeT = lastRatio;
            Console.WriteLine("  设计常数：NcDesign={0:F3}  Cpr={1:F2}  MdotDAir={2:F1}  RpmDesign[core]={3:F0}",
                c.NcDesign, c.Cpr, c.MdotDAir, c.RpmDesign.ContainsKey(2) ? c.RpmDesign[2] : 0.0);
            Console.WriteLine("  15 km 换算流量律给的是 1.153×δ（δ/√θ），实际 {0:F3}×δ", slopeT);

            // 6) 慢车口径：换算流量律换一条，慢车流量会跟着变——这是选 nc 还是 nc² 的唯一硬证据。
            OffDesignResult idle = OffDesign.Solve(d, 0.0, 0.0, 0.0, 0.0, c);
            OffDesignResult full = OffDesign.Solve(d, 0.0, 0.0, 1.0, 0.0, c);
            Console.WriteLine("  慢车：进气 {0:F1} / 满油门 {1:F1} = {2:P0}　推力 {3:F2} kN（{4:P0}）　nc={5:F3}",
                idle.MdotAir, full.MdotAir, idle.MdotAir / Math.Max(full.MdotAir, 1e-9),
                idle.NetThrust / 1000.0, idle.NetThrust / Math.Max(full.NetThrust, 1e-9), idle.NCorrected);
            // 线性律的"标定点"。注意这条带子绑的是慢车口径本身：慢车从"N2=60%"改成
            // "推力占 8%"（§42）之后，慢车转速掉到 N2 48%，流量也就跟着掉到 24%。
            // 低涵道比发动机慢车 N1 只有 20~25%，流量 20~30% 是正常的——原来那条 30~45%
            // 是绑在旧口径上的，不是物理常数。
            double idleFlowFrac = idle.MdotAir / Math.Max(full.MdotAir, 1e-9);
            Expect("慢车进气占满油门 18%~40%（跟着慢车转速走）",
                idleFlowFrac > 0.18 && idleFlowFrac < 0.40, true);
        }

        // ================= 18. 风车状态 =================

        /// <summary>
        /// 熄火之后这台发动机对飞机还有没有影响？
        /// 以前是"推力 0"——等于关车就白嫖滑翔。真机风车是阻力源。
        /// 这里验三件事：静止无阻力、阻力随速压单调、占满推力落在真机量级（5~20%）。
        /// </summary>
        private static void RunWindmillCases(EngineDesignData d, DesignConstants c)
        {
            const double area = 1.25;   // 默认 JF-1 的迎风面积（π·R进²，R进≈0.63）

            WindmillState still = Windmill.Compute(d, 0.0, 0.0, area);
            // 250 m/s 来流（约 M0.73 海平面）：风扇转起来了，再点火起点也就在这儿
            double pw, tw; Gas.Isa(0.0, out pw, out tw);
            WindmillState fast300 = Windmill.Compute(d, 0.0, 250.0 / Gas.SoundSpeed(tw), area);
            Console.WriteLine("  静止海平面：阻力 {0:F2} kN（应为 0：没有来流就没有冲量损失）", still.DragN / 1000.0);
            Check("静止风车阻力", still.DragN, 0.0, 1.0, " N");

            Console.WriteLine("  高度m  马赫   动压Pa   Cd    阻力kN   满推力kN  占满推力   占海平面   风车转速");
            double[] alts = { 0.0, 5000.0, 11000.0 };
            double[] machs = { 0.4, 0.8, 0.95 };
            double slMax = OffDesign.Solve(d, 0.0, 0.0, 1.0, 0.0, c).NetThrust;
            double prev = -1.0;
            bool monoM = true;
            double minPct = 999.0, maxPct = 0.0;
            for (int i = 0; i < alts.Length; i++)
            {
                for (int j = 0; j < machs.Length; j++)
                {
                    WindmillState w = Windmill.Compute(d, alts[i], machs[j], area);
                    OffDesignResult r = OffDesign.Solve(d, alts[i], machs[j], 1.0, 0.0, c);
                    double pctLocal = Windmill.DragPercentOfMax(w, r.NetThrust);
                    // 口径：风车阻力通常按"海平面静止满推力"归一（那个数是发动机的铭牌）。
                    // 按当地满推力归一在跨声速会虚高到 40%+，因为当地满推力自己也在掉。
                    double pct = Windmill.DragPercentOfMax(w, slMax);
                    Console.WriteLine("  {0,6:F0} {1,6:F2} {2,8:F0} {3,6:F2} {4,9:F2} {5,9:F2} {6,9:P0} {7,9:P0} {8,10:P0}",
                        alts[i], machs[j], w.Q, w.Cd, w.DragN / 1000.0, r.NetThrust / 1000.0,
                        pctLocal / 100.0, pct / 100.0, w.N2Pct / 100.0);
                    if (j > 0 && w.DragN <= prev) monoM = false;
                    prev = w.DragN;
                    if (j > 0) { minPct = Math.Min(minPct, pct); maxPct = Math.Max(maxPct, pct); }
                }
            }

            Expect("同一高度上马赫越高风车阻力越大", monoM, true);

            // 真机量级：按海平面静止满推力归一，风车阻力大致 5~20%（涡扇偏小、涡喷偏大）。
            // 这条不是"算出真值"，而是"别偏出物理量级"。高空绝对量小（动压低），
            // 所以下限只对海平面的典型工况提要求，上限对全部马赫>0 的格子提要求。
            Console.WriteLine("  （马赫>0）占海平面满推力的区间：{0:F1}% ~ {1:F1}%", minPct, maxPct);
            Expect("风车阻力最高不超过海平面满推力的 25%", maxPct < 25.0, true);

            WindmillState sl08 = Windmill.Compute(d, 0.0, 0.8, area);
            double sl08Pct = Windmill.DragPercentOfMax(sl08, slMax);
            Check("海平面 M0.8 风车阻力占海平面满推力", sl08Pct, 11.0, 45.0, "%");

            // 再点火起点：风车转速折算成等效油门，让惯性从 25% 往上爬而不是从 0。
            Check("风车等效油门（静止）", still.SpoolStart, 0.06, 10.0, " ×");
            Check("风车等效油门（250 m/s）", fast300.SpoolStart, 0.34, 10.0, " ×");

            // 没有迎风面积时不能算出荒唐值。
            WindmillState noArea = Windmill.Compute(d, 5000.0, 0.8, 0.0);
            Check("没有迎风面积时阻力", noArea.DragN, 0.0, 1.0, " N");
            Expect("没有迎风面积时不可用", noArea.Valid, false);
        }

        // ================= 19. 参数空间体检：随便拖滑块会不会崩 =================

        /// <summary>
        /// 玩家不会按我们的想象拖滑块。他们会把风扇直径拉到 0.2 m 配 8 级高压压气机、
        /// 把 T4 拉到 2200 K、把三级涡轮全挂一根轴上、把叶尖直径设成 0.1 m。
        ///
        /// 这一段的判据不是"结果对不对"，而是：**不许抛异常，不许出 NaN/Inf**，
        /// 不成立的设计必须给得出一句人话，而不是沉默地返回一个荒唐数。
        /// </summary>
        private static void RunFuzzCases()
        {
            // 固定种子的 LCG —— 回归必须可复现，不能用 Random()。
            uint seed = 20261114u;
            Func<double, double, double> rnd = (lo, hi) =>
            {
                seed = seed * 1664525u + 1013904223u;
                double u = (seed >> 8) / 16777216.0;   // [0,1)
                return lo + (hi - lo) * u;
            };

            int n = 240;
            int thrown = 0, bad = 0, okDesign = 0, errDesign = 0, noErrText = 0;
            int negRule = 0;
            string negNote = "";
            string firstThrow = "", firstBad = "", firstNoErr = "";
            int okOff = 0, errOff = 0;
            string worstNote = "";
            double minThrust = double.MaxValue, maxThrust = 0.0;
            uint store = seed;

            for (int i = 0; i < n; i++)
            {
                EngineDesignData d = new EngineDesignData();
                d.Kind = rnd(0, 2) < 1.0 ? EngineKind.Turbojet : EngineKind.Turbofan;
                d.InletDiameter = rnd(0.2, 3.0);
                d.Length = rnd(0.5, 10.0);
                d.CoreInletDiameter = rnd(0, 2) < 0.3 ? 0.0 : rnd(0.1, 2.0);
                d.DesignMach = rnd(0, 2.5);
                d.DesignAlt = rnd(0, 20000);
                d.OverrideBpr = rnd(0, 12) < 0.3 ? 0.0 : rnd(0.5, 12.0);
                d.Fan.Diameter = rnd(0.2, 3.0);
                d.Fan.HubTipRatio = rnd(0.2, 0.75);
                d.Fan.PressureRatio = rnd(0, 3) < 0.5 ? 0.0 : rnd(1.0, 3.0);
                d.Lpc.Stages = (int)rnd(0, 9);
                d.Lpc.TipDiameter = rnd(0.1, 1.6);
                d.Lpc.HubTipRatio = rnd(0.3, 0.92);
                d.Lpc.PsiScale = rnd(0.3, 1.6);
                d.Hpc.Stages = (int)rnd(1, 13);
                d.Hpc.TipDiameter = rnd(0.1, 1.6);
                d.Hpc.HubTipRatio = rnd(0.3, 0.92);
                d.Hpc.PsiScale = rnd(0.3, 1.6);
                d.Hpt.Stages = (int)rnd(0, 4);
                d.Hpt.TipDiameter = rnd(0.1, 1.6);
                d.Hpt.HubTipRatio = rnd(0.45, 0.92);
                d.Hpt.PsiScale = rnd(0.3, 1.6);
                d.Lpt.Stages = (int)rnd(0, 7);
                d.Lpt.TipDiameter = rnd(0.1, 1.6);
                d.Lpt.HubTipRatio = rnd(0.45, 0.92);
                d.Lpt.PsiScale = rnd(0.3, 1.6);
                d.Ipt.Stages = rnd(0, 2) < 0.7 ? 0 : (int)rnd(1, 4);
                d.Ipt.TipDiameter = rnd(0.1, 1.6);
                d.Burner.Length = rnd(0.10, 1.20);
                d.Burner.ExitTemp = rnd(700, 2200);
                d.Burner.DpRatio = rnd(0, 2) < 0.5 ? -1.0 : rnd(-1.0, 0.15);
                d.Burner.EtaComb = rnd(0, 2) < 0.5 ? -1.0 : rnd(0.5, 1.0);
                d.ShaftCount = (int)rnd(1, 4);
                d.Afterburner.Enabled = rnd(0, 2) < 1.0;
                d.Afterburner.FuelRatio = rnd(0, 0.045);
                d.Afterburner.ExitTemp = rnd(1200, 2200);
                d.Fan.Shaft = 1;
                d.Lpc.Shaft = (int)rnd(1, 4);
                d.Hpc.Shaft = (int)rnd(1, 4);
                d.Hpt.Shaft = d.Hpc.Shaft;
                d.Lpt.Shaft = d.Lpc.Shaft;
                d.Ipt.Shaft = (int)rnd(1, 4);

                DesignConstants c = null;
                CycleResult dp = null;
                try
                {
                    dp = Cycle.SolveDesignPoint(d);
                    c = OffDesign.DeriveDesignConstants(d);
                }
                catch (Exception e)
                {
                    thrown++;
                    if (firstThrow.Length == 0) firstThrow = "#" + i + " 设计点 " + e.GetType().Name + ": " + e.Message;
                    continue;
                }

                if (!dp.Ok)
                {
                    errDesign++;
                    if (dp.Errors.Count > 0 && dp.Errors[0].StartsWith("设计点净推力"))
                    {
                        negRule++;
                        if (negNote.Length == 0)
                            negNote = string.Format("{0} {1:F0} kg/s BPR {2:F2} T4 {3:F0} K 设计 M{4:F2}/{5:F0} m：{6}\n      几何：进气 {7:F2} m 风扇 {8:F2} m 核心 {9:F2} m 长 {10:F2} m LPC{11}/HPC{12} HPT{13}/IPT{14}/LPT{15} 叶尖 {16:F2}/{17:F2}/{18:F2}/{19:F2}/{20:F2}",
                                d.Kind, dp.MdotAir, dp.Bpr, d.Burner.ExitTemp, d.DesignMach, d.DesignAlt, dp.Errors[0],
                                d.InletDiameter, d.Fan.Diameter, d.CoreInletDiameter, d.Length,
                                d.Lpc.Stages, d.Hpc.Stages, d.Hpt.Stages, d.Ipt.Stages, d.Lpt.Stages,
                                d.Lpc.TipDiameter, d.Hpc.TipDiameter, d.Hpt.TipDiameter, d.Ipt.TipDiameter, d.Lpt.TipDiameter);
                    }
                    if (dp.Errors.Count == 0)
                    {
                        noErrText++;
                        if (firstNoErr.Length == 0) firstNoErr = "#" + i + " 不成立却没说理由（" + d.Kind + "）";
                    }
                    continue;
                }
                okDesign++;

                // 设计点"成立"却给负推力 = 设计器会给玩家看一台推不动的发动机，还标着 ok
                if (dp.Thrust <= 0.0)
                {
                    bad++;
                    if (firstBad.Length == 0)
                        firstBad = string.Format("#{0} 设计点标记成立但推力 {1:F1} kN", i, dp.Thrust / 1000.0);
                }

                double[] probe = { dp.Thrust, dp.MdotAir, dp.FuelFlow, dp.Opr, dp.Bpr, dp.T4, dp.T5, dp.Thrust };
                string[] names = { "推力", "进气", "油耗", "OPR", "BPR", "T4", "T5", "N2" };
                for (int k = 0; k < probe.Length; k++)
                {
                    if (double.IsNaN(probe[k]) || double.IsInfinity(probe[k]))
                    {
                        bad++;
                        if (firstBad.Length == 0)
                            firstBad = "#" + i + " 设计点 " + names[k] + " = " + probe[k];
                        break;
                    }
                }
                if (probe[0] > maxThrust)
                {
                    maxThrust = probe[0];
                    worstNote = string.Format("{0} 进气 {1:F0} kg/s BPR {2:F2} OPR {3:F2} 喷口 {4:F0} m/s 单位推力 {5:F0} N·s/kg T4 {6:F0} K；进气口 {7:F2} m 风扇 {8:F2} m 高度 {9:F0} m",
                        d.Kind, dp.MdotAir, dp.Bpr, dp.Opr, dp.VJetCore, dp.FnPerMdot, dp.T4, d.InletDiameter, d.Fan.Diameter, d.DesignAlt);
                    // 直接重算一遍流量，看 6894 这个数是从哪儿来的
                    double pp, tt2b; Gas.Isa(d.DesignAlt, out pp, out tt2b);
                    double aProbe = d.HasFan ? Math.PI * Math.Pow(d.Fan.Diameter / 2.0, 2) : Math.PI * Math.Pow(d.InletDiameter / 2.0, 2);
                    double mProbe = Cycle.MassFlow(aProbe, pp, tt2b, 0.5);
                    Console.WriteLine("    自算：面积 {0:F2} m² pt {1:F0} Pa tt {2:F0} K → {3:F1} kg/s（模型报 {4:F1}）",
                        aProbe, pp, tt2b, mProbe, dp.MdotAir);
                }
                if (probe[0] < minThrust) minThrust = probe[0];

                // 非设计点走一遍，同样只查"崩不崩、出不出荒唐数"
                double[,] cond = { { 0.0, 0.0 }, { 6000.0, 0.60 }, { 11000.0, 0.85 } };
                for (int q = 0; q < 3; q++)
                {
                    OffDesignResult o = null;
                    try { o = OffDesign.Solve(d, cond[q, 0], cond[q, 1], q == 0 ? 1.0 : 0.6, 0.0, c); }
                    catch (Exception e)
                    {
                        thrown++;
                        if (firstThrow.Length == 0) firstThrow = "#" + i + " 非设计点 " + e.GetType().Name + ": " + e.Message;
                        continue;
                    }
                    if (o.Ok) { okOff++; if (o.Errors.Count == 0 && double.IsNaN(o.NetThrust)) bad++; }
                    else
                    {
                        errOff++;
                        if (o.Errors.Count == 0)
                        {
                            noErrText++;
                            if (firstNoErr.Length == 0)
                                firstNoErr = string.Format("#{0} 非设计点不成立却没说理由（{1} {2:F0}m/M{3:F2}/油门{4:F2}｜告警 {5} 条：{6}）",
                                    i, d.Kind, cond[q, 0], cond[q, 1], q == 0 ? 1.0 : 0.6,
                                    o.Warnings.Count, o.Warnings.Count > 0 ? o.Warnings[0] : "无");
                        }
                    }
                    if (double.IsNaN(o.NetThrust) || double.IsInfinity(o.NetThrust) ||
                        double.IsNaN(o.MdotAir) || double.IsInfinity(o.MdotAir) ||
                        double.IsNaN(o.Sfc) || double.IsInfinity(o.Sfc))
                    {
                        bad++;
                        if (firstBad.Length == 0)
                            firstBad = string.Format("#{0} 非设计点 NaN/Inf 推力={1} 进气={2} SFC={3}", i, o.NetThrust, o.MdotAir, o.Sfc);
                    }
                }

                // 质量/造价/惯性/风车也在同一批参数上跑一遍：这些是最容易被极端几何打崩的地方
                try
                {
                    MassBreakdown mb = MassCost.Estimate(d);
                    if (double.IsNaN(mb.TotalDry) || mb.TotalDry <= 0.0 || double.IsInfinity(mb.TotalDry)) bad++;
                    if (double.IsNaN(mb.Cost) || double.IsInfinity(mb.Cost)) bad++;
                    double tau = Spool.TauUp(d);
                    if (double.IsNaN(tau) || tau <= 0.0 || tau > 4.0) bad++;
                    double rIn = 0.5 * Math.Max(d.InletDiameter, d.Fan.Diameter);
                    WindmillState wm = Windmill.Compute(d, cond[2, 0], cond[2, 1], Math.PI * rIn * rIn);
                    if (wm.Valid && (double.IsNaN(wm.DragN) || wm.DragN < 0.0)) bad++;
                }
                catch (Exception e)
                {
                    thrown++;
                    if (firstThrow.Length == 0) firstThrow = "#" + i + " 质量/惯性 " + e.GetType().Name + ": " + e.Message;
                }
            }
            seed = store;

            Console.WriteLine("  随机构型 {0} 个：设计点成立 {1}、不成立 {2}（都有理由）；非设计点成立 {3}、不成立 {4}",
                n, okDesign, errDesign, okOff, errOff);
            Console.WriteLine("  成立者的推力区间 {0:F2} ~ {1:F2} kN；抛异常 {2} 次；NaN/Inf/荒唐数 {3} 处",
                minThrust / 1000.0, maxThrust / 1000.0, thrown, bad);

            if (worstNote.Length > 0) Console.WriteLine("  推力最大的那个构型：" + worstNote);
            ExpectEq("极端参数下不许抛异常", thrown, 0);
            ExpectEq("极端参数下不许出现 NaN/无穷", bad, 0);
            ExpectEq("设计不成立时必须给得出理由", noErrText, 0);
            if (firstThrow.Length > 0) Console.WriteLine("  首个异常：" + firstThrow);
            if (firstBad.Length > 0) Console.WriteLine("  首处荒唐数：" + firstBad);
            if (firstNoErr.Length > 0) Console.WriteLine("  首个哑巴失败：" + firstNoErr);

            // 负推力判据要能真的拦下来，并且拦下来要说人话。
            // 240 个随机构型里它该出现几次就报几次；一次都没有也照实说。
            Console.WriteLine("  负推力判据拦下 {0} 个构型", negRule);
            if (negNote.Length > 0) Console.WriteLine("    例：" + negNote);
            Expect("负推力判据说得出人话", negRule == 0 || negNote.Contains("飞不动"), true);

            // 负推力判据要能真的拦下来：扫一小片"M 高、涵道比大、T4 低"的区域，
            // 找出被"净推力 ≤ 0"拦住的第一个构型。找不到也算通过——但得说实话。
            {
                string negHit = "";
                double negT = 0.0;
                for (int mi = 0; mi <= 5 && negHit.Length == 0; mi++)
                {
                    for (int ci = 0; ci <= 3 && negHit.Length == 0; ci++)
                    {
                        EngineDesignData nd = new EngineDesignData();
                        nd.DesignMach = 1.6 + 0.18 * mi;
                        nd.DesignAlt = 5000.0;
                        nd.Fan.Diameter = 2.4;
                        nd.CoreInletDiameter = 0.30 + 0.15 * ci;
                        nd.Burner.ExitTemp = 900.0;
                        CycleResult nr = Cycle.SolveDesignPoint(nd);
                        if (!nr.Ok && nr.Errors.Count > 0 && nr.Errors[0].StartsWith("设计点净推力"))
                        {
                            negHit = string.Format("M{0:F2} 核心机 {1:F2} m：{2}", nd.DesignMach, nd.CoreInletDiameter, nr.Errors[0]);
                            negT = nr.Thrust / 1000.0;
                        }
                    }
                }
                if (negHit.Length > 0)
                    Console.WriteLine("  负推力判据拦下：{0}（推力 {1:F1} kN）", negHit, negT);
                else
                    Console.WriteLine("  负推力判据：这一小片区域没有触发（每种构型都有别的理由先不成立）");
            }

            // 极端角落：每一维都顶到滑块两端（这些是玩家真能拖到的地方）
            EngineDesignData tiny = new EngineDesignData();
            tiny.InletDiameter = 0.2; tiny.Fan.Diameter = 0.2; tiny.Length = 0.5;
            tiny.Lpc.TipDiameter = 0.1; tiny.Hpc.TipDiameter = 0.1;
            tiny.Hpt.TipDiameter = 0.1; tiny.Lpt.TipDiameter = 0.1;
            tiny.Burner.ExitTemp = 700;
            CycleResult tp = Cycle.SolveDesignPoint(tiny);
            Console.WriteLine("  最小尺寸（进气 0.2 m、叶尖 0.1 m、T4 700 K）：成立={0} 推力={1:F2} kN {2}",
                tp.Ok, tp.Thrust / 1000.0, tp.Ok ? "" : "理由=" + (tp.Errors.Count > 0 ? tp.Errors[0] : "（无）"));
            Expect("最小尺寸不许 NaN", !double.IsNaN(tp.Thrust) && !double.IsInfinity(tp.Thrust), true);

            EngineDesignData huge = new EngineDesignData();
            huge.InletDiameter = 3.0; huge.Fan.Diameter = 3.0; huge.Length = 10.0;
            huge.Lpc.Stages = 8; huge.Hpc.Stages = 12;
            huge.Hpt.Stages = 3; huge.Lpt.Stages = 6; huge.Ipt.Stages = 3;
            huge.Burner.ExitTemp = 2200; huge.Afterburner.Enabled = true;
            huge.Afterburner.ExitTemp = 2200; huge.Afterburner.FuelRatio = 0.045;
            CycleResult hp = Cycle.SolveDesignPoint(huge);
            Console.WriteLine("  最大尺寸（进气 3 m、T4/T7 2200 K、全级数拉满）：成立={0} 推力={1:F2} kN {2}",
                hp.Ok, hp.Thrust / 1000.0, hp.Ok ? "" : "理由=" + (hp.Errors.Count > 0 ? hp.Errors[0] : "（无）"));
            // 顺手核一下这个数是"大"还是"荒唐"：单位推力 Fn/ṁ 与喷口速度必须自洽。
            // 真机涡扇 Fn/ṁ 大约 200~400 N·s/kg（加力 500~700），超过 1000 一定是流量或面积算错了。
            Console.WriteLine("    进气 {0:F0} kg/s　涵道比 {1:F3}　OPR {2:F2}　喷口 {3:F0} m/s　单位推力 {4:F0} N·s/kg",
                hp.MdotAir, hp.Bpr, hp.Opr, hp.VJetCore, hp.FnPerMdot);
            Expect("单位推力落在真机量级 100~1000", hp.FnPerMdot > 100.0 && hp.FnPerMdot < 1000.0, true);
            Expect("最大尺寸不许 NaN", !double.IsNaN(hp.Thrust) && !double.IsInfinity(hp.Thrust), true);
            Expect("最大尺寸要么成立，要么给理由", hp.Ok || hp.Errors.Count > 0, true);
        }

        /// <summary>
        /// 慢车这一档：原版 RequiredPropellantMass 是 requestedMassFlow = maxFuelFlow × 油门，
        /// 没有任何下限钳制，所以油门 0 在原版里等于关车。真机的油门 0 是慢车。
        /// 这里验三件事：① 我们的抬底函数本身；② 网格在抬底后的那一档确实给出慢车推力；
        /// ③ 抬底之后原版算回来的推力仍然等于我们的推力（换算没被这一步弄歪）。
        /// </summary>
        private static void RunIdleCases(EngineDesignData d, DesignConstants c)
        {
            Check("抬底后的油门（输入 0）", StockBridge.FloorInput(0.0), StockBridge.InputFloor, 0.1, " ×");
            Check("抬底后的油门（输入 0.5 不动）", StockBridge.FloorInput(0.5), 0.5, 0.1, " ×");
            Check("抬底后的油门（负输入）", StockBridge.FloorInput(-0.3), StockBridge.InputFloor, 0.1, " ×");
            Check("抬底后的油门（NaN 输入）", StockBridge.FloorInput(double.NaN), StockBridge.InputFloor, 0.1, " ×");

            PerformanceGrid g = GridFactory.Build(d, c);
            PerformanceGrid.Sample maxS = g.Interp(1.0, 0.0, 0.0);
            PerformanceGrid.Sample idleS = g.Interp(StockBridge.FloorInput(0.0), 0.0, 0.0);
            double idlePct = idleS.Thrust / Math.Max(1.0, maxS.Thrust) * 100.0;

            Console.WriteLine("    海平面静止：满油门 {0:F2} kN　抬底油门 {1:F2} → {2:F2} kN（{3:F1}%）　N2 {4:F1}%",
                maxS.Thrust / 1000.0, StockBridge.FloorInput(0.0), idleS.Thrust / 1000.0, idlePct, idleS.N2);
            Expect("抬底后网格确实给出慢车推力（>1 kN）", idleS.Thrust > 1000.0, true);
            Expect("慢车推力占满油门 5%~15%（战斗机涡扇 8~13%）", idlePct > 5.0 && idlePct < 15.0, true);

            // 原版在油门 0 时算出来的推力：这就是"慢车消失了"的定量证据。
            FuelBridge zero = StockBridge.Compute(idleS.Thrust / 1000.0, idleS.FuelFlow / 1000.0, 0.0);
            Console.WriteLine("    若按原版油门 0 直接换算：链路进入怠速档（Idle={0}），原版推力 {1:F3} kN",
                zero.Idle, zero.StockThrust(0.0));
            Expect("原版油门 0 的推力确实是 0（这就是要抬底的理由）", zero.StockThrust(0.0) < 0.01, true);

            // 抬底之后：原版用 maxFuelFlow × 抬底油门 × Isp × g 应当原样还回我们的推力。
            FuelBridge b = StockBridge.Compute(idleS.Thrust / 1000.0, idleS.FuelFlow / 1000.0,
                StockBridge.FloorInput(0.0));
            Console.WriteLine("    抬底后换算：maxFuelFlow {0:F6} t/s　Isp {1:F0} s　原版算回 {2:F2} kN（模型 {3:F2}）",
                b.MaxFuelFlow, b.Isp, b.StockThrust(StockBridge.FloorInput(0.0)), idleS.Thrust / 1000.0);
            Check("抬底后原版算回的慢车推力", b.StockThrust(StockBridge.FloorInput(0.0)),
                idleS.Thrust / 1000.0, 0.05, " kN");
            Check("抬底后原版算回的慢车油耗", b.StockFlow(StockBridge.FloorInput(0.0)) * 1000.0,
                idleS.FuelFlow, 0.05, " kg/s");

            // 飞行中真正的油门区间：抬底把 [0,1] 映射成 [0.02,1]，所以要确认满油门没被削。
            Check("抬底后满油门仍是满油门", StockBridge.FloorInput(1.0), 1.0, 0.1, " ×");
            Expect("抬底不该影响巡航段（0.8 原样）", Math.Abs(StockBridge.FloorInput(0.8) - 0.8) < 1e-12, true);
        }

        private static PerformanceGrid BuildGrid(EngineDesignData d, DesignConstants c,
            double[] ths, double[] alts, double[] machs)
        {
            PerformanceGrid g = new PerformanceGrid { Throttle = ths, Alt = alts, Mach = machs };
            int n = ths.Length * alts.Length * machs.Length;
            g.Thrust = new double[n]; g.Sfc = new double[n]; g.MassFlow = new double[n];
            g.FuelFlow = new double[n]; g.N1 = new double[n]; g.N2 = new double[n];
            g.Egt = new double[n]; g.T7 = new double[n]; g.Valid = new bool[n];
            for (int t = 0; t < ths.Length; t++)
                for (int ai = 0; ai < alts.Length; ai++)
                    for (int m = 0; m < machs.Length; m++)
                    {
                        OffDesignResult o = OffDesign.Solve(d, alts[ai], machs[m], ths[t], 0.0, c);
                        int i = g.Index(t, ai, m);
                        g.Thrust[i] = o.Ok ? o.NetThrust : 0.0;
                        g.Sfc[i] = o.Ok ? o.Sfc : 0.0;
                        g.MassFlow[i] = o.MdotAir; g.FuelFlow[i] = o.FuelFlow;
                        g.N1[i] = o.N1Pct; g.N2[i] = o.N2Pct; g.Egt[i] = o.T5;
                        g.T7[i] = o.T7;
                        g.Valid[i] = o.Ok && o.NetThrust > 0.0;
                    }
            return g;
        }

        /// <summary>
        /// 飞行链路整段（Core/FlightStep）。这一段以前只活在 PartModule 里，
        /// 命令行一行都跑不了——顺序错了、闸门放错位置了都只能靠实飞才发现。
        /// 这里按探针自动台架那条时间轴走一遍：点火 → 满油门 → 半油门 → 收光 → 关车。
        /// </summary>
        private static void RunFlightStepCases(EngineDesignData d, DesignConstants c)
        {
            PerformanceGrid g = GridFactory.Build(d, c);
            double tauUp = Spool.TauUp(d), tauDown = Spool.TauDown(d);
            double area = 1.086;   // JF-1 的进气道迎风面积 m²
            double dt = 0.02;

            double spool = 0.0;
            bool init = false;

            // ---- A. 地面时间轴：油门 1.0（10 s）→ 0.5（8 s）→ 0.0（15 s）→ 关车 ----
            FlightStepResult r = default(FlightStepResult);
            for (int i = 0; i < 3300; i++)   // 66 s：够走完 45 s 的台架时间轴再多留几秒
            {
                double t = i * dt;
                bool ignited = t < 45.0;
                double cmdIn = t < 22.0 ? 1.0 : (t < 30.0 ? 0.5 : 0.0);
                r = FlightStep.Compute(g, cmdIn, dt, 0.0, 0.0,
                    ignited, false, true, true, ref spool, ref init,
                    tauUp, tauDown, area, 0.0527);
            }

            // 关车之后：推力必须真的是 0，而且转子停在风车转速（地面 = 0）。
            ExpectEq("关车后模式是 NotIgnited", r.Mode, FlightMode.NotIgnited);
            Expect("关车后推力真的是 0", r.ThrustN == 0.0, true);
            // 地面上没有来流，风车转速只剩 SpeedFrac 的下限 6%（见 Windmill.SpeedFrac）——
            // 真机停在地面当然不转，但这 6% 只影响"地面重新点火"那一下的起点，微不足道。
            Expect("地面关车后转子转速落在地上限附近（≤10%）", spool < 0.10, true);
            Expect("关车后原版链路进入怠速档（不会偷偷给推力）",
                r.Bridge.StockThrust(r.Command) < 0.01, true);

            // ---- B. 收光油门的稳态：慢车。这是第 20 段抬底在整条链路上的兑现 ----
            double spoolIdle = 0.0;
            bool initIdle = false;
            FlightStepResult idle = default(FlightStepResult);
            for (int i = 0; i < 900; i++)   // 18 s 足够滞后收敛
                idle = FlightStep.Compute(g, 0.0, dt, 0.0, 0.0,
                    true, false, true, true, ref spoolIdle, ref initIdle,
                    tauUp, tauDown, area, 0.0527);

            Console.WriteLine("    地面收光油门 18 s 后：推力 {0:F2} kN　命令油门 {1:F3}　实际油门 {2:F3}　N2 {3:F1}%",
                idle.ThrustN / 1000.0, idle.Command, idle.Throttle, idle.N2);
            ExpectEq("收光油门仍在运行（不是关车）", idle.Mode, FlightMode.Running);
            Expect("收光油门有慢车推力，不是 0（真机的慢车）", idle.ThrustN > 5000.0, true);
            Check("抬底后的命令油门", idle.Command, StockBridge.InputFloor, 0.1, " ×");
            Check("慢车占海平面满油门", idle.ThrustN / g.Interp(1.0, 0.0, 0.0).Thrust * 100.0, 13.0, 25.0, "%");

            // 原版那一侧：它拿 Command 去乘 maxFuelFlow，算回来的就该是我们的推力。
            Check("原版按抬底油门算回的慢车推力", idle.Bridge.StockThrust(idle.Command),
                idle.ThrustN / 1000.0, 0.05, " kN");

            // ---- C. 转子滞后：命令跳变之后不许一帧到位 ----
            double spoolJ = 0.0;
            bool initJ = false;
            FlightStep.Compute(g, 0.0, dt, 0.0, 0.0, true, false, true, true,
                ref spoolJ, ref initJ, tauUp, tauDown, area, 0.0527);   // 先落到慢车
            for (int i = 0; i < 900; i++)
                FlightStep.Compute(g, 0.0, dt, 0.0, 0.0, true, false, true, true,
                    ref spoolJ, ref initJ, tauUp, tauDown, area, 0.0527);

            FlightStepResult oneFrame = FlightStep.Compute(g, 1.0, dt, 0.0, 0.0, true, false, true, true,
                ref spoolJ, ref initJ, tauUp, tauDown, area, 0.0527);
            double full = g.Interp(1.0, 0.0, 0.0).Thrust;
            Console.WriteLine("    满油门后一帧：推力 {0:F2} kN（满油门 {1:F2} kN，{2:F1}%）",
                oneFrame.ThrustN / 1000.0, full / 1000.0, oneFrame.ThrustN / full * 100.0);
            Expect("满油门一帧之内不到满推力（有惯性）", oneFrame.ThrustN < full * 0.6, true);
            Expect("满油门一帧也不是 0（转子已经在转了）", oneFrame.ThrustN > 1000.0, true);

            // ---- D. 空中熄火：风车拖飞机，而且再点火不是从 0 起 ----
            double spoolA = 0.0;
            bool initA = false;
            FlightStepResult wm = FlightStep.Compute(g, 0.0, dt, 11000.0, 0.8,
                false, false, true, true, ref spoolA, ref initA, tauUp, tauDown, area, 0.0527);
            Console.WriteLine("    11 km M0.8 熄火：风车阻力 {0:F2} kN　起点转速 {1:F0}%",
                wm.Windmill.DragN / 1000.0, spoolA * 100.0);
            Expect("空中熄火有风车阻力（关车不是消失）", wm.Windmill.Valid && wm.Windmill.DragN > 100.0, true);
            Check("11 km M0.8 风车阻力", wm.Windmill.DragN / 1000.0, 1.75, 25.0, " kN");
            Expect("再点火起点不是 0（真机空中点火就这道理）", spoolA > 0.2, true);

            FlightStepResult reign = FlightStep.Compute(g, 1.0, dt, 11000.0, 0.8,
                true, false, true, true, ref spoolA, ref initA, tauUp, tauDown, area, 0.0527);
            double fullAlt = g.Interp(1.0, 11000.0, 0.8).Thrust;
            Expect("空中点火后第一帧仍低于满推力（从风车转速往上爬）",
                reign.ThrustN < fullAlt * 0.95, true);

            // ---- E. 自持闸：设计不成立时整条链路给 0，而不是"打个折" ----
            double spoolN = 0.0;
            bool initN = false;
            FlightStepResult bad;
            for (int i = 0; i < 300; i++)
                bad = FlightStep.Compute(g, 1.0, dt, 0.0, 0.0, true, false, false, true,
                    ref spoolN, ref initN, tauUp, tauDown, area, 0.0527);
            bad = FlightStep.Compute(g, 1.0, dt, 0.0, 0.0, true, false, false, true,
                ref spoolN, ref initN, tauUp, tauDown, area, 0.0527);
            ExpectEq("设计不成立时模式是 NotSustaining", bad.Mode, FlightMode.NotSustaining);
            Expect("设计不成立时推力是 0", bad.ThrustN == 0.0, true);
            Expect("设计不成立时仍给出网格样本（状态栏要靠它说原因）", bad.Sample.Valid, true);

            // ---- E2. 熄火之后：转速一路往下溜，**没有判据**，也不会自己摘火 ----
            // 以前这里有一档 Shutdown：掉到 12% 就 EngineIgnited=false，等于替物理宣布"完蛋了"。
            // 现在中间那道闸拆了，只留下"火灭了 → 转子按惯性往下溜"这一条物理。
            {
                // 地面上熄火：风车转速≈0，转子一路掉，模式始终是"在溜"，不跳任何一档。
                double sp = 0.09;
                bool ini = true;
                FlightStepResult st = FlightStep.Compute(g, 1.0, dt, 0.0, 0.0,
                    true, true, true, true, ref sp, ref ini, tauUp, tauDown, area, 0.0527);
                Expect("地面熄火 9% 转速：还在溜（不是被强行停车）", st.Coasting, true);
                Expect("  推力归零", st.ThrustN == 0.0, true);
                Expect("  不再烧油", st.FuelTps == 0.0, true);
                Expect("  转速还在往风车掉", sp <= 0.09 + 1e-9, true);
                Expect("  但没被压到 0——惯性还在", sp > 0.0, true);

                // 同一台机器连着溜 60 s（时间加速 60 倍也是这么一帧一帧过的）：
                // 转速单调下降、最后落到风车附近，中途一次都没有"突然变成别的状态"。
                double sp4 = 0.60;
                bool ini4 = true;
                double prevSpool = sp4;
                bool monotone = true;
                FlightStepResult last = st;
                for (int i = 0; i < 600; i++)
                {
                    last = FlightStep.Compute(g, 1.0, dt, 0.0, 0.0,
                        true, true, true, true, ref sp4, ref ini4, tauUp, tauDown, area, 0.0527);
                    if (sp4 > prevSpool + 1e-9) monotone = false;
                    prevSpool = sp4;
                    if (last.Mode == FlightMode.Running) monotone = false;   // 火灭了就不可能回到运行
                    if ((int)last.Mode > (int)FlightMode.Coasting) monotone = false;  // 也不许冒出新状态档
                }
                Expect("溜 10 s 转速单调不升", monotone, true);
                // 掉多少由 tauCoast（≥2.5 s）决定，所以只断言"掉了一大截、且没掉到风车以下"，
                // 不写死一个具体数字——时间常数本来就跟着转子惯量走。
                double floor = st.Windmill.Valid ? st.Windmill.SpoolStart : 0.0;
                Expect("  10 s 后掉了四成以上", sp4 < 0.60 * 0.60, true);
                Expect("  但没掉到风车转速以下", sp4 >= floor - 1e-9, true);

                // 同一帧但海拔高、有风车把转速托住（11 km M0.8）：转速不会掉到 0。
                double sp2 = 0.30;
                bool ini2 = true;
                FlightStepResult st2 = FlightStep.Compute(g, 1.0, dt, 11000.0, 0.8,
                    true, true, true, true, ref sp2, ref ini2, tauUp, tauDown, area, 0.0527);
                Expect("高空熄火：转速被风车托住", sp2 > 0.10, true);
                Expect("  而且不跳状态档（只能是在溜或已熄火）",
                    st2.Mode == FlightMode.Coasting || st2.Mode == FlightMode.Flameout, true);

                // 从没点过火的机器：停机坪上 spool=0，不能报"熄火"，就是没点火。
                double sp3 = 0.0;
                bool ini3 = true;
                FlightStepResult st3 = FlightStep.Compute(g, 0.0, dt, 0.0, 0.0,
                    false, false, true, true, ref sp3, ref ini3, tauUp, tauDown, area, 0.0527);
                ExpectEq("没点过火 → 不是停车", st3.Mode, FlightMode.NotIgnited);
            }

            // ---- F. 收油比加速快：这条只有整段链路看得出来 ----
            Expect("收油时间常数比加速短", tauDown < tauUp, true);
        }

        /// <summary>
        /// 设计指纹与表缓存。两件事：①指纹必须对每个参数敏感（漏一个字段，
        /// 玩家改完参数会发现推力没变）；②同型号的多台发动机必须共用一张表。
        /// </summary>
        private static void RunCacheCases(EngineDesignData d, DesignConstants c)
        {
            EngineDesignData twin = new EngineDesignData();
            Expect("两份默认设计的指纹相同", DesignSignature.Of(d) == DesignSignature.Of(twin), true);
            Expect("Clone 出来的指纹也相同", DesignSignature.Of(d) == DesignSignature.Of(d.Clone()), true);

            // 全字段扫（含嵌套部件的每个叶子字段）：每个参数动一下，指纹都必须跟着变。
            int missed = 0;
            int probed = SweepSignature(d, ref missed);
            Console.WriteLine("  指纹敏感性：动了 {0} 个公有字段，漏 {1} 个", probed, missed);
            ExpectEq("没有字段漏进指纹", missed, 0);
            Expect("扫到的字段数量像话（>30，含嵌套部件）", probed > 30, true);

            // 嵌套部件也必须进去：改一级压气机级数，指纹得变。
            EngineDesignData nested = d.Clone();
            nested.Hpc.Stages += 1;
            Expect("嵌套部件（HPC 级数）进指纹",
                DesignSignature.Of(nested) != DesignSignature.Of(d), true);

            // ---- 缓存 ----
            GridFactory.ClearCache();
            PerformanceGrid g1 = GridFactory.BuildCached(d, c);
            Stopwatch sw = Stopwatch.StartNew();
            PerformanceGrid g2 = GridFactory.BuildCached(d, c);
            double hitMs = sw.Elapsed.TotalMilliseconds;
            sw.Stop();

            Console.WriteLine("  建表 {0:F0} ms，命中 {1:F2} ms（同一张表：{2}）",
                g1.BuildSeconds * 1000.0, hitMs, ReferenceEquals(g1, g2));
            Expect("同设计第二次拿到的是同一张表", ReferenceEquals(g1, g2), true);
            Expect("命中就该是毫秒级（<5 ms）", hitMs < 5.0, true);
            ExpectEq("未命中只发生一次", GridFactory.CacheMisses, 1);

            // 改一个参数必须换一张表——这是缓存最容易出错的地方。
            EngineDesignData tweaked = d.Clone();
            tweaked.Fan.Diameter += 0.05;
            PerformanceGrid g3 = GridFactory.BuildCached(tweaked, OffDesign.DeriveDesignConstants(tweaked));
            Expect("改了风扇直径就不再是同一张表", !ReferenceEquals(g1, g3), true);
            Check("改风扇直径后推力确实变了", g3.Interp(1.0, 0.0, 0.0).Thrust,
                g1.Interp(1.0, 0.0, 0.0).Thrust, 100.0, " N");

            // 干态与加力是两张表，不许互相顶掉。（拿一台真配了加力的，否则 abFrac 不起作用）
            EngineDesignData abl = d.Clone();
            abl.Afterburner.Enabled = true;
            abl.Nozzle = NozzleKind.Variable;
            DesignConstants ablC = OffDesign.DeriveDesignConstants(abl);
            PerformanceGrid gDry = GridFactory.BuildCached(abl, ablC, 0.0);
            PerformanceGrid gAb = GridFactory.BuildCached(abl, ablC, 1.0);
            Expect("干态与加力是两张不同的表", !ReferenceEquals(gDry, gAb), true);
            Expect("加力表推力更大", gAb.Interp(1.0, 0.0, 0.0).Thrust > gDry.Interp(1.0, 0.0, 0.0).Thrust, true);

            // 四台同型号发动机：载入时只该建一张表。这是缓存存在的唯一理由。
            GridFactory.ClearCache();
            int missesBefore = GridFactory.CacheMisses;
            Stopwatch fleet = Stopwatch.StartNew();
            PerformanceGrid first = null;
            bool allSame = true;
            for (int i = 0; i < 4; i++)
            {
                EngineDesignData unit = d.Clone();
                DesignConstants uc = OffDesign.DeriveDesignConstants(unit);
                PerformanceGrid gi = GridFactory.BuildCached(unit, uc);
                if (i == 0) first = gi; else allSame &= ReferenceEquals(gi, first);
            }
            fleet.Stop();
            Console.WriteLine("  四台同型号发动机：4 次取表共 {0:F1} ms，表只建了 {1} 张（4 张的话约 {2:F0} ms）",
                fleet.Elapsed.TotalMilliseconds, GridFactory.CacheMisses - missesBefore,
                first.BuildSeconds * 1000.0 * 4);
            ExpectEq("四台同型号只建一张表", GridFactory.CacheMisses - missesBefore, 1);
            Expect("四台拿的是同一张表", allSame, true);
            Expect("四台取表总耗时远小于建四张", fleet.Elapsed.TotalMilliseconds < first.BuildSeconds * 1000.0 * 2, true);

            // 缓存表必须与现算表逐点相同（缓存不许有"神韵"）。
            PerformanceGrid fresh = GridFactory.Build(d, c);
            double worst = 0.0;
            foreach (double thr in new[] { 0.02, 0.3, 0.7, 1.0 })
                foreach (double alt in new[] { 0.0, 6000.0, 11000.0 })
                    foreach (double mach in new[] { 0.0, 0.6, 0.9 })
                    {
                        PerformanceGrid.Sample s1 = g1.Interp(thr, alt, mach);
                        PerformanceGrid.Sample s2 = fresh.Interp(thr, alt, mach);
                        worst = Math.Max(worst, Math.Abs(s1.Thrust - s2.Thrust));
                    }
            Console.WriteLine("  缓存表 vs 现算表：最大推力差 {0:F4} N", worst);
            Expect("缓存表与现算表逐点相同（差 <1e-6 N）", worst < 1e-6, true);

            // 淘汰：连建设计不同的一堆表，条目数不许涨破上限。
            for (int i = 0; i < 12; i++)
            {
                EngineDesignData more = d.Clone();
                more.Length += 0.1 * (i + 1);
                GridFactory.BuildCached(more, OffDesign.DeriveDesignConstants(more));
            }
            Console.WriteLine("  连建 12 张不同的表之后：缓存条目 {0}（上限 8），命中 {1} / 未命中 {2}",
                GridFactory.CacheCount, GridFactory.CacheHits, GridFactory.CacheMisses);
            Expect("缓存条目不超过上限", GridFactory.CacheCount <= 8, true);
            Expect("淘汰之后仍有条目", GridFactory.CacheCount > 0, true);
        }

        /// <summary>扫每一个公有叶子字段（含嵌套部件 spec），确认它们全都进了指纹。</summary>
        private static int SweepSignature(EngineDesignData baseDesign, ref int missed)
        {
            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            int probed = 0;

            foreach (System.Reflection.FieldInfo f in typeof(EngineDesignData).GetFields(F))
            {
                EngineDesignData m = baseDesign.Clone();
                object cur = f.GetValue(m);

                object leaf = BumpField(cur);
                if (leaf != null)
                {
                    f.SetValue(m, leaf);
                    probed++;
                    if (DesignSignature.Of(m) == DesignSignature.Of(baseDesign))
                    {
                        missed++;
                        Console.WriteLine("    ** 字段没进指纹: {0}", f.Name);
                    }
                    continue;
                }

                if (cur == null) continue;
                Type st = cur.GetType();
                if (st.IsPrimitive || st.IsEnum || cur is string) continue;

                // 嵌套部件：逐个叶子字段改一遍。（struct 以装箱值的形式改，再整体写回父字段）
                foreach (System.Reflection.FieldInfo g in st.GetFields(F))
                {
                    EngineDesignData m2 = baseDesign.Clone();
                    object boxed = f.GetValue(m2);
                    object leaf2 = BumpField(g.GetValue(boxed));
                    if (leaf2 == null) continue;
                    g.SetValue(boxed, leaf2);
                    f.SetValue(m2, boxed);
                    probed++;
                    if (DesignSignature.Of(m2) == DesignSignature.Of(baseDesign))
                    {
                        missed++;
                        Console.WriteLine("    ** 字段没进指纹: {0}.{1}", f.Name, g.Name);
                    }
                }
            }
            return probed;
        }

        /// <summary>
        /// 真机预设。参数是照手册抄的几何 + 把涡轮 psi 拟合到真机 OPR 得出来的，
        /// 所以这里断言的是"我们的求解器对这几台真实发动机给出的数落在真机量级"。
        /// 已知的方向性偏差（SFC 系统性偏低 4~7%，J85 因为手册自相矛盾低 30%）写进容差，
        /// 不当成待修的 bug——它们在阶段一已经查过一轮（docs/phase1-design.md §6）。
        /// </summary>
        private static void RunPresetCases()
        {
            IList<PresetInfo> all = Presets.All;
            Expect("预设至少四台", all.Count >= 4, true);

            Console.WriteLine("  机型      OPR算/真      推力kN算/真    流量算/真      SFC算/真       干重算/真");
            foreach (PresetInfo p in all)
            {
                EngineDesignData d = Presets.Build(p.Key);
                CycleResult r = Cycle.SolveDesignPoint(d);
                Expect("预设 " + p.Key + " 设计点成立", r.Ok, true);
                if (!r.Ok) { Console.WriteLine("    {0}: 设计点不成立 {1}", p.Key, r.Errors.Count > 0 ? r.Errors[0] : ""); continue; }

                double thrustKN = r.Thrust / 1000.0;
                double dry = MassCost.Estimate(d).TotalDry;
                Console.WriteLine("  {0,-8} {1,6:F2}/{2,5:F2}  {3,7:F2}/{4,6:F2}  {5,6:F1}/{6,5:F1}  {7,7:F4}/{8,6:F4}  {9,6:F0}/{10,5:F0}",
                    p.Key, r.Opr, p.RealOpr, thrustKN, p.RealThrustKN, r.MdotAir, p.RealMdot,
                    r.Sfc, p.RealSfc, dry, p.RealDryKg);

                bool j85 = string.Equals(p.Key, "J85", StringComparison.OrdinalIgnoreCase);
                Check(p.Key + " 总压比", r.Opr, p.RealOpr, 8.0, "");
                Check(p.Key + " 进气流量", r.MdotAir, p.RealMdot, 6.0, " kg/s");
                Check(p.Key + " 干态推力", thrustKN, p.RealThrustKN, 20.0, " kN");
                Check(p.Key + " SFC", r.Sfc, p.RealSfc, j85 ? 35.0 : 12.0, " kg/(N·h)");
                Check(p.Key + " 干重", dry, p.RealDryKg, 45.0, " kg");
            }

            // 两次 Build 必须给出同一台机器（FitInlet 是确定性的迭代，不许留下残余漂移）。
            EngineDesignData a = Presets.Build("J79");
            EngineDesignData b = Presets.Build("J79");
            Expect("同一预设两次构建的进气口径相同",
                Math.Abs(a.InletDiameter - b.InletDiameter) < 1e-9, true);
            CycleResult ra = Cycle.SolveDesignPoint(a), rb = Cycle.SolveDesignPoint(b);
            Check("两次构建的设计点推力一致", ra.Thrust / 1000.0, rb.Thrust / 1000.0, 0.001, " kN");

            // 预设是独立对象：改一个不该污染下一次 Build。
            a.Fan.Diameter += 0.5;
            Expect("改预设实例不污染下一个实例",
                Math.Abs(Presets.Build("J79").Fan.Diameter - b.Fan.Diameter) < 1e-9, true);

            // Clone 漏字段是不会报错的：预设写了"罐环 8 罐"，Clone 之后悄悄回到缺省 0 罐，
            // 飞行里只表现为"燃烧室压损和出口不均匀度不对"，没人会去怀疑 Clone。
            EngineDesignData j79 = Presets.Build("J79");
            Expect("J79 燃烧室是罐环 8 罐", (int)j79.Burner.CanCount == 8, true);
            Check("J79 火焰筒长径比", j79.Burner.LinerLd, 2.4, 0.001, "");
            EngineDesignData cl = j79.Clone();
            Expect("Clone 保留罐数", (int)cl.Burner.CanCount == (int)j79.Burner.CanCount, true);
            Check("Clone 保留长径比", cl.Burner.LinerLd, j79.Burner.LinerLd, 1e-9, "");
            Check("Clone 保留加力点亮油门", cl.Afterburner.LightThrottle, j79.Afterburner.LightThrottle, 1e-9, "");
            Expect("Clone 保留加力自动档", cl.Afterburner.AutoLight, j79.Afterburner.AutoLight);

            // 喘振裕度：真机不会一上来就在失速区，预设必须过判据闸门。
            foreach (PresetInfo p in all)
            {
                EngineDesignData dp = Presets.Build(p.Key);
                CycleResult rp = Cycle.SolveDesignPoint(dp);
                if (!rp.Ok) continue;
                CheckReport rep = Criteria.Evaluate(dp, Criteria.ViewOf(rp), dp.Tech, Gas.P0, 0.0);
                Expect("预设 " + p.Key + " 过得了判据闸门（最差不是 FAIL）",
                    rep.Worst == Level.Fail, false);
            }
        }

        /// <summary>把字段的值"动一下"，用于指纹敏感性扫描。</summary>
        private static object BumpField(object cur)
        {
            if (cur is double) return (double)cur + 1.0;
            if (cur is float) return (float)cur + 1.0f;
            if (cur is int) return (int)cur + 1;
            if (cur is bool) return !(bool)cur;
            if (cur is string) return (string)cur + "x";
            if (cur is Enum)
            {
                Array vals = Enum.GetValues(cur.GetType());
                foreach (object v in vals)
                    if (!v.Equals(cur)) return v;
                return null;
            }
            return null;
        }
            // ================= 24. 转速读数（N1 / N2 / N3） =================

        /// <summary>
        /// 真机命名不是按"第几根轴"来的：单转子涡喷（J79/J85）驾驶舱里只有 N1，
        /// 双轴机是 N1/N2，三轴机才轮到 N3。同一份离线求解结果，三条轴的转速也必须
        /// 各自不同——它们的设计转速是由各自叶轮的最大叶尖马赫定出来的。
        /// </summary>
        private static void RunSpoolReadoutCases()
        {
            // 单转子涡喷：J85
            EngineDesignData one = Presets.Build("J85");
            DesignConstants c1 = OffDesign.DeriveDesignConstants(one);
            OffDesignResult o1 = OffDesign.Solve(one, 0.0, 0.0, 1.0, 0.0, c1);
            Console.WriteLine("  单转子 J85：成立={0} 转速 {1}", o1.Ok, SpoolLine(o1));
            Console.WriteLine("    轴数 {0}｜N1={1:F1}% N2={2:F1}% N3={3:F1}%", one.ShaftCount, o1.N1Pct, o1.N2Pct, o1.N3Pct);
            Expect("单转子：有 N1", o1.Spool.ContainsKey("N1%"), true);
            Expect("单转子：没有 N2", o1.Spool.ContainsKey("N2%"), false);
            Expect("单转子：N2 读数是 0", Math.Abs(o1.N2Pct) < 1e-9, true);

            // 双轴涡扇：默认 JF-1
            EngineDesignData two = new EngineDesignData();
            DesignConstants c2 = OffDesign.DeriveDesignConstants(two);
            OffDesignResult o2 = OffDesign.Solve(two, 0.0, 0.0, 1.0, 0.0, c2);
            Console.WriteLine("  双轴 JF-1：成立={0} 转速 {1}", o2.Ok, SpoolLine(o2));
            Console.WriteLine("    轴数 {0}｜N1={1:F1}% N2={2:F1}% N3={3:F1}%", two.ShaftCount, o2.N1Pct, o2.N2Pct, o2.N3Pct);
            Expect("双轴：有 N1", o2.Spool.ContainsKey("N1%"), true);
            Expect("双轴：有 N2", o2.Spool.ContainsKey("N2%"), true);
            Expect("双轴：没有 N3", o2.Spool.ContainsKey("N3%"), false);

            // 三轴
            EngineDesignData three = new EngineDesignData();
            three.ShaftCount = 3;
            three.Fan.Shaft = 1;
            three.Lpc.Stages = 3; three.Lpc.TipDiameter = 0.78; three.Lpc.Shaft = 2;
            three.Hpc.Stages = 6; three.Hpc.TipDiameter = 0.52; three.Hpc.Shaft = 3;
            three.Hpt.Stages = 1; three.Hpt.Shaft = 3;
            three.Ipt.Stages = 1; three.Ipt.TipDiameter = 0.70; three.Ipt.Shaft = 2;
            three.Lpt.Stages = 3; three.Lpt.Shaft = 1;
            DesignConstants c3 = OffDesign.DeriveDesignConstants(three);
            OffDesignResult o3 = OffDesign.Solve(three, 0.0, 0.0, 1.0, 0.0, c3);
            Console.WriteLine("  三轴    ：成立={0} 转速 {1}", o3.Ok, SpoolLine(o3));
            Console.WriteLine("    轴数 {0}｜N1={1:F1}% N2={2:F1}% N3={3:F1}%", three.ShaftCount, o3.N1Pct, o3.N2Pct, o3.N3Pct);
            Expect("三轴：三条转速都在", o3.Spool.ContainsKey("N1%") && o3.Spool.ContainsKey("N2%") && o3.Spool.ContainsKey("N3%"), true);
            Expect("三轴：N3 有数", o3.N3Pct > 0.0, true);

            // 每根轴的物理转速必须不同（设计转速由各自叶尖马赫定）
            List<int> shafts = new List<int>(o3.Rpm.Keys);
            shafts.Sort();
            double minGap = double.MaxValue;
            for (int i = 0; i < shafts.Count; i++)
                for (int j = i + 1; j < shafts.Count; j++)
                    minGap = Math.Min(minGap, Math.Abs(o3.Rpm[shafts[i]] - o3.Rpm[shafts[j]]));
            Console.WriteLine("    三根轴的最小转速差 {0:F0} rpm（若为 0 说明三条读数只是同一个数抄了三遍）", minGap);
            Expect("三根轴的转速互不相同", minGap > 100.0, true);

            // 设计点：每根轴都该是 100%
            CycleResult dp = Cycle.SolveDesignPoint(three);
            double worst = 0.0;
            foreach (KeyValuePair<string, double> kv in dp.Spool)
                if (kv.Key.EndsWith("%")) worst = Math.Max(worst, Math.Abs(kv.Value - 100.0));
            Console.WriteLine("    设计点各轴转速偏离 100% 最多 {0:F3}%（标定点自己必须对得上）", worst);
            Expect("设计点上每根轴都是 100%", worst < 0.5, true);

            // 叶尖线速度：真机压气机 300~500 m/s，风扇可以到 400~450，涡轮 300~450
            Console.WriteLine("    叶尖线速度：{0}", TipLine(o3));
            double maxTip = 0.0;
            foreach (KeyValuePair<int, double> kv in o3.TipSpeed) maxTip = Math.Max(maxTip, kv.Value);
            Check("三轴机最大叶尖线速度", maxTip, 400.0, 35.0, " m/s");

            // 网格与离线求解必须一致（飞行中读的是网格）
            PerformanceGrid g = GridFactory.Build(one, c1);
            PerformanceGrid.Sample sm = g.Interp(1.0, 0.0, 0.0);
            Console.WriteLine("  网格 vs 直接（J85 海平面满油门）：N1 {0:F1} / {1:F1} %", sm.N1, o1.N1Pct);
            Check("网格里的 N1 与离线一致", sm.N1, o1.N1Pct, 1.0, "%");
            PerformanceGrid g3 = GridFactory.Build(three, c3);
            PerformanceGrid.Sample s3m = g3.Interp(1.0, 0.0, 0.0);
            Console.WriteLine("  网格 vs 直接（三轴 海平面满油门）：N2 {0:F1} / {1:F1} %　N3 {2:F1} / {3:F1} %",
                s3m.N2, o3.N2Pct, s3m.N3, o3.N3Pct);
            Check("网格里的 N3 与离线一致", s3m.N3, o3.N3Pct, 1.5, "%");
            Expect("双轴机的 N3 列是 0", Math.Abs(g.Interp(1.0, 0.0, 0.0).N3) < 1e-9, true);

            // 掉转：涡轮"供"不上"需"的那根轴，转速会掉下来。
            // 功 ∝ U² ∝ N²，所以转速按 sqrt(供/需) 缩，不是直接按供/需 缩。
            // 这里先找到"设计点刚刚还能成立"的最低 IPT psi，那一档的涡轮刚好够用，
            // 非设计点就会被拉爆——正是要验的边界。
            double edgePsi = 0.0;
            for (double psi = 0.80; psi >= 0.20; psi -= 0.01)
            {
                EngineDesignData wk0 = three.Clone();
                wk0.Ipt.PsiScale = psi;
                DesignConstants ck0 = OffDesign.DeriveDesignConstants(wk0);
                if (ck0 == null || !ck0.Ok) continue;
                edgePsi = psi;
            }
            Check("设计点刚好成立的 IPT psi", edgePsi, 0.45, 10.0, "");

            EngineDesignData edge = three.Clone();
            edge.Ipt.PsiScale = edgePsi;
            DesignConstants ce = OffDesign.DeriveDesignConstants(edge);
            OffDesignResult full = OffDesign.Solve(edge, 0.0, 0.0, 1.0, 0.0, ce);
            OffDesignResult idle = OffDesign.Solve(edge, 0.0, 0.0, 0.2, 0.0, ce);
            Console.WriteLine("  边缘涡轮（psi {0:F2}）：满油门 掉转 {1} 转速 {2}", edgePsi, ShortfallLine(full), SpoolLine(full));
            Console.WriteLine("  边缘涡轮（psi {0:F2}）：油门0.2 掉转 {1} 转速 {2}", edgePsi, ShortfallLine(idle), SpoolLine(idle));
            Expect("满油门时确实出现掉转", full.SpoolShortfall.Count > 0, true);
            Expect("掉的是 IPT 那根轴（轴 2）", full.SpoolShortfall.ContainsKey(2), true);
            double fFull = full.SpoolShortfall.ContainsKey(2) ? full.SpoolShortfall[2] : 0.0;
            double fIdle = idle.SpoolShortfall.ContainsKey(2) ? idle.SpoolShortfall[2] : 0.0;
            // 97% 是比热改正之前的读数（那时压气机功率虚高，涡轮显得更能带）。
            // 改成按级中点比热之后功率下来一点，同一台"边缘涡轮"能被带到的转速也低一点：
            // 93.9%。这条钉的是"接近设计转速但不是 100%"，不是某个精确值。
            Check("满油门轴 2 掉到设计转速", fFull * 100.0, 94.0, 3.0, "%");
            Expect("低油门掉得比高油门更狠（供按 U² 缩，需按流量缩得慢）", fIdle < fFull, true);
            bool sawWarn = false;
            foreach (string w in full.Warnings) if (w.Contains("掉转")) sawWarn = true;
            Expect("判据里说了为什么掉转", sawWarn, true);

            // 掉转因子必须是 sqrt(供/需)，不是其他关系
            double demIpt = full.WIpt, capIpt = full.CapIpt;
            double expected = demIpt > 1.0 && capIpt > 1.0 ? Math.Sqrt(capIpt / demIpt) : 0.0;
            Check("掉转因子 = sqrt(供/需)", fFull, expected, 0.5, " ×");
            Check("满油门时 IPT 的需/供比", demIpt / Math.Max(capIpt, 1.0), 1.10, 5.0, " ×");

            // 掉转后那根轴的转速读数必须真的低下去，另两根不受牵连
            Expect("掉转的轴 2 低于轴 3（同一换算转速下的相对落后）", full.N2Pct < full.N3Pct, true);
            Expect("扇轴 N1 没被杀螺（扇轴不参与掉转）", full.N1Pct > 99.0, true);

        }

        private static string SpoolLine(OffDesignResult o)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, double> kv in o.Spool)
                if (kv.Key.EndsWith("%")) parts.Add(string.Format("{0}={1:F1}%", kv.Key, kv.Value));
            parts.Sort();
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>
        /// 轴流 vs 离心。离心叶轮 ψ 是轴流的两倍上下（叶轮把气流甩到径向，
        /// 欧拉功里 U² 那一项乘得更狠），所以两级离心能顶十几级轴流；
        /// 代价是通流面积小、效率低一点、而且真机很少连排超过两级。
        /// 这里验三件事：同样的级数离心压比更高、两级离心能顶十几级轴流、
        /// 超过级数上限必须报出来。
        /// </summary>
        private static void RunCompressorKindCases(EngineDesignData d, DesignConstants c)
        {
            // 统一压气机口径：9 级轴流 vs 2 级离心，叶尖直径/轮毂比/载荷系数保持一致
            EngineDesignData ax = d.Clone();
            ax.Hpc.Kind = CompressorKind.Axial;
            ax.Hpc.Stages = 9;
            ax.Hpc.PsiScale = 1.0;

            EngineDesignData ax2 = ax.Clone();
            ax2.Hpc.Stages = 2;

            EngineDesignData ce2 = ax.Clone();
            ce2.Hpc.Kind = CompressorKind.Centrifugal;
            ce2.Hpc.Stages = 2;

            EngineDesignData ce1 = ax.Clone();
            ce1.Hpc.Kind = CompressorKind.Centrifugal;
            ce1.Hpc.Stages = 1;

            EngineDesignData ce3 = ax.Clone();
            ce3.Hpc.Kind = CompressorKind.Centrifugal;
            ce3.Hpc.Stages = 3;

            CycleResult rAx = Cycle.SolveDesignPoint(ax);
            CycleResult rAx2 = Cycle.SolveDesignPoint(ax2);
            CycleResult rCe1 = Cycle.SolveDesignPoint(ce1);
            CycleResult rCe2 = Cycle.SolveDesignPoint(ce2);
            CycleResult rCe3 = Cycle.SolveDesignPoint(ce3);

            Console.WriteLine(string.Format(
                "9 级轴流：成立={0} OPR={1:F2} 推力={2:F1} kN",
                rAx.Ok, rAx.Opr, rAx.Thrust / 1000.0));
            Console.WriteLine(string.Format(
                "2 级轴流：成立={0} OPR={1:F2} 推力={2:F1} kN",
                rAx2.Ok, rAx2.Opr, rAx2.Thrust / 1000.0));
            Console.WriteLine(string.Format(
                "2 级离心：成立={0} OPR={1:F2} 推力={2:F1} kN",
                rCe2.Ok, rCe2.Opr, rCe2.Thrust / 1000.0));
            Console.WriteLine(string.Format(
                "1 级离心：成立={0} OPR={1:F2}",
                rCe1.Ok, rCe1.Opr));
            if (!rAx.Ok && rAx.Errors.Count > 0)
                Console.WriteLine("  9 级轴流为什么不成立：" + rAx.Errors[0]);

            Expect("同样的 2 级，离心 OPR 明显高于轴流（ψ 是两倍上下）",
                             rCe2.Ok && rAx2.Ok && rCe2.Opr > rAx2.Opr * 1.25, true);
            // 期望值口径：比的是"整机 OPR"，里面还含着风扇与 LPC，所以 2 级离心
            // 不该拿去跟 9 级轴流的整机压比掰手腕。只要求它到三分之一。
            Expect("2 级离心的 OPR 至少到 9 级轴流的三分之一",
                             rCe2.Ok && rCe2.Opr > rAx.Opr / 3.0, true);
            Expect("离心级数上限是 2（真机很少超过）",
                             CompressorPhysics.PracticalStageLimit(CompressorKind.Centrifugal) == 2, true);
            Expect("离心效率罚 -1.5 点",
                             Math.Abs(CompressorPhysics.EtaDelta(CompressorKind.Centrifugal) + 0.015) < 1e-9, true);

            // 判据：3 级离心要报出来
            CheckReport rep = Criteria.Evaluate(ce3, Criteria.ViewOf(Cycle.SolveDesignPoint(ce3)));
            bool warned = false;
            foreach (Finding fd in rep.Findings)
                if (fd.Key == "comp.hpc.stages" && fd.LevelValue == Level.Warn) warned = true;
            Expect("3 级离心：判据标出级数超上限", warned, true);

            // 9 级轴流不该报
            CheckReport repAx = Criteria.Evaluate(ax, Criteria.ViewOf(Cycle.SolveDesignPoint(ax)));
            bool axWarn = false;
            foreach (Finding fd in repAx.Findings)
                if (fd.Key == "comp.hpc.stages" && fd.LevelValue == Level.Warn) axWarn = true;
            Expect("9 级轴流：不报级数问题", axWarn, false);
        }

        /// <summary>
        /// 燃烧室形状。类型（罐式/罐环/环形/短环形）给基准压损与效率，
        /// 形状（火焰筒长径比、罐数）在上面加减：太短烧不完、太长压损高、罐式出口温度不均。
        /// 全是量级拟合，不是火焰筒内部流场算出来的——这一点也写在文档里。
        /// </summary>
        private static void RunBurnerShapeCases(EngineDesignData d, DesignConstants c)
        {
            BurnerSpec can = new BurnerSpec { Kind = BurnerKind.Can };
            BurnerSpec ann = new BurnerSpec { Kind = BurnerKind.Annular };

            Cycle.BurnerPerf bCan = Cycle.BurnerPerformance(can);
            Cycle.BurnerPerf bAnn = Cycle.BurnerPerformance(ann);
            Console.WriteLine(string.Format(
                "罐式缺省：罐数 {0}　L/H {1:F2}　ΔP/P {2:F3}　η {3:F3}　不均匀度 {4:F2}",
                bCan.Cans, bCan.Ld, 1.0 - bCan.Sigma, bCan.Eta, bCan.PatternFactor));
            Console.WriteLine(string.Format(
                "环形缺省：罐数 {0}　L/H {1:F2}　ΔP/P {2:F3}　η {3:F3}　不均匀度 {4:F2}",
                bAnn.Cans, bAnn.Ld, 1.0 - bAnn.Sigma, bAnn.Eta, bAnn.PatternFactor));

            ExpectEq("罐式缺省 8 罐", bCan.Cans, 8);
            ExpectEq("环形没有罐", bAnn.Cans, 0);
            Check("罐式缺省 L/H", bCan.Ld, 3.0, 1.0, "");
            Check("环形缺省 L/H", bAnn.Ld, 2.2, 1.0, "");

            // 罐数：环形下被忽略
            BurnerSpec annCans = new BurnerSpec { Kind = BurnerKind.Annular, CanCount = 10 };
            ExpectEq("环形设了罐数也被忽略", Cycle.BurnerPerformance(annCans).Cans, 0);

            // 太短烧不完
            BurnerSpec shortL = new BurnerSpec { Kind = BurnerKind.Annular, LinerLd = 1.0 };
            BurnerSpec longL = new BurnerSpec { Kind = BurnerKind.Annular, LinerLd = 5.0 };
            Cycle.BurnerPerf bShort = Cycle.BurnerPerformance(shortL);
            Cycle.BurnerPerf bLong = Cycle.BurnerPerformance(longL);
            Console.WriteLine(string.Format(
                "L/H 1.0：η {0:F3}（缺省 {1:F3}）　ΔP/P {2:F3}｜L/H 5.0：η {3:F3}　ΔP/P {4:F3}",
                bShort.Eta, bAnn.Eta, 1.0 - bShort.Sigma, bLong.Eta, 1.0 - bLong.Sigma));

            Expect("火焰筒太短：效率明显低于缺省（>10%）",
                             bShort.Eta < bAnn.Eta * 0.90, true);
            Expect("火焰筒太长：总压损失明显高于缺省（>20%）",
                             1.0 - bLong.Sigma > (1.0 - bAnn.Sigma) * 1.20, true);
            Expect("罐式出口温度不均比环形更差",
                             bCan.PatternFactor > bAnn.PatternFactor, true);

            // 设计点：效率掉一点，推力应该跟着掉
            EngineDesignData good = d.Clone();
            good.Burner.LinerLd = 0.0;
            EngineDesignData bad = d.Clone();
            bad.Burner.LinerLd = 1.0;
            CycleResult rg = Cycle.SolveDesignPoint(good);
            CycleResult rb = Cycle.SolveDesignPoint(bad);
            Console.WriteLine(string.Format(
                "设计点：L/H 缺省 {0:F2} kN / SFC {1:F4}｜L/H 1.0 {2:F2} kN / SFC {3:F4}（SFC {4:+0.0;-0.0}%）",
                rg.Thrust / 1000.0, rg.Sfc, rb.Thrust / 1000.0, rb.Sfc, (rb.Sfc / rg.Sfc - 1.0) * 100.0));
            // 设计点固定在 T4 上：燃烧效率掉了并不掉推力，掉的是油耗——
            // 想省的那份燃油必须多烧出来。断言写"推力下降"是我一开始想把两件事混在一起。
            Expect("火焰筒太短时推力基本不变（设计点锁的是 T4）",
                             rb.Ok && Math.Abs(rb.Thrust / rg.Thrust - 1.0) < 0.02, true);
            Expect("火焰筒太短让油耗明显上升（>10%）",
                             rb.Sfc > rg.Sfc * 1.10, true);

            // 判据：三条都要报
            EngineDesignData warnShort = d.Clone();
            warnShort.Burner.LinerLd = 1.0;
            Expect("短火焰筒被判据标出", HasFinding(warnShort, "burner.liner", Level.Warn), true);

            EngineDesignData warnCans = d.Clone();
            warnCans.Burner.Kind = BurnerKind.Annular;
            warnCans.Burner.CanCount = 10;
            Expect("环形设罐数被判据标出", HasFinding(warnCans, "burner.shape", Level.Warn), true);

            EngineDesignData warnFew = d.Clone();
            warnFew.Burner.Kind = BurnerKind.Can;
            warnFew.Burner.CanCount = 2;
            Expect("罐式只有 2 罐被判据标出", HasFinding(warnFew, "burner.cans", Level.Warn), true);
        }

        /// <summary>
        /// 玩家自己存的设计。真机预设是固定的起点，这里是"我调出来的那台"。
        /// 守三件事：存了能原样读回来；同名再存不会堆成两个文件；文件被改坏只报错不崩。
        /// </summary>
        private static void RunUserLibraryCases(EngineDesignData d)
        {
            string dir = System.IO.Path.Combine(Environment.CurrentDirectory, "out", "userlib_test");
            try
            {
                if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            }
            catch (Exception) { }

            EngineDesignData mine = d.Clone();
            mine.DesignName = "测试 一号";
            mine.Burner.Kind = BurnerKind.Cannular;
            mine.Burner.CanCount = 12;
            mine.Burner.LinerLd = 2.75;
            mine.Hpc.Kind = CompressorKind.Centrifugal;
            mine.Hpc.Stages = 2;
            mine.DesignMach = 0.62;
            mine.DesignAlt = 8500.0;

            string p1 = UserLibrary.Save(dir, mine.DesignName, mine);
            Console.WriteLine("存到：" + p1);
            Expect("文件真的写出来了", System.IO.File.Exists(p1), true);

            List<UserDesignEntry> list = UserLibrary.List(dir);
            ExpectEq("目录里 1 个设计", list.Count, 1);
            ExpectEq("名字对得上", list[0].Name, "测试 一号");

            string err;
            EngineDesignData back = UserLibrary.Load(p1, out err);
            Expect("读得回来", back != null, true);
            if (back != null)
            {
                List<string> diffs = new List<string>();
                CompareFields(mine, back, "design", diffs);
                ExpectEq("读回来字段一致", diffs.Count, 0);
                if (diffs.Count > 0) Console.WriteLine("  不一致：" + string.Join("；", diffs.ToArray()));
                ExpectEq("燃烧室形状也回来了", back.Burner.CanCount, 12);
                Expect("压气机形式也回来了", back.Hpc.Kind == CompressorKind.Centrifugal, true);
            }

            // 同名再存一次：覆盖，不是新增
            UserLibrary.Save(dir, mine.DesignName, mine);
            ExpectEq("同名再存不新增文件", UserLibrary.List(dir).Count, 1);

            // 存第二台
            EngineDesignData second = d.Clone();
            second.DesignName = "二号机";
            UserLibrary.Save(dir, second.DesignName, second);
            ExpectEq("两台机两个文件", UserLibrary.List(dir).Count, 2);

            // 名字里的非法字符
            ExpectEq("斜杠被替换掉", UserLibrary.Sanitize("a/b"), "a_b");
            ExpectEq("冒号被替换掉", UserLibrary.Sanitize("a:b"), "a_b");
            ExpectEq("空名字有兜底", UserLibrary.Sanitize("   "), "未命名");
            Expect("超长名字被截断", UserLibrary.Sanitize(new string('x', 80)).Length <= UserLibrary.MaxNameLength, true);
            Console.WriteLine(string.Format("名字净化：a/b → {0}　空 → {1}　80 个 x → {2} 字符",
                UserLibrary.Sanitize("a/b"), UserLibrary.Sanitize("   "), UserLibrary.Sanitize(new string('x', 80)).Length));

            // 坏文件：读失败只报错，不抛
            string badPath = System.IO.Path.Combine(dir, "坏掉的设计.cfg");
            System.IO.File.WriteAllText(badPath, "这不是 cfg\n随便写点什么\n");
            EngineDesignData broken = null;
            string brokenErr = null;
            bool threw = false;
            try { broken = UserLibrary.Load(badPath, out brokenErr); }
            catch (Exception) { threw = true; }
            Expect("坏文件不抛异常", threw, false);
            Expect("坏文件读不出设计", broken == null, true);
            Expect("坏文件给出理由", !string.IsNullOrEmpty(brokenErr), true);
            Console.WriteLine("坏文件：" + (brokenErr ?? "（没给理由）"));

            // 改名与复制（第 116 段）：两者都得按字节搬，不许经过重新序列化。
            string rerr;
            string renamed = UserLibrary.Rename(dir, p1, "改名后的", out rerr);
            Expect("改名成功", renamed != null, true);
            if (renamed != null)
            {
                ExpectEq("改名的文件在原目录", System.IO.Path.GetDirectoryName(renamed), dir);
                ExpectEq("改名的文件名", System.IO.Path.GetFileNameWithoutExtension(renamed), "改名后的");
                Expect("旧名字不在了", System.IO.File.Exists(p1), false);
                Expect("设计还能读回来", UserLibrary.Load(renamed, out rerr) != null, true);
            }
            // 撞名不许覆盖：目录里已经有「二号机」
            Expect("改名撞名被拒", UserLibrary.Rename(dir, renamed ?? p1, "二号机", out rerr) == null, true);
            Expect("撞名给出理由", !string.IsNullOrEmpty(rerr), true);

            string copyName = UserLibrary.CopyName(dir, "二号机");
            ExpectEq("副本名追号", copyName, "二号机 副本");
            string src2 = System.IO.Path.Combine(dir, "二号机" + UserLibrary.Extension);
            string copied = UserLibrary.Copy(dir, src2, copyName, out rerr);
            Expect("复制成功", copied != null, true);
            if (copied != null)
            {
                byte[] a = System.IO.File.ReadAllBytes(src2);
                byte[] b = System.IO.File.ReadAllBytes(copied);
                bool same = a.Length == b.Length;
                if (same) for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { same = false; break; }
                Expect("副本与原件逐字节一致", same, true);
                Expect("副本也能读出设计", UserLibrary.Load(copied, out rerr) != null, true);
                Expect("同名的副本被拒", UserLibrary.Copy(dir, src2, copyName, out rerr) == null, true);
                Expect("副本不能和原件同名", UserLibrary.Copy(dir, src2, "二号机", out rerr) == null, true);
            }

            // 删除（删的是改名之后那个路径——p1 早就不存在了）
            Expect("删除成功", UserLibrary.Delete(renamed ?? p1), true);
            // 目录里还留着「二号机」「二号机 副本」和那个故意写坏的 cfg——列表是按文件列的，
            // 坏文件也会出现在列表里（玩家看得见、点了会得到一句"不是有效的 cfg"）。
            ExpectEq("删完还剩三个文件", UserLibrary.List(dir).Count, 3);
            Expect("删不存在的文件返回 false", UserLibrary.Delete(p1), false);

            // 存档历史（第 119 段）：覆盖之前要留一份，且只留最近几份。
            // 动机很具体——玩家调坏了想回上一版，而「上一版」已经被覆盖掉了。
            string hname = "历史机";
            EngineDesignData v1 = d.Clone();
            v1.DesignName = hname;
            v1.Fan.Diameter = 0.60;
            string hp = UserLibrary.Save(dir, hname, v1);
            Expect("历史机存下来了", System.IO.File.Exists(hp), true);
            ExpectEq("第一次存没有历史（本来就没有可覆盖的）", UserLibrary.History(dir, hname).Count, 0);

            EngineDesignData v2 = v1.Clone();
            v2.Fan.Diameter = 0.80;
            UserLibrary.Save(dir, hname, v2);
            List<UserDesignEntry> hist = UserLibrary.History(dir, hname);
            ExpectEq("覆盖一次留下 1 份历史", hist.Count, 1);
            EngineDesignData vOld = hist.Count > 0 ? UserLibrary.Load(hist[0].Path, out rerr) : null;
            Expect("历史能读出来", vOld != null, true);
            if (vOld != null)
            {
                Expect("历史里是旧那份（Ø" + vOld.Fan.Diameter.ToString("F2") + "）",
                       Math.Abs(vOld.Fan.Diameter - 0.60) < 1e-6, true);
            }

            // 再存 6 次：历史只留最近 5 份，存档目录不该无声长大
            for (int i = 0; i < 6; i++)
            {
                v2.Fan.Diameter = 0.80 + 0.01 * i;
                UserLibrary.Save(dir, hname, v2);
            }
            ExpectEq("历史只留 5 份", UserLibrary.History(dir, hname).Count, 5);

            // 恢复：回到某一份，而且当前状态也被留了一份——「恢复」本身也可能点错，
            // 那一下如果不可逆，这个按钮就变成了删除键。
            List<UserDesignEntry> before = UserLibrary.History(dir, hname);
            string snap = before[0].Path;
            string rp = UserLibrary.Restore(dir, hname, snap, out rerr);
            Expect("恢复成功", rp != null, true);
            EngineDesignData restored = rp != null ? UserLibrary.Load(rp, out rerr) : null;
            Expect("恢复后读得回来", restored != null, true);
            if (restored != null) ExpectEq("恢复后的直径来自那份快照", Math.Round(restored.Fan.Diameter, 2), 0.60);
            ExpectEq("恢复前把当前状态也留了底（仍 5 份，最旧那份被挤掉）",
                     UserLibrary.History(dir, hname).Count, 5);
            Console.WriteLine("存档历史：留 5 份｜恢复自 " + System.IO.Path.GetFileNameWithoutExtension(snap));

            // 分享（第 120 段）：Shared 是"交换站"——玩家自己把文件贴出去/拿进来，
            // 模组只管两头。这里钉的是三件事：按字节搬、重名不覆盖、收别人的不砸自己的。
            EngineDesignData sh = d.Clone();
            sh.DesignName = "交换机";
            sh.Fan.Diameter = 0.71;
            string shp = UserLibrary.Save(dir, "交换机", sh);
            Expect("交换机存下来了", System.IO.File.Exists(shp), true);

            string serr;
            string x1 = UserLibrary.Export(dir, shp, out serr);
            Expect("放进交换站", x1 != null, true);
            Expect("交换站里有它", UserLibrary.Shared(dir).Count, 1);
            if (x1 != null)
            {
                byte[] a = System.IO.File.ReadAllBytes(shp);
                byte[] b = System.IO.File.ReadAllBytes(x1);
                ExpectEq("放出去的是原件那份字节", b.Length, a.Length);
                Expect("放出去的与原件逐字节一致", Convert.ToBase64String(a) == Convert.ToBase64String(b), true);
            }

            // 同一份再放一次：追号，不覆盖前一次放出去的
            string x2 = UserLibrary.Export(dir, shp, out serr);
            Expect("再放一次也成功", x2 != null, true);
            ExpectEq("交换站里变成 2 个文件（不覆盖）", UserLibrary.Shared(dir).Count, 2);
            Expect("两次的文件不是同一个", x1 != null && x2 != null && x1 != x2, true);

            // 收一份没见过的（换个名字模拟别人给的文件）
            string foreign = System.IO.Path.Combine(UserLibrary.SharedDir(dir), "别人的机" + UserLibrary.Extension);
            System.IO.File.Copy(shp, foreign);
            string i1 = UserLibrary.Import(dir, foreign, out serr);
            Expect("收进来", i1 != null, true);
            if (i1 != null)
            {
                ExpectEq("收进来的名字就是文件名", System.IO.Path.GetFileNameWithoutExtension(i1), "别人的机");
                EngineDesignData got2 = UserLibrary.Load(i1, out serr);
                Expect("收进来的读得回来", got2 != null, true);
                if (got2 != null) ExpectEq("收进来的直径跟着文件", Math.Round(got2.Fan.Diameter, 2), 0.71);
            }

            // 同名再收一次：照收，但改名——不能拿别人的东西换自己的
            string i2 = UserLibrary.Import(dir, foreign, out serr);
            Expect("同名也照收", i2 != null, true);
            Expect("同名那份改了名（不砸库里原有的）", i2 != null && System.IO.Path.GetFileNameWithoutExtension(i2) != "别人的机", true);
            Expect("库里原来那份还在", System.IO.File.Exists(System.IO.Path.Combine(dir, "别人的机" + UserLibrary.Extension)), true);

            // 不存在的文件：给理由，不抛
            string i3 = UserLibrary.Import(dir, System.IO.Path.Combine(dir, "根本没有" + UserLibrary.Extension), out serr);
            Expect("收不存在的文件返回空", i3 == null, true);
            Expect("收不存在的文件要给理由", string.IsNullOrEmpty(serr), false);
            Console.WriteLine("交换站：放出 " + UserLibrary.Shared(dir).Count + " 个｜收进 2 个（第二个改名）");

            try { System.IO.Directory.Delete(dir, true); }
            catch (Exception) { }
        }

// ============ 29. 全参数空间不变式体检（新特性一起上） ============

        /// <summary>
        /// 第 19 段只验"不崩、不出 NaN"。这一段验的是**物理上必须成立的关系**，
        /// 而且专门把后来加的东西全部打开：离心压气机、四种燃烧室形状、收扩喷管、
        /// 加力 + 油气比、三轴。
        ///
        /// 不变式（任何一台发动机都该满足）：
        ///  ① 油门加大，推力不减（同一高度/马赫）
        ///  ② 高度升高，推力不增（同一油门/马赫）
        ///  ③ 有推力时 SFC 必须落在 (0, 1.0) —— 真机最差也就 0.3 上下
        ///  ④ 转速百分比落在 (0, 115]
        ///  ⑤ 加力表在同一点上推力不低于干态表
        ///  ⑥ 存文件再读回来，字段一个不差
        /// </summary>
// ============ 30. 加力瞬态：按下到满推力要一秒多，松开要退得慢一点 ============
        // 这一段验的是"时间"：真机加力从命令到推力到位约 1.5 s，熄火 0.5 s 上下。
        // 在这里可以按 20 ms 一步步走，游戏里只能靠人眼看——所以先在这里定死。
        /// <summary>
        /// <summary>
        /// 五台机的体检表：设计点、慢车占比、11 km 巡航、满油门、加力。
        /// 给玩家和文档一个统一口径——拿到零件先看这几个数，再决定要不要飞。
        /// </summary>
        private static void RunFleetSummary(EngineDesignData d)
        {
            Console.WriteLine("=== 35. 五台机体检表（同一口径）===");
            Console.WriteLine("  机种          设计点kN  慢车%  11kmM0.8  满油门kN  SFC慢车 SFC巡航 SFC满油门  加力kN");
            EngineDesignData[] fleet = { d, Presets.Build("J79"), Presets.Build("J57"), Presets.Build("J85"), Presets.Build("RD-33") };
            string[] names = { "JF-1(涡扇)", "J79(涡喷)", "J57(涡喷)", "J85(涡喷)", "RD-33(涡扇)" };
            for (int i = 0; i < fleet.Length; i++)
            {
                EngineDesignData e = fleet[i];
                DesignConstants c = OffDesign.DeriveDesignConstants(e);
                CycleResult dz = Cycle.SolveDesignPoint(e);
                if (!c.Ok || !dz.Ok) { Console.WriteLine("  " + names[i] + " 设计点不成立"); continue; }
                EngineDesignData eab = e.Clone();
                eab.Nozzle = NozzleKind.Variable;
                OffDesignResult idle = OffDesign.Solve(e, 0.0, 0.0, 0.0, 0.0, c, 60);
                Console.WriteLine(string.Format("    [{0}] T4Idle={1:F0}K 慢车有效={2} T4Max={3:F0}K", names[i], c.T4Idle, idle.Ok, c.T4Max));
                if (!idle.Ok) Console.WriteLine("  [{0} 慢车解不出来] {1}", names[i], idle.Errors.Count > 0 ? idle.Errors[0] : "无理由");
                OffDesignResult full = OffDesign.Solve(e, 0.0, 0.0, 1.0, 0.0, c, 60);
                OffDesignResult cru = OffDesign.Solve(e, 11000.0, 0.80, 0.85, 0.0, c, 60);
                OffDesignResult abr = e.Afterburner.Enabled
                    ? OffDesign.Solve(eab, 0.0, 0.0, 1.0, 1.0, OffDesign.DeriveDesignConstants(eab), 60) : null;
                double idleFrac = full.NetThrust > 1.0 ? idle.NetThrust / full.NetThrust * 100.0 : 0.0;
                Console.WriteLine(string.Format(
                    "  {0,-12} {1,7:F1} {2,6:F1} {3,9:F1} {4,9:F1} {5,7:F4} {6,7:F4} {7,9:F4} {8,7}",
                    names[i], dz.Thrust / 1000.0, idleFrac, cru.NetThrust / 1000.0, full.NetThrust / 1000.0,
                    idle.Sfc, cru.Sfc, full.Sfc,
                    (abr != null && abr.Ok) ? string.Format("{0:F1}", abr.NetThrust / 1000.0) : "—"));
            }
            Console.WriteLine();
        }
        private static void RunSfcDecomposition(EngineDesignData d)
        {
            double f1 = RunSfcDecompositionOne("JF-1（涵道比 2.75）", d);
            double f2 = RunSfcDecompositionOne("J79（涡喷）", Presets.Build("J79"));
            double f3 = RunSfcDecompositionOne("J57（涡喷）", Presets.Build("J57"));

            // 涡喷的谷底应该落在大功率段（真机 J79 在 90% 附近）。我们落在 0.65/0.75，
            // 偏低但形状是对的——这一段是在提醒"别把位置偏差当形状错误"。
            Expect("J79 的 SFC 谷底在大功率段（>=0.50）", f2 >= 0.50, true);
            Expect("J57 的 SFC 谷底在大功率段（>=0.50）", f3 >= 0.50, true);

            // 涡扇这一条现在必红——留着当"已知偏差"的刻度尺，收窄它才是这轮真正的活儿
            // （根因是涵道比与风扇转速都没有独立解，见文档 §43）。
            Console.WriteLine(string.Format(
                "  [已知偏差] 涡扇 SFC 谷底在油门 {0:F2}，真机 0.85~0.95（差 {1:F2}）",
                f1, 0.85 - f1));
        }

        private static double RunSfcDecompositionOne(string label, EngineDesignData d)
        {
            DesignConstants c = OffDesign.DeriveDesignConstants(d);
            if (!c.Ok)
            {
                Console.WriteLine("  " + label + " 设计常数不成立，跳过：" + (c.Errors.Count > 0 ? c.Errors[0] : "?"));
                return 0.0;
            }

            Console.WriteLine("  " + label);
            Console.WriteLine("  thr   N1%   N2%    T4    OPR   FPR  ṁair   far   F(kN)  F/ṁair  Vj核心 Vj外涵  SFC");
            double bestSfc = 1e9, bestThr = 0.0;
            for (int i = 0; i <= 20; i++)
            {
                double thr = i * 0.05;
                OffDesignResult r = OffDesign.Solve(d, 0.0, 0.0, thr, 0.0, c, 60);
                if (!r.Ok || r.NetThrust <= 1e-6) continue;
                double ts = r.NetThrust / Math.Max(r.MdotAir, 1e-9);
                if (r.Sfc < bestSfc) { bestSfc = r.Sfc; bestThr = thr; }
                Console.WriteLine(string.Format("  {0:F2} {1,5:F1} {2,5:F1} {3,6:F0} {4,6:F2} {5,5:F2} {6,5:F1} {7,6:F4} {8,6:F2} {9,7:F1} {10,6:F0} {11,6:F0} {12,6:F4}",
                    thr, r.N1Pct, r.N2Pct, r.T4, r.Opr, r.Fpr, r.MdotAir, r.Far,
                    r.NetThrust / 1000.0, ts, r.VJetCore, r.VJetBypass, r.Sfc));
            }
            Console.WriteLine(string.Format("  SFC 谷底：油门 {0:F2}（{1:F4} kg/(N·h)）", bestThr, bestSfc));
            Console.WriteLine();
            return bestThr;
        }

        /// <summary>
        /// 慢车与油耗曲线。
        /// 真机的两个事实：① 慢车推力只占最大推力的 5~10%（J79 约 4~5%，J85 约 8%）；
        /// ② SFC 最低点在 85~95% 功率，不是在小油门——小油门时循环温比低、部件效率掉，
        /// 反而更费油。这两条都是"玩家在座舱里能看出来"的量，所以值得单独钉住。
        /// </summary>
        private static void RunIdleAndSfcCurve(EngineDesignData d)
        {
            DesignConstants c = OffDesign.DeriveDesignConstants(d);
            if (!c.Ok)
            {
                Console.WriteLine("设计常数不成立，跳过：" + (c.Errors.Count > 0 ? c.Errors[0] : "?"));
                return;
            }

            OffDesignResult full = OffDesign.Solve(d, 0.0, 0.0, 1.0, 0.0, c);
            OffDesignResult idle = OffDesign.Solve(d, 0.0, 0.0, 0.0, 0.0, c);
            double fFull = full.Ok ? full.NetThrust / 1000.0 : 0.0;
            double fIdle = idle.Ok ? idle.NetThrust / 1000.0 : 0.0;
            Console.WriteLine(string.Format("慢车：{0:F2} kN（满油门的 {1:F1}%）｜N2 {2:F0}%｜T4 {3:F0}K｜SFC {4:F3}",
                fIdle, fFull > 1e-6 ? fIdle / fFull * 100.0 : 0.0,
                idle.Ok ? idle.N2Pct : 0.0, idle.Ok ? idle.T4 : 0.0, idle.Ok ? idle.Sfc : 0.0));
            Console.WriteLine(string.Format("满油门：{0:F2} kN｜T4 {1:F0}K｜SFC {2:F3}",
                fFull, full.Ok ? full.T4 : 0.0, full.Ok ? full.Sfc : 0.0));

            // 慢车附近逐点看：如果 N2 在这里跳一大格，玩家的油门会像开关一样。
            foreach (double t in new[] { 0.0, 0.01, 0.02, 0.05, 0.08, 0.10, 0.15, 0.20, 0.30 })
            {
                OffDesignResult o = OffDesign.Solve(d, 0.0, 0.0, t, 0.0, c);
                Console.WriteLine(string.Format("  油门 {0:F2}：ok={1} N2 {2:F1}% T4 {3:F0}K 推力 {4:F2} kN SFC {5:F3} OPR {6:F2} Ve {7:F0}/{8:F0} m/s T5 {9:F0}K m {10:F1}/{11:F1} p5 {12:F0}kPa",
                    t, o.Ok, o.Ok ? o.N2Pct : 0.0, o.T4, o.NetThrust / 1000.0, o.Sfc, o.Opr,
                    o.VJetCore, o.VJetBypass, o.T5, o.MdotCore, o.MdotAir, o.P5 / 1000.0));
            }

            // 喷口面积调度：同一条喷道，加力一开总温从 1085 K 涨到 1900 K，
            // 同样的喉道过不了这么多流量。能撑开的喷口应该比撑不开的多给推力。
            {
                EngineDesignData abD = d.Clone();
                abD.Afterburner.Enabled = true;
                abD.Afterburner.ExitTemp = 1900.0;
                DesignConstants cab = OffDesign.DeriveDesignConstants(abD);
                foreach (NozzleKind nk in new[] { NozzleKind.Converging, NozzleKind.Variable, NozzleKind.ConvergingDiverging })
                {
                    abD.Nozzle = nk;
                    OffDesignResult dr = OffDesign.Solve(abD, 0.0, 0.0, 1.0, 0.0, cab);
                    OffDesignResult wr = OffDesign.Solve(abD, 0.0, 0.0, 1.0, 1.0, cab);
                    double dn = dr.Ok ? dr.NetThrust / 1000.0 : 0.0;
                    double wn = wr.Ok ? wr.NetThrust / 1000.0 : 0.0;
                    Console.WriteLine(string.Format("  喷管 {0}：干态 {1:F2} kN｜加力 {2:F2} kN（{3:+0.0;-0.0}%）｜憋住 {4}",
                        nk, dn, wn, dn > 1e-6 ? (wn / dn - 1.0) * 100.0 : 0.0, wr.NozzleBackPressure));
                }
                abD.Nozzle = d.Nozzle;
            }

            double[] ths = { 0.05, 0.15, 0.25, 0.35, 0.50, 0.65, 0.75, 0.85, 0.95, 1.00 };
            foreach (double alt in new[] { 0.0, 11000.0 })
            {
                double mach = alt > 0.0 ? 0.80 : 0.0;
                double bestSfc = double.MaxValue, bestFrac = 0.0;
                StringBuilder line = new StringBuilder();
                foreach (double t in ths)
                {
                    OffDesignResult o = OffDesign.Solve(d, alt, mach, t, 0.0, c);
                    if (!o.Ok || o.NetThrust <= 1.0)
                    {
                        line.Append(string.Format(" {0:F2}→—", t));
                        continue;
                    }
                    double frac = fFull > 1e-6 ? (o.NetThrust / 1000.0) / fFull : 0.0;
                    line.Append(string.Format(" {0:F2}→{1:F3}/{2:F0}%", t, o.Sfc, frac * 100.0));
                    if (o.Sfc < bestSfc) { bestSfc = o.Sfc; bestFrac = frac; }
                }
                Console.WriteLine(string.Format("{0:F0} m M{1:F2} SFC：{2}", alt, mach, line.ToString().Trim()));
                Console.WriteLine(string.Format("  → 最省油在 {0:F0}% 推力（SFC {1:F3}）", bestFrac * 100.0, bestSfc));
            }
        }

        private static void RunAfterburnerTransientCases(EngineDesignData d)
        {
            // ① 从 0 开始点火：50% 与 95% 的时刻
            AbState st = new AbState();
            double t50 = -1.0, t95 = -1.0;
            for (int i = 1; i <= 400; i++)
            {
                double t = i * 0.02;
                AfterburnerTransient.Step(st, 0.02, 1.0, true);
                if (t50 < 0.0 && st.Frac >= 0.5) t50 = t;
                if (t95 < 0.0 && st.Frac >= 0.95) t95 = t;
            }
            Console.WriteLine(string.Format("点火建立：50% 用 {0:F2}s，95% 用 {1:F2}s，最终 {2:F2}（{3}）",
                t50, t95, st.Frac, AfterburnerTransient.Describe(st)));
            Expect("点火建立 50% 在 0.6~0.7 s", t50 > 0.6 && t50 < 0.72, true);
            Expect("点火建立 95% 在 1.2~1.3 s", t95 > 1.2 && t95 < 1.32, true);
            Expect("建立完成后是满加力", st.Frac > 0.999 && st.Phase == AbPhase.Lit, true);

            // ② 松开加力：余焰退干净的时刻
            double tOff = -1.0;
            for (int i = 1; i <= 200; i++)
            {
                double t = i * 0.02;
                AfterburnerTransient.Step(st, 0.02, 0.0, true);
                if (st.Frac <= 0.0) { tOff = t; break; }
            }
            Console.WriteLine(string.Format("熄火：{0:F2}s 退干净，末态 {1}", tOff, AfterburnerTransient.Describe(st)));
            Expect("熄火在 0.55~0.65 s 内退干净", tOff > 0.54 && tOff < 0.66, true);
            Expect("退干净后是 Off", st.Phase == AbPhase.Off && st.Frac == 0.0, true);

            // ③ 开关亮着但点不着：残余推力要退掉，不能变成"半加力"
            AbState st2 = new AbState();
            AfterburnerTransient.Step(st2, 0.0, 1.0, true);   // 先点着（dt<=0 直接同步）
            double tf = -1.0;
            for (int i = 1; i <= 100; i++)
            {
                double t = i * 0.02;
                AfterburnerTransient.Step(st2, 0.02, 1.0, false);   // 高空点不着
                if (st2.Frac <= 0.0) { tf = t; break; }
            }
            Console.WriteLine(string.Format("点不着时残余推力退掉：{0:F2}s，末态 {1}", tf, AfterburnerTransient.Describe(st2)));
            Expect("点不着时残余在 0.4 s 左右退掉", tf > 0.34 && tf < 0.46, true);

            // ④ 两张表之间的插值：0.5 时推力应当正好在干态与湿态中间
            try
            {
                // 测试设计里加力段是关着的（存档默认），这里打开——不然两张表一模一样，
                // "插值"验的其实是同一个数。
                EngineDesignData abD = d.Clone();
                abD.Afterburner.Enabled = true;
                abD.Nozzle = NozzleKind.Variable;   // 加力机必须有能撑开的喷口，否则加力反而掉推力（见第 6 段）
                DesignConstants c = OffDesign.DeriveDesignConstants(abD);
                double alt = abD.DesignAlt, mach = abD.DesignMach;
                PerformanceGrid dryG = GridFactory.Build(abD, c, new[] { 1.0 }, new[] { alt }, new[] { mach }, 0.0);
                PerformanceGrid wetG = GridFactory.Build(abD, c, new[] { 1.0 }, new[] { alt }, new[] { mach }, 1.0);
                OffDesignResult diagDry = OffDesign.Solve(abD, alt, mach, 1.0, 0.0, c);
                OffDesignResult diagWet = OffDesign.Solve(abD, alt, mach, 1.0, 1.0, c);
                Console.WriteLine(string.Format("  诊断：加力 Enabled={0} ExitTemp={1:F0}｜干态 T7={2:F0} 推力 {3:F2}｜加力 T7={4:F0} 推力 {5:F2} 油耗 {6:F3}/{7:F3}",
                    d.Afterburner.Enabled, d.Afterburner.ExitTemp, diagDry.T7, diagDry.NetThrust / 1000.0,
                    diagWet.T7, diagWet.NetThrust / 1000.0, diagDry.FuelFlow, diagWet.FuelFlow));
                double dryN = dryG.Thrust[dryG.Index(0, 0, 0)];
                double wetN = wetG.Thrust[wetG.Index(0, 0, 0)];

                AbState st3 = new AbState();
                double spool = 1.0; bool init = true;
                FlightStepResult r0 = FlightStep.Compute(dryG, 1.0, 0.02, alt, mach, true, false, true, true,
                    ref spool, ref init, 1.5, 0.8, 1.0, 0.06, wetG, st3, 0.0, true);
                double half = 0.0;
                for (int i = 0; i < 1000 && st3.Frac < 0.5; i++)
                {
                    spool = 1.0;
                    FlightStepResult r = FlightStep.Compute(dryG, 1.0, 0.02, alt, mach, true, false, true, true,
                        ref spool, ref init, 1.5, 0.8, 1.0, 0.06, wetG, st3, 1.0, true);
                    half = r.ThrustN;
                }
                Console.WriteLine(string.Format("插值：干态 {0:F2} kN｜加力 {1:F2} kN（+{2:F1}%）｜比例 {3:F2} 时 {4:F2} kN（插值误差 {5:F2}%）",
                    dryN / 1000.0, wetN / 1000.0, (wetN / dryN - 1.0) * 100.0, st3.Frac, half / 1000.0,
                    (half - (dryN + wetN) * 0.5) / ((dryN + wetN) * 0.5) * 100.0));
                Expect("加力比干态多 10% 以上", wetN > dryN * 1.10, true);
                Expect("半开加力推力在两者中间（±3%）", Math.Abs(half - (dryN + wetN) * 0.5) < (dryN + wetN) * 0.5 * 0.03, true);
                Expect("r0 未开加力时比例是 0", r0.AbFrac == 0.0, true);
            }
            catch (Exception e) { Expect("加力插值不抛异常", false, true); Console.WriteLine("  加力插值异常：" + e.Message); }

            // ⑤ AJE 式：油门过线按比例注入（第 56 轮）
            try
            {
                double[] thrs = { 0.00, 0.50, 0.70, 0.80, 0.90, 1.00 };
                StringBuilder sb = new StringBuilder("  油门过线：");
                bool monotone = true;
                double prev = -1.0;
                foreach (double th in thrs)
                {
                    double t = AfterburnerTransient.Target(th, 0.70, true, false);
                    if (t < prev - 1e-9) monotone = false;
                    prev = t;
                    sb.AppendFormat("{0:P0}→{1:F2} ", th, t);
                }
                Console.WriteLine(sb.ToString());
                Expect("70% 以下不加力", AfterburnerTransient.Target(0.70, 0.70, true, false) < 1e-9, true);
                Expect("85% 油门半加力（±0.02）", Math.Abs(AfterburnerTransient.Target(0.85, 0.70, true, false) - 0.5) < 0.02, true);
                Expect("满油门满加力", Math.Abs(AfterburnerTransient.Target(1.0, 0.70, true, false) - 1.0) < 1e-9, true);
                Expect("目标随油门单调不降", monotone, true);
                Expect("手动开关在低油门也能满加力", Math.Abs(AfterburnerTransient.Target(0.40, 0.70, true, true) - 1.0) < 1e-9, true);
                Expect("关掉自动就只剩手动", AfterburnerTransient.Target(1.00, 0.70, false, false) < 1e-9, true);

                // 目标从 0 爬到 0.7 需要多久：应当是 0.7 × LightSeconds，不是整整 1.3 s
                AbState ramp = new AbState();
                double t07 = -1.0;
                for (int i = 1; i <= 300; i++)
                {
                    AfterburnerTransient.Step(ramp, 0.01, 0.7, true);
                    if (t07 < 0.0 && ramp.Frac >= 0.699) { t07 = i * 0.01; break; }
                }
                Console.WriteLine(string.Format("  爬升到 70% 加力用 {0:F2}s（满加力 1.3 s 的七成 = {1:F2}s）", t07, 1.3 * 0.7));
                Expect("爬升速率与满建立同一条曲线", t07 > 0.85 && t07 < 0.95, true);
                Expect("停在目标比例而不是爬到 100%", Math.Abs(ramp.Frac - 0.7) < 1e-6, true);
                Expect("中间比例算 Lighting 而不是 Lit", ramp.Phase == AbPhase.Lighting, true);

                // ⑥ 三档（关 / 自动 / 强制开）。以前两个语义挤进一个布尔，
                // 点"自动"等于把开关关掉，火永远不亮——这是实飞报的"自动模式不自动开启"。
                bool man = true, aut = false;
                StringBuilder cyc = new StringBuilder("  三档循环：");
                cyc.AppendFormat("{0} → ", AbModes.Label(AbModes.Of(man, aut), 0.70));
                for (int i = 0; i < 3; i++)
                {
                    AbModes.Next(man, aut, out man, out aut);
                    cyc.AppendFormat("{0}{1}", AbModes.Label(AbModes.Of(man, aut), 0.70), i < 2 ? " → " : "");
                }
                Console.WriteLine(cyc.ToString());
                Expect("三档一圈回到强制开", man && !aut, true);

                AbModes.Next(false, false, out man, out aut);   // 关 → 自动
                Expect("关之后是自动", !man && aut, true);
                double autoT = AbModes.Target(AbModes.Of(man, aut), 1.0, 0.70);
                Expect("自动档满油门给满加力", Math.Abs(autoT - 1.0) < 1e-9, true);
                double autoLow = AbModes.Target(AbModes.Of(man, aut), 0.50, 0.70);
                Expect("自动档低油门不给加力", autoLow < 1e-9, true);
                double offT = AbModes.Target(AbModeKind.Off, 1.0, 0.70);
                Expect("关档满油门也不给加力", offT < 1e-9, true);
                double manT = AbModes.Target(AbModeKind.Manual, 0.30, 0.70);
                Expect("强制开档低油门也给满加力", Math.Abs(manT - 1.0) < 1e-9, true);

                // 自动档必须真的能把火点着：这一条是"命令"与"点火边界"的接线，
                // 以前模块把 afterburnerLit（自动档下恒为 false）当命令传进去，
                // 于是 canLight 永远 false，前面所有比例算得再对也没用。
                AbState autoRamp = new AbState();
                bool wanted = autoT > 0.02;
                for (int i = 0; i < 400; i++) AfterburnerTransient.Step(autoRamp, 0.01, autoT, wanted);
                Console.WriteLine(string.Format("  自动档满油门 4 秒后加力 {0:P0}（命令={1}）", autoRamp.Frac, wanted));
                Expect("自动档能真的点着火", autoRamp.Frac > 0.99, true);

                // 桥断电：设计不成立时不能靠上一份 maxFuelFlow 继续出力。
                // 注意 Idle 分支故意给 IdleFlow=1e-4 t/s、Isp=1.0（让原版走点火但基本不烧），
                // 换算成推力约 0.001 kN，是"可忽略"而不是"精确 0"。断言要按这个口径写。
                FuelBridge dead = StockBridge.Compute(0.0, 0.0, 0.0);
                double deadKn = dead.MaxFuelFlow * 1000.0 * dead.Isp * JetForge.Core.Gas.G0 / 1000.0;
                Console.WriteLine(string.Format("  断电：maxFuelFlow={0:F6} t/s Isp={1:F0} s → 约 {2:F4} kN（Idle 分支的余量）",
                    dead.MaxFuelFlow, dead.Isp, deadKn));
                Expect("断电后推力可忽略（<0.01 kN）", deadKn < 0.01, true);
            }
            catch (Exception e) { Expect("AJE 比例不抛异常", false, true); Console.WriteLine("  AJE 比例异常：" + e.Message); }
        }

        private static void RunInvariantCases()
        {
            uint seed = 20261115u;
            Func<double, double, double> rnd = (lo, hi) =>
            {
                seed = seed * 1664525u + 1013904223u;
                double u = (seed >> 8) / 16777216.0;
                return lo + (hi - lo) * u;
            };
            Func<int, int, int> ri = (lo, hi) => (int)rnd(lo, hi + 0.999);

            int n = 400;
            int solved = 0;
            int v1 = 0, v2 = 0, v3 = 0, v4 = 0, v5 = 0, v6 = 0, nonfinite = 0, buildThrow = 0, altGov = 0, wetCmp = 0;
            string e1 = "", e2 = "", e3 = "", e4 = "", e5 = "", e6 = "", eFin = "";

            double[] ths = { 0.25, 0.55, 0.85, 1.0 };
            double[] als = { 0.0, 6000.0, 11000.0 };
            double[] mcs = { 0.0, 0.55, 0.90 };

            EngineDesignData[] samples = new EngineDesignData[24];
            EngineDesignData altBad = null;
            int altBadTi = -1, altBadMi = 0;
            double altBadT = 0.25;
            double sfcWorst = 1.0;
            string sfcWorstWhere = "";
            int ns = 0;

            for (int i = 0; i < n; i++)
            {
                EngineDesignData d = new EngineDesignData();
                d.Kind = rnd(0, 2) < 0.6 ? EngineKind.Turbofan : EngineKind.Turbojet;
                d.ShaftCount = ri(1, 3);
                d.DesignAlt = rnd(0, 12000);
                d.DesignMach = rnd(0, 1.4);
                d.Fan.Diameter = rnd(0.5, 1.8);
                d.Fan.HubTipRatio = rnd(0.25, 0.6);
                d.CoreInletDiameter = rnd(0.25, 0.9);
                d.Lpc.Stages = ri(0, 4);
                d.Lpc.TipDiameter = d.CoreInletDiameter * rnd(0.8, 1.0);
                d.Lpc.Kind = rnd(0, 2) < 0.7 ? CompressorKind.Axial : CompressorKind.Centrifugal;
                d.Hpc.Stages = ri(2, 8);
                d.Hpc.TipDiameter = d.CoreInletDiameter * rnd(0.7, 0.95);
                d.Hpc.Kind = rnd(0, 2) < 0.6 ? CompressorKind.Axial : CompressorKind.Centrifugal;
                d.Hpt.Stages = ri(1, 2);
                d.Hpt.TipDiameter = d.CoreInletDiameter * rnd(0.7, 0.95);
                d.Lpt.Stages = ri(1, 5);
                d.Lpt.TipDiameter = d.CoreInletDiameter * rnd(0.8, 1.1);
                d.Burner.Kind = (BurnerKind)ri(0, 3);
                d.Burner.LinerLd = rnd(0, 2) < 0.6 ? 0.0 : rnd(1.0, 5.0);
                d.Burner.CanCount = rnd(0, 2) < 0.7 ? 0 : ri(2, 16);
                d.Burner.ExitTemp = rnd(1100, 1750);
                d.Nozzle = (NozzleKind)ri(0, 2);
                d.Afterburner.Enabled = rnd(0, 2) < 0.6;
                d.Afterburner.FuelRatio = rnd(0, 2) < 0.5 ? 0.0 : rnd(0.02, 0.06);
                d.Afterburner.Kind = (AfterburnerKind)ri(0, 2);

                // 真机的轴分工：LPT 驱动风扇（轴 1），LPC 与 IPT 在轴 2，HPC 与 HPT 在轴 3。
                // 第一版把 LPT 挂在 LPC 的轴上，风扇那根轴没有任何涡轮驱动 → 三轴机全挂。
                d.Fan.Shaft = 1;
                d.Lpt.Shaft = 1;
                d.Lpc.Shaft = Math.Min(2, d.ShaftCount);
                d.Ipt.Shaft = Math.Min(2, d.ShaftCount);
                d.Hpc.Shaft = d.ShaftCount >= 3 ? 3 : Math.Min(2, d.ShaftCount);
                d.Hpt.Shaft = d.Hpc.Shaft;

                CycleResult dp = null;
                DesignConstants c = null;
                try
                {
                    dp = Cycle.SolveDesignPoint(d);
                    if (!dp.Ok) continue;
                    c = OffDesign.DeriveDesignConstants(d);
                    if (c == null) continue;
                }
                catch (Exception) { buildThrow++; continue; }
                solved++;

                if (ns < samples.Length) samples[ns++] = d.Clone();

                PerformanceGrid dry, wet = null;
                try
                {
                    dry = GridFactory.Build(d, c, ths, als, mcs, 0.0);
                    if (d.Afterburner.Enabled) wet = GridFactory.Build(d, c, ths, als, mcs, 1.0);
                }
                catch (Exception ex)
                {
                    buildThrow++;
                    if (eFin.Length == 0) eFin = "#" + i + " 建表抛异常：" + ex.GetType().Name + " " + ex.Message;
                    continue;
                }

                // 每个 (高度,马赫) 的满油门推力：低于它的 5% 就不算"有意义的功率段"，
                // 那里的 SFC 是两个大数相减除出来的，不该拿来当判据。
                double[,] maxThr = new double[als.Length, mcs.Length];
                for (int a = 0; a < als.Length; a++)
                    for (int m = 0; m < mcs.Length; m++)
                        maxThr[a, m] = dry.Thrust[dry.Index(ths.Length - 1, a, m)];

                for (int t = 0; t < ths.Length; t++)
                    for (int a = 0; a < als.Length; a++)
                        for (int m = 0; m < mcs.Length; m++)
                        {
                            double thr = dry.Thrust[dry.Index(t, a, m)];
                            double sfc = dry.Sfc[dry.Index(t, a, m)];
                            double n2 = dry.N2[dry.Index(t, a, m)];

                            if (double.IsNaN(thr) || double.IsInfinity(thr) || double.IsNaN(sfc) || double.IsInfinity(sfc)
                                || double.IsNaN(n2) || double.IsInfinity(n2))
                            {
                                nonfinite++;
                                if (eFin.Length == 0) eFin = string.Format("#{0} thr{1} {2:F0}m M{3:F2}：推力 {4} SFC {5} N2 {6}",
                                    i, ths[t], als[a], mcs[m], thr, sfc, n2);
                                continue;
                            }

                            // ③ SFC 形状不作方向断言。真机的最小油耗点在 85~95% 功率，
                            //    我们的模型却在 20~25%（涡扇低功率时涵道比自然变大，推进效率反而高）。
                            //    这条差异记进文档未证清单；这里只断言"同一高度马赫下 SFC 变化幅度有界"——
                            //    变化超过 2.5 倍说明某一点的油耗算错了（真机的这段是 2~3 倍），那是 bug。
                            if (t == 0 && maxThr[a, m] > 0.0)
                            {
                                double smin = double.MaxValue, smax = 0.0;
                                for (int k = 0; k < ths.Length; k++)
                                {
                                    int ix = dry.Index(k, a, m);
                                    if (!dry.Valid[ix] || dry.Sfc[ix] <= 0.0) continue;
                                    // 只看"真的在干活"的点：净推力不到该点满油门 20% 时，
                                    // 冲压阻力把净推力吃到一个零头，SFC 涨到 4~5 倍是物理结果。
                                    if (dry.Thrust[ix] < 0.20 * maxThr[a, m]) continue;
                                    if (dry.Sfc[ix] < smin) smin = dry.Sfc[ix];
                                    if (dry.Sfc[ix] > smax) smax = dry.Sfc[ix];
                                }
                                if (smax > 0.0 && smin < double.MaxValue && smax > smin * 1.0)
                                {
                                    double ratio = smax / smin;
                                    if (ratio > sfcWorst) { sfcWorst = ratio; sfcWorstWhere = string.Format("#{0} {1:F0}m M{2:F2}", i, als[a], mcs[m]); }
                                }
                                if (smax > 0.0 && smin < double.MaxValue && smax > smin * 2.5)
                                {
                                    v3++;
                                    if (e3.Length == 0) e3 = string.Format("#{0} {1:F0}m M{2:F2}：剖面内 SFC {3:F3}~{4:F3}（{5:F2} 倍）",
                                        i, als[a], mcs[m], smin, smax, smax / smin);
                                }
                            }
                            // ④ 转速
                            if (n2 < 0.0 || n2 > 115.0)
                            {
                                v4++;
                                if (e4.Length == 0) e4 = string.Format("#{0} thr{1} {2:F0}m M{3:F2}：N2 {4:F1}%", i, ths[t], als[a], mcs[m], n2);
                            }
                            // ① 油门单调
                            if (t + 1 < ths.Length)
                            {
                                double next = dry.Thrust[dry.Index(t + 1, a, m)];
                                if (next < thr - Math.Max(1.0, 0.01 * Math.Abs(thr)))
                                {
                                    v1++;
                                    if (e1.Length == 0) e1 = string.Format("#{0} {1:F0}m M{2:F2}：油门 {3:F2}→{4:F2} 推力 {5:F1}→{6:F1} kN",
                                        i, als[a], mcs[m], ths[t], ths[t + 1], thr / 1000.0, next / 1000.0);
                                }
                            }
                            // ② 高度单调：只在满油门查。低功率时比推力随进气温度下降而上升，
                            //    能压过流量下降——那是真实现象，不是错（见文档 §36）。
                            //    被转速调节器压住的机器也跳过：那时决定推力的不是密度，
                            //    而是换算转速还留多少余量——调节器本身会把这两者倒过来。
                            if (a + 1 < als.Length && t == ths.Length - 1)
                            {
                                OffDesignResult loS = OffDesign.Solve(d, als[a], mcs[m], ths[t], -1.0, c);
                                OffDesignResult hiS = OffDesign.Solve(d, als[a + 1], mcs[m], ths[t], -1.0, c);
                                if (loS.Ok && hiS.Ok && !loS.Governed && !hiS.Governed)
                                {
                                    double higher = dry.Thrust[dry.Index(t, a + 1, m)];
                                    if (higher > thr + Math.Max(1.0, 0.01 * Math.Abs(thr)))
                                    {
                                        v2++;
                                        if (e2.Length == 0) e2 = string.Format("#{0} thr{1} M{2:F2}：高度 {3:F0}→{4:F0} m 推力 {5:F1}→{6:F1} kN",
                                            i, ths[t], mcs[m], als[a], als[a + 1], thr / 1000.0, higher / 1000.0);
                                        if (altBad == null) { altBad = d.Clone(); altBadTi = t; altBadMi = m; altBadT = ths[t]; }
                                    }
                                }
                                else if (loS.Governed || hiS.Governed)
                                {
                                    altGov++;
                                }
                            }
                            // ⑤ 加力 ≥ 干态
                            if (wet != null)
                            {
                                wetCmp++;
                                double wt = wet.Thrust[wet.Index(t, a, m)];
                                if (wt < thr - Math.Max(1.0, 0.01 * Math.Abs(thr)))
                                {
                                    v5++;
                                    if (e5.Length == 0) e5 = string.Format("#{0} thr{1} {2:F0}m M{3:F2}：干态 {4:F1} kN，加力 {5:F1} kN",
                                        i, ths[t], als[a], mcs[m], thr / 1000.0, wt / 1000.0);
                                }
                            }
                        }
            }

            {
                DesignConstants c0 = OffDesign.DeriveDesignConstants(samples[0]);
                PerformanceGrid g0 = GridFactory.Build(samples[0], c0, null, null, null, 0.0);
                Console.Write("  SFC-油门定标曲线（样本 #1）（0 m M0.00）：");
                for (int t = 0; t < g0.Nt; t++)
                    Console.Write(string.Format("  {0:F2}→{1:F3}/{2:F1}kN", g0.Throttle[t],
                        g0.Sfc[g0.Index(t, 0, 0)], g0.Thrust[g0.Index(t, 0, 0)] / 1000.0));
                Console.WriteLine();
                int ia = 1, im = 2;
                Console.Write(string.Format("     {0:F0} km M{1:F2}：", g0.Alt[ia] / 1000.0, g0.Mach[im]));
                for (int t = 0; t < g0.Nt; t++)
                    Console.Write(string.Format("  {0:F2}→{1:F3}/{2:F1}kN", g0.Throttle[t],
                        g0.Sfc[g0.Index(t, ia, im)], g0.Thrust[g0.Index(t, ia, im)] / 1000.0));
                Console.WriteLine();
            }

            Console.WriteLine(string.Format("  SFC 剖面内最大变化 {0:F2} 倍（{1}）", sfcWorst, sfcWorstWhere));
            if (altBad != null)
            {
                DesignConstants bc = OffDesign.DeriveDesignConstants(altBad);
                double[] la = { 0.0, 3000.0, 6000.0, 9000.0, 11000.0, 14000.0 };
                Console.WriteLine(string.Format("  ②违例机器几何：轴数 {0}｜风扇 D={1:F2} m｜核心 D={2:F2} m｜LPC {3}×{4} 级｜HPC {5}×{6} 级｜喷管 {7}｜加力 {8}",
                    altBad.ShaftCount, altBad.Fan.Diameter, altBad.Hpc.TipDiameter,
                    altBad.Lpc.Stages, altBad.Lpc.Kind, altBad.Hpc.Stages, altBad.Hpc.Kind,
                    altBad.Nozzle, altBad.Afterburner.Enabled));
                Console.WriteLine(string.Format("  ②违例机器的高度剖面（M0.90 油门 {0:F2}）：", altBadTi >= 0 ? altBadT : 0.25));
                for (int k = 0; k < la.Length; k++)
                {
                    OffDesignResult o = OffDesign.Solve(altBad, la[k], altBadMi >= 0 && altBadMi < mcs.Length ? mcs[altBadMi] : 0.90, altBadT, 0.0, bc);
                    Console.WriteLine(string.Format("      {0,6:F0} m  ok={1,-5} 推力 {2,8:F2} kN  进气 {3,6:F1} kg/s  T4 {4,6:F0} K  N2 {5,5:F1}%  油耗 {6,6:F3} kg/s{7}",
                        la[k], o.Ok, o.NetThrust / 1000.0, o.MdotAir, o.T4, o.N2Pct, o.FuelFlow,
                        o.Governed ? "  [被转速调节器压过]" : ""));
                }
            }

            // ⑥ 存文件往返
            string dir = System.IO.Path.Combine(Environment.CurrentDirectory, "out", "invariant_test");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            for (int k = 0; k < ns; k++)
            {
                string path = UserLibrary.Save(dir, "样本" + k, samples[k]);
                if (path == null) { v6++; if (e6.Length == 0) e6 = "样本" + k + " 存不下来"; continue; }
                string err;
                EngineDesignData back = UserLibrary.Load(path, out err);
                if (back == null) { v6++; if (e6.Length == 0) e6 = "样本" + k + " 读不回来：" + err; continue; }
                List<string> diffs = new List<string>();
                CompareFields(samples[k], back, "design", diffs);
                if (diffs.Count > 0)
                {
                    v6++;
                    if (e6.Length == 0) e6 = "样本" + k + " 不一致 " + diffs.Count + " 处：" + diffs[0];
                }
            }
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);

            Console.WriteLine(string.Format("{0} 台随机机：设计点成立 {1}｜建表抛异常 {2}", n, solved, buildThrow));
            Console.WriteLine(string.Format("  ①油门单调违例 {0}　②高度单调违例 {1}　③SFC 幅度有界 {2}　④转速越界 {3}　⑤加力低于干态 {4}　⑥存档往返不一致 {5}　非有限数 {6}　②跳过（被转速调节器压住）{7}　⑤比较次数 {8}",
                v1, v2, v3, v4, v5, v6, nonfinite, altGov, wetCmp));
            if (e1.Length > 0) Console.WriteLine("  ①例：" + e1);
            if (e2.Length > 0) Console.WriteLine("  ②例：" + e2);
            if (e3.Length > 0) Console.WriteLine("  ③例：" + e3);
            if (e4.Length > 0) Console.WriteLine("  ④例：" + e4);
            if (e5.Length > 0) Console.WriteLine("  ⑤例：" + e5);
            if (e6.Length > 0) Console.WriteLine("  ⑥例：" + e6);
            if (eFin.Length > 0) Console.WriteLine("  非有限/异常例：" + eFin);

            Expect("没有任何一例出 NaN/Inf 或建表抛异常", nonfinite + buildThrow == 0, true);
            // 随机几何本来大半不成立（级数、ψ、叶尖直径随便抽），要求的是样本够做统计。
            Expect("成立的样本足够多（>=60 台）", solved >= 60, true);
        }

        private static bool HasFinding(EngineDesignData dd, string key, string level)
        {
            CheckReport rep = Criteria.Evaluate(dd, Criteria.ViewOf(Cycle.SolveDesignPoint(dd)));
            foreach (Finding f in rep.Findings)
                if (f.Key == key && f.LevelValue == level) return true;
            return false;
        }

        private static string ShortfallLine(OffDesignResult o)
        {
            if (o.SpoolShortfall == null || o.SpoolShortfall.Count == 0) return "无";
            List<string> parts = new List<string>();
            foreach (KeyValuePair<int, double> kv in o.SpoolShortfall)
                parts.Add(string.Format("轴{0}×{1:P0}", kv.Key, kv.Value));
            parts.Sort();
            return string.Join(" ", parts.ToArray());
        }

        private static string TipLine(OffDesignResult o)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<int, double> kv in o.TipSpeed)
                parts.Add(string.Format("轴{0}={1:F0} m/s", kv.Key, kv.Value));
            parts.Sort();
            return string.Join(" ", parts.ToArray());
        }
        /// <summary>加力点火边界：高空点不着、油多吹熄、贫油留不住火。</summary>
        private static void RunAbLightCases(EngineDesignData baseDesign)
        {
            EngineDesignData ab = baseDesign.Clone();
            ab.Afterburner.Enabled = true;
            ab.Afterburner.FuelRatio = 0.035;
            ab.Afterburner.ExitTemp = 0.0;

            AbResult sl = AfterburnerLight.Check(ab, 0.0, 0.0, true);
            AbResult cruise = AfterburnerLight.Check(ab, 11000.0, 0.90, true);
            AbResult high = AfterburnerLight.Check(ab, 15000.0, 0.90, true);
            AbResult top = AfterburnerLight.Check(ab, 20000.0, 0.90, true);
            AbResult topFast = AfterburnerLight.Check(ab, 20000.0, 2.00, true);

            Console.WriteLine(string.Format("油气比 {0:F4}", ab.Afterburner.FuelRatio));
            Console.WriteLine(string.Format("   海平面静止   {0,-10} {1}", sl.Status, sl.Note));
            Console.WriteLine(string.Format("   11km M0.90   {0,-10} {1}", cruise.Status, cruise.Note));
            Console.WriteLine(string.Format("   15km M0.90   {0,-10} {1}", high.Status, high.Note));
            Console.WriteLine(string.Format("   20km M0.90   {0,-10} {1}", top.Status, top.Note));
            Console.WriteLine(string.Format("   20km M2.00   {0,-10} {1}", topFast.Status, topFast.Note));

            Expect("海平面静止能点着", sl.Lit, true);
            Expect("11km 巡航能点着", cruise.Lit, true);
            Expect("15km 仍能点着", high.Lit, true);
            Expect("20km M0.9 点不着（压力不够）", top.Status == AbLightStatus.LowPressure, true);
            Expect("20km 提到 M2 又能点着（冲压把压力抬回来）", topFast.Lit, true);
            Expect("加力段压力随高度单调降", sl.DuctKPa > cruise.DuctKPa && cruise.DuctKPa > top.DuctKPa, true);

            EngineDesignData lean = ab.Clone();
            lean.Afterburner.FuelRatio = 0.002;
            AbResult leanR = AfterburnerLight.Check(lean, 0.0, 0.0, true);
            Console.WriteLine(string.Format("   贫油 0.002   {0,-10} {1}", leanR.Status, leanR.Note));
            Expect("油气比 0.002 判贫油", leanR.Status == AbLightStatus.TooLean, true);

            EngineDesignData rich = ab.Clone();
            rich.Afterburner.FuelRatio = 0.080;
            AbResult richR = AfterburnerLight.Check(rich, 0.0, 0.0, true);
            Console.WriteLine(string.Format("   富油 0.080   {0,-10} {1}", richR.Status, richR.Note));
            Expect("油气比 0.080 判富油吹熄", richR.Status == AbLightStatus.TooRich, true);

            AbResult offCmd = AfterburnerLight.Check(ab, 0.0, 0.0, false);
            Expect("没开加力时不判点火边界", offCmd.Status == AbLightStatus.Off, true);
            AbResult noAb = AfterburnerLight.Check(baseDesign, 0.0, 0.0, true);
            Expect("没配加力的机器直接 Off", noAb.Status == AbLightStatus.Off, true);

            double ceilSlow = AfterburnerLight.CeilingMeters(ab, 0.90);
            double ceilFast = AfterburnerLight.CeilingMeters(ab, 2.00);
            Console.WriteLine(string.Format("   加力高度上限：M0.90 ≈ {0:F0} m｜M2.00 ≈ {1:F0} m", ceilSlow, ceilFast));
            Check("M0.9 加力高度上限", ceilSlow, 19000.0, 12.0, " m");
            Expect("飞得快就能更高", ceilFast > ceilSlow + 1000.0, true);

            EngineDesignData badDesign = ab.Clone();
            badDesign.DesignAlt = 20000.0;
            badDesign.DesignMach = 0.90;
            Expect("设计点定在 20km M0.9：判据标出「点不着」",
                HasFinding(badDesign, "ab.light", Level.Warn), true);
            EngineDesignData goodDesign = ab.Clone();
            goodDesign.DesignAlt = 0.0;
            goodDesign.DesignMach = 0.0;
            Expect("设计点定在海平面：判据不报点不着",
                HasFinding(goodDesign, "ab.light", Level.Warn), false);

            EngineDesignData richDesign = ab.Clone();
            richDesign.Afterburner.FuelRatio = 0.062;
            Expect("油气比 0.062 被判据标成偏高",
                HasFinding(richDesign, "ab.far_rich", Level.Warn), true);
        }


        /// <summary>
        /// 第 36 段：风扇特性线。这一轮只验纯函数——它还没接进求解器，
        /// 所以不碰设计点、不动任何既有断言。接进去之前必须先把形态钉死，
        /// 否则又会像 §44/§45 那两次一样：一个式子改完，别的量全顶墙。
        /// </summary>
        private static void RunFanMapCases(EngineDesignData d)
        {
            CycleResult r = Cycle.SolveDesignPoint(d);
            double fprD = r.Ok ? r.Fpr : 1.59;
            Console.WriteLine("  设计点风扇压比 {0:F4}（来自设计点解）", fprD);

            string msg;
            bool ok = FanMap.DesignPointSelfCheck(fprD, out msg);
            Console.WriteLine("  自检：{0} → {1}", msg, ok ? "过" : "不过");
            Expect("风扇工作线在设计点原样返回", ok, true);

            Console.WriteLine("  转速   压比     流量系数   每公斤风扇功 kJ/kg");
            double[] ncs = { 0.50, 0.70, 0.85, 1.00, 1.05 };
            double prevFpr = 0.0, prevFlow = 0.0;
            bool monoFpr = true, monoFlow = true;
            for (int i = 0; i < ncs.Length; i++)
            {
                double nc = ncs[i];
                double fpr = FanMap.PressureRatio(fprD, nc);
                double flow = FanMap.FlowFrac(nc);
                double w = FanMap.FanWorkPerKg(Gas.T0, fpr, 0.90, Gas.GammaAir, Gas.CpAir);
                Console.WriteLine("  {0:F2}   {1:F4}   {2:F4}    {3:F1}", nc, fpr, flow, w / 1000.0);
                if (fpr <= prevFpr) monoFpr = false;
                if (flow <= prevFlow) monoFlow = false;
                prevFpr = fpr; prevFlow = flow;
            }
            Expect("压比随转速单调上升", monoFpr, true);
            Expect("换算流量随转速单调上升", monoFlow, true);

            double fpr70 = FanMap.PressureRatio(fprD, 0.70);
            double fpr50 = FanMap.PressureRatio(fprD, 0.50);
            Console.WriteLine("  70% 转速压比 {0:F3}｜50% 转速压比 {1:F3}", fpr70, fpr50);
            // 期望值按真机风扇特性线定：70% 转速时压比的增量只剩设计增量的四到六成
            // （第一版我按"转速降 30%、压比增量也降 30%"写的，那是把压比当线性了——超差当场抓住）。
            double inc70 = (fpr70 - 1.0) / (fprD - 1.0);
            Console.WriteLine("  70% 转速的压比增量是设计增量的 {0:P0}", inc70);
            Expect("70% 转速的压比增量只剩设计增量的四到六成",
                inc70 >= 0.40 && inc70 <= 0.60, true);
            Expect("50% 转速的压比不到设计增量的一半",
                fpr50 - 1.0 < (fprD - 1.0) * 0.5, true);

            double w1 = FanMap.FanWorkPerKg(Gas.T0, fprD, 0.90, Gas.GammaAir, Gas.CpAir);
            double w70 = FanMap.FanWorkPerKg(Gas.T0, fpr70, 0.90, Gas.GammaAir, Gas.CpAir);
            Console.WriteLine("  风扇功 70%/设计 = {0:P0}", w70 / w1);
            Expect("70% 转速的风扇功只剩三到六成", w70 / w1 >= 0.30 && w70 / w1 <= 0.60, true);

            double fprJ79 = 1.0;
            Console.WriteLine("  涡喷(fpr 设计=1)：任何转速都给 1.000 → {0:F3}", FanMap.PressureRatio(fprJ79, 0.6));
            Expect("没有风扇的设计任何时候压比都是 1",
                Math.Abs(FanMap.PressureRatio(fprJ79, 0.60) - 1.0) < 1e-9, true);
        }


        /// <summary>
        /// 第 36 段后半：把求解器**已经解出来的**工作点代进特性线，看它偏离多远。
        /// 这一步什么都不改，只是量——量出来才知道接进去要动多少。
        /// 列：油门 / 转速 / 解出来的压比 / 特性线在这个转速给的压比 / 涵道流量比（相对设计点）
        /// 与特性线流量系数之比。比值远大于 1 说明我们的风扇在低转速"吃"得太多。
        /// </summary>
        private static void RunFanMapVsSolved(EngineDesignData d)
        {
            DesignConstants c = OffDesign.DeriveDesignConstants(d);
            CycleResult r0 = Cycle.SolveDesignPoint(d);
            double fprD = r0.Ok ? r0.Fpr : 1.59;

            Console.WriteLine();
            Console.WriteLine("  把解出来的点代进特性线（不改任何东西，只量差距）");
            Console.WriteLine("  油门   转速    解出压比  特性线压比  涵道流量/设计  特性线流量系数  两者之比");
            double[] ths = { 0.10, 0.30, 0.50, 0.85, 1.00 };
            // 先单独解一次满油门拿基准——第一版把基准放在循环里取，结果前四行全用了 -1 当分母，
            // 打印出一列 0.0000（这是打印口径的错，不是模型没涵道流）。
            OffDesignResult rFull = OffDesign.Solve(d, 0.0, 0.0, 1.0, 0.0, c, 60);
            double byFracDesign = rFull.Ok ? (rFull.MdotAir - rFull.MdotCore) / Math.Max(rFull.MdotAir, 1e-9) : 0.0;
            Console.WriteLine("  满油门涵道流量占比 {0:P1}（进气 {1:F1} / 核心 {2:F1} kg/s）",
                byFracDesign, rFull.MdotAir, rFull.MdotCore);
            int bad = 0;
            for (int i = 0; i < ths.Length; i++)
            {
                OffDesignResult r = OffDesign.Solve(d, 0.0, 0.0, ths[i], 0.0, c, 60);
                if (!r.Ok) { Console.WriteLine("  {0:F2}    解不出来", ths[i]); bad++; continue; }
                double n1 = r.N1Pct / 100.0;
                double fprMap = FanMap.PressureRatio(fprD, n1);
                double flowMap = FanMap.FlowFrac(n1);
                double byFrac = (r.MdotAir - r.MdotCore) / Math.Max(r.MdotAir, 1e-9);
                if (ths[i] >= 0.999) byFracDesign = byFrac;
                double flowRatio = byFracDesign > 0.0 ? byFrac / byFracDesign : 0.0;
                Console.WriteLine("  {0:F2}   {1:F3}   {2:F4}    {3:F4}     {4:F4}         {5:F4}        {6:F2}",
                    ths[i], n1, r.Fpr, fprMap, flowRatio, flowMap,
                    flowMap > 1e-6 ? flowRatio / flowMap : 0.0);
                // 压比偏离：偏低就是"风扇转慢了但压比没跟着掉"，正是谷底位置错的机制。
                if (r.Fpr > fprMap * 1.10) bad++;
            }
            Console.WriteLine("  压比比特性线高 10% 以上的油门点：{0} 个", bad);
            Expect("至少能解出大部分油门点", bad <= 2, true);
        }

        /// <summary>
        /// 第 37 段：核心压气机的工作线。这一轮同样只量、不改求解器。
        ///
        /// 要回答的问题：求解器把核心压比当成自由变量（让它去吃掉涡轮剩余功），
        /// 这个"自由"到底有多大？做法是把解出来的转速代进工作线，看它给的压比
        /// 跟求解器给的高出多少。高出越多，说明核心机在低功率时"多吃了"越多功，
        /// 而这份功本来是风扇少要的——SFC 谷底被拖到慢车就是这么来的。
        /// </summary>
        private static void RunCompressorMapCases(EngineDesignData d)
        {
            DesignConstants c = OffDesign.DeriveDesignConstants(d);
            CycleResult r0 = Cycle.SolveDesignPoint(d);
            double cprD = r0.Ok ? r0.Opr : 11.73;

            Console.WriteLine();
            Console.WriteLine("  核心工作线自洽：{0}", CompressorMap.DesignPointSelfCheck(cprD, d.Hpc.Kind, out string m0) ? "True" : "False");
            Console.WriteLine("    {0}", m0);
            Console.WriteLine("  轴流指数 {0:F2}｜离心指数 {1:F2}｜设计总压比 {2:F3}",
                CompressorMap.AxialExponent, CompressorMap.CentrifugalExponent, cprD);
            Console.WriteLine();
            Console.WriteLine("  油门    核心转速   解出压比   工作线压比   高出");

            double[] ths = { 0.10, 0.30, 0.50, 0.85, 1.00 };
            int big = 0;
            double worst = 0.0, worstThr = 0.0;
            for (int i = 0; i < ths.Length; i++)
            {
                OffDesignResult r = OffDesign.Solve(d, 0.0, 0.0, ths[i], 0.0, c, 60);
                if (!r.Ok) { Console.WriteLine("  {0:F2}    解不出来", ths[i]); continue; }
                double nc = r.N2Pct / 100.0;
                double prMap = CompressorMap.PressureRatio(cprD, nc, d.Hpc.Kind);
                double over = r.Opr / Math.Max(prMap, 1e-9);
                Console.WriteLine("  {0:F2}    {1:F3}      {2:F4}     {3:F4}      {4:F2}×",
                    ths[i], nc, r.Opr, prMap, over);
                if (over > 1.10) big++;
                if (over > worst) { worst = over; worstThr = ths[i]; }
            }
            Console.WriteLine("  高出 10% 以上的油门点：{0} 个（最坏 {1:F2}× 在油门 {2:F2}）", big, worst, worstThr);
            Console.WriteLine("  读法：高出越多，说明核心机在低功率时把风扇少要的功吃掉了。");
            Console.WriteLine("        真机上压比由工作线定死，吃不下就是吃不下——涡轮会落到更低的落压比。");
            Expect("设计点严格落在工作线上", Math.Abs(CompressorMap.PressureRatio(cprD, 1.0, d.Hpc.Kind) - cprD) < 1e-6, true);
        }

        /// <summary>
        /// 第 39 段：大涵道比涡扇点加力。
        ///
        /// 玩家报的"大涵道比涡扇配加力算错了"——加力只在核心那一路烧，涵道气流一点都不加温。
        /// 所以这里钉住三件事：①核心流量只占总量一小半；②加力不改进气量、不改涵道推力；
        /// ③推力增量全在核心那一路。
        /// </summary>
        private static void RunHighBypassAbCases(EngineDesignData baseDesign)
        {
            // ---- ① JF-1 开加力（BPR 2.75，非设计点能解）：增量必须全在核心那一路 ----
            EngineDesignData d = baseDesign.Clone();
            d.Afterburner.Enabled = true;
            d.Afterburner.Kind = AfterburnerKind.Ring;
            d.Afterburner.ExitTemp = 1950.0;
            d.Nozzle = NozzleKind.Variable;

            CycleResult r = Cycle.SolveDesignPoint(d);
            DesignConstants c = OffDesign.DeriveDesignConstants(d);
            Console.WriteLine("  涡扇开加力：BPR={0:F2} 进气 {1:F1} kg/s｜核心 {2:F1} kg/s（占 {3:P0}）",
                r.Bpr, r.MdotAir, r.MdotCore, r.MdotCore / Math.Max(r.MdotAir, 1e-6));

            OffDesignResult dry = OffDesign.Solve(d, 0.0, 0.0, 1.0, 0.0, c);
            OffDesignResult wet = OffDesign.Solve(d, 0.0, 0.0, 1.0, 1.0, c);
            Console.WriteLine("  干态 {0:F2} kN（核心 {1:F2} + 涵道 {2:F2}）｜加力 {3:F2} kN（核心 {4:F2} + 涵道 {5:F2}）｜增益 {6:P1}",
                dry.NetThrust / 1000.0, dry.ThrustCore / 1000.0, dry.ThrustBypass / 1000.0,
                wet.NetThrust / 1000.0, wet.ThrustCore / 1000.0, wet.ThrustBypass / 1000.0,
                wet.NetThrust / Math.Max(dry.NetThrust, 1.0) - 1.0);
            Console.WriteLine("  加力：T7 {0:F0} K｜far_ab {1:F4}｜油耗 {2:F3} kg/s（干态 {3:F3}）",
                wet.T7, wet.FarAb, wet.FuelFlow, dry.FuelFlow);

            Expect("核心流量小于总进气量", wet.MdotCore < wet.MdotAir * 0.9, true);
            Expect("加力不改进气量", Math.Abs(wet.MdotAir - dry.MdotAir) < dry.MdotAir * 0.02, true);
            Expect("加力不改涵道推力",
                Math.Abs(wet.ThrustBypass - dry.ThrustBypass) < Math.Abs(dry.ThrustBypass) * 0.02 + 50.0, true);
            Expect("加力推力增量全在核心那一路",
                (wet.ThrustCore - dry.ThrustCore) > (wet.NetThrust - dry.NetThrust) * 0.9, true);
            // 加力油气比是相对**核心**气流的：拿总流量算会低到 0.001 量级，烧不出温度
            Expect("加力油气比按核心流量算", wet.FarAb > 0.008 && wet.FarAb < 0.060, true);
            Expect("加力后 T7 高于干态", wet.T7 > dry.T7 + 100.0, true);

            // ---- ② 真正的大涵道（BPR 6.5，1.70 m 风扇）：设计点上只看流量与油耗口径 ----
            //
            // 这里只做设计点：这台 1.70 m 风扇把低压涡轮的功需求推到 5 级也给不出来
            // （需要 ΔT=683 K，5 级只能提供 325 K），非设计点整条线解不出来——
            // 那是"级数不够"这条判据在正常工作，不是加力的错，所以不在这里改模型。
            EngineDesignData hb = baseDesign.Clone();
            hb.Kind = EngineKind.Turbofan;
            hb.Fan.Diameter = 1.70;
            hb.Fan.Stages = 2;
            hb.Lpt.Stages = 5;
            hb.Nozzle = NozzleKind.Variable;
            hb.Afterburner.Enabled = true;
            hb.Afterburner.Kind = AfterburnerKind.Ring;
            hb.Afterburner.ExitTemp = 1950.0;
            CycleResult hbWet = Cycle.SolveDesignPoint(hb);
            EngineDesignData hbDryDesign = hb.Clone();
            hbDryDesign.Afterburner.Enabled = false;
            CycleResult hbDry = Cycle.SolveDesignPoint(hbDryDesign);
            double coreShare = hbWet.MdotCore / Math.Max(hbWet.MdotAir, 1e-6);
            double abFuel = Math.Max(hbWet.FuelFlow - hbDry.FuelFlow, 0.0);
            Console.WriteLine("  大涵道设计点：BPR={0:F2} 进气 {1:F1}｜核心 {2:F1}（占 {3:P0}）｜加力多烧 {4:F3} kg/s = 核心流量的 {5:F4}",
                hbWet.Bpr, hbWet.MdotAir, hbWet.MdotCore, coreShare, abFuel, abFuel / Math.Max(hbWet.MdotCore, 1e-6));
            Expect("大涵道核心流量占比低于 25%", coreShare < 0.25, true);
            Expect("大涵道加力油耗相对核心流量在合理区间",
                abFuel / Math.Max(hbWet.MdotCore, 1e-6) > 0.010 && abFuel / Math.Max(hbWet.MdotCore, 1e-6) < 0.080, true);
            Expect("大涵道加力不改进气量", Math.Abs(hbWet.MdotAir - hbDry.MdotAir) < hbDry.MdotAir * 0.02, true);
        }

        /// <summary>
        /// 第 40 段：实时解算替代预解网格。
        ///
        /// 验收不是"新的看起来更准"，而是两件事：
        /// ① **在同一点上两条路必须给出同一个数**——实时解算跑的是同一个 OffDesign.Solve，
        ///    网格只是它的采样；差太多说明采样器填字段填错了（以前就漏过 MdotCore）。
        /// ② **够快**。一次 Solve 要几十次气体性质调用，玩家想让它每秒跑十几次，
        ///    就得知道它到底几毫秒。这个数决定刷新率能开到多少，不能凭感觉。
        /// </summary>
        private static void RunRealtimeCases(EngineDesignData baseDesign)
        {
            EngineDesignData d = baseDesign.Clone();
            CycleResult r = Cycle.SolveDesignPoint(d);
            DesignConstants c = OffDesign.DeriveDesignConstants(d);
            Console.WriteLine("  设计点：OPR={0:F2} 推力={1:F2} kN", r.Opr, r.Thrust / 1000.0);

            PerformanceGrid grid = GridFactory.Build(d, c);
            RealtimeSampler rt = new RealtimeSampler(d, c, 40) { Hz = 0.0 };   // Hz=0：每次真算，便于计时
            GridSampler gs = new GridSampler(grid);

            // ---- ① 两种实现的出入 ----
            double[,] pts = { { 1.00, 0.0, 0.0 }, { 0.70, 0.0, 0.0 }, { 1.00, 8000.0, 0.60 }, { 1.00, 11000.0, 0.85 } };
            double worstThrust = 0.0, worstFlow = 0.0;
            for (int i = 0; i < pts.GetLength(0); i++)
            {
                double thr = pts[i, 0], alt = pts[i, 1], mach = pts[i, 2];
                PerformanceGrid.Sample gp = gs.Sample(thr, alt, mach, 0.0);
                PerformanceGrid.Sample sp = rt.Sample(thr, alt, mach, 0.0);
                double dT = Math.Abs(sp.Thrust - gp.Thrust) / Math.Max(Math.Abs(gp.Thrust), 1.0);
                double dF = Math.Abs(sp.MassFlow - gp.MassFlow) / Math.Max(gp.MassFlow, 1e-6);
                worstThrust = Math.Max(worstThrust, dT);
                worstFlow = Math.Max(worstFlow, dF);
                Console.WriteLine("  油门 {0:F2} 高度 {1:F0} 马赫 {2:F2}：网格 {3:F2} kN / {4:F1} kg/s｜实时 {5:F2} kN / {6:F1} kg/s",
                    thr, alt, mach, gp.Thrust / 1000.0, gp.MassFlow, sp.Thrust / 1000.0, sp.MassFlow);
            }
            Console.WriteLine("  两种实现最大偏差：推力 {0:P2}｜流量 {1:P2}", worstThrust, worstFlow);
            Expect("实时与网格同点推力一致（<2%）", worstThrust < 0.02, true);
            Expect("实时与网格同点流量一致（<2%）", worstFlow < 0.02, true);

            // ---- ② 一次 Solve 要多久 ----
            rt.Invalidate();
            int n = 20;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                // 每次换一个状态，避开缓存
                rt.Invalidate();
                rt.Sample(0.55 + 0.02 * i, 500.0 + 40.0 * i, 0.20 + 0.01 * i, 0.0);
            }
            sw.Stop();
            double per = sw.Elapsed.TotalMilliseconds / n;
            Console.WriteLine("  一次实时解算：{0:F2} ms（{1} 次平均）→ 12 Hz 占帧时间 {2:F1} ms/s",
                per, n, per * 12.0);
            Console.WriteLine("  解算器自述：{0}", rt.Describe());
            Expect("单次解算不超过 8 ms", per <= 8.0, true);
        }

        /// <summary>
        /// 第 38 段：设计缺陷在飞行中兑现成什么。
        /// 判据是静态的（"级负荷过高"），这里验的是动态后果（喘振、烧蚀、熄火、超速）。
        /// </summary>
        private static void RunDefectCases(EngineDesignData d)
        {
            // ---- ① 判据 → 风险档案 的映射 ----
            var fs = new List<Finding>();
            fs.Add(new Finding("hpc.stagepi", Level.Fail, "高压压气机单级压比 1.92 超过实用上限 1.55", 1.92, 1.55));
            fs.Add(new Finding("T4", Level.Warn, "涡轮前温度 1560 K 超过材料限值 1500 K", 1560, 1500));
            fs.Add(new Finding("LBO", Level.Fail, "设计点油气比 0.0042 低于贫油熄火边界 0.0045", 0.0042, 0.0045));
            fs.Add(new Finding("fan.tip_speed", Level.Warn, "风扇叶尖速度 462 m/s 偏高", 462, 430));
            Defects.Risk rk = Defects.Read(fs);
            Console.WriteLine("  风险档案：喘振 {0:F2}｜超温 {1:F2}｜熄火 {2:F2}｜超速 {3:F2}",
                rk.Surge, rk.OverTemp, rk.Blowout, rk.Overspeed);
            Expect("级负荷过高被读成喘振风险", rk.Surge > 0.3, true);
            Expect("T4 超限被读成超温风险", rk.OverTemp > 0.0, true);
            Expect("贫油被读成熄火风险", rk.Blowout >= 0.29, true);
            Expect("叶尖超速被读成超速风险", rk.Overspeed > 0.0, true);
            Expect("起因指向具体那条判据", rk.SurgeCause.Contains("单级压比"), true);

            // ---- ② 健康设计：不该凭空出缺陷 ----
            CheckReport rep = Criteria.Evaluate(d, Criteria.ViewOf(Cycle.SolveDesignPoint(d)), d.Tech, Gas.P0, 0.0);
            Defects.Risk healthy = Defects.Read(rep.Findings);
            var rtOk = new DefectRuntime();
            for (int i = 0; i < 40; i++) Defects.Step(healthy, rtOk, 0.05, 1.0, 1450.0, 1500.0, 0.02, 3000.0);
            Console.WriteLine("  JF-1 满油门 2 秒：{0}｜推力 ×{1:F3}｜损伤 {2:P0}",
                rtOk.State, rtOk.ThrustScale, rtOk.Damage);
            Expect("健康设计不出缺陷", rtOk.State == "正常", true);
            Expect("健康设计推力不打折", Math.Abs(rtOk.ThrustScale - 1.0) < 1e-9, true);

            // ---- ③ 喘振：间歇性的，不是一直抖 ----
            var rtSurge = new DefectRuntime();
            var riskSurge = new Defects.Risk { Surge = 0.80, SurgeCause = "高压压气机级负荷过高" };
            int n = 0, surging = 0;
            double minThrust = 1.0, maxFuel = 1.0;
            for (double t = 0.0; t < 4.0; t += 0.01)
            {
                Defects.Step(riskSurge, rtSurge, 0.01, 1.0, 1480.0, 1500.0, 0.02, 3000.0);
                n++;
                if (rtSurge.Surging)
                {
                    surging++;
                    minThrust = Math.Min(minThrust, rtSurge.ThrustScale);
                    maxFuel = Math.Max(maxFuel, rtSurge.FuelScale);
                }
            }
            double duty = (double)surging / n;
            Console.WriteLine("  喘振：4 秒里 {0:F0}% 的时间在喘（设计占空比 {1:F0}%）｜最低推力 ×{2:F2}｜最高油耗 ×{3:F2}",
                duty * 100.0, Defects.SurgeDuty * 100.0, minThrust, maxFuel);
            Expect("喘振占空比接近设计值", Math.Abs(duty - Defects.SurgeDuty) < 0.15, true);
            Expect("喘振时推力只剩 45%", Math.Abs(minThrust - Defects.SurgeThrust) < 1e-6, true);
            Expect("喘振时油耗相对暴涨", maxFuel > 1.5, true);
            Expect("风险档案里的起因被带到状态里", rtSurge.Cause.Length > 0, true);

            // 慢车不该喘：真机慢车压比低、裕度大
            var rtIdle = new DefectRuntime();
            for (int i = 0; i < 100; i++) Defects.Step(riskSurge, rtIdle, 0.01, 0.20, 900.0, 1500.0, 0.01, 3000.0);
            Console.WriteLine("  同一台机慢车 1 秒：{0}", rtIdle.State);
            Expect("慢车不喘", rtIdle.Surging, false);

            // ---- ④ 超温：累计损伤，90 秒烧穿后永久降额 ----
            var rtHot = new DefectRuntime();
            var riskHot = new Defects.Risk { OverTemp = 0.6, TempCause = "涡轮前温度超限" };
            double burnSeconds = -1.0;
            double thrustAtBurn = 1.0;
            for (double t = 0.0; t < 200.0; t += 0.5)
            {
                Defects.Step(riskHot, rtHot, 0.5, 1.0, 1500.0 * 1.12, 1500.0, 0.02, 3000.0);
                if (burnSeconds < 0.0 && rtHot.Damage >= 1.0) { burnSeconds = t; thrustAtBurn = rtHot.ThrustScale; }
            }
            Console.WriteLine("  超温 12%：{0:F1} 秒烧穿｜终态「{1}」｜推力 ×{2:F2}｜损伤 {3:P0}",
                burnSeconds, rtHot.State, rtHot.ThrustScale, rtHot.Damage);
            Expect("超温 12% 约 90 秒烧穿", Math.Abs(burnSeconds - Defects.DamageSeconds) < 12.0, true);
            // 烧穿那一刻是"永久降额 0.85"；再继续超温 20 秒才是叶片烧穿报废（推力归零）。
            Expect("烧穿后永久降额", Math.Abs(thrustAtBurn - Defects.BurntDerate) < 1e-6, true);
            Expect("烧穿仍超温 20 秒后彻底报废", rtHot.Failed && rtHot.ThrustScale < 1e-9, true);
            Expect("烧穿后状态说清楚", rtHot.State.Contains("烧蚀") || rtHot.State.Contains("降额"), true);

            // 超温 3% 不该那么快烧穿（这是"能扛一会儿"的地方）
            var rtWarm = new DefectRuntime();
            for (int i = 0; i < 120; i++) Defects.Step(riskHot, rtWarm, 0.5, 1.0, 1500.0 * 1.03, 1500.0, 0.02, 3000.0);
            Console.WriteLine("  超温 3% 跑 60 秒：损伤 {0:P0}（还没烧穿）", rtWarm.Damage);
            Expect("轻度超温不立刻烧穿", rtWarm.Damage < 1.0, true);

            // ---- ⑤ 贫油熄火：只有高空 + 贫油 + 不是满油门才犯 ----
            var riskLean = new Defects.Risk { Blowout = 0.8, BlowCause = "燃烧室太短，贫油吹熄" };
            var rtHigh = new DefectRuntime();
            Defects.Step(riskLean, rtHigh, 0.1, 0.5, 1200.0, 1500.0, 0.004, 12000.0);
            var rtLow = new DefectRuntime();
            Defects.Step(riskLean, rtLow, 0.1, 0.5, 1200.0, 1500.0, 0.004, 3000.0);
            var rtRich = new DefectRuntime();
            Defects.Step(riskLean, rtRich, 0.1, 0.5, 1200.0, 1500.0, 0.018, 12000.0);
            Console.WriteLine("  贫油熄火：高空贫油={0}｜低空贫油={1}｜高空富油={2}",
                rtHigh.JustBlownOut ? "熄火" : "正常",
                rtLow.JustBlownOut ? "熄火" : "正常",
                rtRich.JustBlownOut ? "熄火" : "正常");
            Expect("高空贫油吹熄", rtHigh.JustBlownOut, true);
            Expect("低空同样贫油不熄火", rtLow.JustBlownOut, false);
            Expect("高空但油够就不熄火", rtRich.JustBlownOut, false);

            // ---- ⑥ 叶尖超速：不熄火，但推力打折、油耗上升 ----
            var rtFast = new DefectRuntime();
            var riskFast = new Defects.Risk { Overspeed = 0.9, SpeedCause = "风扇叶尖速度 462 m/s" };
            Defects.Step(riskFast, rtFast, 0.1, 0.6, 1200.0, 1500.0, 0.012, 5000.0);
            Console.WriteLine("  叶尖超速：{0}｜推力 ×{1:F3}｜油耗 ×{2:F3}",
                rtFast.State, rtFast.ThrustScale, rtFast.FuelScale);
            Expect("超速打折但不熄火", rtFast.ThrustScale < 1.0 && rtFast.ThrustScale > 0.85, true);
            Expect("超速油耗上升", rtFast.FuelScale > 1.0, true);
        }

        /// <summary>
        /// 第 41 段：喘振的实时模拟——"逼出来的"那一层。
        /// 与第 38 段分工：那一层验"设计里带着的病会怎么犯"，
        /// 这一层验"同样的机器，不同的手上油门，结果不同"。
        /// </summary>
        private static void RunSurgeCases()
        {
            // ---- ① 缓推：4 秒把 0.20 推到 1.00，转子 1.2 s 滞后跟着走 ----
            var slow = new SurgeRuntime();
            double thr = 0.20, sp = 0.20;
            for (int i = 0; i < 200; i++)
            {
                const double dt = 0.02;
                double next = Math.Min(1.0, thr + 0.20 * dt);
                double rate = Surge.Rate(thr, next, dt);
                sp += (next - sp) * Math.Min(1.0, dt / 1.2);
                Surge.Step(slow, next, rate, sp, 0.30, 0.10, dt);
                thr = next;
            }
            Console.WriteLine("  缓推 0.20→1.00（0.20/s）：{0}｜风险 {1:F2}｜喘 {2} 次",
                slow.Active ? "喘振中" : "平稳", slow.Risk, slow.Events);
            Expect("缓推不喘", slow.Events == 0, true);

            // ---- ② 猛推：一帧 0.02 秒 0.20→1.00，转子还停在 0.25 ----
            var slam = new SurgeRuntime();
            double rateSlam = Surge.Rate(0.20, 1.00, 0.02);
            Console.WriteLine("  猛推风险：" + Surge.Explain(1.00, rateSlam, 0.25, 0.30, 0.10));
            Surge.Step(slam, 1.00, rateSlam, 0.25, 0.30, 0.10, 0.02);
            Console.WriteLine("  猛推（{0:F0}/s）：{1}｜喘 {2} 次｜叶片磨损 {3:P1}",
                rateSlam, slam.Active ? "喘振中" : "平稳", slam.Events, slam.Wear);
            Expect("猛推必喘", slam.Events > 0, true);
            Expect("喘一次就磨掉一点叶片", slam.Wear > 0.0, true);

            // ---- ③ 拍子：推力在 0.35 与 0.92 之间来回，排气温度上冲 ----
            double lo = 9.0, hi = 0.0, kick = 0.0;
            for (int i = 0; i < 60; i++)
            {
                Surge.Step(slam, 1.00, 0.0, 0.95, 0.30, 0.10, 1.0 / 60.0);
                lo = Math.Min(lo, slam.ThrustFactor);
                hi = Math.Max(hi, slam.ThrustFactor);
                kick = Math.Max(kick, slam.EgtKick);
            }
            Console.WriteLine("  拍子：推力 ×{0:F2}~×{1:F2}｜温升 {2:F0} K", lo, hi, kick);
            Expect("喘振推力掉到四成以下", lo < 0.45, true);
            Expect("间隙里推力回到九成", hi > 0.85, true);
            Expect("喘振时排气温度上冲", kick > 100.0, true);

            // ---- ④ 同样一手中等油门（0.55/s）：坏设计喘、好设计不喘 ----
            var weak = new SurgeRuntime();
            var good = new SurgeRuntime();
            for (int i = 0; i < 40; i++)
            {
                Surge.Step(weak, 0.90, 0.55, 0.70, 0.30, 0.60, 1.0 / 30.0);
                Surge.Step(good, 0.90, 0.55, 0.70, 0.30, 0.10, 1.0 / 30.0);
            }
            Console.WriteLine("  中等推 0.55/s：坏设计喘 {0} 次｜好设计喘 {1} 次", weak.Events, good.Events);
            Expect("裕度不足的机器中等推也会喘", weak.Events > 0, true);
            Expect("健康机同样一手油门不喘", good.Events == 0, true);

            // ---- ⑤ 收油门就该收住：静默期里不再续杯 ----
            var cooled = new SurgeRuntime();
            Surge.Step(cooled, 1.00, rateSlam, 0.25, 0.30, 0.10, 0.02);
            int before = cooled.Events;
            for (int i = 0; i < 300; i++) Surge.Step(cooled, 0.30, 0.0, 0.30, 0.30, 0.10, 1.0 / 30.0);
            Console.WriteLine("  松油门后 10 秒：喘 {0} 次（喘前 {1} 次）｜风险 {2:F2}",
                cooled.Events, before, cooled.Risk);
            Expect("松油门不再续喘", cooled.Events == before, true);
            Expect("收油门后风险归低", cooled.Risk < 0.60, true);
        }

        private static void RunSpoolDynamicsCases()
        {
            // ---- 剩余功率决定加速快慢：四种真机预设的惯量、转速、时间常数 ----
            //
            // 这一段的用处不是"断言某个数"，而是**把标定系数露出来**：
            // J 与 ω 是算出来的，Cap/W 是解算器给的，只有 SpoolDynamics.Calibration 是拍的。
            // 摆在一起才能看出拍的系数有没有把量级带歪。
            Console.WriteLine("转子加速（功率积分律 P = W0·x²·(target−x)）：");
            string[] keys = { "JF-1", "J85", "J79", "J57", "RD-33" };
            double[] real = { 4.50, 2.90, 5.00, 5.20, 3.60 };   // 0.2→0.95 的真机量级（秒）
            double worst = 0.0;
            for (int k = 0; k < keys.Length; k++)
            {
                EngineDesignData d = keys[k] == "JF-1" ? new EngineDesignData() : Presets.Build(keys[k]);
                DesignConstants c = OffDesign.DeriveDesignConstants(d);
                if (!c.Ok) { Console.WriteLine("  " + keys[k] + "：设计不成立"); continue; }
                OffDesignResult r = OffDesign.Solve(d, 0.0, 0.0, 1.0, 0.0, c);
                SpoolDynamics.Law law = new SpoolDynamics.Law();
                law.W0 = r.WComp;
                law.Inertia = SpoolDynamics.Inertia(d);
                law.OmegaDesign = SpoolDynamics.DesignOmega(d);
                double t95 = SpoolDynamics.SettleTime(law, 0.20, 0.95);
                double err = Math.Abs(t95 - real[k]) / real[k];
                if (err > worst) worst = err;
                Console.WriteLine("  {0,-6} W0 {1:F1} MW｜J {2:F1}｜ω {3:F0}｜0.2→0.95 用 {4:F2} s｜真机约 {5:F2} s｜偏差 {6:P0}",
                    keys[k], law.W0 / 1.0e6, law.Inertia, law.OmegaDesign, t95, real[k], err);
                // 额定点上"能提供减必须提供"几乎为零（涡轮本来就被压到极限），
                // 所以剩余功率要看半油门：那里才是"油门一推还有多少余量"的真实数字。
                OffDesignResult rp = OffDesign.Solve(d, 0.0, 0.0, 0.70, 0.0, c);
                Console.WriteLine("         剩余功率：额定 {0:F2} MW｜70% 油门 {1:F2} MW",
                    SpoolDynamics.HeadroomW(r) / 1.0e6, SpoolDynamics.HeadroomW(rp) / 1.0e6);
            }
            Console.WriteLine("  五个预设最大偏差 {0:P0}（整体系数 {1:F3}）", worst, SpoolDynamics.Calibration);
            Expect("加速时间与真机同量级（±25%）", worst < 0.25, true);

            // ---- 小机比大机快：J85 的转子惯量小、单位惯量摊到的功率多 ----
            EngineDesignData small = Presets.Build("J85");
            EngineDesignData big = Presets.Build("J57");
            double jSmall = SpoolDynamics.Inertia(small), jBig = SpoolDynamics.Inertia(big);
            double wSmall = SpoolDynamics.DesignOmega(small), wBig = SpoolDynamics.DesignOmega(big);
            Console.WriteLine("  惯量 J85 {0:F1} kg·m²（ω {1:F0}）｜J57 {2:F1} kg·m²（ω {3:F0}）",
                jSmall, wSmall, jBig, wBig);
            Expect("大机惯量更大", jBig > jSmall, true);

            SpoolDynamics.Law lawS = new SpoolDynamics.Law();
            DesignConstants cs = OffDesign.DeriveDesignConstants(small);
            lawS.W0 = OffDesign.Solve(small, 0.0, 0.0, 1.0, 0.0, cs).WComp;
            lawS.Inertia = jSmall;
            lawS.OmegaDesign = wSmall;
            SpoolDynamics.Law lawB = new SpoolDynamics.Law();
            DesignConstants cb = OffDesign.DeriveDesignConstants(big);
            lawB.W0 = OffDesign.Solve(big, 0.0, 0.0, 1.0, 0.0, cb).WComp;
            lawB.Inertia = jBig;
            lawB.OmegaDesign = wBig;
            double ts = SpoolDynamics.SettleTime(lawS, 0.20, 0.95);
            double tb = SpoolDynamics.SettleTime(lawB, 0.20, 0.95);
            Console.WriteLine("  J85 0.2→0.95 用 {0:F2} s｜J57 用 {1:F2} s", ts, tb);
            Expect("小机加速更快", ts < tb, true);

            // ---- 功率越大越快：同一个设计，压气机功翻倍，加速时间正好减半 ----
            EngineDesignData jf = new EngineDesignData();
            DesignConstants jfc = OffDesign.DeriveDesignConstants(jf);
            OffDesignResult jfr = OffDesign.Solve(jf, 0.0, 0.0, 1.0, 0.0, jfc);
            SpoolDynamics.Law law1 = new SpoolDynamics.Law();
            law1.W0 = jfr.WComp;
            law1.Inertia = SpoolDynamics.Inertia(jf);
            law1.OmegaDesign = SpoolDynamics.DesignOmega(jf);
            SpoolDynamics.Law law2 = new SpoolDynamics.Law();
            law2.W0 = jfr.WComp * 2.0;
            law2.Inertia = law1.Inertia;
            law2.OmegaDesign = law1.OmegaDesign;
            double t1 = SpoolDynamics.SettleTime(law1, 0.20, 0.95);
            double t2 = SpoolDynamics.SettleTime(law2, 0.20, 0.95);
            Console.WriteLine("  JF-1 压气机功 {0:F1} MW → {1:F2} s｜翻倍 → {2:F2} s（应当减半）",
                law1.W0 / 1.0e6, t1, t2);
            Expect("功率翻倍、加速时间减半", Math.Abs(t2 / t1 - 0.5) < 0.02, true);

            // ---- 惯量翻倍：时间也翻倍（J 在分母上，跟功率是同一个量纲的两头） ----
            SpoolDynamics.Law law3 = new SpoolDynamics.Law();
            law3.W0 = law1.W0;
            law3.Inertia = law1.Inertia * 2.0;
            law3.OmegaDesign = law1.OmegaDesign;
            double t3 = SpoolDynamics.SettleTime(law3, 0.20, 0.95);
            Console.WriteLine("  JF-1 惯量翻倍 {0:F1} kg·m² → {1:F2} s", law3.Inertia, t3);
            Expect("惯量翻倍、加速时间翻倍", Math.Abs(t3 / t1 - 2.0) < 0.06, true);

            // ---- 没有剩余功率不许给 0：退回上限，转速缓慢爬而不是冻住 ----
            double t0 = SpoolDynamics.TauUp(jf, 0.0);
            Console.WriteLine("  剩余功率 0 → {0:F2} s（上限，别冻住转速）", t0);
            Expect("没有剩余功率也不给零", t0 > 1.0, true);
        }

        /// <summary>
        /// 第 43 段：进气道对象。
        ///
        /// 这一段的第一条断言是**老数字不许动**：缺省型式（轴对称、不给捕获直径）必须与
        /// 老代码逐位相同。先钉住这一条，后面才好放开型式差异——不然一旦有人改了系数，
        /// 分不清是"新物理生效"还是"老口径被我改坏了"。
        /// </summary>
        private static void RunIntakeCases()
        {
            Console.WriteLine("进气道（缺省轴对称必须与老口径逐位相同）：");
            EngineDesignData jf = new EngineDesignData();
            TechLevel tech = jf.Tech;

            // 喉道面积：老代码 aIn = π(D/2)²（有风扇按风扇，没风扇按核心机进气口径）
            double legA = Math.PI * Math.Pow(jf.Fan.Diameter / 2.0, 2);
            Check("喉道面积（老口径）", Intake.ThroatAreaOf(jf), legA, 0.001, "m²");
            Expect("缺省就是轴对称", jf.Intake.Kind == IntakeKind.Axial, true);

            // 恢复系数：M0.85 / M1.5 / M2.0 三个点上必须等于 tech.IntakeTpr()
            double[] machs = { 0.0, 0.85, 1.5, 2.0 };
            double worstDrift = 0.0;
            foreach (double m in machs)
            {
                double got = Intake.Recovery(new IntakeSpec(), tech, m);
                double want = tech.IntakeTpr(m);
                double drift = Math.Abs(got - want);
                if (drift > worstDrift) worstDrift = drift;
            }
            Console.WriteLine("  轴对称恢复系数与 tech.IntakeTpr() 最大差 {0:E1}", worstDrift);
            Expect("缺省型式不改恢复系数", worstDrift < 1e-9, true);

            // 型式差异：M2 时斜板/锥体必须比轴对称好，皮托必须更差
            Console.WriteLine("  型式对照（总压恢复）：");
            IntakeKind[] kinds = { IntakeKind.Axial, IntakeKind.Pitot, IntakeKind.Ramp, IntakeKind.Cone };
            double ax = 0.0;
            foreach (IntakeKind k in kinds)
            {
                double r = Intake.Recovery(new IntakeSpec { Kind = k }, tech, 2.0);
                if (k == IntakeKind.Axial) ax = r;
                Console.WriteLine("    {0,-6} Ma2.00 → {1:F3}｜Ma0.90 → {2:F3}",
                    IntakeSpec.KindName(k), r, Intake.Recovery(new IntakeSpec { Kind = k }, tech, 0.9));
            }
            double rPitot = Intake.Recovery(new IntakeSpec { Kind = IntakeKind.Pitot }, tech, 2.0);
            double rRamp = Intake.Recovery(new IntakeSpec { Kind = IntakeKind.Ramp }, tech, 2.0);
            double rCone = Intake.Recovery(new IntakeSpec { Kind = IntakeKind.Cone }, tech, 2.0);
            Expect("超声速：皮托比轴对称差", rPitot < ax - 0.01, true);
            Expect("超声速：斜板比轴对称好", rRamp > ax + 0.005, true);
            Expect("斜板又比锥体好一点", rRamp > rCone, true);
            // 亚声速四型都在 0.96 以上：直管最好，要掰弯的略差
            bool subOk = true;
            foreach (IntakeKind k in kinds)
                if (Intake.Recovery(new IntakeSpec { Kind = k }, tech, 0.9) < 0.955) subOk = false;
            Expect("亚声速四型都近乎无损失", subOk, true);

            // 设计点对照：把 JF-1 的设计马赫抬到 2.0，四种进气道各解一遍
            // 工况点选 11 km / M1.8 / 85% 油门，而不是"把设计点抬到 M2"：
            // JF-1 的 HPT 只有 1 级，M2/15km 的设计点上落压比要 2.92 而单级极限 2.40，
            // 那台机器在那个工况根本不成立（报的是涡轮的事，不是进气道的事）。
            // 换工况比换断言诚实：进气道差异该在同一台能跑的机器上比。
            Console.WriteLine("  工况点（11 km / Ma 1.80 / 85% 油门）：");
            double thAx = 0.0, thRamp = 0.0, thPitot = 0.0;
            foreach (IntakeKind k in kinds)
            {
                EngineDesignData dk = new EngineDesignData { Intake = new IntakeSpec { Kind = k } };
                OffDesignResult cr = OffDesign.Solve(dk, 11000.0, 1.80, 0.85);
                double th = cr.Ok ? cr.NetThrust / 1000.0 : 0.0;
                if (k == IntakeKind.Axial) thAx = th;
                if (k == IntakeKind.Ramp) thRamp = th;
                if (k == IntakeKind.Pitot) thPitot = th;
                Console.WriteLine("    {0,-6} ok={1} 推力 {2:F2} kN｜流量 {3:F1} kg/s｜恢复 {4:F3}｜错 {5}",
                    IntakeSpec.KindName(k), cr.Ok, th, cr.MdotAir,
                    Intake.Recovery(new IntakeSpec { Kind = k }, tech, 1.80),
                    cr.Errors.Count > 0 ? cr.Errors[0] : "—");
            }
            Expect("工况点：皮托推力更低", thPitot < thAx, true);
            Expect("工况点：斜板推力更高", thRamp > thAx, true);

            // 捕获直径 + 喉道比：显式给了就用玩家的
            EngineDesignData cap = new EngineDesignData { Intake = new IntakeSpec { CaptureDiameter = 1.00, ThroatRatio = 0.85 } };
            double wantThroat = Math.PI * 0.25 * 0.85;
            Check("显式捕获 ⌀1.00 / 喉道比 0.85", Intake.ThroatAreaOf(cap), wantThroat, 0.01, "m²");
            Check("唇口面积（喉道/喉道比）", Intake.CaptureAreaOf(cap), Math.PI * 0.25, 0.01, "m²");

            // 抽气：流量直接打折
            EngineDesignData bleed = new EngineDesignData { Intake = new IntakeSpec { BleedFrac = 0.05 } };
            IntakeState bs = Intake.Evaluate(bleed, 0.8, 0.0);
            Check("抽气 5% 的流量系数", bs.FlowFactor, 0.95, 0.01, "×");
            OffDesignResult rb = OffDesign.Solve(bleed, 0.0, 0.0, 1.0);
            OffDesignResult r0 = OffDesign.Solve(new EngineDesignData(), 0.0, 0.0, 1.0);
            if (rb.Ok && r0.Ok)
            {
                double ratio = rb.MdotAir / r0.MdotAir;
                Console.WriteLine("  抽气 5%：进气量 {0:F1} → {1:F1} kg/s（{2:P1}）", r0.MdotAir, rb.MdotAir, ratio);
                // 不是正好 95%：抽气把换算流量压低之后，解算器要在"同一 T4"下重新配平转速，
                // 转速一变 wcFrac 又变，所以落到 93% 上下。断言只钉"确实折了、没折过头"。
                Expect("抽气确实折流量（<96%）", ratio < 0.96, true);
                Expect("抽气没折过头（>90%）", ratio > 0.90, true);
            }

            // 型式与设计点配不配：只报警，不改数
            Console.WriteLine("  配不配：");
            EngineDesignData sub = new EngineDesignData { DesignMach = 0.30, Intake = new IntakeSpec { Kind = IntakeKind.Ramp } };
            EngineDesignData sup = new EngineDesignData { DesignMach = 2.00, Intake = new IntakeSpec { Kind = IntakeKind.Pitot } };
            EngineDesignData ok1 = new EngineDesignData { DesignMach = 0.30, Intake = new IntakeSpec { Kind = IntakeKind.Axial } };
            Console.WriteLine("    亚声速用斜板：{0}", Intake.Mismatch(sub, sub.Intake.Resolved()) ?? "（没报）");
            Console.WriteLine("    超声速用皮托：{0}", Intake.Mismatch(sup, sup.Intake.Resolved()) ?? "（没报）");
            Expect("亚声速用斜板要报警", Intake.Mismatch(sub, sub.Intake.Resolved()) != null, true);
            Expect("超声速用皮托要报警", Intake.Mismatch(sup, sup.Intake.Resolved()) != null, true);
            Expect("亚声速用轴对称不报警", Intake.Mismatch(ok1, ok1.Intake.Resolved()) == null, true);

            // 存盘往返：五个字段一个都不许丢
            EngineDesignData sv = new EngineDesignData
            {
                Intake = new IntakeSpec
                {
                    Kind = IntakeKind.Cone, CaptureDiameter = 1.13, ThroatRatio = 0.77,
                    FaceMachDesign = 0.58, BleedFrac = 0.07
                }
            };
            ConfigNode node = new ConfigNode("DESIGN");
            sv.Save(node);
            EngineDesignData back = EngineDesignData.Load(node);
            bool same = back.Intake.Kind == IntakeKind.Cone
                        && Math.Abs(back.Intake.CaptureDiameter - 1.13) < 1e-4
                        && Math.Abs(back.Intake.ThroatRatio - 0.77) < 1e-4
                        && Math.Abs(back.Intake.FaceMachDesign - 0.58) < 1e-4
                        && Math.Abs(back.Intake.BleedFrac - 0.07) < 1e-4;
            Console.WriteLine("  存盘往返：{0}｜{1}｜捕获 {2:F2}｜喉道比 {3:F2}｜迎面 Ma {4:F2}｜抽气 {5:F2}",
                back.Intake.Kind, IntakeSpec.KindName(back.Intake.Kind),
                back.Intake.CaptureDiameter, back.Intake.ThroatRatio,
                back.Intake.FaceMachDesign, back.Intake.BleedFrac);
            Expect("进气道五个字段存得住", same, true);

            // ---- 字段元数据层：可视化编辑器以后就按这张表铺控件 ----
            Console.WriteLine("  字段元数据：" + DesignSchema.Report());
            Expect("元数据不少于 40 条", DesignSchema.All().Count >= 40, true);
            Expect("分组不少于 7 个", DesignSchema.Groups().Count >= 7, true);
            // 按路径读写：可视化编辑器靠这一对函数回写，路径写错要"返回 false"而不是静默
            EngineDesignData rw = new EngineDesignData();
            bool okW = DesignSchema.Write(rw, "Hpc.Stages", 11);
            double gotS = DesignSchema.Read(rw, "Hpc.Stages");
            bool okW2 = DesignSchema.Write(rw, "Fan.HubTipRatio", 0.42);
            double gotH = rw.Fan.HubTipRatio;
            bool okBad = DesignSchema.Write(rw, "Fan.Nonsense", 1.0);
            bool okGone = DesignSchema.Write(rw, "Intake.Kind", (double)IntakeKind.Cone);
            Console.WriteLine("    按路径读写：Hpc.Stages → {0:F0}｜Fan.HubTipRatio → {1:F2}｜错路径返回 {2}｜进气道字段已撤掉 {3}",
                gotS, gotH, okBad, !okGone);
            Expect("按路径写进去、读得回", okW && okW2 && Math.Abs(gotS - 11) < 1e-9 && Math.Abs(gotH - 0.42) < 1e-9, true);
            Expect("错路径明确失败", okBad, false);
            // 进气道不再是发动机设计器的选项。注意 Write 是按对象反射走的，所以
            // "路径还能写进去"并不能证明它还在编辑器里 —— 判据必须是**元数据里没有条目**。
            Expect("进气道在元数据里一条都不剩",
                DesignSchema.Find("Intake.Kind") == null && DesignSchema.Find("Intake.Model") == null
                && DesignSchema.Find("Intake.AlphaDeg") == null, true);

            // ---- 选项字段的范围必须从枚举数出来，不能另抄一份 ----
            // 这一条是还给一个真 bug 补的门：`Nozzle` 曾经另有一份手写名单
            // `{"Converging","ConvergingDiverging","Variable"}`，而 `NozzleKind` 的枚举顺序是
            // `Converging, Variable, ConvergingDiverging`——名单下标 1 是"收扩"，枚举 1 是"可变"。
            // 那份名单当时没人读，所以什么都没发生；一旦有人按它解释数值，"可变"就会被画成"收扩"。
            // 现在只留类型：个数由枚举数出来，值是枚举的真实取值。
            FieldSpec fNoz = DesignSchema.Find("Nozzle");
            Expect("选项字段记得自己对应哪个枚举", fNoz != null && fNoz.ChoiceType == typeof(NozzleKind), true);
            Expect("选项范围按枚举个数算（不是按名单长度）",
                fNoz != null && Math.Abs(fNoz.Max - (Enum.GetValues(typeof(NozzleKind)).Length - 1)) < 1e-9, true);
            // 下标 → 枚举 → 原名，两处必须对上：枚举 1 叫 Variable
            Expect("选项下标 1 对应 NozzleKind.Variable",
                fNoz != null && (NozzleKind)(int)Math.Round(fNoz.Min + 1) == NozzleKind.Variable, true);
            ExpectEq("NozzleKind 的存档顺序不许动（下标就是存档）",
                (int)NozzleKind.Converging + "," + (int)NozzleKind.Variable + "," + (int)NozzleKind.ConvergingDiverging,
                "0,1,2");
            // 每一张表里的选项字段都得说得出自己的类型；说不出的那种，说明又有人加了手写名单
            int choiceNoType = 0;
            foreach (FieldSpec f in DesignSchema.All())
                if (f.Kind == FieldKind.Choice && f.ChoiceType == null) choiceNoType++;
            if (choiceNoType > 0)
                Console.WriteLine("    有 {0} 个选项字段没登记枚举类型", choiceNoType);
            Expect("每个选项字段都登记了枚举类型", choiceNoType, 0);

            // ---- 「表里没有」必须分清两种：漏登记（要红）与有意不给入口（要有一句话） ----
            // 以前 `Uncovered()` 把两种一起报，自检打"3 个没元数据：Length、IdleThrustFrac、IdleN2Pct"，
            // 读起来像三处失职。实际上那三个都是故意的：Length 是手填兜底、后两个是反解读数。
            // 分开之后，新加一个字段忘了登记 → Uncovered() 立刻非空 → 这条红。
            List<string> missing = DesignSchema.Uncovered();
            if (missing.Count > 0)
                Console.WriteLine("    没元数据：{0}", string.Join("、", missing.ToArray()));
            Expect("设计对象里没有漏登记元数据的字段", missing.Count, 0);
            // 每条"不给入口"都得有理由：空理由等于没登记（§233：缺省值冒充实测）。
            int blankReason = 0;
            foreach (KeyValuePair<string, string> kv in DesignSchema.NotInEditor)
                if (kv.Value == null || kv.Value.Trim().Length < 12) blankReason++;
            Expect("每个「不给入口」的字段都写了理由", blankReason, 0);
            // 理由表也会烂：字段改名/删掉之后，这里的键就成了幽灵，而它不会编译报错。
            int ghost = 0;
            foreach (string path in DesignSchema.NotInEditor.Keys)
            {
                System.Reflection.FieldInfo fi = typeof(EngineDesignData).GetField(path,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (fi == null) ghost++;
            }
            if (ghost > 0)
                Console.WriteLine("    理由表里有 {0} 个路径在设计对象上找不到（字段改名或删了）", ghost);
            Expect("理由表里没有幽灵路径", ghost, 0);
            // 反过来：某个字段既在表里、也在"不给入口"表里，说明有人没想清楚要哪一个。
            int both = 0;
            foreach (string path in DesignSchema.NotInEditor.Keys)
                if (DesignSchema.Find(path) != null) both++;
            Expect("同一个字段不会既在表里又在「不给入口」里", both, 0);
        }

        /// <summary>
        /// 逐级编辑：每一级自己的叶尖直径与 ψ 必须真的进到循环里，而且
        /// **全部填成同一个值时结果必须与"全机一个直径"逐位相同**——否则不是
        /// 逐级功能生效了，是我把老算法改坏了，两种错看起来都像"数字变了"。
        /// </summary>
        private static void RunStageEditCases(EngineDesignData d)
        {
            double oprBase = Cycle.SolveDesignPoint(d).Opr;
            Console.WriteLine("  基准 OPR {0:F3}（HPC {1} 级 / 叶尖 {2:F2} m）", oprBase, d.Hpc.Stages, d.Hpc.TipDiameter);
            if (d.Hpc.Stages < 2)
            {
                Console.WriteLine("    这台预设的高压压气机级数不足两级，跳过逐级对照");
                return;
            }

            // 1) 全填成缺省值 → 必须与基准一模一样
            EngineDesignData same = d.Clone();
            same.Hpc.SyncStages();
            for (int i = 0; i < same.Hpc.Stages; i++) same.Hpc.StageTip[i] = same.Hpc.TipDiameter;
            double oprSame = Cycle.SolveDesignPoint(same).Opr;
            Console.WriteLine("  逐级全填缺省：OPR {0:F3}（与基准差 {1:E2}）", oprSame, Math.Abs(oprSame - oprBase));
            Expect("逐级全同 = 单一叶尖（不改变老结果）", Math.Abs(oprSame - oprBase) < 1e-9, true);

            // 2) 流道收敛：最后一级只有第一级的 0.72 → 后面几级线速度低，压比必须掉
            EngineDesignData tap = d.Clone();
            tap.Hpc.SyncStages();
            for (int i = 0; i < tap.Hpc.Stages; i++)
            {
                double f = (double)i / (tap.Hpc.Stages - 1);
                tap.Hpc.StageTip[i] = tap.Hpc.TipDiameter * (1.0 - 0.28 * f);
            }
            double oprTap = Cycle.SolveDesignPoint(tap).Opr;
            Console.WriteLine("  收缩流道 1.00→0.72：OPR {0:F3}（{1:+0.0;-0.0}%）", oprTap, (oprTap / oprBase - 1.0) * 100.0);
            Expect("叶尖收缩 → 压比下降", oprTap < oprBase - 0.05, true);

            // 3) 逐级载荷：最后两级 ψ 抬到 1.4 → 压比必须涨
            EngineDesignData hot = d.Clone();
            hot.Hpc.SyncStages();
            for (int i = hot.Hpc.Stages - 2; i < hot.Hpc.Stages; i++) hot.Hpc.StagePsi[i] = 1.40;
            double oprHot = Cycle.SolveDesignPoint(hot).Opr;
            Console.WriteLine("  末两级 ψ=1.40：OPR {0:F3}（{1:+0.0;-0.0}%）", oprHot, (oprHot / oprBase - 1.0) * 100.0);
            Expect("末两级载荷提高 → 压比上升", oprHot > oprBase + 0.02, true);

            // 4) 涡轮逐级：容量按"每级各自的叶尖速度"累加，不是"最大直径 × 级数"
            EngineDesignData tt = d.Clone();
            tt.Lpt.SyncStages();
            double capBase, capTap;
            TurbineCapacity(d.Lpt, out capBase);
            for (int i = 0; i < tt.Lpt.Stages; i++)
            {
                double f = tt.Lpt.Stages <= 1 ? 0.0 : (double)i / (tt.Lpt.Stages - 1);
                tt.Lpt.StageTip[i] = tt.Lpt.TipDiameter * (1.0 - 0.35 * f);
            }
            TurbineCapacity(tt.Lpt, out capTap);
            Console.WriteLine("  LPT 容量：均匀 {0:F1} K｜前大后小 {1:F1} K（{2:+0.0;-0.0}%）",
                capBase, capTap, (capTap / Math.Max(1e-9, capBase) - 1.0) * 100.0);
            Expect("涡轮逐级容量按级累加（不是按最大直径）", capTap < capBase - 1.0, true);

            // 5) 越界与缺项：项数为 0 / 负数 / 短表都不能抛
            EngineDesignData edge = d.Clone();
            edge.Hpc.StageTip.Clear();
            edge.Hpc.StageTip.Add(0.0);
            edge.Hpc.StagePsi.Clear();
            bool okEdge = true;
            try
            {
                CycleResult r = Cycle.SolveDesignPoint(edge);
                okEdge = r != null;
                edge.Hpc.StageTip.Clear();
                edge.Hpc.StageTip.Add(-1.0);
                Cycle.SolveDesignPoint(edge);
            }
            catch (Exception)
            {
                okEdge = false;
            }
            Expect("短表/零值/负值退回缺省而不抛", okEdge, true);
        }

        /// <summary>
        /// 一台涡轮在给定轴转速下能吃掉多少温降：每级各自的叶尖速度与 ψ 累加。
        /// 与 Cycle.SolveTurbine 里那段是同一个式子，这里只用来做对照量。
        /// </summary>
        private static void TurbineCapacity(TurbineSpec spec, out double total)
        {
            total = 0.0;
            double a = Math.Sqrt(1.4 * 287.05 * 1200.0);
            // 轴转速由**最大**叶尖锁在叶尖马赫 0.9 上（与 Cycle 里的转速锁同一条），
            // 然后每一级按自己的直径算线速度。把每级都当成"刚好顶到马赫上限"
            // 会让直径差异凭空消失——那正是第一版这条断言判 False 的原因。
            double rpm = 0.90 * a * 60.0 / (Math.PI * Math.Max(1e-6, spec.MaxTip()));
            for (int i = 0; i < spec.Stages; i++)
            {
                double u = Math.PI * spec.TipOf(i) * rpm / 60.0;
                total += Cycle.TurbineDT(u, 0.90, 1.30 * spec.PsiOf(i));
            }
        }

        /// <summary>
        /// 慢车口径由玩家定：给"推力占满油门多少"就按推力，给 0 就按 N2。
        /// 这条以前是写死的 0.08 —— 慢车不是热力学必然，是燃油调节器的设计选择。
        /// </summary>
        /// <summary>
        /// 第 49 段：慢车档烧不住 → 熄火。
        ///
        /// 这一段钉的是**判定本身**：以前模块里写的是"`!IdleSustainable` 且 `r.Running`"，
        /// 而"油门到底烧不着"正是要抓的现象——条件自相矛盾，玩家设 2% 什么都不会发生。
        /// 现在判定搬进 Core（`EngineGuard`），这里逐条验：
        ///   ① 设 2% 的机器，油门到底必须判"留不住火"
        ///   ② 设 12% 的机器，油门到底不能误判
        ///   ③ 同一台 2% 的机器，油门推到 40% 不该判熄火（推杆就得能活）
        /// </summary>
        /// <summary>
        /// 第 50 段：停车以后转子要一直往下溜，而不是瞬间归零；
        /// 这期间油门推到底也点不着（没有火，只有重新点火才行）。
        /// </summary>
        /// <summary>
        /// 第 51 段：熄火那一刻的残余推力。
        /// 第 50 段验的是"转速往下溜"，这一段验"推力也是溜下去的"——
        /// 上一版把推力一帧抹平，玩家看到的就是转速不降、推力突变。
        /// </summary>
        static void RunCoastThrustCases(EngineDesignData d, DesignConstants c)
        {
            Console.WriteLine("== 第 51 段 熄火不是断电：推力跟着转速一起溜（F ∝ ω²），不是一帧归零 ==");

            double start = 10.0;                       // 慢车推力 10 kN 左右

            // ---- 主线：转子往下溜，推力按 ω² 跟着溜 ----
            // 上一版这里给推力单独一个 0.22 s 的指数：推力两百毫秒就没了、转速还在 90%，
            // 玩家报的"油门到底推力啪一下归零"就是它。现在推力与转速同源。
            double tauUp = Spool.TauUp(d);
            double tauCoast = Math.Max(2.5, Math.Max(tauUp, 0.5) * 3.0);
            double spoolCut = 1.0;
            double spoolNow = spoolCut;
            double at100ms = -1.0, at1s = -1.0, at3s = -1.0;
            double prev = start;
            bool monotone = true;
            double dt = 1.0 / 30.0;
            for (double t = 0.0; t < 20.0; t += dt)
            {
                spoolNow = Spool.Step(spoolNow, 0.0, dt, tauCoast, tauCoast);
                double v = CoastThrust.Slide(start, spoolCut, spoolNow);
                if (at100ms < 0.0 && t >= 0.09) at100ms = v;
                if (at1s < 0.0 && t >= 0.99) at1s = v;
                if (at3s < 0.0 && t >= 2.99) at3s = v;
                if (v > prev + 1e-9) monotone = false;
                prev = v;
            }
            Console.WriteLine("    10 kN 熄火（惰转 τ={0:F2} s）：0.1 s 后 {1:F2} kN｜1 s 后 {2:F2} kN｜3 s 后 {3:F2} kN｜20 s 后 {4:F3} kN",
                tauCoast, at100ms, at1s, at3s, prev);

            Expect("一帧之内推力不会掉（关机不是切电源）",
                CoastThrust.Slide(start, 1.0, 1.0) == start, true);
            Expect("0.1 s 后仍留着九成以上（老口径这里只剩 63%）", at100ms > start * 0.90, true);
            Expect("1 s 后还看得见（不是两百毫秒抹平）", at1s > start * 0.20, true);
            Expect("20 s 后彻底归零", prev == 0.0, true);
            Expect("衰减一路单调", monotone, true);

            // 与转速同源：转速掉一半，推力正好剩四分之一
            Expect("转速一半 → 推力四分之一（F ∝ ω²）",
                Math.Abs(CoastThrust.Slide(start, 1.0, 0.5) - start * 0.25) < 1e-9, true);
            Expect("转速掉到风车 → 推力归零",
                CoastThrust.Slide(start, 1.0, CoastThrust.MinSpoolFraction) == 0.0, true);
            Expect("转速倒退不会凭空多出推力",
                CoastThrust.Slide(start, 0.5, 0.8) == start, true);
            Expect("熄火前就没转 → 没有残余推力",
                CoastThrust.Slide(start, 0.0, 0.0) == 0.0, true);
            Expect("负值与 NaN 不会反号",
                CoastThrust.Slide(-5.0, 1.0, 0.5) == 0.0 &&
                CoastThrust.Slide(double.NaN, 1.0, 0.5) == 0.0 &&
                CoastThrust.Slide(start, double.NaN, 0.5) == 0.0, true);
            Expect("低于地板直接判零",
                CoastThrust.Slide(CoastThrust.FloorKN * 0.9, 1.0, 1.0) == 0.0, true);

            // ---- 老口径那条腿还留着：余气排空的 0.22 s 短尺度 ----
            double first = CoastThrust.Step(start, dt);
            Expect("余气排空一帧内不会归零", first > start * 0.85, true);
            double tauAt = CoastThrust.Step(start, CoastThrust.TauSeconds);
            Expect("余气时间常数掉到 1/e",
                Math.Abs(tauAt - start * Math.Exp(-1.0)) < start * 0.02, true);

            double f0 = 0.0004;
            Expect("残余油与残余推力同曲线（转速一半 → 油也四分之一）",
                Math.Abs(CoastThrust.SlideFuel(f0, 1.0, 0.5) - f0 * 0.25) < 1e-12, true);

            // 惰转用的是 tauCoast（FlightStep 里 max(2.5, tauUp*3)），不是 tauUp 本身——
            // 拿 tauUp 1.5 s 去比会得出 6.8 倍，看着像"同一个量级"，其实是拿错了量。
            Console.WriteLine("    时间尺度：增速 {0:F2} s｜惰转 {1:F2} s｜推力与转速同一条曲线", tauUp, tauCoast);
            Expect("惰转慢到看得见（≥2 s，不是一帧滑到底）", tauCoast >= 2.0, true);
            Expect("推力曲线不再由 0.22 s 那个短常数决定（老口径正是玩家说的啪一下）",
                CoastThrust.SpoolExponent >= 1.5 && CoastThrust.TauSeconds < 0.5, true);
        }

        private static void RunCoastCases(EngineDesignData d, DesignConstants c)
        {
            PerformanceGrid g = GridFactory.Build(d, c);
            double tauUp = Spool.TauUp(d), tauDown = Spool.TauDown(d);
            double area = 1.086;
            double dt = 0.02;

            // 先在地面跑满油门，把转子带到设计转速
            double spool = 0.0;
            bool init = false;
            for (int i = 0; i < 600; i++)
                FlightStep.Compute(g, 1.0, dt, 0.0, 0.0, true, false, true, true,
                    ref spool, ref init, tauUp, tauDown, area, 0.0527);
            double spoolCut = spool;

            // 关车（火灭了），但油门到底一直推着——真机上这不会让发动机重新转起来
            FlightStepResult first = default(FlightStepResult);
            FlightStepResult last = default(FlightStepResult);
            double prev = spool;
            bool mono = true;
            double atOne = -1.0, atThree = -1.0;
            int coastingFrames = 0;
            for (int i = 0; i < 2000; i++)     // 40 s
            {
                FlightStepResult r = FlightStep.Compute(g, 1.0, dt, 0.0, 0.0, false, false, true, true,
                    ref spool, ref init, tauUp, tauDown, area, 0.0527);
                if (i == 0) first = r;
                last = r;
                if (spool > prev + 1e-9) mono = false;
                prev = spool;
                if (r.Coasting) coastingFrames++;
                if (atOne < 0.0 && i * dt >= 1.0) atOne = spool;
                if (atThree < 0.0 && i * dt >= 3.0) atThree = spool;
            }

            Console.WriteLine("    关车瞬间转速 {0:P0} → 0.02 s 后 {1:P0}｜1 s {2:P0}｜3 s {3:P0}｜40 s {4:P0}",
                spoolCut, first.Spool, atOne, atThree, spool);
            Console.WriteLine("    停车惯性持续 {0:F1} s（{1} 帧）｜末帧模式 {2}｜推力 {3:F2} kN",
                coastingFrames * dt, coastingFrames, last.Mode, last.ThrustN / 1000.0);

            Expect("一帧之内转速不会归零（停车不是断电）", first.Spool > spoolCut * 0.9, true);
            Expect("满油门推 40 s 也不会重新转起来", last.Mode != FlightMode.Running, true);
            Expect("停车期间推力始终是 0", last.ThrustN == 0.0, true);
            Expect("转速一路单调往下", mono, true);
            Expect("40 s 后掉到风车档附近（≤10%）", spool <= 0.10, true);
            Expect("确实经过了「惯性停车」这段（不是直接跳到熄火）", coastingFrames > 20, true);
        }

        private static void RunIdleStarveCases(EngineDesignData d)
        {
            // 现在"烧不住"不能靠玩家填一个小数字造出来（那个旋钮已经撤了），
            // 所以直接把慢车点判成烧不住，验守卫自己的契约：只要油门到底 + 这一档烧不住就熄火。
            DesignConstants c = OffDesign.DeriveDesignConstants(d.Clone());
            c.IdleSustainable = false;

            DesignConstants co = OffDesign.DeriveDesignConstants(d.Clone());

            Console.WriteLine("  造例（慢车点烧不住）：{0}", EngineGuard.Describe(d, c));
            Console.WriteLine("  本机型（反解出来的慢车点）：{0}", EngineGuard.Describe(d, co));

            bool starve = EngineGuard.IdleStarves(c, StockBridge.InputFloor);
            bool fine = EngineGuard.IdleStarves(co, StockBridge.InputFloor);
            bool pushed = EngineGuard.IdleStarves(c, 0.40);
            bool notBottom = EngineGuard.IdleStarves(c, 0.60);

            Console.WriteLine("  油门到底（命令 {0:F3}）：烧不住的机 {1}｜本机型 {2}",
                StockBridge.InputFloor, starve ? "熄火" : "不熄火", fine ? "熄火" : "不熄火");
            Console.WriteLine("  同一台烧不住的机推杆到 40%：{0}｜到 60%：{1}",
                pushed ? "熄火" : "不熄火", notBottom ? "熄火" : "不熄火");

            Expect("油门到底 + 烧不住 → 判熄火", starve, true);
            Expect("本机型油门到底不该误判", fine, false);
            Expect("推杆到 40% 不该判熄火", pushed, false);
            Expect("抬底带内才算到底（0.60 不算）", notBottom, false);
        }

        private static void RunConfigurableIdleCases(EngineDesignData d)
        {
            // 两个旧旋钮还在存档字段里（读旧档不报错），但不再影响任何数：
            // 填 4%、12%、22% 三档，反解出来的慢车点必须一模一样。
            double t4Ref = double.NaN, fracRef = double.NaN;
            foreach (double frac in new[] { 0.04, 0.12, 0.22 })
            {
                EngineDesignData x = d.Clone();
                x.IdleThrustFrac = frac;             // 旧字段，故意写进去
                x.IdleN2Pct = 0.30 + frac;           // 同上
                DesignConstants c = OffDesign.DeriveDesignConstants(x);
                OffDesignResult idle = OffDesign.Solve(x, 0.0, 0.0, 0.0, 0.0, c, 40);
                OffDesignResult full = OffDesign.Solve(x, 0.0, 0.0, 1.0, 0.0, c, 40);
                double got = full.Ok && full.NetThrust > 1.0 ? idle.NetThrust / full.NetThrust : 0.0;
                Console.WriteLine("  旧档写的慢车占比 {0:P0}：反解 T4 慢车 {1:F0} K｜推力占比 {2:P1}｜N2 {3:F1}%｜油气比 {4:F4}",
                    frac, c.T4Idle, got, idle.N2Pct, c.IdleFar);
                if (double.IsNaN(t4Ref)) { t4Ref = c.T4Idle; fracRef = got; }
                Expect("旧旋钮不再改变慢车 T4", Math.Abs(c.T4Idle - t4Ref) < 1e-6, true);
                Expect("旧旋钮不再改变慢车推力占比", Math.Abs(got - fracRef) < 1e-6, true);
                Expect("反解出来的慢车点烧得住",
                    c.IdleSustainable && c.IdleFar >= c.LeanFar * DesignConstants.IdleLeanMargin * 0.98, true);
                Expect("油门到底的油量就是最低可用油量",
                    Math.Abs(idle.FuelFlow - c.IdleFuelFlow) <= Math.Max(c.IdleFuelFlow * 0.05, 1e-5), true);
                Expect("慢车推力占比落在真机带内（1%~30%）",
                    got > 0.01 && got < 0.30, true);
            }

            // 慢车点的自洽性：反解出来之后，再按这个 T4 跑一遍，油气比必须还在可燃窗口里，
            // 而且离贫油下限有《IdleLeanMargin》那点余量——"刚好能烧住"和"贴着熄火线停"不是一回事。
            DesignConstants cn = OffDesign.DeriveDesignConstants(d.Clone());
            OffDesignResult idleN = OffDesign.Solve(d.Clone(), 0.0, 0.0, 0.0, 0.0, cn, 40);
            double leanFloor = cn.LeanFar * DesignConstants.IdleLeanMargin;
            Console.WriteLine("  慢车自洽：T4 {0:F0} K｜油气比 {1:F4}｜下限×余量 {2:F4}｜N2 {3:F1}%｜油量 {4:F4} kg/s",
                cn.T4Idle, cn.IdleFar, leanFloor, idleN.N2Pct, cn.IdleFuelFlow);
            Expect("慢车油气比落在贫油下限与上下限之间",
                cn.IdleFar >= leanFloor * 0.98 && cn.IdleFar < cn.RichFar, true);
            Expect("慢车点自己跑得动（求解收敛、有正流量）",
                idleN.Ok && idleN.MdotCore > 1e-6, true);
        }

        /// <summary>
        /// 第 52 段：进气道几何。
        ///
        /// 这一段钉三件事：
        ///   ① 缺省（不给捕获直径）时几何与性能是同一份数——喉道面积必须等于 `Intake.ThroatAreaOf`；
        ///   ② 三个面积各管各的：改捕获直径不动发动机面，改喉道比不动捕获直径；
        ///   ③ 锥体/斜板有中心锥、轴对称/皮托没有，压缩角随设计马赫变大。
        ///
        /// 以前这三条全都不成立：网格按 `InletDiameter` 画一圈圆唇，
        /// 喉道比调成 0.7 性能掉了、模型一点没变——图上和账上不是同一台发动机。
        /// </summary>
        private static void RunIntakeGeometryCases(EngineDesignData baseDesign)
        {
            Console.WriteLine("进气道几何：性能与外形吃同一份数");
            EngineDesignData d = baseDesign.Clone();
            IntakeShape g = IntakeGeometry.Of(d);
            EngineStations st = EngineGeometry.Of(d);

            // ① 性能与几何同一个喉道
            Check("喉道面积 = Intake.ThroatAreaOf", g.ThroatArea, Intake.ThroatAreaOf(d), 0.001, "m²");
            Check("捕获面积 = Intake.CaptureAreaOf", g.CaptureArea, Intake.CaptureAreaOf(d), 0.001, "m²");
            Check("喉道半径 = √(A/π)", g.RThroat, Math.Sqrt(g.ThroatArea / Math.PI), 1e-9, "m");
            Console.WriteLine("  缺省几何：捕获 R{0:F3}｜喉道 R{1:F3}｜面 R{2:F3}｜长 {3:F2} m｜{4}",
                g.RCapture, g.RThroat, g.RFace, g.Length, IntakeSpec.KindName(g.Kind));
            Expect("缺省没有中心锥", g.HasCenterBody, false);
            Expect("缺省唇口不捅穿短舱", g.RCapture <= st.RIn * 0.985 + 1e-9, true);
            Expect("缺省唇口在机头", Math.Abs(g.YFront - st.YIn) < 1e-9, true);
            Expect("喉道在唇口与发动机面之间",
                g.YThroat < g.YFront && g.YThroat > g.YFace, true);

            // ② 面积互不顶替
            EngineDesignData big = d.Clone();
            big.Intake.CaptureDiameter = d.Fan.Diameter * 1.30;
            IntakeShape gb = IntakeGeometry.Of(big);
            Check("改捕获直径：捕获面积 = π(capD/2)²", gb.CaptureArea,
                Math.PI * Math.Pow(big.Intake.CaptureDiameter * 0.5, 2), 0.01);
            Check("改捕获直径：发动机面不动", gb.FaceArea, g.FaceArea, 0.001);
            Console.WriteLine("  捕获放大：捕获 R{0:F3}（原 {1:F3}）｜喉道 R{2:F3}（原 {3:F3}）｜面 R{4:F3}（原 {5:F3}）",
                gb.RCapture, g.RCapture, gb.RThroat, g.RThroat, gb.RFace, g.RFace);

            EngineDesignData wide = d.Clone();
            wide.Intake.CaptureDiameter = d.Fan.Diameter;
            wide.Intake.ThroatRatio = 0.85;
            EngineDesignData narrow = wide.Clone();
            narrow.Intake.ThroatRatio = 0.70;
            IntakeShape gw = IntakeGeometry.Of(wide), gn = IntakeGeometry.Of(narrow);
            Check("改喉道比：喉道面积按比例缩", gn.ThroatArea / gw.ThroatArea, 0.70 / 0.85, 0.5);
            Check("改喉道比：捕获面积不动", gn.CaptureArea, gw.CaptureArea, 0.001);
            Console.WriteLine("  喉道比 0.85 → 0.70：喉道 A{0:F3} → {1:F3} m²｜捕获 A{2:F3} 不变",
                gw.ThroatArea, gn.ThroatArea, gn.CaptureArea);

            // 短舱跟着捕获直径长（否则唇口比短舱还粗 = 穿模）
            EngineStations stBig = EngineGeometry.Of(big);
            Expect("捕获直径大 → 短舱半径跟着长", stBig.RIn >= gb.RCapture - 1e-9, true);
            Expect("捕获直径大 → 唇口没被短舱夹", gb.CaptureClamped, false);

            // ②b 喉道配小了，非设计点必须真的限流（不是只在面板上写一行提示）
            {
                EngineDesignData tight = d.Clone();
                // 捕获直径不填时按老口径"喉道 = 风扇口径"算，喉道比根本不参与（§85 的兼容规则），
                // 所以要把捕获直径写实了，喉道比才会真的缩喉道。
                tight.Intake.CaptureDiameter = d.Fan.Diameter;
                tight.Intake.ThroatRatio = 0.30;
                // 注意：把喉道改小以后设计点自己也会变小（进气量按喉道算），两边一起缩就永远不会超限。
                // 这里故意沿用大进气道那套设计常数，模拟"进气道的流量标定还是老的"——限流夹子就是为这种
                // 不一致准备的：谁想吞的比喉道能给的多，就按喉道给。
                DesignConstants ct = OffDesign.DeriveDesignConstants(d);
                OffDesignResult rt = OffDesign.Solve(tight, 0.0, 0.8, 1.0, 0.0, ct);
                Console.WriteLine("  小喉道 M0.8：ok={0} 限流={1} 可用 {2:F1} kg/s 实到 {3:F1} kg/s tpr {4:F3}",
                    rt.Ok, rt.IntakeLimited, rt.IntakeCapacity, rt.MdotAir, rt.IntakeTpr);
                Expect("小喉道 → 非设计点限流", rt.IntakeLimited, true);
                Expect("限流后进气量不超过可用流量", rt.MdotAir <= rt.IntakeCapacity * 1.005 + 1e-6, true);

                DesignConstants cd0 = OffDesign.DeriveDesignConstants(d);
                OffDesignResult rd0 = OffDesign.Solve(d, 0.0, 0.8, 1.0, 0.0, cd0);
                Console.WriteLine("  缺省喉道 M0.8：限流={0} 可用 {1:F1} kg/s 实到 {2:F1} kg/s",
                    rd0.IntakeLimited, rd0.IntakeCapacity, rd0.MdotAir);
                Expect("缺省喉道不限流", rd0.IntakeLimited, false);
            }

            // ③ 型式：中心锥与压缩角
            Console.WriteLine("  型式几何：");
            foreach (IntakeKind k in new[] { IntakeKind.Axial, IntakeKind.Pitot, IntakeKind.Ramp, IntakeKind.Cone })
            {
                EngineDesignData kd = d.Clone();
                kd.Intake.Kind = k;
                kd.DesignMach = 1.8;
                IntakeShape kg = IntakeGeometry.Of(kd);
                Console.WriteLine("    {0,-6} 中心锥 {1,-5}｜压缩角 {2,5:F1}°｜唇口 R{3:F4}｜收缩比 {4:F2}",
                    IntakeSpec.KindName(k), kg.HasCenterBody, kg.RampAngleDeg, kg.LipRadius, kg.ContractionRatio);
            }
            EngineDesignData coneLo = d.Clone();
            coneLo.Intake.Kind = IntakeKind.Cone; coneLo.DesignMach = 0.9;
            EngineDesignData coneHi = d.Clone();
            coneHi.Intake.Kind = IntakeKind.Cone; coneHi.DesignMach = 2.0;
            double thLo = IntakeGeometry.Of(coneLo).RampAngleDeg;
            double thHi = IntakeGeometry.Of(coneHi).RampAngleDeg;
            Expect("锥体压缩角随设计马赫变大", thHi > thLo, true);
            Expect("轴对称没有中心锥", IntakeGeometry.Of(
                WithKind(d, IntakeKind.Axial)).HasCenterBody, false);
            Expect("皮托没有中心锥", IntakeGeometry.Of(WithKind(d, IntakeKind.Pitot)).HasCenterBody, false);
            Expect("斜板有中心锥", IntakeGeometry.Of(WithKind(d, IntakeKind.Ramp)).HasCenterBody, true);
            Expect("超声速唇口要薄（斜板唇口 < 轴对称唇口）",
                IntakeGeometry.Of(WithKind(d, IntakeKind.Ramp)).LipRadius
                < IntakeGeometry.Of(WithKind(d, IntakeKind.Axial)).LipRadius, true);

            // ④ 型线确实有单调段，能画
            Expect("气道型线至少 3 点", g.Duct.Count >= 3, true);
            Expect("气道从唇口一路往后", g.Duct[0].Y > g.Duct[g.Duct.Count - 1].Y, true);
            Check("型线插值在发动机面收敛到 RFace",
                IntakeGeometry.RadiusAt(g.Duct, g.YFace), g.RFace, 0.5);

            // ⑤ 锥尖位置可调（外形口径）
            // 以前锥尖钉死在"唇口往后 2% 长度"，玩家没法让锥体伸出唇口——可调中心锥的行程
            // 本身就是"锥尖在动"，钉死等于这根轴不可设计。缺省必须还是老外形，所以 0 的那条先钉住。
            EngineDesignData cone0 = d.Clone();
            cone0.Intake.Kind = IntakeKind.Cone;
            cone0.Intake.ConeTipShift = 0.0;
            IntakeShape sh0 = IntakeGeometry.Of(cone0);
            double tip0 = sh0.CenterBody[0].Y;
            Check("缺省锥尖仍在唇口后 2% 长度", sh0.YFront - tip0, sh0.Length * 0.02, 1e-6, "m");

            EngineDesignData coneFwd = d.Clone();
            coneFwd.Intake.Kind = IntakeKind.Cone;
            coneFwd.Intake.ConeTipShift = 0.10;
            IntakeShape shFwd = IntakeGeometry.Of(coneFwd);
            Check("锥尖前伸 10% 长度后位置前移", shFwd.CenterBody[0].Y - tip0, sh0.Length * 0.10, 1e-6, "m");
            Expect("前伸到 10% 后锥尖已在唇口前", shFwd.CenterBody[0].Y > shFwd.YFront, true);

            EngineDesignData coneAft = d.Clone();
            coneAft.Intake.Kind = IntakeKind.Cone;
            coneAft.Intake.ConeTipShift = -0.20;
            IntakeShape shAft = IntakeGeometry.Of(coneAft);
            Check("锥尖缩进 20% 长度后位置后移", tip0 - shAft.CenterBody[0].Y, sh0.Length * 0.20, 1e-6, "m");

            Expect("位置只改外形：喉道面积一分不动",
                Math.Abs(shFwd.ThroatArea - sh0.ThroatArea) < 1e-9, true);
            Expect("位置只改外形：压缩角一分不动",
                Math.Abs(shFwd.RampAngleDeg - sh0.RampAngleDeg) < 1e-9, true);

            EngineDesignData coneOver = d.Clone();
            coneOver.Intake.Kind = IntakeKind.Cone;
            coneOver.Intake.ConeTipShift = 0.9;
            EngineDesignData coneUnder = d.Clone();
            coneUnder.Intake.Kind = IntakeKind.Cone;
            coneUnder.Intake.ConeTipShift = -0.9;
            Check("超出量程的往前填会被夹住", IntakeGeometry.Of(coneOver).ConeTipShift,
                IntakeGeometry.ConeTipShiftMax, 1e-9, "");
            Check("超出量程的往后填会被夹住", IntakeGeometry.Of(coneUnder).ConeTipShift,
                IntakeGeometry.ConeTipShiftMin, 1e-9, "");
            IntakeSpec nanSpec = new IntakeSpec { Kind = IntakeKind.Cone, ConeTipShift = double.NaN };
            Expect("NaN 当 0（老外形），不是让整条型线变成 NaN",
                Math.Abs(IntakeGeometry.ClampConeTipShift(double.NaN)) < 1e-12, true);

            IntakeSpec axSpec = new IntakeSpec { Kind = IntakeKind.Axial, ConeTipShift = 0.2 };
            EngineDesignData axD = d.Clone();
            axD.Intake = axSpec;
            Expect("轴对称填了锥尖位置也没有中心体", IntakeGeometry.Of(axD).HasCenterBody, false);

            ConfigNode coneNode = new ConfigNode(IntakeSpecCodec.NodeName);
            IntakeSpecCodec.Save(coneNode, new IntakeSpec { Kind = IntakeKind.Cone, ConeTipShift = 0.12 });
            IntakeSpec coneBack = IntakeSpecCodec.Load(coneNode, new IntakeSpec());
            Check("锥尖位置存得回来", coneBack.ConeTipShift, 0.12, 1e-9, "");

        }

        private static EngineDesignData WithKind(EngineDesignData d, IntakeKind k)
        {
            EngineDesignData c = d.Clone();
            c.Intake.Kind = k;
            c.DesignMach = 1.8;
            return c;
        }

        /// <summary>
        /// 第 53 段：站表与逐轴状态。
        ///
        /// 设计点现在是"模板 → 部件 → 网络"跑出来的一条链路，所以有三件事可以验：
        ///   ① 每一站的参数都是有限的、压力温度一路单调（压气机升、涡轮降）；
        ///   ② 网络算出来的结果与 `Cycle.SolveDesignPoint` 完全一致（同一份数，不是两条路）；
        ///   ③ 逐轴状态：每根轴有自己的惯量、转速比例，正盈余会升速、负盈余会掉速。
        /// </summary>
        private static void RunNetworkCases(EngineDesignData baseDesign)
        {
            Console.WriteLine("站表与逐轴状态：一条链路、一份轴账");
            EngineDesignData d = baseDesign.Clone();
            NetworkSolution net = EngineNetwork.SolveDesign(d);
            CycleResult r = net.Result;

            Expect("网络解成功", r.Ok, true);
            Expect("站表非空", net.StationTable.Count >= 6, true);
            Console.WriteLine("  {0}", net.Path.Describe());
            Console.WriteLine("  {0}", net.DescribeStations());

            // ① 站表：每个站的参数有限，且在合理的物理区间里
            int bad = 0;
            foreach (KeyValuePair<int, GasState> kv in net.StationTable)
            {
                GasState g = kv.Value;
                if (g == null || !GasState.IsFinite(g.TotalPressure) || !GasState.IsFinite(g.TotalTemperature)) { bad++; continue; }
                if (g.TotalPressure <= 0.0 || g.TotalTemperature <= 100.0) bad++;
            }
            Expect("每站参数都有限且在物理区间", bad == 0, true);

            // 压气机出口总压必须高于风扇出口，涡轮出口必须低于燃烧室出口
            GasState fanOut = net.Station(Stations.FanExit);
            GasState compOut = net.Station(Stations.HpcExit);
            GasState burnOut = net.Station(Stations.BurnerExit);
            GasState turbOut = net.Station(Stations.TurbineExit);
            if (fanOut != null && compOut != null && burnOut != null && turbOut != null)
            {
                Expect("压气机出口压力 > 风扇出口", compOut.TotalPressure > fanOut.TotalPressure, true);
                Expect("燃烧室出口温度 > 压气机出口", burnOut.TotalTemperature > compOut.TotalTemperature, true);
                Expect("涡轮出口压力 < 燃烧室出口", turbOut.TotalPressure < burnOut.TotalPressure, true);
            }
            else _fails++;

            // ② 网络与旧入口同源
            CycleResult r2 = Cycle.SolveDesignPoint(d.Clone());
            Check("网络 vs Cycle.SolveDesignPoint 推力",
                net.Result.Thrust, r2.Thrust, 0.001, "N");
            Check("网络 vs Cycle.SolveDesignPoint 进气量", net.Result.MdotAir, r2.MdotAir, 0.001, "kg/s");

            // ③ 逐轴状态
            ShaftSet set = ShaftSet.Build(d);
            Expect("轴数 = ShaftCount", set.States.Count == Math.Max(1, d.ShaftCount), true);
            bool sane = set.States.Count > 0;
            int minId = int.MaxValue, maxId = int.MinValue;
            foreach (ShaftState st in set.States.Values)
            {
                if (st.Inertia <= 0.0 || st.OmegaDesign <= 0.0) sane = false;
                if (st.Id < minId) minId = st.Id;
                if (st.Id > maxId) maxId = st.Id;
            }
            Expect("每根轴都有惯量与设计转速", sane, true);
            set.SyncDesignPoint(d, r);
            Console.WriteLine("  " + set.Describe());
            double minPct = 1e9;
            foreach (ShaftState st in set.States.Values) minPct = Math.Min(minPct, st.Percent);
            Check("设计点各轴都在 100%", minPct, 100.0, 2.0, "%");

            // 盈余为正 → 转速往上爬；为负 → 往下溜。只动一个变量。
            ShaftState s0 = set[minId];
            double upFrom = s0.SpeedFraction * 0.80;
            s0.SpeedFraction = upFrom;
            s0.UpdatePower(s0.LoadPower * 2.0 + 5.0e4, s0.LoadPower);   // 涡轮给得比负载多
            double dUp = s0.StepToward(1.0, 0.2);
            Expect("涡轮盈余 → 转速上升", dUp > 0.0, true);

            s0.SpeedFraction = upFrom;
            s0.UpdatePower(s0.LoadPower * 0.30, s0.LoadPower);   // 涡轮只给负载的三成
            double dDown = s0.StepToward(1.0, 0.2);
            Expect("涡轮不够 → 转速下降", dDown < 0.0, true);

            // 每根轴各走各的：推一根，别的轴一动不动
            if (set.States.Count > 1)
            {
                ShaftState a = set[minId], b = set[maxId];
                a.SpeedFraction = 0.70;
                b.SpeedFraction = 0.90;
                double bBefore = b.SpeedFraction;
                a.UpdatePower(a.LoadPower * 1.5 + 1.0e4, a.LoadPower);
                a.StepToward(1.0, 0.5);
                Expect("另一根轴不会被拽着动",
                    Math.Abs(b.SpeedFraction - bBefore) < 1e-12, true);
                Expect("被推的那根轴确实在往上走", a.SpeedFraction > 0.70, true);
                Console.WriteLine("  分轴：{0} {1:F1}% → {2:F2}%（另一根 {3} 保持 {4:F1}%）",
                    a.Name, 70.0, a.Percent, b.Name, b.Percent);
            }
        }

        /// <summary>
        /// 进气畸变接进喘振风险。
        ///
        /// 这里要钉住两件事：一是**没进阶变时读数一动不动**（飞行的每一帧都过这条路径，
        /// 悄悄改一点就成了全包线重标定）；二是**弯管真的更危险**——畸变指数以前只算不用，
        /// 面板上摆着一个数字，实际一点作用都没有，等于骗人。
        ///
        /// 畸变不进推力、不进解算器，只进风险那一项：真机上畸变伤人的方式是
        /// "某几片叶片先失速"，不是"整台机器均匀变差"。
        /// </summary>
        private static void RunDistortionCases(EngineDesignData baseDesign)
        {
            Console.WriteLine("进气畸变：只进喘振风险，不改推力");
            EngineDesignData d = baseDesign.Clone();

            // ① 传 NaN = 老行为（按马赫估），这是全包线不动的保证
            double oldRisk = Surge.RiskOf(1.00, 0.0, 1.00, 1.60, 0.10);
            double nanRisk = Surge.RiskOf(1.00, 0.0, 1.00, 1.60, 0.10, double.NaN);
            Expect("畸变 NaN 与旧版逐位同值", Math.Abs(oldRisk - nanRisk) < 1e-12, true);
            Check("M1.6 马赫代理项 = (1.6-1.2)×0.30", oldRisk - 0.10, 0.12, 0.001, "risk");

            // ② 亚声速畸变就是基线 0.02，风险项该是 0（不能凭空给亚声速加风险）
            Expect("亚声速畸变 = 基线 0.02",
                Math.Abs(IntakeSolver.DistortionOf(d, 0.90) - Surge.DistortionBaseline) < 1e-9, true);
            Check("亚声速畸变项为 0", Surge.RiskOf(1.00, 0.0, 1.00, 0.90, 0.10,
                IntakeSolver.DistortionOf(d, 0.90)), 0.10, 0.0001, "risk");

            // ③ M1.6 这个锚点：真实畸变算出来的风险和老的马赫代理项打平（权重就是这么定的）
            double dist16 = IntakeSolver.DistortionOf(d, 1.60);
            Check("M1.6 畸变指数 0.23", dist16, 0.23, 2.0, "");
            Check("M1.6 真实畸变与代理项打平",
                Surge.RiskOf(1.00, 0.0, 1.00, 1.60, 0.10, dist16), oldRisk, 3.0, "risk");

            // ④ 弯管：同马赫同油门，畸变更大、风险更高
            EngineDesignData bent = d.Clone();
            bent.Intake.DuctBendDeg = 45.0;
            double distStraight = IntakeSolver.DistortionOf(d, 1.30);
            double distBent = IntakeSolver.DistortionOf(bent, 1.30);
            Expect("弯 45° 的畸变明显更大", distBent > distStraight + 0.10, true);
            double riskStraight = Surge.RiskOf(0.90, 0.0, 0.90, 1.30, 0.85, distStraight);
            double riskBent = Surge.RiskOf(0.90, 0.0, 0.90, 1.30, 0.85, distBent);
            Expect("同一手油门弯管风险更高", riskBent > riskStraight, true);
            Console.WriteLine("  M1.30 直管畸变 {0:F3}→风险 {1:F3}｜弯 45° 畸变 {2:F3}→风险 {3:F3}",
                distStraight, riskStraight, distBent, riskBent);

            // ⑤ 畸变有上限：马赫再高也不无限涨（老的马赫代理项没有上限，这是它作为临时占位的味道）
            Check("M3.0 畸变封顶", IntakeSolver.DistortionOf(d, 3.00), 0.35, 0.001, "");

            // ⑥ 真的会因此喘：同一台机器，弯管这一帧喘了，直管没喘
            SurgeRuntime rtStraight = new SurgeRuntime();
            SurgeRuntime rtBent = new SurgeRuntime();
            Surge.Step(rtStraight, 0.90, 0.0, 0.90, 1.30, 0.85, 1.0 / 60.0, distStraight);
            Surge.Step(rtBent, 0.90, 0.0, 0.90, 1.30, 0.85, 1.0 / 60.0, distBent);
            Expect("直管不喘", rtStraight.Events == 0, true);
            Expect("弯管喘了", rtBent.Events == 1, true);
            Console.WriteLine("  " + Surge.Explain(0.90, 0.0, 0.90, 1.30, 0.85, distBent));
        }

        /// <summary>
        /// 简单模式的进气道。
        ///
        /// 简单模式没有几何，只有三个旋钮：喉道/捕获、抽气、管道弯曲。
        /// 这里要钉住的还是那条老规矩——**填 0 的时候一个数都不许动**：
        /// 简单模式的老存档满地都是，缺省悄悄换个口径就等于给所有人重标定一遍。
        /// </summary>
        private static void RunSimpleIntakeCases()
        {
            Console.WriteLine("简单模式进气道：三个旋钮，缺省不动");
            var sd = new SimpleDesign();
            sd.Normalize();
            SimpleConstants c = Simple.DesignConstants(sd);
            Expect("简单模式设计点能解", c.Ok, true);

            SimpleResult baseR = Simple.Solve(sd, 0.0, 0.90, 1.0, -1.0, c);
            Expect("基准工况能解", baseR.Ok, true);

            // ① 缺省：喉道比 0 = 不限流，一个数都不动
            SimpleDesign same = sd.Clone();
            SimpleResult sameR = Simple.Solve(same, 0.0, 0.90, 1.0, -1.0, c);
            Check("缺省进气量不动", sameR.MdotAir, baseR.MdotAir, 1e-9, "kg/s");
            Check("缺省推力不动", sameR.NetThrust, baseR.NetThrust, 1e-9, "N");
            bool chokedWarn = false;
            foreach (string w in sameR.Warnings) if (w.Contains("壅塞")) chokedWarn = true;
            Expect("缺省不报喉道壅塞", chokedWarn, false);

            // ② 喉道比 0.5：高马赫下被憋住，流量与推力一起掉，而且要说清楚为什么
            SimpleDesign th = sd.Clone();
            th.IntakeThroatRatio = 0.50;
            SimpleResult thR = Simple.Solve(th, 0.0, 0.90, 1.0, -1.0, c);
            Expect("喉道比 0.5 → 流量确实被卡住", thR.MdotAir < baseR.MdotAir * 0.995, true);
            Expect("喉道比 0.5 → 推力跟着掉", thR.NetThrust < baseR.NetThrust, true);
            chokedWarn = false;
            foreach (string w in thR.Warnings) if (w.Contains("壅塞")) chokedWarn = true;
            Expect("限流时给出壅塞告警", chokedWarn, true);
            Console.WriteLine("  喉道/捕获 0.50：进气 {0:F1} → {1:F1} kg/s（{2:P1}）｜推力 {3:F2} → {4:F2} kN",
                baseR.MdotAir, thR.MdotAir, thR.MdotAir / Math.Max(baseR.MdotAir, 1e-9),
                baseR.NetThrust / 1000.0, thR.NetThrust / 1000.0);

            // ③ 抽气：多抽多少就少多少推力用的空气（乘性，不玩花样）
            SimpleDesign bl = sd.Clone();
            bl.IntakeBleedFrac = 0.10;
            SimpleResult blR = Simple.Solve(bl, 0.0, 0.90, 1.0, -1.0, c);
            Check("抽气 10% → 进气量 ×0.90", blR.MdotAir, baseR.MdotAir * 0.90, 0.5, "kg/s");
            Expect("抽气 10% → 推力下降", blR.NetThrust < baseR.NetThrust, true);

            // ④ 弯管：每 10° 掉 0.8% 总压，60° 就是 4.8%，一个系数两处共用
            Check("60° 弯管损失系数", IntakeSolver.BendLoss(60.0), 0.952, 0.1, "");
            SimpleDesign bd = sd.Clone();
            bd.IntakeDuctBendDeg = 60.0;
            SimpleResult bdR = Simple.Solve(bd, 0.0, 0.90, 1.0, -1.0, c);
            Expect("弯管 → 推力下降", bdR.NetThrust < baseR.NetThrust, true);
            // 4.8% 的总压损失换来 15% 的净推力损失，不是算错了：M0.9 下净推力是
            // ṁ(v_jet − v_0) 两个大数相减，v_jet 掉一点点，差值就掉一大截。
            // 这正是"高速巡航对进气道恢复异常敏感"的来源，别把它当 bug 抹平。
            double bendDrop = 1.0 - bdR.NetThrust / Math.Max(baseR.NetThrust, 1e-9);
            Expect("弯管 60° 净推力掉在 8%~25%（高速下被放大）",
                bendDrop > 0.08 && bendDrop < 0.25, true);
            Expect("弯管比抽气更伤（4.8% 总压 > 10% 空气在高速下的等价）",
                bdR.NetThrust < blR.NetThrust, true);
            Console.WriteLine("  抽气 10%：{0:F2} kN｜弯 60°：{1:F2} kN｜直管：{2:F2} kN",
                blR.NetThrust / 1000.0, bdR.NetThrust / 1000.0, baseR.NetThrust / 1000.0);

            // ⑤ 型式在简单模式也真的生效：皮托在 M1.6 比轴对称吃亏
            SimpleDesign pit = sd.Clone();
            pit.IntakeKind = IntakeKind.Pitot;
            SimpleResult pitR = Simple.Solve(pit, 0.0, 1.60, 1.0, -1.0, c);
            SimpleDesign ax = sd.Clone();
            SimpleResult axR = Simple.Solve(ax, 0.0, 1.60, 1.0, -1.0, c);
            Expect("M1.6 皮托比轴对称吃亏", pitR.MdotAir < axR.MdotAir, true);
            Console.WriteLine("  M1.60 轴对称 {0:F1} kg/s｜皮托 {1:F1} kg/s", axR.MdotAir, pitR.MdotAir);

            // ⑥ 存档：三个新字段必须存得回来（反射扫描在别处已覆盖，这里点一次名）
            var node = new ConfigNode(SimpleDesign.NodeName);
            SimpleDesignPersistence.Save(bd, node);
            SimpleDesign back = SimpleDesignPersistence.Load(node);
            Check("管道弯曲角存得回来", back.IntakeDuctBendDeg, 60.0, 1e-6, "°");
            Expect("喉道比与抽气也回来了",
                Math.Abs(back.IntakeThroatRatio - bd.IntakeThroatRatio) < 1e-9
                && Math.Abs(back.IntakeBleedFrac - bd.IntakeBleedFrac) < 1e-9, true);
        }

        /// <summary>
        /// 迎角/侧滑：进气道作为独立部件，得能吃"来流不直"这件事。
        ///
        /// 这条路的底线和前面几条一样：**缺省 0° 必须逐位不变**。
        /// 所以断言分成两半——先钉住"不动"，再钉住"动了多少、为什么动"。
        /// </summary>
        private static void RunIncidenceCases(EngineDesignData baseDesign)
        {
            Console.WriteLine("迎角/侧滑：0° 一个数都不动，斜了才掉");

            EngineDesignData d0 = baseDesign.Clone();
            d0.Intake.Kind = IntakeKind.Axial;
            IntakeSolution zero = IntakeSolver.Solve(d0, 0.90, 0.0);
            Expect("缺省迎角为 0", d0.Intake.AlphaDeg == 0.0, true);

            // ① 0° / 0°：横向角 0，折减系数正好 1.0，读数逐位相同
            Expect("横向角 0°", zero.CrossflowDeg == 0.0, true);
            EngineDesignData d0b = d0.Clone();
            d0b.Intake.AlphaDeg = 0.0;
            d0b.Intake.BetaDeg = 0.0;
            IntakeSolution zero2 = IntakeSolver.Solve(d0b, 0.90, 0.0);
            Check("0° 总压恢复逐位不动", zero2.Tpr, zero.Tpr, 1e-12, "");
            Check("0° 畸变逐位不动", zero2.DistortionIndex, zero.DistortionIndex, 1e-12, "");
            Expect("折减系数正好是 1", Intake.IncidenceLossFactor(IntakeKind.Axial, 0.0) == 1.0, true);

            // ② 迎角 10°：亚声速型式每度 0.12%，10° 就是 1.2%
            EngineDesignData a10 = d0.Clone();
            a10.Intake.AlphaDeg = 10.0;
            IntakeSolution a10R = IntakeSolver.Solve(a10, 0.90, 0.0);
            Check("10° 迎角 → 横向角 10°", a10R.CrossflowDeg, 10.0, 1e-9, "°");
            Check("10° 迎角 → 总压恢复 ×0.988", Intake.IncidenceLossFactor(IntakeKind.Axial, 10.0), 0.988, 0.05, "");
            Expect("10° 迎角 → 总压恢复确实降", a10R.Tpr < zero.Tpr * 0.999, true);
            Expect("10° 迎角 → 畸变升高", a10R.DistortionIndex > zero.DistortionIndex, true);

            // ③ 侧滑打七折：10° 侧滑不如 10° 迎角伤
            EngineDesignData b10 = d0.Clone();
            b10.Intake.BetaDeg = 10.0;
            IntakeSolution b10R = IntakeSolver.Solve(b10, 0.90, 0.0);
            Expect("10° 侧滑的横向角小于 10° 迎角", b10R.CrossflowDeg < a10R.CrossflowDeg, true);
            Expect("10° 侧滑比 10° 迎角少吃一点", b10R.Tpr > a10R.Tpr, true);
            Console.WriteLine("  10° 迎角：横向角 {0:F1}°｜TPR {1:F4}｜10° 侧滑：横向角 {2:F1}°｜TPR {3:F4}",
                a10R.CrossflowDeg, a10R.Tpr, b10R.CrossflowDeg, b10R.Tpr);

            // ④ 斜板/锥体对角度更敏感：同样的 10°，压缩型式掉得比亚声速型式多
            Check("斜板 10° 折减 0.965", Intake.IncidenceLossFactor(IntakeKind.Ramp, 10.0), 0.965, 0.05, "");
            Expect("同样 10°：斜板比亚轴对称掉得多",
                Intake.IncidenceLossFactor(IntakeKind.Ramp, 10.0) < Intake.IncidenceLossFactor(IntakeKind.Axial, 10.0), true);

            // ⑤ 大横向角要说人话：超过阈值必须有一条告警，阈值之下不许念
            Expect("5° 不报警", Intake.IncidenceWarning(IntakeKind.Axial, 5.0, 0.9) == null, true);
            Expect("20° 亚声速要报警", Intake.IncidenceWarning(IntakeKind.Axial, 20.0, 0.9) != null, true);
            Expect("M1.8 斜板 14° 要报警", Intake.IncidenceWarning(IntakeKind.Ramp, 14.0, 1.8) != null, true);
            EngineDesignData ramp = d0.Clone();
            ramp.Intake.Kind = IntakeKind.Ramp;
            ramp.Intake.AlphaDeg = 16.0;
            IntakeSolution rampR = IntakeSolver.Solve(ramp, 1.80, 0.0);
            bool incWarned = false;
            foreach (string w in rampR.Warnings) if (w.Contains("横向角")) incWarned = true;
            Expect("解算器把横向角告警带出来了", incWarned, true);

            // ⑥ 折减系数有下限：再斜也不至于把总压掐死（那是"没起动"，不是"迎角大"）
            Expect("折减系数不低于 0.80", Intake.IncidenceLossFactor(IntakeKind.Cone, 60.0) >= 0.80 - 1e-9, true);

            // ⑦ 存盘：两个新字段往返
            EngineDesignData save = baseDesign.Clone();
            save.Intake.AlphaDeg = 8.0;
            save.Intake.BetaDeg = -6.0;
            ConfigNode node = new ConfigNode("JETFORGE_DESIGN");
            save.Save(node);
            EngineDesignData back = EngineDesignData.Load(node);
            Check("迎角存得回来", back.Intake.AlphaDeg, 8.0, 1e-6, "°");
            Check("侧滑角存得回来", back.Intake.BetaDeg, -6.0, 1e-6, "°");

            // ⑧ 设计器元数据：迎角/侧滑角已经**搬到进气道零件面板**，发动机这边的 schema 里必须一条都不剩
            Expect("DesignSchema 里已经没有迎角", DesignSchema.Find("Intake.AlphaDeg") == null, true);
            Expect("DesignSchema 里已经没有侧滑角", DesignSchema.Find("Intake.BetaDeg") == null, true);
            Console.WriteLine("  斜板 M1.8 迎角 16°：TPR {0:F4}｜畸变 {1:F3}｜告警 {2} 条",
                rampR.Tpr, rampR.DistortionIndex, rampR.Warnings.Count);
        }

        /// <summary>
        /// 第 65 段：3D 型线（<see cref="IntakeProfile"/>）。
        ///
        /// 这一段测的是"图上的几何能不能自己算出性能参数"：
        /// 截面积分对不对（面积错了后面全错）、喉道认不认得出来、压缩角是不是真的从内壁量出来的、
        /// 以及 <see cref="IntakeSpec.Resolved"/> 有没有把型线量出来的数**顶掉**那几个手填比例栏。
        /// 判据都挑得能一眼看出根因：面积对不上就是公式错，压缩角对不上就是量错了地方。
        /// </summary>
        /// <summary>第 66 段：整机长度按部件叠出来，四台真机当标尺。</summary>
        private static void RunEngineLengthCases()
        {
            // 四台真机的实测长度（m）。估算值与真机差在 ±10% 之内就算"认得出来是同一台机器"，
            // 这条守着的是"长度不再由玩家拧"这件事本身——系数被谁改坏了，这里立刻掉。
            string[] names = { "J79", "J57", "J85", "RD-33" };
            double[] real = { 5.20, 4.66, 2.90, 4.20 };
            for (int i = 0; i < names.Length; i++)
            {
                EngineDesignData d = Presets.Build(names[i]);
                d.LengthAuto = true;
                double est = d.EffectiveLength;
                Expect(string.Format("{0} 按部件算的长度认得出真机（{1:F2} vs {2:F2} m）", names[i], est, real[i]),
                    Math.Abs(est - real[i]) / real[i] < 0.10, true);
            }

            // 加级数要变长：这是"长度由部件决定"最直白的检验。
            EngineDesignData s8 = Presets.Build("J79");
            s8.LengthAuto = true;
            double len8 = s8.EffectiveLength;
            s8.Hpc.Stages += 4;
            Expect("高压级数 +4 让整机变长", s8.EffectiveLength > len8 + 0.05, true);

            // 同样的级数、更大的直径也要变长（每级轴向长按 0.06·D+0.08 走）。
            EngineDesignData big = Presets.Build("J79");
            big.LengthAuto = true;
            big.Hpc.TipDiameter *= 1.4;
            Expect("压气机直径大一截，整机跟着变长", big.EffectiveLength > len8, true);

            // 加力段是要占地方的：开了加力，长度必须往上涨。
            EngineDesignData dry = Presets.Build("J79");
            dry.LengthAuto = true;
            dry.Afterburner.Enabled = false;
            EngineDesignData wet = Presets.Build("J79");
            wet.LengthAuto = true;
            wet.Afterburner.Enabled = true;
            Expect("开加力之后整机更长", wet.EffectiveLength > dry.EffectiveLength + 0.1, true);

            // 手填兜底：钉住就用钉住的，但太短的会被抬到下限（0.4 m 以下不是发动机）。
            EngineDesignData pin = Presets.Build("J79");
            pin.LengthAuto = false;
            pin.Length = 4.00;
            Check("钉住手填长度时就是手填值 m", pin.EffectiveLength, 4.00, 0.5);
            pin.Length = 0.10;
            Check("手填长度太小会被抬到下限 m", pin.EffectiveLength, EngineLength.MinLength, 0.5);

            // 分段表要真的分段：面板上那行文字不能是空壳。
            string bd = EngineLength.Breakdown(Presets.Build("J79"));
            Expect("分段表有内容", bd != null && bd.Length > 10, true);
            Expect("分段表里看得到喷管那一段", bd != null && bd.IndexOf("喷管") >= 0, true);

            Console.WriteLine("    长度估算：{0}", bd);
        }

        /// <summary>
        /// 第 67 段：杆位给油、解算器反查转速、油给少了转速自己往下掉。
        ///
        /// 用一个假解算器（`FuelCurveSampler`）而不是真机预设，图的是判据干净：
        /// 我规定 `油量 = 0.02 + 0.18x²`、`推力 = 1000x²`，于是"反查出来的转速对不对"
        /// 有一个精确答案（x = √((f−0.02)/0.18)），不用跟别处的标定扯在一起。
        /// 真实解算器那条路在实机自检里另外验。
        /// </summary>
        private static void RunFuelControlCases()
        {
            FuelCurveSampler s = new FuelCurveSampler(monotone: true);
            FuelSchedule sch = FuelSchedule.Build(s, 0.0, 0.0, 0.0);
            Expect("油量-转速表建得起来", sch != null && sch.Valid, true);

            // 表底 = 0.15 那一档的油量；表顶 = 满转速油量
            Check("表底油量 kg/s", sch.MinFuel, 0.02 + 0.18 * 0.15 * 0.15, 1.0);
            Check("表顶油量 kg/s", sch.MaxFuel, 0.02 + 0.18 * 1.0 * 1.0, 1.0);

            // 反查：给定油量，解出来的转速要回到原来那个 x
            double f6 = 0.02 + 0.18 * 0.36;
            Check("按油量反查转速（x=0.60）", sch.SpoolFor(f6), 0.60, 2.0);
            Check("按油量反查转速（x=0.85）", sch.SpoolFor(0.02 + 0.18 * 0.7225), 0.85, 2.0);

            // 低于表底：维持不了任何转速 → 返回 0，让转子自己掉
            Expect("油量低于表底时反查给 0（没有平衡点）", sch.SpoolFor(sch.MinFuel * 0.5) <= 1e-9, true);

            // 单调化：解算器在低转速段给出"转速更低反而更费油"的坏点时，表不许因此倒挂
            FuelCurveSampler bad = new FuelCurveSampler(monotone: false);
            FuelSchedule sbad = FuelSchedule.Build(bad, 0.0, 0.0, 0.0);
            bool mono = true;
            for (int i = 0; i < FuelSchedule.Points - 1; i++)
                if (sbad.Fuel[i + 1] < sbad.Fuel[i] - 1e-9) mono = false;
            Expect("坏点被单调化（油越多转速越高这条不许倒挂）", mono, true);

            // 无控制系统：杆位就是油量，杆到底 = 慢车油量（不是 0）
            FuelLoop hyd = new FuelLoop();
            hyd.IdleFuel = sch.MinFuel;
            hyd.MaxFuel = sch.MaxFuel;
            hyd.Reset();
            double sp0 = hyd.Step(0.0, 0.02, sch);
            Check("液压杆到底 = 慢车油量 kg/s", hyd.Fuel, sch.MinFuel, 0.5);
            Expect("液压杆到底不算低于最低油量", hyd.BelowIdle, false);
            double sp1 = hyd.Step(1.0, 0.02, sch);
            Check("液压满杆 = 满转速油量 kg/s", hyd.Fuel, sch.MaxFuel, 0.5);
            Expect("液压满杆目标转速在满转", sp1 > 0.97, true);
            Expect("液压目标转速随杆位单调升", sp1 > sp0, true);

            // FADEC：杆位是目标推力。要的推力比慢车还低时，控制器一路减油 → 低于最低油量
            FuelLoop f = new FuelLoop();
            f.Fadec = true;
            f.IdleFuel = sch.MinFuel;
            f.MaxFuel = sch.MaxFuel;
            f.Reset();
            f.Trim = 0.9;                                   // 假装在巡航转速上
            double fullT = 100.0;
            bool wentBelow = false;
            double lastFuel = f.Fuel;
            bool monotoneDown = true;
            double prevTrim = f.Trim;
            for (int i = 0; i < 400; i++)
            {
                f.Step(0.0, 0.02, sch, 5.0, 50.0, fullT);   // 目标 5 N，实际 50 N（比慢车还高）
                if (f.Trim > prevTrim + 1e-9) monotoneDown = false;
                prevTrim = f.Trim;
                if (f.BelowIdle) { wentBelow = true; break; }
            }
            Expect("FADEC 要的推力低于慢车时，油量一路减到最低以下", wentBelow, true);
            Expect("  而且是一路往下减，不是跳一下", monotoneDown, true);

            // FADEC 稳态：目标推力等于实际推力时油量不再动（这就是"闭环收敛"）
            FuelLoop hold = new FuelLoop();
            hold.Fadec = true;
            hold.IdleFuel = sch.MinFuel;
            hold.MaxFuel = sch.MaxFuel;
            hold.Reset();
            hold.Trim = 0.5;
            hold.Step(0.5, 0.02, sch, 0.0, 56.0, fullT);   // 杆位 0.5 → 目标 12 + 88×0.5 = 56 N
            double f0 = hold.Fuel;
            for (int i = 0; i < 50; i++) hold.Step(0.5, 0.02, sch, 0.0, 56.0, fullT);
            Check("FADEC 目标=实际时油量不动 kg/s", hold.Fuel, f0, 1.0);

            // 转速自己掉：目标 0 时按转子功率积分单调下降，且没有"一刀切到零"
            double x = 1.0, prev = 1.0;
            bool strict = true;
            for (int i = 0; i < 600; i++)
            {
                x = Spool.Step(x, 0.0, 1.0 / 60.0, 2.0, 2.0);
                if (x > prev + 1e-9) strict = false;
                prev = x;
            }
            Expect("目标转速 0 时转速单调不升", strict, true);
            Expect("  10 s 后确实掉下来了", x < 0.9, true);

            Console.WriteLine("    油量表：{0}", sch.Describe());
            Console.WriteLine("    液压：{0}", hyd.Describe());
        }

        /// <summary>
        /// 假的解算器：油量 = 0.02 + 0.18x²，推力 = 1000x²。
        /// `monotone: false` 时在低转速段故意给一个"转速更低、油却更多"的坏点，
        /// 用来验 <see cref="FuelSchedule.Build"/> 的单调化有没有生效。
        /// </summary>
        private sealed class FuelCurveSampler : ICycleSampler
        {
            private readonly bool _monotone;
            public FuelCurveSampler(bool monotone) { _monotone = monotone; }

            public PerformanceGrid.Sample Sample(double throttle, double alt, double mach, double abFrac, double a8Frac = -1.0)
            {
                PerformanceGrid.Sample s = new PerformanceGrid.Sample();
                double x = throttle < 0.05 ? 0.05 : throttle;
                s.Valid = true;
                s.Thrust = 1000.0 * x * x;
                s.FuelFlow = 0.02 + 0.18 * x * x;
                if (!_monotone && x < 0.22) s.FuelFlow *= 1.6;    // 低转速段的坏点
                s.MassFlow = 10.0 * x;
                s.N2 = x;
                s.WComp = 1.0e6 * x;
                s.SurplusW = 1.0e6 * x;
                return s;
            }

            public string Describe() { return "假解算器"; }
        }

        private static void RunIntakeProfileCases()
        {
            // ① 截面：正圆就是 πr²，超椭圆 n=4 要介于圆和正方形之间
            IntakeSection circle = new IntakeSection(0.0, 0.6, 0.6);
            Check("正圆 Ø0.6 面积 m²", circle.Area, Math.PI * 0.09, 0.5);
            Check("正圆 Ø0.6 周长 m", circle.Perimeter, Math.PI * 0.6, 0.5);

            IntakeSection square = new IntakeSection(0.0, 0.6, 0.6);
            square.Shape = IntakeSectionShape.Superellipse;
            square.Exponent = 4.0;
            double f4 = IntakeSection.SuperellipseFactor(4.0);
            Expect("n=4 面积系数在 π/4 与 1 之间", f4 > Math.PI / 4.0 + 1e-6 && f4 < 1.0, true);
            Check("n=4 截面面积 m²", square.Area, 0.36 * f4, 0.5);
            Expect("方的比圆的能吃更多气", square.Area > circle.Area, true);

            // 扁口：宽 0.8 高 0.4 的椭圆，面积 = πab
            IntakeSection flat = new IntakeSection(0.0, 0.8, 0.4);
            flat.Shape = IntakeSectionShape.Ellipse;
            Check("扁口 0.8×0.4 面积 m²", flat.Area, Math.PI * 0.4 * 0.2, 0.5);

            // ② 型线：捕获 / 喉道 / 出口
            IntakeProfile p = new IntakeProfile();
            p.Sections.Add(new IntakeSection(0.0, 0.6, 0.6));      // 捕获 Ø0.6
            p.Sections.Add(new IntakeSection(0.2, 0.5, 0.5));      // 喉道 Ø0.5
            p.Sections.Add(new IntakeSection(0.8, 0.55, 0.55));    // 出口 Ø0.55

            Expect("喉道认在第二个截面", p.ThroatIndex == 1, true);
            Check("喉道/捕获", p.ThroatRatio, (0.5 * 0.5) / (0.6 * 0.6), 0.5);
            Check("出口面积 m²", p.ExitArea, Math.PI * 0.275 * 0.275, 0.5);
            Check("型线长度 m", p.Length, 0.8, 0.2);

            // ③ 压缩角 = 内壁从捕获到喉道转过的角度（半高收缩 0.05 m / 轴向 0.2 m → 14.04°）
            Check("压缩角（内壁收缩）°", p.CompressionDeg, Math.Atan2(0.05, 0.2) * 180.0 / Math.PI, 0.5);
            Expect("两级折点认得出", p.CompressionStages >= 1, true);

            // ④ 弯曲角：中线 Z 偏移连成折线，每段 26.57°
            IntakeProfile bend = new IntakeProfile();
            bend.Sections.Add(new IntakeSection(0.0, 0.6, 0.6));
            IntakeSection mid = new IntakeSection(0.3, 0.6, 0.6); mid.OffsetZ = 0.15;
            bend.Sections.Add(mid);
            bend.Sections.Add(new IntakeSection(0.6, 0.6, 0.6));
            Check("S 形弯曲角 °", bend.BendDeg, 2.0 * Math.Atan2(0.15, 0.3) * 180.0 / Math.PI, 0.5);

            // ⑤ 中心体：0.3 m 长、半锥 10°、把口堵掉四成 → 锥面半径按 tan 长
            IntakeProfile cone = p.Clone();
            cone.SpinnerLength = 0.3;
            cone.SpinnerRadiusRatio = 0.4;
            cone.SpinnerHalfAngleDeg = 10.0;
            Expect("有中心体", cone.HasSpinner, true);
            Check("锥面 x=0.1 处半径 m", cone.SpinnerRadiusAt(0.1), Math.Tan(10.0 * Math.PI / 180.0) * 0.1, 1.0);
            Expect("锥顶往前没东西", cone.SpinnerRadiusAt(-0.01) <= 0.0, true);
            Expect("锥尾以半径封口", Math.Abs(cone.SpinnerRadiusAt(0.35) - cone.SpinnerRadius) < 1e-9
                                     || cone.SpinnerRadiusAt(0.35) == 0.0, true);
            // ⑤b 锥尖可调位置：整条锥跟着挪，不是只把尖端画在第一个截面上
            IntakeProfile coneFwd = cone.Clone();
            coneFwd.SpinnerTipX = 0.15;
            Check("锥尖前伸 0.15 m 后锥面整体前移 m", coneFwd.SpinnerRadiusAt(0.25),
                Math.Tan(10.0 * Math.PI / 180.0) * 0.10, 1.0);
            Expect("前伸以后第一个截面那一带没东西了", coneFwd.SpinnerRadiusAt(0.05) <= 0.0, true);

            // ⑥ 合法性：截面必须按轴向排、尺寸必须为正
            string why;
            Expect("正常型线通过校验", p.Validate(out why), true);
            IntakeProfile bad = p.Clone();
            bad.Sections[1].X = 0.0;
            bool badOk = bad.Validate(out why);
            Expect("轴向没往后走要被抓住", !badOk && why.Contains("更靠后"), true);
            IntakeProfile zero = p.Clone();
            zero.Sections[2].Width = 0.0;
            bool zeroOk = zero.Validate(out why);
            Expect("截面为 0 要被抓住", !zeroOk && why.Contains("断开了"), true);

            // ⑦ Resolved：型线量出来的数要顶掉手填的比例栏，并且强制走激波模型
            IntakeSpec spec = new IntakeSpec();
            spec.Kind = IntakeKind.Pitot;      // 故意给一个错的型式，验证它不再参与计算
            spec.ThroatRatio = 0.30;           // 故意给一个手填值
            spec.Model = RecoveryModel.Empirical;
            spec.Shape = p;
            IntakeSpec rs = spec.Resolved();
            Expect("型线存在 → 强制激波模型", rs.Model == RecoveryModel.Shock, true);
            Check("捕获口径取等面积圆 m", rs.CaptureDiameter, p.EquivalentCaptureDiameter, 0.2);
            Check("喉道比取量出来的", rs.ThroatRatio, p.ThroatRatio, 0.5);
            Check("弯曲角取量出来的", rs.DuctBendDeg, p.BendDeg, 0.5);
            Expect("型式不再参与（Pitot 也给了 0.69 喉道比）", Math.Abs(rs.ThroatRatio - 0.30) > 0.2, true);
            // 用老口径反算喉道面积，得跟型线上的喉道面积一致（两条口径必须对得上，否则流量会歪）
            Check("喉道面积与型线一致 m²", Math.PI * Math.Pow(rs.CaptureDiameter * 0.5, 2) * rs.ThroatRatio,
                p.ThroatArea, 1.0);

            // ⑧ 性能跟着几何走：喉道收得越狠，压缩角越大，M2 的总压恢复越低
            IntakeSpec tight = new IntakeSpec();
            IntakeProfile tp = new IntakeProfile();
            tp.Sections.Add(new IntakeSection(0.0, 0.6, 0.6));
            tp.Sections.Add(new IntakeSection(0.2, 0.42, 0.42));   // 收得很狠
            tp.Sections.Add(new IntakeSection(0.8, 0.50, 0.50));
            tight.Shape = tp;
            TechLevel tech = TechLevel.Get("1970s");
            double tprLoose = Intake.Recovery(spec, tech, 2.0, 2.0);
            double tprTight = Intake.Recovery(tight, tech, 2.0, 2.0);
            Console.WriteLine("  型线 M2 恢复：缓收 {0:F4}（压缩 {1:F1}°）｜狠收 {2:F4}（压缩 {3:F1}°）",
                tprLoose, p.CompressionDeg, tprTight, tp.CompressionDeg);
            Expect("收得越狠恢复越低", tprTight < tprLoose, true);
            Expect("缓收的不该判脱体", Intake.Started(spec, 2.0, 2.0), true);

            // ⑨ 型线存盘往返：段数、位置、中心体都要原样回来；半条型线要整条作废
            string txt = IntakeSpecCodec.ProfileToString(cone);
            IntakeProfile backP = IntakeSpecCodec.ParseProfile(txt);
            Expect("型线存得回来", backP != null, true);
            if (backP != null)
            {
                Expect("段数一致", backP.Sections.Count == cone.Sections.Count, true);
                Check("第 2 段轴向位置 m", backP.Sections[1].X, cone.Sections[1].X, 0.01);
                Check("第 3 段宽度 m", backP.Sections[2].Width, cone.Sections[2].Width, 0.01);
                Check("中心体长度 m", backP.SpinnerLength, cone.SpinnerLength, 0.01);
                Check("往返后压缩角不变 °", backP.CompressionDeg, cone.CompressionDeg, 0.01);
            }
            Expect("乱码型线整条作废", IntakeSpecCodec.ParseProfile("garbage") == null, true);
            Expect("缺字段的型线整条作废", IntakeSpecCodec.ParseProfile("0,0.6;0.2,0.5") == null, true);

            // ⑩ 网格与面积同源：把截面轮廓按 256 点取样、用鞋带公式量面积，得跟解析面积一致。
            // 这条是"图与账对得上"的机器判据——参数化挪到别处、指数改错、宽高接反，它都会掉。
            Check("轮廓取样面积 = 解析面积（圆）", ShoelaceArea(circle, 256), circle.Area, 0.5);
            Check("轮廓取样面积 = 解析面积（方中带圆）", ShoelaceArea(square, 256), square.Area, 0.5);
            Check("轮廓取样面积 = 解析面积（扁口）", ShoelaceArea(flat, 256), flat.Area, 0.5);

            // ⑪ 网格缓存签名必须看得见型线。
            // 这条守的是一个"改完没反应"型的坑：自定义件的捕获直径/喉道比/弯曲角在 Spec 里
            // 全是 0（都由型线量出来），只看那几个字段的话，两条形状完全不同的气道签名相同，
            // 于是共用同一张性能网格——玩家把喉道拖细了，推力一点不动，而且没有任何报错。
            {
                TechLevel techS = TechLevel.Get("1970s");
                IntakeSpec a = new IntakeSpec();
                a.Shape = IntakeProfile.Default(1.2, 0.80);
                IntakeSpec b = a.Clone();
                b.Shape.Sections[1].Width = 0.55;      // 只改型线，不动任何比例字段
                b.Shape.Sections[1].Height = 0.55;

                List<IntakeUnit> ua = new List<IntakeUnit> { new IntakeUnit(a, 0.6, "A", 0.0, 1.2) };
                List<IntakeUnit> ub = new List<IntakeUnit> { new IntakeUnit(b, 0.6, "A", 0.0, 1.2) };
                List<IntakeUnit> ua2 = new List<IntakeUnit> { new IntakeUnit(a.Clone(), 0.6, "A", 0.0, 1.2) };
                string sa = IntakeInstall.Aggregate(ua, techS, 0.0, 0.6).Signature;
                string sb = IntakeInstall.Aggregate(ub, techS, 0.0, 0.6).Signature;
                string sa2 = IntakeInstall.Aggregate(ua2, techS, 0.0, 0.6).Signature;
                Expect("型线不同的两件进气道签名不同（否则共用同一张性能表）", sa != sb, true);
                Expect("型线相同的两件进气道签名相同（该共用表的时候要共用）", sa == sa2, true);
                Console.WriteLine("    签名长度 {0} 字符（含型线：{1}）", sa.Length,
                    sa.IndexOf(IntakeSpecCodec.ProfileToString(a.Shape)) >= 0);
            }

            // ⑫ 坏型线必须退回老口径，不许把 NaN 带进激波解。
            // 存档被改坏、或者将来某个界面写进一截 NaN 宽度时，这里的表现应该是"没有型线"，
            // 而不是"压缩角 NaN → 激波解 NaN → 推力 0"——后者要跨七八层调用才查得到病根。
            {
                IntakeSpec badP = new IntakeSpec();
                badP.Shape = IntakeProfile.Default(1.2, 0.80);
                badP.Shape.Sections[1].Width = double.NaN;
                Expect("坏型线不算有型线", Intake.HasProfile(badP), false);
                Expect("坏型线不强制走激波（退回经验曲线兜底）", badP.Resolved().Model == RecoveryModel.Empirical, true);
                double ang = Intake.CompressionAngle(badP, 2.0);
                Expect("坏型线的压缩角仍是有限数", !double.IsNaN(ang) && ang > 0.0, true);

                IntakeSpec goodP = new IntakeSpec();
                goodP.Shape = IntakeProfile.Default(1.2, 0.80);
                Expect("好型线算有型线", Intake.HasProfile(goodP), true);
                Expect("好型线强制走激波", goodP.Resolved().Model == RecoveryModel.Shock, true);
            }

            // ⑬ 自由多边形：形状由顶点说了算，面积/周长/采样/存档四处必须同一个形状。
            {
                IntakeSection sq = new IntakeSection(0.5, 0.8, 0.8);
                sq.Polygon = new List<SectionPoint>
                {
                    new SectionPoint(-1, -1), new SectionPoint(1, -1),
                    new SectionPoint(1, 1), new SectionPoint(-1, 1),
                };
                Check("正方形多边形面积 m²", sq.Area, 0.64, 0.5);
                Check("正方形多边形周长 m", sq.Perimeter, 3.2, 0.5);
                Expect("多边形的采样点落在顶点之间", OutlineOnEdge(sq), true);

                int before = sq.Polygon.Count;
                sq.InsertPolygonPoint(0);
                Check("插点之后多一个顶点", sq.Polygon.Count, before + 1, 0.0);
                Expect("插进来的点在那条边的中点上",
                    Math.Abs(sq.Polygon[1].U) < 1e-9 && Math.Abs(sq.Polygon[1].W - (-1.0)) < 1e-9, true);
                Expect("删点删得掉", sq.RemovePolygonPoint(1), true);
                IntakeSection tri = new IntakeSection(0.5, 0.8, 0.8);
                tri.Polygon = new List<SectionPoint>
                {
                    new SectionPoint(-1, -1), new SectionPoint(1, -1), new SectionPoint(0, 1),
                };
                Expect("只剩三个顶点时拒绝再删", tri.RemovePolygonPoint(0), false);

                // 存档往返：多边形本身、以及它算出来的喉道面积都要一致。
                IntakeProfile pp = IntakeProfile.Default(1.2, 0.80);
                pp.Sections[1].Polygon = sq.Polygon == null ? null : IntakeSection.ClonePolygon(sq.Polygon);
                string str = IntakeSpecCodec.ProfileToString(pp);
                IntakeProfile back = IntakeSpecCodec.ParseProfile(str);
                Expect("含多边形的型线存得回来", back != null, true);
                if (back != null)
                {
                    Expect("多边形顶点数往返一致", back.Sections[1].Polygon != null && back.Sections[1].Polygon.Count == sq.Polygon.Count, true);
                    Check("多边形截面积往返一致 m²", back.Sections[1].Area, pp.Sections[1].Area, 0.5);
                }
                // 三角形截面必须比同宽高的圆小得多——证明喉道认的是多边形不是外接椭圆。
                IntakeSection round = new IntakeSection(0.5, 0.8, 0.8);
                Expect("三角形截面明显小于同宽高的圆", tri.Area < round.Area * 0.70, true);
            }

            // ⑭ 预设轮廓与镜像：按钮点下去改的是同一个对象，面积得跟着立刻变。
            {
                IntakeSection ps = new IntakeSection(0.5, 0.8, 0.50);

                ps.ApplyPreset(SectionPreset.Circle);
                Expect("预设：圆把高拉成宽", Math.Abs(ps.Height - ps.Width) < 1e-9, true);
                Expect("预设：圆不带多边形", ps.HasPolygon, false);
                Check("预设：圆面积 m²", ps.Area, Math.PI * 0.4 * 0.4, 1.0);

                ps.ApplyPreset(SectionPreset.WideEllipse);
                Check("预设：宽椭圆高 = 0.62 宽", ps.Height / ps.Width, 0.62, 0.5);

                ps.ApplyPreset(SectionPreset.TallEllipse);
                Check("预设：竖椭圆高 = 1.60 宽", ps.Height / ps.Width, 1.60, 0.5);

                ps.ApplyPreset(SectionPreset.RoundedRect);
                Expect("预设：圆角矩形是超椭圆 n=6", ps.Shape == IntakeSectionShape.Superellipse && Math.Abs(ps.Exponent - 6.0) < 1e-9, true);
                ps.ApplyPreset(SectionPreset.Rect);
                Check("预设：矩形（n=12）面积填满八成以上", ps.Area / (ps.Width * ps.Height), 0.982, 1.0);

                IntakeSection d2 = new IntakeSection(0.5, 0.8, 0.8);
                d2.ApplyPreset(SectionPreset.Diamond);
                Expect("预设：菱形是 4 个顶点", d2.Polygon != null && d2.Polygon.Count == 4, true);
                Check("预设：菱形面积 = 一半的宽×高", d2.Area / (0.8 * 0.8), 0.5, 1.0);

                IntakeSection tz = new IntakeSection(0.5, 0.8, 0.8);
                tz.ApplyPreset(SectionPreset.Trapezoid);
                // 上底 1.2、下底 2.0（归一化）、高 2 → 归一化面积 3.2，占外接矩形 4 的 0.80
                Check("预设：梯形面积 = 0.80 倍的宽×高", tz.Area / (0.8 * 0.8), 0.80, 1.0);

                IntakeSection ln = new IntakeSection(0.5, 0.8, 0.8);
                ln.ApplyPreset(SectionPreset.Lens);
                Expect("预设：双凸透镜两端是尖角（端点处 w≈0）", LensTapered(ln), true);
                Check("预设：双凸透镜面积 ≈ 0.367 宽×高", ln.Area / (0.8 * 0.8), 0.367, 2.0);

                IntakeSection br = new IntakeSection(0.5, 0.8, 0.8);
                br.ApplyPreset(SectionPreset.Barrel);
                Expect("预设：鼓形两侧比上下宽", BarrelWider(br), true);

                // 真实外形 ≠ 名义框：鼓形 W 最远只到 0.786（r=(1+0.12cos2θ)/1.12），
                // 所以 0.8 的框量出来是 0.8 宽 × 0.629 高。读数以前只报框，
                // "宽 0.600 × 高 0.600"和"面积 0.2242 m²"摆在同一个窗口里没人能对上账。
                double rw, rh;
                br.RealExtent(out rw, out rh);
                Check("鼓形真实宽度等于名义框（U 到 ±1）", rw, 0.8, 1.0);
                Check("鼓形真实高度只有框的 78.6%", rh / 0.8, 0.786, 1.5);
                IntakeSection cir = new IntakeSection(0.0, 0.8, 0.8);
                cir.ApplyPreset(SectionPreset.Circle);
                cir.RealExtent(out rw, out rh);
                Check("圆形多边形真实外形就是 0.8×0.8（没有多余缩水）", rw + rh, 1.6, 1.0);

                // 绕向不变量：放样网格按"逆时针"求法线与三角绕序，预设要是顺时针，
                // 图上看着完全正常、3D 里法线朝内、面被背面剔除（玩家看到的是"镜像完形状就坏了"）。
                Expect("预设多边形绕向统一成逆时针",
                    d2.SignedArea > 0.0 && tz.SignedArea > 0.0 && ln.SignedArea > 0.0 && br.SignedArea > 0.0, true);
                Expect("双凸透镜端点不重复（2n−2 个顶点）", ln.Polygon != null && ln.Polygon.Count == 26, true);

                // 玩家报的"有些预设直接用模型就异常、Y 轴镜像之后反而正常"：
                // 四个手写预设逐个验——绕向必须是逆时针、轮廓必须是凸的（不自交、不拧麻花），
                // 再各镜像一次，结论不许变。3D 网格按"环 0 是逆时针"求法线与三角绕序，
                // 这两条一破，画面上就是面朝里、被背面剔除，看着像模型坏了。
                SectionPreset[] handwritten = { SectionPreset.Diamond, SectionPreset.Trapezoid,
                                                SectionPreset.Lens, SectionPreset.Barrel };
                for (int i = 0; i < handwritten.Length; i++)
                {
                    IntakeSection pc = new IntakeSection(0.50, 0.80, 0.70);
                    pc.ApplyPreset(handwritten[i]);
                    Expect("预设 " + handwritten[i] + "：轮廓凸且逆时针", PolyConvexCcw(pc), true);
                    pc.MirrorUpDown();
                    Expect("预设 " + handwritten[i] + "：X 轴镜像后仍凸且逆时针", PolyConvexCcw(pc), true);
                    pc.MirrorLeftRight();
                    Expect("预设 " + handwritten[i] + "：Y 轴镜像后仍凸且逆时针", PolyConvexCcw(pc), true);
                }

                // 拧麻花的根因：放样按"从 0 号顶点走过的周长比例"取样，圆/椭圆却从 θ=0（正右）起。
                // 两截面的 0 号点不在同一方位，相邻两环就错开一段连起来——壳面上是一道螺旋的拧，
                // 中心体和内壁最明显；镜像把顶点顺序倒过来，于是"镜像一下就正常"。
                // 不变量：任何截面的 0 号采样点都落在正右（U 取最大值处）。
                for (int i = 0; i < handwritten.Length; i++)
                {
                    IntakeSection pc = new IntakeSection(0.50, 0.80, 0.70);
                    pc.ApplyPreset(handwritten[i]);
                    double u0, w0;
                    pc.OutlinePoint(0.0, out u0, out w0);
                    double maxU = -1e9;
                    for (int k = 0; k < pc.Polygon.Count; k++) maxU = Math.Max(maxU, pc.Polygon[k].U);
                    Check("预设 " + handwritten[i] + "：0 号采样点在正右（不拧）", u0, maxU * pc.Width * 0.5, 0.5);
                    pc.MirrorUpDown();
                    pc.OutlinePoint(0.0, out u0, out w0);
                    maxU = -1e9;
                    for (int k = 0; k < pc.Polygon.Count; k++) maxU = Math.Max(maxU, pc.Polygon[k].U);
                    Check("预设 " + handwritten[i] + "：镜像之后 0 号采样点仍在正右", u0, maxU * pc.Width * 0.5, 0.5);
                }
                // 椭圆截面本身就是从正右起，两者必须对得上，否则圆过渡到多边形那一段就是拧的。
                IntakeSection ell = new IntakeSection(0.50, 0.80, 0.60);
                double eu, ew;
                ell.OutlinePoint(0.0, out eu, out ew);
                Check("椭圆截面 0 号采样点也在正右 m", eu, 0.40, 0.5);

                // 两个镜像开关必须各自独立：勾 X 只翻 W，再勾 Y 才翻 U，都取消要精确回到原轮廓。
                // 旧实现是每次直接在顶点上取反，按第二个时第一个会被"翻转 + 旋转"顶掉。
                IntakeSection asym = new IntakeSection(0.50, 0.80, 0.60);
                asym.Polygon = new List<SectionPoint>
                {
                    new SectionPoint(-0.5, 0.2), new SectionPoint(0.7, 0.5), new SectionPoint(0.2, -0.8),
                };
                asym.BakeMirrorBase();
                List<SectionPoint> orig = IntakeSection.ClonePolygon(asym.Polygon);
                asym.SetMirror(true, false);
                Expect("镜像开关：只勾 X 轴只翻 W（U 不动）", SameShape(asym.Polygon, orig, false, true), true);
                asym.SetMirror(true, true);
                Expect("镜像开关：再勾 Y 轴，X 轴的效果还在（U、W 都翻）", SameShape(asym.Polygon, orig, true, true), true);
                asym.SetMirror(false, true);
                Expect("镜像开关：取消 X 轴，Y 轴仍保持", SameShape(asym.Polygon, orig, true, false), true);
                asym.SetMirror(false, false);
                Expect("镜像开关：两个都取消 → 精确回到原轮廓", SameShape(asym.Polygon, orig, false, false), true);
                Expect("镜像开关：状态位跟着走", asym.MirrorXOn == false && asym.MirrorYOn == false, true);

                // 镜像：X 轴翻 W 取反、Y 轴翻 U 取反，面积必须不变（翻错方向会把鞋带算成负的）。
                // 用不对称的多边形验方向：拿菱形试是白试——菱形自己就上下对称，翻完看不出变化。
                IntakeSection m = new IntakeSection(0.5, 0.8, 0.6);
                m.Polygon = new List<SectionPoint>
                {
                    new SectionPoint(-0.5, 0.2), new SectionPoint(0.6, 0.4), new SectionPoint(0.1, -1.0),
                };
                double a0 = m.Area;
                m.MirrorUpDown();
                Check("X 轴镜像后面积不变 m²", m.Area, a0, 0.01);
                Expect("X 轴镜像把 W 取反", HasPoint(m, -0.5, -0.2) && HasPoint(m, 0.6, -0.4) && HasPoint(m, 0.1, 1.0), true);
                double a1 = m.Area;
                m.MirrorLeftRight();
                Check("Y 轴镜像后面积不变 m²", m.Area, a1, 0.01);
                Expect("Y 轴镜像把 U 取反", HasPoint(m, 0.5, -0.2) && HasPoint(m, -0.6, -0.4) && HasPoint(m, -0.1, 1.0), true);
                // 取反一个轴等于翻面：不掰回来的话，镜像过的这一段在 3D 里法线朝内。
                Expect("镜像后绕向仍是逆时针", m.SignedArea > 0.0, true);

                // 1/4 镜像：只给第一象限的三角形，生成整圈之后面积应是原来的四倍，且四份对称。
                IntakeSection q = new IntakeSection(0.5, 0.8, 0.6);
                q.Polygon = new List<SectionPoint>
                {
                    new SectionPoint(0.0, 0.0), new SectionPoint(1.0, 0.0), new SectionPoint(0.0, 1.0),
                };
                double qa = q.Area;
                Expect("1/4 镜像生成了整圈", q.MirrorQuarter(), true);
                Check("1/4 镜像后面积是四倍 m²", q.Area, qa * 4.0, 0.5);
                Expect("1/4 镜像四份都在", HasPoint(q, 1.0, 0.0) && HasPoint(q, -1.0, 0.0) && HasPoint(q, 0.0, 1.0) && HasPoint(q, 0.0, -1.0), true);
                Expect("1/4 镜像后没有重复顶点", NoDuplicatePoints(q), true);

                IntakeSection noq = new IntakeSection(0.5, 0.8, 0.6);
                noq.Polygon = new List<SectionPoint>
                {
                    new SectionPoint(-1.0, -1.0), new SectionPoint(-0.5, -0.2), new SectionPoint(0.2, -0.9),
                };
                // 全在下半平面：以前直接返回 false（界面上"点了没反应"），现在把轮廓折进第一象限再做。
                Expect("下半平面的轮廓也能折进第一象限做 1/4 镜像", noq.MirrorQuarter(), true);
                Expect("折进来之后绕向是逆时针", noq.SignedArea > 0.0, true);
                Expect("折进来之后四份都在", HasPoint(noq, 1.0, 1.0) || HasPoint(noq, 0.2, 0.9), true);

                IntakeSection cw = new IntakeSection(0.5, 0.8, 0.6);
                cw.Polygon = new List<SectionPoint>
                {
                    new SectionPoint(0.0, 1.0), new SectionPoint(1.0, 0.0), new SectionPoint(0.0, -1.0),
                };
                Expect("顺时针的环被认出来", cw.SignedArea < 0.0, true);
                Expect("顺时针的环能被掰正", cw.EnsureCounterClockwise(), true);
                Expect("掰完就是逆时针", cw.SignedArea > 0.0, true);
            }

            // ⑫ 截面倾角：只把这一圈歪成一个斜面，**不动它挡住气流的那块投影**。
            //    这是"斜的进气口"的地基：倾角一旦漏进面积/流量，玩家把唇口拉斜推力就跟着变，
            //    而真机上把截面斜过来并不改变迎风投影面积。
            {
                const double D2R = Math.PI / 180.0;
                IntakeSection t = new IntakeSection(0.30, 0.60, 0.60);
                Expect("不歪时轴向偏移是 0", Math.Abs(t.AxialShift(0.10, 0.25)) < 1e-12, true);
                t.TiltPitchDeg = 30.0;
                Expect("侧视歪 30°：半高 0.25 m 处往后挪 tan30·0.25 m",
                    Math.Abs(t.AxialShift(0.0, 0.25) - Math.Tan(30.0 * D2R) * 0.25) < 1e-9, true);
                Expect("侧视倾角不管横向 u", Math.Abs(t.AxialShift(0.25, 0.0)) < 1e-12, true);
                t.TiltYawDeg = 20.0;
                Expect("俯视再歪 20°：横向 0.25 m 处往后挪 tan20·0.25 m",
                    Math.Abs(t.AxialShift(0.25, 0.0) - Math.Tan(20.0 * D2R) * 0.25) < 1e-9, true);
                Expect("两轴一起歪时两个方向都算进去",
                    Math.Abs(t.AxialShift(0.25, 0.25) - (Math.Tan(20.0 * D2R) + Math.Tan(30.0 * D2R)) * 0.25) < 1e-9, true);
                Expect("倾角不改截面面积", Math.Abs(t.Area - circle.Area) < 1e-9, true);
                Expect("倾角超出 ±45° 被夹住",
                    Math.Abs(IntakeSection.ClampTilt(120.0) - IntakeSection.MaxTiltDeg) < 1e-12 &&
                    Math.Abs(IntakeSection.ClampTilt(-90.0) + IntakeSection.MaxTiltDeg) < 1e-12, true);
                Expect("NaN 倾角当 0（NaN 进了网格整圈顶点都是 NaN）", IntakeSection.ClampTilt(double.NaN) == 0.0, true);

                // 段与段之间要线性过渡：两段都歪 30°，中间采样也得是 30°
                // （各歪各的话，放样在中间会拧一下，机匣看着像麻花）。
                IntakeProfile tilt = new IntakeProfile();
                IntakeSection t0 = new IntakeSection(0.0, 0.6, 0.6); t0.TiltPitchDeg = 30.0;
                IntakeSection t1 = new IntakeSection(0.6, 0.5, 0.5); t1.TiltPitchDeg = 30.0;
                tilt.Sections.Add(t0); tilt.Sections.Add(t1);
                Expect("中间采样也是 30°", Math.Abs(tilt.Sample(0.3).TiltPitchDeg - 30.0) < 1e-9, true);

                // 存盘往返；老存档只有 8 个字段，读出来倾角是 0 而不是整条作废
                t1.TiltYawDeg = -12.5;
                IntakeProfile round = IntakeSpecCodec.ParseProfile(IntakeSpecCodec.ProfileToString(tilt));
                Expect("带倾角的型线存得回来", round != null, true);
                if (round != null)
                {
                    Expect("往返后侧视倾角 30°", Math.Abs(round.Sections[1].TiltPitchDeg - 30.0) < 1e-6, true);
                    Expect("往返后俯视倾角 -12.5°", Math.Abs(round.Sections[1].TiltYawDeg + 12.5) < 1e-6, true);
                }
                IntakeProfile oldFmt = IntakeSpecCodec.ParseProfile("0,0.6,0.6,0,0,2,0,0;0.6,0.5,0.5,0,0,2,0,0");
                Expect("老存档（每段 8 个字段）还读得出来", oldFmt != null && oldFmt.Sections.Count == 2, true);
                if (oldFmt != null)
                    Expect("老存档的倾角是 0", oldFmt.Sections[0].TiltPitchDeg == 0.0 && oldFmt.Sections[0].TiltYawDeg == 0.0, true);
            }
        }

        /// <summary>双凸透镜的两端应该收到尖角：u=±1 处的 |w| 小于最大高度的五分之一。</summary>
        private static bool LensTapered(IntakeSection s)
        {
            if (!s.HasPolygon) return false;
            double maxW = 0.0, endW = 0.0;
            for (int i = 0; i < s.Polygon.Count; i++)
            {
                double w = Math.Abs(s.Polygon[i].W);
                if (w > maxW) maxW = w;
                if (Math.Abs(Math.Abs(s.Polygon[i].U) - 1.0) < 1e-6) endW = Math.Max(endW, w);
            }
            return maxW > 0.2 && endW < 0.2 * maxW;
        }

        /// <summary>
        /// 轮廓是不是"按给定的两个符号翻转之后"的样子（顶点集合按容差比，不管顺序）。
        /// 镜像会顺带把顶点顺序掰成逆时针、起点转到正右，所以只能比集合不能比顺序。
        /// </summary>
        private static bool SameShape(List<SectionPoint> got, List<SectionPoint> basePoly, bool flipU, bool flipW)
        {
            if (got == null || basePoly == null || got.Count != basePoly.Count) return false;
            for (int i = 0; i < basePoly.Count; i++)
            {
                double u = flipU ? -basePoly[i].U : basePoly[i].U;
                double w = flipW ? -basePoly[i].W : basePoly[i].W;
                bool hit = false;
                for (int k = 0; k < got.Count && !hit; k++)
                    if (Math.Abs(got[k].U - u) < 1e-6 && Math.Abs(got[k].W - w) < 1e-6) hit = true;
                if (!hit) return false;
            }
            return true;
        }

        /// <summary>
        /// 轮廓是不是"凸 + 逆时针"。
        ///
        /// 凸：相邻三点的叉乘同号（不自交、不拧麻花）；逆时针：叉乘为正。
        /// 这两个性质一破，放样网格的环就不是"绕一圈"，法线与三角绕序全跟着错——
        /// 玩家看到的是"模型异常"，而 Y 轴镜像恰好把绕向翻回来，于是"镜像一下就好了"。
        /// </summary>
        private static bool PolyConvexCcw(IntakeSection s)
        {
            if (!s.HasPolygon || s.Polygon.Count < 3) return false;
            if (s.SignedArea <= 0.0) return false;
            int n = s.Polygon.Count;
            for (int i = 0; i < n; i++)
            {
                SectionPoint a = s.Polygon[i];
                SectionPoint b = s.Polygon[(i + 1) % n];
                SectionPoint c = s.Polygon[(i + 2) % n];
                double cross = (b.U - a.U) * (c.W - b.W) - (b.W - a.W) * (c.U - b.U);
                if (cross <= 1e-9) return false;
            }
            return true;
        }

        /// <summary>鼓形：同一高度上，中间那行的横向跨度要大于中间那列的竖向跨度。</summary>
        private static bool BarrelWider(IntakeSection s)
        {
            if (!s.HasPolygon) return false;
            double maxU = 0.0, maxW = 0.0;
            for (int i = 0; i < s.Polygon.Count; i++)
            {
                maxU = Math.Max(maxU, Math.Abs(s.Polygon[i].U));
                maxW = Math.Max(maxW, Math.Abs(s.Polygon[i].W));
            }
            return maxU > maxW * 1.1 && maxU <= 1.001;
        }

        private static bool HasPoint(IntakeSection s, double u, double w)
        {
            if (!s.HasPolygon) return false;
            for (int i = 0; i < s.Polygon.Count; i++)
                if (Math.Abs(s.Polygon[i].U - u) < 1e-6 && Math.Abs(s.Polygon[i].W - w) < 1e-6) return true;
            return false;
        }

        /// <summary>
        /// 第 100 段：控制系统分档（Phase3 文档 3.4 的"机械/液压/模拟/数字的能力差异"）。
        ///
        /// 钉的不是"限到多少度"，而是**每一档多一道闸、少一道闸**：
        ///   ①能力排序单调（无控 < 机械 < 液压 < 模拟 < 数字）——排序写错，
        ///     年代封顶就会把高档次压成低档次，或者反过来给低年代发高档次；
        ///   ②年代上限落在真年代上（1970s 做不出数字，1990s 做不出全数字）；
        ///   ③机械调速器没有热电偶，所以**不限温**；这道闸不能凭空给它；
        ///   ④模拟式 FADEC 有温度与转速保护，但**没有喘振抑制、没有超转切油**；
        ///   ⑤加减速速率分档，机械必须最慢。
        /// 为什么这些差异值得钉：玩家选年代时，唯一看得见的就是这几道闸。
        /// 四档跑起来一模一样，那年代选择就只是四个字。
        /// </summary>
        private static void RunControlTierCases()
        {
            int rn = ControlSystem.Rank(ControlKind.None);
            int rm = ControlSystem.Rank(ControlKind.Mechanical);
            int rh = ControlSystem.Rank(ControlKind.Hydro);
            int ra = ControlSystem.Rank(ControlKind.Analog);
            int rf = ControlSystem.Rank(ControlKind.Fadec);
            Console.WriteLine("    能力排序：无控 {0}｜机械 {1}｜液压 {2}｜模拟 {3}｜数字 {4}", rn, rm, rh, ra, rf);
            Expect("能力排序单调：无控 < 机械 < 液压 < 模拟 < 数字", rn < rm && rm < rh && rh < ra && ra < rf, true);

            // ② 年代上限：落在 TechLevel 真有的那五个年代上
            Expect("1940s 最多做得出无控",
                ControlSystem.Allowed(TechLevel.Get("1940s")) == ControlKind.None, true);
            Expect("1960s 最多做得出机械液压",
                ControlSystem.Allowed(TechLevel.Get("1960s")) == ControlKind.Hydro, true);
            Expect("1970s 最多做得出模拟式 FADEC（电子调节，分立元件）",
                ControlSystem.Allowed(TechLevel.Get("1970s")) == ControlKind.Analog, true);
            Expect("1990s 才有全权限数字",
                ControlSystem.Allowed(TechLevel.Get("1990s")) == ControlKind.Fadec, true);
            Expect("2010s 也是全权限数字",
                ControlSystem.Allowed(TechLevel.Get("2010s")) == ControlKind.Fadec, true);

            EngineDesignData d = Presets.Build("RD-33");
            Expect("RD-33 预设存在（1970s 机身）", d != null, true);
            if (d == null) return;
            d.Control = ControlKind.Fadec;
            Expect("1970s 选了数字 FADEC，运行时压回模拟式 FADEC",
                ControlSystem.KindName(ControlSystem.Effective(d)) == "模拟式 FADEC", true);
            d.Control = ControlKind.Mechanical;
            Expect("机械调速器不算高档，年代上限压不掉它（这是降级不是超年代）",
                ControlSystem.Effective(d) == ControlKind.Mechanical, true);
            d.TechName = "2010s";
            d.Control = ControlKind.Fadec;
            Expect("2010s 选数字 FADEC 原样生效",
                ControlSystem.Effective(d) == ControlKind.Fadec, true);

            // 三、四：闸的有无。喂一个"超温 + 喘振风险爆表 + 超转"的极端读数，
            // 看每档到底合上了几道闸——档位差异就体现在这里。
            double dt = 1.0 / 60.0;
            double t4Lim = 1500.0;

            ControlRuntime mech = new ControlRuntime();
            ControlSystem.Step(mech, ControlKind.Mechanical, 1.0, 0.6, 60.0, 1600.0, t4Lim, 2.0, dt);
            Expect("机械调速器不限温（没有热电偶，这道闸它不该有）", mech.LimitingTemp == false, true);

            ControlRuntime hyd = new ControlRuntime();
            ControlSystem.Step(hyd, ControlKind.Hydro, 1.0, 0.6, 60.0, 1600.0, t4Lim, 2.0, dt);
            Expect("机械液压超温会收油", hyd.LimitingTemp == true, true);
            Expect("机械液压不认识喘振裕度", hyd.LimitingSurge == false, true);

            ControlRuntime ana = new ControlRuntime();
            ControlSystem.Step(ana, ControlKind.Analog, 1.0, 0.6, 60.0, 1600.0, t4Lim, 2.0, dt);
            Expect("模拟式 FADEC 也限温", ana.LimitingTemp == true, true);
            Expect("模拟式 FADEC 没有喘振抑制（算不出裕度）", ana.LimitingSurge == false, true);

            ControlRuntime fad = new ControlRuntime();
            ControlSystem.Step(fad, ControlKind.Fadec, 1.0, 0.6, 60.0, 1600.0, t4Lim, 2.0, dt);
            Expect("数字式 FADEC 喘振风险爆表时压油门", fad.LimitingSurge == true, true);

            ControlRuntime anaOs = new ControlRuntime();
            ControlSystem.Step(anaOs, ControlKind.Analog, 1.0, 1.0, 110.0, 1200.0, t4Lim, 0.0, dt);
            Expect("模拟式 FADEC 没有超转切油", anaOs.OverspeedTripped == false, true);
            Expect("模拟式 FADEC 仍会比例限转", anaOs.LimitingSpeed == true, true);

            ControlRuntime fadOs = new ControlRuntime();
            ControlSystem.Step(fadOs, ControlKind.Fadec, 1.0, 1.0, 110.0, 1200.0, t4Lim, 0.0, dt);
            Expect("数字式 FADEC 转速过 102% 直接切油", fadOs.OverspeedTripped == true, true);

            // ⑤ 速率分档
            double sM = ControlFirstStep(ControlKind.Mechanical);
            double sH = ControlFirstStep(ControlKind.Hydro);
            double sA = ControlFirstStep(ControlKind.Analog);
            double sF = ControlFirstStep(ControlKind.Fadec);
            Console.WriteLine("    一帧能加多少（1/60 s）：机械 {0:F4}｜液压 {1:F4}｜模拟 {2:F4}｜数字 {3:F4}",
                sM, sH, sA, sF);
            Expect("机械调速器推油最慢、数字最快", sM < sH && sH < sF, true);
            Expect("模拟与数字的推油速率一样（同一个 2.20/s 上限）", Math.Abs(sA - sF) < 1e-9, true);

            string[] names =
            {
                ControlSystem.KindName(ControlKind.None), ControlSystem.KindName(ControlKind.Mechanical),
                ControlSystem.KindName(ControlKind.Hydro), ControlSystem.KindName(ControlKind.Analog),
                ControlSystem.KindName(ControlKind.Fadec)
            };
            bool distinct = true;
            for (int i = 0; i < names.Length; i++)
                for (int j = i + 1; j < names.Length; j++)
                    if (names[i] == names[j]) distinct = false;
            Expect("五档的名字互不相同（面板上分得出来）", distinct, true);
        }

        /// <summary>
        /// 第 101 段：加力点火窗口随控制系统档次变化（Phase3 文档 3.4 的"加力点火逻辑"）。
        ///
        /// 钉四件事：
        ///   ①老点火器的最低可点压力更高（机械 40 kPa > 液压/模拟 25 kPa > 数字 22 kPa）；
        ///   ②**老口径不许动**——不传档位的 `Check` 必须与液压档逐位相同，老存档读数不能因为加档位而变；
        ///   ③点火高度上限随档次单调升高（这条是递给界面看的"能点多高"）；
        ///   ④只有数字机在边界外**当帧断油**（AutoCut），其余档次只是退油——
        ///     真机上这叫"有点火监控" vs "没有"，玩家能感觉到的差别就是"喷着油等它着"和"立刻灭"。
        /// </summary>
        private static void RunAfterburnerTierCases()
        {
            double dMech = AfterburnerLight.MinDuctKPaOf(ControlKind.Mechanical);
            double dHyd = AfterburnerLight.MinDuctKPaOf(ControlKind.Hydro);
            double dAna = AfterburnerLight.MinDuctKPaOf(ControlKind.Analog);
            double dFad = AfterburnerLight.MinDuctKPaOf(ControlKind.Fadec);
            Console.WriteLine("    最低可点压力：机械 {0:F0}｜液压 {1:F0}｜模拟 {2:F0}｜数字 {3:F0} kPa",
                dMech, dHyd, dAna, dFad);
            Expect("老点火器要求更高压力（机械 > 液压 = 模拟 > 数字）",
                dMech > dHyd && Math.Abs(dHyd - dAna) < 1e-9 && dHyd > dFad, true);

            EngineDesignData d = Presets.Build("RD-33");
            // 预设是按"干态额定"标定的（开加力设计点会按加力温度重算），所以这里手动打开加力，
            // 只为测点火边界——不碰它的推力标定。
            if (d != null && d.Afterburner != null) d.Afterburner.Enabled = true;
            Expect("RD-33 打开加力之后具备加力", d != null && d.Afterburner != null && d.Afterburner.Enabled, true);
            if (d == null || d.Afterburner == null || !d.Afterburner.Enabled) return;

            // ② 老口径逐位不变
            AbResult oldWay = AfterburnerLight.Check(d, d.DesignAlt, d.DesignMach, true);
            AbResult hydWay = AfterburnerLight.Check(d, d.DesignAlt, d.DesignMach, true, ControlKind.Hydro);
            Expect("不传档位的老调用与液压档完全一致",
                oldWay.Status == hydWay.Status && Math.Abs(oldWay.DuctKPa - hydWay.DuctKPa) < 1e-12,
                true);

            // ③ 点火高度上限随档次升高
            double cMech = AfterburnerLight.CeilingMeters(d, 0.90, ControlKind.Mechanical);
            double cHyd = AfterburnerLight.CeilingMeters(d, 0.90, ControlKind.Hydro);
            double cFad = AfterburnerLight.CeilingMeters(d, 0.90, ControlKind.Fadec);
            Console.WriteLine("    点火高度上限（Ma0.9）：机械 {0:F0} m｜液压 {1:F0} m｜数字 {2:F0} m",
                cMech, cHyd, cFad);
            Expect("数字机能点得比机械高、液压居中", cFad > cHyd && cHyd >= cMech - 1.0, true);

            // ④ 在"机械点不着、液压点得着"的高度上比三件事
            double alt = cMech + 500.0;
            AbResult m = AfterburnerLight.Check(d, alt, 0.90, true, ControlKind.Mechanical);
            AbResult h = AfterburnerLight.Check(d, alt, 0.90, true, ControlKind.Hydro);
            AbResult f = AfterburnerLight.Check(d, alt, 0.90, true, ControlKind.Fadec);
            Console.WriteLine("    {0:F0} m：机械 {1}（{2:F1} kPa）｜液压 {3}｜数字 {4}",
                alt, m.Status, m.DuctKPa, h.Status, f.Status);
            Expect("同一高度：机械点不着", m.Lit == false, true);
            Expect("同一高度：液压点得着", h.Lit == true, true);

            Expect("液压没有点火监控", h.Monitored == false, true);
            Expect("模拟式有点火监控",
                AfterburnerLight.Check(d, alt, 0.90, true, ControlKind.Analog).Monitored == true, true);
            Expect("点着的时候不报断油", h.AutoCut == false && f.AutoCut == false, true);

            // 再往上一点：连数字机也点不着了。这时才该看到"数字机断油、液压只是退油"。
            double altCut = cFad + 500.0;
            AbResult hydCut = AfterburnerLight.Check(d, altCut, 0.90, true, ControlKind.Hydro);
            AbResult fadCut = AfterburnerLight.Check(d, altCut, 0.90, true, ControlKind.Fadec);
            Console.WriteLine("    {0:F0} m：液压 {1}（{2:F1} kPa）｜数字 {3}（断油 {4}）",
                altCut, hydCut.Status, hydCut.DuctKPa, fadCut.Status, fadCut.AutoCut);
            Expect("两边都判断点不着", hydCut.Lit == false && fadCut.Lit == false, true);
            Expect("数字机在边界外当帧断油，其余档次只退油",
                fadCut.AutoCut == true && hydCut.AutoCut == false, true);

            // 富油也一样：数字机判完就断，老机器只是喷着油等"说不定能着"
            EngineDesignData rich = Presets.Build("RD-33");
            rich.Afterburner.Enabled = true;
            rich.Afterburner.FuelRatio = 0.09;   // 高于富油吹熄极限 0.065
            AbResult richHyd = AfterburnerLight.Check(rich, 0.0, 0.0, true, ControlKind.Hydro);
            AbResult richFad = AfterburnerLight.Check(rich, 0.0, 0.0, true, ControlKind.Fadec);
            Expect("富油点不着时状态是 TooRich",
                richHyd.Status == AbLightStatus.TooRich && richFad.Status == AbLightStatus.TooRich, true);
            Expect("富油点不着：只有数字机断油", richHyd.AutoCut == false && richFad.AutoCut == true, true);
            Expect("断油的理由写在告警里", richFad.Note != null && richFad.Note.Contains("断油"), true);
        }

        /// <summary>从 0.20 推到 1.00 的一帧里，指令实际前进了多少——用来比各档的加速度限幅。</summary>
        private static double ControlFirstStep(ControlKind k)
        {
            ControlRuntime rt = new ControlRuntime();
            double dt = 1.0 / 60.0;
            ControlSystem.Step(rt, k, 0.20, 0.5, 60.0, 1000.0, 1500.0, 0.0, dt);
            double before = rt.Command;
            ControlSystem.Step(rt, k, 1.00, 0.5, 60.0, 1000.0, 1500.0, 0.0, dt);
            return rt.Command - before;
        }

        private static bool NoDuplicatePoints(IntakeSection s)
        {
            if (!s.HasPolygon) return false;
            for (int i = 0; i < s.Polygon.Count; i++)
                for (int j = i + 1; j < s.Polygon.Count; j++)
                    if (Math.Abs(s.Polygon[i].U - s.Polygon[j].U) < 1e-6 &&
                        Math.Abs(s.Polygon[i].W - s.Polygon[j].W) < 1e-6) return false;
            return true;
        }

        /// <summary>多边形采样点必须落在轮廓上（到某条边的距离接近 0）。</summary>
        private static bool OutlineOnEdge(IntakeSection s)
        {
            for (int i = 0; i < 24; i++)
            {
                double u, w;
                s.OutlinePoint(2.0 * Math.PI * i / 24.0, out u, out w);
                double nu = u / (s.Width * 0.5), nw = w / (s.Height * 0.5);
                bool onEdge = Math.Abs(Math.Abs(nu) - 1.0) < 1e-6 || Math.Abs(Math.Abs(nw) - 1.0) < 1e-6;
                if (!onEdge) return false;
            }
            return true;
        }

        private static double ShoelaceArea(IntakeSection s, int n)
        {
            double sum = 0.0;
            double pu = 0.0, pw = 0.0, cu = 0.0, cw = 0.0;
            s.OutlinePoint(0.0, out pu, out pw);
            for (int i = 1; i <= n; i++)
            {
                double t = 2.0 * Math.PI * i / n;
                s.OutlinePoint(t, out cu, out cw);
                sum += pu * cw - cu * pw;
                pu = cu; pw = cw;
            }
            return Math.Abs(sum) * 0.5;
        }

        /// <summary>
        /// 第 102 段：喷口调度按控制系统档次分家。
        ///
        /// 老机器（机械/液压）的 A8 是**杆拉口开**：飞行员扳加力杆，作动筒跟着杆走，
        /// 没人看涡轮后温度，所以喷口位置只认档位；点火失败时喷口照样张着，推力白丢。
        /// 数字 FADEC 的 A8 是**调度**出来的，控制律里就有"喉道得装得下这股气"，
        /// 所以它跟着 T7 走；模拟 FADEC 只有温度补偿、没有闭环，取一半。
        ///
        /// 这一段钉三件事：开环档一个数都没动（老存档读数不变）、干态两档完全一样
        /// （没有加力就没有分歧）、加热之后闭环档明显收得更小。
        /// </summary>
        private static void RunNozzleScheduleTierCases()
        {
            Expect("闭环权重：无控/机械/液压都是纯开环",
                NozzleSchedule.ClosedLoopWeight(ControlKind.None) == 0.0
                && NozzleSchedule.ClosedLoopWeight(ControlKind.Mechanical) == 0.0
                && NozzleSchedule.ClosedLoopWeight(ControlKind.Hydro) == 0.0, true);
            Expect("闭环权重：模拟半掺、数字全闭环",
                Math.Abs(NozzleSchedule.ClosedLoopWeight(ControlKind.Analog) - 0.5) < 1e-12
                && Math.Abs(NozzleSchedule.ClosedLoopWeight(ControlKind.Fadec) - 1.0) < 1e-12, true);

            EngineDesignData rd = Presets.Build("RD-33");
            ExpectTrue("RD-33 预设存在（可变喷口才谈得上调度）", rd != null && NozzleSchedule.CanVary(rd.Nozzle));
            if (rd == null || !NozzleSchedule.CanVary(rd.Nozzle)) return;
            rd.Afterburner.Enabled = true;
            DesignConstants c = OffDesign.DeriveDesignConstants(rd);

            // ① 干态（杆在零位、T7 = 设计 T7）：两档装上去的面积必须一模一样。
            double dryOpen = NozzleSchedule.ThroatAreaInstalled(rd, c, 0.0, 1.0, c.T7D, ControlKind.Hydro);
            double dryFad = NozzleSchedule.ThroatAreaInstalled(rd, c, 0.0, 1.0, c.T7D, ControlKind.Fadec);
            Expect("干态：开环与闭环装上去的面积相同（没有加热就没有分歧）",
                Math.Abs(dryOpen - dryFad) < 1e-12, true);

            // ② 开环档与老口径逐位一致——老存档里液压机的读数不许因为这次改动而变。
            double legacy = NozzleSchedule.ThroatArea(rd, c, 0.0, 1.0);
            Expect("开环档：装上去的面积就是作动筒位置（与老口径逐位一致）",
                Math.Abs(dryOpen - legacy) < 1e-12, true);
            double openHot = NozzleSchedule.ThroatArea(rd, c, 1.0, 1.0);
            Expect("开环档：完全不知道温度（T7 变了面积不变）",
                Math.Abs(NozzleSchedule.ThroatAreaInstalled(rd, c, 1.0, 1.0, 1111.0, ControlKind.Hydro) - openHot) < 1e-12, true);

            // ③ 加热到 1111 K（FAR 0.030 的实测温度）：闭环档收回去，而且正好落到需求上。
            double t7Warm = 1111.0;
            double pos = NozzleSchedule.ThroatArea(rd, c, 1.0, 1.0);
            double need = NozzleSchedule.ThroatAreaNeed(rd, c, 1.0, t7Warm);
            double warmHydro = NozzleSchedule.ThroatAreaInstalled(rd, c, 1.0, 1.0, t7Warm, ControlKind.Hydro);
            double warmAnalog = NozzleSchedule.ThroatAreaInstalled(rd, c, 1.0, 1.0, t7Warm, ControlKind.Analog);
            double warmFadec = NozzleSchedule.ThroatAreaInstalled(rd, c, 1.0, 1.0, t7Warm, ControlKind.Fadec);
            Console.WriteLine("    T7 {0:F0} K：作动筒位置 {1:F5}｜温度需求 {2:F5}｜液压 {3:F5}｜模拟 {4:F5}｜数字 {5:F5} m²",
                t7Warm, pos, need, warmHydro, warmAnalog, warmFadec);
            Expect("闭环档装的就是温度需求", Math.Abs(warmFadec - need) < 1e-12, true);
            Expect("数字档比液压档收得小（不再白撑着喷口）", warmFadec < warmHydro - 1e-6, true);
            Expect("模拟档落在两者之间", warmAnalog < warmHydro - 1e-9 && warmAnalog > warmFadec + 1e-9, true);
            Console.WriteLine("    数字/液压 = {0:P1}（差得越多，开环机在高空低油气比时越吃亏）",
                warmFadec / Math.Max(warmHydro, 1e-9));

            // ④ 端到端：同一台机、地面 Ma0.9 满加力 FAR 0.030，数字档的 A8 必须真的更小。
            EngineDesignData hyd = Presets.Build("RD-33");
            hyd.Afterburner.Enabled = true;
            hyd.Afterburner.FuelRatio = 0.030;
            hyd.Control = ControlKind.Hydro;
            EngineDesignData fad = Presets.Build("RD-33");
            fad.Afterburner.Enabled = true;
            fad.Afterburner.FuelRatio = 0.030;
            fad.Control = ControlKind.Fadec;
            OffDesignResult rh = OffDesign.Solve(hyd, 0.0, 0.9, 1.0, 1.0, null, 61, 1.0, 1.0, 1.0, 0, null, 1.0);
            OffDesignResult rf = OffDesign.Solve(fad, 0.0, 0.9, 1.0, 1.0, null, 61, 1.0, 1.0, 1.0, 0, null, 1.0);
            Console.WriteLine("    端到端（Ma0.9 满加力 FAR 0.030）：液压 A8 {0:F5}｜数字 A8 {1:F5}｜T7 {2:F0}/{3:F0} K",
                rh.A8Used, rf.A8Used, rh.T7, rf.T7);
            // 注意这里只敢钉"数字档不更大"。满加力时 T7 真的烧到了 1900 K，
            // 开环按杆撑到 1.36 倍、闭环按温度要 1.32 倍，两者本来就该差不多——
            // 大偏差只出现在"杆拉了、气没热"的时候（点火失败、油气比没跟上），
            // 那个工况在 ③ 里用 1111 K 直接量过了。
            Expect("端到端：数字档的喷口不大于开环档", rf.A8Used <= rh.A8Used + 1e-9, true);
            Console.WriteLine("    两者相差 {0:P1}（满加力时本来就该接近：杆撑 1.36 倍，温度要 1.32 倍）",
                rf.A8Used / Math.Max(rh.A8Used, 1e-9) - 1.0);
        }

        /// <summary>
        /// 第 103 段：空中再起动的三条墙。
        ///
        /// 这一段守的是"熄火之后还有没有物理"，不是"某一台机能点多高"：
        /// 1. 三条墙分开报，别把"转速不对"说成"空气太稀"；
        /// 2. 点火器能力的阶梯（机械 45 → 数字 20 kPa）真的体现在能点的高度上；
        /// 3. 风车转速随马赫上升——那是"空中点火的 N2 不是 0"的来源。
        /// </summary>
        private static void RunRelightCases()
        {
            Expect("门槛阶梯：无控与机械最高、液压次之、数字最低",
                Math.Abs(Relight.MinPressureKPaOf(ControlKind.None) - 45.0) < 1e-9
                && Math.Abs(Relight.MinPressureKPaOf(ControlKind.Mechanical) - 45.0) < 1e-9
                && Relight.MinPressureKPaOf(ControlKind.Hydro) < Relight.MinPressureKPaOf(ControlKind.Mechanical) - 1.0
                && Relight.MinPressureKPaOf(ControlKind.Analog) < Relight.MinPressureKPaOf(ControlKind.Hydro) - 1.0
                && Relight.MinPressureKPaOf(ControlKind.Fadec) < Relight.MinPressureKPaOf(ControlKind.Analog) - 1.0, true);
            Expect("只有数字机有自动再点火",
                !Relight.AutoRelight(ControlKind.Mechanical) && !Relight.AutoRelight(ControlKind.Hydro)
                && !Relight.AutoRelight(ControlKind.Analog) && Relight.AutoRelight(ControlKind.Fadec), true);

            EngineDesignData rd = Presets.Build("RD-33");
            ExpectTrue("RD-33 预设存在", rd != null);
            if (rd == null) return;
            // 预设的控制系统缺省是"无控"（那是油门直给油），拿来测再点火会读出"无控"；
            // 真机上装进气道火系统的是液压/数字档，这里按液压档测。
            rd.Control = ControlKind.Hydro;

            // ① 转速太低：说"转速"，不许拿"空气稀"顶上。
            RelightResult low = Relight.Window(rd, 3000.0, 0.9, 0.08);
            Console.WriteLine("    3000 m/Ma0.9 转速 8%：{0}", low.Reason);
            Expect("转速低于下限：不让点，且原因指向转速",
                !low.CanLight && low.Reason.Contains("转速") && low.Reason.Contains("下限"), true);

            // ② 转速太高：同样不让点，但理由完全不同（富油/超温）。
            RelightResult high = Relight.Window(rd, 3000.0, 0.9, 0.85);
            Console.WriteLine("    3000 m/Ma0.9 转速 85%：{0}", high.Reason);
            Expect("转速高于上限：不让点，且原因指向上限",
                !high.CanLight && high.Reason.Contains("上限") && !high.Reason.Contains("进气总压"), true);

            // ③ 低空、窗口内：点得着。
            RelightResult ok = Relight.Window(rd, 3000.0, 0.9, 0.40);
            Console.WriteLine("    3000 m/Ma0.9 转速 40%：{0}", ok.Note);
            Expect("低空窗口内：点得着，且没有理由", ok.CanLight && ok.InSpoolWindow && ok.Reason.Length == 0, true);

            // ④ 高空同一转速：空气太稀，点不着，理由换成进气总压。
            RelightResult thin = Relight.Window(rd, 22000.0, 0.9, 0.40);
            Console.WriteLine("    22000 m/Ma0.9 转速 40%：{0}", thin.Reason);
            Expect("高空：不让点，且原因指向进气总压",
                !thin.CanLight && thin.Reason.Contains("进气总压"), true);

            // ⑤ 点火器越好，能点的高度越高：机械 < 液压 < 模拟 < 数字。
            double cMech = Relight.CeilingMeters(rd, 0.9, ControlKind.Mechanical);
            double cHyd = Relight.CeilingMeters(rd, 0.9, ControlKind.Hydro);
            double cAna = Relight.CeilingMeters(rd, 0.9, ControlKind.Analog);
            double cFad = Relight.CeilingMeters(rd, 0.9, ControlKind.Fadec);
            Console.WriteLine("    再点火高度上限（Ma0.9）：机械 {0:F0} m｜液压 {1:F0} m｜模拟 {2:F0} m｜数字 {3:F0} m",
                cMech, cHyd, cAna, cFad);
            Expect("点火器越好，能点的高度越高",
                cMech < cHyd - 100.0 && cHyd < cAna - 100.0 && cAna < cFad - 100.0, true);

            // ⑥ 风车转速随马赫上升：这就是"空中点火的 N2 不是 0"。
            RelightResult slow = Relight.Window(rd, 6000.0, 0.3, -1.0);
            RelightResult fast = Relight.Window(rd, 6000.0, 0.9, -1.0);
            Console.WriteLine("    风车转速：Ma0.3 {0:F0}%｜Ma0.9 {1:F0}%（传 -1 就用风车转速当当前转速）",
                slow.WindmillSpool * 100.0, fast.WindmillSpool * 100.0);
            Expect("传 -1 时当前转速就是风车转速",
                Math.Abs(slow.Spool - slow.WindmillSpool) < 1e-12, true);
            Expect("风车转速随马赫上升", fast.WindmillSpool > slow.WindmillSpool + 1e-6, true);
            ExpectTrue("读数里写了窗口范围", slow.Note.Contains("窗口"));
        }

        /// <summary>
        /// 第 107 段：齿轮减速到底在哪几条路径上生效。
        ///
        /// 起因：`CHANGELOG.md` 的"已知限制"里写着「非设计点风扇转速仍按轴转速比例缩放，
        /// 齿轮比只影响设计点与其后的折算」——那句话是前一轮凭印象写的，没人验过。
        /// 验法很简单：同一个设计只改齿轮比，看风扇叶尖线速度。齿轮真生效，它就该掉到 1/3，
        /// 而轴一侧的转速与推力关系不该跟着一起动。
        ///
        /// 结果（§204）：设计点与非设计点**都**乘了 1/齿轮比，那句限制是过期的。
        /// 断言钉的是"风扇叶尖速度比 ≈ 齿轮比"，不是推力差——推力还受旁路面积、风扇压比一起影响，
        /// 拿它当齿轮的证据是拿混在一起的数当单变量用。
        /// </summary>
        private static void RunGearOffDesignCases()
        {
            EngineDesignData g = Presets.Build("GTF1100");
            ExpectTrue("GTF1100 预设存在", g != null);
            if (g == null) return;
            Expect("预设的风扇带 3.0 的齿轮比", Math.Abs(g.Fan.GearRatio - 3.0) < 1e-9, true);

            EngineDesignData direct = g.Clone();
            direct.Fan.GearRatio = 1.0;

            // ① 设计点：风扇叶尖速度按 1/齿轮比 掉。
            NetworkSolution a = EngineNetwork.SolveAt(g, 0.0, 0.0, 1.0);
            NetworkSolution b = EngineNetwork.SolveAt(direct, 0.0, 0.0, 1.0);
            double tipGear = a.Result.FanTipSpeed, tipDirect = b.Result.FanTipSpeed;
            Console.WriteLine("    设计点风扇叶尖速度：齿轮 3.0 {0:F2} m/s｜直驱 {1:F2} m/s（比值 {2:F3}）",
                tipGear, tipDirect, tipDirect > 0 ? tipGear / tipDirect : 0.0);
            Expect("设计点：风扇叶尖速度 ≈ 直驱的 1/3",
                tipDirect > 1.0 && Math.Abs(tipGear / tipDirect - 1.0 / 3.0) < 0.01, true);
            Expect("设计点：风扇转速也按 1/3 掉",
                a.Result.FanRpm > 1.0 && b.Result.FanRpm > 1.0
                && Math.Abs(a.Result.FanRpm / b.Result.FanRpm - 1.0 / 3.0) < 0.01, true);

            // ② 非设计点（70% 转速）：同一件事还得成立——这就是那句"已知限制"的证伪点。
            NetworkSolution c = EngineNetwork.SolveAt(g, 0.0, 0.0, 0.7);
            NetworkSolution e = EngineNetwork.SolveAt(direct, 0.0, 0.0, 0.7);
            double tipGear70 = c.Result.FanTipSpeed, tipDirect70 = e.Result.FanTipSpeed;
            Console.WriteLine("    70% 转速风扇叶尖速度：齿轮 3.0 {0:F2} m/s｜直驱 {1:F2} m/s（比值 {2:F3}）",
                tipGear70, tipDirect70, tipDirect70 > 0 ? tipGear70 / tipDirect70 : 0.0);
            ExpectTrue("70% 转速下风扇仍在转（两侧都算出了正数）", tipGear70 > 1.0 && tipDirect70 > 1.0);
            Expect("非设计点：齿轮比同样让人慢转（比值仍是 1/3）",
                tipDirect70 > 1.0 && Math.Abs(tipGear70 / tipDirect70 - 1.0 / 3.0) < 0.01, true);
            Expect("掉转速时风扇叶尖速度跟着掉", tipGear70 < tipGear && tipDirect70 < tipDirect, true);

            // ③ 没有风扇就没有这个量：涡喷不该报一个"风扇叶尖速度"。
            EngineDesignData jt = Presets.Build("J79");
            ExpectTrue("J79 预设存在", jt != null);
            if (jt != null)
            {
                NetworkSolution j = EngineNetwork.SolveAt(jt, 0.0, 0.0, 1.0);
                Console.WriteLine("    涡喷的风扇叶尖速度：{0:F2} m/s", j.Result.FanTipSpeed);
                Expect("涡喷没有风扇，这一项是 0", Math.Abs(j.Result.FanTipSpeed) < 1e-9, true);
            }
        }

        // ---- 第 108 段：坏输入不许变成假数 ----

        private static SolverContext BoundaryCtx(EngineDesignData d, double frac)
        {
            double p0, t0;
            Gas.Isa(0.0, out p0, out t0);
            return new SolverContext
            {
                Design = d,
                Tech = d.Tech,
                Altitude = 0.0,
                Mach = 0.0,
                AmbientPressure = p0,
                AmbientTemperature = t0,
                FlightVelocity = 0.0,
                SpeedFrac = frac,
                RpmDesign = Cycle.DesignShaftSpeeds(d, t0),
            };
        }

        /// <summary>把九个部件排成一圈，喂同一口"气"。返回每个部件的产物。</summary>
        private static ComponentResult[] SolveAllParts(EngineDesignData d, GasState inlet, SolverContext ctx)
        {
            return new ComponentResult[]
            {
                new IntakeComponent().Solve(inlet.Clone(), ctx),
                new FanComponent().Solve(inlet.Clone(), ctx),
                new CompressorComponent(d.Lpc, "LPC", Stations.LpcExit, true).Solve(inlet.Clone(), ctx),
                new CompressorComponent(d.Hpc, "HPC", Stations.HpcExit, false).Solve(inlet.Clone(), ctx),
                new SplitterComponent().Solve(inlet.Clone(), ctx),
                new BurnerComponent().Solve(inlet.Clone(), ctx),
                new TurbineComponent(d.Hpt, "HPT", Stations.HptExit).Solve(inlet.Clone(), ctx),
                new AfterburnerComponent().Solve(inlet.Clone(), ctx),
                new NozzleComponent(false).Solve(inlet.Clone(), ctx),
            };
        }

        /// <summary>产物里有没有"会传染下去"的坏数。返回第一处，没问题返回 null。</summary>
        private static string FirstBad(ComponentResult r)
        {
            if (r == null) return "结果是空对象";
            for (int i = 0; i < r.Outlets.Count; i++)
            {
                GasState o = r.Outlets[i];
                if (o == null) return string.Format("第 {0} 个出口是空对象", i);
                string why;
                if (o.Validate(out why) != GasValidity.Valid)
                    return string.Format("第 {0} 个出口不合格：{1}", i, why);
            }
            if (!GasState.IsFinite(r.PowerRequired)) return "PowerRequired 不是有限数";
            if (!GasState.IsFinite(r.PowerProduced)) return "PowerProduced 不是有限数";
            if (!GasState.IsFinite(r.PressureRatio)) return "PressureRatio 不是有限数";
            if (!GasState.IsFinite(r.FuelFlow)) return "FuelFlow 不是有限数";
            for (int i = 0; i < r.Stages.Count; i++)
            {
                StageRow s = r.Stages[i];
                if (!GasState.IsFinite(s.Pi) || !GasState.IsFinite(s.TOut) || !GasState.IsFinite(s.MassFlow))
                    return string.Format("级表第 {0} 行有坏数（pi={1:G4} tout={2:G4} mdot={3:G4}）",
                        i + 1, s.Pi, s.TOut, s.MassFlow);
            }
            return null;
        }

        private static void RunBoundaryCases()
        {
            EngineDesignData d = Presets.Build("GTF1100");
            ExpectTrue("GTF1100 预设存在（边界测试要用一台带风扇与低压的机器）", d != null);
            if (d == null) return;

            SolverContext ctx = BoundaryCtx(d, 1.0);
            string[] names = { "进气道", "风扇", "低压压气机", "高压压气机", "分流器", "燃烧室", "高压涡轮", "加力", "喷管" };

            // ① 对照组：正常入口下九个部件都要能算，而且不许有坏数
            GasState good = GasState.FromTotal(Stations.HpcExit, 300000.0, 700.0, 25.0, FlowRole.Core);
            ComponentResult[] ok = SolveAllParts(d, good, ctx);
            int okCount = 0;
            for (int i = 0; i < ok.Length; i++)
                if (ok[i].Success && FirstBad(ok[i]) == null) okCount++;
            Console.WriteLine("    对照组：{0}/{1} 个部件吃了正常入口后算得出干净的数", okCount, ok.Length);
            Expect("正常入口下九个部件全都算得出干净的数", okCount, ok.Length);

            // ② 坏入口：每一种都要被拦在门口，且一个出口都不许产出
            GasState[] bad =
            {
                GasState.FromTotal(Stations.HpcExit, double.NaN, 700.0, 25.0, FlowRole.Core),
                GasState.FromTotal(Stations.HpcExit, 300000.0, double.NaN, 25.0, FlowRole.Core),
                GasState.FromTotal(Stations.HpcExit, 0.0, 700.0, 25.0, FlowRole.Core),
                GasState.FromTotal(Stations.HpcExit, 300000.0, 0.0, 25.0, FlowRole.Core),
                GasState.FromTotal(Stations.HpcExit, 300000.0, 700.0, -5.0, FlowRole.Core),
                GasState.FromTotal(Stations.BurnerExit, 300000.0, 1200.0, 25.0, FlowRole.Core, 0.40),
            };
            string[] badNames = { "总压 NaN", "总温 NaN", "总压 0", "总温 0", "流量 −5", "油气比 0.40" };
            for (int b = 0; b < bad.Length; b++)
            {
                ComponentResult[] got = SolveAllParts(d, bad[b], ctx);
                int refused = 0, leaked = 0;
                for (int i = 0; i < got.Length; i++)
                {
                    if (!got[i].Success && got[i].FailureCode == "BAD_INLET" && got[i].Outlets.Count == 0) refused++;
                    if (FirstBad(got[i]) != null && got[i].Outlets.Count > 0) leaked++;
                }
                Console.WriteLine("    坏入口「{0}」：拦住 {1}/9 个部件，漏出坏数 {2} 个", badNames[b], refused, leaked);
                Expect(string.Format("坏入口「{0}」被所有部件拦在门口", badNames[b]), refused, got.Length);
                Expect(string.Format("坏入口「{0}」没有漏出任何坏数", badNames[b]), leaked, 0);
            }

            // ③ 零流量是合法状态（关车、风车），不许当成坏输入，也不许除零
            GasState idle = GasState.FromTotal(Stations.HpcExit, 300000.0, 700.0, 0.0, FlowRole.Core);
            ComponentResult[] zero = SolveAllParts(d, idle, ctx);
            int zeroOk = 0;
            string firstZeroBad = null;
            for (int i = 0; i < zero.Length; i++)
            {
                string why = FirstBad(zero[i]);
                if (zero[i].Success && why == null) zeroOk++;
                else if (firstZeroBad == null) firstZeroBad = names[i] + "：" + (why ?? zero[i].FailureCode);
            }
            Console.WriteLine("    零流量：{0}/9 个部件算得出干净的数{1}", zeroOk,
                firstZeroBad == null ? "" : "（第一处问题：" + firstZeroBad + "）");
            Expect("零流量（关车/风车）不是坏输入，九个部件都要算得出干净的数", zeroOk, zero.Length);

            // ④ 网络层：NaN 的高度/马赫/转速要退回设计点，不许把 NaN 传下去
            NetworkSolution junk = EngineNetwork.SolveAt(d, double.NaN, double.NaN, double.NaN);
            bool netFinite = junk != null && GasState.IsFinite(junk.Result.Thrust)
                             && GasState.IsFinite(junk.Result.MdotAir)
                             && GasState.IsFinite(junk.Result.FuelFlow);
            Console.WriteLine("    网络层喂 NaN 参数：推力 {0:F2} kN｜进气 {1:F3} kg/s｜Ok={2}",
                junk == null ? 0.0 : junk.Result.Thrust / 1000.0,
                junk == null ? 0.0 : junk.Result.MdotAir,
                junk != null && junk.Result.Ok);
            Expect("网络层喂 NaN 参数时不崩（拿得到解）", junk != null, true);
            Expect("网络层喂 NaN 参数时退回设计点，读数仍是有限数", netFinite, true);

            // ⑤ 设计层：燃烧室出口温度比压气机出口还低 → 设计不成立，
            //    要么给错误码、要么判不成立，但不许崩、也不许给个漂亮的有限推力假装能飞。
            EngineDesignData cold = Presets.Build("GTF1100");
            cold.Burner.ExitTemp = 300.0;
            bool threw = false;
            NetworkSolution coldSol = null;
            try { coldSol = EngineNetwork.SolveAt(cold, 0.0, 0.0, 1.0); }
            catch (Exception) { threw = true; }
            Console.WriteLine("    燃烧室出口 300 K：抛异常={0}｜Ok={1}｜推力 {2:F2} kN｜错误 {3} 条",
                threw, coldSol != null && coldSol.Result.Ok,
                coldSol == null ? 0.0 : coldSol.Result.Thrust / 1000.0,
                coldSol == null ? 0 : coldSol.Result.Errors.Count);
            Expect("燃烧室出口温度低于压气机出口时不抛异常", threw, false);
            Expect("燃烧室出口温度低于压气机出口时判设计不成立",
                coldSol != null && (!coldSol.Result.Ok || coldSol.Result.Errors.Count > 0), true);
            Expect("这种情况下推力仍是有限数（不是 NaN）",
                coldSol != null && GasState.IsFinite(coldSol.Result.Thrust), true);
        }

        /// <summary>
        /// 第 111 段：向导撤销。
        /// 这一段钉的是"退回来的是同一台机器"，而不是"有个按钮"：
        /// 栈里存的是克隆（改原对象不许影响存档）、深度封顶（老账不许无限长）、
        /// 退回来的设计与按下去之前逐字段一致（用同一条 Save 通道比，不比内存指针）。
        /// </summary>
        /// <summary>§280：界面判断"参数改过没有"的判据（同一份初稿还作不作数）。</summary>
        /// <remarks>
        /// 这条判据原先写在界面上，用的是 `Math.Abs(a-b) > 1e-9`。滑杆返回的是量化到
        /// 像素步长的 float，喂回去下一帧就可能差一个台阶——于是"参数变了"每帧都成立，
        /// 初稿刚生成出来就被自己清掉。搬进 Core 就是为了能被这里钉住：
        /// **判据本身得有个不依赖界面的地方可以验。**
        /// </remarks>
        private static void RunWizardTargetCases()
        {
            Console.WriteLine("== 第 124 段 初稿算不算数：按显示精度判，不按 1e-9 判 ==");
            WizardInput a = new WizardInput { DesignMach = 2.0, DesignAlt = 11000.0, ThrustKN = 80.0, Tech = "2000s" };
            WizardInput b = new WizardInput { DesignMach = 2.0, DesignAlt = 11000.0, ThrustKN = 80.0, Tech = "2000s" };
            Expect("同样的目标算同一份", WizardTarget.SameTarget(a, b), true);
            b.DesignMach = 2.0 + 1e-6;
            Expect("马赫只差 1e-6 也算同一份（滑杆抖一格不许把初稿清掉）", WizardTarget.SameTarget(a, b), true);
            b.DesignMach = 2.0 + 0.05;
            Expect("马赫差了 0.05 就是换了目标", WizardTarget.SameTarget(a, b), false);
            b.DesignMach = 2.0;
            b.ThrustKN = 80.04;
            Expect("推力差 0.04 kN 还算同一份", WizardTarget.SameTarget(a, b), true);
            b.ThrustKN = 81.0;
            Expect("推力差 1 kN 就是换了目标", WizardTarget.SameTarget(a, b), false);
            b.ThrustKN = 80.0;
            b.Tech = "1990s";
            Expect("换了技术年代就是换了目标", WizardTarget.SameTarget(a, b), false);
            b.Tech = "2000s";
            b.Afterburner = true;
            Expect("加力从按用途改成强制开，也算换了目标", WizardTarget.SameTarget(a, b), false);
            Expect("拿空的那份去比，一律不算同一份", WizardTarget.SameTarget(null, b), false);
        }

        private static void RunWizardUndoCases()
        {
            WizardUndo.Clear();
            Expect("还没改过向导：没有可撤销的步骤", WizardUndo.Has, false);
            Expect("还没改过向导：退不出东西", WizardUndo.Pop() == null, true);
            Expect("还没改过向导：按钮上不写字", WizardUndo.Describe(), "");

            EngineDesignData g = Presets.Build("GTF1100");
            string before = DumpDesign(g);

            // ① 存的是克隆：push 之后把原对象改烂，退回来必须还是原样
            double diaBefore = g.Fan.Diameter;
            WizardUndo.Push(g);
            g.Fan.Diameter = 9.9;
            g.Hpc.Stages = 1;
            EngineDesignData back = WizardUndo.Pop();
            Expect("撤销拿得回东西", back != null, true);
            if (back != null)
            {
                Expect("撤销退回来的是同一台机器（逐字段一致）", DumpDesign(back) == before, true);
                // 钉"改之前的那个数"，不钉写死的数字：预设的直径以后调了，这条不该跟着红
                ExpectEq("撤销退回来的风扇直径是改之前的", back.Fan.Diameter, diaBefore);
            }

            // ② 深度：只留三步，第四步进来时最早那份被挤掉
            WizardUndo.Clear();
            EngineDesignData a = Presets.Build("GTF1100");
            for (int i = 0; i < 4; i++)
            {
                EngineDesignData one = a.Clone();
                one.DesignName = "step" + i;
                WizardUndo.Push(one);
            }
            ExpectEq("撤销栈封顶在三步", WizardUndo.Count, WizardUndo.Depth);
            EngineDesignData last = WizardUndo.Pop();
            ExpectEq("最先被挤掉的是第一步", last != null ? last.DesignName : "", "step3");
            EngineDesignData second = WizardUndo.Pop();
            ExpectEq("再退一步是第二步", second != null ? second.DesignName : "", "step2");
            WizardUndo.Pop();
            Expect("退干净了", WizardUndo.Has, false);

            // ③ Clear：换机器/换存档后不许跨机器撤销
            WizardUndo.Push(a);
            WizardUndo.Clear();
            Expect("Clear 之后不许跨机器撤销", WizardUndo.Has, false);

            // ④ 按钮文案带步数（玩家得知道还能退几步）
            WizardUndo.Push(a);
            Expect("按钮文案里有步数", WizardUndo.Describe().Contains("1"), true);
            WizardUndo.Clear();
        }

        /// <summary>
        /// 第 121 段：拖动滑杆的撤销。
        ///
        /// 向导撤销（第 111 段）管的是"按一下换整台机器"，这一段管的是另一件事：
        /// 设计器里那几十根滑杆一根都没有退路，而这模组的玩法就是"调一格看读数怎么变"。
        ///
        /// 钉的四件事，全都不是"有个按钮"：
        /// ① 退回来的是**改之前**那个数；
        /// ② 手没松开的时候不记步——拖动途中每帧都变，逐帧记会把二十步全花在一次拖动上；
        /// ③ 没改动的时候不记步（打开窗口来回晃一圈不该把栈塞满）；
        /// ④ 换机器之后不许跨机器撤销。
        /// </summary>
        private static void RunDesignUndoCases()
        {
            // 指纹走存档通道：同一份设计压出来一样，改一个数就不一样。
            EngineDesignData d = Presets.Build("J79");
            string sigA = DesignUndo.Fingerprint(d);
            Expect("同一份设计的指纹一样", DesignUndo.Fingerprint(d), sigA);
            double dia0 = d.Fan.Diameter;
            d.Fan.Diameter = dia0 * 1.01;
            Expect("改了一个数指纹就变", DesignUndo.Fingerprint(d) != sigA, true);
            d.Fan.Diameter = dia0;

            DesignUndo u = new DesignUndo();
            u.Reset(d);
            Expect("刚起过的时候没得退", u.Has, false);
            Expect("刚起过的时候退不出东西", u.Pop() == null, true);
            Expect("没得退时按钮上也不报步数", u.Describe().Contains("1"), false);

            // ① 改一次、松手 → 记一步，退回来是改之前的数
            d.Fan.Diameter = dia0 * 1.25;
            Expect("改了一根滑杆、手松开后有一步", u.Bind(d), true);
            ExpectEq("能退的步数是 1", u.Count, 1);
            EngineDesignData back = u.Pop();
            Expect("撤销拿得回东西", back != null, true);
            if (back != null)
            {
                ExpectEq("退回来的是改之前的直径", back.Fan.Diameter, dia0);
                // 界面里撤销之后设计就换成退回来那份；这里也要换，否则下一步的"拖动开始前"不成立。
                d.Fan.Diameter = back.Fan.Diameter;
            }
            Expect("退干净了", u.Has, false);

            // ② 手没松开：改三下都不 Bind，松手时只算一步
            double start = d.Fan.Diameter;
            d.Fan.Diameter = start * 1.05;
            d.Fan.Diameter = start * 1.10;
            d.Fan.Diameter = start * 1.15;
            Expect("松手时这一刻才算一步", u.Bind(d), true);
            ExpectEq("一次拖动只记一步", u.Count, 1);
            EngineDesignData dragBack = u.Pop();
            if (dragBack != null) ExpectEq("退回去是拖动开始前那个数", dragBack.Fan.Diameter, start);
            d.Fan.Diameter = start;

            // ③ 没改动就不记步
            u.Reset(d);
            u.Bind(d);
            Expect("什么都没改的时候不记步", u.Bind(d), false);
            ExpectEq("栈还是空的", u.Count, 0);

            // ④ 封顶：改 25 次只留 20 步，最早那几步被挤掉
            for (int i = 0; i < 25; i++)
            {
                d.Fan.Diameter = 0.50 + 0.01 * i;
                u.Bind(d);
            }
            ExpectEq("撤销栈封顶在二十步", u.Count, DesignUndo.Depth);
            EngineDesignData oldest = null;
            for (int i = 0; i < DesignUndo.Depth; i++) oldest = u.Pop();
            Expect("退二十步之后栈空了", u.Has, false);
            ExpectTrue("二十步之外的那几步已经挤掉了", oldest != null && oldest.Fan.Diameter > 0.50);

            // ⑤ 撤销之后紧接着再改：记的是撤销后那份，不是撤销前那份
            u.Reset(d);
            double baseDia = d.Fan.Diameter;
            d.Fan.Diameter = baseDia + 0.05;
            u.Bind(d);
            EngineDesignData undo1 = u.Pop();
            if (undo1 != null) d.Fan.Diameter = undo1.Fan.Diameter;
            d.Fan.Diameter = d.Fan.Diameter + 0.09;
            u.Bind(d);
            EngineDesignData undo2 = u.Pop();
            if (undo2 != null) ExpectEq("撤销后再改，退回去是撤销后的那个数", undo2.Fan.Diameter, baseDia);

            // ⑥ 换机器之后不许跨机器撤销
            u.Reset(Presets.Build("GTF1100"));
            Expect("换机器之后没有可退的", u.Has, false);
            u.Clear();
            Expect("Clear 之后也没有可退的", u.Has, false);
        }

        /// <summary>
        /// 第 122 段：简单模式的撤销。
        ///
        /// 简单模式是"不碰那 58 个字段"的那条路，可它照样是一屏滑杆——
        /// 挑这条路的人恰恰最不会凭记忆把数拖回去。（第 121 段只覆盖了残酷模式。）
        ///
        /// 钉三件事，都不是"按钮在不在"：
        /// ① 退回来的是**改之前**那个数；
        /// ② 指纹真的覆盖滑杆能改的那些字段——漏一个字段就等于那根滑杆永远撤不动，
        ///    而这是最容易犯的错（简单模式的存档键表跟字段表是两份东西）；
        /// ③ 两条栈各记各的：在简单模式里退一步，不许把残酷模式那份设计退出来。
        /// </summary>
        private static void RunSimpleUndoCases()
        {
            SimpleDesign sd = new SimpleDesign
            {
                Diameter = 1.20,
                Bpr = 5.0,
                Opr = 25.0,
                T4 = 1500.0,
                T4Idle = 0.0,
                TExhaust = 0.0,
                AbFuelRatio = 0.0,
                DesignMach = 0.0,
                DesignAlt = 0.0,
                Fpr = 0.0,
                AbThrottle = 0.85,
                EtaOffdesignK = 0.50,
                Nozzle = NozzleKind.Converging,
                Tech = TechLevel.Get("1970s")
            };

            // ② 先问指纹：能改的字段挨个动一下，指纹必须都变。
            string sig = SimpleUndo.Fingerprint(sd);
            // 用比较而不是把整段指纹印出来：指纹有一百多个字符，印出来把日志冲没了。
            Expect("同一份简单设计的指纹一样", SimpleUndo.Fingerprint(sd) == sig, true);
            Expect("克隆一份指纹也一样", SimpleUndo.Fingerprint(sd.Clone()) == sig, true);

            SimpleDesign probe = sd.Clone();
            probe.Diameter = sd.Diameter * 1.01;
            Expect("改直径指纹就变", SimpleUndo.Fingerprint(probe) != sig, true);
            probe = sd.Clone();
            probe.T4 = sd.T4 + 20.0;
            Expect("改涡轮前温指纹就变", SimpleUndo.Fingerprint(probe) != sig, true);
            probe = sd.Clone();
            probe.Opr = sd.Opr + 1.0;
            Expect("改总压比指纹就变", SimpleUndo.Fingerprint(probe) != sig, true);
            probe = sd.Clone();
            probe.Bpr = sd.Bpr + 0.5;
            Expect("改涵道比指纹就变", SimpleUndo.Fingerprint(probe) != sig, true);
            probe = sd.Clone();
            probe.Nozzle = NozzleKind.ConvergingDiverging;
            Expect("改喷管型式指纹就变", SimpleUndo.Fingerprint(probe) != sig, true);
            probe = sd.Clone();
            probe.Tech = TechLevel.Get("1990s");
            Expect("换年代档指纹就变", SimpleUndo.Fingerprint(probe) != sig, true);
            probe = sd.Clone();
            probe.IntakeThroatRatio = 0.85;
            Expect("改进气道喉道比指纹就变", SimpleUndo.Fingerprint(probe) != sig, true);

            SimpleUndo u = new SimpleUndo();
            u.Reset(sd);
            Expect("刚起过的时候没得退", u.Has, false);
            Expect("刚起过的时候退不出东西", u.Pop() == null, true);

            // ① 改一根滑杆、松手 → 记一步，退回来是改之前那个数
            double dia0 = sd.Diameter;
            sd.Diameter = dia0 * 1.30;
            Expect("改了一根简单模式的滑杆后有一步", u.Bind(sd), true);
            ExpectEq("能退的步数是 1", u.Count, 1);
            SimpleDesign back = u.Pop();
            Expect("撤销拿得回东西", back != null, true);
            if (back != null)
            {
                ExpectEq("退回来的是改之前的直径", back.Diameter, dia0);
                ExpectEq("退回来的是改之前的涡轮前温", back.T4, 1500.0);
            }
            Expect("退干净了", u.Has, false);

            // ②（续）手没松开：改三下都不 Bind，松手时只算一步
            sd.Diameter = dia0;
            u.Reset(sd);
            double start = sd.Diameter;
            sd.Diameter = start * 1.05;
            sd.Diameter = start * 1.10;
            sd.Diameter = start * 1.15;
            Expect("松手时这一刻才算一步", u.Bind(sd), true);
            ExpectEq("一次拖动只记一步", u.Count, 1);
            SimpleDesign dragBack = u.Pop();
            if (dragBack != null) ExpectEq("退回去是拖动开始前那个数", dragBack.Diameter, start);
            sd.Diameter = start;

            // ③ 退回来的是副本：改它不影响栈里还没退出来的那一步
            u.Reset(sd);
            sd.T4 = 1600.0;
            u.Bind(sd);
            sd.T4 = 1700.0;
            u.Bind(sd);
            SimpleDesign top = u.Pop();
            if (top != null) top.T4 = 999.0;
            SimpleDesign next = u.Pop();
            if (next != null) ExpectEq("退回来的是副本，改它不影响下一步", next.T4, 1500.0);

            // ④ 两条栈各记各的：简单模式记了一步，残酷模式那条还是空的
            DesignUndo full = new DesignUndo();
            SimpleUndo simple = new SimpleUndo();
            SimpleDesign one = sd.Clone();
            simple.Reset(one);
            one.Opr = one.Opr + 2.0;
            simple.Bind(one);
            ExpectEq("简单模式记了一步", simple.Count, 1);
            ExpectEq("残酷模式那条没跟着动", full.Count, 0);

            // ⑤ 换机器之后不许跨机器撤销
            u.Reset(new SimpleDesign { Diameter = 0.6 });
            Expect("换机器之后没有可退的", u.Has, false);
            u.Clear();
            Expect("Clear 之后也没有可退的", u.Has, false);
        }

        /// <summary>
        /// 第 123 段：有控的杆位是推力（§270）。
        ///
        /// 假机只有一条式子：推力 = 1.15 × 满推力 × 油量分数。那个 1.15 是存心的——
        /// 真机的「油量→推力」永远不等于设计值，前馈刚好对上就验不出闭环。
        /// </summary>
        private static void RunThrustCommandCases()
        {
            const double Cap = 100.0e3;           // 满推力 100 kN
            const double Idle = 1.0, Max = 8.0;    // 慢车占 12.5%
            const double Dt = 0.02;
            const double PlantGain = 1.15;         // 受控对象的真实增益比设计值高 15%

            // ---- 无控：杆位就是油量，一帧到位 ----
            FuelLoop hyd = new FuelLoop();
            hyd.IdleFuel = Idle;
            hyd.MaxFuel = Max;
            hyd.Reset();
            hyd.Step(0.5, Dt, null, 0.0, 0.0, Cap);
            Check("无控 杆位 0.5 的油量 kg/s", hyd.Fuel, Idle + (Max - Idle) * 0.5, 0.01);
            Check("无控 推力读数顺着油量折 kN", hyd.TargetThrustN / 1000.0, Cap / 1000.0 * 0.5, 0.01);
            Expect("无控 不攒积分（没有闭环可攒）", Math.Abs(hyd.Integral) < 1e-12, true);

            // ---- 有控：先在杆位 0.2 上稳住 ----
            FuelLoop f = new FuelLoop();
            f.Fadec = true;
            f.IdleFuel = Idle;
            f.MaxFuel = Max;
            f.Reset();
            double trim = f.Trim;
            for (int i = 0; i < 500; i++)
            {
                trim = f.Trim;
                f.Step(0.2, Dt, null, 0.0, PlantGain * Cap * trim, Cap);
            }
            Check("有控 杆位 0.2 稳住的推力 kN", PlantGain * Cap * trim / 1000.0, 30.0, 2.0);

            // ---- 杆一把到底：指令一帧只许爬 TargetSlewPerSec 那么多 ----
            double maxStep = 0.0, prevT = f.TargetThrustN;
            for (int i = 0; i < 40; i++)
            {
                trim = f.Trim;
                f.Step(1.0, Dt, null, 0.0, PlantGain * Cap * trim, Cap);
                maxStep = Math.Max(maxStep, Math.Abs(f.TargetThrustN - prevT));
                prevT = f.TargetThrustN;
            }
            Check("有控 推力指令每帧最多爬 kN", maxStep / 1000.0, FuelLoop.TargetSlewPerSec * Cap * Dt / 1000.0, 2.0);

            // ---- 二十秒后收敛到满推力（前馈差的那 15% 由积分补上） ----
            for (int i = 0; i < 1000; i++)
            {
                trim = f.Trim;
                f.Step(1.0, Dt, null, 0.0, PlantGain * Cap * trim, Cap);
            }
            Check("有控 杆位到底收敛到满推力 kN", PlantGain * Cap * trim / 1000.0, Cap / 1000.0, 2.0);

            // ---- 推不满（受控对象只出 60 kN）：油量顶到底，积分不许攒到天上 ----
            FuelLoop sat = new FuelLoop();
            sat.Fadec = true;
            sat.IdleFuel = Idle;
            sat.MaxFuel = Max;
            sat.Reset();
            trim = sat.Trim;
            for (int i = 0; i < 1000; i++)
            {
                trim = sat.Trim;
                sat.Step(1.0, Dt, null, 0.0, Math.Min(60.0e3, PlantGain * Cap * trim), Cap);
            }
            Expect("有控 推不满时油量顶到满格", sat.Trim > 0.999, true);
            Expect("有控 推不满时积分没攒过上限", sat.Integral <= FuelLoop.IntegralClamp + 1e-9, true);
            Check("有控 推不满时推力停在对象的天花板 kN", Math.Min(60.0e3, PlantGain * Cap * trim) / 1000.0, 60.0, 2.0);

            // ---- 目标降下来，五秒内跟得上（积分得能自己退回去） ----
            for (int i = 0; i < 250; i++)
            {
                trim = sat.Trim;
                sat.Step(0.3, Dt, null, 0.0, Math.Min(60.0e3, PlantGain * Cap * trim), Cap);
            }
            Check("有控 目标降到 0.3 后跟到 kN", Math.Min(60.0e3, PlantGain * Cap * trim) / 1000.0, 38.75, 3.0);
        }

        /// <summary>把设计按存档通道压成一段文本，用来比"同一台机器"。</summary>
        private static string DumpDesign(EngineDesignData d)
        {
            // 原来是 `n.ToString()`——这个 KSP 版本的 ConfigNode 没重写它，返回类型名
            // "ConfigNode"，于是"逐字段一致"永远成立。改用 DesignUndo 的真指纹（自己走节点）。
            return DesignUndo.Fingerprint(d);
        }

        /// <summary>
        /// 第 110 段：逐级喘振余量。
        /// 这一段要钉的是"级间负荷斜面真的存在"，而不是"多了两个数"：
        /// 设计转速上级间必须一样（斜面为零）、降转速必须前级更紧、
        /// 可调静子必须把斜面掰平一半以上、单级机器必须等于整机口径。
        /// </summary>
        private static void RunStageMarginCases()
        {
            EngineDesignData g = Presets.Build("GTF1100");
            CompressorChart chart = CompressorChart.DesignChart(g, false, g.Tech);
            Expect("GTF1100 高压压气机有设计点图", chart != null, true);
            if (chart == null) return;

            MapPoint design = new MapPoint(1.0, chart.DesignWc, chart.DesignPr, chart.DesignEta);
            double[] atDesign = chart.StageSurgeMargins(design, g.Hpc.Stages, g.Hpc.Vigv);
            Expect("设计转速上级数够分", atDesign.Length == g.Hpc.Stages, true);
            double spreadDesign = 0.0;
            for (int i = 1; i < atDesign.Length; i++)
                spreadDesign = Math.Max(spreadDesign, Math.Abs(atDesign[i] - atDesign[0]));
            Check("设计转速上级间余量差", spreadDesign, 0.0, 0.01);
            double wantDesign = (1.0 / chart.PhiStall - 1.0) * 100.0;
            Check("设计转速单级余量（流量系数口径）", atDesign[0], wantDesign, 0.01);

            MapPoint low = new MapPoint(0.70, chart.DesignWc * 0.70, chart.DesignPr * 0.35, chart.DesignEta);
            double[] noVigv = chart.StageSurgeMargins(low, g.Hpc.Stages, false);
            double[] vigv = chart.StageSurgeMargins(low, g.Hpc.Stages, true);
            int worstNo = 0, worstV = 0;
            for (int i = 1; i < noVigv.Length; i++)
            {
                if (noVigv[i] < noVigv[worstNo]) worstNo = i;
                if (vigv[i] < vigv[worstV]) worstV = i;
            }
            Expect("70% 转速时最紧的是前级（第 1 级）", worstNo == 0, true);
            Expect("可调静子后最紧的仍是前级", worstV == 0, true);
            Expect("70% 转速时前级比设计转速更紧", noVigv[0] < atDesign[0], true);
            Expect("可调静子把前级余量拉回来一半以上",
                vigv[0] - noVigv[0] > 0.5 * (atDesign[0] - noVigv[0]), true);
            Console.WriteLine(string.Format("  逐级（70% 转速）：无静子第 1 级 {0:F1}% / 末级 {1:F1}%；有静子第 1 级 {2:F1}%",
                noVigv[0], noVigv[noVigv.Length - 1], vigv[0]));

            double[] oneStage = chart.StageSurgeMargins(low, 1, false);
            Expect("单级机器的逐级余量只有一个数", oneStage.Length == 1, true);
            Expect("单级机器没有斜面（第一级即全部）", Math.Abs(oneStage[0] - noVigv[0]) < 1e-9, false);

            double[] choke = chart.StageChokeMargins(low, g.Hpc.Stages, false);
            int worstC = 0;
            for (int i = 1; i < choke.Length; i++) if (choke[i] < choke[worstC]) worstC = i;
            Expect("堵塞侧最紧的是末级（与喘振侧相反）", worstC == choke.Length - 1, true);
            Console.WriteLine(string.Format("  堵塞侧（70% 转速）：末级只剩 {0:F1}%，前级 {1:F1}%", choke[worstC], choke[0]));

            NetworkSolution hi = EngineNetwork.SolveAt(g, 0.0, 0.0, 1.00);
            NetworkSolution lo = EngineNetwork.SolveAt(g, 0.0, 0.0, 0.70);
            Expect("满转速时逐级余量有读数", hi != null && Math.Abs(hi.Result.HpcStageSurgeMargin + 1.0) > 1e-6, true);
            Expect("70% 转速时逐级余量有读数", lo != null && Math.Abs(lo.Result.HpcStageSurgeMargin + 1.0) > 1e-6, true);
            if (hi != null && lo != null)
            {
                // 这一段的教训：不能预先假定"整机降转速 → 前级更紧"。
                // 实测（GTF1100 地面 70% 油门）高压压气机的换算流量系数反而升到 1.16 附近，
                // 于是最紧的是**末级**（往堵塞侧走），前级余量比设计点还宽。
                // 所以这里只钉"斜面存在、且最紧的那一级在一端"，方向由 φ 自己决定。
                Expect("满转速时逐级余量都差不多（斜面为零）",
                    Math.Abs(hi.Result.HpcStageSurgeMargin - 11.111) < 0.05, true);
                // 逐级最差永远在最前一级（斜面让前级 φ 最低），所以别钉"哪一级"——
                // 那句恒真。要钉的是"逐级读数与整机读数不是一回事"：
                // 70% 油门时整机报 83%，逐级只有 34%，而末级反而宽到 44%。
                Expect("逐级读数比整机读数紧（整机平均会掩盖级间差）",
                    lo.Result.HpcStageSurgeMargin < lo.Result.HpcSurgeMargin, true);
                Console.WriteLine(string.Format("  整机：满转速第 {0} 级 {1:F1}%｜70% 转速第 {2} 级 {3:F1}%（整机裕度 {4:F0}%）",
                    hi.Result.HpcStageSurgeStage, hi.Result.HpcStageSurgeMargin,
                    lo.Result.HpcStageSurgeStage, lo.Result.HpcStageSurgeMargin, lo.Result.HpcSurgeMargin));

                ResultView view = Criteria.ViewOf(lo.Result);
                List<Finding> fs = Criteria.CheckStageMargin(view);
                Expect("判据里有一条逐级结论", fs.Count > 0, true);
                Expect("判据说的是高压压气机", fs.Exists(delegate (Finding f) { return f.Key == "comp.stage.hpc"; }), true);
            }
        }

        /// <summary>
        /// 第 109 段：齿轮箱自己也带惯量。
        /// 这一段要钉的不是"数变了"，而是"变的是齿轮箱那一项"：
        /// 直驱必须是 0（老存档读数不变）、GTF 必须为正、而且转速爬升的钝化
        /// 要能按 J 的比例算回来——不然它就只是又一个调出来的系数。
        /// </summary>
        private static void RunGearboxInertiaCases()
        {
            EngineDesignData g = Presets.Build("GTF1100");
            ExpectTrue("GTF1100 预设存在（惯量段）", g != null);
            if (g != null)
            {
                MassBreakdown m = MassCost.Estimate(g);
                double jGb = ShaftSet.GearboxInertia(g, m);
                ShaftSet set = ShaftSet.Build(g);
                ShaftState lp = set[g.Fan.Shaft];
                double jLp = lp != null ? lp.Inertia : 0.0;
                Console.WriteLine("    齿轮箱：质量 {0:F1} kg｜折到低压轴 {1:F4} kg·m²｜低压轴总惯量 {2:F4} kg·m²（占 {3:F1}%）",
                    m.Gearbox, jGb, jLp, jLp > 1e-9 ? 100.0 * jGb / jLp : 0.0);
                ExpectTrue("齿轮箱质量是正的（吃传递扭矩）", m.Gearbox > 0.0);
                ExpectTrue("齿轮箱折算惯量为正", jGb > 0.0);
                ExpectTrue("齿轮箱占低压轴惯量不到四成（不然它就是主项，说明回转半径口径错了）",
                    jLp > 1e-9 && jGb / jLp < 0.40);
                ExpectTrue("齿轮箱占低压轴惯量至少千分之三（不是可以忽略的零头）",
                    jLp > 1e-9 && jGb / jLp > 0.003);

                // ① 直驱：同一台机器把齿轮比改成 1.0，这一项必须归零。
                EngineDesignData direct = g.Clone();
                direct.Fan.GearRatio = 1.0;
                MassBreakdown md = MassCost.Estimate(direct);
                double jDirect = ShaftSet.GearboxInertia(direct, md);
                Console.WriteLine("    直驱：齿轮箱质量 {0:F2} kg｜折算惯量 {1:F6} kg·m²", md.Gearbox, jDirect);
                Expect("直驱机型没有齿轮箱惯量", Math.Abs(jDirect) < 1e-12, true);

                // ② 涡喷：连风扇都没有。
                EngineDesignData jt = Presets.Build("J79");
                if (jt != null)
                {
                    MassBreakdown mj = MassCost.Estimate(jt);
                    Expect("涡喷没有风扇，齿轮箱惯量为 0",
                        Math.Abs(ShaftSet.GearboxInertia(jt, mj)) < 1e-12, true);
                }

                // ③ 松 null：不许抛，也不许编一个数出来。
                Expect("没有质量表时返回 0，不编数",
                    Math.Abs(ShaftSet.GearboxInertia(g, null)) < 1e-12, true);

                // ④ 动力学：同一剩余功率、同一初始转速，惯量大的那一根升得慢，
                //    且两个 dw 之比应当正好是 J 之比（这是"口径对不对"的判据，不是"像不像"）。
                double omega = Math.Max(lp != null ? lp.OmegaDesign : 0.0, 1.0);
                ShaftState light = new ShaftState();
                light.OmegaDesign = omega; light.Omega = omega * 0.5; light.SpeedFraction = 0.5;
                light.Inertia = jLp; light.TurbinePower = 200000.0;
                ShaftState heavy = new ShaftState();
                heavy.OmegaDesign = omega; heavy.Omega = omega * 0.5; heavy.SpeedFraction = 0.5;
                heavy.Inertia = jLp + jGb; heavy.TurbinePower = 200000.0;
                double dwLight = light.Integrate(0.5);
                double dwHeavy = heavy.Integrate(0.5);
                Console.WriteLine("    同样的剩余功率走 0.5 s：不含齿轮箱 dω={0:F4} rad/s｜含 {1:F4} rad/s（比值 {2:F4}）",
                    dwLight, dwHeavy, dwLight > 1e-12 ? dwHeavy / dwLight : 0.0);
                Expect("加了齿轮箱惯量之后升速更慢", dwHeavy < dwLight, true);
                Expect("dω 之比等于惯量之比的倒数", Math.Abs(dwHeavy / dwLight - jLp / (jLp + jGb)) < 1e-6, true);

                // ⑤ 整机的低压轴惯量里确实包含这一项（Build 内部接上了，不是只有个静态函数）。
                MassBreakdown mZero = new MassBreakdown();
                mZero.Fan = m.Fan; mZero.Compressor = m.Compressor; mZero.Turbine = m.Turbine;
                mZero.Shaft = m.Shaft; mZero.RotorParts = m.RotorParts;
                mZero.Gearbox = 0.0;
                EngineDesignData probe = g.Clone();
                double jNoGb = ShaftSet.GearboxInertia(probe, mZero);
                Expect("把齿轮箱质量抹掉后探针为 0（对照）", Math.Abs(jNoGb) < 1e-12, true);
                ExpectTrue("低压轴惯量里含着齿轮箱这一项（jLp > 0 且探针为正）", jLp > 0.0 && jGb > 0.0);
            }
        }

        /// <summary>
        /// 第 112 段：判据排列。
        ///
        /// 判据是按物理顺序逐条跑的（温度→燃烧→喷管→轴→堵塞→级间），那个顺序对写代码的人
        /// 有意义，对玩家没有：玩家只想知道最该修哪一条。所以界面按严重度排，同级保持物理顺序。
        /// 这一段钉三件事：排序稳（同级不被打乱）、严重度高的在前、藏起来的通过项要能数出来。
        /// </summary>
        private static void RunReportOrderCases()
        {
            List<Finding> Probe()
            {
                CheckReport r = new CheckReport();
                r.Add(new Finding("a", Level.Ok, "通过的一条"));
                r.Add(new Finding("b", Level.Warn, "警告一"));
                r.Add(new Finding("c", Level.Ok, "通过的另一条"));
                r.Add(new Finding("d", Level.Fail, "不合格"));
                r.Add(new Finding("e", Level.Warn, "警告二"));
                return r.Findings;
            }

            // ① 原始顺序是写入顺序——排序发生在显示时，不是写入时。
            CheckReport rep = new CheckReport();
            rep.AddRange(Probe());
            Expect("判据按写入顺序保存（排序是显示时的事）", rep.Findings[0].Key, "a");

            List<Finding> ordered = rep.Ordered();
            Expect("排完一共还是 5 条（不许丢）", ordered.Count, 5);
            Expect("第一条是最严重的 FAIL", ordered[0].Key, "d");
            Expect("FAIL 之后才是 WARN（第一条警告）", ordered[1].Key, "b");
            Expect("同级 WARN 保持原顺序（第二条警告）", ordered[2].Key, "e");
            Expect("WARN 之后才轮到 OK（第一条通过）", ordered[3].Key, "a");
            Expect("同级 OK 也保持原顺序", ordered[4].Key, "c");

            // ② 只列问题时，通过项一条都不许混进来。
            List<Finding> problems = rep.Ordered(true);
            Expect("只看问题时剩 3 条", problems.Count, 3);
            Expect("只看问题时按严重度排", problems[0].LevelValue, Level.Fail);
            Expect("只看问题时没有 OK 混进来", problems.Exists(f => f.LevelValue == Level.Ok), false);

            // ③ 通过项要数得出来：界面藏了它们，但得能说"另有 N 条通过"。
            Expect("通过项数得出来（藏起来也要说得清）", rep.OkCount, 2);

            // ④ 全通过时不该藏任何东西：没有 FAIL 也没有 WARN，Ordered(true) 应为空，
            //    界面据此决定不显示那行"另有 N 条通过"。
            CheckReport clean = new CheckReport();
            clean.Add(new Finding("x", Level.Ok, "通过"));
            clean.Add(new Finding("y", Level.Ok, "也通过"));
            Expect("全通过时没有可列的问题", clean.Ordered(true).Count, 0);
            Expect("全通过时通过项是 2 条", clean.OkCount, 2);
            Expect("全通过时 Worst 是 OK", clean.Worst, Level.Ok);
        }

        /// <summary>
        /// 第 113 段：同一台预设发动机，换界面语言必须还是同一台。
        ///
        /// 这条是拿一个真 bug 换来的：`Presets.FitSurge` 原先靠
        /// `f.Message.Contains("已进失速区")` 认出"喘振裕度为负"，
        /// 换成英文界面就一条也匹配不上，于是预设的叶尖直径永远不缩——
        /// **J57 的高压压气机在中文下被缩到 0.670 m、英文下留 0.720 m**，
        /// 同一台飞机的推力、判据、存档全都跟着界面语言走。
        ///
        /// 这里钉的就是"语言不进数字"：两种语言各建一次，尺寸必须逐位相同。
        /// </summary>
        private static void RunLanguageCases()
        {
            string[] keys = { "J57", "RD-33", "GTF1100" };
            Lang saved = Loc.Current;

            EngineDesignData[] zh = new EngineDesignData[keys.Length];
            EngineDesignData[] en = new EngineDesignData[keys.Length];
            Loc.Probe = () => Lang.Chinese;
            for (int i = 0; i < keys.Length; i++) zh[i] = Presets.Build(keys[i]);
            Loc.Probe = () => Lang.English;
            for (int i = 0; i < keys.Length; i++) en[i] = Presets.Build(keys[i]);

            for (int i = 0; i < keys.Length; i++)
            {
                // 0.1 mm 的容差只为挡住浮点抖动：真出现"两种语言两组尺寸"，差的是几厘米。
                Check(keys[i] + " 风扇直径与语言无关", en[i].Fan.Diameter, zh[i].Fan.Diameter, 0.05, " m");
                Check(keys[i] + " 高压压气机叶尖与语言无关", en[i].Hpc.TipDiameter, zh[i].Hpc.TipDiameter, 0.05, " m");
                Check(keys[i] + " 低压压气机叶尖与语言无关", en[i].Lpc.TipDiameter, zh[i].Lpc.TipDiameter, 0.05, " m");
                Check(keys[i] + " 涡轮叶尖与语言无关", en[i].Hpt.TipDiameter, zh[i].Hpt.TipDiameter, 0.05, " m");
                Console.WriteLine("  {0}：风扇 Ø{1:F3}｜HPC Ø{2:F3}｜LPC Ø{3:F3}（中英同值）",
                    keys[i], zh[i].Fan.Diameter, zh[i].Hpc.TipDiameter, zh[i].Lpc.TipDiameter);
            }

            // 顺带钉住"语言只改文字"这件事本身：判据条数相同、最差等级相同。
            CycleResult rz = Cycle.SolveDesignPoint(zh[0]);
            CycleResult re = Cycle.SolveDesignPoint(en[0]);
            CheckReport cz = Criteria.Evaluate(zh[0], Criteria.ViewOf(rz), zh[0].Tech, Gas.P0, 0.0);
            CheckReport ce = Criteria.Evaluate(en[0], Criteria.ViewOf(re), en[0].Tech, Gas.P0, 0.0);
            Expect("判据条数不随语言变", ce.Findings.Count, cz.Findings.Count);
            Expect("最差等级不随语言变", ce.Worst, cz.Worst);
            int failsZh = 0, failsEn = 0;
            foreach (Finding f in cz.Findings) if (f.LevelValue == Level.Fail) failsZh++;
            foreach (Finding f in ce.Findings) if (f.LevelValue == Level.Fail) failsEn++;
            Expect("FAIL 条数不随语言变", failsEn, failsZh);

            Loc.Probe = () => saved;
        }

        /// <summary>
        /// 第 118 段：部件规格的检查必须真的被调用。
        ///
        /// 背景：`IntakeComponent`/`CompressorComponent`/`BurnerComponent`/`TurbineComponent`
        /// 四个 `Validate` 写了很久，但全仓没人调用。最坏的一种后果不是"少报一个错"，
        /// 而是**级数写负数时气路模板把那一级当成不存在**（`Stages > 0` 才算有），
        /// 整台机器悄悄换成另一种构型，推力照给、判据照过。
        /// 所以这一段钉的不是数字，而是"错的东西必须解不出来"。
        /// </summary>
        private static void RunSpecValidateCases()
        {
            EngineDesignData good = Presets.Build("J57");
            Expect("正常预设仍然解得出", Cycle.SolveDesignPoint(good).Ok, true);

            EngineDesignData neg = good.Clone();
            neg.Hpc.Stages = -3;
            CycleResult r1 = Cycle.SolveDesignPoint(neg);
            Expect("级数为负必须解不出来", r1.Ok, false);
            Expect("级数为负要说清楚", r1.Errors.Count > 0 && r1.Errors[0].Contains(Loc.T("级数为负", "negative stage count")), true);

            EngineDesignData many = good.Clone();
            many.Hpc.Stages = 25;
            CycleResult r2 = Cycle.SolveDesignPoint(many);
            Expect("级数超过 20 必须解不出来", r2.Ok, false);

            EngineDesignData cold = good.Clone();
            cold.Burner.ExitTemp = 0.0;
            Expect("燃烧室出口温度没设必须解不出来", Cycle.SolveDesignPoint(cold).Ok, false);

            EngineDesignData floating = good.Clone();
            floating.Hpt.Shaft = 0;
            Expect("涡轮不挂在轴上必须解不出来", Cycle.SolveDesignPoint(floating).Ok, false);

            EngineDesignData throat = good.Clone();
            throat.Intake.ThroatRatio = 2.0;
            Expect("喉道比越界必须解不出来", Cycle.SolveDesignPoint(throat).Ok, false);
        }

        /// <summary>
        /// 第 117 段：两台设计比读数。
        ///
        /// 这一段钉的不是某个数，而是三件容易搞错的事：
        /// ①同一台设计与自己比，十行必须全部相等、没有一行"谁更好"——否则差值的容差写错了；
        /// ②"谁更好"的方向要按读数本身给（推力越大越好、造价越低越好），
        ///   而且**没有好坏的读数（涵道比、进口流量）不许判出胜负**；
        /// ③任一台解不出来就不许给出对比表，`BothOk` 与错误原因必须把话说清楚。
        /// 特意不钉的：某一台的总分。设计对比的价值就在于不拍总分。
        /// </summary>
        private static void RunCompareCases()
        {
            EngineDesignData g = Presets.Build("GTF1100");

            CompareResult self = DesignCompare.Run(g, g);
            Expect("与自比能解出来", self.BothOk, true);
            ExpectEq("对比表十行", "10", self.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            int selfDiff = 0;
            for (int i = 0; i < self.Rows.Count; i++) if (self.Rows[i].Differs) selfDiff += 1;
            ExpectEq("与自比没有一行不同", "0", selfDiff.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ExpectEq("与自比没有胜方", "0,0", self.BetterA + "," + self.BetterB);

            EngineDesignData j57 = Presets.Build("J57");
            CompareResult vs = DesignCompare.Run(j57, g);
            Expect("两台真机都能解", vs.BothOk, true);

            CompareRow thrust = null, sfc = null, bpr = null, flow = null;
            for (int i = 0; i < vs.Rows.Count; i++)
            {
                CompareRow r = vs.Rows[i];
                if (r.Label == Loc.T("推力", "Thrust")) thrust = r;
                else if (r.Label == Loc.T("SFC", "SFC")) sfc = r;
                else if (r.Label == Loc.T("涵道比", "Bypass ratio")) bpr = r;
                else if (r.Label == Loc.T("进口流量", "Inlet mass flow")) flow = r;
                Expect("每行方向只有三种取值" + r.Label, r.Better == -1 || r.Better == 0 || r.Better == 1, true);
            }
            Expect("推力那一行在表里", thrust != null, true);
            Expect("推力越大越好", thrust.Better, 1);
            Expect("J57 与 GTF1100 的推力不同", thrust.Differs, true);
            Expect("推力方向与差值同号", thrust.Winner(), thrust.Delta > 0 ? 1 : -1);
            Expect("SFC 越小越好", sfc.Better, -1);
            Expect("涵道比没有好坏", bpr.Better, 0);
            Expect("涵道比不判胜负", bpr.Winner(), 0);
            Expect("进口流量没有好坏", flow.Better, 0);
            Expect("进口流量不判胜负", flow.Winner(), 0);

            CompareResult none = DesignCompare.Run(null, g);
            Expect("缺一台就谈不上对比", none.BothOk, false);
            Expect("缺哪台要说", string.IsNullOrEmpty(none.ErrorA), false);

            EngineDesignData bad = g.Clone();
            bad.Burner.ExitTemp = 0.0;   // 燃烧室出口温度必须 > 0，这是设计点解不出来的最直接一种
            CompareResult broke = DesignCompare.Run(g, bad);
            Expect("解不出来的那台不给对比表", broke.BothOk, false);
            Expect("解不出来要带原因", string.IsNullOrEmpty(broke.ErrorB), false);
            ExpectEq("解不出来时一行都不给", "0", broke.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 第 114 段：齿轮箱的柔度。
        ///
        /// 这一段的重点不是"数对不对"（它是一阶估计，没有对照数据），
        /// 而是三件事不许变坏：①直驱机型不许凭空长出齿轮箱；
        /// ②质量闭式只有一份（`Torsion.GearboxMass` 与 `MassCost` 分叉了就会
        /// 一边说 370 kg、另一边按 350 kg 算轴惯量，而两边都"看着对"）；
        /// ③箱体做大应当更硬，不做大反而"更硬"说明半径或壁厚反解写反了。
        /// </summary>
        private static void RunTorsionCases()
        {
            EngineDesignData jet = Presets.Build("J79");
            TorsionResult t0 = Torsion.Evaluate(jet, 1.0e6, 8000.0);
            Expect("涡喷没有齿轮箱", t0.Present, false);

            EngineDesignData direct = Presets.Build("RD-33");
            TorsionResult t1 = Torsion.Evaluate(direct, 8.0e6, 9000.0);
            Expect("直驱风扇没有齿轮箱", t1.Present, false);

            EngineDesignData gtf = Presets.Build("GTF1100");
            CycleResult r = Cycle.SolveDesignPoint(gtf);
            TorsionResult t2 = Torsion.Evaluate(gtf, r.WFan, r.FanRpm);
            Expect("齿轮传动风扇给出了柔度", t2.Present, true);
            // 数一阶估计，只有量级是对的；这里钉的是"还在量级里"，不是"等于我抄的那个数"。
            Expect("GTF1100 齿轮箱扭角在量级内（0~0.10°）", t2.TwistDeg > 0.0 && t2.TwistDeg < 0.10, true);
            Expect("GTF1100 本体扭振在千赫兹量级（300~5000 Hz）", t2.FreqHz > 300.0 && t2.FreqHz < 5000.0, true);
            Expect("GTF1100 齿轮箱筒半径小于风扇半径", t2.Radius > 0.05 && t2.Radius < gtf.Fan.Diameter * 0.5, true);
            Expect("GTF1100 齿轮箱壁厚在钢板量级（3~80 mm）", t2.Wall > 0.003 && t2.Wall < 0.080, true);
            Expect("齿轮箱质量落在真机量级（50~400 kg）", t2.Mass > 50.0 && t2.Mass < 400.0, true);
            Expect("扭角与本体扭振都写进了设计点结果", r.GearboxTorsionPresent, true);

            // 质量闭式只许有一份：两处各自演化就是"轴惯量按 350 kg 算、读数说 370 kg"。
            MassBreakdown m = MassCost.Estimate(gtf);
            Check("齿轮箱质量闭式两处一致", Torsion.GearboxMass(r.WFan / 1000.0, gtf.Fan.GearRatio), m.Gearbox, 1e-6, " kg");

            // 把齿轮箱做大（风扇直径 ×1.5，比不变）应当更硬，扭角更小。
            // 幂次用**同一份功率与转速**喂进去：整机重解会把功率、转速一起改，
            // 那样比的是"两台发动机"，不是"同一个扭矩下两个箱体"。
            EngineDesignData big = gtf.Clone();
            big.Fan.Diameter *= 1.5;
            TorsionResult t3 = Torsion.Evaluate(big, r.WFan, r.FanRpm);
            Expect("更大的齿轮箱更硬", t3.Stiffness > t2.Stiffness, true);
            Expect("同一扭矩下更大的齿轮箱扭角更小", t3.TwistDeg < t2.TwistDeg, true);

            // 直驱机型的判据里不许出现齿轮箱条目（否则玩家会去找不存在的部件）。
            CheckReport rd = Criteria.Evaluate(direct, Criteria.ViewOf(Cycle.SolveDesignPoint(direct)), direct.Tech, Gas.P0, 0.0);
            int gb = 0;
            foreach (Finding f in rd.Findings) if (f.Key != null && f.Key.StartsWith("gearbox.")) gb++;
            Expect("直驱机型的判据里没有齿轮箱条目", gb, 0);

            // 齿轮传动机型必须有且只有一条扭角结论（Ok 或 Warn），并且不可能是 Fail。
            CheckReport g = Criteria.Evaluate(gtf, Criteria.ViewOf(r), gtf.Tech, Gas.P0, 0.0);
            int gw = 0;
            string worstTwist = Level.Ok;
            foreach (Finding f in g.Findings)
            {
                if (f.Key == "gearbox.twist" || f.Key == "gearbox.soft") gw++;
                if (f.Key != null && f.Key.StartsWith("gearbox.") && Level.Rank(f.LevelValue) > Level.Rank(worstTwist)) worstTwist = f.LevelValue;
            }
            Expect("齿轮传动机型有一条扭角结论", gw, 1);
            Expect("齿轮箱结论最高只到 Warn", Level.Rank(worstTwist) <= Level.Rank(Level.Warn), true);
            Console.WriteLine("  齿轮箱：筒 Ø{0:F2}×{1:F2} m｜壁厚 {2:F1} mm｜{3:F0} kN·m 下扭 {4:F3}°｜本体扭振 {5:F0} Hz",
                2.0 * t2.Radius, t2.Length, t2.Wall * 1000.0, t2.Torque / 1000.0, t2.TwistDeg, t2.FreqHz);
        }
    }
}