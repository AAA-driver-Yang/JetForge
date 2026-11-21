#!/usr/bin/env python3
"""发布就绪检查：这一包现在能不能交给玩家。

分两层，故意分开：
  A 段（默认）——仓库里能自动证明的东西。全部复用 check_release.py 的检查，
                 再补三条它不管的：许可证明细、README 结构、更新日志有没有本轮版本。
  --public    ——上线项：真下载地址、stable、截图目录。这些在开发期必然不满足，
                 所以不放进流水线，只在要发布的那一天跑。

退出码：A 段不合格就 1；--public 下上线项不到位也是 1。
"""
import importlib.util
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

FAILS = []
NOTES = []


def fail(msg):
    FAILS.append(msg)


def note(msg):
    NOTES.append(msg)


def read(path):
    full = os.path.join(ROOT, path)
    if not os.path.isfile(full):
        return None
    with open(full, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def load_release_tools():
    """把 check_release.py 当模块加载：发布闸门的每一条都在这里再跑一遍。

    直接 import 不行——tools 不是包，没有 __init__.py。用 spec 从文件加载，
    模块级的 FAILS 列表就能直接读，省得把同一套规则抄两遍（抄两遍必然分叉）。
    """
    path = os.path.join(ROOT, "tools", "check_release.py")
    spec = importlib.util.spec_from_file_location("jf_check_release", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ---------- A 段：check_release 不管的三条 ----------

def check_license():
    text = read("LICENSE")
    if not text:
        fail("缺 LICENSE")
        return
    if "MIT License" not in text and "MIT" not in text.split("\n")[0]:
        fail("LICENSE 不是 MIT 的正文（发布包里的许可要和 netkan 的 license 字段一致）")
    # 版权行是唯一能证明"这份代码归谁"的东西，空着等于没有许可。
    if not re.search(r"Copyright\s*\(c\)\s*\d{4}", text, re.I):
        fail("LICENSE 里没有版权行（Copyright (c) 年份）")


def check_readme():
    text = read("README.md")
    if not text:
        fail("缺 README.md")
        return
    heads = len(re.findall(r"^##\s+\S", text, re.M))
    if heads < 5:
        fail("README.md 只有 %d 个二级标题：它要能回答「这是什么/依赖/怎么装/怎么用/怎么算的」" % heads)
    if "INSTALL" not in text:
        fail("README.md 没指向 INSTALL.md：新玩家从 README 进来，找不到安装说明就走了")


def check_readme_paths():
    """README 的目录表要跟磁盘对得上——两个方向都查。

    一个方向：表里写的每个路径都得真的存在（§222 那次它就列了两个根本不存在的目录
    `KSP/Source/Arch/` 与 `KSP/Source/Intake/`，而文件其实在 `Core/` 下面）。
    另一个方向：磁盘上的顶层目录都得在表里出现（`proto/` 就这样在表外待了很久）。
    两个方向都不会有任何东西报错：链接点了 404，读者以为自己找错了地方。
    """
    text = read("README.md")
    listed = re.findall(r"`([A-Za-z0-9_][A-Za-z0-9_./-]*/)`", text)
    for rel in sorted(set(listed)):
        if not os.path.isdir(os.path.join(ROOT, rel)):
            fail("README 的目录表列了 `%s`，磁盘上不存在（读者会以为自己的包缺了东西）" % rel)
    for name in sorted(os.listdir(ROOT)):
        full = os.path.join(ROOT, name)
        if not os.path.isdir(full) or name.startswith("_") or name.startswith(".") or name == "out":
            continue  # `_release`/`_scratch` 是产物与草稿，`out/` 是打包输出，`.` 开头不是内容目录
        # 顶层目录可以只以更长的路径出现在表里（`GameData` 就是这样以 `GameData/JetForge/` 出现的），
        # 所以判据是"有没有任何一行从它开始"，不是"有没有恰好这一行"。
        if not any(rel == name + "/" or rel.startswith(name + "/") for rel in listed):
            fail("磁盘上有顶层目录 `%s/`，README 的目录表里没有它" % name)


def check_pack_readme():
    """随包发的玩家说明（`GameData/JetForge/README.md`）必须存在、成对、且页数属实。

    为什么单独查这一个：它跟着 zip 走，是玩家解开包装之后**唯一会自己打开**的说明。
    它曾经在 70 轮里没被碰过 —— 依赖表还对，但页签数写着 8（实际 9）、目录里
    少了独立进气道零件、少了两处玩家会用到的功能。这类错没有任何东西会报。
    """
    base = os.path.join(ROOT, "GameData", "JetForge")
    zh = os.path.join(base, "README.md")
    en = os.path.join(base, "README.en.md")
    if not os.path.exists(zh):
        fail("缺 GameData/JetForge/README.md：随包说明没了，玩家解开 zip 也不知道这是什么")
        return
    if not os.path.exists(en):
        fail("有随包 README.md 却没有 README.en.md（英文玩家照旧只看到中文）")
    text = read(zh)
    for need in ("Waterfall", "RocketSoundEnhancement", "INSTALL"):
        if need not in text:
            fail("随包 README 没提 %s：硬依赖与安装入口必须写在玩家会打开的那份里" % need)
    check_page_count(text)
    check_pack_counts(text)


def check_pack_counts(text):
    """随包 README 里几个"数得出来"的数，必须等于盘上的真相。

    这些数以前用中文数字写（"九层""七套"），读着顺但验不了 —— 而验不了的数字
    正是最容易腐烂的那一类（§225.5）。改成阿拉伯数字之后，它们就各自有了裁判：
    羽流的 FX 条数在 Waterfall.cfg 里，音源层数在 Sounds/ 里，贴图套数在 Textures/ 里。
    """
    base = os.path.join(ROOT, "GameData", "JetForge")
    pairs = [
        (r"(\d+)\s*个\s*FX", os.path.join(base, "Waterfall.cfg"), "ModuleWaterfallFX", "羽流 FX 条数", False),
        (r"(\d+)\s*层", os.path.join(base, "Sounds"), ".wav", "循环音源层数", False),
        (r"(\d+)\s*套贴图", os.path.join(base, "Textures"), ".png", "贴图套数", True),
    ]
    for pat, target, marker, what, base_only in pairs:
        m = re.search(pat, text)
        if not m:
            note("随包 README 没写%s（那就不用查）" % what)
            continue
        if not os.path.exists(target):
            note("找不到 %s，跳过%s检查" % (os.path.basename(target), what))
            continue
        real = count_in(target, marker, base_only)
        if int(m.group(1)) != real:
            fail("随包 README 说%s是 %s，盘上是 %d（说明落后于文件）" % (what, m.group(1), real))


def count_in(target, marker, base_names_only):
    if os.path.isdir(target):
        names = [n for n in os.listdir(target) if n.lower().endswith(marker)]
        if base_names_only:
            # 每套两张：一张本色 + 一张 _N 法线，所以套数是本色那张的张数。
            return len([n for n in names if not n[: -len(marker)].endswith("_N")])
        return len(names)
    return read(target).count(marker)


def check_page_count(text):
    """说明里写的页签数必须等于 `Core/DesignTabs.cs` 里的页数。

    这是仓库里少数"文档里的数字有单一来源"的地方：页签是常量，数是数得出来的。
    写死的数只在一个方向上有用——它能被比自己更准的东西验。
    """
    src = os.path.join(ROOT, "KSP", "Source", "Core", "DesignTabs.cs")
    if not os.path.exists(src):
        note("找不到 DesignTabs.cs，跳过页签数检查")
        return
    real = len(re.findall(r"public\s+const\s+int\s+\w+\s*=\s*\d+\s*;", read(src)))
    m = re.search(r"共\s*(\d+)\s*页", text)
    if not m:
        note("随包 README 没写页签数（那就不用查）")
        return
    if int(m.group(1)) != real:
        fail("随包 README 说设计器有 %s 页，DesignTabs 里是 %d 页（说明落后于代码）"
             % (m.group(1), real))


def check_changelog_entry(ver):
    """本轮版本必须在更新日志里有一条——发版时最容易忘的就是这个。

    只比三段号：`.version` 里是 1.0.0.0（AssemblyVersion 要四段），
    更新日志的标题是 1.0.0。
    """
    if not ver:
        note("版本号没读到，跳过更新日志条目检查")
        return
    three = ".".join(ver.split(".")[:3])
    text = read("CHANGELOG.md")
    if not text:
        fail("缺 CHANGELOG.md")
        return
    if not re.search(r"^##\s+" + re.escape(three) + r"\b", text, re.M):
        fail("CHANGELOG.md 里没有 %s 这一条（发布了却没写更新日志，玩家不知道改了什么）" % three)
    en = read("CHANGELOG.en.md")
    if en and not re.search(r"^##\s+" + re.escape(three) + r"\b", en, re.M):
        fail("CHANGELOG.en.md 里没有 %s 这一条" % three)


# ---------- --public：上线项 ----------

def check_public(ver):
    # 出包脚本用的是三段号（JetForge-1.0.0.zip），`.version` 里是四段（AssemblyVersion 要四段）。
    three = ".".join((ver or "1.0.0").split(".")[:3])
    net = read("_release/JetForge-%s.netkan" % three)
    if net is None:
        # 还没出包：这在开发期很正常，但 --public 是要发布，必须出过包。
        fail("没找到 _release/JetForge-%s.netkan：先跑 tools/make_release.sh" % three)
        return
    dl = re.search(r'"download"\s*:\s*"([^"]*)"', net)
    url = dl.group(1) if dl else ""
    if not url or "example.invalid" in url or "localhost" in url:
        fail("download 还是占位地址（%s）：玩家点安装会拿到 404" % (url or "空"))
    if not url.lower().startswith("https://"):
        fail("download 不是 https：CKAN 会拒收")
    status = re.search(r'"release_status"\s*:\s*"([^"]*)"', net)
    if not status or status.group(1) != "stable":
        fail("release_status 还是 %s：正式发布要 stable（先 testing 试过也可以）"
             % (status.group(1) if status else "缺"))
    shots = [n for n in os.listdir(ROOT) if n.lower() == "screenshots"]
    if not shots:
        fail("仓里没有 screenshots/ 目录：SpaceDock 页面上没有截图，玩家不会点下载")
    else:
        d = os.path.join(ROOT, shots[0])
        pngs = [n for n in os.listdir(d) if n.lower().endswith(".png")]
        if len(pngs) < 3:
            fail("screenshots/ 里只有 %d 张图：设计器/飞行/剖视三张是最低配" % len(pngs))


def main():
    cr = load_release_tools()
    ver = cr.check_version()
    cr.check_parts()
    cr.check_engine_limits()
    cr.check_ksp_version()
    cr.check_dependencies()
    cr.check_netkan_meta()
    cr.check_doc_versions()
    cr.check_doc_pairs()
    cr.check_sources()
    # 上述函数把失败写进 check_release 自己的 FAILS，收过来统一报。
    FAILS.extend(cr.FAILS)
    NOTES.extend(cr.NOTES)

    check_license()
    check_readme()
    check_readme_paths()
    check_pack_readme()
    check_changelog_entry(ver)

    if "--public" in sys.argv:
        check_public(ver)
        print("==")
        print("B 段只能人做的事（勾在 RELEASE.md 里）：截图、英文审校、英文环境走一遍、")
        print("论坛描述、CKAN PR、发布后从 CKAN 装一遍再自检。这些闸门查不出来，别当它们不存在。")

    for n in NOTES:
        print("注意 %s" % n)
    if FAILS:
        print("FAIL 发布就绪（A 段）：%d 条" % len(FAILS))
        for f in FAILS:
            print("  - " + f)
        return 1
    print("OK   发布就绪（A 段）：版本 %s｜许可/README/更新日志齐｜元数据与文档口径一致" % (ver or "?"))
    return 0


if __name__ == "__main__":
    sys.exit(main())