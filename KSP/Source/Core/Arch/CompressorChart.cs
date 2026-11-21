using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 压气机特性图上的一点：换算转速、换算流量、压比、等熵效率。
    /// 全部用**换算量**（corrected）而不是实际量——这是特性图能跨高度、跨总温通用的原因。
    /// </summary>
    public struct MapPoint
    {
        /// <summary>换算转速 Nc = (N/√θ)/N_design：1 = 设计转速。</summary>
        public double Nc;
        /// <summary>换算流量 Wc = ṁ√θ/δ，单位 kg/s。</summary>
        public double Wc;
        /// <summary>总压比。</summary>
        public double Pr;
        /// <summary>等熵效率。</summary>
        public double Eta;

        public MapPoint(double nc, double wc, double pr, double eta)
        {
            Nc = nc; Wc = wc; Pr = pr; Eta = eta;
        }

        public override string ToString()
        {
            return string.Format("Nc {0:F2}｜Wc {1:F2} kg/s｜π {2:F3}｜η {3:F3}", Nc, Wc, Pr, Eta);
        }
    }

    /// <summary>一条等换算转速线。两端就是两条边界：左边喘振、右边堵塞。</summary>
    public sealed class SpeedLine
    {
        public double Nc;
        public List<MapPoint> Points = new List<MapPoint>();
        /// <summary>喘振边界上的点（这条线上的最小流量、最高压比）。</summary>
        public MapPoint Surge;
        /// <summary>堵塞边界上的点（最大流量、接近 1 的压比）。</summary>
        public MapPoint Choke;

        public override string ToString()
        {
            return string.Format("Nc {0:F2}｜喘振 π {1:F3}@Wc {2:F2}｜堵塞 Wc {3:F2}", Nc, Surge.Pr, Surge.Wc, Choke.Wc);
        }
    }

    /// <summary>
    /// 压气机特性图（第一版：**由机器自己的几何与设计点生成**，不是查表）。
    ///
    /// 交接文档 B 要这张图，原话是"修正转速 / 修正流量 / 压比 / 效率 / 喘振边界 / 堵塞边界 / 工作线，
    /// 喘振裕度定义与插值写入文档，不许单一经验阈值当完整边界"。
    /// 此前全项目只有 `Criteria` 里的一个 φ/φ_失速 比值代理指标，`Criteria` 头部那段
    /// "诚实声明"就是在说这件事——它能排相对好坏，但回答不了"这个工作点离喘振线还有几个点"。
    ///
    /// 这张图怎么来：不从厂家的图抄（抄不到，也不知道抄谁的），而是用这台机器**自己的**
    /// 设计点做**归一化**，再按三条关系生成。流量系数用**相对值** f = φ/φ_设计：
    /// 设计点永远在 f=1，图上所有点都相对它说话。为什么不直接用绝对 φ：
    /// 绝对 φ 由"流量 ÷ 环形面积 ÷ 叶尖速度"算出，而本工程五台真机预设的
    /// `TipDiameter` 是按压比拟合的，不是按图纸的环形面积——J79 照现在这套几何
    /// 反推出来 φ≈0.92（真机 0.4~0.5），拿绝对值画图，喘振线会跑到设计点左边去。
    /// 归一化之后"设计点在哪、边界在哪"与几何误差脱钩，这是这张图能用得上的前提。
    ///
    /// 1. **流量**：同一条流道、同一个流量系数，换算流量正比于换算转速——
    ///    `Wc(Nc, f) = Wc_d · Nc · f`。这是"同一条流道"的必然结果，不是拟合。
    /// 2. **压比**：用载荷系数 ψ。`ψ_d = cp·Tt·(π_d^((γ-1)/γ) − 1)/(η·U_d²)`；
    ///    同一转速下 `ψ(f) = ψ_d · (1 + Slope · (1 − f))`，压比按
    ///    `π = [1 + η·ψ·(Nc·U_d)²/(cp·Tt)]^(γ/(γ−1))` 回算。
    ///    `Slope` 不是拍出来的：它由 **喘振点压比 = 设计压比 × 1.20** 这条锚反解
    ///    （见 `SlopeForSurgePrRatio`）。锚放在压比上而不是 ψ 上，是因为高压比机器
    ///    π=(1+Δ)^3.5 非常非线性——ψ 只多 5%，π 就多 20%，拿 ψ 定锚会画出
    ///    "喘振压比是设计的三倍"这种图。
    /// 3. **两条边界**：喘振线在 f = `SurgeFlowFrac` = 0.90（同转速下压气机能吞的
    ///    最小流量约为设计的九成），堵塞线在 f = `ChokeFlowFrac` = 1.18。
    ///    于是设计点的喘振裕度 = 1.20/0.90 − 1 ≈ 33%，与真机 25~35% 同量级。
    ///
    /// 与 `Criteria` 的关系：判据里那个 φ/φ_失速 是**绝对几何**问的"这条流道对这么多流量
    /// 是不是太窄/太宽"；这张图问的是"工作点在自己图上离边界多远"。两个问题都合法，
    /// 但**不是同一把尺**，所以这里不再声称"同一套口径"——
    /// 判据不够时改判据，图不够时改图，别把两个数混着比。
    ///
    /// 诚实声明：这是**解析生成**的特性图，等转速线是直的（ψ 对 f 线性），
    /// 没有真机图上那种弯曲与横向的"钩子"。它的用处是把"点在哪、离边界多远"算清楚，
    /// 而不是复现某台真机的图。
    /// </summary>
    public sealed class CompressorChart
    {
        /// <summary>生成这张图的机器的设计点。</summary>
        public double DesignWc;
        public double DesignPr;
        public double DesignEta;
        /// <summary>相对流量系数：设计点恒为 1.0（见类说明，为什么不用绝对值）。</summary>
        public double DesignPhi = 1.0;
        public double TipDiameter;
        public double HubTipRatio;
        public double DesignTipSpeed;      // U_d，m/s
        public double DesignTt;            // 进口总温，K
        public double DesignN;             // 设计物理转速，rpm

        /// <summary>喘振线所在的相对流量（0.90 = 设计流量的九成）。</summary>
        public double PhiStall = 0.90;
        /// <summary>堵塞线所在的相对流量（1.18 = 设计流量的一点一八倍）。</summary>
        public double PhiChoke = 1.18;
        /// <summary>同转速下喘振点压比 / 设计压比。锚，用来反解 Slope。</summary>
        public double SurgePrRatio = 1.20;
        /// <summary>ψ 对 f 的斜率，由 <see cref="SurgePrRatio"/> 反解得到。</summary>
        public double Slope = 0.60;

        /// <summary>
        /// 这台机器有没有可调静子。有的话，低换算转速下喘振边界往左退、堵塞边界往右挪：
        /// 叶片一转，前几级就能在低得多的流量下仍然贴着设计冲角工作。
        /// 桩效应给到 `0.25·(1−Nc)`（Nc 0.7 时 0.78，Nc 0.5 时 0.70——真机上可调静子把低转速喘振流量压掉一到两成）。
        /// </summary>
        public bool Vigv;

        /// <summary>各换算转速线（0.50~1.05，步长 0.05，共 12 条）。</summary>
        public List<SpeedLine> Lines = new List<SpeedLine>();

        public const double Gamma = 1.4;
        public const double Cp = 1004.5;
        public const double P0Ref = 101325.0;
        public const double T0Ref = 288.15;

        /// <summary>环形通道面积（m²）。压气机进口。</summary>
        public double AnnulusArea
        {
            get
            {
                double a = Math.PI * 0.25 * TipDiameter * TipDiameter;
                return a * Math.Max(1.0 - HubTipRatio * HubTipRatio, 1e-6);
            }
        }

        /// <summary>设计点的载荷系数 ψ_d。</summary>
        public double PsiDesign
        {
            get
            {
                if (DesignTipSpeed <= 1.0 || DesignPr <= 1.0) return 0.0;
                double dt = (Math.Pow(DesignPr, (Gamma - 1.0) / Gamma) - 1.0) / Math.Max(DesignEta, 1e-3);
                return Cp * DesignTt * dt / (DesignTipSpeed * DesignTipSpeed);
            }
        }

        /// <summary>
        /// 由设计点建图。
        /// `mdot` 是设计点进口流量（kg/s）、`tt`/`pt` 是进口总温/总压、
        /// `pr`/`eta` 是设计压比与等熵效率、`nRpm` 是设计物理转速。
        /// </summary>
        public static CompressorChart Build(double mdot, double tt, double pt, double pr, double eta,
                                          double tipDiameter, double hubTipRatio, double nRpm,
                                          double phiStall, double phiChoke)
        {
            CompressorChart m = new CompressorChart();
            m.TipDiameter = tipDiameter;
            m.HubTipRatio = hubTipRatio;
            m.DesignPr = pr;
            m.DesignEta = eta > 1e-3 ? eta : 0.90;
            m.DesignTt = tt > 1.0 ? tt : T0Ref;
            m.DesignN = nRpm;
            m.DesignTipSpeed = Math.PI * tipDiameter * nRpm / 60.0;

            double theta = m.DesignTt / T0Ref;
            double delta = (pt > 1.0 ? pt : P0Ref) / P0Ref;
            m.DesignWc = mdot * Math.Sqrt(theta) / Math.Max(delta, 1e-6);

            // 相对流量系数：设计点恒为 1。传进来的 phiStall/phiChoke 只在 >0 时
            // 当作"相对流量"覆盖缺省值（0.90 / 1.18），不参与绝对 φ 的换算。
            m.DesignPhi = 1.0;
            m.PhiStall = phiStall > 1e-6 && phiStall < 1.0 ? phiStall : 0.90;
            m.PhiChoke = phiChoke > m.PhiStall ? phiChoke : 1.18;

            m.Slope = m.SlopeForSurgePrRatio();
            m.BuildLines();
            return m;
        }

        /// <summary>
        /// 由"喘振点压比 = 设计压比 × SurgePrRatio"反解 ψ 斜率。
        ///
        /// 锚定在压比上而不是 ψ 上：π = (1+Δ)^3.5，π_d=13.5 时 Δ=1.05——
        /// ψ 只多 5% 就到 π 高 20%。同一句"喘振点比设计点高一成"，
        /// 在 π=3 的风扇上对应 ψ +9%，在 π=13 的压气机上只对应 ψ +3%。
        /// 用一个固定 Slope 同时套两种机器，图上必然一个太陡一个画不出来。
        /// </summary>
        public double SlopeForSurgePrRatio()
        {
            double f = Math.Max(1.0 - PhiStall, 1e-6);
            if (DesignPr <= 1.0) return 0.60;
            double dd = Math.Pow(DesignPr, (Gamma - 1.0) / Gamma) - 1.0;
            double ds = Math.Pow(DesignPr * SurgePrRatio, (Gamma - 1.0) / Gamma) - 1.0;
            if (dd <= 1e-9) return 0.60;
            double slope = (ds / dd - 1.0) / f;
            if (slope < 0.05) slope = 0.05;
            if (slope > 8.0) slope = 8.0;
            return slope;
        }

        /// <summary>设计点进口轴向速度（m/s）。只做读数，不参与图的坐标。</summary>
        public double AxialVelocityDesign;

        /// <summary>设计点的绝对流量系数 φ = Va/U（读数用；图的坐标是相对的）。</summary>
        public double AbsolutePhiDesign
        {
            get { return DesignTipSpeed > 1.0 ? AxialVelocityDesign / DesignTipSpeed : 0.0; }
        }

        /// <summary>给定换算转速与流量系数时的压比。</summary>
        public double PressureRatioAt(double nc, double phi)
        {
            if (nc <= 1e-6 || phi <= 1e-6) return 1.0;
            double psi = PsiDesign * (1.0 + Slope * (1.0 - phi / DesignPhi));
            if (psi < 0.0) psi = 0.0;
            double u = nc * DesignTipSpeed;
            double dtRatio = DesignEta * psi * u * u / (Cp * DesignTt);
            if (dtRatio <= 0.0) return 1.0;
            double pr = Math.Pow(1.0 + dtRatio, Gamma / (Gamma - 1.0));
            return pr < 1.0 ? 1.0 : pr;
        }

        /// <summary>给定换算转速与流量系数时的换算流量。</summary>
        public double FlowAt(double nc, double phi)
        {
            return DesignWc * nc * phi / Math.Max(DesignPhi, 1e-6);
        }

        /// <summary>给定换算转速与流量系数时的一点。</summary>
        public MapPoint PointAt(double nc, double phi)
        {
            // 效率随偏离设计点变差：这里用一个碗形，设计点取 1.0。
            double dn = (nc - 1.0) / 0.5;
            double dp = (phi - DesignPhi) / Math.Max(DesignPhi, 1e-3);
            double eta = DesignEta * (1.0 - 0.10 * dn * dn - 0.18 * dp * dp);
            if (eta < DesignEta * 0.55) eta = DesignEta * 0.55;
            if (eta > DesignEta) eta = DesignEta;
            return new MapPoint(nc, FlowAt(nc, phi), PressureRatioAt(nc, phi), eta);
        }

        private void BuildLines()
        {
            Lines.Clear();
            for (int i = 0; i <= 11; i++)
            {
                double nc = 0.50 + 0.05 * i;
                SpeedLine ln = new SpeedLine();
                ln.Nc = nc;
                // 可调静子只在偏离设计转速时起作用：设计点叶片本来就在设计角度上。
                double stall = Vigv ? Math.Max(PhiStall - (1.0 - nc) * 0.40, 0.55) : PhiStall;
                double choke = Vigv ? PhiChoke + (1.0 - nc) * 0.12 : PhiChoke;
                ln.Surge = PointAt(nc, stall);
                ln.Choke = PointAt(nc, choke);
                const int steps = 12;
                for (int k = 0; k <= steps; k++)
                {
                    double phi = stall + (choke - stall) * k / steps;
                    ln.Points.Add(PointAt(nc, phi));
                }
                Lines.Add(ln);
            }
        }

        /// <summary>最近的一条转速线（按 Nc 线性取，不做插值——这是给人看的图）。</summary>
        public SpeedLine LineFor(double nc)
        {
            if (Lines.Count == 0) return null;
            SpeedLine best = Lines[0];
            double d = Math.Abs(best.Nc - nc);
            for (int i = 1; i < Lines.Count; i++)
            {
                double di = Math.Abs(Lines[i].Nc - nc);
                if (di < d) { d = di; best = Lines[i]; }
            }
            return best;
        }

        /// <summary>
        /// 喘振边界：给定换算转速时的喘振点（插值，不是取最近线）。
        /// 多条等转速线的喘振点连起来就是**喘振线**。
        /// </summary>
        public MapPoint SurgeAt(double nc)
        {
            if (Lines.Count == 0) return new MapPoint(nc, 0.0, 1.0, DesignEta);
            if (nc <= Lines[0].Nc) return Lines[0].Surge;
            if (nc >= Lines[Lines.Count - 1].Nc) return Lines[Lines.Count - 1].Surge;
            for (int i = 0; i < Lines.Count - 1; i++)
            {
                if (nc >= Lines[i].Nc && nc <= Lines[i + 1].Nc)
                {
                    double f = (nc - Lines[i].Nc) / Math.Max(Lines[i + 1].Nc - Lines[i].Nc, 1e-9);
                    return new MapPoint(nc,
                        Lines[i].Surge.Wc + f * (Lines[i + 1].Surge.Wc - Lines[i].Surge.Wc),
                        Lines[i].Surge.Pr + f * (Lines[i + 1].Surge.Pr - Lines[i].Surge.Pr),
                        Lines[i].Surge.Eta + f * (Lines[i + 1].Surge.Eta - Lines[i].Surge.Eta));
                }
            }
            return Lines[Lines.Count - 1].Surge;
        }

        /// <summary>堵塞边界：给定换算转速时的最大换算流量（插值）。</summary>
        public MapPoint ChokeAt(double nc)
        {
            if (Lines.Count == 0) return new MapPoint(nc, 0.0, 1.0, DesignEta);
            if (nc <= Lines[0].Nc) return Lines[0].Choke;
            if (nc >= Lines[Lines.Count - 1].Nc) return Lines[Lines.Count - 1].Choke;
            for (int i = 0; i < Lines.Count - 1; i++)
            {
                if (nc >= Lines[i].Nc && nc <= Lines[i + 1].Nc)
                {
                    double f = (nc - Lines[i].Nc) / Math.Max(Lines[i + 1].Nc - Lines[i].Nc, 1e-9);
                    return new MapPoint(nc,
                        Lines[i].Choke.Wc + f * (Lines[i + 1].Choke.Wc - Lines[i].Choke.Wc),
                        Lines[i].Choke.Pr + f * (Lines[i + 1].Choke.Pr - Lines[i].Choke.Pr),
                        Lines[i].Choke.Eta + f * (Lines[i + 1].Choke.Eta - Lines[i].Choke.Eta));
                }
            }
            return Lines[Lines.Count - 1].Choke;
        }

        /// <summary>
        /// 工作线：本项目自己的走向（换算流量随转速涨落）。
        /// 与 `FanMap.FlowFrac` 同一个口径（φ 相对设计点随 Nc^0.8 变），
        /// 这样风扇和压气机的工作线是一条线形，不会一个陡一个缓。
        /// </summary>
        public MapPoint WorkingPoint(double nc)
        {
            if (nc <= 1e-6) return PointAt(1e-3, PhiStall);
            double phi = DesignPhi * Math.Pow(nc, 0.20);
            // φ 也不许穿出两条边界——工作线撞上边界，那就是这篇图该说的话。
            if (phi < PhiStall) phi = PhiStall;
            if (phi > PhiChoke) phi = PhiChoke;
            return PointAt(nc, phi);
        }

        /// <summary>
        /// 查图时越界的方向。图就是这几条线，出了范围必须**说出来**：
        /// 静默外推等于编一条不存在的转速线，而调用方会以为那是量出来的。
        /// </summary>
        [Flags]
        public enum ChartFlags
        {
            None = 0,
            /// <summary>换算转速低于图的最下面一条线（按最下面那条限幅）。</summary>
            NcBelowMap = 1,
            /// <summary>换算转速高于图的最上面一条线（按最上面那条限幅）。</summary>
            NcAboveMap = 2,
            /// <summary>换算流量比这条转速线的喘振点还小（压比夹到喘振点）。</summary>
            FlowLeftOfStall = 4,
            /// <summary>换算流量比这条转速线的堵塞点还大（压比夹到堵塞点）。</summary>
            FlowRightOfChoke = 8
        }

        /// <summary>图上最低一条转速线的 Nc（查图限幅的下界）。</summary>
        public double NcMin { get { return Lines.Count > 0 ? Lines[0].Nc : 0.0; } }

        /// <summary>图上最高一条转速线的 Nc（查图限幅的上界）。</summary>
        public double NcMax { get { return Lines.Count > 0 ? Lines[Lines.Count - 1].Nc : 0.0; } }

        /// <summary>
        /// 这个工作点在图上的位置是否越界（只判断，不改数）。
        /// 两条边界是两堵方向相反的墙，所以要分开报，不能合成一个 "超出范围"。
        /// </summary>
        public ChartFlags FlagsOf(double nc, double wc)
        {
            ChartFlags f = ChartFlags.None;
            if (Lines.Count == 0) return ChartFlags.NcBelowMap | ChartFlags.NcAboveMap;
            if (nc < Lines[0].Nc) f |= ChartFlags.NcBelowMap;
            if (nc > Lines[Lines.Count - 1].Nc) f |= ChartFlags.NcAboveMap;
            double n = nc < Lines[0].Nc ? Lines[0].Nc
                     : (nc > Lines[Lines.Count - 1].Nc ? Lines[Lines.Count - 1].Nc : nc);
            if (wc <= 0.0) return f | ChartFlags.FlowLeftOfStall;
            if (wc < SurgeAt(n).Wc) f |= ChartFlags.FlowLeftOfStall;
            else if (wc > ChokeAt(n).Wc) f |= ChartFlags.FlowRightOfChoke;
            return f;
        }

        /// <summary>
        /// 查图：给定换算转速与换算流量，取回压比与效率。
        ///
        /// 策略（§175，写死在代码里而不是靠调用方自觉）：
        /// - 转速超界 → 夹到最上面/最下面那条转速线，并置 `NcBelowMap`/`NcAboveMap`；
        /// - 流量超出这条线的喘振点/堵塞点 → 夹到边界点，并置 `FlowLeftOfStall`/`FlowRightOfChoke`；
        /// - 返回值里的 `Nc`/`Wc` 是**调用方给的**，不是夹过的——调用方要能看出自己在哪儿查的。
        ///
        /// 这里不做"外推一条新转速线"：那需要真实试验数据，编出来的线比没有更坏。
        /// </summary>
        public MapPoint Query(double nc, double wc, out ChartFlags flags)
        {
            flags = FlagsOf(nc, wc);
            if (Lines.Count == 0)
                return new MapPoint(nc, Math.Max(wc, 0.0), 1.0, DesignEta);
            double n = nc < Lines[0].Nc ? Lines[0].Nc
                     : (nc > Lines[Lines.Count - 1].Nc ? Lines[Lines.Count - 1].Nc : nc);
            if (n <= 1e-6 || wc <= 0.0 || DesignWc <= 0.0)
                return new MapPoint(nc, Math.Max(wc, 0.0), 1.0, DesignEta);

            // 夹到**这条转速线自己的**两条边界上：用 Wc 夹，不用固定的 φ 阈值——
// 可调静子会把低转速的喘振点（φ 更小）与堵塞点（φ 更大）都挪开，
// 拿常数 φ 去夹会在那些线上夹出图上不存在的位置（第 99 段就是这么抓到的）。
            MapPoint s = SurgeAt(n), k = ChokeAt(n);
            if (s.Wc > 0.0 && k.Wc > s.Wc) wc = wc < s.Wc ? s.Wc : (wc > k.Wc ? k.Wc : wc);
            double phi = wc / Math.Max(DesignWc * n, 1e-9);
            MapPoint p = PointAt(n, phi);
            return new MapPoint(nc, wc, p.Pr, p.Eta);
        }

        /// <summary>
        /// 喘振裕度（工业口径，写在这里以免日后各说各话）：
        ///
        ///     SM = [ (π_喘振 / π_工作) · (Wc_工作 / Wc_喘振) − 1 ] × 100%
        ///
        /// 两个比值相乘的意思是：既要往上（压比）有空间，也要往小流量（推油门再收）有空间。
        /// 只用压比差会高估——真机上堵转喘振常常是先撞流量边。
        /// 返回 -1 表示读数不可用（流量或压比非正）。
        /// </summary>
        public double SurgeMargin(MapPoint op)
        {
            if (op.Pr <= 1.0 || op.Wc <= 0.0) return -1.0;
            MapPoint s = SurgeAt(op.Nc);
            if (s.Pr <= 1.0 || s.Wc <= 0.0) return -1.0;
            return ((s.Pr / op.Pr) * (op.Wc / s.Wc) - 1.0) * 100.0;
        }

        /// <summary>
        /// 堵塞侧余量（横向，按换算流量算）：
        ///
        ///     CM = ( Wc_堵塞 / Wc_工作 − 1 ) × 100%
        ///
        /// 喘振线是"再收油门就转不动"，堵塞线是"再加油门只有流量、没有压比"。
        /// 两条都是墙，但撞墙的方向相反：本模型以前只检查了喘振那一侧，
        /// 于是"流量塞进超过堵塞线"的工作点会被当成正常点算下去——
        /// 那不是裕度大，那是这条等转速线根本过不去这么多气。
        ///
        /// 返回 -1 表示读数不可用（流量非正、图是空的）。
        /// </summary>
        public double ChokeMargin(MapPoint op)
        {
            if (op.Wc <= 0.0 || Lines.Count == 0) return -1.0;
            MapPoint c = ChokeAt(op.Nc);
            if (c.Wc <= 0.0) return -1.0;
            return (c.Wc / op.Wc - 1.0) * 100.0;
        }

        /// <summary>这个工作点是不是已经越过堵塞线（横向越界，不是"快到了"）。</summary>
        public bool Choked(MapPoint op)
        {
            double cm = ChokeMargin(op);
            // -1 是"读数不可用"的记号（没有图、流量非正）。必须排掉，
            // 否则"没有读数"会被当成"越界"——审计里那条 65 条假警报就是这么来的。
            if (Math.Abs(cm + 1.0) < 1e-9) return false;
            return cm < -1e-6;
        }

        /// <summary>
        /// 逐级喘振余量（近似，口径只按流量系数，不含压比）。
        ///
        /// 为什么需要它：整机余量 33% 说的是"这台压气机整体离喘振多远"，
        /// 但真机喘振不是整体一起喘——**前级先喘**。同样的总流量系数下，
        /// 前几级处在低流量系数（进气密度大、单级做功不够），后几级反过来偏高（近堵塞）。
        /// 这就是为什么真机要装可调静子与级间放气：它们都是在把这条"级间负荷斜面"掰平。
        ///
        /// 模型：整机流量系数 phi 按级线性摊开，前级 −spread、后级 +spread，
        /// 均值保持 phi（所以整机口径不受影响）；spread 随转速下降增大，
        /// 可调静子把它压掉约 2/3。单级的喘振边界在低转速时会往下放宽
        /// （激波损失随相对马赫下降，这条线不是常数）——这条放宽写在这里，
        /// 否则 50% 转速会算出"前级早就喘了"，那是本模型常数 phi 喘振线的假象。
        /// </summary>
        public double[] StageSurgeMargins(MapPoint op, int stages, bool vigv)
        {
            if (stages <= 0 || op.Wc <= 0.0 || DesignWc <= 0.0) return new double[0];
            double phi = op.Wc / Math.Max(DesignWc * Math.Max(op.Nc, 1e-3), 1e-9);
            double spread = 0.30 * Math.Max(0.0, 1.0 - op.Nc);
            if (spread > 0.30) spread = 0.30;
            if (vigv) spread *= 0.35;
            // 低转速时单级喘振边界放宽：50% 转速放宽 0.05，设计转速不放宽。
            double stall = PhiStall - 0.10 * Math.Max(0.0, 1.0 - op.Nc);
            double[] m = new double[stages];
            for (int i = 0; i < stages; i++)
            {
                double u = stages == 1 ? 0.0 : 2.0 * i / (stages - 1) - 1.0;   // 前级 −1，后级 +1
                double phiI = phi + spread * u;
                m[i] = phiI > 1e-6 ? (phiI / stall - 1.0) * 100.0 : -100.0;
            }
            return m;
        }

        /// <summary>逐级堵塞余量（与逐级喘振同一套级间负荷摊分，方向相反）。</summary>
        public double[] StageChokeMargins(MapPoint op, int stages, bool vigv)
        {
            if (stages <= 0 || op.Wc <= 0.0 || DesignWc <= 0.0) return new double[0];
            double phi = op.Wc / Math.Max(DesignWc * Math.Max(op.Nc, 1e-3), 1e-9);
            double spread = 0.30 * Math.Max(0.0, 1.0 - op.Nc);
            if (spread > 0.30) spread = 0.30;
            if (vigv) spread *= 0.35;
            double[] m = new double[stages];
            for (int i = 0; i < stages; i++)
            {
                double u = stages == 1 ? 0.0 : 2.0 * i / (stages - 1) - 1.0;
                double phiI = phi + spread * u;
                m[i] = phiI > 1e-6 ? (PhiChoke / phiI - 1.0) * 100.0 : 999.0;
            }
            return m;
        }

        /// <summary>这张图是不是自洽（设计点能原样取回、边界单调）。给回归与自检用。</summary>
        public bool SelfCheck(out string why)
        {
            why = "";
            if (DesignWc <= 0.0) { why = "设计换算流量非正"; return false; }
            MapPoint d = PointAt(1.0, DesignPhi);
            if (Math.Abs(d.Pr - DesignPr) > 1e-6 * Math.Max(DesignPr, 1.0))
            {
                why = string.Format("设计点压比取回 {0:F6}，设计值 {1:F6}", d.Pr, DesignPr);
                return false;
            }
            if (Math.Abs(d.Wc - DesignWc) > 1e-6 * DesignWc)
            {
                why = string.Format("设计点换算流量取回 {0:F6}，设计值 {1:F6}", d.Wc, DesignWc);
                return false;
            }
            for (int i = 0; i < Lines.Count; i++)
            {
                if (Lines[i].Surge.Pr < Lines[i].Choke.Pr)
                {
                    why = string.Format("Nc {0:F2} 的喘振压比 {1:F3} 低于堵塞压比 {2:F3}", Lines[i].Nc, Lines[i].Surge.Pr, Lines[i].Choke.Pr);
                    return false;
                }
            }
            return true;
        }

        /// <summary>一句话描述（面板/自检/日志用）。</summary>
        public string Describe()
        {
            if (Lines.Count == 0) return "特性图：空";
            SpeedLine nl = LineFor(1.0);
            return string.Format("特性图：{0} 条转速线｜设计 Wc {1:F2} kg/s｜π {2:F2}｜喘振线 Nc1.00 π {3:F2}｜堵塞 Wc {4:F2}",
                Lines.Count, DesignWc, DesignPr, nl != null ? nl.Surge.Pr : 0.0, nl != null ? nl.Choke.Wc : 0.0);
        }

        /// <summary>
        /// 从一台机器的高压压气机设计点建图。
        /// 进口站给 HPC 进口状态、出口站给 HPC 出口状态（都来自链路求解的站表），
        /// 转速取设计转速（`SpoolDynamics.DesignOmega` 的口径，与转子模型同一个数）。
        /// </summary>
        public static CompressorChart ForHighPressureCompressor(EngineDesignData d,
                                                                GasState inlet, GasState outlet,
                                                                TechLevel tech)
        {
            if (d == null || d.Hpc == null) return null;
            return BuildFor(d, d.Hpc, inlet, outlet, tech, tech != null ? tech.HpcEtaPol : 0.90);
        }

        /// <summary>
        /// 低压压气机的图。口径与高压那张一样，只有三处必须分开：
        /// 级组用 `d.Lpc`、效率取 `LpcEtaPol`、设计流量是**压气机进口那一小股**
        /// （涡扇上它等于核心流量，不是总流量——拿总流量建图会把整张图右移一倍）。
        /// </summary>
        public static CompressorChart ForLowPressureCompressor(EngineDesignData d,
                                                               GasState inlet, GasState outlet,
                                                               TechLevel tech)
        {
            if (d == null || d.Lpc == null) return null;
            return BuildFor(d, d.Lpc, inlet, outlet, tech, tech != null ? tech.LpcEtaPol : 0.88);
        }

        /// <summary>
        /// 按设计对象缓存的基准图。**必须挂在设计点上**，不能拿当前工作点现建——
        /// 现建等于每算一次就宣布"这一刻就是设计点"，于是 φ 恒为 1/nc：
        /// 50% 转速时 φ=2.0 越过堵塞线（1.18），报出 −29.5% 的假越界（§174）。
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EngineDesignData, ChartHolder> DesignCache
            = new System.Runtime.CompilerServices.ConditionalWeakTable<EngineDesignData, ChartHolder>();

        private sealed class ChartHolder
        {
            public CompressorChart Hpc;
            public CompressorChart Lpc;
            public double Sig;
            /// <summary>正在建图。建图要跑一次设计点求解，而那条路径上也会问到图——用它断开递归。</summary>
            public bool Building;
        }

        /// <summary>
        /// 取这台机器的设计点基准图（低压/高压）。第一次调用会跑一次 `EngineNetwork.SolveDesign`，
        /// 之后按设计参数指纹缓存；设计改了（叶尖直径、级数、设计马赫这些）才重建。
        /// 建图期间被重入时返回 null——下游按"这次没有图"如实处理，不能编一个裕度出来。
        /// </summary>
        public static CompressorChart DesignChart(EngineDesignData d, bool low, TechLevel tech)
        {
            if (d == null) return null;
            ChartHolder h = DesignCache.GetValue(d, delegate (EngineDesignData _) { return new ChartHolder(); });
            double sig = Signature(d, low);
            if (Math.Abs(sig - h.Sig) > 1e-9)
            {
                // 设计变了：旧图作废（功率/流量口径全变了，留着比没有更坏）
                h.Hpc = null;
                h.Lpc = null;
                h.Sig = sig;
            }
            if (low ? h.Lpc != null : h.Hpc != null) return low ? h.Lpc : h.Hpc;
            if (h.Building) return null;
            h.Building = true;
            try
            {
                NetworkSolution des = EngineNetwork.SolveDesign(d);
                if (des == null) return null;
                GasState inState = null, outState = null;
                if (low)
                {
                    if (!des.StationTable.TryGetValue(Stations.IntakeExit, out inState)) return null;
                    if (!des.StationTable.TryGetValue(Stations.LpcExit, out outState)) return null;
                    h.Lpc = ForLowPressureCompressor(d, inState, outState, tech);
                    return h.Lpc;
                }
                if (!des.StationTable.TryGetValue(Stations.HpcExit, out outState)) return null;
                if (!des.StationTable.TryGetValue(Stations.LpcExit, out inState)
                    && !des.StationTable.TryGetValue(Stations.FanExit, out inState))
                    des.StationTable.TryGetValue(Stations.IntakeExit, out inState);
                h.Hpc = ForHighPressureCompressor(d, inState, outState, tech);
                return h.Hpc;
            }
            // 探测豁免：建基准图失败只能返回 null——下游如实说"这次没有图"，不会编裕度；这里再喊一遍
            // 就是同一件事报两次（设计点求解自己的错已进 Result.Errors）。
            catch (Exception) { return null; }
            finally { h.Building = false; }
        }

        /// <summary>设计参数指纹：这些数一变，图的基准就全变了。</summary>
        private static double Signature(EngineDesignData d, bool low)
        {
            if (d == null) return 0.0;
            CompressorSpec sp = low ? d.Lpc : d.Hpc;
            double sig = 17.0 * d.DesignMach + 3.0 * d.DesignAlt / 1000.0;
            if (sp != null)
                sig = sig * 1.7 + sp.Stages * 13.0 + sp.TipOf(0) * 101.0 + sp.HubTipRatio * 37.0
                      + (sp.Vigv ? 5.0 : 0.0);
            if (d.Fan != null) sig = sig * 1.3 + d.Fan.Diameter * 29.0;
            return sig;
        }

        private static CompressorChart BuildFor(EngineDesignData d, CompressorSpec spec,
                                                GasState inlet, GasState outlet, TechLevel tech,
                                                double etaPol)
        {
            if (spec == null || inlet == null || outlet == null) return null;
            if (spec.Stages <= 0 || inlet.TotalPressure <= 0.0) return null;
            if (outlet.TotalPressure <= inlet.TotalPressure || inlet.MassFlow <= 0.0) return null;

            CompressorChart m = new CompressorChart();
            m.TipDiameter = spec.TipOf(0);
            m.HubTipRatio = spec.HubTipRatio;
            m.DesignPr = outlet.TotalPressure / inlet.TotalPressure;
            m.DesignEta = etaPol;
            m.DesignTt = inlet.TotalTemperature;
            double omega = SpoolDynamics.DesignOmega(d);
            m.DesignN = omega * 60.0 / (2.0 * Math.PI);
            m.DesignTipSpeed = Math.PI * m.TipDiameter * m.DesignN / 60.0;

            double theta = m.DesignTt / T0Ref;
            double delta = inlet.TotalPressure / P0Ref;
            m.DesignWc = inlet.MassFlow * Math.Sqrt(theta) / Math.Max(delta, 1e-6);

            double rho = inlet.TotalPressure / (Gas.R * m.DesignTt);
            double va = inlet.MassFlow / Math.Max(rho * m.AnnulusArea, 1e-9);
            // 只把这个绝对值留在读数里（`AnnulusAxialVelocity` 会用到），
            // 不参与图的坐标——原因见类说明里"为什么用相对流量系数"。
            m.AxialVelocityDesign = va;

            // 相对坐标：设计点就是 f=1，边界用缺省的 0.90 / 1.18。
            // 这里**不**把 tech 的绝对 φ 阈值搬进来——那是判据的尺，不是图的尺（见类说明）。
            m.DesignPhi = 1.0;
            m.PhiStall = 0.90;
            m.PhiChoke = 1.18;
            m.Slope = m.SlopeForSurgePrRatio();
            // 可调静子：只有轴流、级数 ≥ 4 的机器才算数。离心级与两级小压气机没有这套作动机构，
            // 给它们开这个口子等于凭空多出喘振裕度，比不给更糟。
            m.Vigv = spec.Vigv && spec.Stages >= 4 && spec.Kind == CompressorKind.Axial;

            m.BuildLines();
            return m;
        }
    }
}