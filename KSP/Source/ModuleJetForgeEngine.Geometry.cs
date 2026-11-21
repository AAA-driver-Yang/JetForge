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
    /// 外形与装配：网格重建、推力节点、质量与成本、进气道对接。
    ///
    /// 从 `ModuleJetForgeEngine.cs` 拆出来的。字段一个没动——`[KSPField]`
    /// 与反射读写的成员留在主文件里，这里只有方法。
    /// </summary>
    public partial class ModuleJetForgeEngine
    {
        /// <summary>
        /// 按当前设计重建外形。外形只在设计变了的时候才需要重建，
        /// 但"载入存档"这种情况没有 ApplyDesign 回调，得在这里补。
        /// </summary>
        private void RefreshPartMeshIfNeeded()
        {
            if (part == null) return;
            try
            {
                EngineDesignData md = UseSimple ? MeshDesignFromSimple(SimpleCfg) : Design;
                if (md == null) return;
                // 外形是"按某一套设计"建出来的，所以判据必须是"这个外形是不是当前这套设计的"，
                // 不能只查"有没有那个节点"。节点在、内容却是另一套（对称复制、读档、同型多台）
                // 时，只查节点会让玩家看到"另一台的外形"或者干脆是 cfg 里的默认占位模型。
                // 比的是**几何指纹**：改压比、效率、涡轮前温度不该让网格重建一遍（顶点一个不变），
                // 那是性能层的事。整份设计的指纹留给缓存键与判据用。
                string sig = null;
                try { sig = RefreshStamps.EngineGeometry(md); } catch { sig = null; }
                if (_meshSig != sig || part.FindModelTransform(JetForgeMeshBuilder.VisualObjectName) == null)
                {
                    JetForgeMeshBuilder.RefreshPartMesh(part, md);
                    _meshSig = sig;
                }
                RefreshThrustTransforms(md);
            }
            catch (Exception ex)
            {
                Debug.LogError("[JetForge] 生成外形失败：" + ex);
            }
        }

        /// <summary>
        /// 把原版施力用的 thrustTransforms 换成我们自己的那一个。
        ///
        /// 原版 ThrustUpdate 里施力是：
        ///     part.AddForceAtPosition(-t.forward * finalThrust * mult[i], t.position)
        /// 而 thrustTransforms 是 **ModuleEngines.OnLoad 时** 用 part.FindModelTransforms
        /// 从零件模型上抓的——那一刻零件上只有 cfg 里那个占位模型（stock turboFanSize1），
        /// 抓到的是它的喷口。等我们把外形换成自己生成的，施力点还留在占位模型的位置：
        /// 长发动机的推力点会飘到机身里面，短发动机的会飘到喷口外面。
        /// 数字一直是对的，错的是力作用在哪里。
        /// </summary>
        private void RefreshThrustTransforms(EngineDesignData md)
        {
            Transform t = JetForgeMeshBuilder.EnsureThrustTransform(part, md);
            if (t == null) return;
            List<Transform> ts = new List<Transform>();
            ts.Add(t);
            thrustTransforms = ts;
            // 两个列表必须一样长：原版按 i 同时索引，短的会越界。
            List<float> ms = new List<float>();
            ms.Add(1f);
            thrustTransformMultipliers = ms;
        }

        /// <summary>
        /// 没接进气道。发射台上不算（还没插到船体上、VAB 里也不该天天报警）。
        /// 飞行中返回 true 时发动机按"不吸气"处理：点不着、也烧不出推力。
        /// </summary>
        public bool IntakeMissingNow()
        {
            if (part == null) return false;
            if (HighLogic.LoadedSceneIsEditor) return !EditorIntakeNearby();
            if (vessel == null) return false;
            if (HighLogic.LoadedSceneIsFlight && vessel.situation == Vessel.Situations.PRELAUNCH) return false;
            return _install == null;
        }

        /// <summary>
        /// 编辑器里有没有进气道：VAB 里零件还没落到船上，`IntakeLink` 那套"同船零件列表"
        /// 对着一堆待装件是判不准的，所以这里只问"整艘船的零件列表里有没有进气道"。
        /// 30 帧算一次：`GetInfo` 一开面板就每帧调，扫零件列表不该每帧付。
        /// </summary>
        private bool EditorIntakeNearby()
        {
            if (_editorIntake.HasValue && Time.frameCount - _editorIntakeFrame < 30) return _editorIntake.Value;
            _editorIntakeFrame = Time.frameCount;
            bool found = false;
            try
            {
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.Parts != null)
                {
                    List<Part> ps = EditorLogic.fetch.ship.Parts;
                    for (int i = 0; i < ps.Count && !found; i++)
                    {
                        Part p = ps[i];
                        if (p == null) continue;
                        if (p.FindModuleImplementing<ModuleJetForgeIntake>() != null) { found = true; break; }
                        if (p.Modules != null)
                            for (int k = 0; k < p.Modules.Count; k++)
                                if (p.Modules[k] is ModuleResourceIntake) { found = true; break; }
                    }
                }
            }
            catch { found = false; }
            _editorIntake = found;
            return found;
        }

        /// <summary>
        /// 重新扫一遍附近的进气道零件。
        /// 编辑器里按**设计点**工况算（面板上显示的就是"设计这东西时它有多少气"），
        /// 飞行里按当前高度马赫算（每帧都在变，所以这张表每帧重算一次——两件进气道、
        /// 一次 SolvePart 是微秒级的，比缓存失效判断还便宜）。
        /// </summary>
        public IntakeInstall RefreshIntake()
        {
            if (part == null) { _install = null; _installStamp = null; return null; }

            double alt, mach;
            if (HighLogic.LoadedSceneIsFlight && vessel != null && vessel.situation != Vessel.Situations.PRELAUNCH)
            {
                alt = vessel.altitude;
                mach = vessel.mach;
            }
            else
            {
                alt = Design != null ? Design.DesignAlt : 0.0;
                mach = Design != null ? Design.DesignMach : 0.0;
            }

            _installStamp = IntakeLink.Stamp(part);
            IntakeInstall ins = IntakeLink.For(part, Design != null ? Design.Tech : TechLevel.Get("1970s"), alt, mach);
            _install = ins != null && ins.Present ? ins : null;
            return _install;
        }

        /// <summary>
        /// 装/拆/改进气道之后要让网格重算。用指纹比而不是每帧重建：
        /// 飞行中每帧扫船是几千个零件的遍历，只该在几十帧一次。
        /// </summary>
        private bool CheckIntakeStamp()
        {
            if (part == null || part.vessel == null) return false;
            if (Time.frameCount % 30 != 0) return false;
            string now = IntakeLink.Stamp(part);
            if (now != _installStamp)
            {
                _installStamp = now;
                GridDirty = true;
                return true;
            }
            return false;
        }

        public void RebuildGrid()
        {
            if (UseSimple)
            {
                if (SimpleConsts == null || !SimpleConsts.Ok) return;
                // 简单模式仍用预解网格：它只有一个"填参数出曲线"的循环，
                // 没有喘振边缘、没有叶尖过音速这些"格点之间"的东西要看。
                // 走缓存：一艘船上 4 台同型号发动机载入时只该建一张表（0.2 s，不是 0.8 s）。
                Grid = GridFactory.BuildSimpleCached(SimpleCfg, SimpleConsts, 0.0);
                GridAb = HasAfterburner()
                    ? GridFactory.BuildSimpleCached(SimpleCfg, SimpleConsts, 1.0)
                    : null;
                Sampler = new GridSampler(Grid, GridAb);
            }
            else if (UseRealtime)
            {
                if (Consts == null || !Consts.Ok) return;
                // 残酷模式改现算：网格把"喘振边缘、加力刚点着那半秒、叶尖刚过音速"
                // 全抹成了格点之间的直线，而那几处正是玩家想看的地方。
                // 代价是每秒十几次 Solve，所以频率可调（RealtimeHz）。
                RealtimeSampler rs = new RealtimeSampler(Design, Consts, 24, _install);
                rs.Hz = RealtimeHz;
                Sampler = rs;
                // 网格不再建：省下 0.2 s 载入时间，也免得两套数在状态栏里打架。
                Grid = null;
                GridAb = null;
            }
            else
            {
                if (Consts == null || !Consts.Ok) return;
                Grid = GridFactory.BuildCached(Design, Consts, 0.0, _install);
                GridAb = HasAfterburner() ? GridFactory.BuildCached(Design, Consts, 1.0, _install) : null;
                Sampler = new GridSampler(Grid, GridAb);
            }
            GridDirty = false;
            ApplyToStock();
        }

        /// <summary>
        /// 重算质量、造价，并按新的长度把上下对接点摆回去。
        /// 外形变了节点不跟着动，是 KSP 原版零件的老毛病——程序化外形必须自己维护节点，
        /// 否则发动机变长了，玩家会发现"看起来变长，实际上还接在原来位置"。
        /// </summary>
        public void RefreshMass()
        {
            EngineDesignData src = UseSimple ? MeshDesignFromSimple(SimpleCfg) : Design;
            Mass = MassCost.Estimate(src);
            readMass = (float)(Mass.TotalDry / 1000.0);
            readTwr = (float)Mass.ThrustToWeight;
            readCost = (float)Mass.Cost;
            UpdateAttachNodes(src);
            if (part != null)
            {
                try { part.UpdateMass(); }
                catch (Exception e) { Debug.LogWarning("[JetForge] 质量没更新成，面板上的重量还是旧的：" + e.Message); }
            }
        }

        private void UpdateAttachNodes(EngineDesignData d)
        {
            if (part == null || part.attachNodes == null || d == null) return;
            float half = (float)(Math.Max(d.EffectiveLength, 0.4) * 0.5);
            for (int i = 0; i < part.attachNodes.Count; i++)
            {
                AttachNode n = part.attachNodes[i];
                if (n == null || n.id == null) continue;
                Vector3 p = n.position;
                if (n.id == "top") p.y = half;
                else if (n.id == "bottom") p.y = -half;
                else continue;
                n.position = p;
            }
        }

        // ---------- IPartMassModifier / IPartCostModifier ----------
        // 这两个接口返回的是**增量**不是总量：Part.UpdateMass 会把返回值**加到** cfg 里的基础质量上。
        // 实测证据：cfg 1.400 t + 返回 1.477 = 2.877 t，正是最初的错误。
        // 所以这里一律返回 "模型算出来的值 − 默认值"。
        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit)
        {
            if (Mass == null) RefreshMass();
            if (Mass == null || Mass.TotalDry <= 1.0) return 0f;
            return (float)(Mass.TotalDry / 1000.0 - defaultMass);
        }

        public ModifierChangeWhen GetModuleMassChangeWhen() { return ModifierChangeWhen.FIXED; }

        public float GetModuleCost(float defaultCost, ModifierStagingSituation sit)
        {
            if (Mass == null) RefreshMass();
            if (Mass == null || Mass.Cost <= 0.0) return 0f;
            return (float)(Mass.Cost - defaultCost);
        }

        public ModifierChangeWhen GetModuleCostChangeWhen() { return ModifierChangeWhen.FIXED; }
    }
}
