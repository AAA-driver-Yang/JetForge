#!/usr/bin/env bash
# 一整套自动化测试：构建 → 回归 → 实机自检 → 出包 → 发布闸门。
#
# 为什么要有它：这四种东西以前是我一条一条手敲的，敲漏一条就会出现
# "回归绿了但游戏里炸"或者"游戏里好了但打包里还是旧 dll"。
# 现在一条命令跑完，最后只印一行结论——绿的还是红的。
#
# 用法：
#   tools/run_tests.sh              # 全套（会启动游戏，约 1.5 分钟）
#   tools/run_tests.sh --no-game    # 只跑构建与回归（不启动游戏，约 20 秒）
#   tools/run_tests.sh --ci         # 没有 KSP 的机器（GitHub Actions、贡献者）：只跑闸门与回归
#   KSP_DIR="D:/别的路径" tools/run_tests.sh
set -uo pipefail

cd "$(dirname "$0")/.."
REPO="$(pwd)"
GAME_ARG="${1:-}"
# --ci：这台机器上没有 KSP（CI 跑在 GitHub 的 Windows runner 上，没装游戏）。
# 编 mod 要游戏自带的 Assembly-CSharp/UnityEngine，出包与自检要真的游戏目录，
# 这三步在这里**跳过的理由写在报告里**，不是静默少跑——静默少跑比不跑更坏。
CI_MODE=0
if [ "$GAME_ARG" = "--ci" ]; then CI_MODE=1; fi

# 作者本机的缺省值；别人的机器上用 KSP_DIR 环境变量或仓库根的 local.props 覆盖
KSP_DIR="${KSP_DIR:-D:/KSP FEAT/Kerbal Space Program}"
SELFTEST="$KSP_DIR/GameData/JetForge/selftest.log"

FAILS=0
step() { echo; echo "== $* =="; }
ok()   { echo "   OK   $*"; }
bad()  { echo "   FAIL $*"; FAILS=$((FAILS + 1)); }

# ---------- 1. 构建 ----------
step "1/6 构建（mod + 测试）"
if [ "$CI_MODE" = "1" ]; then
  ok "跳过 mod 编译（--ci：这台机器没有 KSP 的游戏程序集，编不出来也不该判红）"
  BUILD_OUT=""
fi
if [ "$CI_MODE" != "1" ]; then
BUILD_OUT=$(dotnet build KSP/JetForge.csproj -c Release -v q --nologo 2>&1)
if echo "$BUILD_OUT" | grep -qE "error CS"; then
  echo "$BUILD_OUT" | grep -E "error CS" | head -5
  bad "mod 编译失败"
else
  WARN_N=$(echo "$BUILD_OUT" | grep -cE "warning CS" || true)
  if [ "$WARN_N" != "0" ]; then
    echo "$BUILD_OUT" | grep -E "warning CS" | head -5
    bad "mod 有 $WARN_N 条警告（基线是 0）"
  else
    ok "mod 0 错 0 警告"
  fi
fi
fi

TEST_OUT=$(dotnet build JetForge.Test/JetForge.Test.csproj -c Release -v q --nologo 2>&1)
if echo "$TEST_OUT" | grep -qE "error CS"; then
  echo "$TEST_OUT" | grep -E "error CS" | head -5
  bad "测试工程编译失败"
else
  ok "测试工程 0 错"
fi

if [ "$FAILS" != "0" ]; then
  echo; echo "!! 编译都没过，后面不用跑了"; exit 1
fi

# ---------- 2. 回归 ----------
step "2/6 回归与两道闸门"
PANEL_OUT=$(python tools/check_panels.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$PANEL_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$PANEL_OUT" | head -8
  bad "面板读数太密（一行超过 5 个数）"
fi
# 空 catch：物理与状态路径上不许有「吞掉错误」的地方（§167 那次 FormatException 的教训）
CATCH_OUT=$(python tools/check_catches.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$CATCH_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$CATCH_OUT" | head -8
  bad "有静默吞错误的空 catch"
fi
# 本地化：中文字面量只许减不许增（双语迁移分十几轮做，边翻边加就永远做不完）
LOC_OUT=$(python tools/check_loc.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$LOC_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$LOC_OUT" | head -10
  bad "中文字面量比基线多了"
fi
# 本地化键：源码里的 #autoLOC_ 键必须在两份词条里都存在（特性实参写不了 Loc.T，
# 键打错编译照样过、游戏里原样显示 #autoLOC_…，只有这道门能拦）
KEY_OUT=$(python tools/check_loc_keys.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$KEY_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$KEY_OUT" | head -10
  bad "有 #autoLOC 键在两份词条里对不上"
fi
# 反方向：两份词条里写下的键，得有人在源码或随包 cfg 里引用。没人引用的词条
# 不会上屏、改它不会有任何反馈，于是只会越来越旧，最后变成一份假图纸
UNUSED_OUT=$(python tools/check_loc_unused.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$UNUSED_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$UNUSED_OUT" | head -10
  bad "有本地化词条写了没人用（接上、删掉，或写进 tools/loc_unused_allow.txt）"
fi
# 占位符对账：成对字面量两边的 {n} 必须一致。少了 → 玩家看到半句话；
# 多了 → string.Format 抛 FormatException，被 catch 吞掉就是一行空白（§203）
FMT_OUT=$(python tools/check_loc_fmt.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$FMT_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$FMT_OUT" | head -10
  bad "成对字面量的 {n} 对不上"
fi
# 半翻检查：「已翻的半句 + 裸中文的半句」拼在一起，编译过、中文界面正常，
# 只有英文玩家会看到半中半英（Loc.T 成对保证拦不住拼接）。只许降不许升。
MIX_OUT=$(python tools/check_mixed.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$MIX_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$MIX_OUT" | head -10
  bad "有句子成了半中半英"
fi
# 配对检查：Loc.T(中文, English) 两边写反或两边一样长句（中文槽被英文占）。
# 这类错编译过、中文界面直接变英文，靠"某断言里含某个中文词"碰运气抓。
PAIR_OUT=$(python tools/check_loc_pairs.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$PAIR_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$PAIR_OUT" | head -10
  bad "本地化配对有中文槽被英文占的句子"
fi
# 英文句子拼接：`Loc.T("…。", "English.") + Loc.T("…", "more")` 这种两段拼接，
# 中文那边永远看着正常，英文会在边界处挤成 `limit.Where` / `m,above`。
JOIN_OUT=$(python tools/check_loc_join.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$JOIN_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$JOIN_OUT" | head -10
  bad "英文拼接处缺标点或空格（中文看不出来，只有英文会挤成一句）"
fi
# 只写不读的字段：不参与计算的常量/开关编译过、回归也过，它只是躺在那里，
# 但会让后来的人以为"这道闸存在"（§235：T4UncooledLimit 写着 1250 K 而没人读）。
DEAD_OUT=$(python tools/check_dead_fields.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$DEAD_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$DEAD_OUT" | head -10
  bad "有只写不读的字段（要么用起来，要么删掉）"
fi
# 只声明不调用的方法：字段那道闸管不到方法。§260 那四个部件的 Validate 就是
# "写好了、编译过、回归过、从来没人调用"，直到有人把级数写成负数才发现它不在链上。
DEADM_OUT=$(python tools/check_dead_methods.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$DEADM_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$DEADM_OUT" | head -10
  bad "有只声明不调用的方法（要么接上，要么删掉；反射调用的进 tools/dead_methods_allow.txt）"
fi
# 按钮文案长度：按钮不会折行，英文一长就挤掉兄弟按钮或被窗口边裁掉。
# 中文能省就省不是问题（一个字才两格宽），英文 32 字符以上基本就会出画面。
BTN_OUT=$(python tools/check_loc_btn.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$BTN_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$BTN_OUT" | head -10
  bad "有按钮文案英文太长，会挤爆窗口"
fi
# 固定宽度控件里的英文：中文一个字 13 px、英文一个字母 6.6 px，
# `Loc.T("高度剖面","Altitude profile")` 配 Width(80f) 时中文放得下、英文放不下——
# 排版按中文算的，只有英文玩家被裁（§243）。
FIT_OUT=$(python tools/check_label_fit.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$FIT_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$FIT_OUT" | head -10
  bad "有英文文案在固定宽度控件里放不下（会显示不全）"
fi
# 悬停提示的长度预算：盒子是自绘的（320 px 宽、自动折行、高度按行数算），横向不会裁字，
# 但长到七行以上就会盖住它要解释的那个控件。最长的那批文案几乎都是提示（§251）。
HINT_OUT=$(python tools/check_hint_fit.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$HINT_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$HINT_OUT" | head -10
  bad "有英文成对文案长得像说明书（悬停提示会盖住控件）"
fi
# 每个数值输入都得有说明：滑杆上的数字没人能凭标签猜出来。悬停提示和滑杆右边那句注
# 都算，两样都没有就是让玩家拿默认值当唯一选择（§257）。
COVER_OUT=$(python tools/check_hint_cover.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$COVER_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$COVER_OUT" | head -14
  bad "有可编辑字段没写说明（悬停没有、滑杆右边也没有）"
fi
# 随包文件里的开发笔记：`GameData/JetForge/**` 会被原样打包发出去，里面的 cfg 注释
# 与随包 README 都是"货架上的东西"。开发期的"暂时用占位模型""下一步再生成外形"
# 会跟着 zip 走，过几个月连自己都分不清哪句是现状（§242）。
PACK_OUT=$(python tools/check_pack_notes.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$PACK_OUT" | head -1 | sed 's/^OK   //')"
else
  echo "$PACK_OUT" | head -10
  bad "随包文件里还留着开发笔记（改写成现状，或进 tools/pack_notes_allow.txt 说明理由）"
fi
REG_OUT=$(dotnet run --project JetForge.Test/JetForge.Test.csproj -c Release --no-build 2>&1)
REG_TAIL=$(echo "$REG_OUT" | tail -1)
SECTIONS=$(echo "$REG_OUT" | grep -c "^== 第" || true)
ASSERTS=$(echo "$REG_OUT" | grep -c "OK$" || true)
if [ "$REG_TAIL" = "全部回归通过。" ]; then
  ok "回归通过（$SECTIONS 个段头、$ASSERTS 条断言）"
else
  echo "$REG_OUT" | grep -E "超差" | head -10
  bad "回归没通过：$REG_TAIL"
fi

# ---------- 3. 实机自检 ----------
if [ "$GAME_ARG" = "--no-game" ] || [ "$CI_MODE" = "1" ]; then
  step "3/6 实机自检（$GAME_ARG，跳过）：这一步要真启动游戏，CI 上没有"
else
  step "3/6 实机自检（真启动一次游戏，跑一遍 JetForge.SelfTest）"
  if [ ! -d "$KSP_DIR" ]; then
    bad "找不到游戏目录：$KSP_DIR（用 KSP_DIR 环境变量指过去）"
  elif tasklist 2>/dev/null | grep -q "KSP_x64"; then
    # 单实例锁：上一个实例没退干净时，新进程会安静地直接退出，
    # 表现是"进程在、日志二十分钟不出现"。所以这里不猜，直接拒绝跑。
    bad "已经有一个 KSP_x64 在跑。先把它关掉（按精确 PID），再重跑这个脚本"
  else
    if [ -f "$SELFTEST" ]; then mv "$SELFTEST" "$SELFTEST.prev"; fi
    ( cd "$KSP_DIR" && nohup ./KSP_x64.exe -single-instance > /dev/null 2>&1 & )
    sleep 3
    PID=$(tasklist 2>/dev/null | grep "KSP_x64" | awk '{print $2}' | head -1)
    echo "   KSP PID=$PID，轮询日志…"
    LINES=0; WAITED=0
    # 注意：日志开头第 4 行就写着「末行==== 自检结束 ==== 说明跑完了」这句话本身。
    # 用 `grep -q "自检结束"` 会当场命中那句说明，然后在自检才跑一半时就宣布"跑完了"、
    # 把游戏杀掉——第一版就是这么错过的。判据必须是"某一行**以**这个标记结尾"。
    while [ "$WAITED" -lt 240 ]; do
      if [ -f "$SELFTEST" ]; then
        LINES=$(wc -l < "$SELFTEST" | tr -d ' ')
        if grep -qE "==== 自检结束 ====[[:space:]]*$" "$SELFTEST"; then break; fi
      fi
      sleep 5; WAITED=$((WAITED + 5))
    done
    if grep -qE "==== 自检结束 ====[[:space:]]*$" "$SELFTEST" 2>/dev/null; then
      ok "自检跑完（$LINES 行，等了 $WAITED 秒）"
      grep -E "算错共|判据跑了|Exception" "$SELFTEST" | sed 's/^/   /' | head -4
      if grep -q "算错共 [1-9]" "$SELFTEST"; then bad "自检里状态审计有算错项"; else ok "状态审计 0 条算错"; fi
      if grep -qE "Exception" "$SELFTEST"; then bad "自检日志里有异常"; else ok "自检日志里没有异常"; fi
    else
      bad "等了 $WAITED 秒自检还没结束（日志 $LINES 行）"
    fi
    if [ -n "${PID:-}" ]; then taskkill //PID "$PID" //F > /dev/null 2>&1 || true; fi
    sleep 2
    LEFT=$(tasklist 2>/dev/null | grep -cE "KSP_x64" || true)
    if [ "$LEFT" = "0" ]; then ok "游戏进程已退出"; else bad "还有 $LEFT 个 KSP_x64 没退"; fi
  fi
fi

# ---------- 4. 出包检查 ----------
step "4/6 出包（三处版本一致 + 包里没有垃圾）"
if [ "$CI_MODE" = "1" ]; then
  ok "跳过出包（--ci：出包要读 mod 的 Release 产物，这里没有）"
elif bash tools/make_release.sh > /tmp/jf_release.log 2>&1; then
  ok "$(grep -c "" /tmp/jf_release.log) 行日志，末行：$(tail -1 /tmp/jf_release.log)"
else
  tail -5 /tmp/jf_release.log | sed 's/^/   /'
  bad "出包失败（看上面几行）"
fi

# ---------- 5. 发布闸门 ----------
step "5/6 发布闸门（版本 / 贴图引用 / 调试残留 / 包内容 / 发布就绪）"
if [ "$CI_MODE" = "1" ]; then
  ok "跳过发布闸门（--ci：它查的是刚打出来的 zip）"
else
REL_OUT=$(python tools/check_release.py --zip 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$REL_OUT" | grep '^OK' | sed 's/^OK   //')"
  echo "$REL_OUT" | grep '^注意' | sed 's/^/   /' | head -3
else
  echo "$REL_OUT" | head -10
  bad "发布闸门没过"
fi
# 发布就绪（A 段）：许可/README/更新日志/元数据口径。--public 的上线项不在这里查，
# 那些（真地址、stable、截图）开发期必然不满足，归 RELEASE.md 的 B 段人工清单。
# 包与部署：自检跑的是部署目录，玩家拿的是 zip——两份必须逐字节一致，
# 否则"自检通过"证明的不是玩家手里那份。运行时产物（日志、缩略图、用户设计）放过。
PD_OUT=$(python tools/check_pack_vs_deploy.py --deploy "$KSP_DIR/GameData/JetForge" 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$PD_OUT" | grep '^OK' | sed 's/^OK   //')"
else
  echo "$PD_OUT" | head -10
  bad "包与部署不一致（自检测的那份不是玩家拿到的那份）"
fi
RDY_OUT=$(python tools/check_readiness.py 2>&1)
if [ $? -eq 0 ]; then
  ok "$(echo "$RDY_OUT" | grep '^OK' | sed 's/^OK   //')"
else
  echo "$RDY_OUT" | head -10
  bad "发布就绪（A 段）没过"
fi
fi

# ---------- 结论 ----------
echo
if [ "$FAILS" = "0" ]; then
  if [ "$CI_MODE" = "1" ]; then
    echo "CI 通过：闸门与回归全绿（实机自检 / 出包 / 发布闸门按 --ci 跳过）。"
  else
    echo "6/6 全部通过。"
  fi
  exit 0
else
  echo "$FAILS 项没通过。"
  exit 1
fi