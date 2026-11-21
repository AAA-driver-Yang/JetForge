using System;
using System.Collections.Generic;
using JetForge.Core;
using JetForge.KSP;
using UnityEngine;

namespace JetForge.UI
{
    /// <summary>
    /// 设计向导窗口：玩家只填目标（用途/马赫/高度/推力/年代），拿回一台能直接编辑的初稿。
    ///
    /// 这里刻意不做"一键最优解"：生成之后每一条初值都写在下面（从哪台真机起手、缩放了
    /// 多少、级数动没动、实算推力差多少），矛盾也照实列出来。向导的职责是把"空白设计页"
    /// 变成"有依据的草稿"，不是替玩家拍板。
    /// </summary>
    public class JetForgeWizardWindow : MonoBehaviour
    {
        private const int WindowId = 0x4A53;
        private static JetForgeWizardWindow _instance;

        private ModuleJetForgeEngine _target;
        private Rect _rect = new Rect(280f, 110f, 560f, 620f);
        private Vector2 _scroll;
        private bool _visible;

        private WizardRole _role = WizardRole.Fighter;
        private double _mach = 2.0;
        private double _altKm = 11.0;
        private double _thrustKn = 80.0;
        private int _techIndex;
        private string _thrustText = "80";
        private string _machText = "2.0";
        private string _altText = "11.0";
        private bool? _abOverride;
        private WizardResult _result;
        private string _status = "";
        /// <summary>生成这份初稿时用的那份参数。底栏靠它判断"初稿还算不算数"。</summary>
        private WizardInput _genInput;

        private static readonly string[] Techs = { "1950s", "1960s", "1970s", "1980s", "1990s", "2000s", "2010s" };
        private static readonly string[] Roles = { Loc.T("战斗机", "Fighter"), Loc.T("截击机", "Interceptor"), Loc.T("轰炸机", "Bomber"), Loc.T("教练机", "Trainer"), Loc.T("运输机", "Transport") };
        private static readonly WizardRole[] RoleValues =
        {
            WizardRole.Fighter, WizardRole.Interceptor, WizardRole.Bomber, WizardRole.Trainer, WizardRole.Transport
        };

        public static void Open(ModuleJetForgeEngine target)
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("JetForgeWizardWindow");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<JetForgeWizardWindow>();
            }
            _instance.Bind(target);
        }

        private void Bind(ModuleJetForgeEngine target)
        {
            _target = target;
            _visible = true;
            if (target != null && target.Design != null)
            {
                // 已经有设计就按它当前的口径预填，玩家改一个数就能出"邻近方案"
                _mach = target.Design.DesignMach > 0.0 ? target.Design.DesignMach : 2.0;
                _altKm = target.Design.DesignAlt > 0.0 ? target.Design.DesignAlt / 1000.0 : 11.0;
                _techIndex = IndexOfTech(target.Design.TechName);
            }
            SyncText();
        }

        private static int IndexOfTech(string name)
        {
            for (int i = 0; i < Techs.Length; i++)
                if (string.Equals(Techs[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return 3;
        }

        private void SyncText()
        {
            _thrustText = _thrustKn.ToString("0.#");
            _machText = _mach.ToString("0.00");
            _altText = _altKm.ToString("0.#");
        }

        private void Update()
        {
            if (_target == null || _target.part == null) _visible = false;
        }

        private void OnGUI()
        {
            if (!_visible) return;
            if (!HighLogic.LoadedSceneIsEditor && !HighLogic.LoadedSceneIsFlight) return;
            _rect = GUILayout.Window(WindowId, _rect, Draw, Loc.T("JetForge · 设计向导", "JetForge - Design wizard"),
                                     HighLogic.Skin != null ? HighLogic.Skin.window : GUI.skin.window,
                                     GUILayout.MinWidth(480f));
            _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
            _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
        }

        private void Draw(int id)
        {
            GUILayout.BeginVertical();
            // 滚动区**不许**吃满整个窗口：`ExpandHeight(true)` 会让它抢光 `_rect` 剩下的每一像素，
            // 于是底栏那几颗按钮被挤到窗口下沿以外——玩家翻遍整屏也找不到「交给设计器」，
            // 而屏幕上明明写着"初稿生成好了"。这就是"应用不上"报了三轮的那条（§276）。
            // 给滚动区一个上限，剩下的 150 px 留给底栏（按钮 26 + 撤销 24 + 关闭 + 间距）。
            _scroll = GUILayout.BeginScrollView(_scroll, false, false,
                GUILayout.Height(Mathf.Max(220f, _rect.height - 150f)));

            Row(_target != null && _target.part != null
                ? string.Format(Loc.T("目标零件：{0}", "Target part: {0}"), _target.part.partInfo != null ? _target.part.partInfo.title : _target.part.name)
                : Loc.T("目标零件：没有", "Target part: none"));
            Row(Loc.T("填目标，不要填部件。生成后每一项初值的来源都写在下面，改完仍走正常设计页。", "Give a target, not a part. After generating, every initial value says where it came from below; afterwards you keep editing on the normal design page."));
            if (_target == null || _target.Design == null)
            {
                Row(Loc.T("先在编辑器里选中一台 JetForge 发动机，再打开这个窗口。", "Select a JetForge engine in the editor first, then open this window."));
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                GUI.DragWindow();
                return;
            }

            GUILayout.Space(4f);
            Row(Loc.T("用途", "Role"));
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Roles.Length; i++)
            {
                bool on = RoleValues[i] == _role;
                if (GUILayout.Toggle(on, Roles[i], GUI.skin.button) != on)
                {
                    _role = RoleValues[i];
                    _abOverride = null;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            Row(string.Format(Loc.T("设计马赫 {0:F2}", "Design Mach {0:F2}"), _mach));
            _mach = GUILayout.HorizontalSlider((float)_mach, 0.2f, 3.2f);
            _machText = TextField(_machText, ref _mach, 0.2, 3.5, "Ma");

            Row(string.Format(Loc.T("设计高度 {0:F1} km", "Design altitude {0:F1} km"), _altKm));
            _altKm = GUILayout.HorizontalSlider((float)_altKm, 0f, 18f);
            _altText = TextField(_altText, ref _altKm, 0.0, 18.0, "km");

            Row(string.Format(Loc.T("设计推力 {0:F0} kN（有加力时指加力推力）", "Design thrust {0:F0} kN (afterburner thrust when fitted)"), _thrustKn));
            _thrustKn = GUILayout.HorizontalSlider((float)_thrustKn, 5f, 200f);
            _thrustText = TextField(_thrustText, ref _thrustKn, 3.0, 400.0, "kN");

            GUILayout.Space(4f);
            Row(Loc.T("技术年代", "Technology era"));
            int ti = GUILayout.Toolbar(_techIndex, Techs);
            if (ti != _techIndex) _techIndex = ti;

            GUILayout.Space(2f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("加力：", "AB:"), GUILayout.Width(50f));
            if (Button(Loc.T("按用途", "By role"), _abOverride == null)) SetAb(null);
            if (Button(Loc.T("开", "On"), _abOverride == true)) SetAb(true);
            if (Button(Loc.T("关", "Off"), _abOverride == false)) SetAb(false);
            string abText = _abOverride.HasValue
                ? (_abOverride.Value ? Loc.T("强制开", "Force on") : Loc.T("强制关", "Force off"))
                : (DesignWizard.DefaultAfterburner(_role) ? Loc.T("按用途：开", "By role: on") : Loc.T("按用途：关", "By role: off"));
            GUILayout.Label(abText);
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            if (GUILayout.Button(Loc.T("生成初稿", "Generate draft"), GUILayout.Height(26f))) Generate();
            if (_status.Length > 0) Row(_status);

            if (_result != null)
            {
                GUILayout.Space(6f);
                if (!_result.Ok)
                {
                    Row(Loc.T("生成失败：", "Generation failed: ") + _result.Failure);
                }
                else
                {
                    Row(string.Format(Loc.T("起手 {0}｜实算 {1:F1} kN / 目标 {2:F0} kN｜风扇 Ø{3:F2} m\n全长 {4:F2} m", "Starting point {0} | solved {1:F1} kN / target {2:F0} kN | fan dia {3:F2} m\noverall length {4:F2} m"),
                        _result.Basis, _result.ThrustKN, _result.TargetKN,
                        _result.Design.Fan.Diameter, _result.Design.EffectiveLength));
                    GUILayout.Space(2f);
                    Row(Loc.T("每个数是哪来的：", "Where each number came from:"));
                    for (int i = 0; i < _result.Notes.Count; i++) Row("  · " + _result.Notes[i]);
                    if (_result.Conflicts.Count > 0)
                    {
                        GUILayout.Space(2f);
                        Row(Loc.T("这个组合先天的矛盾（不阻止你这么做，但要知道代价）：", "This combination refuses to agree with itself (nothing stops you, but know the price):"));
                        for (int i = 0; i < _result.Conflicts.Count; i++) Row("  · " + _result.Conflicts[i]);
                    }
                    GUILayout.Space(6f);
                    // 这一句以前是个按钮，长在滚动区最底下：说明文字一多，玩家翻不到它就
                    // 只能得出"初稿显示已生成，但应用不到零件上"。按钮搬到滚动区外面的底栏，
                    // 翻到哪一行它都在。
                    Row(Loc.T("初稿生成好了——下面那条底栏上的按钮把它交给设计器。",
                        "The draft is ready - the button on the footer hands it to the designer."));
                }
            }

            GUILayout.EndScrollView();

            // 底栏：不在滚动区里，所以永远点得到。
            GUILayout.Space(4f);
            bool ready = _result != null && _result.Ok && _result.Design != null;
            // 初稿是不是按**上面现在这些参数**算的。不是也不扔掉它——
            // 以前"参数一改就 InvalidateDraft()"用 `Math.Abs(a-b) > 1e-9` 当判据，
            // 而滑杆返回的是量化到像素步长的 float，喂回去下一帧就可能差一个台阶，
            // 于是"参数变了"每帧都成立：初稿刚生成出来就被自己清掉，
            // 玩家看到的是「还没有可以交的初稿」，屏幕正中却还飘着「初稿好了」（§280）。
            // 现在的规矩：**玩家生成出来的东西不悄悄扔掉**，只在下面说清楚它是按哪份参数算的。
            bool stale = ready && _genInput != null && !WizardTarget.SameTarget(_genInput, CurrentInput());
            // 底线那一行先说结论：这一次生成成没成、成的是什么。
            // **结论必须画在固定区域里，细节才可以藏在滚动区里。**
            if (_result != null)
            {
                if (ready && !stale)
                    Row(string.Format(Loc.T("初稿好了：风扇 Ø{0:F2} m｜实算 {1:F1} kN / 目标 {2:F0} kN",
                            "Draft ready: fan dia {0:F2} m | solved {1:F1} kN / target {2:F0} kN"),
                        _result.Design.Fan.Diameter, _result.ThrustKN, _result.TargetKN));
                else if (ready)
                    Row(string.Format(Loc.T("初稿是旧参数算的：风扇 Ø{0:F2} m｜要新数就再点「生成初稿」，或者就这么装上",
                            "Draft from older inputs: fan dia {0:F2} m | regenerate, or apply it as is"),
                        _result.Design.Fan.Diameter));
                else
                    Row(Loc.T("这一次没生成出来，原因在下面滚动区里", "This run produced nothing - the reason is in the scroll area"));
            }
            // 这一颗**永远画**：没有就绪的初稿时它是灰的，并直说下一步该干什么。
            // 以前这里是一句 `if (就绪) 画按钮`——不就绪时底栏凭空少一行，玩家只能得出
            // "初稿生成好了却没地方交"（§277）。缺一颗按钮和少一行字，后果一样糟。
            bool guiWas = GUI.enabled;
            GUI.enabled = ready;
            if (GUILayout.Button(ready
                    ? Loc.T("把初稿装到这台发动机上（同时打开设计器）", "Put it on this engine")
                    : Loc.T("还没有可以交的初稿——先在上面点「生成初稿」", "No draft yet - press Generate"),
                GUILayout.Height(26f)))
                Apply();
            GUI.enabled = guiWas;
            if (WizardUndo.Has)
            {
                if (GUILayout.Button(WizardUndo.Describe(), GUILayout.Height(24f))) Undo();
            }
            else if (GUILayout.Button(Loc.T("撤销向导改动", "Undo wizard change"), GUILayout.Height(24f)))
            {
                _status = Loc.T("向导还没改过任何设计", "the wizard has not changed any design yet");
            }
            if (GUILayout.Button(Loc.T("关闭", "Close"))) _visible = false;

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private void SetAb(bool? v) { _abOverride = v; }

        /// <summary>
        /// 参数一改，刚才生成的那份就不再是"按这些参数生成的"了。
        ///
        /// 以前这里只清 `_result`、不清 `_status`：屏幕上留着「生成完毕（37 ms）」，
        /// 底栏那颗「把这台初稿交给设计器」却因为 `_result == null` 整句不画——
        /// 玩家看到的是"初稿显示已生成，可没有地方交出去"，也就是报了三轮的那句
        /// 「向导初稿无法应用」。**一句描述结果的话，必须和结果一起消失。**
        /// </summary>
        /// <summary>把界面上这些参数收成一份向导输入。</summary>
        private WizardInput CurrentInput()
        {
            return new WizardInput
            {
                Role = _role,
                DesignMach = _mach,
                DesignAlt = _altKm * 1000.0,
                ThrustKN = _thrustKn,
                Tech = Techs[_techIndex],
                Afterburner = _abOverride
            };
        }

        /// <summary>屏幕正上方弹一句，三秒。窗口里的字可能落在滚动区视野外，这一句不会。</summary>
        /// <remarks>
        /// 为什么还要这个：§277 把结论挪进固定底栏、§278 又挪了一次，玩家那边仍然报"点了没有任何反应"。
        /// 界面上"看得见"这件事命令行钉不住，最稳的办法是把结论**放到窗口外面**——
        /// <c>ScreenMessages</c> 画在屏幕中央偏上，不随窗口滚动、不随窗口大小变。
        /// 三秒够看清一句 20 字以内的话；窗口里的详细说明照旧留着。
        /// </remarks>
        private static void Toast(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            ScreenMessages.PostScreenMessage(msg, 3.0f, ScreenMessageStyle.UPPER_CENTER);
        }

        private void Generate()
        {
            _status = "";
            _result = null;
            WizardInput input = CurrentInput();
            _genInput = input;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            _result = DesignWizard.Build(input);
            sw.Stop();
            _status = _result.Ok
                ? string.Format(Loc.T("生成完毕（{0} ms）", "Generated in {0} ms"), sw.ElapsedMilliseconds)
                : Loc.T("没生成出来，原因写在下面", "Nothing was generated; the reason is below");
            // 日志里留一条：向导这一步到底成没成、按什么参数成的。
            // 玩家报"应用不上"时，先分清是没生成出来、还是生成了没交出去。
            Debug.Log("[JetForge] 向导初稿 " + (_result.Ok ? "生成成功" : "生成失败")
                + "｜目标 " + input.ThrustKN.ToString("F0") + " kN｜Ma" + input.DesignMach.ToString("F2")
                + "｜" + input.Tech
                + (_result.Ok ? "｜风扇 Ø" + _result.Design.Fan.Diameter.ToString("F2") + " m" : "｜" + _result.Failure));
            Toast(_result.Ok
                ? string.Format(Loc.T("初稿好了：风扇 Ø{0:F2} m，点向导最下面那颗按钮装上", "Draft ready: fan dia {0:F2} m - use the button at the bottom of the wizard"),
                    _result.Design.Fan.Diameter)
                : Loc.T("没生成出来，原因在向导窗口里", "Nothing was generated - see the wizard window"));
        }

        private void Apply()
        {
            if (_result == null || !_result.Ok || _result.Design == null || _target == null) return;
            // 先把"交出去之前"那份记下来，再改。顺序反了就是记下改完之后的样子，撤销成空操作。
            WizardUndo.Push(_target.Design);
            EngineDesignData draft = _result.Design.Clone();
            _target.ApplyDesign(draft, true);
            // 回读：真的换成这一份才算"交出去了"。
            // `ApplyDesign` 一进门就有 `if (d == null) return;` 这样的出口，只写"已交给设计器"
            // 而不核对，就是又一处"说得比做得多"——玩家看到一句话，零件什么也没变。
            bool took = ReferenceEquals(_target.Design, draft) && _target.Design != null;
            // 日志：交出去这一刻，零件到底是哪一台、设计名是什么。玩家下次说"应用不上"，
            // 这一行能分清"根本没走到这里"和"走到了但零件没换"。
            Debug.Log("[JetForge] 向导交出初稿 " + (took ? "成功" : "失败")
                + "｜零件 " + (_target.part != null && _target.part.partInfo != null ? _target.part.partInfo.title : "?")
                + "｜风扇 Ø" + draft.Fan.Diameter.ToString("F2") + " m");
            if (!took)
            {
                _status = Loc.T("没换上：零件上的设计没有变（这句请报给作者）",
                    "Not applied: the part's design did not change (please report this)");
                Toast(Loc.T("没换上：零件上的设计没变", "Not applied: the design did not change"));
                return;   // 窗口不关，让这句话留在玩家眼前
            }
            _status = Loc.T("已装到这台上；设计器已经打开，可以接着微调，不想要就点下面的撤销",
                "Put on this engine; the designer is open for further tuning, and undo below if you do not want it");
            Toast(string.Format(Loc.T("已装到这台上：风扇 Ø{0:F2} m", "Applied: fan dia {0:F2} m"), draft.Fan.Diameter));
            _visible = false;
            // 把设计器叫出来。以前只关掉向导窗口——而设计器可能根本没开着，
            // 玩家看到的就是"窗口关了，什么都没发生"，也就是那句报了三次的"应用不上"。
            JetForgeDesignerWindow.Open(_target);
        }

        /// <summary>退回向导按下之前的那份设计。网格按设计重建，所以不必另存网格。</summary>
        private void Undo()
        {
            if (_target == null) return;
            EngineDesignData back = WizardUndo.Pop();
            if (back == null) { _status = Loc.T("没有可撤销的步骤", "nothing to undo"); return; }
            _target.ApplyDesign(back, true);
            _status = Loc.T("已退回向导改动之前的样子", "reverted to the design as it was before the wizard");
        }

        /// <summary>
        /// 滑杆与输入框两种改法：输入框失焦时以输入框为准。
        /// 范围与单位由调用点给，**不再拿界面上那句文案当标识**——以前这里写
        /// `id == Loc.T("推力", "Thrust")` 来分派，等于用「屏幕上显示的字」当枚举：
        /// 文案一改（或以后翻译再动一个词），分派就静默落到 else 分支，
        /// 马赫框会按 0~25 km 的范围夹一遍，玩家输入 26 马赫变成 25 —— 看着像能输，其实被换掉了。
        /// </summary>
        private string TextField(string text, ref double value, double min, double max, string unit)
        {
            GUILayout.BeginHorizontal();
            string got = GUILayout.TextField(text, GUILayout.Width(70f));
            if (got != text)
            {
                double v;
                if (double.TryParse(got, out v)) value = Math.Min(max, Math.Max(min, v));
            }
            GUILayout.Label(unit);
            GUILayout.EndHorizontal();
            return got;
        }

        private static void Row(string s)
        {
            GUILayout.Label(s, GUILayout.ExpandWidth(true));
        }

        private static bool Button(string s, bool on)
        {
            return GUILayout.Toggle(on, s, GUI.skin.button);
        }
    }
}