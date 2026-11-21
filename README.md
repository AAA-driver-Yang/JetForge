# JetForge（开发仓库）

参数化喷气发动机与进气道的 KSP 插件。**画几何 → 算热力**：先定尺寸、级数、截面与流道，
再由部件网络算出推力、耗油率、各轴转速与工作点，而不是"填性能反推曲线"。

玩家文档在 [`INSTALL.md`](INSTALL.md)（安装与首次使用，英文版 [`INSTALL.en.md`](INSTALL.en.md)）
与 `GameData/JetForge/README.md`（随发布包一起走）。

## 目录

|路径|内容|
|-|-|
|`KSP/Source/Core/`|热力与气动内核。**不依赖 KSP**，可单独编译与回归|
|`KSP/Source/Core/Arch/`|部件网络、逐轴转矩、几何接口表、长度/损失、特性图|
|`KSP/Source/Core/Intake/`|进气道求解器、激波、型线、多件合成、原版件桥接|
|`KSP/Source/Core/Diagnostics/`|状态审计、故障台账、模型档案（纯 .NET，可测）|
|`KSP/Source/Mesh/`|程序化网格（机身、进气道放样、花瓣、剖视）|
|`KSP/Source/UI/`|设计器、进气道窗口、型线编辑器、向导、测试台、剖视|
|`KSP/Source/FX/`|Waterfall 羽流与 RSE 干态声音（全部走反射）|
|`KSP/Source/Diagnostics/`|实机自检（真启动游戏跑一遍）|
|`KSP/Source/Properties/`|程序集元数据（版本号与 `Core/Version.cs` 同步）|
|`GameData/JetForge/`|随包发布的内容：零件 cfg、贴图、声音、Waterfall/RSE 配置、`.version`、词条|
|`JetForge.Test/`|回归工程（段数见 `dotnet run` 末行，不引用 UnityEngine）|
|`tools/`|十九道闸门、贴图/音效生成器、发布打包与备份脚本（分类见 [`tools/README.md`](tools/README.md)）|
|`proto/`|最早的 Python 原型（移植到 C# 之前的参照实现，不参与构建）|

## 构建

先告诉构建脚本你的 KSP 装在哪。三种办法，挑一种：

```bash
# ① 写一份 local.props（模板就在仓库根，这份不进版本库，也不会被提交）
cp local.props.example local.props      # 然后把里面的路径改成你的

# ② 环境变量
export JETFORGE\\\_KSP\\\_DIR="C:/Program Files (x86)/Steam/steamapps/common/Kerbal Space Program"

# ③ 命令行
dotnet build KSP/JetForge.csproj -c Release -p:KspDir="C:\\\\Program Files (x86)\\\\Steam\\\\steamapps\\\\common\\\\Kerbal Space Program"
```

然后：

```bash
# 插件（net472，产物自动复制到 KspDir 的 GameData/JetForge 下）
dotnet build KSP/JetForge.csproj -c Release

# 回归（84 段、1800 多条断言；末行必须是「全部回归通过。」）
dotnet build JetForge.Test/JetForge.Test.csproj -c Release
dotnet run --project JetForge.Test/JetForge.Test.csproj -c Release --no-build
```

没配 KspDir 时构建会停下来报一句中文，而不是编出一个"缺引用的成功"。

## 一条命令跑全部检查

```bash
bash tools/run\\\_tests.sh            # 六步：构建 → 十六道闸门+回归 → 实机自检 → 出包 → 发布闸门 → 结论
bash tools/run\\\_tests.sh --no-game  # 跳过实机自检（不启动游戏）
bash tools/run\\\_tests.sh --ci       # 没有 KSP 的机器（CI 用这个）：闸门 + 回归
```

各道闸门管什么、账本文件怎么用，见 [`tools/README.md`](tools/README.md)。
GitHub Actions 跑的是 `--ci` 那一档（[`.github/workflows/ci.yml`](.github/workflows/ci.yml)）——
实机自检与出包要真的游戏目录，只能在本地做。

构建要求：.NET SDK 8+（`net472` 目标）、KSP 1.8\~1.12 的 `Managed` 目录下的四个引用
（`Assembly-CSharp` / `UnityEngine` / `UnityEngine.CoreModule` / `UnityEngine.AudioModule`）。
Waterfall 与 RocketSoundEnhancement 是**硬依赖**：羽流只剩 Waterfall 一条路（自绘加法锥已删），
干态声音交给 RSE。少一样不会崩，但玩家会看到「发动机在转却不出火」或「一点声都没有」——
所以 `Core/Dependencies.cs` 在启动时就报一次缺什么，`netkan` 里也写成了 `depends`。

## 回归与自检

* **回归**：`JetForge.Test` 把 Core 全部路径跑一遍——气路守恒、各站状态、喷管分项、
进气道（亚/跨/超声速、型线、多件、原版件）、压气机特性图、逐轴转矩、故障台账、存档往返、
长度与质量标定。末行 `全部回归通过。` 才算过；任何一条超差返回码非 0。
新加 Core 文件**必须手工加进** `JetForge.Test.csproj` 的 `<Compile Include>`，否则静默丢覆盖。
* **实机自检**：进游戏后 `GameData/JetForge/selftest.log` 会记录 400+ 行结果
（判据闸门、状态审计、零件网格三角数、原版桥往返、贴图齐全）。看到
`==== 自检结束 ====` 与 `FAIL 0` 才算跑完。

## 发布

```bash
tools/make\\\_release.sh          # 产出 \\\_release/JetForge-<version>.zip 与解包目录
```

脚本会校验三处版本号一致（`Core/Version.cs`、`Properties/AssemblyInfo.cs`、`JetForge.version`），
不一致直接退出，避免打出 CKAN 拒装的包。

产出的 `.zip` 旁边还会生成一份 `JetForge-<version>.netkan`：里面已经填好包的 sha256 与文件名，
只差一个 `download` 地址（填你上传后的直链，或删掉这一行改用 `$kref`）。
这份元数据是给 CKAN 索引用的，字段都写在文件里，不需要再另写一份。

## 许可

MIT，见 [LICENSE](LICENSE)。`GameData/JetForge/Sounds/` 下的音源由 RSE 的 JetsTurbines 派生，
遵循其 GPL-3.0；贴图与网格由本仓库脚本生成。

