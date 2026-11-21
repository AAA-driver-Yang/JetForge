using System;

namespace JetForge.Core
{
    /// <summary>
    /// 一台发动机的"站位表"：每个部件在轴向哪儿、半径多大。
    ///
    /// 这层是为可视化编辑抽出来的：剖视图、三维网格、重量/造价都得从同一份站位算，
    /// 各算各的就会出现"图上风扇在 30%、模型里在 34%"这种对不上的事。
    /// 纯数学，不引用 UnityEngine——命令行回归也要能用它做断言。
    /// </summary>
    public sealed class EngineStations
    {
        /// <summary>机长（m）。</summary>
        public double L;
        /// <summary>进气口 / 喷口的轴向位置（机头为 +L/2）。</summary>
        public double YIn, YOut;
        /// <summary>风扇叶尖半径、核心机匣半径、进气口内半径、喷口半径。</summary>
        public double RFan, RCore, RIn, RNoz;
        /// <summary>
        /// 短舱外皮最粗处的半径（外径的一半）。
        ///
        /// 这个数以前不存在：玩家填的「短舱直径」被拿去抬 <see cref="RIn"/>，
        /// 而外皮画在 RIn×1.03 上，于是填 2.15 量出来是 2.21；填得比进气口小
        /// 还会被 Math.Max 整个吃掉，滑杆像坏的。现在短舱直径只管这一处。
        /// </summary>
        public double RNac;
        /// <summary>风扇、核心机起点、外涵喷口（风扇舱后缘）、加力段起点、喷管起点。</summary>
        public double YFan, YCoreStart, YBypass, YNoz, YAb;
        /// <summary>外涵喷口半径、加力段长度。</summary>
        public double RBypass, AbLen;
        /// <summary>
        /// 核心机匣收到喷管起点时的半径——**喷管根部就是这个数**。
        ///
        /// 以前机匣末点 0.99RCore、喷管根部写死 1.00RCore，无加力时机匣还多画一段
        /// 越过喷管起点、半径 RNoz；两段壳于是互相穿出来，"尾部直径不对"就是这么来的（§275）。
        /// 提到站位表里是为了能断言：喷管喉道是 0.94RNoz，机匣必须比它粗，否则喷管是"扩"出去的。
        /// </summary>
        public double RTail;
        public bool HasFan, HasAb, HasIpt, HighBypass;

        /// <summary>压气机段末尾（核心机匣开始收窄处）。</summary>
        public double YComp { get { return YCoreStart + (YAb - YCoreStart) * 0.45; } }
        /// <summary>燃烧室中心。</summary>
        public double YBurn { get { return YCoreStart + (YAb - YCoreStart) * 0.60; } }
        /// <summary>涡轮段中心。</summary>
        public double YTurb { get { return YCoreStart + (YAb - YCoreStart) * 0.80; } }
        /// <summary>外形最大半径（画图与包围盒用）。</summary>
        public double MaxRadius
        {
            get
            {
                double r = Math.Max(RNac, Math.Max(RIn, RCore));
                if (HasFan) r = Math.Max(r, RFan * 1.045);
                if (HighBypass) r = Math.Max(r, RBypass * 1.06);
                if (HasAb) r = Math.Max(r, RCore * 1.20 * 1.06);
                return r;
            }
        }

        public string Summary()
        {
            return string.Format(
                "长 {0:F2} m｜风扇 R{1:F3}｜核心 R{2:F3}｜进气 R{3:F3}｜短舱 R{4:F3}｜喷口 R{5:F3}｜{6}{7}{8}{9}",
                L, RFan, RCore, RIn, RNac, RNoz,
                HasFan ? "有风扇 " : "无风扇 ",
                HasAb ? "有加力 " : "",
                HasIpt ? "有中压涡轮 " : "",
                HighBypass ? "高涵道" : "");
        }
    }

    public static class EngineGeometry
    {
        /// <summary>
        /// 从设计算出站位表。公式与三维网格的 Measure() 完全同一套——
        /// 这里算出来的数就是模型上量得出来的数，剖视图才谈得上"尺寸占比正确"。
        /// </summary>
        public static EngineStations Of(EngineDesignData d)
        {
            EngineStations s = new EngineStations();
            if (d == null) { s.L = 3.0; s.RCore = 0.2; s.RFan = 0.2; s.RIn = 0.2; s.RNoz = 0.18; return s; }

            s.L = Math.Max((double)d.EffectiveLength, 0.4);
            s.YIn = s.L * 0.5;
            s.YOut = -s.L * 0.5;
            s.HasFan = d.HasFan;
            s.HasAb = d.Afterburner != null && d.Afterburner.Enabled;
            s.HasIpt = d.Ipt != null && d.Ipt.Stages > 0;

            s.RCore = Math.Max((double)d.CoreInletD * 0.5, 0.06);
            s.RFan = s.HasFan
                ? Math.Max((double)d.Fan.Diameter * 0.5, s.RCore * 1.05)
                : s.RCore;
            s.RIn = s.HasFan
                // 1.02 是"叶尖不许被唇口压住"的下限（唇口内缘 0.968RIn，叶尖 0.962RFan）
                ? Math.Max((double)d.InletDiameter * 0.5, s.RFan * 1.02)
                // 涡喷没有风扇，唇口要清开的是压气机面。以前这里是 0.95RCore，
                // 靠「短舱直径」顺手撑大才勉强盖住——短舱那栏一改口径，进气口就比压气机还细（§275）。
                : Math.Max((double)d.InletDiameter * 0.5, s.RCore * 1.02);
            // 玩家给了捕获直径，短舱就得跟着长：唇口是捕获面积，短舱比唇口还细是穿模。
            // 长到"面积口径的捕获半径"（喉道比折算后），而不是直径的一半，否则唇口还是会被短舱夹。
            // 没填（=0）时一个数都不动，老存档外形不变。
            if (d.Intake != null && d.Intake.CaptureDiameter > 0.0)
                s.RIn = Math.Max(s.RIn, Math.Sqrt(Math.Max(Intake.CaptureAreaOf(d), 0.0) / Math.PI));

            // 短舱外径：玩家填的「短舱直径」= 外皮最粗处的外径，只作用在这一处（§275）。
            // 下限两条：唇口外缘（1.02RIn，外皮不能比唇口还细）与风扇罩（1.045RFan）。
            double rNacFloor = s.HasFan
                ? Math.Max(s.RIn * 1.02, s.RFan * 1.045)
                : Math.Max(s.RIn * 1.02, s.RCore * 1.10);
            s.RNac = d.NacelleDiameter > 0.0
                ? Math.Max((double)d.NacelleDiameter * 0.5, rNacFloor)
                : rNacFloor;

            s.YFan = s.YIn - s.L * 0.10;
            s.YCoreStart = s.YFan - s.L * (s.HasFan ? 0.14 : 0.05);

            int stages = d.Lpt.Stages + d.Hpt.Stages + d.Ipt.Stages;
            double grow = 1.0 + Math.Max(0, Math.Min(12, stages)) * 0.008;
            s.RNoz = s.RCore * 0.88 * grow;
            if (s.HasAb) s.RNoz *= 1.06;

            s.YNoz = s.YOut + s.L * 0.10;
            s.AbLen = 0.0;
            if (s.HasAb)
            {
                double want = d.Afterburner.Length > 0.0 ? (double)d.Afterburner.Length : s.RCore * 3.4;
                // 不许把核心机吃掉：核心段最多让出 32%
                double room = Math.Max(0.06, (s.YCoreStart - s.YNoz) * 0.32);
                s.AbLen = Math.Max(0.06, Math.Min(want, Math.Min(room, s.L * 0.45)));
            }
            s.YAb = s.YNoz + s.AbLen;

            double bpr = 0.0;
            // 探测豁免：算不出涵道比就当 0（按"不是高涵道比"排布外形），比抛异常让整个零件没网格好。
            try { bpr = d.BypassRatio(); } catch { }
            s.HighBypass = s.HasFan && bpr > 2.5;
            // 外涵喷口：以前高涵道取 min(RFan×0.86, RCore×1.72)，大涵道比时 RCore 不到 RFan 的三分之一，
            // 那个 min 把外涵半径压到 0.55RFan，风扇舱在 0.26L 里从 1.076 塌到 0.568——机匣像被捏瘪（§275）。
            // 现在只留"至少包住核心机"这条下限，高低涵道在长度上分家、半径不再互相挤。
            s.YBypass = s.YFan - s.L * 0.26;
            s.RBypass = Math.Max(s.RFan * 0.86, s.RCore * 1.06);

            // 机匣尾端半径：加力段自己粗一圈，机匣就让它接；无加力时机匣得收到喷管喉道外面
            // （喉道 RNoz×0.94），否则喷管是"扩"出去的。
            s.RTail = s.HasAb
                ? s.RCore * 1.06
                : Math.Max(s.RNoz * 1.02, s.HighBypass ? s.RCore * 0.90 : s.RCore * 0.99);
            return s;
        }
    }
}