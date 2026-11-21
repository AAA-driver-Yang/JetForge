using System;

namespace JetForge.Core
{
    /// <summary>
    /// 风扇减速齿轮箱的扭转变形（一次性算出，不含时间历程）。
    ///
    /// 为什么值得算：齿轮传动把风扇和低压轴解耦之后，**风扇转速与轴转速不再相同**，
    /// 两者之间只剩一根轴在传扭矩。这根轴有多软，决定了满油门时风扇叶片会"晚半拍"，
    /// 也决定了齿轮箱自身的扭振频率落在哪儿。之前这里只有惯量（见
    /// <see cref="JetForge.Core.ShaftSet.GearboxInertia"/>），柔度一直写着"没建模"。
    ///
    /// 口径（都是为了不必凭空编系数）：
    /// - 质量用与 <see cref="MassCost"/> 同一个闭式：`40 + 0.0055·风扇kW·比值`；
    /// - 形状当成薄壁圆筒：半径取 0.30 倍风扇半径（行星架与齿圈都在这个量级），
    ///   轴向长度取 <see cref="EngineLength.GearboxLength"/>；
    /// - 壁厚不另设参数，反解成"质量装进这个筒"：`t = m / (ρ·2π·r·L)`；
    /// - 材料取钢：剪切模量 79.3 GPa、密度 7850 kg/m³。
    ///
    /// 没建模的：齿面接触柔度、轴承游隙、多级行星的均载——真机的扭振是模态问题，
    /// 这里只给"静扭矩下扭多少度"和"本体扭振频率"两个量级判断。
    /// </summary>
    public sealed class TorsionResult
    {
        public bool Present;
        public double Radius;        // m
        public double Wall;          // m
        public double Length;        // m
        public double Mass;          // kg
        public double Stiffness;     // N·m/rad
        public double Torque;        // N·m（风扇轴上的设计扭矩）
        public double TwistDeg;      // 设计扭矩下的扭转角
        public double FreqHz;        // 本体扭振频率
        public string Note = "";
    }

    public static class Torsion
    {
        /// <summary>钢的剪切模量（Pa）。</summary>
        public const double ShearModulus = 79.3e9;
        /// <summary>钢的密度（kg/m³）。</summary>
        public const double SteelDensity = 7850.0;
        /// <summary>回转半径 / 风扇半径。与 <see cref="JetForge.Core.ShaftSet.GearboxInertia"/> 同一口径。</summary>
        public const double RingRadiusFrac = 0.30;

        /// <summary>齿轮箱质量闭式。与 <see cref="MassCost"/> 里那一行必须永远一致。</summary>
        public static double GearboxMass(double fanPowerKW, double ratio)
        {
            double kw = fanPowerKW > 0.0 && GasState.IsFinite(fanPowerKW) ? fanPowerKW : 0.0;
            double r = ratio > 1.0001 && GasState.IsFinite(ratio) ? ratio : 1.0;
            return 40.0 + 0.0055 * kw * r;
        }

        /// <summary>
        /// 算一台机器的齿轮箱扭转量。直驱（比值 ≤ 1）或没有风扇时 <c>Present = false</c>，
        /// 读数保持 0——老存档与直驱机型的显示一个数都不会变。
        /// </summary>
        /// <param name="d">设计。</param>
        /// <param name="fanPowerW">风扇轴功率（W），来自设计点。</param>
        /// <param name="fanRpm">风扇设计转速（rpm）。</param>
        public static TorsionResult Evaluate(EngineDesignData d, double fanPowerW, double fanRpm)
        {
            TorsionResult t = new TorsionResult();
            if (d == null || d.Fan == null || !d.HasFan) return t;
            double ratio = d.Fan.GearRatio;
            if (!(ratio > 1.0001) || !GasState.IsFinite(ratio)) return t;
            if (!(fanPowerW > 0.0) || !GasState.IsFinite(fanPowerW)) return t;
            if (!(fanRpm > 1.0) || !GasState.IsFinite(fanRpm)) return t;

            double rFan = Math.Max(d.Fan.Diameter * 0.5, 0.02);
            double r = Math.Max(RingRadiusFrac * rFan, 0.02);
            double len = Math.Max(EngineLength.GearboxLength(d.Fan.Diameter, ratio), 0.02);
            double mass = GearboxMass(fanPowerW / 1000.0, ratio);

            // 壁厚由"质量装进这个筒"反解，不许再单独设一个厚度参数：
            // 多一个手填系数，就会多一个没人知道该填什么数的地方。
            double wall = mass / (SteelDensity * 2.0 * Math.PI * r * len);
            wall = Math.Min(Math.Max(wall, 0.004), 0.45 * r);

            double ro = r, ri = Math.Max(r - wall, 0.0);
            double jp = 0.5 * Math.PI * (ro * ro * ro * ro - ri * ri * ri * ri);   // 极惯矩 m⁴
            double k = ShearModulus * jp / len;                                     // N·m/rad

            double omegaFan = fanRpm * 2.0 * Math.PI / 60.0;
            double torque = fanPowerW / Math.Max(omegaFan, 1e-6);                   // N·m
            double twist = torque / Math.Max(k, 1e-6);                              // rad

            // 本体扭振：齿轮箱自己的惯量（薄环，与 ShaftSet 同口径）当集中质量
            double jgb = 0.5 * mass * (RingRadiusFrac * rFan) * (RingRadiusFrac * rFan);
            double freq = jgb > 1e-9 ? Math.Sqrt(k / jgb) / (2.0 * Math.PI) : 0.0;

            t.Present = true;
            t.Radius = r;
            t.Wall = wall;
            t.Length = len;
            t.Mass = mass;
            t.Stiffness = k;
            t.Torque = torque;
            t.TwistDeg = twist * 180.0 / Math.PI;
            t.FreqHz = freq;
            return t;
        }
    }
}