"""每个可编辑字段都得有说明：要么悬停提示，要么滑杆右边那句注。

为什么要有这道闸门：设计器里一个滑杆没有说明，玩家只能靠猜——尤其"减速齿轮比"
这种一个数换一整套代价的开关，没说明等于让人拿默认值当唯一选择。
而"漏一条"这种缺陷不会报错、不会崩、测不出来，只能靠一条条数。
"""

from __future__ import print_function

import io
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

# 只查数值/文本输入（Slider、TextField）。
# 开关（Toggle）不算：「配备加力」「矢量喷口」这类标签本身就在说会发生什么，
# 它旁边那句 GUILayout.Label 也在解释代价；而滑杆上的数字，没人能凭标签猜出来。
CALL = re.compile(r"\b(Slider|TextField)\s*\(")
PAIR = re.compile(r'Loc\.T\(\s*"((?:[^"\\]|\\.)*)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)')

FILES = [
    ("KSP/Source/UI/JetForgeDesignerWindow.cs", "KSP/Source/UI/JetForgeDesignerWindow.cs"),
    ("KSP/Source/UI/JetForgeIntakeWindow.cs", "KSP/Source/UI/JetForgeIntakeWindow.cs"),
]


def read(path):
    return io.open(path, encoding="utf-8", newline="").read().replace("\r\n", "\n")


def hint_keys(path):
    """同一个文件里的 Hints 表（没有就返回空表）。"""
    text = read(path)
    m = re.search(r"Hints\s*=\s*new Dictionary<string,\s*string>\s*\{(.*?)\n\s{8}\};", text, re.S)
    if not m:
        return []
    return [mm.group(1).strip() for mm in PAIR.finditer(m.group(1))]


def explained(label, keys):
    s = label.strip()
    if not s:
        return True
    for k in keys:
        if k == s or k in s or s in k:
            return True
    return False


def main():
    missing = []
    total = 0
    for path, _ in FILES:
        text = read(path)
        keys = hint_keys(path)
        lines = text.split("\n")
        for i, line in enumerate(lines):
            for m in CALL.finditer(line):
                # 一次调用可能跨行，把这条语句拼起来看
                stmt = line
                j = i
                while stmt.count("(") > stmt.count(")") and j + 1 < len(lines):
                    j += 1
                    stmt += "\n" + lines[j]
                pairs = list(PAIR.finditer(stmt))
                if not pairs:
                    continue
                label = pairs[0].group(1)
                total += 1
                # 滑杆右边那句注也算说明（进气道窗口全是这种写法）
                has_note = len(pairs) > 1 and pairs[1].group(1).strip() != ""
                if has_note:
                    continue
                if explained(label, keys):
                    continue
                missing.append((path.split("/")[-1], label.strip()))

    if missing:
        print("FAIL 可编辑字段没有说明 %d 处（悬停没有、滑杆右边也没有）" % len(missing))
        for f, label in missing:
            print("    %s：%s" % (f, label))
        return 1
    print("OK   可编辑字段说明：%d 处全有（悬停或滑杆右边那句注）" % total)
    return 0


if __name__ == "__main__":
    sys.exit(main())