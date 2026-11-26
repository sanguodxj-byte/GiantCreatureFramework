using System;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public enum GiantDailyActivityKind : byte
    {
        None = 0,
        Ingesting = 1,
        SocialAffection = 2
    }

    /// <summary>
    /// Lightweight procedural pose overlay for biological giant daily behaviors:
    /// 1. Ingesting (food / hunted corpse): strictly neck + head ONLY — forward cervical reach, rhythmic snout pecking,
    ///    and alternating side-to-side meat-tearing head tugs.
    /// 2. SocialAffection (Nuzzle, Tame, Train): strictly neck + head ONLY — gentle forward reach and affectionate head-rub sway.
    /// Thorax, pelvis/hindquarters, legs, wings, and tail remain completely stationary (zero hip/tail sway, zero leg yaw).
    /// </summary>
    public static class GiantDailyActivityPoseSolver
    {
        public const float BlendInDurationTicks = 12f;
        public const float BlendOutDurationTicks = 10f;
        public const float IngestPeckPeriodTicks = 28f;
        public const float IngestTearPeriodTicks = 56f;
        public const float SocialNuzzlePeriodTicks = 48f;
        public const int NuzzleLingerDurationTicks = 78;

        public static GiantDailyActivityKind ResolveActivityKind(
            bool isVehiclePawn,
            bool isMechanoid,
            bool isDeadOrDowned,
            bool isDrafted,
            bool isAirborneOrFlyingEntry,
            bool isMoving,
            bool isSleepingOrCurling,
            bool isNonIdleCombatOrMorphState,
            Job currentJob,
            bool hasActiveExternalTamerOrTrainer,
            bool hasActiveNuzzleLinger)
        {
            return ResolveActivityKind(
                isVehiclePawn,
                isMechanoid,
                isDeadOrDowned,
                isDrafted,
                isAirborneOrFlyingEntry,
                isMoving,
                isSleepingOrCurling,
                isNonIdleCombatOrMorphState,
                currentJob,
                hasActiveExternalTamerOrTrainer,
                hasActiveNuzzleLinger,
                selfPawn: null);
        }

        public static GiantDailyActivityKind ResolveActivityKind(
            bool isVehiclePawn,
            bool isMechanoid,
            bool isDeadOrDowned,
            bool isDrafted,
            bool isAirborneOrFlyingEntry,
            bool isMoving,
            bool isSleepingOrCurling,
            bool isNonIdleCombatOrMorphState,
            Job currentJob,
            bool hasActiveExternalTamerOrTrainer,
            bool hasActiveNuzzleLinger,
            Pawn selfPawn)
        {
            if (isVehiclePawn
                || isMechanoid
                || isDeadOrDowned
                || isDrafted
                || isAirborneOrFlyingEntry
                || isMoving
                || isSleepingOrCurling
                || isNonIdleCombatOrMorphState)
            {
                return GiantDailyActivityKind.None;
            }

            string jobName = currentJob?.def?.defName;
            if (!string.IsNullOrEmpty(jobName))
            {
                if (string.Equals(jobName, "Ingest", StringComparison.Ordinal))
                {
                    return GiantDailyActivityKind.Ingesting;
                }
                if (string.Equals(jobName, "PredatorHunt", StringComparison.Ordinal)
                    && currentJob.targetA.HasThing
                    && currentJob.targetA.Thing is Corpse)
                {
                    return GiantDailyActivityKind.Ingesting;
                }
                if (string.Equals(jobName, "Nuzzle", StringComparison.Ordinal))
                {
                    return GiantDailyActivityKind.SocialAffection;
                }
                if (string.Equals(jobName, "Wait", StringComparison.Ordinal))
                {
                    Pawn targetA = currentJob.targetA.HasThing ? currentJob.targetA.Thing as Pawn : null;
                    Pawn targetB = currentJob.targetB.HasThing ? currentJob.targetB.Thing as Pawn : null;
                    if ((targetA != null && targetA.RaceProps?.Humanlike == true && !IsHostileHumanlike(selfPawn, targetA))
                        || (targetB != null && targetB.RaceProps?.Humanlike == true && !IsHostileHumanlike(selfPawn, targetB)))
                    {
                        return GiantDailyActivityKind.SocialAffection;
                    }
                }
            }

            if (hasActiveExternalTamerOrTrainer || hasActiveNuzzleLinger)
            {
                return GiantDailyActivityKind.SocialAffection;
            }

            return GiantDailyActivityKind.None;
        }

        private static bool IsHostileHumanlike(Pawn selfPawn, Pawn other)
        {
            if (selfPawn == null || other == null)
            {
                return false;
            }
            if (selfPawn.mindState?.mentalStateHandler != null && selfPawn.InAggroMentalState)
            {
                return true;
            }
            if (selfPawn.mindState?.enemyTarget == other)
            {
                return true;
            }
            if (selfPawn.Faction != null && other.Faction != null)
            {
                return selfPawn.HostileTo(other);
            }
            return false;
        }

        public static void ApplyDailyActivity(
            GiantRigDef rig,
            BoneLocalPose[] inoutLocalPoses,
            ReadOnlySpan<bool> partMissingMask,
            float ingestWeight01,
            float socialWeight01,
            float activityPhaseTicks,
            bool includeBezierBones = true)
        {
            if (rig == null || inoutLocalPoses == null || (ingestWeight01 <= 1e-4f && socialWeight01 <= 1e-4f))
            {
                return;
            }

            GiantSleepCurlProfile profile = GiantSleepCurlSolver.GetOrBuildProfile(rig);
            if (profile == null || profile.NodeCount <= 0)
            {
                return;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = Mathf.Min(nodes.Length, Mathf.Min(inoutLocalPoses.Length, profile.NodeCount));
            float wIngest = GiantSleepCurlSolver.SmoothStep01(ingestWeight01);
            float wSocial = GiantSleepCurlSolver.SmoothStep01(socialWeight01);
            float wActive = Mathf.Clamp01(wIngest + wSocial);

            float peckRad = (2f * Mathf.PI * activityPhaseTicks) / IngestPeckPeriodTicks;
            float peck01 = 0.5f + 0.5f * Mathf.Sin(peckRad - 0.35f * Mathf.Cos(peckRad));
            float tearRad = (2f * Mathf.PI * activityPhaseTicks) / IngestTearPeriodTicks;
            float tearSin = Mathf.Sin(tearRad);

            float nuzRad = (2f * Mathf.PI * activityPhaseTicks) / SocialNuzzlePeriodTicks;
            float nuzCos = Mathf.Cos(nuzRad);

            // Stabilize root thorax so pelvis/legs/wings never shift or sway during stationary head-only actions
            if (count > 0 && !nodes[0].isRigid)
            {
                BoneLocalPose rootPose = inoutLocalPoses[0];
                rootPose.offset = Vector2.LerpUnclamped(rootPose.offset, Vector2.zero, wActive);
                rootPose.angle += Mathf.DeltaAngle(rootPose.angle, 0f) * wActive;
                inoutLocalPoses[0] = rootPose;
            }

            for (int i = 0; i < count; i++)
            {
                if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                {
                    inoutLocalPoses[i] = BoneLocalPose.Identity;
                    continue;
                }
                if (!partMissingMask.IsEmpty && i < partMissingMask.Length && partMissingMask[i] && nodes[i].hideWhenMissing)
                {
                    continue;
                }
                if (!includeBezierBones && profile.IsBezierChainBone[i])
                {
                    continue;
                }

                GiantSleepBoneRole role = profile.Roles[i];
                BoneLocalPose pose = inoutLocalPoses[i];

                if (role == GiantSleepBoneRole.Leg)
                {
                    // Strict hard rule: top-down limb bones MUST NEVER rotate horizontally (angle == 0 deg).
                    pose.angle = 0f;
                    inoutLocalPoses[i] = pose;
                    continue;
                }

                if (role == GiantSleepBoneRole.PosteriorTorso || role == GiantSleepBoneRole.Tail)
                {
                    // Keep pelvis, hindquarters, and tail completely steady (zero hip/tail sway)
                    pose.angle += Mathf.DeltaAngle(pose.angle, 0f) * wActive;
                    inoutLocalPoses[i] = pose;
                    continue;
                }

                if (role != GiantSleepBoneRole.AnteriorNeckHead)
                {
                    continue;
                }

                int chainLen = Mathf.Max(1, profile.RoleChainLength[i]);
                int d = profile.RoleDepth[i];
                float u = chainLen > 1 ? d / (float)(chainLen - 1) : 1f;
                float segBias = Mathf.Lerp(0.70f, 1.30f, u);

                if (wIngest > 1e-4f)
                {
                    float tearDeg = (24f / chainLen) * segBias * tearSin * (0.45f + 0.55f * peck01);
                    pose.angle += tearDeg * wIngest;
                    if (profile.HasSagittalTarget[i])
                    {
                        // Positive sagittalPitch extends coupled neck/head sockets forward toward food while thorax/pelvis stay still.
                        pose.sagittalPitch += (10f + 28f * peck01) * wIngest;
                    }
                }
                if (wSocial > 1e-4f)
                {
                    float rubDeg = (28f / chainLen) * segBias * Mathf.Sin(nuzRad - 0.22f * d);
                    pose.angle += rubDeg * wSocial;
                    if (profile.HasSagittalTarget[i])
                    {
                        pose.sagittalPitch += (8f + 10f * (0.5f + 0.5f * nuzCos)) * wSocial;
                    }
                }

                inoutLocalPoses[i] = pose;
            }
        }

        public static void ApplyDailyActivityToBezierBones(
            GiantRigDef rig,
            BoneLocalPose[] inoutLocalPoses,
            ReadOnlySpan<bool> partMissingMask,
            float ingestWeight01,
            float socialWeight01,
            float activityPhaseTicks)
        {
            if (rig == null || inoutLocalPoses == null || (ingestWeight01 <= 1e-4f && socialWeight01 <= 1e-4f))
            {
                return;
            }

            GiantSleepCurlProfile profile = GiantSleepCurlSolver.GetOrBuildProfile(rig);
            if (profile == null || profile.NodeCount <= 0)
            {
                return;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = Mathf.Min(nodes.Length, Mathf.Min(inoutLocalPoses.Length, profile.NodeCount));
            float wIngest = GiantSleepCurlSolver.SmoothStep01(ingestWeight01);
            float wSocial = GiantSleepCurlSolver.SmoothStep01(socialWeight01);
            float wActive = Mathf.Clamp01(wIngest + wSocial);
            float tearRad = (2f * Mathf.PI * activityPhaseTicks) / IngestTearPeriodTicks;
            float nuzRad = (2f * Mathf.PI * activityPhaseTicks) / SocialNuzzlePeriodTicks;

            for (int i = 0; i < count; i++)
            {
                if (!profile.IsBezierChainBone[i])
                {
                    continue;
                }
                if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                {
                    inoutLocalPoses[i] = BoneLocalPose.Identity;
                    continue;
                }
                if (!partMissingMask.IsEmpty && i < partMissingMask.Length && partMissingMask[i] && nodes[i].hideWhenMissing)
                {
                    continue;
                }

                GiantSleepBoneRole role = profile.Roles[i];
                BoneLocalPose pose = inoutLocalPoses[i];
                if (role == GiantSleepBoneRole.Leg)
                {
                    pose.angle = 0f;
                    inoutLocalPoses[i] = pose;
                    continue;
                }

                if (role == GiantSleepBoneRole.PosteriorTorso || role == GiantSleepBoneRole.Tail)
                {
                    pose.angle += Mathf.DeltaAngle(pose.angle, 0f) * wActive;
                    inoutLocalPoses[i] = pose;
                    continue;
                }

                if (role == GiantSleepBoneRole.AnteriorNeckHead)
                {
                    int chainLen = Mathf.Max(1, profile.RoleChainLength[i]);
                    int d = profile.RoleDepth[i];
                    float u = chainLen > 1 ? d / (float)(chainLen - 1) : 1f;
                    float segBias = Mathf.Lerp(0.70f, 1.30f, u);
                    if (wIngest > 1e-4f)
                    {
                        float peckRad = (2f * Mathf.PI * activityPhaseTicks) / IngestPeckPeriodTicks;
                        float peck01 = 0.5f + 0.5f * Mathf.Sin(peckRad - 0.35f * Mathf.Cos(peckRad));
                        pose.angle += (24f / chainLen) * segBias * Mathf.Sin(tearRad) * (0.45f + 0.55f * peck01) * wIngest;
                        if (profile.HasSagittalTarget[i])
                        {
                            pose.sagittalPitch += (10f + 28f * peck01) * wIngest;
                        }
                    }
                    if (wSocial > 1e-4f)
                    {
                        float nuzCos = Mathf.Cos(nuzRad);
                        pose.angle += (28f / chainLen) * segBias * Mathf.Sin(nuzRad - 0.22f * d) * wSocial;
                        if (profile.HasSagittalTarget[i])
                        {
                            pose.sagittalPitch += (8f + 10f * (0.5f + 0.5f * nuzCos)) * wSocial;
                        }
                    }
                }
                inoutLocalPoses[i] = pose;
            }
        }
    }
}
