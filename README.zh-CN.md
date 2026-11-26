# Giant Creature Framework (GCF)

[English](README.md) | **简体中文**

专为 RimWorld 1.7 打造的巨型生物、超重型机甲与大型载具底层框架。

---

## 概述

在原版 RimWorld 中，无论体型多大，生物本质上仍由单张贴图整体平移渲染。Giant Creature Framework（GCF）在 RimWorld 1.7 之上构建了完整的多部件骨骼、线性混合蒙皮与多格空间模拟层：

- **多部位骨骼与柔性动画**：支持独立驱动头部、颈部、四肢、双翼、长尾、炮塔、履带滚动、贝塞尔链、软鞭以及 2D 线性混合蒙皮（LBS）网格。
- **断肢破坏与战损表现**：破坏解剖部位时，对应视觉骨骼子树会断裂飞出并坠地，挂载在该部位上的武器与推进部件随之失效，同时支持伤痕、血迹与包扎等渐进战损贴图叠加。
- **三层动态多格体积与顺滑交互**：随单位朝向实时光栅化核心实体碰撞格（`CoreSolid`）、外部小型单位排挤冠层格（`Canopy`）与像素级受击轮廓格（`HitboxOnly`），近战贴身交战、驯服、喂食与包扎均可直接点击身体任意位置正常响应。
- **多武器齐射、独立炮塔与阶段变形**：支持单单位多炮口同时开火、360 度独立炮塔追踪与后坐力回弹，以及首领战多阶段变形展开。
- **空地飞行、水栖航行、多人座舱与跨图远征**：支持空地飞行切换、两栖/水上载具机动、最多 12 人座舱或骨骼绑定骑乘位，以及直接飞越世界地图远征据点。
- **内置样机（`GCF_MechScorpion`）**：主框架内置「重装机械巨蝎（`GCF_MechScorpion`）」，演示多足步态、部位破坏与尾刺齐射，并可选支持 Biotech 机械师培育。
- **游戏内 AI 巨兽创作工坊（`GiantCreatureFramework.Agent`）**：可在模组设置中按需开启创作终端并填入自己的 LLM 与文生图 API，在游戏内通过自然语言生成分部件贴图、完成关节拼装与动作配置，并免重启热加载至当前地图测试。

---

## 依赖与加载顺序

- **游戏版本**：RimWorld 1.7
- **必需前置**：[Harmony](https://github.com/pardeike/HarmonyRimWorld/releases/latest)（`brrainz.harmony`）
- **官方 DLC**：所有官方 DLC（`Royalty`、`Ideology`、`Biotech`、`Anomaly`、`Odyssey`）均为可选；加载 `Biotech` 时自动启用重装机械巨蝎机械师配方。
- **不兼容模组**：`RimThreaded`（`majorhoff.rimthreaded`）

推荐加载顺序：

1. `Harmony`（`brrainz.harmony`）
2. `RimWorld` 本体及官方 DLC
3. `Giant Creature Framework`（`sanguo.giantcreatureframework`）
4. 独立 GCF 扩展子模组

---

## 仓库结构

| 路径 | 说明 |
| :--- | :--- |
| `About/` | RimWorld 模组元数据（`About.xml`）与模组预览图 |
| `1.7/Defs/` | 核心定义文件（Body、Rig、Anim、HealthBar、Shader、Job、UI 与重装机械巨蝎） |
| `1.7/Biotech/` | 可选 Biotech 机械师配方与重装机械巨蝎兼容补丁 |
| `1.7/Languages/` | 多语言本地化文件（简体中文、英文、日文、俄文） |
| `1.7/Shaders/` | 空中阴影与多层特效自定义 GLSL 着色器 |
| `1.7/Textures/` | 机械巨蝎分部件贴图、通用战损贴花与 UI 图标 |
| `1.7/AgentSkills/` | 游戏内创作终端使用的提示词规范、工具 schema 与制作手册 |
| `Source/GiantCreatureFramework/` | 核心框架 C# 源码工程（`GiantCreatureFramework.csproj`） |
| `Source/GiantCreatureFramework.Agent/` | 游戏内 AI 巨兽创作工坊工程（`GiantCreatureFramework.Agent.csproj`） |
| `Source/GiantCreatureFramework.Agent.Tests/` | 创作终端与图像处理管线独立 MSTest 单元测试套件 |
| `Source/GiantCreatureFramework.Arena.BridgeTools/` | 可选的 RimBridgeServer 巨兽竞技场联调伴侣工具 |
| `Tools/` | 竞技场命令行启动与对战录制工具（`giant_arena_cli.py`） |
| `Docs/` | 架构说明、能力与制作手册、性能报告及工坊描述文档 |

---

## 编译与测试

C# 工程面向 `.NET 10`（`net10.0`），通过 `$(RimWorldPath)` 引用 RimWorld 1.7 程序集（默认 `D:\Steam\steamapps\common\RimWorld`）。若游戏安装路径不同，可通过 `-p:RimWorldPath` 覆盖：

```bash
dotnet build Source/GiantCreatureFramework/GiantCreatureFramework.csproj -c Release -p:RimWorldPath="PATH_TO_RIMWORLD"
dotnet build Source/GiantCreatureFramework.Agent/GiantCreatureFramework.Agent.csproj -c Release -p:RimWorldPath="PATH_TO_RIMWORLD"
```

运行独立的创作终端与图像处理单元测试：

```bash
dotnet test Source/GiantCreatureFramework.Agent.Tests/GiantCreatureFramework.Agent.Tests.csproj -c Release
```

编译产物输出至 `1.7/Assemblies/`（已被 Git 忽略）。编译时附加 `-p:DeployToGame=true` 可自动将模组文件部署到 `$(RimWorldPath)\Mods\GiantCreatureFramework`。

---

## 文档

- [Giant Creature Framework 能力与制作手册](Docs/GiantCreatureFramework_Manual.md)
- [CompGiantRig 架构说明](Docs/CompGiantRig_Architecture.md)
- [性能报告](Docs/Performance_Report.md)

---

## 开源协议

本项目基于 MIT 协议开源，详见 [LICENSE](LICENSE)。
