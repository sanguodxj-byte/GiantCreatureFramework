using System.Collections.Concurrent;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public static class GiantShaderPropertyIDs
    {
        public static readonly int PhaseGlowColor = Shader.PropertyToID("_PhaseGlowColor");
        public static readonly int MechShaderParams = Shader.PropertyToID("_MechShaderParams");
        public static readonly int MaskTex = Shader.PropertyToID("_MaskTex");
    }

    public static class GiantMaterialCache
    {
        private readonly record struct MatKey(string TexPath, string ShaderName, Color32 ColorKey);
        private static readonly ConcurrentDictionary<MatKey, Material> Cache = new();
        private static readonly ConcurrentDictionary<Material, Material> InvisibleMatCache = new();

        public static void WarmInvisibleMaterialOnMainThread(Material mat)
        {
            if (mat == null || !UnityData.IsInMainThread)
            {
                return;
            }
            if (InvisibleMatCache.TryGetValue(mat, out Material existing) && existing != null)
            {
                return;
            }
            Material inv = InvisibilityMatPool.GetInvisibleMat(mat);
            if (inv != null)
            {
                InvisibleMatCache[mat] = inv;
            }
        }

        public static Material ResolveInvisibleMaterialThreadSafe(Material mat)
        {
            if (mat == null)
            {
                return null;
            }
            if (InvisibleMatCache.TryGetValue(mat, out Material cached) && cached != null)
            {
                return cached;
            }
            if (!UnityData.IsInMainThread)
            {
                return mat;
            }
            Material inv = InvisibilityMatPool.GetInvisibleMat(mat);
            if (inv != null)
            {
                InvisibleMatCache[mat] = inv;
                return inv;
            }
            return mat;
        }

        public static void TryBindMaskTexture(Material mat, string texPath)
        {
            if (mat == null || string.IsNullOrEmpty(texPath) || !UnityData.IsInMainThread)
            {
                return;
            }
            Texture2D maskTex = ContentFinder<Texture2D>.Get(texPath + "m", reportFailure: false);
            if (maskTex != null)
            {
                if (maskTex.wrapMode != TextureWrapMode.Clamp)
                {
                    maskTex.wrapMode = TextureWrapMode.Clamp;
                }
                mat.SetTexture(GiantShaderPropertyIDs.MaskTex, maskTex);
            }
        }

        public static void WarmOnMainThread(string texPath, Color color, Shader shader = null)
        {
            if (string.IsNullOrEmpty(texPath) || !UnityData.IsInMainThread)
            {
                return;
            }
            Resolve(texPath, color, shader);
            Resolve(texPath, Color.white, shader);
        }

        public static void Invalidate(string texPath)
        {
            if (string.IsNullOrEmpty(texPath))
            {
                return;
            }
            foreach (MatKey key in Cache.Keys)
            {
                if (string.Equals(key.TexPath, texPath, System.StringComparison.Ordinal))
                {
                    if (Cache.TryRemove(key, out Material removed) && removed != null)
                    {
                        InvisibleMatCache.TryRemove(removed, out _);
                    }
                }
            }
            GiantSkinnedSeveredTextureCache.InvalidateAll();
        }

        public static Material Resolve(string texPath, Color color, Shader shader = null)
        {
            if (string.IsNullOrEmpty(texPath))
            {
                return null;
            }

            Shader resolvedShader = shader != null ? shader : ShaderDatabase.Cutout;
            string shaderKey = resolvedShader != null ? resolvedShader.name : "Cutout";
            Color32 c32 = color;
            var key = new MatKey(texPath, shaderKey, c32);
            if (Cache.TryGetValue(key, out Material cached))
            {
                return cached;
            }

            if (!UnityData.IsInMainThread)
            {
                return Cache.TryGetValue(new MatKey(texPath, shaderKey, new Color32(255, 255, 255, 255)), out cached) ? cached : null;
            }

            Texture2D tex = ContentFinder<Texture2D>.Get(texPath, reportFailure: false);
            if (tex == null)
            {
                return null;
            }

            Material mat = MaterialPool.MatFrom(tex, resolvedShader, color);
            if (mat != null)
            {
                TryBindMaskTexture(mat, texPath);
                WarmInvisibleMaterialOnMainThread(mat);
                Cache[key] = mat;
            }
            return mat;
        }
    }

    public class PawnRenderNodeProperties_GiantRoot : PawnRenderNodeProperties
    {
        public PawnRenderNodeProperties_GiantRoot()
        {
            nodeClass = typeof(PawnRenderNode_GiantRoot);
            workerClass = typeof(PawnRenderNodeWorker);
            useGraphic = false;
            rotDrawMode = RotDrawMode.Fresh | RotDrawMode.Rotting | RotDrawMode.Dessicated;
        }
    }

    public class PawnRenderNode_GiantRoot : PawnRenderNode
    {
        public PawnRenderNode_GiantRoot(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree)
        {
        }

        public override GraphicMeshSet MeshSetFor(Pawn pawn)
        {
            return null;
        }
    }

    public class PawnRenderNodeProperties_GiantBone : PawnRenderNodeProperties
    {
        public BoneNodeDef boneDef;
        public CompGiantRig rigComp;
        public bool isGlowOverlay = false;
        public bool isSilhouetteUnderlay = false;

        public PawnRenderNodeProperties_GiantBone()
        {
            nodeClass = typeof(PawnRenderNode_GiantBone);
            workerClass = typeof(PawnRenderNodeWorker_GiantBone);
            rotDrawMode = RotDrawMode.Fresh | RotDrawMode.Rotting | RotDrawMode.Dessicated;
        }
    }

    public class PawnRenderNode_GiantBone : PawnRenderNode
    {
        private Graphic dorsalGraphic;
        private Graphic eastGraphic;
        private Graphic westGraphic;

        public BoneNodeDef BoneDef => (props as PawnRenderNodeProperties_GiantBone)?.boneDef;
        public CompGiantRig RigComp => (props as PawnRenderNodeProperties_GiantBone)?.rigComp;
        public bool IsGlowOverlay => (props as PawnRenderNodeProperties_GiantBone)?.isGlowOverlay ?? false;
        public bool IsSilhouetteUnderlay => (props as PawnRenderNodeProperties_GiantBone)?.isSilhouetteUnderlay ?? false;

        internal string ResolveActiveTexPath(GiantBoneViewSector sector)
        {
            BoneNodeDef bDef = BoneDef;
            if (bDef == null)
            {
                return null;
            }
            if (IsSilhouetteUnderlay)
            {
                return bDef.GetSilhouetteTexPath(sector);
            }
            return IsGlowOverlay ? bDef.GetGlowTexPath(sector) : bDef.GetTexPath(sector);
        }

        public PawnRenderNode_GiantBone(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree)
        {
        }

        public Graphic GetCachedSectorGraphic(GiantBoneViewSector sector)
        {
            return sector switch
            {
                GiantBoneViewSector.East => eastGraphic ?? dorsalGraphic,
                GiantBoneViewSector.West => westGraphic ?? dorsalGraphic,
                _ => dorsalGraphic
            };
        }

        public override GraphicMeshSet MeshSetFor(Pawn pawn)
        {
            return UnityData.IsInMainThread ? MeshPool.GetMeshSetForSize(1f, 1f) : null;
        }

        public override Mesh GetMesh(PawnDrawParms parms)
        {
            return MeshPool.plane10;
        }

        public override bool FlipGraphic(PawnDrawParms parms)
        {
            return false;
        }

        public override Graphic GraphicFor(Pawn pawn)
        {
            BoneNodeDef bDef = BoneDef;
            if (bDef == null)
            {
                return null;
            }
            string dorsalPath = ResolveActiveTexPath(GiantBoneViewSector.Dorsal);
            string eastPath = ResolveActiveTexPath(GiantBoneViewSector.East);
            string westPath = ResolveActiveTexPath(GiantBoneViewSector.West);
            if (string.IsNullOrEmpty(dorsalPath) && string.IsNullOrEmpty(eastPath) && string.IsNullOrEmpty(westPath))
            {
                return null;
            }
            Color col = IsSilhouetteUnderlay
                ? Color.white
                : (IsGlowOverlay ? bDef.glowColor : ColorFor(pawn));
            Shader shader = IsSilhouetteUnderlay
                ? ShaderDatabase.Cutout
                : (IsGlowOverlay
                    ? ShaderDatabase.TransparentPostLight
                    : (bDef.shaderTypeDef?.Shader ?? ShaderFor(pawn)));
            if (shader == null)
            {
                shader = ShaderDatabase.Cutout;
            }

            if (!string.IsNullOrEmpty(dorsalPath))
            {
                GiantMaterialCache.WarmOnMainThread(dorsalPath, col, shader);
                dorsalGraphic = GraphicDatabase.Get<Graphic_Single>(dorsalPath, shader, Vector2.one, col);
                if (!IsSilhouetteUnderlay && !IsGlowOverlay)
                {
                    GiantMaterialCache.TryBindMaskTexture(dorsalGraphic?.MatSingle, dorsalPath);
                }
                GiantMaterialCache.WarmInvisibleMaterialOnMainThread(dorsalGraphic?.MatSingle);
            }
            else
            {
                dorsalGraphic = null;
            }

            if (!string.IsNullOrEmpty(eastPath))
            {
                if (string.Equals(eastPath, dorsalPath, System.StringComparison.Ordinal))
                {
                    eastGraphic = dorsalGraphic;
                }
                else
                {
                    GiantMaterialCache.WarmOnMainThread(eastPath, col, shader);
                    eastGraphic = GraphicDatabase.Get<Graphic_Single>(eastPath, shader, Vector2.one, col);
                    if (!IsSilhouetteUnderlay && !IsGlowOverlay)
                    {
                        GiantMaterialCache.TryBindMaskTexture(eastGraphic?.MatSingle, eastPath);
                    }
                    GiantMaterialCache.WarmInvisibleMaterialOnMainThread(eastGraphic?.MatSingle);
                }
            }
            else
            {
                eastGraphic = dorsalGraphic;
            }

            if (!string.IsNullOrEmpty(westPath))
            {
                if (string.Equals(westPath, dorsalPath, System.StringComparison.Ordinal))
                {
                    westGraphic = dorsalGraphic;
                }
                else if (string.Equals(westPath, eastPath, System.StringComparison.Ordinal))
                {
                    westGraphic = eastGraphic;
                }
                else
                {
                    GiantMaterialCache.WarmOnMainThread(westPath, col, shader);
                    westGraphic = GraphicDatabase.Get<Graphic_Single>(westPath, shader, Vector2.one, col);
                    if (!IsSilhouetteUnderlay && !IsGlowOverlay)
                    {
                        GiantMaterialCache.TryBindMaskTexture(westGraphic?.MatSingle, westPath);
                    }
                    GiantMaterialCache.WarmInvisibleMaterialOnMainThread(westGraphic?.MatSingle);
                }
            }
            else
            {
                westGraphic = dorsalGraphic ?? eastGraphic;
            }

            return dorsalGraphic ?? eastGraphic ?? westGraphic;
        }
    }

    public class PawnRenderNodeWorker_GiantBone : PawnRenderNodeWorker
    {
        private static GiantBoneViewSector ResolveDrawSector(PawnRenderNode_GiantBone boneNode, PawnDrawParms parms)
        {
            if (boneNode?.BoneDef == null)
            {
                return GiantBoneViewSector.Dorsal;
            }
            return boneNode.RigComp != null
                ? boneNode.RigComp.GetBoneSectorThreadSafe(boneNode.BoneDef.runtimeIndex, parms.Portrait)
                : GiantBoneViewSector.Dorsal;
        }

        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantBone boneNode)
            {
                CompGiantRig comp = boneNode.RigComp;
                BoneNodeDef bDef = boneNode.BoneDef;
                if (comp == null || bDef == null)
                {
                    return false;
                }
                GiantBoneViewSector sector = ResolveDrawSector(boneNode, parms);
                string activeTex = boneNode.ResolveActiveTexPath(sector);
                if (string.IsNullOrEmpty(activeTex))
                {
                    return false;
                }
                if (boneNode.IsGlowOverlay && (comp.Pawn == null || comp.Pawn.Dead))
                {
                    return false;
                }
                if (!comp.IsBoneVisibleThreadSafe(bDef.runtimeIndex))
                {
                    return false;
                }
                return node.DebugEnabled;
            }
            return base.CanDrawNow(node, parms);
        }

        protected override Graphic GetGraphic(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantBone boneNode && boneNode.BoneDef != null)
            {
                GiantBoneViewSector sector = ResolveDrawSector(boneNode, parms);
                Graphic cached = boneNode.GetCachedSectorGraphic(sector);
                if (cached != null)
                {
                    return cached;
                }
            }
            return base.GetGraphic(node, parms);
        }

        protected override Material GetMaterial(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantBone boneNode && boneNode.BoneDef != null)
            {
                GiantBoneViewSector sector = ResolveDrawSector(boneNode, parms);
                Graphic g = GetGraphic(node, parms);
                string activeTex = boneNode.ResolveActiveTexPath(sector);
                Color activeCol = boneNode.IsSilhouetteUnderlay
                    ? Color.white
                    : (boneNode.IsGlowOverlay ? boneNode.BoneDef.glowColor : Color.white);
                Shader customShader = boneNode.IsSilhouetteUnderlay
                    ? ShaderDatabase.Cutout
                    : (boneNode.IsGlowOverlay
                        ? ShaderDatabase.TransparentPostLight
                        : boneNode.BoneDef.shaderTypeDef?.Shader);
                Material mat = g?.MatSingle ?? GiantMaterialCache.Resolve(activeTex, activeCol, customShader);
                if (mat != null && !parms.Portrait && parms.flags.FlagSet(PawnRenderFlags.Invisible))
                {
                    mat = GiantMaterialCache.ResolveInvisibleMaterialThreadSafe(mat);
                }
                return mat;
            }
            return base.GetMaterial(node, parms);
        }

        public static Color ComputeBattleDamageTint(float hpPct, bool isSolidOrMechanoid)
        {
            if (hpPct >= 0.80f || hpPct <= 0f)
            {
                return Color.white;
            }
            float dmg01 = Mathf.Clamp01((0.80f - hpPct) / 0.80f);
            float curved = dmg01 * dmg01 * 0.35f + dmg01 * 0.65f;
            Color targetTint = isSolidOrMechanoid
                ? new Color(0.55f, 0.50f, 0.47f, 1f)
                : new Color(0.64f, 0.48f, 0.48f, 1f);
            return Color.Lerp(Color.white, targetTint, curved * 0.88f);
        }

        public static Color ComposeGiantPartTint(Color baseTint, float hpPct, bool isSolidOrMechanoid, float damageFlash01)
        {
            Color result = baseTint;
            if (hpPct > 0f && hpPct < 0.80f)
            {
                result *= ComputeBattleDamageTint(hpPct, isSolidOrMechanoid);
            }
            if (damageFlash01 > 0.001f)
            {
                float f = Mathf.Clamp01(damageFlash01);
                Color flashMul = Color.Lerp(Color.white, new Color(1.22f, 0.16f, 0.14f, 1f), f);
                result = new Color(result.r * flashMul.r, result.g * flashMul.g, result.b * flashMul.b, baseTint.a);
            }
            return result;
        }

        public static Color ComputeDeadCorpseTint(Color baseTint, bool isSolidOrMechanoid = false, bool hasSeveredChildBone = false)
        {
            Color corpseMul = isSolidOrMechanoid
                ? new Color(0.48f, 0.45f, 0.44f, 1f)
                : new Color(0.44f, 0.37f, 0.39f, 1f);
            if (hasSeveredChildBone)
            {
                corpseMul *= new Color(0.76f, 0.72f, 0.73f, 1f);
            }
            return new Color(baseTint.r * corpseMul.r, baseTint.g * corpseMul.g, baseTint.b * corpseMul.b, baseTint.a);
        }

        private static bool HasAnyMissingDirectChildBone(GiantRigDef rigDef, int boneIdx, bool[] missingMask)
        {
            if (rigDef?.SortedNodes == null || missingMask == null)
            {
                return false;
            }
            BoneNodeDef[] nodes = rigDef.SortedNodes;
            int n = Mathf.Min(nodes.Length, missingMask.Length);
            for (int i = boneIdx + 1; i < n; i++)
            {
                if (nodes[i].parentRuntimeIndex == boneIdx && missingMask[i])
                {
                    return true;
                }
            }
            return false;
        }

        public override MaterialPropertyBlock GetMaterialPropertyBlock(PawnRenderNode node, Material material, PawnDrawParms parms)
        {
            MaterialPropertyBlock mpb = base.GetMaterialPropertyBlock(node, material, parms);
            if (mpb != null && node is PawnRenderNode_GiantBone boneNode && boneNode.RigComp != null && !boneNode.IsGlowOverlay && !boneNode.IsSilhouetteUnderlay)
            {
                GiantRenderSnapshot snap = boneNode.RigComp.LatestSnapshot;
                bool pawnDead = (parms.pawn != null && parms.pawn.Dead) || (boneNode.RigComp.Pawn != null && boneNode.RigComp.Pawn.Dead);
                if (snap != null)
                {
                    if (!parms.Portrait && boneNode.BoneDef != null)
                    {
                        int idx = boneNode.BoneDef.runtimeIndex;
                        bool isSolid = boneNode.BoneDef.linkedBodyPart?.IsSolidInDefinition_Debug
                            ?? (boneNode.RigComp.Pawn?.RaceProps?.IsMechanoid ?? false);
                        if (pawnDead)
                        {
                            bool hasSeveredChild = HasAnyMissingDirectChildBone(boneNode.RigComp.RigDef, idx, snap.missingMask);
                            Color corpseTint = ComputeDeadCorpseTint(parms.tint, isSolid, hasSeveredChild);
                            mpb.SetColor(ShaderPropertyIDs.Color, corpseTint);
                        }
                        else
                        {
                            float hpPct = (snap.partHealthPct != null && (uint)idx < (uint)snap.partHealthPct.Length)
                                ? snap.partHealthPct[idx]
                                : 1f;
                            float flash01 = boneNode.RigComp.GetBoneDamageFlash01(idx);
                            if ((hpPct > 0f && hpPct < 0.80f) || flash01 > 0.001f)
                            {
                                Color finalTint = ComposeGiantPartTint(parms.tint, hpPct, isSolid, flash01);
                                mpb.SetColor(ShaderPropertyIDs.Color, finalTint);
                            }
                        }
                    }

                    if (snap.hasMechShaderParams)
                    {
                        Vector4 shaderVec = snap.mechShaderParams;
                        if (pawnDead)
                        {
                            shaderVec.y = 0f;
                            mpb.SetColor(GiantShaderPropertyIDs.PhaseGlowColor, Color.clear);
                            mpb.SetVector(GiantShaderPropertyIDs.MechShaderParams, shaderVec);
                        }
                        else
                        {
                            if (boneNode.BoneDef != null && boneNode.BoneDef.isReactorCoreBone)
                            {
                                shaderVec.y *= 1.45f;
                            }
                            mpb.SetColor(GiantShaderPropertyIDs.PhaseGlowColor, snap.phaseGlowColor);
                            mpb.SetVector(GiantShaderPropertyIDs.MechShaderParams, shaderVec);
                        }
                    }
                }
            }
            return mpb;
        }

        public override float LayerFor(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantBone boneNode && boneNode.BoneDef != null)
            {
                if (boneNode.IsSilhouetteUnderlay)
                {
                    return node.Props.baseLayer + boneNode.BoneDef.silhouetteLayerOffset + node.debugLayerOffset;
                }
                GiantBoneViewSector sector = ResolveDrawSector(boneNode, parms);
                float layerOff = boneNode.BoneDef.GetLayerOffset(sector);
                float extra = boneNode.IsGlowOverlay ? boneNode.BoneDef.glowLayerOffset : 0f;
                return node.Props.baseLayer + layerOff + extra + node.debugLayerOffset;
            }
            return base.LayerFor(node, parms);
        }
    }
}
