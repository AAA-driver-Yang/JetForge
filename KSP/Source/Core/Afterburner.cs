using System;

namespace JetForge.Core
{
    /// <summary>加力为什么不着。顺序即判据顺序：先看命令，再看贫富油，最后看压力。</summary>
    public enum AbLightStatus
    {
        Off,          // 玩家没开
        Lit,          // 正常点着
        TooLean,      // 油太少：掺混后低于贫油熄火极限
        TooRich,      // 油太多：富油吹熄（真机上是"放炮"然后灭）
        LowPressure   // 燃烧室压力太低：高原/高空点不着
    }

    public struct AbResult
    {
        public AbLightStatus Status;
        public double Far;          // 加力段油气比（相对主流空气质量）
        public double DuctKPa;      // 加力段压力估计（kPa）
        public string Note;         // 给人看的一句话
        /// <summary>这一档控制系统有没有点火监控（读边界、能自己放弃点火）。模拟式 FADEC 起才有。</summary>
        public bool Monitored;
        /// <summary>监控发现点不着、于是主动把油断了（数字机才会做；关断发生在边界不满足的那一帧）。</summary>
        public bool AutoCut;

        public bool Lit { get { return Status == AbLightStatus.Lit; } }
    }

    /// <summary>
    /// 加力点火边界。此前模型里只要有油就一定点着——玩家一路拉到 25 km 按加力照样推，
    /// 而真机在高空加力会点不着（压力太低）或用多了直接把火吹熄（富油）。
    ///
    /// 三个边界都是**量级拟合**，不是燃烧化学：
    ///  · 贫油熄火 FAR ≈ 0.004（约当量比 0.06，火焰留不住）
    ///  · 富油吹熄 FAR ≈ 0.065（约当量比 0.95，再高就缺氧）
    ///  · 加力段压力低于 ≈25 kPa 点不着（真机加力高度上限大致 16~19 km，按这里的压力模型换算就是 25 kPa）
    /// </summary>
    public static class AfterburnerLight
    {
        public const double FarLean = 0.004;
        public const double FarRich = 0.065;
        public const double MinDuctKPa = 25.0;

        /// <summary>加力段压力 ≈ 环境压力 × 该系数。运行中的核心机喷管落压比大致 1.5~3，取 2.0。</summary>
        public const double DuctOverAmbient = 2.0;

        /// <summary>玩家没直接给油气比时，按加力形式取缺省值（与 Cycle.AbTypes 一致）。</summary>
        public static double DefaultFar(AfterburnerSpec ab)
        {
            switch (ab.Kind)
            {
                case AfterburnerKind.SprayRing: return 0.040;
                case AfterburnerKind.Dump: return 0.055;
                default: return 0.030;
            }
        }

        /// <summary>实际进燃烧室的油气比：玩家拧了喷油量就用它，否则按形式缺省。</summary>
        public static double EffectiveFar(AfterburnerSpec ab)
        {
            return ab.FuelRatio > 0.0 ? ab.FuelRatio : DefaultFar(ab);
        }

        /// <summary>
        /// 点火器能量决定的"最低可点压力"。老点火器（机械/液压年代）能量低，
        /// 高空稀薄到一定程度就点不着，而数字机带高压点火与补燃监控，能撑到更低。
        ///
        /// 25 kPa 是老口径（原来写死的常数），现在给液压与模拟档保留它——
        /// 老存档、老断言都不能因为这一轮而改数。
        /// </summary>
        public static double MinDuctKPaOf(ControlKind control)
        {
            switch (control)
            {
                case ControlKind.None:
                case ControlKind.Mechanical: return 40.0;
                case ControlKind.Hydro: return MinDuctKPa;
                case ControlKind.Analog: return MinDuctKPa;
                case ControlKind.Fadec: return 22.0;
                default: return MinDuctKPa;
            }
        }

        /// <summary>老口径：按机械液压的点火边界算。既有调用点一个数都不用改。</summary>
        public static AbResult Check(EngineDesignData d, double alt, double mach, bool commanded)
        {
            return Check(d, alt, mach, commanded, ControlKind.Hydro);
        }

        /// <summary>
        /// 按控制系统档次算点火边界。档次只改两件事：
        /// ①老点火器的最低可点压力更高（高空点不着，这是真机的年代差异）；
        /// ②数字机有**点火监控**：边界不满足时它不会一直往里喷油等"说不定能着"，
        ///   而是当帧断油（<see cref="AbResult.AutoCut"/>）——这就是"点不着就灭"。
        /// </summary>
        public static AbResult Check(EngineDesignData d, double alt, double mach, bool commanded, ControlKind control)
        {
            AbResult r = new AbResult();
            r.Monitored = ControlSystem.Rank(control) >= ControlSystem.Rank(ControlKind.Analog);
            if (d == null || d.Afterburner == null || !d.Afterburner.Enabled)
            {
                r.Status = AbLightStatus.Off;
                r.Note = Loc.T("这台没配加力", "No afterburner fitted");
                return r;
            }
            if (!commanded)
            {
                r.Status = AbLightStatus.Off;
                r.Note = Loc.T("加力关", "Afterburner off");
                return r;
            }

            r.Far = EffectiveFar(d.Afterburner);

            double pAmb, tAmb;
            Gas.Isa(alt, out pAmb, out tAmb);
            // 加力段压力：环境压力 × 2（核心机喷管落压比的量级），再按马赫抬高冲压。
            double ram = 1.0 + 0.2 * mach * mach;
            r.DuctKPa = pAmb * DuctOverAmbient * Math.Pow(ram, 3.5) / 1000.0;
            double minDuct = MinDuctKPaOf(control);

            if (r.Far < FarLean)
            {
                r.Status = AbLightStatus.TooLean;
                r.Note = string.Format(Loc.T("油气比 {0:F4} 低于贫油熄火极限 {1:F3}，火焰留不住", "Fuel/air ratio {0:F4} is below the lean blowout limit {1:F3}; the flame will not hold"), r.Far, FarLean);
                return MarkCut(r, control);
            }
            if (r.Far > FarRich)
            {
                r.Status = AbLightStatus.TooRich;
                r.Note = string.Format(Loc.T("油气比 {0:F4} 高于富油吹熄极限 {1:F3}，会把火吹灭", "Fuel/air ratio {0:F4} is above the rich blowout limit {1:F3}; it blows the flame out"), r.Far, FarRich);
                return MarkCut(r, control);
            }
            if (r.DuctKPa < minDuct)
            {
                r.Status = AbLightStatus.LowPressure;
                r.Note = string.Format(Loc.T("加力段只有 {0:F1} kPa（低于 {1:F0} kPa 点不着），高度 {2:F0} m 太高了", "The afterburner duct sees only {0:F1} kPa (below the {1:F0} kPa light-off floor); {2:F0} m is too high"),
                    r.DuctKPa, minDuct, alt);
                return MarkCut(r, control);
            }

            r.Status = AbLightStatus.Lit;
            r.Note = string.Format(Loc.T("油气比 {0:F4}，加力段 {1:F0} kPa", "Fuel/air ratio {0:F4}, afterburner duct {1:F0} kPa"), r.Far, r.DuctKPa);
            return r;
        }

        /// <summary>数字机在边界外不会"喷着油等它着"：当帧就断（其余档次只是退油）。</summary>
        private static AbResult MarkCut(AbResult r, ControlKind control)
        {
            if (ControlSystem.Rank(control) >= ControlSystem.Rank(ControlKind.Fadec))
            {
                r.AutoCut = true;
                r.Note = r.Note + Loc.T("｜数字控制断油（不试第二次）", " | digital control cut the fuel (no second attempt)");
            }
            return r;
        }

        /// <summary>这台机器在当前形式下能点火的高度上限（静止大气，供界面提示用）。老口径 = 液压。</summary>
        public static double CeilingMeters(EngineDesignData d, double mach)
        {
            return CeilingMeters(d, mach, ControlKind.Hydro);
        }

        /// <summary>按控制系统档次算的点火高度上限。点火器越弱，这个数越低。</summary>
        public static double CeilingMeters(EngineDesignData d, double mach, ControlKind control)
        {
            double lo = 0.0, hi = 30000.0;
            for (int i = 0; i < 40; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Check(d, mid, mach, true, control).Lit) lo = mid; else hi = mid;
            }
            return lo;
        }
    }
}