using System;
using System.Collections;
using JetForge.Core;

namespace JetForge.Diagnostics
{
    public partial class JetForgeSelfTest
    {
        /// <summary>
        /// 慢车与"原版桥往返"：原版油门 0 是零推力，真机油门 0 是慢车。
        /// 这一段不读主流程任何局部变量（所有中间量都在自己的 try 里），所以整段搬得出来。
        /// </summary>
        private void SectionIdleFloor()
        {
            // ---- 慢车：原版油门 0 是零推力，真机油门 0 是慢车 ----
            try
            {
                EngineDesignData idd = new EngineDesignData();
                DesignConstants idc = OffDesign.DeriveDesignConstants(idd);
                PerformanceGrid idg = GridFactory.Build(idd, idc);
                PerformanceGrid.Sample idMax = idg.Interp(1.0, 0.0, 0.0);
                double idFloor = StockBridge.FloorInput(0.0);
                PerformanceGrid.Sample idIdle = idg.Interp(idFloor, 0.0, 0.0);
                Log(string.Format("慢车抬底：输入油门 0 → 生效 {0:F2}；满油门 {1:F2} kN，慢车 {2:F2} kN（{3:F1}%）N2 {4:F1}%",
                    idFloor, idMax.Thrust / 1000.0, idIdle.Thrust / 1000.0,
                    idIdle.Thrust / Math.Max(1.0, idMax.Thrust) * 100.0, idIdle.N2));

                FuelBridge idZero = StockBridge.Compute(idIdle.Thrust / 1000.0, idIdle.FuelFlow / 1000.0, 0.0);
                Log(string.Format("  不抬底（原版油门 0）：油门为 0 时慢车：{0}；原版算回推力 {1:F3} kN —— 慢车在游戏里根本不存在",
                    YN(idZero.Idle), idZero.StockThrust(0.0)));

                FuelBridge idB = StockBridge.Compute(idIdle.Thrust / 1000.0, idIdle.FuelFlow / 1000.0, idFloor);
                Log(string.Format("  抬底后：maxFuelFlow {0:F6} t/s　Isp {1:F0} s　原版算回 {2:F2} kN（模型 {3:F2}）",
                    idB.MaxFuelFlow, idB.Isp, idB.StockThrust(idFloor), idIdle.Thrust / 1000.0));

                // ---- 原版桥往返：热力图给多少，原版就必须算出多少 ----
                // 玩家报的"实飞推力明显大于热力图"就是这条不成立。三档油门逐一钉住：
                // 我们的推力 → 反解 maxFuelFlow/Isp → 用原版那条公式（含油门重映射）算回推力。
                double worst = 0.0;
                for (int k = 0; k < 3; k++)
                {
                    double lever = k == 0 ? 0.05 : (k == 1 ? 0.50 : 1.00);
                    PerformanceGrid.Sample sp = idg.Interp(lever, 0.0, 0.0);
                    FuelBridge fb = StockBridge.Compute(sp.Thrust / 1000.0, sp.FuelFlow / 1000.0, lever);
                    double back = fb.StockThrust(lever) * 1000.0;
                    double err = Math.Abs(back - sp.Thrust) / Math.Max(sp.Thrust, 1e-6) * 100.0;
                    if (err > worst) worst = err;
                    Log(string.Format("  原版桥往返：油门 {0:P0} → 我们 {1:F2} kN｜原版算回 {2:F2} kN｜偏差 {3:F2}%",
                        lever, sp.Thrust / 1000.0, back / 1000.0, err));
                }
                Log(string.Format("  断言：原版算回的推力与热力图一致 {0}（最差偏差 {1:F2}%）",
                    worst < 0.5 ? "对" : "不对", worst));
            }
            catch (Exception e) { Log("慢车异常: " + e); }
        }

        /// <summary>
        /// 飞行链路整段（Core/FlightStep）：模块每帧走的数值路径是个纯函数，游戏里跑的数
        /// 必须和命令行跑出来的一模一样，否则说明哪边的环境（Time/高度/油门）取错了。
        /// 同样不读主流程的局部变量。
        /// </summary>
        private void SectionFlightStep()
        {
            // ---- 飞行链路整段（Core/FlightStep）：与命令行第 21 段逐位对照 ----
            // 这一段的意义：模块每帧走的数值路径现在是一个纯函数，游戏里跑的数
            // 必须和命令行跑出来的一模一样，否则说明哪边的环境（Time/高度/油门）取错了。
            try
            {
                EngineDesignData fd = new EngineDesignData();
                DesignConstants fdc = OffDesign.DeriveDesignConstants(fd);
                PerformanceGrid fg = GridFactory.Build(fd, fdc);
                double tauU = JetForge.Core.Spool.TauUp(fd), tauD = JetForge.Core.Spool.TauDown(fd);
                double rIn2 = JetForgeMeshBuilder.InletRadius(fd);
                double area2 = Math.PI * rIn2 * rIn2;
                double full = fg.Interp(1.0, 0.0, 0.0).Thrust;

                double sp = 0.0;
                bool ini = false;
                FlightStepResult idleStep = default(FlightStepResult);
                for (int i = 0; i < 900; i++)
                    idleStep = FlightStep.Compute(fg, 0.0, 0.02, 0.0, 0.0, true, false, true, true,
                        ref sp, ref ini, tauU, tauD, area2, 0.0527);
                Log(string.Format("飞行链路（收光油门 18 s）：推力 {0:F2} kN（占满油门 {1:F1}%）模式 {2} 命令 {3:F3}",
                    idleStep.ThrustN / 1000.0, idleStep.ThrustN / Math.Max(1.0, full) * 100.0,
                    idleStep.Mode, idleStep.Command));

                FlightStepResult first = FlightStep.Compute(fg, 1.0, 0.02, 0.0, 0.0, true, false, true, true,
                    ref sp, ref ini, tauU, tauD, area2, 0.0527);
                Log(string.Format("  满油门后一帧：{0:F2} kN（满油门 {1:F2} kN）——转子有惯性，不是开关",
                    first.ThrustN / 1000.0, full / 1000.0));

                double spa = 0.0;
                bool inia = false;
                FlightStepResult wmStep = FlightStep.Compute(fg, 0.0, 0.02, 11000.0, 0.8, false, false, true, true,
                    ref spa, ref inia, tauU, tauD, area2, 0.0527);
                Log(string.Format("  11km M0.8 熄火：风车 {0:F2} kN　再点火起点转速 {1:F0}%　模式 {2}",
                    wmStep.Windmill.DragN / 1000.0, spa * 100.0, wmStep.Mode));
            }
            catch (Exception e) { Log("飞行链路异常: " + e); }
        }
    }
}
