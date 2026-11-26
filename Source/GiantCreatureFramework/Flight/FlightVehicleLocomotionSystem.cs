using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public enum FlightVehicleAirframeType : byte
    {
        None = 0,
        FixedWing = 1,
        RotorcraftVTOL = 2,
        HeavyAirship = 3
    }

    public enum FlightVehicleDriveState : byte
    {
        GroundedParked = 0,
        Taxiing = 1,
        Hovering = 2,
        HoverYawing = 3,
        ForwardCruise = 4,
        BankedTurn = 5,
        FlareDecelerating = 6,
        LoiterOrbit = 7
    }

    public struct ResolvedFlightVehicleThrusterBone
    {
        public int boneIndex;
        public float sideSign;
    }

    /// <summary>
    /// Opt-in realistic aircraft / flying-vehicle kinematics specification mounted on
    /// &lt;flightVehicleLocomotion&gt; in GiantRigDef (or &lt;steering&gt;&lt;flightVehicleLocomotion&gt;).
    /// Strictly inert when null or airframeType == FlightVehicleAirframeType.None so biological
    /// flyers (wyverns/dragons), walkers, and ground vehicles execute zero aircraft locomotion code.
    /// </summary>
    public class FlightVehicleLocomotionDef
    {
        public FlightVehicleAirframeType airframeType = FlightVehicleAirframeType.None;

        // Aerodynamic stall / loiter envelope (FixedWing cannot stop dead in mid-air)
        public float minAirspeedFraction = 0.42f;
        public float loiterSpeedFraction = 0.56f;
        public float loiterOrbitRadiusCells = 6.5f;
        public bool orbitClockwise = true;

        // Hover & translational lift transition (RotorcraftVTOL / HeavyAirship)
        public bool canHoverInPlace = true;
        public float hoverTransitionSpeedFraction = 0.24f;
        public float hoverYawRateDegPerTick = 1.65f;
        public float hoverYawEnterAngleDeg = 28f;
        public float hoverYawExitAngleDeg = 7f;

        // Coordinated bank-to-turn aerodynamics: omega_rad = (effectiveGravity * tan(bank)) / max(v, vRef)
        public float maxBankAngleDeg = 28f;
        public float maxRollRateDegPerTick = 1.45f;
        public float maxTurnRateDegPerTick = 2.10f;
        public float minTurnRadiusCells = 5.2f;
        public float effectiveGravityCellsPerSec2 = 11.5f;
        public float inducedTurnDragFactor = 0.22f;

        // Longitudinal engine spool, thrust acceleration & airbrake / cyclic flare deceleration
        public float throttleSpoolRatePerSec = 1.65f;
        public float thrustAccelCellsPerSec2 = 2.40f;
        public float airbrakeDecelCellsPerSec2 = 3.80f;
        public float flareStartDistanceScale = 1.25f;

        // Ground taxiing envelope when grounded (TakeoffAndLanding mode)
        public float taxiSpeedMultiplier = 0.36f;
        public float taxiTurnRateDegPerTick = 1.25f;
        public float taxiMinTurnRadiusCells = 3.2f;

        // Path lookahead, hull lock & turret/hangar gyro stabilization
        public float lookaheadMinCells = 3.2f;
        public float lookaheadMaxCells = 7.5f;
        public bool lockHullHeadingWhenHovered = true;
        public bool allowHullTraverseForFixedWeapons = false;
        public bool stabilizeTurretsAgainstHullTurn = true;
        public float movingFireAccuracyFactor = 0.88f;

        // Visual attitude offsets & biological gait suppression
        public bool suppressBiologicalGait = true;
        public float cruiseNoseDownPitchOffset = 0.04f;
        public float flareNoseUpPitchOffset = 0.06f;
        public float damagedPropulsionMinSpeedFactor = 0.42f;

        [Unsaved]
        public ResolvedFlightVehicleThrusterBone[] resolvedThrusterBones = Array.Empty<ResolvedFlightVehicleThrusterBone>();

        [Unsaved]
        public bool hasPortThrusterGroup;

        [Unsaved]
        public bool hasStarboardThrusterGroup;

        [Unsaved]
        public bool hasCenterThrusterGroup;

        public bool IsActive => airframeType != FlightVehicleAirframeType.None;

        public bool SupportsStationaryHover =>
            airframeType != FlightVehicleAirframeType.FixedWing && canHoverInPlace;

        public void ResolveTopology(BoneNodeDef[] sortedNodes)
        {
            if (!IsActive || sortedNodes == null || sortedNodes.Length == 0)
            {
                resolvedThrusterBones = Array.Empty<ResolvedFlightVehicleThrusterBone>();
                hasPortThrusterGroup = false;
                hasStarboardThrusterGroup = false;
                hasCenterThrusterGroup = false;
                return;
            }

            var list = new List<ResolvedFlightVehicleThrusterBone>(6);
            bool port = false;
            bool starboard = false;
            bool center = false;

            for (int i = 0; i < sortedNodes.Length; i++)
            {
                BoneNodeDef node = sortedNodes[i];
                if (node == null || (!node.isFlightLiftPart && !node.isLocomotionDrivePart))
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

                list.Add(new ResolvedFlightVehicleThrusterBone
                {
                    boneIndex = i,
                    sideSign = sign
                });
            }

            resolvedThrusterBones = list.Count > 0 ? list.ToArray() : Array.Empty<ResolvedFlightVehicleThrusterBone>();
            hasPortThrusterGroup = port;
            hasStarboardThrusterGroup = starboard;
            hasCenterThrusterGroup = center;
        }

        public IEnumerable<string> ValidateConfig(string rigDefName)
        {
            if (!IsActive)
            {
                yield break;
            }
            if (maxBankAngleDeg < 2f || maxBankAngleDeg > 78f)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightVehicleLocomotion> maxBankAngleDeg ({maxBankAngleDeg:F1}) must lie in [2, 78].";
            }
            if (maxRollRateDegPerTick <= 0.05f || maxTurnRateDegPerTick <= 0.05f)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightVehicleLocomotion> maxRollRateDegPerTick and maxTurnRateDegPerTick must be > 0.05.";
            }
            if (minTurnRadiusCells <= 0.5f)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightVehicleLocomotion> minTurnRadiusCells ({minTurnRadiusCells:F2}) must be > 0.5.";
            }
            if (thrustAccelCellsPerSec2 <= 0.05f || airbrakeDecelCellsPerSec2 <= 0.05f)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightVehicleLocomotion> thrustAccelCellsPerSec2 and airbrakeDecelCellsPerSec2 must be > 0.05.";
            }
            if (airframeType == FlightVehicleAirframeType.FixedWing)
            {
                if (minAirspeedFraction < 0.15f || minAirspeedFraction > 0.90f)
                {
                    yield return $"GiantRigDef '{rigDefName}' FixedWing <flightVehicleLocomotion> minAirspeedFraction ({minAirspeedFraction:F2}) must lie in [0.15, 0.90].";
                }
                if (loiterOrbitRadiusCells < 2.0f)
                {
                    yield return $"GiantRigDef '{rigDefName}' FixedWing <flightVehicleLocomotion> loiterOrbitRadiusCells ({loiterOrbitRadiusCells:F2}) must be >= 2.0.";
                }
            }
            if (lookaheadMinCells <= 0.5f || lookaheadMaxCells < lookaheadMinCells)
            {
                yield return $"GiantRigDef '{rigDefName}' <flightVehicleLocomotion> lookahead range [{lookaheadMinCells:F2}, {lookaheadMaxCells:F2}] is invalid.";
            }
        }
    }

    public struct FlightVehicleKinematicsStepInput
    {
        public bool IsAirborne;
        public bool HasMoveOrder;
        public float TotalRemainingPathCells;
        public float LookaheadBearingDeg;
        public float MaxCruiseSpeedCellsPerSec;
        public bool HasStationaryHullAimRequest;
        public float StationaryHullAimDeg;
        public float DeltaTicks;
        public float MapSizeX;
        public float MapSizeZ;
    }

    /// <summary>
    /// Deterministic realistic aircraft / VTOL / airship locomotion runtime controller.
    /// Instantiated ONLY when GiantRigDef.ActiveFlightVehicleLocomotion != null.
    /// </summary>
    public sealed class FlightVehicleLocomotionRuntime
    {
        private const float TicksPerSecond = 60f;
        private const float TicksPerSecondSq = 3600f;

        private readonly GiantRigDef rigDef;
        private readonly FlightVehicleLocomotionDef def;

        private FlightVehicleDriveState driveState = FlightVehicleDriveState.Hovering;
        private float hullHeadingDeg;
        private float lockedHullHeadingDeg;
        private bool headingInitialized;

        private float currentForwardSpeedCellsPerTick;
        private float throttle01;
        private float currentBankAngleDeg;
        private float currentAngularVelDegPerTick;
        private float lastStepHullDeltaDeg;
        private float lastStepForwardCells;
        private float pitchVisualOffsetZ;

        private Vector2 exactWorldPosXZ;
        private Vector2 loiterAnchorXZ;
        private bool hasExactWorldPos;
        private bool hasLoiterAnchor;
        private float activeLoiterOrbitSign = 1f;
        private int loiterExitBlendTicks;
        private bool hoverYawLatched;

        private float propulsionSpeedFactor = 1f;
        private float portPropulsionEfficiency = 1f;
        private float starboardPropulsionEfficiency = 1f;
        private int lastHealthPollTick = -999;
        private int lastMoveSpeedCacheTick = -999;
        private float cachedMoveSpeedStat = 3.8f;

        public FlightVehicleLocomotionDef Def => def;
        public FlightVehicleDriveState DriveState => driveState;
        public float HullHeadingDeg => hullHeadingDeg;
        public float CurrentForwardSpeedCellsPerTick => currentForwardSpeedCellsPerTick;
        public float CurrentForwardSpeedCellsPerSec => currentForwardSpeedCellsPerTick * TicksPerSecond;
        public float Throttle01 => throttle01;
        public float CurrentBankAngleDeg => currentBankAngleDeg;
        public float CurrentAngularVelDegPerTick => currentAngularVelDegPerTick;
        public float LastStepHullDeltaDeg => lastStepHullDeltaDeg;
        public float LastStepForwardCells => lastStepForwardCells;
        public float PitchVisualOffsetZ => pitchVisualOffsetZ;
        public float PropulsionSpeedFactor => propulsionSpeedFactor;
        public float PortPropulsionEfficiency => portPropulsionEfficiency;
        public float StarboardPropulsionEfficiency => starboardPropulsionEfficiency;
        public Vector2 ExactWorldPosXZ => exactWorldPosXZ;

        public bool ShouldHoldPatherInPlace =>
            driveState == FlightVehicleDriveState.HoverYawing ||
            driveState == FlightVehicleDriveState.Hovering ||
            driveState == FlightVehicleDriveState.GroundedParked;

        public bool ShouldBlockPatherCellAdvance =>
            driveState == FlightVehicleDriveState.HoverYawing && hoverYawLatched;

        public bool ShouldLockPawnRotation =>
            def.lockHullHeadingWhenHovered ||
            driveState == FlightVehicleDriveState.ForwardCruise ||
            driveState == FlightVehicleDriveState.BankedTurn ||
            driveState == FlightVehicleDriveState.LoiterOrbit ||
            driveState == FlightVehicleDriveState.FlareDecelerating;

        public FlightVehicleLocomotionRuntime(GiantRigDef rigDef, FlightVehicleLocomotionDef def)
        {
            this.rigDef = rigDef ?? throw new ArgumentNullException(nameof(rigDef));
            this.def = def ?? throw new ArgumentNullException(nameof(def));
        }

        public void Reset(float initialHeadingDeg, Vector2 initialWorldXZ = default, bool hasValidPos = false, bool startAirborne = true)
        {
            hullHeadingDeg = SkeletalKinematics.NormalizeAngle360(initialHeadingDeg);
            lockedHullHeadingDeg = hullHeadingDeg;
            headingInitialized = true;
            currentForwardSpeedCellsPerTick = 0f;
            throttle01 = 0f;
            currentBankAngleDeg = 0f;
            currentAngularVelDegPerTick = 0f;
            lastStepHullDeltaDeg = 0f;
            lastStepForwardCells = 0f;
            pitchVisualOffsetZ = 0f;
            hoverYawLatched = false;
            exactWorldPosXZ = initialWorldXZ;
            loiterAnchorXZ = initialWorldXZ;
            hasExactWorldPos = hasValidPos;
            hasLoiterAnchor = false;
            activeLoiterOrbitSign = def.orbitClockwise ? 1f : -1f;
            loiterExitBlendTicks = 0;
            propulsionSpeedFactor = 1f;
            portPropulsionEfficiency = 1f;
            starboardPropulsionEfficiency = 1f;
            lastHealthPollTick = -999;

            if (!startAirborne)
            {
                driveState = FlightVehicleDriveState.GroundedParked;
            }
            else if (def.airframeType == FlightVehicleAirframeType.FixedWing)
            {
                driveState = FlightVehicleDriveState.LoiterOrbit;
            }
            else
            {
                driveState = FlightVehicleDriveState.Hovering;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref driveState, "gcf_fvehState", FlightVehicleDriveState.Hovering);
            Scribe_Values.Look(ref hullHeadingDeg, "gcf_fvehHeading", 0f);
            Scribe_Values.Look(ref lockedHullHeadingDeg, "gcf_fvehLockedHeading", 0f);
            Scribe_Values.Look(ref headingInitialized, "gcf_fvehHeadingInit", false);
            Scribe_Values.Look(ref currentForwardSpeedCellsPerTick, "gcf_fvehSpeed", 0f);
            Scribe_Values.Look(ref throttle01, "gcf_fvehThrottle", 0f);
            Scribe_Values.Look(ref currentBankAngleDeg, "gcf_fvehBank", 0f);
            Scribe_Values.Look(ref currentAngularVelDegPerTick, "gcf_fvehAngVel", 0f);
            Scribe_Values.Look(ref pitchVisualOffsetZ, "gcf_fvehPitchZ", 0f);
            Scribe_Values.Look(ref exactWorldPosXZ, "gcf_fvehExactXZ", Vector2.zero);
            Scribe_Values.Look(ref loiterAnchorXZ, "gcf_fvehLoiterXZ", Vector2.zero);
            Scribe_Values.Look(ref hasExactWorldPos, "gcf_fvehHasPos", false);
            Scribe_Values.Look(ref hasLoiterAnchor, "gcf_fvehHasLoiter", false);
            Scribe_Values.Look(ref hoverYawLatched, "gcf_fvehHoverYaw", false);
        }

        public static Vector2 HullForwardFromDeg(float headingDeg)
        {
            float rad = headingDeg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        }

        public static float BearingDegFromDelta(Vector2 deltaXZ)
        {
            if (deltaXZ.sqrMagnitude <= 1e-6f)
            {
                return 0f;
            }
            return SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(deltaXZ.x, deltaXZ.y) * Mathf.Rad2Deg);
        }

        /// <summary>
        /// Computes realistic coordinated turn rate (deg/tick) from bank angle (deg) and forward speed (cells/sec):
        /// omega = (g_eff * tan(bank)) / max(v, vFloor), bounded by structural turn rate and minimum turning radius.
        /// Convention: positive headingDeg turns clockwise (starboard, +X from +Z), while positive bankAngleDeg in
        /// GiantFlightRuntime leans starboard/port visually as -headingStepDeg; here signedBankDeg > 0 means right/starboard bank (+heading).
        /// </summary>
        public static float ComputeCoordinatedTurnRateDegPerTick(
            float signedBankDeg,
            float speedCellsPerSec,
            float effectiveGravityCellsPerSec2,
            float minTurnRadiusCells,
            float maxTurnRateDegPerTick)
        {
            float absBank = Mathf.Clamp(Mathf.Abs(signedBankDeg), 0f, 80f);
            if (absBank <= 0.01f)
            {
                return 0f;
            }

            float vEff = Mathf.Max(0.85f, speedCellsPerSec);
            float gEff = Mathf.Max(1.0f, effectiveGravityCellsPerSec2);
            float omegaRadPerSec = (gEff * Mathf.Tan(absBank * Mathf.Deg2Rad)) / vEff;
            float omegaDegPerTick = (omegaRadPerSec * Mathf.Rad2Deg) / TicksPerSecond;

            if (minTurnRadiusCells > 0.25f && speedCellsPerSec > 0.10f)
            {
                float radiusCapDegPerTick = ((speedCellsPerSec / minTurnRadiusCells) * Mathf.Rad2Deg) / TicksPerSecond;
                omegaDegPerTick = Mathf.Min(omegaDegPerTick, radiusCapDegPerTick);
            }

            omegaDegPerTick = Mathf.Min(omegaDegPerTick, Mathf.Max(0.05f, maxTurnRateDegPerTick));
            return Mathf.Sign(signedBankDeg) * omegaDegPerTick;
        }

        public void RefreshPropulsionHealth(CompGiantRig rigComp)
        {
            if (rigComp?.Pawn?.health?.hediffSet == null || def.resolvedThrusterBones.Length == 0)
            {
                propulsionSpeedFactor = 1f;
                portPropulsionEfficiency = 1f;
                starboardPropulsionEfficiency = 1f;
                return;
            }

            int now = Current.Game?.tickManager?.TicksGame ?? 0;
            if (now - lastHealthPollTick < 15 && lastHealthPollTick >= 0)
            {
                return;
            }
            lastHealthPollTick = now;

            float portSum = 0f, starSum = 0f, centerSum = 0f;
            int portCnt = 0, starCnt = 0, centerCnt = 0;

            for (int i = 0; i < def.resolvedThrusterBones.Length; i++)
            {
                ResolvedFlightVehicleThrusterBone tb = def.resolvedThrusterBones[i];
                bool intact = rigComp.IsBoneVisibleThreadSafe(tb.boneIndex);
                float hp = intact ? Mathf.Clamp01(rigComp.GetBoneHealthPercentThreadSafe(tb.boneIndex)) : 0f;
                if (tb.sideSign < -0.05f)
                {
                    portSum += hp;
                    portCnt++;
                }
                else if (tb.sideSign > 0.05f)
                {
                    starSum += hp;
                    starCnt++;
                }
                else
                {
                    centerSum += hp;
                    centerCnt++;
                }
            }

            portPropulsionEfficiency = portCnt > 0 ? (portSum / portCnt) : 1f;
            starboardPropulsionEfficiency = starCnt > 0 ? (starSum / starCnt) : 1f;
            float centerEff = centerCnt > 0 ? (centerSum / centerCnt) : 1f;

            int activeGroups = (portCnt > 0 ? 1 : 0) + (starCnt > 0 ? 1 : 0) + (centerCnt > 0 ? 1 : 0);
            float combined = activeGroups > 0
                ? ((portCnt > 0 ? portPropulsionEfficiency : 0f)
                   + (starCnt > 0 ? starboardPropulsionEfficiency : 0f)
                   + (centerCnt > 0 ? centerEff : 0f)) / activeGroups
                : 1f;

            propulsionSpeedFactor = Mathf.Lerp(
                Mathf.Clamp(def.damagedPropulsionMinSpeedFactor, 0.15f, 1f),
                1f,
                Mathf.Clamp01(combined));
        }

        public void SetSimulatedPropulsionEfficiencyForTest(float portEff, float starboardEff, float centerEff = 1f)
        {
            portPropulsionEfficiency = Mathf.Clamp01(portEff);
            starboardPropulsionEfficiency = Mathf.Clamp01(starboardEff);
            float avg = (portPropulsionEfficiency + starboardPropulsionEfficiency + Mathf.Clamp01(centerEff)) / 3f;
            propulsionSpeedFactor = Mathf.Lerp(
                Mathf.Clamp(def.damagedPropulsionMinSpeedFactor, 0.15f, 1f),
                1f,
                avg);
        }

        public void StepDormantBleed(float dt = 1f)
        {
            float d = Mathf.Max(0f, dt);
            if (d <= 0f)
            {
                return;
            }
            float rollStep = Mathf.Max(0.25f, def.maxRollRateDegPerTick) * 1.2f * d;
            float decelPerTick2 = Mathf.Max(0.10f, def.airbrakeDecelCellsPerSec2) / TicksPerSecondSq;
            float spoolStep = (Mathf.Max(0.15f, def.throttleSpoolRatePerSec) / TicksPerSecond) * d;

            currentBankAngleDeg = Mathf.MoveTowards(currentBankAngleDeg, 0f, rollStep);
            currentForwardSpeedCellsPerTick = Mathf.MoveTowards(currentForwardSpeedCellsPerTick, 0f, decelPerTick2 * d);
            throttle01 = Mathf.MoveTowards(throttle01, 0f, spoolStep);
            currentAngularVelDegPerTick = Mathf.MoveTowards(currentAngularVelDegPerTick, 0f, 0.35f * d);
            pitchVisualOffsetZ = Mathf.Lerp(pitchVisualOffsetZ, 0f, Mathf.Min(1f, 0.18f * d));
            hoverYawLatched = false;
            lastStepHullDeltaDeg = 0f;
            lastStepForwardCells = 0f;
        }

        public float GetMovingFireAccuracyMultiplier()
        {
            if (currentForwardSpeedCellsPerTick <= 1e-4f)
            {
                return 1f;
            }
            return Mathf.Clamp(def.movingFireAccuracyFactor, 0.25f, 1.25f);
        }

        /// <summary>
        /// Core deterministic aerodynamics & flight-control solver shared 1:1 between live RimWorld ticks and headless tests.
        /// Never allows mid-air reverse gear; enforces stall-speed floor & loiter orbits on FixedWing, collective flare & pedal
        /// yaw on RotorcraftVTOL, and high-inertia banked turns on HeavyAirship.
        /// </summary>
        public void StepKinematicsCore(in FlightVehicleKinematicsStepInput input)
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
                lastStepForwardCells = 0f;
                return;
            }

            float prevHeading = hullHeadingDeg;
            float maxCruiseCellsPerSec = Mathf.Max(0.2f, input.MaxCruiseSpeedCellsPerSec * propulsionSpeedFactor);
            float maxCruisePerTick = maxCruiseCellsPerSec / TicksPerSecond;

            float spoolStep = (Mathf.Max(0.15f, def.throttleSpoolRatePerSec) / TicksPerSecond) * dt;
            float accelPerTick2 = Mathf.Max(0.05f, def.thrustAccelCellsPerSec2 * propulsionSpeedFactor) / TicksPerSecondSq;
            float decelPerTick2 = Mathf.Max(0.10f, def.airbrakeDecelCellsPerSec2) / TicksPerSecondSq;

            float asymmetryFactor = 1f - 0.35f * Mathf.Abs(portPropulsionEfficiency - starboardPropulsionEfficiency);
            float effMaxBankDeg = Mathf.Clamp(def.maxBankAngleDeg * asymmetryFactor, 2f, 78f);
            float effMaxRollStep = Mathf.Max(0.05f, def.maxRollRateDegPerTick * asymmetryFactor) * dt;
            float effMaxTurnRate = Mathf.Max(0.05f, def.maxTurnRateDegPerTick * asymmetryFactor);

            // 1. Grounded taxiing / parked envelope (for TakeoffAndLanding aircraft while on runway/pad)
            if (!input.IsAirborne)
            {
                hoverYawLatched = false;
                currentBankAngleDeg = Mathf.MoveTowards(currentBankAngleDeg, 0f, effMaxRollStep * 1.5f);
                pitchVisualOffsetZ = Mathf.Lerp(pitchVisualOffsetZ, 0f, 0.20f * Mathf.Min(1f, dt));

                bool hasGroundMove = input.HasMoveOrder && input.TotalRemainingPathCells > 0.05f;
                if (!hasGroundMove)
                {
                    throttle01 = Mathf.MoveTowards(throttle01, 0f, spoolStep * 1.5f);
                    currentForwardSpeedCellsPerTick = Mathf.MoveTowards(currentForwardSpeedCellsPerTick, 0f, decelPerTick2 * 1.35f * dt);
                    currentAngularVelDegPerTick = Mathf.MoveTowards(currentAngularVelDegPerTick, 0f, 0.30f * dt);
                    driveState = currentForwardSpeedCellsPerTick > 1e-4f
                        ? FlightVehicleDriveState.Taxiing
                        : FlightVehicleDriveState.GroundedParked;
                    lockedHullHeadingDeg = hullHeadingDeg;
                }
                else
                {
                    driveState = FlightVehicleDriveState.Taxiing;
                    float taxiCapPerTick = maxCruisePerTick * Mathf.Clamp(def.taxiSpeedMultiplier, 0.15f, 0.85f);
                    float err = Mathf.DeltaAngle(hullHeadingDeg, input.LookaheadBearingDeg);
                    throttle01 = Mathf.MoveTowards(throttle01, Mathf.Abs(err) > 65f ? 0.35f : 1f, spoolStep);

                    float stopDist = (currentForwardSpeedCellsPerTick * currentForwardSpeedCellsPerTick) / Mathf.Max(1e-6f, 2f * decelPerTick2);
                    float targetTaxiSpeed = input.TotalRemainingPathCells <= stopDist * 1.15f
                        ? Mathf.Sqrt(Mathf.Max(0f, 2f * decelPerTick2 * input.TotalRemainingPathCells))
                        : taxiCapPerTick * throttle01;

                    currentForwardSpeedCellsPerTick = currentForwardSpeedCellsPerTick < targetTaxiSpeed
                        ? Mathf.MoveTowards(currentForwardSpeedCellsPerTick, targetTaxiSpeed, accelPerTick2 * dt)
                        : Mathf.MoveTowards(currentForwardSpeedCellsPerTick, targetTaxiSpeed, decelPerTick2 * dt);

                    // Nosewheel steering requires forward rolling motion for FixedWing, or slow hover-taxi yaw for VTOL
                    float speedRatio = taxiCapPerTick > 1e-5f ? Mathf.Clamp01(currentForwardSpeedCellsPerTick / taxiCapPerTick) : 0f;
                    float nosewheelRate = def.airframeType == FlightVehicleAirframeType.FixedWing
                        ? def.taxiTurnRateDegPerTick * Mathf.Max(0.20f, speedRatio)
                        : def.taxiTurnRateDegPerTick;
                    float stepYaw = Mathf.Clamp(err * 0.35f, -nosewheelRate * dt, nosewheelRate * dt);
                    hullHeadingDeg = SkeletalKinematics.NormalizeAngle360(hullHeadingDeg + stepYaw);
                    currentAngularVelDegPerTick = stepYaw / dt;
                    lockedHullHeadingDeg = hullHeadingDeg;
                }

                AdvanceKinematicsPosition(prevHeading, dt);
                return;
            }

            // 2. Airborne Flight Envelope (ZERO reverse gear ever allowed)
            bool hasAirMove = input.HasMoveOrder && input.TotalRemainingPathCells > 0.06f;

            if (!hasAirMove)
            {
                if (!def.SupportsStationaryHover)
                {
                    // Fixed-wing aircraft CANNOT hover at zero airspeed: enter continuous banked loiter orbit above stall speed
                    driveState = FlightVehicleDriveState.LoiterOrbit;
                    loiterExitBlendTicks = 30;
                    float desiredR = Mathf.Max(2.5f, def.loiterOrbitRadiusCells);
                    const float marginMin = 2.0f;
                    bool hasUpperBounds = input.MapSizeX > 8f && input.MapSizeZ > 8f;
                    bool enforceLowerBounds = hasUpperBounds || (exactWorldPosXZ.x >= 0f && exactWorldPosXZ.y >= 0f);
                    float maxX = hasUpperBounds ? (input.MapSizeX - marginMin) : float.MaxValue;
                    float maxZ = hasUpperBounds ? (input.MapSizeZ - marginMin) : float.MaxValue;

                    if (!hasLoiterAnchor)
                    {
                        float prefSign = def.orbitClockwise ? 1f : -1f;
                        Vector2 prefAnchor = exactWorldPosXZ + HullForwardFromDeg(hullHeadingDeg + 90f * prefSign) * desiredR;
                        Vector2 altAnchor = exactWorldPosXZ + HullForwardFromDeg(hullHeadingDeg - 90f * prefSign) * desiredR;

                        float EvalBoundaryViolation(Vector2 a)
                        {
                            float v = 0f;
                            if (enforceLowerBounds)
                            {
                                v += Mathf.Max(0f, (marginMin + desiredR) - a.x);
                                v += Mathf.Max(0f, (marginMin + desiredR) - a.y);
                            }
                            if (hasUpperBounds)
                            {
                                v += Mathf.Max(0f, a.x - (maxX - desiredR));
                                v += Mathf.Max(0f, a.y - (maxZ - desiredR));
                            }
                            return v;
                        }

                        if (EvalBoundaryViolation(altAnchor) + 0.25f < EvalBoundaryViolation(prefAnchor))
                        {
                            activeLoiterOrbitSign = -prefSign;
                            loiterAnchorXZ = altAnchor;
                        }
                        else
                        {
                            activeLoiterOrbitSign = prefSign;
                            loiterAnchorXZ = prefAnchor;
                        }

                        if (hasUpperBounds)
                        {
                            float loX = Mathf.Min(marginMin + desiredR, input.MapSizeX * 0.5f);
                            float hiX = Mathf.Max(loX, maxX - desiredR);
                            float loZ = Mathf.Min(marginMin + desiredR, input.MapSizeZ * 0.5f);
                            float hiZ = Mathf.Max(loZ, maxZ - desiredR);
                            loiterAnchorXZ = new Vector2(
                                Mathf.Clamp(loiterAnchorXZ.x, loX, hiX),
                                Mathf.Clamp(loiterAnchorXZ.y, loZ, hiZ));
                        }
                        else if (enforceLowerBounds)
                        {
                            loiterAnchorXZ = new Vector2(
                                Mathf.Max(marginMin + desiredR, loiterAnchorXZ.x),
                                Mathf.Max(marginMin + desiredR, loiterAnchorXZ.y));
                        }

                        hasLoiterAnchor = true;
                    }

                    float loiterFrac = Mathf.Max(def.minAirspeedFraction, def.loiterSpeedFraction);
                    float targetLoiterSpeedPerTick = maxCruisePerTick * Mathf.Clamp(loiterFrac, 0.25f, 0.95f);
                    throttle01 = Mathf.MoveTowards(throttle01, loiterFrac, spoolStep);
                    currentForwardSpeedCellsPerTick = currentForwardSpeedCellsPerTick < targetLoiterSpeedPerTick
                        ? Mathf.MoveTowards(currentForwardSpeedCellsPerTick, targetLoiterSpeedPerTick, accelPerTick2 * dt)
                        : Mathf.MoveTowards(currentForwardSpeedCellsPerTick, targetLoiterSpeedPerTick, decelPerTick2 * dt);

                    // Tangent orbit guidance around loiterAnchorXZ
                    Vector2 radial = exactWorldPosXZ - loiterAnchorXZ;
                    float rDist = radial.magnitude;
                    float cwSign = activeLoiterOrbitSign >= 0f ? 1f : -1f;
                    float desiredBearing;
                    if (rDist < 0.25f)
                    {
                        desiredBearing = hullHeadingDeg + cwSign * 45f;
                    }
                    else
                    {
                        float radialBearing = BearingDegFromDelta(radial);
                        float tangentBearing = SkeletalKinematics.NormalizeAngle360(radialBearing + cwSign * 90f);
                        float radialErr = (rDist - desiredR) / desiredR;
                        float inwardCorrectionDeg = Mathf.Clamp(radialErr * 42f, -40f, 40f) * cwSign;
                        desiredBearing = SkeletalKinematics.NormalizeAngle360(tangentBearing + inwardCorrectionDeg);
                    }

                    StepCoordinatedBankAndHeading(
                        desiredBearing,
                        CurrentForwardSpeedCellsPerSec,
                        effMaxBankDeg,
                        effMaxRollStep,
                        effMaxTurnRate,
                        dt);
                    pitchVisualOffsetZ = Mathf.Lerp(pitchVisualOffsetZ, -def.cruiseNoseDownPitchOffset * 0.5f, 0.14f * Mathf.Min(1f, dt));
                    lockedHullHeadingDeg = hullHeadingDeg;
                    AdvanceKinematicsPosition(prevHeading, dt);

                    if (hasUpperBounds)
                    {
                        exactWorldPosXZ = new Vector2(
                            Mathf.Clamp(exactWorldPosXZ.x, marginMin, maxX),
                            Mathf.Clamp(exactWorldPosXZ.y, marginMin, maxZ));
                    }
                    else if (enforceLowerBounds)
                    {
                        exactWorldPosXZ = new Vector2(
                            Mathf.Max(marginMin, exactWorldPosXZ.x),
                            Mathf.Max(marginMin, exactWorldPosXZ.y));
                    }
                    return;
                }

                // RotorcraftVTOL / HeavyAirship: Cyclic flare deceleration to stationary hover + optional pedal yaw
                hasLoiterAnchor = false;
                hoverYawLatched = false;
                if (currentForwardSpeedCellsPerTick > 1e-4f)
                {
                    driveState = FlightVehicleDriveState.FlareDecelerating;
                    throttle01 = Mathf.MoveTowards(throttle01, 0.15f, spoolStep * 1.4f);
                    currentForwardSpeedCellsPerTick = Mathf.MoveTowards(currentForwardSpeedCellsPerTick, 0f, decelPerTick2 * dt);
                    currentBankAngleDeg = Mathf.MoveTowards(currentBankAngleDeg, 0f, effMaxRollStep * 1.25f);
                    currentAngularVelDegPerTick = Mathf.MoveTowards(currentAngularVelDegPerTick, 0f, 0.25f * dt);
                    pitchVisualOffsetZ = Mathf.Lerp(pitchVisualOffsetZ, def.flareNoseUpPitchOffset, 0.18f * Mathf.Min(1f, dt));
                    lockedHullHeadingDeg = hullHeadingDeg;
                }
                else
                {
                    currentForwardSpeedCellsPerTick = 0f;
                    throttle01 = Mathf.MoveTowards(throttle01, 0.10f, spoolStep);
                    currentBankAngleDeg = Mathf.MoveTowards(currentBankAngleDeg, 0f, effMaxRollStep * 1.4f);
                    pitchVisualOffsetZ = Mathf.Lerp(pitchVisualOffsetZ, 0f, 0.16f * Mathf.Min(1f, dt));

                    bool wantStationaryAim = input.HasStationaryHullAimRequest &&
                        (!def.lockHullHeadingWhenHovered || def.allowHullTraverseForFixedWeapons);
                    if (wantStationaryAim)
                    {
                        float aimErr = Mathf.DeltaAngle(hullHeadingDeg, input.StationaryHullAimDeg);
                        if (Mathf.Abs(aimErr) > 1.0f)
                        {
                            driveState = FlightVehicleDriveState.HoverYawing;
                            float maxStep = Mathf.Max(0.10f, def.hoverYawRateDegPerTick * asymmetryFactor) * dt;
                            float yawStep = Mathf.Clamp(aimErr * 0.30f, -maxStep, maxStep);
                            hullHeadingDeg = SkeletalKinematics.NormalizeAngle360(hullHeadingDeg + yawStep);
                            lockedHullHeadingDeg = hullHeadingDeg;
                            currentAngularVelDegPerTick = yawStep / dt;
                        }
                        else
                        {
                            driveState = FlightVehicleDriveState.Hovering;
                            currentAngularVelDegPerTick = 0f;
                        }
                    }
                    else
                    {
                        driveState = FlightVehicleDriveState.Hovering;
                        if (def.lockHullHeadingWhenHovered)
                        {
                            hullHeadingDeg = lockedHullHeadingDeg;
                        }
                        currentAngularVelDegPerTick = 0f;
                    }
                }

                AdvanceKinematicsPosition(prevHeading, dt);
                return;
            }

            // Active airborne waypoint navigation
            hasLoiterAnchor = false;
            float headingErr = Mathf.DeltaAngle(hullHeadingDeg, input.LookaheadBearingDeg);
            float absErr = Mathf.Abs(headingErr);
            float speedFrac = maxCruisePerTick > 1e-5f ? Mathf.Clamp01(currentForwardSpeedCellsPerTick / maxCruisePerTick) : 0f;

            // RotorcraftVTOL / HeavyAirship low-speed hover pedal-yaw before accelerating toward astern waypoints
            if (def.SupportsStationaryHover)
            {
                if (!hoverYawLatched &&
                    speedFrac <= def.hoverTransitionSpeedFraction + 0.08f &&
                    absErr >= def.hoverYawEnterAngleDeg)
                {
                    hoverYawLatched = true;
                }
                else if (hoverYawLatched && absErr <= def.hoverYawExitAngleDeg)
                {
                    hoverYawLatched = false;
                }

                if (hoverYawLatched)
                {
                    driveState = FlightVehicleDriveState.HoverYawing;
                    throttle01 = Mathf.MoveTowards(throttle01, 0.18f, spoolStep);
                    currentForwardSpeedCellsPerTick = Mathf.MoveTowards(currentForwardSpeedCellsPerTick, 0f, decelPerTick2 * dt);
                    currentBankAngleDeg = Mathf.MoveTowards(currentBankAngleDeg, 0f, effMaxRollStep * 1.25f);
                    pitchVisualOffsetZ = Mathf.Lerp(pitchVisualOffsetZ, 0f, 0.18f * Mathf.Min(1f, dt));

                    float maxYaw = Mathf.Max(0.10f, def.hoverYawRateDegPerTick * asymmetryFactor) * dt;
                    float yawStep = Mathf.Clamp(headingErr * 0.40f, -maxYaw, maxYaw);
                    hullHeadingDeg = SkeletalKinematics.NormalizeAngle360(hullHeadingDeg + yawStep);
                    lockedHullHeadingDeg = hullHeadingDeg;
                    currentAngularVelDegPerTick = yawStep / dt;
                    AdvanceKinematicsPosition(prevHeading, dt);
                    return;
                }
            }
            else
            {
                hoverYawLatched = false;
            }

            // Coordinated Bank-to-Turn Cruise + Induced Drag + Arrival Flare (for VTOL/Airship)
            StepCoordinatedBankAndHeading(
                input.LookaheadBearingDeg,
                CurrentForwardSpeedCellsPerSec,
                effMaxBankDeg,
                effMaxRollStep,
                effMaxTurnRate,
                dt);

            float bankRatio01 = effMaxBankDeg > 0.1f ? Mathf.Clamp01(Mathf.Abs(currentBankAngleDeg) / effMaxBankDeg) : 0f;
            float inducedDragSpeedMult = 1f - Mathf.Clamp(def.inducedTurnDragFactor, 0f, 0.65f) * (bankRatio01 * bankRatio01);

            float desiredThrottle = 1f;
            bool isFlaring = false;

            if (def.SupportsStationaryHover)
            {
                float stoppingDist = (currentForwardSpeedCellsPerTick * currentForwardSpeedCellsPerTick)
                    / Mathf.Max(1e-6f, 2f * decelPerTick2);
                float flareTriggerDist = Mathf.Max(1.2f, stoppingDist * Mathf.Max(1.0f, def.flareStartDistanceScale));
                if (input.TotalRemainingPathCells <= flareTriggerDist)
                {
                    isFlaring = true;
                    desiredThrottle = 0.12f;
                }
                else if (absErr > 95f)
                {
                    // Slow down into translational envelope when target is sharply astern so turn radius tightens realistically
                    desiredThrottle = Mathf.Max(def.hoverTransitionSpeedFraction + 0.12f, 0.35f);
                }
            }
            else
            {
                // FixedWing: throttle back slightly in tight high-G turns to tighten turn radius, but NEVER below minAirspeedFraction!
                if (absErr > 80f)
                {
                    desiredThrottle = Mathf.Max(def.minAirspeedFraction, 0.68f);
                }
            }

            throttle01 = Mathf.MoveTowards(throttle01, desiredThrottle, spoolStep);

            float minFloorPerTick = def.SupportsStationaryHover
                ? 0f
                : maxCruisePerTick * Mathf.Clamp(def.minAirspeedFraction, 0.15f, 0.90f);

            float targetSpeedPerTick;
            if (isFlaring)
            {
                float vAllow = Mathf.Sqrt(Mathf.Max(0f, 2f * decelPerTick2 * input.TotalRemainingPathCells));
                targetSpeedPerTick = Mathf.Clamp(vAllow, 0f, maxCruisePerTick);
                driveState = FlightVehicleDriveState.FlareDecelerating;
                pitchVisualOffsetZ = Mathf.Lerp(pitchVisualOffsetZ, def.flareNoseUpPitchOffset, 0.18f * Mathf.Min(1f, dt));
            }
            else
            {
                targetSpeedPerTick = Mathf.Max(minFloorPerTick, maxCruisePerTick * throttle01 * inducedDragSpeedMult);
                driveState = Mathf.Abs(currentBankAngleDeg) >= 3.0f
                    ? FlightVehicleDriveState.BankedTurn
                    : FlightVehicleDriveState.ForwardCruise;
                float targetPitch = -def.cruiseNoseDownPitchOffset * speedFrac;
                pitchVisualOffsetZ = Mathf.Lerp(pitchVisualOffsetZ, targetPitch, 0.14f * Mathf.Min(1f, dt));
            }

            if (currentForwardSpeedCellsPerTick < targetSpeedPerTick)
            {
                currentForwardSpeedCellsPerTick = Mathf.MoveTowards(currentForwardSpeedCellsPerTick, targetSpeedPerTick, accelPerTick2 * dt);
            }
            else
            {
                currentForwardSpeedCellsPerTick = Mathf.MoveTowards(currentForwardSpeedCellsPerTick, targetSpeedPerTick, decelPerTick2 * dt);
            }

            if (!def.SupportsStationaryHover && currentForwardSpeedCellsPerTick < minFloorPerTick)
            {
                // Immediate post-takeoff spool up toward stall floor
                currentForwardSpeedCellsPerTick = Mathf.MoveTowards(currentForwardSpeedCellsPerTick, minFloorPerTick, accelPerTick2 * 1.6f * dt);
            }

            lockedHullHeadingDeg = hullHeadingDeg;
            AdvanceKinematicsPosition(prevHeading, dt);
        }

        private void StepCoordinatedBankAndHeading(
            float targetBearingDeg,
            float currentSpeedCellsPerSec,
            float effMaxBankDeg,
            float effMaxRollStep,
            float effMaxTurnRateDegPerTick,
            float dt)
        {
            float headingErr = Mathf.DeltaAngle(hullHeadingDeg, targetBearingDeg);
            // Roll anticipatorily into coordinated bank proportional to heading error, rolling wings-level as error closes
            float desiredBankDeg = Mathf.Clamp(headingErr * 0.65f, -effMaxBankDeg, effMaxBankDeg);
            currentBankAngleDeg = Mathf.MoveTowards(currentBankAngleDeg, desiredBankDeg, effMaxRollStep);

            float turnRate = ComputeCoordinatedTurnRateDegPerTick(
                currentBankAngleDeg,
                Mathf.Max(0.85f, currentSpeedCellsPerSec),
                def.effectiveGravityCellsPerSec2,
                def.minTurnRadiusCells,
                effMaxTurnRateDegPerTick);

            // Prevent overshoot oscillation when nearly aligned and wings nearly level
            float turnStep = turnRate * dt;
            if (Mathf.Abs(headingErr) <= Mathf.Abs(turnStep) + 0.35f && Mathf.Abs(currentBankAngleDeg) <= 4.5f)
            {
                turnStep = headingErr;
            }

            hullHeadingDeg = SkeletalKinematics.NormalizeAngle360(hullHeadingDeg + turnStep);
            currentAngularVelDegPerTick = dt > 1e-5f ? (turnStep / dt) : 0f;
        }

        private void AdvanceKinematicsPosition(float prevHeadingDeg, float dt)
        {
            lastStepHullDeltaDeg = Mathf.DeltaAngle(prevHeadingDeg, hullHeadingDeg);
            lastStepForwardCells = currentForwardSpeedCellsPerTick * dt;
            if (Mathf.Abs(lastStepForwardCells) > 1e-6f)
            {
                float midHeading = prevHeadingDeg + lastStepHullDeltaDeg * 0.5f;
                exactWorldPosXZ += HullForwardFromDeg(midHeading) * lastStepForwardCells;
                hasExactWorldPos = true;
            }
        }

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

            float targetLookahead = Mathf.Clamp(
                2.6f + 0.55f * CurrentForwardSpeedCellsPerSec,
                def.lookaheadMinCells,
                def.lookaheadMaxCells);

            Vector2 prevPt = fromXZ;
            float accumLen = 0f;
            bool foundCarrot = false;
            Vector2 rawCarrotXZ = fromXZ;

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

        public float PrePatherTickAndComputeSpeedScale(Pawn_PathFollower pather, Pawn pawn, CompGiantRig rigComp)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed)
            {
                return 1f;
            }

            RefreshPropulsionHealth(rigComp);

            Vector3 pawnShifted = pawn.Position.ToVector3Shifted();
            Vector2 patherWorldXZ = pather != null && pather.MovingNow
                ? Vector2.Lerp(
                    new Vector2(pawnShifted.x, pawnShifted.z),
                    new Vector2(pather.nextCell.ToVector3Shifted().x, pather.nextCell.ToVector3Shifted().z),
                    Mathf.Clamp01(pather.MovePercentage))
                : new Vector2(pawnShifted.x, pawnShifted.z);

            if (!hasExactWorldPos)
            {
                exactWorldPosXZ = patherWorldXZ;
                loiterAnchorXZ = patherWorldXZ;
                hasExactWorldPos = true;
            }
            else if (driveState != FlightVehicleDriveState.LoiterOrbit)
            {
                // Keep continuous world visual anchor softly tethered to RimWorld grid cell within aircraft turn envelope,
                // smoothly blending back after exiting LoiterOrbit instead of teleporting across the map.
                Vector2 drift = patherWorldXZ - exactWorldPosXZ;
                float driftMag = drift.magnitude;
                if (loiterExitBlendTicks > 0)
                {
                    loiterExitBlendTicks--;
                    if (driftMag > 28f)
                    {
                        exactWorldPosXZ = patherWorldXZ;
                        loiterExitBlendTicks = 0;
                    }
                    else if (driftMag > 0.02f)
                    {
                        float blendStep = Mathf.Max(0.45f, driftMag * 0.16f);
                        exactWorldPosXZ = Vector2.MoveTowards(exactWorldPosXZ, patherWorldXZ, blendStep);
                        if ((patherWorldXZ - exactWorldPosXZ).sqrMagnitude <= 0.16f)
                        {
                            loiterExitBlendTicks = 0;
                        }
                    }
                }
                else if (driftMag > 3.2f)
                {
                    exactWorldPosXZ = patherWorldXZ;
                }
                else if (driftMag > 0.02f)
                {
                    exactWorldPosXZ = Vector2.Lerp(exactWorldPosXZ, patherWorldXZ, pather != null && pather.MovingNow ? 0.24f : 0.35f);
                }
            }

            bool isAirborne = rigComp.FlightLocomotion != null
                ? rigComp.IsCurrentlyAirborne
                : true;

            bool hasMove = SampleLivePathLookahead(
                pawn,
                exactWorldPosXZ,
                out _,
                out float lookaheadBearing,
                out float remCells);

            bool hasStationaryAim = false;
            float stationaryAimDeg = hullHeadingDeg;
            if (!hasMove && rigComp.PartCombat != null &&
                rigComp.PartCombat.TryGetPrimaryCombatLookTargetXZ(out Vector2 combatTargetXZ, out bool rotateHull) &&
                (rotateHull || def.allowHullTraverseForFixedWeapons))
            {
                Vector2 toTarget = combatTargetXZ - exactWorldPosXZ;
                if (toTarget.sqrMagnitude > 0.09f)
                {
                    hasStationaryAim = true;
                    stationaryAimDeg = BearingDegFromDelta(toTarget);
                }
            }

            int nowTick = Current.Game?.tickManager?.TicksGame ?? -1;
            if (nowTick < 0 || nowTick - lastMoveSpeedCacheTick >= 15 || lastMoveSpeedCacheTick < 0)
            {
                cachedMoveSpeedStat = Mathf.Max(0.4f, pawn.GetStatValue(StatDefOf.MoveSpeed));
                lastMoveSpeedCacheTick = nowTick;
            }
            float baseMoveSpeed = cachedMoveSpeedStat;
            if (rigComp.FlightLocomotion != null && isAirborne)
            {
                baseMoveSpeed *= Mathf.Max(0.1f, rigComp.FlightLocomotion.Def.flightSpeedMultiplier);
            }
            // StatDefOf.MoveSpeed already includes propulsionSpeedFactor from StatWorker_GetValueUnfinalized_Crew_Postfix;
            // unscale here so StepKinematicsCore applies propulsionSpeedFactor cleanly once (mirroring VehicleLocomotionRuntime).
            float unscaledCruiseSpeed = propulsionSpeedFactor > 0.01f
                ? baseMoveSpeed / propulsionSpeedFactor
                : baseMoveSpeed;

            var stepInput = new FlightVehicleKinematicsStepInput
            {
                IsAirborne = isAirborne,
                HasMoveOrder = hasMove,
                TotalRemainingPathCells = remCells,
                LookaheadBearingDeg = lookaheadBearing,
                MaxCruiseSpeedCellsPerSec = unscaledCruiseSpeed,
                HasStationaryHullAimRequest = hasStationaryAim,
                StationaryHullAimDeg = stationaryAimDeg,
                DeltaTicks = 1f,
                MapSizeX = pawn.Map != null ? pawn.Map.Size.x : 0f,
                MapSizeZ = pawn.Map != null ? pawn.Map.Size.z : 0f
            };

            StepKinematicsCore(in stepInput);

            if (!hasMove || remCells <= 0.35f)
            {
                hoverYawLatched = false;
                return 1f;
            }
            if (ShouldBlockPatherCellAdvance)
            {
                return 0f;
            }

            // Modulate pather grid progression by forward velocity component aligned with path bearing (zero crabbing)
            float alignCos = Mathf.Max(0.12f, Mathf.Cos(Mathf.DeltaAngle(hullHeadingDeg, lookaheadBearing) * Mathf.Deg2Rad));
            float maxPerTick = Mathf.Max(1e-4f, baseMoveSpeed / TicksPerSecond);
            float ratio = (currentForwardSpeedCellsPerTick / maxPerTick) * alignCos;
            return Mathf.Clamp(ratio, 0.10f, 1.0f);
        }

        public bool TryGetExactWorldPos(Pawn pawn, out Vector3 worldPos)
        {
            worldPos = Vector3.zero;
            if (!hasExactWorldPos || pawn == null || !pawn.Spawned)
            {
                return false;
            }
            float baseY = pawn.Position.ToVector3Shifted().y;
            worldPos = new Vector3(exactWorldPosXZ.x, baseY, exactWorldPosXZ.y + pitchVisualOffsetZ);
            return true;
        }

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
                2.6f + 0.55f * CurrentForwardSpeedCellsPerSec,
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

            if (accum <= 0.04f)
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

        public void StepHeadlessAlongPath(
            ReadOnlySpan<Vector2> waypointsXZ,
            float maxCruiseSpeedCellsPerSec,
            bool isAirborne = true,
            float deltaTicks = 1f)
        {
            bool hasMove = SamplePolylineLookahead(
                waypointsXZ,
                exactWorldPosXZ,
                out _,
                out float bearingDeg,
                out float remCells);

            var input = new FlightVehicleKinematicsStepInput
            {
                IsAirborne = isAirborne,
                HasMoveOrder = hasMove,
                TotalRemainingPathCells = remCells,
                LookaheadBearingDeg = bearingDeg,
                MaxCruiseSpeedCellsPerSec = maxCruiseSpeedCellsPerSec,
                HasStationaryHullAimRequest = false,
                StationaryHullAimDeg = hullHeadingDeg,
                DeltaTicks = deltaTicks
            };
            StepKinematicsCore(in input);
        }
    }
}
