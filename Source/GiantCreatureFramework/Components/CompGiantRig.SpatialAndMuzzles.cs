using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public partial class CompGiantRig
    {
        public Vector2? ActiveAttackTargetXZ => hasAttackTargetWorldXZ ? (Vector2?)attackTargetWorldXZ : null;
        public string ActiveOneShotClipDefName => activeOneShotAnim?.defName;
        public float ActiveOneShotElapsedTicks => oneShotElapsedTicks;
        public int SoftWhipSlotCount => softWhipRuntimes?.Length ?? 0;

        public bool TryGetIntactSoftWhipCenterline(int slotIndex, out ReadOnlySpan<Vector2> centerlineWorldXZ)
        {
            centerlineWorldXZ = default;
            if (RigDef?.softWhips == null || softWhipRuntimes == null || (uint)slotIndex >= (uint)softWhipRuntimes.Length || slotIndex >= RigDef.softWhips.Count)
            {
                return false;
            }
            SoftWhipDef wDef = RigDef.softWhips[slotIndex];
            if (wDef == null)
            {
                return false;
            }
            if (softWhipTendrilMissingMask != null && slotIndex < softWhipTendrilMissingMask.Length && softWhipTendrilMissingMask[slotIndex])
            {
                return false;
            }
            if ((uint)wDef.resolvedRootBoneIndex < (uint)(partMissingMask?.Length ?? 0) && partMissingMask[wDef.resolvedRootBoneIndex])
            {
                return false;
            }
            SoftWhipRuntime rt = softWhipRuntimes[slotIndex];
            if (rt == null || !rt.IsInitialized || rt.CenterlineWorldXZ.Length < 4)
            {
                return false;
            }
            centerlineWorldXZ = rt.CenterlineWorldXZ;
            return true;
        }

        public bool TryBindWorldPointToIntactBoneUV(
            Vector3 worldPos,
            int preferredBoneIndex,
            float currentWorldHeadingDeg,
            out int boundBoneIndex,
            out Vector2 boundUV,
            out float angleOffsetDeg)
        {
            boundBoneIndex = -1;
            boundUV = new Vector2(0.5f, 0.5f);
            angleOffsetDeg = 0f;
            if (RigDef == null)
            {
                return false;
            }

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            if (nodes == null || nodes.Length == 0)
            {
                return false;
            }

            Span<BoneWorldPose> livePoses = nodes.Length <= 64
                ? stackalloc BoneWorldPose[nodes.Length]
                : new BoneWorldPose[nodes.Length];
            bool hasLive = SolveLiveWorldPoses(livePoses);
            if (!hasLive && footprint.bonePoses.Length >= nodes.Length)
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    livePoses[i] = footprint.bonePoses[i];
                }
                hasLive = true;
            }
            if (!hasLive)
            {
                return false;
            }

            int resolvedIdx = -1;
            if ((uint)preferredBoneIndex < (uint)nodes.Length)
            {
                int walk = preferredBoneIndex;
                int guard = 0;
                while ((uint)walk < (uint)nodes.Length && guard++ < 64)
                {
                    if (livePoses[walk].visible && livePoses[walk].worldSize.x > 1e-4f && livePoses[walk].worldSize.y > 1e-4f)
                    {
                        resolvedIdx = walk;
                        break;
                    }
                    walk = nodes[walk].parentRuntimeIndex;
                }
            }

            if (resolvedIdx < 0)
            {
                float bestScore = float.MaxValue;
                Vector2 targetXZ = new Vector2(worldPos.x, worldPos.z);
                for (int i = nodes.Length - 1; i >= 0; i--)
                {
                    ref readonly BoneWorldPose cand = ref livePoses[i];
                    if (!cand.visible || cand.worldSize.x <= 1e-4f || cand.worldSize.y <= 1e-4f)
                    {
                        continue;
                    }
                    bool insideQuad = cand.TryWorldToUV(targetXZ.x, targetXZ.y, out _, out _);
                    float distSq = (cand.quadCenterWorldXZ - targetXZ).sqrMagnitude;
                    float score = insideQuad ? distSq * 0.25f : distSq + 2.5f;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        resolvedIdx = i;
                    }
                }
            }

            if (resolvedIdx < 0)
            {
                return false;
            }

            ref readonly BoneWorldPose pose = ref livePoses[resolvedIdx];
            float dx = worldPos.x - pose.jointWorldXZ.x;
            float dz = worldPos.z - pose.jointWorldXZ.y;
            float localX = dx * pose.cosTheta - dz * pose.sinTheta;
            float localZ = dx * pose.sinTheta + dz * pose.cosTheta;
            float u = Mathf.Clamp(pose.pivotUV.x + localX / pose.worldSize.x, -0.35f, 1.35f);
            float v = Mathf.Clamp(pose.pivotUV.y + localZ / pose.worldSize.y, -0.35f, 1.35f);

            boundBoneIndex = resolvedIdx;
            boundUV = new Vector2(u, v);
            angleOffsetDeg = Mathf.DeltaAngle(pose.worldAngleDeg, currentWorldHeadingDeg);
            return true;
        }

        public bool TrySampleSoftWhipPointForRootBone(
            string rootBoneId,
            float normalizedT,
            out Vector3 sampleWorldPos,
            out float tangentAngleDeg)
        {
            sampleWorldPos = GetSafeDrawPos(Pawn);
            tangentAngleDeg = currentHeadingDeg;
            if (RigDef?.softWhips == null || softWhipRuntimes == null || string.IsNullOrEmpty(rootBoneId) || !RigDef.TryGetNodeIndex(rootBoneId, out int rootIdx))
            {
                return false;
            }

            float t = Mathf.Clamp01(normalizedT);
            for (int s = 0; s < RigDef.softWhips.Count && s < softWhipRuntimes.Length; s++)
            {
                SoftWhipDef wDef = RigDef.softWhips[s];
                if (wDef == null || wDef.resolvedRootBoneIndex != rootIdx)
                {
                    continue;
                }
                if (softWhipTendrilMissingMask != null && s < softWhipTendrilMissingMask.Length && softWhipTendrilMissingMask[s])
                {
                    return false;
                }
                SoftWhipRuntime rt = softWhipRuntimes[s];
                if (rt != null && rt.IsInitialized)
                {
                    ReadOnlySpan<Vector2> pts = rt.CenterlineWorldXZ;
                    if (pts.Length >= 2)
                    {
                        int idx = Mathf.Clamp(Mathf.RoundToInt(t * (pts.Length - 1)), 0, pts.Length - 1);
                        int i0 = Mathf.Max(0, idx - 2);
                        int i1 = Mathf.Min(pts.Length - 1, idx + 2);
                        Vector2 pSample = pts[idx];
                        Vector2 d = pts[i1] - pts[i0];
                        sampleWorldPos = new Vector3(pSample.x, sampleWorldPos.y, pSample.y);
                        if (d.sqrMagnitude > 1e-4f)
                        {
                            tangentAngleDeg = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        public bool TryGetSoftWhipTipForRootBone(string rootBoneId, out Vector3 tipWorldPos, out float tipAngleDeg)
        {
            tipWorldPos = GetSafeDrawPos(Pawn);
            tipAngleDeg = currentHeadingDeg;
            if (RigDef?.softWhips == null || softWhipRuntimes == null || string.IsNullOrEmpty(rootBoneId) || !RigDef.TryGetNodeIndex(rootBoneId, out int rootIdx))
            {
                return false;
            }

            for (int s = 0; s < RigDef.softWhips.Count && s < softWhipRuntimes.Length; s++)
            {
                SoftWhipDef wDef = RigDef.softWhips[s];
                if (wDef == null || wDef.resolvedRootBoneIndex != rootIdx)
                {
                    continue;
                }
                if (softWhipTendrilMissingMask != null && s < softWhipTendrilMissingMask.Length && softWhipTendrilMissingMask[s])
                {
                    return false;
                }
                SoftWhipRuntime rt = softWhipRuntimes[s];
                if (rt != null && rt.IsInitialized)
                {
                    ReadOnlySpan<Vector2> pts = rt.CenterlineWorldXZ;
                    if (pts.Length >= 2)
                    {
                        Vector2 tip = pts[pts.Length - 1];
                        Vector2 prev = pts[Mathf.Max(0, pts.Length - 4)];
                        Vector2 d = tip - prev;
                        tipWorldPos = new Vector3(tip.x, tipWorldPos.y, tip.y);
                        if (d.sqrMagnitude > 1e-4f)
                        {
                            tipAngleDeg = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        public bool TryGetBoneUVWorldPos(int boneIndex, Vector2 uv, out Vector3 worldPos, out float worldAngleDeg)
        {
            worldPos = GetSafeDrawPos(Pawn);
            worldAngleDeg = currentHeadingDeg;
            if (RigDef == null)
            {
                return false;
            }
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            if ((uint)boneIndex >= (uint)nodes.Length)
            {
                return false;
            }

            Span<BoneWorldPose> livePoses = nodes.Length <= 64
                ? stackalloc BoneWorldPose[nodes.Length]
                : new BoneWorldPose[nodes.Length];
            if (SolveLiveWorldPoses(livePoses) && livePoses[boneIndex].visible)
            {
                ref readonly BoneWorldPose pose = ref livePoses[boneIndex];
                Vector2 xz = pose.UVToWorldXZ(uv.x, uv.y);
                worldPos = new Vector3(xz.x, worldPos.y, xz.y);
                worldAngleDeg = pose.worldAngleDeg;
                return true;
            }

            if (boneIndex < footprint.bonePoses.Length && footprint.bonePoses[boneIndex].visible)
            {
                ref readonly BoneWorldPose pose = ref footprint.bonePoses[boneIndex];
                Vector2 xz = pose.UVToWorldXZ(uv.x, uv.y);
                worldPos = new Vector3(xz.x, worldPos.y, xz.y);
                worldAngleDeg = pose.worldAngleDeg;
                return true;
            }
            return false;
        }

        public bool TryGetBonePixelWorldPos(string boneId, Vector2 pixel, out Vector2 worldXZ, out float worldAngleDeg)
        {
            worldXZ = Vector2.zero;
            worldAngleDeg = currentHeadingDeg;
            if (RigDef == null || string.IsNullOrEmpty(boneId) || !RigDef.TryGetNodeIndex(boneId, out int bIdx) || !IsBoneIntact(boneId))
            {
                return false;
            }
            BoneNodeDef bDef = RigDef.SortedNodes[bIdx];
            float res = Mathf.Max(1f, bDef.textureResolution);
            Vector2 uv = new Vector2(pixel.x / res, 1f - (pixel.y / res));
            if (TryGetBoneUVWorldPos(bIdx, uv, out Vector3 wp, out worldAngleDeg))
            {
                worldXZ = new Vector2(wp.x, wp.z);
                return true;
            }
            return false;
        }

        public bool TryResolveVerbMuzzleShot(Verb verb, bool advanceCursor, out GiantMuzzleShotInfo shot)
        {
            shot = default;
            if (RigDef == null || verb is Verb_CarrierDispatchInterceptors)
            {
                return false;
            }

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            Span<BoneWorldPose> livePoses = nodes.Length <= 64
                ? stackalloc BoneWorldPose[nodes.Length]
                : new BoneWorldPose[nodes.Length];
            if (!SolveLiveWorldPoses(livePoses))
            {
                return false;
            }

            GiantWeaponEmitterDef emitter = ResolveEmitterForVerb(verb);
            int dummyCursor = 0;
            ref int cursorRef = ref GetEmitterCursorRef(emitter, ref dummyCursor);
            float projAltY = AltitudeLayer.Projectile.AltitudeFor();

            if (!GiantMuzzleResolver.TryResolveNextShotFromPoses(
                    RigDef,
                    livePoses,
                    emitter,
                    ref cursorRef,
                    advanceCursor,
                    projAltY,
                    IsBoneIntact,
                    out shot))
            {
                return false;
            }

            if (Pawn?.Map != null && !shot.MapCell.InBounds(Pawn.Map))
            {
                shot.MapCell = Pawn.Position;
            }
            return true;
        }

        public int CollectEmitterMuzzles(string emitterIdOrAbilityDefName, List<GiantMuzzleShotInfo> outShots)
        {
            if (outShots == null)
            {
                return 0;
            }
            outShots.Clear();
            if (RigDef == null || string.IsNullOrEmpty(emitterIdOrAbilityDefName))
            {
                return 0;
            }

            GiantWeaponEmitterDef emitter = GiantMuzzleResolver.MatchEmitter(
                RigDef,
                emitterId: emitterIdOrAbilityDefName,
                abilityDefName: emitterIdOrAbilityDefName,
                allowDefaultFallback: false);
            if (emitter == null)
            {
                return 0;
            }

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            Span<BoneWorldPose> livePoses = nodes.Length <= 64
                ? stackalloc BoneWorldPose[nodes.Length]
                : new BoneWorldPose[nodes.Length];
            if (!SolveLiveWorldPoses(livePoses))
            {
                return 0;
            }

            float projAltY = AltitudeLayer.Projectile.AltitudeFor();
            int count = GiantMuzzleResolver.CollectAllIntactBarrelShots(
                RigDef,
                livePoses,
                emitter,
                projAltY,
                IsBoneIntact,
                outShots);

            if (Pawn?.Map != null)
            {
                for (int i = 0; i < outShots.Count; i++)
                {
                    if (!outShots[i].MapCell.InBounds(Pawn.Map))
                    {
                        GiantMuzzleShotInfo s = outShots[i];
                        s.MapCell = Pawn.Position;
                        outShots[i] = s;
                    }
                }
            }
            return count;
        }

        public bool TryResolveAbilityMuzzle(
            AbilityDef abilityDef,
            string fallbackBoneId,
            out GiantMuzzleShotInfo primaryShot,
            List<GiantMuzzleShotInfo> allShots = null)
        {
            primaryShot = default;
            if (RigDef == null)
            {
                return false;
            }

            List<GiantMuzzleShotInfo> collector = allShots ?? tempEmitterShots;
            collector.Clear();

            if (abilityDef != null && CollectEmitterMuzzles(abilityDef.defName, collector) > 0)
            {
                primaryShot = collector[0];
                return true;
            }

            if (!string.IsNullOrEmpty(fallbackBoneId) && RigDef.TryGetNodeIndex(fallbackBoneId, out int bIdx) && IsBoneIntact(fallbackBoneId))
            {
                BoneNodeDef[] nodes = RigDef.SortedNodes;
                Span<BoneWorldPose> livePoses = nodes.Length <= 64
                    ? stackalloc BoneWorldPose[nodes.Length]
                    : new BoneWorldPose[nodes.Length];
                if (SolveLiveWorldPoses(livePoses))
                {
                    var fbBarrel = new GiantEmitterBarrelDef { boneId = fallbackBoneId };
                    if (GiantMuzzleResolver.TryResolveBarrelWorldShot(
                            RigDef,
                            livePoses,
                            null,
                            fbBarrel,
                            AltitudeLayer.Projectile.AltitudeFor(),
                            IsBoneIntact,
                            out primaryShot))
                    {
                        collector.Add(primaryShot);
                        return true;
                    }
                }
            }

            return false;
        }

        public int CollectAllConfiguredMuzzleDebugShots(List<GiantMuzzleShotInfo> outShots)
        {
            if (outShots == null)
            {
                return 0;
            }
            outShots.Clear();
            if (RigDef == null)
            {
                return 0;
            }

            BoneNodeDef[] nodes = RigDef.SortedNodes;
            Span<BoneWorldPose> livePoses = nodes.Length <= 64
                ? stackalloc BoneWorldPose[nodes.Length]
                : new BoneWorldPose[nodes.Length];
            if (!SolveLiveWorldPoses(livePoses))
            {
                return 0;
            }

            float projAltY = AltitudeLayer.Projectile.AltitudeFor();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (RigDef.weaponEmitters != null)
            {
                for (int e = 0; e < RigDef.weaponEmitters.Count; e++)
                {
                    GiantWeaponEmitterDef em = RigDef.weaponEmitters[e];
                    if (em?.barrels == null)
                    {
                        continue;
                    }
                    for (int b = 0; b < em.barrels.Count; b++)
                    {
                        GiantEmitterBarrelDef barrel = em.barrels[b];
                        if (barrel == null || string.IsNullOrEmpty(barrel.boneId))
                        {
                            continue;
                        }
                        string key = $"{barrel.boneId}:{barrel.socketId ?? "muzzle"}";
                        if (!seenKeys.Add(key))
                        {
                            continue;
                        }
                        if (GiantMuzzleResolver.TryResolveBarrelWorldShot(RigDef, livePoses, em, barrel, projAltY, IsBoneIntact, out GiantMuzzleShotInfo s))
                        {
                            outShots.Add(s);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(RigDef.rangedWeaponBoneId) && seenKeys.Add($"{RigDef.rangedWeaponBoneId}:muzzle"))
            {
                var fb = new GiantEmitterBarrelDef { boneId = RigDef.rangedWeaponBoneId };
                if (GiantMuzzleResolver.TryResolveBarrelWorldShot(RigDef, livePoses, null, fb, projAltY, IsBoneIntact, out GiantMuzzleShotInfo s))
                {
                    outShots.Add(s);
                }
            }

            return outShots.Count;
        }

        public string GetEmitterDebugSummary()
        {
            if (RigDef == null)
            {
                return "emitters=none";
            }
            int activeCount = CollectAllConfiguredMuzzleDebugShots(tempEmitterShots);
            if (activeCount <= 0)
            {
                return string.IsNullOrEmpty(RigDef.rangedWeaponBoneId) ? "emitters=0" : "emitters=0(severed)";
            }
            GiantMuzzleShotInfo first = tempEmitterShots[0];
            return $"muzzles={activeCount}[{first.BoneId}@({first.WorldPos.x:F1},{first.WorldPos.z:F1}) ang={first.WorldAngleDeg:F0}]";
        }

        public bool TryGetRangedMuzzleWorldPos(out Vector3 muzzleWorldPos, out float muzzleWorldAngleDeg, out IntVec3 muzzleCell)
        {
            muzzleWorldPos = GetSafeDrawPos(Pawn);
            muzzleWorldAngleDeg = currentHeadingDeg;
            muzzleCell = Pawn != null ? Pawn.Position : IntVec3.Invalid;

            Verb primaryVerb = Pawn?.equipment?.PrimaryEq?.PrimaryVerb;
            if (TryResolveVerbMuzzleShot(primaryVerb, advanceCursor: false, out GiantMuzzleShotInfo shot))
            {
                muzzleWorldPos = shot.WorldPos;
                muzzleWorldAngleDeg = shot.WorldAngleDeg;
                muzzleCell = shot.MapCell;
                return true;
            }

            return false;
        }

        public bool IsRangedWeaponBoneIntact()
        {
            if (RigDef == null)
            {
                return true;
            }
            GiantWeaponEmitterDef primaryEmitter = GiantMuzzleResolver.MatchEmitter(RigDef);
            return GiantMuzzleResolver.IsEmitterOperational(RigDef, primaryEmitter, IsBoneIntact);
        }

        public bool IsRangedVerbOperational(Verb verb)
        {
            if (RigDef == null)
            {
                return true;
            }
            if (verb is Verb_CarrierDispatchInterceptors)
            {
                return true;
            }
            GiantWeaponEmitterDef emitter = ResolveEmitterForVerb(verb);
            return GiantMuzzleResolver.IsEmitterOperational(RigDef, emitter, IsBoneIntact);
        }

        public GiantWeaponEmitterDef ResolveEmitterForVerb(Verb verb)
        {
            if (RigDef == null)
            {
                return null;
            }

            string abilityDefName = (verb is IAbilityVerb abVerb) ? abVerb.Ability?.def?.defName : null;
            string weaponDefName = verb?.EquipmentSource?.def?.defName;
            string verbTag = verb?.verbProps?.label ?? verb?.GetType().Name;
            bool allowDefault = string.IsNullOrEmpty(abilityDefName);

            return GiantMuzzleResolver.MatchEmitter(
                RigDef,
                emitterId: null,
                weaponDefName: weaponDefName,
                abilityDefName: abilityDefName,
                verbTag: verbTag,
                allowDefaultFallback: allowDefault);
        }

        private ref int GetEmitterCursorRef(GiantWeaponEmitterDef emitter, ref int fallbackZero)
        {
            if (emitter != null && RigDef?.weaponEmitters != null && emitterBarrelCursors != null)
            {
                int idx = RigDef.weaponEmitters.IndexOf(emitter);
                if ((uint)idx < (uint)emitterBarrelCursors.Length)
                {
                    return ref emitterBarrelCursors[idx];
                }
            }
            return ref fallbackZero;
        }

        /// <summary>
        /// Solves exact zero-latency world bone poses from the current skeletal local poses and body heading,
        /// avoiding any multi-tick footprint cache lag during warmup->shot transitions or rapid burst fire.
        /// </summary>
        public bool SolveLiveWorldPoses(Span<BoneWorldPose> outPoses)
        {
            if (RigDef == null)
            {
                return false;
            }
            EnsureBuffersInitialized();
            BoneNodeDef[] nodes = RigDef.SortedNodes;
            if (outPoses.Length < nodes.Length)
            {
                return false;
            }

            Vector3 simPos = Pawn != null ? GetSimTickPos(Pawn) : Vector3.zero;
            Vector2 rootXZ = new Vector2(simPos.x, simPos.z);
            SolveOrReuseWorldPoses(rootXZ);
            worldPoses.AsSpan(0, nodes.Length).CopyTo(outPoses.Slice(0, nodes.Length));
            return true;
        }

        private void SolveOrReuseWorldPoses(Vector2 targetRootXZ)
        {
            EnsureBuffersInitialized();
            int count = RigDef.SortedNodes.Length;
            if (!ManualOverrideActive
                && worldPosesSolvedSerial == localPoseSerial
                && ReferenceEquals(worldPosesSolvedRigDef, RigDef)
                && worldPosesSolvedHeading == currentHeadingDeg
                && worldPoses != null
                && worldPoses.Length >= count)
            {
                Vector2 delta = targetRootXZ - worldPosesSolvedRootXZ;
                if (delta.sqrMagnitude > 1e-12f)
                {
                    for (int i = 0; i < count; i++)
                    {
                        ref BoneWorldPose wp = ref worldPoses[i];
                        wp.jointWorldXZ += delta;
                        wp.quadCenterWorldXZ += delta;
                        wp.flatJointWorldXZ += delta;
                        wp.flatQuadCenterWorldXZ += delta;
                        if (wp.hasCoronalFlap)
                        {
                            wp.flapHingeXZ += delta;
                        }
                    }
                    worldPosesSolvedRootXZ = targetRootXZ;
                }
                return;
            }

            SkeletalKinematics.SolveWorldPoses(
                RigDef,
                targetRootXZ,
                currentHeadingDeg,
                finalLocalPoses,
                partMissingMask,
                worldPoses,
                1f,
                sectorsInitialized ? activeBoneSectors : ReadOnlySpan<GiantBoneViewSector>.Empty,
                -1,
                anyTurretRecoilActive && currentTurretRecoilOffsets != null
                    ? currentTurretRecoilOffsets
                    : ReadOnlySpan<Vector2>.Empty);

            if (!ManualOverrideActive)
            {
                worldPosesSolvedSerial = localPoseSerial;
                worldPosesSolvedRigDef = RigDef;
                worldPosesSolvedRootXZ = targetRootXZ;
                worldPosesSolvedHeading = currentHeadingDeg;
            }
            else
            {
                worldPosesSolvedSerial = 0u;
            }
        }

        public static IntVec3 GetEffectiveCell(Pawn pawn)
        {
            if (pawn == null)
            {
                return IntVec3.Invalid;
            }
            if (pawn.Spawned)
            {
                return pawn.Position;
            }
            if (pawn.Corpse != null && pawn.Corpse.Spawned)
            {
                return pawn.Corpse.Position;
            }
            return pawn.PositionHeld.IsValid ? pawn.PositionHeld : pawn.Position;
        }

        public static Vector3 GetSafeDrawPos(Pawn pawn)
        {
            if (pawn == null)
            {
                return Vector3.zero;
            }
            if (GiantMotionOverrideRegistry.TryGetActiveWorldPos(pawn, out Vector3 exactOverrideDrawPos))
            {
                return exactOverrideDrawPos;
            }
            if (pawn.Spawned && !pawn.Dead &&
                GiantFastLookup.TryGetRig(pawn) is CompGiantRig locoRig)
            {
                if (locoRig.VehicleLocomotion is VehicleLocomotionRuntime vehLoco &&
                    vehLoco.TryGetExactWorldPos(pawn, out Vector3 exactVehPos))
                {
                    return exactVehPos;
                }
                if (locoRig.FlightVehicleLocomotion is FlightVehicleLocomotionRuntime fvehLoco &&
                    fvehLoco.TryGetExactWorldPos(pawn, out Vector3 exactFvehPos))
                {
                    return exactFvehPos;
                }
            }
            if (pawn.Spawned && pawn.pather != null && pawn.Drawer != null)
            {
                return pawn.DrawPos;
            }
            if (pawn.Corpse != null && pawn.Corpse.Spawned)
            {
                return pawn.Corpse.DrawPos;
            }
            IntVec3 cell = GetEffectiveCell(pawn);
            return cell.IsValid ? cell.ToVector3Shifted() : Vector3.zero;
        }

        public static Vector3 GetSimTickPos(Pawn pawn)
        {
            if (pawn == null)
            {
                return Vector3.zero;
            }
            if (GiantFastLookup.TryGetRig(pawn) is CompGiantRig worldRig && worldRig.WorldSimulationActive)
            {
                return worldRig.WorldSimulationPosition;
            }
            if (GiantMotionOverrideRegistry.TryGetActiveWorldPos(pawn, out Vector3 exactOverrideSimPos))
            {
                return exactOverrideSimPos;
            }
            if (pawn.Spawned && !pawn.Dead &&
                GiantFastLookup.TryGetRig(pawn) is CompGiantRig locoRig)
            {
                if (locoRig.VehicleLocomotion is VehicleLocomotionRuntime vehLoco &&
                    vehLoco.TryGetExactWorldPos(pawn, out Vector3 exactVehPos))
                {
                    return exactVehPos;
                }
                if (locoRig.FlightVehicleLocomotion is FlightVehicleLocomotionRuntime fvehLoco &&
                    fvehLoco.TryGetExactWorldPos(pawn, out Vector3 exactFvehPos))
                {
                    return exactFvehPos;
                }
            }
            if (pawn.Spawned && pawn.pather != null && pawn.pather.MovingNow)
            {
                return Vector3.Lerp(
                    pawn.Position.ToVector3Shifted(),
                    pawn.pather.nextCell.ToVector3Shifted(),
                    Mathf.Clamp01(pawn.pather.MovePercentage));
            }
            if (pawn.Corpse != null && pawn.Corpse.Spawned)
            {
                return pawn.Corpse.DrawPos;
            }
            IntVec3 cell = GetEffectiveCell(pawn);
            return cell.IsValid ? cell.ToVector3Shifted() : Vector3.zero;
        }
    }
}
