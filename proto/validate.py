"""
用真实发动机的公开数据反标定原型。

思路：几何参数（级数/直径/轴）照真实机器填，目标压比 OPR 用二分去凑 psi_scale，
凑到之后看两件事：
  1. 拟合出来的载荷系数 psi 是不是落在真实机器的量级（0.15~0.35）
  2. 算出来的 SFC / 单位推力，跟手册值差多少

数据全部来自 Wikipedia 的发动机规格表（research/spec*.txt 有原文）：
  J79  : 52.8 kN 干态, OPR 13.5, TIT 1210 K, SFC 0.84 lb/(lbf·h), 17 级单转子
  J57  : 53.5 kN 起飞, OPR 12.5, TIT 1143 K, SFC 0.785 lb/(lbf·h), 9+7 双转子
  J85  : 16 kN 干态,   OPR 8.3,  TIT 1253 K, SFC 1.24 lb/(lbf·h), 9 级单转子
  RD-33: 50 kN 干态,   OPR 21,   TIT 1680 K, SFC 0.075 kg/(N·h), BPR 0.49
"""

import sys
from dataclasses import replace

from jetforge import (
    TECH_PRESETS, EngineDesign, FanSpec, CompressorSpec, TurbineSpec,
    BurnerSpec, solve_design_point,
)

LB_LBF_H_TO_KG_N_H = 0.10197   # 1 lb/(lbf·h) = 0.10197 kg/(N·h)


def sfc_lb(sfc_kg_n_h: float) -> float:
    return sfc_kg_n_h / LB_LBF_H_TO_KG_N_H


# ---------------------------------------------------------------------------
# 真实机器
# ---------------------------------------------------------------------------

REAL = {
    "J79": dict(
        label="J79-GE-17（1955 涡喷）",
        kind="turbojet",
        opr=13.5, t4=1210.0, sfc_lb=0.84,
        thrust_kn=52.8, mdot=77.0,
        tech="1960s",
        lpc=CompressorSpec(stages=0, tip_diameter=0.0, shaft=1),
        hpc=CompressorSpec(stages=17, tip_diameter=0.76, shaft=1),
        hpt=TurbineSpec(stages=3, tip_diameter=0.70, shaft=1),
        fan=None,
    ),
    "J57": dict(
        label="J57 族 / JT3C-7（1953 涡喷，双转子）",
        kind="turbojet",
        opr=12.5, t4=1143.0, sfc_lb=0.785,
        thrust_kn=53.5, mdot=81.7,
        tech="1960s",
        lpc=CompressorSpec(stages=9, tip_diameter=0.90, shaft=1),
        hpc=CompressorSpec(stages=7, tip_diameter=0.72, shaft=2),
        hpt=TurbineSpec(stages=1, tip_diameter=0.68, shaft=2),
        lpt=TurbineSpec(stages=2, tip_diameter=0.78, shaft=1),
        fan=None,
    ),
    "J85": dict(
        label="J85-GE-13（1957 涡喷，小推力）",
        kind="turbojet",
        opr=8.3, t4=1253.0, sfc_lb=1.24,
        thrust_kn=16.0, mdot=24.0,
        tech="1960s",
        lpc=CompressorSpec(stages=0, tip_diameter=0.0, shaft=1),
        hpc=CompressorSpec(stages=9, tip_diameter=0.45, shaft=1),
        hpt=TurbineSpec(stages=2, tip_diameter=0.42, shaft=1),
        fan=None,
    ),
    "RD33": dict(
        label="RD-33（1970s 涡扇）",
        kind="turbofan",
        opr=21.0, t4=1680.0, sfc_lb=0.735,
        thrust_kn=50.0, mdot=75.5, bpr=0.49,
        tech="1970s",
        fan=FanSpec(diameter=0.78, stages=4, hub_tip_ratio=0.40),
        lpc=CompressorSpec(stages=0, tip_diameter=0.0, shaft=1),
        hpc=CompressorSpec(stages=9, tip_diameter=0.60, shaft=2),
        hpt=TurbineSpec(stages=1, tip_diameter=0.58, shaft=2),
        lpt=TurbineSpec(stages=2, tip_diameter=0.62, shaft=1),
    ),
}


def build(spec, psi_scale, bpr=None):
    fan = replace(spec["fan"], shaft=1) if spec.get("fan") else FanSpec(0, 0, 0.5)
    return EngineDesign(
        name=spec["label"], kind=spec["kind"],
        tech=TECH_PRESETS[spec["tech"]],
        inlet_diameter=1.0,
        fan=fan,
        lpc=replace(spec["lpc"], psi_scale=psi_scale),
        hpc=replace(spec["hpc"], psi_scale=psi_scale),
        hpt=spec["hpt"],
        lpt=spec.get("lpt", TurbineSpec(stages=0)),
        burner=BurnerSpec(exit_temp=spec["t4"]),
        override_bpr=bpr if bpr is not None else spec.get("bpr"),
        shaft_count=2 if spec.get("lpt") else 1,
    )


def fit_psi(spec, target_opr, lo=0.15, hi=1.6, iters=26):
    """二分 psi_scale，让设计点总压比落到真实值。"""
    for _ in range(iters):
        mid = 0.5 * (lo + hi)
        r = solve_design_point(build(spec, mid, None))
        if r.opr <= 0:
            return None, mid
        if r.opr < target_opr:
            lo = mid
        else:
            hi = mid
    mid = 0.5 * (lo + hi)
    return solve_design_point(build(spec, mid, None)), mid


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    print("一、SFC 校验（psi_scale 拟合到真实 OPR，T4 用手册值）")
    print(f"{'机型':<26} {'OPR':>6} {'psi':>6} {'BPR':>6} {'T4':>6} "
          f"{'SFC算':>7} {'SFC真':>7} {'偏差':>7} {'单位推力':>9}{'真值':>7}")
    print("-" * 106)
    rows = {}
    for key, spec in REAL.items():
        r, psi = fit_psi(spec, spec["opr"])
        if r is None or not r.ok:
            print(f"{spec['label']:<26} 求解失败: {(r.errors if r else ['psi 无解'])[0]}")
            continue
        rows[key] = (spec, r, psi)
        sfc = sfc_lb(r.sfc)
        err = (sfc - spec["sfc_lb"]) / spec["sfc_lb"] * 100.0
        fn_man = spec["thrust_kn"] * 1000.0 / spec["mdot"]
        print(f"{spec['label']:<26} {r.opr:>6.2f} {psi:>6.3f} {r.bpr:>6.2f} {r.T4:>6.0f} "
              f"{sfc:>7.3f} {spec['sfc_lb']:>7.3f} {err:>+6.1f}% {r.fn_per_mdot:>9.0f}{fn_man:>7.0f}")

    print("\n二、手册数据自洽性：由 (推力, 流量, SFC, BPR) 反推燃油比，看它跟 (OPR, T4) 能不能同时成立")
    print("   燃油比只由 T4−T3 决定，而 T3 由 OPR 决定。所以'手册 SFC 要求的燃油比'")
    print("   会反过来给出一个 T4 要求值；它跟手册 TIT 差多少，就是手册 TIT 的可信度。")
    print(f"{'机型':<26}{'far模型':>9}{'far手册':>9}{'比值':>7}"
          f"{'TIT手册':>9}{'T4要求':>8}{'差':>7}{'结论':>10}")
    print("-" * 86)
    for key, (spec, r, psi) in rows.items():
        mdot_core = spec["mdot"] / (1.0 + r.bpr)
        far_man = spec["sfc_lb"] * LB_LBF_H_TO_KG_N_H * spec["thrust_kn"] * 1000.0 / 3600.0 / mdot_core
        ratio = r.far / far_man
        # T3 由 OPR 定，燃油比由 T4−T3 定 → 反解出手册数据要求的 T4
        eta_b = 0.98
        t3 = r.T3
        t4_req = t3 + far_man * 43.0e6 * eta_b / 1004.5
        gap = t4_req - spec["t4"]
        verdict = "可用" if abs(ratio - 1.0) <= 0.12 else ("TIT偏低" if gap > 0 else "TIT偏高")
        print(f"{spec['label']:<26}{r.far:>9.5f}{far_man:>9.5f}{ratio:>7.2f}"
              f"{spec['t4']:>9.0f}{t4_req:>8.0f}{gap:>+7.0f}{verdict:>10}")

    print("\n三、尺度效应（间隙占比 + 叶片弦长雷诺数）对各级效率的折减")
    print(f"{'机型':<26}{'部件':<6}{'叶高mm':>8}{'间隙%':>8}{'Re':>10}{'η基准':>8}{'η实际':>8}{'共罚':>7}")
    print("-" * 76)
    for key, (spec, r, psi) in rows.items():
        for part in ("FAN", "LPC", "HPC", "HPT", "LPT"):
            info = r.scale.get(part)
            if not info:
                continue
            print(f"{spec['label']:<26}{part:<6}{info['h'] * 1000:>8.1f}"
                  f"{info['clr_over_h'] * 100:>8.2f}{info['re']:>10.0f}"
                  f"{info['eta_base']:>8.3f}{info['eta']:>8.3f}"
                  f"{(info['eta_base'] - info['eta']) * 100:>6.2f}%")


if __name__ == "__main__":
    main()