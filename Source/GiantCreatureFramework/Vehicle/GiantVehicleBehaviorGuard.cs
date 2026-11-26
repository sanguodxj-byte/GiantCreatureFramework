using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Unified zero-GC O(1) behavioral isolation layer for Pawn-backed GCF vehicles (tanks, dreadnoughts,
    /// carriers, rotorcraft, and Agent-authored ground/aerial vehicles).
    ///
    /// Architectural Principle:
    /// Instead of scattering dozens of per-JobGiver / per-WorkGiver Harmony patches across RimWorld and DLCs,
    /// ALL AI &amp; Job isolation is consolidated into three pipeline choke points:
    ///   1. ThinkTree Entry (`ShouldNullifyConstantThinkTree` + `MainThinkNodeRoot` nullification)
    ///   2. ThinkTree Output (`TryShortCircuitDetermineNextJob` + `SanitizeThinkResultForVehicle` on `DetermineNextJob`)
    ///   3. Direct Imperative Job Dispatch (`TryFilterVehicleStartJob` on `StartJob` / `TryTakeOrderedJob`)
    /// Uses GiantFastLookup exclusively (zero TryGetComp on hotpaths).
    /// </summary>
    public static class GiantVehicleBehaviorGuard
    {
        private static readonly HashSet<string> ForbiddenVehicleJobDefNames = new HashSet<string>(StringComparer.Ordinal)
        {
            // Panic / cowardly flee, crawl-flee & fire-panic responses
            "Flee",
            "FleeAndCower",
            "FleeDanger",
            "ExtinguishSelf",
            "ExtinguishFiresNearby",
            "BeatFire",
            "JumpInWater",
            "RunRandom",

            // Biotech small-mech recharger / shutdown & Odyssey gravship jobs
            "MechCharge",
            "SelfShutdown",
            "BoardOrLeaveGravship",

            // Biological / animal / humanlike life-support & social jobs
            "Ingest",
            "LayDown",
            "Lovin",
            "Mate",
            "Nuzzle",
            "LayEgg",
            "PredatorHunt",
            "Vomit",
            "Wear",
            "Strip",
            "SocialRelax",
            "WatchTelevision",
            "Skygaze",
            "Meditate",
            "Pray",

            // Colony labor, hauling, medical & construction jobs (also caught generically via job.workGiverDef != null)
            "HaulToCell",
            "HaulToContainer",
            "HaulToTransporter",
            "Rescue",
            "Capture",
            "TendPatient",
            "FeedPatient",
            "DeliverFood",
            "Clean",
            "Repair",
            "FixBrokenDownBuilding",
            "FinishFrame",
            "Deconstruct",
            "BuildRoof",
            "RemoveRoof",
            "Mine",
            "OperateDeepDrill",
            "CutPlant",
            "Harvest",
            "HarvestDesignated",
            "Sow",
            "DoBill",
            "Research",
            "Refuel",
            "RefuelAtomic",
            "RearmTurret",
            "RearmTurretAtomic",
            "UnloadYourInventory",
            "RopeToPen",
            "Unrope"
        };

        private static readonly HashSet<string> SuppressedVehicleNeedDefNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Food",
            "Rest",
            "Joy",
            "Mood",
            "Comfort",
            "Beauty",
            "Outdoors",
            "Indoors",
            "RoomSize",
            "DrugDesire",
            "Learning",
            "Play"
        };

        public static bool IsVehicleDef(GiantRigDef rigDef, bool hasCockpit, bool cockpitTreatsHostAsVehicle = true)
        {
            if (hasCockpit && cockpitTreatsHostAsVehicle)
            {
                return true;
            }
            if (rigDef == null)
            {
                return false;
            }
            return (rigDef.vehicleLocomotion != null && rigDef.vehicleLocomotion.IsActive)
                || (rigDef.flightVehicleLocomotion != null && rigDef.flightVehicleLocomotion.IsActive);
        }

        public static bool IsVehiclePawn(Pawn pawn, out CompGiantRig rig, out CompGiantCockpit cockpit)
        {
            rig = pawn != null ? GiantFastLookup.TryGetRig(pawn) : null;
            cockpit = pawn != null ? GiantFastLookup.TryGetCockpit(pawn) : null;
            if (pawn == null)
            {
                return false;
            }
            if (cockpit != null && cockpit.Props.treatHostAsVehicle)
            {
                return true;
            }
            if (rig == null)
            {
                return false;
            }
            if (rig.VehicleLocomotion != null || rig.FlightVehicleLocomotion != null)
            {
                return true;
            }
            return IsVehicleDef(rig.RigDef, hasCockpit: false);
        }

        /// <summary>
        /// Zeroes ConstantThinkTree traversal for dormant cockpit units AND for active vehicles that are not
        /// participating in a Lord duty (e.g. raid/siege/caravan Lord), eliminating per-tick panic/flee checks.
        /// </summary>
        public static bool ShouldNullifyConstantThinkTree(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit &&
                (cockpit.ShouldNullifyThinkTree || cockpit.ShouldHoldPositionWhenPilotedUndrafted))
            {
                return true;
            }
            if (!IsVehiclePawn(pawn, out _, out _) || pawn.Dead || pawn.Downed)
            {
                return false;
            }
            if (Current.Game != null && pawn.Map != null && LordUtility.GetLord(pawn) != null)
            {
                return false;
            }
            return true;
        }

        public static bool IsPassiveDormantAllowedJob(JobDef jobDef)
        {
            if (jobDef == null)
            {
                return false;
            }
            if (jobDef == JobDefOf.Wait_MaintainPosture ||
                jobDef == JobDefOf.Wait_Downed ||
                jobDef == JobDefOf.Wait)
            {
                return true;
            }
            string name = jobDef.defName;
            return string.Equals(name, "Wait_MaintainPosture", StringComparison.Ordinal)
                || string.Equals(name, "Wait_Downed", StringComparison.Ordinal)
                || string.Equals(name, "Wait", StringComparison.Ordinal);
        }

        public static bool IsWanderJobDef(JobDef jobDef)
        {
            if (jobDef == null)
            {
                return false;
            }
            if (jobDef == JobDefOf.GotoWander || jobDef == JobDefOf.Wait_Wander)
            {
                return true;
            }
            string name = jobDef.defName;
            return string.Equals(name, "GotoWander", StringComparison.Ordinal)
                || string.Equals(name, "Wait_Wander", StringComparison.Ordinal);
        }

        public static bool IsAutonomousFollowJob(Job job)
        {
            if (job?.def == null || job.playerForced)
            {
                return false;
            }
            if (job.def == JobDefOf.Follow || job.def == JobDefOf.FollowClose || job.def == JobDefOf.FollowRoper)
            {
                return true;
            }
            string name = job.def.defName;
            return string.Equals(name, "Follow", StringComparison.Ordinal)
                || string.Equals(name, "FollowClose", StringComparison.Ordinal)
                || string.Equals(name, "FollowRoper", StringComparison.Ordinal);
        }

        public static bool IsForbiddenNonVehicleJobDef(JobDef jobDef, bool pawnIsDowned = false)
        {
            if (jobDef == null)
            {
                return false;
            }
            if (pawnIsDowned && (jobDef == JobDefOf.Wait_Downed || string.Equals(jobDef.defName, "Wait_Downed", StringComparison.Ordinal)))
            {
                return false;
            }
            if (jobDef == JobDefOf.Flee ||
                jobDef == JobDefOf.FleeAndCower ||
                jobDef == JobDefOf.ExtinguishSelf ||
                jobDef == JobDefOf.BeatFire ||
                jobDef == JobDefOf.Ingest ||
                jobDef == JobDefOf.LayDown)
            {
                return true;
            }
            return !string.IsNullOrEmpty(jobDef.defName) && ForbiddenVehicleJobDefNames.Contains(jobDef.defName);
        }

        public static bool IsDisallowedThinkSourceNode(Pawn pawn, ThinkNode sourceNode)
        {
            if (sourceNode == null)
            {
                return false;
            }
            if (sourceNode is JobGiver_Work ||
                sourceNode is JobGiver_PatientGoToBed ||
                sourceNode is JobGiver_GetFood ||
                sourceNode is JobGiver_GetRest)
            {
                return true;
            }
            if (sourceNode is JobGiver_Wander && ShouldHoldStationInsteadOfWandering(pawn))
            {
                return true;
            }
            if (sourceNode is JobGiver_AIFollowPawn && IsPlayerFaction(pawn?.Faction))
            {
                return true;
            }
            return false;
        }

        public static bool IsDisallowedVehicleJob(Pawn pawn, CompGiantCockpit cockpit, Job job)
        {
            if (pawn == null || job?.def == null)
            {
                return false;
            }
            if (cockpit != null && !pawn.Dead && !pawn.Downed && cockpit.IsDormantUnmanned)
            {
                return !IsPassiveDormantAllowedJob(job.def);
            }
            if (job.workGiverDef != null && !job.playerForced)
            {
                return true;
            }
            if (IsWanderJobDef(job.def) && ShouldHoldStationInsteadOfWandering(pawn))
            {
                return true;
            }
            if (IsAutonomousFollowJob(job) && IsPlayerFaction(pawn.Faction))
            {
                return true;
            }
            return IsForbiddenNonVehicleJobDef(job.def, pawn.Downed);
        }

        public static Faction GetSafePlayerFaction()
        {
            return Current.Game?.World?.factionManager != null ? Faction.OfPlayerSilentFail : null;
        }

        public static bool IsPlayerFaction(Faction faction)
        {
            if (faction == null)
            {
                return false;
            }
            if (Current.Game?.World?.factionManager != null)
            {
                return faction == Faction.OfPlayerSilentFail;
            }
            return faction.def != null && faction.def.isPlayer;
        }

        public static bool ShouldHoldStationInsteadOfWandering(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || pawn.Drafted)
            {
                return false;
            }
            if (!IsPlayerFaction(pawn.Faction))
            {
                return false;
            }
            if (!IsVehiclePawn(pawn, out _, out _))
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
            return true;
        }

        public static Job MakeStationHoldJob(int expiryInterval = 180)
        {
            Job holdJob = Current.Game != null
                ? JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture)
                : new Job { def = JobDefOf.Wait_MaintainPosture ?? new JobDef { defName = "Wait_MaintainPosture" } };
            holdJob.expiryInterval = expiryInterval;
            holdJob.checkOverrideOnExpire = true;
            return holdJob;
        }

        public static Job MakeDownedHoldJob(int expiryInterval = 240)
        {
            Job downedJob = Current.Game != null
                ? JobMaker.MakeJob(JobDefOf.Wait_Downed)
                : new Job { def = JobDefOf.Wait_Downed ?? new JobDef { defName = "Wait_Downed" } };
            downedJob.expiryInterval = expiryInterval;
            downedJob.checkOverrideOnExpire = true;
            return downedJob;
        }

        /// <summary>
        /// Pre-ThinkTree choke point on Pawn_JobTracker.DetermineNextJob:
        /// 1. Downed vehicles immobilize in place (`Wait_Downed`), bypassing vanilla `&lt;Downed&gt;` crawl-flee (`FleeDanger`).
        /// 2. Unmanned Dormant or undrafted Piloted vehicles hold station without traversing the ThinkTree.
        /// </summary>
        public static bool TryShortCircuitDetermineNextJob(Pawn pawn, ref ThinkTreeDef thinkTree, out ThinkResult result)
        {
            result = ThinkResult.NoJob;
            if (pawn == null || pawn.Dead || (Current.Game != null && !pawn.Spawned))
            {
                return false;
            }

            bool isVehicle = IsVehiclePawn(pawn, out _, out CompGiantCockpit cockpit);
            if (!isVehicle)
            {
                if (!pawn.Downed && cockpit != null && (cockpit.ShouldNullifyThinkTree || cockpit.ShouldHoldPositionWhenPilotedUndrafted))
                {
                    thinkTree = null;
                    if (pawn.pather != null && pawn.pather.Moving)
                    {
                        pawn.pather.StopDead();
                    }
                    result = new ThinkResult(MakeStationHoldJob(120), null);
                    return true;
                }
                return false;
            }

            if (pawn.Downed)
            {
                thinkTree = null;
                if (pawn.pather != null && pawn.pather.Moving)
                {
                    pawn.pather.StopDead();
                }
                result = new ThinkResult(MakeDownedHoldJob(240), null);
                return true;
            }

            if (cockpit != null && (cockpit.ShouldNullifyThinkTree || cockpit.ShouldHoldPositionWhenPilotedUndrafted))
            {
                thinkTree = null;
                if (pawn.pather != null && pawn.pather.Moving)
                {
                    pawn.pather.StopDead();
                }
                result = new ThinkResult(MakeStationHoldJob(120), null);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Post-ThinkTree choke point on Pawn_JobTracker.DetermineNextJob:
        /// Replaces all scattered per-JobGiver Harmony patches in one unified pass. Converts peacetime wandering,
        /// Biotech Escort/Obedience following (`FollowClose`), colony labor (`JobGiver_Work` / modded `WorkGiver`),
        /// small-mech charger/shutdown, biological sleep/eat, and fire/panic flee into stationary hold.
        /// </summary>
        public static bool SanitizeThinkResultForVehicle(Pawn pawn, ref ThinkResult result)
        {
            Job job = result.Job;
            if (job?.def == null || pawn == null || !IsVehiclePawn(pawn, out _, out CompGiantCockpit cockpit))
            {
                return false;
            }

            if (!IsDisallowedThinkSourceNode(pawn, result.SourceNode) &&
                !IsDisallowedVehicleJob(pawn, cockpit, job))
            {
                return false;
            }

            if (cockpit != null && cockpit.IsDormantUnmanned && pawn.pather != null && pawn.pather.Moving)
            {
                pawn.pather.StopDead();
            }
            if (Current.Game != null)
            {
                JobMaker.ReturnToPool(job);
            }
            Job replacement = pawn.Downed ? MakeDownedHoldJob(240) : MakeStationHoldJob(180);
            result = new ThinkResult(replacement, result.SourceNode);
            return true;
        }

        /// <summary>
        /// Direct imperative job firewall on Pawn_JobTracker.StartJob:
        /// Catches jobs pushed directly outside DetermineNextJob (explosions, fire attachment, external scripts).
        /// Replaces disallowed jobs with Wait_MaintainPosture when curJob is null so RimWorld's 10-jobs-in-10-ticks
        /// TryStartErrorRecoverJob loop never trips.
        /// </summary>
        public static bool TryFilterVehicleStartJob(Pawn pawn, ref Job newJob, out bool allowStart)
        {
            allowStart = true;
            if (pawn == null || newJob?.def == null || !IsVehiclePawn(pawn, out _, out CompGiantCockpit cockpit))
            {
                return false;
            }

            if (!IsDisallowedVehicleJob(pawn, cockpit, newJob))
            {
                return false;
            }

            if (cockpit != null && cockpit.IsDormantUnmanned && pawn.pather != null && pawn.pather.Moving)
            {
                pawn.pather.StopDead();
            }
            RewriteOrBlockJob(pawn, ref newJob, expiryInterval: 150);
            allowStart = newJob != null;
            return true;
        }

        public static bool ShouldSuppressVehicleNeed(Pawn pawn, NeedDef needDef)
        {
            if (pawn == null || needDef == null || !IsVehiclePawn(pawn, out _, out _))
            {
                return false;
            }
            if (needDef == NeedDefOf.Food || needDef == NeedDefOf.Rest)
            {
                return true;
            }
            return !string.IsNullOrEmpty(needDef.defName) && SuppressedVehicleNeedDefNames.Contains(needDef.defName);
        }

        public static bool ShouldBlockSmallPawnCarry(Pawn carrier, Thing item)
        {
            if (carrier == null || !(item is Pawn targetPawn))
            {
                return false;
            }
            return GiantFastLookup.TryGetRig(targetPawn) != null
                && GiantFastLookup.TryGetRig(carrier) == null;
        }

        public static bool ShouldSuppressPawnCall(Pawn pawn, bool forceAggressive)
        {
            if (pawn == null || !IsVehiclePawn(pawn, out _, out CompGiantCockpit cockpit))
            {
                return false;
            }
            if (cockpit != null && cockpit.IsDormantUnmanned)
            {
                return true;
            }
            return !forceAggressive;
        }

        public static bool ShouldExcludeFromSendableCaravanPawns(Pawn pawn)
        {
            return pawn != null
                && (GiantFastLookup.TryGetRig(pawn) != null || GiantFastLookup.TryGetCockpit(pawn) != null);
        }

        private static void RewriteOrBlockJob(Pawn pawn, ref Job newJob, int expiryInterval)
        {
            if (Current.Game != null)
            {
                JobMaker.ReturnToPool(newJob);
            }
            if (pawn.jobs != null && pawn.jobs.curJob == null && !pawn.Dead)
            {
                newJob = pawn.Downed ? MakeDownedHoldJob(expiryInterval) : MakeStationHoldJob(expiryInterval);
            }
            else
            {
                newJob = null;
            }
        }
    }
}
