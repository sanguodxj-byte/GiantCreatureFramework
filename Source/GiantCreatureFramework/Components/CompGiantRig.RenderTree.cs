using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Render subtree creation, stage caching, and in-place tree replacement.
    public partial class CompGiantRig
    {
        public void PrewarmStageRenderSubtree(GiantRigDef stageRig)
        {
            if (stageRig == null || Pawn == null)
            {
                return;
            }
            PawnRenderTree tree = Pawn.Drawer?.renderer?.renderTree;
            if (tree == null)
            {
                return;
            }
            GetOrCreateRenderSubtree(stageRig, tree);
        }

        private CachedRigRenderSubtree GetOrCreateRenderSubtree(GiantRigDef targetRig, PawnRenderTree tree)
        {
            cachedRigSubtrees ??= new Dictionary<GiantRigDef, CachedRigRenderSubtree>(4);
            if (cachedRigSubtrees.TryGetValue(targetRig, out CachedRigRenderSubtree cached) && ReferenceEquals(cached.Tree, tree))
            {
                if (UnityData.IsInMainThread && !cached.InitializedOnMainThread)
                {
                    PawnRenderNode[] arr = cached.ChildrenArray;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i]?.EnsureInitialized(PawnRenderFlags.None);
                    }
                    cached.InitializedOnMainThread = true;
                }
                return cached;
            }

            BoneNodeDef[] nodes = targetRig.SortedNodes;
            var result = new List<PawnRenderNode>(nodes.Length * 2);
            PawnRenderNode bodyTagNode = null;
            bool bodyTagAssigned = false;

            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef bDef = nodes[i];
                if (bDef.isRenderedBySkinnedMesh)
                {
                    if (!string.IsNullOrEmpty(bDef.glowTexPath))
                    {
                        var glowOnlyProps = new PawnRenderNodeProperties_GiantBone
                        {
                            boneDef = bDef,
                            rigComp = this,
                            isGlowOverlay = true,
                            texPath = bDef.glowTexPath,
                            debugLabel = $"GCF_Glow_{bDef.nodeId}",
                            baseLayer = 10f,
                            pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                            useGraphic = true
                        };
                        result.Add(new PawnRenderNode_GiantBone(Pawn, glowOnlyProps, tree));
                    }
                    continue;
                }
                string primaryTex = bDef.texPath ?? bDef.eastVariant?.texPath ?? bDef.westVariant?.texPath;
                if (string.IsNullOrEmpty(primaryTex))
                {
                    continue;
                }

                var nodeProps = new PawnRenderNodeProperties_GiantBone
                {
                    boneDef = bDef,
                    rigComp = this,
                    texPath = primaryTex,
                    shaderTypeDef = bDef.shaderTypeDef,
                    debugLabel = $"GCF_{bDef.nodeId}",
                    baseLayer = 10f,
                    pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                    useGraphic = true
                };

                // Tag the root visual bone as Body so PawnRenderTree.BodyGraphic is non-null for silhouettes/icons
                if (bDef.parentRuntimeIndex < 0 && !bodyTagAssigned && PawnRenderNodeTagDefOf.Body != null)
                {
                    nodeProps.tagDef = PawnRenderNodeTagDefOf.Body;
                    bodyTagAssigned = true;
                }

                var renderNode = new PawnRenderNode_GiantBone(Pawn, nodeProps, tree);
                if (nodeProps.tagDef != null && nodeProps.tagDef == PawnRenderNodeTagDefOf.Body)
                {
                    bodyTagNode = renderNode;
                }
                result.Add(renderNode);

                if (!string.IsNullOrEmpty(bDef.silhouetteTexPath))
                {
                    var silProps = new PawnRenderNodeProperties_GiantBone
                    {
                        boneDef = bDef,
                        rigComp = this,
                        isSilhouetteUnderlay = true,
                        texPath = bDef.silhouetteTexPath,
                        debugLabel = $"GCF_Sil_{bDef.nodeId}",
                        baseLayer = 10f,
                        pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                        useGraphic = true
                    };
                    result.Add(new PawnRenderNode_GiantBone(Pawn, silProps, tree));
                }

                if (!string.IsNullOrEmpty(bDef.glowTexPath))
                {
                    var glowProps = new PawnRenderNodeProperties_GiantBone
                    {
                        boneDef = bDef,
                        rigComp = this,
                        isGlowOverlay = true,
                        texPath = bDef.glowTexPath,
                        debugLabel = $"GCF_Glow_{bDef.nodeId}",
                        baseLayer = 10f,
                        pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                        useGraphic = true
                    };
                    var glowNode = new PawnRenderNode_GiantBone(Pawn, glowProps, tree);
                    result.Add(glowNode);
                }
            }

            var softWhipNodes = new List<PawnRenderNode_GiantSoftWhip>(targetRig.softWhips?.Count ?? 0);
            if (targetRig.softWhips != null)
            {
                for (int s = 0; s < targetRig.softWhips.Count; s++)
                {
                    SoftWhipDef whipDef = targetRig.softWhips[s];
                    if (whipDef == null || string.IsNullOrEmpty(whipDef.texPath))
                    {
                        softWhipNodes.Add(null);
                        continue;
                    }

                    var whipProps = new PawnRenderNodeProperties_GiantSoftWhip
                    {
                        whipDef = whipDef,
                        rigComp = this,
                        texPath = whipDef.texPath,
                        debugLabel = $"GCF_SoftWhip_{whipDef.whipId}",
                        baseLayer = 10f,
                        pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                        useGraphic = true
                    };
                    var whipNode = new PawnRenderNode_GiantSoftWhip(Pawn, whipProps, tree);
                    softWhipNodes.Add(whipNode);
                    result.Add(whipNode);
                }
            }

            var rollingTrackNodes = new List<PawnRenderNode_GiantRollingTrack>(targetRig.rollingTracks?.Count ?? 0);
            var trackWheelNodes = new List<PawnRenderNode_GiantTrackWheel>(12);
            if (targetRig.rollingTracks != null)
            {
                for (int t = 0; t < targetRig.rollingTracks.Count; t++)
                {
                    GiantRollingTrackDef trackDef = targetRig.rollingTracks[t];
                    if (trackDef == null || trackDef.resolvedParentBoneIndex < 0)
                    {
                        rollingTrackNodes.Add(null);
                        continue;
                    }

                    string beltPrimaryTex = !string.IsNullOrEmpty(trackDef.dorsalRibbonTexPath)
                        ? trackDef.dorsalRibbonTexPath
                        : trackDef.lateralRibbonTexPath;
                    if (!string.IsNullOrEmpty(beltPrimaryTex))
                    {
                        var beltProps = new PawnRenderNodeProperties_GiantRollingTrack
                        {
                            trackDef = trackDef,
                            rigComp = this,
                            texPath = beltPrimaryTex,
                            debugLabel = $"GCF_TrackBelt_{trackDef.trackId}",
                            baseLayer = 10f,
                            pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                            useGraphic = true
                        };
                        var beltNode = new PawnRenderNode_GiantRollingTrack(Pawn, beltProps, tree);
                        rollingTrackNodes.Add(beltNode);
                        result.Add(beltNode);
                    }
                    else
                    {
                        rollingTrackNodes.Add(null);
                    }

                    if (trackDef.wheels != null)
                    {
                        for (int w = 0; w < trackDef.wheels.Count; w++)
                        {
                            GiantTrackWheelDef wDef = trackDef.wheels[w];
                            if (wDef == null || string.IsNullOrEmpty(wDef.texPath))
                            {
                                continue;
                            }
                            var wheelProps = new PawnRenderNodeProperties_GiantTrackWheel
                            {
                                trackDef = trackDef,
                                wheelDef = wDef,
                                rigComp = this,
                                texPath = wDef.texPath,
                                debugLabel = $"GCF_TrackWheel_{trackDef.trackId}_{wDef.wheelId ?? w.ToString()}",
                                baseLayer = 10f,
                                pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                                useGraphic = true
                            };
                            var wheelNode = new PawnRenderNode_GiantTrackWheel(Pawn, wheelProps, tree);
                            trackWheelNodes.Add(wheelNode);
                            result.Add(wheelNode);
                        }
                    }
                }
            }

            var skinnedPrimaryNodes = new List<PawnRenderNode_GiantSkinnedMesh>(targetRig.skinnedMeshes?.Count ?? 0);
            var allSkinnedNodes = new List<PawnRenderNode_GiantSkinnedMesh>((targetRig.skinnedMeshes?.Count ?? 0) * 3);
            var allSkinnedIndices = new List<int>((targetRig.skinnedMeshes?.Count ?? 0) * 3);
            if (targetRig.skinnedMeshes != null)
            {
                bool isCurrentRig = ReferenceEquals(targetRig, RigDef);
                for (int m = 0; m < targetRig.skinnedMeshes.Count; m++)
                {
                    GiantSkinnedMeshDef smDef = targetRig.skinnedMeshes[m];
                    SkinnedMeshRuntime smRt = isCurrentRig && skinnedMeshRuntimes != null && m < skinnedMeshRuntimes.Length
                        ? skinnedMeshRuntimes[m]
                        : null;
                    if (smDef == null || string.IsNullOrEmpty(smDef.texPath))
                    {
                        skinnedPrimaryNodes.Add(null);
                        continue;
                    }

                    var mainProps = new PawnRenderNodeProperties_GiantSkinnedMesh
                    {
                        skinnedMeshDef = smDef,
                        runtime = smRt,
                        rigComp = this,
                        texPath = smDef.texPath,
                        shaderTypeDef = smDef.shaderTypeDef,
                        debugLabel = $"GCF_SkinnedMesh_{smDef.meshId}",
                        baseLayer = 10f,
                        pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                        useGraphic = true
                    };
                    if (!bodyTagAssigned && PawnRenderNodeTagDefOf.Body != null && (smDef.resolvedAnchorBoneIndex == 0 || Array.IndexOf(smDef.resolvedMemberBoneIndices, 0) >= 0))
                    {
                        mainProps.tagDef = PawnRenderNodeTagDefOf.Body;
                        bodyTagAssigned = true;
                    }
                    var mainNode = new PawnRenderNode_GiantSkinnedMesh(Pawn, mainProps, tree);
                    if (mainProps.tagDef != null && mainProps.tagDef == PawnRenderNodeTagDefOf.Body)
                    {
                        bodyTagNode = mainNode;
                    }
                    skinnedPrimaryNodes.Add(mainNode);
                    allSkinnedNodes.Add(mainNode);
                    allSkinnedIndices.Add(m);
                    result.Add(mainNode);

                    if (!string.IsNullOrEmpty(smDef.silhouetteTexPath))
                    {
                        var silProps = new PawnRenderNodeProperties_GiantSkinnedMesh
                        {
                            skinnedMeshDef = smDef,
                            runtime = smRt,
                            rigComp = this,
                            isSilhouetteUnderlay = true,
                            texPath = smDef.silhouetteTexPath,
                            debugLabel = $"GCF_SkinnedSil_{smDef.meshId}",
                            baseLayer = 10f,
                            pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                            useGraphic = true
                        };
                        var silNode = new PawnRenderNode_GiantSkinnedMesh(Pawn, silProps, tree)
                        {
                            PrimaryMeshOwner = mainNode
                        };
                        mainNode.SilhouetteChildNode = silNode;
                        allSkinnedNodes.Add(silNode);
                        allSkinnedIndices.Add(m);
                        result.Add(silNode);
                    }

                    if (!string.IsNullOrEmpty(smDef.glowTexPath))
                    {
                        var glowProps = new PawnRenderNodeProperties_GiantSkinnedMesh
                        {
                            skinnedMeshDef = smDef,
                            runtime = smRt,
                            rigComp = this,
                            isGlowOverlay = true,
                            texPath = smDef.glowTexPath,
                            debugLabel = $"GCF_SkinnedGlow_{smDef.meshId}",
                            baseLayer = 10f,
                            pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                            useGraphic = true
                        };
                        var glowNode = new PawnRenderNode_GiantSkinnedMesh(Pawn, glowProps, tree)
                        {
                            PrimaryMeshOwner = mainNode
                        };
                        mainNode.GlowChildNode = glowNode;
                        allSkinnedNodes.Add(glowNode);
                        allSkinnedIndices.Add(m);
                        result.Add(glowNode);
                    }
                }
            }

            var woundOverlayProps = new PawnRenderNodeProperties_GiantWoundOverlay
            {
                rigComp = this,
                debugLabel = "GCF_WoundBloodOverlay"
            };
            result.Add(new PawnRenderNode_GiantWoundOverlay(Pawn, woundOverlayProps, tree));

            PawnRenderNode[] childrenArray = result.ToArray();
            var ancestorChains = new List<PawnRenderNode>[childrenArray.Length];
            for (int i = 0; i < childrenArray.Length; i++)
            {
                ancestorChains[i] = new List<PawnRenderNode>(2);
            }

            var built = new CachedRigRenderSubtree
            {
                Tree = tree,
                NodesList = result,
                ChildrenArray = childrenArray,
                AncestorChains = ancestorChains,
                BodyTagNode = bodyTagNode,
                SoftWhipNodes = softWhipNodes,
                RollingTrackNodes = rollingTrackNodes,
                TrackWheelNodes = trackWheelNodes,
                SkinnedMeshPrimaryNodes = skinnedPrimaryNodes,
                AllSkinnedMeshNodes = allSkinnedNodes,
                AllSkinnedMeshIndices = allSkinnedIndices,
                InitializedOnMainThread = false
            };

            if (UnityData.IsInMainThread)
            {
                for (int i = 0; i < childrenArray.Length; i++)
                {
                    childrenArray[i]?.EnsureInitialized(PawnRenderFlags.None);
                }
                built.InitializedOnMainThread = true;
            }

            cachedRigSubtrees[targetRig] = built;
            return built;
        }

        private void ActivateRenderSubtreeLists(CachedRigRenderSubtree subtree)
        {
            if (subtree.AllSkinnedMeshNodes != null && subtree.AllSkinnedMeshNodes.Count > 0)
            {
                for (int i = 0; i < subtree.AllSkinnedMeshNodes.Count; i++)
                {
                    PawnRenderNode_GiantSkinnedMesh smNode = subtree.AllSkinnedMeshNodes[i];
                    int mIdx = subtree.AllSkinnedMeshIndices[i];
                    if (smNode?.Props is PawnRenderNodeProperties_GiantSkinnedMesh smProps)
                    {
                        smProps.runtime = (skinnedMeshRuntimes != null && mIdx < skinnedMeshRuntimes.Length)
                            ? skinnedMeshRuntimes[mIdx]
                            : null;
                    }
                }
            }

            createdRenderNodes = subtree.NodesList;
            createdSoftWhipNodes = subtree.SoftWhipNodes;
            createdRollingTrackNodes = subtree.RollingTrackNodes;
            createdTrackWheelNodes = subtree.TrackWheelNodes;
            createdSkinnedMeshPrimaryNodes = subtree.SkinnedMeshPrimaryNodes;
        }

        private bool TrySwapRenderSubtreeInPlace(GiantRigDef targetRig)
        {
            PawnRenderTree tree = Pawn?.Drawer?.renderer?.renderTree;
            if (tree == null || !tree.Resolved || !(tree.rootNode is PawnRenderNode_GiantRoot rootNode))
            {
                return false;
            }

            CachedRigRenderSubtree subtree = GetOrCreateRenderSubtree(targetRig, tree);
            ActivateRenderSubtreeLists(subtree);

            PawnRenderNode[] children = subtree.ChildrenArray;
            var nodeAncestors = RenderTreeNodeAncestorsField?.GetValue(tree) as Dictionary<PawnRenderNode, List<PawnRenderNode>>;
            for (int i = 0; i < children.Length; i++)
            {
                PawnRenderNode child = children[i];
                if (child == null)
                {
                    continue;
                }
                child.parent = rootNode;
                if (nodeAncestors != null)
                {
                    List<PawnRenderNode> chain = subtree.AncestorChains[i];
                    chain.Clear();
                    chain.Add(rootNode);
                    chain.Add(child);
                    nodeAncestors[child] = chain;
                }
            }

            rootNode.children = children;
            if (subtree.BodyTagNode != null && PawnRenderNodeTagDefOf.Body != null)
            {
                tree.SetTagNode(PawnRenderNodeTagDefOf.Body, subtree.BodyTagNode);
            }
            rootNode.requestRecache = true;
            pendingDynamicMeshUpload = true;
            InPlaceSubtreeSwapCount++;

            if (UnityData.IsInMainThread && Pawn.RaceProps?.Humanlike == true)
            {
                PortraitsCache.SetDirty(Pawn);
            }

            return true;
        }

        public override List<PawnRenderNode> CompRenderNodes()
        {
            if (RigDef == null || Pawn == null)
            {
                return null;
            }

            EnsureBuffersInitialized();
            ResolveBodyPartLinks();
            if (UnityData.IsInMainThread)
            {
                WarmTexturesAndMasks();
            }

            bool prevMissingInit = missingMaskInitialized;
            missingMaskInitialized = false;
            UpdateMissingPartsState(out bool missingChanged, force: true);
            missingMaskInitialized = prevMissingInit;

            PawnRenderTree tree = Pawn.Drawer?.renderer?.renderTree;
            CachedRigRenderSubtree subtree = GetOrCreateRenderSubtree(RigDef, tree);
            ActivateRenderSubtreeLists(subtree);

            if (latestSnapshot == null || missingChanged)
            {
                EvaluateCurrentPoseAndPublish(0f);
            }
            else if (UnityData.IsInMainThread)
            {
                pendingDynamicMeshUpload = true;
                FlushPendingDynamicMeshesOnMainThread(ignoreViewportCull: true);
            }
            return new List<PawnRenderNode>(subtree.NodesList);
        }

    }
}
