using System;
using System.Collections.Generic;
using UnityEngine;
using JetForge.Core;

namespace JetForge
{
    /// <summary>
    /// 程序化细节：叶片排、静子、机匣法兰、附件机匣、点火器。
    ///
    /// 这些东西一个都不参与气动，纯粹是"让玩家看出这台机器有几个级"。
    /// 所以它们全部受 DetailLevel 控制：低档一台都不加，高档才有静子和法兰。
    /// </summary>
    public static partial class JetForgeMeshBuilder
    {
        // ---------- 叶片排 ----------

        /// <summary>
        /// 一排叶片。每片 2 段（根—中—尖），4 个顶点一个四边形，成本 2 个三角。
        ///
        /// 叶片的"扭转"用前后缘周向错开来表示：camber 越大，前缘比后缘更靠周向一边。
        /// 真机转子从根到尖扭转角变小（尖部线速度大），所以 camber 随半径往下收。
        /// </summary>
        private static void AddBladeRow(MeshAcc acc, float yCenter, float rHub, float rTip,
                                        int count, float chord, float camber, int submesh,
                                        bool flip, float phase)
        {
            if (count <= 0) return;
            float span = rTip - rHub;
            if (span < 0.008f || chord < 0.004f) return;

            float[] frac = { 0f, 0.5f, 1f };
            Vector3[] lead = new Vector3[3];
            Vector3[] trail = new Vector3[3];
            Vector3[] nrm = new Vector3[3];

            for (int i = 0; i < count; i++)
            {
                float ang = (i + phase) / count * Mathf.PI * 2f;
                for (int k = 0; k < 3; k++)
                {
                    float t = frac[k];
                    float r = Mathf.Lerp(rHub, rTip, t);
                    float tw = camber * chord / Mathf.Max(r, 0.05f);
                    float sweep = chord * (0.10f + 0.28f * t);
                    float yLead = yCenter + chord * 0.5f - sweep;
                    lead[k] = new Vector3(r * Mathf.Cos(ang + tw), yLead, r * Mathf.Sin(ang + tw));
                    trail[k] = new Vector3(r * Mathf.Cos(ang - tw * 0.55f), yLead - chord,
                                           r * Mathf.Sin(ang - tw * 0.55f));
                    // 薄板法线取周向（叶片像一片立着的plate），双面网格会补另一侧。
                    nrm[k] = new Vector3(-Mathf.Sin(ang), 0.10f, Mathf.Cos(ang)).normalized;
                }
                float u = chord / UvWorldSize;
                for (int k = 0; k < 2; k++)
                {
                    float v0 = Mathf.Lerp(rHub, rTip, frac[k]) / UvWorldSize;
                    float v1 = Mathf.Lerp(rHub, rTip, frac[k + 1]) / UvWorldSize;
                    acc.AddQuad(lead[k], trail[k], trail[k + 1], lead[k + 1],
                                nrm[k], nrm[k], nrm[k + 1], nrm[k + 1],
                                new Vector2(0f, v0), new Vector2(u, v0),
                                new Vector2(u, v1), new Vector2(0f, v1),
                                submesh, flip);
                }
            }
        }

        /// <summary>一组压气机：转子每级一排，静子夹在级间（只有高档才有）。</summary>
        private static int AddCompressorSet(MeshAcc acc, Dims m, EngineDesignData d, CompressorSpec spec,
                                            float yFront, float yBack, bool flip)
        {
            if (spec == null || spec.Stages <= 0) return 0;
            float span = yBack - yFront;
            if (Mathf.Abs(span) < 0.02f) return 0;

            int blades = DetailSpec.RotorBlades(d.Detail);
            int vanes = DetailSpec.StatorVanes(d.Detail);

            int n = spec.Stages;
            float rTip0 = Mathf.Max((float)(spec.TipDiameter * 0.5), m.RCore * 0.30f);
            float rHub0 = rTip0 * Mathf.Clamp((float)spec.HubTipRatio, 0.25f, 0.85f);
            float pitch = span / n;
            int rows = 0;

            for (int i = 0; i < n; i++)
            {
                float t = n > 1 ? (float)i / (n - 1) : 0.5f;
                float y = yFront + pitch * (i + 0.5f);
                // 往后一级，叶尖矮一点、轮毂粗一点：压比越高压气机越"瘦"，通流面积是缩的。
                float rTip = rTip0 * (1f - 0.10f * t);
                // 玩家逐级填过直径就以他的为准：那套数字是"这台机器每级多粗"的
                // 全部信息，外面再用缺省的 0.90 收口等于把他填的盖掉一半。
                if (spec.HasStageEdit())
                {
                    float ratio = (float)(spec.TipOf(i) / Math.Max(1e-6, spec.TipDiameter));
                    rTip = rTip0 * Mathf.Clamp(ratio, 0.45f, 1.35f);
                }
                float rHub = rHub0 * (1f + 0.16f * t);
                float chord = Mathf.Clamp(Mathf.Abs(pitch) * 0.62f, 0.006f, 0.05f);

                if (blades > 0)
                {
                    AddBladeRow(acc, y, rHub, rTip, blades, chord, 0.34f, SubCold, flip, 0.0f);
                    rows++;
                }
                if (vanes > 0 && i < n - 1)
                {
                    float yv = yFront + pitch * (i + 1f);
                    AddBladeRow(acc, yv, rHub, rTip * 1.02f, vanes, chord * 0.80f, -0.22f, SubCold, flip, 0.5f);
                    rows++;
                }
            }
            return rows;
        }

        /// <summary>一组涡轮：最多画 4 环（真机涡轮级数再多，肉眼也就是几环）。</summary>
        private static int AddTurbineSet(MeshAcc acc, Dims m, EngineDesignData d, TurbineSpec spec,
                                         float frac, bool flip)
        {
            if (spec == null || spec.Stages <= 0) return 0;
            int blades = DetailSpec.TurbineBlades(d.Detail);
            if (blades <= 0) return 0;

            int rowsN = Mathf.Min(spec.Stages, 4);
            float yc = Mathf.Lerp(m.YCoreStart, m.YAb, frac);
            float pitch = Mathf.Max(m.L * 0.011f, 0.006f);
            float rTip = Mathf.Max((float)(spec.TipDiameter * 0.5), m.RCore * 0.45f);
            float rHub = rTip * Mathf.Clamp((float)spec.HubTipRatio, 0.25f, 0.85f);
            float chord = Mathf.Clamp(pitch * 0.66f, 0.006f, 0.04f);
            int rows = 0;
            for (int i = 0; i < rowsN; i++)
            {
                float y = yc + pitch * ((rowsN - 1) * 0.5f - i);
                if (spec.HasStageEdit())
                {
                    float ratio = (float)(spec.TipOf(i) / Math.Max(1e-6, spec.TipDiameter));
                    rTip = Mathf.Max((float)(spec.TipDiameter * 0.5), m.RCore * 0.45f)
                           * Mathf.Clamp(ratio, 0.55f, 1.40f);
                    rHub = rTip * Mathf.Clamp((float)spec.HubTipRatio, 0.25f, 0.85f);
                }
                // 涡轮叶片弯得比压气机厉害：一个是把气流拧着加速，一个是被气流拧着转。
                AddBladeRow(acc, y, rHub, rTip, blades, chord, 0.55f, SubHot, flip, 0.0f);
                rows++;
            }
            return rows;
        }

        /// <summary>把压气机与涡轮的叶片排都加进网格，返回加了排数（供自检对账）。</summary>
        private static int AddBladeDetail(MeshAcc acc, Dims m, EngineDesignData d, bool flip)
        {
            DetailLevel lv = d.Detail;
            if (DetailSpec.RotorBlades(lv) <= 0 && DetailSpec.TurbineBlades(lv) <= 0)
                return 0;

            int rows = 0;
            float yCompEnd = Mathf.Lerp(m.YCoreStart, m.YAb, 0.45f);
            int lpcN = d.Lpc != null ? d.Lpc.Stages : 0;
            int hpcN = d.Hpc != null ? d.Hpc.Stages : 0;
            float split = (lpcN > 0 && hpcN > 0) ? Mathf.Lerp(m.YCoreStart, yCompEnd, 0.45f) : m.YCoreStart;

            rows += AddCompressorSet(acc, m, d, d.Lpc, m.YCoreStart, split, flip);
            rows += AddCompressorSet(acc, m, d, d.Hpc, split, yCompEnd, flip);

            if (d.Hpt != null) rows += AddTurbineSet(acc, m, d, d.Hpt, 0.74f, flip);
            if (m.HasIpt && d.Ipt != null) rows += AddTurbineSet(acc, m, d, d.Ipt, 0.84f, flip);
            if (d.Lpt != null) rows += AddTurbineSet(acc, m, d, d.Lpt, 0.93f, flip);
            return rows;
        }

        // ---------- 机匣法兰 ----------

        /// <summary>在给定站位上插值出机匣半径（CoreProfile 的点列是 y 单调的）。</summary>
        private static float CasingAt(Profile core, float y)
        {
            List<Vector2> pts = core.Points;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float y0 = pts[i].x, y1 = pts[i + 1].x;
                if ((y <= y0 && y >= y1) || (y >= y0 && y <= y1))
                {
                    float t = Mathf.Approximately(y1, y0) ? 0f : (y - y0) / (y1 - y0);
                    return Mathf.Lerp(pts[i].y, pts[i + 1].y, t);
                }
            }
            return pts.Count > 0 ? pts[0].y : 0.2f;
        }

        private static void AddFlangeRow(List<Profile> into, Profile core, float y, float half, float grow)
        {
            float r0 = CasingAt(core, y);
            float r1 = r0 * grow;
            Profile p = new Profile();
            p.Add(y + half, r0 * 1.001f);
            p.Add(y + half * 0.55f, r1);
            p.Add(y - half * 0.55f, r1);
            p.Add(y - half, r0 * 1.001f);
            into.Add(p);
        }

        /// <summary>每级之间一道箍：真机压气机机匣就是一级一段、靠法兰连起来的。</summary>
        internal static List<Profile> DetailFlangeProfiles(Dims m, EngineDesignData d)
        {
            List<Profile> list = new List<Profile>();
            if (!DetailSpec.Flanges(d.Detail)) return list;

            Profile core = CoreProfile(m, d);
            float yCompEnd = Mathf.Lerp(m.YCoreStart, m.YAb, 0.45f);
            int lpcN = d.Lpc != null ? d.Lpc.Stages : 0;
            int hpcN = d.Hpc != null ? d.Hpc.Stages : 0;
            float split = (lpcN > 0 && hpcN > 0) ? Mathf.Lerp(m.YCoreStart, yCompEnd, 0.45f) : m.YCoreStart;

            float half = Mathf.Max(m.L * 0.0035f, 0.002f);
            int made = 0;
            made += AddFlangeSet(list, core, d.Lpc, m.YCoreStart, split, half, 6);
            made += AddFlangeSet(list, core, d.Hpc, split, yCompEnd, half, 6);
            if (made == 0)
            {
                // 没有压气机级数（不该发生）也不画假的：宁可一道都没有。
            }
            return list;
        }

        private static int AddFlangeSet(List<Profile> into, Profile core, CompressorSpec spec,
                                        float yFront, float yBack, float half, int maxFlanges)
        {
            if (spec == null || spec.Stages <= 0) return 0;
            int n = Mathf.Min(spec.Stages, maxFlanges);
            float pitch = (yBack - yFront) / spec.Stages;
            if (Mathf.Abs(pitch) < 0.004f) return 0;
            for (int i = 1; i <= n; i++)
            {
                AddFlangeRow(into, core, yFront + pitch * i, half, 1.014f);
            }
            return n;
        }

        // ---------- 附件机匣、管路、点火器 ----------

        private static void AddBox(MeshAcc acc, Vector3 c, Vector3 ax, Vector3 ay, Vector3 az,
                                   int submesh, bool flip)
        {
            Vector3[] p = new Vector3[8];
            int k = 0;
            for (int i = -1; i <= 1; i += 2)
                for (int j = -1; j <= 1; j += 2)
                    for (int l = -1; l <= 1; l += 2)
                        p[k++] = c + ax * i + ay * j + az * l;

            // 顶点编号：i 最快、l 最慢 → (x,y,z) 的符号组合
            // 0(-,-,-) 1(+,-,-) 2(-,+,-) 3(+,+,-) 4(-,-,+) 5(+,-,+) 6(-,+,+) 7(+,+,+)
            AddFace(acc, p, 0, 1, 3, 2, -az, submesh, flip, 1f);
            AddFace(acc, p, 4, 6, 7, 5, az, submesh, flip, 1f);
            AddFace(acc, p, 0, 4, 5, 1, -ay, submesh, flip, 1f);
            AddFace(acc, p, 2, 3, 7, 6, ay, submesh, flip, 1f);
            AddFace(acc, p, 0, 2, 6, 4, -ax, submesh, flip, 1f);
            AddFace(acc, p, 1, 5, 7, 3, ax, submesh, flip, 1f);
        }

        private static void AddFace(MeshAcc acc, Vector3[] p, int a, int b, int c, int d,
                                    Vector3 n, int submesh, bool flip, float uvScale)
        {
            Vector3 nn = n.normalized;
            float u = uvScale / UvWorldSize;
            acc.AddQuad(p[a], p[b], p[c], p[d], nn, nn, nn, nn,
                        new Vector2(0f, 0f), new Vector2(u, 0f), new Vector2(u, u), new Vector2(0f, u),
                        submesh, flip);
        }

        /// <summary>
        /// 附件机匣与管路。真机都挂在压气机下侧：齿轮箱从压气机前轴承取功率，
        /// 带动燃油泵、滑油泵、发电机。这正是"固定开销"那 3% 功率的去处。
        /// </summary>
        private static int AddAccessoryDetail(MeshAcc acc, Dims m, EngineDesignData d, bool flip)
        {
            if (!DetailSpec.Accessories(d.Detail)) return 0;

            Profile core = CoreProfile(m, d);
            float yCase = Mathf.Lerp(m.YCoreStart, m.YAb, 0.22f);
            float rCase = CasingAt(core, yCase);
            float len = Mathf.Max(m.L * 0.075f, 0.03f);
            float wide = Mathf.Max(m.L * 0.045f, 0.018f);
            float depth = Mathf.Max(rCase * 0.30f, 0.02f);

            // 机匣外表面朝下（-z）一点，别穿进壳体里
            Vector3 c = new Vector3(0f, yCase, -(rCase + depth * 0.85f));
            AddBox(acc, c, new Vector3(depth, 0f, 0f), new Vector3(0f, len * 0.5f, 0f),
                   new Vector3(0f, 0f, wide * 0.5f), SubCold, flip);

            // 两根管：从前轴承往后走，绕到机匣侧上方
            for (int i = 0; i < 2; i++)
            {
                float ang = i == 0 ? -0.55f : 3.69f;   // 两侧偏下
                float r = rCase * 1.02f;
                float y0 = yCase + len * 1.15f;
                float y1 = yCase - len * 0.85f;
                float pr = Mathf.Max(rCase * 0.045f, 0.006f);
                Vector3 a = new Vector3(r * Mathf.Cos(ang), y0, r * Mathf.Sin(ang));
                Vector3 b = new Vector3(r * Mathf.Cos(ang), y1, r * Mathf.Sin(ang));
                AddPipe(acc, a, b, pr, 4, SubCold, flip);
            }
            return 3;
        }

        /// <summary>一根方管（4 面），够看清就行——真机那些管子在这个尺度上也就是几像素。</summary>
        private static void AddPipe(MeshAcc acc, Vector3 a, Vector3 b, float radius, int sides,
                                    int submesh, bool flip)
        {
            if (sides < 3) sides = 3;
            Vector3 axis = (b - a);
            if (axis.sqrMagnitude < 1e-8f) return;
            axis = axis.normalized;
            Vector3 up = Mathf.Abs(axis.y) > 0.9f ? Vector3.right : Vector3.up;
            Vector3 u = Vector3.Cross(axis, up).normalized * radius;
            Vector3 v = Vector3.Cross(axis, u).normalized * radius;

            for (int i = 0; i < sides; i++)
            {
                float a0 = i / (float)sides * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)sides * Mathf.PI * 2f;
                Vector3 o0 = u * Mathf.Cos(a0) + v * Mathf.Sin(a0);
                Vector3 o1 = u * Mathf.Cos(a1) + v * Mathf.Sin(a1);
                Vector3 n0 = o0.normalized, n1 = o1.normalized;
                acc.AddQuad(a + o0, a + o1, b + o1, b + o0, n0, n1, n1, n0,
                            new Vector2(0f, 0f), new Vector2(0.2f, 0f),
                            new Vector2(0.2f, 0.2f), new Vector2(0f, 0.2f), submesh, flip);
            }
        }

        /// <summary>点火器凸台：燃烧室两侧各一个，真机也就这么两个。</summary>
        private static int AddIgniterDetail(MeshAcc acc, Dims m, EngineDesignData d, bool flip)
        {
            if (!DetailSpec.Igniters(d.Detail)) return 0;

            float yBurn = Mathf.Lerp(m.YCoreStart, m.YAb, 0.60f);
            float rShell = m.RCore * BurnerShellFactor(d);
            float size = Mathf.Max(m.L * 0.016f, 0.008f);
            float[] angs = { 1.05f, 4.19f };
            for (int i = 0; i < angs.Length; i++)
            {
                float a = angs[i];
                Vector3 c = new Vector3(Mathf.Cos(a) * (rShell * 1.02f + size * 0.5f), yBurn,
                                        Mathf.Sin(a) * (rShell * 1.02f + size * 0.5f));
                AddBox(acc, c, new Vector3(size * 0.5f, 0f, 0f), new Vector3(0f, size * 0.5f, 0f),
                       new Vector3(0f, 0f, size * 0.5f), SubHot, flip);
            }
            return angs.Length;
        }

        /// <summary>
        /// 喷口作动筒：可变/收扩喷管上，每片花瓣背后都有一根拉杆。
        ///
        /// 真机这个位置最显眼（从后面看就是一圈斜杆），而它一点物理都不影响。
        /// 画的是"机匣外壁 → 花瓣铰点"的静态杆：铰点钉在 0.975·rExit 上不动，
        /// 花瓣绕它转，所以杆不需要跟着开度动画走——把它塞进花瓣网格反而会每帧重建。
        /// </summary>
        private static int AddNozzleActuators(MeshAcc acc, Dims m, EngineDesignData d, bool flip)
        {
            if (!DetailSpec.Accessories(d.Detail)) return 0;
            if (d.Nozzle == NozzleKind.Converging) return 0;

            float rExit = NozzleExitR(m, d);
            float hingeR = rExit * (float)Core.NozzleVisual.HingeFactor;
            float yHinge = m.YOut + m.L * 0.004f;
            float yRoot = yHinge + m.L * 0.105f;
            float rootR = rExit * 1.10f;
            float rad = Mathf.Max(m.L * 0.0055f, 0.0035f);
            const int Rods = 6;

            for (int i = 0; i < Rods; i++)
            {
                float a = i / (float)Rods * Mathf.PI * 2f;
                Vector3 root = new Vector3(Mathf.Cos(a) * rootR, yRoot, Mathf.Sin(a) * rootR);
                Vector3 tip = new Vector3(Mathf.Cos(a) * hingeR, yHinge, Mathf.Sin(a) * hingeR);
                // 杆在尾段，跟着涡轮和喷管一起烤，走热端
                AddPipe(acc, root, tip, rad, 4, SubHot, flip);
            }
            return Rods;
        }

        /// <summary>作动筒根数（自检打这个数，免得"改了条件忘了改计数"）。</summary>
        internal static int ActuatorCount(EngineDesignData d)
        {
            if (!DetailSpec.Accessories(d.Detail)) return 0;
            if (d.Nozzle == NozzleKind.Converging) return 0;
            return 6;
        }

        // ---------- 自述 ----------

        internal static string DescribeDetail(EngineDesignData d)
        {
            DetailLevel lv = d.Detail;
            int rot = DetailSpec.RotorBlades(lv);
            int van = DetailSpec.StatorVanes(lv);
            int tur = DetailSpec.TurbineBlades(lv);
            int compStages = (d.Lpc != null ? d.Lpc.Stages : 0) + (d.Hpc != null ? d.Hpc.Stages : 0);
            int turbStages = (d.Hpt != null ? Mathf.Min(d.Hpt.Stages, 4) : 0)
                           + (d.Lpt != null ? Mathf.Min(d.Lpt.Stages, 4) : 0)
                           + (d.Ipt != null ? Mathf.Min(d.Ipt.Stages, 4) : 0);
            int statorRows = van > 0 ? Mathf.Max(0, compStages - ((d.Lpc != null && d.Lpc.Stages > 0 ? 1 : 0)
                                                                 + (d.Hpc != null && d.Hpc.Stages > 0 ? 1 : 0))) : 0;
            return string.Format(
                "细节：{0}｜压气机叶排 {1} 排 × {2} 片｜静子 {3} 排 × {4} 片｜涡轮叶排 {5} 排 × {6} 片｜法兰 {7}｜附件 {8}｜点火器 {9}",
                DetailSpec.Name(lv),
                compStages, rot, statorRows, van, turbStages, tur,
                DetailSpec.Flanges(lv) ? "有" : "无",
                DetailSpec.Accessories(lv) ? "有" : "无",
                DetailSpec.Igniters(lv) ? "有" : "无");
        }
    }
}