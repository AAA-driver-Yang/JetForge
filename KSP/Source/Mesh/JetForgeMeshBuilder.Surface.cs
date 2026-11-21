using System;
using System.Collections.Generic;
using System.Text;
using JetForge.Core;
using UnityEngine;

namespace JetForge
{
    // partial：几何生成在 Mesh/JetForgeMeshBuilder.cs 里；二维剖视在 Mesh/JetForgeCutawayModel.cs 里。
    public static partial class JetForgeMeshBuilder
    {
        // ------------------------------------------------------------------------------------
        // 材质、贴图、"我们有没有盖住原版外形"这些**装配期**的事情，以及一组给自检看的描述。
        //
        // 放在单独一个文件里，是因为它们与"几何长什么样"没有耦合：这里只读 part 的
        // MeshRenderer/Material，从不碰 Profile 与 MeshAcc。改贴图槽位的人不该翻到
        // 剖面线代码里去，反过来也一样。
        // ------------------------------------------------------------------------------------


        private static void HideOriginals(Part part)
        {
            Transform host = HostFor(part);
            if (host == null) return;
            // 先把话说死：**自家外形不在、或者三角面是 0，就不许关原版。**
            // 否则任何一次建网格失败（最典型的是 ALT 复制/对称放置出来的新件，
            // 它的网格是共享的、可能已经被另一边重建时退役掉）都会把零件变成
            // "原版关了、自家的还没有"——玩家看到的就是一整个透明零件。
            if (!HasOurVisual(host)) return;
            foreach (MeshRenderer r in host.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null) continue;
                // 只关原版模型，别把自家生成的东西（机身、尾焰）一起关掉。
                if (r.gameObject.name != null && r.gameObject.name.StartsWith("JetForge")) continue;
                r.enabled = false;
            }
        }

        /// <summary>把原版渲染器放回来（自家外形建失败时的退路：宁可看见占位模型，也别看见空气）。</summary>
        public static void ShowOriginals(Part part)
        {
            Transform host = HostFor(part);
            if (host == null) return;
            foreach (MeshRenderer r in host.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null) continue;
                if (r.gameObject.name != null && r.gameObject.name.StartsWith("JetForge")) continue;
                r.enabled = true;
            }
        }

        /// <summary>host 底下有没有"我们建出来的、真有三角面的"外形。</summary>
        public static bool HasOurVisual(Transform host)
        {
            if (host == null) return false;
            foreach (MeshFilter mf in host.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                string n = mf.gameObject.name;
                if (n != null && n.StartsWith("JetForge") && mf.sharedMesh.vertexCount > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 这一件手里的外形是不是**它自己的**（不是从 ALT 复制/对称克隆那边继承来的共享网格）。
        /// 判据是网格名字里盖的那个 instanceID 章，见 RefreshPartMesh。
        /// </summary>
        public static bool OwnsVisual(Part part, Transform t, string baseName)
        {
            if (part == null || t == null || baseName == null) return false;
            MeshFilter mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0) return false;
            return mf.sharedMesh.name == (baseName + "#" + part.GetInstanceID());
        }

        // ---------- 材质 ----------

        private static Material _coldMat;
        private static Material _hotMat;
        private static Material _innerMat;
        private static Material _casingMat;
        private static Material _lipMat;
        private static Material[] _engineMats;
        private static Material[] _intakeMats;

        /// <summary>
        /// 发动机的五套材质：冷端蒙皮 / 热端 / 喷管内壁 / 机匣 / 唇口。
        /// 贴图是我们自己画的（见 tools/gen_textures_hd.py，1024² 程序化生成）：
        /// 原版那两张 dds 是按它自己的 UV 岛摆的，套到我们的回转面上只会出马赛克。
        /// 材质做成静态缓存——几十台发动机共用同一份，别每次都新建。
        /// </summary>
        private static Material[] PickMaterials(Part part)
        {
            // 进气道零件用自己的三套（外罩漆面 / 抗侵蚀衬里 / 抛光唇口）。
            // 别再让它借用发动机的冷端图：那样进气道和机身那段发动机一眼就是同一张贴图，
            // 玩家分不清"这段是进气道"还是"机身包着发动机"。
            bool isIntake = false;
            try
            {
                isIntake = part != null &&
                           part.FindModuleImplementing<JetForge.KSP.ModuleJetForgeIntake>() != null;
            }
            catch (Exception) { isIntake = false; }
            if (isIntake)
            {
                if (_intakeMats == null)
                {
                    Material outer = BuildSurface(part, "JetForge_Intake_Outer", 0.14f);
                    Material duct = BuildSurface(part, "JetForge_Intake_Inner", 0.06f);
                    Material lip = BuildSurface(part, "JetForge_Lip", 0.55f);
                    // 槽位顺序 = 槽号：0 冷端(外罩) / 1 热端(压缩面) / 2 内壁(流道) / 3 机匣(外罩) / 4 唇口
                    _intakeMats = new Material[] { outer, duct, duct, outer, lip };
                    Debug.Log("[JetForge] 进气道材质就绪：" + DescribeSurface(part));
                }
                return _intakeMats;
            }
            if (_coldMat == null)
            {
                _coldMat = BuildSurface(part, "JetForge_Cold", 0.16f);
                _hotMat = BuildSurface(part, "JetForge_Hot", 0.10f);
                _innerMat = BuildSurface(part, "JetForge_Inner", 0.05f);
                _casingMat = BuildSurface(part, "JetForge_Casing", 0.09f);
                _lipMat = BuildSurface(part, "JetForge_Lip", 0.55f);
                // 材质只在编辑器里第一次造零件时才会真正生成，自检里量到的是"退路版"。
                // 打一行真话：着色器是谁的、贴图有没有挂上、环绕是不是 Repeat。
                Debug.Log("[JetForge] 材质就绪：" + DescribeSurface(part));
            }
            if (_engineMats == null)
                _engineMats = new Material[] { _coldMat, _hotMat, _innerMat, _casingMat, _lipMat };
            return _engineMats;
        }

        private static Material BuildSurface(Part part, string baseName, float shininess)
        {
            // 优先用自带法线的那个 shader。
            //
            // 量过了（自检的 shader 探针）：原版喷气机件的材质是 `KSP/Emissive/Specular`，
            // 它**没有 _BumpMap** —— 克隆它就等于把法线贴图丢掉，表面看着就是一块平板；
            // 反过来自己 new 出来的材质没有"美术调好的高光"，之前那版一片死白就是这么来的。
            // 所以：shader 用 KSP/Bumped Specular（有 _BumpMap），颜色/高光我们自己显式写死，
            // 只有在原版件本身就是这个 shader 时才抄它的属性。
            Shader wanted = Shader.Find("KSP/Bumped Specular");
            if (wanted == null) wanted = Shader.Find("KSP/Bumped");
            if (wanted == null) wanted = Shader.Find("KSP/Specular");

            Material src = null;
            if (part != null)
                foreach (MeshRenderer r in part.FindModelComponents<MeshRenderer>())
                    if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null)
                    {
                        src = r.sharedMaterial;
                        break;
                    }

            Material m;
            if (wanted != null && src != null && src.shader == wanted)
            {
                m = new Material(wanted);
                m.CopyPropertiesFromMaterial(src);
            }
            else if (wanted != null)
            {
                m = new Material(wanted);
            }
            else if (src != null)
            {
                m = new Material(src.shader);
                m.CopyPropertiesFromMaterial(src);
            }
            else
            {
                return null;
            }

            m.name = "JetForge_" + baseName;
            Texture2D tex = LoadTex(baseName, false);
            if (tex != null) m.mainTexture = tex;
            Texture2D nrm = LoadTex(baseName + "_N", true);
            if (nrm != null && m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", nrm);
                m.EnableKeyword("_NORMALMAP");
            }
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            // 原版材质可能带自发光（比如喷口的红），换到我们身上就是死白，一律清零
            if (m.HasProperty("_Emissive")) m.SetColor("_Emissive", Color.black);
            if (m.HasProperty("_RimFalloff")) m.SetFloat("_RimFalloff", 1f);
            if (m.HasProperty("_Shininess")) m.SetFloat("_Shininess", shininess);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(0.22f, 0.22f, 0.23f, 1f));
            return m;
        }

        private static Texture2D LoadTex(string name, bool normalMap)
        {
            try
            {
                Texture2D t = GameDatabase.Instance.GetTexture("JetForge/Textures/" + name, normalMap) as Texture2D;
                if (t == null) return null;
                // UV 走 0~7 圈，必须是 Repeat；Clamp 会把边缘像素铺满整台机器（一片纯色）
                t.wrapMode = TextureWrapMode.Repeat;
                t.filterMode = FilterMode.Trilinear;
                t.anisoLevel = 4;
                return t;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 供自检/调试：三角形的绕序是否与法线同向。Unity 正面是顺时针，
        /// 而我们的绕序是手写的——写反了整台机器就内翻，从外面看是透明的。
        /// 这条断言本该在"贴图"阶段一开始就写，能省掉两轮返工。
        /// </summary>
        public static string DescribeWinding(Mesh m)
        {
            if (m == null) return "绕序：没有网格";
            int total, bad;
            CountWinding(m, out total, out bad);
            return string.Format("绕序：{0} 个三角里 {1} 个与法线反向", total, bad);
        }

        /// <summary>绕序体检的单件版：分部件模式下要能一句话指出"是哪个部件反了"。</summary>
        public static int CountReversed(Mesh m)
        {
            int total, bad;
            CountWinding(m, out total, out bad);
            return bad;
        }

        private static void CountWinding(Mesh m, out int total, out int bad)
        {
            total = 0; bad = 0;
            if (m == null) return;
            Vector3[] v = m.vertices;
            Vector3[] n = m.normals;
            for (int sub = 0; sub < m.subMeshCount; sub++)
            {
                int[] t = m.GetTriangles(sub);
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    Vector3 g = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                    Vector3 s = n[t[i]] + n[t[i + 1]] + n[t[i + 2]];
                    if (g.sqrMagnitude < 1e-12f || s.sqrMagnitude < 1e-12f) continue;
                    total++;
                    if (Vector3.Dot(g, s) < 0f) bad++;
                }
            }
        }

        /// <summary>
        /// 供自检：几何是不是关于 x=0 左右对称。
        ///
        /// 对称模式（VAB 里镜像放置）出的问题有两种，长得完全不一样：
        ///   · **朝向**——镜像件的面片被 Unity 反面剔除，看起来一半透明（已用双面网格解决）；
        ///   · **位置**——镜像件整体偏了一段。后者只要几何本身关于 x=0 对称就**不可能**发生：
        ///     镜像变换把 x 变成 −x，一个 x 对称的几何映射回自己，位置不可能动。
        /// 所以量这一条能一句话分清"是我们的几何错"还是"是别的东西（羽流节点/施力点）偏"。
        /// </summary>
        public static string DescribeMirrorSymmetry(Mesh m)
        {
            if (m == null) return "镜像几何：没有网格";
            Vector3[] v = m.vertices;
            var set = new HashSet<long>();
            for (int i = 0; i < v.Length; i++)
                set.Add(KeyOf(Mathf.RoundToInt(v[i].x * 1000f), Mathf.RoundToInt(v[i].y * 1000f), Mathf.RoundToInt(v[i].z * 1000f)));
            int miss = 0;
            for (int i = 0; i < v.Length; i++)
            {
                int kx = Mathf.RoundToInt(-v[i].x * 1000f), ky = Mathf.RoundToInt(v[i].y * 1000f), kz = Mathf.RoundToInt(v[i].z * 1000f);
                // 毫米格上取整会有边界抖动（正好落半格时 x 与 −x 落到相邻两格），
                // 所以查邻居 27 格——只差 1 mm 的配对算对称。
                bool hit = false;
                for (int dx = -1; dx <= 1 && !hit; dx++)
                for (int dy = -1; dy <= 1 && !hit; dy++)
                for (int dz = -1; dz <= 1 && !hit; dz++)
                    if (set.Contains(KeyOf(kx + dx, ky + dy, kz + dz))) hit = true;
                if (!hit) miss++;
            }
            return string.Format("镜像几何：{0} 个顶点里 {1} 个找不到左右对称的同位点（0 = 镜像件不可能位置偏移）",
                v.Length, miss);
        }

        private static long KeyOf(int x, int y, int z)
        {
            long xi = (long)x + 100000L, yi = (long)y + 100000L, zi = (long)z + 100000L;
            return (xi * 1000003L + yi) * 1000003L + zi;
        }

        /// <summary>
        /// 供自检：外形在轴向是不是正好居中在零件原点上。
        ///
        /// 玩家报的"对称模式下整体向机头方向前后偏移"——如果外形本身相对零件原点有偏移，
        /// 那么镜像件（负缩放）和原件的偏移**方向相反**，一眼看就是"一台前一台后"。
        /// 这条量的是"外形中点在不在 y=0"，和对接点（±L/2）对不对得上。
        /// </summary>
        public static string DescribeCentering(Mesh m, EngineDesignData d)
        {
            if (m == null) return "居中：没有网格";
            float half = Mathf.Max((float)d.EffectiveLength, 0.4f) * 0.5f;
            float lo = m.bounds.min.y, hi = m.bounds.max.y;
            float mid = (lo + hi) * 0.5f;
            return string.Format("居中：外形 y {0:F2}~{1:F2}（长 {2:F2}）｜零件原点偏轴 {3:F2} m｜对接点 ±{4:F2}（±½L）",
                lo, hi, hi - lo, mid, half);
        }

        /// <summary>
        /// 供自检：某个 shader 支持哪些属性、关键默认值是什么。
        /// 上个版本"一片死白"就是没量这个就自己 new 材质——默认值猜错一个就过曝。
        /// </summary>
        public static string DescribeShader(string shaderName)
        {
            try
            {
                Shader s = Shader.Find(shaderName);
                if (s == null) return "shader探针：" + shaderName + " 不存在";
                Material m = new Material(s);
                string[] probe = { "_MainTex", "_BumpMap", "_Color", "_SpecColor", "_Shininess",
                                   "_Emissive", "_RimColor", "_RimFalloff", "_Opacity" };
                string has = "";
                foreach (string p in probe)
                {
                    if (!m.HasProperty(p)) { has += p + "=无 "; continue; }
                    if (p == "_MainTex" || p == "_BumpMap")
                        has += p + "=贴图 ";
                    else
                    {
                        Color c = m.GetColor(p);
                        has += string.Format("{0}=RGBA({1:F2},{2:F2},{3:F2}) ", p, c.r, c.g, c.b);
                    }
                }
                return "shader探针：" + shaderName + "｜" + has;
            }
            catch (Exception ex)
            {
                return "shader探针失败：" + shaderName + " " + ex.Message;
            }
        }

        /// <summary>
        /// 供自检/调试：材质到底成不成立。着色器找不到时上面那段会静默退回原版材质，
        /// 看着"有贴图"但其实是素的——所以这里把真正的 shader 名也打出来。
        /// </summary>
        public static string DescribeSurface(Part part = null)
        {
            try
            {
                Material m = _coldMat != null ? _coldMat : BuildSurface(part, "JetForge_Cold", 0.16f);
                if (m == null) return "材质：没造出来";
                Texture bump = m.HasProperty("_BumpMap") ? m.GetTexture("_BumpMap") : null;
                string src = part == null && _coldMat == null ? "（没有原版材质可克隆，这是退路）" : "";
                Color em = m.HasProperty("_Emissive") ? m.GetColor("_Emissive") : Color.black;
                return string.Format("材质：着色器={0}｜贴图={1}｜法线={2}｜自发光={3}｜环绕={4}{5}",
                    m.shader != null ? m.shader.name : "无",
                    m.mainTexture != null ? m.mainTexture.width + "x" + m.mainTexture.height : "无",
                    bump != null ? "有" : "无",
                    em,
                    m.mainTexture != null ? m.mainTexture.wrapMode.ToString() : "无",
                    src);
            }
            catch (Exception ex)
            {
                return "材质：失败 " + ex.Message;
            }
        }

        /// <summary>供自检/调试：当前外形用了几套材质、贴图有没有找到。</summary>
        /// <summary>
        /// 子网格审计：冷/热/内壁三套材质各分到多少三角。
        ///
        /// 分错的后果不是报错，是"那块地方颜色不对"——玩家看得出丑，看不出是代码分错还是贴图难看。
        /// 以前整台核心机算冷端，涡轮外面那截机匣跟风扇舱一个颜色；现在从燃烧室起点切开，
        /// 这条审计就是防止将来加新部件时又忘给子网格。
        /// </summary>
        /// <summary>喷口作动筒根数：收敛喷管没有花瓣，也就没有拉杆。</summary>
        public static int NozzleActuatorCount(EngineDesignData d)
        {
            return ActuatorCount(d);
        }

        public static string DescribeSubmeshes(Mesh m)
        {
            if (m == null) return "子网格：没有网格";
            string[] role = { "冷端", "热端", "内壁" };
            int[] cnt = new int[m.subMeshCount];
            int total = 0;
            for (int i = 0; i < m.subMeshCount; i++)
            {
                cnt[i] = m.GetTriangles(i).Length / 3;
                total += cnt[i];
            }
            StringBuilder sb = new StringBuilder("子网格：");
            for (int i = 0; i < cnt.Length; i++)
            {
                if (i > 0) sb.Append("｜");
                sb.AppendFormat("{0} {1} 三角（{2:P0}）",
                    i < role.Length ? role[i] : "第" + i,
                    cnt[i], total > 0 ? (double)cnt[i] / total : 0.0);
            }
            return sb.ToString();
        }

        /// <summary>子网格三角数，供断言用；缺失的返回 0。</summary>
        public static int SubmeshTriangles(Mesh m, int submesh)
        {
            if (m == null || submesh < 0 || submesh >= m.subMeshCount) return 0;
            return m.GetTriangles(submesh).Length / 3;
        }

        public static string DescribeMaterials(Part part)
        {
            try
            {
                // 七套都挨个报尺寸：哪一套没被打包进来（缺）比"颜色不对"难查得多，
                // 尤其分件之后，缺一张就是某个部件变成默认白模。
                string cold = TexSize("JetForge_Cold");
                string hot = TexSize("JetForge_Hot");
                string inner = TexSize("JetForge_Inner");
                string casing = TexSize("JetForge_Casing");
                string lip = TexSize("JetForge_Lip");
                string iOut = TexSize("JetForge_Intake_Outer");
                string iIn = TexSize("JetForge_Intake_Inner");
                int ok = CountPresent("JetForge_Cold", "JetForge_Hot", "JetForge_Inner",
                                      "JetForge_Casing", "JetForge_Lip",
                                      "JetForge_Intake_Outer", "JetForge_Intake_Inner");
                return string.Format(
                    "贴图：冷端 {0}｜热端 {1}｜内壁 {2}｜机匣 {3}｜唇口 {4}｜进气道外罩 {5}｜进气道内壁 {6}｜七套齐 {7}｜世界平铺 {8:F2} m",
                    cold, hot, inner, casing, lip, iOut, iIn, ok == 7 ? "对" : "不对", UvWorldSize);
            }
            catch (Exception ex)
            {
                return "贴图：读取失败 " + ex.Message;
            }
        }

        private static string TexSize(string baseName)
        {
            try
            {
                Texture t = GameDatabase.Instance.GetTexture("JetForge/Textures/" + baseName, false);
                Texture n = GameDatabase.Instance.GetTexture("JetForge/Textures/" + baseName + "_N", true);
                if (t == null) return "缺";
                return string.Format("{0}²{1}", t.width, n != null ? " +N" : " 无法线");
            }
            catch (Exception)
            {
                return "缺";
            }
        }

        private static int CountPresent(params string[] names)
        {
            int n = 0;
            for (int i = 0; i < names.Length; i++)
                try
                {
                    if (GameDatabase.Instance.GetTexture("JetForge/Textures/" + names[i], false) != null) n++;
                }
                // 探测豁免：贴图探测：数出来的只是「有几张在」，少一张不影响出图
                catch (Exception) { }
            return n;
        }
    }
}
