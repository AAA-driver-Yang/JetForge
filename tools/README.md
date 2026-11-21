# tools/ 里都是什么

三类东西，混在一个目录里，但用途完全不同：

## 1. 闸门（跟着流水线跑，红了就不许提交）

`tools/run_tests.sh` 的第 2 步挨个调用它们，任何一个返回非 0 都算没过。每条闸门管一件事，
判据写在脚本开头的注释里（为什么需要它、能抓什么、为什么别的闸门抓不到）。

| 脚本 | 管什么 |
| --- | --- |
| `check_panels.py` | 面板一行不超过 5 个读数（多了就没人读） |
| `check_catches.py` | 物理与状态路径上不许有吞掉错误的空 `catch` |
| `check_loc.py` | 中文字面量只许减不许增（双语迁移按轮推进） |
| `check_loc_keys.py` | 源码里的 `#autoLOC_` 键在两份词条里都存在；键不许被拼进字符串 |
| `check_loc_unused.py` | 两份词条里写下的键得有人引用（写好了没人念等于没写） |
| `check_loc_fmt.py` | 成对字面量两边的 `{n}` 一致（少了半句、多了 `FormatException`） |
| `check_mixed.py` | 同一句里不许"已翻的半句 + 裸中文"（只有英文玩家看得见） |
| `check_loc_pairs.py` | `Loc.T(中文, English)` 没写反、中文槽没被英文占 |
| `check_loc_join.py` | 两段拼接的英文不会挤成 `limit.Where` / `m,above` |
| `check_loc_btn.py` | 按钮英文不超过 32 字符（按钮不折行，会挤掉兄弟） |
| `check_label_fit.py` | 固定宽度控件里的英文放得下（中文 13 px/字、英文 6.6 px/字母） |
| `check_hint_fit.py` | 悬停提示不长得像说明书（会盖住它要解释的控件） |
| `check_hint_cover.py` | 每个可编辑字段都有说明（悬停或滑杆右边那句注） |
| `check_dead_fields.py` | 没有只写不读的字段（"自称是一道闸、其实没接线"） |
| `check_dead_methods.py` | 没有只声明不调用的方法（反射入口在白名单里） |
| `check_pack_notes.py` | 随包文件里没留开发笔记（`GameData/**` 会原样发给玩家） |
| `check_pack_vs_deploy.py` | 打出来的 zip 与自检测的那份部署目录逐字节一致 |
| `check_release.py` | 版本三处一致、贴图引用齐、无调试残留、netkan 元数据说得通 |
| `check_readiness.py` | 发布就绪 A 段（许可/README/更新日志/元数据口径） |

最后三条（`pack_vs_deploy` / `release` / `readiness`）在流水线第 5 步跑——它们查的是刚打出来的那个包，
不是源码。

`*_baseline.json` 与 `*_allow.txt` 是这些闸门的账本：基线是"现在还剩多少处"，只许往下走；
`allow` 是"这一处确实有理由，不是欠账"，每条都要写成一句话。

## 2. 生成器（产物进 `GameData/JetForge/`，偶尔才跑一次）

`gen_textures.py` / `gen_textures_hd.py` / `gen_sounds.py` / `gen_waterfall_templates.py`。
产物是二进制，跟着仓库走；重跑会覆盖，改完记得看一眼 diff 里那几张图。

## 3. 打包与备份

- `make_release.sh`：出 `_release/JetForge-<version>/` 与同名 `.zip`、`.netkan`，校验三处版本一致。
- `make_backup.sh` / `make_backup_zip.py`：作者本机的存档备份，与发布无关。
- `tidy_loc.py`：本地化词条排序整理。

## 怎么跑

```bash
bash tools/run_tests.sh          # 六步全跑（要 KSP 安装目录，约 2 分钟）
bash tools/run_tests.sh --no-game  # 跳过实机自检
bash tools/run_tests.sh --ci     # 没有 KSP 的机器：闸门 + 回归（GitHub Actions 用这个）
```

游戏目录按三级来路找：`-p:KspDir=...` > 环境变量 `JETFORGE_KSP_DIR` > 仓库根的 `local.props`
（模板见 `local.props.example`，那份不进版本库）。