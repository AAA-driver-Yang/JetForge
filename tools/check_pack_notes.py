#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""随包文件里的开发笔记闸门。

为什么要有这道闸门：`GameData/JetForge/**` 会被原样打包发出去，里面的 cfg 注释、
随包 README 都是"货架上的东西"。开发期的"暂时用原版模型占位""下一步再生成外形"
写在这儿，过几个月连我自己都分不清哪句是现状、哪句是当时的打算——而它已经随包
发了出去。

判据是"这句话会不会被后来改包的人当成现状"，不是"玩家在游戏里能不能看见"
（cfg 注释不上屏，但会跟着 zip 走，也会被玩家翻到）。

规则：
  · 扫 GameData/JetForge/** 里的 *.cfg / *.md
  · 命中 TODO / FIXME / XXX / 暂时 / 占位 / 下一步 / 待接 / 先这样 / 以后再做 → 报出来
  · 确属有意保留的，同文件根目录 tools/pack_notes_allow.txt 里写
    `相对路径:行号范围: 理由`，命中即放过
用法：
  python tools/check_pack_notes.py            # 检查
  python tools/check_pack_notes.py -v         # 连放过的也列出来
"""
import io
import os
import re
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACK = os.path.join(ROOT, "GameData", "JetForge")
ALLOW = os.path.join(ROOT, "tools", "pack_notes_allow.txt")

MARKS = re.compile(r"TODO|FIXME|XXX|暂时|占位|下一步|待接|先这样|以后再做")
EXTS = (".cfg", ".md")


def load_allow():
    out = []
    if not os.path.isfile(ALLOW):
        return out
    for raw in io.open(ALLOW, encoding="utf-8"):
        s = raw.strip()
        if not s or s.startswith("#"):
            continue
        out.append(s)
    return out


def allowed(allow, rel, line_no):
    for a in allow:
        m = re.match(r"([^:]+):(\d+)(?:-(\d+))?:", a)
        if not m:
            continue
        if m.group(1).strip() != rel:
            continue
        lo = int(m.group(2))
        hi = int(m.group(3)) if m.group(3) else lo
        if lo <= line_no <= hi:
            return True
    return False


def main():
    verbose = "-v" in sys.argv
    allow = load_allow()
    hits = []
    passed = []
    for dirpath, _, names in os.walk(PACK):
        for n in sorted(names):
            if not n.lower().endswith(EXTS):
                continue
            full = os.path.join(dirpath, n)
            rel = os.path.relpath(full, ROOT).replace("\\", "/")
            for i, raw in enumerate(io.open(full, encoding="utf-8", errors="replace"), 1):
                m = MARKS.search(raw)
                if not m:
                    continue
                item = "%s:%d  %s  ← %s" % (rel, i, m.group(0), raw.strip()[:90])
                if allowed(allow, rel, i):
                    passed.append(item)
                else:
                    hits.append(item)
    if verbose:
        for it in passed:
            print("  放过  " + it)
    if hits:
        print("FAIL 随包文件里的开发笔记 %d 处（要么改写成现状，要么进 tools/pack_notes_allow.txt 说明理由）" % len(hits))
        for it in hits:
            print("  " + it)
        return 1
    print("OK 随包文件没有留下开发笔记（放过 %d 处）" % len(passed))
    return 0


if __name__ == "__main__":
    sys.exit(main())