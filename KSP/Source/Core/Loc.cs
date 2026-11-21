using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>玩家可见文字的语言。</summary>
    public enum Lang
    {
        Chinese = 0,
        English = 1,
    }

    /// <summary>
    /// 中英双语：给玩家看的每一句都要有英文，日志可以只有中文。
    ///
    /// 为什么用 `T("中文", "English")` 这种"原文成对"的写法，而不是 `T("key")` 查表：
    /// 这个 mod 里四千多条玩家可见文字，做成键值表意味着每个调用点要起一个键名，
    /// 改文案时还得两头对——那正是最容易漏一处、然后游戏里冒出半句中文的地方。
    /// 原文成对之后，调用点自己就是完整的一句，漏翻一眼就能看出来（英文参数空着）。
    ///
    /// **这一个文件不碰 KSP 类型**（测试工程不引用 Assembly-CSharp，要能一起编译）。
    /// 从游戏设置里识别语言的动作放在 <see cref="LocKsp"/>，它给这里挂一个探测器。
    /// </summary>
    public static class Loc
    {
        private static Lang _current = Lang.Chinese;
        private static bool _ready;
        private static int _fallbacks;
        private static Func<Lang?> _probe;
        private static string _probeState = "没装";
        private static readonly Dictionary<string, string> _enToZh =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _zhToEn =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 语言探测器：返回 null 表示"不知道"，那就保持当前值。
        /// 由 KSP 侧在启动时挂上（见 LocKsp.Install）。
        /// </summary>
        public static Func<Lang?> Probe
        {
            set { _probe = value; _ready = false; }
        }

        public static Lang Current
        {
            get { EnsureReady(); return _current; }
            set { _current = value; _ready = true; }
        }

        public static bool IsEnglish { get { return Current == Lang.English; } }

        /// <summary>英文参数为空的次数。自检里念出来：漏翻是"看不见的错"，得有个数。</summary>
        public static int Fallbacks { get { return _fallbacks; } }

        private static bool _percentFixed;

        /// <summary>
        /// 把百分号排版掰成中英文界面都认的样子。
        ///
        /// 这台机器上 `{0:P0}` 印出来是「70 %」——数字和百分号之间多一个空格。
        /// 中文界面里那是错字，玩家截图指的就是它。改的只有
        /// <c>PercentPositivePattern</c> / <c>PercentNegativePattern</c> 两个值：
        /// 小数点、千分位、货币符号一概不碰——**只治这一个毛病，不顺手改玩家的数字习惯**。
        /// 就地改当前区域设置，所以只做一次。
        ///
        /// 这两个数字的含义别凭直觉填（§273 第一次就填错了）：
        /// 正数 0 = `n %`、1 = `n%`、2 = `%n`、3 = `% n`；负数 0 = `-n %`、1 = `-n%`。
        /// 要的是"数字紧挨着 %"，也就是两个都填 1。改完必须念一句样例验过——
        /// 填成 0 也"设置成功"，屏幕上照旧空一格。
        /// </summary>
        public static void FixPercent()
        {
            if (_percentFixed) return;
            _percentFixed = true;
            try
            {
                var ci = System.Globalization.CultureInfo.CurrentCulture;
                var nf = (System.Globalization.NumberFormatInfo)ci.NumberFormat.Clone();
                if (nf.PercentPositivePattern != 1 || nf.PercentNegativePattern != 1)
                {
                    nf.PercentPositivePattern = 1;   // 70%
                    nf.PercentNegativePattern = 1;   // -70%
                    ci.NumberFormat = nf;
                }
            }
            // 探测豁免：拿不到区域设置就算了，百分号多一格空格不至于让界面不能用。
            catch (Exception) { }
        }

        public static string T(string zh, string en)
        {
            // 记一份「英文 → 中文」的对照。用途只有一个：界面按标签文字查东西（例如悬停提示表）
            // 时，英文界面递过来的是英文，而表是按中文起的键。没有这份对照，英文界面的提示
            // 会静默消失——不报错、不崩，只是鼠标悬上去什么都没有。
            if (!string.IsNullOrEmpty(en) && !string.IsNullOrEmpty(zh) && en != zh)
            {
                _enToZh[en] = zh;
                _zhToEn[zh] = en;
                // 同时登记去掉缩进的写法：字段表的子项标签是靠缩进排版对齐的（"  级数"），
                // 而查表的人会先 trim，两边对不上就等于没有这对词。
                string et = en.Trim(), zt = zh.Trim();
                if (et.Length > 0 && zt.Length > 0 && et != zt)
                {
                    if (!_enToZh.ContainsKey(et)) _enToZh[et] = zt;
                    if (!_zhToEn.ContainsKey(zt)) _zhToEn[zt] = et;
                }
            }
            if (!IsEnglish) return zh;
            if (string.IsNullOrEmpty(en)) { _fallbacks++; return zh; }
            return en;
        }

        /// <summary>
        /// 把界面上的文字换回中文规范形（"Thrust" → "推力"）。认不出来就原样返回。
        /// 只认 <see cref="T"/> 见过的对子，所以前提是那句文案确实走了 T。
        /// </summary>
        public static string Canonical(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string zh;
            if (_enToZh.TryGetValue(text, out zh)) return zh;
            string s = text.Trim();
            if (s.Length != text.Length && _enToZh.TryGetValue(s, out zh)) return zh;
            return text;
        }

        /// <summary>
        /// 百分比排版的自述：模式值与一句样例。自检念出来——「修过了」和「修好了」
        /// 是两件事，Mono 上改区域设置不一定当场生效（§273 第一次就白改了一轮）。
        /// </summary>
        public static string PercentProbe()
        {
            string pattern = "?";
            try { pattern = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.PercentPositivePattern.ToString(); }
            // 探测豁免：读不到区域设置就报问号，不抛。
            catch (Exception) { }
            return string.Format(T("模式 {0}｜样例 {1}", "pattern {0} | sample {1}"),
                pattern, string.Format("{0:P0}", 0.7));
        }

        /// <summary>
        /// 百分比样例里数字和 % 之间有没有空格。
        /// 单独给一个判据：自检念出来的那句话自己带着空格和竖线，
        /// 拿整句去 IndexOf(' ') 会永远为真——**尺子不能把刻度也算进去**。
        /// </summary>
        public static bool PercentTight
        {
            get { return string.Format("{0:P0}", 0.7).IndexOf(' ') < 0; }
        }

        /// <summary>已登记的英中对照条数（自检里念出来，0 说明根本没走过 T）。</summary>
        public static int PairCount { get { return _enToZh.Count; } }

        /// <summary>中文 → 英文（没登记过就返回 null）。自检里用它按中文键找英文标签。</summary>
        public static string EnglishOf(string zh)
        {
            if (string.IsNullOrEmpty(zh)) return null;
            string en;
            if (_zhToEn.TryGetValue(zh, out en)) return en;
            string s = zh.Trim();
            if (s.Length != zh.Length && _zhToEn.TryGetValue(s, out en)) return en;
            return null;
        }

        public static string Name(Lang l) { return l == Lang.English ? "English" : "中文"; }

        /// <summary>
        /// 把"语言名"归一成 <see cref="Lang"/>。认不出来返回 null（不猜）。
        ///
        /// 为什么单独抽出来：KSP 那个字段里可能是**显示名**（`English`、`简体中文`），
        /// 也可能是**代码**（`en-us`、`zh-cn`）。第一版只判"有没有汉字"，
        /// 于是 `zh-cn` 因为不含汉字被判成英文 —— 这台机器的实际语言就是 zh-cn，
        /// 而自检一路报"语言 English"，零件名却是中文。读错枚举值比读不到更糟：
        /// 它会让人相信自己验过了一半。
        /// </summary>
        public static Lang? Classify(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Length == 0) return null;
            for (int i = 0; i < s.Length; i++)
                if (s[i] >= '一' && s[i] <= '鿿') return Lang.Chinese;
            string low = s.ToLowerInvariant();
            if (low.IndexOf("chinese", StringComparison.Ordinal) >= 0) return Lang.Chinese;
            if (low.IndexOf("english", StringComparison.Ordinal) >= 0) return Lang.English;
            // 语言代码：zh / zh-cn / zh_cn / cn / en / en-us ……
            string[] parts = low.Split(new char[] { '-', '_', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "zh" || parts[i] == "cn" || parts[i] == "cht" || parts[i] == "chs")
                    return Lang.Chinese;
            }
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "en") return Lang.English;
            }
            return null;
        }

        private static void EnsureReady()
        {
            if (_ready) return;
            _ready = true;
            // 百分号排版必须在**第一句**话之前修好：晚一步，之前印出去的那些
            // 就带着「70 %」进了日志与屏幕，而且看起来不像错的。
            FixPercent();
            if (_probe == null)
            {
                // 探针没装：`Current` 是构造缺省值。这不是"探测结果是中文"，
                // 是"还没人问过语言"。日志必须把这两件事分开说，否则一行看着正常的
                // 「语言 中文」会替一整条没验过的链路作证（§232 就是这么被骗了七十多轮）。
                _probeState = "没装（读的是缺省值）";
                return;
            }
            Lang? l = null;
            try { l = _probe(); }
            // 探测豁免：识别语言失败不是错误，保持中文是正确回落。
            catch (Exception) { _probeState = "抛异常"; }
            if (l.HasValue)
            {
                _current = l.Value;
                _probeState = "认出 " + Name(l.Value);
            }
            else if (_probeState != "抛异常")
            {
                _probeState = "装了但认不出语言";
            }
        }

        /// <summary>语言探测的真实状态（没装 / 认不出 / 抛异常 / 认出哪个）。自检里念出来。</summary>
        public static string ProbeState { get { EnsureReady(); return _probeState; } }

        /// <summary>一行状态，自检与日志用。</summary>
        public static string Report()
        {
            return string.Format("语言 {0}｜漏翻 {1} 句｜探测 {2}", Name(Current), Fallbacks, ProbeState);
        }
    }
}