"""发布前闸门：出包之前把"会让人装不进去 / 找不到贴图 / 开着调试开关"的事先拦下来。

它检查的四类东西，都是**只有在别人机器上才会炸**的：

1. 版本号一致（AssemblyInfo.cs / JetForge.version）——
   不一致的话 AVC 报的版本和玩家装上的对不上。
2. 零件 cfg 引用的模型与贴图真的在包里 —— 缺一张就整件变白模，而且日志里
   只有一行 warning，玩家看到的是"这 mod 的东西是白的"。
3. 源码里没有调试残留（JF_PROBE 探针接线、Console.WriteLine）—— 探针一旦漏进发布包，
   玩家装完控制台会被刷屏，而我们自己看不到。
4. 发布包里该有的文档都在（README / INSTALL / LICENSE / CHANGELOG）。

用法：python tools/check_release.py        # 只看源码与配置，不需要先出包
      python tools/check_release.py --zip   # 连 _release 里最新的 zip 一起查
"""

import hashlib
import io
import json
import os
import re
import sys
import zipfile

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAMEDATA = os.path.join(ROOT, "GameData", "JetForge")
SOURCE = os.path.join(ROOT, "KSP", "Source")
RELEASE = os.path.join(ROOT, "_release")

FAILS = []
NOTES = []


def fail(msg):
    FAILS.append(msg)


def note(msg):
    NOTES.append(msg)


def read(path):
    with io.open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def check_version():
    asm = os.path.join(SOURCE, "Properties", "AssemblyInfo.cs")
    ver = os.path.join(GAMEDATA, "JetForge.version")
    if not os.path.exists(asm):
        fail("找不到 KSP/Source/Properties/AssemblyInfo.cs")
        return None
    if not os.path.exists(ver):
        fail("找不到 GameData/JetForge/JetForge.version（AVC 靠它认版本）")
        return None
    m = re.search(r'AssemblyVersion\("([^"]+)"\)', read(asm))
    if not m:
        fail("AssemblyInfo.cs 里没有 AssemblyVersion")
        return None
    av = m.group(1)
    # AssemblyVersion 必须是四段（CLR 要求），版本文件是三段；
    # 比对时把尾部的 .0 去掉再比，否则 "1.0.0.0" 与 "1.0.0" 会被判成不一致。
    avc = av[:-2] if re.fullmatch(r"\d+\.\d+\.\d+\.0", av) else av
    try:
        vj = json.loads(read(ver))
    except Exception as e:
        fail("JetForge.version 不是合法 JSON：%s" % e)
        return None
    mv = str(vj.get("VERSION", {}).get("MAJOR", "")) + "." + \
        str(vj.get("VERSION", {}).get("MINOR", "")) + "." + \
        str(vj.get("VERSION", {}).get("PATCH", ""))
    if avc != mv:
        fail("版本号不一致：AssemblyInfo=%s，JetForge.version=%s" % (avc, mv))
    return av


def check_parts():
    parts = os.path.join(GAMEDATA, "Parts")
    if not os.path.isdir(parts):
        fail("GameData/JetForge/Parts 不存在")
        return
    for name in sorted(os.listdir(parts)):
        if not name.endswith(".cfg"):
            continue
        text = read(os.path.join(parts, name))
        for m in re.finditer(r'model\s*=\s*(\S+)', text):
            rel = m.group(1).replace("\\", "/")
            if rel.startswith("Squad/") or "Parts/" in rel and not rel.startswith("JetForge/"):
                continue   # 原版件借用的是游戏自带模型，不该在包里找
            path = os.path.join(GAMEDATA, rel + ".mu")
            alt = os.path.join(GAMEDATA, rel + ".cfg")
            if not (os.path.exists(path) or os.path.exists(alt)):
                fail("%s 引用的模型不存在：%s" % (name, rel))
        for m in re.finditer(r'texture\s*=\s*(\S+)', text):
            rel = m.group(1).replace("\\", "/")
            base = os.path.join(GAMEDATA, rel)
            if not (os.path.exists(base + ".png") or os.path.exists(base + ".dds")):
                fail("%s 引用的贴图不存在：%s" % (name, rel))


def check_engine_limits():
    """发动机 cfg 里那些「原版会当判据用」的字段。

    `machLimit` 原版是熄火上限，我们的飞行循环读的是同一个 `flameout` 字段，
    所以它填小了就等于给整台机器钉了个不是我们算出来的马赫天花板。
    原版喷气件取 0.75~2.5（正好是它们各自的速度包线），本模组的预设能飞到
    Ma 2.2~2.5 —— 这条要求它远高于任何设计包线，让限制由我们自己的模型给。
    """
    path = os.path.join(GAMEDATA, "Parts", "JetForgeEngine.cfg")
    if not os.path.exists(path):
        fail("GameData/JetForge/Parts/JetForgeEngine.cfg 不存在")
        return
    m = re.search(r'^\s*machLimit\s*=\s*([0-9.]+)', read(path), re.M)
    if not m:
        fail("JetForgeEngine.cfg 没写 machLimit（原版缺省行为未定义，别靠猜）")
        return
    v = float(m.group(1))
    if v < 3.0:
        fail("JetForgeEngine.cfg 的 machLimit = %g：原版拿它当熄火上限，"
             "低于预设能飞的 Ma 2.2~2.5 就会先把火车判灭（要求 >= 3.0）" % v)


def check_ksp_version():
    """兼容区间必须是「真跑过的」，而且两份元数据得自洽。

    这里拦的是一种特别贵的话：`.version` 里写一个没人验过的兼容下限
    （曾经是 1.8.0，实际只在本机 1.12.4 上跑过自检）。许诺不存在的东西，
    玩家装上撞 `MissingMethodException`，然后把"装完是坏的"算在模组头上。

    自洽性（min <= 目标版本 <= max）是硬要求；与本机 KSP.log 对不上只提醒——
    别人的机器装 1.12.5 是正常的。
    """
    p = os.path.join(GAMEDATA, "JetForge.version")
    if not os.path.exists(p):
        return
    try:
        vj = json.loads(read(p))
    except Exception:
        return          # check_version 已经报过

    def trip(key):
        d = vj.get(key) or {}
        try:
            return (int(d.get("MAJOR", 0)), int(d.get("MINOR", 0)), int(d.get("PATCH", 0)))
        except Exception:
            return None

    ver, lo, hi = trip("KSP_VERSION"), trip("KSP_VERSION_MIN"), trip("KSP_VERSION_MAX")
    if ver is None:
        fail("JetForge.version 缺 KSP_VERSION（AVC 靠它判兼容）")
        return
    if lo is not None and lo > ver:
        fail("JetForge.version 的 KSP_VERSION_MIN %s 高于 KSP_VERSION %s" % (lo, ver))
    if hi is not None and hi < ver:
        fail("JetForge.version 的 KSP_VERSION_MAX %s 低于 KSP_VERSION %s" % (hi, ver))

    # 本机实测版本（KSP.log 头一行带版本号）：只提醒，不拦
    log = os.path.join(os.path.dirname(ROOT), "Kerbal Space Program", "KSP.log")
    if os.path.exists(log):
        m = re.search(r"Kerbal Space Program - (\d+\.\d+\.\d+)\.\d+", read(log))
        if m and tuple(int(x) for x in m.group(1).split(".")) != ver:
            note("本机 KSP.log 是 %s，.version 写的目标是 %d.%d.%d（换机器跑过就同步一下）"
                 % (m.group(1), ver[0], ver[1], ver[2]))


def check_doc_versions():
    """玩家文档里写的兼容区间，必须和 `.version` 一致。

    文档是手写的，`.version` 是游戏读的 —— 手写的那份会漂。实测漂过一次：
    `INSTALL.md` 写着「1.8.0 – 1.12.99（自检在 1.12.5 上）」，而 `.version`
    早就统一到 1.12.0–1.12.99 / 本机 1.12.4。玩家照着文档去装 1.8.0，装不上会来问；
    而这类错**编译不报、闸门不报、只有人读到才发现**。
    """
    p = os.path.join(GAMEDATA, "JetForge.version")
    if not os.path.exists(p):
        return
    try:
        vj = json.loads(read(p))
    except Exception:
        return

    def trip(key):
        d = vj.get(key) or {}
        try:
            return "%d.%d.%d" % (int(d.get("MAJOR", 0)), int(d.get("MINOR", 0)), int(d.get("PATCH", 0)))
        except Exception:
            return None

    lo, hi = trip("KSP_VERSION_MIN"), trip("KSP_VERSION_MAX")
    if not lo or not hi:
        return
    inst = os.path.join(ROOT, "INSTALL.md")
    if os.path.exists(inst):
        m = re.search(r"1\.[0-9]+\.[0-9]+\s*[–\-~]\s*1\.[0-9]+\.[0-9]+", read(inst))
        if not m:
            fail("INSTALL.md 里找不到 KSP 兼容区间（玩家第一个看的就是这张表）")
        elif m.group(0).replace(" ", "").replace("~", "–").replace("-", "–") != (lo + "–" + hi):
            fail("INSTALL.md 写的 KSP 区间是 %r，而 .version 是 %s – %s（两份对不上，玩家按哪份都可能错）"
                 % (m.group(0), lo, hi))
    else:
        fail("缺 INSTALL.md")

def check_doc_pairs():
    """两份语言的文档必须成对存在，且 CHANGELOG 的版本条目不许多不许少。

    这里拦的是一种"慢慢变成谎话"的错：发了 1.1.0，中文 `CHANGELOG.md` 加了条目，
    英文 `CHANGELOG.en.md` 忘了加 —— 英文玩家看到的最新版本永远停在 1.0.0，
    而没有任何东西会报错。反过来（英文多一条中文少一条）同样糟糕：
    玩家按语言看到两种"这次更新了什么"。
    """
    for base in ("INSTALL", "CHANGELOG"):
        zh = os.path.join(ROOT, base + ".md")
        en = os.path.join(ROOT, base + ".en.md")
        if os.path.exists(zh) and not os.path.exists(en):
            fail("有 %s.md 却没有 %s.en.md（英文玩家没有可看的版本）" % (base, base))
        if os.path.exists(en) and not os.path.exists(zh):
            fail("有 %s.en.md 却没有 %s.md" % (base, base))

    zh_p, en_p = os.path.join(ROOT, "CHANGELOG.md"), os.path.join(ROOT, "CHANGELOG.en.md")
    if not (os.path.exists(zh_p) and os.path.exists(en_p)):
        return

    def versions(path):
        out = []
        for line in read(path).splitlines():
            m = re.match(r"\s*##\s+([0-9]+\.[0-9]+\.[0-9]+)\b", line)
            if m:
                out.append(m.group(1))
        return out

    # 只比对带完整三段号的版本标题（"0.x" 那种区间条目不参与）
    a, b = versions(zh_p), versions(en_p)
    missing_en = [v for v in a if v not in b]
    missing_zh = [v for v in b if v not in a]
    if missing_en:
        fail("CHANGELOG.en.md 缺这些版本条目：%s（英文玩家看到的最新版本会停在旧的那条）"
             % "、".join(missing_en))
    if missing_zh:
        fail("CHANGELOG.md 缺这些版本条目：%s（中英两份对不上）" % "、".join(missing_zh))

    check_changelog_bullets(zh_p, en_p)


def changelog_bullets(path):
    """按版本段数条目：返回 {版本号: 条目数}。

    数的是每条 `- ` 而不是行数：一个条目经常折成两三行，按行数会比出假差额。
    只数到下一个 `## ` 标题为止，所以"归档"段不会混进来。
    """
    out = {}
    cur = None
    for line in read(path).splitlines():
        m = re.match(r"\s*##\s+([0-9]+\.[0-9]+\.[0-9]+)\b", line)
        if m:
            cur = m.group(1)
            out[cur] = 0
            continue
        if re.match(r"\s*##\s", line):
            cur = None      # 归档等非版本段
            continue
        if cur and re.match(r"\s*[-*]\s+\S", line):
            out[cur] += 1
    return out


def check_changelog_bullets(zh_p, en_p):
    """同一个版本，中英两份的条目数必须一样。

    版本标题齐只是"英文没停在旧版本"；条目数齐才是"这一版里每条改动都有人翻"。
    少一条的表现是：英文玩家以为这次只修了一个 bug。数量能不匹配的合法情形
    只有一个——某条改动对中文玩家可见、对英文玩家不可见，那它本来就不该写成两条。
    """
    zh, en = changelog_bullets(zh_p), changelog_bullets(en_p)
    for ver in sorted(set(zh) & set(en)):
        if zh[ver] != en[ver]:
            fail("CHANGELOG %s：中文 %d 条、英文 %d 条（同一版本条目数必须相等，"
                 "差的那条英文玩家看不到）" % (ver, zh[ver], en[ver]))


def check_sources():
    for dp, dn, fn in os.walk(SOURCE):
        for f in fn:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(dp, f)
            short = p.replace(ROOT + os.sep, "")
            for i, line in enumerate(read(p).splitlines(), 1):
                if 'GetEnvironmentVariable("JF_' in line:
                    fail("%s:%d 调试探针接线还在（JF_PROBE）" % (short, i))
                if "Console.WriteLine(" in line:
                    fail("%s:%d 还有 Console.WriteLine（游戏里没地方看，发布前该删）" % (short, i))


# 包里该有什么：GameData/JetForge 全量（剪掉开发垃圾）+ 根目录四份文档 + 插件 DLL。
# DLL 是特例：它只在 make_release.sh 第 4 步被拷进包，磁盘上没有 Plugins 目录，
# 所以不能拿"盘上有"当标准——否则每次都会报"包里多出一个 DLL"。
DOCS = ["README.md", "INSTALL.md", "INSTALL.en.md", "LICENSE", "CHANGELOG.md", "CHANGELOG.en.md"]
PLUGIN = "GameData/JetForge/Plugins/JetForge.dll"
JUNK_EXT = (".log", ".ppm", ".pyc", ".cs", ".pdb", ".bak", ".meta")
JUNK_DIRS = ("__pycache__", "UserDesigns", "obj", "bin")


def manifest_on_disk():
    want = set(DOCS)
    for dp, dn, fn in os.walk(GAMEDATA):
        dn[:] = [d for d in dn if d not in JUNK_DIRS]
        for f in fn:
            if f.lower().endswith(JUNK_EXT):
                continue
            want.add(os.path.relpath(os.path.join(dp, f), ROOT).replace(os.sep, "/"))
    want.add(PLUGIN)
    return want


def cfg_refs():
    """零件 cfg 里点名要的模型与贴图。

    这几项少一个，游戏里就是白模或丢贴图，而日志只有一行 warning——
    玩家看到的是"这 mod 的东西是白的"，不会去翻日志。
    """
    out = []
    parts = os.path.join(GAMEDATA, "Parts")
    if not os.path.isdir(parts):
        return out
    for name in sorted(os.listdir(parts)):
        if not name.endswith(".cfg"):
            continue
        text = read(os.path.join(parts, name))
        for m in re.finditer(r'model\s*=\s*(\S+)', text):
            rel = m.group(1).replace("\\", "/")
            if rel.startswith("Squad/") or not rel.startswith("JetForge/"):
                continue
            out.append((name, "模型", "GameData/JetForge/" + rel))
        for m in re.finditer(r'texture\s*=\s*(\S+)', text):
            rel = m.group(1).replace("\\", "/")
            out.append((name, "贴图", "GameData/JetForge/" + rel))
    return out


def check_zip():
    if not os.path.isdir(RELEASE):
        note("还没有 _release 目录（没出过包）")
        return
    zips = sorted(p for p in os.listdir(RELEASE) if p.endswith(".zip"))
    if not zips:
        note("_release 里没有 zip")
        return
    latest = os.path.join(RELEASE, zips[-1])
    with zipfile.ZipFile(latest) as z:
        names = set(n.replace("\\", "/") for n in z.namelist() if not n.endswith("/"))

    # ① 集合必须与磁盘一致。少一个 = 玩家那边少东西；多一个 = 来路不明。
    #    旧版只抽查五个"该有的文件在不在"，于是删掉一整套贴图也能过。
    want = manifest_on_disk()
    missing = sorted(want - names)
    extra = sorted(names - want)
    for n in missing[:10]:
        fail("%s 里缺 %s（磁盘上有）" % (zips[-1], n))
    for n in extra[:10]:
        fail("%s 里多出 %s（磁盘上没有）" % (zips[-1], n))
    if len(missing) > 10 or len(extra) > 10:
        note("%s 集合比对还有 %d 缺 / %d 多未逐条列出"
             % (zips[-1], max(0, len(missing) - 10), max(0, len(extra) - 10)))

    # ② 开发垃圾一个都不许进包
    for n in sorted(names):
        bad_dir = any(("/" + d + "/") in ("/" + n) for d in JUNK_DIRS)
        if n.lower().endswith(JUNK_EXT) or bad_dir or "__pycache__" in n:
            fail("%s 里不该有 %s" % (zips[-1], n))

    # ③ 零件 cfg 点名的模型与贴图，必须在包里真的存在——磁盘上有不算数
    for part, kind, stem in cfg_refs():
        cands = [stem + e for e in (".mu", ".cfg", ".png", ".dds", ".tga")]
        if not any(c in names for c in cands):
            fail("%s 的%s %s 不在包里" % (part, kind, stem))

    pngs = [n for n in names if n.lower().endswith(".png")]
    if len(pngs) < 14:
        fail("%s 里贴图只有 %d 张（七套各带 _N 至少 14 张）" % (zips[-1], len(pngs)))
    note("%s：%d 个文件（盘上应有 %d 个），%d 张图"
         % (zips[-1], len(names), len(want), len(pngs)))
    check_note_sha(latest)


def check_note_sha(zip_path):
    """发布说明里抄的那个 sha256 必须等于这一版 zip 的真哈希。

    为什么单列一条：那个哈希是**手抄**进 `_release/GITHUB_RELEASE-<ver>.md` 的，
    每重出一版包就旧一次，而没有任何东西会因此变红——玩家照着说明校验会得到"不匹配"，
    于是怀疑包被动过手脚。写时准确、之后每轮都在变旧，这种文档只能靠闸门管。
    md 不存在就是红：以前这里写的是"不存在就不查（不是每个人都写发布说明）"，
    结果升 1.0.1 那次 `make_release.sh` 只**同步**已存在的说明、不会**新建**它，
    于是闸门在没有说明可查的情况下照样变绿——一条"没东西可查就放过"的判据，
    在真正需要它的那一版上恰好什么也没拦。
    说明一版写一次，缺了就是缺了。
    """
    ver = os.path.basename(zip_path)[len("JetForge-"):-len(".zip")]
    md = os.path.join(RELEASE, "GITHUB_RELEASE-%s.md" % ver)
    if not os.path.exists(md):
        fail("出了 %s 的包，却没有 _release/GITHUB_RELEASE-%s.md："
             "发布说明不会自己长出来，手抄的 sha256 也就没人核"
             % (os.path.basename(zip_path), ver))
        return
    with open(md, "r", encoding="utf-8-sig", errors="replace") as f:
        text = f.read()
    m = re.search(r"(?m)^sha256\s+([0-9a-fA-F]{64})\s*$", text)
    if not m:
        fail("发布说明里没有 sha256 那一行（%s）" % os.path.basename(md))
        return
    h = hashlib.sha256()
    with open(zip_path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    if m.group(1).lower() != h.hexdigest():
        fail("发布说明里的 sha256 不是这一版的（说明里 %s…，包里 %s…）——重出包之后说明要跟着更新"
             % (m.group(1)[:12], h.hexdigest()[:12]))


def main():
    ver = check_version()
    check_parts()
    check_engine_limits()
    check_ksp_version()
    check_doc_versions()
    check_doc_pairs()
    check_sources()
    if "--zip" in sys.argv:
        check_zip()
    for n in NOTES:
        print("注意 %s" % n)
    if FAILS:
        print("FAIL 发布闸门：%d 条" % len(FAILS))
        for f in FAILS:
            print("  - " + f)
        return 1
    print("OK   发布闸门：版本 %s｜零件引用齐｜马赫门限及格｜无调试残留" % (ver or "?"))
    return 0


if __name__ == "__main__":
    sys.exit(main())