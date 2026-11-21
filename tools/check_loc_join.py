"""英文句子拼接闸门。

为什么需要它：`Loc.T("中文。", "English.") + Loc.T("中文后半", "second half")` 这种写法编译得过、
回归全绿、中文界面读起来也对——**只有英文会露馅**：两段之间的标点/空格没人管，
拼出来是 `...limit.Where` 或 `...m,above that`。第 97 轮第一次真的把界面切成英文才看见它。

判据：一个字符串字面量以英文标点（, . ; :）结尾、没有尾随空格，紧接着用 `+` 拼下一段，
而下一段的开头是英文字母 —— 就是拼缝。中文用全角标点（，。；：）不受影响，因为它不匹配。

用法：python tools/check_loc_join.py [--verbose]
退出码 0 = 干净，1 = 有拼缝。
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

ROOT = "KSP/Source"
# 英文标点紧贴收尾（标点后面**没有**空格）又用 + 接下一段字面量。
# 注意 `[,.;:]"` 里不许夹空格：`atomise, ` 这种带尾空格的写法是对的，
# 第一版写成 `[,.;:](?:[^"\\]|\\.)*?"` 会把它们全当成拼缝，报出三条假警报。
SEAM = re.compile(r'"(?:[^"\\]|\\.)*?[,.;:]"\s*\)?\s*\+\s*(?:Loc\.T\()?\s*"([A-Za-z])')
# 也被这条覆盖：Loc.T("zh","en,") + Loc.T("zh2","next")
SEAM2 = re.compile(r'Loc\.T\(\s*"(?:[^"\\]|\\.)*"\s*,\s*"(?:[^"\\]|\\.)*?[,.;:](?:[^"\\]|\\.)*?"\s*\)\s*\+\s*Loc\.T\(\s*"(?:[^"\\]|\\.)*"\s*,\s*"(?:[A-Za-z])')


def statements(text):
    """按 C# 语句切分，跳过字符串/字符/注释（拼接串里的 `;` 不能当语句边界）。"""
    out = []
    buf = []
    i = 0
    n = len(text)
    in_str = False
    in_chr = False
    while i < n:
        ch = text[i]
        if in_str:
            buf.append(ch)
            if ch == "\\" and i + 1 < n:
                buf.append(text[i + 1])
                i += 2
                continue
            if ch == '"':
                in_str = False
            i += 1
            continue
        if in_chr:
            buf.append(ch)
            if ch == "\\" and i + 1 < n:
                buf.append(text[i + 1])
                i += 2
                continue
            if ch == "'":
                in_chr = False
            i += 1
            continue
        if ch == '"':
            in_str = True
            buf.append(ch)
            i += 1
            continue
        if ch == "'":
            in_chr = True
            buf.append(ch)
            i += 1
            continue
        if ch == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            if j < 0:
                break
            i = j + 1
            continue
        if ch == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i)
            if j < 0:
                break
            i = j + 2
            continue
        if ch == ";":
            out.append("".join(buf))
            buf = []
            i += 1
            continue
        buf.append(ch)
        i += 1
    if buf:
        out.append("".join(buf))
    return out


def scan():
    bad = []
    for dirpath, dirs, files in os.walk(ROOT):
        for f in files:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(dirpath, f).replace("\\", "/")
            text = open(p, encoding="utf-8").read()
            for stmt in statements(text):
                for m in SEAM.finditer(stmt):
                    if SEAM2.search(stmt) or True:
                        frag = stmt[max(0, m.start() - 60):m.start() + 20]
                        bad.append((p, frag.replace("\n", " ")))
    return bad


def main():
    verbose = "--verbose" in sys.argv
    bad = scan()
    if not bad:
        print("OK 英文拼接：没有一处标点后直接接下一段")
        return 0
    print("英文拼接 %d 处：标点后直接接下一段，英文会挤成 `m,above`" % len(bad))
    for p, frag in bad:
        print("  %s：…%s…" % (p, frag))
        if verbose:
            print()
    return 1


if __name__ == "__main__":
    sys.exit(main())