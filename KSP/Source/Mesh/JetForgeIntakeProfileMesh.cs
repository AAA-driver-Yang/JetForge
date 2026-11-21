using System;
using System.Collections.Generic;
using JetForge.Core;
using UnityEngine;

namespace JetForge
{
    /// <summary>
    /// 按 **3D 型线**（<see cref="IntakeProfile"/>）生成进气道外形。
    ///
    /// 与 <c>JetForgeIntakeMesh.BuildIntake</c> 的老路子（回转体：内壁/外罩/唇口/中心锥四条剖面各自 Revolve）
    /// 的区别：老路子只能画圆，而且偏心、扁口、S 弯这三件事一件事都画不出来。
    /// 这里改成"沿轴取一串截面 → 逐段放样"：
    ///
    ///   内壁面 = 截面序列本身（法线朝轴心，衬里材质）
    ///   外罩面 = 每个截面按壁厚往外长（法线朝外，冷端材质）
    ///   唇口   = 第一对内/外环之间补一段圆角（不然从外面能看进气道的空腔）
    ///   后端面 = 最后一对内/外环之间的环形端面（不然零件是漏的）
    ///   中心体 = 锥/柱，走老的回转体工具（它本来就只需要一根圆剖面）
    ///
    /// 局部坐标沿用 KSP 零件约定：**Y 是串接轴**，X/Z 是横截面。
    /// 所以型线的 `X`（轴向）→ 局部 Y，`OffsetZ`/`Width` → 局部 X，`OffsetY`/`Height` → 局部 Z。
    /// 这一层映射必须和面板上的坐标提示一致，否则玩家拖的是上下、长出来的是左右。
    /// </summary>
    public static partial class JetForgeMeshBuilder
    {
        /// <summary>周向分段。28 段在 0.6 m 口径上弦高约 1 mm，肉眼已经看不出棱。</summary>
        private const int ProfileSegments = 28;

        /// <summary>沿轴采样段数（含两端）。型线只有 2 个截面时，靠它把直筒也切成可贴图的小段。</summary>
        private const int ProfileSteps = 14;

        /// <summary>世界平铺长度（米/UV 单位），与贴图生成器里的 TEX_WORLD 一致。</summary>
        private const float ProfileTexWorld = 0.5f;

        /// <summary>有没有型线；没有就退回老的回转体外形。判据与 <see cref="JetForge.Core.Intake.HasProfile"/> 同一把尺，
        /// 否则"性能说没型线、网格说有"，会出现指哪打哪对不上的外形。</summary>
        public static bool HasProfileShape(IntakeSpec spec)
        {
            return JetForge.Core.Intake.HasProfile(spec);
        }

        /// <summary>按型线生成进气道网格。</summary>
        public static Mesh BuildProfileIntake(IntakeSpec spec, bool flip = false)
        {
            IntakeProfile p = spec.Shape;
            MeshAcc acc = new MeshAcc();

            List<IntakeSection> rings = SampleRings(p);
            Loft(acc, rings, false, SubInner, true, flip);    // 内壁（法线朝轴心）
            Loft(acc, rings, true, SubCold, false, flip);     // 外罩（往外长一个壁厚）
            Lip(acc, rings, flip);                            // 唇口圆角
            Cap(acc, rings, flip);                            // 后端环形端面

            if (p.HasSpinner) Spinner(acc, p, rings, flip);

            // 型线坐标 → 零件坐标的最后一刀：型线的 X 是"从唇口往机尾量"的距离，
            // 而零件坐标系里 **唇口在 +Y、发动机面在 -Y**（老回转体外形就是这么摆的：
            // `IntakeGeometry.OfPart` 里 YFront = +L/2、YFace = -L/2）。
            //
            // 这一刀之前，放样出来的气道是"唇口在原点、往 +Y 长"，于是整件东西沿轴镜像了：
            // 外壳近乎对称看不出来，**只有中心锥看得出来**——锥尖本该朝前，结果朝着机尾。
            // 这个 bug 的外观就是"加了个气动锥体，它反了"，很容易被当成建锥的代码写错，
            // 其实是整条链的轴向约定不一致。所以修在最后一步：整块镜像 + 平移，一次改干净。
            MirrorToPartSpace(acc, rings[0].X, rings[rings.Count - 1].X);

            return acc.ToMesh("JetForgeIntake");
        }

        /// <summary>
        /// 把"唇口在原点、往 +Y 长"的型线空间，搬成零件空间（唇口 +L/2、发动机面 −L/2）。
        ///
        /// 镜像会翻转三角形绕序，所以这里必须把每个三角的后两个顶点换位，
        /// 否则整件东西背面朝外——在 KSP 里表现为"零件是半透明的、能看见内壁"。
        /// </summary>
        private static void MirrorToPartSpace(MeshAcc acc, double x0, double x1)
        {
            float mid = (float)(0.5 * (x0 + x1));
            for (int i = 0; i < acc.V.Count; i++)
            {
                Vector3 v = acc.V[i];
                acc.V[i] = new Vector3(v.x, mid - v.y, v.z);
                Vector3 n = acc.N[i];
                acc.N[i] = new Vector3(n.x, -n.y, n.z);
            }
            for (int s = 0; s < acc.Subs.Length; s++)
            {
                List<int> t = acc.Subs[s];
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    int tmp = t[k + 1];
                    t[k + 1] = t[k + 2];
                    t[k + 2] = tmp;
                }
            }
        }

        /// <summary>型线 → 沿轴等距采样的一串截面。</summary>
        private static List<IntakeSection> SampleRings(IntakeProfile p)
        {
            List<IntakeSection> rings = new List<IntakeSection>();
            double x0 = p.Sections[0].X;
            double x1 = p.Sections[p.Sections.Count - 1].X;
            int steps = Math.Max(ProfileSteps, p.Sections.Count * 2);
            for (int i = 0; i <= steps; i++)
            {
                double t = i / (double)steps;
                rings.Add(p.Sample(x0 + (x1 - x0) * t));
            }
            return rings;
        }

        /// <summary>
        /// 截面 → 一圈顶点。超椭圆参数化：|cosθ|^(2/n)、|sinθ|^(2/n)，
        /// n=2 退化成正圆/椭圆（2/n=1 正好是 cosθ、sinθ），所以一条公式管三种形状。
        /// </summary>
        private static Vector3[] RingPoints(IntakeSection s, bool outer, int m)
        {
            Vector3[] v = new Vector3[m];
            double grow = outer ? s.EffectiveWall : 0.0;
            IntakeSection sh = s;
            if (outer)
            {
                // 外罩 = 同一个截面按壁厚往外长（宽高各长两个壁厚），参数化仍然走 OutlinePoint
                sh = s.Clone();
                sh.Width = Math.Max(s.Width + 2.0 * grow, 1e-3);
                sh.Height = Math.Max(s.Height + 2.0 * grow, 1e-3);
            }
            for (int j = 0; j < m; j++)
            {
                double t = 2.0 * Math.PI * j / m;
                double u, w;
                sh.OutlinePoint(t, out u, out w);
                // 截面倾角：把这一圈歪成一个斜面——局部横向 u、竖向 w 越远，沿轴挪得越多。
                // 挪的量由 `IntakeSection.AxialShift` 算（那是 Core 里能被回归钉住的那份公式）。
                double dx = s.AxialShift(u, w);
                // 局部：Y 是轴，X 是横向（型线的 Z），Z 是上下（型线的 Y）
                v[j] = new Vector3((float)(s.OffsetZ + u), (float)(s.X + dx), (float)(s.OffsetY + w));
            }
            return v;
        }

        /// <summary>
        /// 这一环在 (X,Z) 平面里是不是逆时针（鞋带公式）。局部 X 是横向、Z 是上下、Y 是轴。
        /// </summary>
        private static bool RingIsCcw(Vector3[] ring)
        {
            if (ring == null || ring.Length < 3) return true;
            double sum = 0.0;
            for (int j = 0; j < ring.Length; j++)
            {
                Vector3 a = ring[j];
                Vector3 b = ring[(j + 1) % ring.Length];
                sum += (double)a.x * b.z - (double)b.x * a.z;
            }
            return sum >= 0.0;
        }

        /// <summary>
        /// 放样：把相邻两环之间连成一圈四边形。
        /// 法线用"相邻环之间求差分再叉乘"并做周向平均——直接按截面的法向近似在收扩处会翻面，
        /// 尤其是超椭圆拐角那几段。
        /// </summary>
        private static void Loft(MeshAcc acc, List<IntakeSection> rings, bool outer, int submesh, bool inward, bool flip)
        {
            if (rings == null || rings.Count < 2) return;
            int n = rings.Count, m = ProfileSegments;

            Vector3[][] grid = new Vector3[n][];
            for (int i = 0; i < n; i++) grid[i] = RingPoints(rings[i], outer, m);

            // 绕向保险：法线用"轴向差 × 周向差"求，三角用固定绕序连——两者都默认顶点是逆时针的。
            // 玩家在 2D 页里把某一段的两个顶点拖过界，绕向就会翻过来（形状一样、面全朝里），
            // 所以我们按环 0 实际算一遍：顺时针就把法线和绕序都反过来，而不是指望上层永远摆得对。
            bool ringCcw = RingIsCcw(grid[0]);

            // 逐点法线：取轴向差与周向差的叉乘，朝外为正
            Vector3[][] nrm = new Vector3[n][];
            for (int i = 0; i < n; i++) nrm[i] = new Vector3[m];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < m; j++)
                {
                    int jp = (j + 1) % m, jm = (j + m - 1) % m;
                    Vector3 du = grid[Math.Min(i + 1, n - 1)][j] - grid[Math.Max(i - 1, 0)][j];   // 轴向
                    Vector3 dv = grid[i][jp] - grid[i][jm];                                      // 周向
                    Vector3 nn = Vector3.Cross(dv, du);                                          // 朝外
                    if (!ringCcw) nn = -nn;
                    if (nn.sqrMagnitude < 1e-12f) nn = new Vector3(0f, 0f, 1f);
                    nrm[i][j] = nn.normalized;
                }
            }

            int start = acc.V.Count;
            float circum = 0f;
            for (int i = 0; i < n; i++) circum += (float)rings[i].Perimeter;
            circum = Mathf.Max(circum / n, 0.01f);

            for (int i = 0; i < n; i++)
            {
                float v = (float)((rings[i].X - rings[0].X) / ProfileTexWorld);
                for (int j = 0; j < m; j++)
                {
                    Vector3 pv = grid[i][j];
                    acc.V.Add(pv);
                    Vector3 nv = inward ? -nrm[i][j] : nrm[i][j];
                    acc.N.Add(nv);
                    float u = (float)j / m * (circum / ProfileTexWorld);
                    acc.Uv.Add(new Vector2(u, v));
                }
            }

            List<int> T = acc.Subs[submesh];
            bool rev = (inward ^ flip) ^ !ringCcw;
            for (int i = 0; i < n - 1; i++)
            {
                for (int j = 0; j < m; j++)
                {
                    int jp = (j + 1) % m;
                    int a0 = start + i * m + j;
                    int a1 = start + i * m + jp;
                    int b0 = start + (i + 1) * m + j;
                    int b1 = start + (i + 1) * m + jp;
                    if (!rev)
                    {
                        T.Add(a0); T.Add(b0); T.Add(b1);
                        T.Add(a0); T.Add(b1); T.Add(a1);
                    }
                    else
                    {
                        T.Add(a0); T.Add(b1); T.Add(b0);
                        T.Add(a0); T.Add(a1); T.Add(b1);
                    }
                }
            }
        }

        /// <summary>
        /// 唇口：第一对内环与外环之间补一段四分之一圆，从内缘翻到外缘。
        /// 半径取该截面的壁厚——壁厚的物理含义就是"唇口有多厚"，薄壁即锐唇。
        /// </summary>
        private static void Lip(MeshAcc acc, List<IntakeSection> rings, bool flip)
        {
            if (rings == null || rings.Count == 0) return;
            IntakeSection s = rings[0];
            double wall = s.EffectiveWall;
            int m = ProfileSegments;
            const int K = 4;                                  // 圆角本身分几圈

            // 每一圈：截面按壁厚往外长 wall·sin(t)，同时唇尖内收 wall·(1-cos(t))。
            // 这两个量必须一起用——只长不内收，唇口就是一圈台阶；只内收不长，唇口没厚度。
            Vector3[][] band = new Vector3[K + 1][];
            for (int k = 0; k <= K; k++)
            {
                double t = k / (double)K;                     // 0 = 内缘，1 = 外缘
                double ang = t * Math.PI * 0.5;
                IntakeSection step = s.Clone();
                double grow = wall * Math.Sin(ang);
                step.Width = Math.Max(s.Width + 2.0 * grow, 1e-3);
                step.Height = Math.Max(s.Height + 2.0 * grow, 1e-3);
                step.X = s.X - wall * (1.0 - Math.Cos(ang));
                band[k] = RingPoints(step, false, m);
            }

            int start = acc.V.Count;
            for (int k = 0; k <= K; k++)
            {
                for (int j = 0; j < m; j++)
                {
                    int jp = (j + 1) % m, jm = (j + m - 1) % m;
                    int kp = Math.Min(k + 1, K), km = Math.Max(k - 1, 0);
                    Vector3 du = band[kp][j] - band[km][j];      // 唇口展开方向
                    Vector3 dv = band[k][jp] - band[k][jm];      // 周向
                    Vector3 nn = Vector3.Cross(dv, du);
                    if (nn.sqrMagnitude < 1e-12f) nn = new Vector3(0f, 1f, 0f);
                    acc.V.Add(band[k][j]);
                    acc.N.Add(nn.normalized);
                    acc.Uv.Add(new Vector2((float)j / m, (float)k / K));
                }
            }

            List<int> T = acc.Subs[SubLip];
            bool rev = flip;
            for (int k = 0; k < K; k++)
            {
                for (int j = 0; j < m; j++)
                {
                    int jp = (j + 1) % m;
                    int a0 = start + k * m + j;
                    int a1 = start + k * m + jp;
                    int b0 = start + (k + 1) * m + j;
                    int b1 = start + (k + 1) * m + jp;
                    if (!rev)
                    {
                        T.Add(a0); T.Add(b0); T.Add(b1);
                        T.Add(a0); T.Add(b1); T.Add(a1);
                    }
                    else
                    {
                        T.Add(a0); T.Add(b1); T.Add(b0);
                        T.Add(a0); T.Add(a1); T.Add(b1);
                    }
                }
            }
        }

        /// <summary>后端环形端面：最后一对内/外环之间那一圈，不补就从后面看进去是个洞。</summary>
        private static void Cap(MeshAcc acc, List<IntakeSection> rings, bool flip)
        {
            if (rings == null || rings.Count == 0) return;
            IntakeSection s = rings[rings.Count - 1];
            int m = ProfileSegments;
            Vector3[] inner = RingPoints(s, false, m);
            Vector3[] outer = RingPoints(s, true, m);

            int start = acc.V.Count;
            for (int j = 0; j < m; j++)
            {
                Vector3 nn = new Vector3(0f, 1f, 0f);        // 端面朝后（局部 +Y）
                acc.V.Add(inner[j]); acc.N.Add(nn); acc.Uv.Add(new Vector2((float)j / m, 0f));
                acc.V.Add(outer[j]); acc.N.Add(nn); acc.Uv.Add(new Vector2((float)j / m, 1f));
            }
            List<int> T = acc.Subs[SubCold];
            for (int j = 0; j < m; j++)
            {
                int jp = (j + 1) % m;
                int a0 = start + j * 2, a1 = start + j * 2 + 1;
                int b0 = start + jp * 2, b1 = start + jp * 2 + 1;
                if (!flip)
                {
                    T.Add(a0); T.Add(b0); T.Add(b1);
                    T.Add(a0); T.Add(b1); T.Add(a1);
                }
                else
                {
                    T.Add(a0); T.Add(b1); T.Add(b0);
                    T.Add(a0); T.Add(a1); T.Add(b1);
                }
            }
        }

        /// <summary>
        /// 中心体：锥（可长成"锥+柱"）。用老的回转体工具——它本来就是一根圆剖面，
        /// 没必要为它写一套放样；而且这样中心体的贴图走向与喷管一致。
        /// </summary>
        private static void Spinner(MeshAcc acc, IntakeProfile p, List<IntakeSection> rings, bool flip)
        {
            Profile pr = new Profile();
            const int K = 10;
            for (int k = 0; k <= K; k++)
            {
                double x = p.SpinnerTipX + p.SpinnerLength * k / (double)K;
                double r = p.SpinnerRadiusAt(x);
                pr.Add((float)x, (float)Math.Max(r, 0.0));
            }
            if (pr.Points.Count >= 2) acc.Revolve(pr, ProfileSegments, SubHot, false, flip);

            // 底面封口：锥底若敞开，从后面能顺着中心体看进零件内部
            double rMax = p.SpinnerRadius;
            if (rMax > 1e-4)
            {
                double xEnd = p.SpinnerTipX + p.SpinnerLength;
                Profile cap = new Profile();
                cap.Add((float)xEnd, (float)rMax);
                cap.Add((float)xEnd, 0.0005f);
                acc.Revolve(cap, ProfileSegments, SubHot, false, flip);
            }
        }
    }
}