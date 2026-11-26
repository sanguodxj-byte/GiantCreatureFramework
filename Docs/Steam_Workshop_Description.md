# Giant Creature Framework - Steam 创意工坊完整描述与图文对照文档

本文档归档了玩家向极简版 Steam 创意工坊双语 BBCode 描述（纯文本副本位于 `Docs/SteamWorkshop/Steam_Workshop_CN.bbcode.txt` 与 `Docs/SteamWorkshop/Steam_Workshop_EN.bbcode.txt`）。全文遵循 [Steam 官方 BBCode 格式规范](https://steamcommunity.com/sharedfiles/filedetails/?id=2807121939)，单语种描述严格控制在 8,000 字符上限以内。

## 一、五张极简原版风横幅与动图替换对照表

| 占位标识 | 对应横幅或模组内动图 | 横幅标题文字 |
| :--- | :--- | :--- |
| `00_Hero_Banner.jpg` | 顶部 4:1 主视觉横幅 | `GIANT CREATURE FRAMEWORK` / `巨兽作战与多部件骨骼框架 · RIMWORLD 1.7` |
| `01_Core_Features.jpg` | 第一分栏 8:1 横幅 | `核心玩法特性 · CORE FEATURES` |
| `02_Included_Samples.jpg` | 第二分栏 8:1 横幅 | `内置样机 · BUILT-IN SAMPLE` |
| `03_AI_Workshop.jpg` | 第三分栏 8:1 横幅 | `AI巨兽创作工坊 · AI WORKSHOP` |
| `04_Performance_FAQ.jpg` | 第四分栏 8:1 横幅 | `性能兼容与问答 · PERFORMANCE & FAQ` |
| `gcf_all_giants_brawl.gif` | 图床直链动图 | 顶部引言下方的全巨兽同屏混战实机演示 |

## 二、Steam 创意工坊完整描述（简体中文 BBCode）

```bbcode
[img]https://替换为主视觉横幅直链/00_Hero_Banner.jpg[/img]

[quote]原版边缘世界里的生物无论体型多大，本质上都只是一张整体平移的贴图。
[b]Giant Creature Framework（GCF）[/b] 是一套专为巨型生物、超重型机甲与大型载具打造的基础框架——让巨兽拥有真正独立活动的四肢与双翼、实打实的多格身躯、“打断哪里就废掉哪里”的部位破坏体验，并内置了[b]免手写配置的游戏内 AI 巨兽创作工坊[/b]。[/quote]

[img]https://替换为实机混战动图直链/gcf_all_giants_brawl.gif[/img]

[img]https://替换为分栏横幅1直链/01_Core_Features.jpg[/img]

[list]
[*][b]真正的多部位动作与断肢破坏[/b]：巨兽的头颈、四肢、翅膀、长尾与触须都会随动作自然摆动。战斗中集火斩断某只翅膀或肢体时，对应部位会当场断裂飞出，长在该部位上的利爪、尾刺或重炮也会随之失效。
[*][b]所见即所得的多格体积与顺滑交互[/b]：巨兽转身时，受击范围与阻挡范围会跟着身形实时转动，近战小人贴身肉搏不会卡在透明“空气墙”外。无论是征召小人右键集火，还是给驯服的巨兽喂食、包扎、训练，直接点击它身体任意位置都能正常响应。
[*][b]多武器齐射、独立炮塔与阶段变形[/b]：单只巨兽或重型载具可以同时用多门武器攻击不同方向的敌人，背负的炮塔支持 360 度独立转向开火，强力首领还能在战斗中展开装甲、切换不同阶段形态。
[*][b]空地切换、多人乘骑与跨地图远征[/b]：有翼巨兽与空中母舰可在地面肉搏与高空飞行之间自由切换；支持殖民者骑上兽背或进入最多 12 人的座舱协同作战，并能直接飞越世界地图远征其他据点。
[/list]

[img]https://替换为分栏横幅2直链/02_Included_Samples.jpg[/img]

主框架保持精简无冗余内容，内置「重装机械巨蝎」供验证与体验；同时提供完整的模块化扩展接口，支持独立扩展模组或玩家自制单位按需挂载生效：
[list]
[*][b]内置样机 · 重装机械巨蝎[/b]：演示多足步态、精细肢体破坏与尾刺射击，支持机械师在击败战争女王后培育（Biotech）。
[/list]

[img]https://替换为分栏横幅3直链/03_AI_Workshop.jpg[/img]

几十块肢体坐标、动作曲线与几千行配置本来就不该靠人手敲——在【模组设置】中勾选开启创作终端后，游戏底部菜单就会出现内置的[b]巨兽创作工坊[/b]，让普通玩家和作者都能把脑洞直接变成实机巨兽：
[list]
[*][b]动动嘴就能造巨兽，全程免手搓配置[/b]：填入你自己的模型与文生图 API 后，只需在游戏里用自然语言描述想要的怪兽、机甲或载具，终端就能自动生成全套分部件贴图、自动抠图切分、拼装全身关节并配好战斗动作与技能。
[*][b]免重启热加载，跨档自动生效[/b]：生成好的巨兽无需重启游戏，一键即可投放到当前地图里实机测试手感，不满意随时继续对话修改（开发者模式下还可用骨骼检视面板实时预览动作与关节角度）；所有自制巨兽保存在独立目录中跨档自动加载，也可提取目录内的贴图与配置封装为扩展模组分享。
[/list]

[img]https://替换为分栏横幅4直链/04_Performance_FAQ.jpg[/img]

[list]
[*][b]日常零负担，混战不掉帧[/b]：经过 500+ 模组大型整合包实测，地图上没有巨兽时框架自动休眠，几乎不占性能；9 只巨型首领同屏混战依然稳定保持 [b]60 FPS[/b]。
[*][b]前置与排序[/b]：仅需前置 [b]Harmony[/b]，排在 Core 与官方 DLC 之后、附属巨兽模组之前即可；所有官方 DLC 均为可选。
[/list]

[spoiler=常见问题解答 (FAQ)]
[b]Q：必须买齐所有 DLC 或配置 AI API 才能玩吗？[/b]
A：都不需要。只装 RimWorld 1.7 本体和 Harmony 就能正常游玩全部框架机制与附属巨兽；只有当你想在游戏里自己捏新巨兽时，才需要在模组设置中开启创作终端并填入自己的文本与文生图 API（纯文字描述生成，不支持外部垫图/图生图）。

[b]Q：可以中途加入或移出存档吗？[/b]
A：支持中途安全加入现有存档。若需中途移除，请先在地图与远征队中清理所有巨兽及相关任务点后再保存退出。

[b]Q：怎么让小人集火攻击或照顾巨兽？[/b]
A：和操作原版生物完全一样——征召小人后右键点巨兽身体任意位置即可集火；对自家驯服的巨兽右键点身体任意位置即可喂食、包扎或训练。

[b]Q：兼容 Combat Extended (CE) 吗？[/b]
A：目前基于原版战斗系统制作，暂未内置 CE 兼容补丁；不兼容 RimThreaded 多线程模组。
[/spoiler]
```

## 三、Steam 创意工坊完整描述（English BBCode）

```bbcode
[img]https://REPLACE_WITH_DIRECT_URL/00_Hero_Banner.jpg[/img]

[quote]In vanilla RimWorld, even the biggest creatures are still single flat sprites sliding across the ground.
[b]Giant Creature Framework (GCF)[/b] is a foundation built for colossal beasts, super-heavy mechs, and multi-tile vehicles—bringing independently animated limbs and wings, true multi-tile bodies, limb-severing disarms, and a [b]built-in in-game AI Giant Workshop[/b] so nobody has to hand-write thousands of lines of config.[/quote]

[img]https://REPLACE_WITH_DIRECT_URL/gcf_all_giants_brawl.gif[/img]

[img]https://REPLACE_WITH_DIRECT_URL/01_Core_Features.jpg[/img]

[list]
[*][b]True Multi-Part Animation & Limb Severing[/b]: Heads, limbs, wings, tails, and tentacles move naturally on their own. Focus-firing a wing or claw can sever it mid-battle, sending debris flying and disabling whatever weapons were mounted on that limb.
[*][b]What-You-See-Is-What-You-Hit Multi-Tile Bodies[/b]: Hitboxes and collision turn smoothly with the creature's body, with no invisible air walls blocking melee fighters. Drafted right-click focus fire, feeding, medical tending, and taming all work naturally when clicking any part of a giant's body.
[*][b]Multi-Weapon Volleys, Turrets & Boss Phases[/b]: A single giant or heavy vehicle can fire multiple weapons at different targets simultaneously, rotate independent 360-degree turrets, and unfold into new boss phases mid-fight.
[*][b]Flight, Multi-Crew Riding & World Map Travel[/b]: Winged beasts and carriers can take off to cross obstacles and land at will. Up to 12 colonists can ride on saddles or board enclosed cockpits, and fly directly across the world map to raid distant sites.
[/list]

[img]https://REPLACE_WITH_DIRECT_URL/02_Included_Samples.jpg[/img]

The core framework stays lean and de-contented with the built-in [b]Heavy Mech Scorpion[/b], while providing modular extension hooks for standalone add-ons and player-authored creatures:
[list]
[*][b]Built-in · Heavy Mech Scorpion[/b]: Multi-leg gait, granular limb crippling, tail-stinger volleys, and Mechanitor gestation after defeating a War Queen (Biotech).
[/list]

[img]https://REPLACE_WITH_DIRECT_URL/03_AI_Workshop.jpg[/img]

Rigging dozens of joints, pivot coordinates, and animation curves by hand is brutal—enable the [b]Creature Forge[/b] terminal in Mod Settings to add an in-game AI authoring workshop to your bottom menu bar:
[list]
[*][b]Describe It in Plain Words—Zero Hand-Typed Config[/b]: Connect your own LLM and text-to-image API keys, describe the monster, walker, or vehicle you want in plain language, and the terminal automatically generates part sprites, cuts out backgrounds, assembles the rig, and sets up animations and attacks.
[*][b]Instant Hot-Reload & Persistent Storage[/b]: Spawn your newly created giant straight into your live map without restarting RimWorld, refine it via chat (and preview clips/joint angles in the DevMode rig inspector), and keep it automatically loaded across saves in its own isolated creature folder—ready to copy or wrap into a sub-mod to share.
[/list]

[img]https://REPLACE_WITH_DIRECT_URL/04_Performance_FAQ.jpg[/img]

[list]
[*][b]Smooth 60 FPS Performance[/b]: Tested in a 500+ mod load order. The framework sleeps when no giants are on the map with virtually zero overhead, and holds a steady [b]60 FPS[/b] even with 9 giant bosses fighting on screen at once.
[*][b]Load Order[/b]: Load [b]Harmony[/b] -> Core & Official DLCs (all optional) -> [b]Giant Creature Framework[/b] -> GCF Add-ons.
[/list]

[spoiler=Frequently Asked Questions (FAQ)]
[b]Q: Do I need all DLCs or an AI API key just to play?[/b]
A: Neither is required! Base RimWorld 1.7 + Harmony works out of the box. API keys are only needed if you turn on the Creature Forge terminal in Mod Settings to generate brand-new custom giants (pure text-to-image creation only; external reference images / img2img are not supported).

[b]Q: Safe to add or remove mid-save?[/b]
A: Safe to add mid-save. Before removing, dismiss or destroy any active giants and quest sites first.

[b]Q: Compatible with Combat Extended (CE)?[/b]
A: Designed around vanilla combat (no built-in CE patch yet). Incompatible with RimThreaded.
[/spoiler]
