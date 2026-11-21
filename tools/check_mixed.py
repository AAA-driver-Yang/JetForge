# -*- coding: utf-8 -*-
"""半翻检查：同一句里既有已翻的 Loc.T(...)，又有裸中文，就是半中半英。

为什么需要它：Loc.T 的成对设计能保证「一处漏翻=英文参数空着」，但**拼接式**
文案绕过了这个保证——

    "缺省开。关掉之后长度改用存档里的手填值（Length）：那是给"
    + "「真机多长我清楚」的老手留的口子。一般别关——手填的数字跟部件对不上，"
    + Loc.T("就会出现凭空多出来的管道…", "Off means you supply the length by hand…")

编译过、回归绿、中文界面完全正常，只有英文玩家看到半句中文。本地化闸门也数得
到那两条裸中文，但它只告诉你有几条，不告诉你在哪、也不说它和已翻的那半句粘在
一起——所以单独一个闸门，口径和 check_loc 一样：**按文件计数，只许降不许升**。
计数的意义是"还剩几处没拆干净"，不是"有几个字"。

切语句不能按 `;` 硬切：英文里带分号的句子（"…ram total pressure/temperature;
it is not the face Mach."）会把一条 o.Add(...) 从中间切开，切开之后前半截全是
"已翻的 Loc.T + 裸中文"，于是每一条正经翻译都会被误报。必须跳过字符串内部。
"""
import io
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_loc as G

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "KSP", "Source")
BASE = os.path.join(ROOT, "tools", "mixed_baseline.json")
LOC = re.compile(r'Loc\.T\(\s*"(?:[^"\\]|\\.)*"\s*,\s*"(?:[^"\\]|\\.)*"\s*\)')
LIT = re.compile(r'"((?:[^"\\]|\\.)*)"')


def statements(body):
    """按分号切语句，但跳过字符串字面量里的分号。返回 [(起始偏移, 语句文本)]。"""
    out = []
    start = 0
    i = 0
    n = len(body)
    while i < n:
        c = body[i]
        if c == '"':
            i += 1
            while i < n:
                if body[i] == "\\":
                    i += 2
                    continue
                if body[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if c == ";":
            out.append((start, body[start:i]))
            start = i + 1
        i += 1
    if start < n:
        out.append((start, body[start:]))
    return out


def sentenceish(s):
    """一个裸中文片段算不算"半句话"。

    单位串（`片`/`级`/`罐`/`轴`）、选项名（`无控`/`低`/`中`/`高`）会和同一条语句里的
    Loc.T 撞上，但它们不是"半句话"——它们本来就是短标签，翻不翻都不影响读得懂。
    判据给得笨一点：带中文标点，或者长度 ≥ 8 个字。**短标签不算，成句的才算。**
    这比维护一张豁免名单可靠：名单会忘记更新，规则不会。
    """
    if len(s) >= 8:
        return True
    return any(ch in s for ch in "。；，：！？、")


def scan_file(path):
    raw = io.open(path, encoding="utf-8", errors="replace").read()
    body = G.strip_comments(raw)
    hits = []
    for start, seg in statements(body):
        if LOC.search(seg) is None:
            continue
        bare = [m.group(1) for m in LIT.finditer(LOC.sub("", seg))
                if G.HAN.search(m.group(1)) and sentenceish(m.group(1))]
        if bare:
            line = body.count("\n", 0, start) + 1
            hits.append((line, bare))
    return hits


def scan():
    cur = {}
    for dirpath, dirs, files in os.walk(SRC):
        dirs[:] = [d for d in dirs if d != "Diagnostics"]
        for name in sorted(files):
            if not name.endswith(".cs"):
                continue
            full = os.path.join(dirpath, name)
            hits = scan_file(full)
            if hits:
                rel = os.path.relpath(full, ROOT).replace("\\", "/")
                cur[rel] = hits
    return cur


def main():
    write = "--write" in sys.argv
    show = "--list" in sys.argv
    cur = scan()
    counts = dict((k, len(v)) for k, v in cur.items())
    total = sum(counts.values())

    if write:
        io.open(BASE, "w", encoding="utf-8", newline="\n").write(
            json.dumps(counts, ensure_ascii=False, indent=1, sort_keys=True) + "\n")
        print("OK 半翻基线已写入：%d 处 / %d 个文件" % (total, len(counts)))
        return 0

    old = {}
    if os.path.exists(BASE):
        old = json.loads(io.open(BASE, encoding="utf-8").read())

    bad = []
    for rel, n in sorted(counts.items()):
        was = old.get(rel, 0)
        if n > was:
            bad.append("%s：%d → %d（多 %d 处）" % (rel, was, n, n - was))
    gone = [k for k in old if counts.get(k, 0) == 0]

    if show or bad:
        sys.stdout.reconfigure(encoding="utf-8")
        for rel, hits in sorted(cur.items()):
            for line, bare in hits:
                print("%s:%d  裸中文 %s" % (rel, line, " | ".join(b[:40] for b in bare)))
    if bad:
        print("FAIL 半翻闸门：这些文件的半中半英变多了")
        for line in bad:
            print("  " + line)
        print("  修法：整句塞进一个 Loc.T（中文在左、英文在右），别用 \"中文\" + Loc.T(...) 拼。")
        return 1
    note = "（%d 处待拆）" % total if total else ""
    extra = ("；已拆干净：" + "、".join(gone)) if gone else ""
    print("OK 半翻闸门：%d 处 / %d 个文件，没有变多%s%s" % (total, len(counts), note, extra))
    return 0


if __name__ == "__main__":
    sys.exit(main())