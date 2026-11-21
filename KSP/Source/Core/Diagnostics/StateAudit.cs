using System;
using System.Collections.Generic;

namespace JetForge.Core.Diagnostics
{
    /// <summary>
    /// 运行时数据的"自相矛盾"审计。
    ///
    /// 为什么要有这个文件：这些东西以前一个都不会报。
    /// · `Ok = true` 同时 `Errors` 里有三条——下游只看 `Ok`，于是把一条算错的解当成功；
    /// · 喉道面积算出 0，`AvailableMassFlow` 变成 0，推力变成 0，报表上只写"飞不动"；
    /// · 涡喷的 `Bpr` 却给了 0.6，外涵喷管于是拿一份不存在的流量在算推力；
    /// · 一个数成了 NaN，`if (x &gt; 0)` 全都不成立，所有限流判据静默失效，面板上一切正常。
    ///
    /// 审计不是再算一遍物理，只是核对"这份结果自己跟自己对得上吗"。
    /// 它只描述，不修数——修由各自求解器负责，这里负责让问题显形。
    /// </summary>
    public static class StateAudit
    {
        public struct Issue
        {
            public string Where;
            public string What;
            public bool IsError;
        }

        private const string WhereDesign = "设计点";
        private const string WhereIntake = "进气道";
        private const string WhereOff = "非设计点";

        // ------------------------------------------------------------------
        // 设计点
        // ------------------------------------------------------------------
        public static List<Issue> AuditDesign(CycleResult r, EngineDesignData d)
        {
            List<Issue> o = new List<Issue>();
            if (r == null)
            {
                Add(o, WhereDesign, "结果为空：求解器没返回，下游拿到 null", true);
                return o;
            }

            // 1) Ok 和 Errors 必须一致。这是最容易骗人的一处：Ok 缺省 true，
            //    只要有人忘了在出错分支里把它置 false，错误就永远看不见。
            if (r.Ok && r.Errors != null && r.Errors.Count > 0)
                Add(o, WhereDesign, string.Format("Ok=true 但带着 {0} 条错误：{1}", r.Errors.Count, r.Errors[0]), true);
            if (!r.Ok && (r.Errors == null || r.Errors.Count == 0))
                Add(o, WhereDesign, "Ok=false 却一条错误都没写：下游不知道该修哪里", true);

            // 2) 关键量必须是有限数。NaN 会让所有 "> 0" 的判断同时为假，
            //    于是每一条限流/告警都不触发——表面上"一切正常"。
            Finite(o, WhereDesign, "空气流量 MdotAir", r.MdotAir, true);
            Finite(o, WhereDesign, "内涵流量 MdotCore", r.MdotCore, true);
            Finite(o, WhereDesign, "推力 Thrust", r.Thrust, true);
            Finite(o, WhereDesign, "内涵推力 ThrustCore", r.ThrustCore, true);
            Finite(o, WhereDesign, "外涵推力 ThrustBypass", r.ThrustBypass, true);
            Finite(o, WhereDesign, "总压比 Opr", r.Opr, true);
            Finite(o, WhereDesign, "涵道比 Bpr", r.Bpr, true);
            Finite(o, WhereDesign, "涡轮前温度 T4", r.T4, true);
            Finite(o, WhereDesign, "燃油流量 FuelFlow", r.FuelFlow, true);
            Finite(o, WhereDesign, "耗油率 Sfc", r.Sfc, true);

            // 3) 有推力就必须有空气。这台机器不可能凭空发力。
            if (r.Thrust > 0.0 && r.MdotAir <= 0.0)
                Add(o, WhereDesign, string.Format("推力 {0:F1} N 但空气流量 {1:F3} kg/s：没有气就没有推力", r.Thrust, r.MdotAir), true);
            if (r.FuelFlow > 0.0 && r.MdotCore <= 0.0)
                Add(o, WhereDesign, string.Format("在烧油（{0:F4} kg/s）但内涵一点气都没有", r.FuelFlow), true);

            // 4) 涵道比与风扇必须同时存在或同时不存在。
            //    涡喷报了 Bpr，外涵喷管就会拿一份不存在的流量去算推力（这一条踩过）。
            if (d != null)
            {
                if (!d.HasFan && r.Bpr > 1e-6)
                    Add(o, WhereDesign, string.Format("涡喷却报了涵道比 {0:F3}：外涵喷管会拿不存在的流量算推力", r.Bpr), true);
                if (d.HasFan && r.Bpr <= 0.0)
                    Add(o, WhereDesign, "有风扇却涵道比为 0：外涵全是死气，推力会凭空少一截", true);
            }

            // 5) 燃烧室必须有温升。油耗给了、温升没有，说明热力链断了。
            if (r.Ok && r.FuelFlow > 0.0 && r.T4 <= r.T3)
                Add(o, WhereDesign, string.Format("烧了油但 T4 {0:F1} K 不高于 T3 {1:F1} K", r.T4, r.T3), true);

            // 6) 收缩喷管的出口马赫不该跑过 1（收敛-扩张才算得出来 >1）。
            if (r.NozzleMachE > 1.02 && r.NozzleAreaRatio <= 1.001)
                Add(o, WhereDesign, string.Format("收缩喷管出口马赫 {0:F3} > 1，但没有扩张段", r.NozzleMachE), false);

            // 7) 转速百分比应当在 0~105 之间（设计点 100%，瞬态可以略微越过）。
            if (r.Rpm != null)
            {
                foreach (KeyValuePair<int, double> kv in r.Rpm)
                {
                    if (!GasState.IsFinite(kv.Value))
                        Add(o, WhereDesign, string.Format("轴 {0} 的转速不是数", kv.Key), true);
                }
            }
            return o;
        }

        // ------------------------------------------------------------------
        // 进气道
        // ------------------------------------------------------------------
        public static List<Issue> AuditIntake(IntakeSolution s)
        {
            List<Issue> o = new List<Issue>();
            if (s == null)
            {
                Add(o, WhereIntake, "结果为空：没有进气道解，发动机等于在真空里吸气", true);
                return o;
            }

            if (!s.Valid)
                return o;   // 已经明确失败并带了 FailureCode，不算自相矛盾

            Finite(o, WhereIntake, "总压恢复 Tpr", s.Tpr, true);
            Finite(o, WhereIntake, "迎面马赫 FaceMach", s.FaceMach, true);
            Finite(o, WhereIntake, "喉道面积 ThroatArea", s.ThroatArea, true);
            Finite(o, WhereIntake, "捕获面积 CaptureArea", s.CaptureArea, true);
            Finite(o, WhereIntake, "发动机面面积 FaceArea", s.FaceArea, true);
            Finite(o, WhereIntake, "捕获流量 CapturedMassFlow", s.CapturedMassFlow, true);
            Finite(o, WhereIntake, "可用流量 AvailableMassFlow", s.AvailableMassFlow, true);

            if (s.ThroatArea <= 0.0)
                Add(o, WhereIntake, string.Format("喉道面积 {0:F4} m²（不是正数），这颗解不该是 Valid", s.ThroatArea), true);
            if (s.CaptureArea <= 0.0)
                Add(o, WhereIntake, string.Format("捕获面积 {0:F4} m²（不是正数）", s.CaptureArea), true);

            // 总压恢复只可能 ≤ 1。大于 1 是算错，不是"性能特别好"。
            if (s.Tpr > 1.0001)
                Add(o, WhereIntake, string.Format("总压恢复 {0:F4} > 1：进气道不会自己升压", s.Tpr), true);
            if (s.Tpr <= 0.0)
                Add(o, WhereIntake, string.Format("总压恢复 {0:F4} ≤ 0：把进气口堵死了", s.Tpr), true);

            // 起动与未起动不可能同时成立。
            if (s.Started && s.Unstarted)
                Add(o, WhereIntake, "同一档工况同时标了起动和未起动", true);

            // 三个流量的语义，别拿错口径当守恒：
            //   RequiredMassFlow  = 发动机这个工况想吃多少
            //   CapturedMassFlow  = 实际进到管道里的（= min(想吃, 喉道能过)）
            //   AvailableMassFlow = **喉道能力**（能力可以大于需求，那是余量，不是凭空多的气）
            // 所以能查的是"捕获不该超过需求"和"溢流/阻力跟自己的口径对得上"。
            if (s.CapturedMassFlow > s.RequiredMassFlow + Math.Max(0.01 * s.RequiredMassFlow, 0.01))
                Add(o, WhereIntake, string.Format("捕获 {0:F2} kg/s 大于需求 {1:F2} kg/s：唇口凭空多吸气",
                    s.CapturedMassFlow, s.RequiredMassFlow), true);

            if (s.Spilling != (s.SpillageMassFlow > 1e-6))
                Add(o, WhereIntake, string.Format("溢流 {0:F3} kg/s 与「正在溢流」标记 {1} 不一致",
                    s.SpillageMassFlow, s.Spilling), true);

            if (s.FlightVelocity > 1.0 && s.SpillageMassFlow > 1e-6)
            {
                double drag = s.SpillageMassFlow * s.FlightVelocity;
                if (Math.Abs(drag - s.InletDrag) > Math.Max(0.01 * Math.Abs(drag), 1.0))
                    Add(o, WhereIntake, string.Format("溢流阻力 {0:F0} N ≠ 溢流 {1:F2} kg/s × 速度 {2:F1} m/s",
                        s.InletDrag, s.SpillageMassFlow, s.FlightVelocity), true);
            }

            if (s.CapturedMassFlow > 1e-6 && s.AvailableMassFlow <= 1e-9)
                Add(o, WhereIntake, string.Format("捕获了 {0:F2} kg/s，可用流量却是 0：气进去了出不来",
                    s.CapturedMassFlow), true);

            if (s.AvailableMassFlow < -1e-9)
                Add(o, WhereIntake, string.Format("可用流量为负（{0:F3}）", s.AvailableMassFlow), true);

            if (s.BleedMassFlow < -1e-9)
                Add(o, WhereIntake, string.Format("抽气流量为负（{0:F4}）", s.BleedMassFlow), true);

            // 面马赫的物理范围：太低是堵住，太高是没法再减速。
            if (s.FaceMach > 1.0 + 1e-6)
                Add(o, WhereIntake, string.Format("发动机面马赫 {0:F2} > 1：压气机前面还有激波", s.FaceMach), false);
            if (s.FaceMach <= 0.0)
                Add(o, WhereIntake, "发动机面马赫 ≤ 0：空气没在动", true);

            // 喉道壅塞的判据是"需求超过喉道能力"，那么余量就该 ≤ 0。
            if (s.ThroatChoked && s.ThroatMargin > 1e-3)
                Add(o, WhereIntake, string.Format("标了喉道壅塞，但喉道余量还有 {0:F3}", s.ThroatMargin), true);
            if (!s.ThroatChoked && s.ThroatMargin < -1e-3)
                Add(o, WhereIntake, string.Format("喉道余量已经 {0:F3}（负数）却没标壅塞", s.ThroatMargin), true);

            if (s.DistortionIndex < -1e-9 || s.DistortionIndex > 0.4)
                Add(o, WhereIntake, string.Format("进气畸变 {0:F3} 超出 0~0.4 的经验范围", s.DistortionIndex), false);

            if (s.InletDrag < -1e-6)
                Add(o, WhereIntake, string.Format("溢流阻力为负（{0:F1} N）", s.InletDrag), true);

            return o;
        }

        // ------------------------------------------------------------------
        // 非设计点
        // ------------------------------------------------------------------
        public static List<Issue> AuditOffDesign(OffDesignResult r, EngineDesignData d)
        {
            List<Issue> o = new List<Issue>();
            if (r == null)
            {
                Add(o, WhereOff, "结果为空", true);
                return o;
            }

            if (r.Ok && r.Errors != null && r.Errors.Count > 0)
                Add(o, WhereOff, string.Format("Ok=true 但带着 {0} 条错误：{1}", r.Errors.Count, r.Errors[0]), true);

            Finite(o, WhereOff, "净推力 NetThrust", r.NetThrust, true);
            Finite(o, WhereOff, "空气流量 MdotAir", r.MdotAir, true);
            Finite(o, WhereOff, "总压比 Opr", r.Opr, true);
            Finite(o, WhereOff, "涡轮前温度 T4", r.T4, true);

            if (r.MdotAir <= 0.0 && r.Thrust > 0.0)
                Add(o, WhereOff, string.Format("推力 {0:F1} N 但空气流量 {1:F3} kg/s", r.Thrust, r.MdotAir), true);

            // 油门关到底还在烧油，只在风车状态说得通。
            if (r.Throttle <= 1e-6 && r.FuelFlow > 1e-3 && !r.Windmill && !r.SelfSustaining)
                Add(o, WhereOff, string.Format("油门 0 却仍在烧 {0:F4} kg/s", r.FuelFlow), false);

            if (d != null && !d.HasFan && r.Bpr > 1e-6)
                Add(o, WhereOff, string.Format("涡喷却报了涵道比 {0:F3}", r.Bpr), true);

            // 转速百分比：负数或离谱到 130% 以上才是"积分发散/没夹住"，
            // 105~130% 是**模型有意为之**——整套部件特性按换算转速（N/√θ）限，
            // 高空高速来流总温高，物理转速自然高过 100% 而换算转速仍在限内。
            // 以前这两种情况都长得一样（都只写个数字），现在分开说。
            if (r.SpeedPct != null)
            {
                foreach (KeyValuePair<int, double> kv in r.SpeedPct)
                {
                    if (!GasState.IsFinite(kv.Value))
                        Add(o, WhereOff, string.Format("轴 {0} 的转速百分比不是数", kv.Key), true);
                    else if (kv.Value < -1.0 || kv.Value > 130.0)
                        Add(o, WhereOff, string.Format("轴 {0} 的转速 {1:F1}% 离谱（积分发散或没夹住）", kv.Key, kv.Value), true);
                    else if (kv.Value > 105.0)
                        Add(o, WhereOff, string.Format("轴 {0} 物理转速 {1:F1}% 超过 105%：本模型限的是换算转速，真机会限油", kv.Key, kv.Value), false);
                }
            }

            // 没收敛却给了 Ok：这一条是最危险的，因为面板只会看 Ok。
            if (r.Ok && !r.Converged)
                Add(o, WhereOff, string.Format("Ok=true 但没收敛（迭代 {0} 次）", r.Iterations), false);

            // ---- 喷管几何自洽（§155 之后 Ae 由几何定，这几条才有意义） ----
            // 出口面积、喉道面积、分离状态三者是同一件事的三种说法，互相矛盾就说明
            // 有人只改了一条线。判据都带前提，宁可漏报也不许把正常工况报成错。
            if (!GasState.IsFinite(r.NozzleAeMismatch))
                Add(o, WhereOff, "喷管出口面积偏差不是数", true);

            if (r.MdotAir > 0.0 && r.NozzleExitArea <= 0.0)
                Add(o, WhereOff, string.Format("有 {0:F1} kg/s 在流，喷管出口面积却是 {1:F5} m²",
                    r.MdotAir, r.NozzleExitArea), true);

            if (r.NozzleSeparated && r.A8Used > 0.0 && r.NozzleExitArea > r.A8Used * 1.02)
                Add(o, WhereOff, string.Format("喷流已分离，出口面积 {0:F5} m² 却比喉道 {1:F5} m² 还大",
                    r.NozzleExitArea, r.A8Used), true);

            if (d != null && d.Nozzle == NozzleKind.ConvergingDiverging && r.ChokedNozzle
                && !r.NozzleSeparated && r.NozzleAreaRatio > 1.01 && r.A8Used > 0.0
                && r.NozzleExitArea < r.A8Used * 1.005)
                Add(o, WhereOff, string.Format("收扩喷管壅塞且未分离，出口/喉道 = {0:F3}，面积比 {1:F3} 没起作用",
                    r.NozzleExitArea / r.A8Used, r.NozzleAreaRatio), true);

            return o;
        }

        // ------------------------------------------------------------------
        // 汇总工具
        // ------------------------------------------------------------------
        public static int ErrorCount(List<Issue> issues)
        {
            int n = 0;
            if (issues == null) return 0;
            for (int i = 0; i < issues.Count; i++) if (issues[i].IsError) n++;
            return n;
        }

        public static List<string> Lines(List<Issue> issues)
        {
            List<string> o = new List<string>();
            if (issues == null) return o;
            for (int i = 0; i < issues.Count; i++)
                o.Add(string.Format("[{0}] {1}", issues[i].IsError ? "错" : "疑", issues[i].What));
            return o;
        }

        public static string Summarize(List<Issue> issues)
        {
            if (issues == null || issues.Count == 0) return "数据自洽";
            return string.Format("{0} 条自相矛盾（其中 {1} 条算错）", issues.Count, ErrorCount(issues));
        }

        private static void Finite(List<Issue> o, string where, string label, double v, bool isError)
        {
            if (double.IsNaN(v))
                Add(o, where, label + " 是 NaN：所有 \"> 0\" 判断都会静默失败", isError);
            else if (double.IsInfinity(v))
                Add(o, where, label + " 是无穷大", isError);
        }

        private static void Add(List<Issue> o, string where, string what, bool isError)
        {
            Issue i = new Issue();
            i.Where = where;
            i.What = what;
            i.IsError = isError;
            o.Add(i);
        }
    }
}