#!/usr/bin/env bash
# 打包完整源代码工程 + 全量 git 历史，落到 <仓库上级>/_backup/（可用 JETFORGE_BACKUP_ROOT 改）
set -euo pipefail

STAMP=$(date +%Y%m%d_%H%M)
ROOT="${JETFORGE_BACKUP_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}"
REPO="$ROOT/JetForge"
DEST="$ROOT/_backup"
STAGE="$DEST/JetForge_src_$STAMP"

mkdir -p "$DEST"
rm -rf "$STAGE"
mkdir -p "$STAGE"

cd "$REPO"
HEAD_SHA=$(git rev-parse --short HEAD)
HEAD_MSG=$(git log -1 --pretty=%s)
BRANCH=$(git rev-parse --abbrev-ref HEAD)
FILES=$(git ls-files | wc -l)

# 1) 全量历史（含所有分支，单文件，可 git clone 回来）
git bundle create "$STAGE/JetForge_history_$STAMP.bundle" --all >/dev/null 2>&1

# 2) 工作树快照（只含被跟踪的文件，自动排除 bin/obj/.git）
mkdir -p "$STAGE/src"
git archive --format=tar HEAD | tar -x -C "$STAGE/src"

# 3) 文档与素材（文档不在 git 仓里，只能单独拷）
mkdir -p "$STAGE/docs"
cp "$ROOT/docs/phase2-mod.md" "$STAGE/docs/"
cp "$ROOT/docs/handoff-part2.md" "$STAGE/docs/" 2>/dev/null || true
cp "$ROOT/docs/handoff-part3.md" "$STAGE/docs/" 2>/dev/null || true
cp "$ROOT/docs/phase1-design.md" "$STAGE/docs/" 2>/dev/null || true

# 4) 构建与还原说明
cat > "$STAGE/README.txt" <<EOF
JetForge 完整源代码工程包
=========================
打包时间：$(date '+%Y-%m-%d %H:%M:%S')
版本：$HEAD_SHA（分支 $BRANCH）
提交信息：$HEAD_MSG
跟踪文件数：$FILES

内容
----
src/                       源码工作树快照（= git archive HEAD，不含 bin/obj/.git）
docs/                      设计文档 phase2-mod.md 等（这些不在 git 仓里）
JetForge_history_$STAMP.bundle   全量 git 历史（git bundle，单文件）

怎么构建
--------
需要 .NET SDK 8（测试工程）与 net472 目标包（mod 工程）。

  cd src
  dotnet build KSP/JetForge.csproj -c Release          # 出 GameData/JetForge 产物
  dotnet build JetForge.Test/JetForge.Test.csproj -c Release
  dotnet run  --project JetForge.Test/JetForge.Test.csproj -c Release --no-build

构建 KSP 工程后会自动把产物拷到
  <游戏目录>/GameData/JetForge

怎么还原成完整 git 仓（含历史）
------------------------------
  git clone JetForge_history_$STAMP.bundle JetForge
  cd JetForge
  git checkout $HEAD_SHA        # 或直接用默认分支的最新提交

注：bundle 里是全部历史，clone 出来的工作树与 src/ 内容应当一致；
不一致说明打包时工作树有未提交改动（本次打包前 git status 应为空）。
EOF

# 5) 打成 zip 由 python 做（Git Bash 里 Compress-Archive 会把 /d/... 当非法路径）
echo "HEAD $HEAD_SHA"
echo "跟踪文件 $FILES"
echo "STAMP $STAMP"
echo "STAGE $STAGE"
echo "--- 目录内文件数（不含 zip）:"
find "$STAGE" -type f | wc -l