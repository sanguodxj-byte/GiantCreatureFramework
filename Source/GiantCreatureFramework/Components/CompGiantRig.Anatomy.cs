using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Body-part binding, hit routing, missing-part masks, and health tracking.
    public partial class CompGiantRig
    {
        public void ResolveBodyPartLinks()
        {
            if (Pawn?.RaceProps?.body == null || RigDef == null)
            {
                return;
            }
            EnsureBuffersInitialized();
            List<BodyPartRecord> allParts = Pawn.RaceProps.body.AllParts;
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            allLinkedBodyPartSet ??= new HashSet<BodyPartRecord>();
            allLinkedBodyPartSet.Clear();

            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef node = nodes[i];
                if (node.linkedBodyPart == null)
                {
                    linkedBodyParts[i] = null;
                    continue;
                }

                BodyPartRecord matched = null;
                int matchCounter = 0;
                for (int p = 0; p < allParts.Count; p++)
                {
                    BodyPartRecord rec = allParts[p];
                    if (rec.def != node.linkedBodyPart &&
                        !string.Equals(rec.def?.defName, node.linkedBodyPart.defName, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string rawCustomLabel = !string.IsNullOrEmpty(rec.untranslatedCustomLabel)
                        ? rec.untranslatedCustomLabel
                        : rec.customLabel;
                    if (!string.IsNullOrEmpty(node.linkedBodyPartCustomLabel) &&
                        !string.Equals(rawCustomLabel, node.linkedBodyPartCustomLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (node.linkedBodyPartIndex >= 0 && matchCounter != node.linkedBodyPartIndex)
                    {
                        matchCounter++;
                        continue;
                    }
                    matched = rec;
                    break;
                }
                linkedBodyParts[i] = matched;
                if (matched != null)
                {
                    allLinkedBodyPartSet.Add(matched);
                }
            }

            if (RigDef.softWhips != null && linkedSoftWhipTendrilParts != null)
            {
                for (int s = 0; s < RigDef.softWhips.Count && s < linkedSoftWhipTendrilParts.Length; s++)
                {
                    SoftWhipDef whip = RigDef.softWhips[s];
                    if (whip?.tendrilBodyPart == null)
                    {
                        linkedSoftWhipTendrilParts[s] = null;
                        continue;
                    }

                    BodyPartRecord matched = null;
                    for (int p = 0; p < allParts.Count; p++)
                    {
                        BodyPartRecord rec = allParts[p];
                        if (rec.def != whip.tendrilBodyPart)
                        {
                            continue;
                        }
                        if (!string.IsNullOrEmpty(whip.tendrilBodyPartCustomLabel) &&
                            !string.Equals(rec.untranslatedCustomLabel, whip.tendrilBodyPartCustomLabel, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        matched = rec;
                        break;
                    }
                    // Intentionally omitted from allLinkedBodyPartSet so hits on rootBoneId can roll down to this tendril.
                    linkedSoftWhipTendrilParts[s] = matched;
                }
            }

            if (RigDef.skinnedMeshes != null && linkedSkinnedMeshParts != null)
            {
                for (int m = 0; m < RigDef.skinnedMeshes.Count && m < linkedSkinnedMeshParts.Length; m++)
                {
                    GiantSkinnedMeshDef sm = RigDef.skinnedMeshes[m];
                    if (sm?.linkedBodyPart == null)
                    {
                        linkedSkinnedMeshParts[m] = null;
                        continue;
                    }

                    BodyPartRecord matched = null;
                    int matchCounter = 0;
                    for (int p = 0; p < allParts.Count; p++)
                    {
                        BodyPartRecord rec = allParts[p];
                        if (rec.def != sm.linkedBodyPart)
                        {
                            continue;
                        }
                        if (!string.IsNullOrEmpty(sm.linkedBodyPartCustomLabel) &&
                            !string.Equals(rec.untranslatedCustomLabel, sm.linkedBodyPartCustomLabel, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (sm.linkedBodyPartIndex >= 0 && matchCounter != sm.linkedBodyPartIndex)
                        {
                            matchCounter++;
                            continue;
                        }
                        matched = rec;
                        break;
                    }
                    linkedSkinnedMeshParts[m] = matched;
                }
            }
        }

        public BodyPartRecord GetLinkedBodyPartRaw(int boneIndex)
        {
            if (linkedBodyParts != null && (uint)boneIndex < (uint)linkedBodyParts.Length)
            {
                return linkedBodyParts[boneIndex];
            }
            return null;
        }

        public BodyPartRecord GetBodyPartForBone(int boneIndex)
        {
            if (linkedBodyParts != null && (uint)boneIndex < (uint)linkedBodyParts.Length)
            {
                BodyPartRecord rec = linkedBodyParts[boneIndex];
                if (rec != null && Pawn?.health?.hediffSet != null && !Pawn.health.hediffSet.PartIsMissing(rec))
                {
                    return rec;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves a hit on a visual bone down into that bone's own anatomical sub-parts (e.g., claw pincers/servos,
        /// tarsus foot, cephalothorax sensors) without ever crossing into another bone's own linked BodyPartRecord,
        /// and preventing external ranged/melee hits from bypassing intact Outside shells straight into Inside vital organs.
        /// </summary>
        public static bool ShouldBlockChildOrganRoll(
            BodyPartRecord currentPart,
            float currentPartHpPct,
            BodyPartRecord childPart,
            DamageInfo dinfo,
            float breachRoll01 = 1f)
        {
            if (currentPart == null || childPart == null)
            {
                return true;
            }
            if (dinfo.Height != BodyPartHeight.Undefined && childPart.height != dinfo.Height)
            {
                return true;
            }
            if (dinfo.Depth != BodyPartDepth.Undefined)
            {
                return childPart.depth != dinfo.Depth;
            }

            // When dinfo.Depth == Undefined (standard bullets, melee cuts/stabs/blunt, shrapnel),
            // an intact Outside armor shell protects Inside vital organs (ArtificialBrain, Reactor, Heart, Nexus).
            if (currentPart.depth == BodyPartDepth.Outside && childPart.depth == BodyPartDepth.Inside)
            {
                bool canBreachCompromisedShell = currentPartHpPct < 0.30f
                    && dinfo.Def != null
                    && !dinfo.Def.harmAllLayersUntilOutside
                    && dinfo.ArmorPenetrationInt >= 0.85f
                    && breachRoll01 < 0.15f;
                return !canBreachCompromisedShell;
            }

            return false;
        }

        public BodyPartRecord ResolveHitBodyPartForBone(int boneIndex, DamageInfo dinfo)
        {
            BodyPartRecord cur = GetBodyPartForBone(boneIndex);
            if (cur == null && Pawn?.health?.hediffSet != null)
            {
                BodyPartRecord raw = GetLinkedBodyPartRaw(boneIndex);
                while (raw != null && Pawn.health.hediffSet.PartIsMissing(raw))
                {
                    raw = raw.parent;
                }
                cur = raw;
            }
            if (cur == null || Pawn?.health?.hediffSet == null)
            {
                return null;
            }

            HediffSet hediffSet = Pawn.health.hediffSet;
            for (int depthGuard = 0; depthGuard < 8; depthGuard++)
            {
                if (cur.parts == null || cur.parts.Count == 0)
                {
                    break;
                }

                float curMaxHp = Mathf.Max(1f, cur.def.GetMaxHealth(Pawn));
                float curHpPct = Mathf.Clamp01(hediffSet.GetPartHealth(cur) / curMaxHp);
                float breachRoll = Rand.Value;

                float totalChildCoverage = 0f;
                for (int i = 0; i < cur.parts.Count; i++)
                {
                    BodyPartRecord child = cur.parts[i];
                    if (hediffSet.PartIsMissing(child))
                    {
                        continue;
                    }
                    if (allLinkedBodyPartSet != null && allLinkedBodyPartSet.Contains(child))
                    {
                        continue;
                    }
                    if (ShouldBlockChildOrganRoll(cur, curHpPct, child, dinfo, breachRoll))
                    {
                        continue;
                    }
                    totalChildCoverage += child.coverage;
                }

                if (totalChildCoverage <= 0f)
                {
                    break;
                }

                float roll = Rand.Value;
                if (roll >= totalChildCoverage)
                {
                    break;
                }

                BodyPartRecord chosen = null;
                for (int i = 0; i < cur.parts.Count; i++)
                {
                    BodyPartRecord child = cur.parts[i];
                    if (hediffSet.PartIsMissing(child))
                    {
                        continue;
                    }
                    if (allLinkedBodyPartSet != null && allLinkedBodyPartSet.Contains(child))
                    {
                        continue;
                    }
                    if (ShouldBlockChildOrganRoll(cur, curHpPct, child, dinfo, breachRoll))
                    {
                        continue;
                    }
                    roll -= child.coverage;
                    if (roll < 0f)
                    {
                        chosen = child;
                        break;
                    }
                }

                if (chosen == null)
                {
                    break;
                }
                cur = chosen;
            }

            if (cur != null && cur.coverageAbs <= 0f)
            {
                BodyPartRecord hittable = FindFirstHittableSubPart(cur, hediffSet);
                if (hittable != null)
                {
                    return hittable;
                }
                return null;
            }

            return cur;
        }

        private BodyPartRecord FindFirstHittableSubPart(BodyPartRecord root, HediffSet hediffSet)
        {
            if (root == null || hediffSet == null || root.parts == null)
            {
                return null;
            }
            for (int i = 0; i < root.parts.Count; i++)
            {
                BodyPartRecord child = root.parts[i];
                if (child == null || hediffSet.PartIsMissing(child))
                {
                    continue;
                }
                if (allLinkedBodyPartSet != null && allLinkedBodyPartSet.Contains(child))
                {
                    continue;
                }
                if (child.coverageAbs > 0f)
                {
                    return child;
                }
                BodyPartRecord nested = FindFirstHittableSubPart(child, hediffSet);
                if (nested != null)
                {
                    return nested;
                }
            }
            return null;
        }

        public bool IsBoneIntact(string boneId)
        {
            if (RigDef == null || string.IsNullOrEmpty(boneId) ||
                !RigDef.TryGetNodeIndex(boneId, out int idx))
            {
                return true;
            }

            if (Pawn?.health?.hediffSet == null || linkedBodyParts == null)
            {
                return IsBoneVisibleThreadSafe(idx);
            }

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            int curIdx = idx;
            while (curIdx >= 0 && curIdx < nodes.Length)
            {
                BodyPartRecord rec = linkedBodyParts[curIdx];
                if (nodes[curIdx].hideWhenMissing && rec != null && Pawn.health.hediffSet.PartIsMissing(rec))
                {
                    return false;
                }
                curIdx = nodes[curIdx].parentRuntimeIndex;
            }

            if (RigDef.softWhips != null && linkedSoftWhipTendrilParts != null)
            {
                for (int s = 0; s < RigDef.softWhips.Count && s < linkedSoftWhipTendrilParts.Length; s++)
                {
                    SoftWhipDef whip = RigDef.softWhips[s];
                    if (whip != null &&
                        string.Equals(whip.rootBoneId, boneId, StringComparison.Ordinal) &&
                        linkedSoftWhipTendrilParts[s] != null &&
                        Pawn.health.hediffSet.PartIsMissing(linkedSoftWhipTendrilParts[s]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public bool AreRequiredBonesIntact(IReadOnlyList<string> boneIds, bool requireAll)
        {
            if (boneIds == null || boneIds.Count == 0)
            {
                return true;
            }
            if (requireAll)
            {
                for (int i = 0; i < boneIds.Count; i++)
                {
                    if (!IsBoneIntact(boneIds[i]))
                    {
                        return false;
                    }
                }
                return true;
            }
            for (int i = 0; i < boneIds.Count; i++)
            {
                if (IsBoneIntact(boneIds[i]))
                {
                    return true;
                }
            }
            return false;
        }

        public bool IsBoneVisibleThreadSafe(int boneIndex)
        {
            GiantRenderSnapshot snap = latestSnapshot;
            if (snap?.missingMask != null && (uint)boneIndex < (uint)snap.missingMask.Length)
            {
                BoneNodeDef[] nodes = RigDef.SortedNodes;
                if (snap.missingMask[boneIndex] && nodes[boneIndex].hideWhenMissing)
                {
                    return false;
                }
                int pIdx = nodes[boneIndex].parentRuntimeIndex;
                while (pIdx >= 0)
                {
                    if (snap.missingMask[pIdx] && nodes[pIdx].hideWhenMissing)
                    {
                        return false;
                    }
                    pIdx = nodes[pIdx].parentRuntimeIndex;
                }
            }
            return true;
        }

        public float GetBoneHealthPercentThreadSafe(int boneIndex)
        {
            GiantRenderSnapshot snap = latestSnapshot;
            if (snap?.partHealthPct != null && (uint)boneIndex < (uint)snap.partHealthPct.Length)
            {
                return snap.partHealthPct[boneIndex];
            }
            return 1f;
        }

        public int CountSeveredBones()
        {
            if (RigDef == null || partMissingMask == null)
            {
                return 0;
            }
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            HediffSet hediffSet = Pawn?.health?.hediffSet;
            int severed = 0;
            for (int i = 0; i < nodes.Length && i < partMissingMask.Length; i++)
            {
                if (!nodes[i].hideWhenMissing)
                {
                    continue;
                }
                bool isMissing = partMissingMask[i]
                    || (hediffSet != null && linkedBodyParts != null && i < linkedBodyParts.Length && linkedBodyParts[i] != null && hediffSet.PartIsMissing(linkedBodyParts[i]));
                if (isMissing)
                {
                    severed++;
                }
            }
            if (softWhipTendrilMissingMask != null)
            {
                for (int s = 0; s < softWhipTendrilMissingMask.Length; s++)
                {
                    if (softWhipTendrilMissingMask[s])
                    {
                        severed++;
                    }
                }
            }
            return severed;
        }

        public bool HasAnySeveredBone() => CountSeveredBones() > 0;

        public GiantBoneViewSector GetBoneSectorThreadSafe(int boneIndex, bool portrait = false)
        {
            if (portrait)
            {
                return GiantBoneViewSector.Dorsal;
            }
            GiantRenderSnapshot snap = latestSnapshot;
            GiantBoneViewSector[] sectors = snap?.activeSectors;
            if (sectors != null && (uint)boneIndex < (uint)sectors.Length)
            {
                return sectors[boneIndex];
            }
            return GiantBoneViewSector.Dorsal;
        }

        public bool IsSoftWhipVisibleThreadSafe(int whipIndex)
        {
            GiantRenderSnapshot snap = latestSnapshot;
            if (snap?.softWhips != null && (uint)whipIndex < (uint)snap.softWhips.Length)
            {
                SoftWhipRibbonSnapshot whipSnap = snap.softWhips[whipIndex];
                return whipSnap != null && whipSnap.visible;
            }
            return false;
        }

        /// <summary>
        /// Evaluates pure topological bone-tree severing propagation: whenever an ancestor bone with hideWhenMissing=true
        /// is severed, every downstream child/descendant bone in SortedNodes is also marked severed, including across
        /// intermediate hideWhenMissing=false bones whose ancestor is hidden.
        /// </summary>
        public static void PropagateTopologicalBoneMissingMask(BoneNodeDef[] sortedNodes, Span<bool> missingMask)
        {
            if (sortedNodes == null)
            {
                return;
            }
            int len = Mathf.Min(sortedNodes.Length, missingMask.Length);
            Span<bool> hiddenMask = len <= 128 ? stackalloc bool[len] : new bool[len];
            for (int i = 0; i < len; i++)
            {
                int pIdx = sortedNodes[i].parentRuntimeIndex;
                bool parentHidden = pIdx >= 0 && pIdx < i && hiddenMask[pIdx];
                if (parentHidden)
                {
                    missingMask[i] = true;
                }
                hiddenMask[i] = parentHidden || (missingMask[i] && sortedNodes[i].hideWhenMissing);
            }
        }

        private void UpdateMissingPartsState(out bool changed, bool force = false)
        {
            changed = false;
            if (Pawn?.health?.hediffSet == null || RigDef == null || linkedBodyParts == null)
            {
                return;
            }

            HediffSet hediffSet = Pawn.health.hediffSet;
            int curHediffCount = hediffSet.hediffs?.Count ?? 0;
            int nowTick = Current.Game?.tickManager?.TicksGame ?? 0;
            if (!force
                && missingMaskInitialized
                && !healthStateDirty
                && curHediffCount == lastCheckedHediffCount
                && (nowTick - lastHealthCheckTick) < 15)
            {
                return;
            }

            healthStateDirty = false;
            lastCheckedHediffCount = curHediffCount;
            lastHealthCheckTick = nowTick;

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            bool hasAgeTracker = Pawn.ageTracker != null;
            float curScale = hasAgeTracker ? Pawn.HealthScale : (Pawn.RaceProps?.baseHealthScale ?? 1f);
            if (cachedBoneMaxHp == null || cachedBoneMaxHp.Length != nodes.Length || Mathf.Abs(curScale - cachedMaxHpHealthScale) > 1e-4f)
            {
                cachedMaxHpHealthScale = curScale;
                if (cachedBoneMaxHp == null || cachedBoneMaxHp.Length != nodes.Length)
                {
                    cachedBoneMaxHp = new float[nodes.Length];
                }
                for (int i = 0; i < linkedBodyParts.Length && i < nodes.Length; i++)
                {
                    BodyPartRecord rec = linkedBodyParts[i];
                    cachedBoneMaxHp[i] = rec != null
                        ? Mathf.Max(1f, hasAgeTracker ? rec.def.GetMaxHealth(Pawn) : Mathf.CeilToInt(rec.def.hitPoints * curScale))
                        : 1f;
                }
                int wCnt = linkedSoftWhipTendrilParts?.Length ?? 0;
                if (cachedWhipMaxHp == null || cachedWhipMaxHp.Length != wCnt)
                {
                    cachedWhipMaxHp = wCnt > 0 ? new float[wCnt] : Array.Empty<float>();
                }
                for (int s = 0; s < wCnt; s++)
                {
                    BodyPartRecord rec = linkedSoftWhipTendrilParts[s];
                    cachedWhipMaxHp[s] = rec != null
                        ? Mathf.Max(1f, hasAgeTracker ? rec.def.GetMaxHealth(Pawn) : Mathf.CeilToInt(rec.def.hitPoints * curScale))
                        : 1f;
                }
            }

            BodyPartRecord corePart = Pawn.RaceProps?.body?.corePart;
            ulong healthHash = 14695981039346656037UL;
            bool localAnyBelow65 = false;

            bool canSpawnSeverDebris = missingMaskInitialized && Pawn != null && Pawn.Spawned && Pawn.Map != null;
            if (canSpawnSeverDebris)
            {
                if (prevMissingMaskScratch == null || prevMissingMaskScratch.Length < nodes.Length)
                {
                    prevMissingMaskScratch = new bool[Math.Max(64, nodes.Length)];
                }
                Array.Copy(partMissingMask, prevMissingMaskScratch, nodes.Length);
            }
            newlyMissingBonesScratch.Clear();

            Span<bool> hiddenMask = nodes.Length <= 128 ? stackalloc bool[nodes.Length] : new bool[nodes.Length];
            for (int i = 0; i < linkedBodyParts.Length && i < nodes.Length; i++)
            {
                BodyPartRecord rec = linkedBodyParts[i];
                bool wasMissing = partMissingMask[i];
                bool missing = curHediffCount > 0 && rec != null && hediffSet.PartIsMissing(rec);

                int pIdx = nodes[i].parentRuntimeIndex;
                bool parentHidden = pIdx >= 0 && pIdx < i && hiddenMask[pIdx];
                if (!missing && parentHidden)
                {
                    missing = true;
                    if (!suppressCascadeReentry &&
                        nodes[i].hideWhenMissing &&
                        rec != null &&
                        rec != corePart &&
                        !Pawn.Dead &&
                        !hediffSet.PartIsMissing(rec))
                    {
                        suppressCascadeReentry = true;
                        try
                        {
                            Pawn.health.AddHediff(HediffDefOf.MissingBodyPart, rec);
                            curHediffCount = hediffSet.hediffs?.Count ?? curHediffCount;
                            lastCheckedHediffCount = curHediffCount;
                        }
                        finally
                        {
                            suppressCascadeReentry = false;
                        }
                    }
                }

                partMissingMask[i] = missing;
                hiddenMask[i] = parentHidden || (missing && nodes[i].hideWhenMissing);

                float hpPct = 1f;
                if (missing)
                {
                    hpPct = 0f;
                }
                else if (curHediffCount > 0 && rec != null)
                {
                    float maxHp = cachedBoneMaxHp[i];
                    hpPct = Mathf.Clamp01(hediffSet.GetPartHealth(rec) / maxHp);
                }

                if (partHealthPct != null && i < partHealthPct.Length)
                {
                    partHealthPct[i] = hpPct;
                }
                if (hpPct > 0f && hpPct < 0.65f)
                {
                    localAnyBelow65 = true;
                }

                uint bucket = (uint)Mathf.Clamp(Mathf.CeilToInt(hpPct * 20f), 0, 20);
                healthHash = (healthHash ^ bucket) * 1099511628211UL;

                if (missingMaskInitialized && !wasMissing && missing && Pawn != null && Pawn.Spawned)
                {
                    newlyMissingBonesScratch.Add(i);
                    GiantSubmodEvents.NotifyBoneSevered(Pawn, this, nodes[i]);
                }
            }

            if (canSpawnSeverDebris && newlyMissingBonesScratch.Count > 0)
            {
                // Fire stump sever VFX strictly at detachment root joints (avoids cluttering empty air along cascaded chains)
                List<List<int>> detachGroups = GiantSeveredDebrisSystem.PartitionDetachmentRoots(nodes, newlyMissingBonesScratch);
                for (int g = 0; g < detachGroups.Count; g++)
                {
                    if (detachGroups[g].Count > 0)
                    {
                        GiantVfxSystem.SpawnBoneSeverVfx(Pawn, this, detachGroups[g][0]);
                        GiantBloodFilthUtility.TrySpawnSeverBloodFilth(Pawn, this, detachGroups[g][0]);
                    }
                }
                if (!Pawn.Dead && detachGroups.Count > 0 && detachGroups[0].Count > 0)
                {
                    TriggerPartBreakHitReaction(detachGroups[0][0], nowTick);
                }
                InvalidateWoundDecalCache();

                if (preSeverPosesScratch == null || preSeverPosesScratch.Length < nodes.Length)
                {
                    preSeverPosesScratch = new BoneWorldPose[Math.Max(64, nodes.Length)];
                }
                EnsureBuffersInitialized();
                SkeletalKinematics.SolveWorldPoses(
                    RigDef,
                    Vector2.zero,
                    currentHeadingDeg,
                    finalLocalPoses.AsSpan(0, nodes.Length),
                    prevMissingMaskScratch.AsSpan(0, nodes.Length),
                    preSeverPosesScratch.AsSpan(0, nodes.Length),
                    1f,
                    sectorsInitialized && activeBoneSectors != null
                        ? activeBoneSectors.AsSpan(0, nodes.Length)
                        : ReadOnlySpan<GiantBoneViewSector>.Empty,
                    -1,
                    anyTurretRecoilActive && currentTurretRecoilOffsets != null
                        ? currentTurretRecoilOffsets.AsSpan(0, nodes.Length)
                        : ReadOnlySpan<Vector2>.Empty);
                Vector3 drawPos = GetSafeDrawPos(Pawn);
                GiantSeveredDebrisSystem.SpawnForSeveredBones(
                    Pawn,
                    this,
                    newlyMissingBonesScratch,
                    preSeverPosesScratch.AsSpan(0, nodes.Length),
                    new Vector2(drawPos.x, drawPos.z));
            }

            uint whipBits = 0U;
            if (linkedSoftWhipTendrilParts != null && softWhipTendrilMissingMask != null && RigDef.softWhips != null)
            {
                for (int s = 0; s < linkedSoftWhipTendrilParts.Length && s < softWhipTendrilMissingMask.Length && s < RigDef.softWhips.Count; s++)
                {
                    SoftWhipDef whip = RigDef.softWhips[s];
                    BodyPartRecord rec = linkedSoftWhipTendrilParts[s];
                    bool wasWhipMissing = softWhipTendrilMissingMask[s];
                    bool missing = curHediffCount > 0 && rec != null && hediffSet.PartIsMissing(rec);
                    int rIdx = whip?.resolvedRootBoneIndex ?? -1;
                    if (!missing && rIdx >= 0 && rIdx < hiddenMask.Length && hiddenMask[rIdx])
                    {
                        missing = true;
                        if (!suppressCascadeReentry && rec != null && rec != corePart && !Pawn.Dead && !hediffSet.PartIsMissing(rec))
                        {
                            suppressCascadeReentry = true;
                            try
                            {
                                Pawn.health.AddHediff(HediffDefOf.MissingBodyPart, rec);
                                curHediffCount = hediffSet.hediffs?.Count ?? curHediffCount;
                                lastCheckedHediffCount = curHediffCount;
                            }
                            finally
                            {
                                suppressCascadeReentry = false;
                            }
                        }
                    }

                    softWhipTendrilMissingMask[s] = missing;
                    if (missingMaskInitialized && !wasWhipMissing && missing && Pawn != null && Pawn.Spawned && Pawn.Map != null)
                    {
                        Vector3 whipSeverPos = GetSafeDrawPos(Pawn);
                        if (softWhipRuntimes != null && s < softWhipRuntimes.Length && softWhipRuntimes[s] != null && softWhipRuntimes[s].IsInitialized)
                        {
                            ReadOnlySpan<Vector2> pts = softWhipRuntimes[s].CenterlineWorldXZ;
                            if (pts.Length > 2)
                            {
                                Vector2 midXZ = pts[pts.Length / 3];
                                whipSeverPos = new Vector3(midXZ.x, whipSeverPos.y, midXZ.y);
                            }
                        }
                        GiantVfxSystem.SpawnSeverBurstAt(Pawn, Pawn.Map, whipSeverPos, 1.15f, GiantSeverVfxKind.VoidRupture);
                        if (softWhipSnapshots != null && s < softWhipSnapshots.Length && softWhipSnapshots[s] != null)
                        {
                            Vector3 whipDrawPos = GetSafeDrawPos(Pawn);
                            GiantSeveredDebrisSystem.SpawnForSeveredSoftWhip(
                                Pawn,
                                this,
                                whip,
                                softWhipSnapshots[s],
                                new Vector2(whipDrawPos.x, whipDrawPos.z));
                        }
                    }
                    if (softWhipHealthPct != null && s < softWhipHealthPct.Length)
                    {
                        if (missing)
                        {
                            softWhipHealthPct[s] = 0f;
                        }
                        else if (curHediffCount > 0 && rec != null)
                        {
                            float wMax = s < cachedWhipMaxHp.Length ? cachedWhipMaxHp[s] : Mathf.Max(1f, rec.def.GetMaxHealth(Pawn));
                            softWhipHealthPct[s] = Mathf.Clamp01(hediffSet.GetPartHealth(rec) / wMax);
                        }
                        else
                        {
                            softWhipHealthPct[s] = 1f;
                        }
                    }
                    if (missing && s < 32)
                    {
                        whipBits |= 1U << s;
                    }
                }
            }

            uint skinBits = 0U;
            if (linkedSkinnedMeshParts != null && skinnedMeshMissingMask != null && RigDef.skinnedMeshes != null)
            {
                for (int m = 0; m < linkedSkinnedMeshParts.Length && m < skinnedMeshMissingMask.Length && m < RigDef.skinnedMeshes.Count; m++)
                {
                    GiantSkinnedMeshDef sm = RigDef.skinnedMeshes[m];
                    BodyPartRecord rec = linkedSkinnedMeshParts[m];
                    bool missing = sm != null && sm.hideWhenMissing && curHediffCount > 0 && rec != null && hediffSet.PartIsMissing(rec);
                    skinnedMeshMissingMask[m] = missing;
                    if (missing && m < 32)
                    {
                        skinBits |= 1U << m;
                    }
                }
            }

            anyDamagedBoneBelow65 = localAnyBelow65;
            bool boneMaskChanged = lastMissingMaskSnapshot == null || lastMissingMaskSnapshot.Length != nodes.Length;
            if (!boneMaskChanged)
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (partMissingMask[i] != lastMissingMaskSnapshot[i])
                    {
                        boneMaskChanged = true;
                        break;
                    }
                }
            }
            bool maskChanged = boneMaskChanged || whipBits != lastSoftWhipMissingBits || skinBits != lastSkinnedMeshMissingBits;
            if (maskChanged)
            {
                localPoseSerial++;
            }
            bool healthBucketChanged = healthHash != lastHealthBucketHash;
            if (maskChanged || healthBucketChanged)
            {
                if (lastMissingMaskSnapshot == null || lastMissingMaskSnapshot.Length != nodes.Length)
                {
                    lastMissingMaskSnapshot = new bool[nodes.Length];
                }
                Array.Copy(partMissingMask, lastMissingMaskSnapshot, nodes.Length);
                lastSoftWhipMissingBits = whipBits;
                lastSkinnedMeshMissingBits = skinBits;
                lastHealthBucketHash = healthHash;
                changed = maskChanged;
                if (createdRenderNodes != null)
                {
                    for (int i = 0; i < createdRenderNodes.Count; i++)
                    {
                        createdRenderNodes[i].requestRecache = true;
                    }
                }
                if (maskChanged && missingMaskInitialized && Pawn != null)
                {
                    GiantFastLookup.TryGetCockpit(Pawn)?.CheckMissingSeatPartsAndEject(FootprintMap);
                }
                vehicleLocomotion?.EvaluateDriveMobility(partMissingMask, partHealthPct);
                flightLocomotion?.EvaluateLiftEfficiency(partMissingMask, partHealthPct, this);
            }
        }

    }
}
