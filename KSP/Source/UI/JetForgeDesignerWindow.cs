using System;
using System.Collections.Generic;
using System.Text;
using JetForge.Core;
using JetForge.KSP;
using UnityEngine;

namespace JetForge.UI
{
    /// <summary>
    /// 自定义发动机的独立设置页面。
    ///
    /// 为什么做成独立窗口而不是塞进右键菜单：这台机器的可调项有近三十个，
    /// 全部摊在 PAW 里会滚到看不见。FAR 和 B9 自定义机翼都是这个路子——
    /// 一个窗口、左侧标签分页、右侧实时读数，改哪一项立刻看到后果。
    ///
    /// 窗口不持有零件数据的所有权：它改的是一份草稿（_edit），点"应用"才写回零件。
    /// 这样玩家可以乱试参数而不弄坏已经飞起来的东西。
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class JetForgeDesignerWindow : MonoBehaviour
    {
        private const int WindowId = 0x4A46;
        private static JetForgeDesignerWindow _instance;

        private ModuleJetForgeEngine _target;
        /// <summary>上次从零件读回来（或写回零件）的那两个设计对象。只比身份，不比字段。</summary>
        private EngineDesignData _boundDesign;
        private SimpleDesign _boundSimple;
        private EngineDesignData _edit;
        /// <summary>逐级编辑面板展开着没有。一条共用，切页不丢。</summary>
        private bool _showStages;
        private CycleResult _preview;
        private CheckReport _report;

        /// <summary>
        /// 慢车最低可用喷油量（kg/s）。跟着 `Recompute()` 一起更新——它要跑一遍非设计点反解，
        /// 不能放在 `OnGUI` 里每帧算。
        /// </summary>
        private double _idleFuel;
        private double _idleFrac;
        private double _idleFar;
        private double _idleT4;

        // 1020×700：残酷模式一页里有五到七个滑杆加两三行读数，
        // 760×560 的时候中栏只有 336 px 宽，"核心机进口直径（0=自动）"这种标签
        // 会和滑杆挤在一起。宽度按"标签 190 + 滑杆 150 + 读数 70"给的。
        private Rect _rect = new Rect(200f, 70f, 1020f, 700f);
        private Vector2 _scroll;
        private int _tab;
        private int _gridTab;

        // 两档模式：残酷模式改几何，简单模式改目标。草稿分开存，切来切去不互相覆盖。
        private bool _useSimple;
        private SimpleDesign _simpleEdit;
        private SimpleResult _simplePreview;
        private bool _visible;
        /// <summary>点了预设之后在页面上留一句这台机器的来路（含已知偏差）。</summary>
        private string _presetNote = "";

        // 我的设计：存在 GameData/JetForge/UserDesigns 下。
        // 设计原本只活在零件的存档节点里，换一艘船要同一台发动机就只能复制零件；
        // 存成文件之后，换飞船、重开存档都还在。
        private List<UserDesignEntry> _userEntries;
        private string _userMessage = "";

        /// <summary>拖动滑杆的撤销栈。只在手松开时记一步，详见 Core/DesignUndo.cs。</summary>
        private readonly DesignUndo _undo = new DesignUndo();
        /// <summary>简单模式那条栈。两种模式的编辑对象是两个类，混在一条栈上退一步会换机器。</summary>
        private readonly SimpleUndo _simpleUndo = new SimpleUndo();
        // 改名是"就地改这一行"，不是弹窗：设计库就几个文件，弹一个窗再填一次名字太重。
        private string _renamingPath;
        private string _renameText = "";
        // 对比：把库里某台设计读出来与当前草稿并排比读数。比的是"多花的钱换来了什么"，
        // 所以不拍总分，一行一个读数各说各的方向。
        private CompareResult _compare;
        private string _compareName = "";

        // 存档历史：库里每个设计在「覆盖」之前会留一份旧文件，这里让玩家能点回去。
        private List<UserDesignEntry> _history;
        private string _historyName = "";

        // 分享：Shared 子目录是"交换站"——玩家自己把文件贴到论坛或网盘，模组只负责
        // 把库里那份放进去、把别人放进来那份收进库。null = 面板没打开。
        private List<UserDesignEntry> _shared;
        private string _status = "";
        private double _previewSeconds;
        private string _cappedNote = "";
        private MassBreakdown _mass;
        private DesignVerdict _verdict;   // 设计评价（能不能用、差在哪），与 _preview 同一拍重算

        private static string[] TabNames { get { return JetForge.Core.DesignTabs.Names; } }

        /// <summary>"剖视"那一页的编号。页号统一定义在 Core/DesignTabs.cs，
        /// 别在这里再写一遍数字——剖视图靠页号跳转，漏改一处就跳错页。</summary>
        private const int TabCutaway = JetForge.Core.DesignTabs.Cutaway;

        private static string[] _tabNamesSimple;
        private static JetForge.Core.Lang _tabSimpleLang;

        private static string[] TabNamesSimple
        {
            get
            {
                if (_tabNamesSimple == null || _tabSimpleLang != Loc.Current)
                {
                    _tabNamesSimple = new[]
                    {
                        Loc.T("简单参数", "Simple params"),
                        Loc.T("读数", "Readout"),
                        Loc.T("性能网格", "Performance grid")
                    };
                    _tabSimpleLang = Loc.Current;
                }
                return _tabNamesSimple;
            }
        }

        private string[] CurrentTabs { get { return _useSimple ? TabNamesSimple : TabNames; } }

        public static void Open(ModuleJetForgeEngine target)
        {
            if (target == null) return;
            if (_instance == null)
            {
                GameObject go = new GameObject("JetForgeDesigner");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<JetForgeDesignerWindow>();
            }
            _instance.Bind(target);
        }

        public void Bind(ModuleJetForgeEngine target)
        {
            _target = target;
            _tab = 0;
            _visible = true;
            ReloadFromPart(true);
        }

        /// <summary>
        /// 把零件上的设计读回草稿。
        ///
        /// 这不是"打开窗口时那一下"就够的：向导、对称件同步、从设计库载入都会**换掉零件上
        /// 那个设计对象**，而设计器这边是草稿。以前草稿只在这一行读一次，于是玩家点完向导的
        /// 「把这台初稿交给设计器」——零件上确实变了、屏幕上也写着"已生成"——可设计器手里
        /// 还是老草稿，随手拖一下滑杆就把它按老草稿写回去了。玩家看到的是"初稿无法应用到
        /// 发动机上"，真相比这更难查：它应用了，又被下一帧的草稿盖了。
        /// 判据用**对象身份**而不是字段比较：换设计一定是换了对象（ApplyDesign 收的是
        /// 一份 Clone），比字段便宜，而且不会因为"数字恰好一样"漏掉一次真正的替换。
        /// </summary>
        private void ReloadFromPart(bool quiet)
        {
            _useSimple = _target != null && _target.UseSimple;
            _edit = _target != null && _target.Design != null ? _target.Design.Clone() : new EngineDesignData();
            _simpleEdit = _target != null && _target.SimpleCfg != null ? _target.SimpleCfg.Clone() : new SimpleDesign();
            _boundDesign = _target != null ? _target.Design : null;
            _boundSimple = _target != null ? _target.SimpleCfg : null;
            _presetNote = "";
            Recompute();
            _undo.Reset(_edit);
            _simpleUndo.Reset(_simpleEdit);
            if (!quiet)
                _status = Loc.T("零件上的设计被别处换掉了，草稿按零件重读了一遍",
                    "The part's design was changed elsewhere; the draft was reloaded from the part");
        }

        private void SwitchMode(bool simple)
        {
            _useSimple = simple;
            _tab = 0;
            _scroll = Vector2.zero;
            _status = simple ? Loc.T("已切到简单模式（草稿，点应用才写回零件）", "Switched to Simple (draft; press Apply to write it back)") : Loc.T("已切到残酷模式（草稿，点应用才写回零件）", "Switched to Full model (draft; press Apply to write it back)");
            Recompute();
        }

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
        }

        /// <summary>
        /// 预览只跑设计点 + 判据（约 1 ms）。不在每次滑杆拖动时重建整张网格——
        /// 那是 0.2~1.5 秒的事，手感会废掉。网格由"性能网格"页手动重建。
        /// </summary>
        private void Recompute()
        {
            DateTime t0 = DateTime.UtcNow;
            if (_useSimple)
            {
                if (_simpleEdit == null) _simpleEdit = new SimpleDesign();
                _simpleEdit.Normalize();
                _simplePreview = Simple.DesignPoint(_simpleEdit);
                _report = Criteria.EvaluateSimple(_simpleEdit, _simplePreview.AsView(), 0.0);
            }
            else
            {
                if (_edit == null) return;
                _preview = Cycle.SolveDesignPoint(_edit);
                ResultView v = Criteria.ViewOf(_preview);
                _report = Criteria.Evaluate(_edit, v, _edit.Tech, Gas.P0, 0.0);

                // 设计点那行是按热力学算的，但喉道是按"干态"定尺寸的。
                // 固定收敛喷管一开大加力就被自己的喉道憋住，玩家在这里看到的推力
                // 会比飞行中实际拿到的高——这里补一句"实际只有多少"。
                _cappedNote = "";
                try
                {
                    if (_edit.Afterburner.Enabled && !NozzleSchedule.CanVary(_edit.Nozzle))
                    {
                        OffDesignResult abr = OffDesign.Solve(_edit, _edit.DesignAlt, _edit.DesignMach, 1.0, 1.0);
                        if (abr != null && abr.Ok && abr.NozzleCapped)
                            _cappedNote = string.Format(
                                Loc.T("喷口憋住了：实际 {0:F2} kN（进气 {1:F1} kg/s，喉道按干态定）", "Nozzle is choking: actually {0:F2} kN (inlet {1:F1} kg/s, throat sized for dry)"),
                                abr.NetThrust / 1000.0, abr.MdotAir);
                    }
                }
                catch { _cappedNote = ""; }
            }

            // 设计评价（Phase3 §6.3）：先给结论，再给数。打开这一页最想知道的是
            // "这台机器能不能用、差在哪"，逐条判据回答不了这个问题。
            try
            {
                if (!_useSimple)
                {
                    OffDesignResult vp = OffDesign.Solve(_edit, _edit.DesignAlt, _edit.DesignMach,
                        1.0, 0.0, null, 60, -1.0, 0.0, 1.0, 0, null);
                    _verdict = DesignVerdict.Of(_edit, vp, _report, _preview != null ? _preview.Thrust : double.NaN);
                }
                else _verdict = null;
            }
            catch { _verdict = null; }

            // 干重与造价：跟外形一起变，玩家一边拖滑杆一边就能看到推重比在掉
            try
            {
                EngineDesignData md = _useSimple ? ModuleJetForgeEngine.MeshDesignFromSimple(_simpleEdit) : _edit;
                _mass = MassCost.Estimate(md);
            }
            catch { _mass = null; }

            // 最低可用喷油量：校准一次设计常数顺手就能拿到（反解慢车那一档烧多少油）。
            // 失败了就留 0，面板不显示那一行——不显示比显示一个假数好。
            try
            {
                DesignConstants dc = OffDesign.DeriveDesignConstants(_edit);
                _idleFuel = dc != null && dc.Ok ? dc.IdleFuelFlow : 0.0;
                _idleFrac = dc != null && dc.Ok ? dc.IdleThrustFrac : 0.0;
                _idleFar = dc != null && dc.Ok ? dc.IdleFar : 0.0;
                _idleT4 = dc != null && dc.Ok ? dc.T4Idle : 0.0;
            }
            catch { _idleFuel = 0.0; _idleFrac = 0.0; _idleFar = 0.0; _idleT4 = 0.0; }

            _previewSeconds = (DateTime.UtcNow - t0).TotalMilliseconds;
        }

        private void OnGUI()
        {
            if (!_visible || _edit == null) return;
            // 窗口在机库里开着的时候直接起飞？那就自己关掉。飞行中改设计不会重建外形，
            // 让玩家对着一个改不动的窗口点按钮，比直接关掉更糟。
            if (!HighLogic.LoadedSceneIsEditor) { _visible = false; return; }
            // 别处换了零件上的设计（向导 / 对称件同步 / 从库里载入）就先重读草稿，
            // 否则这一帧玩家拖的任何一根滑杆都会把那份设计写回去。
            if (_target != null
                && (!ReferenceEquals(_target.Design, _boundDesign) || !ReferenceEquals(_target.SimpleCfg, _boundSimple)))
                ReloadFromPart(false);
            GUI.skin = HighLogic.Skin;
            // Ctrl+Z 在设计器里撤销一步。窗口开着时才拦，且只拦这一条组合键——
            // 别的键照旧传给游戏（KSP 自己也用 Ctrl+Z 的地方不该被这个窗口吞掉）。
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Z
                && (Event.current.control || Event.current.command))
            {
                DoUndo();
                Event.current.Use();
            }
            // 小屏幕或者玩家把窗口拖出屏幕外时，别让窗口只能看见一半：进来先夹一次。
            float maxW = Mathf.Max(700f, Screen.width - 40f);
            float maxH = Mathf.Max(420f, Screen.height - 40f);
            _rect.width = Mathf.Clamp(_rect.width, 700f, maxW);
            _rect.height = Mathf.Clamp(_rect.height, 420f, maxH);
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, Loc.T("JetForge · 发动机设计器 ", "JetForge · Engine designer") + JetForgeVersion.Display);

            // 手松开或键松开时才拍快照：滑杆在拖动过程中每帧都变，逐帧记会把二十步
            // 全花在同一次拖动上。放在窗口画完之后，此时这一帧的改动都已经落到 _edit 上。
            EventType t = Event.current.type;
            if (t == EventType.MouseUp || t == EventType.KeyUp)
            {
                if (_useSimple) _simpleUndo.Bind(_simpleEdit);
                else _undo.Bind(_edit);
            }
        }

        /// <summary>退回上一步。栈空时说一句"没有可退的"，不要静默。</summary>
        private void DoUndo()
        {
            if (_useSimple)
            {
                SimpleDesign backSimple = _simpleUndo.Pop();
                if (backSimple == null)
                {
                    _userMessage = Loc.T("没有可以撤销的改动", "Nothing to undo");
                    return;
                }
                _simpleEdit = backSimple;
                _presetNote = "";
                _userMessage = Loc.T("已退回上一步", "Undid one step");
                Recompute();
                return;
            }

            EngineDesignData back = _undo.Pop();
            if (back == null)
            {
                _userMessage = Loc.T("没有可以撤销的改动", "Nothing to undo");
                return;
            }
            _edit = back;
            _presetNote = "";
            _userMessage = Loc.T("已退回上一步", "Undid one step");
            Recompute();
        }

        /// <summary>中栏滚动的可见高度：窗口高 700 时给 490，窗口被拖大也就跟着长。</summary>
        private float BodyH { get { return Mathf.Clamp(_rect.height - 210f, 280f, 900f); } }

        /// <summary>
        /// 把当前悬停项的说明画在鼠标旁边。这里必须自己画：KSP 的 GUI.skin 没有
        /// 帮你实现 tooltip 的绘制，GUI.tooltip 只是个字符串，不画出来玩家就永远看不到。
        /// </summary>
        private void DrawHoverHint()
        {
            string text = GUI.tooltip;
            if (string.IsNullOrEmpty(text)) return;
            GUIStyle st = new GUIStyle(GUI.skin.box);
            st.wordWrap = true;
            // 不对齐成 UpperLeft：TextAnchor 在 UnityEngine.TextRenderingModule 里，
            // 这个工程没引那个 dll（只为了居中一行字加一条引用不值）。
            st.fontSize = 11;
            st.padding = new RectOffset(8, 8, 6, 6);
            float w = 320f;
            float h = st.CalcHeight(new GUIContent(text), w - 16f) + 12f;
            Vector2 mp = Event.current.mousePosition;
            Rect r = new Rect(mp.x + 18f, mp.y + 20f, w, h);
            if (r.xMax > _rect.width - 6f) r.x = Mathf.Max(6f, _rect.width - w - 6f);
            if (r.yMax > _rect.height - 6f) r.y = Mathf.Max(6f, mp.y - h - 12f);
            GUI.Box(r, text, st);
        }

        private void DrawWindow(int id)
        {
            // 模式切换放在最上面：它决定了下面所有标签页的含义，
            // 埋在某一页里会让人不知道自己在改什么。
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("模式", "Mode"), GUILayout.Width(40f));
            if (OptionButton(!_useSimple, Loc.T("残酷模式", "Full model"), GUILayout.Width(88f)) && _useSimple)
                SwitchMode(false);
            if (OptionButton(_useSimple, Loc.T("简单模式", "Simple"), GUILayout.Width(88f)) && !_useSimple)
                SwitchMode(true);
            GUILayout.Label(_useSimple
                ? Loc.T("只填目标：涵道比 / 增压比 / 燃烧室温度 / 排气温度 / 直径", "Targets only: bypass ratio / overall pressure ratio / burner exit temperature / exhaust temperature / diameter")
                : Loc.T("填几何：级数 / 叶尖直径 / 轴连接，性能由能量平衡涌现", "Geometry only: stages, tips, shaft links; performance is emergent"), GUILayout.Width(520f));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            // 左：标签
            string[] tabs = CurrentTabs;
            GUILayout.BeginVertical(GUILayout.Width(104f));
            for (int i = 0; i < tabs.Length; i++)
                if (OptionButton(_tab == i, tabs[i]))
                {
                    if (_tab != i) { _tab = i; _scroll = Vector2.zero; }
                }
            GUILayout.FlexibleSpace();
            GUILayout.Label(_target != null ? Loc.T("零件：", "Part: ") + _target.part.partInfo.title : "", GUILayout.Width(90f));
            GUILayout.EndVertical();

            // 剖视页占满"参数 + 判据"两栏：一张图看得清比多两行读数有用。
            // 判据不显示是因为它一直显示在别的页上，而这里要的是面积。
            bool wideCutaway = !_useSimple && _tab == TabCutaway;

            // 中：参数
            GUILayout.BeginVertical(GUILayout.Width(wideCutaway ? 736f : 432f));
            if (wideCutaway)
            {
                DrawCutaway(732f);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                DrawBottomBar();
                return;
            }
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(BodyH), GUILayout.Width(428f));
            if (_useSimple)
            {
                switch (_tab)
                {
                    case 0: DrawSimpleParams(); break;
                    case 1: DrawSimpleReadout(); break;
                    case 2: DrawGrid(); break;
                }
            }
            else
            {
                switch (_tab)
                {
                    case JetForge.Core.DesignTabs.General: DrawGeneral(); break;
                    case JetForge.Core.DesignTabs.Fan: DrawFan(); break;
                    case JetForge.Core.DesignTabs.Compressor: DrawCompressors(); break;
                    case JetForge.Core.DesignTabs.Turbine: DrawTurbines(); break;
                    case JetForge.Core.DesignTabs.Burner: DrawBurner(); break;
                    case JetForge.Core.DesignTabs.Afterburner: DrawAfterburner(); break;
                    case JetForge.Core.DesignTabs.Readout: DrawReadout(); break;
                    case JetForge.Core.DesignTabs.Grid: DrawGrid(); break;
                    case JetForge.Core.DesignTabs.Cutaway: break;   // 剖视走上面那条宽栏分支
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            // 右：判据
            GUILayout.BeginVertical(GUILayout.Width(300f));
            DrawChecks();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            DrawBottomBar();
        }

        /// <summary>底部按钮 + 状态 + 提示框 + 拖动区。剖视页与普通页共用这一份，
        /// 否则"两套底部"迟早一套有按钮一套没有。</summary>
        private void DrawBottomBar()
        {
            GUILayout.BeginHorizontal();
            // 撤销按钮放在底栏最左边：拖滑杆的地方在上面，手停下来眼一扫就在那儿。
            // 按钮文字里带"还能退几步"，所以宽度留 150——英文那句 "Undo (20 steps left)" 约 132 px。
            if (GUILayout.Button(_useSimple ? _simpleUndo.Describe() : _undo.Describe(), GUILayout.Width(150f), GUILayout.Height(28f))) DoUndo();
            if (GUILayout.Button(Loc.T("应用并重建网格", "Apply and rebuild mesh"), GUILayout.Height(28f))) Apply(true);
            if (GUILayout.Button(Loc.T("只应用（不重建）", "Apply only (no rebuild)"), GUILayout.Height(28f))) Apply(false);
            if (GUILayout.Button(Loc.T("设计向导…", "Design wizard…"), GUILayout.Height(28f)))
            {
                if (_target != null) JetForgeWizardWindow.Open(_target);
            }
            if (GUILayout.Button(Loc.T("还原", "Revert"), GUILayout.Height(28f)))
            {
                if (_target != null)
                {
                    if (_useSimple) { _simpleEdit = _target.SimpleCfg != null ? _target.SimpleCfg.Clone() : new SimpleDesign(); _simpleUndo.Reset(_simpleEdit); }
                    else { _edit = _target.Design.Clone(); _undo.Reset(_edit); }
                    Recompute();
                    _status = Loc.T("已还原到零件上的设计", "Reverted to the design on the part");
                }
            }
            if (GUILayout.Button(Loc.T("关闭", "Close"), GUILayout.Height(28f))) _visible = false;
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);

            // 提示框要最后画，才压在所有控件上面。
            DrawHoverHint();

            GUI.DragWindow(new Rect(0f, 0f, _rect.width, 22f));
        }

        /// <summary>
        /// 剖视页：一张二维剖面 + 部件清单。
        ///
        /// 图上的比例取自 `EngineGeometry`（三维网格用的同一份站位表），
        /// 所以"风扇占机长多少"在图上和模型上是一个数——这正是玩家要的"尺寸占比正确"。
        /// 参数一变（滑杆在任何一页动过）这张图自己就重画，不必点应用。
        /// </summary>
        private void DrawCutaway(float width)
        {
            GUILayout.Label(Loc.T("<b>剖视</b>　点部件跳到那一页去改；参数一动，这张图就重画。", "<b>Cutaway</b> Click a part to jump to its page; the drawing redraws as soon as a number changes."));

            EngineStations st = EngineGeometry.Of(_edit);
            double aspect = 2.0 * st.MaxRadius / Math.Max(st.L, 0.1);
            float w = width;
            float h = (float)(w * aspect);
            float maxH = Mathf.Max(BodyH - 96f, 140f);
            if (h > maxH) { h = maxH; w = (float)(h / Math.Max(aspect, 0.05)); }
            if (h < 130f) h = 130f;

            Rect area = GUILayoutUtility.GetRect(w, h);
            int jump = JetForgeCutaway.Draw(area, _edit);
            if (jump >= 0 && jump < TabNames.Length && jump != _tab)
            {
                _tab = jump;
                _scroll = Vector2.zero;
            }

            GUILayout.Label(JetForgeCutaway.Stats + Loc.T("。悬停读尺寸，点一下跳页。", ". Hover to read dimensions, click to jump."));
            GUILayout.Space(4f);

            // 部件清单：图上太窄点不到的东西（单级涡轮、稳焰环）从这里进。
            List<JetForgeMeshBuilder.CutawayShape> shapes = JetForgeMeshBuilder.Cutaway(_edit);
            List<string> names = new List<string>();
            List<int> tabs = new List<int>();
            for (int i = 0; i < shapes.Count; i++)
            {
                if (names.Contains(shapes[i].Name)) continue;
                names.Add(shapes[i].Name);
                tabs.Add(shapes[i].Tab);
            }
            GUILayout.Label(Loc.T("部件：", "Parts: ") + names.Count + Loc.T(" 个（括号里是页号，点一下过去）", " (page number in parentheses, click to jump)"));
            GUILayout.BeginHorizontal();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0 && i % 4 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                string label = tabs[i] >= 0 && tabs[i] < TabNames.Length
                    ? names[i] : names[i];
                if (GUILayout.Button(label, GUILayout.Height(20f)) && tabs[i] >= 0 && tabs[i] < TabNames.Length)
                {
                    _tab = tabs[i];
                    _scroll = Vector2.zero;
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("点这里的部件会跳到对应的页：风扇→风扇页，压气机→压气机页，燃烧室→燃烧室页，涡轮→涡轮页，加力→加力页。", "Clicking a part here jumps to its page: fan, compressor, burner, turbine, afterburner."));
            GUILayout.Label(Loc.T("机匣与外形在总体页改；喷管型式与排气锥在读数页。", "Casing and overall shape are on the General page; nozzle kind and exhaust cone on the Readout page."));
            GUILayout.Label(Loc.T("进气道不在这里：它是独立零件，装在前面用零件自己的面板改。没装就按机匣口径算。", "No intake here: it is a separate part with its own panel. Without one, the case diameter is used."));
        }

        private void Apply(bool rebuildGrid)
        {
            if (_target == null) { _status = Loc.T("没有绑定零件", "No part bound"); return; }
            DateTime t0 = DateTime.UtcNow;
            if (_useSimple) _target.ApplySimpleDesign(_simpleEdit.Clone(), rebuildGrid);
            else _target.ApplyDesign(_edit.Clone(), rebuildGrid);
            // 应用之后零件上的设计对象换成了刚写回的那份（ApplyDesign 收 Clone），
            // 记下身份，免得下一帧把自己刚应用的东西当成"别处改的"再读一遍。
            _boundDesign = _target.Design;
            _boundSimple = _target.SimpleCfg;
            double ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            _status = string.Format(Loc.T("已应用（{0:F0} ms{1}）", "Applied ({0:F0} ms{1})"), ms, rebuildGrid ? Loc.T("，网格已重建", ", grid rebuilt") : "");
            if (rebuildGrid && _target.Grid != null) _status += "：" + _target.Grid.Summary();
            Recompute();
        }

        // ---------- 各页 ----------

        /// <summary>把一套真机参数装进草稿。只改草稿，不碰零件——玩家点「应用」才生效。</summary>
        private void LoadPreset(PresetInfo p)
        {
            _edit = Presets.Build(p.Key);
            _presetNote = p.Label + "　" + p.Note;
            Recompute();
            _undo.Reset(_edit);
        }

        /// <summary>玩家自存设计放哪儿。用 KSPUtil 而不是相对路径——游戏的工作目录不一定是 KSP 根目录。</summary>
        internal static string UserDesignDir
        {
            get { return KSPUtil.ApplicationRootPath + "GameData/JetForge/UserDesigns"; }
        }

        private void RefreshUserList()
        {
            _userEntries = UserLibrary.List(UserDesignDir);
        }

        private void SaveCurrentAsUserDesign()
        {
            string path = UserLibrary.Save(UserDesignDir, _edit.DesignName, _edit.Clone());
            if (path == null)
            {
                _userMessage = Loc.T("保存失败：目录写不进去（只读或磁盘满）", "Save failed: directory not writable (read-only or full)");
            }
            else
            {
                _userMessage = Loc.T("已存到 UserDesigns", "Saved to UserDesigns") + System.IO.Path.DirectorySeparatorChar
                    + System.IO.Path.GetFileName(path);
                // 存成功但旧版本没留下来时要说一声：玩家以为有后悔药，点「史」却是空的。
                if (!string.IsNullOrEmpty(UserLibrary.LastSnapshotError))
                {
                    _userMessage += Loc.T("（旧版本没留下来：", " (no earlier version kept: ")
                        + UserLibrary.LastSnapshotError + Loc.T("）", ")");
                }
            }
            RefreshUserList();
        }

        private void LoadUserDesign(UserDesignEntry e)
        {
            string err;
            EngineDesignData loaded = UserLibrary.Load(e.Path, out err);
            if (loaded == null)
            {
                _userMessage = Loc.T("读不了「", "Cannot read '") + e.Name + Loc.T("」：", "': ") + err;
                return;
            }
            _edit = loaded;
            _undo.Reset(_edit);
            _presetNote = "";
            _userMessage = Loc.T("已载入「", "Loaded '") + e.Name + Loc.T("」（还没应用到零件，点「应用」才生效）", "' (not applied to the part yet; press Apply)");
            // 老存档读得回来是硬要求，但"有些数是缺省填的"得说出来，否则玩家以为那是他当年调的。
            if (!string.IsNullOrEmpty(loaded.MigrationNote)) _userMessage = loaded.MigrationNote;
            Recompute();
        }

        /// <summary>
        /// 两台设计并排比读数。左边是现在手上这台，右边是库里选中的那台。
        /// 每行只说"这个读数谁更好"，不给总分——推力大 10% 与造价低 10% 不是同一种好，
        /// 拍个总分等于替玩家定权重。
        /// </summary>
        private void DrawCompare()
        {
            GUILayout.Space(4f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format(Loc.T("与「{0}」比：左＝现在这台，右＝{0}", "Vs '{0}': left = this one, right = {0}"), _compareName), GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Loc.T("关掉", "Close"), GUILayout.Width(52f), GUILayout.Height(20f)))
            {
                _compare = null;
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                return;
            }
            GUILayout.EndHorizontal();

            if (!_compare.BothOk)
            {
                if (!string.IsNullOrEmpty(_compare.ErrorA)) GUILayout.Label(Loc.T("这台解不出来：", "This one did not solve: ") + _compare.ErrorA);
                if (!string.IsNullOrEmpty(_compare.ErrorB)) GUILayout.Label(Loc.T("那台解不出来：", "That one did not solve: ") + _compare.ErrorB);
            }
            else
            {
                for (int i = 0; i < _compare.Rows.Count; i++)
                {
                    CompareRow row = _compare.Rows[i];
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(row.Label, GUILayout.Width(132f));
                    GUILayout.Label(Num(row, row.A), GUILayout.Width(96f));
                    GUILayout.Label("→", GUILayout.Width(16f));
                    GUILayout.Label(Num(row, row.B), GUILayout.Width(96f));
                    int w = row.Winner();
                    GUILayout.Label(w > 0 ? "◀" : (w < 0 ? "▶" : ""), GUILayout.Width(18f));
                    GUILayout.EndHorizontal();
                }
                GUILayout.Label(string.Format(Loc.T("各有 {0} 项更好、{1} 项更好（◀ 左，▶ 右，空白＝一样或没有好坏）", "{0} better on the left, {1} on the right (blank = same, or no better)"),
                    _compare.BetterA, _compare.BetterB));
            }
            GUILayout.EndVertical();
        }

        private static string Num(CompareRow row, double v)
        {
            string s = v.ToString(row.Format, System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(row.Unit) ? s : s + " " + row.Unit;
        }

        /// <summary>
        /// 交换站面板：Shared 目录里的文件，一行一个，点「入」收进库。
        /// 顺手把目录路径显示出来——玩家得知道该把别人给的文件往哪放。
        /// </summary>
        private void DrawShared()
        {
            GUILayout.Space(4f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format(Loc.T("交换站（{0} 里 {1} 个文件）", "Exchange ({1} file(s) in {0})"),
                UserLibrary.SharedDirName, _shared.Count), GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Loc.T("关掉", "Close"), GUILayout.Width(52f), GUILayout.Height(20f)))
            {
                _shared = null;
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(Loc.T("别人的设计文件放进这个目录就能收进来：", "Drop someone else's design file here to import it: ")
                + UserLibrary.SharedDir(UserDesignDir));

            for (int i = 0; i < _shared.Count; i++)
            {
                UserDesignEntry s = _shared[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(s.Name + "　" + s.Modified.ToString("MM-dd HH:mm"), GUILayout.ExpandWidth(true));
                if (GUILayout.Button(Loc.T("入", "In"), GUILayout.Width(34f), GUILayout.Height(20f)))
                {
                    string ierr;
                    string ip = UserLibrary.Import(UserDesignDir, s.Path, out ierr);
                    if (ip == null)
                    {
                        _userMessage = Loc.T("收不进来：", "Cannot import: ") + (ierr ?? "");
                    }
                    else
                    {
                        _userMessage = Loc.T("已收进库里：", "Imported as ") + System.IO.Path.GetFileNameWithoutExtension(ip)
                            + Loc.T("（可以在上面列表里载入它）", " (load it from the list above)");
                        RefreshUserList();
                    }
                    break;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        /// <summary>
        /// 旧版本列表。每一行是"某次保存之前的那个文件"，点「回到这份」就把它换回来。
        /// 恢复本身也会先留一份底——玩家点错一下不该把当前状态弄丢。
        /// </summary>
        private void DrawHistory()
        {
            GUILayout.Space(4f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format(Loc.T("「{0}」的旧版本（最多留 {1} 份，新的在前）", "'{0}' earlier versions (keeps up to {1}, newest first)"),
                _historyName, UserLibrary.HistoryKeep), GUILayout.ExpandWidth(true));
            if (GUILayout.Button(Loc.T("关掉", "Close"), GUILayout.Width(52f), GUILayout.Height(20f)))
            {
                _history = null;
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                return;
            }
            GUILayout.EndHorizontal();

            for (int i = 0; i < _history.Count; i++)
            {
                UserDesignEntry h = _history[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(h.Name + "　" + h.Modified.ToString("MM-dd HH:mm"), GUILayout.ExpandWidth(true));
                if (GUILayout.Button(Loc.T("回到这份", "Restore"), GUILayout.Width(84f), GUILayout.Height(20f)))
                {
                    string herr;
                    string hp = UserLibrary.Restore(UserDesignDir, _historyName, h.Path, out herr);
                    if (hp == null)
                    {
                        _userMessage = Loc.T("恢复不了：", "Cannot restore: ") + (herr ?? "");
                    }
                    else
                    {
                        EngineDesignData got = UserLibrary.Load(hp, out herr);
                        if (got == null)
                        {
                            _userMessage = Loc.T("恢复后读不出来：", "Restored, but cannot read it: ") + (herr ?? "");
                        }
                        else
                        {
                            _edit = got;
                            _undo.Reset(_edit);
                            _presetNote = "";
                            _userMessage = Loc.T("已回到 ", "Restored ") + h.Name
                                + Loc.T("（你离开的那份也留了底，还能点回去）", " (the state you left is kept as well)");
                            Recompute();
                        }
                    }
                    _history = UserLibrary.History(UserDesignDir, _historyName);
                    RefreshUserList();
                    break;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        private void DrawGeneral()
        {
            GUILayout.Label(Loc.T("<b>总体</b>", "<b>General</b>"));

            // 从真机起步：空白页面前面先放四台真实发动机。
            // 参数是照真机抄几何 + 把涡轮 psi 拟合到真机 OPR 得出来的（见 Core/Presets.cs），
            // 不是随手调的近似值——玩家点一下就能看到一台 1955 年的涡喷长什么样。
            GUILayout.Label(Loc.T("从真机起步（点一下换整套参数，之后随便改）", "Start from a real engine (one click swaps the whole set; edit freely afterwards)"));
            GUILayout.BeginHorizontal();
            IList<PresetInfo> presets = Presets.All;
            for (int i = 0; i < presets.Count; i++)
            {
                if (GUILayout.Button(presets[i].Key, GUILayout.Height(22f))) LoadPreset(presets[i]);
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_presetNote)) GUILayout.Label(_presetNote);

            // 我的设计：把当前草稿存成文件，换飞船也能装同一台。
            if (_userEntries == null) RefreshUserList();
            GUILayout.Space(4f);
            GUILayout.Label(Loc.T("我的设计（存文件，换飞船和重开存档都还在）", "My designs (kept in files; survive other craft and reloads)"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("保存当前为「", "Save current as ") + UserLibrary.Sanitize(_edit.DesignName) + Loc.T("」", "]"), GUILayout.Height(22f)))
                SaveCurrentAsUserDesign();
            if (GUILayout.Button(Loc.T("刷新", "Refresh"), GUILayout.Width(56f), GUILayout.Height(22f))) RefreshUserList();
            if (GUILayout.Button(Loc.T("分享", "Share"), GUILayout.Width(56f), GUILayout.Height(22f)))
            {
                _shared = UserLibrary.Shared(UserDesignDir);
                _userMessage = _shared.Count == 0
                    ? string.Format(Loc.T("交换站里还没有文件：点某台的「出」把设计放进去，或把别人的文件放进 {0} 目录",
                                          "Nothing in the exchange yet: press 'Out' on a design, or drop someone else's file into {0}"), UserLibrary.SharedDirName)
                    : Loc.T("交换站里的文件见下面（「入」收进库里）", "Files in the exchange are listed below (press 'In')");
            }
            GUILayout.EndHorizontal();
            // 提示单独一行：它塞在按钮那一行里时，一句中文能把三个按钮挤成牙签（同一条路，§272）。
            if (!string.IsNullOrEmpty(_userMessage)) GUILayout.Label(_userMessage);

            if (_userEntries.Count == 0)
            {
                GUILayout.Label(Loc.T("（还没有。调好一台点上面的保存，之后在别的飞船上也能载入。）", "(None yet. Tune one and press save above; it loads on other craft too.)"));
            }
else
            {
                // 一台一行。以前是一行硬塞三台（`i % 3` 换行），名字按钮被挤成几十像素，
                // 七个按钮挤在右边——玩家看不清自己存了哪几台，这才是"总体页拥挤"的主因。
                bool listDirty = false;
                for (int i = 0; i < _userEntries.Count; i++)
                {
                    UserDesignEntry e = _userEntries[i];
                    GUILayout.BeginHorizontal();
                    if (_renamingPath == e.Path)
                    {
                        _renameText = GUILayout.TextField(_renameText, GUILayout.Width(240f), GUILayout.Height(20f));
                        if (GUILayout.Button(Loc.T("改", "OK"), GUILayout.Width(28f), GUILayout.Height(20f)))
                        {
                            string rerr;
                            string np = UserLibrary.Rename(UserDesignDir, e.Path, _renameText, out rerr);
                            _userMessage = np != null
                                ? Loc.T("已改名为「", "Renamed to '") + System.IO.Path.GetFileNameWithoutExtension(np) + Loc.T("」", "'")
                                : Loc.T("改不了名：", "Cannot rename: ") + (rerr ?? "");
                            _renamingPath = null;
                            listDirty = true;
                        }
                        if (GUILayout.Button("×", GUILayout.Width(24f), GUILayout.Height(20f))) _renamingPath = null;
                    }
                    else
                    {
                        if (GUILayout.Button(e.Name, GUILayout.Height(20f))) LoadUserDesign(e);
                        if (GUILayout.Button(Loc.T("改名", "Rename"), GUILayout.Width(46f), GUILayout.Height(20f)))
                        {
                            _renamingPath = e.Path;
                            _renameText = e.Name;
                        }
                        if (GUILayout.Button(Loc.T("复制", "Copy"), GUILayout.Width(46f), GUILayout.Height(20f)))
                        {
                            string cerr;
                            string np = UserLibrary.Copy(UserDesignDir, e.Path, UserLibrary.CopyName(UserDesignDir, e.Name), out cerr);
                            _userMessage = np != null
                                ? Loc.T("已复制为「", "Copied to '") + System.IO.Path.GetFileNameWithoutExtension(np) + Loc.T("」", "'")
                                : Loc.T("复制不了：", "Cannot copy: ") + (cerr ?? "");
                            listDirty = true;
                        }
                        if (GUILayout.Button(Loc.T("比", "Vs"), GUILayout.Width(30f), GUILayout.Height(20f)))
                        {
                            string cerr2;
                            EngineDesignData other = UserLibrary.Load(e.Path, out cerr2);
                            _compare = DesignCompare.Run(_edit, other);
                            _compareName = e.Name;
                            _userMessage = other == null
                                ? Loc.T("读不出来：", "Cannot read: ") + (cerr2 ?? "")
                                : "";
                        }
                        if (GUILayout.Button(Loc.T("史", "Hist"), GUILayout.Width(34f), GUILayout.Height(20f)))
                        {
                            _history = UserLibrary.History(UserDesignDir, e.Name);
                            _historyName = e.Name;
                            _userMessage = _history.Count == 0
                                ? Loc.T("这台还没有旧版本（第一次保存的时候没有旧文件可留）", "No earlier versions yet (nothing to keep on the first save)")
                                : "";
                        }
                        if (GUILayout.Button(Loc.T("出", "Out"), GUILayout.Width(30f), GUILayout.Height(20f)))
                        {
                            string xerr;
                            string xp = UserLibrary.Export(UserDesignDir, e.Path, out xerr);
                            _userMessage = xp != null
                                ? Loc.T("已放进交换站：", "Put into the exchange: ") + System.IO.Path.GetFileName(xp)
                                : Loc.T("放不进去：", "Cannot export: ") + (xerr ?? "");
                        }
                        if (GUILayout.Button("×", GUILayout.Width(24f), GUILayout.Height(20f)))
                        {
                            _userMessage = UserLibrary.Delete(e.Path) ? Loc.T("已删除「", "Deleted ") + e.Name + Loc.T("」", "]") : Loc.T("删不掉「", "Cannot delete ") + e.Name + Loc.T("」", "]");
                            if (_renamingPath == e.Path) _renamingPath = null;
                            listDirty = true;
                        }
                    }
                    GUILayout.EndHorizontal();
                    // 名字/改名/复制/删都会让这张表失效，等这一行走完再刷新——
                    // 边走边刷会让后面几行拿到的 `e` 指向已经被换掉的文件。
                    if (listDirty) break;
                }
                if (listDirty) RefreshUserList();
                else
                {
                    if (_compare != null) DrawCompare();
                    if (_history != null) DrawHistory();
                    if (_shared != null) DrawShared();
                }
            }
            _edit.DesignName = Text(Loc.T("名称", "Name"), _edit.DesignName);
            _edit.TechName = TechSelector();
            bool turbofan = _edit.Kind == EngineKind.Turbofan;
            GUILayout.BeginHorizontal();
            if (OptionButton(turbofan, Loc.T("涡扇（含风扇与涵道）", "Turbofan (fan + bypass)")) && !turbofan)
            {
                _edit.Kind = EngineKind.Turbofan;
                Recompute();
            }
            if (OptionButton(!turbofan, Loc.T("涡喷（无风扇）", "Turbojet (no fan)")) && turbofan)
            {
                _edit.Kind = EngineKind.Turbojet;
                Recompute();
            }
            GUILayout.EndHorizontal();

            // 控制系统：按年代解锁，不是玩家的自由选择。
            // 1940s 的机器没有调节器，油门就是油量；1970s 才有液压调速；
            // 1990s 之后才有把 T4、转速、喘振裕度一起闭环的数字控制。
            // 选了超年代的东西**不替你改回去**——留着让你看见判据在说什么，
            // 运行时按年代上限生效（Effective），免得"设计里写着数字控制"却跑不出数字控制的手感。
            GUILayout.Space(4f);
            ControlKind eraMax = JetForge.Core.ControlSystem.Allowed(_edit.Tech);
            if (ControlSelector()) Recompute();
            if (JetForge.Core.ControlSystem.Rank(_edit.Control) > JetForge.Core.ControlSystem.Rank(eraMax))
            {
                GUILayout.Label("  ⚠ " + _edit.TechName + Loc.T(" 造不出「", " cannot build ") + JetForge.Core.ControlSystem.KindName(_edit.Control)
                    + Loc.T("」，实际按「", "; running as ") + JetForge.Core.ControlSystem.KindName(eraMax) + Loc.T("」跑。", "."));
            }
            GUILayout.Label(Loc.T("  无控：油门给多少油就烧多少，超温、超转、喘振要飞行员自己看着躲。", "  No control: fuel follows the throttle; watch overtemp, overspeed and surge yourself."));
            GUILayout.Label(Loc.T("  有控：限温、限转、加减速限幅；数字式的还会在喘振裕度见底时替你压油门。", "  With FADEC: T4 and speed limits plus spool-rate limits; digital types also back off the throttle when surge margin runs out."));

            // 细节等级：只影响外形，不参与气动。低档一台叶片都不加（省三角），
            // 高档连静子、法兰、附件机匣都画出来——同一台机器摆二十个时，这笔三角数是要算的。
            GUILayout.Space(4f);
            if (Selector(Loc.T("细节等级", "Detail level"), ref _edit.Detail, DetailNames)) Recompute();
            GUILayout.Label("  " + JetForge.Core.DetailSpec.Describe(_edit.Detail)
                            + Loc.T("（只影响外形，不参与气动）", " (visual only, no effect on aerodynamics)"));

            if (Slider(Loc.T("机匣进气口直径 a", "Casing inlet diameter a"), ref _edit.InletDiameter, 0.2f, 3.0f, "F2", " m", true)) { }
            GUILayout.Label(Loc.T("  这是发动机自己的进气口尺寸（机匣口径），决定唇口对接多大。", "  The engine casing's own inlet size; it sets the lip diameter the intake docks to.")
                            + Loc.T("进气道是独立零件：捕获面积、喉道、型式、迎角都在零件面板里改。", "The intake is a separate part: capture area, throat, type and incidence live in its own panel."));
            // 整机长度不再是滑杆：它是从各部件（风扇/压气机几级、涡轮几级、燃烧室多长、有没有加力）
// 加出来的，改任何一件都会让它跟着变。想让它变长，去改那件——不是改这个数。
            GUILayout.Label(string.Format(Loc.T("整机长度 {0:F2} m（按部件算出，不能直接设）", "Overall length {0:F2} m (summed from the parts; not settable directly)"),
                _edit.EffectiveLength));
            GUILayout.Label("  " + JetForge.Core.EngineLength.Breakdown(_edit));
            GUILayout.Label(Loc.T("  长度进物理：管道摩擦（L/D → 总压损失）、质量、造价、外形。", "  Length is physical: duct friction (L/D to pressure loss), mass, cost, silhouette.")
                            + Loc.T("把压气机从 17 级改成 9 级，机器会真的短两米、管道损失跟着小。", "Take the compressor from 17 stages to 9 and the engine really gets two metres shorter, with less duct loss."));
            if (Slider(Loc.T("核心机进口直径（0=自动）", "Core inlet dia (0 = auto)"), ref _edit.CoreInletDiameter, 0.0f, 2.0f, "F2", " m", true)) { }
            if (Slider(Loc.T("设计马赫", "Design Mach"), ref _edit.DesignMach, 0.0f, 2.5f, "F2", "", true)) { }
            if (Slider(Loc.T("设计高度", "Design altitude"), ref _edit.DesignAlt, 0.0f, 20000.0f, "F0", " m", true)) { }
            GUILayout.Label(Loc.T("设计点是整台机器的标定点：冲压升温、进气总压恢复、流量都按它算。", "The design point calibrates the whole engine: ram rise, intake recovery and mass flow all follow it."));

            GUILayout.Space(4f);
            GUILayout.Label(string.Format(Loc.T("轴数：{0}", "Shafts: {0}"), _edit.ShaftCount));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("1 轴", "1 shaft"))) { _edit.ShaftCount = 1; Recompute(); }
            if (GUILayout.Button(Loc.T("2 轴", "2 shafts"))) { _edit.ShaftCount = 2; Recompute(); }
            if (GUILayout.Button(Loc.T("3 轴", "3 shafts"))) { _edit.ShaftCount = 3; Recompute(); }
            GUILayout.EndHorizontal();

            GUILayout.Space(2f);
            GUILayout.Label(string.Format(Loc.T("涵道比（由几何推出）：{0:F2}", "Bypass ratio (from geometry): {0:F2}"), _edit.BypassRatio()));
            if (Slider(Loc.T("涵道比覆盖（0=自动）", "BPR override (0 = auto)"), ref _edit.OverrideBpr, 0.0f, 12.0f, "F2", "", true)) { }

            // 慢车不再是一个旋钮。它是"这台机器自己转着、火还留得住"的最低那一档，
            // 由求解器反解 T4（见 `OffDesign.CalibrateIdleAuto`），飞行里油门到底就是它。
            // 以前这里让人填"慢车推力占满油门几成"，等于把结果当输入：填小一点是诈，
            // 填大一点就把慢车变成"喘振/超温前的高功率档"。
            GUILayout.Space(4f);
            GUILayout.Label(Loc.T("<b>慢车（自动）</b>", "<b>Idle (auto)</b>"));
            if (_idleFuel > 1e-6)
            {
                GUILayout.Label(string.Format(Loc.T("  慢车 T4 {0:F0} K｜油气比 {1:F4}（贫油下限 {2:F4}）", "  Idle T4 {0:F0} K | FAR {1:F4} (lean limit {2:F4})"),
                    _idleT4, _idleFar, 0.0050));
                GUILayout.Label(string.Format(Loc.T("  最低可用喷油量 {0:F3} kg/s（{1:F0} kg/h）——油门到底就是这一档", "  Minimum usable fuel flow {0:F3} kg/s ({1:F0} kg/h) - this is throttle zero"),
                    _idleFuel, _idleFuel * 3600.0));
                GUILayout.Label(string.Format(Loc.T("  折算推力占满油门 {0:P1}（真机 6~8%，这个数由上面的几何与循环算出来）", "  Idle thrust is {0:P1} of full throttle (real engines 6-8%; computed from the geometry and cycle above)"),
                    _idleFrac));
            }
            else
            {
                GUILayout.Label(Loc.T("  反解失败：先看看判据页有没有 FAIL。慢车点算不出来时不会给你一个假数。", "  Idle solve failed: check the Criteria page for FAILs. No fake number appears when the idle point cannot be solved."));
            }
        }

        private void DrawFan()
        {
            GUILayout.Label(Loc.T("<b>风扇</b>", "<b>Fan</b>"));
            if (_edit.Kind == EngineKind.Turbojet)
            {
                GUILayout.Label(Loc.T("涡喷没有风扇。要在总体页改成涡扇。", "A turbojet has no fan. Switch to turbofan on the General page."));
                return;
            }
            Slider(Loc.T("风扇直径", "Fan diameter"), ref _edit.Fan.Diameter, 0.2f, 3.0f, "F2", " m", true);
            _edit.Fan.Stages = (int)SliderI(Loc.T("叶片级数", "Blade stages"), _edit.Fan.Stages, 1, 4);
            Slider(Loc.T("轮毂/叶尖比", "Hub/tip ratio"), ref _edit.Fan.HubTipRatio, 0.2f, 0.75f, "F2", "", true);
            Slider(Loc.T("风扇压比（0=自动）", "Fan PR (0 = auto)"), ref _edit.Fan.PressureRatio, 0.0f, 3.0f, "F2", "", true);
            _edit.Fan.Shaft = (int)SliderI(Loc.T("挂在第几轴", "Shaft"), _edit.Fan.Shaft, 1, _edit.ShaftCount);
            _edit.Fan.BladeCount = (int)SliderI(Loc.T("叶片数（0=按直径自动）", "Blade count (0 = from diameter)"), _edit.Fan.BladeCount, 0, 48);

            // ---- 齿轮传动（GTF）----
            GUILayout.Space(3f);
            Slider(Loc.T("减速齿轮比（1=直驱）", "Gear ratio (1 = direct)"), ref _edit.Fan.GearRatio, 1.0f, 4.0f, "F2", "", true);
            if (_edit.Fan.GearRatio > 1.0001f)
            {
                Slider(Loc.T("齿轮效率（0=0.995）", "Gear efficiency (0 = 0.995)"), ref _edit.Fan.GearEta, 0.0f, 1.0f, "F3", "", true);
                GUILayout.Label(string.Format(Loc.T("风扇转速 = 低压轴转速 / {0:F2}　｜　齿轮箱 {1:F0} kg", "Fan speed = LP shaft speed / {0:F2}　|　gearbox {1:F0} kg"),
                    _edit.Fan.GearRatio, MassCost.Estimate(_edit).Gearbox));
                // 柔度跟在质量后面：同一个箱体，两个数说的是两件事（多重、多软）。
                // 扭角只有设计点解出来才有；没解出来就明说没解，不给一个看着像 0 的数。
                if (_preview != null && _preview.GearboxTorsionPresent)
                    GUILayout.Label(string.Format(Loc.T("　扭 {0:F0} kN·m 下转 {1:F3}°　｜　本体扭振 {2:F0} Hz", "  twist {1:F3} deg at {0:F0} kN m　|　body torsion {2:F0} Hz"),
                        _preview.GearboxTorque / 1000.0, _preview.GearboxTwistDeg, _preview.GearboxFreqHz));
                else
                    GUILayout.Label(Loc.T("　柔度要等设计点解出来才算（现在还没有解）", "  flexibility needs a solved design point (none yet)"));
            }
            int autoBlades = JetForgeMeshBuilder.BladesOf(_edit);
            GUILayout.Label(string.Format(Loc.T("实际叶片 {0} 片{1}", "{0} blades as built{1}"),
                autoBlades, _edit.Fan.BladeCount > 0 ? Loc.T("（手动）", " (manual)") : Loc.T("（自动）", " (auto)")));
            GUILayout.Space(3f);
            GUILayout.Label(string.Format(Loc.T("涵道比 {0:F2}　核心机进口 {1:F2} m", "Bypass ratio {0:F2}　core inlet {1:F2} m"),
                _edit.BypassRatio(), _edit.CoreInletD));
        }

        private void DrawCompressors()
        {
            GUILayout.Label(Loc.T("<b>压气机</b>", "<b>Compressor</b>"));
            DrawComp(Loc.T("低压压气机 LPC", "Low-pressure compressor (LPC)"), _edit.Lpc);
            GUILayout.Space(6f);
            DrawComp(Loc.T("高压压气机 HPC", "High-pressure compressor (HPC)"), _edit.Hpc);
        }

        private static readonly string[] CompressorKindNames = { Loc.T("轴流 axial", "Axial"), Loc.T("离心 centrifugal", "Centrifugal") };

        private void DrawComp(string title, CompressorSpec c)
        {
            GUILayout.Label(title);
            if (Selector(Loc.T("  形式", "  Form"), ref c.Kind, CompressorKindNames)) { }
            c.Stages = (int)SliderI(Loc.T("  级数", "  Stages"), c.Stages, 0, 16);
            Slider(Loc.T("  叶尖直径", "  Tip diameter"), ref c.TipDiameter, 0.1f, 1.6f, "F2", " m", true);
            Slider(Loc.T("  轮毂/叶尖比", "  Hub/tip ratio"), ref c.HubTipRatio, 0.3f, 0.92f, "F2", "", true);
            Slider(Loc.T("  载荷系数 ×", "  Loading factor ×"), ref c.PsiScale, 0.3f, 1.6f, "F2", "", true);
            c.Shaft = (int)SliderI(Loc.T("  挂在第几轴", "  Shaft"), c.Shaft, 1, _edit.ShaftCount);
            bool vigvOn = c.Vigv && c.Stages >= 4 && c.Kind == CompressorKind.Axial;
            c.Vigv = GUILayout.Toggle(c.Vigv, Loc.T("  可调静子", "  Variable IGVs"), GUILayout.Width(300f));
            if (!c.Vigv)
                GUILayout.Label(Loc.T("    没有静子：低转速会诚实地报「已越过喘振线」，这是模型在说真话。", "    No IGVs: at low speed it honestly reports 'past the surge line' — the model is telling the truth."));
            else if (!vigvOn)
                GUILayout.Label(Loc.T("    这台机器用不上它：离心级或级数 < 4，真机也没有这套作动机构。", "    This engine cannot use it: centrifugal stages, or fewer than 4 stages — real engines have no such actuator."));

            bool bleedOn = c.Bleed && c.Stages >= 4 && c.Kind == CompressorKind.Axial;
            c.Bleed = GUILayout.Toggle(c.Bleed, Loc.T("  中间级放气", "  Mid-stage bleed"), GUILayout.Width(300f));
            if (!c.Bleed)
                GUILayout.Label(Loc.T("    不放气：核心流量多一点、推力大一点。", "    No bleed: a little more core flow, a little more thrust."));
            else if (!bleedOn)
                GUILayout.Label(Loc.T("    这台机器用不上它：离心级或级数 < 4，没有中间级可抽。", "    This engine cannot use it: centrifugal stages, or fewer than 4 stages — nothing in the middle to tap."));
            else if (c.BleedFrac > 1e-6)
                Slider(Loc.T("  放气比例", "  Bleed fraction"), ref c.BleedFrac, 0.01f, 0.25f, "F3", "", false);
            else
            {
                GUILayout.Label(Loc.T("    自动：低于 92% 转速开阀，最多抽 12%；设计点一分不放。", "    Automatic: valve opens below 92% speed, up to 12%; nothing at all at the design point."));
                GUILayout.Label(Loc.T("    注意：本模型把压气机当一个整体，抽走的气只减核心流量（推力变小），", "    Note: this model treats the compressor as one lump, so bled air only lowers core flow (less thrust);"));
                GUILayout.Label(Loc.T("    还看不到逐级放气对喘振裕度的好处——那要等逐级求解。", "    the surge-margin benefit of stage-by-stage bleed is not visible yet — that needs a stage-by-stage solver."));
            }
            DrawCompStages(c);
        }

        /// <summary>
        /// 逐级编辑：每一级自己的叶尖直径与载荷系数。
        ///
        /// 为什么值得做：真机的压气机流道是**收敛**的（气流越压越窄），一台九级机
        /// 从第一级到最后一级叶尖能缩掉三成；这里以前只有一个叶尖直径，等于把九级
        /// 画成一样粗。改了以后求解器逐级算压比与温升，网格也逐级画环。
        /// </summary>
        private void DrawCompStages(CompressorSpec c)
        {
            if (c.Stages <= 0) return;
            c.SyncStages();
            GUILayout.BeginHorizontal();
            _showStages = GUILayout.Toggle(_showStages, Loc.T("  逐级编辑", "  Stages"), GUILayout.Width(90f));
            if (GUILayout.Button(Loc.T("按收敛填满", "Fill inward"), GUILayout.Width(90f)))
            {
                // 轴流：叶尖从大到小（0.72 倍收口）；离心：一级一个叶轮，几乎不收。
                double taper = c.Kind == CompressorKind.Centrifugal ? 0.94 : 0.72;
                for (int i = 0; i < c.Stages; i++)
                {
                    double f = c.Stages <= 1 ? 1.0 : (double)i / (c.Stages - 1);
                    c.StageTip[i] = c.TipDiameter * (1.0 + (taper - 1.0) * f);
                    c.StagePsi[i] = 0.0;
                }
                Recompute();
            }
            if (GUILayout.Button(Loc.T("清空", "Clear"), GUILayout.Width(60f)))
            {
                c.StageTip.Clear();
                c.StagePsi.Clear();
                c.SyncStages();
                Recompute();
            }
            GUILayout.EndHorizontal();
            if (!_showStages) return;
            for (int i = 0; i < c.Stages; i++)
            {
                double tip = c.StageTip[i] > 0.0 ? c.StageTip[i] : c.TipDiameter;
                if (Slider(Loc.T("  第 ", "  stage ") + (i + 1) + Loc.T(" 级叶尖", " tip"), ref tip, 0.05f, 1.60f, "F3", " m", false))
                {
                    c.StageTip[i] = tip;
                    Recompute();
                }
                double psi = c.StagePsi[i] > 0.0 ? c.StagePsi[i] : c.PsiScale;
                if (Slider(Loc.T("  第 ", "  stage ") + (i + 1) + Loc.T(" 级载荷 ×", " loading ×"), ref psi, 0.20f, 1.60f, "F3", "", false))
                {
                    c.StagePsi[i] = psi;
                    Recompute();
                }
            }
        }

        private void DrawTurbines()
        {
            GUILayout.Label(Loc.T("<b>涡轮</b>", "<b>Turbine</b>"));
            DrawTurb(Loc.T("高压涡轮 HPT", "High-pressure turbine (HPT)"), _edit.Hpt);
            GUILayout.Space(4f);
            DrawTurb(Loc.T("中压涡轮 IPT（0 级=没有）", "Intermediate turbine (IPT; 0 stages = none)"), _edit.Ipt);
            GUILayout.Space(4f);
            DrawTurb(Loc.T("低压涡轮 LPT", "Low-pressure turbine (LPT)"), _edit.Lpt);
        }

        private void DrawTurb(string title, TurbineSpec t)
        {
            GUILayout.Label(title);
            t.Stages = (int)SliderI(Loc.T("  级数", "  Stages"), t.Stages, 0, 8);
            Slider(Loc.T("  叶尖直径", "  Tip diameter"), ref t.TipDiameter, 0.1f, 1.6f, "F2", " m", true);
            Slider(Loc.T("  轮毂/叶尖比", "  Hub/tip ratio"), ref t.HubTipRatio, 0.45f, 0.92f, "F2", "", true);
            Slider(Loc.T("  载荷系数 ×", "  Loading factor ×"), ref t.PsiScale, 0.3f, 1.6f, "F2", "", true);
            t.Shaft = (int)SliderI(Loc.T("  驱动第几轴", "  Drives shaft"), t.Shaft, 1, _edit.ShaftCount);
            DrawTurbStages(t);
        }

        /// <summary>涡轮流道反过来是**外扩**的（越往后气流体积越大），缺省按 1.12 倍铺开。</summary>
        private void DrawTurbStages(TurbineSpec t)
        {
            if (t.Stages <= 0) return;
            t.SyncStages();
            GUILayout.BeginHorizontal();
            _showStages = GUILayout.Toggle(_showStages, Loc.T("  逐级编辑", "  Stages"), GUILayout.Width(90f));
            if (GUILayout.Button(Loc.T("按外扩填满", "Fill outward"), GUILayout.Width(90f)))
            {
                for (int i = 0; i < t.Stages; i++)
                {
                    double f = t.Stages <= 1 ? 0.0 : (double)i / (t.Stages - 1);
                    t.StageTip[i] = t.TipDiameter * (1.0 + 0.12 * f);
                    t.StagePsi[i] = 0.0;
                }
                Recompute();
            }
            if (GUILayout.Button(Loc.T("清空", "Clear"), GUILayout.Width(60f)))
            {
                t.StageTip.Clear();
                t.StagePsi.Clear();
                t.SyncStages();
                Recompute();
            }
            GUILayout.EndHorizontal();
            if (!_showStages) return;
            for (int i = 0; i < t.Stages; i++)
            {
                double tip = t.StageTip[i] > 0.0 ? t.StageTip[i] : t.TipDiameter;
                if (Slider(Loc.T("  第 ", "  stage ") + (i + 1) + Loc.T(" 级叶尖", " tip"), ref tip, 0.05f, 1.60f, "F3", " m", false))
                {
                    t.StageTip[i] = tip;
                    Recompute();
                }
                double psi = t.StagePsi[i] > 0.0 ? t.StagePsi[i] : t.PsiScale;
                if (Slider(Loc.T("  第 ", "  stage ") + (i + 1) + Loc.T(" 级载荷 ×", " loading ×"), ref psi, 0.20f, 1.60f, "F3", "", false))
                {
                    t.StagePsi[i] = psi;
                    Recompute();
                }
            }
        }

        private static readonly string[] BurnerNames = { Loc.T("罐式 can", "Can"), Loc.T("罐环式 cannular", "Cannular"), Loc.T("环形 annular", "Annular"), Loc.T("短环形 short_annular", "Short annular") };

        private void DrawBurner()
        {
            GUILayout.Label(Loc.T("<b>燃烧室</b>", "<b>Burner</b>"));
            if (Selector(Loc.T("形式", "Form"), ref _edit.Burner.Kind, BurnerNames)) { }
            Slider(Loc.T("长度", "Length"), ref _edit.Burner.Length, 0.10f, 1.20f, "F2", " m", true);
            Slider(Loc.T("出口温度 T4", "Exit temperature T4"), ref _edit.Burner.ExitTemp, 700.0f, 2200.0f, "F0", " K", true);
            Slider(Loc.T("总压损失 ΔP/P（-1=按型查表）", "Pressure loss ΔP/P (-1=tbl)"), ref _edit.Burner.DpRatio, -1.0f, 0.15f, "F3", "", true);
            Slider(Loc.T("燃烧效率（-1=按型查表）", "Combustion eff. (-1 = table)"), ref _edit.Burner.EtaComb, -1.0f, 1.0f, "F3", "", true);
            Slider(Loc.T("火焰筒长径比 L/H（0=按型）", "Liner L/H (0 = from type)"), ref _edit.Burner.LinerLd, 0.0f, 6.0f, "F2", "", true);
            _edit.Burner.CanCount = (int)SliderI(Loc.T("罐数（0=按型，环形无罐）", "Cans (0 = from type; annular has none)"), _edit.Burner.CanCount, 0, 20);

            Cycle.BurnerPerf bp = Cycle.BurnerPerformance(_edit.Burner);
            GUILayout.Label(string.Format(Loc.T("实际采用：ΔP/P={0:F3}　η_b={1:F3}　L/H={2:F2}　罐数={3}", "Actually used: ΔP/P={0:F3}　η_b={1:F3}　L/H={2:F2}　cans={3}"),
                1.0 - bp.Sigma, bp.Eta, bp.Ld, bp.Cans));
            GUILayout.Label(string.Format(Loc.T("出口温度不均匀度 {0:F2}（涡轮热点 ≈ +{1:F0} K）", "Exit temperature spread {0:F2} (turbine hot spot ≈ +{1:F0} K)"),
                bp.PatternFactor, bp.PatternFactor * _edit.Burner.ExitTemp));
        }

        private static readonly string[] AbNames = { Loc.T("环形 ring", "Ring"), Loc.T("喷雾环 spray_ring", "Spray ring"), Loc.T("直喷 dump", "Dump") };

        private void DrawAfterburner()
        {
            GUILayout.Label(Loc.T("<b>加力燃烧室</b>", "<b>Afterburner</b>"));
            _edit.Afterburner.Enabled = GUILayout.Toggle(_edit.Afterburner.Enabled, Loc.T("配备加力", "Fitted with afterburner"));
            if (!_edit.Afterburner.Enabled)
            {
                GUILayout.Label(Loc.T("没有加力时，喷油量输入无效。", "Without an afterburner the fuel-ratio input does nothing."));
                return;
            }
            if (Selector(Loc.T("形式", "Form"), ref _edit.Afterburner.Kind, AbNames)) { }
            // 加力段是独立的一节筒体：太短燃气烧不完，太长白占机身长度
            double abLen = _edit.Afterburner.Length;
            Slider(Loc.T("加力段长度（0=按核心机自适应）", "AB length (0 = fit core)"), ref abLen, 0.0f, 3.0f, "F2", " m", true);
            _edit.Afterburner.Length = abLen;
            Slider(Loc.T("喷油量 far_ab（0=按温度）", "far_ab (0 = from T)"), ref _edit.Afterburner.FuelRatio, 0.0f, 0.045f, "F4", "", true);
            Slider(Loc.T("加力出口温度 T7", "AB exit temperature T7"), ref _edit.Afterburner.ExitTemp, 1200.0f, 2200.0f, "F0", " K", true);

            GUILayout.Space(3f);
            _edit.Afterburner.AutoLight = GUILayout.Toggle(_edit.Afterburner.AutoLight,
                Loc.T("油门过线自动点亮", "Light above throttle line"));
            if (_edit.Afterburner.AutoLight)
            {
                double lt = _edit.Afterburner.LightThrottle;
                Slider(Loc.T("点亮油门", "Light throttle"), ref lt, 0.30f, 0.95f, "P0", "", true);
                _edit.Afterburner.LightThrottle = lt;
                GUILayout.Label(string.Format(
                    Loc.T("油门 {0:P0} 以下纯军推；往上线性注入，100% 油门才是满加力。\n", "Below {0:P0} throttle it is dry thrust; above that it ramps in linearly, and only at 100% is it full afterburner.\n")
                    + Loc.T("飞行中那个「加力」按钮仍然能强制点亮。", "The in-flight 'Afterburner' button can still force it on."),
                    _edit.Afterburner.LightThrottle));
            }
            else
            {
                GUILayout.Label(Loc.T("关闭自动点亮：只能靠飞行中的「加力」按钮开，全开全关。", "Auto-light off: only the in-flight 'Afterburner' button lights it, all or nothing."));
            }

            if (_edit.Nozzle == NozzleKind.Converging)
                GUILayout.Label(Loc.T("<color=#ffcc44>加力机的喷管会被气流卡住——建议改成可变面积或收扩喷管。</color>", "<color=#ffcc44>An afterburning engine will choke its own nozzle — switch to a variable-area or convergent-divergent nozzle.</color>"));
        }

        private static readonly string[] NozzleNames = { Loc.T("固定收敛", "Fixed convergent"), Loc.T("可变面积", "Variable area"), Loc.T("收扩 C-D", "Convergent-divergent") };
        /// <summary>
        /// 控制系统选择器。`Selector` 是按下标映射枚举值的，而 `ControlKind` 的数值顺序
        /// （None 0、Hydro 1、Fadec 2、Mechanical 3、Analog 4）是为了存档兼容才长成这样，
        /// 跟能力高低不是一回事——照值摆按钮会把"纯机械调速器"排在"全权限数字"右边。
        /// 所以这里按能力顺序自己摆，按钮上写的是人话。
        /// </summary>
        private bool ControlSelector()
        {
            JetForge.Core.ControlKind[] order =
            {
                JetForge.Core.ControlKind.None,
                JetForge.Core.ControlKind.Mechanical,
                JetForge.Core.ControlKind.Hydro,
                JetForge.Core.ControlKind.Analog,
                JetForge.Core.ControlKind.Fadec
            };
            GUILayout.BeginHorizontal();
            GUILayout.Label(H(Loc.T("控制系统", "Control system")), GUILayout.Width(130f));
            bool changed = false;
            for (int i = 0; i < order.Length; i++)
            {
                bool on = _edit.Control == order[i];
                if (OptionButton(on, ControlBtnName(order[i]), GUILayout.Width(72f)) && !on)
                {
                    _edit.Control = order[i];
                    changed = true;
                }
            }
            GUILayout.EndHorizontal();
            return changed;
        }

        private static string ControlBtnName(JetForge.Core.ControlKind k)
        {
            switch (k)
            {
                case JetForge.Core.ControlKind.Mechanical: return Loc.T("机械调速", "Mechanical governor");
                case JetForge.Core.ControlKind.Hydro: return Loc.T("液压", "Hydraulic");
                case JetForge.Core.ControlKind.Analog: return Loc.T("模拟 FADEC", "Analog FADEC");
                case JetForge.Core.ControlKind.Fadec: return Loc.T("数字 FADEC", "Digital FADEC");
                default: return Loc.T("无控", "No control");
            }
        }

        private static string[] DetailNames
        {
            get
            {
                if (_detailNames == null || _detailLang != Loc.Current)
                {
                    _detailNames = new[] { Loc.T("低", "Low"), Loc.T("中", "Medium"), Loc.T("高", "High") };
                    _detailLang = Loc.Current;
                }
                return _detailNames;
            }
        }

        private static string[] _detailNames;
        private static JetForge.Core.Lang _detailLang;

        private void DrawReadout()
        {
            GUILayout.Label(Loc.T("<b>设计点读数</b>", "<b>Design-point readout</b>"));
            // 先给结论，再给数：逐条判据说"哪个数越界"，这里说"那我该先改什么"。
            if (_verdict != null)
            {
                GUILayout.Label(Loc.T("<b>评价</b>　", "<b>Verdict</b>　") + _verdict.Headline);
                for (int i = 0; i < _verdict.Lines.Count; i++)
                    GUILayout.Label("  " + _verdict.Lines[i]);
                GUILayout.Space(6f);
            }
            // 这些数有多少是算出来的、多少是估出来的：一行说清（详见自检日志与文档 §170）。
            GUILayout.Label(Loc.T("<color=#9fd0ff>模型精度</color>　", "<color=#9fd0ff>Model accuracy</color>　") + ModelLedger.Summary()
                            + Loc.T("（A 守恒解／B 经验式／C 真机标定／D 游戏近似）", "(A conservation-based / B empirical / C calibrated to real engines / D game approximation)"));
            if (Selector(Loc.T("喷管", "Nozzle"), ref _edit.Nozzle, NozzleNames)) { }
            GUILayout.BeginHorizontal();
            bool vec = GUILayout.Toggle(_edit.ThrustVectoring, Loc.T(" 矢量喷口", " Vector nozzle"), GUILayout.Width(110f));
            if (vec != _edit.ThrustVectoring) _edit.ThrustVectoring = vec;
            GUILayout.Label(_edit.Nozzle == NozzleKind.Converging
                ? Loc.T("<color=#ffcc44>收敛喷管没有可动段，偏不了</color>", "<color=#ffcc44>A convergent nozzle has no moving section — it cannot vector</color>")
                : Loc.T("跟着飞控的俯仰/偏航指令偏", "Follows the flight computer's pitch/yaw commands"), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            if (_edit.ThrustVectoring && _edit.Nozzle != NozzleKind.Converging)
            {
                Slider(Loc.T("最大偏转角", "Maximum deflection"), ref _edit.GimbalMaxDeg, 0.0f, 25.0f, "F0", " °", true);
                // 三个方向的开关：偏航那一轴最容易被 SAS 噪声带得发抖，
                // 关掉它喷口就只做俯仰。装在轴线上时滚转本来就没有力臂，开关留着但会写明。
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("  跟着偏的轴", "  Axes"), GUILayout.Width(96f));
                bool vp = GUILayout.Toggle(_edit.VectorPitch, Loc.T(" 俯仰", " Pitch"), GUILayout.Width(70f));
                if (vp != _edit.VectorPitch) _edit.VectorPitch = vp;
                bool vy = GUILayout.Toggle(_edit.VectorYaw, Loc.T(" 偏航", " Yaw"), GUILayout.Width(70f));
                if (vy != _edit.VectorYaw) _edit.VectorYaw = vy;
                bool vr = GUILayout.Toggle(_edit.VectorRoll, Loc.T(" 滚转", " Roll"), GUILayout.Width(70f));
                if (vr != _edit.VectorRoll) _edit.VectorRoll = vr;
                GUILayout.EndHorizontal();
                GUILayout.Label(Loc.T("指令带 4% 死区与限速率：SAS 的微抖不会让花瓣颤。滚转要有偏心力矩才生效。", "The command has a 4% dead band and a slew limit so SAS jitter does not shake the petals. Roll only works with an off-axis thrust line."));
                GUILayout.Label(string.Format(
                    Loc.T("偏满时轴向推力 {0:P1}、侧向 {1:P1}——偏转只换方向，不增加推力。", "At full deflection the axial thrust is {0:P1} and the sideways {1:P1} — vectoring turns thrust, it does not add any."),
                    System.Math.Cos(_edit.GimbalMaxDeg * System.Math.PI / 180.0),
                    System.Math.Sin(_edit.GimbalMaxDeg * System.Math.PI / 180.0)));
            }
            Slider(Loc.T("音效音量", "Sound volume"), ref _edit.SoundGain, 0.0f, 2.0f, "F2", " ×", true);
            GUILayout.Space(4f);
            if (_preview == null)
            {
                GUILayout.Label(Loc.T("没有结果。", "No result."));
                return;
            }
            if (!_preview.Ok)
            {
                foreach (string e in _preview.Errors) GUILayout.Label("<color=#ff6666>✗ " + e + "</color>");
                foreach (string w in _preview.Warnings) GUILayout.Label("<color=#ffcc44>! " + w + "</color>");
                return;
            }

            Row(Loc.T("推力", "Thrust"), _preview.Thrust / 1000.0, "kN", "F2");
            if (!string.IsNullOrEmpty(_cappedNote))
                GUILayout.Label("<color=#ff9933>! " + _cappedNote + "</color>");
            Row(Loc.T("单位推力", "Specific thrust"), _preview.FnPerMdot, "N/(kg/s)", "F0");
            Row(Loc.T("SFC", "SFC"), _preview.Sfc, "kg/(N·h)", "F4");
            Row(Loc.T("SFC", "SFC"), _preview.Sfc / 0.10197, "lb/(lbf·h)", "F3");
            Row(Loc.T("燃油流量", "Fuel flow"), _preview.FuelFlow, "kg/s", "F3");
            if (_idleFuel > 1e-6)
            {
                // 只读：这是设计点反解出来的慢车油量，飞行时低于它转速就会自己往下掉。
                Row(Loc.T("最低喷油量（只读）", "Minimum fuel flow (read-only)"), _idleFuel, "kg/s", "F3");
                if (_preview.FuelFlow > 1e-6)
                    GUILayout.Label(string.Format(Loc.T("  设计点耗油是最低量的 {0:F1} 倍", "  The design point burns {0:F1}× the minimum"), _preview.FuelFlow / _idleFuel));
            }
            GUILayout.Space(3f);
            Row(Loc.T("进气量", "Intake airflow"), _preview.MdotAir, "kg/s", "F1");
            Row(Loc.T("核心流量", "Core airflow"), _preview.MdotCore, "kg/s", "F1");
            Row(Loc.T("涵道比", "Bypass ratio"), _preview.Bpr, "", "F2");
            Row(Loc.T("总增压比", "Overall pressure ratio"), _preview.Opr, "", "F2");
            Row(Loc.T("风扇压比", "Fan pressure ratio"), _preview.Fpr, "", "F2");
            // 级间负荷：整机喘振裕度是一个数，失速却总是某一级先转不动。
            StageLoadReport sl = StageLoadReport.Of(_preview.Stages);
            if (sl.Ok)
                GUILayout.Label(string.Format(Loc.T("级间负荷　{0}", "Stage loading　{0}"), sl.Describe()));
            GUILayout.Space(3f);
            // 长度到底吃掉了多少：以前拉长发动机读数一分不动，现在这里能看见钱花在哪。
            GUILayout.Label(string.Format(
                Loc.T("管道损失　进气 {0:P2}｜核心 {1:P2}｜排气 {2:P2}｜外涵 {3:P2}（整机 {4:F2} m）", "Duct loss　intake {0:P2} | core {1:P2} | exhaust {2:P2} | bypass {3:P2} (engine {4:F2} m)"),
                _preview.DuctInletLoss, _preview.DuctCoreLoss, _preview.DuctExhaustLoss,
                _preview.DuctBypassLoss, _edit.EffectiveLength));
            GUILayout.Space(3f);
            Row("T3", _preview.T3, "K", "F0");
            Row("T4", _preview.T4, "K", "F0");
            Row("T5 / EGT", _preview.T5, "K", "F0");
            if (_edit.Afterburner.Enabled) Row("T7", _preview.T7, "K", "F0");
            Row("P3", _preview.P3 / 1000.0, "kPa", "F1");
            GUILayout.Space(3f);
            foreach (KeyValuePair<string, double> kv in _preview.Spool)
                Row(kv.Key, kv.Value, "%", "F1");
            GUILayout.Label(_preview.SelfSustaining ? Loc.T("自持：是", "Self-sustaining: yes") : Loc.T("<color=#ff6666>自持：否</color>", "<color=#ff6666>Self-sustaining: no</color>"));
            Row(Loc.T("压气机功", "Compressor power"), _preview.WComp / 1e6, "MW", "F2");
            Row(Loc.T("风扇功", "Fan power"), _preview.WFan / 1e6, "MW", "F2");
            DrawMassBlock();
            GUILayout.Label(string.Format(Loc.T("设计点求解 {0:F1} ms", "Design-point solve {0:F1} ms"), _previewSeconds));
        }

        // ---------- 简单模式页 ----------

        /// <summary>
        /// 简单模式只让玩家填「目标」：涵道比、增压比、燃烧室温度、排气温度、直径。
        /// 风扇压比不填——它由涵道比的统计关系给出（见 Simple.FprFromBpr），
        /// 让玩家手填风扇压比只会得到一堆打不着配合的数。
        /// </summary>
        private void DrawSimpleParams()
        {
            GUILayout.Label(Loc.T("<b>简单模式参数</b>", "<b>Simple mode parameters</b>"));
            GUILayout.Label(Loc.T("只给目标，不给几何。适合快速试一台机器能不能跑。", "Goals only, no geometry. Good for finding out quickly whether an engine will run at all."));

            _simpleEdit.Name = Text(Loc.T("名称", "Name"), _simpleEdit.Name);
            SimpleTechSelector();

            bool turbofan = _simpleEdit.Kind == EngineKind.Turbofan;
            GUILayout.BeginHorizontal();
            if (OptionButton(turbofan, Loc.T("涡扇（含风扇与涵道）", "Turbofan (fan + bypass)")) && !turbofan)
            {
                _simpleEdit.Kind = EngineKind.Turbofan;
                _simpleEdit.Normalize();
                Recompute();
            }
            if (OptionButton(!turbofan, Loc.T("涡喷（无风扇）", "Turbojet (no fan)")) && turbofan)
            {
                _simpleEdit.Kind = EngineKind.Turbojet;
                _simpleEdit.Normalize();
                Recompute();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            Slider(Loc.T("发动机直径", "Engine diameter"), ref _simpleEdit.Diameter, 0.2f, 3.0f, "F2", " m", true);

            GUILayout.Space(6f);
            GUILayout.Label(Loc.T("<b>循环目标</b>", "<b>Cycle goals</b>"));
            if (_simpleEdit.Kind == EngineKind.Turbofan)
            {
                Slider(Loc.T("涵道比 BPR", "Bypass ratio BPR"), ref _simpleEdit.Bpr, 0.0f, 12.0f, "F2", "", true);
                GUILayout.Label(string.Format(Loc.T("　风扇压比按统计式：{0:F2}（可下面覆盖）", "　Fan pressure ratio from the statistic: {0:F2} (overridable below)"),
                    Simple.FprFromBpr(_simpleEdit.Bpr)));
                Slider(Loc.T("风扇压比覆盖（0=自动）", "Fan PR override (0 = auto)"), ref _simpleEdit.Fpr, 0.0f, 3.0f, "F2", "", true);
            }
            Slider(Loc.T("总增压比 OPR", "Overall pressure ratio OPR"), ref _simpleEdit.Opr, 2.0f, 45.0f, "F1", "", true);
            Slider(Loc.T("燃烧室出口温度 T4", "Burner exit temperature T4"), ref _simpleEdit.T4, 800.0f, 2200.0f, "F0", " K", true);
            if (Slider(Loc.T("慢车 T4（0=自动 55%）", "Idle T4 (0 = auto, 55%)"), ref _simpleEdit.T4Idle, 0.0f, 1800.0f, "F0", " K", true)) { }

            GUILayout.Space(6f);
            GUILayout.Label(Loc.T("<b>排气与加力</b>", "<b>Exhaust and afterburner</b>"));
            Slider(Loc.T("排气温度 T7 / EGT 上限", "EGT limit / T7"), ref _simpleEdit.TExhaust, 0.0f, 2200.0f, "F0", " K", true);
            GUILayout.Label(Loc.T("　不配加力时它是 EGT 上限；配了加力它就是 T7。", "　Without an afterburner this is the EGT limit; with one it is T7."));
            Slider(Loc.T("加力喷油量 far_ab（0=不加力）", "far_ab (0 = none)"), ref _simpleEdit.AbFuelRatio, 0.0f, 0.045f, "F4", "", true);
            if (_simpleEdit.AbFuelRatio > 0.0)
                Slider(Loc.T("加力油门门槛", "Afterburner throttle gate"), ref _simpleEdit.AbThrottle, 0.5f, 1.0f, "F2", "", true);

            GUILayout.Space(6f);
            GUILayout.Label(Loc.T("<b>设计点与环境</b>", "<b>Design point & environment</b>"));
            Slider(Loc.T("设计马赫", "Design Mach"), ref _simpleEdit.DesignMach, 0.0f, 2.5f, "F2", "", true);
            Slider(Loc.T("设计高度", "Design altitude"), ref _simpleEdit.DesignAlt, 0.0f, 20000.0f, "F0", " m", true);
            if (Selector(Loc.T("喷管", "Nozzle"), ref _simpleEdit.Nozzle, NozzleNames)) { }
            Slider(Loc.T("非设计点效率折减 k", "Off-design eff. factor k"), ref _simpleEdit.EtaOffdesignK, 0.0f, 1.5f, "F2", "", true);
        }

        private void DrawSimpleReadout()
        {
            GUILayout.Label(Loc.T("<b>设计点读数</b>", "<b>Design-point readout</b>"));
            if (_simplePreview == null)
            {
                GUILayout.Label(Loc.T("没有结果。", "No result."));
                return;
            }
            if (!_simplePreview.Ok)
            {
                foreach (string e in _simplePreview.Errors) GUILayout.Label("<color=#ff6666>✗ " + e + "</color>");
                return;
            }
            foreach (string w in _simplePreview.Warnings) GUILayout.Label("<color=#ffcc44>! " + w + "</color>");

            Row(Loc.T("推力", "Thrust"), _simplePreview.Thrust / 1000.0, "kN", "F2");
            Row(Loc.T("单位推力", "Specific thrust"), _simplePreview.FnPerMdot, "N/(kg/s)", "F0");
            Row(Loc.T("SFC", "SFC"), _simplePreview.Sfc, "kg/(N·h)", "F4");
            Row(Loc.T("SFC", "SFC"), _simplePreview.Sfc / 0.10197, "lb/(lbf·h)", "F3");
            Row(Loc.T("燃油流量", "Fuel flow"), _simplePreview.FuelFlow, "kg/s", "F3");
            GUILayout.Space(3f);
            Row(Loc.T("进气量", "Intake airflow"), _simplePreview.MdotAir, "kg/s", "F1");
            Row(Loc.T("核心流量", "Core airflow"), _simplePreview.MdotCore, "kg/s", "F1");
            Row(Loc.T("涵道比", "Bypass ratio"), _simplePreview.Bpr, "", "F2");
            Row(Loc.T("总增压比", "Overall pressure ratio"), _simplePreview.Opr, "", "F2");
            Row(Loc.T("风扇压比", "Fan pressure ratio"), _simplePreview.Fpr, "", "F2");
            GUILayout.Space(3f);
            Row("T3", _simplePreview.T3, "K", "F0");
            Row("T4", _simplePreview.T4, "K", "F0");
            Row("T5 / EGT", _simplePreview.T5, "K", "F0");
            if (_simplePreview.T7 > 0.0) Row("T7", _simplePreview.T7, "K", "F0");
            Row("P3", _simplePreview.P3 / 1000.0, "kPa", "F1");
            GUILayout.Space(3f);
            Row("N1", _simplePreview.N1Pct, "%", "F1");
            Row("N2", _simplePreview.N2Pct, "%", "F1");
            Row(Loc.T("N1 转速", "N1 speed"), _simplePreview.N1Rpm, "rpm", "F0");
            Row(Loc.T("N2 转速", "N2 speed"), _simplePreview.N2Rpm, "rpm", "F0");
            GUILayout.Label(_simplePreview.SelfSustaining ? Loc.T("自持：是", "Self-sustaining: yes") : Loc.T("<color=#ff6666>自持：否</color>", "<color=#ff6666>Self-sustaining: no</color>"));
            if (_simplePreview.TurbineLimited)
                GUILayout.Label(Loc.T("<color=#ffcc44>涡轮落压比已到可用上限，涡轮少抽了功。</color>", "<color=#ffcc44>Turbine pressure ratio hit its usable limit; less work is drawn than needed.</color>"));
            DrawMassBlock();
            GUILayout.Label(string.Format(Loc.T("设计点求解 {0:F1} ms", "Design-point solve {0:F1} ms"), _previewSeconds));
        }

        private void SimpleTechSelector()
        {
            string[] techs = { "1940s", "1960s", "1970s", "1990s", "2010s" };
            string cur = _simpleEdit.Tech != null ? _simpleEdit.Tech.Name : "1970s";
            GUILayout.BeginHorizontal();
            GUILayout.Label(H(Loc.T("技术年代", "Technology era")), GUILayout.Width(130f));
            foreach (string t in techs)
                if (OptionButton(cur == t, t, GUILayout.Width(54f)) && cur != t)
                {
                    _simpleEdit.Tech = TechLevel.Get(t);
                    Recompute();
                }
            GUILayout.EndHorizontal();
        }

        private void DrawGrid()
        {
            GUILayout.Label(Loc.T("<b>性能网格</b>", "<b>Performance grid</b>"));
            GUILayout.Label(Loc.T("飞行中不跑求解器，只在这张表里三线性内插。", "No solver in flight: it trilinearly interpolates this table."));

            GUILayout.BeginHorizontal();
            int mode = GUILayout.Toolbar(_gridTab, new[] { Loc.T("推力", "Thrust"), Loc.T("SFC", "SFC") });
            if (mode != _gridTab) { _gridTab = mode; }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("构建网格", "Build grid")))
            {
                if (_target != null)
                {
                    DateTime t0 = DateTime.UtcNow;
                    // 以前这里是"直接给 _target.Design 赋值 + 只抄给对称件"，绕过了 ApplyDesign，
                    // 于是同船另一台**手动摆的**同款发动机收不到任何通知，进飞行后还是初始设计
                    // （存档里两台一台 test3、一台 JF-1 就是这么来的）。
                    // 现在统一走 ApplyDesign：它按"改动前的设计指纹"找同款一起改，
                    // 玩家故意做成两台不同参数的发动机不会被顺手抹掉。
                    if (_useSimple)
                    {
                        _target.ApplySimpleDesign(_simpleEdit.Clone(), false);
                    }
                    else
                    {
                        _target.ApplyDesign(_edit.Clone(), false);
                    }
                    _target.GridDirty = true;
                    _target.EnsureReady();
                    _target.RebuildGrid();
                    _status = Loc.T("网格：", "Grid: ") + (_target.Grid != null ? _target.Grid.Summary() : Loc.T("构建失败", "build failed"));
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("同型全部跟着改", "Sync all same type"), GUILayout.Width(160f)))
            {
                // 自动同步只认"改动前和我一样"的，两台已经分叉（老存档里一台还是初始设计）就够不着。
                // 这个按钮不看指纹，点了就是"全船同型都照这一台"。
                if (_target != null)
                {
                    int n = _target.SyncToSameType();
                    _status = n > 0 ? Loc.T("已经同步给 ", "Synced to ") + n + Loc.T(" 台同型发动机", " same-type engines") : Loc.T("船上没有别的同型发动机", "No other same-type engine on this vessel");
                }
            }
            GUILayout.Label(Loc.T("已经改跑偏、或者老存档里两台不一样的，用这个点名覆盖。", "For engines that drifted, or old saves where the two differ: force this one onto them."), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("热力图（独立窗口）", "Heat map (window)"), GUILayout.Width(160f)))
            {
                // 用草稿而不是已提交的设计：滑杆一动就能重算，不必先"构建网格"。
                // 图里每一格都是一次真实解算，所以它和飞行中看到的数是同一套口径。
                if (_target != null && _edit != null) JetForgeHeatmapWindow.Open(_target, _edit.Clone());
            }
            GUILayout.Label(Loc.T("按当前油门算一张高度×速度的推力图，鼠标移上去读点。", "Altitude × speed thrust map at the current throttle; hover to read a point."), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.T("发动机测试台…", "Engine test bench…"), GUILayout.Width(160f)))
            {
                // 台架要的是"飞行里那台机器"，所以传 _target 而不是草稿：
                // 它自己会在打开时拷一份设计，并且读这一台接上的进气道。
                if (_target != null) JetForgeBenchWindow.Open(_target);
            }
            GUILayout.Label(Loc.T("挑高度/马赫/油门开机，或扫一整条剖面看趋势。", "Pick altitude/Mach/throttle, or sweep a whole profile."), GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            if (_target != null && _target.Grid != null)
            {
                GUILayout.Label(_target.Grid.Summary());
                GUILayout.Label(Loc.T("旧网格只在本页保留作对照；飞行中已改走实时解算。", "The old grid is kept on this page for comparison only; flight uses live solving."));
            }
        }

        /// <summary>干重/推重比/造价一栏。数字来自几何，所以拖滑杆时它会跟着动——这就是"每台发动机一个模型"的另一半。</summary>
        private void DrawMassBlock()
        {
            if (_mass == null) { GUILayout.Label(Loc.T("质量：估算失败", "Mass: estimate failed")); return; }
            GUILayout.Space(6);
            GUILayout.Label(string.Format(Loc.T("干重 {0:F0} kg   ·   推重比 {1:F2}   ·   造价 {2:N0} funds", "Dry {0:F0} kg   ·   T/W {1:F2}   ·   Cost {2:N0} funds"),
                _mass.TotalDry, _mass.ThrustToWeight, _mass.Cost));
            GUILayout.Label(string.Format(Loc.T("风扇 {0:F0} · 压气机 {1:F0}", "Fan {0:F0} · Compressor {1:F0}"),
                _mass.Fan, _mass.Compressor), GUILayout.ExpandWidth(false));
            GUILayout.Label(string.Format(Loc.T("涡轮 {0:F0} · 机匣 {1:F0} · 燃烧室 {2:F0} · 附件 {3:F0} kg",
                "Turbine {0:F0} · Casing {1:F0} · Burner {2:F0} · Accessory {3:F0} kg"),
                _mass.Turbine, _mass.Casing, _mass.Burner, _mass.Accessory), GUILayout.ExpandWidth(false));
        }

        private void DrawChecks()
        {
            GUILayout.Label(Loc.T("<b>判据</b>", "<b>Checks</b>"));
            if (_report == null) { GUILayout.Label("无"); return; }

            string worst = _report.Worst;
            GUILayout.Label(string.Format(Loc.T("总体：<color={0}>{1}</color>（{2} 条警告 / {3} 条不合格）", "Overall: <color={0}>{1}</color> ({2} warnings / {3} failures)"),
                ColorOf(worst), worst, _report.Warns.Count, _report.Fails.Count));

            _scroll2 = GUILayout.BeginScrollView(_scroll2, GUILayout.Height(400f));
            bool hideOk = _report.Fails.Count + _report.Warns.Count > 0;
            foreach (Finding f in _report.Ordered(hideOk))
            {
                GUILayout.Label(string.Format("<color={0}>[{1}]</color> {2}", ColorOf(f.LevelValue), f.LevelValue, f.Message));
            }
            // 藏了就要说藏了几条：不然"只剩三条"看起来像"只跑了三条判据"。
            if (hideOk && _report.OkCount > 0)
                GUILayout.Label(string.Format(Loc.T("<color=#77dd77>另有 {0} 条通过（出问题时只列要看的）</color>", "<color=#77dd77>{0} other checks pass (only problems are listed)</color>"), _report.OkCount));
            GUILayout.EndScrollView();
        }

        private Vector2 _scroll2;

        private static string ColorOf(string level)
        {
            if (level == Level.Fail) return "#ff5555";
            if (level == Level.Warn) return "#ffcc44";
            return "#77dd77";
        }

        // ---------- 选项提示 ----------

        /// <summary>
        /// IMGUI 的悬停提示只有一条通路：控件被画出来时用带 tooltip 的 GUIContent，
        /// 鼠标压上去 Unity 就把那句 tooltip 塞进 GUI.tooltip。所以标签一律走 H()，
        /// 末尾再在鼠标旁边把 GUI.tooltip 画成一个小框。
        ///
        /// 说明文字集中在这一张表里而不是散在四十个调用点：改文案只改一处，
        /// 也顺手逼着每条说明都写成"玩家看得懂的一句话"，而不是代码注释的复述。
        /// 匹配用"精确 → 包含"，所以带缩进的子项（"  级数"）不用重复登记。
        /// </summary>
        private static readonly Dictionary<string, string> Hints = new Dictionary<string, string>
        { { Loc.T("减速齿轮比（1=直驱）", "Gear ratio (1 = direct)"), Loc.T("风扇相对低压轴的减速比。1 = 直驱；大于 1 时风扇转得比低压涡轮慢，叶尖速度和外流噪声跟着降，代价是齿轮箱的重量、一段轴向长度，以及轴功率的 1/效率。", "How much slower the fan turns than the LP shaft. 1 = direct drive. A geared fan cuts tip speed and fan noise; the price is gearbox mass, extra axial length and about one over efficiency of the shaft power.") },
            { Loc.T("齿轮效率（0=0.995）", "Gear efficiency (0 = 0.995)"), Loc.T("齿轮啮合效率。0 = 用缺省 0.995。每少一个千分点，那部分功率都变成滑油里的热，风扇拿不到。", "Gear-mesh efficiency. 0 = the default 0.995. Every point lost turns into heat in the oil, not into fan power.") },
            { Loc.T("放气比例", "Bleed fraction"), Loc.T("从压气机抽走的气占进口流量的比例。这些气不进喷管（去冷却或座舱），所以推力与核心流量一起减少。", "Share of the inlet flow taken off the compressor for cooling or the cabin. It never reaches the nozzle, so thrust and core flow both drop.") },
            { Loc.T("最大偏转角", "Maximum deflection"), Loc.T("喷管可偏转到多少度。偏转只改推力方向，不增加推力大小；角度越大侧向分量越多、轴向越少。", "How far the nozzle can vector. Vectoring turns thrust, it does not add any: more sideways, less axial.") },
        { Loc.T("技术年代", "Technology era"), Loc.T("决定一整组常数：材料耐温、压气机与涡轮效率、燃烧室压损、造价。同一份几何换个年代，性能会差一截。", "One knob for a whole era of constants: material limits, efficiencies, burner loss and cost.") },
            { Loc.T("机匣进气口直径 a", "Casing inlet diameter a"), Loc.T("发动机机匣自己的进气口径（唇口对接那一圈）。进气道零件的捕获面积由零件决定，不在这里。", "The engine casing's own inlet diameter (the lip ring). An intake part brings its own capture area and is not covered here.") },
            { Loc.T("整机长度", "Overall length"), Loc.T("唇口到喷口的总长。按各部件 axial 尺寸自动算出（不能直接设）：压气机/涡轮每级加长、燃烧室更长，机器就更长；长度进管道摩擦、质量与外形。", "Not set directly: the sum of every part's axial size. A longer engine costs duct friction and mass.") },
            { Loc.T("核心机进口直径（0=自动）", "Core inlet dia (0 = auto)"), Loc.T("压气机进口直径。0 = 按设计点流量与面马赫 0.5 自动反算。", "Compressor inlet diameter. 0 = back-calculated from design-point flow at face Mach 0.5.") },
            { Loc.T("设计马赫", "Design Mach"), Loc.T("标定点马赫。冲压升温、进气总压恢复、换算流量都按它算——不是飞行时的马赫。", "Mach at the calibration point. Ram rise, inlet pressure recovery and corrected flow all use it - not the flight Mach.") },
            { Loc.T("设计高度", "Design altitude"), Loc.T("标定点高度，ISA 温度压力从这里取。改它会把整台机器的标定点搬到天上。", "Calibration altitude; ISA pressure and temperature come from here. Moving it moves the engine's whole design point skyward.") },
            { Loc.T("涵道比覆盖（0=自动）", "BPR override (0 = auto)"), Loc.T("0 = 由风扇与核心机流量比自动算。填了就强制用它，用于试「这台的涵道比再大一点会怎样」。", "0 = derived from the fan-to-core flow ratio. A number here forces it, handy for asking what a slightly higher bypass would do.") },
            { Loc.T("风扇直径", "Fan diameter"), Loc.T("风扇叶尖直径。叶尖速度被马赫上限锁住，所以直径越大转速越低、流量越大。", "Fan tip diameter. Tip speed is capped by Mach, so a larger fan turns slower and moves more air.") },
            { Loc.T("叶片级数", "Blade stages"), Loc.T("风扇级数。多一级多一份压比，也多一份质量与阻力。", "Fan stage count. Each stage adds pressure ratio, mass and drag.") },
            { Loc.T("轮毂/叶尖比", "Hub/tip ratio"), Loc.T("轮毂半径/叶尖半径。越小叶片越长、涵道越薄；太大会让气流通道扁得没有流量。", "Hub radius over tip radius. Smaller means longer blades and a thinner annulus; too large and the annulus is too flat to pass flow.") },
            { Loc.T("叶片数（0=按直径自动）", "Blade count (0 = from diameter)"), Loc.T("只改外形，不改性能。自动档按每米半径 28 片估（CFM56-7B 1.55 m 是 22 片宽弦，PW4000 2.54 m 是 38 片薄弦）。", "Shape only, not performance. Auto assumes 28 blades per metre of radius (CFM56-7B: 22 wide-chord; PW4000: 38 thin).") },
            { Loc.T("装了自己的声音", "Own base sound"), Loc.T("默认让 RSE 放基础层（两套循环叠一起会打拍）。RSE 是按零件名打补丁的第三方模组，哪天它不认这台机器了，勾上这个就由我们三层全放。", "RSE usually owns the base layer, since two loops beat against each other. Tick only if RSE stops recognising this engine.") },
            { Loc.T("音效音量", "Sound volume"), Loc.T("三层声音（气流轰鸣/压气机啸叫/加力轰爆）的总音量。装了 RSE 时基础层让给 RSE，我们只补加力层。", "Master volume for our three layers. With RSE the base layer is theirs and we only add the afterburner.") },
            { Loc.T("风扇压比（0=自动）", "Fan PR (0 = auto)"), Loc.T("0 = 按叶尖速度与载荷系数算。压比越高外涵射流越快，加力增益越小。", "0 = derived from tip speed and loading coefficient. A higher ratio means a faster bypass jet and less afterburner gain.") },
            { Loc.T("  形式", "  Form"), Loc.T("轴流：级数多、单级压比小、效率高。离心：单级压比大但最多 2 级，效率低一点。", "Axial: many stages, small per-stage ratio, high efficiency. Centrifugal: a big single-stage ratio but at most 2 stages, a little less efficient.") },
            { Loc.T("  级数", "  Stages"), Loc.T("级数只约束「这台机器能不能吃掉这份压比/温降」，不直接给推力。0 级 = 不存在。", "Stages bound the pressure ratio this machine can take; they add no thrust on their own. 0 = absent.") },
            { Loc.T("  叶尖直径", "  Tip diameter"), Loc.T("转子叶尖直径。叶尖速度被马赫上限锁死，所以改直径改的是转速，不是功率。", "Rotor tip diameter. Tip speed is capped by Mach, so changing diameter changes speed, not power.") },
            { Loc.T("  轮毂/叶尖比", "  Hub/tip ratio"), Loc.T("轮毂比越大，通流面积越小、轴向速度越高，流量系数 φ 跟着变。", "A larger hub-to-tip ratio narrows the annulus and raises axial velocity, and the flow coefficient follows.") },
            { Loc.T("  载荷系数 ×", "  Loading factor ×"), Loc.T("ψ，这一级的做功系数。温升 ∝ ψU²，所以调它等于加强或削弱这一级。", "psi, the work coefficient of this stage. Temperature rise scales with psi times U squared, so this knob strengthens or weakens the stage.") },
            { Loc.T("  挂在第几轴", "  Shaft"), Loc.T("这一级由哪根轴带着转。轴数在总体页改。", "Which shaft drives this stage. Shaft count is set on the general page.") },
            { Loc.T("  驱动第几轴", "  Drives shaft"), Loc.T("这台涡轮给哪根轴供功。和压气机对不上就会出现「轴带不动自己」。", "Which shaft this turbine powers. Mismatch it against the compressor and the shaft cannot drive itself.") },
            { Loc.T("长度", "Length"), Loc.T("火焰筒长度。太短油滴烧不完（效率掉、油耗涨），太长掺混压损涨。", "Liner length. Too short and the fuel does not burn out (efficiency drops, fuel flow climbs); too long and mixing losses climb.") },
            { Loc.T("出口温度 T4", "Exit temperature T4"), Loc.T("涡轮前温度。设计点锁住它，所以效率的代价落在油耗上而不是推力上。", "Turbine entry temperature. It is pinned at the design point, so the price of efficiency shows up in fuel burn rather than thrust.") },
            { Loc.T("总压损失 ΔP/P（-1=按型查表）", "Pressure loss ΔP/P (-1=tbl)"), Loc.T("-1 = 按型式查表：罐式约 7.5%、罐环 6%、环形 4.5%、短环形 5.5%。", "-1 = look up by type: can about 7.5%, can-annular 6%, annular 4.5%, short annular 5.5%.") },
            { Loc.T("燃烧效率（-1=按型查表）", "Combustion eff. (-1 = table)"), Loc.T("-1 = 按型式查表。火焰筒太短会在这个值上打折。", "-1 = look up by type. A short liner is discounted on this number.") },
            { Loc.T("火焰筒长径比 L/H（0=按型）", "Liner L/H (0 = from type)"), Loc.T("0 = 按型式。低于 1.6 油滴来不及烧完，超过 3.5 压损开始明显涨。", "0 = by type. Below 1.6 the droplets do not finish burning; above 3.5 pressure loss starts to climb.") },
            { Loc.T("罐数（0=按型，环形无罐）", "Cans (0 = from type; annular has none)"), Loc.T("罐式/罐环的火焰筒个数。0 = 按型式（罐式 8、罐环 10）。环形没有罐。", "Liner count for can and can-annular types. 0 = by type (can 8, can-annular 10). Annular has none.") },
            { Loc.T("喷油量 far_ab（0=按温度）", "far_ab (0 = from T)"), Loc.T("0 = 按加力出口温度反算。填了就强制用它，可以和出口温度对不上。", "0 = back-calculated from afterburner exit temperature. A number here forces it and may then disagree with the exit temperature.") },
            { Loc.T("加力段长度（0=按核心机自适应）", "AB length (0 = fit core)"), Loc.T("涡轮出口到喷管之间那一节独立筒体。0 = 按核心机半径自动给（约 3.4 倍半径）。它只改外形与质量，不改推力。", "Separate barrel between turbine exit and nozzle; 0 = about 3.4 core radii. Shape and mass only.") },
            { Loc.T("加力出口温度 T7", "AB exit temperature T7"), Loc.T("加力段出口温度。它同时决定尾焰分档（暖/中/热）与涡轮后的热负荷。", "Afterburner exit temperature. It sets both the plume tier (warm/mid/hot) and the heat load behind the turbine.") },
            { Loc.T("点亮油门", "Light throttle"), Loc.T("自动档下油门超过这个比例才开始点加力，之后按比例线性开到满——AJE 那种感觉。", "In automatic mode the afterburner lights once the throttle passes this fraction, then opens linearly to full - the AJE feel.") },
            { Loc.T("喷管", "Nozzle"), Loc.T("收敛：简单、干态够用，加力会被自己喉道憋住。可变：面积随油门放大。收扩：只有落压比够高才值。", "Converging chokes when the afterburner lights; variable opens with throttle; C-D only pays at high pressure ratio.") },
            { Loc.T("发动机直径", "Engine diameter"), Loc.T("简单模式的整机直径，用来估流量、外形与质量。", "Whole-engine diameter in simple mode, used to estimate flow, shape and mass.") },
            { Loc.T("涵道比 BPR", "Bypass ratio BPR"), Loc.T("外涵/内涵流量比。0 = 涡喷，5 以上是民航大涵道。", "Bypass-to-core flow ratio. 0 = turbojet; above 5 is a high-bypass airliner engine.") },
            { Loc.T("风扇压比覆盖（0=自动）", "Fan PR override (0 = auto)"), Loc.T("0 = 由单级风扇的叶尖速度算。大涵道民航机的风扇压比通常在 1.5~1.7。", "0 = derived from a single-stage fan's tip speed. High-bypass airliner fans usually sit at 1.5-1.7.") },
            { Loc.T("总增压比 OPR", "Overall pressure ratio OPR"), Loc.T("设计点总压比，决定核心机要做多少功、排多少热。现代战斗机发动机 25~35。", "Design-point overall pressure ratio; it sets how much work the core does and how much heat it rejects. Modern fighter engines run 25-35.") },
            { Loc.T("燃烧室出口温度 T4", "Burner exit temperature T4"), Loc.T("简单模式直接给涡轮前温度。它和 OPR 一起决定这台机器的推力上限。", "Simple mode takes turbine entry temperature directly. Together with OPR it sets the thrust ceiling.") },
            { Loc.T("慢车 T4（0=自动 55%）", "Idle T4 (0 = auto, 55%)"), Loc.T("0 = 满油门 T4 的 55%。慢车推力占满油门 5~8% 才算像真的。", "0 = 55% of full-throttle T4. A believable idle makes 5-8% of full thrust.") },
            { Loc.T("排气温度 T7 / EGT 上限", "EGT limit / T7"), Loc.T("排气温度上限，判据用它算超温与寿命。0 = 按技术年代算。", "Exhaust temperature limit; the checks use it for overtemperature and life. 0 = by technology era.") },
            { Loc.T("加力喷油量 far_ab（0=不加力）", "far_ab (0 = none)"), Loc.T("0 = 这台机器没有加力。填了就是加力段的油气比。", "0 = this engine has no afterburner. A number here is the afterburner fuel ratio.") },
            { Loc.T("加力油门门槛", "Afterburner throttle gate"), Loc.T("简单模式的加力门槛：油门过这个值就点火，全开全关。", "Simple-mode afterburner threshold: light above this throttle, full on or full off.") },
            { Loc.T("非设计点效率折减 k", "Off-design eff. factor k"), Loc.T("偏离设计点后效率随转速掉多少。0 = 不掉（偏乐观），1 以上偏狠。", "How much efficiency falls with speed off design. 0 = not at all (optimistic); above 1 is harsh.") },
        };

        private static string HintOf(string label)
        {
            // 逻辑在 Core/HintIndex.cs：那里能进回归，"英文界面提示消失"这种缺陷不用等进游戏才发现。
            return JetForge.Core.HintIndex.Find(Hints, label);
        }

        private static GUIContent H(string label)
        {
            string t = HintOf(label);
            return t == null ? new GUIContent(label) : new GUIContent(label, t);
        }

        /// <summary>给自检用：这张表本身在 Unity 里，但"某个标签挂不挂得上提示"是纯查表逻辑。</summary>
        public static string HintOfPublic(string label) { return HintOf(label); }

        /// <summary>提示表条数（自检对数用）。</summary>
        public static int HintCount { get { return Hints.Count; } }

        /// <summary>提示表里所有中文键（自检用：按中文键找英文标签，再看提示还挂不挂得上）。</summary>
        public static List<string> HintKeys()
        {
            List<string> keys = new List<string>();
            foreach (KeyValuePair<string, string> kv in Hints) keys.Add(kv.Key);
            return keys;
        }

        /// <summary>
        /// 给自检用：提示表本身没法在命令行里测（这个类依赖 UnityEngine），
        /// 但"标签能不能对上提示"是个纯逻辑问题，可以在游戏里跑一遍再写进 selftest.log。
        /// 特意把"长度"和"整机长度"一起探——两者都含"长度"，先包含匹配就会串。
        /// </summary>
        public static string DescribeHints()
        {
            string[] probe = { "进气道直径 a", Loc.T("  级数", "  Stages"), Loc.T("长度", "Length"), Loc.T("整机长度", "Overall length"), Loc.T("点亮油门", "Light throttle"), Loc.T("喷管", "Nozzle"), "完全没登记的项" };
            StringBuilder sb = new StringBuilder();
            sb.Append(Loc.T("提示表 ", "hint table ")).Append(Hints.Count).Append(" 条｜抽样 ");
            for (int i = 0; i < probe.Length; i++)
            {
                string h = HintOf(probe[i]);
                sb.Append(probe[i].Trim()).Append('=');
                sb.Append(h == null ? Loc.T("（没有）", "(none)") : h.Substring(0, Math.Min(8, h.Length)) + "…");
                if (i < probe.Length - 1) sb.Append("｜");
            }
            return sb.ToString();
        }

        // ---------- 控件 ----------

        private static bool Text(ref string s, string v) { s = v; return true; }

        private static string TextField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(H(label), GUILayout.Width(190f));
            string r = GUILayout.TextField(value ?? "", GUILayout.Width(180f));
            GUILayout.EndHorizontal();
            return r;
        }

        private string Text(string label, string value)
        {
            return TextField(label, value);
        }

        private string TechSelector()
        {
            string[] techs = { "1940s", "1960s", "1970s", "1990s", "2010s" };
            GUILayout.BeginHorizontal();
            GUILayout.Label(H(Loc.T("技术年代", "Technology era")), GUILayout.Width(130f));
            string cur = _edit.TechName;
            foreach (string t in techs)
                if (OptionButton(cur == t, t, GUILayout.Width(54f)) && cur != t)
                { cur = t; Recompute(); }
            GUILayout.EndHorizontal();
            return cur;
        }

        /// <summary>滑杆一旦动了就重算设计点，这样玩家拖的时候能看到读数跟着走。</summary>
        private bool Slider(string label, ref double value, float min, float max, string fmt, string unit, bool live)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(H(label), GUILayout.Width(190f));
            double v = GUILayout.HorizontalSlider((float)value, min, max, GUILayout.Width(140f));
            GUILayout.Label(value.ToString(fmt) + unit, GUILayout.Width(74f));
            GUILayout.EndHorizontal();
            if (Math.Abs(v - value) > 1e-9)
            {
                value = v;
                if (live) Recompute();
                return true;
            }
            return false;
        }

        private float SliderI(string label, int value, int min, int max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(H(label), GUILayout.Width(190f));
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(140f));
            int iv = Mathf.RoundToInt(v);
            GUILayout.Label(iv.ToString(), GUILayout.Width(48f));
            GUILayout.EndHorizontal();
            if (iv != value) { Recompute(); return iv; }
            return value;
        }

        private bool Selector<T>(string label, ref T value, string[] names) where T : struct, IConvertible
        {
            if (!typeof(T).IsEnum) return false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(H(label), GUILayout.Width(130f));
            int cur = Convert.ToInt32(value);
            int next = cur;
            int count = Mathf.Min(names.Length, Enum.GetValues(typeof(T)).Length);
            for (int i = 0; i < count; i++)
                if (OptionButton(cur == i, names[i], GUILayout.Width(84f))) next = i;
            GUILayout.EndHorizontal();
            if (next != cur)
            {
                value = (T)Enum.ToObject(typeof(T), next);
                Recompute();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 一组互斥选项里的一个。用 Button 而不是 Toggle。
        ///
        /// Toggle 的返回值是"点完之后的状态"：点已选中的那个返回 false，
        /// 看起来就像"点了没反应"；而 IMGUI 里同一帧内多个 Toggle 的返回值还会互相打架，
        /// 于是出现"选了 A 之后再也点不回 B"这种怪症。Button 每次点击都返回 true，
        /// 没有状态，也就没有这个坑。当前选中项用 ▣ 标出来。
        /// </summary>
        private static bool OptionButton(bool selected, string label, params GUILayoutOption[] opts)
        {
            return GUILayout.Button((selected ? "▣ " : "") + label, opts);
        }

        private static void Row(string label, double value, string unit, string fmt)
        {
            GUILayout.Label(string.Format("{0,-14}{1} {2}", label, value.ToString(fmt), unit));
        }

    }
}