using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Animation clocks, clip events, transitions, and cross-fade capture.
    public partial class CompGiantRig
    {
        private void UpdateAnimationClock(float deltaTicks, float movedDist)
        {
            if (animState == GiantAnimState.MorphLocked)
            {
                rushActive = false;
                if (activeOneShotAnim != null)
                {
                    float prevMorphTick = oneShotElapsedTicks;
                    oneShotElapsedTicks += deltaTicks;
                    EvaluateClipEvents(activeOneShotAnim, prevMorphTick, oneShotElapsedTicks, isLoop: false);
                    if (oneShotElapsedTicks >= activeOneShotAnim.durationTicks)
                    {
                        oneShotElapsedTicks = activeOneShotAnim.durationTicks;
                        activeOneShotAnim = null;
                        TransitionToClip(RigDef?.idleAnim, GiantAnimState.Idle);
                    }
                }
                else
                {
                    TransitionToClip(RigDef?.idleAnim, GiantAnimState.Idle);
                }
                return;
            }

            if (animState == GiantAnimState.ManualPreview)
            {
                if (activeOneShotAnim != null)
                {
                    oneShotElapsedTicks += deltaTicks;
                    if (!activeOneShotAnim.loop && oneShotElapsedTicks >= activeOneShotAnim.durationTicks)
                    {
                        animState = GiantAnimState.Idle;
                        activeOneShotAnim = null;
                    }
                    return;
                }
                TransitionToClip(RigDef?.idleAnim, GiantAnimState.Idle);
            }

            if (Pawn.Dead)
            {
                rushActive = false;
                if (animState != GiantAnimState.Death)
                {
                    BeginDeathAnimation();
                }
                return;
            }

            if (Pawn.Downed)
            {
                rushActive = false;
                GiantAnimDef downedClip = RigDef.downedAnim ?? RigDef.deathAnim;
                if (animState != GiantAnimState.Downed && downedClip != null && !downedClip.loop)
                {
                    oneShotElapsedTicks = 0f;
                }
                TransitionToClip(downedClip, GiantAnimState.Downed);
                if (downedClip != null && !downedClip.loop)
                {
                    oneShotElapsedTicks = Mathf.Min(oneShotElapsedTicks + deltaTicks, downedClip.durationTicks);
                }
                return;
            }

            if (flightEntryActive && RigDef?.flightEntry != null)
            {
                rushActive = false;
                GiantAnimDef fClip = RigDef.flightEntry.flightAnim;
                TransitionToClip(fClip, GiantAnimState.FlightEntry);
                if (fClip != null && fClip.durationTicks > 0.01f)
                {
                    float prevPhase = walkCyclePhaseTicks;
                    float unModNext = prevPhase + deltaTicks;
                    walkCyclePhaseTicks = unModNext % Mathf.Max(1f, fClip.durationTicks);
                    EvaluateClipEvents(fClip, prevPhase, unModNext, isLoop: true);
                }
                return;
            }

            UpdateRushState();

            if (GiantMotionOverrideRegistry.TryGetActiveAnimOverride(Pawn, out string overrideAnimDefName, out float overrideSampleTick))
            {
                GiantAnimDef overrideClip = ResolveAbilityAnim(overrideAnimDefName);
                if (overrideClip != null)
                {
                    rushActive = false;
                    isWarmupWindup = false;
                    float prevOverrideTick = (activeOneShotAnim == overrideClip) ? oneShotElapsedTicks : 0f;
                    activeOneShotAnim = overrideClip;
                    oneShotElapsedTicks = overrideSampleTick;
                    TransitionToClip(overrideClip, GiantAnimState.AttackOnce);
                    if (oneShotElapsedTicks > prevOverrideTick)
                    {
                        EvaluateClipEvents(overrideClip, prevOverrideTick, oneShotElapsedTicks, isLoop: false);
                    }
                    return;
                }
            }

            if (animState == GiantAnimState.AttackOnce && activeOneShotAnim != null)
            {
                if (isWarmupWindup)
                {
                    if (Pawn.stances?.curStance is Stance_Warmup activeWarmup &&
                        ResolveWarmupAnim(activeWarmup, out Vector2? warmupTargetXZ) == activeOneShotAnim)
                    {
                        float declaredTicks = Mathf.Max(1f, (activeWarmup.verb?.verbProps?.warmupTime ?? 0f) * 60f);
                        warmupPeakTicksLeft = Mathf.Max(warmupPeakTicksLeft, declaredTicks, activeWarmup.ticksLeft, 1f);
                        float p01 = Mathf.Clamp01(1f - activeWarmup.ticksLeft / warmupPeakTicksLeft);
                        float warmupCap = ResolveWarmupCapTick(activeOneShotAnim);
                        float prevTick = oneShotElapsedTicks;
                        oneShotElapsedTicks = Mathf.Clamp(p01 * warmupCap, 0f, warmupCap);
                        if (warmupTargetXZ.HasValue)
                        {
                            hasAttackTargetWorldXZ = true;
                            attackTargetWorldXZ = warmupTargetXZ.Value;
                        }
                        if (oneShotElapsedTicks > prevTick)
                        {
                            EvaluateClipEvents(activeOneShotAnim, prevTick, oneShotElapsedTicks, isLoop: false);
                        }
                        return;
                    }
                    isWarmupWindup = false;
                    activeOneShotAnim = null;
                    hasAttackTargetWorldXZ = false;
                }
                else
                {
                    if (GiantMotionOverrideRegistry.TryGetSyncedClipSampleTick(Pawn, activeOneShotAnim.defName, out float syncedSampleTick))
                    {
                        float prevSyncedTick = oneShotElapsedTicks;
                        oneShotElapsedTicks = syncedSampleTick;
                        if (oneShotElapsedTicks > prevSyncedTick)
                        {
                            EvaluateClipEvents(activeOneShotAnim, prevSyncedTick, oneShotElapsedTicks, isLoop: false);
                        }
                        return;
                    }

                    float prevTick = oneShotElapsedTicks;
                    oneShotElapsedTicks += deltaTicks;
                    EvaluateClipEvents(activeOneShotAnim, prevTick, oneShotElapsedTicks, isLoop: false);
                    if (oneShotElapsedTicks < activeOneShotAnim.durationTicks)
                    {
                        return;
                    }
                    activeOneShotAnim = null;
                    hasAttackTargetWorldXZ = false;
                }
            }

            if (Pawn.stances?.curStance is Stance_Warmup warmup)
            {
                GiantAnimDef wClip = ResolveWarmupAnim(warmup, out Vector2? warmupTargetXZ);
                if (wClip != null)
                {
                    float declaredTicks = Mathf.Max(1f, (warmup.verb?.verbProps?.warmupTime ?? 0f) * 60f);
                    warmupPeakTicksLeft = Mathf.Max(declaredTicks, warmup.ticksLeft, 1f);
                    float p01 = Mathf.Clamp01(1f - warmup.ticksLeft / warmupPeakTicksLeft);
                    float warmupCap = ResolveWarmupCapTick(wClip);
                    activeOneShotAnim = wClip;
                    isWarmupWindup = true;
                    oneShotElapsedTicks = Mathf.Clamp(p01 * warmupCap, 0f, warmupCap);
                    hasAttackTargetWorldXZ = warmupTargetXZ.HasValue;
                    if (warmupTargetXZ.HasValue)
                    {
                        attackTargetWorldXZ = warmupTargetXZ.Value;
                    }
                    TransitionToClip(wClip, GiantAnimState.AttackOnce);
                    if (oneShotElapsedTicks > 0f)
                    {
                        EvaluateClipEvents(wClip, 0f, oneShotElapsedTicks, isLoop: false);
                    }
                    return;
                }
            }

            if (IsCurrentlyAirborne && flightLocomotion?.Def.flightLocomotionAnim is GiantAnimDef airClip)
            {
                GiantAnimState airState = IsLocomotingWithGrace ? GiantAnimState.Walk : GiantAnimState.Idle;
                if (currentClip != null && currentClip != airClip && currentClip.durationTicks > 0.01f)
                {
                    float normPhase = Mathf.Clamp01(walkCyclePhaseTicks / Mathf.Max(1f, currentClip.durationTicks));
                    walkCyclePhaseTicks = normPhase * Mathf.Max(1f, airClip.durationTicks);
                }

                TransitionToClip(airClip, airState);
                turnInPlaceGraceTicks = 0;
                isTurnInPlaceAnimLatched = false;
                float airRate = IsLocomotingWithGrace ? 1.05f : 0.90f;
                float prevPhase = walkCyclePhaseTicks;
                float unModNext = prevPhase + deltaTicks * airRate;
                walkCyclePhaseTicks = unModNext % Mathf.Max(1f, airClip.durationTicks);
                EvaluateClipEvents(airClip, prevPhase, unModNext, isLoop: true);
                return;
            }

            bool inWaterNow = IsCurrentlyInWater;
            GiantAquaticDef aqDef = inWaterNow ? aquaticLocomotion?.Def : null;
            GiantAnimDef resolvedIdleAnim = (aqDef?.swimIdleAnim != null ? aqDef.swimIdleAnim : null) ?? RigDef?.idleAnim;
            GiantAnimDef resolvedWalkAnim = (aqDef?.swimAnim != null ? aqDef.swimAnim : null) ?? RigDef?.walkAnim;
            GiantAnimDef resolvedRunAnim = (aqDef?.swimFastAnim != null ? aqDef.swimFastAnim : null)
                ?? (aqDef?.swimAnim != null ? aqDef.swimAnim : null)
                ?? RigDef?.runAnim;

            if (IsLocomotingWithGrace && (resolvedWalkAnim != null || resolvedRunAnim != null))
            {
                bool useRun = rushActive && IsDistantRushAllowed && resolvedRunAnim != null;
                GiantAnimDef flightLocoOverride = IsCurrentlyAirborne ? flightLocomotion?.Def.flightLocomotionAnim : null;
                GiantAnimDef locoClip = flightLocoOverride ?? (useRun
                    ? resolvedRunAnim
                    : (resolvedWalkAnim ?? resolvedRunAnim));
                GiantAnimState locoState = (useRun && locoClip == resolvedRunAnim)
                    ? GiantAnimState.Run
                    : GiantAnimState.Walk;

                if (currentClip != null && currentClip != locoClip
                    && (animState == GiantAnimState.Walk || animState == GiantAnimState.Run))
                {
                    float normPhase = Mathf.Clamp01(walkCyclePhaseTicks / Mathf.Max(1f, currentClip.durationTicks));
                    walkCyclePhaseTicks = normPhase * Mathf.Max(1f, locoClip.durationTicks);
                }

                TransitionToClip(locoClip, locoState);
                turnInPlaceGraceTicks = 0;
                isTurnInPlaceAnimLatched = false;
                float baseWalkCycle = (aqDef != null && aqDef.swimCycleDistance > 0.05f)
                    ? aqDef.swimCycleDistance
                    : RigDef.walkCycleDistance;
                float cycleDist = locoState == GiantAnimState.Run
                    ? Mathf.Max(0.25f, (aqDef != null && aqDef.swimCycleDistance > 0.05f) ? aqDef.swimCycleDistance * 1.15f : RigDef.runCycleDistance)
                    : Mathf.Max(0.25f, baseWalkCycle);
                float stepTicks = (movedDist > 1e-4f)
                    ? (movedDist / cycleDist) * locoClip.durationTicks
                    : 0f;
                float prevPhase = walkCyclePhaseTicks;
                float unModNext = prevPhase + stepTicks;
                walkCyclePhaseTicks = unModNext % Mathf.Max(1f, locoClip.durationTicks);
                EvaluateClipEvents(locoClip, prevPhase, unModNext, isLoop: true);
            }
            else if (wasBiologicalSleeping || sleepCurlProgress01 > 0.02f)
            {
                turnInPlaceGraceTicks = 0;
                isTurnInPlaceAnimLatched = false;
                TransitionToClip(resolvedIdleAnim, GiantAnimState.Idle);
            }
            else if (!IsCurrentlyAirborne && (RigDef.turnLeftAnim != null || RigDef.turnRightAnim != null))
            {
                float absTurnStep = Mathf.Abs(lastStepHeadingDeltaDeg);
                float remTurnDeg = headingInitialized ? Mathf.DeltaAngle(currentHeadingDeg, targetHeadingDeg) : 0f;
                isTurnInPlaceAnimLatched = StepTurnInPlaceAnimLatch(
                    isTurnInPlaceAnimLatched,
                    isPivotTurningInPlace,
                    Mathf.Abs(remTurnDeg),
                    absTurnStep);
                if (isTurnInPlaceAnimLatched)
                {
                    turnInPlaceGraceTicks = 4;
                    if (absTurnStep > 0.08f)
                    {
                        lastTurnDirectionSign = lastStepHeadingDeltaDeg >= 0f ? 1 : -1;
                    }
                    else if (Mathf.Abs(remTurnDeg) > 1.0f)
                    {
                        lastTurnDirectionSign = remTurnDeg >= 0f ? 1 : -1;
                    }
                    else if (lastTurnDirectionSign == 0)
                    {
                        lastTurnDirectionSign = 1;
                    }
                }
                else if (turnInPlaceGraceTicks > 0)
                {
                    turnInPlaceGraceTicks--;
                }

                GiantAnimDef turnClip = lastTurnDirectionSign >= 0
                    ? (RigDef.turnRightAnim ?? RigDef.turnLeftAnim)
                    : (RigDef.turnLeftAnim ?? RigDef.turnRightAnim);
                if (turnInPlaceGraceTicks > 0 && turnClip != null)
                {
                    if (animState != GiantAnimState.TurnInPlace)
                    {
                        turnCyclePhaseTicks = 0f;
                    }
                    else if (currentClip != null && currentClip != turnClip)
                    {
                        float normTurn = Mathf.Clamp01(turnCyclePhaseTicks / Mathf.Max(1f, currentClip.durationTicks));
                        turnCyclePhaseTicks = normTurn * Mathf.Max(1f, turnClip.durationTicks);
                    }
                    TransitionToClip(turnClip, GiantAnimState.TurnInPlace);
                    float cycleDeg = Mathf.Max(8f, RigDef.turnCycleAngleDeg);
                    float stepTicks = absTurnStep > 1e-4f
                        ? (absTurnStep / cycleDeg) * turnClip.durationTicks
                        : 0f;
                    float prevPhase = turnCyclePhaseTicks;
                    float unModNext = prevPhase + stepTicks;
                    turnCyclePhaseTicks = unModNext % Mathf.Max(1f, turnClip.durationTicks);
                    EvaluateClipEvents(turnClip, prevPhase, unModNext, isLoop: true);
                }
                else
                {
                    TransitionToClip(resolvedIdleAnim, GiantAnimState.Idle);
                }
            }
            else
            {
                GiantAnimDef idleTargetClip = (IsCurrentlyAirborne && flightLocomotion?.Def.flightLocomotionAnim != null)
                    ? flightLocomotion.Def.flightLocomotionAnim
                    : resolvedIdleAnim;
                TransitionToClip(idleTargetClip, GiantAnimState.Idle);
            }
        }

        public static bool StepTurnInPlaceAnimLatch(
            bool currentlyLatched,
            bool isPivotTurningBeforeMove,
            float absRemainingTurnDeg,
            float absStepHeadingDeltaDeg)
        {
            if (isPivotTurningBeforeMove)
            {
                return true;
            }
            if (currentlyLatched)
            {
                return absRemainingTurnDeg > 4.5f || absStepHeadingDeltaDeg > 0.45f;
            }
            return absRemainingTurnDeg >= 22f;
        }

        public void EvaluateClipEvents(GiantAnimDef clip, float prevTick, float curTick, bool isLoop)
        {
            if (clip == null || RigDef == null || Mathf.Abs(curTick - prevTick) <= 1e-5f)
            {
                return;
            }

            bool firedExplicitFootstep = false;
            if (clip.events != null && clip.events.Count > 0)
            {
                for (int i = 0; i < clip.events.Count; i++)
                {
                    GiantAnimEventDef evt = clip.events[i];
                    if (evt == null)
                    {
                        continue;
                    }
                    if (!GiantAnimDef.DidCrossEventTick(prevTick, curTick, evt.tick, clip.durationTicks, isLoop))
                    {
                        continue;
                    }
                    // Regular melee attacks strictly emit zero VFX even if an XML clip defines <events>.
                    if (!isLoop && RigDef?.meleeAttackAnims != null && RigDef.meleeAttackAnims.Contains(clip))
                    {
                        continue;
                    }
                    if (evt.vfxPreset == GiantAnimVfxPreset.FootstepDust)
                    {
                        firedExplicitFootstep = true;
                    }

                    Vector3 worldPos = GetSafeDrawPos(Pawn);
                    float worldAngle = currentHeadingDeg;
                    if (!string.IsNullOrEmpty(evt.emitterId) && CollectEmitterMuzzles(evt.emitterId, tempEmitterShots) > 0)
                    {
                        for (int m = 0; m < tempEmitterShots.Count; m++)
                        {
                            GiantMuzzleShotInfo mShot = tempEmitterShots[m];
                            float mAngle = SkeletalKinematics.NormalizeAngle360(mShot.WorldAngleDeg + evt.angleOffset);
                            GiantVfxSystem.TriggerAnimEvent(Pawn, this, evt, mShot.WorldPos, mAngle);
                        }
                        continue;
                    }

                    if (!string.IsNullOrEmpty(evt.boneId) && RigDef.TryGetNodeIndex(evt.boneId, out int bIdx))
                    {
                        if (!IsBoneIntact(evt.boneId))
                        {
                            continue;
                        }
                        BoneNodeDef bDef = RigDef.SortedNodes[bIdx];
                        evt.ResolveBonePoint(bDef, GetBoneSectorThreadSafe(bIdx), out Vector2 uv, out float angleOff);
                        if (!TryGetBoneUVWorldPos(bIdx, uv, out worldPos, out worldAngle))
                        {
                            continue;
                        }
                        worldAngle = SkeletalKinematics.NormalizeAngle360(worldAngle + angleOff);
                    }
                    GiantVfxSystem.TriggerAnimEvent(Pawn, this, evt, worldPos, worldAngle);
                }
            }

            if (isLoop && !firedExplicitFootstep && RigDef.autoFootstepVfx
                && !IsCurrentlyAirborne
                && (vehicleLocomotion == null || !vehicleLocomotion.Def.suppressBiologicalGait)
                && Pawn != null && Pawn.Spawned)
            {
                float halfDur = clip.durationTicks * 0.5f;
                bool crossedZero = GiantAnimDef.DidCrossEventTick(prevTick, curTick, 0.01f, clip.durationTicks, true);
                bool crossedHalf = GiantAnimDef.DidCrossEventTick(prevTick, curTick, halfDur, clip.durationTicks, true);
                if (crossedZero || crossedHalf)
                {
                    Vector3 center = GetSafeDrawPos(Pawn);
                    float sideSign = crossedZero ? -1f : 1f;
                    float rad = currentHeadingDeg * Mathf.Deg2Rad;
                    Vector3 right = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad));
                    Vector3 stepPos = center + right * (sideSign * 1.1f);
                    GiantVfxSystem.SpawnFootstepVfx(Pawn, stepPos, scale: rushActive ? 1.35f : 0.95f, cameraShake: RigDef.footstepCameraShake);
                }
            }
        }

        private void CaptureBlendFromPoses(BoneLocalPose[] srcPoses)
        {
            if (RigDef == null || srcPoses == null || blendFromPoses == null)
            {
                return;
            }
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            int len = Mathf.Min(nodes.Length, Mathf.Min(srcPoses.Length, blendFromPoses.Length));
            for (int i = 0; i < len; i++)
            {
                if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                {
                    blendFromPoses[i] = BoneLocalPose.Identity;
                    continue;
                }
                BoneLocalPose basePose = srcPoses[i];
                if (Mathf.Abs(nodes[i].continuousSpinDegPerTick) > 1e-4f)
                {
                    basePose.angle = Mathf.DeltaAngle(0f, basePose.angle - continuousSpinTicks * nodes[i].continuousSpinDegPerTick);
                }
                if (proceduralBoneAngleOffsets != null && i < proceduralBoneAngleOffsets.Length && Mathf.Abs(proceduralBoneAngleOffsets[i]) > 1e-4f)
                {
                    basePose.angle = Mathf.DeltaAngle(0f, basePose.angle - proceduralBoneAngleOffsets[i]);
                }
                blendFromPoses[i] = basePose;
            }
        }

        private void TransitionToClip(GiantAnimDef nextClip, GiantAnimState nextState)
        {
            animState = nextState;
            if (currentClip == nextClip)
            {
                return;
            }

            if (blendFromPoses == null)
            {
                EnsureBuffersInitialized();
            }
            BoneLocalPose[] srcPoses = preSteeringLocalPoses != null && blendFromPoses != null && preSteeringLocalPoses.Length == blendFromPoses.Length
                ? preSteeringLocalPoses
                : finalLocalPoses;
            CaptureBlendFromPoses(srcPoses);
            currentClip = nextClip;
            blendElapsedTicks = 0f;
            blendDurationTicks = nextClip != null ? Mathf.Max(0, nextClip.blendInTicks) : 6f;
            if (flightBlendRemainingTicks > 0.01f)
            {
                blendDurationTicks = Mathf.Max(blendDurationTicks, flightBlendRemainingTicks);
            }
        }

    }
}
