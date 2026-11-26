using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public class PawnRenderNodeProperties_GiantSoftWhip : PawnRenderNodeProperties
    {
        public SoftWhipDef whipDef;
        public CompGiantRig rigComp;

        public PawnRenderNodeProperties_GiantSoftWhip()
        {
            nodeClass = typeof(PawnRenderNode_GiantSoftWhip);
            workerClass = typeof(PawnRenderNodeWorker_GiantSoftWhip);
            rotDrawMode = RotDrawMode.Fresh | RotDrawMode.Rotting | RotDrawMode.Dessicated;
        }
    }

    public class PawnRenderNode_GiantSoftWhip : PawnRenderNode
    {
        private Mesh dynamicMesh;
        private Mesh portraitRestMesh;
        private Vector3[] vertArray;
        private Vector2[] uvArray;
        private Vector3[] normArray;
        private int[] triArray;
        private int configuredSampleCount = -1;
        private int lastUploadedFrame = -1;
        private bool hasValidMesh;
        private bool hasValidPortraitMesh;
        private float lastBakedHeadingDeg;

        public SoftWhipDef WhipDef => (props as PawnRenderNodeProperties_GiantSoftWhip)?.whipDef;
        public CompGiantRig RigComp => (props as PawnRenderNodeProperties_GiantSoftWhip)?.rigComp;
        public bool HasValidMesh => hasValidMesh && dynamicMesh != null;
        public Mesh ActiveDynamicMesh => hasValidMesh ? dynamicMesh : null;
        public bool HasValidPortraitMesh => hasValidPortraitMesh && portraitRestMesh != null;
        public float LastBakedHeadingDeg => lastBakedHeadingDeg;

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
            if (portraitRestMesh != null)
            {
                if (UnityData.IsInMainThread)
                {
                    UnityEngine.Object.Destroy(portraitRestMesh);
                }
                portraitRestMesh = null;
            }
            hasValidMesh = false;
            hasValidPortraitMesh = false;
            lastUploadedFrame = -1;
            configuredSampleCount = -1;
        }

        public PawnRenderNode_GiantSoftWhip(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree)
        {
        }

        public override GraphicMeshSet MeshSetFor(Pawn pawn)
        {
            return UnityData.IsInMainThread ? MeshPool.GetMeshSetForSize(1f, 1f) : null;
        }

        public override Mesh GetMesh(PawnDrawParms parms)
        {
            if (parms.Portrait && hasValidPortraitMesh && portraitRestMesh != null)
            {
                return portraitRestMesh;
            }
            return hasValidMesh && dynamicMesh != null ? dynamicMesh : MeshPool.plane10;
        }

        public override bool FlipGraphic(PawnDrawParms parms)
        {
            return false;
        }

        public override Graphic GraphicFor(Pawn pawn)
        {
            SoftWhipDef wDef = WhipDef;
            if (wDef == null || string.IsNullOrEmpty(wDef.texPath))
            {
                return null;
            }
            Color col = ColorFor(pawn);
            Shader shader = ShaderFor(pawn);
            if (shader == null)
            {
                shader = ShaderDatabase.Cutout;
            }
            GiantMaterialCache.WarmOnMainThread(wDef.texPath, col, shader);
            Graphic g = GraphicDatabase.Get<Graphic_Single>(wDef.texPath, shader, Vector2.one, col);
            GiantMaterialCache.WarmInvisibleMaterialOnMainThread(g?.MatSingle);
            return g;
        }

        private void EnsurePortraitRestMeshOnMainThread()
        {
            if (hasValidPortraitMesh && portraitRestMesh != null)
            {
                return;
            }
            SoftWhipDef wDef = WhipDef;
            GiantRigDef rigDef = RigComp?.RigDef;
            if (wDef == null || rigDef == null || !UnityData.IsInMainThread)
            {
                return;
            }
            int nodeCount = rigDef.SortedNodes != null ? rigDef.SortedNodes.Length : 0;
            if (nodeCount == 0)
            {
                return;
            }

            var tempRt = new SoftWhipRuntime(wDef);
            var localPoses = new BoneLocalPose[nodeCount];
            var missingMask = new bool[nodeCount];
            var ctx = default(BezierChainStepContext);
            if (!tempRt.Step(rigDef, Vector2.zero, 0f, localPoses, missingMask, tendrilMissing: false, deltaTicks: 0f, in ctx))
            {
                return;
            }

            ReadOnlySpan<Vector2> leftSpan = tempRt.StripLeftLocalXZ;
            ReadOnlySpan<Vector2> rightSpan = tempRt.StripRightLocalXZ;
            ReadOnlySpan<float> vSpan = tempRt.VCoords;
            int m = leftSpan.Length;
            if (m < 2 || rightSpan.Length < m || vSpan.Length < m)
            {
                return;
            }

            int vCount = m * 2;
            var pVerts = new Vector3[vCount];
            var pUvs = new Vector2[vCount];
            var pNorms = new Vector3[vCount];
            var pTris = new int[(m - 1) * 6];
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int j = 0; j < m; j++)
            {
                Vector2 l = leftSpan[j];
                Vector2 r = rightSpan[j];
                int dst = j * 2;
                pVerts[dst] = new Vector3(l.x, 0f, l.y);
                pVerts[dst + 1] = new Vector3(r.x, 0f, r.y);
                pUvs[dst] = new Vector2(0f, vSpan[j]);
                pUvs[dst + 1] = new Vector2(1f, vSpan[j]);
                pNorms[dst] = Vector3.up;
                pNorms[dst + 1] = Vector3.up;

                if (l.x < minX) minX = l.x;
                if (l.x > maxX) maxX = l.x;
                if (r.x < minX) minX = r.x;
                if (r.x > maxX) maxX = r.x;
                if (l.y < minZ) minZ = l.y;
                if (l.y > maxZ) maxZ = l.y;
                if (r.y < minZ) minZ = r.y;
                if (r.y > maxZ) maxZ = r.y;
            }

            int tDst = 0;
            for (int j = 0; j < m - 1; j++)
            {
                int i0 = j * 2;
                int i1 = i0 + 1;
                int i2 = i0 + 2;
                int i3 = i0 + 3;
                pTris[tDst++] = i0;
                pTris[tDst++] = i2;
                pTris[tDst++] = i3;
                pTris[tDst++] = i0;
                pTris[tDst++] = i3;
                pTris[tDst++] = i1;
            }

            if (portraitRestMesh == null)
            {
                portraitRestMesh = new Mesh
                {
                    name = $"GCF_PortraitSoftWhip_{wDef.whipId ?? "whip"}"
                };
            }
            portraitRestMesh.Clear();
            portraitRestMesh.vertices = pVerts;
            portraitRestMesh.uv = pUvs;
            portraitRestMesh.normals = pNorms;
            portraitRestMesh.SetTriangles(pTris, 0, calculateBounds: false);
            portraitRestMesh.bounds = new Bounds(
                new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f),
                new Vector3((maxX - minX) + 3.0f, 3.0f, (maxZ - minZ) + 3.0f));
            hasValidPortraitMesh = true;
        }

        /// <summary>
        /// Rebuilds the dynamic quad-strip mesh strictly on the Unity main thread from a physics snapshot.
        /// ParallelPreDraw worker threads only read the already-uploaded dynamicMesh reference.
        /// </summary>
        public void RebuildMeshOnMainThread(SoftWhipRibbonSnapshot snap)
        {
            if (!UnityData.IsInMainThread)
            {
                return;
            }

            if (snap == null || !snap.visible || snap.stripLeftLocalXZ == null || snap.stripLeftLocalXZ.Length < 2)
            {
                hasValidMesh = false;
                lastUploadedFrame = -1;
                return;
            }

            EnsurePortraitRestMeshOnMainThread();

            int frame = Time.frameCount;
            if (hasValidMesh && dynamicMesh != null && frame == lastUploadedFrame)
            {
                return;
            }
            lastUploadedFrame = frame;
            CompGiantRig comp = RigComp;
            if (comp != null)
            {
                lastBakedHeadingDeg = comp.LatestSnapshot != null ? comp.LatestSnapshot.headingDeg : comp.CurrentHeadingDeg;
            }

            int m = snap.stripLeftLocalXZ.Length;
            int vCount = m * 2;
            bool topologyChanged = configuredSampleCount != m || vertArray == null || vertArray.Length != vCount;
            if (topologyChanged)
            {
                configuredSampleCount = m;
                vertArray = new Vector3[vCount];
                uvArray = new Vector2[vCount];
                normArray = new Vector3[vCount];
                triArray = new int[(m - 1) * 6];

                for (int j = 0; j < m; j++)
                {
                    float v = snap.vCoords[j];
                    int dst = j * 2;
                    uvArray[dst] = new Vector2(0f, v);
                    uvArray[dst + 1] = new Vector2(1f, v);
                    normArray[dst] = Vector3.up;
                    normArray[dst + 1] = Vector3.up;
                }

                int tDst = 0;
                for (int j = 0; j < m - 1; j++)
                {
                    int i0 = j * 2;
                    int i1 = i0 + 1;
                    int i2 = i0 + 2;
                    int i3 = i0 + 3;

                    triArray[tDst++] = i0;
                    triArray[tDst++] = i2;
                    triArray[tDst++] = i3;
                    triArray[tDst++] = i0;
                    triArray[tDst++] = i3;
                    triArray[tDst++] = i1;
                }
            }

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            Vector2[] leftArr = snap.stripLeftLocalXZ;
            Vector2[] rightArr = snap.stripRightLocalXZ;

            for (int j = 0; j < m; j++)
            {
                Vector2 l = leftArr[j];
                Vector2 r = rightArr[j];
                int dst = j * 2;
                vertArray[dst] = new Vector3(l.x, 0f, l.y);
                vertArray[dst + 1] = new Vector3(r.x, 0f, r.y);

                if (l.x < minX) minX = l.x;
                if (l.x > maxX) maxX = l.x;
                if (r.x < minX) minX = r.x;
                if (r.x > maxX) maxX = r.x;
                if (l.y < minZ) minZ = l.y;
                if (l.y > maxZ) maxZ = l.y;
                if (r.y < minZ) minZ = r.y;
                if (r.y > maxZ) maxZ = r.y;
            }

            if (dynamicMesh == null)
            {
                dynamicMesh = new Mesh
                {
                    name = $"GCF_SoftWhip_{WhipDef?.whipId ?? "whip"}"
                };
                dynamicMesh.MarkDynamic();
                topologyChanged = true;
            }

            if (topologyChanged)
            {
                dynamicMesh.Clear();
                dynamicMesh.vertices = vertArray;
                dynamicMesh.uv = uvArray;
                dynamicMesh.normals = normArray;
                dynamicMesh.SetTriangles(triArray, 0, calculateBounds: false);
            }
            else
            {
                dynamicMesh.vertices = vertArray;
            }

            Vector3 center = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
            Vector3 size = new Vector3((maxX - minX) + 3.0f, 3.0f, (maxZ - minZ) + 3.0f);
            dynamicMesh.bounds = new Bounds(center, size);
            hasValidMesh = true;
        }
    }

    public class PawnRenderNodeWorker_GiantSoftWhip : PawnRenderNodeWorker
    {
        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantSoftWhip whipNode)
            {
                CompGiantRig comp = whipNode.RigComp;
                SoftWhipDef wDef = whipNode.WhipDef;
                if (comp == null || wDef == null || string.IsNullOrEmpty(wDef.texPath))
                {
                    return false;
                }
                if (!whipNode.HasValidMesh || !comp.IsSoftWhipVisibleThreadSafe(wDef.runtimeIndex))
                {
                    return false;
                }
                return node.DebugEnabled;
            }
            return base.CanDrawNow(node, parms);
        }

        protected override Material GetMaterial(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantSoftWhip whipNode && whipNode.WhipDef != null)
            {
                Graphic g = GetGraphic(node, parms);
                Material mat = g?.MatSingle ?? GiantMaterialCache.Resolve(whipNode.WhipDef.texPath, Color.white);
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
            if (mpb != null && !parms.Portrait && node is PawnRenderNode_GiantSoftWhip whipNode && whipNode.RigComp != null && whipNode.WhipDef != null)
            {
                Pawn ownerPawn = whipNode.RigComp.Pawn ?? parms.pawn;
                if (ownerPawn != null && ownerPawn.Dead)
                {
                    bool isSolidDead = whipNode.WhipDef.tendrilBodyPart?.IsSolidInDefinition_Debug
                        ?? (ownerPawn.RaceProps?.IsMechanoid ?? false);
                    mpb.SetColor(ShaderPropertyIDs.Color, PawnRenderNodeWorker_GiantBone.ComputeDeadCorpseTint(parms.tint, isSolidDead));
                    return mpb;
                }
                float flash01 = whipNode.RigComp.GetSoftWhipDamageFlash01(whipNode.WhipDef.runtimeIndex);
                float hpPct = whipNode.RigComp.GetSoftWhipHealthPercentThreadSafe(whipNode.WhipDef.runtimeIndex);
                if ((hpPct > 0f && hpPct < 0.80f) || flash01 > 0.001f)
                {
                    bool isSolid = whipNode.WhipDef.tendrilBodyPart?.IsSolidInDefinition_Debug ?? false;
                    Color finalTint = PawnRenderNodeWorker_GiantBone.ComposeGiantPartTint(parms.tint, hpPct, isSolid, flash01);
                    mpb.SetColor(ShaderPropertyIDs.Color, finalTint);
                }
            }
            return mpb;
        }

        public override float LayerFor(PawnRenderNode node, PawnDrawParms parms)
        {
            if (node is PawnRenderNode_GiantSoftWhip whipNode && whipNode.WhipDef != null)
            {
                return node.Props.baseLayer + whipNode.WhipDef.layerOffset + node.debugLayerOffset;
            }
            return base.LayerFor(node, parms);
        }
    }
}
