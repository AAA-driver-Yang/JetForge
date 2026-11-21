#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查「固定宽度的控件 + 英文文案」会不会被裁掉。

为什么需要这道闸：中文一个字约 13 像素宽、英文一个字母约 6.6 像素，
`Loc.T("高度剖面", "Altitude profile")` 配 `GUILayout.Width(80f)` 时，
中文 52 px 放得下、英文 106 px 放不下——**排版是按中文算的，只有英文玩家会被裁**。
这类问题编译过、回归过、中文界面完全正常，只有真把界面切到英文并盯着看才发现。

判定：估算像素宽 > 控件宽 × 0.98 就记一条。基线 `tools/label_fit_baseline.json`
记同「同一句中文 + 同一句英文」的存量，新增即 FAIL——先把缺的补上，别让存量挡住新代码。
"""
import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASELINE = os.path.join(ROOT, "tools", "label_fit_baseline.json")
sys.stdout.reconfigure(encoding="utf-8")

CJK_PX = 13.0
LATIN_PX = 6.6
PADDING = 0.98

WIDTH = re.compile(r"GUILayout\.Width\(\s*([0-9.]+)f?\s*\)")
PAIR = re.compile(r'Loc\.T\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)')


def est_px(text):
    px = 0.0
    for ch in text:
        px += CJK_PX if ord(ch) > 0x2E80 else LATIN_PX
    return px


def key_of(zh, en):
    return zh + " ||| " + en


def containers(lines):
    """父容器宽度：`GUILayout.BeginVertical(GUILayout.Width(112f))` 之后、
    对应 `End*` 之前的控件都被这个宽度框住，哪怕自己那行没写 Width。

    为什么要这一层：`SectionWindow.Field()` 就是 `BeginVertical(Width(112f))` +
    `GUILayout.Label(key)`，标签自己那行没有 Width，逐行扫永远看不见它（§245 之后补）。
    """
    out = []
    begin = re.compile(r"GUILayout\.Begin(Horizontal|Vertical|Area|ScrollView)\s*\(")
    end = re.compile(r"GUILayout\.End(Horizontal|Vertical|Area|ScrollView)\s*\(")
    stack = []
    for i, line in enumerate(lines, 1):
        if end.search(line) and stack:
            stack.pop()
            continue
        m = begin.search(line)
        if m:
            w = WIDTH.search(line)
            stack.append(float(w.group(1)) if w else None)
            continue
        if not stack:
            continue
        width = None
        for w in reversed(stack):
            if w is not None:
                width = w
                break
        if width is None:
            continue
        if WIDTH.search(line):
            continue  # 自己指定了宽度，交给逐行那段
        if "GUILayout." not in line and "GUILayout.Label(" not in line:
            continue
        for pair in PAIR.finditer(line):
            out.append((i, width, pair.group(1), pair.group(2)))
    return out


def scan():
    """返回 [(key, 说明)]，同 key 只留一条（不同宽度取最紧的那个）。"""
    seen = {}
    for base, _dirs, files in os.walk(os.path.join(ROOT, "KSP", "Source")):
        for name in sorted(files):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(base, name)
            rel = os.path.relpath(path, ROOT).replace("\\", "/")
            with io.open(path, encoding="utf-8", newline="") as f:
                text = f.read()
            lines = text.splitlines()
            for i, line in enumerate(lines, 1):
                m = WIDTH.search(line)
                if not m:
                    continue
                width = float(m.group(1))
                for pair in PAIR.finditer(line):
                    zh, en = pair.group(1), pair.group(2)
                    need = est_px(en)
                    if need <= width * PADDING:
                        continue
                    k = key_of(zh, en)
                    note = "%s:%d 宽 %.0f px，英文约 %.0f px" % (rel, i, width, need)
                    if k not in seen or need / width > seen[k][0]:
                        seen[k] = (need / width, note)
            for i, width, zh, en in containers(lines):
                need = est_px(en)
                if need <= width * PADDING:
                    continue
                k = key_of(zh, en)
                note = "%s:%d 父容器宽 %.0f px，英文约 %.0f px" % (rel, i, width, need)
                if k not in seen or need / width > seen[k][0]:
                    seen[k] = (need / width, note)
            for helper, width in helpers(lines):
                for i, line in enumerate(lines, 1):
                    for pair, need in helper_calls(line, helper):
                        if need <= width * PADDING:
                            continue
                        zh, en = pair
                        k = key_of(zh, en)
                        note = "%s:%d %s() 宽 %.0f px，英文约 %.0f px" % (rel, i, helper, width, need)
                        if k not in seen or need / width > seen[k][0]:
                            seen[k] = (need / width, note)
    return [(k, v[0], v[1]) for k, v in seen.items()]


def helpers(lines):
    """找「把标签塞进固定宽度里的辅助函数」：形参里有 string label/text，
    函数体里又有 GUILayout.Label(... Width(Nf))。

    为什么需要这一层：`TextField(label, value)` 里写一处 `Width(190f)`，
    然后几十个调用点各传一句 `Loc.T(...)` —— 逐行扫 Width 的话，
    那几十句英文全在闸门视野之外（§244）。
    """
    found = []
    sig = re.compile(r"^\s*(?:private|public|internal|protected)?\s*(?:static\s+)?"
                     r"(?:void|string|bool)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(([^)]*)\)")
    for i, line in enumerate(lines):
        m = sig.match(line)
        if not m:
            continue
        name, params = m.group(1), m.group(2)
        if not re.search(r"\bstring\s+(label|text)\b", params):
            continue
        body = lines[i:i + 40]
        # 必须"同一行"里既有 GUILayout.Label(标签形参, …) 又有 Width(Nf)：
        # 只按"函数体里出现过 Width"算，会把 `Row(string text)` 里那句
        # 不限宽、让父容器撑开的整句说明也当成被裁（一次报出 6 倍宽度）。
        for bl in body:
            if "GUILayout.Label(" not in bl or WIDTH.search(bl) is None:
                continue
            pm = re.search(r"\b(string\s+)?(label|text)\b", params)
            label = pm.group(2) if pm else None
            if label is None or not re.search(r"\b" + label + r"\b", bl):
                continue
            found.append((name, float(WIDTH.search(bl).group(1))))
            break
    return found


def helper_calls(line, helper):
    """`Row(Loc.T("中文", "English"), ...)` → 取第一个实参里的英文。"""
    out = []
    for m in re.finditer(r"(?<![A-Za-z0-9_.])" + re.escape(helper) + r"\s*\(", line):
        tail = line[m.end():m.end() + 200]
        pair = PAIR.search(tail)
        if pair:
            en = pair.group(2)
            out.append(((pair.group(1), en), est_px(en)))
    return out


def main():
    rows = scan()
    if "--write" in sys.argv:
        data = {"total": len(rows), "keys": sorted(k for k, _r, _n in rows)}
        with io.open(BASELINE, "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, ensure_ascii=False, indent=1, sort_keys=True)
        print("已重设基线：%d 处英文文案在固定宽度里放不下" % len(rows))
        return 0

    if not os.path.exists(BASELINE):
        print("FAIL 找不到基线 tools/label_fit_baseline.json（先跑 --write）")
        return 1
    with io.open(BASELINE, encoding="utf-8") as f:
        base = set(json.load(f)["keys"])

    fresh = [(k, r, n) for k, r, n in rows if k not in base]
    gone = base - set(k for k, _r, _n in rows)
    if fresh:
        print("新增 %d 处放不下的英文文案（存量 %d 处）：" % (len(fresh), len(base)))
        for _k, ratio, note in fresh:
            print("  %.2f×  %s" % (ratio, note))
        return 1
    print("OK 固定宽度控件：没有新增放不下的英文文案（存量 %d 处）" % len(base))
    if gone:
        print("   另有 %d 处已修好，可以 --write 重设基线" % len(gone))
    return 0


if __name__ == "__main__":
    sys.exit(main())