using System;
using System.Collections;
using System.IO;
using JetForge.Core;
using UnityEngine;

namespace JetForge.Diagnostics
{
    /// <summary>
    /// 自动台架的第二棒：在航天中心场景里把测试机推上发射台。
    ///
    /// 为什么不能在主菜单里直接起飞：主菜单场景里 KSC 的设施变换还没建好，
    /// FlightDriver 找不到 'Facility/LaunchPad_spawn'，vessel 生成失败后
    /// 游戏会自己把场景切回 SPACECENTER（实测日志两次都是这条）。
    /// 所以自检只负责"备好存档 + 转到航天中心"，真正的起飞放在这里。
    ///
    /// 只有 GameData/JetForge/autotest.marker 存在、且自检已把接力棒交过来时才动手。
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class JetForgeAutoLaunch : MonoBehaviour
    {
        private static string TracePath
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/JetForge/selftest.log"); }
        }

        private static void Log(string s)
        {
            try { File.AppendAllText(TracePath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n"); }
            // 探测豁免：写启动痕迹失败不影响起飞，只是留不下证据
            catch { }
        }

        private IEnumerator Start()
        {
            if (!JetForgeSelfTest.AutoFlightPending) yield break;
            JetForgeSelfTest.AutoFlightPending = false;

            string marker = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/JetForge/autotest.marker");
            if (!File.Exists(marker)) yield break;

            // 等几帧：航天中心的设施（含发射台生成点）要等场景完全就绪才有。
            for (int i = 0; i < 20; i++) yield return null;

            string craft = Path.Combine(KSPUtil.ApplicationRootPath, "Ships/VAB/JetForgeTest.craft");
            Log("自动台架（航天中心）：准备起飞 " + craft);
            try
            {
                FlightDriver.StartWithNewLaunch(craft, "Squad/Flags/Default", "LaunchPad", null);
                Log("自动台架（航天中心）：StartWithNewLaunch 已调用");
            }
            catch (Exception e)
            {
                Log("自动台架（航天中心）异常: " + e);
            }
        }
    }
}