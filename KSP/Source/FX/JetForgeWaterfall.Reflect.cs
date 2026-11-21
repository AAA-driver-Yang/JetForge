using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using JetForge.Core;

namespace JetForge.FX
{

    /// <summary>
    /// partial：反射小工具。Waterfall 是软依赖，没装就没有类型可引用，所有跨模组的
    /// 读写都走反射；这一半文件里没有一个字关于气动，全是"找字段、找方法、写值"。
    /// </summary>
    public sealed partial class JetForgeWaterfall
    {

        private static PartModule FindModule(Part part, string moduleId)
        {
            try
            {
                if (part.Modules == null) return null;
                for (int i = 0; i < part.Modules.Count; i++)
                {
                    PartModule pm = part.Modules[i];
                    if (pm == null) continue;
                    if (pm.moduleName != "ModuleWaterfallFX") continue;
                    BaseField f = pm.Fields["moduleID"];
                    string id = f != null ? f.GetValue(pm).ToString() : "";
                    if (id == moduleId) return pm;
                }
            }
            // 探测豁免：反射找 Waterfall 模块，找不到即未安装
            catch { }
            return null;
        }

        /// <summary>
        /// 把每个 FX 的模板缩放改成这台机器的尺寸，并**立刻应用**。返回是否写进去了。
        ///
        /// 原来这里只 `p.SetValue(fx, s)` 就完事——`TemplateScaleOffset` 是个字段型属性，
        /// 写进去只是把值存下，真正把渲染体缩放改掉的是 `ApplyTemplateOffsets()`。
        /// 于是"缩放到 0.66"这句话从来没落到模型上，羽流一直是模板原始的 1.0 倍：
        /// 按 0.35 m 参考半径 authored 的火焰，贴在 0.26 m 的喷口上就粗出 34%，
        /// 从喷口外壁里穿出来——玩家报的"加力火焰直径超过喷口直接穿模"。
        /// </summary>
        private static bool ApplyOffsets(PartModule mod, Vector3 s, bool zeroPosition)
        {
            return ApplyOffsets(mod, s, s, zeroPosition);
        }

        /// <summary>
        /// 同上，但给热浪（refraction）单独一套缩放。
        ///
        /// 为什么必须分开：羽流图是"尾焰多粗"，热浪是"空气被烤得多烫"，两者不是一回事。
        /// 上一版整套一个缩放，于是大涵道机干态把热浪按**外涵出口**放大——玩家看到的是
        /// 一圈比短舱还粗的抖动柱，位置和直径都不对。热浪只属于核心那股热气，
        /// 直径应当**略小于核心喷口**，跟外面套了几层风扇没有关系。
        /// </summary>
        private static bool ApplyOffsets(PartModule mod, Vector3 s, Vector3 hazeS, bool zeroPosition)
        {
            bool wrote = false;
            foreach (object fx in FxList(mod))
            {
                try
                {
                    Type t = fx.GetType();
                    Vector3 scale = FxName(fx) == "refraction" ? hazeS : s;
                    Vector3 pos = Vector3.zero, rot = Vector3.zero;
                    PropertyInfo pp = t.GetProperty("TemplatePositionOffset", BindingFlags.Instance | BindingFlags.Public);
                    if (pp != null) pos = (Vector3)pp.GetValue(fx, null);
                    PropertyInfo pr = t.GetProperty("TemplateRotationOffset", BindingFlags.Instance | BindingFlags.Public);
                    if (pr != null) rot = (Vector3)pr.GetValue(fx, null);
                    if (zeroPosition) pos = Vector3.zero;
                    MethodInfo ap = t.GetMethod("ApplyTemplateOffsets",
                        BindingFlags.Instance | BindingFlags.Public, null,
                        new Type[] { typeof(Vector3), typeof(Vector3), typeof(Vector3) }, null);
                    if (ap != null)
                    {
                        ap.Invoke(fx, new object[] { pos, rot, scale });
                        wrote = true;
                        continue;
                    }
                    PropertyInfo ps = t.GetProperty("TemplateScaleOffset", BindingFlags.Instance | BindingFlags.Public);
                    if (ps == null || !ps.CanWrite) continue;
                    ps.SetValue(fx, scale, null);
                    wrote = true;
                }
                // 探测豁免：反射写缩放字段，失败即这个字段不叫这个名字
                catch { }
            }
            return wrote;
        }

        /// <summary>自检用：读回某个模块第一个 FX 当前的缩放，证明它到底有没有落下去。</summary>
        public static string DescribeScale(Part part, string moduleId)
        {
            PartModule pm = FindModule(part, moduleId);
            if (pm == null) return moduleId + "：没有这个模块";
            IList l = FxList(pm);
            if (l == null || l.Count == 0) return moduleId + "：FX 还没建";
            try
            {
                object fx = l[0];
                PropertyInfo ps = fx.GetType().GetProperty("TemplateScaleOffset", BindingFlags.Instance | BindingFlags.Public);
                if (ps == null) return moduleId + "：没有 TemplateScaleOffset";
                Vector3 v = (Vector3)ps.GetValue(fx, null);
                return string.Format("{0}：缩放 {1:F3}/{2:F3}/{3:F3}", moduleId, v.x, v.y, v.z);
            }
            catch (Exception e) { return moduleId + "：读不出（" + e.Message + "）"; }
        }

        /// <summary>返回是否真的写进去了（读回 values[0] 核对）。失败要让调用方下一帧重试。</summary>
        private bool PushLevel(PartModule mod, PlumeClass cls, double level)
        {
            if (mod == null) return false;
            int slot = (int)cls;
            object ctrl = _throttleCtrl[slot];
            if (ctrl == null)
            {
                ctrl = FindController(mod, "throttle");
                _throttleCtrl[slot] = ctrl;
            }
            if (ctrl == null) return false;
            try
            {
                Type t = ctrl.GetType();

                // 顺序不能反。反汇编 WaterfallController 的结果：
                //   set_overrideValue：先存 _overrideValue，**只有 overridden 为真**才调 Set()；
                //   Update()：如果 overridden 为真就跳过 UpdateInternal()（不再从 links 刷新）。
                // 先写值再 SetOverride → 值只在 _overrideValue 里躺着，values[] 仍是上一帧的
                // 油门值，于是"加力尾焰照样跟着油门 0~100% 长"（玩家报的第一个故障）。
                MethodInfo so = t.GetMethod("SetOverride",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new Type[] { typeof(bool) }, null);
                if (so != null) so.Invoke(ctrl, new object[] { true });
                else SetField(t, ctrl, "_overridden", true);

                // 直接调 Set(float) 把值写进 values[]；属性那条路依赖上一条的副作用，
                // 多留一条兜底不吃亏（两个都存在，先走 Set）。
                MethodInfo set = t.GetMethod("Set",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new Type[] { typeof(float) }, null);
                if (set != null) set.Invoke(ctrl, new object[] { (float)level });
                else
                {
                    PropertyInfo pv = t.GetProperty("overrideValue",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pv != null && pv.CanWrite) pv.SetValue(ctrl, (float)level, null);
                    else SetField(t, ctrl, "_overrideValue", (float)level);
                }
            }
            catch (Exception e) { Note = "灌比例失败：" + e.Message; return false; }

            // 读回 values[0]：模板曲线读的就是它（不是 overrideValue）。
            // 只有它变了才算成功；读不到数组就认"Set 没抛异常"。
            double got = ReadValues0(ctrl);
            if (double.IsNaN(got)) return true;
            return Math.Abs(got - level) <= 0.02;
        }

        private static double ReadValues0(object ctrl)
        {
            try
            {
                FieldInfo f = ctrl.GetType().GetField("values",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Array a = f != null ? f.GetValue(ctrl) as Array : null;
                if (a == null || a.Length < 1) return double.NaN;
                return Convert.ToDouble(a.GetValue(0));
            }
            catch { return double.NaN; }
        }

        private static void SetField(Type t, object o, string name, object v)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) f.SetValue(o, v);
        }

        private static object FindController(PartModule mod, string name)
        {
            // 两条路都要试：`Controllers` 属性可能是"已醒来的控制器"子集（Start 之后才齐），
            // `allControllers` 字段是 OnLoad 解析 cfg 时建的。只认属性那一条，
            // 在模块还没醒来的帧上会拿到空表 → 我们什么都灌不进去，
            // 表现就是"加力尾焰照样跟着油门 0~100% 长"。自检里那句
            // "没有 throttle 控制器"就是这么来的。
            object hit = PickController(ReadList(mod, "Controllers"), name);
            if (hit != null) return hit;
            return PickController(ReadList(mod, "allControllers"), name);
        }

        private static IList ReadList(PartModule mod, string member)
        {
            try
            {
                Type t = mod.GetType();
                PropertyInfo p = t.GetProperty(member,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanRead)
                {
                    IList l = p.GetValue(mod, null) as IList;
                    if (l != null && l.Count > 0) return l;
                }
                FieldInfo f = t.GetField(member,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(mod) as IList;
            }
            // 探测豁免：反射取列表字段，取不到即没有
            catch { }
            return null;
        }

        private static object PickController(IList list, string name)
        {
            if (list == null) return null;
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    object c = list[i];
                    if (c == null) continue;
                    Type t = c.GetType();
                    PropertyInfo pn = t.GetProperty("name",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    string n = pn != null ? pn.GetValue(c, null) as string : null;
                    if (n == null)
                    {
                        FieldInfo f = t.GetField("name",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        n = f != null ? f.GetValue(c) as string : null;
                    }
                    if (n == name) return c;
                }
            }
            // 探测豁免：反射取控制器，取不到即没有
            catch { }
            return null;
        }

        /// <summary>给飞行日志用：这台机器干/湿两套控制器到底灌进去没有。</summary>
        public string Diag()
        {
            if (!Attached) return "未挂";
            return "湿 " + DescribeController(_part, ModuleIdOf(WetClass), "throttle")
                 + "｜干 " + DescribeController(_part, ModuleIdOf(DryClass), "throttle");
        }

        /// <summary>
        /// 飞行中读回羽流缩放——用来回答"缩到底有没有生效"。
        /// 只写属性不调 ApplyTemplateOffsets 时，这里读出来的值会是 0，
        /// 而屏幕上的火焰仍是模板原始大小：那正是"火焰比喷口还粗"的来源。
        /// </summary>
        public string DescribeScaleText()
        {
            if (!Attached || _part == null) return "羽流未挂";
            return DescribeScale(_part, ModuleIdOf(DryClass)) + "｜" + DescribeScale(_part, ModuleIdOf(WetClass));
        }

        /// <summary>
        /// cfg 里这个模块声明了哪些 CONTROLLER。控制器实例要到 Start() 才建，
        /// prefab 上 `Controllers` 是空表——所以自检里"没找到控制器"不代表 cfg 写错了。
        /// 这条读的是模块自带的 config 节点，能证明名字对不对。
        /// </summary>
        public static string ConfControllerNames(Part part, string moduleId)
        {
            PartModule pm = FindModule(part, moduleId);
            if (pm == null) return moduleId + "：没有模块";
            try
            {
                FieldInfo f = pm.GetType().GetField("config",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ConfigNode cfg = f != null ? f.GetValue(pm) as ConfigNode : null;
                if (cfg == null) return moduleId + "：没有 config 节点";
                ConfigNode[] cs = cfg.GetNodes("CONTROLLER");
                if (cs == null || cs.Length == 0) return moduleId + "：cfg 里 0 个控制器";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < cs.Length; i++)
                {
                    if (i > 0) sb.Append("/");
                    sb.Append(cs[i].GetValue("name"));
                }
                return string.Format("{0}：{1} 个（{2}）", moduleId, cs.Length, sb);
            }
            catch (Exception e) { return moduleId + "：读 config 抛了 " + e.Message; }
        }

        private static void SetEnabled(PartModule mod, bool enabled)
        {
            ApplyEnabled(mod, enabled);
        }

        /// <summary>返回是否真的调到了至少一个 FX（FX 表为空 = 模块还没 Start，办不到）。</summary>
        private static bool ApplyEnabled(PartModule mod, bool enabled)
        {
            if (mod == null) return false;
            bool wrote = false;
            foreach (object fx in FxList(mod))
            {
                try
                {
                    MethodInfo m = fx.GetType().GetMethod("SetEnabled",
                        BindingFlags.Instance | BindingFlags.Public);
                    if (m != null) { m.Invoke(fx, new object[] { enabled }); wrote = true; }
                }
                // 探测豁免：反射开关键，没有就是没有
                catch { }
            }
            return wrote;
        }

        private static IList FxList(PartModule mod)
        {
            if (mod == null) return null;
            try
            {
                PropertyInfo p = mod.GetType().GetProperty("FX", BindingFlags.Instance | BindingFlags.Public);
                if (p == null) return null;
                return p.GetValue(mod, null) as IList;
            }
            catch { return null; }
        }

        /// <summary>自检用：数一下某个模块里有几个 FX（没有就 0）。</summary>
        public static int FxCount(Part part, string moduleId)
        {
            PartModule pm = FindModule(part, moduleId);
            IList l = FxList(pm);
            return l != null ? l.Count : -1;
        }

        /// <summary>自检用：某个模块的 throttle 控制器有没有被 override 掉。</summary>
        public static string DescribeController(Part part, string moduleId, string controllerName)
        {
            PartModule pm = FindModule(part, moduleId);
            if (pm == null) return moduleId + "：没有这个模块";
            object c = FindController(pm, controllerName);
            if (c == null) return moduleId + "：没有 " + controllerName + " 控制器";
            Type t = c.GetType();
            FieldInfo ov = t.GetField("_overridden",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            PropertyInfo pv = t.GetProperty("overrideValue",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            bool on = ov != null && ov.GetValue(c) is bool && (bool)ov.GetValue(c);
            string val = pv != null ? Convert.ToString(pv.GetValue(c, null)) : "?";

            // values[] 才是模板曲线真正读的数。overrideValue 对了而 values 没跟上，
            // 就是"值躺在那儿没人用"——加力羽流照样跟着油门走。
            string cur = "?";
            FieldInfo vf = t.GetField("values",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Array arr = vf != null ? vf.GetValue(c) as Array : null;
            if (arr != null && arr.Length > 0)
            {
                object v0 = arr.GetValue(0);
                cur = v0 is float ? ((float)v0).ToString("F3") : Convert.ToString(v0);
            }
            return string.Format("{0}/{1}：override={2} 值={3} values[0]={4}",
                moduleId, controllerName, on, val, cur);
        }
    }
}
