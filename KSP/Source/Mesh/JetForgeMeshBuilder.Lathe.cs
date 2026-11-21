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
        // ---------- 回转与合并 ----------

        private sealed class MeshAcc
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            /// <summary>三套三角：0 冷端、1 热端、2 喷管内壁。分开是为了让它们各用一张贴图。</summary>
            public readonly List<int>[] Subs =
            {
                new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>()
            };

            /// <summary>
            /// 绕零件的 Y 轴回转。剖面点 (y, r)：y 是轴向位置，r 是半径。
            /// 顶点 = (r·cosθ, y, r·sinθ)；法线取剖面切线的垂直方向再绕轴展开，
            /// 这样收扩喷管那种大曲率处的着色才不会翻。
            /// </summary>
            /// <param name="inward">
            /// true = 法线朝轴心（喷管内壁）。内壁和外壁是同一条剖面的两面，
            /// 不能靠"把外壁也画一遍"糊过去：那样从喷口看进去还是空的。
            /// </param>
            /// <param name="flip">
            /// true = 整体翻转绕序。给对称模式（镜像零件）用：镜像变换的行列式是负的，
            /// 投影后三角形绕序会被翻过来，Unity 就按背面剔掉了——表现是"对称出来的那台看不见"。
            /// </param>
            public void Revolve(Profile p, int segments, int submesh = SubCold, bool inward = false, bool flip = false)
            {
                int n = p.Points.Count;
                if (n < 2) return;
                int start = V.Count;
                List<int> T = Subs[submesh];
                // 法线朝内 ⇔ 绕序反着来；镜像零件同理。两者叠加等于不翻。
                bool rev = inward ^ flip;

                // 弧长参数化 UV.v，长段上的贴图才不会被拉花
                float[] arc = new float[n];
                for (int i = 1; i < n; i++) arc[i] = arc[i - 1] + Vector2.Distance(p.Points[i - 1], p.Points[i]);

                // 周向用平均半径算周长，贴图密度才与真实尺寸一致
                float rSum = 0f;
                for (int i = 0; i < n; i++) rSum += p.Points[i].y;
                float circum = 2f * Mathf.PI * Mathf.Max(rSum / n, 0.01f);

                // 2D 剖面法线（切线旋转 90°），朝外为正
                Vector2[] nrm = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    Vector2 t;
                    if (i == 0) t = p.Points[1] - p.Points[0];
                    else if (i == n - 1) t = p.Points[n - 1] - p.Points[n - 2];
                    else t = p.Points[i + 1] - p.Points[i - 1];
                    // 剖面 (y, r)：切线 (dy, dr)，外侧法线 = (dr, -dy) 归一化。
                    // 注意这个 2D 法线是 (轴向分量, 径向分量)——绕轴展开时径向要走 ca/sa，
                    // 轴向直接当 Y。接反了整台机器的法线会变成"顺着轴线指"，光照平得像块板。
                    Vector2 nn = new Vector2(t.y, -t.x);
                    nrm[i] = nn.sqrMagnitude > 1e-8f ? nn.normalized : new Vector2(0f, 1f);
                    if (inward) nrm[i] = -nrm[i];
                }

                for (int i = 0; i < n; i++)
                {
                    Vector2 pt = p.Points[i];
                    float r = pt.y;                 // 注意：Profile.Add(y, r) → Vector2(y, r)
                    for (int s = 0; s <= segments; s++)
                    {
                        float a = s / (float)segments * Mathf.PI * 2f;
                        float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                        V.Add(new Vector3(r * ca, pt.x, r * sa));
                        Uv.Add(new Vector2(s / (float)segments * (circum / UvWorldSize), arc[i] / UvWorldSize));
                        // nrm.x 是轴向分量、nrm.y 是径向分量（上面那行注释），别接反
                        N.Add(new Vector3(nrm[i].y * ca, nrm[i].x, nrm[i].y * sa));
                    }
                }

                for (int i = 0; i < n - 1; i++)
                    for (int s = 0; s < segments; s++)
                    {
                        int a0 = start + i * (segments + 1) + s;
                        int a1 = a0 + 1;
                        int b0 = a0 + (segments + 1);
                        int b1 = b0 + 1;
                        // 绕序必须与法线同向（Unity 正面 = 顺时针）。
                        // 剖面是从前往后走的，轴向 y 递减；用 (a0,b0,a1) 那种顺序算出来的
                        // 几何法线指向轴心，整台机器就成了内翻的壳——从外面看是透明的，
                        // 只能看见对面内壁。这正是"一半面片消失、转视角换另一半"的原因。
                        if (!rev)
                        {
                            T.Add(a0); T.Add(a1); T.Add(b0);
                            T.Add(a1); T.Add(b1); T.Add(b0);
                        }
                        else
                        {
                            // 每个三角按反序写一遍就是反面朝上
                            T.Add(a0); T.Add(b0); T.Add(a1);
                            T.Add(a1); T.Add(b0); T.Add(b1);
                        }
                    }
            }

            /// <summary>
            /// 加一片四边形（花瓣式喷口用）。绕序按"正面朝外"给，传进来的四个角
            /// 要按顺时针排列（从外面看）。
            /// </summary>
            public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                                Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd,
                                Vector2 uva, Vector2 uvb, Vector2 uvc, Vector2 uvd,
                                int submesh, bool flip = false)
            {
                int s = V.Count;
                V.Add(a); V.Add(b); V.Add(c); V.Add(d);
                N.Add(na); N.Add(nb); N.Add(nc); N.Add(nd);
                Uv.Add(uva); Uv.Add(uvb); Uv.Add(uvc); Uv.Add(uvd);
                List<int> T = Subs[submesh];
                if (!flip)
                {
                    T.Add(s); T.Add(s + 1); T.Add(s + 2);
                    T.Add(s); T.Add(s + 2); T.Add(s + 3);
                }
                else
                {
                    T.Add(s); T.Add(s + 2); T.Add(s + 1);
                    T.Add(s); T.Add(s + 3); T.Add(s + 2);
                }
            }

            public Mesh ToMesh(string name)
            {
                Mesh m = new Mesh();
                m.name = name;

                // 绕序对账：凡是几何法线与顶点法线反向的三角，交换两个顶点把它掰正。
                //
                // 为什么要在出口处统一做一次，而不是指望每个生成器都写对：
                // 回转体的绕序方向取决于"剖面是从前往后走还是从后往前走"，四边形片
                // 又另外有一套手推的角序。这两套我一共写错过三次（816 个透明面片、
                // 402 个反法线），每次症状都只是"看着有点暗"或者"半边没了"。
                // 让出口这一道按几何自己说话，就不必再靠人记住每个生成器的手性。
                for (int s = 0; s < Subs.Length; s++)
                {
                    List<int> tris = Subs[s];
                    for (int i = 0; i + 2 < tris.Count; i += 3)
                    {
                        int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                        Vector3 g = Vector3.Cross(V[i1] - V[i0], V[i2] - V[i0]);
                        Vector3 sm = N[i0] + N[i1] + N[i2];
                        if (g.sqrMagnitude < 1e-12f || sm.sqrMagnitude < 1e-12f) continue;
                        if (Vector3.Dot(g, sm) < 0f)
                        {
                            tris[i + 1] = i2;
                            tris[i + 2] = i1;
                            WindingFixes++;
                        }
                    }
                }

                // 双面：每个子网格再挂一遍反向副本（顶点复制、法线取反）。
                //
                // 为什么宁可多一倍三角：Unity 对"负缩放变换"（对称件的镜像副本）到底
                // 会不会自动翻绕序，这件事没有可靠文档，而我在这上面已经让玩家看到过
                // 两次"面片透明/里外翻"。花瓣本来就是单层薄片，单面永远有一侧看不见。
                // 双面之后，无论镜像件怎么变换、无论从哪边看，都不会再出现透底。
                // 代价：顶点与三角各翻一倍（3 千 → 6 千），对这个尺寸的零件无关紧要。
                int vBase = V.Count;
                for (int i = 0; i < vBase; i++)
                {
                    V.Add(V[i]);
                    N.Add(-N[i]);
                    Uv.Add(Uv[i]);
                }
                for (int s = 0; s < Subs.Length; s++)
                {
                    List<int> tris = Subs[s];
                    int cnt = tris.Count;
                    for (int i = 0; i < cnt; i += 3)
                    {
                        tris.Add(tris[i] + vBase);
                        tris.Add(tris[i + 2] + vBase);
                        tris.Add(tris[i + 1] + vBase);
                    }
                }

                m.SetVertices(V);
                m.SetNormals(N);
                m.SetUVs(0, Uv);
                // 槽位顺序必须与 PickMaterials 返回的材质数组一一对应（槽 0 冷端 … 槽 4 唇口）。
                // 末尾空槽直接不发：子网格数与材质数不一致在 Unity 里不报错，只是静默少画一块，
                // 而这种"少画一块"在机库里看就是某个部件神秘失踪——所以宁可少发槽，也不留空槽错位。
                int subs = Subs.Length;
                while (subs > 1 && Subs[subs - 1].Count == 0) subs--;
                m.subMeshCount = subs;
                for (int s = 0; s < subs; s++) m.SetTriangles(Subs[s], s);
                // 不要调 RecalculateNormals：那会把剖面拐角处的法线按面片平均掉，
                // 收扩喷管的大曲率就糊了。法线是我们自己按剖面切线算好的（永远朝外）。
                // 法线贴图要靠切线空间，没有切线只能用 UV 导数硬算，边缘会发脏
                m.RecalculateTangents();
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
