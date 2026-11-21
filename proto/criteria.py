"""
JetForge · 判据层（criteria）
==============================

求解器回答"这台机器能出多少推力"，判据层回答"这台机器能不能真的转起来、安不安全、
离极限还有多远"。两者分开写，是因为判据要能在**任何**求解结果上跑（设计点、非设计点、
玩家手改的中间态），而不是绑死在某个求解器里。

判据分四类：
  1. 气动稳定性  —— 流量系数 φ 距失速边界多远（喘振裕度）、叶尖马赫
  2. 结构与热    —— 转速上限、T3 出口温度、T4 涡轮入口温度（含冷却与否）、排气温度
  3. 燃烧        —— 贫油熄火、富油熄火、加力温度上限
  4. 总体        —— 推力为正、喷管压比、轴功平衡、涡轮级数

**诚实声明**：φ 判据是"静稳裕度的代理指标"，不是真实的压气机特性线。
真实喘振裕度要在 (换算流量, 压比) 平面的喘振线上量，我们没有那根线。
φ = Ca/U 与失速的关联是有物理依据的（级负荷越重、轴向速度越低，越容易失速），
但这里给出的百分比只用于**排序和量级**，不要当工程精度用。
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Dict, List, Optional

from jetforge import (
    CP_AIR, NPR_CRIT, R_AIR, a_sound, annulus_height, cp_of, gamma_of,
    total_from_static, isa,
)

OK, WARN, FAIL = "ok", "warn", "fail"
_RANK = {OK: 0, WARN: 1, FAIL: 2}
T0_SL = 288.15


@dataclass
class Finding:
    key: str
    level: str
    message: str
    value: float = 0.0
    limit: float = 0.0
    unit: str = ""
    part: str = ""

    @property
    def margin_frac(self) -> float:
        """相对限值的裕度。正数=还有余量，负数=已超限。"""
        if self.limit == 0.0:
            return 0.0
        return (self.limit - self.value) / abs(self.limit)


@dataclass
class CheckReport:
    findings: List[Finding] = field(default_factory=list)

    def add(self, f: Finding) -> None:
        self.findings.append(f)

    def extend(self, fs: List[Finding]) -> None:
        self.findings.extend(fs)

    @property
    def worst(self) -> str:
        w = OK
        for f in self.findings:
            if _RANK[f.level] > _RANK[w]:
                w = f.level
        return w

    @property
    def ok(self) -> bool:
        return self.worst != FAIL

    @property
    def fails(self) -> List[Finding]:
        return [f for f in self.findings if f.level == FAIL]

    @property
    def warns(self) -> List[Finding]:
        return [f for f in self.findings if f.level == WARN]

    def by_level(self) -> Dict[str, List[Finding]]:
        out: Dict[str, List[Finding]] = {OK: [], WARN: [], FAIL: []}
        for f in self.findings:
            out[f.level].append(f)
        return out

    def lines(self, show_ok: bool = True) -> List[str]:
        tag = {OK: "  ok  ", WARN: " warn ", FAIL: " FAIL "}
        order = {FAIL: 0, WARN: 1, OK: 2}
        fs = sorted(self.findings, key=lambda f: (order[f.level], f.key))
        out = []
        for f in fs:
            if f.level == OK and not show_ok:
                continue
            out.append(f"[{tag[f.level]}] {f.message}")
        return out


# ---------------------------------------------------------------------------
# 气动：流量系数与喘振裕度
# ---------------------------------------------------------------------------

def axial_velocity(mdot: float, T: float, p: float, tip_diameter: float,
                   hub_tip_ratio: float) -> float:
    """轴向速度 Ca = ṁ/(ρ·A_annulus)。用总温总压近似静密度，偏高几个百分点。"""
    area = math.pi / 4.0 * tip_diameter ** 2 * max(1.0 - hub_tip_ratio ** 2, 1e-6)
    if area <= 0.0 or mdot <= 0.0:
        return 0.0
    rho = p / (R_AIR * max(T, 1.0))
    return mdot / max(rho * area, 1e-9)


def flow_coefficient(mdot: float, T: float, p: float, tip_diameter: float,
                     hub_tip_ratio: float, U: float) -> float:
    """流量系数 φ = Ca/U。轴流压气机的设计值 0.4~0.6，跌破 ~0.35 就进失速区。"""
    if U <= 0.0:
        return 0.0
    return axial_velocity(mdot, T, p, tip_diameter, hub_tip_ratio) / U


def check_rotor(name: str, mdot: float, T: float, p: float,
                tip_diameter: float, hub_tip_ratio: float, U: float,
                n_pct: float, tech, kind: str = "comp") -> List[Finding]:
    """一片转子的转速/叶尖马赫/喘振裕度。kind: comp / fan / turb"""
    out: List[Finding] = []
    if U <= 0.0 or tip_diameter <= 0.0:
        return out

    mtip = U / a_sound(T)
    m_lim = {"comp": tech.max_tip_mach_comp,
             "fan": tech.max_tip_mach_fan,
             "turb": tech.max_tip_mach_turb}[kind]
    # 判据要用**叶尖线速度**，不是当地叶尖马赫：设计转速是按海平面标准声速定的，
    # 到了 11 km 声速掉到 295 m/s，同一个 U 的"当地马赫"自然涨到 1.4——那是真实的，
    # 不是设计缺陷。拿当地马赫去比设计限值，只会在高空刷出一片假报警。
    u_lim = m_lim * a_sound(T0_SL)
    lvl = FAIL if U > u_lim * 1.10 else (WARN if U > u_lim else OK)
    out.append(Finding(
        f"{name}.tip_speed", lvl,
        f"{name} 叶尖线速度 {U:.0f} m/s（设计上限 {u_lim:.0f}，当地叶尖马赫 {mtip:.2f}）",
        U, u_lim, "m/s", name))

    if n_pct > 0.0:
        lvl = FAIL if n_pct > tech.overspeed_limit * 100.0 * 1.02 else \
            (WARN if n_pct > tech.overspeed_limit * 100.0 else OK)
        out.append(Finding(
            f"{name}.overspeed", lvl,
            f"{name} 转速 {n_pct:.0f}%（超转线 {tech.overspeed_limit * 100:.0f}%）",
            n_pct, tech.overspeed_limit * 100.0, "%", name))

    if kind == "turb":
        return out

    phi = flow_coefficient(mdot, T, p, tip_diameter, hub_tip_ratio, U)
    phi_stall = tech.stall_phi_fan if kind == "fan" else tech.stall_phi
    sm = phi / phi_stall - 1.0
    need = tech.surge_margin_req
    if sm < 0.0:
        lvl, note = FAIL, "已进失速区"
    elif sm < need:
        lvl, note = WARN, "裕度不足"
    else:
        lvl, note = OK, ""
    out.append(Finding(
        f"{name}.surge", lvl,
        f"{name} 喘振裕度 {sm * 100:+.0f}%（φ={phi:.2f}，失速 φ≈{phi_stall:.2f}，"
        f"要求 ≥{need * 100:.0f}%）" + (f" {note}" if note else ""),
        sm * 100.0, need * 100.0, "%", name))

    # 反向也要查：φ 过大说明流道相对流量太富，轴向速度高、相对马赫高、攻角偏负，
    # 效率掉、可用裕度也被吃光。这不是失速，但同样是"设计得不对"。
    phi_hi = tech.phi_high_fan if kind == "fan" else tech.phi_high_comp
    if phi > phi_hi:
        m_ax = axial_velocity(mdot, T, p, tip_diameter, hub_tip_ratio) / a_sound(T)
        out.append(Finding(
            f"{name}.phi_high", WARN,
            f"{name} 流量系数 φ={phi:.2f} 偏大（常见 {phi_stall:.2f}~{phi_hi:.2f}），"
            f"轴向马赫 {m_ax:.2f}：流道对这个流量偏窄",
            phi, phi_hi, "-", name))
    return out


# ---------------------------------------------------------------------------
# 温度/燃烧/喷管
# ---------------------------------------------------------------------------

def check_temperatures(view, tech) -> List[Finding]:
    out: List[Finding] = []
    T3, T4, T5, T7 = view.get("T3", 0.0), view.get("T4", 0.0), \
        view.get("T5", 0.0), view.get("T7", 0.0)

    if T3 > 0.0:
        lvl = FAIL if T3 > tech.t3_limit * 1.05 else (WARN if T3 > tech.t3_limit else OK)
        out.append(Finding(
            "T3", lvl, f"压气机出口 {T3:.0f} K（盘/叶片材料限 {tech.t3_limit:.0f} K）",
            T3, tech.t3_limit, "K"))

    if T4 > 0.0:
        cooled = getattr(view, "turbine_cooled", True) if hasattr(view, "turbine_cooled") else True
        lim = tech.t4_limit if cooled else min(tech.t4_limit, tech.t4_uncooled_limit)
        lvl = FAIL if T4 > lim * 1.02 else (WARN if T4 > lim else OK)
        note = "" if cooled else "，无冷却叶片"
        out.append(Finding(
            "T4", lvl, f"涡轮入口 {T4:.0f} K（上限 {lim:.0f} K{note}）", T4, lim, "K"))

    egt_lim = tech.egt_ratio * tech.t4_limit
    if T5 > 0.0:
        lvl = FAIL if T5 > egt_lim * 1.05 else (WARN if T5 > egt_lim else OK)
        out.append(Finding(
            "EGT", lvl, f"排气温度 {T5:.0f} K（上限 {egt_lim:.0f} K）", T5, egt_lim, "K"))

    if T7 > 0.0 and view.get("far_ab", 0.0) > 0.0:
        from jetforge import _ab_max_temp
        lim = _ab_max_temp(tech)
        lvl = FAIL if T7 > lim * 1.02 else (WARN if T7 > lim else OK)
        out.append(Finding(
            "T7", lvl, f"加力出口 {T7:.0f} K（上限 {lim:.0f} K）", T7, lim, "K"))
    return out


def check_combustion(view, tech) -> List[Finding]:
    out: List[Finding] = []
    far = view.get("far", 0.0)
    if far > 0.0:
        if far < tech.far_lbo:
            out.append(Finding("LBO", FAIL,
                               f"主燃油气比 {far:.4f} 低于贫油熄火线 {tech.far_lbo:.4f}：会熄火",
                               far, tech.far_lbo, "-"))
        elif far > tech.far_rich:
            out.append(Finding("rich", FAIL,
                               f"主燃油气比 {far:.4f} 超过富油线 {tech.far_rich:.4f}：冒烟/熄火",
                               far, tech.far_rich, "-"))
        else:
            out.append(Finding("far", OK,
                               f"主燃油气比 {far:.4f}（熄火区间 {tech.far_lbo:.4f}~{tech.far_rich:.4f}）",
                               far, tech.far_rich, "-"))
    return out


def check_nozzle(view, design, tech) -> List[Finding]:
    out: List[Finding] = []
    p0 = view.get("p_amb", 0.0)
    p5 = view.get("P5", 0.0)
    if p0 > 0.0 and p5 > 0.0:
        npr = p5 / p0
        if npr < 1.0:
            out.append(Finding("nozzle.reverse", FAIL,
                               f"核心喷管压比 {npr:.2f} < 1：出口倒流", npr, 1.0, "-"))
        else:
            kind = getattr(design, "nozzle_kind", "converging")
            lim = tech.nozzle_npr_fixed_max if kind == "converging" else 1e9
            lvl = WARN if npr > lim else OK
            msg = f"核心喷管压比 {npr:.2f}"
            if lvl == WARN:
                msg += f"（固定收敛喷管超过 {lim:.0f} 后欠膨胀损失明显，需要可变面积）"
            out.append(Finding("nozzle.npr", lvl, msg, npr, lim, "-"))
    return out


def check_shafts(design, view) -> List[Finding]:
    """每根轴的涡轮功 vs 压气机功。只做体检，真正的功平衡在求解器里。"""
    out: List[Finding] = []
    rpm = view.get("rpm") or {}
    if not rpm:
        return out
    # 非设计点结果没有逐级分量（只有总 W_comp/W_fan），
    # 硬算会把每根轴都报成"涡轮 0 MW 带不动负载"——那是假警报。
    if not view.get("_has_turbine_split", False):
        out.append(Finding("shafts.skipped", OK,
                           "非设计点结果未给逐级功分量，轴平衡判据已跳过"))
        return out
    for s in sorted(rpm):
        need = 0.0
        have = 0.0
        if design.fan.diameter > 0 and design.fan.shaft == s:
            need += view.get("W_fan", 0.0)
        if design.lpc.stages > 0 and design.lpc.shaft == s:
            need += _share(design, view, "lpc")
        if design.hpc.stages > 0 and design.hpc.shaft == s:
            need += _share(design, view, "hpc")
        for name, spec, key in (("HPT", design.hpt, "W_turb_hpt"),
                                ("IPT", design.ipt, "W_turb_ipt"),
                                ("LPT", design.lpt, "W_turb_lpt")):
            if spec.stages > 0 and spec.shaft == s:
                have += view.get(key, 0.0)
        if need > 1.0 or have > 1.0:
            lvl = OK if have >= need * 0.98 else FAIL
            out.append(Finding(
                f"shaft{s}.balance", lvl,
                f"轴 {s}：涡轮 {have / 1e6:.2f} MW vs 负载 {need / 1e6:.2f} MW",
                have / 1e6, need / 1e6, "MW"))
    return out


def check_corrected_speed(view, tech) -> List[Finding]:
    """换算转速 N/√θ 判据。

    压气机特性线是拿换算转速横轴画的，物理转速 100% 在高原意味着换算转速超标，
    叶片相对马赫数与离心应力都按换算值走。

    **θ 用的是什么温度很要紧**：风扇的基准是风扇进口总温（≈来流总温 Tt1），
    核心压气机的基准是核心进口总温（风扇加热之后的 Tt2）。拿静温去算会把
    11 km 的换算转速虚报成 115%，凭空刷出 FAIL——所以这里必须用结果里的 T1/T2。
    """
    out: List[Finding] = []
    t_in = view.get("T1", 0.0) or 0.0
    t_core = view.get("T2", 0.0) or 0.0
    if t_in <= 0.0:
        return out
    if t_core <= 0.0:
        t_core = t_in
    th_fan = t_in / T0_SL
    th_core = t_core / T0_SL
    if th_fan <= 0.05:
        return out
    lim = tech.overspeed_limit * 100.0
    pairs = [("N1", view.get("n1_pct", 0.0)), ("N2", view.get("n2_pct", 0.0))]
    npct = view.get("n_pct") or {}
    if isinstance(npct, dict) and npct:
        # 轴号不是 N 号：双轴机风扇在 1 轴 → N1，另一根叫 N2（不是 N3）。
        # 风扇轴排第一，其余按轴号顺序依次 N2、N3…
        fan = view.get("_fan_shaft")
        shafts = sorted(npct)
        ordered = ([fan] if fan in npct else []) + \
                  [s for s in shafts if s != fan]
        pairs = [(f"N{i + 1}", npct[s]) for i, s in enumerate(ordered)]
    elif isinstance(npct, dict):
        pass
    for i, (name, n) in enumerate(pairs):
        if not n:
            continue
        theta = th_fan if i == 0 else th_core
        ncor = n / math.sqrt(theta)
        lvl = FAIL if ncor > lim * 1.05 else (WARN if ncor > lim else OK)
        msg = (f"{name} {n:.0f}% 物理转速 → 换算 {ncor:.0f}%"
               f"（θ={theta:.3f}，上限 {lim:.0f}%）")
        if lvl != OK:
            msg += "：超出压气机特性线，流量与压比都可能是高估"
        out.append(Finding(f"{name}.ncor", lvl, msg, ncor, lim, "%", name))
    return out


def _share(design, view, which: str) -> float:
    """把压气机功分到 LPC/HPC 上。

    求解器给了分量（W_lpc/W_hpc）就用分量——按级数平均分是错的：
    逐级温升沿流路递增，LPC 的实际功占比远低于级数占比
    （Demo 2-spool：LPC 2/10 级却只占 36/291 K ≈ 12%）。
    非设计点结果没有分量字段，才退回级数粗分，并已在 warnings 里说明只做体检。
    """
    wl, wh = view.get("W_lpc", 0.0), view.get("W_hpc", 0.0)
    if wl > 0.0 or wh > 0.0:
        return wl if which == "lpc" else wh
    n_l, n_h = max(design.lpc.stages, 0), max(design.hpc.stages, 0)
    tot = n_l + n_h
    if tot == 0:
        return 0.0
    return view.get("W_comp", 0.0) * ((n_l if which == "lpc" else n_h) / tot)


# ---------------------------------------------------------------------------
# 总入口
# ---------------------------------------------------------------------------

def _view_of(res) -> dict:
    """把 CycleResult / OffDesignResult / dict 统一成一个字典视图。"""
    if isinstance(res, dict):
        return dict(res)
    names = ["mdot_air", "mdot_core", "bpr", "opr", "fpr", "T1", "T2", "T3", "T4",
             "T5", "T7", "P3", "P5", "far", "far_ab", "thrust", "net_thrust",
             "sfc", "fuel_flow", "W_comp", "W_lpc", "W_hpc", "W_fan",
             "W_turb_hpt", "W_turb_ipt", "W_turb_lpt", "rpm", "n_pct",
             "n1_pct", "n2_pct", "self_sustaining"]
    v = {n: getattr(res, n, 0.0) for n in names}
    # 净推力优先：OffDesignResult 的 thrust 是总推力，扣完冲压阻力才是飞起来的那份
    if v.get("net_thrust"):
        v["thrust_gross"] = v["thrust"]
        v["thrust"] = v["net_thrust"]
    v["ok"] = getattr(res, "ok", True)
    v["errors"] = list(getattr(res, "errors", []) or [])
    v["warnings"] = list(getattr(res, "warnings", []) or [])
    return v


def evaluate(design, res, tech=None, p_amb: float = 0.0,
             alt: float = None) -> CheckReport:
    """对一次求解结果做全套判据。design 为 EngineDesign，res 为任一求解结果。

    p_amb 不给又没给 alt 时，喷管压比判据会自动跳过——别让 101325 变成"海拔 0"
    的默认假设，那会把高空点的喷管压比判得偏保守。
    """
    tech = tech or design.tech
    v = _view_of(res)
    if p_amb <= 0.0:
        a = alt if alt is not None else getattr(res, "alt", None)
        if a is not None:
            p_amb = isa(a)[0]
    # 残酷模式的转速是 per-shaft 字典，判据要知道哪根轴挂风扇（那就是 N1）
    v["_fan_shaft"] = getattr(getattr(design, "fan", None), "shaft", None)
    v["_has_turbine_split"] = hasattr(res, "W_turb_hpt")
    if p_amb > 0.0:
        v["p_amb"] = p_amb
    rep = CheckReport()

    # ---- 求解器自己报的错，原样上抬成 fail ----
    for e in v.get("errors", []):
        rep.add(Finding("solver", FAIL, e))
    for w in v.get("warnings", []):
        rep.add(Finding("solver.warn", WARN, w))

    if not v.get("ok", True):
        return rep

    rep.extend(check_temperatures(v, tech))
    rep.extend(check_corrected_speed(v, tech))
    rep.extend(check_combustion(v, tech))
    rep.extend(check_nozzle(v, design, tech))
    # 简单模式只有六个等效参数，没有叶轮直径/级数，几何类判据无从谈起。
    # 不假装它们通过——明写出来，免得玩家以为"全绿"就等于设计合理。
    if hasattr(design, "fan") and hasattr(design, "hpc"):
        rep.extend(check_shafts(design, v))
        rep.extend(check_flow_path(design, v, tech, getattr(res, "stages", None)))
    else:
        rep.add(Finding("geometry.skipped", OK,
                        "简单模式未定义几何，叶轮/流道类判据已跳过"))

    if v.get("thrust", 0.0) <= 0.0:
        rep.add(Finding("thrust", FAIL, "净推力 ≤ 0：这台发动机推不动自己"))
    else:
        rep.add(Finding("thrust", OK,
                        f"净推力 {v['thrust'] / 1000:.1f} kN，SFC {v['sfc']:.4f} kg/(N·h)", 
                        v["thrust"] / 1000.0))
    return rep


def check_flow_path(design, v: dict, tech, stages=None) -> List[Finding]:
    """沿流路逐部件检查转子：风扇 → LPC → HPC。

    站位优先用求解器给的逐级数据（`StageRow.P_in/T_in`），那才是真实站位；
    只有在非设计点这类没有逐级记录的结果上才退回 fpr/opr 反推。
    """
    out: List[Finding] = []
    rpm = v.get("rpm") or {}
    n_pct = v.get("n_pct") or {}
    mdot_core = v.get("mdot_core", 0.0)
    mdot_air = v.get("mdot_air", 0.0)
    fpr = v.get("fpr", 1.0) or 1.0
    opr = v.get("opr", 1.0) or 1.0
    T1 = v.get("T1", 288.0)
    pt1 = v.get("P3", 0.0) / max(opr, 1e-6) if v.get("P3", 0.0) > 0 else 0.0

    by_part: Dict[str, list] = {}
    for row in (stages or []):
        by_part.setdefault(row.part, []).append(row)

    if design.fan.diameter > 0 and design.kind == "turbofan":
        s = design.fan.shaft
        U = math.pi * design.fan.diameter * rpm.get(s, 0.0) / 60.0
        out.extend(check_rotor("风扇", mdot_air, T1, pt1, design.fan.diameter,
                               design.fan.hub_tip_ratio, U, n_pct.get(s, 0.0),
                               tech, "fan"))

    n_l, n_h = max(design.lpc.stages, 0), max(design.hpc.stages, 0)
    core_pr = max(opr / max(fpr, 1e-6), 1.0)
    share = (n_l / (n_l + n_h)) if (n_l + n_h) else 0.0

    for part, spec in (("LPC", design.lpc), ("HPC", design.hpc)):
        if spec.stages <= 0:
            continue
        rows = by_part.get(part) or []
        if rows:
            T_in, p_in = rows[0].T_in, rows[0].P_in
        elif part == "LPC":
            T_in, p_in = v.get("T2", T1), pt1 * fpr
        else:
            T_in = v.get("T3", T1) * 0.85
            p_in = pt1 * fpr * core_pr ** share
        s = spec.shaft
        U = math.pi * spec.tip_diameter * rpm.get(s, 0.0) / 60.0
        out.extend(check_rotor(part, mdot_core, T_in, p_in, spec.tip_diameter,
                               spec.hub_tip_ratio, U, n_pct.get(s, 0.0), tech))
    return out


# ---------------------------------------------------------------------------
# 自检
# ---------------------------------------------------------------------------

def _demo():
    try:
        import sys
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    from jetforge import (EngineDesign, FanSpec, CompressorSpec, TurbineSpec,
                          BurnerSpec, TECH_PRESETS, solve_design_point)

    def make(**kw) -> EngineDesign:
        base = dict(tech=TECH_PRESETS["1970s"], kind="turbofan", inlet_diameter=1.0,
                    fan=FanSpec(diameter=1.2, stages=1, hub_tip_ratio=0.42),
                    burner=BurnerSpec(exit_temp=1500.0), shaft_count=2)
        base.update(kw)
        return EngineDesign(**base)

    # A：原始 demo。推力数很好看，但流道对流量偏窄（LPC φ=0.80、HPC φ=0.89）。
    a = make(lpc=CompressorSpec(stages=2, tip_diameter=0.62, shaft=1, hub_tip_ratio=0.62),
             hpc=CompressorSpec(stages=8, tip_diameter=0.50, shaft=2, hub_tip_ratio=0.70),
             hpt=TurbineSpec(stages=1, tip_diameter=0.55, shaft=2),
             lpt=TurbineSpec(stages=3, tip_diameter=0.62, shaft=1))
    # B：只放宽流道、给 HPT 补一级。功率流向不变，判据全绿。
    b = make(lpc=CompressorSpec(stages=2, tip_diameter=0.76, shaft=1, hub_tip_ratio=0.55),
             hpc=CompressorSpec(stages=8, tip_diameter=0.64, shaft=2, hub_tip_ratio=0.68),
             hpt=TurbineSpec(stages=2, tip_diameter=0.62, shaft=2),
             lpt=TurbineSpec(stages=3, tip_diameter=0.66, shaft=1))

    for label, d in (("A 原始 demo", a), ("B 放宽流道 + HPT 2 级", b)):
        r = solve_design_point(d)
        rep = evaluate(d, r, p_amb=101325.0)
        print(f"\n=== {label} ===  F {r.thrust / 1000:.1f} kN  OPR {r.opr:.2f}  "
              f"BPR {r.bpr:.2f}  SFC {r.sfc:.4f}  判定 {rep.worst.upper()}")
        show = rep.lines(show_ok=(label.startswith("B")))
        for line in show:
            print("  " + line)


if __name__ == "__main__":
    _demo()