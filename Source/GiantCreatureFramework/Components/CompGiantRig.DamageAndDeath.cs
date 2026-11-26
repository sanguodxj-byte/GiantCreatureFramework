using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Damage flashes, retaliation, and death/corpse animation lifecycle.
    public partial class CompGiantRig
    {
        public void Notify_PreRoutedBoneHit(int boneIndex)
        {
            pendingRoutedHitBoneIndex = boneIndex;
        }

        public BodyPartRecord[] LinkedBodyPartsArray => linkedBodyParts;
        public BodyPartRecord[] LinkedSoftWhipPartsArray => linkedSoftWhipTendrilParts;

        public BodyPartRecord GetLinkedBodyPartRecord(int boneIndex)
        {
            return linkedBodyParts != null && (uint)boneIndex < (uint)linkedBodyParts.Length
                ? linkedBodyParts[boneIndex]
                : null;
        }

        public BodyPartRecord GetLinkedSoftWhipBodyPartRecord(int whipIndex)
        {
            return linkedSoftWhipTendrilParts != null && (uint)whipIndex < (uint)linkedSoftWhipTendrilParts.Length
                ? linkedSoftWhipTendrilParts[whipIndex]
                : null;
        }

        public float GetSoftWhipHealthPercentThreadSafe(int whipIndex)
        {
            return softWhipHealthPct != null && (uint)whipIndex < (uint)softWhipHealthPct.Length
                ? softWhipHealthPct[whipIndex]
                : 1f;
        }

        public static float ComputeDamageFlash01(int lastDamageTick, int currentGameTick)
        {
            if (lastDamageTick < -1000)
            {
                return 0f;
            }
            int ticksLeft = lastDamageTick + PartDamageFlashTicksTotal - currentGameTick;
            return ticksLeft > 0 ? Mathf.Clamp01((float)ticksLeft / PartDamageFlashTicksTotal) : 0f;
        }

        private int ResolveCurrentGameTickThreadSafe()
        {
            if (UnityData.IsInMainThread && Find.TickManager != null)
            {
                int t = Find.TickManager.TicksGame;
                latestPublishedGameTick = t;
                return t;
            }
            return latestPublishedGameTick;
        }

        public float GetBoneDamageFlash01(int boneIndex)
        {
            if (boneLastDamageTick == null || (uint)boneIndex >= (uint)boneLastDamageTick.Length)
            {
                return 0f;
            }
            return ComputeDamageFlash01(boneLastDamageTick[boneIndex], ResolveCurrentGameTickThreadSafe());
        }

        public float GetSoftWhipDamageFlash01(int whipIndex)
        {
            if (softWhipLastDamageTick == null || (uint)whipIndex >= (uint)softWhipLastDamageTick.Length)
            {
                return 0f;
            }
            return ComputeDamageFlash01(softWhipLastDamageTick[whipIndex], ResolveCurrentGameTickThreadSafe());
        }

        /// <summary>
        /// Pure resolver mapping an applied DamageInfo.HitPart (and optional raycasted bone hint)
        /// to the exact visual bone(s) or soft-whip ribbon that should flash red.
        /// </summary>
        public static bool TryResolveFlashTargetsForHitPart(
            BoneNodeDef[] sortedNodes,
            BodyPartRecord[] boneLinkedParts,
            BodyPartRecord[] whipLinkedParts,
            BodyPartRecord hitPart,
            int hintedBoneIndex,
            Span<bool> outBoneFlashed,
            Span<bool> outWhipFlashed)
        {
            outBoneFlashed.Clear();
            outWhipFlashed.Clear();
            if (sortedNodes == null || sortedNodes.Length == 0)
            {
                return false;
            }

            if (hitPart != null && whipLinkedParts != null)
            {
                int wLen = Mathf.Min(whipLinkedParts.Length, outWhipFlashed.Length);
                for (int s = 0; s < wLen; s++)
                {
                    BodyPartRecord wPart = whipLinkedParts[s];
                    if (wPart != null && (hitPart == wPart || GiantRigDef.IsBodyPartStrictDescendant(hitPart, wPart)))
                    {
                        outWhipFlashed[s] = true;
                        return true;
                    }
                }
            }

            if ((uint)hintedBoneIndex < (uint)sortedNodes.Length && (uint)hintedBoneIndex < (uint)outBoneFlashed.Length)
            {
                BodyPartRecord hintedPart = (boneLinkedParts != null && hintedBoneIndex < boneLinkedParts.Length)
                    ? boneLinkedParts[hintedBoneIndex]
                    : null;
                if (hitPart == null || hintedPart == null || hitPart == hintedPart || GiantRigDef.IsBodyPartStrictDescendant(hitPart, hintedPart))
                {
                    outBoneFlashed[hintedBoneIndex] = true;
                    return true;
                }
            }

            if (hitPart != null && boneLinkedParts != null)
            {
                int bLen = Mathf.Min(Mathf.Min(sortedNodes.Length, boneLinkedParts.Length), outBoneFlashed.Length);
                BodyPartRecord cur = hitPart;
                while (cur != null)
                {
                    bool matchedHitboxBone = false;
                    for (int i = 0; i < bLen; i++)
                    {
                        if (boneLinkedParts[i] == cur && sortedNodes[i].contributesToHitbox)
                        {
                            outBoneFlashed[i] = true;
                            matchedHitboxBone = true;
                        }
                    }
                    if (matchedHitboxBone)
                    {
                        return true;
                    }
                    for (int i = 0; i < bLen; i++)
                    {
                        if (boneLinkedParts[i] == cur)
                        {
                            outBoneFlashed[i] = true;
                            return true;
                        }
                    }
                    cur = cur.parent;
                }
            }

            if (outBoneFlashed.Length > 0)
            {
                outBoneFlashed[0] = true;
                return true;
            }
            return false;
        }

        public void RecordPartDamageFlash(BodyPartRecord hitPart, int hintedBoneIndex, int gameTick)
        {
            if (RigDef == null)
            {
                return;
            }
            EnsureBuffersInitialized();
            latestPublishedGameTick = gameTick;
            int bCount = boneLastDamageTick?.Length ?? 0;
            int wCount = softWhipLastDamageTick?.Length ?? 0;
            Span<bool> bFlashed = bCount <= 64 ? stackalloc bool[bCount] : new bool[bCount];
            Span<bool> wFlashed = wCount <= 16 ? stackalloc bool[wCount] : new bool[wCount];

            if (TryResolveFlashTargetsForHitPart(
                    RigDef.SortedNodes,
                    linkedBodyParts,
                    linkedSoftWhipTendrilParts,
                    hitPart,
                    hintedBoneIndex,
                    bFlashed,
                    wFlashed))
            {
                for (int i = 0; i < bCount; i++)
                {
                    if (bFlashed[i])
                    {
                        boneLastDamageTick[i] = gameTick;
                    }
                }
                for (int s = 0; s < wCount; s++)
                {
                    if (wFlashed[s])
                    {
                        softWhipLastDamageTick[s] = gameTick;
                    }
                }
            }
        }

        private Vector2 lastHitWorldPushDirXZ = Vector2.zero;
        private int lastHitWorldPushTick = -99999;

        private Vector2 ResolveHitWorldPushDirection(DamageInfo dinfo)
        {
            if (Pawn != null && Pawn.Spawned && dinfo.Instigator != null && dinfo.Instigator.Spawned && dinfo.Instigator.Map == Pawn.Map)
            {
                Vector3 pawnPos = GetSafeDrawPos(Pawn);
                Vector3 instPos = dinfo.Instigator.DrawPos;
                Vector2 delta = new Vector2(pawnPos.x - instPos.x, pawnPos.z - instPos.z);
                if (delta.sqrMagnitude > 1e-4f)
                {
                    return delta.normalized;
                }
            }
            return SkeletalKinematics.RotateCW(Vector2.up, dinfo.Angle);
        }

        private float EstimateTotalBodyMaxHp()
        {
            if (cachedBoneMaxHp != null && cachedBoneMaxHp.Length > 0)
            {
                float sum = 0f;
                for (int i = 0; i < cachedBoneMaxHp.Length; i++)
                {
                    sum += cachedBoneMaxHp[i];
                }
                if (sum > 20f)
                {
                    return sum;
                }
            }
            float healthScale = Pawn?.RaceProps?.baseHealthScale ?? 4f;
            return Mathf.Max(120f, healthScale * 120f);
        }

        private void ResolvePrimaryHitBoneAndWhip(BodyPartRecord hitPart, int hintedBoneIndex, out int hitBoneIndex, out int hitWhipIndex)
        {
            hitBoneIndex = (RigDef != null && (uint)hintedBoneIndex < (uint)RigDef.SortedNodes.Length) ? hintedBoneIndex : 0;
            hitWhipIndex = -1;
            if (RigDef == null || hitPart == null)
            {
                return;
            }
            int bCount = RigDef.SortedNodes.Length;
            int wCount = RigDef.softWhips?.Count ?? 0;
            Span<bool> bFlags = bCount <= 64 ? stackalloc bool[bCount] : new bool[bCount];
            Span<bool> wFlags = wCount <= 16 ? stackalloc bool[wCount] : new bool[wCount];
            if (!TryResolveFlashTargetsForHitPart(
                    RigDef.SortedNodes,
                    linkedBodyParts,
                    linkedSoftWhipTendrilParts,
                    hitPart,
                    hintedBoneIndex,
                    bFlags,
                    wFlags))
            {
                return;
            }
            for (int i = 0; i < bCount; i++)
            {
                if (bFlags[i])
                {
                    hitBoneIndex = i;
                    break;
                }
            }
            for (int s = 0; s < wCount; s++)
            {
                if (wFlags[s])
                {
                    hitWhipIndex = s;
                    break;
                }
            }
        }

        private void ApplyHitReactionChainImpulses(in GiantHitChainImpulseOutput chainOut)
        {
            if (chainOut.Tier == GiantHitReactionTier.None)
            {
                return;
            }
            if (bezierRuntimes != null && chainOut.BezierWorldImpulseXZ.sqrMagnitude > 1e-8f)
            {
                for (int c = 0; c < bezierRuntimes.Length; c++)
                {
                    bezierRuntimes[c]?.AddWorldImpulse(chainOut.BezierWorldImpulseXZ);
                }
            }
            if (softWhipRuntimes != null && chainOut.SoftWhipWorldImpulseXZ.sqrMagnitude > 1e-8f)
            {
                if ((uint)chainOut.TargetSoftWhipIndex < (uint)softWhipRuntimes.Length)
                {
                    softWhipRuntimes[chainOut.TargetSoftWhipIndex]?.AddWorldImpulse(chainOut.SoftWhipWorldImpulseXZ);
                }
                else
                {
                    Vector2 shared = chainOut.SoftWhipWorldImpulseXZ * 0.65f;
                    for (int s = 0; s < softWhipRuntimes.Length; s++)
                    {
                        softWhipRuntimes[s]?.AddWorldImpulse(shared);
                    }
                }
            }
            if (chainOut.SleepWakeImpulse01 > 0.01f && sleepCurlProgress01 > 0.01f)
            {
                sleepCurlProgress01 = Mathf.Max(0f, sleepCurlProgress01 - chainOut.SleepWakeImpulse01 * 0.45f);
            }
        }

        private void TriggerAnimalHitReaction(DamageInfo dinfo, float totalDamageDealt, int hintedBoneIndex, int nowTick)
        {
            if (Pawn == null || Pawn.Dead || RigDef == null || !IsAnimalHitReactionEligible)
            {
                return;
            }
            if (animState == GiantAnimState.Death || animState == GiantAnimState.MorphLocked || flightEntryActive || ManualOverrideActive)
            {
                return;
            }
            EnsureBuffersInitialized();
            if (hitReactionRuntime == null)
            {
                return;
            }

            Vector2 worldPush = ResolveHitWorldPushDirection(dinfo);
            lastHitWorldPushDirXZ = worldPush;
            lastHitWorldPushTick = nowTick;

            ResolvePrimaryHitBoneAndWhip(dinfo.HitPart, hintedBoneIndex, out int hitBoneIdx, out int hitWhipIdx);
            float partMaxHp = (cachedBoneMaxHp != null && (uint)hitBoneIdx < (uint)cachedBoneMaxHp.Length)
                ? cachedBoneMaxHp[hitBoneIdx]
                : (dinfo.HitPart != null && Pawn != null ? Mathf.Max(10f, dinfo.HitPart.def.GetMaxHealth(Pawn)) : 60f);

            var hitInput = new GiantHitImpulseInput
            {
                HitBoneIndex = hitBoneIdx,
                HitSoftWhipIndex = hitWhipIdx,
                WorldPushDirXZ = worldPush,
                BodyHeadingDeg = currentHeadingDeg,
                DamageAmount = Mathf.Max(0f, totalDamageDealt),
                HitPartMaxHp = partMaxHp,
                TotalBodyMaxHp = EstimateTotalBodyMaxHp(),
                PawnBodySize = Pawn.BodySize,
                DamageCategory = GiantHitReactionRuntime.ClassifyDamageDef(dinfo.Def),
                GameTick = nowTick,
                IsAirborne = IsCurrentlyAirborne,
                IsAquatic = IsCurrentlyInWater,
                IsDowned = Pawn.Downed,
                IsSleeping = sleepCurlProgress01 > 0.08f
            };

            ReadOnlySpan<bool> missingSpan = partMissingMask != null ? partMissingMask : ReadOnlySpan<bool>.Empty;
            GiantHitChainImpulseOutput chainOut = hitReactionRuntime.TriggerHit(RigDef, in hitInput, missingSpan);
            ApplyHitReactionChainImpulses(in chainOut);
        }

        internal void TriggerPartBreakHitReaction(int severedBoneIndex, int nowTick)
        {
            if (Pawn == null || Pawn.Dead || RigDef == null || !IsAnimalHitReactionEligible)
            {
                return;
            }
            if (animState == GiantAnimState.Death || animState == GiantAnimState.MorphLocked || flightEntryActive || ManualOverrideActive)
            {
                return;
            }
            EnsureBuffersInitialized();
            if (hitReactionRuntime == null)
            {
                return;
            }

            Vector2 worldPush = (nowTick - lastHitWorldPushTick <= 4 && lastHitWorldPushDirXZ.sqrMagnitude > 1e-4f)
                ? lastHitWorldPushDirXZ
                : -SkeletalKinematics.RotateCW(Vector2.up, currentHeadingDeg);

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            int targetBone = (uint)severedBoneIndex < (uint)nodes.Length ? severedBoneIndex : 0;
            if (targetBone > 0 && partMissingMask != null && targetBone < partMissingMask.Length && partMissingMask[targetBone])
            {
                int parentIdx = nodes[targetBone].parentRuntimeIndex;
                if (parentIdx >= 0 && parentIdx < nodes.Length)
                {
                    targetBone = parentIdx;
                }
            }

            var hitInput = new GiantHitImpulseInput
            {
                HitBoneIndex = targetBone,
                HitSoftWhipIndex = -1,
                WorldPushDirXZ = worldPush,
                BodyHeadingDeg = currentHeadingDeg,
                DamageAmount = EstimateTotalBodyMaxHp() * 0.25f,
                HitPartMaxHp = 100f,
                TotalBodyMaxHp = EstimateTotalBodyMaxHp(),
                PawnBodySize = Pawn.BodySize,
                DamageCategory = GiantHitDamageCategory.BluntOrExplosive,
                GameTick = nowTick,
                IsAirborne = IsCurrentlyAirborne,
                IsAquatic = IsCurrentlyInWater,
                IsDowned = Pawn.Downed,
                IsSleeping = sleepCurlProgress01 > 0.08f,
                ForceTierOverride = true,
                ForcedTier = GiantHitReactionTier.PartBreakStagger
            };

            ReadOnlySpan<bool> missingSpan = partMissingMask != null ? partMissingMask : ReadOnlySpan<bool>.Empty;
            GiantHitChainImpulseOutput chainOut = hitReactionRuntime.TriggerHit(RigDef, in hitInput, missingSpan);
            ApplyHitReactionChainImpulses(in chainOut);
        }

        public bool TriggerSyntheticHitReaction(
            GiantHitReactionTier tier,
            int hitBoneIndex = -1,
            float impactWorldPushAngleDeg = float.NaN,
            bool flashBone = true)
        {
            if (Pawn == null || Pawn.Dead || RigDef == null || tier == GiantHitReactionTier.None)
            {
                return false;
            }
            EnsureBuffersInitialized();
            if (hitReactionRuntime == null)
            {
                return false;
            }

            int nowTick = Current.Game?.tickManager?.TicksGame ?? latestPublishedGameTick;
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            int resolvedBone = (uint)hitBoneIndex < (uint)nodes.Length ? hitBoneIndex : 0;
            if (hitBoneIndex < 0)
            {
                // Pick a representative non-root bone (head, wing, or limb) for immediate visual clarity.
                GiantSleepCurlProfile prof = GiantSleepCurlSolver.GetOrBuildProfile(RigDef);
                if (prof != null)
                {
                    for (int i = 1; i < nodes.Length && i < prof.Roles.Length; i++)
                    {
                        if (prof.Roles[i] == GiantSleepBoneRole.AnteriorNeckHead || prof.Roles[i] == GiantSleepBoneRole.Wing)
                        {
                            resolvedBone = i;
                            break;
                        }
                    }
                }
            }

            float pushDeg = float.IsNaN(impactWorldPushAngleDeg)
                ? SkeletalKinematics.NormalizeAngle360(currentHeadingDeg + 115f)
                : impactWorldPushAngleDeg;
            Vector2 worldPush = SkeletalKinematics.RotateCW(Vector2.up, pushDeg);

            var hitInput = new GiantHitImpulseInput
            {
                HitBoneIndex = resolvedBone,
                HitSoftWhipIndex = -1,
                WorldPushDirXZ = worldPush,
                BodyHeadingDeg = currentHeadingDeg,
                DamageAmount = tier >= GiantHitReactionTier.HeavyFlinch ? 80f : 22f,
                HitPartMaxHp = 100f,
                TotalBodyMaxHp = EstimateTotalBodyMaxHp(),
                PawnBodySize = Pawn.BodySize,
                DamageCategory = tier >= GiantHitReactionTier.HeavyFlinch ? GiantHitDamageCategory.BluntOrExplosive : GiantHitDamageCategory.SharpOrBallistic,
                GameTick = nowTick,
                IsAirborne = IsCurrentlyAirborne,
                IsAquatic = IsCurrentlyInWater,
                IsDowned = Pawn.Downed,
                IsSleeping = sleepCurlProgress01 > 0.08f,
                ForceTierOverride = true,
                ForcedTier = tier
            };

            ReadOnlySpan<bool> missingSpan = partMissingMask != null ? partMissingMask : ReadOnlySpan<bool>.Empty;
            GiantHitChainImpulseOutput chainOut = hitReactionRuntime.TriggerHit(RigDef, in hitInput, missingSpan);
            ApplyHitReactionChainImpulses(in chainOut);

            if (flashBone && boneLastDamageTick != null && (uint)resolvedBone < (uint)boneLastDamageTick.Length)
            {
                boneLastDamageTick[resolvedBone] = nowTick;
            }
            return chainOut.Tier != GiantHitReactionTier.None;
        }

        public override void PostPostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            int hintedBone = pendingRoutedHitBoneIndex;
            pendingRoutedHitBoneIndex = -1;
            int nowTick = Find.TickManager?.TicksGame ?? latestPublishedGameTick;
            if (dinfo.Def != null && dinfo.Def.harmsHealth && (totalDamageDealt > 0.001f || dinfo.Amount > 0.001f))
            {
                RecordPartDamageFlash(dinfo.HitPart, hintedBone, nowTick);
                InvalidateWoundDecalCache();
            }
            base.PostPostApplyDamage(dinfo, totalDamageDealt);
            if (dinfo.Def != null && dinfo.Def.harmsHealth && totalDamageDealt > 0.001f)
            {
                TriggerAnimalHitReaction(dinfo, totalDamageDealt, hintedBone, nowTick);
            }
            RefreshMissingPartsAfterDamageOrDeath();
            if (dinfo.Def != null && dinfo.Def.harmsHealth && totalDamageDealt > 0.001f)
            {
                GiantBloodFilthUtility.TrySpawnHitBloodFilth(Pawn, this, dinfo, totalDamageDealt, hintedBone);
            }
            RegisterDamageRetaliation(dinfo, totalDamageDealt);
        }

        public void RegisterDamageRetaliation(DamageInfo dinfo, float totalDamageDealt, int fallbackTick = 0)
        {
            if (Pawn == null || (Pawn.health != null && (Pawn.Dead || Pawn.Downed)) || dinfo.Def == null || !dinfo.Def.harmsHealth)
            {
                return;
            }
            if (totalDamageDealt <= 0.001f && dinfo.Amount <= 0.001f)
            {
                return;
            }

            Thing instigator = dinfo.Instigator;
            if (instigator == null || instigator == Pawn || instigator.Destroyed)
            {
                return;
            }
            if (Pawn.Spawned && (!instigator.Spawned || instigator.Map != Pawn.Map))
            {
                return;
            }
            if (Pawn.Faction != null && Pawn.Faction.IsPlayer && Pawn.Drafted)
            {
                return;
            }
            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(Pawn);
            if (cockpit != null && (cockpit.ShouldNullifyThinkTree || cockpit.ShouldHoldPositionWhenPilotedUndrafted))
            {
                return;
            }
            if (Pawn.Faction != null && instigator.Faction == Pawn.Faction && !Pawn.InMentalState)
            {
                return;
            }

            int nowTick = Current.Game?.tickManager != null ? Current.Game.tickManager.TicksGame : fallbackTick;
            if (nowTick > provokedUntilTick)
            {
                provokedInstigatorIds.Clear();
            }
            provokedInstigatorIds.Add(instigator.thingIDNumber);
            lastProvokedInstigator = instigator;
            provokedUntilTick = nowTick + ProvokedRetaliationDurationTicks;
            partCombat?.InvalidateHostileCandidateScan();

            if (Pawn.mindState != null)
            {
                Thing curEnemy = Pawn.mindState.enemyTarget;
                if (curEnemy == null
                    || curEnemy.Destroyed
                    || (curEnemy is Pawn cp && cp.health != null && (cp.Dead || cp.Downed))
                    || !GiantPartCombatRuntime.IsValidHostileThing(Pawn, curEnemy))
                {
                    Pawn.mindState.enemyTarget = instigator;
                }
            }

            if (Pawn.Spawned && Pawn.jobs != null)
            {
                JobDef curDef = Pawn.CurJobDef;
                if (curDef == null
                    || curDef == JobDefOf.GotoWander
                    || curDef == JobDefOf.Wait_Wander
                    || curDef == JobDefOf.LayDown
                    || curDef == JobDefOf.Ingest)
                {
                    Pawn.jobs.CheckForJobOverride();
                }
            }
        }

        public bool HasActiveProvokedTarget =>
            provokedInstigatorIds.Count > 0
            && (Current.Game?.tickManager != null ? Current.Game.tickManager.TicksGame : latestPublishedGameTick) <= provokedUntilTick;
        public bool IsProvokedAgainst(Thing candidate)
        {
            if (candidate == null || candidate.Destroyed || !HasActiveProvokedTarget)
            {
                return false;
            }
            if (candidate is Pawn p && p.health != null && p.Dead)
            {
                return false;
            }
            return provokedInstigatorIds.Contains(candidate.thingIDNumber);
        }

        public Thing TryGetActiveProvokedTarget()
        {
            if (!HasActiveProvokedTarget)
            {
                provokedInstigatorIds.Clear();
                lastProvokedInstigator = null;
                return null;
            }
            if (lastProvokedInstigator != null
                && !lastProvokedInstigator.Destroyed
                && (!Pawn.Spawned || (lastProvokedInstigator.Spawned && lastProvokedInstigator.Map == Pawn.Map))
                && !(lastProvokedInstigator is Pawn lp && lp.health != null && lp.Dead))
            {
                return lastProvokedInstigator;
            }
            return null;
        }

        public void ForceProvokeTarget(Thing target, int durationTicks = ProvokedRetaliationDurationTicks)
        {
            if (Pawn == null || target == null || target == Pawn || target.Destroyed)
            {
                return;
            }
            int nowTick = Current.Game?.tickManager != null ? Current.Game.tickManager.TicksGame : latestPublishedGameTick;
            provokedInstigatorIds.Add(target.thingIDNumber);
            lastProvokedInstigator = target;
            provokedUntilTick = Math.Max(provokedUntilTick, nowTick + Math.Max(120, durationTicks));
            partCombat?.InvalidateHostileCandidateScan();

            if (Pawn.mindState != null)
            {
                Pawn.mindState.enemyTarget = target;
            }
            if (Pawn.Spawned && Pawn.jobs != null)
            {
                JobDef curDef = Pawn.CurJobDef;
                if (curDef == null
                    || curDef == JobDefOf.GotoWander
                    || curDef == JobDefOf.Wait_Wander
                    || curDef == JobDefOf.LayDown
                    || curDef == JobDefOf.Ingest)
                {
                    Pawn.jobs.CheckForJobOverride();
                }
            }
        }

        public override void Notify_Killed(Map prevMap, DamageInfo? dinfo = null)
        {
            if (prevMap != null)
            {
                lastSpawnedMap = prevMap;
            }
            GiantFastLookup.TryGetCockpit(Pawn)?.EjectAll(violent: true, mapOverride: prevMap);
            flightEntryActive = false;
            base.Notify_Killed(prevMap, dinfo);
            BeginDeathAnimation();
            RefreshMissingPartsAfterDamageOrDeath();
            InvalidateWoundDecalCache();
            if (prevMap != null && Pawn != null)
            {
                RebuildFootprintNow(notifyMap: false);
                prevMap.GetComponent<GiantFootprintMapComponent>()?.RegisterCorpseFootprint(Pawn, this);
                GiantVfxSystem.SpawnDeathCollapseVfx(Pawn, this);
                if (!IsCurrentlyAirborne && GiantWoundBloodSystem.IsOrganicFleshGiant(Pawn) && Pawn.RaceProps?.BloodDef != null)
                {
                    GiantBloodFilthUtility.SpawnBloodSplatterAtCell(Pawn, prevMap, Pawn.PositionHeld, Rand.RangeInclusive(3, 5), scatterRadius: 2.2f);
                }
            }
        }

        public void BeginDeathAnimation()
        {
            if (RigDef == null)
            {
                return;
            }
            EnsureBuffersInitialized();
            rushActive = false;
            isWarmupWindup = false;
            hasAttackTargetWorldXZ = false;
            ManualOverrideActive = false;
            ClearAllTurretRecoils();
            hitReactionRuntime?.Reset();

            CaptureBlendFromPoses(finalLocalPoses);

            GiantAnimDef deathClip = RigDef.deathAnim ?? RigDef.downedAnim;
            currentClip = deathClip;
            activeOneShotAnim = deathClip;
            animState = GiantAnimState.Death;
            oneShotElapsedTicks = 0f;
            blendElapsedTicks = 0f;
            blendDurationTicks = deathClip != null ? Mathf.Max(2f, deathClip.blendInTicks) : 0f;
            deathAnimCompleted = deathClip == null || deathClip.durationTicks <= 0.01f;
            deathTickZeroEventsEvaluated = false;
            postDeathSettleTicks = 0;
            deathStartedGameTick = Current.Game?.tickManager?.TicksGame ?? 0;

            currentSteeringCtx.AnimState = GiantAnimState.Death;
            currentSteeringCtx.IsMoving = false;
            currentSteeringCtx.HasExplicitLookTarget = false;
            currentSteeringCtx.IsCombatTarget = false;
            currentSteeringCtx.MovedDistanceCells = 0f;

            if (deathClip != null)
            {
                EvaluateClipEvents(deathClip, -0.01f, 0.001f, isLoop: false);
                deathTickZeroEventsEvaluated = true;
            }
            EvaluateCurrentPoseAndPublish(0f);
        }

        public void RestoreCorpseStateAfterLoad(Map corpseMap)
        {
            if (corpseMap != null)
            {
                lastSpawnedMap = corpseMap;
            }
            if (RigDef == null)
            {
                return;
            }
            EnsureBuffersInitialized();
            ResolveBodyPartLinks();
            if (UnityData.IsInMainThread)
            {
                WarmTexturesAndMasks();
            }

            GiantAnimDef deathClip = RigDef.deathAnim ?? RigDef.downedAnim;
            currentClip = deathClip;
            activeOneShotAnim = deathClip;
            animState = GiantAnimState.Death;
            blendDurationTicks = 0f;
            blendElapsedTicks = 0f;
            if (deathClip == null)
            {
                deathAnimCompleted = true;
            }
            else if (deathAnimCompleted || oneShotElapsedTicks >= deathClip.durationTicks - 1e-4f)
            {
                oneShotElapsedTicks = deathClip.durationTicks;
                deathAnimCompleted = true;
            }
            deathTickZeroEventsEvaluated = deathAnimCompleted || oneShotElapsedTicks > 0f;

            healthStateDirty = true;
            UpdateMissingPartsState(out _, force: true);
            EvaluateCurrentPoseAndPublish(0f);
            RebuildFootprintNow(notifyMap: false);
        }

        public bool TickCorpseDeathAnimation(float deltaTicks, out bool footprintUpdated)
        {
            footprintUpdated = false;
            if (RigDef == null)
            {
                return false;
            }
            EnsureBuffersInitialized();

            if (animState != GiantAnimState.Death)
            {
                BeginDeathAnimation();
            }

            GiantAnimDef clip = currentClip ?? RigDef.deathAnim ?? RigDef.downedAnim;
            if (clip == null)
            {
                deathAnimCompleted = true;
                return false;
            }
            currentClip = clip;

            bool bezierSettling = false;
            if (bezierRuntimes != null)
            {
                for (int c = 0; c < bezierRuntimes.Length; c++)
                {
                    if (bezierRuntimes[c] != null && bezierRuntimes[c].IsSettling)
                    {
                        bezierSettling = true;
                        break;
                    }
                }
            }
            bool softWhipSettling = false;
            if (softWhipRuntimes != null)
            {
                for (int s = 0; s < softWhipRuntimes.Length; s++)
                {
                    if (softWhipRuntimes[s] != null && softWhipRuntimes[s].IsSettling)
                    {
                        softWhipSettling = true;
                        break;
                    }
                }
            }

            bool flightSettling = flightLocomotion != null && flightLocomotion.StepCorpseDescentTick(Mathf.Max(0f, deltaTicks));
            if (deathAnimCompleted && ((!bezierSettling && !softWhipSettling && !flightSettling) || postDeathSettleTicks >= 45))
            {
                return false;
            }

            float dt = Mathf.Max(0f, deltaTicks);
            bool justFinishedClip = false;
            if (!deathAnimCompleted)
            {
                float prevTick = oneShotElapsedTicks;
                float dur = Mathf.Max(1f, clip.durationTicks);
                oneShotElapsedTicks = Mathf.Min(dur, prevTick + dt);
                float rem = 1f - Mathf.Clamp01(oneShotElapsedTicks / dur);
                continuousSpinTicks += dt * (rem * rem);

                if (oneShotElapsedTicks > prevTick)
                {
                    float eventPrevTick = (prevTick <= 0f && deathTickZeroEventsEvaluated) ? 0.001f : prevTick;
                    deathTickZeroEventsEvaluated = false;
                    if (oneShotElapsedTicks > eventPrevTick)
                    {
                        EvaluateClipEvents(clip, eventPrevTick, oneShotElapsedTicks, isLoop: false);
                    }
                }

                if (oneShotElapsedTicks >= dur - 1e-4f)
                {
                    oneShotElapsedTicks = dur;
                    deathAnimCompleted = true;
                    justFinishedClip = true;
                }
            }
            else
            {
                postDeathSettleTicks++;
            }

            EvaluateCurrentPoseAndPublish(dt);

            if (Pawn != null)
            {
                int now = Current.Game?.tickManager?.TicksGame ?? (lastFootprintTick + 1);
                int interval = Mathf.Max(2, RigDef.footprintUpdateIntervalTicks * 2);
                if (justFinishedClip || now - lastFootprintTick >= interval)
                {
                    RebuildFootprintNow(notifyMap: false);
                    footprintUpdated = true;
                }
            }

            return true;
        }

        internal void RefreshMissingPartsAfterDamageOrDeath()
        {
            if (Pawn == null || RigDef == null)
            {
                return;
            }
            healthStateDirty = true;
            UpdateMissingPartsState(out bool missingOrHealthChanged, force: true);
            if (missingOrHealthChanged || Pawn.Dead)
            {
                EvaluateCurrentPoseAndPublish(0f);
                if (Pawn.Spawned)
                {
                    RebuildFootprintNow(notifyMap: true);
                }
            }
        }

    }
}
