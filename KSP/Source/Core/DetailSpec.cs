using System;

namespace JetForge.Core
{
    /// <summary>
    /// 细节级别：程序化几何要加多少东西。
    ///
    /// 为什么要有这一项，而不是"越多越好"：
    /// 每台发动机都是唯一网格，一台机器一万多三角，二十台就是几十万——
    /// 玩家造车队的机器上，KSP 的渲染线程会先疼。低档要能跑得动，
    /// 高档要给想截图的人看清楚"这台是几级压气机"。
    /// </summary>
    public enum DetailLevel
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    public static class DetailSpec
    {
        /// <summary>
        /// 三档明细的名字。做成按语言缓存的属性，不是静态数组——静态初始化发生在语言探测之前，
        /// 那样英文界面会永远显示中文（`DesignTabs.Names` 栽过同一个坑，见 §184）。
        /// </summary>
        private static string[] _names;
        private static Lang _namesLang;

        public static string[] Names
        {
            get
            {
                if (_names == null || _namesLang != Loc.Current)
                {
                    _names = new[] { Loc.T("低", "Low"), Loc.T("中", "Medium"), Loc.T("高", "High") };
                    _namesLang = Loc.Current;
                }
                return _names;
            }
        }

        public static string Name(DetailLevel lv)
        {
            int i = (int)lv;
            string[] names = Names;
            return i >= 0 && i < names.Length ? names[i] : names[1];
        }

        public static DetailLevel Parse(int i)
        {
            if (i <= 0) return DetailLevel.Low;
            if (i >= 2) return DetailLevel.High;
            return DetailLevel.Medium;
        }

        /// <summary>每排压气机转子叶片数（0 = 不生成）。</summary>
        public static int RotorBlades(DetailLevel lv)
        {
            switch (lv)
            {
                case DetailLevel.Low: return 0;
                case DetailLevel.Medium: return 14;
                default: return 22;
            }
        }

        /// <summary>每排静子叶片数（只有高档才有：静子夹在转子之间，三角数翻倍得最快）。</summary>
        public static int StatorVanes(DetailLevel lv)
        {
            return lv == DetailLevel.High ? 18 : 0;
        }

        /// <summary>每排涡轮叶片数。</summary>
        public static int TurbineBlades(DetailLevel lv)
        {
            switch (lv)
            {
                case DetailLevel.Low: return 0;
                case DetailLevel.Medium: return 16;
                default: return 24;
            }
        }

        /// <summary>机匣法兰（每级一道箍）：只有高档。</summary>
        public static bool Flanges(DetailLevel lv)
        {
            return lv == DetailLevel.High;
        }

        /// <summary>附件机匣与管路。</summary>
        public static bool Accessories(DetailLevel lv)
        {
            return lv != DetailLevel.Low;
        }

        /// <summary>点火器凸台。</summary>
        public static bool Igniters(DetailLevel lv)
        {
            return lv != DetailLevel.Low;
        }

        public static string Describe(DetailLevel lv)
        {
            switch (lv)
            {
                case DetailLevel.Low:
                    return Loc.T("低：只有外形与主要部件，最省三角", "Low: shell and main parts only, fewest triangles");
                case DetailLevel.Medium:
                    return Loc.T("中：加压气机/涡轮叶片排、附件机匣、点火器", "Medium: adds compressor and turbine blade rows, accessory gearbox, igniters");
                default:
                    return Loc.T("高：再加静子叶排与机匣法兰，叶片数也加密", "High: adds stator rows and casing flanges, and denser blade counts");
            }
        }
    }
}