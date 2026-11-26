using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Flight entry, movement sampling, pursuit, and rush decisions.
    public partial class CompGiantRig
    {
        public void BeginFlightEntry()
        {
            GiantFlightEntryDef fe = RigDef?.flightEntry;
            if (fe == null || !fe.enabled || Pawn == null || Pawn.Map == null)
            {
                return;
            }

            Vector3 endPos = GetSimTickPos(Pawn);
            flightEndXZ = new Vector2(endPos.x, endPos.z);

            IntVec3 mapSize = Pawn.Map.Size;
            float dW = endPos.x;
            float dE = mapSize.x - endPos.x;
            float dS = endPos.z;
            float dN = mapSize.z - endPos.z;

            Vector2 dirToEdge;
            float minD = dW;
            dirToEdge = new Vector2(-1f, 0f);
            if (dE < minD) { minD = dE; dirToEdge = new Vector2(1f, 0f); }
            if (dS < minD) { minD = dS; dirToEdge = new Vector2(0f, -1f); }
            if (dN < minD) { dirToEdge = new Vector2(0f, 1f); }

            float dist = Mathf.Max(4f, fe.startOffsetCells);
            flightStartXZ = flightEndXZ + dirToEdge * dist;
            flightCurrentGroundXZ = flightStartXZ;

            Vector2 toEnd = flightEndXZ - flightStartXZ;
            flightHeadingDeg = toEnd.sqrMagnitude > 1e-4f
                ? SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(toEnd.x, toEnd.y) * Mathf.Rad2Deg)
                : Pawn.Rotation.AsAngle;
            currentHeadingDeg = flightHeadingDeg;
            targetHeadingDeg = flightHeadingDeg;
            headingInitialized = true;
            steeringRuntime?.Reset(currentHeadingDeg);

            flightEntryActive = true;
            flightEntryElapsedTicks = 0;
            flightEntryDurationTicks = Mathf.Max(30, fe.durationTicks);
            flightStartAltitudeOffset = Mathf.Max(0.5f, fe.startAltitudeOffset);
            flightCurrentHeightOffset = flightStartAltitudeOffset;
            walkCyclePhaseTicks = 0f;

            TransitionToClip(fe.flightAnim, GiantAnimState.FlightEntry);
            blendDurationTicks = 0f;
            blendElapsedTicks = 0f;
            Pawn.pather?.StopDead();
        }

        private void StepFlightEntry()
        {
            if (!flightEntryActive || Pawn == null)
            {
                return;
            }

            if (Pawn.Dead || Pawn.Downed || !Pawn.Spawned || Pawn.Map == null)
            {
                flightEntryActive = false;
                flightEntryCompleted = true;
                flightCurrentHeightOffset = 0f;
                return;
            }

            Pawn.pather?.StopDead();
            if (Pawn.jobs?.curJob != null)
            {
                Pawn.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced, startNewJob: false);
            }

            flightEntryElapsedTicks++;
            float t = Mathf.Clamp01((float)flightEntryElapsedTicks / Mathf.Max(1, flightEntryDurationTicks));
            float moveT = 1f - Mathf.Pow(1f - t, 2.2f);
            float heightT = 1f - Mathf.Pow(1f - t, 1.7f);
            flightCurrentGroundXZ = Vector2.Lerp(flightStartXZ, flightEndXZ, moveT);
            flightCurrentHeightOffset = flightStartAltitudeOffset * (1f - heightT);

            if (flightEntryElapsedTicks >= flightEntryDurationTicks)
            {
                FinishFlightEntry();
            }
        }

        private void FinishFlightEntry()
        {
            GiantFlightEntryDef fe = RigDef?.flightEntry;
            flightEntryActive = false;
            flightEntryCompleted = true;
            flightCurrentHeightOffset = 0f;
            flightCurrentGroundXZ = flightEndXZ;

            if (Pawn != null && Pawn.Spawned && Pawn.Map != null)
            {
                Vector3 landPos = Pawn.DrawPos;
                float dustScale = Mathf.Clamp(fe?.landingDustScale ?? 3.2f, 0.8f, 6f);
                float shake = fe?.landingCameraShake ?? 0.35f;
                GiantVfxSystem.SpawnFlightLandingVfx(Pawn, landPos, dustScale * 0.45f, shake);
                if (fe?.landingSoundDef != null)
                {
                    fe.landingSoundDef.PlayOneShot(new TargetInfo(Pawn.Position, Pawn.Map));
                }
            }

            int foldTicks = Mathf.Max(6, fe?.wingFoldTransitionTicks ?? 32);
            flightBlendRemainingTicks = foldTicks;
            TransitionToClip(RigDef?.idleAnim, GiantAnimState.Idle);
            blendDurationTicks = Mathf.Max(blendDurationTicks, foldTicks);
            RebuildFootprintNow(notifyMap: true);
            if (Pawn != null && Pawn.Spawned && !Pawn.Dead && !Pawn.Downed && Pawn.jobs != null && Pawn.jobs.curJob == null)
            {
                Pawn.jobs.CheckForJobOverride(0f, ignoreQueue: false);
            }
        }

        public void SkipFlightEntryImmediate()
        {
            flightEntryActive = false;
            flightEntryCompleted = true;
            flightEntryElapsedTicks = flightEntryDurationTicks;
            flightCurrentHeightOffset = 0f;
            flightBlendRemainingTicks = 0;
            if (Pawn != null)
            {
                Vector3 pos = GetSimTickPos(Pawn);
                flightEndXZ = new Vector2(pos.x, pos.z);
                flightCurrentGroundXZ = flightEndXZ;
            }
            TransitionToClip(RigDef?.idleAnim, GiantAnimState.Idle);
            blendDurationTicks = 0f;
            blendElapsedTicks = 0f;
            EvaluateCurrentPoseAndPublish(0f);
            RebuildFootprintNow(notifyMap: true);
        }

        private float ConsumeMovedDistanceCells(out float signedForwardDeltaCells)
        {
            Vector3 curPos = GetSimTickPos(Pawn);
            float movedDist = 0f;
            signedForwardDeltaCells = 0f;
            if (hasLastDrawPos)
            {
                Vector3 d = curPos - lastDrawPos;
                d.y = 0f;
                movedDist = d.magnitude;
                if (movedDist > 1e-5f)
                {
                    float rad = currentHeadingDeg * Mathf.Deg2Rad;
                    float fwdX = Mathf.Sin(rad);
                    float fwdZ = Mathf.Cos(rad);
                    signedForwardDeltaCells = d.x * fwdX + d.z * fwdZ;
                }
            }
            lastDrawPos = curPos;
            hasLastDrawPos = true;
            return movedDist;
        }
        public static bool EvaluateRushHysteresis(
            bool currentlyRushing,
            float perimeterDist,
            bool enabled,
            float startDist,
            float stopDist)
        {
            if (!enabled || float.IsNaN(perimeterDist) || perimeterDist < 0f)
            {
                return false;
            }
            float start = Mathf.Max(0.5f, startDist);
            float stop = Mathf.Clamp(stopDist, 0f, start);
            return currentlyRushing ? perimeterDist >= stop : perimeterDist >= start;
        }

        public float GetEffectiveRushSpeedMultiplier()
        {
            if (!rushActive || !IsDistantRushAllowed)
            {
                return 1f;
            }
            return Mathf.Clamp(RigDef.rushMoveSpeedMultiplier, 0.25f, 5f);
        }

        public float GetEffectiveMeleeTouchRadius()
        {
            if (RigDef == null)
            {
                return 0f;
            }
            return RigDef.meleeTouchRadius > 0.01f
                ? RigDef.meleeTouchRadius
                : Mathf.Max(1.5f, RigDef.coreClearanceRadius + 1.5f);
        }

        public Thing ResolvePursuedHostileTarget()
        {
            if (Pawn == null || !Pawn.Spawned || Pawn.Map == null || Pawn.Downed || Pawn.Dead)
            {
                return null;
            }

            Verse.AI.Job curJob = Pawn.CurJob;
            if (curJob != null && curJob.targetA.Thing is Thing jobThing
                && jobThing.Spawned && jobThing.Map == Pawn.Map
                && GiantPartCombatRuntime.IsValidHostileThing(Pawn, jobThing))
            {
                return jobThing;
            }

            if (partCombat != null && partCombat.HasValidForcedTarget() && partCombat.ForcedTarget.HasThing &&
                partCombat.ForcedTarget.Thing.Spawned && partCombat.ForcedTarget.Thing.Map == Pawn.Map &&
                GiantPartCombatRuntime.IsValidHostileThing(Pawn, partCombat.ForcedTarget.Thing))
            {
                return partCombat.ForcedTarget.Thing;
            }

            if (Pawn.mindState?.enemyTarget is Thing enemy
                && enemy.Spawned
                && enemy.Map == Pawn.Map
                && GiantPartCombatRuntime.IsValidHostileThing(Pawn, enemy))
            {
                return enemy;
            }

            Thing provoked = TryGetActiveProvokedTarget();
            if (provoked != null)
            {
                return provoked;
            }

            return null;
        }
        public bool TryGetPursuedHostilePerimeterDistance(out float perimeterDist)
        {
            Thing target = ResolvePursuedHostileTarget();
            if (target == null)
            {
                perimeterDist = -1f;
                return false;
            }

            CellRect targetRect = target.OccupiedRect();
            Vector3 targetCenter = target.DrawPos;
            float bestDistSq = float.MaxValue;

            if (footprint.collisionCells != null && footprint.collisionCells.Count > 0)
            {
                for (int i = 0; i < footprint.collisionCells.Count; i++)
                {
                    IntVec3 cc = footprint.collisionCells[i];
                    float tx = Mathf.Clamp(cc.x, targetRect.minX, targetRect.maxX);
                    float tz = Mathf.Clamp(cc.z, targetRect.minZ, targetRect.maxZ);
                    float dx = cc.x - tx;
                    float dz = cc.z - tz;
                    float dSq = dx * dx + dz * dz;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                    }
                }
                // Subtract 0.5 cell edge margin for 1-cell border contact = ~0
                perimeterDist = Mathf.Max(0f, Mathf.Sqrt(bestDistSq) - 0.5f);
                return true;
            }

            Vector3 selfPos = GetSimTickPos(Pawn);
            float centerDist = Vector2.Distance(new Vector2(selfPos.x, selfPos.z), new Vector2(targetCenter.x, targetCenter.z));
            float targetHalf = 0.5f * Mathf.Max(target.def.size.x, target.def.size.z);
            perimeterDist = Mathf.Max(0f, centerDist - Mathf.Max(0f, (RigDef?.coreClearanceRadius ?? 0) + 1.0f) - targetHalf);
            return true;
        }

        private void UpdateRushState()
        {
            if (Pawn == null || !Pawn.Spawned || Pawn.Downed || Pawn.Dead || !IsDistantRushAllowed || IsCurrentlyAirborne)
            {
                rushActive = false;
                return;
            }

            if (!TryGetPursuedHostilePerimeterDistance(out float dist))
            {
                rushActive = false;
                return;
            }

            rushActive = EvaluateRushHysteresis(
                rushActive,
                dist,
                enabled: true,
                startDist: RigDef.rushStartDistance,
                stopDist: RigDef.rushStopDistance);
        }

        public void ForceSetHeadingImmediate(float headingDeg)
        {
            if (Pawn == null || RigDef == null)
            {
                return;
            }
            EnsureBuffersInitialized();
            float norm = SkeletalKinematics.NormalizeAngle360(headingDeg);
            currentHeadingDeg = norm;
            targetHeadingDeg = norm;
            headingInitialized = true;
            smoothedDesiredLookDeg = norm;
            smoothedLookInitialized = true;
            sectorsInitialized = false;
            trackKinematicsInitialized = false;
            pendingTrackWorldFwdDelta = 0f;
            steeringRuntime?.Reset(norm);
            if (vehicleLocomotion != null || flightVehicleLocomotion != null)
            {
                Vector2 resetXZ = Pawn.Spawned
                    ? new Vector2(Pawn.Position.ToVector3Shifted().x, Pawn.Position.ToVector3Shifted().z)
                    : Vector2.zero;
                vehicleLocomotion?.Reset(norm, resetXZ, hasValidPos: Pawn.Spawned);
                if (flightVehicleLocomotion != null)
                {
                    bool startAir = flightLocomotion == null || flightLocomotion.IsCurrentlyAirborne;
                    flightVehicleLocomotion.Reset(norm, resetXZ, hasValidPos: Pawn.Spawned, startAirborne: startAir);
                }
            }
            EvaluateCurrentPoseAndPublish(0f);
            RebuildFootprintNow(notifyMap: true);
        }

    }
}
