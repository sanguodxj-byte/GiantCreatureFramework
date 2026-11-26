using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using RimWorld;
using RimWorld.Planet;
using Unity.Collections;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public sealed class GiantCollisionPathCustomizer : PathRequest.IPathGridCustomizer, IDisposable
    {
        private NativeArray<ushort> nativeOffsets;
        private readonly ushort[] managedOffsets;
        private bool dirty = true;

        public bool IsCreated => nativeOffsets.IsCreated;

        public GiantCollisionPathCustomizer(int numGridCells)
        {
            managedOffsets = new ushort[numGridCells];
            nativeOffsets = new NativeArray<ushort>(numGridCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        public ushort[] ManagedBuffer => managedOffsets;

        public void MarkDirty()
        {
            dirty = true;
        }

        /// <summary>
        /// Must ONLY be called when no PathGridJob is executing on worker threads
        /// (i.e. immediately after PathFinder.ForceCompleteScheduledJobs or before map teardown).
        /// </summary>
        public bool SyncIfDirty()
        {
            if (!dirty || !nativeOffsets.IsCreated)
            {
                return false;
            }
            NativeArray<ushort>.Copy(managedOffsets, nativeOffsets);
            dirty = false;
            return true;
        }

        public NativeArray<ushort> GetOffsetGrid()
        {
            return nativeOffsets;
        }

        public void Dispose()
        {
            if (nativeOffsets.IsCreated)
            {
                nativeOffsets.Dispose();
            }
        }
    }

    public readonly struct GiantBreachCustomizerKey : IEquatable<GiantBreachCustomizerKey>
    {
        public readonly int clearanceRadius;
        public readonly int factionLoadId;
        public readonly bool allowBuildings;
        public readonly bool allowNaturalRock;
        public readonly bool allowTerrain;
        public readonly bool protectAllied;

        public GiantBreachCustomizerKey(
            int clearanceRadius,
            int factionLoadId,
            bool allowBuildings,
            bool allowNaturalRock,
            bool allowTerrain,
            bool protectAllied)
        {
            this.clearanceRadius = clearanceRadius;
            this.factionLoadId = factionLoadId;
            this.allowBuildings = allowBuildings;
            this.allowNaturalRock = allowNaturalRock;
            this.allowTerrain = allowTerrain;
            this.protectAllied = protectAllied;
        }

        public bool Equals(GiantBreachCustomizerKey other)
        {
            return clearanceRadius == other.clearanceRadius
                && factionLoadId == other.factionLoadId
                && allowBuildings == other.allowBuildings
                && allowNaturalRock == other.allowNaturalRock
                && allowTerrain == other.allowTerrain
                && protectAllied == other.protectAllied;
        }

        public override bool Equals(object obj)
        {
            return obj is GiantBreachCustomizerKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = clearanceRadius;
                hash = (hash * 397) ^ factionLoadId;
                hash = (hash * 397) ^ (allowBuildings ? 1 : 0);
                hash = (hash * 397) ^ (allowNaturalRock ? 2 : 0);
                hash = (hash * 397) ^ (allowTerrain ? 4 : 0);
                hash = (hash * 397) ^ (protectAllied ? 8 : 0);
                return hash;
            }
        }
    }

    /// <summary>
    /// Burst path customizer that provides BOTH wall-clearance penalty offsets
    /// (<see cref="GetOffsetGrid"/>) and a per-faction direct cost grid
    /// (<see cref="DirectCostGrid"/>) making crushable walls, doors, non-thick-roof rocks,
    /// and destructible impassable terrain passable while keeping thick-roof cells impassable.
    /// </summary>
    public sealed class GiantBreachPathCustomizer : PathRequest.IPathGridCustomizer, IDisposable
    {
        private NativeArray<ushort> nativeOffsets;
        private NativeArray<int> nativeDirectCosts;
        private readonly ushort[] managedOffsets;
        private readonly int[] managedDirectCosts;
        private bool dirty = true;

        public GiantBreachPathCustomizer(int numGridCells)
        {
            managedOffsets = new ushort[numGridCells];
            managedDirectCosts = new int[numGridCells];
            nativeOffsets = new NativeArray<ushort>(numGridCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            nativeDirectCosts = new NativeArray<int>(numGridCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        }

        public bool IsCreated => nativeOffsets.IsCreated && nativeDirectCosts.IsCreated;
        public ushort[] ManagedOffsets => managedOffsets;
        public int[] ManagedDirectCosts => managedDirectCosts;
        public NativeArray<int>.ReadOnly DirectCostGrid => nativeDirectCosts.AsReadOnly();

        public void MarkDirty()
        {
            dirty = true;
        }

        public bool SyncIfDirty()
        {
            if (!dirty || !IsCreated)
            {
                return false;
            }
            NativeArray<ushort>.Copy(managedOffsets, nativeOffsets);
            NativeArray<int>.Copy(managedDirectCosts, nativeDirectCosts);
            dirty = false;
            return true;
        }

        public NativeArray<ushort> GetOffsetGrid()
        {
            return nativeOffsets;
        }

        public void Dispose()
        {
            if (nativeOffsets.IsCreated)
            {
                nativeOffsets.Dispose();
            }
            if (nativeDirectCosts.IsCreated)
            {
                nativeDirectCosts.Dispose();
            }
        }
    }

    public sealed class GiantSnapshotEntry
    {
        public readonly int pawnId;
        public readonly IntVec3 anchorCell;
        public readonly CellRect collisionBounds;
        public readonly IntVec3[] localOffsets;
        public readonly IntVec3[] collisionCells;
        public readonly ushort avoidanceCost;
        public readonly int maxExtent;

        public GiantSnapshotEntry(
            int pawnId,
            IntVec3 anchorCell,
            CellRect collisionBounds,
            IntVec3[] localOffsets,
            IntVec3[] collisionCells,
            ushort avoidanceCost,
            int maxExtent)
        {
            this.pawnId = pawnId;
            this.anchorCell = anchorCell;
            this.collisionBounds = collisionBounds;
            this.localOffsets = localOffsets;
            this.collisionCells = collisionCells;
            this.avoidanceCost = avoidanceCost;
            this.maxExtent = maxExtent;
        }
    }

    /// <summary>
    /// Immutable point-in-time snapshot of all live giant collision hulls for lock-free
    /// worker-thread reads inside PathGridDoorsBlockedJob.
    /// </summary>
    public sealed class GiantPathAvoidanceSnapshot
    {
        public readonly int mapSizeX;
        public readonly int mapSizeZ;
        public readonly int numCells;
        public readonly GiantSnapshotEntry[] giants;
        public readonly Dictionary<int, GiantSnapshotEntry> entryByPawnId;

        public GiantPathAvoidanceSnapshot(int mapSizeX, int mapSizeZ, GiantSnapshotEntry[] giants)
        {
            this.mapSizeX = mapSizeX;
            this.mapSizeZ = mapSizeZ;
            this.numCells = mapSizeX * mapSizeZ;
            this.giants = giants ?? Array.Empty<GiantSnapshotEntry>();
            entryByPawnId = new Dictionary<int, GiantSnapshotEntry>(this.giants.Length);
            for (int i = 0; i < this.giants.Length; i++)
            {
                entryByPawnId[this.giants[i].pawnId] = this.giants[i];
            }
        }
    }

    /// <summary>
    /// Pure geometric and cost-stamping helpers for multi-cell giant-to-giant hull collision.
    /// </summary>
    public static class GiantHullCollisionUtility
    {
        public const int BaseFirstOverlapPenalty = 1200;
        public const int PerCellOverlapPenalty = 220;
        public const int MaxGiantAvoidancePenalty = 8500;
        public const int MaxCombinedProviderCost = 9500;
        public const ushort ImpassableProviderCost = 10000;

        [ThreadStatic]
        private static int[] overlapStampScratch;

        [ThreadStatic]
        private static int[] overlapCountScratch;

        [ThreadStatic]
        private static int overlapCurrentStamp;

        [ThreadStatic]
        private static List<int> touchedIndicesScratch;

        public static bool ShouldBlockOverlapMove(int candidateOverlap, int currentOverlap)
        {
            if (candidateOverlap <= 0)
            {
                return false;
            }
            if (currentOverlap <= 0)
            {
                return true;
            }
            return candidateOverlap >= currentOverlap;
        }

        public static int CountTranslatedHullOverlap(
            IntVec3 anchorCell,
            IReadOnlyList<IntVec3> moverOffsets,
            IReadOnlyCollection<IntVec3> blockerHullCells)
        {
            if (blockerHullCells == null || blockerHullCells.Count == 0)
            {
                return 0;
            }

            if (moverOffsets == null || moverOffsets.Count == 0)
            {
                return ContainsCell(blockerHullCells, anchorCell) ? 1 : 0;
            }

            int overlap = 0;
            for (int i = 0; i < moverOffsets.Count; i++)
            {
                if (ContainsCell(blockerHullCells, anchorCell + moverOffsets[i]))
                {
                    overlap++;
                }
            }
            return overlap;
        }

        private static bool ContainsCell(IReadOnlyCollection<IntVec3> cells, IntVec3 target)
        {
            if (cells is HashSet<IntVec3> set)
            {
                return set.Contains(target);
            }
            foreach (IntVec3 c in cells)
            {
                if (c == target)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool ShouldBlockStepAgainstHull(
            IntVec3 currentAnchor,
            IntVec3 candidateAnchor,
            IReadOnlyList<IntVec3> moverOffsets,
            IReadOnlyCollection<IntVec3> blockerHullCells,
            out int candidateOverlap,
            out int currentOverlap)
        {
            candidateOverlap = CountTranslatedHullOverlap(candidateAnchor, moverOffsets, blockerHullCells);
            if (candidateOverlap == 0)
            {
                currentOverlap = 0;
                return false;
            }

            currentOverlap = CountTranslatedHullOverlap(currentAnchor, moverOffsets, blockerHullCells);
            return ShouldBlockOverlapMove(candidateOverlap, currentOverlap);
        }

        public static ushort MergeAvoidanceCost(ushort existingCost, int overlapCount)
        {
            if (overlapCount <= 0 || existingCost >= ImpassableProviderCost)
            {
                return existingCost;
            }
            int penalty = Math.Min(MaxGiantAvoidancePenalty, BaseFirstOverlapPenalty + overlapCount * PerCellOverlapPenalty);
            int combined = Math.Min(MaxCombinedProviderCost, existingCost + penalty);
            return (ushort)Math.Max(existingCost, combined);
        }

        public static void AccumulateOverlapCounts(
            int mapSizeX,
            int mapSizeZ,
            int numCells,
            IReadOnlyList<IntVec3> requesterOffsets,
            IReadOnlyList<GiantSnapshotEntry> giants,
            int requesterPawnId,
            out List<int> touchedIndices,
            out int[] countsByIndex)
        {
            if (overlapStampScratch == null || overlapStampScratch.Length < numCells)
            {
                overlapStampScratch = new int[numCells];
                overlapCountScratch = new int[numCells];
                overlapCurrentStamp = 0;
            }
            if (touchedIndicesScratch == null)
            {
                touchedIndicesScratch = new List<int>(512);
            }
            else
            {
                touchedIndicesScratch.Clear();
            }

            overlapCurrentStamp++;
            if (overlapCurrentStamp == int.MaxValue)
            {
                Array.Clear(overlapStampScratch, 0, overlapStampScratch.Length);
                overlapCurrentStamp = 1;
            }
            int stamp = overlapCurrentStamp;

            int offsetCount = requesterOffsets != null && requesterOffsets.Count > 0 ? requesterOffsets.Count : 0;
            for (int g = 0; g < giants.Count; g++)
            {
                GiantSnapshotEntry other = giants[g];
                if (other == null || other.pawnId == requesterPawnId || other.collisionCells == null)
                {
                    continue;
                }

                IntVec3[] otherCells = other.collisionCells;
                if (offsetCount == 0)
                {
                    for (int i = 0; i < otherCells.Length; i++)
                    {
                        IntVec3 c = otherCells[i];
                        if ((uint)c.x >= (uint)mapSizeX || (uint)c.z >= (uint)mapSizeZ)
                        {
                            continue;
                        }
                        int idx = c.z * mapSizeX + c.x;
                        if (overlapStampScratch[idx] != stamp)
                        {
                            overlapStampScratch[idx] = stamp;
                            overlapCountScratch[idx] = 1;
                            touchedIndicesScratch.Add(idx);
                        }
                        else
                        {
                            overlapCountScratch[idx]++;
                        }
                    }
                    continue;
                }

                for (int i = 0; i < otherCells.Length; i++)
                {
                    IntVec3 c = otherCells[i];
                    for (int o = 0; o < offsetCount; o++)
                    {
                        IntVec3 off = requesterOffsets[o];
                        int ax = c.x - off.x;
                        int az = c.z - off.z;
                        if ((uint)ax >= (uint)mapSizeX || (uint)az >= (uint)mapSizeZ)
                        {
                            continue;
                        }
                        int idx = az * mapSizeX + ax;
                        if (overlapStampScratch[idx] != stamp)
                        {
                            overlapStampScratch[idx] = stamp;
                            overlapCountScratch[idx] = 1;
                            touchedIndicesScratch.Add(idx);
                        }
                        else
                        {
                            overlapCountScratch[idx]++;
                        }
                    }
                }
            }

            touchedIndices = touchedIndicesScratch;
            countsByIndex = overlapCountScratch;
        }

        public static void StampAvoidanceCosts(GiantPathAvoidanceSnapshot snapshot, int requesterPawnId, ushort[] providerCost)
        {
            if (snapshot == null || providerCost == null || snapshot.giants.Length < 2)
            {
                return;
            }
            if (!snapshot.entryByPawnId.TryGetValue(requesterPawnId, out GiantSnapshotEntry selfEntry))
            {
                return;
            }
            AccumulateOverlapCounts(
                snapshot.mapSizeX,
                snapshot.mapSizeZ,
                snapshot.numCells,
                selfEntry.localOffsets,
                snapshot.giants,
                requesterPawnId,
                out List<int> touched,
                out int[] counts);
            for (int i = 0; i < touched.Count; i++)
            {
                int idx = touched[i];
                if ((uint)idx < (uint)providerCost.Length)
                {
                    providerCost[idx] = MergeAvoidanceCost(providerCost[idx], counts[idx]);
                }
            }
        }
    }

    public sealed class GiantFootprintMapComponent : MapComponent, IDisposable
    {
        private static readonly object ByMapIndexLock = new object();
        private static GiantFootprintMapComponent[] byMapIndex = new GiantFootprintMapComponent[16];
        private static bool liveFpsSamplingActive;
        private static float liveFpsStartRealtime;
        private static int liveFpsStartTick;
        private static readonly List<float> liveFrameDeltaSeconds = new List<float>(4096);
        private bool disposed;

        public const ushort MaxOtherPawnGiantCollisionCost = 4500;

        private readonly struct BreachReachCacheKey : IEquatable<BreachReachCacheKey>
        {
            public readonly GiantBreachCustomizerKey customizerKey;
            public readonly int startDistrictId;
            public readonly int destDistrictId;
            public readonly bool exactOnCell;

            public BreachReachCacheKey(
                GiantBreachCustomizerKey customizerKey,
                int startDistrictId,
                int destDistrictId,
                bool exactOnCell)
            {
                this.customizerKey = customizerKey;
                this.startDistrictId = startDistrictId;
                this.destDistrictId = destDistrictId;
                this.exactOnCell = exactOnCell;
            }

            public bool Equals(BreachReachCacheKey other)
            {
                return startDistrictId == other.startDistrictId
                    && destDistrictId == other.destDistrictId
                    && exactOnCell == other.exactOnCell
                    && customizerKey.Equals(other.customizerKey);
            }

            public override bool Equals(object obj)
            {
                return obj is BreachReachCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = customizerKey.GetHashCode();
                    hash = (hash * 397) ^ startDistrictId;
                    hash = (hash * 397) ^ destDistrictId;
                    hash = (hash * 397) ^ (exactOnCell ? 1 : 0);
                    return hash;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static GiantFootprintMapComponent GetFast(Map map)
        {
            if (map == null || Current.Game?.Maps == null)
            {
                return null;
            }
            int idx = map.Index;
            GiantFootprintMapComponent[] arr = byMapIndex;
            if ((uint)idx < (uint)arr.Length)
            {
                GiantFootprintMapComponent cached = arr[idx];
                if (cached != null && cached.map == map && !cached.disposed)
                {
                    return cached;
                }
            }
            return GetAndCacheSlow(map, idx);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GiantFootprintMapComponent GetAndCacheSlow(Map map, int idx)
        {
            GiantFootprintMapComponent comp = map.GetComponent<GiantFootprintMapComponent>();
            if (comp == null || comp.disposed)
            {
                return null;
            }
            if (idx >= 0)
            {
                CacheByMapIndex(idx, comp);
            }
            return comp;
        }

        private static void CacheByMapIndex(int idx, GiantFootprintMapComponent comp)
        {
            if (idx < 0 || idx > 256)
            {
                return;
            }
            lock (ByMapIndexLock)
            {
                if (idx >= byMapIndex.Length)
                {
                    int newLen = Math.Max(idx + 1, byMapIndex.Length * 2);
                    var resized = new GiantFootprintMapComponent[newLen];
                    Array.Copy(byMapIndex, resized, byMapIndex.Length);
                    resized[idx] = comp;
                    byMapIndex = resized;
                    return;
                }
                byMapIndex[idx] = comp;
            }
        }

        public static void DisposeAllStaticMapCaches()
        {
            GiantFootprintMapComponent[] snapshot;
            lock (ByMapIndexLock)
            {
                snapshot = (GiantFootprintMapComponent[])byMapIndex.Clone();
                Array.Clear(byMapIndex, 0, byMapIndex.Length);
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i]?.Dispose();
                }
                catch
                {
                    // Never block map/game teardown
                }
            }
        }

        public static void StartLiveFpsTelemetry()
        {
            liveFrameDeltaSeconds.Clear();
            liveFpsStartRealtime = Time.realtimeSinceStartup;
            liveFpsStartTick = Current.Game?.tickManager?.TicksGame ?? 0;
            liveFpsSamplingActive = true;
        }

        public static string StopLiveFpsTelemetry(Map map)
        {
            liveFpsSamplingActive = false;
            float elapsed = Mathf.Max(0.0001f, Time.realtimeSinceStartup - liveFpsStartRealtime);
            int curTick = Current.Game?.tickManager?.TicksGame ?? liveFpsStartTick;
            int ticksAdv = Math.Max(0, curTick - liveFpsStartTick);
            int frameCount = liveFrameDeltaSeconds.Count;

            double avgFps = frameCount / elapsed;
            double avgFrameMs = frameCount > 0 ? (elapsed * 1000.0) / frameCount : 0.0;
            double medianFps = avgFps;
            double p5LowFps = avgFps;
            double p1LowFps = avgFps;
            double minFps = avgFps;

            if (frameCount >= 4)
            {
                var sorted = liveFrameDeltaSeconds.ToArray();
                Array.Sort(sorted);
                // Ignore index 0 if start action caused a single-frame spike? Keep raw AND drop only frame 0 when frameCount > 20
                int startIdx = frameCount > 20 ? 1 : 0;
                int len = frameCount - startIdx;
                float p50Dt = Mathf.Max(0.0001f, sorted[startIdx + (int)((len - 1) * 0.50)]);
                float p95Dt = Mathf.Max(0.0001f, sorted[startIdx + (int)((len - 1) * 0.95)]);
                float p99Dt = Mathf.Max(0.0001f, sorted[startIdx + (int)((len - 1) * 0.99)]);
                float maxDt = Mathf.Max(0.0001f, sorted[frameCount - 1]);
                medianFps = 1.0 / p50Dt;
                p5LowFps = 1.0 / p95Dt;
                p1LowFps = 1.0 / p99Dt;
                minFps = 1.0 / maxDt;
            }
            liveFrameDeltaSeconds.Clear();

            GiantFootprintMapComponent mc = GetFast(map);
            int liveGiants = mc?.giantsList.Count ?? 0;
            int corpseGiants = mc?.corpseGiantsList.Count ?? 0;
            int spawnedPawns = map?.mapPawns?.AllPawnsSpawnedCount ?? 0;
            double tps = ticksAdv / elapsed;
            var ic = CultureInfo.InvariantCulture;
            return string.Format(
                ic,
                "{{\"wallSec\":{0:F3},\"frames\":{1},\"avgFps\":{2:F2},\"medianFps\":{3:F2},\"p5LowFps\":{4:F2},\"p1LowFps\":{5:F2},\"minFps\":{6:F2},\"avgFrameMs\":{7:F3},\"ticksAdvanced\":{8},\"tps\":{9:F2},\"activeGiants\":{10},\"corpseGiants\":{11},\"spawnedPawns\":{12}}}",
                elapsed, frameCount, avgFps, medianFps, p5LowFps, p1LowFps, minFps, avgFrameMs, ticksAdv, tps, liveGiants, corpseGiants, spawnedPawns);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RecordLiveFrameDelta(float unscaledDt)
        {
            if (unscaledDt > 0.00005f && unscaledDt < 5.0f && liveFrameDeltaSeconds.Count < 60000)
            {
                liveFrameDeltaSeconds.Add(unscaledDt);
            }
        }

        private readonly HashSet<CompGiantRig> activeGiants = new HashSet<CompGiantRig>();
        private readonly List<CompGiantRig> giantsList = new List<CompGiantRig>();
        private readonly HashSet<CompGiantRig> activeCorpseGiants = new HashSet<CompGiantRig>();
        private readonly List<CompGiantRig> corpseGiantsList = new List<CompGiantRig>();
        private readonly HashSet<CompGiantCockpit> activeOccupiedCockpits = new HashSet<CompGiantCockpit>();
        private readonly List<CompGiantCockpit> occupiedCockpitsList = new List<CompGiantCockpit>();
        private readonly GiantBossHealthBarHUD bossHealthBarHud = new GiantBossHealthBarHUD();
        public GiantBossHealthBarHUD BossHealthBarHud => bossHealthBarHud;

        // Fast per-cell spatial lookups
        private List<CompGiantRig>[] hitGiantsByCell;
        private List<CompGiantRig>[] corpseGiantsByCell;
        private List<CompGiantRig>[] pawnExclusionGiantsByCell;
        private List<CompGiantRig>[] collisionGiantsByCell;
        private CompGiantRig[] collisionOwnerByCell;
        private volatile GiantPathAvoidanceSnapshot latestPathSnapshot;
        private bool pathSnapshotDirty = true;
        private readonly HashSet<CompGiantRig> overlapMovedThisTick = new HashSet<CompGiantRig>();
        private readonly Dictionary<int, int> lastGiantFlankRepathTick = new Dictionary<int, int>();

        private GiantCollisionPathCustomizer otherPawnsCustomizer;
        private NativeBitArray emptyBitGrid;
        private NativeArray<ushort> emptyPerceptualCostGrid;
        private readonly Dictionary<int, GiantCollisionPathCustomizer> clearanceCustomizersByRadius = new();
        private readonly Dictionary<GiantBreachCustomizerKey, GiantBreachPathCustomizer> breachCustomizers = new();
        private readonly Dictionary<GiantAerialCustomizerKey, GiantAerialPathCustomizer> aerialCustomizers = new();
        private readonly Dictionary<GiantAerialPathCustomizer, GiantAerialCustomizerKey> aerialKeysByCustomizer = new();
        private readonly Dictionary<GiantAquaticCustomizerKey, GiantAquaticPathCustomizer> aquaticCustomizers = new();
        private readonly Dictionary<GiantAquaticPathCustomizer, GiantAquaticCustomizerKey> aquaticKeysByCustomizer = new();
        private readonly Dictionary<BreachReachCacheKey, bool> cachedDistrictBreachReach = new();
        private readonly Dictionary<CompGiantRig, IntVec3> corpseLastPosition = new();
        private readonly Queue<int> breachBfsQueue = new Queue<int>(2048);
        private int[] breachBfsVisitedStamp;
        private int breachBfsCurrentStamp;
        private bool clearanceDirty = true;
        private bool breachDirty = true;
        private bool aerialDirty = true;
        private bool aquaticDirty = true;

        public static bool GlobalDebugDrawCollisionCells = false;
        public static bool GlobalDebugDrawHitCells = false;
        public static bool GlobalDebugDrawFootprints = false;
        public static bool GlobalDebugDrawSkeleton = false;

        public IReadOnlyList<CompGiantRig> ActiveGiants => giantsList;
        public IReadOnlyList<CompGiantRig> ActiveCorpseGiants => corpseGiantsList;
        public IReadOnlyList<CompGiantCockpit> OccupiedPlayerCockpits => occupiedCockpitsList;
        public bool HasAnyGiants => giantsList.Count > 0;
        public bool HasAnyCorpseGiants => corpseGiantsList.Count > 0;
        public bool HasOccupiedPlayerCockpits => occupiedCockpitsList.Count > 0;

        public void SyncCockpitOccupancy(CompGiantCockpit cockpit)
        {
            if (cockpit == null)
            {
                return;
            }
            Pawn host = cockpit.HostPawn;
            bool shouldTrack = host != null && host.Spawned && host.Map == map && !host.Dead && cockpit.CrewCount > 0;
            if (shouldTrack)
            {
                if (activeOccupiedCockpits.Add(cockpit))
                {
                    occupiedCockpitsList.Add(cockpit);
                }
            }
            else if (activeOccupiedCockpits.Remove(cockpit))
            {
                occupiedCockpitsList.Remove(cockpit);
            }
        }

        public void UnregisterCockpit(CompGiantCockpit cockpit)
        {
            if (cockpit != null && activeOccupiedCockpits.Remove(cockpit))
            {
                occupiedCockpitsList.Remove(cockpit);
            }
        }

        public int CountBoardedFreeColonists()
        {
            if (occupiedCockpitsList.Count == 0)
            {
                return 0;
            }
            int total = 0;
            for (int i = occupiedCockpitsList.Count - 1; i >= 0; i--)
            {
                CompGiantCockpit cp = occupiedCockpitsList[i];
                if (cp?.HostPawn == null || !cp.HostPawn.Spawned || cp.HostPawn.Map != map || cp.CrewCount == 0)
                {
                    if (cp != null)
                    {
                        activeOccupiedCockpits.Remove(cp);
                    }
                    occupiedCockpitsList.RemoveAt(i);
                    continue;
                }
                total += cp.CountBoardedFreeColonists();
            }
            return total;
        }

        public bool HasAnyBoardedColonyPawnBlockingRemoval()
        {
            for (int i = occupiedCockpitsList.Count - 1; i >= 0; i--)
            {
                CompGiantCockpit cp = occupiedCockpitsList[i];
                if (cp?.HostPawn == null || !cp.HostPawn.Spawned || cp.HostPawn.Map != map || cp.CrewCount == 0)
                {
                    if (cp != null)
                    {
                        activeOccupiedCockpits.Remove(cp);
                    }
                    occupiedCockpitsList.RemoveAt(i);
                    continue;
                }
                if (cp.HasAnyValidColonyPawnBlockingRemoval())
                {
                    return true;
                }
            }
            return false;
        }

        public GiantFootprintMapComponent(Map map) : base(map)
        {
            if (map != null && map.Index >= 0)
            {
                CacheByMapIndex(map.Index, this);
            }
            if (map?.events != null)
            {
                map.events.BuildingSpawned += OnBuildingOrTerrainChanged;
                map.events.BuildingDespawned += OnBuildingOrTerrainChanged;
                map.events.TerrainChanged += OnCellChanged;
                map.events.RoofChanged += OnCellChanged;
                map.events.DoorOpened += OnDoorStateChanged;
                map.events.DoorClosed += OnDoorStateChanged;
            }
        }

        private void OnDoorStateChanged(Building_Door door)
        {
            clearanceDirty = true;
            breachDirty = true;
            aerialDirty = true;
            aquaticDirty = true;
            cachedDistrictBreachReach.Clear();
        }

        private void OnBuildingOrTerrainChanged(Building b)
        {
            // Skip non-blocking furniture/lamps/conduits that cannot affect wall clearance, breach, or aerial cost grids.
            if (b != null && b.def != null
                && b.def.passability == Traversability.Standable
                && b.def.pathCost == 0
                && !(b is Building_Door))
            {
                return;
            }
            clearanceDirty = true;
            breachDirty = true;
            aerialDirty = true;
            aquaticDirty = true;
            cachedDistrictBreachReach.Clear();
        }

        private void OnCellChanged(IntVec3 c)
        {
            clearanceDirty = true;
            breachDirty = true;
            aerialDirty = true;
            aquaticDirty = true;
            cachedDistrictBreachReach.Clear();
        }

        public void MarkAllCustomizersDirty()
        {
            clearanceDirty = true;
            breachDirty = true;
            aerialDirty = true;
            aquaticDirty = true;
            cachedDistrictBreachReach.Clear();
            otherPawnsCustomizer?.MarkDirty();
            foreach (var kv in clearanceCustomizersByRadius)
            {
                kv.Value?.MarkDirty();
            }
            foreach (var kv in breachCustomizers)
            {
                kv.Value?.MarkDirty();
            }
            foreach (var kv in aerialCustomizers)
            {
                kv.Value?.MarkDirty();
            }
            foreach (var kv in aquaticCustomizers)
            {
                kv.Value?.MarkDirty();
            }
        }

        public static void MarkActiveMapsPathGridsDirty()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
            {
                return;
            }

            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                GetFast(maps[i])?.MarkAllCustomizersDirty();
            }
        }

        private void EnsureArraysInitialized()
        {
            if (disposed || map == null || hitGiantsByCell != null)
            {
                return;
            }
            int numCells = map.cellIndices.NumGridCells;
            hitGiantsByCell = new List<CompGiantRig>[numCells];
            corpseGiantsByCell = new List<CompGiantRig>[numCells];
            pawnExclusionGiantsByCell = new List<CompGiantRig>[numCells];
            collisionGiantsByCell = new List<CompGiantRig>[numCells];
            collisionOwnerByCell = new CompGiantRig[numCells];
            otherPawnsCustomizer = new GiantCollisionPathCustomizer(numCells);
            if (!emptyBitGrid.IsCreated)
            {
                emptyBitGrid = new NativeBitArray(numCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            if (!emptyPerceptualCostGrid.IsCreated)
            {
                emptyPerceptualCostGrid = new NativeArray<ushort>(numCells, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }
            touchedHitCellIndices.Clear();
            touchedPawnExclusionCellIndices.Clear();
            touchedCollisionCellIndices.Clear();
            touchedCorpseCellIndices.Clear();
            spatialRegistrations.Clear();
            activePawnExclusionCellCount = 0;
            activeCollisionCellCount = 0;
            unionHitBounds = CellRect.Empty;
            lastCollisionGridHash = ulong.MaxValue;
        }

        public bool TryGetEmptyBitGrid(out NativeBitArray.ReadOnly bits)
        {
            if (disposed || !emptyBitGrid.IsCreated)
            {
                bits = default;
                return false;
            }
            bits = emptyBitGrid.AsReadOnly();
            return true;
        }

        public bool TryGetEmptyPerceptualCostGrid(out NativeArray<ushort>.ReadOnly costs)
        {
            if (disposed || !emptyPerceptualCostGrid.IsCreated)
            {
                costs = default;
                return false;
            }
            costs = emptyPerceptualCostGrid.AsReadOnly();
            return true;
        }

        public bool TryGetAerialCustomizerKey(GiantAerialPathCustomizer customizer, out GiantAerialCustomizerKey key)
        {
            if (customizer != null && aerialKeysByCustomizer.TryGetValue(customizer, out key))
            {
                return true;
            }
            key = default;
            return false;
        }

        public bool TryGetAquaticCustomizerKey(GiantAquaticPathCustomizer customizer, out GiantAquaticCustomizerKey key)
        {
            if (customizer != null && aquaticKeysByCustomizer.TryGetValue(customizer, out key))
            {
                return true;
            }
            key = default;
            return false;
        }

        private sealed class GiantSpatialRegistration
        {
            public readonly List<int> hitIndices = new List<int>(128);
            public readonly List<int> pawnExclIndices = new List<int>(128);
            public readonly List<int> colIndices = new List<int>(64);
            public ushort lastPawnExclCost;
            public ushort lastColCost;
        }

        private readonly List<int> touchedHitCellIndices = new List<int>(512);
        private readonly List<int> touchedPawnExclusionCellIndices = new List<int>(512);
        private readonly List<int> touchedCollisionCellIndices = new List<int>(256);
        private readonly List<int> touchedCorpseCellIndices = new List<int>(256);
        private readonly Dictionary<CompGiantRig, GiantSpatialRegistration> spatialRegistrations = new Dictionary<CompGiantRig, GiantSpatialRegistration>();
        private readonly HashSet<Pawn> occludedSilhouetteDrawnScratch = new HashSet<Pawn>(32);
        private int activePawnExclusionCellCount;
        private int activeCollisionCellCount;
        private CellRect unionHitBounds = CellRect.Empty;
        private int subCellBudgetTick = -1;
        private int subCellBudgetCount;
        private ulong lastCollisionGridHash = ulong.MaxValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryConsumeSubCellFootprintBudget(int ticksGame, int maxPerTick = 2)
        {
            if (subCellBudgetTick != ticksGame)
            {
                subCellBudgetTick = ticksGame;
                subCellBudgetCount = 1;
                return true;
            }
            if (subCellBudgetCount < maxPerTick)
            {
                subCellBudgetCount++;
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MightSegmentIntersectAnyGiantHitbox(Vector3 a, Vector3 b)
        {
            if (unionHitBounds.IsEmpty)
            {
                return false;
            }
            int minX = (int)Mathf.Floor(a.x < b.x ? a.x : b.x) - 1;
            int maxX = (int)Mathf.Floor(a.x > b.x ? a.x : b.x) + 1;
            int minZ = (int)Mathf.Floor(a.z < b.z ? a.z : b.z) - 1;
            int maxZ = (int)Mathf.Floor(a.z > b.z ? a.z : b.z) + 1;
            return maxX >= unionHitBounds.minX
                && minX <= unionHitBounds.maxX
                && maxZ >= unionHitBounds.minZ
                && minZ <= unionHitBounds.maxZ;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (map != null && map.Index >= 0)
            {
                CacheByMapIndex(map.Index, this);
            }
            if (map?.listerThings == null)
            {
                return;
            }
            List<Thing> corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
            if (corpses == null)
            {
                return;
            }
            for (int i = 0; i < corpses.Count; i++)
            {
                if (corpses[i] is Corpse corpse && corpse.Spawned && corpse.InnerPawn != null)
                {
                    CompGiantRig rigComp = GiantFastLookup.TryGetRig(corpse.InnerPawn);
                    if (rigComp != null && rigComp.RigDef != null)
                    {
                        rigComp.RestoreCorpseStateAfterLoad(map);
                        RegisterCorpseFootprint(corpse.InnerPawn, rigComp);
                    }
                }
            }
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            Dispose();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;

            Map ownerMap = map;
            if (ownerMap != null)
            {
                try
                {
                    // Ensure no Burst PathGridJob is still reading our NativeArrays before disposal.
                    // Because disposed is already true, Harmony postfixes on ForceCompleteScheduledJobs
                    // will see GetFast() == null and SyncNativeCustomizersSafe() == false.
                    if (ownerMap.pathFinder != null)
                    {
                        HarmonyLib.AccessTools.Method(ownerMap.pathFinder.GetType(), "ForceCompleteScheduledJobs")
                            ?.Invoke(ownerMap.pathFinder, null);
                    }
                }
                catch
                {
                    // Map may already be partially torn down
                }
            }

            lock (ByMapIndexLock)
            {
                for (int i = 0; i < byMapIndex.Length; i++)
                {
                    if (ReferenceEquals(byMapIndex[i], this))
                    {
                        byMapIndex[i] = null;
                    }
                }
            }

            bossHealthBarHud.Clear();
            activeOccupiedCockpits.Clear();
            occupiedCockpitsList.Clear();
            activeGiants.Clear();
            giantsList.Clear();
            activeCorpseGiants.Clear();
            corpseGiantsList.Clear();
            corpseLastPosition.Clear();
            spatialRegistrations.Clear();
            touchedHitCellIndices.Clear();
            touchedPawnExclusionCellIndices.Clear();
            touchedCollisionCellIndices.Clear();
            touchedCorpseCellIndices.Clear();
            occludedSilhouetteDrawnScratch.Clear();
            cachedDistrictBreachReach.Clear();
            latestPathSnapshot = null;
            hitGiantsByCell = null;
            pawnExclusionGiantsByCell = null;
            collisionGiantsByCell = null;
            collisionOwnerByCell = null;
            corpseGiantsByCell = null;
            breachBfsVisitedStamp = null;

            GiantVfxSystem.ClearForMap(ownerMap);
            if (ownerMap?.events != null)
            {
                ownerMap.events.BuildingSpawned -= OnBuildingOrTerrainChanged;
                ownerMap.events.BuildingDespawned -= OnBuildingOrTerrainChanged;
                ownerMap.events.TerrainChanged -= OnCellChanged;
                ownerMap.events.RoofChanged -= OnCellChanged;
                ownerMap.events.DoorOpened -= OnDoorStateChanged;
                ownerMap.events.DoorClosed -= OnDoorStateChanged;
            }
            if (emptyBitGrid.IsCreated)
            {
                emptyBitGrid.Dispose();
            }
            if (emptyPerceptualCostGrid.IsCreated)
            {
                emptyPerceptualCostGrid.Dispose();
            }
            otherPawnsCustomizer?.Dispose();
            otherPawnsCustomizer = null;
            foreach (var kv in clearanceCustomizersByRadius)
            {
                kv.Value?.Dispose();
            }
            clearanceCustomizersByRadius.Clear();
            foreach (var kv in breachCustomizers)
            {
                kv.Value?.Dispose();
            }
            breachCustomizers.Clear();
            foreach (var kv in aerialCustomizers)
            {
                kv.Value?.Dispose();
            }
            aerialCustomizers.Clear();
            aerialKeysByCustomizer.Clear();
            foreach (var kv in aquaticCustomizers)
            {
                kv.Value?.Dispose();
            }
            aquaticCustomizers.Clear();
            aquaticKeysByCustomizer.Clear();
        }

        public void RegisterGiant(CompGiantRig comp)
        {
            if (disposed || comp == null)
            {
                return;
            }
            if (activeCorpseGiants.Remove(comp))
            {
                corpseGiantsList.Remove(comp);
                corpseLastPosition.Remove(comp);
                RebuildCorpseSpatialGrid();
            }
            if (!activeGiants.Add(comp))
            {
                return;
            }
            giantsList.Add(comp);
            bossHealthBarHud.NotifyGiantRegistered(comp);
            RefreshMapConnectivityForGiantCapabilities();
            RebuildSpatialGrid();
        }

        private byte registeredConnectivityCapabilitiesMask;

        public void RefreshMapConnectivityForGiantCapabilities()
        {
            if (disposed || map == null)
            {
                return;
            }
            byte mask = 0;
            if (GiantDestructionUtility.AnyRigSupportsCrushing(map, out bool allowB, out bool allowR, out bool allowT))
            {
                if (allowB) mask |= 1;
                if (allowR) mask |= 2;
                if (allowT) mask |= 4;
            }
            GiantFlightUtility.IsCellPassableForAnyAerialRig(map, IntVec3.Zero);
            for (int i = 0; i < giantsList.Count; i++)
            {
                GiantFlightDef fd = giantsList[i]?.RigDef?.ActiveFlightLocomotion;
                if (fd != null && fd.IsActive)
                {
                    if (fd.canFlyOverWalls) mask |= 8;
                    if (fd.canFlyOverWaterAndLava) mask |= 16;
                }
                GiantAquaticDef aq = giantsList[i]?.RigDef?.ActiveAquaticLocomotion;
                if (aq != null && aq.IsActive)
                {
                    mask |= 32;
                }
            }
            if ((mask & ~registeredConnectivityCapabilitiesMask) != 0)
            {
                registeredConnectivityCapabilitiesMask |= mask;
                map.pathFinder?.MapData?.Notify_CellDelta(new CellRect(0, 0, map.Size.x, map.Size.z));
            }
        }

        public void UnregisterGiant(CompGiantRig comp)
        {
            if (comp == null || !activeGiants.Remove(comp))
            {
                return;
            }
            giantsList.Remove(comp);
            bossHealthBarHud.NotifyGiantUnregistered(comp);
            RebuildSpatialGrid();
            spatialRegistrations.Remove(comp);
        }

        public void RegisterCorpseFootprint(Pawn deadPawn, CompGiantRig comp)
        {
            if (disposed || comp == null || deadPawn == null)
            {
                return;
            }
            if (activeCorpseGiants.Add(comp))
            {
                corpseGiantsList.Add(comp);
            }
            if (deadPawn.Corpse != null)
            {
                corpseLastPosition[comp] = deadPawn.Corpse.Position;
            }
            RebuildCorpseSpatialGrid();
        }

        public void RebuildCorpseSpatialGrid()
        {
            EnsureArraysInitialized();
            if (corpseGiantsByCell == null)
            {
                return;
            }

            for (int i = 0; i < touchedCorpseCellIndices.Count; i++)
            {
                corpseGiantsByCell[touchedCorpseCellIndices[i]]?.Clear();
            }
            touchedCorpseCellIndices.Clear();

            CellIndices indices = map.cellIndices;
            for (int g = corpseGiantsList.Count - 1; g >= 0; g--)
            {
                CompGiantRig comp = corpseGiantsList[g];
                Pawn pawn = comp?.Pawn;
                Corpse corpse = pawn?.Corpse;
                if (comp == null || pawn == null || !pawn.Dead || corpse == null || corpse.Destroyed || !corpse.Spawned || corpse.Map != map)
                {
                    if (comp != null)
                    {
                        activeCorpseGiants.Remove(comp);
                        corpseLastPosition.Remove(comp);
                    }
                    corpseGiantsList.RemoveAt(g);
                    continue;
                }
                if (comp.RigDef != null && !comp.RigDef.enableCorpseFootprint)
                {
                    continue;
                }

                GiantFootprintData fp = comp.Footprint;
                int hitCount = fp.hitCells.Count;
                for (int i = 0; i < hitCount; i++)
                {
                    IntVec3 c = fp.hitCells[i].cell;
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    int idx = indices.CellToIndex(c);
                    List<CompGiantRig> list = corpseGiantsByCell[idx];
                    if (list == null)
                    {
                        list = new List<CompGiantRig>(1);
                        corpseGiantsByCell[idx] = list;
                    }
                    if (list.Count == 0)
                    {
                        touchedCorpseCellIndices.Add(idx);
                    }
                    if (!list.Contains(comp))
                    {
                        list.Add(comp);
                    }
                }
            }
        }

        public void NotifyFootprintChanged(CompGiantRig changedComp)
        {
            if (disposed)
            {
                return;
            }
            if (changedComp != null && hitGiantsByCell != null && activeGiants.Contains(changedComp))
            {
                UpdateSingleGiantInSpatialGrid(changedComp);
            }
            else
            {
                RebuildSpatialGrid();
            }

            if (changedComp != null
                && !changedComp.IsCurrentlyAirborne
                && !changedComp.IsFlyingEntry
                && changedComp.Pawn != null
                && changedComp.Pawn.Spawned
                && !changedComp.Pawn.Dead
                && !changedComp.Pawn.Downed
                && (changedComp.RigDef == null || changedComp.RigDef.enableCollisionPawnPushOut))
            {
                PushOverlappingPawnsOutward(changedComp);
            }
        }

        private GiantSpatialRegistration GetOrCreateSpatialReg(CompGiantRig comp)
        {
            if (!spatialRegistrations.TryGetValue(comp, out GiantSpatialRegistration reg))
            {
                reg = new GiantSpatialRegistration();
                spatialRegistrations[comp] = reg;
            }
            return reg;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort GetGiantCollisionCost(CompGiantRig comp)
        {
            return (ushort)Mathf.Clamp(
                comp?.RigDef != null ? comp.RigDef.collisionPathCost : 400,
                0,
                MaxOtherPawnGiantCollisionCost);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort GetGiantPawnCanopyCost(CompGiantRig comp)
        {
            return (ushort)Mathf.Clamp(
                comp?.RigDef != null ? comp.RigDef.pawnCanopyPathCost : 1800,
                0,
                MaxOtherPawnGiantCollisionCost);
        }

        private ushort RecomputeOtherPawnCellPathCost(int idx)
        {
            ushort best = 0;
            List<CompGiantRig> colList = collisionGiantsByCell?[idx];
            if (colList != null)
            {
                for (int i = 0; i < colList.Count; i++)
                {
                    ushort c = GetGiantCollisionCost(colList[i]);
                    if (c > best)
                    {
                        best = c;
                    }
                }
            }
            List<CompGiantRig> exclList = pawnExclusionGiantsByCell?[idx];
            if (exclList != null)
            {
                for (int i = 0; i < exclList.Count; i++)
                {
                    ushort c = GetGiantPawnCanopyCost(exclList[i]);
                    if (c > best)
                    {
                        best = c;
                    }
                }
            }
            return best;
        }

        private void RecomputeUnionHitBounds()
        {
            bool any = false;
            int minX = int.MaxValue, maxX = int.MinValue;
            int minZ = int.MaxValue, maxZ = int.MinValue;
            for (int g = 0; g < giantsList.Count; g++)
            {
                CompGiantRig comp = giantsList[g];
                Pawn p = comp?.Pawn;
                if (p == null || !p.Spawned || p.Dead)
                {
                    continue;
                }
                CellRect hb = comp.Footprint.tightHitBounds;
                if (hb.IsEmpty)
                {
                    continue;
                }
                if (hb.minX < minX) minX = hb.minX;
                if (hb.maxX > maxX) maxX = hb.maxX;
                if (hb.minZ < minZ) minZ = hb.minZ;
                if (hb.maxZ > maxZ) maxZ = hb.maxZ;
                any = true;
            }
            unionHitBounds = any ? CellRect.FromLimits(minX, minZ, maxX, maxZ) : CellRect.Empty;
        }

        private bool SameRegisteredCells(
            List<int> registeredIndices,
            ushort lastCost,
            List<IntVec3> newCells,
            bool newActive,
            ushort newCost)
        {
            if (!newActive)
            {
                return registeredIndices.Count == 0;
            }
            if (lastCost != newCost)
            {
                return false;
            }
            CellIndices indices = map.cellIndices;
            int matchedInBounds = 0;
            int newCount = newCells.Count;
            for (int i = 0; i < newCount; i++)
            {
                IntVec3 c = newCells[i];
                if (!c.InBounds(map))
                {
                    continue;
                }
                if (matchedInBounds >= registeredIndices.Count || registeredIndices[matchedInBounds] != indices.CellToIndex(c))
                {
                    return false;
                }
                matchedInBounds++;
            }
            return matchedInBounds == registeredIndices.Count;
        }

        private void UpdateSingleGiantInSpatialGrid(CompGiantRig changedComp)
        {
            GiantSpatialRegistration reg = GetOrCreateSpatialReg(changedComp);
            CellIndices indices = map.cellIndices;

            // 1. Remove old hit cells for changedComp only
            for (int i = 0; i < reg.hitIndices.Count; i++)
            {
                hitGiantsByCell[reg.hitIndices[i]]?.Remove(changedComp);
            }
            reg.hitIndices.Clear();

            Pawn pawn = changedComp.Pawn;
            bool liveSpawned = pawn != null && pawn.Spawned && !pawn.Dead;
            GiantFootprintData fp = changedComp.Footprint;

            // 2. Insert new hit cells for changedComp
            if (liveSpawned)
            {
                int hitCount = fp.hitCells.Count;
                for (int i = 0; i < hitCount; i++)
                {
                    IntVec3 c = fp.hitCells[i].cell;
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    int idx = indices.CellToIndex(c);
                    List<CompGiantRig> list = hitGiantsByCell[idx];
                    if (list == null)
                    {
                        list = new List<CompGiantRig>(2);
                        hitGiantsByCell[idx] = list;
                    }
                    list.Add(changedComp);
                    reg.hitIndices.Add(idx);
                }
            }

            // 3. Incrementally update pawn-exclusion canopy cells & core collision cells
            bool newHasCol = liveSpawned && !pawn.Downed && !changedComp.IsCurrentlyAirborne && !changedComp.IsFlyingEntry;
            ushort newCanopyCost = newHasCol ? GetGiantPawnCanopyCost(changedComp) : (ushort)0;
            ushort newCost = newHasCol ? GetGiantCollisionCost(changedComp) : (ushort)0;
            bool otherPawnBufDirty = false;
            ushort[] colBuf = otherPawnsCustomizer.ManagedBuffer;

            if (!SameRegisteredCells(reg.pawnExclIndices, reg.lastPawnExclCost, fp.pawnExclusionCells, newHasCol, newCanopyCost))
            {
                for (int i = 0; i < reg.pawnExclIndices.Count; i++)
                {
                    int idx = reg.pawnExclIndices[i];
                    List<CompGiantRig> exclList = pawnExclusionGiantsByCell[idx];
                    if (exclList != null && exclList.Remove(changedComp))
                    {
                        if (exclList.Count == 0)
                        {
                            activePawnExclusionCellCount = Math.Max(0, activePawnExclusionCellCount - 1);
                        }
                        colBuf[idx] = RecomputeOtherPawnCellPathCost(idx);
                    }
                }
                reg.pawnExclIndices.Clear();
                reg.lastPawnExclCost = newCanopyCost;

                if (newHasCol)
                {
                    int exclCount = fp.pawnExclusionCells.Count;
                    for (int i = 0; i < exclCount; i++)
                    {
                        IntVec3 c = fp.pawnExclusionCells[i];
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        int idx = indices.CellToIndex(c);
                        List<CompGiantRig> exclList = pawnExclusionGiantsByCell[idx];
                        if (exclList == null)
                        {
                            exclList = new List<CompGiantRig>(2);
                            pawnExclusionGiantsByCell[idx] = exclList;
                        }
                        if (exclList.Count == 0)
                        {
                            activePawnExclusionCellCount++;
                        }
                        exclList.Add(changedComp);
                        if (newCanopyCost > colBuf[idx])
                        {
                            colBuf[idx] = newCanopyCost;
                        }
                        reg.pawnExclIndices.Add(idx);
                    }
                }
                otherPawnBufDirty = true;
            }

            if (!SameRegisteredCells(reg.colIndices, reg.lastColCost, fp.collisionCells, newHasCol, newCost))
            {
                for (int i = 0; i < reg.colIndices.Count; i++)
                {
                    int idx = reg.colIndices[i];
                    List<CompGiantRig> colList = collisionGiantsByCell[idx];
                    if (colList != null && colList.Remove(changedComp))
                    {
                        if (colList.Count == 0)
                        {
                            activeCollisionCellCount = Math.Max(0, activeCollisionCellCount - 1);
                            collisionOwnerByCell[idx] = null;
                        }
                        else
                        {
                            collisionOwnerByCell[idx] = colList[0];
                        }
                        colBuf[idx] = RecomputeOtherPawnCellPathCost(idx);
                    }
                }
                reg.colIndices.Clear();
                reg.lastColCost = newCost;

                if (newHasCol)
                {
                    int colCount = fp.collisionCells.Count;
                    for (int i = 0; i < colCount; i++)
                    {
                        IntVec3 c = fp.collisionCells[i];
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        int idx = indices.CellToIndex(c);
                        List<CompGiantRig> colList = collisionGiantsByCell[idx];
                        if (colList == null)
                        {
                            colList = new List<CompGiantRig>(2);
                            collisionGiantsByCell[idx] = colList;
                        }
                        if (colList.Count == 0)
                        {
                            activeCollisionCellCount++;
                            collisionOwnerByCell[idx] = changedComp;
                        }
                        colList.Add(changedComp);
                        if (newCost > colBuf[idx])
                        {
                            colBuf[idx] = newCost;
                        }
                        reg.colIndices.Add(idx);
                    }
                }

                otherPawnBufDirty = true;
                pathSnapshotDirty = true;
            }

            if (otherPawnBufDirty)
            {
                lastCollisionGridHash = ulong.MaxValue;
                otherPawnsCustomizer.MarkDirty();
            }

            RecomputeUnionHitBounds();
        }

        private void RebuildSpatialGrid()
        {
            EnsureArraysInitialized();
            if (hitGiantsByCell == null)
            {
                return;
            }

            ushort[] colBuf = otherPawnsCustomizer.ManagedBuffer;
            foreach (var kv in spatialRegistrations)
            {
                GiantSpatialRegistration reg = kv.Value;
                for (int i = 0; i < reg.hitIndices.Count; i++)
                {
                    hitGiantsByCell[reg.hitIndices[i]]?.Clear();
                }
                reg.hitIndices.Clear();

                for (int i = 0; i < reg.pawnExclIndices.Count; i++)
                {
                    int idx = reg.pawnExclIndices[i];
                    pawnExclusionGiantsByCell[idx]?.Clear();
                    colBuf[idx] = 0;
                }
                reg.pawnExclIndices.Clear();
                reg.lastPawnExclCost = 0;

                for (int i = 0; i < reg.colIndices.Count; i++)
                {
                    int idx = reg.colIndices[i];
                    collisionGiantsByCell[idx]?.Clear();
                    collisionOwnerByCell[idx] = null;
                    colBuf[idx] = 0;
                }
                reg.colIndices.Clear();
                reg.lastColCost = 0;
            }

            for (int i = 0; i < touchedHitCellIndices.Count; i++)
            {
                hitGiantsByCell[touchedHitCellIndices[i]]?.Clear();
            }
            touchedHitCellIndices.Clear();

            for (int i = 0; i < touchedPawnExclusionCellIndices.Count; i++)
            {
                int idx = touchedPawnExclusionCellIndices[i];
                pawnExclusionGiantsByCell[idx]?.Clear();
                colBuf[idx] = 0;
            }
            touchedPawnExclusionCellIndices.Clear();
            activePawnExclusionCellCount = 0;

            for (int i = 0; i < touchedCollisionCellIndices.Count; i++)
            {
                int idx = touchedCollisionCellIndices[i];
                collisionGiantsByCell[idx]?.Clear();
                collisionOwnerByCell[idx] = null;
                colBuf[idx] = 0;
            }
            touchedCollisionCellIndices.Clear();
            activeCollisionCellCount = 0;

            ulong colHash = 14695981039346656037UL;
            CellIndices indices = map.cellIndices;
            for (int g = 0; g < giantsList.Count; g++)
            {
                CompGiantRig comp = giantsList[g];
                Pawn pawn = comp.Pawn;
                if (pawn == null || !pawn.Spawned || pawn.Dead)
                {
                    continue;
                }

                GiantSpatialRegistration reg = GetOrCreateSpatialReg(comp);
                GiantFootprintData fp = comp.Footprint;
                int hitCount = fp.hitCells.Count;
                for (int i = 0; i < hitCount; i++)
                {
                    IntVec3 c = fp.hitCells[i].cell;
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    int idx = indices.CellToIndex(c);
                    List<CompGiantRig> list = hitGiantsByCell[idx];
                    if (list == null)
                    {
                        list = new List<CompGiantRig>(2);
                        hitGiantsByCell[idx] = list;
                    }
                    if (list.Count == 0)
                    {
                        touchedHitCellIndices.Add(idx);
                    }
                    list.Add(comp);
                    reg.hitIndices.Add(idx);
                }

                if (!pawn.Downed && !comp.IsCurrentlyAirborne && !comp.IsFlyingEntry)
                {
                    ushort canopyCost = GetGiantPawnCanopyCost(comp);
                    ushort cost = GetGiantCollisionCost(comp);
                    reg.lastPawnExclCost = canopyCost;
                    reg.lastColCost = cost;
                    colHash = (colHash ^ (ulong)(uint)pawn.thingIDNumber) * 1099511628211UL;

                    int exclCount = fp.pawnExclusionCells.Count;
                    for (int i = 0; i < exclCount; i++)
                    {
                        IntVec3 c = fp.pawnExclusionCells[i];
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        int idx = indices.CellToIndex(c);
                        List<CompGiantRig> exclList = pawnExclusionGiantsByCell[idx];
                        if (exclList == null)
                        {
                            exclList = new List<CompGiantRig>(2);
                            pawnExclusionGiantsByCell[idx] = exclList;
                        }
                        if (exclList.Count == 0)
                        {
                            touchedPawnExclusionCellIndices.Add(idx);
                            activePawnExclusionCellCount++;
                        }
                        exclList.Add(comp);
                        reg.pawnExclIndices.Add(idx);
                        if (canopyCost > colBuf[idx])
                        {
                            colBuf[idx] = canopyCost;
                        }
                        colHash = (colHash ^ (((ulong)(uint)idx << 14) ^ canopyCost)) * 1099511628211UL;
                    }

                    int colCount = fp.collisionCells.Count;
                    for (int i = 0; i < colCount; i++)
                    {
                        IntVec3 c = fp.collisionCells[i];
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        int idx = indices.CellToIndex(c);
                        List<CompGiantRig> colList = collisionGiantsByCell[idx];
                        if (colList == null)
                        {
                            colList = new List<CompGiantRig>(2);
                            collisionGiantsByCell[idx] = colList;
                        }
                        if (colList.Count == 0)
                        {
                            touchedCollisionCellIndices.Add(idx);
                            activeCollisionCellCount++;
                        }
                        colList.Add(comp);
                        reg.colIndices.Add(idx);
                        if (collisionOwnerByCell[idx] == null)
                        {
                            collisionOwnerByCell[idx] = comp;
                        }
                        if (cost > colBuf[idx])
                        {
                            colBuf[idx] = cost;
                        }
                        colHash = (colHash ^ (((ulong)(uint)idx << 16) ^ cost)) * 1099511628211UL;
                    }
                }
            }

            RecomputeUnionHitBounds();

            if (colHash != lastCollisionGridHash)
            {
                lastCollisionGridHash = colHash;
                otherPawnsCustomizer.MarkDirty();
                pathSnapshotDirty = true;
            }
        }

        public void EnsurePathAvoidanceSnapshot()
        {
            if (map == null || (!pathSnapshotDirty && latestPathSnapshot != null))
            {
                return;
            }

            List<GiantSnapshotEntry> entries = new List<GiantSnapshotEntry>(giantsList.Count);
            for (int g = 0; g < giantsList.Count; g++)
            {
                CompGiantRig comp = giantsList[g];
                Pawn pawn = comp?.Pawn;
                if (comp == null || pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Flying || comp.IsFlyingEntry || comp.IsCurrentlyAirborne ||
                    (comp.RigDef != null && !comp.RigDef.enableGiantToGiantCollision))
                {
                    continue;
                }

                GiantFootprintData fp = comp.Footprint;
                IntVec3 anchor = fp.collisionAnchorCell.IsValid ? fp.collisionAnchorCell : pawn.Position;
                IntVec3[] offsetsCopy = fp.localCollisionOffsets.Count > 0
                    ? fp.localCollisionOffsets.ToArray()
                    : new[] { IntVec3.Zero };
                IntVec3[] colCellsCopy = fp.collisionCells.Count > 0
                    ? fp.collisionCells.ToArray()
                    : new[] { pawn.Position };
                ushort avoidCost = (ushort)Mathf.Clamp(
                    comp.RigDef != null ? comp.RigDef.collisionPathCost : 400,
                    0,
                    MaxOtherPawnGiantCollisionCost);
                entries.Add(new GiantSnapshotEntry(
                    pawn.thingIDNumber,
                    anchor,
                    fp.tightCollisionBounds,
                    offsetsCopy,
                    colCellsCopy,
                    avoidCost,
                    fp.maxCollisionHorizontalExtent));
            }

            latestPathSnapshot = new GiantPathAvoidanceSnapshot(map.Size.x, map.Size.z, entries.ToArray());
            pathSnapshotDirty = false;
        }

        /// <summary>
        /// Worker-thread-safe giant-to-giant avoidance injector invoked after PathGridDoorsBlockedJob.Execute().
        /// Reads ONLY the immutable volatile GiantPathAvoidanceSnapshot and thread-local scratch buffers.
        /// </summary>
        public void ApplyPerRequestGiantAvoidance(Pawn requester, NativeArray<ushort> providerCost)
        {
            if (requester == null || ShouldPawnIgnoreGiantCollision(requester) || !providerCost.IsCreated)
            {
                return;
            }

            GiantPathAvoidanceSnapshot snapshot = latestPathSnapshot;
            if (snapshot == null || snapshot.giants.Length < 2)
            {
                return;
            }
            if (!snapshot.entryByPawnId.TryGetValue(requester.thingIDNumber, out GiantSnapshotEntry selfEntry))
            {
                return;
            }

            GiantHullCollisionUtility.AccumulateOverlapCounts(
                snapshot.mapSizeX,
                snapshot.mapSizeZ,
                snapshot.numCells,
                selfEntry.localOffsets,
                snapshot.giants,
                selfEntry.pawnId,
                out List<int> touched,
                out int[] counts);

            int len = providerCost.Length;
            for (int i = 0; i < touched.Count; i++)
            {
                int idx = touched[i];
                if ((uint)idx < (uint)len)
                {
                    providerCost[idx] = GiantHullCollisionUtility.MergeAvoidanceCost(providerCost[idx], counts[idx]);
                }
            }
        }

        /// <summary>
        /// Called right after PathFinder.ForceCompleteScheduledJobs so zero worker threads are reading NativeArrays.
        /// Returns true if any native buffer changed (caller should invalidate PathFinder cached grid outputs).
        /// </summary>
        public bool SyncNativeCustomizersSafe()
        {
            if (disposed)
            {
                return false;
            }
            if (!HasAnyGiants &&
                otherPawnsCustomizer == null &&
                clearanceCustomizersByRadius.Count == 0 &&
                breachCustomizers.Count == 0 &&
                aerialCustomizers.Count == 0 &&
                aquaticCustomizers.Count == 0)
            {
                return false;
            }
            bool anySynced = false;
            if (otherPawnsCustomizer != null && otherPawnsCustomizer.SyncIfDirty())
            {
                anySynced = true;
            }

            if (clearanceDirty && clearanceCustomizersByRadius.Count > 0)
            {
                foreach (var kv in clearanceCustomizersByRadius)
                {
                    RecomputeWallClearanceBuffer(kv.Key, kv.Value.ManagedBuffer);
                    kv.Value.MarkDirty();
                }
                clearanceDirty = false;
            }

            foreach (var kv in clearanceCustomizersByRadius)
            {
                if (kv.Value.SyncIfDirty())
                {
                    anySynced = true;
                }
            }

            if (breachDirty && breachCustomizers.Count > 0)
            {
                RefreshDirtyBreachManagedBuffers();
            }

            foreach (var kv in breachCustomizers)
            {
                if (kv.Value.SyncIfDirty())
                {
                    anySynced = true;
                }
            }

            if (aerialDirty && aerialCustomizers.Count > 0)
            {
                RefreshDirtyAerialManagedBuffers();
            }

            foreach (var kv in aerialCustomizers)
            {
                if (kv.Value.SyncIfDirty())
                {
                    anySynced = true;
                }
            }

            if (aquaticDirty && aquaticCustomizers.Count > 0)
            {
                RefreshDirtyAquaticManagedBuffers();
            }

            foreach (var kv in aquaticCustomizers)
            {
                if (kv.Value.SyncIfDirty())
                {
                    anySynced = true;
                }
            }

            EnsurePathAvoidanceSnapshot();
            return anySynced;
        }

        private static Faction ResolveFactionByLoadId(int factionLoadId)
        {
            if (factionLoadId < 0)
            {
                return null;
            }
            List<Faction> factions = Find.FactionManager?.AllFactionsListForReading;
            if (factions == null)
            {
                return null;
            }
            for (int i = 0; i < factions.Count; i++)
            {
                Faction f = factions[i];
                if (f != null && f.loadID == factionLoadId)
                {
                    return f;
                }
            }
            return null;
        }

        private void RefreshDirtyBreachManagedBuffers()
        {
            foreach (var kv in breachCustomizers)
            {
                FindSampleGiantForKey(kv.Key, out Pawn samplePawn, out _);
                RecomputeBreachBuffers(kv.Key, kv.Value, samplePawn);
                kv.Value.MarkDirty();
            }
            breachDirty = false;
            cachedDistrictBreachReach.Clear();
        }

        private void RefreshDirtyAerialManagedBuffers()
        {
            foreach (var kv in aerialCustomizers)
            {
                RecomputeAerialBuffers(kv.Key, kv.Value);
                kv.Value.MarkDirty();
            }
            aerialDirty = false;
        }

        private void RefreshDirtyAquaticManagedBuffers()
        {
            foreach (var kv in aquaticCustomizers)
            {
                FindSampleAquaticGiantForKey(kv.Key, out Pawn samplePawn);
                RecomputeAquaticBuffers(kv.Key, kv.Value, samplePawn);
                kv.Value.MarkDirty();
            }
            aquaticDirty = false;
        }

        private void FindSampleAquaticGiantForKey(GiantAquaticCustomizerKey key, out Pawn samplePawn)
        {
            samplePawn = null;
            for (int i = 0; i < giantsList.Count; i++)
            {
                CompGiantRig comp = giantsList[i];
                Pawn p = comp?.Pawn;
                if (p == null || !p.Spawned || p.Dead || comp.RigDef == null || comp.IsAirborneOrTakingOff)
                {
                    continue;
                }
                if (comp.RigDef.ActiveAquaticLocomotion is GiantAquaticDef aq && aq.IsActive)
                {
                    int fId = p.Faction != null ? p.Faction.loadID : -1;
                    if (key.factionLoadId < 0 || fId == key.factionLoadId)
                    {
                        samplePawn = p;
                        return;
                    }
                }
            }
        }

        private void FindSampleGiantForKey(
            GiantBreachCustomizerKey key,
            out Pawn samplePawn,
            out CompGiantRig sampleComp)
        {
            samplePawn = null;
            sampleComp = null;
            for (int i = 0; i < giantsList.Count; i++)
            {
                CompGiantRig comp = giantsList[i];
                Pawn p = comp?.Pawn;
                if (p == null || !p.Spawned || p.Dead || comp.RigDef == null || comp.IsAirborneOrTakingOff)
                {
                    continue;
                }
                int fId = p.Faction != null ? p.Faction.loadID : -1;
                if (fId != key.factionLoadId)
                {
                    continue;
                }
                if (!GiantDestructionUtility.CanGiantCrushAnything(comp, out bool allowBuildings, out bool allowRock, out bool allowTerrain))
                {
                    continue;
                }
                bool protectAllied = comp.RigDef.protectAlliedStructures
                    && p.Faction != null
                    && !(p.mindState?.mentalStateHandler != null && p.InAggroMentalState);
                if (allowBuildings == key.allowBuildings
                    && allowRock == key.allowNaturalRock
                    && allowTerrain == key.allowTerrain
                    && protectAllied == key.protectAllied)
                {
                    samplePawn = p;
                    sampleComp = comp;
                    return;
                }
            }
        }

        private bool TryGetOrCreateBreachCustomizer(
            Pawn requester,
            CompGiantRig giantComp,
            out GiantBreachCustomizerKey key,
            out GiantBreachPathCustomizer breachCust)
        {
            key = default;
            breachCust = null;
            if (requester == null || giantComp?.RigDef == null)
            {
                return false;
            }
            if (!GiantDestructionUtility.CanGiantCrushAnything(
                giantComp,
                out bool allowBuildings,
                out bool allowRock,
                out bool allowTerrain))
            {
                return false;
            }

            EnsureArraysInitialized();
            if (disposed)
            {
                return false;
            }

            int radius = giantComp.RigDef.coreClearanceRadius;
            int factionId = requester.Faction != null ? requester.Faction.loadID : -1;
            bool protectAllied = giantComp.RigDef.protectAlliedStructures
                && requester.Faction != null
                && !(requester.mindState?.mentalStateHandler != null && requester.InAggroMentalState);
            key = new GiantBreachCustomizerKey(
                Mathf.Max(0, radius),
                factionId,
                allowBuildings,
                allowRock,
                allowTerrain,
                protectAllied);

            if (!breachCustomizers.TryGetValue(key, out breachCust))
            {
                breachCust = new GiantBreachPathCustomizer(map.cellIndices.NumGridCells);
                RecomputeBreachBuffers(key, breachCust, requester);
                breachCust.SyncIfDirty();
                breachCustomizers[key] = breachCust;
            }
            return true;
        }

        public PathRequest.IPathGridCustomizer GetPathCustomizerFor(Pawn requester, IntVec3 startCell)
        {
            if (disposed || requester == null || ShouldPawnIgnoreGiantCollision(requester))
            {
                return null;
            }

            if (GiantFastLookup.TryGetRig(requester) is CompGiantRig giantComp && giantComp.RigDef != null)
            {
                EnsureArraysInitialized();
                if (giantComp.IsAirborneOrTakingOff && giantComp.RigDef.ActiveFlightLocomotion is GiantFlightDef flightDef)
                {
                    return GetOrCreateAerialCustomizer(giantComp, flightDef);
                }

                EnsurePathAvoidanceSnapshot();
                if (giantComp.RigDef.ActiveAquaticLocomotion is GiantAquaticDef aquaticDef && aquaticDef.IsActive)
                {
                    return GetOrCreateAquaticCustomizer(requester, giantComp, aquaticDef);
                }

                if (TryGetOrCreateBreachCustomizer(requester, giantComp, out _, out GiantBreachPathCustomizer breachCust))
                {
                    return breachCust;
                }

                int radius = giantComp.RigDef.coreClearanceRadius;
                if (radius <= 0)
                {
                    return null;
                }
                if (!clearanceCustomizersByRadius.TryGetValue(radius, out GiantCollisionPathCustomizer selfCust))
                {
                    selfCust = new GiantCollisionPathCustomizer(map.cellIndices.NumGridCells);
                    RecomputeWallClearanceBuffer(radius, selfCust.ManagedBuffer);
                    selfCust.SyncIfDirty();
                    clearanceCustomizersByRadius[radius] = selfCust;
                }
                // If giant starts in a cramped spot, don't trap it at startCell
                if (startCell.InBounds(map) && selfCust.ManagedBuffer[map.cellIndices.CellToIndex(startCell)] >= 10000)
                {
                    return null;
                }
                return selfCust;
            }

            if (!HasAnyGiants || (activeCollisionCellCount <= 0 && activePawnExclusionCellCount <= 0) || otherPawnsCustomizer == null || collisionOwnerByCell == null)
            {
                return null;
            }

            // If a normal pawn is currently inside a collision or canopy-exclusion cell (e.g. before push-out), let it path out freely!
            if (startCell.InBounds(map))
            {
                int startIdx = map.cellIndices.CellToIndex(startCell);
                if (collisionOwnerByCell[startIdx] != null
                    || (pawnExclusionGiantsByCell?[startIdx] != null && pawnExclusionGiantsByCell[startIdx].Count > 0))
                {
                    return null;
                }
            }

            return otherPawnsCustomizer;
        }

        private void RecomputeBreachBuffers(
            GiantBreachCustomizerKey key,
            GiantBreachPathCustomizer cust,
            Pawn sampleGiant)
        {
            ushort[] offsets = cust.ManagedOffsets;
            int[] directCosts = cust.ManagedDirectCosts;
            Array.Clear(offsets, 0, offsets.Length);

            IntVec3 size = map.Size;
            CellIndices indices = map.cellIndices;
            TerrainGrid tg = map.terrainGrid;
            Faction keyFaction = ResolveFactionByLoadId(key.factionLoadId);

            for (int z = 0; z < size.z; z++)
            {
                for (int x = 0; x < size.x; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    int idx = indices.CellToIndex(x, z);
                    Building edifice = c.GetEdifice(map);
                    TerrainDef terrain = tg != null ? tg.TerrainAt(c) : null;

                    int vanillaDirectCost = 0;
                    if ((terrain != null && terrain.passability == Traversability.Impassable)
                        || (edifice != null && edifice.def.passability == Traversability.Impassable))
                    {
                        vanillaDirectCost = GiantDestructionUtility.ImpassablePathCost;
                    }
                    else
                    {
                        if (terrain != null)
                        {
                            vanillaDirectCost += Mathf.Max(0, terrain.pathCost);
                        }
                        if (edifice != null)
                        {
                            vanillaDirectCost += Mathf.Max(0, edifice.def.pathCost);
                        }
                    }

                    GiantDestructionUtility.TryEvaluateGiantCell(
                        map,
                        c,
                        keyFaction,
                        key.allowBuildings,
                        key.allowNaturalRock,
                        key.allowTerrain,
                        key.protectAllied,
                        sampleGiant,
                        vanillaDirectCost,
                        out bool isHardBlocked,
                        out _,
                        out int breachDirectCost);

                    directCosts[idx] = breachDirectCost;

                    if (isHardBlocked || breachDirectCost >= GiantDestructionUtility.ImpassablePathCost)
                    {
                        // Write 10000 into custom offsets as well so Burst PathGridJob hard-blocks thick-roof,
                        // indestructible, and protected allied cells even under PassAllDestroyableThings.
                        offsets[idx] = GiantDestructionUtility.ImpassablePathCost;

                        if (key.clearanceRadius > 0)
                        {
                            int r = key.clearanceRadius;
                            int minX = Mathf.Max(0, x - r);
                            int maxX = Mathf.Min(size.x - 1, x + r);
                            int minZ = Mathf.Max(0, z - r);
                            int maxZ = Mathf.Min(size.z - 1, z + r);
                            for (int nz = minZ; nz <= maxZ; nz++)
                            {
                                for (int nx = minX; nx <= maxX; nx++)
                                {
                                    int nIdx = indices.CellToIndex(nx, nz);
                                    if (offsets[nIdx] < 950)
                                    {
                                        offsets[nIdx] = 950;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public bool CanBreachReach(
            Pawn giant,
            IntVec3 start,
            LocalTargetInfo dest,
            Verse.AI.PathEndMode peMode)
        {
            if (disposed || giant == null || !dest.IsValid || !start.InBounds(map) || !dest.Cell.InBounds(map))
            {
                return false;
            }

            CompGiantRig giantComp = GiantFastLookup.TryGetRig(giant);
            if (giantComp == null || giantComp.IsAirborneOrTakingOff)
            {
                return false;
            }

            if (breachDirty)
            {
                // Refresh existing managed buffers immediately so same-tick queries after map edits never read stale
                // costs or poison cachedDistrictBreachReach, while keeping native sync pending for SyncNativeCustomizersSafe.
                RefreshDirtyBreachManagedBuffers();
            }

            if (!TryGetOrCreateBreachCustomizer(giant, giantComp, out GiantBreachCustomizerKey customizerKey, out GiantBreachPathCustomizer breachCust))
            {
                return false;
            }

            CellIndices indices = map.cellIndices;
            int[] directCosts = breachCust.ManagedDirectCosts;
            IntVec3 destCell = dest.Cell;
            int destIdx = indices.CellToIndex(destCell);

            if (peMode == Verse.AI.PathEndMode.OnCell && directCosts[destIdx] >= GiantDestructionUtility.ImpassablePathCost)
            {
                return false;
            }

            District startDistrict = RegionAndRoomQuery.DistirctAtFast(start, map);
            District destDistrict = RegionAndRoomQuery.DistirctAtFast(destCell, map);
            bool hasCacheKey = false;
            BreachReachCacheKey cacheKey = default;
            if (startDistrict != null && destDistrict != null && directCosts[destIdx] < GiantDestructionUtility.ImpassablePathCost)
            {
                cacheKey = new BreachReachCacheKey(
                    customizerKey,
                    startDistrict.ID,
                    destDistrict.ID,
                    peMode == Verse.AI.PathEndMode.OnCell);
                hasCacheKey = true;
                if (cachedDistrictBreachReach.TryGetValue(cacheKey, out bool cachedResult))
                {
                    return cachedResult;
                }
            }

            int numCells = indices.NumGridCells;
            if (breachBfsVisitedStamp == null || breachBfsVisitedStamp.Length != numCells)
            {
                breachBfsVisitedStamp = new int[numCells];
                breachBfsCurrentStamp = 0;
            }
            breachBfsCurrentStamp++;
            if (breachBfsCurrentStamp == int.MaxValue)
            {
                Array.Clear(breachBfsVisitedStamp, 0, breachBfsVisitedStamp.Length);
                breachBfsCurrentStamp = 1;
            }
            int stamp = breachBfsCurrentStamp;

            CellRect goalRect;
            if (peMode == Verse.AI.PathEndMode.Touch || peMode == Verse.AI.PathEndMode.ClosestTouch)
            {
                CompGiantRig targetComp = dest.HasThing
                    ? (GiantFastLookup.TryGetRig(dest.Thing as Pawn)
                        ?? HarmonyPatches_CombatAndHitbox.TryGetEnabledCorpseRig(dest.Thing))
                    : null;
                CellRect baseRect = targetComp != null && targetComp.Footprint.hitCells.Count > 0
                    ? targetComp.Footprint.tightHitBounds
                    : (dest.HasThing ? dest.Thing.OccupiedRect() : CellRect.FromCell(destCell));
                int expandRadius = 1;
                if (giantComp.RigDef != null)
                {
                    expandRadius = Mathf.Max(
                        1,
                        Mathf.CeilToInt(giantComp.GetEffectiveMeleeTouchRadius()) + giantComp.Footprint.maxCollisionHorizontalExtent);
                }
                goalRect = baseRect.ExpandedBy(expandRadius).ClipInsideMap(map);
            }
            else
            {
                goalRect = CellRect.FromCell(destCell);
            }

            int sizeX = map.Size.x;
            int sizeZ = map.Size.z;
            int startIdx = indices.CellToIndex(start);
            breachBfsQueue.Clear();
            breachBfsVisitedStamp[startIdx] = stamp;
            breachBfsQueue.Enqueue(startIdx);

            bool found = false;
            while (breachBfsQueue.Count > 0)
            {
                int curIdx = breachBfsQueue.Dequeue();
                IntVec3 cur = indices.IndexToCell(curIdx);
                if (goalRect.Contains(cur) && directCosts[curIdx] < GiantDestructionUtility.ImpassablePathCost)
                {
                    found = true;
                    break;
                }

                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = cur.z + dz;
                    if ((uint)nz >= (uint)sizeZ)
                    {
                        continue;
                    }
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                        {
                            continue;
                        }
                        int nx = cur.x + dx;
                        if ((uint)nx >= (uint)sizeX)
                        {
                            continue;
                        }
                        int nIdx = nz * sizeX + nx;
                        if (breachBfsVisitedStamp[nIdx] == stamp)
                        {
                            continue;
                        }
                        if (directCosts[nIdx] >= GiantDestructionUtility.ImpassablePathCost)
                        {
                            continue;
                        }
                        if (dx != 0 && dz != 0)
                        {
                            int ortho1 = cur.z * sizeX + nx;
                            int ortho2 = nz * sizeX + cur.x;
                            if (directCosts[ortho1] >= GiantDestructionUtility.ImpassablePathCost
                                || directCosts[ortho2] >= GiantDestructionUtility.ImpassablePathCost)
                            {
                                continue;
                            }
                        }
                        breachBfsVisitedStamp[nIdx] = stamp;
                        breachBfsQueue.Enqueue(nIdx);
                    }
                }
            }
            breachBfsQueue.Clear();

            if (hasCacheKey)
            {
                cachedDistrictBreachReach[cacheKey] = found;
            }
            return found;
        }

        private GiantAerialPathCustomizer GetOrCreateAerialCustomizer(CompGiantRig giantComp, GiantFlightDef flightDef)
        {
            int clearance = flightDef.blockedByThickRoof
                ? Mathf.Clamp(giantComp?.RigDef?.coreClearanceRadius ?? 0, 0, 2)
                : 0;
            GiantAerialCustomizerKey key = new GiantAerialCustomizerKey(
                clearance,
                flightDef.canFlyOverWalls,
                flightDef.canFlyOverWaterAndLava,
                flightDef.blockedByThickRoof);

            if (!aerialCustomizers.TryGetValue(key, out GiantAerialPathCustomizer aerialCust))
            {
                aerialCust = new GiantAerialPathCustomizer(map.cellIndices.NumGridCells);
                RecomputeAerialBuffers(key, aerialCust);
                aerialCust.SyncIfDirty();
                aerialCustomizers[key] = aerialCust;
                aerialKeysByCustomizer[aerialCust] = key;
            }
            return aerialCust;
        }

        private void RecomputeAerialBuffers(GiantAerialCustomizerKey key, GiantAerialPathCustomizer cust)
        {
            ushort[] offsets = cust.ManagedOffsets;
            int[] directCosts = cust.ManagedDirectCosts;
            Array.Clear(offsets, 0, offsets.Length);

            IntVec3 size = map.Size;
            CellIndices indices = map.cellIndices;
            TerrainGrid tg = map.terrainGrid;

            for (int z = 0; z < size.z; z++)
            {
                for (int x = 0; x < size.x; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    int idx = indices.CellToIndex(x, z);
                    bool thickRoof = GiantDestructionUtility.HasThickRoofAt(map, c);
                    Building edifice = c.GetEdifice(map);
                    TerrainDef terrain = tg != null ? tg.TerrainAt(c) : null;

                    bool impassableEdifice = edifice != null && edifice.def.passability == Traversability.Impassable;
                    bool impassableTerrain = terrain != null && terrain.passability == Traversability.Impassable;

                    int cost = GiantFlightUtility.ComputeAerialDirectCost(
                        thickRoof,
                        key.blockedByThickRoof,
                        impassableEdifice,
                        key.canFlyOverWalls,
                        impassableTerrain,
                        key.canFlyOverWaterAndLava);
                    directCosts[idx] = cost;

                    if (cost >= GiantDestructionUtility.ImpassablePathCost)
                    {
                        offsets[idx] = GiantDestructionUtility.ImpassablePathCost;

                        if (key.clearanceRadius > 0)
                        {
                            int r = key.clearanceRadius;
                            int minX = Mathf.Max(0, x - r);
                            int maxX = Mathf.Min(size.x - 1, x + r);
                            int minZ = Mathf.Max(0, z - r);
                            int maxZ = Mathf.Min(size.z - 1, z + r);
                            for (int nz = minZ; nz <= maxZ; nz++)
                            {
                                for (int nx = minX; nx <= maxX; nx++)
                                {
                                    int nIdx = indices.CellToIndex(nx, nz);
                                    if (offsets[nIdx] < 650)
                                    {
                                        offsets[nIdx] = 650;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        public bool CanAerialReach(
            Pawn giant,
            CompGiantRig giantComp,
            IntVec3 start,
            LocalTargetInfo dest,
            Verse.AI.PathEndMode peMode)
        {
            if (disposed || giant == null || giantComp?.RigDef == null || !dest.IsValid || !start.InBounds(map) || !dest.Cell.InBounds(map))
            {
                return false;
            }

            GiantFlightDef flightDef = giantComp.FlightLocomotion?.Def ?? giantComp.RigDef.ActiveFlightLocomotion;
            if (flightDef == null || !flightDef.IsActive)
            {
                return false;
            }

            EnsureArraysInitialized();
            if (disposed)
            {
                return false;
            }
            if (aerialDirty)
            {
                RefreshDirtyAerialManagedBuffers();
            }
            GiantAerialPathCustomizer aerialCust = GetOrCreateAerialCustomizer(giantComp, flightDef);
            int[] directCosts = aerialCust.ManagedDirectCosts;
            CellIndices indices = map.cellIndices;
            IntVec3 destCell = dest.Cell;
            int destIdx = indices.CellToIndex(destCell);

            if (peMode == Verse.AI.PathEndMode.OnCell && directCosts[destIdx] >= GiantDestructionUtility.ImpassablePathCost)
            {
                return false;
            }

            int numCells = indices.NumGridCells;
            if (breachBfsVisitedStamp == null || breachBfsVisitedStamp.Length != numCells)
            {
                breachBfsVisitedStamp = new int[numCells];
                breachBfsCurrentStamp = 0;
            }
            breachBfsCurrentStamp++;
            if (breachBfsCurrentStamp == int.MaxValue)
            {
                Array.Clear(breachBfsVisitedStamp, 0, breachBfsVisitedStamp.Length);
                breachBfsCurrentStamp = 1;
            }
            int stamp = breachBfsCurrentStamp;

            CellRect goalRect = (peMode == Verse.AI.PathEndMode.Touch || peMode == Verse.AI.PathEndMode.ClosestTouch)
                ? (dest.HasThing ? dest.Thing.OccupiedRect() : CellRect.FromCell(destCell)).ExpandedBy(1).ClipInsideMap(map)
                : CellRect.FromCell(destCell);

            int sizeX = map.Size.x;
            int sizeZ = map.Size.z;
            int startIdx = indices.CellToIndex(start);
            breachBfsQueue.Clear();
            breachBfsVisitedStamp[startIdx] = stamp;
            breachBfsQueue.Enqueue(startIdx);

            bool found = false;
            while (breachBfsQueue.Count > 0)
            {
                int curIdx = breachBfsQueue.Dequeue();
                IntVec3 cur = indices.IndexToCell(curIdx);
                if (goalRect.Contains(cur) && directCosts[curIdx] < GiantDestructionUtility.ImpassablePathCost)
                {
                    found = true;
                    break;
                }

                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = cur.z + dz;
                    if ((uint)nz >= (uint)sizeZ) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cur.x + dx;
                        if ((uint)nx >= (uint)sizeX) continue;
                        int nIdx = nz * sizeX + nx;
                        if (breachBfsVisitedStamp[nIdx] == stamp) continue;
                        if (directCosts[nIdx] >= GiantDestructionUtility.ImpassablePathCost) continue;
                        if (dx != 0 && dz != 0)
                        {
                            int ortho1 = cur.z * sizeX + nx;
                            int ortho2 = nz * sizeX + cur.x;
                            if (directCosts[ortho1] >= GiantDestructionUtility.ImpassablePathCost
                                || directCosts[ortho2] >= GiantDestructionUtility.ImpassablePathCost)
                            {
                                continue;
                            }
                        }
                        breachBfsVisitedStamp[nIdx] = stamp;
                        breachBfsQueue.Enqueue(nIdx);
                    }
                }
            }
            breachBfsQueue.Clear();
            return found;
        }

        private GiantAquaticPathCustomizer GetOrCreateAquaticCustomizer(
            Pawn requester,
            CompGiantRig giantComp,
            GiantAquaticDef aquaticDef)
        {
            bool canCrush = GiantDestructionUtility.CanGiantCrushAnything(
                giantComp,
                out bool allowBuildings,
                out bool allowRock,
                out bool allowTerrain);
            int factionId = canCrush && requester?.Faction != null ? requester.Faction.loadID : -1;
            bool protectAllied = canCrush
                && giantComp.RigDef.protectAlliedStructures
                && requester?.Faction != null
                && !(requester.mindState?.mentalStateHandler != null && requester.InAggroMentalState);
            int wallClearance = Mathf.Clamp(giantComp?.RigDef?.coreClearanceRadius ?? 0, 0, 4);

            GiantAquaticCustomizerKey key = new GiantAquaticCustomizerKey(
                aquaticDef,
                wallClearance,
                factionId,
                allowBuildings,
                allowRock,
                allowTerrain,
                protectAllied);

            if (!aquaticCustomizers.TryGetValue(key, out GiantAquaticPathCustomizer aqCust))
            {
                aqCust = new GiantAquaticPathCustomizer(map.cellIndices.NumGridCells);
                RecomputeAquaticBuffers(key, aqCust, requester);
                aqCust.SyncIfDirty();
                aquaticCustomizers[key] = aqCust;
                aquaticKeysByCustomizer[aqCust] = key;
            }
            return aqCust;
        }

        private void RecomputeAquaticBuffers(
            GiantAquaticCustomizerKey key,
            GiantAquaticPathCustomizer cust,
            Pawn sampleGiant)
        {
            ushort[] offsets = cust.ManagedOffsets;
            int[] directCosts = cust.ManagedDirectCosts;
            Array.Clear(offsets, 0, offsets.Length);

            IntVec3 size = map.Size;
            CellIndices indices = map.cellIndices;
            TerrainGrid tg = map.terrainGrid;
            Faction keyFaction = ResolveFactionByLoadId(key.factionLoadId);
            bool anyCrush = key.AllowCrushBuildings || key.AllowCrushNaturalRock || key.AllowCrushTerrain;

            // Pass 1: Evaluate base terrain & edifice costs per cell
            for (int z = 0; z < size.z; z++)
            {
                for (int x = 0; x < size.x; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    int idx = indices.CellToIndex(x, z);
                    TerrainDef top = tg != null ? tg.TerrainAt(c) : null;
                    TerrainDef under = (top != null && top.bridge) ? tg.UnderTerrainAt(c) : null;
                    GiantAquaticTerrainInfo tInfo = GiantAquaticUtility.ClassifyTerrain(top, under);

                    int terrainCost = GiantAquaticUtility.EvaluateBaseTerrainCost(
                        key.mode,
                        key.capabilityFlags,
                        key.waterDirectPathCost,
                        key.landExtraPathCost,
                        in tInfo,
                        out bool isEligibleWater);

                    // Bridge crush override: if WaterOnly is blocked by bridges over eligible water, allow terrain-crushing rigs to smash bridges
                    if (terrainCost >= GiantDestructionUtility.ImpassablePathCost
                        && key.mode == GiantAquaticMode.WaterOnly
                        && tInfo.isBridge
                        && tInfo.hasUnderWater
                        && key.AllowCrushTerrain
                        && under != null
                        && GiantAquaticUtility.IsTerrainEligibleWaterByFlags(under, null, key.capabilityFlags))
                    {
                        terrainCost = Mathf.Min(GiantDestructionUtility.ImpassablePathCost - 1, key.waterDirectPathCost + 120);
                        isEligibleWater = true;
                    }

                    if (terrainCost >= GiantDestructionUtility.ImpassablePathCost)
                    {
                        directCosts[idx] = GiantDestructionUtility.ImpassablePathCost;
                        offsets[idx] = GiantDestructionUtility.ImpassablePathCost;
                        continue;
                    }

                    Building edifice = c.GetEdifice(map);
                    int finalCost = terrainCost;
                    bool hardBlockedByEdifice = false;

                    if (edifice != null)
                    {
                        if (edifice.def.passability == Traversability.Impassable)
                        {
                            if (anyCrush)
                            {
                                GiantDestructionUtility.TryEvaluateGiantCell(
                                    map,
                                    c,
                                    keyFaction,
                                    key.AllowCrushBuildings,
                                    key.AllowCrushNaturalRock,
                                    key.AllowCrushTerrain,
                                    key.ProtectAllied,
                                    sampleGiant,
                                    GiantDestructionUtility.ImpassablePathCost,
                                    out bool isHardBlocked,
                                    out _,
                                    out int breachCost);
                                if (isHardBlocked || breachCost >= GiantDestructionUtility.ImpassablePathCost)
                                {
                                    hardBlockedByEdifice = true;
                                    finalCost = GiantDestructionUtility.ImpassablePathCost;
                                }
                                else
                                {
                                    finalCost = Mathf.Min(GiantDestructionUtility.ImpassablePathCost - 1, terrainCost + breachCost);
                                }
                            }
                            else
                            {
                                hardBlockedByEdifice = true;
                                finalCost = GiantDestructionUtility.ImpassablePathCost;
                            }
                        }
                        else
                        {
                            finalCost = Mathf.Min(GiantDestructionUtility.ImpassablePathCost - 1, terrainCost + Mathf.Max(0, edifice.def.pathCost));
                        }
                    }

                    directCosts[idx] = finalCost;
                    if (hardBlockedByEdifice || finalCost >= GiantDestructionUtility.ImpassablePathCost)
                    {
                        offsets[idx] = GiantDestructionUtility.ImpassablePathCost;
                    }
                }
            }

            // Pass 2: Enforce hard minWaterClearance (for WaterOnly) and soft shoreline / wall avoidance buffers
            int hardDraft = key.mode == GiantAquaticMode.WaterOnly ? key.minWaterClearance : 0;
            int softShoreR = key.mode == GiantAquaticMode.WaterOnly ? key.shoreAvoidanceRadius : 0;
            ushort softShoreCost = key.shoreAvoidanceExtraCost;
            int wallR = key.wallClearanceRadius;

            if (hardDraft <= 0 && softShoreR <= 0 && wallR <= 0)
            {
                return;
            }

            // Snapshot raw passability before dilating hardWaterClearance so dilation doesn't cascade recursively
            bool[] nonWaterOrBlocked = hardDraft > 0 ? new bool[offsets.Length] : null;
            if (nonWaterOrBlocked != null)
            {
                for (int i = 0; i < offsets.Length; i++)
                {
                    nonWaterOrBlocked[i] = directCosts[i] >= GiantDestructionUtility.ImpassablePathCost;
                }
            }

            for (int z = 0; z < size.z; z++)
            {
                for (int x = 0; x < size.x; x++)
                {
                    int idx = indices.CellToIndex(x, z);
                    bool wasImpassable = nonWaterOrBlocked != null
                        ? nonWaterOrBlocked[idx]
                        : directCosts[idx] >= GiantDestructionUtility.ImpassablePathCost;
                    if (!wasImpassable)
                    {
                        continue;
                    }

                    if (hardDraft > 0)
                    {
                        int minX = Mathf.Max(0, x - hardDraft);
                        int maxX = Mathf.Min(size.x - 1, x + hardDraft);
                        int minZ = Mathf.Max(0, z - hardDraft);
                        int maxZ = Mathf.Min(size.z - 1, z + hardDraft);
                        for (int nz = minZ; nz <= maxZ; nz++)
                        {
                            for (int nx = minX; nx <= maxX; nx++)
                            {
                                int nIdx = indices.CellToIndex(nx, nz);
                                directCosts[nIdx] = GiantDestructionUtility.ImpassablePathCost;
                                offsets[nIdx] = GiantDestructionUtility.ImpassablePathCost;
                            }
                        }
                    }

                    int softR = Mathf.Max(softShoreR, wallR);
                    if (softR > 0)
                    {
                        ushort penalty = key.mode == GiantAquaticMode.WaterOnly
                            ? (softShoreCost > 0 ? softShoreCost : (ushort)45)
                            : (ushort)650;
                        int minX = Mathf.Max(0, x - softR);
                        int maxX = Mathf.Min(size.x - 1, x + softR);
                        int minZ = Mathf.Max(0, z - softR);
                        int maxZ = Mathf.Min(size.z - 1, z + softR);
                        for (int nz = minZ; nz <= maxZ; nz++)
                        {
                            for (int nx = minX; nx <= maxX; nx++)
                            {
                                int nIdx = indices.CellToIndex(nx, nz);
                                if (offsets[nIdx] < penalty)
                                {
                                    offsets[nIdx] = penalty;
                                }
                            }
                        }
                    }
                }
            }
        }

        public bool CanAquaticReach(
            Pawn giant,
            CompGiantRig giantComp,
            IntVec3 start,
            LocalTargetInfo dest,
            Verse.AI.PathEndMode peMode)
        {
            if (disposed || giant == null || giantComp?.RigDef == null || !dest.IsValid || !start.InBounds(map) || !dest.Cell.InBounds(map))
            {
                return false;
            }

            GiantAquaticDef aqDef = giantComp.RigDef.ActiveAquaticLocomotion;
            if (aqDef == null || !aqDef.IsActive)
            {
                return false;
            }

            EnsureArraysInitialized();
            if (disposed)
            {
                return false;
            }
            if (aquaticDirty)
            {
                RefreshDirtyAquaticManagedBuffers();
            }

            GiantAquaticPathCustomizer aqCust = GetOrCreateAquaticCustomizer(giant, giantComp, aqDef);
            int[] directCosts = aqCust.ManagedDirectCosts;
            CellIndices indices = map.cellIndices;
            IntVec3 destCell = dest.Cell;
            int destIdx = indices.CellToIndex(destCell);

            if (peMode == Verse.AI.PathEndMode.OnCell && directCosts[destIdx] >= GiantDestructionUtility.ImpassablePathCost)
            {
                return false;
            }

            int startIdx = indices.CellToIndex(start);
            if (aqDef.IsWaterOnly && directCosts[startIdx] >= GiantDestructionUtility.ImpassablePathCost)
            {
                // If somehow tested from an invalid cell, only allow reaching adjacent valid water
                if (!GiantAquaticUtility.IsCellPassableForAquatic(map, start, giant, giantComp, aqDef))
                {
                    return false;
                }
            }

            int numCells = indices.NumGridCells;
            if (breachBfsVisitedStamp == null || breachBfsVisitedStamp.Length != numCells)
            {
                breachBfsVisitedStamp = new int[numCells];
                breachBfsCurrentStamp = 0;
            }
            breachBfsCurrentStamp++;
            if (breachBfsCurrentStamp == int.MaxValue)
            {
                Array.Clear(breachBfsVisitedStamp, 0, breachBfsVisitedStamp.Length);
                breachBfsCurrentStamp = 1;
            }
            int stamp = breachBfsCurrentStamp;

            CellRect goalRect;
            if (peMode == Verse.AI.PathEndMode.Touch || peMode == Verse.AI.PathEndMode.ClosestTouch || peMode == Verse.AI.PathEndMode.InteractionCell)
            {
                CompGiantRig targetComp = dest.HasThing
                    ? (GiantFastLookup.TryGetRig(dest.Thing as Pawn)
                        ?? HarmonyPatches_CombatAndHitbox.TryGetEnabledCorpseRig(dest.Thing))
                    : null;
                CellRect baseRect = targetComp != null && targetComp.Footprint.hitCells.Count > 0
                    ? targetComp.Footprint.tightHitBounds
                    : (dest.HasThing ? dest.Thing.OccupiedRect() : CellRect.FromCell(destCell));
                int expandRadius = Mathf.Max(
                    1,
                    Mathf.CeilToInt(giantComp.GetEffectiveMeleeTouchRadius()) + giantComp.Footprint.maxCollisionHorizontalExtent);
                goalRect = baseRect.ExpandedBy(expandRadius).ClipInsideMap(map);
            }
            else
            {
                goalRect = CellRect.FromCell(destCell);
            }

            int sizeX = map.Size.x;
            int sizeZ = map.Size.z;
            breachBfsQueue.Clear();
            breachBfsVisitedStamp[startIdx] = stamp;
            breachBfsQueue.Enqueue(startIdx);

            bool found = false;
            while (breachBfsQueue.Count > 0)
            {
                int curIdx = breachBfsQueue.Dequeue();
                IntVec3 cur = indices.IndexToCell(curIdx);
                if (goalRect.Contains(cur) && directCosts[curIdx] < GiantDestructionUtility.ImpassablePathCost)
                {
                    found = true;
                    break;
                }

                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = cur.z + dz;
                    if ((uint)nz >= (uint)sizeZ) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cur.x + dx;
                        if ((uint)nx >= (uint)sizeX) continue;
                        int nIdx = nz * sizeX + nx;
                        if (breachBfsVisitedStamp[nIdx] == stamp) continue;
                        if (directCosts[nIdx] >= GiantDestructionUtility.ImpassablePathCost) continue;
                        if (dx != 0 && dz != 0)
                        {
                            int ortho1 = cur.z * sizeX + nx;
                            int ortho2 = nz * sizeX + cur.x;
                            if (directCosts[ortho1] >= GiantDestructionUtility.ImpassablePathCost
                                || directCosts[ortho2] >= GiantDestructionUtility.ImpassablePathCost)
                            {
                                continue;
                            }
                        }
                        breachBfsVisitedStamp[nIdx] = stamp;
                        breachBfsQueue.Enqueue(nIdx);
                    }
                }
            }
            breachBfsQueue.Clear();
            return found;
        }

        private void RecomputeWallClearanceBuffer(int radius, ushort[] buffer)
        {
            Array.Clear(buffer, 0, buffer.Length);
            IntVec3 size = map.Size;
            CellIndices indices = map.cellIndices;

            for (int z = 0; z < size.z; z++)
            {
                for (int x = 0; x < size.x; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (!IsHardWallOrImpassableTerrain(c))
                    {
                        continue;
                    }

                    int minX = Mathf.Max(0, x - radius);
                    int maxX = Mathf.Min(size.x - 1, x + radius);
                    int minZ = Mathf.Max(0, z - radius);
                    int maxZ = Mathf.Min(size.z - 1, z + radius);
                    for (int nz = minZ; nz <= maxZ; nz++)
                    {
                        for (int nx = minX; nx <= maxX; nx++)
                        {
                            int nIdx = indices.CellToIndex(nx, nz);
                            // High penalty near walls so giant creatures stay centered in wide corridors/open ground
                            buffer[nIdx] = 950;
                        }
                    }
                }
            }
        }

        private bool IsHardWallOrImpassableTerrain(IntVec3 c)
        {
            TerrainDef terrain = map.terrainGrid.TerrainAt(c);
            if (terrain != null && terrain.passability == Traversability.Impassable)
            {
                return true;
            }
            Building ed = c.GetEdifice(map);
            if (ed != null && ed.def.passability == Traversability.Impassable && !(ed is Building_Door))
            {
                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldPawnIgnoreGiantCollision(Pawn pawn)
        {
            return pawn != null && ShouldIgnoreCollisionPawnState(pawn.Flying, pawn.Downed, pawn.Dead);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldIgnoreCollisionPawnState(bool isFlying, bool isDowned, bool isDead)
        {
            return isFlying || isDowned || isDead;
        }

        public bool IsBlockedForPawn(IntVec3 cell, Pawn mover)
        {
            if (!HasAnyGiants || collisionGiantsByCell == null || !cell.InBounds(map))
            {
                return false;
            }
            if (ShouldPawnIgnoreGiantCollision(mover))
            {
                return false;
            }

            CompGiantRig moverComp = GiantFastLookup.TryGetRig(mover);
            if (moverComp == null)
            {
                return GetBlockingGiantForSmallPawnAt(cell, mover) != null;
            }
            if (moverComp.IsCurrentlyAirborne)
            {
                return false;
            }

            if ((moverComp.RigDef != null && !moverComp.RigDef.enableGiantToGiantCollision) || giantsList.Count < 2)
            {
                return false;
            }

            IntVec3 curAnchor = mover.Position;
            return FindBlockingGiantForGiantHullAt(moverComp, curAnchor, cell, out _, out _) != null;
        }

        public bool IsStandBlockedForPawn(IntVec3 cell, Pawn mover)
        {
            if (!HasAnyGiants || !cell.InBounds(map) || ShouldPawnIgnoreGiantCollision(mover))
            {
                return false;
            }
            CompGiantRig moverComp = GiantFastLookup.TryGetRig(mover);
            if (moverComp == null)
            {
                return GetPawnExclusionGiantForSmallPawnAt(cell, mover) != null
                    || GetBlockingGiantForSmallPawnAt(cell, mover) != null;
            }
            return IsBlockedForPawn(cell, mover);
        }

        public CompGiantRig GetPawnExclusionGiantForSmallPawnAt(IntVec3 cell, Pawn mover)
        {
            if (!HasAnyGiants || pawnExclusionGiantsByCell == null || !cell.InBounds(map))
            {
                return null;
            }
            if (ShouldPawnIgnoreGiantCollision(mover))
            {
                return null;
            }
            List<CompGiantRig> owners = pawnExclusionGiantsByCell[map.cellIndices.CellToIndex(cell)];
            if (owners == null || owners.Count == 0)
            {
                return null;
            }
            for (int i = 0; i < owners.Count; i++)
            {
                CompGiantRig owner = owners[i];
                Pawn op = owner?.Pawn;
                if (op != null && op != mover && op.Spawned && !op.Dead && !op.Downed &&
                    !owner.IsCurrentlyAirborne && !owner.IsFlyingEntry)
                {
                    return owner;
                }
            }
            return null;
        }

        public CompGiantRig GetBlockingGiantForSmallPawnAt(IntVec3 cell, Pawn mover)
        {
            if (!HasAnyGiants || collisionGiantsByCell == null || !cell.InBounds(map))
            {
                return null;
            }
            if (ShouldPawnIgnoreGiantCollision(mover))
            {
                return null;
            }
            List<CompGiantRig> owners = collisionGiantsByCell[map.cellIndices.CellToIndex(cell)];
            if (owners == null || owners.Count == 0)
            {
                return null;
            }
            for (int i = 0; i < owners.Count; i++)
            {
                CompGiantRig owner = owners[i];
                Pawn op = owner?.Pawn;
                if (op != null && op != mover && op.Spawned && !op.Dead && !op.Downed &&
                    !owner.IsCurrentlyAirborne && !owner.IsFlyingEntry)
                {
                    return owner;
                }
            }
            return null;
        }

        public int CountGiantHullOverlapAt(
            CompGiantRig moverComp,
            IntVec3 anchorCell,
            CompGiantRig ignoreComp = null)
        {
            if (moverComp == null || (moverComp.Pawn != null && ShouldPawnIgnoreGiantCollision(moverComp.Pawn)) ||
                (moverComp.RigDef != null && !moverComp.RigDef.enableGiantToGiantCollision) ||
                !HasAnyGiants || collisionGiantsByCell == null || giantsList.Count < 2)
            {
                return 0;
            }

            List<IntVec3> offsets = moverComp.Footprint.localCollisionOffsets;
            int offsetCount = offsets.Count;
            IntVec3 mapSize = map.Size;
            CellIndices indices = map.cellIndices;
            int overlap = 0;

            if (offsetCount == 0)
            {
                if (!anchorCell.InBounds(map))
                {
                    return 0;
                }
                List<CompGiantRig> owners = collisionGiantsByCell[indices.CellToIndex(anchorCell)];
                if (owners == null)
                {
                    return 0;
                }
                for (int j = 0; j < owners.Count; j++)
                {
                    CompGiantRig o = owners[j];
                    if (o != moverComp && o != ignoreComp && o?.Pawn != null && o.Pawn.Spawned && !o.Pawn.Dead && !o.Pawn.Downed &&
                        !o.IsCurrentlyAirborne && !o.IsFlyingEntry &&
                        (o.RigDef == null || o.RigDef.enableGiantToGiantCollision))
                    {
                        overlap++;
                    }
                }
                return overlap;
            }

            for (int i = 0; i < offsetCount; i++)
            {
                IntVec3 c = anchorCell + offsets[i];
                if ((uint)c.x >= (uint)mapSize.x || (uint)c.z >= (uint)mapSize.z)
                {
                    continue;
                }
                List<CompGiantRig> owners = collisionGiantsByCell[indices.CellToIndex(c)];
                if (owners == null || owners.Count == 0)
                {
                    continue;
                }
                for (int j = 0; j < owners.Count; j++)
                {
                    CompGiantRig o = owners[j];
                    if (o != moverComp && o != ignoreComp && o?.Pawn != null && o.Pawn.Spawned && !o.Pawn.Dead && !o.Pawn.Downed &&
                        !o.IsCurrentlyAirborne && !o.IsFlyingEntry &&
                        (o.RigDef == null || o.RigDef.enableGiantToGiantCollision))
                    {
                        overlap++;
                    }
                }
            }
            return overlap;
        }

        public CompGiantRig FindBlockingGiantForGiantHullAt(
            CompGiantRig moverComp,
            IntVec3 currentAnchor,
            IntVec3 candidateAnchor,
            out int candidateOverlap,
            out int currentOverlap)
        {
            candidateOverlap = 0;
            currentOverlap = 0;
            if (moverComp == null || (moverComp.Pawn != null && ShouldPawnIgnoreGiantCollision(moverComp.Pawn)) ||
                !HasAnyGiants || collisionGiantsByCell == null || giantsList.Count < 2)
            {
                return null;
            }
            if (moverComp.RigDef != null && !moverComp.RigDef.enableGiantToGiantCollision)
            {
                return null;
            }

            List<IntVec3> offsets = moverComp.Footprint.localCollisionOffsets;
            IntVec3 mapSize = map.Size;
            CellIndices indices = map.cellIndices;
            CompGiantRig closestBlocker = null;
            float closestDistSq = float.MaxValue;

            int count = offsets.Count > 0 ? offsets.Count : 1;
            for (int i = 0; i < count; i++)
            {
                IntVec3 c = offsets.Count > 0 ? (candidateAnchor + offsets[i]) : candidateAnchor;
                if ((uint)c.x >= (uint)mapSize.x || (uint)c.z >= (uint)mapSize.z)
                {
                    continue;
                }
                List<CompGiantRig> owners = collisionGiantsByCell[indices.CellToIndex(c)];
                if (owners == null || owners.Count == 0)
                {
                    continue;
                }
                for (int j = 0; j < owners.Count; j++)
                {
                    CompGiantRig o = owners[j];
                    Pawn op = o?.Pawn;
                    if (o == moverComp || op == null || !op.Spawned || op.Dead || op.Downed ||
                        o.IsCurrentlyAirborne || o.IsFlyingEntry ||
                        (o.RigDef != null && !o.RigDef.enableGiantToGiantCollision))
                    {
                        continue;
                    }
                    candidateOverlap++;
                    float dSq = (op.Position - candidateAnchor).LengthHorizontalSquared;
                    if (closestBlocker == null || dSq < closestDistSq)
                    {
                        closestBlocker = o;
                        closestDistSq = dSq;
                    }
                }
            }

            if (candidateOverlap == 0 || closestBlocker == null)
            {
                return null;
            }

            currentOverlap = CountGiantHullOverlapAt(moverComp, currentAnchor);
            if (!GiantHullCollisionUtility.ShouldBlockOverlapMove(candidateOverlap, currentOverlap))
            {
                return null;
            }

            return closestBlocker;
        }

        public bool ShouldAllowFlankRepathTick(Pawn pawn, int nowTick, int cooldownTicks = 24)
        {
            if (pawn == null)
            {
                return false;
            }
            if (lastGiantFlankRepathTick.TryGetValue(pawn.thingIDNumber, out int lastTick) && nowTick - lastTick < cooldownTicks)
            {
                return false;
            }
            lastGiantFlankRepathTick[pawn.thingIDNumber] = nowTick;
            return true;
        }

        public int ComputeTotalPairwiseCollisionOverlapCells(out float minCenterDist)
        {
            minCenterDist = -1f;
            if (!HasAnyGiants || giantsList.Count < 2)
            {
                return 0;
            }

            float minDist = float.MaxValue;
            int totalOverlap = 0;
            for (int i = 0; i < giantsList.Count; i++)
            {
                CompGiantRig compA = giantsList[i];
                Pawn pA = compA?.Pawn;
                if (compA == null || pA == null || !pA.Spawned || pA.Dead || pA.Downed || pA.Flying || compA.IsFlyingEntry || compA.IsCurrentlyAirborne)
                {
                    continue;
                }

                GiantFootprintData fpA = compA.Footprint;
                Vector3 posA = CompGiantRig.GetSafeDrawPos(pA);

                for (int j = i + 1; j < giantsList.Count; j++)
                {
                    CompGiantRig compB = giantsList[j];
                    Pawn pB = compB?.Pawn;
                    if (compB == null || pB == null || !pB.Spawned || pB.Dead || pB.Downed || pB.Flying || compB.IsFlyingEntry || compB.IsCurrentlyAirborne)
                    {
                        continue;
                    }

                    Vector3 delta = posA - CompGiantRig.GetSafeDrawPos(pB);
                    delta.y = 0f;
                    float dist = delta.magnitude;
                    if (dist < minDist)
                    {
                        minDist = dist;
                    }

                    GiantFootprintData fpB = compB.Footprint;
                    if (!fpA.tightCollisionBounds.Overlaps(fpB.tightCollisionBounds))
                    {
                        continue;
                    }

                    List<IntVec3> colsA = fpA.collisionCells;
                    HashSet<IntVec3> setB = fpB.collisionCellSet;
                    for (int k = 0; k < colsA.Count; k++)
                    {
                        if (setB.Contains(colsA[k]))
                        {
                            totalOverlap++;
                        }
                    }
                }
            }

            minCenterDist = minDist < float.MaxValue ? minDist : -1f;
            return totalOverlap;
        }

        private void ResolveGiantOverlapsTick()
        {
            if (!HasAnyGiants || giantsList.Count < 2)
            {
                return;
            }
            if (Find.TickManager == null || (Find.TickManager.TicksGame % 2) != 0)
            {
                return;
            }

            overlapMovedThisTick.Clear();
            bool anyMoved = false;

            for (int i = 0; i < giantsList.Count; i++)
            {
                CompGiantRig compA = giantsList[i];
                Pawn pA = compA?.Pawn;
                if (compA == null || pA == null || !pA.Spawned || pA.Dead || pA.Downed || pA.Flying || compA.IsFlyingEntry || compA.IsCurrentlyAirborne ||
                    (compA.RigDef != null && !compA.RigDef.enableGiantToGiantCollision))
                {
                    continue;
                }

                for (int j = i + 1; j < giantsList.Count; j++)
                {
                    CompGiantRig compB = giantsList[j];
                    Pawn pB = compB?.Pawn;
                    if (compB == null || pB == null || !pB.Spawned || pB.Dead || pB.Downed || pB.Flying || compB.IsFlyingEntry || compB.IsCurrentlyAirborne ||
                        (compB.RigDef != null && !compB.RigDef.enableGiantToGiantCollision))
                    {
                        continue;
                    }

                    GiantFootprintData fpA = compA.Footprint;
                    GiantFootprintData fpB = compB.Footprint;
                    if (!fpA.tightCollisionBounds.Overlaps(fpB.tightCollisionBounds))
                    {
                        continue;
                    }

                    int pairOverlap = CountPairOverlap(fpA, fpB);
                    if (pairOverlap <= 0)
                    {
                        continue;
                    }
                    if (pairOverlap <= 4
                        && (pA.Position - pB.Position).LengthHorizontalSquared >= 9
                        && (pA.pather == null || !pA.pather.Moving)
                        && (pB.pather == null || !pB.pather.Moving)
                        && ((compA.PartCombat != null && compA.PartCombat.HasActiveContactLunge)
                            || (compB.PartCombat != null && compB.PartCombat.HasActiveContactLunge)))
                    {
                        continue;
                    }

                    Vector3 delta = CompGiantRig.GetSafeDrawPos(pA) - CompGiantRig.GetSafeDrawPos(pB);
                    delta.y = 0f;
                    if (delta.sqrMagnitude < 0.001f)
                    {
                        float angle = ((pA.thingIDNumber - pB.thingIDNumber) * 57) % 360;
                        delta = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                    }
                    Vector3 awayA = delta.normalized;

                    bool aBusy = pA.stances?.FullBodyBusy ?? false;
                    bool bBusy = pB.stances?.FullBodyBusy ?? false;
                    CompGiantRig first = (!aBusy || bBusy) ? compA : compB;
                    CompGiantRig second = first == compA ? compB : compA;
                    Vector3 awayFirst = first == compA ? awayA : -awayA;

                    if (TryStepGiantOutOfOverlap(first, awayFirst))
                    {
                        anyMoved = true;
                        pairOverlap = CountPairOverlap(compA.Footprint, compB.Footprint);
                    }
                    if (pairOverlap > 0 && TryStepGiantOutOfOverlap(second, -awayFirst))
                    {
                        anyMoved = true;
                    }
                }
            }

            if (anyMoved)
            {
                RebuildSpatialGrid();
            }
        }

        public void ResolveImmediateTouchdownOverlap(CompGiantRig landedComp)
        {
            Pawn landed = landedComp?.Pawn;
            if (landed == null || !landed.Spawned || landed.Dead || landed.Downed || !HasAnyGiants || giantsList.Count < 2)
            {
                return;
            }

            bool movedAny = false;
            overlapMovedThisTick.Clear();
            for (int step = 0; step < 3; step++)
            {
                IntVec3 curAnchor = landed.Position;
                if (CountGiantHullOverlapAt(landedComp, curAnchor) <= 0)
                {
                    break;
                }

                Vector3 awaySum = Vector3.zero;
                for (int i = 0; i < giantsList.Count; i++)
                {
                    CompGiantRig other = giantsList[i];
                    if (other == null || other == landedComp || other.Pawn == null || !other.Pawn.Spawned || other.Pawn.Dead || other.Pawn.Downed || other.IsCurrentlyAirborne)
                    {
                        continue;
                    }
                    if (CountPairOverlap(landedComp.Footprint, other.Footprint) > 0)
                    {
                        Vector3 d = CompGiantRig.GetSafeDrawPos(landed) - CompGiantRig.GetSafeDrawPos(other.Pawn);
                        d.y = 0f;
                        awaySum += d.sqrMagnitude > 0.001f ? d.normalized : Vector3.forward;
                    }
                }
                Vector3 awayDir = awaySum.sqrMagnitude > 0.001f ? awaySum.normalized : Vector3.forward;
                overlapMovedThisTick.Clear();
                if (!TryStepGiantOutOfOverlap(landedComp, awayDir))
                {
                    break;
                }
                movedAny = true;
            }

            if (movedAny)
            {
                RebuildSpatialGrid();
            }
        }

        private static int CountPairOverlap(GiantFootprintData fpA, GiantFootprintData fpB)
        {
            if (fpA == null || fpB == null || !fpA.tightCollisionBounds.Overlaps(fpB.tightCollisionBounds))
            {
                return 0;
            }
            int overlap = 0;
            List<IntVec3> colsA = fpA.collisionCells;
            HashSet<IntVec3> setB = fpB.collisionCellSet;
            for (int i = 0; i < colsA.Count; i++)
            {
                if (setB.Contains(colsA[i]))
                {
                    overlap++;
                }
            }
            return overlap;
        }

        private bool TryStepGiantOutOfOverlap(CompGiantRig comp, Vector3 awayDir)
        {
            Pawn pawn = comp?.Pawn;
            if (pawn == null || !pawn.Spawned || overlapMovedThisTick.Contains(comp))
            {
                return false;
            }

            IntVec3 curAnchor = pawn.Position;
            int currentTotalOverlap = CountGiantHullOverlapAt(comp, curAnchor);
            if (currentTotalOverlap <= 0)
            {
                return false;
            }

            if (!TryFindBestGiantSeparationStep(comp, awayDir, currentTotalOverlap, out IntVec3 bestStep))
            {
                return false;
            }

            bool wasMoving = pawn.pather != null && pawn.pather.Moving;
            pawn.Position = bestStep;
            if (pawn.pather != null)
            {
                if (wasMoving && pawn.pather.Destination.IsValid)
                {
                    pawn.pather.ResetToCurrentPosition();
                }
                else
                {
                    pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: false);
                }
            }
            pawn.Drawer?.tweener?.ResetTweenedPosToRoot();
            comp.RebuildFootprintNow(notifyMap: false);
            RebuildSpatialGrid();
            overlapMovedThisTick.Add(comp);
            return true;
        }

        private bool TryFindBestGiantSeparationStep(
            CompGiantRig comp,
            Vector3 awayDir,
            int currentTotalOverlap,
            out IntVec3 bestCell)
        {
            bestCell = IntVec3.Invalid;
            Pawn pawn = comp?.Pawn;
            if (pawn == null)
            {
                return false;
            }

            float bestScore = float.MaxValue;
            IntVec3 origin = pawn.Position;

            for (int k = 0; k < 8; k++)
            {
                IntVec3 cand = origin + GenAdj.AdjacentCells[k];
                if (!cand.InBounds(map)
                    || (GiantDestructionUtility.HasThickRoofAt(map, cand) && !cand.Walkable(map))
                    || !cand.StandableBy(map, pawn)
                    || IsHardWallOrImpassableTerrain(cand)
                    || !cand.WalkableBy(map, pawn))
                {
                    continue;
                }

                int overlapAfter = CountGiantHullOverlapAt(comp, cand);
                if (overlapAfter >= currentTotalOverlap)
                {
                    continue;
                }

                Vector3 stepDir = (cand - origin).ToVector3();
                stepDir.y = 0f;
                float align = stepDir.sqrMagnitude > 0.001f ? Vector3.Dot(stepDir.normalized, awayDir) : 0f;
                float score = overlapAfter * 100f - align;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestCell = cand;
                }
            }

            return bestCell.IsValid;
        }

        public CompGiantRig GetCollisionOwnerAt(IntVec3 cell)
        {
            if (!HasAnyGiants || collisionOwnerByCell == null || !cell.InBounds(map))
            {
                return null;
            }
            return collisionOwnerByCell[map.cellIndices.CellToIndex(cell)];
        }

        public List<CompGiantRig> GetHitGiantsAt(IntVec3 cell)
        {
            if (!HasAnyGiants || hitGiantsByCell == null || !cell.InBounds(map))
            {
                return null;
            }
            return hitGiantsByCell[map.cellIndices.CellToIndex(cell)];
        }

        public List<CompGiantRig> GetCorpseGiantsAt(IntVec3 cell)
        {
            if (!HasAnyCorpseGiants || corpseGiantsByCell == null || !cell.InBounds(map))
            {
                return null;
            }
            return corpseGiantsByCell[map.cellIndices.CellToIndex(cell)];
        }

        private void PushOverlappingPawnsOutward(CompGiantRig giantComp)
        {
            Pawn giant = giantComp?.Pawn;
            if (giant == null || !giant.Spawned || giant.Dead || giant.Downed || giantComp.IsCurrentlyAirborne || giantComp.IsFlyingEntry)
            {
                return;
            }
            GiantFootprintData fp = giantComp.Footprint;
            for (int i = 0; i < fp.collisionCells.Count; i++)
            {
                IntVec3 cell = fp.collisionCells[i];
                if (!cell.InBounds(map)) continue;

                List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
                for (int t = things.Count - 1; t >= 0; t--)
                {
                    if (things[t] is Pawn other && other != giant && other.Spawned && !ShouldPawnIgnoreGiantCollision(other))
                    {
                        if (GiantFastLookup.TryGetRig(other) != null)
                        {
                            continue;
                        }
                        RelocateOverlappingSmallPawn(giantComp, other, cell);
                    }
                }
            }

            for (int i = 0; i < fp.pawnExclusionCells.Count; i++)
            {
                IntVec3 cell = fp.pawnExclusionCells[i];
                if (!cell.InBounds(map) || fp.collisionCellSet.Contains(cell))
                {
                    continue;
                }

                List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
                for (int t = things.Count - 1; t >= 0; t--)
                {
                    if (things[t] is Pawn other && other != giant && other.Spawned && !ShouldPawnIgnoreGiantCollision(other))
                    {
                        if (GiantFastLookup.TryGetRig(other) != null)
                        {
                            continue;
                        }
                        // Allow actively moving pawns to slip through narrow chokepoints beneath overhanging wings/barrels;
                        // only push out stationary, drafted-parked, or fighting pawns so they never stand hidden under the sprite,
                        // while deferring canopy push-out during a stationary giant's transient melee lunge onto its target.
                        if (other.pather != null && other.pather.Moving)
                        {
                            continue;
                        }
                        if (ShouldDeferCanopyPushDuringMeleeStrike(giantComp, other))
                        {
                            continue;
                        }
                        RelocateOverlappingSmallPawn(giantComp, other, cell);
                    }
                }
            }
        }

        private void RelocateOverlappingSmallPawn(CompGiantRig giantComp, Pawn other, IntVec3 originCell)
        {
            if (!TryFindNearestPushOutCell(giantComp, other, originCell, out IntVec3 safeCell))
            {
                return;
            }
            bool wasMoving = other.pather != null && other.pather.Moving && other.pather.Destination.IsValid;
            bool preserveActiveMeleeOnGiant = !wasMoving
                && other.CurJob?.def == JobDefOf.AttackMelee
                && other.CurJob.targetA.HasThing
                && other.CurJob.targetA.Thing == giantComp.Pawn
                && GiantHullInteractionUtility.IsCellTouchingGiantHitbox(safeCell, giantComp.Pawn);

            other.Position = safeCell;
            if (wasMoving)
            {
                // Skip Notify_Teleported so moving stays true and ResetToCurrentPosition queues a fresh path.
                other.pather.ResetToCurrentPosition();
            }
            else if (preserveActiveMeleeOnGiant)
            {
                other.Drawer?.tweener?.ResetTweenedPosToRoot();
            }
            else
            {
                other.Notify_Teleported(endCurrentJob: false, resetTweenedPos: false);
            }
        }

        private void PushStationaryCanopyPawnsTick()
        {
            if (!HasAnyGiants || activePawnExclusionCellCount <= 0 || Find.TickManager == null || (Find.TickManager.TicksGame % 6) != 0)
            {
                return;
            }
            for (int g = 0; g < giantsList.Count; g++)
            {
                CompGiantRig comp = giantsList[g];
                Pawn giant = comp?.Pawn;
                if (giant == null || !giant.Spawned || giant.Dead || giant.Downed || comp.IsCurrentlyAirborne || comp.IsFlyingEntry)
                {
                    continue;
                }
                if (comp.RigDef != null && !comp.RigDef.enableCollisionPawnPushOut)
                {
                    continue;
                }
                GiantFootprintData fp = comp.Footprint;
                for (int i = 0; i < fp.pawnExclusionCells.Count; i++)
                {
                    IntVec3 cell = fp.pawnExclusionCells[i];
                    if (!cell.InBounds(map))
                    {
                        continue;
                    }
                    bool isCoreSolid = fp.collisionCellSet.Contains(cell);
                    List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
                    for (int t = things.Count - 1; t >= 0; t--)
                    {
                        if (things[t] is Pawn other
                            && other != giant
                            && other.Spawned
                            && !ShouldPawnIgnoreGiantCollision(other)
                            && GiantFastLookup.TryGetRig(other) == null
                            && (other.pather == null || !other.pather.Moving))
                        {
                            if (!isCoreSolid && ShouldDeferCanopyPushDuringMeleeStrike(comp, other))
                            {
                                continue;
                            }
                            RelocateOverlappingSmallPawn(comp, other, cell);
                        }
                    }
                }
            }
        }

        private static bool ShouldDeferCanopyPushDuringMeleeStrike(CompGiantRig giantComp, Pawn other)
        {
            if (giantComp == null || other == null || giantComp.IsActivelyLocomoting)
            {
                return false;
            }
            if (giantComp.AnimState == GiantAnimState.AttackOnce
                && giantComp.Pawn != null
                && other.Position.DistanceToSquared(giantComp.Pawn.Position) <= 144)
            {
                return true;
            }
            return giantComp.PartCombat != null
                && giantComp.PartCombat.HasActiveMeleeStrikeOverlayEngaging(other);
        }

        private bool TryFindNearestPushOutCell(CompGiantRig giantComp, Pawn other, IntVec3 origin, out IntVec3 bestCell)
        {
            Vector3 giantCenter = CompGiantRig.GetSafeDrawPos(giantComp.Pawn);
            Vector3 pushDir = (origin.ToVector3Shifted() - giantCenter);
            pushDir.y = 0f;
            if (pushDir.sqrMagnitude < 0.001f)
            {
                pushDir = Vector3.forward;
            }
            pushDir.Normalize();

            District originDistrict = origin.GetDistrict(map);
            bool originIsOpenDoor = origin.GetDoor(map) is Building_Door originDoor && originDoor.Open;
            bool meleeingThisGiant = other?.CurJob?.def == JobDefOf.AttackMelee
                && other.CurJob.targetA.HasThing
                && other.CurJob.targetA.Thing == giantComp.Pawn;
            GiantFootprintData fp = giantComp.Footprint;

            for (int pass = 0; pass < 2; pass++)
            {
                bool enforceCanopyClear = pass == 0;
                float bestScore = float.MaxValue;
                bestCell = IntVec3.Invalid;

                for (int r = 1; r <= 6; r++)
                {
                    int count = GenRadial.NumCellsInRadius(r);
                    for (int i = 1; i < count; i++)
                    {
                        IntVec3 cand = origin + GenRadial.RadialPattern[i];
                        if (!cand.InBounds(map) || cand.Fogged(map))
                        {
                            continue;
                        }
                        if (enforceCanopyClear ? IsStandBlockedForPawn(cand, other) : IsBlockedForPawn(cand, other))
                        {
                            continue;
                        }
                        if (enforceCanopyClear ? !cand.StandableBy(map, other) : !cand.WalkableBy(map, other))
                        {
                            continue;
                        }
                        Building_Door candDoor = cand.GetDoor(map);
                        if (candDoor != null && !candDoor.Open)
                        {
                            continue;
                        }
                        if (originDistrict != null && !originIsOpenDoor && cand.GetDistrict(map) != originDistrict)
                        {
                            continue;
                        }
                        if (!GenSight.LineOfSight(origin, cand, map, skipFirstCell: true))
                        {
                            continue;
                        }
                        Vector3 offset = (cand - origin).ToVector3();
                        float dist = offset.magnitude;
                        float align = Vector3.Dot(offset.normalized, pushDir);
                        float score = dist - align * 0.35f;
                        if (!fp.hitCellSet.Contains(cand))
                        {
                            score -= 0.45f;
                        }
                        if (meleeingThisGiant && fp.IsCellStrictExteriorTouch(cand))
                        {
                            score -= 0.85f;
                        }
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestCell = cand;
                        }
                    }
                    if (bestCell.IsValid)
                    {
                        return true;
                    }
                }
            }

            bestCell = IntVec3.Invalid;
            return false;
        }

        private void TickCorpseGiants()
        {
            if (corpseGiantsList.Count == 0)
            {
                return;
            }

            int now = Current.Game?.tickManager?.TicksGame ?? 0;
            bool anyFootprintDirty = false;

            for (int g = corpseGiantsList.Count - 1; g >= 0; g--)
            {
                CompGiantRig comp = corpseGiantsList[g];
                Pawn pawn = comp?.Pawn;
                Corpse corpse = pawn?.Corpse;
                bool invalidOrGone = comp == null
                    || pawn == null
                    || !pawn.Dead
                    || (corpse != null && (corpse.Destroyed || !corpse.Spawned || corpse.Map != map))
                    || (corpse == null && now > comp.DeathStartedGameTick + 1);

                if (invalidOrGone)
                {
                    if (comp != null)
                    {
                        activeCorpseGiants.Remove(comp);
                        corpseLastPosition.Remove(comp);
                    }
                    corpseGiantsList.RemoveAt(g);
                    anyFootprintDirty = true;
                    continue;
                }

                if (corpse != null)
                {
                    IntVec3 curPos = corpse.Position;
                    if (!corpseLastPosition.TryGetValue(comp, out IntVec3 lastPos) || lastPos != curPos)
                    {
                        corpseLastPosition[comp] = curPos;
                        comp.RebuildFootprintNow(notifyMap: false);
                        anyFootprintDirty = true;
                    }
                }

                if (comp.TickCorpseDeathAnimation(1f, out bool footprintUpdated) && footprintUpdated)
                {
                    anyFootprintDirty = true;
                }
            }

            if (anyFootprintDirty || (Current.Game?.tickManager != null && now % 30 == 0))
            {
                RebuildCorpseSpatialGrid();
            }
        }

        private void DrawOccludedSmallPawnSilhouettes()
        {
            if (!HasAnyGiants || Find.CurrentMap != map || !DebugViewSettings.drawThingsDynamic)
            {
                return;
            }

            CellRect viewRect = Find.CameraDriver != null
                ? Find.CameraDriver.CurrentViewRect.ExpandedBy(1)
                : CellRect.WholeMap(map);
            viewRect.ClipInsideMap(map);

            occludedSilhouetteDrawnScratch.Clear();
            for (int g = 0; g < giantsList.Count; g++)
            {
                CompGiantRig comp = giantsList[g];
                Pawn giant = comp?.Pawn;
                if (giant == null || !giant.Spawned || giant.Dead || giant.Downed || comp.IsCurrentlyAirborne || comp.IsFlyingEntry)
                {
                    continue;
                }
                if (comp.RigDef != null && !comp.RigDef.enablePawnOcclusionSilhouette)
                {
                    continue;
                }
                GiantFootprintData fp = comp.Footprint;
                if (fp.hitCells.Count == 0 || !viewRect.Overlaps(fp.tightHitBounds))
                {
                    continue;
                }

                for (int i = 0; i < fp.hitCells.Count; i++)
                {
                    IntVec3 hc = fp.hitCells[i].cell;
                    if (!hc.InBounds(map) || !viewRect.Contains(hc) || hc.Fogged(map))
                    {
                        continue;
                    }
                    List<Thing> things = map.thingGrid.ThingsListAtFast(hc);
                    for (int t = 0; t < things.Count; t++)
                    {
                        if (!(things[t] is Pawn smallPawn)
                            || smallPawn == giant
                            || !smallPawn.Spawned
                            || smallPawn.Dead
                            || smallPawn.Flying
                            || smallPawn.IsHiddenFromPlayer()
                            || GiantFastLookup.TryGetRig(smallPawn) != null
                            || !occludedSilhouetteDrawnScratch.Add(smallPawn))
                        {
                            continue;
                        }

                        PawnRenderer renderer = smallPawn.Drawer?.renderer;
                        if (renderer?.SilhouetteGraphic == null || SilhouetteUtility.ShouldDrawSilhouette(smallPawn))
                        {
                            continue;
                        }

                        Vector3 drawPos = smallPawn.DrawPos;
                        if (fp.TryHitTestPoint(comp.RigDef, comp.AlphaMasksSpan, drawPos, out _, comp.SoftWhipAlphaMasksSpan))
                        {
                            SilhouetteUtility.DrawGraphicSilhouette(smallPawn, renderer.SilhouettePos);
                        }
                    }
                }
            }
            occludedSilhouetteDrawnScratch.Clear();
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            CompAbilityEffect_AbyssalQuake.TickPendingCrossingDetonations(map);
            GiantVfxSystem.TickMapProceduralVfx(map);
            GiantSeveredDebrisSystem.TickMapDebris(map);
            ResolveGiantOverlapsTick();
            PushStationaryCanopyPawnsTick();
            TickCorpseGiants();
            if (Find.TickManager != null && Find.TickManager.Paused && giantsList.Count > 0)
            {
                for (int i = 0; i < giantsList.Count; i++)
                {
                    giantsList[i]?.Pawn?.Drawer?.tweener?.ResetTweenedPosToRoot();
                }
            }
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (liveFpsSamplingActive && Find.CurrentMap == map)
            {
                RecordLiveFrameDelta(Time.unscaledDeltaTime);
            }
            GiantVfxSystem.DrawMapProceduralVfx(map);
            GiantSeveredDebrisSystem.DrawMapDebris(map);
            if (!HasAnyGiants)
            {
                return;
            }
            DrawOccludedSmallPawnSilhouettes();

            GiantCreatureFrameworkSettings settings = GiantCreatureFrameworkMod.Settings;
            bool globalCol = GlobalDebugDrawCollisionCells || settings.drawCollisionCells || GlobalDebugDrawFootprints;
            bool globalHit = GlobalDebugDrawHitCells || settings.drawHitCells || GlobalDebugDrawFootprints;
            bool globalSkel = GlobalDebugDrawSkeleton || settings.drawSkeleton;

            for (int i = 0; i < giantsList.Count; i++)
            {
                CompGiantRig comp = giantsList[i];
                if (comp.Pawn == null || !comp.Pawn.Spawned)
                {
                    continue;
                }

                bool selected = Find.Selector != null && Find.Selector.IsSelected(comp.Pawn);
                bool perCompFp = comp.DebugDrawFootprint || (selected && comp.DebugDrawOnSelect);
                bool drawCol = globalCol || perCompFp;
                bool drawHit = globalHit || perCompFp;
                if (drawCol)
                {
                    comp.DrawCollisionCellsOverlay();
                }
                if (drawHit)
                {
                    comp.DrawHitCellsOverlay(excludeCollisionCells: drawCol);
                }
                if (globalSkel || comp.DebugDrawSkeleton)
                {
                    comp.DrawSkeletonOverlay();
                }
            }
        }

        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            if (giantsList.Count == 0 && !GiantArenaService.IsArenaActive && !bossHealthBarHud.HasVisibleEntries)
            {
                return;
            }
            bossHealthBarHud.DrawOnGUI(map, giantsList);
        }
    }
}
