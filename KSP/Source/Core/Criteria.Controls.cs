using System;
using System.Collections.Generic;
using System.Linq;

namespace JetForge.Core
{
    /// <summary>
    /// 控制与过渡态判据：油门响应、矢量、再点火、持续工作。
    ///
    /// 从 `Criteria.cs` 拆出来的：判据一共十七组，全挤在一个文件里时
    /// 温度在哪、轴在哪、堵塞在哪只能靠滚；拆开之后一族一个文件，
    /// 加一条判据只需要看那一族的前后文。
    /// </summary>
    public static partial class Criteria
    {
        /// <summary>
        /// 控制系统：年代上做不做得出来，以及"无控却指望它自己别喘"。
        ///
        /// 这不是风格问题——无控的机器油门就是油量，猛推就是会喘、超温就是会烧，
        /// 玩家抱怨"这引擎不听话"的时候，往往就是设计里选了无控。
        /// </summary>
        public static List<Finding> CheckControl(EngineDesignData d)
        {
            List<Finding> o = new List<Finding>();
            if (d == null) return o;

            ControlKind max = ControlSystem.Allowed(d.Tech);
            // 比的是"能力高低"不是枚举数值：Mechanical=3、Analog=4 是后加的成员，
            // 数值顺序（3 > 1）与能力顺序（机械 < 液压）不一致，直接比大小会把 1950s 的
            // 机械调速器判成"超出年代上限"。
            if (ControlSystem.Rank(d.Control) > ControlSystem.Rank(max))
            {
                o.Add(new Finding("control.era", Level.Warn, string.Format(
                    Loc.T("{0} 造不出「{1}」，只能做「{2}」：设计里选高的也不会报错，运行时按年代上限生效", "{0} could not build 「{1}」, only 「{2}」: picking the higher one raises no error, but at runtime the era cap applies"),
                    d.TechName, ControlSystem.KindName(d.Control), ControlSystem.KindName(max))));
            }

            if (d.Control == ControlKind.None)
            {
                // 无控的机器只有一个闸：飞行员的手。
                o.Add(new Finding("control.none", Level.Ok,
                    Loc.T("无控：油门直接给油，没有限温/限转/加速度限幅。猛推就喘、超温就烧，这是年代特征不是 bug", "No control: the throttle feeds fuel directly, with no temperature, speed or acceleration limiting. Slam it and it surges, overtemp it and it burns. That is the era, not a bug")));
            }
            else if (d.Control == ControlKind.Mechanical)
            {
                o.Add(new Finding("control.mech", Level.Ok,
                    Loc.T("纯机械调速器：离心配重压转速（98.5%），0.40/s 加速度限幅；没有热电偶反馈，所以不限温——超温只反映在涡轮寿命上", "Pure mechanical governor: flyweights hold 98.5%, acceleration limited to 0.40/s; no thermocouple feedback so no temperature limiting, and overtemp only shows up in turbine life")));
            }
            else if (d.Control == ControlKind.Hydro)
            {
                if (d.Afterburner != null && d.Afterburner.Enabled && !d.Afterburner.AutoLight)
                    o.Add(new Finding("control.hydro_ab", Level.Warn,
                        Loc.T("机械液压 + 手动加力：液压系统不认加力段的温度，靠飞行员记着别在低空长时间开加力", "Hydromechanical with manual afterburner: the hydraulics ignore afterburner temperature; the pilot has to remember not to hold it low and slow")));
                o.Add(new Finding("control.hydro", Level.Ok,
                    Loc.T("机械液压：有限温、限转与 0.90/s 的加速度限幅；不认识喘振裕度，猛推仍会喘", "Hydromechanical: temperature and speed limiting, 0.90/s acceleration limit; blind to surge margin, so a hard slam still surges")));
            }
            else if (d.Control == ControlKind.Analog)
            {
                o.Add(new Finding("control.analog", Level.Ok,
                    Loc.T("模拟式 FADEC：限温（0.5% 余地）、限转（99.5%）、2.20/s 加速度限幅；算不出喘振裕度，也不会超转切油", "Analog FADEC: temperature limiting (0.5% margin), speed 99.5%, 2.20/s acceleration limit; it cannot compute surge margin and will not cut fuel on overspeed")));
            }
            else
            {
                o.Add(new Finding("control.fadec", Level.Ok,
                    Loc.T("全权限数字：限温（0.5% 余地）、限转（100%）、2.20/s 加速度限幅，喘振裕度见底时主动压油门", "Full-authority digital: temperature limiting (0.5% margin), speed 100%, 2.20/s acceleration limit, and it backs the throttle off when surge margin bottoms out")));
            }
            return o;
        }

        /// <summary>
        /// 矢量喷口的两条：构型上能不能偏、偏多少。
        ///
        /// 收敛喷管只有一圈固定的锥壳，没有可动段——真机上装矢量的都是可变/收扩喷管。
        /// 所以"矢量 + 收敛"不是警告，是构型矛盾，直接判 FAIL。
        /// </summary>
        public static List<Finding> CheckVectoring(EngineDesignData d)
        {
            List<Finding> o = new List<Finding>();
            if (d == null || !d.ThrustVectoring)
            {
                o.Add(new Finding("nozzle.vector_off", Level.Ok, Loc.T("喷口固定，推力始终沿机身轴线", "Fixed nozzle: thrust always stays on the hull axis")));
                return o;
            }
            if (d.Nozzle == NozzleKind.Converging)
            {
                o.Add(new Finding("nozzle.vector_conflict", Level.Fail,
                    Loc.T("矢量喷口配固定收敛喷管：收敛喷管没有可动段，偏不了。改成可变或收扩", "Vectoring with a fixed converging nozzle: there is no moving section to deflect. Switch to variable or converging-diverging")));
                return o;
            }
            double deg = d.GimbalMaxDeg;
            if (deg < 2.0)
                o.Add(new Finding("nozzle.gimbal", Level.Warn,
                    string.Format(Loc.T("最大偏转 {0:F1}°：小于 2° 基本感觉不到，只在配平上有用", "Max deflection {0:F1} deg: under 2 deg you barely feel it, only good for trim"), deg)));
            else if (deg > 22.0)
                o.Add(new Finding("nozzle.gimbal", Level.Warn,
                    string.Format(Loc.T("最大偏转 {0:F1}°：超过 22° 只有试验机做到过，作动筒与喷管重量会明显增加", "Max deflection {0:F1} deg: past 22 deg only test aircraft managed it, and actuators plus nozzle get noticeably heavier"), deg)));
            else
                o.Add(new Finding("nozzle.gimbal", Level.Ok,
                    string.Format(Loc.T("最大偏转 {0:F0}°：偏满时轴向推力只剩 {1:P1}、侧向 {2:P1}", "Max deflection {0:F0} deg: at full deflection axial thrust is down to {1:P1} and lateral is {2:P1}"),
                        deg, Math.Cos(deg * Math.PI / 180.0), Math.Sin(deg * Math.PI / 180.0))));
            return o;
        }

        /// <summary>
        /// 空中再起动：熄火之后"还点得着吗"。
        ///
        /// 这一条只报一个工况（11 km/Ma0.9，典型巡航高度），因为再点火窗口的本质是
        /// "能点的高度上限"——报上限比报某个高度的状态更能说明问题。
        /// 门槛来自 <see cref="Relight"/>，与飞行时的判定是同一个函数：
        /// 判据说点不着，飞的时候按一下也确实点不着，不会两套话。
        /// </summary>
        public static List<Finding> CheckRelight(EngineDesignData d)
        {
            List<Finding> o = new List<Finding>();
            if (d == null) return o;
            ControlKind ctl = ControlSystem.Effective(d);
            double ceiling = Relight.CeilingMeters(d, 0.9, ctl);
            RelightResult atCruise = Relight.Window(d, 11000.0, 0.9, -1.0);
            if (atCruise.CanLight)
            {
                o.Add(new Finding("start.relight", Level.Ok,
                    string.Format(Loc.T("空中再起动：11 km/Ma0.9 熄火后点得着（风车转速 {0:F0}%，进气总压 {1:F1} kPa，门槛 {2:F0}）。这一档的点火高度上限约 {3:F0} m", "Air relight: after a flameout at 11 km/Ma0.9 it lights (windmill {0:F0}%, intake total pressure {1:F1} kPa, threshold {2:F0}). This control tier tops out near {3:F0} m"),
                        atCruise.WindmillSpool * 100.0, atCruise.FacePressureKPa, atCruise.MinPressureKPa, ceiling)));
            }
            else
            {
                o.Add(new Finding("start.relight", Level.Warn,
                    string.Format(Loc.T("空中再起动：11 km/Ma0.9 熄火后点不着——{0}。这一档的点火高度上限约 {1:F0} m，高过它熄火就只能滑翔到下面去再点", "Air relight: it will NOT light after a flameout at 11 km/Ma0.9 — {0}. This control tier tops out near {1:F0} m; above that a flameout means gliding down before you try again"),
                        atCruise.Reason, ceiling)));
            }
            return o;
        }

        /// <summary>自持：把「涡轮能不能带动压气机」翻译成玩家能看懂的一句话。</summary>
        public static List<Finding> CheckSustain(ResultView v)
        {
            List<Finding> o = new List<Finding>();
            if (!v.Num.ContainsKey("self_sustaining")) return o;
            if (v.Num.ContainsKey("windmill") && v.Get("windmill") > 0.5)
            {
                o.Add(new Finding("sustain.off", Level.Ok, Loc.T("油门归零：停车风车状态，不评价自持", "Throttle at zero: windmilling, self-sustaining not judged")));
                return o;
            }
            double thr = v.Num.ContainsKey("throttle") ? v.Get("throttle") : 1.0;
            if (v.Get("self_sustaining") > 0.5)
                o.Add(new Finding("sustain.ok", Level.Ok,
                    string.Format(Loc.T("油门 {0:F0}% 可自持", "Self-sustaining at {0:F0}% throttle"), thr * 100.0)));
            else
                o.Add(new Finding("sustain", Level.Warn,
                    string.Format(Loc.T("油门 {0:F0}% 不能自持：涡轮抽不出足够功率带压气机，真实发动机会熄火", "Not self-sustaining at {0:F0}% throttle: the turbine cannot drive the compressor, and a real engine would flame out"), thr * 100.0)));
            return o;
        }
    }
}
