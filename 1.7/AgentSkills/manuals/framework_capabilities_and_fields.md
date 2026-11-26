# GCF 框架能力与配置字段（framework_capabilities_and_fields）

本手册列出框架已经提供的能力、接入位置、真实字段和使用约束。用户与模型自行选择能力、组合技能和设定数值；本文中的初始值表示源码默认行为，不是推荐配装或平衡方案。视觉制作细节见 `rig_body_and_anim_xml`，总览见 `gcf_archetype_and_def_manual`。

## 1. 能力与接入位置

| 能力 | 挂载位置或入口 | 关键类型 |
| --- | --- | --- |
| 骨架、动作、蒙皮、柔性链、视觉挂点 | `ThingDef.comps` 绑定骨架，再配置骨架子项 | `CompProperties_GiantRig`、`GiantRigDef`、`GiantAnimDef` |
| 多格受击、碰撞、寻路避让、尸体占格 | 骨架及骨骼字段 | `GiantRigDef`、`BoneNodeDef` |
| 断肢、弱点、护甲、痛觉与核心死亡规则 | 骨架与解剖映射 | `GiantRigDef`、`BoneNodeDef` |
| 部位近战、远程、弹射与并发动作 | `BoneNodeDef.partAttacks` | `GiantPartAttackDef` |
| 多发射口轮换、齐射与缺损回退 | `GiantRigDef.weaponEmitters` | `GiantWeaponEmitterDef`、`GiantEmitterBarrelDef` |
| 座舱驾驶、生物骑乘、乘员岗位和登离舱 | `ThingDef.comps` | `CompProperties_GiantCockpit`、`CompProperties_GiantCrew` |
| 阶段变形、阶段属性与主动进退阶段 | `ThingDef.comps` 与 `AbilityDef.comps` | `CompProperties_GiantPhaseTransform`、阶段操作组件 |
| 履带、轮式、悬浮、水面舰船运动 | `GiantRigDef.vehicleLocomotion` | `VehicleLocomotionDef` |
| 水面巨兽、两栖涉水与纯水栖/舰船限制 | `GiantRigDef.aquaticLocomotion` | `GiantAquaticDef` |
| 起降、空中高度层、越障与升力损伤 | `GiantRigDef.flightLocomotion` | `GiantFlightDef` |
| 固定翼、垂直起降、飞艇机动 | `GiantRigDef.flightVehicleLocomotion` | `FlightVehicleLocomotionDef` |
| 机库、舰载机发射/回收/重建 | `ThingDef.comps` | `CompProperties_CarrierHangar` |
| 部位完好施放条件、范围效果与核心修复 | `AbilityDef.comps` 或 `ThingDef.comps` | `CompProperties_AbilityRequiresGiantBone`、`CompProperties_AbilityAbyssalQuake`、`CompProperties_GiantBioferriteCore` |
| 玩家征召、排队命令与自主作战节点 | `ThinkTreeDef` | `ThinkNode_GiantPlayerDrafted`、`JobGiver_GiantPartCombatPositioning`、`JobGiver_GiantAbilityFight` |
| 世界地图旅行、落图与地点交互 | 已接入框架的 Pawn 与世界对象 | `GiantWorldUtility`、`WorldObject_Giant` |
| 扩展模块接口隔离说明 | 主框架通用组件清单 | 本文第 10 节 |
| 创作、预览、热注入、调试与竞技场 | 工坊工具、调试界面、CLI/桥接 | 本文第 11 节 |

`ThingDef.comps` 和 `AbilityDef.comps` 中使用 `<li Class="GiantCreatureFramework.类型名">`。骨架下的普通配置块直接用字段名，例如 `<flightLocomotion>`。Def 引用填目标 `defName`，骨骼引用填 `nodeId`，两者不能混用。

单位：距离通常为地图格，角度为度；`*Tick` / `*Ticks` 使用游戏 Tick，`60 Ticks = 1 秒`。`GiantPartAttackDef.warmupTime` / `cooldownTime` 使用秒。`CellsPerSec`、`CellsPerSec2` 分别为格/秒、格/秒²。具体数值范围以相应配置类型的 `ConfigErrors` / `ValidateConfig` 为准。

## 2. 座舱控制与生物骑乘

`CompProperties_GiantCockpit` 用于座舱；`CompProperties_GiantCrew` 继承相同字段并使用生物骑乘默认值。

| 配置组 | 字段与用途 |
| --- | --- |
| 无人运行 | `autonomousWhenEmpty` 决定无人时是否允许自主工作；`autonomousCoreBodyPart` 绑定自主核心；`allowHostileAutonomousFallback` 控制敌对单位无人回退 |
| 控制前置 | `allowPlayerControlWithoutOverseer`、`requireOverseerWhenBiotechActive`；后者在启用 Biotech 时检查实际机械师控制和带宽 |
| 岗位限制 | `requireDedicatedGunner`、`requireDedicatedCommanderForAbilities` |
| 宿主行为 | `treatHostAsVehicle` 区分机械载具行为与生物行为；生物骑乘组件默认 false |
| 乘员能力修正 | `autonomousMoveSpeedFactor`、`autonomousAccuracyFactor`、`autonomousReloadFactor`；`pilotedMinMoveSpeedFactor`、`pilotedMaxMoveSpeedFactor`、`pilotedMinAccuracyFactor`、`pilotedMaxAccuracyFactor`、`pilotedMinReloadFactor`、`pilotedMaxReloadFactor`；`commanderFireControlBonus` |
| 登舱与显示 | `enterTicks`、`renderCrewOnRig`、`seats` |

`seats` 是 `GiantCockpitSeatDef` 列表：`seatId` 唯一，`role` 取 `PrimaryPilot`、`Gunner`、`Commander`、`Passenger`。`linkedBodyPart` / `linkedBodyPartCustomLabel` / `linkedBodyPartIndex` 绑定座位部位；`bleedthroughFactor` 和强制离舱字段控制乘员受损行为。`rigSeatId` 绑定视觉挂点，省略时使用 `seatId`；开启乘员显示后，骨架必须存在匹配的 `crewSeats`。

控制模式 `Dormant` / `Autonomous` / `Piloted` 是运行时计算结果，**不是可填写的 `mode` 字段**。模式由可控乘员、无人运行设置、核心状态、停车状态和控制前置共同决定。定义了 `PrimaryPilot` 座位时检查该岗位上的有效乘员；未定义该岗位时，有效乘员可以承担控制。

接入片段：

```xml
<li Class="GiantCreatureFramework.CompProperties_GiantCockpit">
  <autonomousWhenEmpty>false</autonomousWhenEmpty>
  <requireDedicatedGunner>true</requireDedicatedGunner>
  <renderCrewOnRig>false</renderCrewOnRig>
  <seats>
    <li><seatId>pilot</seatId><role>PrimaryPilot</role></li>
    <li><seatId>gunner</seatId><role>Gunner</role></li>
  </seats>
</li>
```

该片段展示字段层级，不规定座位数量或岗位组合。座位数来自列表，示例单位的 12 座配置不是框架统一上限。

`CompProperties_GiantCrew` 的构造默认值覆盖父类：`treatHostAsVehicle=false`、`renderCrewOnRig=true`，三个 `autonomous*Factor` 均为 1。其余字段继承座舱组件。

## 3. 阶段变形

`CompProperties_GiantPhaseTransform.stages` 是至少两个 `GiantPhaseStageDef` 的列表，列表顺序决定阶段顺序。每阶段必须有 `rigDef`。

- 组件字段：`triggerSummaryHealthThreshold`、`minStageDwellTicks`、`finalMeltdownRadius`、`finalMeltdownDamage`。
- 阶段标识：`stageId`、`stageLabelKey`。
- 倍率字段：`bodySizeFactor`、`healthScaleFactor`、`meleeDamageFactor`。
- 加法字段：`moveSpeedOffset`、`armorSharpOffset`、`armorBluntOffset`、`armorHeatOffset`；不能当倍率填写。
- 动画引用：`morphOutToNextClip`、`morphInFromPrevClip`、`morphOutToPrevClip`、`morphInFromNextClip`。动画自身定义时长。
- 阶段能力与武器：`stageAbilities`、`rangedWeaponDef`；配置部位武器时，不可同时使用装备武器。
- 表现和转阶段效果：`reactorGlowColor`、`baseGlowIntensity`、`transitionBlastRadius`、`transitionEmpDamage`、`transitionBlastDamage`。

```xml
<li Class="GiantCreatureFramework.CompProperties_GiantPhaseTransform">
  <stages>
    <li><stageId>base</stageId><rigDef>Custom_BaseRig</rigDef></li>
    <li><stageId>expanded</stageId><rigDef>Custom_ExpandedRig</rigDef></li>
  </stages>
</li>
```

引用的骨架需先定义。主动操作使用 `CompProperties_AbilityMechPhaseAdvance` / `CompProperties_AbilityMechPhaseRevert`，挂到 `AbilityDef.comps`；这两个组件没有额外自定义字段，操作当前宿主的阶段组件，实际可用条件由阶段系统检查。

## 4. 部位武器、发射口与动画绑定

`BoneNodeDef.partAttacks` 中每项为 `GiantPartAttackDef`，`kind` 为 `Melee`、`Ranged` 或 `CarrierCatapult`。

- 标识与节奏：`attackId`、`label`、`warmupTime`、`cooldownTime`、`initialPhaseOffset01`。
- 目标范围：`minRange`、`range`、`arcReference`（`HullHeading` / `BoneWorldAngle`）、`sectorCenterDeg`、`sectorHalfAngleDeg`、`requireLineOfSight`。
- 定位：`socketId`、`rangeOriginBoneId`、`strikeUV` / `strikePixel`、`useSoftWhipTip`。
- 近战：`damageDef`、`power`、`armorPenetration`、`cleaveRadius`、`cleaveDamageFactor`、`knockbackDistance`、`stunTicks`、`selectionWeight`。
- 远程：`projectileDef`、`burstShotCount`、`ticksBetweenBurstShots`、`forcedMissRadius`、四档 `accuracy*` 字段。
- 动画与操作：`attackAnim`、`independentOverlay`、`requiresPrimaryWeaponOperator`、`canStrikeGroundWhileFlying`；音效、后坐和炮口表现字段见附录。

近战和远程均需非循环 `attackAnim`。近战片段必须无 `events`；独立近战叠加还需在动画中指定 `isolatedSubtreeRootBoneId`。载具骨架不允许近战。使用部位武器的宿主应清空继承的原版 `tools`，并关闭 PawnKind 的原版近战入口，避免两套攻击来源。

近战包络与三层占地对齐约束：非软鞭、非贝塞尔链近战武器的 XML `range` 不得超过 `rangeOriginBoneId` 到 `strikePixel` 平面解剖跨度加 `1.60` 格（防止隔空挥击），且在 `impactTick` 处的打击点加上 `maxDynamicMeleeContactLungeCells`（默认 `0.85`，范围 `0~2.0`）必须能触及目标表面。巨兽对巨兽近战以双方缩回躯干的 `collisionCells`（核心碰撞体）外 `1` 格停步，允许长颈与双翼互相探入对方轮廓；地面巨兽对非巨兽小型 Pawn（开启 `enableCollisionPawnPushOut` 时）则以自身 `pawnExclusionCells`（视觉冠层）外 `1` 格停步，防止行进时把小型目标持续推走。因此，凡有前伸头颈或双翼冠层的近战生物，至少一项正面主近战武器（如头部撕咬或前探利爪）的 `range` 与 `strikePixel` 必须覆盖视觉冠层前缘外侧 `1` 格。

`GiantRigDef.weaponEmitters` 配置多发射口。`GiantWeaponEmitterDef.fireMode` 为 `Alternating`、`PrimaryWithFallback`、`Salvo`；字段名是 **`fireMode`**。通过 `weaponDefName`、`abilityDefName`、`verbTag` 匹配调用来源，`barrels` 中用 `boneId` / `socketId` 指定出口。`requireAllBonesIntact` 控制部件完好要求。

动作引用包括 `meleeAttackAnims`、`rangedAttackAnim`、`abilityAnims`。`GiantAnimDef.triggerToolTag` 用于动作匹配，`impactTick` 定义动作采样中的命中时刻；实际伤害由调用方执行，单独写动画不等于创建技能。

## 5. 地面、水面、空中与入场运动

| 模块 | 启用字段 | 主要配置 |
| --- | --- | --- |
| 地面与水面载具 | `vehicleLocomotion.chassisType`：`None` / `Tracked` / `Wheeled` / `Hover` / `Naval` | 原地转向、最小转弯半径、加减速、倒车、停车朝向、炮塔稳定、驱动部件损伤 |
| 水面与海生水栖 | `aquaticLocomotion.aquaticMode`：`None` / `Amphibious` / `WaterOnly` | 浅水/齐胸水/深水/海洋/内河/沼泽/毒水通行、吃水净空、岸线避让、水陆移速倍率、水中动画、浮力与水花尾迹、世界海洋/海岸/内河航行 |
| 空地切换 | `flightLocomotion.flightMode`：`None` / `AlwaysAirborne` / `TakeoffAndLanding` | 起降动画与时长、自动起降条件、越障、升力损伤、空中近战规则、阴影 |
| 航空机动 | `flightVehicleLocomotion.airframeType`：`None` / `FixedWing` / `RotorcraftVTOL` / `HeavyAirship` | 最小空速、盘旋、悬停、滚转、推力、减速、地面滑行、炮塔稳定 |
| 入场飞行 | `flightEntry.enabled` | `flightAnim`、`durationTicks`、`startOffsetCells`、`startAltitudeOffset`、落地表现 |

运动配置块挂在 `GiantRigDef` 下（或 `steering` 同名字段下）；`vehicleLocomotion` 与 `flightVehicleLocomotion` 不能同时启用，但 `aquaticLocomotion` 可独立用于生物型海生/两栖巨兽，也可与 `vehicleLocomotion`（例如 `chassisType=Naval` 水面舰船或 `Hover` 气垫艇）或 `flightLocomotion`（水上起降飞行生物/水上飞机）配合使用。

驱动部件用 `BoneNodeDef.isLocomotionDrivePart` 标记；升力部件用 `isFlightLiftPart` 标记；水下推进器官或舰船螺旋桨/喷水推进部件用 `isAquaticPropulsionPart` 标记（`Naval` 舰船在未单独标记 `isAquaticPropulsionPart` 时自动回退绑定 `isLocomotionDrivePart`）。

`aquaticMode=Amphibious` 允许同时通行陆地与合格水域（含原版不可通行的 `WaterDeep` / `WaterOceanDeep` 深水格），并分别按 `waterSpeedMultiplier`（或 `deepWaterSpeedMultiplier` / `shallowWaterSpeedMultiplier`）与 `landSpeedMultiplier` 缩放移速，可用 `landExtraPathCost` 引导优先走水路；`aquaticMode=WaterOnly` 严格限制在合格水域格内寻路、占据、漫游与落图，硬性阻断一切陆地格（除非当前处于高空飞行态，且飞行降落仅允许落回合格水面）。`canTraverseMarsh` 默认 `false`（防止深吃水舰船进入内陆沼泽），`blockedByBridges` 默认 `true`（桥面阻挡水面通行，除非设为 `false` 或启用地形破拆），`requireDeepWater=true` 时禁止进入浅水。

`worldFlightSpeedMultiplier` 大于 0.01 时用于世界飞行，否则回退到 `flightSpeedMultiplier`；`worldWaterSpeedMultiplier` 大于 0.01 时用于世界水域格航行，否则回退到 `waterSpeedMultiplier`。`maxCombatAirborneTicks=0` 表示不设持续滞空上限；起降时长和冷却使用 Tick。运动系统的其余参数、类型与默认值见第 12 节。

## 6. 机库与舰载机

挂载 `CompProperties_CarrierHangar`。当前实现使用左右两组机库和舱门：`leftHangarBoneId` / `rightHangarBoneId`、`leftShutterBoneId` / `rightShutterBoneId`，配合 `leftLaunchPixel` / `rightLaunchPixel` 与舱门角度。**没有 `launchBays` 列表字段。**

- 容量、发射与重建：`maxInterceptors`、`catapultLaunchIntervalTicks`、`rebuildDurationTicks`。
- 单位与弹体：`interceptorDef`、`beamProjectileDef`、`catapultWeaponDef`。`interceptorDef.thingClass` 必须能创建 `CarrierInterceptor`，不能随意指向普通 Pawn。
- 飞行与作战范围：`interceptorCruiseSpeed`、`engagementRange`、`maxLeashRadius`。
- 耐久与动作：`maxInterceptorHullHp`、`maxInterceptorShieldHp`、`launchAnimDefName`、`recallAnimDefName`。

手动放飞和召回使用 `CompProperties_AbilityCarrierScramble` / `CompProperties_AbilityCarrierRecall`，挂在 `AbilityDef.comps`。自动发射与重建开关是机库运行时控制状态，不能把旧手册中的 `autoScrambleInCombat`、`startingInterceptors`、`buildIntervalTicks` 当成配置字段。

## 7. 碰撞、部位损伤、痛觉与核心修复

`GiantRigDef` 提供以下可配置组：

- 空间：`hitCoverageThreshold`、`pawnExclusionCoverageThreshold`、`collisionCoverageThreshold`、`collisionPathCost`、`pawnCanopyPathCost`、`coreClearanceRadius`、`footprintUpdateIntervalTicks`。
- 交互：`enableCollisionPawnPushOut`、`enablePawnOcclusionSilhouette`、`enableGiantToGiantCollision`、`enableCorpseFootprint`、`meleeTouchRadius`、`enableDynamicMeleeContactLunge`、`maxDynamicMeleeContactLungeCells`。
- 破拆：`crushBuildingsAndTerrain`、`canCrushNaturalRock`、`canCrushConstructedBuildings`、`canCrushDestructibleTerrain`、`protectAlliedStructures`、`crushDamagePerStep`、`crushIntervalTicks`。
- 疾奔：`rushWhenEnemyFar`、`rushStartDistance`、`rushStopDistance`、`rushMoveSpeedMultiplier`。
- 部位路由：`enableLimbPartDamageRouting`、`enableBoneArmorAndWeakpoints`、`enableMultiCellExplosionRouting`；骨骼使用 `armorMultiplier`、`damageMultiplier`、`lethalWhenDestroyed` 等字段。
- 痛觉和死亡：`painImmune`、`painScaleFactor`、`painShockThresholdOverride`、`minSeveredBonesForPainShock`、`unseveredPainShockMaxHealthPct`、`onlyDieWhenCoreDestroyed`。

骨骼的 `contributesToHitbox`、`contributesToCollision`、`collisionRole` 决定三层空间参与方式：`CoreSolid`（`contributesToCollision=true`）生成核心碰撞格 `collisionCells`，仅用于躯干、盆骨与颈根，承担巨兽间实体阻挡并始终排挤重叠小型单位；`Canopy`（`contributesToHitbox=true` 且 `contributesToCollision=false`）生成视觉冠层格 `pawnExclusionCells`，用于中远段颈部、头颅、翼膜和悬空尾部，在静止或移动时排挤小型 Pawn 并在对小型目标近战时提供停步边界，而巨兽原地挥击突刺扫过目标格期间会自动暂缓非核心冠层排挤，防止咬击或挥爪瞬间将目标推离命中点；`HitboxOnly` 仅进入 `hitCells` 参与选区与受击路由。解剖关联与层级必须能正确解析。

`CompProperties_GiantBioferriteCore` 实际提供按有效核心比例修复非永久伤势的能力。`requiredBoneIds` 非空时按其完好比例计算，否则使用 `requiredBoneId`；`healPerInterval` 是每次修复预算，`intervalTicks` 是间隔（运行时至少 10 Tick）。它不是一个通用战利品掉落或死亡爆炸组件。

## 8. 能力组件与自主行为

框架提供的 `CompProperties_Ability*` 都挂在原版 `AbilityDef.comps`，还需由原版能力系统授予宿主；定义组件本身不会自动赋予能力。

- `CompProperties_AbilityRequiresGiantBone`：用 `requiredBoneIds` / `requireAll` 检查部位完好，使用 `organLabel` / `disabledReasonKey` 说明禁用原因。
- `CompProperties_AbilityAbyssalQuake`：可配置范围效果、伤害类型、部位挂点和后坐表现；`triggerAtDualWhipCrossing` / `dualWhipRequiredClipDefName` 绑定软鞭交汇时机，`muzzleBoneId` / `muzzlePixel` 定位发射口，履带相关字段需匹配实际骨架。具体效果数值由作者设定。
- 阶段与机库操作组件见前文；其余已提供的效果组件见第 10 节和附录。

自定义范围效果自行处理命中时，应核查 `AbilityDef.canUseAoeToGetTargets`，现有这类示例采用 false，以免与原版目标枚举重复执行。

需要玩家征召和排队命令时，在宿主 `ThinkTreeDef` 的 `ThinkNode_QueuedJob` 前接入：

```xml
<li Class="GiantCreatureFramework.ThinkNode_ConditionalGiantPlayerDrafted">
  <subNodes><li Class="GiantCreatureFramework.ThinkNode_GiantPlayerDrafted" /></subNodes>
</li>
<li Class="ThinkNode_QueuedJob" />
```

部位战斗走位使用 `JobGiver_GiantPartCombatPositioning`（`targetAcquireRadius`、`targetKeepRadius`）。通用能力作战节点是 `JobGiver_GiantAbilityFight`（`abilityDefs`、`maxAcquireRadius`）。这些是可接入的行为节点，不规定作者应该组合哪些能力。

## 9. 世界地图旅行

框架提供 `GCF_WorldGiant` 世界对象和 `GiantWorldUtility` 接口，承载 Pawn 与乘员。玩家出发命令由 `DepartureCommand` 提供；`TryLeaveMap` / `TryEnterMap` 用于地图转换。世界飞行资格由 `WorldObject_Giant.CanFlyOnWorld` 判断，涉及飞行配置和部件状态。

世界对象支持目的地移动、返回/进入地图，以及适用地点的交易或交互。可执行动作取决于目标地点类型、关系、地图状态和原版规则。世界速度使用飞行配置（`flightLocomotion`）或水栖配置（`aquaticLocomotion`）中的对应倍率，没有单独的 `worldTravel` 配置块；当 `aquaticMode=WaterOnly` 且未处于世界飞行时，世界航线限制在海洋/湖泊格、海岸格（`allowCoastalWorldTiles=true`）与内河格（`allowRiverWorldTiles=true`），遇到内陆半岛会自动沿水域绕行，进入海岸或水域地图时自动部署在合格水域格。

## 10. 扩展子模组接口隔离说明

主框架（`GiantCreatureFramework.dll`）仅内置通用核心组件与内置示例单位 `GCF_MechScorpion`。可选扩展子模组中的专属类仅在对应子模组程序集加载时存在，自制新巨兽时应仅使用主框架通用组件（`CompProperties_AbilityAbyssalQuake`、`CompProperties_AbilityRequiresGiantBone`、`CompProperties_GiantPhaseTransform`、`CompProperties_CarrierHangar`、`BoneNodeDef.partAttacks`、`JobGiver_GiantAbilityFight` 等），不得依赖外部子模组专属类。

## 11. 创作、调试与验收工具

工坊提供手册与工作区读写、贴图生成/处理、程序化动作、连拍预览、校验、热注入与地图生成。用 `list_manuals` 发现四本运行时手册，或读取总手册；详细参数以工具 schema 为准。

模组选项提供骨骼、碰撞格、受击格叠层，调试面板和创作终端开关。`Dialog_GiantRigInspector` 用于姿态与关节检查。`Tools/giant_arena_cli.py` 和可选 `GiantCreatureFramework.Arena.BridgeTools` 提供竞技场搭建、配置、录像与遥测；运行前用 CLI `--help` 或桥接工具 schema 查询实际参数。

## 12. 完整字段参考

以下表格从当前源码的公开配置字段提取，排除标记为 `Unsaved` 的运行时缓存。默认列是 **C# 初始值表达式**，不是可直接粘贴的 XML；例如 `new Vector2(a,b)` 在 XML 中写 `(a, b)`。零字段组件仍列出其继承关系。原版 `Def`、`CompProperties`、`CompProperties_AbilityEffect` 的继承字段按原版 API 使用。

使用表格时先看前文的挂载位置、约束和专属限制；字段存在不等于任意组合都有效。`CompProperties_GiantCrew` 的构造覆盖值见第 2 节。动画 `scale` 虽有字段，当前动画采样与校验不支持用它缩放部件。

### 骨架、动画与显示

#### `CompProperties_GiantRig`

来源：`Source/GiantCreatureFramework/Components/CompGiantRig.cs`。 继承：`CompProperties`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `rigDef` | `GiantRigDef` | `默认值（引用类型为 null）` |

#### `GiantRigDef`

来源：`Source/GiantCreatureFramework/Core/GiantRigDef.cs`。 继承：`Def`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `nodes` | `List<BoneNodeDef>` | `new List<BoneNodeDef>()` |
| `turnSpeedDegPerTick` | `float` | `4.5f` |
| `hitCoverageThreshold` | `float` | `0.08f` |
| `pawnExclusionCoverageThreshold` | `float` | `0.16f` |
| `collisionCoverageThreshold` | `float` | `0.45f` |
| `collisionPathCost` | `int` | `10000` |
| `pawnCanopyPathCost` | `int` | `1800` |
| `coreClearanceRadius` | `int` | `1` |
| `footprintUpdateIntervalTicks` | `int` | `2` |
| `walkCycleDistance` | `float` | `2.6f` |
| `runCycleDistance` | `float` | `3.2f` |
| `portraitScaleFactor` | `float` | `0.34f` |
| `useCoupledSagittalKinematics` | `bool` | `false` |
| `partAnimBodyCoupling` | `float` | `-1f` |
| `bodyCouplingRefBodySize` | `float` | `2.0f` |
| `bodyCouplingSizeExponent` | `float` | `0.85f` |
| `minAutoBodyCoupling` | `float` | `0.08f` |
| `rushWhenEnemyFar` | `bool` | `true` |
| `rushStartDistance` | `float` | `12f` |
| `rushStopDistance` | `float` | `7f` |
| `rushMoveSpeedMultiplier` | `float` | `1.65f` |
| `crushBuildingsAndTerrain` | `bool` | `true` |
| `canCrushNaturalRock` | `bool` | `true` |
| `canCrushConstructedBuildings` | `bool` | `true` |
| `canCrushDestructibleTerrain` | `bool` | `true` |
| `protectAlliedStructures` | `bool` | `true` |
| `crushDamagePerStep` | `int` | `450` |
| `crushIntervalTicks` | `int` | `12` |
| `enableCollisionPawnPushOut` | `bool` | `true` |
| `enablePawnOcclusionSilhouette` | `bool` | `true` |
| `enableGiantToGiantCollision` | `bool` | `true` |
| `enableDynamicMeleeContactLunge` | `bool` | `true` |
| `maxDynamicMeleeContactLungeCells` | `float` | `0.85f` |
| `meleeTouchRadius` | `float` | `0f` |
| `useDirectionalMeleeTools` | `bool` | `true` |
| `enableLimbPartDamageRouting` | `bool` | `true` |
| `enableBoneArmorAndWeakpoints` | `bool` | `true` |
| `enableMultiCellExplosionRouting` | `bool` | `true` |
| `enableCorpseFootprint` | `bool` | `true` |
| `painImmune` | `bool` | `false` |
| `painScaleFactor` | `float` | `1.0f` |
| `painShockThresholdOverride` | `float` | `-1f` |
| `minSeveredBonesForPainShock` | `int` | `1` |
| `unseveredPainShockMaxHealthPct` | `float` | `0.25f` |
| `onlyDieWhenCoreDestroyed` | `bool` | `false` |
| `enableMeleeCleave` | `bool` | `true` |
| `cleaveRadius` | `float` | `2.6f` |
| `cleaveArcDeg` | `float` | `110f` |
| `cleaveDamageFactor` | `float` | `0.55f` |
| `cleaveMaxTargets` | `int` | `4` |
| `enableMeleeKnockback` | `bool` | `true` |
| `meleeKnockbackDistance` | `float` | `2.2f` |
| `enableGiantVfx` | `bool` | `true` |
| `autoFootstepVfx` | `bool` | `true` |
| `vfxPaletteTheme` | `GiantVfxPaletteTheme` | `GiantVfxPaletteTheme.Auto` |
| `footstepCameraShake` | `float` | `0.04f` |
| `spawnDeathCollapseVfx` | `bool` | `true` |
| `idleAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `walkAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `runAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `turnLeftAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `turnRightAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `turnCycleAngleDeg` | `float` | `42f` |
| `turnCorneringBlendMax` | `float` | `0.42f` |
| `meleeAttackAnims` | `List<GiantAnimDef>` | `new List<GiantAnimDef>()` |
| `rangedAttackAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `abilityAnims` | `List<GiantAnimDef>` | `new List<GiantAnimDef>()` |
| `rangedWeaponBoneId` | `string` | `默认值（引用类型为 null）` |
| `weaponEmitters` | `List<GiantWeaponEmitterDef>` | `new List<GiantWeaponEmitterDef>()` |
| `downedAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `deathAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `flightEntry` | `GiantFlightEntryDef` | `默认值（引用类型为 null）` |
| `bezierChains` | `List<BezierChainDef>` | `new List<BezierChainDef>()` |
| `softWhips` | `List<SoftWhipDef>` | `new List<SoftWhipDef>()` |
| `skinnedMeshes` | `List<GiantSkinnedMeshDef>` | `new List<GiantSkinnedMeshDef>()` |
| `healthBarDef` | `GiantHealthBarDef` | `默认值（引用类型为 null）` |
| `healthBarTitleOverride` | `string` | `默认值（引用类型为 null）` |
| `shadowTexPath` | `string` | `默认值（引用类型为 null）` |
| `shadowDrawSize` | `Vector2` | `Vector2.zero` |
| `shadowAlpha` | `float` | `0.42f` |
| `shadowOffsetXZ` | `Vector2` | `new Vector2(0.36f, -0.42f)` |
| `renderGroundShadowWhenGrounded` | `bool` | `false` |
| `rollingTracks` | `List<GiantRollingTrackDef>` | `new List<GiantRollingTrackDef>()` |
| `steering` | `SteeringConfigDef` | `默认值（引用类型为 null）` |
| `vehicleLocomotion` | `VehicleLocomotionDef` | `默认值（引用类型为 null）` |
| `flightLocomotion` | `GiantFlightDef` | `默认值（引用类型为 null）` |
| `flightVehicleLocomotion` | `FlightVehicleLocomotionDef` | `默认值（引用类型为 null）` |
| `aquaticLocomotion` | `GiantAquaticDef` | `默认值（引用类型为 null）` |
| `lateralViewHalfAngleDeg` | `float` | `45f` |
| `lateralViewHysteresisDeg` | `float` | `6f` |

#### `BoneNodeDef`

来源：`Source/GiantCreatureFramework/Core/GiantRigDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `nodeId` | `string` | `默认值（引用类型为 null）` |
| `label` | `string` | `默认值（引用类型为 null）` |
| `parentId` | `string` | `默认值（引用类型为 null）` |
| `texPath` | `string` | `默认值（引用类型为 null）` |
| `drawSize` | `Vector2` | `Vector2.one` |
| `pivotUV` | `Vector2` | `new Vector2(0.5f, 0.5f)` |
| `pivotPixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `parentSocketUV` | `Vector2` | `new Vector2(0.5f, 0.5f)` |
| `parentSocketPixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `localOffset` | `Vector2` | `Vector2.zero` |
| `baseAngle` | `float` | `0f` |
| `sagittalDropCells` | `float` | `0f` |
| `isCoronalFlapHinge` | `bool` | `false` |
| `baseCoronalFlap` | `float` | `0f` |
| `baseScale` | `Vector2` | `Vector2.one` |
| `layerOffset` | `float` | `0f` |
| `textureResolution` | `int` | `512` |
| `linkedBodyPart` | `BodyPartDef` | `默认值（引用类型为 null）` |
| `linkedBodyPartCustomLabel` | `string` | `默认值（引用类型为 null）` |
| `linkedBodyPartIndex` | `int` | `-1` |
| `hideWhenMissing` | `bool` | `true` |
| `isRigid` | `bool` | `false` |
| `isTurret` | `bool` | `false` |
| `maxTurretAimAngleDeg` | `float` | `180f` |
| `turretRecoil` | `GiantTurretRecoilDef` | `默认值（引用类型为 null）` |
| `isLocomotionDrivePart` | `bool` | `false` |
| `locomotionSideSign` | `float` | `0f` |
| `isFlightLiftPart` | `bool` | `false` |
| `isAquaticPropulsionPart` | `bool` | `false` |
| `contributesToHitbox` | `bool` | `true` |
| `contributesToCollision` | `bool` | `true` |
| `collisionRole` | `BoneCollisionRole` | `BoneCollisionRole.Auto` |
| `alphaThreshold` | `byte` | `32` |
| `aimAtTargetWeight` | `float` | `0f` |
| `aimMaxAngle` | `float` | `35f` |
| `aimReferenceAngleOffset` | `float` | `0f` |
| `shaderTypeDef` | `ShaderTypeDef` | `默认值（引用类型为 null）` |
| `continuousSpinDegPerTick` | `float` | `0f` |
| `isReactorCoreBone` | `bool` | `false` |
| `lethalWhenDestroyed` | `bool` | `false` |
| `muzzleUV` | `Vector2` | `new Vector2(0.5f, 0.88f)` |
| `muzzlePixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `muzzleAngleOffset` | `float` | `0f` |
| `muzzleVfxPreset` | `GiantAnimVfxPreset` | `GiantAnimVfxPreset.MuzzleBlast` |
| `muzzleVfxScale` | `float` | `1.0f` |
| `muzzleVfxColor` | `Color` | `new Color(0.38f, 0.92f, 1f, 0.9f)` |
| `emitterSockets` | `List<GiantBoneEmitterSocketDef>` | `new List<GiantBoneEmitterSocketDef>()` |
| `partAttacks` | `List<GiantPartAttackDef>` | `new List<GiantPartAttackDef>()` |
| `glowTexPath` | `string` | `默认值（引用类型为 null）` |
| `glowColor` | `Color` | `Color.white` |
| `glowLayerOffset` | `float` | `0.05f` |
| `silhouetteTexPath` | `string` | `默认值（引用类型为 null）` |
| `silhouetteLayerOffset` | `float` | `-9.2f` |
| `hitVfxPreset` | `GiantHitVfxKind` | `GiantHitVfxKind.Auto` |
| `severVfxPreset` | `GiantSeverVfxKind` | `GiantSeverVfxKind.Auto` |
| `severVfxScale` | `float` | `1.2f` |
| `persistentVfxPreset` | `GiantBoneAuraVfxKind` | `GiantBoneAuraVfxKind.None` |
| `persistentVfxUV` | `Vector2` | `new Vector2(0.5f, 0.5f)` |
| `persistentVfxPixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `persistentVfxIntervalTicks` | `int` | `24` |
| `persistentVfxScale` | `float` | `1.0f` |
| `damageMultiplier` | `float` | `1f` |
| `armorMultiplier` | `float` | `1f` |
| `deflectChance` | `float` | `0f` |
| `sectorSourceBoneId` | `string` | `默认值（引用类型为 null）` |
| `eastVariant` | `BoneDirectionalVariantDef` | `默认值（引用类型为 null）` |
| `westVariant` | `BoneDirectionalVariantDef` | `默认值（引用类型为 null）` |
| `crewSeats` | `List<GiantBoneCrewSeatDef>` | `new List<GiantBoneCrewSeatDef>()` |

#### `BoneDirectionalVariantDef`

来源：`Source/GiantCreatureFramework/Core/GiantRigDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `texPath` | `string` | `默认值（引用类型为 null）` |
| `glowTexPath` | `string` | `默认值（引用类型为 null）` |
| `silhouetteTexPath` | `string` | `默认值（引用类型为 null）` |
| `drawSize` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `pivotUV` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `pivotPixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `parentSocketUV` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `parentSocketPixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `layerOffset` | `float` | `-9999f` |
| `muzzleUV` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `muzzlePixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `muzzleAngleOffset` | `float` | `-9999f` |
| `persistentVfxUV` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `persistentVfxPixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `emitterSockets` | `List<GiantBoneEmitterSocketDef>` | `new List<GiantBoneEmitterSocketDef>()` |

#### `GiantAnimDef`

来源：`Source/GiantCreatureFramework/Core/GiantAnimDef.cs`。 继承：`Def`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `durationTicks` | `float` | `60f` |
| `loop` | `bool` | `true` |
| `blendInTicks` | `int` | `8` |
| `triggerToolTag` | `string` | `默认值（引用类型为 null）` |
| `isolatedSubtreeRootBoneId` | `string` | `默认值（引用类型为 null）` |
| `actingSubtreeRootBoneId` | `string` | `默认值（引用类型为 null）` |
| `coupledBodyMotionFactor` | `float` | `-1f` |
| `impactTick` | `float` | `-1f` |
| `tracks` | `List<BoneAnimTrack>` | `new List<BoneAnimTrack>()` |
| `events` | `List<GiantAnimEventDef>` | `new List<GiantAnimEventDef>()` |

#### `BoneAnimTrack`

来源：`Source/GiantCreatureFramework/Core/GiantAnimDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `nodeId` | `string` | `默认值（引用类型为 null）` |
| `keyframes` | `List<BoneKeyframe>` | `new List<BoneKeyframe>()` |

#### `BoneKeyframe`

来源：`Source/GiantCreatureFramework/Core/GiantAnimDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `tick` | `float` | `0` |
| `angle` | `float` | `0f` |
| `sagittalPitch` | `float` | `0f` |
| `coronalFlap` | `float` | `0f` |
| `offset` | `Vector2` | `Vector2.zero` |
| `scale` | `Vector2` | `Vector2.one` |
| `easing` | `AnimEasing` | `AnimEasing.SmoothStep` |

#### `GiantAnimEventDef`

来源：`Source/GiantCreatureFramework/Core/GiantAnimDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `tick` | `float` | `0` |
| `boneId` | `string` | `默认值（引用类型为 null）` |
| `socketId` | `string` | `默认值（引用类型为 null）` |
| `emitterId` | `string` | `默认值（引用类型为 null）` |
| `useBoneMuzzle` | `bool` | `false` |
| `angleOffset` | `float` | `0f` |
| `uv` | `Vector2` | `new Vector2(0.5f, 0.5f)` |
| `pixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `vfxPreset` | `GiantAnimVfxPreset` | `GiantAnimVfxPreset.None` |
| `fleckDef` | `FleckDef` | `默认值（引用类型为 null）` |
| `moteDef` | `ThingDef` | `默认值（引用类型为 null）` |
| `effecterDef` | `EffecterDef` | `默认值（引用类型为 null）` |
| `soundDef` | `SoundDef` | `默认值（引用类型为 null）` |
| `scale` | `float` | `1f` |
| `color` | `Color` | `Color.white` |
| `cameraShake` | `float` | `0f` |

#### `GiantFlightEntryDef`

来源：`Source/GiantCreatureFramework/Core/GiantRigDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `enabled` | `bool` | `true` |
| `flightAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `durationTicks` | `int` | `150` |
| `startOffsetCells` | `float` | `18f` |
| `startAltitudeOffset` | `float` | `6.5f` |
| `wingFoldTransitionTicks` | `int` | `32` |
| `landingDustScale` | `float` | `3.2f` |
| `landingCameraShake` | `float` | `0.35f` |
| `landingSoundDef` | `SoundDef` | `默认值（引用类型为 null）` |

#### `GiantBoneCrewSeatDef`

来源：`Source/GiantCreatureFramework/Core/GiantRigDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `seatId` | `string` | `默认值（引用类型为 null）` |
| `mountUV` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `mountPixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `localOffset` | `Vector2` | `Vector2.zero` |
| `screenOffset` | `Vector2` | `Vector2.zero` |
| `facingOffsetDeg` | `float` | `0f` |
| `layerOffset` | `float` | `8.0f` |
| `drawScale` | `float` | `1.0f` |
| `renderOccupant` | `bool` | `true` |
| `renderAboveAllParts` | `bool` | `true` |
| `rotateSpriteWithBone` | `bool` | `false` |
| `renderOccupantWeapon` | `bool` | `false` |

#### `GiantHealthBarDef`

来源：`Source/GiantCreatureFramework/Core/GiantHealthBarDef.cs`。 继承：`Def`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `topScreenMargin` | `float` | `16f` |
| `barWidth` | `float` | `540f` |
| `mainBarHeight` | `float` | `26f` |
| `subRowHeight` | `float` | `24f` |
| `subMeterWidth` | `float` | `156f` |
| `stackSpacing` | `float` | `10f` |
| `maxVisibleSubRows` | `int` | `12` |
| `enterExpandSeconds` | `float` | `0.48f` |
| `exitCollapseSeconds` | `float` | `0.40f` |
| `drawerToggleSpeed` | `float` | `12f` |
| `damageLagDecayPerSecond` | `float` | `0.55f` |
| `defaultExpanded` | `bool` | `false` |
| `showExpandButton` | `bool` | `true` |
| `clickBarToSelectAndJump` | `bool` | `true` |
| `hideFullHealthMinorLimbs` | `bool` | `false` |
| `drawHierarchyLines` | `bool` | `true` |
| `backgroundColor` | `Color` | `new Color(0.086f, 0.098f, 0.118f, 0.92f)` |
| `drawerBackgroundColor` | `Color` | `new Color(0.078f, 0.090f, 0.110f, 0.90f)` |
| `borderColor` | `Color` | `new Color(0.32f, 0.35f, 0.40f, 1.00f)` |
| `lagBarColor` | `Color` | `new Color(0.85f, 0.76f, 0.52f, 0.95f)` |
| `healthyFillColor` | `Color` | `new Color(0.33f, 0.61f, 0.35f, 1.00f)` |
| `woundedFillColor` | `Color` | `new Color(0.78f, 0.57f, 0.23f, 1.00f)` |
| `criticalFillColor` | `Color` | `new Color(0.74f, 0.27f, 0.25f, 1.00f)` |
| `textColor` | `Color` | `new Color(0.96f, 0.97f, 0.98f, 1.00f)` |
| `subtitleKey` | `string` | `默认值（引用类型为 null）` |
| `subtitleText` | `string` | `默认值（引用类型为 null）` |
| `barFillTexPath` | `string` | `默认值（引用类型为 null）` |
| `barFillActiveTexPath` | `string` | `默认值（引用类型为 null）` |
| `barFrameTexPath` | `string` | `默认值（引用类型为 null）` |
| `activeBorderColor` | `Color` | `Color.clear` |
| `activeTextColor` | `Color` | `Color.clear` |
| `leftOrnamentTexPath` | `string` | `默认值（引用类型为 null）` |
| `rightOrnamentTexPath` | `string` | `默认值（引用类型为 null）` |
| `leftOrnamentActiveTexPath` | `string` | `默认值（引用类型为 null）` |
| `rightOrnamentActiveTexPath` | `string` | `默认值（引用类型为 null）` |
| `leftOrnamentGlowTexPath` | `string` | `默认值（引用类型为 null）` |
| `rightOrnamentGlowTexPath` | `string` | `默认值（引用类型为 null）` |
| `ornamentSize` | `Vector2` | `Vector2.zero` |
| `ornamentOverlapX` | `float` | `12f` |
| `ornamentOffsetY` | `float` | `0f` |
| `centerCrestTexPath` | `string` | `默认值（引用类型为 null）` |
| `centerCrestActiveTexPath` | `string` | `默认值（引用类型为 null）` |
| `centerCrestGlowTexPath` | `string` | `默认值（引用类型为 null）` |
| `centerCrestSize` | `Vector2` | `Vector2.zero` |
| `centerCrestOffsetY` | `float` | `0f` |
| `showSecondaryStateBar` | `bool` | `false` |
| `secondaryBarHeight` | `float` | `6f` |
| `secondaryBarBgColor` | `Color` | `new Color(0.050f, 0.055f, 0.072f, 0.94f)` |
| `secondaryBarFillColor` | `Color` | `new Color(0.92f, 0.18f, 0.26f, 1.00f)` |

**自定义 Boss 血条 C# 扩展接口**（来源：`Source/GiantCreatureFramework/Core/GiantSubmodExtensions.cs`）：
- `IGiantBossHudStateProvider`：由巨兽身上的 `ThingComp` 实现，每帧填充 `GiantBossHudTelemetry`（通常态→活性态融合权重 `ActiveBlend01`、辉光呼吸 `GlowPulse01`、超载态 `IsOverdrive`、副能量/阶段/炸膛阈值条 `SecondaryFill01` / `SecondaryThresholdMarker01`、状态文本 `StatusReadout` 与可破坏器官徽章 `PartBadges`）。
- `IGiantBossHudOverlayDrawer`：由 `ThingComp` 实现，在主血条槽上叠加生物专属程序化特效（如超音速马赫尖章、龙气电弧、吸入涡流线）。
- `IGiantCustomBossBarRenderer` + `GiantCustomBossBarRegistry.Register(...)`：供子模组完全接管整条 Boss 血条的布局与绘制，保持主框架零具体生物耦合。

#### `GiantSkinnedMeshDef`

来源：`Source/GiantCreatureFramework/Core/GiantSkinnedMeshDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `meshId` | `string` | `默认值（引用类型为 null）` |
| `texPath` | `string` | `默认值（引用类型为 null）` |
| `silhouetteTexPath` | `string` | `默认值（引用类型为 null）` |
| `glowTexPath` | `string` | `默认值（引用类型为 null）` |
| `glowColor` | `Color` | `Color.white` |
| `shaderTypeDef` | `ShaderTypeDef` | `默认值（引用类型为 null）` |
| `anchorBoneId` | `string` | `默认值（引用类型为 null）` |
| `drawSize` | `Vector2` | `Vector2.one` |
| `worldMinXZ` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `worldMaxXZ` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `pivotUV` | `Vector2` | `new Vector2(0.5f, 0.5f)` |
| `parentSocketUV` | `Vector2` | `new Vector2(0.5f, 0.5f)` |
| `pivotPixel` | `Vector2` | `new Vector2(-1f, -1f)` |
| `parentSocketPixel` | `Vector2` | `new Vector2(-1f, -1f)` |
| `baseAngle` | `float` | `0f` |
| `layerOffset` | `float` | `0f` |
| `silhouetteLayerOffset` | `float` | `-9.2f` |
| `glowLayerOffset` | `float` | `0.05f` |
| `textureResolution` | `int` | `512` |
| `alphaThreshold` | `byte` | `16` |
| `subdivisionsX` | `int` | `20` |
| `subdivisionsY` | `int` | `20` |
| `weightSharpness` | `float` | `3.0f` |
| `maxInfluences` | `int` | `4` |
| `cullSeveredBoneTriangles` | `bool` | `true` |
| `linkedBodyPart` | `BodyPartDef` | `默认值（引用类型为 null）` |
| `linkedBodyPartCustomLabel` | `string` | `默认值（引用类型为 null）` |
| `linkedBodyPartIndex` | `int` | `-1` |
| `hideWhenMissing` | `bool` | `true` |
| `boneIds` | `List<string>` | `new List<string>()` |
| `memberBoneIds` | `List<string>` | `new List<string>()` |
| `suppressMemberBoneSprites` | `bool` | `true` |
| `capsules` | `List<GiantSkinCapsuleDef>` | `new List<GiantSkinCapsuleDef>()` |
| `rigidPinBoneId` | `string` | `默认值（引用类型为 null）` |
| `pinToSkinnedMeshId` | `string` | `默认值（引用类型为 null）` |

#### `GiantSkinCapsuleDef`

来源：`Source/GiantCreatureFramework/Core/GiantSkinnedMeshDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `boneId` | `string` | `默认值（引用类型为 null）` |
| `groupId` | `string` | `默认值（引用类型为 null）` |
| `startXZ` | `Vector2` | `默认值（引用类型为 null）` |
| `endXZ` | `Vector2` | `默认值（引用类型为 null）` |
| `radius` | `float` | `0.40f` |
| `weightMultiplier` | `float` | `1.0f` |

#### `BezierChainDef`

来源：`Source/GiantCreatureFramework/Core/DampedBezierChainSolver.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `chainId` | `string` | `默认值（引用类型为 null）` |
| `boneIds` | `List<string>` | `new List<string>()` |
| `tipUV` | `Vector2` | `new Vector2(0.5f, 0.88f)` |
| `p1Frequency` | `float` | `0.28f` |
| `p1Damping` | `float` | `0.58f` |
| `p2Frequency` | `float` | `0.16f` |
| `p2Damping` | `float` | `0.36f` |
| `p3Frequency` | `float` | `0.095f` |
| `p3Damping` | `float` | `0.25f` |
| `whipCrackGain` | `float` | `0.42f` |
| `waveLagCoupling` | `float` | `0.45f` |
| `maxJointBendDeg` | `float` | `58f` |
| `idleWaveAmplitudeDeg` | `float` | `0f` |
| `idleWavePeriodTicks` | `float` | `60f` |
| `velocityTrailingGain` | `float` | `0f` |
| `trailingMaxOffsetCells` | `float` | `7.5f` |
| `trailingLateralSign` | `float` | `0f` |
| `trailingLateralRatio` | `float` | `0.22f` |
| `moveSwayAmplitudeCells` | `float` | `0f` |
| `moveSwayPeriodTicks` | `float` | `54f` |
| `moveRefSpeedCellsPerTick` | `float` | `0.05f` |
| `strikeAnimDefs` | `List<string>` | `new List<string>()` |
| `strikeWindupEndNorm` | `float` | `0.26f` |
| `strikeSnapEndNorm` | `float` | `0.62f` |
| `strikeFreqBoost` | `float` | `2.75f` |
| `defaultStrikeRangeFactor` | `float` | `0.82f` |
| `defaultStrikeSideCells` | `float` | `0f` |

#### `SoftWhipDef`

来源：`Source/GiantCreatureFramework/Core/SoftBodyWhipSolver.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `whipId` | `string` | `默认值（引用类型为 null）` |
| `texPath` | `string` | `默认值（引用类型为 null）` |
| `rootBoneId` | `string` | `默认值（引用类型为 null）` |
| `exitUV` | `Vector2` | `new Vector2(0.5f, 0.78125f)` |
| `exitPixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `tendrilBodyPart` | `BodyPartDef` | `默认值（引用类型为 null）` |
| `tendrilBodyPartCustomLabel` | `string` | `默认值（引用类型为 null）` |
| `torsoBoneId` | `string` | `"abyssal_thorax"` |
| `colliderCenterLocal` | `Vector2` | `new Vector2(0f, -0.40f)` |
| `colliderRadii` | `Vector2` | `new Vector2(2.05f, 3.45f)` |
| `particleCount` | `int` | `29` |
| `totalLength` | `float` | `14.3f` |
| `baseWidth` | `float` | `1.15f` |
| `tipWidthFactor` | `float` | `0.84f` |
| `harpoonWidthBoost` | `float` | `0.34f` |
| `layerOffset` | `float` | `0.76f` |
| `substeps` | `int` | `6` |
| `constraintIterations` | `int` | `3` |
| `massTaper` | `float` | `0.80f` |
| `minEndMass` | `float` | `0.05f` |
| `lateralSideSign` | `float` | `-1f` |
| `restNormalDrag` | `float` | `0.16f` |
| `fastNormalDrag` | `float` | `0.025f` |
| `postSnapNormalDrag` | `float` | `0.23f` |
| `tangentialDrag` | `float` | `0.014f` |
| `strikeDragSpeedRef` | `float` | `0.40f` |
| `restProximalCutoff` | `float` | `2.20f` |
| `restProximalGain` | `float` | `0.75f` |
| `strikeProximalCutoff` | `float` | `1.42f` |
| `strikeProximalGain` | `float` | `2.80f` |
| `strikeAnimDefs` | `List<string>` | `new List<string>()` |
| `windupEndNorm` | `float` | `0.27f` |
| `snapEndNorm` | `float` | `0.45f` |
| `checkBackEndNorm` | `float` | `0.58f` |
| `defaultStrikeAzimuthDeg` | `float` | `18f` |
| `defaultStrikeRangeFactor` | `float` | `0.78f` |
| `contributesToHitbox` | `bool` | `true` |
| `contributesToCollision` | `bool` | `false` |
| `alphaThreshold` | `byte` | `32` |

#### `GiantRollingTrackDef`

来源：`Source/GiantCreatureFramework/Core/GiantRollingTrackDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `trackId` | `string` | `默认值（引用类型为 null）` |
| `parentBoneId` | `string` | `默认值（引用类型为 null）` |
| `sideSign` | `float` | `1f` |
| `gaugeHalfWidthCells` | `float` | `1.92f` |
| `dorsalRibbonTexPath` | `string` | `默认值（引用类型为 null）` |
| `lateralRibbonTexPath` | `string` | `默认值（引用类型为 null）` |
| `tileLengthCells` | `float` | `2.60f` |
| `dorsalStartPixel` | `Vector2` | `new Vector2(256f, 480f)` |
| `dorsalEndPixel` | `Vector2` | `new Vector2(256f, 32f)` |
| `dorsalWidthCells` | `float` | `0.64f` |
| `dorsalLayerOffset` | `float` | `-1.05f` |
| `nearLoopPixels` | `List<Vector2>` | `new List<Vector2>()` |
| `nearWidthCells` | `float` | `0.30f` |
| `nearLayerOffset` | `float` | `1.45f` |
| `farStartPixel` | `Vector2` | `new Vector2(256f, 470f)` |
| `farEndPixel` | `Vector2` | `new Vector2(256f, 42f)` |
| `farWidthCells` | `float` | `0.24f` |
| `farLayerOffset` | `float` | `-1.08f` |
| `wheels` | `List<GiantTrackWheelDef>` | `new List<GiantTrackWheelDef>()` |

#### `GiantTrackWheelDef`

来源：`Source/GiantCreatureFramework/Core/GiantRollingTrackDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `wheelId` | `string` | `默认值（引用类型为 null）` |
| `texPath` | `string` | `默认值（引用类型为 null）` |
| `centerPixel` | `Vector2` | `new Vector2(256f, 256f)` |
| `diameterCells` | `float` | `0.72f` |
| `layerOffset` | `float` | `1.10f` |

#### `GiantTurretRecoilDef`

来源：`Source/GiantCreatureFramework/Core/GiantRigDef.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `distanceCells` | `float` | `0.28f` |
| `kickTicks` | `int` | `2` |
| `recoveryTicks` | `int` | `12` |
| `maxAccumulatedCells` | `float` | `0f` |
| `recoilBoneId` | `string` | `默认值（引用类型为 null）` |

#### `SteeringConfigDef`

来源：`Source/GiantCreatureFramework/Core/MultiPartSteeringSolver.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `enabled` | `bool` | `true` |
| `intensityScale` | `float` | `1.0f` |
| `enableNeckIdleAndGaitArticulation` | `bool` | `true` |
| `bodyTurnAccelDegPerTick2` | `float` | `0.40f` |
| `bodyPathLookAheadCells` | `int` | `3` |
| `bodyPathLookAheadWeight` | `float` | `0.35f` |
| `gazePathLookAheadCells` | `int` | `6` |
| `headLeadGateAngleDeg` | `float` | `45f` |
| `minBodyTurnScaleWhileHeadLeads` | `float` | `0.42f` |
| `lookChains` | `List<SteeringLookChainDef>` | `new List<SteeringLookChainDef>()` |
| `lagBones` | `List<SteeringLagBoneDef>` | `new List<SteeringLagBoneDef>()` |
| `yawBankBones` | `List<SteeringYawBankDef>` | `new List<SteeringYawBankDef>()` |
| `vehicleLocomotion` | `VehicleLocomotionDef` | `默认值（引用类型为 null）` |
| `flightLocomotion` | `GiantFlightDef` | `默认值（引用类型为 null）` |
| `flightVehicleLocomotion` | `FlightVehicleLocomotionDef` | `默认值（引用类型为 null）` |
| `aquaticLocomotion` | `GiantAquaticDef` | `默认值（引用类型为 null）` |

#### `SteeringLookChainDef`

来源：`Source/GiantCreatureFramework/Core/MultiPartSteeringSolver.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `chainId` | `string` | `默认值（引用类型为 null）` |
| `boneIds` | `List<string>` | `new List<string>()` |
| `weights` | `List<float>` | `new List<float>()` |
| `maxChainAngleDeg` | `float` | `68f` |
| `maxBoneAngleDeg` | `float` | `28f` |
| `turnSpeedDegPerTick` | `float` | `5.6f` |
| `responsiveness` | `float` | `0.24f` |
| `referenceAngleOffsetDeg` | `float` | `0f` |
| `full360Traverse` | `bool` | `false` |
| `combatTargetWeight` | `float` | `1.0f` |
| `pathLookAheadWeight` | `float` | `0.92f` |
| `idleScanAmplitudeDeg` | `float` | `0f` |
| `idleScanPeriodTicks` | `float` | `180f` |
| `locoWaveAmplitudeDeg` | `float` | `0f` |
| `locoWavePeriodTicks` | `float` | `60f` |
| `wavePhaseStepRad` | `float` | `1.10f` |
| `oneShotClipSwayScale` | `float` | `0f` |
| `oneShotClipAimScale` | `float` | `1.0f` |
| `parentSwayCompensation` | `float` | `0f` |
| `proximalSpeedScale` | `float` | `0.88f` |
| `distalSpeedScale` | `float` | `1.15f` |
| `proximalResponsivenessScale` | `float` | `1.0f` |
| `distalResponsivenessScale` | `float` | `1.0f` |
| `distalErrorCompensation` | `float` | `0f` |
| `turnSagittalDipDeg` | `float` | `0f` |
| `rushSagittalExtendDeg` | `float` | `0f` |
| `idlePoiScanMaxAngleDeg` | `float` | `0f` |
| `idlePoiHoldMinTicks` | `int` | `150` |
| `idlePoiHoldMaxTicks` | `int` | `270` |
| `idlePoiGlanceMinTicks` | `int` | `45` |
| `idlePoiGlanceMaxTicks` | `int` | `85` |

#### `SteeringLagBoneDef`

来源：`Source/GiantCreatureFramework/Core/MultiPartSteeringSolver.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `boneId` | `string` | `默认值（引用类型为 null）` |
| `followSpeedDegPerTick` | `float` | `1.60f` |
| `followResponsiveness` | `float` | `0.12f` |
| `forwardMotionAlignGain` | `float` | `2.2f` |
| `maxBendAngleDeg` | `float` | `25f` |

#### `SteeringYawBankDef`

来源：`Source/GiantCreatureFramework/Core/MultiPartSteeringSolver.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `boneId` | `string` | `默认值（引用类型为 null）` |
| `yawRateGain` | `float` | `-2.2f` |
| `maxAngleDeg` | `float` | `10f` |
| `smoothing` | `float` | `0.22f` |

### 通用控制、战斗与运动

#### `CompProperties_GiantCockpit`

来源：`Source/GiantCreatureFramework/Vehicle/CompGiantCockpit.cs`。 继承：`CompProperties`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `autonomousWhenEmpty` | `bool` | `true` |
| `allowHostileAutonomousFallback` | `bool` | `true` |
| `allowPlayerControlWithoutOverseer` | `bool` | `true` |
| `requireOverseerWhenBiotechActive` | `bool` | `false` |
| `requireDedicatedGunner` | `bool` | `false` |
| `requireDedicatedCommanderForAbilities` | `bool` | `false` |
| `autonomousCoreBodyPart` | `BodyPartDef` | `默认值（引用类型为 null）` |
| `treatHostAsVehicle` | `bool` | `true` |
| `renderCrewOnRig` | `bool` | `false` |
| `autonomousMoveSpeedFactor` | `float` | `0.85f` |
| `autonomousAccuracyFactor` | `float` | `0.88f` |
| `autonomousReloadFactor` | `float` | `0.88f` |
| `pilotedMinMoveSpeedFactor` | `float` | `0.85f` |
| `pilotedMaxMoveSpeedFactor` | `float` | `1.30f` |
| `pilotedMinAccuracyFactor` | `float` | `0.85f` |
| `pilotedMaxAccuracyFactor` | `float` | `1.36f` |
| `pilotedMinReloadFactor` | `float` | `0.85f` |
| `pilotedMaxReloadFactor` | `float` | `1.34f` |
| `commanderFireControlBonus` | `float` | `0.08f` |
| `enterTicks` | `int` | `55` |
| `seats` | `List<GiantCockpitSeatDef>` | `new List<GiantCockpitSeatDef>()` |

#### `CompProperties_GiantCrew`

来源：`Source/GiantCreatureFramework/Vehicle/CompGiantCockpit.cs`。 继承：`CompProperties_GiantCockpit`。

无新增公开配置字段；使用上文说明的继承字段或宿主能力。

#### `GiantCockpitSeatDef`

来源：`Source/GiantCreatureFramework/Vehicle/CompGiantCockpit.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `seatId` | `string` | `"MainCockpit"` |
| `role` | `GiantCockpitRole` | `GiantCockpitRole.PrimaryPilot` |
| `labelKey` | `string` | `默认值（引用类型为 null）` |
| `partLabelKey` | `string` | `默认值（引用类型为 null）` |
| `linkedBodyPart` | `BodyPartDef` | `默认值（引用类型为 null）` |
| `linkedBodyPartCustomLabel` | `string` | `默认值（引用类型为 null）` |
| `linkedBodyPartIndex` | `int` | `-1` |
| `bleedthroughFactor` | `float` | `0.08f` |
| `violentEjectStunTicks` | `int` | `180` |
| `violentEjectBluntDamage` | `float` | `6f` |
| `comfortLevel` | `float` | `0.72f` |
| `renderOccupant` | `bool` | `true` |
| `rigSeatId` | `string` | `默认值（引用类型为 null）` |

#### `CompProperties_GiantPhaseTransform`

来源：`Source/GiantCreatureFramework/Components/CompGiantPhaseTransform.cs`。 继承：`CompProperties`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `stages` | `List<GiantPhaseStageDef>` | `new List<GiantPhaseStageDef>()` |
| `triggerSummaryHealthThreshold` | `float` | `0.04f` |
| `minStageDwellTicks` | `int` | `240` |
| `finalMeltdownRadius` | `float` | `8.5f` |
| `finalMeltdownDamage` | `int` | `65` |

#### `GiantPhaseStageDef`

来源：`Source/GiantCreatureFramework/Components/CompGiantPhaseTransform.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `stageId` | `string` | `默认值（引用类型为 null）` |
| `stageLabelKey` | `string` | `默认值（引用类型为 null）` |
| `rigDef` | `GiantRigDef` | `默认值（引用类型为 null）` |
| `morphOutToNextClip` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `morphInFromPrevClip` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `morphOutToPrevClip` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `morphInFromNextClip` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `bodySizeFactor` | `float` | `1f` |
| `healthScaleFactor` | `float` | `1f` |
| `moveSpeedOffset` | `float` | `0f` |
| `meleeDamageFactor` | `float` | `1f` |
| `armorSharpOffset` | `float` | `0f` |
| `armorBluntOffset` | `float` | `0f` |
| `armorHeatOffset` | `float` | `0f` |
| `reactorGlowColor` | `Color` | `new Color(0.22f, 0.88f, 1f, 1f)` |
| `baseGlowIntensity` | `float` | `1.15f` |
| `stageAbilities` | `List<AbilityDef>` | `new List<AbilityDef>()` |
| `rangedWeaponDef` | `ThingDef` | `默认值（引用类型为 null）` |
| `transitionBlastRadius` | `float` | `6.2f` |
| `transitionEmpDamage` | `int` | `40` |
| `transitionBlastDamage` | `int` | `26` |

#### `GiantPartAttackDef`

来源：`Source/GiantCreatureFramework/Combat/GiantPartCombatSystem.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `attackId` | `string` | `默认值（引用类型为 null）` |
| `label` | `string` | `默认值（引用类型为 null）` |
| `kind` | `GiantPartAttackKind` | `GiantPartAttackKind.Melee` |
| `warmupTime` | `float` | `0.45f` |
| `cooldownTime` | `float` | `2.2f` |
| `initialPhaseOffset01` | `float` | `0f` |
| `minRange` | `float` | `0f` |
| `range` | `float` | `3.6f` |
| `arcReference` | `GiantPartArcReference` | `GiantPartArcReference.HullHeading` |
| `sectorCenterDeg` | `float` | `0f` |
| `sectorHalfAngleDeg` | `float` | `180f` |
| `requireLineOfSight` | `bool` | `true` |
| `socketId` | `string` | `默认值（引用类型为 null）` |
| `rangeOriginBoneId` | `string` | `默认值（引用类型为 null）` |
| `strikeUV` | `Vector2` | `new Vector2(0.5f, 0.85f)` |
| `strikePixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `useSoftWhipTip` | `bool` | `false` |
| `independentOverlay` | `bool` | `false` |
| `projectileDef` | `ThingDef` | `默认值（引用类型为 null）` |
| `burstShotCount` | `int` | `1` |
| `ticksBetweenBurstShots` | `int` | `8` |
| `forcedMissRadius` | `float` | `0f` |
| `accuracyTouch` | `float` | `0.88f` |
| `accuracyShort` | `float` | `0.84f` |
| `accuracyMedium` | `float` | `0.76f` |
| `accuracyLong` | `float` | `0.66f` |
| `spawnMuzzleVfx` | `bool` | `true` |
| `vfxPresetOverride` | `GiantAnimVfxPreset` | `GiantAnimVfxPreset.None` |
| `vfxScale` | `float` | `1.0f` |
| `vfxColor` | `Color` | `Color.clear` |
| `recoilMultiplier` | `float` | `1.0f` |
| `damageDef` | `DamageDef` | `默认值（引用类型为 null）` |
| `power` | `float` | `30f` |
| `armorPenetration` | `float` | `0.45f` |
| `cleaveRadius` | `float` | `0f` |
| `cleaveDamageFactor` | `float` | `0.45f` |
| `knockbackDistance` | `float` | `0f` |
| `stunTicks` | `int` | `0` |
| `selectionWeight` | `float` | `1.0f` |
| `attackAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `soundCast` | `SoundDef` | `默认值（引用类型为 null）` |
| `soundCastTail` | `SoundDef` | `默认值（引用类型为 null）` |
| `soundHitPawn` | `SoundDef` | `默认值（引用类型为 null）` |
| `soundHitBuilding` | `SoundDef` | `默认值（引用类型为 null）` |
| `soundMiss` | `SoundDef` | `默认值（引用类型为 null）` |
| `requiresPrimaryWeaponOperator` | `bool` | `true` |
| `canStrikeGroundWhileFlying` | `bool` | `false` |

#### `GiantWeaponEmitterDef`

来源：`Source/GiantCreatureFramework/Combat/GiantMuzzleSystem.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `emitterId` | `string` | `默认值（引用类型为 null）` |
| `weaponDefName` | `string` | `默认值（引用类型为 null）` |
| `abilityDefName` | `string` | `默认值（引用类型为 null）` |
| `verbTag` | `string` | `默认值（引用类型为 null）` |
| `fireMode` | `GiantEmitterFireMode` | `GiantEmitterFireMode.Alternating` |
| `requireAllBonesIntact` | `bool` | `false` |
| `replaceVanillaMuzzleFlash` | `bool` | `true` |
| `spawnMuzzleVfxOnLaunch` | `bool` | `true` |
| `vfxPresetOverride` | `GiantAnimVfxPreset` | `GiantAnimVfxPreset.None` |
| `vfxScale` | `float` | `1.0f` |
| `vfxColor` | `Color` | `Color.clear` |
| `barrels` | `List<GiantEmitterBarrelDef>` | `new List<GiantEmitterBarrelDef>()` |

#### `GiantEmitterBarrelDef`

来源：`Source/GiantCreatureFramework/Combat/GiantMuzzleSystem.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `boneId` | `string` | `默认值（引用类型为 null）` |
| `socketId` | `string` | `默认值（引用类型为 null）` |
| `uvOverride` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `pixelOverride` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `angleOffsetOverride` | `float` | `-9999f` |
| `vfxScaleMultiplier` | `float` | `1.0f` |

#### `GiantBoneEmitterSocketDef`

来源：`Source/GiantCreatureFramework/Combat/GiantMuzzleSystem.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `socketId` | `string` | `默认值（引用类型为 null）` |
| `uv` | `Vector2` | `new Vector2(0.5f, 0.88f)` |
| `pixel` | `Vector2` | `new Vector2(-9999f, -9999f)` |
| `angleOffset` | `float` | `0f` |
| `vfxPreset` | `GiantAnimVfxPreset` | `GiantAnimVfxPreset.MuzzleBlast` |
| `vfxScale` | `float` | `1.0f` |
| `vfxColor` | `Color` | `new Color(0.38f, 0.92f, 1f, 0.9f)` |

#### `VehicleLocomotionDef`

来源：`Source/GiantCreatureFramework/Vehicle/VehicleLocomotionSystem.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `chassisType` | `VehicleChassisType` | `VehicleChassisType.None` |
| `allowPivotTurnInPlace` | `bool` | `true` |
| `pivotTurnEnterAngleDeg` | `float` | `32f` |
| `pivotTurnExitAngleDeg` | `float` | `8f` |
| `pivotTurnRateDegPerTick` | `float` | `1.8f` |
| `movingTurnRateDegPerTick` | `float` | `1.1f` |
| `turnSpeedMinFactor` | `float` | `0.55f` |
| `minTurnRadiusCells` | `float` | `3.8f` |
| `maxSlipAngleDeg` | `float` | `18f` |
| `accelerationCellsPerSec2` | `float` | `1.9f` |
| `brakingCellsPerSec2` | `float` | `4.6f` |
| `handbrakeCellsPerSec2` | `float` | `9.5f` |
| `gearShiftPauseTicks` | `int` | `10` |
| `allowReverse` | `bool` | `true` |
| `reverseMinHeadingErrorDeg` | `float` | `115f` |
| `reverseSpeedMultiplier` | `float` | `0.40f` |
| `maxReverseDistanceCells` | `float` | `8.5f` |
| `lookaheadMinCells` | `float` | `2.0f` |
| `lookaheadMaxCells` | `float` | `3.6f` |
| `lockHullHeadingWhenParked` | `bool` | `true` |
| `allowHullTraverseForFixedWeapons` | `bool` | `false` |
| `stabilizeTurretsAgainstHullTurn` | `bool` | `true` |
| `movingFireAccuracyFactor` | `float` | `0.78f` |
| `requireHaltToFireWhenSingleCrew` | `bool` | `false` |
| `autoBindRollingTrackParentBones` | `bool` | `true` |
| `immobilizeWhenAnySideDestroyed` | `bool` | `true` |
| `damagedDriveMinSpeedFactor` | `float` | `0.35f` |
| `suppressBiologicalGait` | `bool` | `true` |
| `accelSquatOffset` | `float` | `0f` |
| `brakeDiveOffset` | `float` | `0f` |

#### `GiantFlightDef`

来源：`Source/GiantCreatureFramework/Flight/GiantFlightSystem.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `flightMode` | `GiantFlightMode` | `GiantFlightMode.None` |
| `altitudeTier` | `FlightAltitudeTier` | `FlightAltitudeTier.HighAirborne` |
| `flightSpeedMultiplier` | `float` | `1.0f` |
| `worldFlightSpeedMultiplier` | `float` | `0f` |
| `takeoffDurationTicks` | `int` | `35` |
| `landingDurationTicks` | `int` | `40` |
| `autoTakeoffInCombat` | `bool` | `true` |
| `autoTakeoffMinDistanceCells` | `float` | `10f` |
| `autoTakeoffWhenPathBlocked` | `bool` | `true` |
| `autoLandInCombat` | `bool` | `true` |
| `autoLandMaxDistanceCells` | `float` | `14.5f` |
| `minAirborneTicksBeforeAutoLand` | `int` | `48` |
| `maxCombatAirborneTicks` | `int` | `0` |
| `postLandTakeoffCooldownTicks` | `int` | `210` |
| `minLiftEfficiencyToFly` | `float` | `0.40f` |
| `crashOnZeroLift` | `bool` | `false` |
| `damagedLiftMinSpeedFactor` | `float` | `0.45f` |
| `forcedLandingStunTicks` | `int` | `180` |
| `takeoffWindblastRadius` | `float` | `0f` |
| `takeoffWindblastDamage` | `int` | `0` |
| `takeoffWindblastStunTicks` | `int` | `0` |
| `landingImpactRadius` | `float` | `0f` |
| `landingImpactDamage` | `int` | `0` |
| `landingImpactStunTicks` | `int` | `0` |
| `canFlyOverWalls` | `bool` | `true` |
| `canFlyOverWaterAndLava` | `bool` | `true` |
| `blockedByThickRoof` | `bool` | `true` |
| `canHoverOverImpassable` | `bool` | `true` |
| `immuneToGroundMelee` | `bool` | `true` |
| `allowAirToAirMelee` | `bool` | `true` |
| `altitudeVisualOffsetZ` | `float` | `1.50f` |
| `renderLayerAltitudeBoost` | `float` | `3.2f` |
| `dropShadowScale` | `float` | `0.80f` |
| `dropShadowAlpha` | `float` | `0.40f` |
| `dropShadowOffsetXZ` | `Vector2` | `new Vector2(0.42f, -0.45f)` |
| `hoverBobAmplitude` | `float` | `0.08f` |
| `hoverBobPeriodTicks` | `float` | `120f` |
| `bankingRollMaxAngleDeg` | `float` | `6.0f` |
| `flightLocomotionAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `takeoffAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `landingAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |

#### `FlightVehicleLocomotionDef`

来源：`Source/GiantCreatureFramework/Flight/FlightVehicleLocomotionSystem.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `airframeType` | `FlightVehicleAirframeType` | `FlightVehicleAirframeType.None` |
| `minAirspeedFraction` | `float` | `0.42f` |
| `loiterSpeedFraction` | `float` | `0.56f` |
| `loiterOrbitRadiusCells` | `float` | `6.5f` |
| `orbitClockwise` | `bool` | `true` |
| `canHoverInPlace` | `bool` | `true` |
| `hoverTransitionSpeedFraction` | `float` | `0.24f` |
| `hoverYawRateDegPerTick` | `float` | `1.65f` |
| `hoverYawEnterAngleDeg` | `float` | `28f` |
| `hoverYawExitAngleDeg` | `float` | `7f` |
| `maxBankAngleDeg` | `float` | `28f` |
| `maxRollRateDegPerTick` | `float` | `1.45f` |
| `maxTurnRateDegPerTick` | `float` | `2.10f` |
| `minTurnRadiusCells` | `float` | `5.2f` |
| `effectiveGravityCellsPerSec2` | `float` | `11.5f` |
| `inducedTurnDragFactor` | `float` | `0.22f` |
| `throttleSpoolRatePerSec` | `float` | `1.65f` |
| `thrustAccelCellsPerSec2` | `float` | `2.40f` |
| `airbrakeDecelCellsPerSec2` | `float` | `3.80f` |
| `flareStartDistanceScale` | `float` | `1.25f` |
| `taxiSpeedMultiplier` | `float` | `0.36f` |
| `taxiTurnRateDegPerTick` | `float` | `1.25f` |
| `taxiMinTurnRadiusCells` | `float` | `3.2f` |
| `lookaheadMinCells` | `float` | `3.2f` |
| `lookaheadMaxCells` | `float` | `7.5f` |
| `lockHullHeadingWhenHovered` | `bool` | `true` |
| `allowHullTraverseForFixedWeapons` | `bool` | `false` |
| `stabilizeTurretsAgainstHullTurn` | `bool` | `true` |
| `movingFireAccuracyFactor` | `float` | `0.88f` |
| `suppressBiologicalGait` | `bool` | `true` |
| `cruiseNoseDownPitchOffset` | `float` | `0.04f` |
| `flareNoseUpPitchOffset` | `float` | `0.06f` |
| `damagedPropulsionMinSpeedFactor` | `float` | `0.42f` |

#### `GiantAquaticDef`

来源：`Source/GiantCreatureFramework/Aquatic/GiantAquaticSystem.cs`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `aquaticMode` | `GiantAquaticMode` | `GiantAquaticMode.None` |
| `canTraverseShallowWater` | `bool` | `true` |
| `canTraverseChestDeepWater` | `bool` | `true` |
| `canTraverseDeepWater` | `bool` | `true` |
| `canTraverseOceanWater` | `bool` | `true` |
| `canTraverseRivers` | `bool` | `true` |
| `canTraverseMarsh` | `bool` | `false` |
| `canTraverseToxicWater` | `bool` | `true` |
| `immuneToToxicWater` | `bool` | `true` |
| `requireDeepWater` | `bool` | `false` |
| `blockedByBridges` | `bool` | `true` |
| `minWaterClearanceRadius` | `int` | `0` |
| `shoreAvoidanceRadius` | `int` | `2` |
| `shoreAvoidanceExtraCost` | `int` | `45` |
| `waterSpeedMultiplier` | `float` | `1.0f` |
| `deepWaterSpeedMultiplier` | `float` | `0f` |
| `shallowWaterSpeedMultiplier` | `float` | `0f` |
| `landSpeedMultiplier` | `float` | `1.0f` |
| `waterDirectPathCost` | `int` | `10` |
| `landExtraPathCost` | `int` | `0` |
| `damagedPropulsionMinSpeedFactor` | `float` | `0.25f` |
| `immobilizeInWaterWhenPropulsionDestroyed` | `bool` | `false` |
| `swimIdleAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `swimAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `swimFastAnim` | `GiantAnimDef` | `默认值（引用类型为 null）` |
| `swimCycleDistance` | `float` | `0f` |
| `waterVisualOffsetZ` | `float` | `0f` |
| `buoyancyBobAmplitude` | `float` | `0.04f` |
| `buoyancyBobPeriodTicks` | `float` | `110f` |
| `spawnWaterWakeVfx` | `bool` | `true` |
| `wakeScale` | `float` | `1.0f` |
| `wakeIntervalTicks` | `int` | `9` |
| `suppressGroundDustInWater` | `bool` | `true` |
| `allowWorldWaterTravel` | `bool` | `true` |
| `allowCoastalWorldTiles` | `bool` | `true` |
| `allowRiverWorldTiles` | `bool` | `true` |
| `allowInlandWorldTravelWhenWaterOnly` | `bool` | `false` |
| `worldWaterSpeedMultiplier` | `float` | `0f` |

#### `CompProperties_CarrierHangar`

来源：`Source/GiantCreatureFramework/Carrier/ProtossCarrierSystem.cs`。 继承：`CompProperties`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `maxInterceptors` | `int` | `8` |
| `leftHangarBoneId` | `string` | `"hangar_bay_left"` |
| `rightHangarBoneId` | `string` | `"hangar_bay_right"` |
| `leftShutterBoneId` | `string` | `"hangar_door_left"` |
| `rightShutterBoneId` | `string` | `"hangar_door_right"` |
| `leftShutterOpenAngleDeg` | `float` | `0f` |
| `rightShutterOpenAngleDeg` | `float` | `0f` |
| `leftLaunchPixel` | `Vector2` | `new Vector2(180f, 220f)` |
| `rightLaunchPixel` | `Vector2` | `new Vector2(332f, 220f)` |
| `interceptorDef` | `ThingDef` | `默认值（引用类型为 null）` |
| `beamProjectileDef` | `ThingDef` | `默认值（引用类型为 null）` |
| `catapultWeaponDef` | `ThingDef` | `默认值（引用类型为 null）` |
| `catapultLaunchIntervalTicks` | `int` | `8` |
| `rebuildDurationTicks` | `int` | `360` |
| `maxInterceptorHullHp` | `float` | `95f` |
| `maxInterceptorShieldHp` | `float` | `60f` |
| `interceptorCruiseSpeed` | `float` | `16.5f` |
| `engagementRange` | `float` | `32f` |
| `maxLeashRadius` | `float` | `40f` |
| `launchAnimDefName` | `string` | `"GCF_Anim_Carrier_CatapultDispatch"` |
| `recallAnimDefName` | `string` | `"GCF_Anim_Carrier_RecallDock"` |

#### `CompProperties_AbilityCarrierScramble`

来源：`Source/GiantCreatureFramework/Carrier/ProtossCarrierSystem.cs`。 继承：`CompProperties_AbilityEffect`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `overdriveDurationTicks` | `int` | `600` |
| `immediateCatapultCount` | `int` | `4` |
| `animDefName` | `string` | `"GCF_Anim_Carrier_ScrambleOverdrive"` |

#### `CompProperties_AbilityCarrierRecall`

来源：`Source/GiantCreatureFramework/Carrier/ProtossCarrierSystem.cs`。 继承：`CompProperties_AbilityEffect`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `bonusShieldRestore` | `float` | `35f` |
| `animDefName` | `string` | `"GCF_Anim_Carrier_RecallDock"` |

#### `CompProperties_AbilityRequiresGiantBone`

来源：`Source/GiantCreatureFramework/Abilities/GiantEntitySkillSystem.cs`。 继承：`CompProperties_AbilityEffect`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `requiredBoneIds` | `List<string>` | `new List<string>()` |
| `requireAll` | `bool` | `false` |
| `organLabel` | `string` | `默认值（引用类型为 null）` |
| `disabledReasonKey` | `string` | `"GCF_AbilityDisabledMissingOrgan"` |

#### `CompProperties_AbilityAbyssalQuake`

来源：`Source/GiantCreatureFramework/Abilities/GiantEntitySkillSystem.cs`。 继承：`CompProperties_AbilityEffect`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `radius` | `float` | `4.4f` |
| `damageAmount` | `int` | `38` |
| `armorPenetration` | `float` | `0.55f` |
| `stunTicks` | `int` | `150` |
| `damageDef` | `DamageDef` | `默认值（引用类型为 null）` |
| `explosionSound` | `SoundDef` | `默认值（引用类型为 null）` |
| `triggerAtDualWhipCrossing` | `bool` | `false` |
| `dualWhipRequiredClipDefName` | `string` | `默认值（引用类型为 null）` |
| `muzzleBoneId` | `string` | `默认值（引用类型为 null）` |
| `muzzlePixel` | `Vector2` | `new Vector2(256f, 24f)` |
| `triggerTurretRecoil` | `bool` | `false` |
| `turretRecoilScale` | `float` | `1.15f` |
| `trackLeftBoneId` | `string` | `默认值（引用类型为 null）` |
| `trackRightBoneId` | `string` | `默认值（引用类型为 null）` |
| `vfxColor` | `Color` | `new Color(1.00f, 0.68f, 0.22f, 0.98f)` |

#### `CompProperties_GiantBioferriteCore`

来源：`Source/GiantCreatureFramework/Abilities/GiantEntitySkillSystem.cs`。 继承：`CompProperties`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `requiredBoneId` | `string` | `默认值（引用类型为 null）` |
| `requiredBoneIds` | `List<string>` | `new List<string>()` |
| `healPerInterval` | `float` | `16f` |
| `intervalTicks` | `int` | `60` |

#### `CompProperties_AbilityMechPhaseAdvance`

来源：`Source/GiantCreatureFramework/Abilities/MechColossusAbilitiesAndVfx.cs`。 继承：`CompProperties_AbilityEffect`。

无新增公开配置字段；使用上文说明的继承字段或宿主能力。

#### `CompProperties_AbilityMechPhaseRevert`

来源：`Source/GiantCreatureFramework/Abilities/MechColossusAbilitiesAndVfx.cs`。 继承：`CompProperties_AbilityEffect`。

无新增公开配置字段；使用上文说明的继承字段或宿主能力。

#### `JobGiver_GiantPartCombatPositioning`

来源：`Source/GiantCreatureFramework/Combat/GiantPartCombatSystem.cs`。 继承：`ThinkNode_JobGiver`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `targetAcquireRadius` | `float` | `58f` |
| `targetKeepRadius` | `float` | `70f` |

#### `JobGiver_GiantAbilityFight`

来源：`Source/GiantCreatureFramework/Abilities/GiantEntitySkillSystem.cs`。 继承：`ThinkNode_JobGiver`。

| 字段 | 类型 | 源码初始值 |
| --- | --- | --- |
| `abilityDefs` | `List<AbilityDef>` | `默认值（引用类型为 null）` |
| `maxAcquireRadius` | `float` | `42f` |

### 枚举值

表内为当前源码声明的值；可填写性仍取决于所属字段和运行时约束。控制模式为运行时状态。

- `AnimEasing`：`Linear`、`SmoothStep`、`EaseInQuad`、`EaseOutQuad`、`EaseInCubic`、`EaseOutCubic`、`EaseOutBack`、`Sine`、`CatmullRom`。
- `BoneCollisionRole`：`Auto`、`None`、`Canopy`、`CoreHull`。
- `FlightAltitudeTier`：`LowSkim`、`HighAirborne`。
- `FlightVehicleAirframeType`：`None`、`FixedWing`、`RotorcraftVTOL`、`HeavyAirship`。
- `GiantAnimVfxPreset`：`None`、`FootstepDust`、`HeavyGroundSlam`、`MuzzleBlast`、`SlashArc`、`WhipCrackSnap`、`PlasmaBurst`、`RoarShockwave`。
- `GiantAquaticMode`：`None`、`Amphibious`、`WaterOnly`。
- `GiantBoneAuraVfxKind`：`None`、`ReactorCoreGlow`、`VoidCrucibleMist`、`BioferritePulse`、`PlasmaVent`。
- `GiantCockpitControlMode`：`Dormant`、`Autonomous`、`Piloted`。
- `GiantCockpitRole`：`PrimaryPilot`、`Gunner`、`Commander`、`Passenger`。
- `GiantEmitterFireMode`：`Alternating`、`PrimaryWithFallback`、`Salvo`。
- `GiantFlightMode`：`None`、`AlwaysAirborne`、`TakeoffAndLanding`。
- `GiantHitVfxKind`：`Auto`、`Sparks`、`FleshBlood`、`BioferriteDark`、`PlasmaFlash`、`None`。
- `GiantPartArcReference`：`HullHeading`、`BoneWorldAngle`。
- `GiantPartAttackKind`：`Melee`、`Ranged`、`CarrierCatapult`。
- `GiantSeverVfxKind`：`Auto`、`MechExplosion`、`FleshSever`、`VoidRupture`、`None`。
- `GiantVfxPaletteTheme`：`Auto`、`ProtossShieldPlasma`。
- `VehicleChassisType`：`None`、`Tracked`、`Wheeled`、`Hover`、`Naval`。
