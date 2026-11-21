# -*- coding: utf-8 -*-
"""可读性闸门：按钮/开关的英文文案不许长到挤爆窗口。

为什么只管按钮：KSP 的 GUILayout.Label 会自动折行，按钮不会——按钮文案一长，
要么把同一行里的兄弟按钮挤压到看不见，要么被窗口边裁掉，玩家看到半截词。
窗口宽 560~660 px（见各窗口的 Rect），一行里常并排 2~3 个按钮，
所以单条英文超过 32 字符基本就会出问题。

口径：只对 `GUILayout.Button(` / `GUILayout.Toggle(` / `Selector(` 这段调用里
第一个 `Loc.T(中文, English)` 的英文槽计数。中文槽长度不计（中文一个字两格宽，
但中文本身短，从没出过事）。

用法：`python tools/check_loc_btn.py`；有超长就打印并返回 1。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

MAX_EN = 32
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "KSP", "Source")
CALLS = ("GUILayout.Button(", "GUILayout.Toggle(", "Selector(")
LOC = re.compile(r'Loc\.T\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"', re.S)


def statements(text):
    """把源码切成语句：跳过字符串与注释，遇 ; 断开。"""
    out = []
    start = 0
    i = 0
    n = len(text)
    in_str = False
    in_char = False
    line_com = False
    block_com = False
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if line_com:
            if c == "\n":
                line_com = False
            i += 1
            continue
        if block_com:
            if c == "*" and nxt == "/":
                block_com = False
                i += 2
                continue
            i += 1
            continue
        if in_str:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                in_str = False
            i += 1
            continue
        if in_char:
            if c == "\\":
                i += 2
                continue
            if c == "'":
                in_char = False
            i += 1
            continue
        if c == "/" and nxt == "/":
            line_com = True
            i += 2
            continue
        if c == "/" and nxt == "*":
            block_com = True
            i += 2
            continue
        if c == '"':
            in_str = True
            i += 1
            continue
        if c == "'":
            in_char = True
            i += 1
            continue
        if c == ";":
            out.append((start, i))
            start = i + 1
        i += 1
    out.append((start, n))
    return out


def arg_span(text, open_paren):
    """从 open_paren 处的 ( 起，返回到配对 ) 为止的 (内容起点, 内容终点)。"""
    depth = 0
    i = open_paren
    n = len(text)
    in_str = False
    in_char = False
    while i < n:
        c = text[i]
        if in_str:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                in_str = False
            i += 1
            continue
        if in_char:
            if c == "\\":
                i += 2
                continue
            if c == "'":
                in_char = False
            i += 1
            continue
        if c == '"':
            in_str = True
        elif c == "'":
            in_char = True
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return (open_paren + 1, i)
        i += 1
    return (open_paren + 1, n)


def scan():
    bad = []
    for dirpath, _, names in os.walk(ROOT):
        for name in sorted(names):
            if not name.endswith(".cs"):
                continue
            path = os.path.join(dirpath, name)
            text = io.open(path, encoding="utf-8", newline="").read()
            for (s, e) in statements(text):
                body = text[s:e]
                if "Loc.T(" not in body:
                    continue
                for call in CALLS:
                    idx = body.find(call)
                    if idx < 0:
                        continue
                    pos = s + idx + len(call) - 1
                    a, b = arg_span(text, pos)
                    m = LOC.search(text, a, b)
                    if not m:
                        continue
                    en = m.group(2)
                    if len(en) > MAX_EN:
                        bad.append((len(en), path, en))
                    break
    return bad


def main():
    bad = scan()
    if not bad:
        print("OK 按钮文案：没有一条英文超过 %d 字符" % MAX_EN)
        return 0
    print("按钮文案 %d 条英文超长（上限 %d 字符）：" % (len(bad), MAX_EN))
    for n, path, en in sorted(bad, reverse=True):
        rel = os.path.relpath(path, os.path.join(ROOT, "..", "..")).replace("\\", "/")
        print("  %3d  %s — %s" % (n, rel, en))
    return 1


if __name__ == "__main__":
    sys.exit(main())