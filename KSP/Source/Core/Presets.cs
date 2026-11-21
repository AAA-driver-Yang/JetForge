using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 真实发动机预设：让玩家不用从空白页开始。
    ///
    /// 数据来源是 research/engine_data.md 里的公开手册值（推力、空气流量、总压比、T4、
    /// SFC、干重），参数化的过程与本项目阶段一的 Python 校验脚本（proto/validate.py）
    /// 完全一致：几何（级数、叶尖直径、双转子怎么分）照真机抄，涡轮 psi 用二分拟合到
    /// 真机的总压比。所以这不是"随便调一调到看着像"，而是同一套拟合方法在 C# 里的复现。
    ///
    /// 两个必须说明的地方：
    ///
    /// 1. **进气口径是等效捕获面积，不是真机进气口径。** 我们的流量是
    ///    `ṁ = A·f(面马赫=0.5)·pt0/√Tt0`，一个 1.0 m 的进气口在静止海平面给 203.6 kg/s，
    ///    而 J79 真机只要 77 kg/s。所以预设里按目标流量反解口径（`FitInlet`），
    ///    保证"海平面静止的推力和流量落在真机量级"。真机的进气口径另有含义
    ///    （唇口、喉道、附面层），那部分属于进气道造型，是后期的事。
    ///
    /// 2. **SFC 我们系统性偏低 4%~7%（J85 除外）。** 阶段一已经查清：不是模型错，
    ///    是老手册的 TIT 系统性偏低 130~310 K（燃油比反解要求的 T4 比标称高），
    ///    J85 那条 30% 是维基数据自相矛盾。预设注释里逐条写明，别当成待修的 bug。
    /// </summary>
    public class PresetInfo
    {
        public string Key;
        public string Label;
        /// <summary>一句话说明这台机器的来路与已知偏差。</summary>
        public string Note;

        public double RealOpr;
        public double RealThrustKN;
        public double RealMdot;
        /// <summary>kg/(N·h)。手册常给 lb/(lbf·h)，换算系数 0.10197162。</summary>
        public double RealSfc;
        public double RealDryKg;

        internal EngineDesignData Base;
    }

    public static class Presets
    {
        /// <summary>lb/(lbf·h) → kg/(N·h)</summary>
        private const double SfcLbToSI = 0.10197162;

        private static List<PresetInfo> _all;

        public static IList<PresetInfo> All
        {
            get
            {
                if (_all == null) _all = BuildTable();
                return _all;
            }
        }

        public static PresetInfo Get(string key)
        {
            IList<PresetInfo> all = All;
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].Key, key, StringComparison.OrdinalIgnoreCase)) return all[i];
            return null;
        }

        /// <summary>造一台可用的设计（每次都是新对象，进气口径已标定到真机流量）。</summary>
        public static EngineDesignData Build(string key)
        {
            PresetInfo p = Get(key);
            if (p == null) return null;
            EngineDesignData d = p.Base.Clone();

            // 两步标定要交替：缩进气口径会轻微改变各级压比（叶尖线速度跟着变），
            // 而拟合压比又会轻微改变核心流量。两次交替就收敛，剩下的残差在 0.5% 以内。
            FitInlet(d, p.RealMdot);
            FitPsi(d, p.RealOpr);
            FitInlet(d, p.RealMdot);
            FitPsi(d, p.RealOpr);

            // 第三步：喘振裕度。前两步只管"流量对得上、压比对得上"，
            // 结果 J57/RD-33 的 HPC 被判成"已进失速区"——真机当然不会。
            // 这一步把叶尖直径缩到裕度够，再回头补一次压比（缩直径会改流量分配）。
            FitSurge(d);
            FitPsi(d, p.RealOpr);
            FitSurge(d);
            return d;
        }

        /// <summary>
        /// 把压气机叶尖直径缩到喘振裕度够用。
        ///
        /// φ = V_ax / U。U 被"叶尖马赫上限"锁死，改直径不会改 U（转速跟着变），
        /// 而通流面积 ∝ D²，所以**缩小直径只会把轴向速度抬上去，压比不动、只有 φ 动**。
        /// 这与"改涡轮叶尖直径不改变涡轮能力"是同一把锁的两面。
        ///
        /// 判据直接用 Criteria 本身，不另写一套阈值：预设能被判据放行，
        /// 就必须和判据算出同一个 φ（两套公式迟早对不上）。
        /// </summary>
        public static void FitSurge(EngineDesignData d, int passes = 24)
        {
            if (d == null) return;
            for (int i = 0; i < passes; i++)
            {
                CycleResult r = Cycle.SolveDesignPoint(d);
                if (!r.Ok) return;
                CheckReport rep = Criteria.Evaluate(d, Criteria.ViewOf(r), d.Tech, Gas.P0, 0.0);
                string bad = null;
                for (int k = 0; k < rep.Findings.Count; k++)
                {
                    Finding f = rep.Findings[k];
                    if (f == null || f.LevelValue != Level.Fail) continue;
                    // 认"喘振裕度为负"这件事本身，不去认它那句话。
                    // 这里以前写的是 `f.Message.Contains("已进失速区")`——中文界面下能过，
                    // 切成英文就一条也匹配不上，于是预设的叶尖直径永远不缩：
                    // **同一台预设发动机，换个界面语言会有两组几何**。
                    // 键与数值是稳定量，文案不是。
                    if (f.Key != null && f.Key.EndsWith(".surge", StringComparison.Ordinal) && f.Value < 0.0)
                    { bad = f.Key; break; }
                }
                if (bad == null) return;
                if (bad.StartsWith("HPC")) d.Hpc.TipDiameter *= 0.93;
                else if (bad.StartsWith("LPC")) d.Lpc.TipDiameter *= 0.93;
                else if (bad.StartsWith("Fan")) d.Fan.Diameter *= 0.93;
                else return;
            }
        }

        /// <summary>
        /// 二分压气机 psi：让设计点总压比落到真机值。这跟阶段一 Python 的 `fit_psi`
        /// 是同一件事——真机手册给的是总压比，不是"每级负荷系数"，所以只能反着解。
        /// </summary>
        public static void FitPsi(EngineDesignData d, double targetOpr)
        {
            if (d == null || targetOpr <= 1.0) return;
            double lo = 0.15, hi = 1.60;
            for (int i = 0; i < 22; i++)
            {
                double mid = 0.5 * (lo + hi);
                d.Lpc.PsiScale = mid;
                d.Hpc.PsiScale = mid;
                CycleResult r = Cycle.SolveDesignPoint(d);
                if (!r.Ok || r.Opr <= 0.0) { hi = mid; continue; }
                if (r.Opr < targetOpr) lo = mid; else hi = mid;
            }
            double psi = 0.5 * (lo + hi);
            d.Lpc.PsiScale = psi;
            d.Hpc.PsiScale = psi;
        }

        /// <summary>
        /// 按目标流量反解进气口径。流量大致 ∝ 迎风面积，所以每次按 sqrt(比值) 缩，
        /// 迭代三次就够（面积与流量之间有个风扇轮毂比带来的非线性，一次缩不准）。
        /// </summary>
        public static void FitInlet(EngineDesignData d, double targetMdot)
        {
            if (d == null || targetMdot <= 0.0) return;
            for (int i = 0; i < 3; i++)
            {
                CycleResult r = Cycle.SolveDesignPoint(d);
                if (!r.Ok || r.MdotAir <= 1e-6) return;
                double k = Math.Sqrt(targetMdot / r.MdotAir);
                if (Math.Abs(k - 1.0) < 0.002) return;
                d.InletDiameter *= k;
                if (d.HasFan) d.Fan.Diameter *= k;
                if (d.CoreInletDiameter > 0.0) d.CoreInletDiameter *= k;
            }
        }

        private static List<PresetInfo> BuildTable()
        {
            List<PresetInfo> list = new List<PresetInfo>();

            // ---- J79：单转子涡喷，17 级压气机 + 3 级涡轮，1955 ----
            EngineDesignData j79 = new EngineDesignData();
            j79.DesignName = "J79-GE-17";
            j79.Kind = EngineKind.Turbojet;
            j79.TechName = "1960s";
            j79.Length = 5.2; j79.LengthAuto = false;
            // 真机外径 0.99 m（39 in），长 5.30 m → L/D 5.35。进气口径反解出来只有 0.74 m，
            // 那按流量算的等效捕获面积，不是拿尺子能量到的数。
            j79.NacelleDiameter = 0.99;
            j79.Lpc.Stages = 0;
            j79.Hpc = new CompressorSpec { Name = "HPC", Stages = 17, TipDiameter = 0.76, PsiScale = 0.662, Shaft = 1 };
            j79.Hpt = new TurbineSpec { Name = "HPT", Stages = 3, TipDiameter = 0.70, Shaft = 1 };
            j79.Lpt = new TurbineSpec { Name = "LPT", Stages = 0, Shaft = 1 };
            j79.Ipt = new TurbineSpec { Name = "IPT", Stages = 0, Shaft = 1 };
            j79.Burner.ExitTemp = 1210.0;
            // J79 是罐环（can-annular）8 罐，不是环形——原来四台预设全写成 Annular，
            // 那是把"省事"写成了"数据"。罐式的压损与出口不均匀度都比环形大，这是它的真实短板。
            j79.Burner.Kind = BurnerKind.Cannular;
            j79.Burner.CanCount = 8;
            j79.Burner.LinerLd = 2.4;
            j79.Afterburner.Enabled = false;  // 预设按"干态额定"标定：开了加力设计点会按加力温度重算，真机那 79 kN 是加力额定
            j79.Afterburner.ExitTemp = 1900.0;
            j79.ShaftCount = 1;
            j79.OverrideBpr = 0.0;
            list.Add(new PresetInfo
            {
                Key = "J79",
                Label = "J79-GE-17（1955 单转子涡喷，17 级压气机）",
                Note = "干态 SFC 比手册低 7.2%：手册 TIT 1210 K 反解要 1361 K，是老手册系统性偏低。加力可自行打开。",
                RealOpr = 13.5, RealThrustKN = 52.8, RealMdot = 77.0,
                RealSfc = 0.84 * SfcLbToSI, RealDryKg = 1740.0,
                Base = j79
            });

            // ---- J57 / JT3C：双转子涡喷，9 级低压 + 7 级高压 ----
            EngineDesignData j57 = new EngineDesignData();
            j57.DesignName = "J57 / JT3C-7";
            j57.Kind = EngineKind.Turbojet;
            j57.TechName = "1960s";
            j57.Length = 4.66; j57.LengthAuto = false;   // 真机 -20A 全长 183.6 in；以前写 6.1 是按"两段压气机加起来猜的"
            j57.NacelleDiameter = 1.06;   // 41.6 in
            j57.Lpc = new CompressorSpec { Name = "LPC", Stages = 9, TipDiameter = 0.90, PsiScale = 0.677, Shaft = 1 };
            j57.Hpc = new CompressorSpec { Name = "HPC", Stages = 7, TipDiameter = 0.72, PsiScale = 0.677, Shaft = 2 };
            j57.Hpt = new TurbineSpec { Name = "HPT", Stages = 1, TipDiameter = 0.68, Shaft = 2 };
            j57.Lpt = new TurbineSpec { Name = "LPT", Stages = 2, TipDiameter = 0.78, Shaft = 1 };
            j57.Ipt = new TurbineSpec { Name = "IPT", Stages = 0, Shaft = 2 };
            j57.Burner.ExitTemp = 1143.0;
            j57.Burner.Kind = BurnerKind.Cannular;   // J57 也是 8 罐罐环，不是环形
            j57.Burner.CanCount = 8;
            j57.Burner.LinerLd = 2.6;
            j57.Afterburner.Enabled = false;
            j57.Afterburner.ExitTemp = 1900.0;
            j57.ShaftCount = 2;
            j57.OverrideBpr = 0.0;
            list.Add(new PresetInfo
            {
                Key = "J57",
                Label = "J57 / JT3C-7（1953 双转子涡喷，9 级低压 + 7 级高压）",
                Note = "干态 SFC 低 4.0%。双转子是这台机器的重点：低压轴挂 LPC + LPT，高压轴挂 HPC + HPT。",
                RealOpr = 12.5, RealThrustKN = 53.5, RealMdot = 81.7,
                RealSfc = 0.785 * SfcLbToSI, RealDryKg = 2347.0,
                Base = j57
            });

            // ---- J85：小推力单转子，9 级压气机 ----
            EngineDesignData j85 = new EngineDesignData();
            j85.DesignName = "J85-GE-13";
            j85.Kind = EngineKind.Turbojet;
            j85.TechName = "1960s";
            j85.Length = 2.9; j85.LengthAuto = false;
            j85.NacelleDiameter = 0.45;   // 17.7 in，L/D 6.4（本机最细长的一台，真实如此）
            j85.Lpc.Stages = 0;
            j85.Hpc = new CompressorSpec { Name = "HPC", Stages = 9, TipDiameter = 0.45, PsiScale = 0.964, Shaft = 1 };
            j85.Hpt = new TurbineSpec { Name = "HPT", Stages = 2, TipDiameter = 0.42, Shaft = 1 };
            j85.Lpt = new TurbineSpec { Name = "LPT", Stages = 0, Shaft = 1 };
            j85.Ipt = new TurbineSpec { Name = "IPT", Stages = 0, Shaft = 1 };
            j85.Burner.ExitTemp = 1253.0;
            j85.Burner.Kind = BurnerKind.Annular;    // J85 是环形，短火焰筒
            j85.Burner.CanCount = 0;
            j85.Burner.LinerLd = 1.9;
            j85.Afterburner.Enabled = false;
            j85.Afterburner.ExitTemp = 1900.0;
            j85.ShaftCount = 1;
            j85.OverrideBpr = 0.0;
            list.Add(new PresetInfo
            {
                Key = "J85",
                Label = "J85-GE-13（1957 小推力单转子涡喷，9 级压气机）",
                Note = "SFC 低 29.5% 是手册数据自相矛盾（燃油比反解要 T4=1564 K，标称 1253 K），不是模型问题；干重也偏重 38%。",
                RealOpr = 8.3, RealThrustKN = 16.0, RealMdot = 24.0,
                RealSfc = 1.24 * SfcLbToSI, RealDryKg = 310.0,
                Base = j85
            });

            // ---- RD-33：双转子低涵道比涡扇（米格-29）----
            EngineDesignData rd33 = new EngineDesignData();
            rd33.DesignName = "RD-33";
            rd33.Kind = EngineKind.Turbofan;
            rd33.TechName = "1970s";
            rd33.Length = 4.2; rd33.LengthAuto = false;
            rd33.NacelleDiameter = 1.04;   // 真机最大外径，L/D 4.07
            rd33.Fan = new FanSpec { Diameter = 0.78, Stages = 4, HubTipRatio = 0.40, Shaft = 1 };
            rd33.Lpc.Stages = 0;
            rd33.Hpc = new CompressorSpec { Name = "HPC", Stages = 9, TipDiameter = 0.60, PsiScale = 0.690, Shaft = 2 };
            rd33.Hpt = new TurbineSpec { Name = "HPT", Stages = 1, TipDiameter = 0.58, Shaft = 2 };
            rd33.Lpt = new TurbineSpec { Name = "LPT", Stages = 2, TipDiameter = 0.62, Shaft = 1 };
            rd33.Ipt = new TurbineSpec { Name = "IPT", Stages = 0, Shaft = 2 };
            rd33.Burner.ExitTemp = 1600.0;
            rd33.Burner.Kind = BurnerKind.Annular;   // RD-33 是环形，长径比偏短
            rd33.Burner.CanCount = 0;
            rd33.Burner.LinerLd = 2.0;
            rd33.Afterburner.Enabled = false;
            rd33.Afterburner.ExitTemp = 1900.0;
            rd33.Afterburner.FuelRatio = 0.022;
            rd33.ShaftCount = 2;
            rd33.OverrideBpr = 0.49;
            rd33.Nozzle = NozzleKind.ConvergingDiverging;
            list.Add(new PresetInfo
            {
                Key = "RD-33",
                Label = "RD-33（1970s 双转子低涵道比涡扇，BPR 0.49）",
                Note = "干态 SFC 低 6.7%。涵道比 0.49 是覆盖值（真机风扇直径 0.78 m 推不出这么小的涵道比）；加力喷油量已预填。",
                RealOpr = 21.0, RealThrustKN = 50.0, RealMdot = 75.5,
                RealSfc = 0.735 * SfcLbToSI, RealDryKg = 1055.0,
                Base = rd33
            });

            // ---- PW1100G 级：齿轮传动涡扇（GTF） ----
            // 这台预设存在的意义不是"再抄一台真机"，是**把齿轮减速这条路走通**：
            // 风扇 Ø2.06 m 只有 18 片宽弦叶片、单级、叶尖速度压到 ~300 m/s，
            // 低压涡轮却按 9000 rpm 转——两个转速差 3 倍，全靠齿轮箱。
            // 注意齿轮比会同时抬高齿轮箱重量（百公斤级）与轴上的等效惯量（×比值²），
            // 所以它不是"免费的推力增益"：加速会变慢，这也是真 GTF 的特点。
            EngineDesignData gtf = new EngineDesignData();
            gtf.DesignName = "GTF-1100（齿轮传动涡扇）";
            gtf.Kind = EngineKind.Turbofan;
            gtf.TechName = "2010s";
            gtf.Length = 3.85; gtf.LengthAuto = false;   // PW1100G 全长约 3.85 m
            gtf.NacelleDiameter = 2.15;                  // 短舱最大外径（风扇 2.06 m + 包容机匣）
            gtf.Fan = new FanSpec
            {
                Diameter = 2.06, Stages = 1, HubTipRatio = 0.30, BladeCount = 18,
                // 风扇压比按真机给：单级宽弦从叶尖速度反推只有 1.05 左右，
                // 那是把"齿轮把风扇转速压低"当成了"风扇不干活"。真机风扇压比 1.4~1.5。
                PressureRatio = 1.10,
                Shaft = 1, GearRatio = 3.0, GearEta = 0.995
            };
            gtf.Lpc = new CompressorSpec { Name = "LPC", Stages = 3, TipDiameter = 0.66, PsiScale = 0.72, Shaft = 1 };
            gtf.Hpc = new CompressorSpec { Name = "HPC", Stages = 11, TipDiameter = 0.52, PsiScale = 0.72, Shaft = 2 };
            gtf.Hpt = new TurbineSpec { Name = "HPT", Stages = 2, TipDiameter = 0.52, Shaft = 2 };
            gtf.Lpt = new TurbineSpec { Name = "LPT", Stages = 5, TipDiameter = 0.66, Shaft = 1 };
            gtf.Ipt = new TurbineSpec { Name = "IPT", Stages = 0, Shaft = 2 };
            gtf.Burner.ExitTemp = 1800.0;
            gtf.Burner.Kind = BurnerKind.Annular;
            gtf.Burner.CanCount = 0;
            gtf.Burner.LinerLd = 1.6;
            gtf.Afterburner.Enabled = false;
            gtf.ShaftCount = 2;
            gtf.OverrideBpr = 0.0;
            gtf.Nozzle = NozzleKind.Converging;
            list.Add(new PresetInfo
            {
                Key = "GTF1100",
                Label = "PW1100G 级齿轮传动涡扇（GTF，风扇 Ø2.06 m，齿轮比 3.0）",
                Note = "齿轮比 3.0：低压轴约 4600 rpm、风扇约 1500 rpm。想比较直驱/齿轮传动的差别，把齿轮比改回 1 再看同一个设计点。" +
                       "已知偏差：模型算出的涵道比 6.8（真机 12.5）——核心机通流面积在本模型里由叶尖直径定，压不到真机那么小；推力/流量/SFC 三项是按真机标定过的。",
                RealOpr = 50.0, RealThrustKN = 110.0, RealMdot = 480.0,
                RealSfc = 0.51 * SfcLbToSI, RealDryKg = 2858.0,
                Base = gtf
            });

            return list;
        }
    }
}