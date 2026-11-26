using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    [HarmonyPatch]
    public static class HarmonyPatches_VehicleAndTurrets
    {
        private static readonly AccessTools.FieldRef<Gizmo, bool> GizmoDisabledRef =
            AccessTools.FieldRefAccess<Gizmo, bool>("disabled");

        // =========================================================================
        // 1. Unified AI ThinkTree & Job Pipeline Choke Points
        //    (Zero per-JobGiver / per-WorkGiver patches; all filtering is centralized
        //     in GiantVehicleBehaviorGuard across ThinkTree entry + DetermineNextJob)
        // =========================================================================

        #region 1. AI ThinkTree & Job Pipeline Choke Points

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextJob")]
        public static bool Pawn_JobTracker_DetermineNextJob_Prefix(
            Pawn ___pawn,
            ref ThinkTreeDef thinkTree,
            ref ThinkResult __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return true;
            }
            if (GiantVehicleBehaviorGuard.TryShortCircuitDetermineNextJob(___pawn, ref thinkTree, out ThinkResult shortCircuit))
            {
                __result = shortCircuit;
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextJob")]
        public static void Pawn_JobTracker_DetermineNextJob_Postfix(
            Pawn ___pawn,
            ref ThinkResult __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            GiantVehicleBehaviorGuard.SanitizeThinkResultForVehicle(___pawn, ref __result);
        }

        #endregion

        // =========================================================================
        // 2. Vehicle Kinematics, Hull Rotation Lock & Crew Stat Scaling
        // =========================================================================

        #region 2. Vehicle Kinematics, Hull Rotation Lock & Crew Stat Scaling

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        [HarmonyPatch(typeof(Pawn), "TicksPerMove", new[] { typeof(bool) })]
        public static void Pawn_TicksPerMove_Crew_Postfix(Pawn __instance, ref float __result)
        {
            // Crew, drive mobility, and propulsion speed multipliers are already applied in
            // StatWorker_GetValueUnfinalized_Crew_Postfix (which feeds Pawn.TicksPerMove via StatDefOf.MoveSpeed).
            // Only enforce hard immobilization clamps here so speed multipliers are never squared or cubed.
            if (GiantFastLookup.TryGetCockpit(__instance) is CompGiantCockpit cockpit && cockpit.IsDormantUnmanned)
            {
                __result = 600f;
                return;
            }

            if (GiantFastLookup.TryGetRig(__instance) is CompGiantRig locoRig &&
                locoRig.VehicleLocomotion is VehicleLocomotionRuntime vehLoco &&
                vehLoco.IsDriveImmobilized)
            {
                __result = 600f;
            }
        }

        public struct VehiclePatherStepState
        {
            public bool active;
            public bool restoreUnconditionally;
            public IntVec3 prevNextCell;
            public float origCostLeft;
            public float injectedCostLeft;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick))]
        public static bool Pawn_PathFollower_PatherTick_Vehicle_Prefix(
            Pawn_PathFollower __instance,
            Pawn ___pawn,
            ref float ___nextCellCostLeft,
            out VehiclePatherStepState __state)
        {
            __state = default;
            if (__instance == null || ___pawn == null || !___pawn.Spawned)
            {
                return true;
            }

            if (GiantFastLookup.TryGetCockpit(___pawn) is CompGiantCockpit dormantCockpit &&
                dormantCockpit.IsDormantUnmanned)
            {
                if (__instance.Moving)
                {
                    __instance.StopDead();
                }
                if (GiantFastLookup.TryGetRig(___pawn)?.FlightVehicleLocomotion is FlightVehicleLocomotionRuntime dormantFveh)
                {
                    dormantFveh.StepDormantBleed(1f);
                }
                return false;
            }

            if (!(GiantFastLookup.TryGetRig(___pawn) is CompGiantRig rig))
            {
                return true;
            }

            if (rig.FlightLocomotion != null && rig.FlightLocomotion.FlightState == GiantFlightState.TakingOff && !rig.IsCurrentlyAirborne)
            {
                if (__instance.Moving && __instance.curPath != null)
                {
                    return false;
                }
                return true;
            }

            if (rig.FlightVehicleLocomotion is FlightVehicleLocomotionRuntime fvehLoco)
            {
                float fSpeedScale = fvehLoco.PrePatherTickAndComputeSpeedScale(__instance, ___pawn, rig);
                if (__instance.Moving && fvehLoco.ShouldBlockPatherCellAdvance)
                {
                    if (__instance.curPath == null && __instance.curPathRequest != null)
                    {
                        __state.active = true;
                        __state.restoreUnconditionally = true;
                        __state.prevNextCell = __instance.nextCell;
                        __state.origCostLeft = ___nextCellCostLeft;
                        return true;
                    }
                    return false;
                }
                if (__instance.MovingNow && fSpeedScale < 0.999f && fSpeedScale > 0.001f)
                {
                    __state.active = true;
                    __state.prevNextCell = __instance.nextCell;
                    __state.origCostLeft = ___nextCellCostLeft;
                    ___nextCellCostLeft += (1f - fSpeedScale);
                    __state.injectedCostLeft = ___nextCellCostLeft;
                }
                return true;
            }

            if (rig.VehicleLocomotion == null)
            {
                if (rig.EvaluateGroundedPivotTurnGate(__instance))
                {
                    if (__instance.curPath == null && __instance.curPathRequest != null)
                    {
                        __state.active = true;
                        __state.restoreUnconditionally = true;
                        __state.prevNextCell = __instance.nextCell;
                        __state.origCostLeft = ___nextCellCostLeft;
                        return true;
                    }
                    return false;
                }
                return true;
            }

            VehicleLocomotionRuntime vehLoco = rig.VehicleLocomotion;
            float speedScale = vehLoco.PrePatherTickAndComputeSpeedScale(__instance, ___pawn, rig);
            if (__instance.Moving && vehLoco.ShouldBlockPatherCellAdvance)
            {
                if (__instance.curPath == null && __instance.curPathRequest != null)
                {
                    __state.active = true;
                    __state.restoreUnconditionally = true;
                    __state.prevNextCell = __instance.nextCell;
                    __state.origCostLeft = ___nextCellCostLeft;
                    return true;
                }
                return false;
            }

            if (__instance.MovingNow && speedScale < 0.999f && speedScale > 0.001f)
            {
                __state.active = true;
                __state.prevNextCell = __instance.nextCell;
                __state.origCostLeft = ___nextCellCostLeft;
                ___nextCellCostLeft += (1f - speedScale);
                __state.injectedCostLeft = ___nextCellCostLeft;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick))]
        public static void Pawn_PathFollower_PatherTick_Vehicle_Postfix(
            Pawn_PathFollower __instance,
            ref float ___nextCellCostLeft,
            VehiclePatherStepState __state)
        {
            if (!__state.active || __instance == null)
            {
                return;
            }
            if (__state.restoreUnconditionally)
            {
                if (__instance.nextCell == __state.prevNextCell)
                {
                    ___nextCellCostLeft = __state.origCostLeft;
                }
                return;
            }
            if (__instance.nextCell == __state.prevNextCell &&
                Mathf.Abs(___nextCellCostLeft - __state.injectedCostLeft) < 1e-4f)
            {
                ___nextCellCostLeft = __state.origCostLeft;
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.UpdateRotation))]
        public static bool Pawn_RotationTracker_UpdateRotation_Vehicle_Prefix(Pawn ___pawn)
        {
            if (___pawn != null && GiantFastLookup.TryGetRig(___pawn) is CompGiantRig rig)
            {
                if (rig.VehicleLocomotion is VehicleLocomotionRuntime vehLoco && vehLoco.ShouldLockPawnRotation)
                {
                    Rot4 lockedRot = Rot4.FromAngleFlat(vehLoco.HullHeadingDeg);
                    if (___pawn.Rotation != lockedRot)
                    {
                        ___pawn.Rotation = lockedRot;
                    }
                    return false;
                }
                if (rig.FlightVehicleLocomotion is FlightVehicleLocomotionRuntime fvehLoco && fvehLoco.ShouldLockPawnRotation)
                {
                    Rot4 lockedRot = Rot4.FromAngleFlat(fvehLoco.HullHeadingDeg);
                    if (___pawn.Rotation != lockedRot)
                    {
                        ___pawn.Rotation = lockedRot;
                    }
                    return false;
                }
                if (rig.IsPivotTurningInPlace && rig.HeadingInitialized)
                {
                    Rot4 lockedRot = Rot4.FromAngleFlat(rig.CurrentHeadingDeg);
                    if (___pawn.Rotation != lockedRot)
                    {
                        ___pawn.Rotation = lockedRot;
                    }
                    return false;
                }
                // Preserve drafted arrival heading instead of vanilla Pawn_RotationTracker snapping stationary drafted pawns to Rot4.South (180 deg)
                if (___pawn.Drafted
                    && !___pawn.Destroyed
                    && !(___pawn.jobs?.HandlingFacing ?? false)
                    && !(___pawn.pather?.Moving ?? false)
                    && !(___pawn.stances?.curStance is Stance_Busy sb && sb.focusTarg.IsValid))
                {
                    if (rig.HeadingInitialized)
                    {
                        Rot4 heldRot = Rot4.FromAngleFlat(rig.CurrentHeadingDeg);
                        if (___pawn.Rotation != heldRot)
                        {
                            ___pawn.Rotation = heldRot;
                        }
                    }
                    return false;
                }
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.FaceCell))]
        public static bool Pawn_RotationTracker_FaceCell_Vehicle_Prefix(Pawn ___pawn)
        {
            if (___pawn != null && GiantFastLookup.TryGetRig(___pawn) is CompGiantRig rig)
            {
                if ((rig.VehicleLocomotion != null && rig.VehicleLocomotion.ShouldLockPawnRotation) ||
                    (rig.FlightVehicleLocomotion != null && rig.FlightVehicleLocomotion.ShouldLockPawnRotation))
                {
                    return false;
                }
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.Face))]
        public static bool Pawn_RotationTracker_Face_Vehicle_Prefix(Pawn ___pawn)
        {
            if (___pawn != null && GiantFastLookup.TryGetRig(___pawn) is CompGiantRig rig)
            {
                if ((rig.VehicleLocomotion != null && rig.VehicleLocomotion.ShouldLockPawnRotation) ||
                    (rig.FlightVehicleLocomotion != null && rig.FlightVehicleLocomotion.ShouldLockPawnRotation))
                {
                    return false;
                }
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetValueUnfinalized), new[] { typeof(StatRequest), typeof(bool) })]
        public static void StatWorker_GetValueUnfinalized_Crew_Postfix(
            StatDef ___stat,
            StatRequest req,
            ref float __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            if (___stat != StatDefOf.MoveSpeed &&
                ___stat != StatDefOf.ShootingAccuracyPawn &&
                ___stat != StatDefOf.AimingDelayFactor)
            {
                return;
            }
            if (!req.HasThing || !(req.Thing is Pawn pawn))
            {
                return;
            }
            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(pawn);
            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            VehicleLocomotionRuntime vehLoco = rig?.VehicleLocomotion;
            FlightVehicleLocomotionRuntime fvehLoco = rig?.FlightVehicleLocomotion;
            if (cockpit == null && vehLoco == null && fvehLoco == null)
            {
                return;
            }

            if (___stat == StatDefOf.MoveSpeed)
            {
                if (cockpit != null)
                {
                    __result *= cockpit.ComputeMoveSpeedMultiplier();
                }
                if (vehLoco != null)
                {
                    __result *= vehLoco.DriveMobilitySpeedFactor;
                }
                else if (fvehLoco != null)
                {
                    __result *= fvehLoco.PropulsionSpeedFactor;
                }
            }
            else if (___stat == StatDefOf.ShootingAccuracyPawn)
            {
                if (cockpit != null)
                {
                    __result *= cockpit.ComputeWeaponAccuracyMultiplier();
                }
                if (vehLoco != null)
                {
                    __result *= vehLoco.CurrentMovingFireAccuracyMultiplier;
                }
                else if (fvehLoco != null)
                {
                    __result *= fvehLoco.GetMovingFireAccuracyMultiplier();
                }
            }
            else if (___stat == StatDefOf.AimingDelayFactor && cockpit != null)
            {
                float reloadMult = cockpit.ComputeReloadSpeedMultiplier();
                if (reloadMult > 0.05f)
                {
                    __result /= reloadMult;
                }
            }
        }

        #endregion

        // =========================================================================
        // 3. Cockpit Weapons, Abilities, Severed-Seat Ejection & Draft Controls
        // =========================================================================

        #region 3. Cockpit Weapons, Abilities, Severed-Seat Ejection & Draft Controls

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch(typeof(Ability), nameof(Ability.Activate), new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) })]
        public static bool Ability_Activate_Crew_Prefix(Ability __instance)
        {
            Pawn pawn = __instance?.pawn;
            if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit)
            {
                if (cockpit.IsDormantUnmanned || !cockpit.CanActivateAbilities)
                {
                    return false;
                }
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Ability), nameof(Ability.GizmoDisabled))]
        public static void Ability_GizmoDisabled_Crew_Postfix(Ability __instance, ref bool __result, ref string reason)
        {
            if (__result)
            {
                return;
            }
            Pawn pawn = __instance?.pawn;
            if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit)
            {
                if (cockpit.IsDormantUnmanned)
                {
                    reason = "GCF_CrewDisabledUnmanned".CanTranslate()
                        ? "GCF_CrewDisabledUnmanned".Translate().ToString()
                        : "Disabled: unit in standby";
                    __result = true;
                }
                else if (!cockpit.CanActivateAbilities)
                {
                    reason = "GCF_CrewDisabledNoOperator".CanTranslate()
                        ? "GCF_CrewDisabledNoOperator".Translate().ToString()
                        : "Disabled: missing commander/operator";
                    __result = true;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(VerbTracker), "CreateVerbTargetCommand")]
        public static void VerbTracker_CreateVerbTargetCommand_Crew_Postfix(Command_VerbTarget __result, Verb verb)
        {
            Pawn pawn = verb?.CasterPawn;
            if (__result == null || pawn == null || pawn.Faction != Faction.OfPlayer ||
                !(GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit))
            {
                return;
            }

            if (cockpit.IsDormantUnmanned)
            {
                __result.Disable("GCF_CrewDisabledUnmanned".CanTranslate()
                    ? "GCF_CrewDisabledUnmanned".Translate().ToString()
                    : "Disabled: unit in standby");
                return;
            }
            if (!cockpit.CanFirePrimaryWeapons)
            {
                __result.Disable("GCF_CrewDisabledNoGunner".CanTranslate()
                    ? "GCF_CrewDisabledNoGunner".Translate().ToString()
                    : "Disabled: missing main gunner");
                return;
            }

            if (GizmoDisabledRef(__result) &&
                cockpit.IsAutonomousPlayerControlledVehicle &&
                pawn.Drafted &&
                !pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                if (!(verb is Verb_LaunchProjectile) || verb.FirstApparelPreventingShooting() == null)
                {
                    GizmoDisabledRef(__result) = false;
                    __result.disabledReason = null;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.GetGizmos))]
        public static IEnumerable<Gizmo> Pawn_AbilityTracker_GetGizmos_Crew_Postfix(
            IEnumerable<Gizmo> __result,
            Pawn_AbilityTracker __instance)
        {
            Pawn pawn = __instance?.pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed ||
                GiantFastLookup.TryGetRig(pawn) == null)
            {
                return __result;
            }
            bool canEmitAbilities = GiantPlayerControlUtility.CanControl(pawn)
                || (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit && cockpit.IsAutonomousPlayerControlledVehicle);
            if (!canEmitAbilities)
            {
                return __result;
            }
            return EnsureVehicleAbilityGizmos(__result, __instance, pawn);
        }

        private static IEnumerable<Gizmo> EnsureVehicleAbilityGizmos(
            IEnumerable<Gizmo> source,
            Pawn_AbilityTracker tracker,
            Pawn pawn)
        {
            bool anyAbility = false;
            foreach (Gizmo g in source)
            {
                if (g is Command_Ability)
                {
                    anyAbility = true;
                }
                yield return g;
            }
            if (anyAbility)
            {
                yield break;
            }

            List<Ability> abilities = tracker.AllAbilitiesForReading;
            for (int i = 0; i < abilities.Count; i++)
            {
                Ability ab = abilities[i];
                if (ab?.def == null)
                {
                    continue;
                }
                bool visible = (pawn.Drafted || ab.def.displayGizmoWhileUndrafted) && ab.GizmosVisible();
                if (!visible)
                {
                    continue;
                }
                foreach (Command cmd in ab.GetGizmos())
                {
                    yield return cmd;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
            new[] { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) })]
        public static void Pawn_HealthTracker_AddHediff_Cockpit_Postfix(Pawn ___pawn, Hediff hediff)
        {
            if (hediff is Hediff_MissingPart && ___pawn != null &&
                GiantFastLookup.TryGetCockpit(___pawn) is CompGiantCockpit cockpit)
            {
                cockpit.CheckMissingSeatPartsAndEject();
            }
        }

        #endregion

        // =========================================================================
        // 4. World & Map Boarded-Crew Anti-GameOver & Man-in-Black Suppression
        // =========================================================================

        #region 4. World & Map Boarded-Crew Anti-GameOver

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.FreeColonistsSpawnedOrInPlayerEjectablePodsCount), MethodType.Getter)]
        public static void MapPawns_FreeColonistsSpawnedOrInPlayerEjectablePodsCount_Postfix(Map ___map, ref int __result)
        {
            if (___map != null)
            {
                GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(___map);
                if (mapComp != null && mapComp.HasOccupiedPlayerCockpits)
                {
                    __result += mapComp.CountBoardedFreeColonists();
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameEnder), nameof(GameEnder.CheckOrUpdateGameOver))]
        public static void GameEnder_CheckOrUpdateGameOver_Postfix(GameEnder __instance)
        {
            if (__instance == null || !__instance.gameEnding)
            {
                return;
            }

            List<Map> maps = Find.Maps;
            if (maps != null)
            {
                for (int i = 0; i < maps.Count; i++)
                {
                    GiantFootprintMapComponent mc = GiantFootprintMapComponent.GetFast(maps[i]);
                    if (mc != null && mc.CountBoardedFreeColonists() > 0)
                    {
                        __instance.gameEnding = false;
                        return;
                    }
                }
            }

            List<Caravan> caravans = Find.WorldObjects?.Caravans;
            if (caravans != null)
            {
                for (int c = 0; c < caravans.Count; c++)
                {
                    Caravan car = caravans[c];
                    if (car == null || !car.IsPlayerControlled)
                    {
                        continue;
                    }
                    List<Pawn> carPawns = car.PawnsListForReading;
                    for (int p = 0; p < carPawns.Count; p++)
                    {
                        if (GiantFastLookup.TryGetCockpit(carPawns[p]) is CompGiantCockpit cp && cp.HasAnyBoardedFreeColonist())
                        {
                            __instance.gameEnding = false;
                            return;
                        }
                    }
                }
            }

            if (HasAnyWorldGiantBoardedFreeColonist(Find.WorldObjects?.AllWorldObjects, GiantVehicleBehaviorGuard.GetSafePlayerFaction(), requireHealthyUndowned: false))
            {
                __instance.gameEnding = false;
            }
        }

        public static bool HasAnyWorldGiantBoardedFreeColonist(
            List<WorldObject> worldObjects,
            Faction playerFaction,
            bool requireHealthyUndowned)
        {
            if (worldObjects == null || playerFaction == null)
            {
                return false;
            }
            for (int i = 0; i < worldObjects.Count; i++)
            {
                if (!(worldObjects[i] is WorldObject_Giant worldGiant) || worldGiant.Faction != playerFaction)
                {
                    continue;
                }
                Pawn hostPawn = worldGiant.Pawn;
                if (hostPawn == null || hostPawn.Dead || !(GiantFastLookup.TryGetCockpit(hostPawn) is CompGiantCockpit cockpit))
                {
                    continue;
                }
                if (!requireHealthyUndowned)
                {
                    if (cockpit.HasAnyBoardedFreeColonist())
                    {
                        return true;
                    }
                    continue;
                }

                ThingOwner held = cockpit.GetDirectlyHeldThings();
                if (held == null)
                {
                    continue;
                }
                for (int k = 0; k < held.Count; k++)
                {
                    if (held[k] is Pawn col && col.IsFreeColonist && !col.Dead && !col.Downed)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StorytellerComp_Triggered), nameof(StorytellerComp_Triggered.Notify_PawnEvent))]
        public static bool StorytellerComp_Triggered_NotifyPawnEvent_Prefix(StorytellerComp_Triggered __instance, Pawn p)
        {
            if (!(__instance?.props is StorytellerCompProperties_Triggered triggeredProps)
                || triggeredProps.incident?.defName != "StrangerInBlackJoin")
            {
                return true;
            }

            List<Map> maps = Find.Maps;
            if (maps != null)
            {
                for (int m = 0; m < maps.Count; m++)
                {
                    GiantFootprintMapComponent mc = GiantFootprintMapComponent.GetFast(maps[m]);
                    if (mc == null || !mc.HasOccupiedPlayerCockpits)
                    {
                        continue;
                    }
                    IReadOnlyList<CompGiantCockpit> cockpits = mc.OccupiedPlayerCockpits;
                    for (int i = 0; i < cockpits.Count; i++)
                    {
                        ThingOwner held = cockpits[i]?.GetDirectlyHeldThings();
                        if (held == null)
                        {
                            continue;
                        }
                        for (int k = 0; k < held.Count; k++)
                        {
                            if (held[k] is Pawn col && col != p && col.IsFreeColonist && !col.Dead && !col.Downed)
                            {
                                return false;
                            }
                        }
                    }
                }
            }
            if (HasAnyWorldGiantBoardedFreeColonist(Find.WorldObjects?.AllWorldObjects, GiantVehicleBehaviorGuard.GetSafePlayerFaction(), requireHealthyUndowned: true))
            {
                return false;
            }
            return true;
        }

        #endregion

        // =========================================================================
        // 5. Pawn-as-Vehicle Domain Root Guards
        //    (Minimal orthogonal root hooks for non-Job RimWorld subsystems:
        //     reactive panic, mental state, ground crawling, ambient animal/mech calls,
        //     biological needs/filth, colonist carrying/medical/penning, caravan standby)
        // =========================================================================

        #region 5. Pawn-as-Vehicle Domain Root Guards

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_MindState), nameof(Pawn_MindState.StartFleeingBecauseOfPawnAction))]
        public static bool Pawn_MindState_StartFleeingBecauseOfPawnAction_Vehicle_Prefix(Pawn_MindState __instance)
        {
            return !GiantVehicleBehaviorGuard.IsVehiclePawn(__instance?.pawn, out _, out _);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
        public static bool MentalStateHandler_TryStartMentalState_Vehicle_Prefix(Pawn ___pawn, ref bool __result)
        {
            if (GiantVehicleBehaviorGuard.IsVehiclePawn(___pawn, out _, out _))
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_CallTracker), nameof(Pawn_CallTracker.DoCall))]
        public static bool Pawn_CallTracker_DoCall_Vehicle_Prefix(Pawn_CallTracker __instance, bool forceAggressive)
        {
            return !GiantVehicleBehaviorGuard.ShouldSuppressPawnCall(__instance?.pawn, forceAggressive);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_NeedsTracker), "ShouldHaveNeed")]
        public static void Pawn_NeedsTracker_ShouldHaveNeed_Vehicle_Postfix(Pawn ___pawn, NeedDef nd, ref bool __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            if (__result && GiantVehicleBehaviorGuard.ShouldSuppressVehicleNeed(___pawn, nd))
            {
                __result = false;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Need_MechEnergy), nameof(Need_MechEnergy.NeedInterval))]
        public static bool Need_MechEnergy_NeedInterval_Vehicle_Prefix(Need_MechEnergy __instance, Pawn ___pawn)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return true;
            }
            if (__instance != null && GiantVehicleBehaviorGuard.IsVehiclePawn(___pawn, out _, out _))
            {
                __instance.CurLevel = __instance.MaxLevel;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_FilthTracker), nameof(Pawn_FilthTracker.Notify_EnteredNewCell))]
        public static bool Pawn_FilthTracker_Notify_EnteredNewCell_Vehicle_Prefix(Pawn ___pawn)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return true;
            }
            return !GiantVehicleBehaviorGuard.IsVehiclePawn(___pawn, out _, out _);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(RestUtility), nameof(RestUtility.CanUseBedEver))]
        public static void RestUtility_CanUseBedEver_Vehicle_Postfix(Pawn p, ref bool __result)
        {
            if (__result && p != null && GiantFastLookup.TryGetRig(p) != null)
            {
                __result = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HealthAIUtility), nameof(HealthAIUtility.ShouldEverReceiveMedicalCareFromPlayer))]
        public static void HealthAIUtility_ShouldEverReceiveMedicalCareFromPlayer_Vehicle_Postfix(Pawn pawn, ref bool __result)
        {
            if (__result && GiantVehicleBehaviorGuard.IsVehiclePawn(pawn, out _, out _))
            {
                __result = false;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry), new[] { typeof(Thing) })]
        public static bool Pawn_CarryTracker_TryStartCarry_Single_Prefix(
            Pawn_CarryTracker __instance,
            Thing item,
            ref bool __result)
        {
            if (GiantVehicleBehaviorGuard.ShouldBlockSmallPawnCarry(__instance?.pawn, item))
            {
                __result = false;
                return false;
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry), new[] { typeof(Thing), typeof(int), typeof(bool) })]
        public static bool Pawn_CarryTracker_TryStartCarry_Count_Prefix(
            Pawn_CarryTracker __instance,
            Thing item,
            ref int __result)
        {
            if (GiantVehicleBehaviorGuard.ShouldBlockSmallPawnCarry(__instance?.pawn, item))
            {
                __result = 0;
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.AllSendablePawns))]
        public static void CaravanFormingUtility_AllSendablePawns_Vehicle_Postfix(List<Pawn> __result)
        {
            if (__result == null || __result.Count == 0)
            {
                return;
            }
            for (int i = __result.Count - 1; i >= 0; i--)
            {
                if (GiantVehicleBehaviorGuard.ShouldExcludeFromSendableCaravanPawns(__result[i]))
                {
                    __result.RemoveAt(i);
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CompOverseerSubject), nameof(CompOverseerSubject.CompInspectStringExtra))]
        public static void CompOverseerSubject_CompInspectStringExtra_Vehicle_Postfix(
            CompOverseerSubject __instance,
            ref string __result)
        {
            if (string.IsNullOrEmpty(__result) || !(__instance?.parent is Pawn pawn))
            {
                return;
            }
            if (pawn.Faction != Faction.OfPlayerSilentFail)
            {
                return;
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(pawn);
            if (cockpit != null)
            {
                if (cockpit.Props.allowPlayerControlWithoutOverseer && !cockpit.Props.requireOverseerWhenBiotechActive)
                {
                    __result = null;
                }
                return;
            }

            if (GiantVehicleBehaviorGuard.IsVehiclePawn(pawn, out _, out _) &&
                GiantPlayerControlUtility.CanOpenControlMenu(pawn))
            {
                __result = null;
            }
        }

        #endregion
    }
}
