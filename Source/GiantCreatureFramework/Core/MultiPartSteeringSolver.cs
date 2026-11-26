using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Declarative look-ahead / target-gaze steering chain across one or more skeletal bones
    /// (e.g., neck_seg1 -> neck_seg2 -> eldritch_skull) ordered proximal to distal.
    /// Operates strictly via joint angle offsets so child UV sockets stay locked with zero wobble.
    /// </summary>
    public class SteeringLookChainDef
    {
        public string chainId;
        public List<string> boneIds = new List<string>();
        public List<float> weights = new List<float>();

        public float maxChainAngleDeg = 68f;
        public float maxBoneAngleDeg = 28f;
        public float turnSpeedDegPerTick = 5.6f;
        public float responsiveness = 0.24f;
        public float referenceAngleOffsetDeg = 0f;
        public bool full360Traverse = false;

        public float combatTargetWeight = 1.0f;
        public float pathLookAheadWeight = 0.92f;

        // Subtle organic neck articulation waves (pure rotational angle, zero child offset)
        public float idleScanAmplitudeDeg = 0f;
        public float idleScanPeriodTicks = 180f;
        public float locoWaveAmplitudeDeg = 0f;
        public float locoWavePeriodTicks = 60f;
        public float wavePhaseStepRad = 1.10f;

        public float oneShotClipSwayScale = 0f;
        public float oneShotClipAimScale = 1.0f;

        // Vestibulo-collic stride stabilization & proximal->distal cervical cascade ("head-first, neck-follow")
        public float parentSwayCompensation = 0f;
        public float proximalSpeedScale = 0.88f;
        public float distalSpeedScale = 1.15f;
        public float proximalResponsivenessScale = 1.0f;
        public float distalResponsivenessScale = 1.0f;
        public float distalErrorCompensation = 0f;

        // Coupled sagittal neck posture modulation during turns & predator rush
        public float turnSagittalDipDeg = 0f;
        public float rushSagittalExtendDeg = 0f;

        // Episodic predatory POI vigilance gaze in live Idle state (fixate -> saccade glance -> fixate)
        public float idlePoiScanMaxAngleDeg = 0f;
        public int idlePoiHoldMinTicks = 150;
        public int idlePoiHoldMaxTicks = 270;
        public int idlePoiGlanceMinTicks = 45;
        public int idlePoiGlanceMaxTicks = 85;

        [Unsaved]
        public int[] resolvedBoneIndices = Array.Empty<int>();

        [Unsaved]
        public float[] normalizedWeights = Array.Empty<float>();
    }

    /// <summary>
    /// Posterior axial spine / pelvis / tail-base lag follower so turning bends the body into a
    /// biological C/S curve while hindquarters trail and straighten smoothly.
    /// </summary>
    public class SteeringLagBoneDef
    {
        public string boneId;
        public float followSpeedDegPerTick = 1.60f;
        public float followResponsiveness = 0.12f;
        public float forwardMotionAlignGain = 2.2f;
        public float maxBendAngleDeg = 25f;

        [Unsaved]
        public int resolvedBoneIndex = -1;
    }

    /// <summary>
    /// Secondary yaw-rate inertial banking / counter-swing on wings, whip collars, or limbs during turns.
    /// </summary>
    public class SteeringYawBankDef
    {
        public string boneId;
        public float yawRateGain = -2.2f;
        public float maxAngleDeg = 10f;
        public float smoothing = 0.22f;

        [Unsaved]
        public int resolvedBoneIndex = -1;
    }

    public class SteeringConfigDef
    {
        public bool enabled = true;
        public float intensityScale = 1.0f;
        public bool enableNeckIdleAndGaitArticulation = true;
        public float bodyTurnAccelDegPerTick2 = 0.40f;
        public int bodyPathLookAheadCells = 3;
        public float bodyPathLookAheadWeight = 0.35f;
        public int gazePathLookAheadCells = 6;
        public float headLeadGateAngleDeg = 45f;
        public float minBodyTurnScaleWhileHeadLeads = 0.42f;

        public List<SteeringLookChainDef> lookChains = new List<SteeringLookChainDef>();
        public List<SteeringLagBoneDef> lagBones = new List<SteeringLagBoneDef>();
        public List<SteeringYawBankDef> yawBankBones = new List<SteeringYawBankDef>();
        public VehicleLocomotionDef vehicleLocomotion;
        public GiantFlightDef flightLocomotion;
        public FlightVehicleLocomotionDef flightVehicleLocomotion;
        public GiantAquaticDef aquaticLocomotion;
    }

    public struct GiantSteeringContext
    {
        public float DesiredBodyHeadingDeg;
        public float DesiredLookHeadingDeg;
        public bool HasExplicitLookTarget;
        public bool IsCombatTarget;
        public bool IsMoving;
        public float MovedDistanceCells;
        public GiantAnimState AnimState;
        public float ContinuousTicks;
        public bool SteeringEnabled;
        public float IntensityScale;
        public bool EnableNeckIdleAndGaitArticulation;
        public bool HasLookTargetWorldXZ;
        public Vector2 LookTargetWorldXZ;
        public float LastBodyHeadingStepDeg;
        public bool StabilizeTurretsAgainstHullTurn;
        public bool IsRushing;
    }

    public sealed class GiantSteeringRuntime
    {
        private sealed class LookChainRuntimeState
        {
            public SteeringLookChainDef Def;
            public float[] CurrentBoneOffsetsDeg;
            public float[] CurrentBoneVelocitiesDeg;
            public float RestOffsetFromRootDeg;
            public float CurrentSagittalCouplingDeg;
        }

        private sealed class LagBoneRuntimeState
        {
            public SteeringLagBoneDef Def;
            public float LaggedWorldHeadingDeg;
            public float CurrentBendDeg;
            public bool Initialized;
        }

        private sealed class YawBankRuntimeState
        {
            public SteeringYawBankDef Def;
            public float CurrentAngleDeg;
        }

        private readonly GiantRigDef rig;
        private readonly SteeringConfigDef config;
        private readonly bool hasExplicitConfig;
        private readonly List<LookChainRuntimeState> lookStates = new List<LookChainRuntimeState>();
        private readonly List<LagBoneRuntimeState> lagStates = new List<LagBoneRuntimeState>();
        private readonly List<YawBankRuntimeState> bankStates = new List<YawBankRuntimeState>();
        private readonly float[] workingWorldAngles;
        private readonly BoneWorldPose[] workingWorldPoses;

        private float bodyAngularVelocityDegPerTick;
        private float smoothedYawRateDegPerTick;
        private bool anyAngleChangedSignificantly;
        private float primaryLookLeadDeg;
        private float primarySpineBendDeg;

        public SteeringConfigDef Config => config;
        public float BodyAngularVelocityDegPerTick => bodyAngularVelocityDegPerTick;
        public float SmoothedYawRateDegPerTick => smoothedYawRateDegPerTick;
        public float PrimaryLookLeadDeg => primaryLookLeadDeg;
        public float PrimarySpineBendDeg => primarySpineBendDeg;
        public bool HasAnySteeringParts => lookStates.Count > 0 || lagStates.Count > 0 || bankStates.Count > 0;

        public bool IsSettling
        {
            get
            {
                if (Mathf.Abs(bodyAngularVelocityDegPerTick) > 0.05f || anyAngleChangedSignificantly)
                {
                    return true;
                }
                for (int i = 0; i < lagStates.Count; i++)
                {
                    if (Mathf.Abs(lagStates[i].CurrentBendDeg) > 0.8f)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public GiantSteeringRuntime(GiantRigDef rig)
        {
            this.rig = rig ?? throw new ArgumentNullException(nameof(rig));
            BoneNodeDef[] nodes = rig.SortedNodes;
            hasExplicitConfig = rig.steering != null;
            config = rig.steering ?? new SteeringConfigDef();

            int nodeCount = nodes.Length;
            workingWorldAngles = new float[nodeCount];
            workingWorldPoses = new BoneWorldPose[nodeCount];

            var coveredByExplicitLook = new HashSet<int>();
            if (config.lookChains != null)
            {
                for (int c = 0; c < config.lookChains.Count; c++)
                {
                    SteeringLookChainDef chain = config.lookChains[c];
                    if (chain?.resolvedBoneIndices == null || chain.resolvedBoneIndices.Length == 0)
                    {
                        continue;
                    }
                    bool anyValid = false;
                    for (int k = 0; k < chain.resolvedBoneIndices.Length; k++)
                    {
                        int bIdx = chain.resolvedBoneIndices[k];
                        if (bIdx >= 0 && !nodes[bIdx].isRigid)
                        {
                            coveredByExplicitLook.Add(bIdx);
                            anyValid = true;
                        }
                    }
                    if (anyValid)
                    {
                        int distalIdx = chain.resolvedBoneIndices[chain.resolvedBoneIndices.Length - 1];
                        lookStates.Add(new LookChainRuntimeState
                        {
                            Def = chain,
                            CurrentBoneOffsetsDeg = new float[chain.resolvedBoneIndices.Length],
                            CurrentBoneVelocitiesDeg = new float[chain.resolvedBoneIndices.Length],
                            RestOffsetFromRootDeg = ComputeRestOffsetFromRoot(nodes, distalIdx),
                            CurrentSagittalCouplingDeg = 0f
                        });
                    }
                }
            }

            // Backward-compatible fallback for any BoneNodeDef using aimAtTargetWeight without explicit steering block
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef bDef = nodes[i];
                if (!bDef.isRigid && bDef.aimAtTargetWeight > 0.001f && !coveredByExplicitLook.Contains(i))
                {
                    var autoDef = new SteeringLookChainDef
                    {
                        chainId = $"auto_aim_{bDef.nodeId}",
                        boneIds = new List<string> { bDef.nodeId },
                        weights = new List<float> { 1f },
                        maxChainAngleDeg = bDef.aimMaxAngle,
                        maxBoneAngleDeg = bDef.aimMaxAngle,
                        turnSpeedDegPerTick = 8f,
                        responsiveness = 0.35f,
                        referenceAngleOffsetDeg = bDef.aimReferenceAngleOffset,
                        combatTargetWeight = bDef.aimAtTargetWeight,
                        pathLookAheadWeight = 0f,
                        idleScanAmplitudeDeg = 0f,
                        locoWaveAmplitudeDeg = 0f,
                        oneShotClipAimScale = 1.0f,
                        resolvedBoneIndices = new[] { i },
                        normalizedWeights = new[] { 1f }
                    };
                    lookStates.Add(new LookChainRuntimeState
                    {
                        Def = autoDef,
                        CurrentBoneOffsetsDeg = new float[1],
                        CurrentBoneVelocitiesDeg = new float[1],
                        RestOffsetFromRootDeg = ComputeRestOffsetFromRoot(nodes, i),
                        CurrentSagittalCouplingDeg = 0f
                    });
                }
            }

            if (config.lagBones != null)
            {
                for (int i = 0; i < config.lagBones.Count; i++)
                {
                    SteeringLagBoneDef lag = config.lagBones[i];
                    if (lag != null && lag.resolvedBoneIndex >= 0 && !nodes[lag.resolvedBoneIndex].isRigid)
                    {
                        lagStates.Add(new LagBoneRuntimeState
                        {
                            Def = lag,
                            LaggedWorldHeadingDeg = 0f,
                            CurrentBendDeg = 0f,
                            Initialized = false
                        });
                    }
                }
            }

            if (config.yawBankBones != null)
            {
                for (int i = 0; i < config.yawBankBones.Count; i++)
                {
                    SteeringYawBankDef bank = config.yawBankBones[i];
                    if (bank != null && bank.resolvedBoneIndex >= 0 && !nodes[bank.resolvedBoneIndex].isRigid)
                    {
                        bankStates.Add(new YawBankRuntimeState
                        {
                            Def = bank,
                            CurrentAngleDeg = 0f
                        });
                    }
                }
            }
        }

        public void Reset(float initialHeadingDeg)
        {
            bodyAngularVelocityDegPerTick = 0f;
            smoothedYawRateDegPerTick = 0f;
            anyAngleChangedSignificantly = false;
            primaryLookLeadDeg = 0f;
            primarySpineBendDeg = 0f;

            for (int c = 0; c < lookStates.Count; c++)
            {
                Array.Clear(lookStates[c].CurrentBoneOffsetsDeg, 0, lookStates[c].CurrentBoneOffsetsDeg.Length);
                Array.Clear(lookStates[c].CurrentBoneVelocitiesDeg, 0, lookStates[c].CurrentBoneVelocitiesDeg.Length);
                lookStates[c].CurrentSagittalCouplingDeg = 0f;
            }
            for (int i = 0; i < bankStates.Count; i++)
            {
                bankStates[i].CurrentAngleDeg = 0f;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                int pIdx = nodes[i].parentRuntimeIndex;
                float parentAngle = pIdx >= 0 ? workingWorldAngles[pIdx] : initialHeadingDeg;
                workingWorldAngles[i] = SkeletalKinematics.NormalizeAngle360(parentAngle + nodes[i].baseAngle);
            }

            for (int i = 0; i < lagStates.Count; i++)
            {
                int idx = lagStates[i].Def.resolvedBoneIndex;
                lagStates[i].LaggedWorldHeadingDeg = idx >= 0 && idx < workingWorldAngles.Length
                    ? workingWorldAngles[idx]
                    : initialHeadingDeg;
                lagStates[i].CurrentBendDeg = 0f;
                lagStates[i].Initialized = true;
            }
        }

        /// <summary>
        /// Locks posterior axial lag bones (pelvis/lumbar/tail) and yaw-bank bones to zero bend while preserving
        /// active neck/head lookChain offsets (used during stationary head-only daily activities).
        /// </summary>
        public void ResetLagAndBankOnly(float rootHeadingDeg)
        {
            bodyAngularVelocityDegPerTick = 0f;
            smoothedYawRateDegPerTick = 0f;
            primarySpineBendDeg = 0f;

            for (int i = 0; i < bankStates.Count; i++)
            {
                bankStates[i].CurrentAngleDeg = 0f;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                int pIdx = nodes[i].parentRuntimeIndex;
                float parentAngle = pIdx >= 0 ? workingWorldAngles[pIdx] : rootHeadingDeg;
                workingWorldAngles[i] = SkeletalKinematics.NormalizeAngle360(parentAngle + nodes[i].baseAngle);
            }

            for (int i = 0; i < lagStates.Count; i++)
            {
                int idx = lagStates[i].Def.resolvedBoneIndex;
                lagStates[i].LaggedWorldHeadingDeg = idx >= 0 && idx < workingWorldAngles.Length
                    ? workingWorldAngles[idx]
                    : rootHeadingDeg;
                lagStates[i].CurrentBendDeg = 0f;
                lagStates[i].Initialized = true;
            }
        }

        public float StepBodyHeading(
            float currentHeadingDeg,
            float maxTurnSpeedDegPerTick,
            float deltaTicks,
            in GiantSteeringContext ctx)
        {
            currentHeadingDeg = SkeletalKinematics.NormalizeAngle360(currentHeadingDeg);
            if (deltaTicks <= 0f || maxTurnSpeedDegPerTick <= 0f)
            {
                bodyAngularVelocityDegPerTick = 0f;
                smoothedYawRateDegPerTick = Mathf.Lerp(smoothedYawRateDegPerTick, 0f, 0.25f);
                return currentHeadingDeg;
            }

            float desiredHeadingDeg = SkeletalKinematics.NormalizeAngle360(ctx.DesiredBodyHeadingDeg);
            float diff = Mathf.DeltaAngle(currentHeadingDeg, desiredHeadingDeg);
            float absDiff = Mathf.Abs(diff);

            if (!ctx.SteeringEnabled || !hasExplicitConfig || !config.enabled || config.bodyTurnAccelDegPerTick2 <= 0f)
            {
                float maxStep = maxTurnSpeedDegPerTick * deltaTicks;
                float step = absDiff <= maxStep ? diff : Mathf.Sign(diff) * maxStep;
                float next = SkeletalKinematics.NormalizeAngle360(currentHeadingDeg + step);
                bodyAngularVelocityDegPerTick = step / deltaTicks;
                smoothedYawRateDegPerTick = Mathf.Lerp(smoothedYawRateDegPerTick, bodyAngularVelocityDegPerTick, 0.35f);
                return next;
            }

            // Head-lead gating: during sharp turns, allow the head/neck chain to start sweeping in before
            // the heavy thorax hits 100% yaw rate (never dropping below minBodyTurnScaleWhileHeadLeads).
            float effectiveMaxSpeed = maxTurnSpeedDegPerTick;
            if (absDiff > config.headLeadGateAngleDeg && lookStates.Count > 0)
            {
                LookChainRuntimeState primaryLook = lookStates[0];
                float lookDeltaFromBody = Mathf.DeltaAngle(currentHeadingDeg, ctx.DesiredLookHeadingDeg);
                if (Mathf.Sign(lookDeltaFromBody) == Mathf.Sign(diff) && Mathf.Abs(lookDeltaFromBody) > 1f)
                {
                    float refLead = Mathf.Max(12f, Mathf.Min(Mathf.Abs(lookDeltaFromBody), primaryLook.Def.maxChainAngleDeg * 0.65f));
                    float sameSideLead = Mathf.Sign(primaryLookLeadDeg) == Mathf.Sign(diff)
                        ? Mathf.Abs(primaryLookLeadDeg)
                        : 0f;
                    float leadProgress = Mathf.Clamp01(sameSideLead / refLead);
                    float gateScale = Mathf.Lerp(Mathf.Clamp(config.minBodyTurnScaleWhileHeadLeads, 0.25f, 1f), 1f, leadProgress);
                    effectiveMaxSpeed *= gateScale;
                }
            }

            float accel = Mathf.Max(0.05f, config.bodyTurnAccelDegPerTick2);
            if (absDiff < 0.05f && Mathf.Abs(bodyAngularVelocityDegPerTick) <= accel * deltaTicks)
            {
                bodyAngularVelocityDegPerTick = 0f;
                smoothedYawRateDegPerTick = Mathf.Lerp(smoothedYawRateDegPerTick, 0f, 0.28f);
                return desiredHeadingDeg;
            }

            // Braking-curve target speed avoids rigid snap-stop and prevents overshoot
            float brakingSpeed = Mathf.Sqrt(2f * accel * absDiff);
            float targetSpeed = Mathf.Min(effectiveMaxSpeed, brakingSpeed) * Mathf.Sign(diff);
            float maxDeltaVel = accel * deltaTicks;
            float dv = targetSpeed - bodyAngularVelocityDegPerTick;
            if (Mathf.Abs(dv) > maxDeltaVel)
            {
                bodyAngularVelocityDegPerTick += Mathf.Sign(dv) * maxDeltaVel;
            }
            else
            {
                bodyAngularVelocityDegPerTick = targetSpeed;
            }

            float angleStep = bodyAngularVelocityDegPerTick * deltaTicks;
            if (Mathf.Abs(angleStep) >= absDiff && Mathf.Sign(angleStep) == Mathf.Sign(diff))
            {
                angleStep = diff;
                bodyAngularVelocityDegPerTick = angleStep / deltaTicks;
            }

            smoothedYawRateDegPerTick = Mathf.Lerp(smoothedYawRateDegPerTick, bodyAngularVelocityDegPerTick, 0.32f);
            return SkeletalKinematics.NormalizeAngle360(currentHeadingDeg + angleStep);
        }

        private void RefreshWorkingWorldState(
            GiantRigDef targetRig,
            BoneNodeDef[] nodes,
            Vector2 rootWorldXZ,
            float rootHeadingDeg,
            ReadOnlySpan<BoneLocalPose> localPoses,
            ReadOnlySpan<bool> missingMask,
            bool solveWorldPositions)
        {
            if (solveWorldPositions && workingWorldPoses.Length >= nodes.Length)
            {
                SkeletalKinematics.SolveWorldPoses(
                    targetRig,
                    rootWorldXZ,
                    rootHeadingDeg,
                    localPoses,
                    missingMask,
                    workingWorldPoses);
                for (int i = 0; i < nodes.Length; i++)
                {
                    workingWorldAngles[i] = workingWorldPoses[i].worldAngleDeg;
                }
            }
            else
            {
                ComputeBaseWorldAngles(nodes, rootHeadingDeg, localPoses);
            }
        }

        public void ApplyBoneSteering(
            GiantRigDef targetRig,
            float rootHeadingDeg,
            Span<BoneLocalPose> localPoses,
            ReadOnlySpan<bool> missingMask,
            float deltaTicks,
            in GiantSteeringContext ctx,
            Vector2 rootWorldXZ = default,
            GiantPartCombatRuntime partCombat = null)
        {
            if (targetRig == null || !HasAnySteeringParts)
            {
                return;
            }

            BoneNodeDef[] nodes = targetRig.SortedNodes;
            int nodeCount = nodes.Length;
            if (workingWorldAngles.Length != nodeCount || localPoses.Length < nodeCount)
            {
                return;
            }

            anyAngleChangedSignificantly = false;
            primaryLookLeadDeg = 0f;
            primarySpineBendDeg = 0f;

            bool solveWorldPositions = partCombat != null || ctx.HasLookTargetWorldXZ;

            // 1. Compute base world angle (and world pivot positions when per-turret aiming is active)
            RefreshWorkingWorldState(targetRig, nodes, rootWorldXZ, rootHeadingDeg, localPoses, missingMask, solveWorldPositions);

            float intensity = ctx.SteeringEnabled ? Mathf.Clamp(ctx.IntensityScale, 0f, 2f) : 0f;
            bool allowDynamic = intensity > 0.001f && ctx.AnimState != GiantAnimState.Downed;
            bool isOneShot = ctx.AnimState == GiantAnimState.AttackOnce || ctx.AnimState == GiantAnimState.ManualPreview;

            // 2. Apply look-ahead / target-gaze / living neck articulation chains (proximal -> distal)
            for (int c = 0; c < lookStates.Count; c++)
            {
                LookChainRuntimeState st = lookStates[c];
                SteeringLookChainDef def = st.Def;
                int[] boneIndices = def.resolvedBoneIndices;
                float[] weights = def.normalizedWeights;
                if (boneIndices == null || boneIndices.Length == 0)
                {
                    continue;
                }

                int distalIdx = boneIndices[boneIndices.Length - 1];
                Vector2 chainPivotXZ = (solveWorldPositions && (uint)distalIdx < (uint)nodeCount)
                    ? workingWorldPoses[distalIdx].jointWorldXZ
                    : rootWorldXZ;

                float targetLookHeadingDeg = ctx.DesiredLookHeadingDeg;
                bool hasChainCombatTarget = false;
                if (allowDynamic && partCombat != null &&
                    partCombat.TryResolveLookChainAimHeading(def, chainPivotXZ, missingMask, out float chainAimHeadingDeg))
                {
                    targetLookHeadingDeg = chainAimHeadingDeg;
                    hasChainCombatTarget = true;
                }
                else if (allowDynamic && ctx.HasLookTargetWorldXZ)
                {
                    Vector2 dLook = ctx.LookTargetWorldXZ - chainPivotXZ;
                    if (dLook.sqrMagnitude > 0.04f)
                    {
                        targetLookHeadingDeg = SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(dLook.x, dLook.y) * Mathf.Rad2Deg);
                    }
                }
                bool effectiveIsCombatTarget = hasChainCombatTarget || ctx.IsCombatTarget;

                float trackW = 0f;
                if (allowDynamic)
                {
                    if (effectiveIsCombatTarget)
                    {
                        trackW = def.combatTargetWeight;
                    }
                    else if (ctx.HasExplicitLookTarget || ctx.IsMoving)
                    {
                        trackW = def.pathLookAheadWeight;
                    }
                    if (isOneShot)
                    {
                        trackW *= def.oneShotClipAimScale;
                    }
                }

                // Anchor reference frame at the parent of the first bone in the chain (e.g., thorax forward axis)
                int firstIdx = boneIndices[0];
                if (ctx.StabilizeTurretsAgainstHullTurn &&
                    Mathf.Abs(ctx.LastBodyHeadingStepDeg) > 1e-5f &&
                    st.CurrentBoneOffsetsDeg != null &&
                    st.CurrentBoneOffsetsDeg.Length > 0 &&
                    firstIdx >= 0 &&
                    firstIdx < nodeCount &&
                    targetRig.IsBoneInTurretSubtree(firstIdx, out _))
                {
                    // World-space gyro stabilization strictly scoped to turret parts (<isTurret>true</isTurret>):
                    // counter-rotates local turret ring angle by -deltaHullDeg so hull pivot/steering never drags gun off target.
                    if (def.full360Traverse)
                    {
                        st.CurrentBoneOffsetsDeg[0] = Mathf.DeltaAngle(0f, st.CurrentBoneOffsetsDeg[0] - ctx.LastBodyHeadingStepDeg);
                    }
                    else
                    {
                        float maxTurretStop = def.maxBoneAngleDeg * Mathf.Max(1f, intensity);
                        st.CurrentBoneOffsetsDeg[0] = Mathf.Clamp(st.CurrentBoneOffsetsDeg[0] - ctx.LastBodyHeadingStepDeg, -maxTurretStop, maxTurretStop);
                    }
                }
                float refWorldAngleDeg = rootHeadingDeg + def.referenceAngleOffsetDeg;
                float swayCompTotalDeg = 0f;
                if (def.parentSwayCompensation > 0.001f && !isOneShot && distalIdx >= 0 && distalIdx < nodeCount)
                {
                    float restDistalWorldDeg = rootHeadingDeg + st.RestOffsetFromRootDeg + def.referenceAngleOffsetDeg;
                    float unsteeredDistalWorldDeg = workingWorldAngles[distalIdx] + def.referenceAngleOffsetDeg;
                    refWorldAngleDeg = restDistalWorldDeg;
                    if (allowDynamic && (ctx.IsMoving || ctx.HasExplicitLookTarget || effectiveIsCombatTarget))
                    {
                        float unsteeredSwayDeg = Mathf.DeltaAngle(restDistalWorldDeg, unsteeredDistalWorldDeg);
                        swayCompTotalDeg = -unsteeredSwayDeg * Mathf.Clamp01(def.parentSwayCompensation) * intensity;
                    }
                }
                else if (firstIdx >= 0 && firstIdx < nodeCount)
                {
                    int firstParent = nodes[firstIdx].parentRuntimeIndex;
                    float parentWorld = firstParent >= 0 ? workingWorldAngles[firstParent] : rootHeadingDeg;
                    refWorldAngleDeg = parentWorld + nodes[firstIdx].baseAngle + def.referenceAngleOffsetDeg;
                }

                float rawLookDelta = Mathf.DeltaAngle(refWorldAngleDeg, targetLookHeadingDeg);
                float bodyTurnDelta = Mathf.DeltaAngle(rootHeadingDeg, ctx.DesiredBodyHeadingDeg);
                if (!def.full360Traverse && def.maxChainAngleDeg < 90f && Mathf.Abs(rawLookDelta) > 145f)
                {
                    if (Mathf.Abs(bodyTurnDelta) > 8f)
                    {
                        // Lock U-turn side to body turn direction so thorax gait yaw cannot flip +/-180 deg sign
                        rawLookDelta = Mathf.Abs(rawLookDelta) * Mathf.Sign(bodyTurnDelta);
                    }
                    else
                    {
                        // Smoothly center head when target is directly astern of a stationary body
                        rawLookDelta *= 1f - SmoothRamp01(135f, 170f, Mathf.Abs(rawLookDelta));
                    }
                }

                float desiredDelta = def.full360Traverse
                    ? (rawLookDelta * trackW)
                    : (rawLookDelta * trackW * intensity);
                if (!def.full360Traverse || def.maxChainAngleDeg < 179.9f)
                {
                    float maxChain = def.maxChainAngleDeg * Mathf.Max(1f, intensity);
                    desiredDelta = Mathf.Clamp(desiredDelta, -maxChain, maxChain);
                }
                else
                {
                    desiredDelta = Mathf.DeltaAngle(0f, desiredDelta);
                }

                // Coupled sagittal neck dip during sharp turns & forward neck extension during predator rush
                if (allowDynamic && !isOneShot && targetRig.useCoupledSagittalKinematics
                    && (Mathf.Abs(def.turnSagittalDipDeg) > 0.01f || Mathf.Abs(def.rushSagittalExtendDeg) > 0.01f))
                {
                    float targetSag = 0f;
                    float turnFactor = Mathf.Clamp01(Mathf.Abs(smoothedYawRateDegPerTick) / 2.6f);
                    targetSag += def.turnSagittalDipDeg * turnFactor * intensity;
                    if (ctx.IsRushing && ctx.IsMoving)
                    {
                        targetSag += def.rushSagittalExtendDeg * intensity;
                    }
                    float sagAlpha = deltaTicks > 0f ? 1f - Mathf.Pow(0.82f, deltaTicks) : 1f;
                    st.CurrentSagittalCouplingDeg = Mathf.Lerp(st.CurrentSagittalCouplingDeg, targetSag, sagAlpha);
                }
                else
                {
                    float sagDecay = deltaTicks > 0f ? 1f - Mathf.Pow(0.80f, deltaTicks) : 1f;
                    st.CurrentSagittalCouplingDeg = Mathf.Lerp(st.CurrentSagittalCouplingDeg, 0f, sagDecay);
                }

                int m = boneIndices.Length;
                float prevChainSumDeg = 0f;
                for (int k = 0; k < m; k++)
                {
                    prevChainSumDeg += st.CurrentBoneOffsetsDeg[k];
                }

                float chainSumDeg = 0f;
                float expectedCumWeight = 0f;
                float expectedCumSwayWeight = 0f;
                float totalTargetDelta = desiredDelta + swayCompTotalDeg;

                for (int k = 0; k < m; k++)
                {
                    int bIdx = boneIndices[k];
                    bool visible = bIdx >= 0 && bIdx < nodeCount && IsBoneChainIntact(nodes, bIdx, missingMask);
                    float w = weights != null && k < weights.Length ? weights[k] : 1f / m;
                    float swayW = m > 1 ? (2f * (k + 1f)) / (m * (m + 1f)) : 1f;
                    float targetBoneAngle = 0f;

                    if (visible && allowDynamic)
                    {
                        targetBoneAngle = desiredDelta * w + swayCompTotalDeg * swayW;

                        // Head-first, neck-follow feed-forward: distal cervical & skull joints absorb unreached proximal lag
                        if (!def.full360Traverse && def.distalErrorCompensation > 0.001f && k > 0 && Mathf.Abs(totalTargetDelta) > 0.25f)
                        {
                            float expectedBeforeK = desiredDelta * expectedCumWeight + swayCompTotalDeg * expectedCumSwayWeight;
                            float proximalLag = expectedBeforeK - chainSumDeg;
                            float compShare = (k == m - 1) ? 1.0f : 0.45f;
                            targetBoneAngle += proximalLag * Mathf.Clamp01(def.distalErrorCompensation) * compShare;

                            if (totalTargetDelta > 0f)
                            {
                                float ceiling = Mathf.Max(desiredDelta * w + swayCompTotalDeg * swayW, totalTargetDelta - chainSumDeg);
                                targetBoneAngle = Mathf.Min(targetBoneAngle, ceiling);
                            }
                            else if (totalTargetDelta < 0f)
                            {
                                float floor = Mathf.Min(desiredDelta * w + swayCompTotalDeg * swayW, totalTargetDelta - chainSumDeg);
                                targetBoneAngle = Mathf.Max(targetBoneAngle, floor);
                            }
                        }

                        // Suppress idle/loco sine wagging whenever focused on a target or explicit look heading
                        if (ctx.EnableNeckIdleAndGaitArticulation && !effectiveIsCombatTarget && !ctx.HasExplicitLookTarget)
                        {
                            float amp = 0f;
                            float period = 60f;
                            if (ctx.AnimState == GiantAnimState.Idle)
                            {
                                amp = def.idleScanAmplitudeDeg * w;
                                period = Mathf.Max(10f, def.idleScanPeriodTicks);
                            }
                            else if ((ctx.AnimState == GiantAnimState.Walk || ctx.AnimState == GiantAnimState.Run)
                                && ctx.MovedDistanceCells > 1e-4f)
                            {
                                amp = def.locoWaveAmplitudeDeg * w * Mathf.Clamp01(ctx.MovedDistanceCells / 0.04f);
                                period = Mathf.Max(10f, def.locoWavePeriodTicks);
                            }
                            else if (isOneShot && ctx.MovedDistanceCells > 1e-4f)
                            {
                                amp = def.locoWaveAmplitudeDeg * def.oneShotClipSwayScale * w * Mathf.Clamp01(ctx.MovedDistanceCells / 0.04f);
                                period = Mathf.Max(10f, def.locoWavePeriodTicks);
                            }

                            if (amp > 0.01f)
                            {
                                float phase = (2f * Mathf.PI * ctx.ContinuousTicks / period) - (k * def.wavePhaseStepRad);
                                targetBoneAngle += Mathf.Sin(phase) * amp * intensity;
                            }
                        }

                        if (!def.full360Traverse || def.maxBoneAngleDeg < 179.9f)
                        {
                            float maxBone = def.maxBoneAngleDeg * Mathf.Max(1f, intensity);
                            targetBoneAngle = Mathf.Clamp(targetBoneAngle, -maxBone, maxBone);
                        }
                        else
                        {
                            targetBoneAngle = Mathf.DeltaAngle(0f, targetBoneAngle);
                        }
                    }

                    // Scale chain-total speed & responsiveness along proximal -> distal gradient ("head snaps first, heavy neck base follows")
                    float tNorm = m > 1 ? k / (float)(m - 1) : 1f;
                    float weightShare = m > 1 ? Mathf.Clamp(w, 0.15f, 1f) : 1f;
                    float distalSpeedBias = m > 1 ? Mathf.Lerp(def.proximalSpeedScale, def.distalSpeedScale, tNorm) : 1f;
                    float distalRespBias = m > 1 ? Mathf.Lerp(def.proximalResponsivenessScale, def.distalResponsivenessScale, tNorm) : 1f;
                    float boneMaxSpeed = Mathf.Max(0.25f, def.turnSpeedDegPerTick * weightShare * distalSpeedBias);
                    float boneResponsiveness = Mathf.Clamp(def.responsiveness * distalRespBias, 0.06f, 0.92f);
                    float prev = st.CurrentBoneOffsetsDeg[k];
                    float next = StepDampedBoneAngle(
                        prev,
                        ref st.CurrentBoneVelocitiesDeg[k],
                        targetBoneAngle,
                        boneMaxSpeed,
                        boneResponsiveness,
                        deltaTicks,
                        visible && allowDynamic,
                        wrap360: def.full360Traverse);

                    // Guarantee monotonic non-overshoot (and zero mid-cascade backward gaze slip) on total chain gaze angle as proximal vertebrae catch up
                    if (!def.full360Traverse && def.distalErrorCompensation > 0.001f && k == m - 1 && visible && allowDynamic && deltaTicks > 0f)
                    {
                        float maxBone = def.maxBoneAngleDeg * Mathf.Max(1f, intensity);
                        if (totalTargetDelta > 0.1f && prevChainSumDeg <= totalTargetDelta + 0.02f)
                        {
                            if ((chainSumDeg + next) > totalTargetDelta)
                            {
                                next = Mathf.Clamp(totalTargetDelta - chainSumDeg, -maxBone, maxBone);
                                st.CurrentBoneVelocitiesDeg[k] = (next - prev) / deltaTicks;
                            }
                            else if (prevChainSumDeg > 0.05f && Mathf.Abs(swayCompTotalDeg) <= 0.25f && (chainSumDeg + next) < prevChainSumDeg)
                            {
                                next = Mathf.Clamp(prevChainSumDeg - chainSumDeg, -maxBone, maxBone);
                                st.CurrentBoneVelocitiesDeg[k] = (next - prev) / deltaTicks;
                            }
                        }
                        else if (totalTargetDelta < -0.1f && prevChainSumDeg >= totalTargetDelta - 0.02f)
                        {
                            if ((chainSumDeg + next) < totalTargetDelta)
                            {
                                next = Mathf.Clamp(totalTargetDelta - chainSumDeg, -maxBone, maxBone);
                                st.CurrentBoneVelocitiesDeg[k] = (next - prev) / deltaTicks;
                            }
                            else if (prevChainSumDeg < -0.05f && Mathf.Abs(swayCompTotalDeg) <= 0.25f && (chainSumDeg + next) > prevChainSumDeg)
                            {
                                next = Mathf.Clamp(prevChainSumDeg - chainSumDeg, -maxBone, maxBone);
                                st.CurrentBoneVelocitiesDeg[k] = (next - prev) / deltaTicks;
                            }
                        }
                    }

                    if (Mathf.Abs(Mathf.DeltaAngle(prev, next)) > 0.12f)
                    {
                        anyAngleChangedSignificantly = true;
                    }

                    st.CurrentBoneOffsetsDeg[k] = next;
                    if (visible)
                    {
                        if (Mathf.Abs(next) > 1e-4f)
                        {
                            localPoses[bIdx].angle += next;
                            chainSumDeg += next;
                        }
                        if (Mathf.Abs(st.CurrentSagittalCouplingDeg) > 1e-3f)
                        {
                            localPoses[bIdx].sagittalPitch += st.CurrentSagittalCouplingDeg * w;
                        }
                    }

                    expectedCumWeight += w;
                    expectedCumSwayWeight += swayW;
                }

                if (c == 0)
                {
                    primaryLookLeadDeg = chainSumDeg;
                }

                // Refresh base world angles & positions before lag/bank pass or subsequent chains
                RefreshWorkingWorldState(targetRig, nodes, rootWorldXZ, rootHeadingDeg, localPoses, missingMask, solveWorldPositions);
            }

            // 3. Apply posterior axial lag bones (pelvis / haunch / tail base) to create spinal turn curvature
            for (int i = 0; i < lagStates.Count; i++)
            {
                LagBoneRuntimeState st = lagStates[i];
                SteeringLagBoneDef def = st.Def;
                int bIdx = def.resolvedBoneIndex;
                if (bIdx < 0 || bIdx >= nodeCount)
                {
                    continue;
                }
                bool visible = IsBoneChainIntact(nodes, bIdx, missingMask);

                int pIdx = nodes[bIdx].parentRuntimeIndex;
                float parentWorldDeg = pIdx >= 0 ? workingWorldAngles[pIdx] : rootHeadingDeg;
                float desiredWorldDeg = SkeletalKinematics.NormalizeAngle360(parentWorldDeg + nodes[bIdx].baseAngle);

                if (!visible || !allowDynamic)
                {
                    st.LaggedWorldHeadingDeg = desiredWorldDeg;
                    st.CurrentBendDeg = 0f;
                    st.Initialized = true;
                    continue;
                }

                if (!st.Initialized)
                {
                    st.LaggedWorldHeadingDeg = desiredWorldDeg;
                    st.CurrentBendDeg = 0f;
                    st.Initialized = true;
                    continue;
                }

                if (deltaTicks > 0f)
                {
                    float err = Mathf.DeltaAngle(st.LaggedWorldHeadingDeg, desiredWorldDeg);
                    float maxStep = def.followSpeedDegPerTick * deltaTicks;
                    float springStep = err * Mathf.Clamp01(def.followResponsiveness) * deltaTicks;
                    float followStep = Mathf.Clamp(springStep, -maxStep, maxStep);

                    // Moving forward naturally aligns trailing hindquarters along the path arc
                    float motionAlign = ctx.MovedDistanceCells > 1e-4f
                        ? err * Mathf.Clamp01(ctx.MovedDistanceCells * def.forwardMotionAlignGain * 0.18f)
                        : 0f;

                    float totalCatchup = followStep + motionAlign;
                    if (Mathf.Abs(totalCatchup) > Mathf.Abs(err) && Mathf.Sign(totalCatchup) == Mathf.Sign(err))
                    {
                        totalCatchup = err;
                    }

                    st.LaggedWorldHeadingDeg = SkeletalKinematics.NormalizeAngle360(st.LaggedWorldHeadingDeg + totalCatchup);

                    float maxAllowedBend = def.maxBendAngleDeg;
                    float curFromDesired = Mathf.DeltaAngle(desiredWorldDeg, st.LaggedWorldHeadingDeg);
                    if (Mathf.Abs(curFromDesired) > maxAllowedBend)
                    {
                        st.LaggedWorldHeadingDeg = SkeletalKinematics.NormalizeAngle360(
                            desiredWorldDeg + Mathf.Sign(curFromDesired) * maxAllowedBend);
                    }
                }

                float rawBend = Mathf.DeltaAngle(desiredWorldDeg, st.LaggedWorldHeadingDeg) * intensity;
                float maxOutBend = def.maxBendAngleDeg * Mathf.Max(1f, intensity);
                float nextBend = Mathf.Clamp(rawBend, -maxOutBend, maxOutBend);

                if (Mathf.Abs(nextBend - st.CurrentBendDeg) > 0.12f)
                {
                    anyAngleChangedSignificantly = true;
                }

                st.CurrentBendDeg = nextBend;
                localPoses[bIdx].angle += nextBend;

                if (i == 0)
                {
                    primarySpineBendDeg = nextBend;
                }

                ComputeBaseWorldAngles(nodes, rootHeadingDeg, localPoses);
            }

            // 4. Apply secondary yaw-rate inertial banking on wings / collars / secondary limbs
            for (int i = 0; i < bankStates.Count; i++)
            {
                YawBankRuntimeState st = bankStates[i];
                SteeringYawBankDef def = st.Def;
                int bIdx = def.resolvedBoneIndex;
                bool visible = bIdx >= 0 && bIdx < nodeCount && IsBoneChainIntact(nodes, bIdx, missingMask);

                float targetBank = 0f;
                if (visible && allowDynamic)
                {
                    targetBank = smoothedYawRateDegPerTick * def.yawRateGain * intensity;
                    float maxBank = def.maxAngleDeg * Mathf.Max(1f, intensity);
                    targetBank = Mathf.Clamp(targetBank, -maxBank, maxBank);
                }

                float prev = st.CurrentAngleDeg;
                float alpha = deltaTicks > 0f
                    ? 1f - Mathf.Pow(1f - Mathf.Clamp01(def.smoothing), deltaTicks)
                    : (visible && allowDynamic ? 1f : 0f);
                float next = Mathf.Lerp(prev, targetBank, alpha);

                if (Mathf.Abs(next - prev) > 0.12f)
                {
                    anyAngleChangedSignificantly = true;
                }

                st.CurrentAngleDeg = next;
                if (visible && Mathf.Abs(next) > 1e-4f)
                {
                    localPoses[bIdx].angle += next;
                }
            }
        }

        private static float ComputeRestOffsetFromRoot(BoneNodeDef[] nodes, int distalIdx)
        {
            float sum = 0f;
            int cur = distalIdx;
            int guard = 0;
            while (cur >= 0 && cur < nodes.Length && guard++ < nodes.Length)
            {
                sum += nodes[cur].baseAngle;
                cur = nodes[cur].parentRuntimeIndex;
            }
            return Mathf.DeltaAngle(0f, sum);
        }

        private void ComputeBaseWorldAngles(BoneNodeDef[] nodes, float rootHeadingDeg, ReadOnlySpan<BoneLocalPose> localPoses)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef bDef = nodes[i];
                float parentWorld = bDef.parentRuntimeIndex >= 0
                    ? workingWorldAngles[bDef.parentRuntimeIndex]
                    : rootHeadingDeg;
                float animAngle = i < localPoses.Length ? localPoses[i].angle : 0f;
                workingWorldAngles[i] = SkeletalKinematics.NormalizeAngle360(parentWorld + bDef.baseAngle + animAngle);
            }
        }

        internal static float SmoothRamp01(float fromEdge, float toEdge, float value)
        {
            float t = Mathf.InverseLerp(fromEdge, toEdge, value);
            return Mathf.SmoothStep(0f, 1f, t);
        }

        private static float StepDampedBoneAngle(
            float current,
            ref float velocityDegPerTick,
            float target,
            float maxSpeedDegPerTick,
            float responsiveness,
            float deltaTicks,
            bool active,
            bool wrap360 = false)
        {
            if (!active)
            {
                if (deltaTicks <= 0f)
                {
                    velocityDegPerTick = 0f;
                    return 0f;
                }
                target = 0f;
            }
            else if (deltaTicks <= 0f)
            {
                return current;
            }

            float diff = wrap360 ? Mathf.DeltaAngle(current, target) : (target - current);
            float absDiff = Mathf.Abs(diff);
            float maxSpd = Mathf.Max(0.15f, maxSpeedDegPerTick);
            float accel = Mathf.Max(0.08f, maxSpd * Mathf.Clamp(responsiveness, 0.06f, 0.90f) * 1.65f);

            if (absDiff < 0.03f && Mathf.Abs(velocityDegPerTick) <= accel * deltaTicks)
            {
                velocityDegPerTick = 0f;
                return target;
            }

            float brakingSpeed = Mathf.Sqrt(2f * accel * absDiff);
            float desiredVel = Mathf.Min(maxSpd, brakingSpeed) * Mathf.Sign(diff);
            float maxDv = accel * deltaTicks;
            float dv = desiredVel - velocityDegPerTick;
            if (Mathf.Abs(dv) > maxDv)
            {
                velocityDegPerTick += Mathf.Sign(dv) * maxDv;
            }
            else
            {
                velocityDegPerTick = desiredVel;
            }

            float step = velocityDegPerTick * deltaTicks;
            if (Mathf.Abs(step) >= absDiff && Mathf.Sign(step) == Mathf.Sign(diff))
            {
                step = diff;
                velocityDegPerTick = step / deltaTicks;
            }
            return wrap360 ? Mathf.DeltaAngle(0f, current + step) : (current + step);
        }

        private static bool IsBoneChainIntact(BoneNodeDef[] nodes, int idx, ReadOnlySpan<bool> missingMask)
        {
            if (missingMask.IsEmpty)
            {
                return true;
            }
            int cur = idx;
            while (cur >= 0 && cur < nodes.Length)
            {
                if (cur < missingMask.Length && missingMask[cur] && nodes[cur].hideWhenMissing)
                {
                    return false;
                }
                cur = nodes[cur].parentRuntimeIndex;
            }
            return true;
        }
    }
}
