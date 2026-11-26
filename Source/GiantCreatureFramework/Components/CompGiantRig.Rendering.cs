using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    // Ground shadows, persistent auras, and main-thread dynamic mesh uploads.
    public partial class CompGiantRig
    {
        private bool TryDrawCustomRigShadowTexture(Vector3 shadowOriginWorld, float headingDeg, float shadowScale, float shadowAlpha)
        {
            if (!UnityData.IsInMainThread || RigDef == null || string.IsNullOrEmpty(RigDef.shadowTexPath) || shadowAlpha <= 0.01f)
            {
                return false;
            }

            Vector2 baseSize = RigDef.shadowDrawSize;
            if (baseSize.x <= 0.1f || baseSize.y <= 0.1f)
            {
                return false;
            }

            Material mat = GiantMaterialCache.Resolve(RigDef.shadowTexPath, Color.white, ShaderDatabase.Transparent);
            if (mat == null)
            {
                return false;
            }

            MaterialPropertyBlock shadowMpb = GiantVfxSystem.SharedShadowPropertyBlock;
            shadowMpb.Clear();
            shadowMpb.SetColor(ShaderPropertyIDs.Color, new Color(0f, 0f, 0f, Mathf.Clamp01(shadowAlpha)));
            float shadowY = AltitudeLayer.Shadows.AltitudeFor() + 0.015f;
            Vector3 pos = new Vector3(shadowOriginWorld.x, shadowY, shadowOriginWorld.z);
            Quaternion rot = Quaternion.AngleAxis(headingDeg, Vector3.up);
            Vector3 sz = new Vector3(baseSize.x * shadowScale, 1f, baseSize.y * shadowScale);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, rot, sz), mat, 0, null, 0, shadowMpb);
            return true;
        }

        private bool TryDrawSkeletalGroundShadow(Vector3 shadowOriginWorld, float headingDeg, float shadowScale, float shadowAlpha)
        {
            if (!UnityData.IsInMainThread || RigDef == null || shadowAlpha <= 0.01f)
            {
                return false;
            }

            Shader shadowShader = GCF_VfxDefOf.GCF_GiantDropShadow?.Shader
                ?? DefDatabase<ShaderTypeDef>.GetNamedSilentFail("GCF_GiantDropShadow")?.Shader;
            if (shadowShader == null)
            {
                return false;
            }

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            int count = nodes.Length;
            if (count <= 0)
            {
                return false;
            }

            if (shadowPoseScratch == null || shadowPoseScratch.Length < count)
            {
                shadowPoseScratch = new BoneWorldPose[Math.Max(64, count)];
            }

            GiantRenderSnapshot snap = latestSnapshot;
            ReadOnlySpan<BoneLocalPose> localSpan = snap?.localPoses != null ? snap.localPoses : ReadOnlySpan<BoneLocalPose>.Empty;
            ReadOnlySpan<bool> missingSpan = snap?.missingMask != null ? snap.missingMask : ReadOnlySpan<bool>.Empty;
            ReadOnlySpan<GiantBoneViewSector> sectorSpan = snap?.activeSectors != null ? snap.activeSectors : ReadOnlySpan<GiantBoneViewSector>.Empty;
            ReadOnlySpan<Vector2> recoilSpan = snap?.turretRecoilOffsets != null ? snap.turretRecoilOffsets : ReadOnlySpan<Vector2>.Empty;

            SkeletalKinematics.SolveWorldPoses(
                RigDef,
                new Vector2(shadowOriginWorld.x, shadowOriginWorld.z),
                headingDeg,
                localSpan,
                missingSpan,
                shadowPoseScratch.AsSpan(0, count),
                shadowScale,
                sectorSpan,
                -1,
                recoilSpan);

            MaterialPropertyBlock shadowMpb = GiantVfxSystem.SharedShadowPropertyBlock;
            shadowMpb.Clear();
            shadowMpb.SetColor(ShaderPropertyIDs.Color, new Color(0f, 0f, 0f, Mathf.Clamp01(shadowAlpha)));
            float shadowY = AltitudeLayer.Shadows.AltitudeFor() + 0.022f;

            bool drewAny = false;
            for (int i = 0; i < count; i++)
            {
                if (nodes[i].isRenderedBySkinnedMesh)
                {
                    continue;
                }
                ref readonly BoneWorldPose pose = ref shadowPoseScratch[i];
                if (!pose.visible)
                {
                    continue;
                }
                string texPath = nodes[i].GetTexPath(pose.activeSector);
                Material mat = GiantMaterialCache.Resolve(texPath, Color.white, shadowShader);
                if (mat == null)
                {
                    continue;
                }
                Graphics.DrawMesh(MeshPool.plane10, pose.BuildRenderMatrix(shadowY), mat, 0, null, 0, shadowMpb);
                drewAny = true;
            }

            float headingDelta = Mathf.DeltaAngle(currentHeadingDeg, headingDeg);
            Quaternion localShadowRot = Mathf.Abs(headingDelta) > 0.01f
                ? Quaternion.AngleAxis(headingDelta, Vector3.up)
                : Quaternion.identity;
            Matrix4x4 localShadowMat = Matrix4x4.TRS(
                new Vector3(shadowOriginWorld.x, shadowY, shadowOriginWorld.z),
                localShadowRot,
                new Vector3(shadowScale, 1f, shadowScale));

            if (createdSkinnedMeshPrimaryNodes != null && RigDef.skinnedMeshes != null)
            {
                for (int m = 0; m < createdSkinnedMeshPrimaryNodes.Count && m < RigDef.skinnedMeshes.Count; m++)
                {
                    PawnRenderNode_GiantSkinnedMesh smNode = createdSkinnedMeshPrimaryNodes[m];
                    GiantSkinnedMeshDef smDef = RigDef.skinnedMeshes[m];
                    if (smNode == null || smDef == null || !smNode.HasValidMesh || IsSkinnedMeshMissingThreadSafe(m))
                    {
                        continue;
                    }
                    Material mat = smNode.ResolveActiveShadowMaterial(shadowShader)
                        ?? GiantMaterialCache.Resolve(smDef.texPath, Color.white, shadowShader);
                    Mesh dynMesh = smNode.ActiveDynamicMesh;
                    if (mat != null && dynMesh != null)
                    {
                        Graphics.DrawMesh(dynMesh, localShadowMat, mat, 0, null, 0, shadowMpb);
                        drewAny = true;
                    }
                }
            }

            if (createdSoftWhipNodes != null && RigDef.softWhips != null)
            {
                for (int s = 0; s < createdSoftWhipNodes.Count && s < RigDef.softWhips.Count; s++)
                {
                    PawnRenderNode_GiantSoftWhip whipNode = createdSoftWhipNodes[s];
                    SoftWhipDef wDef = RigDef.softWhips[s];
                    if (whipNode == null || wDef == null || string.IsNullOrEmpty(wDef.texPath)
                        || !whipNode.HasValidMesh || !IsSoftWhipVisibleThreadSafe(wDef.runtimeIndex))
                    {
                        continue;
                    }
                    Material mat = GiantMaterialCache.Resolve(wDef.texPath, Color.white, shadowShader);
                    Mesh whipMesh = whipNode.ActiveDynamicMesh;
                    if (mat != null && whipMesh != null)
                    {
                        Graphics.DrawMesh(whipMesh, localShadowMat, mat, 0, null, 0, shadowMpb);
                        drewAny = true;
                    }
                }
            }

            return drewAny;
        }

        public override void PostDraw()
        {
            base.PostDraw();
            if (Pawn == null || !Pawn.Spawned || Pawn.Dead || Pawn.Map == null || Pawn.IsHiddenFromPlayer()
                || GiantMotionOverrideRegistry.IsHiddenInOrbit(Pawn)
                || GiantMotionOverrideRegistry.HasActiveHighAltitudeOverride(Pawn))
            {
                return;
            }
            if (flightEntryActive)
            {
                float heightRatio = Mathf.Clamp01(flightCurrentHeightOffset / Mathf.Max(0.1f, flightStartAltitudeOffset));
                float scale = Mathf.Lerp(1.0f, 0.58f, heightRatio);
                float alpha = Mathf.Lerp(0.95f, 0.55f, heightRatio);
                Vector3 shadowPos = new Vector3(flightCurrentGroundXZ.x + 0.35f, 0f, flightCurrentGroundXZ.y - 0.35f);
                bool drewCustom = TryDrawCustomRigShadowTexture(shadowPos, flightHeadingDeg, scale, alpha * 0.72f);
                bool drewSkel = TryDrawSkeletalGroundShadow(shadowPos, flightHeadingDeg, scale, alpha);
                if (!drewCustom && !drewSkel)
                {
                    GiantVfxSystem.DrawFlightGroundShadow(shadowPos, flightHeadingDeg, scale, alpha);
                }
                return;
            }
            if (flightLocomotion != null && !Pawn.Downed && flightLocomotion.CurrentAltitude01 > 0.02f)
            {
                float alt01 = flightLocomotion.CurrentAltitude01;
                GiantFlightDef fDef = flightLocomotion.Def;
                Vector3 groundPos = GetSafeDrawPos(Pawn);
                float scale = Mathf.Lerp(1.0f, fDef.dropShadowScale, alt01);
                float alpha = Mathf.Lerp(0.18f, fDef.dropShadowAlpha, alt01);
                float customAlpha = RigDef != null ? Mathf.Lerp(0.22f, RigDef.shadowAlpha, alt01) : alpha;
                Vector3 shadowPos = new Vector3(
                    groundPos.x + fDef.dropShadowOffsetXZ.x * alt01,
                    0f,
                    groundPos.z + fDef.dropShadowOffsetXZ.y * alt01);
                bool drewCustom = TryDrawCustomRigShadowTexture(shadowPos, currentHeadingDeg, scale, customAlpha);
                bool drewSkel = TryDrawSkeletalGroundShadow(shadowPos, currentHeadingDeg, scale, alpha);
                if (!drewCustom && !drewSkel)
                {
                    GiantVfxSystem.DrawFlightGroundShadow(shadowPos, currentHeadingDeg, scale, alpha);
                }
                return;
            }
            if (RigDef != null && !string.IsNullOrEmpty(RigDef.shadowTexPath) && (RigDef.renderGroundShadowWhenGrounded || Pawn.Downed))
            {
                Vector3 groundPos = GetSafeDrawPos(Pawn);
                Vector3 shadowPos = new Vector3(
                    groundPos.x + RigDef.shadowOffsetXZ.x * 0.35f,
                    0f,
                    groundPos.z + RigDef.shadowOffsetXZ.y * 0.35f);
                TryDrawCustomRigShadowTexture(shadowPos, currentHeadingDeg, 0.96f, RigDef.shadowAlpha * 0.75f);
            }
        }

        private void TickPersistentBoneAuras(int ticksGame)
        {
            if (RigDef == null || footprint.bonePoses.Length == 0)
            {
                return;
            }
            int[] auraIndices = RigDef.persistentAuraBoneIndices;
            bool hasAuras = auraIndices != null && auraIndices.Length > 0;
            if (!hasAuras && !anyDamagedBoneBelow65)
            {
                return;
            }
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            int count = Mathf.Min(nodes.Length, footprint.bonePoses.Length);
            if (hasAuras)
            {
                for (int k = 0; k < auraIndices.Length; k++)
                {
                    int idx = auraIndices[k];
                    if ((uint)idx < (uint)count)
                    {
                        GiantVfxSystem.TickPersistentBoneAura(Pawn, this, nodes[idx], in footprint.bonePoses[idx], ticksGame);
                    }
                }
            }
            if (anyDamagedBoneBelow65 && partHealthPct != null)
            {
                int hpLen = Mathf.Min(count, partHealthPct.Length);
                for (int i = 0; i < hpLen; i++)
                {
                    float hp = partHealthPct[i];
                    if (hp > 0f && hp < 0.65f)
                    {
                        GiantVfxSystem.TickBattleDamagedBoneVfx(Pawn, this, nodes[i], in footprint.bonePoses[i], hp, ticksGame);
                    }
                }
            }
        }

        public void FlushPendingDynamicMeshesOnMainThread(bool ignoreViewportCull = false)
        {
            if (!pendingDynamicMeshUpload || !UnityData.IsInMainThread)
            {
                return;
            }

            GiantRenderSnapshot snap = latestSnapshot;
            if (snap == null)
            {
                return;
            }

            if (!ignoreViewportCull
                && initialDynamicMeshUploaded
                && Pawn != null
                && Pawn.Spawned
                && !Pawn.Dead
                && Pawn.Map != null
                && Find.CurrentMap == Pawn.Map
                && Find.CameraDriver != null
                && !footprint.worldDrawBounds.IsEmpty)
            {
                CellRect paddedView = Find.CameraDriver.CurrentViewRect.ExpandedBy(12);
                if (!paddedView.Overlaps(footprint.worldDrawBounds))
                {
                    return;
                }
            }

            pendingDynamicMeshUpload = false;
            initialDynamicMeshUploaded = true;

            SoftWhipRibbonSnapshot[] snapSoftWhips = snap.softWhips;
            if (snapSoftWhips != null && createdSoftWhipNodes != null)
            {
                for (int s = 0; s < createdSoftWhipNodes.Count && s < snapSoftWhips.Length; s++)
                {
                    createdSoftWhipNodes[s]?.RebuildMeshOnMainThread(snapSoftWhips[s]);
                }
            }
            float[] snapTrackScroll = snap.trackScrollCells;
            if (snapTrackScroll != null && createdRollingTrackNodes != null)
            {
                RebuildRollingTrackMeshesOnMainThread(snapTrackScroll);
            }
            if (createdSkinnedMeshPrimaryNodes != null && createdSkinnedMeshPrimaryNodes.Count > 0)
            {
                RebuildSkinnedMeshesOnMainThread();
            }
        }

        public bool IsSkinnedMeshMissingThreadSafe(int meshIndex)
        {
            GiantRenderSnapshot snap = latestSnapshot;
            if (snap?.skinnedMeshMissingMask != null && (uint)meshIndex < (uint)snap.skinnedMeshMissingMask.Length)
            {
                return snap.skinnedMeshMissingMask[meshIndex];
            }
            return skinnedMeshMissingMask != null && (uint)meshIndex < (uint)skinnedMeshMissingMask.Length && skinnedMeshMissingMask[meshIndex];
        }

        private void RebuildSkinnedMeshesOnMainThread()
        {
            if (RigDef?.skinnedMeshes == null || createdSkinnedMeshPrimaryNodes == null || createdSkinnedMeshPrimaryNodes.Count == 0)
            {
                return;
            }
            int count = RigDef.SortedNodes.Length;
            SolveOrReuseWorldPoses(Vector2.zero);

            for (int m = 0; m < RigDef.skinnedMeshes.Count && m < createdSkinnedMeshPrimaryNodes.Count; m++)
            {
                bool meshPartMissing = skinnedMeshMissingMask != null && m < skinnedMeshMissingMask.Length && skinnedMeshMissingMask[m];
                createdSkinnedMeshPrimaryNodes[m]?.RebuildMeshOnMainThread(worldPoses.AsSpan(0, count), Vector2.zero, meshPartMissing);
            }
        }

        /// <summary>
        /// Thread-safe world-Y altitude boost for airborne giants (relative to GroundAliveBaseAltitudeY).
        /// Elevates LowSkim giants above FogOfWar (12.439f) and HighAirborne / flightEntryActive giants
        /// above FogOfWar + Darkness (12.845f) while staying strictly below WorldClipper and MetaOverlays.
        /// </summary>
        public static float ComputeAirborneRenderAltitudeBoost(
            bool flightEntryActive,
            GiantFlightRuntime flightRuntime,
            int thingIdNumber = 0)
        {
            float groundBaseY = GroundAliveBaseAltitudeY;
            float highAirDeltaY = HighAirborneBaseAltitudeY - groundBaseY;
            float lowSkimDeltaY = LowSkimAirborneBaseAltitudeY - groundBaseY;
            float idTieBreak = (thingIdNumber & 15) * 0.004f;

            if (flightEntryActive)
            {
                return highAirDeltaY + idTieBreak;
            }
            if (flightRuntime == null || flightRuntime.CurrentAltitude01 <= 1e-3f)
            {
                return 0f;
            }

            float targetDeltaY = flightRuntime.Def != null && flightRuntime.Def.altitudeTier == FlightAltitudeTier.LowSkim
                ? lowSkimDeltaY
                : highAirDeltaY;
            return (targetDeltaY + idTieBreak) * Mathf.Clamp01(flightRuntime.CurrentAltitude01);
        }

        private void RebuildRollingTrackMeshesOnMainThread(float[] scrollCells)
        {
            if (RigDef?.rollingTracks == null || createdRollingTrackNodes == null || scrollCells == null)
            {
                return;
            }
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            for (int t = 0; t < RigDef.rollingTracks.Count && t < createdRollingTrackNodes.Count && t < scrollCells.Length; t++)
            {
                GiantRollingTrackDef tDef = RigDef.rollingTracks[t];
                PawnRenderNode_GiantRollingTrack trackNode = createdRollingTrackNodes[t];
                if (tDef == null || trackNode == null)
                {
                    continue;
                }
                int pIdx = tDef.resolvedParentBoneIndex;
                if ((uint)pIdx >= (uint)nodes.Length)
                {
                    trackNode.RebuildMeshOnMainThread(GiantBoneViewSector.Dorsal, Vector2.one, 512, 0f, false);
                    continue;
                }
                BoneNodeDef pNode = nodes[pIdx];
                GiantBoneViewSector sec = RigDef.hasAnyDirectionalVariants && sectorsInitialized
                    ? activeBoneSectors[pIdx]
                    : GiantBoneViewSector.Dorsal;
                bool parentVisible = IsBoneVisibleThreadSafe(pIdx);
                if (parentVisible
                    && lastUploadedTrackScroll != null
                    && t < lastUploadedTrackScroll.Length
                    && !float.IsNaN(lastUploadedTrackScroll[t])
                    && Mathf.Abs(scrollCells[t] - lastUploadedTrackScroll[t]) < 1e-4f
                    && lastUploadedTrackSector[t] == sec)
                {
                    continue;
                }
                if (lastUploadedTrackScroll != null && t < lastUploadedTrackScroll.Length)
                {
                    lastUploadedTrackScroll[t] = parentVisible ? scrollCells[t] : float.NaN;
                    lastUploadedTrackSector[t] = sec;
                }
                pNode.GetResolvedSelfParams(sec, out Vector2 pDrawSize, out _, out _);
                trackNode.RebuildMeshOnMainThread(sec, pDrawSize, pNode.textureResolution, scrollCells[t], parentVisible);
            }
        }

    }
}
