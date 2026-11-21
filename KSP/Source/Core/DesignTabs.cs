namespace JetForge.Core
{
    /// <summary>
    /// 设计器页号。
    ///
    /// 以前这些数字是散落在 UI 与网格/剖视里的裸字面量（剖视图里"点部件跳页"、
    /// 自检里"页号不许超过剖视页"，各自写死一个 8）。加一页就得同时改四五处，
    /// 漏一处就是"点了进气道跳到风扇去"这种没人会立刻发现的错。
    /// 现在只有这一份，谁要用谁引用。
    ///
    /// ※ 进气道那一页已经删了：进气道是**独立零件**，装机时由零件自己的面板编辑
    /// （<c>UI/JetForgeIntakeWindow.cs</c>）。发动机设计器再去设一份"自带进气道口径"，
    /// 就是同一个物理量有两个真相，装了两件进气道时更是直接打架。
    /// 发动机这边只保留一个不可编辑的兜底口径（一件进气道都没装时用）。
    /// </summary>
    public static class DesignTabs
    {
        public const int General = 0;
        public const int Fan = 1;
        public const int Compressor = 2;
        public const int Turbine = 3;
        public const int Burner = 4;
        public const int Afterburner = 5;
        public const int Readout = 6;
        public const int Grid = 7;
        public const int Cutaway = 8;

        private static string[] _names;
        private static Lang _namesLang;

        /// <summary>页名（与设计器左侧标签顺序一致）。
        /// 按当前语言缓存一份：每帧画一遍标签，不能每帧重新拼两次字符串。</summary>
        public static string[] Names
        {
            get
            {
                if (_names == null || _namesLang != Loc.Current)
                {
                    _names = new[]
                    {
                        Loc.T("总体", "General"),
                        Loc.T("风扇", "Fan"),
                        Loc.T("压气机", "Compressor"),
                        Loc.T("涡轮", "Turbine"),
                        Loc.T("燃烧室", "Burner"),
                        Loc.T("加力", "Afterburner"),
                        Loc.T("读数", "Readout"),
                        Loc.T("性能网格", "Performance grid"),
                        Loc.T("剖视", "Cutaway")
                    };
                    _namesLang = Loc.Current;
                }
                return _names;
            }
        }

    }
}