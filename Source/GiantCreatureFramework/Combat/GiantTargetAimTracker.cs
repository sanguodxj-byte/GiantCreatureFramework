using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Tracks per-attacker preferred HitCell/bone aim targets on Giant Creatures and maintains
    /// thread-static hover/resolution contexts across targeting, cover, and projectile pipelines.
    /// </summary>
    public static class GiantTargetAimTracker
    {
        public struct AimPreference
        {
            public int AttackerId;
            public int GiantId;
            public int BoneIndex;
            public Vector2 BoneUV;
            public bool HasBoneUV;
            public Vector2 LocalOffsetXZ;
            public IntVec3 LastClickedCell;
            public int RecordedTick;
        }

        private const int MaxCachedPreferences = 512;
        private static readonly Dictionary<long, AimPreference> preferences = new Dictionary<long, AimPreference>(64);
        private static readonly List<long> cleanupKeys = new List<long>(32);
        private static int lastSweepTick = -9999;

        [ThreadStatic]
        private static int hoverDepth;

        [ThreadStatic]
        private static IntVec3 hoverCell;

        [ThreadStatic]
        private static List<IntVec3> hoverCellStack;

        [ThreadStatic]
        private static int clickDepth;

        [ThreadStatic]
        private static IntVec3 activeClickCell;

        [ThreadStatic]
        private static Vector3 activeClickWorldPos;

        [ThreadStatic]
        private static List<(IntVec3 Cell, Vector3 WorldPos)> clickStack;

        [ThreadStatic]
        private static int fleckOverrideDepth;

        [ThreadStatic]
        private static Vector3 fleckOverridePos;

        [ThreadStatic]
        private static List<Vector3> fleckOverrideStack;

        [ThreadStatic]
        private static Thing activeShooter;

        [ThreadStatic]
        private static Verb activeVerb;

        [ThreadStatic]
        private static bool activeIgnoreRange;

        [ThreadStatic]
        private static float[] sortKeysBuffer;

        [ThreadStatic]
        private static IntVec3[] sortCellsBuffer;

        public static void PushHoverQuery(IntVec3 cell)
        {
            if (hoverDepth > 0)
            {
                hoverCellStack ??= new List<IntVec3>(4);
                hoverCellStack.Add(hoverCell);
            }
            hoverDepth++;
            hoverCell = cell;
        }

        public static void PopHoverQuery()
        {
            if (hoverDepth <= 0)
            {
                hoverDepth = 0;
                hoverCell = IntVec3.Invalid;
                hoverCellStack?.Clear();
                return;
            }
            hoverDepth--;
            if (hoverDepth > 0 && hoverCellStack != null && hoverCellStack.Count > 0)
            {
                int last = hoverCellStack.Count - 1;
                hoverCell = hoverCellStack[last];
                hoverCellStack.RemoveAt(last);
            }
            else
            {
                hoverDepth = 0;
                hoverCell = IntVec3.Invalid;
                hoverCellStack?.Clear();
            }
        }

        public static void PushClickCapture(IntVec3 cell, Vector3 worldPos)
        {
            if (clickDepth > 0)
            {
                clickStack ??= new List<(IntVec3 Cell, Vector3 WorldPos)>(4);
                clickStack.Add((activeClickCell, activeClickWorldPos));
            }
            clickDepth++;
            activeClickCell = cell;
            activeClickWorldPos = worldPos;
        }

        public static void PopClickCapture()
        {
            if (clickDepth <= 0)
            {
                clickDepth = 0;
                activeClickCell = IntVec3.Invalid;
                activeClickWorldPos = Vector3.zero;
                clickStack?.Clear();
                return;
            }
            clickDepth--;
            if (clickDepth > 0 && clickStack != null && clickStack.Count > 0)
            {
                int last = clickStack.Count - 1;
                (IntVec3 prevCell, Vector3 prevWorld) = clickStack[last];
                clickStack.RemoveAt(last);
                activeClickCell = prevCell;
                activeClickWorldPos = prevWorld;
            }
            else
            {
                clickDepth = 0;
                activeClickCell = IntVec3.Invalid;
                activeClickWorldPos = Vector3.zero;
                clickStack?.Clear();
            }
        }

        public static bool TryGetActiveClick(out IntVec3 cell, out Vector3 worldPos)
        {
            if (clickDepth > 0 && activeClickCell.IsValid)
            {
                cell = activeClickCell;
                worldPos = activeClickWorldPos;
                return true;
            }
            cell = IntVec3.Invalid;
            worldPos = Vector3.zero;
            return false;
        }

        public static void PushOverrideFleckPos(Vector3 pos)
        {
            if (fleckOverrideDepth > 0)
            {
                fleckOverrideStack ??= new List<Vector3>(4);
                fleckOverrideStack.Add(fleckOverridePos);
            }
            fleckOverrideDepth++;
            fleckOverridePos = pos;
        }

        public static void PopOverrideFleckPos()
        {
            if (fleckOverrideDepth <= 0)
            {
                fleckOverrideDepth = 0;
                fleckOverridePos = Vector3.zero;
                fleckOverrideStack?.Clear();
                return;
            }
            fleckOverrideDepth--;
            if (fleckOverrideDepth > 0 && fleckOverrideStack != null && fleckOverrideStack.Count > 0)
            {
                int last = fleckOverrideStack.Count - 1;
                fleckOverridePos = fleckOverrideStack[last];
                fleckOverrideStack.RemoveAt(last);
            }
            else
            {
                fleckOverrideDepth = 0;
                fleckOverridePos = Vector3.zero;
                fleckOverrideStack?.Clear();
            }
        }

        public static bool TryGetOverrideFleckPos(out Vector3 pos)
        {
            if (fleckOverrideDepth > 0)
            {
                pos = fleckOverridePos;
                return true;
            }
            pos = Vector3.zero;
            return false;
        }

        public static void PushVerbResolution(Thing shooter, Verb verb, bool ignoreRange)
        {
            activeShooter = shooter;
            activeVerb = verb;
            activeIgnoreRange = ignoreRange;
        }

        public static void PopVerbResolution(Thing prevShooter, Verb prevVerb, bool prevIgnoreRange)
        {
            activeShooter = prevShooter;
            activeVerb = prevVerb;
            activeIgnoreRange = prevIgnoreRange;
        }

        public static bool IsHoverQueryActive => hoverDepth > 0 && hoverCell.IsValid;
        public static IntVec3 CurrentHoverCell => hoverCell;
        public static Thing CurrentResolutionShooter => activeShooter;
        public static Verb CurrentResolutionVerb => activeVerb;
        public static bool CurrentIgnoreRange => activeIgnoreRange;

        private static long MakeKey(int attackerId, int giantId)
        {
            return ((long)attackerId << 32) | (uint)giantId;
        }

        public static void RecordHitCellClick(Thing attacker, Pawn giant, IntVec3 clickedCell, Vector3? clickWorldPos = null)
        {
            if (attacker == null || giant == null || !clickedCell.IsValid)
            {
                return;
            }
            if (!(giant.TryGetComp<CompGiantRig>() is CompGiantRig comp) || comp.Footprint.hitCells.Count == 0)
            {
                return;
            }
            if (!comp.Footprint.hitEntryByCell.TryGetValue(clickedCell, out HitCellEntry entry))
            {
                return;
            }

            int boneIdx = entry.dominantBoneIndex;
            Vector3 worldSample = clickWorldPos ?? clickedCell.ToVector3Shifted();
            if (comp.Footprint.TryHitTestPoint(comp.RigDef, comp.AlphaMasksSpan, worldSample, out int exactBone))
            {
                boneIdx = exactBone;
            }

            bool hasUV = false;
            Vector2 uv = Vector2.zero;
            BoneWorldPose[] poses = comp.Footprint.bonePoses;
            if ((uint)boneIdx < (uint)poses.Length && poses[boneIdx].visible)
            {
                hasUV = poses[boneIdx].TryWorldToUV(worldSample.x, worldSample.z, out uv.x, out uv.y);
            }

            Vector3 giantPos = CompGiantRig.GetSafeDrawPos(giant);
            Vector2 deltaXZ = new Vector2(worldSample.x - giantPos.x, worldSample.z - giantPos.z);
            Vector2 localOffsetXZ = SkeletalKinematics.RotateCW(deltaXZ, -comp.CurrentHeadingDeg);

            int now = Find.TickManager?.TicksGame ?? 0;
            SweepExpiredIfNeeded(now);

            long key = MakeKey(attacker.thingIDNumber, giant.thingIDNumber);
            preferences[key] = new AimPreference
            {
                AttackerId = attacker.thingIDNumber,
                GiantId = giant.thingIDNumber,
                BoneIndex = boneIdx,
                BoneUV = uv,
                HasBoneUV = hasUV,
                LocalOffsetXZ = localOffsetXZ,
                LastClickedCell = clickedCell,
                RecordedTick = now
            };
        }

        public static void ClearForAttacker(Thing attacker)
        {
            if (attacker == null || preferences.Count == 0)
            {
                return;
            }
            int id = attacker.thingIDNumber;
            cleanupKeys.Clear();
            foreach (var kv in preferences)
            {
                if (kv.Value.AttackerId == id)
                {
                    cleanupKeys.Add(kv.Key);
                }
            }
            for (int i = 0; i < cleanupKeys.Count; i++)
            {
                preferences.Remove(cleanupKeys[i]);
            }
            cleanupKeys.Clear();
        }

        public static void ClearForGiant(Pawn giant)
        {
            if (giant == null || preferences.Count == 0)
            {
                return;
            }
            int id = giant.thingIDNumber;
            cleanupKeys.Clear();
            foreach (var kv in preferences)
            {
                if (kv.Value.GiantId == id)
                {
                    cleanupKeys.Add(kv.Key);
                }
            }
            for (int i = 0; i < cleanupKeys.Count; i++)
            {
                preferences.Remove(cleanupKeys[i]);
            }
            cleanupKeys.Clear();
        }

        public static void ClearAll()
        {
            preferences.Clear();
            cleanupKeys.Clear();
            lastSweepTick = -999999;
            hoverDepth = 0;
            hoverCell = IntVec3.Invalid;
            hoverCellStack?.Clear();
            clickDepth = 0;
            activeClickCell = IntVec3.Invalid;
            activeClickWorldPos = Vector3.zero;
            clickStack?.Clear();
            fleckOverrideDepth = 0;
            fleckOverridePos = Vector3.zero;
            fleckOverrideStack?.Clear();
            activeShooter = null;
            activeVerb = null;
            activeIgnoreRange = false;
        }

        private static void SweepExpiredIfNeeded(int now)
        {
            // Handle save-load to an earlier game tick cleanly so expiry never stalls.
            if (now < lastSweepTick)
            {
                lastSweepTick = now;
            }
            if (preferences.Count <= MaxCachedPreferences && now - lastSweepTick < 2500)
            {
                return;
            }
            lastSweepTick = now;
            cleanupKeys.Clear();
            foreach (var kv in preferences)
            {
                if (now < kv.Value.RecordedTick || now - kv.Value.RecordedTick > 30000)
                {
                    cleanupKeys.Add(kv.Key);
                }
            }
            for (int i = 0; i < cleanupKeys.Count; i++)
            {
                preferences.Remove(cleanupKeys[i]);
            }
            cleanupKeys.Clear();
        }

        public static bool TryGetPreference(Thing attacker, Pawn giant, out AimPreference pref)
        {
            if (attacker != null && giant != null &&
                preferences.TryGetValue(MakeKey(attacker.thingIDNumber, giant.thingIDNumber), out pref))
            {
                return true;
            }
            pref = default;
            return false;
        }

        /// <summary>
        /// Resolves the best target cell on a Giant Creature for a shooter, honoring active hover preview first,
        /// then the shooter's previously clicked HitCell/bone preference, and falling back to the closest HitCell.
        /// </summary>
        public static IntVec3 ResolvePreferredHitCell(
            Pawn giant,
            CompGiantRig comp,
            IntVec3 shooterPos,
            Thing shooter = null,
            Verb verb = null,
            bool ignoreRange = false)
        {
            if (giant == null || comp == null)
            {
                return giant?.Position ?? IntVec3.Invalid;
            }

            List<HitCellEntry> hitCells = comp.Footprint.hitCells;
            if (hitCells == null || hitCells.Count == 0)
            {
                return giant.Position;
            }

            if (IsHoverQueryActive && comp.Footprint.hitCellSet.Contains(hoverCell) &&
                IsCellInVerbRange(hoverCell, shooterPos, giant, verb, ignoreRange))
            {
                return hoverCell;
            }

            Thing resolvedShooter = shooter ?? activeShooter ?? verb?.caster;
            if (resolvedShooter != null && TryGetPreference(resolvedShooter, giant, out AimPreference pref))
            {
                if (TryFindTrackedCell(giant, comp, in pref, shooterPos, verb, ignoreRange, out IntVec3 tracked))
                {
                    return tracked;
                }
            }

            if (TryFindBioferriteCorePriorityCell(giant, comp, hitCells, shooterPos, verb, ignoreRange, out IntVec3 coreCell))
            {
                return coreCell;
            }

            return FindClosestHitCell(hitCells, giant.Position, shooterPos, giant, verb, ignoreRange, comp.Footprint.collisionCellSet);
        }

        private static bool IsIntactBioferriteCoreBone(CompGiantRig comp, CompGiantBioferriteCore bioCore, int boneIdx)
        {
            if (comp?.RigDef?.SortedNodes == null || bioCore?.Props == null || (uint)boneIdx >= (uint)comp.RigDef.SortedNodes.Length)
            {
                return false;
            }
            string nodeId = comp.RigDef.SortedNodes[boneIdx].nodeId;
            if (string.IsNullOrEmpty(nodeId) || !comp.IsBoneIntact(nodeId))
            {
                return false;
            }
            if (bioCore.Props.requiredBoneIds != null && bioCore.Props.requiredBoneIds.Count > 0)
            {
                for (int i = 0; i < bioCore.Props.requiredBoneIds.Count; i++)
                {
                    if (string.Equals(bioCore.Props.requiredBoneIds[i], nodeId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }
            return !string.IsNullOrEmpty(bioCore.Props.requiredBoneId)
                && string.Equals(bioCore.Props.requiredBoneId, nodeId, StringComparison.Ordinal);
        }

        private static bool TryFindBioferriteCorePriorityCell(
            Pawn giant,
            CompGiantRig comp,
            List<HitCellEntry> hitCells,
            IntVec3 shooterPos,
            Verb verb,
            bool ignoreRange,
            out IntVec3 bestCell)
        {
            bestCell = IntVec3.Invalid;
            if (!GiantFastLookup.HasBioferriteCore(giant))
            {
                return false;
            }
            float summaryPct = giant.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
            if (summaryPct > 0.45f)
            {
                return false;
            }
            CompGiantBioferriteCore bioCore = giant.TryGetComp<CompGiantBioferriteCore>();
            if (bioCore == null || !bioCore.IsCoreIntact())
            {
                return false;
            }

            bool checkRange = TryComputeVerbRangeSq(verb, giant, ignoreRange, out float minRangeSq, out float maxRangeSq);
            int bestDirectDistSq = int.MaxValue;
            for (int i = 0; i < hitCells.Count; i++)
            {
                HitCellEntry entry = hitCells[i];
                if (!IsIntactBioferriteCoreBone(comp, bioCore, entry.dominantBoneIndex))
                {
                    continue;
                }
                if (checkRange && !IsCellInPrecomputedRangeSq(entry.cell, shooterPos, minRangeSq, maxRangeSq))
                {
                    continue;
                }
                int dSq = (entry.cell - shooterPos).LengthHorizontalSquared;
                if (dSq < bestDirectDistSq)
                {
                    bestDirectDistSq = dSq;
                    bestCell = entry.cell;
                }
            }
            if (bestCell.IsValid)
            {
                return true;
            }

            // Fallback: pick hit cell closest to any intact required core bone pose center
            BoneWorldPose[] poses = comp.Footprint.bonePoses;
            BoneNodeDef[] nodes = comp.RigDef?.SortedNodes;
            if (poses == null || nodes == null)
            {
                return false;
            }
            float bestAnchorScore = float.MaxValue;
            for (int b = 0; b < nodes.Length && b < poses.Length; b++)
            {
                if (!poses[b].visible || !IsIntactBioferriteCoreBone(comp, bioCore, b))
                {
                    continue;
                }
                Vector2 centerXZ = poses[b].quadCenterWorldXZ;
                for (int i = 0; i < hitCells.Count; i++)
                {
                    IntVec3 c = hitCells[i].cell;
                    if (checkRange && !IsCellInPrecomputedRangeSq(c, shooterPos, minRangeSq, maxRangeSq))
                    {
                        continue;
                    }
                    float dx = (c.x + 0.5f) - centerXZ.x;
                    float dz = (c.z + 0.5f) - centerXZ.y;
                    float dSq = dx * dx + dz * dz;
                    if (dSq < bestAnchorScore)
                    {
                        bestAnchorScore = dSq;
                        bestCell = c;
                    }
                }
            }
            return bestCell.IsValid;
        }

        public static bool TryPickIntactBioferriteCoreMeleeBone(
            Pawn giant,
            CompGiantRig comp,
            Vector3 strikeOriginWorld,
            float maxReachWorld,
            out int boneIndex,
            out Vector3 boneWorldPos)
        {
            boneIndex = -1;
            boneWorldPos = Vector3.zero;
            if (giant == null || comp?.RigDef?.SortedNodes == null || !GiantFastLookup.HasBioferriteCore(giant))
            {
                return false;
            }
            float summaryPct = giant.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
            if (summaryPct > 0.45f)
            {
                return false;
            }
            CompGiantBioferriteCore bioCore = giant.TryGetComp<CompGiantBioferriteCore>();
            if (bioCore == null || !bioCore.IsCoreIntact())
            {
                return false;
            }
            BoneWorldPose[] poses = comp.Footprint.bonePoses;
            BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
            float maxSq = maxReachWorld * maxReachWorld;
            float bestSq = float.MaxValue;
            for (int b = 0; b < nodes.Length && b < poses.Length; b++)
            {
                if (!poses[b].visible || !IsIntactBioferriteCoreBone(comp, bioCore, b))
                {
                    continue;
                }
                Vector2 cXZ = poses[b].quadCenterWorldXZ;
                float dx = cXZ.x - strikeOriginWorld.x;
                float dz = cXZ.y - strikeOriginWorld.z;
                float dSq = dx * dx + dz * dz;
                if (dSq <= maxSq && dSq < bestSq)
                {
                    bestSq = dSq;
                    boneIndex = b;
                    boneWorldPos = new Vector3(cXZ.x, strikeOriginWorld.y, cXZ.y);
                }
            }
            return boneIndex >= 0;
        }

        public static bool TryResolveAnchorWorldPos(
            Pawn giant,
            CompGiantRig comp,
            in AimPreference pref,
            out Vector2 anchorWorldXZ)
        {
            BoneWorldPose[] poses = comp.Footprint.bonePoses;
            if ((uint)pref.BoneIndex < (uint)poses.Length && poses[pref.BoneIndex].visible)
            {
                if (pref.HasBoneUV)
                {
                    anchorWorldXZ = poses[pref.BoneIndex].UVToWorldXZ(pref.BoneUV.x, pref.BoneUV.y);
                    return true;
                }
                anchorWorldXZ = poses[pref.BoneIndex].quadCenterWorldXZ;
                return true;
            }

            Vector3 giantPos = CompGiantRig.GetSafeDrawPos(giant);
            Vector2 rotated = SkeletalKinematics.RotateCW(pref.LocalOffsetXZ, comp.CurrentHeadingDeg);
            anchorWorldXZ = new Vector2(giantPos.x + rotated.x, giantPos.z + rotated.y);
            return false;
        }

        private static bool TryFindTrackedCell(
            Pawn giant,
            CompGiantRig comp,
            in AimPreference pref,
            IntVec3 shooterPos,
            Verb verb,
            bool ignoreRange,
            out IntVec3 bestCell)
        {
            List<HitCellEntry> hitCells = comp.Footprint.hitCells;
            bool boneAlive = TryResolveAnchorWorldPos(giant, comp, in pref, out Vector2 anchorXZ);

            bestCell = IntVec3.Invalid;
            float bestScore = float.MaxValue;

            bool checkRange = TryComputeVerbRangeSq(verb, giant, ignoreRange, out float minRangeSq, out float maxRangeSq);

            for (int i = 0; i < hitCells.Count; i++)
            {
                HitCellEntry entry = hitCells[i];
                if (checkRange && !IsCellInPrecomputedRangeSq(entry.cell, shooterPos, minRangeSq, maxRangeSq))
                {
                    continue;
                }

                float dx = (entry.cell.x + 0.5f) - anchorXZ.x;
                float dz = (entry.cell.z + 0.5f) - anchorXZ.y;
                float distToAnchorSq = dx * dx + dz * dz;

                // Strongly prefer cells belonging to the clicked bone while that bone remains visible.
                float bonePenalty = (boneAlive && entry.dominantBoneIndex != pref.BoneIndex) ? 10000f : 0f;
                float score = bonePenalty + distToAnchorSq;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestCell = entry.cell;
                }
            }

            return bestCell.IsValid;
        }

        public static IntVec3 FindClosestHitCell(
            List<HitCellEntry> hitCells,
            IntVec3 fallback,
            IntVec3 fromCell,
            Pawn giant = null,
            Verb verb = null,
            bool ignoreRange = true,
            HashSet<IntVec3> preferredSolidCells = null)
        {
            if (hitCells == null || hitCells.Count == 0)
            {
                return fallback;
            }

            bool checkRange = TryComputeVerbRangeSq(verb, giant, ignoreRange, out float minRangeSq, out float maxRangeSq);
            bool hasSolidSet = preferredSolidCells != null && preferredSolidCells.Count > 0;
            IntVec3 bestSolidInRange = IntVec3.Invalid;
            int bestSolidInRangeDistSq = int.MaxValue;
            IntVec3 bestInRange = IntVec3.Invalid;
            int bestInRangeDistSq = int.MaxValue;
            IntVec3 bestOverall = fallback;
            int bestOverallDistSq = int.MaxValue;

            for (int i = 0; i < hitCells.Count; i++)
            {
                IntVec3 c = hitCells[i].cell;
                int dSq = (c - fromCell).LengthHorizontalSquared;
                if (dSq < bestOverallDistSq)
                {
                    bestOverallDistSq = dSq;
                    bestOverall = c;
                }
                if (checkRange && !IsCellInPrecomputedRangeSq(c, fromCell, minRangeSq, maxRangeSq))
                {
                    continue;
                }
                if (dSq < bestInRangeDistSq)
                {
                    bestInRangeDistSq = dSq;
                    bestInRange = c;
                }
                if (hasSolidSet && preferredSolidCells.Contains(c) && dSq < bestSolidInRangeDistSq)
                {
                    bestSolidInRangeDistSq = dSq;
                    bestSolidInRange = c;
                }
            }

            if (bestSolidInRange.IsValid)
            {
                return bestSolidInRange;
            }
            return bestInRange.IsValid ? bestInRange : bestOverall;
        }

        public static bool TryComputeVerbRangeSq(
            Verb verb,
            Pawn giant,
            bool ignoreRange,
            out float minRangeSq,
            out float maxRangeSq)
        {
            if (verb == null || ignoreRange)
            {
                minRangeSq = 0f;
                maxRangeSq = float.MaxValue;
                return false;
            }

            float maxR = verb.EffectiveRange;
            maxRangeSq = maxR * maxR;
            float minR = verb.verbProps != null
                ? verb.verbProps.EffectiveMinRange(new LocalTargetInfo(giant), verb.caster)
                : 0f;
            minRangeSq = minR > 0f ? minR * minR : 0f;
            return true;
        }

        private static bool IsCellInPrecomputedRangeSq(IntVec3 cell, IntVec3 shooterPos, float minRangeSq, float maxRangeSq)
        {
            int dx = cell.x - shooterPos.x;
            int dz = cell.z - shooterPos.z;
            float distSq = dx * dx + dz * dz;
            return distSq <= maxRangeSq && distSq >= minRangeSq;
        }

        public static bool IsCellInVerbRange(
            IntVec3 cell,
            IntVec3 shooterPos,
            Pawn giant,
            Verb verb,
            bool ignoreRange)
        {
            if (!TryComputeVerbRangeSq(verb, giant, ignoreRange, out float minRangeSq, out float maxRangeSq))
            {
                return true;
            }
            return IsCellInPrecomputedRangeSq(cell, shooterPos, minRangeSq, maxRangeSq);
        }

        public static bool AnyHitCellInVerbRange(CompGiantRig comp, IntVec3 shooterPos, Pawn giant, Verb verb)
        {
            List<HitCellEntry> hitCells = comp?.Footprint?.hitCells;
            if (hitCells == null || hitCells.Count == 0)
            {
                return false;
            }
            if (!TryComputeVerbRangeSq(verb, giant, ignoreRange: false, out float minRangeSq, out float maxRangeSq))
            {
                return true;
            }
            for (int i = 0; i < hitCells.Count; i++)
            {
                if (IsCellInPrecomputedRangeSq(hitCells[i].cell, shooterPos, minRangeSq, maxRangeSq))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Sorts shootable target cells so the preferred HitCell (hovered or clicked limb) comes first,
        /// followed by cells on the same bone, then remaining HitCells ordered by distance to shooterPos.
        /// Hoists weapon EffectiveRange stat evaluation once per call and sorts with reusable key/cell buffers.
        /// </summary>
        public static void AppendAndOrderShootableHitCells(
            List<IntVec3> outCells,
            Pawn giant,
            CompGiantRig comp,
            IntVec3 shooterPos,
            Thing shooter = null,
            Verb verb = null,
            bool ignoreRange = false)
        {
            GiantFootprintData fp = comp.Footprint;
            List<HitCellEntry> hitCells = fp.hitCells;
            if (hitCells == null || hitCells.Count == 0)
            {
                return;
            }

            // Retain only pre-existing cells not already in hitCellSet (typically 0 after filtering Pawn.Position), then append unique hitCells in O(N).
            HashSet<IntVec3> hitSet = fp.hitCellSet;
            int keptExtra = 0;
            for (int i = 0; i < outCells.Count; i++)
            {
                IntVec3 existing = outCells[i];
                if (!hitSet.Contains(existing))
                {
                    outCells[keptExtra++] = existing;
                }
            }
            if (keptExtra < outCells.Count)
            {
                outCells.RemoveRange(keptExtra, outCells.Count - keptExtra);
            }

            int hitCount = hitCells.Count;
            int totalCount = keptExtra + hitCount;
            if (outCells.Capacity < totalCount)
            {
                outCells.Capacity = totalCount;
            }

            bool checkRange = TryComputeVerbRangeSq(verb, giant, ignoreRange, out float minRangeSq, out float maxRangeSq);
            IntVec3 curHoverCell = hoverCell;
            bool hasHover = IsHoverQueryActive && hitSet.Contains(curHoverCell);
            int hoverBoneIdx = -1;
            if (hasHover && fp.hitEntryByCell.TryGetValue(curHoverCell, out HitCellEntry hoverEntry))
            {
                hoverBoneIdx = hoverEntry.dominantBoneIndex;
            }

            Thing resolvedShooter = shooter ?? activeShooter ?? verb?.caster;
            AimPreference pref = default;
            bool hasPref = !hasHover && resolvedShooter != null && TryGetPreference(resolvedShooter, giant, out pref);
            Vector2 anchorXZ = Vector2.zero;
            bool boneAlive = hasPref && TryResolveAnchorWorldPos(giant, comp, in pref, out anchorXZ);

            float[] keys = sortKeysBuffer;
            IntVec3[] cells = sortCellsBuffer;
            if (keys == null || keys.Length < totalCount)
            {
                int cap = Math.Max(256, totalCount * 2);
                keys = sortKeysBuffer = new float[cap];
                cells = sortCellsBuffer = new IntVec3[cap];
            }

            for (int i = 0; i < keptExtra; i++)
            {
                IntVec3 c = outCells[i];
                cells[i] = c;
                keys[i] = ScoreCellFast(
                    c,
                    dominantBoneIndex: -2,
                    shooterPos,
                    checkRange,
                    minRangeSq,
                    maxRangeSq,
                    hasHover,
                    curHoverCell,
                    hoverBoneIdx,
                    hasPref,
                    in pref,
                    boneAlive,
                    anchorXZ);
            }

            for (int i = 0; i < hitCount; i++)
            {
                HitCellEntry entry = hitCells[i];
                IntVec3 c = entry.cell;
                outCells.Add(c);
                int dst = keptExtra + i;
                cells[dst] = c;
                keys[dst] = ScoreCellFast(
                    c,
                    entry.dominantBoneIndex,
                    shooterPos,
                    checkRange,
                    minRangeSq,
                    maxRangeSq,
                    hasHover,
                    curHoverCell,
                    hoverBoneIdx,
                    hasPref,
                    in pref,
                    boneAlive,
                    anchorXZ);
            }

            Array.Sort(keys, cells, 0, totalCount);
            for (int i = 0; i < totalCount; i++)
            {
                outCells[i] = cells[i];
            }
        }

        private static float ScoreCellFast(
            IntVec3 cell,
            int dominantBoneIndex,
            IntVec3 shooterPos,
            bool checkRange,
            float minRangeSq,
            float maxRangeSq,
            bool hasHover,
            IntVec3 curHoverCell,
            int hoverBoneIdx,
            bool hasPref,
            in AimPreference pref,
            bool boneAlive,
            Vector2 anchorXZ)
        {
            int sdx = cell.x - shooterPos.x;
            int sdz = cell.z - shooterPos.z;
            float shooterDistSq = sdx * sdx + sdz * sdz;

            float score = (checkRange && (shooterDistSq > maxRangeSq || shooterDistSq < minRangeSq))
                ? 1000000f
                : 0f;

            if (hasHover)
            {
                if (cell == curHoverCell)
                {
                    return score - 100000f;
                }
                if (hoverBoneIdx >= 0 && dominantBoneIndex == hoverBoneIdx)
                {
                    score -= 10000f;
                }
                int hdx = cell.x - curHoverCell.x;
                int hdz = cell.z - curHoverCell.z;
                return score + (hdx * hdx + hdz * hdz) * 10f + shooterDistSq * 0.01f;
            }

            if (hasPref)
            {
                if (dominantBoneIndex >= -1)
                {
                    if (boneAlive && dominantBoneIndex == pref.BoneIndex)
                    {
                        score -= 50000f;
                    }
                    else if (!boneAlive && cell == pref.LastClickedCell)
                    {
                        score -= 20000f;
                    }
                }
                float dx = (cell.x + 0.5f) - anchorXZ.x;
                float dz = (cell.z + 0.5f) - anchorXZ.y;
                return score + (dx * dx + dz * dz) * 10f + shooterDistSq * 0.01f;
            }

            return score + shooterDistSq;
        }

        /// <summary>
        /// Checks whether any cell in HitCells satisfies a predicate, useful for range/command-range checks.
        /// </summary>
        public static bool AnyHitCellSatisfies(CompGiantRig comp, Func<IntVec3, bool> predicate)
        {
            List<HitCellEntry> hitCells = comp?.Footprint?.hitCells;
            if (hitCells == null)
            {
                return false;
            }
            for (int i = 0; i < hitCells.Count; i++)
            {
                if (predicate(hitCells[i].cell))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
