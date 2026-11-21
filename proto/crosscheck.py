"""两模式交叉验证 + 网格/热力图。

用法：python crosscheck.py
产出：out/{brutal,simple}_{thrust,sfc}.png（相对仓库根）
"""
import os
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

import criteria
import engine as E
import simple as S
from jetforge import (BurnerSpec, CompressorSpec, EngineDesign, FanSpec,
                      TECH_PRESETS, TurbineSpec)

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "out")

ALTS = [0.0, 3000.0, 6000.0, 9000.0, 12000.0, 15000.0]
MACHS = [0.0, 0.2, 0.4, 0.6, 0.8, 0.9, 1.0, 1.2, 1.5]
THRS = [0.3, 0.5, 0.7, 0.85, 1.0]


def build_pair(tech_name="1970s"):
    tech = TECH_PRESETS[tech_name]
    b = EngineDesign(
        name="JF-1", kind="turbofan", tech=tech, inlet_diameter=1.2,
        fan=FanSpec(diameter=1.2, stages=1, hub_tip_ratio=0.42),
        lpc=CompressorSpec(stages=2, tip_diameter=0.62, shaft=1, hub_tip_ratio=0.62),
        hpc=CompressorSpec(stages=8, tip_diameter=0.50, shaft=2, hub_tip_ratio=0.70),
        hpt=TurbineSpec(stages=1, tip_diameter=0.55, shaft=2),
        lpt=TurbineSpec(stages=3, tip_diameter=0.62, shaft=1),
        burner=BurnerSpec(exit_temp=1500.0), shaft_count=2)
    eb = E.Engine(b)
    rb = eb.design_point()
    s = S.SimpleDesign(name="JF-1 (simple)", kind="turbofan", diameter=1.2,
                       bpr=rb.bpr, opr=rb.opr, t4=1500.0, tech=tech)
    return eb, E.Engine(s), rb


def main():
    eb, es, rb = build_pair()
    rs = es.design_point()

    print("== 设计点 ==")
    print(f"{'':<6}{'推力 kN':>10}{'SFC':>9}{'流量 kg/s':>11}{'OPR':>8}{'FPR':>7}{'T5 K':>8}")
    for tag, r in (("残酷", rb), ("简单", rs)):
        f = (getattr(r, "net_thrust", 0.0) or r.thrust) / 1000.0
        print(f"{tag:<6}{f:>10.1f}{r.sfc:>9.4f}{r.mdot_air:>11.1f}"
              f"{r.opr:>8.2f}{r.fpr:>7.2f}{r.T5:>8.0f}")
    print(f"偏差 {(rs.net_thrust / rb.thrust - 1) * 100:+.1f}% "
          f"{(rs.sfc / rb.sfc - 1) * 100:+.1f}% "
          f"{(rs.mdot_air / rb.mdot_air - 1) * 100:+.1f}%")

    print("\n== 飞行剖面（满油门）==")
    print(f"{'状态':<15}{'残酷 kN':>10}{'简单 kN':>10}{'偏差':>9}"
          f"{'残酷判据':>10}{'简单判据':>10}")
    for alt, ma in ((0, 0.0), (0, 0.6), (0, 0.85), (6000, 0.0), (6000, 0.6),
                    (6000, 0.85), (11000, 0.0), (11000, 0.6), (11000, 0.85)):
        a = eb.at(alt, ma, 1.0)
        d = es.at(alt, ma, 1.0)
        fa = (a.net_thrust or a.thrust) / 1000.0
        fd = d.net_thrust / 1000.0
        ca = criteria.evaluate(eb.design, a, alt=alt)
        cd = criteria.evaluate(es.design, d, alt=alt)
        tag = "FAIL" if (ca.fails or cd.fails) else \
            ("WARN" if (ca.warns or cd.warns) else "OK")
        print(f"{f'{alt / 1000:.0f} km M{ma:.2f}':<15}{fa:>10.1f}{fd:>10.1f}"
              f"{(fd / fa - 1) * 100:>8.1f}%{len(ca.findings):>10}{len(cd.findings):>10}"
              f"  {tag}")

    print("\n== 高原因子诊断 ==")
    a = eb.at(11000.0, 0.0, 1.0)
    theta = a.T1 / 288.15
    print(f"  11 km：T1 {a.T1:.1f} K，θ {theta:.4f}，"
          f"N1 {a.n1_pct:.1f}% N2 {a.n2_pct:.1f}%，"
          f"换算 N1 {a.n1_pct / theta ** 0.5:.0f}% N2 {a.n2_pct / theta ** 0.5:.0f}%")
    ca = criteria.evaluate(eb.design, a)
    for f in ca.fails + ca.warns:
        print(f"    [{f.level}] {f.message}")

    print("\n== 海平面设计点判据（残酷）==")
    for f in criteria.evaluate(eb.design, rb).fails:
        print(f"    [fail] {f.message}")

    print("\n== 网格 ==")
    for tag, e in (("残酷", eb), ("简单", es)):
        t0 = time.time()
        g = e.grid(ALTS, MACHS, THRS)
        p1 = E.render_heatmaps(g, os.path.join(OUT, f"{'brutal' if tag == '残酷' else 'simple'}_thrust.png"), "thrust")
        p2 = E.render_heatmaps(g, os.path.join(OUT, f"{'brutal' if tag == '残酷' else 'simple'}_sfc.png"), "sfc")
        print(f"  {tag}: {g.summary()}")
        print(f"        → {os.path.basename(p1)} / {os.path.basename(p2)}")
        # 内插一致性
        for alt, ma, thr in ((0.0, 0.3, 0.7), (6000.0, 0.7, 0.9), (11000.0, 0.5, 1.0)):
            r = e.at(alt, ma, thr)
            i = g.interp(alt, ma, thr)
            f = (getattr(r, "net_thrust", 0.0) or r.thrust) / 1000.0
            print(f"        {alt / 1000:.0f}km M{ma:.2f} 油门{thr:.0%}: "
                  f"直接 {f:.2f} / 内插 {i['thrust'] / 1000:.2f} kN "
                  f"({(i['thrust'] / 1000 / f - 1) * 100:+.1f}%)")

    print("\n== 网格持久化往返 ==")
    g = eb.grid(ALTS, MACHS, THRS)
    tmp = os.path.join(OUT, "_grid_roundtrip.json")
    g.to_json(tmp)
    g2 = E.PerformanceGrid.from_json(tmp)
    worst = 0.0
    for alt, ma, thr in ((0.0, 0.3, 0.7), (6000.0, 0.7, 0.9), (11000.0, 0.5, 1.0),
                         (15000.0, 1.2, 0.5)):
        x = g.interp(alt, ma, thr)["thrust"]
        y = g2.interp(alt, ma, thr)["thrust"]
        worst = max(worst, abs(y / max(x, 1e-9) - 1))
    size = os.path.getsize(tmp)
    print(f"  JSON {size / 1024:.0f} KB，往返后最大内插偏差 {worst * 100:.2f}%")
    os.remove(tmp)
    g.to_csv(os.path.join(OUT, "grid_sample.csv"))

    print("\n== 判据在三种构型上不漏字段 ==")
    cases = [
        ("双轴涡扇", es.design),
        ("单轴涡喷", S.SimpleDesign(name="tj", kind="turbojet", diameter=0.74,
                                 bpr=0.0, opr=13.5, t4=1210.0,
                                 tech=TECH_PRESETS["1960s"])),
        ("三轴涡扇", S.SimpleDesign(name="3s", kind="turbofan", diameter=1.3,
                                 bpr=5.0, opr=30.0, t4=1550.0,
                                 t_exhaust=1900.0, tech=TECH_PRESETS["1990s"])),
    ]
    for name, d in cases:
        e = E.Engine(d)
        r = e.at(3000.0, 0.5, 0.9)
        rep = criteria.evaluate(d, r)
        nm = [f for f in rep.findings if f.key.endswith(".ncor")]
        print(f"  {name}: {len(rep.findings)} 条判据，"
              f"换算转速项 {[f.part for f in nm]}，判定 {rep.worst}")
        if rep.fails:
            for f in rep.fails[:3]:
                print(f"      [fail] {f.message}")


if __name__ == "__main__":
    main()