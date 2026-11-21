"""
JetForge · 参数化喷气发动机性能模型（参考实现 v0.2）
=====================================================

把"玩家在编辑器里拧的那几个旋钮"翻译成一台发动机的真实性能。

设计哲学
--------
1. **只做一维、准稳态**。KSP 物理帧 0.02 s，一维稳态足够，而且每一步都能向玩家解释。
2. **几何直接参与计算**。叶尖速度 U = π·D·n/60 有上限 → 直径锁死转速 → 转速×载荷系数
   锁死每级压比 → 级数锁死总压比。这也解释了为什么"再加一级"到后面会失效：
   出口温度升高，同样的叶尖功只能换来越来越小的压比。
3. **功平衡是主轴**。每个轴的涡轮必须恰好提起挂在这根轴上的所有压气机/风扇。
   涡轮落压比不是设计参数，它是功平衡的结果。
4. **"能不能自持"拆成可判定的几条**：涡轮级数够不够吃掉所需的落压比、
   T4 有没有超材料上限、喷管压比够不够、流量是否壅塞。

单位：m, m², K, Pa, kg/s, N, W。
"""

from __future__ import annotations

import math
import sys
from dataclasses import dataclass, field
from typing import List, Optional, Dict, Tuple

R_AIR = 287.05
GAMMA_AIR = 1.4
CP_AIR = 1004.5
G0 = 9.80665
LHV_JET = 43.0e6          # 航空煤油低位热值 J/kg
NPR_CRIT = ((GAMMA_AIR + 1.0) / 2.0) ** (GAMMA_AIR / (GAMMA_AIR - 1.0))  # ≈1.893


# ---------------------------------------------------------------------------
# 1. 气体模型 / 大气
# ---------------------------------------------------------------------------

def gamma_of(T: float, far: float = 0.0) -> float:
    g = GAMMA_AIR - 0.1 * max((T - 300.0) * 0.0005, 0.0) * (1.0 + far)
    return min(GAMMA_AIR, max(1.1, g))


def cp_of(T: float, far: float = 0.0) -> float:
    cp = CP_AIR + 250.0 * max((T - 300.0) * 0.0005, 0.0) * (1.0 + 10.0 * far)
    return min(1404.5, max(CP_AIR, cp))


def isa(alt_m: float) -> Tuple[float, float]:
    if alt_m < 11000.0:
        T = 288.15 - 0.0065 * alt_m
        p = 101325.0 * (T / 288.15) ** (G0 / (0.0065 * R_AIR))
    else:
        T = 216.65
        p = 22632.06 * math.exp(-G0 * (alt_m - 11000.0) / (R_AIR * T))
    return p, T


def a_sound(T: float) -> float:
    return math.sqrt(GAMMA_AIR * R_AIR * T)


def total_from_static(p: float, T: float, mach: float) -> Tuple[float, float]:
    Tt = T * (1.0 + 0.5 * (GAMMA_AIR - 1.0) * mach * mach)
    pt = p * (Tt / T) ** (GAMMA_AIR / (GAMMA_AIR - 1.0))
    return pt, Tt


# ---------------------------------------------------------------------------
# 2. 技术代际
# ---------------------------------------------------------------------------

@dataclass
class TechLevel:
    name: str = "1970s"
    fan_eta_pol: float = 0.88
    lpc_eta_pol: float = 0.89
    hpc_eta_pol: float = 0.90
    hpt_eta_pol: float = 0.86
    lpt_eta_pol: float = 0.89
    burner_eta: float = 0.99
    burner_dp_ratio: float = 0.04
    nozzle_eta: float = 0.975
    intake_tpr_sub: float = 0.99
    intake_tpr_super: float = 0.93

    # 叶尖马赫限制（同时也是"直径→转速"的锁）
    max_tip_mach_fan: float = 1.25
    max_tip_mach_comp: float = 1.05
    max_tip_mach_turb: float = 0.95

    # 级功系数（Δh0 = ψ·U²）
    fan_psi: float = 0.26
    compressor_psi: float = 0.38
    turbine_psi: float = 2.4

    # 涡轮单级最大落压比（结构/气动极限）
    turb_stage_pr_max: float = 2.4

    t4_limit: float = 1600.0
    mech_efficiency: float = 0.99

    # ---- 尺度效应：同样的技术，机器做小了效率就是要差 ----
    # 叶尖间隙有制造下限，不会随叶高成比例缩小，所以 clearance/span 在小机器上更大。
    clearance_m: float = 0.00035        # 能保证的最小径向间隙（米）
    clearance_ref_frac: float = 0.008   # 大机器典型的间隙/叶高，低于它不罚
    clearance_sens: float = 1.30        # 间隙占比每多 1%，效率掉几个百分点
    re_ref: float = 3.0e5               # 叶片弦长雷诺数参考值
    re_exponent: float = 0.20           # 损失 ∝ Re^-n 的 n

    # ---- 判据参数（criteria.py 用；不参与循环求解） ----
    stall_phi: float = 0.38             # 轴流压气机级失速边界流量系数 φ = Ca/U
    stall_phi_fan: float = 0.32         # 风扇单级弦长大、稠度高，失速 φ 比压气机更低
    phi_high_comp: float = 0.75         # φ 偏大的告警线（轴向速度过高/流道偏窄）
    phi_high_fan: float = 0.70
    surge_margin_req: float = 0.15      # 要求的最小喘振裕度（军用 15%，民用取 20%）
    overspeed_limit: float = 1.05       # 转速上限（额定 100%）
    t3_limit: float = 900.0             # 压气机出口温度上限（盘/叶片材料）
    egt_ratio: float = 0.68             # 排气温度上限 = egt_ratio × t4_limit
    far_lbo: float = 0.0045             # 贫油熄火油气比
    far_rich: float = 0.070             # 富油熄火/冒烟油气比
    t4_uncooled_limit: float = 1250.0   # 无冷却涡轮叶片的温度上限
    nozzle_npr_fixed_max: float = 6.0   # 固定收敛喷管超过这个压比，欠膨胀损失明显


TECH_PRESETS: Dict[str, TechLevel] = {
    "1940s": TechLevel("1940s", fan_eta_pol=0.80, lpc_eta_pol=0.80, hpc_eta_pol=0.80,
                       hpt_eta_pol=0.78, lpt_eta_pol=0.80, burner_eta=0.96,
                       burner_dp_ratio=0.08, nozzle_eta=0.95,
                       max_tip_mach_fan=0.90, max_tip_mach_comp=0.90, max_tip_mach_turb=0.85,
                       fan_psi=0.20, compressor_psi=0.28, turbine_psi=1.4,
                       turb_stage_pr_max=1.8, t4_limit=1110.0, mech_efficiency=0.97),
    "1960s": TechLevel("1960s", fan_eta_pol=0.84, lpc_eta_pol=0.85, hpc_eta_pol=0.86,
                       hpt_eta_pol=0.82, lpt_eta_pol=0.85, burner_eta=0.98,
                       burner_dp_ratio=0.06, nozzle_eta=0.96,
                       max_tip_mach_fan=1.05, max_tip_mach_comp=1.00, max_tip_mach_turb=0.90,
                       fan_psi=0.22, compressor_psi=0.32, turbine_psi=1.9,
                       turb_stage_pr_max=2.1, t4_limit=1390.0, mech_efficiency=0.98),
    "1970s": TechLevel("1970s"),
    "1990s": TechLevel("1990s", fan_eta_pol=0.90, lpc_eta_pol=0.91, hpc_eta_pol=0.92,
                       hpt_eta_pol=0.88, lpt_eta_pol=0.91, burner_eta=0.995,
                       burner_dp_ratio=0.04, nozzle_eta=0.98,
                       max_tip_mach_fan=1.30, max_tip_mach_comp=1.10, max_tip_mach_turb=0.95,
                       fan_psi=0.28, compressor_psi=0.42, turbine_psi=3.0,
                       turb_stage_pr_max=2.7, t4_limit=1780.0, mech_efficiency=0.99),
    "2010s": TechLevel("2010s", fan_eta_pol=0.92, lpc_eta_pol=0.92, hpc_eta_pol=0.93,
                       hpt_eta_pol=0.89, lpt_eta_pol=0.92, burner_eta=0.995,
                       burner_dp_ratio=0.035, nozzle_eta=0.985,
                       max_tip_mach_fan=1.35, max_tip_mach_comp=1.15, max_tip_mach_turb=0.98,
                       fan_psi=0.30, compressor_psi=0.45, turbine_psi=3.4,
                       turb_stage_pr_max=3.0, t4_limit=2000.0, mech_efficiency=0.995),
}


def intake_tpr(tech: TechLevel, mach: float) -> float:
    """进气道总压恢复系数（随马赫连续）。

    原来三个调用点都写成 `super if mach > 1.3 else sub`——那是个台阶：M1.28→1.32 之间
    总压凭空少 6%，进气量和推力跟着掉 24%，而这一段在物理上是平滑的。现实里亚声速
    进气道几乎无损失，超声速要靠激波系减速，恢复系数随马赫平缓劣化（MIL-E-5008B，
    η = 1 − 0.075(M−1)^1.35）。这里用同一幂次把两端接起来：M≤1 取亚声速值，
    M≥2 取超声速值，中间平滑过渡。
    """
    if mach <= 1.0:
        return tech.intake_tpr_sub
    x = mach - 1.0
    if x >= 1.0:
        return tech.intake_tpr_super
    return tech.intake_tpr_sub - (tech.intake_tpr_sub - tech.intake_tpr_super) * (x ** 1.35)


# ---------------------------------------------------------------------------
# 3. 设计输入
# ---------------------------------------------------------------------------

@dataclass
class CompressorSpec:
    stages: int = 1
    tip_diameter: float = 0.4
    psi_scale: float = 1.0
    shaft: int = 1
    hub_tip_ratio: float = 0.75       # 前几级大、末级小；这里给代表值
    name: str = ""                    # UI 显示用（LPC / HPC / 自定义）


@dataclass
class TurbineSpec:
    stages: int = 1
    tip_diameter: float = 0.4
    psi_scale: float = 1.0
    shaft: int = 1
    hub_tip_ratio: float = 0.75
    name: str = ""


@dataclass
class FanSpec:
    diameter: float = 1.0
    stages: int = 1
    hub_tip_ratio: float = 0.4
    pressure_ratio: Optional[float] = None   # None → 由叶尖速度自动推
    shaft: int = 1


@dataclass
class BurnerSpec:
    kind: str = "annular"                     # can / cannular / annular / short_annular
    length: float = 0.4
    exit_temp: float = 1500.0
    dp_ratio: Optional[float] = None          # None → 由 kind 查表
    eta_comb: Optional[float] = None          # None → 由 kind + length 推


@dataclass
class AfterburnerSpec:
    enabled: bool = False
    exit_temp: float = 1900.0
    # 玩家直接拧"喷油量"时用这个：加力段油气比 far_ab（相对主流空气质量）。
    # 给了它就由它反算 exit_temp，忽略上面的 exit_temp。
    fuel_ratio: Optional[float] = None
    # 喷油环形式：影响加力燃烧效率与总压损失
    kind: str = "ring"                        # ring / spray_ring / dump


BURNER_TYPES = {
    #          ΔP/P      η_b
    "can":           (0.075, 0.970),
    "cannular":      (0.060, 0.980),
    "annular":       (0.045, 0.985),
    "short_annular": (0.055, 0.975),
}

AB_TYPES = {
    #             η_ab    ΔP/P
    "ring":       (0.930, 0.030),
    "spray_ring": (0.950, 0.040),
    "dump":       (0.900, 0.055),
}


def _ab_max_temp(tech: "TechLevel") -> float:
    """加力段没有涡轮叶片挡着，允许比 T4 热得多，但仍受喷管与隔热材料限制。"""
    return min(max(tech.t4_limit + 400.0, 1800.0), 2100.0)


def burner_performance(b: "BurnerSpec"):
    """由燃烧室形式与长度得到 (总压恢复系数, 燃烧效率)。短燃烧室驻留时间不够，效率会掉。"""
    dp, eta = BURNER_TYPES.get(b.kind, BURNER_TYPES["annular"])
    if b.dp_ratio is not None:
        dp = b.dp_ratio
    if b.eta_comb is not None:
        eta = b.eta_comb
    # 长度修正：0.30 m 以上视作驻留充分，以下按比例打折
    if b.length < 0.30:
        eta *= max(0.80, 0.55 + 1.5 * max(b.length, 0.0))
    return 1.0 - dp, eta


@dataclass
class EngineDesign:
    name: str = "Untitled"
    kind: str = "turbofan"                    # turbojet / turbofan
    tech: TechLevel = field(default_factory=lambda: TECH_PRESETS["1970s"])

    inlet_diameter: float = 0.8
    length: float = 3.0

    fan: FanSpec = field(default_factory=FanSpec)
    lpc: CompressorSpec = field(default_factory=lambda: CompressorSpec(stages=0, shaft=1))
    hpc: CompressorSpec = field(default_factory=lambda: CompressorSpec(stages=8, shaft=2))
    hpt: TurbineSpec = field(default_factory=lambda: TurbineSpec(stages=1, shaft=2))
    ipt: TurbineSpec = field(default_factory=lambda: TurbineSpec(stages=0, shaft=2))
    lpt: TurbineSpec = field(default_factory=lambda: TurbineSpec(stages=3, shaft=1))

    burner: BurnerSpec = field(default_factory=BurnerSpec)
    afterburner: AfterburnerSpec = field(default_factory=AfterburnerSpec)

    shaft_count: int = 2
    design_mach: float = 0.0
    design_alt: float = 0.0

    core_inlet_diameter: Optional[float] = None   # None → 由压气机叶尖直径推
    override_bpr: Optional[float] = None
    nozzle_kind: str = "converging"           # converging / variable / converging_diverging


# ---------------------------------------------------------------------------
# 4. 结果
# ---------------------------------------------------------------------------

@dataclass
class StageRow:
    part: str
    index: int
    pi: float
    T_in: float
    T_out: float
    P_in: float = 0.0                     # 该级入口总压（判据按真实站位算 φ）


@dataclass
class CycleResult:
    ok: bool = True
    errors: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)

    mdot_air: float = 0.0
    mdot_core: float = 0.0
    bpr: float = 0.0
    opr: float = 0.0
    fpr: float = 1.0

    rpm: Dict[int, float] = field(default_factory=dict)
    n_pct: Dict[int, float] = field(default_factory=dict)
    spool: Dict[str, float] = field(default_factory=dict)   # N1/N2/N3 读数

    T1: float = 0.0
    T2: float = 0.0
    T3: float = 0.0
    T4: float = 0.0
    T5: float = 0.0
    T7: float = 0.0
    P3: float = 0.0
    P5: float = 0.0
    far: float = 0.0                      # 主燃烧室油气比
    far_ab: float = 0.0                   # 加力段油气比

    W_comp: float = 0.0
    W_lpc: float = 0.0                    # 分量单列，判据按轴对账要用
    W_hpc: float = 0.0
    W_fan: float = 0.0
    W_turb_hpt: float = 0.0
    W_turb_ipt: float = 0.0
    W_turb_lpt: float = 0.0

    pi_hpt: float = 1.0
    pi_ipt: float = 1.0
    pi_lpt: float = 1.0

    thrust: float = 0.0
    thrust_core: float = 0.0
    thrust_bypass: float = 0.0
    fuel_flow: float = 0.0
    sfc: float = 0.0                      # kg/(N·h)
    v_jet_core: float = 0.0
    v_jet_bypass: float = 0.0
    fn_per_mdot: float = 0.0

    self_sustaining: bool = False
    stages: List[StageRow] = field(default_factory=list)
    scale: Dict[str, dict] = field(default_factory=dict)   # 各部件的尺度效应明细


# ---------------------------------------------------------------------------
# 5. 几何 → 转速
# ---------------------------------------------------------------------------

def tip_speed(diameter: float, rpm: float) -> float:
    return math.pi * diameter * rpm / 60.0


def design_shaft_speeds(d: EngineDesign, T_ref: float) -> Dict[int, float]:
    """
    每个轴的设计转速 = 该轴上所有叶轮中最严的那个马赫限制。
    叶轮越在冷端，当地音速越低，同样马赫数对应的转速越高。
    """
    tech = d.tech
    a = a_sound(T_ref)
    lim: Dict[int, List[float]] = {i: [] for i in range(1, d.shaft_count + 1)}

    def add(shaft: int, diam: float, mach: float):
        if diam <= 0 or mach <= 0:
            return
        lim.setdefault(shaft, []).append(mach * a * 60.0 / (math.pi * diam))

    if d.kind == "turbofan":
        add(d.fan.shaft, d.fan.diameter, tech.max_tip_mach_fan)
    if d.lpc.stages > 0:
        add(d.lpc.shaft, d.lpc.tip_diameter, tech.max_tip_mach_comp)
    if d.hpc.stages > 0:
        add(d.hpc.shaft, d.hpc.tip_diameter, tech.max_tip_mach_comp)
    if d.hpt.stages > 0:
        add(d.hpt.shaft, d.hpt.tip_diameter, tech.max_tip_mach_turb)
    if d.ipt.stages > 0:
        add(d.ipt.shaft, d.ipt.tip_diameter, tech.max_tip_mach_turb)
    if d.lpt.stages > 0:
        add(d.lpt.shaft, d.lpt.tip_diameter, tech.max_tip_mach_turb)

    return {s: (min(v) if v else 0.0) for s, v in lim.items()}


# ---------------------------------------------------------------------------
# 6. 单级关系
# ---------------------------------------------------------------------------

def air_viscosity(T: float) -> float:
    """Sutherland 公式：空气动力粘度 [Pa·s]"""
    return 1.458e-6 * T ** 1.5 / (T + 110.4)


def annulus_height(tip_diameter: float, hub_tip_ratio: float = 0.75) -> float:
    """叶高（叶片高度）= (D/2)·(1 − 轮毂比)"""
    return max(0.5 * tip_diameter * (1.0 - hub_tip_ratio), 0.0)


def scale_eta(tip_diameter: float, hub_tip_ratio: float, U: float,
              T: float, p: float, eta_pol: float, tech: "TechLevel",
              growth: float = 1.0) -> Tuple[float, dict]:
    """尺度效应折减：叶尖间隙占比 + 叶片雷诺数。

    大机器上这两项都饱和、不罚；机器做小了两项一起恶化，SFC 就是这么涨上去的。
    growth 是流道高度的变化倍率（压气机收缩 <1，涡轮扩张 >1），用来取代表叶高。
    """
    h_in = annulus_height(tip_diameter, hub_tip_ratio)
    if h_in <= 0.0 or U <= 0.0:
        return eta_pol, {}
    h_rep = max(h_in * growth, 1e-4)

    # 1) 叶尖间隙：间隙有制造下限，不随叶高缩小
    clr = max(tech.clearance_m, tech.clearance_ref_frac * h_rep)
    d_clr = tech.clearance_sens * (clr / h_rep - tech.clearance_ref_frac)
    d_clr = max(d_clr, 0.0)

    # 2) 叶片弦长雷诺数：损失 ∝ Re^-n
    rho = p / (R_AIR * T)
    re = rho * U * (h_rep / 2.5) / air_viscosity(T)
    d_re = 0.0
    if re < tech.re_ref:
        d_re = (1.0 - eta_pol) * ((tech.re_ref / re) ** tech.re_exponent - 1.0)

    eta = min(max(eta_pol - d_clr - d_re, 0.50), 0.97)
    info = dict(eta=eta, eta_base=eta_pol, re=re, clr_over_h=clr / h_rep,
                d_clr=d_clr, d_re=d_re, h=h_rep)
    return eta, info


def stage_pi(U: float, T_in: float, eta_pol: float, psi: float) -> float:
    """压气机单级压比：π = (1 + ψU²/(cp·T))^(γ·η/(γ-1))"""
    cp = cp_of(T_in)
    g = gamma_of(T_in)
    x = psi * U * U / (cp * T_in)
    if x <= 0:
        return 1.0
    return (1.0 + x) ** (g * eta_pol / (g - 1.0))


def stage_dT_comp(U: float, T_in: float, eta_pol: float, psi: float) -> float:
    """压气机单级实际温升 ΔT（也就是单级耗功 / (cp·mdot)）"""
    cp = cp_of(T_in)
    return psi * U * U / cp


def turbine_dT(U: float, eta_pol: float, psi: float) -> float:
    """涡轮单级实际温降 ΔT = ψU²/cp（涡轮功系数把效率包在里面）"""
    return psi * U * U / CP_AIR / eta_pol


def pr_from_tr(T_in: float, T_out: float, eta_pol: float) -> float:
    """由涡轮温降反解落压比：T_out/T_in = (1/π)^((γ-1)·η/γ)"""
    if T_out >= T_in:
        return 1.0
    g = gamma_of(T_in)
    tr = max(T_out / T_in, 1e-6)
    return tr ** (-g / ((g - 1.0) * eta_pol))


# ---------------------------------------------------------------------------
# 7. 设计点求解
# ---------------------------------------------------------------------------

SPOOL_LABELS = {
    # 轴号 → (玩家看到的编号, 通常挂什么)
    1: "N1（低压/风扇）",
    2: "N2（高压）",
    3: "N3（中压/高压）",
}


def spool_readout(d: EngineDesign, rpm: Dict[int, float],
                  n_pct: Optional[Dict[int, float]] = None) -> Dict[str, float]:
    """把轴号映射成 N1/N2/N3 读数。

    约定（与真实发动机命名一致）：
      单轴      → 只有 N2（KSP 玩家仍习惯看 N2）
      双轴      → N1=低压轴（风扇+低压压气机+低压涡轮），N2=高压轴
      三轴      → N1=风扇轴，N2=中压轴，N3=高压轴
    轴号由设计本身决定：风扇/LPC 用同一个轴号就是双轴，LPC 单独一个轴就是三轴。
    """
    out: Dict[str, float] = {}
    if not rpm:
        return out
    shafts = sorted(rpm.keys())
    if len(shafts) == 1:
        names = {shafts[0]: "N2"}
    elif len(shafts) == 2:
        # 挂风扇的轴是 N1，另一根是 N2
        fan_shaft = d.fan.shaft if d.fan.diameter > 0 else shafts[0]
        low = fan_shaft if fan_shaft in shafts else shafts[0]
        names = {s: ("N1" if s == low else "N2") for s in shafts}
    else:
        # 三轴：风扇轴 N1，其余按直径从大到小 N2、N3
        fan_shaft = d.fan.shaft if d.fan.diameter > 0 else shafts[0]
        rest = [s for s in shafts if s != fan_shaft]
        rest.sort(key=lambda s: -max(
            (d.lpc.tip_diameter if d.lpc.shaft == s else 0.0),
            (d.hpc.tip_diameter if d.hpc.shaft == s else 0.0)))
        names = {fan_shaft: "N1"}
        for i, s in enumerate(rest, start=2):
            names[s] = f"N{i}"
    for s, r in rpm.items():
        out[names.get(s, f"N{s}")] = r
    if n_pct:
        for s, p in n_pct.items():
            out[names.get(s, f"N{s}") + "%"] = p
    return out


def solve_design_point(d: EngineDesign) -> CycleResult:
    res = CycleResult()
    tech = d.tech

    # ---- 来流 ----
    p0, T0 = isa(d.design_alt)
    if d.design_mach > 0:
        pt0, Tt0 = total_from_static(p0, T0, d.design_mach)
        tpr = intake_tpr(tech, d.design_mach)
    else:
        pt0, Tt0 = p0, T0
        tpr = tech.intake_tpr_sub
    pt1 = pt0 * tpr
    Tt1 = Tt0
    V0 = d.design_mach * a_sound(T0)
    res.T1 = Tt1

    # ---- 转速 ----
    rpm = design_shaft_speeds(d, Tt1)
    res.rpm = rpm
    res.n_pct = {s: 100.0 for s in rpm}

    # ---- 风扇 ----
    has_fan = d.kind == "turbofan" and d.fan.diameter > 0
    if has_fan:
        U_fan = tip_speed(d.fan.diameter, rpm.get(d.fan.shaft, 0.0))
        eta_fan, info = scale_eta(d.fan.diameter, d.fan.hub_tip_ratio, U_fan,
                                  Tt1, pt1, tech.fan_eta_pol, tech, growth=0.80)
        res.scale["FAN"] = info
        pi_f = stage_pi(U_fan, Tt1, eta_fan, tech.fan_psi) ** d.fan.stages
        fpr = d.fan.pressure_ratio if d.fan.pressure_ratio else pi_f
    else:
        fpr = 1.0
        U_fan = 0.0
        eta_fan = tech.fan_eta_pol
    res.fpr = fpr
    Tt2 = Tt1 + (fpr ** ((gamma_of(Tt1) - 1.0) / gamma_of(Tt1)) - 1.0) * Tt1 / eta_fan
    pt2 = pt1 * fpr
    res.T2 = Tt2

    # ---- 压气机逐级 ----
    pt, Tt = pt2, Tt2
    Tt_lpc_out = Tt2
    if d.lpc.stages > 0:
        U_l = tip_speed(d.lpc.tip_diameter, rpm.get(d.lpc.shaft, 0.0))
        eta_lpc, info = scale_eta(d.lpc.tip_diameter, d.lpc.hub_tip_ratio, U_l, Tt, pt,
                                  tech.lpc_eta_pol, tech, growth=0.55)
        res.scale["LPC"] = info
    else:
        eta_lpc = tech.lpc_eta_pol
    for i in range(d.lpc.stages):
        U = tip_speed(d.lpc.tip_diameter, rpm.get(d.lpc.shaft, 0.0))
        pi_s = stage_pi(U, Tt, eta_lpc, tech.compressor_psi * d.lpc.psi_scale)
        T_in = Tt
        Tt = T_in + stage_dT_comp(U, T_in, eta_lpc,
                                  tech.compressor_psi * d.lpc.psi_scale)
        res.stages.append(StageRow("LPC", i + 1, pi_s, T_in, Tt, pt))
        pt *= pi_s
        Tt_lpc_out = Tt
    Tt_hpc_in = Tt
    if d.hpc.stages > 0:
        U_h = tip_speed(d.hpc.tip_diameter, rpm.get(d.hpc.shaft, 0.0))
        eta_hpc, info = scale_eta(d.hpc.tip_diameter, d.hpc.hub_tip_ratio, U_h, Tt, pt,
                                  tech.hpc_eta_pol, tech, growth=0.55)
        res.scale["HPC"] = info
    else:
        eta_hpc = tech.hpc_eta_pol
    for i in range(d.hpc.stages):
        U = tip_speed(d.hpc.tip_diameter, rpm.get(d.hpc.shaft, 0.0))
        pi_s = stage_pi(U, Tt, eta_hpc, tech.compressor_psi * d.hpc.psi_scale)
        T_in = Tt
        Tt = T_in + stage_dT_comp(U, T_in, eta_hpc,
                                  tech.compressor_psi * d.hpc.psi_scale)
        res.stages.append(StageRow("HPC", i + 1, pi_s, T_in, Tt, pt))
        pt *= pi_s

    res.opr = pt / pt1
    res.T3 = Tt
    res.P3 = pt

    if d.hpc.stages == 0 and d.lpc.stages == 0:
        res.errors.append("没有任何压气机级：这只是一台冲压发动机，不是涡喷/涡扇")
        res.ok = False

    # ---- 燃烧室 ----
    sigma_b, eta_b = burner_performance(d.burner)
    pt4 = pt * sigma_b
    t4 = min(d.burner.exit_temp, tech.t4_limit)
    if d.burner.exit_temp > tech.t4_limit:
        res.warnings.append(
            f"设定 T4={d.burner.exit_temp:.0f} K 超过技术上限 {tech.t4_limit:.0f} K，已按上限")
    res.T4 = t4
    if t4 <= Tt:
        res.errors.append(f"T4={t4:.0f}K 不高于压气机出口 {Tt:.0f}K：无法加热")
        res.ok = False

    # ---- 流量 ----
    if has_fan:
        # 涡扇：进气道捕获面积就是风扇罩面积
        A_in = math.pi * (d.fan.diameter / 2.0) ** 2
    else:
        A_in = math.pi * (d.inlet_diameter / 2.0) ** 2
    # 面马赫按设计惯例取 0.5：进气道在减速增压，迎面马赫远低于飞行马赫。
    # 早先写成"设计马赫>0 就拿它当面马赫"，等于把进气道当空管子，Ma0.85 时流量多出七成。
    m0 = 0.5
    mdot_air = _mass_flow(A_in, pt0, Tt0, m0)
    res.mdot_air = mdot_air

    # ---- 涵道比：由风扇环面积 / 核心机进口面积 ----
    if has_fan:
        A_fan = math.pi * (d.fan.diameter / 2.0) ** 2
        D_core = d.core_inlet_diameter
        if D_core is None:
            D_core = max(d.lpc.tip_diameter if d.lpc.stages > 0 else 0.0,
                         d.hpc.tip_diameter if d.hpc.stages > 0 else 0.0)
        D_core = min(D_core, d.fan.diameter * 0.95)
        A_core_in = math.pi * (D_core / 2.0) ** 2
        bpr_geo = max((A_fan - A_core_in) / A_core_in, 0.0)
        bpr = d.override_bpr if d.override_bpr is not None else bpr_geo
    else:
        bpr = 0.0
    res.bpr = bpr
    mdot_core = mdot_air / (1.0 + bpr)
    mdot_byp = mdot_air - mdot_core
    res.mdot_core = mdot_core
    if mdot_core <= 0:
        res.errors.append("核心机流量为零：检查风扇轮毂比 / 涵道比")
        res.ok = False

    # ---- 压气机与风扇耗功 ----
    W_lpc = mdot_core * cp_of(Tt_lpc_out) * (Tt_lpc_out - Tt2) / tech.mech_efficiency
    W_hpc = mdot_core * cp_of(res.T3) * (res.T3 - Tt_hpc_in) / tech.mech_efficiency
    W_comp = W_lpc + W_hpc
    W_fan = 0.0
    if has_fan and fpr > 1.0001:
        W_fan = mdot_air * cp_of(Tt2) * (Tt2 - Tt1) / tech.mech_efficiency
    res.W_comp, res.W_fan = W_comp, W_fan
    res.W_lpc, res.W_hpc = W_lpc, W_hpc

    # ---- 燃烧室加热 ----
    far = 0.0
    cp3, cp4 = cp_of(res.T3), cp_of(t4)
    dT_burn = t4 - res.T3
    denom = LHV_JET * eta_b - cp4 * dT_burn
    if denom <= 0:
        res.errors.append("燃烧室温升过高，能量不守恒（检查 T4 与 LHV 模型）")
        res.ok = False
        far = 0.0
    else:
        far = cp3 * dT_burn / denom
    if far > 0.08:
        res.warnings.append(f"油气比 f={far:.3f} 偏高（典型上限 ~0.068），燃烧室可能熄火")
    fuel_main = mdot_core * far
    res.far = far

    # ---- 按轴做涡轮功平衡 ----
    # 每个涡轮按流路顺序：HPT 在 T4 处工作，LPT 在 HPT 出口处工作
    # 轴上功需求：该轴挂的所有压气机/风扇
    need: Dict[int, float] = {}
    if has_fan:
        need[d.fan.shaft] = need.get(d.fan.shaft, 0.0) + W_fan
    if d.lpc.stages > 0:
        need[d.lpc.shaft] = need.get(d.lpc.shaft, 0.0) + max(W_lpc, 0.0)
    if d.hpc.stages > 0:
        need[d.hpc.shaft] = need.get(d.hpc.shaft, 0.0) + max(W_hpc, 0.0)

    # 校验：需求轴必须有涡轮
    driven = set()
    if d.hpt.stages > 0:
        driven.add(d.hpt.shaft)
    if d.ipt.stages > 0:
        driven.add(d.ipt.shaft)
    if d.lpt.stages > 0:
        driven.add(d.lpt.shaft)
    for s, w in need.items():
        if w > 1.0 and s not in driven:
            res.errors.append(f"轴 {s} 上有 {w/1e6:.2f} MW 的压气机负载，但没有涡轮驱动它 → 转不起来")
            res.ok = False

    # ---- 涡轮逐台求解（按流路顺序） ----
    T_cur = t4
    P_cur = pt4
    far_cur = far
    for name, spec, need_key, work_out in (
        ("HPT", d.hpt, d.hpt.shaft, "hpt"),
        ("IPT", d.ipt, d.ipt.shaft, "ipt"),
        ("LPT", d.lpt, d.lpt.shaft, "lpt"),
    ):
        if spec.stages <= 0:
            continue
        U = tip_speed(spec.tip_diameter, rpm.get(need_key, 0.0))
        eta_t = tech.lpt_eta_pol if name == "LPT" else tech.hpt_eta_pol
        eta_t, info = scale_eta(spec.tip_diameter, spec.hub_tip_ratio, U, T_cur, P_cur,
                                eta_t, tech, growth=1.40)
        res.scale[name] = info
        dT_stage = turbine_dT(U, eta_t, tech.turbine_psi * spec.psi_scale)
        max_dT = spec.stages * dT_stage
        m_turb = mdot_core * (1.0 + far_cur)
        w_dem = need.get(need_key, 0.0)
        dT_need = w_dem / max(m_turb * cp_of(T_cur), 1e-9)
        if dT_need > max_dT:
            res.errors.append(
                f"{name} 级数不足：需要 ΔT={dT_need:.0f}K，{spec.stages} 级只能提供 {max_dT:.0f}K")
            res.ok = False
            dT_need = max_dT
        T_next = T_cur - dT_need
        pi_t = pr_from_tr(T_cur, T_next, eta_t)
        if pi_t > tech.turb_stage_pr_max ** spec.stages:
            res.errors.append(
                f"{name} 落压比 {pi_t:.2f} 超过 {spec.stages} 级的极限 "
                f"{tech.turb_stage_pr_max ** spec.stages:.2f}")
            res.ok = False
        if T_next < 0.4 * T_cur:
            res.warnings.append(f"{name} 温降过大（{T_cur:.0f}→{T_next:.0f}K），涡轮可能超负荷")
        if name == "HPT":
            res.pi_hpt, res.W_turb_hpt = pi_t, w_dem
        elif name == "IPT":
            res.pi_ipt, res.W_turb_ipt = pi_t, w_dem
        else:
            res.pi_lpt, res.W_turb_lpt = pi_t, w_dem
        T_cur, P_cur = T_next, P_cur / pi_t
        far_cur = far_cur

    res.T5 = T_cur
    res.P5 = P_cur

    # ---- 加力 ----
    t7 = T_cur
    fuel_ab = 0.0
    far_ab = 0.0
    ab = d.afterburner
    if ab.enabled and d.nozzle_kind == "converging":
        res.warnings.append(
            "配了加力却用固定收敛喷管：加力后落压比升到 5–6，欠膨胀损失会吃掉不少推力，"
            "真实加力机都用可变面积喷管")
    eta_ab, dp_ab = AB_TYPES.get(ab.kind, AB_TYPES["ring"])
    if ab.enabled:
        if ab.fuel_ratio is not None and ab.fuel_ratio > 0.0:
            # 玩家直接给的喷油量：由油气比反算出口温度
            f_ab = min(ab.fuel_ratio, 0.045)      # 加力油气比物理上限约 0.04~0.05
            added = f_ab * LHV_JET * eta_ab
            t7 = T_cur + added / cp_of(T_cur) / max(1.0 + f_ab, 1e-6)
            t7 = min(t7, _ab_max_temp(tech))
            far_ab = f_ab
        elif ab.exit_temp > T_cur:
            t7 = min(ab.exit_temp, _ab_max_temp(tech))
            dT7 = t7 - T_cur
            den = LHV_JET * eta_ab - cp_of(t7) * dT7
            far_ab = cp_of(T_cur) * dT7 / den if den > 0 else 0.0
        else:
            far_ab = 0.0
        if far_ab > 0.0:
            fuel_ab = mdot_core * (1.0 + far) * far_ab
            P_ab = P_cur * (1.0 - dp_ab)
            P_cur = P_ab
    res.T7 = t7
    res.far_ab = far_ab if ab.enabled else 0.0

    # ---- 喷管（含压力推力项）----
    m_core_exh = mdot_core + fuel_main + fuel_ab
    v_core, a_core, p_core, f_core_gross, _chk = nozzle_flow(
        m_core_exh, P_cur, t7, p0, tech.nozzle_eta)
    f_core = f_core_gross - mdot_core * V0
    f_byp = 0.0
    v_byp = 0.0
    if has_fan:
        # 旁路喷管：从风扇出口总压直接膨胀到环境（注意 fpr*tpr 是压比，要乘 p0）
        v_byp, _a, _p, f_byp_gross, _c = nozzle_flow(
            mdot_byp, p0 * fpr * tpr, Tt2, p0, tech.nozzle_eta)
        f_byp = f_byp_gross - mdot_byp * V0
    res.v_jet_core, res.v_jet_bypass = v_core, v_byp
    res.thrust_core, res.thrust_bypass = f_core, f_byp
    res.thrust = f_core + f_byp

    if P_cur / p0 < 1.0:
        res.errors.append("涡轮出口总压低于环境压力：喷管会倒流，设计不可行")
        res.ok = False

    fuel_total = fuel_main + fuel_ab
    res.fuel_flow = fuel_total
    res.sfc = fuel_total * 3600.0 / max(res.thrust, 1e-9)
    res.fn_per_mdot = res.thrust / max(mdot_air, 1e-9)

    res.self_sustaining = res.ok and t4 <= tech.t4_limit and res.thrust > 0
    res.spool = spool_readout(d, res.rpm, res.n_pct)
    return res


def _mass_flow(area: float, pt: float, Tt: float, mach: float) -> float:
    g = gamma_of(Tt)
    return area * pt / math.sqrt(Tt) * math.sqrt(g / R_AIR) * mach * \
        (1.0 + 0.5 * (g - 1.0) * mach * mach) ** (-(g + 1.0) / (2.0 * (g - 1.0)))


def nozzle_flow(mdot: float, pt: float, Tt: float, p_amb: float, eta_n: float):
    """收敛喷管完整解算。

    返回 (V_e, A_e, p_e, F_gross, choked)。F_gross = ṁV_e + (p_e − p_a)·A_e
    —— 壅塞时出口压力高于环境，压力推力项不可省（涡喷上占 25% 以上）。
    A_e 由连续方程反解（喷管喉道假定恰好流过 ṁ）。
    """
    if pt <= p_amb or mdot <= 0.0:
        return 0.0, 0.0, p_amb, 0.0, False
    g = gamma_of(Tt)
    npr = pt / p_amb
    npr_crit = ((g + 1.0) / 2.0) ** (g / (g - 1.0))
    if npr >= npr_crit:
        choked = True
        tr = 2.0 / (g + 1.0)
        pe = pt / npr_crit
    else:
        choked = False
        tr = (1.0 / npr) ** ((g - 1.0) / g)
        pe = p_amb
    te = Tt * tr
    v_ideal = math.sqrt(max(2.0 * cp_of(Tt) * Tt * (1.0 - tr), 0.0))
    ve = v_ideal * math.sqrt(max(eta_n, 0.0))
    rho_e = pe / (R_AIR * max(te, 1.0))
    ae = mdot / max(rho_e * v_ideal, 1e-9)
    f_gross = mdot * ve + (pe - p_amb) * ae
    return ve, ae, pe, f_gross, choked


def _nozzle_vel(epr: float, Tt: float, eta_n: float) -> float:
    """收敛喷管出口速度（只给速度，不给推力）。壅塞时按 M=1 处理。"""
    if epr <= 1.0:
        return 0.0
    g = gamma_of(Tt)
    npr_crit = ((g + 1.0) / 2.0) ** (g / (g - 1.0))
    tr = 2.0 / (g + 1.0) if epr >= npr_crit else (1.0 / epr) ** ((g - 1.0) / g)
    return math.sqrt(max(2.0 * cp_of(Tt) * Tt * (1.0 - tr) * eta_n, 0.0))


# ---------------------------------------------------------------------------
# 8. 自检
# ---------------------------------------------------------------------------

def _demo():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

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
    r = solve_design_point(d)
    print(f"OPR={r.opr:.2f} FPR={r.fpr:.2f} BPR={r.bpr:.2f}")
    print(f"mdot={r.mdot_air:.1f} kg/s (core {r.mdot_core:.1f})")
    print(f"rpm={ {k: round(v) for k, v in r.rpm.items()} }")
    print(f"T1={r.T1:.0f} T2={r.T2:.0f} T3={r.T3:.0f} T4={r.T4:.0f} T5={r.T5:.0f}")
    print(f"W_comp={r.W_comp/1e6:.2f} MW  W_fan={r.W_fan/1e6:.2f} MW")
    print(f"pi_hpt={r.pi_hpt:.2f} pi_lpt={r.pi_lpt:.2f}")
    print(f"Thrust={r.thrust/1000:.2f} kN (core {r.thrust_core/1000:.2f})")
    print(f"SFC={r.sfc:.4f} kg/(N·h)  Fn/mdot={r.fn_per_mdot:.1f} N/(kg/s)")
    print(f"Vj_core={r.v_jet_core:.0f} m/s  Vj_byp={r.v_jet_bypass:.0f}")
    print("self_sustaining:", r.self_sustaining)
    for e in r.errors:
        print(" ✗", e)
    for w in r.warnings:
        print(" !", w)
    print("\n每级压比:")
    for s in r.stages:
        print(f"  {s.part}{s.index:>2}  π={s.pi:.3f}  T:{s.T_in:.0f}→{s.T_out:.0f}K")


if __name__ == "__main__":
    _demo()