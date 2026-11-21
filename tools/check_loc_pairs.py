# -*- coding: utf-8 -*-
"""扫全仓 Loc.T(中文, English) 的坏配对。

坏的两种：
1. 第一个参数没有汉字、第二个有 —— 说明中文槽被英文占了（第 81 轮在 StageLoad 里真踩过：
   "没配平" 那句两边都写成英文，回归立刻报超差，但如果没人钉它就静默变英文界面永远缺这句）；
2. 两边完全相同且含汉字 —— 没翻或翻错位。
"""
import io, glob, re, sys

sys.stdout.reconfigure(encoding="utf-8")

STR = r'"((?:[^"\\]|\\.)*)"'
PAIR = re.compile(r"Loc\.T\(\s*" + STR + r"\s*,\s*" + STR + r"\s*\)", re.S)
CJK = re.compile(r"[\u4e00-\u9fa5]")


def main():
    pairs = same = zh_empty = 0
    bad = []
    for path in sorted(glob.glob("KSP/Source/**/*.cs", recursive=True)):
        text = io.open(path, encoding="utf-8", errors="replace").read()
        for m in PAIR.finditer(text):
            zh, en = m.group(1), m.group(2)
            pairs += 1
            if zh == en:
                same += 1
                # 两边完全相同：'SFC' 这种缩写本来就该一样；但一整句英文两边一样，
                # 说明中文槽被英文占了（第 81 轮我自己写出的 bug：整句英文塞进中文槽，
                # 中文界面直接变英文，回归里只有一条"含某中文词"的断言碰巧抓到）。
                if CJK.search(zh) or (" " in zh and len(zh) > 12):
                    bad.append((path, "both sides identical and untranslated", zh[:50]))
            elif not CJK.search(zh) and CJK.search(en):
                zh_empty += 1
                bad.append((path, "Chinese slot holds English", (zh or "(empty)")[:40] + " | " + en[:40]))
    print("成对 %d 条｜两边相同 %d｜中文槽空缺 %d" % (pairs, same, zh_empty))
    for path, why, sample in bad[:20]:
        print("  FAIL %s：%s：%s" % (path, why, sample))
    if bad:
        print("FAIL 本地化配对 %d 处需要人工看" % len(bad))
        return 1
    print("OK 本地化配对：没有中文槽被英文占、没有两边相同的未译句")
    return 0


sys.exit(main())