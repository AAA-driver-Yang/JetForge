#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
本地化闸门：中文字面量只许减，不许增。

为什么需要这道闸门：这个 mod 原本全中文（4565 条含中文的字符串字面量，101 个文件），
玩家选了"中英双语"，但这活得分十几轮才做得完。分很多轮做的工作最怕的不是做得慢，
而是**边做边加新的中文**——那样永远做不完。所以先把现状记成基线，
以后每次提交只要有任何文件的中文条数比基线多，这条流水线就红。

用法：
    python tools/check_loc.py            # 检查（基线 tools/loc_baseline.json）
    python tools/check_loc.py --write    # 把当前状态写成新基线（只在真的翻译掉一批时用）

注意：这里数的是"含汉字的字符串字面量"，不是"汉字数"。一条长句算一条，
因为要判断的是"还有多少句没翻"，不是"还剩多少个字"。
"""

import json
import os
import re
import sys

# Windows 控制台默认是 GBK，直接 print 中文会输出乱码；闸门的输出是给人看的，先修编码。
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "KSP", "Source")
BASELINE = os.path.join(ROOT, "tools", "loc_baseline.json")

HAN = re.compile("[" + chr(0x4E00) + "-" + chr(0x9FFF) + "]")


def strip_comments(text):
    """去掉 // 与 /* */ 注释，注释里的中文不算玩家可见文字。"""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == "'":
            # 字符字面量：`'"'` 是一个含双引号的 char。不先吃掉它，下面那个 `"` 会被
            # 当成长字符串的开头，于是一路吞到下一个引号——中文判断从此错位，
            # 本该算的没算、不该算的算进来（§258 就是被 `c == '"'` 这一行坑的）。
            out.append(c)
            i += 1
            while i < n:
                out.append(text[i])
                if text[i] == "\\" and i + 1 < n:
                    out.append(text[i + 1])
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        if c == '"':
            out.append(c)
            i += 1
            while i < n:
                out.append(text[i])
                if text[i] == "\\" and i + 1 < n:
                    out.append(text[i + 1])
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


def count_file(path):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        text = f.read()
    body = strip_comments(text)
    # 已经翻过的句子长这样：Loc.T("中文", "English")——中文那一半是**原文**，
    # 不是"还没翻"。所以先把成对的 Loc.T 整段挖掉，剩下还带汉字的字面量才算没翻。
    # 挖不干净的（比如英文那一半是空串）会留下来，正好该留下来。
    body = re.sub(r'Loc\.T\(\s*"(?:[^"\\]|\\.)*"\s*,\s*"(?:[^"\\]|\\.)*"\s*\)', "", body)
    n = 0
    for m in re.finditer(r'"((?:[^"\\]|\\.)*)"', body):
        if HAN.search(m.group(1)):
            n += 1
    return n


def scan():
    cur = {}
    for dirpath, dirs, files in os.walk(SRC):
        # Diagnostics 是自检日志，只有开发者看（写进 selftest.log，玩家看不到），
        # 这里只盯玩家可见的位置：Core / UI / FX / Mesh / Module*。
        dirs[:] = [d for d in dirs if d != "Diagnostics"]
        for name in files:
            if not name.endswith(".cs"):
                continue
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, ROOT).replace("\\", "/")
            c = count_file(full)
            if c > 0:
                cur[rel] = c
    return cur


LOCDIR = os.path.join(ROOT, "GameData", "JetForge", "Localization")
KEY = re.compile(r"(#autoLOC_[A-Za-z0-9_]+)\s*=")


def check_entries():
    """游戏内词条必须成对：zh-cn 里有、en-us 里没有的键，英文玩家看到的就是 #autoLOC_…。

    为什么用文件比对而不是进游戏验：KSP 这个版本的 Localizer.CurrentLanguage 是只读的，
    自检切不了语言（切不动就如实说"没验"）。那就退一步，把"键成对"这件事在文件层面钉死。
    """
    if not os.path.isdir(LOCDIR):
        print("FAIL 本地化词条：找不到 %s" % os.path.relpath(LOCDIR, ROOT))
        return 1
    keys = {}
    for name in sorted(os.listdir(LOCDIR)):
        if not name.endswith(".cfg"):
            continue
        lang = name[:-4]
        with open(os.path.join(LOCDIR, name), "r", encoding="utf-8", errors="replace") as f:
            keys[lang] = set(KEY.findall(f.read()))
    if "en-us" not in keys or "zh-cn" not in keys:
        print("FAIL 本地化词条：%s 里缺 en-us.cfg 或 zh-cn.cfg" % os.path.relpath(LOCDIR, ROOT))
        return 1
    en, zh = keys["en-us"], keys["zh-cn"]
    bad = 0
    if zh - en:
        print("FAIL 本地化词条：%d 个键只有中文" % len(zh - en))
        for k in sorted(zh - en)[:8]:
            print("  缺英文：%s" % k)
        bad += 1
    if en - zh:
        print("注意 本地化词条：%d 个键只有英文（中文玩家会看到键名）" % len(en - zh))
        for k in sorted(en - zh)[:8]:
            print("  缺中文：%s" % k)
        bad += 1
    if bad == 0:
        print("OK 本地化词条：%d 个键中英成对" % len(zh))
    return bad


def main():
    cur = scan()
    total = sum(cur.values())
    entry_bad = check_entries()

    if "--write" in sys.argv:
        with open(BASELINE, "w", encoding="utf-8") as f:
            json.dump({"total": total, "files": cur}, f, ensure_ascii=False, indent=1, sort_keys=True)
        print("OK 本地化基线已写入：%d 条 / %d 个文件" % (total, len(cur)))
        return 1 if entry_bad else 0

    if entry_bad:
        return 1

    if not os.path.exists(BASELINE):
        print("FAIL 本地化闸门：找不到基线 tools/loc_baseline.json（先跑 --write）")
        return 1

    with open(BASELINE, "r", encoding="utf-8") as f:
        base = json.load(f)
    base_files = base.get("files", {})

    worse = []
    for rel, n in sorted(cur.items()):
        b = base_files.get(rel, 0)
        if n > b:
            worse.append((rel, b, n))
    # 基线里有、现在没了（文件删了或翻干净了）不算错，只是别让它悄悄变大。

    if worse:
        print("FAIL 本地化闸门：%d 个文件的中文字面量变多了" % len(worse))
        for rel, b, n in worse:
            print("  %s：%d → %d（多 %d 条）" % (rel, b, n, n - b))
        print("  双语迁移期间新加的文字请同时给英文；日志类的确只能用中文时，"
              "把它放进 Logger，而不是玩家可见的位置。")
        return 1

    print("OK 本地化闸门：中文字面量 %d 条 / %d 个文件（基线 %d 条），没有增加"
          % (total, len(cur), base.get("total", 0)))
    done = [rel for rel in base_files if rel not in cur]
    if done:
        print("注意 已翻干净的文件 %d 个：%s" % (len(done), ", ".join(sorted(done)[:5])))
    return 0


if __name__ == "__main__":
    sys.exit(main())