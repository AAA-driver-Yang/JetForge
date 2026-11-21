#!/usr/bin/env bash
# 打发布包：_release/JetForge-<version>/GameData/JetForge  +  同名 zip
#
# 为什么要有这个脚本而不是手点：发布包最容易出三种错，
# ①版本三处不一致（Core/Version.cs、AssemblyInfo.cs、JetForge.version）→ CKAN 拒装；
# ②把开发垃圾打进包（日志、ppm 截图、玩家设计、__pycache__）；
# ③忘了先构建，包里还是上一版的 dll。
# 这三件事脚本全给拦住。
set -euo pipefail

cd "$(dirname "$0")/.."
REPO="$(pwd)"

# ---------- 1. 取版本号（唯一来源：Core/Version.cs） ----------
VERSION=$(grep -oE 'Number[[:space:]]*=[[:space:]]*"[0-9]+\.[0-9]+\.[0-9]+"' KSP/Source/Core/Version.cs | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1)
if [ -z "$VERSION" ]; then
  echo "!! Core/Version.cs 里找不到 Number = \"x.y.z\""
  exit 1
fi
echo "== JetForge $VERSION"

# ---------- 2. 三处版本必须一致 ----------
ASM=$(grep -oE 'AssemblyVersion\("[0-9]+\.[0-9]+\.[0-9]+' KSP/Source/Properties/AssemblyInfo.cs | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1)
if [ "$ASM" != "$VERSION" ]; then
  echo "!! AssemblyInfo.cs 写的是 $ASM，Version.cs 是 $VERSION"
  exit 1
fi

VFILE="GameData/JetForge/JetForge.version"
PY=$(command -v python || command -v python3 || true)
if [ -z "$PY" ]; then echo "!! 需要 python 来读 JetForge.version"; exit 1; fi
VFILE_VERSION=$("$PY" - "$VFILE" <<'EOF'
import json, sys
v = json.load(open(sys.argv[1], encoding='utf-8'))['VERSION']
print("%d.%d.%d" % (v['MAJOR'], v['MINOR'], v['PATCH']))
EOF
)
if [ "$VFILE_VERSION" != "$VERSION" ]; then
  echo "!! JetForge.version 写的是 $VFILE_VERSION，Version.cs 是 $VERSION"
  exit 1
fi
echo "== 三处版本一致"

# ---------- 3. 构建（不许拿旧 dll 打包） ----------
dotnet build KSP/JetForge.csproj -c Release -v q --nologo | tail -3

# ---------- 4. 组装 ----------
OUT="_release/JetForge-$VERSION"
rm -rf "$OUT"
mkdir -p "$OUT/GameData/JetForge/Plugins"
cp -r GameData/JetForge/. "$OUT/GameData/JetForge/"
cp KSP/bin/Release/JetForge.dll "$OUT/GameData/JetForge/Plugins/"
cp README.md INSTALL.md INSTALL.en.md LICENSE CHANGELOG.md CHANGELOG.en.md "$OUT/"

# 开发垃圾：日志、截图、玩家设计、Python 缓存，一律不许进包
find "$OUT" -type f \( -name "*.log" -o -name "*.ppm" -o -name "*.pyc" \) -delete
rm -rf "$OUT/GameData/JetForge/UserDesigns" "$OUT/GameData/JetForge/__pycache__"

# ---------- 5. 打包 ----------
# 不用 zip 命令：Windows 上常常没有。python 的 zipfile 到处都有，还能顺手设时间戳。
ZIP="$REPO/_release/JetForge-$VERSION.zip"
"$PY" - "$OUT" "$ZIP" <<'EOF'
import os, sys, zipfile
src, dst = sys.argv[1], sys.argv[2]
if os.path.exists(dst):
    os.remove(dst)
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, dirs, files in os.walk(src):
        for f in files:
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, src))
print("zip ok:", dst)
EOF

# ---------- 6. CKAN 元数据 ----------
# 包旁边放一份 .netkan：sha256 与文件名由脚本填，只剩 download 地址要人填。
# 之所以在这里生成而不是手写一份固定文件：手写的那份一定会忘记改版本号，
# 而忘记改版本号的元数据比没有元数据更糟——CKAN 会按它去找一个不存在的包。
"$PY" - "$ZIP" "$REPO/_release/JetForge-$VERSION.netkan" "$VERSION" <<'EOF'
import hashlib, json, os, sys
zip_path, out, ver = sys.argv[1], sys.argv[2], sys.argv[3]
# JETFORGE_NETKAN_KREF="你的用户名/JetForge" 走 GitHub Release 路线（见文件末尾注释）
kref = os.environ.get("JETFORGE_NETKAN_KREF", "").strip().strip("/")
homepage = os.environ.get("JETFORGE_HOMEPAGE",
                          ("https://github.com/" + kref) if kref else "https://github.com/AAA-driver-Yang/JetForge")
h = hashlib.sha256()
with open(zip_path, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""):
        h.update(chunk)
meta = {
    "spec_version": "v1.4",
    "identifier": "JetForge",
    "name": "JetForge",
    # abstract/description 是英文：CKAN 的索引与客户端是英文世界的东西，
    # 中文摘要会让英语玩家在列表里看到一串方块，而且 CKAN 的搜索不做中文分词。
    "abstract": "Parametric jet engines: from design parameters to gas path, shape, cutaway and flight behaviour",
    "description": ("Design an engine by its geometry: fan and compressor stages, burner, turbines and nozzle, "
                    "plus a separate intake part with its own cross-section profile. The mod solves the gas path "
                    "from those parameters - thrust, specific fuel consumption, spool speeds, surge and choke "
                    "margins - instead of asking you for a performance curve. Includes a design wizard, a test "
                    "bench, a cutaway view and a bilingual UI (English and Chinese)."),
    "author": "JetForge contributors",
    "license": "MIT",
    "homepage": homepage,
    # 兼容区间只写真跑过的：本模组在 1.12.4 上每次发布都真机自检一遍，
    # 更早的版本（1.8~1.11）从没装过——写 1.8.0 等于许诺一个没人验过的区间，
    # 玩家装上会撞 MissingMethodException，然后来问"为什么 KCAN 装完是坏的"。
    "ksp_version_min": "1.12.0",
    "ksp_version_max": "1.12.99",
    # release_status 只有"包真的挂在一个能下载的地方"才配写 stable。
    # 本地出包时 zip 还躺在 _release/ 里，download 是占位域名——
    # 这种元数据如果标 stable，CKAN 会把它当真索引给玩家装，然后 404。
    # 所以缺省 development；等真的传到 SpaceDock 再把地址与状态一起改。
    "release_status": os.environ.get("JETFORGE_RELEASE_STATUS", "development"),
    # 依赖必须写在这里，不能只写在 README 里：CKAN 装的时候只读这份元数据，
    # 缺了它玩家会装到一个没有羽流、没有声音、cfg 补丁还没生效的 JetForge，
    # 然后在论坛上说"这 mod 的发动机是白板"。
    # ModuleManager 是硬依赖——Waterfall.cfg / RSE.cfg 都是 @PART 补丁，没它一句都不生效。
    "depends": [
        {"name": "ModuleManager"},
        {"name": "Waterfall"},
        {"name": "RocketSoundEnhancement"}
    ],
    "install": [{"find": "GameData/JetForge", "install_to": "GameData"}],
}
# 两种来路，选一种：
#   ① 自己传包（SpaceDock / 直链）：要写 version + download + 大小 + 哈希，
#      因为 CKAN 不会去猜你把文件放哪了；
#   ② 包挂在 GitHub Release 上：写一行 "$kref"，版本号、地址、大小、哈希全由
#      CKAN 的机器人从 Release 里取——手写的版本号迟早会和 Release 里的对不上，
#      对不上的元数据比没有元数据更糟（它会去找一个不存在的包）。
if kref:
    meta["$kref"] = "#/ckan/github/" + kref
else:
    meta["version"] = ver
    meta["download"] = os.environ.get("JETFORGE_DOWNLOAD", "https://example.invalid/JetForge-%s.zip" % ver)
    meta["download_size"] = os.path.getsize(zip_path)
    meta["download_hash"] = {"sha256": h.hexdigest()}
with open(out, "w", encoding="utf-8") as f:
    json.dump(meta, f, ensure_ascii=False, indent=2)
print("netkan ok:", out)
EOF
# 发布说明里那个 sha256 是**手抄**的，每重出一版包就旧一次，而它旧了不会让任何东西变红，
# 只有玩家照着校验时发现对不上、然后开始怀疑包被动过手脚。这里先把它改成这一版的。
# md 不存在就不动（不是每个版本都写发布说明）；`check_release.py` 会比对这两处。
"$PY" - "$ZIP" "$REPO/_release/GITHUB_RELEASE-$VERSION.md" <<'EOF'
import hashlib, io, os, re, sys
zip_path, md = sys.argv[1], sys.argv[2]
if os.path.exists(md):
    h = hashlib.sha256()
    with open(zip_path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    t = io.open(md, encoding="utf-8-sig", newline="").read()
    pat = r"(?m)^(sha256\s+)[0-9a-fA-F]{64}\s*$"
    new, n = re.subn(pat, lambda m: m.group(1) + h.hexdigest(), t)
    if n:
        io.open(md, "w", encoding="utf-8-sig", newline="").write(new)
        print("release-note sha256 synced:", os.path.basename(md))
EOF
printf "\n== 完成\n   目录 %s（%s）\n   包   %s（%s）\n   元数据 %s\n   文件数 %s\n" \
  "$OUT" "$(du -sh "$OUT" | cut -f1)" "$ZIP" "$(du -h "$ZIP" | cut -f1)" \
  "$REPO/_release/JetForge-$VERSION.netkan" "$(find "$OUT" -type f | wc -l)"