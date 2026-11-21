using System;
using JetForge.Core;

namespace JetForge.Test
{
    internal static class IdleProbe
    {
        public static void Run()
        {
            EngineDesignData d = new EngineDesignData();   // 默认 JF-1 双轴涡扇
            foreach (double frac in new[] { 0.02, 0.04, 0.08, 0.12 })
            {
                EngineDesignData dd = d.Clone();
                dd.IdleThrustFrac = frac;
                DesignConstants c = OffDesign.DeriveDesignConstants(dd);
                Console.WriteLine(string.Format(
                    "设 {0:P1}｜T4Idle {1:F0}｜IdleFar {2:F4}｜LeanFar {3:F4}｜夹住 {4}｜可稳 {5}｜T4Idle下限?",
                    frac, c.T4Idle, c.IdleFar, c.LeanFar, c.IdleClamped, c.IdleSustainable));
            }

            // 油门到底时到底给多少推力（不是"设计期算出来的慢车点"，是模块真正会走的那条路）
            EngineDesignData starve = d.Clone();
            starve.IdleThrustFrac = 0.02;
            DesignConstants cs = OffDesign.DeriveDesignConstants(starve);
            ICycleSampler s = new RealtimeSampler(starve, cs);
            foreach (double thr in new[] { 0.0, 0.02, 0.05, 0.10, 0.20 })
            {
                FlightStepResult r = default(FlightStepResult);
                double spool = 0.0;
                bool init = false;
                for (int i = 0; i < 400; i++)
                {
                    r = FlightStep.Compute(s, thr, 0.02, 0.0, 0.0,
                        true, false, true, true, ref spool, ref init, 2.0, 1.2, 1.0, 0.05);
                }
                Console.WriteLine(string.Format(
                    "油门 {0:F2} → 命令 {1:F3}｜模式 {2}｜推力 {3:F2} kN｜N2 {4:F1}%｜T4 {5:F0}｜油气比 {6:F4}",
                    thr, r.Command, r.Mode, r.ThrustN / 1000.0, r.N2, r.Sample != null ? r.Sample.T4 : 0.0,
                    r.Sample != null && r.Sample.MdotCore > 1e-6 ? r.Sample.FuelFlow / r.Sample.MdotCore : 0.0));
            }
        }
    }
}