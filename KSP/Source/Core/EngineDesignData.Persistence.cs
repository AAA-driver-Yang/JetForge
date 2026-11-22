using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JetForge.Core
{
    /// <summary>
    /// 设计参数的 ConfigNode 读写。放在单独文件里，物理内核（Cycle/OffDesign/Criteria）
    /// 才不会因为引用 ConfigNode 而绑死在 KSP 上——命令行回归测试要能用同一份代码。
    /// </summary>
    public partial class EngineDesignData
    {
        /// <summary>part 存档里设计节点用的名字，ModuleJetForgeEngine 与设计器共用。</summary>
        public const string NodeName = "JETFORGE_DESIGN";

        /// <summary>
        /// 存档格式的版本号。**只在"老存档读进来会被理解错"时才加一**，加别的不加
        /// （新字段有缺省值就是兼容的，正是靠这一点，前面几十轮加参数没坏过老存档）。
        /// 写进去是为了能对玩家说一句话：这份设计是哪个年代的、有没有字段是缺省填的。
        /// </summary>
        public const int SchemaVersion = 1;

        /// <summary>读进来那份存档的版本标记；0 = 没有标记（本模组还没写版本号时存的）。</summary>
        public int LoadedSchema { get; private set; }

        /// <summary>
        /// 读进来时的兼容性提示，正常同版本为 null。
        /// 不拦人、不拒绝加载——老存档必须读得回来；但要让人知道"有些数是缺省填的"，
        /// 否则玩家会以为那是他当年调的。
        /// </summary>
        public string MigrationNote { get; private set; }

        /// <summary>
        /// 带前缀的成品提示行，给零件说明用（null = 不用说）。
        /// 单独一个属性是为了它**能被回归钉住**：零件说明那段代码在 KSP 程序集里、
        /// 测试工程编译不进去，能测的只有"这句话长什么样"。
        /// </summary>
        public string MigrationLine
        {
            get
            {
                if (string.IsNullOrEmpty(MigrationNote)) return null;
                return Loc.T("存档版本：", "Save version: ") + MigrationNote;
            }
        }

        public void Save(ConfigNode node)
        {
            node.SetValue("schema", SchemaVersion.ToString(), true);
            node.SetValue("name", DesignName, true);
            node.SetValue("kind", Kind.ToString(), true);
            node.SetValue("tech", TechName, true);
            node.SetValue("inletDiameter", F(InletDiameter), true);
            node.SetValue("nacelleDiameter", F(NacelleDiameter), true);
            node.SetValue("length", F(Length), true);
            // 长度现在按部件算（Core/Arch/EngineLength.cs）；`length` 这一份只是"手填兜底值"。
            // 单独存一个开关：老存档里没有它 = 默认按部件算，于是老机器一读进来就换成算出来的长度。
            node.SetValue("lengthAuto", LengthAuto ? "1" : "0", true);
            node.SetValue("coreInletDiameter", F(CoreInletDiameter), true);
            node.SetValue("shaftCount", ShaftCount.ToString(), true);
            node.SetValue("designMach", F(DesignMach), true);
            node.SetValue("designAlt", F(DesignAlt), true);
            node.SetValue("overrideBpr", F(OverrideBpr), true);
            node.SetValue("nozzle", Nozzle.ToString(), true);
            node.SetValue("vectoring", ThrustVectoring.ToString(), true);
            node.SetValue("gimbalMax", F(GimbalMaxDeg), true);
            // 只写跟缺省不一样的：老存档不该因为我们加了参数就多出一堆键。
            if (PetalCount > 0) node.SetValue("petalCount", PetalCount.ToString(), true);
            if (Math.Abs(PetalLengthFrac - 1.0) > 1e-9) node.SetValue("petalLen", F(PetalLengthFrac), true);
            node.SetValue("vecPitch", VectorPitch.ToString(), true);
            node.SetValue("vecYaw", VectorYaw.ToString(), true);
            node.SetValue("vecRoll", VectorRoll.ToString(), true);
            node.SetValue("control", ((int)Control).ToString(), true);
            node.SetValue("detail", ((int)Detail).ToString(), true);
            node.SetValue("soundGain", F(SoundGain), true);
            node.SetValue("forceOwnBase", ForceOwnBaseSound ? "1" : "0", true);
            // 过载损伤开关与加强系统。老存档里没有这两键 = 默认开、没有加强，读数不变。
            // 慢车那两个旋钮已经撤了（慢车由求解器反解）。仍旧存读，只为两件事：
            // 老存档读得回来不报错、以及"每个公共字段都存得回来"那条回归仍然成立。
            node.SetValue("idleThrustFrac", F(IdleThrustFrac), true);
            node.SetValue("idleN2Pct", F(IdleN2Pct), true);

            ConfigNode fan = node.AddNode("FAN");
            fan.SetValue("diameter", F(Fan.Diameter), true);
            fan.SetValue("stages", Fan.Stages.ToString(), true);
            fan.SetValue("hubTip", F(Fan.HubTipRatio), true);
            fan.SetValue("pressureRatio", F(Fan.PressureRatio), true);
            fan.SetValue("shaft", Fan.Shaft.ToString(), true);
            fan.SetValue("bladeCount", Fan.BladeCount.ToString(), true);
            // 齿轮比只在 GTF 上写盘：直驱机型不写这一行，老存档的读写路径一个字都不用变
            if (Fan.GearRatio > 1.0001)
            {
                fan.SetValue("gearRatio", F(Fan.GearRatio), true);
                if (Fan.GearEta > 0.0) fan.SetValue("gearEta", F(Fan.GearEta), true);
            }

            SaveComp(node, "LPC", Lpc);
            SaveComp(node, "HPC", Hpc);
            SaveTurb(node, "HPT", Hpt);
            SaveTurb(node, "IPT", Ipt);
            SaveTurb(node, "LPT", Lpt);

            ConfigNode b = node.AddNode("BURNER");
            b.SetValue("kind", Burner.Kind.ToString(), true);
            b.SetValue("length", F(Burner.Length), true);
            b.SetValue("exitTemp", F(Burner.ExitTemp), true);
            b.SetValue("dpRatio", F(Burner.DpRatio), true);
            b.SetValue("etaComb", F(Burner.EtaComb), true);
            b.SetValue("linerLd", F(Burner.LinerLd), true);
            b.SetValue("canCount", Burner.CanCount.ToString(), true);

            ConfigNode ab = node.AddNode("AFTERBURNER");
            ab.SetValue("enabled", Afterburner.Enabled.ToString(), true);
            ab.SetValue("exitTemp", F(Afterburner.ExitTemp), true);
            ab.SetValue("fuelRatio", F(Afterburner.FuelRatio), true);
            ab.SetValue("kind", Afterburner.Kind.ToString(), true);
            ab.SetValue("autoLight", Afterburner.AutoLight.ToString(), true);
            ab.SetValue("lightThrottle", F(Afterburner.LightThrottle), true);
            ab.SetValue("length", F(Afterburner.Length), true);

            ConfigNode inl = node.AddNode(IntakeSpecCodec.NodeName);
            IntakeSpecCodec.Save(inl, Intake ?? new IntakeSpec());
        }

        /// <summary>逐级表存成逗号串（空表存空串）。分隔符用逗号而不是空格：ConfigNode 的值按空格切。</summary>
        private static string JoinList(List<double> v)
        {
            if (v == null || v.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < v.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(v[i].ToString("G17", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static List<double> SplitList(string text)
        {
            List<double> o = new List<double>();
            if (string.IsNullOrEmpty(text)) return o;
            string[] parts = text.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                double v;
                if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    o.Add(v);
            }
            return o;
        }

        private static string F(double v)
        {
            // G17 而不是 F6：存储要的是"读回来逐位相同"，不是"文件好看"。
            //
            // 教训来自 RD-33 的存档往返：F6 只保留 6 位小数，读回后设计与原来差 1e-6 量级，
            // 而那台机器的收扩喷管正好站在"是否完全膨胀"的临界上——这点漂移把出口马赫
            // 从 1.545 翻回 1.000，推力凭空多 1.7%。一台飞机的存档存一次就换了一台机器，
            // 这种错比文件里多几位数字难查一百倍。
            return v.ToString("G17", CultureInfo.InvariantCulture);
        }

        private static void SaveComp(ConfigNode node, string tag, CompressorSpec c)
        {
            ConfigNode n = node.AddNode(tag);
            n.SetValue("name", c.Name, true);
            n.SetValue("stages", c.Stages.ToString(), true);
            n.SetValue("tipDiameter", F(c.TipDiameter), true);
            n.SetValue("psiScale", F(c.PsiScale), true);
            n.SetValue("shaft", c.Shaft.ToString(), true);
            n.SetValue("hubTip", F(c.HubTipRatio), true);
            n.SetValue("kind", c.Kind.ToString(), true);
            n.SetValue("stageTip", JoinList(c.StageTip), true);
            n.SetValue("stagePsi", JoinList(c.StagePsi), true);
            // 可调静子：只写"关掉"的那一种。老存档没有这个键 = 有静子，
            // 与缺省一致，也就不会因为多写一个键把老文件变成另一台机器。
            if (!c.Vigv) n.SetValue("vigv", "0", true);
            // 中间级放气：同样只写"跟缺省不一样"的那一种。
            // 缺省 = 有这套机构、按转速自动开阀——老存档没有这两个键，读回来就是它。
            if (!c.Bleed) n.SetValue("bleed", "0", true);
            if (c.BleedFrac > 1e-9) n.SetValue("bleedFrac", F(c.BleedFrac), true);
        }

        private static void SaveTurb(ConfigNode node, string tag, TurbineSpec t)
        {
            ConfigNode n = node.AddNode(tag);
            n.SetValue("name", t.Name, true);
            n.SetValue("stages", t.Stages.ToString(), true);
            n.SetValue("tipDiameter", F(t.TipDiameter), true);
            n.SetValue("psiScale", F(t.PsiScale), true);
            n.SetValue("shaft", t.Shaft.ToString(), true);
            n.SetValue("hubTip", F(t.HubTipRatio), true);
            n.SetValue("stageTip", JoinList(t.StageTip), true);
            n.SetValue("stagePsi", JoinList(t.StagePsi), true);
        }

        public static EngineDesignData Load(ConfigNode node)
        {
            EngineDesignData d = new EngineDesignData();
            if (node == null) return d;
            d.LoadedSchema = (int)GetD(node, "schema", 0.0);
            if (d.LoadedSchema <= 0)
            {
                d.MigrationNote = Loc.T(
                    "这份设计没有版本标记（本模组更早的版本存的）：新加的参数按缺省填了，数值可能跟你当年调的不一样。",
                    "This design carries no version stamp (saved by an earlier build): newer parameters were filled with defaults, so the numbers may differ from what you tuned.");
            }
            else if (d.LoadedSchema > SchemaVersion)
            {
                d.MigrationNote = string.Format(Loc.T(
                    "这份设计来自更新的版本（存档 {0}，本模组认到 {1}）：不认识的键被忽略了，改动后存回去会丢掉它们。",
                    "This design comes from a newer build (file {0}, this build reads {1}): unknown keys were ignored and will be lost if you save over it."),
                    d.LoadedSchema, SchemaVersion);
            }
            d.DesignName = node.GetValue("name") ?? d.DesignName;
            d.Kind = ParseEnum(node.GetValue("kind"), EngineKind.Turbofan);
            d.TechName = node.GetValue("tech") ?? d.TechName;
            d.InletDiameter = GetD(node, "inletDiameter", d.InletDiameter);
            d.NacelleDiameter = GetD(node, "nacelleDiameter", 0.0);
            d.Length = GetD(node, "length", d.Length);
            d.LengthAuto = GetB(node, "lengthAuto", true);
            d.CoreInletDiameter = GetD(node, "coreInletDiameter", 0.0);
            d.ShaftCount = (int)GetD(node, "shaftCount", 2);
            d.DesignMach = GetD(node, "designMach", 0.0);
            d.DesignAlt = GetD(node, "designAlt", 0.0);
            d.OverrideBpr = GetD(node, "overrideBpr", 0.0);
            d.Nozzle = ParseEnum(node.GetValue("nozzle"), NozzleKind.Converging);
            d.ThrustVectoring = GetB(node, "vectoring", false);
            d.GimbalMaxDeg = GetD(node, "gimbalMax", 15.0);
            d.PetalCount = (int)GetD(node, "petalCount", 0.0);
            d.PetalLengthFrac = GetD(node, "petalLen", 1.0);
            d.VectorPitch = GetB(node, "vecPitch", true);
            d.VectorYaw = GetB(node, "vecYaw", true);
            d.VectorRoll = GetB(node, "vecRoll", false);
            d.Control = (ControlKind)(int)GetD(node, "control", 0.0);
            d.Detail = DetailSpec.Parse((int)GetD(node, "detail", 1.0));
            d.SoundGain = GetD(node, "soundGain", 1.0);
            d.ForceOwnBaseSound = GetD(node, "forceOwnBase", 0.0) > 0.5;
            // 这里**不夹**范围：存读要能原样往返（回归会随机灌 86.125 这种值进来验）。
            // 夹在用的地方——Defects.For 与 MassCost.Estimate 各自夹一次，读数与物理共用一个口径。
            d.IdleThrustFrac = GetD(node, "idleThrustFrac", 0.0);
            d.IdleN2Pct = GetD(node, "idleN2Pct", 0.0);

            ConfigNode fan = node.GetNode("FAN");
            if (fan != null)
            {
                d.Fan.Diameter = GetD(fan, "diameter", d.Fan.Diameter);
                d.Fan.Stages = (int)GetD(fan, "stages", d.Fan.Stages);
                d.Fan.HubTipRatio = GetD(fan, "hubTip", d.Fan.HubTipRatio);
                d.Fan.PressureRatio = GetD(fan, "pressureRatio", 0.0);
                d.Fan.Shaft = (int)GetD(fan, "shaft", 1);
                d.Fan.BladeCount = (int)GetD(fan, "bladeCount", 0);
                d.Fan.GearRatio = GetD(fan, "gearRatio", 1.0);
                d.Fan.GearEta = GetD(fan, "gearEta", 0.0);
                if (d.Fan.GearRatio < 1.0) d.Fan.GearRatio = 1.0;
            }
            d.Lpc = LoadComp(node.GetNode("LPC"), d.Lpc);
            d.Hpc = LoadComp(node.GetNode("HPC"), d.Hpc);
            d.Hpt = LoadTurb(node.GetNode("HPT"), d.Hpt);
            d.Ipt = LoadTurb(node.GetNode("IPT"), d.Ipt);
            d.Lpt = LoadTurb(node.GetNode("LPT"), d.Lpt);

            ConfigNode b = node.GetNode("BURNER");
            if (b != null)
            {
                d.Burner.Kind = ParseEnum(b.GetValue("kind"), BurnerKind.Annular);
                d.Burner.Length = GetD(b, "length", d.Burner.Length);
                d.Burner.ExitTemp = GetD(b, "exitTemp", d.Burner.ExitTemp);
                d.Burner.DpRatio = GetD(b, "dpRatio", -1.0);
                d.Burner.LinerLd = GetD(b, "linerLd", 0.0);
                d.Burner.CanCount = (int)GetD(b, "canCount", 0);
                d.Burner.EtaComb = GetD(b, "etaComb", -1.0);
            }
            ConfigNode ab = node.GetNode("AFTERBURNER");
            if (ab != null)
            {
                d.Afterburner.Enabled = GetB(ab, "enabled", false);
                d.Afterburner.ExitTemp = GetD(ab, "exitTemp", 1900.0);
                d.Afterburner.FuelRatio = GetD(ab, "fuelRatio", 0.0);
                d.Afterburner.Kind = ParseEnum(ab.GetValue("kind"), AfterburnerKind.Ring);
                d.Afterburner.AutoLight = GetB(ab, "autoLight", true);
                d.Afterburner.LightThrottle = GetD(ab, "lightThrottle", 0.70);
                d.Afterburner.Length = GetD(ab, "length", 0.0);
            }
            // 老存档没有 INTAKE 节点：保持 EngineDesignData 构造里那份缺省（轴对称、与老行为相同），
            // 不要在这里用 `new IntakeSpec()` 覆盖——那样会悄悄清掉玩家刚填的值。
            ConfigNode inl = node.GetNode(IntakeSpecCodec.NodeName);
            if (inl != null)
            {
                if (d.Intake == null) d.Intake = new IntakeSpec();
                IntakeSpecCodec.Load(inl, d.Intake);
            }
            return d;
        }

        private static CompressorSpec LoadComp(ConfigNode n, CompressorSpec def)
        {
            if (n == null) return def;
            return new CompressorSpec
            {
                Name = n.GetValue("name") ?? def.Name,
                Stages = (int)GetD(n, "stages", def.Stages),
                TipDiameter = GetD(n, "tipDiameter", def.TipDiameter),
                PsiScale = GetD(n, "psiScale", 1.0),
                Shaft = (int)GetD(n, "shaft", 1),
                HubTipRatio = GetD(n, "hubTip", 0.75),
                Kind = ParseKind(n.GetValue("kind"), def.Kind),
                StageTip = SplitList(n.GetValue("stageTip")),
                StagePsi = SplitList(n.GetValue("stagePsi")),
                Bleed = GetB(n, "bleed", true),
                BleedFrac = GetD(n, "bleedFrac", 0.0),
                Vigv = GetB(n, "vigv", true)
            };
        }

        private static CompressorKind ParseKind(string s, CompressorKind def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            try { return (CompressorKind)Enum.Parse(typeof(CompressorKind), s, true); }
            catch { return def; }
        }

        private static TurbineSpec LoadTurb(ConfigNode n, TurbineSpec def)
        {
            if (n == null) return def;
            return new TurbineSpec
            {
                Name = n.GetValue("name") ?? def.Name,
                Stages = (int)GetD(n, "stages", def.Stages),
                TipDiameter = GetD(n, "tipDiameter", def.TipDiameter),
                PsiScale = GetD(n, "psiScale", 1.0),
                Shaft = (int)GetD(n, "shaft", 1),
                HubTipRatio = GetD(n, "hubTip", 0.75),
                StageTip = SplitList(n.GetValue("stageTip")),
                StagePsi = SplitList(n.GetValue("stagePsi"))
            };
        }

        private static double GetD(ConfigNode n, string key, double def)
        {
            string s = n.GetValue(key);
            double v;
            return s != null && double.TryParse(s, NumberStyles.Float,
                CultureInfo.InvariantCulture, out v) ? v : def;
        }

        private static bool GetB(ConfigNode n, string key, bool def)
        {
            string s = n.GetValue(key);
            if (string.IsNullOrEmpty(s)) return def;
            bool v;
            if (bool.TryParse(s, out v)) return v;
            // 存档里同时出现过 "1"/"0"（我们自己写的）与 "True"/"False"（KSP 的 bool 字段）：
            // 只认 bool.TryParse 的话 "0" 会静默落回缺省值——一个"关掉的开关自己又打开了"的坑。
            double d;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) return d > 0.5;
            return def;
        }

        private static T ParseEnum<T>(string s, T def)
        {
            try
            {
                // 存档里出现未知枚举就退回默认——别让一个拼错的字段把整台机器读死
                if (s != null) return (T)Enum.Parse(typeof(T), s, true);
            }
            // 探测豁免：存档里出现未知枚举就退回默认，解析失败本身就是要的结果。
            catch (Exception)
            {
            }
            return def;
        }
    }
}