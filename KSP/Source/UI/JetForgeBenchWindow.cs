using System;
using System.Collections.Generic;
using UnityEngine;
using JetForge.Core;
using JetForge.Core.Diagnostics;
using JetForge.KSP;

namespace JetForge.UI
{
    /// <summary>
    /// 发动机测试台：挑一个工况（高度、马赫、油门），把这台机器在那一点上的表现摆出来。
    ///
    /// 它**不是第二套求解器**。公式只有 `Core/Diagnostics/EngineBench` 那一份，而那一份调的
    /// 又是飞行里同一个 `OffDesign.Solve`。台架做的事只有三件：选参数、调求解、把数字排成一行。
    /// 之所以要这么区分：一份引擎模型如果有两套算法，迟早会分家——玩家照着台架调好一台机器，
    /// 飞起来却是另一个数，那比没有台架更糟。
    ///
    /// 为什么不做成"每帧实时算"：一格 1.67 ms（875 点体检实测），每帧算会吃掉帧预算。
    /// 所以按一下算一格，或者按一下扫一排。
    /// </summary>
    public class JetForgeBenchWindow : MonoBehaviour
    {
        private const int WindowId = 0x4A54;

        private static JetForgeBenchWindow _instance;

        private enum View { Point = 0, Throttle = 1, Altitude = 2, Mach = 3 }

        private Rect _rect = new Rect(260f, 90f, 760f, 520f);
        private bool _visible;
        private ModuleJetForgeEngine _target;
        private EngineDesignData _design;

        private View _view = View.Point;
        private double _alt;
        private double _mach;
        private double _throttle = 1.0;

        private List<BenchRow> _rows = new List<BenchRow>();
        private string _note = "";

        public static void Open(ModuleJetForgeEngine target)
        {
            if (target == null || target.Design == null) return;
            if (_instance == null)
            {
                GameObject go = new GameObject("JetForgeBench");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<JetForgeBenchWindow>();
            }
            _instance.Bind(target);
        }

        private void Awake() { _instance = this; }

        public void Bind(ModuleJetForgeEngine target)
        {
            _target = target;
            // 拷一份：台架跑的时候玩家可能还在改设计，边算边变会算出"半新半旧"的一行。
            _design = target != null && target.Design != null ? target.Design.Clone() : null;
            _alt = _design != null ? _design.DesignAlt : 0.0;
            _mach = _design != null ? _design.DesignMach : 0.0;
            _rows.Clear();
            _note = "";
            _visible = true;
        }

        private void Run()
        {
            if (_design == null) { _note = Loc.T("没有接上发动机", "No engine attached"); return; }
            _rows.Clear();
            try
            {
                switch (_view)
                {
                    case View.Point:
                        _rows.Add(EngineBench.Point(_design, _alt, _mach, _throttle, Install()));
                        break;
                    case View.Throttle:
                        _rows.AddRange(EngineBench.ThrottleLine(_design, _alt, _mach, Install()));
                        break;
                    case View.Altitude:
                        _rows.AddRange(EngineBench.Sweep(_design, true, Install()));
                        break;
                    case View.Mach:
                        _rows.AddRange(EngineBench.Sweep(_design, false, Install()));
                        break;
                }
                _note = _rows.Count == 0 ? Loc.T("这一组没算出来", "This set did not solve") : "";
            }
            catch (Exception e)
            {
                _note = Loc.T("求解抛了异常：", "The solve threw: ") + e.Message;
            }
        }

        private IntakeInstall Install()
        {
            return _target != null ? _target.Install : null;
        }

        private void OnGUI()
        {
            if (!_visible) return;
            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, Loc.T("JetForge · 发动机测试台", "JetForge - Engine bench"));
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label(Loc.T("这里的每一格都是正式求解的结果，和飞行中用同一套公式，只是把高度、速度、油门换着参数跑一遍。", "Every cell here is a real solve - the same equations in flight, just swept over altitude, speed and throttle."));

            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_view == View.Point, Loc.T("单点", "Single"), GUI.skin.button, GUILayout.Width(70f))) _view = View.Point;
            if (GUILayout.Toggle(_view == View.Throttle, Loc.T("油门线", "Throttle"), GUI.skin.button, GUILayout.Width(70f))) _view = View.Throttle;
            if (GUILayout.Toggle(_view == View.Altitude, Loc.T("高度剖面", "Altitude"), GUI.skin.button, GUILayout.Width(80f))) _view = View.Altitude;
            if (GUILayout.Toggle(_view == View.Mach, Loc.T("马赫剖面", "Mach"), GUI.skin.button, GUILayout.Width(80f))) _view = View.Mach;
            GUILayout.EndHorizontal();

            if (_view == View.Point)
            {
                GUILayout.Label(string.Format(Loc.T("高度 {0:F0} m", "Altitude {0:F0} m"), _alt));
                _alt = GUILayout.HorizontalSlider((float)_alt, 0f, 25000f);
                GUILayout.Label(string.Format(Loc.T("马赫 {0:F2}", "Mach {0:F2}"), _mach));
                _mach = GUILayout.HorizontalSlider((float)_mach, 0f, 2.5f);
                GUILayout.Label(string.Format(Loc.T("油门 {0:F0}%（台架按稳态算，不含加减速瞬态）", "Throttle {0:F0}% (steady state only, no acceleration transient)"), _throttle * 100.0));
                _throttle = GUILayout.HorizontalSlider((float)_throttle, 0f, 1f);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_rows.Count == 0 ? Loc.T("开始算", "Start") : Loc.T("重算", "Recompute"), GUILayout.Width(90f))) Run();
            if (GUILayout.Button(Loc.T("关掉", "Close"), GUILayout.Width(70f))) _visible = false;
            GUILayout.Label(string.Format(Loc.T("{0} 行", "{0} rows"), _rows.Count));
            GUILayout.EndHorizontal();

            if (_note.Length > 0) GUILayout.Label(_note);

            for (int i = 0; i < _rows.Count; i++)
                GUILayout.Label(_rows[i].Line());

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }
    }
}