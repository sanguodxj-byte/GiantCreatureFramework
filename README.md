# Giant Creature Framework (GCF)

**English** | [简体中文](README.zh-CN.md)

A RimWorld 1.7 framework for multi-part colossal creatures, super-heavy mechs, and multi-tile vehicles.

---

## Overview

In vanilla RimWorld, large creatures are rendered and simulated as a single translating sprite. Giant Creature Framework (GCF) introduces an articulated skeletal, skinned-mesh, and multi-cell simulation layer on top of RimWorld 1.7:

- **Multi-Part Skeletal & Soft-Body Animation**: Independently animated heads, necks, limbs, wings, tails, turrets, rolling tracks, Bezier chains, soft whips, and 2D Linear Blend Skinning (LBS) meshes.
- **Granular Limb Severing & Battle Damage**: Destroying anatomical body parts severs the corresponding visual subtree into falling debris, disables limb-mounted weapons and propulsion, and applies progressive wound, scar, bloodstain, and bandage overlays.
- **Three-Tier Dynamic Multi-Cell Footprints**: Real-time rotation-aware rasterization into core solid collision cells (`CoreSolid`), exterior small-pawn exclusion canopies (`Canopy`), and alpha-accurate silhouette hitboxes (`HitboxOnly`), supporting seamless melee engagement, taming, feeding, and tending on any body cell.
- **Multi-Weapon Volleys, Turrets & Boss Phases**: Simultaneous multi-muzzle firing, 360-degree turret tracking with recoil, and multi-stage boss transformations.
- **Flight, Aquatic Locomotion, Multi-Crew Cockpits & World Travel**: Ground-to-air flight transitions, amphibious/naval locomotion, up to 12-crew cockpits or bone-mounted rider seats, and world map overflight expeditions.
- **Built-in Sample (`GCF_MechScorpion`)**: Includes the Heavy Mech Scorpion (`GCF_MechScorpion`) with multi-leg gait, limb crippling, tail-stinger volleys, and optional Biotech Mechanitor gestation.
- **In-Game AI Giant Workshop (`GiantCreatureFramework.Agent`)**: Optional Creature Forge terminal (enabled in Mod Settings, using your own LLM and text-to-image API keys) for generating part sprites, rigging joints, configuring animations, and hot-reloading custom creatures without restarting RimWorld.

---

## Requirements & Load Order

- **Game Version**: RimWorld 1.7
- **Required Dependency**: [Harmony](https://github.com/pardeike/HarmonyRimWorld/releases/latest) (`brrainz.harmony`)
- **DLCs**: All official DLCs (`Royalty`, `Ideology`, `Biotech`, `Anomaly`, `Odyssey`) are optional. Mech Scorpion Mechanitor recipes activate automatically when `Biotech` is loaded.
- **Incompatibilities**: `RimThreaded` (`majorhoff.rimthreaded`)

Recommended load order:

1. `Harmony` (`brrainz.harmony`)
2. `RimWorld` Core and official DLCs
3. `Giant Creature Framework` (`sanguo.giantcreatureframework`)
4. Standalone GCF add-on mods

---

## Repository Structure

| Path | Description |
| :--- | :--- |
| `About/` | RimWorld mod metadata (`About.xml`) and mod preview image |
| `1.7/Defs/` | Core defs (bodies, rigs, animations, health bars, shaders, jobs, UI, Heavy Mech Scorpion) |
| `1.7/Biotech/` | Optional Biotech Mechanitor recipes and patches for Heavy Mech Scorpion |
| `1.7/Languages/` | Localization files (ChineseSimplified, English, Japanese, Russian) |
| `1.7/Shaders/` | Custom GLSL shaders for drop shadows and multi-layer VFX |
| `1.7/Textures/` | Mech Scorpion part textures, generic wound decals, and UI icons |
| `1.7/AgentSkills/` | Prompt specs, tool schemas, and manuals used by the in-game Authoring Terminal |
| `Source/GiantCreatureFramework/` | Core framework C# assembly (`GiantCreatureFramework.csproj`) |
| `Source/GiantCreatureFramework.Agent/` | In-game AI Giant Workshop assembly (`GiantCreatureFramework.Agent.csproj`) |
| `Source/GiantCreatureFramework.Agent.Tests/` | Standalone MSTest suite for the authoring agent and imaging pipeline |
| `Source/GiantCreatureFramework.Arena.BridgeTools/` | Optional RimBridgeServer companion tools for Giant Arena matches |
| `Tools/` | Arena CLI launcher and match recorder (`giant_arena_cli.py`) |
| `Docs/` | Architecture notes, authoring manual, performance report, and Workshop descriptions |

---

## Building & Testing

The C# projects target `.NET 10` (`net10.0`) and reference RimWorld 1.7 assemblies via `$(RimWorldPath)` (default `D:\Steam\steamapps\common\RimWorld`). Override `RimWorldPath` if your RimWorld installation is in another directory:

```bash
dotnet build Source/GiantCreatureFramework/GiantCreatureFramework.csproj -c Release -p:RimWorldPath="PATH_TO_RIMWORLD"
dotnet build Source/GiantCreatureFramework.Agent/GiantCreatureFramework.Agent.csproj -c Release -p:RimWorldPath="PATH_TO_RIMWORLD"
```

Run the standalone Agent and imaging unit tests:

```bash
dotnet test Source/GiantCreatureFramework.Agent.Tests/GiantCreatureFramework.Agent.Tests.csproj -c Release
```

Compiled mod assemblies output to `1.7/Assemblies/` (ignored by Git). Pass `-p:DeployToGame=true` during build to copy mod files into `$(RimWorldPath)\Mods\GiantCreatureFramework`.

---

## Documentation

- [Giant Creature Framework Manual](Docs/GiantCreatureFramework_Manual.md)
- [CompGiantRig Architecture](Docs/CompGiantRig_Architecture.md)
- [Performance Report](Docs/Performance_Report.md)

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
