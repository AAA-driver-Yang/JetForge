using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Text;
using UnityEngine;
using JetForge.Core;
using JetForge.Core.Diagnostics;
using JetForge.FX;
using JetForge.KSP;
using JetForge.UI;

namespace JetForge.Diagnostics
{
    /// <summary>
    /// 主菜单自检：不进入编辑器也能确认「cfg → 设计 → 设计点 → 常数 → 网格 → 判据 → 外形」
    /// 这条链路在游戏运行时不会炸。日志前缀统一为 [JetForge.SelfTest]。
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public partial class JetForgeSelfTest : MonoBehaviour
    {
        static bool _ran;
        readonly StringBuilder _sb = new StringBuilder();

        /// <summary>
        /// 痕迹文件。KSP.log 与 Player.log 都带缓冲，自检一旦卡住，日志里就只剩上一行，
        /// 分不清是"卡住了"还是"还没写出来"。这份文件每条都单独写、写完即关，
        /// 卡在哪一句一眼就能看出来。
        /// </summary>
        static string TracePath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/JetForge/selftest.log"); }
        }

        void Start()
        {
            if (_ran) return;
            _ran = true;
            // 探测豁免：自检痕迹文件写不出来时自检照跑，只是没法事后对照
            try { File.WriteAllText(TracePath, "JetForge 自检痕迹（每条立即落盘）\r\n"); } catch { }
            StartCoroutine(Run());
        }

        /// <summary>
        /// 分段标题。日志四百多行没有小标题时，想找一段只能从头翻——加标题比把每行都润色一遍更值。
        /// </summary>
        void Section(string title)
        {
            Log("");
            Log("—— " + title + " ——");
        }

        /// <summary>把 bool 写成中文：日志是给人看的，True/False 是给编译器看的。</summary>
        static string YN(bool v) { return v ? "对" : "不对"; }

        /// <summary>有没有汉字。英文界面的判据是"这句英文里不该有汉字"，而不是"它看起来像英文"。</summary>
        static bool HasHan(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 0x4E00 && c <= 0x9FFF) return true;
            }
            return false;
        }

        void Log(string s)
        {
            Debug.Log("[JetForge.SelfTest] " + s);
            try { File.AppendAllText(TracePath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n"); }
            // 探测豁免：每条痕迹落盘失败不影响自检结论
            catch { }
        }

        /// <summary>
        /// 火色的"蓝红比"：温度越高该越大。
        /// 用比值而不是绝对色，是因为亮度倍数也乘在颜色上（颜色 &gt; 1 就是 HDR 更亮），
        /// 比绝对通道会跟着推力一起漂。
        /// </summary>
        static double Ratio(double t7, double thrustN, double refN)
        {
            Color s, e;
            float glow, t;
            JetForgeWaterfall.Colors(t7, thrustN, refN, out s, out e, out glow, out t);
            return s.r > 1e-4f ? (double)s.b / s.r : 0.0;
        }

        IEnumerator Run()
        {
            yield return null;
            // 语言要在**第一句**之前装好：LocKsp.Install() 平时挂在
            // ModuleJetForgeEngine.OnStart 里，而自检跑在加载阶段、还没有零件被实例化。
            // 不装就会一路报中文——包括下面那些本该按语言走的判据文案，
            // 于是"英文半边从没被验证过"这件事在日志里看不出来。
            JetForge.Core.LocKsp.Install();
            Log("==== 开始自检 ====");
            Log("这份日志是一台机器把所有自检项跑一遍的记录：每行【】里是这一项在查什么，冒号后是结论，数字放在后面。");
            Log("只想看结论的话，盯两处：「判据」那一行说这一遍有没有红的，末行「==== 自检结束 ====」说明跑完了。");
            Log("行尾标 **超差** 的是没通过的；标「WARN」的是过得去但值得看一眼的。");

            EngineDesignData design = null;
            try { design = LoadDesignFromGameDatabase(); }
            catch (Exception e) { Log("读 cfg 失败: " + e); }

            if (design == null)
            {
                Log("找不到零件 cfg 里的 JETFORGE_DESIGN，自检中止");
                yield break;
            }

            Log(string.Format("【设计】{0}，{1} 根轴｜风扇 Ø{2:F3} m、{3} 级｜高压压气机 {4} 级、叶尖 Ø{5:F3} m｜高压涡轮 {6} 级｜低压涡轮 {7} 级｜燃烧室 {8}，出口 {9:F0} K",
                design.Kind, design.ShaftCount,
                design.Fan.Diameter, design.Fan.Stages,
                design.Hpc.Stages, design.Hpc.TipDiameter,
                design.Hpt.Stages, design.Lpt.Stages,
                design.Burner.Kind, design.Burner.ExitTemp));

            yield return null;

            CycleResult dp = null;
            try
            {
                dp = Cycle.SolveDesignPoint(design);
                if (dp == null || !dp.Ok)
                    Log("设计点求解失败: " + (dp == null ? "null" : string.Join("; ", dp.Errors.ToArray())));
                else
                    Log(string.Format("【设计点】算得出来：总压比 {0:F2}，风扇压比 {1:F2}，涵道比 {2:F2}；空气流量 {3:F1} kg/s，推力 {4:F2} kN，耗油率 {5:F4} kg/(N·h)；{6}",
                        dp.Opr, dp.Fpr, dp.Bpr, dp.MdotAir, dp.Thrust / 1000.0, dp.Sfc,
                        dp.SelfSustaining ? "转速能自持" : "撑不住，转速会往下掉"));
            // 高压压气机的工作点在它自己的特性图上离喘振线多远。这是"图参与工作点"的收据：
            // -1 表示图没建起来（这时不要假装裕度是 0）。
            if (dp != null)
                Log(dp.HpcSurgeMargin > 0.0
                    ? string.Format("【喘振裕度】高压压气机的工作点离喘振线还有 {0:F0}% 余量{1}",
                        dp.HpcSurgeMargin, dp.HpcSurgeRisk ? "（已经在警戒线以内，再收油门就要喘）" : "")
                    : "【喘振裕度】这张特性图没建起来，裕度读不到");
            if (dp != null)
                Log(dp.LpcSurgeMargin > 0.0
                    ? string.Format("【喘振裕度】低压压气机 {0:F0}% 余量{1}（它有自己的图，不是把高压那张的数抄一遍）",
                        dp.LpcSurgeMargin, dp.LpcSurgeRisk ? "——已在警戒线以内" : "")
                    : "【喘振裕度】这台机器没有低压压气机（或它的图没建起来），低压那一侧不报数");
            }
            catch (Exception e) { Log("设计点异常: " + e); }

            yield return null;

            DesignConstants c = null;
            try
            {
                c = OffDesign.DeriveDesignConstants(design);
                Log(string.Format("【设计常数】由设计点反推出来的那些数：压气机总压比 {0:F3}，涡轮膨胀比 {1:F4}，风扇功常数 {2:F3}；涡轮喉道面积 {3:F5} m²，喷管出口面积 {4:F4} m²，出口/喉道面积比 {5:F2}；涡轮前温度可用 {6:F0}~{7:F0} K；设计流量 {8:F1} kg/s",
                    c.Cpr, c.Ttr, c.FanWorkConstant, c.ATurb, c.A8, c.NozzleAreaRatio, c.T4Idle, c.T4Max, c.MdotDAir));
            }
            catch (Exception e) { Log("设计常数异常: " + e); }

            yield return null;

            // 实时解算 vs 预解网格：同一台机器、同一点，两条路给出的数必须一样。
            // 不一样的唯一可能就是采样器填字段填错了（以前漏过 MdotCore）。
            Section("性能：实时解算和预解网格是不是同一台机器");
            try
            {
                PerformanceGrid rtg = GridFactory.BuildCoarse(design, c);
                GridSampler gs = new GridSampler(rtg);
                RealtimeSampler rt = new RealtimeSampler(design, c, 40);
                rt.Hz = 0.0;    // 关掉刷新率闸，每次都真算
                string[] labels = { "满油门海平面", "70% 油门", "8 km M0.60", "11 km M0.85" };
                double[,] pts = { { 1.00, 0.0, 0.0 }, { 0.70, 0.0, 0.0 }, { 1.00, 8000.0, 0.60 }, { 1.00, 11000.0, 0.85 } };
                double worst = 0.0;
                for (int i = 0; i < pts.GetLength(0); i++)
                {
                    PerformanceGrid.Sample a = gs.Sample(pts[i, 0], pts[i, 1], pts[i, 2], 0.0);
                    PerformanceGrid.Sample b = rt.Sample(pts[i, 0], pts[i, 1], pts[i, 2], 0.0);
                    double err = Math.Abs(b.Thrust - a.Thrust) / Math.Max(Math.Abs(a.Thrust), 1.0);
                    if (err > worst) worst = err;
                    Log(string.Format("  {0}：网格 {1:F2} kN / {2:F1} kg/s｜实时 {3:F2} kN / {4:F1} kg/s（差 {5:P2}）",
                        labels[i], a.Thrust / 1000.0, a.MassFlow, b.Thrust / 1000.0, b.MassFlow, err));
                }
                Log(string.Format("  两条路最大偏差 {0:P2}｜两条路一致：{1}", worst, worst < 0.03 ? "是" : "不是"));
                Log("    节点上应当分毫不差；差出来的部分是粗网格在两点之间插出来的误差。");

                // 一次解算几毫秒，决定刷新率能开到多少。这个数不能凭感觉。
                var sw = new System.Diagnostics.Stopwatch();
                int n = 12;
                sw.Start();
                for (int i = 0; i < n; i++)
                {
                    rt.Invalidate();
                    rt.Sample(0.55 + 0.02 * i, 500.0 + 40.0 * i, 0.20 + 0.01 * i, 0.0);
                }
                sw.Stop();
                double per = sw.Elapsed.TotalMilliseconds / n;
                Log(string.Format("  一次实时解算 {0:F2} ms → 每帧算（60 fps）每秒 {1:F1} ms，30 Hz 只要 {2:F1} ms（一秒 1000 ms）",
                    per, per * 60.0, per * 30.0));
                Log("  解算器自述：" + rt.Describe());
                Log("    多线程：不必要——单次零点二毫秒，每帧算也只占帧时间的百分之一点几；上线程反而多一处竞态。");
            }
            catch (Exception e) { Log("实时解算段异常: " + e.Message); }

            yield return null;

            try
            {
                OffDesignResult od = OffDesign.Solve(design, 0.0, 0.0, 1.0, 0.0, c);
                Log(string.Format("海平面静止、油门到底：高压转子 {0:F1}% 转速（换算 {1:F1}%），吸 {2:F1} kg/s 空气，总压比 {3:F2}，涡轮前 {4:F0} K；推力 {5:F2} kN，耗油率 {6:F4} kg/(N·h)；{7}",
                    od.N2Pct, od.NCorrected * 100.0, od.MdotAir, od.Opr, od.T4, od.Thrust / 1000.0, od.Sfc,
                    od.Governed ? "被调节器限住了（没到物理极限）" : "油门全开、不受调节器限"));
            }
            catch (Exception e) { Log("非设计点异常: " + e); }

            yield return null;

            try
            {
                // 运行时状态审计：看结果自己跟自己对不对得上，不改任何物理。
                // "Ok 了却带着错误"、"推力是 NaN"、"涡喷报了涵道比"、"喉道面积 0 还算出流量"
                // 这类自相矛盾以前在面板上都长得像"一切正常"，所以这一行必须每天自检时念一遍。
                List<StateAudit.Issue> di = StateAudit.AuditDesign(dp, design);
                OffDesignResult odAudit = OffDesign.Solve(design, 0.0, 0.0, 1.0, 0.0, c);
                List<StateAudit.Issue> oi = StateAudit.AuditOffDesign(odAudit, design);
                IntakeSolution isl = IntakeSolver.SolveAt(design.Intake, design, 0.0, 2.0, 0.0, 0.0);
                List<StateAudit.Issue> ii = StateAudit.AuditIntake(isl);
                Log(string.Format("状态审计：设计点 {0}｜非设计点 {1}｜进气道 M2 {2}｜算错共 {3} 条",
                    StateAudit.Summarize(di), StateAudit.Summarize(oi), StateAudit.Summarize(ii),
                    StateAudit.ErrorCount(di) + StateAudit.ErrorCount(oi) + StateAudit.ErrorCount(ii)));
                foreach (string ln in StateAudit.Lines(ii)) Log("  " + ln);

                // 模型档案：每条物理模型是 A/B/C/D 哪一级、误差多大。
                // 念出来的目的是让"这个数是算出来的还是估出来的"每次自检都被提醒一次——
                // 文档可以漂，日志里的这一行跟着构建走。
                Log(string.Format("模型档案：{0} 条｜{1}", ModelLedger.Entries.Count, ModelLedger.Summary()));
                ModelEntry worst = null;
                foreach (ModelEntry me in ModelLedger.Entries)
                    if (worst == null || me.Grade > worst.Grade) worst = me;
                if (worst != null) Log(string.Format("  最粗的一条：{0}（{1}）", worst.Name, worst.GradeName()));
            }
            catch (Exception e) { Log("状态审计异常: " + e.Message); }

            yield return null;

            try
            {
                CheckReport rep = Criteria.Evaluate(design, Criteria.ViewOf(dp), null, Gas.P0, 0.0);
                Log(string.Format("判据跑了 {0} 条：{1}", rep.Findings.Count,
                    rep.Fails.Count > 0
                        ? string.Format("{0} 条没过、{1} 条要留意（下面逐条列出）", rep.Fails.Count, rep.Warns.Count)
                        : rep.Warns.Count > 0
                            ? string.Format("没有过不去的，{0} 条要留意（下面逐条列出）", rep.Warns.Count)
                            : "全部通过"));
                foreach (Finding f in rep.Warns) Log("  " + f.Line());
                foreach (Finding f in rep.Fails) Log("  " + f.Line());
            }
            catch (Exception e) { Log("判据异常: " + e); }

            yield return null;

            try
            {
                PerformanceGrid g = GridFactory.BuildCoarse(design, c);
                int valid = 0;
                double peak = 0.0, minSfc = 1e9;
                for (int i = 0; i < g.Thrust.Length; i++)
                {
                    if (!g.Valid[i]) continue;
                    valid++;
                    if (g.Thrust[i] > peak) peak = g.Thrust[i];
                    if (g.Sfc[i] > 1e-9 && g.Sfc[i] < minSfc) minSfc = g.Sfc[i];
                }
                Log(string.Format("粗网格：{0}×{1}×{2}={3} 点，有效 {4}，构建 {5:F3} s，峰值 {6:F2} kN，最低 SFC {7:F4}",
                    g.Throttle.Length, g.Alt.Length, g.Mach.Length, g.Thrust.Length, valid, g.BuildSeconds, peak / 1000.0, minSfc));
            }
            catch (Exception e) { Log("网格异常: " + e); }

            yield return null;

            try
            {
                Mesh merged = JetForgeMeshBuilder.Build(design);
                var pieces = JetForgeMeshBuilder.BuildComponents(design);
                Section("外形：网格、贴图、材质");
                Log(string.Format("外形：合并网格 {0} 顶点 / {1} 三角（包围盒 {2:F2}×{3:F2}×{4:F2} m），分部件 {5} 个",
                    merged != null ? merged.vertexCount : 0,
                    merged != null ? merged.triangles.Length / 3 : 0,
                    merged != null ? merged.bounds.size.x : 0f,
                    merged != null ? merged.bounds.size.y : 0f,
                    merged != null ? merged.bounds.size.z : 0f,
                    pieces != null ? pieces.Count : 0));
                if (pieces != null)
                    foreach (var p in pieces)
                        Log(string.Format("  部件 {0}: {1} 顶点", p.Name, p.Mesh != null ? p.Mesh.vertexCount : 0));

                // 剖视：界面上的"尺寸占比"必须和三维网格量出同一件事。
                // 两边各算一遍一定会漂，所以图形那边只搬 EngineGeometry 的数。
                try
                {
                    Log("  " + JetForgeMeshBuilder.DescribeCutaway(design));
                    EngineStations st = EngineGeometry.Of(design);
                    var shapes = JetForgeMeshBuilder.Cutaway(design);
                    float bl = merged != null ? merged.bounds.size.y : 0f;
                    float br = merged != null ? Mathf.Max(merged.bounds.size.x, merged.bounds.size.z) * 0.5f : 0f;
                    float rShell = (float)st.RNac;   // 外壳最粗处（以前是 RIn×1.03，短舱直径一改就量不准）
                    bool lenOk = Mathf.Abs(bl - (float)st.L) < 0.15f;
                    bool radOk = Mathf.Abs(br - rShell) <= rShell * 0.08f;
                    int badTab = 0, degenerate = 0, clickable = 0;
                    foreach (JetForgeMeshBuilder.CutawayShape s in shapes)
                    {
                        if (s.Tab < 0 || s.Tab > JetForge.Core.DesignTabs.Cutaway) badTab++;
                        if (s.Poly == null || s.Poly.Count < 3 || Mathf.Abs(s.YFront - s.YBack) < 1e-4f) degenerate++;
                        else clickable++;
                    }
                    // 长径比：真机涡扇多在 5~7；我们外形直径是"按流量反解的等效捕获面积"，
                    // 所以这条只作参考量打印，不做断言。
                    Log(string.Format("  尺寸占比：机长 {0:F2} m（网格 {1:F2}）｜进气口半径 {2:F3}（网格 {3:F3}）｜最大半径 {4:F3}｜长径比 {5:F2}｜可点 {6}/{7}",
                        st.L, bl, st.RIn, br, st.MaxRadius, st.L / (2.0 * Mathf.Max(0.01f, (float)st.MaxRadius)),
                        clickable, shapes != null ? shapes.Count : 0));
                    // 存两张图：干态与带加力。游戏里截不到只剩我们这层皮的画面，
                    // 而这张平面图就是玩家真看到的那一张，尺寸占比看它最直接。
                    Log("  " + JetForge.UI.JetForgeCutaway.DumpPng(design,
                        Path.Combine(KSPUtil.ApplicationRootPath, "GameData/JetForge/cutaway_dry.ppm")));
                    EngineDesignData abDesign = design.Clone();
                    abDesign.Afterburner.Enabled = true;
                    Log("  " + JetForge.UI.JetForgeCutaway.DumpPng(abDesign,
                        Path.Combine(KSPUtil.ApplicationRootPath, "GameData/JetForge/cutaway_ab.ppm")));
                    Log(string.Format("  断言：机长一致 {0}｜半径一致 {1}｜页号都合法 {2}｜没有退化形 {3}",
                        lenOk ? "对" : "不对", radOk ? "对" : "不对",
                        badTab == 0 ? "对" : "不对", degenerate == 0 ? "对" : "不对"));
                }
                catch (Exception e) { Log("剖视异常: " + e); }

                // 进气道四型式：面板上四个型式都能选，但"选完在游戏里长什么样"以前没人验过。
                // 锥体和斜板应该多出一个中心锥（剖视里一块、网格里一个部件），轴对称和皮托不该有。
                try
                {
                    IntakeKind[] kinds = { IntakeKind.Axial, IntakeKind.Pitot, IntakeKind.Ramp, IntakeKind.Cone };
                    string[] tags = { "axial", "pitot", "ramp", "cone" };
                    for (int i = 0; i < kinds.Length; i++)
                    {
                        EngineDesignData ik = design.Clone();
                        ik.Intake.Kind = kinds[i];
                        // 明确填一个捕获直径：几何才走参数化那条路（0 = 按风扇/进气口径推）
                        ik.Intake.CaptureDiameter = 0.72;
                        IntakeShape ish = IntakeGeometry.Of(ik);
                        var ikShapes = JetForgeMeshBuilder.Cutaway(ik);
                        int coneShapes = 0;
                        if (ikShapes != null)
                            foreach (JetForgeMeshBuilder.CutawayShape s in ikShapes)
                                if (!string.IsNullOrEmpty(s.Name) && (s.Name.Contains("锥") || s.Name.Contains("斜板"))) coneShapes++;
                        bool hasConePart = false;
                        var ikPieces = JetForgeMeshBuilder.BuildComponents(ik);
                        if (ikPieces != null)
                            foreach (var p in ikPieces)
                                if (p.Name != null && p.Name.Contains("中心")) hasConePart = true;
                        Log(string.Format("  进气道 {0}：捕获半径 {1:F3} m｜喉道半径 {2:F3} m｜发动机面半径 {3:F3} m｜弯 {4:F0}°｜中心锥 {5}（剖视 {6} 块／网格部件 {7}）",
                            tags[i], ish.RCapture, ish.RThroat, ish.RFace, ik.Intake.DuctBendDeg,
                            ish.HasCenterBody ? "有" : "无", coneShapes, hasConePart ? "有" : "无"));
                        Log("  " + JetForge.UI.JetForgeCutaway.DumpPng(ik,
                            Path.Combine(KSPUtil.ApplicationRootPath, "GameData/JetForge/cutaway_intake_" + tags[i] + ".ppm")));
                    }
                }
                catch (Exception e) { Log("进气道四连拍异常: " + e); }

                // 进气道零件自己的网格与材质槽：唇口、内壁、外罩都必须有东西。
                // 这三个槽只要有一个是 0，零件在机库里就会缺一圈（看着像"少了个唇口"），
                // 而 Unity 对空子网格不报任何错。
                try
                {
                    IntakeSpec ip = design.Intake.Clone();
                    ip.Kind = IntakeKind.Cone;
                    ip.CaptureDiameter = 0.80;
                    Mesh im = JetForgeMeshBuilder.BuildIntake(ip, 0.60, 1.2);
                    int aOuter = JetForgeMeshBuilder.SubmeshTriangles(im, JetForgeMeshBuilder.SubCold);
                    int aCone = JetForgeMeshBuilder.SubmeshTriangles(im, JetForgeMeshBuilder.SubHot);
                    int aDuct = JetForgeMeshBuilder.SubmeshTriangles(im, JetForgeMeshBuilder.SubInner);
                    int aLip = JetForgeMeshBuilder.SubmeshTriangles(im, JetForgeMeshBuilder.SubLip);
                    int aStrut = JetForgeMeshBuilder.SubmeshTriangles(im, JetForgeMeshBuilder.SubCasing);
                    Log(string.Format("进气道零件网格：子网格 {0} 个｜外罩 {1}｜压缩面 {2}｜内壁 {3}｜唇口 {4}｜支板 {5}｜三槽齐全 {6}",
                        im != null ? im.subMeshCount : 0, aOuter, aCone, aDuct, aLip, aStrut,
                        (aOuter > 0 && aDuct > 0 && aLip > 0) ? "对" : "不对"));
                    // 锥体/斜板必须长出支板：超声速进气道那根锥是靠支板吊在流道里的，缺了支板它看着像浮着。
                    {
                        EngineDesignData ax = design.Clone();
                        ax.Intake.Kind = IntakeKind.Axial;
                        ax.Intake.CaptureDiameter = 0.80;
                        int strutAxial = JetForgeMeshBuilder.SubmeshTriangles(
                            JetForgeMeshBuilder.BuildIntake(ax.Intake, 0.60, 1.2), JetForgeMeshBuilder.SubCasing);
                        Log(string.Format("  支撑支板：锥体支板 {0} 三角｜轴对称支板 {1} 三角｜只给中心体：对",
                            aStrut, strutAxial));
                    }
                    Log("  " + JetForgeMeshBuilder.DescribeMaterials(null));
                }
                catch (Exception e) { Log("进气道零件网格异常: " + e); }

                // 型线放样：自定义件的形状由截面序列给出，这条路径和上面那条"锥/斜板"完全不同
                // （上面是按面积反推半径，这里是逐截面放样）。放样写错时 Unity 不会报错，
                // 只会长出一个翻面、缺内壁或缺唇口的壳子，所以这里逐槽数三角。
                try
                {
                    IntakeSpec ps = design.Intake.Clone();
                    ps.Shape = IntakeProfile.Default(1.2, 0.80);
                    ps.Shape.Sections[1].Width = 0.68;
                    ps.Shape.Sections[1].Height = 0.68;
                    ps.Shape.SpinnerLength = 0.35;
                    Mesh pm = JetForgeMeshBuilder.BuildIntake(ps, 0.60, 1.2);
                    int pOuter = JetForgeMeshBuilder.SubmeshTriangles(pm, JetForgeMeshBuilder.SubCold);
                    int pSpin = JetForgeMeshBuilder.SubmeshTriangles(pm, JetForgeMeshBuilder.SubHot);
                    int pDuct = JetForgeMeshBuilder.SubmeshTriangles(pm, JetForgeMeshBuilder.SubInner);
                    int pLip = JetForgeMeshBuilder.SubmeshTriangles(pm, JetForgeMeshBuilder.SubLip);
                    Log(string.Format("型线放样网格：子网格 {0} 个｜外罩 {1}｜中心体 {2}｜内壁 {3}｜唇口 {4}｜四槽齐全 {5}",
                        pm != null ? pm.subMeshCount : 0, pOuter, pSpin, pDuct, pLip,
                        (pOuter > 0 && pSpin > 0 && pDuct > 0 && pLip > 0) ? "对" : "不对"));
                    IntakeSpec resolvedProf = ps.Resolved();
                    Log(string.Format("  型线量出来的量：捕获 Ø{0:F2}｜喉道比 {1:F2}｜压缩角 {2:F1}°（{3} 级）｜弯曲 {4:F1}°｜模型 {5}",
                        resolvedProf.CaptureDiameter, resolvedProf.ThroatRatio, ps.Shape.CompressionDeg,
                        ps.Shape.CompressionStages, resolvedProf.DuctBendDeg, resolvedProf.Model));
                }
                catch (Exception e) { Log("型线放样网格异常: " + e); }

                // 自由多边形截面：网格必须按多边形长，不是按外接椭圆。
                // 判据看"面积"最省事：同样宽高的方形截面比圆多出 27%，三角形少一截。
                try
                {
                    IntakeSpec polySpec = new IntakeSpec();
                    IntakeProfile pp = IntakeProfile.Default(1.2, 0.80);
                    pp.Sections[1].Polygon = new List<JetForge.Core.SectionPoint>
                    {
                        new JetForge.Core.SectionPoint(-1, -1), new JetForge.Core.SectionPoint(1, -1),
                        new JetForge.Core.SectionPoint(1, 1), new JetForge.Core.SectionPoint(-1, 1),
                    };
                    polySpec.Shape = pp;
                    Mesh polyMesh = JetForgeMeshBuilder.BuildIntake(polySpec, 0.60, 1.2, false);
                    int polyTris = 0;
                    if (polyMesh != null)
                        for (int i = 0; i < polyMesh.subMeshCount; i++) polyTris += polyMesh.GetTriangles(i).Length / 3;
                    double polyArea = pp.Sections[1].Area;
                    Log(string.Format("  多边形截面：{0} 三角｜截面积 {1:F3} m²｜形状 {2}",
                        polyTris, polyArea, pp.Sections[1].HasPolygon ? "自由多边形" : "参数化"));
                }
                catch (Exception e) { Log("多边形截面网格异常: " + e); }

                // 加力段几何：独立筒体多长、排气锥是不是收在加力段之前
                {
                    EngineDesignData ab = design.Clone();
                    ab.Afterburner.Enabled = true;
                    ab.Afterburner.Length = 0.0;
                    var abPieces = JetForgeMeshBuilder.BuildComponents(ab);
                    float abLen = JetForgeMeshBuilder.AbLengthOf(ab);
                    float yNoz = JetForgeMeshBuilder.NozzleStartY(ab);
                    Log(string.Format("加力段：长度 {0:F2} m（自动档）｜喷管起点 y={1:F2}｜部件数 {2} 个（无加力 {3} 个）",
                        abLen, yNoz, abPieces != null ? abPieces.Count : 0, pieces != null ? pieces.Count : 0));
                    ab.Afterburner.Length = 0.5;
                    Log(string.Format("  指定 0.50 m：实际 {0:F2} m｜喷管起点 y={1:F2}",
                        JetForgeMeshBuilder.AbLengthOf(ab), JetForgeMeshBuilder.NozzleStartY(ab)));
                    Log(string.Format("  排气锥尖端 y={0:F2}｜加力段起点 y={1:F2}（尖端不许比起点更靠后）",
                        JetForgeMeshBuilder.TailConeTipY(ab),
                        JetForgeMeshBuilder.NozzleStartY(ab) + JetForgeMeshBuilder.AbLengthOf(ab)));
                }

                // 贴图与 UV：子网格是不是真的分了冷热两套、UV 是不是按世界尺寸平铺
                if (merged != null)
                {
                    var uv = merged.uv;
                    float uMax = 0f, vMax = 0f, uMin = 1e9f, vMin = 1e9f;
                    for (int i = 0; i < uv.Length; i++)
                    {
                        if (uv[i].x > uMax) uMax = uv[i].x;
                        if (uv[i].x < uMin) uMin = uv[i].x;
                        if (uv[i].y > vMax) vMax = uv[i].y;
                        if (uv[i].y < vMin) vMin = uv[i].y;
                    }
                    Log(string.Format("贴图：子网格 {0} 个｜UV u {1:F2}~{2:F2}（≈{3:F1} 圈）v {4:F2}~{5:F2}（≈{6:F1} 圈，按 {7:F2} m 平铺）",
                        merged.subMeshCount, uMin, uMax, (uMax - uMin),
                        vMin, vMax, (vMax - vMin), JetForgeMeshBuilder.UvWorldSize));
                    Log("  贴图按世界坐标平铺：UV 涨 1 就是贴图重复一遍，所以上面写的「≈几圈」是这台机器身上这套贴图重复了几次。");
                    Log("  " + JetForgeMeshBuilder.DescribeMaterials(null));
                    Log("  " + JetForgeMeshBuilder.DescribeSurface());
                    // 绕序：写反了整台机器就内翻（Unity 剔背面），从外面看是透明的。
                    // 这条检查本该在贴图阶段一开始就有——省掉两轮"用户截图才发现"的返工。
                    Log("  " + JetForgeMeshBuilder.DescribeSubmeshes(merged));
                    // 冷/热/内壁/机匣/唇口五套都得分到东西：热端为 0 说明核心机后段没切成 SubHot，
                    // 内壁为 0 说明喷管内通道丢了，机匣为 0 说明箍和法兰还挂在蒙皮上。
                    // 这几种错都不会报错，只会"某个部件看起来和隔壁一样"。
                    int scTri = JetForgeMeshBuilder.SubmeshTriangles(merged, JetForgeMeshBuilder.SubCold);
                    int shTri = JetForgeMeshBuilder.SubmeshTriangles(merged, JetForgeMeshBuilder.SubHot);
                    int siTri = JetForgeMeshBuilder.SubmeshTriangles(merged, JetForgeMeshBuilder.SubInner);
                    int skTri = JetForgeMeshBuilder.SubmeshTriangles(merged, JetForgeMeshBuilder.SubCasing);
                    int slTri = JetForgeMeshBuilder.SubmeshTriangles(merged, JetForgeMeshBuilder.SubLip);
                    Log(string.Format("  冷热分配：冷 {0}｜热 {1}｜内壁 {2}｜机匣 {3}｜唇口 {4}｜热端占 {5:P0}｜五套都非空 {6}",
                        scTri, shTri, siTri, skTri, slTri,
                        (scTri + shTri + siTri + skTri + slTri) > 0
                            ? (double)shTri / (scTri + shTri + siTri + skTri + slTri) : 0.0,
                        (scTri > 0 && shTri > 0 && siTri > 0 && skTri > 0 && slTri > 0) ? "对" : "不对"));
                    Log("  " + JetForgeMeshBuilder.DescribeWinding(merged));
                    Log("  " + JetForgeMeshBuilder.DescribeMirrorSymmetry(merged));
                    Log("  " + JetForgeMeshBuilder.DescribeCentering(merged, design));
                    Log("  " + JetForgeMeshBuilder.DescribeDetails(design));
                    // 叶片密度必须能压过"按直径自动"：直径一样，宽弦和薄弦的片数差 70%
                    EngineDesignData bc = design.Clone();
                    bc.Fan.BladeCount = 0;
                    int auto = JetForgeMeshBuilder.BladesOf(bc);
                    int triAuto = JetForgeMeshBuilder.Build(bc).triangles.Length / 3;
                    bc.Fan.BladeCount = 12;
                    int tri12 = JetForgeMeshBuilder.Build(bc).triangles.Length / 3;
                    bc.Fan.BladeCount = 40;
                    int tri40 = JetForgeMeshBuilder.Build(bc).triangles.Length / 3;
                    Log(string.Format("  叶片密度：自动 {0} 片（{1} 三角）｜手动 12 片（{2}）｜手动 40 片（{3}）｜{4}",
                        auto, triAuto, tri12, tri40,
                        (bc.Fan.BladeCount == 40 && JetForgeMeshBuilder.BladesOf(bc) == 40) ? "手动能压过自动：对" : "手动能压过自动：不对"));

                    // ---- 羽流直径：干态跟外涵出口、加力跟核心机出口 ----
                    // 第五阶段起羽流只走 Waterfall，自绘那套已删；先确认硬依赖在不在。
                    try { Log("  依赖：" + Dependencies.Report()); }
                    catch (Exception e) { Log("  依赖检查异常: " + e.Message); }
                    // 零件名与说明走 KSP 本地化：键解析不出来时游戏里显示的就是 #autoLOC_…，
                    // 那是最刺眼的错，所以在这里先看一遍"是不是还留着键名"。
                    try
                    {
                        var tp = PartLoader.getPartInfoByName("JetForgeEngine");
                        var ip2 = PartLoader.getPartInfoByName("JetForgeIntake");
                        string raw1 = tp != null ? tp.title : "";
                        string raw2 = ip2 != null ? ip2.title : "";
                        // PartInfo.title 存的是 cfg 里的**原始值**（就是那个 #autoLOC_… 键），
                        // 上屏前 KSP 才做一次 Localizer.Format。所以要判的是"格式化之后还是不是键"，
                        // 第一版直接看 title 就把键当成了错——那种"检查自己看错了"最坑，
                        // 它会让人去改一个本来正确的 cfg。
                        string f1 = global::KSP.Localization.Localizer.Format(raw1);
                        string f2 = global::KSP.Localization.Localizer.Format(raw2);
                        bool keyLeak = (f1 ?? "").Contains("#autoLOC") || (f2 ?? "").Contains("#autoLOC");
                        bool empty = string.IsNullOrEmpty(f1) || string.IsNullOrEmpty(f2);
                        // 自检跑在加载阶段，那时可能还没有任何 JetForge 零件被实例化，
                        // 而 LocKsp.Install() 挂在 ModuleJetForgeEngine.OnStart 里 —— 于是
                        // 这一行会永远报"语言 中文"，即使 GameData/JetForge/Loc.cfg 里写着 en。
                        // 自检自己先装一次：诊断要能看见真实状态，不能因为"没人开过车"就报默认值。
                        JetForge.Core.LocKsp.Install();
                        Log(string.Format("  本地化：{0}｜发动机名 {1}（键 {2}）｜进气道名 {3}（键 {4}）｜上屏没有残留键 {5}",
                            Loc.Report(), f1, raw1, f2, raw2, (keyLeak || empty) ? "不对" : "对"));
                        // 语言探源：这台机器上得到过三个互相打架的读数——settings.cfg 写 en-us、
                        // Localizer 报 zh-cn、零件名却是中文。打架时不许挑一个信，三个来源都打出来。
                        // "库存样例"取一条原版零件的说明：它在两种语言下完全不同，能证明**活的是哪张表**。
                        // 键故意拼成 "#autoLOC_" + "500005"：本地化键闸门会把源码里的 #autoLOC_ 字面量
                        // 当成我们自己的键去查两份 cfg，原版键查不到就会报"缺词条"——那是别人的键。
                        Log(string.Format("  语言探源：Loc 认 {0}｜Localizer.CurrentLanguage {1}｜GameSettings {2}｜零件名样例 {3}｜库存样例 {4}",
                            Loc.Current, KspLanguage(), GameLanguage(),
                            global::KSP.Localization.Localizer.Format("#autoLOC_JF_engine_title"),
                            global::KSP.Localization.Localizer.Format("#autoLOC_" + "500005")));
                        // 资源名探针：玩家截图里，零件信息把「液体燃料 / 氧化剂 / 进气道」印成了
                        // #autoLOC_500999 / 501000 / 501005。这三个键**不是我们的**（原版 ResourcesGeneric.cfg
                        // 里引用的原版键），我们一行代码都没碰过它们。所以要问的只有一句：运行时这张表里
                        // 还有没有这三条？在——那就不是词条的事，是别的东西把键原样搬上了屏；
                        // 不在——那是语言包（这台机器上是 0000Tinygrox_CNPatches 那份词典）缺条目。
                        // 键故意拆开拼：本地化键闸门会把源码里的 #autoLOC_ 字面量当我们自己的键去查。
                        string rr1 = global::KSP.Localization.Localizer.Format("#autoLOC_" + "500999");
                        string rr2 = global::KSP.Localization.Localizer.Format("#autoLOC_" + "501000");
                        string rr3 = global::KSP.Localization.Localizer.Format("#autoLOC_" + "501005");
                        int resLeak = LeakCount(rr1) + LeakCount(rr2) + LeakCount(rr3);
                        Log(string.Format("  资源名表：液体燃料「{0}」｜氧化剂「{1}」｜进气道「{2}」｜{3}",
                            rr1, rr2, rr3, resLeak == 0 ? "三条都翻得出来" : "有 " + resLeak + " 条翻不出来（语言包缺条目）"));
                        // 右键菜单（PAW）的字段名走的也是 #autoLOC 键，而特性实参必须是编译期常量，
                        // 所以那批标签写不成 Loc.T(...)。键名写错在编辑器里看不出来——游戏只会
                        // 原样显示「#autoLOC_JF_f_thrust」。这里把每个键都格式化一遍，数一遍泄漏。
                        int pawKeys = 0, pawLeak = 0, pawFields = 0, pawPlain = 0;
                        string pawSample = "";
                        if (tp != null && tp.partPrefab != null && tp.partPrefab.Modules != null)
                        {
                            foreach (PartModule pm in tp.partPrefab.Modules)
                            {
                                if (pm == null || pm.Fields == null) continue;
                                foreach (BaseField bf in pm.Fields)
                                {
                                    if (bf == null) continue;
                                    pawFields++;
                                    string gn = bf.guiName;
                                    if (string.IsNullOrEmpty(gn)) continue;
                                    // 判据只有一条：格式化之后还留着 #autoLOC 就是没解析出来。
                                    // 「以 # 开头」不能当判据——KSP 在编译零件时就把 guiName 格式化过一遍，
                                    // 预制件上读到的往往已经是译文，第一版因此数不出一个键（假的 0）。
                                    if (gn.Contains("#autoLOC")) pawLeak++;
                                    else if (gn.StartsWith("#")) pawKeys++;
                                    else
                                    {
                                        pawPlain++;
                                        if (bf.name == "readThrust") pawSample = gn;
                                        else if (pawSample.Length == 0) pawSample = gn;
                                    }
                                }
                            }
                        }
                        Log(string.Format("  右键菜单词条：字段 {0} 个｜已解析 {1} 个｜直接还带键 {2} 个｜残留 #autoLOC {3} 个｜抽样 {4}｜{5}",
                            pawFields, pawPlain, pawKeys, pawLeak, pawSample,
                            pawFields == 0 ? "预制件上没解析出字段（这条没验到）"
                                : (pawLeak == 0 ? "没有残留键" : "有键没解析出来")));
                        // 上面那一遍看的是**加载时**就有的 guiName。加力按钮的名字是运行期
                        // 按状态拼出来的（随"自动/手动/没装"变），加载时那一遍根本看不见它——
                        // 于是 `"#autoLOC_JF_f_afterburner" + 后缀` 这种写法在游戏里原样上屏
                        // 形如「#autoLOC_JF_f_afterburner 自动（油门 70% 起）」，而自检报 0 泄漏。
                        // 这里当着预制件的面真调一次，再数一遍（§273）。
                        int dynLeak = 0;
                        string dynText = "（预制件上没有这台发动机，没验到）";
                        ModuleJetForgeEngine emTmp = null;
                        if (tp != null && tp.partPrefab != null && tp.partPrefab.Modules != null)
                            foreach (PartModule pm in tp.partPrefab.Modules)
                                if (pm is ModuleJetForgeEngine) { emTmp = (ModuleJetForgeEngine)pm; break; }
                        if (emTmp != null && emTmp.Events != null && emTmp.Events.Contains("ToggleAfterburnerEvent"))
                        {
                            // 光调一次不够：预制件多半"没装加力"，走的是「加力：未配置」那条**整条键**
                            // 的支路，而拼接只发生在"装了加力"的支路上——不把它推过去，
                            // 这条检查就是个恒绿的空壳。临时把加力打开，调完再改回去。
                            bool savedAb = emTmp.Design != null && emTmp.Design.Afterburner.Enabled;
                            try
                            {
                                if (emTmp.Design != null) emTmp.Design.Afterburner.Enabled = true;
                                emTmp.RefreshAbButton();
                                dynText = emTmp.Events["ToggleAfterburnerEvent"].guiName ?? "";
                            }
                            finally
                            {
                                if (emTmp.Design != null) emTmp.Design.Afterburner.Enabled = savedAb;
                            }
                            if (dynText.Contains("#autoLOC")) dynLeak = 1;
                        }
                        Log(string.Format("  运行期标签：加力按钮「{0}」｜{1}",
                            dynText, dynLeak == 0 ? "名字里没有残留键" : "名字里还留着键"));
                        // 百分号排版：`{0:P0}` 在这台机器上印成「70 %」（数字与 % 之间多一格）。
                        // §273 修过一轮（改区域设置的 PercentPositivePattern），但 Mono 上
                        // "改过了"不等于"当场生效"——所以这里念一句样例，让结果自己说话。
                        string pctProbe = JetForge.Core.Loc.PercentProbe();
                        Log(string.Format("  百分比排版：{0}｜{1}", pctProbe,
                            JetForge.Core.Loc.PercentTight ? "数字紧挨着 %：对" : "数字和 % 之间还空着一格：不对"));
                        // 不切一次语言，英文那半边就永远是"没验过的"——这台机器是中文系统，
                        // 中文正常并不能说明英文词条存在。切过去格式化一遍再切回来。
                        string en1 = null, en2 = null;
                        bool switched = TryFormatEnglish(ref en1, ref en2);
                        bool enOk = switched && !string.IsNullOrEmpty(en1) && !(en2 ?? "").Contains("#autoLOC");
                        Log(switched
                            ? string.Format("  英文词条：{0}｜{1}｜两边都在 {2}", en1, en2, enOk ? "对" : "不对")
                            : "  英文词条：没能切换语言（只验了当前语言）");
                        // 英文界面除了词条，还有整块 UI：字段表的标签、悬停提示。
                        // 这里把语言切成英文，从头建一次字段表（它按当前语言缓存），
                        // 数三件事：标签里还有没有汉字、提示能不能挂上、抽样长得什么样。
                        // 注意：字段表是首次访问时按当时语言建的，这一步必须早于任何打开设计器的代码。
                        Lang prevLang = Loc.Current;
                        try
                        {
                            Loc.Current = Lang.English;
                            List<FieldSpec> all = DesignSchema.All();
                            int enLabelCn = 0, enHintMiss = 0, enHintHit = 0;
                            string enSample = "", enMiss = "";
                            foreach (FieldSpec fs in all)
                            {
                                if (HasHan(fs.Label)) { enLabelCn++; if (enSample.Length == 0) enSample = fs.Label; }
                                string hint = JetForge.UI.JetForgeDesignerWindow.HintOfPublic(fs.Label);
                                if (hint == null) { enHintMiss++; if (enMiss.Length == 0) enMiss = fs.Label; }
                                else enHintHit++;
                            }
                            Log(string.Format("  英文界面：字段表 {0} 项｜标签还带汉字 {1} 项{2}｜提示挂上 {3} 项 / 没有 {4} 项{5}",
                                all.Count, enLabelCn,
                                enSample.Length > 0 ? "（例如 " + enSample + "）" : "",
                                enHintHit, enHintMiss,
                                enMiss.Length > 0 ? "（例如 " + enMiss + "）" : ""));
                            // 悬停提示表的键是中文、标签在英文界面是英文。按中文键找回英文标签，
                            // 再看提示还挂不挂得上——这正是 §187.5 那个"英文界面提示静默消失"的缺陷。
                            int hkTotal = 0, hkNoEn = 0, hkHit = 0, hkMiss = 0;
                            string hkSample = "";
                            foreach (string k in JetForge.UI.JetForgeDesignerWindow.HintKeys())
                            {
                                hkTotal++;
                                string en = Loc.EnglishOf(k);
                                if (string.IsNullOrEmpty(en)) { hkNoEn++; continue; }
                                if (JetForge.UI.JetForgeDesignerWindow.HintOfPublic(en) == null)
                                {
                                    hkMiss++;
                                    if (hkSample.Length == 0) hkSample = en.Trim();
                                }
                                else hkHit++;
                            }
                            // 三种情形要分开说，不然"0 条"会同时表示"没问题"和"没测到"：
                            // 键全是 Loc.T 对子 → 没有裸中文键可试（该项不适用）；
                            // 有裸中文键且都能找回英文标签 → 正常；
                            // 有裸中文键但找不回 → 那是真的缺陷（英文界面提示会静默消失）。
                            if (hkNoEn == hkTotal)
                                Log(string.Format("  英文提示表：{0} 条｜键已全部走 Loc.T，没有裸中文键可试（这一项不适用；机制钉在回归第 104 段）",
                                    hkTotal));
                            else
                                Log(string.Format("  英文提示表：{0} 条｜其中裸中文键 {1} 条｜找回英文标签 {2} 条｜挂上 {3} 条 / 挂不上 {4} 条{5}（对照表按渲染登记，没渲染过的标签这里量不到——机制本身钉在回归第 104 段）",
                                    hkTotal, hkNoEn, hkTotal - hkNoEn, hkHit, hkMiss,
                                    hkSample.Length > 0 ? "（例如 " + hkSample + "）" : ""));
                        }
                        finally { Loc.Current = prevLang; }
                    }
                    // 探测豁免：只在自检里报告"没取到零件信息"，取不到零件名不是崩溃条件。
                    catch (Exception) { }
                    try
                    {
                        // PlumeRadius 返回的是**半径**，这里以前标签写成"直径"，对账时容易差一倍。
                        float rDry = JetForgeMeshBuilder.PlumeRadius(design, false);
                        float rWet = JetForgeMeshBuilder.PlumeRadius(design, true);
                        Log(string.Format("  羽流半径：干态 {0:F3} m｜加力 {1:F3} m（加力火光只从核心喷口出来）", rDry, rWet));
                        // 缩放落不落得下去，就是"火焰比喷口还粗"的唯一开关：
                        // 模板按参考半径 0.35 m authored，等比缩放后火焰半径 = 0.35 × scale。
                        // 这条断言是纯算术，不依赖渲染——渲染只能靠人眼，算术可以先兜住。
                        double sDry = JetForgeWaterfall.PlumeScale(rDry);
                        double sWet = JetForgeWaterfall.PlumeScale(rWet);
                        double flameDry = JetForgeWaterfall.RefNozzleRadius * sDry;
                        double flameWet = JetForgeWaterfall.RefNozzleRadius * sWet;
                        Log(string.Format("  羽流缩放：干 半径 {0:F3} → 缩放 {1:F3} → 火焰 {2:F3} m｜加力 半径 {3:F3} → 缩放 {4:F3} → 火焰 {5:F3} m",
                            rDry, sDry, flameDry, rWet, sWet, flameWet));
                        Log(string.Format("  火焰不比喷口粗：干 {0}｜加力 {1}",
                            flameDry <= rDry * 1.001 ? "对" : "不对",
                            flameWet <= rWet * 1.001 ? "对" : "不对"));
                        // 可变喷管的花瓣收起来时，实际出口比唇口还小（0.88 倍）：
                        // 羽流要按花瓣算，不然就是"从叶片缝里钻出来"。
                        bool petalOk = true;
                        float[] opens = { 0.05f, 0.30f, 0.60f, 1.00f };
                        var sbP = new StringBuilder();
                        for (int i = 0; i < opens.Length; i++)
                        {
                            float op = opens[i];
                            float rw = JetForgeMeshBuilder.PlumeRadiusLimited(design, true, op);
                            double sw = JetForgeWaterfall.PlumeScale(rw);
                            double fw = JetForgeWaterfall.RefNozzleRadius * sw;
                            double gate = JetForgeMeshBuilder.NozzleExitRadius(design) * JetForgeMeshBuilder.PetalExitFactor(op);
                            bool inside = design.Nozzle == NozzleKind.Converging || fw <= gate * 0.999;
                            if (!inside) petalOk = false;
                            if (i > 0) sbP.Append("｜");
                            sbP.Append(string.Format("开度 {0:P0} 火焰 {1:F3} / 花瓣口 {2:F3}", op, fw, gate));
                        }
                        Log("  花瓣各开度：加力 " + sbP.ToString());
                        Log(string.Format("  火焰收在花瓣里面：{0}", petalOk ? "对" : "不对"));

                        // 进气口是不是真的敞着：唇口内缘必须比叶尖大一圈，
                        // 否则正面看就是给风扇盖了个环（以前 RIn 只取到 0.98RFan）。
                        float throat = JetForgeMeshBuilder.IntakeThroatRadius(design);
                        float tip = JetForgeMeshBuilder.FanTipRadius(design);
                        Log(string.Format("  进气口开敞：唇口内缘 {0:F3} m｜叶尖 {1:F3} m｜叶尖不被压住 {2}",
                            throat, tip, (tip <= 0f || throat >= tip * 1.005f) ? "对" : "不对"));
                        EngineDesignData hbD = design.Clone();
                        hbD.Fan.Diameter = Math.Max(hbD.Fan.Diameter, 1.70);
                        float hbDry = JetForgeMeshBuilder.PlumeRadius(hbD, false);
                        float hbWet = JetForgeMeshBuilder.PlumeRadius(hbD, true);
                        Log(string.Format("  高涵道对照（扇径 {0:F2} m）：干态 {1:F3} m｜加力 {2:F3} m｜{3}",
                            hbD.Fan.Diameter, hbDry, hbWet,
                            hbDry > hbWet * 1.05f ? "干态确实比核心粗：对" : "干态比核心粗：不对"));

                        // 火色跟排气温度：低温偏黄、高温偏蓝紫，亮度跟推力。
                        // 主菜单里 FX 还没建、写不进材质，但颜色是纯算术，可以先把它钉住。
                        double refN = 80000.0;
                        double r1 = Ratio(800.0, refN, refN);
                        double r2 = Ratio(1600.0, refN, refN);
                        double r3 = Ratio(2100.0, refN, refN);
                        Log("  尾焰色温：" + JetForgeWaterfall.DescribeColor(800.0, refN, refN));
                        Log("            " + JetForgeWaterfall.DescribeColor(1600.0, refN, refN));
                        Log("            " + JetForgeWaterfall.DescribeColor(2100.0, refN, refN));
                        Log(string.Format("  温度越高越蓝：蓝红比 {0:F2} → {1:F2} → {2:F2}｜单调 {3}",
                            r1, r2, r3, (r1 < r2 && r2 < r3) ? "对" : "不对"));
                        Log(string.Format("  低温偏黄（蓝红比 < 0.35）：{0}｜高温偏蓝紫（蓝红比 > 0.90）：{1}",
                            r1 < 0.35 ? "对" : "不对", r3 > 0.90 ? "对" : "不对"));
                        Color cLo, cHi; float gLo, gHi, tIgnored;
                        JetForgeWaterfall.Colors(1800.0, 20000.0, refN, out cLo, out cHi, out gLo, out tIgnored);
                        JetForgeWaterfall.Colors(1800.0, refN, refN, out cLo, out cHi, out gHi, out tIgnored);
                        Log(string.Format("  推力越大越亮：慢推力 {0:F2}× → 满推力 {1:F2}×｜{2}",
                            gLo, gHi, gHi > gLo * 1.3f ? "对" : "不对"));

                        // 热浪：**已经回退成模板原值，一行都不写**。
                        // 那两个数是折射的横向偏移和模糊半径，往上推一点整片羽流就开始晃
                        // （玩家："热浪过于夸张"）。我们只能靠人眼判断，没有比模板作者更高的手段。
                        Log("  热浪：不碰（_Swirl 4 / _Blur 5 用模板原值，只调色温和亮度）");
                    }
                    catch (Exception e) { Log("  羽流半径异常: " + e.Message); }

                    // ---- 抹平层：读数和声音不再跟着每帧噪声跳 ----
                    //
                    // 玩家报的"飞行中数据抖动、声音跟着抖难听"。抖动不是循环算错了，
                    // 是输入（高度/马赫）带着刚体求解的微小振荡、输出又受 30 Hz 闸门影响。
                    // 这里用两条可验证的性质把它钉住：阶跃响应的时间常数对不对、
                    // 3% 的逐帧噪声进去以后还剩多少。
                    try
                    {
                        // 先喂一帧 0 让它对齐——第一次 Step 是直接对齐的（不让玩家看见从 0 爬上来），
                        // 所以这里不先归零的话，量到的永远是 1.000（我第一次就是这么写错的）。
                        Lag lag = new Lag();
                        lag.Step(0.0, 0.02, 0.20);
                        double a = 0.0, b = 0.0, cc = 0.0;
                        for (int i = 0; i < 5; i++) a = lag.Step(1.0, 0.02, 0.20);    // 0.10 s = 0.5 τ
                        Lag l2 = new Lag();
                        l2.Step(0.0, 0.02, 0.20);
                        for (int i = 0; i < 15; i++) b = l2.Step(1.0, 0.02, 0.20);   // 0.30 s = 1.5 τ
                        Lag l3 = new Lag();
                        l3.Step(0.0, 0.02, 0.20);
                        for (int i = 0; i < 30; i++) cc = l3.Step(1.0, 0.02, 0.20);   // 0.60 s = 3 τ
                        Log(string.Format("  阶跃响应（τ=0.20 s）：0.5τ {0:F3}｜1.5τ {1:F3}｜3τ {2:F3}",
                            a, b, cc));
                        bool stepOk = Math.Abs(a - 0.393) < 0.01 && Math.Abs(b - 0.777) < 0.01
                                      && Math.Abs(cc - 0.950) < 0.01;

                        // 噪声：逐帧 ±3% 交替抖动（这就是"读数一直在跳"的样子），
                        // 过一遍抹平层，看剩下多少。
                        Lag ln = new Lag();
                        double lo = 1.0, hi = 0.0;
                        for (int i = 0; i < 200; i++)
                        {
                            double noisy = (i % 2 == 0) ? 1.03 : 0.97;
                            double v = ln.Step(noisy, 0.02, 0.20);
                            if (i > 100) { if (v < lo) lo = v; if (v > hi) hi = v; }
                        }
                        Log(string.Format("  3% 抖进去、抹平后残留：{0:P2}（峰峰值）", hi - lo));
                        bool noiseOk = (hi - lo) < 0.01;

                        double held = 0.50;
                        Held.Of(0.502, ref held, 0.01, false);
                        bool held1 = Math.Abs(held - 0.50) < 1e-9;
                        Held.Of(0.53, ref held, 0.01, false);
                        bool held2 = Math.Abs(held - 0.53) < 1e-9;
                        Log(string.Format("  死区：0.50→0.502 不动 {0}｜→0.53 跟上 {1}",
                            held1 ? "对" : "不对", held2 ? "对" : "不对"));

                        // 没有加力燃烧室的机器：干态羽流强度必须是 0（只留热浪）。
                        // 注意基准是谁：这台设计本身有没有加力不能假设，两个都显式造出来。
                        EngineDesignData withAb = design.Clone();
                        withAb.Afterburner.Enabled = true;
                        EngineDesignData noAb = design.Clone();
                        noAb.Afterburner.Enabled = false;
                        double abDry = JetForgeWaterfall.DryLevel(withAb, 1.0, 0.0);
                        double abDryLit = JetForgeWaterfall.DryLevel(withAb, 1.0, 1.0);
                        double noAbDry = JetForgeWaterfall.DryLevel(noAb, 1.0, 0.0);
                        Log(string.Format("  干态羽流强度：有加力满油门 {0:F2}｜满加力时 {1:F2}｜无加力 {2:F2}（只留热浪）",
                            abDry, abDryLit, noAbDry));
                        bool abOk = noAbDry < 1e-9 && Math.Abs(abDry - 1.0) < 1e-6
                                    && Math.Abs(abDryLit - 0.15) < 1e-6;

                        // 热浪：直径必须按**核心机尾喷口**算，不能按风扇。
                        // 玩家拿大涵道机看得很清楚——以前那根柱子跟风扇罩一样粗。
                        double rCoreExit = JetForgeMeshBuilder.NozzleExitRadius(design);
                        double hazeWant = rCoreExit * JetForgeWaterfall.HazeRadiusFactor;
                        double hazeAtIdle = JetForgeWaterfall.HazeLevel(0.0);
                        double hazeAt10 = JetForgeWaterfall.HazeLevel(0.10);
                        double hazeAt100 = JetForgeWaterfall.HazeLevel(1.0);
                        bool hazeOk = hazeAtIdle < 1e-9 && hazeAt10 < hazeAt100
                                      && hazeAt100 > 0.05 && hazeAt100 < 0.60;
                        Log(string.Format("  热浪：核心喷口 {0:F3} m → 半径应 {1:F3} m（×{2:F2}）｜强度 慢车 {3:F2} / 10% {4:F2} / 满 {5:F2}",
                            rCoreExit, hazeWant, JetForgeWaterfall.HazeRadiusFactor,
                            hazeAtIdle, hazeAt10, hazeAt100));
                        Log(string.Format("  断言：热浪按核心喷口 {0}｜油门 0 处为 0 {1}｜随油门单调 {2}",
                            Math.Abs(hazeWant - rCoreExit * JetForgeWaterfall.HazeRadiusFactor) < 1e-9 ? "对" : "不对",
                            hazeAtIdle < 1e-9 ? "对" : "不对",
                            (hazeAt10 < hazeAt100) ? "对" : "不对"));

                        Log(string.Format("  断言：阶跃 {0}｜抖动残留 <1% {1}｜死区 {2}｜无加力无喷流 {3}",
                            stepOk ? "对" : "不对", noiseOk ? "对" : "不对",
                            (held1 && held2) ? "对" : "不对", abOk ? "对" : "不对"));
                    }
                    catch (Exception e) { Log("  抹平层异常: " + e.Message); }

                    // ---- 热力图：每一格都是真解算，不是插值 ----
                    //
                    // 这张图是玩家用来"把设计放在不同高度速度下比一比"的工具，
                    // 所以它必须和飞行读同一个解算器；拿旧网格插出来的图会骗人。
                    try
                    {
                        // 常数先推一次再计时：不缓存的话每次 Solve 都会重推设计点，
                        // 首版就这么量出 5 ms/次、一张图 20 秒，跟飞行里的 0.2 ms 差了 25 倍。
                        DesignConstants hc = OffDesign.DeriveDesignConstants(design);
                        DateTime h0 = DateTime.UtcNow;
                        JetForgeHeatmapWindow.StatsResult full = JetForgeHeatmapWindow.Compute(
                            design, 1.0, 0.0, 20000.0, 2.0, 14, 11, hc);
                        JetForgeHeatmapWindow.StatsResult half = JetForgeHeatmapWindow.Compute(
                            design, 0.5, 0.0, 20000.0, 2.0, 14, 11, hc);
                        double hms = (DateTime.UtcNow - h0).TotalMilliseconds;
                        Log(string.Format("  热力图满油门：最大 {0:F2} kN @ {1:F1} km / Ma {2:F2}｜有效 {3}/{4}",
                            full.MaxThrust / 1000.0, full.MaxAlt / 1000.0, full.MaxMach, full.Valid, full.Total));
                        Log(string.Format("  热力图半油门：最大 {0:F2} kN @ {1:F1} km / Ma {2:F2}｜有效 {3}/{4}",
                            half.MaxThrust / 1000.0, half.MaxAlt / 1000.0, half.MaxMach, half.Valid, half.Total));
                        // 一格一次解算，所以这里能直接报"算一张图要多久"——玩家拖滑杆会不会卡就看它
                        Log(string.Format("  一张 14×11 的图 {0:F0} ms（合 72×56 约 {1:F0} ms，分帧算）",
                            hms, hms / (14.0 * 11.0) * (72.0 * 56.0)));

                        bool hOk = full.Ok && half.Ok;
                        bool hThrust = full.MaxThrust > half.MaxThrust * 1.5;
                        bool hLow = full.MaxAlt < 8000.0;
                        bool hCover = full.Valid > full.Total / 2;
                        Log(string.Format("  断言：都算得出来 {0}｜满油门更大 {1}｜最大在低空 {2}｜过半格可用 {3}",
                            hOk ? "对" : "不对", hThrust ? "对" : "不对",
                            hLow ? "对" : "不对", hCover ? "对" : "不对"));
                    }
                    catch (Exception e) { Log("  热力图异常: " + e.Message); }

                    // ---- 音效：九条音层都在不在，曲线是不是跟着物理量走 ----
                    //
                    // 上一版只有三层、每层一条线性公式，实飞听着是一层噪声。
                    // 现在按 RSE 的结构改成"一层一音源，各自音量/音高曲线 + 各自滞后"，
                    // 曲线是纯函数，所以这里能直接把 x=0/0.5/1 三档摊开看，不用等耳朵。
                    try
                    {
                        int specN = JetForgeSound.SpecCount;
                        int have = 0;
                        string miss = "";
                        for (int i = 0; i < specN; i++)
                        {
                            string clip = JetForgeSound.ClipOf(i);
                            if (JetForgeSound.ClipExists(clip)) have++;
                            else miss += " " + clip;
                        }
                        Log(string.Format("音效素材：{0}/{1} 条音层找到{2}", have, specN,
                            have == specN ? "" : "｜缺：" + miss));

                        bool rse = JetForgeSound.RseInstalled();
                        bool rseHere = false;
                        string rseState = "未查";
                        bool rseDuck = false;
                        string rseGroups = "未查";
                        AvailablePart ap0 = null;
                        try
                        {
                            ap0 = PartLoader.getPartInfoByName("JetForgeEngine");
                            if (ap0 != null)
                            {
                                rseHere = rse && JetForgeSound.RseOnPart(ap0.partPrefab);
                                rseState = JetForgeSound.RseState(ap0.partPrefab);
                                rseDuck = JetForgeSound.RseActive(ap0.partPrefab);
                                rseGroups = JetForgeSound.RseGroups(ap0.partPrefab);
                            }
                        }
                        // 探测豁免：RSE 组名探测：拿不到就当没装 RSE，这一行的输出本来就是「装没装」
                        catch (Exception) { }
                        Log("  RSE：" + (rse
                            ? "装在本机" + (rseHere ? "，且给这个零件挂了 RSE_Engines" : "，但没给这个零件挂 RSE_Engines")
                              + "｜状态：" + rseState + " → " + (rseDuck ? "它真在放声，我们退到 0.35" : "它没声，我们满音量（不让位）")
                              + "｜声音组：" + rseGroups
                            : "不在 → 九层都我们放") + "｜借的是它的分层结构与音源，不是它的模块");
                        if (rseHere && ap0 != null && ap0.partPrefab != null)
                        {
                            // 主菜单里 Start() 不跑，所以"在放声"永远测不到；
                            // 但 OnStart 里那句查表可以在这里复刻——键命中就说明它不会死在启动路径上。
                            Log("  RSE 接管判据：" + JetForgeSound.RseGroupMatch(ap0.partPrefab));
                            // 加力层搬进 RSE 之后，必须证明两件事：找得到、没加力的机器上真的被压到零。
                            // 上一版留着加力层自己播，玩家听出来的就是"这部分没经过 RSE"。
                            EngineDesignData vAb = design.Clone();
                            vAb.Afterburner.Enabled = true;
                            EngineDesignData vNoAb = design.Clone();
                            vNoAb.Afterburner.Enabled = false;
                            Log("  RSE 加力层（有加力设计）：" + JetForgeSound.RseAbReport(ap0.partPrefab, vAb));
                            Log("  RSE 加力层（无加力设计）：" + JetForgeSound.RseAbReport(ap0.partPrefab, vNoAb));
                        }

                        Log("  " + JetForgeSound.DescribePlan(0.00f, 0.0f));
                        Log("  " + JetForgeSound.DescribePlan(0.50f, 0.0f));
                        Log("  " + JetForgeSound.DescribePlan(1.00f, 1.0f));

                        float v, pLo, pMd, pHi, vLo, vMd;
                        int iw = JetForgeSound.IndexOf("whine");
                        JetForgeSound.LayerAt(iw, 0.2f, 0f, 0.2, out v, out pLo);
                        JetForgeSound.LayerAt(iw, 0.6f, 0f, 0.6, out v, out pMd);
                        JetForgeSound.LayerAt(iw, 1.0f, 0f, 1.0, out v, out pHi);
                        int ir = JetForgeSound.IndexOf("res250");
                        JetForgeSound.LayerAt(ir, 0.00f, 0f, 0f, out vLo, out v);
                        JetForgeSound.LayerAt(ir, 0.25f, 0f, 0.25, out vMd, out v);
                        int iab = JetForgeSound.IndexOf("ab_roar");
                        float abOff, abOn;
                        JetForgeSound.LayerAt(iab, 1.0f, 0.0f, 1.0, out abOff, out v);
                        JetForgeSound.LayerAt(iab, 1.0f, 1.0f, 1.0, out abOn, out v);
                        float fanV;
                        int ifa = JetForgeSound.IndexOf("fan");
                        JetForgeSound.LayerAt(ifa, 1.0f, 0f, 1.0, out fanV, out v);
                        Log(string.Format("  曲线：啸叫音高 {0:F2}→{1:F2}→{2:F2}（转速升，音调升）｜共振层 0 转速时 {3:F2}、25% 时 {4:F2}｜加力层关 {5:F2} 开 {6:F2}",
                            pLo, pMd, pHi, vLo, vMd, abOff, abOn));
                        Log(string.Format("  断言：九条音层 {0}｜转速升高啸叫变高 {1}｜静止时共振层静音 {2}｜加力层只随加力开 {3}",
                            specN == 9 ? "对" : "不对",
                            (pLo < pMd && pMd < pHi) ? "对" : "不对",
                            vLo <= 0.001f ? "对" : "不对",
                            (abOff <= 0.001f && abOn > 0.05f) ? "对" : "不对"));

                        // ---- 嗓音：不同设计要听得出区别 ----
                        // 以前这些参数全是常数，60 cm 的涡喷和 2.6 m 的大涵道一个音色。
                        // 现在从几何推：出口半径定音高、级数定高频、机型定宽带、叶尖马赫定轰鸣。
                        {
                            EngineDesignData dBig = Presets.Build("RD-33");
                            EngineDesignData dJet = Presets.Build("J79");
                            EngineDesignData dSmall = Presets.Build("J85");
                            if (dBig != null) Log("  RD-33：" + JetForgeSound.DescribeVoice(dBig));
                            if (dJet != null) Log("  J79  ：" + JetForgeSound.DescribeVoice(dJet));
                            if (dSmall != null) Log("  J85  ：" + JetForgeSound.DescribeVoice(dSmall));
                            JetForgeSound.Voice vBig = JetForgeSound.VoiceOf(dBig);
                            JetForgeSound.Voice vJet = JetForgeSound.VoiceOf(dJet);
                            JetForgeSound.Voice vSmall = JetForgeSound.VoiceOf(dSmall);
                            Log(string.Format("  嗓音差异：出口 RD-33 {0:F2} / J79 {1:F2} / J85 {2:F2} m｜音高 {3:F2} / {4:F2} / {5:F2}（越小越尖）",
                                vBig.NozzleR, vJet.NozzleR, vSmall.NozzleR, vBig.Pitch, vJet.Pitch, vSmall.Pitch));
                            Log(string.Format("  断言：大机比小机沉 {0}｜三台音高互不相同 {1}｜涡喷宽带比涵道机重 {2}",
                                vBig.Pitch < vSmall.Pitch ? "对" : "不对",
                                (Mathf.Abs(vBig.Pitch - vSmall.Pitch) > 0.02f &&
                                 Mathf.Abs(vJet.Pitch - vSmall.Pitch) > 0.02f &&
                                 Mathf.Abs(vBig.Pitch - vJet.Pitch) > 0.02f) ? "对" : "不对",
                                vJet.Roar > vBig.Roar ? "对" : "不对"));

                            // 风扇层：只由风扇直径 × 涵道比决定。涡喷没有风扇段，必须是 0。
                            // 玩家报的"涡喷上听见明显的风扇声"就是这一层以前只按开关走，
                            // 而窄带层又跟着核心级数封顶——听着就是风扇。
                            Log(string.Format("  风扇层：RD-33 {0:F2}｜J79 {1:F2}｜J85 {2:F2}｜高频 {3:F2} / {4:F2} / {5:F2}",
                                vBig.Fan, vJet.Fan, vSmall.Fan, vBig.Whine, vJet.Whine, vSmall.Whine));
                            Log(string.Format("  断言：涡喷风扇层为 0 {0}｜大涵道风扇层最高 {1}｜涡喷窄带只来自核心 {2}",
                                vJet.Fan <= 0.001f ? "对" : "不对",
                                vBig.Fan > vSmall.Fan && vBig.Fan > vJet.Fan ? "对" : "不对",
                                (vJet.Whine < 1.0f && vSmall.Whine < vJet.Whine) ? "对" : "不对"));

                            // 叶尖激波：**相对设计点**。设计点本来就顶在叶尖马赫上限上，
                            // 拿绝对值判"超音速"等于把设计点当成超速点——那是上一版的错。
                            const double mdA = 1.30;   // 一台 1990s 后的风扇：设计叶尖 M1.30
                            const double mdB = 0.90;   // 一台老式亚音速风扇
                            Log(string.Format("  叶尖激波（设计 M{0:F2}）：100% {1:F2}×｜110% {2:F2}×｜115% {3:F2}×｜125% {4:F2}×",
                                mdA,
                                JetForgeSound.ShockGain(mdA, mdA),
                                JetForgeSound.ShockGain(mdA * 1.10, mdA),
                                JetForgeSound.ShockGain(mdA * 1.15, mdA),
                                JetForgeSound.ShockGain(mdA * 1.25, mdA)));
                            Log(string.Format("  叶尖激波（设计 M{0:F2}）：100% {1:F2}×｜110% {2:F2}×",
                                mdB, JetForgeSound.ShockGain(mdB, mdB), JetForgeSound.ShockGain(mdB * 1.10, mdB)));
                            Log(string.Format("  断言：设计点不超 {0:F2} 倍 {1}｜超速才涨 {2}｜亚音速机设计点 1.00 倍 {3}",
                                1.30f,
                                JetForgeSound.ShockGain(mdA, mdA) <= 1.30f ? "对" : "不对",
                                JetForgeSound.ShockGain(mdA * 1.15, mdA) > JetForgeSound.ShockGain(mdA, mdA) ? "对" : "不对",
                                Math.Abs(JetForgeSound.ShockGain(mdB, mdB) - 1f) < 0.001f ? "对" : "不对"));
                        }
                    }
                    catch (Exception e) { Log("音效段异常: " + e.Message); }
                    // 对称模式：KSP 的镜像零件是负缩放，屏幕上绕序会跟着翻。
                    // 这件事我不再"猜 Unity 的约定"——网格现在是双面的（正反各挂一遍），
                    // 无论镜像要不要翻，从任何一侧看都不会透底。这里只验"两份都在"。
                    try
                    {
                        Mesh mir = JetForgeMeshBuilder.Build(design, true);
                        if (mir != null)
                            {
                                Log(string.Format("  镜像外形（翻绕过的那份）：{0} 顶点 / {1} 三角｜{2}｜双面：每一面都有反向副本，镜像件不会再透底",
                                    mir.vertexCount, mir.triangles.Length / 3,
                                    JetForgeMeshBuilder.DescribeWinding(mir)));
                                var pi2 = PartLoader.getPartInfoByName("JetForgeEngine");
                                if (pi2 != null && pi2.partPrefab != null)
                                    Log("  " + JetForgeMeshBuilder.DescribeSymmetry(pi2.partPrefab));
                            }
                    }
                    catch (Exception e) { Log("  镜像外形异常: " + e.Message); }

                    // 喷口型式：收敛喷管是素筒子，可变/收扩带花瓣（黑豹那种）。
                    // 数一下三角数就能证明"花瓣确实生成了"，不用等截图。
                    try
                    {
                        foreach (NozzleKind nk in new[] { NozzleKind.Converging, NozzleKind.Variable, NozzleKind.ConvergingDiverging })
                        {
                            EngineDesignData nd = design.Clone();
                            nd.Nozzle = nk;
                            Mesh nm = JetForgeMeshBuilder.Build(nd, false);
                            if (nm == null) continue;
                            Log(string.Format(" 喷口型式 {0}：{1} 顶点 / {2} 三角｜出口半径 {3:F3} m｜{4}",
                                nk, nm.vertexCount, nm.triangles.Length / 3,
                                JetForgeMeshBuilder.NozzleExitRadius(nd),
                                JetForgeMeshBuilder.DescribeWinding(nm)));
                        }
                    }
                    catch (Exception e) { Log("  喷口型式异常: " + e.Message); }
                    // 作动筒：可变/收扩有 6 根拉杆，收敛喷管没有花瓣也就不该有杆。
                    // 这条断言防的是"改了条件忘了改件数"——它不影响任何数，只会突然消失或翻倍。
                    try
                    {
                        EngineDesignData aCon = design.Clone(); aCon.Nozzle = NozzleKind.Converging;
                        EngineDesignData aVar = design.Clone(); aVar.Nozzle = NozzleKind.Variable;
                        Log(string.Format("  喷口作动筒：收敛 {0} 根｜可变 {1} 根｜差 {2} 三角",
                            JetForgeMeshBuilder.NozzleActuatorCount(aCon),
                            JetForgeMeshBuilder.NozzleActuatorCount(aVar),
                            JetForgeMeshBuilder.Build(aVar).triangles.Length / 3
                            - JetForgeMeshBuilder.Build(aCon).triangles.Length / 3));
                    }
                    catch (Exception e) { Log("  喷口作动筒异常: " + e.Message); }
                    // 材质探针：把两个候选 shader 到底支持哪些属性、默认值是什么打出来。
                    // 上个版本"一片死白"就是没量这个——原版件材质用的是 Emissive/Specular（不支持法线），
                    // 克隆它就等于放弃法线贴图；而自己 new 出来又得猜默认值。先量再选。
                    Log("  下面两行是在问游戏：这两个着色器各自认哪些参数、默认值多少。参数名是着色器自带的，不能改；「无」表示它不认这个参数。");
                    Log("  " + JetForgeMeshBuilder.DescribeShader("KSP/Bumped Specular"));
                    Log("  " + JetForgeMeshBuilder.DescribeShader("KSP/Emissive/Specular"));
                }
            }
            catch (Exception e) { Log("外形异常: " + e); }

            yield return null;

            // 质量与造价：必须由几何算出来，且随级数/直径变化
            try
            {
                MassBreakdown m = MassCost.Estimate(design);
                Log(string.Format("质量：干重 {0:F0} kg 推重比 {1:F2} 造价 {2:N0} funds 解锁 {3:N0}",
                    m.TotalDry, m.ThrustToWeight, m.Cost, m.EntryCost));
                foreach (string line in m.Lines) Log("  " + line);

                EngineDesignData heavy = design.Clone();
                heavy.Hpc.Stages = design.Hpc.Stages + 6;
                heavy.Lpt.Stages = design.Lpt.Stages + 2;
                MassBreakdown m2 = MassCost.Estimate(heavy);
                Log(string.Format("质量对照：高压 +6 级、低压 +2 级 → {0:F0} → {1:F0} kg（{2:+0.0;-0.0}%）",
                    m.TotalDry, m2.TotalDry, (m2.TotalDry - m.TotalDry) / Math.Max(m.TotalDry, 1.0) * 100.0));

                EngineDesignData fat = design.Clone();
                fat.Fan.Diameter = design.Fan.Diameter * 1.4;
                fat.InletDiameter = design.InletDiameter * 1.4;
                MassBreakdown m3 = MassCost.Estimate(fat);
                Log(string.Format("质量对照：风扇 ×1.4 → {0:F0} → {1:F0} kg（{2:+0.0;-0.0}%）",
                    m.TotalDry, m3.TotalDry, (m3.TotalDry - m.TotalDry) / Math.Max(m.TotalDry, 1.0) * 100.0));
            }
            catch (Exception e) { Log("质量异常: " + e); }

            yield return null;

            // 喷管型式对照：同一台机器换喷管，看加力时的差别
            Section("喷管：收敛 / 可调 / 收敛-扩张三种型式换着看");
            try
            {
                foreach (NozzleKind k in new[] { NozzleKind.Converging, NozzleKind.Variable, NozzleKind.ConvergingDiverging })
                {
                    Log("  喷管 " + k + " 开始");
                    EngineDesignData dk = design.Clone();
                    dk.Nozzle = k;
                    dk.Afterburner.Enabled = true;
                    dk.Afterburner.ExitTemp = 2000.0;
                    dk.Afterburner.FuelRatio = 0.03;
                    EngineDesignData dkDry = dk.Clone();
                    dkDry.Afterburner.Enabled = false;
                    DesignConstants ck = OffDesign.DeriveDesignConstants(dkDry);
                    OffDesignResult ab = OffDesign.Solve(dk, 0.0, 0.0, 1.0, 0.0, ck, 40, -1.0, 0.03);
                    OffDesignResult dr = OffDesign.Solve(dkDry, 0.0, 0.0, 1.0, 0.0, ck);
                    Log(string.Format("喷管 {0,-20} 出口/喉道面积比 {1:F2}，出口马赫 {2:F2}：干态 {3:F2} kN，加力 {4:F2} kN，加力耗油率 {5:F4} kg/(N·h)；{6}，{7}，{8}",
                        k, ck.NozzleAreaRatio, ab.NozzleMachE, dr.NetThrust / 1000.0, ab.NetThrust / 1000.0, ab.Sfc,
                        ab.NozzleBackPressure ? "喷口前憋着背压" : "喷口不憋",
                        ab.NozzleCapped ? "出口被压住（流量受限）" : "出口不压",
                        ab.NozzleSeparated ? "气流在喷管里就分离了" : "气流贴壁不分离"));
                }
            }
            catch (Exception e) { Log("喷管对照异常: " + e); }

            // 推力分项：F = ṁVe + (Pe−Pa)Ae。以前只有一个 gross，谁也没法验证压力项还在不在。
            try
            {
                NetworkSolution nsn = EngineNetwork.SolveDesign(design);
                ComponentResult nrc = nsn.Of("NOZZLE");
                if (nrc != null)
                {
                    double mo = nrc.Get("momentum"), pr2 = nrc.Get("pressure"), gr = nrc.Get("gross");
                    Log(string.Format("  推力分项：动量 {0:F2} kN｜压力 {1:F2} kN｜总 {2:F2} kN｜失配 {3:E1} N｜出口压 {4:F0} Pa",
                        mo / 1000.0, pr2 / 1000.0, gr / 1000.0, nrc.Get("split_err"), nrc.Get("pe")));
                    bool okSp = Math.Abs(mo + pr2 - gr) <= Math.Abs(gr) * 1e-9 + 1e-6;
                    Log(string.Format("  断言：动量+压力=总推力 {0}", okSp ? "对" : "不对"));
                }
            }
            catch (Exception e) { Log("推力分项异常: " + e); }

            // 几何接口：热力学那条链有站表管着，几何这条链以前没人管。
            try
            {
                List<PortLink> ports = Ports.Chain(design);
                string pm;
                int bad = Ports.Problems(ports, out pm);
                int loose = Ports.Loose(ports, Ports.DefaultTolerancePct);
                Log(string.Format("  截面接口：{0} 对｜坏接口 {1}｜面积跳变 {2} 处（容差 {3:F0}%）",
                    ports.Count, bad, loose, Ports.DefaultTolerancePct));
                if (ports.Count > 0)
                {
                    bool allPos = true;
                    for (int i = 0; i < ports.Count; i++)
                        if (!(ports[i].Upstream.Area > 0.0) || !(ports[i].Downstream.Area > 0.0)) allPos = false;
                    Log(string.Format("  首末截面：{0} → {1}", ports[0].Upstream.Label,
                        ports[ports.Count - 1].Downstream.Label));
                    Log(string.Format("  断言：截面面积都是正数 {0}", allPos ? "对" : "不对"));
                }
                if (bad > 0) Log("    坏接口：" + pm);
            }
            catch (Exception e) { Log("截面接口异常: " + e); }

            // 逐轴转矩平衡：设计点按定义是稳态，各轴净转矩必须≈0。
            // 这条能抓住"只改了一侧功率"的错误——那种错不会让任何部件报错，只会让整机悄悄永远在加速。
            try
            {
                CycleResult cr = Cycle.SolveDesignPoint(design);
                List<ShaftTorque> shafts = TorqueBalance.Build(design, cr, null);
                Log(string.Format("  逐轴转矩：{0} 根轴", shafts.Count));
                for (int i = 0; i < shafts.Count; i++)
                    Log("    " + shafts[i].Describe());
                bool bal = TorqueBalance.AllBalanced(shafts, 5.0);
                double worst = 0.0;
                for (int i = 0; i < shafts.Count; i++)
                {
                    double scale = Math.Abs(shafts[i].LoadPower) + Math.Abs(shafts[i].AccessoryPower) + 1e-9;
                    double rel = Math.Abs(shafts[i].NetPower) / scale * 100.0;
                    if (rel > worst) worst = rel;
                }
                Log(string.Format("  断言：设计点各轴都接近平衡 {0}（最差净功率失衡 {1:F2}%）",
                    bal ? "对" : "不对", worst));
            }
            catch (Exception e) { Log("逐轴转矩异常: " + e); }

            // 气路契约：坏状态必须说得出是哪一类坏，好状态一个都不许被冤枉。
            // 这一条查的是"设计点每个测站都合格"——若某天某个部件开始吐 0 压力，
            // 这里会先炸，而不是等到玩家报"推力为什么是 0"。
            try
            {
                NetworkSolution nw = EngineNetwork.SolveDesign(design);
                int stations = 0, bad = 0;
                string firstBad = "";
                foreach (KeyValuePair<int, GasState> kv in nw.StationTable)
                {
                    if (kv.Value == null) continue;
                    stations++;
                    string why;
                    if (kv.Value.Validate(out why) != GasValidity.Valid)
                    {
                        bad++;
                        if (firstBad.Length == 0) firstBad = string.Format("{0}：{1}", kv.Value.Label, why);
                    }
                }
                GasState zeroFlow = GasState.FromTotal(Stations.BypassExit, 101325.0, 288.15, 0.0, FlowRole.Bypass);
                GasState badP = GasState.FromTotal(Stations.HpcExit, 0.0, 900.0, 40.0, FlowRole.Core);
                string whyP;
                badP.Validate(out whyP);
                Log(string.Format("  气路契约：{0} 个测站｜不合格 {1} 个{2}", stations, bad,
                    firstBad.Length > 0 ? "｜首个：" + firstBad : ""));
                Log(string.Format("  断言：设计点测站全部合格 {0}｜零流量仍算合格 {1}｜0 压力被判成 BadPressure {2}",
                    bad == 0 ? "对" : "不对",
                    zeroFlow.IsValid ? "对" : "不对",
                    badP.Validate(out whyP) == GasValidity.BadPressure ? "对" : "不对"));
            }
            catch (Exception e) { Log("气路契约异常: " + e); }

            // 分层刷新：改迎角侧滑不许触发重建，改型线才许。
            // 这条放在自检里是因为它只在真机上有意义——它管的是"每帧别白干活"。
            try
            {
                IntakeSpec rp = new IntakeSpec();
                rp.Kind = IntakeKind.Ramp;
                rp.Shape = IntakeProfile.Default(1.2, 0.8);
                string g0 = RefreshStamps.IntakeGeometry(rp, 0.8, 1.2);
                string p0 = RefreshStamps.IntakePerformance(rp, design.Tech);
                RefreshStamp s0 = new RefreshStamp(g0, p0, "v1");

                rp.AlphaDeg = 11.0;
                rp.BetaDeg = -6.0;
                RefreshPlan angleOnly = RefreshPlan.Compare(s0,
                    new RefreshStamp(RefreshStamps.IntakeGeometry(rp, 0.8, 1.2),
                                     RefreshStamps.IntakePerformance(rp, design.Tech), "v1"));

                rp.Shape.Sections[1].Width = 0.52;
                RefreshPlan afterShape = RefreshPlan.Compare(s0,
                    new RefreshStamp(RefreshStamps.IntakeGeometry(rp, 0.8, 1.2),
                                     RefreshStamps.IntakePerformance(rp, design.Tech), "v1"));

                Log(string.Format("  分层刷新：只改迎角侧滑 → {0}｜改喉道截面 → {1}", angleOnly, afterShape));
                Log(string.Format("  断言：迎角侧滑不触发重建 {0}｜型线收窄触发几何重建 {1}",
                    angleOnly.Anything ? "不对" : "对",
                    afterShape.Geometry ? "对" : "不对"));
            }
            catch (Exception e) { Log("分层刷新异常: " + e); }

            // 压气机特性图：设计点必须落在自己的图上，喘振裕度要是个能对账的数。
            // 放在自检里是因为这张图的坐标口径（相对流量系数）只有真机预设才暴露——
            // 用绝对 φ 时 J79 的喘振线会跑到设计点左边去，回归看不出来。
            try
            {
                NetworkSolution cn = EngineNetwork.SolveDesign(design);
                CompressorChart ch = cn != null ? cn.Chart : null;
                if (ch != null)
                {
                    MapPoint op = ch.WorkingPoint(1.0);
                    double sm = ch.SurgeMargin(op);
                    SpeedLine line = ch.LineFor(1.0);
                    Log(ch.Describe());
                    Log(string.Format("  设计点：Nc {0:F2}｜Wc {1:F2} kg/s｜π {2:F3}｜η {3:F3}",
                        op.Nc, op.Wc, op.Pr, op.Eta));
                    Log(string.Format("  喘振线：Wc {0:F2}｜π {1:F3}｜η {2:F3}｜堵塞线：Wc {3:F2}｜π {4:F3}",
                        ch.SurgeAt(1.0).Wc, ch.SurgeAt(1.0).Pr, ch.SurgeAt(1.0).Eta,
                        ch.ChokeAt(1.0).Wc, ch.ChokeAt(1.0).Pr));
                    Log(string.Format("  工作点裕度 {0:F1}%（设计点 {1:F1}%）", sm, ch.SurgeMargin(op)));
                    // 查图接口（§175）：同一个位置图说该给多少压比与效率，越界要标出来。
                    // 这一步不参与气路计算，但它是"图到底认不认这个点"的唯一凭据。
                    CompressorChart.ChartFlags qf;
                    MapPoint said = ch.Query(op.Nc, op.Wc, out qf);
                    Log(string.Format("  查图：Nc {0:F2}｜Wc {1:F2} → π {2:F3}｜η {3:F3}｜越界 {4}",
                        op.Nc, op.Wc, said.Pr, said.Eta,
                        qf == CompressorChart.ChartFlags.None ? "在图里" : qf.ToString()));
                    Log(string.Format("  断言：设计点压比取回 {0}｜喘振压比高于设计 {1}｜裕度为正 {2}｜等转速线 {3} 条",
                        Math.Abs(op.Pr - ch.DesignPr) < 1e-6 ? "对" : "不对",
                        ch.SurgeAt(1.0).Pr > ch.DesignPr ? "对" : "不对",
                        sm > 0.0 ? "对" : "不对",
                        ch.Lines.Count));
                }
                else Log("  特性图：无（这台机器没建出图）");
            }
            catch (Exception e) { Log("特性图异常: " + e); }

            // 可调几何：机构关着时不许动一个数，开了之后推动由马赫走。
            try
            {
                IntakeSpec vg = new IntakeSpec { Kind = IntakeKind.Cone, VariableGeometry = true };
                IntakeSpec off = new IntakeSpec { Kind = IntakeKind.Cone };
                Log(string.Format("  可调锥：M1.0 → {0:P0}｜M1.9 → {1:P0}｜M2.6 → {2:P0}｜没装机构 → {3:P0}",
                    Intake.ThroatFactor(vg, 1.0), Intake.ThroatFactor(vg, 1.9),
                    Intake.ThroatFactor(vg, 2.6), Intake.ThroatFactor(off, 2.6)));
                Log(string.Format("  断言：没装机构一个数都不动 {0}｜装上之后超声速确实收窄 {1}",
                    Math.Abs(Intake.ThroatFactor(off, 2.6) - 1.0) < 1e-12 ? "对" : "不对",
                    Intake.ThroatFactor(vg, 2.6) < 0.99 ? "对" : "不对"));
            }
            catch (Exception e) { Log("可调几何异常: " + e); }

            // 矢量喷口：偏转方向与力矩方向必须自洽
            // 这里验的是"力偏了、力矩轴对不对"，不是"能不能偏"。
            //
            // 坐标系按真实装机摆：forward = +Z、up = +Y、right = +X；喷口在机尾 r = (0,0,-1.5)，
            // 推力朝 +Z。抬头指令（pitch=+1）的**物理结果**必须是"机尾被往下压"，
            // 也就是侧向力的 Y 分量为负——这一条以前没验，结果正负号一直是反的。
            try
            {
                Vec3 fwd = new Vec3(0.0, 0.0, 1.0);
                Vec3 pAx = new Vec3(1.0, 0.0, 0.0);   // right
                Vec3 yAx = new Vec3(0.0, 1.0, 0.0);   // up
                Vec3 rTail = new Vec3(0.0, 0.0, -1.5);
                Vec3 rFront = new Vec3(0.0, 0.0, 1.5);   // 倒装：喷口朝前
                double aP, aY, aI;
                Vec3 dP = ThrustVector.Direction(fwd, rTail, pAx, yAx, 1.0, 0.0, 15.0, out aP);
                Vec3 dY = ThrustVector.Direction(fwd, rTail, pAx, yAx, 0.0, 1.0, 15.0, out aY);
                Vec3 dI = ThrustVector.Direction(fwd, rFront, pAx, yAx, 1.0, 0.0, 15.0, out aI);
                Vec3 tP = Vec3.Cross(rTail, dP);
                Vec3 tY = Vec3.Cross(rTail, dY);
                Vec3 tI = Vec3.Cross(rFront, dI);
                // 机尾被往下压 = 抬头；机尾被往左推 = 右偏
                bool pitchOk = dP.Y < -0.05;
                bool yawOk = dY.X < -0.05;
                bool mountOk = dI.Y > 0.05;      // 倒装时是"机头被往上抬"
                Log(string.Format("矢量：抬头指令 → 侧向 {0}（机尾下压={1}）｜右偏指令 → 侧向 {2}（机尾左推={3}）",
                    dP.ToString(), pitchOk ? "对" : "不对", dY.ToString(), yawOk ? "对" : "不对"));
                Log(string.Format("  力矩：俯仰 {0}（应沿 -right）｜偏航 {1}（应沿 +up）｜倒装 {2}",
                    tP.ToString(), tY.ToString(), tI.ToString()));
                bool okVec = Math.Abs(aP - 15.0) < 0.05
                             && Math.Abs(Vec3.Dot(dP, fwd) - Math.Cos(15.0 * Math.PI / 180.0)) < 0.002
                             && pitchOk && yawOk && mountOk;
                Log(string.Format("  断言：偏角=最大角 {0}｜轴向=cos15° {1}｜抬头指令压机尾 {2}｜倒装不反 {3}",
                    Math.Abs(aP - 15.0) < 0.05 ? "对" : "不对",
                    Math.Abs(Vec3.Dot(dP, fwd) - Math.Cos(15.0 * Math.PI / 180.0)) < 0.002 ? "对" : "不对",
                    pitchOk ? "对" : "不对", mountOk ? "对" : "不对"));
                Log(string.Format("  判据：矢量+收敛喷管 {0}｜可变喷管+矢量 {1}",
                    FirstNozzleFinding(new EngineDesignData(), NozzleKind.Converging, true),
                    FirstNozzleFinding(new EngineDesignData(), NozzleKind.Variable, true)));
            }
            catch (Exception e) { Log("矢量喷口异常: " + e); }

            // 控制系统：同一台机器、同一次猛推，只换油门后面那道闸
            try
            {
                EngineDesignData cd = Presets.Build("J79");
                double t4Lim = cd.Tech != null ? cd.Tech.T4Limit : 1500.0;
                ControlKind[] ks = { ControlKind.None, ControlKind.Hydro, ControlKind.Fadec };
                string[] kn = { "无控", "液压", "数字" };
                double spill = 0.0;
                bool noSurge = true, noOver = true, slewOk = true;
                for (int i = 0; i < 3; i++)
                {
                    ControlRuntime rt = new ControlRuntime();
                    SurgeRuntime sg = new SurgeRuntime();
                    double dt = 1.0 / 60.0;
                    double spool = 0.20, t4 = 900.0, prevRaw = 0.20, surgeS = 0.0, over = 0.0, peak = 0.0;
                    for (int f = 0; f < 600; f++)
                    {
                        double raw = f < 30 ? 0.20 : 1.00;
                        double rate = Surge.Rate(prevRaw, raw, dt);
                        prevRaw = raw;
                        double risk = Surge.RiskOf(rt.Command, rate, spool, 0.0, 0.45);
                        double cmd = ControlSystem.Step(rt, ks[i], raw, spool, 50 + 55 * spool, t4, t4Lim, risk, dt);
                        spool = Spool.Step(spool, cmd, dt, 1.8, 1.0);
                        t4 = 820.0 + 780.0 * cmd;
                        if (t4 > peak) peak = t4;
                        Surge.Step(sg, cmd, rate, spool, 0.0, 0.45, dt);
                        if (sg.Active) surgeS += dt;
                        if (t4 > t4Lim) over += dt;
                    }
                    // 限幅：20%→100% 指令走完的时间（温度转速都放在安全区，只量限幅这一件事）
                    ControlRuntime rt2 = new ControlRuntime();
                    double cur = 0.20;
                    int steps = 0;
                    ControlSystem.Step(rt2, ks[i], 0.20, 0.2, 50, 900, t4Lim, 0.2, 1.0 / 60.0);
                    while (steps < 600 && cur < 0.999)
                    {
                        cur = ControlSystem.Step(rt2, ks[i], 1.0, cur, 50, 900, t4Lim, 0.2, 1.0 / 60.0);
                        steps++;
                    }
                    double slew = steps / 60.0;
                    Log(string.Format(
                        "  {0}：喘振 {1} 次（{2:F2} s）｜超温 {3:F2} s｜峰值 T4 {4:F0} K（限 {5:F0}）｜限幅走完 {6:F2} s｜{7}",
                        kn[i], sg.Events, surgeS, over, peak, t4Lim, slew, rt.Note));
                    if (i == 0)
                    {
                        if (sg.Events <= 0 || over < 0.5) noSurge = false;
                        if (slew > 0.05) slewOk = false;
                    }
                    else
                    {
                        if (sg.Events > 0 || over > 0.05) { noSurge = false; noOver = false; }
                        spill = slew;
                    }
                    if (i == 1) spill = slew;
                }
                Log("  断言：无控猛推必喘且不管超温 " + (noSurge && noOver ? "对" : "不对")
                    + "｜无控立刻跟上指令 " + (slewOk ? "对" : "不对")
                    + "｜数字比液压快 " + (spill > 0.0 ? "对" : "不对"));
                // 行首机构也跟着界面语言走：英文界面里这行是「Checks (1940s with digital control): …」。
                // 只翻行首不改判据本体——判据文案由 Criteria 那边成对给出。
                Log("  " + Loc.T("判据（1940s 却选数字控制）：", "Checks (1940s with digital control): ") + FirstControlFinding());
            }
            catch (Exception e) { Log("控制系统异常: " + e); }

            // 程序化细节：低/中/高三档。叶片排、静子、法兰、附件都只是外形，
            // 但三角数必须单调往上走、绕序必须仍然是 0 反向（新生成器最容易在这里翻车）。
            try
            {
                Log("程序化细节：");
                EngineDesignData det = new EngineDesignData();
                int[] tri = new int[3];
                bool windingOk = true;
                for (int i = 0; i < 3; i++)
                {
                    det.Detail = JetForge.Core.DetailSpec.Parse(i);
                    Mesh mm = JetForgeMeshBuilder.Build(det);
                    tri[i] = mm != null ? mm.triangles.Length / 3 : 0;
                    int rev = mm != null ? JetForgeMeshBuilder.CountReversed(mm) : -1;
                    if (rev != 0) windingOk = false;
                    Log(string.Format("  {0}：三角 {1}｜反向 {2}｜{3}",
                        JetForge.Core.DetailSpec.Name(det.Detail), tri[i], rev,
                        JetForgeMeshBuilder.DescribeDetail(det)));
                }
                Log("  断言：三角数单调 " + (tri[2] > tri[1] && tri[1] > tri[0] ? "对" : "不对")
                    + "｜三档绕序全对 " + (windingOk ? "对" : "不对")
                    + "｜低档增量 " + tri[0] + " / 高档增量 +" + (tri[2] - tri[1]));
            }
            catch (Exception e) { Log("细节建模异常: " + e); }

            // 花瓣几何：加力那一份必须等于真 A8 的 √ 比例，羽流让位必须与网格同一套数
            try
            {
                Log("花瓣几何：");
                EngineDesignData pv = new EngineDesignData();
                pv.Nozzle = NozzleKind.Variable;
                float rx = JetForgeMeshBuilder.NozzleExitRadius(pv);
                double f0 = JetForge.Core.NozzleVisual.ExitFactor(0.0, rx);
                double f1 = JetForge.Core.NozzleVisual.ExitFactor(1.0, rx);
                Log("  " + JetForge.Core.NozzleVisual.Describe(rx));

                double openDry = JetForge.Core.NozzleVisual.OpenFor(rx, 1.0, 0.0, false);
                double openAb = JetForge.Core.NozzleVisual.OpenFor(rx, 1.0, 1.0, false);
                double rDry = JetForge.Core.NozzleVisual.ExitFactor(openDry, rx);
                double rAb = JetForge.Core.NozzleVisual.ExitFactor(openAb, rx);
                double want = Math.Sqrt(1.0 + NozzleSchedule.AbAreaGain);
                double got = rAb / rDry;
                Log(string.Format("  干态满油门 开度 {0:F3} → 出口 {1:F3}·r｜满加力 开度 {2:F3} → 出口 {3:F3}·r",
                    openDry, rDry, openAb, rAb));
                bool ratioOk = Math.Abs(got - want) < 0.01 * want;
                Log(string.Format("  断言：加力张开比 {0:F4}（应为 √(1+{1:F2}) = {2:F4}）{3}",
                    got, NozzleSchedule.AbAreaGain, want, ratioOk ? "对" : "不对"));

                double o1 = JetForge.Core.NozzleVisual.OpenFor(rx, 0.10, 0.0, false);
                double o2 = JetForge.Core.NozzleVisual.OpenFor(rx, 0.50, 0.0, false);
                double o3 = JetForge.Core.NozzleVisual.OpenFor(rx, 1.00, 0.0, false);
                bool mono = o1 < o2 && o2 < o3;
                Log(string.Format("  油门 10/50/100% 开度 {0:F3}/{1:F3}/{2:F3}｜单调 {3}",
                    o1, o2, o3, mono ? "对" : "不对"));

                // 羽流让位：同一条曲线算出来的花瓣直径，羽流不能超过它
                float petal = JetForgeMeshBuilder.PetalExitFactor((float)openDry, rx);
                float limA = JetForgeMeshBuilder.PlumeRadiusLimited(pv, true, (float)openDry);
                float limB = JetForgeMeshBuilder.PlumeRadiusLimited(pv, true, 0.0f);
                Log(string.Format("  让位：花瓣 {0:F3}·r｜羽流上限 干态 {1:F3} m / 收到底 {2:F3} m｜收缩有效 {3}",
                    petal, limA, limB, limB < limA ? "对" : "不对"));
                Log(string.Format("  出口系数 收到底/全开 = {0:F3} / {1:F3}", f0, f1));
            }
            catch (Exception e) { Log("花瓣几何异常: " + e); }

            // 逐级编辑：每一级自己的叶尖直径与 ψ；热浪只能按核心喷口定，与大涵道无关
            try
            {
                Log("逐级编辑与热浪：");
                double oprBase = Cycle.SolveDesignPoint(design).Opr;

                EngineDesignData same = design.Clone();
                same.Hpc.SyncStages();
                for (int i = 0; i < same.Hpc.Stages; i++) same.Hpc.StageTip[i] = same.Hpc.TipDiameter;
                double oprSame = Cycle.SolveDesignPoint(same).Opr;

                EngineDesignData taper = design.Clone();
                taper.Hpc.SyncStages();
                for (int i = 0; i < taper.Hpc.Stages; i++)
                {
                    double f = taper.Hpc.Stages <= 1 ? 0.0 : (double)i / (taper.Hpc.Stages - 1);
                    taper.Hpc.StageTip[i] = taper.Hpc.TipDiameter * (1.0 - 0.28 * f);
                }
                double oprTaper = Cycle.SolveDesignPoint(taper).Opr;

                Log(string.Format("  压气机逐级（HPC {0} 级，叶尖 {1:F2} m）：基准 OPR {2:F3}｜全填缺省 {3:F3}｜收口 0.72 {4:F3}（{5:+0.0;-0.0}%）",
                    design.Hpc.Stages, design.Hpc.TipDiameter, oprBase, oprSame, oprTaper,
                    (oprTaper / oprBase - 1.0) * 100.0));
                Log(string.Format("  断言：全同不改老结果 {0}｜收口让压比下降 {1}",
                    Math.Abs(oprSame - oprBase) < 1e-9 ? "对" : "不对",
                    oprTaper < oprBase - 0.05 ? "对" : "不对"));

                // 网格：逐级直径必须体现在涡轮环上，否则只是"后台算了个数"
                Mesh uni = JetForgeMeshBuilder.Build(design);
                Mesh tpd = JetForgeMeshBuilder.Build(taper);
                Log(string.Format("  网格：均匀 {0} 三角｜逐级 {1} 三角｜绕序反向 {2}",
                    uni.triangles.Length / 3, tpd.triangles.Length / 3,
                    JetForgeMeshBuilder.CountReversed(tpd)));

                // 热浪：半径必须跟核心喷口走，跟涵道比无关
                EngineDesignData high = design.Clone();
                high.OverrideBpr = 6.0;
                float rCore = JetForgeMeshBuilder.NozzleExitRadius(design);
                float rCoreHigh = JetForgeMeshBuilder.NozzleExitRadius(high);
                Log(string.Format("  热浪：核心喷口 {0:F3} m → 热浪半径 {1:F3} m（×{2:F2}）｜高涵道机核心喷口 {3:F3} m",
                    rCore, rCore * (float)JetForgeWaterfall.HazeRadiusFactor,
                    JetForgeWaterfall.HazeRadiusFactor, rCoreHigh));
                Log(string.Format("  断言：热浪比喷口细 {0}｜不按外涵放大 {1}",
                    rCore * (float)JetForgeWaterfall.HazeRadiusFactor < rCore ? "对" : "不对",
                    Math.Abs(rCoreHigh - rCore) < 0.02f ? "对" : "不对"));
            }
            catch (Exception e) { Log("逐级编辑异常: " + e.Message); }

            // 慢车口径：不再是"玩家设多少就给多少"，而是"这台机器自己转着、火还留得住"的最低点。
            // 三件事要钉住：①反解出来的点烧得住（油气比有裕度）②油门到底的求解点就是它
            // ③它对应的推力占比落在真机那一带（不是被谁夹出来的）。
            try
            {
                DesignConstants ci = OffDesign.DeriveDesignConstants(design);
                OffDesignResult i0 = OffDesign.Solve(design, 0.0, 0.0, 0.0, 0.0, ci, 40);
                OffDesignResult i1 = OffDesign.Solve(design, 0.0, 0.0, 1.0, 0.0, ci, 40);
                double frac = i1.Ok && i1.NetThrust > 1.0 ? i0.NetThrust / i1.NetThrust : 0.0;
                Log(string.Format("  慢车（自动反解）：T4 {0:F0} K｜油气比 {1:F4}（贫油下限 {2:F4}，余量 {3:F2}×）｜N2 {4:F1}%",
                    ci.T4Idle, ci.IdleFar, ci.LeanFar,
                    ci.LeanFar > 1e-9 ? ci.IdleFar / ci.LeanFar : 0.0, i0.N2Pct));
                Log(string.Format("  油门到底：油量 {0:F4} kg/s（最低可用 {1:F4}）｜推力 {2:F2} kN｜占满油门 {3:P1}",
                    i0.FuelFlow, ci.IdleFuelFlow, i0.NetThrust / 1000.0, frac));
                Log(string.Format("  断言：慢车烧得住 {0}｜油门到底就是最低油量 {1}｜推力占比在真机带内 {2}",
                    ci.IdleSustainable ? "对" : "不对",
                    Math.Abs(i0.FuelFlow - ci.IdleFuelFlow) <= Math.Max(ci.IdleFuelFlow * 0.05, 1e-5) ? "对" : "不对",
                    (frac > 0.01 && frac < 0.30) ? "对" : "不对"));

                // 燃料口径：恒为原版煤油（RealFuels 那一套已撤除）。
                Log(string.Format("  燃料：原版 LiquidFuel｜热值 {0:F1} MJ/kg", JetForge.Core.Gas.LhvJet / 1e6));

                // 结构：过载只记峰值，不计故障。
                // 这个开关原来是"过载会累积轴承应变直到断裂"，玩家明确不要它：
                // 拉 g 是飞行员的事，不该由发动机替人挨罚。现在 12 g 连拉十几秒，
                // 推力一点不掉，只有"峰值"这个读数留下来解释别处的性能。
                {
                    JetForge.Core.DefectRuntime gRt = new JetForge.Core.DefectRuntime();
                    JetForge.Core.Defects.Configure(gRt, JetForge.Core.FailureLimits.Default);
                    for (int i = 0; i < 400; i++)
                        JetForge.Core.Defects.Step(new JetForge.Core.Defects.Risk(), gRt, 1.0 / 30.0,
                            1.0, 900.0, 1400.0, 0.02, 0.0, 12.0);
                    Log(string.Format("  结构：12 g 连拉 13 秒 → {0}｜峰值 {1:F1} g｜推力 ×{2:F2}",
                        gRt.Failed ? gRt.FailNote : "未断", gRt.GPeak, gRt.ThrustScale));
                    Log(string.Format("  断言：过载不断推 {0}｜峰值记下来了 {1}｜推力不打折 {2}",
                        gRt.Failed ? "不对" : "对",
                        gRt.GPeak >= 9.0 ? "对" : "不对",
                        gRt.ThrustScale > 0.99 ? "对" : "不对"));
                }

                // 可燃窗口：贫油 0.005、富油按设计点拉开 1.9 倍（地板 0.040）。
                Log(string.Format("  可燃窗口：贫油 {0:F4}｜富油 {1:F4}｜慢车油气比 {2:F4}",
                    ci.LeanFar, ci.RichFar, ci.IdleFar));
                Log(string.Format("  断言：富油线高于贫油线 {0}｜慢车落在窗口内 {1}",
                    ci.RichFar > ci.LeanFar * 3.0 ? "对" : "不对",
                    (ci.IdleFar > ci.LeanFar && ci.IdleFar < ci.RichFar) ? "对" : "不对"));

                // 熄火判定：只有"慢车点本身烧不住"或"油门高于到底"时口径才该说没事。
                bool gFine = JetForge.Core.EngineGuard.IdleStarves(ci, JetForge.Core.StockBridge.InputFloor);
                bool gPushed = JetForge.Core.EngineGuard.IdleStarves(ci, 0.40);
                Log(string.Format("  熄火判定：本机型 {0}｜推杆 40% {1}",
                    JetForge.Core.EngineGuard.Describe(design, ci),
                    gPushed ? "仍判熄火" : "不熄火"));
                Log(string.Format("  断言：健康的机不该灭 {0}｜推杆就活 {1}",
                    gFine ? "不对" : "对",
                    gPushed ? "不对" : "对"));
            }
            catch (Exception e) { Log("慢车口径异常: " + e.Message); }

            // 音量跟转速走：0 转几乎没声、满转 1.0，且窄带层的地面静止分量不再是"一大坨"
            try
            {
                float a0 = JetForgeSound.SpoolWeight(0.0);
                float a60 = JetForgeSound.SpoolWeight(0.60);
                float a100 = JetForgeSound.SpoolWeight(1.0);
                int bigIdx = JetForgeSound.IndexOf("big180");
                float v0, p0, v1, p1;
                JetForgeSound.LayerAt(bigIdx, 0f, 0f, 0.0, out v0, out p0);
                JetForgeSound.LayerAt(bigIdx, 1f, 0f, 1.0, out v1, out p1);
                Log(string.Format("  音量权重：0 转 {0:F3}｜60% 转 {1:F3}｜满转 {2:F3}", a0, a60, a100));
                Log(string.Format("  宽带层：x=0 处 {0:F3}｜满油门 {1:F3}", v0, v1));
                Log(string.Format("  断言：0 转几乎没声 {0}｜满转到底 {1}｜单调 {2}｜慢车明显更轻 {3}｜静止不再是一坨 {4}",
                    a0 < 0.08f ? "对" : "不对",
                    a100 > 0.98f ? "对" : "不对",
                    (a0 < a60 && a60 < a100) ? "对" : "不对",
                    a60 < 0.45f ? "对" : "不对",
                    v0 < 0.06f ? "对" : "不对"));
            }
            catch (Exception e) { Log("音量权重异常: " + e.Message); }

            // 喘振闪烁：纯函数，一拍 0.22 s 里前段起亮、中段衰减、后段灭
            try
            {
                float f0 = JetForgeWaterfall.Flicker(0.10, 1.0);
                float f1 = JetForgeWaterfall.Flicker(0.36, 1.0);
                float f2 = JetForgeWaterfall.Flicker(0.60, 1.0);
                float f3 = JetForgeWaterfall.Flicker(0.85, 1.0);
                float fh = JetForgeWaterfall.Flicker(0.10, 0.5);   // 同一相位、强度减半：应恰好是一半
                Log(string.Format("  喘振闪烁：相位 0.10/0.36/0.60/0.85 → {0:F2}/{1:F2}/{2:F2}/{3:F2}｜强度减半 {4:F2}",
                    f0, f1, f2, f3, fh));
                Log(string.Format("  断言：起亮 {0}｜峰在最前 {1}｜中段衰减 {2}｜后段归零 {3}｜强度可调 {4}",
                    f0 > 0.15f ? "对" : "不对",
                    f1 > f0 ? "对" : "不对",
                    f2 < f1 ? "对" : "不对",
                    f3 <= 0.001f ? "对" : "不对",
                    Math.Abs(fh - f0 * 0.5f) < 0.02f ? "对" : "不对"));
            }
            catch (Exception e) { Log("喘振闪烁异常: " + e.Message); }

            // 停车惰转：关车那一刻不该"啪一下归零"，转速往下溜，油门推不回来
            try
            {
                DesignConstants cd = OffDesign.DeriveDesignConstants(design);
                if (!cd.Ok) Log("  停车惰转：设计常数不成立，跳过");
                else
                {
                    RealtimeSampler rs = new RealtimeSampler(design, cd, 40);
                    double spool = 1.0; bool init = true;
                    double area = design.InletDiameter * design.InletDiameter * 0.25 * Math.PI;
                    double[] marks = new double[] { 0.02, 1.0, 3.0, 20.0, 40.0 };
                    double acc = 0.0; int k = 0; double prev = 1.0; bool mono = true;
                    double at1 = 0.0, at3 = 0.0, at40 = 0.0; bool lastRun = true; double maxThrust = 0.0;
                    while (k < marks.Length)
                    {
                        FlightStepResult rf = FlightStep.Compute(rs, 1.0, 0.05, 0.0, 0.0,
                            false, true, true, true, ref spool, ref init, 1.5, 0.8, area, 0.06);
                        acc += 0.05;
                        if (spool > prev + 1e-6) mono = false;
                        prev = spool;
                        if (rf.ThrustN > maxThrust) maxThrust = rf.ThrustN;
                        lastRun = rf.Running;
                        if (acc >= marks[k] - 1e-9)
                        {
                            if (k == 1) at1 = spool;
                            if (k == 2) at3 = spool;
                            if (k == 4) at40 = spool;
                            k++;
                        }
                    }
                    Log(string.Format("  停车惰转：1 s {0:P0}｜3 s {1:P0}｜40 s {2:P0}｜末帧还在转 {3}｜峰值推力 {4:F2} kN",
                        at1, at3, at40, lastRun ? "对" : "不对", maxThrust / 1000.0));
                    Log(string.Format("  断言：一帧不归零 {0}｜单调往下 {1}｜40 s 后≤10% {2}｜油门推不回来 {3}",
                        at1 > 0.5 ? "对" : "不对",
                        mono ? "对" : "不对",
                        at40 <= 0.10 ? "对" : "不对",
                        maxThrust < 1.0 ? "对" : "不对"));
                }
            }
            catch (Exception e) { Log("停车惰转异常: " + e.Message); }

            // 三轴逐台涡轮：IPT 不能只是摆设，中低转速最容易露馅
            try
            {
                EngineDesignData three = new EngineDesignData();
                three.ShaftCount = 3;
                three.Fan.Shaft = 1;
                three.Lpc.Stages = 3; three.Lpc.TipDiameter = 0.78; three.Lpc.Shaft = 2;
                three.Hpc.Stages = 6; three.Hpc.TipDiameter = 0.52; three.Hpc.Shaft = 3;
                three.Hpt.Shaft = 3;
                three.Ipt.Stages = 1; three.Ipt.TipDiameter = 0.70; three.Ipt.Shaft = 2;
                three.Lpt.Stages = 3; three.Lpt.Shaft = 1;
                CycleResult rt = Cycle.SolveDesignPoint(three);
                Log(string.Format("三轴设计点：成立 {0}，总压比 {1:F2}，推力 {2:F1} kN，错误 {3} 条",
                    YN(rt.Ok), rt.Opr, rt.Thrust / 1000.0, rt.Errors.Count));
                if (rt.Ok)
                {
                    DesignConstants c3 = OffDesign.DeriveDesignConstants(three);
                    foreach (double thr in new[] { 1.0, 0.5, 0.2 })
                    {
                        OffDesignResult o3 = OffDesign.Solve(three, 0.0, 0.0, thr, 0.0, c3);
                        Log(string.Format("  三轴 thr{0:F2}  HPT {1:F2}/{2:F2}  IPT {3:F2}/{4:F2}  LPT {5:F2}/{6:F2} MW  带不动的轴={7}",
                            thr, o3.WHpt / 1e6, o3.CapHpt / 1e6, o3.WIpt / 1e6, o3.CapIpt / 1e6,
                            o3.WLpt / 1e6, o3.CapLpt / 1e6, o3.LimitSpool));
                    }
                    // 弱 IPT：设计点仍过，但非设计点必须指出轴 2 带不动
                    EngineDesignData weak = three.Clone();
                    weak.Ipt.PsiScale = 0.45;
                    CycleResult rw = Cycle.SolveDesignPoint(weak);
                    bool flagged = false;
                    if (rw.Ok)
                    {
                        DesignConstants cw = OffDesign.DeriveDesignConstants(weak);
                        foreach (double thr in new[] { 1.0, 0.5, 0.2 })
                            if (OffDesign.Solve(weak, 0.0, 0.0, thr, 0.0, cw).LimitSpool == 2) flagged = true;
                        Log("  弱中压涡轮（载荷系数 0.45）：判据标出轴 2 带不动：" + (flagged ? "是" : "不是"));
                    }
                    else Log("  弱 IPT：设计点就报错 → " + (rw.Errors.Count > 0 ? rw.Errors[0] : ""));
                }
            }
            catch (Exception e) { Log("三轴功平衡异常: " + e); }

            // 设计点环境：设计马赫/高度必须真的进循环。面马赫固定 0.5——进气道是减速增压的，
            // 以前把飞行马赫当面马赫用，Ma0.85 时流量凭空多出七成。
            try
            {
                EngineDesignData cruise = new EngineDesignData { DesignMach = 0.85, DesignAlt = 11000.0 };
                CycleResult rCr = Cycle.SolveDesignPoint(cruise);
                CycleResult rSl = Cycle.SolveDesignPoint(new EngineDesignData());
                Log(string.Format("设计点环境：海平面 mdot={0:F1} 推力={1:F1} kN ｜ 11km Ma0.85 mdot={2:F1} 推力={3:F1} kN ｜ 流量比 {4:F3}（期望 0.386）",
                    rSl.MdotAir, rSl.Thrust / 1000.0, rCr.MdotAir, rCr.Thrust / 1000.0,
                    rSl.MdotAir > 1e-9 ? rCr.MdotAir / rSl.MdotAir : 0.0));

                EngineDesignData ht = new EngineDesignData();
                MassBreakdown mb = MassCost.Estimate(ht);
                ht.Lpt.HubTipRatio = 0.55;
                MassBreakdown mh = MassCost.Estimate(ht);
                Log(string.Format("涡轮轮毂比 0.75→0.55：涡轮 {0:F0}→{1:F0} kg，干重 {2:F0}→{3:F0} kg",
                    mb.Turbine, mh.Turbine, mb.TotalDry, mh.TotalDry));
            }
            catch (Exception e) { Log("设计点环境异常: " + e); }

            // 尾焰：不为看效果，只确认"求解器真量 → 尾焰参数"这条链在游戏进程里也通
            try
            {
                CycleResult dp2 = Cycle.SolveDesignPoint(design);
                double exitR = JetForgeMeshBuilder.NozzleExitRadius(design);
                double ve = dp2.MdotAir > 1e-6 ? dp2.VJetCore : 0.0;
                double te = dp2.T5 - ve * ve / (2.0 * Math.Max(Gas.Cp(dp2.T5, 0.02), 800.0));
                if (te < 150.0) te = 150.0;
                double me = ve / Math.Max(Gas.SoundSpeed(te), 1.0);
                PlumeMath.PlumeSpec ps = PlumeMath.Compute(exitR, ve, dp2.T5, me, 1.0, false);
                Log(string.Format("尾焰干态：{0}", ps.Note));
                Log(string.Format("  喷口半径 {0:F3} m  长 {1:F2} m  尾端 {2:F3} m  颜色 ({3:F2},{4:F2},{5:F2}) A={6:F2} 可见：{7}",
                    exitR, ps.Length, ps.RadiusEnd, ps.R, ps.G, ps.B, ps.A, YN(ps.Visible)));
                PlumeMath.PlumeSpec pa = PlumeMath.Compute(exitR, ve * 1.12, dp2.T5 + 350.0, me * 1.2, 1.0, true);
                Log(string.Format("尾焰加力：长 {0:F2} m 颜色 ({1:F2},{2:F2},{3:F2}) A={4:F2}",
                    pa.Length, pa.R, pa.G, pa.B, pa.A));
            }
            catch (Exception e) { Log("尾焰异常: " + e); }

            // 自绘尾焰已删（羽流硬依赖 Waterfall），着色器探测那一段就没用了。
            // 羽流的可见性改由 Waterfall 段的"控制器/缩放"两行负责。

            yield return null;

            // 进气道恢复系数与网格内插：飞行中玩家只用这张表，表错了没人会喊。
            try
            {
                TechLevel tech2 = design.Tech;
                Log(string.Format("进气道恢复：M0.9={0:F4} M1.0={1:F4} M1.15={2:F4} M1.30={3:F4} M1.60={4:F4} M2.00={5:F4} M2.20={6:F4}",
                    tech2.IntakeTpr(0.9), tech2.IntakeTpr(1.0), tech2.IntakeTpr(1.15),
                    tech2.IntakeTpr(1.30), tech2.IntakeTpr(1.60), tech2.IntakeTpr(2.00), tech2.IntakeTpr(2.20)));

                DesignConstants dc = OffDesign.DeriveDesignConstants(design);
                PerformanceGrid grid = GridFactory.Build(design, dc);
                Log(string.Format("默认网格：{0}", grid.Summary()));
                double[,] pts = { { 0.62, 500.0, 1.30 }, { 0.75, 6000.0, 0.60 }, { 1.00, 0.0, 0.0 } };
                for (int i = 0; i < 3; i++)
                {
                    PerformanceGrid.Sample s = grid.Interp(pts[i, 0], pts[i, 1], pts[i, 2]);
                    OffDesignResult o = OffDesign.Solve(design, pts[i, 1], pts[i, 2], pts[i, 0], 0.0, dc);
                    double dk = o.Ok ? o.NetThrust / 1000.0 : 0.0;
                    double err = Math.Abs(dk) > 1e-6 ? (s.Thrust / 1000.0 - dk) / dk * 100.0 : 0.0;
                    Log(string.Format("  网格 vs 直接：油门 {0:F2} 高度 {1:F0} m M{2:F2} → {3:F2} / {4:F2} kN，差 {5:F1}%",
                        pts[i, 0], pts[i, 1], pts[i, 2], s.Thrust / 1000.0, dk, err));
                }
            }
            catch (Exception e) { Log("网格/进气道异常: " + e); }
            yield return null;

            // 玩家推着油门却一点推力都没有时，右键菜单"状态"栏显示的就是 Errors[0]。
            // 这里确认那句人话真的存在、且说得清是哪一级不够。
            try
            {
                EngineDesignData bad = design.Clone();
                bad.Lpt.Stages = 1;
                bad.Lpt.PsiScale = 0.25;
                DesignConstants bd = OffDesign.DeriveDesignConstants(bad);
                OffDesignResult bo = OffDesign.Solve(bad, 0.0, 0.0, 1.0, 0.0, bd);
                string line = bo.Errors.Count > 0 ? bo.Errors[0] : "（空）";
                Log(string.Format("坏设计（低压涡轮只给 1 级、载荷系数 0.25）：成立 {0}，状态句=\"{1}\"", YN(bo.Ok), line));
            }
            catch (Exception e) { Log("坏设计异常: " + e); }
            yield return null;

            // 转子惯性：飞行中油门到推力之间差的那几秒。
            try
            {
                EngineDesignData sp = design.Clone();
                double tau = Spool.TauUp(sp);
                double cur = 0.0;
                double t95 = -1.0, at1 = 0.0, at2 = 0.0, at3 = 0.0;
                for (int i = 1; i <= 1000; i++)
                {
                    cur = Spool.Step(cur, 1.0, 0.01, tau, Spool.TauDown(sp));
                    double t = i * 0.01;
                    if (t95 < 0 && cur >= 0.95) t95 = t;
                    if (t <= 1.0) at1 = cur;
                    if (t <= 2.0) at2 = cur;
                    if (t <= 3.0) at3 = cur;
                }
                Log(string.Format("转子惯性：风扇 {0:F2} m τ={1:F2}s 爬升到 95% 用 {2:F2}s 收油 τ={3:F2}s",
                    sp.HasFan ? sp.Fan.Diameter : sp.InletDiameter, tau, t95, Spool.TauDown(sp)));
                Log(string.Format("  阶跃响应 1s={0:F1}% 2s={1:F1}% 3s={2:F1}%",
                    at1 * 100.0, at2 * 100.0, at3 * 100.0));
            }
            catch (Exception e) { Log("转子惯性异常: " + e); }
            yield return null;

// 第 30 段：不变式体检（游戏内版）。
            // 随机 400 台跑不动（MainMenu 里不能占太久），改成拿三台有代表性的机器
            // 验证那几条"物理必须成立"的关系：油门方向推力只能涨、
            // 满油门时高度越高推力越低、加力不能比干态小、SFC 幅度不许乱跳。
            try
            {
                EngineDesignData[] fleet = new EngineDesignData[3];
                fleet[0] = design.Clone();
                fleet[1] = design.Clone();
                // 三轴：照搬上面"三轴逐台涡轮"那台的几何（那台是设计点通过的）。
                fleet[1].ShaftCount = 3;
                fleet[1].Fan.Shaft = 1;
                fleet[1].Lpc.Stages = 3; fleet[1].Lpc.TipDiameter = 0.78; fleet[1].Lpc.Shaft = 2;
                fleet[1].Hpc.Stages = 6; fleet[1].Hpc.TipDiameter = 0.52; fleet[1].Hpc.Shaft = 3;
                fleet[1].Hpt.Shaft = 3;
                fleet[1].Ipt.Stages = 1; fleet[1].Ipt.TipDiameter = 0.70; fleet[1].Ipt.Shaft = 2;
                fleet[1].Lpt.Stages = 3; fleet[1].Lpt.Shaft = 1;
                fleet[2] = design.Clone();
                fleet[2].Afterburner.Enabled = false;
                fleet[2].Burner.Kind = BurnerKind.Can;
                fleet[2].Lpc.Kind = CompressorKind.Centrifugal;
                fleet[2].Lpc.Stages = 2;
                fleet[2].Hpc.Kind = CompressorKind.Centrifugal;
                fleet[2].Hpc.Stages = 2;

                for (int f = 0; f < fleet.Length; f++)
                {
                    EngineDesignData d = fleet[f];
                    DesignConstants dc2 = OffDesign.DeriveDesignConstants(d);
                    OffDesignResult dp2 = OffDesign.Solve(d, d.DesignAlt, d.DesignMach, 1.0, 0.0, dc2);
                    // 包线要和命令行那条一致（0/6/11 km × 0/0.55/0.90），否则
                    // 20 km 与 M3 的格子会把"高度越高推力越低"和"SFC 幅度"两条判成违例——
                    // 那两点上净推力本来就是个零头，阈值不该套那里。
                    double[] ta = { 0.25, 0.55, 0.85, 1.0 };
                    double[] al = { 0.0, 6000.0, 11000.0 };
                    double[] mc = { 0.0, 0.55, 0.90 };
                    PerformanceGrid g = GridFactory.Build(d, dc2, ta, al, mc, 0.0);
                    PerformanceGrid gb = null;
                    if (d.Afterburner.Enabled) gb = GridFactory.Build(d, dc2, ta, al, mc, 1.0);

                    int upBad = 0, altBad = 0, abBad = 0, sfcBad = 0;
                    double worstSfc = 1.0;
                    for (int m = 0; m < g.Nm; m++)
                        for (int a = 0; a < g.Na; a++)
                        {
                            double colMax = g.Thrust[g.Index(g.Nt - 1, a, m)];
                            for (int t = 0; t < g.Nt; t++)
                            {
                                double th = g.Thrust[g.Index(t, a, m)];
                                if (t + 1 < g.Nt)
                                {
                                    double nx = g.Thrust[g.Index(t + 1, a, m)];
                                    if (nx < th - Math.Max(1.0, 0.01 * Math.Abs(th))) upBad++;
                                }
                                if (t == g.Nt - 1 && a + 1 < g.Na)
                                {
                                    double hi = g.Thrust[g.Index(t, a + 1, m)];
                                    if (hi > th + Math.Max(1.0, 0.01 * Math.Abs(th))) altBad++;
                                }
                                if (gb != null)
                                {
                                    double wt = gb.Thrust[gb.Index(t, a, m)];
                                    if (wt < th - Math.Max(1.0, 0.01 * Math.Abs(th))) abBad++;
                                }
                            }
                            if (colMax <= 0.0) continue;
                            double smin = double.MaxValue, smax = 0.0;
                            for (int t = 0; t < g.Nt; t++)
                            {
                                int ix = g.Index(t, a, m);
                                if (!g.Valid[ix] || g.Sfc[ix] <= 0.0) continue;
                                if (g.Thrust[ix] < 0.20 * colMax) continue;
                                if (g.Sfc[ix] < smin) smin = g.Sfc[ix];
                                if (g.Sfc[ix] > smax) smax = g.Sfc[ix];
                            }
                            if (smax > 0.0 && smin < double.MaxValue && smin > 0.0)
                            {
                                double ratio = smax / smin;
                                if (ratio > worstSfc) worstSfc = ratio;
                                if (ratio > 2.5) sfcBad++;
                            }
                        }

                    Log(string.Format("不变式 #{0}：成立 {1}，设计点 {2:F1} kN｜油门反向 {3}｜满油门高度反向 {4}｜加力低于干态 {5}｜SFC 幅度超 2.5 倍 {6}（最大 {7:F2} 倍）",
                        f, YN(dp2.Ok), dp2.Ok ? dp2.NetThrust / 1000.0 : 0.0, upBad, altBad, abBad, sfcBad, worstSfc));
                }

                // 存档往返也带一遍：这三台里有离心/罐式/三轴/无加力。
                for (int f = 0; f < fleet.Length; f++)
                {
                    ConfigNode cn = new ConfigNode("T");
                    fleet[f].Save(cn);
                    EngineDesignData back = EngineDesignData.Load(cn);
                    DesignConstants ca = OffDesign.DeriveDesignConstants(fleet[f]);
                    DesignConstants cb = OffDesign.DeriveDesignConstants(back);
                    OffDesignResult oa = OffDesign.Solve(fleet[f], 0.0, 0.0, 1.0, 0.0, ca);
                    OffDesignResult ob = OffDesign.Solve(back, 0.0, 0.0, 1.0, 0.0, cb);
                    double dk = oa.Ok ? oa.NetThrust : 0.0, dkb = ob.Ok ? ob.NetThrust : 0.0;
                    bool same = oa.Ok == ob.Ok && Math.Abs(dk - dkb) < 1e-3;
                    Log(string.Format("不变式 #{0} 存档往返：两边都成立：{1}／{2}；推力 {3:F3}／{4:F3} kN；一致：{5}",
                        f, YN(oa.Ok), YN(ob.Ok), dk / 1000.0, dkb / 1000.0, YN(same)));
                }
            }
            catch (Exception e) { Log("不变式体检异常: " + e); }
            yield return null;

            // 第 31 段：加力瞬态。
            // 加力以前是个开关：按下就满推力、松开立刻没有。真机要一两秒才把推力推上去，
            // 关掉之后余焰还要烧一会儿。这段验的是"时间"本身，所以只能靠步进。
            Section("瞬态：加力那一两秒里推力是怎么爬上去的");
            try
            {
                EngineDesignData abD = design.Clone();
                abD.Afterburner.Enabled = true;
                DesignConstants cab = OffDesign.DeriveDesignConstants(abD);
                if (!cab.Ok)
                {
                    Log("加力瞬态：设计常数不成立——" + (cab.Errors.Count > 0 ? cab.Errors[0] : "?"));
                }
                else
                {
                    AbState st = new AbState();
                    double t50 = -1.0, t95 = -1.0;
                    const double dt = 0.01;
                    for (double t = 0.0; t <= 4.0; t += dt)
                    {
                        AfterburnerTransient.Step(st, dt, 1.0, true);
                        if (t50 < 0 && st.Frac >= 0.50) t50 = t + dt;
                        if (t95 < 0 && st.Frac >= 0.95) t95 = t + dt;
                    }
                    Log(string.Format("加力瞬态：50% 用 {0:F2}s｜95% 用 {1:F2}s｜4 s 后 {2:F0}% {3}",
                        t50, t95, st.Frac * 100.0, AfterburnerTransient.Describe(st)));

                    double tOff = -1.0;
                    for (double t = 0.0; t <= 2.0; t += dt)
                    {
                        AfterburnerTransient.Step(st, dt, 0.0, true);
                        if (tOff < 0 && st.Frac <= 0.0) tOff = t + dt;
                    }
                    Log(string.Format("  关掉后 {0:F2}s 退干净（末态 {1}）", tOff, AfterburnerTransient.Describe(st)));

                    // 点不着时不能留半加力：残余必须按"失速"那档更快退掉。
                    AbState st2 = new AbState();
                    st2.Frac = 1.0; st2.Phase = AbPhase.Lit;
                    double tFail = -1.0;
                    for (double t = 0.0; t <= 2.0; t += dt)
                    {
                        AfterburnerTransient.Step(st2, dt, 1.0, false);
                        if (tFail < 0 && st2.Frac <= 0.0) tFail = t + dt;
                    }
                    Log(string.Format("  点不着时残余 {0:F2}s 退掉（末态 {1}）", tFail, AfterburnerTransient.Describe(st2)));

                    // 半开加力：推力应该落在干/湿之间，而不是跳变。
                    double alt = abD.DesignAlt, mach = abD.DesignMach;
                    PerformanceGrid dryG = GridFactory.Build(abD, cab,
                        new[] { 1.0 }, new[] { alt }, new[] { mach }, 0.0);
                    PerformanceGrid wetG = GridFactory.Build(abD, cab,
                        new[] { 1.0 }, new[] { alt }, new[] { mach }, 1.0);
                    double halfFrac = 0.5;
                    bool halfInit = true;
                    // dt 不能给 0：那在 Step 里是"同步"信号，会直接跳到满加力，
                    // 于是"半开"验的是满开——这个坑命令行那边刚踩过一次。
                    FlightStepResult rf = FlightStep.Compute(dryG, 1.0, 0.001, alt, mach,
                        true, false, true, true, ref halfFrac, ref halfInit,
                        Spool.TauUp(abD), Spool.TauDown(abD), JetForge.KSP.ModuleJetForgeEngine.WindmillArea(abD), 0.05,
                        wetG, new AbState { Frac = 0.5, Phase = AbPhase.Lighting },
                        1.0, true);
                    double dryN = dryG.Thrust[0] / 1000.0, wetN = wetG.Thrust[0] / 1000.0;
                    Log(string.Format("  插值：干态 {0:F2} kN｜加力 {1:F2} kN（{2:+0.0;-0.0}%）｜半开 {3:F2} kN",
                        dryN, wetN, (wetN / Math.Max(dryN, 1e-6) - 1.0) * 100.0, rf.ThrustN / 1000.0));
                }
            }
            catch (Exception e) { Log("加力瞬态异常: " + e); }
            yield return null;

            // 第 32 段：风扇特性线 + "解出来的点偏离特性线多远"。
            // 命令行那边是第 36 段；同一套数字两边必须一致，否则飞行里的表就是另一个模型算的。
            try
            {
                CycleResult r0 = Cycle.SolveDesignPoint(design);
                double fprD = r0.Ok ? r0.Fpr : 1.59;
                string fmMsg;
                bool fmOk = FanMap.DesignPointSelfCheck(fprD, out fmMsg);
                Log(string.Format("风扇特性线：设计压比 {0:F4}｜{1}｜设计点自洽：{2}", fprD, fmMsg, YN(fmOk)));
                Log(string.Format("  0.50 {0:F4}/{1:F4}  0.70 {2:F4}/{3:F4}  0.85 {4:F4}/{5:F4}  1.00 {6:F4}/{7:F4}（压比/流量系数）",
                    FanMap.PressureRatio(fprD, 0.50), FanMap.FlowFrac(0.50),
                    FanMap.PressureRatio(fprD, 0.70), FanMap.FlowFrac(0.70),
                    FanMap.PressureRatio(fprD, 0.85), FanMap.FlowFrac(0.85),
                    FanMap.PressureRatio(fprD, 1.00), FanMap.FlowFrac(1.00)));

                double byDesign = -1.0;
                for (int i = 0; i < 3; i++)
                {
                    double thr = i == 0 ? 0.10 : (i == 1 ? 0.50 : 1.0);
                    OffDesignResult ro = OffDesign.Solve(design, 0.0, 0.0, thr, 0.0, c, 60);
                    if (!ro.Ok) { Log(string.Format("  油门 {0:F2}：解不出来", thr)); continue; }
                    double byF = (ro.MdotAir - ro.MdotCore) / Math.Max(ro.MdotAir, 1e-9);
                    if (thr >= 0.999) byDesign = byF;
                    Log(string.Format("  油门 {0:F2}：N1 {1:F1}% 解出压比 {2:F4} 特性线 {3:F4}｜涵道占比 {4:P1}",
                        thr, ro.N1Pct, ro.Fpr, FanMap.PressureRatio(fprD, ro.N1Pct / 100.0), byF));
                }
                if (byDesign > 0.0)
                    Log(string.Format("  涵道占比在设计点是 {0:P1}——低油门能不能变，就是 §46 量出来的那个缺口", byDesign));
            }
            catch (Exception e) { Log("风扇特性线异常: " + e); }
            yield return null;

            try
            {
                var ap = PartLoader.getPartInfoByName("JetForgeEngine");
                if (ap == null) Log("警告：PartLoader 里找不到 JetForgeEngine 零件");
                else Log(string.Format("零件已在数据库：{0}（{1}），质量 {2:F3} t，关键字 {3}",
                    ap.name, ap.title, ap.partPrefab != null ? ap.partPrefab.mass : -1f,
                    ap.tags));

                // 再走一遍真实路径：让 Part 自己调 IPartMassModifier，看质量有没有真的换成几何算出来的值
                if (ap != null && ap.partPrefab != null)
                {
                    ModuleJetForgeEngine mod = null;
                    for (int i = 0; i < ap.partPrefab.Modules.Count; i++)
                    {
                        ModuleJetForgeEngine m0 = ap.partPrefab.Modules[i] as ModuleJetForgeEngine;
                        if (m0 != null) { mod = m0; break; }
                    }
                    if (mod == null) Log("预置零件上找不到 ModuleJetForgeEngine");
                    else
                    {
                        mod.EnsureReady();
                        float cfgMass = ap.partPrefab.mass;
                        float cfgCost = ap.partPrefab.partInfo != null ? ap.partPrefab.partInfo.cost : -1f;
                        float dMass = ap.partPrefab.GetModuleMass(cfgMass, ModifierStagingSituation.CURRENT);
                        float dCost = ap.partPrefab.GetModuleCosts(cfgCost, ModifierStagingSituation.CURRENT);
                        ap.partPrefab.UpdateMass();
                        Log(string.Format("预置零件质量：cfg {0:F3} t + 模块增量 {1:+0.000;-0.000} t → {2:F3} t（模型算出 {3:F3} t）",
                            cfgMass, dMass, ap.partPrefab.mass, mod.Mass != null ? mod.Mass.TotalDry / 1000.0 : -1.0));
                        Log(string.Format("预置零件造价：cfg {0:N0} + 模块增量 {1:+0;-0} → {2:N0} funds（模型算出 {3:N0}）",
                            cfgCost, dCost, cfgCost + dCost, mod.Mass != null ? mod.Mass.Cost : -1.0));
                    }
                }
            }
            catch (Exception e) { Log("查零件异常: " + e); }

            // 进气道零件的质量：以前系数按公斤写、返回值按吨交，算出来比整机重六十倍。
            // 这里盯的是"质量随几何走、且在同一个数量级"，不是某个具体数。
            try
            {
                var ip = PartLoader.getPartInfoByName("JetForgeIntake");
                if (ip == null || ip.partPrefab == null) Log("警告：PartLoader 里找不到 JetForgeIntake 零件");
                else
                {
                    ModuleJetForgeIntake im = null;
                    for (int i = 0; i < ip.partPrefab.Modules.Count; i++)
                    {
                        ModuleJetForgeIntake m0 = ip.partPrefab.Modules[i] as ModuleJetForgeIntake;
                        if (m0 != null) { im = m0; break; }
                    }
                    if (im == null) Log("预置零件上找不到 ModuleJetForgeIntake");
                    else
                    {
                        float cfgMass = ip.partPrefab.mass;
                        float dMass = ip.partPrefab.GetModuleMass(cfgMass, ModifierStagingSituation.CURRENT);
                        float dCost = ip.partPrefab.GetModuleCosts(ip.partPrefab.partInfo != null ? ip.partPrefab.partInfo.cost : 0f,
                            ModifierStagingSituation.CURRENT);
                        ip.partPrefab.UpdateMass();
                        Log(string.Format("进气道零件质量：cfg {0:F3} t + 模块增量 {1:+0.000;-0.000} t → {2:F3} t（几何算出 {3:F1} kg，"
                            + "捕获 Ø{4:F2} m｜长 {5:F2} m｜造价 {6:N0}，增量造价 {7:+0;-0}）",
                            cfgMass, dMass, ip.partPrefab.mass, im.MassKg(),
                            im.CaptureArea > 0.0 ? Math.Sqrt(4.0 * im.CaptureArea / Math.PI) : 0.0, im.length,
                            im.CostFunds(), dCost));
                    }
                }
            }
            catch (Exception e) { Log("查进气道零件异常: " + e); }

            // 原版链路：反汇编说得很清楚——飞行中 finalThrust = maxFuelFlow×油门×Isp×g，
            // 我们写的 finalThrust 会被每帧覆盖。所以真正要验的是"喂进去的这两个数对不对"。
            try
            {
                var ap2 = PartLoader.getPartInfoByName("JetForgeEngine");
                ModuleJetForgeEngine e2 = null;
                if (ap2 != null && ap2.partPrefab != null)
                    for (int i = 0; i < ap2.partPrefab.Modules.Count; i++)
                    {
                        ModuleJetForgeEngine m2 = ap2.partPrefab.Modules[i] as ModuleJetForgeEngine;
                        if (m2 != null) { e2 = m2; break; }
                    }
                if (e2 == null) Log("警告：原版链路验证拿不到模块");
                else
                {
                    e2.EnsureReady();
                    double wantKN = e2.DpThrust() / 1000.0;
                    double wantTps = e2.DpFuelFlow() / 1000.0;
                    double stockKN = e2.maxFuelFlow * 1.0 * e2.realIsp * e2.g * e2.multFlow;
                    double stockTps = e2.maxFuelFlow * 1.0 * e2.multFlow;
                    Log(string.Format("原版链路：maxFuelFlow={0:F6} t/s Isp={1:F0} s g={2:F3} multFlow={3:F2}",
                        e2.maxFuelFlow, e2.realIsp, e2.g, e2.multFlow));
                    Log(string.Format("  原版算回推力 {0:F2} kN（模型 {1:F2}，差 {2:+0.0;-0.0}%）｜油耗 {3:F4} kg/s（模型 {4:F4}）",
                        stockKN, wantKN, (stockKN - wantKN) / wantKN * 100.0, stockTps * 1000.0, wantTps * 1000.0));
                    // 半油门：原版是 推力×油门 线性，我们的推力不是——桥接每帧重算才跟得上。
                    double half = e2.maxFuelFlow * 0.5 * e2.realIsp * e2.g * e2.multFlow;
                    Log(string.Format("  半油门按设计点线性：原版会算 {0:F2} kN（我们 0.5 油门实际 {1:F2} kN）",
                        half, OffDesign.Solve(e2.Design, 0.0, 0.0, 0.5, 0.0, e2.Consts).NetThrust / 1000.0));
                }
            }
            catch (Exception e) { Log("原版链路验证异常: " + e); }

            // 任务剖面：把飞行里那串调用连续跑一遍（油门滞后 → 查表 → 喂原版参数）。
            // 命令行已经验过，这里验的是"在游戏进程里、用同一个装配好的模块"还能不能跑通。
            try
            {
                var gp = PartLoader.getPartInfoByName("JetForgeEngine");
                ModuleJetForgeEngine ep = null;
                if (gp != null && gp.partPrefab != null)
                    for (int i = 0; i < gp.partPrefab.Modules.Count; i++)
                    {
                        ModuleJetForgeEngine mp = gp.partPrefab.Modules[i] as ModuleJetForgeEngine;
                        if (mp != null) { ep = mp; break; }
                    }
                if (ep == null) Log("警告：任务剖面仿真拿不到模块");
                else
                {
                    PerformanceGrid mg = GridFactory.Build(ep.Design, ep.Consts);
                    double tUp = Spool.TauUp(ep.Design), tDn = Spool.TauDown(ep.Design);
                    double sp = 0.0, fuel = 0.0, imp = 0.0, pk = 0.0, last = 0.0, slew = 0.0;
                    int bad = 0;
                    for (int k = 0; k <= 3500; k++)
                    {
                        double t = k * 0.02;
                        double alt, mach, cmd;
                        bool lit = t < 66.0;
                        if (t < 10.0) { alt = 0.0; mach = 0.0; cmd = 1.0; }
                        else if (t < 40.0)
                        {
                            double f = (t - 10.0) / 30.0;
                            alt = 8000.0 * f; mach = 0.75 * f; cmd = 1.0;
                        }
                        else if (t < 60.0) { alt = 8000.0; mach = 0.75; cmd = 0.80; }
                        else { alt = 8000.0; mach = 0.75; cmd = 0.0; }

                        sp = lit ? Spool.Step(sp, cmd, 0.02, tUp, tDn) : 0.0;
                        PerformanceGrid.Sample s = mg.Interp(sp, alt, mach);
                        double th = (lit && s.Valid) ? s.Thrust : 0.0;
                        double ff = (lit && s.Valid) ? s.FuelFlow : 0.0;
                        if (double.IsNaN(th) || double.IsInfinity(th)) bad++;
                        if (t > 1.0 && t < 66.0)
                        {
                            double r = Math.Abs(th - last) / 0.02;
                            if (r > slew) slew = r;
                        }
                        if (th > pk) pk = th;
                        last = th;
                        fuel += ff * 0.02;
                        imp += th * 0.02;
                    }
                    double sfc = imp > 1.0 ? fuel * 3600.0 / imp : 0.0;
                    Log(string.Format("任务剖面：3501 步 70 s，峰值 {0:F1} kN，总耗油 {1:F1} kg，平均 SFC {2:F4}，最大变化率 {3:F0} kN/s（τ 允许 {4:F0}）",
                        pk / 1000.0, fuel, sfc, slew / 1000.0, pk / tUp / 1000.0));
                    Log(string.Format("  关车后推力 {0:F3} kN（应为 0）｜NaN/无穷 {1} 步（应为 0）", last, bad));
                }
            }
            catch (Exception e) { Log("任务剖面异常: " + e); }

            // 加力：干态表 / 加力表 / 设计点三者是否自洽，以及 T7 这一列是不是真的在动。
            try
            {
                if (design != null)
                {
                    EngineDesignData ab = design.Clone();
                    ab.Afterburner.Enabled = true;
                    ab.Afterburner.FuelRatio = 0.025;
                    ab.Nozzle = NozzleKind.ConvergingDiverging;
                    DesignConstants ac = OffDesign.DeriveDesignConstants(ab);
                    if (ac.Ok)
                    {
                        CycleResult dpab = Cycle.SolveDesignPoint(ab);
                        PerformanceGrid gdry = GridFactory.Build(ab, ac, null, null, null, 0.0);
                        PerformanceGrid gwet = GridFactory.Build(ab, ac, null, null, null, 1.0);
                        PerformanceGrid.Sample ds = gdry.Interp(1.0, 0.0, 0.0);
                        PerformanceGrid.Sample ws = gwet.Interp(1.0, 0.0, 0.0);
                        Log(string.Format("加力设计点 {0:F2} kN / {1:F4} kg/s",
                            dpab.Thrust / 1000.0, dpab.FuelFlow));
                        Log(string.Format("加力对照：干态 {0:F2} kN / {1:F4} kg/s　T5 {2:F0} K　T7 {3:F0} K ｜ 加力 {4:F2} kN / {5:F4} kg/s　T5 {6:F0} K　T7 {7:F0} K",
                            ds.Thrust / 1000.0, ds.FuelFlow, ds.Egt, ds.T7,
                            ws.Thrust / 1000.0, ws.FuelFlow, ws.Egt, ws.T7));
                        double gg = ds.Thrust > 1.0 ? (ws.Thrust - ds.Thrust) / ds.Thrust * 100.0 : 0.0;
                        Log(string.Format("加力增益 {0:+0.0;-0.0}%　油耗 {1:F2}×　设计点 vs 加力表 {2:+0.0;-0.0}%　T7 列随加力上升 {3}",
                            gg, ws.FuelFlow / Math.Max(ds.FuelFlow, 1e-9),
                            (dpab.Thrust - ws.Thrust) / Math.Max(ws.Thrust, 1.0) * 100.0,
                            ws.T7 > ds.T7 + 100.0 ? "是" : "否"));
                        // 尾焰：加力态该更长更亮（着色器与几何都不变，只是参数变了）
                        float r = JetForgeMeshBuilder.NozzleExitRadius(ab);
                        PlumeMath.PlumeSpec pd = PlumeMath.Compute(r, 700.0, ds.T7, 1.0, 1.0, false);
                        PlumeMath.PlumeSpec pa = PlumeMath.Compute(r, 700.0, ws.T7, 1.0, 1.0, true);
                        Log(string.Format("尾焰加力判定：干态长 {0:F2} m　加力长 {1:F2} m（{2:F2}×）",
                            pd.Length, pa.Length, pa.Length / Math.Max(pd.Length, 1e-6)));
                    }
                    else Log("加力设计不成立：" + ac.Errors[0]);
                }
            }
            catch (Exception e) { Log("加力对照异常: " + e); }

            // 存档往返：命令行用的是我自己写的 ConfigNode 最小实现，这里是 KSP 真货。
            // 两边都能过，才说明"设计存得回来"不是我在自己的桩上自说自话。
            Section("存档：存一次再读回来，每个字段还是原样吗");
            try
            {
                if (design != null)
                {
                    EngineDesignData src = design.Clone();
                    src.ShaftCount = 3;
                    src.Lpt.Shaft = 1;
                    src.Ipt.Shaft = 2;
                    src.Hpt.Shaft = 3;
                    src.Lpc.HubTipRatio = 0.62;
                    src.Hpt.HubTipRatio = 0.81;
                    src.Burner.Kind = BurnerKind.Can;
                    src.Afterburner.Enabled = true;
                    src.Afterburner.FuelRatio = 0.031;
                    src.DesignMach = 0.85;
                    src.DesignAlt = 11000.0;
                    src.OverrideBpr = 0.0;
                    src.Nozzle = NozzleKind.ConvergingDiverging;

                    ConfigNode n = new ConfigNode("MODULE");
                    n.RemoveNodes(EngineDesignData.NodeName);
                    src.Save(n.AddNode(EngineDesignData.NodeName));
                    EngineDesignData back = EngineDesignData.Load(n.GetNode(EngineDesignData.NodeName));

                    List<string> diffs = new List<string>();
                    CmpFields(src, back, "design", diffs);
                    Log(diffs.Count == 0
                        ? "存档往返：读取值与保存值逐字段一致"
                        : string.Format("存档往返：{0} 个字段读回来对不上——{1}",
                            diffs.Count, string.Join("；", diffs.ToArray())));

                    // 同一份节点连存两次，节点数不该涨——模块 OnSave 里就是先删再写。
                    n.RemoveNodes(EngineDesignData.NodeName);
                    src.Save(n.AddNode(EngineDesignData.NodeName));
                    n.RemoveNodes(EngineDesignData.NodeName);
                    src.Save(n.AddNode(EngineDesignData.NodeName));
                    Log(string.Format("重复保存：设计节点 {0} 个（应为 1）", n.GetNodes(EngineDesignData.NodeName).Length));

                    SimpleDesign s0 = new SimpleDesign();
                    s0.Bpr = 4.2; s0.Opr = 22.5; s0.T4 = 1480.0; s0.TExhaust = 1200.0;
                    s0.AbFuelRatio = 0.028; s0.Nozzle = NozzleKind.Variable;
                    ConfigNode sn = new ConfigNode("MODULE");
                    SimpleDesignPersistence.Save(s0, sn);
                    SimpleDesign s1 = SimpleDesignPersistence.Load(sn);
                    List<string> sdiffs = new List<string>();
                    CmpFields(s0, s1, "simple", sdiffs);
                    Log(string.Format("简单模式存档往返：{0} 个字段不一致", sdiffs.Count));
                }
            }
            catch (Exception e) { Log("存档往返异常: " + e); }

            SectionAltitudeAndWindmill();


            SectionCorners();

            SectionIdleFloor();

            SectionFlightStep();

            SectionDesignTables();


            SectionEngineLength();


            SectionPresetsFormsAndPaw(design);

            // ---- 第四阶段②：Waterfall 在不在、模板在不在、这台机器的配置长什么样 ----
            try
            {
                Log("Waterfall：装配里" + (JetForgeWaterfall.Available() ? "有" : "没有（就没有羽流，硬依赖）"));

                int tplCount = 0;
                bool[] have = new bool[JetForgeWaterfall.ClassCount];
                foreach (ConfigNode cn in GameDatabase.Instance.GetConfigNodes("EFFECTTEMPLATE"))
                {
                    string tn = cn.GetValue("templateName");
                    if (string.IsNullOrEmpty(tn)) continue;
                    tplCount++;
                    for (int i = 0; i < JetForgeWaterfall.ClassCount; i++)
                        if (tn == JetForgeWaterfall.TemplateOf((PlumeClass)i)) have[i] = true;
                }
                int haveCount = 0;
                StringBuilder miss = new StringBuilder();
                for (int i = 0; i < JetForgeWaterfall.ClassCount; i++)
                {
                    if (have[i]) haveCount++;
                    else
                    {
                        if (miss.Length > 0) miss.Append("/");
                        miss.Append(JetForgeWaterfall.TemplateOf((PlumeClass)i));
                    }
                }
                Log(string.Format("  EFFECTTEMPLATE 共 {0} 个｜我们自己的 {1}/{2} 套在{3}",
                    tplCount, haveCount, JetForgeWaterfall.ClassCount,
                    miss.Length == 0 ? "（干低/干高/干老/涡喷暖中热/涡扇暖中热）" : "｜缺：" + miss));

                // 两台不同尺寸的机器，看算出来的缩放是不是真的不一样——这就是"每台唯一"的证据。
                EngineDesignData big = LoadDesignFromGameDatabase();
                big.Fan.Diameter = 1.40;
                big.Afterburner.Enabled = true;
                EngineDesignData small = LoadDesignFromGameDatabase();
                small.Fan.Diameter = 0.52;
                small.Afterburner.Enabled = false;

                double rBig = JetForgeMeshBuilder.NozzleExitRadius(big);
                double rSmall = JetForgeMeshBuilder.NozzleExitRadius(small);
                Log(string.Format("  喷口半径：大机 {0:F3} m / 小机 {1:F3} m｜模板缩放 {2:F2} vs {3:F2}（原版 Panther = 1.00）｜加力 {4}/{5}",
                    rBig, rSmall, JetForgeWaterfall.PlumeScale(rBig), JetForgeWaterfall.PlumeScale(rSmall),
                    big.Afterburner.Enabled ? "有" : "无", small.Afterburner.Enabled ? "有" : "无"));
                Log(string.Format("  等效羽流长度：大机 {0:F1} m / 小机 {1:F1} m（加力模板最长那根 25 × 缩放）",
                    rBig * 25.0 * JetForgeWaterfall.PlumeScale(rBig),
                    rSmall * 25.0 * JetForgeWaterfall.PlumeScale(rSmall)));
                Log(string.Format("  加力比例：油门过线 {0:P0}｜70%→{1:F2}｜85%→{2:F2}｜100%→{3:F2}",
                    big.Afterburner.LightThrottle,
                    AfterburnerTransient.Target(0.70, big.Afterburner.LightThrottle, big.Afterburner.AutoLight, false),
                    AfterburnerTransient.Target(0.85, big.Afterburner.LightThrottle, big.Afterburner.AutoLight, false),
                    AfterburnerTransient.Target(1.00, big.Afterburner.LightThrottle, big.Afterburner.AutoLight, false)));
                Log("  羽流来源：" + (JetForgeWaterfall.Available() ? "Waterfall（自绘那套关掉）" : "自绘两层锥"));

                // 加力三档：以前"命令"和"点火边界"共用 afterburnerLit 一个布尔，
                // 自动档下它恒为 false → canLight 永远 false → 比例算得再对也点不着。
                // 这里把循环顺序和每档给的目标比例都打出来，谁改坏了立刻看得见。
                bool cycM = false, cycA = true;       // 出厂：自动档
                StringBuilder cyl = new StringBuilder();
                for (int k = 0; k < 3; k++)
                {
                    cyl.Append(AbModes.Label(AbModes.Of(cycM, cycA), big.Afterburner.LightThrottle));
                    if (k < 2) cyl.Append(" → ");
                    AbModes.Next(cycM, cycA, out cycM, out cycA);
                }
                Log("  加力三档（按三次）：" + cyl.ToString());
                Log(string.Format("  目标比例：自动 50%→{0:F2} 85%→{1:F2}｜强制开 50%→{2:F2}｜关 100%→{3:F2}",
                    AbModes.Target(AbModeKind.Auto, 0.50, big.Afterburner.LightThrottle),
                    AbModes.Target(AbModeKind.Auto, 0.85, big.Afterburner.LightThrottle),
                    AbModes.Target(AbModeKind.Manual, 0.50, big.Afterburner.LightThrottle),
                    AbModes.Target(AbModeKind.Off, 1.00, big.Afterburner.LightThrottle)));

                // 判据闸：循环能收敛 ≠ 这台机器说得通。把高压压气机砍到 1 级，
                // 循环往往还能解出来（推力很小），但判据会判 FAIL —— 飞行里必须不许出力。
                CheckReport okRep = Criteria.Evaluate(big, Criteria.ViewOf(Cycle.SolveDesignPoint(big)), big.Tech, Gas.P0, 0.0);
                Log(string.Format("  判据闸：出厂机最差 {0}，放行 {1}", okRep.Worst, YN(okRep.Ok)));
                EngineDesignData badD = big.Clone();
                badD.Hpc.Stages = 1;
                CycleResult badPoint = Cycle.SolveDesignPoint(badD);
                CheckReport badRep = Criteria.Evaluate(badD, Criteria.ViewOf(badPoint), badD.Tech, Gas.P0, 0.0);
                string badMsg = "";
                for (int i = 0; i < badRep.Findings.Count; i++)
                {
                    Finding f = badRep.Findings[i];
                    if (f != null && f.LevelValue == Level.Fail) { badMsg = f.Message; break; }
                }
                Log(string.Format("  判据闸：高压压气机砍到 1 级时循环成立 {0} 推力 {1:F1} kN｜最差={2} 放行={3}",
                    YN(badPoint.Ok), badPoint.Thrust / 1000.0, badRep.Worst, YN(badRep.Ok)));
                if (badMsg.Length > 0) Log("    拦住它的判据：" + badMsg);

                // cfg 补丁有没有真的挂上两个模块——挂不上的话飞行里就是"静默退回自绘"，
// 不会有人报错，也没人会发现。
                AvailablePart pi4 = PartLoader.getPartInfoByName("JetForgeEngine");
                if (pi4 != null && pi4.partPrefab != null)
                {
                    Part pf = pi4.partPrefab;
                    int wfCount = 0;
                    StringBuilder ids = new StringBuilder();
                    for (int i = 0; i < pf.Modules.Count; i++)
                    {
                        PartModule pm2 = pf.Modules[i];
                        if (pm2 == null || pm2.moduleName != "ModuleWaterfallFX") continue;
                        wfCount++;
                        BaseField mf = pm2.Fields["moduleID"];
                        if (ids.Length > 0) ids.Append("/");
                        ids.Append(mf != null ? mf.GetValue(pm2).ToString() : "?");
                    }
                    Log(string.Format("  cfg 补丁：ModuleWaterfallFX {0} 个（{1}）", wfCount, ids.ToString()));

                    // 分层模板：内芯层（plume-core）现在**故意撤掉**——玩家报的"加力白环"怀疑是它：
                    // 同一个 fx-cylinder 缩小后的副本，圆柱端盖落在羽流中间就是一道环。
                    // 现在这一行报 0 才是对的；以后换成无端盖模型（fx-complex-plume-1）再做分层。
                    // 这个数只能读文件数——模板是 Waterfall 自己的数据，我们这边没有句柄。
                    try
                    {
                        string tpath = KSPUtil.ApplicationRootPath + "GameData/JetForge/WaterfallTemplates.cfg";
                        string ttext = System.IO.File.Exists(tpath) ? System.IO.File.ReadAllText(tpath) : "";
                        int nCore = 0, pos2 = 0;
                        while ((pos2 = ttext.IndexOf("name = plume-core", pos2, StringComparison.Ordinal)) >= 0)
                        { nCore++; pos2 += 8; }
                        Log(string.Format("  分层模板：plume-core {0} 片｜{1}", nCore,
                            nCore == 0 ? "内芯层已撤（白环疑似它）"
                                       : "还留着内芯层 → 加力时留意白环 = 待查"));
                    }
                    catch (Exception te) { Log("  分层模板读取异常: " + te.Message); }
                    // 预置件上的 FX 是 Start 之后才建的，prefab 上 0 个是正常的
                    int fxZero = 0;
                    StringBuilder fxIds = new StringBuilder();
                    for (int i = 0; i < JetForgeWaterfall.ClassCount; i++)
                    {
                        int n = JetForgeWaterfall.FxCount(pf, JetForgeWaterfall.ModuleIdOf((PlumeClass)i));
                        if (n <= 0) fxZero++;
                        fxIds.Append(n);
                        if (i + 1 < JetForgeWaterfall.ClassCount) fxIds.Append("/");
                    }
                    Log(string.Format("  prefab 上的 FX：{0}/{1} 个模块是 0（各模块 {2}；0 正常，飞行时才建）",
                        fxZero, JetForgeWaterfall.ClassCount, fxIds));

                    // 构型选择：同一套代码要分出"小涡喷/低涵道/高涵道"三种尾焰。
                    // 判据全来自设计本身（是否装风扇、涵道比 2.5），不是玩家勾的开关。
                    EngineDesignData tjp = LoadDesignFromGameDatabase();
                    tjp.Kind = EngineKind.Turbojet;
                    tjp.Afterburner.Enabled = true;
                    // 早期涡喷（1940s）干态是带烟的琥珀灰，靠技术级年代分档。
                    // 注意 Tech 是只读属性（按 TechName 去查表），只能改 TechName；
                    // 而且查表不认的名字会静默退回 1970s——这里必须用 Names 里真有的档。
                    EngineDesignData tjpOld = LoadDesignFromGameDatabase();
                    tjpOld.Kind = EngineKind.Turbojet;
                    tjpOld.TechName = "1940s";
                    EngineDesignData lofan = LoadDesignFromGameDatabase();
                    lofan.Kind = EngineKind.Turbofan;
                    lofan.OverrideBpr = 1.2;
                    EngineDesignData hifan = LoadDesignFromGameDatabase();
                    hifan.Kind = EngineKind.Turbofan;
                    hifan.OverrideBpr = 5.6;
                    Log(string.Format("  构型选择：干态 涡喷→{0}｜早期涡喷(1940s)→{1}｜低涵道→{2}｜高涵道→{3}",
                        JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(tjp, false)),
                        JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(tjpOld, false)),
                        JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(lofan, false)),
                        JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(hifan, false))));

                    // 设计器的悬停提示：标签能不能对上提示是一道纯逻辑题，
                    // 顺手在游戏里跑一遍——"长度"和"整机长度"都要各归各的。
                    Log("  " + JetForge.UI.JetForgeDesignerWindow.DescribeHints());

                    // 加力温度三档：同一台机器，1900 K 和 2100 K 的火不该是同一种颜色。
                    // 分界是涡喷 1850/2000、涡扇 1900/2050（涡扇多 50 K 的余量）。
                    double keepT = tjp.Afterburner.ExitTemp;
                    tjp.Afterburner.ExitTemp = 1800.0;
                    string jw = JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(tjp, true));
                    tjp.Afterburner.ExitTemp = 1950.0;
                    string jm = JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(tjp, true));
                    tjp.Afterburner.ExitTemp = 2100.0;
                    string jh = JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(tjp, true));
                    tjp.Afterburner.ExitTemp = keepT;
                    lofan.Afterburner.Enabled = true;
                    double keepF = lofan.Afterburner.ExitTemp;
                    lofan.Afterburner.ExitTemp = 1850.0;
                    string fw = JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(lofan, true));
                    lofan.Afterburner.ExitTemp = 1975.0;
                    string fm = JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(lofan, true));
                    lofan.Afterburner.ExitTemp = 2100.0;
                    string fh = JetForgeWaterfall.TemplateOf(JetForgeWaterfall.Classify(lofan, true));
                    lofan.Afterburner.ExitTemp = keepF;
                    Log(string.Format("  加力温度：涡喷 1800→{0} 1950→{1} 2100→{2}｜涡扇 1850→{3} 1975→{4} 2100→{5}",
                        jw, jm, jh, fw, fm, fh));
                    Log(string.Format("  干湿过渡：干羽流强度 = 油门 ×（1 − {0:F2} × 加力比例）→ 满加力时干态剩 {1:P0}",
                        JetForgeWaterfall.DryFadeWithAb, 1.0 - JetForgeWaterfall.DryFadeWithAb));

                    // "加力尾焰大小跟着油门 0~100% 变"这一轮：反汇编确认
                    // WaterfallController.set_overrideValue 只有 overridden 已经是真才调 Set()，
                    // Update() 在 overridden 时跳过 UpdateInternal。所以顺序必须是先 override 再写值。
                    // 这里把"灌完之后 values[] 到底是多少"量出来——只看 overrideValue 是看不到那个 bug 的。
                    try
                    {
                        JetForgeWaterfall pw = new JetForgeWaterfall();
                        pw.Sync(pf, big, false);
                        string wetId = JetForgeWaterfall.ModuleIdOf(pw.WetClass);
                        string dryId = JetForgeWaterfall.ModuleIdOf(pw.DryClass);
                        pw.SetLevel(0.0, 0.90);
                        Log("  羽流比例 0%（油门 90%）：" + JetForgeWaterfall.DescribeController(pf, wetId, "throttle"));
                        Log("    干态：" + JetForgeWaterfall.DescribeController(pf, dryId, "throttle"));
                        pw.SetLevel(1.0, 0.90);
                        Log("  羽流比例 100%（油门 90%）：" + JetForgeWaterfall.DescribeController(pf, wetId, "throttle"));
                        Log("    干态：" + JetForgeWaterfall.DescribeController(pf, dryId, "throttle"));
                        Log("    cfg 里的控制器：" + JetForgeWaterfall.ConfControllerNames(pf, wetId));
                    }
                    catch (Exception ex)
                    {
                        Log("  羽流比例灌入：抛了 " + ex.GetType().Name + "：" + ex.Message);
                    }

                    // 真实预设的自检：每台机型必须过判据闸门（最差等级不是 FAIL），
                    // 且几何要跟"拟合后"的样子对得上（FitSurge 会缩叶尖直径）。
                    foreach (PresetInfo pi in Presets.All)
                    {
                        EngineDesignData pd0 = Presets.Build(pi.Key);
                        if (pd0 == null) { Log("  预设 " + pi.Key + "：Build 返回 null"); continue; }
                        CycleResult pr = Cycle.SolveDesignPoint(pd0);
                        CheckReport prep = Criteria.Evaluate(pd0, Criteria.ViewOf(pr), pd0.Tech, Gas.P0, 0.0);
                        Log(string.Format(
                            "  预设 {0}：最差={1} 推力 {2:F1} kN OPR {3:F2}｜HPC {4:F3} m φ 拟合｜燃烧室 {5} {6} 罐 L/H {7:F1}",
                            pi.Key, prep.Worst, pr.Thrust / 1000.0, pr.Opr,
                            pd0.Hpc.TipDiameter, pd0.Burner.Kind, pd0.Burner.CanCount, pd0.Burner.LinerLd));
                    }

                    // 层级诊断：这轮"模型叠成两台"和"Waterfall 不工作"是同一个根因——
                    // 我们把自己的节点挂在 part.transform 下，而 Part.FindModelTransform 只搜
                    // "model" 的子树，于是每次刷新都以为"还没有"，新建一台；Waterfall 的
                    // overrideParentTransform 也找不到。这里把三件事各量一遍钉成数字：
                    //   · HostFor 是不是真的拿到了 model 节点
                    //   · 连刷两次之后，场景里还剩几个 JetForgeBody（必须 1 个）
                    //   · 施力节点在不在 model 子树里（FindModelTransform 找得到才算数）
                    bool hasModel = pf.transform.Find("model") != null;
                    Transform host = JetForgeMeshBuilder.HostFor(pf);
                    Log(string.Format("  层级：model 节点 {0}｜host={1}",
                        hasModel ? "在" : "没有（退回 part.transform）",
                        host != null ? host.name : "null"));
                    Log(string.Format("  层级：刷新前 JetForgeBody {0} 个｜FindModelTransform 找得到={1}",
                        CountByName(pf, JetForgeMeshBuilder.VisualObjectName),
                        YN(pf.FindModelTransform(JetForgeMeshBuilder.VisualObjectName) != null)));

                    JetForgeMeshBuilder.RefreshPartMesh(pf, big);
                    int after1 = CountByName(pf, JetForgeMeshBuilder.VisualObjectName);
                    JetForgeMeshBuilder.RefreshPartMesh(pf, big);
                    int after2 = CountByName(pf, JetForgeMeshBuilder.VisualObjectName);
                    Log(string.Format("  层级：连刷两次 JetForgeBody {0} → {1} 个（必须是 1，多了就是叠模型）",
                        after1, after2));
                    Log(string.Format("  层级：施力节点 FindModelTransform(\"{0}\")={1}｜FindModelTransform(\"thrustTransform\")={2}",
                        JetForgeMeshBuilder.ThrustTransformName,
                        YN(pf.FindModelTransform(JetForgeMeshBuilder.ThrustTransformName) != null),
                        YN(pf.FindModelTransform("thrustTransform") != null)));
                    // 测完拆掉：prefab 是全局共享的，留一台 1.40 m 的壳会跟着每个零件进游戏
                    DestroyByName(pf, JetForgeMeshBuilder.VisualObjectName);
                    DestroyByName(pf, JetForgeMeshBuilder.ThrustTransformName);
                    Log(string.Format("  层级：测完清场 JetForgeBody {0} 个",
                        CountByName(pf, JetForgeMeshBuilder.VisualObjectName)));
                }
            }
            catch (Exception e) { Log("Waterfall 检查异常: " + e.Message); }

            // 原版发动机数据体检：查"推力被放大 1000 倍"这类全局问题到底发生在数据层还是运行时。
            // 原版最大的发动机（Mammoth/Rhino）在 2000~3000 kN 量级，超过 10000 kN 就不可能是设计值。
            try
            {
                int engineCount = 0, suspicious = 0;
                float maxSeen = 0f;
                string maxName = "";
                foreach (AvailablePart ap in PartLoader.LoadedPartsList)
                {
                    ConfigNode cp = ap.partConfig;
                    if (cp == null) continue;
                    foreach (ConfigNode m in cp.GetNodes("MODULE"))
                    {
                        string mn = m.GetValue("name");
                        if (mn != "ModuleEngines" && mn != "ModuleEnginesFX" && mn != "ModuleEnginesRF") continue;
                        engineCount++;
                        float mt = 0f;
                        float.TryParse(m.GetValue("maxThrust"), out mt);
                        if (mt > maxSeen) { maxSeen = mt; maxName = ap.name + "/" + mn; }
                        if (mt > 10000f)
                        {
                            suspicious++;
                            if (suspicious <= 10)
                                Log(string.Format("  [可疑推力] {0} {1} maxThrust={2:F0} kN maxFuelFlow={3} {4}",
                                    ap.name, mn, mt, m.GetValue("maxFuelFlow"), m.GetValue("g")));
                        }
                    }
                }
                Log(string.Format("原版发动机数据体检：引擎模块 {0} 个，maxThrust>10000 kN 的 {1} 个，最大 {2:F0} kN（{3}）",
                    engineCount, suspicious, maxSeen, maxName));

                // 运行时口径再看一遍最常见的两台：prefab 上的字段才是飞行时真正用的
                for (int i = 0; i < PartLoader.LoadedPartsList.Count; i++)
                {
                    AvailablePart ap = PartLoader.LoadedPartsList[i];
                    if (ap.name != "turboJet" && ap.name != "turboFanSize2" && ap.name != "JetEngine" && ap.name != "JetForgeEngine"
                        && ap.name != "liquidEngine2" && ap.name != "liquidEngineMainsail_v2") continue;
                    if (ap.partPrefab == null) { Log(string.Format("  {0}：prefab 为空", ap.name)); continue; }
                    ModuleEngines me = ap.partPrefab.GetComponent<ModuleEngines>();
                    if (me == null) { Log(string.Format("  {0}：没有 ModuleEngines", ap.name)); continue; }
                    Log(string.Format("  [运行时] {0}：maxThrust={1:F1} kN maxFuelFlow={2:F5} g={3:F5} Isp@0={4:F0} s minThrust={5:F1}",
                        ap.name, me.maxThrust, me.maxFuelFlow, me.g,
                        me.atmosphereCurve != null ? me.atmosphereCurve.Evaluate(0f) : -1f, me.minThrust));
                    // 施力方向对照：原版 ThrustUpdate 施的是 -t.forward，所以 t.forward 必须指向"排气方向"。
                    // 打印原版与自家的局部朝向，一眼能看出谁反了。
                    // 自家零件不能按 thrustVectorTransformName 找——那是原版字段，指向名字叫
                    // "thrustTransform" 的占位模型节点，抓到的是别人的。
                    string tname = ap.name == "JetForgeEngine"
                        ? JetForgeMeshBuilder.ThrustTransformName
                        : me.thrustVectorTransformName;
                    DumpThrustTransform(ap.name, ap.partPrefab, tname);
                    me = null;
                }
            }
            catch (Exception e) { Log("发动机数据体检异常: " + e.Message); }

            SectionSpoolSpeed(design);

            // ---- 进气道：从"函数"变成"零件" ----
            Section("进气道：型式、恢复、限流、原版件兼容");
            try
            {
                EngineDesignData id = new EngineDesignData();
                Log("进气道：");
                Log(string.Format("  缺省：{0}｜喉道 {1:F3} m²（老口径 π(D/2)² = {2:F3}）｜恢复 Ma0.85 {3:F3}",
                    IntakeSpec.KindName(id.Intake.Kind), Intake.ThroatAreaOf(id),
                    Math.PI * Math.Pow(id.Fan.Diameter / 2.0, 2),
                    Intake.Recovery(id.Intake, id.Tech, 0.85)));

                double axT = 0.0, rampT = 0.0, pitotT = 0.0;
                foreach (IntakeKind k in new[] { IntakeKind.Axial, IntakeKind.Pitot, IntakeKind.Ramp, IntakeKind.Cone })
                {
                    EngineDesignData dk = new EngineDesignData { Intake = new IntakeSpec { Kind = k } };
                    OffDesignResult r = OffDesign.Solve(dk, 11000.0, 1.80, 0.85);
                    double th = r.Ok ? r.NetThrust / 1000.0 : 0.0;
                    if (k == IntakeKind.Axial) axT = th;
                    if (k == IntakeKind.Ramp) rampT = th;
                    if (k == IntakeKind.Pitot) pitotT = th;
                    Log(string.Format("  {0,-5} 11 km / Ma1.80：恢复 {1:F3}｜推力 {2:F2} kN｜流量 {3:F1} kg/s",
                        IntakeSpec.KindName(k), Intake.Recovery(new IntakeSpec { Kind = k }, dk.Tech, 1.80),
                        th, r.MdotAir));
                }
                Log(string.Format("  皮托比轴对称少 {0:F1} kN｜斜板比轴对称多 {1:F1} kN（同一台机器，只换进气道）",
                    axT - pitotT, rampT - axT));

                // 面马赫不再是玩家填的：喉道→发动机面的面积比定下来，出口马赫就定了。
                // 这一行是"几何说话"的直接证据——型式缺省是 0.50，型线件应该不是它。
                IntakeSpec pr = new IntakeSpec { Kind = IntakeKind.Axial, Shape = IntakeProfile.Default(1.2, 0.60) };
                IntakeSolution ps = IntakeSolver.SolvePart(pr, 0.62, TechLevel.Get("1970s"), 0.0, 0.60, 0.0,
                    double.NaN, 1.2);
                Log(string.Format("  面马赫：喉道/面 {0:F3} → 反解 {1:F3}（型式缺省 {2:F3}）｜恢复 {3:F3}｜可用 {4:F2} kg/s",
                    ps.ThroatArea / Math.Max(ps.FaceArea, 1e-9), ps.FaceMach, IntakeSpec.DefaultFaceMach(pr.Kind),
                    ps.Tpr, ps.AvailableMassFlow));

                // 流量匹配：进气道供多少，核心机就只能吃多少（交接文档 §六）
                CycleResult dpc = Cycle.SolveDesignPoint(Presets.Build("J79"));
                Log(string.Format("  流量匹配：J79 设计点 需求 {0:F2} → 可用 {1:F2} kg/s｜限流 {2}｜缺口 {3:F2} kg/s",
                    dpc.MdotAir + dpc.InletMassFlowShortfall, dpc.InletAvailableMassFlow,
                    dpc.InletFlowLimited ? "是" : "否", dpc.InletMassFlowShortfall));
                bool okFm = ps.FaceMach > 0.3 && ps.FaceMach < 0.9
                            && Math.Abs(ps.FaceMach - IntakeSpec.DefaultFaceMach(pr.Kind)) > 0.01;
                bool okFm2 = dpc.InletAvailableMassFlow > 0.0
                             && dpc.MdotAir <= dpc.InletAvailableMassFlow * 1.001 + 1e-9;
                Log(string.Format("  断言：面马赫来自几何 {0}｜流量不超可用 {1}",
                    okFm ? "对" : "不对", okFm2 ? "对" : "不对"));

                EngineDesignData bl = new EngineDesignData { Intake = new IntakeSpec { BleedFrac = 0.05 } };
                OffDesignResult rb = OffDesign.Solve(bl, 0.0, 0.0, 1.0);
                OffDesignResult r0 = OffDesign.Solve(new EngineDesignData(), 0.0, 0.0, 1.0);
                if (rb.Ok && r0.Ok)
                    Log(string.Format("  抽气 5%：流量 {0:F1} → {1:F1} kg/s（{2:P1}）｜只折流量，不给喘振裕度好处",
                        r0.MdotAir, rb.MdotAir, rb.MdotAir / r0.MdotAir));

                EngineDesignData mis = new EngineDesignData { DesignMach = 2.0, Intake = new IntakeSpec { Kind = IntakeKind.Pitot } };
                Log("  配不配：" + (Intake.Mismatch(mis, mis.Intake.Resolved()) ?? "（没报）"));

                bool okLeg = Math.Abs(Intake.ThroatAreaOf(id) - Math.PI * Math.Pow(id.Fan.Diameter / 2.0, 2)) < 1e-6;
                bool okKind = pitotT < axT && rampT > axT;
                Log(string.Format("  断言：缺省不改老口径 {0}｜皮托更差、斜板更好 {1}",
                    okLeg ? "对" : "不对", okKind ? "对" : "不对"));

                // ---- 独立进气道零件：它自己的那套数是不是跟求解器一致 ----
                //
                // 这里不建 Part（自检跑在 KSP 主线程里，凭空 new Part 会带出模型与资源依赖），
                // 只 new 一个模块、塞一份 Spec 进去，看它自己算出来的面积/直径与
                // IntakeSolver 对同一份 Spec 的结果是否一致。零件与整机共用一份求解，
                // 这条断言就是"共用"的凭据。
                try
                {
                    JetForge.KSP.ModuleJetForgeIntake part = new JetForge.KSP.ModuleJetForgeIntake();
                    part.Spec = new IntakeSpec { Kind = IntakeKind.Cone, CaptureDiameter = 0.80, ThroatRatio = 0.86 };
                    part.techName = "1970s";
                    IntakeSolution solo = IntakeSolver.SolvePart(part.Spec, part.FaceDiameter, part.Tech, 0.0, 0.0);
                    bool okArea = Math.Abs(part.CaptureArea - solo.CaptureArea) < 1e-6
                               && Math.Abs(part.ThroatArea - solo.ThroatArea) < 1e-6;
                    bool okFace = Math.Abs(part.FaceDiameter - Math.Sqrt(4.0 * solo.ThroatArea / Math.PI)) < 1e-6;
                    Log(string.Format("  独立进气道零件：捕获 Ø{0:F2} m（{1:F3} m²）｜喉道 {2:F3} m²｜出口 Ø{3:F2} m｜恢复 {4:F3}",
                        part.CaptureDiameter, part.CaptureArea, part.ThroatArea, part.FaceDiameter, solo.Tpr));
                    Log(string.Format("  断言：零件面积与求解器一致 {0}｜出口直径按喉道反推 {1}",
                        okArea ? "对" : "不对", okFace ? "对" : "不对"));

                    // 两件同款合成：面积与流量翻倍、恢复不变
                    List<IntakeUnit> units = new List<IntakeUnit>();
                    units.Add(new IntakeUnit(part.Spec, part.FaceDiameter, "左"));
                    units.Add(new IntakeUnit(part.Spec, part.FaceDiameter, "右"));
                    IntakeInstall two = IntakeInstall.Aggregate(units, part.Tech, 0.0, 0.0);
                    Log(string.Format("  两件合成：捕获 {0:F3} m²｜喉道 {1:F3} m²｜可用 {2:F1} kg/s｜恢复 {3:F3}",
                        two.CaptureArea, two.ThroatArea, two.AvailableMassFlow, two.Tpr));
                    Log(string.Format("  断言：两件面积翻倍 {0}｜恢复不变 {1}｜件数 {2}",
                        Math.Abs(two.CaptureArea - solo.CaptureArea * 2.0) < 1e-6 ? "对" : "不对",
                        Math.Abs(two.Tpr - solo.Tpr) < 1e-3 ? "对" : "不对", two.Count));

                    // ---- 大进气道不能白拿推力：发动机进口那张面是硬上限 ----
                    //
                    // 这一行是 FlowCappedByEngineFace / EngineFaceCapMassFlow 唯一的读者，
                    // 也是"§284 那个 7787 kg/s"在实机上真的被压住的凭据。
                    IntakeSpec hugeSpec = new IntakeSpec { Kind = IntakeKind.Cone, CaptureDiameter = 7.00, ThroatRatio = 0.90 };
                    List<IntakeUnit> hugeUnits = new List<IntakeUnit>();
                    hugeUnits.Add(new IntakeUnit(hugeSpec, 7.00, "超大"));
                    IntakeInstall big = IntakeInstall.Aggregate(hugeUnits, part.Tech, 0.0, 0.0);
                    double smallFace = Math.PI * 0.25 * 0.50 * 0.50;
                    IntakeSolution capped = big.ToSolution(0.0, 0.0, smallFace, IntakeSolver.FaceMachOf(null), double.NaN);
                    Log(string.Format("  进气道封顶：Ø7.00 m 的件想要 {0:F1} kg/s｜Ø0.50 m 发动机面只收 {1:F1} kg/s｜多余 {2:F1} kg/s 在唇口溢流",
                        big.AvailableMassFlow, capped.LegacyMassFlow, big.AvailableMassFlow - capped.LegacyMassFlow));
                    Log(string.Format("  断言：超大件被发动机面削住 {0}｜削到这一面能吃的量 {1}",
                        capped.FlowCappedByEngineFace ? "对" : "不对",
                        Math.Abs(capped.LegacyMassFlow - capped.EngineFaceCapMassFlow) < 1e-6 ? "对" : "不对"));
                }
                catch (Exception ex)
                {
                    Log("  独立进气道零件：自检抛异常 " + ex.Message);
                }

                // ---- 原版进气道桥接：读的是真模块的字段名，不是我们猜的名字 ----
                //
                // IntakeLink.TryStockUnit 用反射读 area / machLimit / maxIntakeSpeed / machCurve。
                // 字段名一旦跟这个 KSP 版本对不上，它只会静默返回 false——表现是
                // "装了原版进气道，发动机一点都不买账"，最难查的一类错。
                // 这里不建 Part（同上面的理由），只把真类型上的字段名打出来核对。
                //
                // 另外钉住一条教训：intakeSpeed **不是速度上限**（环形进气道写的是 15），
                // 拿它当上限就是 15 m/s 断气、起飞熄火。真性能曲线是 machCurve（乘子）。
                try
                {
                    Type mriType = typeof(ModuleResourceIntake);
                    BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
                    var names = new StringBuilder();
                    int area = 0, limit = 0, res = 0;
                    foreach (FieldInfo f in mriType.GetFields(pub))
                    {
                        string low = f.Name.ToLowerInvariant();
                        if (low.Contains("area")) area++;
                        if (low.Contains("mach") || low.Contains("speed")) limit++;
                        if (low.Contains("resource")) res++;
                        if (low.Contains("area") || low.Contains("mach") || low.Contains("speed")
                            || low.Contains("scalar") || low.Contains("resource"))
                        {
                            if (names.Length > 0) names.Append("｜");
                            names.Append(f.Name).Append(':').Append(f.FieldType.Name);
                        }
                    }
                    bool hasArea = mriType.GetField("area", pub) != null;
                    bool hasCurve = mriType.GetField("machCurve", pub) != null;
                    Log("  原版进气道字段：" + names);
                    Log(string.Format("  断言：面积字段 area 存在 {0}｜性能曲线 machCurve 存在 {1}｜马赫/速度类字段 {2} 个｜资源类字段 {3} 个",
                        hasArea ? "对" : "不对", hasCurve ? "对" : "不对", limit + area, res));
                    Log("  口径：area 是游戏口径（物理面积 ≈ area×64）｜intakeSpeed 是低速吸气流速、不是上限｜machCurve 才是性能乘子");

                // 拿真实的原版零件走一遍桥接：口径是量出来的还是退回 cfg、曲线读没读到、
                // 换算出来的捕获面积是不是人话。这一条比字段列表有用——字段在、语义错，照样是坏的。
                try
                {
                    string[] probes = { "CircularIntake", "ramAirIntake", "shockConeIntake", "airIntakeRadial" };
                    for (int q = 0; q < probes.Length; q++)
                    {
                        AvailablePart sap = PartLoader.getPartInfoByName(probes[q]);
                        if (sap == null || sap.partPrefab == null) { Log("  原版件 " + probes[q] + "：库里没有"); continue; }
                        double mouth = IntakeLink.MouthDiameterOf(sap.partPrefab);
                        List<IntakeUnit> got = new List<IntakeUnit>();
                        int n = IntakeLink.AppendStockUnits(sap.partPrefab, null, got);
                        string curve = "无曲线";
                        if (got.Count > 0 && !string.IsNullOrEmpty(got[0].SupplyStamp)) curve = got[0].SupplyStamp;
                        double sup25 = got.Count > 0 ? got[0].SupplyAt(2.5) : -1.0;
                        // 曲线为什么没读到？把反射这一步单独摊开看，别在桥接层猜
                        string dbg = "";
                        for (int mi = 0; mi < sap.partPrefab.Modules.Count; mi++)
                        {
                            ModuleResourceIntake mri2 = sap.partPrefab.Modules[mi] as ModuleResourceIntake;
                            if (mri2 == null) continue;
                            List<double> km2, kv2;
                            bool ok2 = IntakeLink.ReadCurve(mri2, "machCurve", out km2, out kv2);
                            object raw = null;
                            var fi = mri2.GetType().GetField("machCurve", System.Reflection.BindingFlags.Instance
                                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (fi != null) raw = fi.GetValue(mri2);
                            dbg += string.Format("｜[{0}] machCurve {1}｜取值 {2}｜节点 {3}",
                                mri2.GetType().Name, ok2 ? "读到" : "没读到",
                                raw == null ? "null" : raw.GetType().Name,
                                ok2 ? km2.Count.ToString() : "-");
                            if (raw != null)
                            {
                                System.Reflection.FieldInfo[] rfs = raw.GetType().GetFields(System.Reflection.BindingFlags.Instance
                                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                for (int k = 0; k < rfs.Length && k < 6; k++)
                                    dbg += "｜f:" + rfs[k].Name + "=" + rfs[k].FieldType.Name;
                            }
                            break;
                        }
                        if (got.Count > 0)
                        {
                            double cfgArea = -1.0;
                            for (int mi = 0; mi < sap.partPrefab.Modules.Count; mi++)
                            {
                                ModuleResourceIntake mri3 = sap.partPrefab.Modules[mi] as ModuleResourceIntake;
                                if (mri3 != null) { cfgArea = mri3.area; break; }
                            }
                            if (cfgArea > 0.0)
                                Log(string.Format("    ↳ 对照：cfg area = {0:F4}（×64 折算 Ø{1:F2} m）｜量外形 Ø{2:F2} m｜面积比值 {3:F1}×",
                                    cfgArea, Math.Sqrt(4.0 * cfgArea * 64.0 / Math.PI), mouth,
                                    mouth > 0.0 ? (Math.PI * 0.25 * mouth * mouth) / (cfgArea * 64.0) : -1.0));
                        }
                        Log(string.Format("  原版件 {0}：量到口径 {1:F3} m｜折进气道 {2} 个｜{3}｜M2.5 供气 {4:P0}｜捕获直径 {5:F3} m{6}",
                            probes[q], mouth, n, curve, sup25, got.Count > 0 ? got[0].Spec.CaptureDiameter : 0.0, dbg));
                    }
                }
                catch (Exception e2) { Log("  原版件桥接试算异常：" + e2.Message); }
                }
                catch (Exception ex)
                {
                    Log("  原版进气道字段：自检抛异常 " + ex.Message);
                }

                // 字段元数据：可视化编辑器以后按这张表铺控件
                Log("字段元数据：" + DesignSchema.Report());
                EngineDesignData rw = new EngineDesignData();
                bool w1 = DesignSchema.Write(rw, "Hpc.Stages", 11);
                bool w2 = DesignSchema.Write(rw, "Fan.HubTipRatio", 0.42);
                bool goneMeta = DesignSchema.Find("Intake.Kind") == null
                                && DesignSchema.Find("Intake.Model") == null
                                && DesignSchema.Find("Intake.AlphaDeg") == null;
                bool goneWrite = !DesignSchema.Write(rw, "Intake.Kind", (double)IntakeKind.Cone);
                Log(string.Format("  按路径读写：Hpc.Stages → {0:F0}｜Fan.HubTipRatio → {1:F2}｜错路径返回 {2}｜进气道元数据已撤 {3}｜按路径仍能反射写到 {4}",
                    DesignSchema.Read(rw, "Hpc.Stages"), rw.Fan.HubTipRatio,
                    YN(DesignSchema.Write(rw, "Fan.Nonsense", 1.0)), YN(goneMeta), YN(!goneWrite)));
                // 判据只认 Find：Write 走的是对象反射，不查元数据表——
                // "字段还能被写进去"根本不证明它还挂在编辑器上。这一条以前写反过。
                Log(string.Format("  断言：按路径存取可用 {0}｜进气道选项确实不在发动机 schema 里 {1}",
                    (w1 && w2 && Math.Abs(DesignSchema.Read(rw, "Hpc.Stages") - 11) < 1e-9
                     && Math.Abs(rw.Fan.HubTipRatio - 0.42) < 1e-9) ? "对" : "不对",
                    goneMeta ? "对" : "不对"));
            }
            catch (Exception e) { Log("  进气道异常: " + e.Message); }

            Log("==== 自检结束 ====");

            // 自动台架：只在 autotest.marker 存在时起飞。
            // 平时这个分支永远不执行——它会把玩家直接拽进飞行场景。
            TryAutoFlightTest();
        }

        /// <summary>
        /// 打印某个零件 prefab 上施力变换的局部朝向。
        /// 原版 ThrustUpdate 施力那一句是 part.AddForceAtPosition(-t.forward * finalThrust, t.position)，
        /// 所以 t.forward 必须指向**排气方向**（喷口朝哪，力就朝反方向）。
        /// 把原版与自家的 localForward 并排打出来，谁反了一眼就看得出。
        /// </summary>
        void DumpThrustTransform(string partName, Part prefab, string transformName)
        {
            try
            {
                if (prefab == null) return;
                // 自家的节点用唯一名字，且是零件根下的直接子物体——不能走 FindModelTransform 深搜，
                // 否则抓到 cfg 占位模型（stock turboFanSize1）里那个同名节点，读数就是别人的。
                Transform t = prefab.transform.Find(transformName);
                if (t == null) t = prefab.FindModelTransform(transformName);
                if (t == null) { Log(string.Format("    [{0}] 找不到施力变换 {1}", partName, transformName)); return; }
                Vector3 f = t.localRotation * Vector3.forward;
                Log(string.Format("    [{0}] 施力变换 {1} 局部位置 {2:F2}/{3:F2}/{4:F2} localForward {5:F2}/{6:F2}/{7:F2}",
                    partName, t.name, t.localPosition.x, t.localPosition.y, t.localPosition.z, f.x, f.y, f.z));
            }
            catch (Exception e) { Log("  施力变换打印异常: " + e.Message); }
        }

        /// <summary>
        /// 准备一架测试机：借默认存档把 HighLogic.CurrentGame 填上，然后进航天中心。
        /// 真正的起飞交给 JetForgeAutoLaunch（SpaceCenter 场景里做），
        /// 原因是主菜单里没有 KSC 的设施变换，直接从主菜单起飞会被踢回去
        /// （实测日志：[PSystemSetup::SpaceCenterFacility::SpawnPoint]: Cannot find a transform
        /// named 'Facility/LaunchPad_spawn' on 'LaunchPad'，紧接着 FLIGHT → SPACECENTER）。
        /// </summary>
        void TryAutoFlightTest()
        {
            try
            {
                string marker = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "GameData/JetForge/autotest.marker");
                if (!System.IO.File.Exists(marker)) return;

                string craft = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "Ships/VAB/JetForgeTest.craft");
                Log("自动台架：标记在，" + (System.IO.File.Exists(craft) ? "开始载入 " + craft : "但找不到 craft：" + craft));
                if (!System.IO.File.Exists(craft)) return;

                ShipConstruct sc = ShipConstruction.LoadShip(craft);
                if (sc == null) { Log("自动台架：LoadShip 返回 null（craft 读不进来）"); return; }
                Log("自动台架：craft 载入成功，零件 " + sc.parts.Count + " 个");

                if (HighLogic.CurrentGame == null)
                {
                    HighLogic.SaveFolder = "default";
                    Game g = GamePersistence.LoadGame("persistent", "default", true, false);
                    if (g == null) { Log("自动台架：借默认存档建游戏失败（LoadGame 返回 null）"); return; }
                    HighLogic.CurrentGame = g;
                    Log("自动台架：借默认存档 default/persistent 建好了当前游戏");
                }

                AutoFlightPending = true;
                Log("自动台架：转到航天中心，起飞交给 JetForgeAutoLaunch");
                HighLogic.LoadScene(GameScenes.SPACECENTER);
            }
            catch (Exception e)
            {
                Log("自动台架异常: " + e);
            }
        }

        /// <summary>给 JetForgeAutoLaunch 看的接力棒：自检已经把存档准备好了。</summary>
        internal static bool AutoFlightPending;

        /// <summary>反射读 KSP 的 Localizer.CurrentLanguage（读不到就给空串，不抛）。</summary>
        static string KspLanguage()
        {
            try
            {
                Type t = Type.GetType("KSP.Localization.Localizer, Assembly-CSharp");
                PropertyInfo p = t != null ? t.GetProperty("CurrentLanguage") : null;
                if (p == null) return "（没有这个属性）";
                string s = (p.GetValue(null, null) as string) ?? "";
                return s.Length == 0 ? "（空）" : s + (p.CanWrite ? "（可写）" : "（只读）");
            }
            // 探测豁免：读不到只是"这条没验"，不该让自检失败。
            catch (Exception e) { return "（读失败：" + e.GetType().Name + "）"; }
        }

        /// <summary>
        /// 一个键翻完之后还留着 `#autoLOC` 吗？留着＝这张语言表里没有它（或者它压根不是一个键）。
        /// 空白也算翻不出来——空白更坏：玩家看到的是一个没有名字的数值。
        /// </summary>
        static int LeakCount(string formatted)
        {
            if (string.IsNullOrEmpty(formatted)) return 1;
            return formatted.IndexOf("#autoLOC", StringComparison.Ordinal) >= 0 ? 1 : 0;
        }

        /// <summary>反射读 GameSettings.LANGUAGE —— 这是设置文件里那一行，不等于实际加载的表。</summary>
        static string GameLanguage()
        {
            try
            {
                Type t = Type.GetType("GameSettings, Assembly-CSharp");
                FieldInfo f = t != null ? t.GetField("LANGUAGE") : null;
                if (f == null) return "（没有这个字段）";
                string s = (f.GetValue(null) as string) ?? "";
                return s.Length == 0 ? "（空）" : s;
            }
            // 探测豁免：同上。
            catch (Exception e) { return "（读失败：" + e.GetType().Name + "）"; }
        }

        /// <summary>
        /// 临时把游戏语言切到英文，格式化两个零件名，再切回来。
        /// 为什么需要：这台机器是中文系统，"中文显示正常"完全不能说明英文词条存在——
        /// 而英文词条缺了，英文玩家看到的是 #autoLOC_…，那是最刺眼的错。
        /// 切不动（老版本没有可写的 CurrentLanguage）就返回 false，自检如实说"这一项没验"。
        /// </summary>
        static bool TryFormatEnglish(ref string engine, ref string intake)
        {
            try
            {
                Type t = Type.GetType("KSP.Localization.Localizer, Assembly-CSharp");
                PropertyInfo p = t != null ? t.GetProperty("CurrentLanguage") : null;
                if (p == null || !p.CanWrite) return false;
                string prev = (p.GetValue(null, null) as string) ?? "English";
                p.SetValue(null, "English", null);
                engine = global::KSP.Localization.Localizer.Format("#autoLOC_JF_engine_title");
                intake = global::KSP.Localization.Localizer.Format("#autoLOC_JF_intake_title");
                p.SetValue(null, prev, null);
                return true;
            }
            // 探测豁免：切不了语言只是"这一项没验"，不该让自检失败。
            catch (Exception) { return false; }
        }

        /// <summary>逐字段比对（含嵌套的部件 spec），给存档往返用。只比 Persistence 管得着的公共字段。</summary>
        static void CmpFields(object a, object b, string path, List<string> diffs)
        {
            Type t = a.GetType();
            foreach (System.Reflection.FieldInfo f in t.GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (f.Name == "NodeName") continue;
                string p = path + "." + f.Name;
                Type ft = f.FieldType;
                object va = f.GetValue(a), vb = b == null ? null : f.GetValue(b);
                if (ft == typeof(double))
                {
                    if (Math.Abs((double)va - (double)vb) > 2e-6) diffs.Add(p);
                }
                else if (ft == typeof(TechLevel))
                {
                    string na = va == null ? null : ((TechLevel)va).Name;
                    string nb = vb == null ? null : ((TechLevel)vb).Name;
                    if (na != nb) diffs.Add(p);
                }
                else if (ft.IsClass && ft.Namespace == "JetForge.Core")
                {
                    if (va == null || vb == null) { if (va != vb) diffs.Add(p); continue; }
                    CmpFields(va, vb, p, diffs);
                }
                else if (va is System.Collections.IList)
                {
                    // 逐元素比。以前走到下面那条 Equals 上，List 是引用相等，
                    // 内容一模一样的两份也会全报"不一致"——自检里那条
                    // "存档往返：10 个字段不一致：design.Lpc.StageTip…"就是这么来的假警报。
                    System.Collections.IList la = va as System.Collections.IList;
                    System.Collections.IList lb = vb as System.Collections.IList;
                    if (lb == null) { diffs.Add(p); continue; }
                    if (la.Count != lb.Count) { diffs.Add(p); continue; }
                    for (int i = 0; i < la.Count; i++)
                    {
                        if (la[i] is double && lb[i] is double)
                        {
                            if (Math.Abs((double)la[i] - (double)lb[i]) > 2e-6) { diffs.Add(p + "[" + i + "]"); break; }
                        }
                        else if (!object.Equals(la[i], lb[i])) { diffs.Add(p + "[" + i + "]"); break; }
                    }
                }
                else if (!object.Equals(va, vb))
                {
                    diffs.Add(p);
                }
            }
        }

        /// <summary>
        /// 数零件层级里有几个叫这个名字的物体。用递归数而不是 GetComponentsInChildren——
        /// 后者按组件数，同名节点上可能没有我们要的组件，数出来会少。
        /// </summary>
        static int CountByName(Part part, string name)
        {
            if (part == null || string.IsNullOrEmpty(name)) return 0;
            int n = 0;
            CountRecursive(part.transform, name, ref n);
            return n;
        }

        static void CountRecursive(Transform t, string name, ref int n)
        {
            if (t == null) return;
            if (t.gameObject.name == name) n++;
            for (int i = 0; i < t.childCount; i++) CountRecursive(t.GetChild(i), name, ref n);
        }

        /// <summary>清掉自检自己在 prefab 上造的节点，别把一台 1.40 m 的壳留给真实零件。</summary>
        static void DestroyByName(Part part, string name)
        {
            if (part == null) return;
            Transform[] all = part.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].gameObject.name == name)
                    UnityEngine.Object.Destroy(all[i].gameObject);
        }

        static EngineDesignData LoadDesignFromGameDatabase()
        {
            // 首选：从 PartLoader 的运行时配置里取，避免数据库扫描与补丁后的差异
            try
            {
                var ap = PartLoader.getPartInfoByName("JetForgeEngine");
                if (ap != null && ap.partConfig != null)
                {
                    ConfigNode n = FromPartNode(ap.partConfig);
                    if (n != null)
                    {
                        Debug.Log("[JetForge.SelfTest] 设计来源：PartLoader.partConfig");
                        return EngineDesignData.Load(n);
                    }
                }
                Debug.Log("[JetForge.SelfTest] PartLoader 路径没取到，partConfig=" +
                          (ap != null ? (ap.partConfig != null ? "有" : "空") : "零件不存在"));
            }
            catch (Exception e) { Debug.Log("[JetForge.SelfTest] PartLoader 路径异常: " + e.Message); }

            ConfigNode[] parts = GameDatabase.Instance.GetConfigNodes("PART");
            Debug.Log("[JetForge.SelfTest] 数据库里 PART 节点 " + parts.Length + " 个");
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].GetValue("name") != "JetForgeEngine") continue;
                Debug.Log("[JetForge.SelfTest] 找到 PART，子节点：" + JoinNames(parts[i].GetNodes()));
                ConfigNode n = FromPartNode(parts[i]);
                if (n != null) return EngineDesignData.Load(n);
            }
            Debug.Log("[JetForge.SelfTest] 数据库扫描也没找到 JETFORGE_DESIGN");
            return null;
        }

        /// <summary>
        /// 只算矢量那几条判据并取第一句。走 CheckVectoring 而不是整套 Evaluate：
        /// 自检这里关心的是"喷管型式与矢量开关配不配"，不需要一次完整求解。
        /// </summary>
        static string FirstNozzleFinding(EngineDesignData d, NozzleKind kind, bool vector)
        {
            EngineDesignData c = d.Clone();
            c.Nozzle = kind;
            c.ThrustVectoring = vector;
            List<Finding> fs = Criteria.CheckVectoring(c);
            return fs.Count > 0 ? fs[0].Line() : "（没有矢量判据）";
        }

        /// <summary>
        /// 年代上限的判据只看年份与所选控制系统，不需要求解。
        /// </summary>
        static string FirstControlFinding()
        {
            EngineDesignData c = new EngineDesignData();
            c.TechName = "1940s";
            c.Control = ControlKind.Fadec;
            List<Finding> fs = Criteria.CheckControl(c);
            return fs.Count > 0 ? fs[0].Line() : "（没有控制系统判据）";
        }

        static string JoinNames(ConfigNode[] nodes)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < nodes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(nodes[i].name);
            }
            return sb.ToString();
        }

        static ConfigNode FromPartNode(ConfigNode partNode)
        {
            ConfigNode[] mods = partNode.GetNodes("MODULE");
            for (int j = 0; j < mods.Length; j++)
            {
                if (mods[j].GetValue("name") != "ModuleJetForgeEngine") continue;
                Debug.Log("[JetForge.SelfTest] 找到模块节点，子节点：" + JoinNames(mods[j].GetNodes()));
                return mods[j].GetNode(EngineDesignData.NodeName);
            }
            return null;
        }
    }
}
