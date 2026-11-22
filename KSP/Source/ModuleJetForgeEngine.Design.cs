using System;
using System.Collections.Generic;
using System.Text;
using JetForge.Core;
using JetForge.FX;
using JetForge.UI;
using UnityEngine;

namespace JetForge.KSP
{
    /// <summary>
    /// 设计与同步：设计器入口、应用设计、同型跟随、简单模式换算。
    ///
    /// 从 `ModuleJetForgeEngine.cs` 拆出来的。字段一个没动——`[KSPField]`
    /// 与反射读写的成员留在主文件里，这里只有方法。
    /// </summary>
    public partial class ModuleJetForgeEngine
    {
        public override string GetInfo()
        {
            StringBuilder sb = new StringBuilder();
            // 进气道是硬件，不是设计参数：没接上就不吸气，说清楚比让玩家去猜推力为什么是 0 好。
            if (IntakeMissingNow())
                sb.AppendLine(Loc.T("<color=#ffb347>· 没接进气道：这台发动机不吸气，点不着。同一艘船上装一个 JetForge 进气道或原版进气道即可。</color>", "<color=#ffb347>· No intake connected: this engine gets no air and cannot light. Add a JetForge intake or a stock intake on the same vessel.</color>"));
            // 老存档读进来的设计：哪些数是缺省填的，在 VAB 的零件说明里说一句。
            // 只说在这里，不进 PAW——飞行中那个面板已经很多字，多一句就没人看任何一句。
            if (Design != null && Design.MigrationLine != null)
                sb.AppendLine("<color=#ffcc44>· " + Design.MigrationLine + "</color>");
            if (!DesignOk())
            {
                sb.AppendLine(Loc.T("<b>JetForge</b>：设计无法闭合", "<b>JetForge</b>: design does not close"));
                if (UseSimple)
                {
                    if (SimplePoint != null)
                        foreach (string e in SimplePoint.Errors) sb.AppendLine("  <color=#ff6666>· " + e + "</color>");
                }
                else if (DesignPoint != null)
                {
                    foreach (string e in DesignPoint.Errors) sb.AppendLine("  <color=#ff6666>· " + e + "</color>");
                }
                return sb.ToString();
            }

            if (UseSimple)
            {
                sb.AppendLine("<b>" + SimpleCfg.Name + "</b>  "
                    + (SimpleCfg.Kind == EngineKind.Turbofan ? Loc.T("涡扇", "turbofan") : Loc.T("涡喷", "turbojet"))
                    + Loc.T(" · 简单模式 · ", " · simple mode · ") + (SimpleCfg.Tech != null ? SimpleCfg.Tech.Name : "1970s"));
            }
            else
            {
                sb.AppendLine("<b>" + Design.DesignName + "</b>  "
                    + (Design.Kind == EngineKind.Turbofan ? Loc.T("涡扇", "turbofan") : Loc.T("涡喷", "turbojet"))
                    + " · " + Design.TechName);
            }
            sb.AppendFormat(Loc.T("干推力 {0:F1} kN　SFC {1:F4} kg/(N·h)\n", "Dry {0:F1} kN　SFC {1:F4} kg/(N·h)\n"), DpThrust() / 1000.0, DpSfc());
            sb.AppendFormat(Loc.T("增压比 {0:F2}　涵道比 {1:F2}　进气 {2:F1} kg/s\n", "OPR {0:F2}　BPR {1:F2}　air {2:F1} kg/s\n"), DpOpr(), DpBpr(), DpMdot());
            // 供气与需求分开写：玩家报过"面板写着供气 120、发动机要 70，可推力就是上不去"，
            // 那时这两个数根本不同源（一个来自上一帧刷新、一个来自这次求解）。
            // 现在两个数都取自同一份解，封顶了就直说多出来的去哪了。
            if (_install != null && _install.Present && _install.Last.Valid && _install.Last.LegacyMassFlow > 0.0)
                sb.AppendFormat(Loc.T("进气道供 {0:F1} kg/s｜这台机器的进口面最多收 {1:F1} kg/s{2}\n", "intake supplies {0:F1} kg/s | this engine's face takes at most {1:F1} kg/s{2}\n"),
                    _install.Last.LegacyMassFlow, _install.Last.DemandMassFlow,
                    _install.Last.FlowCappedByEngineFace ? Loc.T("（多出来的在唇口溢流）", " (the rest spills at the lip)") : "");
            sb.AppendFormat("N1 {0:F0}%　N2 {1:F0}%　EGT {2:F0} K\n", DpN1(), DpN2(), DpT5());
            if (HasAfterburner())
                sb.AppendFormat(Loc.T("加力 T7 {0:F0} K\n", "afterburner T7 {0:F0} K\n"), DpT7());
            // 最低可用喷油量：低于它涡轮功就带不动压气机，转速会一路掉到风车。
            // 这个数由非设计点反解算出（OffDesign.DeriveDesignConstants），不是拍脑袋的下限。
            if (Consts != null && Consts.IdleFuelFlow > 0.0)
                sb.AppendFormat(Loc.T("慢车最低喷油量 {0:F3} kg/s（{1:F0} kg/h）\n", "min idle fuel {0:F3} kg/s ({1:F0} kg/h)\n"),
                    Consts.IdleFuelFlow, Consts.IdleFuelFlow * 3600.0);
            // 逐轴转矩：设计点是稳态，这里应当接近 0。玩家看不见"功率"，
            // 但看得懂"这根轴还在加速还是已经定住了"——它也是"油门推到底为什么还不走"的答案。
            if (!UseSimple && DesignPoint != null)
            {
                try
                {
                    List<ShaftTorque> shafts = TorqueBalance.Build(Design, DesignPoint, null);
                    for (int i = 0; i < shafts.Count; i++)
                    {
                        ShaftTorque t = shafts[i];
                        // 一行只放三个数：零件信息栏很窄，塞进惯量只会把"轴上还剩多少转矩"挤没。
                        sb.AppendFormat(Loc.T("{0} 净转矩 {1:F0} N·m（涡轮 {2:F0} / 负载 {3:F0} kW）\n", "{0} net torque {1:F0} N·m (turbine {2:F0} / load {3:F0} kW)\n"),
                            t.Name, t.NetTorque, t.TurbinePower / 1000.0, t.LoadPower / 1000.0);
                    }
                }
                // 读数缺一段要留痕：以前这里静默吞，结果"零件信息栏少一行"被当成没实现。
                catch (Exception e) { sb.AppendFormat(Loc.T("（逐轴转矩没算成：{0}）\n", "(per-shaft torque failed: {0})\n"), e.GetType().Name); }
            }
            // 压气机特性图：判据说的"喘振裕度"是个比值代理，这里给的是图上的真数。
            // 两个数不一致时先看图——图上算的是"工作点到喘振线多远"，
            // 判据算的是"流道对这么多流量宽不宽"，不是同一把尺（见 §120）。
            if (!UseSimple && DesignPoint != null && DesignPoint.Chart != null)
            {
                try
                {
                    CompressorChart cht = DesignPoint.Chart;
                    MapPoint op = cht.WorkingPoint(1.0);
                    sb.AppendFormat(Loc.T("压气机图 {0} 条转速线｜喘振裕度 {1:F1}%\n", "chart {0} speed lines | surge margin {1:F1}%\n"), cht.Lines.Count, cht.SurgeMargin(op));
                    // 堵塞侧也要看：喘振是"再收油门就转不动"，堵塞是"再加油门只有流量没有压比"。
                    // 两条墙方向相反，只看一边等于只报告一半的边界。
                    sb.AppendFormat(Loc.T("堵塞余量 {0:F1}%{1}\n", "choke margin {0:F1}%{1}\n"), cht.ChokeMargin(op),
                        cht.Choked(op) ? Loc.T("（已越界）", " (past the line)") : "");
                    sb.AppendFormat(Loc.T("工作点 Wc {0:F1} kg/s｜π {1:F2}｜η {2:F2}\n", "operating point Wc {0:F1} kg/s | pi {1:F2} | eta {2:F2}\n"), op.Wc, op.Pr, op.Eta);
                }
                catch (Exception e) { sb.AppendFormat(Loc.T("（特性图读数没取到：{0}）\n", "(chart readout unavailable: {0})\n"), e.GetType().Name); }
            }
            if (LastReport != null && LastReport.Worst != Level.Ok)
                sb.AppendFormat(Loc.T("<color=#ffcc44>判据 {0}（{1} 条警告 / {2} 条不合格）</color>\n", "<color=#ffcc44>checks {0} ({1} warnings / {2} failed)</color>\n"),
                    LastReport.Worst, LastReport.Warns.Count, LastReport.Fails.Count);
            return sb.ToString();
        }

        public CheckReport EvaluateReport()
        {
            if (UseSimple)
            {
                if (SimplePoint == null) SimplePoint = Simple.DesignPoint(SimpleCfg);
                return Criteria.EvaluateSimple(SimpleCfg, SimplePoint.AsView(), FlightAltitude());
            }
            if (DesignPoint == null) DesignPoint = Cycle.SolveDesignPoint(Design);
            ResultView v = Criteria.ViewOf(DesignPoint);
            return Criteria.Evaluate(Design, v, Design.Tech, Gas.P0, 0.0);
        }

        /// <summary>
        /// 缺陷风险档案只跟设计有关，跟飞行状态无关——所以按设计指纹缓存，
        /// 每帧重算一遍判据既浪费又会把整条飞行链路拖慢。
        /// </summary>
        private Defects.Risk CurrentRisk()
        {
            string sig;
            try { sig = DesignSignature.Of(Design); }
            catch { sig = ""; }
            if (_riskReady && sig == _riskSig && _risk != null) return _risk;
            // 设计变了就是"换了一台机器"：累计热损伤归零，否则改个滑块就带着上一台的烧蚀痕迹。
            // 喘振同理：叶片磨损不该跨机器继承。
            if (sig != _riskSig) { _defect.Reset(); _surge.Reset(); _lastThrottleSurge = -1.0; }
            _riskSig = sig;
            _riskReady = true;
            try
            {
                CheckReport rep = EvaluateReport();
                _risk = Defects.Read(rep != null ? rep.Findings : null);
            }
            catch { _risk = new Defects.Risk(); }
            return _risk;
        }

        [KSPEvent(guiName = "#autoLOC_JF_e_open_designer", guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, unfocusedRange = 8f)]
        public void OpenDesigner()
        {
            // 飞行中改设计是没有意义的：外形要重建、性能网格要重跑，而这时飞机已经在天上，
            // 玩家改完看到的是"读数变了但飞机还在按旧参数飞"。编辑器里改才是设计，飞行中改是作弊。
            if (!HighLogic.LoadedSceneIsEditor)
            {
                ScreenMessages.PostScreenMessage(Loc.T("发动机设计器只能在机库里用。飞行中改设计不会重建外形，先回机库。", "The designer only works in the VAB/SPH. Changing a design in flight does not rebuild the mesh; go back to the hangar first."),
                                                 4f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            JetForgeDesignerWindow.Open(this);
        }

        [KSPEvent(guiName = "#autoLOC_JF_e_rebuild_grid", guiActive = true, guiActiveEditor = true)]
        public void RebuildGridEvent()
        {
            GridDirty = true;
            Consts = null;
            SimpleConsts = null;
            EnsureReady();
            LastReport = EvaluateReport();
            // 故障门槛从设计与判据里量出来（不再写死四个数）。设计改了就在这条路上重算。
            Defects.Configure(_defect, Defects.For(Design, LastReport));
        }

        [KSPAction("#autoLOC_JF_e_open_designer")]
        public void OpenDesignerAction(KSPActionParam p)
        {
            OpenDesigner();
        }

        /// <summary>设计器改完参数后回调（残酷模式）。</summary>
        public void ApplyDesign(EngineDesignData d, bool rebuildGrid = true)
        {
            // 换设计 ⇒ 惯量和设计转速都变了，转子律必须重建，否则还按上一台的惯量在加速。
            _spoolLaw = null;
            // 控制系统同理：换了一台机器，上一台的油门轨迹不该带过来。
            _control.Reset();
            _lastT4 = 0.0;
            _lastNPct = 0.0;
            ApplyDesign(d, rebuildGrid, true);
        }

        /// <summary>
        /// 改这一台，同款的一起改。
        ///
        /// 对称放出来的每一台都是**独立的零件**，各有各的 `Design` 实例；
        /// 而设计器窗口只认它打开的那一台。所以以前"改一台、另外几台还是老样子"，
        /// 玩家眼里就是"对称的两台一台长一台短，前端对齐、尾端错开"。
        /// 这里按**改动之前的设计指纹**找人：只有改之前和它一模一样的那些才跟着改，
        /// 玩家故意做两台不同参数的发动机不会被顺手抹掉。
        /// </summary>
        public void ApplyDesign(EngineDesignData d, bool rebuildGrid, bool propagate)
        {
            if (d == null) return;
            string before = null;
            try { if (Design != null) before = DesignSignature.Of(Design); } catch { before = null; }

            Design = d;
            UseSimple = false;
            GridDirty = true;
            Consts = null;
            _idleStarved = false;   // 换了一台机器，上一台的"烧不住"不该带过来
            DesignPoint = Cycle.SolveDesignPoint(Design);
            Consts = OffDesign.DeriveDesignConstants(Design);
            if (rebuildGrid) RebuildGrid();
            LastReport = EvaluateReport();
            // 设计里的 AutoLight 现在是"默认档位"：换设计时把飞行中的档位重置成设计说的那样，
            // 之后玩家按按钮改的是运行时状态。以前它是运行时的第二道闸，两处各说各话。
            abAuto = Design.Afterburner.AutoLight && HasAfterburner();
            if (!HasAfterburner()) afterburnerLit = false;
            RefreshAbButton();
            // 只有几何层变了才重建网格。拖压比滑杆时顶点一个都不动，重建一遍纯属白干——
            // 而且会连带把材质、羽流挂点重挂一遍，手感也跟着断。
            string geoSig = null;
            try { geoSig = RefreshStamps.EngineGeometry(Design); } catch { geoSig = null; }
            if (geoSig != _meshSig || part.FindModelTransform(JetForgeMeshBuilder.VisualObjectName) == null)
            {
                JetForgeMeshBuilder.RefreshPartMesh(part, Design);
                _meshSig = geoSig;
            }
            RefreshThrustTransforms(Design);
            RefreshMass();
            // 外形重建了，喷口位置与口径也跟着变，尾焰挂点必须一起重建。
            AttachPlume();
            // 设计与判据变了，故障门槛跟着重算：玩家改完裕度，抗造程度也该变。
            LastReport = EvaluateReport();
            Defects.Configure(_defect, Defects.For(Design, LastReport));

            if (propagate && !string.IsNullOrEmpty(before)) PropagateDesign(before, Design);
        }

        /// <summary>把这次改动同步给同一艘船/同一个编辑器飞船里"改动前和它一样"的其它 JetForge 发动机。</summary>
        private void PropagateDesign(string beforeSig, EngineDesignData d)
        {
            try
            {
                List<Part> all = null;
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null) all = EditorLogic.fetch.ship.parts;
                else if (vessel != null) all = vessel.parts;
                if (all == null) return;
                int n = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    Part p = all[i];
                    if (p == null || p == part) continue;
                    ModuleJetForgeEngine m = p.FindModuleImplementing<ModuleJetForgeEngine>();
                    if (m == null || m.Design == null || m.UseSimple) continue;
                    string sig;
                    try { sig = DesignSignature.Of(m.Design); } catch { continue; }
                    if (sig != beforeSig) continue;
                    m.ApplyDesign(d.Clone(), true, false);
                    n++;
                }
                if (n > 0) Debug.Log("[JetForge] 设计同步：" + n + " 台同款发动机一起改成了新参数");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[JetForge] 设计同步失败（不影响这一台）：" + ex.Message);
            }
        }

        /// <summary>
        /// 把这一台的设计**无条件**推给同型的所有 JetForge 发动机。
        ///
        /// <see cref="PropagateDesign"/> 只碰"改动前和我一模一样"的那些，那是为了不抹掉玩家
        /// 故意做的两台不同参数。可代价是：一旦两台已经分叉（比如老版本存下的存档里一台是
        /// 初始设计），自动同步就再也够不着它——玩家看到的就是"进飞行后一台是原样"。
        /// 这个入口是给那种情况点名覆盖用的，不做任何猜测。
        /// </summary>
        public int SyncToSameType()
        {
            int n = 0;
            try
            {
                List<Part> all = null;
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null) all = EditorLogic.fetch.ship.parts;
                else if (vessel != null) all = vessel.parts;
                if (all == null || Design == null) return 0;
                string mine = part != null && part.partInfo != null ? part.partInfo.name : null;
                for (int i = 0; i < all.Count; i++)
                {
                    Part p = all[i];
                    if (p == null || p == part) continue;
                    if (mine != null && (p.partInfo == null || p.partInfo.name != mine)) continue;
                    ModuleJetForgeEngine m = p.FindModuleImplementing<ModuleJetForgeEngine>();
                    if (m == null) continue;
                    m.ApplyDesign(Design.Clone(), false, false);
                    n++;
                }
                if (n > 0) Debug.Log("[JetForge] 点名同步：" + n + " 台同型发动机改成了这一台的设计");
            }
            catch (Exception ex) { Debug.LogWarning("[JetForge] 点名同步失败：" + ex.Message); }
            return n;
        }

        /// <summary>设计器改完参数后回调（简单模式）。</summary>
        public void ApplySimpleDesign(SimpleDesign sd, bool rebuildGrid = true)
        {
            ApplySimpleDesign(sd, rebuildGrid, true);
        }

        public void ApplySimpleDesign(SimpleDesign sd, bool rebuildGrid, bool propagate)
        {
            if (sd == null) return;
            SimpleCfg = sd;
            UseSimple = true;
            GridDirty = true;
            _idleStarved = false;
            Consts = null;
            SimpleConsts = null;
            SimpleCfg.Normalize();
            SimplePoint = Simple.DesignPoint(SimpleCfg);
            SimpleConsts = Simple.DesignConstants(SimpleCfg);
            if (rebuildGrid) RebuildGrid();
            LastReport = EvaluateReport();
            // 简单模式的加力判定是"喷油量/排气温度"两条，没有 AutoLight 这个开关，
            // 配了加力就默认走自动档。
            abAuto = HasAfterburner();
            if (!HasAfterburner()) afterburnerLit = false;
            RefreshAbButton();
            // 简单模式没有几何细节，但风扇直径是玩家明确给的，外形照样按它生成
            JetForgeMeshBuilder.RefreshPartMesh(part, MeshDesignFromSimple(SimpleCfg));
            RefreshThrustTransforms(MeshDesignFromSimple(SimpleCfg));
            RefreshMass();
            AttachPlume();

            if (propagate)
            {
                // 简单模式的设计面很小（几个滑块），玩家几乎总是希望同款一致：
                // 直接按"和这一台现在一样"的指纹找人。
                PropagateSimple(SimpleCfg);
            }
        }

        private void PropagateSimple(SimpleDesign sd)
        {
            try
            {
                List<Part> all = null;
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null) all = EditorLogic.fetch.ship.parts;
                else if (vessel != null) all = vessel.parts;
                if (all == null || sd == null) return;
                string sig = DesignSignature.Of(sd);
                int n = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    Part p = all[i];
                    if (p == null || p == part) continue;
                    ModuleJetForgeEngine m = p.FindModuleImplementing<ModuleJetForgeEngine>();
                    if (m == null || !m.UseSimple || m.SimpleCfg == null) continue;
                    if (DesignSignature.Of(m.SimpleCfg) != sig) continue;
                    m.ApplySimpleDesign(sd.Clone(), true, false);
                    n++;
                }
                if (n > 0) Debug.Log("[JetForge] 设计同步（简单模式）：" + n + " 台同款一起改");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[JetForge] 简单模式设计同步失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 把简单模式的几个输入翻译成一个「够用的」 EngineDesignData，只用于生成外形：
        /// 风扇直径、核心机直径（由涵道比反推）、有没有加力、喷管型式。
        /// 级数按典型值给，外形上只影响涡轮段长度，不参与性能计算。
        /// </summary>
        public static EngineDesignData MeshDesignFromSimple(SimpleDesign sd)
        {
            var d = new EngineDesignData();
            d.Kind = sd.Kind;
            d.TechName = sd.Tech != null ? sd.Tech.Name : "1970s";
            d.DesignName = sd.Name;
            d.InletDiameter = sd.Diameter;
            d.Nozzle = sd.Nozzle;
            d.Fan.Diameter = sd.Diameter;
            d.Fan.Stages = sd.Kind == EngineKind.Turbofan ? 1 : 0;
            d.CoreInletDiameter = sd.Diameter / Math.Sqrt(1.0 + Math.Max(sd.Bpr, 0.0));
            d.Hpc.Stages = 8;
            d.Hpc.TipDiameter = d.CoreInletDiameter * 0.80;
            d.Hpt.Stages = 1;
            d.Hpt.TipDiameter = d.CoreInletDiameter * 0.88;
            d.Lpt.Stages = sd.Kind == EngineKind.Turbofan ? 3 : 2;
            d.Lpt.TipDiameter = d.CoreInletDiameter;
            d.Burner.ExitTemp = sd.T4;
            d.Afterburner.Enabled = sd.AbFuelRatio > 0.0 || sd.TExhaust > 0.0;
            return d;
        }

        public double FlightAltitude()
        {
            if (HighLogic.LoadedSceneIsEditor) return Design.DesignAlt;
            return part != null && part.vessel != null ? part.vessel.altitude : 0.0;
        }

        public double FlightMach()
        {
            if (HighLogic.LoadedSceneIsEditor) return Design.DesignMach;
            return part != null && part.vessel != null ? part.vessel.mach : 0.0;
        }
    }
}
