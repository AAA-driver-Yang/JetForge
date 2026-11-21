using System;
using System.Collections.Generic;
using System.Text;
using JetForge.Core;
using UnityEngine;

namespace JetForge
{
    // partial：网格构建按"算外形 / 画剖面 / 回转合并 / 对外接口 / 装到零件上"分家。
    // 提到这个文件里的方法分不出来——它们共用同一份剖面线与量纲，
    // 拆的只是行数，不是职责边界。
    public static partial class JetForgeMeshBuilder
    {
        // ---------- 装到零件上 ----------

        private const string VisualName = "JetForgeBody";

        /// <summary>
        /// 我们自己那些节点该挂在哪个父物体下。
        ///
        /// 必须是 part.transform 里那个叫 "model" 的子物体，**不能**直接挂 part.transform。
        /// Part.FindModelTransform 的实现（反射读到的 IL）是：
        ///     partTransform.Find("model")  →  在它的子树里按名字搜
        /// 挂在 model 外面的节点，它一辈子找不到。于是长出两个症状：
        ///   · RefreshPartMesh 每次刷新都以为"还没有"，新建一个 JetForgeBody——
        ///     旧的那台还立在原地，机库里就是两台发动机叠着；
        ///   · Waterfall 的 overrideParentTransform="JetForgeThrust" 同样找不到，
        ///     羽流父节点是 null，"模块挂上了但什么都没画"。
        /// 一个父节点挂错，两个完全不相干的报障。这种错最费时间。
        /// </summary>
        /// <summary>
        /// 把一个我们自己造的节点摆到"零件坐标系"的原点上，位置/朝向/缩放都由 part.transform 决定。
        ///
        /// 父节点是原版 .mu 实例化出来的 `model` 物体，它不一定是单位变换
        /// （KSP 给导入模型套的那层常带旋转/缩放）。以前我们写的是
        /// `localPosition=0, localRotation=identity`，等于把外形交给爸妈的坐标系去解释：
        /// 单看一台看不出来，一开对称（负缩放）就整台前后错开。
        /// </summary>
        private static void AlignToPart(Transform t, Part part, Transform host)
        {
            t.SetParent(host, false);
            t.localRotation = Quaternion.Inverse(host.rotation) * part.transform.rotation;
            t.localPosition = host.InverseTransformPoint(part.transform.position);
            Vector3 hs = host.lossyScale;
            t.localScale = new Vector3(
                1f / Mathf.Max(Mathf.Abs(hs.x), 1e-4f),
                1f / Mathf.Max(Mathf.Abs(hs.y), 1e-4f),
                1f / Mathf.Max(Mathf.Abs(hs.z), 1e-4f));
        }

        public static Transform HostFor(Part part)
        {
            if (part == null) return null;
            Transform t = part.transform.Find("model");
            return t != null ? t : part.transform;
        }

        /// <summary>
        /// 把生成的网格挂到零件上。做法是新建一个子物体放我们自己的 MeshRenderer，
        /// 并把原版的渲染器关掉——这样随时可以切回去（比如调试原版贴图）。
        /// </summary>
        public static void RefreshPartMesh(Part part, EngineDesignData design)
        {
            if (part == null || design == null) return;
            try
            {
                Transform host = HostFor(part);
                // 不再按"镜像与否"翻绕序：网格是双面的，翻不翻都能看见。
                // 之前那条路让玩家在对称件上看到里外翻——Unity 对负缩放的绕序约定
                // 没有可靠文档，与其猜，不如两面都挂。
                Mesh mesh = Build(design);

                // 先找旧的。挂在 part.transform 下的老版本节点顺手搬进来，
                // 再清掉同名但非本体的残留——不清理就是"更新一次多一台"。
                Transform t = host.Find(VisualName);
                if (t == null)
                {
                    Transform legacy = part.transform.Find(VisualName);
                    if (legacy != null) { legacy.SetParent(host, false); t = legacy; }
                }
                foreach (MeshFilter stray in part.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (stray != null && stray.gameObject.name == VisualName && (t == null || stray.transform != t))
                        UnityEngine.Object.Destroy(stray.gameObject);
                }

                GameObject go;
                if (t == null)
                {
                    go = new GameObject(VisualName);
                    go.transform.SetParent(host, false);
                    go.AddComponent<MeshFilter>();
                    MeshRenderer mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = PickMaterials(part);
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
                else
                {
                    go = t.gameObject;
                }
                // 不管新旧都归位，而且是**归到零件原点**，不是归到父节点的"零位"。
                //
                // 父节点是原版 .mu 实例化出来的 `model` 物体，它不一定是个单位变换
                // （KSP 给导入模型套的那层常带旋转/缩放）。以前这里写的是
                // `localPosition=0, localRotation=identity`，等于把我们 3 m 长的外形
                // 交给爸妈的坐标系去解释——原地看不出来，一开对称（负缩放）就整台
                // 前后错开。改成先求"零件坐标系"再换算回父节点，模型就必然
                // 正好长在零件原点上，谁当爹都一样。
                go.transform.SetParent(host, false);
                AlignToPart(go.transform, part, host);

                MeshFilter mf = go.GetComponent<MeshFilter>();
                if (mf == null) mf = go.AddComponent<MeshFilter>();
                if (mf.sharedMesh != null && mf.sharedMesh.name.StartsWith("JetForgeBody"))
                        MeshRetire.Retire(mf.sharedMesh);
                mf.sharedMesh = mesh;
                // 给这份网格盖个章："这是哪一件的"。
                //
                // ALT 复制 / 对称放置出来的是**克隆零件**，克隆的那一份 MeshFilter 直接
                // 引用了原来那件的 Mesh 实例（同一个对象）。于是"我这份外形其实是别人的"
                // 这件事在场景里没有任何痕迹可查——以前只能靠重建一遍碰运气。
                // 名字里带上自己的 instanceID 之后，谁在借谁的网格一眼就能判出来，
                // OnUpdate 里那个自愈就能准确地在"借来的"时候重建一份自己的。
                try { mesh.name = VisualName + "#" + part.GetInstanceID(); }
                // 归属章打不上 = 自愈判不出"这份网格是借来的"，ALT 复制后可能整件透明
                catch (System.Exception e) { StampWarnOnce(e); }
                MeshRenderer mr2 = go.GetComponent<MeshRenderer>();
                if (mr2 == null) mr2 = go.AddComponent<MeshRenderer>();
                if (mr2.sharedMaterial == null) mr2.sharedMaterials = PickMaterials(part);

                // 每次刷新都关一遍原版模型：编辑器里重新进场景时它会自己启用回来
                HideOriginals(part);

                EnsureThrustTransform(part, design);

                // 可变喷管的花瓣是独立网格：机库里先给个半开的样子，飞行中由模块按开度重建
                UpdatePetals(part, design, 0.55f);

                // 每一份外形（含对称复制出来的那些）留一行：哪个零件、缩放多少、
                // 是不是镜像件、几个顶点。对称模式出问题时，这行是唯一能分辨
                // "镜像件根本没建" 与 "建了但朝向不对" 的东西。
                Debug.Log(string.Format("[JetForge] 外形 {0}#{1} {2}｜顶点 {3} 三角 {4}｜网格 y {5:F2}~{6:F2}｜零件世界位置 {7:F2}/{8:F2}/{9:F2}｜父节点 {10}",
                    part.partInfo != null ? part.partInfo.name : "?",
                    part.flightID, DescribeSymmetry(part),
                    mesh.vertexCount, mesh.triangles.Length / 3,
                    mesh.bounds.min.y, mesh.bounds.max.y,
                    part.transform.position.x, part.transform.position.y, part.transform.position.z,
                    (go.transform.parent != null ? go.transform.parent.name : "(无)")
                        + (go.transform.parent != null && go.transform.parent.parent != null
                           ? "<" + go.transform.parent.parent.name + ">" : "")
                        + string.Format(" 父变换 位移{0} 旋转{1} 缩放{2}｜本节点世界旋转{3}",
                            host.localPosition.ToString("F2"),
                            host.localEulerAngles.ToString("F0"),
                            host.lossyScale.ToString("F2"),
                            go.transform.eulerAngles.ToString("F0"))));
            }
            catch (Exception ex)
            {
                // 生成到一半失败时，原版渲染器已经被 HideOriginals 关掉了：不把它放回来，
                // 玩家看到的是一块**隐形的零件**——比看见占位模型糟得多，因为看不见就没法判断
                // 是自己装错了还是模组坏了。ShowOriginals 就是为这一刻写的，之前没人调用它。
                Debug.LogError("[JetForge] 生成外形失败：" + ex);
                try { ShowOriginals(part); }
                catch (Exception e2) { Debug.LogError("[JetForge] 放回原版模型也失败了：" + e2.Message); }
            }
        }

        /// <summary>
        /// 施力节点名。故意不叫 "thrustTransform"。
        ///
        /// 原版 OnLoad 时用 part.FindModelTransforms("thrustTransform") 填 thrustTransforms，
        /// 会深搜整个模型层级——cfg 占位模型（stock turboFanSize1）里那个喷口节点就叫这个名字。
        /// 我们把自己的节点也起同名的话，自检、日志、FindModelTransform 都分不清抓到的是谁的，
        /// 上一轮就是这么把力写成 45° 斜的（改了别人的节点，而别人的父节点带旋转）。
        /// 换个唯一名字，自己的东西自己认。
        /// </summary>
        public const string ThrustTransformName = "JetForgeThrust";

        /// <summary>
        /// 造一个名叫 thrustTransform 的空物体，放在喷口中心、朝后。
        ///
        /// 这不是装饰。原版 ModuleEngines.ThrustUpdate 里施力那一句是：
        ///     part.AddForceAtPosition(-t.forward * finalThrust * mult, t.position)
        /// 而 t 来自 thrustTransforms —— 那个列表是 **ModuleEngines.OnLoad 时**
        /// 用 part.FindModelTransforms("thrustTransform") 填的。
        /// 我们的外形是运行时才生成的，OnLoad 那一刻零件上什么都没有，
        /// 所以列表是空的、循环一次都不跑、推力一分都施不出去。
        /// 数字全对，飞机不动。
        /// </summary>
        public static Transform EnsureThrustTransform(Part part, EngineDesignData design)
        {
            if (part == null || design == null) return null;
            // 只认零件根下的直接子物体（就是我们自己造的那个）。
            // 以前用的是 part.FindModelTransform("thrustTransform")，它会往模型层级里深搜，
            // cfg 占位模型（stock turboFanSize1）里的喷口节点也叫这个名字，于是抓到的是别人的。
            // 那个节点的父节点带 45° 旋转，localPosition/localRotation 全被带着歪：
            // 飞行日志里 thrustTransform 在零件坐标的 forward 是 (0, 0.71, 0.71)，
            // 力被劈成"向下 + 向后"，飞机被推着倒走、还往地面里压。
            Transform host = HostFor(part);
            Transform t = host.Find(ThrustTransformName);
            if (t == null)
            {
                // 老存档里这个节点在我们改父节点之前挂在 part.transform 下，搬进来而不是再造一个
                Transform legacy = part.transform.Find(ThrustTransformName);
                if (legacy != null) { legacy.SetParent(host, false); t = legacy; }
            }
            if (t == null)
            {
                GameObject go = new GameObject(ThrustTransformName);
                go.transform.SetParent(host, false);
                t = go.transform;
            }
            // 用世界坐标落位：父节点（model）自己万一带偏移或旋转，pid 算出来的 local 就会歪。
            // 喷口在 -Y 方向出气 → 推力朝 +Y。原版施的是 -forward，所以 forward 必须朝 -Y。
            t.position = part.transform.TransformPoint(new Vector3(0f, NozzleExitY(design), 0f));
            t.rotation = part.transform.rotation * Quaternion.FromToRotation(Vector3.forward, Vector3.down);
            t.localScale = Vector3.one;
            // 留一行证据：零件坐标里的 forward 必须是 (0,-1,0)，否则力的方向就是歪的。
            Vector3 fwdLocal = part.transform.InverseTransformDirection(t.forward);
            if (fwdLocal.y > -0.95f || Mathf.Abs(fwdLocal.x) > 0.1f || Mathf.Abs(fwdLocal.z) > 0.1f)
                Debug.LogWarning(string.Format(
                    "[JetForge] 施力朝向不对：零件坐标 forward={0:F2}/{1:F2}/{2:F2}（应为 0/-1/0），父节点={3}",
                    fwdLocal.x, fwdLocal.y, fwdLocal.z, t.parent == null ? "(无)" : t.parent.name));
            return t;
        }

        /// <summary>
        /// 把施力朝向设成给定的**世界坐标推力方向**。
        ///
        /// 原版施的是 `-t.forward`，所以想让推力朝 dir，就得让 forward 朝 -dir。
        /// 不偏转时传 `part.transform.up`（喷口在 -Y、推力朝 +Y），结果与
        /// <see cref="EnsureThrustTransform"/> 里的基准姿态逐位相同。
        /// </summary>
        public static void SetThrustDirection(Transform t, Vector3 worldForceDir)
        {
            if (t == null) return;
            if (worldForceDir.sqrMagnitude < 1e-8f) return;
            t.rotation = Quaternion.FromToRotation(Vector3.forward, -worldForceDir.normalized);
        }
    }
}
