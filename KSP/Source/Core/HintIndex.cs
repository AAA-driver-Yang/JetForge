using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 按界面标签文字找悬停提示。
    ///
    /// **为什么单独一个文件**：这段逻辑以前藏在 Unity 侧的窗口类里，只有进游戏点开设计器才算跑过，
    /// 于是英文界面"提示静默消失"这个缺陷（提示表的键是中文，英文界面递过来的是英文）
    /// 只能靠人眼看。逻辑搬到 Core 之后，回归工程可以直接钉住它。
    ///
    /// 匹配顺序是有讲究的：
    /// 1. 精确（两边都 trim）——"长度"（燃烧室）与"整机长度"都含"长度"，先做包含匹配就会串；
    /// 2. 包含——子项标签带缩进，去掉缩进后才对得上；
    /// 3. 把英文换回中文规范形，再来一遍 1 和 2（<see cref="Loc.Canonical"/>）；
    /// 4. 换不回来就算了，返回 null，**不许硬凑一条错的提示**——错的提示比没有提示更坏。
    /// </summary>
    public static class HintIndex
    {
        public static string Find(IEnumerable<KeyValuePair<string, string>> table, string label)
        {
            if (table == null || string.IsNullOrEmpty(label)) return null;
            string s = label.Trim();
            if (s.Length == 0) return null;

            string hit = Lookup(table, s);
            if (hit != null) return hit;

            string canon = Loc.Canonical(s).Trim();
            if (canon.Length > 0 && canon != s) return Lookup(table, canon);
            return null;
        }

        private static string Lookup(IEnumerable<KeyValuePair<string, string>> table, string key)
        {
            foreach (KeyValuePair<string, string> kv in table)
                if (kv.Key != null && kv.Key.Trim() == key) return kv.Value;
            foreach (KeyValuePair<string, string> kv in table)
            {
                if (kv.Key == null) continue;
                string k = kv.Key.Trim();
                if (k.Length > 0 && key.Contains(k)) return kv.Value;
            }
            return null;
        }
    }
}