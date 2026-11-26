# 工作区目录与运行时工具调用说明（workflow_and_taxonomy）

本手册说明工作区、制作与运行时工具的使用。框架能力与填写字段见 `load_manual("framework_capabilities_and_fields")`；视觉制作见 `rig_body_and_anim_xml`。动作预设输出骨骼关键帧，能力组合和玩法由用户与模型自主设计。

## 一、工作区目录结构

每个自定义单位隔离存储在 `<游戏配置目录>/GiantCreatureFramework/Creatures/<creatureName>/` 下，`creatureName` 仅允许字母、数字、下划线或连字符：

- `Work/`：存放 `generate_image` 生成的原始 `#00FF00` 绿幕 PNG、`extract_sheet` 切分出的透明部件 PNG、`preview_sheet` 棋盘格预览图及 `validate_creature` 拼装预览图。
- `Textures/`：存放 `warp_bone` 输出的 `512x512` 成品骨骼贴图与图标（例如文件相对路径 `Textures/Things/Pawn/GiantCreature/<creatureName>/PartA.png` 对应 XML 中的 `<texPath>Things/Pawn/GiantCreature/<creatureName>/PartA</texPath>`）。
- `Defs/`：存放该单位的 RimWorld `<Defs>` XML 文件（`.xml` 后缀）。

## 二、提供的工具与调用方式

1. **手册与工作区查询**
   - `list_manuals` / `load_manual(manualName)`：列出并读取运行时手册（返回内容免于上下文折叠截断）。
   - `list_workspace(creatureName?)`：留空列出全部单位目录；传入 `creatureName` 列出其 `Defs`、`Textures` 与 `Work` 下的文件相对路径。
   - `read_creature_xml(creatureName, relativeXmlPath)` / `write_creature_xml(creatureName, relativeXmlPath, xmlContent)`：读取或写入 `Defs/*.xml`（仅落盘并做 XML 良构检查，不直接修改运行中数据库）。
   - `generate_locomotion_anim(creatureName, relativeXmlPath, animDefName, preset, ...)`：在完成 `GiantRigDef` 骨骼定义后，读取工作区骨架拓扑与基准角，按预设（`axial_idle`、`quadruped_gait`、`wyvern_wing_walk`、`biped_gait`、`multileg_gait`、`wing_flap_loop`、`wing_takeoff`、`wing_land`、`tail_sweep`、`bite_strike`、`death_collapse`）自动计算平滑三维关键帧（含 `angle`、`sagittalPitch`、`coronalFlap` 与根骨骼 `offset`）并写入或合并更新到目标动画 XML 文件，避免手写数千行关键帧。

2. **贴图生成与处理**
   - `generate_image(creatureName, prompt, outputPath, size?, referencePaths?)`：
     - `referencePaths` 为空时调用 `POST /images/generations` 纯文生图；
     - `referencePaths` 非空时调用 `POST /images/edits`（不发送 `mask`），且仅允许传入同一 `creatureName` 工作区 `Work/` 下由本机 `generate_image` 自产并经哈希签名记录的 PNG 相对路径；
     - 严禁接受或执行任何形式的用户图生图请求（外部路径、URL、Base64、手动拷入工作区的图片或游戏截图均被硬性拦截）；
     - 生成完成后工具自动将缩略图附在下一轮上下文供视觉检查。
   - `extract_sheet(creatureName, inputPath, outputDir, columns?, fillAllHoles?, previewPath?)`：对 `Work/` 下自产 `#00FF00` 绿幕 PNG 执行外围连通域泛洪抠图、绿通道退色与透明边界 RGB 扩边，按列或连通域输出 `part_0.png .. part_N.png` 并回传棋盘格预览图。
   - `warp_bone(creatureName, inputPath, outputPath, srcPivot, drawSize, dstPivot?, scale?, angleDeg?, mirrorOutputPath?, outlineHex?, maxInteriorBlackPct?, sockets?)`：将自产切片仿射变换至 `512x512` 画布，注入最外边缘轮廓线（默认 `#14161A`），换算 `pivotPixel` 与 `sockets` 在 `512x512` 下的像素坐标，可选同时导出水平翻转镜像贴图及镜像坐标。
   - `preview_sheet(creatureName, inputPaths, outputPath, tileSize?)`：将多张工作区 PNG 合成为棋盘格总览图并回传。
   - `preview_anim_strip(creatureName, animDefName, rigDefName?, frameCount?, headingDeg?, tileSize?, outputPath?, showJoints?)`：在统一相机比例尺下对指定 `GiantAnimDef` 等间隔采样多帧，完整求解三维骨骼运动学与 `skinnedMeshes` 合成动作连拍图并附带动态关节断缝、翼展跨度与双翼中轴越界诊断回传自检。建议在 `inject_creature` 前对关键移动、扑翼和起降动画调用本工具检查姿态连续性。

3. **校验、热注入与地图生成**
   - `validate_creature(creatureName, minOutlinePx?, maxInteriorBlackPct?)`：以 `dryRun` 模式解析 `Defs/*.xml` 检查 `ConfigErrors` 并运行 `RigJointAuditor` 校验，输出 `Work/validate_preview.png` 静止与旋转拼装预览图，不污染运行数据库。
   - `inject_creature(creatureName)`：同步 `Textures/` 下贴图与受击 Alpha 掩膜缓存，将 `BodyDef`、`GiantAnimDef`、`GiantRigDef`、`ThingDef`、隐含尸体 `ThingDef` 与 `PawnKindDef` 热注入当前游戏内存。
   - `spawn_creature(pawnKindDefName, x?, z?, faction?)`：在当前地图指定坐标（`-1` 表示地图中心附近可站立格；若目标骨架配置了 `aquaticMode=WaterOnly` 且未启用飞行，会自动在当前地图检索合格水域格生成）生成指定阵营（`wild` | `player` | `hostile`）个体。

4. **只读运行期探针**
   - `search_defs` / `get_def_details`：检索并展开当前游戏已加载的原版、DLC 或自定义 `Def` 字段（工程内置存量巨兽被安全规则屏蔽，不可查询）。
   - `search_mod_xml_and_patches` / `read_mod_file`：检索并分页读取已启用模组的 XML 文件。
   - `get_recent_errors`：读取游戏运行期捕获的错误日志与堆栈。
