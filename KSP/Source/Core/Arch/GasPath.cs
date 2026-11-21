using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 拓扑模板上的一个节点：哪个部件、从哪个测站进、到哪个测站出、哪一路气、挂哪根轴。
    ///
    /// 这一层存在的理由：以前"流路"是写在 `Cycle.SolveDesignPoint` 里的语句顺序——
    /// 想插一台中压压气机，得去函数中间改十行；想让两台涡轮串在一条轴上，
    /// 得去 `SolveTurbine` 里加一次调用。现在流路是**数据**，求解器照着走。
    /// 这台机器的部件集合是**固定拓扑**（不是任意节点图），但节点顺序、有没有、
    /// 挂哪根轴、哪一路气，都是模板给出来的。
    /// </summary>
    public sealed class PathNode
    {
        public string Part = "";              // "FAN" / "LPC" / "HPT" …
        public ComponentKind Kind = ComponentKind.Duct;
        public int InletStation = -1;
        public int OutletStation = -1;
        public FlowRole Flow = FlowRole.Core;
        /// <summary>挂哪根轴（压气机/涡轮/风扇才有）。0 = 不挂轴。</summary>
        public int Shaft;
        public bool Present = true;

        public string Describe()
        {
            return string.Format("{0}[{1}] {2}→{3}{4}{5}",
                Part, Kind, Stations.Name(InletStation), Stations.Name(OutletStation),
                Flow == FlowRole.Bypass ? " 外涵" : "",
                Shaft > 0 ? " 轴" + Shaft : "");
        }
    }

    /// <summary>
    /// 固定拓扑的气路模板。`ForDesign()` 按设计对象把"这台机器有哪些部件、按什么顺序、
    /// 各在哪条路上"展开成一张表；求解器只按这张表走，不再自己写死顺序。
    ///
    /// 为什么先固定拓扑而不是一上来就做通用节点图：部件化本身就会动到每一行公式，
    /// 再叠一个图编辑器，出问题时分不清是"部件写错了"还是"图接错了"。
    /// 固定拓扑的节点表已经能表达现有全部机型（涡喷/涡扇/单轴~三轴/有加力），
    /// 后面要插中压压气机，只是往表里加一个节点。
    /// </summary>
    public sealed class GasPathTemplate
    {
        public List<PathNode> Nodes = new List<PathNode>();
        /// <summary>核心那一路（含进气道、含分流器）。</summary>
        public List<PathNode> Core = new List<PathNode>();
        /// <summary>外涵那一路（只有涡扇才有内容）。</summary>
        public List<PathNode> Bypass = new List<PathNode>();

        public static GasPathTemplate ForDesign(EngineDesignData d)
        {
            GasPathTemplate t = new GasPathTemplate();
            if (d == null) return t;
            bool hasFan = d.HasFan;
            bool hasLpc = d.Lpc != null && d.Lpc.Stages > 0;
            bool hasHpc = d.Hpc != null && d.Hpc.Stages > 0;
            bool hasAb = d.Afterburner != null && d.Afterburner.Enabled;

            PathNode intake = new PathNode
            {
                Part = "INTAKE", Kind = ComponentKind.Intake,
                InletStation = Stations.Ambient, OutletStation = Stations.IntakeExit, Flow = FlowRole.Core,
            };
            t.Add(intake);

            if (hasFan)
            {
                t.Add(new PathNode
                {
                    Part = "FAN", Kind = ComponentKind.Fan,
                    InletStation = Stations.IntakeExit, OutletStation = Stations.FanExit,
                    Flow = FlowRole.Mixed, Shaft = d.Fan.Shaft,
                });
                // 分流器：在风扇出口把一股气分成核心与外涵。外涵那一路从这里开始。
                PathNode split = new PathNode
                {
                    Part = "SPLIT", Kind = ComponentKind.Splitter,
                    InletStation = Stations.FanExit, OutletStation = Stations.FanExit,
                    Flow = FlowRole.Core,
                };
                t.Add(split);
                // 外涵那一路照旧永远建出来：老 Cycle 只要 HasFan 就算外涵喷管，
                // 无论 BPR 是 0 还是 22（后者是玩家人为拉大的荒唐值，也必须算，否则推力凭空少一半）。
                {
                    PathNode duct = new PathNode
                    {
                        Part = "BYPASS_DUCT", Kind = ComponentKind.Duct,
                        InletStation = Stations.BypassExit, OutletStation = Stations.BypassExit,
                        Flow = FlowRole.Bypass,
                    };
                    PathNode bnoz = new PathNode
                    {
                        Part = "BYPASS_NOZZLE", Kind = ComponentKind.Nozzle,
                        InletStation = Stations.BypassExit, OutletStation = Stations.BypassNozzleExit,
                        Flow = FlowRole.Bypass,
                    };
                    t.Add(duct);
                    t.Add(bnoz);
                    t.Bypass.Add(split);
                    t.Bypass.Add(duct);
                    t.Bypass.Add(bnoz);
                }
            }

            int coreIn = hasFan ? Stations.FanExit : Stations.IntakeExit;
            if (hasLpc)
            {
                t.Add(new PathNode
                {
                    Part = "LPC", Kind = ComponentKind.Compressor,
                    InletStation = coreIn, OutletStation = Stations.LpcExit,
                    Flow = FlowRole.Core, Shaft = d.Lpc.Shaft,
                });
                coreIn = Stations.LpcExit;
            }
            if (hasHpc)
            {
                t.Add(new PathNode
                {
                    Part = "HPC", Kind = ComponentKind.Compressor,
                    InletStation = coreIn, OutletStation = Stations.HpcExit,
                    Flow = FlowRole.Core, Shaft = d.Hpc.Shaft,
                });
                coreIn = Stations.HpcExit;
            }
            t.Add(new PathNode
            {
                Part = "BURNER", Kind = ComponentKind.Burner,
                InletStation = coreIn, OutletStation = Stations.BurnerExit, Flow = FlowRole.Core,
            });

            int tIn = Stations.BurnerExit;
            if (d.Hpt != null && d.Hpt.Stages > 0)
            {
                int tOut = (d.Ipt != null && d.Ipt.Stages > 0) || (d.Lpt != null && d.Lpt.Stages > 0)
                    ? Stations.HptExit : Stations.TurbineExit;
                t.Add(new PathNode
                {
                    Part = "HPT", Kind = ComponentKind.Turbine,
                    InletStation = tIn, OutletStation = tOut,
                    Flow = FlowRole.Core, Shaft = d.Hpt.Shaft,
                });
                tIn = tOut;
            }
            if (d.Ipt != null && d.Ipt.Stages > 0)
            {
                int tOut = d.Lpt != null && d.Lpt.Stages > 0 ? Stations.IptExit : Stations.TurbineExit;
                t.Add(new PathNode
                {
                    Part = "IPT", Kind = ComponentKind.Turbine,
                    InletStation = tIn, OutletStation = tOut,
                    Flow = FlowRole.Core, Shaft = d.Ipt.Shaft,
                });
                tIn = tOut;
            }
            if (d.Lpt != null && d.Lpt.Stages > 0)
            {
                t.Add(new PathNode
                {
                    Part = "LPT", Kind = ComponentKind.Turbine,
                    InletStation = tIn, OutletStation = Stations.TurbineExit,
                    Flow = FlowRole.Core, Shaft = d.Lpt.Shaft,
                });
                tIn = Stations.TurbineExit;
            }

            if (hasAb)
            {
                t.Add(new PathNode
                {
                    Part = "AFTERBURNER", Kind = ComponentKind.Afterburner,
                    InletStation = tIn, OutletStation = Stations.AfterburnerExit, Flow = FlowRole.Core,
                });
                tIn = Stations.AfterburnerExit;
            }
            t.Add(new PathNode
            {
                Part = "NOZZLE", Kind = ComponentKind.Nozzle,
                InletStation = tIn, OutletStation = Stations.NozzleExit, Flow = FlowRole.Core,
            });

            // 核心那一路的顺序 = 全表里除"外涵专属"以外的节点
            foreach (PathNode n in t.Nodes)
            {
                if (n.Flow == FlowRole.Bypass) continue;
                t.Core.Add(n);
            }
            return t;
        }

        private void Add(PathNode n)
        {
            Nodes.Add(n);
        }

        /// <summary>按部件种类找节点（设计点求解按 Part 名取，不按下标取——下标会随拓扑变）。</summary>
        public PathNode Find(string part)
        {
            for (int i = 0; i < Nodes.Count; i++)
                if (string.Equals(Nodes[i].Part, part, StringComparison.OrdinalIgnoreCase)) return Nodes[i];
            return null;
        }

        /// <summary>
        /// 拓扑级校验：部件齐全、测站接得上、每根有负载的轴上有涡轮。
        /// 这些错在求解之前就该报出来——跑到一半报"涡轮级数不足"，
        /// 玩家根本看不出是自己轴挂错了。
        /// </summary>
        public void Validate(EngineDesignData d, List<string> errors, List<string> warnings = null)
        {
            if (errors == null) errors = new List<string>();
            if (d == null) { errors.Add(Loc.T("没有设计", "No design")); return; }
            if (Nodes.Count == 0) { errors.Add(Loc.T("气路模板是空的", "The gas path template is empty")); return; }

            if (Nodes[0].Kind != ComponentKind.Intake)
                errors.Add(string.Format(
                    Loc.T("流路必须以进气道开头（现在第一个部件是 {0}）",
                          "The flow path must start at the intake (the first part is {0})"),
                    Nodes[0].Part));

            PathNode nozzle = Find("NOZZLE");
            if (nozzle == null) errors.Add(Loc.T("没有主喷管：这台机器没有出口", "No main nozzle: this engine has no exit"));
            else if (Nodes[Nodes.Count - 1] != nozzle && Find("NOZZLE").Flow == FlowRole.Core)
            {
                // 主喷管后面不该再挂核心部件
                int at = Nodes.IndexOf(nozzle);
                if (at >= 0 && at != Nodes.Count - 1)
                    errors.Add(string.Format(
                        Loc.T("主喷管后面还接着部件（{0}）：排气已经出去了",
                              "Another part ({0}) is still attached behind the main nozzle: the exhaust has already left"),
                        Nodes[at + 1].Part));
            }

            // 测站连续：每个核心节点的入口必须等于上一个核心节点的出口
            int prevOut = -1;
            foreach (PathNode n in Core)
            {
                if (n.Kind == ComponentKind.Splitter) { prevOut = n.OutletStation; continue; }
                if (prevOut >= 0 && n.InletStation != prevOut)
                    errors.Add(string.Format(Loc.T("测站不连续：{0} 从 {1} 进，但上一个部件出的是 {2}", "Stations are not continuous: {0} enters at {1} but the previous part leaves at {2}"),
                        n.Part, Stations.Name(n.InletStation), Stations.Name(prevOut)));
                prevOut = n.OutletStation;
            }

            // 机型与部件对得上
            if (d.Kind == EngineKind.Turbofan && !d.HasFan)
                errors.Add(Loc.T("标成了涡扇但没有风扇（风扇直径必须 > 0）", "Marked as a turbofan but has no fan (the fan diameter must be > 0)"));
            if (d.Kind == EngineKind.Turbojet && d.HasFan)
                errors.Add(Loc.T("标成了涡喷却装了风扇：改 Kind 或者把风扇直径清零", "Marked as a turbojet but a fan is fitted: change Kind or zero the fan diameter"));

            bool hasComp = (d.Lpc != null && d.Lpc.Stages > 0) || (d.Hpc != null && d.Hpc.Stages > 0);
            if (!hasComp) errors.Add(Loc.T("没有任何压气机级：这只是一台冲压发动机", "No compressor stages at all: that is a ramjet"));

            if (d.Burner == null || d.Burner.ExitTemp <= 0.0)
                errors.Add(Loc.T("燃烧室没有出口温度：这台机器不烧油", "The burner has no exit temperature: this engine burns no fuel"));

            // 每根轴上"有负载就必须有涡轮"，而且轴号得在设计范围内
            Dictionary<int, double> load = new Dictionary<int, double>();
            Dictionary<int, bool> driven = new Dictionary<int, bool>();
            int maxShaft = Math.Max(d.ShaftCount, 1);
            // 轴号超出整机轴数只报警告、不判死：老设计器允许"叶尖/轴号"留成上一次的值，
            // 真机上也确实有玩家把 ShaftCount 从 2 改回 1 却忘了改部件的轴号。
            // 这种行为在旧 Cycle 里是能算的（多出来的轴自己配平），现在就照旧算，
            // 但要明确告诉玩家"你设的轴数和挂法对不上"。
            Action<int, string> noteShaft = (sh, part) =>
            {
                if (sh < 1)
                    errors.Add(string.Format(Loc.T("{0} 挂在轴 {1}：轴号必须从 1 开始", "{0} is on shaft {1}: shaft numbers must start at 1"), part, sh));
                else if (sh > maxShaft && warnings != null)
                    warnings.Add(string.Format(Loc.T("{0} 挂在轴 {1}，但整机只设了 {2} 根轴：按第 {1} 根轴单独处理", "{0} is on shaft {1} but the engine declares only {2} shaft(s): it is solved on shaft {1} anyway"),
                                                part, sh, maxShaft));
            };
            Action<int, string, double> addLoad = (sh, part, w) =>
            {
                noteShaft(sh, part);
                if (!load.ContainsKey(sh)) load[sh] = 0.0;
                load[sh] += w;
            };
            if (d.HasFan) addLoad(d.Fan.Shaft, Loc.T("风扇", "fan"), 1.0);
            if (d.Lpc != null && d.Lpc.Stages > 0) addLoad(d.Lpc.Shaft, Loc.T("低压压气机", "LPC"), 1.0);
            if (d.Hpc != null && d.Hpc.Stages > 0) addLoad(d.Hpc.Shaft, Loc.T("高压压气机", "HPC"), 1.0);
            Action<int, string> addDriven = (sh, part) =>
            {
                noteShaft(sh, part);
                driven[sh] = true;
            };
            if (d.Hpt != null && d.Hpt.Stages > 0) addDriven(d.Hpt.Shaft, Loc.T("高压涡轮", "HPT"));
            if (d.Ipt != null && d.Ipt.Stages > 0) addDriven(d.Ipt.Shaft, Loc.T("中压涡轮", "IPT"));
            if (d.Lpt != null && d.Lpt.Stages > 0) addDriven(d.Lpt.Shaft, Loc.T("低压涡轮", "LPT"));
            foreach (KeyValuePair<int, double> kv in load)
            {
                bool ok;
                if (!driven.TryGetValue(kv.Key, out ok) || !ok)
                    errors.Add(string.Format(Loc.T("轴 {0} 上有转子负载，但没有涡轮驱动它 → 转不起来", "Shaft {0} carries rotor load but no turbine drives it, so it cannot spin"), kv.Key));
            }
        }

        public string Describe()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("核心：");
            for (int i = 0; i < Core.Count; i++)
            {
                if (i > 0) sb.Append(" → ");
                sb.Append(Core[i].Part).Append('(').Append(Stations.Name(Core[i].InletStation))
                  .Append('-').Append(Stations.Name(Core[i].OutletStation)).Append(')');
            }
            if (Bypass.Count > 0)
            {
                sb.Append("｜外涵：");
                for (int i = 0; i < Bypass.Count; i++)
                {
                    if (i > 0) sb.Append(" → ");
                    sb.Append(Bypass[i].Part);
                }
            }
            return sb.ToString();
        }
    }
}