using System;
using System.Collections.Generic;
using System.IO;
using JetForge.Core;
using UnityEngine;

namespace JetForge.UI
{
    /// <summary>
    /// 二维剖视图：在设置页里把整台发动机画成一张剖面图，点哪个部件就跳到哪一页去改。
    ///
    /// 两条设计原则：
    /// ① **尺寸只有一份来源**——`JetForgeMeshBuilder.Cutaway()` 用的是三维网格那套 `Measure()`，
    ///    所以图上量到的占比就是模型上的占比，改了参数两张图一起变。
    /// ② **画成贴图而不是每帧画线**——只有参数变了才重新光栅化（几十毫秒），
    ///    平时就是贴一张 Texture2D。IMGUI 逐帧画上千条线反而更贵、还容易掉帧。
    /// </summary>
    public static class JetForgeCutaway
    {
        public const int TabIndex = JetForge.Core.DesignTabs.Cutaway;   // 设计器里"剖视"那一页
        public const int TexW = 720;
        public const int TexH = 260;

        /// <summary>一个可点区域（贴图像素坐标，画到屏幕上时再按比例换算）。</summary>
        public sealed class Hit
        {
            public string Name;
            public string Info;
            public int Tab;
            public float X0, X1;   // 贴图 x 范围
            public float RMax;     // 该部件最大半径（用来排序：先命中"贴得最近的"）
        }

        private static Texture2D _tex;
        private static string _sig;
        private static List<Hit> _hits = new List<Hit>();
        private static float _nextCheck;
        private static EngineDesignData _buildDesign;
        private static string _stats = "";

        public static string Stats { get { return _stats; } }

        /// <summary>
        /// 把剖视图存成 PNG 供人眼核对。游戏里的 IMGUI 截不到“只有我们这层皮”的图，
        /// 而这张图就是玩家真正看到的那一张——尺寸占比对不对，看它最直接。
        /// </summary>
        public static string DumpPng(EngineDesignData d, string path)
        {
            if (d == null) return "没有设计";
            try
            {
                Rebuild(d);
                if (_tex == null) return "没有图";
                // 这台 KSP 引用的 UnityEngine 里没有 ImageConversion 模块（EncodeToPNG 编不过），
                // 所以自己写 PPM：格式只有一行头 + 裸 RGB，没有压缩也没有依赖。
                Color32[] px = _tex.GetPixels32();
                int w = _tex.width, h = _tex.height;
                string header = "P6" + (char)10 + w.ToString() + " " + h.ToString() + (char)10 + "255" + (char)10;
                byte[] head = System.Text.Encoding.ASCII.GetBytes(header);
                byte[] body = new byte[w * h * 3];
                for (int i = 0; i < px.Length; i++)
                {
                    body[i * 3 + 0] = px[i].r;
                    body[i * 3 + 1] = px[i].g;
                    body[i * 3 + 2] = px[i].b;
                }
                File.WriteAllBytes(path, Concat(head, body));
                return string.Format("剖视图存成 PPM（无压缩位图，给人眼核对用）：{0}｜{1}×{2} 像素、{3} 字节｜{4}",
                    path, w, h, head.Length + body.Length, _stats);
            }
            catch (Exception e) { return "剖视图存档失败：" + e.Message; }
        }


        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        /// <summary>参数变了才重建。检查频率 5 Hz——比这更快人眼也看不出来，白烧 CPU。</summary>
        private static void EnsureTexture(EngineDesignData d)
        {
            if (d == null) return;
            if (_tex != null && Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.2f;
            string sig;
            try { sig = DesignSignature.Of(d); } catch { sig = null; }
            if (_tex != null && sig != null && sig == _sig && _buildDesign != null) return;
            _sig = sig;
            Rebuild(d);
        }

        private static void Rebuild(EngineDesignData d)
        {
            List<JetForgeMeshBuilder.CutawayShape> shapes = JetForgeMeshBuilder.Cutaway(d);
            EngineStations st = EngineGeometry.Of(d);
            float yFront = (float)st.YIn;
            float yBack = (float)st.YOut;
            float rMax = (float)st.MaxRadius;
            _buildFront = yFront;
            _buildBack = yBack;
            if (rMax < 1e-3f) rMax = 0.5f;
            // 纵向留一点边：唇口外皮会到 1.03RIn
            rMax *= 1.04f;

            if (_tex == null)
            {
                _tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false);
                _tex.wrapMode = TextureWrapMode.Clamp;
                _tex.filterMode = FilterMode.Point;
                _tex.hideFlags = HideFlags.HideAndDontSave;
            }

            Color32 bg = new Color32(26, 28, 31, 255);
            Color32[] px = new Color32[TexW * TexH];
            for (int i = 0; i < px.Length; i++) px[i] = bg;

            // 中轴线：一眼看出这是"对称剖面"而不是侧视图
            int axisV = (int)RToV(0f, rMax);
            for (int x = 0; x < TexW; x += 6)
            {
                px[axisV * TexW + x] = new Color32(64, 70, 78, 255);
                if (x + 1 < TexW) px[axisV * TexW + x + 1] = new Color32(64, 70, 78, 255);
            }

            _hits.Clear();
            for (int si = 0; si < shapes.Count; si++)
            {
                JetForgeMeshBuilder.CutawayShape s = shapes[si];
                if (s.Poly.Count < 3) continue;
                // 上下各画一份：发动机是回转体
                FillPolygon(px, s.Poly, rMax, s.Fill, false);
                FillPolygon(px, s.Poly, rMax, s.Fill, true);

                Hit h = new Hit();
                h.Name = s.Name;
                h.Info = s.Info;
                h.Tab = s.Tab;
                h.X0 = YToX(yFront, yBack, s.YFront);
                h.X1 = YToX(yFront, yBack, s.YBack);
                if (h.X1 < h.X0) { float tmp = h.X0; h.X0 = h.X1; h.X1 = tmp; }
                h.RMax = s.RMax;
                _hits.Add(h);
            }

            _tex.SetPixels32(px);
            _tex.Apply(false, false);
            _buildDesign = d;

            int clickable = 0;
            for (int i = 0; i < _hits.Count; i++) if (_hits[i].Tab >= 0) clickable++;
            _stats = string.Format("画了 {0} 个部件（{1} 个可点）｜轴向 {2:F2} m｜最粗处半径 {3:F3} m",
                _hits.Count, clickable, yFront - yBack, st.MaxRadius);
        }

        private static float YToX(float yFront, float yBack, float y)
        {
            float span = yFront - yBack;
            if (span <= 1e-6f) return 0f;
            return (yFront - y) / span * (TexW - 1);
        }

        private static float RToV(float r, float rMax)
        {
            if (rMax <= 1e-6f) return TexH * 0.5f;
            return TexH * 0.5f - r / rMax * (TexH * 0.5f - 3f);
        }

        /// <summary>
        /// 扫描线填充：按列求多边形与竖线的交点，成对填中间。
        /// 多边形本来就是 (y, r) 平面里的闭合形，镜像那一份只需把 r 取反。
        /// </summary>
        private static void FillPolygon(Color32[] px, List<Vector2> poly, float rMax, Color32 color, bool mirror)
        {
            int n = poly.Count;
            if (n < 3) return;
            // 先求包围盒（贴图坐标）
            float xMin = 1e9f, xMax = -1e9f;
            for (int i = 0; i < n; i++)
            {
                float x = PolyX(poly[i].x);
                if (x < xMin) xMin = x;
                if (x > xMax) xMax = x;
            }
            int ix0 = Mathf.Clamp(Mathf.FloorToInt(xMin), 0, TexW - 1);
            int ix1 = Mathf.Clamp(Mathf.CeilToInt(xMax), 0, TexW - 1);
            float[] hits = new float[n];
            for (int x = ix0; x <= ix1; x++)
            {
                int cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    Vector2 a = poly[i];
                    Vector2 b = poly[(i + 1) % n];
                    float ax = PolyX(a.x), bx = PolyX(b.x);
                    if ((ax <= x && bx >= x) || (bx <= x && ax >= x))
                    {
                        if (Mathf.Abs(bx - ax) < 1e-5f) continue;
                        float tt = (x - ax) / (bx - ax);
                        float r = a.y + (b.y - a.y) * tt;
                        if (cnt < hits.Length) hits[cnt++] = r;
                    }
                }
                if (cnt < 2) continue;
                // 交点两两成对
                for (int i = 0; i < cnt - 1; i++)
                    for (int k = i + 1; k < cnt; k++)
                    {
                        if (hits[k] < hits[i]) { float tmp = hits[i]; hits[i] = hits[k]; hits[k] = tmp; }
                    }
                for (int i = 0; i + 1 < cnt; i += 2)
                {
                    float r0 = hits[i], r1 = hits[i + 1];
                    if (mirror) { float tmp = -r1; r1 = -r0; r0 = tmp; }
                    int v0 = Mathf.Clamp(Mathf.RoundToInt(RToV(r1, rMax)), 0, TexH - 1);
                    int v1 = Mathf.Clamp(Mathf.RoundToInt(RToV(r0, rMax)), 0, TexH - 1);
                    if (v1 < v0) { int tmp = v0; v0 = v1; v1 = tmp; }
                    for (int v = v0; v <= v1; v++) px[v * TexW + x] = color;
                }
            }
        }

        private static float PolyX(float y)
        {
            // 与 YToX 一致的换算，但用当前这一次构建的两端。
            float span = _buildFront - _buildBack;
            if (span <= 1e-6f) return 0f;
            return (_buildFront - y) / span * (TexW - 1);
        }

        private static float _buildFront = 1f;
        private static float _buildBack = -1f;

        /// <summary>画在给定区域里，并处理悬停/点击。返回被点中的页号（没点返回 -1）。</summary>
        public static int Draw(Rect area, EngineDesignData d)
        {
            if (d == null) return -1;
            EnsureTexture(d);
            if (_tex == null) return -1;

            GUI.DrawTexture(area, _tex, ScaleMode.StretchToFill, false);

            // 鼠标落在哪一个部件上：取"最窄的那个"——外壳和核心机匣在 x 上大面积重叠，
            // 只有最窄的那个才是玩家想看的那一行。
            Vector2 mouse = Event.current.mousePosition;
            Hit best = null;
            if (area.Contains(mouse))
            {
                for (int i = 0; i < _hits.Count; i++)
                {
                    Hit h = _hits[i];
                    float sx0 = area.x + h.X0 / TexW * area.width;
                    float sx1 = area.x + h.X1 / TexW * area.width;
                    if (mouse.x < sx0 - 1f || mouse.x > sx1 + 1f) continue;
                    if (best == null || (sx1 - sx0) < HitWidth(best, area)) best = h;
                }
            }

            if (best != null)
            {
                float sx0 = area.x + best.X0 / TexW * area.width;
                float sx1 = area.x + best.X1 / TexW * area.width;
                Color old = GUI.color;
                GUI.color = new Color(0.55f, 0.78f, 1f, 0.22f);
                GUI.DrawTexture(new Rect(sx0, area.y, Mathf.Max(sx1 - sx0, 1f), area.height), Texture2D.whiteTexture);
                GUI.color = old;

                string tip = best.Name + "\n" + best.Info
                    + (best.Tab >= 0 ? "\n（点一下去改）" : "");
                DrawTip(new Rect(mouse.x + 14f, mouse.y + 10f, 240f, 62f), tip);

                if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    Event.current.Use();
                    return best.Tab;
                }
            }
            return -1;
        }

        private static float HitWidth(Hit h, Rect area)
        {
            return Mathf.Abs(h.X1 - h.X0) / TexW * area.width;
        }

        private static void DrawTip(Rect r, string text)
        {
            // 靠右/靠下时往回收，免得提示框跑出窗口
            if (r.xMax > Screen.width - 8f) r.x -= 260f;
            if (r.yMax > Screen.height - 8f) r.y -= 72f;
            Color old = GUI.color;
            GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.92f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(r.x + 6f, r.y + 4f, r.width - 12f, r.height - 8f), text);
        }

        /// <summary>自检用：不画图，只把剖视模型的关键数字打出来。</summary>
        public static string Describe(EngineDesignData d)
        {
            return JetForgeMeshBuilder.DescribeCutaway(d);
        }
    }
}