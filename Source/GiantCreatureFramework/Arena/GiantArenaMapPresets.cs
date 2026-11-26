using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public static class GiantArenaMapPresets
    {
        private static readonly GiantArenaPresetInfo[] PresetCatalog =
        {
            new GiantArenaPresetInfo
            {
                Id = nameof(GiantArenaPresetId.Colosseum),
                DisplayName = "Colosseum",
                Description = "Paved sandstone and granite arena with a center clash ring, corner obelisks, and an indestructible outer boundary.",
                HasInnerObstacles = false,
                PrimaryTerrain = "FlagstoneSandstone"
            },
            new GiantArenaPresetInfo
            {
                Id = nameof(GiantArenaPresetId.Plains),
                DisplayName = "Open Plains",
                Description = "Flat natural grassland battlefield with minimal stone corner markers and unobstructed sightlines.",
                HasInnerObstacles = false,
                PrimaryTerrain = "Soil"
            },
            new GiantArenaPresetInfo
            {
                Id = nameof(GiantArenaPresetId.DesertRuins),
                DisplayName = "Desert Ruins",
                Description = "Sun-baked sand arena with symmetric off-center crushable sandstone ruins for dramatic collateral destruction.",
                HasInnerObstacles = true,
                PrimaryTerrain = "Sand"
            },
            new GiantArenaPresetInfo
            {
                Id = nameof(GiantArenaPresetId.Tundra),
                DisplayName = "Frost Ring",
                Description = "High-contrast marble and limestone frost arena engineered for maximum silhouette clarity without movement penalties.",
                HasInnerObstacles = false,
                PrimaryTerrain = "FlagstoneLimestone"
            },
            new GiantArenaPresetInfo
            {
                Id = nameof(GiantArenaPresetId.VolcanicBasalt),
                DisplayName = "Volcanic Basalt",
                Description = "Dark slate caldera floor designed to make fire, plasma, and dragon-energy VFX pop on camera.",
                HasInnerObstacles = false,
                PrimaryTerrain = "FlagstoneSlate"
            }
        };

        public static IReadOnlyList<GiantArenaPresetInfo> ListPresets() => PresetCatalog;

        public static CellRect BuildPresetArena(
            Map map,
            GiantArenaPresetId preset,
            IntVec3 center,
            int halfWidth,
            int halfHeight,
            out CellRect innerRect)
        {
            if (map == null)
            {
                innerRect = CellRect.Empty;
                return CellRect.Empty;
            }

            int borderThickness = 2;
            int minInnerX = Mathf.Max(4 + borderThickness, center.x - halfWidth);
            int maxInnerX = Mathf.Min(map.Size.x - 5 - borderThickness, center.x + halfWidth);
            int minInnerZ = Mathf.Max(4 + borderThickness, center.z - halfHeight);
            int maxInnerZ = Mathf.Min(map.Size.z - 5 - borderThickness, center.z + halfHeight);

            innerRect = CellRect.FromLimits(minInnerX, minInnerZ, maxInnerX, maxInnerZ);
            CellRect outerRect = innerRect.ExpandedBy(borderThickness).ClipInsideMap(map);
            CellRect wipeRect = outerRect.ExpandedBy(6).ClipInsideMap(map);

            TerrainDef soil = TerrainDefOf.Soil;
            TerrainDef sand = ResolveTerrain("Sand", soil);
            TerrainDef flagSandstone = ResolveTerrain("FlagstoneSandstone", soil);
            TerrainDef tileGranite = ResolveTerrain("TileGranite", flagSandstone);
            TerrainDef tileSandstone = ResolveTerrain("TileSandstone", flagSandstone);
            TerrainDef tileSlate = ResolveTerrain("TileSlate", tileGranite);
            TerrainDef flagSlate = ResolveTerrain("FlagstoneSlate", tileSlate);
            TerrainDef flagLimestone = ResolveTerrain("FlagstoneLimestone", flagSandstone);
            TerrainDef tileMarble = ResolveTerrain("TileMarble", flagLimestone);
            TerrainDef pavedTile = ResolveTerrain("PavedTile", tileGranite);

            ThingDef rockGranite = ThingDefOf.Granite ?? DefDatabase<ThingDef>.GetNamedSilentFail("Granite");
            ThingDef rockSlate = DefDatabase<ThingDef>.GetNamedSilentFail("Slate") ?? rockGranite;
            ThingDef rockSandstone = DefDatabase<ThingDef>.GetNamedSilentFail("Sandstone") ?? rockGranite;
            ThingDef wallDef = ThingDefOf.Wall;
            ThingDef blocksSandstone = DefDatabase<ThingDef>.GetNamedSilentFail("BlocksSandstone") ?? ThingDefOf.BlocksGranite;
            ThingDef blocksGranite = ThingDefOf.BlocksGranite ?? blocksSandstone;
            ThingDef blocksSlate = DefDatabase<ThingDef>.GetNamedSilentFail("BlocksSlate") ?? blocksGranite;

            var scratch = new List<Thing>(16);

            // 1. Wipe terrain, roofs, fog, snow, and non-pawn things across the entire arena zone
            foreach (IntVec3 c in wipeRect)
            {
                map.roofGrid?.SetRoof(c, null);
                map.snowGrid?.SetDepth(c, 0f);
                map.fogGrid?.Unfog(c);

                scratch.Clear();
                scratch.AddRange(c.GetThingList(map));
                for (int i = 0; i < scratch.Count; i++)
                {
                    Thing t = scratch[i];
                    if (t == null || t is Pawn || t.Destroyed || !t.Spawned)
                    {
                        continue;
                    }
                    if (t is Corpse corpse && corpse.InnerPawn != null)
                    {
                        try { corpse.InnerPawn.jobs?.StopAll(ifLayingKeepLaying: false); } catch { }
                    }
                    if (t is Projectile || t.def.destroyable)
                    {
                        t.Destroy(DestroyMode.Vanish);
                    }
                    else
                    {
                        t.DeSpawn(DestroyMode.Vanish);
                    }
                }

                if (map.terrainGrid != null)
                {
                    if (map.terrainGrid.TempTerrainAt(c) != null)
                    {
                        map.terrainGrid.RemoveTempTerrain(c, doLeavings: false);
                    }
                    TerrainDef chosen = ChooseCellTerrain(
                        preset,
                        c,
                        center,
                        innerRect,
                        soil,
                        sand,
                        flagSandstone,
                        tileSandstone,
                        tileGranite,
                        tileSlate,
                        flagSlate,
                        flagLimestone,
                        tileMarble,
                        pavedTile);
                    map.terrainGrid.SetTerrain(c, chosen);
                }
            }

            // 2. Build indestructible thick-rock perimeter ring outside innerRect so giants stay cleanly inside the arena
            ThingDef borderRock = preset == GiantArenaPresetId.VolcanicBasalt
                ? rockSlate
                : (preset == GiantArenaPresetId.DesertRuins ? rockSandstone : rockGranite);
            if (borderRock != null)
            {
                foreach (IntVec3 c in outerRect)
                {
                    if (innerRect.Contains(c))
                    {
                        continue;
                    }
                    map.roofGrid?.SetRoof(c, RoofDefOf.RoofRockThick);
                    Thing rock = ThingMaker.MakeThing(borderRock);
                    GenSpawn.Spawn(rock, c, map, WipeMode.Vanish);
                }
            }

            // 3. Add preset-specific architectural details (never blocking the central west-east clash corridor)
            if (preset == GiantArenaPresetId.Colosseum && wallDef != null)
            {
                int ox = Math.Max(14, innerRect.Width / 2 - 12);
                int oz = Math.Max(12, innerRect.Height / 2 - 10);
                SpawnPillar2x2(map, new IntVec3(center.x - ox, 0, center.z - oz), wallDef, blocksGranite, thickRoof: false);
                SpawnPillar2x2(map, new IntVec3(center.x + ox - 1, 0, center.z - oz), wallDef, blocksGranite, thickRoof: false);
                SpawnPillar2x2(map, new IntVec3(center.x - ox, 0, center.z + oz - 1), wallDef, blocksGranite, thickRoof: false);
                SpawnPillar2x2(map, new IntVec3(center.x + ox - 1, 0, center.z + oz - 1), wallDef, blocksGranite, thickRoof: false);
            }
            else if (preset == GiantArenaPresetId.DesertRuins && wallDef != null)
            {
                // Symmetric crushable sandstone ruins on north and south flanks (leaves z in [center.z-9, center.z+9] completely open)
                SpawnWallSegment(map, center.x - 10, center.x - 4, center.z + 12, wallDef, blocksSandstone);
                SpawnWallSegment(map, center.x + 4, center.x + 10, center.z + 12, wallDef, blocksSandstone);
                SpawnWallSegment(map, center.x - 10, center.x - 4, center.z - 12, wallDef, blocksSandstone);
                SpawnWallSegment(map, center.x + 4, center.x + 10, center.z - 12, wallDef, blocksSandstone);
                SpawnPillar2x2(map, new IntVec3(center.x - 20, 0, center.z + 16), wallDef, blocksSandstone, thickRoof: false);
                SpawnPillar2x2(map, new IntVec3(center.x + 19, 0, center.z - 17), wallDef, blocksSandstone, thickRoof: false);
            }
            else if (preset == GiantArenaPresetId.VolcanicBasalt && wallDef != null)
            {
                int ox = Math.Max(16, innerRect.Width / 2 - 11);
                int oz = Math.Max(13, innerRect.Height / 2 - 9);
                SpawnPillar2x2(map, new IntVec3(center.x - ox, 0, center.z - oz), wallDef, blocksSlate, thickRoof: false);
                SpawnPillar2x2(map, new IntVec3(center.x + ox - 1, 0, center.z - oz), wallDef, blocksSlate, thickRoof: false);
                SpawnPillar2x2(map, new IntVec3(center.x - ox, 0, center.z + oz - 1), wallDef, blocksSlate, thickRoof: false);
                SpawnPillar2x2(map, new IntVec3(center.x + ox - 1, 0, center.z + oz - 1), wallDef, blocksSlate, thickRoof: false);
            }

            map.regionAndRoomUpdater?.TryRebuildDirtyRegionsAndRooms();
            return outerRect;
        }

        private static TerrainDef ResolveTerrain(string defName, TerrainDef fallback)
        {
            return DefDatabase<TerrainDef>.GetNamedSilentFail(defName) ?? fallback ?? TerrainDefOf.Soil;
        }

        private static TerrainDef ChooseCellTerrain(
            GiantArenaPresetId preset,
            IntVec3 cell,
            IntVec3 center,
            CellRect innerRect,
            TerrainDef soil,
            TerrainDef sand,
            TerrainDef flagSandstone,
            TerrainDef tileSandstone,
            TerrainDef tileGranite,
            TerrainDef tileSlate,
            TerrainDef flagSlate,
            TerrainDef flagLimestone,
            TerrainDef tileMarble,
            TerrainDef pavedTile)
        {
            float dx = cell.x - center.x;
            float dz = cell.z - center.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);

            switch (preset)
            {
                case GiantArenaPresetId.Colosseum:
                {
                    if (!innerRect.Contains(cell))
                    {
                        return tileGranite;
                    }
                    // Center clash medallion + concentric ring + border track
                    if (dist <= 3.5f || (dist >= 11.5f && dist <= 13.0f))
                    {
                        return tileGranite;
                    }
                    if (Mathf.Abs(dz) <= 1.1f && Mathf.Abs(dx) <= 24f)
                    {
                        return tileSandstone;
                    }
                    int edgeDist = Math.Min(
                        Math.Min(cell.x - innerRect.minX, innerRect.maxX - cell.x),
                        Math.Min(cell.z - innerRect.minZ, innerRect.maxZ - cell.z));
                    if (edgeDist <= 3)
                    {
                        return tileGranite;
                    }
                    return flagSandstone;
                }

                case GiantArenaPresetId.Plains:
                {
                    if (!innerRect.Contains(cell))
                    {
                        return tileGranite;
                    }
                    if (dist >= 12.0f && dist <= 13.2f)
                    {
                        return flagSandstone;
                    }
                    return soil;
                }

                case GiantArenaPresetId.DesertRuins:
                {
                    if (!innerRect.Contains(cell))
                    {
                        return flagSandstone;
                    }
                    if (dist <= 6.5f)
                    {
                        return flagSandstone;
                    }
                    if (Mathf.Abs(dz) >= 10f && Mathf.Abs(dz) <= 14f && Mathf.Abs(dx) >= 3f && Mathf.Abs(dx) <= 12f)
                    {
                        return flagSandstone;
                    }
                    return sand;
                }

                case GiantArenaPresetId.Tundra:
                {
                    if (!innerRect.Contains(cell))
                    {
                        return tileSlate;
                    }
                    if (dist <= 3.5f || (dist >= 12.0f && dist <= 13.2f))
                    {
                        return pavedTile;
                    }
                    int edgeDist = Math.Min(
                        Math.Min(cell.x - innerRect.minX, innerRect.maxX - cell.x),
                        Math.Min(cell.z - innerRect.minZ, innerRect.maxZ - cell.z));
                    if (edgeDist <= 2)
                    {
                        return tileSlate;
                    }
                    return ((cell.x + cell.z) & 3) == 0 ? tileMarble : flagLimestone;
                }

                case GiantArenaPresetId.VolcanicBasalt:
                {
                    if (!innerRect.Contains(cell))
                    {
                        return tileSlate;
                    }
                    if (dist <= 3.5f || (dist >= 12.0f && dist <= 13.2f))
                    {
                        return tileSlate;
                    }
                    return flagSlate;
                }

                default:
                    return soil;
            }
        }

        private static void SpawnWallSegment(Map map, int minX, int maxX, int z, ThingDef wallDef, ThingDef stuff)
        {
            for (int x = minX; x <= maxX; x++)
            {
                IntVec3 c = new IntVec3(x, 0, z);
                if (!c.InBounds(map))
                {
                    continue;
                }
                Thing w = ThingMaker.MakeThing(wallDef, wallDef.MadeFromStuff ? stuff : null);
                GenSpawn.Spawn(w, c, map, WipeMode.Vanish);
            }
        }

        private static void SpawnPillar2x2(Map map, IntVec3 origin, ThingDef wallDef, ThingDef stuff, bool thickRoof)
        {
            for (int dz = 0; dz < 2; dz++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    IntVec3 c = new IntVec3(origin.x + dx, 0, origin.z + dz);
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    if (thickRoof)
                    {
                        map.roofGrid?.SetRoof(c, RoofDefOf.RoofRockThick);
                    }
                    Thing w = ThingMaker.MakeThing(wallDef, wallDef.MadeFromStuff ? stuff : null);
                    GenSpawn.Spawn(w, c, map, WipeMode.Vanish);
                }
            }
        }
    }
}
