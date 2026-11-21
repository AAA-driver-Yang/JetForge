# -*- coding: utf-8 -*-
"""把 Localization/*.cfg 里的词条块整理成「两缩进 + 按字母排序 + 无重复」。

用法：python tidy_loc.py <cfg> [<cfg> …]
只重排 #autoLOC_ 段落，别的行原样保留。
"""
import io
import re
import sys

KEY = re.compile(r'^\s*(#autoLOC_\S+)\s*=\s*(.*)$')

def tidy(path):
    lines = io.open(path, "r", encoding="utf-8", newline="").read().split("\n")
    first = last = -1
    seen = {}
    dups = []
    for i, line in enumerate(lines):
        m = KEY.match(line)
        if not m:
            continue
        if first < 0:
            first = i
        last = i
        k, v = m.group(1), m.group(2).strip()
        if k in seen and seen[k] != v:
            dups.append("%s: %r -> %r" % (k, seen[k], v))
        seen[k] = v
    if first < 0:
        print("%s: 没有词条" % path)
        return 1
    block = ["\t\t%s = %s" % (k, seen[k]) for k in sorted(seen)]
    lines[first:last + 1] = block
    io.open(path, "w", encoding="utf-8", newline="").write("\n".join(lines))
    print("%s: %d 条（重排）" % (path, len(block)))
    for d in dups:
        print("  重复键 %s" % d)
    return 0

if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    rc = 0
    for p in sys.argv[1:]:
        rc |= tidy(p)
    sys.exit(rc)