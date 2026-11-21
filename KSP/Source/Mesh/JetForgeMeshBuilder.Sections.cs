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
        // ---------- 各部件剖面 ----------

        /// <summary>短舱：进气口唇 → 风扇罩 → 逐渐收向喷口。有风扇时是包住核心机的整圈外壳。</summary>
        /// <summary>
        /// 后缘厚度：把剖面最后一个点折回来一小段，蒙皮才有"边"而不是一张纸。
        ///
        /// 双面网格虽然不会透底，但零厚度的边缘在掠射角下会闪一条亮线——看着像穿模。
        /// 折回来的那一段朝前（y 增大），正好是唇口内缘，真机也是这么折的。
        /// </summary>
        private static void FoldEdge(Profile p, float yEdge, float rEdge, float depth, float thickness)
        {
            if (depth <= 0f) return;
            p.Add(yEdge + depth * 0.18f, rEdge * (1f - thickness));
            p.Add(yEdge + depth, rEdge * (1f - thickness) * 1.004f);
        }

        private static Profile NacelleProfile(Dims m)
        {
            Profile p = new Profile();
            // 唇口外缘那一点（IntakeLipProfile 的末点）——外壳从这儿接上，高光才是连续的。
            float rLip = Mathf.Max(m.RIn * 1.02f, 0.02f);
            // 最粗处：玩家填的「短舱直径」就在这一处生效（§275）。以前这个数被拿去抬进气口半径，
            // 外皮又是那个半径的 1.03 倍，填 2.15 量出来 2.21，填小了还被 Math.Max 吃掉。
            float rOut = Mathf.Max(m.RNac, rLip);
            p.Add(m.YIn - m.L * 0.028f, rLip);
            p.Add(m.YIn - m.L * 0.055f, rOut);
            p.Add(m.YIn - m.L * 0.062f, rOut * 0.995f);
            // 风扇那一站：短舱不服（玩家把短舱填得比风扇罩粗很多）就别在这儿硬掐回风扇罩，
            // 否则进气口后面会有一段陡收缩，看着像被捏了一下（§275）。
            p.Add(m.YFan, m.HasFan
                ? Mathf.Max(m.RFan * 1.045f, rOut * 0.98f)
                : Mathf.Max(m.RCore * 1.10f, rOut * 0.98f));

            if (m.HighBypass)
            {
                // 高涵道：风扇舱到外涵喷口就结束，后缘一圈薄唇。
                // 这一段后面露出来的是核心机——BPR 5 和 BPR 0.5 一眼能分开，靠的就是这个长度差。
                p.Add(m.YFan - m.L * 0.08f, m.RFan * 1.015f);
                p.Add(m.YBypass + m.L * 0.02f, m.RBypass * 1.06f);
                p.Add(m.YBypass, m.RBypass * 1.03f);
                p.Add(m.YBypass - m.L * 0.006f, m.RBypass * 0.985f);   // 唇口往里翻
                FoldEdge(p, m.YBypass - m.L * 0.006f, m.RBypass * 0.985f, m.L * 0.055f, 0.020f);
                return p;
            }

            if (m.HasFan)
            {
                // 低涵道：外壳一直包到尾部，末端收口，露出后面的喷管。
                float yMid = Mathf.Lerp(m.YFan, m.YOut, 0.55f);
                p.Add(yMid, m.RFan * 0.92f);
                float yTail = m.YOut + m.L * 0.16f;
                float rTail = Mathf.Max(m.RNoz * 1.10f, m.RCore * 1.03f);
                p.Add(yTail, rTail);
                float rEnd = Mathf.Max(m.RNoz * 1.04f, m.RCore * 0.99f);
                p.Add(m.YOut + m.L * 0.12f, rEnd);
                FoldEdge(p, m.YOut + m.L * 0.12f, rEnd, m.L * 0.040f, 0.022f);
                return p;
            }

            // 涡喷：一根筒子，尾部同样收口，让喷管露在外面
            p.Add(Mathf.Lerp(m.YFan, m.YOut, 0.5f), m.RCore * 1.10f);
            p.Add(m.YOut + m.L * 0.16f, m.RNoz * 1.06f);
            float rEndJ = m.RNoz * 1.00f;
            p.Add(m.YOut + m.L * 0.12f, rEndJ);
            FoldEdge(p, m.YOut + m.L * 0.12f, rEndJ, m.L * 0.036f, 0.024f);
            return p;
        }

        /// <summary>
        /// 机匣对接箍：压在"冷端机匣 / 热端机匣"的分界站位上。
        ///
        /// 那条分界本来是硬缝——两边贴图色调不同，中间凭空一刀，看着像贴图错了。
        /// 真机这个位置就是一道连接法兰（燃烧室机匣跟压气机机匣对接），
        /// 给它加一圈箍，颜色变化就有了物理理由。箍本身走热端。
        /// </summary>
        private static Profile CasingJointProfile(Dims m, EngineDesignData d)
        {
            const float Grow = 1.035f;
            float y = CoreSplitY(m);
            float r = CasingAt(CoreProfile(m, d), y) * Grow;
            Profile p = new Profile();
            float h = m.L * 0.009f;
            // 两道法兰盘 + 中间一道槽：真机对接是两片法兰拿一圈螺栓夹起来的，中间那道槽
            // 就是夹缝。以前只有一个鼓包，配上机匣的砂面贴图看着像个胀起来的环。
            p.Add(y + h, r * 0.985f);
            p.Add(y + h * 0.78f, r);
            p.Add(y + h * 0.30f, r);
            p.Add(y + h * 0.16f, r * 0.972f);        // 槽口
            p.Add(y - h * 0.16f, r * 0.972f);
            p.Add(y - h * 0.30f, r);
            p.Add(y - h * 0.78f, r);
            p.Add(y - h, r * 0.985f);
            return p;
        }

// ---------- 进气口与风扇 ----------

        /// <summary>
        /// 进气唇口：五点圆唇（喉道 → 唇口前缘 → 外唇）。
        ///
        /// 短舱剖面原来从 RIn 一步跳到 1.03RIn，侧壁上像被刀切了一刀，圆唇才有那道高光。
        /// 现在半径不再写死成 0.968/1.032 倍：捕获半径、喉道比、唇口倒圆
        /// 都从 `IntakeGeometry` 读——玩家把喉道比调到 0.7，剖面里那条内壁就真的往里收。
        /// </summary>
        private static Profile IntakeLipProfile(Dims m, EngineDesignData d)
        {
            Profile p = new Profile();
            var shape = JetForge.Core.IntakeGeometry.Of(d);
            float rc = (float)shape.RCapture;
            float rt = (float)shape.RThroat;
            float lip = (float)Math.Max(shape.LipRadius, 0.002);
            float y = m.YIn;

            p.Add(y + m.L * 0.022f, rt);                              // 内唇起点（喉道侧）
            p.Add(y + m.L * 0.016f, Mathf.Lerp(rt, rc, 0.45f));
            p.Add(y + m.L * 0.008f + lip * 0.35f, rc - lip * 0.15f);   // 前缘内侧
            p.Add(y + m.L * 0.002f, rc);                               // 最前一点（唇口）
            p.Add(y - m.L * 0.010f - lip * 0.35f, rc + lip * 0.85f);
            p.Add(y - m.L * 0.028f, Mathf.Max(rc + lip * 0.60f, m.RIn * 1.02f)); // 外唇，交给短舱
            return p;
        }

        /// <summary>
        /// 进气中心锥 / 斜板（锥体与斜板才有；轴对称与皮托没有）。
        /// 剖面从锥尖到喉道，回转体——斜板在这里是"一圈斜板"的近似，界面上有说明。
        /// </summary>
        private static Profile IntakeCenterBodyProfile(Dims m, EngineDesignData d)
        {
            var shape = JetForge.Core.IntakeGeometry.Of(d);
            Profile p = new Profile();
            if (!shape.HasCenterBody || shape.CenterBody.Count < 2) return p;
            for (int i = 0; i < shape.CenterBody.Count; i++)
            {
                float r = (float)shape.CenterBody[i].R;
                // 实心体：外沿走一遍，再沿轴封回去
                p.Add((float)shape.CenterBody[i].Y, Mathf.Max(r, 0.004f));
            }
            return p;
        }

        /// <summary>
        /// 风扇叶片数。<c>Fan.BladeCount</c> 为 0 时按直径自动估：宽弦风扇大约每米半径 28 片。
        /// 自动档只是缺省值，真机这个数跟"宽弦还是薄弦"强相关（CFM56-7B 1.55 m 只有 22 片宽弦，
        /// PW4000 2.54 m 有 38 片薄弦），所以必须允许玩家自己压过它。
        /// </summary>
        public static int BladesOf(EngineDesignData d)
        {
            if (d == null || d.Fan == null) return 0;
            if (d.Fan.BladeCount > 0) return Mathf.Clamp(d.Fan.BladeCount, 6, 60);
            float r = (float)(d.Fan.Diameter * 0.5);
            return Mathf.Clamp(Mathf.RoundToInt(r * 28f), 12, 38);
        }

        /// <summary>
        /// 风扇叶片：每片两段四边形，根 0.30、尖 0.962 RFan，弦 0.07L。
        /// 扭转靠把前缘偏移一个角度（camber），叶根转得多、叶尖转得少；前缘再往后掠一点。
        /// 这三样缺一样，叶片看上去就是一片片直板贴在轴上。
        /// </summary>
        private static void AddFanBlades(MeshAcc acc, Dims m, EngineDesignData d, bool flip)
        {
            if (!m.HasFan) return;
            int blades = BladesOf(d);
            float rHub = m.RFan * 0.30f;
            float rTip = m.RFan * 0.962f;
            float yLead = m.YFan + m.L * 0.010f;
            float chord = Mathf.Clamp(m.L * 0.070f, 0.05f, 0.20f);
            float[] frac = { 0f, 0.55f, 1f };

            for (int i = 0; i < blades; i++)
            {
                float a = (i + 0.5f) / blades * Mathf.PI * 2f;
                Vector3[] lead = new Vector3[3];
                Vector3[] trail = new Vector3[3];
                for (int k = 0; k < 3; k++)
                {
                    float t = frac[k];
                    float r = Mathf.Lerp(rHub, rTip, t);
                    // 扭转：叶根前缘转得多（迎角大），叶尖转得少
                    float camber = Mathf.Lerp(0.40f, 0.14f, t) * chord / Mathf.Max(r, 0.05f);
                    float sweep = chord * (0.08f + 0.40f * t);   // 前缘后掠
                    lead[k] = new Vector3(r * Mathf.Cos(a + camber), yLead - sweep, r * Mathf.Sin(a + camber));
                    trail[k] = new Vector3(r * Mathf.Cos(a - camber * 0.55f),
                                           yLead - sweep - chord * (1f - 0.16f * t),
                                           r * Mathf.Sin(a - camber * 0.55f));
                }
                for (int k = 0; k < 2; k++)
                {
                    Vector3 n = new Vector3(Mathf.Cos(a) * 0.25f, 1f, Mathf.Sin(a) * 0.25f).normalized;
                    float r0 = Mathf.Lerp(rHub, rTip, frac[k]);
                    float r1 = Mathf.Lerp(rHub, rTip, frac[k + 1]);
                    float u0 = a * r0 / UvWorldSize;
                    float u1 = a * r1 / UvWorldSize;
                    acc.AddQuad(lead[k], lead[k + 1], trail[k + 1], trail[k],
                                n, n, n, n,
                                new Vector2(u0, 0f), new Vector2(u1, 0f),
                                new Vector2(u1, chord / UvWorldSize),
                                new Vector2(u0, chord / UvWorldSize), SubCold, flip);
                }
            }
        }
    }
}
