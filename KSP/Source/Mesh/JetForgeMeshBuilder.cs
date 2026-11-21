using System;
using System.Collections.Generic;
using System.Text;
using JetForge.Core;
using UnityEngine;

namespace JetForge
{
    /// <summary>
    /// 按设计参数程序化生成发动机外形。
    ///
    /// 原则：**一台发动机一个网格**。风扇直径、核心机直径、燃烧室长度、涡轮级数、
    /// 有没有加力、喷管是固定收敛还是收扩——这些都直接改外形，所以不能靠贴图假装，
    /// 必须真的把几何算出来。
    ///
    /// 做法：所有部件都是"回转体"——给定一条 (轴向位置, 半径) 剖面线，绕轴转一圈就是面。
    /// 分部件各自生成，再合进一个 Mesh（顶点/三角/法线/UV 顺序拼接）。这样设置页里能
    /// 分开检视每个部件，装到船上时又是一个整件。
    /// </summary>
    // partial：二维剖视模型在 Mesh/JetForgeCutawayModel.cs 里。
    // 它复用这里的 Measure() 与各段剖面——共用同一份私有实现，
    // 才不会有"剖视图上的比例"和"模型上的比例"两套数。
    public static partial class JetForgeMeshBuilder
    {

        private static bool _stampWarned;

        /// <summary>
        /// 网格归属章打不上时报一次。归属章（<c>名字#instanceID</c>）是 ALT 复制/对称件
        /// "整件透明"那次的修法本体：自愈靠它判断"我这份网格是不是借了原件的"。
        /// 打不上不会崩，但换成整件透明这件事，玩家完全没法从现象反推到原因，所以说一次。
        /// </summary>
        private static void StampWarnOnce(Exception e)
        {
            if (_stampWarned) return;
            _stampWarned = true;
            Debug.LogWarning("[JetForge] 网格归属章没打上：" + e.GetType().Name + "：" + e.Message
                + "——ALT 复制/对称出来的件可能共用原件的网格（表现为整件透明）");
        }
        public const int Segments = 48;

        /// <summary>
        /// 一张贴图在世界里代表多少米见方。UV 按世界尺寸平铺，不按 0..1 拉满：
        /// 拉满的话，一台 3 m 长的发动机和一台 0.8 m 长的发动机用同一张图，
        /// 前者的面板线会被拉成后者的一半宽——同一个设计换个尺寸就像换了台机器。
        /// 这个数必须与 tools/gen_textures.py 里的 TEX_WORLD 一致。
        /// </summary>
        public const float UvWorldSize = 0.5f;

        /// <summary>
        /// 子网格序号：0 = 冷端（短舱/风扇/核心机匣），1 = 热端（喷管外壁/涡轮/排气锥），
        /// 2 = 喷管内壁（从喷口看进去的那一圈，单独一张更黑的贴图）。
        /// 分开是因为这三处的"脏法"完全不同：机匣是擦过但干净，喷口外壁是烧变色，内壁是一直熏着的。
        /// </summary>
        public const int SubCold = 0;
        public const int SubHot = 1;
        public const int SubInner = 2;
        // 机匣（砂面铸铁 + 螺栓圈 + 油迹）与进气唇口（抛光钢 + 车削纹）单开两槽。
        //
        // 为什么值得多开两个子网格：以前"机匣箍、机匣对接箍、机匣法兰、进气唇口、短舱、核心机"
        // 全是同一张冷端蒙皮图，整台机器糊成一根灰管子，玩家看不出这台机器有几层结构。
        // 子网格多两个的代价是索引多存几 KB，换来的是"一眼分得清哪段是壳、哪段是箍、哪圈是唇口"。
        public const int SubCasing = 3;
        public const int SubLip = 4;

        /// <summary>
        /// ToMesh 在出口处掰正的三角累计数。这个数字说明"有多少生成器的绕序写反了"，
        /// 自检把它打出来：涨到几百说明新加的部件手性不对（虽然图上看不出来）。
        /// </summary>
        public static int WindingFixes;
        public static int TakeWindingFixes() { int n = WindingFixes; WindingFixes = 0; return n; }

        public sealed class Piece
        {
            public string Name;
            public Mesh Mesh;
        }

        /// <summary>剖面线：每个点 = (轴向位置 y, 半径 r)。y 从 +半长（进气口）到 -半长（喷口）。</summary>
        public sealed class Profile
        {
            public readonly List<Vector2> Points = new List<Vector2>();
            public bool CapStart, CapEnd;
            public Profile Add(float y, float r) { Points.Add(new Vector2(y, Mathf.Max(r, 0.0005f))); return this; }
        }

        // ---------- 外形尺寸的推导 ----------

        internal struct Dims
        {
            public float L, YIn, YOut;      // 长度与两端
            public float RFan, RCore, RIn;  // 风扇半径、核心机半径、进气口半径
            /// <summary>短舱外皮最粗处半径（玩家填的「短舱直径」的一半，带下限）。</summary>
            public float RNac;
            public float YFan;              // 风扇所在轴向位置
            public float YCoreStart;        // 核心机开始位置
            public bool HasFan, HasAb, HasIpt;
            public float RNoz;
            /// <summary>核心机匣尾端（喷管根部）半径。</summary>
            public float RTail;
            /// <summary>高涵道（BPR &gt; 2.5）：短而粗的风扇舱 + 露在外面的细核心机，跟军机那种"一根筒子"是两回事。</summary>
            public bool HighBypass;
            public float YBypass;           // 外涵喷口（风扇舱后缘）位置
            public float RBypass;           // 外涵喷口半径
            /// <summary>加力段长度（涡轮出口到喷管之间那段独立筒体）。没有加力时为 0。</summary>
            public float AbLen;
            /// <summary>加力段起点（涡轮出口）。没有加力时等于喷管起点。</summary>
            public float YAb;
            /// <summary>喷管起点。有加力时它被加力段往后顶。</summary>
            public float YNoz;
        }

        /// <summary>
        /// 站位表 → 网格用的 Dims。**这里不再做任何算术**：所有比例都在 Core.EngineGeometry 里，
        /// 剖视图、网格、质量/造价读的是同一份数。以前这套公式只活在网格里，
        /// 界面想画个剖面就得抄一遍——抄的那份迟早跟这份对不上。
        /// </summary>
        private static Dims Measure(EngineDesignData d)
        {
            EngineStations s = EngineGeometry.Of(d);
            Dims m = new Dims();
            m.L = (float)s.L;
            m.YIn = (float)s.YIn;
            m.YOut = (float)s.YOut;
            m.RFan = (float)s.RFan;
            m.RCore = (float)s.RCore;
            m.RIn = (float)s.RIn;
            m.RNac = (float)s.RNac;
            m.RNoz = (float)s.RNoz;
            m.RTail = (float)s.RTail;
            m.YFan = (float)s.YFan;
            m.YCoreStart = (float)s.YCoreStart;
            m.YBypass = (float)s.YBypass;
            m.RBypass = (float)s.RBypass;
            m.AbLen = (float)s.AbLen;
            m.YAb = (float)s.YAb;
            m.YNoz = (float)s.YNoz;
            m.HasFan = s.HasFan;
            m.HasAb = s.HasAb;
            m.HasIpt = s.HasIpt;
            m.HighBypass = s.HighBypass;
            return m;
        }

        // ---------- 对外暴露的喷口几何（尾焰要挂在喷口上） ----------

        public static float NozzleExitY(EngineDesignData d)
        {
            return Measure(d).YOut;
        }

        public static float NozzleExitRadius(EngineDesignData d)
        {
            Dims m = Measure(d);
            return NozzleExitR(m, d);
        }

        /// <summary>羽流该有多粗。
        ///
        /// 干态涡轮风扇的尾迹是"风扇那股冷气流包着核心那根热柱"，从后面看最外圈是
        /// 外涵喷口，所以干态用风扇舱后缘半径；一旦点加力，火光只是核心那一根，
        /// 直径必须跟着**核心/加力段出口**走——以前两种情况都拿核心喷口算，
        /// 大涵道机器干态偏细；而如果反过来一律用外径，加力就会烧成一整个大圆圈。
        /// </summary>
        public static float PlumeRadius(EngineDesignData d, bool ab)
        {
            if (d == null) return 0f;
            Dims m = Measure(d);
            if (ab) return NozzleExitR(m, d);
            if (m.HighBypass && m.RBypass > 0f) return m.RBypass * 0.98f;
            return NozzleExitR(m, d);
        }

        /// <summary>羽流不许越过花瓣：可变喷管收起来时，实际出口比唇口还小。</summary>
        public static float PlumeRadiusLimited(EngineDesignData d, bool ab, float openFrac)
        {
            float r = PlumeRadius(d, ab);
            if (d == null || d.Nozzle == NozzleKind.Converging) return r;
            Dims m = Measure(d);
            // 花瓣装在核心喷管上：只有从核心出来的那根羽流受它约束。
            // 大涵道干态的羽流是外涵那股冷气流，跟核心喷管的花瓣没有关系。
            if (!ab && m.HighBypass) return r;
            float petal = PetalExitFactor(openFrac, NozzleExitR(m, d));
            // 0.94 而不是 0.96：花瓣之间的缝里也能看到一点羽流，玩家捡的就是这条缝。
            return Mathf.Min(r, r * petal * 0.94f);
        }

        /// <summary>进气唇口内缘半径（正面看进去，这是第一个会挡住叶尖的东西）。</summary>
        public static float IntakeThroatRadius(EngineDesignData d)
        {
            Dims m = Measure(d);
            // 玩家填了捕获直径，喉道就是几何里那个真喉道（可能收得很狠 → 会压住叶尖，自检会报）。
            // 没填就还是老口径 0.968RIn，老存档的外形与读数一分不动。
            if (d != null && d.Intake != null && d.Intake.CaptureDiameter > 0.0)
                return (float)JetForge.Core.IntakeGeometry.Of(d).RThroat;
            return m.RIn * 0.968f;
        }

        /// <summary>风扇叶尖半径。</summary>
        public static float FanTipRadius(EngineDesignData d)
        {
            Dims m = Measure(d);
            return m.HasFan ? m.RFan * 0.962f : 0f;
        }

        /// <summary>加力段实际长度（0 = 没有加力）。</summary>
        public static float AbLengthOf(EngineDesignData d)
        {
            return Measure(d).AbLen;
        }

        /// <summary>喷管起点（加力机的喷管被加力段往后顶）。</summary>
        public static float NozzleStartY(EngineDesignData d)
        {
            return Measure(d).YNoz;
        }

        /// <summary>排气锥尖端轴向位置。它必须比加力段起点更靠前，否则从喷口能看见锥子。</summary>
        public static float TailConeTipY(EngineDesignData d)
        {
            Dims m = Measure(d);
            return m.HasAb ? m.YAb : m.YOut - m.L * 0.018f;
        }

        /// <summary>
        /// 花瓣式喷口：可变/收扩喷管的出口一圈外翻叶片（参考原版 J-404"黑豹"的喷口）。
        ///
        /// 这不是纯装饰：固定收敛喷管真的没有这一圈（它就是个锥），
        /// 可变喷管必须靠这圈叶片撑开喉道——几何上把设计差别摆出来，
        /// 比在设置页写一行"可变喷管"更直观。
        /// </summary>
        private const int PetalCount = 16;   // 缺省值；实际片数由 `NozzleVisual.CountOf(d)` 给（0 = 用它）

        /// <summary>按设计取花瓣数（0 = 缺省的 16 片）。</summary>
        public static int PetalsOf(EngineDesignData d)
        {
            return Core.NozzleVisual.CountOf(d);
        }

        /// <summary>
        /// 花瓣喷口的开口程度 → 出口半径的那条映射。
        /// 0 = 收到底（低油门/干态收油门时喉道最小），1 = 开到最大（加力全开）。
        /// 真机可调喷管就是这么动的：A8 随油门和加力开关变，叶片跟着张开。
        /// </summary>
        public static float PetalExitFactor(float openFrac)
        {
            return PetalExitFactor(openFrac, 1.0f);
        }

        /// <summary>同一个几何的唯一出口：羽流要让开花瓣时调这里，别自己再写一条曲线。</summary>
        public static float PetalExitFactor(float openFrac, float rExit)
        {
            return (float)Core.NozzleVisual.ExitFactor(openFrac, rExit);
        }

        private static void AddNozzlePetals(MeshAcc acc, Dims m, EngineDesignData d, bool flip, float openFrac,
                                           float pitchDeg, float yawDeg)
        {
            if (d.Nozzle == NozzleKind.Converging) return;
            float rExit = NozzleExitR(m, d);
            // 铰链环在出口唇前一点：真机叶片是挂在环形作动环上的，不是从唇口"长"出来的。
            float y0 = m.YOut + m.L * 0.030f;
            float r0 = rExit * 0.975f;
            // 片数与长度都是设计参数（0/1.0 = 老口径 16 片、0.48·R）。
            int petals = PetalsOf(d);
            float len = (float)Core.NozzleVisual.PetalLength(rExit, d.PetalLengthFrac);
            float tilt = (float)(Core.NozzleVisual.TiltOf(openFrac) * Math.PI / 180.0);
            float y1 = y0 - len * Mathf.Cos(tilt);
            float r1 = r0 + len * Mathf.Sin(tilt);
            float step = Mathf.PI * 2f / Mathf.Max(4, petals);
            float halfArc = step * 0.43f;          // 留缝，才看得出是一片一片的
            // 叶片要**有厚度**。上一版是一片零厚度的四边形，双面网格一开，从侧面看就是
            // 一张纸——玩家说的"像被撕开的铁皮"就是这个。真叶片是 2~4 mm 的耐热合金板，
            // 按喷口尺度取 1.8% 出口半径（0.26 m 的喷口 → 4.7 mm）。
            float thick = Mathf.Max(0.002f, rExit * 0.018f);
            // 三个轴向站：铰链、中段、梢部。中段微鼓，叶片才有弧度（直板看着像刀片）。
            const int Stations = 3;
            for (int i = 0; i < petals; i++)
            {
                float a = step * i;
                float a0 = a - halfArc, a1 = a + halfArc;
                Vector3[] outer = new Vector3[Stations * 2];
                Vector3[] inner = new Vector3[Stations * 2];
                Vector3[] nOut = new Vector3[Stations * 2];
                for (int s = 0; s < Stations; s++)
                {
                    float t = s / (float)(Stations - 1);
                    float bulge = 1f + 0.012f * Mathf.Sin(t * Mathf.PI);   // 中段 1.2% 外鼓
                    float ys = Mathf.Lerp(y0, y1, t);
                    float rs = Mathf.Lerp(r0, r1, t) * bulge;
                    Vector3 pA = RingPoint(ys, rs, a0);
                    Vector3 pB = RingPoint(ys, rs, a1);
                    Vector3 nrm = new Vector3(Mathf.Cos(a), -0.10f, Mathf.Sin(a)).normalized;
                    if (Mathf.Abs(pitchDeg) > 0.01f || Mathf.Abs(yawDeg) > 0.01f)
                    {
                        pA = GimbalPoint(pA, y0, pitchDeg, yawDeg);
                        pB = GimbalPoint(pB, y0, pitchDeg, yawDeg);
                        nrm = GimbalDir(nrm, pitchDeg, yawDeg);
                    }
                    outer[s * 2] = pA; outer[s * 2 + 1] = pB;
                    nOut[s * 2] = nrm; nOut[s * 2 + 1] = nrm;
                    Vector3 inw = -nrm;
                    inner[s * 2] = pA + inw * thick;
                    inner[s * 2 + 1] = pB + inw * thick;
                }
                // UV：周向按弧长、轴向按 t，跟着世界尺寸走（0.5 m 一张，与其它部件一致）
                for (int s = 0; s < Stations - 1; s++)
                {
                    float ts = s / (float)(Stations - 1), tn = (s + 1) / (float)(Stations - 1);
                    Vector2 uv00 = new Vector2(a0 * r0 / UvWorldSize, Mathf.Lerp(y0, y1, ts) / UvWorldSize);
                    Vector2 uv01 = new Vector2(a1 * r0 / UvWorldSize, Mathf.Lerp(y0, y1, ts) / UvWorldSize);
                    Vector2 uv11 = new Vector2(a1 * r0 / UvWorldSize, Mathf.Lerp(y0, y1, tn) / UvWorldSize);
                    Vector2 uv10 = new Vector2(a0 * r0 / UvWorldSize, Mathf.Lerp(y0, y1, tn) / UvWorldSize);
                    // 外表面
                    acc.AddQuad(outer[s * 2], outer[s * 2 + 1], outer[(s + 1) * 2 + 1], outer[(s + 1) * 2],
                                nOut[s * 2], nOut[s * 2 + 1], nOut[(s + 1) * 2 + 1], nOut[(s + 1) * 2],
                                uv00, uv01, uv11, uv10, SubHot, flip);
                    // 内表面（法线朝里）
                    Vector3 in0 = -nOut[s * 2], in1 = -nOut[s * 2 + 1];
                    Vector3 in2 = -nOut[(s + 1) * 2 + 1], in3 = -nOut[(s + 1) * 2];
                    acc.AddQuad(inner[(s + 1) * 2], inner[(s + 1) * 2 + 1], inner[s * 2 + 1], inner[s * 2],
                                in3, in2, in1, in0, uv10, uv11, uv01, uv00, SubHot, flip);
                }
                // 两条侧边 + 梢部封口：把两层连起来，侧后方看过去才是一块板
                for (int s = 0; s < Stations - 1; s++)
                {
                    for (int edge = 0; edge < 2; edge++)
                    {
                        int e = s * 2 + edge, f = (s + 1) * 2 + edge;
                        Vector3 side = edge == 0
                            ? new Vector3(-Mathf.Sin(a0), 0f, Mathf.Cos(a0))
                            : new Vector3(Mathf.Sin(a1), 0f, -Mathf.Cos(a1));
                        acc.AddQuad(outer[e], inner[e], inner[f], outer[f],
                                    side, side, side, side,
                                    new Vector2(0f, 0f), new Vector2(1f, 0f),
                                    new Vector2(1f, 1f), new Vector2(0f, 1f), SubHot, flip);
                    }
                }
                int tip = (Stations - 1) * 2;
                Vector3 tipN = new Vector3(Mathf.Cos(a) * Mathf.Cos(tilt), -Mathf.Sin(tilt), Mathf.Sin(a) * Mathf.Cos(tilt)).normalized;
                acc.AddQuad(outer[tip], outer[tip + 1], inner[tip + 1], inner[tip],
                            tipN, tipN, tipN, tipN,
                            new Vector2(0f, 0f), new Vector2(1f, 0f),
                            new Vector2(1f, 1f), new Vector2(0f, 1f), SubHot, flip);
            }
        }

        /// <summary>绕喷口铰链环转一点——视觉上的矢量偏转。</summary>
        private static Vector3 GimbalPoint(Vector3 p, float y0, float pitchDeg, float yawDeg)
        {
            Vector3 pivot = new Vector3(0f, y0, 0f);
            return pivot + GimbalRot(pitchDeg, yawDeg) * (p - pivot);
        }

        private static Vector3 GimbalDir(Vector3 v, float pitchDeg, float yawDeg)
        {
            return GimbalRot(pitchDeg, yawDeg) * v;
        }

        /// <summary>
        /// 与 <see cref="Core.ThrustVector"/> 同一个约定：俯仰绕零件 X、偏航绕零件 Z。
        /// 两处必须一致，否则"喷口往上看、推力往下去"。
        /// </summary>
        private static Quaternion GimbalRot(float pitchDeg, float yawDeg)
        {
            return Quaternion.Euler(-pitchDeg, 0f, -yawDeg);
        }

        /// <summary>
        /// 花瓣单独一个网格（不并进机身）。
        ///
        /// 并进去的话想让它动就得整台机器重建——那是每帧 3000 个三角。
        /// 单独一个 16 片的网格只有 128 顶点，改开口程度直接重建它，代价可以忽略。
        /// </summary>
        public static Mesh BuildPetals(EngineDesignData d, bool flip, float openFrac)
        {
            return BuildPetals(d, flip, openFrac, 0f, 0f);
        }

        public static Mesh BuildPetals(EngineDesignData d, bool flip, float openFrac, float pitchDeg, float yawDeg)
        {
            Dims m = Measure(d);
            MeshAcc acc = new MeshAcc();
            AddNozzlePetals(acc, m, d, flip, openFrac, pitchDeg, yawDeg);
            Mesh mesh = acc.ToMesh("JetForgePetals");
            mesh.name = "JetForgePetals";
            return mesh;
        }

        public const string PetalsName = "JetForgePetals";

        /// <summary>花瓣开口程度的缓存：变了才重建网格。</summary>
        public sealed class PetalState : MonoBehaviour
        {
            public float Open = -1f;
            public float Pitch = -999f;
            public float Yaw = -999f;
            public string Sig = "";
            /// <summary>
            /// 上一次算指纹时用的那份设计对象。指纹是整份设计的逐字段文本（几千字符），
            /// 而这里每帧都被调一次——只要设计对象没换（换设计必是新对象，见 ApplyDesign
            /// 里的 Clone），指纹就不可能变，没必要每帧重拼一遍。
            /// </summary>
            public EngineDesignData Design;
        }

        /// <summary>按开度更新花瓣网格。openFrac 0=收到底，1=全开。</summary>
        public static void UpdatePetals(Part part, EngineDesignData d, float openFrac)
        {
            UpdatePetals(part, d, openFrac, 0f, 0f);
        }

        /// <summary>按开度 + 矢量偏转更新花瓣网格。</summary>
        public static void UpdatePetals(Part part, EngineDesignData d, float openFrac, float pitchDeg, float yawDeg)
        {
            if (part == null || d == null) return;
            if (d.Nozzle == NozzleKind.Converging) return;
            try
            {
                Transform host = HostFor(part);
                Transform t = host != null ? host.Find(PetalsName) : null;
                if (t == null)
                {
                    GameObject go = new GameObject(PetalsName);
                    AlignToPart(go.transform, part, host);
                    go.AddComponent<MeshFilter>();
                    MeshRenderer mr = go.AddComponent<MeshRenderer>();
                    Material[] mats = PickMaterials(part);
                    if (mats != null && mats.Length > SubHot) mr.sharedMaterial = mats[SubHot];
                    else if (mats != null && mats.Length > 0) mr.sharedMaterial = mats[0];
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    t = go.transform;
                }
                PetalState ps = t.GetComponent<PetalState>();
                if (ps == null) ps = t.gameObject.AddComponent<PetalState>();
                string sig;
                if (ReferenceEquals(ps.Design, d) && !string.IsNullOrEmpty(ps.Sig))
                {
                    // 设计对象还是同一份，指纹不可能变 —— 不必每帧重拼那几千字符
                    sig = ps.Sig;
                }
                else
                {
                    try { sig = DesignSignature.Of(d); } catch { sig = ""; }
                    ps.Design = d;
                }
                if (Mathf.Abs(ps.Open - openFrac) < 0.004f
                    && Mathf.Abs(ps.Pitch - pitchDeg) < 0.05f
                    && Mathf.Abs(ps.Yaw - yawDeg) < 0.05f
                    && ps.Sig == sig) return;
                ps.Open = openFrac;
                ps.Pitch = pitchDeg;
                ps.Yaw = yawDeg;
                ps.Sig = sig;
                MeshFilter mf = t.GetComponent<MeshFilter>();
                if (mf == null) mf = t.gameObject.AddComponent<MeshFilter>();
                // 旧网格可能正被 ALT 复制/对称件共用，不能立刻销毁（见 MeshRetire 的注释）
                if (mf.sharedMesh != null) MeshRetire.Retire(mf.sharedMesh);
                mf.sharedMesh = BuildPetals(d, false, openFrac, pitchDeg, yawDeg);
            }
            catch (Exception ex)
            {
                Debug.LogError("[JetForge] 花瓣重建失败：" + ex);
            }
        }

        private static Vector3 RingPoint(float y, float r, float a)
        {
            return new Vector3(r * Mathf.Cos(a), y, r * Mathf.Sin(a));
        }

        /// <summary>进气口半径——风车阻力要拿它算迎风面积（πR²），跟外形是同一份几何。</summary>
        public static float InletRadius(EngineDesignData d)
        {
            return Measure(d).RIn;
        }

        public static string VisualObjectName { get { return VisualName; } }

    }
}