using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Anatomical capsule segment in flat bind-pose rig XZ used to compute smooth scale-free
    /// multi-bone Linear Blend Skinning weights. Segments sharing the same (groupId ?? boneId)
    /// evaluate minimum normalized point-to-segment distance before applying weightMultiplier.
    /// </summary>
    public class GiantSkinCapsuleDef
    {
        public string boneId;
        public string groupId;
        public Vector2 startXZ;
        public Vector2 endXZ;
        public float radius = 0.40f;
        public float weightMultiplier = 1.0f;

        [Unsaved(false)] public int resolvedBoneIndex = -1;
    }

    /// <summary>
    /// Declarative multi-bone 2D Linear Blend Skinning (LBS) mesh definition inside a GiantRigDef.
    /// Supports both standalone quad placement (anchorBoneId + drawSize) and flat bind-pose world-bounds
    /// layer atlases (worldBoundsXZ + memberBoneIds + capsules), as well as 100% rigid SO(2) wrist-pinned
    /// hard appendages (rigidPinBoneId + pinToSkinnedMeshId).
    /// </summary>
    public class GiantSkinnedMeshDef
    {
        public string meshId;
        public string texPath;
        public string silhouetteTexPath;
        public string glowTexPath;
        public Color glowColor = Color.white;
        public ShaderTypeDef shaderTypeDef;

        /// <summary>
        /// Optional reference bone whose rest socket/pivot anchors this mesh in bind pose,
        /// and whose damage flash / HP tint / visibility state can drive material feedback.
        /// Defaults to boneIds[0] when omitted.
        /// </summary>
        public string anchorBoneId;

        /// <summary>
        /// World width (X) and height/length (Z) of the un-deformed sprite quad in map cells.
        /// Automatically populated from worldBoundsXZ when worldBoundsXZ is configured.
        /// </summary>
        public Vector2 drawSize = Vector2.one;

        /// <summary>
        /// Optional flat bind-pose world rectangle [worldMinXZ.x..worldMaxXZ.x] x [worldMinXZ.y..worldMaxXZ.y] in map cells.
        /// Uses standard RimWorld Vector2 XML syntax so DirectXmlToObject parses without custom Vector4 converters.
        /// </summary>
        public Vector2 worldMinXZ = new Vector2(-9999f, -9999f);
        public Vector2 worldMaxXZ = new Vector2(-9999f, -9999f);

        public Vector2 pivotUV = new Vector2(0.5f, 0.5f);
        public Vector2 parentSocketUV = new Vector2(0.5f, 0.5f);
        public Vector2 pivotPixel = new Vector2(-1f, -1f);
        public Vector2 parentSocketPixel = new Vector2(-1f, -1f);
        public float baseAngle = 0f;

        public float layerOffset = 0f;
        public float silhouetteLayerOffset = -9.2f;
        public float glowLayerOffset = 0.05f;

        public int textureResolution = 512;
        public byte alphaThreshold = 16;
        public int subdivisionsX = 20;
        public int subdivisionsY = 20;
        public float weightSharpness = 3.0f;
        public int maxInfluences = 4;
        public bool cullSeveredBoneTriangles = true;

        /// <summary>
        /// Optional BodyPartDef link. When destroyed and hideWhenMissing is true, hides the entire skinned mesh.
        /// </summary>
        public BodyPartDef linkedBodyPart;
        public string linkedBodyPartCustomLabel;
        public int linkedBodyPartIndex = -1;
        public bool hideWhenMissing = true;

        /// <summary>
        /// Ordered list of BoneNodeDef.nodeId names that deform this mesh via Linear Blend Skinning.
        /// </summary>
        public List<string> boneIds = new List<string>();

        /// <summary>
        /// Optional list of BoneNodeDef.nodeId names baked into this continuous skinned layer.
        /// Suppresses duplicate rigid PawnRenderNode_GiantBone sprites when suppressMemberBoneSprites is true
        /// while preserving 1:1 bone hitboxes, partAttacks, flight lift, and per-bone triangle severing.
        /// </summary>
        public List<string> memberBoneIds = new List<string>();
        public bool suppressMemberBoneSprites = true;

        /// <summary>
        /// Optional explicit anatomical capsule segments in flat bind-pose XZ for smooth LBS weights.
        /// </summary>
        public List<GiantSkinCapsuleDef> capsules = new List<GiantSkinCapsuleDef>();

        /// <summary>
        /// Optional hard-appendage boneId (e.g. wing_thumb_claw_left) + target skinned meshId (e.g. Wing_Arm_Left).
        /// Locks this layer as a 100% rigid SO(2) Euclidean sprite (zero bending/squash/shear) whose pivot rides
        /// with 0.0 px drift on pinToSkinnedMeshId's deformed socket.
        /// </summary>
        public string rigidPinBoneId;
        public string pinToSkinnedMeshId;

        [Unsaved(false)] public int runtimeIndex = -1;
        [Unsaved(false)] public int resolvedAnchorBoneIndex = -1;
        [Unsaved(false)] public int[] resolvedBoneIndices = Array.Empty<int>();
        [Unsaved(false)] public int[] resolvedMemberBoneIndices = Array.Empty<int>();
        [Unsaved(false)] public int resolvedRigidPinBoneIndex = -1;
        [Unsaved(false)] public int resolvedPinTargetMeshIndex = -1;
        [Unsaved(false)] public Vector2 resolvedPivotUV = new Vector2(0.5f, 0.5f);
        [Unsaved(false)] public Vector2 resolvedParentSocketUV = new Vector2(0.5f, 0.5f);

        public bool HasWorldBoundsXZ =>
            worldMinXZ.x > -9000f
            && worldMaxXZ.x > worldMinXZ.x + 1e-4f
            && worldMaxXZ.y > worldMinXZ.y + 1e-4f;

        public IEnumerable<string> ConfigErrors(string rigName, Dictionary<string, int> nodeLookup)
        {
            string prefix = $"[GiantRigDef:{rigName}] SkinnedMesh '{meshId ?? "<null>"}'";
            if (string.IsNullOrEmpty(meshId))
            {
                yield return $"{prefix} has empty meshId.";
            }
            if (string.IsNullOrEmpty(texPath))
            {
                yield return $"{prefix} has empty texPath.";
            }
            if (!HasWorldBoundsXZ && (drawSize.x <= 0.01f || drawSize.y <= 0.01f))
            {
                yield return $"{prefix} drawSize {drawSize} must be positive.";
            }
            if (subdivisionsX < 2 || subdivisionsX > 128 || subdivisionsY < 2 || subdivisionsY > 128)
            {
                yield return $"{prefix} subdivisions ({subdivisionsX}x{subdivisionsY}) must be in [2..128].";
            }
            if (maxInfluences < 1 || maxInfluences > 4)
            {
                yield return $"{prefix} maxInfluences ({maxInfluences}) must be in [1..4].";
            }
            if (weightSharpness <= 0.05f || weightSharpness > 16f)
            {
                yield return $"{prefix} weightSharpness ({weightSharpness}) must be in (0.05..16].";
            }

            bool hasAnyBones = (boneIds != null && boneIds.Count > 0)
                || (capsules != null && capsules.Count > 0)
                || !string.IsNullOrEmpty(rigidPinBoneId);
            if (!hasAnyBones)
            {
                yield return $"{prefix} must declare at least one bone in <boneIds>, <capsules>, or <rigidPinBoneId>.";
                yield break;
            }

            if (!string.IsNullOrEmpty(anchorBoneId) && (nodeLookup == null || !nodeLookup.ContainsKey(anchorBoneId)))
            {
                yield return $"{prefix} anchorBoneId '{anchorBoneId}' does not exist in <nodes>.";
            }
            if (!string.IsNullOrEmpty(rigidPinBoneId) && (nodeLookup == null || !nodeLookup.ContainsKey(rigidPinBoneId)))
            {
                yield return $"{prefix} rigidPinBoneId '{rigidPinBoneId}' does not exist in <nodes>.";
            }

            if (boneIds != null)
            {
                for (int i = 0; i < boneIds.Count; i++)
                {
                    string bid = boneIds[i];
                    if (string.IsNullOrEmpty(bid))
                    {
                        yield return $"{prefix} has empty entry at boneIds[{i}].";
                    }
                    else if (nodeLookup != null && !nodeLookup.ContainsKey(bid))
                    {
                        yield return $"{prefix} boneId '{bid}' does not exist in <nodes>.";
                    }
                }
            }

            if (memberBoneIds != null)
            {
                for (int i = 0; i < memberBoneIds.Count; i++)
                {
                    string mbid = memberBoneIds[i];
                    if (string.IsNullOrEmpty(mbid))
                    {
                        yield return $"{prefix} has empty entry at memberBoneIds[{i}].";
                    }
                    else if (nodeLookup != null && !nodeLookup.ContainsKey(mbid))
                    {
                        yield return $"{prefix} memberBoneId '{mbid}' does not exist in <nodes>.";
                    }
                }
            }

            if (capsules != null)
            {
                for (int i = 0; i < capsules.Count; i++)
                {
                    GiantSkinCapsuleDef cap = capsules[i];
                    if (cap == null || string.IsNullOrEmpty(cap.boneId))
                    {
                        yield return $"{prefix} has null or empty boneId at capsules[{i}].";
                    }
                    else if (nodeLookup != null && !nodeLookup.ContainsKey(cap.boneId))
                    {
                        yield return $"{prefix} capsule boneId '{cap.boneId}' does not exist in <nodes>.";
                    }
                }
            }
        }
    }

    /// <summary>
    /// Precomputed per-vertex up-to-4-bone influence weights and bind-pose coordinates
    /// for zero-GC Linear Blend Skinning evaluation.
    /// </summary>
    public struct SkinnedVertexBinding
    {
        public Vector2 uv;
        public Vector2 restRigXZ;
        public byte influenceCount;
        public int b0, b1, b2, b3;
        public float w0, w1, w2, w3;
        public Vector2 uv0, uv1, uv2, uv3;
    }

    /// <summary>
    /// Zero-GC runtime solver and mesh deformer for a GiantSkinnedMeshDef instance.
    /// Supports legacy UVToWorldXZ LBS, coupled 2x3 affine matrix LBS with scale-free capsule weights,
    /// and 100% rigid SO(2) Euclidean wrist-pinned hard appendages.
    /// </summary>
    public sealed class SkinnedMeshRuntime
    {
        private readonly GiantRigDef rigDef;
        private readonly GiantSkinnedMeshDef meshDef;

        private bool bindInitialized;
        private bool useMatrixLbs;
        private BoneWorldPose[] flatBindPoses = Array.Empty<BoneWorldPose>();
        private BoneMatrix2x3[] boneMatsScratch = Array.Empty<BoneMatrix2x3>();

        // Optional rigid SO(2) Euclidean wrist-pin state
        private bool hasRigidPin;
        private int rigidPinClawBoneIdx = -1;
        private Vector2 rigidPinBindSocketXZ;
        private int pinB0 = -1, pinB1 = -1, pinB2 = -1, pinB3 = -1;
        private float pinW0, pinW1, pinW2, pinW3;
        private byte pinInfCount;
        private float idleClawWorldAngleDeg;
        private Vector2[] deltaIdleVertXZ = Array.Empty<Vector2>();

        private struct CompactLbsVertex
        {
            public float rx, rz;
            public float w0, w1, w2, w3;
            public ushort b0, b1, b2, b3;
        }

        private SkinnedVertexBinding[] bindings = Array.Empty<SkinnedVertexBinding>();
        private CompactLbsVertex[] compactLbsVerts = Array.Empty<CompactLbsVertex>();
        private int[] usedSkinBoneIndices = Array.Empty<int>();
        private Vector2[] uvs = Array.Empty<Vector2>();
        private Vector3[] normals = Array.Empty<Vector3>();
        private int[] allTriangles = Array.Empty<int>();
        private int[] triDominantBone = Array.Empty<int>();
        private ulong[] triMemberSilMask = Array.Empty<ulong>();
        private bool hasValidMemberMasks;
        private int[] activeTriangles = Array.Empty<int>();
        private Vector3[] deformedVerts = Array.Empty<Vector3>();

        private ulong lastSeveredMaskBits = ulong.MaxValue;
        private ulong lastVisibleMemberMask = ulong.MaxValue;
        private ulong currentVisibleMemberMask;
        private bool lastRigidPinVisible;
        private bool lastMeshPartMissing;
        private bool hasPartialMemberSevering;
        private int activeTriangleIndexCount;
        private Bounds currentBounds;
        private bool currentlyVisible;

        public GiantSkinnedMeshDef MeshDef => meshDef;
        public GiantRigDef RigDef => rigDef;
        public BoneWorldPose[] FlatBindPoses => flatBindPoses;
        public bool IsBindInitialized => bindInitialized;
        public int VertexCount => bindings.Length;
        public int ActiveTriangleCount => activeTriangleIndexCount / 3;
        public bool IsCurrentlyVisible => currentlyVisible && activeTriangleIndexCount >= 3;
        public ulong CurrentVisibleMemberMask => currentVisibleMemberMask;
        public bool HasPartialMemberSevering => hasPartialMemberSevering;
        public Vector3[] DeformedVertices => deformedVerts;
        public Vector2[] UVs => uvs;
        public Vector3[] Normals => normals;
        public int[] ActiveTriangleIndices => activeTriangles;
        public int ActiveTriangleIndexCount => activeTriangleIndexCount;
        public int[] TriangleDominantBones => triDominantBone;
        public ulong[] TriangleMemberSilMasks => triMemberSilMask;
        public Bounds CurrentBounds => currentBounds;

        public SkinnedMeshRuntime(GiantRigDef rigDef, GiantSkinnedMeshDef meshDef)
        {
            this.rigDef = rigDef;
            this.meshDef = meshDef;
        }

        /// <summary>
        /// Computes bind-pose topology, culls transparent grid cells when alphaMask is available,
        /// and solves normalized multi-bone LBS weights (or rigid SO(2) pin offsets) for every active vertex.
        /// Safe to call headless in unit tests (alphaMask, rigBoneAlphaMasks, and rigBoneSilhouetteMasks may be empty).
        /// </summary>
        public void EnsureBindTopology(
            TextureAlphaMask alphaMask,
            ReadOnlySpan<TextureAlphaMask> rigBoneAlphaMasks = default,
            ReadOnlySpan<TextureAlphaMask> rigBoneSilhouetteMasks = default)
        {
            if (bindInitialized || rigDef == null || meshDef == null)
            {
                return;
            }

            BoneNodeDef[] nodes = rigDef.SortedNodes;
            int nodeCount = nodes.Length;
            int[] boneIndices = meshDef.resolvedBoneIndices;
            if (nodeCount == 0 || boneIndices == null || boneIndices.Length == 0)
            {
                bindInitialized = true;
                return;
            }

            bool hasCapsules = meshDef.capsules != null && meshDef.capsules.Count > 0;
            useMatrixLbs = hasCapsules
                || rigDef.useCoupledSagittalKinematics
                || meshDef.HasWorldBoundsXZ
                || meshDef.resolvedRigidPinBoneIndex >= 0;

            BoneWorldPose[] bindPoses = new BoneWorldPose[nodeCount];
            if (useMatrixLbs)
            {
                SkeletalKinematics.SolveFlatBindPoses(rigDef, bindPoses);
                flatBindPoses = bindPoses;
                boneMatsScratch = new BoneMatrix2x3[nodeCount];
            }
            else
            {
                SkeletalKinematics.SolveWorldPoses(
                    rigDef,
                    Vector2.zero,
                    0f,
                    ReadOnlySpan<BoneLocalPose>.Empty,
                    ReadOnlySpan<bool>.Empty,
                    bindPoses,
                    1f);
                flatBindPoses = bindPoses;
            }

            Vector2 bindJointXZ = Vector2.zero;
            float bindAngleDeg = SkeletalKinematics.NormalizeAngle360(meshDef.baseAngle);
            int anchorIdx = meshDef.resolvedAnchorBoneIndex;
            if (!meshDef.HasWorldBoundsXZ && (uint)anchorIdx < (uint)nodeCount)
            {
                ref readonly BoneWorldPose anchorPose = ref bindPoses[anchorIdx];
                if (!string.IsNullOrEmpty(meshDef.anchorBoneId))
                {
                    if (meshDef.parentSocketPixel.x >= 0f && meshDef.parentSocketPixel.y >= 0f)
                    {
                        bindJointXZ = anchorPose.UVToWorldXZFlat(meshDef.resolvedParentSocketUV.x, meshDef.resolvedParentSocketUV.y);
                    }
                    else
                    {
                        bindJointXZ = anchorPose.jointWorldXZ;
                    }
                    bindAngleDeg = SkeletalKinematics.NormalizeAngle360(anchorPose.worldAngleDeg + meshDef.baseAngle);
                }
            }

            float bindRad = bindAngleDeg * Mathf.Deg2Rad;
            float bindCos = Mathf.Cos(bindRad);
            float bindSin = Mathf.Sin(bindRad);

            // Auto bone segments for fallback mode when explicit <capsules> are not specified
            int infBoneCount = boneIndices.Length;
            Vector2[] segStart = null;
            Vector2[] segEnd = null;
            if (!hasCapsules)
            {
                segStart = new Vector2[infBoneCount];
                segEnd = new Vector2[infBoneCount];
                HashSet<int> infSet = new HashSet<int>(boneIndices);
                for (int k = 0; k < infBoneCount; k++)
                {
                    int bIdx = boneIndices[k];
                    if ((uint)bIdx >= (uint)nodeCount)
                    {
                        continue;
                    }
                    ref readonly BoneWorldPose bp = ref bindPoses[bIdx];
                    segStart[k] = bp.jointWorldXZ;

                    Vector2 childSum = Vector2.zero;
                    int childCnt = 0;
                    for (int n = 0; n < nodeCount; n++)
                    {
                        if (nodes[n].parentRuntimeIndex == bIdx && infSet.Contains(n))
                        {
                            childSum += bindPoses[n].jointWorldXZ;
                            childCnt++;
                        }
                    }

                    segEnd[k] = childCnt > 0
                        ? (childSum / childCnt)
                        : bp.UVToWorldXZFlat(0.5f, 0.85f);
                }
            }

            // Pre-group explicit capsules by (groupId ?? boneId) to match Python capsule grouping 1:1
            CapsuleGroup[] capGroups = hasCapsules ? BuildCapsuleGroups(meshDef.capsules, nodeCount) : Array.Empty<CapsuleGroup>();

            int nx = Mathf.Clamp(meshDef.subdivisionsX, 2, 128);
            int ny = Mathf.Clamp(meshDef.subdivisionsY, 2, 128);
            int strideX = nx + 1;
            int totalGridVerts = (nx + 1) * (ny + 1);

            int[] gridToCompact = new int[totalGridVerts];
            for (int i = 0; i < totalGridVerts; i++)
            {
                gridToCompact[i] = -1;
            }

            var triList = new List<int>(nx * ny * 6);
            var triDomList = new List<int>(nx * ny * 2);
            var bindList = new List<SkinnedVertexBinding>(totalGridVerts);

            int maxInf = Mathf.Clamp(meshDef.maxInfluences, 1, 4);
            float sharpness = Mathf.Clamp(meshDef.weightSharpness, 0.05f, 16f);

            int GetOrAddVertex(int gx, int gy)
            {
                int gIdx = gy * strideX + gx;
                int existing = gridToCompact[gIdx];
                if (existing >= 0)
                {
                    return existing;
                }

                float u = (float)gx / nx;
                float v = (float)gy / ny;
                Vector2 restXZ;
                if (meshDef.HasWorldBoundsXZ)
                {
                    restXZ = new Vector2(
                        Mathf.Lerp(meshDef.worldMinXZ.x, meshDef.worldMaxXZ.x, u),
                        Mathf.Lerp(meshDef.worldMinXZ.y, meshDef.worldMaxXZ.y, v));
                }
                else
                {
                    float lx = (u - meshDef.resolvedPivotUV.x) * meshDef.drawSize.x;
                    float lz = (v - meshDef.resolvedPivotUV.y) * meshDef.drawSize.y;
                    restXZ = new Vector2(
                        bindJointXZ.x + (lx * bindCos + lz * bindSin),
                        bindJointXZ.y + (-lx * bindSin + lz * bindCos));
                }

                Span<int> topB = stackalloc int[4] { -1, -1, -1, -1 };
                Span<float> topW = stackalloc float[4] { 0f, 0f, 0f, 0f };
                int found;

                if (hasCapsules)
                {
                    found = EvaluateCapsuleTopWeights(restXZ, capGroups, maxInf, topB, topW);
                }
                else
                {
                    found = 0;
                    for (int k = 0; k < infBoneCount; k++)
                    {
                        int bIdx = boneIndices[k];
                        if ((uint)bIdx >= (uint)nodeCount)
                        {
                            continue;
                        }
                        float dist = DistancePointToSegment(restXZ, segStart[k], segEnd[k]);
                        float w = 1f / Mathf.Pow(dist + 0.08f, sharpness);
                        InsertTopWeight(bIdx, w, maxInf, ref found, topB, topW);
                    }
                }

                if (found == 0)
                {
                    topB[0] = boneIndices[0];
                    topW[0] = 1f;
                    found = 1;
                }

                NormalizeTopWeights(found, topW);

                Vector2 ComputeBoneBindUV(int bIdx)
                {
                    if (useMatrixLbs || (uint)bIdx >= (uint)nodeCount)
                    {
                        return new Vector2(0.5f, 0.5f);
                    }
                    ref readonly BoneWorldPose bp = ref bindPoses[bIdx];
                    float wsX = Mathf.Max(1e-4f, bp.worldSize.x);
                    float wsY = Mathf.Max(1e-4f, bp.worldSize.y);
                    float dx = restXZ.x - bp.jointWorldXZ.x;
                    float dz = restXZ.y - bp.jointWorldXZ.y;
                    float blx = dx * bp.cosTheta - dz * bp.sinTheta;
                    float blz = dx * bp.sinTheta + dz * bp.cosTheta;
                    return new Vector2(bp.pivotUV.x + blx / wsX, bp.pivotUV.y + blz / wsY);
                }

                var binding = new SkinnedVertexBinding
                {
                    uv = new Vector2(u, v),
                    restRigXZ = restXZ,
                    influenceCount = (byte)found,
                    b0 = topB[0],
                    b1 = topB[1],
                    b2 = topB[2],
                    b3 = topB[3],
                    w0 = topW[0],
                    w1 = topW[1],
                    w2 = topW[2],
                    w3 = topW[3],
                    uv0 = ComputeBoneBindUV(topB[0]),
                    uv1 = found > 1 ? ComputeBoneBindUV(topB[1]) : Vector2.zero,
                    uv2 = found > 2 ? ComputeBoneBindUV(topB[2]) : Vector2.zero,
                    uv3 = found > 3 ? ComputeBoneBindUV(topB[3]) : Vector2.zero
                };

                int compactIdx = bindList.Count;
                bindList.Add(binding);
                gridToCompact[gIdx] = compactIdx;
                return compactIdx;
            }

            // Sort memberBoneIndices descending by flatBindPoses layerOffset for exact pixel-ownership severing
            int[] memberIndices = meshDef.resolvedMemberBoneIndices ?? Array.Empty<int>();
            int memberCount = memberIndices.Length;
            int[] sortedMembersDesc = Array.Empty<int>();
            int[] sortedMemberBitPos = Array.Empty<int>();
            if (memberCount > 0)
            {
                var order = new int[memberCount];
                for (int m = 0; m < memberCount; m++)
                {
                    order[m] = m;
                }
                Array.Sort(order, (ia, ib) => bindPoses[memberIndices[ib]].layerOffset.CompareTo(bindPoses[memberIndices[ia]].layerOffset));
                sortedMembersDesc = new int[memberCount];
                sortedMemberBitPos = new int[memberCount];
                for (int m = 0; m < memberCount; m++)
                {
                    sortedMembersDesc[m] = memberIndices[order[m]];
                    sortedMemberBitPos[m] = order[m];
                }
            }

            bool anyMemberHasMask = false;
            for (int m = 0; m < memberCount; m++)
            {
                int mbIdx = memberIndices[m];
                if ((!rigBoneAlphaMasks.IsEmpty && (uint)mbIdx < (uint)rigBoneAlphaMasks.Length && rigBoneAlphaMasks[mbIdx] != null)
                    || (!rigBoneSilhouetteMasks.IsEmpty && (uint)mbIdx < (uint)rigBoneSilhouetteMasks.Length && rigBoneSilhouetteMasks[mbIdx] != null))
                {
                    anyMemberHasMask = true;
                    break;
                }
            }

            var triMemMaskList = memberCount > 0 ? new List<ulong>(nx * ny * 2) : null;
            var triCentList = memberCount > 0 ? new List<Vector2>(nx * ny * 2) : null;

            for (int gy = 0; gy < ny; gy++)
            {
                for (int gx = 0; gx < nx; gx++)
                {
                    if (alphaMask != null && !IsCellOpaqueWithMargin(alphaMask, gx, gy, nx, ny))
                    {
                        continue;
                    }

                    int i00 = GetOrAddVertex(gx, gy);
                    int i10 = GetOrAddVertex(gx + 1, gy);
                    int i01 = GetOrAddVertex(gx, gy + 1);
                    int i11 = GetOrAddVertex(gx + 1, gy + 1);
                    SkinnedVertexBinding b00 = bindList[i00];
                    SkinnedVertexBinding b10 = bindList[i10];
                    SkinnedVertexBinding b01 = bindList[i01];
                    SkinnedVertexBinding b11 = bindList[i11];

                    // Top-down +Y normal clockwise winding in XZ plane: (00, 01, 11) and (00, 11, 10)
                    triList.Add(i00);
                    triList.Add(i01);
                    triList.Add(i11);
                    int dom0 = ResolveTriangleOwnerAndMemberMask(
                        in b00, in b01, in b11,
                        sortedMembersDesc, sortedMemberBitPos, anyMemberHasMask,
                        bindPoses, rigBoneAlphaMasks, rigBoneSilhouetteMasks,
                        out ulong memMask0);
                    triDomList.Add(dom0);
                    if (triMemMaskList != null)
                    {
                        triMemMaskList.Add(memMask0);
                        triCentList.Add((b00.restRigXZ + b01.restRigXZ + b11.restRigXZ) * (1f / 3f));
                    }

                    triList.Add(i00);
                    triList.Add(i11);
                    triList.Add(i10);
                    int dom1 = ResolveTriangleOwnerAndMemberMask(
                        in b00, in b11, in b10,
                        sortedMembersDesc, sortedMemberBitPos, anyMemberHasMask,
                        bindPoses, rigBoneAlphaMasks, rigBoneSilhouetteMasks,
                        out ulong memMask1);
                    triDomList.Add(dom1);
                    if (triMemMaskList != null)
                    {
                        triMemMaskList.Add(memMask1);
                        triCentList.Add((b00.restRigXZ + b11.restRigXZ + b10.restRigXZ) * (1f / 3f));
                    }
                }
            }

            // Pass 2: Propagate exact ownership to any outer silhouette-border triangles via nearest owned triangle (Voronoi),
            // never falling back to closest joint pivot (which breaks elongated limbs, shared-wrist wing fans, and horns).
            hasValidMemberMasks = false;
            if (memberCount > 0 && triMemMaskList != null && triDomList.Count > 0)
            {
                int triCount = triDomList.Count;
                int ownedCount = 0;
                for (int t = 0; t < triCount; t++)
                {
                    if (triDomList[t] >= 0 && triMemMaskList[t] != 0UL)
                    {
                        ownedCount++;
                    }
                }

                if (ownedCount > 0)
                {
                    hasValidMemberMasks = true;
                    if (ownedCount < triCount)
                    {
                        var ownedIndices = new int[ownedCount];
                        int oi = 0;
                        for (int t = 0; t < triCount; t++)
                        {
                            if (triDomList[t] >= 0 && triMemMaskList[t] != 0UL)
                            {
                                ownedIndices[oi++] = t;
                            }
                        }

                        for (int t = 0; t < triCount; t++)
                        {
                            if (triDomList[t] >= 0 && triMemMaskList[t] != 0UL)
                            {
                                continue;
                            }
                            Vector2 ct = triCentList[t];
                            int nearestTri = ownedIndices[0];
                            float bestDistSq = float.MaxValue;
                            for (int k = 0; k < ownedCount; k++)
                            {
                                int cand = ownedIndices[k];
                                float dSq = (ct - triCentList[cand]).sqrMagnitude;
                                if (dSq < bestDistSq)
                                {
                                    bestDistSq = dSq;
                                    nearestTri = cand;
                                }
                            }
                            if (triDomList[t] < 0)
                            {
                                triDomList[t] = triDomList[nearestTri];
                            }
                            if (triMemMaskList[t] == 0UL)
                            {
                                triMemMaskList[t] = triMemMaskList[nearestTri];
                            }
                        }
                    }

                    // 1-ring vertex dilation on triMemberSilMask so newly exposed severed stumps always retain
                    // full unclipped mesh coverage for their dynamically dilated #14161A silhouette outline.
                    int vCntPre = bindList.Count;
                    var vertMemMask = new ulong[vCntPre];
                    for (int t = 0; t < triCount; t++)
                    {
                        ulong mMask = triMemMaskList[t];
                        int baseIdx = t * 3;
                        vertMemMask[triList[baseIdx]] |= mMask;
                        vertMemMask[triList[baseIdx + 1]] |= mMask;
                        vertMemMask[triList[baseIdx + 2]] |= mMask;
                    }
                    triMemberSilMask = new ulong[triCount];
                    for (int t = 0; t < triCount; t++)
                    {
                        int baseIdx = t * 3;
                        triMemberSilMask[t] = triMemMaskList[t]
                            | vertMemMask[triList[baseIdx]]
                            | vertMemMask[triList[baseIdx + 1]]
                            | vertMemMask[triList[baseIdx + 2]];
                    }
                }
                else
                {
                    // Headless fallback when no per-bone alpha masks were loaded
                    for (int t = 0; t < triCount; t++)
                    {
                        if (triDomList[t] < 0)
                        {
                            int baseIdx = t * 3;
                            SkinnedVertexBinding va = bindList[triList[baseIdx]];
                            SkinnedVertexBinding vb = bindList[triList[baseIdx + 1]];
                            SkinnedVertexBinding vc = bindList[triList[baseIdx + 2]];
                            triDomList[t] = SelectDominantBone(in va, in vb, in vc);
                        }
                    }
                    triMemberSilMask = Array.Empty<ulong>();
                }
            }
            else
            {
                triMemberSilMask = Array.Empty<ulong>();
            }

            bindings = bindList.ToArray();
            int vCount = bindings.Length;
            uvs = new Vector2[vCount];
            normals = new Vector3[vCount];
            deformedVerts = new Vector3[vCount];
            for (int i = 0; i < vCount; i++)
            {
                uvs[i] = bindings[i].uv;
                normals[i] = Vector3.up;
            }

            // Initialize optional 100% rigid SO(2) Euclidean wrist-pin state
            hasRigidPin = false;
            if (useMatrixLbs
                && (uint)meshDef.resolvedRigidPinBoneIndex < (uint)nodeCount
                && rigDef.skinnedMeshes != null
                && (uint)meshDef.resolvedPinTargetMeshIndex < (uint)rigDef.skinnedMeshes.Count)
            {
                GiantSkinnedMeshDef targetMesh = rigDef.skinnedMeshes[meshDef.resolvedPinTargetMeshIndex];
                if (targetMesh?.capsules != null && targetMesh.capsules.Count > 0)
                {
                    int clawIdx = meshDef.resolvedRigidPinBoneIndex;
                    CapsuleGroup[] armGroups = BuildCapsuleGroups(targetMesh.capsules, nodeCount);
                    Vector2 jBind = bindPoses[clawIdx].flatJointWorldXZ;
                    Span<int> pB = stackalloc int[4] { -1, -1, -1, -1 };
                    Span<float> pW = stackalloc float[4] { 0f, 0f, 0f, 0f };
                    int pFound = EvaluateCapsuleTopWeights(jBind, armGroups, Mathf.Clamp(targetMesh.maxInfluences, 1, 4), pB, pW);
                    if (pFound > 0)
                    {
                        NormalizeTopWeights(pFound, pW);
                        BoneWorldPose[] idlePoses = new BoneWorldPose[nodeCount];
                        BoneMatrix2x3[] idleMats = new BoneMatrix2x3[nodeCount];
                        SkeletalKinematics.SolveWorldPoses(
                            rigDef,
                            Vector2.zero,
                            0f,
                            ReadOnlySpan<BoneLocalPose>.Empty,
                            ReadOnlySpan<bool>.Empty,
                            idlePoses,
                            1f);
                        SkeletalKinematics.ComputeBoneSkinningMatrices2x3(rigDef, bindPoses, idlePoses, idleMats);

                        pinInfCount = (byte)pFound;
                        pinB0 = pB[0]; pinB1 = pB[1]; pinB2 = pB[2]; pinB3 = pB[3];
                        pinW0 = pW[0]; pinW1 = pW[1]; pinW2 = pW[2]; pinW3 = pW[3];
                        rigidPinClawBoneIdx = clawIdx;
                        rigidPinBindSocketXZ = jBind;
                        idleClawWorldAngleDeg = idlePoses[clawIdx].worldAngleDeg;

                        Vector2 idleWristXZ = EvaluateWeightedMatrixPoint(
                            jBind, pinInfCount, pinB0, pinB1, pinB2, pinB3, pinW0, pinW1, pinW2, pinW3, idleMats);
                        deltaIdleVertXZ = new Vector2[vCount];
                        ref readonly BoneMatrix2x3 clawIdleMat = ref idleMats[clawIdx];
                        for (int i = 0; i < vCount; i++)
                        {
                            deltaIdleVertXZ[i] = clawIdleMat.TransformPoint(bindings[i].restRigXZ) - idleWristXZ;
                        }
                        hasRigidPin = true;
                    }
                }
            }

            allTriangles = triList.ToArray();
            triDominantBone = triDomList.ToArray();
            activeTriangles = new int[allTriangles.Length];
            Array.Copy(allTriangles, activeTriangles, allTriangles.Length);
            activeTriangleIndexCount = allTriangles.Length;

            // Build cache-compact 32-byte LBS vertex buffer + exact used bone subset for O(usedBones) skinning.
            if (useMatrixLbs)
            {
                bool[] boneUsed = new bool[nodeCount];
                compactLbsVerts = new CompactLbsVertex[vCount];
                for (int i = 0; i < vCount; i++)
                {
                    ref readonly SkinnedVertexBinding vb = ref bindings[i];
                    byte ic = vb.influenceCount;
                    int b0 = vb.b0 >= 0 ? vb.b0 : 0;
                    int b1 = ic > 1 && vb.b1 >= 0 ? vb.b1 : b0;
                    int b2 = ic > 2 && vb.b2 >= 0 ? vb.b2 : b0;
                    int b3 = ic > 3 && vb.b3 >= 0 ? vb.b3 : b0;
                    if ((uint)b0 < (uint)nodeCount) boneUsed[b0] = true;
                    if (ic > 1 && (uint)b1 < (uint)nodeCount) boneUsed[b1] = true;
                    if (ic > 2 && (uint)b2 < (uint)nodeCount) boneUsed[b2] = true;
                    if (ic > 3 && (uint)b3 < (uint)nodeCount) boneUsed[b3] = true;
                    compactLbsVerts[i] = new CompactLbsVertex
                    {
                        rx = vb.restRigXZ.x,
                        rz = vb.restRigXZ.y,
                        w0 = vb.w0,
                        w1 = ic > 1 ? vb.w1 : 0f,
                        w2 = ic > 2 ? vb.w2 : 0f,
                        w3 = ic > 3 ? vb.w3 : 0f,
                        b0 = (ushort)b0,
                        b1 = (ushort)b1,
                        b2 = (ushort)b2,
                        b3 = (ushort)b3
                    };
                }
                if (hasRigidPin)
                {
                    if ((uint)pinB0 < (uint)nodeCount) boneUsed[pinB0] = true;
                    if (pinInfCount > 1 && (uint)pinB1 < (uint)nodeCount) boneUsed[pinB1] = true;
                    if (pinInfCount > 2 && (uint)pinB2 < (uint)nodeCount) boneUsed[pinB2] = true;
                    if (pinInfCount > 3 && (uint)pinB3 < (uint)nodeCount) boneUsed[pinB3] = true;
                }
                int usedCount = 0;
                for (int b = 0; b < nodeCount; b++)
                {
                    if (boneUsed[b]) usedCount++;
                }
                usedSkinBoneIndices = new int[usedCount];
                int uPos = 0;
                for (int b = 0; b < nodeCount; b++)
                {
                    if (boneUsed[b]) usedSkinBoneIndices[uPos++] = b;
                }
            }
            else
            {
                compactLbsVerts = Array.Empty<CompactLbsVertex>();
                usedSkinBoneIndices = Array.Empty<int>();
            }

            lastSeveredMaskBits = ulong.MaxValue;
            lastVisibleMemberMask = ulong.MaxValue;
            currentVisibleMemberMask = 0UL;
            lastRigidPinVisible = false;
            lastMeshPartMissing = false;
            hasPartialMemberSevering = false;
            bindInitialized = true;
        }

        private ulong ComputeVisibleMemberMask(ReadOnlySpan<BoneWorldPose> solvedWorldPoses)
        {
            int[] memberIndices = meshDef.resolvedMemberBoneIndices;
            if (memberIndices == null || memberIndices.Length == 0)
            {
                return 0UL;
            }
            int poseCount = solvedWorldPoses.Length;
            int clampedM = Math.Min(memberIndices.Length, 64);
            ulong visMask = 0UL;
            for (int m = 0; m < clampedM; m++)
            {
                int mbIdx = memberIndices[m];
                if ((uint)mbIdx < (uint)poseCount && solvedWorldPoses[mbIdx].visible)
                {
                    visMask |= (1UL << m);
                }
            }
            return visMask;
        }

        private ulong ComputeSeveredMaskBits(ReadOnlySpan<BoneWorldPose> solvedWorldPoses)
        {
            int poseCount = solvedWorldPoses.Length;
            ulong sevBits = 0UL;
            int[] boneIndices = meshDef.resolvedBoneIndices;
            for (int k = 0; k < boneIndices.Length && k < 64; k++)
            {
                int bIdx = boneIndices[k];
                if ((uint)bIdx >= (uint)poseCount || !solvedWorldPoses[bIdx].visible)
                {
                    sevBits |= (1UL << k);
                }
            }
            return sevBits;
        }

        /// <summary>
        /// Fast check for whether bone visibility or mesh missing state differs from the last uploaded severing mask,
        /// ensuring immediate mesh/texture rebuild even when severing occurs on the same render frame while paused.
        /// </summary>
        public bool HasVisibilityChanged(ReadOnlySpan<BoneWorldPose> solvedWorldPoses, bool meshPartMissing)
        {
            if (!bindInitialized)
            {
                return true;
            }
            if (bindings.Length == 0 || allTriangles.Length < 3)
            {
                return false;
            }
            if (meshPartMissing)
            {
                return currentlyVisible;
            }
            if (lastMeshPartMissing)
            {
                return true;
            }
            int poseCount = solvedWorldPoses.Length;
            if (hasRigidPin)
            {
                bool pinVis = (uint)rigidPinClawBoneIdx < (uint)poseCount && solvedWorldPoses[rigidPinClawBoneIdx].visible;
                if (!pinVis)
                {
                    return currentlyVisible;
                }
                if (!lastRigidPinVisible)
                {
                    return true;
                }
            }
            int[] memberIndices = meshDef.resolvedMemberBoneIndices;
            if (memberIndices != null && memberIndices.Length > 0)
            {
                ulong visMask = ComputeVisibleMemberMask(solvedWorldPoses);
                if (visMask != lastVisibleMemberMask)
                {
                    return true;
                }
            }
            ulong sevBits = ComputeSeveredMaskBits(solvedWorldPoses);
            return sevBits != lastSeveredMaskBits;
        }

        /// <summary>
        /// Evaluates LBS (or rigid SO(2) wrist-pinned) vertex positions in root-relative world XZ with zero heap allocations.
        /// Returns true if the triangle index buffer changed due to bone severing/restoration.
        /// </summary>
        public bool EvaluateDeformedVertices(
            ReadOnlySpan<BoneWorldPose> solvedWorldPoses,
            Vector2 rootWorldXZ,
            bool meshPartMissing,
            out bool trianglesChanged)
        {
            trianglesChanged = false;
            if (!bindInitialized || bindings.Length == 0 || allTriangles.Length < 3 || meshPartMissing)
            {
                lastMeshPartMissing = meshPartMissing;
                currentlyVisible = false;
                hasPartialMemberSevering = false;
                currentVisibleMemberMask = 0UL;
                return false;
            }
            lastMeshPartMissing = false;

            int poseCount = solvedWorldPoses.Length;
            if (hasRigidPin && ((uint)rigidPinClawBoneIdx >= (uint)poseCount || !solvedWorldPoses[rigidPinClawBoneIdx].visible))
            {
                lastRigidPinVisible = false;
                currentlyVisible = false;
                hasPartialMemberSevering = false;
                currentVisibleMemberMask = 0UL;
                lastVisibleMemberMask = ComputeVisibleMemberMask(solvedWorldPoses);
                lastSeveredMaskBits = ComputeSeveredMaskBits(solvedWorldPoses);
                return false;
            }
            if (hasRigidPin)
            {
                lastRigidPinVisible = true;
            }

            ulong severedBits = 0UL;
            bool anyBoneVisible = false;
            int[] boneIndices = meshDef.resolvedBoneIndices;
            for (int k = 0; k < boneIndices.Length; k++)
            {
                int bIdx = boneIndices[k];
                bool vis = (uint)bIdx < (uint)poseCount && solvedWorldPoses[bIdx].visible;
                if (vis)
                {
                    anyBoneVisible = true;
                }
                else if (k < 64)
                {
                    severedBits |= (1UL << k);
                }
            }

            int[] memberIndices = meshDef.resolvedMemberBoneIndices;
            int memberCount = memberIndices != null ? memberIndices.Length : 0;
            ulong visMemberMask = 0UL;
            ulong allMembersMask = 0UL;
            if (memberCount > 0)
            {
                int clampedM = Math.Min(memberCount, 64);
                allMembersMask = clampedM >= 64 ? ulong.MaxValue : ((1UL << clampedM) - 1UL);
                for (int m = 0; m < clampedM; m++)
                {
                    int mbIdx = memberIndices[m];
                    if ((uint)mbIdx < (uint)poseCount && solvedWorldPoses[mbIdx].visible)
                    {
                        visMemberMask |= (1UL << m);
                    }
                }
                if (visMemberMask == 0UL)
                {
                    currentlyVisible = false;
                    hasPartialMemberSevering = false;
                    currentVisibleMemberMask = 0UL;
                    lastVisibleMemberMask = 0UL;
                    lastSeveredMaskBits = severedBits;
                    return false;
                }
            }

            if (!anyBoneVisible)
            {
                currentlyVisible = false;
                hasPartialMemberSevering = false;
                currentVisibleMemberMask = 0UL;
                lastVisibleMemberMask = visMemberMask;
                lastSeveredMaskBits = severedBits;
                return false;
            }

            currentVisibleMemberMask = visMemberMask;
            hasPartialMemberSevering = meshDef.cullSeveredBoneTriangles
                && memberCount > 1
                && visMemberMask != allMembersMask;

            if (meshDef.cullSeveredBoneTriangles)
            {
                if (severedBits != lastSeveredMaskBits || visMemberMask != lastVisibleMemberMask)
                {
                    lastSeveredMaskBits = severedBits;
                    lastVisibleMemberMask = visMemberMask;
                    trianglesChanged = true;
                    if (hasValidMemberMasks && memberCount > 0)
                    {
                        if (visMemberMask == allMembersMask)
                        {
                            Array.Copy(allTriangles, activeTriangles, allTriangles.Length);
                            activeTriangleIndexCount = allTriangles.Length;
                        }
                        else
                        {
                            int dst = 0;
                            int triCount = triMemberSilMask.Length;
                            for (int t = 0; t < triCount; t++)
                            {
                                if ((triMemberSilMask[t] & visMemberMask) == 0UL)
                                {
                                    continue;
                                }
                                int src = t * 3;
                                activeTriangles[dst++] = allTriangles[src];
                                activeTriangles[dst++] = allTriangles[src + 1];
                                activeTriangles[dst++] = allTriangles[src + 2];
                            }
                            activeTriangleIndexCount = dst;
                        }
                    }
                    else if (severedBits == 0UL)
                    {
                        Array.Copy(allTriangles, activeTriangles, allTriangles.Length);
                        activeTriangleIndexCount = allTriangles.Length;
                    }
                    else
                    {
                        int dst = 0;
                        int triCount = triDominantBone.Length;
                        for (int t = 0; t < triCount; t++)
                        {
                            int domBone = triDominantBone[t];
                            if ((uint)domBone < (uint)poseCount && !solvedWorldPoses[domBone].visible)
                            {
                                continue;
                            }
                            int src = t * 3;
                            activeTriangles[dst++] = allTriangles[src];
                            activeTriangles[dst++] = allTriangles[src + 1];
                            activeTriangles[dst++] = allTriangles[src + 2];
                        }
                        activeTriangleIndexCount = dst;
                    }
                }
            }
            else if (lastSeveredMaskBits != 0UL || lastVisibleMemberMask != allMembersMask)
            {
                lastSeveredMaskBits = 0UL;
                lastVisibleMemberMask = allMembersMask;
                Array.Copy(allTriangles, activeTriangles, allTriangles.Length);
                activeTriangleIndexCount = allTriangles.Length;
                trianglesChanged = true;
            }

            if (activeTriangleIndexCount < 3)
            {
                currentlyVisible = false;
                return trianglesChanged;
            }

            if (useMatrixLbs)
            {
                int nodeCount = flatBindPoses.Length;
                if (poseCount < nodeCount)
                {
                    currentlyVisible = false;
                    return false;
                }
                if (hasRigidPin)
                {
                    SkeletalKinematics.ComputeBoneSkinningMatrices2x3Subset(
                        rigDef, flatBindPoses, solvedWorldPoses, boneMatsScratch, usedSkinBoneIndices);
                }
                else
                {
                    SkeletalKinematics.ComputeBoneSkinningMatrices2x3Subset(
                        rigDef, flatBindPoses, solvedWorldPoses, boneMatsScratch, usedSkinBoneIndices,
                        rootWorldXZ.x, rootWorldXZ.y);
                }
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            if (hasRigidPin)
            {
                Vector2 liveWristXZ = EvaluateWeightedMatrixPoint(
                    rigidPinBindSocketXZ, pinInfCount, pinB0, pinB1, pinB2, pinB3, pinW0, pinW1, pinW2, pinW3, boneMatsScratch);
                float dTheta = Mathf.DeltaAngle(idleClawWorldAngleDeg, solvedWorldPoses[rigidPinClawBoneIdx].worldAngleDeg);
                float rad = dTheta * Mathf.Deg2Rad;
                float ca = Mathf.Cos(rad);
                float sa = Mathf.Sin(rad);
                float baseX = liveWristXZ.x - rootWorldXZ.x;
                float baseZ = liveWristXZ.y - rootWorldXZ.y;

                for (int i = 0; i < deltaIdleVertXZ.Length; i++)
                {
                    Vector2 dv = deltaIdleVertXZ[i];
                    float lx = baseX + (dv.x * ca + dv.y * sa);
                    float lz = baseZ + (-dv.x * sa + dv.y * ca);
                    deformedVerts[i] = new Vector3(lx, 0f, lz);
                    if (lx < minX) minX = lx;
                    if (lx > maxX) maxX = lx;
                    if (lz < minZ) minZ = lz;
                    if (lz > maxZ) maxZ = lz;
                }
            }
            else if (useMatrixLbs)
            {
                // Cache-compact 32-byte LBS vertex stream with rootWorldXZ already subtracted into boneMatsScratch.
                BoneMatrix2x3[] mats = boneMatsScratch;
                CompactLbsVertex[] verts = compactLbsVerts;
                Vector3[] outV = deformedVerts;
                int vCount = verts.Length;
                for (int i = 0; i < vCount; i++)
                {
                    ref readonly CompactLbsVertex cv = ref verts[i];
                    float rx = cv.rx;
                    float rz = cv.rz;
                    ref readonly BoneMatrix2x3 m0 = ref mats[cv.b0];
                    float lx, lz;
                    float w1 = cv.w1;
                    if (w1 == 0f)
                    {
                        lx = m0.m00 * rx + m0.m01 * rz + m0.m02;
                        lz = m0.m10 * rx + m0.m11 * rz + m0.m12;
                    }
                    else
                    {
                        float w0 = cv.w0;
                        ref readonly BoneMatrix2x3 m1 = ref mats[cv.b1];
                        lx = (m0.m00 * rx + m0.m01 * rz + m0.m02) * w0
                           + (m1.m00 * rx + m1.m01 * rz + m1.m02) * w1;
                        lz = (m0.m10 * rx + m0.m11 * rz + m0.m12) * w0
                           + (m1.m10 * rx + m1.m11 * rz + m1.m12) * w1;
                        float w2 = cv.w2;
                        if (w2 != 0f)
                        {
                            ref readonly BoneMatrix2x3 m2 = ref mats[cv.b2];
                            lx += (m2.m00 * rx + m2.m01 * rz + m2.m02) * w2;
                            lz += (m2.m10 * rx + m2.m11 * rz + m2.m12) * w2;
                            float w3 = cv.w3;
                            if (w3 != 0f)
                            {
                                ref readonly BoneMatrix2x3 m3 = ref mats[cv.b3];
                                lx += (m3.m00 * rx + m3.m01 * rz + m3.m02) * w3;
                                lz += (m3.m10 * rx + m3.m11 * rz + m3.m12) * w3;
                            }
                        }
                    }

                    outV[i] = new Vector3(lx, 0f, lz);
                    if (lx < minX) minX = lx;
                    if (lx > maxX) maxX = lx;
                    if (lz < minZ) minZ = lz;
                    if (lz > maxZ) maxZ = lz;
                }
            }
            else
            {
                for (int i = 0; i < bindings.Length; i++)
                {
                    ref readonly SkinnedVertexBinding vb = ref bindings[i];
                    Vector2 blendedXZ = EvaluateVertexWorldXZ(in vb, solvedWorldPoses, poseCount);
                    float lx = blendedXZ.x - rootWorldXZ.x;
                    float lz = blendedXZ.y - rootWorldXZ.y;
                    deformedVerts[i] = new Vector3(lx, 0f, lz);
                    if (lx < minX) minX = lx;
                    if (lx > maxX) maxX = lx;
                    if (lz < minZ) minZ = lz;
                    if (lz > maxZ) maxZ = lz;
                }
            }

            Vector3 center = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
            Vector3 size = new Vector3((maxX - minX) + 3.0f, 3.0f, (maxZ - minZ) + 3.0f);
            currentBounds = new Bounds(center, size);
            currentlyVisible = true;
            return trianglesChanged;
        }

        /// <summary>
        /// Pure read-only extraction of a detached subset of this skinned mesh evaluated at the exact
        /// pre-sever world pose. Never mutates live SkinnedMeshRuntime state.
        /// Produces compacted world-space XZ vertices, UVs, and triangle indices for severed-part falling debris.
        /// </summary>
        public bool TryExtractDetachedMeshSnapshot(
            ReadOnlySpan<BoneWorldPose> preSeverPoses,
            Vector2 pawnRootWorldXZ,
            ulong detachedMemberMask,
            out Vector2[] outWorldVertsXZ,
            out Vector2[] outUVs,
            out int[] outTriangles,
            out Vector2 outMinXZ,
            out Vector2 outMaxXZ,
            out bool outIsFullMesh)
        {
            outWorldVertsXZ = Array.Empty<Vector2>();
            outUVs = Array.Empty<Vector2>();
            outTriangles = Array.Empty<int>();
            outMinXZ = Vector2.zero;
            outMaxXZ = Vector2.zero;
            outIsFullMesh = false;

            if (!bindInitialized || bindings.Length == 0 || allTriangles.Length < 3 || detachedMemberMask == 0UL)
            {
                return false;
            }

            int poseCount = preSeverPoses.Length;
            int[] memberIndices = meshDef.resolvedMemberBoneIndices;
            int memberCount = memberIndices != null ? memberIndices.Length : 0;
            if (memberCount <= 0)
            {
                return false;
            }

            int clampedM = Math.Min(memberCount, 64);
            ulong allMembersMask = clampedM >= 64 ? ulong.MaxValue : ((1UL << clampedM) - 1UL);
            ulong maskedDetached = detachedMemberMask & allMembersMask;
            if (maskedDetached == 0UL)
            {
                return false;
            }

            bool isFull = memberCount <= 1 || maskedDetached == allMembersMask;
            outIsFullMesh = isFull;

            int triTotal = allTriangles.Length / 3;
            var selTris = new List<int>(isFull ? allTriangles.Length : Math.Min(allTriangles.Length, 512));

            if (isFull)
            {
                for (int i = 0; i < allTriangles.Length; i++)
                {
                    selTris.Add(allTriangles[i]);
                }
            }
            else if (hasValidMemberMasks && triMemberSilMask.Length == triTotal)
            {
                for (int t = 0; t < triTotal; t++)
                {
                    if ((triMemberSilMask[t] & maskedDetached) == 0UL)
                    {
                        continue;
                    }
                    int src = t * 3;
                    selTris.Add(allTriangles[src]);
                    selTris.Add(allTriangles[src + 1]);
                    selTris.Add(allTriangles[src + 2]);
                }
            }
            else if (triDominantBone.Length == triTotal)
            {
                for (int t = 0; t < triTotal; t++)
                {
                    int dom = triDominantBone[t];
                    bool hit = false;
                    for (int m = 0; m < clampedM; m++)
                    {
                        if (((maskedDetached >> m) & 1UL) != 0UL && memberIndices[m] == dom)
                        {
                            hit = true;
                            break;
                        }
                    }
                    if (!hit)
                    {
                        continue;
                    }
                    int src = t * 3;
                    selTris.Add(allTriangles[src]);
                    selTris.Add(allTriangles[src + 1]);
                    selTris.Add(allTriangles[src + 2]);
                }
            }

            if (selTris.Count < 3)
            {
                return false;
            }

            BoneMatrix2x3[] localMats = null;
            if (useMatrixLbs)
            {
                int nodeCount = flatBindPoses.Length;
                if (poseCount < nodeCount)
                {
                    return false;
                }
                localMats = new BoneMatrix2x3[nodeCount];
                SkeletalKinematics.ComputeBoneSkinningMatrices2x3(rigDef, flatBindPoses, preSeverPoses, localMats);
            }

            // Compact referenced vertices into a tight vertex buffer
            int vTotal = bindings.Length;
            var remap = new int[vTotal];
            Array.Fill(remap, -1);
            var usedVerts = new List<int>(selTris.Count / 2);
            var compactTris = new int[selTris.Count];

            for (int i = 0; i < selTris.Count; i++)
            {
                int oldIdx = selTris[i];
                int newIdx = remap[oldIdx];
                if (newIdx < 0)
                {
                    newIdx = usedVerts.Count;
                    remap[oldIdx] = newIdx;
                    usedVerts.Add(oldIdx);
                }
                compactTris[i] = newIdx;
            }

            int compactCount = usedVerts.Count;
            var worldXZ = new Vector2[compactCount];
            var compactUV = new Vector2[compactCount];
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            if (hasRigidPin && localMats != null && (uint)rigidPinClawBoneIdx < (uint)poseCount)
            {
                Vector2 liveWristXZ = EvaluateWeightedMatrixPoint(
                    rigidPinBindSocketXZ, pinInfCount, pinB0, pinB1, pinB2, pinB3, pinW0, pinW1, pinW2, pinW3, localMats);
                float dTheta = Mathf.DeltaAngle(idleClawWorldAngleDeg, preSeverPoses[rigidPinClawBoneIdx].worldAngleDeg);
                float rad = dTheta * Mathf.Deg2Rad;
                float ca = Mathf.Cos(rad);
                float sa = Mathf.Sin(rad);

                for (int c = 0; c < compactCount; c++)
                {
                    int orig = usedVerts[c];
                    Vector2 dv = deltaIdleVertXZ[orig];
                    float wx = pawnRootWorldXZ.x + liveWristXZ.x + (dv.x * ca + dv.y * sa);
                    float wz = pawnRootWorldXZ.y + liveWristXZ.y + (-dv.x * sa + dv.y * ca);
                    worldXZ[c] = new Vector2(wx, wz);
                    compactUV[c] = uvs[orig];
                    if (wx < minX) minX = wx;
                    if (wx > maxX) maxX = wx;
                    if (wz < minZ) minZ = wz;
                    if (wz > maxZ) maxZ = wz;
                }
            }
            else if (useMatrixLbs && localMats != null)
            {
                for (int c = 0; c < compactCount; c++)
                {
                    int orig = usedVerts[c];
                    ref readonly SkinnedVertexBinding vb = ref bindings[orig];
                    Vector2 blendedXZ = EvaluateVertexMatrixWorldXZ(in vb, preSeverPoses, localMats, poseCount);
                    float wx = pawnRootWorldXZ.x + blendedXZ.x;
                    float wz = pawnRootWorldXZ.y + blendedXZ.y;
                    worldXZ[c] = new Vector2(wx, wz);
                    compactUV[c] = uvs[orig];
                    if (wx < minX) minX = wx;
                    if (wx > maxX) maxX = wx;
                    if (wz < minZ) minZ = wz;
                    if (wz > maxZ) maxZ = wz;
                }
            }
            else
            {
                for (int c = 0; c < compactCount; c++)
                {
                    int orig = usedVerts[c];
                    ref readonly SkinnedVertexBinding vb = ref bindings[orig];
                    Vector2 blendedXZ = EvaluateVertexWorldXZ(in vb, preSeverPoses, poseCount);
                    float wx = pawnRootWorldXZ.x + blendedXZ.x;
                    float wz = pawnRootWorldXZ.y + blendedXZ.y;
                    worldXZ[c] = new Vector2(wx, wz);
                    compactUV[c] = uvs[orig];
                    if (wx < minX) minX = wx;
                    if (wx > maxX) maxX = wx;
                    if (wz < minZ) minZ = wz;
                    if (wz > maxZ) maxZ = wz;
                }
            }

            outWorldVertsXZ = worldXZ;
            outUVs = compactUV;
            outTriangles = compactTris;
            outMinXZ = new Vector2(minX, minZ);
            outMaxXZ = new Vector2(maxX, maxZ);
            return true;
        }

        private struct CapsuleGroup
        {
            public int targetBoneIdx;
            public float weightMultiplier;
            public GiantSkinCapsuleDef[] segments;
        }

        private static CapsuleGroup[] BuildCapsuleGroups(List<GiantSkinCapsuleDef> caps, int nodeCount)
        {
            var map = new Dictionary<string, List<GiantSkinCapsuleDef>>(StringComparer.Ordinal);
            var order = new List<string>(caps.Count);
            for (int i = 0; i < caps.Count; i++)
            {
                GiantSkinCapsuleDef c = caps[i];
                if (c == null || (uint)c.resolvedBoneIndex >= (uint)nodeCount)
                {
                    continue;
                }
                string key = !string.IsNullOrEmpty(c.groupId) ? c.groupId : c.boneId;
                if (!map.TryGetValue(key, out List<GiantSkinCapsuleDef> list))
                {
                    list = new List<GiantSkinCapsuleDef>(4);
                    map[key] = list;
                    order.Add(key);
                }
                list.Add(c);
            }

            var groups = new CapsuleGroup[order.Count];
            for (int g = 0; g < order.Count; g++)
            {
                List<GiantSkinCapsuleDef> list = map[order[g]];
                groups[g] = new CapsuleGroup
                {
                    targetBoneIdx = list[0].resolvedBoneIndex,
                    weightMultiplier = list[0].weightMultiplier,
                    segments = list.ToArray()
                };
            }
            return groups;
        }

        private static int EvaluateCapsuleTopWeights(
            Vector2 p,
            CapsuleGroup[] groups,
            int maxInf,
            Span<int> topB,
            Span<float> topW)
        {
            Span<int> accBones = stackalloc int[32];
            Span<double> accWeights = stackalloc double[32];
            int uniqueCount = 0;

            for (int g = 0; g < groups.Length; g++)
            {
                ref readonly CapsuleGroup grp = ref groups[g];
                float minD2 = 1e9f;
                float sigVal = 0.40f;
                for (int s = 0; s < grp.segments.Length; s++)
                {
                    GiantSkinCapsuleDef seg = grp.segments[s];
                    float d = DistancePointToSegment(p, seg.startXZ, seg.endXZ);
                    float d2 = d * d;
                    if (d2 < minD2)
                    {
                        minD2 = d2;
                        sigVal = seg.radius;
                    }
                }

                double scaledD = Math.Sqrt(minD2) / Math.Max(0.20, sigVal);
                double w = grp.weightMultiplier / Math.Pow(scaledD + 0.14, 3.6);

                int slot = -1;
                for (int u = 0; u < uniqueCount; u++)
                {
                    if (accBones[u] == grp.targetBoneIdx)
                    {
                        slot = u;
                        break;
                    }
                }
                if (slot < 0)
                {
                    if (uniqueCount < accBones.Length)
                    {
                        slot = uniqueCount++;
                        accBones[slot] = grp.targetBoneIdx;
                        accWeights[slot] = w;
                    }
                }
                else
                {
                    accWeights[slot] += w;
                }
            }

            int found = 0;
            for (int u = 0; u < uniqueCount; u++)
            {
                InsertTopWeight(accBones[u], (float)accWeights[u], maxInf, ref found, topB, topW);
            }
            return found;
        }

        private static void InsertTopWeight(int bIdx, float w, int maxInf, ref int found, Span<int> topB, Span<float> topW)
        {
            int insertPos = -1;
            if (found < maxInf)
            {
                insertPos = found;
                found++;
            }
            else if (w > topW[maxInf - 1])
            {
                insertPos = maxInf - 1;
            }

            if (insertPos >= 0)
            {
                topB[insertPos] = bIdx;
                topW[insertPos] = w;
                for (int s = insertPos; s > 0 && topW[s] > topW[s - 1]; s--)
                {
                    int tb = topB[s - 1]; topB[s - 1] = topB[s]; topB[s] = tb;
                    float tw = topW[s - 1]; topW[s - 1] = topW[s]; topW[s] = tw;
                }
            }
        }

        private static void NormalizeTopWeights(int found, Span<float> topW)
        {
            float wSum = 0f;
            for (int s = 0; s < found; s++)
            {
                wSum += topW[s];
            }
            float invSum = wSum > 1e-12f ? (1f / wSum) : 1f;
            for (int s = 0; s < found; s++)
            {
                topW[s] *= invSum;
            }
        }

        private static Vector2 EvaluateWeightedMatrixPoint(
            Vector2 ptBindXZ,
            byte infCount,
            int b0, int b1, int b2, int b3,
            float w0, float w1, float w2, float w3,
            ReadOnlySpan<BoneMatrix2x3> mats)
        {
            Vector2 sum = Vector2.zero;
            if (infCount >= 1 && (uint)b0 < (uint)mats.Length) sum += mats[b0].TransformPoint(ptBindXZ) * w0;
            if (infCount >= 2 && (uint)b1 < (uint)mats.Length) sum += mats[b1].TransformPoint(ptBindXZ) * w1;
            if (infCount >= 3 && (uint)b2 < (uint)mats.Length) sum += mats[b2].TransformPoint(ptBindXZ) * w2;
            if (infCount >= 4 && (uint)b3 < (uint)mats.Length) sum += mats[b3].TransformPoint(ptBindXZ) * w3;
            return sum;
        }

        /// <summary>
        /// Evaluates continuous 2x3 matrix LBS using all valid FK bone transforms without dropping severed neighbor
        /// weights, eliminating cut-boundary snapping and stretched triangle spears when adjacent parts break.
        /// </summary>
        private static Vector2 EvaluateVertexMatrixWorldXZ(
            in SkinnedVertexBinding vb,
            ReadOnlySpan<BoneWorldPose> poses,
            ReadOnlySpan<BoneMatrix2x3> mats,
            int poseCount)
        {
            Vector2 sum = Vector2.zero;
            float wTotal = 0f;

            if (vb.influenceCount >= 1 && (uint)vb.b0 < (uint)poseCount)
            {
                sum += mats[vb.b0].TransformPoint(vb.restRigXZ) * vb.w0;
                wTotal += vb.w0;
            }
            if (vb.influenceCount >= 2 && (uint)vb.b1 < (uint)poseCount)
            {
                sum += mats[vb.b1].TransformPoint(vb.restRigXZ) * vb.w1;
                wTotal += vb.w1;
            }
            if (vb.influenceCount >= 3 && (uint)vb.b2 < (uint)poseCount)
            {
                sum += mats[vb.b2].TransformPoint(vb.restRigXZ) * vb.w2;
                wTotal += vb.w2;
            }
            if (vb.influenceCount >= 4 && (uint)vb.b3 < (uint)poseCount)
            {
                sum += mats[vb.b3].TransformPoint(vb.restRigXZ) * vb.w3;
                wTotal += vb.w3;
            }

            if (wTotal > 1e-5f)
            {
                return sum * (1f / wTotal);
            }
            if ((uint)vb.b0 < (uint)poseCount)
            {
                return mats[vb.b0].TransformPoint(vb.restRigXZ);
            }
            return vb.restRigXZ;
        }

        private static Vector2 EvaluateVertexWorldXZ(
            in SkinnedVertexBinding vb,
            ReadOnlySpan<BoneWorldPose> poses,
            int poseCount)
        {
            Vector2 sum = Vector2.zero;
            float wTotal = 0f;

            if (vb.influenceCount >= 1 && (uint)vb.b0 < (uint)poseCount)
            {
                sum += poses[vb.b0].UVToWorldXZ(vb.uv0.x, vb.uv0.y) * vb.w0;
                wTotal += vb.w0;
            }
            if (vb.influenceCount >= 2 && (uint)vb.b1 < (uint)poseCount)
            {
                sum += poses[vb.b1].UVToWorldXZ(vb.uv1.x, vb.uv1.y) * vb.w1;
                wTotal += vb.w1;
            }
            if (vb.influenceCount >= 3 && (uint)vb.b2 < (uint)poseCount)
            {
                sum += poses[vb.b2].UVToWorldXZ(vb.uv2.x, vb.uv2.y) * vb.w2;
                wTotal += vb.w2;
            }
            if (vb.influenceCount >= 4 && (uint)vb.b3 < (uint)poseCount)
            {
                sum += poses[vb.b3].UVToWorldXZ(vb.uv3.x, vb.uv3.y) * vb.w3;
                wTotal += vb.w3;
            }

            if (wTotal > 1e-5f)
            {
                return sum * (1f / wTotal);
            }
            if ((uint)vb.b0 < (uint)poseCount)
            {
                return poses[vb.b0].UVToWorldXZ(vb.uv0.x, vb.uv0.y);
            }
            return vb.restRigXZ;
        }

        private static bool SampleMaskHitAtPoint(
            Vector2 pt,
            in BoneWorldPose bindPose,
            TextureAlphaMask mask)
        {
            return mask != null
                && bindPose.TryWorldToUV(pt.x, pt.y, out float bu, out float bv)
                && mask.IsOpaqueUV(bu, bv);
        }

        private static bool SampleMaskHitOnTriangle7(
            Vector2 pa, Vector2 pb, Vector2 pc,
            Vector2 cent, Vector2 m01, Vector2 m12, Vector2 m20,
            in BoneWorldPose bindPose,
            TextureAlphaMask mask)
        {
            if (mask == null)
            {
                return false;
            }
            return SampleMaskHitAtPoint(cent, in bindPose, mask)
                || SampleMaskHitAtPoint(pa, in bindPose, mask)
                || SampleMaskHitAtPoint(pb, in bindPose, mask)
                || SampleMaskHitAtPoint(pc, in bindPose, mask)
                || SampleMaskHitAtPoint(m01, in bindPose, mask)
                || SampleMaskHitAtPoint(m12, in bindPose, mask)
                || SampleMaskHitAtPoint(m20, in bindPose, mask);
        }

        /// <summary>
        /// Resolves both the top-layer owner bone (triDominantBone) and the full inclusive bitmask of every member
        /// bone whose silhouette or color sprite covers any part of triangle (a, b, c).
        /// Returns -1 when outside all member masks so Pass 2 resolves outer border triangles via nearest-owned Voronoi.
        /// </summary>
        private static int ResolveTriangleOwnerAndMemberMask(
            in SkinnedVertexBinding a,
            in SkinnedVertexBinding b,
            in SkinnedVertexBinding c,
            int[] sortedMembersDesc,
            int[] sortedMemberBitPos,
            bool anyMemberHasMask,
            BoneWorldPose[] bindPoses,
            ReadOnlySpan<TextureAlphaMask> rigBoneAlphaMasks,
            ReadOnlySpan<TextureAlphaMask> rigBoneSilhouetteMasks,
            out ulong memberSilMask)
        {
            memberSilMask = 0UL;
            if (sortedMembersDesc == null || sortedMembersDesc.Length == 0)
            {
                return SelectDominantBone(in a, in b, in c);
            }
            if (!anyMemberHasMask)
            {
                return -1;
            }

            Vector2 pa = a.restRigXZ;
            Vector2 pb = b.restRigXZ;
            Vector2 pc = c.restRigXZ;
            Vector2 cent = (pa + pb + pc) * (1f / 3f);
            Vector2 m01 = (pa + pb) * 0.5f;
            Vector2 m12 = (pb + pc) * 0.5f;
            Vector2 m20 = (pc + pa) * 0.5f;

            int bestColorCentroidOwner = -1;
            int bestColorAnyOwner = -1;
            int bestSilOwner = -1;

            for (int m = 0; m < sortedMembersDesc.Length; m++)
            {
                int mbIdx = sortedMembersDesc[m];
                if ((uint)mbIdx >= (uint)bindPoses.Length)
                {
                    continue;
                }
                int bitPos = sortedMemberBitPos[m];
                ref readonly BoneWorldPose bp = ref bindPoses[mbIdx];

                TextureAlphaMask colMask = (!rigBoneAlphaMasks.IsEmpty && (uint)mbIdx < (uint)rigBoneAlphaMasks.Length)
                    ? rigBoneAlphaMasks[mbIdx]
                    : null;
                TextureAlphaMask silMask = (!rigBoneSilhouetteMasks.IsEmpty && (uint)mbIdx < (uint)rigBoneSilhouetteMasks.Length)
                    ? rigBoneSilhouetteMasks[mbIdx]
                    : null;

                bool hitCol = false;
                if (colMask != null)
                {
                    if (SampleMaskHitAtPoint(cent, in bp, colMask))
                    {
                        hitCol = true;
                        if (bestColorCentroidOwner < 0)
                        {
                            bestColorCentroidOwner = mbIdx;
                        }
                    }
                    else if (SampleMaskHitOnTriangle7(pa, pb, pc, cent, m01, m12, m20, in bp, colMask))
                    {
                        hitCol = true;
                        if (bestColorAnyOwner < 0)
                        {
                            bestColorAnyOwner = mbIdx;
                        }
                    }
                }

                bool hitSil = hitCol;
                if (!hitSil && silMask != null)
                {
                    if (SampleMaskHitOnTriangle7(pa, pb, pc, cent, m01, m12, m20, in bp, silMask))
                    {
                        hitSil = true;
                        if (bestSilOwner < 0)
                        {
                            bestSilOwner = mbIdx;
                        }
                    }
                }

                if (hitSil && (uint)bitPos < 64u)
                {
                    memberSilMask |= (1UL << bitPos);
                }
            }

            if (bestColorCentroidOwner >= 0) return bestColorCentroidOwner;
            if (bestColorAnyOwner >= 0) return bestColorAnyOwner;
            if (bestSilOwner >= 0) return bestSilOwner;
            return -1;
        }

        /// <summary>
        /// Calibrates the silhouette outline dilation radius (in pixels) and silhouette color directly from the intact
        /// skinned layer's color and silhouette textures so newly exposed severed stumps match intact outlines seamlessly.
        /// </summary>
        public static float CalibrateOutlineRadiusPx(
            Color32[] srcColor,
            Color32[] srcSil,
            int width,
            int height,
            GiantSkinnedMeshDef meshDef,
            out Color32 silhouetteColor)
        {
            silhouetteColor = new Color32(20, 22, 26, 255);
            float worldW = meshDef != null
                ? (meshDef.HasWorldBoundsXZ ? (meshDef.worldMaxXZ.x - meshDef.worldMinXZ.x) : meshDef.drawSize.x)
                : 4f;
            float pxPerCell = width / Mathf.Max(0.01f, worldW);
            float fallbackRadius = Mathf.Clamp(0.165f * pxPerCell, 3.5f, 22.0f);

            int total = width * height;
            if (srcColor == null || srcSil == null || srcColor.Length != total || srcSil.Length != total || total <= 0)
            {
                return fallbackRadius;
            }

            byte ath = meshDef != null ? meshDef.alphaThreshold : (byte)16;
            for (int i = 0; i < total; i++)
            {
                if (srcSil[i].a >= 200)
                {
                    silhouetteColor = new Color32(srcSil[i].r, srcSil[i].g, srcSil[i].b, 255);
                    break;
                }
            }

            var colMask = new bool[total];
            bool anyCol = false;
            for (int i = 0; i < total; i++)
            {
                if (srcColor[i].a >= ath)
                {
                    colMask[i] = true;
                    anyCol = true;
                }
            }
            if (!anyCol)
            {
                return fallbackRadius;
            }

            int[] distSq = ComputeSquaredDistanceTransform(colMask, width, height);
            var samples = new List<float>(256);
            for (int y = 1; y < height - 1; y++)
            {
                int row = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = row + x;
                    if (srcSil[idx].a < ath || colMask[idx])
                    {
                        continue;
                    }
                    if (srcSil[idx - 1].a < ath || srcSil[idx + 1].a < ath
                        || srcSil[idx - width].a < ath || srcSil[idx + width].a < ath)
                    {
                        int d2 = distSq[idx];
                        if (d2 > 0 && d2 < 2500)
                        {
                            samples.Add(Mathf.Sqrt(d2));
                        }
                    }
                }
            }

            if (samples.Count < 8)
            {
                return fallbackRadius;
            }
            samples.Sort();
            float median = samples[samples.Count / 2];
            return Mathf.Clamp(median, 3.0f, 24.0f);
        }

        /// <summary>
        /// Pure C# pixel-exact severing compositor for partially severed multi-bone skinned meshes.
        /// Masks color (and optional glow) to the exact union of surviving member bones' anatomical shapes,
        /// bleeds RGB into adjacent transparent pixels (zero bilinear halo), and synthesizes a seamless
        /// Euclidean-dilated silhouette underlay that wraps every newly exposed stump in a crisp RimWorld outline.
        /// </summary>
        public static bool BuildSeveredLayerPixels(
            GiantRigDef rigDef,
            GiantSkinnedMeshDef meshDef,
            BoneWorldPose[] flatBindPoses,
            ReadOnlySpan<TextureAlphaMask> rigBoneColorMasks,
            ulong visibleMemberMask,
            Color32[] srcColor,
            Color32[] srcGlow,
            int width,
            int height,
            float outlineRadiusPx,
            Color32 silhouetteColor,
            out Color32[] outColorPixels,
            out Color32[] outSilPixels,
            out Color32[] outGlowPixels)
        {
            outColorPixels = null;
            outSilPixels = null;
            outGlowPixels = null;
            int total = width * height;
            if (rigDef == null || meshDef == null || flatBindPoses == null || srcColor == null
                || srcColor.Length != total || total <= 0 || visibleMemberMask == 0UL)
            {
                return false;
            }

            int[] memberIndices = meshDef.resolvedMemberBoneIndices;
            if (memberIndices == null || memberIndices.Length == 0)
            {
                return false;
            }

            // Collect active surviving member bones and their flat bind world AABBs for O(1) rejection
            int clampedM = Math.Min(memberIndices.Length, 64);
            var activeBones = new List<int>(clampedM);
            var activeMasks = new List<TextureAlphaMask>(clampedM);
            var aabbMinX = new List<float>(clampedM);
            var aabbMaxX = new List<float>(clampedM);
            var aabbMinZ = new List<float>(clampedM);
            var aabbMaxZ = new List<float>(clampedM);

            for (int m = 0; m < clampedM; m++)
            {
                if ((visibleMemberMask & (1UL << m)) == 0UL)
                {
                    continue;
                }
                int mbIdx = memberIndices[m];
                if ((uint)mbIdx >= (uint)flatBindPoses.Length
                    || (uint)mbIdx >= (uint)rigBoneColorMasks.Length
                    || rigBoneColorMasks[mbIdx] == null)
                {
                    continue;
                }
                ref readonly BoneWorldPose bp = ref flatBindPoses[mbIdx];
                TextureAlphaMask bm = rigBoneColorMasks[mbIdx];
                Vector2 c00 = bp.UVToWorldXZFlat(bm.minU, bm.minV);
                Vector2 c10 = bp.UVToWorldXZFlat(bm.maxU, bm.minV);
                Vector2 c01 = bp.UVToWorldXZFlat(bm.minU, bm.maxV);
                Vector2 c11 = bp.UVToWorldXZFlat(bm.maxU, bm.maxV);
                float minX = Mathf.Min(Mathf.Min(c00.x, c10.x), Mathf.Min(c01.x, c11.x)) - 0.05f;
                float maxX = Mathf.Max(Mathf.Max(c00.x, c10.x), Mathf.Max(c01.x, c11.x)) + 0.05f;
                float minZ = Mathf.Min(Mathf.Min(c00.y, c10.y), Mathf.Min(c01.y, c11.y)) - 0.05f;
                float maxZ = Mathf.Max(Mathf.Max(c00.y, c10.y), Mathf.Max(c01.y, c11.y)) + 0.05f;

                activeBones.Add(mbIdx);
                activeMasks.Add(bm);
                aabbMinX.Add(minX);
                aabbMaxX.Add(maxX);
                aabbMinZ.Add(minZ);
                aabbMaxZ.Add(maxZ);
            }

            if (activeBones.Count == 0)
            {
                return false;
            }

            // World coordinate mapping for bottom-up pixels (py=0 -> v=0, py=height-1 -> v=1)
            bool hasBounds = meshDef.HasWorldBoundsXZ;
            float wMinX = meshDef.worldMinXZ.x;
            float wMinZ = meshDef.worldMinXZ.y;
            float wSpanX = hasBounds ? (meshDef.worldMaxXZ.x - meshDef.worldMinXZ.x) : meshDef.drawSize.x;
            float wSpanZ = hasBounds ? (meshDef.worldMaxXZ.y - meshDef.worldMinXZ.y) : meshDef.drawSize.y;
            float invW = 1f / width;
            float invH = 1f / height;

            Vector2 bindJointXZ = Vector2.zero;
            float bindCos = 1f;
            float bindSin = 0f;
            if (!hasBounds)
            {
                float bindAng = SkeletalKinematics.NormalizeAngle360(meshDef.baseAngle);
                int anchorIdx = meshDef.resolvedAnchorBoneIndex;
                if ((uint)anchorIdx < (uint)flatBindPoses.Length && !string.IsNullOrEmpty(meshDef.anchorBoneId))
                {
                    ref readonly BoneWorldPose ap = ref flatBindPoses[anchorIdx];
                    bindJointXZ = (meshDef.parentSocketPixel.x >= 0f && meshDef.parentSocketPixel.y >= 0f)
                        ? ap.UVToWorldXZFlat(meshDef.resolvedParentSocketUV.x, meshDef.resolvedParentSocketUV.y)
                        : ap.jointWorldXZ;
                    bindAng = SkeletalKinematics.NormalizeAngle360(ap.worldAngleDeg + meshDef.baseAngle);
                }
                float br = bindAng * Mathf.Deg2Rad;
                bindCos = Mathf.Cos(br);
                bindSin = Mathf.Sin(br);
            }

            byte ath = meshDef.alphaThreshold;
            var surviveStrict = new bool[total];
            int activeCount = activeBones.Count;
            bool anySurviving = false;

            for (int py = 0; py < height; py++)
            {
                int row = py * width;
                float v = (py + 0.5f) * invH;
                float wzFlat = wMinZ + v * wSpanZ;
                float lzQuad = (v - meshDef.resolvedPivotUV.y) * wSpanZ;

                for (int px = 0; px < width; px++)
                {
                    int idx = row + px;
                    if (srcColor[idx].a < ath)
                    {
                        continue;
                    }

                    float wx, wz;
                    if (hasBounds)
                    {
                        wx = wMinX + ((px + 0.5f) * invW) * wSpanX;
                        wz = wzFlat;
                    }
                    else
                    {
                        float lxQuad = ((px + 0.5f) * invW - meshDef.resolvedPivotUV.x) * wSpanX;
                        wx = bindJointXZ.x + (lxQuad * bindCos + lzQuad * bindSin);
                        wz = bindJointXZ.y + (-lxQuad * bindSin + lzQuad * bindCos);
                    }

                    for (int a = 0; a < activeCount; a++)
                    {
                        if (wx < aabbMinX[a] || wx > aabbMaxX[a] || wz < aabbMinZ[a] || wz > aabbMaxZ[a])
                        {
                            continue;
                        }
                        if (flatBindPoses[activeBones[a]].TryWorldToUV(wx, wz, out float bu, out float bv)
                            && activeMasks[a].IsOpaqueUV(bu, bv))
                        {
                            surviveStrict[idx] = true;
                            anySurviving = true;
                            break;
                        }
                    }
                }
            }

            if (!anySurviving)
            {
                return false;
            }

            // Preserve 1-pixel anti-aliased outer feathering adjacent to surviving opaque pixels
            outColorPixels = new Color32[total];
            bool hasGlow = srcGlow != null && srcGlow.Length == total;
            outGlowPixels = hasGlow ? new Color32[total] : null;
            for (int py = 0; py < height; py++)
            {
                int row = py * width;
                for (int px = 0; px < width; px++)
                {
                    int idx = row + px;
                    if (surviveStrict[idx])
                    {
                        outColorPixels[idx] = srcColor[idx];
                        if (hasGlow) outGlowPixels[idx] = srcGlow[idx];
                    }
                    else if (srcColor[idx].a > 0)
                    {
                        bool adjSurvive = (px > 0 && surviveStrict[idx - 1])
                            || (px + 1 < width && surviveStrict[idx + 1])
                            || (py > 0 && surviveStrict[idx - width])
                            || (py + 1 < height && surviveStrict[idx + width]);
                        if (adjSurvive)
                        {
                            outColorPixels[idx] = srcColor[idx];
                            if (hasGlow) outGlowPixels[idx] = srcGlow[idx];
                        }
                    }
                }
            }

            // Bleed RGB 2px into transparent border pixels to prevent Unity bilinear dark fringe along cut seams
            BleedTransparentRgb(outColorPixels, width, height, passes: 2);
            if (hasGlow)
            {
                BleedTransparentRgb(outGlowPixels, width, height, passes: 2);
            }

            // Synthesize crisp Euclidean-dilated #14161A silhouette wrapping both intact edges and newly cut stumps
            int[] distSq = ComputeSquaredDistanceTransform(surviveStrict, width, height);
            float rClamped = Mathf.Clamp(outlineRadiusPx, 2.0f, 28.0f);
            float rSq = rClamped * rClamped;
            outSilPixels = new Color32[total];
            Color32 silOpaque = new Color32(silhouetteColor.r, silhouetteColor.g, silhouetteColor.b, 255);
            Color32 silTrans = new Color32(silhouetteColor.r, silhouetteColor.g, silhouetteColor.b, 0);
            for (int i = 0; i < total; i++)
            {
                outSilPixels[i] = distSq[i] <= rSq ? silOpaque : silTrans;
            }

            return true;
        }

        /// <summary>
        /// Exact linear-time O(W*H) Meijster 2D Euclidean Distance Transform returning exact integer squared distance
        /// from every pixel to the nearest foreground==true pixel.
        /// </summary>
        public static int[] ComputeSquaredDistanceTransform(bool[] foreground, int width, int height)
        {
            int total = width * height;
            var g = new int[total];
            var distSq = new int[total];
            int inf = width + height + 1;

            // Phase 1: vertical column distance
            for (int x = 0; x < width; x++)
            {
                g[x] = foreground[x] ? 0 : inf;
                for (int y = 1; y < height; y++)
                {
                    int idx = y * width + x;
                    g[idx] = foreground[idx] ? 0 : Math.Min(inf, g[idx - width] + 1);
                }
                for (int y = height - 2; y >= 0; y--)
                {
                    int idx = y * width + x;
                    int below = g[idx + width] + 1;
                    if (below < g[idx])
                    {
                        g[idx] = below;
                    }
                }
            }

            // Phase 2: horizontal lower envelope of parabolas per row
            var s = new int[width];
            var t = new int[width];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int q = -1;
                for (int u = 0; u < width; u++)
                {
                    int gu = g[row + u];
                    if (gu >= inf)
                    {
                        continue;
                    }
                    long gu2 = (long)gu * gu;
                    while (q >= 0)
                    {
                        int sq = s[q];
                        int tq = t[q];
                        int gq = g[row + sq];
                        long fOld = (long)(tq - sq) * (tq - sq) + (long)gq * gq;
                        long fNew = (long)(tq - u) * (tq - u) + gu2;
                        if (fOld <= fNew)
                        {
                            break;
                        }
                        q--;
                    }
                    if (q < 0)
                    {
                        q = 0;
                        s[0] = u;
                        t[0] = 0;
                    }
                    else
                    {
                        int sq = s[q];
                        int gq = g[row + sq];
                        long num = (long)u * u - (long)sq * sq + gu2 - (long)gq * gq;
                        long den = 2L * (u - sq);
                        long sep = num / den;
                        if (num % den != 0L && num < 0L)
                        {
                            sep--;
                        }
                        long wSep = sep + 1L;
                        if (wSep < width)
                        {
                            q++;
                            s[q] = u;
                            t[q] = wSep < 0L ? 0 : (int)wSep;
                        }
                    }
                }

                if (q < 0)
                {
                    for (int x = 0; x < width; x++)
                    {
                        distSq[row + x] = int.MaxValue;
                    }
                }
                else
                {
                    for (int x = width - 1; x >= 0; x--)
                    {
                        while (q > 0 && x < t[q])
                        {
                            q--;
                        }
                        int sq = s[q];
                        int dx = x - sq;
                        int gq = g[row + sq];
                        distSq[row + x] = dx * dx + gq * gq;
                    }
                }
            }

            return distSq;
        }

        private static void BleedTransparentRgb(Color32[] pixels, int width, int height, int passes)
        {
            int total = width * height;
            var hasColor = new bool[total];
            for (int i = 0; i < total; i++)
            {
                hasColor[i] = pixels[i].a > 0;
            }
            var nextHasColor = new bool[total];
            for (int p = 0; p < passes; p++)
            {
                Array.Copy(hasColor, nextHasColor, total);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = row + x;
                        if (hasColor[idx])
                        {
                            continue;
                        }
                        int rSum = 0, gSum = 0, bSum = 0, cnt = 0;
                        if (x > 0 && hasColor[idx - 1]) { Color32 c = pixels[idx - 1]; rSum += c.r; gSum += c.g; bSum += c.b; cnt++; }
                        if (x + 1 < width && hasColor[idx + 1]) { Color32 c = pixels[idx + 1]; rSum += c.r; gSum += c.g; bSum += c.b; cnt++; }
                        if (y > 0 && hasColor[idx - width]) { Color32 c = pixels[idx - width]; rSum += c.r; gSum += c.g; bSum += c.b; cnt++; }
                        if (y + 1 < height && hasColor[idx + width]) { Color32 c = pixels[idx + width]; rSum += c.r; gSum += c.g; bSum += c.b; cnt++; }
                        if (cnt > 0)
                        {
                            pixels[idx] = new Color32((byte)(rSum / cnt), (byte)(gSum / cnt), (byte)(bSum / cnt), 0);
                            nextHasColor[idx] = true;
                        }
                    }
                }
                (hasColor, nextHasColor) = (nextHasColor, hasColor);
            }
        }

        private static int SelectDominantBone(in SkinnedVertexBinding a, in SkinnedVertexBinding b, in SkinnedVertexBinding c)
        {
            int cand0 = a.b0;
            float s0 = AccumulateBoneWeight(cand0, in a) + AccumulateBoneWeight(cand0, in b) + AccumulateBoneWeight(cand0, in c);
            int bestBone = cand0;
            float bestScore = s0;

            int cand1 = b.b0;
            if (cand1 != bestBone)
            {
                float s1 = AccumulateBoneWeight(cand1, in a) + AccumulateBoneWeight(cand1, in b) + AccumulateBoneWeight(cand1, in c);
                if (s1 > bestScore) { bestBone = cand1; bestScore = s1; }
            }

            int cand2 = c.b0;
            if (cand2 != bestBone)
            {
                float s2 = AccumulateBoneWeight(cand2, in a) + AccumulateBoneWeight(cand2, in b) + AccumulateBoneWeight(cand2, in c);
                if (s2 > bestScore) { bestBone = cand2; }
            }
            return bestBone;
        }

        private static float AccumulateBoneWeight(int boneIdx, in SkinnedVertexBinding v)
        {
            float s = 0f;
            if (v.influenceCount >= 1 && v.b0 == boneIdx) s += v.w0;
            if (v.influenceCount >= 2 && v.b1 == boneIdx) s += v.w1;
            if (v.influenceCount >= 3 && v.b2 == boneIdx) s += v.w2;
            if (v.influenceCount >= 4 && v.b3 == boneIdx) s += v.w3;
            return s;
        }

        private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq <= 1e-6f)
            {
                return Vector2.Distance(p, a);
            }
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            Vector2 proj = a + ab * t;
            return Vector2.Distance(p, proj);
        }

        private static bool IsCellOpaqueWithMargin(TextureAlphaMask mask, int gx, int gy, int nx, int ny)
        {
            float marginU = 1.5f / Mathf.Max(16, mask.width);
            float marginV = 1.5f / Mathf.Max(16, mask.height);
            float u0 = Mathf.Max(0f, ((float)gx / nx) - marginU);
            float u1 = Mathf.Min(0.9999f, ((float)(gx + 1) / nx) + marginU);
            float v0 = Mathf.Max(0f, ((float)gy / ny) - marginV);
            float v1 = Mathf.Min(0.9999f, ((float)(gy + 1) / ny) + marginV);

            const int samples = 4;
            for (int sy = 0; sy <= samples; sy++)
            {
                float v = Mathf.Lerp(v0, v1, (float)sy / samples);
                for (int sx = 0; sx <= samples; sx++)
                {
                    float u = Mathf.Lerp(u0, u1, (float)sx / samples);
                    if (mask.IsOpaqueUV(u, v))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
