using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Physics profile for a detached giant limb/part falling to the ground in 2.5D top-down space.
    /// Pure value struct for deterministic headless testing and category-specific weight feel.
    /// </summary>
    public struct SeveredDebrisProfile
    {
        public float startHeight;
        public float gravity;
        public float outwardSpeed;
        public float upwardVel;
        public float angularVelDeg;
        public float restitution;
        public int maxBounces;
        public int restDurationTicks;
        public int fadeDurationTicks;
    }

    public sealed class SeveredBoneSpritePiece
    {
        public string TexPath;
        public string SilhouetteTexPath;
        public Matrix4x4 LocalQuadMatrix;
        public float LayerOffset;
        public float SilhouetteLayerOffset;
    }

    public sealed class SeveredSkinnedMeshPiece
    {
        public Mesh SnapshotMesh;
        public GiantSkinnedSeveredTextureCache.SeveredMaterialBundle SeveredBundle;
        public string FallbackTexPath;
        public string FallbackSilTexPath;
        public Shader MainShader;
        public float LayerOffset;
        public float SilhouetteLayerOffset;
        public bool IsFullMesh;
    }

    public sealed class SeveredSoftWhipPiece
    {
        public Mesh SnapshotMesh;
        public string TexPath;
        public string SilhouetteTexPath;
        public float LayerOffset;
        public float SilhouetteLayerOffset;
    }

    /// <summary>
    /// Represents one coherent severed limb/tail/wing/turret/tentacle subtree tumbling through the air,
    /// bouncing on touchdown, resting darkened on the ground, and fading out cleanly.
    /// </summary>
    public sealed class SeveredDebrisInstance
    {
        public const float SecondsPerTick = 1f / 60f;
        public const float HeightVisualShiftZFactor = 0.40f;
        public const float HeightScaleBoostFactor = 0.075f;
        public const float MaxHeightScaleBoost = 0.26f;

        public Map Map;
        public int SlotId;
        public string DebugRootBoneId;
        public Vector2 GroundPosXZ;
        public Vector2 VelXZ;
        public float Height;
        public float LaunchHeightBaseline;
        public float VelY;
        public float AngleDeg;
        public float AngularVelDeg;
        public float Gravity;
        public float Restitution;
        public int MaxBounces;
        public int BounceCount;
        public float ApproxRadius;
        public bool IsSolidOrMech;
        public bool IsProtoss;
        public bool SpawnedFromAirborne;
        public GiantSeverVfxKind Kind;

        public int ElapsedTicks;
        public int LandedTick;
        public int SettledTick;
        public int RestDurationTicks;
        public int FadeDurationTicks;
        public float ImpactSquashTicks;
        public bool HasLandedOnce;
        public bool IsSettled;
        public bool IsExpired;
        public int NextSmokeTick;

        public readonly List<SeveredBoneSpritePiece> BonePieces = new List<SeveredBoneSpritePiece>(4);
        public readonly List<SeveredSkinnedMeshPiece> SkinnedPieces = new List<SeveredSkinnedMeshPiece>(2);
        public readonly List<SeveredSoftWhipPiece> WhipPieces = new List<SeveredSoftWhipPiece>(1);

        public float ComputeFadeAlpha()
        {
            if (!IsSettled)
            {
                return 1f;
            }
            int settledAge = ElapsedTicks - SettledTick;
            if (settledAge <= RestDurationTicks)
            {
                return 1f;
            }
            if (FadeDurationTicks <= 0)
            {
                return 0f;
            }
            float t = (settledAge - RestDurationTicks) / (float)FadeDurationTicks;
            return Mathf.Clamp01(1f - t);
        }

        public Color ComputeBodyTint(float fadeAlpha)
        {
            float darkenT = Mathf.Clamp01(ElapsedTicks / 48f);
            Color deadTarget = IsSolidOrMech
                ? new Color(0.58f, 0.55f, 0.53f, 1f)
                : new Color(0.64f, 0.52f, 0.54f, 1f);
            Color c = Color.Lerp(Color.white, deadTarget, darkenT);
            c.a = fadeAlpha;
            return c;
        }

        public float ComputeVisualScale()
        {
            float deltaH = Mathf.Max(0f, Height) - LaunchHeightBaseline;
            float airScale = 1f + Mathf.Clamp(deltaH * 0.065f, -0.03f, MaxHeightScaleBoost);
            if (ImpactSquashTicks > 0f)
            {
                float pulseT = 1f - Mathf.Clamp01(ImpactSquashTicks / 8f);
                airScale *= 1f + 0.042f * Mathf.Sin(pulseT * Mathf.PI);
            }
            return airScale;
        }

        public Vector2 ComputeVisualWorldXZ()
        {
            return new Vector2(GroundPosXZ.x, GroundPosXZ.y + Mathf.Max(0f, Height) * HeightVisualShiftZFactor);
        }

        /// <summary>
        /// Deterministic pure-C# ballistic tick (1/60s). Returns true on the exact tick of first touchdown.
        /// Safe to call headless without UnityEngine.Mesh or Map.
        /// </summary>
        public bool StepTick(out bool triggeredFirstImpact, out bool triggeredBounceImpact, float dt = SecondsPerTick)
        {
            triggeredFirstImpact = false;
            triggeredBounceImpact = false;
            if (IsExpired)
            {
                return false;
            }

            ElapsedTicks++;
            if (ImpactSquashTicks > 0f)
            {
                ImpactSquashTicks = Mathf.Max(0f, ImpactSquashTicks - 1f);
            }

            if (!IsSettled)
            {
                GroundPosXZ += VelXZ * dt;
                AngleDeg += AngularVelDeg * dt;

                if (Map != null)
                {
                    float maxX = Mathf.Max(2f, Map.Size.x - 1.5f);
                    float maxZ = Mathf.Max(2f, Map.Size.z - 1.5f);
                    GroundPosXZ.x = Mathf.Clamp(GroundPosXZ.x, 1.5f, maxX);
                    GroundPosXZ.y = Mathf.Clamp(GroundPosXZ.y, 1.5f, maxZ);
                }

                if (Height > 0f || VelY > 0f)
                {
                    VelY -= Gravity * dt;
                    Height += VelY * dt;
                    VelXZ *= 0.990f;
                    AngularVelDeg *= 0.992f;

                    if (Height <= 0f)
                    {
                        Height = 0f;
                        if (!HasLandedOnce)
                        {
                            HasLandedOnce = true;
                            LandedTick = ElapsedTicks;
                            ImpactSquashTicks = 8f;
                            triggeredFirstImpact = true;
                        }
                        else if (-VelY > 1.0f)
                        {
                            ImpactSquashTicks = 4f;
                            triggeredBounceImpact = true;
                        }

                        if (BounceCount < MaxBounces && -VelY > 1.25f)
                        {
                            BounceCount++;
                            VelY = -VelY * Restitution;
                            VelXZ *= 0.60f;
                            AngularVelDeg *= 0.52f;
                        }
                        else
                        {
                            VelY = 0f;
                            VelXZ *= 0.42f;
                            AngularVelDeg *= 0.25f;
                        }
                    }
                }
                else
                {
                    VelXZ *= 0.84f;
                    AngularVelDeg *= 0.78f;
                    if (VelXZ.sqrMagnitude < 0.03f && Mathf.Abs(AngularVelDeg) < 3.5f)
                    {
                        VelXZ = Vector2.zero;
                        AngularVelDeg = 0f;
                        IsSettled = true;
                        SettledTick = ElapsedTicks;
                    }
                }
            }
            else
            {
                int settledAge = ElapsedTicks - SettledTick;
                if (settledAge >= RestDurationTicks + FadeDurationTicks)
                {
                    IsExpired = true;
                    return false;
                }
            }

            return true;
        }

        public void DisposeGpuResources()
        {
            for (int i = 0; i < SkinnedPieces.Count; i++)
            {
                Mesh m = SkinnedPieces[i].SnapshotMesh;
                if (m != null)
                {
                    UnityEngine.Object.Destroy(m);
                    SkinnedPieces[i].SnapshotMesh = null;
                }
            }
            for (int i = 0; i < WhipPieces.Count; i++)
            {
                Mesh m = WhipPieces[i].SnapshotMesh;
                if (m != null)
                {
                    UnityEngine.Object.Destroy(m);
                    WhipPieces[i].SnapshotMesh = null;
                }
            }
            BonePieces.Clear();
            SkinnedPieces.Clear();
            WhipPieces.Clear();
        }
    }

    /// <summary>
    /// Manages falling-to-the-ground physical destruction animations for severed giant body parts across
    /// all giant categories (biological creatures, mechanoids, vehicles) and render modes (bone sprites,
    /// continuous LBS skinned meshes, and soft-body whip ribbons).
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GiantSeveredDebrisSystem
    {
        private const int MaxActiveDebris = 48;

        private readonly struct DrawLayerEntry : IComparable<DrawLayerEntry>
        {
            public readonly float EffectiveLayer;
            public readonly bool IsSilhouette;
            public readonly int PieceKind; // 0 = bone, 1 = skinned, 2 = whip
            public readonly int PieceIndex;

            public DrawLayerEntry(float effectiveLayer, bool isSilhouette, int pieceKind, int pieceIndex)
            {
                EffectiveLayer = effectiveLayer;
                IsSilhouette = isSilhouette;
                PieceKind = pieceKind;
                PieceIndex = pieceIndex;
            }

            public int CompareTo(DrawLayerEntry other)
            {
                int cmp = EffectiveLayer.CompareTo(other.EffectiveLayer);
                if (cmp != 0) return cmp;
                // Silhouette always draws before color when equal
                if (IsSilhouette != other.IsSilhouette) return IsSilhouette ? -1 : 1;
                return PieceIndex.CompareTo(other.PieceIndex);
            }
        }

        private static readonly List<SeveredDebrisInstance> ActiveDebris = new List<SeveredDebrisInstance>(24);
        private static readonly List<DrawLayerEntry> DrawOrderScratch = new List<DrawLayerEntry>(32);
        private static MaterialPropertyBlock sharedBodyMpb;
        private static MaterialPropertyBlock sharedShadowMpb;
        private static uint deterministicCounter = 31u;
        private static int nextSlotId = 1;

        public static int ActiveDebrisCount => ActiveDebris.Count;
        public static int TotalSpawnedCount { get; private set; }

        public static void ResetCountersForTesting()
        {
            TotalSpawnedCount = 0;
            deterministicCounter = 31u;
            nextSlotId = 1;
        }

        public static void ClearAll()
        {
            for (int i = 0; i < ActiveDebris.Count; i++)
            {
                ActiveDebris[i].DisposeGpuResources();
            }
            ActiveDebris.Clear();
        }

        public static void ClearForMap(Map map)
        {
            if (map == null)
            {
                return;
            }
            for (int i = ActiveDebris.Count - 1; i >= 0; i--)
            {
                if (ActiveDebris[i].Map == map)
                {
                    ActiveDebris[i].DisposeGpuResources();
                    ActiveDebris.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Pure static helper: partitions a set of newly missing bones into connected detachment-root subtrees.
        /// Each root bone collects all descendant bones that became missing in the same sever event so whole
        /// tails, wings, legs, or turrets detach and tumble as one cohesive anatomical piece.
        /// </summary>
        public static List<List<int>> PartitionDetachmentRoots(BoneNodeDef[] sortedNodes, IReadOnlyList<int> newlyMissingBones)
        {
            var groups = new List<List<int>>(4);
            if (sortedNodes == null || newlyMissingBones == null || newlyMissingBones.Count == 0)
            {
                return groups;
            }

            int n = sortedNodes.Length;
            var isNew = new bool[n];
            for (int i = 0; i < newlyMissingBones.Count; i++)
            {
                int bIdx = newlyMissingBones[i];
                if ((uint)bIdx < (uint)n)
                {
                    isNew[bIdx] = true;
                }
            }

            // SortedNodes is topologically ordered (parent < child), so assigning root index in one forward pass is exact
            var rootOf = new int[n];
            Array.Fill(rootOf, -1);
            var groupByRoot = new Dictionary<int, List<int>>(4);

            for (int i = 0; i < n; i++)
            {
                if (!isNew[i])
                {
                    continue;
                }
                int pIdx = sortedNodes[i].parentRuntimeIndex;
                int rootIdx = (pIdx >= 0 && pIdx < n && rootOf[pIdx] >= 0) ? rootOf[pIdx] : i;
                rootOf[i] = rootIdx;

                if (!groupByRoot.TryGetValue(rootIdx, out List<int> list))
                {
                    list = new List<int>(6);
                    groupByRoot[rootIdx] = list;
                    groups.Add(list);
                }
                list.Add(i);
            }

            return groups;
        }

        /// <summary>
        /// Pure static helper: derives category-appropriate and mass-scaled ballistic fall parameters.
        /// </summary>
        public static SeveredDebrisProfile ComputeDebrisProfile(
            GiantSeverVfxKind kind,
            float approxRadiusCells,
            float flightAltitude01,
            int seed)
        {
            float r = Mathf.Clamp(approxRadiusCells, 0.35f, 5.5f);
            float massT = Mathf.Clamp01((r - 0.55f) / 3.8f);
            float hRand = (((seed * 1103515245 + 12345) >> 16) & 0x7FFF) / 32767f;
            float spinSign = (seed & 1) == 0 ? 1f : -1f;

            float baseH = 0.62f + Mathf.Clamp(r * 0.14f, 0f, 0.44f);
            float airExtra = flightAltitude01 > 0.02f ? (1.15f + 2.65f * Mathf.Clamp01(flightAltitude01)) : 0f;

            switch (kind)
            {
                case GiantSeverVfxKind.MechExplosion:
                    return new SeveredDebrisProfile
                    {
                        startHeight = baseH + airExtra,
                        gravity = 17.8f,
                        outwardSpeed = Mathf.Lerp(5.4f, 3.1f, massT) * (0.92f + 0.18f * hRand),
                        upwardVel = Mathf.Lerp(5.8f, 4.1f, massT),
                        angularVelDeg = Mathf.Lerp(165f, 52f, massT) * spinSign,
                        restitution = 0.31f,
                        maxBounces = 2,
                        restDurationTicks = 720,
                        fadeDurationTicks = 180
                    };

                case GiantSeverVfxKind.VoidRupture:
                    return new SeveredDebrisProfile
                    {
                        startHeight = baseH + airExtra,
                        gravity = 12.8f,
                        outwardSpeed = Mathf.Lerp(4.4f, 2.4f, massT) * (0.92f + 0.18f * hRand),
                        upwardVel = Mathf.Lerp(4.6f, 3.3f, massT),
                        angularVelDeg = Mathf.Lerp(125f, 38f, massT) * spinSign,
                        restitution = 0.22f,
                        maxBounces = 1,
                        restDurationTicks = 660,
                        fadeDurationTicks = 180
                    };

                case GiantSeverVfxKind.FleshSever:
                default:
                    return new SeveredDebrisProfile
                    {
                        startHeight = baseH + airExtra,
                        gravity = 15.2f,
                        outwardSpeed = Mathf.Lerp(4.8f, 2.8f, massT) * (0.92f + 0.18f * hRand),
                        upwardVel = Mathf.Lerp(5.1f, 3.7f, massT),
                        angularVelDeg = Mathf.Lerp(150f, 46f, massT) * spinSign,
                        restitution = 0.24f,
                        maxBounces = 1,
                        restDurationTicks = 660,
                        fadeDurationTicks = 180
                    };
            }
        }

        /// <summary>
        /// Spawns falling-debris clusters for all newly severed bones in a single health-update pass.
        /// Captures exact pre-sever bone quads and continuous skinned-mesh sub-chunks at t = 0.
        /// </summary>
        public static void SpawnForSeveredBones(
            Pawn giant,
            CompGiantRig comp,
            IReadOnlyList<int> newlyMissingBones,
            ReadOnlySpan<BoneWorldPose> preSeverPosesRootRel,
            Vector2 pawnDrawPosXZ)
        {
            if (giant == null || comp?.RigDef?.SortedNodes == null || newlyMissingBones == null || newlyMissingBones.Count == 0)
            {
                return;
            }
            Map map = giant.Map;
            if (map == null || !UnityData.IsInMainThread)
            {
                return;
            }

            GiantRigDef rigDef = comp.RigDef;
            BoneNodeDef[] nodes = rigDef.SortedNodes;
            int nodeCount = nodes.Length;
            if (preSeverPosesRootRel.Length < nodeCount)
            {
                return;
            }

            List<List<int>> rootGroups = PartitionDetachmentRoots(nodes, newlyMissingBones);
            if (rootGroups.Count == 0)
            {
                return;
            }

            comp.EnsureAlphaMasksLoaded();
            ReadOnlySpan<SkinnedMeshRuntime> skinRuntimes = comp.SkinnedMeshRuntimesSpan;
            int skinCount = rigDef.skinnedMeshes != null ? Math.Min(rigDef.skinnedMeshes.Count, skinRuntimes.Length) : 0;
            float flightAlt01 = comp.CurrentFlightAltitude01;
            bool isSolidOrMech = (giant.RaceProps?.IsMechanoid ?? false) || GiantFastLookup.TryGetCockpit(giant) != null;
            bool isProtoss = giant.def?.defName != null
                && giant.def.defName.IndexOf("Protoss", StringComparison.OrdinalIgnoreCase) >= 0;

            for (int g = 0; g < rootGroups.Count; g++)
            {
                List<int> groupBones = rootGroups[g];
                if (groupBones.Count == 0)
                {
                    continue;
                }

                int rootBoneIdx = groupBones[0];
                BoneNodeDef rootBone = nodes[rootBoneIdx];
                if (!rootBone.hideWhenMissing)
                {
                    continue;
                }

                // Mark group membership for fast lookup
                var inGroup = new bool[nodeCount];
                for (int i = 0; i < groupBones.Count; i++)
                {
                    int b = groupBones[i];
                    if ((uint)b < (uint)nodeCount && nodes[b].hideWhenMissing)
                    {
                        inGroup[b] = true;
                    }
                }

                // Collect raw geometry & unified world AABB across all skinned-mesh slices and non-skinned bones
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                bool hasAnyGeo = false;

                var pendingSkinned = new List<(SkinnedMeshRuntime rt, GiantSkinnedMeshDef def, ulong mask, Vector2[] vertsXZ, Vector2[] uvs, int[] tris, bool isFull)>(2);
                var pendingBones = new List<(BoneNodeDef bDef, BoneWorldPose worldPose, string tex, string sil)>(4);

                for (int m = 0; m < skinCount; m++)
                {
                    GiantSkinnedMeshDef smDef = rigDef.skinnedMeshes[m];
                    SkinnedMeshRuntime rt = skinRuntimes[m];
                    if (smDef == null || rt == null || smDef.resolvedMemberBoneIndices == null || smDef.resolvedMemberBoneIndices.Length == 0)
                    {
                        continue;
                    }

                    int[] mems = smDef.resolvedMemberBoneIndices;
                    int clampedM = Math.Min(mems.Length, 64);
                    ulong detachedMask = 0UL;
                    for (int mb = 0; mb < clampedM; mb++)
                    {
                        int bIdx = mems[mb];
                        if ((uint)bIdx < (uint)nodeCount && inGroup[bIdx])
                        {
                            detachedMask |= (1UL << mb);
                        }
                    }

                    if (detachedMask == 0UL)
                    {
                        continue;
                    }

                    if (!rt.IsBindInitialized)
                    {
                        string maskPath = !string.IsNullOrEmpty(smDef.silhouetteTexPath) ? smDef.silhouetteTexPath : smDef.texPath;
                        TextureAlphaMask mask = !string.IsNullOrEmpty(maskPath)
                            ? GiantAlphaMaskCache.GetOrLoad(maskPath, smDef.alphaThreshold)
                            : null;
                        rt.EnsureBindTopology(mask, comp.AlphaMasksSpan, comp.SilhouetteAlphaMasksSpan);
                    }

                    if (rt.TryExtractDetachedMeshSnapshot(
                        preSeverPosesRootRel,
                        pawnDrawPosXZ,
                        detachedMask,
                        out Vector2[] wVerts,
                        out Vector2[] wUVs,
                        out int[] wTris,
                        out Vector2 sMin,
                        out Vector2 sMax,
                        out bool isFull))
                    {
                        pendingSkinned.Add((rt, smDef, detachedMask, wVerts, wUVs, wTris, isFull));
                        if (sMin.x < minX) minX = sMin.x;
                        if (sMax.x > maxX) maxX = sMax.x;
                        if (sMin.y < minZ) minZ = sMin.y;
                        if (sMax.y > maxZ) maxZ = sMax.y;
                        hasAnyGeo = true;
                    }
                }

                for (int i = 0; i < groupBones.Count; i++)
                {
                    int bIdx = groupBones[i];
                    BoneNodeDef bDef = nodes[bIdx];
                    if (!bDef.hideWhenMissing || bDef.isRenderedBySkinnedMesh)
                    {
                        continue;
                    }
                    BoneWorldPose pose = preSeverPosesRootRel[bIdx];
                    string texPath = bDef.GetTexPath(pose.activeSector);
                    if (string.IsNullOrEmpty(texPath))
                    {
                        continue;
                    }
                    string silPath = bDef.GetSilhouetteTexPath(pose.activeSector);

                    Vector2 centerW = pawnDrawPosXZ + ((pose.hasCoronalFlap || pose.hasSagittalShear) ? pose.flatQuadCenterWorldXZ : pose.quadCenterWorldXZ);
                    float halfW = pose.worldSize.x * 0.5f;
                    float halfH = pose.worldSize.y * 0.5f;
                    float radEst = Mathf.Sqrt(halfW * halfW + halfH * halfH) * 0.72f;
                    if (centerW.x - radEst < minX) minX = centerW.x - radEst;
                    if (centerW.x + radEst > maxX) maxX = centerW.x + radEst;
                    if (centerW.y - radEst < minZ) minZ = centerW.y - radEst;
                    if (centerW.y + radEst > maxZ) maxZ = centerW.y + radEst;

                    // Store world-shifted quad center inside a copy
                    pose.quadCenterWorldXZ = centerW;
                    pose.flatQuadCenterWorldXZ = centerW;
                    pendingBones.Add((bDef, pose, texPath, silPath));
                    hasAnyGeo = true;
                }

                if (!hasAnyGeo)
                {
                    continue;
                }

                Vector2 centroidXZ = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
                float spanX = Mathf.Max(0.35f, maxX - minX);
                float spanZ = Mathf.Max(0.35f, maxZ - minZ);
                float approxRadius = Mathf.Sqrt(spanX * spanX + spanZ * spanZ) * 0.5f;

                int seed = unchecked((int)(deterministicCounter++ * 2654435761u) ^ (giant.thingIDNumber * 31) ^ (rootBoneIdx * 97));
                GiantSeverVfxKind sevKind = GiantVfxSystem.ResolveEffectiveSeverVfxKind(giant, rootBone);
                if (sevKind == GiantSeverVfxKind.None)
                {
                    sevKind = isSolidOrMech ? GiantSeverVfxKind.MechExplosion : GiantSeverVfxKind.FleshSever;
                }

                SeveredDebrisProfile prof = ComputeDebrisProfile(sevKind, approxRadius, flightAlt01, seed);

                // Always eject strictly outward away from the giant's torso center so backward-swept
                // appendages (wings, horns, shins) NEVER fling inward across the giant's back
                Vector2 rootJointW = pawnDrawPosXZ + preSeverPosesRootRel[rootBoneIdx].jointWorldXZ;
                Vector2 fromCenter = centroidXZ - pawnDrawPosXZ;
                Vector2 fromJoint = centroidXZ - rootJointW;
                Vector2 outward = fromCenter.sqrMagnitude > 0.04f ? fromCenter.normalized : Vector2.zero;
                if (fromJoint.sqrMagnitude > 0.04f)
                {
                    Vector2 jf = fromJoint.normalized;
                    if (outward.sqrMagnitude < 0.01f)
                    {
                        outward = jf;
                    }
                    else if (Vector2.Dot(jf, outward) > 0.25f)
                    {
                        outward = (outward * 0.72f + jf * 0.28f).normalized;
                    }
                }
                if (outward.sqrMagnitude < 0.01f)
                {
                    float fallbackRad = preSeverPosesRootRel[rootBoneIdx].worldAngleDeg * Mathf.Deg2Rad;
                    outward = new Vector2(Mathf.Sin(fallbackRad), Mathf.Cos(fallbackRad));
                }
                outward.Normalize();

                // Add subtle deterministic scatter (-14..+14 deg) so consecutive severs land cleanly apart
                float jitter01 = (((seed * 214013 + 2531011) >> 16) & 0x7FFF) / 32767f;
                float jitterRad = (jitter01 * 2f - 1f) * 14f * Mathf.Deg2Rad;
                float jc = Mathf.Cos(jitterRad);
                float js = Mathf.Sin(jitterRad);
                Vector2 ejectDir = new Vector2(outward.x * jc - outward.y * js, outward.x * js + outward.y * jc);

                // Choose physically intuitive spin direction based on side of torso
                Vector2 relFromCenter = centroidXZ - pawnDrawPosXZ;
                float crossSign = (relFromCenter.x * ejectDir.y - relFromCenter.y * ejectDir.x);
                float angVel = Mathf.Abs(prof.angularVelDeg) * (Mathf.Abs(crossSign) > 0.05f ? Mathf.Sign(crossSign) : (prof.angularVelDeg >= 0f ? 1f : -1f));

                // Align visual start position at t=0 with the exact pre-sever rendered position (0.0 px pop)
                Vector2 flightVisOff = comp.CurrentFlightVisualOffsetXZ;
                Vector2 visualStartXZ = centroidXZ + flightVisOff;
                Vector2 groundStartXZ = new Vector2(visualStartXZ.x, visualStartXZ.y - prof.startHeight * SeveredDebrisInstance.HeightVisualShiftZFactor);

                var inst = new SeveredDebrisInstance
                {
                    Map = map,
                    SlotId = nextSlotId++,
                    DebugRootBoneId = rootBone.nodeId ?? "bone",
                    GroundPosXZ = groundStartXZ,
                    VelXZ = ejectDir * prof.outwardSpeed,
                    Height = prof.startHeight,
                    LaunchHeightBaseline = prof.startHeight,
                    VelY = prof.upwardVel,
                    AngleDeg = 0f,
                    AngularVelDeg = angVel,
                    Gravity = prof.gravity,
                    Restitution = prof.restitution,
                    MaxBounces = prof.maxBounces,
                    ApproxRadius = approxRadius,
                    IsSolidOrMech = isSolidOrMech,
                    IsProtoss = isProtoss,
                    SpawnedFromAirborne = flightAlt01 > 0.02f,
                    Kind = sevKind,
                    RestDurationTicks = prof.restDurationTicks,
                    FadeDurationTicks = prof.fadeDurationTicks,
                    NextSmokeTick = 18
                };

                // Build centered GPU meshes for skinned-mesh sub-chunks
                for (int s = 0; s < pendingSkinned.Count; s++)
                {
                    var entry = pendingSkinned[s];
                    Shader mainShader = entry.def.shaderTypeDef != null ? entry.def.shaderTypeDef.Shader : ShaderDatabase.Cutout;
                    GiantSkinnedSeveredTextureCache.SeveredMaterialBundle bundle = null;
                    if (!entry.isFull)
                    {
                        bundle = GiantSkinnedSeveredTextureCache.ResolveOrBuildOnMainThread(
                            entry.rt,
                            comp.AlphaMasksSpan,
                            entry.mask,
                            mainShader);
                    }

                    Mesh mesh = BuildCenteredMesh(
                        $"GCF_DebrisSkin_{entry.def.meshId}_{inst.SlotId}",
                        entry.vertsXZ,
                        entry.uvs,
                        entry.tris,
                        centroidXZ);

                    inst.SkinnedPieces.Add(new SeveredSkinnedMeshPiece
                    {
                        SnapshotMesh = mesh,
                        SeveredBundle = bundle,
                        FallbackTexPath = entry.def.texPath,
                        FallbackSilTexPath = entry.def.silhouetteTexPath,
                        MainShader = mainShader,
                        LayerOffset = entry.def.layerOffset,
                        SilhouetteLayerOffset = entry.def.silhouetteLayerOffset,
                        IsFullMesh = entry.isFull
                    });
                }

                // Build centered quad matrices for non-skinned bones
                for (int b = 0; b < pendingBones.Count; b++)
                {
                    var entry = pendingBones[b];
                    Vector2 localOff = entry.worldPose.quadCenterWorldXZ - centroidXZ;
                    Matrix4x4 localMat = Matrix4x4.TRS(
                        new Vector3(localOff.x, 0f, localOff.y),
                        Quaternion.AngleAxis(entry.worldPose.worldAngleDeg, Vector3.up),
                        new Vector3(entry.worldPose.worldSize.x, 1f, entry.worldPose.worldSize.y));
                    inst.BonePieces.Add(new SeveredBoneSpritePiece
                    {
                        TexPath = entry.tex,
                        SilhouetteTexPath = entry.sil,
                        LocalQuadMatrix = localMat,
                        LayerOffset = entry.bDef.GetLayerOffset(entry.worldPose.activeSector),
                        SilhouetteLayerOffset = entry.bDef.silhouetteLayerOffset
                    });
                }

                AddActiveDebris(inst);
            }
        }

        /// <summary>
        /// Spawns a falling-debris ribbon for a severed soft-body whip/tentacle.
        /// </summary>
        public static void SpawnForSeveredSoftWhip(
            Pawn giant,
            CompGiantRig comp,
            SoftWhipDef whipDef,
            SoftWhipRibbonSnapshot snap,
            Vector2 pawnDrawPosXZ)
        {
            if (giant == null || comp == null || whipDef == null || snap == null
                || snap.stripLeftLocalXZ == null || snap.stripRightLocalXZ == null || snap.stripLeftLocalXZ.Length < 2)
            {
                return;
            }
            Map map = giant.Map;
            if (map == null || !UnityData.IsInMainThread || string.IsNullOrEmpty(whipDef.texPath))
            {
                return;
            }

            int m = snap.stripLeftLocalXZ.Length;
            var worldVerts = new Vector2[m * 2];
            var uvs = new Vector2[m * 2];
            var tris = new int[(m - 1) * 6];
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int j = 0; j < m; j++)
            {
                Vector2 lW = pawnDrawPosXZ + snap.stripLeftLocalXZ[j];
                Vector2 rW = pawnDrawPosXZ + snap.stripRightLocalXZ[j];
                float v = snap.vCoords != null && j < snap.vCoords.Length ? snap.vCoords[j] : (j / (float)(m - 1));
                int dst = j * 2;
                worldVerts[dst] = lW;
                worldVerts[dst + 1] = rW;
                uvs[dst] = new Vector2(0f, v);
                uvs[dst + 1] = new Vector2(1f, v);

                if (lW.x < minX) minX = lW.x;
                if (lW.x > maxX) maxX = lW.x;
                if (lW.y < minZ) minZ = lW.y;
                if (lW.y > maxZ) maxZ = lW.y;
                if (rW.x < minX) minX = rW.x;
                if (rW.x > maxX) maxX = rW.x;
                if (rW.y < minZ) minZ = rW.y;
                if (rW.y > maxZ) maxZ = rW.y;
            }

            int tDst = 0;
            for (int j = 0; j < m - 1; j++)
            {
                int i0 = j * 2;
                int i1 = i0 + 1;
                int i2 = i0 + 2;
                int i3 = i0 + 3;
                tris[tDst++] = i0;
                tris[tDst++] = i2;
                tris[tDst++] = i3;
                tris[tDst++] = i0;
                tris[tDst++] = i3;
                tris[tDst++] = i1;
            }

            Vector2 centroidXZ = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            float approxRadius = Mathf.Sqrt((maxX - minX) * (maxX - minX) + (maxZ - minZ) * (maxZ - minZ)) * 0.5f;
            int seed = unchecked((int)(deterministicCounter++ * 2654435761u) ^ (giant.thingIDNumber * 17));
            SeveredDebrisProfile prof = ComputeDebrisProfile(GiantSeverVfxKind.VoidRupture, approxRadius, comp.CurrentFlightAltitude01, seed);

            Vector2 outward = centroidXZ - pawnDrawPosXZ;
            if (outward.sqrMagnitude < 0.04f) outward = new Vector2(1f, 0f);
            outward.Normalize();

            Vector2 visualStartXZ = centroidXZ + comp.CurrentFlightVisualOffsetXZ;
            Vector2 groundStartXZ = new Vector2(visualStartXZ.x, visualStartXZ.y - prof.startHeight * SeveredDebrisInstance.HeightVisualShiftZFactor);

            var inst = new SeveredDebrisInstance
            {
                Map = map,
                SlotId = nextSlotId++,
                DebugRootBoneId = whipDef.whipId ?? "whip",
                GroundPosXZ = groundStartXZ,
                VelXZ = outward * prof.outwardSpeed,
                Height = prof.startHeight,
                LaunchHeightBaseline = prof.startHeight,
                VelY = prof.upwardVel,
                AngleDeg = 0f,
                AngularVelDeg = prof.angularVelDeg * 0.65f,
                Gravity = prof.gravity,
                Restitution = prof.restitution,
                MaxBounces = prof.maxBounces,
                ApproxRadius = approxRadius,
                IsSolidOrMech = false,
                IsProtoss = false,
                SpawnedFromAirborne = comp.CurrentFlightAltitude01 > 0.02f,
                Kind = GiantSeverVfxKind.VoidRupture,
                RestDurationTicks = prof.restDurationTicks,
                FadeDurationTicks = prof.fadeDurationTicks,
                NextSmokeTick = 20
            };

            Mesh mesh = BuildCenteredMesh($"GCF_DebrisWhip_{whipDef.whipId}_{inst.SlotId}", worldVerts, uvs, tris, centroidXZ);
            inst.WhipPieces.Add(new SeveredSoftWhipPiece
            {
                SnapshotMesh = mesh,
                TexPath = whipDef.texPath,
                SilhouetteTexPath = null,
                LayerOffset = whipDef.layerOffset,
                SilhouetteLayerOffset = whipDef.layerOffset - 0.1f
            });

            AddActiveDebris(inst);
        }

        private static Mesh BuildCenteredMesh(string name, Vector2[] worldVertsXZ, Vector2[] uvs, int[] tris, Vector2 centroidXZ)
        {
            int count = worldVertsXZ.Length;
            var verts = new Vector3[count];
            var normals = new Vector3[count];
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                float lx = worldVertsXZ[i].x - centroidXZ.x;
                float lz = worldVertsXZ[i].y - centroidXZ.y;
                verts[i] = new Vector3(lx, 0f, lz);
                normals[i] = Vector3.up;
                if (lx < minX) minX = lx;
                if (lx > maxX) maxX = lx;
                if (lz < minZ) minZ = lz;
                if (lz > maxZ) maxZ = lz;
            }
            var mesh = new Mesh { name = name };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(
                new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f),
                new Vector3((maxX - minX) + 2f, 2f, (maxZ - minZ) + 2f));
            return mesh;
        }

        private static void AddActiveDebris(SeveredDebrisInstance inst)
        {
            if (ActiveDebris.Count >= MaxActiveDebris)
            {
                int victimIdx = 0;
                for (int i = 0; i < ActiveDebris.Count; i++)
                {
                    if (ActiveDebris[i].IsSettled)
                    {
                        victimIdx = i;
                        break;
                    }
                }
                ActiveDebris[victimIdx].DisposeGpuResources();
                ActiveDebris.RemoveAt(victimIdx);
            }
            ActiveDebris.Add(inst);
            TotalSpawnedCount++;
        }

        public static void TickMapDebris(Map map)
        {
            if (map == null || ActiveDebris.Count == 0)
            {
                return;
            }

            for (int i = ActiveDebris.Count - 1; i >= 0; i--)
            {
                SeveredDebrisInstance inst = ActiveDebris[i];
                if (inst.Map != map)
                {
                    continue;
                }

                inst.StepTick(out bool firstImpact, out bool bounceImpact);

                if (firstImpact)
                {
                    Vector3 impactPos = new Vector3(inst.GroundPosXZ.x, AltitudeLayer.MoteOverhead.AltitudeFor(), inst.GroundPosXZ.y);
                    float impactScale = Mathf.Clamp(0.55f + inst.ApproxRadius * 0.34f, 0.55f, 1.85f);
                    GiantVfxSystem.SpawnSeverImpactOnGround(map, impactPos, impactScale, inst.Kind, inst.IsProtoss);

                    if (!inst.IsSolidOrMech && inst.Kind == GiantSeverVfxKind.FleshSever && ThingDefOf.Filth_Blood != null)
                    {
                        IntVec3 cell = IntVec3.FromVector3(impactPos);
                        if (cell.InBounds(map) && cell.Walkable(map))
                        {
                            FilthMaker.TryMakeFilth(cell, map, ThingDefOf.Filth_Blood, count: 1);
                        }
                    }
                }
                else if (bounceImpact)
                {
                    Vector3 bouncePos = new Vector3(inst.GroundPosXZ.x, AltitudeLayer.MoteOverhead.AltitudeFor(), inst.GroundPosXZ.y);
                    float bounceScale = Mathf.Clamp(0.35f + inst.ApproxRadius * 0.18f, 0.35f, 1.0f);
                    GiantVfxSystem.SpawnSeverImpactOnGround(map, bouncePos, bounceScale, inst.Kind, inst.IsProtoss, isSecondaryBounce: true);
                }

                // Subtle trailing smoke while falling or smoldering briefly for mechanoid/vehicle wreckage
                if (inst.Kind == GiantSeverVfxKind.MechExplosion && !inst.IsProtoss && inst.ElapsedTicks >= inst.NextSmokeTick)
                {
                    bool stillSmoking = !inst.IsSettled || (inst.ElapsedTicks - inst.SettledTick) < 120;
                    if (stillSmoking)
                    {
                        inst.NextSmokeTick = inst.ElapsedTicks + (inst.IsSettled ? 30 : 12);
                        Vector2 visXZ = inst.ComputeVisualWorldXZ();
                        Vector3 smokePos = new Vector3(visXZ.x, AltitudeLayer.MoteOverhead.AltitudeFor(), visXZ.y);
                        GiantVfxSystem.SpawnDebrisSmokeWisp(map, smokePos, Mathf.Clamp(0.45f + inst.ApproxRadius * 0.25f, 0.45f, 1.25f));
                    }
                }

                if (inst.IsExpired)
                {
                    inst.DisposeGpuResources();
                    ActiveDebris.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Smooth, C1-continuous ground shadow alpha as a function of debris height and fade alpha.
        /// Eliminates any visual pop at touchdown while smoothly darkening when airborne and fading cleanly.
        /// </summary>
        public static float ComputeDebrisShadowAlpha(float height, float fadeAlpha = 1f)
        {
            float h = Mathf.Max(0f, height);
            float hNorm = Mathf.Clamp01(h / 2.8f);
            float baseAlpha = Mathf.SmoothStep(0.36f, 0.62f, Mathf.Clamp01(h / 0.12f));
            return Mathf.Lerp(baseAlpha, 0.28f, hNorm) * Mathf.Clamp01(fadeAlpha);
        }

        public static void DrawMapDebris(Map map)
        {
            if (map == null || !UnityData.IsInMainThread || ActiveDebris.Count == 0
                || WorldRendererUtility.WorldRendered || Find.CurrentMap != map)
            {
                return;
            }

            CellRect viewRect = Find.CameraDriver != null
                ? Find.CameraDriver.CurrentViewRect.ExpandedBy(10)
                : CellRect.WholeMap(map);

            sharedBodyMpb ??= new MaterialPropertyBlock();
            sharedShadowMpb ??= new MaterialPropertyBlock();

            Shader shadowShader = GCF_VfxDefOf.GCF_GiantDropShadow?.Shader;
            float shadowBaseY = AltitudeLayer.Shadows.AltitudeFor() + 0.014f;
            float groundedBaseY = AltitudeLayer.Item.AltitudeFor() - 0.06f;
            float airborneBaseY = CompGiantRig.GroundAliveBaseAltitudeY + 0.16f;
            float skyfallerBaseY = CompGiantRig.HighAirborneBaseAltitudeY + 0.14f;

            for (int i = 0; i < ActiveDebris.Count; i++)
            {
                SeveredDebrisInstance inst = ActiveDebris[i];
                if (inst.Map != map || inst.IsExpired)
                {
                    continue;
                }

                IntVec3 cellCheck = new IntVec3((int)inst.GroundPosXZ.x, 0, (int)inst.GroundPosXZ.y);
                if (!viewRect.Contains(cellCheck))
                {
                    continue;
                }

                float fadeAlpha = inst.ComputeFadeAlpha();
                if (fadeAlpha <= 0.005f)
                {
                    continue;
                }

                bool useFadeShader = fadeAlpha < 0.992f;
                Color bodyTint = inst.ComputeBodyTint(fadeAlpha);
                Color silTint = new Color(1f, 1f, 1f, fadeAlpha);

                float h = Mathf.Max(0f, inst.Height);
                float hNorm = Mathf.Clamp01(h / 2.8f);
                float visScale = inst.ComputeVisualScale();
                Vector2 visXZ = inst.ComputeVisualWorldXZ();
                Quaternion rot = Quaternion.AngleAxis(inst.AngleDeg, Vector3.up);
                Vector3 scaleVec = new Vector3(visScale, 1f, visScale);

                float slotBias = (inst.SlotId & 31) * 0.00045f;
                float pieceBaseY;
                if (h > 0.035f)
                {
                    pieceBaseY = inst.SpawnedFromAirborne
                        ? Mathf.Lerp(airborneBaseY, skyfallerBaseY, Mathf.Clamp01((h - 0.55f) / 2.2f)) + slotBias
                        : airborneBaseY + slotBias;
                }
                else
                {
                    pieceBaseY = groundedBaseY + slotBias;
                }

                // 1. Silhouette-accurate 2.5D ground drop shadow (smooth height ramp, skips near-transparent draws)
                float shAlpha = ComputeDebrisShadowAlpha(h, fadeAlpha);
                if (shadowShader != null && shAlpha >= 0.02f)
                {
                    float shScale = Mathf.Lerp(1.0f, 0.78f, hNorm);
                    Vector2 shXZ = new Vector2(
                        inst.GroundPosXZ.x + 0.09f + 0.38f * h,
                        inst.GroundPosXZ.y - 0.09f - 0.44f * h);
                    float shY = shadowBaseY + (inst.SlotId & 31) * 0.00012f;
                    Matrix4x4 shClusterMat = Matrix4x4.TRS(new Vector3(shXZ.x, shY, shXZ.y), rot, new Vector3(shScale, 1f, shScale));

                    sharedShadowMpb.Clear();
                    sharedShadowMpb.SetColor(ShaderPropertyIDs.Color, new Color(0f, 0f, 0f, shAlpha));

                    for (int s = 0; s < inst.SkinnedPieces.Count; s++)
                    {
                        SeveredSkinnedMeshPiece sp = inst.SkinnedPieces[s];
                        if (sp.SnapshotMesh == null) continue;
                        if (sp.SeveredBundle != null && sp.SeveredBundle.IsDisposed) continue;
                        bool bundleAlive = sp.SeveredBundle != null;
                        if (bundleAlive)
                        {
                            sp.SeveredBundle.LastUsedFrame = Time.frameCount;
                        }
                        Material shMat = bundleAlive
                            ? GiantSkinnedSeveredTextureCache.ResolveShadowMaterial(sp.SeveredBundle, shadowShader)
                            : GiantMaterialCache.Resolve(sp.FallbackTexPath, Color.white, shadowShader);
                        if (shMat != null)
                        {
                            Graphics.DrawMesh(sp.SnapshotMesh, shClusterMat, shMat, 0, null, 0, sharedShadowMpb);
                        }
                    }
                    for (int b = 0; b < inst.BonePieces.Count; b++)
                    {
                        SeveredBoneSpritePiece bp = inst.BonePieces[b];
                        Material shMat = GiantMaterialCache.Resolve(bp.TexPath, Color.white, shadowShader);
                        if (shMat != null)
                        {
                            Graphics.DrawMesh(MeshPool.plane10, shClusterMat * bp.LocalQuadMatrix, shMat, 0, null, 0, sharedShadowMpb);
                        }
                    }
                    for (int w = 0; w < inst.WhipPieces.Count; w++)
                    {
                        SeveredSoftWhipPiece wp = inst.WhipPieces[w];
                        if (wp.SnapshotMesh == null) continue;
                        Material shMat = GiantMaterialCache.Resolve(wp.TexPath, Color.white, shadowShader);
                        if (shMat != null)
                        {
                            Graphics.DrawMesh(wp.SnapshotMesh, shClusterMat, shMat, 0, null, 0, sharedShadowMpb);
                        }
                    }
                }

                // 2. Sort all silhouette & color sub-layers within this cluster by original effective layerOffset
                //    Skip independent solid-black _Sil underlays while fading so they never bleed dark smudges through semi-transparent sprites.
                DrawOrderScratch.Clear();
                for (int s = 0; s < inst.SkinnedPieces.Count; s++)
                {
                    SeveredSkinnedMeshPiece sp = inst.SkinnedPieces[s];
                    if (sp.SnapshotMesh == null) continue;
                    if (sp.SeveredBundle != null && sp.SeveredBundle.IsDisposed) continue;
                    bool bundleAlive = sp.SeveredBundle != null;
                    if (bundleAlive)
                    {
                        sp.SeveredBundle.LastUsedFrame = Time.frameCount;
                    }
                    if (!useFadeShader && ((bundleAlive && sp.SeveredBundle.SilMaterial != null) || (!bundleAlive && !string.IsNullOrEmpty(sp.FallbackSilTexPath))))
                    {
                        DrawOrderScratch.Add(new DrawLayerEntry(sp.SilhouetteLayerOffset, isSilhouette: true, pieceKind: 1, pieceIndex: s));
                    }
                    DrawOrderScratch.Add(new DrawLayerEntry(sp.LayerOffset, isSilhouette: false, pieceKind: 1, pieceIndex: s));
                }
                for (int b = 0; b < inst.BonePieces.Count; b++)
                {
                    SeveredBoneSpritePiece bp = inst.BonePieces[b];
                    if (!useFadeShader && !string.IsNullOrEmpty(bp.SilhouetteTexPath))
                    {
                        DrawOrderScratch.Add(new DrawLayerEntry(bp.SilhouetteLayerOffset, isSilhouette: true, pieceKind: 0, pieceIndex: b));
                    }
                    DrawOrderScratch.Add(new DrawLayerEntry(bp.LayerOffset, isSilhouette: false, pieceKind: 0, pieceIndex: b));
                }
                for (int w = 0; w < inst.WhipPieces.Count; w++)
                {
                    SeveredSoftWhipPiece wp = inst.WhipPieces[w];
                    if (wp.SnapshotMesh == null) continue;
                    DrawOrderScratch.Add(new DrawLayerEntry(wp.LayerOffset, isSilhouette: false, pieceKind: 2, pieceIndex: w));
                }

                DrawOrderScratch.Sort();
                int drawCount = DrawOrderScratch.Count;
                float layerStep = drawCount > 1 ? (0.012f / (drawCount - 1)) : 0f;

                for (int d = 0; d < drawCount; d++)
                {
                    DrawLayerEntry ent = DrawOrderScratch[d];
                    float y = pieceBaseY + d * layerStep;
                    Matrix4x4 clusterMat = Matrix4x4.TRS(new Vector3(visXZ.x, y, visXZ.y), rot, scaleVec);

                    sharedBodyMpb.Clear();
                    sharedBodyMpb.SetColor(ShaderPropertyIDs.Color, ent.IsSilhouette ? silTint : bodyTint);

                    if (ent.PieceKind == 1)
                    {
                        SeveredSkinnedMeshPiece sp = inst.SkinnedPieces[ent.PieceIndex];
                        if (sp.SeveredBundle != null && sp.SeveredBundle.IsDisposed)
                        {
                            continue;
                        }
                        bool bundleAlive = sp.SeveredBundle != null;
                        Material mat = null;
                        if (ent.IsSilhouette)
                        {
                            if (bundleAlive)
                            {
                                mat = useFadeShader
                                    ? GiantSkinnedSeveredTextureCache.ResolveFadeSilMaterial(sp.SeveredBundle)
                                    : sp.SeveredBundle.SilMaterial;
                            }
                            else if (!string.IsNullOrEmpty(sp.FallbackSilTexPath))
                            {
                                mat = GiantMaterialCache.Resolve(sp.FallbackSilTexPath, Color.white, useFadeShader ? ShaderDatabase.Transparent : ShaderDatabase.Cutout);
                            }
                        }
                        else
                        {
                            if (bundleAlive)
                            {
                                mat = useFadeShader
                                    ? GiantSkinnedSeveredTextureCache.ResolveFadeColorMaterial(sp.SeveredBundle)
                                    : sp.SeveredBundle.ColorMaterial;
                            }
                            else
                            {
                                Shader sh = useFadeShader ? ShaderDatabase.Transparent : (sp.MainShader ?? ShaderDatabase.Cutout);
                                mat = GiantMaterialCache.Resolve(sp.FallbackTexPath, Color.white, sh);
                            }
                        }

                        if (mat != null)
                        {
                            Graphics.DrawMesh(sp.SnapshotMesh, clusterMat, mat, 0, null, 0, sharedBodyMpb);
                        }
                    }
                    else if (ent.PieceKind == 0)
                    {
                        SeveredBoneSpritePiece bp = inst.BonePieces[ent.PieceIndex];
                        string path = ent.IsSilhouette ? bp.SilhouetteTexPath : bp.TexPath;
                        Shader sh = useFadeShader ? ShaderDatabase.Transparent : ShaderDatabase.Cutout;
                        Material mat = GiantMaterialCache.Resolve(path, Color.white, sh);
                        if (mat != null)
                        {
                            Graphics.DrawMesh(MeshPool.plane10, clusterMat * bp.LocalQuadMatrix, mat, 0, null, 0, sharedBodyMpb);
                        }
                    }
                    else
                    {
                        SeveredSoftWhipPiece wp = inst.WhipPieces[ent.PieceIndex];
                        Shader sh = useFadeShader ? ShaderDatabase.Transparent : ShaderDatabase.Cutout;
                        Material mat = GiantMaterialCache.Resolve(wp.TexPath, Color.white, sh);
                        if (mat != null)
                        {
                            Graphics.DrawMesh(wp.SnapshotMesh, clusterMat, mat, 0, null, 0, sharedBodyMpb);
                        }
                    }
                }
            }
        }
    }
}
