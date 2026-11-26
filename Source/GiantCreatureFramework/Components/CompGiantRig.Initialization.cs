using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Runtime buffers, main-thread resource prewarming, and rig replacement.
    public partial class CompGiantRig
    {
        public void ReleaseAllDynamicRenderMeshes()
        {
            if (cachedRigSubtrees != null && cachedRigSubtrees.Count > 0)
            {
                foreach (CachedRigRenderSubtree subtree in cachedRigSubtrees.Values)
                {
                    if (subtree == null) continue;
                    if (subtree.SkinnedMeshPrimaryNodes != null)
                    {
                        for (int i = 0; i < subtree.SkinnedMeshPrimaryNodes.Count; i++)
                        {
                            subtree.SkinnedMeshPrimaryNodes[i]?.ReleaseDynamicMeshOnMainThread();
                        }
                    }
                    if (subtree.SoftWhipNodes != null)
                    {
                        for (int i = 0; i < subtree.SoftWhipNodes.Count; i++)
                        {
                            subtree.SoftWhipNodes[i]?.ReleaseDynamicMeshOnMainThread();
                        }
                    }
                    if (subtree.RollingTrackNodes != null)
                    {
                        for (int i = 0; i < subtree.RollingTrackNodes.Count; i++)
                        {
                            subtree.RollingTrackNodes[i]?.ReleaseDynamicMeshOnMainThread();
                        }
                    }
                }
            }
            if (createdSkinnedMeshPrimaryNodes != null)
            {
                for (int i = 0; i < createdSkinnedMeshPrimaryNodes.Count; i++)
                {
                    createdSkinnedMeshPrimaryNodes[i]?.ReleaseDynamicMeshOnMainThread();
                }
            }
            if (createdSoftWhipNodes != null)
            {
                for (int i = 0; i < createdSoftWhipNodes.Count; i++)
                {
                    createdSoftWhipNodes[i]?.ReleaseDynamicMeshOnMainThread();
                }
            }
            if (createdRollingTrackNodes != null)
            {
                for (int i = 0; i < createdRollingTrackNodes.Count; i++)
                {
                    createdRollingTrackNodes[i]?.ReleaseDynamicMeshOnMainThread();
                }
            }
        }

        private void EnsureBuffersInitialized()
        {
            if (RigDef == null)
            {
                return;
            }
            int count = RigDef.SortedNodes.Length;
            if (ReferenceEquals(buffersInitializedRigDef, RigDef) && evalPoses != null && evalPoses.Length == count && snapshotRing != null)
            {
                return;
            }

            alphaMasksLoaded = false;
            alphaMasksLoadedRigDef = null;
            worldPosesSolvedSerial = 0u;
            lastMissingMaskSnapshot = null;

            evalPoses = new BoneLocalPose[count];
            turnBlendPoses = new BoneLocalPose[count];
            finalLocalPoses = new BoneLocalPose[count];
            preSteeringLocalPoses = new BoneLocalPose[count];
            blendFromPoses = new BoneLocalPose[count];
            worldPoses = new BoneWorldPose[count];
            partMissingMask = new bool[count];
            partHealthPct = new float[count];
            boneLastDamageTick = new int[count];
            linkedBodyParts = new BodyPartRecord[count];
            alphaMasks = new TextureAlphaMask[count];
            alphaMasksEast = new TextureAlphaMask[count];
            alphaMasksWest = new TextureAlphaMask[count];
            activeAlphaMasks = new TextureAlphaMask[count];
            silhouetteAlphaMasks = new TextureAlphaMask[count];
            activeBoneSectors = new GiantBoneViewSector[count];
            sectorsInitialized = false;
            anySectorChangedPendingFootprint = false;
            ManualBoneAngleOffsets = new float[count];
            turretRecoilStates = new TurretRecoilState[count];
            currentTurretRecoilOffsets = new Vector2[count];
            anyTurretRecoilActive = false;

            for (int i = 0; i < count; i++)
            {
                evalPoses[i] = BoneLocalPose.Identity;
                turnBlendPoses[i] = BoneLocalPose.Identity;
                finalLocalPoses[i] = BoneLocalPose.Identity;
                preSteeringLocalPoses[i] = BoneLocalPose.Identity;
                blendFromPoses[i] = BoneLocalPose.Identity;
                partHealthPct[i] = 1f;
                boneLastDamageTick[i] = -9999;
            }

            if (RigDef.bezierChains != null && RigDef.bezierChains.Count > 0)
            {
                bezierRuntimes = new DampedBezierChainRuntime[RigDef.bezierChains.Count];
                for (int c = 0; c < RigDef.bezierChains.Count; c++)
                {
                    bezierRuntimes[c] = RigDef.bezierChains[c] != null ? new DampedBezierChainRuntime(RigDef.bezierChains[c]) : null;
                }
            }
            else
            {
                bezierRuntimes = Array.Empty<DampedBezierChainRuntime>();
            }

            if (RigDef.softWhips != null && RigDef.softWhips.Count > 0)
            {
                int whipCount = RigDef.softWhips.Count;
                softWhipRuntimes = new SoftWhipRuntime[whipCount];
                softWhipSnapshots = new SoftWhipRibbonSnapshot[whipCount];
                softWhipAlphaMasks = new TextureAlphaMask[whipCount];
                linkedSoftWhipTendrilParts = new BodyPartRecord[whipCount];
                softWhipTendrilMissingMask = new bool[whipCount];
                softWhipHealthPct = new float[whipCount];
                softWhipLastDamageTick = new int[whipCount];
                for (int s = 0; s < whipCount; s++)
                {
                    softWhipRuntimes[s] = RigDef.softWhips[s] != null ? new SoftWhipRuntime(RigDef.softWhips[s]) : null;
                    softWhipHealthPct[s] = 1f;
                    softWhipLastDamageTick[s] = -9999;
                }
            }
            else
            {
                softWhipRuntimes = Array.Empty<SoftWhipRuntime>();
                softWhipSnapshots = Array.Empty<SoftWhipRibbonSnapshot>();
                softWhipAlphaMasks = Array.Empty<TextureAlphaMask>();
                linkedSoftWhipTendrilParts = Array.Empty<BodyPartRecord>();
                softWhipTendrilMissingMask = Array.Empty<bool>();
                softWhipHealthPct = Array.Empty<float>();
                softWhipLastDamageTick = Array.Empty<int>();
            }

            if (RigDef.skinnedMeshes != null && RigDef.skinnedMeshes.Count > 0)
            {
                int smCount = RigDef.skinnedMeshes.Count;
                skinnedMeshRuntimes = new SkinnedMeshRuntime[smCount];
                skinnedMeshAlphaMasks = new TextureAlphaMask[smCount];
                linkedSkinnedMeshParts = new BodyPartRecord[smCount];
                skinnedMeshMissingMask = new bool[smCount];
                for (int m = 0; m < smCount; m++)
                {
                    skinnedMeshRuntimes[m] = RigDef.skinnedMeshes[m] != null
                        ? new SkinnedMeshRuntime(RigDef, RigDef.skinnedMeshes[m])
                        : null;
                }
            }
            else
            {
                skinnedMeshRuntimes = Array.Empty<SkinnedMeshRuntime>();
                skinnedMeshAlphaMasks = Array.Empty<TextureAlphaMask>();
                linkedSkinnedMeshParts = Array.Empty<BodyPartRecord>();
                skinnedMeshMissingMask = Array.Empty<bool>();
            }
            lastSkinnedMeshMissingBits = 0U;

            int trackCount = RigDef.rollingTracks?.Count ?? 0;
            trackScrollCells = trackCount > 0 ? new float[trackCount] : Array.Empty<float>();
            lastUploadedTrackScroll = trackCount > 0 ? new float[trackCount] : Array.Empty<float>();
            lastUploadedTrackSector = trackCount > 0 ? new GiantBoneViewSector[trackCount] : Array.Empty<GiantBoneViewSector>();
            for (int t = 0; t < trackCount; t++)
            {
                lastUploadedTrackScroll[t] = float.NaN;
            }
            trackKinematicsInitialized = false;
            pendingTrackWorldFwdDelta = 0f;

            int whipCountForRing = RigDef.softWhips?.Count ?? 0;
            snapshotRing = new GiantRenderSnapshot[SnapshotRingSize];
            snapshotRingCursor = 0;
            for (int r = 0; r < SnapshotRingSize; r++)
            {
                var ringWhips = whipCountForRing > 0 ? new SoftWhipRibbonSnapshot[whipCountForRing] : null;
                if (ringWhips != null)
                {
                    for (int s = 0; s < whipCountForRing; s++)
                    {
                        ringWhips[s] = new SoftWhipRibbonSnapshot();
                    }
                }
                snapshotRing[r] = new GiantRenderSnapshot
                {
                    localPoses = new BoneLocalPose[count],
                    turretRecoilOffsets = new Vector2[count],
                    missingMask = new bool[count],
                    partHealthPct = new float[count],
                    activeSectors = RigDef.hasAnyDirectionalVariants ? new GiantBoneViewSector[count] : null,
                    softWhips = ringWhips,
                    trackScrollCells = trackCount > 0 ? new float[trackCount] : null,
                    skinnedMeshMissingMask = (RigDef.skinnedMeshes?.Count ?? 0) > 0 ? new bool[RigDef.skinnedMeshes.Count] : null
                };
            }
            cachedBoneMaxHp = new float[count];
            cachedWhipMaxHp = whipCountForRing > 0 ? new float[whipCountForRing] : Array.Empty<float>();
            cachedMaxHpHealthScale = -1f;
            healthStateDirty = true;
            lastCheckedHediffCount = -1;
            lastHealthCheckTick = -999;
            lastWoundHediffSignature = ulong.MaxValue;
            cachedWoundDecalSpecs.Clear();

            steeringRuntime = new GiantSteeringRuntime(RigDef);
            if (headingInitialized)
            {
                steeringRuntime.Reset(currentHeadingDeg);
            }
            hitReactionRuntime = new GiantHitReactionRuntime(count);

            VehicleLocomotionDef vehDef = RigDef.ActiveVehicleLocomotion;
            if (vehDef != null)
            {
                if (vehicleLocomotion == null || !ReferenceEquals(vehicleLocomotion.Def, vehDef))
                {
                    vehicleLocomotion = new VehicleLocomotionRuntime(RigDef, vehDef);
                    if (headingInitialized)
                    {
                        Vector2 initXZ = Pawn != null && Pawn.Spawned
                            ? new Vector2(Pawn.Position.ToVector3Shifted().x, Pawn.Position.ToVector3Shifted().z)
                            : Vector2.zero;
                        vehicleLocomotion.Reset(currentHeadingDeg, initXZ, hasValidPos: Pawn != null && Pawn.Spawned);
                    }
                }
            }
            else
            {
                vehicleLocomotion = null;
            }

            GiantFlightDef fltDef = RigDef.ActiveFlightLocomotion;
            if (fltDef != null)
            {
                if (flightLocomotion == null || !ReferenceEquals(flightLocomotion.Def, fltDef))
                {
                    float prevHoverPhase = flightLocomotion != null ? flightLocomotion.HoverPhaseTicks : 0f;
                    float prevBankDeg = flightLocomotion != null ? flightLocomotion.CurrentBankAngleDeg : 0f;
                    bool hadPrev = flightLocomotion != null;
                    flightLocomotion = new GiantFlightRuntime(RigDef, fltDef);
                    if (Pawn != null)
                    {
                        flightLocomotion.ResetForSpawn(Pawn.thingIDNumber);
                    }
                    if (hadPrev)
                    {
                        flightLocomotion.RestoreVisualContinuity(prevHoverPhase, prevBankDeg);
                    }
                }
            }
            else
            {
                flightLocomotion = null;
            }

            FlightVehicleLocomotionDef fvehDef = RigDef.ActiveFlightVehicleLocomotion;
            if (fvehDef != null)
            {
                if (flightVehicleLocomotion == null || !ReferenceEquals(flightVehicleLocomotion.Def, fvehDef))
                {
                    flightVehicleLocomotion = new FlightVehicleLocomotionRuntime(RigDef, fvehDef);
                    if (headingInitialized)
                    {
                        Vector2 initXZ = Pawn != null && Pawn.Spawned
                            ? new Vector2(Pawn.Position.ToVector3Shifted().x, Pawn.Position.ToVector3Shifted().z)
                            : Vector2.zero;
                        bool startAir = flightLocomotion == null || flightLocomotion.IsCurrentlyAirborne;
                        flightVehicleLocomotion.Reset(currentHeadingDeg, initXZ, hasValidPos: Pawn != null && Pawn.Spawned, startAirborne: startAir);
                    }
                }
            }
            else
            {
                flightVehicleLocomotion = null;
            }

            GiantAquaticDef aqDef = RigDef.ActiveAquaticLocomotion;
            if (aqDef != null)
            {
                if (aquaticLocomotion == null || !ReferenceEquals(aquaticLocomotion.Def, aqDef))
                {
                    aquaticLocomotion = new GiantAquaticRuntime(RigDef, aqDef);
                    if (Pawn != null)
                    {
                        aquaticLocomotion.ResetForSpawn(Pawn);
                    }
                }
            }
            else
            {
                aquaticLocomotion = null;
            }

            int emitterCount = RigDef.weaponEmitters?.Count ?? 0;
            emitterBarrelCursors = emitterCount > 0 ? new int[emitterCount] : Array.Empty<int>();
            if (partCombat == null)
            {
                partCombat = new GiantPartCombatRuntime(this);
            }
            else
            {
                partCombat.RebuildFromRig(RigDef);
            }
            buffersInitializedRigDef = RigDef;
        }

        private void RefreshActiveAlphaMask(int boneIdx)
        {
            if (activeAlphaMasks == null || alphaMasks == null || (uint)boneIdx >= (uint)activeAlphaMasks.Length)
            {
                return;
            }
            GiantBoneViewSector sector = (activeBoneSectors != null && boneIdx < activeBoneSectors.Length)
                ? activeBoneSectors[boneIdx]
                : GiantBoneViewSector.Dorsal;
            TextureAlphaMask chosen = sector switch
            {
                GiantBoneViewSector.East => (alphaMasksEast != null ? alphaMasksEast[boneIdx] : null) ?? alphaMasks[boneIdx],
                GiantBoneViewSector.West => (alphaMasksWest != null ? alphaMasksWest[boneIdx] : null) ?? alphaMasks[boneIdx],
                _ => alphaMasks[boneIdx]
            };
            activeAlphaMasks[boneIdx] = chosen;
        }

        private void WarmTexturesAndMasks()
        {
            if (RigDef == null)
            {
                return;
            }
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef node = nodes[i];
                Shader nodeShader = node.shaderTypeDef?.Shader;
                if (!string.IsNullOrEmpty(node.texPath))
                {
                    GiantMaterialCache.WarmOnMainThread(node.texPath, Color.white, nodeShader);
                    alphaMasks[i] = GiantAlphaMaskCache.GetOrLoad(node.texPath, node.alphaThreshold);
                }

                string eastTex = node.GetTexPath(GiantBoneViewSector.East);
                if (!string.IsNullOrEmpty(eastTex) && !string.Equals(eastTex, node.texPath, StringComparison.Ordinal))
                {
                    GiantMaterialCache.WarmOnMainThread(eastTex, Color.white, nodeShader);
                    alphaMasksEast[i] = GiantAlphaMaskCache.GetOrLoad(eastTex, node.alphaThreshold);
                }
                else
                {
                    alphaMasksEast[i] = alphaMasks[i];
                }

                string westTex = node.GetTexPath(GiantBoneViewSector.West);
                if (!string.IsNullOrEmpty(westTex) && !string.Equals(westTex, node.texPath, StringComparison.Ordinal))
                {
                    GiantMaterialCache.WarmOnMainThread(westTex, Color.white, nodeShader);
                    alphaMasksWest[i] = string.Equals(westTex, eastTex, StringComparison.Ordinal)
                        ? alphaMasksEast[i]
                        : GiantAlphaMaskCache.GetOrLoad(westTex, node.alphaThreshold);
                }
                else
                {
                    alphaMasksWest[i] = alphaMasks[i];
                }

                if (silhouetteAlphaMasks != null && i < silhouetteAlphaMasks.Length
                    && silhouetteAlphaMasks[i] == null
                    && !string.IsNullOrEmpty(node.silhouetteTexPath))
                {
                    silhouetteAlphaMasks[i] = GiantAlphaMaskCache.GetOrLoad(node.silhouetteTexPath, node.alphaThreshold);
                }
                RefreshActiveAlphaMask(i);

                if (!string.IsNullOrEmpty(node.glowTexPath))
                {
                    GiantMaterialCache.WarmOnMainThread(node.glowTexPath, node.glowColor, ShaderDatabase.TransparentPostLight);
                }
                string eastGlow = node.GetGlowTexPath(GiantBoneViewSector.East);
                if (!string.IsNullOrEmpty(eastGlow) && !string.Equals(eastGlow, node.glowTexPath, StringComparison.Ordinal))
                {
                    GiantMaterialCache.WarmOnMainThread(eastGlow, node.glowColor, ShaderDatabase.TransparentPostLight);
                }
                string westGlow = node.GetGlowTexPath(GiantBoneViewSector.West);
                if (!string.IsNullOrEmpty(westGlow) && !string.Equals(westGlow, node.glowTexPath, StringComparison.Ordinal))
                {
                    GiantMaterialCache.WarmOnMainThread(westGlow, node.glowColor, ShaderDatabase.TransparentPostLight);
                }

                if (!string.IsNullOrEmpty(node.silhouetteTexPath))
                {
                    GiantMaterialCache.WarmOnMainThread(node.silhouetteTexPath, Color.white, ShaderDatabase.Cutout);
                }
                string eastSil = node.GetSilhouetteTexPath(GiantBoneViewSector.East);
                if (!string.IsNullOrEmpty(eastSil) && !string.Equals(eastSil, node.silhouetteTexPath, StringComparison.Ordinal))
                {
                    GiantMaterialCache.WarmOnMainThread(eastSil, Color.white, ShaderDatabase.Cutout);
                }
                string westSil = node.GetSilhouetteTexPath(GiantBoneViewSector.West);
                if (!string.IsNullOrEmpty(westSil) && !string.Equals(westSil, node.silhouetteTexPath, StringComparison.Ordinal))
                {
                    GiantMaterialCache.WarmOnMainThread(westSil, Color.white, ShaderDatabase.Cutout);
                }
            }
            if (RigDef.softWhips != null && softWhipAlphaMasks != null)
            {
                for (int s = 0; s < RigDef.softWhips.Count && s < softWhipAlphaMasks.Length; s++)
                {
                    SoftWhipDef whip = RigDef.softWhips[s];
                    if (whip != null && !string.IsNullOrEmpty(whip.texPath))
                    {
                        GiantMaterialCache.WarmOnMainThread(whip.texPath, Color.white);
                        softWhipAlphaMasks[s] = GiantAlphaMaskCache.GetOrLoad(whip.texPath, whip.alphaThreshold);
                    }
                }
            }
            if (RigDef.rollingTracks != null)
            {
                for (int t = 0; t < RigDef.rollingTracks.Count; t++)
                {
                    GiantRollingTrackDef track = RigDef.rollingTracks[t];
                    if (track == null)
                    {
                        continue;
                    }
                    WarmRepeatTrackTexture(track.dorsalRibbonTexPath);
                    WarmRepeatTrackTexture(track.lateralRibbonTexPath);
                    if (track.wheels != null)
                    {
                        for (int w = 0; w < track.wheels.Count; w++)
                        {
                            string wPath = track.wheels[w]?.texPath;
                            if (!string.IsNullOrEmpty(wPath))
                            {
                                GiantMaterialCache.WarmOnMainThread(wPath, Color.white);
                            }
                        }
                    }
                }
            }
            if (!string.IsNullOrEmpty(RigDef.shadowTexPath))
            {
                GiantMaterialCache.WarmOnMainThread(RigDef.shadowTexPath, Color.white, ShaderDatabase.Transparent);
            }
            if (RigDef.skinnedMeshes != null && skinnedMeshAlphaMasks != null)
            {
                for (int m = 0; m < RigDef.skinnedMeshes.Count && m < skinnedMeshAlphaMasks.Length; m++)
                {
                    GiantSkinnedMeshDef sm = RigDef.skinnedMeshes[m];
                    if (sm == null)
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(sm.texPath))
                    {
                        GiantMaterialCache.WarmOnMainThread(sm.texPath, Color.white, sm.shaderTypeDef?.Shader);
                        skinnedMeshAlphaMasks[m] = GiantAlphaMaskCache.GetOrLoad(sm.texPath, sm.alphaThreshold);
                    }
                    if (!string.IsNullOrEmpty(sm.silhouetteTexPath))
                    {
                        GiantMaterialCache.WarmOnMainThread(sm.silhouetteTexPath, Color.white, ShaderDatabase.Cutout);
                    }
                    if (!string.IsNullOrEmpty(sm.glowTexPath))
                    {
                        GiantMaterialCache.WarmOnMainThread(sm.glowTexPath, sm.glowColor, ShaderDatabase.TransparentPostLight);
                    }
                }
            }
            GiantWoundBloodSystem.PrewarmMaterialsOnMainThread();
        }

        private static void WarmRepeatTrackTexture(string texPath)
        {
            if (string.IsNullOrEmpty(texPath))
            {
                return;
            }
            Texture2D tex = ContentFinder<Texture2D>.Get(texPath, reportFailure: false);
            if (tex != null && tex.wrapMode != TextureWrapMode.Repeat)
            {
                tex.wrapMode = TextureWrapMode.Repeat;
            }
            GiantMaterialCache.WarmOnMainThread(texPath, Color.white);
        }

        private static void WarmSingleGraphicAndMaterialOnMainThread(string texPath, Color color, Shader shader)
        {
            if (string.IsNullOrEmpty(texPath) || !UnityData.IsInMainThread)
            {
                return;
            }
            Shader resolvedShader = shader != null ? shader : ShaderDatabase.Cutout;
            GiantMaterialCache.WarmOnMainThread(texPath, color, resolvedShader);
            GraphicDatabase.Get<Graphic_Single>(texPath, resolvedShader, Vector2.one, color);
        }

        public static void PrewarmRigMaterialsAndMasks(GiantRigDef rigDef)
        {
            if (rigDef == null)
            {
                return;
            }
            PrewarmRigAlphaMasks(rigDef);
            if (!UnityData.IsInMainThread)
            {
                return;
            }

            BoneNodeDef[] nodes = rigDef.SortedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef node = nodes[i];
                if (node == null)
                {
                    continue;
                }
                Shader nodeShader = node.shaderTypeDef?.Shader ?? ShaderDatabase.Cutout;
                WarmSingleGraphicAndMaterialOnMainThread(node.texPath, Color.white, nodeShader);
                string eastTex = node.GetTexPath(GiantBoneViewSector.East);
                if (!string.IsNullOrEmpty(eastTex) && !string.Equals(eastTex, node.texPath, StringComparison.Ordinal))
                {
                    WarmSingleGraphicAndMaterialOnMainThread(eastTex, Color.white, nodeShader);
                }
                string westTex = node.GetTexPath(GiantBoneViewSector.West);
                if (!string.IsNullOrEmpty(westTex)
                    && !string.Equals(westTex, node.texPath, StringComparison.Ordinal)
                    && !string.Equals(westTex, eastTex, StringComparison.Ordinal))
                {
                    WarmSingleGraphicAndMaterialOnMainThread(westTex, Color.white, nodeShader);
                }

                WarmSingleGraphicAndMaterialOnMainThread(node.glowTexPath, node.glowColor, ShaderDatabase.TransparentPostLight);
                string eastGlow = node.GetGlowTexPath(GiantBoneViewSector.East);
                if (!string.IsNullOrEmpty(eastGlow) && !string.Equals(eastGlow, node.glowTexPath, StringComparison.Ordinal))
                {
                    WarmSingleGraphicAndMaterialOnMainThread(eastGlow, node.glowColor, ShaderDatabase.TransparentPostLight);
                }
                string westGlow = node.GetGlowTexPath(GiantBoneViewSector.West);
                if (!string.IsNullOrEmpty(westGlow)
                    && !string.Equals(westGlow, node.glowTexPath, StringComparison.Ordinal)
                    && !string.Equals(westGlow, eastGlow, StringComparison.Ordinal))
                {
                    WarmSingleGraphicAndMaterialOnMainThread(westGlow, node.glowColor, ShaderDatabase.TransparentPostLight);
                }

                WarmSingleGraphicAndMaterialOnMainThread(node.silhouetteTexPath, Color.white, ShaderDatabase.Cutout);
                string eastSil = node.GetSilhouetteTexPath(GiantBoneViewSector.East);
                if (!string.IsNullOrEmpty(eastSil) && !string.Equals(eastSil, node.silhouetteTexPath, StringComparison.Ordinal))
                {
                    WarmSingleGraphicAndMaterialOnMainThread(eastSil, Color.white, ShaderDatabase.Cutout);
                }
                string westSil = node.GetSilhouetteTexPath(GiantBoneViewSector.West);
                if (!string.IsNullOrEmpty(westSil)
                    && !string.Equals(westSil, node.silhouetteTexPath, StringComparison.Ordinal)
                    && !string.Equals(westSil, eastSil, StringComparison.Ordinal))
                {
                    WarmSingleGraphicAndMaterialOnMainThread(westSil, Color.white, ShaderDatabase.Cutout);
                }
            }

            if (rigDef.softWhips != null)
            {
                for (int s = 0; s < rigDef.softWhips.Count; s++)
                {
                    SoftWhipDef whip = rigDef.softWhips[s];
                    if (whip != null && !string.IsNullOrEmpty(whip.texPath))
                    {
                        WarmSingleGraphicAndMaterialOnMainThread(whip.texPath, Color.white, ShaderDatabase.Cutout);
                    }
                }
            }

            if (rigDef.rollingTracks != null)
            {
                for (int t = 0; t < rigDef.rollingTracks.Count; t++)
                {
                    GiantRollingTrackDef track = rigDef.rollingTracks[t];
                    if (track == null)
                    {
                        continue;
                    }
                    WarmRepeatTrackTexture(track.dorsalRibbonTexPath);
                    WarmRepeatTrackTexture(track.lateralRibbonTexPath);
                    if (track.wheels != null)
                    {
                        for (int w = 0; w < track.wheels.Count; w++)
                        {
                            string wPath = track.wheels[w]?.texPath;
                            if (!string.IsNullOrEmpty(wPath))
                            {
                                WarmSingleGraphicAndMaterialOnMainThread(wPath, Color.white, ShaderDatabase.Cutout);
                            }
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(rigDef.shadowTexPath))
            {
                GiantMaterialCache.WarmOnMainThread(rigDef.shadowTexPath, Color.white, ShaderDatabase.Transparent);
            }

            if (rigDef.skinnedMeshes != null)
            {
                for (int m = 0; m < rigDef.skinnedMeshes.Count; m++)
                {
                    GiantSkinnedMeshDef sm = rigDef.skinnedMeshes[m];
                    if (sm == null)
                    {
                        continue;
                    }
                    WarmSingleGraphicAndMaterialOnMainThread(sm.texPath, Color.white, sm.shaderTypeDef?.Shader ?? ShaderDatabase.Cutout);
                    WarmSingleGraphicAndMaterialOnMainThread(sm.silhouetteTexPath, Color.white, ShaderDatabase.Cutout);
                    WarmSingleGraphicAndMaterialOnMainThread(sm.glowTexPath, sm.glowColor, ShaderDatabase.TransparentPostLight);
                }
            }
            GiantWoundBloodSystem.PrewarmMaterialsOnMainThread();
        }

        public static void PrewarmRigAlphaMasks(GiantRigDef rigDef)
        {
            if (rigDef == null)
            {
                return;
            }
            BoneNodeDef[] nodes = rigDef.SortedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef node = nodes[i];
                if (node == null)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(node.texPath))
                {
                    GiantAlphaMaskCache.GetOrLoad(node.texPath, node.alphaThreshold);
                }
                string eastTex = node.GetTexPath(GiantBoneViewSector.East);
                if (!string.IsNullOrEmpty(eastTex) && !string.Equals(eastTex, node.texPath, StringComparison.Ordinal))
                {
                    GiantAlphaMaskCache.GetOrLoad(eastTex, node.alphaThreshold);
                }
                string westTex = node.GetTexPath(GiantBoneViewSector.West);
                if (!string.IsNullOrEmpty(westTex)
                    && !string.Equals(westTex, node.texPath, StringComparison.Ordinal)
                    && !string.Equals(westTex, eastTex, StringComparison.Ordinal))
                {
                    GiantAlphaMaskCache.GetOrLoad(westTex, node.alphaThreshold);
                }
            }
            if (rigDef.softWhips != null)
            {
                for (int s = 0; s < rigDef.softWhips.Count; s++)
                {
                    SoftWhipDef whip = rigDef.softWhips[s];
                    if (whip != null && !string.IsNullOrEmpty(whip.texPath))
                    {
                        GiantAlphaMaskCache.GetOrLoad(whip.texPath, whip.alphaThreshold);
                    }
                }
            }
            if (rigDef.skinnedMeshes != null)
            {
                for (int m = 0; m < rigDef.skinnedMeshes.Count; m++)
                {
                    GiantSkinnedMeshDef sm = rigDef.skinnedMeshes[m];
                    if (sm != null && !string.IsNullOrEmpty(sm.texPath))
                    {
                        GiantAlphaMaskCache.GetOrLoad(sm.texPath, sm.alphaThreshold);
                    }
                }
            }
        }

        public void EnsureAlphaMasksLoaded()
        {
            EnsureBuffersInitialized();
            if (RigDef == null)
            {
                return;
            }
            if (alphaMasksLoaded && ReferenceEquals(alphaMasksLoadedRigDef, RigDef))
            {
                return;
            }
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef node = nodes[i];
                if (alphaMasks[i] == null && !string.IsNullOrEmpty(node.texPath))
                {
                    alphaMasks[i] = GiantAlphaMaskCache.GetOrLoad(node.texPath, node.alphaThreshold);
                }
                string eastTex = node.GetTexPath(GiantBoneViewSector.East);
                if (alphaMasksEast[i] == null && !string.IsNullOrEmpty(eastTex))
                {
                    alphaMasksEast[i] = string.Equals(eastTex, node.texPath, StringComparison.Ordinal)
                        ? alphaMasks[i]
                        : GiantAlphaMaskCache.GetOrLoad(eastTex, node.alphaThreshold);
                }
                string westTex = node.GetTexPath(GiantBoneViewSector.West);
                if (alphaMasksWest[i] == null && !string.IsNullOrEmpty(westTex))
                {
                    alphaMasksWest[i] = string.Equals(westTex, node.texPath, StringComparison.Ordinal)
                        ? alphaMasks[i]
                        : (string.Equals(westTex, eastTex, StringComparison.Ordinal)
                            ? alphaMasksEast[i]
                            : GiantAlphaMaskCache.GetOrLoad(westTex, node.alphaThreshold));
                }
                if (silhouetteAlphaMasks != null && i < silhouetteAlphaMasks.Length
                    && silhouetteAlphaMasks[i] == null
                    && !string.IsNullOrEmpty(node.silhouetteTexPath))
                {
                    silhouetteAlphaMasks[i] = GiantAlphaMaskCache.GetOrLoad(node.silhouetteTexPath, node.alphaThreshold);
                }
                RefreshActiveAlphaMask(i);
            }
            if (RigDef.softWhips != null && softWhipAlphaMasks != null)
            {
                for (int s = 0; s < RigDef.softWhips.Count && s < softWhipAlphaMasks.Length; s++)
                {
                    SoftWhipDef whip = RigDef.softWhips[s];
                    if (softWhipAlphaMasks[s] == null && whip != null && !string.IsNullOrEmpty(whip.texPath))
                    {
                        softWhipAlphaMasks[s] = GiantAlphaMaskCache.GetOrLoad(whip.texPath, whip.alphaThreshold);
                    }
                }
            }
            if (RigDef.skinnedMeshes != null && skinnedMeshAlphaMasks != null)
            {
                for (int m = 0; m < RigDef.skinnedMeshes.Count && m < skinnedMeshAlphaMasks.Length; m++)
                {
                    GiantSkinnedMeshDef sm = RigDef.skinnedMeshes[m];
                    if (skinnedMeshAlphaMasks[m] == null && sm != null && !string.IsNullOrEmpty(sm.texPath))
                    {
                        skinnedMeshAlphaMasks[m] = GiantAlphaMaskCache.GetOrLoad(sm.texPath, sm.alphaThreshold);
                    }
                }
            }
            alphaMasksLoaded = true;
            alphaMasksLoadedRigDef = RigDef;
        }

        public void SwitchRig(GiantRigDef newRig, GiantAnimDef initialMorphClip = null)
        {
            if (newRig == null)
            {
                return;
            }

            overrideRigDef = newRig;
            buffersInitializedRigDef = null;
            alphaMasksLoaded = false;
            alphaMasksLoadedRigDef = null;
            worldPosesSolvedSerial = 0u;
            evalPoses = null;
            turnBlendPoses = null;
            finalLocalPoses = null;
            preSteeringLocalPoses = null;
            blendFromPoses = null;
            worldPoses = null;
            partMissingMask = null;
            partHealthPct = null;
            linkedBodyParts = null;
            alphaMasks = null;
            alphaMasksEast = null;
            alphaMasksWest = null;
            activeAlphaMasks = null;
            silhouetteAlphaMasks = null;
            activeBoneSectors = null;
            sectorsInitialized = false;
            anySectorChangedPendingFootprint = false;
            emitterBarrelCursors = null;
            proceduralBoneAngleOffsets = null;
            turretRecoilStates = null;
            currentTurretRecoilOffsets = null;
            anyTurretRecoilActive = false;
            hitReactionRuntime = null;
            snapshotRing = null;
            cachedBoneMaxHp = null;
            cachedWhipMaxHp = null;
            healthStateDirty = true;
            lastCheckedHediffCount = -1;
            lastHealthCheckTick = -999;
            lastWoundHediffSignature = ulong.MaxValue;
            cachedWoundDecalSpecs.Clear();
            lastMissingMaskSnapshot = null;
            lastSoftWhipMissingBits = uint.MaxValue;
            lastHealthBucketHash = 0UL;
            deathTickZeroEventsEvaluated = false;
            currentClip = null;
            activeOneShotAnim = null;
            oneShotElapsedTicks = 0f;
            blendElapsedTicks = 0f;
            blendDurationTicks = 0f;

            EnsureBuffersInitialized();
            ResolveBodyPartLinks();
            if (UnityData.IsInMainThread)
            {
                WarmTexturesAndMasks();
            }

            if (bezierRuntimes != null)
            {
                for (int i = 0; i < bezierRuntimes.Length; i++)
                {
                    bezierRuntimes[i]?.Reset();
                }
            }
            if (softWhipRuntimes != null)
            {
                for (int i = 0; i < softWhipRuntimes.Length; i++)
                {
                    softWhipRuntimes[i]?.Reset();
                }
            }
            steeringRuntime?.Reset(currentHeadingDeg);
            trackKinematicsInitialized = false;
            pendingTrackWorldFwdDelta = 0f;

            if (initialMorphClip != null)
            {
                activeOneShotAnim = initialMorphClip;
                oneShotElapsedTicks = 0f;
                animState = GiantAnimState.MorphLocked;
                currentClip = initialMorphClip;
                blendElapsedTicks = 0f;
                blendDurationTicks = 0f;
            }
            else
            {
                animState = GiantAnimState.Idle;
                currentClip = newRig.idleAnim;
            }

            missingMaskInitialized = false;
            UpdateMissingPartsState(out _, force: true);
            missingMaskInitialized = true;
            EvaluateCurrentPoseAndPublish(0f);

            if (Pawn?.Drawer?.renderer != null)
            {
                if (!TrySwapRenderSubtreeInPlace(newRig) && UnityData.IsInMainThread)
                {
                    Pawn.Drawer.renderer.SetAllGraphicsDirty();
                }
            }

            if (Pawn != null && Pawn.Spawned)
            {
                RebuildFootprintNow(notifyMap: true);
                GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
            }
        }

    }
}
