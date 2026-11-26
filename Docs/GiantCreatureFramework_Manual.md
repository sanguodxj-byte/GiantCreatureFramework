# Giant Creature Framework 能力与制作手册

本手册面向模组作者和巨兽工坊模型，列出框架提供的能力及其配置入口。用户与模型自主决定如何组合能力、设计技能和设定数值；调用现有能力时使用框架实际提供的字段。

第 1—9 节说明渲染与视觉制作，第 10 节提供全部能力导航。座舱、阶段、武器、飞行、机库等完整说明和字段类型/初始值见 `load_manual("framework_capabilities_and_fields")`，文件为 `1.7/AgentSkills/manuals/framework_capabilities_and_fields.md`。

## 1. 渲染视觉范围

部件贴图与关节坐标写入 `GiantRigDef`，`GiantAnimDef` 提供关键帧，`CompGiantRig` 求解姿态并交给渲染节点绘制。柔性表面可使用蒙皮，长条部件可使用贝塞尔链或软鞭。

| 视觉需求 | 配置入口 |
| --- | --- |
| 拼装、关节旋转与遮挡顺序 | `GiantRigDef.nodes` / `BoneNodeDef` |
| 待机、步态、转身与自定义动作 | `GiantAnimDef.tracks` |
| 连续变形表面 | `GiantRigDef.skinnedMeshes` |
| 长尾、触须与柔性条带 | `bezierChains` / `softWhips` |
| 侧向贴图、回转与后坐表现 | `eastVariant` / `westVariant`、`isTurret`、`turretRecoil` |
| 履带纹理滚动与轮组显示 | `rollingTracks` |
| 阴影、辉光与特效挂点 | `shadowTexPath`、`glowTexPath`、`GiantAnimDef.events` |
| 乘员跟随骨骼显示 | `BoneNodeDef.crewSeats` |
| 血条外观 | `GiantRigDef.healthBarDef` / `GiantHealthBarDef` |

## 2. “字段”是什么

字段就是配置参数名。XML 标签对应 C# 配置类型中的字段；名称、所在类型和数值类型都需要匹配。例如：

```xml
<texPath>Things/CustomCreature/Body</texPath>
<drawSize>(4, 6)</drawSize>
<pivotPixel>(256, 300)</pivotPixel>
<layerOffset>5</layerOffset>
```

这些 `BoneNodeDef` 字段依次表示贴图路径、显示尺寸、旋转中心和绘制层级。模型与用户自由设计内容，字段负责把设计传给渲染器。

- `texPath` 从 `Textures/` 内部开始，不带 `.png` 后缀。
- `*Pixel` 使用贴图左上角原点，X 向右、Y 向下；默认 `textureResolution=512`。连续画布中心为 `(256,256)`；纹素索引是 `0..511`，中心为 `(255.5,255.5)`。
- `pivotUV`、`parentSocketUV` 使用左下角原点的 `0..1` UV 坐标；像素字段在解析时换算为 UV。
- `drawSize`、`localOffset`、`offset` 使用地图格，二维坐标对应世界 XZ 平面。角度单位为度。
- `60 Ticks = 1 秒`。`durationTicks`、`tick` 是 float，`blendInTicks` 是 int。Tick 是游戏时间，不是视频帧数。

## 3. 骨架与贴图

`GiantRigDef` 是顶层 Def。`nodes` 内每个 `BoneNodeDef` 表示一个部件；`nodeId` 唯一，`parentId` 指向父骨骼，根骨骼留空。

| 类型 | 视觉字段 |
| --- | --- |
| `GiantRigDef` | `nodes`、`idleAnim`、`walkAnim`、`runAnim`、`turnLeftAnim`、`turnRightAnim`、`downedAnim`、`deathAnim`、`walkCycleDistance`、`runCycleDistance`、`portraitScaleFactor` |
| `BoneNodeDef` 拼装 | `nodeId`、`parentId`、`texPath`、`drawSize`、`textureResolution`、`pivotPixel`、`parentSocketPixel`、`localOffset`、`baseAngle`、`layerOffset` |
| `BoneNodeDef` 材质 | `silhouetteTexPath`、`silhouetteLayerOffset`、`glowTexPath`、`glowColor`、`glowLayerOffset`、`shaderTypeDef`、`alphaThreshold` |
| `BoneNodeDef` 关节与状态 | `isRigid`、`continuousSpinDegPerTick`、`sagittalDropCells`、`isCoronalFlapHinge`、`baseCoronalFlap`、`linkedBodyPart`、`linkedBodyPartCustomLabel`、`linkedBodyPartIndex`、`hideWhenMissing` |
| `BoneNodeDef` 空间层级 | `contributesToHitbox`、`contributesToCollision`、`collisionRole`（`CoreSolid` / `Canopy` / `HitboxOnly`） |

`pivotPixel` 是自身旋转中心，`parentSocketPixel` 是父贴图上的连接点。连接处需有足够不透明区域，避免动作中露缝。`layerOffset` 控制遮挡顺序。

空间光栅化分为三层：核心实体格 `collisionCells`（`contributesToCollision=true`，仅放置胸腔、盆骨与颈根等躯干核心，决定巨兽对巨兽贴胸格斗与建筑阻挡）、视觉冠层排挤格 `pawnExclusionCells`（长颈、头颅、翼臂与悬空尾节设为 `contributesToCollision=false`，排挤静止的非巨兽小型 Pawn 防止钻入贴图下方，同时作为对小型目标近战寻路的停步外缘）、以及受击轮廓格 `hitCells`（完整可见剪影命中区）。长颈或前伸头部生物配置正面近战 `partAttacks` 时，其 `rangeOriginBoneId`、`range`、`strikePixel` 与 `maxDynamicMeleeContactLungeCells` 必须覆盖视觉冠层正前方外侧至少 `1` 格，避免因头部冠层大于近战触及范围而持续推走小型目标。

`linkedBodyPart` 等字段把显示部件关联到宿主解剖结构，使隐藏、局部着色与部件状态一致。使用 `hideWhenMissing=true` 时，需正确解析到唯一的身体部位记录。

已有 Pawn 的接入片段如下，所引用骨架必须存在；宿主仍需具备原版要求的完整种族定义：

```xml
<comps>
  <li Class="GiantCreatureFramework.CompProperties_GiantRig">
    <rigDef>Custom_VisualRig</rigDef>
  </li>
</comps>
<race>
  <renderTree>GCF_GiantSkeletalRenderTree</renderTree>
</race>
```

## 4. 骨骼动画

`GiantAnimDef` 使用 `durationTicks`、`loop`、`blendInTicks`、`tracks`。每条 `BoneAnimTrack` 用 `nodeId` 选择骨骼，以 `keyframes` 定义运动。

| `BoneKeyframe` 字段 | 含义 |
| --- | --- |
| `tick` | float，片段内时间 |
| `angle` | float，俯视平面顺时针偏航增量 |
| `sagittalPitch` | float，矢状面俯仰，配合骨骼 `sagittalDropCells` |
| `coronalFlap` | float，冠状面折叠，配合 `isCoronalFlapHinge` |
| `offset` | Vector2，根骨骼 XZ 位移 |
| `easing` | `Linear`、`SmoothStep`、`EaseInQuad`、`EaseOutQuad`、`EaseInCubic`、`EaseOutCubic`、`EaseOutBack`、`Sine`、`CatmullRom` |

下面是独立动作 Def，供绑定到骨架或由预览工具采样。`root` 必须对应实际骨骼：

```xml
<GiantCreatureFramework.GiantAnimDef>
  <defName>Custom_VisualIdle</defName>
  <durationTicks>60</durationTicks>
  <loop>true</loop>
  <blendInTicks>8</blendInTicks>
  <tracks>
    <li>
      <nodeId>root</nodeId>
      <keyframes>
        <li><tick>0</tick><angle>-2</angle><easing>CatmullRom</easing></li>
        <li><tick>30</tick><angle>2</angle><easing>CatmullRom</easing></li>
        <li><tick>60</tick><angle>-2</angle><easing>CatmullRom</easing></li>
      </keyframes>
    </li>
  </tracks>
</GiantCreatureFramework.GiantAnimDef>
```

循环动作设 `loop=true`，单次动作设 `false`。当前校验要求非根骨骼关键帧不能有非零 `offset`，也不要用关键帧 `scale` 拉伸部件。`isRigid=true` 的子件保持相对固定。每个骨架需配置有效的非循环 `deathAnim`，时长至少 20 Tick，并具有可见姿态变化。

用 `generate_locomotion_anim` 生成动作草案，再用 `preview_anim_strip` 比较多个时间点和朝向，调整动作幅度、时序与关节连接。

## 5. 蒙皮与柔性部件

在 `GiantRigDef` 下配置 `<skinnedMeshes>`，每项为 `GiantSkinnedMeshDef`：

| 字段 | 作用 |
| --- | --- |
| `meshId`、`texPath`、`boneIds` | 网格标识、贴图与参与变形的骨骼 |
| `anchorBoneId`、`drawSize`、`pivotPixel`、`parentSocketPixel` | 基准姿态的放置方式 |
| `subdivisionsX`、`subdivisionsY` | 网格细分数，int |
| `weightSharpness`、`maxInfluences` | 权重衰减与单顶点影响骨骼数 |
| `layerOffset`、`silhouetteTexPath`、`silhouetteLayerOffset` | 表面和轮廓底衬 |
| `glowTexPath`、`glowColor`、`glowLayerOffset`、`shaderTypeDef` | 辉光与材质 |
| `textureResolution`、`alphaThreshold` | 坐标分辨率与透明阈值；此类型透明阈值默认 16 |
| `cullSeveredBoneTriangles` | 隐藏已缺失骨骼主导的局部三角面 |

以下片段挂在 `GiantRigDef` 下，骨骼和贴图需先存在：

```xml
<skinnedMeshes>
  <li>
    <meshId>surface</meshId>
    <texPath>Things/CustomCreature/Surface</texPath>
    <anchorBoneId>root</anchorBoneId>
    <drawSize>(4, 6)</drawSize>
    <pivotPixel>(256, 256)</pivotPixel>
    <parentSocketPixel>(256, 256)</parentSocketPixel>
    <layerOffset>3</layerOffset>
    <subdivisionsX>20</subdivisionsX>
    <subdivisionsY>20</subdivisionsY>
    <boneIds><li>root</li><li>segment</li></boneIds>
  </li>
</skinnedMeshes>
```

| 类型与入口 | 视觉配置 |
| --- | --- |
| `BezierChainDef` / `bezierChains` | `chainId`、`boneIds`、`tipUV`、`p1Frequency`、`p1Damping`、`p2Frequency`、`p2Damping`、`p3Frequency`、`p3Damping` |
| `SoftWhipDef` / `softWhips` | `whipId`、`rootBoneId`、`texPath`、`exitPixel`、`particleCount`、`totalLength`、`baseWidth`、`tipWidthFactor`、`layerOffset`、`torsoBoneId`、`colliderCenterLocal`、`colliderRadii` |
| `GiantRollingTrackDef` / `rollingTracks` | `trackId`、`parentBoneId`、`sideSign`、`gaugeHalfWidthCells`、`dorsalRibbonTexPath`、`lateralRibbonTexPath`、`tileLengthCells`、`wheels` |
| `GiantTrackWheelDef` / `wheels` | `wheelId`、`texPath`、`centerPixel`、`diameterCells`、`layerOffset` |

贝塞尔链使用连续父子骨骼；软鞭使用连续条带与粒子约束，`torsoBoneId` 指向自己的躯干。履带滚动使用运动系统提供的位移输入。

## 6. 朝向、回转、后坐与挂点

`BoneNodeDef.eastVariant` / `westVariant` 配置侧向贴图和坐标；`sectorSourceBoneId` 选择方向判断来源。`SteeringConfigDef.lookChains`、`lagBones`、`yawBankBones` 描述注视链、滞后部件和转向侧倾。

回转部件在 `nodes` 中配置 `<isTurret>true</isTurret>` 与 `maxTurretAimAngleDeg`；注视链可使用 `full360Traverse`、`turnSpeedDegPerTick`。`BoneNodeDef.turretRecoil` 的参数为 `distanceCells`、`kickTicks`、`recoveryTicks`、`maxAccumulatedCells`、`recoilBoneId`。

`emitterSockets` 定义局部挂点，每个 `GiantBoneEmitterSocketDef` 使用 `socketId`、`pixel`、`angleOffset`。调用方取得世界位置和方向后，自主组织表现。

已有乘员系统需要跟随骨骼显示时，在 `BoneNodeDef.crewSeats` 配置 `seatId`、`mountPixel`、`localOffset`、`screenOffset`、`facingOffsetDeg`、`layerOffset`、`drawScale`、`renderOccupant`、`renderAboveAllParts`、`rotateSpriteWithBone`。宿主乘员组件用 `renderCrewOnRig` 开启显示，其 `seats` 内的 `rigSeatId` 关联到骨骼挂点。

## 7. 阴影、水面、视觉事件与血条样式

- 地面阴影：`GiantRigDef.shadowTexPath`、`shadowDrawSize`、`shadowOffsetXZ`、`shadowAlpha`。
- 已启用飞行运动时的视觉参数：`GiantFlightDef.altitudeVisualOffsetZ`、`hoverBobAmplitude`、`hoverBobPeriodTicks`、`dropShadowOffsetXZ`、`dropShadowScale`、`dropShadowAlpha`。
- 已启用水面水栖运动时的视觉参数：`GiantAquaticDef.swimIdleAnim`、`swimAnim`、`swimFastAnim`、`swimCycleDistance`、`waterVisualOffsetZ`、`buoyancyBobAmplitude`、`buoyancyBobPeriodTicks`、`spawnWaterWakeVfx`、`wakeScale`、`wakeIntervalTicks`、`suppressGroundDustInWater`。
- 动画视觉事件：`GiantAnimDef.events` 中的 `GiantAnimEventDef` 用 `tick`、`boneId`、`socketId`、`pixel`、`angleOffset` 定位，用 `vfxPreset`、`fleckDef`、`moteDef`、`effecterDef`、`scale`、`color`、`cameraShake` 配置表现；事件服从当前播放路径的过滤规则。
- 血条样式：`GiantHealthBarDef` 使用 `barWidth`、`mainBarHeight`、`topScreenMargin`、`subRowHeight`、`defaultExpanded` 及颜色字段。当前类型没有 `displayMode` 字段。

## 8. 水面巨兽、海生巨兽与舰船定义（可选配置）

在 `GiantRigDef` 下按需挂载可选配置块 `<aquaticLocomotion>`（`GiantAquaticDef`）；不配置或 `aquaticMode=None` 时完全不生效、零额外开销。支持**生物型海生/两栖巨兽**与**机械舰船/水面航母**两类用法：

| 配置维度 | 关键字段与说明 |
| --- | --- |
| 水栖模式 | `aquaticMode`：`Amphibious`（水陆两栖：可下浅水、齐胸流水、深水与海洋，也可正常上岸陆行）；`WaterOnly`（纯水栖海兽/水面舰船：严格绑定合格水域，禁止在陆地寻路、驻留、漫游或生成） |
| 吃水与水域类型 | `canTraverseShallowWater`、`canTraverseChestDeepWater`、`canTraverseDeepWater`、`canTraverseOceanWater`、`canTraverseRivers`、`canTraverseMarsh`（默认 `false`，避免深吃水舰船驶入内陆泥沼）、`canTraverseToxicWater`、`requireDeepWater`（设为 `true` 时禁止进入浅水，仅限齐胸水/深水）、`blockedByBridges`（默认 `true`，桥面阻挡水面舰船；若开启地形破拆或设为 `false` 可通过） |
| 岸线净空与航道偏好 | `minWaterClearanceRadius`（硬性吃水净空半径 `0..6`，`WaterOnly` 下要求周围半径内均为合格水域）、`shoreAvoidanceRadius` 与 `shoreAvoidanceExtraCost`（软性岸线避让权重，使舰船优先沿开阔主航道航行，同时仍可靠岸停泊离舱） |
| 移速、寻路代价与推进部件 | `waterSpeedMultiplier`、`deepWaterSpeedMultiplier`、`shallowWaterSpeedMultiplier`、`landSpeedMultiplier`（两栖上岸移速倍率）、`waterDirectPathCost`、`landExtraPathCost`（两栖陆地额外寻路代价，用于引导偏好水路）；骨骼标记 `<isAquaticPropulsionPart>true</isAquaticPropulsionPart>`（尾鳍、蹼、喷水推进器、舰船螺旋桨）关联推进效率，可配 `damagedPropulsionMinSpeedFactor` 与 `immobilizeInWaterWhenPropulsionDestroyed` |
| 水中动作与波浪尾迹 | `swimIdleAnim`、`swimAnim`、`swimFastAnim`、`swimCycleDistance`（留空自动回退到 `idleAnim` / `walkAnim` / `runAnim`）；`waterVisualOffsetZ`、`buoyancyBobAmplitude`、`buoyancyBobPeriodTicks` 控制吃水线与浮力起伏；`spawnWaterWakeVfx`、`wakeScale`、`wakeIntervalTicks` 生成首尾水花尾迹 |
| 世界地图远洋与近岸航行 | `allowWorldWaterTravel`（允许跨越海洋/湖泊格）、`allowCoastalWorldTiles`（允许访问/进攻海岸聚落并从海面落图）、`allowRiverWorldTiles`（允许沿内河格航行）、`allowInlandWorldTravelWhenWaterOnly`、`worldWaterSpeedMultiplier` |

水面舰船（战列舰、巡洋舰、水面航空母舰、炮艇）可同时配置 `<aquaticLocomotion>` 与 `<vehicleLocomotion><chassisType>Naval</chassisType>...</vehicleLocomotion>`：启用舰体加减速惯性、舵效最小转弯半径（`allowPivotTurnInPlace=false` 时按 `minTurnRadiusCells` 弧线操舵；`true` 时支持双桨/侧推器原地回转）、倒车航行（`allowReverse`）、甲板多座 360° 稳定炮塔（`<isTurret>true</isTurret>`）、舰桥乘员舱（`CompProperties_GiantCockpit`）以及海面舰载机机库（`CompProperties_CarrierHangar`）。纯生物海兽则仅配 `<aquaticLocomotion>`，保留完整生物 AI、近战/远程部位攻击与柔性触手/尾链动画。若同时配置 `<flightLocomotion>`（如水上飞机或海龙），升空期间走空中越障规则，`WaterOnly` 降落时仅允许落回合格水面。

```xml
<!-- 示例 A：无法上陆地的水面战舰（搭配 Naval 舰船运动学） -->
<aquaticLocomotion>
  <aquaticMode>WaterOnly</aquaticMode>
  <canTraverseDeepWater>true</canTraverseDeepWater>
  <canTraverseOceanWater>true</canTraverseOceanWater>
  <blockedByBridges>true</blockedByBridges>
  <shoreAvoidanceRadius>2</shoreAvoidanceRadius>
  <waterSpeedMultiplier>1.15</waterSpeedMultiplier>
  <immobilizeInWaterWhenPropulsionDestroyed>true</immobilizeInWaterWhenPropulsionDestroyed>
  <spawnWaterWakeVfx>true</spawnWaterWakeVfx>
  <wakeScale>1.4</wakeScale>
</aquaticLocomotion>
<vehicleLocomotion>
  <chassisType>Naval</chassisType>
  <allowPivotTurnInPlace>false</allowPivotTurnInPlace>
  <minTurnRadiusCells>4.5</minTurnRadiusCells>
  <movingTurnRateDegPerTick>0.85</movingTurnRateDegPerTick>
  <accelerationCellsPerSec2>1.4</accelerationCellsPerSec2>
  <brakingCellsPerSec2>2.2</brakingCellsPerSec2>
  <allowReverse>true</allowReverse>
</vehicleLocomotion>

<!-- 示例 B：可以下水也可上岸的两栖海生巨兽 -->
<aquaticLocomotion>
  <aquaticMode>Amphibious</aquaticMode>
  <waterSpeedMultiplier>1.35</waterSpeedMultiplier>
  <landSpeedMultiplier>0.75</landSpeedMultiplier>
  <landExtraPathCost>35</landExtraPathCost>
  <swimAnim>Custom_SeaBeast_Swim</swimAnim>
</aquaticLocomotion>
```

## 9. 视觉调用接口

以下为现有 `CompGiantRig` 入口，调用者自行组织时机，方法签名以当前程序集为准：

| 方法 | 用途 |
| --- | --- |
| `TriggerPreviewClip` | 播放调试预览片段 |
| `SetProceduralBoneAngleOffset` | 设置骨骼的程序化旋转偏移 |
| `TryGetBonePixelWorldPos` / `TryGetBoneUVWorldPos` | 采样挂点世界位置与方向 |
| `TriggerTurretRecoilByBoneId` | 触发指定骨骼的后坐表现 |
| `SwitchRig` | 替换骨架，重建关联状态与显示数据 |

C# 扩展节点可继承 `PawnRenderNode_GiantBone` 等框架节点并设置有效的骨架属性，进入现有矩阵计算路径；普通外部节点在巨兽上受渲染隔离策略限制。

## 10. 制作工具与视觉检查

人类通过巨兽工坊说明书窗口读取本文件。模型使用 `list_manuals` / `load_manual(manualName: "gcf_archetype_and_def_manual")`；RimCopilot 可使用其 `read_mod_manual`。手册标识保留以兼容已有调用。

| 工作 | 工具 |
| --- | --- |
| 读取字段与工作区 | `list_manuals`、`load_manual`、`list_workspace`、`read_creature_xml` |
| 制作贴图 | `generate_image`、`extract_sheet`、`warp_bone`、`preview_sheet` |
| 编写并预览动作 | `write_creature_xml`、`generate_locomotion_anim`、`preview_anim_strip` |
| 检查并进入游戏查看 | `validate_creature`、`inject_creature`、`spawn_creature` |

视觉检查覆盖静止、转向、完整动作循环、不同朝向、部件隐藏、阴影和辉光；确认关节不断开、遮挡顺序合理、贴图没有裁切或残留背景。`validate_creature` 同时执行现有运行时配置校验，完整宿主仍需满足游戏加载条件。

开发模式下可使用骨骼、受击格和碰撞格叠层，以及 `Dialog_GiantRigInspector` 检查关节、挂点和动画；同时核对核心碰撞格缩回胸口且正面主近战武器可触及视觉冠层外侧 `1` 格。关键帧预览用于制作检查，最终画面仍应在游戏渲染中核对。

## 11. 框架能力与完整字段入口

以下能力均有对应接口；专属实现的限制见完整配置手册。

| 能力 | 配置入口与关键字段 |
| --- | --- |
| 座舱控制与生物骑乘 | `CompProperties_GiantCockpit` / `CompProperties_GiantCrew`：`seats`、`autonomousWhenEmpty`、岗位与控制前置字段；运行时控制模式不是 XML `mode` |
| 多阶段变形 | `CompProperties_GiantPhaseTransform.stages`；各阶段用 `rigDef`、`healthScaleFactor`、`moveSpeedOffset` 和变形动画引用 |
| 部位武器 | `BoneNodeDef.partAttacks`：近战、远程、弹射、范围、时序、伤害与动作绑定 |
| 多炮口与轮换发射 | `GiantRigDef.weaponEmitters`：`fireMode`、`barrels`、来源匹配和缺损回退 |
| 地面与舰船载具运动 | `vehicleLocomotion`：`Tracked`、`Wheeled`、`Hover`、`Naval`（水面舰船操舵、倒车、惯性与推进器损伤） |
| 水面巨兽与海生水栖 | `aquaticLocomotion`（`GiantAquaticDef`）：`Amphibious`（可下水可上岸）与 `WaterOnly`（纯水栖海兽/舰船，无法上陆地）、吃水深度、桥面阻挡、水中动作、浮力尾迹与远洋/近岸航行 |
| 飞行与航空机动 | `flightLocomotion` / `flightVehicleLocomotion`：起降、高度层、越障、升力、固定翼/垂直起降/飞艇；`flightEntry` 配置入场飞行 |
| 机库与舰载机 | `CompProperties_CarrierHangar`：容量、左右挂点、舱门、弹射、回收和重建 |
| 空间与交互 | `GiantRigDef` / `BoneNodeDef`：多格受击、碰撞、避让、破拆、疾奔、尸体占格 |
| 部位健康规则 | 解剖映射、护甲/弱点、断肢、痛觉、核心死亡与核心修复组件 |
| 已有能力组件 | 部位完好条件、范围效果、阶段操作与机库操作通用组件；扩展子模组专属组件限制见完整配置手册 |
| 玩家控制与自主行为 | 征召/排队命令节点、部位战斗走位节点、通用能力作战节点 |
| 世界地图旅行 | `GiantWorldUtility`、`WorldObject_Giant`：出发、目的地、落图和适用地点交互 |
| 模块化扩展挂钩 | `GiantMotionOverrideRegistry`、`GiantAutonomousAbilityRegistry`、`GiantPhaseTransformVfxRegistry`、`GiantProgressionUtility`：供独立 `[GCF]` 子模组按需注册事件、动作覆盖与配方门控 |
| 创作与验证 | 工作区、贴图与动作工具、热注入、调试叠层、骨骼检查器、竞技场 CLI 与桥接 |

在巨兽工坊中用 `load_manual("framework_capabilities_and_fields")` 读取配置说明与按类型列出的字段表。本文与该手册都说明接口和约束，不预设技能组合；示例名称相关的同步限制也会明确列出。
