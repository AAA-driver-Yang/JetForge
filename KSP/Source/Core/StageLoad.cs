using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JetForge.Core
{
    /// <summary>一级的负荷读数。</summary>
    public class StageLoadRow
    {
        public string Part = "";
        public int Index;
        /// <summary>这一级的温升（K）。</summary>
        public double DeltaT;
        /// <summary>这一级温升占所属压气机总温升的比例（0~1）。</summary>
        public double Share;
        /// <summary>这一级温升比全机平均级温升高出或低出多少（1 = 正好平均）。</summary>
        public double VsMean;
        /// <summary>这一级的压比。</summary>
        public double Pi;
        /// <summary>这一级看到的流量（kg/s）。</summary>
        public double MassFlow;
        /// <summary>这一级之后流量掉了（中间级放气就发生在这里）。</summary>
        public bool BleedHere;
    }

    /// <summary>
    /// 逐级负荷分配。
    ///
    /// 这台机器只有一个整机喘振裕度，而压气机失速从来是**某一级先转不动**：
    /// 级负荷分得越不均，最先吃满的那一级离失速越近，整机的裕度读数就越不能代表它。
    /// 所以这一段不假装知道每一级的喘振边界（那要有逐级特性图），只把
    /// "谁吃得最重、重多少倍"算清楚——这是从已有的逐级温升与流量里就能得出的、
    /// 不含任何新假设的量。
    ///
    /// 真机的设计惯例大致是全机级温升均匀（等功分布），实测最重一级与最轻一级的
    /// 温升比在 1.5 以内；超过 2 倍基本可以说"这台的级间负荷没配平"。
    /// </summary>
    public sealed class StageLoadReport
    {
        public bool Ok;
        public string Failure = "";
        public List<StageLoadRow> Rows = new List<StageLoadRow>();
        /// <summary>最重一级的温升 ÷ 该压气机平均级温升。</summary>
        public double WorstVsMean = 1.0;
        public string WorstStage = "";
        /// <summary>同一台压气机内，最重一级 ÷ 最轻一级的温升比。</summary>
        public double PeakToTrough = 1.0;
        public string PeakToTroughPart = "";
        /// <summary>中间级放气发生在第几级之后（0 = 没放气）。</summary>
        public int BleedStage;
        public double BleedFlow;

        public static StageLoadReport Of(List<StageRow> stages)
        {
            StageLoadReport rep = new StageLoadReport();
            if (stages == null || stages.Count == 0)
            {
                rep.Ok = false;
                rep.Failure = Loc.T("没有逐级数据：这个结果不是从级进级出的部件网络算出来的",
                    "No per-stage data: this result did not come from the stage-by-stage component network");
                return rep;
            }

            // 先按部件分组：风扇、低压、高压各自的级数不同，平均级温升不能混着算。
            Dictionary<string, List<StageRow>> groups = new Dictionary<string, List<StageRow>>();
            List<string> order = new List<string>();
            for (int i = 0; i < stages.Count; i++)
            {
                StageRow s = stages[i];
                if (s == null) continue;
                string key = s.Part ?? "";
                if (!groups.ContainsKey(key)) { groups[key] = new List<StageRow>(); order.Add(key); }
                groups[key].Add(s);
            }

            for (int g = 0; g < order.Count; g++)
            {
                List<StageRow> rows = groups[order[g]];
                if (rows.Count == 0) continue;

                double sum = 0.0;
                double maxT = 0.0;
                double minT = double.MaxValue;
                int maxIdx = -1;
                for (int i = 0; i < rows.Count; i++)
                {
                    double dt = rows[i].TOut - rows[i].TIn;
                    if (!GasState.IsFinite(dt) || dt < 0.0) dt = 0.0;
                    sum += dt;
                    if (dt > maxT) { maxT = dt; maxIdx = i; }
                    if (dt < minT) minT = dt;
                }
                double mean = sum / rows.Count;
                if (minT == double.MaxValue) minT = 0.0;

                for (int i = 0; i < rows.Count; i++)
                {
                    StageRow s = rows[i];
                    double dt = s.TOut - s.TIn;
                    if (!GasState.IsFinite(dt) || dt < 0.0) dt = 0.0;
                    StageLoadRow row = new StageLoadRow();
                    row.Part = s.Part ?? "";
                    row.Index = s.Index;
                    row.DeltaT = dt;
                    row.Share = sum > 1e-9 ? dt / sum : 0.0;
                    row.VsMean = mean > 1e-9 ? dt / mean : 0.0;
                    row.Pi = s.Pi;
                    row.MassFlow = s.MassFlow;
                    // 级间放气：后面那一级看到的流量比前面少。级表里流量阶梯下降的位置
                    // 就是抽气点——比单独记一个"第几级"更可靠，因为它来自实际流量。
                    if (i > 0)
                    {
                        double before = rows[i - 1].MassFlow;
                        if (before > 1e-6 && s.MassFlow < before * 0.995)
                        {
                            row.BleedHere = true;
                            if (rep.BleedStage == 0)
                            {
                                rep.BleedStage = s.Index;
                                rep.BleedFlow = before - s.MassFlow;
                            }
                        }
                    }
                    rep.Rows.Add(row);

                    if (mean > 1e-9 && row.VsMean > rep.WorstVsMean)
                    {
                        rep.WorstVsMean = row.VsMean;
                        rep.WorstStage = string.Format(CultureInfo.InvariantCulture, Loc.T("{0} 第 {1} 级", "{0} stage {1}"), row.Part, row.Index);
                    }
                }

                if (minT > 1e-9 && rows.Count >= 2)
                {
                    double ratio = maxT / minT;
                    if (ratio > rep.PeakToTrough)
                    {
                        rep.PeakToTrough = ratio;
                        rep.PeakToTroughPart = string.Format(CultureInfo.InvariantCulture, Loc.T("{0} 第 {1} 级", "{0} stage {1}"), rows[maxIdx].Part, rows[maxIdx].Index);
                    }
                }
            }

            rep.Ok = rep.Rows.Count > 0;
            if (!rep.Ok) rep.Failure = Loc.T("逐级数据里没有可用的级", "No usable stage in the per-stage data");
            return rep;
        }

        /// <summary>一句话结论（给读数区与判据共用，不重复造话）。</summary>
        public string Headline()
        {
            if (!Ok) return Failure;
            if (WorstVsMean > 2.2)
                return string.Format(CultureInfo.InvariantCulture,
                    Loc.T("{0} 吃了平均级的 {1:F2} 倍温升：级间负荷没配平，喘振会先在那一级出现",
                    "{0} takes {1:F2}x the average stage temperature rise: loading is not balanced, surge will start at that stage"),
                    WorstStage, WorstVsMean);
            if (WorstVsMean > 1.6)
                return string.Format(CultureInfo.InvariantCulture,
                    Loc.T("{0} 吃了平均级的 {1:F2} 倍温升：偏重，但还在真机的配平范围里",
                    "{0} takes {1:F2}x the average stage temperature rise: heavy, still within real-engine trim"),
                    WorstStage, WorstVsMean);
            return string.Format(CultureInfo.InvariantCulture,
                Loc.T("级间负荷均匀（最重一级 {0:F2} 倍平均）",
                "Stage loading is even (heaviest stage {0:F2}x average)"), WorstVsMean);
        }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Headline());
            if (Ok && PeakToTrough > 1.0 && PeakToTroughPart.Length > 0)
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("；最重 {0}，最重/最轻 {1:F2}", "; heaviest {0}, heaviest/lightest {1:F2}"), PeakToTroughPart, PeakToTrough));
            if (BleedStage > 0)
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    Loc.T("；第 {0} 级后放气 {1:F2} kg/s", "; bleeding {1:F2} kg/s after stage {0}"), BleedStage, BleedFlow));
            return sb.ToString();
        }
    }
}