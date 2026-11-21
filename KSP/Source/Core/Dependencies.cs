using System;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace JetForge.Core
{
    /// <summary>
    /// 硬依赖检查：Waterfall 与 RocketSoundEnhancement。
    ///
    /// 第五阶段把这两样变成了"没有就不成立"：
    /// 羽流只有 Waterfall 一条路（自绘加法锥已删），声音交给 RSE 出干态。
    /// 少一样不会崩——cfg 里带 NEEDS，模块还是能挂上来——但那时候玩家看到的会是
    /// "发动机在转却不出火"或者"一点声都没有"，那种沉默的失败最难查。
    /// 所以启动时报一次，写清缺什么、少了会怎样。
    ///
    /// 用 AssemblyLoader 扫已加载程序集，而不是 GameDatabase 找文件：
    /// 文件在但没编译过（或者被别的 mod 的版本覆盖）时，找文件会给出假阳性。
    /// </summary>
    public static class Dependencies
    {
        public const string WaterfallDll = "Waterfall";
        public const string RseDll = "RocketSoundEnhancement";

        private static bool _logged;

        public static bool Installed(string needle)
        {
            try
            {
                var list = AssemblyLoader.loadedAssemblies;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (a == null || a.assembly == null) continue;
                    string n = a.name ?? "";
                    if (n.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            // 探测豁免：找不到依赖就是"没装"，return false 就是要的结果。
            catch (Exception) { }
            return false;
        }

        public static bool WaterfallInstalled() { return Installed(WaterfallDll); }
        public static bool RseInstalled() { return Installed(RseDll); }
        public static bool Satisfied { get { return WaterfallInstalled() && RseInstalled(); } }

        /// <summary>一行可读状态，自检与 PAW 共用。</summary>
        public static string Report()
        {
            bool w = WaterfallInstalled(), r = RseInstalled();
            var sb = new StringBuilder();
            sb.AppendFormat("Waterfall {0}｜RSE {1}｜硬依赖满足 {2}",
                w ? "有" : "缺（没有尾焰）",
                r ? "有" : "缺（干态声音只剩我们那几层）",
                (w && r) ? "对" : "不对");
            return sb.ToString();
        }

        /// <summary>只报一次：每台发动机每次载入都刷一遍会把日志淹掉。</summary>
        public static void LogOnce()
        {
            if (_logged) return;
            _logged = true;
            if (Satisfied) return;
            Debug.LogError("[JetForge] 缺少必需依赖：" + Report()
                + "。JetForge 从第五阶段起硬依赖 Waterfall（羽流）与 RocketSoundEnhancement（干态声音），"
                + "请安装这两个 mod，否则尾焰/声音不完整。");
        }
    }
}