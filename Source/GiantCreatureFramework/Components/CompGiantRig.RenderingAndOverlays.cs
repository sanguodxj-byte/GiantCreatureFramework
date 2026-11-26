using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public struct CrewSeatWorldPose
    {
        public bool visible;
        public int boneIndex;
        public Vector2 worldXZ;
        public float worldY;
        public float worldFacingDeg;
        public Rot4 facingRot4;
        public float spriteAngleDeg;
        public float drawScale;
        public bool renderWeapon;
    }

    public partial class CompGiantRig
    {
        public void RebuildFootprintNow(bool notifyMap)
        {
            if (Pawn == null || RigDef == null)
            {
                return;
            }
            EnsureAlphaMasksLoaded();

            Vector3 drawPos = (Pawn.Spawned && Pawn.Drawer != null) ? GetSafeDrawPos(Pawn) : GetSimTickPos(Pawn);
            anySectorChangedPendingFootprint = false;
            SolveOrReuseWorldPoses(new Vector2(drawPos.x, drawPos.z));

            Map activeMap = Pawn.Map ?? Pawn.Corpse?.Map ?? lastSpawnedMap;
            IntVec3 pawnCell = GetEffectiveCell(Pawn);
            if (!pawnCell.IsValid)
            {
                pawnCell = IntVec3.FromVector3(drawPos);
            }
            IntVec3? mapSize = activeMap != null ? activeMap.Size : null;
            IntVec3 anchorCell = IntVec3.FromVector3(drawPos);
            if (activeMap != null && !anchorCell.InBounds(activeMap))
            {
                anchorCell = pawnCell;
            }
            footprint.Rebuild(RigDef, worldPoses, activeAlphaMasks ?? alphaMasks, pawnCell, mapSize, softWhipSnapshots, softWhipAlphaMasks, anchorCell);
            if (IsCurrentlyAirborne || IsFlyingEntry || GiantMotionOverrideRegistry.HasActiveHighAltitudeOverride(Pawn))
            {
                footprint.ClearCollisionVolume();
            }

            lastFootprintCell = pawnCell;
            lastFootprintWorldXZ = new Vector2(drawPos.x, drawPos.z);
            lastFootprintHeading = currentHeadingDeg;
            lastFootprintTick = Current.Game?.tickManager?.TicksGame ?? 0;
            lastFootprintSpineBend = steeringRuntime?.PrimarySpineBendDeg ?? 0f;
            lastFootprintSleepCurl = sleepCurlProgress01;

            if (notifyMap)
            {
                if (Pawn.Spawned && Pawn.Map != null)
                {
                    GiantFootprintMapComponent.GetFast(Pawn.Map)?.NotifyFootprintChanged(this);
                }
                else if (Pawn.Dead && activeMap != null)
                {
                    GiantFootprintMapComponent.GetFast(activeMap)?.RebuildCorpseSpatialGrid();
                }
            }
        }

        [ThreadStatic] private static CompGiantRig tsSolvedPoseComp;
        [ThreadStatic] private static GiantRigDef tsSolvedPoseRigDef;
        [ThreadStatic] private static GiantRenderSnapshot tsSolvedPoseSnap;
        [ThreadStatic] private static uint tsSolvedPoseSnapVersion;
        [ThreadStatic] private static float tsSolvedRootX;
        [ThreadStatic] private static float tsSolvedRootY;
        [ThreadStatic] private static float tsSolvedRootZ;
        [ThreadStatic] private static bool tsSolvedPortrait;
        [ThreadStatic] private static bool tsSolvedPawnDead;
        [ThreadStatic] private static BoneWorldPose[] tsSolvedWorldPoses;

        public static void ClearThreadStaticPoseCache()
        {
            tsSolvedPoseComp = null;
            tsSolvedPoseRigDef = null;
            tsSolvedPoseSnap = null;
            tsSolvedPoseSnapVersion = 0u;
            tsSolvedRootX = 0f;
            tsSolvedRootY = 0f;
            tsSolvedRootZ = 0f;
            tsSolvedPortrait = false;
            tsSolvedPawnDead = false;
            tsSolvedWorldPoses = null;
        }

        internal ReadOnlySpan<BodyPartRecord> LinkedBodyPartsSpan =>
            linkedBodyParts != null ? linkedBodyParts : ReadOnlySpan<BodyPartRecord>.Empty;

        internal BodyPartRecord[] LinkedBodyPartsRawArray => linkedBodyParts;

        internal ReadOnlySpan<bool> PartMissingMaskSpan =>
            partMissingMask != null ? partMissingMask : ReadOnlySpan<bool>.Empty;

        internal ReadOnlySpan<BoneWorldPose> FootprintBonePosesSpan =>
            footprint.bonePoses != null ? footprint.bonePoses : ReadOnlySpan<BoneWorldPose>.Empty;

        internal TextureAlphaMask GetSectorAlphaMask(int boneIndex, GiantBoneViewSector sector)
        {
            if (RigDef == null || (uint)boneIndex >= (uint)RigDef.SortedNodes.Length)
            {
                return null;
            }
            EnsureBuffersInitialized();
            if (!alphaMasksLoaded && UnityData.IsInMainThread)
            {
                WarmTexturesAndMasks();
                alphaMasksLoaded = true;
                alphaMasksLoadedRigDef = RigDef;
            }
            TextureAlphaMask dorsal = (alphaMasks != null && boneIndex < alphaMasks.Length) ? alphaMasks[boneIndex] : null;
            if (dorsal == null)
            {
                string dTex = RigDef.SortedNodes[boneIndex].texPath;
                if (!string.IsNullOrEmpty(dTex))
                {
                    dorsal = GiantAlphaMaskCache.GetOrLoad(dTex, RigDef.SortedNodes[boneIndex].alphaThreshold);
                    if (dorsal != null && alphaMasks != null && boneIndex < alphaMasks.Length)
                    {
                        alphaMasks[boneIndex] = dorsal;
                    }
                }
            }
            return sector switch
            {
                GiantBoneViewSector.East => (alphaMasksEast != null && boneIndex < alphaMasksEast.Length ? alphaMasksEast[boneIndex] : null) ?? dorsal,
                GiantBoneViewSector.West => (alphaMasksWest != null && boneIndex < alphaMasksWest.Length ? alphaMasksWest[boneIndex] : null) ?? dorsal,
                _ => dorsal
            };
        }

        internal BoneWorldPose[] SolveOrGetCachedPosesForOverlay(
            PawnDrawParms parms,
            out Vector4 rootCol,
            out GiantRenderSnapshot snap)
        {
            return GetOrSolveThreadCachedWorldPoses(parms, out rootCol, out snap);
        }

        internal float ResolveOverlayWorldAltitude(
            PawnDrawParms parms,
            Vector4 rootCol,
            GiantRenderSnapshot snap,
            float partLayerOffset,
            out bool isDeadOrDowned)
        {
            return ResolvePartWorldAltitude(parms, rootCol, snap, partLayerOffset, out isDeadOrDowned);
        }

        public void InvalidateWoundDecalCache()
        {
            lastWoundHediffSignature = ulong.MaxValue;
        }

        public IReadOnlyList<GiantWoundDecalSpec> GetOrRefreshWoundDecalSpecs(bool forceRefresh = false)
        {
            if (Pawn == null || RigDef == null)
            {
                cachedWoundDecalSpecs.Clear();
                return cachedWoundDecalSpecs;
            }
            EnsureBuffersInitialized();
            ulong sig = GiantWoundBloodSystem.ComputeHediffWoundSignature(Pawn, RigDef);
            if (forceRefresh || sig != lastWoundHediffSignature)
            {
                lastWoundHediffSignature = sig;
                if (sig <= 1UL)
                {
                    cachedWoundDecalSpecs.Clear();
                }
                else
                {
                    GiantWoundBloodSystem.BuildDecalSpecsForGiant(Pawn, this, cachedWoundDecalSpecs);
                }
            }
            return cachedWoundDecalSpecs;
        }

        private BoneWorldPose[] GetOrSolveThreadCachedWorldPoses(
            PawnDrawParms parms,
            out Vector4 rootCol,
            out GiantRenderSnapshot snap)
        {
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            int count = nodes.Length;
            snap = latestSnapshot;
            uint snapVer = snap?.version ?? 0u;
            rootCol = parms.matrix.GetColumn(3);
            bool isDead = Pawn != null && Pawn.Dead;

            if (tsSolvedPoseComp == this
                && tsSolvedPoseRigDef == RigDef
                && ReferenceEquals(tsSolvedPoseSnap, snap)
                && tsSolvedPoseSnapVersion == snapVer
                && tsSolvedPortrait == parms.Portrait
                && tsSolvedPawnDead == isDead
                && tsSolvedRootX == rootCol.x
                && tsSolvedRootY == rootCol.y
                && tsSolvedRootZ == rootCol.z
                && tsSolvedWorldPoses != null
                && tsSolvedWorldPoses.Length >= count)
            {
                return tsSolvedWorldPoses;
            }

            if (tsSolvedWorldPoses == null || tsSolvedWorldPoses.Length < count)
            {
                tsSolvedWorldPoses = new BoneWorldPose[Math.Max(64, count)];
            }

            SolveSnapshotWorldPoses(parms, out rootCol, tsSolvedWorldPoses.AsSpan(0, count));

            tsSolvedPoseComp = this;
            tsSolvedPoseRigDef = RigDef;
            tsSolvedPoseSnap = snap;
            tsSolvedPoseSnapVersion = snapVer;
            tsSolvedPortrait = parms.Portrait;
            tsSolvedPawnDead = isDead;
            tsSolvedRootX = rootCol.x;
            tsSolvedRootY = rootCol.y;
            tsSolvedRootZ = rootCol.z;
            return tsSolvedWorldPoses;
        }

        private void SolveSnapshotWorldPoses(
            PawnDrawParms parms,
            out Vector4 rootCol,
            Span<BoneWorldPose> outPoses)
        {
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            GiantRenderSnapshot snap = latestSnapshot;
            ReadOnlySpan<BoneLocalPose> localSpan = snap != null ? snap.localPoses : ReadOnlySpan<BoneLocalPose>.Empty;
            ReadOnlySpan<bool> missingSpan = snap != null ? snap.missingMask : ReadOnlySpan<bool>.Empty;
            float heading = snap != null ? snap.headingDeg : 0f;

            rootCol = parms.matrix.GetColumn(3);
            Vector2 rootXZ = new Vector2(rootCol.x, rootCol.z);
            float scale = 1f;

            if (parms.Portrait)
            {
                heading = 0f;
                scale = RigDef.portraitScaleFactor;
                localSpan = ReadOnlySpan<BoneLocalPose>.Empty;
            }
            else if ((Pawn != null && Pawn.Dead) || (snap != null && (snap.isDead || snap.isDowned)))
            {
                heading = SkeletalKinematics.NormalizeAngle360(heading);
            }
            else if (snap != null && snap.isFlyingEntry)
            {
                rootXZ += snap.flightVisualOffsetXZ;
            }

            Span<GiantBoneViewSector> portraitDorsal = parms.Portrait
                ? (nodes.Length <= 64 ? stackalloc GiantBoneViewSector[nodes.Length] : new GiantBoneViewSector[nodes.Length])
                : Span<GiantBoneViewSector>.Empty;
            ReadOnlySpan<GiantBoneViewSector> sectorSpan = parms.Portrait
                ? (ReadOnlySpan<GiantBoneViewSector>)portraitDorsal
                : (snap?.activeSectors != null ? snap.activeSectors : ReadOnlySpan<GiantBoneViewSector>.Empty);

            SkeletalKinematics.SolveWorldPoses(
                RigDef,
                rootXZ,
                heading,
                localSpan,
                missingSpan,
                outPoses,
                scale,
                sectorSpan,
                -1,
                !parms.Portrait && snap?.turretRecoilOffsets != null
                    ? snap.turretRecoilOffsets
                    : ReadOnlySpan<Vector2>.Empty);
        }

        /// <summary>
        /// World-Y altitude step per unit of intra-giant layerOffset.
        /// Unclamped across [-40, +40] (total span <= 0.02f world-Y) so silhouette underlays (-12),
        /// hulls (0), dorsal railguns (+4.8), arched scorpion stingers (+16), and glow overlays (+0.05)
        /// preserve monotonic float32 separation without ever spilling across entity elevation bands.
        /// </summary>
        public const float IntraGiantLayerStepY = 0.00025f;

        public static float ComputeEntitySubSlotOffsetY(float worldZ, int thingIdNumber)
        {
            // 16 disjoint entity sub-slots spaced by 0.010f (> full [-14..+18] intra-giant span 0.0080f)
            // plus a gentle south-in-front Z bias within each slot so overlapping giants/corpses never Z-interleave bones.
            float zNorm = 1f - Mathf.Clamp01((worldZ + 16f) / 432f);
            float idSlot = (thingIdNumber & 15) * 0.010f;
            return idSlot + zNorm * 0.001f;
        }

        /// <summary>
        /// Base world-Y for standing alive grounded giants (Band 2).
        /// Placed above AltitudeLayer.MoteOverheadLow (9.878f) so living giants always render cleanly
        /// above terrain, plants, buildings, items, projectiles, all normal pawns (including modded
        /// headgear/apparel/oversized weapons), PawnState overlays, and blueprints, while staying strictly
        /// below AltitudeLayer.MoteOverhead (10.244f) so combat hit slashes/VFX, weather, lighting, and
        /// FogOfWar remain properly layered above grounded giants.
        /// </summary>
        public static float GroundAliveBaseAltitudeY => Altitudes.AltitudeFor(AltitudeLayer.MoteOverheadLow) + 0.02f;

        /// <summary>
        /// Base world-Y for LowSkim airborne giants (Band 3).
        /// Placed strictly above AltitudeLayer.FogOfWar (12.439f) so flying/skimming giants render
        /// above FogOfWar, Weather, LightingOverlay, and ground layers, while staying below Darkness
        /// (12.805f) and HighAirborne giants (Band 4).
        /// </summary>
        public static float LowSkimAirborneBaseAltitudeY => Altitudes.AltitudeFor(AltitudeLayer.FogOfWar) + 0.06f;

        /// <summary>
        /// Base world-Y for HighAirborne and FlightEntry giants (Band 4).
        /// Placed strictly above AltitudeLayer.FogOfWar (12.439f) and AltitudeLayer.Darkness (12.805f)
        /// so high-flying giants soar cleanly above fog of war and unnatural darkness, while staying
        /// strictly below WorldClipper (13.171f), Silhouettes (13.537f), MapDataOverlay (13.902f),
        /// MetaOverlays (14.268f), and Camera near-clip (14.500f).
        /// </summary>
        public static float HighAirborneBaseAltitudeY => Altitudes.AltitudeFor(AltitudeLayer.Darkness) + 0.04f;

        /// <summary>
        /// Base world-Y for airborne giant VFX, breath cones, fireballs, projectiles, and carrier interceptors
        /// so they render cleanly above HighAirborne giants and mounted riders, yet below WorldClipper/UI.
        /// </summary>
        public static float AirborneVfxBaseAltitudeY => Altitudes.AltitudeFor(AltitudeLayer.Darkness) + 0.24f;

        /// <summary>
        /// Computes strictly stratified entity-level base world-Y across 5 physical elevation bands:
        /// Band 0 (Dead Corpse) &lt; Band 1 (Downed Ground) &lt; Normal Pawns &lt; Band 2 (Standing Ground Alive)
        /// &lt; MoteOverhead / Weather / FogOfWar &lt; Band 3 (LowSkim Airborne) &lt; Darkness &lt; Band 4 (HighAirborne / FlightEntry) &lt; WorldClipper.
        /// </summary>
        public static float ComputeGiantEntityBaseWorldY(
            bool isPortrait,
            bool isDead,
            bool isDowned,
            float rootColY,
            float worldZ,
            int thingIdNumber,
            bool isFlyingVisual,
            float flightAltitudeBoost,
            bool isWorldMap = false)
        {
            if (isPortrait || isWorldMap)
            {
                return rootColY;
            }

            float subSlotY = ComputeEntitySubSlotOffsetY(worldZ, thingIdNumber);

            if (isDead)
            {
                float shadowsSafeFloorY = Altitudes.AltitudeFor(AltitudeLayer.Shadows) + 0.045f;
                float corpseBaseY = Altitudes.AltitudeFor(AltitudeLayer.LayingPawn) - 0.14f;
                return Mathf.Max(shadowsSafeFloorY, corpseBaseY) + subSlotY;
            }

            if (isDowned)
            {
                float downedBaseY = Altitudes.AltitudeFor(AltitudeLayer.PawnRope) - 0.08f;
                return downedBaseY + subSlotY;
            }

            float groundBaseY = GroundAliveBaseAltitudeY;
            if (isFlyingVisual && flightAltitudeBoost > 1e-5f)
            {
                return Mathf.Max(groundBaseY + subSlotY, groundBaseY + flightAltitudeBoost + subSlotY * 0.45f);
            }

            return groundBaseY + subSlotY;
        }

        public static float ComputeGiantPartWorldAltitude(
            bool isPortrait,
            bool isDead,
            bool isDowned,
            float rootColY,
            float worldZ,
            int thingIdNumber,
            bool isFlyingVisual,
            float flightAltitudeBoost,
            float partLayerOffset,
            bool isWorldMap = false)
        {
            float baseY = ComputeGiantEntityBaseWorldY(
                isPortrait,
                isDead,
                isDowned,
                rootColY,
                worldZ,
                thingIdNumber,
                isFlyingVisual,
                flightAltitudeBoost,
                isWorldMap);
            return baseY + partLayerOffset * IntraGiantLayerStepY;
        }

        private float ResolvePartWorldAltitude(
            PawnDrawParms parms,
            Vector4 rootCol,
            GiantRenderSnapshot snap,
            float partLayerOffset,
            out bool isDeadOrDowned)
        {
            Pawn ownerPawn = Pawn ?? parms.pawn;
            bool isDead = (ownerPawn != null && ownerPawn.Dead) || (snap != null && snap.isDead);
            bool isDowned = !isDead && ((ownerPawn != null && ownerPawn.Downed) || (snap != null && snap.isDowned));
            isDeadOrDowned = isDead || isDowned;
            int thingId = ownerPawn != null ? ownerPawn.thingIDNumber : 0;
            bool isWorldMap = worldSimulationActive && !parms.Portrait;
            bool flyingVis = !parms.Portrait && !isWorldMap && !isDeadOrDowned && snap != null && snap.isFlyingEntry;
            float flightBoost = flyingVis ? snap.flightAltitudeBoost : 0f;
            return ComputeGiantPartWorldAltitude(
                parms.Portrait,
                isDead,
                isDowned,
                rootCol.y,
                rootCol.z,
                thingId,
                flyingVis,
                flightBoost,
                partLayerOffset,
                isWorldMap);
        }

        /// <summary>
        /// Thread-safe matrix computation invoked from PawnRenderTree.TryGetMatrix on Job worker threads or main thread.
        /// </summary>
        public bool TryComputeBoneRenderMatrix(PawnRenderNode_GiantBone boneNode, PawnDrawParms parms, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (RigDef == null || boneNode?.BoneDef == null)
            {
                return false;
            }

            int idx = boneNode.BoneDef.runtimeIndex;
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            if ((uint)idx >= (uint)nodes.Length)
            {
                return false;
            }

            BoneWorldPose[] solvedPoses = GetOrSolveThreadCachedWorldPoses(parms, out Vector4 rootCol, out GiantRenderSnapshot snap);
            ref readonly BoneWorldPose pose = ref solvedPoses[idx];
            float baseBoneLayer = boneNode.BoneDef.GetLayerOffset(pose.activeSector);
            float partLayer = (boneNode.IsSilhouetteUnderlay
                ? boneNode.BoneDef.silhouetteLayerOffset
                : (boneNode.IsGlowOverlay ? (baseBoneLayer + boneNode.BoneDef.glowLayerOffset) : baseBoneLayer)) + boneNode.debugLayerOffset;
            float altitude = ResolvePartWorldAltitude(parms, rootCol, snap, partLayer, out _);
            matrix = pose.BuildRenderMatrix(altitude);
            return true;
        }

        /// <summary>
        /// Thread-safe matrix computation invoked from PawnRenderTree.TryGetMatrix for scrolling caterpillar track belt overlays.
        /// </summary>
        public bool TryComputeRollingTrackRenderMatrix(PawnRenderNode_GiantRollingTrack trackNode, PawnDrawParms parms, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (parms.Portrait || RigDef == null || trackNode?.TrackDef == null || !trackNode.HasValidMesh)
            {
                return false;
            }

            int pIdx = trackNode.TrackDef.resolvedParentBoneIndex;
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            if ((uint)pIdx >= (uint)nodes.Length)
            {
                return false;
            }

            BoneWorldPose[] solvedPoses = GetOrSolveThreadCachedWorldPoses(parms, out Vector4 rootCol, out GiantRenderSnapshot snap);
            ref readonly BoneWorldPose pose = ref solvedPoses[pIdx];
            if (!pose.visible)
            {
                return false;
            }

            float partLayer = trackNode.TrackDef.GetBeltLayerOffset(pose.activeSector) + trackNode.debugLayerOffset;
            float altitude = ResolvePartWorldAltitude(parms, rootCol, snap, partLayer, out _);
            matrix = Matrix4x4.TRS(
                new Vector3(pose.quadCenterWorldXZ.x, altitude, pose.quadCenterWorldXZ.y),
                Quaternion.AngleAxis(pose.worldAngleDeg, Vector3.up),
                Vector3.one);
            return true;
        }

        /// <summary>
        /// Thread-safe matrix computation invoked from PawnRenderTree.TryGetMatrix for near-side rotating track wheels.
        /// </summary>
        public bool TryComputeTrackWheelRenderMatrix(PawnRenderNode_GiantTrackWheel wheelNode, PawnDrawParms parms, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            GiantRollingTrackDef tDef = wheelNode?.TrackDef;
            GiantTrackWheelDef wDef = wheelNode?.WheelDef;
            if (parms.Portrait || RigDef == null || tDef == null || wDef == null)
            {
                return false;
            }

            int pIdx = tDef.resolvedParentBoneIndex;
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            if ((uint)pIdx >= (uint)nodes.Length)
            {
                return false;
            }

            BoneWorldPose[] solvedPoses = GetOrSolveThreadCachedWorldPoses(parms, out Vector4 rootCol, out GiantRenderSnapshot snap);
            ref readonly BoneWorldPose pose = ref solvedPoses[pIdx];
            if (!pose.visible || !tDef.IsNearSector(pose.activeSector))
            {
                return false;
            }

            float res = Mathf.Max(1f, nodes[pIdx].textureResolution);
            float px = pose.activeSector == GiantBoneViewSector.West
                ? (res - 1f - wDef.centerPixel.x)
                : wDef.centerPixel.x;
            Vector2 worldXZ = pose.UVToWorldXZ(px / res, 1f - (wDef.centerPixel.y / res));

            float scrollCells = (snap?.trackScrollCells != null && (uint)tDef.runtimeIndex < (uint)snap.trackScrollCells.Length)
                ? snap.trackScrollCells[tDef.runtimeIndex]
                : 0f;
            float spinDeg = GiantRollingTrackKinematics.ComputeWheelSpinAngleDeg(scrollCells, wDef.diameterCells, pose.activeSector);
            float worldAngle = SkeletalKinematics.NormalizeAngle360(pose.worldAngleDeg + spinDeg);
            float partLayer = wDef.layerOffset + wheelNode.debugLayerOffset;
            float altitude = ResolvePartWorldAltitude(parms, rootCol, snap, partLayer, out _);
            float diam = Mathf.Max(0.1f, wDef.diameterCells);

            matrix = Matrix4x4.TRS(
                new Vector3(worldXZ.x, altitude, worldXZ.y),
                Quaternion.AngleAxis(worldAngle, Vector3.up),
                new Vector3(diam, 1f, diam));
            return true;
        }

        /// <summary>
        /// Thread-safe matrix computation invoked from PawnRenderTree.TryGetMatrix for boneless soft-whip meshes.
        /// </summary>
        public bool TryComputeSoftWhipRenderMatrix(PawnRenderNode_GiantSoftWhip whipNode, PawnDrawParms parms, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (RigDef == null || whipNode?.WhipDef == null)
            {
                return false;
            }

            int idx = whipNode.WhipDef.runtimeIndex;
            if (RigDef.softWhips == null || (uint)idx >= (uint)RigDef.softWhips.Count)
            {
                return false;
            }

            GiantRenderSnapshot snap = latestSnapshot;
            if (snap?.softWhips == null || (uint)idx >= (uint)snap.softWhips.Length || snap.softWhips[idx] == null || !snap.softWhips[idx].visible)
            {
                return false;
            }

            Vector4 rootCol = parms.matrix.GetColumn(3);
            float partLayer = whipNode.WhipDef.layerOffset + whipNode.debugLayerOffset;
            float altitude = ResolvePartWorldAltitude(parms, rootCol, snap, partLayer, out bool isDeadOrDowned);
            Vector2 flightOff = (!parms.Portrait && !isDeadOrDowned && snap.isFlyingEntry) ? snap.flightVisualOffsetXZ : Vector2.zero;
            Vector3 pos = new Vector3(rootCol.x + flightOff.x, altitude, rootCol.z + flightOff.y);

            if (parms.Portrait)
            {
                float s = RigDef.portraitScaleFactor;
                Quaternion rot = whipNode.HasValidPortraitMesh
                    ? Quaternion.identity
                    : Quaternion.AngleAxis(-whipNode.LastBakedHeadingDeg, Vector3.up);
                matrix = Matrix4x4.TRS(pos, rot, new Vector3(s, 1f, s));
            }
            else
            {
                matrix = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one);
            }
            return true;
        }

        /// <summary>
        /// Thread-safe matrix computation invoked from PawnRenderTree.TryGetMatrix for multi-bone 2D skinned meshes.
        /// </summary>
        public bool TryComputeSkinnedMeshRenderMatrix(PawnRenderNode_GiantSkinnedMesh smNode, PawnDrawParms parms, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            if (RigDef == null || smNode?.SkinnedMeshDef == null || !smNode.HasValidMesh)
            {
                return false;
            }

            int idx = smNode.SkinnedMeshDef.runtimeIndex;
            if (RigDef.skinnedMeshes == null || (uint)idx >= (uint)RigDef.skinnedMeshes.Count || IsSkinnedMeshMissingThreadSafe(idx))
            {
                return false;
            }

            GiantRenderSnapshot snap = latestSnapshot;
            if (snap == null)
            {
                return false;
            }

            Vector4 rootCol = parms.matrix.GetColumn(3);
            GiantSkinnedMeshDef sDef = smNode.SkinnedMeshDef;
            float partLayer = (smNode.IsSilhouetteUnderlay
                ? sDef.silhouetteLayerOffset
                : (smNode.IsGlowOverlay ? (sDef.layerOffset + sDef.glowLayerOffset) : sDef.layerOffset)) + smNode.debugLayerOffset;
            float altitude = ResolvePartWorldAltitude(parms, rootCol, snap, partLayer, out bool isDeadOrDowned);
            Vector2 flightOff = (!parms.Portrait && !isDeadOrDowned && snap.isFlyingEntry) ? snap.flightVisualOffsetXZ : Vector2.zero;
            Vector3 pos = new Vector3(rootCol.x + flightOff.x, altitude, rootCol.z + flightOff.y);

            if (parms.Portrait)
            {
                float s = RigDef.portraitScaleFactor;
                Quaternion rot = smNode.HasValidPortraitMesh
                    ? Quaternion.identity
                    : Quaternion.AngleAxis(-smNode.LastBakedHeadingDeg, Vector3.up);
                matrix = Matrix4x4.TRS(pos, rot, new Vector3(s, 1f, s));
            }
            else
            {
                matrix = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one);
            }
            return true;
        }

        /// <summary>
        /// World-Y floor clearance added when GiantBoneCrewSeatDef.renderAboveAllParts is true so even
        /// a rider's lowest PawnRenderTree sub-node (-10f cape/pack = -0.00366f Y) and worst-case northern
        /// zSortTieBreak (-0.0030f Y) still clear the highest giant dorsal part cleanly.
        /// </summary>
        public const float CrewRenderTreeFloorClearanceY = 0.0085f;

        /// <summary>
        /// Pure deterministic evaluator mapping a BoneNodeDef &lt;crewSeats&gt; mount onto world (X, Y, Z) and facing
        /// from solved BoneWorldPoses. Shared by live rendering and headless kinematics tests.
        /// </summary>
        public static bool TryEvaluateCrewSeatWorldPoseFromPoses(
            GiantRigDef rigDef,
            string seatId,
            ReadOnlySpan<BoneWorldPose> solvedWorldPoses,
            Vector3 hostDrawPos,
            bool isDowned,
            int thingIdNumber,
            bool isFlyingVisual,
            Vector2 flightVisualOffsetXZ,
            float flightAltitudeBoost,
            out CrewSeatWorldPose result,
            bool posesIncludeFlightOffset = true)
        {
            result = default;
            if (rigDef == null || string.IsNullOrEmpty(seatId) ||
                !rigDef.TryGetCrewSeatMount(seatId, out int boneIdx, out GiantBoneCrewSeatDef seatMount) ||
                seatMount == null || !seatMount.renderOccupant)
            {
                return false;
            }

            if ((uint)boneIdx >= (uint)solvedWorldPoses.Length)
            {
                return false;
            }

            ref readonly BoneWorldPose pose = ref solvedWorldPoses[boneIdx];
            if (!pose.visible)
            {
                return false;
            }

            bool mirrorWest = pose.activeSector == GiantBoneViewSector.West;
            Vector2 uv = seatMount.resolvedMountUV;
            if (mirrorWest && (seatMount.HasCustomMountPixel || seatMount.HasCustomMountUV))
            {
                uv.x = 1f - uv.x;
            }

            float localOffX = mirrorWest ? -seatMount.localOffset.x : seatMount.localOffset.x;
            Vector2 worldXZ;
            if (pose.worldSize.x > 1e-5f && pose.worldSize.y > 1e-5f)
            {
                float u = uv.x + localOffX / pose.worldSize.x;
                float v = uv.y + seatMount.localOffset.y / pose.worldSize.y;
                worldXZ = pose.UVToWorldXZ(u, v);
            }
            else
            {
                float lx = localOffX;
                float lz = seatMount.localOffset.y;
                worldXZ = new Vector2(
                    pose.jointWorldXZ.x + lx * pose.cosTheta + lz * pose.sinTheta,
                    pose.jointWorldXZ.y - lx * pose.sinTheta + lz * pose.cosTheta);
            }

            worldXZ += seatMount.screenOffset;
            if (isFlyingVisual && !posesIncludeFlightOffset)
            {
                worldXZ += flightVisualOffsetXZ;
            }

            float worldFacingDeg = SkeletalKinematics.NormalizeAngle360(pose.worldAngleDeg + seatMount.facingOffsetDeg);
            Rot4 facingRot4 = Rot4.FromAngleFlat(worldFacingDeg);
            float spriteAngleDeg = seatMount.rotateSpriteWithBone ? worldFacingDeg : 0f;

            float effectiveLayerOffset = seatMount.renderAboveAllParts
                ? Mathf.Max(rigDef.maxAuthoredLayerOffset + 2.0f, seatMount.layerOffset)
                : seatMount.layerOffset;
            float partAltY = ComputeGiantPartWorldAltitude(
                isPortrait: false,
                isDead: false,
                isDowned: isDowned,
                rootColY: hostDrawPos.y,
                worldZ: hostDrawPos.z,
                thingIdNumber: thingIdNumber,
                isFlyingVisual: isFlyingVisual,
                flightAltitudeBoost: isFlyingVisual ? flightAltitudeBoost : 0f,
                partLayerOffset: effectiveLayerOffset);

            float centerZ = hostDrawPos.z + (isFlyingVisual ? flightVisualOffsetXZ.y : 0f);
            float zSortTieBreak = Mathf.Clamp((centerZ - worldXZ.y) * 0.00035f, -0.0030f, 0.0030f);
            float clearanceY = seatMount.renderAboveAllParts ? CrewRenderTreeFloorClearanceY : 0f;

            result = new CrewSeatWorldPose
            {
                visible = true,
                boneIndex = boneIdx,
                worldXZ = worldXZ,
                worldY = partAltY + clearanceY + zSortTieBreak,
                worldFacingDeg = worldFacingDeg,
                facingRot4 = facingRot4,
                spriteAngleDeg = spriteAngleDeg,
                drawScale = Mathf.Clamp(seatMount.drawScale, 0.1f, 4.0f),
                renderWeapon = seatMount.renderOccupantWeapon
            };
            return true;
        }

        /// <summary>
        /// Evaluates live world (X, Y, Z), facing, and scale for a named crew seat mount on this giant's rig,
        /// reusing the frame's thread-cached BoneWorldPoses with zero heap allocation.
        /// </summary>
        public bool TryEvaluateCrewSeatWorldPose(string seatId, Vector3 hostDrawPos, out CrewSeatWorldPose result)
        {
            result = default;
            if (RigDef == null || string.IsNullOrEmpty(seatId))
            {
                return false;
            }

            Pawn ownerPawn = Pawn;
            GiantRenderSnapshot snap = latestSnapshot;
            bool isDead = (ownerPawn != null && ownerPawn.Dead) || (snap != null && snap.isDead);
            if (isDead)
            {
                return false;
            }

            bool isDowned = (ownerPawn != null && ownerPawn.Downed) || (snap != null && snap.isDowned);
            PawnDrawParms parms = new PawnDrawParms
            {
                pawn = ownerPawn,
                matrix = Matrix4x4.TRS(hostDrawPos, Quaternion.identity, Vector3.one)
            };
            BoneWorldPose[] solvedPoses = GetOrSolveThreadCachedWorldPoses(parms, out _, out snap);
            int count = RigDef.SortedNodes.Length;
            if (solvedPoses == null || solvedPoses.Length < count)
            {
                return false;
            }

            bool flyingVis = !isDowned && snap != null && snap.isFlyingEntry;
            Vector2 flightOff = flyingVis ? snap.flightVisualOffsetXZ : Vector2.zero;
            float flightBoost = flyingVis ? snap.flightAltitudeBoost : 0f;
            int thingId = ownerPawn != null ? ownerPawn.thingIDNumber : 0;

            return TryEvaluateCrewSeatWorldPoseFromPoses(
                RigDef,
                seatId,
                solvedPoses.AsSpan(0, count),
                hostDrawPos,
                isDowned,
                thingId,
                flyingVis,
                flightOff,
                flightBoost,
                out result,
                posesIncludeFlightOffset: true);
        }

        public void DrawCollisionCellsOverlay()
        {
            if (footprint.collisionCells.Count > 0)
            {
                GenDraw.DrawFieldEdges(footprint.collisionCells, new Color(1f, 0.32f, 0.24f, 0.92f));
            }
            DrawPawnExclusionCellsOverlay(excludeCollisionCells: true);
        }

        private readonly List<IntVec3> pawnExclusionOverlayScratch = new List<IntVec3>(64);
        private readonly List<IntVec3> hitCellsOverlayScratch = new List<IntVec3>(64);

        public void DrawPawnExclusionCellsOverlay(bool excludeCollisionCells = true)
        {
            if (footprint.pawnExclusionCells.Count == 0)
            {
                return;
            }

            pawnExclusionOverlayScratch.Clear();
            for (int i = 0; i < footprint.pawnExclusionCells.Count; i++)
            {
                IntVec3 cell = footprint.pawnExclusionCells[i];
                if (!excludeCollisionCells || !footprint.collisionCellSet.Contains(cell))
                {
                    pawnExclusionOverlayScratch.Add(cell);
                }
            }
            if (pawnExclusionOverlayScratch.Count > 0)
            {
                GenDraw.DrawFieldEdges(pawnExclusionOverlayScratch, new Color(1f, 0.76f, 0.20f, 0.88f));
            }
        }

        public void DrawHitCellsOverlay(bool excludeCollisionCells = false)
        {
            if (footprint.hitCells.Count == 0)
            {
                return;
            }

            hitCellsOverlayScratch.Clear();
            for (int i = 0; i < footprint.hitCells.Count; i++)
            {
                HitCellEntry entry = footprint.hitCells[i];
                if (!excludeCollisionCells || (!entry.isCollisionCell && !entry.isPawnExclusionCell))
                {
                    hitCellsOverlayScratch.Add(entry.cell);
                }
            }
            if (hitCellsOverlayScratch.Count > 0)
            {
                GenDraw.DrawFieldEdges(hitCellsOverlayScratch, new Color(0.28f, 0.92f, 0.48f, 0.85f));
            }
        }

        public void DrawFootprintOverlay()
        {
            DrawCollisionCellsOverlay();
            DrawHitCellsOverlay(excludeCollisionCells: true);
        }

        public void DrawSkeletonOverlay()
        {
            if (RigDef == null || footprint.bonePoses.Length == 0)
            {
                return;
            }

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            float alt = AltitudeLayer.MetaOverlays.AltitudeFor();
            for (int i = 0; i < footprint.bonePoses.Length; i++)
            {
                ref readonly BoneWorldPose pose = ref footprint.bonePoses[i];
                if (!pose.visible)
                {
                    continue;
                }

                Vector3 jointPos = new Vector3(pose.jointWorldXZ.x, alt, pose.jointWorldXZ.y);
                int pIdx = nodes[i].parentRuntimeIndex;
                if (pIdx >= 0 && footprint.bonePoses[pIdx].visible)
                {
                    Vector3 parentJoint = new Vector3(footprint.bonePoses[pIdx].jointWorldXZ.x, alt, footprint.bonePoses[pIdx].jointWorldXZ.y);
                    GenDraw.DrawLineBetween(parentJoint, jointPos, SimpleColor.Yellow, 0.08f);
                }

                Vector2 tipXZ = pose.UVToWorldXZ(0.5f, 0.85f);
                Vector3 tipPos = new Vector3(tipXZ.x, alt, tipXZ.y);
                GenDraw.DrawLineBetween(jointPos, tipPos, SimpleColor.Cyan, 0.05f);
                GenDraw.DrawCircleOutline(jointPos, 0.16f, SimpleColor.Red);
            }

            if (bezierRuntimes != null)
            {
                for (int c = 0; c < bezierRuntimes.Length; c++)
                {
                    DampedBezierChainRuntime rt = bezierRuntimes[c];
                    if (rt == null || !rt.IsInitialized)
                    {
                        continue;
                    }
                    ReadOnlySpan<Vector2> pts = rt.CurvePoints;
                    for (int s = 0; s < pts.Length - 1; s++)
                    {
                        Vector3 a = new Vector3(pts[s].x, alt + 0.02f, pts[s].y);
                        Vector3 b = new Vector3(pts[s + 1].x, alt + 0.02f, pts[s + 1].y);
                        GenDraw.DrawLineBetween(a, b, SimpleColor.Magenta, 0.04f);
                    }
                }
            }

            if (softWhipRuntimes != null)
            {
                for (int w = 0; w < softWhipRuntimes.Length; w++)
                {
                    SoftWhipRuntime rt = softWhipRuntimes[w];
                    if (rt == null || !rt.IsInitialized)
                    {
                        continue;
                    }
                    ReadOnlySpan<Vector2> pts = rt.CenterlineWorldXZ;
                    for (int s = 0; s < pts.Length - 1; s++)
                    {
                        Vector3 a = new Vector3(pts[s].x, alt + 0.03f, pts[s].y);
                        Vector3 b = new Vector3(pts[s + 1].x, alt + 0.03f, pts[s + 1].y);
                        GenDraw.DrawLineBetween(a, b, SimpleColor.Green, 0.045f);
                    }
                }
            }

            if (CollectAllConfiguredMuzzleDebugShots(tempEmitterShots) > 0)
            {
                for (int m = 0; m < tempEmitterShots.Count; m++)
                {
                    GiantMuzzleShotInfo mShot = tempEmitterShots[m];
                    Vector3 mPos = new Vector3(mShot.WorldPos.x, alt + 0.04f, mShot.WorldPos.z);
                    float rad = mShot.WorldAngleDeg * Mathf.Deg2Rad;
                    Vector3 mDirEnd = mPos + new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad)) * 0.95f;
                    GenDraw.DrawCircleOutline(mPos, 0.22f, SimpleColor.Orange);
                    GenDraw.DrawCircleOutline(mPos, 0.10f, SimpleColor.White);
                    GenDraw.DrawLineBetween(mPos, mDirEnd, SimpleColor.Orange, 0.07f);
                }
            }

            IReadOnlyList<GiantBoneCrewSeatDef> seatMounts = RigDef.CrewSeatMounts;
            if (seatMounts != null && seatMounts.Count > 0 && Pawn != null)
            {
                Vector3 hostDrawPos = GetSafeDrawPos(Pawn);
                for (int s = 0; s < seatMounts.Count; s++)
                {
                    GiantBoneCrewSeatDef sm = seatMounts[s];
                    if (sm == null || !TryEvaluateCrewSeatWorldPose(sm.seatId, hostDrawPos, out CrewSeatWorldPose sPose))
                    {
                        continue;
                    }
                    Vector3 sPos = new Vector3(sPose.worldXZ.x, alt + 0.045f, sPose.worldXZ.y);
                    float sRad = sPose.worldFacingDeg * Mathf.Deg2Rad;
                    Vector3 sDir = sPos + new Vector3(Mathf.Sin(sRad), 0f, Mathf.Cos(sRad)) * 0.65f;
                    GenDraw.DrawCircleOutline(sPos, 0.26f, SimpleColor.Blue);
                    GenDraw.DrawLineBetween(sPos, sDir, SimpleColor.Blue, 0.06f);
                }
            }
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            partCombat?.DrawSelectionOverlays();
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            if (RigDef == null)
            {
                yield break;
            }

            if (PartCombat != null)
            {
                foreach (Gizmo pg in PartCombat.GetPartCombatGizmos())
                {
                    yield return pg;
                }
            }

            if (flightLocomotion != null)
            {
                foreach (Gizmo fg in flightLocomotion.GetFlightGizmos(this))
                {
                    yield return fg;
                }
            }

            GiantCreatureFrameworkSettings modSettings = GiantCreatureFrameworkMod.Settings;
            bool showDebugRigInspector = Prefs.DevMode && DebugSettings.godMode &&
                (DebugDrawFootprint || DebugDrawSkeleton ||
                 (modSettings != null && (modSettings.drawCollisionCells || modSettings.drawHitCells || modSettings.drawSkeleton)));
            if (showDebugRigInspector)
            {
                yield return new Command_Action
                {
                    defaultLabel = "GCF_InspectRigLabel".Translate(),
                    defaultDesc = "GCF_InspectRigDesc".Translate(),
                    icon = GiantRigGizmoAssets.GizmoIcon ?? BaseContent.BadTex,
                    action = () => Find.WindowStack.Add(new Dialog_GiantRigInspector(this))
                };
            }
        }

        public override string CompInspectStringExtra()
        {
            string result = base.CompInspectStringExtra();
            if (Pawn == null || Pawn.Dead)
            {
                return result;
            }
            if (flightLocomotion != null)
            {
                string flightLine = flightLocomotion.GetInspectStringExtra(this);
                if (!string.IsNullOrEmpty(flightLine))
                {
                    result = string.IsNullOrEmpty(result) ? flightLine : result + "\n" + flightLine;
                }
            }
            if (aquaticLocomotion != null)
            {
                string aqLine = aquaticLocomotion.GetInspectStringExtra(this);
                if (!string.IsNullOrEmpty(aqLine))
                {
                    result = string.IsNullOrEmpty(result) ? aqLine : result + "\n" + aqLine;
                }
            }
            return result;
        }
    }
}
