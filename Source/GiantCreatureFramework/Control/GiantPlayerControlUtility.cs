using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public static class GiantPlayerControlUtility
    {
        public static bool IsPlayerGiant(Pawn pawn) => pawn != null && !pawn.Destroyed
            && GiantFastLookup.TryGetRig(pawn)?.RigDef != null
            && pawn.Faction == Faction.OfPlayerSilentFail;

        public static bool EvaluateMechanitorActiveControl(
            bool hasOverseer,
            bool overseerDeadOrDestroyed,
            bool hasMechanitorTracker,
            bool isInControlledPawns,
            int totalBandwidth,
            int requiredBandwidth)
        {
            if (!hasOverseer || overseerDeadOrDestroyed || !hasMechanitorTracker)
            {
                return false;
            }
            if (totalBandwidth < requiredBandwidth)
            {
                return false;
            }
            return isInControlledPawns;
        }

        public static bool HasRequiredBiotechOverseer(Pawn pawn, CompGiantCockpit cockpit)
        {
            if (cockpit == null || !cockpit.Props.requireOverseerWhenBiotechActive || !ModsConfig.BiotechActive || DebugSettings.godMode)
            {
                return true;
            }
            if (pawn == null)
            {
                return false;
            }
            int nowTick = Current.Game?.tickManager?.TicksGame ?? -1;
            if (nowTick >= 0 && cockpit.lastOverseerCheckTick == nowTick)
            {
                return cockpit.cachedOverseerValid;
            }
            Pawn overseer = pawn.GetOverseer();
            if (overseer == null)
            {
                if (nowTick >= 0)
                {
                    cockpit.lastOverseerCheckTick = nowTick;
                    cockpit.cachedOverseerValid = false;
                }
                return false;
            }
            Pawn_MechanitorTracker tracker = overseer.mechanitor;
            if (cockpit.cachedRequiredBandwidth < 0)
            {
                cockpit.cachedRequiredBandwidth = Mathf.RoundToInt(pawn.GetStatValue(StatDefOf.BandwidthCost));
            }
            bool inControlled = tracker?.ControlledPawns != null && tracker.ControlledPawns.Contains(pawn);
            bool ok = EvaluateMechanitorActiveControl(
                hasOverseer: true,
                overseerDeadOrDestroyed: overseer.Dead || overseer.Destroyed,
                hasMechanitorTracker: tracker != null,
                isInControlledPawns: inControlled,
                totalBandwidth: tracker?.TotalBandwidth ?? 0,
                requiredBandwidth: cockpit.cachedRequiredBandwidth);
            if (nowTick >= 0)
            {
                cockpit.lastOverseerCheckTick = nowTick;
                cockpit.cachedOverseerValid = ok;
            }
            return ok;
        }

        public static bool CanControl(Pawn pawn) => IsPlayerGiant(pawn) && !pawn.Dead && !pawn.Downed && !pawn.InMentalState
            && !GiantDormantLockRegistry.IsLocked(pawn)
            && (GiantFastLookup.TryGetCockpit(pawn) is not CompGiantCockpit cockpit
                || (cockpit.Props.allowPlayerControlWithoutOverseer && !cockpit.IsDormantUnmanned && HasRequiredBiotechOverseer(pawn, cockpit)));

        public static bool CanOpenControlMenu(Pawn pawn)
        {
            if (!IsPlayerGiant(pawn) || pawn.Dead || pawn.Downed || pawn.InMentalState || GiantDormantLockRegistry.IsLocked(pawn))
            {
                return false;
            }
            if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit)
            {
                if (!cockpit.Props.allowPlayerControlWithoutOverseer || !HasRequiredBiotechOverseer(pawn, cockpit))
                {
                    return false;
                }
                if (cockpit.IsDormantUnmanned)
                {
                    return cockpit.ParkedWhenEmpty && cockpit.Props.autonomousWhenEmpty && cockpit.IsAutonomousCoreIntact();
                }
            }
            return true;
        }

        public static bool CanMove(Pawn pawn) => CanControl(pawn)
            && (pawn.health?.capacities?.CapableOf(PawnCapacityDefOf.Moving) ?? false)
            && !(GiantFastLookup.TryGetRig(pawn)?.VehicleLocomotion?.IsDriveImmobilized ?? false);

        private static readonly AccessTools.FieldRef<Pawn_DraftController, AutoUndrafter> AutoUndrafterField =
            AccessTools.FieldRefAccess<Pawn_DraftController, AutoUndrafter>("autoUndrafter");

        internal static void RefreshAutoUndrafterIfDrafted(Pawn pawn)
        {
            if (pawn?.drafter == null || !pawn.drafter.Drafted || Current.Game?.tickManager == null)
            {
                return;
            }
            AutoUndrafterField?.Invoke(pawn.drafter)?.Notify_Drafted();
        }

        public static void EnsureController(Pawn pawn)
        {
            if (!IsPlayerGiant(pawn))
            {
                return;
            }
            if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit && cockpit.Props.allowPlayerControlWithoutOverseer)
            {
                if (GiantDormantLockRegistry.IsLocked(pawn) || !HasRequiredBiotechOverseer(pawn, cockpit))
                {
                    if (pawn.drafter != null && pawn.drafter.Drafted)
                    {
                        pawn.drafter.Drafted = false;
                    }
                    return;
                }
                if (pawn.playerSettings != null && pawn.playerSettings.hostilityResponse == HostilityResponseMode.Flee)
                {
                    pawn.playerSettings.hostilityResponse = HostilityResponseMode.Ignore;
                }
                if (cockpit.IsDormantUnmanned && pawn.drafter != null && pawn.drafter.Drafted)
                {
                    pawn.drafter.Drafted = false;
                }
            }
            if (pawn.drafter == null) pawn.drafter = new Pawn_DraftController(pawn);
            if (pawn.drafter.Drafted)
            {
                RefreshAutoUndrafterIfDrafted(pawn);
            }
        }

        public static bool WakeAndDraftGiant(Pawn pawn)
        {
            if (!CanOpenControlMenu(pawn))
            {
                return false;
            }
            if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit && cockpit.ParkedWhenEmpty)
            {
                cockpit.SetParkedWhenEmpty(false);
            }
            EnsureController(pawn);
            if (pawn.drafter != null && !pawn.drafter.Drafted)
            {
                pawn.drafter.Drafted = true;
            }
            return pawn.Drafted;
        }

        public static bool IsIdleOrHoldJob(Job job)
        {
            if (job?.def == null)
            {
                return true;
            }
            if (job.def.isIdle)
            {
                return true;
            }
            return job.def == JobDefOf.Wait_Combat
                || job.def == JobDefOf.Wait_MaintainPosture
                || job.def == JobDefOf.Wait
                || job.def == JobDefOf.Wait_Wander
                || job.def == JobDefOf.Wait_WithSleeping;
        }

        public static Job MakeDraftedIdleWaitJob(Pawn pawn, int expiryInterval = -1, bool checkOverrideOnExpire = false)
        {
            Job waitJob;
            if (Current.Game != null)
            {
                waitJob = pawn != null && pawn.Spawned
                    ? JobMaker.MakeJob(JobDefOf.Wait_Combat, pawn.Position)
                    : JobMaker.MakeJob(JobDefOf.Wait_Combat);
            }
            else
            {
                waitJob = new Job { def = JobDefOf.Wait_Combat };
            }
            waitJob.canUseRangedWeapon = false;
            waitJob.expiryInterval = expiryInterval;
            waitJob.checkOverrideOnExpire = checkOverrideOnExpire;
            return waitJob;
        }

        /// <summary>
        /// Synchronizes drafted job state every tick so direct field toggles (e.g. Achtung! setting drafter.draftedInt),
        /// ErroredPather/ErrorRecover Wait(250/150) jobs ("等待中。"), and Shift-queued jobs sitting behind posture-hold
        /// jobs are resolved on Tick 0 without stalling.
        /// </summary>
        public static void SynchronizeDraftedJobState(Pawn pawn, ref bool lastObservedDrafted, ref bool initialized)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.jobs == null)
            {
                initialized = false;
                return;
            }

            bool nowDrafted = pawn.Drafted;
            bool justDrafted = (!initialized || !lastObservedDrafted) && nowDrafted;
            lastObservedDrafted = nowDrafted;
            initialized = true;

            if (!nowDrafted)
            {
                return;
            }
            if (!CanControl(pawn))
            {
                if (IsPlayerGiant(pawn) && pawn.drafter != null)
                {
                    pawn.drafter.Drafted = false;
                    lastObservedDrafted = false;
                }
                return;
            }

            if (justDrafted)
            {
                RefreshAutoUndrafterIfDrafted(pawn);
            }

            Job curJob = pawn.jobs.curJob;
            JobQueue queue = pawn.jobs.jobQueue;

            // 1. If a queued player order is waiting while the giant is merely standing in an idle/hold job, start it immediately
            if (queue != null && queue.Count > 0 && IsIdleOrHoldJob(curJob) && queue.Peek().job.CanBeginNow(pawn, false))
            {
                if (curJob != null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: true);
                }
                else
                {
                    pawn.jobs.CheckForJobOverride(0f, ignoreQueue: false);
                }
                return;
            }

            // 2. If drafted state was poked directly without setter (e.g. Achtung!) while executing a non-player autonomous job, interrupt immediately
            if (justDrafted && curJob != null && !curJob.playerForced && curJob.def != JobDefOf.Wait_Combat)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: true);
                return;
            }

            // 3. Never allow non-player-forced JobDefOf.Wait ("等待中。") to stall a drafted controllable giant for 150-500 ticks
            if (curJob != null && curJob.def == JobDefOf.Wait && !curJob.playerForced)
            {
                Job combatHold = MakeDraftedIdleWaitJob(pawn);
                pawn.jobs.StartJob(
                    combatHold,
                    JobCondition.InterruptForced,
                    ThinkNode_GiantPlayerDrafted.Instance,
                    resumeCurJobAfterwards: false,
                    cancelBusyStances: false,
                    pawn.thinker?.MainThinkTree,
                    JobTag.DraftedOrder,
                    fromQueue: false,
                    addToJobsThisTick: false);
            }
        }
    }

    public class ThinkNode_ConditionalGiantPlayerDrafted : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn)
        {
            return pawn != null && pawn.Drafted && GiantPlayerControlUtility.CanControl(pawn);
        }
    }

    public class ThinkNode_ConditionalAutonomousGiantCanInterrupt : ThinkNode_Conditional
    {
        protected override bool Satisfied(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState)
            {
                return false;
            }
            if (pawn.CurJob != null && pawn.CurJob.playerForced)
            {
                return false;
            }
            if (pawn.jobs?.jobQueue != null && pawn.jobs.jobQueue.Count > 0)
            {
                return false;
            }
            if (pawn.mindState != null && pawn.mindState.forcedGotoPosition.IsValid)
            {
                return false;
            }
            if (GiantPartCombatRuntime.TryGetActivePredatorHuntPrey(pawn, out Pawn huntedPrey))
            {
                Pawn threat = pawn.mindState?.meleeThreat;
                bool hasExternalThreat = threat != null && threat != huntedPrey && threat.Spawned && !threat.Dead && !threat.Downed
                    && Find.TickManager != null && Find.TickManager.TicksGame - pawn.mindState.lastMeleeThreatHarmTick <= 180;
                if (!hasExternalThreat)
                {
                    return false;
                }
            }
            return true;
        }
    }

    public sealed class ThinkNode_GiantPlayerDrafted : ThinkNode
    {
        public static readonly ThinkNode_GiantPlayerDrafted Instance = new ThinkNode_GiantPlayerDrafted();
        public ThinkNode_GiantPlayerDrafted() { subNodes = new List<ThinkNode>(); }

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            return (ThinkNode_GiantPlayerDrafted)base.DeepCopy(resolve);
        }

        public override ThinkResult TryIssueJobPackage(Pawn pawn, JobIssueParams jobParams)
        {
            if (pawn == null)
            {
                return ThinkResult.NoJob;
            }

            JobQueue jobQueue = pawn.jobs?.jobQueue;
            if (jobQueue != null && (pawn.Downed || jobQueue.AnyCanBeginNow(pawn, false)))
            {
                while (jobQueue.Count > 0 && !jobQueue.Peek().job.CanBeginNow(pawn, false))
                {
                    QueuedJob stale = jobQueue.Dequeue();
                    stale.Cleanup(pawn, canReturnToPool: false);
                }
            }

            bool canConsumeQueueNow = !jobParams.ignoreQueue || GiantPlayerControlUtility.IsIdleOrHoldJob(pawn.CurJob);
            if (canConsumeQueueNow && jobQueue != null && jobQueue.Count > 0 && jobQueue.Peek().job.CanBeginNow(pawn, false))
            {
                QueuedJob queued = jobQueue.Dequeue();
                return new ThinkResult(queued.job, this, queued.tag ?? JobTag.DraftedOrder, fromQueue: true);
            }

            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            if (rig?.PartCombat?.HasValidForcedTarget() == true)
            {
                Job posJob = GiantPartCombatUtility.TryGivePartCombatPositioningJob(pawn);
                if (posJob != null)
                {
                    if (posJob.def == JobDefOf.Wait_MaintainPosture || posJob.def == JobDefOf.Wait)
                    {
                        JobMaker.ReturnToPool(posJob);
                        posJob = GiantPlayerControlUtility.MakeDraftedIdleWaitJob(pawn, expiryInterval: 24, checkOverrideOnExpire: true);
                    }
                    else if (posJob.ability == null && (posJob.def == null || !posJob.def.abilityCasting))
                    {
                        posJob.expiryInterval = 24;
                        posJob.checkOverrideOnExpire = true;
                    }
                    return new ThinkResult(posJob, this, JobTag.DraftedOrder);
                }
            }

            Job idleJob = GiantPlayerControlUtility.MakeDraftedIdleWaitJob(pawn);
            return new ThinkResult(idleJob, this, JobTag.DraftedOrder);
        }
    }

    /// <summary>
    /// Provides tactical right-click control options when a player Giant Creature
    /// is selected (especially while undrafted or when right-clicking the giant itself), without interfering
    /// with instant single-right-click DraftedMove / PartFocus auto-take orders when drafted.
    /// </summary>
    public class FloatMenuOptionProvider_GiantControl : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => true;
        protected override bool MechanoidCanDo => true;
        protected override bool CanSelfTarget => true;

        public override bool SelectedPawnValid(Pawn pawn, FloatMenuContext context)
        {
            if (!base.SelectedPawnValid(pawn, context))
            {
                return false;
            }
            return GiantPlayerControlUtility.CanOpenControlMenu(pawn);
        }

        public override IEnumerable<FloatMenuOption> GetOptions(FloatMenuContext context)
        {
            if (context == null || context.map == null)
            {
                yield break;
            }

            var selectedGiants = new List<Pawn>();
            bool anyUndrafted = false;
            foreach (Pawn sp in context.ValidSelectedPawns)
            {
                if (sp != null && GiantPlayerControlUtility.CanOpenControlMenu(sp))
                {
                    selectedGiants.Add(sp);
                    if (!sp.Drafted)
                    {
                        anyUndrafted = true;
                    }
                }
            }

            if (selectedGiants.Count == 0)
            {
                yield break;
            }

            Pawn firstGiant = selectedGiants[0];
            Pawn focusTarget = null;
            if (context.ClickedPawns != null)
            {
                for (int i = 0; i < context.ClickedPawns.Count; i++)
                {
                    Pawn cp = context.ClickedPawns[i];
                    if (cp != null && !selectedGiants.Contains(cp) &&
                        HarmonyPatches_CombatAndHitbox.IsValidRightClickPartFocusTarget(cp, out Pawn resolved))
                    {
                        focusTarget = resolved;
                        break;
                    }
                }
            }

            IntVec3 clickedCell = context.ClickedCell;
            IntVec3 standableNear = CellFinder.StandableCellNear(clickedCell, context.map, 2.9f);
            CompGiantRig firstRig = GiantFastLookup.TryGetRig(firstGiant);
            bool isSelfAnchorClick = !context.IsMultiselect &&
                (clickedCell == firstGiant.Position ||
                 (firstRig != null && firstRig.Footprint.hitCellSet.Contains(clickedCell)) ||
                 (standableNear.IsValid && standableNear == firstGiant.Position));

            // Keep instant drafted auto-take behavior untouched when right-clicking ground or enemies while drafted.
            if (!anyUndrafted && (!isSelfAnchorClick || focusTarget != null))
            {
                yield break;
            }

            if (anyUndrafted)
            {
                if (focusTarget != null)
                {
                    Pawn capturedTarget = focusTarget;
                    Vector3 clickPos = context.clickPosition;
                    bool recordHitCell = GiantFastLookup.TryGetRig(capturedTarget) is CompGiantRig tRig &&
                        tRig.Footprint.hitCellSet.Contains(clickedCell);
                    string focusLabel = "GCF_FloatMenu_DraftAndFocusAttack".CanTranslate()
                        ? string.Format("GCF_FloatMenu_DraftAndFocusAttack".Translate(), capturedTarget.LabelShort)
                        : $"Draft & Focus Weapons on {capturedTarget.LabelShort}";
                    yield return new FloatMenuOption(focusLabel, delegate
                    {
                        if (capturedTarget.Destroyed || !capturedTarget.Spawned || capturedTarget.Dead)
                        {
                            return;
                        }
                        for (int i = 0; i < selectedGiants.Count; i++)
                        {
                            Pawn g = selectedGiants[i];
                            if (GiantPlayerControlUtility.WakeAndDraftGiant(g) &&
                                HarmonyPatches_CombatAndHitbox.CanGiantIssueDraftedPartFocusOrder(g, capturedTarget, out CompGiantRig gr))
                            {
                                if (recordHitCell)
                                {
                                    GiantTargetAimTracker.RecordHitCellClick(g, capturedTarget, clickedCell, clickPos);
                                }
                                GiantPartCombatUtility.OrderGiantFocusAttack(g, gr, new LocalTargetInfo(capturedTarget), allowApproachIfOutOfRange: true);
                            }
                        }
                    }, MenuOptionPriority.AttackEnemy);
                }

                if (standableNear.IsValid && !isSelfAnchorClick)
                {
                    bool anyCanGoto = false;
                    for (int i = 0; i < selectedGiants.Count; i++)
                    {
                        Pawn g = selectedGiants[i];
                        bool mobile = (g.health?.capacities?.CapableOf(PawnCapacityDefOf.Moving) ?? false)
                            && !(GiantFastLookup.TryGetRig(g)?.VehicleLocomotion?.IsDriveImmobilized ?? false);
                        if (mobile &&
                            (GiantPlayerControlUtility.CanControl(g) ||
                             (GiantFastLookup.TryGetCockpit(g) is CompGiantCockpit cp && cp.ParkedWhenEmpty && cp.Props.autonomousWhenEmpty && cp.IsAutonomousCoreIntact())) &&
                            g.CanReach(standableNear, PathEndMode.OnCell, Danger.Deadly))
                        {
                            anyCanGoto = true;
                            break;
                        }
                    }

                    if (anyCanGoto)
                    {
                        string moveLabel = "GCF_FloatMenu_DraftAndMoveHere".CanTranslate()
                            ? "GCF_FloatMenu_DraftAndMoveHere".Translate().ToString()
                            : "Draft & Move Here";
                        yield return new FloatMenuOption(moveLabel, delegate
                        {
                            for (int i = 0; i < selectedGiants.Count; i++)
                            {
                                Pawn g = selectedGiants[i];
                                if (GiantPlayerControlUtility.WakeAndDraftGiant(g) && GiantPlayerControlUtility.CanMove(g))
                                {
                                    IntVec3 dest = RCellFinder.BestOrderedGotoDestNear(standableNear, g);
                                    if (dest.IsValid)
                                    {
                                        FloatMenuOptionProvider_DraftedMove.PawnGotoAction(clickedCell, g, dest);
                                    }
                                }
                            }
                        }, MenuOptionPriority.GoHere);
                    }
                }

                string targetName = selectedGiants.Count == 1 ? firstGiant.LabelShort : $"{selectedGiants.Count}x";
                string draftLabel = "GCF_FloatMenu_DraftControl".CanTranslate()
                    ? string.Format("GCF_FloatMenu_DraftControl".Translate(), targetName)
                    : $"Draft & Control {targetName}";
                yield return new FloatMenuOption(draftLabel, delegate
                {
                    for (int i = 0; i < selectedGiants.Count; i++)
                    {
                        GiantPlayerControlUtility.WakeAndDraftGiant(selectedGiants[i]);
                    }
                }, MenuOptionPriority.High);
            }
            else
            {
                string undraftLabel = "GCF_FloatMenu_UndraftControl".CanTranslate()
                    ? string.Format("GCF_FloatMenu_UndraftControl".Translate(), firstGiant.LabelShort)
                    : $"Undraft {firstGiant.LabelShort}";
                yield return new FloatMenuOption(undraftLabel, delegate
                {
                    for (int i = 0; i < selectedGiants.Count; i++)
                    {
                        Pawn g = selectedGiants[i];
                        GiantPlayerControlUtility.EnsureController(g);
                        if (g.drafter != null)
                        {
                            g.drafter.Drafted = false;
                        }
                    }
                }, MenuOptionPriority.High);
            }

            if (context.IsMultiselect)
            {
                yield break;
            }

            CompGiantRig rig = GiantFastLookup.TryGetRig(firstGiant);
            if (rig?.PartCombat != null && rig.PartCombat.HasAnyPartWeapons)
            {
                if (rig.PartCombat.HasValidForcedTarget())
                {
                    string tgtName = rig.PartCombat.ForcedTarget.HasThing
                        ? rig.PartCombat.ForcedTarget.Thing.LabelShort
                        : rig.PartCombat.ForcedTarget.Cell.ToString();
                    string clearLabel = "GCF_PartClearTarget_Label".CanTranslate()
                        ? string.Format("GCF_PartClearTarget_Label".Translate(), tgtName)
                        : $"Clear Focus: {tgtName}";
                    yield return new FloatMenuOption(clearLabel, delegate
                    {
                        rig.PartCombat.ClearForcedTarget();
                    });
                }

                bool faw = rig.PartCombat.FireAtWill;
                string fawLabel = faw
                    ? ("GCF_FloatMenu_PartFireAtWillOn".CanTranslate()
                        ? "GCF_FloatMenu_PartFireAtWillOn".Translate().ToString()
                        : "Part Fire at Will: ON (Click to Disable)")
                    : ("GCF_FloatMenu_PartFireAtWillOff".CanTranslate()
                        ? "GCF_FloatMenu_PartFireAtWillOff".Translate().ToString()
                        : "Part Fire at Will: OFF (Click to Enable)");
                yield return new FloatMenuOption(fawLabel, delegate
                {
                    rig.PartCombat.FireAtWill = !faw;
                });
            }

            if (GiantFastLookup.TryGetCockpit(firstGiant) is CompGiantCockpit cockpit)
            {
                if (cockpit.Props.autonomousWhenEmpty && cockpit.CrewCount == 0 && cockpit.IsAutonomousCoreIntact())
                {
                    bool currentlyParked = cockpit.ParkedWhenEmpty;
                    string modeLabel = currentlyParked
                        ? ("GCF_FloatMenu_SwitchAutonomousOn".CanTranslate()
                            ? "GCF_FloatMenu_SwitchAutonomousOn".Translate().ToString()
                            : "Control Mode: Enable Autonomous AI")
                        : ("GCF_FloatMenu_SwitchStandbyOn".CanTranslate()
                            ? "GCF_FloatMenu_SwitchStandbyOn".Translate().ToString()
                            : "Control Mode: Park in Standby");
                    yield return new FloatMenuOption(modeLabel, delegate
                    {
                        cockpit.SetParkedWhenEmpty(!currentlyParked);
                    });
                }

                bool flightBlocksCrew = cockpit.IsFlightStateBlockingBoardingOrDisembark(out string flightBlockReason);
                if (cockpit.HasAvailableIntactSeat && firstGiant.Map != null && !flightBlocksCrew)
                {
                    string assignLabel = "GCF_Gizmo_BoardCrew_Label".CanTranslate()
                        ? "GCF_Gizmo_BoardCrew_Label".Translate().ToString()
                        : "Assign Crew";
                    yield return new FloatMenuOption(assignLabel, delegate
                    {
                        cockpit.OpenQuickBoardFloatMenu(firstGiant);
                    });
                }

                if (cockpit.CrewCount > 0)
                {
                    string ratio = $"{cockpit.CrewCount}/{cockpit.SeatCount}";
                    string ejectLabel = "GCF_Gizmo_DisembarkAll_Label".CanTranslate()
                        ? string.Format("GCF_Gizmo_DisembarkAll_Label".Translate(), ratio)
                        : $"Disembark All ({ratio})";
                    if (flightBlocksCrew)
                    {
                        yield return new FloatMenuOption($"{ejectLabel} ({flightBlockReason})", null);
                    }
                    else
                    {
                        yield return new FloatMenuOption(ejectLabel, delegate
                        {
                            cockpit.EjectAll(violent: false);
                        });
                    }
                }
            }

            if (GiantFastLookup.TryGetPhaseTransform(firstGiant) is CompGiantPhaseTransform phaseComp)
            {
                if (phaseComp.HasNextStage)
                {
                    int nextIdx = phaseComp.CurrentStageIndex + 1;
                    GiantPhaseStageDef nextStage = phaseComp.Props.stages[nextIdx];
                    string nextStageLabel = !string.IsNullOrEmpty(nextStage.stageLabelKey) && nextStage.stageLabelKey.CanTranslate()
                        ? nextStage.stageLabelKey.Translate().ToString()
                        : $"Stage {nextIdx + 1}";
                    if (phaseComp.CanManuallyAdvanceToNextStage(out string advReasonKey, out string advFallback))
                    {
                        string advLabel = "GCF_FloatMenu_PhaseAdvance".CanTranslate()
                            ? string.Format("GCF_FloatMenu_PhaseAdvance".Translate(), nextStageLabel)
                            : $"Phase Reconfigure: Advance to {nextStageLabel}";
                        yield return new FloatMenuOption(advLabel, delegate
                        {
                            phaseComp.TryAdvanceToNextStageManually();
                        });
                    }
                    else
                    {
                        string why = !string.IsNullOrEmpty(advReasonKey) && advReasonKey.CanTranslate()
                            ? advReasonKey.Translate().ToString()
                            : advFallback;
                        string disLabel = "GCF_FloatMenu_PhaseDisabled".CanTranslate()
                            ? string.Format("GCF_FloatMenu_PhaseDisabled".Translate(), why)
                            : $"Cannot Reconfigure: {why}";
                        yield return new FloatMenuOption(disLabel, null);
                    }
                }

                if (phaseComp.HasPreviousStage)
                {
                    int prevIdx = phaseComp.CurrentStageIndex - 1;
                    GiantPhaseStageDef prevStage = phaseComp.Props.stages[prevIdx];
                    string prevStageLabel = !string.IsNullOrEmpty(prevStage.stageLabelKey) && prevStage.stageLabelKey.CanTranslate()
                        ? prevStage.stageLabelKey.Translate().ToString()
                        : $"Stage {prevIdx + 1}";
                    if (phaseComp.CanManuallyRevertToPreviousStage(out string revReasonKey, out string revFallback))
                    {
                        string revLabel = "GCF_FloatMenu_PhaseRevert".CanTranslate()
                            ? string.Format("GCF_FloatMenu_PhaseRevert".Translate(), prevStageLabel)
                            : $"Phase Reconfigure: Revert to {prevStageLabel}";
                        yield return new FloatMenuOption(revLabel, delegate
                        {
                            phaseComp.TryRevertToPreviousStageManually();
                        });
                    }
                    else
                    {
                        string why = !string.IsNullOrEmpty(revReasonKey) && revReasonKey.CanTranslate()
                            ? revReasonKey.Translate().ToString()
                            : revFallback;
                        string disLabel = "GCF_FloatMenu_PhaseDisabled".CanTranslate()
                            ? string.Format("GCF_FloatMenu_PhaseDisabled".Translate(), why)
                            : $"Cannot Reconfigure: {why}";
                        yield return new FloatMenuOption(disLabel, null);
                    }
                }
            }
        }
    }

    [HarmonyPatch]
    public static class HarmonyPatches_PlayerControl
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.CanTakeOrder), MethodType.Getter)]
        public static void Pawn_CanTakeOrder_Postfix(Pawn __instance, ref bool __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            if (GiantPlayerControlUtility.IsPlayerGiant(__instance))
            {
                __result = __instance.Drafted
                    ? GiantPlayerControlUtility.CanControl(__instance)
                    : GiantPlayerControlUtility.CanOpenControlMenu(__instance);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.IsPlayerControlled), MethodType.Getter)]
        public static void Pawn_IsPlayerControlled_Postfix(Pawn __instance, ref bool __result)
        {
            if (GiantFastLookup.IsEmpty)
            {
                return;
            }
            if (GiantPlayerControlUtility.CanControl(__instance)) __result = true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PawnComponentsUtility), nameof(PawnComponentsUtility.AddAndRemoveDynamicComponents))]
        public static void PawnComponentsUtility_AddAndRemoveDynamicComponents_Prefix(Pawn pawn, out Pawn_DraftController __state)
        {
            __state = (pawn != null && !pawn.Dead && GiantPlayerControlUtility.IsPlayerGiant(pawn))
                ? pawn.drafter
                : null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PawnComponentsUtility), nameof(PawnComponentsUtility.AddAndRemoveDynamicComponents))]
        public static void PawnComponentsUtility_AddAndRemoveDynamicComponents_Postfix(Pawn pawn, bool actAsIfSpawned, Pawn_DraftController __state)
        {
            if (pawn == null || pawn.Dead || !(pawn.Spawned || actAsIfSpawned))
            {
                return;
            }
            if (GiantPlayerControlUtility.IsPlayerGiant(pawn))
            {
                if (pawn.drafter == null && __state != null)
                {
                    pawn.drafter = __state;
                }
                GiantPlayerControlUtility.EnsureController(pawn);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.IsColonyMechPlayerControlled), MethodType.Getter)]
        public static void Pawn_IsColonyMechPlayerControlled_Postfix(Pawn __instance, ref bool __result)
        {
            if (__result || __instance == null || !ModsConfig.BiotechActive || !__instance.Spawned || __instance.RaceProps?.IsMechanoid != true)
            {
                return;
            }
            if (GiantPlayerControlUtility.CanControl(__instance))
            {
                __result = true;
                return;
            }
            if (!__instance.Dead && !__instance.Downed
                && GiantFastLookup.TryGetCockpit(__instance) is CompGiantCockpit cockpit
                && cockpit.IsAutonomousPlayerControlledVehicle)
            {
                __result = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MechanitorUtility), nameof(MechanitorUtility.IsColonyMechRequiringMechanitor))]
        public static void IsColonyMechRequiringMechanitor_Postfix(Pawn mech, ref bool __result)
        {
            if (__result && mech != null)
            {
                CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(mech);
                if (cockpit != null)
                {
                    if (cockpit.Props.requireOverseerWhenBiotechActive && ModsConfig.BiotechActive)
                    {
                        return;
                    }
                    if (cockpit.Props.allowPlayerControlWithoutOverseer)
                    {
                        __result = false;
                        return;
                    }
                }
                if (GiantPlayerControlUtility.CanOpenControlMenu(mech))
                {
                    __result = false;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.CanControlMechs), MethodType.Getter)]
        public static void Pawn_MechanitorTracker_CanControlMechs_Postfix(Pawn ___pawn, ref AcceptanceReport __result)
        {
            if (!__result.Accepted &&
                ___pawn != null &&
                !___pawn.Dead &&
                !___pawn.Downed &&
                !___pawn.InMentalState &&
                !___pawn.IsPrisoner &&
                (CompGiantCockpit.CurrentlyBoardingPawn == ___pawn ||
                 (___pawn.ParentHolder is CompGiantCockpit cockpit &&
                  cockpit.HostPawn != null &&
                  cockpit.HostPawn.Spawned)))
            {
                __result = true;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.Notify_DeSpawned))]
        public static bool Pawn_MechanitorTracker_NotifyDeSpawned_Prefix(Pawn ___pawn)
        {
            // Keep overseen mechs drafted & controlled when the Mechanitor boards a GCF cockpit
            if (___pawn != null && !___pawn.Dead && !___pawn.Downed &&
                (CompGiantCockpit.CurrentlyBoardingPawn == ___pawn || ___pawn.ParentHolder is CompGiantCockpit))
            {
                return false;
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MechanitorUtility), nameof(MechanitorUtility.CanControlMech))]
        public static void CanControlMech_Postfix(Pawn pawn, Pawn mech, ref AcceptanceReport __result)
        {
            if (!__result.Accepted && __result.Reason.NullOrEmpty() &&
                mech != null && mech.IsColonyMech && GiantFastLookup.TryGetRig(mech) != null && mech.IsAttacking())
            {
                __result = "Incapable".CanTranslate() ? "Incapable".Translate() : new AcceptanceReport("Busy in combat");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
        public static void Pawn_GetGizmos_Prefix(Pawn __instance) => GiantPlayerControlUtility.EnsureController(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
        public static IEnumerable<Gizmo> Pawn_GetGizmos_Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            bool isGiant = __instance != null && GiantFastLookup.TryGetRig(__instance) != null;
            if (!isGiant)
            {
                foreach (Gizmo gizmo in __result) yield return gizmo;
                yield break;
            }

            bool isPlayerGiant = GiantPlayerControlUtility.IsPlayerGiant(__instance);
            bool autonomousUnoverseenMech = __instance.IsColonyMech && __instance.GetOverseer() == null;
            string selectOverseerLabel = autonomousUnoverseenMech
                ? ("CommandSelectOverseer".CanTranslate() ? "CommandSelectOverseer".Translate().ToString() : "Select overseer")
                : null;
            HashSet<Ability> seenAbilities = null;

            foreach (Gizmo gizmo in __result)
            {
                if (gizmo == null) continue;
                if (gizmo is Command_VerbTarget vt && vt.verb?.EquipmentSource == null)
                {
                    continue;
                }
                if (gizmo is Command_Ability ca)
                {
                    if (ca.devGizmo && !isPlayerGiant)
                    {
                        continue;
                    }
                    if (ca.Ability != null)
                    {
                        seenAbilities ??= new HashSet<Ability>();
                        if (!seenAbilities.Add(ca.Ability))
                        {
                            continue;
                        }
                    }
                }
                else if (gizmo is Command cmd)
                {
                    if (autonomousUnoverseenMech && cmd.Disabled && selectOverseerLabel != null &&
                        string.Equals(cmd.defaultLabel, selectOverseerLabel, System.StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (cmd.defaultLabel != null &&
                        (cmd.defaultLabel.StartsWith("DEV: Mech energy", System.StringComparison.Ordinal) ||
                         string.Equals(cmd.defaultLabel, "DEV: Wake up", System.StringComparison.Ordinal)))
                    {
                        continue;
                    }
                }
                yield return gizmo;
            }

            if (!__instance.Spawned || !isPlayerGiant || __instance.Dead) yield break;
            if (!__instance.IsColonistPlayerControlled && !__instance.IsColonyMech && !__instance.IsColonySubhumanPlayerControlled)
            {
                var draft = new Command_Toggle
                {
                    hotKey = KeyBindingDefOf.Command_ColonistDraft,
                    defaultLabel = (__instance.Drafted ? "CommandUndraftLabel" : "CommandDraftLabel").Translate(),
                    defaultDesc = "CommandToggleDraftDesc".Translate(),
                    icon = TexCommand.Draft,
                    isActive = () => __instance.Drafted,
                    toggleAction = () => __instance.drafter.Drafted = !__instance.Drafted,
                    turnOnSound = SoundDefOf.DraftOn,
                    turnOffSound = SoundDefOf.DraftOff,
                    groupKeyIgnoreContent = 81729172
                };
                if (!GiantPlayerControlUtility.CanControl(__instance)) draft.Disable("Incapable".Translate());
                yield return draft;
            }
            if (__instance.Drafted && GiantPlayerControlUtility.CanMove(__instance))
            {
                yield return GiantWorldUtility.DepartureCommand(__instance);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.ShowDraftGizmo), MethodType.Getter)]
        public static void ShowDraftGizmo_Postfix(Pawn_DraftController __instance, ref bool __result)
        {
            if (GiantPlayerControlUtility.IsPlayerGiant(__instance.pawn)) __result = true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MechanitorUtility), nameof(MechanitorUtility.CanDraftMech))]
        public static bool CanDraftMech_Prefix(Pawn mech, ref AcceptanceReport __result)
        {
            if (!GiantPlayerControlUtility.IsPlayerGiant(mech)) return true;
            if (GiantPlayerControlUtility.CanControl(mech))
            {
                __result = AcceptanceReport.WasAccepted;
                return false;
            }
            if (GiantFastLookup.TryGetCockpit(mech) is CompGiantCockpit cockpit && cockpit.IsDormantUnmanned)
            {
                __result = "GCF_CrewDisabledUnmanned".CanTranslate()
                    ? "GCF_CrewDisabledUnmanned".Translate().ToString()
                    : "Standby (unmanned)";
                return false;
            }
            __result = "Incapable".Translate();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MechanitorUtility), nameof(MechanitorUtility.InMechanitorCommandRange))]
        public static bool InMechanitorCommandRange_Prefix(Pawn mech, ref bool __result)
        {
            if (!GiantPlayerControlUtility.IsPlayerGiant(mech)) return true;
            __result = GiantPlayerControlUtility.CanControl(mech)
                || (GiantFastLookup.TryGetCockpit(mech) is CompGiantCockpit cockpit && cockpit.IsAutonomousPlayerControlledVehicle);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_Thinker), nameof(Pawn_Thinker.MainThinkNodeRoot), MethodType.Getter)]
        public static bool MainThinkNodeRoot_Prefix(Pawn_Thinker __instance, ref ThinkNode __result)
        {
            Pawn pawn = __instance?.pawn;
            if (pawn == null) return true;
            if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit && cockpit.ShouldNullifyThinkTree)
            {
                __result = ThinkNode_GiantDormantNull.Instance;
                return false;
            }
            if (!pawn.Spawned || !pawn.Drafted || !GiantPlayerControlUtility.CanControl(pawn)) return true;
            __result = ThinkNode_GiantPlayerDrafted.Instance;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_Thinker), nameof(Pawn_Thinker.ConstantThinkTree), MethodType.Getter)]
        public static void ConstantThinkTree_Postfix(Pawn_Thinker __instance, ref ThinkTreeDef __result)
        {
            if (GiantFastLookup.IsEmpty) return;
            Pawn pawn = __instance?.pawn;
            if (__result == null || pawn == null) return;
            if (GiantVehicleBehaviorGuard.ShouldNullifyConstantThinkTree(pawn)
                || (pawn.Spawned && pawn.Drafted && GiantPlayerControlUtility.CanControl(pawn)))
            {
                __result = null;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Pawn_Thinker), nameof(Pawn_Thinker.ConstantThinkNodeRoot), MethodType.Getter)]
        public static bool ConstantThinkNodeRoot_Prefix(Pawn_Thinker __instance, ref ThinkNode __result)
        {
            if (GiantFastLookup.IsEmpty) return true;
            Pawn pawn = __instance?.pawn;
            if (pawn == null) return true;
            if (GiantVehicleBehaviorGuard.ShouldNullifyConstantThinkTree(pawn)
                || (pawn.Spawned && pawn.Drafted && GiantPlayerControlUtility.CanControl(pawn)))
            {
                __result = ThinkNode_GiantDormantNull.Instance;
                return false;
            }
            return true;
        }

        private static readonly Func<FloatMenuOptionProvider, bool> FloatMenuProviderMechanoidCanDoGetter =
            CreateFloatMenuProviderMechanoidCanDoGetter();

        private static Func<FloatMenuOptionProvider, bool> CreateFloatMenuProviderMechanoidCanDoGetter()
        {
            PropertyInfo prop = AccessTools.Property(typeof(FloatMenuOptionProvider), "MechanoidCanDo");
            MethodInfo getter = prop?.GetGetMethod(nonPublic: true);
            if (getter == null)
            {
                return null;
            }
            try
            {
                return (Func<FloatMenuOptionProvider, bool>)Delegate.CreateDelegate(
                    typeof(Func<FloatMenuOptionProvider, bool>),
                    getter);
            }
            catch
            {
                return provider => (bool)getter.Invoke(provider, null);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FloatMenuOptionProvider), nameof(FloatMenuOptionProvider.SelectedPawnValid))]
        public static void FloatMenuOptionProvider_SelectedPawnValid_Postfix(
            FloatMenuOptionProvider __instance,
            Pawn pawn,
            ref bool __result)
        {
            if (!__result || __instance == null || pawn == null)
            {
                return;
            }

            // Pure NRE guard: vanilla FloatMenuOptionProvider_WorkGivers unconditionally dereferences pawn.workSettings.
            if (__instance is FloatMenuOptionProvider_WorkGivers && pawn.workSettings == null)
            {
                __result = false;
                return;
            }

            if (GiantPlayerControlUtility.IsPlayerGiant(pawn) && !(pawn.RaceProps?.Humanlike ?? false))
            {
                bool mechanoidCanDo = FloatMenuProviderMechanoidCanDoGetter != null
                    && FloatMenuProviderMechanoidCanDoGetter(__instance);
                if (!mechanoidCanDo)
                {
                    __result = false;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnCanGoto))]
        public static void PawnCanGoto_Postfix(Pawn pawn, ref AcceptanceReport __result)
        {
            if (GiantPlayerControlUtility.IsPlayerGiant(pawn) && !GiantPlayerControlUtility.CanMove(pawn))
                __result = "Incapable".Translate();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnGotoAction))]
        public static void PawnGotoAction_Prefix(Pawn pawn)
        {
            if (GiantPlayerControlUtility.IsPlayerGiant(pawn)) GiantFastLookup.TryGetRig(pawn)?.PartCombat?.ClearForcedTarget();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnGotoAction))]
        public static void PawnGotoAction_Postfix(IntVec3 clickCell, Pawn pawn)
        {
            if (pawn != null
                && GiantPlayerControlUtility.IsPlayerGiant(pawn)
                && pawn.Map?.exitMapGrid != null
                && pawn.Map.exitMapGrid.IsExitCell(clickCell)
                && pawn.CurJobDef == JobDefOf.Goto
                && pawn.CurJob != null)
            {
                pawn.CurJob.exitMapOnArrival = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
        public static void Drafted_Postfix(Pawn_DraftController __instance)
        {
            if (!__instance.Drafted && GiantPlayerControlUtility.IsPlayerGiant(__instance.pawn))
                GiantFastLookup.TryGetRig(__instance.pawn)?.PartCombat?.ClearForcedTarget();
        }
    }
}
