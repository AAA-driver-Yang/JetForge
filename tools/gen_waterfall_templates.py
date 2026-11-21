#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
从 StockWaterfallEffects 的原版模板派生出我们自己的 EFFECTTEMPLATE。

为什么要派生而不是直接用原版的两张：
  1. 原版的加力模板是**按 KSP 油门**写曲线的（`tBright` 从 0.55 就开始亮），
     而我们的加力是"过了点亮油门以后按比例开"。直接套用会出现两个问题：
       · 油门还没到点亮线，火已经亮了一半（用户报的"70% 以下就开启加力"）；
       · 模板自己的 `tScale` 直径曲线随油门从 0.78 涨到 1.0，和我们按
         `abFrac` 缩的倍率叠在一起，调油门时羽流会整体变细。
  2. 不同构型（老涡喷 / 现代低涵道 / 高涵道民航）的尾焰颜色与亮度本来就不同，
     原版一套模板伺候不了。

派生规则：
  · 所有 `controllerName = throttle` 的曲线改写成"加力比例 0→1"的定义域，
    运行时我们把该控制器 override 成 abFrac，于是曲线直接跟着物理走；
  · `tScale` 的 x/z（直径）拉平为 1.0，只让 y（长度）随比例长；
  · 马赫、大气密度驱动的曲线一律保留原样（那是真物理量）；
  · 加力模板保留 shock1..shock5（近端激波胞），6..11 是远场，删掉省算力；
  · 颜色按构型调 _StartTint / _EndTint。
"""

import io
import os
import re
import sys
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# StockWaterfallEffects 的模板在游戏目录里，不进本仓库：用 JETFORGE_SWE_PLUMES 指过去
SRC = os.environ.get("JETFORGE_SWE_PLUMES", "")
OUT = os.path.join(ROOT, "GameData/JetForge/WaterfallTemplates.cfg")

# 构型调色：RGB 目标 + 混入比例（0 = 完全用原版色，1 = 完全用目标色）
#
# 为什么是"三档温度 × 两种构型"而不是只按构型分：
#   加力温度是这台机器自己的数（`Afterburner.ExitTemp`，600~2200 K 都由玩家定），
#   1900 K 的老涡喷和 2100 K 的三代机，肉眼看就不是一种火。
#   温度档位是"这台发动机独有的"那一半，构型是"这一族共有的"那一半。
PALETTES = {
    # 老式加力涡喷：暖白黄核心 + 蓝灰剪切层
    "JetWarm": dict(
        warm=(1.00, 0.82, 0.52), cool=(0.70, 0.76, 0.94), mix=0.58,
    ),
    "JetMid": dict(
        warm=(1.00, 0.88, 0.66), cool=(0.74, 0.80, 0.97), mix=0.52,
    ),
    "JetHot": dict(
        warm=(0.92, 0.94, 1.00), cool=(0.66, 0.74, 1.00), mix=0.62,
    ),
    # 现代低涵道加力涡扇：蓝白/淡紫核心 + 低饱和蓝紫外层
    "FanWarm": dict(
        warm=(0.98, 0.90, 0.78), cool=(0.66, 0.74, 1.00), mix=0.60,
    ),
    "FanMid": dict(
        warm=(0.88, 0.92, 1.00), cool=(0.62, 0.70, 1.00), mix=0.62,
    ),
    "FanHot": dict(
        warm=(0.80, 0.88, 1.00), cool=(0.58, 0.66, 1.00), mix=0.66,
    ),
}

DRY_PALETTES = {
    # 低涵道涡扇：几乎不可见的热扰动，偏灰
    "LowBypass": dict(warm=(0.90, 0.92, 0.96), cool=(0.80, 0.84, 0.94), mix=0.35, opacity=1.00),
    # 高涵道民航：更淡更冷，几乎只剩热扰动
    "HighBypass": dict(warm=(0.94, 0.96, 1.00), cool=(0.86, 0.90, 1.00), mix=0.45, opacity=0.42),
    # 早期涡喷：偏琥珀灰、带烟
    "OldJet": dict(warm=(0.92, 0.86, 0.74), cool=(0.82, 0.80, 0.84), mix=0.45, opacity=1.10),
}


# ---------------- 解析 ----------------

def split_top_blocks(text, keyword):
    """把 `\tEFFECT\n\t{ ... }` 这种顶层块切出来，返回 (前缀, [块文本...], 后缀)。"""
    blocks = []
    i = 0
    prefix = None
    last_end = 0
    n = len(text)
    while True:
        m = re.compile(r"\n\t" + keyword + r"\s*\n\t\{").search(text, i)
        if not m:
            break
        start = m.start() + 1
        depth = 0
        j = text.index("{", m.start())
        while j < n:
            c = text[j]
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        if prefix is None:
            prefix = text[:start]
        blocks.append(text[start:j + 1])
        i = j + 1
        last_end = i
    suffix = text[last_end:] if prefix is not None else ""
    return prefix or text, blocks, suffix


def effect_name(block):
    # 允许连字符：内芯层叫 plume-core，正则里不带 - 会被截成 plume，
    # 于是"校验有没有内芯"永远为假（第一版就是这么静默通过的）。
    m = re.search(r"name\s*=\s*([A-Za-z0-9_\-]+)", block)
    return m.group(1) if m else "?"


def rewrite_curves(block, level_domain=True, shock=0, shock_keep=0):
    """把 controllerName = throttle 的那些曲线改写成加力比例定义域。

    shock/shock_keep：这片 EFFECT 是第几片激波（0 = 不是激波），
    激波的起亮门限按序号递进，才有"钻石一个接一个冒出来"的过程。
    """
    out = []
    pos = 0
    pat = re.compile(r"(\t\t(MODIFIER|SCALEMODIFIER|FLOATMODIFIER|COLORMODIFIER)\s*\n\t\t\{)")
    for m in pat.finditer(block):
        start = m.start()
        # 找这个 modifier 的块尾
        j = block.index("{", m.start())
        depth = 0
        k = j
        while k < len(block):
            c = block[k]
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    break
            k += 1
        seg = block[start:k + 1]
        out.append(block[pos:start])
        out.append(fix_modifier(seg, level_domain, shock, shock_keep))
        pos = k + 1
    out.append(block[pos:])
    return "".join(out)


def need(seg, key):
    m = re.search(key + r"\s*=\s*([^\s]+)", seg)
    return m.group(1) if m else ""


def set_curves(seg, axes):
    """把 xCurve/yCurve/zCurve 的 key 行换掉（保留四阶切线参数为 0）。"""
    for axis, keys in axes.items():
        new = "".join("\t\t\t\tkey = %s 0 0\n" % k for k in keys)
        pat = re.compile(r"(\t\t\t" + axis + r"\s*\n\t\t\t\{\n)(.*?)(\t\t\t\})", re.S)
        seg = pat.sub(lambda m: m.group(1) + new + m.group(3), seg)
    return seg


def set_float_curve(seg, keys):
    """改 floatCurve 里的 key 行。tBright/aBright/tTintFalloff 都是 floatCurve，
    不是 xCurve/yCurve/zCurve——写成 set_curves 会"静默什么都没改"。"""
    m = re.search(r"(floatCurve\s*\n)(\t+)\{", seg)
    if not m:
        return seg
    ind = m.group(2)
    new = "".join("%skey = %g %.4f 0 0\n" % (ind, k, v) for k, v in keys)
    return re.sub(r"(floatCurve\s*\n" + ind + r"\{\n)(.*?)(" + ind + r"\})",
                  lambda mm: mm.group(1) + new + mm.group(3), seg, flags=re.S)


def fix_modifier(seg, level_domain, shock=0, shock_keep=0):
    ctrl = need(seg, "controllerName")
    name = need(seg, "name")
    if ctrl != "throttle":
        return seg                       # 马赫/大气密度：真物理量，不动
    if not level_domain:
        return seg

    if name == "tScale":
        # 直径拉平（x/z 恒 1.0），长度随比例 0.40→1.15，中间给一个折点：
        # 加力刚点着时火是"短而亮"的，不是等比例拉长的荧光棒。
        return set_curves(seg, {
            "xCurve": ["0 1", "1 1"],
            "yCurve": ["0 0.40", "0.35 0.82", "1 1.15"],
            "zCurve": ["0 1", "1 1"],
        })
    if name == "tStrength":
        # 原版是"油门 0.699 之前强度 0，过线瞬间跳到峰值再回落"——
        # 这正是"加力还没点就亮一半"的来源。
        # 新曲线：0 没有火 → 0.18 到六成 → 0.55 到峰值 → 1 略回落。
        # 三折点是为了让"点火那 1.3 秒"看起来在长大，而不是整块淡入。
        m = re.search(r"floatCurve\s*\n(\t+)\{", seg)
        if not m:
            return seg
        ind = m.group(1)
        peaks = [float(x) for x in re.findall(r"key = [\d.]+ ([\d.\-]+)", seg)]
        peak = max(peaks) if peaks else 1.0
        if peak <= 0.0:
            peak = 1.0
        keys = [(0.0, 0.0), (0.18, peak * 0.60), (0.55, peak), (1.0, peak * 0.88)]
        new = "".join("%skey = %g %.4f 0 0\n" % (ind, k, v) for k, v in keys)
        return re.sub(r"(floatCurve\s*\n" + ind + r"\{\n)(.*?)(" + ind + r"\})",
                      lambda mm: mm.group(1) + new + mm.group(3), seg, flags=re.S)
    if name in ("tBright", "aBright"):
        if shock > 0:
            # 激波胞（钻石）要"一个接一个出现"，不能一起弹出来：
            # shock1 在加力 30% 起亮，shock5 要到 70%，每片间隔 0.10。
            lo = 0.30 + 0.10 * (shock - 1)
            hi = lo + 0.25
            if hi > 0.95:
                hi = 0.95
            return set_float_curve(seg, [(0.0, 0.0), (lo, 0.0), (hi, 1.0), (1.0, 1.0)])
        # 非激波件：0.15 起亮到 1.0
        return set_float_curve(seg, [(0.0, 0.0), (0.15, 1.0), (1.0, 1.0)])
    if name == "tTintFalloff":
        # 色偏衰减：比例 0 时"没火"（衰减值大），比例满时衰减到最小。
        return set_float_curve(seg, [(0.0, 10.0), (0.3, 4.0), (1.0, 0.0)])
    return seg


def tint(block, warm, cool, mix):
    """把 _StartTint / _EndTint 往目标色拉。KSP 的字段名是 colorValue（不是 color）。"""
    if mix <= 0.0:
        return block

    def conv(m):
        kind = m.group(1)
        ind = m.group(2)
        vals = [float(x) for x in re.findall(r"-?\d+\.?\d*", m.group(3))]
        rgb = vals[:3]
        tgt = warm if kind == "_StartTint" else cool
        new = [rgb[i] * (1 - mix) + tgt[i] * mix for i in range(3)]
        return "colorName = %s\n%scolorValue = %.4f,%.4f,%.4f,1" % (
            kind, ind, new[0], new[1], new[2])

    return re.sub(r"colorName = (_StartTint|_EndTint)\s*\n(\t+)colorValue = ([0-9.,\s\-]+)",
                  conv, block)


def core_layer(block, mix=0.55, shrink=0.45, shorten=0.88):
    """从主羽流 EFFECT 复制出"内芯"层。

    为什么要有两层：现实里的加力羽流不是一个均匀的发光圆柱，是**外面一圈淡的、里面一根亮的**。
    原版模板是单层，看起来像"荧光棒"；AJE_Realistic_JetFX 的做法也是核心 + 外晕。
    这里不新增素材，直接把同一片 EFFECT 缩小、提亮、并把强度曲线压到高比例段——
    于是"火芯"只在油门推上去以后才出现，低比例时只有外晕。
    """
    out = block

    # 1) 名字加后缀（EFFECT 的 name 在 MODEL 之前，第一次出现就是它）
    m = re.search(r"(name\s*=\s*)([A-Za-z0-9_]+)", out)
    if m:
        out = out[:m.start(2)] + m.group(2) + "-core" + out[m.end(2):]

    # 2) 直径缩，长度略缩（内芯比外晕短一截）
    def shrink_scale(mm):
        vals = [float(x) for x in re.findall(r"-?\d+\.?\d*", mm.group(2))]
        if len(vals) < 3:
            return mm.group(0)
        a = vals[0] * shrink
        b = vals[1] * shorten
        c = vals[2] * shrink
        return "%s%.4g,%.4g,%.4g" % (mm.group(1), a, b, c)

    out = re.sub(r"(scaleOffset\s*=\s*)([-\d.eE.,\s]+)", shrink_scale, out)

    # 3) 强度曲线压到高比例段：0.35 以下不亮，满比例才最亮
    def core_strength(seg):
        m2 = re.search(r"floatCurve\s*\n(\t+)\{", seg)
        if not m2:
            return seg
        ind = m2.group(1)
        peaks = [float(x) for x in re.findall(r"key = [\d.]+ ([\d.\-]+)", seg)]
        peak = max(peaks) if peaks else 1.0
        if peak <= 0.0:
            peak = 1.0
        keys = [(0.0, 0.0), (0.35, 0.0), (0.70, peak * 0.55), (1.0, peak * 0.80)]
        new = "".join("%skey = %g %.4f 0 0\n" % (ind, k, v) for k, v in keys)
        return re.sub(r"(floatCurve\s*\n" + ind + r"\{\n)(.*?)(" + ind + r"\})",
                      lambda mm: mm.group(1) + new + mm.group(3), seg, flags=re.S)

    out = re.sub(r"(\t+FLOATMODIFIER\s*\n\t+\{[^\x00]*?name\s*=\s*tStrength[^\x00]*?\n\t+\})",
                 lambda mm: core_strength(mm.group(1)), out)

    # 4) 颜色往白里拉（火芯应该是白热的，不是橙的）
    def whiten(mm):
        vals = [float(x) for x in re.findall(r"-?\d+\.?\d*", mm.group(3))]
        if len(vals) < 3:
            return mm.group(0)
        rgb = [min(1.0, vals[i] * (1 - mix) + 1.0 * mix) for i in range(3)]
        return "colorName = %s\n%scolorValue = %.4f,%.4f,%.4f,1" % (
            mm.group(1), mm.group(2), rgb[0], rgb[1], rgb[2])

    out = re.sub(r"colorName = (_StartTint|_EndTint)\s*\n(\t+)colorValue = ([0-9.,\s\-]+)",
                 whiten, out)
    return out


def top_level_blocks(text, keyword):
    """切顶格块（`EFFECTTEMPLATE\\n{`，前面没有缩进）。

    split_top_blocks 只认带一个 tab 缩进的块（那是 EFFECT 的层级），
    拿它切模板永远返回 0 个——第一版校验就因此报了"0 套模板，异常 0 处"，
    等于什么都没验。宁可多写十行，也不能让校验静默放过。
    """
    blocks = []
    for m in re.finditer(r"(?m)^" + keyword + r"\s*\n\{", text):
        depth = 0
        j = text.index("{", m.start())
        k = j
        while k < len(text):
            c = text[k]
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    break
            k += 1
        blocks.append(text[m.start():k + 1])
    return blocks


def validate(path, expect_core=False):
    """生成完自己读一遍：每套模板几片 EFFECT、湿态有没有内芯层、内芯是不是真的更细。

    这一步是被"tBright 写成 xCurve 会静默不改还报成功"教出来的——
    cfg 生成器不自己验一遍，错误只能等玩家截图。
    注意要先按 EFFECTTEMPLATE 切，再按 EFFECT 切：直接全文件找 EFFECT
    会把 9 套模板里同名的 plume/shock1 混成一堆。
    """
    text = io.open(path, encoding="utf-8-sig").read()
    tmpl_blocks = top_level_blocks(text, "EFFECTTEMPLATE")
    bad = 0
    for tb in tmpl_blocks:
        tn = re.search(r"templateName\s*=\s*([A-Za-z0-9_\-]+)", tb)
        tn = tn.group(1) if tn else "?"
        _, ebs, _ = split_top_blocks(tb, "EFFECT")
        names = [effect_name(b) for b in ebs]
        want_core = expect_core and "Wet" in tn
        cores = [b for b in ebs if effect_name(b).endswith("-core")]
        if want_core and not cores:
            print("  ! %-26s 湿态模板没有内芯层（%s）" % (tn, ",".join(names)))
            bad += 1
            continue
        if not want_core and cores:
            print("  ! %-26s 干态模板不该有内芯层" % tn)
            bad += 1
            continue
        if cores:
            # 参照物要用主羽流（plume），不是最外层折射壳：壳和芯比没有可比性。
            main = ([b for b in ebs if effect_name(b) == "plume"]
                    or [b for b in ebs if not effect_name(b).endswith("-core")])
            a = [float(x) for x in re.findall(r"-?\d+\.?\d*",
                                              re.search(r"scaleOffset\s*=\s*([-\d.eE.,\s]+)", main[0]).group(1))]
            c = [float(x) for x in re.findall(r"-?\d+\.?\d*",
                                              re.search(r"scaleOffset\s*=\s*([-\d.eE.,\s]+)", cores[0]).group(1))]
            ok = len(a) >= 3 and len(c) >= 3 and c[0] < a[0] * 0.99 and c[2] < a[2] * 0.99
            if not ok:
                bad += 1
            print("  %-26s %2d 层  外晕 %s → 内芯 %s %s" % (
                tn, len(ebs), ",".join("%.3g" % v for v in a[:3]),
                ",".join("%.3g" % v for v in c[:3]), "OK" if ok else "！没变小"))
        else:
            print("  %-26s %2d 层  无内芯层" % (tn, len(ebs)))
    print("  校验：%d 套模板，异常 %d 处" % (len(tmpl_blocks), bad))
    return bad


def build(src_file, out_name, kind, palette, keep_effects=None, level_domain=True, with_core=False):
    text = io.open(src_file, encoding="utf-8-sig").read()
    prefix, blocks, suffix = split_top_blocks(text, "EFFECT")
    kept = []
    for b in blocks:
        nm = effect_name(b)
        shock = 0
        if nm.startswith("shock"):
            shock = int(re.sub(r"\D", "", nm) or "0")
            if keep_effects is not None and shock > keep_effects:
                continue
        kept.append((b, shock))
    out = [prefix]
    n_shock = 0
    core_done = False
    for b, shock in kept:
        b = rewrite_curves(b, level_domain, shock, keep_effects or 0)
        b = tint(b, palette.get("warm", (1, 1, 1)), palette.get("cool", (1, 1, 1)),
                 palette.get("mix", 0.0))
        if shock > 0:
            n_shock += 1
        out.append("\n")
        out.append(b)
        # 内芯层挂在第一片"非激波"的羽流后面。激波片本身已经是一个个亮点，
        # 再给它们各自加内芯会变成一串白球——那是另一种假。
        # 内芯只从主羽流升出来：refraction 是最外层的折射壳、exitSmoke 是尾烟，
        # 拿它们复制内芯会得到"两层壳"而不是"一根亮芯"。
        is_plume = effect_name(b) == "plume" or (
            shock == 0 and "tStrength" in b and effect_name(b) not in ("refraction", "exitSmoke"))
        if with_core and not core_done and is_plume:
            out.append("\n")
            out.append(core_layer(b))
            core_done = True
    out.append(suffix)
    body = "".join(out)
    body = body.replace("templateName = " + os.path.basename(src_file)[:-4],
                        "templateName = " + out_name, 1)
    return body, len(kept)


def main():
    jobs = [
        # (源文件, 新模板名, 构型表, 保留几片激波, 是否改曲线)
        # 湿态 6 套：构型（涡喷/涡扇）× 加力温度（暖/中/热）
        ("stock-kerozine-afterburner.cfg", "JetForge-Wet-Jet-Warm", PALETTES["JetWarm"], 5, True),
        ("stock-kerozine-afterburner.cfg", "JetForge-Wet-Jet-Mid", PALETTES["JetMid"], 5, True),
        ("stock-kerozine-afterburner.cfg", "JetForge-Wet-Jet-Hot", PALETTES["JetHot"], 5, True),
        ("stock-kerozine-afterburner.cfg", "JetForge-Wet-Fan-Warm", PALETTES["FanWarm"], 5, True),
        ("stock-kerozine-afterburner.cfg", "JetForge-Wet-Fan-Mid", PALETTES["FanMid"], 5, True),
        ("stock-kerozine-afterburner.cfg", "JetForge-Wet-Fan-Hot", PALETTES["FanHot"], 5, True),
        # 干态 3 套：低涵道 / 高涵道 / 早期涡喷（带烟）
        ("stock-kerozine-turbofan-1.cfg", "JetForge-Dry-LowBypass", DRY_PALETTES["LowBypass"], None, True),
        ("stock-kerozine-turbofan-1.cfg", "JetForge-Dry-HighBypass", DRY_PALETTES["HighBypass"], None, True),
        ("stock-kerozine-turbofan-1.cfg", "JetForge-Dry-OldJet", DRY_PALETTES["OldJet"], None, True),
    ]
    chunks = []
    for src, name, pal, keep, lvl in jobs:
        # 湿态（有加力）双层：外晕 + 白热火芯。干态只有一层——干态羽流本来就薄，
        # 加一层内芯会让它看起来像在烧加力。
        # 玩家反馈：加力时出现一圈白环。怀疑是内芯层——它是同一个 fx-cylinder
        # 缩小后的副本，圆柱端盖在羽流中间露出来就是一道环。先关掉，
        # 等换成没有端盖的模型（fx-complex-plume-1）再谈分层。
        want_core = False
        body, n = build(os.path.join(SRC, src), name, src, pal, keep, lvl, want_core)
        chunks.append("// 派生自 %s，%d 个 EFFECT%s\n%s\n" % (
            src, n, "（含内芯层）" if want_core else "", body))
        print("%-28s <- %-32s %d effects%s" % (name, src, n, " +core" if want_core else ""))
    io.open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(chunks))
    print("written:", OUT, os.path.getsize(OUT), "bytes")
    validate(OUT, expect_core=any("Wet" in j[1] for j in jobs) and False)


if __name__ == "__main__":
    main()