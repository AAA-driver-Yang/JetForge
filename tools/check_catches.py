#!/usr/bin/env python3
"""可靠性闸门：空 catch 不许出现在状态与物理路径上。

为什么要有这条：§167 那次故障，`FormatException` 被一个 `catch (Exception) { }` 吞掉，
紧跟其后的喘振风险标记整段没执行，日志一个字没有。症状离原因极远——
回归红了两条不相干的断言，排查方向全错。兜底 catch 会把「我的 bug」变成「别人的数据不对」。

规则：`KSP/Source/` 全部（Core / Diagnostics / FX / Mesh / UI）与顶层 `ModuleJetForge*.cs` 里凡是
`catch { }` / `catch (X) { }`，body 必须把失败说出来（打日志、往读数里写一句、记进告警）；
确实属于「失败即结果」的探测（解析失败退回默认、反射找依赖、拿不到可选数据），
在 catch 上一行或 body 里写 `探测豁免：<原因>` 即通过——豁免要写原因，不是写个词糊过去。

为什么最后把范围铺满整个 `KSP/Source`：一开始只收 `Core/`，理由是 `FX/`、`Mesh/`、`Diagnostics/`
"失败只影响观感"。这个理由只对了一半——观感失败也有两种：羽流少一层、声音少一档，玩家听得出来
却查不出来；而网格归属章打不上会让 ALT 复制出来的件整件透明（§131 那个 bug 的修法本体）。
所以现在全收，逐处判「这是我们自己的计算，还是别人的东西不在」：
前者要说出来，后者写 `探测豁免：<原因>`。
"""
import io, os, re, sys

# 控制台是 GBK 时中文会变成乱码，先把它掰成 UTF-8（gen_textures_hd.py 踩过一次）
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "KSP", "Source"))

# catch (...) { body } —— body 不含花括号（嵌套的先不管，宁可漏报也不误报）
CATCH = re.compile(r"catch\s*(?:\([^)]*\))?\s*\{([^{}]*)\}")
EXEMPT = "探测豁免"


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def in_scope(rel, fn):
    # 顶层只收 ModuleJetForge*.cs（零件模块），其余目录全收
    if rel == ".":
        return fn.startswith("ModuleJetForge")
    return True


def scan():
    bad = []
    for dirpath, _dirnames, filenames in os.walk(ROOT):
        rel = os.path.relpath(dirpath, ROOT)
        for fn in filenames:
            if not fn.endswith(".cs") or not in_scope(rel, fn):
                continue
            path = os.path.join(dirpath, fn)
            src = io.open(path, encoding="utf-8-sig", errors="replace").read()
            for m in CATCH.finditer(src):
                if strip_comments(m.group(1)).strip():
                    continue
                line_start = src.rfind("\n", 0, m.start()) + 1
                line = src.count("\n", 0, m.start()) + 1
                # 豁免标记可以写在 catch 之前的三行内：很多探测的说明要两行才说得清，
                # 逼着塞进一行只会把人话压成黑话。
                prev_start = line_start
                for _ in range(3):
                    prev_start = src.rfind("\n", 0, max(0, prev_start - 1)) + 1
                if EXEMPT in src[prev_start:m.end()]:
                    continue
                bad.append((os.path.relpath(path, ROOT).replace(os.sep, "/"), line,
                            src[line_start:src.find("\n", line_start)].strip()))
    return bad


def main():
    bad = scan()
    if not bad:
        print("OK 空 catch 0 处（KSP/Source 全部：Core / Diagnostics / FX / Mesh 都已交代清楚）")
        return 0
    print("有 %d 处空 catch 会静默吞掉错误：" % len(bad))
    for path, line, text in bad:
        print("  %s:%d  %s" % (path, line, text[:100]))
    print("")
    print("改法二选一：① 把失败说出来（Debug.LogWarning / 往读数里写一句 / 记进告警）；")
    print("            ② 确实是失败即结果的探测，在 catch 上一行写「// 探测豁免：<原因>」。")
    return 1


if __name__ == "__main__":
    sys.exit(main())