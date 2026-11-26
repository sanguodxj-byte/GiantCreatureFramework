# GCF 骨架、动画与渲染字段（rig_body_and_anim_xml）

本手册提供视觉实现所需的 XML 字段。技能机制和玩法由用户与模型自主设计。

座舱控制、阶段变形、部位武器、飞行、机库等框架能力的完整配置，以及全部公开配置类型的字段参考，见 `load_manual("framework_capabilities_and_fields")`。

## 一、字段与单位

字段是 XML 标签对应的配置参数名。标签必须放在正确的配置类型下，例如 `BoneNodeDef.drawSize` 控制骨骼贴图尺寸，`GiantSkinnedMeshDef.drawSize` 控制蒙皮表面尺寸。

- `60 Ticks = 1 秒`；`durationTicks`、`tick` 为 float，`blendInTicks` 为 int。
- `drawSize` 和位移使用地图格，Vector2 对应世界 XZ。
- 默认 `textureResolution=512`；`*Pixel` 从左上角计量，X 向右、Y 向下。
- `pivotUV` / `parentSocketUV` 使用左下角原点的 UV；像素字段会转换为 UV。
- 连续画布中心是 `(256,256)`，纹素索引中心是 `(255.5,255.5)`；镜像纹素索引用 `511-x`，连续像素坐标用 `512-x`。
- `texPath` 是 `Textures/` 内部路径，省略 `.png` 后缀。

## 二、骨架与部件

顶层 `GiantCreatureFramework.GiantRigDef`：

- `defName`、`label`、`nodes`。
- 动画引用：`idleAnim`、`walkAnim`、`runAnim`、`turnLeftAnim`、`turnRightAnim`、`downedAnim`、`deathAnim`。
- 显示与采样：`portraitScaleFactor`、`walkCycleDistance`、`runCycleDistance`、`useCoupledSagittalKinematics`。
- 视觉子系统：`skinnedMeshes`、`softWhips`、`bezierChains`、`rollingTracks`、`steering`。
- 阴影：`shadowTexPath`、`shadowDrawSize`、`shadowOffsetXZ`、`shadowAlpha`；血条样式引用 `healthBarDef`。

`nodes` 中每项为 `BoneNodeDef`：

- 拓扑：`nodeId` 唯一，`parentId` 指向父节点，根节点留空。
- 拼装：`texPath`、`drawSize`、`pivotPixel`、`parentSocketPixel`、`localOffset`、`baseAngle`、`layerOffset`、`textureResolution`。
- 材质：`silhouetteTexPath`、`silhouetteLayerOffset`、`glowTexPath`、`glowColor`、`glowLayerOffset`、`shaderTypeDef`、`alphaThreshold`（byte，此类型默认 32）。
- 关节：`isRigid`、`continuousSpinDegPerTick`、`sagittalDropCells`、`isCoronalFlapHinge`、`baseCoronalFlap`；推进与升力部位标记：`isLocomotionDrivePart`、`isFlightLiftPart`、`isAquaticPropulsionPart`。
- 状态映射：`linkedBodyPart`、`linkedBodyPartCustomLabel`、`linkedBodyPartIndex`、`hideWhenMissing`。
- 空间层级：`contributesToHitbox`、`contributesToCollision`、`collisionRole`（`CoreSolid` / `Canopy` / `HitboxOnly`）；胸腹盆骨等核心躯干保留 `contributesToCollision=true`，前伸头颈、翼面与悬空尾节应设为 `false` 归入视觉冠层。
- 方向变体：`eastVariant`、`westVariant`、`sectorSourceBoneId`。
- 回转与后坐表现：`isTurret`、`maxTurretAimAngleDeg`、`turretRecoil`。
- 局部挂点：`emitterSockets`，使用 `socketId`、`pixel`、`angleOffset`。
- 乘员视觉挂点：`crewSeats`，使用 `seatId`、`mountPixel`、`localOffset`、`screenOffset`、`facingOffsetDeg`、`layerOffset`、`drawScale`、`renderOccupant`、`renderAboveAllParts`、`rotateSpriteWithBone`。

宿主 `ThingDef.comps` 挂载 `GiantCreatureFramework.CompProperties_GiantRig`，其 `rigDef` 指向骨架；`race.renderTree` 使用 `GCF_GiantSkeletalRenderTree`。

宿主 `BodyDef` 为显示状态映射提供解剖树。骨骼关联的部位须能唯一解析，父子关系与对应 `BodyPartRecord` 层级一致。完整 Pawn 还需具备游戏要求的意识、移动等身体标签；可以查询原版定义确认宿主加载条件。

## 三、关键帧动画

`GiantCreatureFramework.GiantAnimDef`：`defName`、`label`、`durationTicks`、`loop`、`blendInTicks`、`tracks`。`tracks` 为 `BoneAnimTrack` 列表，包含 `nodeId` 与 `keyframes`。

| `BoneKeyframe` 字段 | 类型与含义 |
| --- | --- |
| `tick` | float，片段内时间 |
| `angle` | float，俯视顺时针旋转增量 |
| `sagittalPitch` | float，矢状面俯仰；配合骨骼 `sagittalDropCells` |
| `coronalFlap` | float，冠状面折叠；配合 `isCoronalFlapHinge` |
| `offset` | Vector2，根骨骼 XZ 位移 |
| `easing` | `Linear`、`SmoothStep`、`EaseInQuad`、`EaseOutQuad`、`EaseInCubic`、`EaseOutCubic`、`EaseOutBack`、`Sine`、`CatmullRom` |

循环动作使用 `loop=true`，单次动作用 `false`。当前关节校验不接受非根骨骼的非零关键帧 `offset` 或动画缩放；刚性子骨骼保持相对固定。地面载具非死亡片段的车体保持零位移，部件后坐使用 `turretRecoil`。

每个骨架需配置非循环 `deathAnim`，时长至少 20 Tick，至少一根有效骨骼有两帧及可见变化（旋转跨度至少 8 度，或根部位移至少 0.25 格）。

`generate_locomotion_anim` 输出待机、步态、扑翼、起降等动作关键帧；`preview_anim_strip` 输出多帧拼图和关节诊断。

## 四、柔性表面与转向

- `GiantSkinnedMeshDef`（`skinnedMeshes`）：`meshId`、`texPath`、`anchorBoneId`、`boneIds`、`drawSize`、`pivotPixel`、`parentSocketPixel`、`layerOffset`、`subdivisionsX`、`subdivisionsY`、`weightSharpness`、`maxInfluences`、`cullSeveredBoneTriangles`；可选 `silhouetteTexPath`、`silhouetteLayerOffset`、`glowTexPath`、`glowColor`、`glowLayerOffset`、`shaderTypeDef`。`alphaThreshold` 默认 16。
- `BezierChainDef`（`bezierChains`）：`chainId`、`boneIds`、`tipUV`、`p1Frequency` / `p1Damping`、`p2Frequency` / `p2Damping`、`p3Frequency` / `p3Damping`。骨骼组成连续父子链。
- `SoftWhipDef`（`softWhips`）：`whipId`、`rootBoneId`、`texPath`、`exitPixel`、`particleCount`、`totalLength`、`baseWidth`、`tipWidthFactor`、`layerOffset`、`torsoBoneId`、`colliderCenterLocal`、`colliderRadii`。躯干引用采用自己的骨骼 ID。
- `SteeringConfigDef`（`steering`）：`enabled`、`lookChains`、`lagBones`、`yawBankBones`。
- `SteeringLookChainDef`：`chainId`、`boneIds`、`weights`、`maxChainAngleDeg`、`maxBoneAngleDeg`、`turnSpeedDegPerTick`、`full360Traverse`。
- `SteeringLagBoneDef`：`boneId`、`followSpeedDegPerTick`、`followResponsiveness`、`maxBendAngleDeg`。
- `SteeringYawBankDef`：`boneId`、`yawRateGain`、`maxAngleDeg`、`smoothing`。

## 五、其他视觉配置

- `GiantTurretRecoilDef`（骨骼 `turretRecoil`）：`distanceCells`、`kickTicks`、`recoveryTicks`、`maxAccumulatedCells`、`recoilBoneId`。
- `GiantRollingTrackDef`（`rollingTracks`）：`trackId`、`parentBoneId`、`sideSign`、`gaugeHalfWidthCells`、`dorsalRibbonTexPath`、`lateralRibbonTexPath`、`tileLengthCells`、`wheels`。轮组字段为 `wheelId`、`texPath`、`centerPixel`、`diameterCells`、`layerOffset`。
- `GiantFlightDef` 已启用时的视觉字段：`altitudeVisualOffsetZ`、`hoverBobAmplitude`、`hoverBobPeriodTicks`、`dropShadowOffsetXZ`、`dropShadowScale`、`dropShadowAlpha`。
- `GiantAquaticDef`（`aquaticLocomotion`）已启用时的视觉字段：`swimIdleAnim`、`swimAnim`、`swimFastAnim`、`swimCycleDistance`、`waterVisualOffsetZ`、`buoyancyBobAmplitude`、`buoyancyBobPeriodTicks`、`spawnWaterWakeVfx`、`wakeScale`、`wakeIntervalTicks`、`suppressGroundDustInWater`。
- `GiantAnimEventDef`（动画 `events`）：`tick`、`boneId`、`socketId`、`pixel`、`angleOffset`、`vfxPreset`、`fleckDef`、`moteDef`、`effecterDef`、`scale`、`color`、`cameraShake`。事件服从播放路径的过滤规则。
- `GiantHealthBarDef`：`barWidth`、`mainBarHeight`、`topScreenMargin`、`subRowHeight`、`defaultExpanded`、`backgroundColor`、`healthyFillColor`、`woundedFillColor`、`criticalFillColor`。此类型没有 `displayMode`。

## 六、视觉校验

1. 检查骨骼 ID、父子拓扑、解剖映射和关节坐标。
2. 父子绘制层级差至少 0.5；左右配对、回转件、轮廓底衬等顺序按当前 `ConfigErrors` / `RigJointAuditor` 检查。
3. 连接点周围应有足够不透明像素，贴图轮廓及透明边缘应通过 `validate_creature`。
4. 用 `preview_anim_strip` 检查多帧和不同朝向，核对拼接、折叠与动作连续性。
5. 注入后在游戏中检查实际渲染。`validate_creature` 还会返回现有运行时与宿主配置错误，按错误对应类型核实字段。
6. 若为地面近战生物，核对前伸头颈设为 `contributesToCollision=false`（归入 `pawnExclusionCells` 视觉冠层），且正面主近战武器的 `strikePixel`、`range` 与 `maxDynamicMeleeContactLungeCells` 能触及视觉冠层前缘外侧 `1` 格，避免战斗中推走小型目标。

更多视觉示例和工具入口见 `load_manual("gcf_archetype_and_def_manual")`。
