using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Unity.Collections;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace GiantCreatureFramework
{
    [HarmonyPatch]
    public static class HarmonyPatches_CollisionAndSelection
    {
        internal static int LastTargetHighlightFrame = -1;
        internal static int LastTargetHighlightGiantId = -1;

        [ThreadStatic]
        private static bool suppressAutoTakeoffReachCheck;

        [ThreadStatic]
        private static bool suppressGiantTargetReachFallback;

        [ThreadStatic]
        private static HashSet<int> hullReachVisitedIndices;

        private sealed class DegeneratePathRetryState
        {
            public IntVec3 LastDestCell;
            public IntVec3 LastPawnPos;
            public int LastRetryTick;
            public int RetryCount;
        }

        private static readonly ConditionalWeakTable<Pawn_PathFollower, DegeneratePathRetryState> DegenerateOneNodePathRetries = new();
        private const int MaxConsecutiveOneNodePathRetries = 2;
        private const int OneNodeRetryWindowTicks = 90;

        private static readonly MethodInfo FindCurrentMapGetter =
            AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));

        private static readonly MethodInfo GridsGetRoofMethod =
            AccessTools.Method(typeof(GridsUtility), nameof(GridsUtility.GetRoof), new[] { typeof(IntVec3), typeof(Map) });

        private static readonly MethodInfo DrawGiantMouseoverHelper =
            AccessTools.Method(typeof(HarmonyPatches_CollisionAndSelection), nameof(DrawGiantHitCellMouseoverReadout));

        private static readonly Action<PathFinder> PathFinderRecycleGridJobDataAction = CreateRecycleGridJobDataDelegate();

        private static Action<PathFinder> CreateRecycleGridJobDataDelegate()
        {
            MethodInfo mi = AccessTools.Method(typeof(PathFinder), "RecycleGridJobData");
            if (mi == null)
            {
                return null;
            }
            try
            {
                return (Action<PathFinder>)Delegate.CreateDelegate(typeof(Action<PathFinder>), mi);
            }
            catch
            {
                return pf => mi.Invoke(pf, null);
            }
        }

        private static readonly MethodInfo PatherAtDestMethod =
            AccessTools.Method(typeof(Pawn_PathFollower), "AtDestinationPosition");

        private static readonly MethodInfo PatherArrivedMethod =
            AccessTools.Method(typeof(Pawn_PathFollower), "PatherArrived");

        private static readonly MethodInfo PatherFailedMethod =
            AccessTools.Method(typeof(Pawn_PathFollower), "PatherFailed");

        private static readonly MethodInfo PatherTrySetNewPathRequestMethod =
            AccessTools.Method(typeof(Pawn_PathFollower), "TrySetNewPathRequest");

        private static readonly Vector2 MouseoverBotLeft =
            AccessTools.Field(typeof(MouseoverReadout), "BotLeft")?.GetValue(null) is Vector2 v
                ? v
                : new Vector2(15f, 65f);
        /// <summary>
        /// Synchronizes managed collision and breach grids into NativeArrays immediately after ForceCompleteScheduledJobs,
        /// invalidating cached grid job outputs whenever buffers changed so Burst PathGridJob never reads stale costs.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PathFinder), "ForceCompleteScheduledJobs")]
        public static void PathFinder_ForceCompleteScheduledJobs_Postfix(PathFinder __instance, Map ___map)
        {
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
            if (mapComp != null && mapComp.SyncNativeCustomizersSafe())
            {
                PathFinderRecycleGridJobDataAction?.Invoke(__instance);
            }
        }

        /// <summary>
        /// Swaps the Burst PathGridJob direct base-cost array when a giant creature uses a
        /// GiantBreachPathCustomizer so crushable walls, doors, non-thick-roof rock, and
        /// destructible terrain become passable while thick-roof cells remain impassable.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PathFinderMapData), nameof(PathFinderMapData.ParameterizeGridJob))]
        public static void PathFinderMapData_ParameterizeGridJob_Postfix(
            Map ___map,
            PathRequest request,
            ref PathGridJob job)
        {
            if (request?.customizer is GiantBreachPathCustomizer breach && breach.IsCreated)
            {
                job.pathGridDirect = breach.DirectCostGrid;
                GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
                if (mapComp != null && mapComp.TryGetEmptyBitGrid(out NativeBitArray.ReadOnly emptyBits))
                {
                    job.fence = emptyBits;
                }
            }
            else if (request?.customizer is GiantAerialPathCustomizer aerial && aerial.IsCreated)
            {
                job.pathGridDirect = aerial.DirectCostGrid;
                GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
                if (mapComp != null && mapComp.TryGetEmptyBitGrid(out NativeBitArray.ReadOnly emptyBits))
                {
                    job.fence = emptyBits;
                    if (mapComp.TryGetAerialCustomizerKey(aerial, out GiantAerialCustomizerKey aerialKey)
                        && aerialKey.canFlyOverWaterAndLava)
                    {
                        job.water = emptyBits;
                    }
                }
            }
            else if (request?.customizer is GiantAquaticPathCustomizer aquatic && aquatic.IsCreated)
            {
                job.pathGridDirect = aquatic.DirectCostGrid;
                GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
                if (mapComp != null)
                {
                    if (mapComp.TryGetEmptyBitGrid(out NativeBitArray.ReadOnly emptyBits))
                    {
                        job.water = emptyBits;
                        job.fence = emptyBits;
                    }
                    if (mapComp.TryGetEmptyPerceptualCostGrid(out NativeArray<ushort>.ReadOnly emptyPerceptual))
                    {
                        job.perceptualCost = emptyPerceptual;
                    }
                }
            }
        }

        /// <summary>
        /// Prevents NeedNewPath from flagging diagonal corners over deep water, lava, walls, or breachable obstacles
        /// as blocked for airborne, aquatic, or breaching giant creatures (and blocks diagonal corner-cutting across
        /// dry land spits for WaterOnly vessels).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PathUtility), nameof(PathUtility.BlocksDiagonalMovement),
            new[] { typeof(int), typeof(PathingContext), typeof(bool) })]
        public static void PathUtility_BlocksDiagonalMovement_Postfix(
            int index,
            PathingContext pc,
            ref bool __result)
        {
            if (GiantFastLookup.IsEmpty || pc?.map == null)
            {
                return;
            }
            Pawn activeGiant = GiantPathingContext.ActiveGiant;
            if (activeGiant == null)
            {
                return;
            }
            CompGiantRig comp = GiantFastLookup.TryGetRig(activeGiant);
            if (comp == null)
            {
                return;
            }
            IntVec3 cell = pc.map.cellIndices.IndexToCell(index);
            if (comp.IsAirborneOrTakingOff)
            {
                if (__result && GiantFlightUtility.IsCellPassableForFlight(pc.map, comp, cell))
                {
                    __result = false;
                }
                return;
            }
            if (comp.RigDef?.ActiveAquaticLocomotion is GiantAquaticDef aq && aq.IsActive)
            {
                bool passableAq = GiantAquaticUtility.IsCellPassableForAquatic(pc.map, cell, activeGiant, comp, aq);
                if (aq.IsWaterOnly && !passableAq)
                {
                    __result = true;
                    return;
                }
                if (__result && passableAq)
                {
                    __result = false;
                    return;
                }
            }
            if (__result && GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(pc.map, activeGiant, cell))
            {
                __result = false;
            }
        }

        /// <summary>
        /// Keeps ConnectivitySource neighbor links open across destructible, aerial, and aquatic impassable terrain
        /// during incremental map updates; normal non-giant pawns still see cost 10000 in PathGridJob and never traverse them.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ConnectivitySource), "ComputeCellConnectivity")]
        public static void ConnectivitySource_ComputeCellConnectivity_Postfix(
            Map ___map,
            IntVec3 cell,
            ref CellConnection __result)
        {
            if (GiantFastLookup.IsEmpty || ___map == null || __result == CellConnection.AllNeighbours)
            {
                return;
            }
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
            if (mapComp == null || mapComp.ActiveGiants == null || mapComp.ActiveGiants.Count == 0)
            {
                return;
            }

            foreach (CellConnection dir in CellConnection.AllNeighbours)
            {
                if (__result.HasBit(dir))
                {
                    continue;
                }
                IntVec3 neighbor = cell + dir.Offset();
                if (GiantDestructionUtility.IsCellPassableForAnyDestruction(___map, neighbor)
                    || GiantFlightUtility.IsCellPassableForAnyAerialRig(___map, neighbor)
                    || GiantAquaticUtility.IsCellPassableForAnyAquaticRig(___map, neighbor))
                {
                    __result |= dir;
                }
            }
        }

        /// <summary>
        /// Allows breaching giant creatures to path through crushable closed doors by returning
        /// a weighted breach cost instead of ushort.MaxValue.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PathUtility), nameof(PathUtility.GetDoorCost))]
        public static void PathUtility_GetDoorCost_Postfix(
            Building_Door door,
            Pawn pawn,
            ref ushort __result)
        {
            if (door == null || pawn == null)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(pawn) is CompGiantRig comp)
            {
                if (comp.IsAirborneOrTakingOff && GiantFlightUtility.IsCellPassableForFlight(door.Map, comp, door.Position))
                {
                    __result = 0;
                    return;
                }
                if (__result < ushort.MaxValue)
                {
                    return;
                }
                if (GiantDestructionUtility.IsCrushableBuilding(pawn, comp, door))
                {
                    __result = (ushort)Mathf.Clamp(30 + door.HitPoints / 40, 25, 250);
                }
            }
        }

        /// <summary>
        /// Scales giant creature movement ticks down during Distant-Enemy Rush or Airborne Flight.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Normal)]
        [HarmonyPatch(typeof(Pawn), "TicksPerMove", new[] { typeof(bool) })]
        public static void Pawn_TicksPerMove_Postfix(Pawn __instance, ref float __result)
        {
            if (GiantFastLookup.TryGetRig(__instance) is CompGiantRig comp)
            {
                if (comp.IsCurrentlyAirborne)
                {
                    float fMult = comp.GetEffectiveFlightSpeedMultiplier();
                    if (fMult > 0.01f && !Mathf.Approximately(fMult, 1f))
                    {
                        __result = Mathf.Max(1f, __result / fMult);
                    }
                    return;
                }
                if (comp.AquaticLocomotion != null)
                {
                    float aqMult = comp.GetEffectiveAquaticSpeedMultiplier();
                    if (aqMult > 0.0005f && !Mathf.Approximately(aqMult, 1f))
                    {
                        __result = Mathf.Clamp(__result / aqMult, 1f, 600f);
                    }
                }
                if (comp.IsRushing)
                {
                    float mult = comp.GetEffectiveRushSpeedMultiplier();
                    if (mult > 0.01f && !Mathf.Approximately(mult, 1f))
                    {
                        __result = Mathf.Max(1f, __result / mult);
                    }
                }
            }
        }

        /// <summary>
        /// Injects the map's shared GiantCollisionPathCustomizer for normal ground pawns (preserving MapGridRequest
        /// deduplication across pawns!) and GiantSelfClearanceCustomizer for giant creatures themselves.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.CreateRequest),
            new[]
            {
                typeof(IntVec3),
                typeof(LocalTargetInfo),
                typeof(IntVec3?),
                typeof(TraverseParms),
                typeof(PathFinderCostTuning?),
                typeof(PathEndMode),
                typeof(Pawn),
                typeof(PathRequest.IPathGridCustomizer)
            })]
        public static void PathFinder_CreateRequest_Prefix(
            Map ___map,
            IntVec3 start,
            Pawn pawn,
            ref PathRequest.IPathGridCustomizer customizer)
        {
            if (GiantFastLookup.IsEmpty || customizer != null || ___map == null || pawn == null)
            {
                return;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
            if (mapComp == null)
            {
                return;
            }

            customizer = mapComp.GetPathCustomizerFor(pawn, start);
        }

        /// <summary>
        /// Real-time movement collision check: moving ground pawns treat a Giant Creature's animated CollisionCells
        /// as blocked, triggering clean repathing around the creature's limbs/body.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_PathFollower), "WillCollideWithPawnAt")]
        public static void Pawn_PathFollower_WillCollideWithPawnAt_Postfix(
            Pawn ___pawn,
            IntVec3 c,
            ref bool __result)
        {
            if (__result || GiantFastLookup.IsEmpty || ___pawn == null || !___pawn.Spawned ||
                GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(___pawn) ||
                (GiantFastLookup.TryGetRig(___pawn)?.IsCurrentlyAirborne ?? false))
            {
                return;
            }
            // Never report own Position as blocked (avoids vanilla unstuck teleporter on giant anchor cells).
            if (c == ___pawn.Position)
            {
                return;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___pawn.Map);
            if (mapComp != null && mapComp.IsBlockedForPawn(c, ___pawn))
            {
                __result = true;
            }
        }

        /// <summary>
        /// Worker-thread-safe giant-to-giant hull avoidance injector for RimWorld 1.7 per-request pathfinding.
        /// Stamps Minkowski overlap penalties into providerCost from the immutable GiantPathAvoidanceSnapshot.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PathGridDoorsBlockedJob), nameof(PathGridDoorsBlockedJob.Execute))]
        public static void PathGridDoorsBlockedJob_Execute_Postfix(
            Map ___map,
            Pawn ___pawn,
            Unity.Collections.NativeArray<ushort> ___providerCost)
        {
            if (___map == null || ___pawn == null ||
                GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(___pawn) ||
                !___providerCost.IsCreated ||
                (GiantFastLookup.TryGetRig(___pawn)?.IsCurrentlyAirborne ?? false))
            {
                return;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
            mapComp?.ApplyPerRequestGiantAvoidance(___pawn, ___providerCost);
        }

        /// <summary>
        /// Ensures RimWorld's PawnUtility.PawnBlockingPathAt recognizes multi-cell giant CollisionCells
        /// for both small pawns and other giants (except on the requester's own Position).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.ShouldCollideWithPawns))]
        public static void PawnUtility_ShouldCollideWithPawns_Postfix(Pawn p, ref bool __result)
        {
            if (!__result || p == null)
            {
                return;
            }
            CompGiantRig rig = GiantFastLookup.TryGetRig(p);
            if (rig != null && (rig.IsCurrentlyAirborne || rig.IsFlyingEntry))
            {
                __result = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PawnUtility), nameof(PawnUtility.PawnBlockingPathAt),
            new[] { typeof(IntVec3), typeof(Pawn), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
        public static void PawnUtility_PawnBlockingPathAt_Postfix(
            IntVec3 c,
            Pawn forPawn,
            bool collideOnlyWithStandingPawns,
            ref Pawn __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            if (__result != null && GiantFastLookup.TryGetRig(__result) is CompGiantRig resRig &&
                (resRig.IsCurrentlyAirborne || resRig.IsFlyingEntry))
            {
                __result = null;
            }
            if (__result != null || forPawn == null || !forPawn.Spawned ||
                GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(forPawn) ||
                forPawn.Map == null ||
                (GiantFastLookup.TryGetRig(forPawn) is CompGiantRig forRig && (forRig.IsCurrentlyAirborne || forRig.IsFlyingEntry)))
            {
                return;
            }
            if (c == forPawn.Position)
            {
                return;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(forPawn.Map);
            if (mapComp == null || !mapComp.HasAnyGiants)
            {
                return;
            }

            CompGiantRig blocker;
            if (GiantFastLookup.TryGetRig(forPawn) is CompGiantRig moverComp)
            {
                if (moverComp.RigDef != null && !moverComp.RigDef.enableGiantToGiantCollision)
                {
                    return;
                }
                IntVec3 curAnchor = forPawn.Position;
                blocker = mapComp.FindBlockingGiantForGiantHullAt(moverComp, curAnchor, c, out _, out _);
            }
            else
            {
                blocker = mapComp.GetBlockingGiantForSmallPawnAt(c, forPawn);
            }

            if (blocker?.Pawn != null)
            {
                if (collideOnlyWithStandingPawns && blocker.Pawn.pather != null && blocker.Pawn.pather.MovingNow)
                {
                    return;
                }
                __result = blocker.Pawn;
            }
        }

        /// <summary>
        /// Triggers fast flanking repath when a moving giant is blocked by another giant,
        /// or engages an immediately reachable hostile giant blocking its step.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick))]
        public static void Pawn_PathFollower_PatherTick_Postfix(
            Pawn ___pawn,
            Pawn_PathFollower __instance)
        {
            if (__instance == null || !__instance.Moving || !__instance.WillCollideNextCell || ___pawn == null || !___pawn.Spawned || ___pawn.Flying)
            {
                return;
            }
            if (__instance.nextCell == ___pawn.Position)
            {
                return;
            }

            CompGiantRig moverComp = GiantFastLookup.TryGetRig(___pawn);
            if (moverComp?.RigDef == null || moverComp.IsCurrentlyAirborne || !moverComp.RigDef.enableGiantToGiantCollision)
            {
                return;
            }

            int nowTick = Find.TickManager?.TicksGame ?? 0;
            if (nowTick - __instance.LastMovedTick < 10)
            {
                return;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___pawn.Map);
            if (mapComp == null || !mapComp.HasAnyGiants)
            {
                return;
            }

            IntVec3 curAnchor = ___pawn.Position;
            CompGiantRig blocker = mapComp.FindBlockingGiantForGiantHullAt(moverComp, curAnchor, __instance.nextCell, out _, out _);
            if (blocker?.Pawn == null)
            {
                return;
            }

            if (___pawn.HostileTo(blocker.Pawn))
            {
                moverComp.PartCombat?.NotifyCollisionHostileTarget(blocker.Pawn);
            }

            if (mapComp.ShouldAllowFlankRepathTick(___pawn, nowTick, cooldownTicks: 24))
            {
                __instance.ResetToCurrentPosition();
            }
        }

        /// <summary>
        /// Prevents other pawns/items from treating solid Giant Creature CollisionCells as Standable destination cells.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.StandableBy))]
        public static void GenGrid_StandableBy_Postfix(IntVec3 c, Map map, Pawn pawn, ref bool __result)
        {
            if (map == null)
            {
                return;
            }
            if (pawn != null && !GiantFastLookup.IsEmpty && GiantFastLookup.TryGetRig(pawn) is CompGiantRig pawnRig)
            {
                if (pawnRig.IsAirborneOrTakingOff)
                {
                    if (!__result && GiantPathingContext.IsWalkableOverrideActive(pawn)
                        && GiantFlightUtility.IsCellPassableForFlight(map, pawnRig, c))
                    {
                        __result = true;
                    }
                    return;
                }
                if (pawnRig.RigDef?.ActiveAquaticLocomotion is GiantAquaticDef aq && aq.IsActive)
                {
                    bool passableAq = GiantAquaticUtility.IsCellPassableForAquatic(map, c, pawn, pawnRig, aq);
                    if (aq.IsWaterOnly && !passableAq)
                    {
                        __result = false;
                        return;
                    }
                    if (!__result && passableAq)
                    {
                        __result = true;
                    }
                }
            }
            if (!__result)
            {
                return;
            }
            if (pawn != null && (GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(pawn) || (pawn.Spawned && c == pawn.Position)))
            {
                return;
            }
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp != null && mapComp.IsStandBlockedForPawn(c, pawn))
            {
                __result = false;
            }
        }

        /// <summary>
        /// Prevents placing building blueprints directly on cells currently occupied by a Giant Creature's collision body.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt_NewTemp))]
        public static void GenConstruct_CanPlaceBlueprintAt_Postfix(
            BuildableDef entDef,
            IntVec3 center,
            Rot4 rot,
            Map map,
            bool godMode,
            ref AcceptanceReport __result)
        {
            if (GiantFastLookup.IsEmpty || !__result.Accepted || godMode || map == null || entDef == null)
            {
                return;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp == null || !mapComp.HasAnyGiants)
            {
                return;
            }

            foreach (IntVec3 c in GenAdj.OccupiedRect(center, rot, entDef.Size))
            {
                CompGiantRig owner = mapComp.GetCollisionOwnerAt(c);
                if (owner?.Pawn != null)
                {
                    __result = new AcceptanceReport("SpaceAlreadyOccupied".Translate());
                    return;
                }
            }
        }

        /// <summary>
        /// Allows left-click selection, hover inspection, and right-click targeting/FloatMenus on ANY map cell
        /// covered by a Giant Creature's texture-mapped HitCells, prioritizing HitCells over ground clutter.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenUI), nameof(GenUI.ThingsUnderMouse))]
        public static void GenUI_ThingsUnderMouse_Postfix(
            Vector3 clickPos,
            TargetingParameters clickParams,
            ITargetingSource source,
            List<Thing> __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            Map map = Find.CurrentMap;
            if (map == null || __result == null)
            {
                return;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp == null || (!mapComp.HasAnyGiants && !mapComp.HasAnyCorpseGiants))
            {
                return;
            }

            IntVec3 mouseCell = IntVec3.FromVector3(clickPos);
            if (!mouseCell.InBounds(map) || mouseCell.Fogged(map))
            {
                return;
            }

            List<CompGiantRig> giantsAtCell = mapComp.GetHitGiantsAt(mouseCell);
            List<CompGiantRig> corpsesAtCell = mapComp.GetCorpseGiantsAt(mouseCell);
            List<CompGiantRig> elevatedAirborneHits = null;
            IReadOnlyList<CompGiantRig> activeRigs = mapComp.ActiveGiants;
            for (int i = 0; i < activeRigs.Count; i++)
            {
                CompGiantRig ar = activeRigs[i];
                if (ar?.Pawn == null || !ar.Pawn.Spawned || !ar.IsCurrentlyAirborne)
                {
                    continue;
                }
                Vector2 visOff = ar.CurrentFlightVisualOffsetXZ;
                IntVec3 groundEquiv = IntVec3.FromVector3(clickPos - new Vector3(visOff.x, 0f, visOff.y));
                if (groundEquiv.InBounds(map)
                    && !groundEquiv.Fogged(map)
                    && ar.Footprint.hitCellSet.Contains(groundEquiv)
                    && (giantsAtCell == null || !giantsAtCell.Contains(ar)))
                {
                    elevatedAirborneHits ??= new List<CompGiantRig>(2);
                    elevatedAirborneHits.Add(ar);
                }
            }
            if ((giantsAtCell == null || giantsAtCell.Count == 0)
                && (corpsesAtCell == null || corpsesAtCell.Count == 0)
                && (elevatedAirborneHits == null || elevatedAirborneHits.Count == 0))
            {
                return;
            }

            // Preserve close-radius (< 0.4f) non-giant pawns first so small pawns beside a limb remain clickable,
            // while placing hit Giant Creatures ahead of plants, chunks, items, floors, and buildings.
            int insertIdx = 0;
            while (insertIdx < __result.Count)
            {
                if (__result[insertIdx] is Pawn existingPawn &&
                    (existingPawn.DrawPos - clickPos).MagnitudeHorizontal() < 0.4f)
                {
                    insertIdx++;
                    continue;
                }
                break;
            }

            if (giantsAtCell != null)
            {
                for (int i = 0; i < giantsAtCell.Count; i++)
                {
                    CompGiantRig comp = giantsAtCell[i];
                    Pawn giant = comp?.Pawn;
                    if (giant == null || !giant.Spawned || giant.IsHiddenFromPlayer() || __result.Contains(giant))
                    {
                        continue;
                    }
                    if (source != null && source.Caster == giant && clickParams != null && !clickParams.canTargetSelf)
                    {
                        continue;
                    }
                    if (clickParams != null && !clickParams.CanTarget(giant, source))
                    {
                        continue;
                    }

                    // Prefer placing an exact alpha-mask hit ahead of a cell-only overlap when multiple giants stack.
                    bool exactPixelHit = comp.Footprint.TryHitTestPoint(comp.RigDef, comp.AlphaMasksSpan, clickPos, out _);
                    int targetIndex = exactPixelHit ? insertIdx : Math.Min(__result.Count, insertIdx);
                    __result.Insert(targetIndex, giant);
                    if (exactPixelHit)
                    {
                        insertIdx++;
                    }
                }
            }

            if (elevatedAirborneHits != null)
            {
                for (int i = 0; i < elevatedAirborneHits.Count; i++)
                {
                    CompGiantRig comp = elevatedAirborneHits[i];
                    Pawn giant = comp?.Pawn;
                    if (giant == null || !giant.Spawned || giant.IsHiddenFromPlayer() || __result.Contains(giant))
                    {
                        continue;
                    }
                    if (source != null && source.Caster == giant && clickParams != null && !clickParams.canTargetSelf)
                    {
                        continue;
                    }
                    if (clickParams != null && !clickParams.CanTarget(giant, source))
                    {
                        continue;
                    }
                    Vector2 visOff = comp.CurrentFlightVisualOffsetXZ;
                    Vector3 groundEquivWorld = clickPos - new Vector3(visOff.x, 0f, visOff.y);
                    if (comp.RigDef != null && !comp.Footprint.TryHitTestPoint(comp.RigDef, comp.AlphaMasksSpan, groundEquivWorld, out _))
                    {
                        continue;
                    }
                    __result.Insert(Math.Min(__result.Count, insertIdx), giant);
                    insertIdx++;
                }
            }

            if (corpsesAtCell != null)
            {
                for (int i = 0; i < corpsesAtCell.Count; i++)
                {
                    CompGiantRig comp = corpsesAtCell[i];
                    Corpse corpse = comp?.Pawn?.Corpse;
                    if (corpse == null || !corpse.Spawned || (corpse.InnerPawn != null && corpse.InnerPawn.IsHiddenFromPlayer()) || __result.Contains(corpse))
                    {
                        continue;
                    }
                    if (clickParams != null && !clickParams.CanTarget(corpse, source))
                    {
                        continue;
                    }
                    int targetIndex = Math.Min(__result.Count, insertIdx);
                    __result.Insert(targetIndex, corpse);
                }
            }
        }

        /// <summary>
        /// Displays Giant Creature names in the bottom-left MouseoverReadout when hovering any of their HitCells.
        /// </summary>
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(MouseoverReadout), nameof(MouseoverReadout.MouseoverReadoutOnGUI))]
        public static IEnumerable<CodeInstruction> MouseoverReadout_MouseoverReadoutOnGUI_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            bool patched = false;

            for (int i = 0; i < codes.Count - 2; i++)
            {
                if (codes[i].opcode == OpCodes.Ldloc_0 &&
                    codes[i + 1].Calls(FindCurrentMapGetter) &&
                    codes[i + 2].Calls(GridsGetRoofMethod))
                {
                    var injected = new List<CodeInstruction>
                    {
                        new CodeInstruction(OpCodes.Ldloc_0),
                        new CodeInstruction(OpCodes.Ldloca_S, (byte)1),
                        new CodeInstruction(OpCodes.Ldloc_S, (byte)4),
                        new CodeInstruction(OpCodes.Call, DrawGiantMouseoverHelper)
                    };
                    if (codes[i].labels.Count > 0)
                    {
                        injected[0].labels.AddRange(codes[i].labels);
                        codes[i].labels.Clear();
                    }
                    codes.InsertRange(i, injected);
                    patched = true;
                    break;
                }
            }

            if (!patched)
            {
                Log.Warning("[GiantCreatureFramework] Failed to locate GetRoof anchor in MouseoverReadoutOnGUI transpiler.");
            }
            return codes;
        }

        private static void DrawGiantHitCellMouseoverReadout(IntVec3 cell, ref float curY, List<Thing> thingList)
        {
            Map map = Find.CurrentMap;
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp == null || (!mapComp.HasAnyGiants && !mapComp.HasAnyCorpseGiants))
            {
                return;
            }

            List<CompGiantRig> giants = mapComp.GetHitGiantsAt(cell);
            if (giants != null)
            {
                for (int i = 0; i < giants.Count; i++)
                {
                    Pawn giant = giants[i]?.Pawn;
                    if (giant == null || !giant.Spawned || giant.IsHiddenFromPlayer())
                    {
                        continue;
                    }
                    if (thingList != null && thingList.Contains(giant))
                    {
                        continue;
                    }

                    Rect rect = new Rect(
                        MouseoverBotLeft.x,
                        UI.screenHeight - MouseoverBotLeft.y - curY,
                        999f,
                        999f);
                    Widgets.Label(rect, giant.LabelMouseover);
                    curY += 19f;
                }
            }

            IReadOnlyList<CompGiantRig> activeRigs = mapComp.ActiveGiants;
            if (activeRigs != null && activeRigs.Count > 0)
            {
                Vector3 mouseWorld = UI.MouseMapPosition();
                for (int i = 0; i < activeRigs.Count; i++)
                {
                    CompGiantRig ar = activeRigs[i];
                    Pawn giant = ar?.Pawn;
                    if (giant == null || !giant.Spawned || !ar.IsCurrentlyAirborne || giant.IsHiddenFromPlayer())
                    {
                        continue;
                    }
                    if (giants != null && giants.Contains(ar))
                    {
                        continue;
                    }
                    if (thingList != null && thingList.Contains(giant))
                    {
                        continue;
                    }
                    Vector2 visOff = ar.CurrentFlightVisualOffsetXZ;
                    IntVec3 groundEquiv = IntVec3.FromVector3(mouseWorld - new Vector3(visOff.x, 0f, visOff.y));
                    if (!groundEquiv.InBounds(map) || groundEquiv.Fogged(map) || !ar.Footprint.hitCellSet.Contains(groundEquiv))
                    {
                        continue;
                    }

                    Rect rect = new Rect(
                        MouseoverBotLeft.x,
                        UI.screenHeight - MouseoverBotLeft.y - curY,
                        999f,
                        999f);
                    Widgets.Label(rect, giant.LabelMouseover);
                    curY += 19f;
                }
            }

            List<CompGiantRig> corpses = mapComp.GetCorpseGiantsAt(cell);
            if (corpses != null)
            {
                for (int i = 0; i < corpses.Count; i++)
                {
                    Corpse corpse = corpses[i]?.Pawn?.Corpse;
                    if (corpse == null || !corpse.Spawned || (corpse.InnerPawn != null && corpse.InnerPawn.IsHiddenFromPlayer()))
                    {
                        continue;
                    }
                    if (thingList != null && thingList.Contains(corpse))
                    {
                        continue;
                    }

                    Rect rect = new Rect(
                        MouseoverBotLeft.x,
                        UI.screenHeight - MouseoverBotLeft.y - curY,
                        999f,
                        999f);
                    Widgets.Label(rect, corpse.LabelMouseover);
                    curY += 19f;
                }
            }
        }

        /// <summary>
        /// Draws the targeting reticle on the hovered/preferred HitCell and outlines all HitCells of a targeted Giant Creature.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GenDraw), "DrawTargetingHighlight_Thing")]
        public static bool GenDraw_DrawTargetingHighlight_Thing_Prefix(Thing t)
        {
            if (!(t is Pawn giant) || !giant.Spawned || !(GiantFastLookup.TryGetRig(giant) is CompGiantRig comp))
            {
                return true;
            }

            if (comp.Footprint.hitCellPositions.Count == 0)
            {
                return true;
            }

            LastTargetHighlightFrame = Time.frameCount;
            LastTargetHighlightGiantId = giant.thingIDNumber;

            bool hostile = giant.HostileTo(Faction.OfPlayer);
            Color outlineColor = hostile
                ? new Color(1f, 0.35f, 0.25f, 0.55f)
                : new Color(0.45f, 0.9f, 1f, 0.55f);
            GenDraw.DrawFieldEdges(comp.Footprint.hitCellPositions, outlineColor);

            IntVec3 mouseCell = UI.MouseCell();
            IntVec3 highlightCell;
            if (comp.Footprint.hitCellSet.Contains(mouseCell))
            {
                highlightCell = mouseCell;
            }
            else
            {
                Thing shooter = Find.Selector?.SingleSelectedThing;
                IntVec3 shooterPos = shooter != null ? shooter.Position : giant.Position;
                highlightCell = GiantTargetAimTracker.ResolvePreferredHitCell(giant, comp, shooterPos, shooter);
            }

            if (highlightCell.IsValid)
            {
                GenDraw.DrawTargetHighlightWithLayer(highlightCell, AltitudeLayer.MapDataOverlay);
            }
            else
            {
                Vector3 center = giant.TrueCenter();
                GenDraw.DrawTargetHighlightWithLayer(center, AltitudeLayer.MapDataOverlay);
            }

            TargetHighlighter.Highlight(giant, arrow: false);
            return false;
        }

        /// <summary>
        /// Ensures multi-cell drag-box selection picks up a Giant Creature even when dragging only over its claw or tail!
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ThingSelectionUtility), nameof(ThingSelectionUtility.SelectableByMapClick))]
        public static void ThingSelectionUtility_SelectableByMapClick_Postfix(Thing t, ref bool __result)
        {
            if (__result || t == null || !t.Spawned || !t.def.selectable || ((t as Pawn ?? (t as Corpse)?.InnerPawn)?.IsHiddenFromPlayer() == true))
            {
                return;
            }
            CompGiantRig comp = GiantFastLookup.TryGetRig(t as Pawn)
                ?? HarmonyPatches_CombatAndHitbox.TryGetEnabledCorpseRig(t);
            if (comp != null && t.Map != null)
            {
                List<HitCellEntry> cells = comp.Footprint.hitCells;
                for (int i = 0; i < cells.Count; i++)
                {
                    if (!cells[i].cell.Fogged(t.Map))
                    {
                        __result = true;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Extends drag-box selection to pick up Giant Creatures when dragging over any of their outer HitCells
        /// (such as extended pincers or tail stinger), even when the Pawn's center Position cell is outside the drag rect.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ThingSelectionUtility), nameof(ThingSelectionUtility.MultiSelectableThingsInScreenRectDistinct))]
        public static IEnumerable<Thing> MultiSelectableThingsInScreenRectDistinct_Postfix(
            IEnumerable<Thing> __result,
            Rect rect)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return __result;
            }
            Map map = Find.CurrentMap;
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp == null || (!mapComp.HasAnyGiants && !mapComp.HasAnyCorpseGiants))
            {
                return __result;
            }
            return MultiSelectableThingsWithGiantsIterator(__result, rect, map, mapComp);
        }

        private static IEnumerable<Thing> MultiSelectableThingsWithGiantsIterator(
            IEnumerable<Thing> baseResult,
            Rect rect,
            Map map,
            GiantFootprintMapComponent mapComp)
        {
            var yielded = new HashSet<Thing>();
            foreach (Thing t in baseResult)
            {
                yielded.Add(t);
                yield return t;
            }

            Vector2 topLeft = new Vector2(rect.x, UI.screenHeight - rect.y);
            Vector2 bottomRight = new Vector2(rect.x + rect.width, UI.screenHeight - (rect.y + rect.height));
            Vector3 worldTL = UI.UIToMapPosition(topLeft);
            Vector3 worldBR = UI.UIToMapPosition(bottomRight);
            CellRect mapRect = new CellRect
            {
                minX = Mathf.FloorToInt(worldTL.x),
                minZ = Mathf.FloorToInt(worldBR.z),
                maxX = Mathf.FloorToInt(worldBR.x),
                maxZ = Mathf.FloorToInt(worldTL.z)
            };

            IReadOnlyList<CompGiantRig> rigs = mapComp.ActiveGiants;
            for (int i = 0; i < rigs.Count; i++)
            {
                CompGiantRig comp = rigs[i];
                Pawn giant = comp?.Pawn;
                if (giant == null || !giant.Spawned || giant.def.neverMultiSelect || yielded.Contains(giant))
                {
                    continue;
                }
                if (!ThingSelectionUtility.SelectableByMapClick(giant))
                {
                    continue;
                }

                bool checkAirborneOffset = comp.IsCurrentlyAirborne;
                CellRect shiftedMapRect = default;
                if (checkAirborneOffset)
                {
                    Vector2 visOff = comp.CurrentFlightVisualOffsetXZ;
                    shiftedMapRect = new CellRect
                    {
                        minX = Mathf.FloorToInt(worldTL.x - visOff.x),
                        minZ = Mathf.FloorToInt(worldBR.z - visOff.y),
                        maxX = Mathf.FloorToInt(worldBR.x - visOff.x),
                        maxZ = Mathf.FloorToInt(worldTL.z - visOff.y)
                    };
                }

                List<HitCellEntry> hitCells = comp.Footprint.hitCells;
                for (int c = 0; c < hitCells.Count; c++)
                {
                    IntVec3 hc = hitCells[c].cell;
                    if (hc.InBounds(map)
                        && !hc.Fogged(map)
                        && (mapRect.Contains(hc) || (checkAirborneOffset && shiftedMapRect.Contains(hc))))
                    {
                        yielded.Add(giant);
                        yield return giant;
                        break;
                    }
                }
            }

            IReadOnlyList<CompGiantRig> corpseRigs = mapComp.ActiveCorpseGiants;
            for (int i = 0; i < corpseRigs.Count; i++)
            {
                CompGiantRig comp = corpseRigs[i];
                if (comp?.RigDef != null && !comp.RigDef.enableCorpseFootprint)
                {
                    continue;
                }
                Corpse corpse = comp?.Pawn?.Corpse;
                if (corpse == null || !corpse.Spawned || corpse.def.neverMultiSelect || yielded.Contains(corpse))
                {
                    continue;
                }
                if (!ThingSelectionUtility.SelectableByMapClick(corpse))
                {
                    continue;
                }

                List<HitCellEntry> hitCells = comp.Footprint.hitCells;
                for (int c = 0; c < hitCells.Count; c++)
                {
                    IntVec3 hc = hitCells[c].cell;
                    if (mapRect.Contains(hc) && hc.InBounds(map) && !hc.Fogged(map))
                    {
                        yielded.Add(corpse);
                        yield return corpse;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Re-registers giant corpses and rebuilds their multi-cell footprint whenever a corpse spawns on a map
        /// (including after hauling/dropping or loading).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Corpse), nameof(Corpse.SpawnSetup))]
        public static void Corpse_SpawnSetup_Postfix(Corpse __instance, Map map, bool respawningAfterLoad)
        {
            if (__instance == null || !__instance.Spawned || map == null || __instance.InnerPawn == null)
            {
                return;
            }
            CompGiantRig rigComp = GiantFastLookup.TryGetRig(__instance.InnerPawn);
            if (rigComp == null || rigComp.RigDef == null)
            {
                return;
            }
            if (respawningAfterLoad)
            {
                rigComp.RestoreCorpseStateAfterLoad(map);
            }
            else
            {
                rigComp.RebuildFootprintNow(notifyMap: false);
            }
            GiantFootprintMapComponent.GetFast(map)?.RegisterCorpseFootprint(__instance.InnerPawn, rigComp);
        }

        /// <summary>
        /// Extends vanilla center-cell-only PathEndModes (InteractionCell / ClosestTouch / OnCell) targeting
        /// a multi-cell Giant Creature or giant Corpse to PathEndMode.Touch so WorkGiver_Tend, WorkGiver_FeedPatient,
        /// WorkGiver_Tame/Train/Slaughter, and all vanilla JobDrivers evaluate all HitCells instead of Position alone.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GenPath), nameof(GenPath.ResolvePathMode))]
        public static bool GenPath_ResolvePathMode_Prefix(
            Pawn pawn,
            TargetInfo dest,
            ref PathEndMode peMode,
            ref TargetInfo __result)
        {
            if (GiantFastLookup.IsEmpty || !dest.HasThing || dest.Thing == null || !dest.Thing.Spawned || pawn == dest.Thing)
            {
                return true;
            }
            if ((peMode == PathEndMode.InteractionCell || peMode == PathEndMode.ClosestTouch || peMode == PathEndMode.OnCell)
                && GiantHullInteractionUtility.TryGetInteractableGiantRig(dest.Thing) != null)
            {
                peMode = PathEndMode.Touch;
                __result = dest;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Falls back from PathEndMode.OnCell to PathEndMode.Touch when a ClosestTouch target
        /// (e.g., dropped item, filth, or downed pawn) rests inside a live giant creature's collision cells.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenPath), nameof(GenPath.ResolveClosestTouchPathMode))]
        public static void GenPath_ResolveClosestTouchPathMode_Postfix(
            Pawn pawn,
            Map map,
            IntVec3 target,
            ref PathEndMode __result)
        {
            if (__result == PathEndMode.Touch || GiantFastLookup.IsEmpty || map == null || !target.IsValid)
            {
                return;
            }
            if (pawn != null && (GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(pawn) || (pawn.Spawned && target == pawn.Position)))
            {
                return;
            }
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp != null && mapComp.IsBlockedForPawn(target, pawn))
            {
                __result = PathEndMode.Touch;
            }
        }

        /// <summary>
        /// Pushes GiantPathingContext while Reachability checks run and evaluates breach reachability
        /// across destructible walls, doors, non-thick-roof rocks, and removable terrain when regional
        /// reachability failed.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach),
            new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
        public static void Reachability_CanReach_Prefix(
            TraverseParms traverseParams,
            out GiantPathingContext.Scope __state)
        {
            __state = GiantPathingContext.Enter(traverseParams.pawn);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach),
            new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
        public static void Reachability_CanReach_Postfix(
            Map ___map,
            IntVec3 start,
            LocalTargetInfo dest,
            PathEndMode peMode,
            TraverseParms traverseParams,
            ref bool __result)
        {
            if (!GiantFastLookup.IsEmpty && __result && ___map != null && dest.IsValid)
            {
                Pawn checkPawn = traverseParams.pawn ?? GiantPathingContext.ActiveGiant;
                if (checkPawn != null && GiantFastLookup.TryGetRig(checkPawn) is CompGiantRig wComp && wComp.IsWaterOnlySurfaceBoundNow)
                {
                    GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
                    __result = mapComp != null && mapComp.CanAquaticReach(checkPawn, wComp, start, dest, peMode);
                    return;
                }
            }

            if (!__result && ___map != null && dest.IsValid)
            {
                Pawn giant = traverseParams.pawn ?? GiantPathingContext.ActiveGiant;
                if (giant != null && GiantFastLookup.TryGetRig(giant) is CompGiantRig comp)
                {
                    GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
                    if (mapComp != null)
                    {
                        bool canFlyReach = !suppressAutoTakeoffReachCheck &&
                            (comp.IsAirborneOrTakingOff ||
                             (comp.FlightLocomotion != null
                              && comp.FlightLocomotion.Def.flightMode == GiantFlightMode.TakeoffAndLanding
                              && comp.FlightLocomotion.Def.autoTakeoffWhenPathBlocked
                              && comp.FlightLocomotion.CanMaintainLift
                              && (!comp.FlightLocomotion.Def.blockedByThickRoof || !GiantDestructionUtility.HasThickRoofAt(___map, start))));
                        if (canFlyReach && mapComp.CanAerialReach(giant, comp, start, dest, peMode))
                        {
                            __result = true;
                            return;
                        }
                        if (!comp.IsAirborneOrTakingOff
                            && comp.RigDef?.ActiveAquaticLocomotion is GiantAquaticDef aq
                            && aq.IsActive
                            && mapComp.CanAquaticReach(giant, comp, start, dest, peMode))
                        {
                            __result = true;
                            return;
                        }
                        if (!comp.IsWaterOnlySurfaceBoundNow
                            && GiantDestructionUtility.CanGiantCrushAnything(comp, out _, out _, out _)
                            && mapComp.CanBreachReach(giant, start, dest, peMode))
                        {
                            __result = true;
                            return;
                        }
                    }
                }

                if (!suppressGiantTargetReachFallback
                    && dest.HasThing
                    && dest.Thing != null
                    && dest.Thing.Spawned
                    && dest.Thing.Map == ___map
                    && GiantHullInteractionUtility.IsGiantTouchCompatiblePathEndMode(peMode)
                    && GiantHullInteractionUtility.TryGetInteractableGiantRig(dest.Thing) != null)
                {
                    Pawn smallActor = traverseParams.pawn ?? GiantPathingContext.ActiveGiant;
                    if (smallActor == dest.Thing)
                    {
                        return;
                    }
                    if (GiantHullInteractionUtility.IsCellValidSmallPawnTouch(___map, smallActor, dest.Thing, start))
                    {
                        __result = true;
                        return;
                    }
                    if (smallActor == null || GiantFastLookup.TryGetRig(smallActor) == null)
                    {
                        suppressGiantTargetReachFallback = true;
                        try
                        {
                            if (CanReachAnyGiantHullCellFromStart(___map, start, dest.Thing, smallActor, traverseParams))
                            {
                                __result = true;
                            }
                        }
                        finally
                        {
                            suppressGiantTargetReachFallback = false;
                        }
                    }
                }
            }
        }

        private static bool CanReachAnyGiantHullCellFromStart(
            Map map,
            IntVec3 start,
            Thing giantOrCorpse,
            Pawn smallActor,
            TraverseParms traverseParams)
        {
            if (map == null || !start.InBounds(map) || giantOrCorpse == null || !giantOrCorpse.Spawned)
            {
                return false;
            }
            CompGiantRig rig = GiantHullInteractionUtility.TryGetInteractableGiantRig(giantOrCorpse);
            if (rig == null)
            {
                return false;
            }

            bool isLiveStandingGiant = giantOrCorpse is Pawn gp && !gp.Dead && !gp.Downed && !rig.IsCurrentlyAirborne && !rig.IsFlyingEntry;
            bool actorIgnoresCollision = smallActor != null && GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(smallActor);
            HashSet<IntVec3> blockedCollision = (isLiveStandingGiant && !actorIgnoresCollision) ? rig.Footprint.collisionCellSet : null;
            HashSet<IntVec3> blockedCanopy = (isLiveStandingGiant && !actorIgnoresCollision) ? rig.Footprint.pawnExclusionCellSet : null;

            hullReachVisitedIndices ??= new HashSet<int>(256);
            CellIndices indices = map.cellIndices;
            IntVec3 mapSize = map.Size;

            bool RunReachPass(bool allowCanopyFallback)
            {
                hullReachVisitedIndices.Clear();

                bool TryCandidate(IntVec3 cand)
                {
                    if ((uint)cand.x >= (uint)mapSize.x || (uint)cand.z >= (uint)mapSize.z)
                    {
                        return false;
                    }
                    int idx = indices.CellToIndex(cand);
                    if (!hullReachVisitedIndices.Add(idx))
                    {
                        return false;
                    }
                    if (blockedCollision != null && blockedCollision.Contains(cand))
                    {
                        return false;
                    }
                    if (!allowCanopyFallback && blockedCanopy != null && blockedCanopy.Contains(cand))
                    {
                        return false;
                    }
                    bool passable = allowCanopyFallback
                        ? (smallActor != null ? cand.WalkableBy(map, smallActor) : cand.Walkable(map))
                        : (smallActor != null ? cand.StandableBy(map, smallActor) : cand.Standable(map));
                    if (!passable)
                    {
                        return false;
                    }
                    return map.reachability.CanReach(start, cand, PathEndMode.OnCell, traverseParams);
                }

                bool TryAround(IntVec3 center)
                {
                    if (TryCandidate(center))
                    {
                        return true;
                    }
                    for (int d = 0; d < 8; d++)
                    {
                        if (TryCandidate(center + GenAdj.AdjacentCells[d]))
                        {
                            return true;
                        }
                    }
                    return false;
                }

                bool checkedAny = false;
                List<IntVec3> hitCells = rig.Footprint.hitCellPositions;
                if (hitCells != null && hitCells.Count > 0)
                {
                    checkedAny = true;
                    for (int i = 0; i < hitCells.Count; i++)
                    {
                        if (TryAround(hitCells[i]))
                        {
                            return true;
                        }
                    }
                }
                List<IntVec3> collisionCells = rig.Footprint.collisionCells;
                if (collisionCells != null && collisionCells.Count > 0)
                {
                    checkedAny = true;
                    for (int i = 0; i < collisionCells.Count; i++)
                    {
                        if (TryAround(collisionCells[i]))
                        {
                            return true;
                        }
                    }
                }
                if (!checkedAny)
                {
                    return TryAround(giantOrCorpse.Position);
                }
                return false;
            }

            if (RunReachPass(allowCanopyFallback: false))
            {
                return true;
            }
            if (blockedCanopy != null && blockedCanopy.Count > 0)
            {
                return RunReachPass(allowCanopyFallback: true);
            }
            return false;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Reachability), nameof(Reachability.CanReach),
            new[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
        public static void Reachability_CanReach_Finalizer(GiantPathingContext.Scope __state)
        {
            __state.Dispose();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GenClosest), nameof(GenClosest.ClosestThingReachable))]
        public static void GenClosest_ClosestThingReachable_Prefix(
            TraverseParms traverseParams,
            ref bool forceAllowGlobalSearch)
        {
            if (forceAllowGlobalSearch || traverseParams.pawn == null)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(traverseParams.pawn) is CompGiantRig comp
                && (comp.IsAirborneOrTakingOff
                    || (comp.RigDef?.ActiveAquaticLocomotion?.IsActive ?? false)
                    || GiantDestructionUtility.CanGiantCrushAnything(comp, out _, out _, out _)))
            {
                forceAllowGlobalSearch = true;
            }
        }

        private static bool IsPawnWithinSocialRangeOfGiant(Pawn smallPawn, Pawn giantPawn, CompGiantRig giantRig, float maxHullGap = 4.5f)
        {
            if (smallPawn == null || giantPawn == null || giantRig == null || !smallPawn.Spawned || !giantPawn.Spawned || smallPawn.Map == null)
            {
                return false;
            }
            if (GiantHullInteractionUtility.IsPawnTouchingGiantHitbox(smallPawn, giantPawn))
            {
                return true;
            }
            IntVec3 pCell = smallPawn.Position;
            float maxSq = maxHullGap * maxHullGap;
            IntVec3 nearestHitCell = IntVec3.Invalid;
            float bestSq = float.MaxValue;
            List<HitCellEntry> hits = giantRig.Footprint.hitCells;
            for (int i = 0; i < hits.Count; i++)
            {
                float dSq = (hits[i].cell - pCell).LengthHorizontalSquared;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    nearestHitCell = hits[i].cell;
                }
            }
            foreach (IntVec3 hc in giantRig.Footprint.collisionCellSet)
            {
                float dSq = (hc - pCell).LengthHorizontalSquared;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    nearestHitCell = hc;
                }
            }
            if (nearestHitCell.IsValid && bestSq <= maxSq)
            {
                return GenSight.LineOfSight(pCell, nearestHitCell, smallPawn.Map, skipFirstCell: true)
                    || GenSight.LineOfSight(pCell, giantPawn.Position, smallPawn.Map, skipFirstCell: true);
            }
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SocialInteractionUtility), nameof(SocialInteractionUtility.IsGoodPositionForInteraction),
            new[] { typeof(Pawn), typeof(Pawn) })]
        public static void SocialInteractionUtility_IsGoodPositionForInteraction_Postfix(
            Pawn p,
            Pawn recipient,
            ref bool __result)
        {
            if (__result || p == null || recipient == null || p == recipient || !p.Spawned || !recipient.Spawned || p.Map == null || p.Map != recipient.Map)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(recipient) is CompGiantRig recipientRig && GiantFastLookup.TryGetRig(p) == null)
            {
                if (IsPawnWithinSocialRangeOfGiant(p, recipient, recipientRig))
                {
                    __result = true;
                    return;
                }
            }
            if (GiantFastLookup.TryGetRig(p) is CompGiantRig initiatorRig)
            {
                if (HarmonyPatches_CombatAndHitbox.IsGiantAttackerInTouchRange(initiatorRig, p.Position, recipient)
                    || (GiantFastLookup.TryGetRig(recipient) == null && IsPawnWithinSocialRangeOfGiant(recipient, p, initiatorRig)))
                {
                    __result = true;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SocialInteractionUtility), nameof(SocialInteractionUtility.BestInteractableCell))]
        public static void SocialInteractionUtility_BestInteractableCell_Postfix(
            Pawn actor,
            Pawn targetPawn,
            ref IntVec3 __result)
        {
            if (__result.IsValid || actor == null || targetPawn == null || !actor.Spawned || !targetPawn.Spawned || actor.Map == null || actor.Map != targetPawn.Map)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(targetPawn) != null && GiantFastLookup.TryGetRig(actor) == null)
            {
                if (GiantHullInteractionUtility.IsPawnTouchingGiantHull(actor, targetPawn))
                {
                    __result = actor.Position;
                }
                else if (GiantHullInteractionUtility.TryFindBestHullInteractionCell(actor, targetPawn, out IntVec3 hullCell))
                {
                    __result = hullCell;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Toils_Interpersonal), nameof(Toils_Interpersonal.GotoInteractablePosition))]
        public static void Toils_Interpersonal_GotoInteractablePosition_Postfix(
            TargetIndex target,
            ref Toil __result)
        {
            if (GiantFastLookup.IsEmpty || __result == null)
            {
                return;
            }
            Toil toil = __result;
            System.Action origInit = toil.initAction;
            System.Action<int> origTickInterval = toil.tickIntervalAction;

            toil.initAction = delegate
            {
                Pawn actor = toil.actor;
                if (actor?.CurJob != null &&
                    actor.CurJob.GetTarget(target).Thing is Pawn targetPawn &&
                    GiantFastLookup.TryGetRig(targetPawn) != null &&
                    GiantFastLookup.TryGetRig(actor) == null)
                {
                    if (GiantHullInteractionUtility.IsPawnTouchingGiantHull(actor, targetPawn))
                    {
                        actor.pather?.StopDead();
                        actor.jobs?.curDriver?.ReadyForNextToil();
                        return;
                    }
                    actor.pather?.StartPath(targetPawn, PathEndMode.Touch);
                    return;
                }
                origInit?.Invoke();
            };

            toil.tickIntervalAction = delegate(int delta)
            {
                Pawn actor = toil.actor;
                Map map = actor?.Map;
                if (actor != null && map != null && actor.CurJob != null &&
                    actor.CurJob.GetTarget(target).Thing is Pawn targetPawn &&
                    GiantFastLookup.TryGetRig(targetPawn) != null &&
                    GiantFastLookup.TryGetRig(actor) == null)
                {
                    if (GiantHullInteractionUtility.IsPawnTouchingGiantHull(actor, targetPawn) &&
                        (actor.pather == null || !actor.pather.Moving || actor.pather.nextCell.GetDoor(map) == null))
                    {
                        actor.pather?.StopDead();
                        actor.jobs?.curDriver?.ReadyForNextToil();
                        return;
                    }
                    if (actor.pather != null && !actor.pather.Moving)
                    {
                        if (GiantHullInteractionUtility.CanPawnReachGiantHull(actor, targetPawn))
                        {
                            actor.pather.StartPath(targetPawn, PathEndMode.Touch);
                        }
                        else
                        {
                            actor.jobs?.curDriver?.EndJobWith(JobCondition.Incompletable);
                        }
                    }
                    return;
                }
                origTickInterval?.Invoke(delta);
            };
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
        public static void Pawn_PathFollower_StartPath_Prefix(
            Pawn ___pawn,
            ref LocalTargetInfo dest,
            ref PathEndMode peMode,
            out GiantPathingContext.Scope __state)
        {
            __state = GiantPathingContext.Enter(___pawn);
            if (___pawn != null &&
                ___pawn.Spawned &&
                ___pawn.Map != null &&
                dest.IsValid &&
                GiantFastLookup.TryGetRig(___pawn) is CompGiantRig selfRig &&
                !selfRig.IsAirborneOrTakingOff &&
                !selfRig.IsBiologicalSleepCurling &&
                !GiantFlightRuntime.IsBiologicalTerrestrialActivityJob(___pawn.CurJob) &&
                selfRig.FlightLocomotion != null &&
                selfRig.FlightLocomotion.Def.flightMode == GiantFlightMode.TakeoffAndLanding &&
                selfRig.FlightLocomotion.Def.autoTakeoffWhenPathBlocked &&
                selfRig.FlightLocomotion.CanMaintainLift &&
                (!selfRig.FlightLocomotion.Def.blockedByThickRoof || !GiantDestructionUtility.HasThickRoofAt(___pawn.Map, ___pawn.Position)))
            {
                bool groundOrBreachReachable;
                suppressAutoTakeoffReachCheck = true;
                try
                {
                    groundOrBreachReachable = ___pawn.Map.reachability.CanReach(
                        ___pawn.Position,
                        dest,
                        peMode,
                        TraverseParms.For(___pawn, Danger.Deadly, TraverseMode.PassDoors));
                }
                finally
                {
                    suppressAutoTakeoffReachCheck = false;
                }

                if (!groundOrBreachReachable)
                {
                    GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___pawn.Map);
                    if (mapComp != null && mapComp.CanAerialReach(___pawn, selfRig, ___pawn.Position, dest, peMode))
                    {
                        selfRig.FlightLocomotion.TryCommandTakeoff(selfRig, out _);
                    }
                }
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
        public static void Pawn_PathFollower_StartPath_Finalizer(GiantPathingContext.Scope __state)
        {
            __state.Dispose();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_PathFollower), "NeedNewPath")]
        public static void Pawn_PathFollower_NeedNewPath_Prefix(
            Pawn ___pawn,
            out GiantPathingContext.Scope __state)
        {
            __state = GiantPathingContext.Enter(___pawn);
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Pawn_PathFollower), "NeedNewPath")]
        public static void Pawn_PathFollower_NeedNewPath_Finalizer(GiantPathingContext.Scope __state)
        {
            __state.Dispose();
        }

        private static bool HasGiantPathingInvolvement(Pawn pawn, LocalTargetInfo destination)
        {
            if (GiantFastLookup.TryGetRig(pawn) != null)
            {
                return true;
            }
            if (destination.HasThing)
            {
                Thing t = destination.Thing;
                if (GiantFastLookup.TryGetRig(t as Pawn) != null
                    || GiantFastLookup.TryGetRig((t as Corpse)?.InnerPawn) != null)
                {
                    return true;
                }
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_PathFollower), "SetupMoveIntoNextCell")]
        public static bool Pawn_PathFollower_SetupMoveIntoNextCell_Prefix(
            Pawn_PathFollower __instance,
            Pawn ___pawn,
            LocalTargetInfo ___destination,
            out GiantPathingContext.Scope __state)
        {
            if (GiantFastLookup.IsEmpty)
            {
                __state = default;
                return true;
            }
            __state = GiantPathingContext.Enter(___pawn);
            if (__instance?.curPath != null
                && __instance.curPath.NodesLeftCount <= 1
                && HasGiantPathingInvolvement(___pawn, ___destination))
            {
                if (PatherAtDestMethod != null && PatherArrivedMethod != null && (bool)PatherAtDestMethod.Invoke(__instance, null))
                {
                    if (DegenerateOneNodePathRetries.TryGetValue(__instance, out DegeneratePathRetryState arrivedState))
                    {
                        arrivedState.RetryCount = 0;
                    }
                    PatherArrivedMethod.Invoke(__instance, null);
                    return false;
                }

                int nowTick = Find.TickManager?.TicksGame ?? 0;
                IntVec3 destCell = ___destination.IsValid ? ___destination.Cell : IntVec3.Invalid;
                IntVec3 pawnPos = ___pawn != null ? ___pawn.Position : IntVec3.Invalid;
                DegeneratePathRetryState retryState = DegenerateOneNodePathRetries.GetOrCreateValue(__instance);
                if (retryState.LastDestCell == destCell
                    && retryState.LastPawnPos == pawnPos
                    && nowTick >= retryState.LastRetryTick
                    && nowTick - retryState.LastRetryTick <= OneNodeRetryWindowTicks)
                {
                    retryState.RetryCount++;
                }
                else
                {
                    retryState.LastDestCell = destCell;
                    retryState.LastPawnPos = pawnPos;
                    retryState.RetryCount = 1;
                }
                retryState.LastRetryTick = nowTick;

                if (retryState.RetryCount > MaxConsecutiveOneNodePathRetries)
                {
                    retryState.RetryCount = 0;
                    if (PatherFailedMethod != null)
                    {
                        PatherFailedMethod.Invoke(__instance, null);
                        return false;
                    }
                    return true;
                }

                __instance.ResetToCurrentPosition();
                if (__instance.Moving)
                {
                    PatherTrySetNewPathRequestMethod?.Invoke(__instance, null);
                }
                return false;
            }
            return true;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Pawn_PathFollower), "SetupMoveIntoNextCell")]
        public static void Pawn_PathFollower_SetupMoveIntoNextCell_Finalizer(GiantPathingContext.Scope __state)
        {
            __state.Dispose();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
        public static void Pawn_PathFollower_TryEnterNextPathCell_Prefix(
            Pawn ___pawn,
            IntVec3 ___nextCell,
            out GiantPathingContext.Scope __state)
        {
            if (GiantFastLookup.IsEmpty)
            {
                __state = default;
                return;
            }
            __state = GiantPathingContext.Enter(___pawn);
            if (___pawn != null && ___nextCell.IsValid)
            {
                GiantDestructionUtility.EnsureCellClearedForGiantStep(___pawn, ___nextCell);
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
        public static void Pawn_PathFollower_TryEnterNextPathCell_Finalizer(GiantPathingContext.Scope __state)
        {
            __state.Dispose();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_PathFollower), "BuildingBlockingNextPathCell")]
        public static void Pawn_PathFollower_BuildingBlockingNextPathCell_Postfix(
            Pawn ___pawn,
            ref Building __result)
        {
            if (__result != null
                && ___pawn != null
                && ___pawn.Map != null
                && GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp
                && comp.IsAirborneOrTakingOff
                && GiantFlightUtility.IsCellPassableForFlight(___pawn.Map, comp, __result.Position))
            {
                __result = null;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_PathFollower), "NextCellDoorToWaitForOrManuallyOpen")]
        public static void Pawn_PathFollower_NextCellDoorToWaitForOrManuallyOpen_Postfix(
            Pawn ___pawn,
            ref Building_Door __result)
        {
            if (__result != null
                && ___pawn != null
                && GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp
                && comp.IsAirborneOrTakingOff)
            {
                __result = null;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_PathFollower), "PawnCanOccupy")]
        public static void Pawn_PathFollower_PawnCanOccupy_Postfix(
            Pawn ___pawn,
            IntVec3 c,
            ref bool __result)
        {
            if (___pawn == null || ___pawn.Map == null || GiantFastLookup.IsEmpty)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(___pawn) is CompGiantRig comp)
            {
                if (comp.IsAirborneOrTakingOff)
                {
                    if (!__result && GiantFlightUtility.IsCellPassableForFlight(___pawn.Map, comp, c))
                    {
                        __result = true;
                    }
                    return;
                }
                if (comp.RigDef?.ActiveAquaticLocomotion is GiantAquaticDef aq && aq.IsActive)
                {
                    __result = GiantAquaticUtility.IsCellPassableForAquatic(___pawn.Map, c, ___pawn, comp, aq);
                    return;
                }
                if (!__result && GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(___pawn.Map, ___pawn, c))
                {
                    __result = true;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_PathFollower), "CostToMoveIntoCell", new[] { typeof(Pawn), typeof(IntVec3) })]
        public static void Pawn_PathFollower_CostToMoveIntoCell_Postfix(
            Pawn pawn,
            IntVec3 c,
            ref float __result)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map == null || GiantFastLookup.IsEmpty)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(pawn) is CompGiantRig rigComp)
            {
                if (rigComp.IsAirborneOrTakingOff)
                {
                    if (GiantFlightUtility.IsCellPassableForFlight(pawn.Map, rigComp, c))
                    {
                        bool diag = c.x != pawn.Position.x && c.z != pawn.Position.z;
                        float moveTicks = diag ? pawn.TicksPerMoveDiagonal : pawn.TicksPerMoveCardinal;
                        __result = Mathf.Max(1f, moveTicks);
                    }
                    return;
                }

                if (rigComp.RigDef?.ActiveAquaticLocomotion is GiantAquaticDef aq && aq.IsActive)
                {
                    if (!GiantAquaticUtility.IsCellPassableForAquatic(pawn.Map, c, pawn, rigComp, aq))
                    {
                        __result = 10000f;
                        return;
                    }
                    TerrainGrid tg = pawn.Map.terrainGrid;
                    TerrainDef top = c.InBounds(pawn.Map) ? tg?.TerrainAt(c) : null;
                    TerrainDef under = (top != null && top.bridge) ? tg.UnderTerrainAt(c) : null;
                    GiantAquaticTerrainInfo info = GiantAquaticUtility.ClassifyTerrain(top, under);
                    if (GiantAquaticUtility.IsTerrainEligibleWater(in info, aq))
                    {
                        bool diag = c.x != pawn.Position.x && c.z != pawn.Position.z;
                        float moveTicks = diag ? pawn.TicksPerMoveDiagonal : pawn.TicksPerMoveCardinal;
                        __result = Mathf.Max(1f, moveTicks);
                        return;
                    }
                }
            }
            if (__result < 9000f)
            {
                return;
            }
            if (GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(pawn.Map, pawn, c))
            {
                bool diag = c.x != pawn.Position.x && c.z != pawn.Position.z;
                float moveTicks = diag ? pawn.TicksPerMoveDiagonal : pawn.TicksPerMoveCardinal;
                __result = Mathf.Min(__result, moveTicks + 35f);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Building_Door), nameof(Building_Door.PawnCanOpen))]
        public static void Building_Door_PawnCanOpen_Postfix(
            Building_Door __instance,
            Pawn p,
            ref bool __result)
        {
            if (__result || __instance == null || p == null)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(p) is CompGiantRig comp)
            {
                if (comp.IsAirborneOrTakingOff)
                {
                    // Do not grant PawnCanOpen for airborne giants; BlocksPawn/NextCellDoorToWaitForOrManuallyOpen/GetDoorCost
                    // let them fly overhead without pausing, triggering door open/close, or paying door open costs.
                    return;
                }
                if (GiantPlayerControlUtility.IsPlayerGiant(p) &&
                    (__instance.Faction == null || !p.HostileTo(__instance)))
                {
                    __result = true;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Building_Door), nameof(Building_Door.BlocksPawn))]
        public static void Building_Door_BlocksPawn_Postfix(
            Building_Door __instance,
            Pawn p,
            ref bool __result)
        {
            if (!__result || p == null)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(p) is CompGiantRig comp)
            {
                if (comp.IsAirborneOrTakingOff && GiantFlightUtility.IsCellPassableForFlight(__instance.Map, comp, __instance.Position))
                {
                    __result = false;
                    return;
                }
                if (GiantPathingContext.IsWalkableOverrideActive(p)
                    && GiantDestructionUtility.IsCrushableBuilding(p, comp, __instance))
                {
                    __result = false;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.WalkableBy))]
        public static void GenGrid_WalkableBy_Postfix(
            IntVec3 c,
            Map map,
            Pawn pawn,
            ref bool __result)
        {
            if (map == null || pawn == null || GiantFastLookup.IsEmpty)
            {
                return;
            }
            if (GiantFastLookup.TryGetRig(pawn) is CompGiantRig comp)
            {
                if (comp.IsAirborneOrTakingOff)
                {
                    if (!__result && GiantPathingContext.IsWalkableOverrideActive(pawn)
                        && GiantFlightUtility.IsCellPassableForFlight(map, comp, c))
                    {
                        __result = true;
                    }
                    return;
                }
                if (comp.RigDef?.ActiveAquaticLocomotion is GiantAquaticDef aq && aq.IsActive)
                {
                    bool passableAq = GiantAquaticUtility.IsCellPassableForAquatic(map, c, pawn, comp, aq);
                    if (aq.IsWaterOnly && !passableAq)
                    {
                        __result = false;
                        return;
                    }
                    if (!__result && passableAq)
                    {
                        __result = true;
                    }
                    return;
                }
                if (!__result && GiantPathingContext.IsWalkableOverrideActive(pawn)
                    && GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(map, pawn, c))
                {
                    __result = true;
                }
            }
        }

        /// <summary>
        /// Extends GenAdj.AdjacentTo8WayOrInside and GenAdj.IsInside from the single center cell (Position)
        /// to all HitCells of a Giant Creature or giant Corpse so every vanilla Toil/JobDriver/interaction
        /// adjacency check succeeds around the full hitbox.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenAdj), nameof(GenAdj.AdjacentTo8WayOrInside), new[] { typeof(IntVec3), typeof(Thing) })]
        public static void GenAdj_AdjacentTo8WayOrInside_Thing_Postfix(IntVec3 root, Thing t, ref bool __result)
        {
            if (__result || GiantFastLookup.IsEmpty || t == null || !t.Spawned || !root.IsValid)
            {
                return;
            }
            if (GiantHullInteractionUtility.TryGetInteractableGiantRig(t) is CompGiantRig comp
                && HarmonyPatches_CombatAndHitbox.IsCellTouchingFootprint(root, comp.Footprint))
            {
                __result = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenAdj), nameof(GenAdj.AdjacentTo8WayOrInside), new[] { typeof(IntVec3), typeof(LocalTargetInfo) })]
        public static void GenAdj_AdjacentTo8WayOrInside_LocalTarget_Postfix(IntVec3 me, LocalTargetInfo other, ref bool __result)
        {
            if (__result || GiantFastLookup.IsEmpty || !other.HasThing || !me.IsValid)
            {
                return;
            }
            Thing t = other.Thing;
            if (t != null && t.Spawned
                && GiantHullInteractionUtility.TryGetInteractableGiantRig(t) is CompGiantRig comp
                && HarmonyPatches_CombatAndHitbox.IsCellTouchingFootprint(me, comp.Footprint))
            {
                __result = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenAdj), nameof(GenAdj.AdjacentTo8WayOrInside), new[] { typeof(Thing), typeof(Thing) })]
        public static void GenAdj_AdjacentTo8WayOrInside_ThingPair_Postfix(Thing a, Thing b, ref bool __result)
        {
            if (__result || GiantFastLookup.IsEmpty || a == null || b == null || !a.Spawned || !b.Spawned || a.Map != b.Map)
            {
                return;
            }
            if (GiantHullInteractionUtility.TryGetInteractableGiantRig(b) is CompGiantRig compB
                && HarmonyPatches_CombatAndHitbox.IsCellTouchingFootprint(a.Position, compB.Footprint))
            {
                __result = true;
                return;
            }
            if (GiantHullInteractionUtility.TryGetInteractableGiantRig(a) is CompGiantRig compA
                && HarmonyPatches_CombatAndHitbox.IsCellTouchingFootprint(b.Position, compA.Footprint))
            {
                __result = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GenAdj), nameof(GenAdj.IsInside), new[] { typeof(IntVec3), typeof(Thing) })]
        public static void GenAdj_IsInside_Thing_Postfix(IntVec3 root, Thing t, ref bool __result)
        {
            if (__result || GiantFastLookup.IsEmpty || t == null || !t.Spawned || !root.IsValid)
            {
                return;
            }
            if (GiantHullInteractionUtility.TryGetInteractableGiantRig(t) is CompGiantRig comp
                && comp.Footprint.hitCellSet.Contains(root))
            {
                __result = true;
            }
        }

        /// <summary>
        /// Extends cell-level forbidden checks (allowed area / squad duty radius) on Giant Creatures/Corpses
        /// from center Position to all HitCells without overriding faction, host-faction, lord, or ritual forbids.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden), new[] { typeof(Thing), typeof(Pawn) })]
        public static void ForbidUtility_IsForbidden_ThingPawn_Postfix(Thing t, Pawn pawn, ref bool __result)
        {
            if (!__result || GiantFastLookup.IsEmpty || t == null || pawn == null || !t.Spawned)
            {
                return;
            }
            CompGiantRig rig = GiantHullInteractionUtility.TryGetInteractableGiantRig(t);
            if (rig == null)
            {
                return;
            }
            // Only relax forbids caused by the center Position cell being outside allowed area / squad radius.
            if (!t.Position.IsForbidden(pawn))
            {
                return;
            }
            // Preserve explicit faction, host-faction (slaves/prisoners), lord extraForbiddenThings, and ritual forbids.
            if (t.IsForbidden(pawn.Faction) || t.IsForbidden(pawn.HostFaction))
            {
                return;
            }
            Lord lord = pawn.GetLord();
            if (lord != null && lord.extraForbiddenThings != null && lord.extraForbiddenThings.Contains(t))
            {
                return;
            }
            Map mapHeld = pawn.MapHeld;
            if (mapHeld?.lordManager?.lords != null)
            {
                List<Lord> lords = mapHeld.lordManager.lords;
                for (int i = 0; i < lords.Count; i++)
                {
                    Lord otherLord = lords[i];
                    if (otherLord == null || otherLord == lord)
                    {
                        continue;
                    }
                    if (otherLord.CurLordToil is LordToil_Ritual ritualToil
                        && ritualToil.ReservedThings != null
                        && ritualToil.ReservedThings.Contains(t))
                    {
                        return;
                    }
                    if (otherLord.CurLordToil is LordToil_PsychicRitual psychicToil
                        && psychicToil.RitualData?.psychicRitual?.def is PsychicRitualDef_InvocationCircle invocationDef
                        && invocationDef.TargetRole != null
                        && psychicToil.RitualData.psychicRitual.assignments?.FirstAssignedPawn(invocationDef.TargetRole) == t
                        && !(psychicToil.RitualData.CurPsychicRitualToil is PsychicRitualToil_TargetCleanup))
                    {
                        return;
                    }
                }
            }

            List<IntVec3> hitCells = rig.Footprint.hitCellPositions;
            if (hitCells != null)
            {
                for (int i = 0; i < hitCells.Count; i++)
                {
                    if (!hitCells[i].IsForbidden(pawn))
                    {
                        __result = false;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Biological player animal giants cannot use 1x1 animal sleeping spots (CanUseBedEver = false);
        /// allow doctors to tend stationary, non-fighting, grounded biological player giants on the spot.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.GoodLayingStatusForTend))]
        public static void WorkGiver_Tend_GoodLayingStatusForTend_Postfix(Pawn patient, Pawn doctor, ref bool __result)
        {
            if (__result || GiantFastLookup.IsEmpty || patient == null || !patient.Spawned)
            {
                return;
            }
            CompGiantRig rig = GiantFastLookup.TryGetRig(patient);
            if (rig == null
                || rig.IsAirborneOrTakingOff
                || GiantVehicleBehaviorGuard.IsVehiclePawn(patient, out _, out _))
            {
                return;
            }
            if (patient.Faction == Faction.OfPlayer
                && (patient.pather == null || !patient.pather.Moving)
                && !patient.InAggroMentalState
                && !(patient.CurJob?.def == JobDefOf.AttackMelee || patient.CurJob?.def == JobDefOf.AttackStatic))
            {
                __result = true;
            }
        }
    }
}
