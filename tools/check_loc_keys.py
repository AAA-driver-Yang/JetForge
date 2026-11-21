#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""键门：源码里出现的 #autoLOC_ 键，必须在两份 Localization cfg 里都存在。

为什么需要它：KSPField / KSPAction 的特性实参必须是编译期常量，所以右键菜单的
标签只能写成 "#autoLOC_xxx"，写不成 Loc.T(...)。键名打错在编辑器里完全看不出来，
游戏里只会原样显示「#autoLOC_JF_f_thrust」——那是最刺眼的一种错，而它编译得过、
测试也绿。这里把键集合两边对一遍。

只用标准库；退出码 1 表示有缺键。
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "KSP", "Source")
LOC = os.path.join(ROOT, "GameData", "JetForge", "Localization")

KEY = re.compile(r'"#(autoLOC_[A-Za-z0-9_]+)"')
DEF = re.compile(r'^\s*#(autoLOC_[A-Za-z0-9_]+)\s*=', re.M)
# 键不许**拼**。KSP 只在"整个字符串就是一个键"时才翻译：
# guiName = "#autoLOC_JF_f_afterburner" + "自动" 得到的是一串不存在的键，
# 游戏里原样显示「#autoLOC_JF_f_afterburner 自动（油门 70% 起）」。
# 上面那道"键必须在 cfg 里存在"查不出这个——键确实存在，是拼接毁掉了它。
PARTIAL_LEFT = re.compile(r'"#autoLOC_[A-Za-z0-9_]+"\s*\+')
PARTIAL_RIGHT = re.compile(r'\+\s*"#autoLOC_[A-Za-z0-9_]+"')


def keys_in_source():
    found = {}
    for base, _dirs, files in os.walk(SRC):
        for name in files:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(base, name)
            text = io.open(path, encoding="utf-8").read()
            for key in KEY.findall(text):
                found.setdefault(key, path)
    return found


def keys_in_cfg(name):
    text = io.open(os.path.join(LOC, name), encoding="utf-8").read()
    return set(DEF.findall(text))


def partial_keys():
    """键被拼在别的字里：两头只要挨着 `+` 就是这个毛病。"""
    bad = []
    for base, _dirs, files in os.walk(SRC):
        for name in files:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(base, name)
            for no, line in enumerate(io.open(path, encoding="utf-8").read().splitlines(), 1):
                head = line.lstrip()
                # 注释里写这个毛病当反面教材是好事，不算犯。
                if head.startswith("//") or head.startswith("*") or head.startswith("/*"):
                    continue
                if PARTIAL_LEFT.search(line) or PARTIAL_RIGHT.search(line):
                    bad.append((os.path.relpath(path, ROOT), no, line.strip()))
    return bad


def main():
    used = keys_in_source()
    en = keys_in_cfg("en-us.cfg")
    zh = keys_in_cfg("zh-cn.cfg")
    missing = []
    for key, path in sorted(used.items()):
        short = os.path.relpath(path, ROOT)
        if key not in en:
            missing.append("en-us 缺 %s（%s）" % (key, short))
        if key not in zh:
            missing.append("zh-cn 缺 %s（%s）" % (key, short))
    partial = partial_keys()
    if partial:
        for path, no, line in partial:
            sys.stderr.write("  BAD %s:%d 键被拼进了字符串：%s\n" % (path, no, line))
        sys.stderr.write("FAIL 本地化键：%d 处键和别的字拼在一起（先翻整条键，再拼后缀）\n" % len(partial))
        return 1
    # 反过来也查：词条里有的键没人用，是删代码留下的孤儿，不算错但要说一声
    orphans = sorted((en | zh) - set(used) - {"autoLOC_JF_engine_title", "autoLOC_JF_engine_desc",
                                              "autoLOC_JF_intake_title", "autoLOC_JF_intake_desc"})
    if missing:
        for m in missing:
            sys.stderr.write("  BAD %s\n" % m)
        sys.stderr.write("FAIL 本地化键：%d 处缺词条\n" % len(missing))
        return 1
    print("OK 本地化键：源码用 %d 个键，两份词条都齐（en %d / zh %d）"
          % (len(used), len(en), len(zh)))
    if orphans:
        print("注意 词条里没人用的键 %d 个：%s" % (len(orphans), ", ".join(orphans[:6])))
    return 0


if __name__ == "__main__":
    # 控制台是 GBK：不显式声明的话中文输出会变成乱码，门禁日志里就只剩一堆问号
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
    sys.exit(main())