#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""本地化词条写了没人用。

check_loc_keys.py 管的是反方向：源码里引用的键，两份词条里必须有。
这一道管：两份词条里写下的键，得有人在源码或随包 cfg 里引用。

为什么要有：词条是"玩家会读到的字"，没人引用的词条不会上屏，改它不会有任何反馈，
于是下一次改模型、改说明的人只会改有人看的那份，剩下那份慢慢烂掉。半年后有人
想把某个功能接回来，会照着一条三十轮前就过时的英文去写界面。

判据：键名在 .cs 与随包 .cfg（Localization 目录之外）里出现次数 == 0 即没人用。
Localization 自己的两份文件不算语料，否则每个键都"至少出现过一次"。

用法：
    python tools/check_loc_unused.py            # 检查
    python tools/check_loc_unused.py -v         # 连白名单一起列出来

白名单 tools/loc_unused_allow.txt：一行 `键名 # 理由`。空着是正常的。
"""
from __future__ import print_function
import io
import os
import re
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOC_DIR = os.path.join(ROOT, "GameData", "JetForge", "Localization")
SRC_DIR = os.path.join(ROOT, "KSP", "Source")
PACK_DIR = os.path.join(ROOT, "GameData", "JetForge")
ALLOW = os.path.join(ROOT, "tools", "loc_unused_allow.txt")

KEY = re.compile(r"^\s*#(autoLOC_[A-Za-z0-9_]+)\s*=", re.M)
REF = re.compile(r"autoLOC_[A-Za-z0-9_]+")


def read(path):
    try:
        return io.open(path, encoding="utf-8", errors="replace").read()
    except IOError:
        return ""


def defined_keys():
    """两份词条文件里定义的键（同一键在两份里都该有，这里只取并集并记录来源）。"""
    found = {}
    for name in sorted(os.listdir(LOC_DIR)) if os.path.isdir(LOC_DIR) else []:
        if not name.lower().endswith(".cfg"):
            continue
        path = os.path.join(LOC_DIR, name)
        for m in KEY.finditer(read(path)):
            found.setdefault(m.group(1), set()).add(name)
    return found


def corpus():
    """所有可能引用键的地方：源码 + 随包 cfg（Localization 目录除外）。"""
    text = []
    for base, _dirs, files in os.walk(SRC_DIR):
        for name in files:
            if name.endswith(".cs"):
                text.append(read(os.path.join(base, name)))
    for base, _dirs, files in os.walk(PACK_DIR):
        if os.path.abspath(base).startswith(os.path.abspath(LOC_DIR)):
            continue
        for name in files:
            if name.endswith(".cfg"):
                text.append(read(os.path.join(base, name)))
    return "\n".join(text)


def allowlist():
    allow = {}
    if not os.path.isfile(ALLOW):
        return allow
    for line in read(ALLOW).splitlines():
        line = line.split("#", 1)[0].strip() if line.lstrip().startswith("#") else line
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        parts = line.split("#", 1)
        key = parts[0].strip()
        why = parts[1].strip() if len(parts) > 1 else ""
        if key:
            allow[key] = why
    return allow


def main():
    verbose = "-v" in sys.argv
    keys = defined_keys()
    if not keys:
        print("FAIL 本地化词条：一份词条文件都没读到（%s）" % LOC_DIR)
        return 1

    body = corpus()
    refs = set(REF.findall(body))
    allow = allowlist()

    unused = []
    for key in sorted(keys):
        if key in refs or key in allow:
            continue
        unused.append(key)

    if verbose:
        for key in sorted(allow):
            print("  （放过）%s  %s" % (key, allow.get(key) or "没写理由"))

    if unused:
        print("FAIL 本地化词条：%d 个键写了没人引用" % len(unused))
        for key in unused[:20]:
            print("  %s  （在 %s 里定义）" % (key, "、".join(sorted(keys[key]))))
        if len(unused) > 20:
            print("  …… 还有 %d 个" % (len(unused) - 20))
        print("  没人引用的词条不会上屏，改它不会有反馈，最后一定烂掉。")
        print("  真要用就接到源码或随包 cfg 里；暂时不用就删掉；需要留着就写进 tools/loc_unused_allow.txt。")
        return 1

    if allow:
        print("OK   本地化词条没有写了没人用的（%d 个键全被引用，放过 %d 个）" % (len(keys), len(allow)))
    else:
        print("OK   本地化词条没有写了没人用的（%d 个键全被引用）" % len(keys))
    return 0


if __name__ == "__main__":
    sys.exit(main())