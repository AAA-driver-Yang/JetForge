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
        // ---------- 燃烧室外部件 ----------

        /// <summary>燃油总管：燃烧室外面一圈凸环，真机的燃油从这里分到各个喷嘴。</summary>
        private static Profile FuelManifoldProfile(Dims m, EngineDesignData d)
        {
            Profile p = new Profile();
            float y = Mathf.Lerp(m.YCoreStart, m.YAb, 0.545f);
            float r = m.RCore * BurnerShellFactor(d);
            float h = m.L * 0.013f;
            p.Add(y + h, r * 0.999f);
            p.Add(y + h * 0.45f, r * 1.028f);
            p.Add(y - h * 0.45f, r * 1.028f);
            p.Add(y - h, r * 0.999f);
            return p;
        }

        /// <summary>
        /// 燃油喷嘴支管：环式 8 根，罐式按罐数取 4~18 根。
        /// 只画凸台（两片交叉四边形），不画内部油路——那些在机匣里看不见。
        /// </summary>
        private static void AddFuelNozzles(MeshAcc acc, Dims m, EngineDesignData d, bool flip)
        {
            int n = BurnerCans(d);
            if (n <= 0) n = 8;
            n = Mathf.Clamp(n, 4, 18);
            float y = Mathf.Lerp(m.YCoreStart, m.YAb, 0.545f);
            float r = m.RCore * BurnerShellFactor(d) * 1.028f;
            float len = m.L * 0.020f;
            float half = m.L * 0.006f;

            for (int i = 0; i < n; i++)
            {
                float a = (i + 0.5f) / n * Mathf.PI * 2f;
                Vector3 radial = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Vector3 tang = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
                Vector3 p0 = new Vector3(radial.x * r, y, radial.z * r);
                Vector3 p1 = new Vector3(radial.x * (r + len), y, radial.z * (r + len));
                Vector3 nrm = radial;

                acc.AddQuad(p0 - tang * half, p0 + tang * half, p1 + tang * half, p1 - tang * half,
                            nrm, nrm, nrm, nrm,
                            Vector2.zero, new Vector2(half * 2f / UvWorldSize, 0f),
                            new Vector2(half * 2f / UvWorldSize, len / UvWorldSize),
                            new Vector2(0f, len / UvWorldSize), SubCold, flip);
                Vector3 up = Vector3.up * half;
                acc.AddQuad(p0 + up, p0 - up, p1 - up, p1 + up,
                            Vector3.up, Vector3.up, Vector3.up, Vector3.up,
                            Vector2.zero, new Vector2(half * 2f / UvWorldSize, 0f),
                            new Vector2(half * 2f / UvWorldSize, len / UvWorldSize),
                            new Vector2(0f, len / UvWorldSize), SubCold, flip);
            }
        }

        /// <summary>外涵出口环（只有高涵道才加）：外圈写到内圈，轴向切向朝后，法线朝后。</summary>
        private static Profile BypassDuctRing(Dims m)
        {
            Profile p = new Profile();
            p.Add(m.YBypass, m.RBypass * 0.985f);
            p.Add(m.YBypass, m.RCore * 1.02f);
            return p;
        }
        private static Profile NacelleBandProfile(Dims m)
        {
            Profile p = new Profile();
            float y = Mathf.Lerp(m.YIn, m.HasFan ? m.YFan : m.YOut, 0.62f);
            // 箍压在短舱外皮上，半径得跟着外壳走。以前高涵道拿外涵喷口半径当箍半径，
            // 而箍画在风扇前头那段——那个半径比这段外壳小一截，箍整圈埋在蒙皮里看不见（§275）。
            float rSkin = Mathf.Lerp(m.RNac, m.HasFan ? m.RFan * 1.045f : m.RCore * 1.10f, 0.62f);
            float r = rSkin;
            float h = m.L * 0.010f;
            // 卡箍带宽 + 中间一道凸筋：以前是个光滑鼓包，跟短舱的弧度连在一起看不出来是箍。
            // 现在有 0.016R 的台阶落差，掠射角下能看见一条硬边，材质又和蒙皮不同，就分得开了。
            p.Add(y + h, r * 0.995f);
            p.Add(y + h * 0.74f, r * 1.010f);
            p.Add(y + h * 0.30f, r * 1.010f);
            p.Add(y + h * 0.20f, r * 1.028f);        // 凸筋
            p.Add(y - h * 0.20f, r * 1.028f);
            p.Add(y - h * 0.30f, r * 1.010f);
            p.Add(y - h * 0.74f, r * 1.010f);
            p.Add(y - h, r * 0.995f);
            return p;
        }

        /// <summary>核心机机匣：从风扇后到涡轮出口。</summary>
        /// <summary>机匣上"冷变热"的站位：燃烧室起点。再往后的机匣挨着火焰筒和涡轮，会烧出回火色。</summary>
        private static float CoreSplitY(Dims m)
        {
            return Mathf.Lerp(m.YCoreStart, m.YAb, 0.60f);
        }

        /// <summary>
        /// 把一个回转剖面在某站位切成两段。
        ///
        /// 为什么要切：贴图是分冷/热/内壁三套的，以前整台核心机都算冷端，
        /// 于是涡轮外面的那截机匣跟风扇舱一个颜色——真机那里早就烧成蓝紫色了。
        /// 切在燃烧室起点（0.60），后半段走热端贴图。
        ///
        /// 两段共用同一个边界点，接缝处半径一致；除了盖面限制（只留外端），没有别的特殊处理。
        /// </summary>
        private static void SplitProfile(Profile src, float y, out Profile front, out Profile rear)
        {
            front = new Profile();
            rear = new Profile();
            if (src == null || src.Points.Count < 2) { front = src; rear = null; return; }

            // 站位的 y 在本工程里是**从前往后递减**的（YIn 大、YOut 小），
            // 所以不能写 `pt.x <= y` 这种带方向的判断——第一次就是这么写的，
            // 第一点就判"越过分界"，去取 Points[-1]，自检里炸成 ArgumentOutOfRange。
            // 改成只问"这一点和上一点在分界的同一侧吗"。
            bool put = false;
            for (int i = 0; i < src.Points.Count; i++)
            {
                Vector2 pt = src.Points[i];
                if (put) { rear.Add(pt.x, pt.y); continue; }

                front.Add(pt.x, pt.y);
                if (i == 0) continue;

                Vector2 prev = src.Points[i - 1];
                bool prevAhead = prev.x > y;
                bool curAhead = pt.x > y;
                if (prevAhead == curAhead) continue;   // 同侧

                float t = Mathf.Approximately(pt.x, prev.x) ? 0f : (y - prev.x) / (pt.x - prev.x);
                float ry = Mathf.Lerp(prev.y, pt.y, t);
                front.Add(y, ry);
                rear.Add(y, ry);
                rear.Add(pt.x, pt.y);
                put = true;
            }
            if (!put || front.Points.Count < 2 || rear.Points.Count < 2) { front = src; rear = null; return; }
            front.CapStart = src.CapStart;
            front.CapEnd = false;
            rear.CapStart = false;
            rear.CapEnd = src.CapEnd;
        }

        /// <summary>
        /// 核心机匣收到尾端（喷管起点）时的半径。**喷管根部必须从这个数接着长**。
        ///
        /// 以前没有这个函数：机匣末点是 RCore×0.99，而喷管根部写死 RCore×1.00，
        /// 无加力时 CoreProfile 还会再往后再画一段到 YOut+0.06L、半径 RNoz——
        /// 那一段正好落在喷管收敛锥里头（GTF 那里机匣 0.307、喷管 0.305），
        /// 两个壳套在一起、机匣从喷管面戳出来，尾部怎么看都不对（§275）。
        /// </summary>
        private static float CoreTailR(Dims m)
        {
            // 数在 Core.EngineGeometry（站位表）里算，网格只搬——剖视图与网格必须是同一个数。
            return m.RTail;
        }

        private static Profile CoreProfile(Dims m, EngineDesignData d)
        {
            Profile p = new Profile();
            float rStart = m.HasFan ? m.RCore * 0.96f : m.RIn * 0.92f;
            p.Add(m.YCoreStart, rStart);
            p.Add(m.YCoreStart - m.L * 0.05f, m.RCore);
            // 压气机段略微收缩（真实机匣是前粗后细）
            float yComp = Mathf.Lerp(m.YCoreStart, m.YAb, 0.45f);
            p.Add(yComp, m.RCore * 0.97f);
            // 燃烧室段：罐式要多包一圈筒体（真机 J79 有 10 个单罐），环形最瘦
            float yBurn = Mathf.Lerp(m.YCoreStart, m.YAb, 0.60f);
            p.Add(yBurn, m.RCore * BurnerShellFactor(d));
            float yTurb = Mathf.Lerp(m.YCoreStart, m.YAb, 0.80f);
            p.Add(yTurb, m.RCore * 1.01f);
            // 末点收在喷管起点上。以前这里之后还有一段"机匣一路收到喷管"，
            // 那段越过了喷管起点、半径还比喷管大，两个壳互相穿（见 CoreTailR）。
            p.Add(m.YAb, CoreTailR(m));
            return p;
        }

        /// <summary>加力筒体的半径（比核心机粗一圈：里面有筒衬 + 火焰稳定器）。</summary>
        private static float AbRadius(Dims m, EngineDesignData d)
        {
            if (d == null || d.Afterburner == null) return m.RCore * 1.12f;
            switch (d.Afterburner.Kind)
            {
                case AfterburnerKind.Ring: return m.RCore * 1.20f;      // 径向环：筒子最粗
                case AfterburnerKind.SprayRing: return m.RCore * 1.14f;
                default: return m.RCore * 1.12f;
            }
        }

        /// <summary>
        /// 加力段筒体：涡轮出口 → 喷管起点。
        ///
        /// 真机的加力段是一整节：外筒 + 隔热筒衬 + 火焰稳定器（V 形环或径向喷油环），
        /// 燃气在这里二次燃烧，然后才进喷管。我们的模型以前把它压缩成"核心机后段粗 12%"，
        /// 于是排气锥一路戳到喷口上——玩家从后面看到一根锥子插在喷口里，确实不像现实。
        /// </summary>
        private static Profile AbDuctProfile(Dims m, EngineDesignData d)
        {
            Profile p = new Profile();
            float r = AbRadius(m, d);
            p.Add(m.YAb, m.RCore * 1.06f);
            p.Add(m.YAb - m.AbLen * 0.16f, r);
            p.Add(m.YNoz + m.AbLen * 0.12f, r * 1.01f);
            p.Add(m.YNoz, m.RNoz * 1.05f);
            return p;
        }

        /// <summary>
        /// 火焰稳定器：加力段里 2~3 圈比流道略小的薄环，从喷口往里看能看见。
        /// 真机是 V 形槽环（J79 是环形稳定器 + 喷油环），不是实心的。
        /// </summary>
        private static void AddAbFlameHolders(List<Profile> into, Dims m, EngineDesignData d)
        {
            if (d == null || !m.HasAb || m.AbLen <= 0f) return;
            float r = AbRadius(m, d) * 0.80f;
            float half = m.AbLen * 0.030f;
            for (int i = 0; i < 3; i++)
            {
                float y = m.YAb - m.AbLen * (0.26f + 0.16f * i);
                Profile p = new Profile();
                p.Add(y + half, r * 0.94f);
                p.Add(y + half * 0.5f, r);
                p.Add(y - half * 0.5f, r);
                p.Add(y - half, r * 0.94f);
                into.Add(p);
            }
        }

        /// <summary>
        /// 燃烧室让机匣粗多少。罐式要一圈独立的火焰筒，罐环介于中间，
        /// 环形只有一个通道——真机拿 J79（罐式）和 J85（环形）比外径，差别一眼可见。
        /// </summary>
        public static float BurnerShellFactor(EngineDesignData d)
        {
            if (d == null || d.Burner == null) return 1.0f;
            switch (d.Burner.Kind)
            {
                case BurnerKind.Can: return 1.13f;
                case BurnerKind.Cannular: return 1.08f;
                case BurnerKind.ShortAnnular: return 0.97f;
                default: return 1.00f;
            }
        }

        /// <summary>
        /// 喷口外套环：机匣收向喷管那一圈外翻的唇。
        ///
        /// 参考原版 J-404"黑豹"的尾巴：喷管不是直接从机匣上"长出来"的，
        /// 中间有一圈明显的翻边，花瓣/作动筒都从这个环里伸出去。
        /// 没有它，机匣到喷管是一段没有交代的收缩面，玩家看到的"穿模"
        /// 有很大一部分其实是这里缺了一圈边界。
        /// </summary>
        private static Profile NozzleShroudProfile(Dims m, EngineDesignData d)
        {
            Profile p = new Profile();
            float rRoot = m.HasAb ? m.RNoz * 1.05f : CoreTailR(m);
            float yStart = m.YNoz;
            p.Add(yStart + m.L * 0.030f, rRoot * 1.005f);
            p.Add(yStart + m.L * 0.012f, rRoot * 1.055f);
            p.Add(yStart - m.L * 0.004f, rRoot * 1.085f);   // 外翻唇
            p.Add(yStart - m.L * 0.010f, rRoot * 1.075f);   // 唇口厚度
            p.Add(yStart - m.L * 0.002f, rRoot * 1.030f);   // 折回来压在机匣上
            return p;
        }

        /// <summary>尾喷管：收敛锥 + 喷口外翻唇边；收扩喷管会先缩到喉道再扩出去。</summary>
        private static Profile NozzleProfile(Dims m, EngineDesignData d)
        {
            Profile p = new Profile();
            float yStart = m.YNoz;
            float rThroat = m.RNoz * 0.94f;
            float rRoot = m.HasAb ? m.RNoz * 1.05f : CoreTailR(m);
            p.Add(yStart, rRoot);
            p.Add(yStart - m.L * 0.001f, rRoot * 0.995f);

            if (d.Nozzle == NozzleKind.ConvergingDiverging || d.Nozzle == NozzleKind.Variable)
            {
                p.Add(Mathf.Lerp(yStart, m.YOut, 0.55f), rThroat);
                float rExit = NozzleExitR(m, d);
                p.Add(m.YOut + m.L * 0.01f, rExit / 1.01f);
                p.Add(m.YOut, rExit);
            }
            else
            {
                p.Add(m.YOut + m.L * 0.01f, rThroat);
                p.Add(m.YOut, rThroat * 0.99f);
            }
            return p;
        }

        /// <summary>喷口出口半径（外壁最后一点）。尾焰、内壁、花瓣都用它，别各算各的。</summary>
        private static float NozzleExitR(Dims m, EngineDesignData d)
        {
            float rThroat = m.RNoz * 0.94f;
            if (d.Nozzle == NozzleKind.ConvergingDiverging) return rThroat * 1.22f;
            if (d.Nozzle == NozzleKind.Variable) return rThroat * 1.02f;
            return rThroat * 0.99f;
        }

        /// <summary>
        /// 喷管内壁：从唇口往里收，接到排气锥根部。
        ///
        /// 以前只有外壁一层壳，从正后方看进去是**空的**——能看到对面的内表面，
        /// 这就是"转视角一半面片消失"被修好之后露出来的第二个问题：
        /// 玩家在机库里会从后面看发动机，那里必须有一条真正的内通道。
        /// </summary>
        private static Profile NozzleInnerProfile(Dims m, EngineDesignData d)
        {
            Profile p = new Profile();
            float rExit = NozzleExitR(m, d);
            // 内通道起点半径：有加力时它得比稳焰环大，否则环会穿进内壁里去
            float rRoot = m.HasAb
                ? Mathf.Min(AbRadius(m, d) * 0.92f, m.RNoz * 1.02f)
                : m.RCore * 0.56f;
            float yBack = m.YAb;                     // 内通道的"底"：加力机封在火焰稳定器前
            // 剖面仍按"从前往后"写（轴向递减），inward=true 再把法线与绕序一起翻过来
            //
            // 头两点必须同轴向位置：这是封底的一圈盘面。没有它，内通道就是一节两头通
            // 的管子——从正后方看进去能直接看到前面的地形，"喷口透明"就是这么来的。
            // 真机那儿是涡轮后机匣/加力筒的实心面，不是一个洞。
            p.Add(yBack, 0.012f);
            p.Add(yBack, rRoot * 0.96f);
            p.Add(m.YNoz, rRoot);
            p.Add(m.YNoz - m.L * 0.02f, Mathf.Lerp(rRoot, rExit, 0.35f));
            p.Add(m.YOut + m.L * 0.03f, Mathf.Lerp(rRoot, rExit, 0.78f));
            p.Add(m.YOut, rExit * 0.985f);
            return p;
        }

        /// <summary>
        /// 涡轮叶环：给每一级做一圈箍在机匣外的薄环。
        ///
        /// 涡轮级数以前只影响"喷口半径松一点"，外形上完全看不出来。
        /// 真机数级数最直观：高压涡轮一级两级的机匣很短，低压涡轮三四级的机匣一段一段往下排。
        /// 高压涡轮贴着燃烧室（粗），低压涡轮往后越走越粗（流道面积要放大）。
        /// </summary>
        private static void AddTurbineRings(List<Profile> into, Dims m, EngineDesignData d)
        {
            if (d == null) return;
            if (d.Hpt != null && d.Hpt.Stages > 0)
                AddTurbineRow(into, m, Mathf.Lerp(m.YCoreStart, m.YAb, 0.74f), d.Hpt, 1.045f, 0.019f);
            if (m.HasIpt && d.Ipt != null && d.Ipt.Stages > 0)
                AddTurbineRow(into, m, Mathf.Lerp(m.YCoreStart, m.YAb, 0.84f), d.Ipt, 1.065f, 0.017f);
            if (d.Lpt != null && d.Lpt.Stages > 0)
                AddTurbineRow(into, m, Mathf.Lerp(m.YCoreStart, m.YAb, 0.93f), d.Lpt, 1.095f, 0.021f);
        }

        private static void AddTurbineRow(List<Profile> into, Dims m, float yCenter, TurbineSpec spec,
                                          float bump, float half)
        {
            int n = Mathf.Clamp(spec.Stages, 1, 4);
            float pitch = half * 2.35f;
            float y0 = yCenter + pitch * (n - 1) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                float y = y0 - pitch * i;
                // 逐级直径：玩家填了就以他的为准（相对部件缺省直径的比值），
                // 没填时比值恒为 1，与上一版逐位相同。
                float ratio = spec.HasStageEdit()
                    ? (float)(spec.TipOf(i) / Math.Max(1e-6, spec.TipDiameter)) : 1f;
                ratio = Mathf.Clamp(ratio, 0.70f, 1.35f);
                float rBase = m.RCore * 0.99f;
                float rTop = m.RCore * bump * (n > 1 ? 1f : 1.02f) * ratio;
                Profile p = new Profile();
                p.Add(y + half, rBase);
                p.Add(y + half * 0.45f, rTop);
                p.Add(y - half * 0.45f, rTop);
                p.Add(y - half, rBase);
                into.Add(p);
            }
        }

        // 风扇盘（原来的"从轮毂到叶尖的环面"）已删除：
        // 那张面正好盖在叶片正前方，从进气口看进去就是一个实心圆盘，
        // 等于给整排叶片扣了个罩子。现在前面只有整流罩（SpinnerProfile）+ 叶片本身。

        /// <summary>整流罩：风扇前面的锥。</summary>
        private static Profile SpinnerProfile(Dims m, EngineDesignData d)
        {
            Profile p = new Profile();
            float rHub = m.RFan * (float)Mathf.Clamp((float)d.Fan.HubTipRatio, 0.2f, 0.75f);
            p.Add(m.YFan + m.L * 0.075f, 0.005f);
            p.Add(m.YFan + m.L * 0.04f, rHub * 0.55f);
            p.Add(m.YFan + m.L * 0.004f, rHub);
            p.CapEnd = false;
            return p;
        }

        /// <summary>
        /// 排气内锥。从内通道的底一直伸到出口稍后，尖端露出喷口一点点——
        /// 真机（尤其带加力的）从后面能看见那个锥尖，这也是"不是个空管子"的第二个证据。
        /// </summary>
        private static Profile TailConeProfile(Dims m, EngineDesignData d)
        {
            Profile p = new Profile();
            if (m.HasAb)
            {
                // 有加力段：锥体在加力段之前就收完，喷口里看到的是筒衬和火焰稳定器。
                // （玩家原话："喷口处有个锥体这不符合现实"——真机加力机的锥体也确实
                //   止于涡轮后机匣，不会伸进加力筒，更不会露在出口上。）
                p.Add(m.YAb + m.AbLen * 0.30f, m.RCore * 0.58f);
                p.Add(m.YAb + m.AbLen * 0.10f, m.RCore * 0.40f);
                p.Add(m.YAb, m.RCore * 0.26f);
                // 尖端正好停在加力段起点：它得把内通道封底那圈小孔堵上（半径 0.012），
                // 但不能往后多伸一公分——多伸就又是"喷口里插着一根锥子"。
                p.Add(m.YAb, 0.02f);
                return p;
            }
            p.Add(m.YOut + m.L * 0.21f, m.RCore * 0.54f);
            p.Add(m.YOut + m.L * 0.12f, m.RCore * 0.40f);
            p.Add(m.YOut, m.RCore * 0.13f);
            p.Add(m.YOut - m.L * 0.018f, 0.012f);
            return p;
        }
    }
}
