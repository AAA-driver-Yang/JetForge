#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""可读性闸门：一屏一行最多放 5 个数。

背景（§159）：玩家面板上的读数以前是"一个数字接一个数字"堆在 `｜` 后面，
一行五六个数，看着像日志不像仪表。这一轮手工切了九处，但手工的事会退回去，
所以做成闸门：

  · 只看玩家看得见的地方：`KSP/Source/UI/*.cs` 与 `KSP/Source/ModuleJetForge*.cs`；
  · 只看**显示行**：字符串里的 `\\n` 切开之后每一段单独算；
  · 只数格式槽 `{0:F2}` 这种，纯文字长句不算（提示文字该长就长）；
  · 一行超过 5 个槽 → 要么拆行，要么在该行写上 `可读性豁免：<理由>`。

用法：python tools/check_panels.py        （返回码 0 = 通过）
"""
import glob
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "KSP", "Source")
LIMIT = 5

lit = re.compile(r'"(?:[^"\\]|\\.)*"')
slot = re.compile(r'\{\d+(?::[^}]*)?\}')
EXEMPT = "可读性豁免"


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    bad = []
    files = glob.glob(os.path.join(ROOT, "UI", "*.cs"))
    files += glob.glob(os.path.join(ROOT, "ModuleJetForge*.cs"))
    for path in sorted(files):
        with open(path, encoding="utf-8") as f:
            lines = f.read().split("\n")
        for no, line in enumerate(lines, 1):
            if EXEMPT in line:
                continue
            for m in lit.finditer(line):
                s = m.group(0)[1:-1]
                for piece in re.split(r"\\n", s):
                    n = len(slot.findall(piece))
                    if n > LIMIT:
                        bad.append((n, os.path.relpath(path, ROOT), no, piece))
    if not bad:
        print("OK   面板读数可读性：没有一行超过 %d 个数" % LIMIT)
        return 0
    bad.sort(reverse=True)
    for n, path, no, s in bad:
        print("FAIL %s:%d 一行 %d 个数：%s" % (path, no, n, s[:60]))
    print("共 %d 行太密：拆成两行，或在行尾写「%s：<理由>」" % (len(bad), EXEMPT))
    return 1


if __name__ == "__main__":
    sys.exit(main())