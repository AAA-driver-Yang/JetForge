#!/usr/bin/env bash
# 打发布包：_release/JetForge-<version>/GameData/JetForge  +  同名 zip
#
# 为什么要有这个脚本而不是手点：发布包最容易出三种错，
# ①版本三处不一致（Core/Version.cs、AssemblyInfo.cs、JetForge.version）→ 玩家装回家版本对不上；
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

# 发布说明里那个 sha256 是**手抄**的，每重出一版包就旧一次，而它旧了不会让任何东西变红，
# 只有玩家照着校验时发现对不上、然后开始怀疑包被动过手脚。这里先把它改成这一版的。
# md 不存在就不动（不是每个版本都写发布说明）；`check_release.py` 会比对这两处，缺文件直接红。
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

printf "\n== 完成\n   目录 %s（%s）\n   包   %s（%s）\n   文件数 %s\n" \
  "$OUT" "$(du -sh "$OUT" | cut -f1)" "$ZIP" "$(du -h "$ZIP" | cut -f1)" \
  "$(find "$OUT" -type f | wc -l)"