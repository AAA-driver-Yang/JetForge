"""找"只有声明、没有调用"的方法。

判据与 tools/check_dead_fields.py 同源：数名字在全仓的出现次数。
- `.field` 那种漏判（类内部不带点读）在这里不存在，因为方法调用一定带括号，
  所以数 `Name` 的全部出现即可；声明本身也算一次，`出现次数 - 声明次数 <= 0` 即无人调用。
- 语料要含测试工程：`JetForge.Test` 会调用一批只给测试用的公共方法（CSV、扫油门之类），
  只看 KSP/Source 会把它们全报成死码。
- 反射调用看不见，进 tools/dead_methods_allow.txt，一行 `名字 # 理由`。
  这一条必须写理由：以前那份白名单不写理由，于是没人知道那几行是"KSP 回调"还是"忘了删"。
"""

import io
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCES = ["KSP/Source", "JetForge.Test"]
ALLOW = os.path.join(ROOT, "tools", "dead_methods_allow.txt")

DECL = re.compile(
    r"^[ \t]*(?:public|internal|private|protected)[^\n;(=]*?\b([A-Z][A-Za-z0-9_]*)\s*\([^;]*?\)\s*(?:\{|=>)",
    re.M)


def corpus():
    files = []
    for rel in SOURCES:
        base_dir = os.path.join(ROOT, *rel.split("/"))
        for base, _, names in os.walk(base_dir):
            for n in names:
                if n.endswith(".cs"):
                    files.append(os.path.join(base, n))
    return {p: io.open(p, encoding="utf-8", errors="replace").read() for p in files}


def allowlist():
    ok = {}
    if not os.path.isfile(ALLOW):
        return ok
    for line in io.open(ALLOW, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if "#" in line:
            name, why = line.split("#", 1)
            ok[name.strip()] = why.strip()
        else:
            ok[line] = ""
    return ok


def main():
    texts = corpus()
    joined = "\n".join(texts.values())
    decls = {}
    for path, text in texts.items():
        rel = os.path.relpath(path, ROOT).replace("\\", "/")
        if not rel.startswith("KSP/"):
            continue
        for m in DECL.finditer(text):
            decls.setdefault(m.group(1), []).append(
                (os.path.relpath(path, ROOT).replace("\\", "/"), text[:m.start()].count("\n") + 1))
    allowed = allowlist()
    dead = []
    for name, places in decls.items():
        seen = len(re.findall(r"\b%s\b" % re.escape(name), joined))
        if seen - len(places) <= 0:
            dead.append((places[0][0], places[0][1], name))
    dead.sort()

    bad = [d for d in dead if d[2] not in allowed]
    verbose = "-v" in sys.argv
    if verbose:
        for f, ln, name in dead:
            mark = "放过" if name in allowed else "FAIL"
            print("  %s  %s:%d  %s" % (mark, f, ln, name))
    if bad:
        print("FAIL  只有声明没有调用的方法 %d 个：" % len(bad))
        for f, ln, name in bad:
            print("  %s:%d  %s" % (f, ln, name))
        print("确实是死码就删掉；由 KSP/Unity 反射调用的，写进 tools/dead_methods_allow.txt 并说明理由。")
        return 1
    print("OK   没有只声明不调用的方法（放过 %d 个反射入口）" % len(allowed))
    return 0


if __name__ == "__main__":
    sys.exit(main())