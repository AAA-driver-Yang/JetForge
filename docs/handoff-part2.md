# JetForge 交接：第二部分开发起步包

写于 2026-11-17，对应提交 `381182e`。这份是给"下一段对话/下一阶段"看的：不读聊天记录也能接着干。

---

## 0 一句话

KSP 1.12.2 的**参数化自定义喷气发动机 mod**。玩家在游戏内窗口里画几何（风扇直径、压气机级数与逐级叶尖、燃烧室形状、涡轮、轴数、加力、喷管、矢量……），mod 用**经验公式解设计点 + 非设计点循环**算推力/进气量/SFC/转速/自持，再按几何生成每台唯一的网格与羽流。

和 Flyout 的区别：Flyout 是"填性能出曲线"，我们是"画几何算性能"。

---

## 1 当前状态（可复现的硬数字）

| 项 | 值 |
| --- | --- |
| 提交 | `381182e`（git 仓库在 `D:\KSP FEAT\JetForge`） |
| 插件 | `JetForge.dll` 424960 B，md5 `8c2f7b0a75741e2e5ebc79ea56ebcd7b` |
| 设计点（内嵌 JF-1 双轴涡扇） | OPR 11.73 / FPR 1.59 / BPR 2.75 / ṁ 203.6 kg/s / 推力 82.07 kN / SFC 0.0527 kg/(N·h) |
| 网格 | 13456 三角 / 22 分部件，绕序反向 0 |
| 命令行回归 | 51 段，`全部回归通过。` |
| 游戏内自检 | 385 行，`==== 自检结束 ====`，无异常 |
| 文档 | `docs/phase2-mod.md` §1–§98（8862 行） |
| 备份 | `out/JetForge_Backup_20261117.zip`（本体+源码+文档+脚本） |

---

## 2 代码地图（`JetForge/KSP/Source`）

### 物理内核 `Core/`（不依赖 KSP，能直接在命令行跑）

| 文件 | 管什么 |
| --- | --- |
| `Gas.cs` `TechLevel.cs` | 气体物性（cp 按部件出口温度取！）、技术等级（压比/效率/叶尖马赫上限/年代表 `Year`） |
| `EngineDesignData.cs` (+`.Persistence.cs`) | 全部设计参数；`CompressorSpec`/`TurbineSpec` 含**逐级** `StageTip`/`StagePsi`；存档读写 |
| `Cycle.cs` | 设计点：压气机逐级、燃烧室（形状→效率/压损/不均匀度）、涡轮逐级容量、喷管 |
| `OffDesign.cs` | 非设计点：换算转速调节器、轴功平衡、`CalibrateIdle`、`DeriveDesignConstants`（A8/A_turb/慢车油气比/可燃窗口） |
| `FlightStep.cs` | **飞行每帧决策**：油门抬底→风车→滞后→查表→自持闸→库存桥；含 `Coasting` 惰转态 |
| `CoastThrust.cs` | 惰转残余推力指数衰减（τ=0.22 s） |
| `EngineGuard.cs` | 慢车烧不住判定（`AtBottom`/`IdleStarves`/`Describe`） |
| `Spool.cs` `SpoolDynamics.cs` | 转子惯性（τ_up、惰转 tauCoast=max(2.5,3τup)、功率积分法） |
| `Defects.cs` | 风险→降额（`DesignDerate`）与故障层（`FailureLimits.For(design, report)` 按设计裕度+年代推门槛） |
| `Surge.cs` | 喘振实时模拟（猛推/滞后/裕度→砰、推力折损、永久磨损） |
| `Criteria.cs` | 判据全集（含 `CheckIntake`/`CheckSpoolBalance`/`CheckBurnerShape`/`CheckAfterburnerLight`） |
| `PerformanceGrid.cs` `GridFactory.cs` `CycleSampler.cs` | 预解网格 / 实时解算两条路（实时 0.11–0.24 ms/次，游戏内 30 Hz） |
| `StockBridge.cs` | 接管原版 `ModuleEngines` 的推力通道（`maxFuelFlow × Isp × g`） |
| `Windmill.cs` | 风车阻力与残余转速 |
| `NozzleVisual.cs` `NozzleSchedule.cs` | 花瓣开度唯一几何源（加力那份 = 真 A8）、A8 调度 |
| `Intake.cs` | 进气道型式（Axial/Pitot/Ramp/Cone）、恢复、抽气 |
| `CompressorMap.cs` `FanMap.cs` | 压气机/风扇特性线（**两轴各自平衡仍未做**） |
| `ControlSystem.cs` | 按年代的控制系统（无控/液压/FADEC，四道闸） |
| `ThrustVector.cs` | 矢量喷口（**俯仰要带负号**，见 §87/§95） |
| `DesignSchema.cs` `DesignSignature.cs` `DetailSpec.cs` `EngineGeometry.cs` `UserLibrary.cs` `Presets.cs` `MassCost.cs` `Afterburner*.cs` `Dependencies.cs` `Filter.cs` `BuildInfo.cs` `Simple*.cs` | 元数据/指纹/细节档/剖视几何/我的设计/四台真机预设/质量造价/加力/依赖检查/噪声抹平 |

### 外围

| 目录 | 内容 |
| --- | --- |
| `Mesh/` | `JetForgeMeshBuilder.cs`（主构建：剖面回转、叶片、花瓣、法兰、细节）、`JetForgeDetailModel.cs`（低/中/高细节）、`JetForgeCutawayModel.cs`（二维剖视） |
| `FX/` | `JetForgeWaterfall.cs`（羽流/热浪/喘振闪烁，9 套模板按构型与加力分类）、`JetForgeSound.cs`（九层声音 + 转速权重 + RSE 对接） |
| `UI/` | `JetForgeDesignerWindow.cs`（1020×700，9 页）、`JetForgeCutaway.cs`、`JetForgeHeatmapWindow.cs` |
| `Diagnostics/` | `JetForgeSelfTest.cs`（主菜单全链路取证，写 `GameData/JetForge/selftest.log`）、`JetForgeFlightProbe.cs`（`flight.log` 每帧）、`JetForgeAutoLaunch.cs`（台架起飞，仅 `autotest.marker` 存在时动作） |
| `ModuleJetForgeEngine.cs` | 部件模块：接管原版链路、每帧状态、PAW、声音/羽流/矢量接线 |

### 测试 `JetForge.Test/`
`Program.cs`（51 段回归）、`ConfigNodeStub.cs`（存档往返桩）、`IdleProbe.cs`（`JF_PROBE=idle`）、`AuditProbe.cs`（`JF_PROBE=audit`，恒等式+单调性体检）、`TwoSpoolProbe.cs`（写了没跑）。

---

## 3 常用命令

```bash
# 构建（自动部署到 Kerbal Space Program/GameData/JetForge/Plugins）
dotnet build JetForge/KSP/JetForge.csproj -c Release

# 命令行回归
dotnet run --project JetForge/JetForge.Test/JetForge.Test.csproj -c Release

# 探针
JF_PROBE=idle  dotnet run --project JetForge/JetForge.Test/JetForge.Test.csproj -c Release
JF_PROBE=audit dotnet run --project JetForge/JetForge.Test/JetForge.Test.csproj -c Release

# 实机取证：清空 selftest.log → 启动 KSP → 轮询行数 → 精确 PID 杀
pwsh -NoProfile -Command "Start-Process 'D:\KSP FEAT\Kerbal Space Program\KSP_x64.exe'"
pwsh -NoProfile -Command "Get-Process KSP_x64 | ForEach-Object { Stop-Process -Id \$_.Id -Force }"
```

---

## 4 硬约束（踩过的坑，别再踩）

1. **只改 `GameData/JetForge`**，别的 mod 用 cfg 引用（用户 m09258 定的规矩）。仓库侧 `JetForge/GameData/JetForge` 才是源，csproj 每次构建覆盖安装目录。
2. **改含中文的 C# 只许 edit/write**（pwsh/python heredoc 会双重编码或折掉 `\`，导致 CS1010 或乱码）。
3. **新增 Core 文件必须同时加进 `JetForge.Test.csproj`**，否则插件编得过、回归静默丢覆盖。
4. 剖面 **y 从前往后递减**；任何"从第一个点往后判"的循环都要写成 `prevAhead == curAhead` 的形式。
5. 绕序有出口统一对账（`MeshAcc.ToMesh` 里按法线掰正，`WindingFixes` 计数必须打出来）。
6. KSP 冷启动 30~150 s 才写满 `selftest.log`；日志有缓冲，别拿"文件没长"当没加载。启动偶尔假死（CPU 不涨、`Responding=False`）→ 杀干净含 `UnityCrashHandler64`、等 5 s 重启。
7. `git` 需要显式身份：`git -c user.name=JetForge -c user.email=jetforge@local commit`。
8. `E:` 盘不许动。`KSP_clean` 备份目录已不存在，别擅自重建。
9. 用 `python` 处理素材/脚本时用 `C:\Users\sistine\.dsh\dsh-runtimes\dsh-next-primary-runtime\dependencies\python\python.exe`（PATH 上的没有 numpy）。

---

## 5 还没证的 / 已知缺口

**只能人眼人耳判的**：折边厚度、对接箍外凸、花瓣 0.765~1.139 会不会顶到短舱、羽流 0.84 倍是否还穿模、热浪粗细、声音在慢车与停车时是否够轻、喘振闪烁够不够"砰"、矢量压杆方向与叶片是否同向。

**工程缺口**：
- 涡扇 SFC 谷底在低油门（真机 0.85~0.95）——根因是缺压气机/风扇特性线 + 两轴未各自平衡；`TwoSpoolProbe.cs` 已写未跑。
- 高原有 1.30×δ 偏乐观（简单模式无特性线，抹平要凭空造）。
- `DesignSession` 未从模块里抽出来（设计器与模块仍共享草稿逻辑）。
- 进气道**形状**还没接进外形网格（性能已接）。
- 喘振裕度按最大叶尖算，逐级编辑后偏乐观。
- 加力喷管：真机 F100 加力时收小，我们开大（未定）。
- 仓库里误提交了 `Source/KSP.log`（8.3 MB），要 `git rm` 掉。

---

## 6 第二部分可以做什么（候选，待用户拍板）

1. **进气道与喷管造型落地**（第一阶段就被用户定为"后期"）：按 `Intake.cs` 的型式生成模型、与性能打通。
2. **压气机/风扇特性线**：把 SFC 谷底与两轴平衡做真——这是最后一个大物理缺口。
3. **性能→飞行闭环的第二圈**：实测数据回灌标定（有 `flight.log` 探针可用）。
4. **编辑器体验**：可视化拖拽（`DesignSchema` 47 条元数据已就位，就等渲染）、剖视图做成可点可拖。
5. **多发动机编队/共用**：同款设计多台复用网格缓存（`GridFactory.BuildCached` 已有 LRU 8）。

---

## 7 交接备注

- 上一个阶段（六阶段）已收官，五项全部有构建/自检/实机取证/文档。
- 另一个对话曾把代码改崩（Extinction 状态机），已回退；其现场在 tag `other-conv-3f64502` 与 `out/backup_otherconv/`。若想合并，重点看它的"第 52 段帧序重放"测试思路。
- 用户偏好：一句给目标、细节留给提问；讨厌谄媚与长篇铺垫；要结论先行；会拿文档核对数字。