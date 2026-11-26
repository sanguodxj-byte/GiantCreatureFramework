using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Main-thread cache of dynamically masked Texture2D/Material pairs for partially severed multi-bone
    /// skinned meshes. Intact skinned meshes incur zero memory or CPU cost (they use GiantMaterialCache directly).
    /// </summary>
    public static class GiantSkinnedSeveredTextureCache
    {
        private readonly record struct SourceKey(string RigDefName, string MeshId);
        private readonly record struct VariantKey(string RigDefName, string MeshId, ulong VisibleMemberMask, string ShaderName);

        private sealed class SourceAtlasData
        {
            public Color32[] ColorPixels;
            public Color32[] GlowPixels;
            public Color32[] MaskPixels;
            public int Width;
            public int Height;
            public float OutlineRadiusPx;
            public Color32 SilhouetteColor;
            public bool LoadFailed;
        }

        public sealed class SeveredMaterialBundle
        {
            public Texture2D ColorTexture;
            public Texture2D SilTexture;
            public Texture2D GlowTexture;
            public Texture2D MaskTexture;
            public Material ColorMaterial;
            public Material SilMaterial;
            public Material GlowMaterial;
            public Material InvisibleColorMaterial;
            public Material InvisibleSilMaterial;
            public Material InvisibleGlowMaterial;
            public Material ShadowMaterial;
            public Shader ShadowShader;
            public Material FadeColorMaterial;
            public Material FadeSilMaterial;
            public int LastUsedFrame;
            public bool IsDisposed;
        }

        private const int MaxCachedVariants = 128;
        private static readonly Dictionary<SourceKey, SourceAtlasData> Sources = new();
        private static readonly Dictionary<VariantKey, SeveredMaterialBundle> Variants = new();

        public static void InvalidateAll()
        {
            foreach (SeveredMaterialBundle bundle in Variants.Values)
            {
                DestroyBundle(bundle);
            }
            Variants.Clear();
            Sources.Clear();
        }

        public static SeveredMaterialBundle ResolveOrBuildOnMainThread(
            SkinnedMeshRuntime rt,
            ReadOnlySpan<TextureAlphaMask> rigBoneColorMasks,
            ulong visibleMemberMask,
            Shader mainShader)
        {
            if (!UnityData.IsInMainThread || rt == null || rt.MeshDef == null || rt.RigDef == null || visibleMemberMask == 0UL)
            {
                return null;
            }

            GiantSkinnedMeshDef sDef = rt.MeshDef;
            string rigName = rt.RigDef.defName ?? string.Empty;
            string meshId = sDef.meshId ?? sDef.texPath ?? string.Empty;
            Shader resolvedMain = mainShader != null ? mainShader : (sDef.shaderTypeDef?.Shader ?? ShaderDatabase.Cutout);
            string shaderName = resolvedMain != null ? resolvedMain.name : "Cutout";

            var vKey = new VariantKey(rigName, meshId, visibleMemberMask, shaderName);
            if (Variants.TryGetValue(vKey, out SeveredMaterialBundle existing))
            {
                existing.LastUsedFrame = Time.frameCount;
                return existing;
            }

            SourceAtlasData src = GetOrLoadSource(rigName, meshId, sDef);
            if (src == null || src.LoadFailed || src.ColorPixels == null)
            {
                return null;
            }

            if (!SkinnedMeshRuntime.BuildSeveredLayerPixels(
                rt.RigDef,
                sDef,
                rt.FlatBindPoses,
                rigBoneColorMasks,
                visibleMemberMask,
                src.ColorPixels,
                src.GlowPixels,
                src.Width,
                src.Height,
                src.OutlineRadiusPx,
                src.SilhouetteColor,
                out Color32[] outColorPixels,
                out Color32[] outSilPixels,
                out Color32[] outGlowPixels))
            {
                return null;
            }

            EvictIfOverLimit();

            Texture2D colTex = CreateDynamicSpriteTexture($"GCF_SevCol_{meshId}_{visibleMemberMask:X}", src.Width, src.Height, outColorPixels);
            Material colMat = CreateDynamicMaterial(colTex, resolvedMain, Color.white);
            Material invColMat = CreateLocalInvisibleMaterial(colMat);

            Texture2D maskTex = null;
            if (src.MaskPixels != null && src.MaskPixels.Length == outColorPixels.Length && colMat != null)
            {
                var outMaskPixels = new Color32[src.MaskPixels.Length];
                for (int p = 0; p < outMaskPixels.Length; p++)
                {
                    if (outColorPixels[p].a >= 12)
                    {
                        Color32 mp = src.MaskPixels[p];
                        outMaskPixels[p] = new Color32(mp.r, mp.g, mp.b, 255);
                    }
                }
                maskTex = CreateDynamicSpriteTexture($"GCF_SevMask_{meshId}_{visibleMemberMask:X}", src.Width, src.Height, outMaskPixels);
                colMat.SetTexture(GiantShaderPropertyIDs.MaskTex, maskTex);
            }

            Texture2D silTex = null;
            Material silMat = null;
            Material invSilMat = null;
            if (!string.IsNullOrEmpty(sDef.silhouetteTexPath) && outSilPixels != null)
            {
                silTex = CreateDynamicSpriteTexture($"GCF_SevSil_{meshId}_{visibleMemberMask:X}", src.Width, src.Height, outSilPixels);
                silMat = CreateDynamicMaterial(silTex, ShaderDatabase.Cutout, Color.white);
                invSilMat = CreateLocalInvisibleMaterial(silMat);
            }

            Texture2D glowTex = null;
            Material glowMat = null;
            Material invGlowMat = null;
            if (!string.IsNullOrEmpty(sDef.glowTexPath) && outGlowPixels != null)
            {
                glowTex = CreateDynamicSpriteTexture($"GCF_SevGlow_{meshId}_{visibleMemberMask:X}", src.Width, src.Height, outGlowPixels);
                glowMat = CreateDynamicMaterial(glowTex, ShaderDatabase.TransparentPostLight, sDef.glowColor);
                invGlowMat = CreateLocalInvisibleMaterial(glowMat);
            }

            var bundle = new SeveredMaterialBundle
            {
                ColorTexture = colTex,
                SilTexture = silTex,
                GlowTexture = glowTex,
                MaskTexture = maskTex,
                ColorMaterial = colMat,
                SilMaterial = silMat,
                GlowMaterial = glowMat,
                InvisibleColorMaterial = invColMat,
                InvisibleSilMaterial = invSilMat,
                InvisibleGlowMaterial = invGlowMat,
                LastUsedFrame = Time.frameCount
            };
            Variants[vKey] = bundle;
            return bundle;
        }

        public static Material ResolveShadowMaterial(SeveredMaterialBundle bundle, Shader shadowShader)
        {
            if (bundle == null || bundle.IsDisposed || bundle.ColorTexture == null || shadowShader == null || !UnityData.IsInMainThread)
            {
                return null;
            }
            bundle.LastUsedFrame = Time.frameCount;
            if (bundle.ShadowMaterial != null && ReferenceEquals(bundle.ShadowShader, shadowShader))
            {
                return bundle.ShadowMaterial;
            }
            bundle.ShadowShader = shadowShader;
            if (bundle.ShadowMaterial != null)
            {
                UnityEngine.Object.Destroy(bundle.ShadowMaterial);
            }
            bundle.ShadowMaterial = CreateDynamicMaterial(bundle.ColorTexture, shadowShader, Color.white);
            return bundle.ShadowMaterial;
        }

        public static Material ResolveFadeColorMaterial(SeveredMaterialBundle bundle)
        {
            if (bundle == null || bundle.IsDisposed || bundle.ColorTexture == null || !UnityData.IsInMainThread)
            {
                return null;
            }
            bundle.LastUsedFrame = Time.frameCount;
            if (bundle.FadeColorMaterial == null)
            {
                bundle.FadeColorMaterial = CreateDynamicMaterial(bundle.ColorTexture, ShaderDatabase.Transparent, Color.white);
            }
            return bundle.FadeColorMaterial;
        }

        public static Material ResolveFadeSilMaterial(SeveredMaterialBundle bundle)
        {
            if (bundle == null || bundle.IsDisposed || bundle.SilTexture == null || !UnityData.IsInMainThread)
            {
                return null;
            }
            bundle.LastUsedFrame = Time.frameCount;
            if (bundle.FadeSilMaterial == null)
            {
                bundle.FadeSilMaterial = CreateDynamicMaterial(bundle.SilTexture, ShaderDatabase.Transparent, Color.white);
            }
            return bundle.FadeSilMaterial;
        }

        private static SourceAtlasData GetOrLoadSource(string rigName, string meshId, GiantSkinnedMeshDef sDef)
        {
            var sKey = new SourceKey(rigName, meshId);
            if (Sources.TryGetValue(sKey, out SourceAtlasData cached))
            {
                return cached;
            }

            var data = new SourceAtlasData();
            if (!GiantAlphaMaskCache.TryLoadRgbaPixels(sDef.texPath, out Color32[] colPx, out int w, out int h))
            {
                data.LoadFailed = true;
                Sources[sKey] = data;
                return data;
            }

            Color32[] silPx = null;
            if (!string.IsNullOrEmpty(sDef.silhouetteTexPath)
                && GiantAlphaMaskCache.TryLoadRgbaPixels(sDef.silhouetteTexPath, out Color32[] rawSil, out int sw, out int sh)
                && sw == w && sh == h)
            {
                silPx = rawSil;
            }

            Color32[] glowPx = null;
            if (!string.IsNullOrEmpty(sDef.glowTexPath)
                && GiantAlphaMaskCache.TryLoadRgbaPixels(sDef.glowTexPath, out Color32[] rawGlow, out int gw, out int gh)
                && gw == w && gh == h)
            {
                glowPx = rawGlow;
            }

            Color32[] maskPx = null;
            if (!string.IsNullOrEmpty(sDef.texPath)
                && GiantAlphaMaskCache.TryLoadRgbaPixels(sDef.texPath + "m", out Color32[] rawMask, out int mw, out int mh)
                && mw == w && mh == h)
            {
                maskPx = rawMask;
            }

            data.ColorPixels = colPx;
            data.GlowPixels = glowPx;
            data.MaskPixels = maskPx;
            data.Width = w;
            data.Height = h;
            data.OutlineRadiusPx = SkinnedMeshRuntime.CalibrateOutlineRadiusPx(colPx, silPx, w, h, sDef, out Color32 silCol);
            data.SilhouetteColor = silCol;
            Sources[sKey] = data;
            return data;
        }

        private static Material CreateDynamicMaterial(Texture2D tex, Shader shader, Color color)
        {
            if (tex == null || shader == null)
            {
                return null;
            }
            return new Material(shader)
            {
                name = $"{shader.name}_{tex.name}",
                mainTexture = tex,
                color = color
            };
        }

        private static Material CreateLocalInvisibleMaterial(Material baseMat)
        {
            if (baseMat == null || ShaderDatabase.Invisible == null)
            {
                return null;
            }
            // Local disposable clone instead of Verse.InvisibilityMatPool (which has zero eviction and leaks destroyed keys forever).
            var inv = new Material(baseMat)
            {
                name = $"GCF_Inv_{baseMat.name}",
                shader = ShaderDatabase.Invisible,
                color = new Color(0.75f, 0.93f, 0.98f, 0.5f)
            };
            inv.SetTexture(ShaderPropertyIDs.NoiseTex, TexGame.InvisDistortion);
            return inv;
        }

        private static Texture2D CreateDynamicSpriteTexture(string name, int width, int height, Color32[] pixels)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return tex;
        }

        private static void EvictIfOverLimit()
        {
            if (Variants.Count < MaxCachedVariants)
            {
                return;
            }
            VariantKey oldestKey = default;
            int oldestFrame = int.MaxValue;
            bool found = false;
            foreach (KeyValuePair<VariantKey, SeveredMaterialBundle> kv in Variants)
            {
                if (kv.Value.LastUsedFrame < oldestFrame)
                {
                    oldestFrame = kv.Value.LastUsedFrame;
                    oldestKey = kv.Key;
                    found = true;
                }
            }
            if (found && Variants.TryGetValue(oldestKey, out SeveredMaterialBundle victim))
            {
                Variants.Remove(oldestKey);
                DestroyBundle(victim);
            }
        }

        private static void DestroyBundle(SeveredMaterialBundle bundle)
        {
            if (bundle == null || bundle.IsDisposed) return;
            bundle.IsDisposed = true;
            if (bundle.ColorMaterial != null) UnityEngine.Object.Destroy(bundle.ColorMaterial);
            if (bundle.SilMaterial != null) UnityEngine.Object.Destroy(bundle.SilMaterial);
            if (bundle.GlowMaterial != null) UnityEngine.Object.Destroy(bundle.GlowMaterial);
            if (bundle.InvisibleColorMaterial != null) UnityEngine.Object.Destroy(bundle.InvisibleColorMaterial);
            if (bundle.InvisibleSilMaterial != null) UnityEngine.Object.Destroy(bundle.InvisibleSilMaterial);
            if (bundle.InvisibleGlowMaterial != null) UnityEngine.Object.Destroy(bundle.InvisibleGlowMaterial);
            if (bundle.ShadowMaterial != null) UnityEngine.Object.Destroy(bundle.ShadowMaterial);
            if (bundle.FadeColorMaterial != null) UnityEngine.Object.Destroy(bundle.FadeColorMaterial);
            if (bundle.FadeSilMaterial != null) UnityEngine.Object.Destroy(bundle.FadeSilMaterial);
            if (bundle.ColorTexture != null) UnityEngine.Object.Destroy(bundle.ColorTexture);
            if (bundle.SilTexture != null) UnityEngine.Object.Destroy(bundle.SilTexture);
            if (bundle.GlowTexture != null) UnityEngine.Object.Destroy(bundle.GlowTexture);
            if (bundle.MaskTexture != null) UnityEngine.Object.Destroy(bundle.MaskTexture);
            bundle.ColorMaterial = null;
            bundle.SilMaterial = null;
            bundle.GlowMaterial = null;
            bundle.InvisibleColorMaterial = null;
            bundle.InvisibleSilMaterial = null;
            bundle.InvisibleGlowMaterial = null;
            bundle.ShadowMaterial = null;
            bundle.FadeColorMaterial = null;
            bundle.FadeSilMaterial = null;
            bundle.ColorTexture = null;
            bundle.SilTexture = null;
            bundle.GlowTexture = null;
            bundle.MaskTexture = null;
        }
    }

    public class PawnRenderNodeProperties_GiantSkinnedMesh : PawnRenderNodeProperties
    {
        public GiantSkinnedMeshDef skinnedMeshDef;
        public SkinnedMeshRuntime runtime;
        public CompGiantRig rigComp;
        public bool isSilhouetteUnderlay;
        public bool isGlowOverlay;

        public PawnRenderNodeProperties_GiantSkinnedMesh()
        {
            nodeClass = typeof(PawnRenderNode_GiantSkinnedMesh);
            workerClass = typeof(PawnRenderNodeWorker_GiantSkinnedMesh);
            rotDrawMode = RotDrawMode.Fresh | RotDrawMode.Rotting | RotDrawMode.Dessicated;
        }
    }

    /// <summary>
    /// Renders a multi-bone 2D Linear Blend Skinned (LBS) mesh inside RimWorld's PawnRenderTree.
    /// When main sprite, silhouette underlay, and glow overlay nodes share the same SkinnedMeshRuntime,
    /// the primary node uploads the deformed UnityEngine.Mesh once per frame on the main thread and
    /// auxiliary overlay/underlay nodes reuse that exact GPU mesh with zero duplicate deformation cost.
    /// </summary>
    public class PawnRenderNode_GiantSkinnedMesh : PawnRenderNode
    {
        private const int DynamicMeshRingSize = 4;
        private readonly Mesh[] dynamicMeshRing = new Mesh[DynamicMeshRingSize];
        private readonly bool[] dynamicMeshSlotInitialized = new bool[DynamicMeshRingSize];
        private readonly ulong[] dynamicMeshSlotTopoVersion = new ulong[DynamicMeshRingSize];
        private readonly int[] dynamicMeshSlotTriCount = new int[DynamicMeshRingSize];
        private int activeDynamicMeshSlot;
        private ulong currentTriangleTopoVersion = 1UL;
        private Mesh portraitBindMesh;
        private int[] triUploadScratch;
        private int lastUploadedFrame = -1;
        private bool hasValidMesh;
        private bool hasValidPortraitMesh;
        private float lastBakedHeadingDeg;
        private GiantSkinnedSeveredTextureCache.SeveredMaterialBundle activeSeveredBundle;
        private ulong lastResolvedSeveredMemberMask = ulong.MaxValue;
        private ulong lastAttemptedSeveredMemberMask = ulong.MaxValue;

        public PawnRenderNode_GiantSkinnedMesh PrimaryMeshOwner;
        public PawnRenderNode_GiantSkinnedMesh SilhouetteChildNode;
        public PawnRenderNode_GiantSkinnedMesh GlowChildNode;

        public void ReleaseDynamicMeshOnMainThread()
        {
            for (int i = 0; i < DynamicMeshRingSize; i++)
            {
                if (dynamicMeshRing[i] != null)
                {
                    if (UnityData.IsInMainThread)
                    {
                        UnityEngine.Object.Destroy(dynamicMeshRing[i]);
                    }
                    dynamicMeshRing[i] = null;
                }
                dynamicMeshSlotInitialized[i] = false;
                dynamicMeshSlotTopoVersion[i] = 0UL;
                dynamicMeshSlotTriCount[i] = 0;
            }
            activeDynamicMeshSlot = 0;
            if (portraitBindMesh != null)
            {
                if (UnityData.IsInMainThread)
                {
                    UnityEngine.Object.Destroy(portraitBindMesh);
                }
                portraitBindMesh = null;
            }
            hasValidMesh = false;
            hasValidPortraitMesh = false;
            lastUploadedFrame = -1;
            activeSeveredBundle = null;
            lastResolvedSeveredMemberMask = ulong.MaxValue;
            lastAttemptedSeveredMemberMask = ulong.MaxValue;
        }

        public GiantSkinnedMeshDef SkinnedMeshDef => (props as PawnRenderNodeProperties_GiantSkinnedMesh)?.skinnedMeshDef;
        public SkinnedMeshRuntime Runtime => (props as PawnRenderNodeProperties_GiantSkinnedMesh)?.runtime;
        public CompGiantRig RigComp => (props as PawnRenderNodeProperties_GiantSkinnedMesh)?.rigComp;
        public bool IsSilhouetteUnderlay => (props as PawnRenderNodeProperties_GiantSkinnedMesh)?.isSilhouetteUnderlay ?? false;
        public bool IsGlowOverlay => (props as PawnRenderNodeProperties_GiantSkinnedMesh)?.isGlowOverlay ?? false;

        public Mesh ActiveDynamicMesh => PrimaryMeshOwner != null ? PrimaryMeshOwner.ActiveDynamicMesh : dynamicMeshRing[activeDynamicMeshSlot];
        public Mesh ActivePortraitMesh => PrimaryMeshOwner != null ? PrimaryMeshOwner.portraitBindMesh : portraitBindMesh;
        public bool HasValidMesh => PrimaryMeshOwner != null
            ? PrimaryMeshOwner.HasValidMesh
            : (hasValidMesh && dynamicMeshRing[activeDynamicMeshSlot] != null);
        public bool HasValidPortraitMesh => PrimaryMeshOwner != null
            ? PrimaryMeshOwner.HasValidPortraitMesh
            : (hasValidPortraitMesh && portraitBindMesh != null);
        public float LastBakedHeadingDeg => PrimaryMeshOwner != null ? PrimaryMeshOwner.lastBakedHeadingDeg : lastBakedHeadingDeg;
        public GiantSkinnedSeveredTextureCache.SeveredMaterialBundle ActiveSeveredBundle =>
            PrimaryMeshOwner != null ? PrimaryMeshOwner.activeSeveredBundle : activeSeveredBundle;

        public Material ResolveActiveShadowMaterial(Shader shadowShader)
        {
            GiantSkinnedSeveredTextureCache.SeveredMaterialBundle bundle = ActiveSeveredBundle;
            return bundle != null
                ? GiantSkinnedSeveredTextureCache.ResolveShadowMaterial(bundle, shadowShader)
                : null;
        }

        public PawnRenderNode_GiantSkinnedMesh(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree)
        {
        }

        public override GraphicMeshSet MeshSetFor(Pawn pawn)
        {
            return UnityData.IsInMainThread ? MeshPool.GetMeshSetForSize(1f, 1f) : null;
        }

        public override Mesh GetMesh(PawnDrawParms parms)
        {
            if (parms.Portrait && HasValidPortraitMesh)
            {
                Mesh pm = ActivePortraitMesh;
                if (pm != null)
                {
                    return pm;
                }
            }
            Mesh m = ActiveDynamicMesh;
            return HasValidMesh && m != null ? m : MeshPool.plane10;
        }

        public override bool FlipGraphic(PawnDrawParms parms)
        {
            return false;
        }

        public string ResolveActiveTexPath()
        {
            GiantSkinnedMeshDef sDef = SkinnedMeshDef;
            if (sDef == null)
            {
                return null;
            }
            if (IsSilhouetteUnderlay)
            {
                return sDef.silhouetteTexPath;
            }
            if (IsGlowOverlay)
            {
                return sDef.glowTexPath;
            }
            return sDef.texPath;
        }

        public override Graphic GraphicFor(Pawn pawn)
        {
            GiantSkinnedMeshDef sDef = SkinnedMeshDef;
            string activeTex = ResolveActiveTexPath();
            if (sDef == null || string.IsNullOrEmpty(activeTex))
            {
                return null;
            }

            if (UnityData.IsInMainThread)
            {
                Texture2D rawTex = ContentFinder<Texture2D>.Get(activeTex, reportFailure: false);
                if (rawTex != null && rawTex.wrapMode != TextureWrapMode.Clamp)
                {
                    rawTex.wrapMode = TextureWrapMode.Clamp;
                }
            }

            if (IsGlowOverlay)
            {
                GiantMaterialCache.WarmOnMainThread(activeTex, sDef.glowColor, ShaderDatabase.TransparentPostLight);
                Graphic glowG = GraphicDatabase.Get<Graphic_Single>(
                    activeTex,
                    ShaderDatabase.TransparentPostLight,
                    Vector2.one,
                    sDef.glowColor);
                GiantMaterialCache.WarmInvisibleMaterialOnMainThread(glowG?.MatSingle);
                return glowG;
            }

            if (IsSilhouetteUnderlay)
            {
                GiantMaterialCache.WarmOnMainThread(activeTex, Color.white, ShaderDatabase.Cutout);
                Graphic silG = GraphicDatabase.Get<Graphic_Single>(
                    activeTex,
                    ShaderDatabase.Cutout,
                    Vector2.one,
                    Color.white);
                GiantMaterialCache.WarmInvisibleMaterialOnMainThread(silG?.MatSingle);
                return silG;
            }

            Color col = ColorFor(pawn);
            Shader shader = sDef.shaderTypeDef != null ? sDef.shaderTypeDef.Shader : ShaderFor(pawn);
            if (shader == null)
            {
                shader = ShaderDatabase.Cutout;
            }
            GiantMaterialCache.WarmOnMainThread(activeTex, col, shader);
            Graphic g = GraphicDatabase.Get<Graphic_Single>(activeTex, shader, Vector2.one, col);
            GiantMaterialCache.TryBindMaskTexture(g?.MatSingle, activeTex);
            GiantMaterialCache.WarmInvisibleMaterialOnMainThread(g?.MatSingle);
            return g;
        }

        /// <summary>
        /// Evaluates LBS vertex deformation and uploads to UnityEngine.Mesh strictly on the Unity main thread.
        /// </summary>
        public void RebuildMeshOnMainThread(
            ReadOnlySpan<BoneWorldPose> solvedWorldPoses,
            Vector2 rootWorldXZ,
            bool meshPartMissing)
        {
            if (!UnityData.IsInMainThread || PrimaryMeshOwner != null)
            {
                return;
            }

            SkinnedMeshRuntime rt = Runtime;
            GiantSkinnedMeshDef sDef = SkinnedMeshDef;
            if (rt == null || sDef == null)
            {
                hasValidMesh = false;
                hasValidPortraitMesh = false;
                return;
            }

            if (!rt.IsBindInitialized)
            {
                RigComp?.EnsureAlphaMasksLoaded();
                string maskPath = !string.IsNullOrEmpty(sDef.silhouetteTexPath) ? sDef.silhouetteTexPath : sDef.texPath;
                TextureAlphaMask mask = !string.IsNullOrEmpty(maskPath)
                    ? GiantAlphaMaskCache.GetOrLoad(maskPath, sDef.alphaThreshold)
                    : null;
                ReadOnlySpan<TextureAlphaMask> rigMasks = RigComp != null ? RigComp.AlphaMasksSpan : default;
                ReadOnlySpan<TextureAlphaMask> rigSilMasks = RigComp != null ? RigComp.SilhouetteAlphaMasksSpan : default;
                rt.EnsureBindTopology(mask, rigMasks, rigSilMasks);
            }

            int frame = Time.frameCount;
            bool visChanged = rt.HasVisibilityChanged(solvedWorldPoses, meshPartMissing);
            bool needPortraitRebuild = visChanged || !hasValidPortraitMesh || portraitBindMesh == null;
            bool forceRebuild = !hasValidMesh || frame != lastUploadedFrame || visChanged || needPortraitRebuild;
            if (!forceRebuild && hasValidMesh)
            {
                return;
            }
            lastUploadedFrame = frame;
            lastBakedHeadingDeg = RigComp != null
                ? RigComp.CurrentHeadingDeg
                : (solvedWorldPoses.Length > 0 ? solvedWorldPoses[0].worldAngleDeg : 0f);

            bool bindTrisChanged = false;
            bool bindIdxChanged = false;
            if (needPortraitRebuild)
            {
                if (meshPartMissing)
                {
                    hasValidPortraitMesh = false;
                }
                else if (RigComp?.RigDef?.SortedNodes != null && RigComp.RigDef.SortedNodes.Length > 0)
                {
                    int nodeCount = RigComp.RigDef.SortedNodes.Length;
                    bool[] bindMissing = new bool[nodeCount];
                    for (int i = 0; i < nodeCount && i < solvedWorldPoses.Length; i++)
                    {
                        bindMissing[i] = !solvedWorldPoses[i].visible;
                    }
                    var bindPoses = new BoneWorldPose[nodeCount];
                    SkeletalKinematics.SolveWorldPoses(
                        RigComp.RigDef,
                        Vector2.zero,
                        0f,
                        ReadOnlySpan<BoneLocalPose>.Empty,
                        bindMissing,
                        bindPoses,
                        1f);
                    bindTrisChanged = rt.EvaluateDeformedVertices(bindPoses, Vector2.zero, meshPartMissing, out bindIdxChanged);
                    if (rt.IsCurrentlyVisible)
                    {
                        if (portraitBindMesh == null)
                        {
                            portraitBindMesh = new Mesh
                            {
                                name = $"GCF_PortraitSkinnedMesh_{sDef.meshId ?? "mesh"}"
                            };
                        }
                        portraitBindMesh.Clear();
                        portraitBindMesh.vertices = rt.DeformedVertices;
                        portraitBindMesh.uv = rt.UVs;
                        portraitBindMesh.normals = rt.Normals;
                        UploadActiveTrianglesTo(portraitBindMesh, rt);
                        portraitBindMesh.bounds = rt.CurrentBounds;
                        hasValidPortraitMesh = true;
                    }
                    else
                    {
                        hasValidPortraitMesh = false;
                    }
                }
            }

            bool liveTrisChanged = rt.EvaluateDeformedVertices(solvedWorldPoses, rootWorldXZ, meshPartMissing, out bool liveIdxChanged);
            bool trisChanged = liveTrisChanged || bindTrisChanged;
            bool indexChanged = liveIdxChanged || bindIdxChanged;
            if (!rt.IsCurrentlyVisible)
            {
                bool wasValid = hasValidMesh;
                hasValidMesh = false;
                hasValidPortraitMesh = false;
                if (activeSeveredBundle != null || lastAttemptedSeveredMemberMask != ulong.MaxValue || wasValid)
                {
                    activeSeveredBundle = null;
                    lastResolvedSeveredMemberMask = ulong.MaxValue;
                    lastAttemptedSeveredMemberMask = ulong.MaxValue;
                    MarkMaterialRecacheOnFamily();
                }
                return;
            }

            if (rt.HasPartialMemberSevering)
            {
                ulong visMask = rt.CurrentVisibleMemberMask;
                bool bundleInvalid = activeSeveredBundle != null && activeSeveredBundle.IsDisposed;
                if (bundleInvalid)
                {
                    activeSeveredBundle = null;
                    lastAttemptedSeveredMemberMask = ulong.MaxValue;
                }
                // Only attempt rebuild when visMask changed or a previously valid bundle was disposed/evicted;
                // prevents per-frame EDT & recache storm if a mask legitimately fails to build.
                if (visMask != lastAttemptedSeveredMemberMask)
                {
                    RigComp?.EnsureAlphaMasksLoaded();
                    Pawn ownerPawn = RigComp?.Pawn ?? tree?.pawn;
                    Shader mainShader = sDef.shaderTypeDef != null
                        ? sDef.shaderTypeDef.Shader
                        : (ownerPawn != null ? ShaderFor(ownerPawn) : ShaderDatabase.Cutout);
                    activeSeveredBundle = GiantSkinnedSeveredTextureCache.ResolveOrBuildOnMainThread(
                        rt,
                        RigComp != null ? RigComp.AlphaMasksSpan : default,
                        visMask,
                        mainShader);
                    lastAttemptedSeveredMemberMask = visMask;
                    lastResolvedSeveredMemberMask = visMask;
                    MarkMaterialRecacheOnFamily();
                }
                else if (activeSeveredBundle != null)
                {
                    activeSeveredBundle.LastUsedFrame = frame;
                }
            }
            else if (activeSeveredBundle != null || lastResolvedSeveredMemberMask != ulong.MaxValue || lastAttemptedSeveredMemberMask != ulong.MaxValue)
            {
                activeSeveredBundle = null;
                lastResolvedSeveredMemberMask = ulong.MaxValue;
                lastAttemptedSeveredMemberMask = ulong.MaxValue;
                MarkMaterialRecacheOnFamily();
            }

            if (trisChanged || indexChanged)
            {
                currentTriangleTopoVersion++;
            }

            int slot = (activeDynamicMeshSlot + 1) & (DynamicMeshRingSize - 1);
            activeDynamicMeshSlot = slot;
            Mesh slotMesh = dynamicMeshRing[slot];
            bool createdMesh = false;
            if (slotMesh == null)
            {
                slotMesh = new Mesh
                {
                    name = $"GCF_SkinnedMesh_{sDef.meshId ?? "mesh"}_{slot}"
                };
                slotMesh.MarkDynamic();
                dynamicMeshRing[slot] = slotMesh;
                dynamicMeshSlotInitialized[slot] = false;
                dynamicMeshSlotTopoVersion[slot] = 0UL;
                dynamicMeshSlotTriCount[slot] = 0;
                createdMesh = true;
            }

            if (!dynamicMeshSlotInitialized[slot])
            {
                slotMesh.Clear();
                slotMesh.vertices = rt.DeformedVertices;
                slotMesh.uv = rt.UVs;
                slotMesh.normals = rt.Normals;
                UploadActiveTrianglesTo(slotMesh, rt);
                dynamicMeshSlotInitialized[slot] = true;
                dynamicMeshSlotTopoVersion[slot] = currentTriangleTopoVersion;
                dynamicMeshSlotTriCount[slot] = rt.ActiveTriangleIndexCount;
            }
            else
            {
                slotMesh.vertices = rt.DeformedVertices;
                if (dynamicMeshSlotTopoVersion[slot] != currentTriangleTopoVersion
                    || dynamicMeshSlotTriCount[slot] != rt.ActiveTriangleIndexCount)
                {
                    UploadActiveTrianglesTo(slotMesh, rt);
                    dynamicMeshSlotTopoVersion[slot] = currentTriangleTopoVersion;
                    dynamicMeshSlotTriCount[slot] = rt.ActiveTriangleIndexCount;
                }
            }

            slotMesh.bounds = rt.CurrentBounds;
            bool becameValid = !hasValidMesh || createdMesh;
            hasValidMesh = true;
            if (becameValid)
            {
                MarkMaterialRecacheOnFamily();
            }
        }

        private void MarkMaterialRecacheOnFamily()
        {
            requestRecache = true;
            if (SilhouetteChildNode != null) SilhouetteChildNode.requestRecache = true;
            if (GlowChildNode != null) GlowChildNode.requestRecache = true;
        }

        private void UploadActiveTrianglesTo(Mesh targetMesh, SkinnedMeshRuntime rt)
        {
            int idxCount = rt.ActiveTriangleIndexCount;
            int[] src = rt.ActiveTriangleIndices;
            if (idxCount == src.Length)
            {
                targetMesh.SetTriangles(src, 0, calculateBounds: false);
                return;
            }

            if (triUploadScratch == null || triUploadScratch.Length != idxCount)
            {
                triUploadScratch = new int[idxCount];
            }
            System.Array.Copy(src, 0, triUploadScratch, 0, idxCount);
            targetMesh.SetTriangles(triUploadScratch, 0, calculateBounds: false);
        }
    }

    public class PawnRenderNodeWorker_GiantSkinnedMesh : PawnRenderNodeWorker
    {
        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantSkinnedMesh smNode)
            {
                CompGiantRig comp = smNode.RigComp;
                GiantSkinnedMeshDef sDef = smNode.SkinnedMeshDef;
                SkinnedMeshRuntime rt = smNode.Runtime;
                if (comp == null || sDef == null || rt == null)
                {
                    return false;
                }
                string activeTex = smNode.ResolveActiveTexPath();
                if (string.IsNullOrEmpty(activeTex) || !smNode.HasValidMesh || !rt.IsCurrentlyVisible)
                {
                    return false;
                }
                bool ownerDeadOrDowned = (parms.pawn != null && (parms.pawn.Dead || parms.pawn.Downed))
                    || (comp.Pawn != null && (comp.Pawn.Dead || comp.Pawn.Downed));
                if (smNode.IsGlowOverlay && ownerDeadOrDowned)
                {
                    return false;
                }
                if (comp.IsSkinnedMeshMissingThreadSafe(sDef.runtimeIndex))
                {
                    return false;
                }
                return node.DebugEnabled;
            }
            return base.CanDrawNow(node, parms);
        }

        protected override Material GetMaterial(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantSkinnedMesh smNode && smNode.SkinnedMeshDef != null)
            {
                string activeTex = smNode.ResolveActiveTexPath();
                if (string.IsNullOrEmpty(activeTex))
                {
                    return null;
                }

                GiantSkinnedSeveredTextureCache.SeveredMaterialBundle severedBundle = smNode.ActiveSeveredBundle;
                if (severedBundle != null && severedBundle.IsDisposed)
                {
                    severedBundle = null;
                }
                bool wantInvisible = !parms.Portrait && parms.flags.FlagSet(PawnRenderFlags.Invisible);

                if (smNode.IsGlowOverlay)
                {
                    if (severedBundle != null && severedBundle.GlowMaterial != null)
                    {
                        return wantInvisible ? (severedBundle.InvisibleGlowMaterial ?? severedBundle.GlowMaterial) : severedBundle.GlowMaterial;
                    }
                    Material glowMat = GetGraphic(node, parms)?.MatSingle
                        ?? GiantMaterialCache.Resolve(activeTex, smNode.SkinnedMeshDef.glowColor, ShaderDatabase.TransparentPostLight);
                    if (glowMat != null && wantInvisible)
                    {
                        glowMat = GiantMaterialCache.ResolveInvisibleMaterialThreadSafe(glowMat);
                    }
                    return glowMat;
                }

                if (smNode.IsSilhouetteUnderlay)
                {
                    if (severedBundle != null && severedBundle.SilMaterial != null)
                    {
                        return wantInvisible ? (severedBundle.InvisibleSilMaterial ?? severedBundle.SilMaterial) : severedBundle.SilMaterial;
                    }
                    Material silMat = GetGraphic(node, parms)?.MatSingle
                        ?? GiantMaterialCache.Resolve(activeTex, Color.white, ShaderDatabase.Cutout);
                    if (silMat != null && wantInvisible)
                    {
                        silMat = GiantMaterialCache.ResolveInvisibleMaterialThreadSafe(silMat);
                    }
                    return silMat;
                }

                if (severedBundle != null && severedBundle.ColorMaterial != null)
                {
                    return wantInvisible ? (severedBundle.InvisibleColorMaterial ?? severedBundle.ColorMaterial) : severedBundle.ColorMaterial;
                }
                Shader customShader = smNode.SkinnedMeshDef.shaderTypeDef?.Shader;
                Material mat = GetGraphic(node, parms)?.MatSingle
                    ?? GiantMaterialCache.Resolve(activeTex, Color.white, customShader);
                if (mat != null && wantInvisible)
                {
                    mat = GiantMaterialCache.ResolveInvisibleMaterialThreadSafe(mat);
                }
                return mat;
            }
            return base.GetMaterial(node, parms);
        }

        public override MaterialPropertyBlock GetMaterialPropertyBlock(PawnRenderNode node, Material material, PawnDrawParms parms)
        {
            MaterialPropertyBlock mpb = base.GetMaterialPropertyBlock(node, material, parms);
            if (mpb != null && node is PawnRenderNode_GiantSkinnedMesh smNode && smNode.RigComp != null && smNode.SkinnedMeshDef != null)
            {
                if (smNode.IsSilhouetteUnderlay)
                {
                    return mpb;
                }

                GiantRenderSnapshot snap = smNode.RigComp.LatestSnapshot;
                Pawn ownerPawn = smNode.RigComp.Pawn ?? parms.pawn;
                bool pawnDead = ownerPawn != null && ownerPawn.Dead;

                if (smNode.IsGlowOverlay)
                {
                    Color glowCol = smNode.SkinnedMeshDef.glowColor;
                    if (!pawnDead && snap != null && snap.hasMechShaderParams)
                    {
                        Color phaseCol = snap.phaseGlowColor;
                        glowCol = new Color(
                            phaseCol.r * glowCol.r,
                            phaseCol.g * glowCol.g,
                            phaseCol.b * glowCol.b,
                            phaseCol.a * glowCol.a);
                        mpb.SetColor(GiantShaderPropertyIDs.PhaseGlowColor, phaseCol);
                        mpb.SetVector(GiantShaderPropertyIDs.MechShaderParams, snap.mechShaderParams);
                    }
                    mpb.SetColor(ShaderPropertyIDs.Color, glowCol);
                    return mpb;
                }

                if (!pawnDead && snap != null && snap.hasMechShaderParams)
                {
                    mpb.SetColor(GiantShaderPropertyIDs.PhaseGlowColor, snap.phaseGlowColor);
                    mpb.SetVector(GiantShaderPropertyIDs.MechShaderParams, snap.mechShaderParams);
                }
                else if (pawnDead && snap != null && snap.hasMechShaderParams)
                {
                    mpb.SetColor(GiantShaderPropertyIDs.PhaseGlowColor, new Color(0f, 0f, 0f, 0f));
                    mpb.SetVector(GiantShaderPropertyIDs.MechShaderParams, Vector4.zero);
                }

                if (!parms.Portrait)
                {
                    if (pawnDead)
                    {
                        bool isSolidDead = smNode.SkinnedMeshDef.linkedBodyPart?.IsSolidInDefinition_Debug
                            ?? (ownerPawn.RaceProps?.IsMechanoid ?? false);
                        mpb.SetColor(ShaderPropertyIDs.Color, PawnRenderNodeWorker_GiantBone.ComputeDeadCorpseTint(parms.tint, isSolidDead));
                        return mpb;
                    }

                    int[] members = smNode.SkinnedMeshDef.resolvedMemberBoneIndices;
                    int anchorIdx = smNode.SkinnedMeshDef.resolvedAnchorBoneIndex;
                    float flash01 = 0f;
                    float hpPct = 1f;
                    int refBoneIdx = anchorIdx;

                    if (members != null && members.Length > 0)
                    {
                        float hpSum = 0f;
                        float minBoneHp = 1f;
                        int hpCnt = 0;
                        for (int i = 0; i < members.Length; i++)
                        {
                            int bIdx = members[i];
                            if (bIdx < 0)
                            {
                                continue;
                            }
                            float bf = smNode.RigComp.GetBoneDamageFlash01(bIdx);
                            if (bf > flash01)
                            {
                                flash01 = bf;
                                refBoneIdx = bIdx;
                            }
                            float bhp = smNode.RigComp.GetBoneHealthPercentThreadSafe(bIdx);
                            if (bhp > 0f)
                            {
                                hpSum += bhp;
                                if (bhp < minBoneHp)
                                {
                                    minBoneHp = bhp;
                                    if (flash01 <= 0.001f)
                                    {
                                        refBoneIdx = bIdx;
                                    }
                                }
                                hpCnt++;
                            }
                        }
                        if (hpCnt > 0)
                        {
                            float avgBoneHp = hpSum / hpCnt;
                            hpPct = Mathf.Lerp(avgBoneHp, minBoneHp, 0.65f);
                        }
                    }
                    else if (anchorIdx >= 0)
                    {
                        flash01 = smNode.RigComp.GetBoneDamageFlash01(anchorIdx);
                        hpPct = smNode.RigComp.GetBoneHealthPercentThreadSafe(anchorIdx);
                    }

                    if ((hpPct > 0f && hpPct < 0.80f) || flash01 > 0.001f)
                    {
                        BoneNodeDef[] nodes = smNode.RigComp.RigDef?.SortedNodes;
                        bool isSolid = smNode.SkinnedMeshDef.linkedBodyPart?.IsSolidInDefinition_Debug
                            ?? ((nodes != null && (uint)refBoneIdx < (uint)nodes.Length ? nodes[refBoneIdx].linkedBodyPart?.IsSolidInDefinition_Debug : null)
                            ?? (smNode.RigComp.Pawn?.RaceProps?.IsMechanoid ?? false));
                        Color finalTint = PawnRenderNodeWorker_GiantBone.ComposeGiantPartTint(parms.tint, hpPct, isSolid, flash01);
                        mpb.SetColor(ShaderPropertyIDs.Color, finalTint);
                    }
                }
            }
            return mpb;
        }

        public override float LayerFor(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantSkinnedMesh smNode && smNode.SkinnedMeshDef != null)
            {
                GiantSkinnedMeshDef sDef = smNode.SkinnedMeshDef;
                float offset = smNode.IsSilhouetteUnderlay
                    ? sDef.silhouetteLayerOffset
                    : (smNode.IsGlowOverlay ? (sDef.layerOffset + sDef.glowLayerOffset) : sDef.layerOffset);
                return node.Props.baseLayer + offset + node.debugLayerOffset;
            }
            return base.LayerFor(node, parms);
        }
    }
}
