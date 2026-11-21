"""
JetForge · 统一入口 + 性能网格 + 热力图
=======================================

这一层解决三件事：

1. **两种模式走同一个门**。玩家在编辑器里选"简单/残酷"，后面刷推力读数、
   飞行中查性能，用的都是同一组接口——上层不需要知道底下是六参数经验模型
   还是逐级求解。
2. **性能网格**。飞行中实时求解是不行的（一次非设计点求解几十次迭代），
   所以在地面/编辑器里把 (油门 × 高度 × 马赫) 预解成表，飞行中只做三线性插值。
   这和 KSP 原版 `FloatCurve` 的思路一致，只是多了油门一维。
3. **热力图**。把网格画成人能看懂的东西——推力在哪里被抽干了，一眼就能看出来。

关于精度：网格是**预先算好的**，插值误差取决于格距。高度用 1 km、马赫用 0.1、
油门用 0.1 时，线性插值误差通常在 2% 以内；跨音速区（M0.9~1.1）要密一点，
因为总压恢复在那里折得最快。
"""

from __future__ import annotations

import json
import math
import os
import time
from dataclasses import asdict, dataclass, field
from typing import Callable, Dict, List, Optional, Sequence, Tuple

import jetforge
import offdesign
import simple
from jetforge import EngineDesign, isa

MODE_SIMPLE = "simple"
MODE_BRUTAL = "brutal"


# ---------------------------------------------------------------------------
# 统一门面
# ---------------------------------------------------------------------------

class Engine:
    """一台发动机。design 可以是 EngineDesign（残酷）或 SimpleDesign（简单）。"""

    def __init__(self, design, mode: Optional[str] = None):
        if mode is None:
            mode = MODE_SIMPLE if isinstance(design, simple.SimpleDesign) else MODE_BRUTAL
        self.design = design
        self.mode = mode
        self._consts = None
        if mode == MODE_BRUTAL and not isinstance(design, EngineDesign):
            raise TypeError("残酷模式需要 EngineDesign")

    # ---- 设计点 ----
    def design_point(self):
        if self.mode == MODE_SIMPLE:
            return simple.design_point(self.design)
        return jetforge.solve_design_point(self.design)

    # ---- 任意状态 ----
    def at(self, alt: float, mach: float, throttle: float,
           ab_fuel_ratio: float = 0.0):
        if self.mode == MODE_SIMPLE:
            if self._consts is None:
                # 设计点常数（fc/ttr）在整张网格里是同一套，缓存住；
                # 每个点重算一次要多花一倍时间且数值完全一样。
                self._consts = simple.design_constants(self.design)
            return simple.solve(self.design, alt, mach, throttle,
                                ab_fuel_ratio=ab_fuel_ratio or None,
                                consts=self._consts)
        if self._consts is None:
            self._consts = offdesign.derive_design_constants(self.design)
        return offdesign.solve_offdesign(
            self.design, alt, mach, throttle, consts=self._consts,
            ab_fuel_ratio=ab_fuel_ratio)

    # ---- 判据 ----
    def check(self, res=None, alt: float = 0.0, p_amb: float = 0.0):
        import criteria
        if res is None:
            res = self.design_point()
        if p_amb <= 0.0:
            p_amb = isa(alt)[0]
        view = res.as_view() if hasattr(res, "as_view") else res
        return criteria.evaluate(self.design, view, p_amb=p_amb)

    # ---- 网格 ----
    def grid(self, alts: Sequence[float], machs: Sequence[float],
             throttles: Sequence[float], ab_fuel_ratio: float = 0.0,
             progress: Optional[Callable[[int, int], None]] = None) -> "PerformanceGrid":
        return build_grid(self, alts, machs, throttles, ab_fuel_ratio, progress)


def _thrust(r) -> float:
    return float(getattr(r, "net_thrust", 0.0) or 0.0)


def _speeds(engine, r) -> Tuple[float, float]:
    """把两种模式的转速读数统一成 (N1%, N2%)。

    简单模式直接给 n1_pct/n2_pct；残酷模式给的是 per-shaft 的 n_pct 字典，
    N1 是挂风扇那根轴，其余取最大的一根作 N2。三轴机这里只报 N2，
    N3 需要在网格里单独开一条轴，现在不做。
    """
    a = getattr(r, "n1_pct", None)
    if a is not None:
        return float(a), float(getattr(r, "n2_pct", 0.0) or 0.0)
    npct = getattr(r, "n_pct", None) or {}
    if not npct:
        return 0.0, 0.0
    fan = getattr(getattr(engine.design, "fan", None), "shaft", None)
    if fan in npct:
        others = [float(v) for k, v in npct.items() if k != fan]
        return float(npct[fan]), (max(others) if others else float(npct[fan]))
    vals = [float(v) for v in npct.values()]
    return max(vals), max(vals)


# ---------------------------------------------------------------------------
# 性能网格
# ---------------------------------------------------------------------------

@dataclass
class PerformanceGrid:
    """(油门, 高度, 马赫) 三轴表。t[i_thr][i_alt][i_mach]。"""
    throttle: List[float]
    alt: List[float]
    mach: List[float]
    thrust: List[List[List[float]]] = field(default_factory=list)
    sfc: List[List[List[float]]] = field(default_factory=list)
    mass_flow: List[List[List[float]]] = field(default_factory=list)
    fuel_flow: List[List[List[float]]] = field(default_factory=list)
    n1_pct: List[List[List[float]]] = field(default_factory=list)
    n2_pct: List[List[List[float]]] = field(default_factory=list)
    egt: List[List[List[float]]] = field(default_factory=list)
    valid: List[List[List[bool]]] = field(default_factory=list)
    mode: str = MODE_BRUTAL
    name: str = ""
    ab_fuel_ratio: float = 0.0
    built_at: str = ""
    build_seconds: float = 0.0

    # ---- 插值 ----
    def interp(self, alt: float, mach: float, throttle: float) -> dict:
        """三线性插值。越界按边界取值（KSP 里飞出网格不该变成 NaN）。"""
        t = _axis_weights(self.throttle, throttle)
        a = _axis_weights(self.alt, alt)
        m = _axis_weights(self.mach, mach)

        def blend(cube) -> float:
            total, wsum = 0.0, 0.0
            for it, wt in t:
                for ia, wa in a:
                    for im, wm in m:
                        w = wt * wa * wm
                        if w <= 0.0:
                            continue
                        total += w * cube[it][ia][im]
                        wsum += w
            return total / wsum if wsum > 0 else 0.0

        return {
            "thrust": blend(self.thrust),
            "sfc": blend(self.sfc),
            "mass_flow": blend(self.mass_flow),
            "fuel_flow": blend(self.fuel_flow),
            "n1_pct": blend(self.n1_pct),
            "n2_pct": blend(self.n2_pct),
            "egt": blend(self.egt),
        }

    # ---- 序列化 ----
    def to_json(self, path: str) -> None:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(asdict(self), f, ensure_ascii=False, indent=1)

    @classmethod
    def from_json(cls, path: str) -> "PerformanceGrid":
        with open(path, "r", encoding="utf-8") as f:
            d = json.load(f)
        return cls(**d)

    def to_csv(self, path: str) -> None:
        """平表：一行一个网格点。给 Excel / KSP ConfigNode 转换脚本用。"""
        cols = ["throttle", "alt", "mach", "thrust", "sfc", "mass_flow",
                "fuel_flow", "n1_pct", "n2_pct", "egt", "valid"]
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(",".join(cols) + "\n")
            for it, thr in enumerate(self.throttle):
                for ia, al in enumerate(self.alt):
                    for im, ma in enumerate(self.mach):
                        f.write(",".join([
                            f"{thr:.4f}", f"{al:.1f}", f"{ma:.3f}",
                            f"{self.thrust[it][ia][im]:.2f}",
                            f"{self.sfc[it][ia][im]:.6f}",
                            f"{self.mass_flow[it][ia][im]:.4f}",
                            f"{self.fuel_flow[it][ia][im]:.6f}",
                            f"{self.n1_pct[it][ia][im]:.2f}",
                            f"{self.n2_pct[it][ia][im]:.2f}",
                            f"{self.egt[it][ia][im]:.1f}",
                            "1" if self.valid[it][ia][im] else "0",
                        ]) + "\n")

    # ---- 体检 ----
    def summary(self) -> str:
        ok = sum(1 for it in range(len(self.throttle))
                 for ia in range(len(self.alt))
                 for im in range(len(self.mach)) if self.valid[it][ia][im])
        total = len(self.throttle) * len(self.alt) * len(self.mach)
        flat = [v for it in range(len(self.throttle))
                for ia in range(len(self.alt))
                for im in range(len(self.mach))
                if self.valid[it][ia][im] for v in [self.thrust[it][ia][im]]]
        peak = max(flat) if flat else 0.0
        best_sfc = min([v for it in range(len(self.throttle))
                        for ia in range(len(self.alt))
                        for im in range(len(self.mach))
                        if self.valid[it][ia][im] and self.thrust[it][ia][im] > 1000.0
                        for v in [self.sfc[it][ia][im]] if v > 0] or [0.0])
        return (f"{self.name or 'grid'} · {self.mode} · {total} 点，有效 {ok} "
                f"({ok / total * 100:.0f}%)，峰值推力 {peak / 1000:.1f} kN，"
                f"最低 SFC {best_sfc:.4f} kg/(N·h)，构建 {self.build_seconds:.1f} s")


def _axis_weights(axis: Sequence[float], x: float) -> List[Tuple[int, float]]:
    """在一条轴上找到 x 的包围区间，返回 [(索引, 权重), ...]（最多两项）。"""
    n = len(axis)
    if n == 0:
        return []
    if n == 1:
        return [(0, 1.0)]
    x = min(max(x, axis[0]), axis[-1])
    lo = 0
    for i in range(n - 1):
        if axis[i] <= x <= axis[i + 1]:
            lo = i
            break
    span = axis[lo + 1] - axis[lo]
    if span <= 0:
        return [(lo, 1.0)]
    f = (x - axis[lo]) / span
    return [(lo, 1.0 - f), (lo + 1, f)]


def build_grid(engine: Engine, alts: Sequence[float], machs: Sequence[float],
               throttles: Sequence[float], ab_fuel_ratio: float = 0.0,
               progress: Optional[Callable[[int, int], None]] = None) -> PerformanceGrid:
    g = PerformanceGrid(throttle=list(throttles), alt=list(alts), mach=list(machs),
                        mode=engine.mode,
                        name=getattr(engine.design, "name", ""),
                        ab_fuel_ratio=ab_fuel_ratio)
    nt, na, nm = len(g.throttle), len(g.alt), len(g.mach)
    zero = lambda: [[[0.0] * nm for _ in range(na)] for _ in range(nt)]
    false = lambda: [[[False] * nm for _ in range(na)] for _ in range(nt)]
    g.thrust, g.sfc, g.mass_flow = zero(), zero(), zero()
    g.fuel_flow, g.n1_pct, g.n2_pct, g.egt = zero(), zero(), zero(), zero()
    g.valid = false()

    total = nt * na * nm
    done = 0
    t0 = time.time()
    for it, thr in enumerate(g.throttle):
        for ia, al in enumerate(g.alt):
            for im, ma in enumerate(g.mach):
                r = engine.at(al, ma, thr, ab_fuel_ratio=ab_fuel_ratio)
                ok = bool(getattr(r, "ok", True)) and _thrust(r) > 1.0
                g.valid[it][ia][im] = ok
                if ok:
                    g.thrust[it][ia][im] = _thrust(r)
                    g.sfc[it][ia][im] = float(getattr(r, "sfc", 0.0))
                    g.mass_flow[it][ia][im] = float(getattr(r, "mdot_air", 0.0))
                    g.fuel_flow[it][ia][im] = float(getattr(r, "fuel_flow", 0.0))
                    n1, n2 = _speeds(engine, r)
                    g.n1_pct[it][ia][im] = n1
                    g.n2_pct[it][ia][im] = n2
                    g.egt[it][ia][im] = float(getattr(r, "egt", getattr(r, "T5", 0.0)))
                done += 1
                if progress:
                    progress(done, total)
    g.build_seconds = time.time() - t0
    g.built_at = time.strftime("%Y-%m-%d %H:%M:%S")
    return g


# ---------------------------------------------------------------------------
# 热力图（只用 PIL，不依赖 matplotlib）
# ---------------------------------------------------------------------------

def _jet(v: float) -> Tuple[int, int, int]:
    """0→1 映射到蓝-青-绿-黄-红。"""
    v = min(max(v, 0.0), 1.0)
    if v < 0.25:
        f = v / 0.25
        return (0, int(255 * f), 255)
    if v < 0.5:
        f = (v - 0.25) / 0.25
        return (0, 255, int(255 * (1 - f)))
    if v < 0.75:
        f = (v - 0.5) / 0.25
        return (int(255 * f), 255, 0)
    f = (v - 0.75) / 0.25
    return (255, int(255 * (1 - f)), 0)


def _font(size: int = 12):
    from PIL import ImageFont
    for path in (r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\msyhbd.ttc",
                 r"C:\Windows\Fonts\simhei.ttf", r"C:\Windows\Fonts\arial.ttf"):
        try:
            return ImageFont.truetype(path, size)
        except Exception:
            continue
    return ImageFont.load_default()


def render_heatmaps(grid: PerformanceGrid, out_path: str,
                    quantity: str = "thrust",
                    title: Optional[str] = None) -> str:
    """每个油门一张子图（横轴马赫、纵轴高度、颜色=所选量）。"""
    from PIL import Image, ImageDraw

    cube = {"thrust": grid.thrust, "sfc": grid.sfc,
            "mass_flow": grid.mass_flow, "egt": grid.egt}[quantity]
    label = {"thrust": "推力 kN", "sfc": "SFC kg/(N·h)",
             "mass_flow": "进气量 kg/s", "egt": "EGT K"}[quantity]
    scale = 1.0 / 1000.0 if quantity == "thrust" else 1.0

    nt, na, nm = len(grid.throttle), len(grid.alt), len(grid.mach)
    panel_w, panel_h = 210, 150
    pad_l, pad_b, pad_t, pad_gap = 46, 34, 34, 26
    cbar_w = 16
    W = pad_l + nt * (panel_w + pad_gap) + 60 + cbar_w + 26
    H = pad_t + panel_h + pad_b + 30
    img = Image.new("RGB", (W, H), (18, 18, 20))
    dr = ImageDraw.Draw(img)
    f_small, f_tiny = _font(11), _font(10)

    # 全网格统一色标，才能横向比较不同油门
    vals = [cube[it][ia][im] for it in range(nt) for ia in range(na)
            for im in range(nm) if grid.valid[it][ia][im]]
    lo, hi = (min(vals), max(vals)) if vals else (0.0, 1.0)

    for it, thr in enumerate(grid.throttle):
        x0 = pad_l + it * (panel_w + pad_gap)
        y0 = pad_t
        cw = panel_w / max(nm - 1, 1)
        ch = panel_h / max(na - 1, 1)
        for ia in range(na):
            for im in range(nm):
                if not grid.valid[it][ia][im]:
                    px0 = x0 + im * cw
                    py0 = y0 + panel_h - (ia + 1) * ch
                    dr.rectangle([px0, py0, px0 + cw + 1, py0 + ch + 1],
                                 fill=(60, 30, 30))
                    continue
                v = cube[it][ia][im] * scale
                t = (v - lo * scale) / max((hi - lo) * scale, 1e-9)
                col = _jet(t)
                px0 = x0 + im * cw
                py0 = y0 + panel_h - (ia + 1) * ch
                dr.rectangle([px0, py0, px0 + cw + 1, py0 + ch + 1], fill=col)
        dr.rectangle([x0, y0, x0 + panel_w, y0 + panel_h], outline=(110, 110, 120))
        dr.text((x0 + 4, y0 - 18), f"油门 {thr * 100:.0f}%", font=f_small,
                fill=(230, 230, 235))
        # 轴标注只给第一张
        if it == 0:
            for im in range(0, nm, max(nm // 5, 1)):
                dr.text((x0 + im * cw - 4, y0 + panel_h + 4), f"{grid.mach[im]:.1f}",
                        font=f_tiny, fill=(180, 180, 190))
            for ia in range(0, na, max(na // 5, 1)):
                dr.text((x0 - 30, y0 + panel_h - (ia + 0.5) * ch - 4),
                        f"{grid.alt[ia] / 1000:.0f}", font=f_tiny, fill=(180, 180, 190))
            dr.text((x0 - 34, y0 - 18), "高度 km", font=f_tiny, fill=(180, 180, 190))

    # 横轴在所有子图下方居中，说明这是共用轴
    if nt > 0:
        mid_x = pad_l + (nt * (panel_w + pad_gap) - pad_gap) // 2
        dr.text((mid_x - 12, pad_t + panel_h + 20), "马赫", font=f_tiny,
                fill=(180, 180, 190))

    # 色标
    cx = pad_l + nt * (panel_w + pad_gap) + 14
    for i in range(panel_h):
        t = 1.0 - i / panel_h
        dr.line([(cx, pad_t + i), (cx + cbar_w, pad_t + i)], fill=_jet(t))
    dr.rectangle([cx, pad_t, cx + cbar_w, pad_t + panel_h], outline=(110, 110, 120))
    dr.text((cx - 6, pad_t - 16), f"{hi * scale:.2f}", font=f_tiny, fill=(200, 200, 210))
    dr.text((cx - 6, pad_t + panel_h + 2), f"{lo * scale:.2f}", font=f_tiny,
            fill=(200, 200, 210))
    dr.text((cx - 34, pad_t + panel_h + 18), label, font=f_tiny, fill=(200, 200, 210))

    head = title or f"{grid.name or 'engine'} · {grid.mode} · {label}"
    dr.text((pad_l, 8), head, font=_font(13), fill=(240, 240, 245))
    dr.text((pad_l, H - 18),
            "暗红格 = 该状态求解失败或推力 ≤ 0（进发匹配不上/涡轮带不动/喘振）",
            font=f_tiny, fill=(200, 140, 140))
    img.save(out_path)
    return out_path


# ---------------------------------------------------------------------------
# 自检：两种模式交叉验证 + 网格 + 热力图
# ---------------------------------------------------------------------------

def _demo():
    try:
        import sys
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    from jetforge import (BurnerSpec, CompressorSpec, FanSpec, TECH_PRESETS,
                          TurbineSpec)

    # 同一台机器：残酷模式按几何搭，简单模式只给六个等效参数
    brutal = EngineDesign(
        name="JF-1", kind="turbofan", tech=TECH_PRESETS["1970s"],
        inlet_diameter=1.2,
        fan=FanSpec(diameter=1.2, stages=1, hub_tip_ratio=0.42),
        lpc=CompressorSpec(stages=2, tip_diameter=0.62, shaft=1, hub_tip_ratio=0.62),
        hpc=CompressorSpec(stages=8, tip_diameter=0.50, shaft=2, hub_tip_ratio=0.70),
        hpt=TurbineSpec(stages=1, tip_diameter=0.55, shaft=2),
        lpt=TurbineSpec(stages=3, tip_diameter=0.62, shaft=1),
        burner=BurnerSpec(exit_temp=1500.0), shaft_count=2)

    eb = Engine(brutal)
    rb = eb.design_point()
    es = Engine(simple.SimpleDesign(
        name="JF-1 (simple)", kind="turbofan", diameter=1.2,
        bpr=rb.bpr, opr=rb.opr, t4=1500.0, tech=TECH_PRESETS["1970s"]))
    rs = es.design_point()

    print("设计点交叉验证")
    print(f"{'':<10}{'推力 kN':>10}{'SFC':>9}{'进气 kg/s':>11}{'FPR':>7}{'涵道比':>8}")
    print(f"{'残酷':<10}{rb.thrust / 1000:>10.1f}{rb.sfc:>9.4f}{rb.mdot_air:>11.1f}"
          f"{rb.fpr:>7.2f}{rb.bpr:>8.2f}")
    print(f"{'简单':<10}{rs.net_thrust / 1000:>10.1f}{rs.sfc:>9.4f}{rs.mdot_air:>11.1f}"
          f"{rs.fpr:>7.2f}{rs.bpr:>8.2f}")
    print(f"{'偏差':<10}{(rs.net_thrust / rb.thrust - 1) * 100:>9.1f}%"
          f"{(rs.sfc / rb.sfc - 1) * 100:>8.1f}%"
          f"{(rs.mdot_air / rb.mdot_air - 1) * 100:>10.1f}%")

    # 飞行剖面上的偏差
    print("\n飞行剖面（满油门）")
    print(f"{'状态':<16}{'残酷 kN':>10}{'简单 kN':>10}{'偏差':>9}")
    for alt in (0.0, 6000.0, 11000.0):
        for mach in (0.0, 0.6, 0.85):
            a = eb.at(alt, mach, 1.0)
            b = es.at(alt, mach, 1.0)
            fa, fb = _thrust(a), _thrust(b)
            dev = (fb / fa - 1) * 100 if fa > 1 else 0.0
            print(f"{f'{alt / 1000:.0f} km / M{mach:.2f}':<16}"
                  f"{fa / 1000:>10.1f}{fb / 1000:>10.1f}{dev:>8.1f}%")

    # 网格 + 热力图（用小网格，能看到形状就够）
    print("\n构建性能网格 …")
    alts = [0.0, 3000.0, 6000.0, 9000.0, 12000.0, 15000.0]
    machs = [0.0, 0.2, 0.4, 0.6, 0.8, 1.0, 1.2, 1.5]
    throttles = [0.3, 0.5, 0.7, 0.85, 1.0]
    t0 = time.time()
    g = eb.grid(alts, machs, throttles)
    print("  " + g.summary())
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "out")
    os.makedirs(out, exist_ok=True)
    p1 = render_heatmaps(g, os.path.join(out, "thrust_map.png"), "thrust")
    p2 = render_heatmaps(g, os.path.join(out, "sfc_map.png"), "sfc")
    print(f"  热力图 → {os.path.abspath(p1)}")
    print(f"  热力图 → {os.path.abspath(p2)}")

    # 插值一致性：网格内插 vs 现场重算
    print("\n插值一致性（网格内插 vs 直接求解）")
    for alt, mach, thr in ((0.0, 0.3, 0.7), (6000.0, 0.7, 0.9), (11000.0, 0.5, 1.0)):
        r = eb.at(alt, mach, thr)
        i = g.interp(alt, mach, thr)
        f = _thrust(r)
        print(f"  {alt / 1000:.0f} km M{mach:.2f} 油门{thr:.0%}: 直接 {f / 1000:.2f} kN，"
              f"内插 {i['thrust'] / 1000:.2f} kN，偏差 {(i['thrust'] / f - 1) * 100:+.1f}%")


if __name__ == "__main__":
    _demo()