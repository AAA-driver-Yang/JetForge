using System;

namespace JetForge.Core
{
    /// <summary>
    /// 语言识别（KSP 侧）。核心的 <see cref="Loc"/> 不碰 KSP 类型，
    /// 这里负责两件事：读游戏语言、读 `GameData/JetForge/Loc.cfg` 的手动覆盖。
    ///
    /// 为什么要手动覆盖：做截图、录视频、给英文论坛做演示时，
    /// 不方便为了看一眼英文界面去改整个游戏的设置。
    /// </summary>
    public static class LocKsp
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            Loc.FixPercent();
            Loc.Probe = Detect;
        }

        private static Lang? Detect()
        {
            // 手动覆盖先认：`en` / `English` / `zh` / `cn` 都交给 Loc.Classify，
            // 认不出就当没写（不要"不是 en 就算 zh"——那是猜）。
            Lang? forced = Loc.Classify(Forced());
            if (forced.HasValue) return forced;

            try
            {
                // KSP 1.8+：Localizer.CurrentLanguage 里装的可能是显示名（"English"、"简体中文"），
                // 也可能是语言代码（"en-us"、"zh-cn"）—— 实测这台机器给的是 "zh-cn"。
                // 所以判定交给 Loc.Classify（它会认代码）；认不出来就返回 null、保持原值，
                // 不要"不是中文就算英文"：读错枚举值比读不到更糟。
                Type t = Type.GetType("KSP.Localization.Localizer, Assembly-CSharp");
                if (t != null)
                {
                    var p = t.GetProperty("CurrentLanguage");
                    if (p != null)
                    {
                        string s = (p.GetValue(null, null) as string) ?? "";
                        Lang? got = Loc.Classify(s);
                        if (got.HasValue) return got;
                    }
                }
            }
            // 探测豁免：拿不到游戏语言就交回 null（保持中文），不是错误。
            catch (Exception) { }

            return null;
        }

        private static System.Reflection.MethodInfo _formatMethod;
        private static bool _formatLooked;

        /// <summary>
        /// 把一个**完整**的 `#autoLOC_…` 键当场翻成话，好让调用方在它后面拼别的东西。
        ///
        /// 为什么需要它：KSP 只在"整个字符串就是一个键"时才翻译。右键菜单那个加力按钮
        /// 的名字要随状态变（「加力：自动（油门 70% 起）」），于是有人写成
        /// `guiName = "#autoLOC_JF_f_afterburner" + 后缀` —— 拼出来是一个不存在的键，
        /// 游戏里原样显示成形如「#autoLOC_JF_f_afterburner 自动（油门 70% 起）」。
        /// 键门也抓不住：键本身确实存在，是拼接毁掉了它。
        ///
        /// 返回值里**绝不会**带 `#autoLOC`：翻不出来就返回空串
        /// （少一段字，胜过把一串键摆在玩家眼前）。
        /// </summary>
        public static string Gui(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key[0] != '#') return key;
            if (!_formatLooked)
            {
                _formatLooked = true;
                try
                {
                    Type t = Type.GetType("KSP.Localization.Localizer, Assembly-CSharp");
                    if (t != null) _formatMethod = t.GetMethod("Format", new[] { typeof(string) });
                }
                // 探测豁免：没有 Assembly-CSharp 就是"不在游戏里"（测试工程），不是错误。
                catch (Exception) { }
            }
            if (_formatMethod == null) return "";
            try
            {
                string s = (_formatMethod.Invoke(null, new object[] { key }) as string) ?? "";
                if (s.Length == 0) return "";
                if (s.IndexOf("#autoLOC", StringComparison.Ordinal) >= 0) return "";
                return s;
            }
            // 探测豁免：翻译抛异常就当没翻出来，返回空串。
            catch (Exception) { }
            return "";
        }

        private static string Forced()
        {
            try
            {
                var db = GameDatabase.Instance;
                if (db == null) return "";
                // 别按「目录/文件名」查：`UrlConfig.url` 里带着节点名，
                // KSP.log 写的是 `Config(JETFORGE_LOC) JetForge/Loc/JETFORGE_LOC`，
                // 所以 `GetConfigs("JetForge/Loc")` 一条也拿不到 —— 这条按 URL 查的路
                // 曾经让整只开关静默失效：Loc.cfg 里明明写着 en，界面还是中文。
                // 按节点名取才对，也不在乎玩家把节点放进哪个 cfg。
                ConfigNode[] nodes = db.GetConfigNodes("JETFORGE_LOC");
                if (nodes == null || nodes.Length == 0) return "";
                ConfigNode cfg = nodes[0];
                if (cfg == null) return "";
                string f = cfg.GetValue("forceLang");
                if (string.IsNullOrEmpty(f)) return "";
                return f.Trim();
            }
            // 探测豁免：没有 Loc.cfg 就是"不覆盖"。
            catch (Exception) { }
            return "";
        }

    }
}