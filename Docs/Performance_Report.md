# GiantCreatureFramework (RimWorld 1.7) 性能与多模组兼容性基准测试报告 (Round 5 转阶段分帧平滑定版)

- **测试版本**：RimWorld 1.7 (1.7.5014 rev1195, .NET 10 / CoreCLR Waylith, Vulkan 1.4.312, RTX 4060 Ti)
- **测试日期**：2026-11-20
- **架构状态**：9 个 Harmony 补丁类共 209 个钩子（95 Prefix / 92 Postfix / 14 Finalizer / 8 Transpiler），新增 Harmony 补丁数为 0，0 个 `JobGiver_*` / `WorkGiver_*` 补丁，载具非 Pawn 行为统一由 `GiantVehicleBehaviorGuard` 三大收口点治理
- **剖析工具**：Dubs Performance Analyzer (DPA Exclusive / Nested 方法级采样) + 实机逐帧 FPS/TPS 遥测 + `RuntimePatchProbe` 全量 Harmony 共补丁链审计
- **原始遥测归档**：`.verify/perf_compat/live_r5_phase_smooth_min10.json`、`.verify/perf_compat/live_r5_phase_smooth_full530.json`（终版时间戳 `2026-11-20T22:45:30+0800`）、`.verify/perf_compat/live_r4_min10.json`、`.verify/perf_compat/live_r4_full530.json`、`.verify/perf_compat/gcf_copatch_audit_full530.json`

## 1. Round 5 专项优化结论概览（`CompGiantPhaseTransform` 转阶段分帧平滑与冷启动预解析）

针对第四轮 530 模组 9 Boss 混战中捕获到的 `CompGiantPhaseTransform:CompTick` 单帧 `442.92 ms` 瞬时尖峰，在不新增任何 Harmony 补丁的前提下完成了四项架构级治理，并通过 10 模组与 530 模组实机冷启动首轮混战及双阶段连续变构压测（Stage 1 -> Stage 2 -> Stage 3）：

1. **530 模组冷启动首轮混战与连续双阶段跃迁尖峰全面消除**：
   - **优化前（Round 4 `full530`）**：首次进入混战及阶段切换时 `CompGiantPhaseTransform:CompTick` 单次峰值高达 **442.9164 ms**（全窗口均值 **0.899605 ms/tick**，最低瞬时帧率跌至 **1.50 FPS**）。
   - **优化后冷启动首轮 9 Boss 混战（`wC_4x_Tick`，549 Tick，9 Boss 全程存活）**：`CompGiantPhaseTransform:CompTick` 首轮冷启动单次峰值由 `442.92 ms`（中间复测定位其中约 `424.44 ms` 来自首次全模组包 AssetBundle 扫描）直接压降至 **6.7687 ms**（削峰 **98.47%**），均值降至 **0.166597 ms/tick**（下降 **81.48%**），全场 GCF Tick 总独占降至 **2.932449 ms/tick**。
   - **优化后强制连续两次转阶段混战（`wD_PhaseMorph`，789 Tick，Stage 1 -> Stage 2 -> Stage 3 全流程）**：`CompGiantPhaseTransform:CompTick` 单次峰值仅 **1.4261 ms**（较 `442.92 ms` 削峰 **99.68% / 310.6 倍**），均值仅 **0.068465 ms/tick**（下降 **92.39%**），4x 解锁极限吞吐最低帧由 `1.50 FPS` 提升至 **12.07 - 14.88 FPS**，平均吞吐提升至 **162.84 - 199.39 TPS**。
2. **四项核心平滑机制与终版实测子函数开销（530 模组连续 2 次阶段跃迁）**：
   - **渲染子树原位切换（`CompGiantRig.TrySwapRenderSubtreeInPlace`）**：按 `GiantRigDef` 预构建并缓存已初始化的 `PawnRenderNode[]` 子树，在 `SwitchRig` 时直接原位替换 `PawnRenderNode_GiantRoot.children`、同步 `Body` 标签与 `nodeAncestors` 并置位 `rootNode.requestRecache = true`，彻底绕过 `PawnRenderer.SetAllGraphicsDirty()` 在 530 模组下触发的全量 `DynamicPawnRenderNodeSetup` 与伤口/图集重建链。终版实测 2 次原位换树总耗时仅 **0.2340 ms**（单次峰值 **0.2090 ms**，零节点重分配、零动态 `Mesh` 泄漏）。
   - **生成期全阶段材质与贴图预热（`CompGiantRig.PrewarmRigMaterialsAndMasks` + `PrewarmStageRenderSubtree`）**：在 `PostSpawnSetup` 主线程一次性预热全部阶段的 `GiantAlphaMaskCache`、`GiantMaterialCache` 与 `GraphicDatabase.Get<Graphic_Single>`，消除战斗中途首次切阶段时的贴图加载与材质池分配毛刺。
   - **双波次冲击爆炸跨 Tick 错峰解耦与占格重建去重**：移除 `DisplaceOverlappingAdjacentGiants` 内紧跟 `SwitchRig` 之后的重复 `RebuildFootprintNow`；将原本同帧叠加的两次大半径 `GenExplosion.DoExplosion` 拆分为三拍流水线：`Tick 0` 执行骨架原位切换与着色器波纹（`CompleteMorphOutAndSwitchRig` 峰值仅 **1.3765 ms**），`MorphingIn Tick +2` 触发第一波 EMP 脉冲（`ExecuteStageTransitionEmpWave` 峰值 **0.9336 ms**），`MorphingIn Tick +5` 触发第二波钝击/烈焰冲击（`ExecuteStageTransitionBlastWave` 峰值 **0.2828 ms**），并在 `PostExposeData` 中完整持久化待发波次状态。
   - **启动期限定本模组 AssetBundle 解析（`MechColossusVfxHost` 静态构造预解析）**：修复 `MechColossusVfxHost.EnsurePrefabsResolved` 原先缺少静态构造函数且首次调用遍历全部 530 个模组 `loadedAssetBundles` 导致的 `424.44 ms` 冷启动卡顿，现已移至启动期静态构造并严格限定仅匹配本模组包名，使冷启动首轮战斗也彻底告别百毫秒级阻塞。
3. **9 Boss 混战 1x 渲染与全量回归稳定性**：
   - **530 模组 9 Boss + 8 拦截机 + 殖民者同屏 1x 正常档**：实测平均 **59.21 - 60.00 FPS**、中位 **60.01 FPS**、5% Low **59.96 FPS**、1% Low **59.96 FPS**，GCF 渲染 Update 总独占降至 **0.34207 - 0.86835 ms/frame**。
   - **自动化测试与日志健康度**：核心验证套件 **55/55 全部通过**，Agent 单元测试 **18/18 全部通过**；10 模组与 530 模组实机全程 **0 启动报错、0 运行期报错、0 GCF 警告**，后台测试进程清理完整无残留。

## 2. 转阶段分帧平滑前后实机 DPA 对比（530 模组 9 Boss 混战）

| 指标项（530 激活模组，9 Boss 混战 4x 极速档） | 优化前 (Round 4 同步双爆 + 全包扫描 + SetAllGraphicsDirty) | 优化后 (Round 5 终版 `2026-11-20T22:45:30+0800`) | 优化幅度 / 结论 |
| --- | --- | --- | --- |
| 首轮冷启动混战 `CompGiantPhaseTransform:CompTick` 峰值 | 442.9164 ms (前次未加包过滤时首轮为 424.4356 ms) | 6.7687 ms (首轮均值 0.166597 ms/tick) | 冷启动首轮削峰 98.47% |
| 连续双阶段变构 `CompGiantPhaseTransform:CompTick` 峰值 | 442.9164 ms | 1.4261 ms (2 次完整跃迁均值 0.068465 ms/tick) | 变构峰值降低 99.68% (310.6 倍) |
| `CompleteMorphOutAndSwitchRig` (Tick 0 骨架切换峰值) | 约 442.9 ms (耦合双爆与全树标脏) | 1.3765 ms (总计 2.6428 ms / 2 次) | 单帧远低于 16.67 ms 帧预算 |
| `CompGiantRig:TrySwapRenderSubtreeInPlace` (原位换树峰值) | 全树重建 (`SetAllGraphicsDirty`) | 0.2090 ms (总计 0.2340 ms / 2 次) | 单次均摊仅 0.1170 ms |
| `ExecuteStageTransitionEmpWave` (Tick +2 第一波 EMP) | 与 Tick 0 同帧叠加 | 0.9336 ms (总计 1.2959 ms / 2 次) | 独立帧执行，无叠加尖峰 |
| `ExecuteStageTransitionBlastWave` (Tick +5 第二波冲击) | 与 Tick 0 同帧叠加 | 0.2828 ms (总计 0.5020 ms / 2 次) | 独立帧执行，无叠加尖峰 |
| 首轮 9 Boss 混战全场 GCF Tick 独占总和 | 3.44167 ms/tick (前次首轮 3.87183 ms/tick) | 2.93245 ms/tick (9 Boss 全程存活) | 总独占进一步下降 14.8% |
| 混战变构窗口最低瞬时帧率 (`minFps`) | 1.50 FPS | 12.07 - 14.88 FPS (4x 解锁极限吞吐) | 消除肉眼可见顿卡 |

## 3. 多轮性能优化与架构收敛演进总表

### 3.1 10 模组纯净基准五轮演进 (v1 -> v2 -> v3 -> v4 -> v5)

| 场景与核心指标 | 第一轮 (v1) | 第二轮 (v2) | 第三轮 (v3) | 第四轮 (v4) | 第五轮定版 (v5, 分帧平滑) | 累计结论 |
| --- | --- | --- | --- | --- | --- | --- |
| 0 巨兽殖民地 - 1x Update 独占 | 0.04790 ms/frame | 0.01113 ms/frame | 0.00920 ms/frame | 0.00826 ms/frame | 0.01109 ms/frame (60.09 FPS) | 微秒级常驻开销 |
| 0 巨兽殖民地 - 4x Tick 独占 | 0.01498 ms/tick | 0.00336 ms/tick | 0.00329 ms/tick | 0.00756 ms/tick (43 人) | 0.00360 ms/tick (3,589.5 TPS) | 空载近乎零损耗 |
| 6 巨兽征召待命 - 4x Tick 独占 | 未单列 | 0.47186 ms/tick | 0.24345 ms/tick | 0.20251 ms/tick | 0.20251 - 0.69753 ms/tick | 6/6 队列与起飞全绿 |
| 9 Boss 混战 - 1x 帧率 (均值 / 最低帧) | 59.9 / 28.27 FPS | 60.0 / 59.99 FPS | 60.0 / 59.99 FPS | 60.00 / 59.99 FPS | 60.00 / 59.99 FPS | 全程锁死 60 FPS |
| 9 Boss 混战双阶段变构峰值 (`maxMs`) | 未单列 | 未单列 | 未单列 | 未单列 | 2.1893 ms (`SubtreeSwap` 0.2516 ms) | 2 次完整变构平滑无感 |

### 3.2 530 模组超大型整合包演进总表 (v2 -> v3 -> v4 -> v5)

| 场景与核心指标 (528 - 530 激活模组) | 第二轮 (v2) | 第三轮 (v3) | 第四轮 (v4) | 第五轮定版 (v5, 分帧平滑) | 定版结论 |
| --- | --- | --- | --- | --- | --- |
| 0 巨兽殖民地 - 3x Tick 独占耗时 | 未单列 | 低于 0.018 ms/tick | 0.01296 ms/tick (322 TPS) | 0.01251 ms/tick (41 人, 303.3 TPS) | 静默短路稳定 |
| 0 巨兽殖民地 - 4x 实测墙钟 TPS | 未单列 | 620.76 TPS (38 人) | 635.45 TPS (41 人) | 838.20 TPS (41 人, 0.01411 ms) | 41 人下吞吐再创新高 |
| 6 巨兽征召待命 - GCF Tick 独占 | 0.95958 ms/tick | 0.71462 ms/tick | 0.71003 ms/tick (57 人) | 0.54652 ms/tick (44 人, 663.0 TPS) | 6/6 征召与防空洞全过 |
| 无人坦克非 Pawn 行为隔离 | 8 个 JobGiver 钩子 | 8 个 JobGiver 钩子 | 0 钩子 / 0 泄漏 | 0 钩子 / 坐标锁死 `(120, 125)` / 0 泄漏 | 彻底杜绝非载具行为 |
| 9 Boss 混战 - 1x 帧率 (均值 / 5% Low) | 5% Low 29.11 FPS | 56.72 / 59.96 FPS | 57.44 / 59.96 FPS | 59.21 - 60.00 / 59.96 FPS | 1x 实战稳定满帧 |
| 9 Boss 混战 - 1x Update 渲染总独占 | 未单列 | 1.42502 ms/frame | 1.37790 ms/frame | 0.34207 - 0.86835 ms/frame | 渲染开销大幅压低 |
| 9 Boss 混战 - 冷启动首轮与双变构峰值 | 未单列 | 未单列 | 442.9164 ms (单帧卡顿) | 首轮 6.7687 ms / 双变构 1.4261 ms | 百毫秒卡顿彻底消除 |

## 4. 530 模组 Harmony 共补丁兼容性与回归验证结论

- **补丁规模保持恒定**：全模组维持 **9 个 `[HarmonyPatch]` 类、209 个 Harmony 钩子**（`RuntimePatchProbe` 实机扫描覆盖 **169 个目标方法**，其中 **104 个方法**与整合包其余 529 个模组共补丁链无冲突共存），本次分帧平滑、原位渲染子树切换与启动期预解析 **0 新增补丁**。
- **全量回归测试通过**：
  - `GiantCreatureFramework.Tests`：**55/55 全部通过**（含新增 `TrySwapRenderSubtreeInPlace` 往返缓存零重分配断言与 `Tick 0 / Tick +2 / Tick +5` 分帧爆炸状态机断言）。
  - `GiantCreatureFramework.Agent.Tests`：**18/18 全部通过**。
  - 10 模组与 530 模组实机全流程日志审计：**0 启动错误、0 运行期错误、0 GCF 警告**。
