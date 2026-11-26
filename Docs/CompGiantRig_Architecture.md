# CompGiantRig 职责划分

`CompGiantRig` 是挂载到 Pawn 的单一 `ThingComp`，通过 partial 文件按职责组织。
XML 中的类型名称、公开 API、Harmony 挂点及存档键都保持原有契约。

## 文件导航

所有实现文件位于 `Source/GiantCreatureFramework/Components/`。

| 文件 | 职责 |
| --- | --- |
| `CompGiantRig.cs` | 共享状态、公开状态查询、存档、生成/离图/销毁入口与 `CompTick` 调度 |
| `CompGiantRig.Initialization.cs` | 工作缓冲区初始化、贴图与材质预热、资源释放、`SwitchRig` |
| `CompGiantRig.Anatomy.cs` | 骨骼与身体部位绑定、受击部位解析、断肢级联、健康与可见性状态 |
| `CompGiantRig.DamageAndDeath.cs` | 受伤闪红、受击反击、死亡动画和尸体加载/更新 |
| `CompGiantRig.Locomotion.cs` | 入场飞行、移动距离采样、追击、疾奔判定和即时朝向设置 |
| `CompGiantRig.Steering.cs` | 路径前视、凝视目标、警戒扫视、身体与关节转向 |
| `CompGiantRig.DailyActivities.cs` | 睡眠判断、进食与社交动作权重、日常行为注视目标 |
| `CompGiantRig.Animation.cs` | 动画时钟、关键帧事件、片段切换和混合起始姿态 |
| `CompGiantRig.Actions.cs` | 近战/远程/技能动画入口、预热衔接、强制变形和预览播放 |
| `CompGiantRig.Pose.cs` | 程序化偏移、炮塔后坐、姿态求值和渲染快照发布 |
| `CompGiantRig.RenderTree.cs` | 渲染子树创建、阶段子树缓存、原位切换与 `CompRenderNodes` |
| `CompGiantRig.Rendering.cs` | 地面阴影、常驻骨骼特效、动态网格主线程上传和空中高度计算 |
| `CompGiantRig.SpatialAndMuzzles.cs` | 世界姿态求解、实际位置、骨骼 UV/像素采样、软鞭采样与炮口分配 |
| `CompGiantRig.RenderingAndOverlays.cs` | 并行绘制矩阵、空间占格重建、乘员挂点、调试覆盖层与 Gizmo |
| `CompGiantRig.World.cs` | 世界地图模拟、放置占格预览和世界动画更新 |

## 运行顺序与维护边界

`CompTick` 保留在主文件，集中表达各子系统的执行顺序：

1. 同步征召状态，更新身体缺损与运动系统。
2. 采样移动距离，推进部位战斗。
3. 更新朝向和动画时钟，求值姿态并发布快照。
4. 按占格变化与更新预算重建空间数据，处理破拆和常驻特效。

渲染树创建、材质预热和动态网格上传保留原有主线程约束；并行渲染继续读取已发布的快照。
生成、读档、切换骨架、尸体更新和世界地图模拟仍调用同一组姿态与空间实现。

共享字段保留在原声明位置，以维持初始化顺序和既有反射访问。partial 文件仍属于同一个类，
不构成独立服务或新的运行时状态容器。新增方法应放入对应职责文件；跨领域调用顺序应保留在
生命周期入口中，避免重新集中到主文件或引入第二份状态。

源码结构检查应聚合 `CompGiantRig.cs` 与 `CompGiantRig.*.cs`，不能假定全部实现位于主文件。
