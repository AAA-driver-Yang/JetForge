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
    /// 原版桥：推力/耗油写回原版、比冲曲线、准备状态。
    ///
    /// 从 `ModuleJetForgeEngine.cs` 拆出来的。字段一个没动——`[KSPField]`
    /// 与反射读写的成员留在主文件里，这里只有方法。
    /// </summary>
    public partial class ModuleJetForgeEngine
    {
        private void PruneStockPaw()
        {
            string[] kill = StockPawKeys;
            if (Fields == null) return;
            for (int i = 0; i < kill.Length; i++)
            {
                // BaseFieldList 没有 ContainsKey，取不到会是 null——所以判 null 就够。
                BaseField f = Fields[kill[i]];
                if (f == null) continue;
                f.guiActive = false;
                f.guiActiveEditor = false;
            }
        }

        /// <summary>设计、常数、网格这一串依赖关系，一处算错全盘皆错，所以收在一个入口里。</summary>
        public void EnsureReady()
        {
            RefreshIntake();
            if (UseSimple)
            {
                if (SimpleCfg == null) SimpleCfg = new SimpleDesign();
                SimpleCfg.Normalize();
                if (SimpleConsts == null || GridDirty)
                {
                    SimplePoint = Simple.DesignPoint(SimpleCfg);
                    SimpleConsts = Simple.DesignConstants(SimpleCfg);
                    Grid = null;
                    GridDirty = SimpleConsts == null || !SimpleConsts.Ok;
                }
            }
            else
            {
                if (Consts == null || GridDirty)
                {
                    RefreshIntake();
                    DesignPoint = Cycle.SolveDesignPoint(Design, _install);
                    Consts = OffDesign.DeriveDesignConstants(Design, _install);
                    Grid = null;
                    GridDirty = Consts == null || !Consts.Ok;
                }
            }

            if ((Grid == null || Sampler == null) && autoRebuild) RebuildGrid();
            // 判据必须在这里评一次：飞行中没人调 EvaluateReport，LastReport 空着，
            // DesignOk() 就会退回"只看循环闭不闭合"——那正是"不合理的设计照样能飞"。
            try { LastReport = EvaluateReport(); }
            catch (Exception ex) { Debug.LogError("[JetForge] 判据失败：" + ex.Message); }
            RefreshMass();
            SyncSpeedFields();
            if (part != null)
            {
                // 存档里的船载入时也要按存下来的设计生成外形——不然玩家看到的是
                // cfg 里那个占位模型（stock turboFanSize1），跟设计器里画的完全不是一台机器。
                RefreshPartMeshIfNeeded();
                AttachPlume();
                AttachSound();
            }
        }

        /// <summary>
        /// 按轴数决定显示几条转速。单转子涡喷（J79/J85）只有 N1，显示一个 "N2 0.0%"
        /// 只会让玩家以为发动机坏了；双轴机没有 N3；三轴机才三条都给。
        /// </summary>
        private void SyncSpeedFields()
        {
            int shafts = 1;
            try
            {
                shafts = UseSimple ? 1 : Math.Max(Design.ShaftCount, 1);
                if (UseSimple && SimpleCfg != null)
                    shafts = SimpleCfg.Kind == EngineKind.Turbofan ? 2 : 1;
            }
            catch { shafts = 1; }

            BaseField f1 = Fields["readN1"];
            BaseField f2 = Fields["readN2"];
            BaseField f3 = Fields["readN3"];
            if (f1 != null) { f1.guiActive = true; f1.guiActiveEditor = true; }
            if (f2 != null) { f2.guiActive = shafts >= 2; f2.guiActiveEditor = shafts >= 2; }
            if (f3 != null) { f3.guiActive = shafts >= 3; f3.guiActiveEditor = false; }
            if (shafts < 2) { readN2 = 0f; readN3 = 0f; }
            else if (shafts < 3) readN3 = 0f;
        }

        /// <summary>
        /// 把"我们要的推力 + 燃油流量"翻译成原版的 maxFuelFlow 与 Isp。
        /// 原版在飞行中每帧重算 finalThrust = maxFuelFlow×油门×Isp×g，
        /// 我们写的 finalThrust 活不过一帧（反汇编 ThrustUpdate 得到的确证）。
        /// </summary>
        private void ApplyStockBridge(double thrustKN, double fuelTps, double throttle)
        {
            FuelBridge b = StockBridge.Compute(thrustKN, fuelTps, throttle);
            maxFuelFlow = (float)b.MaxFuelFlow;
            minFuelFlow = 0f;
            multFlow = 1f;
            multIsp = 1f;
            g = (float)Gas.G0;
            useAtmCurve = false;
            useAtmCurveIsp = false;
            useThrottleIspCurve = false;
            // ModifyFlow() 的 IL 告诉我们它头一句就读 atmChangeFlow：为 true 时会按静压缩放流量，
            // 和我们自己的换算流量叠成两重补偿。实测证明它默认返回 1 之前，先关掉。
            atmChangeFlow = false;
            // 慢车。原版 RequiredPropellantMass 是 requestedMassFlow = maxFuelFlow × 油门，
            // 没有下限钳制：油门 0 就是零流量、零推力。对火箭没错，对喷气机错了——
            // 真机油门 0 是慢车（战斗机涡扇 8~13%），关了才是关车。用原版自带的
            // throttleUseAlternate 重映射到 [throttleMin, 1]，慢车推力才真的存在。
            throttleUseAlternate = true;
            throttleMin = (float)StockBridge.InputFloor;
            throttleInstant = true;            // 滞后由 Core/Spool.cs 负责，别叠原版那一层
            throttleInstantShutdown = false;
            SetIspCurve(b.Isp);
            realIsp = (float)b.Isp;
        }

        /// <summary>
        /// 把 Isp 曲线压平成一条水平线——原版只看 atmosphereCurve.Evaluate(静压)，
        /// 而我们的 Isp 随高度、油门、马赫都在变，所以每帧把当前值写进去。
        /// </summary>
        private void SetIspCurve(double isp)
        {
            if (double.IsNaN(isp) || isp < 1.0) isp = 1.0;
            float v = (float)isp;
            // 变化小于 0.1% 就不重建曲线，省掉每帧新建 FloatCurve 的开销。
            // （KSP 用的 Unity 版本里 AnimationCurve 没有 SetKeys，只能整条重造。）
            if (_ispCurve != null && Math.Abs(_ispLast - v) < 0.001f * Math.Max(1f, Math.Abs(v))) return;
            _ispCurve = new FloatCurve();
            _ispCurve.Add(0f, v);
            _ispCurve.Add(2f, v);   // 压平成水平线：原版按静压取值，取到哪都是这个 Isp
            atmosphereCurve = _ispCurve;
            _ispLast = v;
        }

        /// <summary>把模型读数回写给原版字段，让 VAB 里的推重比、UI 里的推力都对得上。</summary>
        private void ApplyToStock()
        {
            if (!DesignOk()) return;

            double dpThrust = DpThrust();
            // maxThrust 是千牛，DpThrust() 是牛顿
            maxThrust = (float)(dpThrust > 0 ? dpThrust / 1000.0 : 1.0);
            minThrust = 0f;

            // 设计点的油耗：按我们的模型算出来的燃油流量直接喂给原版。
            ApplyStockBridge(dpThrust / 1000.0, DpFuelFlow() / 1000.0, 1.0);

            readOpr = (float)DpOpr();
            readBpr = (float)DpBpr();
            readThrust = (float)(dpThrust / 1000.0);
            readSfc = (float)DpSfc();
            readMassFlow = (float)DpMdot();
            readFuelFlow = (float)DpFuelFlow();
        }

        public bool DesignOk()
        {
            bool cycleOk = UseSimple ? (SimplePoint != null && SimplePoint.Ok)
                                     : (DesignPoint != null && DesignPoint.Ok);
            if (!cycleOk) return false;

            // 循环能收敛，不等于"这台机器说得通"。判据里的 FAIL 就是"设计不成立"的同义词
            // （级数装不下这个温降、流道装不下这个流量、轴带不动自己那级压气机……）。
            // 以前只有循环闭合这一道闸，于是判据 FAIL 的机器照样能飞，面板一切正常，
            // N1/N2 还可能是 0 —— 玩家看到的就是"明明不合理，它却在工作"。
            CheckReport rep = LastReport;
            if (rep == null) return true;
            return rep.Ok;
        }

        /// <summary>
        /// 循环本身闭不闭合（不看判据）。
        ///
        /// 这两个问题的答案要分开：循环不闭合是"算不出这台机器的工况"，
        /// 判据不成立是"算得出来，但这台机器有毛病"。前者只能停，后者该降额照飞。
        /// 以前合成一个布尔，于是配错一个参数的机器连点火都试不了。
        /// </summary>
        public bool SolverOk()
        {
            return UseSimple ? (SimplePoint != null && SimplePoint.Ok)
                             : (DesignPoint != null && DesignPoint.Ok);
        }
    }
}
