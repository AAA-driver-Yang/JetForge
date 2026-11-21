using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 发动机测试台。
    ///
    /// 它**不是**第二套求解器——这一点是设计约束，不是顺手：如果测试台自己算一遍，
    /// 那它证明的只是"另一套公式也能跑"，玩家看到的数字与飞行中看到的数字对不上时，
    /// 谁也不知道该信哪个。所以这里只做三件事：选工况 → 调 <see cref="OffDesign.Solve"/> → 排版。
    ///
    /// 放在 Core 里而不是 UI 里，是为了让回归能直接调它：界面上看得见的东西，
    /// 必须也有一个不靠鼠标就能验证的入口。
    /// </summary>
    public sealed class BenchRow
    {
        public double Alt, Mach, Throttle;
        public bool Ok;
        /// <summary>净推力（kN）。净 = 毛推力 − 冲压阻力，是"这台机器这一刻真的推了多大力"。</summary>
        public double NetThrust;
        /// <summary>毛推力（kN）。装机前的那份数，风车状态下它是负的。</summary>
        public double GrossThrust;
        public double Sfc;
        public double MdotAir, Opr, T4, Bpr;
        /// <summary>各轴转速读成一句话（"低压 99.9%｜高压 98.4%"），因为单轴机没有 N2、三轴机有三个，写死一列必然有一半是空的。</summary>
        public string Spool = "";
        public string Note = "";

        public string Line()
        {
            if (!Ok)
                return string.Format("{0,5:F0} m｜Ma{1:F2}｜油门 {2,3:F0}%　算不出来：{3}", Alt, Mach, Throttle * 100.0, Note);
            return string.Format("{0,5:F0} m｜Ma{1:F2}｜油门 {2,3:F0}%　净推力 {3,7:F2} kN｜耗油率 {4:F4} kg/(N·h)｜进气 {5,6:F1} kg/s｜压比 {6,5:F2}｜涡轮前 {7,4:F0} K｜{8}",
                Alt, Mach, Throttle * 100.0, NetThrust, Sfc, MdotAir, Opr, T4, Spool);
        }
    }

    public static class EngineBench
    {
        /// <summary>算一个工况点。失败也返回一行（带说明），不返回 null——表格里空一格比写"算不出来"更容易被当成 0。</summary>
        public static BenchRow Point(EngineDesignData d, double alt, double mach, double throttle, IntakeInstall install = null)
        {
            BenchRow row = new BenchRow { Alt = alt, Mach = mach, Throttle = throttle };
            if (d == null) { row.Note = "没有设计"; return row; }

            DesignConstants consts = OffDesign.DeriveDesignConstants(d);
            if (consts == null || !consts.Ok)
            {
                row.Note = "设计点本身不成立，先修设计再上台架";
                return row;
            }

            OffDesignResult r = OffDesign.Solve(d, alt, mach, throttle, 0.0, consts, 40, -1.0, 0.0, 1.0, 0, install);
            if (r == null) { row.Note = "求解没返回结果"; return row; }

            row.Ok = r.Ok;
            row.NetThrust = r.NetThrust / 1000.0;
            row.GrossThrust = r.Thrust / 1000.0;
            row.Sfc = r.Sfc;
            row.MdotAir = r.MdotAir;
            row.Opr = r.Opr;
            row.T4 = r.T4;
            row.Bpr = r.Bpr;
            // 轴转速读数是 Dictionary（单轴机只有一条、三轴机三条），所以这里拼成一句话，
            // 而不是硬取 N2——单轴机上那一格永远是 0，看上去像"转子不转"。
            if (r.Spool != null && r.Spool.Count > 0)
            {
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, double> kv in r.Spool)
                {
                    // 这张表里既有 "N1%" 也有 "N1 rpm"（物理转速），只要百分数那几条：
                    // 混着打会变成 "N1 7057%" 这种一看就是错的数字。
                    if (kv.Key == null || !kv.Key.EndsWith("%")) continue;
                    parts.Add(string.Format("{0} {1:F1}%", kv.Key.Substring(0, kv.Key.Length - 1), kv.Value));
                }
                row.Spool = string.Join("｜", parts.ToArray());
            }
            if (!r.Ok) row.Note = r.Errors != null && r.Errors.Count > 0 ? r.Errors[0] : "求解失败";
            return row;
        }

        /// <summary>
        /// 扫一张表。三个维度都取"人看得懂的档位"：高度 0/3/8/11/15 km、马赫 0/0.5/0.9/1.2/1.6、
        /// 油门 0/25/50/75/100%。25 个点够看出趋势，再多就只是把同一张图里的字挤小。
        /// </summary>
        public static List<BenchRow> Sweep(EngineDesignData d, bool byAltitude, IntakeInstall install = null)
        {
            double[] alts = { 0.0, 3000.0, 8000.0, 11000.0, 15000.0 };
            double[] machs = { 0.0, 0.5, 0.9, 1.2, 1.6 };
            double[] thrs = { 0.0, 0.25, 0.5, 0.75, 1.0 };

            List<BenchRow> rows = new List<BenchRow>();
            if (byAltitude)
            {
                for (int i = 0; i < alts.Length; i++)
                    rows.Add(Point(d, alts[i], 0.9, 1.0, install));
            }
            else
            {
                for (int i = 0; i < machs.Length; i++)
                    rows.Add(Point(d, 0.0, machs[i], 1.0, install));
            }
            return rows;
        }

        /// <summary>推力随油门怎么走。测试台最有用的一个问题：这条线该是单调的。</summary>
        public static List<BenchRow> ThrottleLine(EngineDesignData d, double alt, double mach, IntakeInstall install = null)
        {
            double[] thrs = { 0.0, 0.25, 0.5, 0.75, 1.0 };
            List<BenchRow> rows = new List<BenchRow>();
            for (int i = 0; i < thrs.Length; i++) rows.Add(Point(d, alt, mach, thrs[i], install));
            return rows;
        }
    }
}