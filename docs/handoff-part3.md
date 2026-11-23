# JetForge 交接 · 第三部分（§99–§101 之后）

本轮目标（用户原话大意）：先做核心机架构升级，再把"第二部分 自定义进气道"做完，进气道要是完全独立的部件、有自己的编辑面板。

## 一、这一轮交付了什么

五个提交（`JetForge` 仓库，基线是 `381182e`）：

| 提交 | 内容 |
| --- | --- |
| `1f87ec4` | 设计点从 311 行函数搬进 `EngineNetwork`（固定拓扑模板 + 部件求解器 + 逐轴 `ShaftSet`），进气道拆出 `IntakeSolver`。51 段回归与基线逐位一致 |
| `1383b58` | 进气道几何进网格与剖视（唇口参数化、中心锥、喉道由面积反推）、独立「进气道」编辑页、OffDesign 喉道限流、站表与网络回归 |
| `b92bdba` | 畸变接进喘振风险（NaN 默认走老代理项）、简单模式给三个进气道旋钮 |
| `d731ceb` | 自检四型式剖视四连拍（中心锥三处一致） |
| `48a80dd` | 迎角与侧滑进进气道（缺省 0° 逐位不变） |

## 二、架构长什么样

```
Core/Arch/GasState.cs        GasState（总/静参数、流量、组分）+ Stations 测站编号
Core/Arch/EngineComponent.cs IEngineComponent / ComponentResult / SolverContext / 端口
Core/Arch/GasPath.cs         固定拓扑模板（GasPathTemplate.ForDesign），可校验
Core/Arch/Components.cs      九个部件求解器（进气道/风扇/压气机/分流器/燃烧室/涡轮/加力/喷管/管道）
Core/Arch/EngineNetwork.cs   按模板调度、站表、组装 CycleResult
Core/Arch/ShaftSet.cs        ShaftDefinition / ShaftState（每轴 ω、惯量、功率、净转矩）、ShaftSet
Core/Intake/IntakeSpec       （仍在 Core/Intake.cs）型式+三块面积+抽气+唇口+弯管+迎角侧滑
Core/Intake/IntakeSolver     性能：恢复系数、三块面积、捕获/可用流量、溢流、阻力、畸变、限流
Core/Intake/IntakeGeometry   几何：捕获/喉道/面三截面、流道/外罩/中心锥型线
Core/DesignTabs.cs           页号唯一来源（总体 0 / 进气道 1 / … / 剖视 9）
```

`Cycle.SolveDesignPoint` 现在只有一行：`return EngineNetwork.SolveDesign(d).Result;`。

## 三、口径（改之前务必读）

1. **缺省必须逐位相同**。进气道所有新参数（捕获直径、喉道比、抽气、唇口、弯管、迎角、侧滑）缺省值都让系数正好为 1 或 0，乘/加都是精确的。基线 diff 只剩时序、路径、存档字段数（87→91）、指纹（77→81）、元数据（53→57）。
2. **三块面积各管一件事**：捕获面积=唇口兜多少、喉道面积=最多能过多少（硬上限）、发动机面=压气机面前截面（配面马赫）。不许互相顶替——上一轮把喉道比乘进捕获面积，所有老存档进气量凭空少 15%。
3. **可用流量按喉道壅塞算，不按面马赫算**。按面马赫算会把 11 km 静推力压掉 9.6%：设计选择不是运行约束。
4. **外涵喷管进口总压仍是 `p0*fpr*tpr`**（老口径，非含冲压的 pt2）。改它会让 JF-1 在 11 km/Ma0.85 推力从 14.57 跳到 20.34 kN，属于改物理，得单独一轮统一定标。
5. **`ShaftSet.Build` 会调 `MassCost.Estimate` → `Cycle.SolveDesignPoint`**，绝不能在设计点求解内部调用它，会无限递归。
6. 判据类模块只吃 `CycleResult` 的扁平字段（`mdot_core`/`T2` 等），漏填不报错，只会算出 φ=0 然后触发 `Presets.FitSurge` 把叶尖直径从 0.62 一路缩到 0.10——涡喷那次就是这么塌的。

## 四、验证现状

- `JetForge.Test` 56 段全绿（含新增 52/53/54/55/56 五段：几何、站表与逐轴、畸变、简单模式进气道、迎角侧滑）。
- `dotnet build KSP/JetForge.csproj -c Release`（net472）0 错 0 警告；DLL 由 csproj 自动装到 `GameData/JetForge/Plugins`。
- 实机自检 387 行跑完：判据 FAIL 0 / WARN 2、剖视越界页 0、网格 13648 三角绕序 0、`提示表 52 条｜完全没登记的项=（没有）`、`字段元数据 57 条`。
- 文档：`docs/phase2-mod.md` §99（架构）、§100（畸变+简单模式）、§100.6（四型式剖视）、§101（迎角侧滑）。

## 五、还没做的（都需要用户点头，会改手感/读数）

1. **逐轴瞬态接进飞行**。`ShaftSet` 有物理与单测（盈余升速、缺功掉速、分轴互不影响），但 `FlightStep` 还是总 spool + 时间常数律。接进去要重标定五个真机预设的时间常数。
2. **非设计点走部件链路**。现在还是等效模型（Prat3 + 换算转速 + 特性线标定）。`OffDesign` 只给"总压气机功"，LPC/HPC 份额拆不开——所以上面第 1 条也不能单独做，两根轴谁该转多快是它算的。
3. 进气道几何是回转近似（二维斜板的真实三维形状没做）；迎角是设计参数，不随飞行姿态自动变化。
