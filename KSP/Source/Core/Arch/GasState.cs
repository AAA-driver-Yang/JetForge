using System;

namespace JetForge.Core
{
    /// <summary>这股气要去哪。核心机 / 外涵 / 抽气 / 冷却 / 合流。</summary>
    public enum FlowRole
    {
        Core = 0,
        Bypass = 1,
        Bleed = 2,
        Cooling = 3,
        Mixed = 4,
    }

    /// <summary>状态合格与否。不合格要给得出是哪一类，不能只给一个 false。</summary>
    public enum GasValidity
    {
        Valid = 0,
        NotFinite = 1,
        BadPressure = 2,
        BadTemperature = 3,
        NegativeFlow = 4,
        BadComposition = 5,
        StaticAboveTotal = 6,
    }

    /// <summary>
    /// 一股气在某个测站上的状态。全部 SI：Pa、K、kg/s、m²、m/s。
    ///
    /// 这是设计点、非设计点、瞬态**三条算路共用的唯一口径**。以前每条算路各自
    /// 拿一堆 double 传来传去（tt1/pt1/tt2/pt2…），谁漏了一个字段都没人发现；
    /// 现在一个状态对象带着测站名字走完整条流路，部件只能读输入、只能写输出，
    /// 不许回头改上游那个对象。
    ///
    /// 静参数是"可选"的：只有算到喷管/截面壅塞时才需要。需要的时候调
    /// <see cref="SolveStaticFromArea"/> 由"面积 + 流量 + 总参数"反解出来，
    /// 而不是让每个部件自己写一遍面积-马赫迭代。
    /// </summary>
    public sealed class GasState
    {
        /// <summary>测站名（"0"、"1"、"2"、"3"…，与 CycleResult.T1..T7 的编号一致）。</summary>
        public string Station = "";
        /// <summary>人话名，界面上直接显示（"风扇出口"）。</summary>
        public string Label = "";
        public FlowRole Role = FlowRole.Core;

        /// <summary>总压 Pa。</summary>
        public double TotalPressure;
        /// <summary>总温 K。</summary>
        public double TotalTemperature;
        /// <summary>质量流量 kg/s。</summary>
        public double MassFlow;
        /// <summary>油气比（0 = 纯空气）。它只影响 Cp/γ，不改变"流量守恒"这条。</summary>
        public double FuelAirRatio;

        // ---- 静参数：NaN = 还没算 ----
        public double StaticPressure = double.NaN;
        public double StaticTemperature = double.NaN;
        public double Mach = double.NaN;
        public double Velocity = double.NaN;
        public double FlowArea = double.NaN;

        public double Gamma { get { return Gas.Gamma(TotalTemperature, FuelAirRatio); } }
        public double Cp { get { return Gas.Cp(TotalTemperature, FuelAirRatio); } }
        public double R { get { return Gas.R; } }

        /// <summary>当地音速：用静温（有）否则退回总温。仅用于诊断显示。</summary>
        public double SoundSpeed
        {
            get
            {
                double t = IsFinite(StaticTemperature) && StaticTemperature > 0.0
                    ? StaticTemperature : TotalTemperature;
                return Math.Sqrt(Gamma * Gas.R * Math.Max(t, 1e-3));
            }
        }

        public static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>这个状态能不能往下传。总压/总温必须为正且有限。</summary>
        public bool IsValid
        {
            get
            {
                string why;
                return Validate(out why) == GasValidity.Valid;
            }
        }

        /// <summary>
        /// 不合格的原因。交接文档 A 要求"不许用 0 代表未知"、也不许把坏状态悄悄传下去：
        /// 一个 `TotalPressure = 0` 的出口往下走，压比会算成 0（看着像"压气机不干活"），
        /// 推力算成 0（看着像"设计不行"），而真正的原因在三个部件之前。
        /// 所以合格与否要能说出一句话，而不是一个 bool。
        /// </summary>
        public GasValidity Validate(out string why)
        {
            why = "";
            if (!IsFinite(TotalPressure) || !IsFinite(TotalTemperature) ||
                !IsFinite(MassFlow) || !IsFinite(FuelAirRatio))
            {
                why = string.Format("有 NaN/Inf（pt={0:G4} tt={1:G4} mdot={2:G4} far={3:G4}）",
                    TotalPressure, TotalTemperature, MassFlow, FuelAirRatio);
                return GasValidity.NotFinite;
            }
            if (TotalPressure <= 0.0)
            {
                why = string.Format("总压 {0:G4} Pa 不是正数（压力为 0 的出口往下走会把压比算成 0）", TotalPressure);
                return GasValidity.BadPressure;
            }
            if (TotalTemperature <= 0.0)
            {
                why = string.Format("总温 {0:G4} K 不是正数", TotalTemperature);
                return GasValidity.BadTemperature;
            }
            if (MassFlow < 0.0)
            {
                why = string.Format("流量 {0:G4} kg/s 是负数", MassFlow);
                return GasValidity.NegativeFlow;
            }
            if (FuelAirRatio < 0.0 || FuelAirRatio >= 0.30)
            {
                why = string.Format("油气比 {0:G4} 超出 [0, 0.30)（超过 0.30 不是喷气发动机）", FuelAirRatio);
                return GasValidity.BadComposition;
            }
            if (IsFinite(StaticPressure) && StaticPressure > TotalPressure * 1.000001)
            {
                why = string.Format("静压 {0:G4} Pa 高于总压 {1:G4} Pa（总/静参数混用了）", StaticPressure, TotalPressure);
                return GasValidity.StaticAboveTotal;
            }
            return GasValidity.Valid;
        }

        /// <summary>按测站编号建状态。站名与中文名自动从 <see cref="Stations"/> 里取，
        /// 免得每个调用点手写 "3" 这种魔法字符串。</summary>
        public static GasState FromTotal(int stationId, double pt, double tt, double mdot,
                                         FlowRole role, double far = 0.0)
        {
            GasState s = FromTotal(Stations.Name(stationId), pt, tt, mdot, role, far);
            s.Label = Stations.Label(stationId);
            return s;
        }

        public static GasState FromTotal(string station, double pt, double tt, double mdot,
                                         FlowRole role, double far = 0.0)
        {
            GasState s = new GasState();
            s.Station = station;
            s.TotalPressure = pt;
            s.TotalTemperature = tt;
            s.MassFlow = mdot;
            s.FuelAirRatio = far;
            s.Role = role;
            return s;
        }

        public GasState Clone()
        {
            GasState c = new GasState();
            c.Station = Station;
            c.Label = Label;
            c.Role = Role;
            c.TotalPressure = TotalPressure;
            c.TotalTemperature = TotalTemperature;
            c.MassFlow = MassFlow;
            c.FuelAirRatio = FuelAirRatio;
            c.StaticPressure = StaticPressure;
            c.StaticTemperature = StaticTemperature;
            c.Mach = Mach;
            c.Velocity = Velocity;
            c.FlowArea = FlowArea;
            return c;
        }

        /// <summary>
        /// 给定截面面积，由"面积 + 流量 + 总参数"反解马赫数与静参数。
        /// 面积-流量关系是单调的（亚声速支），二分 60 次足够到 1e-18。
        /// 流量为 0 时不报错，面积与马赫留 NaN。
        /// </summary>
        public void SolveStaticFromArea(double area)
        {
            FlowArea = area;
            if (!(area > 1e-9) || !(MassFlow > 1e-12) || !(TotalPressure > 0.0)) return;
            double g = Gamma;
            double coef = area * TotalPressure / Math.Sqrt(TotalTemperature) * Math.Sqrt(g / Gas.R);
            double lo = 1e-6, hi = 1.0;
            double e = (g + 1.0) / (2.0 * (g - 1.0));
            for (int i = 0; i < 80; i++)
            {
                double mid = 0.5 * (lo + hi);
                double cap = coef * mid * Math.Pow(1.0 + 0.5 * (g - 1.0) * mid * mid, -e);
                if (cap < MassFlow) lo = mid; else hi = mid;
            }
            double m = 0.5 * (lo + hi);
            Mach = m;
            double tr = 1.0 / (1.0 + 0.5 * (g - 1.0) * m * m);
            StaticTemperature = TotalTemperature * tr;
            StaticPressure = TotalPressure * Math.Pow(tr, g / (g - 1.0));
            Velocity = m * Math.Sqrt(g * Gas.R * Math.Max(StaticTemperature, 1e-3));
        }

        /// <summary>这个截面在当前总参数下的壅塞通过量（Ma=1）。</summary>
        public double ChokedMassFlow(double area)
        {
            return Gas.ChokedFlow(area, TotalPressure, TotalTemperature, Gamma);
        }

        /// <summary>测站表里的一行。自检与日志直接用，不另写格式化。</summary>
        public string Describe()
        {
            return string.Format("{0}{1}  P={2:F0}Pa T={3:F1}K mdot={4:F2}kg/s{5}",
                string.IsNullOrEmpty(Station) ? "?" : Station,
                string.IsNullOrEmpty(Label) ? "" : "(" + Label + ")",
                TotalPressure, TotalTemperature, MassFlow,
                FuelAirRatio > 0.0 ? string.Format(" f={0:F4}", FuelAirRatio) : "");
        }
    }

    /// <summary>
    /// 测站编号。沿用 CycleResult.T1..T7 那套老编号，避免"新表一套号、旧读数另一套号"。
    /// 旁路与喷管喉道/出口在旧编号里没有位置，单独占 61/62/8/9。
    /// </summary>
    public static class Stations
    {
        public const int Ambient = 0;
        public const int IntakeExit = 1;
        public const int FanExit = 2;
        public const int LpcExit = 25;
        public const int HpcExit = 3;
        public const int BurnerExit = 4;
        public const int TurbineExit = 5;
        /// <summary>高压涡轮出口。单列出来是为了让"逐台涡轮"也有自己的测站，
        /// 否则 HPT/IPT 只在 tCur/pCur 两个临时变量里过一遍，谁也看不见中间发生了什么。</summary>
        public const int HptExit = 45;
        public const int IptExit = 46;
        public const int AfterburnerExit = 7;
        public const int NozzleThroat = 8;
        public const int NozzleExit = 9;
        public const int BypassExit = 61;
        public const int BypassNozzleExit = 62;

        public static string Name(int id)
        {
            switch (id)
            {
                case Ambient: return "0";
                case IntakeExit: return "1";
                case FanExit: return "2";
                case LpcExit: return "25";
                case HpcExit: return "3";
                case BurnerExit: return "4";
                case TurbineExit: return "5";
                case HptExit: return "45";
                case IptExit: return "46";
                case AfterburnerExit: return "7";
                case NozzleThroat: return "8";
                case NozzleExit: return "9";
                case BypassExit: return "61";
                case BypassNozzleExit: return "62";
                default: return id.ToString();
            }
        }

        public static string Label(int id)
        {
            switch (id)
            {
                case Ambient: return "来流";
                case IntakeExit: return "进气道出口";
                case FanExit: return "风扇出口";
                case LpcExit: return "低压压气机出口";
                case HpcExit: return "高压压气机出口";
                case BurnerExit: return "燃烧室出口";
                case TurbineExit: return "涡轮出口";
                case HptExit: return "高压涡轮出口";
                case IptExit: return "中压涡轮出口";
                case AfterburnerExit: return "加力出口";
                case NozzleThroat: return "喷管喉道";
                case NozzleExit: return "喷管出口";
                case BypassExit: return "外涵出口";
                case BypassNozzleExit: return "外涵喷口";
                default: return "测站 " + id;
            }
        }
    }
}