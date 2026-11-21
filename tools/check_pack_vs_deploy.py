"""包里那份和我实测的那份是不是同一份。

背景：自检跑的是部署目录（`<KSP>/GameData/JetForge`），玩家拿到的是 zip 里的
`GameData/JetForge`。两者能分叉的场合不少——旧 DLL 没删、构建没拷过去、
出包脚本漏了一条文件、或者反过来部署目录里混进了几个只在本地存在的文件。
**闸门管得住"包里有没有垃圾"，管不住"包和实测的那份对不对得上"。**

用法：
    python tools/check_pack_vs_deploy.py                    # 自动找 _release/*.zip 与部署目录
    python tools/check_pack_vs_deploy.py --deploy <目录>     # 指定部署目录
    python tools/check_pack_vs_deploy.py --verbose          # 连"放过的运行时产物"一起列

判定：
  1. 包里与部署目录的相对路径集合必须相等，除了 RUNTIME 白名单里的运行时产物；
  2. 同名文件逐字节必须相等；
  3. 部署目录里有、包里没有的"非运行时"文件 → FAIL（那是玩家拿不到、我却在测的东西）；
  4. 包里有、部署目录里没有的 → FAIL（说明部署没跟上，自检测的不是这份）。
"""
import argparse
import glob
import hashlib
import io
import os
import sys
import zipfile

sys.stdout.reconfigure(encoding="utf-8")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELEASE = os.path.join(ROOT, "_release")

# 运行时产物：游戏或自检自己写出来的，本来就不该进包。
RUNTIME_DIRS = ("UserDesigns", "Parts/@thumbs")
RUNTIME_EXTS = (".ppm",)
# 日志不只叫 `x.log`：自检会写 `selftest.log.prev`、飞行日志会写 `flight.prev.log`。
# 按"名字里含 .log"判，比按扩展名判可靠——这些名字是运行时自己拼的，随时可能变。
RUNTIME_LOG_MARK = ".log"
RUNTIME_NAMES = ("ModuleManager.ConfigCache",)


def is_runtime(rel):
    rel = rel.replace("\\", "/")
    if rel.split("/")[0] in RUNTIME_DIRS or rel.startswith(tuple(d + "/" for d in RUNTIME_DIRS)):
        return True
    if RUNTIME_LOG_MARK in rel:
        return True
    if rel.endswith(RUNTIME_EXTS):
        return True
    return os.path.basename(rel) in RUNTIME_NAMES


def sha(data):
    return hashlib.sha1(data).hexdigest()[:12]


def pack_files(zip_path, prefix):
    out = {}
    with zipfile.ZipFile(zip_path) as z:
        for info in z.infolist():
            if info.is_dir():
                continue
            rel = info.filename.replace("\\", "/")
            if not rel.startswith(prefix):
                continue
            key = rel[len(prefix):]
            out[key] = z.read(info)
    return out


def deploy_files(base):
    out = {}
    for cur, dirs, files in os.walk(base):
        for f in files:
            full = os.path.join(cur, f)
            rel = os.path.relpath(full, base).replace("\\", "/")
            with open(full, "rb") as h:
                out[rel] = h.read()
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--deploy", default=os.environ.get("JETFORGE_DEPLOY", ""))
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    zips = sorted(glob.glob(os.path.join(RELEASE, "*.zip")))
    if not zips:
        print("跳过：还没有出包（_release/*.zip）")
        return 0
    zip_path = zips[-1]

    deploy = args.deploy or os.path.join(os.environ.get("KSPDIR", ""), "GameData", "JetForge")
    if not deploy or not os.path.isdir(deploy):
        print("跳过：找不到部署目录（用 --deploy 或环境变量 JETFORGE_DEPLOY 指定）")
        return 0

    prefix = "GameData/JetForge/"
    pack = pack_files(zip_path, prefix)
    live = deploy_files(deploy)

    bad = []

    only_live = sorted(set(live) - set(pack))
    only_pack = sorted(set(pack) - set(live))
    stray = [r for r in only_live if not is_runtime(r)]
    missing = [r for r in only_pack if not is_runtime(r)]

    relaxed = [r for r in only_live if is_runtime(r)]
    if args.verbose and relaxed:
        for r in relaxed:
            print("   放过（运行时产物）：%s" % r)

    if stray:
        for r in stray:
            print("FAIL 部署目录里有、包里没有：%s" % r)
        bad.append("部署目录多出 %d 个非运行时文件（玩家拿不到，我却在测）" % len(stray))
    if missing:
        for r in missing:
            print("FAIL 包里有、部署目录里没有：%s" % r)
        bad.append("包里有 %d 个文件没部署（自检测的不是这份）" % len(missing))

    differ = []
    for rel in sorted(set(pack) & set(live)):
        if sha(pack[rel]) != sha(live[rel]):
            differ.append(rel)
    for rel in differ:
        print("FAIL 内容不一致：%s（包 %s / 部署 %s）" % (rel, sha(pack[rel]), sha(live[rel])))
    if differ:
        bad.append("%d 个文件内容不一致" % len(differ))

    if bad:
        print("FAIL 包与部署：%s" % "；".join(bad))
        return 1

    print("OK   包与部署：%d 个文件逐字节一致（放过 %d 个运行时产物）  %s"
          % (len(set(pack) & set(live)), len(relaxed), os.path.basename(zip_path)))
    return 0


if __name__ == "__main__":
    sys.exit(main())