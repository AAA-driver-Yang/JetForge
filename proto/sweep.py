"""JetForge 原型的物理合理性扫描：确认旋钮的方向性是对的。"""
import sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

from jetforge import (EngineDesign, FanSpec, CompressorSpec, TurbineSpec,
                      BurnerSpec, AfterburnerSpec, TECH_PRESETS,
                      solve_design_point)


def base(**kw):
    d = EngineDesign(
        name="scan",
        kind="turbofan",
        tech=TECH_PRESETS["1970s"],
        inlet_diameter=1.0,
        fan=FanSpec(diameter=1.2, stages=1, hub_tip_ratio=0.42),
        lpc=CompressorSpec(stages=2, tip_diameter=0.62, shaft=1),
        hpc=CompressorSpec(stages=8, tip_diameter=0.50, shaft=2),
        hpt=TurbineSpec(stages=2, tip_diameter=0.55, shaft=2),
        lpt=TurbineSpec(stages=4, tip_diameter=0.62, shaft=1),
        burner=BurnerSpec(exit_temp=1500.0),
        shaft_count=2,
    )
    for k, v in kw.items():
        obj, attr = k.split("__")
        setattr(getattr(d, obj), attr, v)
    return d


def row(label, d):
    r = solve_design_point(d)
    flag = "OK " if r.ok else "ERR"
    # 转速读数按轴分配显示（单轴只显 N2，与 AJE 一致）
    sp = " ".join(f"{k}={v:5.0f}" for k, v in r.spool.items())
    print(f"{label:<26} {flag} OPR={r.opr:6.2f} BPR={r.bpr:5.2f} "
          f"F={r.thrust/1000:6.2f}kN SFC={r.sfc:.4f} "
          f"T5={r.T5:5.0f}K Vj={r.v_jet_core:4.0f} {sp}")
    for e in r.errors:
        print(f"    ✗ {e}")
    return r


print("=" * 110)
print("1. HPC 级数扫描（压比与推力如何随级数变化）")
print("=" * 110)
for n in (4, 6, 8, 10, 12, 14, 16):
    row(f"HPC {n} 级", base(hpc__stages=n))

print()
print("=" * 110)
print("2. 燃烧室出口温度扫描")
print("=" * 110)
for t in (1100, 1200, 1300, 1400, 1500, 1600):
    row(f"T4={t}K", base(burner__exit_temp=float(t)))

print()
print("=" * 110)
print("3. 风扇直径扫描（直径更大 → 转速更低 → 涵道比更大）")
print("=" * 110)
for dia in (0.9, 1.1, 1.3, 1.5, 1.7):
    row(f"风扇 D={dia}m", base(fan__diameter=dia))

print()
print("=" * 110)
print("4. HPC 叶尖直径扫描（直径大 → 转速低 → 单级做功少）")
print("=" * 110)
for dia in (0.40, 0.45, 0.50, 0.55, 0.60):
    row(f"HPC D={dia}m", base(hpc__tip_diameter=dia))

print()
print("=" * 110)
print("5. 加力")
print("=" * 110)
d0 = base()
row("干态", d0)
d1 = base()
d1.afterburner = AfterburnerSpec(enabled=True, exit_temp=1900.0)
row("加力 1900K", d1)

print()
print("=" * 110)
print("6. 涡喷 vs 涡扇对照（同一核心机）")
print("=" * 110)
jet = EngineDesign(
    name="turbojet", kind="turbojet", tech=TECH_PRESETS["1970s"],
    inlet_diameter=0.6,
    lpc=CompressorSpec(stages=4, tip_diameter=0.55, shaft=1),
    hpc=CompressorSpec(stages=8, tip_diameter=0.50, shaft=1),
    hpt=TurbineSpec(stages=2, tip_diameter=0.55, shaft=1),
    lpt=TurbineSpec(stages=0),
    burner=BurnerSpec(exit_temp=1500.0), shaft_count=1,
)
row("涡喷（单轴）", jet)

print()
print("=" * 110)
print("7. 三轴布局")
print("=" * 110)
d3 = base()
d3.shaft_count = 3
d3.fan_shaft = 1
d3.lpc_shaft = 2
d3.lpc.stages = 2
d3.lpc.shaft = 2
d3.hpc.shaft = 3
d3.hpt.shaft = 3
d3.lpt.shaft = 1
d3.lpt.stages = 4
# 三轴还需要一个 IPT 驱动 LPC，但模型目前只有 HPT/LPT 两台
row("三轴（缺 IPT）", d3)