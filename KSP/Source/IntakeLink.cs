using System;
using System.Collections.Generic;
using System.Reflection;
using JetForge.Core;

namespace JetForge.KSP
{
    /// <summary>
    /// 独立进气道零件与发动机之间**唯一的**联系通道。
    ///
    /// 关系是"这台发动机吸的是那几件进气道送过来的气"，不是"进气道是发动机的一部分"：
    /// 零件各存各的存档、各有各的面板、各有各的质量与造价，拆掉任何一边另一边照旧成立
    /// （拆了进气道，发动机退回自己设计里那份 IntakeSpec；拆了发动机，进气道只是一根管子）。
    ///
    /// 判"接上"的规则只有两条，都能在编辑器里一眼看出来：
    /// ① 同一艘船；② 两个零件的直线距离在 <see cref="MaxDistance"/> 以内。
    /// 不看节点连接：现实中进气道可以挂在机身侧面，靠 S 形管道把气送到后面的核心机，
    /// 强求节点相连会把最常见的那种装机排除掉。
    ///
    /// **原版进气道也算数**：原版那类零件（<c>ModuleResourceIntake</c>）没有我们的参数，
    /// 但有面积和马赫上限，换算见 <see cref="StockIntakeBridge"/>。用了 TweakScale 之类
    /// 缩放 mod 的也照样认——缩放系数按模块反射读，不硬依赖任何一个 mod 的程序集。
    /// </summary>
    public static class IntakeLink
    {
        /// <summary>
        /// 算"接上"的距离阈值：现在**不设限**。
        ///
        /// 以前是 12 m（约一个机身段），本意是防止船首的进气道喂到船尾的发动机。
        /// 但在 KSP 里这没有意义：一艘船就是一个系统，玩家把进气道装在机头、发动机装在机尾，
        /// 中间的机身可能有二三十米长；12 m 一卡，S 形长管道装机就整片失效，
        /// 而"到底接上了没有"在面板上没有任何提示，表现只是"推力莫名其妙不对"。
        /// 所以现在同船的所有进气道都算——要排除某一件，拆掉它。
        /// </summary>
        public const double MaxDistance = double.PositiveInfinity;

        /// <summary>把一件进气道包成合计口径要的最小信息。</summary>
        public static IntakeUnit UnitOf(ModuleJetForgeIntake m)
        {
            return new IntakeUnit(m.Spec, m.FaceDiameter, m.DisplayName, 0.0, m.length);
        }

        /// <summary>
        /// 附近所有算得上进气道的零件：我们自己的 + 原版（含被缩放 mod 改过尺寸的）。
        /// 顺序按距离由近到远——加权平均不看顺序，但告警列表读起来是这个顺序。
        /// </summary>
        public static List<IntakeUnit> UnitsOf(Part enginePart, double maxDistance = MaxDistance)
        {
            List<IntakeUnit> units = new List<IntakeUnit>();
            if (enginePart == null || enginePart.vessel == null) return units;

            double max2 = maxDistance * maxDistance;
            bool unlimited = double.IsInfinity(maxDistance);
            List<Part> near = new List<Part>();
            List<Part> parts = enginePart.vessel.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p == null || p == enginePart) continue;
                if (!unlimited && (p.transform.position - enginePart.transform.position).sqrMagnitude > max2) continue;
                near.Add(p);
            }
            near.Sort(delegate (Part a, Part b)
            {
                double da = (a.transform.position - enginePart.transform.position).sqrMagnitude;
                double db = (b.transform.position - enginePart.transform.position).sqrMagnitude;
                return da.CompareTo(db);
            });

            for (int i = 0; i < near.Count; i++)
            {
                Part p = near[i];
                ModuleJetForgeIntake m = p.FindModuleImplementing<ModuleJetForgeIntake>();
                if (m != null) { units.Add(UnitOf(m)); continue; }
                AppendStockUnits(p, enginePart, units);
            }
            return units;
        }

        /// <summary>
        /// 一件零件上的**所有**原版进气道 → 我们的口径。
        ///
        /// 一个零件挂两个 <c>ModuleResourceIntake</c> 是允许的（有的 mod 这么干），
        /// 以前只取第一个，另一路进气凭空消失。现在逐个算、逐个加。
        /// </summary>
        public static int AppendStockUnits(Part p, Part enginePart, List<IntakeUnit> into)
        {
            if (p == null || into == null) return 0;
            if (p.Modules == null || p.Modules.Count == 0) return 0;

            int added = 0;
            for (int i = 0; i < p.Modules.Count; i++)
            {
                ModuleResourceIntake mri = p.Modules[i] as ModuleResourceIntake;
                if (mri == null) continue;
                IntakeUnit u;
                if (TryStockUnit(p, mri, enginePart, out u)) { into.Add(u); added++; }
            }
            return added;
        }

        /// <summary>
        /// 一件原版进气道 → 我们的口径。不是进气道、或者面积读不出来就返回 false。
        /// 面积/曲线都用反射读，字段名在版本之间改过也无所谓。
        /// </summary>
        public static bool TryStockUnit(Part p, ModuleResourceIntake mri, Part enginePart, out IntakeUnit unit)
        {
            unit = new IntakeUnit();
            if (p == null || mri == null) return false;

            // 只认吸空气的：原版还有吸 IntakeAtm / 其他资源的，那些不喂喷气发动机。
            string res = StringField(mri, "resourceName");
            if (!string.IsNullOrEmpty(res) && res != "IntakeAir" && res != "IntakeAtm") return false;

            double area = FloatField(mri, "area", 0.0);
            if (area <= 0.0) area = FloatField(mri, "unitScalar", 0.0);

            double scale = PartScaleOf(p);
            double mouth = MouthDiameterOf(p);
            if (area <= 0.0 && mouth <= StockIntakeBridge.MinMouthDiameter) return false;

            // 速度上限：**只有修过 cfg 的 mod 才有**，字段名 machLimit（马赫）/ maxIntakeSpeed（m/s）。
            // 原生 cfg 这两个字段都不存在。特别注意：绝不能把 intakeSpeed 当上限——
            // 它是低速吸气流速（环形进气道 15、冲压 10、激波锥 5），当上限用就是 15 m/s 断气。
            double machLimit = FloatField(mri, "machLimit", 0.0);
            if (machLimit <= 0.0) machLimit = StockIntakeBridge.SpeedToMach(FloatField(mri, "maxIntakeSpeed", 0.0));

            // 性能曲线：cfg 里的 machCurve（节点 → 乘子）。没有这一项的是"全马赫等熵"的那种零件。
            List<double> km, kv;
            Func<double, double> curve = null;
            string stamp = "";
            if (ReadCurve(mri, "machCurve", out km, out kv))
            {
                curve = StockIntakeBridge.CurveOf(km, kv);
                stamp = StockIntakeBridge.CurveStamp(km, kv);
            }

            string name = p.partInfo != null && !string.IsNullOrEmpty(p.partInfo.title) ? p.partInfo.title : p.partName;

            // 出口直径按发动机面来：原版进气道是"送多少算多少"，不限制发动机进口尺寸。
            double face = 0.0;
            ModuleJetForgeEngine eng = enginePart != null ? enginePart.FindModuleImplementing<ModuleJetForgeEngine>() : null;
            if (eng != null && eng.Design != null) face = eng.Design.EngineFaceDiameter();

            unit = StockIntakeBridge.UnitOf(area, scale, name, face, mouth, curve, stamp, machLimit);
            return unit.Spec != null && unit.Spec.CaptureDiameter > 0.0;
        }

        /// <summary>
        /// 零件的**唇口直径**（米，已含缩放）。
        ///
        /// 量外形而不是信 cfg 的 `area`，因为那个数不是物理面积（见 <see cref="StockIntakeBridge"/>）。
        /// 取包围盒横向两维里较小的那个当口径：零件沿 Y 轴串接，X/Z 是横截面；
        /// 径向进气道的 Z 会带上长度，取小的那个更接近唇口。
        /// 量不到（没有 Renderer、或者数值离谱）返回 0，调用方退回 cfg 面积。
        /// </summary>
        public static double MouthDiameterOf(Part p)
        {
            if (p == null) return 0.0;

            // 量一次就够：原版件的模型在零件载入之后不再变，而调用方是"每 30 帧扫一遍船"的
            // 指纹比对——每台发动机、每件进气道都要走一趟 GetComponentsInChildren（会分配数组）。
            // 一艘带四台发动机、四件进气道的船上，这就是每半秒几十次分配，飞行中会变成 GC 卡顿。
            // 用 ConditionalWeakTable：零件被拆掉/换船后条目自动消失，不会把已销毁的 Part 拖住。
            MouthBox box;
            if (_mouthCache.TryGetValue(p, out box)) return box.Value;

            double d = MeasureMouth(p);
            // 量为 0 不缓存：那是"这次没量到"（prefab 还没长好），下次可能就有了。
            if (d > 0.0) _mouthCache.Add(p, new MouthBox { Value = d });
            return d;
        }

        private sealed class MouthBox
        {
            public double Value;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Part, MouthBox> _mouthCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Part, MouthBox>();

        private static double MeasureMouth(Part p)
        {

            // 用**网格包围盒 × 世界缩放**量，不用 Renderer.bounds。
            // 这一版踩过：三个不同的原版进气道在 prefab 上量出来一律是 1.250 m——
            // Renderer.bounds 在零件 prefab 上不是几何尺寸（蒙皮/预览会把 bounds 拉成同一个数），
            // 而 MeshFilter.sharedMesh.bounds 是模型自己的、还带上零件的缩放，逐件都不一样。
            UnityEngine.MeshFilter[] mfs = p.GetComponentsInChildren<UnityEngine.MeshFilter>();
            if (mfs == null || mfs.Length == 0) return 0.0;

            bool any = false;
            UnityEngine.Bounds b = new UnityEngine.Bounds();
            for (int i = 0; i < mfs.Length; i++)
            {
                UnityEngine.MeshFilter mf = mfs[i];
                if (mf == null || mf.sharedMesh == null) continue;
                UnityEngine.Vector3 sc = mf.transform.lossyScale;
                UnityEngine.Bounds lb = mf.sharedMesh.bounds;
                UnityEngine.Vector3 size = new UnityEngine.Vector3(
                    lb.size.x * Math.Abs(sc.x), lb.size.y * Math.Abs(sc.y), lb.size.z * Math.Abs(sc.z));
                if (!any) { b = new UnityEngine.Bounds(UnityEngine.Vector3.zero, size); any = true; }
                else b.Encapsulate(new UnityEngine.Bounds(UnityEngine.Vector3.zero, size));
            }
            if (!any) return 0.0;

            double d = Math.Min(b.size.x, b.size.z);
            if (double.IsNaN(d) || d <= StockIntakeBridge.MinMouthDiameter || d > 8.0) return 0.0;
            return d;
        }

        /// <summary>
        /// 读一个 <c>FloatCurve</c> 字段的节点。KSP 的 FloatCurve 包着一条 AnimationCurve，
        /// 这里按字段名反射取 <c>keys</c> 数组，再读每个 key 的 time/value。
        /// </summary>
        public static bool ReadCurve(object owner, string field, out List<double> machs, out List<double> values)
        {
            machs = null; values = null;
            if (owner == null || string.IsNullOrEmpty(field)) return false;

            object curve = MemberValue(owner, field);
            if (curve == null) return false;

            UnityEngine.Keyframe[] keys = KeyframesOf(curve);
            if (keys == null || keys.Length < 2) return false;

            machs = new List<double>(keys.Length);
            values = new List<double>(keys.Length);
            for (int i = 0; i < keys.Length; i++)
            {
                machs.Add(keys[i].time);
                values.Add(keys[i].value);
            }
            // 节点必须升序，否则不做曲线（宁可用恒 1.0 也别用一条乱序曲线）
            for (int i = 1; i < machs.Count; i++)
                if (machs[i] <= machs[i - 1]) { machs = null; values = null; return false; }
            return true;
        }

        private static UnityEngine.Keyframe[] KeyframesOf(object curve)
        {
            // KSP 的 FloatCurve 里包着一条 AnimationCurve，字段名**是 fCurve**（不是 curve）。
            // 这一版踩过：写死 "curve" 时每个原版进气道都"读不到曲线"，
            // 而故障表现是"性能曲线恒 1.0"，看起来像原版不需要曲线。
            object anim = null;
            string[] names = { "fCurve", "curve", "animCurve", "Curve" };
            for (int i = 0; i < names.Length; i++)
            {
                anim = MemberValue(curve, names[i]);
                if (anim != null) break;
            }
            if (anim == null) anim = curve;                 // 万一直接给的就是 AnimationCurve

            object keysObj = MemberValue(anim, "keys");
            UnityEngine.Keyframe[] keys = keysObj as UnityEngine.Keyframe[];
            if (keys != null) return keys;

            // 有些版本 keys 是 IList<Keyframe>：走 IEnumerable
            System.Collections.IEnumerable en = keysObj as System.Collections.IEnumerable;
            if (en == null) return null;
            List<UnityEngine.Keyframe> list = new List<UnityEngine.Keyframe>();
            foreach (object o in en) if (o is UnityEngine.Keyframe) list.Add((UnityEngine.Keyframe)o);
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>
        /// 这台发动机在 (alt, mach) 上接到的进气道合计口径。
        /// 一件都没挂时返回 <see cref="IntakeInstall.None"/>（Present=false，上层走自带口径）。
        /// </summary>
        public static IntakeInstall For(Part enginePart, TechLevel tech, double alt, double mach,
                                        double maxDistance = MaxDistance)
        {
            List<IntakeUnit> units = UnitsOf(enginePart, maxDistance);
            if (units.Count == 0) return IntakeInstall.None;
            return IntakeInstall.Aggregate(units, tech, alt, mach);
        }

        /// <summary>
        /// 装机指纹：附近那几件进气道是谁、各是什么参数。
        /// 发动机每帧拿它跟上次的比，变了就把性能网格标脏——装/拆/改一件进气道
        /// 都得让发动机重新构表，否则飞行中读到的还是旧表。
        /// </summary>
        public static string Stamp(Part enginePart, double maxDistance = MaxDistance)
        {
            if (enginePart == null || enginePart.vessel == null) return "";
            List<IntakeUnit> units = UnitsOf(enginePart, maxDistance);
            if (units.Count == 0) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < units.Count; i++)
            {
                IntakeUnit u = units[i];
                IntakeSpec sp = u.Spec ?? new IntakeSpec();
                sb.Append((int)sp.Kind).Append('|')
                  .Append(sp.CaptureDiameter.ToString("R")).Append('|')
                  .Append(sp.ThroatRatio.ToString("R")).Append('|')
                  .Append(sp.FaceMachDesign.ToString("R")).Append('|')
                  .Append(sp.BleedFrac.ToString("R")).Append('|')
                  .Append(sp.LipRadius.ToString("R")).Append('|')
                  .Append(sp.DuctBendDeg.ToString("R")).Append('|')
                  .Append(u.FaceDiameter.ToString("R")).Append('|')
                  .Append(u.MaxMach.ToString("R")).Append('|')
                  .Append(u.SupplyStamp ?? "").Append(';');
            }
            return sb.ToString();
        }

        /// <summary>这件进气道在喂几台发动机（给进气道面板显示"我到底接没接上"）。</summary>
        public static int EnginesFedBy(Part intakePart, double maxDistance = MaxDistance)
        {
            if (intakePart == null || intakePart.vessel == null) return 0;
            int n = 0;
            double max2 = maxDistance * maxDistance;
            bool unlimited = double.IsInfinity(maxDistance);
            List<Part> parts = intakePart.vessel.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                Part p = parts[i];
                if (p == null || p == intakePart) continue;
                if (p.FindModuleImplementing<ModuleJetForgeEngine>() == null) continue;
                if (!unlimited && (p.transform.position - intakePart.transform.position).sqrMagnitude > max2) continue;
                n++;
            }
            return n;
        }

        // ---- 缩放 mod 与反射读字段 -------------------------------------------------

        /// <summary>
        /// 零件被缩放 mod 改过多少倍。找不到任何缩放模块就是 1.0。
        /// 只按名字找 `currentScale` / `scaleFactor` / `scale`，不引用任何 mod 的程序集
        /// （TweakScale 没装、装了别的、版本改字段，都不该让这里崩）。
        /// </summary>
        public static double PartScaleOf(Part p)
        {
            if (p == null) return 1.0;
            for (int i = 0; i < p.Modules.Count; i++)
            {
                PartModule m = p.Modules[i];
                if (m == null) continue;
                string cn = m.ClassName;
                if (string.IsNullOrEmpty(cn)) cn = m.GetType().Name;
                if (cn.IndexOf("TweakScale", StringComparison.OrdinalIgnoreCase) < 0 &&
                    cn.IndexOf("Scale", StringComparison.OrdinalIgnoreCase) < 0) continue;

                double v = NumberMember(m, "currentScale");
                if (v > 0.0) return v;
                v = NumberMember(m, "scaleFactor");
                if (v > 0.0) return v;
                v = NumberMember(m, "scale");
                if (v > 0.0) return v;
            }
            return 1.0;
        }

        private static object MemberValue(object o, string name)
        {
            if (o == null || string.IsNullOrEmpty(name)) return null;
            Type t = o.GetType();
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
            PropertyInfo pr = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pr != null && pr.CanRead) return pr.GetValue(o, null);
            return null;
        }

        private static double NumberMember(object o, string name)
        {
            if (o == null) return 0.0;
            Type t = o.GetType();
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null)
            {
                object v = f.GetValue(o);
                if (v is float) return (float)v;
                if (v is double) return (double)v;
                if (v is int) return (int)v;
            }
            PropertyInfo pr = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pr != null && pr.CanRead)
            {
                object v = pr.GetValue(o, null);
                if (v is float) return (float)v;
                if (v is double) return (double)v;
                if (v is int) return (int)v;
            }
            return 0.0;
        }

        private static double FloatField(object o, string name, double fallback)
        {
            double v = NumberMember(o, name);
            return v > 0.0 ? v : fallback;
        }

        private static string StringField(object o, string name)
        {
            if (o == null) return null;
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) return null;
            object v = f.GetValue(o);
            return v as string;
        }
    }
}