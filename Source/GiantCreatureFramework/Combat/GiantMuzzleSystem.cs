using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public enum GiantEmitterFireMode
    {
        Alternating = 0,
        PrimaryWithFallback = 1,
        Salvo = 2
    }

    /// <summary>
    /// Named muzzle / VFX emitter socket authored on a single BoneNodeDef's 512x512 canvas.
    /// Allows a single turret, bridge, or pod part to host multiple distinct projectile or VFX ports.
    /// </summary>
    public class GiantBoneEmitterSocketDef
    {
        public string socketId;
        public Vector2 uv = new Vector2(0.5f, 0.88f);
        public Vector2 pixel = new Vector2(-9999f, -9999f);

        /// <summary>
        /// Clockwise local angle offset in degrees added to the bone's world angle (e.g. 180 for downward-drawn tail stingers).
        /// </summary>
        public float angleOffset = 0f;

        public GiantAnimVfxPreset vfxPreset = GiantAnimVfxPreset.MuzzleBlast;
        public float vfxScale = 1.0f;
        public Color vfxColor = new Color(0.38f, 0.92f, 1f, 0.9f);

        [Unsaved]
        public Vector2 resolvedUV = new Vector2(0.5f, 0.88f);

        public bool HasCustomPixel => pixel.x > -9000f;

        public void ResolveCoordinates(int textureResolution)
        {
            if (HasCustomPixel)
            {
                float res = Mathf.Max(1f, textureResolution);
                resolvedUV = new Vector2(pixel.x / res, 1f - (pixel.y / res));
            }
            else
            {
                resolvedUV = uv;
            }
        }
    }

    /// <summary>
    /// Individual barrel / emitter port entry inside a GiantWeaponEmitterDef group.
    /// </summary>
    public class GiantEmitterBarrelDef
    {
        public string boneId;
        public string socketId;
        public Vector2 uvOverride = new Vector2(-9999f, -9999f);
        public Vector2 pixelOverride = new Vector2(-9999f, -9999f);
        public float angleOffsetOverride = -9999f;
        public float vfxScaleMultiplier = 1.0f;

        public bool HasCustomUVOverride => uvOverride.x > -9000f;
        public bool HasCustomPixelOverride => pixelOverride.x > -9000f;
        public bool HasAngleOffsetOverride => angleOffsetOverride > -9000f;
    }

    /// <summary>
    /// Declarative multi-part / multi-barrel projectile & VFX emitter group on a GiantRigDef.
    /// Supports binding to specific weapons, abilities, or verb labels, alternating twin/quad barrels,
    /// automatic fallback when individual limb/turret parts are severed, and full disarming when all barrels are lost.
    /// </summary>
    public class GiantWeaponEmitterDef
    {
        public string emitterId;
        public string weaponDefName;
        public string abilityDefName;
        public string verbTag;
        public GiantEmitterFireMode fireMode = GiantEmitterFireMode.Alternating;
        public bool requireAllBonesIntact = false;
        public bool replaceVanillaMuzzleFlash = true;
        public bool spawnMuzzleVfxOnLaunch = true;
        public GiantAnimVfxPreset vfxPresetOverride = GiantAnimVfxPreset.None;
        public float vfxScale = 1.0f;
        public Color vfxColor = Color.clear;
        public List<GiantEmitterBarrelDef> barrels = new List<GiantEmitterBarrelDef>();

        public bool IsDefaultRangedEmitter =>
            string.IsNullOrEmpty(weaponDefName) &&
            string.IsNullOrEmpty(abilityDefName) &&
            string.IsNullOrEmpty(verbTag);
    }

    /// <summary>
    /// Resolved world-space projectile / VFX launch point for an active giant part barrel.
    /// </summary>
    public struct GiantMuzzleShotInfo
    {
        public bool IsValid;
        public string EmitterId;
        public int BoneIndex;
        public string BoneId;
        public string SocketId;
        public Vector2 ResolvedUV;
        public Vector3 WorldPos;
        public float WorldAngleDeg;
        public IntVec3 MapCell;
        public bool ReplaceVanillaMuzzleFlash;
        public bool SpawnMuzzleVfx;
        public GiantAnimVfxPreset VfxPreset;
        public float VfxScale;
        public Color VfxColor;
    }

    /// <summary>
    /// Thread-static scope active strictly during a Giant Creature's own Verb_LaunchProjectile.TryCastShot execution.
    /// Prevents external or child entities (such as airborne CarrierInterceptors attributing kills to their Carrier pawn)
    /// from having their projectile origins hijacked back to the mothership, while redirecting vanilla ShotFlash flecks
    /// to the exact articulated barrel muzzle.
    /// </summary>
    public static class ActiveGiantVerbShotContext
    {
        public struct ScopeState
        {
            public bool DidPush;
            public bool Active;
            public Pawn PrevCaster;
            public CompGiantRig PrevComp;
            public Verb PrevVerb;
            public GiantMuzzleShotInfo PrevShot;
            public bool PrevLaunched;
        }

        [ThreadStatic]
        private static bool isActive;

        [ThreadStatic]
        private static Pawn activeCaster;

        [ThreadStatic]
        private static CompGiantRig activeComp;

        [ThreadStatic]
        private static Verb activeVerb;

        [ThreadStatic]
        private static GiantMuzzleShotInfo activeShot;

        [ThreadStatic]
        private static bool hasLaunchedProjectile;

        public static CompGiantRig ActiveComp => isActive ? activeComp : null;

        public static ScopeState Push(Pawn caster, CompGiantRig comp, Verb verb, in GiantMuzzleShotInfo shot)
        {
            var prev = new ScopeState
            {
                DidPush = true,
                Active = isActive,
                PrevCaster = activeCaster,
                PrevComp = activeComp,
                PrevVerb = activeVerb,
                PrevShot = activeShot,
                PrevLaunched = hasLaunchedProjectile
            };

            isActive = true;
            activeCaster = caster;
            activeComp = comp;
            activeVerb = verb;
            activeShot = shot;
            hasLaunchedProjectile = false;
            return prev;
        }

        public static void Pop(in ScopeState prev)
        {
            if (!prev.DidPush)
            {
                return;
            }
            isActive = prev.Active;
            activeCaster = prev.PrevCaster;
            activeComp = prev.PrevComp;
            activeVerb = prev.PrevVerb;
            activeShot = prev.PrevShot;
            hasLaunchedProjectile = prev.PrevLaunched;
        }

        public static bool TryConsumeForLauncher(Thing launcher, out CompGiantRig comp, out Verb verb, out GiantMuzzleShotInfo shot)
        {
            if (isActive && activeShot.IsValid && launcher != null && launcher == activeCaster)
            {
                comp = activeComp;
                verb = activeVerb;
                shot = activeShot;
                hasLaunchedProjectile = true;
                return true;
            }

            comp = null;
            verb = null;
            shot = default;
            return false;
        }

        public static bool TryGetActiveFlashRedirect(out GiantMuzzleShotInfo shot)
        {
            if (isActive && activeShot.IsValid && (hasLaunchedProjectile || activeShot.ReplaceVanillaMuzzleFlash))
            {
                shot = activeShot;
                return true;
            }

            shot = default;
            return false;
        }
    }

    /// <summary>
    /// Deterministic resolver for custom part muzzles, multi-barrel alternation, limb-loss fallback, and emitter sockets.
    /// Operates cleanly in both live RimWorld maps and offline unit tests.
    /// </summary>
    public static class GiantMuzzleResolver
    {
        public static GiantWeaponEmitterDef MatchEmitter(
            GiantRigDef rig,
            string emitterId = null,
            string weaponDefName = null,
            string abilityDefName = null,
            string verbTag = null,
            bool allowDefaultFallback = true)
        {
            if (rig?.weaponEmitters == null || rig.weaponEmitters.Count == 0)
            {
                return null;
            }

            List<GiantWeaponEmitterDef> list = rig.weaponEmitters;

            if (!string.IsNullOrEmpty(emitterId))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    GiantWeaponEmitterDef em = list[i];
                    if (em != null && string.Equals(em.emitterId, emitterId, StringComparison.OrdinalIgnoreCase))
                    {
                        return em;
                    }
                }
            }

            if (!string.IsNullOrEmpty(abilityDefName))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    GiantWeaponEmitterDef em = list[i];
                    if (em != null && !string.IsNullOrEmpty(em.abilityDefName) &&
                        string.Equals(em.abilityDefName, abilityDefName, StringComparison.OrdinalIgnoreCase))
                    {
                        return em;
                    }
                }
            }

            if (!string.IsNullOrEmpty(weaponDefName))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    GiantWeaponEmitterDef em = list[i];
                    if (em != null && !string.IsNullOrEmpty(em.weaponDefName) &&
                        string.Equals(em.weaponDefName, weaponDefName, StringComparison.OrdinalIgnoreCase))
                    {
                        return em;
                    }
                }
            }

            if (!string.IsNullOrEmpty(verbTag))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    GiantWeaponEmitterDef em = list[i];
                    if (em != null && !string.IsNullOrEmpty(em.verbTag) &&
                        verbTag.IndexOf(em.verbTag, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return em;
                    }
                }
            }

            if (allowDefaultFallback)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    GiantWeaponEmitterDef em = list[i];
                    if (em != null && em.IsDefaultRangedEmitter)
                    {
                        return em;
                    }
                }
            }

            return null;
        }

        public static bool IsEmitterOperational(
            GiantRigDef rig,
            GiantWeaponEmitterDef emitter,
            Func<string, bool> isBoneIntact)
        {
            if (rig == null)
            {
                return true;
            }

            if (emitter != null && emitter.barrels != null && emitter.barrels.Count > 0)
            {
                if (emitter.requireAllBonesIntact)
                {
                    for (int i = 0; i < emitter.barrels.Count; i++)
                    {
                        GiantEmitterBarrelDef b = emitter.barrels[i];
                        if (b == null || string.IsNullOrEmpty(b.boneId) || !isBoneIntact(b.boneId))
                        {
                            return false;
                        }
                    }
                    return true;
                }

                for (int i = 0; i < emitter.barrels.Count; i++)
                {
                    GiantEmitterBarrelDef b = emitter.barrels[i];
                    if (b != null && !string.IsNullOrEmpty(b.boneId) && isBoneIntact(b.boneId))
                    {
                        return true;
                    }
                }
                return false;
            }

            if (!string.IsNullOrEmpty(rig.rangedWeaponBoneId))
            {
                return isBoneIntact(rig.rangedWeaponBoneId);
            }

            return true;
        }

        public static bool TryResolveBarrelWorldShot(
            GiantRigDef rig,
            ReadOnlySpan<BoneWorldPose> worldPoses,
            GiantWeaponEmitterDef emitter,
            GiantEmitterBarrelDef barrel,
            float projectileAltitudeY,
            Func<string, bool> isBoneIntact,
            out GiantMuzzleShotInfo shot)
        {
            shot = default;
            if (rig == null || barrel == null || string.IsNullOrEmpty(barrel.boneId))
            {
                return false;
            }

            if (!rig.TryGetNodeIndex(barrel.boneId, out int bIdx) ||
                (uint)bIdx >= (uint)worldPoses.Length ||
                !worldPoses[bIdx].visible)
            {
                return false;
            }

            if (isBoneIntact != null && !isBoneIntact(barrel.boneId))
            {
                return false;
            }

            BoneNodeDef bDef = rig.SortedNodes[bIdx];
            ref readonly BoneWorldPose pose = ref worldPoses[bIdx];
            bDef.ResolveEmitterSocketOrMuzzle(
                barrel.socketId,
                pose.activeSector,
                out Vector2 uv,
                out float angleOff,
                out GiantAnimVfxPreset preset,
                out float scale,
                out Color color);

            if (barrel.HasCustomPixelOverride)
            {
                float res = Mathf.Max(1f, bDef.textureResolution);
                uv = new Vector2(barrel.pixelOverride.x / res, 1f - (barrel.pixelOverride.y / res));
            }
            else if (barrel.HasCustomUVOverride)
            {
                uv = barrel.uvOverride;
            }

            if (barrel.HasAngleOffsetOverride)
            {
                angleOff = barrel.angleOffsetOverride;
            }

            scale *= Mathf.Max(0.1f, barrel.vfxScaleMultiplier);

            if (emitter != null)
            {
                if (emitter.vfxPresetOverride != GiantAnimVfxPreset.None)
                {
                    preset = emitter.vfxPresetOverride;
                }
                scale *= Mathf.Max(0.1f, emitter.vfxScale);
                if (emitter.vfxColor.a > 0.001f)
                {
                    color = emitter.vfxColor;
                }
            }

            Vector2 worldXZ = pose.UVToWorldXZ(uv.x, uv.y);
            float worldAngle = SkeletalKinematics.NormalizeAngle360(pose.worldAngleDeg + angleOff);
            Vector3 worldPos = new Vector3(worldXZ.x, projectileAltitudeY, worldXZ.y);

            shot = new GiantMuzzleShotInfo
            {
                IsValid = true,
                EmitterId = emitter?.emitterId,
                BoneIndex = bIdx,
                BoneId = barrel.boneId,
                SocketId = barrel.socketId,
                ResolvedUV = uv,
                WorldPos = worldPos,
                WorldAngleDeg = worldAngle,
                MapCell = worldPos.ToIntVec3(),
                ReplaceVanillaMuzzleFlash = emitter?.replaceVanillaMuzzleFlash ?? true,
                SpawnMuzzleVfx = emitter?.spawnMuzzleVfxOnLaunch ?? true,
                VfxPreset = preset,
                VfxScale = scale,
                VfxColor = color
            };
            return true;
        }

        public static bool TryResolveNextShotFromPoses(
            GiantRigDef rig,
            ReadOnlySpan<BoneWorldPose> worldPoses,
            GiantWeaponEmitterDef emitter,
            ref int barrelCursor,
            bool advanceCursor,
            float projectileAltitudeY,
            Func<string, bool> isBoneIntact,
            out GiantMuzzleShotInfo shot)
        {
            shot = default;
            if (rig == null || worldPoses.IsEmpty)
            {
                return false;
            }

            if (emitter != null && emitter.barrels != null && emitter.barrels.Count > 0)
            {
                if (emitter.requireAllBonesIntact && isBoneIntact != null && !IsEmitterOperational(rig, emitter, isBoneIntact))
                {
                    return false;
                }

                int count = emitter.barrels.Count;
                int startIdx = emitter.fireMode == GiantEmitterFireMode.Alternating
                    ? ((barrelCursor % count) + count) % count
                    : 0;

                for (int offset = 0; offset < count; offset++)
                {
                    int idx = (startIdx + offset) % count;
                    GiantEmitterBarrelDef cand = emitter.barrels[idx];
                    if (TryResolveBarrelWorldShot(rig, worldPoses, emitter, cand, projectileAltitudeY, isBoneIntact, out shot))
                    {
                        if (advanceCursor && emitter.fireMode == GiantEmitterFireMode.Alternating)
                        {
                            barrelCursor = (idx + 1) % count;
                        }
                        return true;
                    }
                }
                return false;
            }

            // Backward-compatible single-bone fallback via rangedWeaponBoneId
            if (!string.IsNullOrEmpty(rig.rangedWeaponBoneId))
            {
                var singleBarrel = new GiantEmitterBarrelDef { boneId = rig.rangedWeaponBoneId };
                return TryResolveBarrelWorldShot(rig, worldPoses, null, singleBarrel, projectileAltitudeY, isBoneIntact, out shot);
            }

            return false;
        }

        public static int CollectAllIntactBarrelShots(
            GiantRigDef rig,
            ReadOnlySpan<BoneWorldPose> worldPoses,
            GiantWeaponEmitterDef emitter,
            float projectileAltitudeY,
            Func<string, bool> isBoneIntact,
            List<GiantMuzzleShotInfo> outShots)
        {
            if (outShots == null)
            {
                return 0;
            }
            outShots.Clear();
            if (rig == null || worldPoses.IsEmpty)
            {
                return 0;
            }

            if (emitter != null && emitter.barrels != null && emitter.barrels.Count > 0)
            {
                if (emitter.requireAllBonesIntact && isBoneIntact != null && !IsEmitterOperational(rig, emitter, isBoneIntact))
                {
                    return 0;
                }
                for (int i = 0; i < emitter.barrels.Count; i++)
                {
                    if (TryResolveBarrelWorldShot(rig, worldPoses, emitter, emitter.barrels[i], projectileAltitudeY, isBoneIntact, out GiantMuzzleShotInfo s))
                    {
                        outShots.Add(s);
                    }
                }
                return outShots.Count;
            }

            if (!string.IsNullOrEmpty(rig.rangedWeaponBoneId))
            {
                var singleBarrel = new GiantEmitterBarrelDef { boneId = rig.rangedWeaponBoneId };
                if (TryResolveBarrelWorldShot(rig, worldPoses, null, singleBarrel, projectileAltitudeY, isBoneIntact, out GiantMuzzleShotInfo s))
                {
                    outShots.Add(s);
                }
            }
            return outShots.Count;
        }
    }
}
