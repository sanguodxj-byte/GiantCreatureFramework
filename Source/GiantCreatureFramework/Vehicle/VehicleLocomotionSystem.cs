using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public enum VehicleChassisType : byte
    {
        None = 0,
        Tracked = 1,
        Wheeled = 2,
        Hover = 3,
        Naval = 4
    }

    public enum VehicleDriveState : byte
    {
        Parked = 0,
        PivotTurning = 1,
        ForwardDriving = 2,
        ReverseDriving = 3,
        Braking = 4
    }

    public struct ResolvedVehicleDriveBone
    {
        public int boneIndex;
        public float sideSign;
    }

    /// <summary>
    /// Opt-in ground vehicle locomotion specification mounted under &lt;steering&gt;&lt;vehicleLocomotion&gt;
    /// (or directly on &lt;vehicleLocomotion&gt; in GiantRigDef).
    /// Strictly inert when null or chassisType == VehicleChassisType.None so biological creatures,
    /// arachnid/biped mechs, wyverns, and aerial carriers execute zero vehicle code.
    /// </summary>
    public class VehicleLocomotionDef
    {
        public VehicleChassisType chassisType = VehicleChassisType.None;

        // Neutral pivot turn-in-place (Tracked) & Ackermann minimum turning radius (Wheeled)
        public bool allowPivotTurnInPlace = true;
        public float pivotTurnEnterAngleDeg = 32f;
        public float pivotTurnExitAngleDeg = 8f;
        public float pivotTurnRateDegPerTick = 1.8f;

        // Moving arc steering & cornering speed floor
        public float movingTurnRateDegPerTick = 1.1f;
        public float turnSpeedMinFactor = 0.55f;
        public float minTurnRadiusCells = 3.8f;
        public float maxSlipAngleDeg = 18f;

        // Throttle acceleration, service braking, emergency handbrake & gear shift pause
        public float accelerationCellsPerSec2 = 1.9f;
        public float brakingCellsPerSec2 = 4.6f;
        public float handbrakeCellsPerSec2 = 9.5f;
        public int gearShiftPauseTicks = 10;

        // Tactical reverse gear
        public bool allowReverse = true;
        public float reverseMinHeadingErrorDeg = 115f;
        public float reverseSpeedMultiplier = 0.40f;
        public float maxReverseDistanceCells = 8.5f;

        // Dual-layer arc-length lookahead & parked hull heading lock
        public float lookaheadMinCells = 2.0f;
        public float lookaheadMaxCells = 3.6f;
        public bool lockHullHeadingWhenParked = true;
        public bool allowHullTraverseForFixedWeapons = false;
        public bool stabilizeTurretsAgainstHullTurn = true;

        // Crew fire-on-the-move & part-level drive-bone health linkage
        public float movingFireAccuracyFactor = 0.78f;
        public bool requireHaltToFireWhenSingleCrew = false;
        public bool autoBindRollingTrackParentBones = true;
        public bool immobilizeWhenAnySideDestroyed = true;
        public float damagedDriveMinSpeedFactor = 0.35f;

        // Rigid suspension pitch impulse & biological gait suppression (defaults to 0 so hull stays strictly stationary; recoil is barrel-only via turretRecoil)
        public bool suppressBiologicalGait = true;
        public float accelSquatOffset = 0f;
        public float brakeDiveOffset = 0f;

        [Unsaved]
        public ResolvedVehicleDriveBone[] resolvedDriveBones = Array.Empty<ResolvedVehicleDriveBone>();

        [Unsaved]
        public bool hasPortDriveGroup;

        [Unsaved]
        public bool hasStarboardDriveGroup;

        [Unsaved]
        public bool hasCenterDriveGroup;

        public bool IsActive => chassisType != VehicleChassisType.None;

        public void ResolveTopology(BoneNodeDef[] sortedNodes, List<GiantRollingTrackDef> rollingTracks)
        {
            if (!IsActive || sortedNodes == null || sortedNodes.Length == 0)
            {
                resolvedDriveBones = Array.Empty<ResolvedVehicleDriveBone>();
                hasPortDriveGroup = false;
                hasStarboardDriveGroup = false;
                hasCenterDriveGroup = false;
                return;
            }

            var byBone = new Dictionary<int, float>();
            for (int i = 0; i < sortedNodes.Length; i++)
            {
                BoneNodeDef node = sortedNodes[i];
                if (node != null && (node.isLocomotionDrivePart || (chassisType == VehicleChassisType.Naval && node.isAquaticPropulsionPart)))
                {
                    byBone[i] = node.locomotionSideSign;
                }
            }

            if (autoBindRollingTrackParentBones && rollingTracks != null)
            {
                for (int t = 0; t < rollingTracks.Count; t++)
                {
                    GiantRollingTrackDef tr = rollingTracks[t];
                    if (tr == null)
                    {
                        continue;
                    }
                    int pIdx = tr.resolvedParentBoneIndex;
                    if ((uint)pIdx < (uint)sortedNodes.Length && !byBone.ContainsKey(pIdx))
                    {
                        byBone[pIdx] = tr.sideSign;
                    }
                }
            }

            if (byBone.Count == 0)
            {
                resolvedDriveBones = Array.Empty<ResolvedVehicleDriveBone>();
                hasPortDriveGroup = false;
                hasStarboardDriveGroup = false;
                hasCenterDriveGroup = false;
                return;
            }

            var list = new List<ResolvedVehicleDriveBone>(byBone.Count);
            bool port = false;
            bool starboard = false;
            bool center = false;
            foreach (KeyValuePair<int, float> kv in byBone)
            {
                float sign = kv.Value;
                if (sign < -0.05f) port = true;
                else if (sign > 0.05f) starboard = true;
                else center = true;
                list.Add(new ResolvedVehicleDriveBone
                {
                    boneIndex = kv.Key,
                    sideSign = sign
                });
            }
            list.Sort((a, b) => a.boneIndex.CompareTo(b.boneIndex));
            resolvedDriveBones = list.ToArray();
            hasPortDriveGroup = port;
            hasStarboardDriveGroup = starboard;
            hasCenterDriveGroup = center;
        }

        public IEnumerable<string> ValidateConfig(string rigDefName)
        {
            if (!IsActive)
            {
                yield break;
            }
            if (pivotTurnEnterAngleDeg < 5f || pivotTurnEnterAngleDeg > 175f)
            {
                yield return $"GiantRigDef '{rigDefName}' <vehicleLocomotion> pivotTurnEnterAngleDeg ({pivotTurnEnterAngleDeg:F1}) must lie in [5, 175].";
            }
            if (pivotTurnExitAngleDeg < 0.5f || pivotTurnExitAngleDeg >= pivotTurnEnterAngleDeg)
            {
                yield return $"GiantRigDef '{rigDefName}' <vehicleLocomotion> pivotTurnExitAngleDeg ({pivotTurnExitAngleDeg:F1}) must be in [0.5, pivotTurnEnterAngleDeg).";
            }
            if ((chassisType == VehicleChassisType.Wheeled || (chassisType == VehicleChassisType.Naval && !allowPivotTurnInPlace))
                && minTurnRadiusCells <= 0.25f)
            {
                yield return $"GiantRigDef '{rigDefName}' {chassisType} <vehicleLocomotion> minTurnRadiusCells ({minTurnRadiusCells:F2}) must be > 0.25.";
            }
            if (accelerationCellsPerSec2 <= 0.05f || brakingCellsPerSec2 <= 0.05f)
            {
                yield return $"GiantRigDef '{rigDefName}' <vehicleLocomotion> accelerationCellsPerSec2 and brakingCellsPerSec2 must be > 0.05.";
            }
            if (lookaheadMinCells <= 0.25f || lookaheadMaxCells < lookaheadMinCells)
            {
                yield return $"GiantRigDef '{rigDefName}' <vehicleLocomotion> lookahead range [{lookaheadMinCells:F2}, {lookaheadMaxCells:F2}] is invalid.";
            }
            if (reverseSpeedMultiplier <= 0.05f || reverseSpeedMultiplier > 1.0f)
            {
                yield return $"GiantRigDef '{rigDefName}' <vehicleLocomotion> reverseSpeedMultiplier ({reverseSpeedMultiplier:F2}) must lie in (0.05, 1.0].";
            }
        }
    }

    public struct VehicleKinematicsStepInput
    {
        public bool HasMoveOrder;
        public float TotalRemainingPathCells;
        public float LookaheadBearingDeg;
        public float MaxCruiseSpeedCellsPerSec;
        public bool HasStationaryHullAimRequest;
        public float StationaryHullAimDeg;
        public float DeltaTicks;
    }

    /// <summary>
    /// Per-instance deterministic runtime controller for ground vehicles.
    /// Instantiated ONLY when GiantRigDef.ActiveVehicleLocomotion != null.
    /// </summary>
    public sealed class VehicleLocomotionRuntime
    {
        private const float TicksPerSecond = 60f;
        private const float TicksPerSecondSq = 3600f;

        private readonly GiantRigDef rigDef;
        private readonly VehicleLocomotionDef def;

        private VehicleDriveState driveState = VehicleDriveState.Parked;
        private float hullHeadingDeg;
        private float lockedHullHeadingDeg;
        private bool headingInitialized;

        private float currentSignedSpeedCellsPerTick;
        private float lastStepHullDeltaDeg;
        private float lastStepSignedForwardCells;
        private float lastStepLateralDriftCells;
        private float currentAngularVelDegPerTick;

        private int gearShiftCooldownTicks;
        private bool reverseGearLatched;
        private int wheeledKTurnBackingTicks;
        private bool isStationaryPivot;

        private bool hasExplicitArrivalHeading;
        private float desiredArrivalHeadingDeg;

        private float pitchImpulseOffsetY;
        private float pitchImpulseVelY;

        private Vector2 exactVehiclePosXZ;
        private bool hasExactVehiclePos;

        private float driveMobilitySpeedFactor = 1f;
        private float driveMobilityTurnFactor = 1f;
        private bool isDriveImmobilized;

        private int lastDustPulseTick = -999;
        private int lastSteppedGameTick = -999;
        private int lastMoveSpeedCacheTick = -999;
        private float cachedMoveSpeedStat = 3.5f;

        public VehicleLocomotionDef Def => def;
        public GiantRigDef RigDef => rigDef;
        public VehicleDriveState DriveState => driveState;
        public float HullHeadingDeg => hullHeadingDeg;
        public float LockedHullHeadingDeg => lockedHullHeadingDeg;
        public float CurrentSignedSpeedCellsPerTick => currentSignedSpeedCellsPerTick;
        public float CurrentSignedSpeedCellsPerSec => currentSignedSpeedCellsPerTick * TicksPerSecond;
        public float LastStepHullDeltaDeg => lastStepHullDeltaDeg;
        public float LastStepSignedForwardCells => lastStepSignedForwardCells;
        public float LastStepLateralDriftCells => lastStepLateralDriftCells;
        public float PitchImpulseOffsetY => pitchImpulseOffsetY;
        public Vector2 ExactVehiclePosXZ => exactVehiclePosXZ;
        public bool HasExactVehiclePos => hasExactVehiclePos;
        public float DriveMobilitySpeedFactor => driveMobilitySpeedFactor;
        public float DriveMobilityTurnFactor => driveMobilityTurnFactor;
        public bool IsDriveImmobilized => isDriveImmobilized;
        public int GearShiftCooldownTicks => gearShiftCooldownTicks;
        public bool IsReversing => driveState == VehicleDriveState.ReverseDriving || (driveState == VehicleDriveState.Braking && currentSignedSpeedCellsPerTick < -1e-5f);
        public bool HasExplicitArrivalHeading => hasExplicitArrivalHeading;
        public float DesiredArrivalHeadingDeg => desiredArrivalHeadingDeg;

        public bool ShouldLockPawnRotation => def.lockHullHeadingWhenParked;

        public bool ShouldHoldPositionInPlace =>
            isDriveImmobilized
            || gearShiftCooldownTicks > 0
            || driveState == VehicleDriveState.Parked
            || driveState == VehicleDriveState.PivotTurning;

        public bool ShouldBlockPatherCellAdvance =>
            isDriveImmobilized
            || gearShiftCooldownTicks > 0
            || (driveState == VehicleDriveState.PivotTurning && !isStationaryPivot);

        public float CurrentMovingFireAccuracyMultiplier =>
            Mathf.Abs(currentSignedSpeedCellsPerTick) > (0.15f / TicksPerSecond)
                ? Mathf.Clamp(def.movingFireAccuracyFactor, 0.1f, 1.2f)
                : 1f;

        public VehicleLocomotionRuntime(GiantRigDef rigDef, VehicleLocomotionDef def)
        {
            this.rigDef = rigDef ?? throw new ArgumentNullException(nameof(rigDef));
            this.def = def ?? throw new ArgumentNullException(nameof(def));
        }

        public static Vector2 HullForwardFromDeg(float angleDeg)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        }

        public static Vector2 HullRightFromDeg(float angleDeg)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(rad), -Mathf.Sin(rad));
        }

        public static float BearingDegFromDelta(Vector2 deltaXZ)
        {
            return SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(deltaXZ.x, deltaXZ.y) * Mathf.Rad2Deg);
        }

        public void Reset(float initialHeadingDeg, Vector2 initialWorldPosXZ, bool hasValidPos = true)
        {
            float norm = SkeletalKinematics.NormalizeAngle360(initialHeadingDeg);
            hullHeadingDeg = norm;
            lockedHullHeadingDeg = norm;
            headingInitialized = true;
            driveState = VehicleDriveState.Parked;
            currentSignedSpeedCellsPerTick = 0f;
            currentAngularVelDegPerTick = 0f;
            lastStepHullDeltaDeg = 0f;
            lastStepSignedForwardCells = 0f;
            lastStepLateralDriftCells = 0f;
            gearShiftCooldownTicks = 0;
            reverseGearLatched = false;
            wheeledKTurnBackingTicks = 0;
            isStationaryPivot = false;
            pitchImpulseOffsetY = 0f;
            pitchImpulseVelY = 0f;
            exactVehiclePosXZ = initialWorldPosXZ;
            hasExactVehiclePos = hasValidPos;
            lastSteppedGameTick = -999;
        }

        public void SetDesiredArrivalHeading(float arrivalHeadingDeg)
        {
            hasExplicitArrivalHeading = true;
            desiredArrivalHeadingDeg = SkeletalKinematics.NormalizeAngle360(arrivalHeadingDeg);
        }

        public void ClearDesiredArrivalHeading()
        {
            hasExplicitArrivalHeading = false;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref driveState, "gcf_vehDriveState", VehicleDriveState.Parked);
            Scribe_Values.Look(ref hullHeadingDeg, "gcf_vehHullHeading", 0f);
            Scribe_Values.Look(ref lockedHullHeadingDeg, "gcf_vehLockedHeading", 0f);
            Scribe_Values.Look(ref headingInitialized, "gcf_vehHeadingInit", defaultValue: false);
            Scribe_Values.Look(ref currentSignedSpeedCellsPerTick, "gcf_vehSignedSpeed", 0f);
            Scribe_Values.Look(ref gearShiftCooldownTicks, "gcf_vehGearCooldown", 0);
            Scribe_Values.Look(ref reverseGearLatched, "gcf_vehRevLatched", defaultValue: false);
            Scribe_Values.Look(ref hasExplicitArrivalHeading, "gcf_vehHasArrivalHdg", defaultValue: false);
            Scribe_Values.Look(ref desiredArrivalHeadingDeg, "gcf_vehArrivalHdg", 0f);
            Scribe_Values.Look(ref pitchImpulseOffsetY, "gcf_vehPitchY", 0f);
            Scribe_Values.Look(ref exactVehiclePosXZ, "gcf_vehExactPosXZ", Vector2.zero);
            Scribe_Values.Look(ref hasExactVehiclePos, "gcf_vehHasExactPos", defaultValue: false);
        }

        public bool CanFireWhileMoving(CompGiantCockpit cockpit)
        {
            if (!def.requireHaltToFireWhenSingleCrew)
            {
                return true;
            }
            if (Mathf.Abs(currentSignedSpeedCellsPerTick) <= (0.10f / TicksPerSecond))
            {
                return true;
            }
            if (cockpit == null || !cockpit.IsPiloted)
            {
                return true;
            }
            return cockpit.CrewCount >= 2;
        }

        /// <summary>
        /// Evaluates part-level drive-track / wheel health strictly from declarative resolvedDriveBones.
        /// Completely decoupled from turret and weapon bones: severing tracks immobilizes chassis (MoveSpeed = 0)
        /// while leaving 360-deg turret rotation and firing 100% operational.
        /// </summary>
        public void EvaluateDriveMobility(ReadOnlySpan<bool> missingMask, ReadOnlySpan<float> partHealthPct)
        {
            ResolvedVehicleDriveBone[] driveBones = def.resolvedDriveBones;
            if (driveBones == null || driveBones.Length == 0)
            {
                driveMobilitySpeedFactor = 1f;
                driveMobilityTurnFactor = 1f;
                isDriveImmobilized = false;
                return;
            }

            int portIntact = 0;
            int starboardIntact = 0;
            int centerIntact = 0;
            float portHpSum = 0f;
            float starboardHpSum = 0f;
            float centerHpSum = 0f;
            int portTotal = 0;
            int starboardTotal = 0;
            int centerTotal = 0;

            for (int i = 0; i < driveBones.Length; i++)
            {
                int idx = driveBones[i].boneIndex;
                float sign = driveBones[i].sideSign;
                bool missing = idx >= 0 && idx < missingMask.Length && missingMask[idx];
                float hp = missing
                    ? 0f
                    : (idx >= 0 && idx < partHealthPct.Length ? Mathf.Clamp01(partHealthPct[idx]) : 1f);
                bool intact = !missing && hp > 0.001f;

                if (sign < -0.05f)
                {
                    portTotal++;
                    if (intact) portIntact++;
                    portHpSum += hp;
                }
                else if (sign > 0.05f)
                {
                    starboardTotal++;
                    if (intact) starboardIntact++;
                    starboardHpSum += hp;
                }
                else
                {
                    centerTotal++;
                    if (intact) centerIntact++;
                    centerHpSum += hp;
                }
            }

            bool portSevered = def.hasPortDriveGroup && portTotal > 0 && portIntact == 0;
            bool starboardSevered = def.hasStarboardDriveGroup && starboardTotal > 0 && starboardIntact == 0;
            bool centerSevered = def.hasCenterDriveGroup && centerTotal > 0 && centerIntact == 0 && !def.hasPortDriveGroup && !def.hasStarboardDriveGroup;

            if (def.immobilizeWhenAnySideDestroyed && (portSevered || starboardSevered || centerSevered))
            {
                isDriveImmobilized = true;
                driveMobilitySpeedFactor = 0f;
                driveMobilityTurnFactor = 0f;
                return;
            }

            float minSideHp = 1f;
            if (portTotal > 0) minSideHp = Mathf.Min(minSideHp, portHpSum / portTotal);
            if (starboardTotal > 0) minSideHp = Mathf.Min(minSideHp, starboardHpSum / starboardTotal);
            if (centerTotal > 0) minSideHp = Mathf.Min(minSideHp, centerHpSum / centerTotal);

            isDriveImmobilized = false;
            if (minSideHp < 0.50f)
            {
                float t = Mathf.Clamp01(minSideHp / 0.50f);
                float floor = Mathf.Clamp(def.damagedDriveMinSpeedFactor, 0.10f, 0.90f);
                driveMobilitySpeedFactor = Mathf.Lerp(floor, 1f, t);
                driveMobilityTurnFactor = Mathf.Lerp(Mathf.Max(0.25f, floor), 1f, t);
            }
            else
            {
                driveMobilitySpeedFactor = 1f;
                driveMobilityTurnFactor = 1f;
            }
        }

        public bool TryGetExactWorldPos(Pawn pawn, out Vector3 worldPos)
        {
            if (pawn == null || !hasExactVehiclePos || !pawn.Spawned || pawn.Dead)
            {
                worldPos = Vector3.zero;
                return false;
            }
            float y = Altitudes.AltitudeFor(AltitudeLayer.Pawn);
            worldPos = new Vector3(exactVehiclePosXZ.x, y, exactVehiclePosXZ.y);
            return true;
        }

        private static Vector2 ComputeRawPatherPosXZ(Pawn pawn)
        {
            if (pawn == null)
            {
                return Vector2.zero;
            }
            if (pawn.Spawned && pawn.pather != null && pawn.pather.MovingNow)
            {
                Vector3 a = pawn.Position.ToVector3Shifted();
                Vector3 b = pawn.pather.nextCell.ToVector3Shifted();
                Vector3 p = Vector3.Lerp(a, b, Mathf.Clamp01(pawn.pather.MovePercentage));
                return new Vector2(p.x, p.z);
            }
            IntVec3 c = CompGiantRig.GetEffectiveCell(pawn);
            if (c.IsValid)
            {
                Vector3 p = c.ToVector3Shifted();
                return new Vector2(p.x, p.z);
            }
            return Vector2.zero;
        }

        private static bool IsSegmentBlockedByImpassableWall(Pawn pawn, Map map, Vector2 fromXZ, Vector2 toXZ)
        {
            if (map == null || pawn == null)
            {
                return false;
            }
            Vector2 delta = toXZ - fromXZ;
            float dist = delta.magnitude;
            if (dist < 0.65f)
            {
                return false;
            }
            int steps = Mathf.Clamp(Mathf.CeilToInt(dist * 2.0f), 2, 12);
            IntVec3 startCell = new Vector3(fromXZ.x, 0f, fromXZ.y).ToIntVec3();
            for (int s = 1; s <= steps; s++)
            {
                float t = (float)s / steps;
                Vector2 pt = fromXZ + delta * t;
                IntVec3 cell = new Vector3(pt.x, 0f, pt.y).ToIntVec3();
                if (cell == startCell || !cell.InBounds(map))
                {
                    continue;
                }
                if (!cell.WalkableBy(map, pawn) &&
                    !GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(map, pawn, cell))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Samples continuous arc-length lookahead point along RimWorld's discrete PawnPath to eliminate
        /// 45/90-degree staircase fishtailing, while clamping at tight corridor wall corners.
        /// </summary>
        public bool SampleLivePathLookahead(
            Pawn pawn,
            Vector2 fromXZ,
            out Vector2 lookaheadXZ,
            out float lookaheadBearingDeg,
            out float totalRemainingPathCells)
        {
            lookaheadXZ = fromXZ;
            lookaheadBearingDeg = hullHeadingDeg;
            totalRemainingPathCells = 0f;

            Pawn_PathFollower pather = pawn?.pather;
            if (pather == null || !pather.Moving || (pawn.stances != null && pawn.stances.FullBodyBusy))
            {
                return false;
            }

            Map map = pawn.Map;
            float targetLookahead = Mathf.Clamp(
                1.8f + 0.35f * Mathf.Abs(CurrentSignedSpeedCellsPerSec),
                def.lookaheadMinCells,
                def.lookaheadMaxCells);

            Vector2 prevPt = fromXZ;
            float accumLen = 0f;
            bool foundCarrot = false;
            Vector2 rawCarrotXZ = fromXZ;
            Vector2 firstCornerXZ = fromXZ;
            bool hasFirstCorner = false;

            void ProcessWaypoint(IntVec3 cell)
            {
                if (!cell.IsValid)
                {
                    return;
                }
                Vector3 shifted = cell.ToVector3Shifted();
                Vector2 wp = new Vector2(shifted.x, shifted.z);
                Vector2 seg = wp - prevPt;
                float segLen = seg.magnitude;
                if (segLen <= 1e-4f)
                {
                    return;
                }
                if (!hasFirstCorner && segLen > 0.08f)
                {
                    firstCornerXZ = wp;
                    hasFirstCorner = true;
                }
                if (!foundCarrot && accumLen + segLen >= targetLookahead)
                {
                    float rem = targetLookahead - accumLen;
                    rawCarrotXZ = prevPt + (seg / segLen) * Mathf.Clamp(rem, 0f, segLen);
                    foundCarrot = true;
                }
                accumLen += segLen;
                prevPt = wp;
            }

            if (pather.nextCell.IsValid)
            {
                ProcessWaypoint(pather.nextCell);
            }

            PawnPath curPath = pather.curPath;
            if (curPath != null && curPath.Found && !curPath.Finished)
            {
                int left = curPath.NodesLeftCount;
                for (int i = 0; i < left; i++)
                {
                    IntVec3 node = curPath.Peek(i);
                    if (i == 0 && node == pather.nextCell)
                    {
                        continue;
                    }
                    ProcessWaypoint(node);
                }
            }
            else if (pather.Destination.IsValid)
            {
                ProcessWaypoint(pather.Destination.Cell);
            }

            if (accumLen <= 0.03f)
            {
                return false;
            }

            if (!foundCarrot)
            {
                rawCarrotXZ = prevPt;
            }

            // Corridor clearance clamp: shrink lookahead to immediate corner if straight chord clips an impassable wall
            if (hasFirstCorner && IsSegmentBlockedByImpassableWall(pawn, map, fromXZ, rawCarrotXZ))
            {
                rawCarrotXZ = firstCornerXZ;
            }

            Vector2 toCarrot = rawCarrotXZ - fromXZ;
            if (toCarrot.sqrMagnitude <= 1e-4f)
            {
                return false;
            }

            lookaheadXZ = rawCarrotXZ;
            lookaheadBearingDeg = BearingDegFromDelta(toCarrot);
            totalRemainingPathCells = accumLen;
            return true;
        }

        /// <summary>
        /// Pure arc-length polyline sampler usable in both unit tests and path previewing without a live Map.
        /// </summary>
        public bool SamplePolylineLookahead(
            ReadOnlySpan<Vector2> waypointsXZ,
            Vector2 fromXZ,
            out Vector2 lookaheadXZ,
            out float lookaheadBearingDeg,
            out float totalRemainingCells)
        {
            lookaheadXZ = fromXZ;
            lookaheadBearingDeg = hullHeadingDeg;
            totalRemainingCells = 0f;
            if (waypointsXZ.Length == 0)
            {
                return false;
            }

            float targetLookahead = Mathf.Clamp(
                1.8f + 0.35f * Mathf.Abs(CurrentSignedSpeedCellsPerSec),
                def.lookaheadMinCells,
                def.lookaheadMaxCells);

            Vector2 prevPt = fromXZ;
            float accum = 0f;
            bool found = false;
            Vector2 carrot = waypointsXZ[waypointsXZ.Length - 1];

            for (int i = 0; i < waypointsXZ.Length; i++)
            {
                Vector2 wp = waypointsXZ[i];
                Vector2 seg = wp - prevPt;
                float segLen = seg.magnitude;
                if (segLen <= 1e-5f)
                {
                    continue;
                }
                if (!found && accum + segLen >= targetLookahead)
                {
                    carrot = prevPt + (seg / segLen) * (targetLookahead - accum);
                    found = true;
                }
                accum += segLen;
                prevPt = wp;
            }

            if (accum <= 0.02f)
            {
                return false;
            }
            if (!found)
            {
                carrot = prevPt;
            }

            Vector2 d = carrot - fromXZ;
            if (d.sqrMagnitude <= 1e-5f)
            {
                return false;
            }
            lookaheadXZ = carrot;
            lookaheadBearingDeg = BearingDegFromDelta(d);
            totalRemainingCells = accum;
            return true;
        }

        /// <summary>
        /// Core deterministic 5-state ground vehicle kinematics solver shared 1:1 between live RimWorld ticks and headless tests.
        /// </summary>
        public void StepKinematicsCore(in VehicleKinematicsStepInput input)
        {
            float dt = Mathf.Max(0f, input.DeltaTicks);
            if (!headingInitialized)
            {
                hullHeadingDeg = SkeletalKinematics.NormalizeAngle360(hullHeadingDeg);
                lockedHullHeadingDeg = hullHeadingDeg;
                headingInitialized = true;
            }
            if (dt <= 0f)
            {
                lastStepHullDeltaDeg = 0f;
                return;
            }

            float prevHeading = hullHeadingDeg;
            float prevSpeed = currentSignedSpeedCellsPerTick;

            float maxSpeedPerTick = isDriveImmobilized
                ? 0f
                : Mathf.Max(0f, (input.MaxCruiseSpeedCellsPerSec * driveMobilitySpeedFactor) / TicksPerSecond);

            float accelPerTick2 = Mathf.Max(0.05f, def.accelerationCellsPerSec2) / TicksPerSecondSq;
            float brakePerTick2 = Mathf.Max(0.10f, def.brakingCellsPerSec2) / TicksPerSecondSq;
            float handbrakePerTick2 = Mathf.Max(brakePerTick2, def.handbrakeCellsPerSec2 / TicksPerSecondSq);

            if (isDriveImmobilized || maxSpeedPerTick <= 1e-6f)
            {
                currentSignedSpeedCellsPerTick = Mathf.MoveTowards(currentSignedSpeedCellsPerTick, 0f, handbrakePerTick2 * dt);
                currentAngularVelDegPerTick = 0f;
                isStationaryPivot = false;
                driveState = Mathf.Abs(currentSignedSpeedCellsPerTick) > 1e-5f ? VehicleDriveState.Braking : VehicleDriveState.Parked;
                hullHeadingDeg = lockedHullHeadingDeg;
                lastStepHullDeltaDeg = 0f;
                StepSuspensionPitch(currentSignedSpeedCellsPerTick - prevSpeed, dt);
                return;
            }

            bool hasMove = input.HasMoveOrder && input.TotalRemainingPathCells > 0.04f;
            if (!hasMove)
            {
                reverseGearLatched = false;
                wheeledKTurnBackingTicks = 0;

                if (Mathf.Abs(currentSignedSpeedCellsPerTick) > 1e-5f)
                {
                    driveState = VehicleDriveState.Braking;
                    isStationaryPivot = false;
                    currentSignedSpeedCellsPerTick = Mathf.MoveTowards(currentSignedSpeedCellsPerTick, 0f, brakePerTick2 * dt);
                    currentAngularVelDegPerTick = Mathf.MoveTowards(currentAngularVelDegPerTick, 0f, 0.35f * dt);
                    lockedHullHeadingDeg = hullHeadingDeg;
                }
                else
                {
                    currentSignedSpeedCellsPerTick = 0f;
                    bool wantStationaryPivot = false;
                    float targetStationaryDeg = lockedHullHeadingDeg;

                    if (hasExplicitArrivalHeading &&
                        Mathf.Abs(Mathf.DeltaAngle(hullHeadingDeg, desiredArrivalHeadingDeg)) > 1.2f)
                    {
                        wantStationaryPivot = def.chassisType != VehicleChassisType.Wheeled && def.allowPivotTurnInPlace;
                        targetStationaryDeg = desiredArrivalHeadingDeg;
                    }
                    else
                    {
                        hasExplicitArrivalHeading = false;
                        if (input.HasStationaryHullAimRequest &&
                            def.chassisType != VehicleChassisType.Wheeled &&
                            def.allowPivotTurnInPlace &&
                            Mathf.Abs(Mathf.DeltaAngle(hullHeadingDeg, input.StationaryHullAimDeg)) > 2.0f)
                        {
                            wantStationaryPivot = true;
                            targetStationaryDeg = input.StationaryHullAimDeg;
                        }
                    }

                    if (wantStationaryPivot)
                    {
                        driveState = VehicleDriveState.PivotTurning;
                        isStationaryPivot = true;
                        StepHullTurnToward(targetStationaryDeg, def.pivotTurnRateDegPerTick * driveMobilityTurnFactor, dt);
                        lockedHullHeadingDeg = hullHeadingDeg;
                    }
                    else
                    {
                        driveState = VehicleDriveState.Parked;
                        isStationaryPivot = false;
                        currentAngularVelDegPerTick = 0f;
                        if (def.lockHullHeadingWhenParked)
                        {
                            hullHeadingDeg = lockedHullHeadingDeg;
                        }
                    }
                }

                lastStepHullDeltaDeg = Mathf.DeltaAngle(prevHeading, hullHeadingDeg);
                StepSuspensionPitch(currentSignedSpeedCellsPerTick - prevSpeed, dt);
                return;
            }

            isStationaryPivot = false;

            // Active movement order
            float fwdErr = Mathf.DeltaAngle(hullHeadingDeg, input.LookaheadBearingDeg);
            float revErr = Mathf.DeltaAngle(hullHeadingDeg + 180f, input.LookaheadBearingDeg);
            float absFwdErr = Mathf.Abs(fwdErr);
            float absRevErr = Mathf.Abs(revErr);

            // Evaluate Reverse Gear (with hysteresis latch so vehicle never flip-flops mid-backing)
            if (def.allowReverse)
            {
                if (reverseGearLatched)
                {
                    if (input.TotalRemainingPathCells > def.maxReverseDistanceCells + 1.75f || absRevErr > 88f)
                    {
                        reverseGearLatched = false;
                    }
                }
                else if (input.TotalRemainingPathCells <= def.maxReverseDistanceCells &&
                         absFwdErr >= def.reverseMinHeadingErrorDeg)
                {
                    reverseGearLatched = true;
                }
            }
            else
            {
                reverseGearLatched = false;
            }

            // Wheeled Ackermann / rudder-only Naval deadzone K-turn backing when target sits inside minimum turning circle
            bool usesArcTurnConstraint = def.chassisType == VehicleChassisType.Wheeled
                || (def.chassisType == VehicleChassisType.Naval && !def.allowPivotTurnInPlace);
            if (usesArcTurnConstraint && !reverseGearLatched && def.allowReverse)
            {
                if (wheeledKTurnBackingTicks > 0)
                {
                    wheeledKTurnBackingTicks--;
                    if (absFwdErr <= 38f)
                    {
                        wheeledKTurnBackingTicks = 0;
                    }
                }
                else
                {
                    float minChord = 2f * Mathf.Max(0.5f, def.minTurnRadiusCells) * Mathf.Sin(Mathf.Min(absFwdErr, 90f) * Mathf.Deg2Rad);
                    if (absFwdErr > 58f && input.TotalRemainingPathCells < minChord * 0.92f)
                    {
                        wheeledKTurnBackingTicks = 26;
                    }
                }
            }
            else
            {
                wheeledKTurnBackingTicks = 0;
            }

            bool useReverse = reverseGearLatched || wheeledKTurnBackingTicks > 0;
            float desiredGearSign = useReverse ? -1f : 1f;

            // Gear shift through zero with transmission pause
            if (currentSignedSpeedCellsPerTick * desiredGearSign < -1e-5f)
            {
                driveState = VehicleDriveState.Braking;
                currentSignedSpeedCellsPerTick = Mathf.MoveTowards(currentSignedSpeedCellsPerTick, 0f, handbrakePerTick2 * dt);
                if (Mathf.Abs(currentSignedSpeedCellsPerTick) <= 1e-5f)
                {
                    currentSignedSpeedCellsPerTick = 0f;
                    gearShiftCooldownTicks = Mathf.Max(0, def.gearShiftPauseTicks);
                }
                lockedHullHeadingDeg = hullHeadingDeg;
                lastStepHullDeltaDeg = Mathf.DeltaAngle(prevHeading, hullHeadingDeg);
                StepSuspensionPitch(currentSignedSpeedCellsPerTick - prevSpeed, dt);
                return;
            }

            if (gearShiftCooldownTicks > 0)
            {
                gearShiftCooldownTicks = Mathf.Max(0, gearShiftCooldownTicks - Mathf.RoundToInt(dt));
                currentSignedSpeedCellsPerTick = 0f;
                currentAngularVelDegPerTick = 0f;
                driveState = VehicleDriveState.Braking;
                lockedHullHeadingDeg = hullHeadingDeg;
                lastStepHullDeltaDeg = 0f;
                StepSuspensionPitch(-prevSpeed, dt);
                return;
            }

            // Neutral Pivot Turn gate for Tracked (and Hover when stationary/sharp turn)
            bool supportsPivot = def.allowPivotTurnInPlace && def.chassisType != VehicleChassisType.Wheeled;
            if (supportsPivot && !useReverse)
            {
                bool enterPivot = absFwdErr > def.pivotTurnEnterAngleDeg;
                bool keepPivot = driveState == VehicleDriveState.PivotTurning && absFwdErr > def.pivotTurnExitAngleDeg;

                if (enterPivot || keepPivot)
                {
                    if (Mathf.Abs(currentSignedSpeedCellsPerTick) > (0.02f / TicksPerSecond))
                    {
                        driveState = VehicleDriveState.Braking;
                        currentSignedSpeedCellsPerTick = Mathf.MoveTowards(currentSignedSpeedCellsPerTick, 0f, handbrakePerTick2 * dt);
                        StepHullTurnToward(input.LookaheadBearingDeg, def.movingTurnRateDegPerTick * 0.6f * driveMobilityTurnFactor, dt);
                    }
                    else
                    {
                        currentSignedSpeedCellsPerTick = 0f;
                        driveState = VehicleDriveState.PivotTurning;
                        StepHullTurnToward(input.LookaheadBearingDeg, def.pivotTurnRateDegPerTick * driveMobilityTurnFactor, dt);
                    }
                    lockedHullHeadingDeg = hullHeadingDeg;
                    lastStepHullDeltaDeg = Mathf.DeltaAngle(prevHeading, hullHeadingDeg);
                    StepSuspensionPitch(currentSignedSpeedCellsPerTick - prevSpeed, dt);
                    return;
                }
            }

            // ForwardDriving / ReverseDriving / Arrival Braking
            float absSpeed = Mathf.Abs(currentSignedSpeedCellsPerTick);
            float stoppingDist = (absSpeed * absSpeed) / (2f * brakePerTick2) + 0.32f;
            float steerErr = wheeledKTurnBackingTicks > 0
                ? -Mathf.Sign(Mathf.Abs(fwdErr) > 0.1f ? fwdErr : 1f) * 45f
                : (useReverse ? revErr : fwdErr);

            float targetAbsSpeed;
            if (input.TotalRemainingPathCells <= stoppingDist && wheeledKTurnBackingTicks == 0)
            {
                driveState = VehicleDriveState.Braking;
                float safeRem = Mathf.Max(0f, input.TotalRemainingPathCells - 0.04f);
                targetAbsSpeed = Mathf.Min(maxSpeedPerTick, Mathf.Sqrt(2f * brakePerTick2 * safeRem));
                if (useReverse)
                {
                    targetAbsSpeed = Mathf.Min(targetAbsSpeed, maxSpeedPerTick * def.reverseSpeedMultiplier);
                }
            }
            else
            {
                driveState = useReverse ? VehicleDriveState.ReverseDriving : VehicleDriveState.ForwardDriving;
                float turnRatio = Mathf.Clamp01(Mathf.Abs(steerErr) / Mathf.Max(10f, def.pivotTurnEnterAngleDeg));
                float turnSpeedFactor = Mathf.Lerp(1f, Mathf.Clamp(def.turnSpeedMinFactor, 0.2f, 1f), turnRatio);
                float gearMult = useReverse ? def.reverseSpeedMultiplier : 1f;
                targetAbsSpeed = maxSpeedPerTick * gearMult * turnSpeedFactor;
            }

            float desiredSignedSpeed = desiredGearSign * targetAbsSpeed;
            float rate = Mathf.Abs(desiredSignedSpeed) < absSpeed ? brakePerTick2 : accelPerTick2;
            currentSignedSpeedCellsPerTick = Mathf.MoveTowards(currentSignedSpeedCellsPerTick, desiredSignedSpeed, rate * dt);

            // Non-holonomic angular velocity update per chassis type
            float updatedAbsSpeed = Mathf.Abs(currentSignedSpeedCellsPerTick);
            float maxTurnDegPerTick;
            if (usesArcTurnConstraint)
            {
                // Ackermann / hydrodynamic rudder kinematic constraint: zero turn rate at zero linear speed; omega <= |v| / R_min
                if (updatedAbsSpeed <= 1e-6f)
                {
                    maxTurnDegPerTick = 0f;
                }
                else
                {
                    float ackermannMax = (updatedAbsSpeed / Mathf.Max(0.25f, def.minTurnRadiusCells)) * Mathf.Rad2Deg;
                    maxTurnDegPerTick = Mathf.Min(def.movingTurnRateDegPerTick, ackermannMax) * driveMobilityTurnFactor;
                }
            }
            else
            {
                maxTurnDegPerTick = def.movingTurnRateDegPerTick * driveMobilityTurnFactor;
            }

            if (maxTurnDegPerTick > 1e-6f)
            {
                float targetHeading = SkeletalKinematics.NormalizeAngle360(hullHeadingDeg + steerErr);
                StepHullTurnToward(targetHeading, maxTurnDegPerTick, dt);
            }
            else
            {
                currentAngularVelDegPerTick = 0f;
            }

            lockedHullHeadingDeg = hullHeadingDeg;
            lastStepHullDeltaDeg = Mathf.DeltaAngle(prevHeading, hullHeadingDeg);
            StepSuspensionPitch(currentSignedSpeedCellsPerTick - prevSpeed, dt);
        }

        private void StepHullTurnToward(float targetHeadingDeg, float maxRateDegPerTick, float dt)
        {
            float err = Mathf.DeltaAngle(hullHeadingDeg, targetHeadingDeg);
            float absErr = Mathf.Abs(err);
            if (absErr <= 1e-4f || maxRateDegPerTick <= 1e-6f)
            {
                currentAngularVelDegPerTick = 0f;
                return;
            }

            // 6-tick angular ramp-in & arrival softening so heavy hulls start/stop turning with steel inertia
            float decelSoftScale = Mathf.Clamp01(absErr / Mathf.Max(1.5f, maxRateDegPerTick * 5f));
            float desiredOmega = Mathf.Sign(err) * maxRateDegPerTick * Mathf.Max(0.18f, decelSoftScale);
            float maxAlpha = Mathf.Max(0.08f, maxRateDegPerTick / 6f);
            currentAngularVelDegPerTick = Mathf.MoveTowards(currentAngularVelDegPerTick, desiredOmega, maxAlpha * dt);

            float step = currentAngularVelDegPerTick * dt;
            if (Mathf.Abs(step) >= absErr)
            {
                hullHeadingDeg = SkeletalKinematics.NormalizeAngle360(targetHeadingDeg);
                currentAngularVelDegPerTick = 0f;
            }
            else
            {
                hullHeadingDeg = SkeletalKinematics.NormalizeAngle360(hullHeadingDeg + step);
            }
        }

        private void StepSuspensionPitch(float deltaSpeedPerTick, float dt)
        {
            if (def.accelSquatOffset <= 0f && def.brakeDiveOffset <= 0f)
            {
                pitchImpulseOffsetY = 0f;
                pitchImpulseVelY = 0f;
                return;
            }

            float refAccel = Mathf.Max(0.2f, def.accelerationCellsPerSec2) / TicksPerSecondSq;
            float refBrake = Mathf.Max(0.4f, def.brakingCellsPerSec2) / TicksPerSecondSq;
            float targetOffset = 0f;

            // Accelerating away from rest -> rear squat (-Y); braking toward rest -> nose dive (+Y)
            float speedMag = Mathf.Abs(currentSignedSpeedCellsPerTick);
            float prevMag = Mathf.Abs(currentSignedSpeedCellsPerTick - deltaSpeedPerTick);
            float dMag = speedMag - prevMag;
            float gearSign = currentSignedSpeedCellsPerTick >= -1e-5f ? 1f : -1f;

            if (dMag > 1e-6f)
            {
                float ratio = Mathf.Clamp01(dMag / refAccel);
                targetOffset = -def.accelSquatOffset * ratio * gearSign;
            }
            else if (dMag < -1e-6f)
            {
                float ratio = Mathf.Clamp01(-dMag / refBrake);
                targetOffset = def.brakeDiveOffset * ratio * gearSign;
            }

            const float stiffness = 0.22f;
            const float damping = 0.62f;
            float force = (targetOffset - pitchImpulseOffsetY) * stiffness;
            pitchImpulseVelY = (pitchImpulseVelY + force * dt) * Mathf.Pow(damping, dt);
            pitchImpulseOffsetY = Mathf.Clamp(pitchImpulseOffsetY + pitchImpulseVelY * dt, -0.08f, 0.08f);
            if (Mathf.Abs(pitchImpulseOffsetY) < 1e-4f && Mathf.Abs(targetOffset) < 1e-4f)
            {
                pitchImpulseOffsetY = 0f;
                pitchImpulseVelY = 0f;
            }
        }

        /// <summary>
        /// Steps headless simulation along an arbitrary waypoint polyline, advancing ExactVehiclePosXZ strictly
        /// along HullForward so lateral crab-sliding is provably 0.000000 cells.
        /// </summary>
        public void StepHeadlessAlongPath(
            ReadOnlySpan<Vector2> waypointsXZ,
            float maxCruiseSpeedCellsPerSec,
            ReadOnlySpan<bool> missingMask,
            ReadOnlySpan<float> partHealthPct,
            bool hasStationaryAimRequest = false,
            float stationaryAimDeg = 0f,
            float deltaTicks = 1f)
        {
            EvaluateDriveMobility(missingMask, partHealthPct);
            bool hasMove = SamplePolylineLookahead(
                waypointsXZ,
                exactVehiclePosXZ,
                out _,
                out float lookBearing,
                out float totalRem);

            var stepInput = new VehicleKinematicsStepInput
            {
                HasMoveOrder = hasMove,
                TotalRemainingPathCells = totalRem,
                LookaheadBearingDeg = lookBearing,
                MaxCruiseSpeedCellsPerSec = maxCruiseSpeedCellsPerSec,
                HasStationaryHullAimRequest = hasStationaryAimRequest,
                StationaryHullAimDeg = stationaryAimDeg,
                DeltaTicks = deltaTicks
            };
            StepKinematicsCore(in stepInput);

            Vector2 prevPos = exactVehiclePosXZ;
            if (!ShouldHoldPositionInPlace && Mathf.Abs(currentSignedSpeedCellsPerTick) > 1e-7f)
            {
                Vector2 fwd = HullForwardFromDeg(hullHeadingDeg);
                exactVehiclePosXZ += fwd * (currentSignedSpeedCellsPerTick * deltaTicks);
            }
            hasExactVehiclePos = true;

            Vector2 disp = exactVehiclePosXZ - prevPos;
            Vector2 hullFwd = HullForwardFromDeg(hullHeadingDeg);
            Vector2 hullRight = HullRightFromDeg(hullHeadingDeg);
            lastStepSignedForwardCells = Vector2.Dot(disp, hullFwd);
            lastStepLateralDriftCells = Vector2.Dot(disp, hullRight);
        }

        /// <summary>
        /// Evaluates vehicle state right before Pawn_PathFollower.PatherTick so PivotTurning / GearShift / TrackSever
        /// freeze cell advancement on the very first tick of a turn, and returns the continuous throttle speed ratio [0..1].
        /// </summary>
        public float PrePatherTickAndComputeSpeedScale(
            Pawn_PathFollower pather,
            Pawn pawn,
            CompGiantRig rig)
        {
            if (pawn == null || pather == null || rig == null || !pawn.Spawned || pawn.Dead || pawn.Downed)
            {
                return 1f;
            }

            int nowTick = Current.Game?.tickManager?.TicksGame ?? -1;
            bool alreadySteppedThisTick = nowTick >= 0 && lastSteppedGameTick == nowTick;
            if (nowTick >= 0)
            {
                lastSteppedGameTick = nowTick;
            }

            if (!headingInitialized)
            {
                float initDeg = rig.CurrentHeadingDeg;
                Reset(initDeg, ComputeRawPatherPosXZ(pawn), hasValidPos: true);
            }
            else if (!hasExactVehiclePos)
            {
                exactVehiclePosXZ = ComputeRawPatherPosXZ(pawn);
                hasExactVehiclePos = true;
            }

            rig.GetLivePartMaskAndHealth(out ReadOnlySpan<bool> missingSpan, out ReadOnlySpan<float> healthSpan);
            EvaluateDriveMobility(missingSpan, healthSpan);

            Vector2 rawPatherXZ = ComputeRawPatherPosXZ(pawn);
            if ((rawPatherXZ - exactVehiclePosXZ).sqrMagnitude > 3.6f)
            {
                // Clean snap on dev teleport / map respawn
                exactVehiclePosXZ = rawPatherXZ;
            }

            if (nowTick < 0 || nowTick - lastMoveSpeedCacheTick >= 15 || lastMoveSpeedCacheTick < 0)
            {
                cachedMoveSpeedStat = Mathf.Max(0.1f, pawn.GetStatValue(StatDefOf.MoveSpeed));
                lastMoveSpeedCacheTick = nowTick;
            }
            float baseMoveSpeed = cachedMoveSpeedStat;
            float unscaledCruiseSpeed = driveMobilitySpeedFactor > 0.01f
                ? baseMoveSpeed / driveMobilitySpeedFactor
                : baseMoveSpeed;

            bool hasMove = SampleLivePathLookahead(
                pawn,
                exactVehiclePosXZ,
                out _,
                out float lookBearing,
                out float totalRem);

            bool wantStationaryAim = false;
            float stationaryAimDeg = lockedHullHeadingDeg;
            if (!hasMove && rig.PartCombat != null &&
                (def.allowHullTraverseForFixedWeapons || !rig.HasAnyOperationalTurretBone()))
            {
                if (rig.PartCombat.TryGetPrimaryCombatLookTargetXZ(out Vector2 aimXZ, out bool rotateHull) && rotateHull)
                {
                    Vector2 dAim = aimXZ - exactVehiclePosXZ;
                    if (dAim.sqrMagnitude > 0.09f)
                    {
                        wantStationaryAim = true;
                        stationaryAimDeg = BearingDegFromDelta(dAim);
                    }
                }
            }

            if (!alreadySteppedThisTick)
            {
                var stepInput = new VehicleKinematicsStepInput
                {
                    HasMoveOrder = hasMove,
                    TotalRemainingPathCells = totalRem,
                    LookaheadBearingDeg = lookBearing,
                    MaxCruiseSpeedCellsPerSec = unscaledCruiseSpeed,
                    HasStationaryHullAimRequest = wantStationaryAim,
                    StationaryHullAimDeg = stationaryAimDeg,
                    DeltaTicks = 1f
                };
                StepKinematicsCore(in stepInput);
            }

            // Once ExactVehiclePosXZ has reached the final destination cell center (totalRem <= 0.35 or !hasMove),
            // let PatherTick finish stepping nextCellCostLeft -> 0 cleanly so PatherArrived() fires without stalling,
            // even if a severed-turret hull begins stationary aim pivoting at arrival.
            if (!isDriveImmobilized && gearShiftCooldownTicks <= 0 && (!hasMove || totalRem <= 0.35f))
            {
                isStationaryPivot = true;
                return 1f;
            }

            if (ShouldBlockPatherCellAdvance || baseMoveSpeed <= 0.01f)
            {
                return 0f;
            }

            float curAbsCellsPerSec = Mathf.Abs(CurrentSignedSpeedCellsPerSec);
            return Mathf.Clamp(curAbsCellsPerSec / baseMoveSpeed, 0.12f, 1.0f);
        }

        /// <summary>
        /// Ensures kinematics stepped once for the current tick even if PatherTick did not run, then syncs ExactVehiclePosXZ.
        /// </summary>
        public void EnsureSteppedAndSyncPosition(Pawn pawn, CompGiantRig rig)
        {
            if (pawn != null && pawn.Spawned && !pawn.Dead && !pawn.Downed && pawn.pather != null)
            {
                int nowTick = Current.Game?.tickManager?.TicksGame ?? -1;
                if (nowTick < 0 || lastSteppedGameTick != nowTick)
                {
                    PrePatherTickAndComputeSpeedScale(pawn.pather, pawn, rig);
                }
            }
            PostCompTickSyncPositionAndVfx(pawn);
        }

        /// <summary>
        /// Synchronizes non-holonomic ExactVehiclePosXZ after PatherTick and emits subtle rear track dust during pivots/braking.
        /// </summary>
        public void PostCompTickSyncPositionAndVfx(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead)
            {
                lastStepSignedForwardCells = 0f;
                lastStepLateralDriftCells = 0f;
                return;
            }

            if (!hasExactVehiclePos)
            {
                exactVehiclePosXZ = ComputeRawPatherPosXZ(pawn);
                hasExactVehiclePos = true;
            }

            Vector2 prevPos = exactVehiclePosXZ;
            Vector2 rawPatherXZ = ComputeRawPatherPosXZ(pawn);
            Vector2 toPather = rawPatherXZ - exactVehiclePosXZ;
            bool settledWhileParked = false;

            if (toPather.sqrMagnitude > 3.6f)
            {
                exactVehiclePosXZ = rawPatherXZ;
            }
            else if (!ShouldHoldPositionInPlace && pawn.pather != null && pawn.pather.Moving)
            {
                Vector2 hullFwd = HullForwardFromDeg(hullHeadingDeg);
                float projFwd = Vector2.Dot(toPather, hullFwd);
                if (currentSignedSpeedCellsPerTick >= 0f)
                {
                    projFwd = Mathf.Max(0f, projFwd);
                }
                else
                {
                    projFwd = Mathf.Min(0f, projFwd);
                }

                exactVehiclePosXZ += hullFwd * projFwd;

                if ((def.chassisType == VehicleChassisType.Hover || def.chassisType == VehicleChassisType.Naval) && def.maxSlipAngleDeg > 0.1f)
                {
                    Vector2 hullRight = HullRightFromDeg(hullHeadingDeg);
                    float maxLat = Mathf.Abs(projFwd) * Mathf.Tan(def.maxSlipAngleDeg * Mathf.Deg2Rad);
                    float projRight = Mathf.Clamp(Vector2.Dot(toPather, hullRight) * 0.25f, -maxLat, maxLat);
                    exactVehiclePosXZ += hullRight * projRight;
                }

                // Safety leash so sub-cell path smoothing never separates from logical Pawn.Position by >= 1 cell
                Vector2 residual = rawPatherXZ - exactVehiclePosXZ;
                if (residual.sqrMagnitude > 0.72f)
                {
                    exactVehiclePosXZ = rawPatherXZ - residual.normalized * 0.84f;
                }
            }
            else if (driveState == VehicleDriveState.Parked && toPather.sqrMagnitude > 1e-6f)
            {
                settledWhileParked = true;
                if (toPather.sqrMagnitude <= 1e-4f)
                {
                    exactVehiclePosXZ = rawPatherXZ;
                }
                else
                {
                    exactVehiclePosXZ = Vector2.Lerp(exactVehiclePosXZ, rawPatherXZ, 0.14f);
                }
            }

            Vector2 actualDisp = exactVehiclePosXZ - prevPos;
            Vector2 uFwd = HullForwardFromDeg(hullHeadingDeg);
            Vector2 uRight = HullRightFromDeg(hullHeadingDeg);
            if (settledWhileParked)
            {
                lastStepSignedForwardCells = 0f;
                lastStepLateralDriftCells = 0f;
            }
            else
            {
                lastStepSignedForwardCells = Vector2.Dot(actualDisp, uFwd);
                lastStepLateralDriftCells = Vector2.Dot(actualDisp, uRight);
            }

            // Sync discrete Rot4 cleanly from continuous HullHeadingDeg
            if (def.lockHullHeadingWhenParked)
            {
                Rot4 desiredRot = Rot4.FromAngleFlat(hullHeadingDeg);
                if (pawn.Rotation != desiredRot)
                {
                    pawn.Rotation = desiredRot;
                }
            }

            MaybeSpawnTrackGroundFeedback(pawn, uFwd, uRight);
        }

        private void MaybeSpawnTrackGroundFeedback(Pawn pawn, Vector2 uFwd, Vector2 uRight)
        {
            Map map = pawn.Map;
            if (map == null || def.chassisType == VehicleChassisType.Hover)
            {
                return;
            }
            int now = Current.Game?.tickManager?.TicksGame ?? 0;
            bool pivoting = driveState == VehicleDriveState.PivotTurning && Mathf.Abs(lastStepHullDeltaDeg) > 0.35f;
            bool hardBraking = driveState == VehicleDriveState.Braking && Mathf.Abs(pitchImpulseOffsetY) > 0.012f;
            if (!pivoting && !hardBraking)
            {
                return;
            }
            int interval = pivoting ? 9 : 11;
            if (now - lastDustPulseTick < interval)
            {
                return;
            }
            lastDustPulseTick = now;

            float halfGauge = 1.85f;
            if (rigDef.rollingTracks != null && rigDef.rollingTracks.Count > 0)
            {
                halfGauge = Mathf.Max(0.8f, rigDef.rollingTracks[0].gaugeHalfWidthCells);
            }

            Vector2 sternBase = exactVehiclePosXZ - uFwd * 2.8f;
            Vector2 leftTrackPt = sternBase - uRight * halfGauge;
            Vector2 rightTrackPt = sternBase + uRight * halfGauge;
            Vector3 lPos = new Vector3(leftTrackPt.x, 0f, leftTrackPt.y);
            Vector3 rPos = new Vector3(rightTrackPt.x, 0f, rightTrackPt.y);

            TerrainDef curTerrain = pawn.Position.InBounds(map) ? map.terrainGrid?.TerrainAt(pawn.Position) : null;
            bool onWater = def.chassisType == VehicleChassisType.Naval
                || (curTerrain != null && curTerrain.IsWater && !GiantAquaticUtility.IsMarshTerrain(curTerrain));
            if (onWater)
            {
                if (lPos.InBounds(map))
                {
                    FleckMaker.WaterSplash(lPos, map, 1.35f, 1.8f);
                }
                if (rPos.InBounds(map))
                {
                    FleckMaker.WaterSplash(rPos, map, 1.35f, 1.8f);
                }
                return;
            }

            Color lowSatDust = new Color(0.56f, 0.54f, 0.50f, 0.42f);
            if (lPos.InBounds(map))
            {
                FleckMaker.ThrowDustPuffThick(lPos, map, 1.15f, lowSatDust);
            }
            if (rPos.InBounds(map))
            {
                FleckMaker.ThrowDustPuffThick(rPos, map, 1.15f, lowSatDust);
            }
        }
    }
}
