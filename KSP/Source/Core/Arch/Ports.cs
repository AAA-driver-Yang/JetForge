using System;
using System.Collections.Generic;
using System.Text;

namespace JetForge.Core
{
    /// <summary>流道截面型式。这两种占了发动机的九成：圆管（进气道口）与环道（压气机/涡轮）。</summary>
    public enum PortShape
    {
        Circular = 0,
        Annular = 1,
    }

    /// <summary>
    /// 一个"流道接口"：某部件某一端的截面。
    ///
    /// 交接文档 B 要求"几何匹配与热力学匹配分别校核"。热力学那边有站表管着（总压/总温/流量一路传下去），
    /// 几何这边以前全靠人看图：直径差一圈、轮毂比反了、环形面积比圆面积小一半，这些都不会报错，
    /// 只会在游戏里表现为"这台发动机看着怪但还能飞"。所以这里给几何也配一张接口表。
    ///
    /// 只用直径与轮毂比描述，是因为现在设计数据里就这些（<see cref="CompressorSpec.TipOf"/>、
    /// <see cref="FanSpec.HubTipRatio"/>）。环形面积的坑恰恰在这里：叶尖直径一样的两台机器，
    /// 轮毂比 0.42 和 0.75 的通流面积差 2.3 倍——按直径比校核会把它们判成"匹配"。
    /// </summary>
    public struct FlowPort
    {
        public PortShape Shape;
        /// <summary>外径（环形道的外机匣直径 / 圆管的直径）m。</summary>
        public double OuterDiameter;
        /// <summary>内径（环形道的轮毂直径），圆管为 0。</summary>
        public double InnerDiameter;
        public string Label;

        public static FlowPort Circle(double diameter, string label)
        {
            FlowPort p = new FlowPort();
            p.Shape = PortShape.Circular;
            p.OuterDiameter = diameter;
            p.InnerDiameter = 0.0;
            p.Label = label;
            return p;
        }

        public static FlowPort Annulus(double outerDiameter, double innerDiameter, string label)
        {
            FlowPort p = new FlowPort();
            p.Shape = PortShape.Annular;
            p.OuterDiameter = outerDiameter;
            p.InnerDiameter = innerDiameter;
            p.Label = label;
            return p;
        }

        /// <summary>通流面积 m²。环道是外圈减内圈——这就是"直径一样面积可以差两倍"的来源。</summary>
        public double Area
        {
            get
            {
                double dO = GasState.IsFinite(OuterDiameter) ? OuterDiameter : 0.0;
                double dI = GasState.IsFinite(InnerDiameter) ? InnerDiameter : 0.0;
                if (Shape == PortShape.Circular) return Math.PI * 0.25 * dO * dO;
                if (dI >= dO) return 0.0;
                return Math.PI * 0.25 * (dO * dO - dI * dI);
            }
        }

        public double HubTipRatio
        {
            get { return Shape == PortShape.Annular && OuterDiameter > 1e-9 ? InnerDiameter / OuterDiameter : 0.0; }
        }

        public bool IsValid
        {
            get
            {
                if (!GasState.IsFinite(OuterDiameter) || OuterDiameter <= 0.0) return false;
                if (Shape == PortShape.Circular) return true;
                return GasState.IsFinite(InnerDiameter) && InnerDiameter >= 0.0 && InnerDiameter < OuterDiameter;
            }
        }

        public string Describe()
        {
            if (Shape == PortShape.Circular)
                return string.Format("{0} 圆管 Ø{1:F3}｜面积 {2:F4} m²", Label, OuterDiameter, Area);
            return string.Format("{0} 环道 Ø{1:F3}/Ø{2:F3}（轮毂比 {3:F2}）｜面积 {4:F4} m²",
                Label, OuterDiameter, InnerDiameter, HubTipRatio, Area);
        }
    }

    /// <summary>相邻两个接口的对齐情况。只描述几何，不参与任何热力学计算。</summary>
    public sealed class PortLink
    {
        public FlowPort Upstream;
        public FlowPort Downstream;
        /// <summary>
        /// 这一段是"管道"而不是"部件交界面"。进气道喉道到压气机进口本来就要扩压几倍，
        /// 拿 25% 的容差去量它只会每次都报警，所以它照样列出来、但不计入"面积跳变"。
        /// </summary>
        public bool IsDuct;

        /// <summary>下游面积 / 上游面积。&gt;1 是扩压（面积变大、流速变低），&lt;1 是收敛。</summary>
        public double AreaRatio
        {
            get
            {
                double a = Upstream.Area;
                return a > 1e-12 ? Downstream.Area / a : 0.0;
            }
        }

        /// <summary>面积失配的百分比（对称口径：|下游−上游|/上游）。</summary>
        public double MismatchPct
        {
            get { return Math.Abs(AreaRatio - 1.0) * 100.0; }
        }

        public bool Aligned(double tolerancePct)
        {
            return Upstream.IsValid && Downstream.IsValid && MismatchPct <= tolerancePct;
        }

        public string Describe()
        {
            return string.Format("{0} → {1}｜面积比 {2:F3}（差 {3:F1}%）",
                Upstream.Label, Downstream.Label, AreaRatio, MismatchPct);
        }
    }

    /// <summary>
    /// 把整台机器的流道接口按气路顺序串起来，并回答"哪里对不上"。
    ///
    /// 判据只有一条：相邻两个截面的**通流面积**不该差太多。差得多不一定错（压气机到燃烧室
    /// 本来就要扩压），但不该悄无声息——所以这里报的是"需要人看一眼的地方"，不是禁飞令。
    /// 真正的禁飞条件（面积≤0、轮毂比≥1）单独用 <see cref="Problems"/> 数出来。
    /// </summary>
    public static class Ports
    {
        /// <summary>面积差超过这个百分数就要在读数里提一句。25% 是按真机流道收敛率给的：一级压气机的面积收缩大致 15~20%。</summary>
        public const double DefaultTolerancePct = 25.0;

        /// <summary>
        /// 按气路顺序列出各个截面：进气道喉道 → 风扇 → 低压压气机 → 高压压气机 → 燃烧室 → 各级涡轮。
        /// 没有的部件（涡喷没有风扇）直接跳过。
        ///
        /// 部件的进口与出口都列出来，因为"流道是不是收敛"恰恰要拿进口比出口看：
        /// 压气机每级面积收缩 15~20% 是正常的，收缩 60% 就是把叶片顶到机匣外面去了。
        /// </summary>
        public static List<FlowPort> Profile(EngineDesignData d)
        {
            List<FlowPort> ports = new List<FlowPort>();
            if (d == null) return ports;

            ports.Add(IntakeFace(d));

            if (d.HasFan)
            {
                ports.Add(AnnulusOf(d.Fan.Diameter, d.Fan.HubTipRatio, "风扇进口"));
                // 风扇前后同一个环形道（没有逐级收缩数据），面积比 1.0——如实登记，不编数据。
                ports.Add(AnnulusOf(d.Fan.Diameter, d.Fan.HubTipRatio, "风扇出口"));
            }

            if (d.Lpc != null && d.Lpc.Stages > 0)
            {
                ports.Add(CompressorPort(d.Lpc, 0, "低压压气机进口"));
                ports.Add(CompressorPort(d.Lpc, d.Lpc.Stages - 1, "低压压气机出口"));
            }

            if (d.Hpc != null && d.Hpc.Stages > 0)
            {
                ports.Add(CompressorPort(d.Hpc, 0, "高压压气机进口"));
                ports.Add(CompressorPort(d.Hpc, d.Hpc.Stages - 1, "高压压气机出口"));
            }

            // 燃烧室：外径沿用压气机出口。设计数据里没有火焰筒内径，
            // 就按"扩压器把气流摊开、火焰筒占住中间那块"的净面积取与压气机出口相同——
            // 与其编一个会天天报警的轮毂比，不如老实承认这一段的几何现在没建模。
            FlowPort prev = ports.Count > 0 ? ports[ports.Count - 1] : IntakeFace(d);
            ports.Add(FlowPort.Annulus(prev.OuterDiameter, prev.InnerDiameter, "燃烧室出口"));

            if (d.Hpt != null && d.Hpt.IsPresent)
            {
                ports.Add(TurbinePort(d.Hpt, 0, "高压涡轮进口"));
                ports.Add(TurbinePort(d.Hpt, d.Hpt.Stages - 1, "高压涡轮出口"));
            }

            if (d.Ipt != null && d.Ipt.IsPresent)
            {
                ports.Add(TurbinePort(d.Ipt, 0, "中压涡轮进口"));
                ports.Add(TurbinePort(d.Ipt, d.Ipt.Stages - 1, "中压涡轮出口"));
            }

            if (d.Lpt != null && d.Lpt.IsPresent)
            {
                ports.Add(TurbinePort(d.Lpt, 0, "低压涡轮进口"));
                ports.Add(TurbinePort(d.Lpt, d.Lpt.Stages - 1, "低压涡轮出口"));
            }

            return ports;
        }

        /// <summary>把相邻两个截面配成对。</summary>
        public static List<PortLink> Chain(EngineDesignData d)
        {
            List<PortLink> links = new List<PortLink>();
            List<FlowPort> ports = Profile(d);
            for (int i = 0; i + 1 < ports.Count; i++) links.Add(Make(ports[i], ports[i + 1]));

            // 第一段（进气道喉道 → 第一台旋转部件）是管道：喉道本来就比发动机面小一大截，
            // 拿部件交界的容差去量它只会每次报警。
            if (links.Count > 0) links[0].IsDuct = true;
            return links;
        }

        /// <summary>气路最后一端（喷管进口）。喷管自己的喉道/出口面积由设计常数反解，不在设计数据里。</summary>
        public static FlowPort Terminal(EngineDesignData d)
        {
            List<FlowPort> ports = Profile(d);
            return ports.Count > 0 ? ports[ports.Count - 1] : IntakeFace(d);
        }

        /// <summary>发动机面（进气道与核心机的交界面）：用捕获面积折算成等面积圆。</summary>
        public static FlowPort IntakeFace(EngineDesignData d)
        {
            double a = 0.0;
            try { a = Intake.CaptureAreaOf(d); }
            catch { a = 0.0; }
            if (!GasState.IsFinite(a) || a <= 0.0) a = Math.PI * 0.25 * 0.5 * 0.5;
            return FlowPort.Circle(Math.Sqrt(4.0 * a / Math.PI), "进气道喉道");
        }

        public static FlowPort CompressorPort(CompressorSpec c, int stage, string label)
        {
            double tip = c.TipOf(stage);
            return AnnulusOf(tip, c.HubTipRatio, label);
        }

        public static FlowPort TurbinePort(TurbineSpec t, int stage, string label)
        {
            double tip = t.TipOf(stage);
            return AnnulusOf(tip, t.HubTipRatio, label);
        }

        private static FlowPort AnnulusOf(double tip, double hubTipRatio, string label)
        {
            double r = GasState.IsFinite(hubTipRatio) ? hubTipRatio : 0.0;
            if (r < 0.0) r = 0.0;
            return FlowPort.Annulus(tip, tip * r, label);
        }

        private static PortLink Make(FlowPort up, FlowPort down, bool isDuct = false)
        {
            PortLink l = new PortLink();
            l.Upstream = up;
            l.Downstream = down;
            l.IsDuct = isDuct;
            return l;
        }

        /// <summary>
        /// 数"这不是审美问题、是数据坏了"的接口：截面积算成 0 或负、轮毂比 ≥1、直径非有限。
        /// 返回坏掉的个数，并把第一处的原因写进 <paramref name="message"/>。
        /// </summary>
        public static int Problems(List<PortLink> links, out string message)
        {
            message = "";
            if (links == null) { message = "接口表是空的"; return 1; }
            int bad = 0;
            for (int i = 0; i < links.Count; i++)
            {
                if (!links[i].Upstream.IsValid)
                {
                    bad++;
                    if (message.Length == 0)
                        message = string.Format("上游接口 {0} 不成立（Ø{1:F4}，轮毂比 {2:F3} 或直径非正/非有限）",
                            links[i].Upstream.Label, links[i].Upstream.OuterDiameter, links[i].Upstream.HubTipRatio);
                }
                if (!links[i].Downstream.IsValid)
                {
                    bad++;
                    if (message.Length == 0)
                        message = string.Format("下游接口 {0} 不成立（Ø{1:F4}，轮毂比 {2:F3} 或直径非正/非有限）",
                            links[i].Downstream.Label, links[i].Downstream.OuterDiameter, links[i].Downstream.HubTipRatio);
                }
            }
            return bad;
        }

        /// <summary>面积失配超出容差的对数：这些不是错，是"该有人看一眼"。管道段不算。</summary>
        public static int Loose(List<PortLink> links, double tolerancePct)
        {
            int n = 0;
            if (links == null) return 0;
            for (int i = 0; i < links.Count; i++)
            {
                if (links[i].IsDuct) continue;
                if (links[i].Upstream.IsValid && links[i].Downstream.IsValid && !links[i].Aligned(tolerancePct)) n++;
            }
            return n;
        }

        public static string Describe(List<PortLink> links, double tolerancePct)
        {
            if (links == null || links.Count == 0) return "接口表：空";
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("接口表：{0} 对｜容差 {1:F0}%", links.Count, tolerancePct);
            for (int i = 0; i < links.Count; i++)
            {
                sb.Append("\n  ");
                sb.Append(links[i].Describe());
                if (links[i].IsDuct) sb.Append("  （管道段，不计跳变）");
                else if (links[i].Upstream.IsValid && links[i].Downstream.IsValid && !links[i].Aligned(tolerancePct))
                    sb.Append("  ← 面积跳变");
            }
            return sb.ToString();
        }
    }
}