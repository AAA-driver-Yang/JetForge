# 发布清单（Release checklist）

这份文件回答一个问题：**这个包现在能不能交给玩家。**

分两段：A 段是仓库里能自动证明的，一条命令跑完；B 段只能由人做，做完了在这里打勾。
B 段不做完也可以"发布"，但那叫"上传一个 zip"，不叫"可以正式发布"。

```bash
python tools/check_readiness.py            # A 段：包本身合格吗
python tools/check_readiness.py --public   # A 段 + 上线项（真地址、stable、截图）
bash tools/run_tests.sh                    # 六步流水线（含 A 段）
```

## A 段：能自动证明的

| 项 | 判据 | 谁在查 |
| --- | --- | --- |
| 版本号三处一致 | `AssemblyInfo.cs` / `JetForge.version` / netkan | `check_release.check_version` |
| 兼容区间是承诺过的 | `.version` 的 MIN ≤ 目标 ≤ MAX，netkan 区间盖住它，`INSTALL.md` 写的与之逐字一致 | `check_ksp_version` / `check_doc_versions` |
| 元数据能拿去提交 | 英文 abstract、description、homepage、release_status 合规 | `check_netkan_meta` |
| 中英文档成对且版本条目相等 | `INSTALL` / `CHANGELOG` 两份 `.md` 与 `.en.md` | `check_doc_pairs` |
| 零件 cfg 引用的模型与贴图都在包里 | 逐条 `model=` / `texture=` 存在 | `check_release.check_parts` |
| 包里没有开发垃圾 | 集合与磁盘一致，`obj/`、`.pyc`、`*.log` 一律拒收 | `check_release.check_zip` |
| 没有调试残留 | 源码里没有 `JF_*` 环境变量钩子、没有 `Console.WriteLine` | `check_release.check_sources` |
| 马赫门限不会把火车判灭 | 每个零件的 `machLimit` ≥ 3.0 | `check_release.check_engine_limits` |
| 三种语言口径不半翻 | `check_loc` / `check_loc_keys` / `check_loc_fmt` / `check_mixed` / `check_loc_pairs` | 五道闸门 |
| 按钮文案放得下 | 英文按钮文案 ≤ 32 字符 | `check_loc_btn` |
| 数字都说得出来源 | 模型台账 22 条，每条五栏齐全，分级有断言钉住 | 回归第 98 段 |
| 实机能跑 | 真启动一次游戏跑自检，FAIL 0、算错 0 | `run_tests.sh` 第 3 步 |

## B 段：只能人做的

- [ ] **截图**：VAB 里的设计器、飞行中的发动机与尾焰、剖视页各一张。
      SpaceDock 页面上没有截图，玩家不会点下载。仓里没有 `screenshots/` 目录，
      `--public` 会报这一条。
- [ ] **真实下载地址**：两种来路挑一种，都能让 `release_status` 变成 stable，但只挑一种：
      - **包挂在 GitHub Release 上（推荐）**：写一行 `$kref`，版本号/地址/大小/哈希全由 CKAN 的
        机器人从 Release 里取，手写的版本号就不会和 Release 里的对不上：
        ```
        JETFORGE_NETKAN_KREF="你的用户名/JetForge" JETFORGE_RELEASE_STATUS=stable bash tools/make_release.sh
        ```
        这条路要求 `.version` 里的 URL 与仓库地址一致（`check_release` 会拦），
        Release 的 tag 建议就用 `v<版本号>`。
      - **自己传包（SpaceDock / 直链）**：
        ```
        JETFORGE_RELEASE_STATUS=stable JETFORGE_DOWNLOAD=https://… bash tools/make_release.sh
        ```
      状态与地址一起改，避免"地址改了状态忘了"；`JETFORGE_HOMEPAGE` 可单独覆盖主页。
- [ ] **英文审校**：`INSTALL.en.md` / `CHANGELOG.en.md` 与英文界面都是我照着中文写的，
      没有人以英语为母语读过一遍。闸门只能保证结构一致（键齐、条目齐、占位符齐），
      保证不了读起来像人话。
- [ ] **一次英文环境的完整流程**：切成 English → 装 mod → 设计一台 → 飞一次 →
      看面板 → 读 `KSP.log`。这一步能抓出所有"只有人眼看得出来"的问题。
- [ ] **强制英文开关跑一次自检**：把部署目录 `GameData/JetForge/Loc.cfg` 的
      `forceLang = en` 取消注释（**先构建、后改部署副本**，构建会把仓库那份拷回去），
      起一次游戏，确认自检第 92 行是 `本地化：语言 English｜…`。
      闸门管不到这只开关——§248 就是它静默失效了八十多轮的记录。
- [ ] **老存档的提示看一眼**：用更早版本存的零件（或把存档里 `design` 节点的 `schema` 那行删掉）
       进 VAB，悬停发动机零件说明，确认多出「存档版本：…」那一行、且同版本存档没有这一行。
       `GetInfo()` 只在游戏里由引擎调用，自检不实例化零件，闸门看不见它——§265 只有形状被钉住。
- [ ] **论坛/SpaceDock 描述**：沿用 netkan 的 abstract/description，但要配图与版本表。
- [ ] **CKAN 提交**：往 `KSP-CKAN/CKAN-meta` 提 PR，附上已上线的 netkan。
- [ ] **发布后一小时自检**：从 CKAN 装一遍（不要用手里的开发目录），确认游戏能启动、
      零件出现在列表里、`KSP.log` 里没有 `[JetForge]` 的红色异常。

## 已知的模型边界（可以发布，但不许吹）

这几条在 `CHANGELOG` 的「已知限制」里也写了，发布时不要改写成"高保真"：

- 压气机特性图是**合成的**（设计点 + 三个常数），裕度只能横向比，不能当绝对判据；
- 锥激波用等偏角斜激波 ×1.25 损失近似；
- 原版进气道 `area × 64` 只有一个样本对上过；
- 长度估算 ±8~10%，复合材料风扇的干重偏重；
- GTF1100 的涵道比 6.8 对真机 12.5，核心通流面积压不到那么小；
- 齿轮箱柔度只有一阶模型：薄壁筒的静扭角与本体扭振频率，齿面接触柔度、轴承游隙、
  多级行星均载、齿频激励都没建模——它够用来说明"这个箱子在纸面上不软"，不够用来做扭振设计。

**能发布的标准不是"没有边界"，是"边界写在玩家看得见的地方"。**