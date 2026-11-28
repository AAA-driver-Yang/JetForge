using System;

namespace JetForge.Core
{
    /// <summary>
    /// 版本号的唯一来源。改版本只改这里一个数，然后跑 `tools/make_release.sh`。
    ///
    /// 为什么要集中：KSP 的 `.version` 文件（AVC 读的那个）、程序集版本、
    /// 发布包的目录名，三处必须一致，否则玩家装上的版本和日志里显示的不是同一个。
    /// 分三处手写迟早会分叉。
    /// </summary>
    public static class JetForgeVersion
    {
        /// <summary>语义化版本：主.次.修订。</summary>
        public const string Number = "1.0.1";

        /// <summary>给界面标题用的短串。</summary>
        public const string Display = "v" + Number;

        // 这里曾经还有 KspVersion / KspVersionMin / KspVersionMax 三个常量，外加一个 Authors 数组。
        // 全仓没有一处读它们——兼容区间真正的唯一来源是仓库根的 `.version` 文件
        // （`tools/check_release.py` 拿它比对 `INSTALL.md` 里写的那一行）。
        // 那两个数还不只没人读，是**已经烂了**：代码写 MIN 1.8.0，发布元数据写 1.12.0。
        // 一份没人读的兼容区间，比没有这份区间更坏：它总有一天会被当成依据（§235）。
    }
}