using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 一根轴的定义：叫什么（N1/N2/N3）、设计转速、惯量、上面挂了哪些转子与涡轮、附件负载。
    ///
    /// 以前整机只有一个"spool"标量，多轴机也被压成一根轴——于是"风扇和核心机谁先跟上"
    /// 这种玩家一眼能看出来的事，模型里根本不存在。这一层把轴变成有名字的对象：
    /// 惯量按这根轴上真实挂了什么算，转速上限按这根轴上最严的叶尖马赫定。
    /// </summary>
    public sealed class ShaftDefinition
    {
        public int Id;
        public string Name = "N1";
        public double RpmDesign;
        public double OmegaDesign;
        public double Inertia;
        /// <summary>附件机匣从这根轴上拿走的功率（W）。真机附件齿轮箱挂在高压轴上。</summary>
        public double AccessoryLoad;
        public List<string> Rotors = new List<string>();
        public List<string> Turbines = new List<string>();

        public string Describe()
        {
            return string.Format("{0}：{1:F0} rpm · J={2:F2} kg·m² · 转子[{3}] · 涡轮[{4}]",
                Name, RpmDesign, Inertia,
                Rotors.Count == 0 ? "-" : string.Join("/", Rotors.ToArray()),
                Turbines.Count == 0 ? "-" : string.Join("/", Turbines.ToArray()));
        }
    }

    /// <summary>
    /// 一根轴的运行状态。物理只有一条：`J·ω·dω/dt = P_涡轮 − P_负载`。
    /// 这是**逐轴**写的，不再把三根轴折成一个等效转子。
    /// </summary>
    public sealed class ShaftState
    {
        public int Id;
        public string Name = "N1";
        public double Omega;
        public double OmegaDesign;
        public double Inertia;
        public double TurbinePower;
        public double LoadPower;
        /// <summary>附件负载（从负载里扣，单独留着是为了界面能说明"这 3% 去哪了"）。</summary>
        public double AccessoryPower;

        public const double MinFraction = 0.02;
        public const double MaxFraction = 1.06;

        public double Rpm { get { return Omega * 60.0 / (2.0 * Math.PI); } }

        public double SpeedFraction
        {
            get { return OmegaDesign > 1e-9 ? Omega / OmegaDesign : 0.0; }
            set { Omega = Math.Max(OmegaDesign * value, 1.0); }
        }

        public double Percent { get { return SpeedFraction * 100.0; } }

        /// <summary>这一刻轴上多出来的功率 W（负 = 在掉转）。</summary>
        public double SurplusW { get { return TurbinePower - LoadPower - AccessoryPower; } }

        public double NetTorque
        {
            get { return SurplusW / Math.Max(Omega, 1.0); }
        }

        /// <summary>灌入本帧求解出来的功率（设计点/非设计点都从这里进）。</summary>
        public void UpdatePower(double turbineW, double loadW)
        {
            TurbinePower = Math.Max(turbineW, 0.0);
            LoadPower = Math.Max(loadW, 0.0);
        }

        /// <summary>
        /// 积分一步：J·ω·(dω/dt) = P_net。
        /// 返回这一步的 dω（rad/s），测试要拿它核对"多给功率就一定加速"。
        /// </summary>
        public double Integrate(double dt)
        {
            if (dt <= 0.0 || Inertia <= 1e-9) return 0.0;
            double w = Math.Max(Omega, 1.0);
            double dw = SurplusW / (Inertia * w) * dt;
            Omega = Math.Max(Omega + dw, OmegaDesign * MinFraction);
            if (Omega > OmegaDesign * MaxFraction) Omega = OmegaDesign * MaxFraction;
            return dw;
        }

        /// <summary>
        /// 按"这根轴自己的惯量与剩余功率"朝目标转速逼近一步。
        /// 时间常数 τ = J·ω²/(2·P_net)：惯量大、功率小就是慢慢来——这正是多轴机
        /// "核心机先上、风扇后跟"的来源，而不是给每根轴各拍一个数字。
        /// 没有功率信息（P_net≈0）时退回 1.0 s 的保守滞后，保证不发散。
        /// </summary>
        public double StepToward(double targetFraction, double dt)
        {
            if (dt <= 0.0) return 0.0;
            double target = Math.Min(Math.Max(targetFraction, MinFraction), MaxFraction);
            double cur = SpeedFraction;
            double err = target - cur;
            if (Math.Abs(err) < 1e-5) { SpeedFraction = target; return 0.0; }

            double p = Math.Abs(SurplusW);
            double tau;
            if (p > 1.0 && Inertia > 1e-6)
                tau = 0.5 * Inertia * Omega * Omega / p;
            else
                tau = 1.0;
            tau = Math.Min(Math.Max(tau, 0.15), 6.0);

            double alpha = 1.0 - Math.Exp(-dt / tau);
            double before = Omega;
            double wanted = cur + err * alpha;

            // 油门要不到它没有的功率：这一帧的转速变化受净功率限制。
            // 涡轮给不够的时候，慢车以上再踩油门也是掉转——以前的写法只按目标比例逼近，
            // 结果"油不够也会升速"，那是假的。
            if (p > 1.0 && Inertia > 1e-6)
            {
                double rate = SurplusW / (Inertia * Math.Max(Omega, 1.0));       // rad/s²
                double reach = cur + rate * dt / Math.Max(OmegaDesign, 1e-9);
                wanted = err > 0.0 ? Math.Min(wanted, reach) : Math.Max(wanted, reach);
            }

            SpeedFraction = Math.Min(Math.Max(wanted, MinFraction), MaxFraction);
            return Omega - before;
        }
    }

    /// <summary>
    /// 整套轴：定义 + 状态。设计点/非设计点把功率灌进来，瞬态从这里往下走。
    /// </summary>
    public sealed class ShaftSet
    {
        public List<ShaftDefinition> Defs = new List<ShaftDefinition>();
        public Dictionary<int, ShaftState> States = new Dictionary<int, ShaftState>();

        public int Count { get { return Defs.Count; } }

        public ShaftState this[int shaft]
        {
            get
            {
                ShaftState s;
                return States.TryGetValue(shaft, out s) ? s : null;
            }
        }

        /// <summary>
        /// 按设计建轴系。惯量只算"这根轴上真挂着的转子"：
        /// 风扇算在风扇轴上，LPC 算在它自己的轴上，涡轮同理；主轴质量按转子质量占比分。
        /// </summary>
        public static ShaftSet Build(EngineDesignData d)
        {
            ShaftSet set = new ShaftSet();
            if (d == null) return set;
            MassBreakdown m;
            try { m = MassCost.Estimate(d); }
            catch { m = null; }

            Dictionary<int, double> rpm = Cycle.DesignShaftSpeeds(d, Gas.T0);
            List<int> shafts = new List<int>(rpm.Keys);
            shafts.Sort();
            Dictionary<int, string> names = Cycle.ShaftNames(d, shafts);

            double rFan = d.HasFan ? Math.Max(d.Fan.Diameter * 0.5, 0.02) : Math.Max(d.InletDiameter * 0.5, 0.02);
            double rLpc = d.Lpc != null && d.Lpc.Stages > 0 ? Math.Max(d.Lpc.MaxTip() * 0.5, 0.02) : 0.0;
            double rHpc = d.Hpc != null && d.Hpc.Stages > 0 ? Math.Max(d.Hpc.MaxTip() * 0.5, 0.02) : 0.0;
            double rHpt = d.Hpt != null && d.Hpt.Stages > 0 ? Math.Max(d.Hpt.MaxTip() * 0.5, 0.02) : 0.0;
            double rIpt = d.Ipt != null && d.Ipt.Stages > 0 ? Math.Max(d.Ipt.MaxTip() * 0.5, 0.02) : 0.0;
            double rLpt = d.Lpt != null && d.Lpt.Stages > 0 ? Math.Max(d.Lpt.MaxTip() * 0.5, 0.02) : 0.0;

            double rotorMassTotal = m != null ? Math.Max(m.Fan + m.Compressor + m.Turbine, 1e-6) : 1.0;

            foreach (int id in shafts)
            {
                ShaftDefinition def = new ShaftDefinition();
                def.Id = id;
                def.Name = names.ContainsKey(id) ? names[id] : "N" + id;
                double r;
                rpm.TryGetValue(id, out r);
                def.RpmDesign = r;
                def.OmegaDesign = r * 2.0 * Math.PI / 60.0;

                double j = 0.0, massOnShaft = 0.0;
                Action<string, double, double> attach = (part, radius, factor) =>
                {
                    double mass = 0.0;
                    if (m != null && m.RotorParts.ContainsKey(part)) mass = m.RotorParts[part];
                    if (mass <= 0.0 || radius <= 0.0) return;
                    j += mass * Sq(factor * radius);
                    massOnShaft += mass;
                };

                // 风扇若经齿轮减速，折算到低压轴的惯量要乘齿轮比的平方
                // （同一份转动惯量，转速降一半，等效惯量就要乘 4）——GTF 的低压轴
                // 响应比直驱慢，原因就在这里，不是参数没调好。
                double fanGear = (d.HasFan && d.Fan.GearRatio > 1.0) ? d.Fan.GearRatio : 1.0;
                if (d.HasFan && d.Fan.Shaft == id) { attach("FAN", rFan, 0.45 * fanGear); def.Rotors.Add(fanGear > 1.0001 ? "风扇（齿轮减速）" : "风扇"); }
                // 齿轮箱自己也带惯量：太阳轮轴随低压轴转，行星架随风扇转。
                // 行星架那部分折算到低压轴还要除以 r²，比主体小一个数量级，这里只算主体——
                // 也就是说"齿轮让低压轴变钝"这件事，风扇转子的 r² 是主项，齿轮箱是次项。
                if (d.HasFan && d.Fan.Shaft == id)
                    j += GearboxInertia(d, m);
                if (d.Lpc != null && d.Lpc.Stages > 0 && d.Lpc.Shaft == id) { attach("LPC", rLpc, 0.55); def.Rotors.Add("低压压气机"); }
                if (d.Hpc != null && d.Hpc.Stages > 0 && d.Hpc.Shaft == id) { attach("HPC", rHpc, 0.55); def.Rotors.Add("高压压气机"); }
                if (d.Hpt != null && d.Hpt.Stages > 0 && d.Hpt.Shaft == id) { attach("HPT", rHpt, 0.55); def.Turbines.Add("高压涡轮"); }
                if (d.Ipt != null && d.Ipt.Stages > 0 && d.Ipt.Shaft == id) { attach("IPT", rIpt, 0.55); def.Turbines.Add("中压涡轮"); }
                if (d.Lpt != null && d.Lpt.Stages > 0 && d.Lpt.Shaft == id) { attach("LPT", rLpt, 0.55); def.Turbines.Add("低压涡轮"); }

                if (m != null && m.Shaft > 0.0 && rotorMassTotal > 0.0)
                {
                    double share = massOnShaft / rotorMassTotal;
                    double rShaft = Math.Max(Math.Min(rLpc > 0.0 ? rLpc : rHpc, rHpt > 0.0 ? rHpt : rLpt), 0.02);
                    j += share * m.Shaft * Sq(0.60 * rShaft);
                }
                def.Inertia = Math.Max(j, 0.02);

                set.Defs.Add(def);

                ShaftState st = new ShaftState();
                st.Id = id;
                st.Name = def.Name;
                st.OmegaDesign = def.OmegaDesign > 1.0 ? def.OmegaDesign : 1.0;
                st.Inertia = def.Inertia;
                st.Omega = st.OmegaDesign;
                set.States[id] = st;
            }

            // 附件齿轮箱挂在驱动高压压气机的那根轴上（真机如此）；找不到就挂最后一根。
            int accShaft = shafts.Count > 0 ? shafts[shafts.Count - 1] : 1;
            if (d.Hpc != null && d.Hpc.Stages > 0) accShaft = d.Hpc.Shaft;
            ShaftDefinition accDef = null;
            for (int i = 0; i < set.Defs.Count; i++) if (set.Defs[i].Id == accShaft) accDef = set.Defs[i];
            if (accDef != null) accDef.AccessoryLoad = 0.0;   // 具体瓦数等设计点功率出来再填

            return set;
        }

        private static double Sq(double v) { return v * v; }

        /// <summary>
        /// 风扇减速齿轮箱折算到低压轴的转动惯量（kg·m²）。
        /// 直驱机型返回 0——老存档与直驱读数一个数都不会变。
        ///
        /// 口径说明：齿轮箱的质量来自 <see cref="MassCost"/>（吃传递扭矩，`40 + 0.0055·风扇kW·比值`），
        /// 这里把它当成一个薄环，回转半径取 0.30 倍风扇半径——行星齿轮的太阳轮与行星架
        /// 都在这个量级上。行星架按风扇转速转，折算到低压轴要除以 r²，所以只算主体；
        /// 齿轮的**柔度**（扭振）仍然没建模，那需要弹簧-质量模型，不在本轮范围内。
        /// </summary>
        public static double GearboxInertia(EngineDesignData d, MassBreakdown m)
        {
            if (d == null || m == null) return 0.0;
            if (!d.HasFan || d.Fan == null || d.Fan.GearRatio <= 1.0001) return 0.0;
            if (m.Gearbox <= 0.0) return 0.0;
            double rFan = Math.Max(d.Fan.Diameter * 0.5, 0.02);
            double rMesh = Math.Max(0.30 * rFan, 0.02);
            return 0.5 * m.Gearbox * Sq(rMesh);
        }

        /// <summary>把设计点的功率灌进各轴（设计点转速 = 100%）。</summary>
        public void SyncDesignPoint(EngineDesignData d, CycleResult r)
        {
            if (d == null || r == null) return;
            foreach (ShaftDefinition def in Defs)
            {
                ShaftState st = this[def.Id];
                if (st == null) continue;
                double turb = 0.0, load = 0.0;
                if (d.Hpt != null && d.Hpt.Stages > 0 && d.Hpt.Shaft == def.Id) turb += r.WTurbHpt;
                if (d.Ipt != null && d.Ipt.Stages > 0 && d.Ipt.Shaft == def.Id) turb += r.WTurbIpt;
                if (d.Lpt != null && d.Lpt.Stages > 0 && d.Lpt.Shaft == def.Id) turb += r.WTurbLpt;
                if (d.HasFan && d.Fan.Shaft == def.Id) load += r.WFan;
                if (d.Lpc != null && d.Lpc.Stages > 0 && d.Lpc.Shaft == def.Id) load += r.WLpc;
                if (d.Hpc != null && d.Hpc.Stages > 0 && d.Hpc.Shaft == def.Id) load += r.WHpc;
                st.UpdatePower(turb, load);
                if (st.OmegaDesign > 1.0) st.SpeedFraction = 1.0;
            }
            // 附件功率按设计涡轮功的固定比例分到高压轴上
            double acc = 0.0;
            if (r.WTurbHpt > 0.0) acc = 0.03 * r.WTurbHpt;
            foreach (ShaftDefinition def in Defs)
            {
                if (def.Turbines.Contains("高压涡轮"))
                {
                    ShaftState st = this[def.Id];
                    if (st != null) st.AccessoryPower = acc;
                }
            }
        }

        /// <summary>最吃力的那根轴（转得最少）。"整机"读数该跟它走。</summary>
        public double GoverningFraction
        {
            get
            {
                double f = 1.0;
                bool any = false;
                foreach (ShaftState s in States.Values)
                {
                    if (!any || s.SpeedFraction < f) { f = s.SpeedFraction; any = true; }
                }
                return any ? f : 1.0;
            }
        }

        /// <summary>逐轴推进一个时间步。目标转速由调用方给（通常是油门命令）。</summary>
        public void StepToward(double targetFraction, double dt)
        {
            foreach (ShaftState s in States.Values) s.StepToward(targetFraction, dt);
        }

        /// <summary>逐轴积分（用当前灌进来的功率）。熄火滑行就靠它。</summary>
        public void Integrate(double dt)
        {
            foreach (ShaftState s in States.Values) s.Integrate(dt);
        }

        /// <summary>各轴转速给 Cycle.SpoolReadout 用的字典（键 = 轴号，值 = %）。</summary>
        public Dictionary<int, double> SpeedPct()
        {
            Dictionary<int, double> o = new Dictionary<int, double>();
            foreach (ShaftState s in States.Values) o[s.Id] = s.Percent;
            return o;
        }

        public string Describe()
        {
            List<string> parts = new List<string>();
            foreach (ShaftState s in States.Values)
                parts.Add(string.Format("{0} {1:F0}%", s.Name, s.Percent));
            return parts.Count == 0 ? "没有轴" : string.Join(" · ", parts.ToArray());
        }
    }
}