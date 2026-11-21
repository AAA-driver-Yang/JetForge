using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 逐轴转矩平衡：J·dω/dt = τ_涡轮 − τ_压气机 − τ_附件。
    ///
    /// 为什么要把功率换成转矩再积分，而不是"按目标转速做指数逼近"：
    /// 指数逼近假设"越接近目标越慢"是因为时间常数，而真机是因为**剩余扭矩本身在减小**。
    /// 两者在开环时长得像，但在三处会分岔：
    ///   · 半油门推到底：功率差与(目标−当前)不是线性关系，指数律会先快后慢得不真实；
    ///   · 双轴机：风扇轴与核心机轴各带各的惯量，核心机先起转、风扇后跟，一个等效转子做不出来；
    ///   · 附件与引气：它们只吃功率、不认目标转速，指数律里没地方放。
    ///
    /// 这里只做一件事：把解算器已经算出来的那些功率，按轴分好、换成转矩、积分一步。
    /// 不设"目标转速"——目标转速是燃油计划的事，不是转子的事。
    /// </summary>
    public sealed class ShaftTorque
    {
        public int Id;
        public string Name = "N1";
        /// <summary>转动惯量 kg·m²（只含挂在这根轴上的转子）。</summary>
        public double Inertia;
        /// <summary>设计转速 rad/s。</summary>
        public double OmegaDesign;
        /// <summary>当前转速 rad/s。</summary>
        public double Omega;

        /// <summary>涡轮给这根轴的功率 W。</summary>
        public double TurbinePower;
        /// <summary>压气机/风扇从这根轴取走的功率 W。</summary>
        public double LoadPower;
        /// <summary>附件（油泵、发电机、液压泵）取走的功率 W。</summary>
        public double AccessoryPower;
        /// <summary>
        /// 机械效率（轴承、传动）。
        ///
        /// 缺省 1.0，不是 0.99——**因为设计点里没有单独的机械损失账目**：求解器是按
        /// "涡轮功 = 压气机功"配平的，若在这里再乘 0.99，所有发动机都会永远以 1% 的速度掉转，
        /// 而那个 1% 是我们自己凭空造的。要表达轴承损失就显式给 0.99（或走附件功率），
        /// 不要让它变成藏在缺省值里的系统性偏差。
        /// </summary>
        public double MechanicalEfficiency = 1.0;

        public double Fraction
        {
            get { return OmegaDesign > 1e-9 ? Omega / OmegaDesign : 0.0; }
            set { Omega = Math.Max(OmegaDesign * value, 1e-6); }
        }

        /// <summary>轴得到的总功率（已扣机械效率）。负 = 在掉转。</summary>
        public double NetPower
        {
            get { return TurbinePower * MechanicalEfficiency - LoadPower - AccessoryPower; }
        }

        /// <summary>净转矩 N·m。转速很低时除以 ω 会炸，所以给 ω 一个下限。</summary>
        public double NetTorque
        {
            get { return NetPower / Math.Max(Omega, 1.0); }
        }

        /// <summary>角加速度 rad/s²。</summary>
        public double AngularAcceleration
        {
            get { return Inertia > 1e-9 ? NetTorque / Inertia : 0.0; }
        }

        /// <summary>每分钟转速。</summary>
        public double Rpm { get { return Omega * 60.0 / (2.0 * Math.PI); } }

        /// <summary>积分一步，返回 Δω（rad/s）。</summary>
        public double Step(double dt)
        {
            if (dt <= 0.0 || Inertia <= 1e-9) return 0.0;
            double dw = AngularAcceleration * dt;
            double before = Omega;
            Omega = Math.Max(Omega + dw, OmegaDesign * TorqueBalance.MinFraction);
            if (Omega > OmegaDesign * TorqueBalance.MaxFraction) Omega = OmegaDesign * TorqueBalance.MaxFraction;
            return Omega - before;
        }

        /// <summary>这根轴是不是已经在平衡点上（|τ| 相对转矩的千分之一以内）。</summary>
        public bool Balanced(double tolerancePct = 0.1)
        {
            double scale = Math.Abs(TurbinePower * MechanicalEfficiency) + Math.Abs(LoadPower) + 1e-9;
            return Math.Abs(NetPower) <= scale * tolerancePct / 100.0;
        }

        public string Describe()
        {
            return string.Format("{0} {1,5:F2}%｜J {2,7:F1} kg·m²｜涡轮 {3,8:F1} kW｜负载 {4,8:F1} kW｜附件 {5,5:F1} kW｜净转矩 {6,8:F1} N·m｜dω/dt {7,7:F2} rad/s²",
                Name, Fraction * 100.0, Inertia, TurbinePower / 1000.0, LoadPower / 1000.0,
                AccessoryPower / 1000.0, NetTorque, AngularAcceleration);
        }
    }

    /// <summary>把解算器给出的功率按轴分好，并推动整组轴。</summary>
    public static class TorqueBalance
    {
        public const double MinFraction = 0.02;
        public const double MaxFraction = 1.06;

        /// <summary>
        /// 按设计数据与一次解算结果建出逐轴状态。
        ///
        /// 轴的分工沿用 <see cref="ShaftSet"/>：风扇与低压涡轮在风扇轴，高压压气机与高压涡轮在核心轴，
        /// 中压涡轮跟着它服务的压气机走（现在的设计数据里 IPT 挂核心轴）。
        /// 没有解算结果时只建"结构"（功率全 0），转速按传进来的分数给。
        /// </summary>
        public static List<ShaftTorque> Build(EngineDesignData d, OffDesignResult r, Func<int, double> fraction)
        {
            List<ShaftDefinition> defs;
            List<ShaftTorque> rows = Rows(d, fraction, out defs);
            if (r == null || !r.Ok) return rows;
            for (int i = 0; i < rows.Count && i < defs.Count; i++) Assign(d, defs[i], r, rows[i]);
            return rows;
        }

        /// <summary>
        /// 用设计点结果建逐轴状态。
        ///
        /// 设计点按定义就是稳态：涡轮给的功恰好等于压气机要的功。所以这里拿 `W*` 灌进两侧，
        /// 算出来的净转矩应当≈0——这不是"顺手写的断言"，而是**设计点本身是否自洽**的检验：
        /// 如果哪天有人只改了涡轮那一侧的功，这条会立刻歪。
        /// （第一版按轴号硬猜"1 号轴背风扇、其余背压气机"，设计点上 N1 多 2022 kW、N2 少 2022 kW，
        ///   正好把"低压压气机挂在风扇轴上"这件事丢了。现在按轴号字段逐部件分，不再靠猜。）
        /// </summary>
        public static List<ShaftTorque> Build(EngineDesignData d, CycleResult c, Func<int, double> fraction)
        {
            List<ShaftDefinition> defs;
            List<ShaftTorque> rows = Rows(d, fraction, out defs);
            if (c == null) return rows;
            for (int i = 0; i < rows.Count && i < defs.Count; i++) Assign(d, defs[i], c, rows[i]);
            return rows;
        }

        /// <summary>只建结构：轴、惯量、设计转速，功率留 0。</summary>
        private static List<ShaftTorque> Rows(EngineDesignData d, Func<int, double> fraction, out List<ShaftDefinition> defs)
        {
            defs = new List<ShaftDefinition>();
            List<ShaftTorque> rows = new List<ShaftTorque>();
            if (d == null) return rows;

            ShaftSet set = null;
            try { set = ShaftSet.Build(d); }
            catch { set = null; }

            if (set == null || set.Count == 0)
            {
                // 没有轴表就退化成"一根等效轴"：物理还是对的，只是少了双轴的先后。
                ShaftDefinition only = new ShaftDefinition();
                only.Id = 0;
                only.Name = "N1";
                only.Inertia = SpoolDynamics.Inertia(d);
                only.OmegaDesign = SpoolDynamics.DesignOmega(d);
                defs.Add(only);
                rows.Add(Make(only, fraction));
                return rows;
            }

            for (int i = 0; i < set.Defs.Count; i++)
            {
                defs.Add(set.Defs[i]);
                rows.Add(Make(set.Defs[i], fraction));
            }
            return rows;
        }

        private static ShaftTorque Make(ShaftDefinition def, Func<int, double> fraction)
        {
            ShaftTorque t = new ShaftTorque();
            t.Id = def.Id;
            t.Name = def.Name;
            t.Inertia = def.Inertia > 1e-6 ? def.Inertia : 1.0;
            t.OmegaDesign = def.OmegaDesign > 1e-6 ? def.OmegaDesign : 1.0;
            t.Fraction = fraction != null ? fraction(def.Id) : 1.0;
            t.AccessoryPower = Math.Max(def.AccessoryLoad, 0.0);
            return t;
        }

        /// <summary>
        /// 把一次解算结果拆到轴上——按每个部件的 `Shaft` 字段，不按轴号猜。
        ///
        /// 用 `Cap*`（涡轮"能提供"的功）而不是 `W*`（"必须提供"的功）：
        /// 前者是当前燃气条件与转速下真能拿出来的扭矩，后者是压气机要的。
        /// 两者之差就是加速扭矩——这正是 J·dω/dt 的右端。
        /// </summary>
        private static void Assign(EngineDesignData d, ShaftDefinition def, OffDesignResult r, ShaftTorque t)
        {
            double turb = 0.0, load = 0.0;
            if (d.Hpt != null && d.Hpt.Stages > 0 && d.Hpt.Shaft == def.Id) turb += r.CapHpt;
            if (d.Ipt != null && d.Ipt.Stages > 0 && d.Ipt.Shaft == def.Id) turb += r.CapIpt;
            if (d.Lpt != null && d.Lpt.Stages > 0 && d.Lpt.Shaft == def.Id) turb += r.CapLpt;
            if (d.HasFan && d.Fan.Shaft == def.Id) load += r.WFan;
            // 非设计点结果只给"压气机总功"，没拆 LPC/HPC：挂在风扇轴上的增压级只能算作风扇的负载一部分，
            // 所以这里把 WComp 记在带 HPC 的那根轴上（没有 HPC 时记在最后一根轴上）。
            bool hasHpc = d.Hpc != null && d.Hpc.Stages > 0;
            if (hasHpc)
            {
                if (d.Hpc.Shaft == def.Id) load += r.WComp;
            }
            else if (!(d.HasFan && d.Fan.Shaft == def.Id)) load += r.WComp;
            t.TurbinePower = turb;
            t.LoadPower = load;
        }

        /// <summary>设计点的拆法：每台压气机、每台涡轮的功都还在，不用猜。</summary>
        private static void Assign(EngineDesignData d, ShaftDefinition def, CycleResult c, ShaftTorque t)
        {
            double turb = 0.0, load = 0.0;
            if (d.Hpt != null && d.Hpt.Stages > 0 && d.Hpt.Shaft == def.Id) turb += c.WTurbHpt;
            if (d.Ipt != null && d.Ipt.Stages > 0 && d.Ipt.Shaft == def.Id) turb += c.WTurbIpt;
            if (d.Lpt != null && d.Lpt.Stages > 0 && d.Lpt.Shaft == def.Id) turb += c.WTurbLpt;
            if (d.HasFan && d.Fan.Shaft == def.Id) load += c.WFan;
            if (d.Lpc != null && d.Lpc.Stages > 0 && d.Lpc.Shaft == def.Id) load += c.WLpc;
            if (d.Hpc != null && d.Hpc.Stages > 0 && d.Hpc.Shaft == def.Id) load += c.WHpc;
            t.TurbinePower = turb;
            t.LoadPower = load;
        }

        /// <summary>
        /// 整组推进一步。返回每根轴的 Δω（rad/s），顺序与输入一致。
        /// 每根轴各积分各的——这就是"核心机先起转"的来源，不需要额外写一条规则。
        /// </summary>
        public static double[] Step(List<ShaftTorque> shafts, double dt)
        {
            if (shafts == null) return new double[0];
            double[] dw = new double[shafts.Count];
            for (int i = 0; i < shafts.Count; i++) dw[i] = shafts[i].Step(dt);
            return dw;
        }

        /// <summary>所有轴都平衡了才算"稳态"。任何一根还在加减速，整机就还没定。</summary>
        public static bool AllBalanced(List<ShaftTorque> shafts, double tolerancePct)
        {
            if (shafts == null || shafts.Count == 0) return false;
            for (int i = 0; i < shafts.Count; i++) if (!shafts[i].Balanced(tolerancePct)) return false;
            return true;
        }

        /// <summary>一行一行写出来。自检与面板读数共用。</summary>
        public static string Describe(List<ShaftTorque> shafts)
        {
            if (shafts == null || shafts.Count == 0) return "转矩平衡：无轴";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < shafts.Count; i++)
            {
                if (i > 0) sb.Append("\n  ");
                sb.Append(shafts[i].Describe());
            }
            return sb.ToString();
        }
    }
}