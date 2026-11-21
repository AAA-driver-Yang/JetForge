using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using JetForge.Core;

namespace JetForge.FX
{

    /// <summary>
    /// partial：看得见的那部分——颜色随排气温度走、亮度随推力走、喘振时闪。
    /// 它只写材质属性，不决定"该不该有羽流"（那是 Sync 那半边的事）。
    /// </summary>
    public sealed partial class JetForgeWaterfall
    {
        /// <summary>
        /// 尾焰颜色跟着**排气温度**走：低温偏黄、高温偏蓝紫；亮度跟推力。
        ///
        /// 为什么直接写材质、不走 COLORMODIFIER：
        /// 我们的模板里 `_StartTint`/`_EndTint` 是静态 `COLOR{...}`，全文 279 个
        /// FLOATMODIFIER 里**没有任何一个颜色修改器**碰这两个名字。既然没人碰，
        /// 我们写进去就不会被 Waterfall 的 modifier 每帧回灌覆盖——比新加一个
        /// 需要自定义控制器的 COLORMODIFIER 更可控（控制器子类里只有
        /// Throttle/Thrust/Atmosphere/... 那些，温度得自己造，而原版没有任何
        /// `CustomPush` 的 cfg 范例，吃不准 `LoadControllers` 认不认）。
        ///
        /// `_Brightness` **不能碰**：`tBright` 那个 FLOATMODIFIER 是 REPLACE，
        /// 我们乘上去会一帧乘一次，最后爆掉。要更亮就抬颜色本身——这几个羽流
        /// 材质是加法混合，颜色值大于 1 就是更亮（HDR），不需要改 shader。
        /// </summary>
        /// <summary>
        /// 喘振的视觉：真实喘振是燃烧室里"砰、砰"地回火，尾焰不是变亮，
        /// 是**一亮一暗地闪**，而且每次闪的那一下颜色发白发橙（没烧完的油在喷口外接着烧）。
        /// 所以这里只给三个数：在不在喘、闪到第几拍了、这一拍的强度。
        /// </summary>
        public void SetSurge(bool active, double phase01, double strength)
        {
            bool changed = active != _surgeOn;
            _surgeOn = active;
            _surgePhase = active ? (float)(phase01 - Math.Floor(phase01)) : 0f;
            _surgeStrength = active ? (float)Clamp01(strength) : 0f;
            if (changed) _visT7 = -1.0;   // 刚进/刚出喘振，下一帧必须重画
        }

        /// <summary>
        /// 一拍里的闪烁系数（纯函数，自检直接验）：0 = 平稳，1 = 砰的那一下最亮。
        /// 前 45% 起亮、45~70% 衰减、之后为空——拍长与占空比取自 Surge 的同一套比例。
        /// </summary>
        public static float Flicker(double phase01, double strength)
        {
            float p = (float)(phase01 - Math.Floor(phase01));
            float v;
            if (p < 0.45f) v = p / 0.45f;
            else if (p < 0.70f) v = 1f - (p - 0.45f) / 0.25f;
            else v = 0f;
            return Mathf.Clamp01(v) * (float)Clamp01(strength);
        }

        private float SurgeFlicker()
        {
            if (!_surgeOn) return 0f;
            return Flicker(_surgePhase, _surgeStrength);
        }

        public void SetVisualState(double t7, double thrustN, double refThrustN)
        {
            if (!Attached) return;

            Color s0, e0;
            float glow, t;
            Colors(t7, thrustN, refThrustN, out s0, out e0, out glow, out t);

            // 喘振：亮度按拍子闪，颜色往白热/橙推。
            // "闪"比"整体变亮"更像回火——玩家看到的是断续的火，不是一根更粗的火。
            float flick = SurgeFlicker();
            if (flick > 0.001f)
            {
                glow *= 1f + 1.35f * flick;
                s0 = Color.Lerp(s0, new Color(1.00f, 0.97f, 0.90f), 0.60f * flick);
                e0 = Color.Lerp(e0, new Color(1.00f, 0.52f, 0.18f), 0.60f * flick);
            }

            // 温度差 8 K、亮度差 2%、闪动差 4% 以内不必重写（每帧写材质是白烧）。
            if (Math.Abs(t7 - _visT7) < 8.0 && Math.Abs(glow - _visGlow) < 0.02
                && Math.Abs(flick - _visFlick) < 0.04) return;
            _visT7 = t7;
            _visGlow = glow;
            _visFlick = flick;

            int n = 0;
            for (int i = 0; i < _mods.Length; i++)
            {
                if (!_known[i] || !_isOn[i]) continue;
                n += Paint(FxList(_mods[i]), s0, e0);
            }
            _visWritten = n;
            _visNote = string.Format("T7 {0:F0} K → 温度档 {1:F2}｜亮 {2:F2}×｜着色 {3} 个渲染体",
                t7, t, glow, n);
        }

        /// <summary>
        /// 纯函数：排气温度 → 两个 tint 颜色 + 亮度倍数。拆出来是为了自检能验
        /// "温度高就该更蓝"而不用真去建材质（主菜单里 FX 还没建，写不进任何东西）。
        /// </summary>
        public static void Colors(double t7, double thrustN, double refThrustN,
            out Color start, out Color end, out float glow, out float tNorm)
        {
            // 750 K 以下算"最黄"，2050 K 以上算"最蓝紫"。中间线性。
            float t = (float)Clamp01((t7 - 750.0) / 1300.0);
            tNorm = t;

            // 三档色标：暖黄（老涡喷干态/小加力）→ 亮白黄（中等加力）→ 蓝紫（高温加力）
            start = Color.Lerp(new Color(1.00f, 0.70f, 0.28f), new Color(1.00f, 0.88f, 0.62f), Mathf.Clamp01(t / 0.55f));
            end = Color.Lerp(new Color(0.95f, 0.42f, 0.16f), new Color(0.78f, 0.62f, 0.86f), Mathf.Clamp01(t / 0.55f));
            if (t > 0.55f)
            {
                float u = Mathf.Clamp01((t - 0.55f) / 0.45f);
                start = Color.Lerp(start, new Color(0.80f, 0.86f, 1.00f), u);
                end = Color.Lerp(end, new Color(0.46f, 0.36f, 1.00f), u);
            }

            // 亮度：满推力 = 1.60 倍，零推力 = 0.75 倍。推力越大越亮。
            double refN = refThrustN > 1.0 ? refThrustN : 1.0;
            glow = 0.75f + 0.85f * (float)Clamp01(thrustN / refN);
            start *= glow;
            end *= glow;
        }


        /// <summary>自检用的一句话：某温度下的火色（RGB + 蓝红比）。</summary>
        public static string DescribeColor(double t7, double thrustN, double refThrustN)
        {
            Color s, e;
            float glow, t;
            Colors(t7, thrustN, refThrustN, out s, out e, out glow, out t);
            double ratio = s.r > 1e-4 ? s.b / s.r : 0.0;
            return string.Format("T7 {0:F0} K：起 {1:F2},{2:F2},{3:F2} 尾 {4:F2},{5:F2},{6:F2}｜亮 {7:F2}×｜蓝红比 {8:F2}",
                t7, s.r, s.g, s.b, e.r, e.g, e.b, glow, ratio);
        }

        private double _visT7 = -1.0;
        private float _visGlow = -1f;
        private float _visFlick = -1f;
        private bool _surgeOn;
        private float _surgePhase;
        private float _surgeStrength;
        private int _visWritten;
        private string _visNote = "还没着色";

        /// <summary>自检用：最近一次着色的温度档、亮度倍数、写到几个渲染体。</summary>
        public string VisualNote { get { return _visNote; } }

        /// <summary>遍历一个模块的 FX，把两个 tint 颜色（和热浪的抖/糊）写进它们的渲染材质。返回写到几个材质。</summary>
        private static int Paint(IList fxList, Color start, Color end)
        {
            if (fxList == null) return 0;
            int n = 0;
            foreach (object fx in fxList)
            {
                if (fx == null) continue;
                try
                {
                    FieldInfo mf = fx.GetType().GetField("effectRendererMaterials",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    IList mats = mf != null ? mf.GetValue(fx) as IList : null;
                    if (mats == null) continue;
                    foreach (object o in mats)
                    {
                        Material m = o as Material;
                        if (m == null) continue;
                        // SetColor 对不存在的属性是静默无效，但有 HasProperty 就先问一句，
                        // 免得"写了但没生效"又被当成成功。
                        bool ok = false;
                        if (m.HasProperty("_StartTint")) { m.SetColor("_StartTint", start); ok = true; }
                        if (m.HasProperty("_EndTint")) { m.SetColor("_EndTint", end); ok = true; }
                        // 热浪：只有折射那一片材质有这个属性，羽流本体的材质问一句就跳过。
                        if (ok) n++;
                    }
                }
                // 探测豁免：材质属性探测：羽流本体材质没有这个属性，问一句就跳过
                catch { }
            }
            return n;
        }

        private static double Clamp01(double v) { return v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v); }

        /// <summary>加力满开时干羽流退掉多少。留 0.15 是让喷口那点亮还在。</summary>
        public const double DryFadeWithAb = 0.85;

        /// <summary>
        /// 干态羽流强度该给多少。没有加力燃烧室的机器一律 0：
        /// 它连加力都没有，干态那条"火焰"就只是模板带出来的东西，不该出现。
        /// 真机在高空的干态尾迹是一股近乎透明的热流，看得见的是热浪（refraction），不是火。
        /// </summary>
        public static double DryLevel(EngineDesignData d, double throttle, double abFrac)
        {
            if (d == null || d.Afterburner == null || !d.Afterburner.Enabled) return 0.0;
            if (throttle < 0.0) throttle = 0.0;
            if (throttle > 1.0) throttle = 1.0;
            if (abFrac < 0.0) abFrac = 0.0;
            if (abFrac > 1.0) abFrac = 1.0;
            return throttle * (1.0 - DryFadeWithAb * abFrac);
        }

        /// <summary>
        /// 没有加力燃烧室时喂给干模块的数——它只驱动热浪那一层。
        ///
        /// 为什么不是 0：模板里热浪的强度曲线（`tStrength`）横轴就是 throttle 控制器，
        /// 灌 0 等于把热浪钉死在"最小"档，玩家看到的是"关车了还在、也不随油门变"。
        /// 为什么不是 throttle：慢车就能看见一圈热气本来就不对，真机慢车的热浪很淡。
        /// 所以取 0.35·油门^0.8 这条，慢车几乎看不见，满油门到 0.35 档。
        /// </summary>
        public static double HazeLevel(double throttle)
        {
            if (throttle < 0.0) throttle = 0.0;
            if (throttle > 1.0) throttle = 1.0;
            return 0.35 * Math.Pow(throttle, 0.8);
        }

        /// <summary>兼容旧调用：干湿不再整模块互切（那样点火瞬间会闪），改成强度过渡。</summary>
        public void Toggle(bool wet) { }

        public string Describe()
        {
            if (!Available()) return "Waterfall：未安装（没有羽流）";
            return string.Format("Waterfall：{0}｜干={1}｜加力={2}｜缩放 {3:F2}｜等效羽流 {4:F1} m｜{5}",
                Attached ? "已挂" : "未挂",
                TemplateOf(DryClass),
                TemplateOf(WetClass),
                ScaleR, ScaleL, Note);
        }

        /// <summary>
        /// PAW 上那一行只留玩家能用上的数：这束火多长、多粗。
        /// 模板名、控制器归属、"灌进去没有"这些是我们的排查口径，全部走日志（见 flight.log 与 KSP.log）。
        /// 玩家每次点开零件都读一遍"jetforge_wet_fan_mid"，除了让人以为出了故障没有别的用。
        /// </summary>
        public string Short()
        {
            if (!Available()) return "没有 Waterfall";
            if (!Attached) return "未挂上（看日志）";
            return string.Format("{0:F1} m｜{1:F2}×", ScaleL, ScaleR);
        }
    }
}
