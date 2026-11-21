using System;
using System.Collections.Generic;
using JetForge.Core;
using UnityEngine;

namespace JetForge
{
    /// <summary>
    /// 进气道零件的程序化外形（partial：和发动机共用同一套回转面工具）。
    ///
    /// 以前进气道零件挂的是原版圆形进气道的 .mu——参数改了形状不变，账上算 0.7 喉道
    /// 而图里画的是一个固定模型，玩家看不出自己改了什么。这里改成按
    /// <see cref="IntakeShape"/> 当场生成：
    ///
    ///   气道内壁（朝轴心，喷管那套内壁材质）
    ///   唇口倒圆（把内壁前端和外罩前端连起来；钝唇/锐唇就差这一段）
    ///   外罩（朝外，冷端材质）
    ///   中心锥 / 斜板（锥体、斜板才有；斜板在回转体上只能画成"一圈板"）
    ///
    /// 型线一律来自 <see cref="IntakeGeometry.OfPart"/>，性能那边（<c>IntakeSolver.SolvePart</c>）
    /// 用的是同一组三块面积——所以"面板上把喉道比从 0.85 调到 0.70"这件事，
    /// 图、账、质量造价三处会同时动。
    /// </summary>
    public static partial class JetForgeMeshBuilder
    {
        /// <summary>进气道外形节点名。以 JetForge 开头，<see cref="HideOriginals"/> 才不会把它关掉。</summary>
        public const string IntakeVisualName = "JetForgeIntakeBody";

        /// <summary>型线 → 剖面（(y, r) 直接对应 Profile.Add）。</summary>
        private static Profile ProfileOf(List<IntakePoint> pts)
        {
            Profile p = new Profile();
            if (pts == null) return p;
            for (int i = 0; i < pts.Count; i++) p.Add((float)pts[i].Y, (float)pts[i].R);
            return p;
        }

        /// <summary>
        /// 唇口：内壁前端 → 外罩前端的一段圆弧。
        /// 半径就是 <c>LipRadius</c>：填 0.002 是接近刀口，填 0.05 是圆钝的亚声速唇。
        /// 这一段别省——没有它，内壁和外罩在前面是两个断开的圆环，从外面能看进去。
        /// </summary>
        private static Profile LipProfile(IntakeShape s)
        {
            Profile p = new Profile();
            double rIn = Math.Max(s.RCapture - s.LipRadius * 0.55, s.RThroat * 0.9);
            double rOut = s.RCapture + s.LipRadius;
            double lip = Math.Max(s.LipRadius, 0.002);
            // 半圆：从内缘经唇尖到外缘，唇尖在唇口平面上（YFront）
            const int N = 6;
            for (int i = 0; i <= N; i++)
            {
                double t = i / (double)N;                      // 0 = 内缘，1 = 外缘
                double a = Math.PI * t;                        // 0..π
                double r = (rIn + rOut) * 0.5
                           + (rIn - rOut) * 0.5 * Math.Cos(a) * -1.0;
                double y = s.YFront + Math.Sin(a) * lip;
                p.Add((float)y, (float)r);
            }
            return p;
        }

        /// <summary>
        /// 支撑支板：中心锥/斜板到外罩之间的几片径向板。
        ///
        /// 为什么只给锥体和斜板：超声速进气道那根锥（或那排斜板）得靠支板吊在流道里，
        /// 亚声速的皮托/轴对称进气道本来就没有中心体，凭空加几片板只会让人问"这板子撑着什么"。
        ///
        /// 板厚与弦长按零件长度缩放，所以把长度滑杆从 0.6 拖到 3 m，
        /// 支板不会变成两张比头发还薄的纸片。走机匣材质（脏金属），和内壁的衬里区分开。
        /// </summary>
        private static void AddStruts(MeshAcc acc, IntakeShape s, bool flip)
        {
            if (!s.HasCenterBody || s.CenterBody == null || s.CenterBody.Count < 2) return;
            if (s.Duct == null || s.Duct.Count < 2) return;

            double yStrut = s.YThroat - s.Length * 0.02;
            double hubR = IntakeGeometry.RadiusAt(s.CenterBody, yStrut);
            double wallR = IntakeGeometry.RadiusAt(s.Duct, yStrut);
            if (hubR <= 0.0 || wallR <= hubR * 1.05) return;

            int count = s.Kind == IntakeKind.Ramp ? 4 : 3;
            float yF = (float)(yStrut + s.Length * 0.09);
            float yB = (float)(yStrut - s.Length * 0.09);
            float half = (float)Math.Max(s.Length * 0.012, 0.004);   // 板厚的一半
            float r0 = (float)hubR, r1 = (float)wallR;
            float span = r1 - r0;
            float uSpan = span / UvWorldSize;
            float vSpan = Math.Max(yF - yB, 0.001f) / UvWorldSize;

            for (int i = 0; i < count; i++)
            {
                float a = (i + 0.5f) / count * Mathf.PI * 2f;
                Vector3 u = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));   // 径向
                Vector3 v = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));  // 周向

                Vector3 At(float r, float y, float side) => u * r + v * (half * side) + Vector3.up * y;

                Vector3 aFp = At(r0, yF, +1f), aFm = At(r0, yF, -1f);
                Vector3 aBp = At(r0, yB, +1f), aBm = At(r0, yB, -1f);
                Vector3 bFp = At(r1, yF, +1f), bFm = At(r1, yF, -1f);
                Vector3 bBp = At(r1, yB, +1f), bBm = At(r1, yB, -1f);

                Vector2 z = Vector2.zero;
                Vector2 u0 = new Vector2(0f, 0f), u1 = new Vector2(uSpan, 0f);
                Vector2 u2 = new Vector2(uSpan, vSpan), u3 = new Vector2(0f, vSpan);

                // 两面大板
                acc.AddQuad(aFp, bFp, bBp, aBp, v, v, v, v, u0, u1, u2, u3, SubCasing, flip);
                acc.AddQuad(aBm, bBm, bFm, aFm, -v, -v, -v, -v, u0, u1, u2, u3, SubCasing, flip);
                // 前缘、后缘、根部、尖部（薄边）
                acc.AddQuad(aFm, bFm, bFp, aFp, Vector3.up, Vector3.up, Vector3.up, Vector3.up,
                            z, z, z, z, SubCasing, flip);
                acc.AddQuad(aBp, bBp, bBm, aBm, Vector3.down, Vector3.down, Vector3.down, Vector3.down,
                            z, z, z, z, SubCasing, flip);
                acc.AddQuad(aBp, aFp, aFm, aBm, -u, -u, -u, -u, z, z, z, z, SubCasing, flip);
                acc.AddQuad(bFp, bBp, bBm, bFm, u, u, u, u, z, z, z, z, SubCasing, flip);
            }
        }

        /// <summary>按 Spec 生成进气道外形网格（不含材质）。</summary>
        public static Mesh BuildIntake(IntakeSpec spec, double faceDiameter, double length, bool flip = false)
        {
            // 有 3D 型线就按型线放样——那是玩家自己画的形状，不能让老的回转体重画一遍，
            // 否则"图上看着是扁口、性能算的是圆口"，图与账又对不上了。
            if (HasProfileShape(spec)) return BuildProfileIntake(spec, flip);

            IntakeShape s = IntakeGeometry.OfPart(spec, faceDiameter, length);
            MeshAcc acc = new MeshAcc();

            Profile duct = ProfileOf(s.Duct);
            if (duct.Points.Count >= 2) acc.Revolve(duct, Segments, SubInner, true, flip);

            Profile cowl = ProfileOf(s.Cowl);
            if (cowl.Points.Count >= 2) acc.Revolve(cowl, Segments, SubCold, false, flip);

            Profile lip = LipProfile(s);
            if (lip.Points.Count >= 2) acc.Revolve(lip, Segments, SubLip, false, flip);

            if (s.HasCenterBody)
            {
                Profile cb = ProfileOf(s.CenterBody);
                if (cb.Points.Count >= 2) acc.Revolve(cb, Segments, SubHot, false, flip);
            }

            AddStruts(acc, s, flip);

            return acc.ToMesh("JetForgeIntake");
        }

        /// <summary>
        /// 把外形挂到进气道零件上（做法与发动机那套一致：自己的节点、关掉原版渲染器）。
        /// 参数一变就重建，所以面板上拖滑杆是"边拖边长出来"。
        /// </summary>
        public static void RefreshIntakeMesh(Part part, IntakeSpec spec, double faceDiameter, double length)
        {
            if (part == null) return;
            try
            {
                Transform host = HostFor(part);
                if (host == null) return;
                Mesh mesh = BuildIntake(spec, faceDiameter, length);

                Transform t = host.Find(IntakeVisualName);
                if (t == null)
                {
                    Transform legacy = part.transform.Find(IntakeVisualName);
                    if (legacy != null) { legacy.SetParent(host, false); t = legacy; }
                }
                // 清掉同名残留：不然改一次参数多一层壳
                foreach (MeshFilter stray in part.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (stray != null && stray.gameObject.name == IntakeVisualName && (t == null || stray.transform != t))
                        UnityEngine.Object.Destroy(stray.gameObject);
                }

                GameObject go;
                if (t == null)
                {
                    go = new GameObject(IntakeVisualName);
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

                go.transform.SetParent(host, false);
                AlignToPart(go.transform, part, host);

                MeshFilter mf = go.GetComponent<MeshFilter>();
                if (mf == null) mf = go.AddComponent<MeshFilter>();
                if (mf.sharedMesh != null && mf.sharedMesh.name.StartsWith("JetForgeIntake"))
                    MeshRetire.Retire(mf.sharedMesh);
                mf.sharedMesh = mesh;
                // 与机身网格同一套归属章：ALT 复制/对称克隆出来的件会共用原件那份
                // Mesh 实例，名字里带上自己的 instanceID，自愈那边才判得出"这是借来的"。
                try { mesh.name = IntakeVisualName + "#" + part.GetInstanceID(); }
                // 归属章是 ALT 复制/对称件"整件透明"那次的修法本体：打不上就等于自愈瞎了
                catch (Exception e) { StampWarnOnce(e); }

                MeshRenderer mr2 = go.GetComponent<MeshRenderer>();
                if (mr2 == null) mr2 = go.AddComponent<MeshRenderer>();
                if (mr2.sharedMaterial == null) mr2.sharedMaterials = PickMaterials(part);

                HideOriginals(part);
            }
            catch (Exception e)
            {
                // 同机身那条：原版模型已经被 HideOriginals 关掉，建失败就必须放回来，
                // 否则零件是隐形的——看不见的东西没法判断是装错了还是模组坏了。
                Debug.LogError("[JetForge] 进气道外形生成失败：" + e.Message);
                try { JetForgeMeshBuilder.ShowOriginals(part); }
                catch (Exception e2) { Debug.LogError("[JetForge] 放回原版模型也失败了：" + e2.Message); }
            }
        }

    }
}