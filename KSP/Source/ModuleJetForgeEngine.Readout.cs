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
    /// 读数与状态：PAW 上的数、状态行、风车阻力、转速查表。
    ///
    /// 从 `ModuleJetForgeEngine.cs` 拆出来的。字段一个没动——`[KSPField]`
    /// 与反射读写的成员留在主文件里，这里只有方法。
    /// </summary>
    public partial class ModuleJetForgeEngine
    {
        public double DpThrust() { return UseSimple ? (SimplePoint != null ? SimplePoint.Thrust : 0.0) : (DesignPoint != null ? DesignPoint.Thrust : 0.0); }

        public double DpSfc() { return UseSimple ? (SimplePoint != null ? SimplePoint.Sfc : 0.0) : (DesignPoint != null ? DesignPoint.Sfc : 0.0); }

        public double DpOpr() { return UseSimple ? (SimplePoint != null ? SimplePoint.Opr : 0.0) : (DesignPoint != null ? DesignPoint.Opr : 0.0); }

        public double DpBpr() { return UseSimple ? (SimplePoint != null ? SimplePoint.Bpr : 0.0) : (DesignPoint != null ? DesignPoint.Bpr : 0.0); }

        public double DpMdot() { return UseSimple ? (SimplePoint != null ? SimplePoint.MdotAir : 0.0) : (DesignPoint != null ? DesignPoint.MdotAir : 0.0); }

        public double DpFuelFlow() { return UseSimple ? (SimplePoint != null ? SimplePoint.FuelFlow : 0.0) : (DesignPoint != null ? DesignPoint.FuelFlow : 0.0); }

        public double DpT5() { return UseSimple ? (SimplePoint != null ? SimplePoint.T5 : 0.0) : (DesignPoint != null ? DesignPoint.T5 : 0.0); }

        public double DpT7() { return UseSimple ? (SimplePoint != null ? SimplePoint.T7 : 0.0) : (DesignPoint != null ? DesignPoint.T7 : 0.0); }

        public double DpN1() { return UseSimple ? (SimplePoint != null ? SimplePoint.N1Pct : 0.0) : Spool("N1"); }

        public double DpN2() { return UseSimple ? (SimplePoint != null ? SimplePoint.N2Pct : 0.0) : Spool("N2"); }

        /// <summary>整条设计的第一句错误。给玩家看"为什么不出力"。</summary>
        public string FirstError()
        {
            if (UseSimple)
            {
                if (SimplePoint == null) return Loc.T("设计点没算出来", "design point not solved");
                if (SimplePoint.Errors.Count > 0) return SimplePoint.Errors[0];
            }
            else
            {
                if (DesignPoint == null) return Loc.T("设计点没算出来", "design point not solved");
                if (DesignPoint.Errors.Count > 0) return DesignPoint.Errors[0];
            }

            // 循环没错但判据判了 FAIL：把最严重的那条说出来，别让玩家对着
            // "设计不成立" 四个字猜是哪个参数。
            CheckReport rep = LastReport;
            if (rep != null && !rep.Ok)
            {
                string worst = "";
                double rank = -1.0;
                for (int i = 0; i < rep.Findings.Count; i++)
                {
                    Finding f = rep.Findings[i];
                    if (f == null || f.LevelValue != Level.Fail) continue;
                    double r = Math.Abs(f.MarginFrac);       // 越有余量越先报
                    if (r > rank) { rank = r; worst = f.Message; }
                }
                if (!string.IsNullOrEmpty(worst)) return worst;
            }
            return "";
        }

        /// <summary>更新状态读数。只在文字变了的时候动，免得每帧刷字符串。</summary>
        private void SetState(string s)
        {
            if (readState != s) readState = s;
        }

        /// <summary>迎风面积按外形算（π·R进²），跟网格是同一份几何，不另外估。</summary>
        private double WindmillArea()
        {
            return WindmillArea(_plumeDesign ?? Design);
        }

        /// <summary>同一份几何的静态版：自检要能单独算，不然它会去问一个还没挂上的零件要面积。</summary>
        internal static double WindmillArea(EngineDesignData fd)
        {
            if (fd == null) return 0.0;
            double rIn = JetForgeMeshBuilder.InletRadius(fd);
            return Math.PI * rIn * rIn;
        }

        /// <summary>
        /// 把风车阻力真的加到刚体上。这一条是"关掉发动机的飞机应该掉得更快"的唯一实现。
        ///
        /// 单位：KSP 里 Part.AddForce 吃的是**千牛**（Part.force 这个累加量是千牛，
        /// 原版 ModuleLiftingSurface 算出来的升阻力也是千牛直接 AddForceAtPosition）。
        /// 这里除 1000 是必须的，写错就是一千倍的阻力。
        /// </summary>
        private void ApplyWindmillDrag()
        {
            if (!_windmill.Valid || _windmill.DragN <= 1.0) return;
            if (part == null || vessel == null) return;

            Vector3d v = vessel.GetSrfVelocity();
            double speed = v.magnitude;
            if (speed < 1.0) return;   // 停在地上没有来流，也就没有冲压阻力

            Vector3d f = -v * (_windmill.DragN / 1000.0 / speed);
            part.AddForce(f);
        }

        /// <summary>加力是否开启（简单模式看喷油量/排气温度，残酷模式看设计里的开关）。</summary>
        public bool HasAfterburner()
        {
            return UseSimple
                ? (SimpleCfg != null && (SimpleCfg.AbFuelRatio > 0.0 || SimpleCfg.TExhaust > 0.0))
                : (Design != null && Design.Afterburner.Enabled);
        }

        private double Spool(string label)
        {
            if (DesignPoint == null || DesignPoint.Spool == null) return 0.0;
            double v;
            return DesignPoint.Spool.TryGetValue(label, out v) ? v : 0.0;
        }

        /// <summary>
        /// 物理转速 → 换算转速。θ 取当地 ISA 静温比，下限 0.6（再冷也不是这个模型的世界）。
        /// 求解器限的是换算转速，网格要是拿物理转速去查同一个函数，
        /// 高空高速时两边就各说各话——那正是"花瓣开度与喉道面积对不上"的来源。
        /// </summary>
        private void RefreshConvertedSpeed()
        {
            if (vessel == null) { _ncVis = 1.0; return; }
            double p, t;
            try { JetForge.Core.Gas.Isa(vessel.altitude, out p, out t); }
            catch (Exception) { _ncVis = 1.0; return; }
            double theta = t > 0.0 ? Math.Max(t / 288.15, 0.6) : 1.0;
            double n = (_lastNPct / 100.0) / Math.Sqrt(theta);
            if (double.IsNaN(n) || double.IsInfinity(n) || n < 0.0) n = 1.0;
            _ncVis = n;
        }
    }
}
