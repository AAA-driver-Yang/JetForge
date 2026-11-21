using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>部件种类。拓扑模板按它分派求解器，界面上按它分页。</summary>
    public enum ComponentKind
    {
        Intake = 0,
        Fan = 1,
        Compressor = 2,
        Splitter = 3,
        Burner = 4,
        Turbine = 5,
        Afterburner = 6,
        Nozzle = 7,
        Duct = 8,
    }

    public enum PortRole
    {
        Inlet = 0,
        Outlet = 1,
    }

    /// <summary>
    /// 部件的一个接口。带着"这个口在哪个测站、过哪一路气、面积多大"——
    /// 流量匹配以后要迭代的就是这些口上的残差，不是散在代码里的几个 double。
    /// </summary>
    public sealed class ComponentPort
    {
        public string PortId = "";
        public PortRole Role = PortRole.Inlet;
        public FlowRole Flow = FlowRole.Core;
        public int Station = -1;
        public double Area = double.NaN;
        // 曾经有个 `Required` 开关（"这个口的流量是可选的，外涵可以没有"），从没被读过：
        // 接口表里列出来的口，`Ports` 一律当必需。留着一个不生效的开关，等于告诉后来的人
        // "这里可以放宽"，而实际上放宽不了。真要做可选口，得让校验器认它，那是一次改接口的事。

        public ComponentPort() { }
        public ComponentPort(string id, PortRole role, FlowRole flow, int station, double area = double.NaN)
        {
            PortId = id; Role = role; Flow = flow; Station = station; Area = area;
        }

        public string Describe()
        {
            return string.Format("{0}{1}/{2}/{3}{4}",
                PortId, Role == PortRole.Inlet ? "→" : Loc.T("→出", "→out"),
                Flow, Stations.Name(Station),
                GasState.IsFinite(Area) ? string.Format(" A={0:F4}m²", Area) : "");
        }
    }

    /// <summary>
    /// 一次求解的上下文：技术代、设计对象、各轴转速、环境。
    /// 部件**只读**它，绝不许往里写。这样同一个 ctx 能同时跑设计点、非设计点、
    /// 以及"把某一个部件单独拎出来试"的诊断，不会互相污染。
    /// </summary>
    public sealed class SolverContext
    {
        public EngineDesignData Design;
        public TechLevel Tech;

        /// <summary>各轴设计转速 rpm（键 = 轴号）。</summary>
        public Dictionary<int, double> RpmDesign = new Dictionary<int, double>();
        /// <summary>各轴当前转速比例（1.0 = 设计转速）。缺省全 1。</summary>
        public double SpeedFrac = 1.0;

        public double Altitude;
        public double Mach;
        public double AmbientPressure;
        public double AmbientTemperature;
        /// <summary>来流速度 m/s（冲压阻力要用）。</summary>
        public double FlightVelocity;

        /// <summary>这轮求解已经积累的告警/错误（由网络汇总，部件只管往里加）。</summary>
        public List<string> Warnings = new List<string>();

        /// <summary>
        /// 各轴的功需求（W，键 = 轴号）。由"先解出来的负载部件"写进来，
        /// 涡轮只读它——这是"轴"在架构里唯一的位置。负载部件还没解出来时为空，
        /// 涡轮按 0 需求直通（于是设计点必须先跑压气机、再跑涡轮，与老 Cycle 同序）。
        /// </summary>
        public Dictionary<int, double> ShaftDemand = new Dictionary<int, double>();

        public void AddDemand(int shaft, double watts)
        {
            double v;
            ShaftDemand.TryGetValue(shaft, out v);
            ShaftDemand[shaft] = v + (watts > 0.0 ? watts : 0.0);
        }

        public double Demand(int shaft)
        {
            double v;
            return ShaftDemand.TryGetValue(shaft, out v) ? v : 0.0;
        }

        public double ShaftRpm(int shaft)
        {
            double r;
            if (RpmDesign.TryGetValue(shaft, out r)) return r * SpeedFrac;
            return 0.0;
        }
    }

    /// <summary>
    /// 一个部件的求解产物。**只输出，不回写输入**：
    /// 出口气流是新对象，轴功/燃油是数，告警是文字。
    /// 这样"同一条链路上同一股气"才有可能被核对——每个站都能拿出来看。
    /// </summary>
    public sealed class ComponentResult
    {
        public bool Success = true;
        /// <summary>失败码（"NO_STAGES"、"T4_BELOW_COMPRESSOR"…），成功时为空。</summary>
        public string FailureCode = "";
        public string FailureMessage = "";

        public List<GasState> Outlets = new List<GasState>();
        public GasState Outlet
        {
            get { return Outlets.Count > 0 ? Outlets[0] : null; }
        }

        /// <summary>这个部件吃掉的轴功 W。</summary>
        public double PowerRequired;
        /// <summary>这个部件从轴上取出来的功 W。</summary>
        public double PowerProduced;
        public double FuelFlow;
        public double PressureRatio = 1.0;
        public double TemperatureRise;

        public List<StageRow> Stages = new List<StageRow>();
        public Dictionary<string, ScaleInfo> Scale = new Dictionary<string, ScaleInfo>();
        public List<string> Warnings = new List<string>();
        /// <summary>软错误：还能继续算，但这台机器的设计不成立（级数不足、落压比超限）。</summary>
        public List<string> Errors = new List<string>();
        public Dictionary<string, double> Diagnostics = new Dictionary<string, double>();

        public static ComponentResult Failed(string code, string message)
        {
            ComponentResult r = new ComponentResult();
            r.Success = false;
            r.FailureCode = code;
            r.FailureMessage = message;
            return r;
        }

        public void Warn(string s) { Warnings.Add(s); }

        /// <summary>记一条软错误：设计不成立，但计算继续（老 Cycle 的行为）。</summary>
        public void Fail(string s) { Errors.Add(s); }

        public void Put(string key, double v)
        {
            Diagnostics[key] = v;
        }

        public double Get(string key, double fallback = 0.0)
        {
            double v;
            return Diagnostics.TryGetValue(key, out v) ? v : fallback;
        }

        public string Describe()
        {
            GasState o = Outlet;
            return o == null ? Loc.T("(无出口)", "(no outlet)") : o.Describe();
        }
    }

    /// <summary>
    /// 部件接口。设计点、非设计点、以后的流量匹配迭代都只认这一个签名。
    ///
    /// 约定（写成接口注释是因为它比代码更难守）：
    /// 1. 不许改 inlet 指向的对象；要改就 Clone。
    /// 2. 不许读 ctx 之外的全局面（static 字段）。
    /// 3. 失败要返回 FailureCode，不许"悄悄给一个能跑的推力"。
    /// </summary>
    public interface IEngineComponent
    {
        string ComponentId { get; }
        ComponentKind Kind { get; }
        IList<ComponentPort> InletPorts { get; }
        IList<ComponentPort> OutletPorts { get; }

        ComponentResult Solve(GasState inlet, SolverContext ctx);

        /// <summary>设计是否合法（级数、轴连接、面积）。错误写进 errors，不下场判定。</summary>
        void Validate(EngineDesignData d, List<string> errors);
    }

    /// <summary>部件基类：只管端口清单，求解在子类。</summary>
    public abstract class EngineComponentBase : IEngineComponent
    {
        public string ComponentId = "";
        public ComponentKind Kind = ComponentKind.Duct;

        private readonly List<ComponentPort> _in = new List<ComponentPort>();
        private readonly List<ComponentPort> _out = new List<ComponentPort>();

        string IEngineComponent.ComponentId { get { return ComponentId; } }
        ComponentKind IEngineComponent.Kind { get { return Kind; } }
        IList<ComponentPort> IEngineComponent.InletPorts { get { return _in; } }
        IList<ComponentPort> IEngineComponent.OutletPorts { get { return _out; } }

        public List<ComponentPort> Inlets { get { return _in; } }
        public List<ComponentPort> Outlets { get { return _out; } }

        protected void Port(ComponentPort p)
        {
            if (p.Role == PortRole.Inlet) _in.Add(p); else _out.Add(p);
        }

        public abstract ComponentResult Solve(GasState inlet, SolverContext ctx);

        /// <summary>
        /// 入口状态能不能用。**每个部件求解的第一步都必须是这一句。**
        ///
        /// 为什么值得每个部件都做一遍：坏状态从上游漏下来时，各部件会各自把它算成
        /// "看着正常"的假数——`pt = 0` 的入口会把压比算成 0（像"压气机不干活"）、
        /// 把推力算成 0（像"设计不行"），而真正的原因在两三个部件之前。
        /// 更糟的是 `NaN` 会一路传染：只要有一处进来，出口、级表、推力全是 `NaN`，
        /// 最后只剩审计在喊"数据不自洽"，而没人知道是谁先坏的。
        ///
        /// 所以约定：入口不合格 → `Success=false` + `FailureCode="BAD_INLET"`，
        /// 并且**不产出任何出口**（不产出垃圾，就不会有人拿它当数用）。
        /// </summary>
        protected bool GuardInlet(GasState inlet, ComponentResult r)
        {
            if (inlet == null)
            {
                r.Success = false;
                r.FailureCode = "NO_INLET";
                r.FailureMessage = string.Format(
                    Loc.T("{0} 没拿到入口状态", "{0}: no inlet state"), ComponentId);
                return false;
            }
            string why;
            GasValidity v = inlet.Validate(out why);
            if (v == GasValidity.Valid) return true;
            r.Success = false;
            r.FailureCode = "BAD_INLET";
            r.FailureMessage = string.Format(
                Loc.T("{0} 的入口状态不能用：{1}（{2}）", "{0}: inlet state unusable — {1} ({2})"),
                ComponentId, why, v);
            return false;
        }

        public virtual void Validate(EngineDesignData d, List<string> errors) { }
    }
}