using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public sealed class GiantWorldRenderer : IDisposable
    {
        // Strictly above WorldTerrain (3500), Hills (3510), Roads (3540), WorldObjects (3550),
        // SelectedTile (3560), WorldLines (3590), Caravans (3600), FeatureNames (3610), and strictly below Clouds (3700).
        public const int WorldGiantMinRenderQueue = WorldMaterials.FeatureNameRenderQueue + 1; // 3611
        public const int WorldGiantMaxRenderQueue = 3695;
        private const int MaxDistinctRenderQueues = WorldGiantMaxRenderQueue - WorldGiantMinRenderQueue + 1; // 85

        private readonly struct WorldPartEntry
        {
            public readonly PawnGraphicDrawRequest Request;
            public readonly float Layer;
            public readonly bool IsSilhouette;
            public readonly int OriginalOrder;
            public readonly int LayerRank;

            public WorldPartEntry(PawnGraphicDrawRequest request, float layer, bool isSilhouette, int originalOrder, int layerRank)
            {
                Request = request;
                Layer = layer;
                IsSilhouette = isSilhouette;
                OriginalOrder = originalOrder;
                LayerRank = layerRank;
            }
        }

        private readonly struct WorldMaterialKey : IEquatable<WorldMaterialKey>
        {
            public readonly Material Source;
            public readonly int RenderQueue;

            public WorldMaterialKey(Material source, int renderQueue)
            {
                Source = source;
                RenderQueue = renderQueue;
            }

            public bool Equals(WorldMaterialKey other) => ReferenceEquals(Source, other.Source) && RenderQueue == other.RenderQueue;
            public override bool Equals(object obj) => obj is WorldMaterialKey other && Equals(other);
            public override int GetHashCode() => (Source != null ? Source.GetHashCode() : 0) * 397 ^ RenderQueue;
        }

        private static readonly AccessTools.FieldRef<PawnRenderTree, List<PawnGraphicDrawRequest>> DrawRequests =
            AccessTools.FieldRefAccess<PawnRenderTree, List<PawnGraphicDrawRequest>>("drawRequests");
        private static readonly Comparison<WorldPartEntry> PartSortComparison = CompareWorldParts;
        private static readonly HashSet<GiantWorldRenderer> ActiveWorldRenderers = new();

        public static void ClearAllWorldMaterialCaches()
        {
            if (ActiveWorldRenderers.Count == 0)
            {
                return;
            }
            var snapshot = new List<GiantWorldRenderer>(ActiveWorldRenderers);
            ActiveWorldRenderers.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                snapshot[i]?.DestroyWorldMaterials();
            }
        }

        private readonly WorldObject_Giant owner;
        private PawnDrawParms parms;
        private readonly List<WorldPartEntry> preparedParts = new(64);
        private int preparedMaxRank;
        private uint preparedVersion = uint.MaxValue;
        private GiantRigDef preparedRig;
        private readonly Dictionary<Mesh, (Vector3[] vertices, Vector2[] uvs, int[] triangles)> staticMeshCache = new();
        private readonly Dictionary<Mesh, (Vector3[] vertices, Vector2[] uvs, int[] triangles)> dynamicMeshCache = new();
        private readonly Dictionary<WorldMaterialKey, Material> worldMaterialCache = new();
        private readonly Vector2[] brackets = new Vector2[4];

        public GiantWorldRenderer(WorldObject_Giant owner) { this.owner = owner; }

        public float CurrentBaseAltitude
        {
            get
            {
                float camDist = ResolveCameraDistance();
                float step = ComputeWorldLayerStep(camDist, SystemInfo.usesReversedZBuffer);
                bool isFlying = owner != null && WorldObject_Giant.CanFlyOnWorld(owner.Pawn);
                int id = owner != null ? owner.ID : 0;
                return ComputeGiantBaseAltitude(id, isFlying, Math.Max(1, preparedMaxRank + 1), step);
            }
        }

        public static void GetBasis(Vector3 position, out Vector3 east, out Vector3 normal, out Vector3 north)
        {
            normal = position.normalized;
            Vector3 pole = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.999f ? Vector3.forward : Vector3.up;
            north = (pole - normal * Vector3.Dot(normal, pole)).normalized;
            east = Vector3.Cross(normal, north).normalized;
        }

        public static Matrix4x4 BuildWorldMatrix(Vector3 center, Vector3 origin, float scale, float altitude)
        {
            Vector3 relative = center - origin;
            GetBasis(relative, out _, out Vector3 normal, out Vector3 north);
            return Matrix4x4.TRS(center + normal * altitude, Quaternion.LookRotation(north, normal), Vector3.one * scale);
        }

        /// <summary>
        /// Computes a camera-distance-safe normal separation between adjacent giant part layers on the planet sphere,
        /// staying far above both planet-radius (~100) float32 vertex quantization (7.6e-6) and perspective Z-buffer steps.
        /// </summary>
        public static float ComputeWorldLayerStep(float cameraDistance, bool usesReversedZBuffer)
        {
            float d = Mathf.Clamp(cameraDistance, 20f, 1200f);
            if (usesReversedZBuffer)
            {
                // 32-bit float reversed-Z (near=2, far=7500): depth quantum ~1.2e-7*d; float32 ULP at R=100 is 7.6e-6.
                // Maintain >= 98 ULPs and >= 65x depth quantum across all zoom levels.
                return Mathf.Clamp(d * 0.000008f, 0.00075f, 0.012f);
            }
            // Standard 24-bit fixed-point perspective Z (near=2): depth quantum ~d^2 / (2 * 2^24) = 3.0e-8*d^2.
            return Mathf.Clamp(d * d * 0.00000012f, 0.0010f, 0.035f);
        }

        /// <summary>
        /// Computes a strictly monotonic WorldLayer render queue in [3611..3695] (above WorldTerrain/Hills/Roads/
        /// WorldObjects/SelectedTile/Caravans/FeatureNames and strictly below Clouds=3700).
        /// </summary>
        public static int ComputePartRenderQueue(int rank, int maxRank)
        {
            if (rank <= 0)
            {
                return WorldGiantMinRenderQueue;
            }
            int effectiveMax = Math.Max(1, maxRank);
            if (effectiveMax < MaxDistinctRenderQueues)
            {
                return WorldGiantMinRenderQueue + rank;
            }
            int mapped = Mathf.RoundToInt(((float)rank / effectiveMax) * (MaxDistinctRenderQueues - 1));
            return Mathf.Clamp(WorldGiantMinRenderQueue + mapped, WorldGiantMinRenderQueue, WorldGiantMaxRenderQueue);
        }

        /// <summary>
        /// Computes a grounded or aerial planet surface base altitude with disjoint per-entity bands so
        /// overlapping WorldObject_Giant instances never Z-fight or interleave.
        /// </summary>
        public static float ComputeGiantBaseAltitude(int worldObjectId, bool isFlying, int rankCount, float layerStep)
        {
            float stackHeight = Math.Max(1, rankCount) * Mathf.Max(0.0005f, layerStep);
            float slotBand = Mathf.Max(0.035f, stackHeight + 0.006f);
            int slot = (worldObjectId & 3) + (isFlying ? 4 : 0);
            // Ground slot 0 sits at 0.050 (just above Caravan's 0.035 and WorldObject's 0.030..0.040).
            return 0.050f + slot * slotBand;
        }

        /// <summary>
        /// Projects a single part's local XZ matrix onto the planet tangent plane at (baseAltitude + partNormalLift),
        /// stripping map-space local Y so scale (~0.07) never crushes inter-layer normal separation into Z-fighting.
        /// </summary>
        public static Matrix4x4 BuildPartWorldMatrix(
            Matrix4x4 baseWorldMatrix,
            Vector3 surfaceNormal,
            float partNormalLift,
            Matrix4x4 localPartMatrix)
        {
            localPartMatrix.m13 = 0f;
            Matrix4x4 mat = baseWorldMatrix * localPartMatrix;
            mat.m03 += surfaceNormal.x * partNormalLift;
            mat.m13 += surfaceNormal.y * partNormalLift;
            mat.m23 += surfaceNormal.z * partNormalLift;
            return mat;
        }

        private static int CompareWorldParts(WorldPartEntry a, WorldPartEntry b)
        {
            int silCmp = a.IsSilhouette == b.IsSilhouette ? 0 : (a.IsSilhouette ? -1 : 1);
            if (silCmp != 0) return silCmp;
            int layerCmp = a.Layer.CompareTo(b.Layer);
            if (layerCmp != 0) return layerCmp;
            return a.OriginalOrder.CompareTo(b.OriginalOrder);
        }

        private static bool IsSilhouettePart(PawnRenderNode node)
        {
            if (node is PawnRenderNode_GiantBone bone) return bone.IsSilhouetteUnderlay;
            if (node is PawnRenderNode_GiantSkinnedMesh skin) return skin.IsSilhouetteUnderlay;
            return false;
        }

        private float ResolveCameraDistance()
        {
            Camera cam = Find.WorldCamera;
            if (cam == null || owner == null)
            {
                return 60f;
            }
            return Vector3.Distance(cam.transform.position, owner.DrawPos);
        }

        private Material ResolveWorldMaterial(Material sourceMat, int targetQueue)
        {
            if (sourceMat == null)
            {
                return null;
            }
            var key = new WorldMaterialKey(sourceMat, targetQueue);
            if (worldMaterialCache.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }
            Material clone = new Material(sourceMat)
            {
                renderQueue = targetQueue
            };
            worldMaterialCache[key] = clone;
            ActiveWorldRenderers.Add(this);
            return clone;
        }

        private void DestroyWorldMaterials()
        {
            ActiveWorldRenderers.Remove(this);
            if (worldMaterialCache.Count == 0)
            {
                return;
            }
            if (UnityData.IsInMainThread)
            {
                foreach (Material mat in worldMaterialCache.Values)
                {
                    if (mat != null)
                    {
                        UnityEngine.Object.Destroy(mat);
                    }
                }
            }
            worldMaterialCache.Clear();
        }

        private void Prepare()
        {
            Pawn pawn = owner.Pawn;
            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            if (rig?.RigDef == null)
            {
                preparedParts.Clear();
                preparedMaxRank = 0;
                return;
            }
            if (!rig.WorldSimulationActive) rig.BeginWorldSimulation();
            PawnRenderTree tree = pawn.Drawer.renderer.renderTree;
            GiantRenderSnapshot snapshot = rig.LatestSnapshot;
            if (preparedRig == rig.RigDef && preparedVersion == (snapshot?.version ?? 0u) && preparedParts.Count > 0) return;

            if (preparedRig != rig.RigDef)
            {
                DestroyWorldMaterials();
            }

            parms = PawnDrawParms.DefaultFor(pawn);
            parms.matrix = Matrix4x4.identity;
            parms.facing = Rot4.North;
            parms.posture = PawnPosture.Standing;
            tree.EnsureInitialized(parms.flags);
            rig.FlushPendingDynamicMeshesOnMainThread(ignoreViewportCull: true);
            if (tree.rootNode != null) tree.rootNode.requestRecache = true;
            tree.ParallelPreDraw(parms);

            // Copy out of PawnRenderTree.drawRequests immediately so PortraitsCache renders on the same pawn
            // can never overwrite our world-map matrices mid-frame.
            List<PawnGraphicDrawRequest> raw = DrawRequests(tree);
            preparedParts.Clear();
            int nonSilCount = 0;
            if (raw != null)
            {
                for (int i = 0; i < raw.Count; i++)
                {
                    PawnGraphicDrawRequest req = raw[i];
                    if (!IsGiantPart(req.node) || req.material == null || req.mesh == null) continue;
                    bool isSil = IsSilhouettePart(req.node);
                    float layer = req.node.Worker != null ? req.node.Worker.LayerFor(req.node, parms) : 0f;
                    if (!isSil) nonSilCount++;
                    preparedParts.Add(new WorldPartEntry(req, layer, isSil, i, 0));
                }
            }

            preparedParts.Sort(PartSortComparison);

            // Assign deterministic monotonic ranks: all silhouettes share rank 0 (solid outline underlay);
            // each non-silhouette part gets a strictly ascending rank so neither depth-buffer ties nor
            // transparent perspective-distance sorting can ever flip or flicker overlapping parts.
            bool mergeEqualNonSilLayers = nonSilCount >= MaxDistinctRenderQueues;
            int currentRank = 0;
            float prevLayer = float.NegativeInfinity;
            bool hasNonSil = false;
            for (int i = 0; i < preparedParts.Count; i++)
            {
                WorldPartEntry e = preparedParts[i];
                int rank;
                if (e.IsSilhouette)
                {
                    rank = 0;
                }
                else if (!hasNonSil)
                {
                    hasNonSil = true;
                    currentRank = 1;
                    prevLayer = e.Layer;
                    rank = currentRank;
                }
                else if (mergeEqualNonSilLayers && Mathf.Abs(e.Layer - prevLayer) <= 1e-4f)
                {
                    rank = currentRank;
                }
                else
                {
                    currentRank++;
                    prevLayer = e.Layer;
                    rank = currentRank;
                }
                preparedParts[i] = new WorldPartEntry(e.Request, e.Layer, e.IsSilhouette, e.OriginalOrder, rank);
            }

            preparedMaxRank = currentRank;
            preparedRig = rig.RigDef;
            preparedVersion = snapshot?.version ?? 0u;
            dynamicMeshCache.Clear();
        }

        public void Draw()
        {
            Prepare();
            if (preparedParts.Count == 0) return;

            // Perform material cache eviction strictly BEFORE submitting any Graphics.DrawMesh calls for this frame.
            if (worldMaterialCache.Count >= 256)
            {
                DestroyWorldMaterials();
            }

            Vector3 center = owner.DrawPos;
            Vector3 origin = owner.Tile.Layer.Origin;
            GetBasis(center - origin, out _, out Vector3 normal, out Vector3 north);
            float camDist = ResolveCameraDistance();
            float layerStep = ComputeWorldLayerStep(camDist, SystemInfo.usesReversedZBuffer);
            bool isFlying = WorldObject_Giant.CanFlyOnWorld(owner.Pawn);
            float baseAlt = ComputeGiantBaseAltitude(owner.ID, isFlying, preparedMaxRank + 1, layerStep);
            Matrix4x4 baseWorld = Matrix4x4.TRS(center + normal * baseAlt, Quaternion.LookRotation(north, normal), Vector3.one * owner.WorldUnitsPerCell);

            for (int i = 0; i < preparedParts.Count; i++)
            {
                WorldPartEntry entry = preparedParts[i];
                PawnGraphicDrawRequest request = entry.Request;
                if (request.material == null || request.mesh == null) continue;
                int queue = ComputePartRenderQueue(entry.LayerRank, preparedMaxRank);
                Material worldMat = ResolveWorldMaterial(request.material, queue);
                if (worldMat == null) continue;

                request.node.Worker.PreDraw(request.node, worldMat, parms);
                MaterialPropertyBlock block = request.node.Worker.GetMaterialPropertyBlock(request.node, worldMat, parms);
                foreach (PawnRenderSubWorker worker in request.node.Props.SubWorkers)
                    worker.EditMaterialPropertyBlock(request.node, worldMat, parms, ref block);

                Matrix4x4 partWorld = BuildPartWorldMatrix(baseWorld, normal, entry.LayerRank * layerStep, request.preDrawnComputedMatrix);
                Graphics.DrawMesh(request.mesh, partWorld, worldMat, WorldCameraManager.WorldLayer, null, 0, block);
                block?.Clear();
            }
        }

        private static bool IsGiantPart(PawnRenderNode node) => node is PawnRenderNode_GiantBone
            || node is PawnRenderNode_GiantSoftWhip || node is PawnRenderNode_GiantSkinnedMesh
            || node is PawnRenderNode_GiantRollingTrack || node is PawnRenderNode_GiantTrackWheel;

        public Rect ScreenBounds()
        {
            Prepare();
            if (preparedParts.Count == 0) return new Rect();

            Vector3 center = owner.DrawPos;
            Vector3 origin = owner.Tile.Layer.Origin;
            GetBasis(center - origin, out _, out Vector3 normal, out Vector3 north);
            float camDist = ResolveCameraDistance();
            float layerStep = ComputeWorldLayerStep(camDist, SystemInfo.usesReversedZBuffer);
            bool isFlying = WorldObject_Giant.CanFlyOnWorld(owner.Pawn);
            float baseAlt = ComputeGiantBaseAltitude(owner.ID, isFlying, preparedMaxRank + 1, layerStep);
            Matrix4x4 baseWorld = Matrix4x4.TRS(center + normal * baseAlt, Quaternion.LookRotation(north, normal), Vector3.one * owner.WorldUnitsPerCell);

            bool any = false;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int r = 0; r < preparedParts.Count; r++)
            {
                WorldPartEntry entry = preparedParts[r];
                PawnGraphicDrawRequest request = entry.Request;
                if (request.mesh == null || request.material == null) continue;
                Matrix4x4 transform = BuildPartWorldMatrix(baseWorld, normal, entry.LayerRank * layerStep, request.preDrawnComputedMatrix);
                Bounds bounds = request.mesh.bounds;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 local = bounds.center + new Vector3((i & 1) == 0 ? -bounds.extents.x : bounds.extents.x,
                        0f, (i & 2) == 0 ? -bounds.extents.z : bounds.extents.z);
                    Vector2 p = GenWorldUI.WorldToUIPosition(transform.MultiplyPoint3x4(local));
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                    any = true;
                }
            }
            return any ? Rect.MinMaxRect(minX, minY, maxX, maxY) : new Rect();
        }

        public bool HitTest(Vector2 screenMouse)
        {
            Prepare();
            if (preparedParts.Count == 0) return false;
            Ray ray = Find.WorldCamera.ScreenPointToRay(screenMouse * Prefs.UIScale);

            Vector3 center = owner.DrawPos;
            Vector3 origin = owner.Tile.Layer.Origin;
            GetBasis(center - origin, out _, out Vector3 normal, out Vector3 north);
            float camDist = ResolveCameraDistance();
            float layerStep = ComputeWorldLayerStep(camDist, SystemInfo.usesReversedZBuffer);
            bool isFlying = WorldObject_Giant.CanFlyOnWorld(owner.Pawn);
            float baseAlt = ComputeGiantBaseAltitude(owner.ID, isFlying, preparedMaxRank + 1, layerStep);
            Matrix4x4 baseWorld = Matrix4x4.TRS(center + normal * baseAlt, Quaternion.LookRotation(north, normal), Vector3.one * owner.WorldUnitsPerCell);

            for (int r = preparedParts.Count - 1; r >= 0; r--)
            {
                WorldPartEntry entry = preparedParts[r];
                PawnGraphicDrawRequest request = entry.Request;
                if (request.material == null || request.mesh == null) continue;
                Matrix4x4 transform = BuildPartWorldMatrix(baseWorld, normal, entry.LayerRank * layerStep, request.preDrawnComputedMatrix);
                Matrix4x4 inverse = transform.inverse;
                Vector3 localOrigin = inverse.MultiplyPoint3x4(ray.origin);
                Vector3 localDirection = inverse.MultiplyVector(ray.direction);
                if (Mathf.Abs(localDirection.y) < 1e-9f) continue;
                float distance = -localOrigin.y / localDirection.y;
                if (distance < 0f) continue;
                Vector3 hit = localOrigin + localDirection * distance;
                Bounds localBounds = request.mesh.bounds;
                if (hit.x < localBounds.min.x || hit.x > localBounds.max.x
                    || hit.z < localBounds.min.z || hit.z > localBounds.max.z)
                {
                    continue;
                }
                bool isDynamic = request.node is PawnRenderNode_GiantSkinnedMesh
                    || request.node is PawnRenderNode_GiantSoftWhip
                    || request.node is PawnRenderNode_GiantRollingTrack;
                var cache = isDynamic ? dynamicMeshCache : staticMeshCache;
                if (!cache.TryGetValue(request.mesh, out var mesh))
                {
                    mesh = (request.mesh.vertices, request.mesh.uv, request.mesh.triangles);
                    cache[request.mesh] = mesh;
                }
                string path = PartTexturePath(request.node);
                TextureAlphaMask alpha = !string.IsNullOrEmpty(path) ? GiantAlphaMaskCache.GetOrLoad(path) : null;
                for (int i = 0; i + 2 < mesh.triangles.Length; i += 3)
                {
                    int a = mesh.triangles[i], b = mesh.triangles[i + 1], c = mesh.triangles[i + 2];
                    if (!TryTriangle(new Vector2(hit.x, hit.z), mesh.vertices[a], mesh.vertices[b], mesh.vertices[c], out Vector3 weights)) continue;
                    Vector2 uv = mesh.uvs[a] * weights.x + mesh.uvs[b] * weights.y + mesh.uvs[c] * weights.z;
                    if (alpha == null || alpha.IsOpaqueUV(Mathf.Repeat(uv.x, 1f), Mathf.Repeat(uv.y, 1f))) return true;
                }
            }
            return false;
        }

        private string PartTexturePath(PawnRenderNode node)
        {
            if (node == null || owner?.Pawn == null)
            {
                return null;
            }
            CompGiantRig rig = GiantFastLookup.TryGetRig(owner.Pawn);
            if (node is PawnRenderNode_GiantBone bone)
            {
                if (bone.BoneDef == null)
                {
                    return null;
                }
                GiantBoneViewSector sector = rig != null
                    ? rig.GetBoneSectorThreadSafe(bone.BoneDef.runtimeIndex, false)
                    : GiantBoneViewSector.Dorsal;
                return bone.ResolveActiveTexPath(sector);
            }
            if (node is PawnRenderNode_GiantSoftWhip whip)
            {
                return whip.WhipDef?.texPath;
            }
            if (node is PawnRenderNode_GiantSkinnedMesh skin)
            {
                return skin.ResolveActiveTexPath();
            }
            if (node is PawnRenderNode_GiantRollingTrack track)
            {
                GiantRollingTrackDef tDef = track.TrackDef;
                if (tDef == null)
                {
                    return null;
                }
                GiantBoneViewSector sector = (rig != null && tDef.resolvedParentBoneIndex >= 0)
                    ? rig.GetBoneSectorThreadSafe(tDef.resolvedParentBoneIndex, false)
                    : GiantBoneViewSector.Dorsal;
                return sector == GiantBoneViewSector.Dorsal
                    ? tDef.dorsalRibbonTexPath
                    : (!string.IsNullOrEmpty(tDef.lateralRibbonTexPath) ? tDef.lateralRibbonTexPath : tDef.dorsalRibbonTexPath);
            }
            if (node is PawnRenderNode_GiantTrackWheel wheel)
            {
                return wheel.WheelDef?.texPath;
            }
            return null;
        }

        public static bool TryTriangle(Vector2 p, Vector3 av, Vector3 bv, Vector3 cv, out Vector3 weights)
        {
            Vector2 a = new Vector2(av.x, av.z), b = new Vector2(bv.x, bv.z), c = new Vector2(cv.x, cv.z);
            Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
            float determinant = v0.x * v1.y - v1.x * v0.y;
            if (Mathf.Abs(determinant) < 1e-10f) { weights = default; return false; }
            float u = (v2.x * v1.y - v1.x * v2.y) / determinant;
            float v = (v0.x * v2.y - v2.x * v0.y) / determinant;
            weights = new Vector3(1f - u - v, u, v);
            return weights.x >= 0f && weights.y >= 0f && weights.z >= 0f;
        }

        public void DrawSelection()
        {
            Rect bounds = ScreenBounds();
            if (bounds.width <= 0f) return;
            SelectionDrawerUtility.CalculateSelectionBracketPositionsUI(brackets, (WorldObject)owner, bounds,
                WorldSelectionDrawer.SelectTimes,
                new Vector2(SelectionDrawerUtility.SelectedTexGUI.width * 0.4f, SelectionDrawerUtility.SelectedTexGUI.height * 0.4f), 25f);
            for (int i = 0; i < 4; i++)
                Widgets.DrawTextureRotated(brackets[i], SelectionDrawerUtility.SelectedTexGUI, 90f + i * 90f, 0.4f);
        }

        public void Dispose()
        {
            DestroyWorldMaterials();
            staticMeshCache.Clear();
            dynamicMeshCache.Clear();
            preparedParts.Clear();
            preparedMaxRank = 0;
        }
    }
}
