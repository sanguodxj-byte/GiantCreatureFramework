using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public class PawnRenderNodeProperties_GiantRollingTrack : PawnRenderNodeProperties
    {
        public GiantRollingTrackDef trackDef;
        public CompGiantRig rigComp;

        public PawnRenderNodeProperties_GiantRollingTrack()
        {
            nodeClass = typeof(PawnRenderNode_GiantRollingTrack);
            workerClass = typeof(PawnRenderNodeWorker_GiantRollingTrack);
            rotDrawMode = RotDrawMode.Fresh | RotDrawMode.Rotting | RotDrawMode.Dessicated;
        }
    }

    public class PawnRenderNode_GiantRollingTrack : PawnRenderNode
    {
        private Mesh dynamicMesh;
        private readonly List<Vector3> vertBuffer = new List<Vector3>(128);
        private readonly List<Vector2> uvBuffer = new List<Vector2>(128);
        private readonly List<float> baseVBuffer = new List<float>(128);
        private readonly List<int> triBuffer = new List<int>(384);
        private bool hasValidMesh;
        private GiantBoneViewSector lastBuiltSector;
        private Vector2 lastBuiltDrawSize;
        private int lastBuiltTexRes;
        private float lastBuiltScrollCells;
        private float lastBuiltEffectiveTileLength = 1f;
        private bool lastBuiltVisible;
        private Graphic dorsalGraphic;
        private Graphic lateralGraphic;

        public GiantRollingTrackDef TrackDef => (props as PawnRenderNodeProperties_GiantRollingTrack)?.trackDef;
        public CompGiantRig RigComp => (props as PawnRenderNodeProperties_GiantRollingTrack)?.rigComp;
        public bool HasValidMesh => hasValidMesh && dynamicMesh != null;

        public void ReleaseDynamicMeshOnMainThread()
        {
            if (dynamicMesh != null)
            {
                if (UnityData.IsInMainThread)
                {
                    UnityEngine.Object.Destroy(dynamicMesh);
                }
                dynamicMesh = null;
            }
            hasValidMesh = false;
            lastBuiltVisible = false;
        }

        public PawnRenderNode_GiantRollingTrack(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree)
        {
        }

        public Graphic GetCachedSectorGraphic(GiantBoneViewSector sector)
        {
            return sector == GiantBoneViewSector.Dorsal
                ? (dorsalGraphic ?? lateralGraphic)
                : (lateralGraphic ?? dorsalGraphic);
        }

        public override GraphicMeshSet MeshSetFor(Pawn pawn)
        {
            return UnityData.IsInMainThread ? MeshPool.GetMeshSetForSize(1f, 1f) : null;
        }

        public override Mesh GetMesh(PawnDrawParms parms)
        {
            return hasValidMesh && dynamicMesh != null ? dynamicMesh : MeshPool.plane10;
        }

        public override bool FlipGraphic(PawnDrawParms parms)
        {
            return false;
        }

        public override Graphic GraphicFor(Pawn pawn)
        {
            GiantRollingTrackDef tDef = TrackDef;
            if (tDef == null)
            {
                return null;
            }
            Color col = ColorFor(pawn);
            Shader shader = ShaderFor(pawn) ?? ShaderDatabase.Cutout;

            dorsalGraphic = ResolveRepeatGraphic(tDef.dorsalRibbonTexPath, col, shader);
            lateralGraphic = !string.IsNullOrEmpty(tDef.lateralRibbonTexPath)
                ? ResolveRepeatGraphic(tDef.lateralRibbonTexPath, col, shader)
                : dorsalGraphic;
            return dorsalGraphic ?? lateralGraphic;
        }

        private static Graphic ResolveRepeatGraphic(string texPath, Color col, Shader shader)
        {
            if (string.IsNullOrEmpty(texPath))
            {
                return null;
            }
            Texture2D tex = ContentFinder<Texture2D>.Get(texPath, reportFailure: false);
            if (tex != null && tex.wrapMode != TextureWrapMode.Repeat)
            {
                tex.wrapMode = TextureWrapMode.Repeat;
            }
            GiantMaterialCache.WarmOnMainThread(texPath, col, shader);
            Graphic g = GraphicDatabase.Get<Graphic_Single>(texPath, shader, Vector2.one, col);
            GiantMaterialCache.WarmInvisibleMaterialOnMainThread(g?.MatSingle);
            return g;
        }

        /// <summary>
        /// Rebuilds the dynamic quad-strip caterpillar belt mesh strictly on the Unity main thread.
        /// ParallelPreDraw worker threads only read the already-uploaded dynamicMesh reference.
        /// </summary>
        public void RebuildMeshOnMainThread(
            GiantBoneViewSector parentSector,
            Vector2 parentDrawSize,
            int parentTexRes,
            float scrollCells,
            bool visible)
        {
            if (!UnityData.IsInMainThread)
            {
                return;
            }

            GiantRollingTrackDef tDef = TrackDef;
            if (!visible || tDef == null)
            {
                hasValidMesh = false;
                lastBuiltVisible = false;
                return;
            }

            if (hasValidMesh
                && dynamicMesh != null
                && lastBuiltVisible
                && lastBuiltSector == parentSector
                && lastBuiltTexRes == parentTexRes
                && (lastBuiltDrawSize - parentDrawSize).sqrMagnitude < 1e-6f)
            {
                if (Mathf.Abs(lastBuiltScrollCells - scrollCells) < 1e-4f)
                {
                    return;
                }

                // Fast path: geometry/normals/triangles are identical; shift UV.y in-place with zero topology rebuild or GC.
                if (baseVBuffer.Count == uvBuffer.Count && uvBuffer.Count >= 4 && lastBuiltEffectiveTileLength > 1e-4f)
                {
                    float wrapped = Mathf.Repeat(scrollCells, lastBuiltEffectiveTileLength);
                    float vShift = wrapped / lastBuiltEffectiveTileLength;
                    for (int i = 0; i < uvBuffer.Count; i++)
                    {
                        Vector2 uv = uvBuffer[i];
                        uv.y = baseVBuffer[i] - vShift;
                        uvBuffer[i] = uv;
                    }
                    dynamicMesh.SetUVs(0, uvBuffer);
                    lastBuiltScrollCells = scrollCells;
                    return;
                }
            }

            bool mirrorX = parentSector == GiantBoneViewSector.West;
            bool built;
            float effectiveTileLength = Mathf.Max(0.25f, tDef.tileLengthCells);
            if (parentSector == GiantBoneViewSector.Dorsal)
            {
                if (string.IsNullOrEmpty(tDef.dorsalRibbonTexPath))
                {
                    hasValidMesh = false;
                    return;
                }
                built = GiantRollingTrackMeshBuilder.BuildOpenStrip(
                    tDef.dorsalStartPixel,
                    tDef.dorsalEndPixel,
                    tDef.dorsalWidthCells,
                    tDef.tileLengthCells,
                    parentTexRes,
                    parentDrawSize,
                    mirrorX: false,
                    scrollCells,
                    vertBuffer,
                    uvBuffer,
                    triBuffer,
                    segments: 12);
            }
            else if (tDef.IsNearSector(parentSector) && tDef.nearLoopPixels != null && tDef.nearLoopPixels.Count >= 4)
            {
                if (string.IsNullOrEmpty(tDef.lateralRibbonTexPath) && string.IsNullOrEmpty(tDef.dorsalRibbonTexPath))
                {
                    hasValidMesh = false;
                    return;
                }
                built = GiantRollingTrackMeshBuilder.BuildClosedLoopStrip(
                    tDef.nearLoopPixels,
                    tDef.nearWidthCells,
                    tDef.tileLengthCells,
                    parentTexRes,
                    parentDrawSize,
                    mirrorX,
                    scrollCells,
                    vertBuffer,
                    uvBuffer,
                    triBuffer);
                if (built && uvBuffer.Count >= 2)
                {
                    // Derive exact quantized effectiveTile from closed-loop UV span (endV - startV == repeats).
                    float repeats = Mathf.Max(1f, Mathf.Abs(uvBuffer[uvBuffer.Count - 2].y - uvBuffer[0].y));
                    float perim = 0f;
                    int rawCount = tDef.nearLoopPixels.Count;
                    Vector2 prevPt = GiantRollingTrackMeshBuilder.PixelToBoneLocalXZ(tDef.nearLoopPixels[0], parentTexRes, parentDrawSize, mirrorX);
                    for (int i = 1; i <= rawCount; i++)
                    {
                        Vector2 curPt = GiantRollingTrackMeshBuilder.PixelToBoneLocalXZ(tDef.nearLoopPixels[i % rawCount], parentTexRes, parentDrawSize, mirrorX);
                        perim += Vector2.Distance(prevPt, curPt);
                        prevPt = curPt;
                    }
                    if (perim > 1e-3f)
                    {
                        effectiveTileLength = perim / repeats;
                    }
                }
            }
            else
            {
                if ((string.IsNullOrEmpty(tDef.lateralRibbonTexPath) && string.IsNullOrEmpty(tDef.dorsalRibbonTexPath))
                    || tDef.farWidthCells <= 1e-3f)
                {
                    hasValidMesh = false;
                    return;
                }
                built = GiantRollingTrackMeshBuilder.BuildOpenStrip(
                    tDef.farStartPixel,
                    tDef.farEndPixel,
                    tDef.farWidthCells,
                    tDef.tileLengthCells,
                    parentTexRes,
                    parentDrawSize,
                    mirrorX,
                    scrollCells,
                    vertBuffer,
                    uvBuffer,
                    triBuffer,
                    segments: 10);
            }

            if (!built || vertBuffer.Count < 4 || triBuffer.Count < 6)
            {
                hasValidMesh = false;
                return;
            }

            baseVBuffer.Clear();
            float initShift = Mathf.Repeat(scrollCells, effectiveTileLength) / effectiveTileLength;
            for (int i = 0; i < uvBuffer.Count; i++)
            {
                baseVBuffer.Add(uvBuffer[i].y + initShift);
            }

            bool createdMesh = false;
            if (dynamicMesh == null)
            {
                dynamicMesh = new Mesh
                {
                    name = $"GCF_RollingTrack_{tDef.trackId ?? "track"}"
                };
                dynamicMesh.MarkDynamic();
                createdMesh = true;
            }

            dynamicMesh.Clear();
            dynamicMesh.SetVertices(vertBuffer);
            dynamicMesh.SetUVs(0, uvBuffer);
            dynamicMesh.SetTriangles(triBuffer, 0);
            dynamicMesh.RecalculateNormals();
            dynamicMesh.RecalculateBounds();

            Bounds b = dynamicMesh.bounds;
            b.Expand(1.0f);
            dynamicMesh.bounds = b;
            bool becameValid = !hasValidMesh || createdMesh;
            hasValidMesh = true;
            lastBuiltSector = parentSector;
            lastBuiltDrawSize = parentDrawSize;
            lastBuiltTexRes = parentTexRes;
            lastBuiltScrollCells = scrollCells;
            lastBuiltEffectiveTileLength = effectiveTileLength;
            lastBuiltVisible = true;
            if (becameValid)
            {
                requestRecache = true;
            }
        }
    }

    public class PawnRenderNodeWorker_GiantRollingTrack : PawnRenderNodeWorker
    {
        private static GiantBoneViewSector ResolveSector(PawnRenderNode_GiantRollingTrack trackNode, PawnDrawParms parms)
        {
            if (trackNode?.RigComp == null || trackNode.TrackDef == null)
            {
                return GiantBoneViewSector.Dorsal;
            }
            return trackNode.RigComp.GetBoneSectorThreadSafe(trackNode.TrackDef.resolvedParentBoneIndex, parms.Portrait);
        }

        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantRollingTrack trackNode)
            {
                if (parms.Portrait)
                {
                    return false;
                }
                CompGiantRig comp = trackNode.RigComp;
                GiantRollingTrackDef tDef = trackNode.TrackDef;
                if (comp == null || tDef == null || tDef.resolvedParentBoneIndex < 0)
                {
                    return false;
                }
                if (!trackNode.HasValidMesh || !comp.IsBoneVisibleThreadSafe(tDef.resolvedParentBoneIndex))
                {
                    return false;
                }
                return node.DebugEnabled;
            }
            return base.CanDrawNow(node, parms);
        }

        protected override Graphic GetGraphic(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantRollingTrack trackNode && trackNode.TrackDef != null)
            {
                GiantBoneViewSector sec = ResolveSector(trackNode, parms);
                Graphic g = trackNode.GetCachedSectorGraphic(sec);
                if (g != null)
                {
                    return g;
                }
            }
            return base.GetGraphic(node, parms);
        }

        protected override Material GetMaterial(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantRollingTrack trackNode && trackNode.TrackDef != null)
            {
                GiantBoneViewSector sec = ResolveSector(trackNode, parms);
                Graphic g = GetGraphic(node, parms);
                string activePath = sec == GiantBoneViewSector.Dorsal
                    ? trackNode.TrackDef.dorsalRibbonTexPath
                    : (!string.IsNullOrEmpty(trackNode.TrackDef.lateralRibbonTexPath)
                        ? trackNode.TrackDef.lateralRibbonTexPath
                        : trackNode.TrackDef.dorsalRibbonTexPath);
                Material mat = g?.MatSingle ?? GiantMaterialCache.Resolve(activePath, Color.white);
                if (mat != null && !parms.Portrait && parms.flags.FlagSet(PawnRenderFlags.Invisible))
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
            if (mpb != null && node is PawnRenderNode_GiantRollingTrack trackNode && trackNode.RigComp != null)
            {
                GiantRenderSnapshot snap = trackNode.RigComp.LatestSnapshot;
                Pawn ownerPawn = trackNode.RigComp.Pawn ?? parms.pawn;
                bool pawnDead = ownerPawn != null && ownerPawn.Dead;
                if (snap != null)
                {
                    if (!parms.Portrait && pawnDead)
                    {
                        mpb.SetColor(ShaderPropertyIDs.Color, PawnRenderNodeWorker_GiantBone.ComputeDeadCorpseTint(parms.tint, isSolidOrMechanoid: true));
                        if (snap.hasMechShaderParams)
                        {
                            mpb.SetColor(GiantShaderPropertyIDs.PhaseGlowColor, new Color(0f, 0f, 0f, 0f));
                            mpb.SetVector(GiantShaderPropertyIDs.MechShaderParams, Vector4.zero);
                        }
                        return mpb;
                    }
                    if (!parms.Portrait && trackNode.TrackDef != null && trackNode.TrackDef.resolvedParentBoneIndex >= 0)
                    {
                        int pIdx = trackNode.TrackDef.resolvedParentBoneIndex;
                        float hpPct = (snap.partHealthPct != null && (uint)pIdx < (uint)snap.partHealthPct.Length)
                            ? snap.partHealthPct[pIdx]
                            : 1f;
                        float flash01 = trackNode.RigComp.GetBoneDamageFlash01(pIdx);
                        if ((hpPct > 0f && hpPct < 0.80f) || flash01 > 0.001f)
                        {
                            Color finalTint = PawnRenderNodeWorker_GiantBone.ComposeGiantPartTint(parms.tint, hpPct, true, flash01);
                            mpb.SetColor(ShaderPropertyIDs.Color, finalTint);
                        }
                    }
                    if (snap.hasMechShaderParams)
                    {
                        mpb.SetColor(GiantShaderPropertyIDs.PhaseGlowColor, snap.phaseGlowColor);
                        mpb.SetVector(GiantShaderPropertyIDs.MechShaderParams, snap.mechShaderParams);
                    }
                }
            }
            return mpb;
        }

        public override float LayerFor(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantRollingTrack trackNode && trackNode.TrackDef != null)
            {
                GiantBoneViewSector sec = ResolveSector(trackNode, parms);
                return node.Props.baseLayer + trackNode.TrackDef.GetBeltLayerOffset(sec) + node.debugLayerOffset;
            }
            return base.LayerFor(node, parms);
        }
    }

    public class PawnRenderNodeProperties_GiantTrackWheel : PawnRenderNodeProperties
    {
        public GiantRollingTrackDef trackDef;
        public GiantTrackWheelDef wheelDef;
        public CompGiantRig rigComp;

        public PawnRenderNodeProperties_GiantTrackWheel()
        {
            nodeClass = typeof(PawnRenderNode_GiantTrackWheel);
            workerClass = typeof(PawnRenderNodeWorker_GiantTrackWheel);
            rotDrawMode = RotDrawMode.Fresh | RotDrawMode.Rotting | RotDrawMode.Dessicated;
        }
    }

    public class PawnRenderNode_GiantTrackWheel : PawnRenderNode
    {
        public GiantRollingTrackDef TrackDef => (props as PawnRenderNodeProperties_GiantTrackWheel)?.trackDef;
        public GiantTrackWheelDef WheelDef => (props as PawnRenderNodeProperties_GiantTrackWheel)?.wheelDef;
        public CompGiantRig RigComp => (props as PawnRenderNodeProperties_GiantTrackWheel)?.rigComp;

        public PawnRenderNode_GiantTrackWheel(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree)
        {
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
            GiantTrackWheelDef wDef = WheelDef;
            if (wDef == null || string.IsNullOrEmpty(wDef.texPath))
            {
                return null;
            }
            Color col = ColorFor(pawn);
            Shader shader = ShaderFor(pawn) ?? ShaderDatabase.Cutout;
            GiantMaterialCache.WarmOnMainThread(wDef.texPath, col, shader);
            Graphic g = GraphicDatabase.Get<Graphic_Single>(wDef.texPath, shader, Vector2.one, col);
            GiantMaterialCache.WarmInvisibleMaterialOnMainThread(g?.MatSingle);
            return g;
        }
    }

    public class PawnRenderNodeWorker_GiantTrackWheel : PawnRenderNodeWorker
    {
        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantTrackWheel wheelNode)
            {
                if (parms.Portrait)
                {
                    return false;
                }
                CompGiantRig comp = wheelNode.RigComp;
                GiantRollingTrackDef tDef = wheelNode.TrackDef;
                GiantTrackWheelDef wDef = wheelNode.WheelDef;
                if (comp == null || tDef == null || wDef == null || string.IsNullOrEmpty(wDef.texPath) || tDef.resolvedParentBoneIndex < 0)
                {
                    return false;
                }
                GiantBoneViewSector sec = comp.GetBoneSectorThreadSafe(tDef.resolvedParentBoneIndex, false);
                if (!tDef.IsNearSector(sec) || !comp.IsBoneVisibleThreadSafe(tDef.resolvedParentBoneIndex))
                {
                    return false;
                }
                return node.DebugEnabled;
            }
            return base.CanDrawNow(node, parms);
        }

        protected override Material GetMaterial(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantTrackWheel wheelNode && wheelNode.WheelDef != null)
            {
                Graphic g = GetGraphic(node, parms);
                Material mat = g?.MatSingle ?? GiantMaterialCache.Resolve(wheelNode.WheelDef.texPath, Color.white);
                if (mat != null && !parms.Portrait && parms.flags.FlagSet(PawnRenderFlags.Invisible))
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
            if (mpb != null && node is PawnRenderNode_GiantTrackWheel wheelNode && wheelNode.RigComp != null)
            {
                GiantRenderSnapshot snap = wheelNode.RigComp.LatestSnapshot;
                Pawn ownerPawn = wheelNode.RigComp.Pawn ?? parms.pawn;
                bool pawnDead = ownerPawn != null && ownerPawn.Dead;
                if (snap != null)
                {
                    if (!parms.Portrait && pawnDead)
                    {
                        mpb.SetColor(ShaderPropertyIDs.Color, PawnRenderNodeWorker_GiantBone.ComputeDeadCorpseTint(parms.tint, isSolidOrMechanoid: true));
                        if (snap.hasMechShaderParams)
                        {
                            mpb.SetColor(GiantShaderPropertyIDs.PhaseGlowColor, new Color(0f, 0f, 0f, 0f));
                            mpb.SetVector(GiantShaderPropertyIDs.MechShaderParams, Vector4.zero);
                        }
                        return mpb;
                    }
                    if (!parms.Portrait && wheelNode.TrackDef != null && wheelNode.TrackDef.resolvedParentBoneIndex >= 0)
                    {
                        int pIdx = wheelNode.TrackDef.resolvedParentBoneIndex;
                        float hpPct = (snap.partHealthPct != null && (uint)pIdx < (uint)snap.partHealthPct.Length)
                            ? snap.partHealthPct[pIdx]
                            : 1f;
                        float flash01 = wheelNode.RigComp.GetBoneDamageFlash01(pIdx);
                        if ((hpPct > 0f && hpPct < 0.80f) || flash01 > 0.001f)
                        {
                            Color finalTint = PawnRenderNodeWorker_GiantBone.ComposeGiantPartTint(parms.tint, hpPct, true, flash01);
                            mpb.SetColor(ShaderPropertyIDs.Color, finalTint);
                        }
                    }
                    if (snap.hasMechShaderParams)
                    {
                        mpb.SetColor(GiantShaderPropertyIDs.PhaseGlowColor, snap.phaseGlowColor);
                        mpb.SetVector(GiantShaderPropertyIDs.MechShaderParams, snap.mechShaderParams);
                    }
                }
            }
            return mpb;
        }

        public override float LayerFor(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantTrackWheel wheelNode && wheelNode.WheelDef != null)
            {
                return node.Props.baseLayer + wheelNode.WheelDef.layerOffset + node.debugLayerOffset;
            }
            return base.LayerFor(node, parms);
        }
    }
}
