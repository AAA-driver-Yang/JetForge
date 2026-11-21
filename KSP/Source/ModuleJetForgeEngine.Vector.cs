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
    /// 矢量喷口：逐帧把指令变成作用力矩。
    ///
    /// 从 `ModuleJetForgeEngine.cs` 拆出来的。字段一个没动——`[KSPField]`
    /// 与反射读写的成员留在主文件里，这里只有方法。
    /// </summary>
    public partial class ModuleJetForgeEngine
    {
        private void UpdateVectoring(double dt, double throttle, double abFrac)
        {
            EngineDesignData d = _plumeDesign != null ? _plumeDesign : Design;
            if (d == null) return;

            // 开度不再拍公式：加力那一份按真 A8 的 √ 比例（面积 ∝ 半径²），
            // 干态那一份吃 `NozzleSchedule.DryFactor` —— 与求解器同一个函数。
            //
            // 传进去的必须是**换算转速**而不是物理转速：求解器限的是 N/√θ，
            // 高空高速时物理转速本来就高于换算转速（来流总温高），
            // 拿物理转速查干态开度，花瓣会比喉道实际开得更大。
            RefreshConvertedSpeed();
            float rExitNow = JetForgeMeshBuilder.NozzleExitRadius(d);
            double openTarget = JetForge.Core.NozzleVisual.OpenFor(
                rExitNow, ConvertedSpeedNow, abFrac, d.Nozzle == NozzleKind.ConvergingDiverging,
                d.Nozzle != NozzleKind.Converging, d.PetalLengthFrac);
            float open = (float)_lagPetal.Step(openTarget, dt, PetalTau);
            if (_petalOpen < 0f) _petalOpen = open;

            float maxDeg = (float)Math.Max(0.0, Math.Min(25.0, d.GimbalMaxDeg));
            bool canVector = d.ThrustVectoring && d.Nozzle != NozzleKind.Converging
                             && EngineIgnited && !flameout && vessel != null && maxDeg > 0.05f;

            float cmdP = 0f, cmdY = 0f, cmdR = 0f;
            if (canVector)
            {
                FlightCtrlState cs = vessel.ctrlState;
                cmdP = cs.pitch;
                cmdY = cs.yaw;
                cmdR = cs.roll;
            }
            // 死区 + 轴开关 + 限速率：SAS 稳态下 ctrlState 在 ±0.02 抖，
            // 以前这个抖直接当偏航指令使，花瓣每帧来回颤——玩家报的"偏航抖动"。
            _vecCmd.Step(cmdP, cmdY, cmdR, dt, d.VectorPitch, d.VectorYaw, d.VectorRoll);

            Transform t = (thrustTransforms != null && thrustTransforms.Count > 0) ? thrustTransforms[0] : null;
            if (t == null) return;

            // 未偏转时的推力方向：喷口在 -Y，所以推力朝零件 +Y。
            Vec3 axis = ToVec(part.transform.up);
            Vec3 r = vessel != null ? ToVec(t.position - vessel.CoM) : Vec3.Zero;
            Vec3 pAxis = vessel != null ? ToVec(vessel.ReferenceTransform.right) : new Vec3(1.0, 0.0, 0.0);
            Vec3 yAxis = vessel != null ? ToVec(vessel.ReferenceTransform.up) : new Vec3(0.0, 1.0, 0.0);

            double angleDeg;
            Vec3 dir = ThrustVector.Direction(axis, r, pAxis, yAxis,
                _vecCmd.Pitch, _vecCmd.Yaw, _vecCmd.Roll, true, true, true, maxDeg, out angleDeg);
            _vecAngleDeg = angleDeg;
            // 偏了不等于转对了：三根轴重合的装机上气流掰过去了，换到的却是绕另一根轴的力矩。
            // 这个数只为状态栏那一句话存在，不参与施力。
            Vec3 goalTau = (pAxis * (-_vecCmd.Pitch) + yAxis * (-_vecCmd.Yaw) + axis * _vecCmd.Roll).Normalized;
            double alignNow = ThrustVector.TorqueAlign(r, axis, dir, goalTau);
            Vector3 fdir = ToV3(dir);
            JetForgeMeshBuilder.SetThrustDirection(t, fdir);

            // 花瓣的角度从"力在世界里的方向"反推零件坐标里的两个角，
            // 这样图和力不可能各说各话——上一版两处各算一遍，正负号对不上过。
            Vector3 lf = part.transform.InverseTransformDirection(fdir);
            double lx = Math.Max(-1.0, Math.Min(1.0, lf.x));
            double lz = Math.Max(-1.0, Math.Min(1.0, lf.z));
            const double Rad2Deg = 57.29577951308232;
            float pDeg = (float)(-Math.Asin(lz) * Rad2Deg);
            float yDeg = (float)(Math.Asin(lx) * Rad2Deg);
            _vecPitch = (float)_lagVecP.Step(pDeg, dt, VectorTau);
            _vecYaw = (float)_lagVecY.Step(yDeg, dt, VectorTau);

            JetForgeMeshBuilder.UpdatePetals(part, d, _petalOpen, _vecPitch, _vecYaw);

            if (!d.ThrustVectoring) readVector = Loc.T("固定", "fixed");
            else if (d.Nozzle == NozzleKind.Converging) readVector = Loc.T("收敛喷管不可偏", "converging nozzle cannot vector");
            else if (!d.VectorPitch && !d.VectorYaw && !d.VectorRoll) readVector = Loc.T("三轴都关", "all three axes off");
            else if (!canVector) readVector = Loc.T("待机", "idle");
            else
            {
                // 滚转这一轴要单独说一句：喷口在质心轴线上时它就没力臂，
                // 玩家打开开关看到"没反应"不能算 bug，得当场讲清楚。
                bool rollLive = d.VectorRoll && (r - axis * Vec3.Dot(r, axis)).Length > 0.02;
                bool noLever = _vecAngleDeg > 0.05 && alignNow < 0.5;
                readVector = string.Format("{0:F1}° / {1:F1}°{2}{3}{4}",
                    _vecPitch, _vecYaw,
                    d.VectorRoll ? (rollLive ? Loc.T("｜滚转", " | roll") : Loc.T("｜滚转无力臂", " | roll has no lever arm")) : "",
                    _vecAngleDeg < 0.05 ? Loc.T("（指令在死区内）", " (command inside deadband)") : "",
                    noLever ? Loc.T("｜使劲的方向不对", " | wrong torque axis") : "");
            }
        }

        private static Vec3 ToVec(Vector3 v) { return new Vec3(v.x, v.y, v.z); }

        private static Vector3 ToV3(Vec3 v) { return new Vector3((float)v.X, (float)v.Y, (float)v.Z); }
    }
}
