using System;
using System.Collections.Generic;
using System.Text;

namespace JetForge.Core
{
    /// <summary>一件装在机上的进气道（零件给过来的最小信息）。</summary>
    public struct IntakeUnit
    {
        public IntakeSpec Spec;
        public double FaceDiameter;   // 这件进气道出口（发动机面）直径
        public string Name;
        /// <summary>这件进气道自己的马赫上限（原版进气道才填；0 = 不限）。超过就退出供气。</summary>
        public double MaxMach;
        /// <summary>
        /// 唇口到发动机面的管道长度（米）。0 = 不按形状扣管道损失。
        /// 原版零件读不到内部管长，就给 0——它的面积已经由 cfg 自己声明了。
        /// </summary>
        public double Length;

        /// <summary>
        /// 这件进气道"在这个马赫上还能不能算一件这么大的进气道"的系数。
        ///
        /// 我们的零件不需要它（几何是定死的，能力随马赫由求解器自己算），但**原版零件需要**：
        /// 原版每个进气道在 cfg 里都带一条 `machCurve`（真正的性能曲线，不是上限），
        /// 比如原版环形进气道 M1→1.0、M2.5→0.45、M3.5→0.1。以前那条曲线被完全无视，
        /// 换成了从 `intakeSpeed` 硬读出来的"马赫上限"——那是个大错，
        /// 详见 <see cref="StockIntakeBridge"/>。null = 全马赫 1.0（我们的零件都是 null）。
        /// </summary>
        public Func<double, double> Supply;

        /// <summary>曲线的指纹，用来判网格缓存该不该重建（Func 本身没法比较）。</summary>
        public string SupplyStamp;

        public IntakeUnit(IntakeSpec spec, double faceDiameter, string name)
        {
            Spec = spec; FaceDiameter = faceDiameter; Name = name; MaxMach = 0.0; Length = 0.0;
            Supply = null; SupplyStamp = "";
        }

        public IntakeUnit(IntakeSpec spec, double faceDiameter, string name, double maxMach)
        {
            Spec = spec; FaceDiameter = faceDiameter; Name = name; MaxMach = maxMach; Length = 0.0;
            Supply = null; SupplyStamp = "";
        }

        public IntakeUnit(IntakeSpec spec, double faceDiameter, string name, double maxMach, double length)
        {
            Spec = spec; FaceDiameter = faceDiameter; Name = name; MaxMach = maxMach; Length = length;
            Supply = null; SupplyStamp = "";
        }

        public IntakeUnit(IntakeSpec spec, double faceDiameter, string name, double maxMach, double length,
                          Func<double, double> supply, string supplyStamp)
        {
            Spec = spec; FaceDiameter = faceDiameter; Name = name; MaxMach = maxMach; Length = length;
            Supply = supply; SupplyStamp = supplyStamp ?? "";
        }

        /// <summary>
        /// 这个马赫上的供气系数。没给曲线 → 1.0（我们自己的零件走这条）。
        /// 夹在 [0, 1.5]：曲线本身是"效率"，不该被当成"凭空多进气"用；
        /// NaN（曲线坏了）按 1.0 处理，别让一个坏节点把整台发动机吞掉。
        /// </summary>
        public double SupplyAt(double mach)
        {
            if (Supply == null) return 1.0;
            double v;
            try { v = Supply(mach); }
            catch { return 1.0; }
            if (double.IsNaN(v)) return 1.0;
            if (v < 0.0) return 0.0;
            if (v > 1.5) return 1.5;
            return v;
        }
    }

    /// <summary>
    /// 一台发动机**实际接在什么进气道后面**。
    ///
    /// 进气道装成独立零件以后，发动机就不能再拿"自己设计里那个 IntakeSpec"当唯一真相了：
    /// 一架飞机可以挂两个 Ø1.2 的进气道去喂一个 Ø0.6 的核心机，也可以一个都不挂
    /// （那就回到发动机自带的那份口径）。
    ///
    /// 这一层只做算术，不碰 Unity：把 N 件进气道的解按流量加权合成一个"等效进气道"，
    /// 再包成 <see cref="IntakeSolution"/> 让老路径（OffDesign / EngineNetwork）原样消费。
    /// 合成口径：
    /// · 面积直接相加（两件 Ø1.2 就是一件 Ø1.697 的捕获面积）；
    /// · 总压恢复与面马赫按流量加权（一件好一件差，喂进去的气按各自流量掺着算）；
    /// · 畸变取最差那件（流场不匀不会因为并联就变匀）；
    /// · 溢流与进气阻力相加（两件都溢就是两份阻力）。
    ///
    /// **解是按工况现算的**：几何（面积、型式）不随高度马赫变，但恢复系数、面马赫、
    /// 溢流、畸变都变。所以这里存的是"零件清单 + 技术代"，每次 <see cref="ToSolution"/>
    /// 都按传进来的 (alt, mach) 重算一遍——一张 0.2 s 建完的性能网格跨 18 个高度、
    /// 14 个马赫，拿单一工况的解去填全表会错得很离谱。
    /// </summary>
    public sealed class IntakeInstall
    {
        public bool Present;                 // 装了独立进气道零件
        public int Count;
        public readonly List<string> Names = new List<string>();

        // ---- 几何（与工况无关，构造时算一次） ----
        public double CaptureArea, ThroatArea, FaceArea;
        public double BleedFrac;
        public int DistinctKinds;
        public IntakeKind UniformKind = IntakeKind.Axial;

        // ---- 当前工况下的解（由 Refresh 填） ----
        public double Altitude, Mach;
        public double Tpr = 1.0, Pt2, Tt2, FaceMach;
        public double CapturedMassFlow, AvailableMassFlow, SpillageMassFlow, BleedMassFlow, InletDrag;
        public double Distortion = 0.02;
        public double CrossflowDeg;
        public bool ThroatChoked, Spilling, Started = true, Valid;
        public readonly List<string> Warnings = new List<string>();

        /// <summary>
        /// 发动机这一刻需要多少空气（按发动机面面积 × 面马赫算）。
        ///
        /// **这是多件进气道那笔账的关键**：以前每件进气道都按"我要喂饱整台发动机"算，
        /// 于是装三件就有三份满流量的进气道，加起来三倍——推力跟着乘三。
        /// 物理上发动机面就那么一块，三件进气道是**分着供**这一份需求，多出来的溢流掉。
        /// </summary>
        public double DemandMassFlow;

        private double _sumThroatCap;      // Σ 各件喉道的壅塞能力（kg/s）
        private double _sumCaptured;       // Σ 各件的摄取量（单件时就是解本身）
        private double _sumSpill;          // Σ 各件溢流
        private double _sumDrag;           // Σ 各件进气阻力
        private double _maxRequired;       // 各件"喂饱这台发动机"的需求里最大的那份（多件时的上限）
        private double _maxLegacy;         // 各件按老口径（喉道+面马赫）算出来的流量里最大的那份

        private readonly List<IntakeUnit> _units = new List<IntakeUnit>();
        private readonly List<string> _names = new List<string>();
        private TechLevel _tech;
        private string _signature = "";

        /// <summary>"什么都没装"：调用点拿它当缺省，present=false 时上层走发动机自带口径。</summary>
        public static readonly IntakeInstall None = new IntakeInstall();

        /// <summary>
        /// 把若干件进气道合成一个。
        /// <paramref name="units"/> 为空 = 没装独立零件（返回 <see cref="None"/>）。
        /// </summary>
        public static IntakeInstall Aggregate(IList<IntakeUnit> units, TechLevel tech, double alt, double mach)
        {
            if (units == null || units.Count == 0) return None;

            IntakeInstall ins = new IntakeInstall();
            ins.Present = true;
            ins.Count = units.Count;
            ins._tech = tech;

            bool firstKind = true;
            StringBuilder sig = new StringBuilder();
            for (int i = 0; i < units.Count; i++)
            {
                IntakeUnit u = units[i];
                IntakeUnit copy = new IntakeUnit(u.Spec != null ? u.Spec.Clone() : new IntakeSpec(),
                                                u.FaceDiameter,
                                                string.IsNullOrEmpty(u.Name) ? string.Format(Loc.T("进气道 {0}", "Intake {0}"), i + 1) : u.Name,
                                                u.MaxMach, u.Length, u.Supply, u.SupplyStamp);
                ins._units.Add(copy);
                ins._names.Add(copy.Name);

                if (firstKind) { ins.UniformKind = copy.Spec.Kind; firstKind = false; }
                else if (copy.Spec.Kind != ins.UniformKind) { ins.DistinctKinds++; }
                else { /* 同型式 */ }
                sig.Append(copy.Spec.Kind).Append('/').Append(copy.FaceDiameter.ToString("R")).Append('/')
                   .Append(copy.Spec.CaptureDiameter.ToString("R")).Append('/')
                   .Append(copy.Spec.ThroatRatio.ToString("R")).Append('/')
                   .Append(copy.Spec.FaceMachDesign.ToString("R")).Append('/')
                   .Append(copy.Spec.BleedFrac.ToString("R")).Append('/')
                   .Append(copy.Spec.LipRadius.ToString("R")).Append('/')
                   .Append(copy.Spec.DuctBendDeg.ToString("R")).Append('/')
                   .Append(copy.Spec.AlphaDeg.ToString("R")).Append('/')
                   .Append(copy.Spec.BetaDeg.ToString("R")).Append('/')
                   .Append(copy.Spec.Model).Append('/')
                   // 型线必须进签名：自定义件的捕获/喉道/弯曲在 Spec 里是 0（都由型线量出来），
                   // 只看上面那几个字段的话，两条形状完全不同的气道会算出**同一个签名**，
                   // 于是共用同一张性能网格——玩家改了形状，推力一点不动，而且没有任何报错。
                   .Append(IntakeSpecCodec.ProfileToString(copy.Spec.Shape)).Append('/')
                   .Append(copy.MaxMach.ToString("R")).Append('/')
                   .Append(copy.SupplyStamp ?? "").Append(';');
            }
            ins._signature = sig.ToString();

            ins.Refresh(alt, mach);
            return ins;
        }

        /// <summary>
        /// 网格缓存的键：同型号发动机 + 同几何进气道才该共用同一张表。
        /// 不含工况——表本身就是跨工况的。
        /// </summary>
        public string Signature { get { return Present ? _signature : ""; } }

        /// <summary>按给定工况重算这一组进气道的合计解。</summary>
        public void Refresh(double alt, double mach)
        {
            Altitude = alt; Mach = mach;
            Names.Clear(); Names.AddRange(_names);

            CaptureArea = 0.0; ThroatArea = 0.0; FaceArea = 0.0;
            CapturedMassFlow = 0.0; AvailableMassFlow = 0.0; SpillageMassFlow = 0.0;
            BleedMassFlow = 0.0; InletDrag = 0.0;
            Tpr = 0.0; FaceMach = 0.0; Pt2 = 0.0; Tt2 = 0.0; BleedFrac = 0.0;
            Distortion = 0.02; CrossflowDeg = 0.0;
            ThroatChoked = false; Spilling = false; Started = true;
            Warnings.Clear();
            _sumThroatCap = 0.0; _sumCaptured = 0.0; _sumSpill = 0.0; _sumDrag = 0.0; _maxRequired = 0.0; _maxLegacy = 0.0; DemandMassFlow = 0.0;

            double wsum = 0.0;
            for (int i = 0; i < _units.Count; i++)
            {
                IntakeUnit u = _units[i];

                // 原版进气道自带马赫上限：过了这个数它原本就不进气了，我们不替它硬撑。
                if (u.MaxMach > 0.0 && !StockIntakeBridge.SuppliesAt(u.MaxMach, mach))
                {
                    Warnings.Add(string.Format(Loc.T("{0}：M{1:F2} 超过它的马赫上限 M{2:F2}，这件不供气", "{0}: M{1:F2} is above its M{2:F2} limit, so this intake supplies nothing"),
                        u.Name, mach, u.MaxMach));
                    continue;
                }

                // 原版零件的 cfg 性能曲线：曲线值就是"这件进气道此刻还算多大面积"。
                // 归零就真的不供气了（原版环形进气道 M3.5 以上是 0.1，不是 0，所以这是兜底）。
                double supply = u.SupplyAt(mach);
                if (supply <= 1e-6)
                {
                    Warnings.Add(string.Format(Loc.T("{0}：M{1:F2} 上它的性能曲线归零（原版 cfg 里就这么写的），这件不供气", "{0}: its curve is zero at M{1:F2} (that is what the stock cfg says), so this intake supplies nothing"),
                        u.Name, mach));
                    continue;
                }

                IntakeSpec eff = u.Spec;
                if (supply < 0.9999)
                {
                    // 面积按曲线折算：曲线 0.45 等于"这件只剩 45% 的捕获面积"。
                    // 折面积而不是折流量，是因为喉道、面马赫、畸变都是按面积推的，
                    // 只改流量会让喉道壅塞判据与面积自相矛盾。
                    eff = u.Spec.Clone();
                    if (eff.CaptureDiameter > 0.0) eff.CaptureDiameter *= Math.Sqrt(supply);
                    else eff.ThroatRatio = Math.Max(0.2, Math.Min(1.2, eff.ThroatRatio * supply));
                }

                IntakeSolution s = IntakeSolver.SolvePart(eff, u.FaceDiameter, _tech, alt, mach, 0.0, double.NaN, u.Length);
                if (s == null || !s.Valid) continue;

                CaptureArea += s.CaptureArea;
                ThroatArea += s.ThroatArea;
                // 发动机面面积**不能相加**：装三件进气道，发动机面还是那一块。
                // 以前这里 +=，于是三件进气道各按"整机需求"算摄取量、再求和，推力乘三。
                if (s.FaceArea > FaceArea) FaceArea = s.FaceArea;
                _sumThroatCap += Gas.ChokedFlow(s.ThroatArea, s.Pt2, s.Tt2);
                _sumCaptured += s.CapturedMassFlow;
                if (s.RequiredMassFlow > _maxRequired) _maxRequired = s.RequiredMassFlow;
                double lg = IntakeSolver.MassFlowAt(s.ThroatArea, s.Pt2, s.Tt2, s.FaceMach);
                if (lg > _maxLegacy) _maxLegacy = lg;
                _sumSpill += s.SpillageMassFlow;
                _sumDrag += s.InletDrag;
                AvailableMassFlow += s.AvailableMassFlow;
                BleedMassFlow += s.BleedMassFlow;

                double w = Math.Max(s.CapturedMassFlow, 1e-6);
                Tpr += s.Tpr * w;
                FaceMach += s.FaceMach * w;
                Pt2 += s.Pt2 * w;
                Tt2 += s.Tt2 * w;
                BleedFrac += (s.CapturedMassFlow > 1e-9 ? s.BleedMassFlow / s.CapturedMassFlow : 0.0) * w;
                wsum += w;

                if (s.DistortionIndex > Distortion) Distortion = s.DistortionIndex;
                if (s.CrossflowDeg > CrossflowDeg) CrossflowDeg = s.CrossflowDeg;
                ThroatChoked |= s.ThroatChoked;
                Spilling |= s.Spilling;
                Started &= s.Started;

                // 只搬各件自己的告警，前面缀上是谁在报——Loc.T("喉道壅塞", "throat choked")落在哪一件上是两回事。
                for (int k = 0; k < s.Warnings.Count; k++)
                    Warnings.Add(string.Format("{0}：{1}", u.Name, s.Warnings[k]));
            }

            if (wsum > 1e-9)
            {
                Tpr /= wsum; FaceMach /= wsum; Pt2 /= wsum; Tt2 /= wsum; BleedFrac /= wsum;
            }
            else
            {
                Tpr = 0.0; Pt2 = 0.0; Tt2 = 0.0;
            }

            Valid = ThroatArea > 1e-9 && CaptureArea > 1e-9 && FaceArea > 1e-9 && wsum > 1e-9;
            if (!Valid) Warnings.Add(Loc.T("装上的进气道一件都没解出流量（面积非法或高度/马赫超范围）", "None of the fitted intakes produced any flow (bad areas, or altitude/Mach out of range)"));
            else
            {
                // ---- 把"N 件进气道"合成"一台发动机真正吃到的流量" ----
                // 需求由发动机面定一次：面就这么大，装十件进气道也不会让它变大。
                // 摄取 = min(各自能吞的合计, 需求)——多了就溢流。
                //
                // **只在多件时生效**：单件是"整机自带口径"那条老路径，逐位不能动
                //（老存档、五台真机预设、所有回归的对账数都钉在那条口径上）。
                DemandMassFlow = IntakeSolver.MassFlowAt(FaceArea, Pt2, Tt2, FaceMach);
                if (!GasState.IsFinite(DemandMassFlow) || DemandMassFlow <= 0.0) DemandMassFlow = _sumCaptured;

                if (Count > 1)
                {
                    // 发动机这一面吃多少，是**一台发动机的需求**，不是"每件进气道各吃一份再相加"。
                    // 每件进气道算出来的 `RequiredMassFlow` 都是"喂饱这台发动机"的量，
                    // 取其中最大的一份当上限：两件同款不会变成两倍，三件也不会变成三倍，
                    // 而两件小进气道仍然能各自加（它们各自的需求大于自己能吞的量，于是按各自能力相加）。
                    double cap = _maxRequired > 1e-6 ? _maxRequired : _sumCaptured;
                    double before = _sumCaptured;
                    CapturedMassFlow = Math.Min(cap, _sumCaptured);
                    SpillageMassFlow = Math.Max(0.0, _sumSpill + before - CapturedMassFlow);
                    Spilling = SpillageMassFlow > 1e-6;
                    double v = mach * Gas.SoundSpeed(Tt2 > 0.0 ? Tt2 / (1.0 + 0.2 * mach * mach) : 288.15);
                    InletDrag = SpillageMassFlow * v;
                    if (CapturedMassFlow < before * 0.99999)
                    {
                        Warnings.Add(string.Format(Loc.T("多件进气道：这几件合起来能吞 {0:F2} kg/s，发动机只要 {1:F2} kg/s，多出来的 {2:F2} 溢流掉（溢流阻力已计入）", "Several intakes: together they swallow {0:F2} kg/s but the engine needs only {1:F2} kg/s; the extra {2:F2} is spilled (spill drag is included)"),
                            before, CapturedMassFlow, before - CapturedMassFlow));
                    }
                }
                else
                {
                    // 单件：原样搬解的四个数（逐位不变）
                    CapturedMassFlow = _sumCaptured;
                    SpillageMassFlow = _sumSpill;
                    InletDrag = _sumDrag;
                }
            }
        }

        /// <summary>
        /// 包成等价解，喂给 OffDesign / EngineNetwork 那两条老路径。
        ///
        /// 注意 `LegacyMassFlow`：发动机的流量口径一直是"喉道 + 来流总压 + 面马赫"，
        /// 那是为缺省轴对称逐位对齐过的老公式。这里按同样口径给合计值，
        /// 所以装了独立进气道之后，设计点也不会因为换了套算法而莫名其妙地跳一截。
        /// </summary>
        public IntakeSolution ToSolution(double alt, double mach)
        {
            Refresh(alt, mach);

            IntakeSolution s = new IntakeSolution();
            s.Valid = Valid;
            s.Altitude = alt;
            s.Mach = mach;
            s.Tpr = Tpr;
            s.Pt2 = Pt2;
            s.Tt2 = Tt2;
            s.FaceMach = FaceMach;
            s.FaceArea = FaceArea;
            s.CaptureArea = CaptureArea;
            s.ThroatArea = ThroatArea;
            s.AreaRatioThroatFace = FaceArea > 1e-9 ? ThroatArea / FaceArea : 0.0;
            s.CapturedMassFlow = CapturedMassFlow;
            s.AvailableMassFlow = AvailableMassFlow;
            s.SpillageMassFlow = SpillageMassFlow;
            s.BleedMassFlow = BleedMassFlow;
            s.InletDrag = InletDrag;
            s.DistortionIndex = Distortion;
            s.CrossflowDeg = CrossflowDeg;
            s.ThroatChoked = ThroatChoked;
            s.Spilling = Spilling;
            s.Started = Started;
            s.Unstarted = !Started;

            double p0, t0;
            Gas.Isa(alt, out p0, out t0);
            s.AmbientPressure = p0;
            s.AmbientTemperature = t0;
            s.FlightVelocity = mach * Gas.SoundSpeed(t0);
            double pt0, tt0;
            if (mach > 0.0) Gas.Total(p0, t0, mach, out pt0, out tt0);
            else { pt0 = p0; tt0 = t0; }
            s.Pt0 = pt0; s.Tt0 = tt0;

            s.ThroatMargin = 0.0;
            s.RequiredMassFlow = CapturedMassFlow;
            s.LegacyMassFlow = IntakeSolver.MassFlowAt(ThroatArea, pt0, tt0, FaceMach)
                             * (1.0 - Math.Min(Math.Max(BleedFrac, 0.0), 0.25));
            // 发动机面是最终上限：喉道再宽，进发动机的也就这么多。
            // （单件、且发动机面本来就比喉道宽时这条不生效，所以老口径逐位不变；
            //   多件时它把"N 份满流量"压回一份，就是这一步治住了"装几件推力乘几"。）
            if (Count > 1 && _maxLegacy > 1e-6 && s.LegacyMassFlow > _maxLegacy) s.LegacyMassFlow = _maxLegacy;
            for (int i = 0; i < Warnings.Count; i++) s.Warn(Warnings[i]);
            return s;
        }

        public string Describe()
        {
            if (!Present) return Loc.T("接在自己的进气道上（没装独立进气道零件）", "On its own intake (no separate intake part fitted)");
            return string.Format(Loc.T("接 {0} 件独立进气道：捕获 {1:F3} m²｜喉道 {2:F3} m²｜可用 {3:F1} kg/s｜总压恢复 {4:F3}｜畸变 {5:F3}｜{6}", "Fed by {0} intake part(s): capture {1:F3} m2 | throat {2:F3} m2 | available {3:F1} kg/s | recovery {4:F3} | distortion {5:F3} | {6}"),
                Count, CaptureArea, ThroatArea, AvailableMassFlow, Tpr, Distortion,
                ThroatChoked ? Loc.T("喉道壅塞", "throat choked") : (Spilling ? Loc.T("有溢流", "spilling") : Loc.T("正常", "nominal")));
        }
    }
}