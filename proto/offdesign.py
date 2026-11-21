"""
JetForge · 非设计点求解（v0.2）
==============================

设计点只回答"这台发动机最擅长在哪个点工作"。真正进游戏的是非设计点：
任意高度、任意马赫、任意油门，推力多少、油耗多少、会不会熄火。

算法结构直接沿用 AJE（`Source/SolverJet.cs`）那条被 KSP 验证过十年的路子，
但把它的黑盒参数换成我们自己的几何量：

1. **固定涡轮温比 TTR**。设计点算出 ``TTR = T5/T4``，非设计点认为涡轮始终
   按这个温比膨胀（导向器壅塞时这是对的：壅塞把流通能力锁死，膨胀比随
   上下游压力自己调整）。
2. **迭代压比求功平衡**。猜一个总压比 prat3 → 算出压气机出口温度 → 烧到
   T4 → 按 TTR 膨胀得到的涡轮功 → 反过来压气机用这点功能压到多高 →
   新的压比。20 次循环内收敛（阈值 0.01），这是 AJE 原版的做法。
3. **流量由涡轮导向器壅塞决定**。``mdot = A_t·p4/sqrt(T4)·sqrt(γ/R)·(...)``。
   于是飞行高度、油门、涡轮喉道面积三者共同决定"喘多大气"。
4. **风扇功按设计点的比例分账**。``fc = W_fan/W_comp``（设计点），非设计点
   认为这个比例不变，于是涡轮功天然按 ``1 : fc`` 分给核心机和风扇。

转速 N1/N2 不是输入，是**解完之后反推**出来的：在几何模型里找到能复现
当前压比的那个转速。这样"N 转速"始终和"玩家拧的几何"自洽。
"""

from __future__ import annotations

import math
import sys
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

from jetforge import (
    CP_AIR, G0, GAMMA_AIR, LHV_JET,
    NPR_CRIT, R_AIR,
    BurnerSpec, CompressorSpec, CycleResult, EngineDesign,
    FanSpec, TechLevel, TurbineSpec,
    a_sound, cp_of, gamma_of, isa, stage_pi, stage_dT_comp, tip_speed,
    total_from_static, design_shaft_speeds, solve_design_point, nozzle_flow,
    burner_performance, intake_tpr,
)

NAC_CD = 0.08   # 短舱外罩型面阻力系数（相对迎风面积）

T_REF = 288.15
P_REF = 101325.0

# 换算参数用的标准状态（ISA 海平面）。换算转速/换算流量都以它为基准。
T0_SL = 288.15
P0_SL = 101325.0
FAR_RICH_LIMIT = 0.070


# ---------------------------------------------------------------------------
# 通流工具
# ---------------------------------------------------------------------------

def choked_mass_flow(area: float, pt: float, Tt: float, gamma: Optional[float] = None) -> float:
    """壅塞喉道质量流量"""
    g = gamma if gamma is not None else gamma_of(Tt)
    if area <= 0 or pt <= 0 or Tt <= 0:
        return 0.0
    return area * pt / math.sqrt(Tt) * math.sqrt(g / R_AIR) * \
        (2.0 / (g + 1.0)) ** ((g + 1.0) / (2.0 * (g - 1.0)))


def nozzle_exit_velocity(pt: float, Tt: float, pb: float, eta_n: float) -> float:
    """收敛喷管出口速度，自动处理壅塞"""
    if pt <= pb or Tt <= 0:
        return 0.0
    g = gamma_of(Tt)
    pr_eff = min(pt / pb, NPR_CRIT)
    dT = Tt * (1.0 - (1.0 / pr_eff) ** ((g - 1.0) / g))
    return math.sqrt(max(2.0 * cp_of(Tt) * dT * eta_n, 0.0))


def annulus_area(tip_diameter: float, hub_tip_ratio: float = 0.75) -> float:
    r_tip = tip_diameter / 2.0
    r_hub = r_tip * hub_tip_ratio
    return math.pi * (r_tip * r_tip - r_hub * r_hub)


def turbine_throat_area(spec: TurbineSpec, hub_tip_ratio: float = 0.75) -> float:
    return annulus_area(spec.tip_diameter, hub_tip_ratio) * 0.8


# ---------------------------------------------------------------------------
# 结果
# ---------------------------------------------------------------------------

@dataclass
class OffDesignResult:
    ok: bool = True
    errors: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)

    alt: float = 0.0
    mach: float = 0.0
    throttle: float = 0.0
    n2_pct: float = 0.0
    n1_pct: float = 0.0

    mdot_air: float = 0.0
    mdot_core: float = 0.0
    bpr: float = 0.0
    opr: float = 0.0
    fpr: float = 1.0
    ttr: float = 1.0

    T1: float = 0.0
    T2: float = 0.0
    T3: float = 0.0
    T4: float = 0.0
    T5: float = 0.0
    P3: float = 0.0
    P5: float = 0.0

    far: float = 0.0
    rpm: Dict[int, float] = field(default_factory=dict)

    thrust: float = 0.0
    thrust_core: float = 0.0
    thrust_bypass: float = 0.0
    ram_drag: float = 0.0
    net_thrust: float = 0.0

    fuel_flow: float = 0.0
    sfc: float = 0.0
    v_jet_core: float = 0.0
    v_jet_bypass: float = 0.0

    W_comp: float = 0.0
    W_fan: float = 0.0

    converged: bool = False
    iterations: int = 0
    choked_turbine: bool = True
    choked_nozzle: bool = False
    self_sustaining: bool = False
    windmill: bool = False
    T7: float = 0.0
    far_ab: float = 0.0
    # 换算转速调节器：true = 这一点的燃油量被转速上限压住了（不是玩家油门不够）
    governed: bool = False
    T4_command: float = 0.0
    n_corrected_limit: float = 1.05
    turbine_nozzle_limited: bool = False
    n_corrected: float = 0.0
    wc_frac: float = 1.0
    fan_speed_limited: bool = False


# ---------------------------------------------------------------------------
# 设计点派生常数（一次算好，反复用）
# ---------------------------------------------------------------------------

@dataclass
class DesignConstants:
    cpr: float = 1.0        # 设计总压比
    ttr: float = 0.85       # 涡轮温比 T5/T4
    fan_work_constant: float = 0.0   # W_fan / W_comp
    a_turb: float = 0.0     # 涡轮导向器喉道面积（设计点反解，保证自洽）
    a_turb_geom: float = 0.0  # 玩家几何给的喉道面积（只做体检用）
    bpr: float = 0.0
    t4_max: float = 1500.0
    t4_idle: float = 1100.0
    rpm_design: Dict[int, float] = field(default_factory=dict)
    # 设计点的换算转速：换算流量能力必须相对它归一化。
    # 设计物理转速是 100%，但它是 288 K 下标定的，压气机进口被风扇加热后
    # 换算转速其实低于 100%——不归一化会让设计点流量对不上。
    nc_design: float = 1.0
    # 设计点吸入流量：非设计点用换算流量律从这里缩放，而不是从涡轮喉道硬算
    mdot_d_air: float = 0.0
    mdot_d_core: float = 0.0
    # 燃烧室的压损与效率必须来自"这台机器自己的形状"（罐式/环形/L/H），
    # 不能再用 tech.burner_dp_ratio：那样设计点罚了、飞行中不罚，同一台机器两套数。
    burner_sigma: float = 0.955
    burner_eta: float = 0.985
    # 设计点反解出的等效核心压气机效率与等效风扇效率。
    # 设计点的压比是逐部件（风扇/LPC/HPC，各带雷诺与间隙修正）算的，
    # 非设计点只有一台等效压气机；不把设计点的等效效率留给它，
    # 就会在机器自己的设计工况上比设计点多出 5~8% 的压比。
    eta_core_design: float = 0.0
    eta_fan_design: float = 0.0
    ok: bool = True
    errors: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)


def derive_design_constants(d: EngineDesign) -> DesignConstants:
    """从设计点抽出非设计点要用的那几个常数。"""
    c = DesignConstants()
    r = solve_design_point(d)
    c.errors = list(r.errors)
    c.ok = r.ok
    c.cpr = max(r.opr, 1.001)
    c.bpr = r.bpr
    c.a_turb = 0.0
    if d.hpt.stages > 0 and r.mdot_core > 0 and r.P3 > 0 and r.T4 > 0:
        # 涡轮导向器喉道面积由设计点流量反解：设计点上"进气道能吃的流量"
        # 必须正好等于"涡轮能过的流量"，两者不能各说各话。
        pt4 = r.P3 * burner_performance(d.burner)[0]
        g = gamma_of(r.T4)
        choke = math.sqrt(g / R_AIR) * (2.0 / (g + 1.0)) ** ((g + 1.0) / (2.0 * (g - 1.0)))
        c.a_turb = r.mdot_core / (pt4 / math.sqrt(r.T4) * choke)
    c.a_turb_geom = turbine_throat_area(d.hpt) if d.hpt.stages > 0 else 0.0
    c.t4_max = min(d.burner.exit_temp, d.tech.t4_limit)
    c.mdot_d_air = r.mdot_air
    c.mdot_d_core = r.mdot_core

    # 涡轮温比
    if r.T4 > 0 and r.T5 > 0:
        c.ttr = max(min(r.T5 / r.T4, 0.98), 0.5)
    # 风扇/核心功比例
    if r.W_comp > 0:
        c.fan_work_constant = max(r.W_fan / r.W_comp, 0.0)
    c.rpm_design = design_shaft_speeds(d, T_REF)

    # 设计点的换算转速（压气机进口温度是风扇出口温度，不是来流温度）
    core_pr_d = r.opr / max(r.fpr, 1.0)
    nc_d = invert_speed(d, core_pr_d, T0_SL, c.rpm_design)
    c.nc_design = nc_d if nc_d > 0.05 else 1.0

    # 燃烧室：压损与效率取"这台机器自己的形状"，与设计点同一把尺子
    bpd = burner_performance(d.burner)
    c.burner_sigma = max(min(bpd[0], 0.99), 0.60)
    c.burner_eta = max(min(bpd[1], 0.999), 0.60)

    # 等效效率：从设计点自己的温度与压比反解，让非设计点在设计工况上不再"多出 5~8% 压比"
    p0d, t0d = isa(d.design_alt)
    tt1d = r.T1 if r.T1 > 1.0 else t0d      # 有马赫时含冲压，不能用静温
    tt2d = r.T2 if r.T2 > 1.0 else tt1d     # 核心压气机入口是风扇出口
    core_pr_d = r.opr / max(r.fpr, 1.0)
    if r.T3 > tt2d * 1.001 and core_pr_d > 1.001:
        g = gamma_of(r.T3)
        eta = math.log(core_pr_d) * (g - 1.0) / g / math.log(r.T3 / tt2d)
        lo_e, hi_e = d.tech.hpc_eta_pol * 0.85, d.tech.hpc_eta_pol * 1.02
        c.eta_core_design = max(min(eta, hi_e), lo_e)
    if r.fpr > 1.001 and r.T2 > tt1d * 1.001:
        g = gamma_of(r.T2)
        eta = math.log(r.fpr) * (g - 1.0) / g / math.log(r.T2 / tt1d)
        lo_e, hi_e = d.tech.fan_eta_pol * 0.85, d.tech.fan_eta_pol * 1.02
        c.eta_fan_design = max(min(eta, hi_e), lo_e)

    # 慢车 T4：不靠拍脑袋的常数，而是反解"海平面静止下 N2 刚好 60%"对应的 T4。
    c.t4_idle = _calibrate_idle(d, c, target_n2=0.60)
    if d.shaft_count >= 3 or (d.ipt.stages > 0):
        c.warnings.append(
            "三轴非设计点按双轴近似：IPT 与中压压气机的功平衡没有单独迭代，"
            "中压转速与设计点保持同比例")
    return c


def _calibrate_idle(d: EngineDesign, c: "DesignConstants", target_n2: float = 0.60) -> float:
    """反解慢车 T4：让慢车转速落在真实发动机的量级（约 60% N2）。

    慢车不是"温度调低一点"，它是"刚好自持且转速最低"的那个点；
    反过来用转速定义慢车，比硬编码 900 K 稳得多。
    """
    r = solve_design_point(d)
    t3_design = r.T3 if r.T3 > 0 else 400.0
    lo = t3_design * 1.02
    hi = c.t4_max * 0.98
    if hi <= lo:
        return c.t4_max * 0.75
    best = lo
    for _ in range(18):
        mid = 0.5 * (lo + hi)
        res = solve_offdesign(d, 0.0, 0.0, 0.0, consts=c, max_iter=30, t4_override=mid)
        n2 = res.n2_pct / 100.0 if res.ok else 0.0
        best = mid
        if n2 < target_n2:
            lo = mid
        else:
            hi = mid
        if abs(hi - lo) < 5.0:
            break
    return 0.5 * (lo + hi)


# ---------------------------------------------------------------------------
# 压比 → 转速 反推（给玩家看 N1/N2）
# ---------------------------------------------------------------------------

def _opr_at_speed(d: EngineDesign, speed_frac: float, Tt_in: float,
                  rpm_design: Optional[Dict[int, float]] = None) -> float:
    """把整台压气机（LPC+HPC）按给定转速比例走一遍，返回总压比。

    转速用设计转速乘比例，而不是"最大叶尖马赫"——设计转速常常是被同一根轴上的
    涡轮叶尖马赫锁住的，直接用压气机自己的马赫上限会算出偏高的转速。
    """
    tech = d.tech
    pt, Tt = 1.0, Tt_in
    rpm_design = rpm_design or {}
    for spec, eta in ((d.lpc, tech.lpc_eta_pol), (d.hpc, tech.hpc_eta_pol)):
        if spec.stages <= 0:
            continue
        U = tip_speed(spec.tip_diameter, rpm_design.get(spec.shaft, 0.0) * speed_frac)
        if U <= 0:
            continue
        psi = tech.compressor_psi * spec.psi_scale
        for _ in range(spec.stages):
            pt *= stage_pi(U, Tt, eta, psi)
            Tt += stage_dT_comp(U, Tt, eta, psi)
    return pt


def _turbine_exit(T4: float, pt4: float, p_amb: float, ttr: float,
                  eta_t: float = 0.88) -> Tuple[float, float, bool]:
    """涡轮出口：固定温比 vs "出口不能低于大气"两种约束取更松的那个。

    返回 (T5, 实际膨胀比 pi_t, 是否被喷管背压限制)。
    低功率时固定温比会让涡轮出口压降到大气以下——那是不可能的，
    实际机器这时喷管不壅塞、涡轮少抽功，T5 反而更高。
    """
    g = gamma_of(T4)
    pi_req = ttr ** (-g / ((g - 1.0) * eta_t))
    pi_max = max(pt4 / max(p_amb, 1.0), 1.0)
    if pi_req <= pi_max:
        return T4 * ttr, pi_req, False
    # 被背压限制：按可用的膨胀比反算温比
    tr = pi_max ** (-(g - 1.0) * eta_t / g)
    return T4 * tr, pi_max, True


def invert_speed(d: EngineDesign, target_opr: float, Tt_in: float,
                 rpm_design: Optional[Dict[int, float]] = None) -> float:
    """二分找到产生目标压比的转速比例。"""
    if target_opr <= 1.001:
        return 0.0
    lo, hi = 0.05, 1.45
    for _ in range(44):
        mid = 0.5 * (lo + hi)
        if _opr_at_speed(d, mid, Tt_in, rpm_design) < target_opr:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


# ---------------------------------------------------------------------------
# 换算转速 / 换算流量：压气机特性线的两条硬约束
# ---------------------------------------------------------------------------

def opr_at_corrected_speed(d: EngineDesign, nc: float,
                           rpm_design: Optional[Dict[int, float]] = None) -> float:
    """在标准温度下按给定换算转速把压气机走一遍，返回核心压比（LPC×HPC）。

    级压比只取决于 ψU²/(cp·T)，所以 U/√T 才是相似参数——这就是换算转速。
    在 T_ref 下评估，等于把"高空冷进气、同转速反而压得更高"这件事自动含进去，
    也正好给出"换算转速顶到上限时压比最高能到多少"。
    """
    return _opr_at_speed(d, nc, T0_SL, rpm_design)


def wc_capacity_frac(nc: float) -> float:
    """压气机的换算流量能力（相对设计点），特性线的一个粗拟合。

    低于设计转速取**线性**：同一圈通道面积、轴向速度比 Ca/U 基本不变时，
    ṁ = ρ·A·Ca ∝ Ca ∝ U ∝ N。原来写的是 nc²，等于让 90% 转速只吃 81% 流量；
    高空时换算转速从 0.93 抬到 1.05，就凭空多出 16% 流量（详见 phase2-mod §23）。
    线性律下慢车进气占满油门 38%（真机 30~40%），仍然对得上。
    超过设计转速后靠压气机壅塞，增益很有限。
    """
    if nc <= 0.0:
        return 0.0
    if nc <= 1.0:
        return max(nc, 0.02)
    return min(1.0 + 0.55 * (nc - 1.0), 1.12)


# ---------------------------------------------------------------------------
# 主求解器
# ---------------------------------------------------------------------------

def solve_offdesign(
    d: EngineDesign,
    alt: float,
    mach: float,
    throttle: float,
    ab_frac: float = 0.0,
    consts: Optional[DesignConstants] = None,
    *,
    max_iter: int = 40,
    t4_override: Optional[float] = None,
    ab_fuel_ratio: float = 0.0,
) -> OffDesignResult:
    """
    任意飞行状态下的稳态性能。

    throttle: 0..1 油门杆位置 → 燃烧室出口温度指令（FADEC 的调度）
    ab_frac:  0..1 加力段温度插值（油门式驱动，给 KSP 用）
    ab_fuel_ratio: far_ab，加力喷油量（设计器式驱动，玩家填的就是这个；>0 时覆盖 ab_frac）
    t4_override: 直接指定 T4，绕过油门映射（用于标定慢车、做等温线扫描）
    """
    res = OffDesignResult(alt=alt, mach=mach, throttle=throttle)
    tech = d.tech
    if consts is None:
        consts = derive_design_constants(d)
    if not consts.ok:
        res.ok = False
        res.errors = list(consts.errors)
        return res
    if consts.a_turb <= 0:
        res.ok = False
        res.errors.append("没有高压涡轮：核心机没有功率来源")
        return res

    thr = min(max(throttle, 0.0), 1.0)
    res.ttr = consts.ttr

    # ---- 来流 ----
    p0, T0 = isa(alt)
    if mach > 0:
        pt0, Tt0 = total_from_static(p0, T0, mach)
        tpr = intake_tpr(tech, mach)
    else:
        pt0, Tt0 = p0, T0
        tpr = tech.intake_tpr_sub
    pt1, Tt1 = pt0 * tpr, Tt0
    V0 = mach * a_sound(T0)
    res.T1 = Tt1

    # ---- 油门 → T4 指令 ----
    if t4_override is not None:
        t4_cmd = float(t4_override)
    else:
        t4_cmd = consts.t4_idle + thr * (consts.t4_max - consts.t4_idle)

    # ---- 压比迭代（AJE 结构）：给定 T4，求"涡轮功刚好养活压气机"的压比 ----
    # 抽成闭包是因为后面转速调节器要反复调 T4 再解一遍（二分）。
    eta_c = consts.eta_core_design if consts.eta_core_design > 0.0 else tech.hpc_eta_pol
    # 风扇效率同样取设计点反解的等效值：原来直接吃 tech.fan_eta_pol，
    # 与设计点里带雷诺/间隙修正的口径不同，等于同一台机器两套数。
    _eta_fan = consts.eta_fan_design if consts.eta_fan_design > 0.0 else tech.fan_eta_pol

    def _eta_f() -> float:
        return _eta_fan


    def _fpr_of(prat3_v: float, w_t_v: float) -> float:
        """由风扇功反推风扇压比（涡喷恒为 1）。"""
        if consts.bpr <= 0.0 or consts.fan_work_constant <= 0.0:
            return 1.0
        w_fan_total = w_t_v * consts.fan_work_constant / (1.0 + consts.fan_work_constant)
        dT_fan = w_fan_total / ((1.0 + consts.bpr) * cp_of(Tt1))
        if dT_fan <= 0.0:
            return 1.0
        gf = gamma_of(Tt1)
        return (1.0 + dT_fan / Tt1) ** (gf * _eta_f() / (gf - 1.0))

    def _balance(t4_in: float, prat3_guess: float, iters: int):
        """给定燃烧室出口温度，迭代出压比。

        返回 (prat3, T3, T4, far, w_t, pi_t_used, turbine_limited, converged, iters)；
        能量不守恒时返回 None。
        """
        prat3_l = max(prat3_guess, 1.001)
        T3_l, T4_l, far_l = Tt1, t4_in, 0.0
        w_t_l, pi_t_l, lim_l, conv_l, it_l = 0.0, 1.0, False, False, 0
        for it_l in range(1, iters + 1):
            g1 = gamma_of(Tt1)
            T3_l = Tt1 * prat3_l ** ((g1 - 1.0) / g1 / eta_c)
            pt4_l = pt1 * prat3_l * consts.burner_sigma
            dT_burn = t4_in - T3_l
            if dT_burn <= 0.0:
                T4_l, far_l = T3_l, 0.0
            else:
                cp3, cp4 = cp_of(T3_l), cp_of(t4_in)
                den = LHV_JET * consts.burner_eta - cp4 * dT_burn
                if den <= 0.0:
                    return None
                far_l = min(cp3 * dT_burn / den, FAR_RICH_LIMIT)
                T4_l = t4_in
            T5_l, pi_t_l, lim_l = _turbine_exit(T4_l, pt4_l, p0, consts.ttr, tech.hpt_eta_pol)
            w_t_l = cp_of(T4_l) * (T4_l - T5_l)
            w_comp = w_t_l / (1.0 + consts.fan_work_constant)
            w_fan = w_t_l - w_comp
            dT_fan = w_fan / ((1.0 + consts.bpr) * cp_of(Tt1))
            T3_new = Tt1 + dT_fan + w_comp / cp_of(Tt1)
            prat3_new = 1.0 if T3_new <= Tt1 else (T3_new / Tt1) ** (g1 * eta_c / (g1 - 1.0))
            if abs(prat3_new - prat3_l) < 0.01 * max(prat3_l, 1.0):
                prat3_l, conv_l = prat3_new, True
                break
            prat3_l = 0.5 * (prat3_l + prat3_new) if it_l > 1 else prat3_new
        return prat3_l, T3_l, T4_l, far_l, w_t_l, pi_t_l, lim_l, conv_l, it_l

    t4_cmd_raw = t4_cmd
    sol = _balance(t4_cmd, consts.cpr, max_iter)
    if sol is None:
        res.ok = False
        res.errors.append("燃烧室温升过高，能量不守恒")
        return res
    prat3, T3, T4, far, w_t, pi_t_used, turbine_limited, converged, it = sol

    # ---- 换算转速约束：真实发动机的高空推力衰减与高空富油限制都出在这里 ----
    # 级压比只取决于 ψU²/(cp·T)，所以 U/√T 是相似参数——这就是换算转速。
    # 用 T_ref 评估压气机得到的就是"换算转速下的压比"，与物理转速解耦。
    theta = Tt1 / T0_SL
    sqrt_theta = math.sqrt(max(theta, 1e-3))

    def _tt2_of(s) -> float:
        """核心压气机进口总温（风扇加热之后）——换算转速的基准温度。"""
        if consts.bpr <= 0.0 or consts.fan_work_constant <= 0.0:
            return Tt1
        w_fan_v = s[4] * consts.fan_work_constant / (1.0 + consts.fan_work_constant)
        return Tt1 + max(w_fan_v, 0.0) / ((1.0 + consts.bpr) * cp_of(Tt1))

    def _core_pr_of(s) -> float:
        return s[0] / max(_fpr_of(s[0], s[4]), 1.0)

    def _speed_state(s):
        """返回 (换算转速 nc, 物理转速 nphys, 换算转速上限 nc_limit)。

        两条上限都要查：换算转速顶破特性线（气动），物理转速顶破红线（离心应力）。
        """
        tt2 = _tt2_of(s)
        th = tt2 / T0_SL
        nc_v = invert_speed(d, _core_pr_of(s), T0_SL, consts.rpm_design)
        return nc_v, nc_v * math.sqrt(max(th, 1e-3)), min(
            tech.overspeed_limit, tech.overspeed_limit / math.sqrt(max(th, 1e-3)))

    nc, nphys, nc_limit = _speed_state(sol)
    res.n_corrected_limit = nc_limit
    if nc > nc_limit * 1.002:
        # 转速调节器把油收回来：压比锁在特性线上限，反解需要的 T4。
        # 这是真实发动机在高空"推不满油门"的物理原因，不是把推力硬砍一刀。
        opr_lim_core = opr_at_corrected_speed(d, nc_limit, consts.rpm_design)
        lo, hi = T3 * 1.001, t4_cmd
        guess = opr_lim_core * max(_fpr_of(sol[0], sol[4]), 1.0)
        best = sol
        for _ in range(22):
            mid = 0.5 * (lo + hi)
            s = _balance(mid, guess, max_iter)
            if s is None:
                hi = mid
                continue
            best = s
            guess = s[0]
            if _core_pr_of(s) > opr_lim_core:
                hi = mid
            else:
                lo = mid
        sol = best if best is not None else sol
        prat3, T3, T4, far, w_t, pi_t_used, turbine_limited, converged, it = sol
        res.governed = True
        res.T4_command = t4_cmd_raw
        res.warnings.append(
            f"换算转速顶到 {nc_limit*100:.0f}% 上限：燃油量被转速调节器压到 T4={T4:.0f}K"
            f"（指令 {t4_cmd_raw:.0f}K），高空/大马赫下推不满")
        nc, nphys, nc_limit = _speed_state(sol)

    res.iterations, res.converged = it, converged
    if not converged:
        res.warnings.append(f"压比迭代 {it} 次未收敛（prat3={prat3:.2f}）")

    if prat3 <= 1.001:
        res.ok = False
        res.windmill = True
        res.errors.append("压比降到 1 以下：这个油门点核心机转不起来（风车状态）")
        return res

    P3 = pt1 * prat3
    pt4 = P3 * consts.burner_sigma
    res.ttr = (T4 - w_t / max(cp_of(T4), 1e-6)) / T4 if T4 > 0 else consts.ttr
    res.opr, res.T3, res.P3 = prat3, T3, P3
    res.T4, res.far = T4, far
    res.T5 = T4 - w_t / max(cp_of(T4), 1e-6)

    # 富油：先按未截断的油气比算，超了才报
    if T4 > T3 + 1.0:
        _cp3, _cp4 = cp_of(T3), cp_of(T4)
        _dT = T4 - T3
        _den = LHV_JET * consts.burner_eta - _cp4 * _dT
        if _den > 0:
            far_raw = _cp3 * _dT / _den
            if far_raw > FAR_RICH_LIMIT:
                res.warnings.append(
                    f"油气比 f={far_raw:.3f} 超过可燃上限 {FAR_RICH_LIMIT:.3f}："
                    f"这段温升实际烧不出来，T4 被截断")
                res.ok = False

    # ---- 流量：换算流量律（压气机能吞多少），涡轮导向器只当上限 ----
    # 以前这里直接用涡轮壅塞算流量，高空会算偏大：压比随高度升上去，涡轮"看起来能过"更多气，
    # 但压气机在 100% 换算转速上根本没那个流通能力。真实的高空推力衰减是 δ 掉下去带来的，
    # 所以主律必须是 ṁ = ṁ_d · (δ/√θ) · Wc(nc)。
    delta = pt1 / P0_SL
    # 换算流量能力相对设计点归一化：设计点必须严格回到 mdot_d
    wc_frac = wc_capacity_frac(nc) / max(wc_capacity_frac(consts.nc_design), 1e-3)
    mdot_air_corr = consts.mdot_d_air * (delta / sqrt_theta) * wc_frac
    if mdot_air_corr <= 0.0:
        res.ok = False
        res.errors.append("换算流量为零：进不去气")
        return res
    bpr = consts.bpr
    mdot_core = mdot_air_corr / (1.0 + bpr)
    mdot_core_choke = choked_mass_flow(consts.a_turb, pt4, T4)
    if mdot_core_choke > 0.0 and mdot_core > mdot_core_choke:
        # 涡轮导向器壅塞：喉道面积不够，气流过不去。
        # 注意这是常态（导向器几乎总在壅塞），只在"卡得离谱"时才值得警告。
        ratio = mdot_core / mdot_core_choke
        mdot_core = mdot_core_choke
        mdot_air_corr = mdot_core * (1.0 + bpr)
        res.choked_turbine = True
        if ratio > 1.25:
            res.warnings.append(
                f"涡轮导向器严重限流：压气机能吞 {mdot_air_corr/(1+bpr)*ratio:.1f} kg/s，"
                f"喉道只过 {mdot_core_choke:.1f} kg/s（面积偏小 {ratio:.1f}×）")
    mdot_air = mdot_air_corr
    if mdot_core <= 0:
        res.ok = False
        res.errors.append("涡轮流量为零")
        return res
    res.mdot_core, res.mdot_air, res.bpr = mdot_core, mdot_air, bpr
    res.wc_frac = wc_frac

    # ---- 风扇 ----
    fpr = 1.0
    Tt2 = Tt1
    pt2 = pt1
    if bpr > 0:
        w_fan_total = w_t * consts.fan_work_constant / (1.0 + consts.fan_work_constant)
        # w_fan_total 是"每 kg 核心空气"分到的风扇功，折到每 kg 总空气要除以 (1+BPR)
        dT_fan = w_fan_total / ((1.0 + bpr) * cp_of(Tt1))
        Tt2 = Tt1 + dT_fan
        gf = gamma_of(Tt1)
        fpr = (Tt2 / Tt1) ** (gf * _eta_f() / (gf - 1.0))
        # 风扇也要查换算转速：同样的物理转速，到了 11 km 换算转速会顶破风扇特性线。
        # 顶破了就只能少压（fpr 被夹住），旁路射流速度随之下降——这才是高空推力
        # 掉得比"进气量按 δ 掉"更快的原因。
        fan_rpm_d = consts.rpm_design.get(d.fan.shaft, 0.0)
        if fan_rpm_d > 0.0 and d.fan.diameter > 0.0:
            n1_lim = tech.overspeed_limit * sqrt_theta
            u_lim_fan = math.pi * d.fan.diameter * fan_rpm_d * n1_lim / 60.0
            fpr_max = (1.0 + tech.fan_psi * d.fan.stages * u_lim_fan * u_lim_fan
                       / (cp_of(Tt1) * Tt1)) ** (gf * _eta_f() / (gf - 1.0))
            if fpr > fpr_max:
                fpr = max(fpr_max, 1.0)
                Tt2 = Tt1 * fpr ** ((gf - 1.0) / (gf * _eta_f()))
                res.fan_speed_limited = True
                res.warnings.append(
                    f"风扇换算转速顶到 {tech.overspeed_limit*100:.0f}% 上限："
                    f"风扇压比被夹到 {fpr:.2f}，旁路射流速度下降")
        pt2 = pt1 * fpr
    res.fpr, res.T2 = fpr, Tt2

    # ---- 功率（绝对值，MW 级） ----
    res.W_comp = mdot_core * cp_of(Tt1) * (T3 - Tt1)
    res.W_fan = mdot_air * cp_of(Tt1) * (Tt2 - Tt1)

    # ---- 加力 ----
    # 两种驱动方式，语义必须分清：
    #   ab_frac      = 油门式（0..1 向"加力最高温"插值），给 KSP 油门映射用
    #   ab_fuel_ratio = 喷油量式（far_ab，相对主流空气），给设计器 UI 用——玩家填的就是这个
    T5 = res.T5
    t7 = T5
    fuel_ab = 0.0
    far_ab_used = 0.0
    if d.afterburner.enabled and (ab_frac > 0.0 or ab_fuel_ratio > 0.0):
        # 与 jetforge._ab_max_temp 保持一致：加力段没有涡轮叶片，可比 T4 热得多
        t_max = min(max(tech.t4_limit + 400.0, 1800.0), 2100.0)
        if d.afterburner.exit_temp:
            t_max = min(t_max, d.afterburner.exit_temp)
        if ab_fuel_ratio > 0.0:
            far_ab = min(ab_fuel_ratio, 0.045)
            eta_ab = 0.95
            t7 = T5 + far_ab * LHV_JET * eta_ab / cp_of(T5) / (1.0 + far_ab)
            t7 = min(t7, t_max)
        else:
            t7 = T5 + min(max(ab_frac, 0.0), 1.0) * (t_max - T5)
        dT7 = t7 - T5
        if dT7 > 0:
            den = LHV_JET * 0.95 - cp_of(t7) * dT7
            if den > 0:
                far_ab_used = cp_of(T5) * dT7 / den
                fuel_ab = mdot_core * (1.0 + far) * far_ab_used
    res.far_ab = far_ab_used
    res.T7 = t7
    res.fuel_flow = mdot_core * far + fuel_ab

    # ---- 喷管与推力 ----
    P5 = pt4 / max(pi_t_used, 1.0)
    res.P5 = P5
    res.turbine_nozzle_limited = turbine_limited
    res.choked_nozzle = (P5 / max(p0, 1e-3)) >= NPR_CRIT

    # 喷管用 nozzle_flow：含壅塞时的压力推力项 (p_e−p_a)·A_e
    m_core_exh = mdot_core * (1.0 + far)
    v_core, _ae_c, _pe_c, f_core_gross, choked_c = nozzle_flow(
        m_core_exh, P5, t7, p0, tech.nozzle_eta)
    f_core = f_core_gross - mdot_core * V0
    res.choked_nozzle = choked_c
    v_byp, f_byp = 0.0, 0.0
    if bpr > 0:
        m_byp = mdot_air * (bpr / (1.0 + bpr))
        v_byp, _ae_b, _pe_b, f_byp_gross, _cb = nozzle_flow(
            m_byp, pt2, Tt2, p0, tech.nozzle_eta)
        f_byp = f_byp_gross - m_byp * V0

    res.v_jet_core, res.v_jet_bypass = v_core, v_byp
    res.thrust_core, res.thrust_bypass = f_core, f_byp
    res.thrust = f_core + f_byp
    # 冲压动量项已经在 Σṁ(Vj−V0) 里扣过一次，这里只补短舱外罩的型面阻力。
    # 以前这里写 0.15·ṁ·V0，等于把冲压阻力扣了两遍，高空大马赫下净推力被吃掉一大截。
    d_front = d.fan.diameter if d.fan.diameter > 0 else d.inlet_diameter
    a_front = math.pi * (d_front / 2.0) ** 2
    res.ram_drag = 0.5 * (p0 / (R_AIR * T0)) * V0 * V0 * a_front * NAC_CD
    res.net_thrust = res.thrust - res.ram_drag
    # 净推力 <= 0（风车、或冲压阻力吃掉全部推力）时 SFC 没有定义：给 0 而不是 inf。
    # inf 会顺着 realIsp = 3600/(sfc*g0) 变成 0，也会污染网格的 JSON/CSV。
    res.sfc = res.fuel_flow * 3600.0 / max(abs(res.net_thrust), 1e-9) if res.net_thrust > 0 else 0.0

    # ---- 转速（解完再反推）：物理转速 = 换算转速 × √θ（θ 取核心压气机进口温度） ----
    core_shaft = d.hpc.shaft if d.hpc.stages > 0 else (
        d.lpc.shaft if d.lpc.stages > 0 else 2)
    res.n_corrected = nc
    res.n2_pct = 100.0 * nphys
    res.n1_pct = 100.0 * nphys
    res.rpm = {}
    for s, n in consts.rpm_design.items():
        res.rpm[s] = n * nphys
    if bpr > 0 and d.fan.diameter > 0:
        # 风扇：由 FPR 反推叶尖马赫（同样是换算口径，但要换算回物理转速显示）
        g = gamma_of(Tt1)
        U_fan = math.sqrt(max((fpr ** ((g - 1.0) / (g * _eta_f())) - 1.0)
                              * cp_of(Tt1) * Tt1 / (tech.fan_psi * d.fan.stages), 0.0))
        n_fan = U_fan * 60.0 / (math.pi * d.fan.diameter) if d.fan.diameter > 0 else 0.0
        res.rpm[d.fan.shaft] = n_fan
        res.n1_pct = 100.0 * n_fan / max(consts.rpm_design.get(d.fan.shaft, 1.0), 1e-6)

    # ---- 判据 ----
    if T4 > tech.t4_limit + 1.0:
        res.errors.append(f"需要 T4={T4:.0f}K，超过材料上限 {tech.t4_limit:.0f}K")
        res.ok = False
    if P5 < p0 * 0.99:
        res.warnings.append(f"涡轮出口总压 {P5/1000:.1f} kPa 低于环境 {p0/1000:.1f} kPa：喷管倒流，该油门点维持不住")
        res.ok = False
    elif P5 < p0:
        # 落在环境压 1% 以内＝喷管未壅塞、出口就是大气压，物理上允许
        P5 = p0
        res.P5 = P5
        _v, _a, _p, f_core_gross, _ch = nozzle_flow(
            m_core_exh, P5, t7, p0, tech.nozzle_eta)
        v_core = _v
        f_core = f_core_gross - mdot_core * V0
        res.v_jet_core, res.thrust_core = v_core, f_core
        res.thrust = f_core + f_byp
    if res.net_thrust < 0:
        res.warnings.append("净推力为负：进气道冲压阻力超过可用推力")

    res.self_sustaining = (res.ok and converged and res.net_thrust > 0
                           and T4 <= tech.t4_limit and not res.windmill)
    return res


# ---------------------------------------------------------------------------
# 油门扫描 / 包线
# ---------------------------------------------------------------------------

def sweep_throttle(d: EngineDesign, alt: float, mach: float,
                   n: int = 6, consts: Optional[DesignConstants] = None) -> List[OffDesignResult]:
    if consts is None:
        consts = derive_design_constants(d)
    return [solve_offdesign(d, alt, mach, i / max(n - 1, 1), consts=consts) for i in range(n)]


def sweep_altitude(d: EngineDesign, mach: float, throttle: float,
                   alts: Optional[List[float]] = None,
                   consts: Optional[DesignConstants] = None) -> List[OffDesignResult]:
    if consts is None:
        consts = derive_design_constants(d)
    if alts is None:
        alts = [0, 3000, 6000, 9000, 12000, 15000, 18000]
    return [solve_offdesign(d, h, mach, throttle, consts=consts) for h in alts]


# ---------------------------------------------------------------------------
# 自检
# ---------------------------------------------------------------------------

def _demo():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    from jetforge import TECH_PRESETS

    d = EngineDesign(
        name="Demo 2-spool turbofan",
        kind="turbofan",
        tech=TECH_PRESETS["1970s"],
        inlet_diameter=1.0,
        fan=FanSpec(diameter=1.2, stages=1, hub_tip_ratio=0.42),
        lpc=CompressorSpec(stages=2, tip_diameter=0.62, shaft=1),
        hpc=CompressorSpec(stages=8, tip_diameter=0.50, shaft=2),
        hpt=TurbineSpec(stages=1, tip_diameter=0.55, shaft=2),
        lpt=TurbineSpec(stages=3, tip_diameter=0.62, shaft=1),
        burner=BurnerSpec(exit_temp=1500.0),
        shaft_count=2,
    )
    c = derive_design_constants(d)
    print(f"设计常数: CPR={c.cpr:.2f} TTR={c.ttr:.3f} fc={c.fan_work_constant:.2f} "
          f"A_turb={c.a_turb:.4f}m² BPR={c.bpr:.2f} T4:[{c.t4_idle:.0f},{c.t4_max:.0f}]")
    print()
    print(f"{'高度':>6} {'马赫':>5} {'油门':>5} {'N2%':>6} {'N1%':>6} {'Nc%':>6} {'mdot':>7} "
          f"{'OPR':>6} {'T4':>6} {'F':>9} {'SFC':>8} 状态")
    print("-" * 104)
    for alt, mach in [(0, 0.0), (0, 0.5), (6000, 0.6), (11000, 0.85)]:
        for thr in (0.0, 0.3, 0.6, 0.85, 1.0):
            r = solve_offdesign(d, alt, mach, thr, consts=c)
            flag = "OK" if r.ok else "ERR"
            sfc = f"{r.sfc:8.4f}" if r.sfc < 100 else "     inf"
            gov = " [转速受限]" if r.governed else ""
            print(f"{alt:>6.0f} {mach:>5.2f} {thr:>5.2f} {r.n2_pct:>6.1f} {r.n1_pct:>6.1f} "
                  f"{100*r.n_corrected:>6.1f} "
                  f"{r.mdot_air:>7.1f} {r.opr:>6.2f} {r.T4:>6.0f} "
                  f"{r.net_thrust/1000:>8.2f}k {sfc} {flag}"
                  + gov
                  + ("  " + r.errors[0] if r.errors else "")
                  + ("  " + r.warnings[0] if r.warnings else ""))
        print()


if __name__ == "__main__":
    _demo()