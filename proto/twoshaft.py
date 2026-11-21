# -*- coding: utf-8 -*-
# ⚠ 这个原型**不可信**，只作为失败记录留着。它现在给出"慢车 91 kN、SFC 谷底在 0% 油门"，
# 显然不对（JF-1 慢车应该 7~8 kN）。原因：核心压比那条迭代没真的收敛（一直贴在 12.32），
# 风扇转速被顶在区间上沿（fpr 恒 1.786）。也就是说管子里的水还没接通，
# 谁拿它当基线去调 C# 都会被带偏。见 docs §46.7。
"""

两轴各自平衡的原型：验证"高压轴 W_HPT=W_HPC、低压轴 W_LPT=W_fan"能不能
把涡扇的油耗谷底从 10~25% 油门推回 85~95%。

为什么先写原型：§44、§45、§46 三次直接改 OffDesign 都撞墙，根因都是
"一根总轴"下的按比例分配。这一次先把物理跑通、看形状对不对，再动 C#。

几何与设计点取自 JF-1：
  进气 204.2 kg/s，涵道比 2.75，OPR 11.73，风扇压比 1.59，
  T3 626 K，T4 1500 K，T5 1085 K，A8 0.1723 m²，A_turb 0.04706 m²
"""
import math

R = 287.05
G0 = 9.80665
P0 = 101325.0
T0 = 288.15
LHV = 43.0e6


def cp_air(t):
    return 1004.5


def cp_hot(t):
    # 简单线性：600 K 处 ~1060，1500 K 处 ~1180
    return 1004.5 + 0.126 * (t - 300.0)


def gam_air(t):
    return 1.40


def gam_hot(t):
    return 1.33 - 0.00002 * (t - 1000.0)


# ---- 设计点 ----
MDOT_AIR_D = 204.2
BPR_D = 2.75
OPR_D = 11.73
FPR_D = 1.59
T3_D = 626.0
T4_D = 1500.0
T5_D = 1085.0
T4_IDLE = 796.0
A8_D = 0.1723
ATURB_D = 0.04706
ETA_C = 0.88
ETA_F = 0.90
ETA_T = 0.90
ETA_B = 0.99


def psi_ratio(nc):
    if nc <= 0.0:
        return 0.0
    if nc < 1.0:
        r = 1.0 - 0.62 * (1.0 - nc) ** 2
        return max(r, 0.02)
    return min(1.0 + 0.05 * (nc - 1.0), 1.25)


def flow_frac(nc):
    return max(min(nc, 1.15) ** 0.80, 0.02)


def fpr_of(n1):
    return 1.0 + (FPR_D - 1.0) * psi_ratio(n1) * n1 * n1


def choked_flow(a, pt, tt, gamma):
    # 单位面积的质量流量（临界）
    return a * pt / math.sqrt(tt) * math.sqrt(gamma / R) * (2.0 / (gamma + 1.0)) ** ((gamma + 1.0) / (2.0 * (gamma - 1.0)))


def area_from_flow(mdot, pt, tt, gamma):
    phi = (2.0 / (gamma + 1.0)) ** ((gamma + 1.0) / (2.0 * (gamma - 1.0)))
    return mdot / (pt / math.sqrt(tt) * math.sqrt(gamma / R) * phi)


def nozzle_thrust(mdot, tt, pt, p_amb, area, gamma, eta=1.0):
    """收敛喷管：能憋到临界就按临界出口，否则按完全膨胀。"""
    p_crit = pt * (2.0 / (gamma + 1.0)) ** (gamma / (gamma - 1.0))
    if p_crit >= p_amb:
        te = tt * 2.0 / (gamma + 1.0)
        pe = p_crit
        ve = math.sqrt(gamma * R * te)
        ae = area
    else:
        te = tt * (p_amb / pt) ** ((gamma - 1.0) / gamma)
        pe = p_amb
        ve = math.sqrt(max(2.0 * gamma / (gamma - 1.0) * R * tt * (1.0 - (p_amb / pt) ** ((gamma - 1.0) / gamma)), 0.0))
        ae = mdot / max(pe / (R * te) * ve, 1e-9)
    return mdot * ve + (pe - p_amb) * ae, ve


def design_consts():
    """从设计点反解涡轮与旁路喷口的面积（与 C# 的 DeriveDesignConstants 一个口径）。"""
    c = {}
    c['mdot_core'] = MDOT_AIR_D / (1.0 + BPR_D)
    c['mdot_by'] = MDOT_AIR_D - c['mdot_core']
    # 风扇：T2 = T1 + T1*(fpr^(e)-1)
    ef = (gam_air(T0) - 1.0) / gam_air(T0) * ETA_F
    tt2 = T0 * (1.0 + (FPR_D ** ef - 1.0))
    pt2 = P0 * FPR_D
    c['A_by'] = area_from_flow(c['mdot_by'], pt2, tt2, gam_air(tt2))
    # 核心机：pt4 = P0*OPR*(1-ΔP)
    pt4 = P0 * OPR_D * 0.96
    c['A_turb'] = area_from_flow(c['mdot_core'], pt4, T4_D, gam_hot(T4_D))
    c['A8'] = A8_D
    c['tt2_d'] = tt2
    c['pt2_d'] = pt2
    return c


def solve_point(t4, c, verbose=False):
    """给定 T4，解出核心机电平与风扇转速。"""
    gc = gam_hot(T4_D)
    mdot_core_d = c['mdot_core']

    # (A) 涡轮导向器临界：核心流量由压气机容量给，涡轮只当上限。
    #     这里反过来用：涡轮临界流量定核心流量（定几何下这是主因）。
    pt4 = None
    t3 = None
    mdot_core = None
    core_pr = None
    # 迭代：核心压比 ↔ 涡轮入口压力 ↔ 流量
    core_pr = OPR_D
    for _ in range(40):
        # 压气机：T3 = T1*(1+ (corePr^e -1))
        ec = (gam_air(T0) - 1.0) / gam_air(T0) * ETA_C
        t3 = T0 * (1.0 + (core_pr ** ec - 1.0))
        if t3 > t4 * 0.92:
            t3 = t4 * 0.92
            core_pr = (1.0 + (t3 / T0 - 1.0)) ** (1.0 / ec)
        pt4 = P0 * core_pr * 0.96
        mdot_choke = choked_flow(c['A_turb'], pt4, t4, gc)
        wc = mdot_core_d * flow_frac(min(core_pr / OPR_D, 1.02) ** 0.5)
        if abs(mdot_choke - wc) < 0.2:
            mdot_core = mdot_choke
            break
        mdot_core = mdot_choke
        # 用压气机容量拉回：流量小 → 压比低
        core_pr *= 1.0 + 0.3 * (mdot_choke / max(wc, 1e-9) - 1.0) * 0.5
        core_pr = max(min(core_pr, OPR_D * 1.05), 1.2)

    w_hpc = cp_air(t3) * (t3 - T0)
    # HPT：只提取 W_HPC
    dh_hpt = w_hpc
    t45 = t4 - dh_hpt / cp_hot(t4)
    gh = gam_hot(t4)
    pi_hpt = (1.0 - dh_hpt / (cp_hot(t4) * t4 * ETA_T)) ** (-gh / (gh - 1.0))
    pt45 = pt4 / max(pi_hpt, 1.0001)

    # 低压涡轮可用功：从 pt45 膨胀到环境压力
    gl = gam_hot(t45)
    t5_min = t45 * (P0 / pt45) ** ((gl - 1.0) * ETA_T / gl)
    w_lpt_max = cp_hot(t45) * (t45 - t5_min)          # 每 kg 核心流量

    # 风扇：n1 决定压比与流量，用低压轴功率平衡定 n1
    def fan_demand(n1):
        fpr = fpr_of(n1)
        ef = (gam_air(T0) - 1.0) / gam_air(T0) * ETA_F
        tt2 = T0 * (1.0 + (fpr ** ef - 1.0))
        pt2 = P0 * fpr
        mby = choked_flow(c['A_by'], pt2, tt2, gam_air(tt2))
        w = mby * cp_air(T0) * (fpr ** ef - 1.0)
        return w / mdot_core, mby, fpr, tt2, pt2

    lo, hi = 0.35, 1.15
    for _ in range(60):
        mid = 0.5 * (lo + hi)
        wreq = fan_demand(mid)[0]
        if wreq > w_lpt_max:
            hi = mid
        else:
            lo = mid
    n1 = lo
    wfan_pkg, mby, fpr, tt2, pt2 = fan_demand(n1)

    # 涡轮出口：取出风扇这份功
    t5 = t45 - wfan_pkg / (cp_hot(t45) / cp_hot(t45)) / 1.0
    t5 = t45 - wfan_pkg / cp_hot(t45)
    if t5 < t5_min:
        t5 = t5_min
    gh5 = gam_hot(t5)
    pi_lpt = (1.0 - (t45 - t5) / (t45 * ETA_T)) ** (-gh5 / (gh5 - 1.0))
    p5 = pt45 / max(pi_lpt, 1.0001)

    # 推力
    f_core, ve_core = nozzle_thrust(mdot_core, t5, p5, P0, c['A8'], gam_hot(t5))
    f_by, ve_by = nozzle_thrust(mby, tt2, pt2, P0, c['A_by'], gam_air(tt2))
    far = cp_air(t3) * (t4 - t3) / (LHV * ETA_B - cp_hot(t4) * (t4 - t3))
    mdot_fuel = mdot_core * far
    thrust = f_core + f_by
    sfc = mdot_fuel * 3600.0 / thrust if thrust > 1.0 else float('inf')
    if verbose:
        print("  T4={0:.0f} n1={1:.3f} corePr={2:.2f} fpr={3:.3f} bpr={4:.2f} mdot={5:.1f} T5={6:.0f} "
              "Vc={7:.0f} Vb={8:.0f} F={9:.1f}kN SFC={10:.4f}".format(
                  t4, n1, core_pr, fpr, mby / mdot_core, mdot_core * (1 + mby / mdot_core), t5,
                  ve_core, ve_by, thrust / 1000.0, sfc))
    return dict(t4=t4, n1=n1, fpr=fpr, core_pr=core_pr, bpr=mby / mdot_core,
                thrust=thrust, sfc=sfc, t5=t5, wc=ve_core, wb=ve_by)


if __name__ == '__main__':
    c = design_consts()
    print("设计常数：A_turb={0:.5f} A_by={1:.4f} A8={2:.4f} 核心流量={3:.1f}".format(
        c['A_turb'], c['A_by'], c['A8'], c['mdot_core']))
    print("\n两轴各自平衡下的油门扫描（T4 线性映射到 796~1500 K）")
    print("油门  T4    核心压比 风扇压比 涵道比 核心速度 外涵速度 推力kN   SFC")
    best = (None, 9e9)
    for i in range(11):
        thr = i / 10.0
        t4 = T4_IDLE + thr * (T4_D - T4_IDLE)
        r = solve_point(t4, c)
        print("{0:.2f}  {1:.0f}   {2:6.2f}   {3:5.3f}   {4:5.2f}  {5:6.0f}   {6:6.0f}  {7:6.1f}  {8:.4f}".format(
            thr, t4, r['core_pr'], r['fpr'], r['bpr'], r['wc'], r['wb'], r['thrust'] / 1000.0, r['sfc']))
        if r['sfc'] < best[1]:
            best = (thr, r['sfc'])
    print("\n最省油油门 {0:.2f}（SFC {1:.4f}）——真机涡扇应该在 0.85~0.95".format(best[0], best[1]))