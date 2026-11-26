using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Procedural offsets, turret recoil, pose evaluation, and snapshot publication.
    public partial class CompGiantRig
    {
        public void SetProceduralBoneAngleOffset(string nodeId, float angleOffsetDeg)
        {
            GiantRigDef rig = RigDef;
            if (rig == null || string.IsNullOrEmpty(nodeId) || !rig.TryGetNodeIndex(nodeId, out int idx))
            {
                return;
            }
            int count = rig.SortedNodes.Length;
            if (proceduralBoneAngleOffsets == null || proceduralBoneAngleOffsets.Length != count)
            {
                proceduralBoneAngleOffsets = new float[count];
            }
            if (rig.SortedNodes[idx].isRigid && rig.SortedNodes[idx].parentRuntimeIndex >= 0)
            {
                proceduralBoneAngleOffsets[idx] = 0f;
                return;
            }
            proceduralBoneAngleOffsets[idx] = angleOffsetDeg;
        }

        private void ClearAllTurretRecoils()
        {
            anyTurretRecoilActive = false;
            if (currentTurretRecoilOffsets != null)
            {
                Array.Clear(currentTurretRecoilOffsets, 0, currentTurretRecoilOffsets.Length);
            }
            if (turretRecoilStates == null)
            {
                return;
            }
            for (int i = 0; i < turretRecoilStates.Length; i++)
            {
                turretRecoilStates[i].ClearActiveMotion();
            }
        }

        public bool TriggerTurretRecoil(int firingBoneIndex, float fireWorldAngleDeg, float scale = 1f, bool publishImmediate = false)
        {
            if (RigDef == null || scale <= 0.001f)
            {
                return false;
            }
            EnsureBuffersInitialized();
            if (!RigDef.TryResolveTurretRecoilForFiringBone(firingBoneIndex, out GiantTurretRecoilDef recDef, out int targetIdx))
            {
                return false;
            }
            if (turretRecoilStates == null || (uint)targetIdx >= (uint)turretRecoilStates.Length)
            {
                return false;
            }
            if (!IsBoneIntact(RigDef.SortedNodes[targetIdx].nodeId))
            {
                return false;
            }
            turretRecoilStates[targetIdx].QueueShotImpulse(recDef, fireWorldAngleDeg, scale);
            anyTurretRecoilActive = true;
            if (publishImmediate && Pawn != null && Pawn.Spawned)
            {
                EvaluateCurrentPoseAndPublish(0f);
            }
            return true;
        }

        public bool TriggerTurretRecoilByBoneId(string firingBoneId, float fireWorldAngleDeg, float scale = 1f, bool publishImmediate = false)
        {
            if (RigDef == null || string.IsNullOrEmpty(firingBoneId) || !RigDef.TryGetNodeIndex(firingBoneId, out int idx))
            {
                return false;
            }
            return TriggerTurretRecoil(idx, fireWorldAngleDeg, scale, publishImmediate);
        }

        private void StepAndApplyTurretRecoils(float deltaTicks, BoneLocalPose[] poses)
        {
            if (currentTurretRecoilOffsets != null && !anyTurretRecoilActive)
            {
                Array.Clear(currentTurretRecoilOffsets, 0, currentTurretRecoilOffsets.Length);
            }
            if (!anyTurretRecoilActive || turretRecoilStates == null || RigDef == null || poses == null)
            {
                return;
            }

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            int len = Mathf.Min(Mathf.Min(nodes.Length, poses.Length), turretRecoilStates.Length);
            if (currentTurretRecoilOffsets == null || currentTurretRecoilOffsets.Length < nodes.Length)
            {
                currentTurretRecoilOffsets = new Vector2[nodes.Length];
            }
            else
            {
                Array.Clear(currentTurretRecoilOffsets, 0, currentTurretRecoilOffsets.Length);
            }
            if (len <= 0)
            {
                anyTurretRecoilActive = false;
                return;
            }

            Span<float> boneWorldAngles = len <= 64 ? stackalloc float[len] : new float[len];
            for (int i = 0; i < len; i++)
            {
                int pIdx = nodes[i].parentRuntimeIndex;
                float parentAngle = pIdx >= 0 && pIdx < i ? boneWorldAngles[pIdx] : currentHeadingDeg;
                boneWorldAngles[i] = SkeletalKinematics.NormalizeAngle360(parentAngle + nodes[i].baseAngle + poses[i].angle);
            }

            bool stillAnyActive = false;
            for (int i = 1; i < len; i++)
            {
                if (!turretRecoilStates[i].active)
                {
                    continue;
                }
                if (partMissingMask != null && i < partMissingMask.Length && partMissingMask[i])
                {
                    turretRecoilStates[i].ClearActiveMotion();
                    continue;
                }
                currentTurretRecoilOffsets[i] = turretRecoilStates[i].StepAndEvaluate(deltaTicks, boneWorldAngles[i]);
                if (turretRecoilStates[i].active)
                {
                    stillAnyActive = true;
                }
            }

            anyTurretRecoilActive = stillAnyActive;
        }

        private void StepAndApplyHitReaction(float deltaTicks, BoneLocalPose[] poses)
        {
            if (hitReactionRuntime == null || RigDef == null || poses == null)
            {
                return;
            }
            bool isFullBodyAttacking = animState == GiantAnimState.AttackOnce;
            bool isDowned = (Pawn != null && Pawn.Downed) || animState == GiantAnimState.Downed;
            ReadOnlySpan<bool> missingSpan = partMissingMask != null ? partMissingMask : ReadOnlySpan<bool>.Empty;
            hitReactionRuntime.StepAndApply(RigDef, deltaTicks, poses, missingSpan, isFullBodyAttacking, isDowned);
        }

        public void EvaluateCurrentPoseAndPublish(float deltaTicks)
        {
            EnsureBuffersInitialized();
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            int count = nodes.Length;

            if (animState == GiantAnimState.Idle && currentClip == null && RigDef.idleAnim != null)
            {
                currentClip = RigDef.idleAnim;
            }

            float sampleTick = 0f;
            float pawnBodySize = Pawn?.BodySize ?? -1f;
            if (currentClip != null)
            {
                sampleTick = currentClip switch
                {
                    _ when animState == GiantAnimState.Walk
                        || animState == GiantAnimState.Run
                        || animState == GiantAnimState.FlightEntry
                        || (IsCurrentlyAirborne && currentClip == flightLocomotion?.Def.flightLocomotionAnim) => walkCyclePhaseTicks,
                    _ when animState == GiantAnimState.TurnInPlace => turnCyclePhaseTicks,
                    _ when animState == GiantAnimState.AttackOnce
                        || animState == GiantAnimState.MorphLocked
                        || animState == GiantAnimState.ManualPreview
                        || animState == GiantAnimState.Death
                        || (animState == GiantAnimState.Downed && !currentClip.loop) => oneShotElapsedTicks,
                    _ when animState == GiantAnimState.Idle && sleepCurlProgress01 > 0.001f => sleepIdleClockTicks,
                    _ => Current.Game?.tickManager != null ? Current.Game.tickManager.TicksGame + (Pawn?.thingIDNumber ?? 0) * 13f : continuousSteeringTicks
                };
                currentClip.EvaluateInto(RigDef, sampleTick, evalPoses, pawnBodySize);

                if (animState == GiantAnimState.FlightEntry
                    && flightEntryActive
                    && flightEntryDurationTicks > 30
                    && RigDef.idleAnim != null
                    && turnBlendPoses != null
                    && turnBlendPoses.Length >= count)
                {
                    int remFlightTicks = flightEntryDurationTicks - flightEntryElapsedTicks;
                    if (remFlightTicks < 24)
                    {
                        float uPrep = Mathf.Clamp01(1f - remFlightTicks / 24f);
                        float legPrepWeight = uPrep * uPrep * (3f - 2f * uPrep) * 0.62f;
                        RigDef.idleAnim.EvaluateInto(RigDef, 0f, turnBlendPoses, pawnBodySize);
                        for (int i = 0; i < count; i++)
                        {
                            string nid = nodes[i].nodeId;
                            if (nid != null && nid.StartsWith("leg_", StringComparison.Ordinal))
                            {
                                evalPoses[i] = BoneLocalPose.Lerp(in evalPoses[i], in turnBlendPoses[i], legPrepWeight);
                            }
                        }
                    }
                }

                if ((animState == GiantAnimState.Walk || animState == GiantAnimState.Run) &&
                    (vehicleLocomotion == null || !vehicleLocomotion.Def.suppressBiologicalGait) &&
                    Mathf.Abs(lastStepHeadingDeltaDeg) > 0.12f &&
                    RigDef.turnCorneringBlendMax > 0.001f &&
                    turnBlendPoses != null &&
                    turnBlendPoses.Length >= count)
                {
                    GiantAnimDef cornerClip = lastStepHeadingDeltaDeg > 0f
                        ? (RigDef.turnRightAnim ?? RigDef.turnLeftAnim)
                        : (RigDef.turnLeftAnim ?? RigDef.turnRightAnim);
                    if (cornerClip != null)
                    {
                        float turnRate01 = Mathf.Clamp01(Mathf.Abs(lastStepHeadingDeltaDeg) / Mathf.Max(0.35f, RigDef.turnSpeedDegPerTick));
                        float cornerWeight = turnRate01 * Mathf.Clamp(RigDef.turnCorneringBlendMax, 0f, 0.65f);
                        if (cornerWeight > 0.005f)
                        {
                            float normPhase = currentClip.durationTicks > 0.01f
                                ? Mathf.Clamp01(sampleTick / currentClip.durationTicks)
                                : 0f;
                            cornerClip.EvaluateInto(RigDef, normPhase * cornerClip.durationTicks, turnBlendPoses, pawnBodySize);
                            for (int i = 0; i < count; i++)
                            {
                                evalPoses[i] = BoneLocalPose.Lerp(in evalPoses[i], in turnBlendPoses[i], cornerWeight);
                            }
                        }
                    }
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    evalPoses[i] = BoneLocalPose.Identity;
                }
            }

            // Cross-fade blend from previous clip
            float blendT = 1f;
            if (blendDurationTicks > 0.01f && blendElapsedTicks < blendDurationTicks)
            {
                blendElapsedTicks += deltaTicks;
                blendT = Mathf.Clamp01(blendElapsedTicks / blendDurationTicks);
                blendT = blendT * blendT * (3f - 2f * blendT);
            }

            // Resolve active combat focus XZ for Bezier / whip world-space locking
            bool hasAimTarget = false;
            Vector2 aimTargetXZ = Vector2.zero;
            if (Pawn != null && Pawn.Spawned && !Pawn.Downed && Pawn.stances?.curStance is Stance_Busy busy && busy.focusTarg.IsValid)
            {
                Vector3 tp = busy.focusTarg.HasThing
                    ? (busy.focusTarg.Thing is Pawn tpPawn ? GetSimTickPos(tpPawn) : busy.focusTarg.Thing.DrawPos)
                    : busy.focusTarg.Cell.ToVector3Shifted();
                aimTargetXZ = new Vector2(tp.x, tp.z);
                Vector3 d = tp - GetSimTickPos(Pawn);
                if (d.sqrMagnitude > 0.05f)
                {
                    hasAimTarget = true;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                {
                    finalLocalPoses[i] = BoneLocalPose.Identity;
                    continue;
                }
                BoneLocalPose p = blendT < 0.999f
                    ? BoneLocalPose.Lerp(in blendFromPoses[i], in evalPoses[i], blendT)
                    : evalPoses[i];
                if (Mathf.Abs(nodes[i].continuousSpinDegPerTick) > 1e-4f)
                {
                    p.angle = Mathf.DeltaAngle(0f, p.angle + continuousSpinTicks * nodes[i].continuousSpinDegPerTick);
                }
                if (proceduralBoneAngleOffsets != null && i < proceduralBoneAngleOffsets.Length && Mathf.Abs(proceduralBoneAngleOffsets[i]) > 1e-4f)
                {
                    p.angle = Mathf.DeltaAngle(0f, p.angle + proceduralBoneAngleOffsets[i]);
                }
                finalLocalPoses[i] = p;
            }

            if (vehicleLocomotion != null && count > 0 && animState != GiantAnimState.Death && !ManualOverrideActive)
            {
                finalLocalPoses[0].offset.y += vehicleLocomotion.PitchImpulseOffsetY;
                if (vehicleLocomotion.Def.suppressBiologicalGait &&
                    (animState == GiantAnimState.Idle || animState == GiantAnimState.Walk || animState == GiantAnimState.Run))
                {
                    finalLocalPoses[0].angle = 0f;
                    finalLocalPoses[0].scale = Vector2.one;
                }
            }

            if (!ManualOverrideActive && animState != GiantAnimState.MorphLocked && animState != GiantAnimState.Death)
            {
                if (preSteeringLocalPoses != null && preSteeringLocalPoses.Length >= count)
                {
                    Array.Copy(finalLocalPoses, preSteeringLocalPoses, count);
                }
                partCombat?.ApplyLayeredPartAnimations(RigDef, IsLocomotingWithGrace, finalLocalPoses);
                Vector3 steerDrawPos = Pawn != null ? GetSimTickPos(Pawn) : Vector3.zero;
                if ((dailyIngestWeight01 > 0.001f || dailySocialWeight01 > 0.001f) && !IsLocomotingWithGrace)
                {
                    steeringRuntime?.ResetLagAndBankOnly(currentHeadingDeg);
                }
                if (partCombat == null || !partCombat.HasActiveFullHullSpinClip())
                {
                    steeringRuntime?.ApplyBoneSteering(
                        RigDef,
                        currentHeadingDeg,
                        finalLocalPoses,
                        partMissingMask,
                        deltaTicks,
                        in currentSteeringCtx,
                        new Vector2(steerDrawPos.x, steerDrawPos.z),
                        partCombat);
                }
                if (sleepCurlProgress01 > 0.001f)
                {
                    GiantSleepCurlSolver.ApplySleepCurl(
                        RigDef,
                        finalLocalPoses,
                        partMissingMask,
                        sleepCurlProgress01,
                        sleepCurlSideSign,
                        sleepBreathTicks,
                        includeBezierBones: !(bezierRuntimes != null && bezierRuntimes.Length > 0));
                }
                else if (dailyIngestWeight01 > 0.001f || dailySocialWeight01 > 0.001f)
                {
                    GiantDailyActivityPoseSolver.ApplyDailyActivity(
                        RigDef,
                        finalLocalPoses,
                        partMissingMask,
                        dailyIngestWeight01,
                        dailySocialWeight01,
                        dailyActivityPhaseTicks,
                        includeBezierBones: !(bezierRuntimes != null && bezierRuntimes.Length > 0));
                }
                StepAndApplyTurretRecoils(deltaTicks, finalLocalPoses);
                StepAndApplyHitReaction(deltaTicks, finalLocalPoses);
            }
            else
            {
                if (ManualBoneAngleOffsets != null)
                {
                    for (int i = 0; i < count && i < ManualBoneAngleOffsets.Length; i++)
                    {
                        if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                        {
                            finalLocalPoses[i] = BoneLocalPose.Identity;
                            continue;
                        }
                        finalLocalPoses[i].angle += ManualBoneAngleOffsets[i];
                    }
                }
                if (preSteeringLocalPoses != null && preSteeringLocalPoses.Length >= count)
                {
                    Array.Copy(finalLocalPoses, preSteeringLocalPoses, count);
                }
                if (currentTurretRecoilOffsets != null)
                {
                    Array.Clear(currentTurretRecoilOffsets, 0, currentTurretRecoilOffsets.Length);
                }
            }

            localPoseSerial++;

            int nextSlot = (snapshotRingCursor + 1) & (SnapshotRingSize - 1);
            snapshotRingCursor = nextSlot;
            GiantRenderSnapshot nextSnap = snapshotRing[nextSlot];

            bool hasBeziers = bezierRuntimes != null && bezierRuntimes.Length > 0;
            bool hasSoftWhips = softWhipRuntimes != null && softWhipRuntimes.Length > 0;
            SoftWhipRibbonSnapshot[] snapSoftWhips = nextSnap.softWhips;

            Vector3 simDrawPos = Pawn != null ? GetSimTickPos(Pawn) : Vector3.zero;
            Vector2 simDrawXZ = new Vector2(simDrawPos.x, simDrawPos.z);
            bool pawnDead = (Pawn != null && Pawn.Dead) || animState == GiantAnimState.Death;
            bool pawnDowned = !pawnDead && ((Pawn != null && Pawn.Downed) || animState == GiantAnimState.Downed);
            bool activeFlightEntry = flightEntryActive && !pawnDead && !pawnDowned;
            bool activeFlightVisual = !activeFlightEntry
                && flightLocomotion != null
                && Pawn != null
                && !pawnDead
                && !pawnDowned
                && flightLocomotion.CurrentAltitude01 > 0.005f;
            Vector2 flightOffsetXZ = activeFlightEntry
                ? (flightCurrentGroundXZ + new Vector2(0f, flightCurrentHeightOffset) - simDrawXZ)
                : (activeFlightVisual
                    ? flightLocomotion.ComputeVisualOffsetXZ()
                    : (aquaticLocomotion != null ? aquaticLocomotion.ComputeWaterVisualOffsetXZ(pawnDead || pawnDowned) : Vector2.zero));
            Vector2 effectiveRootXZ = simDrawXZ + flightOffsetXZ;

            if (hasBeziers || hasSoftWhips)
            {
                Vector2 rootXZ = effectiveRootXZ;
                float clipNormTime = (currentClip != null && currentClip.durationTicks > 1e-4f)
                    ? Mathf.Clamp01(sampleTick / currentClip.durationTicks)
                    : 0f;
                bool useLockedAttackTarget = animState == GiantAnimState.AttackOnce && hasAttackTargetWorldXZ;
                var bezierCtx = new BezierChainStepContext
                {
                    BodyWorldAngleDeg = currentHeadingDeg,
                    ActiveClipDefName = currentClip?.defName,
                    ActiveClipNormalizedTime = clipNormTime,
                    HasAttackTarget = useLockedAttackTarget || hasAimTarget || hasAttackTargetWorldXZ,
                    AttackTargetWorldXZ = useLockedAttackTarget
                        ? attackTargetWorldXZ
                        : (hasAimTarget ? aimTargetXZ : attackTargetWorldXZ),
                    SleepCurlProgress01 = sleepCurlProgress01,
                    SleepCurlSideSign = sleepCurlSideSign,
                    IsDeadOrDowned = pawnDead || pawnDowned
                };
                if (hasBeziers && !ManualOverrideActive && animState != GiantAnimState.MorphLocked)
                {
                    for (int c = 0; c < bezierRuntimes.Length; c++)
                    {
                        if (sleepCurlProgress01 >= 0.995f
                            || ((dailyIngestWeight01 >= 0.95f || dailySocialWeight01 >= 0.95f) && !IsLocomotingWithGrace))
                        {
                            bezierRuntimes[c]?.Reset();
                            continue;
                        }
                        if (animState != GiantAnimState.Death &&
                            animState != GiantAnimState.Downed &&
                            partCombat != null &&
                            c < RigDef.bezierChains.Count &&
                            partCombat.IsBezierChainDrivenByRawClip(RigDef.bezierChains[c]))
                        {
                            bezierRuntimes[c]?.Reset();
                            continue;
                        }
                        BezierChainStepContext chainCtx = bezierCtx;
                        if (partCombat != null &&
                            c < RigDef.bezierChains.Count &&
                            partCombat.TryGetBezierChainStrikeOverride(RigDef.bezierChains[c], out string partChainClip, out float partChainNorm, out Vector2 partChainTargetXZ))
                        {
                            chainCtx.ActiveClipDefName = partChainClip;
                            chainCtx.ActiveClipNormalizedTime = partChainNorm;
                            chainCtx.HasAttackTarget = true;
                            chainCtx.AttackTargetWorldXZ = partChainTargetXZ;
                        }
                        bezierRuntimes[c]?.Step(RigDef, rootXZ, currentHeadingDeg, finalLocalPoses, partMissingMask, deltaTicks, in chainCtx);
                    }
                    if (sleepCurlProgress01 > 0.001f)
                    {
                        GiantSleepCurlSolver.ApplySleepCurlToBezierBones(
                            RigDef,
                            finalLocalPoses,
                            partMissingMask,
                            sleepCurlProgress01,
                            sleepCurlSideSign,
                            sleepBreathTicks);
                    }
                    else if (dailyIngestWeight01 > 0.001f || dailySocialWeight01 > 0.001f)
                    {
                        GiantDailyActivityPoseSolver.ApplyDailyActivityToBezierBones(
                            RigDef,
                            finalLocalPoses,
                            partMissingMask,
                            dailyIngestWeight01,
                            dailySocialWeight01,
                            dailyActivityPhaseTicks);
                    }
                }

                if (hasSoftWhips && snapSoftWhips != null)
                {
                    float whipDt = ManualOverrideActive ? 0f : deltaTicks;
                    for (int s = 0; s < softWhipRuntimes.Length && s < snapSoftWhips.Length; s++)
                    {
                        SoftWhipRuntime rt = softWhipRuntimes[s];
                        bool tendrilMissing = softWhipTendrilMissingMask != null && s < softWhipTendrilMissingMask.Length && softWhipTendrilMissingMask[s];
                        if (rt != null)
                        {
                            bool anyPartnerTendrilIntact = false;
                            for (int p = 0; p < softWhipRuntimes.Length; p++)
                            {
                                if (p != s && (softWhipTendrilMissingMask == null || p >= softWhipTendrilMissingMask.Length || !softWhipTendrilMissingMask[p]))
                                {
                                    anyPartnerTendrilIntact = true;
                                    break;
                                }
                            }
                            rt.PartnerTendrilIntact = anyPartnerTendrilIntact;
                        }
                        SoftWhipRibbonSnapshot wSnap = snapSoftWhips[s];
                        BezierChainStepContext whipCtx = bezierCtx;
                        if (partCombat != null &&
                            s < RigDef.softWhips.Count &&
                            partCombat.TryGetSoftWhipStrikeOverride(RigDef.softWhips[s], out string partWhipClip, out float partWhipNorm, out Vector2 partWhipTargetXZ))
                        {
                            whipCtx.ActiveClipDefName = partWhipClip;
                            whipCtx.ActiveClipNormalizedTime = partWhipNorm;
                            whipCtx.HasAttackTarget = true;
                            whipCtx.AttackTargetWorldXZ = partWhipTargetXZ;
                        }
                        if (rt != null && rt.Step(RigDef, rootXZ, currentHeadingDeg, finalLocalPoses, partMissingMask, tendrilMissing, whipDt, in whipCtx))
                        {
                            rt.FillSnapshot(rootXZ, wSnap);
                            softWhipSnapshots[s] = wSnap;
                        }
                        else
                        {
                            wSnap.visible = false;
                            softWhipSnapshots[s] = null;
                        }
                    }
                }
            }

            if (!phaseCompResolved && Pawn != null)
            {
                cachedPhaseComp = Pawn.TryGetComp<CompGiantPhaseTransform>();
                cachedExtraShaderComp = null;
                if (Pawn.AllComps is List<ThingComp> allComps)
                {
                    for (int c = 0; c < allComps.Count; c++)
                    {
                        if (allComps[c] is IGiantShaderSnapshotComp sc && !ReferenceEquals(sc, cachedPhaseComp))
                        {
                            cachedExtraShaderComp = sc;
                            break;
                        }
                    }
                }
                phaseCompResolved = true;
            }

            bool hasShaderParams = false;
            Color glowCol = Color.white;
            Vector4 shaderParams = Vector4.zero;
            if (cachedPhaseComp != null)
            {
                cachedPhaseComp.ComputeShaderSnapshot(out glowCol, out shaderParams);
                hasShaderParams = true;
            }
            else if (cachedExtraShaderComp != null)
            {
                cachedExtraShaderComp.ComputeShaderSnapshot(out glowCol, out shaderParams);
                hasShaderParams = true;
            }

            GiantBoneViewSector[] snapSectors = nextSnap.activeSectors;
            if (RigDef.hasAnyDirectionalVariants)
            {
                SolveOrReuseWorldPoses(Vector2.zero);

                bool anySectorChanged = !sectorsInitialized;
                for (int i = 0; i < count; i++)
                {
                    BoneNodeDef bDef = nodes[i];
                    GiantBoneViewSector newSec;
                    if (bDef.HasDirectionalVariants)
                    {
                        int srcIdx = bDef.sectorSourceRuntimeIndex;
                        float srcAngle = (uint)srcIdx < (uint)count ? worldPoses[srcIdx].worldAngleDeg : worldPoses[i].worldAngleDeg;
                        newSec = RigDef.EvaluateSector(srcAngle, activeBoneSectors[i], applyHysteresis: sectorsInitialized);
                    }
                    else
                    {
                        newSec = GiantBoneViewSector.Dorsal;
                    }

                    if (newSec != activeBoneSectors[i])
                    {
                        anySectorChanged = true;
                    }
                    activeBoneSectors[i] = newSec;
                    RefreshActiveAlphaMask(i);
                }
                sectorsInitialized = true;

                if (anySectorChanged)
                {
                    localPoseSerial++;
                    anySectorChangedPendingFootprint = true;
                    if (createdRenderNodes != null && UnityData.IsInMainThread)
                    {
                        for (int r = 0; r < createdRenderNodes.Count; r++)
                        {
                            PawnRenderNode rn = createdRenderNodes[r];
                            if (rn is PawnRenderNode_GiantBone
                                || rn is PawnRenderNode_GiantRollingTrack
                                || rn is PawnRenderNode_GiantTrackWheel)
                            {
                                rn.requestRecache = true;
                            }
                        }
                    }
                }

                if (snapSectors != null)
                {
                    Array.Copy(activeBoneSectors, snapSectors, count);
                }
            }

            float[] snapTrackScroll = nextSnap.trackScrollCells;
            if (RigDef.rollingTracks != null && RigDef.rollingTracks.Count > 0 && trackScrollCells != null)
            {
                float curRootOffsetY = count > 0 ? finalLocalPoses[0].offset.y : 0f;
                if (!trackKinematicsInitialized)
                {
                    trackPrevHeadingDeg = currentHeadingDeg;
                    trackPrevRootOffsetY = curRootOffsetY;
                    pendingTrackWorldFwdDelta = 0f;
                    trackKinematicsInitialized = true;
                }
                else if (deltaTicks > 0f && !ManualOverrideActive)
                {
                    float rootAnimDelta = curRootOffsetY - trackPrevRootOffsetY;
                    float totalFwdDelta = pendingTrackWorldFwdDelta + rootAnimDelta;
                    float deltaHeadingCw = Mathf.DeltaAngle(trackPrevHeadingDeg, currentHeadingDeg);
                    for (int t = 0; t < RigDef.rollingTracks.Count && t < trackScrollCells.Length; t++)
                    {
                        GiantRollingTrackDef tDef = RigDef.rollingTracks[t];
                        if (tDef == null)
                        {
                            continue;
                        }
                        GiantRollingTrackKinematics.ComputeDifferentialStep(
                            totalFwdDelta,
                            deltaHeadingCw,
                            tDef.gaugeHalfWidthCells,
                            out float lDelta,
                            out float rDelta);
                        trackScrollCells[t] += (tDef.sideSign < 0f ? lDelta : rDelta);
                    }
                    trackPrevHeadingDeg = currentHeadingDeg;
                    trackPrevRootOffsetY = curRootOffsetY;
                    pendingTrackWorldFwdDelta = 0f;
                }

                if (snapTrackScroll != null)
                {
                    Array.Copy(trackScrollCells, snapTrackScroll, trackScrollCells.Length);
                }
            }

            // Publish zero-allocation ring snapshot for ParallelPreDraw worker threads
            Array.Copy(finalLocalPoses, nextSnap.localPoses, count);
            if (nextSnap.turretRecoilOffsets != null && currentTurretRecoilOffsets != null)
            {
                Array.Copy(currentTurretRecoilOffsets, nextSnap.turretRecoilOffsets, count);
            }
            Array.Copy(partMissingMask, nextSnap.missingMask, count);
            if (nextSnap.skinnedMeshMissingMask != null && skinnedMeshMissingMask != null)
            {
                Array.Copy(skinnedMeshMissingMask, nextSnap.skinnedMeshMissingMask, nextSnap.skinnedMeshMissingMask.Length);
            }
            if (partHealthPct != null && partHealthPct.Length == count)
            {
                Array.Copy(partHealthPct, nextSnap.partHealthPct, count);
            }
            else
            {
                Array.Fill(nextSnap.partHealthPct, 1f);
            }
            if (UnityData.IsInMainThread && Find.TickManager != null)
            {
                latestPublishedGameTick = Find.TickManager.TicksGame;
            }
            nextSnap.version = ++snapshotPublishVersion;
            nextSnap.headingDeg = currentHeadingDeg;
            nextSnap.hasMechShaderParams = hasShaderParams;
            nextSnap.phaseGlowColor = glowCol;
            nextSnap.mechShaderParams = shaderParams;
            bool activeHighAltitudeOverride = !pawnDead && !pawnDowned && Pawn != null
                && GiantMotionOverrideRegistry.HasActiveHighAltitudeOverride(Pawn);
            nextSnap.isDead = pawnDead;
            nextSnap.isDowned = pawnDowned;
            nextSnap.isFlyingEntry = activeFlightEntry || activeFlightVisual || activeHighAltitudeOverride;
            nextSnap.flightVisualOffsetXZ = activeHighAltitudeOverride ? Vector2.zero : flightOffsetXZ;
            nextSnap.flightAltitudeBoost = (pawnDead || pawnDowned)
                ? 0f
                : ComputeAirborneRenderAltitudeBoost(
                    activeFlightEntry || activeHighAltitudeOverride,
                    activeFlightVisual ? flightLocomotion : null,
                    Pawn != null ? Pawn.thingIDNumber : 0);
            latestSnapshot = nextSnap;
            pendingDynamicMeshUpload = true;

            if (UnityData.IsInMainThread && (!initialDynamicMeshUploaded || Pawn == null || !Pawn.Spawned || Pawn.Dead))
            {
                FlushPendingDynamicMeshesOnMainThread(ignoreViewportCull: true);
            }
        }

    }
}
