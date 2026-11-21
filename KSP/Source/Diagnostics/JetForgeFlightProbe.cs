using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using JetForge.KSP;

namespace JetForge.Diagnostics
{
    /// <summary>
    /// 飞行场景探针：每两秒把「模型算的」和「场上正发生的」并排写进
    /// <c>GameData/JetForge/flight.log</c>。
    ///
    /// 为什么需要它：主菜单自检只能证明求解器和几何算得对，证明不了
    /// UpdateThrottle 在真实飞行里被调用的时机、关车后的行为、尾焰在动态推力下的表现。
    /// 这些只有飞起来才知道，而飞行中截图是看不出数字的，所以让 mod 自己记。
    ///
    /// 这个探针只读，不参与任何计算，删掉它不影响发动机。
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class JetForgeFlightProbe : MonoBehaviour
    {
        private const float Interval = 2.0f;
        private float _next;
        private int _lines;
        private bool _activated;
        private readonly List<ModuleJetForgeEngine> _found = new List<ModuleJetForgeEngine>();
        // 已经写过"装法"的载具（按 rootPart.flightID），每个只写一次，免得日志刷屏。
        private readonly HashSet<uint> _mountSeen = new HashSet<uint>();
        // 每个载具只报一次方向问题，避免每 2 秒刷一行。
        private readonly HashSet<uint> _dirWarned = new HashSet<uint>();

        /// <summary>
        /// 自动台架模式：存在这个标记文件时，探针自己按时间轴推油门、点车、关车。
        ///
        /// 为什么要这东西：飞行链路（UpdateThrottle 的真实调用时机、力作用在哪个点、
        /// 关车后的风车阻力）只有飞起来才能验，而人不在键盘前的时候它永远验不了。
        /// 标记文件删掉就恢复成纯读数探针——平时不会自己动你的飞机。
        /// </summary>
        private static bool AutoTest
        {
            get
            {
                try
                {
                    return File.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "GameData/JetForge/autotest.marker"));
                }
                catch { return false; }
            }
        }

        private static string TracePath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/JetForge/flight.log"); }
        }

        void Start()
        {
            try
            {
                File.WriteAllText(TracePath,
                    "JetForge 飞行探针（每 2 秒一行，写完即关）\r\n" +
                    "自动台架=" + (AutoTest ? "开" : "关") + "\r\n" +
                    "时间  高度m  马赫  油门  模型推力kN(本帧)  场上推力kN  原版推力kN  油耗kg/s  原版流量kg/s  进气kg/s  Isp  N1%  N2%  EGT  点火  熄火  尾焰  加力  状态  施力点数  施力点高度m  油门命令  施力方向(零件坐标)  速度m/s  速度dot  羽流缩放  音效\r\n");
            }
            // 探测豁免：表头写不进去就少一行标题，数据行还在
            catch { }
            _next = Time.time + Interval;
        }

        /// <summary>自动台架的时间轴。返回这一拍该给的油门。</summary>
        private float ScriptedThrottle(float t, ModuleJetForgeEngine e)
        {
            if (t >= 3.0f && !_activated)
            {
                _activated = true;
                // 探测豁免：自动台架里激活失败 = 这台上没有可测的发动机
                try { e.Activate(); e.Events["Activate"].active = false; } catch { }
            }
            if (t < 8.0f) return 1.0f;
            if (t < 22.0f) return 1.0f;
            if (t < 30.0f) return 0.5f;
            if (t < 45.0f) return 0.0f;
            // 探测豁免：自动台架关车失败 = 本来就已经停着
            try { if (e.EngineIgnited) e.Shutdown(); } catch { }
            return 0.0f;
        }

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + (AutoTest ? 1.0f : Interval);

            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null && AutoTest)
                {
                    // 从脚本里直接起飞时 KSP 不会把新船设成活动船（实测 ActiveVessel 一直是 null，
                    // 飞行界面跟着抛一堆 NRE，物理也就一直停在 "waiting to start physics"）。
                    // SetActiveVessel 会让游戏直接崩、只写 activeVessel 字段同样会崩（都试过），
                    // 所以这里只读不抢：拿已加载的船读数据，能读到多少写多少。
                    try
                    {
                        if (FlightGlobals.VesselsLoaded != null)
                            for (int k = 0; k < FlightGlobals.VesselsLoaded.Count; k++)
                                if (FlightGlobals.VesselsLoaded[k] != null) { v = FlightGlobals.VesselsLoaded[k]; break; }
                    }
                    // 探测豁免：找不到船就是本帧没有可写的对象
                    catch { }
                }
                if (v == null)
                {
                    // 自动台架时把"为什么没数据"写出来——否则 0 字节日志什么也说明不了。
                    if (AutoTest)
                        File.AppendAllText(TracePath, DateTime.Now.ToString("HH:mm:ss") +
                            "  无载具：ActiveVessel=null，Vessels=" +
                            (FlightGlobals.Vessels == null ? -1 : FlightGlobals.Vessels.Count) +
                            "，VesselsLoaded=" + (FlightGlobals.VesselsLoaded == null ? -1 : FlightGlobals.VesselsLoaded.Count) + "\r\n");
                    return;
                }
                _found.Clear();
                foreach (Part p in v.parts)
                    foreach (PartModule m in p.Modules)
                    {
                        ModuleJetForgeEngine e = m as ModuleJetForgeEngine;
                        if (e != null) _found.Add(e);
                    }
                if (_found.Count == 0)
                {
                    if (AutoTest)
                        File.AppendAllText(TracePath, DateTime.Now.ToString("HH:mm:ss") +
                            "  载具在（" + v.vesselName + "，" + v.parts.Count + " 零件）但没找到 ModuleJetForgeEngine\r\n");
                    return;
                }

                StringBuilder sb = new StringBuilder();
                // 每个载具只写一次"装法"：零件自己的推力轴在世界里朝哪，跟载具的三根轴各成什么角。
                // 这是分辨"玩家把发动机装反了"与"我们施力方向写反了"的关键——前者零件轴与载具轴对不上，
                // 后者零件轴对得上而力朝反边打。
                try
                {
                    if (_mountSeen.Add(v.rootPart.flightID))
                    {
                        Transform m0 = null;
                        for (int k = 0; k < _found.Count; k++)
                        {
                            ModuleJetForgeEngine ek = _found[k];
                            if (ek.thrustTransforms != null && ek.thrustTransforms.Count > 0 && ek.thrustTransforms[0] != null)
                            { m0 = ek.thrustTransforms[0]; break; }
                        }
                        if (m0 != null)
                        {
                            Vector3 worldThrust = -m0.forward;
                            File.AppendAllText(TracePath, string.Format(
                                "装法：载具={0} 零件数={1} 发动机={2} 台｜推力轴·up={3:F2} 推力轴·fwd={4:F2} 推力轴·right={5:F2}｜零件up·载具up={6:F2}\r\n",
                                v.vesselName, v.parts.Count, _found.Count,
                                Vector3.Dot(worldThrust, v.transform.up),
                                Vector3.Dot(worldThrust, v.transform.forward),
                                Vector3.Dot(worldThrust, v.transform.right),
                                Vector3.Dot(_found[0].part.transform.up, v.transform.up)));
                        }
                    }
                }
                // 探测豁免：取不到件信息就跳过这一段，探针不负责修载具
                catch { }
                if (AutoTest)
                {
                    float thrCmd = ScriptedThrottle(Time.time, _found[0]);
                    // 探测豁免：自动油门设不进去就按玩家手上的油门走
                    try { FlightInputHandler.state.mainThrottle = thrCmd; } catch { }
                }
                for (int i = 0; i < _found.Count; i++)
                {
                    ModuleJetForgeEngine e = _found[i];
                    // 原版推力：按 RequiredPropellantMass 的公式自己重算一遍（流量 × Isp × g），
                    // 用来核对我们喂进去的 maxFuelFlow / Isp 是否真的被执行了。
                    double stockFlowTps = e.requestedMassFlow;
                    double stockKN = stockFlowTps * e.realIsp * JetForge.Core.Gas.G0;
                    // 施力方向：原版用 -t.forward 施力，所以 -forward 就是"推力指向"。
                    // 记它在零件坐标里的分量：本机应该是 0/1/0（喷口朝 -Y，推力朝 +Y）。
                    // 速度dot 是速度方向与推力方向的余弦：>0 表示加速方向与推力一致，
                    // <0 表示整台机在往推力的反方向走——那才是玩家说的"反"。
                    string dirTxt = "-";
                    string dotTxt = "-";
                    string headTxt = "-";
                    double spdTxt = 0.0;
                    Transform ht = (e.thrustTransforms != null && e.thrustTransforms.Count > 0) ? e.thrustTransforms[0] : null;
                    if (ht != null)
                    {
                        Vector3 loc = e.part.transform.InverseTransformDirection(-ht.forward);
                        dirTxt = string.Format("{0:F2}/{1:F2}/{2:F2}", loc.x, loc.y, loc.z);
                        Vector3d sv = v.GetSrfVelocity();
                        spdTxt = sv.magnitude;
                        double dotV = 0.0, dotHead = 0.0;
                        bool hasDot = spdTxt > 0.05;
                        if (hasDot)
                        {
                            dotTxt = string.Format("{0:F2}", Vector3d.Dot(sv / spdTxt, -(Vector3d)ht.forward));
                            // 机头dot：速度与载具机头方向的余弦。它跟"速度dot"一起看才有意义——
                            // 两个都 +1 是正常；速度dot +1 而机头dot -1 是倒着飞；都反过来才是推力反了。
                            headTxt = string.Format("{0:F2}", Vector3d.Dot(sv / spdTxt, (Vector3d)v.transform.forward));
                            dotV = Vector3d.Dot(sv / spdTxt, -(Vector3d)ht.forward);
                            dotHead = Vector3d.Dot(sv / spdTxt, (Vector3d)v.transform.forward);
                        }
                        // 自己判一次方向对不对，别等玩家看出来再回头查日志。
                        // 判据两条：① 施力方向在零件坐标必须是 (0,1,0)；
                        //          ② 已经在动了，就别出现"机头朝前、速度朝后"的组合。
                        bool dirBad = loc.y < 0.95f || Mathf.Abs(loc.x) > 0.1f || Mathf.Abs(loc.z) > 0.1f;
                        bool movingBack = hasDot && dotHead > 0.2 && dotV < -0.2;
                        if ((dirBad || movingBack) && _dirWarned.Add(v.rootPart.flightID))
                        {
                            File.AppendAllText(TracePath, string.Format(
                                "[方向告警] {0}：施力方向(零件坐标)={1}｜父节点={2}｜零件 up·载具 up={3:F2}｜机头dot={4}｜速度dot={5}\r\n",
                                dirBad ? "施力节点朝向不对，力会被劈歪" : "机头朝前但速度朝后，推力方向反了",
                                dirTxt, ht.parent == null ? "(无)" : ht.parent.name,
                                Vector3.Dot(e.part.transform.up, v.transform.up), headTxt, dotTxt));
                        }
                    }
                    sb.AppendFormat("{0}  h={1:F0}  Ma={2:F2}  thr={3:F3}  F模型={4:F2}  F场上={5:F2}  F原版={6:F2}  " +
                                    "油耗={7:F4}  原版流量={8:F4}  进气={9:F1}  Isp={10:F0}  N1={11:F1}  N2={12:F1}  EGT={13:F0}  " +
                                    "点火={14}  熄火={15}  尾焰={16}  加力={17}  状态={18}  风车={19}  施力点={20}  施力高度={21:F2}  油门命令={22:F3}  " +
                                    "施力方向={23}  速度={24:F1}  速度dot={25}  机头dot={26}  羽流缩放={27}  音效={28}  喘振={29}  " +
                                    "推力比={30:F3}  最大推力={31:F1}",
                        DateTime.Now.ToString("HH:mm:ss"), v.altitude, v.mach, e.currentThrottle,
                        e.modelThrustN / 1000.0, e.finalThrust, stockKN, e.readFuelFlow, stockFlowTps * 1000.0,
                        e.readMassFlow, e.realIsp, e.readN1, e.readN2, e.readEgt, e.EngineIgnited, e.flameout,
                        e.PlumeVisible ? "有" : "无", e.afterburnerLit ? "开" : "关", e.readState,
                        string.IsNullOrEmpty(e.readWindmill) ? "-" : e.readWindmill,
                        e.thrustTransforms == null ? -1 : e.thrustTransforms.Count,
                        (e.thrustTransforms != null && e.thrustTransforms.Count > 0 &&
                         e.thrustTransforms[0] != null) ? e.thrustTransforms[0].localPosition.y : -999.0,
                        v.ctrlState != null ? v.ctrlState.mainThrottle : 0f,
                        dirTxt, spdTxt, dotTxt, headTxt,
                        string.IsNullOrEmpty(e.readPlumeScale) ? "-" : e.readPlumeScale,
                        // 音效这一列带着 RSE 自己的状态：实飞时它能回答"RSE 到底放声没有"，
                        // 也是"我们有没有让位"的唯一现场证据（prefab 上 Initialized 永远为假）。
                        string.IsNullOrEmpty(e.readSound) ? "-" : e.readSound,
                        // 喘振这一列：实飞时猛推一把就该看见"喘振中"，松油门就该回到平稳。
                        string.IsNullOrEmpty(e.readSurge) ? "-" : e.readSurge,
                        // 推力比 = finalThrust / maxThrust。RSE 的音量/音高横轴读的就是这个比值
                        // （反汇编 RSE_Engines.LateUpdate：GetCurrentThrust / maxThrust），
                        // 所以"声音不跟油门走"时，先看这一列有没有动——它不动就不是声音的事。
                        e.maxThrust > 1e-3f ? e.finalThrust / e.maxThrust : 0f,
                        e.maxThrust);
                    if (i < _found.Count - 1) sb.Append("\r\n");
                }
                File.AppendAllText(TracePath, sb.ToString() + "\r\n");
                _lines++;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(TracePath, "探针异常：" + ex.GetType().Name + ": " + ex.Message + "\r\n"); }
                // 探测豁免：异常已经写进痕迹文件了，再写一条说明失败就算了
                catch { }
            }
        }
    }
}