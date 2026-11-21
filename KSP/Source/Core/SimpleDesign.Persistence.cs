using System.Globalization;

namespace JetForge.Core
{
    /// <summary>
    /// 简单模式的存档层。放在单独文件里，是因为 `Simple.cs` 必须保持"零 KSP 依赖"——
    /// 那样同一份算法才能在命令行回归里跑。这里只做 ConfigNode ↔ 字段的搬运。
    /// </summary>
    public static class SimpleDesignPersistence
    {
        static double D(ConfigNode n, string key, double def)
        {
            string s = n.GetValue(key);
            double v;
            if (s == null) return def;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        static void S(ConfigNode n, string key, double v)
        {
            n.SetValue(key, v.ToString("F6", CultureInfo.InvariantCulture), true);
        }

        public static void Save(SimpleDesign sd, ConfigNode node)
        {
            if (sd == null || node == null) return;
            node.SetValue("name", sd.Name ?? "simple engine", true);
            node.SetValue("tech", sd.Tech != null ? sd.Tech.Name : "1970s", true);
            node.SetValue("kind", sd.Kind.ToString(), true);
            node.SetValue("nozzle", sd.Nozzle.ToString(), true);
            S(node, "diameter", sd.Diameter);
            S(node, "bpr", sd.Bpr);
            S(node, "opr", sd.Opr);
            S(node, "t4", sd.T4);
            S(node, "tExhaust", sd.TExhaust);
            S(node, "abFuelRatio", sd.AbFuelRatio);
            S(node, "designMach", sd.DesignMach);
            S(node, "designAlt", sd.DesignAlt);
            S(node, "fpr", sd.Fpr);
            S(node, "t4Idle", sd.T4Idle);
            S(node, "abThrottle", sd.AbThrottle);
            S(node, "etaOffdesignK", sd.EtaOffdesignK);
            node.SetValue("intakeKind", sd.IntakeKind.ToString(), true);
            S(node, "intakeThroatRatio", sd.IntakeThroatRatio);
            S(node, "intakeBleedFrac", sd.IntakeBleedFrac);
            S(node, "intakeDuctBend", sd.IntakeDuctBendDeg);
        }

        public static SimpleDesign Load(ConfigNode node)
        {
            var sd = new SimpleDesign();
            if (node == null) return sd;
            string name = node.GetValue("name");
            if (!string.IsNullOrEmpty(name)) sd.Name = name;
            string tech = node.GetValue("tech");
            sd.Tech = (!string.IsNullOrEmpty(tech) ? TechLevel.Get(tech) : null) ?? TechLevel.Get("1970s");
            string kind = node.GetValue("kind");
            sd.Kind = kind == "Turbojet" ? EngineKind.Turbojet : EngineKind.Turbofan;
            string nozzle = node.GetValue("nozzle");
            if (nozzle == "Variable") sd.Nozzle = NozzleKind.Variable;
            else if (nozzle == "ConvergingDiverging") sd.Nozzle = NozzleKind.ConvergingDiverging;
            else sd.Nozzle = NozzleKind.Converging;

            sd.Diameter = D(node, "diameter", sd.Diameter);
            sd.Bpr = D(node, "bpr", sd.Bpr);
            sd.Opr = D(node, "opr", sd.Opr);
            sd.T4 = D(node, "t4", sd.T4);
            sd.TExhaust = D(node, "tExhaust", sd.TExhaust);
            sd.AbFuelRatio = D(node, "abFuelRatio", sd.AbFuelRatio);
            sd.DesignMach = D(node, "designMach", sd.DesignMach);
            sd.DesignAlt = D(node, "designAlt", sd.DesignAlt);
            sd.Fpr = D(node, "fpr", sd.Fpr);
            sd.T4Idle = D(node, "t4Idle", sd.T4Idle);
            sd.AbThrottle = D(node, "abThrottle", sd.AbThrottle);
            sd.EtaOffdesignK = D(node, "etaOffdesignK", sd.EtaOffdesignK);
            string ik = node.GetValue("intakeKind");
            IntakeKind parsed;
            if (ik != null && System.Enum.IsDefined(typeof(IntakeKind), ik) == false)
            {
                // 拼错就退回缺省，别让一个字段把整台机器读死（与 ParseEnum 同一套规矩）
                parsed = IntakeKind.Axial;
            }
            else
            {
                parsed = ik != null ? (IntakeKind)System.Enum.Parse(typeof(IntakeKind), ik, true) : sd.IntakeKind;
            }
            sd.IntakeKind = parsed;
            sd.IntakeThroatRatio = D(node, "intakeThroatRatio", sd.IntakeThroatRatio);
            sd.IntakeBleedFrac = D(node, "intakeBleedFrac", sd.IntakeBleedFrac);
            sd.IntakeDuctBendDeg = D(node, "intakeDuctBend", sd.IntakeDuctBendDeg);
            sd.Normalize();
            return sd;
        }
    }
}