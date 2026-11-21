using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 故障的三级：设计阶段的警告、运行阶段的风险、真的坏了的故障事件。
    ///
    /// 交接文档 A §八 / 文档 B 都要求把这三层分开，而且写了一句硬规矩：
    /// **同一个缺陷不得在设计阶段与运行阶段重复惩罚。**
    ///
    /// 这句话看着像废话，其实是我们原来的真问题：判据 FAIL 时已经给了一次推力降额
    /// （`Defects.DesignDerate` → ×0.78），飞行里同一个缺陷又按"风险"再降一次
    /// （喘振 → ×0.45，超温 → 累积损伤，超速 → ×0.9）。玩家配出一台喘振裕度不足的机器，
    /// 受的是两遍罚，而报表上只说了一遍理由——他算不出"为什么实际推力只有设计的四成"。
    ///
    /// 规矩落在这里的办法很直接：**每个缺陷只有一条记录**。
    /// 以缺陷的说明文字为识别串（同一条判据在设计期与运行期给的是同一句），
    /// 第二次、第三次来报告的时候只累加次数与持续时间、把级别升到最高的那层，
    /// 然后告诉调用者"这不是新缺陷，别再罚一次"。
    /// </summary>
    public enum FaultTier
    {
        /// <summary>设计阶段：编辑器里就看得见，靠判据与几何校核报出来。</summary>
        Design = 0,
        /// <summary>运行阶段：设计能闭合，但工况把某个余量吃掉了，表现为喘振/超温/超速倾向。</summary>
        Risk = 1,
        /// <summary>故障事件：真的坏了，这一趟不可恢复。</summary>
        Event = 2
    }

    public enum FaultSeverity
    {
        Note = 0,
        Minor = 1,
        Major = 2,
        Critical = 3
    }

    /// <summary>一条缺陷的完整档案：谁触发的、判据是多少、持续多久、后果是什么、怎么恢复。</summary>
    public sealed class FaultRecord
    {
        /// <summary>识别串（同一缺陷反复报告时用它去重）。</summary>
        public string Code = "";
        public FaultTier Tier = FaultTier.Design;
        public FaultSeverity Severity = FaultSeverity.Minor;

        /// <summary>触发变量名（人话，例如"高压压气机级负荷"）。</summary>
        public string Variable = "";
        public double Value;
        public double Limit;
        public string Unit = "";

        /// <summary>后果：推力掉多少、油耗涨多少、会不会熄火。</summary>
        public string Impact = "";
        /// <summary>恢复条件：什么样才算好了（"等到转速回落" / "回编辑器改级负荷"）。</summary>
        public string Recovery = "";

        /// <summary>现在还在发生。故障事件为 true 且不再清。</summary>
        public bool Active;
        /// <summary>被报告过多少次（说明它不是偶发）。</summary>
        public int Count = 1;
        /// <summary>累计持续时间（秒）。运行期由 `FaultBook.Step` 累加。</summary>
        public double Duration;
        /// <summary>第一次 / 最近一次出现的时间（秒，从这本书建立时算起）。</summary>
        public double FirstSeen;
        public double LastSeen;
        /// <summary>最近一次被报告的帧号（`FaultBook.Frame`）。运行风险靠它判断"这一刻还在不在"。</summary>
        public int LastFrame;

        public bool EverPenalized { get { return Tier != FaultTier.Design || Severity >= FaultSeverity.Major; } }

        public string TierName
        {
            get
            {
                if (Tier == FaultTier.Design) return "设计警告";
                if (Tier == FaultTier.Risk) return "运行风险";
                return "故障事件";
            }
        }

        public string Describe()
        {
            string v = string.IsNullOrEmpty(Unit)
                ? string.Format("{0} {1:G4}", Variable, Value)
                : string.Format("{0} {1:G4} {2}", Variable, Value, Unit);
            return string.Format("[{0}/{1}] {2}｜{3}｜{4}｜持续 {5:F1} s ×{6}{7}",
                TierName, Severity, Code, v, Active ? "进行中" : "已恢复", Duration, Count,
                string.IsNullOrEmpty(Impact) ? "" : "｜" + Impact);
        }
    }

    /// <summary>缺陷台账：三级共用一本，同一个缺陷只有一条记录。</summary>
    public sealed class FaultBook
    {
        public List<FaultRecord> Records = new List<FaultRecord>();
        /// <summary>被"同一缺陷不重复罚"挡掉的重复报告次数。非零本身是个信号：说明它在反复犯。</summary>
        public int Deduped;
        /// <summary>台账时钟（秒），由 `Step(dt)` 推进。</summary>
        public double Clock;
        /// <summary>帧号。`Step` 每调一次加一，用来判断运行风险"这一帧还报没报"。</summary>
        public int Frame;

        private readonly Dictionary<string, FaultRecord> _index = new Dictionary<string, FaultRecord>();

        public int Count { get { return Records.Count; } }

        /// <summary>整本清空（模块 Reset 时用；索引一起清，否则下次去重会命中幽灵记录）。</summary>
        public void Reset()
        {
            Records.Clear();
            _index.Clear();
            Deduped = 0;
            Clock = 0.0;
            Frame = 0;
        }

        public FaultRecord Find(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            FaultRecord r;
            return _index.TryGetValue(code, out r) ? r : null;
        }

        /// <summary>
        /// 报一条缺陷。返回 **true 表示这是一条新缺陷**（调用者该按它施加一次后果）；
        /// 返回 false 表示已经记过了——只累加次数、按需升级级别，**不要再罚一次**。
        /// </summary>
        public bool Raise(string code, FaultTier tier, FaultSeverity severity,
                          string variable, double value, double limit, string unit,
                          string impact, string recovery)
        {
            if (string.IsNullOrEmpty(code)) return false;

            FaultRecord rec = Find(code);
            if (rec != null)
            {
                // 同一个缺陷第二次、第三次来报告：记录留着、级别升上去，但**不再算新缺陷**，
                // 调用者拿到 false 就不该再罚一遍。这是"同码不重复惩罚"的落点。
                bool newEpisode = !rec.Active;
                rec.LastSeen = Clock;
                rec.LastFrame = Frame;
                rec.Active = true;
                if (tier > rec.Tier) rec.Tier = tier;
                if (severity > rec.Severity) rec.Severity = severity;
                if (value < rec.Value) rec.Value = value;      // 记住最差的那个读数
                if (newEpisode) rec.Count++;                   // 新的一轮，不是新的一条
                else Deduped++;                                // 同一轮里反复报，挡住不给二次罚
                return false;
            }

            rec = new FaultRecord();
            rec.Code = code;
            rec.Tier = tier;
            rec.Severity = severity;
            rec.Variable = variable ?? "";
            rec.Value = value;
            rec.Limit = limit;
            rec.Unit = unit ?? "";
            rec.Impact = impact ?? "";
            rec.Recovery = recovery ?? "";
            rec.Active = true;
            rec.FirstSeen = Clock;
            rec.LastSeen = Clock;
            rec.LastFrame = Frame;
            Records.Add(rec);
            _index[code] = rec;
            return true;
        }

        /// <summary>
        /// 推进时钟。已经报过的记录要判断这一刻还在不在：运行风险靠"这一帧报过没有"判，
        /// 设计警告与故障事件不清（它们不是每帧重报的东西）。
        /// </summary>
        public void Step(double dt)
        {
            if (dt <= 0.0) return;
            Clock += dt;
            Frame++;
            for (int i = 0; i < Records.Count; i++)
            {
                FaultRecord r = Records[i];
                if (!r.Active) continue;
                if (r.Tier != FaultTier.Risk) { r.Duration += dt; continue; }
                // 运行风险：本帧报过就继续算"还在发生"，没报过就收工（历史留在台账里）。
                if (r.LastFrame == Frame - 1) r.Duration += dt;
                else r.Active = false;
            }
        }

        /// <summary>这一刻不再发生了。记录留着（历史有用），但不再累加时间。</summary>
        public void Clear(string code)
        {
            FaultRecord r = Find(code);
            if (r != null) r.Active = false;
        }

        /// <summary>
        /// 每帧开头清一次"运行风险"的活动标记：只有本帧又报上来的才算还在发生。
        /// 设计警告不清（那是设计属性，一直在），故障事件也不清（坏了就是坏了）。
        /// </summary>
        public void ExpireRisks()
        {
            for (int i = 0; i < Records.Count; i++)
                if (Records[i].Tier == FaultTier.Risk) Records[i].Active = false;
        }

        public FaultSeverity Worst
        {
            get
            {
                FaultSeverity w = FaultSeverity.Note;
                for (int i = 0; i < Records.Count; i++)
                    if (Records[i].Active && Records[i].Severity > w) w = Records[i].Severity;
                return w;
            }
        }

        public int ActiveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Records.Count; i++) if (Records[i].Active) n++;
                return n;
            }
        }

        /// <summary>把判据报告里不成立的条目记成"设计警告"。同一条反复评估只记一次。</summary>
        public void BookFindings(CheckReport rep, TechLevel tech = null)
        {
            if (rep == null || rep.Findings == null) return;
            for (int i = 0; i < rep.Findings.Count; i++)
            {
                Finding f = rep.Findings[i];
                if (f == null || f.LevelValue == Level.Ok) continue;
                FaultSeverity sev = f.LevelValue == Level.Fail ? FaultSeverity.Major : FaultSeverity.Minor;
                Raise(f.Message, FaultTier.Design, sev,
                    string.IsNullOrEmpty(f.Key) ? "判据" : f.Key,
                    f.Value, f.Limit, f.Unit,
                    f.LevelValue == Level.Fail ? "设计不成立，飞行中降额运转" : "踩线，性能不宽裕",
                    "回编辑器改设计");
            }
        }

        /// <summary>
        /// 判据报告里有不成立的条目，而它的说明文字在台账里已经被记成"运行风险/故障事件"了。
        /// 这就是"同一个缺陷在设计期与运行期都罚了一遍"的样子——
        /// 调用者该据此**放过其中一次**（我们放过设计期的降额，留着运行期那条更具体的）。
        /// </summary>
        public bool DesignOverlapsRuntime(CheckReport rep, out string code)
        {
            code = "";
            if (rep == null || rep.Findings == null) return false;
            for (int i = 0; i < rep.Findings.Count; i++)
            {
                Finding f = rep.Findings[i];
                if (f == null || f.LevelValue == Level.Ok) continue;
                FaultRecord r = Find(f.Message);
                if (r != null && r.Tier >= FaultTier.Risk && r.Active)
                {
                    code = f.Message;
                    return true;
                }
            }
            return false;
        }

        public string Describe(int maxLines = 8)
        {
            if (Records.Count == 0) return "缺陷台账：空";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendFormat("缺陷台账：{0} 条（进行中 {1}｜最重 {2}｜去重拦截 {3} 次）",
                Records.Count, ActiveCount, Worst, Deduped);
            int n = 0;
            for (int i = 0; i < Records.Count && n < maxLines; i++, n++)
            {
                sb.Append("\n  ");
                sb.Append(Records[i].Describe());
            }
            if (Records.Count > n) sb.AppendFormat("\n  ……还有 {0} 条", Records.Count - n);
            return sb.ToString();
        }
    }
}