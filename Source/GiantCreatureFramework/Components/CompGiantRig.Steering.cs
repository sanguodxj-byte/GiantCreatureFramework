using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Path and gaze targets, heading changes, and articulated steering.
    public partial class CompGiantRig
    {
        private bool TryGetPathLookDirectionDeg(Vector3 simPos, int nodesAhead, out float dirDeg)
        {
            Verse.AI.PawnPath curPath = Pawn?.pather?.curPath;
            if (curPath == null || !curPath.Found || curPath.Finished)
            {
                dirDeg = 0f;
                return false;
            }

            int nodesLeft = curPath.NodesLeftCount;
            if (nodesLeft <= 0)
            {
                dirDeg = 0f;
                return false;
            }

            int i0 = Mathf.Clamp(nodesAhead - 1, 0, nodesLeft - 1);
            int i1 = Mathf.Clamp(nodesAhead, 0, nodesLeft - 1);
            int i2 = Mathf.Clamp(nodesAhead + 2, 0, nodesLeft - 1);

            Vector3 accum = Vector3.zero;
            AccumulatePathSample(curPath, i0, nodesLeft, 0.25f, simPos, ref accum);
            AccumulatePathSample(curPath, i1, nodesLeft, 0.50f, simPos, ref accum);
            AccumulatePathSample(curPath, i2, nodesLeft, 0.25f, simPos, ref accum);

            if (accum.sqrMagnitude <= 0.001f)
            {
                Vector3 d = curPath.Peek(i1).ToVector3Shifted() - simPos;
                d.y = 0f;
                if (d.sqrMagnitude <= 0.04f)
                {
                    dirDeg = 0f;
                    return false;
                }
                dirDeg = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                return true;
            }

            dirDeg = Mathf.Atan2(accum.x, accum.z) * Mathf.Rad2Deg;
            return true;
        }

        private static void AccumulatePathSample(
            Verse.AI.PawnPath curPath,
            int index,
            int nodesLeft,
            float weight,
            Vector3 simPos,
            ref Vector3 accum)
        {
            Vector3 d = curPath.Peek(index).ToVector3Shifted() - simPos;
            d.y = 0f;
            float sqr = d.sqrMagnitude;
            float minSqr = index == nodesLeft - 1 ? 0.09f : 0.36f;
            if (sqr > minSqr)
            {
                accum += (d / Mathf.Sqrt(sqr)) * weight;
            }
        }

        private bool IsValidHostileGazeTarget(Thing t, Vector3 simPos, float maxDistSq)
        {
            if (t == null || t.Destroyed || !t.Spawned || t.Map != Pawn.Map || !GiantPartCombatRuntime.IsValidHostileThing(Pawn, t))
            {
                return false;
            }
            if (t is Pawn tp && (tp.Dead || tp.Downed))
            {
                return false;
            }
            Vector3 pos = t is Pawn p ? GetSimTickPos(p) : t.DrawPos;
            Vector3 d = pos - simPos;
            d.y = 0f;
            return d.sqrMagnitude > 0.04f && d.sqrMagnitude <= maxDistSq;
        }

        private Thing ResolveStableGazeHostileTarget(Vector3 simPos, float maxDist)
        {
            int now = Current.Game?.tickManager?.TicksGame ?? 0;
            float maxDistSq = maxDist * maxDist;
            float keepDistSq = (maxDist + 3f) * (maxDist + 3f);

            Verse.AI.Job curJob = Pawn.CurJob;
            if (curJob != null
                && curJob.def == JobDefOf.AttackMelee
                && curJob.targetA.Thing is Thing jobTarget
                && IsValidHostileGazeTarget(jobTarget, simPos, maxDistSq))
            {
                if (jobTarget is Pawn
                    || !(lockedLookTargetRef is Pawn)
                    || !IsValidHostileGazeTarget(lockedLookTargetRef, simPos, keepDistSq))
                {
                    lockedLookTargetRef = jobTarget;
                    lockedLookTargetUntilTick = now + 45;
                    return jobTarget;
                }
                return lockedLookTargetRef;
            }

            if (now < lockedLookTargetUntilTick && IsValidHostileGazeTarget(lockedLookTargetRef, simPos, keepDistSq))
            {
                return lockedLookTargetRef;
            }

            Thing pursued = ResolvePursuedHostileTarget();
            Thing enemy = Pawn.mindState?.enemyTarget;
            Thing cand = null;
            if (pursued is Pawn && IsValidHostileGazeTarget(pursued, simPos, maxDistSq))
            {
                cand = pursued;
            }
            else if (enemy is Pawn && IsValidHostileGazeTarget(enemy, simPos, maxDistSq))
            {
                cand = enemy;
            }
            else if (IsValidHostileGazeTarget(pursued, simPos, maxDistSq))
            {
                cand = pursued;
            }
            else if (IsValidHostileGazeTarget(enemy, simPos, maxDistSq))
            {
                cand = enemy;
            }

            lockedLookTargetRef = cand;
            lockedLookTargetUntilTick = cand != null ? now + 50 : -999;
            return cand;
        }

        private void StepIdleVigilanceGaze(SteeringLookChainDef chain, Vector3 simPos, float bodyHeadingDeg, float deltaTicks)
        {
            idleVigilanceRemainingTicks -= Mathf.Max(0f, deltaTicks);
            if (idleVigilanceRemainingTicks > 0f)
            {
                return;
            }

            idleVigilanceStepCounter++;
            int gameTicks = Current.Game?.tickManager?.TicksGame ?? (int)continuousSteeringTicks;
            uint hash = (uint)((Pawn.thingIDNumber * 73856093) ^ (idleVigilanceStepCounter * 19349663) ^ (gameTicks * 83492791));
            hash ^= hash >> 13;
            hash *= 1274126177u;
            hash ^= hash >> 16;

            if (idleVigilanceInGlance)
            {
                // Return to motionless predator fixation along forward axis
                idleVigilanceInGlance = false;
                idleVigilanceOffsetDeg = 0f;
                int minHold = Mathf.Max(60, chain.idlePoiHoldMinTicks);
                int maxHold = Mathf.Max(minHold + 10, chain.idlePoiHoldMaxTicks);
                idleVigilanceRemainingTicks = minHold + (int)(hash % (uint)(maxHold - minHold + 1));
                return;
            }

            // Saccadic glance toward a nearby moving creature/pawn or forward-flank surveillance sector
            idleVigilanceInGlance = true;
            float maxScan = Mathf.Clamp(chain.idlePoiScanMaxAngleDeg, 4f, 45f);
            if (!TryPickNearbyPoiGlanceOffset(simPos, bodyHeadingDeg, maxScan, hash, out float pickedOffset))
            {
                float mag = Mathf.Lerp(0.42f, 0.95f, (hash & 0xFFu) / 255f) * maxScan;
                float sign = ((idleVigilanceStepCounter + ((int)(hash >> 9) & 1)) & 1) == 0 ? 1f : -1f;
                pickedOffset = mag * sign;
            }
            idleVigilanceOffsetDeg = Mathf.Clamp(pickedOffset, -maxScan, maxScan);

            int minGlance = Mathf.Max(25, chain.idlePoiGlanceMinTicks);
            int maxGlance = Mathf.Max(minGlance + 10, chain.idlePoiGlanceMaxTicks);
            idleVigilanceRemainingTicks = minGlance + (int)((hash >> 8) % (uint)(maxGlance - minGlance + 1));
        }

        private bool TryPickNearbyPoiGlanceOffset(Vector3 simPos, float bodyHeadingDeg, float maxScanDeg, uint seed, out float offsetDeg)
        {
            offsetDeg = 0f;
            var mapPawns = Pawn?.Map?.mapPawns?.AllPawnsSpawned;
            if (mapPawns == null || mapPawns.Count <= 1)
            {
                return false;
            }

            Pawn bestPawn = null;
            float bestScore = 0f;
            int count = mapPawns.Count;
            int startIdx = (int)(seed % (uint)count);
            int checks = Mathf.Min(count, 24);
            for (int n = 0; n < checks; n++)
            {
                Pawn other = mapPawns[(startIdx + n) % count];
                if (other == null || other == Pawn || other.Dead || other.Downed || !other.Spawned)
                {
                    continue;
                }
                Vector3 d = other.DrawPos - simPos;
                d.y = 0f;
                float distSq = d.sqrMagnitude;
                if (distSq < 2.25f || distSq > 324f)
                {
                    continue;
                }
                float ang = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                float delta = Mathf.DeltaAngle(bodyHeadingDeg, ang);
                if (Mathf.Abs(delta) > 65f)
                {
                    continue;
                }
                float score = (1f / (4f + Mathf.Sqrt(distSq))) * (other.pather != null && other.pather.Moving ? 1.85f : 1.0f);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPawn = other;
                    offsetDeg = Mathf.Clamp(delta, -maxScanDeg, maxScanDeg);
                }
            }
            return bestPawn != null && Mathf.Abs(offsetDeg) > 2.5f;
        }

        public static bool StepPivotTurnHysteresis(
            bool currentlyPivoting,
            float absAngleErrorDeg,
            float enterAngleDeg,
            float exitAngleDeg)
        {
            float safeExit = Mathf.Clamp(exitAngleDeg, 2f, 45f);
            float safeEnter = Mathf.Max(safeExit + 4f, enterAngleDeg);
            if (currentlyPivoting)
            {
                return absAngleErrorDeg > safeExit;
            }
            return absAngleErrorDeg > safeEnter;
        }

        public static float ComputeEffectiveBodyTurnSpeed(
            float baseTurnSpeedDegPerTick,
            float movingTurnSpeedFactor,
            bool isMovingAlongGroundPath,
            bool isPivotTurningInPlace,
            bool hasActiveLinearDashDir)
        {
            float safeBase = Mathf.Max(0.1f, baseTurnSpeedDegPerTick);
            if (hasActiveLinearDashDir)
            {
                return safeBase * 1.35f;
            }
            if (isMovingAlongGroundPath && !isPivotTurningInPlace)
            {
                return safeBase * Mathf.Clamp(movingTurnSpeedFactor, 0.25f, 1.0f);
            }
            return safeBase;
        }

        public bool EvaluateGroundedPivotTurnGate(Verse.AI.Pawn_PathFollower pather)
        {
            if (RigDef == null
                || !RigDef.enablePivotTurnBeforeMove
                || vehicleLocomotion != null
                || flightVehicleLocomotion != null
                || IsAirborneOrTakingOff
                || flightEntryActive
                || Pawn == null
                || !Pawn.Spawned
                || Pawn.Dead
                || Pawn.Downed
                || pather == null
                || !pather.Moving
                || (Pawn.stances != null && Pawn.stances.FullBodyBusy)
                || IsBiologicalSleepingNow()
                || (partCombat != null && partCombat.HasActiveFullHullSpinClip())
                || GiantMotionOverrideRegistry.TryGetActiveWorldPos(Pawn, out _))
            {
                isPivotTurningInPlace = false;
                hasPivotDesiredBearing = false;
                lastSampledPathRemainingCells = 0f;
                return false;
            }

            Vector3 simPos = GetSimTickPos(Pawn);
            Vector2 fromXZ = new Vector2(simPos.x, simPos.z);
            Vector2 prevPt = fromXZ;
            float accumLen = 0f;
            const float targetCarrotCells = 2.15f;
            bool foundCarrot = false;
            Vector2 carrotXZ = fromXZ;
            bool hasImmediateNode = false;
            Vector2 immediateXZ = fromXZ;

            void ProcessNode(IntVec3 cell)
            {
                Vector2 wp = new Vector2(cell.x + 0.5f, cell.z + 0.5f);
                Vector2 seg = wp - prevPt;
                float segLen = seg.magnitude;
                if (segLen <= 1e-4f)
                {
                    return;
                }
                if (!hasImmediateNode && (wp - fromXZ).sqrMagnitude > 0.04f)
                {
                    immediateXZ = wp;
                    hasImmediateNode = true;
                }
                if (!foundCarrot && accumLen + segLen >= targetCarrotCells)
                {
                    float t = Mathf.Clamp01((targetCarrotCells - accumLen) / segLen);
                    carrotXZ = Vector2.Lerp(prevPt, wp, t);
                    foundCarrot = true;
                }
                accumLen += segLen;
                prevPt = wp;
            }

            if (pather.nextCell.IsValid)
            {
                ProcessNode(pather.nextCell);
            }

            Verse.AI.PawnPath curPath = pather.curPath;
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
                    ProcessNode(node);
                }
            }
            else if (pather.Destination.IsValid)
            {
                ProcessNode(pather.Destination.Cell);
            }

            lastSampledPathRemainingCells = accumLen;

            // Allow final sub-cell arrival into destination cell center so PatherArrived() never stalls
            if (accumLen <= 0.38f)
            {
                isPivotTurningInPlace = false;
                hasPivotDesiredBearing = false;
                return false;
            }

            if (!foundCarrot)
            {
                carrotXZ = prevPt;
            }

            // Blend immediate step vector with 2-cell carrot so corner walls are respected while avoiding 45-deg grid zig-zag jitter
            Vector2 aimTargetXZ = hasImmediateNode
                ? Vector2.Lerp(immediateXZ, carrotXZ, 0.35f)
                : carrotXZ;
            Vector2 toAim = aimTargetXZ - fromXZ;
            if (toAim.sqrMagnitude <= 0.01f)
            {
                toAim = carrotXZ - fromXZ;
            }
            if (toAim.sqrMagnitude <= 0.01f)
            {
                isPivotTurningInPlace = false;
                hasPivotDesiredBearing = false;
                return false;
            }

            float desiredBearing = SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(toAim.x, toAim.y) * Mathf.Rad2Deg);

            // Short <= 2.8-cell tactical melee spacing sidestep/backstep while engaged with a close enemy:
            // never force the giant to turn its back 180 deg away from the enemy or deadlock PatherTick against PartCombat!
            if (accumLen <= 2.8f && partCombat != null &&
                partCombat.TryGetPrimaryCombatLookTargetXZ(out Vector2 closeEnemyXZ, out bool closeRotHull, out float closeOptHdg) &&
                closeRotHull && (closeEnemyXZ - fromXZ).sqrMagnitude <= 144f &&
                Mathf.Abs(Mathf.DeltaAngle(desiredBearing, closeOptHdg)) > 70f)
            {
                isPivotTurningInPlace = false;
                hasPivotDesiredBearing = false;
                return false;
            }

            float refHeading = headingInitialized ? currentHeadingDeg : Pawn.Rotation.AsAngle;
            float absErr = Mathf.Abs(Mathf.DeltaAngle(refHeading, desiredBearing));

            isPivotTurningInPlace = StepPivotTurnHysteresis(
                isPivotTurningInPlace,
                absErr,
                RigDef.pivotTurnEnterAngleDeg,
                RigDef.pivotTurnExitAngleDeg);

            if (isPivotTurningInPlace)
            {
                hasPivotDesiredBearing = true;
                pivotDesiredBearingDeg = desiredBearing;
                movingPauseGraceTicks = 0;
                return true;
            }

            hasPivotDesiredBearing = false;
            return false;
        }

        private void UpdateHeadingAndSteering(float deltaTicks, float movedDist)
        {
            lastStepHeadingDeltaDeg = 0f;
            if (Pawn.Dead || Pawn.Downed)
            {
                currentSteeringCtx.AnimState = GiantAnimState.Downed;
                currentSteeringCtx.IsMoving = false;
                currentSteeringCtx.HasExplicitLookTarget = false;
                currentSteeringCtx.IsCombatTarget = false;
                currentSteeringCtx.HasLookTargetWorldXZ = false;
                currentSteeringCtx.MovedDistanceCells = 0f;
                currentSteeringCtx.ContinuousTicks = continuousSteeringTicks;
                return;
            }

            if (GiantFastLookup.TryGetCockpit(Pawn) is CompGiantCockpit dormantCockpit && dormantCockpit.IsDormantUnmanned)
            {
                targetHeadingDeg = currentHeadingDeg;
                currentSteeringCtx.AnimState = GiantAnimState.Idle;
                currentSteeringCtx.IsMoving = false;
                currentSteeringCtx.HasExplicitLookTarget = false;
                currentSteeringCtx.IsCombatTarget = false;
                currentSteeringCtx.HasLookTargetWorldXZ = false;
                currentSteeringCtx.MovedDistanceCells = 0f;
                currentSteeringCtx.ContinuousTicks = continuousSteeringTicks;
                return;
            }

            if (flightEntryActive)
            {
                currentHeadingDeg = flightHeadingDeg;
                targetHeadingDeg = flightHeadingDeg;
                currentSteeringCtx.AnimState = GiantAnimState.FlightEntry;
                currentSteeringCtx.DesiredBodyHeadingDeg = flightHeadingDeg;
                currentSteeringCtx.DesiredLookHeadingDeg = flightHeadingDeg;
                currentSteeringCtx.IsMoving = true;
                currentSteeringCtx.HasExplicitLookTarget = false;
                currentSteeringCtx.IsCombatTarget = false;
                currentSteeringCtx.HasLookTargetWorldXZ = false;
                currentSteeringCtx.MovedDistanceCells = 0.08f;
                currentSteeringCtx.ContinuousTicks = continuousSteeringTicks;
                return;
            }

            if (!IsPatherMovingNotBusy)
            {
                isPivotTurningInPlace = false;
                hasPivotDesiredBearing = false;
                lastSampledPathRemainingCells = 0f;
            }

            Vector3 simPos = GetSimTickPos(Pawn);
            float desiredBodyDeg = Pawn.Rotation.AsAngle;
            float desiredLookDeg = desiredBodyDeg;
            bool desiredBodyFound = false;
            bool hasExplicitLookTarget = false;
            bool isCombatTarget = false;
            bool hasLookTargetWorldXZ = false;
            Vector2 lookTargetWorldXZ = Vector2.zero;
            bool isMovingNow = IsActivelyLocomoting;
            bool isLocomoting = IsLocomotingWithGrace;
            bool sleepingNow = IsBiologicalSleepingNow();
            if (sleepingNow && !wasBiologicalSleeping)
            {
                wasBiologicalSleeping = true;
                sleepStartHeadingDeg = headingInitialized ? currentHeadingDeg : Pawn.Rotation.AsAngle;
                sleepCurlSideSign = GiantSleepCurlSolver.ChooseBestSleepSideSign(RigDef, sleepStartHeadingDeg);
                sleepIdleClockTicks = (Current.Game?.tickManager != null ? Current.Game.tickManager.TicksGame : continuousSteeringTicks);
            }
            else if (!sleepingNow && wasBiologicalSleeping)
            {
                wasBiologicalSleeping = false;
            }

            if (deltaTicks > 0f)
            {
                float targetSleep01 = sleepingNow ? 1f : 0f;
                float sleepStep = sleepingNow
                    ? (deltaTicks / GiantSleepCurlSolver.CurlInDurationTicks)
                    : (deltaTicks / GiantSleepCurlSolver.UncurlDurationTicks);
                sleepCurlProgress01 = Mathf.MoveTowards(sleepCurlProgress01, targetSleep01, sleepStep);
                if (sleepCurlProgress01 > 0.001f)
                {
                    sleepBreathTicks += deltaTicks;
                    float idleRate = Mathf.Lerp(1f, GiantSleepCurlSolver.DeepSleepIdleRateMultiplier, sleepCurlProgress01);
                    sleepIdleClockTicks += deltaTicks * idleRate;
                }
                else
                {
                    sleepBreathTicks = 0f;
                }
            }

            StepBiologicalDailyActivityWeights(deltaTicks, isMovingNow, isLocomoting, sleepingNow);

            if (sleepingNow)
            {
                float uTurn = GiantSleepCurlSolver.EaseInOutCubic(sleepCurlProgress01);
                float targetSleepHeading = GiantSleepCurlSolver.GetTargetSleepHeadingDeg(RigDef, sleepCurlSideSign);
                float sleepTurnDelta = Mathf.DeltaAngle(sleepStartHeadingDeg, targetSleepHeading);
                desiredBodyDeg = SkeletalKinematics.NormalizeAngle360(sleepStartHeadingDeg + sleepTurnDelta * uTurn);
                desiredLookDeg = desiredBodyDeg;
                desiredBodyFound = true;
            }
            bool isRearTailClip = animState == GiantAnimState.AttackOnce
                && activeOneShotAnim?.defName != null
                && (activeOneShotAnim.defName.IndexOf("Tail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    activeOneShotAnim.defName.IndexOf("Thruster", StringComparison.OrdinalIgnoreCase) >= 0);

            bool isBusyRearTool = isRearTailClip;
            bool hasActiveLinearDashDir = false;
            float maxTurretArc = 135f;
            bool canHoldHullRanged = RigDef != null && RigDef.HasAnyIntactTurretBone(IsBoneIntact, out maxTurretArc);
            if (!desiredBodyFound &&
                GiantMotionOverrideRegistry.TryGetActiveDirectionXZ(Pawn, out Vector2 activeChargeDirXZ))
            {
                hasActiveLinearDashDir = GiantMotionOverrideRegistry.TryGetActiveWorldPos(Pawn, out _);
                float chargeHeading = Mathf.Atan2(activeChargeDirXZ.x, activeChargeDirXZ.y) * Mathf.Rad2Deg;
                desiredBodyDeg = chargeHeading;
                desiredLookDeg = chargeHeading;
                desiredBodyFound = true;
                hasExplicitLookTarget = true;
                isCombatTarget = !hasActiveLinearDashDir;
                hasLookTargetWorldXZ = true;
                lookTargetWorldXZ = new Vector2(simPos.x, simPos.z) + activeChargeDirXZ * 40f;
            }
            else if (Pawn.stances?.curStance is Stance_Busy busy && busy.focusTarg.IsValid)
            {
                if (!isBusyRearTool && busy.verb is Verb_MeleeAttack busyMelee && busyMelee.tool != null)
                {
                    string busyTag = GiantRigDef.BuildToolMatchTag(busyMelee.tool).ToLowerInvariant();
                    isBusyRearTool = busyTag.Contains("tail") || busyTag.Contains("fluke") ||
                                     busyTag.Contains("stinger") || busyTag.Contains("thruster") ||
                                     busyTag.Contains("rear") || busyTag.Contains("abdomen");
                }

                Vector3 targPos = busy.focusTarg.HasThing
                    ? (busy.focusTarg.Thing is Pawn targPawn ? GetSimTickPos(targPawn) : busy.focusTarg.Thing.DrawPos)
                    : busy.focusTarg.Cell.ToVector3Shifted();
                Vector3 dir = targPos - simPos;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                {
                    float attackAngle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                    if (canHoldHullRanged && busy.verb != null && !busy.verb.IsMeleeAttack
                        && Mathf.Abs(Mathf.DeltaAngle(currentHeadingDeg, attackAngle)) <= maxTurretArc)
                    {
                        desiredBodyDeg = currentHeadingDeg;
                        desiredLookDeg = attackAngle;
                        desiredBodyFound = true;
                        hasExplicitLookTarget = true;
                        isCombatTarget = true;
                        hasLookTargetWorldXZ = true;
                        lookTargetWorldXZ = new Vector2(targPos.x, targPos.z);
                    }
                    else if (isBusyRearTool && Mathf.Abs(Mathf.DeltaAngle(currentHeadingDeg, attackAngle)) > 110f)
                    {
                        desiredBodyDeg = currentHeadingDeg;
                        desiredLookDeg = currentHeadingDeg;
                        desiredBodyFound = true;
                    }
                    else
                    {
                        desiredBodyDeg = attackAngle;
                        desiredLookDeg = attackAngle;
                        desiredBodyFound = true;
                        hasExplicitLookTarget = true;
                        isCombatTarget = true;
                        hasLookTargetWorldXZ = true;
                        lookTargetWorldXZ = new Vector2(targPos.x, targPos.z);
                    }
                }
            }
            else if (animState == GiantAnimState.AttackOnce && hasAttackTargetWorldXZ)
            {
                float dx = attackTargetWorldXZ.x - simPos.x;
                float dz = attackTargetWorldXZ.y - simPos.z;
                if (dx * dx + dz * dz > 0.01f)
                {
                    float attackAngle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
                    bool isMeleeClip = activeOneShotAnim != null
                        && RigDef?.meleeAttackAnims != null
                        && RigDef.meleeAttackAnims.Contains(activeOneShotAnim);
                    if (canHoldHullRanged && !isMeleeClip
                        && Mathf.Abs(Mathf.DeltaAngle(currentHeadingDeg, attackAngle)) <= maxTurretArc)
                    {
                        desiredBodyDeg = currentHeadingDeg;
                        desiredLookDeg = attackAngle;
                        desiredBodyFound = true;
                        hasExplicitLookTarget = true;
                        isCombatTarget = true;
                        hasLookTargetWorldXZ = true;
                        lookTargetWorldXZ = attackTargetWorldXZ;
                    }
                    else if (isRearTailClip && Mathf.Abs(Mathf.DeltaAngle(currentHeadingDeg, attackAngle)) > 110f)
                    {
                        desiredBodyDeg = currentHeadingDeg;
                        desiredLookDeg = currentHeadingDeg;
                        desiredBodyFound = true;
                    }
                    else
                    {
                        desiredBodyDeg = attackAngle;
                        desiredLookDeg = attackAngle;
                        desiredBodyFound = true;
                        hasExplicitLookTarget = true;
                        isCombatTarget = true;
                        hasLookTargetWorldXZ = true;
                        lookTargetWorldXZ = attackTargetWorldXZ;
                    }
                }
            }

            // Priority 1.9: Grounded pivot-turn-before-move gate (turns in place via TurnLeft/TurnRight before advancing along path)
            if (!desiredBodyFound && isPivotTurningInPlace && hasPivotDesiredBearing)
            {
                desiredBodyDeg = pivotDesiredBearingDeg;
                desiredLookDeg = pivotDesiredBearingDeg;
                desiredBodyFound = true;
                hasExplicitLookTarget = true;

                SteeringConfigDef pivotSteerCfg = steeringRuntime?.Config;
                int pivotGazeAhead = Math.Max(3, pivotSteerCfg?.gazePathLookAheadCells ?? 6);
                if (TryGetPathLookDirectionDeg(simPos, pivotGazeAhead, out float pivotPathGaze))
                {
                    desiredLookDeg = pivotPathGaze;
                }
            }

            // Priority 2: Smooth locomotion velocity vector + multi-cell path corner look-ahead
            if (!desiredBodyFound && isLocomoting)
            {
                float stepAngle = headingInitialized ? targetHeadingDeg : Pawn.Rotation.AsAngle;
                bool hasStepAngle = !isMovingNow;
                if (isMovingNow)
                {
                    IntVec3 step = Pawn.pather.nextCell - Pawn.Position;
                    if (step.x != 0 || step.z != 0)
                    {
                        stepAngle = Mathf.Atan2(step.x, step.z) * Mathf.Rad2Deg;
                        hasStepAngle = true;
                    }
                    else
                    {
                        Vector3 nextWorld = Pawn.pather.nextCell.ToVector3Shifted();
                        Vector3 dir = nextWorld - simPos;
                        dir.y = 0f;
                        if (dir.sqrMagnitude > 0.002f)
                        {
                            stepAngle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                            hasStepAngle = true;
                        }
                    }
                }

                if (hasStepAngle)
                {
                    if (!IsCurrentlyAirborne
                        && lastSampledPathRemainingCells > 0.01f
                        && lastSampledPathRemainingCells <= 2.8f
                        && partCombat != null
                        && partCombat.TryGetPrimaryCombatLookTargetXZ(out Vector2 shortEnemyXZ, out bool shortRotHull, out float shortOptHdg)
                        && shortRotHull
                        && (shortEnemyXZ - new Vector2(simPos.x, simPos.z)).sqrMagnitude <= 144f
                        && Mathf.Abs(Mathf.DeltaAngle(stepAngle, shortOptHdg)) > 70f)
                    {
                        desiredBodyDeg = shortOptHdg;
                        desiredLookDeg = shortOptHdg;
                        desiredBodyFound = true;
                        hasExplicitLookTarget = true;
                        isCombatTarget = true;
                        hasLookTargetWorldXZ = true;
                        lookTargetWorldXZ = shortEnemyXZ;
                    }
                    else
                    {
                        desiredBodyDeg = stepAngle;
                        desiredLookDeg = stepAngle;
                        desiredBodyFound = true;
                    }
                }

                SteeringConfigDef steerCfg = steeringRuntime?.Config;
                int bodyAhead = Math.Max(1, steerCfg?.bodyPathLookAheadCells ?? 3);
                float bodyAheadWeight = Mathf.Clamp01(steerCfg?.bodyPathLookAheadWeight ?? 0.35f);
                int gazeAhead = Math.Max(bodyAhead, steerCfg?.gazePathLookAheadCells ?? 6);

                if (isMovingNow && desiredBodyFound && bodyAheadWeight > 0.001f && TryGetPathLookDirectionDeg(simPos, bodyAhead, out float bodyPeekAngle))
                {
                    desiredBodyDeg = stepAngle + Mathf.DeltaAngle(stepAngle, bodyPeekAngle) * bodyAheadWeight;
                }

                bool hasPathGaze = TryGetPathLookDirectionDeg(simPos, gazeAhead, out float pathGazeAngle);
                float baseLocoGaze = hasPathGaze ? pathGazeAngle : desiredBodyDeg;

                // Smoothly blend from path look-ahead toward stable combat target without sharp cutoffs or backward neck twists
                Thing pursued = ResolveStableGazeHostileTarget(simPos, 20f);
                if (pursued != null)
                {
                    Vector3 preyPos = pursued is Pawn pp ? GetSimTickPos(pp) : pursued.DrawPos;
                    Vector3 toPrey = preyPos - simPos;
                    toPrey.y = 0f;
                    float distSq = toPrey.sqrMagnitude;
                    if (distSq > 0.04f)
                    {
                        float dist = Mathf.Sqrt(distSq);
                        float preyAngle = Mathf.Atan2(toPrey.x, toPrey.z) * Mathf.Rad2Deg;
                        float distW = GiantSteeringRuntime.SmoothRamp01(19f, 9.5f, dist);
                        float offBow = Mathf.Abs(Mathf.DeltaAngle(baseLocoGaze, preyAngle));
                        float bowW = 1f - GiantSteeringRuntime.SmoothRamp01(75f, 130f, offBow);
                        float wPrey = distW * bowW;

                        desiredLookDeg = baseLocoGaze + Mathf.DeltaAngle(baseLocoGaze, preyAngle) * wPrey;
                        hasExplicitLookTarget = hasPathGaze || wPrey > 0.05f;
                        isCombatTarget = wPrey > 0.35f;
                    }
                    else if (hasPathGaze)
                    {
                        desiredLookDeg = pathGazeAngle;
                        hasExplicitLookTarget = true;
                    }
                }
                else if (hasPathGaze)
                {
                    desiredLookDeg = pathGazeAngle;
                    hasExplicitLookTarget = true;
                }
            }

            // Priority 2.5: Part-combat target alignment (turret traverse within arc, or continuous 360-deg hull rotation to maximize firepower)
            if (!desiredBodyFound && !isLocomoting && partCombat != null &&
                partCombat.TryGetPrimaryCombatLookTargetXZ(out Vector2 partTargetXZ, out bool shouldRotateHull, out float optimalHullHeadingDeg))
            {
                float dx = partTargetXZ.x - simPos.x;
                float dz = partTargetXZ.y - simPos.z;
                if (dx * dx + dz * dz > 0.04f)
                {
                    float attackAngle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
                    float absDelta = Mathf.Abs(Mathf.DeltaAngle(currentHeadingDeg, attackAngle));
                    if (!shouldRotateHull && (!canHoldHullRanged || absDelta <= maxTurretArc))
                    {
                        desiredBodyDeg = currentHeadingDeg;
                        desiredLookDeg = canHoldHullRanged ? attackAngle : currentHeadingDeg;
                        desiredBodyFound = true;
                        hasExplicitLookTarget = canHoldHullRanged;
                        isCombatTarget = canHoldHullRanged;
                        hasLookTargetWorldXZ = canHoldHullRanged;
                        lookTargetWorldXZ = canHoldHullRanged ? partTargetXZ : Vector2.zero;
                    }
                    else
                    {
                        desiredBodyDeg = optimalHullHeadingDeg;
                        desiredLookDeg = attackAngle;
                        desiredBodyFound = true;
                        hasExplicitLookTarget = true;
                        isCombatTarget = true;
                        hasLookTargetWorldXZ = true;
                        lookTargetWorldXZ = partTargetXZ;
                    }
                }
            }
            else if (isLocomoting && partCombat != null &&
                     partCombat.TryGetPrimaryCombatLookTargetXZ(out Vector2 moveTargetXZ, out _))
            {
                float dx = moveTargetXZ.x - simPos.x;
                float dz = moveTargetXZ.y - simPos.z;
                if (dx * dx + dz * dz > 0.09f)
                {
                    float aimAngle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
                    float maxAim = canHoldHullRanged ? maxTurretArc : 95f;
                    if (Mathf.Abs(Mathf.DeltaAngle(currentHeadingDeg, aimAngle)) <= maxAim)
                    {
                        desiredLookDeg = aimAngle;
                        hasExplicitLookTarget = true;
                        isCombatTarget = true;
                        hasLookTargetWorldXZ = true;
                        lookTargetWorldXZ = moveTargetXZ;
                    }
                }
            }

            bool peacefulUncurlingInPlace = false;
            if (!desiredBodyFound && !sleepingNow && sleepCurlProgress01 > 0.001f && !isLocomoting && !isCombatTarget)
            {
                peacefulUncurlingInPlace = true;
                float uTurn = GiantSleepCurlSolver.EaseInOutCubic(sleepCurlProgress01);
                float targetSleepHeading = GiantSleepCurlSolver.GetTargetSleepHeadingDeg(RigDef, sleepCurlSideSign);
                float sleepTurnDelta = Mathf.DeltaAngle(sleepStartHeadingDeg, targetSleepHeading);
                desiredBodyDeg = SkeletalKinematics.NormalizeAngle360(sleepStartHeadingDeg + sleepTurnDelta * uTurn);
                desiredLookDeg = desiredBodyDeg;
                desiredBodyFound = true;
            }

            // Priority 3: Vanilla Pawn.Rotation + stationary hostile head tracking
            if (!desiredBodyFound)
            {
                if (!IsPatherMovingNotBusy && headingInitialized && Pawn.CurJobDef != JobDefOf.AttackMelee
                    && (canHoldHullRanged || Pawn.Drafted || currentDailyActivity != GiantDailyActivityKind.None))
                {
                    desiredBodyDeg = currentHeadingDeg;
                }
                else
                {
                    desiredBodyDeg = (IsPatherMovingNotBusy && headingInitialized)
                        ? targetHeadingDeg
                        : Pawn.Rotation.AsAngle;
                }
                desiredLookDeg = desiredBodyDeg;

                CompGiantCockpit gazeCockpit = GiantFastLookup.TryGetCockpit(Pawn);
                bool isDormantCockpit = gazeCockpit != null && gazeCockpit.IsDormantUnmanned;
                bool isMechanicalVehicle = (gazeCockpit != null && (gazeCockpit.Props.treatHostAsVehicle || gazeCockpit.IsPiloted))
                    || vehicleLocomotion != null
                    || flightVehicleLocomotion != null;

                Thing threat = isDormantCockpit ? null : ResolveStableGazeHostileTarget(simPos, 22f);
                if (threat != null &&
                    (partCombat == null || partCombat.HasValidForcedTarget() || !partCombat.IsTargetCurrentlyEngageableByIndependentOverlay(threat)))
                {
                    Vector3 threatPos = threat is Pawn tp ? GetSimTickPos(tp) : threat.DrawPos;
                    Vector3 d = threatPos - simPos;
                    d.y = 0f;
                    float distSq = d.sqrMagnitude;
                    if (distSq > 0.04f)
                    {
                        float dist = Mathf.Sqrt(distSq);
                        float threatAngle = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                        float distW = GiantSteeringRuntime.SmoothRamp01(23f, 15f, dist);
                        float offBow = Mathf.Abs(Mathf.DeltaAngle(currentHeadingDeg, threatAngle));
                        float bowW = 1f - GiantSteeringRuntime.SmoothRamp01(105f, 155f, offBow);
                        float wThreat = distW * bowW;
                        if (wThreat > 0.05f)
                        {
                            desiredLookDeg = desiredBodyDeg + Mathf.DeltaAngle(desiredBodyDeg, threatAngle) * wThreat;
                            hasExplicitLookTarget = true;
                            isCombatTarget = wThreat > 0.35f;
                        }
                    }
                }

                if (!hasExplicitLookTarget && !isCombatTarget && currentDailyActivity != GiantDailyActivityKind.None
                    && TryGetDailyActivityLookAngleDeg(simPos, currentHeadingDeg, out float dailyLookDeg))
                {
                    desiredLookDeg = dailyLookDeg;
                    hasExplicitLookTarget = true;
                }

                // Episodic predatory POI vigilance gaze in live non-drafted Idle state (biological creatures only; never vehicles)
                if (!hasExplicitLookTarget && !isLocomoting && !isMechanicalVehicle && !isDormantCockpit
                    && animState == GiantAnimState.Idle && !IsCurrentlyAirborne
                    && Pawn != null && Pawn.Spawned && Pawn.Map != null && !Pawn.Drafted
                    && RigDef?.steering?.lookChains != null && RigDef.steering.lookChains.Count > 0)
                {
                    SteeringLookChainDef primaryChain = RigDef.steering.lookChains[0];
                    if (primaryChain != null && primaryChain.idlePoiScanMaxAngleDeg > 0.5f)
                    {
                        StepIdleVigilanceGaze(primaryChain, simPos, desiredBodyDeg, deltaTicks);
                        if (Mathf.Abs(idleVigilanceOffsetDeg) > 0.25f)
                        {
                            desiredLookDeg = desiredBodyDeg + idleVigilanceOffsetDeg;
                            hasExplicitLookTarget = true;
                        }
                    }
                }
                else if (idleVigilanceInGlance || Mathf.Abs(idleVigilanceOffsetDeg) > 0.01f)
                {
                    idleVigilanceOffsetDeg = 0f;
                    idleVigilanceInGlance = false;
                    idleVigilanceRemainingTicks = 140f;
                }
            }
            else if (idleVigilanceInGlance || Mathf.Abs(idleVigilanceOffsetDeg) > 0.01f)
            {
                idleVigilanceOffsetDeg = 0f;
                idleVigilanceInGlance = false;
                idleVigilanceRemainingTicks = 140f;
            }

            desiredBodyDeg = SkeletalKinematics.NormalizeAngle360(desiredBodyDeg);
            desiredLookDeg = SkeletalKinematics.NormalizeAngle360(desiredLookDeg);

            if (!smoothedLookInitialized || deltaTicks <= 0f)
            {
                smoothedDesiredLookDeg = desiredLookDeg;
                smoothedLookInitialized = true;
            }
            else
            {
                float lookDiff = Mathf.DeltaAngle(smoothedDesiredLookDeg, desiredLookDeg);
                float maxInStep = 14.0f * deltaTicks;
                float inStep = Mathf.Clamp(lookDiff * 0.62f, -maxInStep, maxInStep);
                smoothedDesiredLookDeg = SkeletalKinematics.NormalizeAngle360(smoothedDesiredLookDeg + inStep);
            }

            currentSteeringCtx = new GiantSteeringContext
            {
                DesiredBodyHeadingDeg = vehicleLocomotion != null
                    ? vehicleLocomotion.HullHeadingDeg
                    : (flightVehicleLocomotion != null ? flightVehicleLocomotion.HullHeadingDeg : desiredBodyDeg),
                DesiredLookHeadingDeg = smoothedDesiredLookDeg,
                HasExplicitLookTarget = hasExplicitLookTarget,
                IsCombatTarget = isCombatTarget,
                IsMoving = vehicleLocomotion != null
                    ? (Mathf.Abs(vehicleLocomotion.CurrentSignedSpeedCellsPerTick) > 1e-4f || vehicleLocomotion.DriveState == VehicleDriveState.PivotTurning)
                    : (flightVehicleLocomotion != null
                        ? (flightVehicleLocomotion.CurrentForwardSpeedCellsPerTick > 1e-4f || flightVehicleLocomotion.DriveState == FlightVehicleDriveState.HoverYawing)
                        : isLocomoting),
                MovedDistanceCells = movedDist,
                AnimState = animState,
                ContinuousTicks = continuousSteeringTicks,
                SteeringEnabled = RigDef?.steering?.enabled ?? true,
                IntensityScale = Mathf.Clamp(RigDef?.steering?.intensityScale ?? 1.0f, 0.25f, 2.0f),
                EnableNeckIdleAndGaitArticulation = (RigDef?.steering?.enableNeckIdleAndGaitArticulation ?? true)
                    && (vehicleLocomotion == null || !vehicleLocomotion.Def.suppressBiologicalGait)
                    && (flightVehicleLocomotion == null || !flightVehicleLocomotion.Def.suppressBiologicalGait),
                HasLookTargetWorldXZ = hasLookTargetWorldXZ,
                LookTargetWorldXZ = lookTargetWorldXZ,
                LastBodyHeadingStepDeg = 0f,
                StabilizeTurretsAgainstHullTurn =
                    (vehicleLocomotion != null && vehicleLocomotion.Def.stabilizeTurretsAgainstHullTurn) ||
                    (flightVehicleLocomotion != null && flightVehicleLocomotion.Def.stabilizeTurretsAgainstHullTurn),
                IsRushing = rushActive
            };

            float prevHeadingBeforeStep = currentHeadingDeg;
            if (vehicleLocomotion != null)
            {
                currentHeadingDeg = vehicleLocomotion.HullHeadingDeg;
                lastStepHeadingDeltaDeg = headingInitialized
                    ? Mathf.DeltaAngle(prevHeadingBeforeStep, currentHeadingDeg)
                    : 0f;
                targetHeadingDeg = currentHeadingDeg;
                headingInitialized = true;
                currentSteeringCtx.LastBodyHeadingStepDeg = lastStepHeadingDeltaDeg;
                return;
            }
            if (flightVehicleLocomotion != null)
            {
                currentHeadingDeg = flightVehicleLocomotion.HullHeadingDeg;
                lastStepHeadingDeltaDeg = headingInitialized
                    ? Mathf.DeltaAngle(prevHeadingBeforeStep, currentHeadingDeg)
                    : 0f;
                targetHeadingDeg = currentHeadingDeg;
                headingInitialized = true;
                currentSteeringCtx.LastBodyHeadingStepDeg = lastStepHeadingDeltaDeg;
                return;
            }
            if ((sleepingNow || peacefulUncurlingInPlace) && deltaTicks > 0f)
            {
                currentHeadingDeg = desiredBodyDeg;
                headingInitialized = true;
                // Keep lagBones synced with body turn during sleep curl/uncurl to prevent wake-up tail whip overshoot
                steeringRuntime?.Reset(currentHeadingDeg);
            }
            else if (currentDailyActivity != GiantDailyActivityKind.None && !isLocomoting && !isCombatTarget && deltaTicks > 0f)
            {
                // Stationary head-only daily motions: lock body heading & posterior lagBones so pelvis/butt never sway
                headingInitialized = true;
                steeringRuntime?.ResetLagAndBankOnly(currentHeadingDeg);
            }
            else if (partCombat != null && partCombat.HasActiveFullHullSpinClip() && deltaTicks > 0f)
            {
                // Lock base hull heading during an active 360-deg full-hull spin clip so Pawn.Rotation snaps
                // or target micro-steps never jerk the spin frame mid-windup or mid-revolution.
                headingInitialized = true;
                steeringRuntime?.ResetLagAndBankOnly(currentHeadingDeg);
            }
            else if (GiantMotionOverrideRegistry.TryGetLockedFlightHeading(Pawn, out float lockedFlightHeadingDeg))
            {
                currentHeadingDeg = SkeletalKinematics.NormalizeAngle360(lockedFlightHeadingDeg);
                desiredBodyDeg = currentHeadingDeg;
                headingInitialized = true;
                steeringRuntime?.Reset(currentHeadingDeg);
            }
            else if (steeringRuntime != null && deltaTicks > 0f)
            {
                bool isMovingAlongGroundPath = isLocomoting && !IsCurrentlyAirborne;
                float effectiveTurnRate = ComputeEffectiveBodyTurnSpeed(
                    RigDef.turnSpeedDegPerTick,
                    RigDef.movingTurnSpeedFactor,
                    isMovingAlongGroundPath,
                    isPivotTurningInPlace,
                    hasActiveLinearDashDir);
                currentHeadingDeg = steeringRuntime.StepBodyHeading(
                    currentHeadingDeg,
                    effectiveTurnRate,
                    deltaTicks,
                    in currentSteeringCtx);
            }
            else if (deltaTicks > 0f)
            {
                bool isMovingAlongGroundPath = isLocomoting && !IsCurrentlyAirborne;
                float effectiveTurnRate = ComputeEffectiveBodyTurnSpeed(
                    RigDef.turnSpeedDegPerTick,
                    RigDef.movingTurnSpeedFactor,
                    isMovingAlongGroundPath,
                    isPivotTurningInPlace,
                    hasActiveLinearDashDir);
                float maxStep = effectiveTurnRate * deltaTicks;
                float diff = Mathf.DeltaAngle(currentHeadingDeg, desiredBodyDeg);
                currentHeadingDeg = Math.Abs(diff) <= maxStep
                    ? desiredBodyDeg
                    : SkeletalKinematics.NormalizeAngle360(currentHeadingDeg + Mathf.Sign(diff) * maxStep);
            }

            lastStepHeadingDeltaDeg = headingInitialized ? Mathf.DeltaAngle(prevHeadingBeforeStep, currentHeadingDeg) : 0f;
            targetHeadingDeg = desiredBodyDeg;
            if (Pawn != null && Mathf.Abs(lastStepHeadingDeltaDeg) > 0.05f &&
                (isPivotTurningInPlace || isLocomoting || (!isLocomoting && isCombatTarget)))
            {
                Pawn.Rotation = Rot4.FromAngleFlat(currentHeadingDeg);
            }
        }

    }
}
