using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using BitOperations = System.Numerics.BitOperations;

namespace GiantCreatureFramework
{
    public readonly struct HitCellEntry
    {
        public readonly IntVec3 cell;
        public readonly float hitCoverage;
        public readonly float pawnExclusionCoverage;
        public readonly float collisionCoverage;
        public readonly int dominantBoneIndex;
        public readonly bool isPawnExclusionCell;
        public readonly bool isCollisionCell;

        public HitCellEntry(
            IntVec3 cell,
            float hitCoverage,
            float collisionCoverage,
            int dominantBoneIndex,
            bool isCollisionCell,
            float pawnExclusionCoverage = 0f,
            bool isPawnExclusionCell = false)
        {
            this.cell = cell;
            this.hitCoverage = hitCoverage;
            this.pawnExclusionCoverage = pawnExclusionCoverage > 0f ? pawnExclusionCoverage : (isCollisionCell ? collisionCoverage : 0f);
            this.collisionCoverage = collisionCoverage;
            this.dominantBoneIndex = dominantBoneIndex;
            this.isPawnExclusionCell = isPawnExclusionCell || isCollisionCell;
            this.isCollisionCell = isCollisionCell;
        }
    }

    /// <summary>
    /// Reusable, allocation-free rasterization workspace and output snapshot for a single Giant Creature's
    /// three-tier spatial footprint: HitCells (silhouette), PawnExclusionCells (visual canopy), and CollisionCells (core hull).
    /// </summary>
    public sealed class GiantFootprintData
    {
        private struct CellAccum
        {
            public ushort hitSubMask;
            public ushort pawnExclusionSubMask;
            public ushort collisionSubMask;
            public int bestBoneIndex;
            public int bestBoneSamples;
            public float bestBoneLayer;
        }

        private const int LocalGridShift = 7;
        private const int LocalGridSize = 1 << LocalGridShift; // 128x128 local stamp grid
        private const int LocalGridHalf = LocalGridSize >> 1;  // [-64..+63] around center
        private const ushort CenterCoreSubMask = 0x0660;       // central 2x2 subsamples at {0.375, 0.625} x {0.375, 0.625}

        private readonly CellAccum[] localAccumGrid = new CellAccum[LocalGridSize * LocalGridSize];
        private readonly int[] localAccumStamp = new int[LocalGridSize * LocalGridSize];
        private readonly List<int> touchedLocalGridSlots = new List<int>(256);
        private int currentLocalStamp;
        private int localOriginX;
        private int localOriginZ;

        private readonly Dictionary<IntVec3, CellAccum> accumMap = new Dictionary<IntVec3, CellAccum>(16);

        public readonly List<HitCellEntry> hitCells = new List<HitCellEntry>(256);
        public readonly List<IntVec3> hitCellPositions = new List<IntVec3>(256);
        public readonly HashSet<IntVec3> hitCellSet = new HashSet<IntVec3>(256);
        public readonly Dictionary<IntVec3, HitCellEntry> hitEntryByCell = new Dictionary<IntVec3, HitCellEntry>(256);

        public readonly List<IntVec3> pawnExclusionCells = new List<IntVec3>(128);
        public readonly HashSet<IntVec3> pawnExclusionCellSet = new HashSet<IntVec3>(128);
        public readonly List<IntVec3> localPawnExclusionOffsets = new List<IntVec3>(128);

        public readonly List<IntVec3> collisionCells = new List<IntVec3>(64);
        public readonly HashSet<IntVec3> collisionCellSet = new HashSet<IntVec3>(64);
        public readonly List<IntVec3> localCollisionOffsets = new List<IntVec3>(64);
        public readonly List<IntVec3> groundedLandingOffsets = new List<IntVec3>(64);
        public IReadOnlyList<IntVec3> LandingCollisionOffsets => localCollisionOffsets.Count > 0 ? localCollisionOffsets : groundedLandingOffsets;

        public CellRect tightHitBounds;
        public CellRect tightPawnExclusionBounds;
        public CellRect tightCollisionBounds;
        public CellRect worldDrawBounds;
        public IntVec3 collisionAnchorCell = IntVec3.Invalid;
        public int maxPawnExclusionHorizontalExtent;
        public int maxCollisionHorizontalExtent;

        public BoneWorldPose[] bonePoses = Array.Empty<BoneWorldPose>();
        public Vector4[] boneBoundsXZ = Array.Empty<Vector4>(); // x=minX, y=maxX, z=minZ, w=maxZ
        public int[] boneOrderDescByLayer = Array.Empty<int>();
        private GiantRigDef lastSortedRigDef;
        public SoftWhipRibbonSnapshot[] softWhipSnapshots = Array.Empty<SoftWhipRibbonSnapshot>();
        public Vector4[] softWhipBoundsXZ = Array.Empty<Vector4>(); // x=minX, y=maxX, z=minZ, w=maxZ
        private Vector4[][] softWhipQuadBoundsXZ = Array.Empty<Vector4[]>();
        private TextureAlphaMask[] cachedSoftWhipMasks = Array.Empty<TextureAlphaMask>();

        private struct PrecomputedTri2D
        {
            public bool valid;
            public float ax;
            public float az;
            public float dwB_dx;
            public float dwB_dz;
            public float dwC_dx;
            public float dwC_dz;

            public static PrecomputedTri2D Create(Vector2 a, Vector2 b, Vector2 c)
            {
                float v0x = b.x - a.x;
                float v0z = b.y - a.y;
                float v1x = c.x - a.x;
                float v1z = c.y - a.y;
                float denom = v0x * v1z - v1x * v0z;
                if (Mathf.Abs(denom) < 1e-8f)
                {
                    return default;
                }
                float inv = 1f / denom;
                return new PrecomputedTri2D
                {
                    valid = true,
                    ax = a.x,
                    az = a.y,
                    dwB_dx = v1z * inv,
                    dwB_dz = -v1x * inv,
                    dwC_dx = -v0z * inv,
                    dwC_dz = v0x * inv
                };
            }
        }

        private void AccumulateCellMask(
            int cx,
            int cz,
            ushort cellMask,
            int samples,
            int ownerBoneIdx,
            float ownerLayer,
            bool contributesToHitbox,
            bool contributesToPawnExclusion,
            bool contributesToCollision)
        {
            int lx = cx - localOriginX;
            int lz = cz - localOriginZ;
            if ((uint)lx < LocalGridSize && (uint)lz < LocalGridSize)
            {
                int slot = (lz << LocalGridShift) | lx;
                CellAccum acc;
                if (localAccumStamp[slot] != currentLocalStamp)
                {
                    localAccumStamp[slot] = currentLocalStamp;
                    touchedLocalGridSlots.Add(slot);
                    acc = default;
                }
                else
                {
                    acc = localAccumGrid[slot];
                }

                if (contributesToHitbox)
                {
                    acc.hitSubMask |= cellMask;
                    if (acc.bestBoneSamples == 0 ||
                        samples > acc.bestBoneSamples + 2 ||
                        (Mathf.Abs(samples - acc.bestBoneSamples) <= 2 && ownerLayer >= acc.bestBoneLayer))
                    {
                        acc.bestBoneIndex = ownerBoneIdx;
                        acc.bestBoneSamples = samples;
                        acc.bestBoneLayer = ownerLayer;
                    }
                }
                if (contributesToPawnExclusion)
                {
                    acc.pawnExclusionSubMask |= cellMask;
                }
                if (contributesToCollision)
                {
                    acc.collisionSubMask |= cellMask;
                }
                localAccumGrid[slot] = acc;
                return;
            }

            IntVec3 cell = new IntVec3(cx, 0, cz);
            accumMap.TryGetValue(cell, out CellAccum fallbackAcc);
            if (contributesToHitbox)
            {
                fallbackAcc.hitSubMask |= cellMask;
                if (fallbackAcc.bestBoneSamples == 0 ||
                    samples > fallbackAcc.bestBoneSamples + 2 ||
                    (Mathf.Abs(samples - fallbackAcc.bestBoneSamples) <= 2 && ownerLayer >= fallbackAcc.bestBoneLayer))
                {
                    fallbackAcc.bestBoneIndex = ownerBoneIdx;
                    fallbackAcc.bestBoneSamples = samples;
                    fallbackAcc.bestBoneLayer = ownerLayer;
                }
            }
            if (contributesToPawnExclusion)
            {
                fallbackAcc.pawnExclusionSubMask |= cellMask;
            }
            if (contributesToCollision)
            {
                fallbackAcc.collisionSubMask |= cellMask;
            }
            accumMap[cell] = fallbackAcc;
        }

        public void Clear()
        {
            touchedLocalGridSlots.Clear();
            if (accumMap.Count > 0)
            {
                accumMap.Clear();
            }
            hitCells.Clear();
            hitCellPositions.Clear();
            hitCellSet.Clear();
            hitEntryByCell.Clear();
            pawnExclusionCells.Clear();
            pawnExclusionCellSet.Clear();
            localPawnExclusionOffsets.Clear();
            collisionCells.Clear();
            collisionCellSet.Clear();
            localCollisionOffsets.Clear();
            groundedLandingOffsets.Clear();
            tightHitBounds = CellRect.Empty;
            tightPawnExclusionBounds = CellRect.Empty;
            tightCollisionBounds = CellRect.Empty;
            worldDrawBounds = CellRect.Empty;
            collisionAnchorCell = IntVec3.Invalid;
            maxPawnExclusionHorizontalExtent = 0;
            maxCollisionHorizontalExtent = 0;
            for (int s = 0; s < softWhipSnapshots.Length; s++)
            {
                softWhipSnapshots[s] = null;
                softWhipBoundsXZ[s] = Vector4.zero;
            }
        }

        /// <summary>
        /// Strips all ground collision and pawn-exclusion cells when a giant is airborne while preserving
        /// hitCells for ranged targeting, selection, and groundedLandingOffsets for safe touchdown checks.
        /// </summary>
        public void ClearCollisionVolume()
        {
            if (localCollisionOffsets.Count > 0)
            {
                groundedLandingOffsets.Clear();
                groundedLandingOffsets.AddRange(localCollisionOffsets);
            }

            pawnExclusionCells.Clear();
            pawnExclusionCellSet.Clear();
            localPawnExclusionOffsets.Clear();
            collisionCells.Clear();
            collisionCellSet.Clear();
            localCollisionOffsets.Clear();
            tightPawnExclusionBounds = CellRect.Empty;
            tightCollisionBounds = CellRect.Empty;
            collisionAnchorCell = IntVec3.Invalid;
            maxPawnExclusionHorizontalExtent = 0;
            maxCollisionHorizontalExtent = 0;

            for (int i = 0; i < hitCells.Count; i++)
            {
                HitCellEntry entry = hitCells[i];
                if (entry.isCollisionCell || entry.isPawnExclusionCell || entry.collisionCoverage > 0f || entry.pawnExclusionCoverage > 0f)
                {
                    var stripped = new HitCellEntry(
                        entry.cell,
                        entry.hitCoverage,
                        0f,
                        entry.dominantBoneIndex,
                        isCollisionCell: false,
                        pawnExclusionCoverage: 0f,
                        isPawnExclusionCell: false);
                    hitCells[i] = stripped;
                    hitEntryByCell[entry.cell] = stripped;
                }
            }
        }

        public void Rebuild(
            GiantRigDef rig,
            ReadOnlySpan<BoneWorldPose> worldPoses,
            ReadOnlySpan<TextureAlphaMask> masks,
            IntVec3 fallbackCenterCell,
            IntVec3? mapSize = null,
            ReadOnlySpan<SoftWhipRibbonSnapshot> softWhips = default,
            ReadOnlySpan<TextureAlphaMask> softWhipMasks = default,
            IntVec3? collisionAnchorOverride = null)
        {
            Clear();
            currentLocalStamp++;
            if (currentLocalStamp == int.MaxValue)
            {
                Array.Clear(localAccumStamp, 0, localAccumStamp.Length);
                currentLocalStamp = 1;
            }
            localOriginX = fallbackCenterCell.x - LocalGridHalf;
            localOriginZ = fallbackCenterCell.z - LocalGridHalf;

            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = nodes.Length;

            bool needSort = !ReferenceEquals(lastSortedRigDef, rig);
            if (bonePoses.Length != count)
            {
                bonePoses = new BoneWorldPose[count];
                boneBoundsXZ = new Vector4[count];
                boneOrderDescByLayer = new int[count];
                for (int i = 0; i < count; i++)
                {
                    boneOrderDescByLayer[i] = i;
                }
                needSort = true;
            }
            else if (needSort)
            {
                for (int i = 0; i < count; i++)
                {
                    boneOrderDescByLayer[i] = i;
                }
            }

            for (int i = 0; i < count; i++)
            {
                bonePoses[i] = worldPoses[i];
                boneBoundsXZ[i] = new Vector4(float.MaxValue, float.MinValue, float.MaxValue, float.MinValue);
            }

            if (needSort || rig.hasAnyDirectionalVariants)
            {
                // Allocation-free insertion sort descending by layerOffset (O(N) on already-sorted rigs).
                for (int i = 1; i < count; i++)
                {
                    int keyIdx = boneOrderDescByLayer[i];
                    float keyLayer = bonePoses[keyIdx].layerOffset;
                    int j = i - 1;
                    while (j >= 0 && bonePoses[boneOrderDescByLayer[j]].layerOffset < keyLayer)
                    {
                        boneOrderDescByLayer[j + 1] = boneOrderDescByLayer[j];
                        j--;
                    }
                    boneOrderDescByLayer[j + 1] = keyIdx;
                }
                lastSortedRigDef = rig;
            }

            float drawMinX = fallbackCenterCell.x;
            float drawMaxX = fallbackCenterCell.x + 1f;
            float drawMinZ = fallbackCenterCell.z;
            float drawMaxZ = fallbackCenterCell.z + 1f;

            for (int i = 0; i < count; i++)
            {
                ref readonly BoneWorldPose pose = ref worldPoses[i];
                if (!pose.visible)
                {
                    continue;
                }

                BoneNodeDef node = nodes[i];
                TextureAlphaMask mask = i < masks.Length ? masks[i] : null;
                if (mask == null || !mask.HasAnyOpaquePixels)
                {
                    continue;
                }

                Vector2 c0 = pose.UVToWorldXZ(mask.minU, mask.minV);
                Vector2 c1 = pose.UVToWorldXZ(mask.maxU, mask.minV);
                Vector2 c2 = pose.UVToWorldXZ(mask.maxU, mask.maxV);
                Vector2 c3 = pose.UVToWorldXZ(mask.minU, mask.maxV);

                float bMinX = Mathf.Min(Mathf.Min(c0.x, c1.x), Mathf.Min(c2.x, c3.x));
                float bMaxX = Mathf.Max(Mathf.Max(c0.x, c1.x), Mathf.Max(c2.x, c3.x));
                float bMinZ = Mathf.Min(Mathf.Min(c0.y, c1.y), Mathf.Min(c2.y, c3.y));
                float bMaxZ = Mathf.Max(Mathf.Max(c0.y, c1.y), Mathf.Max(c2.y, c3.y));
                boneBoundsXZ[i] = new Vector4(bMinX, bMaxX, bMinZ, bMaxZ);

                if (bMinX < drawMinX) drawMinX = bMinX;
                if (bMaxX > drawMaxX) drawMaxX = bMaxX;
                if (bMinZ < drawMinZ) drawMinZ = bMinZ;
                if (bMaxZ > drawMaxZ) drawMaxZ = bMaxZ;

                BoneCollisionRole colRole = node.ResolveEffectiveCollisionRole();
                bool contributesToCoreCollision = colRole == BoneCollisionRole.CoreHull;
                bool contributesToPawnExclusion = colRole == BoneCollisionRole.CoreHull || colRole == BoneCollisionRole.Canopy;
                bool contributesToHitbox = node.contributesToHitbox || contributesToPawnExclusion;
                if (!contributesToHitbox && !contributesToPawnExclusion && !contributesToCoreCollision)
                {
                    continue;
                }

                int minCX = Mathf.FloorToInt(bMinX);
                int maxCX = Mathf.FloorToInt(Mathf.Max(bMinX, bMaxX - 1e-5f));
                int minCZ = Mathf.FloorToInt(bMinZ);
                int maxCZ = Mathf.FloorToInt(Mathf.Max(bMinZ, bMaxZ - 1e-5f));

                if (mapSize.HasValue)
                {
                    minCX = Mathf.Max(0, minCX);
                    minCZ = Mathf.Max(0, minCZ);
                    maxCX = Mathf.Min(mapSize.Value.x - 1, maxCX);
                    maxCZ = Mathf.Min(mapSize.Value.z - 1, maxCZ);
                }

                if (minCX > maxCX || minCZ > maxCZ)
                {
                    continue;
                }

                pose.ComputeWorldToUVAffineAt(
                    minCX + 0.125f,
                    minCZ + 0.125f,
                    out float originU,
                    out float originV,
                    out float duCellX,
                    out float dvCellX,
                    out float duCellZ,
                    out float dvCellZ);

                float duStepX = 0.25f * duCellX;
                float dvStepX = 0.25f * dvCellX;
                float duStepZ = 0.25f * duCellZ;
                float dvStepZ = 0.25f * dvCellZ;

                float maskWF = mask.widthF;
                float maskHF = mask.heightF;
                float dpxStepX = duStepX * maskWF;
                float dpyStepX = dvStepX * maskHF;
                float dpxStepZ = duStepZ * maskWF;
                float dpyStepZ = dvStepZ * maskHF;

                float spanUX = 3f * duStepX;
                float spanUZ = 3f * duStepZ;
                float spanVX = 3f * dvStepX;
                float spanVZ = 3f * dvStepZ;
                float uMinOff = (spanUX < 0f ? spanUX : 0f) + (spanUZ < 0f ? spanUZ : 0f);
                float uMaxOff = (spanUX > 0f ? spanUX : 0f) + (spanUZ > 0f ? spanUZ : 0f);
                float vMinOff = (spanVX < 0f ? spanVX : 0f) + (spanVZ < 0f ? spanVZ : 0f);
                float vMaxOff = (spanVX > 0f ? spanVX : 0f) + (spanVZ > 0f ? spanVZ : 0f);

                for (int cz = minCZ; cz <= maxCZ; cz++)
                {
                    int dzCells = cz - minCZ;
                    float rowStartU = originU + dzCells * duCellZ;
                    float rowStartV = originV + dzCells * dvCellZ;
                    for (int cx = minCX; cx <= maxCX; cx++)
                    {
                        int dxCells = cx - minCX;
                        float rowU = rowStartU + dxCells * duCellX;
                        float rowV = rowStartV + dxCells * dvCellX;

                        int tileClass = mask.ClassifyUVRect(rowU + uMinOff, rowU + uMaxOff, rowV + vMinOff, rowV + vMaxOff);
                        if (tileClass == 0)
                        {
                            continue;
                        }

                        ushort cellMask;
                        int samples;
                        if (tileClass == 2)
                        {
                            cellMask = 0xFFFF;
                            samples = 16;
                        }
                        else
                        {
                            cellMask = mask.SampleCell4x4Pixels(rowU * maskWF, rowV * maskHF, dpxStepX, dpyStepX, dpxStepZ, dpyStepZ);
                            if (cellMask == 0)
                            {
                                continue;
                            }
                            samples = BitOperations.PopCount(cellMask);
                        }

                        AccumulateCellMask(
                            cx,
                            cz,
                            cellMask,
                            samples,
                            i,
                            pose.layerOffset,
                            contributesToHitbox,
                            contributesToPawnExclusion,
                            contributesToCoreCollision);
                    }
                }
            }

            RasterizeSoftWhips(
                rig,
                softWhips,
                softWhipMasks,
                mapSize,
                ref drawMinX,
                ref drawMaxX,
                ref drawMinZ,
                ref drawMaxZ);

            worldDrawBounds = CellRect.FromLimits(
                Mathf.FloorToInt(drawMinX) - 1,
                Mathf.FloorToInt(drawMinZ) - 1,
                Mathf.CeilToInt(drawMaxX) + 1,
                Mathf.CeilToInt(drawMaxZ) + 1);

            bool anyHitBounds = false;
            int hMinX = int.MaxValue, hMaxX = int.MinValue;
            int hMinZ = int.MaxValue, hMaxZ = int.MinValue;

            for (int t = 0; t < touchedLocalGridSlots.Count; t++)
            {
                int slot = touchedLocalGridSlots[t];
                int lx = slot & (LocalGridSize - 1);
                int lz = slot >> LocalGridShift;
                IntVec3 cell = new IntVec3(localOriginX + lx, 0, localOriginZ + lz);
                CommitAccumulatedCell(rig, cell, in localAccumGrid[slot], ref anyHitBounds, ref hMinX, ref hMaxX, ref hMinZ, ref hMaxZ);
            }

            if (accumMap.Count > 0)
            {
                foreach (var kv in accumMap)
                {
                    CellAccum fAcc = kv.Value;
                    CommitAccumulatedCell(rig, kv.Key, in fAcc, ref anyHitBounds, ref hMinX, ref hMaxX, ref hMinZ, ref hMaxZ);
                }
            }

            if (anyHitBounds)
            {
                tightHitBounds = CellRect.FromLimits(hMinX, hMinZ, hMaxX, hMaxZ);
            }
            else
            {
                tightHitBounds = CellRect.SingleCell(fallbackCenterCell);
            }

            collisionAnchorCell = collisionAnchorOverride ?? fallbackCenterCell;
            if (collisionCells.Count > 0)
            {
                int cMinX = int.MaxValue;
                int cMaxX = int.MinValue;
                int cMinZ = int.MaxValue;
                int cMaxZ = int.MinValue;
                int maxExtent = 0;

                for (int i = 0; i < collisionCells.Count; i++)
                {
                    IntVec3 cell = collisionCells[i];
                    if (cell.x < cMinX) cMinX = cell.x;
                    if (cell.x > cMaxX) cMaxX = cell.x;
                    if (cell.z < cMinZ) cMinZ = cell.z;
                    if (cell.z > cMaxZ) cMaxZ = cell.z;

                    IntVec3 offset = cell - collisionAnchorCell;
                    localCollisionOffsets.Add(offset);
                    int ext = Mathf.Max(Mathf.Abs(offset.x), Mathf.Abs(offset.z));
                    if (ext > maxExtent) maxExtent = ext;
                }

                tightCollisionBounds = CellRect.FromLimits(cMinX, cMinZ, cMaxX, cMaxZ);
                maxCollisionHorizontalExtent = maxExtent;
            }
            else if (hitCellPositions.Count > 0)
            {
                IntVec3 bestCell = hitCellSet.Contains(collisionAnchorCell) ? collisionAnchorCell : hitCellPositions[0];
                if (!hitCellSet.Contains(collisionAnchorCell))
                {
                    int bestDistSq = int.MaxValue;
                    for (int i = 0; i < hitCellPositions.Count; i++)
                    {
                        int dSq = (hitCellPositions[i] - collisionAnchorCell).LengthHorizontalSquared;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestCell = hitCellPositions[i];
                        }
                    }
                }
                collisionCells.Add(bestCell);
                collisionCellSet.Add(bestCell);
                if (pawnExclusionCellSet.Add(bestCell))
                {
                    pawnExclusionCells.Add(bestCell);
                }
                IntVec3 fbOffset = bestCell - collisionAnchorCell;
                localCollisionOffsets.Add(fbOffset);
                tightCollisionBounds = CellRect.SingleCell(bestCell);
                maxCollisionHorizontalExtent = Mathf.Max(Mathf.Abs(fbOffset.x), Mathf.Abs(fbOffset.z));
            }
            else
            {
                tightCollisionBounds = CellRect.SingleCell(collisionAnchorCell);
                localCollisionOffsets.Add(IntVec3.Zero);
                maxCollisionHorizontalExtent = 0;
            }

            localPawnExclusionOffsets.Clear();
            if (pawnExclusionCells.Count > 0)
            {
                int pMinX = int.MaxValue, pMaxX = int.MinValue;
                int pMinZ = int.MaxValue, pMaxZ = int.MinValue;
                int maxExclExtent = 0;
                for (int i = 0; i < pawnExclusionCells.Count; i++)
                {
                    IntVec3 pc = pawnExclusionCells[i];
                    if (pc.x < pMinX) pMinX = pc.x;
                    if (pc.x > pMaxX) pMaxX = pc.x;
                    if (pc.z < pMinZ) pMinZ = pc.z;
                    if (pc.z > pMaxZ) pMaxZ = pc.z;

                    IntVec3 exclOffset = pc - collisionAnchorCell;
                    localPawnExclusionOffsets.Add(exclOffset);
                    int ext = Mathf.Max(Mathf.Abs(exclOffset.x), Mathf.Abs(exclOffset.z));
                    if (ext > maxExclExtent)
                    {
                        maxExclExtent = ext;
                    }
                }
                tightPawnExclusionBounds = CellRect.FromLimits(pMinX, pMinZ, pMaxX, pMaxZ);
                maxPawnExclusionHorizontalExtent = maxExclExtent;
            }
            else
            {
                tightPawnExclusionBounds = tightCollisionBounds;
                localPawnExclusionOffsets.AddRange(localCollisionOffsets);
                maxPawnExclusionHorizontalExtent = maxCollisionHorizontalExtent;
            }
        }

        private void CommitAccumulatedCell(
            GiantRigDef rig,
            IntVec3 cell,
            in CellAccum acc,
            ref bool anyHitBounds,
            ref int hMinX,
            ref int hMaxX,
            ref int hMinZ,
            ref int hMaxZ)
        {
            float hitCov = BitOperations.PopCount(acc.hitSubMask) * 0.0625f;
            float pawnExclCov = BitOperations.PopCount(acc.pawnExclusionSubMask) * 0.0625f;
            float colCov = BitOperations.PopCount(acc.collisionSubMask) * 0.0625f;

            float pawnExclThresh = Mathf.Max(
                rig.hitCoverageThreshold,
                rig.pawnExclusionCoverageThreshold > 0.001f ? rig.pawnExclusionCoverageThreshold : 0.16f);
            bool isCol = (acc.collisionSubMask & CenterCoreSubMask) != 0 && colCov >= rig.collisionCoverageThreshold;
            bool isPawnExcl = isCol || pawnExclCov >= pawnExclThresh;
            bool isHit = isPawnExcl || hitCov >= rig.hitCoverageThreshold;
            if (!isHit)
            {
                return;
            }

            if (isCol)
            {
                collisionCells.Add(cell);
                collisionCellSet.Add(cell);
            }

            if (isPawnExcl)
            {
                pawnExclusionCells.Add(cell);
                pawnExclusionCellSet.Add(cell);
            }

            float effectiveHitCov = Mathf.Max(hitCov, Mathf.Max(pawnExclCov, colCov));
            var entry = new HitCellEntry(
                cell,
                effectiveHitCov,
                colCov,
                acc.bestBoneIndex,
                isCol,
                pawnExclusionCoverage: Mathf.Max(pawnExclCov, colCov),
                isPawnExclusionCell: isPawnExcl);
            hitCells.Add(entry);
            hitCellPositions.Add(cell);
            hitCellSet.Add(cell);
            hitEntryByCell[cell] = entry;

            if (cell.x < hMinX) hMinX = cell.x;
            if (cell.x > hMaxX) hMaxX = cell.x;
            if (cell.z < hMinZ) hMinZ = cell.z;
            if (cell.z > hMaxZ) hMaxZ = cell.z;
            anyHitBounds = true;
        }

        /// <summary>
        /// Exact sub-pixel point hit test against rendered bone textures and boneless soft-whip ribbons from topmost layer downward.
        /// </summary>
        public bool TryHitTestPoint(
            GiantRigDef rig,
            ReadOnlySpan<TextureAlphaMask> masks,
            Vector3 worldPos,
            out int hitBoneIndex,
            ReadOnlySpan<TextureAlphaMask> softWhipMasks = default)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            bool anyHit = false;
            float bestLayer = float.NegativeInfinity;
            hitBoneIndex = -1;

            for (int k = 0; k < boneOrderDescByLayer.Length; k++)
            {
                int idx = boneOrderDescByLayer[k];
                if (!nodes[idx].contributesToHitbox || idx >= bonePoses.Length)
                {
                    continue;
                }
                ref readonly BoneWorldPose pose = ref bonePoses[idx];
                if (!pose.visible)
                {
                    continue;
                }
                if (idx < boneBoundsXZ.Length)
                {
                    Vector4 bb = boneBoundsXZ[idx];
                    if (worldPos.x < bb.x || worldPos.x > bb.y || worldPos.z < bb.z || worldPos.z > bb.w)
                    {
                        continue;
                    }
                }
                TextureAlphaMask mask = idx < masks.Length ? masks[idx] : null;
                if (mask == null)
                {
                    continue;
                }
                if (pose.TryWorldToUV(worldPos.x, worldPos.z, out float u, out float v) && mask.IsOpaqueUV(u, v))
                {
                    anyHit = true;
                    bestLayer = pose.layerOffset;
                    hitBoneIndex = idx;
                    break;
                }
            }

            if (rig.softWhips != null && softWhipSnapshots.Length == rig.softWhips.Count)
            {
                ReadOnlySpan<TextureAlphaMask> effectiveWhipMasks = !softWhipMasks.IsEmpty
                    ? softWhipMasks
                    : (ReadOnlySpan<TextureAlphaMask>)cachedSoftWhipMasks;
                Vector2 pt = new Vector2(worldPos.x, worldPos.z);
                for (int s = 0; s < rig.softWhips.Count; s++)
                {
                    SoftWhipDef wDef = rig.softWhips[s];
                    if (wDef == null || !wDef.contributesToHitbox || wDef.resolvedRootBoneIndex < 0)
                    {
                        continue;
                    }
                    if (anyHit && wDef.layerOffset <= bestLayer)
                    {
                        continue;
                    }
                    SoftWhipRibbonSnapshot snap = softWhipSnapshots[s];
                    if (snap == null || !snap.visible || snap.stripLeftLocalXZ == null || snap.stripLeftLocalXZ.Length < 2)
                    {
                        continue;
                    }
                    Vector4 b = softWhipBoundsXZ[s];
                    if (pt.x < b.x || pt.x > b.y || pt.y < b.z || pt.y > b.w)
                    {
                        continue;
                    }
                    TextureAlphaMask mask = s < effectiveWhipMasks.Length ? effectiveWhipMasks[s] : null;
                    if (mask == null || !mask.HasAnyOpaquePixels)
                    {
                        continue;
                    }

                    int m = snap.stripLeftLocalXZ.Length;
                    Vector2 root = snap.rootWorldXZ;
                    Vector4[] qBounds = s < softWhipQuadBoundsXZ.Length ? softWhipQuadBoundsXZ[s] : null;
                    for (int k = 0; k < m - 1; k++)
                    {
                        if (qBounds != null && k < qBounds.Length)
                        {
                            Vector4 qb = qBounds[k];
                            if (pt.x < qb.x || pt.x > qb.y || pt.y < qb.z || pt.y > qb.w)
                            {
                                continue;
                            }
                        }
                        Vector2 a = root + snap.stripLeftLocalXZ[k];
                        Vector2 bPt = root + snap.stripLeftLocalXZ[k + 1];
                        Vector2 c = root + snap.stripRightLocalXZ[k + 1];
                        Vector2 d = root + snap.stripRightLocalXZ[k];
                        if (TryQuadWorldToUV(pt, a, bPt, c, d, snap.vCoords[k], snap.vCoords[k + 1], out float u, out float v) &&
                            mask.IsOpaqueUV(u, v))
                        {
                            anyHit = true;
                            bestLayer = wDef.layerOffset;
                            hitBoneIndex = wDef.resolvedRootBoneIndex;
                            break;
                        }
                    }
                }
            }

            return anyHit;
        }

        /// <summary>
        /// Sweeps a projectile segment [startWorld -> endWorld] across the transformed alpha masks,
        /// returning the exact world entry point and struck bone index.
        /// </summary>
        public bool TryHitTestSegment(
            GiantRigDef rig,
            ReadOnlySpan<TextureAlphaMask> masks,
            Vector3 startWorld,
            Vector3 endWorld,
            out int hitBoneIndex,
            out Vector3 hitWorldPos,
            ReadOnlySpan<TextureAlphaMask> softWhipMasks = default)
        {
            Vector3 delta = endWorld - startWorld;
            delta.y = 0f;
            float dist = delta.magnitude;
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / 0.12f));
            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                Vector3 samplePos = Vector3.LerpUnclamped(startWorld, endWorld, t);
                if (TryHitTestPoint(rig, masks, samplePos, out hitBoneIndex, softWhipMasks))
                {
                    hitWorldPos = samplePos;
                    return true;
                }
            }
            hitBoneIndex = -1;
            hitWorldPos = endWorld;
            return false;
        }

        private void RasterizeSoftWhips(
            GiantRigDef rig,
            ReadOnlySpan<SoftWhipRibbonSnapshot> softWhips,
            ReadOnlySpan<TextureAlphaMask> softWhipMasks,
            IntVec3? mapSize,
            ref float drawMinX,
            ref float drawMaxX,
            ref float drawMinZ,
            ref float drawMaxZ)
        {
            int whipCount = rig.softWhips?.Count ?? 0;
            if (softWhipSnapshots.Length != whipCount)
            {
                softWhipSnapshots = new SoftWhipRibbonSnapshot[whipCount];
                softWhipBoundsXZ = new Vector4[whipCount];
                softWhipQuadBoundsXZ = new Vector4[whipCount][];
                cachedSoftWhipMasks = new TextureAlphaMask[whipCount];
            }
            for (int s = 0; s < whipCount; s++)
            {
                cachedSoftWhipMasks[s] = s < softWhipMasks.Length ? softWhipMasks[s] : null;
            }
            if (whipCount == 0 || softWhips.IsEmpty)
            {
                return;
            }

            for (int s = 0; s < whipCount && s < softWhips.Length; s++)
            {
                SoftWhipDef wDef = rig.softWhips[s];
                SoftWhipRibbonSnapshot snap = softWhips[s];
                softWhipSnapshots[s] = snap;
                if (wDef == null || snap == null || !snap.visible || snap.stripLeftLocalXZ == null || snap.stripLeftLocalXZ.Length < 2)
                {
                    softWhipBoundsXZ[s] = Vector4.zero;
                    continue;
                }

                TextureAlphaMask mask = s < softWhipMasks.Length ? softWhipMasks[s] : null;
                if (mask == null || !mask.HasAnyOpaquePixels)
                {
                    softWhipBoundsXZ[s] = Vector4.zero;
                    continue;
                }

                int m = snap.stripLeftLocalXZ.Length;
                Vector2 root = snap.rootWorldXZ;
                float wMinX = float.MaxValue, wMaxX = float.MinValue;
                float wMinZ = float.MaxValue, wMaxZ = float.MinValue;
                for (int k = 0; k < m; k++)
                {
                    Vector2 lw = root + snap.stripLeftLocalXZ[k];
                    Vector2 rw = root + snap.stripRightLocalXZ[k];
                    if (lw.x < wMinX) wMinX = lw.x;
                    if (lw.x > wMaxX) wMaxX = lw.x;
                    if (rw.x < wMinX) wMinX = rw.x;
                    if (rw.x > wMaxX) wMaxX = rw.x;
                    if (lw.y < wMinZ) wMinZ = lw.y;
                    if (lw.y > wMaxZ) wMaxZ = lw.y;
                    if (rw.y < wMinZ) wMinZ = rw.y;
                    if (rw.y > wMaxZ) wMaxZ = rw.y;
                }
                softWhipBoundsXZ[s] = new Vector4(wMinX, wMaxX, wMinZ, wMaxZ);

                if (wMinX < drawMinX) drawMinX = wMinX;
                if (wMaxX > drawMaxX) drawMaxX = wMaxX;
                if (wMinZ < drawMinZ) drawMinZ = wMinZ;
                if (wMaxZ > drawMaxZ) drawMaxZ = wMaxZ;

                int quadCount = m - 1;
                Vector4[] qBounds = softWhipQuadBoundsXZ[s];
                if (qBounds == null || qBounds.Length != quadCount)
                {
                    qBounds = new Vector4[quadCount];
                    softWhipQuadBoundsXZ[s] = qBounds;
                }

                int ownerIdx = wDef.resolvedRootBoneIndex >= 0 ? wDef.resolvedRootBoneIndex : 0;
                for (int k = 0; k < quadCount; k++)
                {
                    Vector2 a = root + snap.stripLeftLocalXZ[k];
                    Vector2 bPt = root + snap.stripLeftLocalXZ[k + 1];
                    Vector2 c = root + snap.stripRightLocalXZ[k + 1];
                    Vector2 d = root + snap.stripRightLocalXZ[k];
                    float v0 = snap.vCoords[k];
                    float v1 = snap.vCoords[k + 1];

                    float qMinX = Mathf.Min(Mathf.Min(a.x, bPt.x), Mathf.Min(c.x, d.x));
                    float qMaxX = Mathf.Max(Mathf.Max(a.x, bPt.x), Mathf.Max(c.x, d.x));
                    float qMinZ = Mathf.Min(Mathf.Min(a.y, bPt.y), Mathf.Min(c.y, d.y));
                    float qMaxZ = Mathf.Max(Mathf.Max(a.y, bPt.y), Mathf.Max(c.y, d.y));
                    qBounds[k] = new Vector4(qMinX, qMaxX, qMinZ, qMaxZ);

                    if (!wDef.contributesToHitbox && !wDef.contributesToCollision)
                    {
                        continue;
                    }
                    if (Mathf.Max(v0, v1) < mask.minV || Mathf.Min(v0, v1) >= mask.maxV)
                    {
                        continue;
                    }

                    int minCX = Mathf.FloorToInt(qMinX);
                    int maxCX = Mathf.FloorToInt(qMaxX);
                    int minCZ = Mathf.FloorToInt(qMinZ);
                    int maxCZ = Mathf.FloorToInt(qMaxZ);

                    if (mapSize.HasValue)
                    {
                        minCX = Mathf.Max(0, minCX);
                        minCZ = Mathf.Max(0, minCZ);
                        maxCX = Mathf.Min(mapSize.Value.x - 1, maxCX);
                        maxCZ = Mathf.Min(mapSize.Value.z - 1, maxCZ);
                    }
                    if (minCX > maxCX || minCZ > maxCZ)
                    {
                        continue;
                    }

                    PrecomputedTri2D tri1 = PrecomputedTri2D.Create(a, bPt, c);
                    PrecomputedTri2D tri2 = PrecomputedTri2D.Create(a, c, d);
                    if (!tri1.valid && !tri2.valid)
                    {
                        continue;
                    }

                    float step1B_X = 0.25f * tri1.dwB_dx;
                    float step1C_X = 0.25f * tri1.dwC_dx;
                    float step1B_Z = 0.25f * tri1.dwB_dz;
                    float step1C_Z = 0.25f * tri1.dwC_dz;
                    float step1BC_X = step1B_X + step1C_X;
                    float step1BC_Z = step1B_Z + step1C_Z;
                    float max1BOff = (3f * step1B_X > 0f ? 3f * step1B_X : 0f) + (3f * step1B_Z > 0f ? 3f * step1B_Z : 0f);
                    float max1COff = (3f * step1C_X > 0f ? 3f * step1C_X : 0f) + (3f * step1C_Z > 0f ? 3f * step1C_Z : 0f);
                    float min1BCOff = (3f * step1BC_X < 0f ? 3f * step1BC_X : 0f) + (3f * step1BC_Z < 0f ? 3f * step1BC_Z : 0f);

                    float step2C_X = 0.25f * tri2.dwB_dx; // In tri2(a, c, d), B-weight is wc2 and C-weight is wd2
                    float step2D_X = 0.25f * tri2.dwC_dx;
                    float step2C_Z = 0.25f * tri2.dwB_dz;
                    float step2D_Z = 0.25f * tri2.dwC_dz;
                    float step2CD_X = step2C_X + step2D_X;
                    float step2CD_Z = step2C_Z + step2D_Z;
                    float max2COff = (3f * step2C_X > 0f ? 3f * step2C_X : 0f) + (3f * step2C_Z > 0f ? 3f * step2C_Z : 0f);
                    float max2DOff = (3f * step2D_X > 0f ? 3f * step2D_X : 0f) + (3f * step2D_Z > 0f ? 3f * step2D_Z : 0f);
                    float min2CDOff = (3f * step2CD_X < 0f ? 3f * step2CD_X : 0f) + (3f * step2CD_Z < 0f ? 3f * step2CD_Z : 0f);
                    float dvSpan = v1 - v0;

                    for (int cz = minCZ; cz <= maxCZ; cz++)
                    {
                        float baseWz = cz + 0.125f;
                        for (int cx = minCX; cx <= maxCX; cx++)
                        {
                            float baseWx = cx + 0.125f;
                            float row1B = tri1.valid ? ((baseWx - tri1.ax) * tri1.dwB_dx + (baseWz - tri1.az) * tri1.dwB_dz) : 0f;
                            float row1C = tri1.valid ? ((baseWx - tri1.ax) * tri1.dwC_dx + (baseWz - tri1.az) * tri1.dwC_dz) : 0f;
                            float row2C = tri2.valid ? ((baseWx - tri2.ax) * tri2.dwB_dx + (baseWz - tri2.az) * tri2.dwB_dz) : 0f;
                            float row2D = tri2.valid ? ((baseWx - tri2.ax) * tri2.dwC_dx + (baseWz - tri2.az) * tri2.dwC_dz) : 0f;

                            bool hitTri1 = tri1.valid
                                && (row1B + max1BOff >= -0.0011f)
                                && (row1C + max1COff >= -0.0011f)
                                && (row1B + row1C + min1BCOff <= 1.0011f);
                            bool hitTri2 = tri2.valid
                                && (row2C + max2COff >= -0.0011f)
                                && (row2D + max2DOff >= -0.0011f)
                                && (row2C + row2D + min2CDOff <= 1.0011f);
                            if (!hitTri1 && !hitTri2)
                            {
                                continue;
                            }

                            ushort cellMask = 0;
                            int bitIdx = 0;

                            for (int sz = 0; sz < 4; sz++)
                            {
                                float w1B = row1B, w1C = row1C;
                                float w2C = row2C, w2D = row2D;
                                for (int sx = 0; sx < 4; sx++)
                                {
                                    bool hitSample = false;
                                    if (hitTri1)
                                    {
                                        float w1A = 1f - w1B - w1C;
                                        if (w1A >= -0.001f && w1B >= -0.001f && w1C >= -0.001f)
                                        {
                                            float u = w1C;
                                            float v = v0 + (w1B + w1C) * dvSpan;
                                            hitSample = mask.IsOpaqueUV(u, v);
                                        }
                                    }
                                    if (!hitSample && hitTri2)
                                    {
                                        float w2A = 1f - w2C - w2D;
                                        if (w2A >= -0.001f && w2C >= -0.001f && w2D >= -0.001f)
                                        {
                                            float u = w2C + w2D;
                                            float v = v0 + w2C * dvSpan;
                                            hitSample = mask.IsOpaqueUV(u, v);
                                        }
                                    }
                                    if (hitSample)
                                    {
                                        cellMask |= (ushort)(1 << bitIdx);
                                    }
                                    w1B += step1B_X;
                                    w1C += step1C_X;
                                    w2C += step2C_X;
                                    w2D += step2D_X;
                                    bitIdx++;
                                }
                                row1B += step1B_Z;
                                row1C += step1C_Z;
                                row2C += step2C_Z;
                                row2D += step2D_Z;
                            }

                            if (cellMask == 0)
                            {
                                continue;
                            }

                            int samples = BitOperations.PopCount(cellMask);
                            AccumulateCellMask(
                                cx,
                                cz,
                                cellMask,
                                samples,
                                ownerIdx,
                                wDef.layerOffset,
                                wDef.contributesToHitbox || wDef.contributesToCollision,
                                wDef.contributesToHitbox || wDef.contributesToCollision,
                                wDef.contributesToCollision);
                        }
                    }
                }
            }
        }

        private static bool TryQuadWorldToUV(
            Vector2 p,
            Vector2 a,
            Vector2 b,
            Vector2 c,
            Vector2 d,
            float v0,
            float v1,
            out float u,
            out float v)
        {
            if (TryTriangleBarycentric(p, a, b, c, out float wa, out float wb, out float wc))
            {
                u = wc;
                v = wa * v0 + (wb + wc) * v1;
                return true;
            }
            if (TryTriangleBarycentric(p, a, c, d, out float wa2, out float wc2, out float wd2))
            {
                u = wc2 + wd2;
                v = (wa2 + wd2) * v0 + wc2 * v1;
                return true;
            }
            u = 0f;
            v = 0f;
            return false;
        }

        private static bool TryTriangleBarycentric(
            Vector2 p,
            Vector2 a,
            Vector2 b,
            Vector2 c,
            out float wA,
            out float wB,
            out float wC)
        {
            Vector2 v0 = b - a;
            Vector2 v1 = c - a;
            Vector2 v2 = p - a;
            float denom = v0.x * v1.y - v1.x * v0.y;
            if (Mathf.Abs(denom) < 1e-8f)
            {
                wA = wB = wC = 0f;
                return false;
            }
            float inv = 1f / denom;
            wB = (v2.x * v1.y - v1.x * v2.y) * inv;
            wC = (v0.x * v2.y - v2.x * v0.y) * inv;
            wA = 1f - wB - wC;
            return wA >= -0.001f && wB >= -0.001f && wC >= -0.001f;
        }

        /// <summary>
        /// Finds the visible hitbox bone whose joint or covered cells are closest to an adjacent melee attacker cell.
        /// </summary>
        public int FindClosestHitBoneToCell(IntVec3 attackerCell)
        {
            int bestBone = -1;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < hitCells.Count; i++)
            {
                HitCellEntry entry = hitCells[i];
                float dSq = (entry.cell - attackerCell).LengthHorizontalSquared;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestBone = entry.dominantBoneIndex;
                }
            }

            if (bestBone < 0 && bonePoses.Length > 0)
            {
                Vector2 ac = new Vector2(attackerCell.x + 0.5f, attackerCell.z + 0.5f);
                for (int i = 0; i < bonePoses.Length; i++)
                {
                    if (bonePoses[i].visible)
                    {
                        float dSq = (bonePoses[i].jointWorldXZ - ac).sqrMagnitude;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestBone = i;
                        }
                    }
                }
            }

            return bestBone;
        }

        public bool IsCellAdjacent8WayToHitbox(IntVec3 cell)
        {
            if (hitCellSet.Count > 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (hitCellSet.Contains(cell + GenAdj.AdjacentCells[i]))
                    {
                        return true;
                    }
                }
            }
            if (pawnExclusionCellSet.Count > 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (pawnExclusionCellSet.Contains(cell + GenAdj.AdjacentCells[i]))
                    {
                        return true;
                    }
                }
            }
            if (collisionCellSet.Count > 0)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (collisionCellSet.Contains(cell + GenAdj.AdjacentCells[i]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Tier-A exterior perimeter check for small pawns engaging a live standing grounded giant:
        /// cell must lie strictly outside all rendered HitCells / PawnExclusionCells / CollisionCells
        /// while being 8-way adjacent to the giant's visual silhouette.
        /// </summary>
        public bool IsCellStrictExteriorTouch(IntVec3 cell)
        {
            if (collisionCellSet.Contains(cell) || pawnExclusionCellSet.Contains(cell) || hitCellSet.Contains(cell))
            {
                return false;
            }
            return IsCellAdjacent8WayToHitbox(cell);
        }

        /// <summary>
        /// Tier-B fringe touch check for cramped corridors: cell must lie strictly outside PawnExclusionCells
        /// and CollisionCells (so &lt; 16% peripheral outline coverage) while touching or neighboring the silhouette.
        /// </summary>
        public bool IsCellCanopySafeTouch(IntVec3 cell)
        {
            if (collisionCellSet.Contains(cell) || pawnExclusionCellSet.Contains(cell))
            {
                return false;
            }
            return hitCellSet.Contains(cell) || IsCellAdjacent8WayToHitbox(cell);
        }
    }
}
