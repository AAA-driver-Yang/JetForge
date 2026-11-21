using System;
using System.Collections.Generic;
using System.Linq;

namespace JetForge.Core
{
    /// <summary>
    /// 轴系判据：功率平衡、转速范围。
    ///
    /// 从 `Criteria.cs` 拆出来的：判据一共十七组，全挤在一个文件里时
    /// 温度在哪、轴在哪、堵塞在哪只能靠滚；拆开之后一族一个文件，
    /// 加一条判据只需要看那一族的前后文。
    /// </summary>
    public static partial class Criteria
    {
        public static List<Finding> CheckShafts(EngineDesignData design, ResultView v)
        {
            List<Finding> o = new List<Finding>();
            if (v.Num.Count == 0 && v.Stages.Count == 0) return o;
            if (!v.HasTurbineSplit)
            {
                o.Add(new Finding("shafts.skipped", Level.Ok,
                    Loc.T("非设计点结果未给逐级功分量，轴平衡判据已跳过", "Off-design reported no per-stage work, so the shaft balance check was skipped")));
                return o;
            }
            HashSet<int> shafts = new HashSet<int>();
            if (design.HasFan) shafts.Add(design.Fan.Shaft);
            if (design.Lpc.Stages > 0) shafts.Add(design.Lpc.Shaft);
            if (design.Hpc.Stages > 0) shafts.Add(design.Hpc.Shaft);
            if (design.Hpt.Stages > 0) shafts.Add(design.Hpt.Shaft);
            if (design.Ipt.Stages > 0) shafts.Add(design.Ipt.Shaft);
            if (design.Lpt.Stages > 0) shafts.Add(design.Lpt.Shaft);

            foreach (int s in shafts.OrderBy(x => x))
            {
                double need = 0.0, have = 0.0;
                if (design.HasFan && design.Fan.Shaft == s) need += v.Get("W_fan");
                if (design.Lpc.Stages > 0 && design.Lpc.Shaft == s) need += Share(design, v, "lpc");
                if (design.Hpc.Stages > 0 && design.Hpc.Shaft == s) need += Share(design, v, "hpc");
                if (design.Hpt.Stages > 0 && design.Hpt.Shaft == s) have += v.Get("W_turb_hpt");
                if (design.Ipt.Stages > 0 && design.Ipt.Shaft == s) have += v.Get("W_turb_ipt");
                if (design.Lpt.Stages > 0 && design.Lpt.Shaft == s) have += v.Get("W_turb_lpt");
                if (need > 1.0 || have > 1.0)
                {
                    string lvl = have >= need * 0.98 ? Level.Ok : Level.Fail;
                    o.Add(new Finding(string.Format("shaft{0}.balance", s), lvl,
                        string.Format(Loc.T("轴 {0}：涡轮 {1:F2} MW vs 负载 {2:F2} MW", "Shaft {0}: turbine {1:F2} MW vs load {2:F2} MW"), s, have / 1e6, need / 1e6),
                        have / 1e6, need / 1e6, "MW"));
                }
            }
            return o;
        }

        /// <summary>
        /// 齿轮箱柔度（`Arch/Torsion.cs` 一阶模型）。直驱机型没有这个部件，不给任何结论。
        ///
        /// 门槛怎么定的：扭转角 0.10° 是"叶片跟着指令慢半拍能被感觉出来"的量级，
        /// 本体扭振低于 300 Hz 说明箱体又大又薄——这两条都不是失效判据，
        /// 是"这台机器在纸面上有个软处"的提醒，所以最高只到 Warn。
        /// </summary>
        public static List<Finding> CheckTorsion(EngineDesignData design, ResultView v)
        {
            List<Finding> o = new List<Finding>();
            if (design == null || design.Fan == null) return o;
            if (!(design.Fan.GearRatio > 1.0001)) return o;
            if (v.Get("gearbox_present") < 0.5) return o;

            double twist = v.Get("gearbox_twist");
            double freq = v.Get("gearbox_freq");
            double torque = v.Get("gearbox_torque");
            if (twist > 0.10)
                o.Add(new Finding("gearbox.soft", Level.Warn,
                    string.Format(Loc.T("齿轮箱在 {0:F0} kN·m 下扭 {1:F2}°：风扇响应会滞后于油门",
                        "Gearbox twists {1:F2} deg at {0:F0} kN m: the fan lags the throttle"), torque / 1000.0, twist),
                    twist, 0.10, "°"));
            else
                o.Add(new Finding("gearbox.twist", Level.Ok,
                    string.Format(Loc.T("齿轮箱扭 {0:F3}°（{1:F0} kN·m 下）", "Gearbox twists {0:F3} deg ({1:F0} kN m)"), twist, torque / 1000.0),
                    twist, 0.10, "°"));

            if (freq > 1.0 && freq < 300.0)
                o.Add(new Finding("gearbox.mode", Level.Warn,
                    string.Format(Loc.T("齿轮箱本体扭振只有 {0:F0} Hz：箱体偏薄，齿频容易撞上它", "Gearbox body torsion is only {0:F0} Hz: thin housing, gear mesh can drive it"), freq),
                    freq, 300.0, "Hz"));
            return o;
        }
    }
}
