using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Biological sleep, ingest, and social-affection pose inputs.
    public partial class CompGiantRig
    {
        public bool IsBiologicalSleepingNow()
        {
            if (Pawn == null || !Pawn.Spawned || Pawn.Dead || Pawn.Downed || Pawn.Drafted)
            {
                return false;
            }
            if (GiantVehicleBehaviorGuard.IsVehiclePawn(Pawn, out _, out _))
            {
                return false;
            }
            if (Pawn.RaceProps != null && Pawn.RaceProps.IsMechanoid)
            {
                return false;
            }
            if (IsCurrentlyAirborne || flightEntryActive || ManualOverrideActive)
            {
                return false;
            }
            if (animState == GiantAnimState.MorphLocked || animState == GiantAnimState.AttackOnce || animState == GiantAnimState.Death)
            {
                return false;
            }
            if (IsPatherMovingNotBusy || IsActivelyLocomoting)
            {
                return false;
            }
            Verse.AI.Job curJob = Pawn.CurJob;
            if (curJob?.def == null)
            {
                return false;
            }
            bool isLayDown = curJob.def == JobDefOf.LayDown
                || string.Equals(curJob.def.defName, "LayDown", StringComparison.Ordinal);
            if (!isLayDown)
            {
                return false;
            }
            if (Pawn.jobs?.curDriver != null && Pawn.jobs.curDriver.asleep)
            {
                return true;
            }
            if (!curJob.targetA.IsValid || Pawn.Position == curJob.targetA.Cell || Pawn.Position.DistanceToSquared(curJob.targetA.Cell) <= 4)
            {
                return true;
            }
            return false;
        }

        public void TriggerSocialAffectionLinger(Pawn focusPawn = null, int durationTicks = GiantDailyActivityPoseSolver.NuzzleLingerDurationTicks)
        {
            int nowTick = Current.Game?.tickManager != null ? Current.Game.tickManager.TicksGame : Mathf.RoundToInt(continuousSteeringTicks);
            if (focusPawn != null)
            {
                lastSocialFocusPawn = focusPawn;
            }
            socialAffectionLingerUntilTick = Mathf.Max(socialAffectionLingerUntilTick, nowTick + Mathf.Max(1, durationTicks));
        }

        private void StepBiologicalDailyActivityWeights(float deltaTicks, bool isMovingNow, bool isLocomoting, bool sleepingNow)
        {
            int nowTick = Current.Game?.tickManager != null ? Current.Game.tickManager.TicksGame : Mathf.RoundToInt(continuousSteeringTicks);
            bool isVeh = Pawn != null && GiantVehicleBehaviorGuard.IsVehiclePawn(Pawn, out _, out _);
            bool isMech = Pawn?.RaceProps != null && Pawn.RaceProps.IsMechanoid;
            bool deadOrDowned = Pawn == null || !Pawn.Spawned || Pawn.Dead || Pawn.Downed;
            bool drafted = Pawn != null && Pawn.Drafted;
            bool airborne = IsCurrentlyAirborne || flightEntryActive || ManualOverrideActive;

            if (!isVeh && !isMech && !deadOrDowned && !drafted && !airborne)
            {
                Verse.AI.Job curJob = Pawn.CurJob;
                if (curJob?.def != null && (curJob.def == JobDefOf.Nuzzle || string.Equals(curJob.def.defName, "Nuzzle", StringComparison.Ordinal)))
                {
                    if (curJob.targetA.HasThing && curJob.targetA.Thing is Pawn nuzTarget && nuzTarget.Spawned && nuzTarget.Map == Pawn.Map)
                    {
                        lastSocialFocusPawn = nuzTarget;
                        lastNuzzleArmedTick = nowTick;
                    }
                }
                else if (lastNuzzleArmedTick > -90000 && nowTick - lastNuzzleArmedTick <= 18)
                {
                    lastNuzzleArmedTick = -99999;
                    if (lastSocialFocusPawn != null && lastSocialFocusPawn.Spawned && lastSocialFocusPawn.Map == Pawn.Map)
                    {
                        socialAffectionLingerUntilTick = Mathf.Max(
                            socialAffectionLingerUntilTick,
                            nowTick + GiantDailyActivityPoseSolver.NuzzleLingerDurationTicks);
                    }
                }
            }
            else
            {
                lastNuzzleArmedTick = -99999;
                socialAffectionLingerUntilTick = -99999;
                cachedExternalTrainerPawn = null;
            }

            bool bigTurnInPlace = animState == GiantAnimState.TurnInPlace
                && headingInitialized
                && Mathf.Abs(Mathf.DeltaAngle(currentHeadingDeg, targetHeadingDeg)) > 10f;
            bool movingGate = isMovingNow || isLocomoting || IsPatherMovingNotBusy || bigTurnInPlace;
            bool sleepGate = sleepingNow || wasBiologicalSleeping || sleepCurlProgress01 > 0.02f;
            bool busyCombatOrMorph = (animState != GiantAnimState.Idle && animState != GiantAnimState.TurnInPlace)
                || (Pawn?.stances?.curStance is Stance_Busy);

            bool hasActiveExternalTrainer = false;
            if (!isVeh && !isMech && !deadOrDowned && !drafted && !airborne && !movingGate && !sleepGate && !busyCombatOrMorph && Pawn.Map != null)
            {
                if (nowTick >= nextExternalTrainerScanTick || (cachedExternalTrainerPawn != null && (!cachedExternalTrainerPawn.Spawned || cachedExternalTrainerPawn.Map != Pawn.Map)))
                {
                    nextExternalTrainerScanTick = nowTick + 8;
                    cachedExternalTrainerPawn = null;
                    IReadOnlyList<Pawn> colonists = Pawn.Map.mapPawns?.FreeColonistsSpawned;
                    if (colonists != null)
                    {
                        for (int i = 0; i < colonists.Count; i++)
                        {
                            Pawn col = colonists[i];
                            if (col == null || col.Dead || col.Downed || (col.pather != null && col.pather.Moving) || col.HostileTo(Pawn))
                            {
                                continue;
                            }
                            JobDef cDef = col.CurJobDef;
                            if ((cDef == JobDefOf.Tame || cDef == JobDefOf.Train)
                                && col.CurJob != null
                                && col.CurJob.targetA.Thing == Pawn)
                            {
                                cachedExternalTrainerPawn = col;
                                lastSocialFocusPawn = col;
                                break;
                            }
                        }
                    }
                }
                hasActiveExternalTrainer = cachedExternalTrainerPawn != null;
            }
            else
            {
                cachedExternalTrainerPawn = null;
            }

            bool hasNuzzleLinger = nowTick <= socialAffectionLingerUntilTick;
            currentDailyActivity = GiantDailyActivityPoseSolver.ResolveActivityKind(
                isVehiclePawn: isVeh,
                isMechanoid: isMech,
                isDeadOrDowned: deadOrDowned,
                isDrafted: drafted,
                isAirborneOrFlyingEntry: airborne,
                isMoving: movingGate,
                isSleepingOrCurling: sleepGate,
                isNonIdleCombatOrMorphState: busyCombatOrMorph,
                currentJob: Pawn?.CurJob,
                hasActiveExternalTamerOrTrainer: hasActiveExternalTrainer,
                hasActiveNuzzleLinger: hasNuzzleLinger,
                selfPawn: Pawn);

            if (deltaTicks <= 0f)
            {
                return;
            }

            float targetIngest = currentDailyActivity == GiantDailyActivityKind.Ingesting ? 1f : 0f;
            float targetSocial = currentDailyActivity == GiantDailyActivityKind.SocialAffection ? 1f : 0f;
            float ingestStep = deltaTicks / (targetIngest > dailyIngestWeight01
                ? GiantDailyActivityPoseSolver.BlendInDurationTicks
                : GiantDailyActivityPoseSolver.BlendOutDurationTicks);
            float socialStep = deltaTicks / (targetSocial > dailySocialWeight01
                ? GiantDailyActivityPoseSolver.BlendInDurationTicks
                : GiantDailyActivityPoseSolver.BlendOutDurationTicks);

            dailyIngestWeight01 = Mathf.MoveTowards(dailyIngestWeight01, targetIngest, ingestStep);
            dailySocialWeight01 = Mathf.MoveTowards(dailySocialWeight01, targetSocial, socialStep);

            if (dailyIngestWeight01 > 0.001f || dailySocialWeight01 > 0.001f)
            {
                dailyActivityPhaseTicks += deltaTicks;
            }
            else
            {
                dailyActivityPhaseTicks = 0f;
            }
        }

        private bool TryGetDailyActivityLookAngleDeg(Vector3 simPos, float bodyHeadingDeg, out float lookAngleDeg)
        {
            lookAngleDeg = bodyHeadingDeg;
            if (Pawn == null || !Pawn.Spawned)
            {
                return false;
            }

            Vector3? focusWorld = null;
            if (currentDailyActivity == GiantDailyActivityKind.Ingesting)
            {
                Verse.AI.Job job = Pawn.CurJob;
                if (job != null && job.targetA.IsValid)
                {
                    focusWorld = job.targetA.HasThing
                        ? job.targetA.Thing.DrawPos
                        : job.targetA.Cell.ToVector3Shifted();
                }
            }
            else if (currentDailyActivity == GiantDailyActivityKind.SocialAffection)
            {
                Verse.AI.Job job = Pawn.CurJob;
                Pawn focusPawn = (job?.targetA.HasThing == true ? job.targetA.Thing as Pawn : null)
                    ?? (job?.targetB.HasThing == true ? job.targetB.Thing as Pawn : null)
                    ?? cachedExternalTrainerPawn
                    ?? lastSocialFocusPawn;
                if (focusPawn != null && focusPawn.Spawned && focusPawn.Map == Pawn.Map)
                {
                    focusWorld = GetSimTickPos(focusPawn);
                }
            }

            if (!focusWorld.HasValue)
            {
                return false;
            }

            Vector3 delta = focusWorld.Value - simPos;
            delta.y = 0f;
            if (delta.sqrMagnitude < 0.09f)
            {
                return false;
            }

            float rawDeg = Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg;
            float relDeg = Mathf.DeltaAngle(bodyHeadingDeg, rawDeg);
            if (Mathf.Abs(relDeg) > 80f)
            {
                return false;
            }
            lookAngleDeg = bodyHeadingDeg + Mathf.Clamp(relDeg, -45f, 45f);
            return true;
        }

    }
}
