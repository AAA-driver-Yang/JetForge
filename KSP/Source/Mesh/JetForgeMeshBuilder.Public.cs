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
        // ---------- 对外 ----------

        /// <summary>分部件生成，供设置页做"先分部件、再合并"的检视。</summary>
        /// <summary>
        /// 离心压气机是"盘"，轴流是"鼓"。同一个核心机剖面下，离心级会鼓出一圈——
        /// 这不是装饰：真机的离心叶轮直径就是比同级轴流大，玩家一眼能看出形式差别。
        /// </summary>
        private static void AddCompressorDisks(List<Profile> into, Dims m, EngineDesignData d)
        {
            if (d == null) return;
            int lpc = d.Lpc != null && d.Lpc.Kind == CompressorKind.Centrifugal ? d.Lpc.Stages : 0;
            int hpc = d.Hpc != null && d.Hpc.Kind == CompressorKind.Centrifugal ? d.Hpc.Stages : 0;
            if (lpc <= 0 && hpc <= 0) return;
            if (lpc > 0) AddDiskRow(into, m, Mathf.Lerp(m.YCoreStart, m.YOut, 0.18f), d.Lpc);
            if (hpc > 0) AddDiskRow(into, m, Mathf.Lerp(m.YCoreStart, m.YOut, 0.34f), d.Hpc);
        }

        private static void AddDiskRow(List<Profile> into, Dims m, float yCenter, CompressorSpec spec)
        {
            int n = Mathf.Clamp(spec.Stages, 1, 4);
            float half = m.L * 0.026f;
            float pitch = half * 2.4f;
            float y0 = yCenter + pitch * (n - 1) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                float y = y0 - pitch * i;
                float ratio = spec.HasStageEdit()
                    ? (float)(spec.TipOf(i) / Math.Max(1e-6, spec.TipDiameter)) : 1f;
                ratio = Mathf.Clamp(ratio, 0.70f, 1.35f);
                float r0 = m.RCore * 0.99f;
                float r1 = m.RCore * (n > 1 ? 1.13f : 1.16f) * ratio;
                Profile p = new Profile();
                p.Add(y + half, r0);
                p.Add(y + half * 0.55f, r1);
                p.Add(y - half * 0.55f, r1);
                p.Add(y - half, r0);
                into.Add(p);
            }
        }

        public static List<Piece> BuildComponents(EngineDesignData d, bool flip = false)
        {
            Dims m = Measure(d);
            List<Piece> list = new List<Piece>();

            list.Add(Single(Loc.T("短舱", "nacelle"), NacelleProfile(m), SubCold, false, flip));
            // 风扇盘撤了：它从轮毂一直盖到叶尖，正对进气口就是一块实心圆盘。
            if (m.HasFan) list.Add(Single(Loc.T("整流罩", "cowling"), SpinnerProfile(m, d), SubCold, false, flip));
            {
                Profile coreF, coreR;
                SplitProfile(CoreProfile(m, d), CoreSplitY(m), out coreF, out coreR);
                list.Add(Single(Loc.T("核心机", "core"), coreF, SubCold, false, flip));
                if (coreR != null) list.Add(Single(Loc.T("核心机后段", "core aft section"), coreR, SubHot, false, flip));
                list.Add(Single(Loc.T("机匣对接箍", "casing joint ring"), CasingJointProfile(m, d), SubCasing, false, flip));
            }
            list.Add(Single(Loc.T("机匣箍", "casing band"), NacelleBandProfile(m), SubCasing, false, flip));
            list.Add(Single(Loc.T("进气唇口", "intake lip"), IntakeLipProfile(m, d), SubLip, false, flip));
            // 中心锥/斜板：锥体与斜板才有，独立成件（玩家拆开看能看到它）
            if (IntakeCenterBodyProfile(m, d).Points.Count >= 2)
                list.Add(Single(Loc.T("进气中心锥", "intake centre cone"), IntakeCenterBodyProfile(m, d), SubHot, false, flip));
            // 气道内壁撤掉了：有叶片之后，那层内壁挡在进气口前面，等于给风扇盖个罩子。
            // 真机从进气口看进去看到的就是叶片本身，不是一圈管子。
            if (m.HasFan) list.Add(SingleQuads(Loc.T("风扇叶片", "fan blades"), delegate (MeshAcc a) { AddFanBlades(a, m, d, flip); }, SubCold));
            // 外涵出口导叶（OGV）也撤了：玩家说的是"发动机中部叶片不要"，
            // 那圈叶片在短舱和核心机之间的缝里，远看就是一排多余的板子。
            list.Add(Single(Loc.T("燃油总管", "fuel manifold"), FuelManifoldProfile(m, d), SubCold, false, flip));
            list.Add(SingleQuads(Loc.T("燃油喷嘴", "fuel injectors"), delegate (MeshAcc a) { AddFuelNozzles(a, m, d, flip); }, SubCold));
            List<Profile> disks = new List<Profile>();
            AddCompressorDisks(disks, m, d);
            foreach (Profile dp in disks) list.Add(Single(Loc.T("离心盘", "centrifugal impeller"), dp, SubCold, false, flip));
            List<Profile> turb = new List<Profile>();
            AddTurbineRings(turb, m, d);
            foreach (Profile tp in turb) list.Add(Single(Loc.T("涡轮", "turbine"), tp, SubHot, false, flip));
            // 加力段：独立筒体 + 筒里的火焰稳定器（只在装了加力时存在）
            if (m.HasAb && m.AbLen > 0f)
            {
                list.Add(Single(Loc.T("加力筒体", "afterburner barrel"), AbDuctProfile(m, d), SubHot, false, flip));
                List<Profile> holders = new List<Profile>();
                AddAbFlameHolders(holders, m, d);
                foreach (Profile hp in holders) list.Add(Single(Loc.T("稳焰环", "flame holder ring"), hp, SubHot, false, flip));
            }
            list.Add(Single(Loc.T("喷口外套", "nozzle shell"), NozzleShroudProfile(m, d), SubHot, false, flip));
            list.Add(Single(Loc.T("尾喷管", "exhaust nozzle"), NozzleProfile(m, d), SubHot, false, flip));
            list.Add(Single(Loc.T("喷管内壁", "nozzle inner wall"), NozzleInnerProfile(m, d), SubInner, true, flip));
            list.Add(Single(Loc.T("排气锥", "exhaust cone"), TailConeProfile(m, d), SubHot, false, flip));
            if (m.HighBypass) list.Add(Single(Loc.T("外涵出口", "bypass outlet"), BypassDuctRing(m), SubCold, false, flip));
            // 细节件单独成件：玩家拆开看的时候，"叶片""法兰""附件"各是一坨，而不是糊在核心机上
            List<Profile> flanges = DetailFlangeProfiles(m, d);
            if (flanges.Count > 0) { foreach (Profile fp in flanges) list.Add(Single(Loc.T("机匣法兰", "casing flange"), fp, SubCasing, false, flip)); }
            if (JetForge.Core.DetailSpec.RotorBlades(d.Detail) > 0
                || JetForge.Core.DetailSpec.TurbineBlades(d.Detail) > 0)
            {
                list.Add(SingleQuads(Loc.T("叶片", "blades"), delegate (MeshAcc a) { AddBladeDetail(a, m, d, flip); }, SubCold));
            }
            if (JetForge.Core.DetailSpec.Accessories(d.Detail))
            {
                list.Add(SingleQuads(Loc.T("附件", "accessories"), delegate (MeshAcc a) { AddAccessoryDetail(a, m, d, flip); }, SubCold));
            }
            if (JetForge.Core.DetailSpec.Igniters(d.Detail))
            {
                list.Add(SingleQuads(Loc.T("点火器", "igniter"), delegate (MeshAcc a) { AddIgniterDetail(a, m, d, flip); }, SubHot));
            }
            if (ActuatorCount(d) > 0)
            {
                list.Add(SingleQuads(Loc.T("喷口作动筒", "nozzle actuator"), delegate (MeshAcc a) { AddNozzleActuators(a, m, d, flip); }, SubHot));
            }
            return list;
        }

        private static Piece Single(string name, Profile p, int submesh = SubCold, bool inward = false, bool flip = false)
        {
            MeshAcc acc = new MeshAcc();
            acc.Revolve(p, Segments, submesh, inward, flip);
            return new Piece { Name = name, Mesh = acc.ToMesh("JF_" + name) };
        }

        /// <summary>不是回转体的部件（叶片、导叶、喷嘴支管）：交给调用方往累加器里塞四边形。</summary>
        private static Piece SingleQuads(string name, Action<MeshAcc> fill, int submesh = SubCold)
        {
            MeshAcc acc = new MeshAcc();
            fill(acc);
            return new Piece { Name = name, Mesh = acc.ToMesh("JF_" + name) };
        }

        /// <summary>
        /// 细部清单，供自检。带每个部件的三角数——"某个部件是空的"这种错
        /// 只有把数字打出来才看得见（空网格不报错，只是看不见）。
        /// </summary>
        public static string DescribeDetails(EngineDesignData d)
        {
            if (d == null) return "细部：没有设计";
            TakeWindingFixes();
            List<Piece> ps = BuildComponents(d);
            Dims m = Measure(d);
            StringBuilder sb = new StringBuilder();
            int tris = 0;
            for (int i = 0; i < ps.Count; i++)
            {
                int t = ps[i].Mesh != null ? ps[i].Mesh.triangles.Length / 3 : 0;
                tris += t;
                if (i > 0) sb.Append(" / ");
                int rev = CountReversed(ps[i].Mesh);
                sb.Append(ps[i].Name).Append(" ").Append(t);
                if (rev > 0) sb.Append("(反").Append(rev).Append(")");
            }
            int blades = m.HasFan ? BladesOf(d) : 0;
            return string.Format("细部：分部件 {0} 个、三角 {1}（出口处掰正绕序 {2} 个）｜风扇叶片 {3} 片｜燃油喷嘴 {4} 根｜{5}",
                ps.Count, tris, TakeWindingFixes(), blades,
                (d.Burner != null && d.Burner.CanCount > 0 && BurnerShellFactor(d) > 1.05f)
                    ? Mathf.Clamp(d.Burner.CanCount, 4, 18) : 8,
                sb.ToString());
        }

        /// <summary>
        /// 尺寸比例体检：把"这台机器看着像不像真机"变成几个比值。
        ///
        /// 这些都是玩家一眼能看出别扭、但代码里没有任何一处会报错的东西：
        /// 整机细长得像根棍、核心机粗得像个球、加力段占掉半台机器、喷口比风扇还大。
        /// 参考区间来自真机（见文档 §66 的表），不是拍脑袋定的。
        /// </summary>
        public static string DescribeProportions(EngineDesignData d)
        {
            if (d == null) return "比例：没有设计";
            Dims m = Measure(d);
            // 参考口径取"拿尺子能量到的最大外径"，不是风扇直径——涡喷根本没风扇，
            // 拿核心机当参考会把 L/D 算出 6.8 这种数字，只不过是把"细"换了个说法。
            float dia = 2f * Mathf.Max(m.RNac, Mathf.Max(m.RFan, m.RCore));
            float ld = m.L / Mathf.Max(dia, 0.01f);
            float coreRatio = m.RCore / Mathf.Max(dia * 0.5f, 0.01f);
            float abFrac = m.L > 0f ? m.AbLen / m.L : 0f;
            float nozRatio = (m.RNoz * 2f) / Mathf.Max(dia, 0.01f);
            return string.Format("比例：整机 L/D {0:F2}｜最大外径 {1:F2} m｜核心机/外径 {2:F2}｜喷口/外径 {3:F2}｜加力段占长 {4:P0}｜叶片 {5} 片{6}",
                ld, dia, coreRatio, nozRatio, abFrac, BladesOf(d),
                d.Fan != null && d.Fan.BladeCount > 0 ? "(手动)" : "(自动)");
        }

        /// <summary>合并成一个整件网格。</summary>
        public static Mesh Build(EngineDesignData d, bool flip = false)
        {
            Dims m = Measure(d);
            List<List<Profile>> groups = new List<List<Profile>>();
            List<string> names = new List<string>();
            List<int> subs = new List<int>();

            AddGroup(groups, names, subs, Loc.T("短舱", "nacelle"), SubCold, NacelleProfile(m));
            AddGroup(groups, names, subs, Loc.T("机匣箍", "casing band"), SubCasing, NacelleBandProfile(m));
            // 唇口留着，内壁撤了——有叶片之后内壁只会挡住叶片
            AddGroup(groups, names, subs, Loc.T("进气唇口", "intake lip"), SubLip, IntakeLipProfile(m, d));
            if (IntakeCenterBodyProfile(m, d).Points.Count >= 2)
                AddGroup(groups, names, subs, Loc.T("进气中心锥", "intake centre cone"), SubHot, IntakeCenterBodyProfile(m, d));
            if (m.HasFan)
            {
                AddGroup(groups, names, subs, Loc.T("整流罩", "cowling"), SubCold, SpinnerProfile(m, d));
            }
            {
                Profile coreF, coreR;
                SplitProfile(CoreProfile(m, d), CoreSplitY(m), out coreF, out coreR);
                AddGroup(groups, names, subs, Loc.T("核心机", "core"), SubCold, coreF);
                // 燃烧室与涡轮外面的机匣会烧出回火色，走热端贴图
                if (coreR != null) AddGroup(groups, names, subs, Loc.T("核心机后段", "core aft section"), SubHot, coreR);
                AddGroup(groups, names, subs, Loc.T("机匣对接箍", "casing joint ring"), SubCasing, CasingJointProfile(m, d));
            }
            // 每级之间一道箍（只有高档才有）：真机压气机机匣就是一级一段、靠法兰连起来
            List<Profile> flanges = DetailFlangeProfiles(m, d);
            if (flanges.Count > 0)
            {
                AddGroup(groups, names, subs, Loc.T("机匣法兰", "casing flange"), SubCasing, flanges.ToArray());
            }
            AddGroup(groups, names, subs, Loc.T("燃油总管", "fuel manifold"), SubCold, FuelManifoldProfile(m, d));
            List<Profile> disks = new List<Profile>();
            AddCompressorDisks(disks, m, d);
            if (disks.Count > 0)
            {
                List<Profile> dg = new List<Profile>(disks);
                AddGroup(groups, names, subs, Loc.T("离心盘", "centrifugal impeller"), SubCold, dg.ToArray());
            }
            // 涡轮段接着燃烧室往后，它自己也是热的：环箍用热端贴图
            List<Profile> turb = new List<Profile>();
            AddTurbineRings(turb, m, d);
            if (turb.Count > 0)
            {
                List<Profile> tg = new List<Profile>(turb);
                AddGroup(groups, names, subs, Loc.T("涡轮", "turbine"), SubHot, tg.ToArray());
            }
            // 加力段：独立筒体 + 火焰稳定器。它比核心机粗一圈，也是热的
            if (m.HasAb && m.AbLen > 0f)
            {
                AddGroup(groups, names, subs, Loc.T("加力筒体", "afterburner barrel"), SubHot, AbDuctProfile(m, d));
                List<Profile> holders = new List<Profile>();
                AddAbFlameHolders(holders, m, d);
                if (holders.Count > 0)
                {
                    List<Profile> hg = new List<Profile>(holders);
                    AddGroup(groups, names, subs, Loc.T("稳焰环", "flame holder ring"), SubHot, hg.ToArray());
                }
            }
            // 喷管外壁、内壁、排气锥都算热端——它们直接看着燃气
            AddGroup(groups, names, subs, Loc.T("喷口外套", "nozzle shell"), SubHot, NozzleShroudProfile(m, d));
            AddGroup(groups, names, subs, Loc.T("尾喷管", "exhaust nozzle"), SubHot, NozzleProfile(m, d));
            AddGroup(groups, names, subs, Loc.T("喷管内壁", "nozzle inner wall"), SubInner, NozzleInnerProfile(m, d));
            AddGroup(groups, names, subs, Loc.T("排气锥", "exhaust cone"), SubHot, TailConeProfile(m, d));
            // 高涵道才有外涵出口那圈环形封板
            if (m.HighBypass) AddGroup(groups, names, subs, Loc.T("外涵出口", "bypass outlet"), SubCold, BypassDuctRing(m));

            MeshAcc acc = new MeshAcc();
            for (int i = 0; i < groups.Count; i++)
            {
                bool inward = names[i] == Loc.T("喷管内壁", "nozzle inner wall");
                foreach (Profile p in groups[i])
                    acc.Revolve(p, Segments, subs[i], inward, flip);
            }
            // 叶片与喷嘴支管不是回转体，直接在同一个累加器上补四边形。
            // 外涵导叶不再生成（玩家要求撤掉中部叶片）。
            if (m.HasFan) AddFanBlades(acc, m, d, flip);
            AddFuelNozzles(acc, m, d, flip);
            // 程序化细节：压气机/涡轮叶片排、附件机匣与管路、点火器凸台。
            // 全部由 DetailLevel 控制，低档一台都不加。
            AddBladeDetail(acc, m, d, flip);
            AddNozzleActuators(acc, m, d, flip);
            AddAccessoryDetail(acc, m, d, flip);
            AddIgniterDetail(acc, m, d, flip);

            // 花瓣不并进来：它是会动的（可变喷管收放），单独一个网格由 UpdatePetals 重建

            Mesh mesh = acc.ToMesh("JetForgeBody");
            // 子网格分组结果记录在名字里，供调试与后续材质分配使用
            mesh.name = "JetForgeBody[" + string.Join("|", names.ToArray()) + "]";
            return mesh;
        }

        /// <summary>
        /// 零件是不是镜像出来的（对称模式）。镜像变换的行列式是负的：
        /// 投影之后我们手算的绕序会被翻过来，Unity 就把它当背面剔掉——
        /// 症状是"正对着看的那台是空的，对称出来的那台才是实的"。
        /// 处理办法是按面积符号判断，再生成一份绕序相反的同形网格。
        /// </summary>
        public static bool IsMirrored(Part part)
        {
            if (part == null) return false;
            Vector3 s = part.transform.lossyScale;
            return s.x * s.y * s.z < 0f;
        }

        public static string DescribeSymmetry(Part part)
        {
            if (part == null) return "对称：没有零件";
            Vector3 s = part.transform.lossyScale;
            int n = part.symmetryCounterparts != null ? part.symmetryCounterparts.Count : 0;
            return string.Format("对称：对手 {0} 台｜lossyScale={1:F2}/{2:F2}/{3:F2}｜镜像={4}",
                                 n, s.x, s.y, s.z, IsMirrored(part) ? "是" : "否");
        }

        private static void AddGroup(List<List<Profile>> g, List<string> names, List<int> subs,
                                     string name, int submesh, params Profile[] ps)
        {
            names.Add(name);
            subs.Add(submesh);
            g.Add(new List<Profile>(ps));
        }
    }
}
