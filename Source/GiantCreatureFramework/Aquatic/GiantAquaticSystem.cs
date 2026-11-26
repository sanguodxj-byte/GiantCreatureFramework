using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Unity.Collections;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public enum GiantAquaticMode
    {
        None = 0,
        Amphibious = 1,
        WaterOnly = 2
    }

    public readonly struct GiantAquaticTerrainInfo
    {
        public readonly bool isBridge;
        public readonly bool hasUnderWater;
        public readonly bool isMarsh;
        public readonly bool isShallowWater;
        public readonly bool isChestDeepWater;
        public readonly bool isDeepWater;
        public readonly bool isOcean;
        public readonly bool isRiverOrMoving;
        public readonly bool isToxic;
        public readonly bool isStandableLand;
        public readonly int vanillaPathCost;

        public bool IsAnyOpenWater => isShallowWater || isChestDeepWater || isDeepWater;
        public bool IsAnyWaterOrMarsh => IsAnyOpenWater || isMarsh;

        public GiantAquaticTerrainInfo(
            bool isBridge,
            bool hasUnderWater,
            bool isMarsh,
            bool isShallowWater,
            bool isChestDeepWater,
            bool isDeepWater,
            bool isOcean,
            bool isRiverOrMoving,
            bool isToxic,
            bool isStandableLand,
            int vanillaPathCost)
        {
            this.isBridge = isBridge;
            this.hasUnderWater = hasUnderWater;
            this.isMarsh = isMarsh;
            this.isShallowWater = isShallowWater;
            this.isChestDeepWater = isChestDeepWater;
            this.isDeepWater = isDeepWater;
            this.isOcean = isOcean;
            this.isRiverOrMoving = isRiverOrMoving;
            this.isToxic = isToxic;
            this.isStandableLand = isStandableLand;
            this.vanillaPathCost = vanillaPathCost;
        }
    }

    /// <summary>
    /// Opt-in aquatic / naval surface locomotion specification mounted on <see cref="GiantRigDef.aquaticLocomotion"/>
    /// (or <c>&lt;steering&gt;&lt;aquaticLocomotion&gt;</c>).
    /// Supports both amphibious creatures/hovercraft (<see cref="GiantAquaticMode.Amphibious"/> — can traverse water and land)
    /// and obligate marine leviathans / surface warships (<see cref="GiantAquaticMode.WaterOnly"/> — strictly bound to water).
    /// Completely inert with zero runtime overhead when <see cref="aquaticMode"/> is <see cref="GiantAquaticMode.None"/>.
    /// </summary>
    public class GiantAquaticDef
    {
        public GiantAquaticMode aquaticMode = GiantAquaticMode.None;

        // Water terrain & draft eligibility
        public bool canTraverseShallowWater = true;
        public bool canTraverseChestDeepWater = true;
        public bool canTraverseDeepWater = true;
        public bool canTraverseOceanWater = true;
        public bool canTraverseRivers = true;
        public bool canTraverseMarsh = false;
        public bool canTraverseToxicWater = true;
        public bool immuneToToxicWater = true;
        public bool requireDeepWater = false;
        public bool blockedByBridges = true;

        // Hull draft clearance (hard water-only clearance) & soft shoreline channel preference
        public int minWaterClearanceRadius = 0;
        public int shoreAvoidanceRadius = 2;
        public int shoreAvoidanceExtraCost = 45;

        // Speed multipliers & path costs
        public float waterSpeedMultiplier = 1.0f;
        public float deepWaterSpeedMultiplier = 0f;
        public float shallowWaterSpeedMultiplier = 0f;
        public float landSpeedMultiplier = 1.0f;
        public int waterDirectPathCost = 10;
        public int landExtraPathCost = 0;

        // Anatomical propulsion linkage (BoneNodeDef.isAquaticPropulsionPart, or Naval isLocomotionDrivePart fallback)
        public float damagedPropulsionMinSpeedFactor = 0.25f;
        public bool immobilizeInWaterWhenPropulsionDestroyed = false;

        // Optional water-state animation overrides (fall back cleanly to idleAnim / walkAnim / runAnim)
        public GiantAnimDef swimIdleAnim;
        public GiantAnimDef swimAnim;
        public GiantAnimDef swimFastAnim;
        public float swimCycleDistance = 0f;

        // Waterline visual offset, buoyancy wave bobbing & wake VFX
        public float waterVisualOffsetZ = 0f;
        public float buoyancyBobAmplitude = 0.04f;
        public float buoyancyBobPeriodTicks = 110f;
        public bool spawnWaterWakeVfx = true;
        public float wakeScale = 1.0f;
        public int wakeIntervalTicks = 9;
        public bool suppressGroundDustInWater = true;

        // World globe navigation rules
        public bool allowWorldWaterTravel = true;
        public bool allowCoastalWorldTiles = true;
        public bool allowRiverWorldTiles = true;
        public bool allowInlandWorldTravelWhenWaterOnly = false;
        public float worldWaterSpeedMultiplier = 0f;

        public bool IsActive => aquaticMode != GiantAquaticMode.None;
        public bool IsWaterOnly => aquaticMode == GiantAquaticMode.WaterOnly;
        public bool IsAmphibious => aquaticMode == GiantAquaticMode.Amphibious;

        public float EffectiveDeepWaterSpeedMultiplier =>
            deepWaterSpeedMultiplier > 0.01f ? deepWaterSpeedMultiplier : waterSpeedMultiplier;

        public float EffectiveShallowWaterSpeedMultiplier =>
            shallowWaterSpeedMultiplier > 0.01f ? shallowWaterSpeedMultiplier : waterSpeedMultiplier;

        public float EffectiveWorldWaterSpeedMultiplier =>
            worldWaterSpeedMultiplier > 0.01f ? worldWaterSpeedMultiplier : waterSpeedMultiplier;

        public IEnumerable<string> ConfigErrors(string rigDefName, GiantRigDef rigDef = null)
        {
            if (!IsActive)
            {
                yield break;
            }

            if (!canTraverseShallowWater && !canTraverseChestDeepWater && !canTraverseDeepWater && !canTraverseMarsh)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> has aquaticMode={aquaticMode} but disables all water tiers (shallow, chest-deep, deep, marsh).";
            }
            if (requireDeepWater && !canTraverseChestDeepWater && !canTraverseDeepWater)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> sets requireDeepWater=true while disabling both chest-deep and deep water.";
            }
            if (minWaterClearanceRadius < 0 || minWaterClearanceRadius > 6)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> minWaterClearanceRadius ({minWaterClearanceRadius}) must lie in [0, 6].";
            }
            if (shoreAvoidanceRadius < 0 || shoreAvoidanceRadius > 8)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> shoreAvoidanceRadius ({shoreAvoidanceRadius}) must lie in [0, 8].";
            }
            if (shoreAvoidanceExtraCost < 0 || shoreAvoidanceExtraCost > 4000)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> shoreAvoidanceExtraCost ({shoreAvoidanceExtraCost}) must lie in [0, 4000].";
            }
            if (waterSpeedMultiplier < 0.1f || waterSpeedMultiplier > 5.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> waterSpeedMultiplier ({waterSpeedMultiplier:F2}) must lie in [0.1, 5.0].";
            }
            if (deepWaterSpeedMultiplier < 0f || (deepWaterSpeedMultiplier > 0f && (deepWaterSpeedMultiplier < 0.1f || deepWaterSpeedMultiplier > 5.0f)))
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> deepWaterSpeedMultiplier ({deepWaterSpeedMultiplier:F2}) must be 0 or lie in [0.1, 5.0].";
            }
            if (shallowWaterSpeedMultiplier < 0f || (shallowWaterSpeedMultiplier > 0f && (shallowWaterSpeedMultiplier < 0.1f || shallowWaterSpeedMultiplier > 5.0f)))
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> shallowWaterSpeedMultiplier ({shallowWaterSpeedMultiplier:F2}) must be 0 or lie in [0.1, 5.0].";
            }
            if (landSpeedMultiplier < 0.05f || landSpeedMultiplier > 5.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> landSpeedMultiplier ({landSpeedMultiplier:F2}) must lie in [0.05, 5.0].";
            }
            if (waterDirectPathCost < 0 || waterDirectPathCost > 2000)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> waterDirectPathCost ({waterDirectPathCost}) must lie in [0, 2000].";
            }
            if (landExtraPathCost < 0 || landExtraPathCost > 4000)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> landExtraPathCost ({landExtraPathCost}) must lie in [0, 4000].";
            }
            if (damagedPropulsionMinSpeedFactor < 0f || damagedPropulsionMinSpeedFactor > 1.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> damagedPropulsionMinSpeedFactor ({damagedPropulsionMinSpeedFactor:F2}) must lie in [0.0, 1.0].";
            }
            if (swimCycleDistance < 0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> swimCycleDistance ({swimCycleDistance:F2}) must be >= 0.";
            }
            if (waterVisualOffsetZ < -2.0f || waterVisualOffsetZ > 2.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> waterVisualOffsetZ ({waterVisualOffsetZ:F2}) must lie in [-2.0, 2.0].";
            }
            if (buoyancyBobAmplitude < 0f || buoyancyBobAmplitude > 0.5f)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> buoyancyBobAmplitude ({buoyancyBobAmplitude:F2}) must lie in [0.0, 0.5].";
            }
            if (buoyancyBobPeriodTicks < 15f || buoyancyBobPeriodTicks > 1200f)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> buoyancyBobPeriodTicks ({buoyancyBobPeriodTicks:F1}) must lie in [15, 1200].";
            }
            if (wakeScale < 0.1f || wakeScale > 5.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> wakeScale ({wakeScale:F2}) must lie in [0.1, 5.0].";
            }
            if (wakeIntervalTicks < 1 || wakeIntervalTicks > 120)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> wakeIntervalTicks ({wakeIntervalTicks}) must lie in [1, 120].";
            }
            if (worldWaterSpeedMultiplier < 0f || (worldWaterSpeedMultiplier > 0f && (worldWaterSpeedMultiplier < 0.1f || worldWaterSpeedMultiplier > 5.0f)))
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> worldWaterSpeedMultiplier ({worldWaterSpeedMultiplier:F2}) must be 0 or lie in [0.1, 5.0].";
            }
            if (swimIdleAnim != null && !swimIdleAnim.loop)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> swimIdleAnim '{swimIdleAnim.defName}' must have <loop>true</loop>.";
            }
            if (swimAnim != null && !swimAnim.loop)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> swimAnim '{swimAnim.defName}' must have <loop>true</loop>.";
            }
            if (swimFastAnim != null && !swimFastAnim.loop)
            {
                yield return $"GiantRigDef '{rigDefName}' <aquaticLocomotion> swimFastAnim '{swimFastAnim.defName}' must have <loop>true</loop>.";
            }
        }
    }

    public readonly struct GiantAquaticCustomizerKey : IEquatable<GiantAquaticCustomizerKey>
    {
        public readonly GiantAquaticMode mode;
        public readonly ushort capabilityFlags;
        public readonly byte minWaterClearance;
        public readonly byte shoreAvoidanceRadius;
        public readonly ushort shoreAvoidanceExtraCost;
        public readonly ushort waterDirectPathCost;
        public readonly ushort landExtraPathCost;
        public readonly byte wallClearanceRadius;
        public readonly int factionLoadId;

        public bool AllowCrushBuildings => (capabilityFlags & (1 << 9)) != 0;
        public bool AllowCrushNaturalRock => (capabilityFlags & (1 << 10)) != 0;
        public bool AllowCrushTerrain => (capabilityFlags & (1 << 11)) != 0;
        public bool ProtectAllied => (capabilityFlags & (1 << 12)) != 0;

        public GiantAquaticCustomizerKey(
            GiantAquaticDef def,
            int wallClearanceRadius,
            int factionLoadId,
            bool allowCrushBuildings,
            bool allowCrushNaturalRock,
            bool allowCrushTerrain,
            bool protectAllied)
        {
            mode = def.aquaticMode;
            ushort flags = 0;
            if (def.canTraverseShallowWater) flags |= 1 << 0;
            if (def.canTraverseChestDeepWater) flags |= 1 << 1;
            if (def.canTraverseDeepWater) flags |= 1 << 2;
            if (def.canTraverseOceanWater) flags |= 1 << 3;
            if (def.canTraverseRivers) flags |= 1 << 4;
            if (def.canTraverseMarsh) flags |= 1 << 5;
            if (def.canTraverseToxicWater) flags |= 1 << 6;
            if (def.requireDeepWater) flags |= 1 << 7;
            if (def.blockedByBridges) flags |= 1 << 8;
            if (allowCrushBuildings) flags |= 1 << 9;
            if (allowCrushNaturalRock) flags |= 1 << 10;
            if (allowCrushTerrain) flags |= 1 << 11;
            if (protectAllied) flags |= 1 << 12;

            capabilityFlags = flags;
            minWaterClearance = (byte)Mathf.Clamp(def.minWaterClearanceRadius, 0, 6);
            this.shoreAvoidanceRadius = (byte)Mathf.Clamp(def.shoreAvoidanceRadius, 0, 8);
            this.shoreAvoidanceExtraCost = (ushort)Mathf.Clamp(def.shoreAvoidanceExtraCost, 0, 4000);
            this.waterDirectPathCost = (ushort)Mathf.Clamp(def.waterDirectPathCost, 0, 2000);
            this.landExtraPathCost = (ushort)Mathf.Clamp(def.landExtraPathCost, 0, 4000);
            this.wallClearanceRadius = (byte)Mathf.Clamp(wallClearanceRadius, 0, 6);
            this.factionLoadId = (allowCrushBuildings || allowCrushNaturalRock || allowCrushTerrain) ? factionLoadId : -1;
        }

        public bool IsTerrainEligibleWater(TerrainDef topTerrain, TerrainDef underTerrain)
        {
            return GiantAquaticUtility.IsTerrainEligibleWaterByFlags(topTerrain, underTerrain, capabilityFlags);
        }

        public bool Equals(GiantAquaticCustomizerKey other)
        {
            return mode == other.mode
                && capabilityFlags == other.capabilityFlags
                && minWaterClearance == other.minWaterClearance
                && shoreAvoidanceRadius == other.shoreAvoidanceRadius
                && shoreAvoidanceExtraCost == other.shoreAvoidanceExtraCost
                && waterDirectPathCost == other.waterDirectPathCost
                && landExtraPathCost == other.landExtraPathCost
                && wallClearanceRadius == other.wallClearanceRadius
                && factionLoadId == other.factionLoadId;
        }

        public override bool Equals(object obj) => obj is GiantAquaticCustomizerKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)mode;
                h = (h * 397) ^ capabilityFlags;
                h = (h * 397) ^ minWaterClearance;
                h = (h * 397) ^ shoreAvoidanceRadius;
                h = (h * 397) ^ shoreAvoidanceExtraCost;
                h = (h * 397) ^ waterDirectPathCost;
                h = (h * 397) ^ landExtraPathCost;
                h = (h * 397) ^ wallClearanceRadius;
                h = (h * 397) ^ factionLoadId;
                return h;
            }
        }
    }

    /// <summary>
    /// Burst-compatible path grid customizer for aquatic giants and surface vessels.
    /// Replaces <c>pathGridDirect</c> with aquatic direct costs (opening deep/ocean water and blocking dry land
    /// when <see cref="GiantAquaticMode.WaterOnly"/>) and populates <c>custom</c> offsets to hard-block ineligible
    /// cells even under <c>TraverseMode.PassAllDestroyableThings</c> while adding soft shoreline channel avoidance.
    /// </summary>
    public sealed class GiantAquaticPathCustomizer : PathRequest.IPathGridCustomizer, IDisposable
    {
        private NativeArray<ushort> nativeOffsets;
        private NativeArray<int> nativeDirectCosts;
        public readonly ushort[] ManagedOffsets;
        public readonly int[] ManagedDirectCosts;
        private bool dirty;
        private bool disposed;

        public bool IsDirty => dirty;
        public bool IsCreated => !disposed && nativeOffsets.IsCreated && nativeDirectCosts.IsCreated;
        public NativeArray<int>.ReadOnly DirectCostGrid => nativeDirectCosts.AsReadOnly();

        public GiantAquaticPathCustomizer(int numGridCells)
        {
            ManagedOffsets = new ushort[numGridCells];
            ManagedDirectCosts = new int[numGridCells];
            nativeOffsets = new NativeArray<ushort>(numGridCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            nativeDirectCosts = new NativeArray<int>(numGridCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            dirty = true;
        }

        public void MarkDirty() => dirty = true;

        public bool SyncIfDirty()
        {
            if (disposed || !dirty || !nativeOffsets.IsCreated || !nativeDirectCosts.IsCreated)
            {
                return false;
            }
            nativeOffsets.CopyFrom(ManagedOffsets);
            nativeDirectCosts.CopyFrom(ManagedDirectCosts);
            dirty = false;
            return true;
        }

        public NativeArray<ushort> GetOffsetGrid() => nativeOffsets;

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
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
    }

    public sealed class GiantAquaticRuntime
    {
        private readonly GiantRigDef rigDef;
        private readonly GiantAquaticDef def;
        private readonly int[] propulsionBoneIndices;

        private bool isCurrentlyInWater;
        private bool isInDeepWater;
        private bool isInShallowWater;
        private float propulsionEfficiency01 = 1f;
        private bool isPropulsionImmobilized;
        private float waterBobPhaseTicks;
        private int lastWakeTick = -9999;

        public GiantAquaticDef Def => def;
        public bool IsCurrentlyInWater => isCurrentlyInWater;
        public bool IsInDeepWater => isInDeepWater;
        public bool IsInShallowWater => isInShallowWater;
        public float PropulsionEfficiency01 => propulsionEfficiency01;
        public bool IsPropulsionImmobilized => isPropulsionImmobilized;
        public float WaterBobPhaseTicks => waterBobPhaseTicks;

        public GiantAquaticRuntime(GiantRigDef rigDef, GiantAquaticDef def)
        {
            this.rigDef = rigDef ?? throw new ArgumentNullException(nameof(rigDef));
            this.def = def ?? throw new ArgumentNullException(nameof(def));
            propulsionBoneIndices = ResolvePropulsionBoneIndices(rigDef);
        }

        internal static int[] ResolvePropulsionBoneIndices(GiantRigDef rig)
        {
            BoneNodeDef[] nodes = rig?.SortedNodes;
            if (nodes == null || nodes.Length == 0)
            {
                return Array.Empty<int>();
            }

            List<int> list = null;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].isAquaticPropulsionPart)
                {
                    list ??= new List<int>(4);
                    list.Add(i);
                }
            }

            if (list == null && rig.ActiveVehicleLocomotion != null && rig.ActiveVehicleLocomotion.chassisType == VehicleChassisType.Naval)
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (nodes[i].isLocomotionDrivePart)
                    {
                        list ??= new List<int>(4);
                        list.Add(i);
                    }
                }
            }

            return list != null ? list.ToArray() : Array.Empty<int>();
        }

        public void ResetForSpawn(Pawn pawn)
        {
            waterBobPhaseTicks = pawn != null ? (pawn.thingIDNumber & 255) * 3.7f : 0f;
            lastWakeTick = -9999;
            RefreshEnvironmentAndPropulsion(pawn, ReadOnlySpan<bool>.Empty, ReadOnlySpan<float>.Empty);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref isCurrentlyInWater, "gcf_aq_inWater", defaultValue: false);
            Scribe_Values.Look(ref isInDeepWater, "gcf_aq_deep", defaultValue: false);
            Scribe_Values.Look(ref isInShallowWater, "gcf_aq_shallow", defaultValue: false);
            Scribe_Values.Look(ref waterBobPhaseTicks, "gcf_aq_bobPhase", 0f);
        }

        public void EvaluatePropulsionEfficiency(ReadOnlySpan<bool> missingMask, ReadOnlySpan<float> partHealthPct)
        {
            if (propulsionBoneIndices.Length == 0 || missingMask.IsEmpty)
            {
                propulsionEfficiency01 = 1f;
                isPropulsionImmobilized = false;
                return;
            }

            float sumHealth = 0f;
            int total = propulsionBoneIndices.Length;
            int intactCount = 0;
            for (int i = 0; i < total; i++)
            {
                int idx = propulsionBoneIndices[i];
                if ((uint)idx < (uint)missingMask.Length && !missingMask[idx])
                {
                    intactCount++;
                    float hp = (uint)idx < (uint)partHealthPct.Length ? Mathf.Clamp01(partHealthPct[idx]) : 1f;
                    sumHealth += Mathf.Lerp(0.45f, 1f, hp);
                }
            }

            if (intactCount == 0)
            {
                propulsionEfficiency01 = 0f;
                isPropulsionImmobilized = def.immobilizeInWaterWhenPropulsionDestroyed;
                return;
            }

            propulsionEfficiency01 = Mathf.Clamp01(sumHealth / total);
            isPropulsionImmobilized = false;
        }

        public void RefreshEnvironmentAndPropulsion(
            Pawn pawn,
            ReadOnlySpan<bool> missingMask,
            ReadOnlySpan<float> partHealthPct)
        {
            EvaluatePropulsionEfficiency(missingMask, partHealthPct);
            if (pawn == null || !pawn.Spawned || pawn.Map == null || !pawn.Position.InBounds(pawn.Map))
            {
                if (def.IsWaterOnly)
                {
                    isCurrentlyInWater = true;
                    isInDeepWater = true;
                    isInShallowWater = false;
                }
                else
                {
                    isCurrentlyInWater = false;
                    isInDeepWater = false;
                    isInShallowWater = false;
                }
                return;
            }

            TerrainGrid tg = pawn.Map.terrainGrid;
            TerrainDef top = tg?.TerrainAt(pawn.Position);
            TerrainDef under = (top != null && top.bridge) ? tg.UnderTerrainAt(pawn.Position) : null;
            GiantAquaticTerrainInfo info = GiantAquaticUtility.ClassifyTerrain(top, under);
            bool eligibleWater = GiantAquaticUtility.IsTerrainEligibleWater(in info, def);
            isCurrentlyInWater = eligibleWater;
            isInDeepWater = eligibleWater && (info.isDeepWater || info.isChestDeepWater);
            isInShallowWater = eligibleWater && !isInDeepWater;
        }

        public void StepTick(CompGiantRig rig, float movedDistCells)
        {
            Pawn pawn = rig?.Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead)
            {
                return;
            }

            rig.GetLivePartMaskAndHealth(out ReadOnlySpan<bool> missingSpan, out ReadOnlySpan<float> healthSpan);
            RefreshEnvironmentAndPropulsion(pawn, missingSpan, healthSpan);

            if (rig.IsCurrentlyAirborne || rig.IsFlyingEntry)
            {
                return;
            }

            if (isCurrentlyInWater)
            {
                waterBobPhaseTicks = (waterBobPhaseTicks + 1f) % Mathf.Max(20f, def.buoyancyBobPeriodTicks * 4f);
                if (def.spawnWaterWakeVfx && movedDistCells > 0.004f && pawn.Map != null)
                {
                    MaybeSpawnWaterWake(pawn, rig);
                }
            }
        }

        public float ComputeEffectiveSurfaceSpeedMultiplier()
        {
            if (isCurrentlyInWater)
            {
                if (isPropulsionImmobilized)
                {
                    return 0.001f;
                }
                float baseWaterMult = isInDeepWater
                    ? def.EffectiveDeepWaterSpeedMultiplier
                    : (isInShallowWater ? def.EffectiveShallowWaterSpeedMultiplier : def.waterSpeedMultiplier);
                float propScale = propulsionBoneIndices.Length > 0
                    ? Mathf.Lerp(Mathf.Clamp01(def.damagedPropulsionMinSpeedFactor), 1f, propulsionEfficiency01)
                    : 1f;
                return Mathf.Clamp(baseWaterMult * propScale, 0.05f, 5.0f);
            }

            return def.IsWaterOnly ? 0.05f : Mathf.Clamp(def.landSpeedMultiplier, 0.05f, 5.0f);
        }

        public Vector2 ComputeWaterVisualOffsetXZ(bool pawnDeadOrDowned)
        {
            if (!isCurrentlyInWater || pawnDeadOrDowned)
            {
                return Vector2.zero;
            }
            float bobZ = 0f;
            if (def.buoyancyBobAmplitude > 1e-4f && def.buoyancyBobPeriodTicks >= 15f)
            {
                float phaseRad = (waterBobPhaseTicks / def.buoyancyBobPeriodTicks) * (Mathf.PI * 2f);
                bobZ = Mathf.Sin(phaseRad) * def.buoyancyBobAmplitude;
            }
            return new Vector2(0f, def.waterVisualOffsetZ + bobZ);
        }

        private void MaybeSpawnWaterWake(Pawn pawn, CompGiantRig rig)
        {
            int now = Current.Game?.tickManager?.TicksGame ?? 0;
            int interval = Mathf.Max(2, def.wakeIntervalTicks);
            if (now - lastWakeTick < interval)
            {
                return;
            }
            lastWakeTick = now;

            Map map = pawn.Map;
            if (map == null)
            {
                return;
            }

            Vector3 center = CompGiantRig.GetSafeDrawPos(pawn);
            float rad = rig.CurrentHeadingDeg * Mathf.Deg2Rad;
            Vector3 fwd = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);

            float extent = Mathf.Clamp((rigDef.coreClearanceRadius + 1) * 0.9f, 1.0f, 5.0f);
            float wakeScale = Mathf.Clamp(def.wakeScale, 0.2f, 4.5f);
            Vector3 sternPos = center - fwd * (extent * 1.15f);
            Vector3 bowPort = center + fwd * (extent * 0.85f) - right * (extent * 0.45f);
            Vector3 bowStarboard = center + fwd * (extent * 0.85f) + right * (extent * 0.45f);

            if (sternPos.InBounds(map))
            {
                FleckMaker.WaterSplash(sternPos, map, 1.6f * wakeScale, 2.2f);
            }
            if (bowPort.InBounds(map))
            {
                FleckMaker.WaterSplash(bowPort, map, 1.05f * wakeScale, 1.4f);
            }
            if (bowStarboard.InBounds(map))
            {
                FleckMaker.WaterSplash(bowStarboard, map, 1.05f * wakeScale, 1.4f);
            }
        }

        public string GetInspectStringExtra(CompGiantRig rig)
        {
            if (rig?.Pawn == null || rig.Pawn.Dead)
            {
                return null;
            }

            string modeStr = def.IsWaterOnly
                ? GiantFlightUtility.SafeTranslate("GCF_AquaticMode_WaterOnly", "Water-Only Vessel/Marine")
                : GiantFlightUtility.SafeTranslate("GCF_AquaticMode_Amphibious", "Amphibious");

            string stateStr;
            if (rig.IsCurrentlyAirborne)
            {
                stateStr = GiantFlightUtility.SafeTranslate("GCF_FlightState_Airborne", "Airborne");
            }
            else if (isCurrentlyInWater)
            {
                stateStr = isInDeepWater
                    ? GiantFlightUtility.SafeTranslate("GCF_AquaticState_DeepWater", "Deep Water")
                    : GiantFlightUtility.SafeTranslate("GCF_AquaticState_ShallowWater", "Shallow Water");
            }
            else
            {
                stateStr = GiantFlightUtility.SafeTranslate("GCF_AquaticState_Ashore", "Ashore");
            }

            if (propulsionBoneIndices.Length > 0)
            {
                string effPct = propulsionEfficiency01.ToStringPercent();
                if (LanguageDatabase.activeLanguage != null && "GCF_AquaticInspectLineWithPropulsion".CanTranslate())
                {
                    return string.Format("GCF_AquaticInspectLineWithPropulsion".Translate(), modeStr, stateStr, effPct);
                }
                return $"Aquatic: {modeStr} · {stateStr} (Propulsion {effPct})";
            }

            if (LanguageDatabase.activeLanguage != null && "GCF_AquaticInspectLine".CanTranslate())
            {
                return string.Format("GCF_AquaticInspectLine".Translate(), modeStr, stateStr);
            }
            return $"Aquatic: {modeStr} · {stateStr}";
        }
    }

    public static class GiantAquaticUtility
    {
        public static bool IsMarshTerrain(TerrainDef terrain)
        {
            if (terrain == null)
            {
                return false;
            }
            if (string.Equals(terrain.defName, "Marsh", StringComparison.Ordinal) || terrain.HasTag("WaterMarshy"))
            {
                return true;
            }
            return terrain.defName != null && terrain.defName.IndexOf("Marsh", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static GiantAquaticTerrainInfo ClassifyTerrain(TerrainDef topTerrain, TerrainDef underTerrain = null)
        {
            if (topTerrain == null)
            {
                return new GiantAquaticTerrainInfo(
                    isBridge: false,
                    hasUnderWater: false,
                    isMarsh: false,
                    isShallowWater: false,
                    isChestDeepWater: false,
                    isDeepWater: false,
                    isOcean: false,
                    isRiverOrMoving: false,
                    isToxic: false,
                    isStandableLand: true,
                    vanillaPathCost: 0);
            }

            string defName = topTerrain.defName ?? string.Empty;
            bool isBridge = topTerrain.bridge;
            if (isBridge)
            {
                bool hasUnderWater = underTerrain != null && underTerrain.IsWater && !IsMarshTerrain(underTerrain);
                bool underDeep = hasUnderWater && (underTerrain.passability == Traversability.Impassable
                    || (underTerrain.defName != null
                        && underTerrain.defName.IndexOf("Deep", StringComparison.OrdinalIgnoreCase) >= 0
                        && underTerrain.defName.IndexOf("ChestDeep", StringComparison.OrdinalIgnoreCase) < 0));
                bool underChest = hasUnderWater && !underDeep && underTerrain.defName != null
                    && underTerrain.defName.IndexOf("ChestDeep", StringComparison.OrdinalIgnoreCase) >= 0;
                bool underShallow = hasUnderWater && !underDeep && !underChest;
                bool underOcean = hasUnderWater && (underTerrain.IsOcean
                    || underTerrain.waterBodyType == WaterBodyType.Saltwater
                    || (underTerrain.defName != null && underTerrain.defName.IndexOf("Ocean", StringComparison.OrdinalIgnoreCase) >= 0));
                bool underRiver = hasUnderWater && (underTerrain.IsRiver
                    || (underTerrain.defName != null && underTerrain.defName.IndexOf("Moving", StringComparison.OrdinalIgnoreCase) >= 0));
                bool underToxic = hasUnderWater && (underTerrain.toxicBuildupFactor > 0f
                    || (underTerrain.defName != null && underTerrain.defName.IndexOf("Toxic", StringComparison.OrdinalIgnoreCase) >= 0));

                return new GiantAquaticTerrainInfo(
                    isBridge: true,
                    hasUnderWater: hasUnderWater,
                    isMarsh: underTerrain != null && IsMarshTerrain(underTerrain),
                    isShallowWater: underShallow,
                    isChestDeepWater: underChest,
                    isDeepWater: underDeep,
                    isOcean: underOcean,
                    isRiverOrMoving: underRiver,
                    isToxic: underToxic,
                    isStandableLand: topTerrain.passability != Traversability.Impassable,
                    vanillaPathCost: Mathf.Max(0, topTerrain.pathCost));
            }

            bool marsh = IsMarshTerrain(topTerrain);
            bool isWaterTag = topTerrain.IsWater || topTerrain.waterBodyType != WaterBodyType.None
                || defName.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0;
            bool openWater = isWaterTag && !marsh;

            bool isDeep = false;
            bool isChestDeep = false;
            bool isShallow = false;
            if (openWater)
            {
                bool hasChestToken = defName.IndexOf("ChestDeep", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasDeepToken = !hasChestToken && defName.IndexOf("Deep", StringComparison.OrdinalIgnoreCase) >= 0;
                if (topTerrain.passability == Traversability.Impassable || hasDeepToken)
                {
                    isDeep = true;
                }
                else if (hasChestToken || topTerrain.pathCost >= 100)
                {
                    isChestDeep = true;
                }
                else
                {
                    isShallow = true;
                }
            }

            bool isOcean = openWater && (topTerrain.IsOcean
                || topTerrain.waterBodyType == WaterBodyType.Saltwater
                || defName.IndexOf("Ocean", StringComparison.OrdinalIgnoreCase) >= 0);
            bool isRiver = openWater && (topTerrain.IsRiver
                || defName.IndexOf("Moving", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("River", StringComparison.OrdinalIgnoreCase) >= 0);
            bool isToxic = (openWater || marsh) && (topTerrain.toxicBuildupFactor > 0f
                || defName.IndexOf("Toxic", StringComparison.OrdinalIgnoreCase) >= 0);
            bool standableLand = !openWater && topTerrain.passability != Traversability.Impassable;

            return new GiantAquaticTerrainInfo(
                isBridge: false,
                hasUnderWater: false,
                isMarsh: marsh,
                isShallowWater: isShallow,
                isChestDeepWater: isChestDeep,
                isDeepWater: isDeep,
                isOcean: isOcean,
                isRiverOrMoving: isRiver,
                isToxic: isToxic,
                isStandableLand: standableLand,
                vanillaPathCost: topTerrain.passability == Traversability.Impassable
                    ? GiantDestructionUtility.ImpassablePathCost
                    : Mathf.Max(0, topTerrain.pathCost));
        }

        public static ushort PackCapabilityFlags(GiantAquaticDef def)
        {
            if (def == null)
            {
                return 0;
            }
            ushort flags = 0;
            if (def.canTraverseShallowWater) flags |= 1 << 0;
            if (def.canTraverseChestDeepWater) flags |= 1 << 1;
            if (def.canTraverseDeepWater) flags |= 1 << 2;
            if (def.canTraverseOceanWater) flags |= 1 << 3;
            if (def.canTraverseRivers) flags |= 1 << 4;
            if (def.canTraverseMarsh) flags |= 1 << 5;
            if (def.canTraverseToxicWater) flags |= 1 << 6;
            if (def.requireDeepWater) flags |= 1 << 7;
            if (def.blockedByBridges) flags |= 1 << 8;
            return flags;
        }

        public static bool IsTerrainEligibleWater(in GiantAquaticTerrainInfo info, GiantAquaticDef def)
        {
            if (def == null || !def.IsActive)
            {
                return false;
            }
            return IsTerrainEligibleWaterByFlags(in info, PackCapabilityFlags(def));
        }

        public static bool IsTerrainEligibleWaterByFlags(TerrainDef topTerrain, TerrainDef underTerrain, ushort flags)
        {
            GiantAquaticTerrainInfo info = ClassifyTerrain(topTerrain, underTerrain);
            return IsTerrainEligibleWaterByFlags(in info, flags);
        }

        public static bool IsTerrainEligibleWaterByFlags(in GiantAquaticTerrainInfo info, ushort flags)
        {
            bool canShallow = (flags & (1 << 0)) != 0;
            bool canChest = (flags & (1 << 1)) != 0;
            bool canDeep = (flags & (1 << 2)) != 0;
            bool canOcean = (flags & (1 << 3)) != 0;
            bool canRiver = (flags & (1 << 4)) != 0;
            bool canMarsh = (flags & (1 << 5)) != 0;
            bool canToxic = (flags & (1 << 6)) != 0;
            bool requireDeep = (flags & (1 << 7)) != 0;
            bool blockedByBridges = (flags & (1 << 8)) != 0;

            if (info.isBridge)
            {
                if (blockedByBridges)
                {
                    return false;
                }
                if (!info.hasUnderWater && !info.isMarsh)
                {
                    return false;
                }
            }

            if (info.isMarsh && !info.IsAnyOpenWater)
            {
                if (info.isToxic && !canToxic)
                {
                    return false;
                }
                return !requireDeep && canMarsh;
            }

            if (!info.IsAnyOpenWater)
            {
                return false;
            }

            if (info.isToxic && !canToxic)
            {
                return false;
            }
            if (info.isOcean && !canOcean)
            {
                return false;
            }
            if (info.isRiverOrMoving && !canRiver)
            {
                return false;
            }
            if (info.isShallowWater)
            {
                if (!canShallow || requireDeep)
                {
                    return false;
                }
            }
            if (info.isChestDeepWater && !canChest)
            {
                return false;
            }
            if (info.isDeepWater && !canDeep)
            {
                return false;
            }

            return true;
        }

        public static int EvaluateBaseTerrainCost(
            GiantAquaticMode mode,
            ushort capabilityFlags,
            ushort waterDirectPathCost,
            ushort landExtraPathCost,
            in GiantAquaticTerrainInfo info,
            out bool isEligibleWater)
        {
            isEligibleWater = IsTerrainEligibleWaterByFlags(in info, capabilityFlags);
            if (mode == GiantAquaticMode.WaterOnly)
            {
                return isEligibleWater
                    ? waterDirectPathCost
                    : GiantDestructionUtility.ImpassablePathCost;
            }

            if (mode == GiantAquaticMode.Amphibious)
            {
                if (isEligibleWater)
                {
                    return waterDirectPathCost;
                }
                if (info.isStandableLand)
                {
                    return Mathf.Min(GiantDestructionUtility.ImpassablePathCost - 1, info.vanillaPathCost + landExtraPathCost);
                }
                return GiantDestructionUtility.ImpassablePathCost;
            }

            return info.vanillaPathCost;
        }

        public static bool IsCellPassableForAquatic(Map map, IntVec3 cell, CompGiantRig comp)
        {
            if (map == null || comp?.RigDef == null || !cell.InBounds(map))
            {
                return false;
            }
            GiantAquaticDef aq = comp.RigDef.ActiveAquaticLocomotion;
            if (aq == null || !aq.IsActive)
            {
                return false;
            }
            return IsCellPassableForAquatic(map, cell, comp.Pawn, comp, aq);
        }

        public static bool IsCellPassableForAquatic(
            Map map,
            IntVec3 cell,
            Pawn pawn,
            CompGiantRig comp,
            GiantAquaticDef aq)
        {
            if (map == null || aq == null || !aq.IsActive || !cell.InBounds(map))
            {
                return false;
            }

            TerrainGrid tg = map.terrainGrid;
            TerrainDef top = tg?.TerrainAt(cell);
            TerrainDef under = (top != null && top.bridge) ? tg.UnderTerrainAt(cell) : null;
            GiantAquaticTerrainInfo info = ClassifyTerrain(top, under);

            bool eligibleWater = IsTerrainEligibleWater(in info, aq);
            if (aq.IsWaterOnly)
            {
                if (!eligibleWater)
                {
                    return false;
                }
                if (aq.minWaterClearanceRadius > 0 && !HasWaterClearanceAt(map, cell, aq))
                {
                    return false;
                }
            }
            else if (aq.IsAmphibious)
            {
                if (!eligibleWater && !info.isStandableLand)
                {
                    return false;
                }
            }

            Building edifice = cell.GetEdifice(map);
            if (edifice != null && edifice.def.passability == Traversability.Impassable)
            {
                if (comp?.Pawn == null || !GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(map, comp.Pawn, cell))
                {
                    return false;
                }
            }

            if (pawn != null)
            {
                GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
                if (mapComp != null && mapComp.IsBlockedForPawn(cell, pawn))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool HasWaterClearanceAt(Map map, IntVec3 center, GiantAquaticDef aq)
        {
            int r = Mathf.Clamp(aq.minWaterClearanceRadius, 0, 6);
            if (r <= 0)
            {
                return true;
            }

            IntVec3 size = map.Size;
            TerrainGrid tg = map.terrainGrid;
            ushort flags = PackCapabilityFlags(aq);
            for (int dz = -r; dz <= r; dz++)
            {
                int nz = center.z + dz;
                if ((uint)nz >= (uint)size.z)
                {
                    continue;
                }
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = center.x + dx;
                    if ((uint)nx >= (uint)size.x)
                    {
                        continue;
                    }
                    IntVec3 nc = new IntVec3(nx, 0, nz);
                    TerrainDef top = tg?.TerrainAt(nc);
                    TerrainDef under = (top != null && top.bridge) ? tg.UnderTerrainAt(nc) : null;
                    if (!IsTerrainEligibleWaterByFlags(top, under, flags))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static bool IsCellPassableForAnyAquaticRig(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map))
            {
                return false;
            }

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            if (mapComp == null || !mapComp.HasAnyGiants)
            {
                return false;
            }

            IReadOnlyList<CompGiantRig> giants = mapComp.ActiveGiants;
            for (int i = 0; i < giants.Count; i++)
            {
                CompGiantRig comp = giants[i];
                if (comp?.Pawn == null || comp.Pawn.Dead || comp.IsAirborneOrTakingOff)
                {
                    continue;
                }
                GiantAquaticDef aq = comp.RigDef?.ActiveAquaticLocomotion;
                if (aq != null && aq.IsActive && IsCellPassableForAquatic(map, cell, comp.Pawn, comp, aq))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool TryFindValidAquaticSpawnCell(
            Map map,
            GiantRigDef rigDef,
            IntVec3 preferredOrigin,
            out IntVec3 resultCell,
            bool preferEdgeCells = false)
        {
            resultCell = IntVec3.Invalid;
            if (map == null || rigDef == null)
            {
                return false;
            }

            GiantAquaticDef aq = rigDef.ActiveAquaticLocomotion;
            if (aq == null || !aq.IsActive)
            {
                return false;
            }

            IntVec3 origin = preferredOrigin.IsValid && preferredOrigin.InBounds(map) ? preferredOrigin : map.Center;
            TerrainGrid tg = map.terrainGrid;
            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            IntVec3 mapSize = map.Size;
            int pad = Mathf.Clamp(rigDef.coreClearanceRadius + 1, 2, 10);

            bool IsValidWaterCandidate(IntVec3 c, out bool isDeepChannel)
            {
                isDeepChannel = false;
                if (c.x < pad || c.z < pad || c.x >= mapSize.x - pad || c.z >= mapSize.z - pad)
                {
                    return false;
                }
                if (c.Fogged(map))
                {
                    return false;
                }
                TerrainDef top = tg?.TerrainAt(c);
                TerrainDef under = (top != null && top.bridge) ? tg.UnderTerrainAt(c) : null;
                GiantAquaticTerrainInfo info = ClassifyTerrain(top, under);
                if (!IsTerrainEligibleWater(in info, aq))
                {
                    return false;
                }
                if (aq.minWaterClearanceRadius > 0 && !HasWaterClearanceAt(map, c, aq))
                {
                    return false;
                }
                Building ed = c.GetEdifice(map);
                if (ed != null && ed.def.passability == Traversability.Impassable)
                {
                    return false;
                }
                if (mapComp != null && mapComp.GetCollisionOwnerAt(c) != null)
                {
                    return false;
                }
                isDeepChannel = info.isDeepWater || info.isChestDeepWater;
                return true;
            }

            if (!preferEdgeCells && IsValidWaterCandidate(origin, out _))
            {
                resultCell = origin;
                return true;
            }

            IntVec3 bestCell = IntVec3.Invalid;
            float bestScore = float.MaxValue;
            int edgeBand = Mathf.Max(pad + 6, 18);

            foreach (IntVec3 c in map.AllCells)
            {
                if (preferEdgeCells)
                {
                    int edgeDist = Mathf.Min(Mathf.Min(c.x, mapSize.x - 1 - c.x), Mathf.Min(c.z, mapSize.z - 1 - c.z));
                    if (edgeDist > edgeBand)
                    {
                        continue;
                    }
                }

                if (!IsValidWaterCandidate(c, out bool isDeepChannel))
                {
                    continue;
                }

                float distSq = (c - origin).LengthHorizontalSquared;
                float score = distSq - (isDeepChannel ? 64f : 0f);
                if (preferEdgeCells)
                {
                    int edgeDist = Mathf.Min(Mathf.Min(c.x, mapSize.x - 1 - c.x), Mathf.Min(c.z, mapSize.z - 1 - c.z));
                    score = edgeDist * 25f + distSq * 0.05f - (isDeepChannel ? 40f : 0f);
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestCell = c;
                }
            }

            if (bestCell.IsValid)
            {
                resultCell = bestCell;
                return true;
            }

            // Fallback: scan entire map if edge band had no eligible water (e.g. inland lake on coastal/river map)
            if (preferEdgeCells)
            {
                return TryFindValidAquaticSpawnCell(map, rigDef, origin, out resultCell, preferEdgeCells: false);
            }

            return false;
        }

        // -------------------- World Map Tile Eligibility --------------------

        public static bool IsWorldTilePassableForAquatic(
            GiantAquaticDef aq,
            bool isWaterCovered,
            bool isImpassableHilliness,
            bool isCoastal,
            bool hasRiver)
        {
            if (aq == null || !aq.IsActive)
            {
                return !isWaterCovered && !isImpassableHilliness;
            }

            if (aq.IsAmphibious)
            {
                if (isWaterCovered)
                {
                    return aq.allowWorldWaterTravel;
                }
                return !isImpassableHilliness;
            }

            // WaterOnly mode
            if (isWaterCovered)
            {
                return aq.allowWorldWaterTravel;
            }
            if (aq.allowInlandWorldTravelWhenWaterOnly)
            {
                return !isImpassableHilliness;
            }
            if (isCoastal && aq.allowCoastalWorldTiles)
            {
                return !isImpassableHilliness || isCoastal;
            }
            if (hasRiver && aq.allowRiverWorldTiles && !isImpassableHilliness)
            {
                return true;
            }
            return false;
        }

        public static bool IsWorldTilePassableForAquatic(PlanetTile tile, GiantAquaticDef aq)
        {
            if (!tile.Valid || Find.WorldGrid == null || Find.World == null)
            {
                return false;
            }

            Tile tileInfo = Find.WorldGrid[tile];
            if (tileInfo == null)
            {
                return false;
            }

            bool waterCovered = tileInfo.WaterCovered;
            bool impassableHills = tileInfo.hilliness == Hilliness.Impassable;
            bool isCoastal = !waterCovered && Find.World.CoastDirectionAt(tile).IsValid;
            bool hasRiver = false;
            if (!waterCovered && tileInfo is SurfaceTile st && st.Rivers != null && st.Rivers.Count > 0)
            {
                hasRiver = true;
            }

            return IsWorldTilePassableForAquatic(aq, waterCovered, impassableHills, isCoastal, hasRiver);
        }

        /// <summary>
        /// Checks whether a spherical geodesic arc between two world tiles stays on valid aquatic tiles
        /// for <see cref="GiantAquaticMode.WaterOnly"/> rigs when not flying.
        /// </summary>
        public static bool IsWorldGeodesicArcNavigableForAquatic(
            PlanetTile startTile,
            PlanetTile destTile,
            GiantAquaticDef aq)
        {
            if (aq == null || !aq.IsActive || !startTile.Valid || !destTile.Valid || Find.WorldGrid == null)
            {
                return true;
            }
            if (startTile.Layer != destTile.Layer || startTile.Layer == null)
            {
                return false;
            }
            if (!IsWorldTilePassableForAquatic(destTile, aq))
            {
                return false;
            }
            if (!aq.IsWaterOnly || aq.allowInlandWorldTravelWhenWaterOnly || startTile == destTile)
            {
                return true;
            }

            PlanetLayer layer = startTile.Layer;
            float radius = layer.Radius;
            Vector3 a = Find.WorldGrid.GetTileCenter(startTile).normalized;
            Vector3 b = Find.WorldGrid.GetTileCenter(destTile).normalized;
            float angleDeg = Vector3.Angle(a, b);
            int samples = Mathf.Clamp(Mathf.CeilToInt(angleDeg * 2.5f), 2, 200);
            PlanetTile curBest = startTile;
            var neighbors = new List<PlanetTile>(8);

            for (int i = 1; i < samples; i++)
            {
                float t = i / (float)samples;
                Vector3 sampleRel = Vector3.Slerp(a, b, t) * radius;
                curBest = AdvanceNearestTileOnLayer(layer, curBest, sampleRel, neighbors);
                if (curBest.Valid && curBest != startTile && curBest != destTile)
                {
                    if (!IsWorldTilePassableForAquatic(curBest, aq))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static PlanetTile AdvanceNearestTileOnLayer(
            PlanetLayer layer,
            PlanetTile seedTile,
            Vector3 targetRel,
            List<PlanetTile> scratchNeighbors)
        {
            PlanetTile best = seedTile;
            float bestDistSq = (Find.WorldGrid.GetTileCenter(best) - targetRel).sqrMagnitude;
            for (int iter = 0; iter < 12; iter++)
            {
                scratchNeighbors.Clear();
                layer.GetTileNeighbors(best, scratchNeighbors);
                PlanetTile nextBest = best;
                for (int i = 0; i < scratchNeighbors.Count; i++)
                {
                    PlanetTile cand = scratchNeighbors[i];
                    float dSq = (Find.WorldGrid.GetTileCenter(cand) - targetRel).sqrMagnitude;
                    if (dSq + 1e-6f < bestDistSq)
                    {
                        bestDistSq = dSq;
                        nextBest = cand;
                    }
                }
                if (nextBest == best)
                {
                    break;
                }
                best = nextBest;
            }
            return best;
        }

        /// <summary>
        /// Finds a valid water/coastal/river tile path via PlanetLayer neighbor A* when a direct great-circle chord
        /// crosses an inland peninsula or continent, simplifying the resulting tile chain into smooth sea legs.
        /// </summary>
        public static bool TryFindAquaticWorldRouteWaypoints(
            PlanetTile startTile,
            PlanetTile destTile,
            GiantAquaticDef aq,
            List<PlanetTile> outWaypoints,
            int maxSearchNodes = 12000)
        {
            outWaypoints?.Clear();
            if (!startTile.Valid || !destTile.Valid || startTile.Layer == null || startTile.Layer != destTile.Layer || Find.WorldGrid == null)
            {
                return false;
            }
            if (!IsWorldTilePassableForAquatic(destTile, aq))
            {
                return false;
            }
            if (startTile == destTile)
            {
                outWaypoints?.Add(destTile);
                return true;
            }

            if (IsWorldGeodesicArcNavigableForAquatic(startTile, destTile, aq))
            {
                outWaypoints?.Add(destTile);
                return true;
            }

            PlanetLayer layer = startTile.Layer;
            WorldGrid wg = Find.WorldGrid;
            Vector3 goalPos = wg.GetTileCenter(destTile);
            float avgTileSize = Mathf.Max(0.1f, layer.AverageTileSize);
            var cameFrom = new Dictionary<PlanetTile, PlanetTile>(512);
            var gScore = new Dictionary<PlanetTile, float>(512);
            var open = new List<(PlanetTile tile, float f)>(256);
            var neighbors = new List<PlanetTile>(8);

            cameFrom[startTile] = PlanetTile.Invalid;
            gScore[startTile] = 0f;
            open.Add((startTile, Vector3.Distance(wg.GetTileCenter(startTile), goalPos) / avgTileSize));

            int expanded = 0;
            bool reached = false;
            while (open.Count > 0 && expanded < maxSearchNodes)
            {
                int bestIdx = 0;
                float bestF = open[0].f;
                for (int i = 1; i < open.Count; i++)
                {
                    if (open[i].f < bestF)
                    {
                        bestF = open[i].f;
                        bestIdx = i;
                    }
                }
                PlanetTile cur = open[bestIdx].tile;
                open[bestIdx] = open[open.Count - 1];
                open.RemoveAt(open.Count - 1);

                if (cur == destTile)
                {
                    reached = true;
                    break;
                }

                expanded++;
                float curG = gScore[cur];
                neighbors.Clear();
                layer.GetTileNeighbors(cur, neighbors);
                for (int n = 0; n < neighbors.Count; n++)
                {
                    PlanetTile nb = neighbors[n];
                    if (!nb.Valid)
                    {
                        continue;
                    }
                    if (nb != destTile && !IsWorldTilePassableForAquatic(nb, aq))
                    {
                        continue;
                    }
                    Tile nbInfo = wg[nb];
                    float stepCost = (nbInfo != null && nbInfo.WaterCovered) ? 1.0f : 1.55f;
                    float tentativeG = curG + stepCost;
                    if (!gScore.TryGetValue(nb, out float prevG) || tentativeG < prevG - 1e-4f)
                    {
                        gScore[nb] = tentativeG;
                        cameFrom[nb] = cur;
                        float h = (Vector3.Distance(wg.GetTileCenter(nb), goalPos) / avgTileSize) * 0.9f;
                        open.Add((nb, tentativeG + h));
                    }
                }
            }

            if (!reached)
            {
                return false;
            }

            if (outWaypoints != null)
            {
                var rawChain = new List<PlanetTile>(64);
                PlanetTile trace = destTile;
                while (trace.Valid && trace != startTile)
                {
                    rawChain.Add(trace);
                    trace = cameFrom.TryGetValue(trace, out PlanetTile p) ? p : PlanetTile.Invalid;
                }
                rawChain.Reverse();

                // String-pull / line-of-sight simplify along water-safe geodesic chords so ships sail smooth sea legs
                PlanetTile anchor = startTile;
                int cursor = 0;
                while (cursor < rawChain.Count)
                {
                    int farthest = cursor;
                    for (int probe = rawChain.Count - 1; probe > cursor; probe--)
                    {
                        if (IsWorldGeodesicArcNavigableForAquatic(anchor, rawChain[probe], aq))
                        {
                            farthest = probe;
                            break;
                        }
                    }
                    anchor = rawChain[farthest];
                    outWaypoints.Add(anchor);
                    cursor = farthest + 1;
                }
            }
            return true;
        }
    }
}
