using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Thread-local context flags used during giant pathfinding and step advancement
    /// so <see cref="GenGrid.WalkableBy(IntVec3, Map, Pawn)"/> and door checks allow
    /// destructible breach paths only for the active giant query without affecting
    /// normal pawns, placement, or spawning.
    /// Kept outside Harmony patch classes intentionally so reflection-based patch
    /// field checks do not see nullable thread-static state.
    /// </summary>
    public static class GiantPathingContext
    {
        [ThreadStatic]
        private static Pawn activeGiant;

        [ThreadStatic]
        private static int allowDestructibleWalkableDepth;

        [ThreadStatic]
        private static Pawn parameterizePawn;

        [ThreadStatic]
        private static IntVec3 parameterizeStartCell;

        [ThreadStatic]
        private static int parameterizePawnDepth;

        public readonly struct ParameterizeScopeState
        {
            public readonly bool Pushed;
            public readonly Pawn PreviousPawn;
            public readonly IntVec3 PreviousStartCell;

            public ParameterizeScopeState(bool pushed, Pawn previousPawn, IntVec3 previousStartCell)
            {
                Pushed = pushed;
                PreviousPawn = previousPawn;
                PreviousStartCell = previousStartCell;
            }
        }

        /// <summary>
        /// Current pawn whose path request is being parameterized on this thread.
        /// </summary>
        public static Pawn ActiveParameterizePawn => parameterizePawnDepth > 0 ? parameterizePawn : null;

        /// <summary>
        /// Start cell of the path request currently being parameterized on this thread,
        /// or <see cref="IntVec3.Invalid"/> when inactive or unset.
        /// </summary>
        public static IntVec3 ActiveParameterizeStartCell => parameterizePawnDepth > 0 ? parameterizeStartCell : IntVec3.Invalid;

        public static ParameterizeScopeState PushParameterizePawn(Pawn pawn, IntVec3 startCell)
        {
            if (pawn != null && GiantFastLookup.IsEmpty)
            {
                return default;
            }
            Pawn previousPawn = parameterizePawnDepth > 0 ? parameterizePawn : null;
            IntVec3 previousStart = parameterizePawnDepth > 0 ? parameterizeStartCell : IntVec3.Invalid;
            parameterizePawn = pawn;
            parameterizeStartCell = startCell;
            parameterizePawnDepth++;
            return new ParameterizeScopeState(true, previousPawn, previousStart);
        }

        public static ParameterizeScopeState PushParameterizePawn(Pawn pawn)
        {
            return PushParameterizePawn(pawn, IntVec3.Invalid);
        }

        public static void PopParameterizePawn(ParameterizeScopeState previous)
        {
            if (!previous.Pushed)
            {
                return;
            }
            if (parameterizePawnDepth > 0)
            {
                parameterizePawnDepth--;
            }

            if (parameterizePawnDepth > 0)
            {
                parameterizePawn = previous.PreviousPawn;
                parameterizeStartCell = previous.PreviousStartCell;
            }
            else
            {
                parameterizePawn = null;
                parameterizeStartCell = IntVec3.Invalid;
            }
        }

        public readonly struct Scope : IDisposable
        {
            private readonly Pawn previousGiant;
            private readonly bool active;

            public Scope(Pawn giant)
            {
                if (giant != null)
                {
                    previousGiant = activeGiant;
                    activeGiant = giant;
                    allowDestructibleWalkableDepth++;
                    active = true;
                }
                else
                {
                    previousGiant = null;
                    active = false;
                }
            }

            public void Dispose()
            {
                if (!active)
                {
                    return;
                }
                allowDestructibleWalkableDepth = Math.Max(0, allowDestructibleWalkableDepth - 1);
                activeGiant = previousGiant;
            }
        }

        public static Scope Enter(Pawn giant)
        {
            if (GiantFastLookup.IsEmpty || giant == null)
            {
                return default;
            }
            return new Scope(giant);
        }

        public static Pawn ActiveGiant => allowDestructibleWalkableDepth > 0 ? activeGiant : null;

        public static bool IsWalkableOverrideActive(Pawn pawn)
        {
            return allowDestructibleWalkableDepth > 0 && pawn != null && ReferenceEquals(activeGiant, pawn);
        }
    }

    /// <summary>
    /// Evaluates thick-roof exclusions, building/rock/terrain crush eligibility,
    /// breach pathability costs, and step/collision obstacle destruction for giant creatures.
    /// </summary>
    public static class GiantDestructionUtility
    {
        public const int ImpassablePathCost = 10000;
        public const int DefaultNaturalRockBreachCost = 36;
        public const int DefaultBuildingBreachCost = 28;
        public const int DefaultTerrainBreachCost = 24;

        public static bool CanGiantCrushAnything(
            GiantRigDef rig,
            out bool buildings,
            out bool naturalRock,
            out bool terrain)
        {
            if (rig == null || !rig.crushBuildingsAndTerrain)
            {
                buildings = false;
                naturalRock = false;
                terrain = false;
                return false;
            }

            buildings = rig.canCrushConstructedBuildings;
            naturalRock = rig.canCrushNaturalRock;
            terrain = rig.canCrushDestructibleTerrain;
            return buildings || naturalRock || terrain;
        }

        public static bool CanGiantCrushAnything(
            CompGiantRig comp,
            out bool buildings,
            out bool naturalRock,
            out bool terrain)
        {
            if (comp != null && comp.IsAirborneOrTakingOff)
            {
                buildings = false;
                naturalRock = false;
                terrain = false;
                return false;
            }
            return CanGiantCrushAnything(
                comp?.RigDef,
                out buildings,
                out naturalRock,
                out terrain);
        }

        public static bool AnyRigSupportsCrushing(
            Map map,
            out bool allowBuildings,
            out bool allowRock,
            out bool allowTerrain)
        {
            allowBuildings = false;
            allowRock = false;
            allowTerrain = false;

            if (map != null)
            {
                GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
                IReadOnlyList<CompGiantRig> active = mapComp?.ActiveGiants;
                if (active == null || active.Count == 0)
                {
                    return false;
                }
                for (int i = 0; i < active.Count; i++)
                {
                    if (CanGiantCrushAnything(active[i]?.RigDef, out bool b, out bool r, out bool t))
                    {
                        allowBuildings |= b;
                        allowRock |= r;
                        allowTerrain |= t;
                    }
                }
                return allowBuildings || allowRock || allowTerrain;
            }

            List<GiantRigDef> allRigs = DefDatabase<GiantRigDef>.AllDefsListForReading;
            if (allRigs != null && allRigs.Count > 0)
            {
                for (int i = 0; i < allRigs.Count; i++)
                {
                    if (CanGiantCrushAnything(allRigs[i], out bool b, out bool r, out bool t))
                    {
                        allowBuildings |= b;
                        allowRock |= r;
                        allowTerrain |= t;
                    }
                }
                return allowBuildings || allowRock || allowTerrain;
            }

            return false;
        }

        public static bool HasThickRoofAt(Map map, IntVec3 cell)
        {
            if (map?.roofGrid == null || !cell.InBounds(map))
            {
                return false;
            }
            RoofDef roof = map.roofGrid.RoofAt(cell);
            return roof != null && roof.isThickRoof;
        }

        public static bool HasThickRoofUnderThing(Map map, Thing thing)
        {
            if (map?.roofGrid == null || thing == null)
            {
                return false;
            }

            CellRect rect = thing.OccupiedRect();
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (HasThickRoofAt(map, c))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool IsProtectedAlliedThingForFaction(Faction giantFaction, Thing thing)
        {
            if (giantFaction == null || thing == null)
            {
                return false;
            }
            Faction thingFaction = thing.Faction;
            if (thingFaction == null)
            {
                return false;
            }
            return !thingFaction.HostileTo(giantFaction);
        }

        public static bool IsProtectedAlliedThing(Pawn giant, Thing thing)
        {
            if (giant == null || thing == null)
            {
                return false;
            }
            if (giant.Faction == null)
            {
                return false;
            }
            if (giant.mindState?.mentalStateHandler != null && giant.InAggroMentalState)
            {
                return false;
            }
            if (giant.kindDef != null && giant.HostileTo(thing))
            {
                return false;
            }
            CompGiantRig comp = GiantFastLookup.TryGetRig(giant);
            if (comp?.RigDef != null && !comp.RigDef.protectAlliedStructures)
            {
                return false;
            }

            return IsProtectedAlliedThingForFaction(giant.Faction, thing);
        }

        public static bool IsProtectedSpecialBuilding(Building building)
        {
            if (building?.def == null)
            {
                return false;
            }
            if (!building.questTags.NullOrEmpty())
            {
                return true;
            }
            if (GiantArenaService.IsProtectedArenaBoundaryBuilding(building))
            {
                return true;
            }
            if (building is Building_AncientMechRemains
                || building is Building_GravEngine
                || building.def.defName == "GravEngine")
            {
                return true;
            }
            BuildingProperties bp = building.def.building;
            if (bp != null)
            {
                if (bp.ai_neverTrashThis)
                {
                    return true;
                }
                if (!bp.IsDeconstructible && !IsNaturalRockOrMineable(building))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsNaturalRockOrMineable(Building building)
        {
            if (building?.def == null)
            {
                return false;
            }
            return building.def.mineable || (building.def.building != null && building.def.building.isNaturalRock);
        }

        public static bool IsTerrainCellDestructible(
            bool isThickRoof,
            TerrainDef top,
            TerrainDef temp,
            TerrainDef under,
            TerrainDef foundation,
            bool allowDestructibleTerrain)
        {
            if (isThickRoof || !allowDestructibleTerrain)
            {
                return false;
            }

            if (temp != null && temp.passability == Traversability.Impassable)
            {
                return temp.Removable;
            }

            TerrainDef effective = top ?? foundation;
            if (effective == null || effective.passability != Traversability.Impassable)
            {
                return false;
            }

            if (foundation != null && foundation.Removable && foundation.passability == Traversability.Impassable)
            {
                return under == null || under.passability != Traversability.Impassable;
            }

            if (top != null && top.Removable)
            {
                if (under != null)
                {
                    return under.passability != Traversability.Impassable;
                }
                if (top.burnedDef != null && top.burnedDef.passability != Traversability.Impassable)
                {
                    return true;
                }
                return !top.IsWater;
            }

            if (top != null && !top.IsWater)
            {
                if (top.tags != null && top.tags.Contains("GCF_DestructibleTerrain"))
                {
                    return true;
                }
                if (top.burnedDef != null && top.burnedDef.passability != Traversability.Impassable)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsDestructibleImpassableTerrainAt(Map map, IntVec3 cell)
        {
            if (map?.terrainGrid == null || !cell.InBounds(map))
            {
                return false;
            }

            if (HasThickRoofAt(map, cell))
            {
                return false;
            }

            TerrainGrid tg = map.terrainGrid;
            return IsTerrainCellDestructible(
                isThickRoof: false,
                top: tg.TopTerrainAt(cell),
                temp: tg.TempTerrainAt(cell),
                under: tg.UnderTerrainAt(cell),
                foundation: tg.FoundationAt(cell),
                allowDestructibleTerrain: true);
        }

        public static bool IsCrushableBuilding(
            Pawn giant,
            CompGiantRig comp,
            Building building,
            bool checkAllied = true)
        {
            if (building == null || !building.Spawned || building.Destroyed || building.Map == null)
            {
                return false;
            }

            if (!building.def.destroyable || !building.def.useHitPoints)
            {
                return false;
            }

            if (IsProtectedSpecialBuilding(building))
            {
                return false;
            }

            bool isDoorOrFence = building is Building_Door || building.def.IsFence;
            bool isBlockingOrEdifice = building.def.passability != Traversability.Standable
                || isDoorOrFence
                || building.def.Fillage != FillCategory.None
                || (building.def.building != null && building.def.building.IsTurret);
            if (!isBlockingOrEdifice)
            {
                return false;
            }

            if (HasThickRoofUnderThing(building.Map, building))
            {
                return false;
            }

            if (!CanGiantCrushAnything(comp, out bool allowBuildings, out bool allowRock, out _))
            {
                return false;
            }

            if (IsNaturalRockOrMineable(building))
            {
                return allowRock;
            }

            if (!allowBuildings)
            {
                return false;
            }

            if (checkAllied && IsProtectedAlliedThing(giant, building))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Pure cost calculator for giant pathfinding cells. Thick-roof cells and
        /// indestructible obstacles always remain impassable (10000).
        /// </summary>
        public static int ComputeBreachDirectCost(
            bool hasThickRoof,
            bool hasIndestructibleBlocker,
            bool hasProtectedAlliedObstacle,
            bool hasCrushableObstacle,
            int vanillaDirectCost,
            int breachExtraCost = DefaultBuildingBreachCost)
        {
            if (hasThickRoof || hasIndestructibleBlocker || hasProtectedAlliedObstacle)
            {
                return ImpassablePathCost;
            }

            if (!hasCrushableObstacle)
            {
                return vanillaDirectCost;
            }

            int baseWalk = vanillaDirectCost >= ImpassablePathCost ? 1 : Math.Max(0, vanillaDirectCost);
            return Math.Min(ImpassablePathCost - 1, baseWalk + Math.Max(1, breachExtraCost));
        }

        public static bool IsCellPassableForAnyDestruction(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map) || HasThickRoofAt(map, cell))
            {
                return false;
            }

            if (!AnyRigSupportsCrushing(map, out bool allowBuildings, out bool allowRock, out bool allowTerrain))
            {
                return false;
            }

            Building edifice = cell.GetEdifice(map);
            if (edifice != null)
            {
                if (!edifice.def.destroyable
                    || !edifice.def.useHitPoints
                    || IsProtectedSpecialBuilding(edifice)
                    || HasThickRoofUnderThing(map, edifice))
                {
                    return false;
                }
                return IsNaturalRockOrMineable(edifice)
                    ? allowRock
                    : allowBuildings;
            }

            return allowTerrain && IsDestructibleImpassableTerrainAt(map, cell);
        }

        public static bool TryEvaluateGiantCell(
            Map map,
            IntVec3 cell,
            Pawn sampleGiant,
            CompGiantRig sampleComp,
            int vanillaDirectCost,
            out bool isHardBlocked,
            out bool isCrushableObstacle,
            out int breachDirectCost)
        {
            bool allowBuildings;
            bool allowRock;
            bool allowTerrain;
            if (sampleComp != null)
            {
                CanGiantCrushAnything(sampleComp, out allowBuildings, out allowRock, out allowTerrain);
            }
            else
            {
                AnyRigSupportsCrushing(map, out allowBuildings, out allowRock, out allowTerrain);
            }

            bool protectAllied = sampleGiant != null
                && sampleGiant.Faction != null
                && !(sampleGiant.mindState?.mentalStateHandler != null && sampleGiant.InAggroMentalState)
                && (sampleComp?.RigDef == null || sampleComp.RigDef.protectAlliedStructures);

            return TryEvaluateGiantCell(
                map,
                cell,
                sampleGiant?.Faction,
                allowBuildings,
                allowRock,
                allowTerrain,
                protectAllied,
                sampleGiant,
                vanillaDirectCost,
                out isHardBlocked,
                out isCrushableObstacle,
                out breachDirectCost);
        }

        public static bool TryEvaluateGiantCell(
            Map map,
            IntVec3 cell,
            Faction giantFaction,
            bool allowBuildings,
            bool allowRock,
            bool allowTerrain,
            bool protectAllied,
            Pawn sampleGiant,
            int vanillaDirectCost,
            out bool isHardBlocked,
            out bool isCrushableObstacle,
            out int breachDirectCost)
        {
            if (map == null || !cell.InBounds(map))
            {
                isHardBlocked = true;
                isCrushableObstacle = false;
                breachDirectCost = ImpassablePathCost;
                return false;
            }

            bool hasThickRoof = HasThickRoofAt(map, cell);
            if (hasThickRoof)
            {
                // Thick rock roof (Overhead Mountain) cells can never be crushed or pathed through.
                isHardBlocked = vanillaDirectCost >= ImpassablePathCost || cell.GetEdifice(map)?.def.passability == Traversability.Impassable;
                isCrushableObstacle = false;
                breachDirectCost = isHardBlocked ? ImpassablePathCost : vanillaDirectCost;
                return false;
            }

            Building edifice = cell.GetEdifice(map);
            if (edifice != null && (edifice.def.passability == Traversability.Impassable || edifice is Building_Door || edifice.def.IsFence))
            {
                bool doorOpenForPawn = edifice is Building_Door door && (door.FreePassage
                    || (sampleGiant != null
                        ? door.PawnCanOpen(sampleGiant)
                        : (giantFaction != null && giantFaction.IsPlayer && (door.Faction == null || !giantFaction.HostileTo(door.Faction)))));
                if (doorOpenForPawn)
                {
                    isHardBlocked = false;
                    isCrushableObstacle = false;
                    breachDirectCost = vanillaDirectCost >= ImpassablePathCost ? 1 : vanillaDirectCost;
                    return false;
                }

                bool canDestroy = edifice.def.destroyable
                    && edifice.def.useHitPoints
                    && !IsProtectedSpecialBuilding(edifice)
                    && !HasThickRoofUnderThing(map, edifice);
                bool rock = IsNaturalRockOrMineable(edifice);
                bool typeAllowed = rock ? allowRock : allowBuildings;
                bool alliedProtected = !rock && protectAllied && IsProtectedAlliedThingForFaction(giantFaction, edifice);

                if (canDestroy && typeAllowed && !alliedProtected)
                {
                    isHardBlocked = false;
                    isCrushableObstacle = true;
                    int hpFactor = Mathf.Clamp(edifice.HitPoints / 45, 0, 80);
                    int baseExtra = rock ? DefaultNaturalRockBreachCost : DefaultBuildingBreachCost;
                    breachDirectCost = ComputeBreachDirectCost(false, false, false, true, vanillaDirectCost, baseExtra + hpFactor);
                    return true;
                }

                bool isBlocking = edifice.def.passability == Traversability.Impassable || (edifice is Building_Door d && !d.Open) || (edifice.def.IsFence && sampleGiant != null && sampleGiant.FenceBlocked);
                isHardBlocked = isBlocking;
                isCrushableObstacle = false;
                breachDirectCost = isHardBlocked ? ImpassablePathCost : vanillaDirectCost;
                return false;
            }

            TerrainGrid tg = map.terrainGrid;
            if (tg != null)
            {
                TerrainDef top = tg.TopTerrainAt(cell);
                TerrainDef temp = tg.TempTerrainAt(cell);
                TerrainDef effective = temp ?? top ?? tg.FoundationAt(cell);
                if (effective != null && effective.passability == Traversability.Impassable)
                {
                    bool destructibleTerrain = allowTerrain && IsTerrainCellDestructible(
                        isThickRoof: false,
                        top: top,
                        temp: temp,
                        under: tg.UnderTerrainAt(cell),
                        foundation: tg.FoundationAt(cell),
                        allowDestructibleTerrain: true);
                    if (destructibleTerrain)
                    {
                        isHardBlocked = false;
                        isCrushableObstacle = true;
                        breachDirectCost = ComputeBreachDirectCost(false, false, false, true, vanillaDirectCost, DefaultTerrainBreachCost);
                        return true;
                    }

                    isHardBlocked = true;
                    isCrushableObstacle = false;
                    breachDirectCost = ImpassablePathCost;
                    return false;
                }
            }

            isHardBlocked = vanillaDirectCost >= ImpassablePathCost;
            isCrushableObstacle = false;
            breachDirectCost = vanillaDirectCost;
            return false;
        }

        public static bool IsCellWalkableOrBreachableForGiant(Map map, Pawn pawn, IntVec3 cell)
        {
            if (map == null || pawn == null || !cell.InBounds(map))
            {
                return false;
            }

            CompGiantRig comp = GiantFastLookup.TryGetRig(pawn);
            if (comp == null)
            {
                return false;
            }

            if (comp.IsAirborneOrTakingOff)
            {
                return GiantFlightUtility.IsCellPassableForFlight(map, comp, cell);
            }

            if (HasThickRoofAt(map, cell))
            {
                return false;
            }

            if (IsDestructibleImpassableTerrainAt(map, cell) && CanGiantCrushAnything(comp, out _, out _, out bool allowTerrain) && allowTerrain)
            {
                return true;
            }

            List<Thing> things = cell.GetThingList(map);
            bool foundObstacle = false;
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Building b && (b.def.passability == Traversability.Impassable || b is Building_Door || b.def.IsFence))
                {
                    if (b is Building_Door door && door.PawnCanOpen(pawn))
                    {
                        continue;
                    }
                    if (!IsCrushableBuilding(pawn, comp, b))
                    {
                        return false;
                    }
                    foundObstacle = true;
                }
            }
            if (foundObstacle)
            {
                return true;
            }
            return map.pathing?.For(pawn)?.pathGrid?.Walkable(cell) ?? cell.Walkable(map);
        }

        public static void CrushObstaclesInCollisionCells(CompGiantRig comp, int damageOverride = -1)
        {
            if (comp == null || comp.Pawn == null || !comp.Pawn.Spawned || comp.Pawn.Dead || comp.Pawn.Downed || comp.IsAirborneOrTakingOff)
            {
                return;
            }

            Map map = comp.Pawn.Map;
            GiantRigDef rig = comp.RigDef;
            if (map == null || rig == null || !CanGiantCrushAnything(comp, out _, out _, out bool allowTerrain))
            {
                return;
            }

            int damage = damageOverride > 0 ? damageOverride : Mathf.Max(1, rig.crushDamagePerStep);

            List<IntVec3> colCells = comp.Footprint.collisionCells;
            if (colCells == null || colCells.Count == 0)
            {
                return;
            }
            var cellSnapshot = new List<IntVec3>(colCells.Count);
            for (int i = 0; i < colCells.Count; i++)
            {
                cellSnapshot.Add(colCells[i]);
            }

            var damagedBuildings = new HashSet<Building>();
            for (int i = 0; i < cellSnapshot.Count; i++)
            {
                IntVec3 c = cellSnapshot[i];
                if (!c.InBounds(map) || HasThickRoofAt(map, c) || GiantArenaService.IsOutsideActiveArenaInnerRect(map, c))
                {
                    continue;
                }

                ApplyCrushAtCell(comp, map, c, damage, allowTerrain, damagedBuildings);
            }
        }

        public static bool EnsureCellClearedForGiantStep(Pawn pawn, IntVec3 cell)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map == null || !cell.InBounds(pawn.Map))
            {
                return false;
            }

            Map map = pawn.Map;
            if (HasThickRoofAt(map, cell))
            {
                return false;
            }

            CompGiantRig comp = GiantFastLookup.TryGetRig(pawn);
            if (comp == null || comp.IsAirborneOrTakingOff || !CanGiantCrushAnything(comp, out _, out _, out bool allowTerrain))
            {
                return false;
            }

            bool clearedAnything = false;
            List<Thing> cellThings = cell.GetThingList(map);
            List<Building> blockingBuildings = null;
            for (int i = 0; i < cellThings.Count; i++)
            {
                if (cellThings[i] is Building b && b.Spawned && !b.Destroyed)
                {
                    bool isBlockingStep = b.def.passability == Traversability.Impassable
                        || (b is Building_Door door && !door.Open && !door.FreePassage && !door.PawnCanOpen(pawn))
                        || b.def.IsFence;
                    if (isBlockingStep && IsCrushableBuilding(pawn, comp, b))
                    {
                        blockingBuildings ??= new List<Building>(2);
                        blockingBuildings.Add(b);
                    }
                }
            }
            if (blockingBuildings != null)
            {
                for (int i = 0; i < blockingBuildings.Count; i++)
                {
                    Building b = blockingBuildings[i];
                    if (b.Spawned && !b.Destroyed && IsCrushableBuilding(pawn, comp, b))
                    {
                        int lethalDamage = Mathf.Max(b.HitPoints + 25, Mathf.Max(1, comp.RigDef.crushDamagePerStep));
                        b.TakeDamage(new DamageInfo(DamageDefOf.Blunt, lethalDamage, armorPenetration: 1f, instigator: pawn));
                        clearedAnything = true;
                    }
                }
            }

            if (allowTerrain && IsDestructibleImpassableTerrainAt(map, cell))
            {
                DestroyTerrainAt(map, cell);
                clearedAnything = true;
            }

            return clearedAnything;
        }

        private static void ApplyCrushAtCell(
            CompGiantRig comp,
            Map map,
            IntVec3 c,
            int damage,
            bool allowTerrain,
            HashSet<Building> alreadyHitBuildings)
        {
            List<Thing> cellThings = c.GetThingList(map);
            List<Building> buildingSnapshot = null;
            bool blockedByThickRoofBuilding = false;
            for (int i = 0; i < cellThings.Count; i++)
            {
                if (cellThings[i] is Building b && b.Spawned && !b.Destroyed)
                {
                    buildingSnapshot ??= new List<Building>(2);
                    buildingSnapshot.Add(b);
                }
            }
            if (buildingSnapshot != null)
            {
                for (int i = 0; i < buildingSnapshot.Count; i++)
                {
                    Building b = buildingSnapshot[i];
                    if (!b.Spawned || b.Destroyed)
                    {
                        continue;
                    }
                    if (HasThickRoofUnderThing(map, b))
                    {
                        blockedByThickRoofBuilding = true;
                        continue;
                    }
                    if (!alreadyHitBuildings.Add(b))
                    {
                        continue;
                    }
                    if (IsCrushableBuilding(comp.Pawn, comp, b))
                    {
                        b.TakeDamage(new DamageInfo(DamageDefOf.Blunt, damage, armorPenetration: 1f, instigator: comp.Pawn));
                        if (!b.Destroyed && b.Spawned && c.InBounds(map))
                        {
                            GiantVfxSystem.SpawnVolumetricSmokeCluster(map, c.ToVector3Shifted(), 0.80f, 18, new Color(0.26f, 0.24f, 0.22f, 1f), new Color(0.56f, 0.52f, 0.47f, 1f), alpha: 0.36f);
                        }
                    }
                }
            }

            if (!blockedByThickRoofBuilding && allowTerrain && IsDestructibleImpassableTerrainAt(map, c))
            {
                DestroyTerrainAt(map, c);
            }
        }

        public static bool DestroyTerrainAt(Map map, IntVec3 cell)
        {
            if (map?.terrainGrid == null || !cell.InBounds(map) || HasThickRoofAt(map, cell))
            {
                return false;
            }

            TerrainGrid tg = map.terrainGrid;
            TerrainDef temp = tg.TempTerrainAt(cell);
            if (temp != null && temp.Removable && temp.passability == Traversability.Impassable)
            {
                tg.RemoveTempTerrain(cell, doLeavings: true);
                GiantVfxSystem.SpawnVolumetricSmokeCluster(map, cell.ToVector3Shifted(), 1.05f, 22, new Color(0.25f, 0.24f, 0.22f, 1f), new Color(0.55f, 0.52f, 0.48f, 1f), alpha: 0.42f);
                return true;
            }

            TerrainDef top = tg.TopTerrainAt(cell);
            TerrainDef foundation = tg.FoundationAt(cell);
            TerrainDef under = tg.UnderTerrainAt(cell);
            if (!IsTerrainCellDestructible(false, top, temp, under, foundation, true))
            {
                return false;
            }

            if (tg.CanRemoveTopLayerAt(cell) && top != null && top.passability == Traversability.Impassable)
            {
                tg.Notify_TerrainDestroyed(cell);
                GiantVfxSystem.SpawnVolumetricSmokeCluster(map, cell.ToVector3Shifted(), 1.05f, 22, new Color(0.25f, 0.24f, 0.22f, 1f), new Color(0.55f, 0.52f, 0.48f, 1f), alpha: 0.42f);
                return true;
            }

            if (tg.CanRemoveFoundationAt(cell) && foundation != null && foundation.passability == Traversability.Impassable)
            {
                tg.Notify_FoundationDestroyed(cell);
                GiantVfxSystem.SpawnVolumetricSmokeCluster(map, cell.ToVector3Shifted(), 1.05f, 22, new Color(0.25f, 0.24f, 0.22f, 1f), new Color(0.55f, 0.52f, 0.48f, 1f), alpha: 0.42f);
                return true;
            }

            if (top != null && top.passability == Traversability.Impassable)
            {
                TerrainDef fallback = (top.burnedDef != null && top.burnedDef.passability != Traversability.Impassable)
                    ? top.burnedDef
                    : TerrainDefOf.Soil;
                tg.SetTerrain(cell, fallback);
                GiantVfxSystem.SpawnVolumetricSmokeCluster(map, cell.ToVector3Shifted(), 1.05f, 22, new Color(0.25f, 0.24f, 0.22f, 1f), new Color(0.55f, 0.52f, 0.48f, 1f), alpha: 0.42f);
                return true;
            }

            return false;
        }
    }
}
