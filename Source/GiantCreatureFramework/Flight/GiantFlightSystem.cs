using System;
using System.Collections.Generic;
using RimWorld;
using Unity.Collections;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public enum GiantFlightMode : byte
    {
        None = 0,
        AlwaysAirborne = 1,
        TakeoffAndLanding = 2
    }

    public enum GiantFlightState : byte
    {
        Grounded = 0,
        TakingOff = 1,
        Airborne = 2,
        Landing = 3
    }

    public enum FlightAltitudeTier : byte
    {
        LowSkim = 0,
        HighAirborne = 1
    }

    public struct ResolvedFlightLiftBone
    {
        public int boneIndex;
        public float sideSign;
    }

    [StaticConstructorOnStartup]
    internal static class GiantFlightGizmoAssets
    {
        public static readonly Texture2D FlightToggleIcon =
            ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", reportFailure: false)
            ?? ContentFinder<Texture2D>.Get("UI/Icons/GCF_DebugGizmo", reportFailure: false)
            ?? BaseContent.BadTex;
    }

    /// <summary>
    /// Opt-in flight locomotion specification mounted on &lt;flightLocomotion&gt; in GiantRigDef
    /// (or &lt;steering&gt;&lt;flightLocomotion&gt;).
    /// Strictly inert when null or flightMode == GiantFlightMode.None so all ground biological
    /// giants, walkers, and tracked/wheeled vehicles execute zero flight code and zero extra allocations.
    /// </summary>
    public class GiantFlightDef
    {
        public GiantFlightMode flightMode = GiantFlightMode.None;
        public FlightAltitudeTier altitudeTier = FlightAltitudeTier.HighAirborne;

        // Cruise & transition timing
        public float flightSpeedMultiplier = 1.0f;
        public float worldFlightSpeedMultiplier = 0f;
        public int takeoffDurationTicks = 35;

        public float ResolvedWorldFlightSpeedMultiplier =>
            worldFlightSpeedMultiplier > 0.01f ? worldFlightSpeedMultiplier : flightSpeedMultiplier;
        public int landingDurationTicks = 40;

        // Autonomous takeoff triggers (TakeoffAndLanding mode)
        public bool autoTakeoffInCombat = true;
        public float autoTakeoffMinDistanceCells = 10f;
        public bool autoTakeoffWhenPathBlocked = true;

        // Autonomous combat landing triggers & combat flight duration cap (TakeoffAndLanding mode)
        public bool autoLandInCombat = true;
        public float autoLandMaxDistanceCells = 14.5f;
        public int minAirborneTicksBeforeAutoLand = 48;
        public int maxCombatAirborneTicks = 0;
        public int postLandTakeoffCooldownTicks = 210;

        // Anatomical lift-part health linkage (<isFlightLiftPart>true</isFlightLiftPart>)
        public float minLiftEfficiencyToFly = 0.40f;
        public bool crashOnZeroLift = false;
        public float damagedLiftMinSpeedFactor = 0.45f;
        public int forcedLandingStunTicks = 180;

        // Opt-in takeoff windblast & heavy landing touchdown impact (0 = inert)
        public float takeoffWindblastRadius = 0f;
        public int takeoffWindblastDamage = 0;
        public int takeoffWindblastStunTicks = 0;
        public float landingImpactRadius = 0f;
        public int landingImpactDamage = 0;
        public int landingImpactStunTicks = 0;

        // Aerial pathfinding & obstacle traversal rules
        public bool canFlyOverWalls = true;
        public bool canFlyOverWaterAndLava = true;
        public bool blockedByThickRoof = true;
        public bool canHoverOverImpassable = true;

        // Melee immunity & air-to-air / air-to-ground rules
        public bool immuneToGroundMelee = true;
        public bool allowAirToAirMelee = true;

        // Visual elevation, hover oscillation, banking & separated ground drop shadow
        public float altitudeVisualOffsetZ = 1.50f;
        public float renderLayerAltitudeBoost = 3.2f;
        public float dropShadowScale = 0.80f;
        public float dropShadowAlpha = 0.40f;
        public Vector2 dropShadowOffsetXZ = new Vector2(0.42f, -0.45f);
        public float hoverBobAmplitude = 0.08f;
        public float hoverBobPeriodTicks = 120f;
        public float bankingRollMaxAngleDeg = 6.0f;
        public GiantAnimDef flightLocomotionAnim;
        public GiantAnimDef takeoffAnim;
        public GiantAnimDef landingAnim;

        [Unsaved]
        public ResolvedFlightLiftBone[] resolvedLiftBones = Array.Empty<ResolvedFlightLiftBone>();

        [Unsaved]
        public bool hasPortLiftGroup;

        [Unsaved]
        public bool hasStarboardLiftGroup;

        [Unsaved]
        public bool hasCenterLiftGroup;

        public bool IsActive => flightMode != GiantFlightMode.None;

        public void ResolveTopology(BoneNodeDef[] sortedNodes)
        {
            if (!IsActive || sortedNodes == null || sortedNodes.Length == 0)
            {
                resolvedLiftBones = Array.Empty<ResolvedFlightLiftBone>();
                hasPortLiftGroup = false;
                hasStarboardLiftGroup = false;
                hasCenterLiftGroup = false;
                return;
            }

            var list = new List<ResolvedFlightLiftBone>(4);
            bool port = false;
            bool starboard = false;
            bool center = false;

            for (int i = 0; i < sortedNodes.Length; i++)
            {
                BoneNodeDef node = sortedNodes[i];
                if (node == null || !node.isFlightLiftPart)
                {
                    continue;
                }

                float sign = node.locomotionSideSign;
                if (Mathf.Abs(sign) <= 0.05f && !string.IsNullOrEmpty(node.nodeId))
                {
                    string lower = node.nodeId.ToLowerInvariant();
                    if (lower.Contains("_left") || lower.EndsWith("_l", StringComparison.Ordinal) || lower.Contains("_l_") || lower.Contains("port"))
                    {
                        sign = -1f;
                    }
                    else if (lower.Contains("_right") || lower.EndsWith("_r", StringComparison.Ordinal) || lower.Contains("_r_") || lower.Contains("starboard"))
                    {
                        sign = 1f;
                    }
                }

                if (sign < -0.05f) port = true;
                else if (sign > 0.05f) starboard = true;
                else center = true;

                list.Add(new ResolvedFlightLiftBone
                {
                    boneIndex = i,
                    sideSign = sign
                });
            }

            resolvedLiftBones = list.Count > 0 ? list.ToArray() : Array.Empty<ResolvedFlightLiftBone>();
            hasPortLiftGroup = port;
            hasStarboardLiftGroup = starboard;
            hasCenterLiftGroup = center;
        }

        public IEnumerable<string> ValidateConfig(string rigDefName)
        {
            if (!IsActive)
            {
                yield break;
            }
            if (flightSpeedMultiplier <= 0.05f || flightSpeedMultiplier > 6.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> flightSpeedMultiplier ({flightSpeedMultiplier:F2}) must lie in (0.05, 6.0].";
            }
            if (takeoffDurationTicks < 4 || takeoffDurationTicks > 600)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> takeoffDurationTicks ({takeoffDurationTicks}) must lie in [4, 600].";
            }
            if (landingDurationTicks < 4 || landingDurationTicks > 600)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> landingDurationTicks ({landingDurationTicks}) must lie in [4, 600].";
            }
            if (maxCombatAirborneTicks < 0 || (maxCombatAirborneTicks > 0 && (maxCombatAirborneTicks < 60 || maxCombatAirborneTicks > 7200)))
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> maxCombatAirborneTicks ({maxCombatAirborneTicks}) must be 0 (unlimited) or lie in [60, 7200].";
            }
            if (minLiftEfficiencyToFly < 0.05f || minLiftEfficiencyToFly > 1.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> minLiftEfficiencyToFly ({minLiftEfficiencyToFly:F2}) must lie in [0.05, 1.0].";
            }
            if (damagedLiftMinSpeedFactor <= 0.05f || damagedLiftMinSpeedFactor > 1.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> damagedLiftMinSpeedFactor ({damagedLiftMinSpeedFactor:F2}) must lie in (0.05, 1.0].";
            }
            if (altitudeVisualOffsetZ < 0f || altitudeVisualOffsetZ > 6.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> altitudeVisualOffsetZ ({altitudeVisualOffsetZ:F2}) must lie in [0, 6.0].";
            }
            if (flightLocomotionAnim != null && !flightLocomotionAnim.loop)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> flightLocomotionAnim '{flightLocomotionAnim.defName}' must have <loop>true</loop>.";
            }
            if (takeoffAnim != null && takeoffAnim.loop)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> takeoffAnim '{takeoffAnim.defName}' must have <loop>false</loop>.";
            }
            if (landingAnim != null && landingAnim.loop)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightLocomotion> landingAnim '{landingAnim.defName}' must have <loop>false</loop>.";
            }
        }
    }

    public readonly struct GiantAerialCustomizerKey : IEquatable<GiantAerialCustomizerKey>
    {
        public readonly int clearanceRadius;
        public readonly bool canFlyOverWalls;
        public readonly bool canFlyOverWaterAndLava;
        public readonly bool blockedByThickRoof;

        public GiantAerialCustomizerKey(
            int clearanceRadius,
            bool canFlyOverWalls,
            bool canFlyOverWaterAndLava,
            bool blockedByThickRoof)
        {
            this.clearanceRadius = clearanceRadius;
            this.canFlyOverWalls = canFlyOverWalls;
            this.canFlyOverWaterAndLava = canFlyOverWaterAndLava;
            this.blockedByThickRoof = blockedByThickRoof;
        }

        public bool Equals(GiantAerialCustomizerKey other)
        {
            return clearanceRadius == other.clearanceRadius
                && canFlyOverWalls == other.canFlyOverWalls
                && canFlyOverWaterAndLava == other.canFlyOverWaterAndLava
                && blockedByThickRoof == other.blockedByThickRoof;
        }

        public override bool Equals(object obj)
        {
            return obj is GiantAerialCustomizerKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = clearanceRadius;
                hash = (hash * 397) ^ (canFlyOverWalls ? 1 : 0);
                hash = (hash * 397) ^ (canFlyOverWaterAndLava ? 2 : 0);
                hash = (hash * 397) ^ (blockedByThickRoof ? 4 : 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// Worker-thread-safe Burst path customizer for airborne giant units.
    /// Supplies uniform low aerial traversal costs across walls, doors, rivers, deep water, and lava
    /// while keeping Overhead Mountain (thick roof) cells impassable (10000) without mutating shared ground PathGrids.
    /// </summary>
    public sealed class GiantAerialPathCustomizer : PathRequest.IPathGridCustomizer, IDisposable
    {
        private NativeArray<ushort> nativeOffsets;
        private NativeArray<int> nativeDirectCosts;
        private readonly ushort[] managedOffsets;
        private readonly int[] managedDirectCosts;
        private bool dirty = true;

        public GiantAerialPathCustomizer(int numGridCells)
        {
            managedOffsets = new ushort[numGridCells];
            managedDirectCosts = new int[numGridCells];
            nativeOffsets = new NativeArray<ushort>(numGridCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            nativeDirectCosts = new NativeArray<int>(numGridCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        public bool IsCreated => nativeOffsets.IsCreated && nativeDirectCosts.IsCreated;
        public ushort[] ManagedOffsets => managedOffsets;
        public int[] ManagedDirectCosts => managedDirectCosts;
        public NativeArray<int>.ReadOnly DirectCostGrid => nativeDirectCosts.AsReadOnly();

        public void MarkDirty()
        {
            dirty = true;
        }

        public bool SyncIfDirty()
        {
            if (!dirty || !IsCreated)
            {
                return false;
            }
            NativeArray<ushort>.Copy(managedOffsets, nativeOffsets);
            NativeArray<int>.Copy(managedDirectCosts, nativeDirectCosts);
            dirty = false;
            return true;
        }

        public NativeArray<ushort> GetOffsetGrid()
        {
            return nativeOffsets;
        }

        public void Dispose()
        {
            if (nativeOffsets.IsCreated)
            {
                nativeOffsets.Dispose();
            }
            if (nativeDirectCosts.IsCreated)
            {
                nativeDirectCosts.Dispose();
            }
        }
    }

    /// <summary>
    /// Resolves unobstructed dry ground cells before touchdown so TakeoffAndLanding units
    /// hovering over walls, deep water, or lava glide to a valid standable pad before landing.
    /// </summary>
    public static class SafeLandingSpotResolver
    {
        public static bool IsLandingFootprintClearHeadless(
            IntVec3 mapSize,
            IntVec3 candidateAnchor,
            IReadOnlyList<IntVec3> localCollisionOffsets,
            Func<IntVec3, bool> isCellStandableGround)
        {
            if (isCellStandableGround == null)
            {
                return false;
            }
            if ((uint)candidateAnchor.x >= (uint)mapSize.x || (uint)candidateAnchor.z >= (uint)mapSize.z)
            {
                return false;
            }
            if (!isCellStandableGround(candidateAnchor))
            {
                return false;
            }

            if (localCollisionOffsets != null && localCollisionOffsets.Count > 0)
            {
                for (int i = 0; i < localCollisionOffsets.Count; i++)
                {
                    IntVec3 c = candidateAnchor + localCollisionOffsets[i];
                    if ((uint)c.x >= (uint)mapSize.x || (uint)c.z >= (uint)mapSize.z || !isCellStandableGround(c))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static bool TryResolveSafeLandingCellHeadless(
            IntVec3 mapSize,
            IntVec3 preferredOrigin,
            IReadOnlyList<IntVec3> localCollisionOffsets,
            Func<IntVec3, bool> isCellStandableGround,
            int maxSearchRadius,
            out IntVec3 safeCell)
        {
            if (IsLandingFootprintClearHeadless(mapSize, preferredOrigin, localCollisionOffsets, isCellStandableGround))
            {
                safeCell = preferredOrigin;
                return true;
            }

            int clampedRadius = Mathf.Clamp(maxSearchRadius, 1, 32);
            for (int r = 1; r <= clampedRadius; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r)
                        {
                            continue;
                        }
                        IntVec3 cand = new IntVec3(preferredOrigin.x + dx, 0, preferredOrigin.z + dz);
                        if (IsLandingFootprintClearHeadless(mapSize, cand, localCollisionOffsets, isCellStandableGround))
                        {
                            safeCell = cand;
                            return true;
                        }
                    }
                }
            }

            safeCell = IntVec3.Invalid;
            return false;
        }

        public static bool IsCellValidForLandingGround(Map map, Pawn pawn, CompGiantRig rig, IntVec3 c)
        {
            if (map == null || !c.InBounds(map) || c.Fogged(map))
            {
                return false;
            }
            if (GiantDestructionUtility.HasThickRoofAt(map, c))
            {
                return false;
            }

            Building edifice = c.GetEdifice(map);
            if (edifice != null && (edifice.def.passability == Traversability.Impassable || edifice is Building_Door))
            {
                return false;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp != null && rig != null && mapComp.GetCollisionOwnerAt(c) is CompGiantRig other && other != rig)
            {
                return false;
            }

            GiantAquaticDef aq = rig?.RigDef?.ActiveAquaticLocomotion;
            if (aq != null && aq.IsActive)
            {
                return GiantAquaticUtility.IsCellPassableForAquatic(map, c, pawn, rig, aq);
            }

            if (!c.Standable(map))
            {
                return false;
            }

            TerrainDef terrain = map.terrainGrid?.TerrainAt(c);
            if (terrain != null && terrain.passability == Traversability.Impassable)
            {
                return false;
            }

            return true;
        }

        public static bool IsAnchorFootprintValidForLandingGround(Map map, Pawn pawn, CompGiantRig rig, IntVec3 candidateAnchor)
        {
            if (map == null)
            {
                return false;
            }
            return IsLandingFootprintClearHeadless(
                map.Size,
                candidateAnchor,
                rig?.Footprint?.LandingCollisionOffsets,
                c => IsCellValidForLandingGround(map, pawn, rig, c));
        }

        public static bool TryResolveSafeLandingCell(
            Map map,
            Pawn pawn,
            CompGiantRig rig,
            IntVec3 preferredOrigin,
            int maxSearchRadius,
            out IntVec3 safeCell)
        {
            if (map == null)
            {
                safeCell = IntVec3.Invalid;
                return false;
            }

            IReadOnlyList<IntVec3> offsets = rig?.Footprint?.LandingCollisionOffsets;
            if (TryResolveSafeLandingCellHeadless(
                map.Size,
                preferredOrigin,
                offsets,
                c => IsCellValidForLandingGround(map, pawn, rig, c),
                maxSearchRadius,
                out safeCell))
            {
                return true;
            }

            return TryResolveSafeLandingCellHeadless(
                map.Size,
                preferredOrigin,
                null,
                c => IsCellValidForLandingGround(map, pawn, rig, c),
                maxSearchRadius,
                out safeCell);
        }
    }

    /// <summary>
    /// Per-instance flight state machine, lift-efficiency monitor, and immediate path-rebuild controller.
    /// Allocated ONLY on giants declaring an active &lt;flightLocomotion&gt; block.
    /// </summary>
    public sealed class GiantFlightRuntime
    {
        public GiantFlightDef Def { get; }

        private GiantFlightState flightState;
        private float altitude01;
        private int stateElapsedTicks;
        private bool desiredAirborne;
        private bool wasForcedCrashLanding;
        private float liftEfficiency = 1f;
        private float liftSpeedFactor = 1f;
        private bool canMaintainLift = true;
        private float hoverPhaseTicks;
        private float currentBankAngleDeg;
        private int autoTakeoffCheckCooldown;
        private int airborneSustainedTicks;
        private int combatAirborneTicks;
        private int postLandTakeoffLockoutTicks;
        private int autoLandCheckCooldown;
        private int landingGlideTicks;
        private int landingRescanCooldown;
        private int lastCombatScanTick = -999;
        private int lastWindblastTick = -9999;
        private bool cachedHasNearbyHostile;
        private IntVec3 pendingLandingCell = IntVec3.Invalid;
        private CompGiantRig cachedRigComp;

        public int PathRebuildCount { get; private set; }
        public int ForcedCrashCount { get; private set; }
        public int BrokenMeleePursuersCount { get; private set; }

        public GiantFlightState FlightState => flightState;
        public float CurrentAltitude01 => altitude01;
        public float LiftEfficiency => liftEfficiency;
        public float LiftSpeedFactor => liftSpeedFactor;
        public bool CanMaintainLift => canMaintainLift;
        public bool DesiredAirborne => desiredAirborne;
        public float CurrentBankAngleDeg => currentBankAngleDeg;
        public float HoverPhaseTicks => hoverPhaseTicks;
        public int AirborneSustainedTicks => airborneSustainedTicks;
        public int CombatAirborneTicks => combatAirborneTicks;
        public int PostLandTakeoffLockoutTicks => postLandTakeoffLockoutTicks;
        public IntVec3 PendingLandingCell => pendingLandingCell;

        public void RestoreVisualContinuity(float prevHoverPhaseTicks, float prevBankDeg)
        {
            hoverPhaseTicks = prevHoverPhaseTicks;
            currentBankAngleDeg = prevBankDeg;
        }

        public bool IsCurrentlyAirborne =>
            flightState == GiantFlightState.Airborne
            || (flightState == GiantFlightState.TakingOff && altitude01 >= 0.50f)
            || flightState == GiantFlightState.Landing;

        public bool IsAirborneOrTakingOff =>
            flightState == GiantFlightState.Airborne
            || flightState == GiantFlightState.TakingOff
            || flightState == GiantFlightState.Landing;

        public bool IsImmuneToGroundMelee =>
            Def.immuneToGroundMelee && IsCurrentlyAirborne;

        public float EffectiveFlightSpeedMultiplier =>
            IsCurrentlyAirborne
                ? Mathf.Max(0.15f, Def.flightSpeedMultiplier * liftSpeedFactor)
                : 1f;

        public float WorldFlightSpeedMultiplier =>
            Mathf.Max(0.15f, Def.ResolvedWorldFlightSpeedMultiplier * liftSpeedFactor);

        public GiantFlightRuntime(GiantRigDef rigDef, GiantFlightDef def)
        {
            Def = def ?? throw new ArgumentNullException(nameof(def));
            if (rigDef != null)
            {
                Def.ResolveTopology(rigDef.SortedNodes);
            }
            ResetForSpawn(unitSeed: 0);
        }

        public void ResetForSpawn(int unitSeed)
        {
            hoverPhaseTicks = (unitSeed & 255) * 3.7f;
            currentBankAngleDeg = 0f;
            wasForcedCrashLanding = false;
            pendingLandingCell = IntVec3.Invalid;
            autoTakeoffCheckCooldown = 8;
            airborneSustainedTicks = 0;
            combatAirborneTicks = 0;
            postLandTakeoffLockoutTicks = 0;
            autoLandCheckCooldown = 0;
            landingGlideTicks = 0;
            landingRescanCooldown = 0;
            lastCombatScanTick = -999;
            lastWindblastTick = -9999;
            cachedHasNearbyHostile = false;

            if (Def.flightMode == GiantFlightMode.AlwaysAirborne)
            {
                flightState = GiantFlightState.Airborne;
                altitude01 = 1f;
                desiredAirborne = true;
                stateElapsedTicks = 0;
            }
            else
            {
                flightState = GiantFlightState.Grounded;
                altitude01 = 0f;
                desiredAirborne = false;
                stateElapsedTicks = 0;
            }
        }

        public void ForceRestoreAirborneAfterMapEntry(CompGiantRig rigComp)
        {
            if (Def.flightMode == GiantFlightMode.None)
            {
                return;
            }
            if (rigComp != null)
            {
                cachedRigComp = rigComp;
            }
            bool prevAir = IsCurrentlyAirborne;
            flightState = GiantFlightState.Airborne;
            altitude01 = 1f;
            desiredAirborne = true;
            stateElapsedTicks = 0;
            wasForcedCrashLanding = false;
            pendingLandingCell = IntVec3.Invalid;
            landingGlideTicks = 0;
            if (!prevAir && rigComp != null)
            {
                OnAirborneDomainChanged(rigComp);
            }
            else
            {
                rigComp?.RebuildFootprintNow(notifyMap: true);
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref flightState, "gcf_flightState",
                Def.flightMode == GiantFlightMode.AlwaysAirborne ? GiantFlightState.Airborne : GiantFlightState.Grounded);
            Scribe_Values.Look(ref altitude01, "gcf_flightAlt01",
                Def.flightMode == GiantFlightMode.AlwaysAirborne ? 1f : 0f);
            Scribe_Values.Look(ref stateElapsedTicks, "gcf_flightStateElapsed", 0);
            Scribe_Values.Look(ref desiredAirborne, "gcf_flightDesiredAir",
                Def.flightMode == GiantFlightMode.AlwaysAirborne);
            Scribe_Values.Look(ref wasForcedCrashLanding, "gcf_flightForcedCrash", defaultValue: false);
            Scribe_Values.Look(ref pendingLandingCell, "gcf_flightPendingLandCell", IntVec3.Invalid);
            Scribe_Values.Look(ref airborneSustainedTicks, "gcf_flightAirborneSustained", 0);
            Scribe_Values.Look(ref combatAirborneTicks, "gcf_flightCombatAirborneTicks", 0);
            Scribe_Values.Look(ref postLandTakeoffLockoutTicks, "gcf_flightPostLandLockout", 0);
        }

        public float EvaluateHoverBobOffset()
        {
            if (altitude01 <= 1e-3f || Def.hoverBobAmplitude <= 1e-4f)
            {
                return 0f;
            }
            float period = Mathf.Max(16f, Def.hoverBobPeriodTicks);
            float wave = Mathf.Sin((hoverPhaseTicks / period) * Mathf.PI * 2f);
            return wave * Def.hoverBobAmplitude * altitude01;
        }

        public Vector2 ComputeVisualOffsetXZ()
        {
            if (altitude01 <= 1e-3f)
            {
                return Vector2.zero;
            }
            return new Vector2(0f, altitude01 * Def.altitudeVisualOffsetZ + EvaluateHoverBobOffset());
        }

        public float ComputeRenderAltitudeBoost()
        {
            if (altitude01 <= 1e-3f)
            {
                return 0f;
            }
            return altitude01 * Def.renderLayerAltitudeBoost;
        }

        /// <summary>
        /// Evaluates anatomical lift efficiency across all &lt;isFlightLiftPart&gt; bones.
        /// If bilateral wings exist and either entire wing side is severed, lift drops below takeoff/cruise threshold.
        /// </summary>
        public bool EvaluateLiftEfficiency(
            ReadOnlySpan<bool> partMissingMask,
            ReadOnlySpan<float> partHealthPct,
            CompGiantRig rigComp = null)
        {
            ResolvedFlightLiftBone[] liftBones = Def.resolvedLiftBones;
            if (liftBones == null || liftBones.Length == 0)
            {
                liftEfficiency = 1f;
                liftSpeedFactor = 1f;
                canMaintainLift = true;
                return false;
            }

            float totalHp = 0f;
            int totalCount = 0;
            float portHp = 0f;
            int portCount = 0;
            float starboardHp = 0f;
            int starboardCount = 0;

            for (int i = 0; i < liftBones.Length; i++)
            {
                int bIdx = liftBones[i].boneIndex;
                bool missing = (uint)bIdx < (uint)partMissingMask.Length && partMissingMask[bIdx];
                float hp = missing
                    ? 0f
                    : ((uint)bIdx < (uint)partHealthPct.Length ? Mathf.Clamp01(partHealthPct[bIdx]) : 1f);

                totalHp += hp;
                totalCount++;
                if (liftBones[i].sideSign < -0.05f)
                {
                    portHp += hp;
                    portCount++;
                }
                else if (liftBones[i].sideSign > 0.05f)
                {
                    starboardHp += hp;
                    starboardCount++;
                }
            }

            float avgLift = totalCount > 0 ? (totalHp / totalCount) : 1f;
            if (Def.hasPortLiftGroup && Def.hasStarboardLiftGroup)
            {
                bool portSevered = portCount > 0 && portHp <= 0.01f;
                bool starboardSevered = starboardCount > 0 && starboardHp <= 0.01f;
                if (portSevered || starboardSevered)
                {
                    avgLift = Mathf.Min(avgLift, Def.minLiftEfficiencyToFly * 0.45f);
                }
            }

            liftEfficiency = Mathf.Clamp01(avgLift);
            liftSpeedFactor = Mathf.Lerp(Def.damagedLiftMinSpeedFactor, 1f, liftEfficiency);

            bool mustCrashWhenLowLift = Def.flightMode == GiantFlightMode.TakeoffAndLanding || Def.crashOnZeroLift;
            bool thresholdOk = Def.flightMode == GiantFlightMode.AlwaysAirborne && !Def.crashOnZeroLift
                ? true
                : (Def.flightMode == GiantFlightMode.AlwaysAirborne
                    ? liftEfficiency > 0.02f
                    : liftEfficiency >= Def.minLiftEfficiencyToFly);

            canMaintainLift = thresholdOk;

            if (mustCrashWhenLowLift && !canMaintainLift &&
                (flightState == GiantFlightState.Airborne || flightState == GiantFlightState.TakingOff))
            {
                TriggerForcedCrashLanding(rigComp);
                return true;
            }

            return false;
        }

        private static bool HasThickRoofInTakeoffFootprint(Map map, CompGiantRig rigComp, IntVec3 origin)
        {
            if (map == null || !origin.InBounds(map))
            {
                return false;
            }
            if (GiantDestructionUtility.HasThickRoofAt(map, origin))
            {
                return true;
            }
            IReadOnlyList<IntVec3> offsets = rigComp?.Footprint?.LandingCollisionOffsets;
            if (offsets != null)
            {
                for (int i = 0; i < offsets.Count; i++)
                {
                    IntVec3 c = origin + offsets[i];
                    if (c.InBounds(map) && GiantDestructionUtility.HasThickRoofAt(map, c))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool TryCommandTakeoff(CompGiantRig rigComp, out string failReason)
        {
            failReason = null;
            if (rigComp != null)
            {
                cachedRigComp = rigComp;
            }
            if (Def.flightMode == GiantFlightMode.None)
            {
                failReason = "Flight disabled.";
                return false;
            }
            Pawn pawn = rigComp?.Pawn;
            if (pawn != null)
            {
                if (pawn.Dead || pawn.Downed || (Current.Game != null && (pawn.stances?.stunner?.Stunned ?? false)))
                {
                    failReason = GiantFlightUtility.SafeTranslate("Incapable", "Incapable");
                    return false;
                }
                if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit && cockpit.IsDormantUnmanned)
                {
                    failReason = GiantFlightUtility.SafeTranslate("GCF_CrewDisabledUnmanned", "Disabled: unit in standby");
                    return false;
                }
            }
            if (!canMaintainLift)
            {
                failReason = GiantFlightUtility.SafeTranslate("GCF_FlightFail_LiftDamaged", "Lift organs too damaged to fly");
                return false;
            }
            if (pawn != null && Def.blockedByThickRoof && pawn.Map != null &&
                HasThickRoofInTakeoffFootprint(pawn.Map, rigComp, pawn.Position))
            {
                failReason = GiantFlightUtility.SafeTranslate("GCF_FlightFail_ThickRoof", "Blocked by overhead mountain roof");
                return false;
            }
            if (flightState == GiantFlightState.Grounded && IsCombatTakeoffLockedOut(rigComp, out string cooldownReason))
            {
                failReason = cooldownReason;
                return false;
            }

            desiredAirborne = true;
            wasForcedCrashLanding = false;
            pendingLandingCell = IntVec3.Invalid;
            landingGlideTicks = 0;
            landingRescanCooldown = 0;

            if (flightState == GiantFlightState.Airborne || flightState == GiantFlightState.TakingOff)
            {
                return true;
            }

            GiantFlightState prevState = flightState;
            bool prevAirborne = IsCurrentlyAirborne;
            flightState = GiantFlightState.TakingOff;
            stateElapsedTicks = Mathf.RoundToInt(altitude01 * Def.takeoffDurationTicks);
            if (Def.takeoffAnim != null)
            {
                rigComp?.PlayOneShotFlightTransition(Def.takeoffAnim);
            }

            int nowTick = Current.Game?.tickManager?.TicksGame ?? -1;
            bool windblastCooldownOk = nowTick < 0 || nowTick < lastWindblastTick || (nowTick - lastWindblastTick >= 60);
            if (prevState == GiantFlightState.Grounded && windblastCooldownOk && pawn != null && pawn.Spawned && pawn.Map != null)
            {
                lastWindblastTick = nowTick;
                GiantVfxSystem.SpawnFlightLandingVfx(pawn, pawn.DrawPos, 1.15f, 0.08f);
                if (Def.takeoffWindblastRadius > 0.5f && (Def.takeoffWindblastDamage > 0 || Def.takeoffWindblastStunTicks > 0))
                {
                    ApplyFlightTransitionShockwave(
                        pawn,
                        pawn.Map,
                        pawn.Position,
                        Def.takeoffWindblastRadius,
                        Def.takeoffWindblastDamage,
                        0.28f,
                        Def.takeoffWindblastStunTicks);
                }
            }

            if (IsCurrentlyAirborne != prevAirborne)
            {
                OnAirborneDomainChanged(rigComp);
            }
            return true;
        }

        public bool TryCommandLanding(CompGiantRig rigComp, out string failReason)
        {
            failReason = null;
            if (Def.flightMode == GiantFlightMode.AlwaysAirborne && !wasForcedCrashLanding && canMaintainLift)
            {
                failReason = "Always-airborne unit cannot land.";
                return false;
            }

            desiredAirborne = false;
            if (flightState == GiantFlightState.Grounded || flightState == GiantFlightState.Landing)
            {
                return true;
            }

            Pawn pawn = rigComp?.Pawn;
            Map map = pawn?.Map;
            if (map != null && pawn.Spawned)
            {
                if (SafeLandingSpotResolver.TryResolveSafeLandingCell(map, pawn, rigComp, pawn.Position, 18, out IntVec3 safeCell))
                {
                    pendingLandingCell = safeCell;
                    if (safeCell != pawn.Position && pawn.pather != null)
                    {
                        pawn.pather.StartPath(safeCell, PathEndMode.OnCell);
                    }
                }
            }

            flightState = GiantFlightState.Landing;
            landingGlideTicks = 0;
            landingRescanCooldown = 0;
            stateElapsedTicks = Mathf.RoundToInt((1f - altitude01) * Def.landingDurationTicks);
            if (Def.landingAnim != null)
            {
                rigComp?.PlayOneShotFlightTransition(Def.landingAnim);
            }
            return true;
        }

        public void TriggerForcedCrashLanding(CompGiantRig rigComp)
        {
            wasForcedCrashLanding = true;
            desiredAirborne = false;
            ForcedCrashCount++;

            if (flightState == GiantFlightState.Grounded)
            {
                return;
            }

            Pawn pawn = rigComp?.Pawn;
            Map map = pawn?.Map;
            if (map != null && pawn.Spawned &&
                SafeLandingSpotResolver.TryResolveSafeLandingCell(map, pawn, rigComp, pawn.Position, 14, out IntVec3 safeCell))
            {
                pendingLandingCell = safeCell;
            }

            flightState = GiantFlightState.Landing;
            stateElapsedTicks = Mathf.RoundToInt((1f - altitude01) * Mathf.Max(8, Def.landingDurationTicks / 2));
            if (Def.landingAnim != null)
            {
                rigComp?.PlayOneShotFlightTransition(Def.landingAnim);
            }
        }

        public void StepHeadlessTick(float headingStepDeg = 0f, Action onDomainFlipped = null)
        {
            hoverPhaseTicks += 1f;
            float targetBank = Mathf.Clamp(-headingStepDeg * 2.4f, -Def.bankingRollMaxAngleDeg, Def.bankingRollMaxAngleDeg) * altitude01;
            currentBankAngleDeg = Mathf.Lerp(currentBankAngleDeg, targetBank, 0.22f);

            if (Def.flightMode == GiantFlightMode.AlwaysAirborne && !wasForcedCrashLanding && canMaintainLift)
            {
                flightState = GiantFlightState.Airborne;
                altitude01 = 1f;
                return;
            }

            bool prevAirborne = IsCurrentlyAirborne;
            switch (flightState)
            {
                case GiantFlightState.TakingOff:
                {
                    stateElapsedTicks++;
                    int dur = Mathf.Max(4, Def.takeoffDurationTicks);
                    float t = Mathf.Clamp01((float)stateElapsedTicks / dur);
                    altitude01 = t * t * (3f - 2f * t);
                    if (stateElapsedTicks >= dur)
                    {
                        flightState = GiantFlightState.Airborne;
                        altitude01 = 1f;
                        stateElapsedTicks = 0;
                    }
                    break;
                }

                case GiantFlightState.Landing:
                {
                    stateElapsedTicks++;
                    int dur = wasForcedCrashLanding
                        ? Mathf.Max(6, Def.landingDurationTicks / 2)
                        : Mathf.Max(4, Def.landingDurationTicks);
                    float t = Mathf.Clamp01((float)stateElapsedTicks / dur);
                    float smooth = t * t * (3f - 2f * t);
                    altitude01 = 1f - smooth;
                    if (stateElapsedTicks >= dur)
                    {
                        flightState = GiantFlightState.Grounded;
                        altitude01 = 0f;
                        stateElapsedTicks = 0;
                        combatAirborneTicks = 0;
                        postLandTakeoffLockoutTicks = Mathf.Max(postLandTakeoffLockoutTicks, Def.postLandTakeoffCooldownTicks);
                    }
                    break;
                }
            }

            if (IsCurrentlyAirborne != prevAirborne)
            {
                PathRebuildCount++;
                onDomainFlipped?.Invoke();
            }
        }

        public void StepTick(CompGiantRig rigComp, float lastStepHeadingDeltaDeg)
        {
            if (rigComp != null)
            {
                cachedRigComp = rigComp;
            }
            Pawn pawn = rigComp?.Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead)
            {
                return;
            }
            if (pawn.Downed)
            {
                bool prevAir = IsCurrentlyAirborne;
                StepCorpseDescentTick(1f, rigComp);
                if (IsCurrentlyAirborne != prevAir)
                {
                    if (!IsCurrentlyAirborne)
                    {
                        RelocateToSafeGroundIfNeeded(pawn, pawn, rigComp, pendingLandingCell.IsValid ? pendingLandingCell : pawn.Position);
                        pendingLandingCell = IntVec3.Invalid;
                    }
                    OnAirborneDomainChanged(rigComp);
                }
                return;
            }

            hoverPhaseTicks += 1f;
            if (rigComp.FlightVehicleLocomotion is FlightVehicleLocomotionRuntime fveh)
            {
                currentBankAngleDeg = -fveh.CurrentBankAngleDeg * altitude01;
            }
            else
            {
                float targetBank = Mathf.Clamp(-lastStepHeadingDeltaDeg * 2.4f, -Def.bankingRollMaxAngleDeg, Def.bankingRollMaxAngleDeg) * altitude01;
                currentBankAngleDeg = Mathf.Lerp(currentBankAngleDeg, targetBank, 0.22f);
            }

            if (Def.flightMode == GiantFlightMode.AlwaysAirborne && !wasForcedCrashLanding && canMaintainLift)
            {
                bool wasAir = IsCurrentlyAirborne;
                flightState = GiantFlightState.Airborne;
                altitude01 = 1f;
                desiredAirborne = true;
                if (!wasAir)
                {
                    OnAirborneDomainChanged(rigComp);
                }
                else if (((Current.Game?.tickManager?.TicksGame ?? 0) & 15) == 0)
                {
                    BreakGroundMeleePursuers(rigComp);
                }
                return;
            }

            if (flightState == GiantFlightState.Grounded)
            {
                combatAirborneTicks = 0;
                if (postLandTakeoffLockoutTicks > 0)
                {
                    postLandTakeoffLockoutTicks--;
                }
            }

            if (Def.flightMode == GiantFlightMode.TakeoffAndLanding
                && flightState == GiantFlightState.Grounded
                && canMaintainLift
                && postLandTakeoffLockoutTicks <= 0
                && !pawn.Downed
                && !IsUnderHumanPilotOrActiveOrder(pawn))
            {
                if (autoTakeoffCheckCooldown > 0)
                {
                    autoTakeoffCheckCooldown--;
                }
                else
                {
                    autoTakeoffCheckCooldown = 15;
                    CheckAutonomousTakeoff(rigComp);
                }
            }

            bool prevAirborne = IsCurrentlyAirborne;
            switch (flightState)
            {
                case GiantFlightState.TakingOff:
                {
                    stateElapsedTicks++;
                    int dur = Mathf.Max(4, Def.takeoffDurationTicks);
                    float t = Mathf.Clamp01((float)stateElapsedTicks / dur);
                    altitude01 = t * t * (3f - 2f * t);
                    if (stateElapsedTicks >= dur)
                    {
                        flightState = GiantFlightState.Airborne;
                        altitude01 = 1f;
                        stateElapsedTicks = 0;
                        airborneSustainedTicks = 0;
                        autoLandCheckCooldown = 12;
                        RebuildActivePathOnFlightChange(rigComp);
                    }
                    break;
                }

                case GiantFlightState.Airborne:
                {
                    altitude01 = 1f;
                    airborneSustainedTicks++;
                    if (!desiredAirborne)
                    {
                        TryCommandLanding(rigComp, out _);
                    }
                    else
                    {
                        if (((Current.Game?.tickManager?.TicksGame ?? 0) & 15) == 0)
                        {
                            BreakGroundMeleePursuers(rigComp);
                        }
                        bool inCombatNow = Def.flightMode == GiantFlightMode.TakeoffAndLanding
                            && Def.maxCombatAirborneTicks > 0
                            && IsInActiveCombat(rigComp);
                        if (StepCombatFlightCapTick(
                                inCombatNow,
                                GiantMotionOverrideRegistry.HasActiveDisplacement(pawn),
                                () => TryCommandLanding(rigComp, out _)))
                        {
                            break;
                        }
                        if (Def.flightMode == GiantFlightMode.TakeoffAndLanding
                            && (Def.autoLandInCombat || !GiantVehicleBehaviorGuard.IsVehiclePawn(pawn, out _, out _))
                            && !pawn.Downed
                            && !IsUnderHumanPilotOrActiveOrder(pawn)
                            && airborneSustainedTicks >= Def.minAirborneTicksBeforeAutoLand)
                        {
                            if (autoLandCheckCooldown > 0)
                            {
                                autoLandCheckCooldown--;
                            }
                            else
                            {
                                autoLandCheckCooldown = 12;
                                CheckAutonomousLanding(rigComp);
                            }
                        }
                    }
                    break;
                }

                case GiantFlightState.Landing:
                {
                    Map map = pawn.Map;
                    if (map != null &&
                        !SafeLandingSpotResolver.IsAnchorFootprintValidForLandingGround(map, pawn, rigComp, pawn.Position) &&
                        !wasForcedCrashLanding &&
                        landingGlideTicks < 90)
                    {
                        bool needRescan = !pendingLandingCell.IsValid ||
                            !pendingLandingCell.InBounds(map) ||
                            !SafeLandingSpotResolver.IsAnchorFootprintValidForLandingGround(map, pawn, rigComp, pendingLandingCell);
                        if (needRescan)
                        {
                            if (landingRescanCooldown > 0)
                            {
                                landingRescanCooldown--;
                            }
                            else
                            {
                                landingRescanCooldown = 12;
                                if (SafeLandingSpotResolver.TryResolveSafeLandingCell(map, pawn, rigComp, pawn.Position, 18, out IntVec3 freshPad))
                                {
                                    pendingLandingCell = freshPad;
                                }
                            }
                        }
                        if (pendingLandingCell.IsValid && pawn.Position != pendingLandingCell)
                        {
                            landingGlideTicks++;
                            altitude01 = Mathf.Max(0.35f, altitude01);
                            if (pawn.pather != null && (!pawn.pather.Moving || pawn.pather.Destination.Cell != pendingLandingCell))
                            {
                                pawn.pather.StartPath(pendingLandingCell, PathEndMode.OnCell);
                            }
                            break;
                        }
                    }

                    stateElapsedTicks++;
                    int dur = wasForcedCrashLanding
                        ? Mathf.Max(6, Def.landingDurationTicks / 2)
                        : Mathf.Max(4, Def.landingDurationTicks);
                    float t = Mathf.Clamp01((float)stateElapsedTicks / dur);
                    float smooth = t * t * (3f - 2f * t);
                    altitude01 = 1f - smooth;

                    if (stateElapsedTicks >= dur)
                    {
                        CompleteTouchdown(rigComp);
                        prevAirborne = IsCurrentlyAirborne;
                    }
                    break;
                }
            }

            if (IsCurrentlyAirborne != prevAirborne)
            {
                OnAirborneDomainChanged(rigComp);
            }
        }

        public bool StepCombatFlightCapTick(bool inCombat, bool hasActiveDisplacement, Action triggerLanding)
        {
            if (flightState == GiantFlightState.Grounded)
            {
                combatAirborneTicks = 0;
                if (postLandTakeoffLockoutTicks > 0)
                {
                    postLandTakeoffLockoutTicks--;
                }
                return false;
            }

            if (flightState != GiantFlightState.Airborne
                || Def.flightMode != GiantFlightMode.TakeoffAndLanding
                || Def.maxCombatAirborneTicks <= 0)
            {
                return false;
            }

            if (inCombat)
            {
                combatAirborneTicks++;
                if (combatAirborneTicks >= Def.maxCombatAirborneTicks && !hasActiveDisplacement)
                {
                    triggerLanding?.Invoke();
                    return true;
                }
            }
            else
            {
                combatAirborneTicks = Mathf.Max(0, combatAirborneTicks - 2);
            }

            return false;
        }

        public bool IsCombatTakeoffLockedOut(CompGiantRig rigComp, out string failReason, bool? inCombatOverride = null)
        {
            failReason = null;
            if (Def.flightMode != GiantFlightMode.TakeoffAndLanding
                || (Def.maxCombatAirborneTicks <= 0 && Def.postLandTakeoffCooldownTicks <= 0)
                || postLandTakeoffLockoutTicks <= 0)
            {
                return false;
            }
            bool activeCombat = inCombatOverride ?? (rigComp != null && IsInActiveCombat(rigComp));
            if (!activeCombat)
            {
                return false;
            }
            float secs = Mathf.Max(0.1f, postLandTakeoffLockoutTicks / 60f);
            string secStr = $"{secs:F1}";
            failReason = GiantFlightUtility.SafeTranslateFormatted(
                "GCF_FlightFail_CombatCooldown",
                $"Combat flight recovering ({secStr}s)",
                secStr);
            return true;
        }

        public bool IsInActiveCombat(CompGiantRig rigComp)
        {
            Pawn pawn = rigComp?.Pawn;
            Map map = pawn?.Map;
            if (pawn == null || map == null || !pawn.Spawned || pawn.Dead)
            {
                return false;
            }
            if (rigComp.PartCombat != null && rigComp.PartCombat.HasValidForcedTarget())
            {
                return true;
            }
            Thing pursued = rigComp.ResolvePursuedHostileTarget() ?? pawn.mindState?.enemyTarget;
            if (pursued != null && !pursued.Destroyed && pursued.Spawned && pursued.Map == map &&
                !(pursued is Pawn pp && (pp.Dead || pp.Downed)) &&
                GiantPartCombatRuntime.IsValidHostileThing(pawn, pursued))
            {
                return true;
            }
            int now = Current.Game?.tickManager?.TicksGame ?? 0;
            if (pawn.mindState != null && now > 0)
            {
                if ((pawn.mindState.lastAttackTargetTick > 0 && now - pawn.mindState.lastAttackTargetTick <= 360) ||
                    (pawn.mindState.lastHarmTick > 0 && now - pawn.mindState.lastHarmTick <= 360))
                {
                    return true;
                }
            }
            if (Mathf.Abs(now - lastCombatScanTick) >= 15)
            {
                lastCombatScanTick = now;
                cachedHasNearbyHostile = GiantPartCombatUtility.FindBestAttackTargetForSearcher(
                    pawn,
                    TargetScanFlags.NeedThreat,
                    validator: null,
                    minDist: 0f,
                    maxDist: 68f,
                    locus: IntVec3.Invalid,
                    maxTravelRadiusFromLocus: float.MaxValue)?.Thing != null;
            }
            return cachedHasNearbyHostile;
        }

        private static bool IsUnderHumanPilotOrActiveOrder(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (pawn.CurJob != null && (pawn.CurJob.ability != null || pawn.CurJob.playerForced))
            {
                return true;
            }
            if (GiantMotionOverrideRegistry.HasActiveDisplacement(pawn))
            {
                return true;
            }
            if (pawn.Faction == Faction.OfPlayerSilentFail)
            {
                if (pawn.Drafted)
                {
                    return true;
                }
                if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit &&
                    cockpit.IsPiloted &&
                    cockpit.Props.treatHostAsVehicle)
                {
                    return true;
                }
            }
            return false;
        }

        private void CheckAutonomousTakeoff(CompGiantRig rigComp)
        {
            Pawn pawn = rigComp?.Pawn;
            Map map = pawn?.Map;
            if (pawn == null || map == null)
            {
                return;
            }
            if (GiantMotionOverrideRegistry.HasActiveWeaponSuppressingSequence(pawn))
            {
                return;
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(pawn);
            if (cockpit != null && cockpit.IsDormantUnmanned)
            {
                return;
            }

            if (!GiantVehicleBehaviorGuard.IsVehiclePawn(pawn, out _, out _) &&
                (IsBiologicalTerrestrialActivityJob(pawn.CurJob) ||
                 (pawn.jobs?.curDriver != null && pawn.jobs.curDriver.asleep) ||
                 rigComp.IsBiologicalSleepCurling))
            {
                return;
            }

            Thing enemy = rigComp.ResolvePursuedHostileTarget() ?? pawn.mindState?.enemyTarget;
            enemy ??= GiantPartCombatUtility.FindBestAttackTargetForSearcher(
                pawn,
                TargetScanFlags.NeedThreat,
                validator: null,
                minDist: 0f,
                maxDist: 65f,
                locus: IntVec3.Invalid,
                maxTravelRadiusFromLocus: float.MaxValue)?.Thing;
            if (enemy != null && enemy.Spawned && enemy.Map == map)
            {
                if (Def.autoTakeoffInCombat)
                {
                    float dist = pawn.Position.DistanceTo(enemy.Position);
                    bool interceptAirborneEnemy = Def.altitudeTier == FlightAltitudeTier.HighAirborne
                        && Def.allowAirToAirMelee
                        && GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(pawn, enemy)
                        && !(rigComp.PartCombat != null && rigComp.PartCombat.HasActiveFullHullSpinClip());
                    if (dist >= Def.autoTakeoffMinDistanceCells || interceptAirborneEnemy)
                    {
                        TryCommandTakeoff(rigComp, out _);
                        return;
                    }
                }

                if (Def.autoTakeoffWhenPathBlocked && !pawn.CanReach(enemy, PathEndMode.Touch, Danger.Deadly))
                {
                    GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
                    if (mapComp != null && mapComp.CanAerialReach(pawn, rigComp, pawn.Position, enemy, PathEndMode.Touch))
                    {
                        TryCommandTakeoff(rigComp, out _);
                    }
                }
            }
        }

        private static void RelocateToSafeGroundIfNeeded(Thing entity, Pawn pawn, CompGiantRig rigComp, IntVec3 preferredOrigin)
        {
            if (entity == null || !entity.Spawned || entity.Map == null)
            {
                return;
            }
            Map map = entity.Map;
            if (SafeLandingSpotResolver.IsAnchorFootprintValidForLandingGround(map, pawn, rigComp, entity.Position))
            {
                return;
            }

            IntVec3 origin = preferredOrigin.IsValid && preferredOrigin.InBounds(map) ? preferredOrigin : entity.Position;
            IntVec3 safeCell = IntVec3.Invalid;
            if (!SafeLandingSpotResolver.TryResolveSafeLandingCell(map, pawn, rigComp, origin, 24, out safeCell) || !safeCell.IsValid)
            {
                if (!CellFinder.TryFindRandomCellNear(origin, map, 40,
                        c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && !GiantDestructionUtility.HasThickRoofAt(map, c),
                        out safeCell))
                {
                    CellFinder.TryFindRandomCellNear(origin, map, 60,
                        c => c.InBounds(map) && c.Standable(map),
                        out safeCell);
                }
            }

            if (safeCell.IsValid && safeCell != entity.Position)
            {
                entity.Position = safeCell;
                if (entity is Pawn p)
                {
                    p.Notify_Teleported(endCurrentJob: false, resetTweenedPos: true);
                }
                rigComp?.RebuildFootprintNow(notifyMap: true);
            }
        }

        private void CompleteTouchdown(CompGiantRig rigComp)
        {
            if (rigComp != null)
            {
                cachedRigComp = rigComp;
            }
            Pawn pawn = rigComp?.Pawn;
            Map map = pawn?.Map;
            IntVec3 savedPendingLandingCell = pendingLandingCell;

            // Relocate onto valid standable dry ground BEFORE flipping flightState to Grounded
            // so IsCurrentlyAirborne remains true until relocation finishes.
            if (map != null && pawn != null && pawn.Spawned)
            {
                IntVec3 fallbackOrigin = savedPendingLandingCell.IsValid ? savedPendingLandingCell : pawn.Position;
                RelocateToSafeGroundIfNeeded(pawn, pawn, rigComp, fallbackOrigin);
            }

            flightState = GiantFlightState.Grounded;
            altitude01 = 0f;
            stateElapsedTicks = 0;

            if (map != null && pawn != null && pawn.Spawned)
            {
                float vfxScale = wasForcedCrashLanding ? 1.85f : 1.25f;
                float shake = wasForcedCrashLanding ? 0.32f : 0.12f;
                GiantVfxSystem.SpawnFlightLandingVfx(pawn, pawn.DrawPos, vfxScale, shake);

                if (Def.landingImpactRadius > 0.5f && (Def.landingImpactDamage > 0 || Def.landingImpactStunTicks > 0))
                {
                    ApplyFlightTransitionShockwave(
                        pawn,
                        map,
                        pawn.Position,
                        Def.landingImpactRadius,
                        Def.landingImpactDamage,
                        0.42f,
                        Def.landingImpactStunTicks);
                }

                if (wasForcedCrashLanding && Def.forcedLandingStunTicks > 0)
                {
                    pawn.stances?.stunner?.StunFor(Def.forcedLandingStunTicks, null, addBattleLog: false, showMote: true);
                }
            }

            pendingLandingCell = IntVec3.Invalid;
            wasForcedCrashLanding = false;
            airborneSustainedTicks = 0;
            combatAirborneTicks = 0;
            landingGlideTicks = 0;
            autoLandCheckCooldown = 0;
            postLandTakeoffLockoutTicks = Mathf.Max(postLandTakeoffLockoutTicks, Def.postLandTakeoffCooldownTicks);
            autoTakeoffCheckCooldown = Mathf.Max(autoTakeoffCheckCooldown, Def.postLandTakeoffCooldownTicks);

            if (pawn?.pather != null && pawn.pather.Moving && savedPendingLandingCell.IsValid &&
                pawn.pather.Destination.IsValid && pawn.pather.Destination.Cell == savedPendingLandingCell)
            {
                pawn.pather.StopDead();
            }

            RebuildActivePathOnFlightChange(rigComp);
        }

        private static void ApplyFlightTransitionShockwave(
            Pawn caster,
            Map map,
            IntVec3 centerCell,
            float radius,
            int bluntDamage,
            float armorPen,
            int stunTicks)
        {
            if (caster == null || map == null || !centerCell.InBounds(map) || radius <= 0.2f)
            {
                return;
            }

            foreach (Thing t in GenRadial.RadialDistinctThingsAround(centerCell, map, radius, useCenter: true))
            {
                if (t == null || t.Destroyed || t == caster)
                {
                    continue;
                }
                if (t is Pawn vp && !vp.Dead && vp.Spawned && GiantPartCombatRuntime.IsValidHostileThing(caster, vp))
                {
                    CompGiantRig victimRig = GiantFastLookup.TryGetRig(vp);
                    if (victimRig != null && victimRig.IsImmuneToGroundMeleeNow)
                    {
                        continue;
                    }
                    if (bluntDamage > 0)
                    {
                        DamageInfo dinfo = new DamageInfo(DamageDefOf.Blunt, bluntDamage, armorPen, -1f, caster);
                        bool pushedCtx = false;
                        if (victimRig != null)
                        {
                            int hitBoneIdx = victimRig.Footprint.FindClosestHitBoneToCell(vp.Position);
                            if (hitBoneIdx >= 0)
                            {
                                BodyPartRecord part = victimRig.GetBodyPartForBone(hitBoneIdx);
                                PendingBoneHitContext.Set(vp, victimRig, hitBoneIdx, part, vp.DrawPos);
                                pushedCtx = true;
                            }
                        }
                        try
                        {
                            vp.TakeDamage(dinfo);
                        }
                        finally
                        {
                            if (pushedCtx)
                            {
                                PendingBoneHitContext.Clear();
                            }
                        }
                    }
                    if (!vp.Dead && vp.Spawned && victimRig == null && stunTicks > 0)
                    {
                        vp.stances?.stunner?.StunFor(stunTicks, caster, addBattleLog: false, showMote: true);
                        vp.stances?.stagger?.StaggerFor(Mathf.Max(60, stunTicks));
                    }
                }
            }
        }

        public static bool IsBiologicalTerrestrialActivityJob(Job job)
        {
            if (job?.def == null)
            {
                return false;
            }
            JobDef def = job.def;
            if (def == JobDefOf.Ingest ||
                def == JobDefOf.LayDown ||
                def == JobDefOf.Mate ||
                def == JobDefOf.LayEgg ||
                def == JobDefOf.Nuzzle)
            {
                return true;
            }
            string name = def.defName;
            if (string.Equals(name, "Ingest", StringComparison.Ordinal) ||
                string.Equals(name, "LayDown", StringComparison.Ordinal) ||
                string.Equals(name, "Mate", StringComparison.Ordinal) ||
                string.Equals(name, "LayEgg", StringComparison.Ordinal) ||
                string.Equals(name, "Nuzzle", StringComparison.Ordinal))
            {
                return true;
            }
            if ((def == JobDefOf.PredatorHunt || string.Equals(name, "PredatorHunt", StringComparison.Ordinal)) &&
                job.targetA.HasThing && job.targetA.Thing is Corpse)
            {
                return true;
            }
            // Also land when a handler/tamer orders ForceWait during feeding/taming
            if ((def == JobDefOf.Wait || string.Equals(name, "Wait", StringComparison.Ordinal)) &&
                job.targetB.HasThing && job.targetB.Thing is Pawn)
            {
                return true;
            }
            return false;
        }

        public static bool ShouldBiologicalFlyerAutoLandOutOfCombat(
            bool isVehiclePawn,
            GiantFlightMode flightMode,
            bool hasActiveHostileEnemy,
            bool isFlyingEnRouteFar,
            int airborneSustainedTicks,
            int minAirborneTicksBeforeAutoLand,
            Job currentJob)
        {
            if (isVehiclePawn || flightMode != GiantFlightMode.TakeoffAndLanding)
            {
                return false;
            }
            if (IsBiologicalTerrestrialActivityJob(currentJob))
            {
                return true;
            }
            if (hasActiveHostileEnemy || isFlyingEnRouteFar)
            {
                return false;
            }
            return airborneSustainedTicks >= Mathf.Max(minAirborneTicksBeforeAutoLand, 120);
        }

        private void CheckAutonomousLanding(CompGiantRig rigComp)
        {
            Pawn pawn = rigComp?.Pawn;
            Map map = pawn?.Map;
            if (pawn == null || map == null || !pawn.Spawned)
            {
                return;
            }
            if (GiantMotionOverrideRegistry.HasActiveWeaponSuppressingSequence(pawn))
            {
                return;
            }

            bool isVehicle = GiantVehicleBehaviorGuard.IsVehiclePawn(pawn, out _, out _);
            Thing enemy = rigComp.ResolvePursuedHostileTarget() ?? pawn.mindState?.enemyTarget;
            enemy ??= GiantPartCombatUtility.FindBestAttackTargetForSearcher(
                pawn,
                TargetScanFlags.NeedThreat,
                validator: null,
                minDist: 0f,
                maxDist: 65f,
                locus: IntVec3.Invalid,
                maxTravelRadiusFromLocus: float.MaxValue)?.Thing;

            bool hasActiveEnemy = enemy != null && enemy.Spawned && enemy.Map == map && !(enemy is Pawn ep && ep.Dead);
            bool isFlyingEnRouteFar = pawn.pather != null
                && pawn.pather.Moving
                && pawn.pather.Destination.IsValid
                && pawn.Position.DistanceToSquared(pawn.pather.Destination.Cell) > 36;

            if (ShouldBiologicalFlyerAutoLandOutOfCombat(
                    isVehicle,
                    Def.flightMode,
                    hasActiveEnemy,
                    isFlyingEnRouteFar,
                    airborneSustainedTicks,
                    Def.minAirborneTicksBeforeAutoLand,
                    pawn.CurJob))
            {
                TryCommandLanding(rigComp, out _);
                return;
            }

            if (!hasActiveEnemy || !Def.autoLandInCombat || !RequiresLandingToEngageGroundTarget(rigComp))
            {
                return;
            }

            bool enemyAirborne = enemy is Pawn tp &&
                (tp.Flying || (GiantFastLookup.TryGetRig(tp)?.IsCurrentlyAirborne ?? false));
            if (enemyAirborne)
            {
                return;
            }

            float dist = pawn.Position.DistanceTo(enemy.Position);
            bool stoppedNearTarget = !(pawn.pather?.Moving ?? false) && dist < Def.autoTakeoffMinDistanceCells - 1.5f;
            if (dist <= Def.autoLandMaxDistanceCells || stoppedNearTarget)
            {
                TryCommandLanding(rigComp, out _);
            }
        }

        public bool StepCorpseDescentTick(float dt, CompGiantRig rigComp = null)
        {
            if (rigComp != null)
            {
                cachedRigComp = rigComp;
            }
            desiredAirborne = false;
            bool wasNotGrounded = flightState != GiantFlightState.Grounded || altitude01 > 0.001f;
            if (!wasNotGrounded)
            {
                altitude01 = 0f;
                return false;
            }
            altitude01 = Mathf.MoveTowards(altitude01, 0f, Mathf.Max(0.02f, dt / 18f));
            if (altitude01 <= 0.001f)
            {
                altitude01 = 0f;
                flightState = GiantFlightState.Grounded;
                CompGiantRig rig = rigComp ?? cachedRigComp;
                Pawn p = rig?.Pawn;
                if (p != null && p.Dead && p.Corpse != null && p.Corpse.Spawned)
                {
                    RelocateToSafeGroundIfNeeded(p.Corpse, p, rig, pendingLandingCell.IsValid ? pendingLandingCell : p.Corpse.Position);
                    pendingLandingCell = IntVec3.Invalid;
                }
                return false;
            }
            flightState = GiantFlightState.Landing;
            return true;
        }

        private static bool RequiresLandingToEngageGroundTarget(CompGiantRig rigComp)
        {
            if (rigComp?.RigDef?.SortedNodes == null)
            {
                return false;
            }

            bool hasAnyMelee = false;
            BoneNodeDef[] nodes = rigComp.RigDef.SortedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef n = nodes[i];
                if (n == null || n.partAttacks == null || n.partAttacks.Count == 0 || !rigComp.IsBoneIntact(n.nodeId))
                {
                    continue;
                }
                for (int a = 0; a < n.partAttacks.Count; a++)
                {
                    GiantPartAttackDef atk = n.partAttacks[a];
                    if (atk == null)
                    {
                        continue;
                    }
                    if (atk.kind == GiantPartAttackKind.Ranged || atk.kind == GiantPartAttackKind.CarrierCatapult)
                    {
                        return false;
                    }
                    if (atk.kind == GiantPartAttackKind.Melee)
                    {
                        if (!atk.independentOverlay && atk.canStrikeGroundWhileFlying)
                        {
                            return false;
                        }
                        if (!atk.independentOverlay)
                        {
                            hasAnyMelee = true;
                        }
                    }
                }
            }
            return hasAnyMelee;
        }

        private void OnAirborneDomainChanged(CompGiantRig rigComp)
        {
            if (IsCurrentlyAirborne)
            {
                BreakGroundMeleePursuers(rigComp);
            }
            RebuildActivePathOnFlightChange(rigComp);
        }

        private static readonly System.Reflection.FieldInfo PatherPeModeField =
            HarmonyLib.AccessTools.Field(typeof(Pawn_PathFollower), "peMode");

        /// <summary>
        /// Atomically discards any active ground/airborne PawnPath when flight state crosses
        /// between Grounded and Airborne, refreshes spatial collision grids, and re-issues
        /// StartPath(savedDest, savedPeMode) under the new pathfinding modality.
        /// </summary>
        public void RebuildActivePathOnFlightChange(CompGiantRig rigComp)
        {
            PathRebuildCount++;
            if (rigComp == null)
            {
                return;
            }

            Pawn pawn = rigComp.Pawn;
            Map map = pawn?.Map;
            rigComp.RebuildFootprintNow(notifyMap: true);
            if (map != null)
            {
                GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
                if (!IsCurrentlyAirborne)
                {
                    mapComp?.ResolveImmediateTouchdownOverlap(rigComp);
                }
                mapComp?.MarkAllCustomizersDirty();
            }

            Pawn_PathFollower pather = pawn?.pather;
            if (pather == null || !pawn.Spawned || pawn.Dead)
            {
                return;
            }

            if (pather.Moving)
            {
                LocalTargetInfo savedDest = pather.Destination;
                PathEndMode savedPeMode = PatherPeModeField?.GetValue(pather) is PathEndMode pm
                    ? pm
                    : PathEndMode.OnCell;
                pather.curPath?.ReleaseToPool();
                pather.curPath = null;
                pather.ResetToCurrentPosition();
                if (savedDest.IsValid)
                {
                    pather.StartPath(savedDest, savedPeMode);
                }
            }
        }

        /// <summary>
        /// Breaks any ground pawn's active AttackMelee job targeting this giant when it lifts off,
        /// preventing melee enemies from standing beneath its shadow swinging at empty air.
        /// </summary>
        public int BreakGroundMeleePursuers(CompGiantRig rigComp)
        {
            Pawn self = rigComp?.Pawn;
            Map map = self?.Map;
            if (self == null || map?.mapPawns == null || !IsImmuneToGroundMelee)
            {
                return 0;
            }

            int interrupted = 0;
            IReadOnlyList<Pawn> allPawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < allPawns.Count; i++)
            {
                Pawn other = allPawns[i];
                if (other == null || other == self || other.Dead || !other.Spawned)
                {
                    continue;
                }

                Job curJob = other.CurJob;
                if (curJob == null ||
                    curJob.targetA.Thing != self ||
                    (curJob.def != JobDefOf.AttackMelee && curJob.def != JobDefOf.PredatorHunt && curJob.def != JobDefOf.Ignite))
                {
                    continue;
                }

                if (!GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(other, self))
                {
                    continue;
                }

                if (other.mindState != null && other.mindState.enemyTarget == self &&
                    !GiantFlightUtility.CanVanillaPawnEngageAirborneTargets(other))
                {
                    other.mindState.enemyTarget = null;
                }
                other.jobs?.EndCurrentJob(JobCondition.Incompletable, startNewJob: true);
                interrupted++;
                BrokenMeleePursuersCount++;
            }
            return interrupted;
        }

        public IEnumerable<Gizmo> GetFlightGizmos(CompGiantRig rigComp)
        {
            Pawn pawn = rigComp?.Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead)
            {
                yield break;
            }

            if (Def.flightMode != GiantFlightMode.TakeoffAndLanding)
            {
                yield break;
            }

            bool playerControlled = pawn.Faction == Faction.OfPlayerSilentFail && GiantPlayerControlUtility.CanControl(pawn);
            if (!playerControlled)
            {
                yield break;
            }

            var toggle = new Command_Toggle
            {
                defaultLabel = (desiredAirborne || IsCurrentlyAirborne)
                    ? GiantFlightUtility.SafeTranslate("GCF_Gizmo_FlightLand_Label", "Land")
                    : GiantFlightUtility.SafeTranslate("GCF_Gizmo_FlightTakeoff_Label", "Take Off"),
                defaultDesc = GiantFlightUtility.SafeTranslate(
                    "GCF_Gizmo_FlightToggle_Desc",
                    "Toggle between aerial flight (bypasses walls/water and grants ground melee immunity) and grounded locomotion."),
                icon = GiantFlightGizmoAssets.FlightToggleIcon,
                isActive = () => desiredAirborne || flightState == GiantFlightState.Airborne || flightState == GiantFlightState.TakingOff,
                toggleAction = () =>
                {
                    if (desiredAirborne || flightState == GiantFlightState.Airborne || flightState == GiantFlightState.TakingOff)
                    {
                        TryCommandLanding(rigComp, out _);
                    }
                    else if (!TryCommandTakeoff(rigComp, out string reason) && !string.IsNullOrEmpty(reason))
                    {
                        Messages.Message(reason, pawn, MessageTypeDefOf.RejectInput, historical: false);
                    }
                }
            };

            if (pawn.Downed || (Current.Game != null && (pawn.stances?.stunner?.Stunned ?? false)))
            {
                toggle.Disable(GiantFlightUtility.SafeTranslate("Incapable", "Incapable"));
            }
            else if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit && cockpit.IsDormantUnmanned)
            {
                toggle.Disable(GiantFlightUtility.SafeTranslate("GCF_CrewDisabledUnmanned", "Disabled: unit in standby"));
            }
            else if (!desiredAirborne && !IsCurrentlyAirborne && !canMaintainLift)
            {
                toggle.Disable(GiantFlightUtility.SafeTranslate("GCF_FlightFail_LiftDamaged", "Lift organs too damaged to fly"));
            }
            else if (!desiredAirborne && !IsCurrentlyAirborne && Def.blockedByThickRoof && pawn.Map != null &&
                     HasThickRoofInTakeoffFootprint(pawn.Map, rigComp, pawn.Position))
            {
                toggle.Disable(GiantFlightUtility.SafeTranslate("GCF_FlightFail_ThickRoof", "Blocked by overhead mountain roof"));
            }
            else if (!desiredAirborne && !IsCurrentlyAirborne && IsCombatTakeoffLockedOut(rigComp, out string combatCooldownReason))
            {
                toggle.Disable(combatCooldownReason);
            }

            yield return toggle;
        }

        public string GetInspectStringExtra(CompGiantRig rigComp = null)
        {
            string stateKey = flightState switch
            {
                GiantFlightState.TakingOff => "GCF_FlightState_TakingOff",
                GiantFlightState.Airborne => "GCF_FlightState_Airborne",
                GiantFlightState.Landing => "GCF_FlightState_Landing",
                _ => "GCF_FlightState_Grounded"
            };
            string stateText = GiantFlightUtility.SafeTranslate(stateKey, flightState.ToString());
            int liftPct = Mathf.RoundToInt(liftEfficiency * 100f);
            string baseLine = LanguageDatabase.activeLanguage != null && "GCF_FlightInspectLine".CanTranslate()
                ? "GCF_FlightInspectLine".Translate(stateText, $"{liftPct}%").ToString()
                : $"Flight: {stateText} (Lift {liftPct}%)";

            if (Def.flightMode == GiantFlightMode.TakeoffAndLanding && rigComp != null && IsInActiveCombat(rigComp))
            {
                if (flightState == GiantFlightState.Airborne && Def.maxCombatAirborneTicks > 0)
                {
                    float leftSec = Mathf.Max(0f, (Def.maxCombatAirborneTicks - combatAirborneTicks) / 60f);
                    string s = $"{leftSec:F1}";
                    string suffix = GiantFlightUtility.SafeTranslateFormatted(
                        "GCF_FlightInspect_CombatAirLeft",
                        $"Combat flight limit: {s}s",
                        s);
                    return baseLine + " | " + suffix;
                }
                if (flightState == GiantFlightState.Grounded && postLandTakeoffLockoutTicks > 0)
                {
                    float cdSec = Mathf.Max(0.1f, postLandTakeoffLockoutTicks / 60f);
                    string s = $"{cdSec:F1}";
                    string suffix = GiantFlightUtility.SafeTranslateFormatted(
                        "GCF_FlightInspect_CombatCooldown",
                        $"Takeoff recovery: {s}s",
                        s);
                    return baseLine + " | " + suffix;
                }
            }

            return baseLine;
        }
    }

    /// <summary>
    /// Zero-allocation static flight domain queries used across pathfinding, melee immunity,
    /// AI target acquisition, and GiantPartCombatSystem. Uses GiantFastLookup exclusively.
    /// </summary>
    public static class GiantFlightUtility
    {
        public const int AerialOpenDirectCost = 1;
        public const int AerialWallOverflyDirectCost = 2;

        public static string SafeTranslate(string key, string fallback)
        {
            if (LanguageDatabase.activeLanguage != null && !string.IsNullOrEmpty(key) && key.CanTranslate())
            {
                return key.Translate().ToString();
            }
            return fallback;
        }

        public static string SafeTranslateFormatted(string key, string fallback, NamedArgument arg0)
        {
            if (LanguageDatabase.activeLanguage != null && !string.IsNullOrEmpty(key) && key.CanTranslate())
            {
                return key.Translate(arg0).ToString();
            }
            return fallback;
        }

        public static bool IsPawnAirborneGiant(Pawn pawn, out CompGiantRig rig)
        {
            rig = GiantFastLookup.TryGetRig(pawn);
            return rig != null && rig.IsCurrentlyAirborne;
        }

        /// <summary>
        /// Pure predicate for Layer 1-4 melee immunity: returns true when <paramref name="targetThing"/>
        /// is an airborne giant that blocks melee from <paramref name="attacker"/>'s domain.
        /// </summary>
        public static bool ShouldBlockMeleeAgainstAirborneTarget(Pawn attacker, Thing targetThing)
        {
            if (!(targetThing is Pawn targetPawn) || (targetPawn.health != null && targetPawn.Dead))
            {
                return false;
            }

            CompGiantRig targetRig = GiantFastLookup.TryGetRig(targetPawn);
            if (targetRig == null || !targetRig.IsCurrentlyAirborne)
            {
                return false;
            }

            bool attackerIsHighAirborne = false;
            if (attacker != null)
            {
                CompGiantRig attackerRig = GiantFastLookup.TryGetRig(attacker);
                if (attackerRig != null)
                {
                    GiantFlightDef attackerFlightDef = attackerRig.FlightLocomotion?.Def;
                    attackerIsHighAirborne = attackerRig.IsCurrentlyAirborne
                        && attackerFlightDef != null
                        && attackerFlightDef.altitudeTier == FlightAltitudeTier.HighAirborne;
                }
                else
                {
                    attackerIsHighAirborne = attacker.Flying;
                }
            }

            GiantFlightDef fDef = targetRig.FlightLocomotion?.Def;
            return !CanAttackerReachAirborneInMelee(
                attackerIsAirborne: attackerIsHighAirborne,
                targetAllowsAirToAirMelee: fDef?.allowAirToAirMelee ?? true,
                targetImmuneToGroundMelee: fDef?.immuneToGroundMelee ?? true);
        }

        public static bool CanAttackerReachAirborneInMelee(
            bool attackerIsAirborne,
            bool targetAllowsAirToAirMelee,
            bool targetImmuneToGroundMelee = true)
        {
            if (!attackerIsAirborne)
            {
                return !targetImmuneToGroundMelee;
            }
            return targetAllowsAirToAirMelee;
        }

        public static bool CanAirborneAttackerMeleeTarget(
            CompGiantRig attackerRig,
            GiantPartAttackDef partAtk,
            Thing targetThing)
        {
            if (attackerRig == null || !attackerRig.IsCurrentlyAirborne)
            {
                return true;
            }

            GiantFlightDef attackerFlightDef = attackerRig.FlightLocomotion?.Def;
            if (attackerFlightDef != null && attackerFlightDef.altitudeTier != FlightAltitudeTier.HighAirborne)
            {
                return !ShouldBlockMeleeAgainstAirborneTarget(attackerRig.Pawn, targetThing);
            }

            bool targetIsAirborne = targetThing is Pawn tp &&
                (tp.Flying || (GiantFastLookup.TryGetRig(tp)?.IsCurrentlyAirborne ?? false));
            if (targetIsAirborne)
            {
                return attackerFlightDef?.allowAirToAirMelee ?? true;
            }

            return partAtk != null && partAtk.canStrikeGroundWhileFlying;
        }

        public static bool IsBlockedGroundMeleeDamage(Pawn instigator, Pawn targetPawn, in DamageInfo dinfo)
        {
            if (instigator == null || targetPawn == null || dinfo.Def == null)
            {
                return false;
            }
            if (dinfo.Def.isExplosive || dinfo.Def.isRanged)
            {
                return false;
            }
            if (!dinfo.Def.harmsHealth
                || dinfo.Def == DamageDefOf.Burn
                || dinfo.Def == DamageDefOf.Flame
                || dinfo.Def == DamageDefOf.EMP
                || dinfo.Def == DamageDefOf.Stun
                || dinfo.Def == DamageDefOf.Frostbite
                || dinfo.Def == DamageDefOf.Smoke
                || dinfo.Def == DamageDefOf.Extinguish)
            {
                return false;
            }
            if (dinfo.Weapon == null && dinfo.WeaponBodyPartGroup == null && !dinfo.Def.hasForcefulImpact)
            {
                return false;
            }
            if (PendingBoneHitContext.TargetPawn == targetPawn)
            {
                return false;
            }
            if (dinfo.Weapon != null && dinfo.Weapon.IsRangedWeapon)
            {
                return false;
            }
            return ShouldBlockMeleeAgainstAirborneTarget(instigator, targetPawn);
        }

        public static bool CanVanillaPawnEngageAirborneTargets(Pawn searcher)
        {
            if (searcher == null)
            {
                return true;
            }
            if (searcher.Flying)
            {
                return true;
            }
            if (GiantFastLookup.TryGetRig(searcher) is CompGiantRig rig)
            {
                if (rig.IsCurrentlyAirborne)
                {
                    return true;
                }
                if (rig.PartCombat != null)
                {
                    rig.PartCombat.GetTacticalRangeCapabilities(out bool hasRanged, out _, out _, out _, out _);
                    if (hasRanged)
                    {
                        return true;
                    }
                }
                return searcher.abilities?.abilities != null && searcher.abilities.abilities.Count > 0;
            }

            Verb primaryEqVerb = searcher.equipment?.PrimaryEq?.PrimaryVerb;
            if (primaryEqVerb != null && primaryEqVerb.verbProps != null && !primaryEqVerb.verbProps.IsMeleeAttack && primaryEqVerb.Available())
            {
                return true;
            }

            List<Verb> allVerbs = searcher.verbTracker?.AllVerbs;
            if (allVerbs != null)
            {
                for (int i = 0; i < allVerbs.Count; i++)
                {
                    Verb v = allVerbs[i];
                    if (v != null && v.verbProps != null && !v.verbProps.IsMeleeAttack && v.Available())
                    {
                        return true;
                    }
                }
            }

            if (searcher.abilities?.abilities != null)
            {
                for (int i = 0; i < searcher.abilities.abilities.Count; i++)
                {
                    Ability ab = searcher.abilities.abilities[i];
                    if (ab?.verb?.verbProps != null && ab.verb.verbProps.range > 2.5f && ab.CanCast)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool CanGiantEngageTargetInCurrentDomain(
            Pawn searcherGiant,
            CompGiantRig searcherRig,
            Thing targetThing)
        {
            if (searcherGiant == null || targetThing == null)
            {
                return false;
            }
            if (!ShouldBlockMeleeAgainstAirborneTarget(searcherGiant, targetThing))
            {
                return true;
            }
            if (searcherRig?.FlightLocomotion != null &&
                searcherRig.FlightLocomotion.Def.flightMode == GiantFlightMode.TakeoffAndLanding &&
                searcherRig.FlightLocomotion.CanMaintainLift)
            {
                return true;
            }
            return CanVanillaPawnEngageAirborneTargets(searcherGiant);
        }

        public static int ComputeAerialDirectCost(
            bool hasThickRoof,
            bool blockedByThickRoof,
            bool hasImpassableEdifice,
            bool canFlyOverWalls,
            bool hasImpassableTerrain,
            bool canFlyOverWaterAndLava)
        {
            if (blockedByThickRoof && hasThickRoof)
            {
                return GiantDestructionUtility.ImpassablePathCost;
            }
            if (hasImpassableEdifice)
            {
                return canFlyOverWalls ? AerialWallOverflyDirectCost : GiantDestructionUtility.ImpassablePathCost;
            }
            if (hasImpassableTerrain)
            {
                return canFlyOverWaterAndLava ? AerialOpenDirectCost : GiantDestructionUtility.ImpassablePathCost;
            }
            return AerialOpenDirectCost;
        }

        public static bool IsCellPassableForFlight(Map map, GiantFlightDef flightDef, IntVec3 cell)
        {
            if (map == null || flightDef == null || !flightDef.IsActive || !cell.InBounds(map))
            {
                return false;
            }

            bool thickRoof = GiantDestructionUtility.HasThickRoofAt(map, cell);
            if (flightDef.blockedByThickRoof && thickRoof)
            {
                return false;
            }

            Building edifice = cell.GetEdifice(map);
            bool impassableEdifice = edifice != null && edifice.def.passability == Traversability.Impassable;
            if (impassableEdifice && !flightDef.canFlyOverWalls)
            {
                return false;
            }

            TerrainDef terrain = map.terrainGrid?.TerrainAt(cell);
            bool impassableTerrain = terrain != null && terrain.passability == Traversability.Impassable;
            if (impassableTerrain && !flightDef.canFlyOverWaterAndLava)
            {
                return false;
            }

            return true;
        }

        public static bool IsCellPassableForFlight(Map map, CompGiantRig comp, IntVec3 cell)
        {
            GiantFlightDef def = comp?.FlightLocomotion?.Def ?? comp?.RigDef?.ActiveFlightLocomotion;
            return IsCellPassableForFlight(map, def, cell);
        }

        private static int cachedRigDefCount = -1;
        private static bool cachedAnyDefFlyWalls;
        private static bool cachedAnyDefFlyWater;

        public static bool AnyRigSupportsAerialFlight(
            Map map,
            out bool anyFlyOverWalls,
            out bool anyFlyOverWater)
        {
            anyFlyOverWalls = false;
            anyFlyOverWater = false;

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
                    GiantFlightDef fd = active[i]?.RigDef?.ActiveFlightLocomotion;
                    if (fd != null && fd.IsActive)
                    {
                        anyFlyOverWalls |= fd.canFlyOverWalls;
                        anyFlyOverWater |= fd.canFlyOverWaterAndLava;
                    }
                }
                return anyFlyOverWalls || anyFlyOverWater;
            }

            List<GiantRigDef> allRigs = DefDatabase<GiantRigDef>.AllDefsListForReading;
            if (allRigs != null)
            {
                int count = allRigs.Count;
                if (count != cachedRigDefCount)
                {
                    bool defWalls = false;
                    bool defWater = false;
                    for (int i = 0; i < count; i++)
                    {
                        GiantFlightDef fd = allRigs[i]?.ActiveFlightLocomotion;
                        if (fd != null && fd.IsActive)
                        {
                            defWalls |= fd.canFlyOverWalls;
                            defWater |= fd.canFlyOverWaterAndLava;
                        }
                    }
                    cachedAnyDefFlyWalls = defWalls;
                    cachedAnyDefFlyWater = defWater;
                    cachedRigDefCount = count;
                }
                anyFlyOverWalls |= cachedAnyDefFlyWalls;
                anyFlyOverWater |= cachedAnyDefFlyWater;
            }

            return anyFlyOverWalls || anyFlyOverWater;
        }

        public static bool IsCellPassableForAnyAerialRig(Map map, IntVec3 cell)
        {
            if (map?.info == null || !cell.InBounds(map) || GiantDestructionUtility.HasThickRoofAt(map, cell))
            {
                return false;
            }
            if (!AnyRigSupportsAerialFlight(map, out bool allowWalls, out bool allowWater))
            {
                return false;
            }

            Building edifice = cell.GetEdifice(map);
            if (edifice != null && edifice.def.passability == Traversability.Impassable)
            {
                return allowWalls;
            }

            TerrainDef terrain = map.terrainGrid?.TerrainAt(cell);
            if (terrain != null && terrain.passability == Traversability.Impassable)
            {
                return allowWater;
            }

            return true;
        }
    }
}
