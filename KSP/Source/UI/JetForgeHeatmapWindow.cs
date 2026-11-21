using System;
using UnityEngine;
using JetForge.Core;
using JetForge.KSP;

namespace JetForge.UI
{
    /// <summary>
    /// 推力热力图：纵轴高度、横轴速度、颜色是推力，按玩家指定的油门算一整张。
    ///
    /// 为什么单独开一个窗口，而不是塞在设计器里：
    /// 这张图是"这台机器在不同高度/速度下能给出多少推力"的全景，玩家要拿着它和别的设计比，
    /// 所以它得能一直开着、能和设计器并排放、能悬停读点。塞在页面里只能看到 320×200 的缩略图。
    ///
    /// 每格都是一次真实解算（`OffDesign.Solve`），不是插值——设计器那张缩略图插的是网格，
    /// 而现在飞行里已经不用网格了，设计工具如果还拿旧网格画，就会和飞行中看到的数不一致。
    /// 代价是 4000 次求解，所以分帧算、带进度；算一次约 1 秒，库里调参时感觉不到卡。
    /// </summary>
    public class JetForgeHeatmapWindow : MonoBehaviour
    {
        private static JetForgeHeatmapWindow _instance;

        // 72×56 是折中：再密一格就 2 px 宽，再疏看不出"推力包线"的形状。
        private const int Nx = 72;
        private const int Ny = 56;

        /// <summary>每帧最多解多少格。4000 格 ÷ 400 ≈ 10 帧算完，约 0.2 秒一帧的预算。</summary>
        private const int BudgetPerFrame = 400;

        private Rect _rect = new Rect(250f, 80f, 780f, 640f);
        private bool _visible;
        private ModuleJetForgeEngine _target;
        private EngineDesignData _design;

        private float _throttle = 1f;
        private bool _ab;
        private double _altMax = 25000.0;
        private double _machMax = 2.5;

        private double[] _thrust;
        private bool[] _ok;
        private Texture2D _tex;
        private int _done;
        private double _lo, _hi;
        private string _note = "";
        private string _stats = "";
        private double _buildMs;
        private DateTime _buildStart = DateTime.UtcNow;

        // 设计常数只跟设计有关，跟高度/速度/油门无关。不缓存的话，每次 Solve 都会
        // 从头推一遍设计点（实测 5 ms/次），一张图要 20 秒——缓存后是 0.2 ms/次。
        private DesignConstants _consts;

        private double _builtThr = -1.0;
        private bool _builtAb;
        private string _builtSig = "";

        public static void Open(ModuleJetForgeEngine target, EngineDesignData design)
        {
            if (design == null) return;
            if (_instance == null)
            {
                GameObject go = new GameObject("JetForgeHeatmap");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<JetForgeHeatmapWindow>();
            }
            _instance.Bind(target, design);
            Debug.Log("[JetForge] 热力图窗口：打开（油门 " + _instance._throttle.ToString("P0") +
                      "，高度上限 " + (_instance._altMax / 1000.0).ToString("F0") + " km）");
        }

        private void Awake() { _instance = this; }

        private void OnDestroy()
        {
            if (_tex != null) Destroy(_tex);
        }

        public void Bind(ModuleJetForgeEngine target, EngineDesignData design)
        {
            _target = target;
            _design = design != null ? design.Clone() : null;
            _consts = null;
            if (_design != null)
            {
                // 常数也按"这一刻接上的进气道"推：设计常数里的设计流量/喉道面积都是进气道给的，
                // 不传 install 就会推出另一套常数，跟飞行里那套分家。
                try { _consts = OffDesign.DeriveDesignConstants(_design, target != null ? target.Install : null); }
                catch { _consts = null; }
            }
            _visible = true;
            _ab = _design != null && _design.Afterburner != null && _design.Afterburner.Enabled;
            Invalidate();
        }

        /// <summary>下次 OnGUI 重新算。油门、加力、设计任一变了都走这里。</summary>
        private void Invalidate()
        {
            _buildStart = DateTime.UtcNow;
            _thrust = null;
            _ok = null;
            _done = 0;
            _builtThr = -1.0;
            _stats = "";
            _note = "";
        }

        private void Update()
        {
            if (!_visible || _design == null) return;

            // 先分配，再判断"算完没有"。
            //
            // 这里原来写反了：`Invalidate()` 把 _thrust 置 null，下一行看到 null 就 return，
            // 而分配缓冲的 EnsureBuffers 在 return 之后——于是这张图永远一格都算不出来。
            // 玩家点"开始算"没有任何反应，控制台也没有异常：它只是安静地什么都不做。
            EnsureBuffers();
            if (_done >= Nx * Ny) return;

            // 分帧算：每帧 400 格。算完之前界面显示进度条——
            // 一次性 4000 次求解会让窗口卡住 1 秒，拖滑杆的感受会很糟。
            double[] alt = AltNodes();
            double[] mach = MachNodes();
            int end = Math.Min(_done + BudgetPerFrame, Nx * Ny);
            for (int k = _done; k < end; k++)
            {
                int i = k / Nx;       // 高度
                int j = k % Nx;       // 速度
                Sample(alt[i], mach[j], k);
            }
            _done = end;
            if (_done >= Nx * Ny) Finish();
        }

        private void EnsureBuffers()
        {
            if (_thrust == null) _thrust = new double[Nx * Ny];
            if (_ok == null) _ok = new bool[Nx * Ny];
        }

        private void Sample(double alt, double mach, int k)
        {
            try
            {
                double abFrac = _ab ? 1.0 : 0.0;
                // 必须带上这一刻接上的进气道。不带的话，这张图算的是"进气道无限供气"的世界，
                // 而飞行里发动机吃的是被进气道限住的流量——两套数当然对不上
                // （玩家报的"热力图显示的低、实飞出来高"就是差在这里）。
                IntakeInstall install = _target != null ? _target.Install : null;
                OffDesignResult r = OffDesign.Solve(_design, alt, mach, _throttle, abFrac, _consts,
                                                  24, -1.0, 0.0, 1.0, 0, install);
                bool ok = r != null && r.Ok && r.NetThrust > 1.0;
                _ok[k] = ok;
                _thrust[k] = ok ? r.NetThrust : 0.0;
            }
            catch { _ok[k] = false; _thrust[k] = 0.0; }
        }

        /// <summary>一格算完之后做的收尾：定色标、生成贴图、写一句统计。</summary>
        private void Finish()
        {
            double lo = double.MaxValue, hi = double.MinValue;
            int valid = 0, best = -1;
            for (int k = 0; k < Nx * Ny; k++)
            {
                if (!_ok[k]) continue;
                valid++;
                if (_thrust[k] < lo) lo = _thrust[k];
                if (_thrust[k] > hi) { hi = _thrust[k]; best = k; }
            }
            if (valid == 0)
            {
                // 保持"算完了"的状态，只留一句说明。回到 _thrust = null 会让 Update
                // 立刻从头再算一遍，白白烧 2.5 秒还什么都不显示。
                _note = Loc.T("这张图一个格都没算出来：设计点不成立或这个油门烧不出推力。", "Not a single cell came out: the design point does not hold, or this throttle makes no thrust.");
                _done = Nx * Ny;
                _stats = string.Format(Loc.T("有效格 0/{0}：这台设计在这个油门下整片都烧不出推力。", "Valid cells 0/{0}: this design makes no thrust anywhere at this throttle."), Nx * Ny);
                Debug.Log("[JetForge] 热力图：" + _stats);
                return;
            }
            _lo = Math.Max(0.0, lo);
            _hi = hi > lo ? hi : lo + 1.0;

            if (_tex != null) Destroy(_tex);
            _tex = new Texture2D(Nx, Ny, TextureFormat.RGBA32, false);
            _tex.filterMode = FilterMode.Point;
            _tex.wrapMode = TextureWrapMode.Clamp;
            for (int i = 0; i < Ny; i++)
                for (int j = 0; j < Nx; j++)
                {
                    int k = i * Nx + j;
                    Color32 c = _ok[k] ? Heat((_thrust[k] - _lo) / (_hi - _lo)) : new Color32(48, 30, 30, 255);
                    _tex.SetPixel(j, i, c);
                }
            _tex.Apply();

            double[] alt = AltNodes();
            double[] mach = MachNodes();
            int bi = best / Nx, bj = best % Nx;
            _stats = string.Format(Loc.T("最大 {0:F2} kN（{1:F1} km / Ma {2:F2}）｜有效格 {3}/{4}\n算完用了 {5:F0} ms", "Max {0:F2} kN ({1:F1} km / Ma {2:F2}) | valid cells {3}/{4}\ncomputed in {5:F0} ms"),
                _thrust[best] / 1000.0, alt[bi] / 1000.0, mach[bj], valid, Nx * Ny, _buildMs);
            _buildMs = (DateTime.UtcNow - _buildStart).TotalMilliseconds;
            _builtThr = _throttle;
            _builtAb = _ab;
            _builtSig = Sig();
            // 一行日志：玩家下次说"生成不了"时，先看这里有没有这行。
            Debug.Log("[JetForge] 热力图：" + _stats);
        }

        private string Sig()
        {
            if (_design == null) return "";
            string s = "";
            try { s = DesignSignature.Of(_design); } catch { s = ""; }
            // 进气道换了，这张图整张都该作废——它是(高度,马赫)平面上的推力，
            // 而进气道决定的就是"每个点上还能不能吃到那么多气"。
            IntakeInstall install = _target != null ? _target.Install : null;
            if (install != null) s += "|I" + install.Signature;
            return s;
        }

        private double[] AltNodes()
        {
            double[] a = new double[Ny];
            for (int i = 0; i < Ny; i++) a[i] = (double)i / (Ny - 1) * _altMax;
            return a;
        }

        private double[] MachNodes()
        {
            double[] m = new double[Nx];
            for (int j = 0; j < Nx; j++) m[j] = (double)j / (Nx - 1) * _machMax;
            return m;
        }

        private void OnGUI()
        {
            if (!_visible || _design == null) return;
            // 只在编辑器里开：这是设计工具，飞行中拿它没有意义。
            if (!HighLogic.LoadedSceneIsEditor) { _visible = false; return; }

            GUI.skin = HighLogic.Skin;
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - 300f));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - 200f));
            _rect = GUILayout.Window(0x4A47, _rect, DrawWindow, Loc.T("JetForge · 推力热力图", "JetForge - Thrust heatmap"));
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("油门", "Thr"), GUILayout.Width(40f));
            float t = GUILayout.HorizontalSlider(_throttle, 0f, 1f, GUILayout.Width(220f));
            GUILayout.Label(string.Format("{0:P0}", _throttle), GUILayout.Width(48f));
            if (Math.Abs(t - _throttle) > 0.005f)
            {
                _throttle = t;
                Invalidate();
            }

            if (_design.Afterburner != null && _design.Afterburner.Enabled)
            {
                bool ab = GUILayout.Toggle(_ab, Loc.T("开加力", "Afterburn"), GUILayout.Width(72f));
                if (ab != _ab) { _ab = ab; Invalidate(); }
            }

            GUILayout.Label(Loc.T("高度上限", "Alt cap"), GUILayout.Width(60f));
            double am = SliderRange("alt", _altMax / 1000.0, 5.0, 40.0);
            if (Math.Abs(am - _altMax / 1000.0) > 1e-6) { _altMax = am * 1000.0; Invalidate(); }
            GUILayout.Label(string.Format("{0:F0} km", _altMax / 1000.0), GUILayout.Width(52f));

            GUILayout.Label(Loc.T("速度上限", "Spd cap"), GUILayout.Width(60f));
            double mm = SliderRange("mach", _machMax, 0.8, 5.0);
            if (Math.Abs(mm - _machMax) > 1e-6) { _machMax = mm; Invalidate(); }
            GUILayout.Label(string.Format("Ma {0:F1}", _machMax), GUILayout.Width(54f));

            if (GUILayout.Button(Loc.T("重算", "Redo"), GUILayout.Width(56f))) Invalidate();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.T("关闭", "Close"), GUILayout.Width(56f))) _visible = false;
            GUILayout.EndHorizontal();

            if (_thrust == null || _done < Nx * Ny)
            {
                GUILayout.Label(string.Format(Loc.T("正在算：{0}/{1} 格（每格一次真实解算）", "Computing {0}/{1} cells (one real solve per cell)"), _done, Nx * Ny));
                if (GUILayout.Button(Loc.T("开始算", "Start"))) Invalidate();
                GUI.DragWindow(new Rect(0f, 0f, _rect.width, 22f));
                return;
            }

            GUILayout.Label(_stats);
            if (!string.IsNullOrEmpty(_note)) GUILayout.Label(_note);

            DrawMap();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("颜色：", "Colour"), GUILayout.Width(42f));
            GUILayout.Label(string.Format(Loc.T("0 kN（蓝）→ {0:F1} kN（红）", "0 kN (blue) -> {0:F1} kN (red)"), _hi / 1000.0));
            GUILayout.FlexibleSpace();
            GUILayout.Label(_builtAb ? Loc.T("加力档", "Afterburner") : Loc.T("干态", "Dry"));
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 22f));
        }

        private double _altSlider = -1.0, _machSlider = -1.0;

        private double SliderRange(string key, double value, double lo, double hi)
        {
            // 两个滑块放在同一行里，各自记一份值：IMGUI 没有"滑块状态"这一说，
            // 不记住就会出现"拖高度时速度也在动"。
            if (key == "alt") { if (_altSlider < 0) _altSlider = value; _altSlider = GUILayout.HorizontalSlider((float)_altSlider, (float)lo, (float)hi, GUILayout.Width(90f)); return _altSlider; }
            if (_machSlider < 0) _machSlider = value;
            _machSlider = GUILayout.HorizontalSlider((float)_machSlider, (float)lo, (float)hi, GUILayout.Width(90f));
            return _machSlider;
        }

        /// <summary>把贴图画在给定的矩形里，下面再铺一串刻度。</summary>
        private void DrawMap()
        {
            const float padL = 46f, padB = 22f;
            Rect area = GUILayoutUtility.GetRect(600f, 460f, GUILayout.ExpandWidth(true));
            Rect map = new Rect(area.x + padL, area.y, area.width - padL - 8f, area.height - padB);
            if (_tex != null) GUI.DrawTexture(map, _tex);

            // 边框：贴图与背景同色时会分不清边界
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            GUI.DrawTexture(new Rect(map.x, map.y, map.width, 1f), _tex);
            GUI.DrawTexture(new Rect(map.x, map.yMax, map.width, 1f), _tex);
            GUI.DrawTexture(new Rect(map.x, map.y, 1f, map.height), _tex);
            GUI.DrawTexture(new Rect(map.xMax, map.y, 1f, map.height), _tex);
            GUI.color = Color.white;

            // 纵轴：高度（底 = 0，顶 = 上限）
            for (int i = 0; i <= 5; i++)
            {
                float frac = i / 5f;
                float y = map.yMax - frac * map.height;
                GUI.Label(new Rect(area.x, y - 8f, padL - 6f, 18f),
                    string.Format("{0:F0}k", frac * _altMax / 1000.0));
            }
            // 横轴：马赫
            for (int j = 0; j <= 5; j++)
            {
                float frac = j / 5f;
                float x = map.x + frac * map.width - 12f;
                GUI.Label(new Rect(x, map.yMax + 2f, 44f, 18f),
                    string.Format("{0:F1}", frac * _machMax));
            }

            // 悬停：把鼠标位置换算成格点，读数直接给"该格"的真实解算值。
            // 不做双线性插值——图上每一格都是一个真解，插出来的数反而会骗人。
            Event e = Event.current;
            if (e != null && map.Contains(e.mousePosition) && _thrust != null)
            {
                int j = Mathf.Clamp((int)((e.mousePosition.x - map.x) / map.width * Nx), 0, Nx - 1);
                int i = Mathf.Clamp((int)((map.yMax - e.mousePosition.y) / map.height * Ny), 0, Ny - 1);
                int k = i * Nx + j;
                double[] alt = AltNodes();
                double[] mach = MachNodes();

                // 十字准线：不描一下，玩家不知道自己读的是哪一格
                float cx = map.x + (j + 0.5f) / Nx * map.width;
                float cy = map.yMax - (i + 0.5f) / Ny * map.height;
                GUI.color = new Color(1f, 1f, 1f, 0.55f);
                GUI.DrawTexture(new Rect(cx, map.y, 1f, map.height), _tex);
                GUI.DrawTexture(new Rect(map.x, cy, map.width, 1f), _tex);
                GUI.color = Color.white;

                string text = _ok[k]
                    ? string.Format(Loc.T("推力 {0:F2} kN\n{1:F1} km / Ma {2:F2}", "Thrust {0:F2} kN\n{1:F1} km / Ma {2:F2}"),
                        _thrust[k] / 1000.0, alt[i] / 1000.0, mach[j])
                    : string.Format(Loc.T("这一格烧不出推力\n{0:F1} km / Ma {1:F2}", "No thrust in this cell\n{0:F1} km / Ma {1:F2}"), alt[i] / 1000.0, mach[j]);
                // 靠右时把提示框往左挪，别让它跑出窗口
                float bw = 190f, bh = 40f;
                float bx = e.mousePosition.x + 14f;
                if (bx + bw > map.xMax) bx = e.mousePosition.x - bw - 14f;
                float by = Mathf.Min(e.mousePosition.y + 12f, map.yMax - bh - 2f);
                GUI.Box(new Rect(bx, by, bw, bh), "");
                GUI.Label(new Rect(bx + 6f, by + 4f, bw - 12f, bh - 8f), text);
            }
        }

        private static Color32 Heat(double t)
        {
            // 蓝 → 青 → 绿 → 黄 → 红
            t = Mathf.Clamp01((float)t);
            if (t < 0.25) return new Color32((byte)(20 * t / 0.25), (byte)(120 * t / 0.25), (byte)(200), 255);
            if (t < 0.5) return new Color32(20, (byte)(120 + 100 * (t - 0.25) / 0.25), (byte)(200 - 120 * (t - 0.25) / 0.25), 255);
            if (t < 0.75) return new Color32((byte)(20 + 220 * (t - 0.5) / 0.25), 220, (byte)(80 - 80 * (t - 0.5) / 0.25), 255);
            return new Color32(240, (byte)(220 - 200 * (t - 0.75) / 0.25), 20, 255);
        }

        /// <summary>一张图的统计量。自检与窗口共用，别在两处各算一套。</summary>
        public struct StatsResult
        {
            public bool Ok;
            public double MaxThrust, LoThrust, MaxAlt, MaxMach;
            public int Valid, Total;
        }

        /// <summary>
        /// 不解窗口也能算出一张图的统计量。窗口依赖 GUI 与编辑器场景，自检跑在加载期，
        /// 两者只能共用"采样"这一层——所以坐标换算写在这里，窗口与自检都用它。
        /// </summary>
        public static StatsResult Compute(EngineDesignData d, double throttle, double abFrac,
            double altMax, double machMax, int nx, int ny, DesignConstants consts = null)
        {
            StatsResult r0 = new StatsResult();
            r0.Total = nx * ny;
            r0.LoThrust = double.MaxValue;
            if (d == null || nx < 2 || ny < 2) return r0;
            if (consts == null) { try { consts = OffDesign.DeriveDesignConstants(d); } catch { return r0; } }
            int bestI = -1, bestJ = -1;
            for (int i = 0; i < ny; i++)
            {
                double alt = (double)i / (ny - 1) * altMax;
                for (int j = 0; j < nx; j++)
                {
                    double mach = (double)j / (nx - 1) * machMax;
                    OffDesignResult r;
                    try { r = OffDesign.Solve(d, alt, mach, throttle, abFrac, consts); }
                    catch { continue; }
                    if (r == null || !r.Ok || r.NetThrust <= 1.0) continue;
                    r0.Valid++;
                    if (r.NetThrust > r0.MaxThrust) { r0.MaxThrust = r.NetThrust; bestI = i; bestJ = j; }
                    if (r.NetThrust < r0.LoThrust) r0.LoThrust = r.NetThrust;
                }
            }
            r0.Ok = r0.Valid > 0;
            if (r0.Ok)
            {
                r0.MaxAlt = (double)bestI / (ny - 1) * altMax;
                r0.MaxMach = (double)bestJ / (nx - 1) * machMax;
            }
            if (r0.LoThrust == double.MaxValue) r0.LoThrust = 0.0;
            return r0;
        }

        public static string Stats(EngineDesignData d, double throttle, double abFrac,
            double altMax, double machMax, int nx, int ny)
        {
            StatsResult r = Compute(d, throttle, abFrac, altMax, machMax, nx, ny);
            if (!r.Ok) return Loc.T("一格都没有", "Not one cell");
            return string.Format(Loc.T("最大 {0:F2} kN @ {1:F1} km / Ma {2:F2}｜有效 {3}/{4}", "Max {0:F2} kN @ {1:F1} km / Ma {2:F2} | valid {3}/{4}"),
                r.MaxThrust / 1000.0, r.MaxAlt / 1000.0, r.MaxMach, r.Valid, r.Total);
        }
    }
}