#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""本地化成对字面量的占位符对账。

为什么要有这一条：`Loc.T("要过 {0:F1} kg/s，喉道 {1:F4} m²", "must pass {0:F1} kg/s …")`
两边占位符对不上时，C# **不会**在编译期说话：
- 英文少了 {1}：格式串合法，只是那个数不显示——玩家看到半句话；
- 英文多了 {2}：`string.Format` 抛 `FormatException`，如果这个调用点在 catch 里，
  屏幕上就是一行空白（§167 就是这么吞掉一整段喘振标记的）。
两种都是"只有玩家能发现"的错，所以放进流水线当闸门。

规则：成对里出现过的每个下标，必须两边都有；顺序不比（英文语序本来就不同），
但 repeats 要比（`{0}` 在中文里出现两次、英文里只出现一次，说明少写了一处）。
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "KSP", "Source")

# Loc.T("中文", "English") —— 两个字符串字面量、允许转义
PAIR = re.compile(
    r'Loc\.T\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)', re.S)
# 占位符：{0} {1:F2} {0:P0} {{ 转义不算
PH = re.compile(r"\{(\d+)(?:[,:][^{}]*)?\}")


def walk():
    for base, _dirs, files in os.walk(SRC):
        for fn in files:
            if fn.endswith(".cs"):
                yield os.path.join(base, fn)


def counters(s):
    d = {}
    for m in PH.finditer(s):
        k = int(m.group(1))
        d[k] = d.get(k, 0) + 1
    return d


def main():
    bad = []
    pairs = 0
    for path in walk():
        with io.open(path, encoding="utf-8") as f:
            text = f.read()
        for m in PAIR.finditer(text):
            pairs += 1
            zh, en = m.group(1), m.group(2)
            cz, ce = counters(zh), counters(en)
            if not cz and not ce:
                continue
            if set(cz) != set(ce) or any(cz[k] != ce.get(k, 0) for k in cz):
                line = text[:m.start()].count("\n") + 1
                rel = os.path.relpath(path, ROOT).replace("\\", "/")
                bad.append((rel, line, zh, en, cz, ce))
    if bad:
        out = io.open(os.path.join(ROOT, "_scratch", "loc_fmt.txt"), "w",
                      encoding="utf-8")
        for rel, line, zh, en, cz, ce in bad:
            out.write("%s:%d\n  中 %s  -> %s\n  英 %s  -> %s\n"
                      % (rel, line, sorted(cz.items()), zh, sorted(ce.items()), en))
        out.close()
        print("占位符对账：%d 对里有 %d 处不符（清单见 _scratch/loc_fmt.txt）"
              % (pairs, len(bad)))
        for rel, line, zh, en, cz, ce in bad[:20]:
            print("  %s:%d  中%s / 英%s" % (rel, line, sorted(cz), sorted(ce)))
        return 1
    print("OK 占位符对账：%d 对成对字面量，两边的 {n} 全一致" % pairs)
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())