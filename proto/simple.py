"""
JetForge · 简单模式（simple）
=============================

给"我想要一台大概这样的发动机"的人用。输入只有六个数：涵道比、总增压比、
燃烧室温度、排气温度、发动机尺寸，外加一个技术年代。没有级数、没有叶尖直径、
没有轴怎么连——这些全交给经验律去填。

**它和残酷模式的差别不是精度，是自由度的归属**：
残酷模式里每个几何尺寸都是玩家填的，算法只负责算；简单模式里几何是算法反推的，
玩家只决定气动热力目标。所以简单模式算出来的"这台机器长什么样"只是**一个可行的样子**，
不是唯一解。

核心算法（比残酷模式少三层）：
  1. 来流 → 总温总压（含进气道总压恢复）
  2. 压气机：总压比 → 压气机出口温度（多变效率）
  3. 燃烧室：烧到 T4，求油气比
  4. 涡轮：**只做能量守恒**——涡轮功 = 风扇功 + 压气机功，反推涡轮出口温度。
     不逐级求落压比，也不查级数够不够（那是"能不能造出来"的问题，简单模式不管）
  5. 喷管：核心/旁路各自算速度与压力推力
  6. 玩家没给风扇压比 fpr，就用实测统计式从涵道比反推（`fpr_from_bpr`）：
     真实大涵道比机的风扇压比集中在 1.4~1.8，涵道比越大风扇压比越低。
     这与"两股射流速度协调"不是一回事——后者在本模型里会把 BPR 2.75 算出 fpr 2.6，
     而真实同涵道比机型只有 1.6 左右，直接导致 T2 虚高、涡轮功不够、SFC 跑偏。

**非设计点的三条经验律（这是简单模式的真正内容）**
  1. 涡轮温比 TTR = T5/T4 由设计点能量平衡反解后**冻结**（AJE 的 SolverJet 同款做法）；
  2. 风扇与压气机压比由几何决定，非设计点**冻结** fpr；
  3. 油门只动 T4，于是涡轮功跟着变，**总增压比是能量平衡的涌现结果，不是输入**。
     这条很关键：它让"高空满油门增压比升高""慢车增压比骤降"自己长出来，
     不需要再手搓 OPR = 1+(OPR_d−1)·n^k 这种指数拟合。
     物理依据：涡轮能抽多少功由 T4 与 TTR 定，压气机分到多少功就决定它能压到多高。

内燃机式的细节一概不建模：级压比分布、喘振裕度、转子动力学、冷却、间隙泄漏。
这些是残酷模式存在的理由。
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

from jetforge import (
    CP_AIR, G0, LHV_JET, NPR_CRIT, R_AIR, TECH_PRESETS, TechLevel, _ab_max_temp,
    a_sound, cp_of, gamma_of, intake_tpr, isa, nozzle_flow, total_from_static,
)
from offdesign import choked_mass_flow  # noqa: F401  (保持与残酷模式同一套流量律)

FACE_MACH_STATIC = 0.5      # 海平面静止时取的面马赫（把"静止"变成可积分的进气道状态）
T0_SL = 288.15              # 换算转速的参考温度
TTR_IDLE = 0.78             # 慢车涡轮温比 T5/T4。满功率由设计点反解，慢车取这个值
TTR_IDLE_EXP = 1.5          # TTR 从设计值抬到慢车值的插值指数（>1 表示中段压得住）
T4_THROTTLE_EXP = 2.0       # 油门 → T4 的指数（线性会让低油门段推力虚高一大截）


@dataclass
class SimpleDesign:
    """简单模式的全部输入。没有几何，只有目标。"""
    name: str = "simple engine"
    tech: TechLevel = field(default_factory=lambda: TECH_PRESETS["1970s"])
    kind: str = "turbofan"              # turbojet / turbofan
    diameter: float = 1.2               # 发动机最大直径 m（涡扇=风扇，涡喷=进气口）
    bpr: float = 5.0                    # 涵道比（涡喷自动归零）
    opr: float = 25.0                   # 总增压比
    t4: float = 1450.0                  # 燃烧室出口温度 K
    t_exhaust: float = 0.0              # 排气温度 K：涡喷/涡扇无加力时作为 EGT 上限；有加力时就是 T7
    ab_fuel_ratio: float = 0.0          # 加力喷油量 far_ab，>0 即开启加力（优先于 t_exhaust）
    design_mach: float = 0.0
    design_alt: float = 0.0
    fpr: float = 0.0                    # 风扇压比；0 = 由涵道比按实测统计式反推
    nozzle_kind: str = "converging"     # converging / variable / converging_diverging
    t4_idle: float = 0.0                # 慢车 T4；0 = 自动取 40% 设计 T4
    ab_throttle: float = 0.85           # 加力油门门槛：低于它点火器不喷（对应 Flyout 的 throttle_threshold）
    eta_offdesign_k: float = 0.50       # 非设计点部件效率折减系数（见 _cycle 里的说明）

    def __post_init__(self):
        if self.kind == "turbojet":
            self.bpr = 0.0
        self.bpr = max(self.bpr, 0.0)
        self.opr = max(self.opr, 1.0)


@dataclass
class SimpleResult:
    ok: bool = True
    errors: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)

    alt: float = 0.0
    mach: float = 0.0
    throttle: float = 1.0

    # 设计输入回显 + 反推出来的量
    bpr: float = 0.0
    opr: float = 0.0
    fpr: float = 1.0
    cpr: float = 1.0

    n1_pct: float = 100.0
    n2_pct: float = 100.0
    n1_rpm: float = 0.0
    n2_rpm: float = 0.0

    mdot_air: float = 0.0
    mdot_core: float = 0.0
    T1: float = 0.0
    T2: float = 0.0
    T3: float = 0.0
    T4: float = 0.0
    T5: float = 0.0
    T7: float = 0.0
    P3: float = 0.0
    P5: float = 0.0
    far: float = 0.0
    far_ab: float = 0.0
    egt: float = 0.0

    thrust: float = 0.0
    thrust_core: float = 0.0
    thrust_bypass: float = 0.0
    ram_drag: float = 0.0
    net_thrust: float = 0.0
    fuel_flow: float = 0.0
    sfc: float = 0.0
    v_jet_core: float = 0.0
    v_jet_bypass: float = 0.0
    fn_per_mdot: float = 0.0

    self_sustaining: bool = False
    turbine_limited: bool = False       # 涡轮可用落压比不够，T5 被抬高
    spool: Dict[int, float] = field(default_factory=dict)

    def as_view(self) -> dict:
        """给 criteria.evaluate 用的字典视图。"""
        return {
            "ok": self.ok, "errors": list(self.errors), "warnings": list(self.warnings),
            "mdot_air": self.mdot_air, "mdot_core": self.mdot_core,
            "bpr": self.bpr, "opr": self.opr, "fpr": self.fpr,
            "T1": self.T1, "T2": self.T2, "T3": self.T3, "T4": self.T4,
            "T5": self.T5, "T7": self.T7, "P3": self.P3, "P5": self.P5,
            "far": self.far, "far_ab": self.far_ab,
            "thrust": self.net_thrust, "sfc": self.sfc, "fuel_flow": self.fuel_flow,
            "rpm": {}, "n_pct": {},
            "self_sustaining": self.self_sustaining,
        }


# ---------------------------------------------------------------------------
# 单点循环
# ---------------------------------------------------------------------------

def _t_exhaust_cap(sd: SimpleDesign) -> float:
    """加力段（或喷管入口）温度上限。"""
    tech = sd.tech
    cap = _ab_max_temp(tech)
    if sd.t_exhaust > 0.0:
        cap = min(cap, sd.t_exhaust)
    return cap


def fpr_from_bpr(bpr: float) -> float:
    """风扇压比的经验统计式。

    数据来源：`research/engine_data.md` 那 23 台里的涵道比—风扇压比对，
    真实机型离散度不小（同为 BPR≈5 的 CF6/JT9D 取 1.6~1.67，PW4000 取 1.75），
    所以这条曲线只保证**单调 + 落在带宽内**，精度按 ±10% 看待：

        BPR  0.0 → 1.98（纯涡喷，没有风扇，仅作连续过渡）
        BPR  0.4 → 1.90（EJ200 级低涵道比战斗机发动机）
        BPR  0.6 → 1.86（AL-31F 级）
        BPR  2.8 → 1.64
        BPR  5.0 → 1.55（CF6/JT9D 实测 1.60~1.67）
        BPR  5.9 → 1.52（CFM56 实测 1.6）
        BPR  8.4 → 1.48（GE90 实测 1.43）
        BPR  9.0 → 1.47（GEnx 实测 1.40）

    形式取双曲衰减：`fpr = 1.32 + 0.66/(1 + 0.38·BPR)`。
    渐近 1.32 K 是"无限大涵道比"的极限，等于超高通流风扇的单级压比下限。
    """
    if bpr <= 1e-6:
        return 1.0
    return 1.32 + 0.66 / (1.0 + 0.38 * bpr)


@dataclass
class SimpleConstants:
    """设计点解出来、非设计点要冻结住的那几个量。"""
    fpr_d: float = 1.0        # 设计点风扇压比（由涵道比统计式反推，仅作回显）
    fc: float = 0.0           # W_fan / W_comp，设计点算出后冻结——非设计点真正的锚
    ttr: float = 0.7          # T5/T4，由设计点能量平衡反解
    opr_d: float = 1.0
    bpr: float = 0.0
    Tt0_d: float = 288.15
    pt0_d: float = 101325.0
    mdot_d: float = 0.0        # 设计点吸入空气流量 kg/s
    T2_d: float = 0.0
    T3_d: float = 0.0
    T5_d: float = 0.0
    ok: bool = True
    errors: List[str] = field(default_factory=list)


def design_constants(sd: SimpleDesign) -> SimpleConstants:
    """在设计点跑一次能量平衡，反解 fc 与 TTR。

    设计点必须能复现玩家给的增压比，这是简单模式的"锚"：
    给定 (BPR, OPR, T4, 尺寸) 后，风扇压比由涵道比统计式定，涡轮该抽多少功、
    出口温度多少、TTR 与 fc 各是多少，全由这一点的能量平衡定死。
    之后非设计点只动 T4，其它跟着走。
    """
    c = SimpleConstants(bpr=sd.bpr, opr_d=sd.opr)
    c.fpr_d = sd.fpr if sd.fpr > 0.0 else fpr_from_bpr(sd.bpr)
    tech = sd.tech
    p0, T0 = isa(sd.design_alt)
    pt0, Tt0 = (total_from_static(p0, T0, sd.design_mach) if sd.design_mach > 0.0
                else (p0, T0))
    c.Tt0_d = Tt0
    c.pt0_d = pt0
    # 设计点流量：台架静止吸入量，面马赫取 FACE_MACH_STATIC
    c.mdot_d = _face_mass_flow(math.pi * (sd.diameter / 2.0) ** 2,
                               p0, T0, FACE_MACH_STATIC)

    gam = gamma_of(Tt0)
    eta_f, eta_c = tech.fan_eta_pol, tech.hpc_eta_pol
    T2 = Tt0 * c.fpr_d ** ((gam - 1.0) / (gam * eta_f))
    T3 = Tt0 * c.opr_d ** ((gam - 1.0) / (gam * eta_c))
    c.T2_d, c.T3_d = T2, T3

    if T3 >= sd.t4:
        c.ok = False
        c.errors.append(f"设计点压气机出口 {T3:.0f} K 已不低于 T4={sd.t4:.0f} K："
                        f"增压比 {sd.opr:.1f} 与燃烧室温度不相容")
        return c

    w_comp = cp_of(T3) * (T3 - T2)
    w_fan = cp_of(T2) * (T2 - Tt0) * (1.0 + c.bpr)
    w_turb = (w_comp + w_fan) / tech.mech_efficiency
    T5 = sd.t4 - w_turb / cp_of(sd.t4)
    if T5 <= 200.0:
        c.ok = False
        c.errors.append(f"设计点涡轮出口温度 {T5:.0f} K 不合理："
                        f"增压比 {sd.opr:.1f} 配 T4={sd.t4:.0f} K 时轴功率不平衡")
        return c
    c.T5_d, c.ttr = T5, T5 / sd.t4
    c.fc = w_fan / max(w_comp, 1e-6)
    return c


def _cycle(sd: SimpleDesign, alt: float, mach: float, t4_cmd: float,
           consts: SimpleConstants, mdot_scale: float = 1.0,
           ab_scale: float = 1.0, speed_frac: float = 1.0,
           ttr: Optional[float] = None) -> SimpleResult:
    """给定 T4 与冻结的 (fpr, TTR)，算一个工作点；总增压比由能量平衡涌现。

    ab_scale: 0~1，加力喷油量的比例。0 = 纯干态，1 = 满加力。
    speed_frac: 0~1，相对设计转速。只用来做效率折减，不参与流量（流量走 mdot_scale）。
    """
    res = SimpleResult(alt=alt, mach=mach)
    tech = sd.tech
    p0, T0 = isa(alt)
    res.T1 = T0

    # 非设计点部件效率折减。简单模式里没有特性线，效率本来是常数，
    # 这会让"降油门"看起来免费——实际上压气机在部分转速下攻角偏离设计、
    # 涡轮在低落压比下也掉效率，整机 SFC 会明显变差。
    # 用一个二次折减把趋势补回来：Δη/η ≈ -k·(1 - n)²，n=0.6 时约 -8%。
    eta_pen = 1.0 - sd.eta_offdesign_k * (1.0 - min(max(speed_frac, 0.0), 1.0)) ** 2

    if mach > 0.0:
        pt0, Tt0 = total_from_static(p0, T0, mach)
        tpr = intake_tpr(tech, mach)
        pt0 *= tpr
    else:
        Tt0, pt0 = T0, p0

    # ---- 流量：换算流量 ṁ√θ/δ 在等转速下守恒 ----
    # 站 1 记**总**温（与残酷模式同口径），判据要拿它算换算转速
    res.T1 = Tt0
    # 曾经写成"按面马赫积分"：mdot = ρ(pt0,Tt0)·a(Tt0)·max(M,0.5)·A。
    # 那样在 M0.85 海平面会把流量算到设计值的 1.95 倍（真实约 1.48 倍），
    # 因为把总压当成了可吸入的静压、还让迎面马赫直接进面积。
    # 正确的台架口径是：等换算转速下 ṁ·√θ/δ 为常数。
    mdot_air = (consts.mdot_d * (pt0 / max(consts.pt0_d, 1e-6))
                * math.sqrt(consts.Tt0_d / max(Tt0, 1.0)) * mdot_scale)
    bpr = sd.bpr
    mdot_core = mdot_air / (1.0 + bpr)
    res.mdot_air, res.mdot_core, res.bpr = mdot_air, mdot_core, bpr

    # ---- 风扇/压气机/涡轮：一个能量平衡闭环定三者的功与压比 ----
    # 冻结的是 fc = W_fan/W_comp（设计点算出），不是风扇压比。
    # 这条选择解决低功率下的死结：若冻结 fpr，慢车时风扇仍按设计压比吃功、
    # 涡轮供不起，增压比会被压到 1.x 而崩掉；冻结 fc 则风扇功随涡轮功一起退，
    # 慢车增压比自然落到 3~4（J79 实测正是 3.5 上下）。
    eta_b, dp_b = tech.burner_eta, tech.burner_dp_ratio
    eta_c = tech.hpc_eta_pol * eta_pen
    eta_f = tech.fan_eta_pol * eta_pen
    eta_t = tech.hpt_eta_pol * eta_pen
    gam = gamma_of(Tt0)
    cp4 = cp_of(t4_cmd)
    mec = tech.mech_efficiency
    fc = consts.fc

    ratio = Tt0 / max(consts.Tt0_d, 1.0)
    T2 = max(consts.T2_d * ratio, Tt0 + 1.0)
    T3 = max(consts.T3_d * ratio, T2 + 1.0)
    limited = False
    ttr_use = consts.ttr if ttr is None else ttr

    def balance(T5_in: float) -> Tuple[float, float, float, float, float]:
        """由涡轮出口温度反解 (T2, T3, opr, w_comp, w_turb)。

        注意 opr 不进入这个式子——涡轮功只由 T4 与 T5 定，压气机分到的功就是
        `w_turb/(1+fc)`，所以**给定 T5 时增压比是唯一的、无需迭代**。
        上一版把 opr 当迭代变量会踩到双不动点：低分支里涡轮受限把 T5 抬上去，
        又反过来压低 opr，慢车会被吸到 3.8 而真解在 5.3。
        """
        w_turb = cp4 * (t4_cmd - T5_in) * mec
        w_comp = w_turb / (1.0 + fc)
        w_fan_all = w_turb - w_comp
        t2 = Tt0 + w_fan_all / ((1.0 + bpr) * cp_of(T2))
        t3 = t2 + w_comp / cp_of(T3)
        return t2, t3, (t3 / Tt0) ** (gam * eta_c / (gam - 1.0)), w_comp, w_turb

    T5 = t4_cmd * ttr_use
    T2, T3, opr, w_comp, w_turb = balance(T5)
    if w_comp <= 0.0:
        res.errors.append(
            f"T4={t4_cmd:.0f} K 时涡轮功 {w_turb / 1000:.1f} kJ/kg 扣掉风扇后"
            f"没有剩余驱动压气机：这台机器在这个状态下转不起来")
        res.ok = False
        return res

    # 涡轮出口不能低于环境压强（喷管在它下游，靠剩下的压头出推力）。
    # 若冻结温比要求的膨胀比超过可用值，说明这台机器的涡轮在这个功率下"抽太狠"：
    # 按可用落压比把 T5 抬上去，重解功平衡，最多几轮就够。
    for _ in range(8):
        pt4 = pt0 * opr * (1.0 - dp_b)
        g5 = gamma_of(T5)
        pi_t = (t4_cmd / T5) ** (g5 / ((g5 - 1.0) * eta_t))
        pi_avail = max(pt4 / p0, 1.0001)
        if pi_t <= pi_avail:
            break
        limited = True
        T5_new = t4_cmd * pi_avail ** (-(g5 - 1.0) * eta_t / g5)
        if abs(T5_new - T5) < 1.0:
            T5 = T5_new
            break
        T5 = T5_new
        T2, T3, opr, w_comp, w_turb = balance(T5)
        if w_comp <= 0.0:
            res.errors.append(f"T4={t4_cmd:.0f} K 时涡轮已无剩余功率驱动压气机")
            res.ok = False
            return res

    pt4 = pt0 * opr * (1.0 - dp_b)
    g5 = gamma_of(T5)
    pi_t = (t4_cmd / T5) ** (g5 / ((g5 - 1.0) * eta_t))
    if pi_t > max(pt4 / p0, 1.0001):
        pi_t = max(pt4 / p0, 1.0001)

    res.T2, res.T3 = T2, T3
    res.opr = opr
    # 风扇压比在这里是涌现量：ΔT2 由分到的功决定，不再按设计值冻结
    res.fpr = (T2 / Tt0) ** (gam * eta_f / (gam - 1.0)) if T2 > Tt0 else 1.0
    res.cpr = max(opr / max(res.fpr, 1e-6), 1.0)
    res.P3 = pt0 * opr
    res.turbine_limited = limited
    if limited:
        res.warnings.append(
            f"涡轮可用落压比只有 {pt4 / p0:.2f}，低于冻结温比要求的 {pi_t:.2f}："
            f"出口温度被抬到 {T5:.0f} K（冻结值 {t4_cmd * ttr_use:.0f} K）")

    # ---- 燃烧室 ----
    cp3 = cp_of(res.T3)
    dT = t4_cmd - res.T3
    if dT <= 0.0:
        res.errors.append(f"T4={t4_cmd:.0f} K 不高于压气机出口 {res.T3:.0f} K：无法加热")
        res.ok = False
        return res
    denom = LHV_JET * eta_b - cp4 * dT
    if denom <= 0.0:
        res.errors.append("燃烧室温升过高，能量不守恒")
        res.ok = False
        return res
    far = cp3 * dT / denom
    res.far = far
    res.T4 = t4_cmd
    res.T5 = T5
    res.P5 = pt4 / max(pi_t, 1e-6)
    res.egt = T5

    # ---- 加力 ----
    T_noz, P_noz, far_ab = T5, res.P5, 0.0
    mdot_noz = mdot_core
    want_ab = (sd.ab_fuel_ratio > 0.0 or sd.t_exhaust > 0.0) and ab_scale > 0.0
    if want_ab:
        cap = _t_exhaust_cap(sd)
        if sd.ab_fuel_ratio > 0.0:
            far_ab = min(sd.ab_fuel_ratio * ab_scale, 0.045)
            dT_ab = far_ab * LHV_JET * 0.90 / cp_of(T5) / (1.0 + far_ab)
            T_noz = min(T5 + dT_ab, cap)
        else:
            # 玩家直接给排气温度：满加力时把温度顶到 t_exhaust，按比例插值
            T_target = min(cap, max(sd.t_exhaust, T5))
            T_noz = T5 + (T_target - T5) * min(ab_scale, 1.0)
            far_ab = max((T_noz - T5) * cp_of(T5) / (LHV_JET * 0.90), 0.0)
        if T_noz > T5 + 1.0:
            dp_ab = 0.03
            P_noz = res.P5 * (1.0 - dp_ab)
            mdot_noz = mdot_core * (1.0 + far_ab)
    res.T7 = T_noz
    res.far_ab = far_ab

    # ---- 喷管 ----
    eta_n = tech.nozzle_eta
    ve_c, ae_c, pe_c, f_c, choked = nozzle_flow(mdot_noz, P_noz, T_noz, p0, eta_n)
    pt2 = pt0 * max(res.fpr, 1.0)     # 旁路总压用当前风扇压比，不是设计值
    ve_b, ae_b, pe_b, f_b, _ = nozzle_flow(
        mdot_air - mdot_core, pt2, res.T2, p0, eta_n) if bpr > 0.0 else (0.0, 0.0, 0.0, 0.0, False)
    res.v_jet_core, res.v_jet_bypass = ve_c, ve_b
    res.thrust_core, res.thrust_bypass = f_c, f_b

    V0 = mach * a_sound(T0)
    gross = f_c + f_b
    # 净推力 = 总推力 − 冲压阻力 − 短舱型面阻力。
    # 冲压阻力是 ṁ·V0 那一项（进气道把来流从 V0 刹到 ~0），
    # 型面阻力只占前者的百分之几，别拿它顶替前者。
    A = math.pi * (sd.diameter / 2.0) ** 2
    nac_cd, a_front = 0.08, A
    res.ram_drag = mdot_air * V0 + 0.5 * (p0 / (R_AIR * T0)) * V0 ** 2 * a_front * nac_cd
    res.thrust = gross
    res.net_thrust = gross - res.ram_drag

    # ---- 油耗 ----
    res.fuel_flow = mdot_core * far + mdot_noz * far_ab
    # 净推力 <= 0 时 SFC 无定义，给 0：给 inf 会让 realIsp 变 0，夹 1e-6 分母又会得到 1e9 量级的荒唐值。
    res.sfc = res.fuel_flow * 3600.0 / max(res.net_thrust, 1e-9) if res.net_thrust > 0 else 0.0
    res.fn_per_mdot = res.net_thrust / max(mdot_air, 1e-6)

    # ---- 名义转速：简单模式没有几何，用"叶尖速度假定"折出一个转速 ----
    res.n1_rpm, res.n2_rpm = _nominal_rpm(sd, Tt0)
    res.spool = {1: res.n1_rpm, 2: res.n2_rpm}

    res.self_sustaining = (res.ok and res.net_thrust > 0.0 and t4_cmd > res.T3)
    return res


def _face_mass_flow(area: float, pt: float, Tt: float, mach: float) -> float:
    """进气流量：按面马赫积分。静止时面马赫取 0.5，等价于"地面试车台吸入量"。"""
    g = gamma_of(Tt)
    tr = 1.0 + 0.5 * (g - 1.0) * mach * mach
    p_stat = pt / tr ** (g / (g - 1.0))
    rho = p_stat / (R_AIR * (Tt / tr))
    return rho * mach * math.sqrt(g * R_AIR * (Tt / tr)) * area


def _nominal_rpm(sd: SimpleDesign, Tt0: float) -> Tuple[float, float]:
    """名义转速：假定设计点叶尖马赫用到技术上限的 95%，折出 N1/N2（rpm）。"""
    tech = sd.tech
    a = a_sound(Tt0)
    u_f = 0.95 * tech.max_tip_mach_fan * a
    u_c = 0.95 * tech.max_tip_mach_comp * a
    d_core = sd.diameter / math.sqrt(1.0 + sd.bpr)
    n1 = 60.0 * u_f / (math.pi * sd.diameter) if sd.diameter > 0 else 0.0
    n2 = 60.0 * u_c / (math.pi * d_core) if d_core > 0 else 0.0
    return n1, n2


# ---------------------------------------------------------------------------
# 对外接口
# ---------------------------------------------------------------------------

def design_point(sd: SimpleDesign) -> SimpleResult:
    c = design_constants(sd)
    if not c.ok:
        r = SimpleResult(alt=sd.design_alt, mach=sd.design_mach, bpr=sd.bpr,
                         opr=sd.opr, fpr=c.fpr_d)
        r.ok = False
        r.errors.extend(c.errors)
        return r
    r = _cycle(sd, sd.design_alt, sd.design_mach, sd.t4, c, ab_scale=1.0)
    r.throttle = 1.0
    return r


def solve(sd: SimpleDesign, alt: float, mach: float, throttle: float,
          ab_fuel_ratio: Optional[float] = None,
          consts: Optional[SimpleConstants] = None) -> SimpleResult:
    """非设计点。只调度三件事：油门 → T4、油门 → 转速（流量与效率）、加力门槛。

    总增压比**不是**调度出来的——它由能量平衡涌现（见 `_cycle`）。
    这样"高空满油门增压比升高""慢车增压比骤降到 3~4"都是算出来的，不是拟合的。
    """
    tech = sd.tech
    thr = min(max(throttle, 0.0), 1.0)
    n_frac = 0.60 + 0.40 * thr

    # 风扇的换算转速上限：物理转速 100% 在 11 km 意味着换算转速 115%，
    # 早就跑出风扇特性线了。FADEC 会按换算值限速，所以高空满油门的物理转速
    # 反而更低——这正是高空推力掉得比 ρ 掉得更快的那一截。
    p_sl, t_amb = isa(alt)
    _pt_in, Tt_in = total_from_static(p_sl, t_amb, mach)
    theta_fan = Tt_in / T0_SL
    n_frac_lim = tech.overspeed_limit * math.sqrt(theta_fan)
    spool_limited = n_frac > n_frac_lim + 1e-6
    if spool_limited:
        n_frac = n_frac_lim

    t4_idle = sd.t4_idle if sd.t4_idle > 0.0 else sd.t4 * 0.55
    # T4 随油门取指数 2 而不是线性：线性时 15% 油门就把 T4 顶到 750 K，
    # 推力直接跳到最大值的 23%，而 J79 在 N2 66% 只有 8% 左右。
    t4_cmd = t4_idle + (sd.t4 - t4_idle) * thr ** T4_THROTTLE_EXP

    if consts is None:
        consts = design_constants(sd)
    if not consts.ok:
        r = SimpleResult(alt=alt, mach=mach, throttle=thr, bpr=sd.bpr,
                         opr=sd.opr, fpr=consts.fpr_d)
        r.ok = False
        r.errors.extend(consts.errors)
        return r

    ab = sd.ab_fuel_ratio if ab_fuel_ratio is None else ab_fuel_ratio
    # 油门在加力门槛以下完全不喷油；越过门槛后在 [门槛, 1] 区间里线性加满
    if ab > 0.0 and sd.ab_throttle < 1.0:
        ab_scale = min(max((thr - sd.ab_throttle) / (1.0 - sd.ab_throttle), 0.0), 1.0)
    else:
        ab_scale = 1.0 if ab > 0.0 else 0.0
    saved = sd.ab_fuel_ratio
    sd.ab_fuel_ratio = ab
    # 涡轮温比不能真的冻结到慢车：冻结时慢车要抽掉全部压头，喷管拿不到压力、
    # 推力会精确掉到 0（这是数学，不是 bug——它说明"恒定温比"这条假设在低功率失效）。
    # 物理原因：涡轮落压比随流量一起退，慢车的膨胀比只有设计值的 1/3~1/2，
    # 所以 TTR 从设计值抬到 0.90（J79 慢车 T4≈950 K / EGT≈750 K 正是 0.79~0.8 量级）。
    ttr_cmd = consts.ttr + (TTR_IDLE - consts.ttr) * (1.0 - thr) ** TTR_IDLE_EXP
    ttr_cmd = max(ttr_cmd, consts.ttr)
    try:
        # 流量律：换算流量 ṁ√θ/δ 与转速成正比。总压总温的影响已经由
        # `_face_mass_flow` 里的 ρ(pt0,Tt0)·a(Tt0) 表达，这里只补转速那一维，
        # 再乘一次换算因子就是把 θ、δ 算了第二遍。
        r = _cycle(sd, alt, mach, t4_cmd, consts, mdot_scale=n_frac,
                   ab_scale=ab_scale, speed_frac=n_frac, ttr=ttr_cmd)
    finally:
        sd.ab_fuel_ratio = saved

    r.throttle = thr
    r.n1_pct = n_frac * 100.0
    r.n2_pct = n_frac * 100.0
    r.n1_rpm *= n_frac
    r.n2_rpm *= n_frac
    if sd.kind == "turbojet":
        # 单轴机只有一根轴，但简单模式内部仍按"风扇+核心"两条流路算，
        # 所以 N1/N2 同步变化。读数照给，但不能让玩家以为这是双轴。
        r.warnings.append("单轴机：N1/N2 为等效读数，实际同轴同步")
    if spool_limited:
        r.warnings.append(
            f"风扇换算转速顶到 {tech.overspeed_limit * 100:.0f}% 上限：物理转速被限到 "
            f"{n_frac * 100:.0f}%（θ={theta_fan:.3f}）")
    if tech and r.n1_pct > tech.overspeed_limit * 100.0:
        r.warnings.append(f"N1 {r.n1_pct:.0f}% 超过超转线")
    return r


# ---------------------------------------------------------------------------
# 自检
# ---------------------------------------------------------------------------

def _demo():
    try:
        import sys
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    cases = [
        ("涡扇 高涵道比", SimpleDesign(name="HBPR", kind="turbofan", diameter=1.7,
                                     bpr=6.0, opr=30.0, t4=1500.0)),
        ("涡扇 低涵道比", SimpleDesign(name="LBPR", kind="turbofan", diameter=1.0,
                                     bpr=0.6, opr=22.0, t4=1500.0)),
        ("涡喷 干态", SimpleDesign(name="TJ", kind="turbojet", diameter=0.74,
                                 bpr=0.0, opr=13.5, t4=1210.0)),
        ("涡喷 + 加力", SimpleDesign(name="TJ-AB", kind="turbojet", diameter=0.74,
                                   bpr=0.0, opr=13.5, t4=1210.0, ab_fuel_ratio=0.03)),
    ]
    print(f"{'型号':<14}{'BPR':>6}{'OPR':>7}{'FPR':>7}{'T3':>7}{'T5':>7}"
          f"{'F kN':>9}{'SFC':>8}{'mdot':>8}{'Vj c/b':>14}{'自持':>6}")
    for label, sd in cases:
        r = design_point(sd)
        if not r.ok:
            print(f"{label:<14} 失败：{' / '.join(r.errors)}")
            continue
        print(f"{label:<14}{r.bpr:>6.2f}{r.opr:>7.2f}{r.fpr:>7.2f}{r.T3:>7.0f}"
              f"{r.T5:>7.0f}{r.net_thrust / 1000:>9.1f}{r.sfc:>8.4f}"
              f"{r.mdot_air:>8.1f}{r.v_jet_core:>7.0f}/{r.v_jet_bypass:<6.0f}"
              f"{'是' if r.self_sustaining else '否':>4}")

    sd = cases[2][1]
    print("\n涡喷加力油门扫描（海平面静止）")
    print(f"{'油门':>5}{'N2%':>6}{'OPR':>7}{'T4':>7}{'T7':>7}{'F kN':>9}{'SFC':>8}")
    for thr in (0.0, 0.3, 0.6, 0.85, 1.0):
        r = solve(sd, 0.0, 0.0, thr)
        print(f"{thr:>5.2f}{r.n2_pct:>6.1f}{r.opr:>7.2f}{r.T4:>7.0f}{r.T7:>7.0f}"
              f"{r.net_thrust / 1000:>9.1f}{r.sfc:>8.4f}")


if __name__ == "__main__":
    _demo()