using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>一行对比：同一个读数在 A、B 两台设计上各是多少，以及"哪个更好"。</summary>
    public sealed class CompareRow
    {
        /// <summary>已本地化的读数名（进界面前不要再包 Loc.T）。</summary>
        public string Label = "";
        public double A, B;
        public string Unit = "";
        public string Format = "F2";
        /// <summary>+1 = 越大越好，−1 = 越小越好，0 = 没有好坏（吞吐量、涵道比这类取向问题）。</summary>
        public int Better;

        /// <summary>A 相对 B 的差（A − B）。</summary>
        public double Delta { get { return A - B; } }

        /// <summary>
        /// 两个数是否真的不同。容差按相对 1e-6 给——设计点求解是确定性的，同一台设计解两遍
        /// 应当逐位相同；这里留一点余地是给"存档里少写一个字段、读回来取缺省"的场合。
        /// </summary>
        public bool Differs
        {
            get
            {
                double scale = Math.Max(Math.Abs(A), Math.Abs(B));
                return Math.Abs(A - B) > Math.Max(1e-9, scale * 1e-6);
            }
        }

        /// <summary>
        /// 谁更好：只看方向，不看倍数。返回 +1 表示 A 更好、−1 表示 B 更好、0 表示这一行没有好坏
        /// 或者两边一样。**不拿它算总分**——推力大 10% 与造价低 10% 不是同一种好，
        /// 拍一个总分出来就是在替玩家定权重。
        /// </summary>
        public int Winner()
        {
            if (Better == 0 || !Differs) return 0;
            double d = Delta * Better;
            return d > 0 ? 1 : (d < 0 ? -1 : 0);
        }
    }

    /// <summary>两台设计的对比结果。</summary>
    public sealed class CompareResult
    {
        public List<CompareRow> Rows = new List<CompareRow>();
        /// <summary>两台都解出来了才谈得上对比；任一台解不出来就把原因带在下面。</summary>
        public bool BothOk = true;
        public string ErrorA = "";
        public string ErrorB = "";

        public int BetterA
        {
            get { int n = 0; foreach (CompareRow r in Rows) if (r.Winner() > 0) n += 1; return n; }
        }

        public int BetterB
        {
            get { int n = 0; foreach (CompareRow r in Rows) if (r.Winner() < 0) n += 1; return n; }
        }
    }

    /// <summary>
    /// 两台设计并排比读数。
    ///
    /// **为什么不是"算一个总评分"**：玩家拿两台机器比，想知道的是"我多花的那些钱换来了什么"。
    /// 把推力、耗油、重量、造价压成一个数，等于替玩家定了权重，而且那个权重一定不合用。
    /// 所以这里只给每个读数各自的差和方向，判断留给看的人。
    ///
    /// 读哪几个数不是随便挑的：推力（要干什么用）、SFC（费多少油）、总压比与涡轮前温
    /// （这台机器有多"使劲"）、长度与干重（装不装得下、拖不拖得动）、造价（负担得起吗），
    /// 再加一条高压压气机喘振裕度（离边界还有多远——推力更大但余量只剩 5% 的两台机器不是同一类货）。
    /// </summary>
    public static class DesignCompare
    {
        public static CompareResult Run(EngineDesignData a, EngineDesignData b)
        {
            CompareResult res = new CompareResult();
            if (a == null || b == null)
            {
                res.BothOk = false;
                res.ErrorA = a == null ? Loc.T("没有这个设计", "no such design") : "";
                res.ErrorB = b == null ? Loc.T("没有这个设计", "no such design") : "";
                return res;
            }

            CycleResult ra = Cycle.SolveDesignPoint(a);
            CycleResult rb = Cycle.SolveDesignPoint(b);
            MassBreakdown ma = MassCost.Estimate(a);
            MassBreakdown mb = MassCost.Estimate(b);
            double la = EngineLength.Estimate(a);
            double lb = EngineLength.Estimate(b);

            if (ra == null || !ra.Ok)
            {
                res.BothOk = false;
                res.ErrorA = ra == null ? "null" : First(ra);
            }
            if (rb == null || !rb.Ok)
            {
                res.BothOk = false;
                res.ErrorB = rb == null ? "null" : First(rb);
            }
            if (!res.BothOk) return res;

            Add(res, Loc.T("推力", "Thrust"), ra.Thrust / 1000.0, rb.Thrust / 1000.0, "kN", "F2", +1);
            Add(res, Loc.T("SFC", "SFC"), ra.Sfc, rb.Sfc, "kg/(N·h)", "F4", -1);
            Add(res, Loc.T("总压比", "Overall pressure ratio"), ra.Opr, rb.Opr, "", "F2", +1);
            Add(res, Loc.T("涵道比", "Bypass ratio"), ra.Bpr, rb.Bpr, "", "F2", 0);
            Add(res, Loc.T("涡轮前温", "Turbine inlet"), ra.T4, rb.T4, "K", "F0", -1);
            Add(res, Loc.T("进口流量", "Inlet mass flow"), ra.MdotAir, rb.MdotAir, "kg/s", "F2", 0);
            Add(res, Loc.T("高压喘振裕度", "HPC surge margin"), ra.HpcSurgeMargin, rb.HpcSurgeMargin, "%", "F1", +1);
            Add(res, Loc.T("整机长度", "Overall length"), la, lb, "m", "F2", -1);
            Add(res, Loc.T("干重", "Dry mass"), ma.TotalDry, mb.TotalDry, "kg", "F0", -1);
            Add(res, Loc.T("造价", "Cost"), ma.Cost, mb.Cost, "funds", "F0", -1);
            return res;
        }

        private static string First(CycleResult r)
        {
            if (r.Errors != null && r.Errors.Count > 0) return r.Errors[0];
            return Loc.T("解不出来，但没有说明原因", "it did not solve, with no reason given");
        }

        private static void Add(CompareResult res, string label, double a, double b,
                                string unit, string format, int better)
        {
            CompareRow row = new CompareRow();
            row.Label = label;
            row.A = a;
            row.B = b;
            row.Unit = unit;
            row.Format = format;
            row.Better = better;
            res.Rows.Add(row);
        }
    }
}