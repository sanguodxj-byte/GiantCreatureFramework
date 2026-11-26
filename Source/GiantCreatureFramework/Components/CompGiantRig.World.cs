using System;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public partial class CompGiantRig
    {
        private bool worldSimulationActive;
        private Vector3 worldSimulationPosition;

        public bool WorldSimulationActive => worldSimulationActive;
        public Vector3 WorldSimulationPosition => worldSimulationPosition;

        public void BeginWorldSimulation()
        {
            EnsureBuffersInitialized();
            ResolveBodyPartLinks();
            worldSimulationActive = true;
            worldSimulationPosition = Vector3.zero;
            flightEntryActive = false;
            flightBlendRemainingTicks = 0f;
            activeOneShotAnim = null;
            isWarmupWindup = false;
            hasAttackTargetWorldXZ = false;
            lockedLookTargetRef = null;
            partCombat?.ClearForcedTarget();
            if (Pawn?.mindState != null)
            {
                Pawn.mindState.enemyTarget = null;
            }
            steeringRuntime?.Reset(currentHeadingDeg);
            if (bezierRuntimes != null)
            {
                foreach (DampedBezierChainRuntime chain in bezierRuntimes) chain?.Reset();
            }
            if (softWhipRuntimes != null)
            {
                foreach (SoftWhipRuntime whip in softWhipRuntimes) whip?.Reset();
            }
            trackKinematicsInitialized = false;
            healthStateDirty = true;
            UpdateMissingPartsState(out _, force: true);
            TransitionToClip(Pawn?.Dead == true ? RigDef.deathAnim : RigDef.idleAnim,
                Pawn?.Dead == true ? GiantAnimState.Death : GiantAnimState.Idle);
            EvaluateCurrentPoseAndPublish(0f);
            Pawn?.Drawer?.renderer?.renderTree?.SetDirty();
        }

        public void EndWorldSimulation()
        {
            worldSimulationActive = false;
            hasLastDrawPos = false;
            trackKinematicsInitialized = false;
            partCombat?.ClearForcedTarget();
            Pawn?.Drawer?.renderer?.renderTree?.SetDirty();
        }

        public GiantFootprintData BuildPlacementFootprint()
        {
            EnsureBuffersInitialized();
            EnsureAlphaMasksLoaded();
            var placementPoses = new BoneWorldPose[RigDef.SortedNodes.Length];
            SkeletalKinematics.SolveWorldPoses(RigDef, new Vector2(0.5f, 0.5f), currentHeadingDeg,
                finalLocalPoses, partMissingMask, placementPoses, 1f,
                sectorsInitialized ? activeBoneSectors : ReadOnlySpan<GiantBoneViewSector>.Empty);
            var result = new GiantFootprintData();
            result.Rebuild(RigDef, placementPoses, activeAlphaMasks ?? alphaMasks, IntVec3.Zero,
                softWhips: softWhipSnapshots, softWhipMasks: softWhipAlphaMasks);
            return result;
        }

        public void TickWorldAnimation(float desiredHeading, float movedDistanceCells)
        {
            if (!worldSimulationActive || Pawn == null || RigDef == null) return;
            EnsureBuffersInitialized();
            UpdateMissingPartsState(out _);
            float previousHeading = currentHeadingDeg;
            targetHeadingDeg = desiredHeading;
            currentHeadingDeg = Mathf.MoveTowardsAngle(currentHeadingDeg, desiredHeading, RigDef.turnSpeedDegPerTick);
            headingInitialized = true;
            lastStepHeadingDeltaDeg = Mathf.DeltaAngle(previousHeading, currentHeadingDeg);
            continuousSteeringTicks += 1f;
            if (!Pawn.Dead && !(GiantFastLookup.TryGetCockpit(Pawn) is CompGiantCockpit wSpinCp && wSpinCp.IsDormantUnmanned))
            {
                continuousSpinTicks += 1f;
            }
            bool moving = movedDistanceCells > 0f && !Pawn.Dead && !Pawn.Downed;
            if (moving)
            {
                float radians = currentHeadingDeg * Mathf.Deg2Rad;
                worldSimulationPosition += new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians)) * movedDistanceCells;
                pendingTrackWorldFwdDelta += movedDistanceCells;
            }

            if (Pawn.Dead)
            {
                if (animState != GiantAnimState.Death) BeginDeathAnimation();
                if (currentClip != null) oneShotElapsedTicks = Mathf.Min(oneShotElapsedTicks + 1f, currentClip.durationTicks);
            }
            else if (Pawn.Downed)
            {
                GiantAnimDef downedClip = RigDef.downedAnim ?? RigDef.deathAnim;
                if (animState != GiantAnimState.Downed && downedClip != null && !downedClip.loop)
                {
                    oneShotElapsedTicks = 0f;
                }
                TransitionToClip(downedClip, GiantAnimState.Downed);
                if (downedClip != null && !downedClip.loop)
                {
                    oneShotElapsedTicks = Mathf.Min(oneShotElapsedTicks + 1f, downedClip.durationTicks);
                }
            }
            else if (moving)
            {
                GiantFlightDef worldFlightDef = RigDef.ActiveFlightLocomotion;
                bool worldFlying = worldFlightDef != null && worldFlightDef.IsActive
                    && (flightLocomotion == null || flightLocomotion.CanMaintainLift);
                GiantAquaticDef worldAqDef = !worldFlying ? RigDef.ActiveAquaticLocomotion : null;
                bool worldInWater = worldAqDef != null && worldAqDef.IsActive && worldAqDef.IsWaterOnly;
                if (!worldInWater && worldAqDef != null && worldAqDef.IsActive && Pawn != null && Find.WorldObjects != null)
                {
                    IThingHolder holder = Pawn.ParentHolder;
                    if (holder is WorldObject_Giant wg && wg.Tile.Valid && Find.WorldGrid != null)
                    {
                        RimWorld.Planet.Tile tInfo = Find.WorldGrid[wg.Tile];
                        worldInWater = tInfo != null && tInfo.WaterCovered;
                    }
                }
                GiantAnimDef clip = (worldFlying ? worldFlightDef.flightLocomotionAnim : null)
                    ?? (worldInWater ? worldAqDef?.swimAnim : null)
                    ?? RigDef.walkAnim ?? RigDef.runAnim ?? RigDef.idleAnim;
                TransitionToClip(clip, GiantAnimState.Walk);
                if (clip != null)
                {
                    float cycleDist = worldFlying
                        ? Mathf.Max(0.25f, RigDef.runCycleDistance > 0.05f ? RigDef.runCycleDistance : RigDef.walkCycleDistance)
                        : Mathf.Max(0.25f, (worldInWater && worldAqDef != null && worldAqDef.swimCycleDistance > 0.05f)
                            ? worldAqDef.swimCycleDistance
                            : RigDef.walkCycleDistance);
                    walkCyclePhaseTicks = (walkCyclePhaseTicks + movedDistanceCells / cycleDist * clip.durationTicks)
                        % Mathf.Max(1f, clip.durationTicks);
                }
            }
            else if (Mathf.Abs(lastStepHeadingDeltaDeg) > 0.001f && (RigDef.turnLeftAnim != null || RigDef.turnRightAnim != null))
            {
                GiantAnimDef clip = lastStepHeadingDeltaDeg >= 0f
                    ? RigDef.turnRightAnim ?? RigDef.turnLeftAnim : RigDef.turnLeftAnim ?? RigDef.turnRightAnim;
                TransitionToClip(clip, GiantAnimState.TurnInPlace);
                turnCyclePhaseTicks = (turnCyclePhaseTicks + Mathf.Abs(lastStepHeadingDeltaDeg) / Mathf.Max(8f, RigDef.turnCycleAngleDeg) * clip.durationTicks)
                    % Mathf.Max(1f, clip.durationTicks);
            }
            else
            {
                TransitionToClip(RigDef.idleAnim, GiantAnimState.Idle);
            }
            currentSteeringCtx = new GiantSteeringContext
            {
                DesiredBodyHeadingDeg = desiredHeading,
                DesiredLookHeadingDeg = desiredHeading,
                IsMoving = moving,
                MovedDistanceCells = movedDistanceCells,
                AnimState = animState,
                ContinuousTicks = continuousSteeringTicks,
                SteeringEnabled = true,
                IntensityScale = 1f,
                EnableNeckIdleAndGaitArticulation = true,
                LastBodyHeadingStepDeg = lastStepHeadingDeltaDeg,
                StabilizeTurretsAgainstHullTurn = true
            };
            EvaluateCurrentPoseAndPublish(1f);
        }
    }
}
