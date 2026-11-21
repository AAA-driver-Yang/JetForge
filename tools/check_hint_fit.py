#!/usr/bin/env python
"""检查悬停提示的长度预算。

悬停提示是自绘的（`UI/JetForgeDesignerWindow.cs` 的 `DrawHoverHint`）：固定 320 px 宽、
`wordWrap = true`、高度用 `st.CalcHeight` 算出来，所以**不会横向裁字**——长提示会折行，
盒子跟着变高。真正会出事的是"高到装不下窗口"：一屏 700 px 里，盒子超过半屏就开始盖住
它要解释的那个控件。

按 320 px 宽、11 px 字号估：一行约 46 个字母，行高约 14 px，上下 padding 12 px。
本闸门钉住"英文提示不超过 300 字符"（≈ 7 行 ≈ 110 px），比现状最长的 251 字符留一点余量。

超过时先想清楚：这条提示是**说明书**还是**一句话**。说明书该进文档，提示里只留一句话。
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "KSP", "Source")

# 一行的容量（320 px 宽、11 px 字号、≈6.6 px/字母）与预算
CHARS_PER_LINE = 46.0
MAX_EN_CHARS = 300

PAIR = re.compile(r'Loc\.T\(\s*"([^"]*)"\s*,\s*"([^"]*)"')


def scan():
    out = []
    for dp, _, fs in os.walk(SRC):
        for f in sorted(fs):
            if not f.endswith(".cs"):
                continue
            p = os.path.join(dp, f)
            rel = os.path.relpath(p, ROOT).replace(os.sep, "/")
            for i, ln in enumerate(io.open(p, encoding="utf-8").read().splitlines(), 1):
                for m in PAIR.finditer(ln):
                    en = m.group(2)
                    if len(en) > MAX_EN_CHARS:
                        lines = len(en) / CHARS_PER_LINE
                        out.append((rel, i, len(en), lines, en[:60]))
    return out


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    bad = scan()
    if not bad:
        print("OK   悬停提示长度：英文都在 %d 字符以内（约 %d 行、%d px）"
              % (MAX_EN_CHARS, int(MAX_EN_CHARS / CHARS_PER_LINE) + 1,
                 (int(MAX_EN_CHARS / CHARS_PER_LINE) + 1) * 14 + 12))
        return 0
    print("FAIL 悬停提示长度：%d 条英文超过 %d 字符（盒子会高到盖住控件）" % (len(bad), MAX_EN_CHARS))
    for rel, i, n, lines, head in bad:
        print("  %s:%d  %d 字符 ≈ %.1f 行  %s…" % (rel, i, n, lines, head))
    print("改法：提示里只留一句话，说明书内容进 docs/。")
    return 1


if __name__ == "__main__":
    sys.exit(main())