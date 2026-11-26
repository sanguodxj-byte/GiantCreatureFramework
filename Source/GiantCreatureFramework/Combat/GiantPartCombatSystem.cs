using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace GiantCreatureFramework
{
    public enum GiantPartAttackKind
    {
        Melee = 0,
        Ranged = 1,
        CarrierCatapult = 2
    }

    public enum GiantPartArcReference
    {
        HullHeading = 0,
        BoneWorldAngle = 1
    }

    /// <summary>
    /// Declarative offensive module mounted directly on a skeletal BoneNodeDef.
    /// Replaces all root Pawn &lt;tools&gt; and equipped BaseGun weapons so every attack
    /// is executed, aimed, cooled down, and disarmed strictly per anatomical part.
    /// </summary>
    public class GiantPartAttackDef
    {
        public string attackId;
        public string label;
        public GiantPartAttackKind kind = GiantPartAttackKind.Melee;

        // Timing (seconds)
        public float warmupTime = 0.45f;
        public float cooldownTime = 2.2f;
        public float initialPhaseOffset01 = 0f;

        // Range & Firing/Strike Sector
        public float minRange = 0f;
        public float range = 3.6f;
        public GiantPartArcReference arcReference = GiantPartArcReference.HullHeading;
        public float sectorCenterDeg = 0f;
        public float sectorHalfAngleDeg = 180f;
        public bool requireLineOfSight = true;

        // Bone Origin / Strike Socket
        public string socketId;
        public string rangeOriginBoneId;
        public Vector2 strikeUV = new Vector2(0.5f, 0.85f);
        public Vector2 strikePixel = new Vector2(-9999f, -9999f);
        public bool useSoftWhipTip = false;
        public bool independentOverlay = false;

        // Ranged Projectile Parameters
        public ThingDef projectileDef;
        public int burstShotCount = 1;
        public int ticksBetweenBurstShots = 8;
        public float forcedMissRadius = 0f;
        public float accuracyTouch = 0.88f;
        public float accuracyShort = 0.84f;
        public float accuracyMedium = 0.76f;
        public float accuracyLong = 0.66f;
        public bool spawnMuzzleVfx = true;
        public GiantAnimVfxPreset vfxPresetOverride = GiantAnimVfxPreset.None;
        public float vfxScale = 1.0f;
        public Color vfxColor = Color.clear;
        public float recoilMultiplier = 1.0f;

        // Melee Strike Parameters
        public DamageDef damageDef;
        public float power = 30f;
        public float armorPenetration = 0.45f;
        public float cleaveRadius = 0f;
        public float cleaveDamageFactor = 0.45f;
        public float knockbackDistance = 0f;
        public int stunTicks = 0;
        public float selectionWeight = 1.0f;

        // Animation & Audio
        public GiantAnimDef attackAnim;
        public SoundDef soundCast;
        public SoundDef soundCastTail;
        public SoundDef soundHitPawn;
        public SoundDef soundHitBuilding;
        public SoundDef soundMiss;

        // Cockpit Operator & Aerial Strike Gates
        public bool requiresPrimaryWeaponOperator = true;
        public bool canStrikeGroundWhileFlying = false;

        public bool HasCustomStrikePixel => strikePixel.x > -9000f;

        public Vector2 ResolveStrikeUV(int texRes)
        {
            if (HasCustomStrikePixel)
            {
                float r = Mathf.Max(1f, texRes);
                return new Vector2(strikePixel.x / r, 1f - (strikePixel.y / r));
            }
            return strikeUV;
        }

        public float EvaluateDistanceAccuracy(float dist)
        {
            if (dist <= 3f)
            {
                return accuracyTouch;
            }
            if (dist <= 12f)
            {
                return Mathf.Lerp(accuracyTouch, accuracyShort, (dist - 3f) / 9f);
            }
            if (dist <= 25f)
            {
                return Mathf.Lerp(accuracyShort, accuracyMedium, (dist - 12f) / 13f);
            }
            if (dist <= 40f)
            {
                return Mathf.Lerp(accuracyMedium, accuracyLong, (dist - 25f) / 15f);
            }
            return accuracyLong;
        }

        public IEnumerable<string> ConfigErrors(string rigDefName, BoneNodeDef ownerBone)
        {
            string prefix = $"GiantPartAttackDef '{attackId ?? "<unnamed>"}' on bone '{ownerBone?.nodeId}' in GiantRigDef '{rigDefName}'";
            if (string.IsNullOrWhiteSpace(attackId))
            {
                yield return $"{prefix} is missing <attackId>.";
            }
            if (range <= 0.1f)
            {
                yield return $"{prefix} must have <range> > 0.1 (got {range}).";
            }
            if (minRange < 0f || minRange >= range)
            {
                yield return $"{prefix} has invalid minRange={minRange} vs range={range}.";
            }
            if (cooldownTime <= 0.05f)
            {
                yield return $"{prefix} must have positive <cooldownTime> (got {cooldownTime}).";
            }
            if (sectorHalfAngleDeg <= 0f || sectorHalfAngleDeg > 180.01f)
            {
                yield return $"{prefix} has invalid <sectorHalfAngleDeg>={sectorHalfAngleDeg} (expected (0, 180]).";
            }

            if (kind == GiantPartAttackKind.Ranged)
            {
                if (projectileDef == null)
                {
                    yield return $"{prefix} (Ranged) is missing <projectileDef>.";
                }
                if (burstShotCount < 1)
                {
                    yield return $"{prefix} (Ranged) must have <burstShotCount> >= 1.";
                }
                if (attackAnim == null)
                {
                    yield return $"{prefix} (Ranged) is missing mandatory non-looping <attackAnim>.";
                }
                else if (attackAnim.loop)
                {
                    yield return $"{prefix} (Ranged) <attackAnim> '{attackAnim.defName}' must have <loop>false</loop>.";
                }
            }
            else if (kind == GiantPartAttackKind.Melee)
            {
                if (damageDef == null)
                {
                    yield return $"{prefix} (Melee) is missing <damageDef>.";
                }
                if (power <= 0f)
                {
                    yield return $"{prefix} (Melee) must have positive <power> (got {power}).";
                }
                if (attackAnim == null)
                {
                    yield return $"{prefix} (Melee) is missing mandatory non-looping <attackAnim>.";
                }
                else
                {
                    if (attackAnim.loop)
                    {
                        yield return $"{prefix} (Melee) <attackAnim> '{attackAnim.defName}' must have <loop>false</loop>.";
                    }
                    if (attackAnim.events != null && attackAnim.events.Count > 0)
                    {
                        yield return $"{prefix} (Melee) <attackAnim> '{attackAnim.defName}' must have zero <events> (regular melee VFX strictly forbidden).";
                    }
                    if (independentOverlay && string.IsNullOrWhiteSpace(attackAnim.isolatedSubtreeRootBoneId))
                    {
                        yield return $"{prefix} sets <independentOverlay>true</independentOverlay>, so <attackAnim> '{attackAnim.defName}' must declare <isolatedSubtreeRootBoneId> to enforce zero influence on other body parts.";
                    }
                }
            }
        }
    }

    public enum PartWeaponPhase : byte
    {
        CooldownIdle = 0,
        WarmingUp = 1,
        FiringBurst = 2,
        FollowThrough = 3
    }

    public enum GiantForcedAttackMode : byte
    {
        Auto = 0,
        Ranged = 1,
        Melee = 2
    }

    [StaticConstructorOnStartup]
    internal static class GiantPartCombatGizmoAssets
    {
        public static readonly Texture2D FocusTargetIcon =
            ContentFinder<Texture2D>.Get("UI/Commands/Attack", reportFailure: false)
            ?? ContentFinder<Texture2D>.Get("UI/Icons/GCF_DebugGizmo", reportFailure: false)
            ?? BaseContent.BadTex;

        public static readonly Texture2D ClearTargetIcon =
            ContentFinder<Texture2D>.Get("UI/Commands/Halt", reportFailure: false)
            ?? TexCommand.ClearPrioritizedWork
            ?? BaseContent.BadTex;

        public static readonly Texture2D FireAtWillIcon =
            ContentFinder<Texture2D>.Get("UI/Commands/FireAtWill", reportFailure: false)
            ?? FocusTargetIcon
            ?? BaseContent.BadTex;
    }

    /// <summary>
    /// Precomputed rest-pose and impactTick world/local envelope for a melee GiantPartAttackDef
    /// solved at heading = 0 deg (North, +Z) and scale = 1.0.
    /// </summary>
    public struct MeleeStrikeImpactProfile
    {
        public bool Valid;
        public Vector2 RestLocalXZ;
        public Vector2 ImpactLocalXZ;
        public Vector2 AnchorRestLocalXZ;
        public float RestSpanFromAnchor;
        public float ImpactSpanFromAnchor;
        public float ImpactReachFromRoot;
        public float ImpactBearingDeg;
    }

    /// <summary>
    /// Runtime state for a single offensive BoneNodeDef and all GiantPartAttackDefs mounted on it.
    /// Different BonePartWeaponGroups execute concurrently, while multiple attacks on the same physical bone
    /// (e.g., horns/jaws/headbutt on Behemoth head, or ranged barrage + close sting on Scorpion tail_stinger)
    /// coordinate cleanly without overlapping on the same limb.
    /// </summary>
    public sealed class BonePartWeaponGroup
    {
        public readonly BoneNodeDef Bone;
        public readonly int BoneIndex;
        public readonly GiantPartAttackDef[] Attacks;
        public readonly MeleeStrikeImpactProfile[] StrikeProfiles;

        public PartWeaponPhase Phase;
        public GiantPartAttackDef ActiveAttack;
        public int ActiveAttackIndex = -1;
        public LocalTargetInfo CurrentTarget = LocalTargetInfo.Invalid;
        public Vector2 TargetWorldXZ;
        public bool HasTargetWorldXZ;

        public bool IsLookChainDriven;
        public bool IsTurret;
        public float MaxTurretAimAngleDeg = 180f;
        public int DefaultRangeAnchorBoneIndex = -1;
        public LocalTargetInfo LastTrackedTarget = LocalTargetInfo.Invalid;
        public Vector2 LastTrackedTargetXZ;
        public bool HasLastTrackedTargetXZ;
        public int TraverseHoldTicks;

        public int CooldownTicksLeft;
        public int WarmupTicksLeft;
        public int WarmupTicksTotal;
        public int BurstShotsRemaining;
        public int BurstShotTimer;

        public float AnimSampleTick;
        public float AnimDurationTicks;
        public float AnimImpactTick;
        public Vector2 ContactLungePeakLocalXZ;

        public GiantAnimDef FadeOutClip;
        public float FadeOutSampleTick;
        public float FadeOutImpactTick;
        public float FadeOutDurationTicks;
        public Vector2 FadeOutLungePeakLocalXZ;
        public float FadeOutStartEnv;
        public int FadeOutTicksLeft;
        public int FadeOutTicksTotal;

        public long TotalShotsOrStrikesFired;

        public BonePartWeaponGroup(BoneNodeDef bone, int boneIndex, List<GiantPartAttackDef> attacks)
        {
            Bone = bone;
            BoneIndex = boneIndex;
            Attacks = attacks.ToArray();
            StrikeProfiles = new MeleeStrikeImpactProfile[Attacks.Length];
            if (Attacks.Length > 0 && Attacks[0].initialPhaseOffset01 > 0.001f)
            {
                CooldownTicksLeft = Mathf.Max(1, Mathf.RoundToInt(Attacks[0].cooldownTime * 60f * Mathf.Clamp01(Attacks[0].initialPhaseOffset01)));
            }
        }

        public static float ComputeContactLungeBell(float sampleTick, float impactTick, float durationTicks)
        {
            float dur = Mathf.Max(2f, durationTicks);
            float imp = Mathf.Clamp(impactTick, 1f, dur - 0.5f);
            float t = Mathf.Clamp(sampleTick, 0f, dur);
            float u = t <= imp
                ? Mathf.Clamp01(t / imp)
                : Mathf.Clamp01((dur - t) / Mathf.Max(1f, dur - imp));
            return u * u * (3f - 2f * u);
        }

        public bool IsAnimating =>
            (Phase == PartWeaponPhase.WarmingUp || Phase == PartWeaponPhase.FiringBurst || Phase == PartWeaponPhase.FollowThrough)
            && ActiveAttack?.attackAnim != null;

        public bool IsFadingOut =>
            FadeOutTicksLeft > 0 && FadeOutClip != null;

        public bool HasActiveVisualOverlay =>
            IsAnimating || IsFadingOut;

        public static float ComputeClipEnvelope(GiantAnimDef clip, float sampleTick)
        {
            if (clip == null)
            {
                return 0f;
            }
            float dur = Mathf.Max(1f, clip.durationTicks);
            float t = Mathf.Clamp(sampleTick, 0f, dur);
            float fadeIn = Mathf.Clamp01(t / 4f);
            float fadeOut = Mathf.Clamp01((dur - t) / 5f);
            float env = Mathf.Min(fadeIn, fadeOut);
            return env * env * (3f - 2f * env);
        }

        public void StepFadeOut()
        {
            if (FadeOutTicksLeft > 0)
            {
                FadeOutTicksLeft--;
                if (FadeOutTicksLeft <= 0)
                {
                    FadeOutTicksLeft = 0;
                    FadeOutClip = null;
                    FadeOutLungePeakLocalXZ = Vector2.zero;
                    FadeOutStartEnv = 0f;
                }
            }
        }

        public void ClearTrackedTarget()
        {
            LastTrackedTarget = LocalTargetInfo.Invalid;
            HasLastTrackedTargetXZ = false;
        }

        public void AbortActiveCycle(int minResetCooldown = 6)
        {
            if (IsAnimating)
            {
                float curEnv = ComputeClipEnvelope(ActiveAttack.attackAnim, AnimSampleTick);
                if (curEnv > 0.02f)
                {
                    FadeOutClip = ActiveAttack.attackAnim;
                    FadeOutSampleTick = AnimSampleTick;
                    FadeOutImpactTick = AnimImpactTick;
                    FadeOutDurationTicks = AnimDurationTicks;
                    FadeOutLungePeakLocalXZ = ContactLungePeakLocalXZ;
                    FadeOutStartEnv = curEnv;
                    FadeOutTicksTotal = 8;
                    FadeOutTicksLeft = 8;
                }
            }
            Phase = PartWeaponPhase.CooldownIdle;
            ActiveAttack = null;
            ActiveAttackIndex = -1;
            ContactLungePeakLocalXZ = Vector2.zero;
            CurrentTarget = LocalTargetInfo.Invalid;
            HasTargetWorldXZ = false;
            WarmupTicksLeft = 0;
            BurstShotsRemaining = 0;
            BurstShotTimer = 0;
            AnimSampleTick = 0f;
            TraverseHoldTicks = 0;
            if (CooldownTicksLeft < minResetCooldown)
            {
                CooldownTicksLeft = minResetCooldown;
            }
        }
    }

    /// <summary>
    /// Per-giant runtime orchestrator for 100% part-driven ranged, melee, and carrier hangar combat.
    /// Zero-allocation in steady-state CompTick; supports concurrent multi-turret/multi-limb attacks while moving.
    /// </summary>
    public sealed class GiantPartCombatRuntime
    {
        private readonly CompGiantRig rigComp;
        private GiantRigDef activeRigDef;
        private BonePartWeaponGroup[] groups = Array.Empty<BonePartWeaponGroup>();

        private LocalTargetInfo forcedTarget = LocalTargetInfo.Invalid;
        private GiantForcedAttackMode forcedAttackMode = GiantForcedAttackMode.Auto;
        private bool forcedTargetStartedDowned;
        private bool fireAtWill = true;

        private readonly List<Thing> cachedHostileCandidates = new List<Thing>(32);
        private int lastCandidateScanTick = -999;
        private int groupEvalCursor;
        private BoneWorldPose[] livePoseScratch = Array.Empty<BoneWorldPose>();
        private BoneLocalPose[] clipEvalScratch = Array.Empty<BoneLocalPose>();
        private bool[] clipHasTrackMask = Array.Empty<bool>();
        private bool[] bezierChainBoneMask = Array.Empty<bool>();

        public IReadOnlyList<BonePartWeaponGroup> Groups => groups;
        public bool HasAnyPartWeapons => groups.Length > 0;
        public LocalTargetInfo ForcedTarget => forcedTarget;
        public GiantForcedAttackMode ForcedAttackMode => forcedAttackMode;

        public bool FireAtWill
        {
            get => fireAtWill;
            set
            {
                fireAtWill = value;
                Pawn ownerPawn = rigComp?.Pawn;
                if (ownerPawn?.drafter != null && ownerPawn.drafter.FireAtWill != value)
                {
                    ownerPawn.drafter.FireAtWill = value;
                }
                if (!value && !forcedTarget.IsValid)
                {
                    AbortRangedGroups(minCooldown: 4);
                }
                if (GiantFastLookup.TryGetCarrierHangar(ownerPawn) is CompCarrierHangar hangar)
                {
                    hangar.AutoLaunchEnabled = value;
                }
            }
        }

        public GiantPartCombatRuntime(CompGiantRig rigComp)
        {
            this.rigComp = rigComp;
            RebuildFromRig(rigComp?.RigDef);
        }

        public void ExposeData()
        {
            Scribe_TargetInfo.Look(ref forcedTarget, "gcf_partForcedTarget", LocalTargetInfo.Invalid);
            Scribe_Values.Look(ref forcedAttackMode, "gcf_partForcedAttackMode", GiantForcedAttackMode.Auto);
            Scribe_Values.Look(ref forcedTargetStartedDowned, "gcf_partForcedTargetStartedDowned", defaultValue: false);
            Scribe_Values.Look(ref fireAtWill, "gcf_partFireAtWill", defaultValue: true);
        }

        public void RebuildFromRig(GiantRigDef rig)
        {
            activeRigDef = rig;
            if (rig == null || rig.SortedNodes == null || rig.SortedNodes.Length == 0)
            {
                activeRigDef = null;
                groups = Array.Empty<BonePartWeaponGroup>();
                livePoseScratch = Array.Empty<BoneWorldPose>();
                clipEvalScratch = Array.Empty<BoneLocalPose>();
                clipHasTrackMask = Array.Empty<bool>();
                bezierChainBoneMask = Array.Empty<bool>();
                return;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            livePoseScratch = new BoneWorldPose[nodes.Length];
            clipEvalScratch = new BoneLocalPose[nodes.Length];
            clipHasTrackMask = new bool[nodes.Length];
            bezierChainBoneMask = new bool[nodes.Length];
            if (rig.bezierChains != null)
            {
                for (int c = 0; c < rig.bezierChains.Count; c++)
                {
                    int[] bIndices = rig.bezierChains[c]?.resolvedBoneIndices;
                    if (bIndices == null)
                    {
                        continue;
                    }
                    for (int k = 0; k < bIndices.Length; k++)
                    {
                        int bIdx = bIndices[k];
                        if ((uint)bIdx < (uint)nodes.Length)
                        {
                            bezierChainBoneMask[bIdx] = true;
                        }
                    }
                }
            }

            var lookBoneSet = new HashSet<int>();
            if (rig.steering?.lookChains != null)
            {
                for (int c = 0; c < rig.steering.lookChains.Count; c++)
                {
                    int[] indices = rig.steering.lookChains[c]?.resolvedBoneIndices;
                    if (indices == null)
                    {
                        continue;
                    }
                    for (int k = 0; k < indices.Length; k++)
                    {
                        if (indices[k] >= 0)
                        {
                            lookBoneSet.Add(indices[k]);
                        }
                    }
                }
            }
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef nd = nodes[i];
                if (nd != null && !nd.isRigid && nd.aimAtTargetWeight > 0.001f)
                {
                    lookBoneSet.Add(i);
                }
            }
            if (lookBoneSet.Count > 0)
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (nodes[i]?.parentRuntimeIndex >= 0 && lookBoneSet.Contains(nodes[i].parentRuntimeIndex))
                    {
                        lookBoneSet.Add(i);
                    }
                }
            }

            var built = new List<BonePartWeaponGroup>(8);
            for (int i = 0; i < nodes.Length; i++)
            {
                BoneNodeDef node = nodes[i];
                if (node?.partAttacks != null && node.partAttacks.Count > 0)
                {
                    var validList = new List<GiantPartAttackDef>(node.partAttacks.Count);
                    for (int a = 0; a < node.partAttacks.Count; a++)
                    {
                        if (node.partAttacks[a] != null)
                        {
                            validList.Add(node.partAttacks[a]);
                        }
                    }
                    if (validList.Count > 0)
                    {
                        bool isLookDriven = lookBoneSet.Contains(i);
                        int defaultAnchorIdx = -1;
                        for (int a = 0; a < validList.Count; a++)
                        {
                            if (!string.IsNullOrEmpty(validList[a].rangeOriginBoneId) &&
                                rig.TryGetNodeIndex(validList[a].rangeOriginBoneId, out int explicitOriginIdx))
                            {
                                defaultAnchorIdx = explicitOriginIdx;
                                break;
                            }
                        }
                        if (defaultAnchorIdx < 0 && rig.bezierChains != null)
                        {
                            for (int c = 0; c < rig.bezierChains.Count; c++)
                            {
                                int[] bIndices = rig.bezierChains[c]?.resolvedBoneIndices;
                                if (bIndices != null && bIndices.Length >= 2 && Array.IndexOf(bIndices, i) >= 0)
                                {
                                    defaultAnchorIdx = bIndices[0];
                                    break;
                                }
                            }
                        }
                        if (defaultAnchorIdx < 0 && i > 0 && !isLookDriven)
                        {
                            int walk = i;
                            int chainDepth = 0;
                            int proximalIdx = i;
                            while (walk > 0 && nodes[walk].parentRuntimeIndex > 0 && !nodes[nodes[walk].parentRuntimeIndex].isRigid)
                            {
                                walk = nodes[walk].parentRuntimeIndex;
                                proximalIdx = walk;
                                chainDepth++;
                            }
                            if (chainDepth >= 2)
                            {
                                defaultAnchorIdx = proximalIdx;
                            }
                        }

                        bool isTurret = rig.IsBoneInTurretSubtree(i, out float maxTurretArc);
                        var grp = new BonePartWeaponGroup(node, i, validList)
                        {
                            IsLookChainDriven = isLookDriven,
                            IsTurret = isTurret,
                            MaxTurretAimAngleDeg = isTurret ? maxTurretArc : 0f,
                            DefaultRangeAnchorBoneIndex = defaultAnchorIdx
                        };
                        built.Add(grp);
                    }
                }
            }
            groups = built.Count > 0 ? built.ToArray() : Array.Empty<BonePartWeaponGroup>();
            if (groups.Length > 0)
            {
                PrecomputeMeleeStrikeImpactProfiles(rig, nodes, groups);
            }
            lastCandidateScanTick = -999;
            cachedHostileCandidates.Clear();
        }

        private void PrecomputeMeleeStrikeImpactProfiles(GiantRigDef rig, BoneNodeDef[] nodes, BonePartWeaponGroup[] builtGroups)
        {
            int count = nodes.Length;
            var restLocal = new BoneLocalPose[count];
            var impactLocal = new BoneLocalPose[count];
            var emptyMissing = new bool[count];
            var restWorld = new BoneWorldPose[count];
            var impactWorld = new BoneWorldPose[count];
            for (int i = 0; i < count; i++)
            {
                restLocal[i] = BoneLocalPose.Identity;
            }
            SkeletalKinematics.SolveWorldPoses(rig, Vector2.zero, 0f, restLocal, emptyMissing, restWorld);

            for (int g = 0; g < builtGroups.Length; g++)
            {
                BonePartWeaponGroup grp = builtGroups[g];
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    GiantPartAttackDef atk = grp.Attacks[a];
                    if (atk == null || atk.kind != GiantPartAttackKind.Melee || (uint)grp.BoneIndex >= (uint)count)
                    {
                        continue;
                    }

                    Array.Copy(restLocal, impactLocal, count);
                    if (atk.attackAnim?.tracks != null && atk.attackAnim.tracks.Count > 0)
                    {
                        float impactTick = atk.attackAnim.ResolveImpactTick();
                        float env = BonePartWeaponGroup.ComputeClipEnvelope(atk.attackAnim, impactTick);
                        if (env > 0.001f)
                        {
                            ApplySingleClipOverlay(rig, nodes, count, isLocomoting: false, grp, atk.attackAnim, impactTick, env, isAbortedFade: false, impactLocal);
                        }
                    }

                    SkeletalKinematics.SolveWorldPoses(rig, Vector2.zero, 0f, impactLocal, emptyMissing, impactWorld);
                    Vector2 sUv = atk.ResolveStrikeUV(grp.Bone.textureResolution);
                    Vector2 restStrikeXZ = atk.useSoftWhipTip
                        ? GetUnprojectedJointWorldXZ(in restWorld[grp.BoneIndex])
                        : restWorld[grp.BoneIndex].UVToWorldXZFlat(sUv.x, sUv.y);
                    Vector2 impactStrikeXZ = atk.useSoftWhipTip
                        ? GetUnprojectedJointWorldXZ(in impactWorld[grp.BoneIndex])
                        : impactWorld[grp.BoneIndex].UVToWorldXZFlat(sUv.x, sUv.y);

                    int anchorIdx = -1;
                    string explicitOriginId = atk.rangeOriginBoneId;
                    if (!string.IsNullOrWhiteSpace(explicitOriginId) && rig.TryGetNodeIndex(explicitOriginId, out int expIdx))
                    {
                        anchorIdx = expIdx;
                    }
                    else if (grp.DefaultRangeAnchorBoneIndex >= 0)
                    {
                        anchorIdx = grp.DefaultRangeAnchorBoneIndex;
                    }

                    Vector2 anchorRestXZ = (uint)anchorIdx < (uint)count
                        ? GetUnprojectedJointWorldXZ(in restWorld[anchorIdx])
                        : restStrikeXZ;
                    Vector2 anchorImpactXZ = (uint)anchorIdx < (uint)count
                        ? GetUnprojectedJointWorldXZ(in impactWorld[anchorIdx])
                        : restStrikeXZ;
                    float reachFromRoot = impactStrikeXZ.magnitude;
                    bool isFullBodySpin = atk.attackAnim != null && atk.attackAnim.coupledBodyMotionFactor >= 0.99f;
                    float bearingDeg;
                    Vector2 profileImpactLocalXZ = impactStrikeXZ;
                    if (isFullBodySpin && reachFromRoot > 0.08f)
                    {
                        bearingDeg = SkeletalKinematics.NormalizeAngle360(atk.sectorCenterDeg);
                        profileImpactLocalXZ = RotateLocalToHeadingXZ(new Vector2(0f, reachFromRoot), bearingDeg);
                    }
                    else
                    {
                        bearingDeg = reachFromRoot > 0.08f
                            ? SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(impactStrikeXZ.x, impactStrikeXZ.y) * Mathf.Rad2Deg)
                            : atk.sectorCenterDeg;
                    }

                    grp.StrikeProfiles[a] = new MeleeStrikeImpactProfile
                    {
                        Valid = true,
                        RestLocalXZ = restStrikeXZ,
                        ImpactLocalXZ = profileImpactLocalXZ,
                        AnchorRestLocalXZ = anchorRestXZ,
                        RestSpanFromAnchor = Vector2.Distance(anchorRestXZ, restStrikeXZ),
                        ImpactSpanFromAnchor = Vector2.Distance(anchorImpactXZ, impactStrikeXZ),
                        ImpactReachFromRoot = reachFromRoot,
                        ImpactBearingDeg = bearingDeg
                    };
                }
            }
        }

        public bool HasActiveContactLunge
        {
            get
            {
                for (int g = 0; g < groups.Length; g++)
                {
                    BonePartWeaponGroup grp = groups[g];
                    if (grp.IsAnimating && grp.ContactLungePeakLocalXZ.sqrMagnitude > 0.0025f)
                    {
                        return true;
                    }
                    if (grp.IsFadingOut && grp.FadeOutLungePeakLocalXZ.sqrMagnitude > 0.0025f)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        public bool HasActiveMeleeStrikeOverlayEngaging(Pawn other)
        {
            if (other == null || groups.Length == 0)
            {
                return false;
            }
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!grp.HasActiveVisualOverlay)
                {
                    continue;
                }
                bool isMeleeClip = (grp.IsAnimating && grp.ActiveAttack?.kind == GiantPartAttackKind.Melee)
                    || grp.IsFadingOut;
                if (!isMeleeClip)
                {
                    continue;
                }
                if (!grp.CurrentTarget.IsValid)
                {
                    return true;
                }
                if (grp.CurrentTarget.HasThing && grp.CurrentTarget.Thing == other)
                {
                    return true;
                }
                IntVec3 targCell = grp.CurrentTarget.Cell;
                if (targCell.IsValid && (other.Position - targCell).LengthHorizontalSquared <= 16)
                {
                    return true;
                }
            }
            return false;
        }

        public bool TryGetPrimaryMeleeStrikeProfile(out MeleeStrikeImpactProfile profile)
        {
            profile = default;
            if (groups.Length == 0)
            {
                return false;
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(rigComp?.Pawn);
            float bestScore = -1f;
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (rigComp != null && !rigComp.IsBoneIntact(grp.Bone.nodeId))
                {
                    continue;
                }
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    GiantPartAttackDef atk = grp.Attacks[a];
                    if (atk == null || atk.kind != GiantPartAttackKind.Melee || atk.useSoftWhipTip || atk.independentOverlay)
                    {
                        continue;
                    }
                    if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                    {
                        continue;
                    }
                    if ((uint)a >= (uint)grp.StrikeProfiles.Length || !grp.StrikeProfiles[a].Valid)
                    {
                        continue;
                    }
                    MeleeStrikeImpactProfile candProf = grp.StrikeProfiles[a];
                    float dps = ComputeAttackTacticalDps(atk);
                    // Favor forward-facing primary jaws/horns/talons and longer-reaching frontal limbs for standoff geometry
                    bool isForward = Mathf.Abs(Mathf.DeltaAngle(0f, atk.sectorCenterDeg)) <= 75f;
                    float forwardBias = isForward ? 1.15f : 1.0f;
                    float reachBias = isForward
                        ? Mathf.Lerp(0.92f, 1.22f, Mathf.Clamp01((candProf.ImpactReachFromRoot - 2.0f) / 3.8f))
                        : 1.0f;
                    float score = dps * forwardBias * reachBias;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        profile = candProf;
                    }
                }
            }
            return bestScore > 0f;
        }

        public bool EvaluateCanopyPerimeterMeleeAtCell(
            Vector2 candRootXZ,
            Vector2 targetCenterXZ,
            float bearingToTarget,
            Thing target,
            float maxLungeContactGap,
            out float bestHeadingDeg,
            out float bestFleshGap,
            out float bestReachableDps)
        {
            bestHeadingDeg = bearingToTarget;
            bestFleshGap = 99f;
            bestReachableDps = 0f;
            if (groups.Length == 0 || target == null)
            {
                return false;
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(rigComp?.Pawn);
            float bestHeadingUtility = float.MinValue;

            // Evaluate direct face-target heading first, then each intact primary melee attack's natural strike bearing
            int totalCandidates = 1;
            for (int g = 0; g < groups.Length; g++)
            {
                totalCandidates += groups[g].Attacks.Length;
            }

            for (int candIdx = 0; candIdx < totalCandidates; candIdx++)
            {
                float testHeading;
                if (candIdx == 0)
                {
                    testHeading = bearingToTarget;
                }
                else
                {
                    int rem = candIdx - 1;
                    BonePartWeaponGroup srcGrp = null;
                    int srcAtkIdx = -1;
                    for (int g = 0; g < groups.Length; g++)
                    {
                        if (rem < groups[g].Attacks.Length)
                        {
                            srcGrp = groups[g];
                            srcAtkIdx = rem;
                            break;
                        }
                        rem -= groups[g].Attacks.Length;
                    }
                    if (srcGrp == null
                        || (rigComp != null && !rigComp.IsBoneIntact(srcGrp.Bone.nodeId))
                        || (uint)srcAtkIdx >= (uint)srcGrp.StrikeProfiles.Length
                        || !srcGrp.StrikeProfiles[srcAtkIdx].Valid)
                    {
                        continue;
                    }
                    GiantPartAttackDef srcAtk = srcGrp.Attacks[srcAtkIdx];
                    if (srcAtk == null || srcAtk.kind != GiantPartAttackKind.Melee || srcAtk.independentOverlay)
                    {
                        continue;
                    }
                    testHeading = SkeletalKinematics.NormalizeAngle360(bearingToTarget - srcGrp.StrikeProfiles[srcAtkIdx].ImpactBearingDeg);
                    if (Mathf.Abs(Mathf.DeltaAngle(testHeading, bearingToTarget)) <= 4f)
                    {
                        continue;
                    }
                }

                float sumDps = 0f;
                float minFleshGap = 99f;

                for (int g = 0; g < groups.Length; g++)
                {
                    BonePartWeaponGroup grp = groups[g];
                    if (rigComp != null && !rigComp.IsBoneIntact(grp.Bone.nodeId))
                    {
                        continue;
                    }
                    float grpBestDps = 0f;
                    float grpBestGap = 99f;

                    for (int a = 0; a < grp.Attacks.Length; a++)
                    {
                        GiantPartAttackDef atk = grp.Attacks[a];
                        if (atk == null || atk.kind != GiantPartAttackKind.Melee || atk.independentOverlay)
                        {
                            continue;
                        }
                        if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                        {
                            continue;
                        }
                        if (rigComp != null && !GiantFlightUtility.CanAirborneAttackerMeleeTarget(rigComp, atk, target))
                        {
                            continue;
                        }

                        bool hasProf = (uint)a < (uint)grp.StrikeProfiles.Length && grp.StrikeProfiles[a].Valid;
                        Vector2 anchorWorldXZ = hasProf
                            ? candRootXZ + RotateLocalToHeadingXZ(grp.StrikeProfiles[a].AnchorRestLocalXZ, testHeading)
                            : candRootXZ;
                        Vector2 d = targetCenterXZ - anchorWorldXZ;
                        float distFromAnchor = Mathf.Max(0f, d.magnitude - 0.35f);
                        if (distFromAnchor > atk.range || distFromAnchor < atk.minRange)
                        {
                            continue;
                        }

                        if (atk.sectorHalfAngleDeg < 179.9f && d.sqrMagnitude > 0.04f)
                        {
                            float bDeg = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
                            float secCenter = SkeletalKinematics.NormalizeAngle360(testHeading + atk.sectorCenterDeg);
                            if (Mathf.Abs(Mathf.DeltaAngle(secCenter, bDeg)) > atk.sectorHalfAngleDeg)
                            {
                                continue;
                            }
                        }

                        float fGap = 0.1f;
                        bool inBezier = (uint)grp.BoneIndex < (uint)bezierChainBoneMask.Length && bezierChainBoneMask[grp.BoneIndex];
                        bool isFullSpin = atk.attackAnim != null && atk.attackAnim.coupledBodyMotionFactor >= 0.99f;
                        if (!atk.useSoftWhipTip && !inBezier && !isFullSpin && hasProf)
                        {
                            MeleeStrikeImpactProfile prof = grp.StrikeProfiles[a];
                            Vector2 nominalImpactXZ = candRootXZ + RotateLocalToHeadingXZ(prof.ImpactLocalXZ, testHeading);
                            Vector2 aimFromAnchor = grp.IsLookChainDriven
                                ? SteerVectorTowardsXZ(nominalImpactXZ - anchorWorldXZ, targetCenterXZ - anchorWorldXZ, maxTurnDeg: 28f)
                                : (nominalImpactXZ - anchorWorldXZ);
                            Vector2 predictedStrikeXZ = anchorWorldXZ + aimFromAnchor;
                            fGap = MeasurePointDistanceToTargetVisualFlesh(predictedStrikeXZ, target, targetRig: null);
                            if (fGap > maxLungeContactGap)
                            {
                                continue;
                            }
                        }

                        float dps = ComputeAttackTacticalDps(atk);
                        if (dps > grpBestDps)
                        {
                            grpBestDps = dps;
                        }
                        if (fGap < grpBestGap)
                        {
                            grpBestGap = fGap;
                        }
                    }

                    sumDps += grpBestDps;
                    if (grpBestGap < minFleshGap)
                    {
                        minFleshGap = grpBestGap;
                    }
                }

                float facePenalty = Mathf.Abs(Mathf.DeltaAngle(testHeading, bearingToTarget)) * 0.04f;
                float utility = sumDps - Mathf.Min(6f, minFleshGap) * 4.5f - facePenalty;
                if (utility > bestHeadingUtility)
                {
                    bestHeadingUtility = utility;
                    bestHeadingDeg = testHeading;
                    bestFleshGap = minFleshGap;
                    bestReachableDps = sumDps;
                }
            }

            return bestReachableDps > 0.01f;
        }

        public static Vector2 RotateLocalToHeadingXZ(Vector2 localNorthXZ, float headingDeg)
        {
            float rad = headingDeg * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);
            return new Vector2(
                localNorthXZ.x * cos + localNorthXZ.y * sin,
                -localNorthXZ.x * sin + localNorthXZ.y * cos);
        }

        public static Vector2 InverseRotateHeadingToLocalXZ(Vector2 worldDeltaXZ, float headingDeg)
        {
            float rad = headingDeg * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);
            return new Vector2(
                worldDeltaXZ.x * cos - worldDeltaXZ.y * sin,
                worldDeltaXZ.x * sin + worldDeltaXZ.y * cos);
        }

        public static Vector2 SteerVectorTowardsXZ(Vector2 vec, Vector2 desiredDir, float maxTurnDeg)
        {
            if (maxTurnDeg <= 0.01f || vec.sqrMagnitude < 1e-4f || desiredDir.sqrMagnitude < 1e-4f)
            {
                return vec;
            }
            float curDeg = Mathf.Atan2(vec.x, vec.y) * Mathf.Rad2Deg;
            float desDeg = Mathf.Atan2(desiredDir.x, desiredDir.y) * Mathf.Rad2Deg;
            float clampedTurn = Mathf.Clamp(Mathf.DeltaAngle(curDeg, desDeg), -maxTurnDeg, maxTurnDeg);
            float newRad = (curDeg + clampedTurn) * Mathf.Deg2Rad;
            float len = vec.magnitude;
            return new Vector2(Mathf.Sin(newRad) * len, Mathf.Cos(newRad) * len);
        }

        public static float MeasurePointDistanceToTargetVisualFlesh(Vector2 worldPtXZ, Thing target, CompGiantRig targetRig)
        {
            if (targetRig != null)
            {
                IReadOnlyList<IntVec3> fleshCells = targetRig.Footprint.pawnExclusionCells.Count > 0
                    ? targetRig.Footprint.pawnExclusionCells
                    : targetRig.Footprint.hitCellPositions;
                HashSet<IntVec3> fleshSet = targetRig.Footprint.pawnExclusionCells.Count > 0
                    ? targetRig.Footprint.pawnExclusionCellSet
                    : targetRig.Footprint.hitCellSet;
                if (fleshCells != null && fleshCells.Count > 0)
                {
                    IntVec3 ptCell = new IntVec3(Mathf.FloorToInt(worldPtXZ.x), 0, Mathf.FloorToInt(worldPtXZ.y));
                    if (fleshSet != null && fleshSet.Contains(ptCell))
                    {
                        return 0f;
                    }
                    float bestSq = float.MaxValue;
                    for (int i = 0; i < fleshCells.Count; i++)
                    {
                        IntVec3 fc = fleshCells[i];
                        float cx = Mathf.Clamp(worldPtXZ.x, fc.x, fc.x + 1f);
                        float cz = Mathf.Clamp(worldPtXZ.y, fc.z, fc.z + 1f);
                        float dx = worldPtXZ.x - cx;
                        float dz = worldPtXZ.y - cz;
                        float dSq = dx * dx + dz * dz;
                        if (dSq < bestSq)
                        {
                            bestSq = dSq;
                            if (bestSq <= 0.0001f)
                            {
                                return 0f;
                            }
                        }
                    }
                    return Mathf.Sqrt(bestSq);
                }
            }

            if (target == null)
            {
                return 0f;
            }
            CellRect rect = target.OccupiedRect();
            if (rect.Width <= 1 && rect.Height <= 1)
            {
                Vector3 tp = target is Pawn p ? CompGiantRig.GetSimTickPos(p) : target.DrawPos;
                return Mathf.Max(0f, Vector2.Distance(worldPtXZ, new Vector2(tp.x, tp.z)) - 0.35f);
            }
            float rx = Mathf.Clamp(worldPtXZ.x, rect.minX, rect.maxX + 1f);
            float rz = Mathf.Clamp(worldPtXZ.y, rect.minZ, rect.maxZ + 1f);
            return Vector2.Distance(worldPtXZ, new Vector2(rx, rz));
        }

        public void SetForcedTarget(LocalTargetInfo target, GiantForcedAttackMode mode = GiantForcedAttackMode.Auto)
        {
            if (!target.IsValid || (target.HasThing && (target.Thing.Destroyed || !target.Thing.Spawned)))
            {
                ClearForcedTarget();
                return;
            }

            forcedTarget = target;
            forcedAttackMode = mode;
            forcedTargetStartedDowned = target.Thing is Pawn initPawn && initPawn.Downed;
            Pawn pawn = rigComp?.Pawn;
            if (pawn != null && pawn.mindState != null && target.HasThing)
            {
                pawn.mindState.enemyTarget = target.Thing;
            }
            if (mode == GiantForcedAttackMode.Melee)
            {
                AbortRangedGroups(minCooldown: 4);
            }
            else if (GiantFastLookup.TryGetCarrierHangar(pawn) is CompCarrierHangar hangar)
            {
                hangar.CommandAttackTarget(target, overdriveDurationTicks: 0, immediateBurstCount: 2);
            }
        }

        public void ClearForcedTarget()
        {
            forcedTarget = LocalTargetInfo.Invalid;
            forcedAttackMode = GiantForcedAttackMode.Auto;
            forcedTargetStartedDowned = false;
        }

        public bool HasValidForcedTarget()
        {
            if (!forcedTarget.IsValid)
            {
                forcedAttackMode = GiantForcedAttackMode.Auto;
                forcedTargetStartedDowned = false;
                return false;
            }
            if (forcedTarget.HasThing)
            {
                Thing t = forcedTarget.Thing;
                if (t == null || t.Destroyed || !t.Spawned || t.Map != rigComp?.Pawn?.Map)
                {
                    ClearForcedTarget();
                    return false;
                }
                if (t is Pawn tp)
                {
                    if (tp.Dead)
                    {
                        ClearForcedTarget();
                        return false;
                    }
                    if (tp.Downed && !forcedTargetStartedDowned &&
                        (forcedAttackMode == GiantForcedAttackMode.Ranged || forcedAttackMode == GiantForcedAttackMode.Melee))
                    {
                        ClearForcedTarget();
                        return false;
                    }
                }
            }
            return true;
        }

        public bool HasAnyConfiguredRangedOrCarrier()
        {
            Pawn pawn = rigComp?.Pawn;
            if (pawn != null && GiantFastLookup.TryGetCarrierHangar(pawn) is CompCarrierHangar hangar && hangar.MaxAllowedInterceptors > 0)
            {
                return true;
            }
            for (int g = 0; g < groups.Length; g++)
            {
                GiantPartAttackDef[] atks = groups[g].Attacks;
                for (int a = 0; a < atks.Length; a++)
                {
                    if (atks[a].kind == GiantPartAttackKind.Ranged || atks[a].kind == GiantPartAttackKind.CarrierCatapult)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool HasAnyConfiguredMelee()
        {
            for (int g = 0; g < groups.Length; g++)
            {
                GiantPartAttackDef[] atks = groups[g].Attacks;
                for (int a = 0; a < atks.Length; a++)
                {
                    if (atks[a].kind == GiantPartAttackKind.Melee)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private readonly List<IAttackTarget> candidateScanScratch = new List<IAttackTarget>(128);

        public void InvalidateHostileCandidateScan()
        {
            lastCandidateScanTick = -99999;
        }

        public void NotifyCollisionHostileTarget(Pawn blockerPawn)
        {
            if (blockerPawn == null || blockerPawn.Dead || !blockerPawn.Spawned)
            {
                return;
            }
            if (!HasValidForcedTarget() && rigComp?.Pawn != null && IsValidHostileThing(rigComp.Pawn, blockerPawn))
            {
                if (rigComp.Pawn.mindState != null)
                {
                    rigComp.Pawn.mindState.enemyTarget = blockerPawn;
                }
                lastCandidateScanTick = -999;
            }
        }
        public int CountIntactWeaponGroups()
        {
            int count = 0;
            for (int i = 0; i < groups.Length; i++)
            {
                if (rigComp.IsBoneIntact(groups[i].Bone.nodeId))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsGroupIndependentOverlayOnly(BonePartWeaponGroup grp)
        {
            if (grp?.Attacks == null || grp.Attacks.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < grp.Attacks.Length; i++)
            {
                if (!grp.Attacks[i].independentOverlay)
                {
                    return false;
                }
            }
            return true;
        }

        private bool HasAnyIntactNonIndependentAttack(CompGiantCockpit cockpit)
        {
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
                {
                    continue;
                }
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    GiantPartAttackDef atk = grp.Attacks[a];
                    if (atk.independentOverlay)
                    {
                        continue;
                    }
                    if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                    {
                        continue;
                    }
                    return true;
                }
            }
            return false;
        }

        public bool GetTacticalRangeCapabilities(
            out bool hasIntactRanged,
            out float minPreferredStandoff,
            out float maxIntactRange,
            out bool hasIntactMelee,
            out float maxIntactMeleeRange)
        {
            hasIntactRanged = false;
            minPreferredStandoff = 0f;
            maxIntactRange = 0f;
            hasIntactMelee = false;
            maxIntactMeleeRange = 0f;

            Pawn pawn = rigComp?.Pawn;
            CompGiantCockpit cockpit = pawn != null ? GiantFastLookup.TryGetCockpit(pawn) : null;
            if (cockpit != null && cockpit.IsDormantUnmanned)
            {
                return false;
            }

            bool hasPrimary = HasAnyIntactNonIndependentAttack(cockpit);

            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
                {
                    continue;
                }

                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    GiantPartAttackDef atk = grp.Attacks[a];
                    if (hasPrimary && atk.independentOverlay)
                    {
                        continue;
                    }
                    if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                    {
                        continue;
                    }

                    if (atk.kind == GiantPartAttackKind.Ranged || atk.kind == GiantPartAttackKind.CarrierCatapult)
                    {
                        hasIntactRanged = true;
                        if (atk.range > maxIntactRange)
                        {
                            maxIntactRange = atk.range;
                            minPreferredStandoff = Mathf.Max(atk.minRange + 1.5f, atk.range * 0.58f);
                        }
                    }
                    else if (atk.kind == GiantPartAttackKind.Melee)
                    {
                        hasIntactMelee = true;
                        if (atk.range > maxIntactMeleeRange)
                        {
                            maxIntactMeleeRange = atk.range;
                        }
                    }
                }
            }

            if (GiantFastLookup.TryGetCarrierHangar(pawn) is CompCarrierHangar hangar && hangar.MaxAllowedInterceptors > 0)
            {
                hasIntactRanged = true;
                maxIntactRange = Mathf.Max(maxIntactRange, hangar.Props.engagementRange);
                minPreferredStandoff = Mathf.Max(minPreferredStandoff, hangar.Props.engagementRange * 0.65f);
            }

            return hasIntactRanged || hasIntactMelee;
        }

        public float GetMaxOperationalRange()
        {
            GetTacticalRangeCapabilities(out _, out _, out float maxRanged, out _, out float maxMelee);
            return Mathf.Max(maxRanged, maxMelee);
        }

        public bool IsTargetCurrentlyEngageableByPrimaryParts(Thing target, bool checkSector = false)
        {
            if (target == null || !target.Spawned || rigComp?.Pawn == null || !rigComp.Pawn.Spawned || target.Map != rigComp.Pawn.Map)
            {
                return false;
            }
            if (!rigComp.SolveLiveWorldPoses(livePoseScratch))
            {
                return false;
            }

            if (GiantFastLookup.TryGetCarrierHangar(rigComp.Pawn) is CompCarrierHangar hangar && hangar.MaxAllowedInterceptors > 0)
            {
                float d = rigComp.Pawn.Position.DistanceTo(target.Position);
                if (d <= hangar.Props.engagementRange)
                {
                    return true;
                }
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(rigComp.Pawn);
            bool hasPrimary = HasAnyIntactNonIndependentAttack(cockpit);

            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
                {
                    continue;
                }
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    GiantPartAttackDef atk = grp.Attacks[a];
                    if (hasPrimary && atk.independentOverlay)
                    {
                        continue;
                    }
                    if (CanPartAttackReachAndSeeTarget(grp, atk, target, checkSector: checkSector))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool IsTargetCurrentlyEngageableByIndependentOverlay(Thing target)
        {
            if (target == null || !target.Spawned || rigComp?.Pawn == null || !rigComp.Pawn.Spawned || target.Map != rigComp.Pawn.Map)
            {
                return false;
            }
            if (!rigComp.SolveLiveWorldPoses(livePoseScratch))
            {
                return false;
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(rigComp.Pawn);
            if (cockpit != null && cockpit.IsDormantUnmanned)
            {
                return false;
            }

            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
                {
                    continue;
                }
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    GiantPartAttackDef atk = grp.Attacks[a];
                    if (!atk.independentOverlay)
                    {
                        continue;
                    }
                    if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                    {
                        continue;
                    }
                    if (CanPartAttackReachAndSeeTarget(grp, atk, target, checkSector: true))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool IsAnyIndependentOverlayWarmingUpOnTarget(Thing target)
        {
            if (target == null || !target.Spawned)
            {
                return false;
            }
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!IsGroupIndependentOverlayOnly(grp) || (rigComp != null && !rigComp.IsBoneIntact(grp.Bone.nodeId)))
                {
                    continue;
                }
                if ((grp.Phase == PartWeaponPhase.WarmingUp || grp.Phase == PartWeaponPhase.FiringBurst)
                    && grp.CurrentTarget.HasThing
                    && grp.CurrentTarget.Thing == target)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsFullBodyChoreographyMeleeClip(BonePartWeaponGroup grp, GiantPartAttackDef atk)
        {
            if (grp == null || atk == null || atk.kind != GiantPartAttackKind.Melee || grp.IsTurret || atk.independentOverlay)
            {
                return false;
            }
            GiantAnimDef clip = atk.attackAnim;
            if (clip == null
                || !string.IsNullOrWhiteSpace(clip.isolatedSubtreeRootBoneId)
                || !string.IsNullOrWhiteSpace(clip.actingSubtreeRootBoneId))
            {
                return false;
            }
            return true;
        }

        private bool ClipsShareNonRootBodyTracks(GiantAnimDef a, GiantAnimDef b)
        {
            if (a?.tracks == null || b?.tracks == null || activeRigDef == null)
            {
                return false;
            }
            for (int i = 0; i < a.tracks.Count; i++)
            {
                string nodeA = a.tracks[i]?.nodeId;
                if (string.IsNullOrEmpty(nodeA) || !activeRigDef.TryGetNodeIndex(nodeA, out int idxA) || idxA <= 0)
                {
                    continue;
                }
                if ((uint)idxA < (uint)bezierChainBoneMask.Length && bezierChainBoneMask[idxA])
                {
                    continue;
                }
                for (int j = 0; j < b.tracks.Count; j++)
                {
                    if (string.Equals(nodeA, b.tracks[j]?.nodeId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool HasConflictingActiveFullBodyMeleeClip(BonePartWeaponGroup candidateGrp, GiantPartAttackDef candidateAtk)
        {
            if (!IsFullBodyChoreographyMeleeClip(candidateGrp, candidateAtk))
            {
                return false;
            }
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup other = groups[g];
                if (other == candidateGrp || !other.IsAnimating || other.ActiveAttack == null)
                {
                    continue;
                }
                if (!IsFullBodyChoreographyMeleeClip(other, other.ActiveAttack))
                {
                    continue;
                }
                if (ClipsShareNonRootBodyTracks(other.ActiveAttack.attackAnim, candidateAtk.attackAnim))
                {
                    return true;
                }
            }
            return false;
        }

        private int CountActiveNonIsolatedOverlays()
        {
            int c = 0;
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!grp.HasActiveVisualOverlay || IsGroupIndependentOverlayOnly(grp))
                {
                    continue;
                }
                GiantAnimDef clip = grp.IsAnimating ? grp.ActiveAttack?.attackAnim : grp.FadeOutClip;
                if (clip != null && string.IsNullOrWhiteSpace(clip.isolatedSubtreeRootBoneId))
                {
                    c++;
                }
            }
            return c;
        }

        public void TickPartCombat(int ticksGame)
        {
            Pawn pawn = rigComp?.Pawn;
            GiantRigDef rig = rigComp?.RigDef;
            if (pawn == null || !pawn.Spawned || pawn.Map == null || rig == null || groups.Length == 0)
            {
                return;
            }

            for (int g = 0; g < groups.Length; g++)
            {
                groups[g].StepFadeOut();
            }

            if (pawn.Dead || pawn.Downed)
            {
                AbortAllGroups();
                return;
            }

            CompGiantPhaseTransform phaseComp = GiantFastLookup.TryGetPhaseTransform(pawn);
            if (phaseComp != null && phaseComp.IsTransforming)
            {
                AbortAllGroups();
                return;
            }

            if (rigComp.CurrentAnimState == GiantAnimState.MorphLocked)
            {
                AbortAllGroups();
                return;
            }

            // Pause full-body part attacks during an active signature Ability cast warmup/execution;
            // during full-body displacement, abort all part weapon groups including overlays so passing
            // corridor targets never hijack head gaze or overwrite charge clips.
            if (GiantMotionOverrideRegistry.HasActiveWeaponSuppressingSequence(pawn))
            {
                AbortAllGroups(minCooldown: 6, includeIndependentOverlays: true);
                return;
            }

            bool inSignatureAbilityCast = (pawn.stances?.curStance is Stance_Busy busy && busy.verb is IAbilityVerb)
                || CompAbilityEffect_AbyssalQuake.HasPendingCrossingStrike(pawn)
                || rigComp.IsSoftWhipStrikeActive;
            if (inSignatureAbilityCast)
            {
                AbortAllGroups(minCooldown: 4, includeIndependentOverlays: false);
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(pawn);
            if (cockpit != null && cockpit.IsDormantUnmanned)
            {
                AbortAllGroups();
                return;
            }

            HasValidForcedTarget();

            if (!fireAtWill && !forcedTarget.IsValid)
            {
                AbortRangedGroups(minCooldown: 4);
                GetTacticalRangeCapabilities(out _, out _, out _, out bool anyIntactMelee, out _);
                if (!anyIntactMelee)
                {
                    AbortAllGroups(minCooldown: 4);
                    return;
                }
            }

            int scanInterval = cachedHostileCandidates.Count == 0 ? 15 : 10;
            if (lastCandidateScanTick < 0 || ticksGame - lastCandidateScanTick >= scanInterval)
            {
                RefreshHostileCandidates(pawn, ticksGame);
            }

            bool hasActivePrey = TryGetActivePredatorHuntPrey(pawn, out Pawn huntedPrey);
            if (hasActivePrey && pawn.pather != null && !pawn.pather.Moving)
            {
                GiantPartCombatUtility.SetFacingToward(pawn, huntedPrey.Position);
            }

            if (cachedHostileCandidates.Count == 0 && !forcedTarget.IsValid && pawn.mindState?.enemyTarget == null && !hasActivePrey)
            {
                bool anyActiveCycle = false;
                for (int i = 0; i < groups.Length; i++)
                {
                    if (groups[i].Phase != PartWeaponPhase.CooldownIdle)
                    {
                        anyActiveCycle = true;
                        break;
                    }
                }
                if (!anyActiveCycle)
                {
                    for (int i = 0; i < groups.Length; i++)
                    {
                        if (groups[i].CooldownTicksLeft > 0)
                        {
                            groups[i].CooldownTicksLeft--;
                        }
                        groups[i].ClearTrackedTarget();
                    }
                    return;
                }
            }

            if (!rigComp.SolveLiveWorldPoses(livePoseScratch))
            {
                return;
            }

            float reloadMult = cockpit != null ? Mathf.Clamp(cockpit.ComputeReloadSpeedMultiplier(), 0.25f, 3.0f) : 1f;
            float accMult = cockpit != null ? Mathf.Clamp(cockpit.ComputeWeaponAccuracyMultiplier(), 0.20f, 2.5f) : 1f;
            if (rigComp.VehicleLocomotion is VehicleLocomotionRuntime vehLoco)
            {
                if (!vehLoco.CanFireWhileMoving(cockpit))
                {
                    AbortAllGroups(minCooldown: 4);
                    return;
                }
                accMult *= vehLoco.CurrentMovingFireAccuracyMultiplier;
            }
            float meleeDmgFactor = pawn.GetStatValue(StatDefOf.MeleeDamageFactor, applyPostProcess: true);

            int groupCount = groups.Length;
            int startIdx = groupCount > 0 ? ((groupEvalCursor % groupCount) + groupCount) % groupCount : 0;
            for (int offset = 0; offset < groupCount; offset++)
            {
                int g = (startIdx + offset) % groupCount;
                if (inSignatureAbilityCast && !IsGroupIndependentOverlayOnly(groups[g]))
                {
                    continue;
                }
                TickSingleGroup(groups[g], pawn, cockpit, reloadMult, accMult, meleeDmgFactor);
            }
        }

        private void AbortRangedGroups(int minCooldown = 6)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                BonePartWeaponGroup grp = groups[i];
                if (grp.Phase != PartWeaponPhase.CooldownIdle &&
                    grp.ActiveAttack != null &&
                    grp.ActiveAttack.kind != GiantPartAttackKind.Melee)
                {
                    grp.AbortActiveCycle(minCooldown);
                }
                bool anyMeleeOnGroup = false;
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    if (grp.Attacks[a].kind == GiantPartAttackKind.Melee)
                    {
                        anyMeleeOnGroup = true;
                        break;
                    }
                }
                if (!anyMeleeOnGroup)
                {
                    grp.ClearTrackedTarget();
                }
            }
        }

        private void AbortAllGroups(int minCooldown = 6, bool includeIndependentOverlays = true)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                if (!includeIndependentOverlays && IsGroupIndependentOverlayOnly(groups[i]))
                {
                    continue;
                }
                if (groups[i].Phase != PartWeaponPhase.CooldownIdle)
                {
                    groups[i].AbortActiveCycle(minCooldown);
                }
                groups[i].ClearTrackedTarget();
            }
        }

        private void RefreshGroupTrackedLookTarget(BonePartWeaponGroup grp, Pawn pawn)
        {
            if (grp == null || !grp.IsLookChainDriven || pawn == null || pawn.Map == null)
            {
                return;
            }

            float maxGroupRange = 0f;
            for (int i = 0; i < grp.Attacks.Length; i++)
            {
                if (grp.Attacks[i].range > maxGroupRange)
                {
                    maxGroupRange = grp.Attacks[i].range;
                }
            }
            float keepRange = Mathf.Max(8f, maxGroupRange + 4f);
            bool isIndep = IsGroupIndependentOverlayOnly(grp);
            bool hasForced = HasValidForcedTarget();
            Vector3 selfPos = CompGiantRig.GetSimTickPos(pawn);
            Vector2 mountXZ = grp.Attacks.Length > 0
                ? GetBoneOriginWorldXZ(grp, grp.Attacks[0], out _)
                : new Vector2(selfPos.x, selfPos.z);

            if (grp.LastTrackedTarget.IsValid)
            {
                if (grp.LastTrackedTarget.HasThing)
                {
                    Thing t = grp.LastTrackedTarget.Thing;
                    bool isForcedTracked = hasForced && forcedTarget.HasThing && t == forcedTarget.Thing;
                    bool isHuntedPrey = TryGetActivePredatorHuntPrey(pawn, out Pawn trackedPrey) && t == trackedPrey;
                    if (isForcedTracked)
                    {
                        if (!IsValidManualFocusTarget(pawn, t))
                        {
                            grp.ClearTrackedTarget();
                        }
                    }
                    else if ((!isHuntedPrey && !IsValidHostileThing(pawn, t)) || (!isHuntedPrey && t is Pawn tp && tp.Downed))
                    {
                        grp.ClearTrackedTarget();
                    }
                    if (grp.LastTrackedTarget.IsValid)
                    {
                        Vector3 wp = t is Pawn tp2 ? CompGiantRig.GetSimTickPos(tp2) : t.DrawPos;
                        Vector2 wxz = new Vector2(wp.x, wp.z);
                        bool inRangeAndSector = Vector2.Distance(mountXZ, wxz) <= keepRange;
                        if (inRangeAndSector && !hasForced && grp.Attacks.Length > 0)
                        {
                            inRangeAndSector = false;
                            for (int a = 0; a < grp.Attacks.Length; a++)
                            {
                                if (CanPartAttackReachAndSeeTarget(grp, grp.Attacks[a], t, checkSector: true, extraRangeSlack: 6f))
                                {
                                    inRangeAndSector = true;
                                    break;
                                }
                            }
                        }
                        if (!inRangeAndSector)
                        {
                            grp.ClearTrackedTarget();
                        }
                        else
                        {
                            grp.LastTrackedTargetXZ = wxz;
                            grp.HasLastTrackedTargetXZ = true;
                        }
                    }
                }
                else if (!grp.LastTrackedTarget.Cell.InBounds(pawn.Map))
                {
                    grp.ClearTrackedTarget();
                }
            }

            if (!grp.HasLastTrackedTargetXZ && grp.IsLookChainDriven && (fireAtWill || hasForced))
            {
                Thing cand = hasForced && forcedTarget.HasThing
                    ? forcedTarget.Thing
                    : (isIndep ? null : pawn.mindState?.enemyTarget);
                if (cand != null && (isIndep || !hasForced))
                {
                    bool inLookGroupSector = false;
                    for (int a = 0; a < grp.Attacks.Length; a++)
                    {
                        if (CanPartAttackReachAndSeeTarget(grp, grp.Attacks[a], cand, checkSector: true, extraRangeSlack: 6f))
                        {
                            inLookGroupSector = true;
                            break;
                        }
                    }
                    if (!inLookGroupSector)
                    {
                        cand = null;
                    }
                }
                if (cand == null && fireAtWill && cachedHostileCandidates.Count > 0)
                {
                    float bestDistSq = keepRange * keepRange;
                    for (int cIdx = 0; cIdx < cachedHostileCandidates.Count; cIdx++)
                    {
                        Thing h = cachedHostileCandidates[cIdx];
                        if (!IsValidHostileThing(pawn, h) || (h is Pawn hp && hp.Downed))
                        {
                            continue;
                        }
                        Vector3 hpPos = h is Pawn hp2 ? CompGiantRig.GetSimTickPos(hp2) : h.DrawPos;
                        Vector2 hpXZ = new Vector2(hpPos.x, hpPos.z);
                        float dSq = (hpXZ - mountXZ).sqrMagnitude;
                        if (dSq >= bestDistSq)
                        {
                            continue;
                        }
                        bool reachable = false;
                        for (int a = 0; a < grp.Attacks.Length; a++)
                        {
                            if (CanPartAttackReachAndSeeTarget(grp, grp.Attacks[a], h, checkSector: true))
                            {
                                reachable = true;
                                break;
                            }
                        }
                        if (reachable)
                        {
                            bestDistSq = dSq;
                            cand = h;
                        }
                    }
                }
                bool candIsForced = hasForced && forcedTarget.HasThing && cand == forcedTarget.Thing;
                if (cand != null && (candIsForced ? IsValidManualFocusTarget(pawn, cand) : (IsValidHostileThing(pawn, cand) && !(cand is Pawn cp && cp.Downed))))
                {
                    Vector3 wp = cand is Pawn tp ? CompGiantRig.GetSimTickPos(tp) : cand.DrawPos;
                    Vector2 wxz = new Vector2(wp.x, wp.z);
                    if (Vector2.Distance(mountXZ, wxz) <= keepRange)
                    {
                        grp.LastTrackedTarget = new LocalTargetInfo(cand);
                        grp.LastTrackedTargetXZ = wxz;
                        grp.HasLastTrackedTargetXZ = true;
                    }
                }
            }
        }

        private void TickSingleGroup(
            BonePartWeaponGroup grp,
            Pawn pawn,
            CompGiantCockpit cockpit,
            float reloadMult,
            float accMult,
            float meleeDmgFactor)
        {
            if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
            {
                if (grp.Phase != PartWeaponPhase.CooldownIdle)
                {
                    grp.AbortActiveCycle();
                }
                grp.ClearTrackedTarget();
                return;
            }

            if (grp.CooldownTicksLeft > 0)
            {
                grp.CooldownTicksLeft--;
            }

            switch (grp.Phase)
            {
                case PartWeaponPhase.FollowThrough:
                {
                    RefreshGroupTrackedLookTarget(grp, pawn);
                    float prev = grp.AnimSampleTick;
                    grp.AnimSampleTick += 1f;
                    if (grp.ActiveAttack?.attackAnim != null && grp.ActiveAttack.kind != GiantPartAttackKind.Melee)
                    {
                        rigComp.EvaluateClipEvents(grp.ActiveAttack.attackAnim, prev, grp.AnimSampleTick, isLoop: false);
                    }
                    if (grp.AnimSampleTick >= grp.AnimDurationTicks)
                    {
                        grp.Phase = PartWeaponPhase.CooldownIdle;
                        grp.ActiveAttack = null;
                        grp.ActiveAttackIndex = -1;
                        grp.ContactLungePeakLocalXZ = Vector2.zero;
                        grp.HasTargetWorldXZ = false;
                    }
                    if (grp.Phase != PartWeaponPhase.CooldownIdle)
                    {
                        return;
                    }
                    break;
                }

                case PartWeaponPhase.WarmingUp:
                {
                    GiantPartAttackDef atk = grp.ActiveAttack;
                    if (atk == null ||
                        (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons) ||
                        !IsTargetStillValidForWarmup(grp, atk, pawn))
                    {
                        grp.AbortActiveCycle(8);
                        return;
                    }

                    UpdateTargetWorldXZ(grp);
                    if (grp.WarmupTicksLeft > 2)
                    {
                        UpdateDynamicMeleeContactLunge(grp, atk, pawn, smoothExisting: true);
                    }
                    grp.WarmupTicksLeft--;
                    float p01 = grp.WarmupTicksTotal > 0
                        ? Mathf.Clamp01(1f - (float)grp.WarmupTicksLeft / grp.WarmupTicksTotal)
                        : 1f;
                    float prevAnim = grp.AnimSampleTick;
                    grp.AnimSampleTick = p01 * grp.AnimImpactTick;
                    if (atk.attackAnim != null && atk.kind != GiantPartAttackKind.Melee && grp.AnimSampleTick > prevAnim)
                    {
                        rigComp.EvaluateClipEvents(atk.attackAnim, prevAnim, grp.AnimSampleTick, isLoop: false);
                    }

                    if (grp.WarmupTicksLeft <= 0)
                    {
                        if (atk.kind == GiantPartAttackKind.Ranged &&
                            grp.IsLookChainDriven &&
                            grp.HasTargetWorldXZ)
                        {
                            Vector2 muzzleXZ = GetBoneOriginWorldXZ(grp, atk, out float barrelWorldDeg);
                            if (atk.arcReference != GiantPartArcReference.BoneWorldAngle &&
                                (uint)grp.BoneIndex < (uint)livePoseScratch.Length &&
                                livePoseScratch[grp.BoneIndex].visible)
                            {
                                float mAngOff = 0f;
                                if (!string.IsNullOrEmpty(atk.socketId))
                                {
                                    grp.Bone.ResolveEmitterSocketOrMuzzle(atk.socketId, livePoseScratch[grp.BoneIndex].activeSector, out _, out mAngOff, out _, out _, out _);
                                }
                                barrelWorldDeg = SkeletalKinematics.NormalizeAngle360(livePoseScratch[grp.BoneIndex].worldAngleDeg + mAngOff);
                            }
                            Vector2 d = grp.TargetWorldXZ - muzzleXZ;
                            if (d.sqrMagnitude > 0.09f)
                            {
                                float aimDeg = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
                                float alignErr = Mathf.Abs(Mathf.DeltaAngle(barrelWorldDeg, aimDeg));
                                if (alignErr > 8f)
                                {
                                    if (grp.TraverseHoldTicks < 90)
                                    {
                                        grp.TraverseHoldTicks++;
                                        grp.WarmupTicksLeft = 1;
                                        return;
                                    }
                                    if (alignErr > 18f)
                                    {
                                        grp.AbortActiveCycle(10);
                                        return;
                                    }
                                }
                            }
                        }
                        ExecutePartAttackImpactOrFirstShot(grp, pawn, reloadMult, accMult, meleeDmgFactor);
                    }
                    return;
                }

                case PartWeaponPhase.FiringBurst:
                {
                    GiantPartAttackDef atk = grp.ActiveAttack;
                    if (atk == null || (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons))
                    {
                        grp.AbortActiveCycle(10);
                        return;
                    }

                    float prevAnim = grp.AnimSampleTick;
                    float stepTicks = atk.kind == GiantPartAttackKind.Melee ? 1.0f : 0.65f;
                    grp.AnimSampleTick = Mathf.Min(grp.AnimDurationTicks, grp.AnimSampleTick + stepTicks);
                    if (atk.attackAnim != null && atk.kind != GiantPartAttackKind.Melee && grp.AnimSampleTick > prevAnim)
                    {
                        rigComp.EvaluateClipEvents(atk.attackAnim, prevAnim, grp.AnimSampleTick, isLoop: false);
                    }

                    if (!IsTargetStillAliveAndInMap(grp.CurrentTarget, pawn.Map))
                    {
                        BeginFollowThroughAndCooldown(grp, atk, reloadMult);
                        return;
                    }

                    UpdateTargetWorldXZ(grp);
                    grp.BurstShotTimer--;
                    if (grp.BurstShotTimer <= 0)
                    {
                        if (atk.kind == GiantPartAttackKind.Melee)
                        {
                            ExecutePartMeleeStrike(grp, atk, pawn, accMult, meleeDmgFactor);
                        }
                        else
                        {
                            FireSinglePartProjectile(grp, atk, pawn, accMult);
                        }
                        grp.TotalShotsOrStrikesFired++;
                        grp.BurstShotsRemaining--;
                        if (grp.BurstShotsRemaining <= 0)
                        {
                            BeginFollowThroughAndCooldown(grp, atk, reloadMult);
                        }
                        else
                        {
                            grp.BurstShotTimer = Mathf.Max(2, atk.ticksBetweenBurstShots);
                        }
                    }
                    return;
                }
            }

            // Phase == CooldownIdle: keep turret/head tracking target while reloading and try to acquire once cooldown reaches 0
            int curTick = Find.TickManager?.TicksGame ?? 0;
            if (!grp.HasLastTrackedTargetXZ || ((curTick + grp.BoneIndex) & 3) == 0)
            {
                RefreshGroupTrackedLookTarget(grp, pawn);
            }
            if (grp.CooldownTicksLeft > 0)
            {
                return;
            }

            if (TrySelectAttackAndTarget(grp, cockpit, out GiantPartAttackDef chosenAtk, out LocalTargetInfo chosenTarg))
            {
                if (chosenAtk.kind == GiantPartAttackKind.CarrierCatapult)
                {
                    if (GiantFastLookup.TryGetCarrierHangar(pawn) is CompCarrierHangar hangar &&
                        hangar.MaxAllowedInterceptors > 0 &&
                        hangar.DockedReadyCount > 0)
                    {
                        hangar.CommandAttackTarget(chosenTarg, overdriveDurationTicks: 0, immediateBurstCount: 1);
                        grp.TotalShotsOrStrikesFired++;
                    }
                    grp.CooldownTicksLeft = Mathf.Max(15, Mathf.RoundToInt(chosenAtk.cooldownTime * 60f / reloadMult));
                    return;
                }

                grp.ActiveAttack = chosenAtk;
                grp.ActiveAttackIndex = Array.IndexOf(grp.Attacks, chosenAtk);
                grp.CurrentTarget = chosenTarg;
                UpdateTargetWorldXZ(grp);
                grp.WarmupTicksTotal = Mathf.Max(4, Mathf.RoundToInt(chosenAtk.warmupTime * 60f / reloadMult));
                grp.WarmupTicksLeft = grp.WarmupTicksTotal;
                grp.TraverseHoldTicks = 0;
                grp.AnimDurationTicks = chosenAtk.attackAnim != null ? Mathf.Max(6f, chosenAtk.attackAnim.durationTicks) : 20f;
                grp.AnimImpactTick = chosenAtk.attackAnim != null ? chosenAtk.attackAnim.ResolveImpactTick() : grp.AnimDurationTicks * 0.45f;
                grp.AnimSampleTick = 0f;
                grp.ContactLungePeakLocalXZ = Vector2.zero;
                grp.Phase = PartWeaponPhase.WarmingUp;
                UpdateDynamicMeleeContactLunge(grp, chosenAtk, pawn, smoothExisting: false);
                if (IsFullBodyChoreographyMeleeClip(grp, chosenAtk) && groups.Length > 1)
                {
                    groupEvalCursor = (Array.IndexOf(groups, grp) + 1) % groups.Length;
                }
            }
            else if (Current.Game != null)
            {
                grp.CooldownTicksLeft = 4;
            }
        }

        private void ExecutePartAttackImpactOrFirstShot(
            BonePartWeaponGroup grp,
            Pawn pawn,
            float reloadMult,
            float accMult,
            float meleeDmgFactor)
        {
            GiantPartAttackDef atk = grp.ActiveAttack;
            if (atk == null)
            {
                grp.AbortActiveCycle();
                return;
            }

            float prevAnim = grp.AnimSampleTick;
            grp.AnimSampleTick = grp.AnimImpactTick;
            if (atk.attackAnim != null && atk.kind != GiantPartAttackKind.Melee && grp.AnimSampleTick > prevAnim)
            {
                rigComp.EvaluateClipEvents(atk.attackAnim, prevAnim, grp.AnimSampleTick, isLoop: false);
            }

            if (atk.kind == GiantPartAttackKind.Ranged)
            {
                FireSinglePartProjectile(grp, atk, pawn, accMult);
                grp.TotalShotsOrStrikesFired++;
                int remaining = atk.burstShotCount - 1;
                if (remaining > 0)
                {
                    grp.Phase = PartWeaponPhase.FiringBurst;
                    grp.BurstShotsRemaining = remaining;
                    grp.BurstShotTimer = Mathf.Max(2, atk.ticksBetweenBurstShots);
                }
                else
                {
                    BeginFollowThroughAndCooldown(grp, atk, reloadMult);
                }
            }
            else if (atk.kind == GiantPartAttackKind.Melee)
            {
                ExecutePartMeleeStrike(grp, atk, pawn, accMult, meleeDmgFactor);
                grp.TotalShotsOrStrikesFired++;
                int remaining = atk.burstShotCount - 1;
                if (remaining > 0)
                {
                    grp.Phase = PartWeaponPhase.FiringBurst;
                    grp.BurstShotsRemaining = remaining;
                    grp.BurstShotTimer = Mathf.Max(2, atk.ticksBetweenBurstShots);
                }
                else
                {
                    BeginFollowThroughAndCooldown(grp, atk, reloadMult);
                }
            }
            else
            {
                BeginFollowThroughAndCooldown(grp, atk, reloadMult);
            }
        }

        private static void BeginFollowThroughAndCooldown(BonePartWeaponGroup grp, GiantPartAttackDef atk, float reloadMult)
        {
            grp.CooldownTicksLeft = Mathf.Max(8, Mathf.RoundToInt(atk.cooldownTime * 60f / reloadMult));
            grp.BurstShotsRemaining = 0;
            grp.BurstShotTimer = 0;
            if (atk.attackAnim != null && grp.AnimSampleTick < grp.AnimDurationTicks - 1f)
            {
                grp.Phase = PartWeaponPhase.FollowThrough;
            }
            else
            {
                grp.Phase = PartWeaponPhase.CooldownIdle;
                grp.ActiveAttack = null;
                grp.ActiveAttackIndex = -1;
                grp.ContactLungePeakLocalXZ = Vector2.zero;
                grp.HasTargetWorldXZ = false;
            }
        }

        public void UpdateDynamicMeleeContactLunge(BonePartWeaponGroup grp, GiantPartAttackDef atk, Pawn pawn, bool smoothExisting)
        {
            GiantRigDef rig = rigComp?.RigDef;
            if (grp == null || atk == null || pawn == null || rig == null
                || !rig.enableDynamicMeleeContactLunge
                || atk.kind != GiantPartAttackKind.Melee
                || atk.useSoftWhipTip
                || atk.independentOverlay
                || grp.IsTurret
                || (pawn.pather != null && pawn.pather.Moving))
            {
                grp.ContactLungePeakLocalXZ = Vector2.zero;
                return;
            }
            if ((uint)grp.BoneIndex < (uint)bezierChainBoneMask.Length && bezierChainBoneMask[grp.BoneIndex])
            {
                grp.ContactLungePeakLocalXZ = Vector2.zero;
                return;
            }
            if (atk.attackAnim != null &&
                (!string.IsNullOrWhiteSpace(atk.attackAnim.isolatedSubtreeRootBoneId) || atk.attackAnim.coupledBodyMotionFactor >= 0.99f))
            {
                grp.ContactLungePeakLocalXZ = Vector2.zero;
                return;
            }

            int atkIdx = grp.ActiveAttackIndex >= 0 && grp.ActiveAttackIndex < grp.Attacks.Length && grp.Attacks[grp.ActiveAttackIndex] == atk
                ? grp.ActiveAttackIndex
                : Array.IndexOf(grp.Attacks, atk);
            if ((uint)atkIdx >= (uint)grp.StrikeProfiles.Length || !grp.StrikeProfiles[atkIdx].Valid || !grp.CurrentTarget.HasThing)
            {
                grp.ContactLungePeakLocalXZ = Vector2.zero;
                return;
            }

            Thing target = grp.CurrentTarget.Thing;
            if (target == null || !target.Spawned)
            {
                grp.ContactLungePeakLocalXZ = Vector2.zero;
                return;
            }

            MeleeStrikeImpactProfile prof = grp.StrikeProfiles[atkIdx];
            float curHeading = rigComp.CurrentHeadingDeg;
            Vector3 selfSim = CompGiantRig.GetSimTickPos(pawn);
            Vector2 rootXZ = new Vector2(selfSim.x, selfSim.z);
            Vector2 anchorWorldXZ = rootXZ + RotateLocalToHeadingXZ(prof.AnchorRestLocalXZ, curHeading);
            Vector2 nominalImpactXZ = rootXZ + RotateLocalToHeadingXZ(prof.ImpactLocalXZ, curHeading);

            Vector3 targSim = target is Pawn tp ? CompGiantRig.GetSimTickPos(tp) : target.DrawPos;
            Vector2 targetCenterXZ = new Vector2(targSim.x, targSim.z);
            Vector2 aimFromAnchor = grp.IsLookChainDriven
                ? SteerVectorTowardsXZ(nominalImpactXZ - anchorWorldXZ, targetCenterXZ - anchorWorldXZ, maxTurnDeg: 28f)
                : (nominalImpactXZ - anchorWorldXZ);
            Vector2 unlungedStrikeXZ = anchorWorldXZ + aimFromAnchor;

            Vector2 toTarget = targetCenterXZ - anchorWorldXZ;
            Vector2 attackDir = toTarget.sqrMagnitude > 0.04f
                ? toTarget.normalized
                : RotateLocalToHeadingXZ(new Vector2(0f, 1f), curHeading);

            CompGiantRig targetRig = GiantFastLookup.TryGetRig(target as Pawn);
            Vector2 desiredStrikeXZ;
            if (targetRig != null)
            {
                targetRig.EnsureAlphaMasksLoaded();
                Vector3 segStart = new Vector3(anchorWorldXZ.x, 0f, anchorWorldXZ.y);
                Vector3 segEnd = new Vector3(targetCenterXZ.x, 0f, targetCenterXZ.y);
                if (!targetRig.AlphaMasksSpan.IsEmpty &&
                    targetRig.Footprint.TryHitTestSegment(
                        targetRig.RigDef,
                        targetRig.AlphaMasksSpan,
                        segStart,
                        segEnd,
                        out _,
                        out Vector3 hitWorld,
                        targetRig.SoftWhipAlphaMasksSpan))
                {
                    desiredStrikeXZ = new Vector2(hitWorld.x, hitWorld.z) + attackDir * 0.18f;
                }
                else
                {
                    float fleshGap = MeasurePointDistanceToTargetVisualFlesh(unlungedStrikeXZ, target, targetRig);
                    desiredStrikeXZ = unlungedStrikeXZ + attackDir * (fleshGap + 0.12f);
                }
            }
            else if (target is Pawn)
            {
                // Crisp edge touch against 1x1 pawn without sliding the giant's sprite over top of the colonist
                desiredStrikeXZ = targetCenterXZ - attackDir * 0.24f;
            }
            else
            {
                CellRect bRect = target.OccupiedRect();
                Vector2 edgeXZ = new Vector2(
                    Mathf.Clamp(anchorWorldXZ.x, bRect.minX, bRect.maxX + 1f),
                    Mathf.Clamp(anchorWorldXZ.y, bRect.minZ, bRect.maxZ + 1f));
                desiredStrikeXZ = edgeXZ + attackDir * 0.15f;
            }

            float maxLunge = Mathf.Clamp(rig.maxDynamicMeleeContactLungeCells, 0f, 2.0f);
            float forwardShift = Mathf.Clamp(Vector2.Dot(desiredStrikeXZ - unlungedStrikeXZ, attackDir), -maxLunge, maxLunge);
            Vector2 worldShift = attackDir * forwardShift;
            Vector2 targetLocal = InverseRotateHeadingToLocalXZ(worldShift, curHeading);
            grp.ContactLungePeakLocalXZ = smoothExisting
                ? Vector2.Lerp(grp.ContactLungePeakLocalXZ, targetLocal, 0.35f)
                : targetLocal;
        }

        private bool TrySelectAttackAndTarget(
            BonePartWeaponGroup grp,
            CompGiantCockpit cockpit,
            out GiantPartAttackDef chosenAttack,
            out LocalTargetInfo chosenTarget)
        {
            chosenAttack = null;
            chosenTarget = LocalTargetInfo.Invalid;

            // Priority 1: Player-assigned B (Ranged) / H (Melee) / Auto Focus Target
            if (forcedTarget.IsValid && forcedTarget.HasThing && IsValidManualFocusTarget(rigComp.Pawn, forcedTarget.Thing))
            {
                if (forcedAttackMode == GiantForcedAttackMode.Melee)
                {
                    if (TryPickWeightedAttackForTarget(grp, cockpit, forcedTarget.Thing, out chosenAttack, preferMeleeOnly: true))
                    {
                        chosenTarget = forcedTarget;
                        return true;
                    }
                    if (!IsGroupIndependentOverlayOnly(grp))
                    {
                        return false;
                    }
                }
                else if (forcedAttackMode == GiantForcedAttackMode.Ranged)
                {
                    if (TryPickWeightedAttackForTarget(grp, cockpit, forcedTarget.Thing, out chosenAttack, preferRangedOnly: true))
                    {
                        chosenTarget = forcedTarget;
                        return true;
                    }
                    if (!grp.IsTurret && !IsGroupIndependentOverlayOnly(grp))
                    {
                        return false;
                    }
                }
                else
                {
                    if (TryPickWeightedAttackForTarget(grp, cockpit, forcedTarget.Thing, out chosenAttack))
                    {
                        chosenTarget = forcedTarget;
                        return true;
                    }
                    // Non-turret body/limb/tail parts (grp.IsTurret == false && !independentOverlay) ordered to Focus-Attack
                    // a target must never behave like independent turrets firing off-axis at secondary targets when inside minRange.
                    if (!grp.IsTurret && !IsGroupIndependentOverlayOnly(grp))
                    {
                        return false;
                    }
                }
            }

            bool meleeOnlySelfDefense = !fireAtWill;
            if (meleeOnlySelfDefense)
            {
                bool hasMeleeOnGroup = false;
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    if (grp.Attacks[a].kind == GiantPartAttackKind.Melee)
                    {
                        hasMeleeOnGroup = true;
                        break;
                    }
                }
                if (!hasMeleeOnGroup)
                {
                    return false;
                }
            }

            // Priority 2: Pawn's mindState.enemyTarget if valid & in this part's sector/range (point-defense overlays scan per-mount closest threat)
            Pawn pawn = rigComp.Pawn;
            if (!IsGroupIndependentOverlayOnly(grp) &&
                pawn.mindState?.enemyTarget is Thing mindEnemy &&
                IsValidHostileThing(pawn, mindEnemy) &&
                TryPickWeightedAttackForTarget(grp, cockpit, mindEnemy, out chosenAttack, preferMeleeOnly: meleeOnlySelfDefense))
            {
                chosenTarget = new LocalTargetInfo(mindEnemy);
                return true;
            }

            // Priority 2B: Active vanilla PredatorHunt prey (finish prey even after downed so Corpse spawns for Ingest)
            if (!IsGroupIndependentOverlayOnly(grp) &&
                TryGetActivePredatorHuntPrey(pawn, out Pawn huntPrey))
            {
                if (TryPickWeightedAttackForTarget(grp, cockpit, huntPrey, out chosenAttack, preferMeleeOnly: true) ||
                    (!meleeOnlySelfDefense && TryPickWeightedAttackForTarget(grp, cockpit, huntPrey, out chosenAttack, preferMeleeOnly: false)))
                {
                    chosenTarget = new LocalTargetInfo(huntPrey);
                    return true;
                }
            }

            // Priority 3: Closest high-priority candidate in cachedHostileCandidates that fits this bone's sector & range
            Vector2 boneXZ = GetBoneOriginWorldXZ(grp, grp.Attacks[0], out _);
            Thing bestCand = null;
            GiantPartAttackDef bestAtk = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < cachedHostileCandidates.Count; i++)
            {
                Thing cand = cachedHostileCandidates[i];
                if (!IsValidHostileThing(pawn, cand))
                {
                    continue;
                }
                if (!TryPickWeightedAttackForTarget(grp, cockpit, cand, out GiantPartAttackDef candAtk, preferMeleeOnly: meleeOnlySelfDefense))
                {
                    continue;
                }

                Vector3 cp = cand is Pawn cpPawn ? CompGiantRig.GetSimTickPos(cpPawn) : cand.DrawPos;
                float dx = cp.x - boneXZ.x;
                float dz = cp.z - boneXZ.y;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                float score = 100f - dist;
                if (cand is Pawn pCand)
                {
                    if (pCand.Downed)
                    {
                        score -= 55f;
                    }
                    if (GiantFastLookup.TryGetRig(pCand) != null)
                    {
                        score += 18f;
                    }
                }
                else
                {
                    score -= 15f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCand = cand;
                    bestAtk = candAtk;
                }
            }

            if (bestCand != null && bestAtk != null)
            {
                chosenAttack = bestAtk;
                chosenTarget = new LocalTargetInfo(bestCand);
                return true;
            }

            return false;
        }

        private bool TryPickWeightedAttackForTarget(
            BonePartWeaponGroup grp,
            CompGiantCockpit cockpit,
            Thing target,
            out GiantPartAttackDef selected,
            bool preferMeleeOnly = false,
            bool preferRangedOnly = false)
        {
            selected = null;
            float totalWeight = 0f;

            for (int i = 0; i < grp.Attacks.Length; i++)
            {
                GiantPartAttackDef atk = grp.Attacks[i];
                if (preferMeleeOnly && atk.kind != GiantPartAttackKind.Melee)
                {
                    continue;
                }
                if (preferRangedOnly && atk.kind == GiantPartAttackKind.Melee)
                {
                    continue;
                }
                if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                {
                    continue;
                }
                if (HasConflictingActiveFullBodyMeleeClip(grp, atk))
                {
                    continue;
                }
                if (!CanPartAttackReachAndSeeTarget(grp, atk, target, checkSector: true))
                {
                    continue;
                }

                float w = Mathf.Max(0.05f, atk.selectionWeight);
                totalWeight += w;
                if (Rand.Value * totalWeight <= w)
                {
                    selected = atk;
                }
            }

            return selected != null;
        }

        public Vector2 GetBoneOriginWorldXZ(BonePartWeaponGroup grp, GiantPartAttackDef atk, out float refWorldAngleDeg)
        {
            refWorldAngleDeg = rigComp.CurrentHeadingDeg;
            if (atk != null && atk.useSoftWhipTip &&
                rigComp.TryGetSoftWhipTipForRootBone(grp.Bone.nodeId, out Vector3 whipTip, out float whipAng))
            {
                if (atk.arcReference == GiantPartArcReference.BoneWorldAngle)
                {
                    refWorldAngleDeg = whipAng;
                }
                return new Vector2(whipTip.x, whipTip.z);
            }

            if ((uint)grp.BoneIndex < (uint)livePoseScratch.Length && livePoseScratch[grp.BoneIndex].visible)
            {
                ref readonly BoneWorldPose pose = ref livePoseScratch[grp.BoneIndex];
                if (atk != null && atk.kind == GiantPartAttackKind.Ranged)
                {
                    grp.Bone.ResolveEmitterSocketOrMuzzle(
                        atk.socketId,
                        pose.activeSector,
                        out Vector2 mUv,
                        out float mAngOff,
                        out _,
                        out _,
                        out _);
                    Vector2 mXZ = pose.UVToWorldXZ(mUv.x, mUv.y);
                    if (atk.arcReference == GiantPartArcReference.BoneWorldAngle)
                    {
                        refWorldAngleDeg = SkeletalKinematics.NormalizeAngle360(pose.worldAngleDeg + mAngOff);
                    }
                    return mXZ;
                }
                else
                {
                    Vector2 sUv = atk != null ? atk.ResolveStrikeUV(grp.Bone.textureResolution) : new Vector2(0.5f, 0.85f);
                    Vector2 jXZ = atk != null && atk.useSoftWhipTip
                        ? pose.jointWorldXZ
                        : pose.UVToWorldXZ(sUv.x, sUv.y);
                    if (atk != null && atk.arcReference == GiantPartArcReference.BoneWorldAngle)
                    {
                        refWorldAngleDeg = pose.worldAngleDeg;
                    }
                    return jXZ;
                }
            }

            Vector3 fallback = CompGiantRig.GetSimTickPos(rigComp.Pawn);
            return new Vector2(fallback.x, fallback.z);
        }

        private int ResolveRangeAnchorBoneIndex(GiantPartAttackDef atk, BonePartWeaponGroup grp)
        {
            if (atk != null && !string.IsNullOrEmpty(atk.rangeOriginBoneId) &&
                rigComp?.RigDef != null && rigComp.RigDef.TryGetNodeIndex(atk.rangeOriginBoneId, out int anchorIdx) &&
                (uint)anchorIdx < (uint)livePoseScratch.Length && livePoseScratch[anchorIdx].visible)
            {
                return anchorIdx;
            }
            if (atk != null && atk.useSoftWhipTip && grp != null &&
                (uint)grp.BoneIndex < (uint)livePoseScratch.Length && livePoseScratch[grp.BoneIndex].visible)
            {
                return grp.BoneIndex;
            }
            if (grp != null && grp.DefaultRangeAnchorBoneIndex >= 0 &&
                (uint)grp.DefaultRangeAnchorBoneIndex < (uint)livePoseScratch.Length &&
                livePoseScratch[grp.DefaultRangeAnchorBoneIndex].visible)
            {
                return grp.DefaultRangeAnchorBoneIndex;
            }
            return -1;
        }

        private Vector2 ResolveRangeAnchorWorldXZ(Vector2 partOriginXZ, GiantPartAttackDef atk, BonePartWeaponGroup grp)
        {
            int anchorIdx = ResolveRangeAnchorBoneIndex(atk, grp);
            if (anchorIdx >= 0)
            {
                return livePoseScratch[anchorIdx].jointWorldXZ;
            }
            return partOriginXZ;
        }

        private static Vector2 GetUnprojectedJointWorldXZ(in BoneWorldPose pose)
        {
            bool hasProj = (pose.hasCoronalFlap && Mathf.Abs(pose.flapCosPsi - 1f) > 1e-5f) || pose.hasSagittalShear;
            return hasProj ? pose.flatJointWorldXZ : pose.jointWorldXZ;
        }

        public float ComputeFlatAnatomicalSpanFromAnchor(BonePartWeaponGroup grp, GiantPartAttackDef atk)
        {
            if (grp == null || (uint)grp.BoneIndex >= (uint)livePoseScratch.Length || !livePoseScratch[grp.BoneIndex].visible)
            {
                return 0f;
            }
            int anchorIdx = ResolveRangeAnchorBoneIndex(atk, grp);
            if (anchorIdx < 0)
            {
                return 0f;
            }
            ref readonly BoneWorldPose anchorPose = ref livePoseScratch[anchorIdx];
            ref readonly BoneWorldPose strikePose = ref livePoseScratch[grp.BoneIndex];
            Vector2 anchorFlatXZ = GetUnprojectedJointWorldXZ(in anchorPose);
            Vector2 sUv = atk != null ? atk.ResolveStrikeUV(grp.Bone.textureResolution) : new Vector2(0.5f, 0.85f);
            Vector2 strikeFlatXZ = strikePose.UVToWorldXZFlat(sUv.x, sUv.y);
            return Vector2.Distance(anchorFlatXZ, strikeFlatXZ);
        }

        private float MeasureDistanceFromPartToTarget(Vector2 partOriginXZ, GiantPartAttackDef atk, BonePartWeaponGroup grp, Thing target, out Vector2 closestTargetXZ)
        {
            // Measure reach from rangeOriginBoneId (e.g. tail_seg1 base) or whip root socket so moving tips cannot shift target acquisition zones
            Vector2 measureOriginXZ = ResolveRangeAnchorWorldXZ(partOriginXZ, atk, grp);

            if (target is Pawn targetGiant && GiantFastLookup.TryGetRig(targetGiant) is CompGiantRig targetRig)
            {
                bool useSolidHull = atk != null
                    && atk.kind == GiantPartAttackKind.Melee
                    && targetRig.Footprint.collisionCells != null
                    && targetRig.Footprint.collisionCells.Count > 0;
                if (useSolidHull)
                {
                    float bestSolidSq = float.MaxValue;
                    IntVec3 bestSolidCell = target.Position;
                    List<IntVec3> solid = targetRig.Footprint.collisionCells;
                    for (int i = 0; i < solid.Count; i++)
                    {
                        IntVec3 sc = solid[i];
                        float dx = (sc.x + 0.5f) - measureOriginXZ.x;
                        float dz = (sc.z + 0.5f) - measureOriginXZ.y;
                        float dSq = dx * dx + dz * dz;
                        if (dSq < bestSolidSq)
                        {
                            bestSolidSq = dSq;
                            bestSolidCell = sc;
                        }
                    }
                    closestTargetXZ = new Vector2(bestSolidCell.x + 0.5f, bestSolidCell.z + 0.5f);
                    return Mathf.Max(0f, Mathf.Sqrt(bestSolidSq) - 0.25f);
                }

                if (targetRig.Footprint.hitCells.Count > 0)
                {
                    float bestSq = float.MaxValue;
                    IntVec3 bestCell = target.Position;
                    List<HitCellEntry> hits = targetRig.Footprint.hitCells;
                    for (int i = 0; i < hits.Count; i++)
                    {
                        IntVec3 hc = hits[i].cell;
                        float dx = (hc.x + 0.5f) - measureOriginXZ.x;
                        float dz = (hc.z + 0.5f) - measureOriginXZ.y;
                        float dSq = dx * dx + dz * dz;
                        if (dSq < bestSq)
                        {
                            bestSq = dSq;
                            bestCell = hc;
                        }
                    }
                    closestTargetXZ = new Vector2(bestCell.x + 0.5f, bestCell.z + 0.5f);
                    return Mathf.Max(0f, Mathf.Sqrt(bestSq) - 0.35f);
                }
            }

            CellRect rect = target.OccupiedRect();
            Vector3 tp = target is Pawn p ? CompGiantRig.GetSimTickPos(p) : target.DrawPos;
            if (rect.Width <= 1 && rect.Height <= 1)
            {
                closestTargetXZ = new Vector2(tp.x, tp.z);
                float centerDist = Vector2.Distance(measureOriginXZ, closestTargetXZ);
                return atk != null && atk.kind == GiantPartAttackKind.Melee
                    ? Mathf.Max(0f, centerDist - 0.35f)
                    : centerDist;
            }

            float cx = Mathf.Clamp(measureOriginXZ.x, rect.minX, rect.maxX + 1f);
            float cz = Mathf.Clamp(measureOriginXZ.y, rect.minZ, rect.maxZ + 1f);
            closestTargetXZ = new Vector2(cx, cz);
            return Vector2.Distance(measureOriginXZ, closestTargetXZ);
        }

        public static float ComputeEffectiveRangedMinRangeDistance(
            float nearestHitCellDist,
            float targetCenterDist,
            bool targetIsGiant,
            bool targetIsAirborne)
        {
            if (!targetIsGiant)
            {
                return nearestHitCellDist;
            }
            // Outstretched wingtips/tails on huge giants (e.g. 15-cell wyvern wingspan) must not falsely
            // trigger minRange deadzones when the target giant's torso is still well outside minRange.
            float effective = Mathf.Max(nearestHitCellDist, targetCenterDist);
            if (targetIsAirborne && targetCenterDist >= 2.2f)
            {
                // Firing upward at a high-altitude flying giant adds real 3D slant separation (~5.5 cells vertical).
                const float airborneSlantAltitudeCells = 5.5f;
                float slantDist = Mathf.Sqrt(targetCenterDist * targetCenterDist + airborneSlantAltitudeCells * airborneSlantAltitudeCells);
                effective = Mathf.Max(effective, slantDist);
            }
            return effective;
        }

        private float ComputeEffectiveMinRangeDistanceForTarget(
            Vector2 partOriginXZ,
            GiantPartAttackDef atk,
            BonePartWeaponGroup grp,
            Thing target,
            float nearestHitCellDist)
        {
            if (atk == null || atk.kind != GiantPartAttackKind.Ranged || atk.minRange <= 0.01f)
            {
                return nearestHitCellDist;
            }
            if (target is Pawn tp && GiantFastLookup.TryGetRig(tp) is CompGiantRig targetRig)
            {
                Vector2 selfOriginXZ;
                if (rigComp?.Pawn != null)
                {
                    Vector3 selfCenter = CompGiantRig.GetSimTickPos(rigComp.Pawn);
                    selfOriginXZ = new Vector2(selfCenter.x, selfCenter.z);
                }
                else
                {
                    selfOriginXZ = ResolveRangeAnchorWorldXZ(partOriginXZ, atk, grp);
                }
                Vector3 centerPos = CompGiantRig.GetSimTickPos(tp);
                float centerDist = Vector2.Distance(selfOriginXZ, new Vector2(centerPos.x, centerPos.z));
                return ComputeEffectiveRangedMinRangeDistance(
                    nearestHitCellDist,
                    centerDist,
                    targetIsGiant: true,
                    targetIsAirborne: targetRig.IsAirborneOrTakingOff);
            }
            return nearestHitCellDist;
        }

        public bool CanPartAttackReachAndSeeTarget(
            BonePartWeaponGroup grp,
            GiantPartAttackDef atk,
            Thing target,
            bool checkSector,
            float extraRangeSlack = 0f,
            bool checkMinRange = true)
        {
            Pawn caster = rigComp?.Pawn;
            Map map = caster?.Map;
            if (caster == null || map == null || target == null || !target.Spawned || target.Map != map)
            {
                return false;
            }

            if (atk.kind == GiantPartAttackKind.Melee)
            {
                if (GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, target))
                {
                    return false;
                }
                if (!GiantFlightUtility.CanAirborneAttackerMeleeTarget(rigComp, atk, target))
                {
                    return false;
                }
            }

            Vector2 originXZ = GetBoneOriginWorldXZ(grp, atk, out float refAngleDeg);
            float dist = MeasureDistanceFromPartToTarget(originXZ, atk, grp, target, out Vector2 targetXZ);
            float minR = checkMinRange ? Mathf.Max(0f, atk.minRange - extraRangeSlack * 0.5f) : 0f;
            float maxR = atk.range + extraRangeSlack;
            float minCheckDist = minR > 0f
                ? ComputeEffectiveMinRangeDistanceForTarget(originXZ, atk, grp, target, dist)
                : dist;
            if (minCheckDist < minR || dist > maxR)
            {
                return false;
            }

            // Enforce physical strikePixel/anatomical lunge envelope on ALL non-Bezier, non-whip melee parts
            // (including jaws, foreclaws, wings, and tail clubs with rangeOriginBoneId) so giants never air-swing!
            if (atk.kind == GiantPartAttackKind.Melee && !atk.useSoftWhipTip)
            {
                bool inBezierChain = grp != null
                    && (uint)grp.BoneIndex < (uint)bezierChainBoneMask.Length
                    && bezierChainBoneMask[grp.BoneIndex];
                if (!inBezierChain)
                {
                    float tipMargin = GiantFastLookup.TryGetRig(target as Pawn) != null ? 0.30f : 0f;
                    int anchorIdx = ResolveRangeAnchorBoneIndex(atk, grp);
                    if (anchorIdx >= 0)
                    {
                        float flatLimbSpan = ComputeFlatAnatomicalSpanFromAnchor(grp, atk);
                        float maxAnatomicalReach = flatLimbSpan + 1.60f + tipMargin + extraRangeSlack * 0.50f;
                        if (atk.attackAnim != null && atk.attackAnim.coupledBodyMotionFactor >= 0.99f)
                        {
                            maxAnatomicalReach = Mathf.Max(maxAnatomicalReach, atk.range + extraRangeSlack);
                        }
                        if (dist > maxAnatomicalReach)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        float tipDist = Vector2.Distance(originXZ, targetXZ) - tipMargin;
                        float maxPhysicalTipLunge = Mathf.Clamp(atk.range * 0.92f, 1.65f, 2.45f) + extraRangeSlack * 0.50f;
                        if (tipDist > maxPhysicalTipLunge)
                        {
                            return false;
                        }
                    }

                    bool isFullBodySpin = atk.attackAnim != null && atk.attackAnim.coupledBodyMotionFactor >= 0.99f;
                    if (!isFullBodySpin && grp != null)
                    {
                        int profIdx = Array.IndexOf(grp.Attacks, atk);
                        if ((uint)profIdx < (uint)grp.StrikeProfiles.Length && grp.StrikeProfiles[profIdx].Valid)
                        {
                            MeleeStrikeImpactProfile prof = grp.StrikeProfiles[profIdx];
                            CompGiantRig targetRig = GiantFastLookup.TryGetRig(target as Pawn);
                            Vector3 selfSim = CompGiantRig.GetSimTickPos(caster);
                            Vector2 rootXZ = new Vector2(selfSim.x, selfSim.z);
                            float curHeading = rigComp.CurrentHeadingDeg;
                            Vector2 anchorWorldXZ = rootXZ + RotateLocalToHeadingXZ(prof.AnchorRestLocalXZ, curHeading);
                            Vector2 nominalImpactXZ = rootXZ + RotateLocalToHeadingXZ(prof.ImpactLocalXZ, curHeading);
                            Vector3 targSim = target is Pawn tp ? CompGiantRig.GetSimTickPos(tp) : target.DrawPos;
                            Vector2 targetCenterXZ = new Vector2(targSim.x, targSim.z);
                            Vector2 aimFromAnchor = grp.IsLookChainDriven
                                ? SteerVectorTowardsXZ(nominalImpactXZ - anchorWorldXZ, targetCenterXZ - anchorWorldXZ, maxTurnDeg: 28f)
                                : (nominalImpactXZ - anchorWorldXZ);
                            Vector2 predictedStrikeXZ = anchorWorldXZ + aimFromAnchor;

                            float fleshGap = MeasurePointDistanceToTargetVisualFlesh(predictedStrikeXZ, target, targetRig);
                            float lungeBudget = (rigComp.RigDef?.enableDynamicMeleeContactLunge ?? true) && !grp.IsTurret && !atk.independentOverlay
                                ? Mathf.Clamp(rigComp.RigDef?.maxDynamicMeleeContactLungeCells ?? 0.85f, 0f, 2.0f)
                                : 0.15f;
                            float maxVisualGap = lungeBudget + 0.42f + extraRangeSlack * 0.65f;
                            if (fleshGap > maxVisualGap)
                            {
                                return false;
                            }
                        }
                    }
                }
            }

            if (checkSector && grp != null && grp.IsTurret && grp.MaxTurretAimAngleDeg < 175f)
            {
                Vector2 mountXZ = (uint)grp.BoneIndex < (uint)livePoseScratch.Length && livePoseScratch[grp.BoneIndex].visible
                    ? livePoseScratch[grp.BoneIndex].jointWorldXZ
                    : originXZ;
                float mdx = targetXZ.x - mountXZ.x;
                float mdz = targetXZ.y - mountXZ.y;
                if (mdx * mdx + mdz * mdz > 0.04f)
                {
                    float mountBearing = Mathf.Atan2(mdx, mdz) * Mathf.Rad2Deg;
                    float mountCenter = SkeletalKinematics.NormalizeAngle360(rigComp.CurrentHeadingDeg + grp.Bone.baseAngle);
                    if (Mathf.Abs(Mathf.DeltaAngle(mountCenter, mountBearing)) > grp.MaxTurretAimAngleDeg + extraRangeSlack * 6f)
                    {
                        return false;
                    }
                }
            }

            if (checkSector && atk.sectorHalfAngleDeg < 179.9f)
            {
                Vector2 hullPosXZ;
                if (!string.IsNullOrEmpty(atk.rangeOriginBoneId) || (grp != null && grp.DefaultRangeAnchorBoneIndex >= 0 && atk.arcReference != GiantPartArcReference.HullHeading))
                {
                    hullPosXZ = ResolveRangeAnchorWorldXZ(originXZ, atk, grp);
                }
                else if (atk.arcReference == GiantPartArcReference.HullHeading)
                {
                    Vector3 simP = CompGiantRig.GetSimTickPos(caster);
                    hullPosXZ = new Vector2(simP.x, simP.z);
                }
                else
                {
                    hullPosXZ = originXZ;
                }
                float dx = targetXZ.x - hullPosXZ.x;
                float dz = targetXZ.y - hullPosXZ.y;
                if (dx * dx + dz * dz > 0.04f)
                {
                    float bearingDeg = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
                    float sectorWorldCenter = SkeletalKinematics.NormalizeAngle360(refAngleDeg + atk.sectorCenterDeg);
                    float maxSectorDeg = atk.sectorHalfAngleDeg + extraRangeSlack * 8f;
                    float delta = Mathf.Abs(Mathf.DeltaAngle(sectorWorldCenter, bearingDeg));
                    if (delta > maxSectorDeg)
                    {
                        bool centerInSector = false;
                        if (atk.kind == GiantPartAttackKind.Ranged && target is Pawn sectorGiantPawn && GiantFastLookup.TryGetRig(sectorGiantPawn) != null)
                        {
                            Vector3 tc = CompGiantRig.GetSimTickPos(sectorGiantPawn);
                            float cdx = tc.x - hullPosXZ.x;
                            float cdz = tc.z - hullPosXZ.y;
                            if (cdx * cdx + cdz * cdz > 0.04f)
                            {
                                float centerBearing = Mathf.Atan2(cdx, cdz) * Mathf.Rad2Deg;
                                centerInSector = Mathf.Abs(Mathf.DeltaAngle(sectorWorldCenter, centerBearing)) <= maxSectorDeg;
                            }
                        }
                        if (!centerInSector)
                        {
                            return false;
                        }
                    }

                    // Non-rear melee attacks must also satisfy body-center hull sector alignment (not merely an anteriorly offset neck socket),
                    // ensuring the giant pivots its torso into facing alignment via TurnLeft/TurnRight while rear tail attacks remain free behind!
                    if (atk.kind == GiantPartAttackKind.Melee
                        && (grp == null || !grp.IsTurret)
                        && !(atk.attackAnim != null && atk.attackAnim.coupledBodyMotionFactor >= 0.99f)
                        && !IsRearSectorMeleeAttack(atk))
                    {
                        Vector3 bodySim = CompGiantRig.GetSimTickPos(caster);
                        float bdx = targetXZ.x - bodySim.x;
                        float bdz = targetXZ.y - bodySim.z;
                        if (bdx * bdx + bdz * bdz > 0.09f)
                        {
                            float bodyBearing = Mathf.Atan2(bdx, bdz) * Mathf.Rad2Deg;
                            float hullSectorCenter = SkeletalKinematics.NormalizeAngle360(rigComp.CurrentHeadingDeg + atk.sectorCenterDeg);
                            if (Mathf.Abs(Mathf.DeltaAngle(hullSectorCenter, bodyBearing)) > maxSectorDeg)
                            {
                                return false;
                            }
                        }
                    }
                }
            }

            if (atk.requireLineOfSight && atk.kind == GiantPartAttackKind.Ranged)
            {
                IntVec3 srcCell = new Vector3(originXZ.x, 0f, originXZ.y).ToIntVec3();
                if (!srcCell.InBounds(map))
                {
                    srcCell = caster.Position;
                }
                IntVec3 dstCell = new Vector3(targetXZ.x, 0f, targetXZ.y).ToIntVec3();
                if (!dstCell.InBounds(map))
                {
                    dstCell = target.Position;
                }
                if (srcCell != dstCell &&
                    !GenSight.LineOfSight(srcCell, dstCell, map, skipFirstCell: true) &&
                    !GenSight.LineOfSight(caster.Position, dstCell, map, skipFirstCell: true))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsRearSectorMeleeAttack(GiantPartAttackDef atk)
        {
            return atk != null && Mathf.Abs(Mathf.DeltaAngle(0f, atk.sectorCenterDeg)) > 110f;
        }

        private bool IsTargetStillValidForWarmup(BonePartWeaponGroup grp, GiantPartAttackDef atk, Pawn pawn)
        {
            if (!IsTargetStillAliveAndInMap(grp.CurrentTarget, pawn.Map))
            {
                return false;
            }
            if (grp.CurrentTarget.HasThing)
            {
                bool isFullHullSpin = atk?.attackAnim != null && atk.attackAnim.coupledBodyMotionFactor >= 0.99f;
                float slack = isFullHullSpin
                    ? 2.8f
                    : (atk != null ? (atk.kind == GiantPartAttackKind.Melee ? (atk.independentOverlay ? 0.35f : 0.45f) : 2.2f) : 1.5f);
                return CanPartAttackReachAndSeeTarget(grp, atk, grp.CurrentTarget.Thing, checkSector: !isFullHullSpin, extraRangeSlack: slack, checkMinRange: false);
            }
            return true;
        }
        private static bool IsTargetStillAliveAndInMap(LocalTargetInfo targ, Map map)
        {
            if (!targ.IsValid || map == null)
            {
                return false;
            }
            if (targ.HasThing)
            {
                Thing t = targ.Thing;
                if (t == null || t.Destroyed || !t.Spawned || t.Map != map)
                {
                    return false;
                }
                if (t is Pawn p && p.Dead)
                {
                    return false;
                }
            }
            return true;
        }

        private void UpdateTargetWorldXZ(BonePartWeaponGroup grp)
        {
            if (!grp.CurrentTarget.IsValid)
            {
                grp.HasTargetWorldXZ = false;
                return;
            }
            if (grp.CurrentTarget.HasThing)
            {
                Thing t = grp.CurrentTarget.Thing;
                Vector3 wp = t is Pawn tp ? CompGiantRig.GetSimTickPos(tp) : t.DrawPos;
                grp.TargetWorldXZ = new Vector2(wp.x, wp.z);
                grp.HasTargetWorldXZ = true;
            }
            else
            {
                Vector3 wp = grp.CurrentTarget.Cell.ToVector3Shifted();
                grp.TargetWorldXZ = new Vector2(wp.x, wp.z);
                grp.HasTargetWorldXZ = true;
            }
            if (grp.HasTargetWorldXZ)
            {
                grp.LastTrackedTarget = grp.CurrentTarget;
                grp.LastTrackedTargetXZ = grp.TargetWorldXZ;
                grp.HasLastTrackedTargetXZ = true;
            }
        }

        private void FireSinglePartProjectile(
            BonePartWeaponGroup grp,
            GiantPartAttackDef atk,
            Pawn caster,
            float cockpitAccuracyMult)
        {
            Map map = caster.Map;
            if (map == null || atk.projectileDef == null || !grp.CurrentTarget.IsValid)
            {
                return;
            }

            rigComp.SolveLiveWorldPoses(livePoseScratch);
            var barrel = new GiantEmitterBarrelDef
            {
                boneId = grp.Bone.nodeId,
                socketId = atk.socketId
            };
            float projAltY = (rigComp.IsCurrentlyAirborne || rigComp.IsFlyingEntry)
                ? CompGiantRig.AirborneVfxBaseAltitudeY
                : AltitudeLayer.Projectile.AltitudeFor();

            Vector3 launchWorldPos;
            float launchAngleDeg;
            IntVec3 launchCell;
            GiantAnimVfxPreset vfxPreset = atk.vfxPresetOverride != GiantAnimVfxPreset.None ? atk.vfxPresetOverride : grp.Bone.muzzleVfxPreset;
            float vfxScale = atk.vfxScale > 0.01f ? atk.vfxScale : grp.Bone.muzzleVfxScale;
            Color vfxColor = atk.vfxColor.a > 0.01f ? atk.vfxColor : grp.Bone.muzzleVfxColor;

            if (GiantMuzzleResolver.TryResolveBarrelWorldShot(
                    rigComp.RigDef,
                    livePoseScratch,
                    null,
                    barrel,
                    projAltY,
                    rigComp.IsBoneIntact,
                    out GiantMuzzleShotInfo shotInfo))
            {
                launchWorldPos = shotInfo.WorldPos;
                launchAngleDeg = shotInfo.WorldAngleDeg;
                launchCell = shotInfo.MapCell.InBounds(map) ? shotInfo.MapCell : caster.Position;
                if (atk.vfxPresetOverride == GiantAnimVfxPreset.None)
                {
                    vfxPreset = shotInfo.VfxPreset;
                }
            }
            else
            {
                Vector2 fbXZ = GetBoneOriginWorldXZ(grp, atk, out launchAngleDeg);
                launchWorldPos = new Vector3(fbXZ.x, projAltY, fbXZ.y);
                IntVec3 candCell = launchWorldPos.ToIntVec3();
                launchCell = candCell.InBounds(map) ? candCell : caster.Position;
            }

            launchWorldPos.x = Mathf.Clamp(launchWorldPos.x, 0.5f, Mathf.Max(0.5f, map.Size.x - 0.5f));
            launchWorldPos.z = Mathf.Clamp(launchWorldPos.z, 0.5f, Mathf.Max(0.5f, map.Size.z - 0.5f));

            LocalTargetInfo intended = grp.CurrentTarget;
            IntVec3 aimDestCell = intended.Cell;
            if (intended.HasThing && intended.Thing is Pawn targetGiant &&
                GiantFastLookup.TryGetRig(targetGiant) is CompGiantRig targetRig &&
                targetRig.Footprint.hitCells.Count > 0)
            {
                aimDestCell = GiantTargetAimTracker.ResolvePreferredHitCell(targetGiant, targetRig, launchCell, caster, null, ignoreRange: true);
            }

            if (!aimDestCell.InBounds(map))
            {
                aimDestCell = intended.Cell.ClampInsideMap(map);
            }

            Vector3 aimWorld = intended.HasThing && !(intended.Thing is Pawn pg && GiantFastLookup.TryGetRig(pg) != null)
                ? intended.Thing.DrawPos
                : aimDestCell.ToVector3Shifted();
            float dist = Vector2.Distance(new Vector2(launchWorldPos.x, launchWorldPos.z), new Vector2(aimWorld.x, aimWorld.z));

            if (!(ThingMaker.MakeThing(atk.projectileDef) is Projectile projectile))
            {
                return;
            }

            GenSpawn.Spawn(projectile, launchCell, map, WipeMode.Vanish);

            LocalTargetInfo usedTarget = intended.HasThing && aimDestCell != intended.Thing.Position
                ? new LocalTargetInfo(aimDestCell)
                : intended;
            Thing coverThing = null;

            if (atk.forcedMissRadius > 0.05f)
            {
                int maxCells = GenRadial.NumCellsInRadius(atk.forcedMissRadius);
                IntVec3 missCell = aimDestCell + GenRadial.RadialPattern[Rand.Range(0, Mathf.Max(1, maxCells))];
                usedTarget = new LocalTargetInfo(missCell.ClampInsideMap(map));
            }
            else
            {
                float baseAcc = atk.EvaluateDistanceAccuracy(dist);
                float shooterAcc = caster.GetStatValue(StatDefOf.ShootingAccuracyPawn, applyPostProcess: true);
                float coverBlock = CoverUtility.CalculateOverallBlockChance( new LocalTargetInfo(aimDestCell), launchCell, map);
                float hitChance = Mathf.Clamp(baseAcc * Mathf.Lerp(0.72f, 1.12f, Mathf.Clamp01(shooterAcc)) * cockpitAccuracyMult * (1f - coverBlock * 0.65f), 0.08f, 0.98f);

                if (!Rand.Chance(hitChance))
                {
                    float missRadius = Mathf.Clamp(1.0f + dist * 0.075f, 1.0f, 3.4f);
                    Vector2 offset2D = Rand.InsideUnitCircleVec3.ToVector2() * missRadius;
                    if (offset2D.sqrMagnitude < 0.65f)
                    {
                        offset2D = offset2D.normalized * 1.05f;
                    }
                    IntVec3 wildCell = new IntVec3(
                        Mathf.RoundToInt(aimDestCell.x + offset2D.x),
                        0,
                        Mathf.RoundToInt(aimDestCell.z + offset2D.y)).ClampInsideMap(map);
                    if (wildCell == aimDestCell)
                    {
                        wildCell = (aimDestCell + GenAdj.CardinalDirections[Rand.Range(0, 4)]).ClampInsideMap(map);
                    }
                    usedTarget = new LocalTargetInfo(wildCell);
                }
            }

            ProjectileHitFlags hitFlags = usedTarget == intended && intended.HasThing
                ? ProjectileHitFlags.IntendedTarget
                : ProjectileHitFlags.All;

            projectile.Launch(
                launcher: caster,
                origin: launchWorldPos,
                usedTarget: usedTarget,
                intendedTarget: intended,
                hitFlags: hitFlags,
                preventFriendlyFire: true,
                equipment: null,
                targetCoverDef: coverThing?.def);

            Vector3 toTarget = aimWorld - launchWorldPos;
            toTarget.y = 0f;
            float vfxAngle = launchAngleDeg;
            if (toTarget.sqrMagnitude > 0.04f)
            {
                float aimDeg = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
                if (Mathf.Abs(Mathf.DeltaAngle(launchAngleDeg, aimDeg)) <= 65f)
                {
                    vfxAngle = aimDeg;
                }
            }

            if (atk.spawnMuzzleVfx && GiantVfxSystem.IsRigVfxEnabled(caster, rigComp))
            {
                GiantVfxSystem.SpawnMuzzleBlastVfx(caster, rigComp, launchWorldPos, vfxAngle, vfxScale, vfxColor, vfxPreset);
                GiantVfxSystem.TrySpawnGiantProjectileLaunchWake(projectile, caster, launchWorldPos, intended, vfxAngle);
            }

            if (atk.recoilMultiplier > 0.001f)
            {
                rigComp.TriggerTurretRecoil(grp.BoneIndex, vfxAngle, atk.recoilMultiplier, publishImmediate: false);
            }

            atk.soundCast?.PlayOneShot(new TargetInfo(launchCell, map));
            atk.soundCastTail?.PlayOneShotOnCamera(map);
        }

        private void ExecutePartMeleeStrike(
            BonePartWeaponGroup grp,
            GiantPartAttackDef atk,
            Pawn caster,
            float cockpitAccuracyMult,
            float meleeDmgFactor)
        {
            Map map = caster.Map;
            if (map == null || atk.damageDef == null || !grp.CurrentTarget.HasThing)
            {
                return;
            }

            Thing primaryVictim = grp.CurrentTarget.Thing;
            if (primaryVictim == null || primaryVictim.Destroyed || !primaryVictim.Spawned || primaryVictim.Map != map)
            {
                return;
            }

            rigComp.SolveLiveWorldPoses(livePoseScratch);
            bool isFullHullSpin = atk.attackAnim != null && atk.attackAnim.coupledBodyMotionFactor >= 0.99f;
            float maxStrikeSlack = isFullHullSpin ? 2.6f : (atk.independentOverlay ? 0.35f : 0.45f);
            if (!CanPartAttackReachAndSeeTarget(grp, atk, primaryVictim, checkSector: !isFullHullSpin, extraRangeSlack: maxStrikeSlack, checkMinRange: false))
            {
                return;
            }
            Vector2 strikeOriginXZ = GetBoneOriginWorldXZ(grp, atk, out _);
            float dist = MeasureDistanceFromPartToTarget(strikeOriginXZ, atk, grp, primaryVictim, out Vector2 impactXZ);
            if (dist > atk.range + maxStrikeSlack)
            {
                return;
            }
            Vector3 strikeWorld = new Vector3(strikeOriginXZ.x, caster.DrawPos.y, strikeOriginXZ.y);
            Vector3 victimWorld = new Vector3(impactXZ.x, primaryVictim.DrawPos.y, impactXZ.y);
            if (isFullHullSpin && atk.burstShotCount > 1 && primaryVictim is Pawn spinVictimPawn && GiantFastLookup.TryGetRig(spinVictimPawn) is CompGiantRig spinVictimRig)
            {
                Vector3 victimCenter = CompGiantRig.GetSimTickPos(spinVictimPawn);
                victimCenter.y = primaryVictim.DrawPos.y;
                float vRad = spinVictimRig.CurrentHeadingDeg * Mathf.Deg2Rad;
                Vector3 victimFwd = new Vector3(Mathf.Sin(vRad), 0f, Mathf.Cos(vRad));
                Vector3 victimRight = new Vector3(victimFwd.z, 0f, -victimFwd.x);
                switch (grp.TotalShotsOrStrikesFired & 3)
                {
                    case 0:
                        strikeWorld = victimCenter - victimRight * 9.0f;
                        victimWorld = victimCenter - victimRight * 2.0f;
                        break;
                    case 1:
                        strikeWorld = victimCenter - victimFwd * 0.55f + victimRight * 0.15f;
                        victimWorld = victimCenter - victimFwd * 0.35f;
                        break;
                    case 2:
                        strikeWorld = victimCenter + victimRight * 9.0f;
                        victimWorld = victimCenter + victimRight * 2.0f;
                        break;
                    default:
                        strikeWorld = victimCenter - victimFwd * 8.5f;
                        victimWorld = victimCenter - victimFwd * 2.8f;
                        break;
                }
                strikeWorld.y = caster.DrawPos.y;
            }
            Vector3 dir = victimWorld - strikeWorld;
            dir.y = 0f;
            float strikeAngle = dir.sqrMagnitude > 1e-4f
                ? Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg
                : rigComp.CurrentHeadingDeg;

            IntVec3 soundCell = primaryVictim.Position.InBounds(map) ? primaryVictim.Position : caster.Position;
            if (!RollVanillaMeleeHit(caster, primaryVictim, cockpitAccuracyMult, map, atk.soundMiss, out _))
            {
                return;
            }

            float finalDamage = Mathf.Max(1f, atk.power * meleeDmgFactor);
            float primaryArmorPen = ResolveEffectivePartMeleeArmorPenetration(caster, atk.armorPenetration);
            ApplySinglePartMeleeDamage(caster, primaryVictim, atk.damageDef, finalDamage, primaryArmorPen, strikeAngle, strikeWorld, victimWorld, atk.stunTicks, atk.knockbackDistance);

            // Optional close-quarters cleave (uses exact vanilla melee hit/dodge roll; strictly zero melee VFX)
            if (atk.cleaveRadius > 0.25f && atk.cleaveDamageFactor > 0.05f)
            {
                Vector2 spinAnchorXZ = isFullHullSpin ? ResolveRangeAnchorWorldXZ(strikeOriginXZ, atk, grp) : impactXZ;
                IntVec3 cleaveCenter = new Vector3(spinAnchorXZ.x, 0f, spinAnchorXZ.y).ToIntVec3();
                float effectiveCleaveRadius = isFullHullSpin ? Mathf.Max(atk.cleaveRadius, atk.range + 1.5f) : atk.cleaveRadius;
                if (cleaveCenter.InBounds(map))
                {
                    float cleaveDmg = Mathf.Max(1f, finalDamage * atk.cleaveDamageFactor);
                    float cleaveArmorPen = ResolveEffectivePartMeleeArmorPenetration(caster, atk.armorPenetration * 0.85f);
                    int hitExtra = 0;
                    foreach (Thing extra in GenRadial.RadialDistinctThingsAround(cleaveCenter, map, effectiveCleaveRadius, useCenter: true))
                    {
                        if (extra == null || extra == caster || extra == primaryVictim || extra.Destroyed || !extra.Spawned)
                        {
                            continue;
                        }
                        if (extra is Pawn ep && !ep.Dead && caster.HostileTo(ep))
                        {
                            if (GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, ep))
                            {
                                continue;
                            }
                            if (isFullHullSpin && MeasureDistanceFromPartToTarget(strikeOriginXZ, atk, grp, ep, out _) > atk.range + maxStrikeSlack)
                            {
                                continue;
                            }
                            if (atk.independentOverlay && !CanPartAttackReachAndSeeTarget(grp, atk, ep, checkSector: true, extraRangeSlack: 0.6f))
                            {
                                continue;
                            }
                            if (!RollVanillaMeleeHit(caster, ep, cockpitAccuracyMult, map, atk.soundMiss, out _))
                            {
                                continue;
                            }
                            ApplySinglePartMeleeDamage(caster, ep, atk.damageDef, cleaveDmg, cleaveArmorPen, strikeAngle, strikeWorld, ep.DrawPos, atk.stunTicks / 2, atk.knockbackDistance * 0.6f);
                            hitExtra++;
                            if (hitExtra >= 4)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            if (primaryVictim is Pawn)
            {
                SoundDef hitPawnSnd = atk.soundHitPawn
                    ?? (atk.damageDef == DamageDefOf.Blunt
                        ? DefDatabase<SoundDef>.GetNamedSilentFail("Pawn_Melee_MechanoidBash_HitPawn")
                        : DefDatabase<SoundDef>.GetNamedSilentFail("Pawn_Melee_MechanoidSlash_HitPawn"));
                hitPawnSnd?.PlayOneShot(new TargetInfo(soundCell, map));
            }
            else
            {
                SoundDef hitBldgSnd = atk.soundHitBuilding
                    ?? DefDatabase<SoundDef>.GetNamedSilentFail("Pawn_Melee_MechanoidBash_HitBuilding");
                hitBldgSnd?.PlayOneShot(new TargetInfo(soundCell, map));
            }
        }

        /// <summary>
        /// Exact vanilla RimWorld Verb_MeleeAttack.IsTargetImmobile check.
        /// </summary>
        public static bool IsVanillaMeleeTargetImmobile(Thing target)
        {
            if (target == null)
            {
                return true;
            }
            if (target.def.category == ThingCategory.Pawn && target is Pawn pawn && !pawn.Downed)
            {
                return pawn.GetPosture() != PawnPosture.Standing;
            }
            return true;
        }

        /// <summary>
        /// Exact vanilla RimWorld Verb_MeleeAttack.GetNonMissChance calculation (with optional cockpit accuracy multiplier).
        /// </summary>
        public static float GetVanillaMeleeNonMissChance(Pawn attacker, Thing target, float accuracyMultiplier = 1f)
        {
            if (IsVanillaMeleeTargetImmobile(target))
            {
                return 1f;
            }
            float num = attacker != null
                ? attacker.GetStatValue(StatDefOf.MeleeHitChance, applyPostProcess: true)
                : 0.85f;
            if (ModsConfig.IdeologyActive && attacker != null && target != null)
            {
                if (DarknessCombatUtility.IsOutdoorsAndLit(target))
                {
                    num += attacker.GetStatValue(StatDefOf.MeleeHitChanceOutdoorsLitOffset, applyPostProcess: true);
                }
                else if (DarknessCombatUtility.IsOutdoorsAndDark(target))
                {
                    num += attacker.GetStatValue(StatDefOf.MeleeHitChanceOutdoorsDarkOffset, applyPostProcess: true);
                }
                else if (DarknessCombatUtility.IsIndoorsAndDark(target))
                {
                    num += attacker.GetStatValue(StatDefOf.MeleeHitChanceIndoorsDarkOffset, applyPostProcess: true);
                }
                else if (DarknessCombatUtility.IsIndoorsAndLit(target))
                {
                    num += attacker.GetStatValue(StatDefOf.MeleeHitChanceIndoorsLitOffset, applyPostProcess: true);
                }
            }
            return Mathf.Clamp01(num * Mathf.Max(0.05f, accuracyMultiplier));
        }

        /// <summary>
        /// Exact vanilla RimWorld Verb_MeleeAttack.GetDodgeChance calculation (zero artificial dodge cap or hit floor).
        /// </summary>
        public static float GetVanillaMeleeDodgeChance(Thing target)
        {
            if (IsVanillaMeleeTargetImmobile(target))
            {
                return 0f;
            }
            if (!(target is Pawn pawn))
            {
                return 0f;
            }
            if (pawn.stances?.curStance is Stance_Busy busy && busy.verb?.verbProps != null && !busy.verb.verbProps.IsMeleeAttack)
            {
                return 0f;
            }
            float num = pawn.GetStatValue(StatDefOf.MeleeDodgeChance, applyPostProcess: true);
            if (ModsConfig.IdeologyActive)
            {
                if (DarknessCombatUtility.IsOutdoorsAndLit(target))
                {
                    num += pawn.GetStatValue(StatDefOf.MeleeDodgeChanceOutdoorsLitOffset, applyPostProcess: true);
                }
                else if (DarknessCombatUtility.IsOutdoorsAndDark(target))
                {
                    num += pawn.GetStatValue(StatDefOf.MeleeDodgeChanceOutdoorsDarkOffset, applyPostProcess: true);
                }
                else if (DarknessCombatUtility.IsIndoorsAndDark(target))
                {
                    num += pawn.GetStatValue(StatDefOf.MeleeDodgeChanceIndoorsDarkOffset, applyPostProcess: true);
                }
                else if (DarknessCombatUtility.IsIndoorsAndLit(target))
                {
                    num += pawn.GetStatValue(StatDefOf.MeleeDodgeChanceIndoorsLitOffset, applyPostProcess: true);
                }
            }
            return Mathf.Clamp01(num);
        }

        /// <summary>
        /// Pure arithmetic helper matching vanilla RimWorld's two-stage melee roll:
        /// P(hit) = clamp01(nonMissChance) * (1 - clamp01(dodgeChance)), with zero artificial dodge cap.
        /// </summary>
        public static float ComputeVanillaMeleeCombinedHitProbability(float nonMissChance, float dodgeChance)
        {
            return Mathf.Clamp01(nonMissChance) * (1f - Mathf.Clamp01(dodgeChance));
        }

        /// <summary>
        /// Executes RimWorld's exact two-stage Verb_MeleeAttack.TryCastShot() hit and dodge roll,
        /// emitting vanilla dodge floating text and dodge/miss sound effects.
        /// </summary>
        public static bool RollVanillaMeleeHit(
            Pawn attacker,
            Thing victim,
            float accuracyMultiplier,
            Map map,
            SoundDef fallbackMissSound,
            out bool dodged)
        {
            dodged = false;
            if (victim == null)
            {
                return false;
            }
            IntVec3 soundCell = (map != null && victim.Position.InBounds(map))
                ? victim.Position
                : (attacker != null ? attacker.Position : IntVec3.Zero);
            SoundDef missSnd = fallbackMissSound
                ?? attacker?.def?.race?.soundMeleeMiss
                ?? SoundDefOf.Pawn_Melee_Punch_Miss;

            float nonMissChance = GetVanillaMeleeNonMissChance(attacker, victim, accuracyMultiplier);
            if (!Rand.Chance(nonMissChance))
            {
                if (map != null)
                {
                    missSnd?.PlayOneShot(new TargetInfo(soundCell, map));
                }
                return false;
            }

            float dodgeChance = GetVanillaMeleeDodgeChance(victim);
            if (dodgeChance > 0f && Rand.Chance(dodgeChance))
            {
                dodged = true;
                if (map != null)
                {
                    SoundDef dodgeSnd = victim.def?.race?.soundMeleeDodge ?? missSnd;
                    dodgeSnd?.PlayOneShot(new TargetInfo(soundCell, map));
                    MoteMaker.ThrowText(victim.DrawPos, map, "TextMote_Dodge".Translate(), 1.9f);
                }
                return false;
            }

            return true;
        }

        public static float ResolveEffectivePartMeleeArmorPenetration(Pawn attacker, float baseArmorPenetration)
        {
            float result = baseArmorPenetration;
            if (attacker != null)
            {
                if (attacker.AllComps is List<ThingComp> comps)
                {
                    for (int i = 0; i < comps.Count; i++)
                    {
                        if (comps[i] is IGiantMeleeArmorPenModifier compMod)
                        {
                            result = compMod.ModifyMeleeArmorPenetration(result);
                        }
                    }
                }
                if (attacker.health?.hediffSet?.hediffs is List<Hediff> hediffs)
                {
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        if (hediffs[i] is IGiantMeleeArmorPenModifier hediffMod)
                        {
                            result = hediffMod.ModifyMeleeArmorPenetration(result);
                        }
                    }
                }
            }
            return result;
        }

        private static void ApplySinglePartMeleeDamage(
            Pawn attacker,
            Thing victim,
            DamageDef damDef,
            float amount,
            float armorPen,
            float strikeAngle,
            Vector3 strikeOriginWorld,
            Vector3 victimImpactWorld,
            int stunTicks,
            float knockbackDist)
        {
            armorPen = ResolveEffectivePartMeleeArmorPenetration(attacker, armorPen);
            var dinfo = new DamageInfo(
                damDef,
                amount,
                armorPen,
                strikeAngle,
                instigator: attacker,
                hitPart: null,
                weapon: attacker.def,
                category: DamageInfo.SourceCategory.ThingOrUnknown,
                intendedTarget: victim);

            PendingBoneHitContext.ScopeState hitScope = default;
            if (victim is Pawn victimGiant && GiantFastLookup.TryGetRig(victimGiant) is CompGiantRig victimRig)
            {
                int hitBoneIdx = -1;
                Vector3 hitWorld = victimImpactWorld;
                if (!GiantTargetAimTracker.TryPickIntactBioferriteCoreMeleeBone(
                        victimGiant,
                        victimRig,
                        strikeOriginWorld,
                        maxReachWorld: 7.5f,
                        out hitBoneIdx,
                        out hitWorld))
                {
                    if (!victimRig.Footprint.TryHitTestSegment(
                            victimRig.RigDef,
                            victimRig.AlphaMasksSpan,
                            strikeOriginWorld,
                            victimImpactWorld,
                            out hitBoneIdx,
                            out hitWorld,
                            victimRig.SoftWhipAlphaMasksSpan))
                    {
                        hitBoneIdx = victimRig.Footprint.FindClosestHitBoneToCell(victimImpactWorld.ToIntVec3());
                        hitWorld = victimImpactWorld;
                    }
                }
                if (hitBoneIdx >= 0)
                {
                    BodyPartRecord part = victimRig.GetBodyPartForBone(hitBoneIdx);
                    hitScope = PendingBoneHitContext.Push(victimGiant, victimRig, hitBoneIdx, part, hitWorld);
                }
            }

            try
            {
                victim.TakeDamage(dinfo);
            }
            finally
            {
                PendingBoneHitContext.Pop(in hitScope);
            }

            if (victim is Pawn vp && !vp.Dead && vp.Spawned && GiantFastLookup.TryGetRig(vp) == null)
            {
                if (stunTicks > 0)
                {
                    vp.stances?.stunner?.StunFor(stunTicks, attacker, addBattleLog: false, showMote: true);
                    vp.stances?.stagger?.StaggerFor(Mathf.Max(60, stunTicks));
                }
                if (knockbackDist >= 0.9f && vp.Map != null)
                {
                    Vector3 pushOrigin = attacker != null ? CompGiantRig.GetSimTickPos(attacker) : strikeOriginWorld;
                    Vector3 delta = vp.Position.ToVector3Shifted() - pushOrigin;
                    delta.y = 0f;
                    if (delta.sqrMagnitude <= 0.01f)
                    {
                        pushOrigin = strikeOriginWorld;
                    }
                    CompGiantRig attackerRig = attacker != null ? GiantFastLookup.TryGetRig(attacker) : null;
                    GiantCombatMechanicsUtility.TryKnockbackPawn(attackerRig, vp, pushOrigin, knockbackDist);
                }
            }
        }

        private void RefreshHostileCandidates(Pawn giant, int ticksGame)
        {
            lastCandidateScanTick = ticksGame;
            cachedHostileCandidates.Clear();
            Map map = giant?.Map;
            if (map == null)
            {
                return;
            }

            float maxScanDist = Mathf.Max(42f, GetMaxOperationalRange() + 14f);
            float maxScanSq = maxScanDist * maxScanDist;
            IntVec3 selfPos = giant.Position;

            CollectPotentialAttackTargets(giant, candidateScanScratch);
            if (candidateScanScratch.Count == 0)
            {
                return;
            }

            for (int i = 0; i < candidateScanScratch.Count; i++)
            {
                IAttackTarget iat = candidateScanScratch[i];
                if (iat == null || iat.ThreatDisabled(giant))
                {
                    continue;
                }
                Thing t = iat.Thing;
                if (!IsValidHostileThing(giant, t))
                {
                    continue;
                }
                if (selfPos.DistanceToSquared(t.Position) <= maxScanSq)
                {
                    cachedHostileCandidates.Add(t);
                }
            }
        }

        /// <summary>
        /// Collects potential hostile IAttackTargets for a giant.
        /// Vanilla AttackTargetsCache.GetPotentialTargetsFor(th) skips faction-hostile targets whenever
        /// th.Thing.Faction == null (wild animals in Manhunter / ManhunterPermanent / provoked) and omits
        /// same-faction targets during Berserk mental states; in those cases we scan ThingRequestGroup.AttackTarget
        /// directly (matching vanilla AttackTargetFinder.BestAttackTarget's aggro/melee branch).
        /// </summary>
        public static void CollectPotentialAttackTargets(Pawn giant, List<IAttackTarget> outTargets)
        {
            outTargets.Clear();
            Map map = giant?.Map;
            if (giant == null || map == null)
            {
                return;
            }

            CompGiantRig rig = GiantFastLookup.TryGetRig(giant);
            bool needFullGroupScan = giant.Faction == null
                || giant.InMentalState
                || (rig != null && rig.HasActiveProvokedTarget);

            if (!needFullGroupScan && map.attackTargetsCache != null)
            {
                List<IAttackTarget> cached = map.attackTargetsCache.GetPotentialTargetsFor(giant);
                if (cached != null)
                {
                    for (int i = 0; i < cached.Count; i++)
                    {
                        IAttackTarget item = cached[i];
                        if (item != null && item.Thing != giant)
                        {
                            outTargets.Add(item);
                        }
                    }
                }
                return;
            }

            List<Thing> attackGroupThings = map.listerThings?.ThingsInGroup(ThingRequestGroup.AttackTarget);
            if (attackGroupThings != null)
            {
                for (int i = 0; i < attackGroupThings.Count; i++)
                {
                    Thing t = attackGroupThings[i];
                    if (t != null && t != giant && t is IAttackTarget iat)
                    {
                        outTargets.Add(iat);
                    }
                }
            }
        }

        public static bool TryGetActivePredatorHuntPrey(Pawn predator, out Pawn prey)
        {
            prey = null;
            Job curJob = predator?.CurJob;
            if (curJob?.def == null || predator.Dead || predator.Downed)
            {
                return false;
            }
            bool isHuntJob = curJob.def == JobDefOf.PredatorHunt
                || string.Equals(curJob.def.defName, "PredatorHunt", StringComparison.Ordinal);
            if (!isHuntJob || !curJob.targetA.HasThing || !(curJob.targetA.Thing is Pawn targetPawn))
            {
                return false;
            }
            if (targetPawn == predator || targetPawn.Destroyed || (targetPawn.health != null && targetPawn.Dead))
            {
                return false;
            }
            if (predator.Spawned && (!targetPawn.Spawned || targetPawn.Map != predator.Map))
            {
                return false;
            }
            prey = targetPawn;
            return true;
        }

        /// <summary>
        /// Validates a player-designated manual focus/attack target (via vanilla drafted right-click attack
        /// or Focus Target gizmo). Unlike IsValidHostileThing, allows non-hostile pawns, wild animals,
        /// downed pawns, and attackable buildings as long as the target is spawned, alive/undestroyed, and visible.
        /// </summary>
        public static bool IsValidManualFocusTarget(Pawn giant, Thing candidate)
        {
            if (giant == null || candidate == null || candidate == giant || candidate.Destroyed)
            {
                return false;
            }
            if (giant.Spawned && (!candidate.Spawned || candidate.Map != giant.Map))
            {
                return false;
            }
            if (giant.Map != null && candidate.Spawned)
            {
                CompGiantRig targetRig = GiantFastLookup.TryGetRig(candidate as Pawn);
                if (targetRig != null && targetRig.Footprint.hitCellPositions.Count > 0)
                {
                    bool anyVisible = !candidate.Position.Fogged(giant.Map);
                    if (!anyVisible)
                    {
                        List<IntVec3> hits = targetRig.Footprint.hitCellPositions;
                        for (int i = 0; i < hits.Count; i++)
                        {
                            if (!hits[i].Fogged(giant.Map))
                            {
                                anyVisible = true;
                                break;
                            }
                        }
                    }
                    if (!anyVisible)
                    {
                        return false;
                    }
                }
                else if (candidate.Position.Fogged(giant.Map))
                {
                    return false;
                }
            }
            if (candidate is Pawn p)
            {
                if ((p.health != null && p.Dead) || (giant.Faction != null && giant.Faction.IsPlayer && p.IsHiddenFromPlayer()))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsValidHostileThing(Pawn giant, Thing candidate)
        {
            if (giant == null || candidate == null || candidate == giant || candidate.Destroyed)
            {
                return false;
            }
            if (giant.Spawned && (!candidate.Spawned || candidate.Map != giant.Map))
            {
                return false;
            }
            if (giant.Map != null && candidate.Spawned && candidate.Position.Fogged(giant.Map))
            {
                return false;
            }

            CompGiantRig giantRig = GiantFastLookup.TryGetRig(giant);
            bool isProvokedTarget = giantRig != null && giantRig.IsProvokedAgainst(candidate);

            if (candidate is Pawn p)
            {
                if ((p.health != null && p.Dead) || (giant.Faction != null && giant.Faction.IsPlayer && p.IsHiddenFromPlayer()))
                {
                    return false;
                }
                if (isProvokedTarget)
                {
                    return true;
                }
                if (giant.InMentalState &&
                    (giant.MentalStateDef == MentalStateDefOf.Manhunter ||
                     giant.MentalStateDef == MentalStateDefOf.ManhunterPermanent))
                {
                    return (p.RaceProps != null && p.RaceProps.Humanlike && !p.Roamer) || giant.HostileTo(p);
                }
            }
            else if (isProvokedTarget)
            {
                return true;
            }

            return giant.HostileTo(candidate);
        }

        public bool TryGetActiveClipAndSampleTick(out string clipDefName, out float sampleTick)
        {
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (grp.IsAnimating && grp.ActiveAttack?.attackAnim != null &&
                    (rigComp == null || rigComp.IsBoneIntact(grp.Bone.nodeId)))
                {
                    clipDefName = grp.ActiveAttack.attackAnim.defName ?? string.Empty;
                    sampleTick = grp.AnimSampleTick;
                    return true;
                }
            }
            clipDefName = string.Empty;
            sampleTick = 0f;
            return false;
        }

        public bool HasActiveFullHullSpinClip()
        {
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                GiantAnimDef clip = grp.IsAnimating ? grp.ActiveAttack?.attackAnim : null;
                if (clip != null && clip.coupledBodyMotionFactor >= 0.99f &&
                    (rigComp == null || rigComp.IsBoneIntact(grp.Bone.nodeId)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Layers active per-bone attack animations directly onto finalLocalPoses so a giant can walk/turn
        /// smoothly while multiple claws, whips, tails, and turrets recoil or strike independently.
        /// </summary>
        public void ApplyLayeredPartAnimations(GiantRigDef rig, bool isLocomoting, BoneLocalPose[] finalLocalPoses)
        {
            if (rig == null || groups.Length == 0 || finalLocalPoses == null)
            {
                return;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = Mathf.Min(nodes.Length, finalLocalPoses.Length);
            if (clipEvalScratch.Length < count)
            {
                clipEvalScratch = new BoneLocalPose[count];
                clipHasTrackMask = new bool[count];
            }

            Vector2 accumulatedDynamicLunge = Vector2.zero;
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!grp.HasActiveVisualOverlay || (rigComp != null && rigComp.Pawn?.health != null && !rigComp.IsBoneIntact(grp.Bone.nodeId)))
                {
                    continue;
                }

                if (grp.IsFadingOut)
                {
                    float fadeProgress = grp.FadeOutTicksTotal > 0
                        ? Mathf.Clamp01((float)grp.FadeOutTicksLeft / grp.FadeOutTicksTotal)
                        : 0f;
                    float smoothFade = fadeProgress * fadeProgress * (3f - 2f * fadeProgress);
                    float fadeEnv = grp.FadeOutStartEnv * smoothFade;
                    if (fadeEnv > 0.001f)
                    {
                        ApplySingleClipOverlay(rig, nodes, count, isLocomoting, grp, grp.FadeOutClip, grp.FadeOutSampleTick, fadeEnv, isAbortedFade: true, finalLocalPoses);
                        if (!isLocomoting && grp.FadeOutLungePeakLocalXZ.sqrMagnitude > 1e-6f &&
                            (grp.FadeOutClip == null || string.IsNullOrWhiteSpace(grp.FadeOutClip.isolatedSubtreeRootBoneId)))
                        {
                            float bell = BonePartWeaponGroup.ComputeContactLungeBell(grp.FadeOutSampleTick, grp.FadeOutImpactTick, grp.FadeOutDurationTicks);
                            accumulatedDynamicLunge += grp.FadeOutLungePeakLocalXZ * (bell * fadeEnv);
                        }
                    }
                }

                if (!grp.IsAnimating)
                {
                    continue;
                }

                GiantAnimDef clip = grp.ActiveAttack.attackAnim;
                float env = BonePartWeaponGroup.ComputeClipEnvelope(clip, grp.AnimSampleTick);
                if (env > 0.001f)
                {
                    ApplySingleClipOverlay(rig, nodes, count, isLocomoting, grp, clip, grp.AnimSampleTick, env, isAbortedFade: false, finalLocalPoses);
                    if (!isLocomoting && !grp.ActiveAttack.independentOverlay && grp.ContactLungePeakLocalXZ.sqrMagnitude > 1e-6f &&
                        (clip == null || string.IsNullOrWhiteSpace(clip.isolatedSubtreeRootBoneId)))
                    {
                        float bell = BonePartWeaponGroup.ComputeContactLungeBell(grp.AnimSampleTick, grp.AnimImpactTick, grp.AnimDurationTicks);
                        accumulatedDynamicLunge += grp.ContactLungePeakLocalXZ * bell;
                    }
                }
            }

            if (count > 0 && !isLocomoting && accumulatedDynamicLunge.sqrMagnitude > 1e-6f)
            {
                float maxLunge = Mathf.Clamp(rig.maxDynamicMeleeContactLungeCells, 0f, 2.0f);
                if (accumulatedDynamicLunge.sqrMagnitude > maxLunge * maxLunge)
                {
                    accumulatedDynamicLunge = accumulatedDynamicLunge.normalized * maxLunge;
                }
                finalLocalPoses[0].offset += accumulatedDynamicLunge;
            }
        }

        private void ApplySingleClipOverlay(
            GiantRigDef rig,
            BoneNodeDef[] nodes,
            int count,
            bool isLocomoting,
            BonePartWeaponGroup grp,
            GiantAnimDef clip,
            float rawSampleTick,
            float env,
            bool isAbortedFade,
            BoneLocalPose[] finalLocalPoses)
        {
            if (clip?.tracks == null || clip.tracks.Count == 0)
            {
                return;
            }

            float dur = Mathf.Max(1f, clip.durationTicks);
            float t = Mathf.Clamp(rawSampleTick, 0f, dur);

            int isolatedRootIdx = -1;
            bool isIndep = !isAbortedFade && grp.ActiveAttack != null && grp.ActiveAttack.independentOverlay;
            if (!string.IsNullOrWhiteSpace(clip.isolatedSubtreeRootBoneId))
            {
                rig.TryGetNodeIndex(clip.isolatedSubtreeRootBoneId, out isolatedRootIdx);
            }
            else if (isIndep)
            {
                isolatedRootIdx = grp.BoneIndex;
            }
            bool enforceSubtreeIsolation = isIndep || isolatedRootIdx >= 0;
            bool groupInBezierChain = (uint)grp.BoneIndex < (uint)bezierChainBoneMask.Length && bezierChainBoneMask[grp.BoneIndex];

            Array.Clear(clipHasTrackMask, 0, count);
            for (int trIdx = 0; trIdx < clip.tracks.Count; trIdx++)
            {
                BoneAnimTrack tr = clip.tracks[trIdx];
                if (tr == null || string.IsNullOrEmpty(tr.nodeId) || !rig.TryGetNodeIndex(tr.nodeId, out int bIdx) || (uint)bIdx >= (uint)count)
                {
                    continue;
                }
                if (enforceSubtreeIsolation && (isolatedRootIdx < 0 || !rig.IsBoneInSubtree(bIdx, isolatedRootIdx)))
                {
                    continue;
                }
                if (!groupInBezierChain && (uint)bIdx < (uint)bezierChainBoneMask.Length && bezierChainBoneMask[bIdx])
                {
                    continue;
                }
                clipEvalScratch[bIdx] = tr.SampleAt(t, dur, loop: false);
                clipHasTrackMask[bIdx] = true;
            }

            int actingRootIdx = -1;
            bool hasActingSubtree = !string.IsNullOrWhiteSpace(clip.actingSubtreeRootBoneId)
                && rig.TryGetNodeIndex(clip.actingSubtreeRootBoneId, out actingRootIdx);
            Pawn hostPawn = rigComp?.Pawn;
            float safeBodySize = hostPawn != null
                ? (hostPawn.ageTracker != null ? hostPawn.BodySize : (hostPawn.RaceProps?.baseBodySize ?? -1f))
                : -1f;
            float bodyCoupling = hasActingSubtree
                ? rig.ResolvePartAnimBodyCoupling(safeBodySize, clip.coupledBodyMotionFactor)
                : 1f;

            for (int i = 0; i < count; i++)
            {
                if (!clipHasTrackMask[i] || (rigComp != null && !rigComp.IsBoneIntact(nodes[i].nodeId)))
                {
                    continue;
                }
                if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                {
                    continue;
                }

                BoneLocalPose sampled = clipEvalScratch[i];
                if (hasActingSubtree && !rig.IsBoneInSubtree(i, actingRootIdx))
                {
                    sampled.offset *= bodyCoupling;
                    sampled.angle *= bodyCoupling;
                    sampled.sagittalPitch *= bodyCoupling;
                    sampled.coronalFlap *= bodyCoupling;
                }

                if (i == 0)
                {
                    if (enforceSubtreeIsolation)
                    {
                        continue;
                    }
                    bool fullHullSpin = clip.coupledBodyMotionFactor >= 0.99f;
                    float wrappedRootAngle = Mathf.DeltaAngle(0f, sampled.angle);
                    if (fullHullSpin)
                    {
                        Vector2 nextOff = Vector2.LerpUnclamped(finalLocalPoses[0].offset, sampled.offset, env);
                        if (nextOff.sqrMagnitude > 0.81f)
                        {
                            nextOff = nextOff.normalized * 0.90f;
                        }
                        finalLocalPoses[0].offset = nextOff;
                        float baseRootAng = Mathf.DeltaAngle(0f, finalLocalPoses[0].angle);
                        float spinAngle = sampled.angle;
                        if (!isAbortedFade && t >= dur - 12f && Mathf.Abs(spinAngle) > 270f)
                        {
                            spinAngle = wrappedRootAngle;
                        }
                        else if (isAbortedFade)
                        {
                            spinAngle = wrappedRootAngle;
                        }
                        finalLocalPoses[0].angle = Mathf.DeltaAngle(0f, Mathf.LerpUnclamped(baseRootAng, spinAngle, env));
                        finalLocalPoses[0].sagittalPitch = Mathf.LerpUnclamped(finalLocalPoses[0].sagittalPitch, sampled.sagittalPitch, env);
                        finalLocalPoses[0].coronalFlap = Mathf.LerpUnclamped(finalLocalPoses[0].coronalFlap, sampled.coronalFlap, env);
                    }
                    else if (isLocomoting)
                    {
                        // Preserve walking stride translation; apply gentle upper-hull recoil/twist only
                        float locoTwistWeight = hasActingSubtree ? 1f : 0.35f;
                        finalLocalPoses[0].angle = Mathf.DeltaAngle(0f, finalLocalPoses[0].angle + wrappedRootAngle * env * locoTwistWeight);
                    }
                    else if (hasActingSubtree)
                    {
                        Vector2 nextOff = finalLocalPoses[0].offset + sampled.offset * env;
                        if (nextOff.sqrMagnitude > 0.7225f)
                        {
                            nextOff = nextOff.normalized * 0.85f;
                        }
                        finalLocalPoses[0].offset = nextOff;
                        finalLocalPoses[0].angle = Mathf.DeltaAngle(0f, finalLocalPoses[0].angle + wrappedRootAngle * env);
                        finalLocalPoses[0].sagittalPitch = Mathf.LerpUnclamped(finalLocalPoses[0].sagittalPitch, sampled.sagittalPitch, env);
                        finalLocalPoses[0].coronalFlap = Mathf.LerpUnclamped(finalLocalPoses[0].coronalFlap, sampled.coronalFlap, env);
                    }
                    else if (grp.BoneIndex > 0)
                    {
                        // Single active full-body choreography clip (e.g. ClawLeft / ClawRight twist) preserves clear shoulder twist;
                        // when multiple disjoint limb overlays run simultaneously, damp root torso recoil so they never fight over hull pose.
                        bool soloOverlay = CountActiveNonIsolatedOverlays() <= 1;
                        float offScale = soloOverlay ? 0.55f : 0.30f;
                        float angScale = soloOverlay ? 0.78f : 0.25f;
                        Vector2 nextOff = finalLocalPoses[0].offset + sampled.offset * (env * offScale);
                        if (nextOff.sqrMagnitude > 0.7225f)
                        {
                            nextOff = nextOff.normalized * 0.85f;
                        }
                        finalLocalPoses[0].offset = nextOff;
                        finalLocalPoses[0].angle = Mathf.DeltaAngle(0f, finalLocalPoses[0].angle + wrappedRootAngle * (env * angScale));
                    }
                    else
                    {
                        finalLocalPoses[0] = BoneLocalPose.Lerp(in finalLocalPoses[0], in sampled, env);
                    }
                }
                else
                {
                    finalLocalPoses[i].offset += sampled.offset * env;
                    finalLocalPoses[i].angle = Mathf.DeltaAngle(0f, Mathf.LerpAngle(finalLocalPoses[i].angle, sampled.angle, env));
                    finalLocalPoses[i].sagittalPitch = Mathf.LerpUnclamped(finalLocalPoses[i].sagittalPitch, sampled.sagittalPitch, env);
                    finalLocalPoses[i].coronalFlap = Mathf.LerpUnclamped(finalLocalPoses[i].coronalFlap, sampled.coronalFlap, env);
                }
            }
        }

        public bool IsBezierChainDrivenByRawClip(BezierChainDef chainDef)
        {
            if (chainDef?.resolvedBoneIndices == null || chainDef.resolvedBoneIndices.Length == 0)
            {
                return false;
            }
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!grp.HasActiveVisualOverlay)
                {
                    continue;
                }
                bool belongsToChain = false;
                for (int k = 0; k < chainDef.resolvedBoneIndices.Length; k++)
                {
                    if (chainDef.resolvedBoneIndices[k] == grp.BoneIndex)
                    {
                        belongsToChain = true;
                        break;
                    }
                }
                if (!belongsToChain)
                {
                    continue;
                }
                GiantAnimDef clip = grp.IsAnimating ? grp.ActiveAttack?.attackAnim : grp.FadeOutClip;
                if (clip != null && !chainDef.IsStrikeAnim(clip.defName))
                {
                    return true;
                }
            }
            return false;
        }

        public bool TryGetBezierChainStrikeOverride(
            BezierChainDef chainDef,
            out string activeClipDefName,
            out float normalizedTime,
            out Vector2 targetWorldXZ)
        {
            activeClipDefName = null;
            normalizedTime = 0f;
            targetWorldXZ = Vector2.zero;
            if (chainDef?.resolvedBoneIndices == null || chainDef.resolvedBoneIndices.Length == 0)
            {
                return false;
            }

            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!grp.IsAnimating)
                {
                    continue;
                }

                bool belongsToChain = false;
                for (int k = 0; k < chainDef.resolvedBoneIndices.Length; k++)
                {
                    if (chainDef.resolvedBoneIndices[k] == grp.BoneIndex)
                    {
                        belongsToChain = true;
                        break;
                    }
                }
                if (!belongsToChain)
                {
                    continue;
                }

                GiantAnimDef clip = grp.ActiveAttack?.attackAnim;
                if (clip != null && chainDef.IsStrikeAnim(clip.defName))
                {
                    activeClipDefName = clip.defName;
                    normalizedTime = clip.durationTicks > 0.01f ? Mathf.Clamp01(grp.AnimSampleTick / clip.durationTicks) : 0f;
                    targetWorldXZ = grp.TargetWorldXZ;
                    return grp.HasTargetWorldXZ;
                }
            }

            return false;
        }

        public bool TryGetSoftWhipStrikeOverride(
            SoftWhipDef whipDef,
            out string activeClipDefName,
            out float normalizedTime,
            out Vector2 targetWorldXZ)
        {
            activeClipDefName = null;
            normalizedTime = 0f;
            targetWorldXZ = Vector2.zero;
            if (whipDef == null || string.IsNullOrEmpty(whipDef.rootBoneId))
            {
                return false;
            }

            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!grp.IsAnimating || !string.Equals(grp.Bone.nodeId, whipDef.rootBoneId, StringComparison.Ordinal))
                {
                    continue;
                }

                GiantAnimDef clip = grp.ActiveAttack?.attackAnim;
                if (clip != null && whipDef.IsStrikeAnim(clip.defName))
                {
                    activeClipDefName = clip.defName;
                    normalizedTime = clip.durationTicks > 0.01f ? Mathf.Clamp01(grp.AnimSampleTick / clip.durationTicks) : 0f;
                    targetWorldXZ = grp.TargetWorldXZ;
                    return grp.HasTargetWorldXZ;
                }
            }

            return false;
        }

        private static bool IsBoneOrDescendantOf(BoneNodeDef[] nodes, int boneIdx, int ancestorIdx)
        {
            int cur = boneIdx;
            while ((uint)cur < (uint)nodes.Length)
            {
                if (cur == ancestorIdx)
                {
                    return true;
                }
                cur = nodes[cur].parentRuntimeIndex;
            }
            return false;
        }

        public bool TryResolveLookChainAimHeading(
            SteeringLookChainDef chainDef,
            Vector2 chainPivotWorldXZ,
            ReadOnlySpan<bool> missingMask,
            out float aimHeadingDeg)
        {
            aimHeadingDeg = 0f;
            GiantRigDef rig = rigComp?.RigDef ?? activeRigDef;
            BoneNodeDef[] nodes = rig?.SortedNodes;
            if (nodes == null || chainDef?.resolvedBoneIndices == null || chainDef.resolvedBoneIndices.Length == 0 || groups.Length == 0)
            {
                return false;
            }

            int distalIdx = chainDef.resolvedBoneIndices[chainDef.resolvedBoneIndices.Length - 1];
            if ((uint)distalIdx >= (uint)nodes.Length)
            {
                return false;
            }
            if (!missingMask.IsEmpty && (uint)distalIdx < (uint)missingMask.Length && missingMask[distalIdx])
            {
                return false;
            }
            if (rigComp != null && !rigComp.IsBoneIntact(nodes[distalIdx].nodeId))
            {
                return false;
            }

            bool hasForced = HasValidForcedTarget();
            Vector2 forcedXZ = Vector2.zero;
            if (hasForced)
            {
                Vector3 fp = forcedTarget.HasThing
                    ? (forcedTarget.Thing is Pawn tp ? CompGiantRig.GetSimTickPos(tp) : forcedTarget.Thing.DrawPos)
                    : forcedTarget.Cell.ToVector3Shifted();
                forcedXZ = new Vector2(fp.x, fp.z);
            }

            float sumX = 0f;
            float sumZ = 0f;
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!IsBoneOrDescendantOf(nodes, grp.BoneIndex, distalIdx))
                {
                    continue;
                }
                if (!missingMask.IsEmpty && (uint)grp.BoneIndex < (uint)missingMask.Length && missingMask[grp.BoneIndex])
                {
                    continue;
                }
                if (rigComp != null && !rigComp.IsBoneIntact(grp.Bone.nodeId))
                {
                    continue;
                }

                Vector2 targXZ;
                float w;
                if (grp.HasTargetWorldXZ &&
                    (grp.Phase == PartWeaponPhase.WarmingUp || grp.Phase == PartWeaponPhase.FiringBurst || grp.Phase == PartWeaponPhase.FollowThrough))
                {
                    targXZ = grp.TargetWorldXZ;
                    w = 2.0f;
                }
                else if (hasForced && (!IsGroupIndependentOverlayOnly(grp) || (forcedTarget.HasThing && grp.Attacks.Length > 0 && CanPartAttackReachAndSeeTarget(grp, grp.Attacks[0], forcedTarget.Thing, checkSector: true, extraRangeSlack: 2f))))
                {
                    targXZ = forcedXZ;
                    w = 1.5f;
                }
                else if (grp.HasLastTrackedTargetXZ)
                {
                    targXZ = grp.LastTrackedTargetXZ;
                    w = 1.0f;
                }
                else
                {
                    continue;
                }

                Vector2 d = targXZ - chainPivotWorldXZ;
                if (d.sqrMagnitude <= 0.04f)
                {
                    continue;
                }

                float mAngOff = 0f;
                GiantPartAttackDef refAtk = grp.ActiveAttack ?? (grp.Attacks.Length > 0 ? grp.Attacks[0] : null);
                if (refAtk != null && refAtk.kind == GiantPartAttackKind.Ranged)
                {
                    grp.Bone.ResolveEmitterSocketOrMuzzle(refAtk.socketId, GiantBoneViewSector.Dorsal, out _, out mAngOff, out _, out _, out _);
                }

                float bearingDeg = Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg - mAngOff;
                float rad = bearingDeg * Mathf.Deg2Rad;
                sumX += Mathf.Sin(rad) * w;
                sumZ += Mathf.Cos(rad) * w;
            }

            if (sumX * sumX + sumZ * sumZ > 1e-5f)
            {
                aimHeadingDeg = SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(sumX, sumZ) * Mathf.Rad2Deg);
                return true;
            }

            return false;
        }

        private static readonly float[] FirepowerSampleOffsetsDeg =
        {
            0f, -15f, 15f, -30f, 30f, -45f, 45f, -65f, 65f, -90f, 90f, -120f, 120f, -150f, 150f, 180f
        };

        public bool HasAnyOtherHostileEngageableByPrimaryParts(Thing excludedTarget)
        {
            Pawn pawn = rigComp?.Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Map == null)
            {
                return false;
            }
            for (int i = 0; i < cachedHostileCandidates.Count; i++)
            {
                Thing cand = cachedHostileCandidates[i];
                if (cand == null || cand == excludedTarget || !IsValidHostileThing(pawn, cand) || (cand is Pawn cp && cp.Downed))
                {
                    continue;
                }
                if (IsTargetCurrentlyEngageableByPrimaryParts(cand, checkSector: true))
                {
                    return true;
                }
            }
            return false;
        }

        public bool TryFindImmediateEngageablePrimaryHostile(Thing excludedTarget, out Thing bestImmediate)
        {
            bestImmediate = null;
            Pawn pawn = rigComp?.Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Map == null || cachedHostileCandidates.Count == 0)
            {
                return false;
            }

            float bestInSectorDistSq = float.MaxValue;
            Thing bestInSector = null;
            float bestPivotTurnDeg = float.MaxValue;
            Thing bestPivotReady = null;
            float curHdg = rigComp.CurrentHeadingDeg;

            for (int i = 0; i < cachedHostileCandidates.Count; i++)
            {
                Thing cand = cachedHostileCandidates[i];
                if (cand == null
                    || cand == excludedTarget
                    || !IsValidHostileThing(pawn, cand)
                    || (cand is Pawn cp && (cp.Dead || cp.Downed))
                    || !GiantFlightUtility.CanGiantEngageTargetInCurrentDomain(pawn, rigComp, cand))
                {
                    continue;
                }

                if (IsTargetCurrentlyEngageableByPrimaryParts(cand, checkSector: true))
                {
                    float dSq = pawn.Position.DistanceToSquared(cand.Position);
                    if (dSq < bestInSectorDistSq)
                    {
                        bestInSectorDistSq = dSq;
                        bestInSector = cand;
                    }
                    continue;
                }

                if (bestInSector == null &&
                    EvaluateFirepowerAlignment(cand, out float optHdg, out _, out _, out float optDps, out bool reqCloser) &&
                    optDps > 0.01f && !reqCloser)
                {
                    float turnErr = Mathf.Abs(Mathf.DeltaAngle(curHdg, optHdg));
                    if (turnErr < bestPivotTurnDeg)
                    {
                        bestPivotTurnDeg = turnErr;
                        bestPivotReady = cand;
                    }
                }
            }

            bestImmediate = bestInSector ?? bestPivotReady;
            return bestImmediate != null;
        }

        public static float ComputeAttackTacticalDps(GiantPartAttackDef atk)
        {
            if (atk == null)
            {
                return 0f;
            }
            float cycle = Mathf.Max(0.6f, atk.warmupTime + atk.cooldownTime);
            float weight = Mathf.Max(0.25f, atk.selectionWeight);
            if (atk.kind == GiantPartAttackKind.Melee)
            {
                return (atk.power * Mathf.Max(1, atk.burstShotCount) * weight) / cycle;
            }

            float perShot = 26f;
            ProjectileProperties proj = atk.projectileDef?.projectile;
            if (proj != null && proj.damageDef != null)
            {
                int baseDmg = proj.GetDamageAmount((Thing)null);
                if (baseDmg > 0)
                {
                    float aoeMult = proj.explosionRadius > 0.1f
                        ? 1f + Mathf.Clamp(proj.explosionRadius * 0.25f, 0.15f, 0.85f)
                        : 1f;
                    perShot = baseDmg * aoeMult;
                }
            }
            return (perShot * Mathf.Max(1, atk.burstShotCount) * weight) / cycle;
        }

        public static bool IsCapitalMainRangedWeapon(
            float atkRange,
            float atkMinRange,
            float atkDps,
            float maxRangedRange,
            float maxSingleRangedGroupDps)
        {
            if (maxRangedRange <= 0.01f)
            {
                return false;
            }
            return atkRange >= maxRangedRange * 0.78f
                || atkDps >= maxSingleRangedGroupDps * 0.55f
                || atkMinRange >= 3.0f;
        }

        public static bool IsMainGunInMinRangeDeadzone(
            float atkRange,
            float atkMinRange,
            float atkDps,
            float maxRangedRange,
            float maxSingleRangedGroupDps,
            float alignedMuzzleDist,
            float deadzoneMargin = 0.65f)
        {
            if (atkMinRange <= 0.05f)
            {
                return false;
            }
            if (!IsCapitalMainRangedWeapon(atkRange, atkMinRange, atkDps, maxRangedRange, maxSingleRangedGroupDps))
            {
                return false;
            }
            return alignedMuzzleDist < atkMinRange + deadzoneMargin;
        }

        private static Vector2 RotateClockwiseXZ(Vector2 v, float deltaDeg)
        {
            if (Mathf.Abs(deltaDeg) <= 0.01f)
            {
                return v;
            }
            float rad = deltaDeg * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad);
            float s = Mathf.Sin(rad);
            return new Vector2(v.x * c + v.y * s, -v.x * s + v.y * c);
        }

        private float ComputeSampledRangedMuzzleDistance(
            BonePartWeaponGroup grp,
            GiantPartAttackDef atk,
            Vector2 simPosXZ,
            float curHeading,
            float candHeading,
            Vector2 targetBodyXZ,
            Thing target)
        {
            Vector2 curOriginXZ = GetBoneOriginWorldXZ(grp, atk, out _);
            float deltaDeg = Mathf.DeltaAngle(curHeading, candHeading);

            Vector2 sampleOriginXZ;
            if (grp != null && grp.IsTurret && (uint)grp.BoneIndex < (uint)livePoseScratch.Length && livePoseScratch[grp.BoneIndex].visible)
            {
                Vector2 curMountXZ = livePoseScratch[grp.BoneIndex].jointWorldXZ;
                Vector2 rotMountXZ = simPosXZ + RotateClockwiseXZ(curMountXZ - simPosXZ, deltaDeg);
                float barrelLen = Vector2.Distance(curMountXZ, curOriginXZ);
                if (barrelLen > 0.02f)
                {
                    Vector2 toTarg = targetBodyXZ - rotMountXZ;
                    float desiredAimDeg = toTarg.sqrMagnitude > 0.01f
                        ? Mathf.Atan2(toTarg.x, toTarg.y) * Mathf.Rad2Deg
                        : candHeading;
                    float mountCenterDeg = SkeletalKinematics.NormalizeAngle360(candHeading + grp.Bone.baseAngle);
                    float aimErr = Mathf.Clamp(Mathf.DeltaAngle(mountCenterDeg, desiredAimDeg), -grp.MaxTurretAimAngleDeg, grp.MaxTurretAimAngleDeg);
                    float actualAimRad = (mountCenterDeg + aimErr) * Mathf.Deg2Rad;
                    sampleOriginXZ = rotMountXZ + new Vector2(Mathf.Sin(actualAimRad), Mathf.Cos(actualAimRad)) * barrelLen;
                }
                else
                {
                    sampleOriginXZ = rotMountXZ;
                }
            }
            else
            {
                sampleOriginXZ = simPosXZ + RotateClockwiseXZ(curOriginXZ - simPosXZ, deltaDeg);
            }

            return MeasureDistanceFromPartToTarget(sampleOriginXZ, atk, grp, target, out _);
        }

        /// <summary>
        /// Computes the hull heading that maximizes simultaneous intact weapon DPS on target,
        /// current vs optimal firepower, and whether the giant needs to move closer (or retreat out of main-gun minRange deadzone)
        /// to bring its primary arsenal into optimal engagement range.
        /// </summary>
        public bool EvaluateFirepowerAlignment(
            Thing target,
            out float optimalHullHeadingDeg,
            out Vector2 targetBodyXZ,
            out float currentFirepowerDps,
            out float optimalFirepowerDps,
            out bool requiresCloserApproach)
        {
            optimalHullHeadingDeg = rigComp?.CurrentHeadingDeg ?? 0f;
            targetBodyXZ = Vector2.zero;
            currentFirepowerDps = 0f;
            optimalFirepowerDps = 0f;
            requiresCloserApproach = true;

            Pawn caster = rigComp?.Pawn;
            Map map = caster?.Map;
            if (caster == null || map == null || !caster.Spawned || target == null || !target.Spawned || target.Map != map)
            {
                return false;
            }
            if (!rigComp.SolveLiveWorldPoses(livePoseScratch))
            {
                return false;
            }

            Vector3 simPos3 = CompGiantRig.GetSimTickPos(caster);
            Vector2 simPosXZ = new Vector2(simPos3.x, simPos3.z);

            CompGiantRig targetRig = GiantFastLookup.TryGetRig(target as Pawn);
            if (targetRig != null && targetRig.Footprint.collisionCells != null && targetRig.Footprint.collisionCells.Count > 0)
            {
                float bestSq = float.MaxValue;
                IntVec3 bestCell = target.Position;
                List<IntVec3> solid = targetRig.Footprint.collisionCells;
                for (int i = 0; i < solid.Count; i++)
                {
                    float dx = (solid[i].x + 0.5f) - simPosXZ.x;
                    float dz = (solid[i].z + 0.5f) - simPosXZ.y;
                    float dSq = dx * dx + dz * dz;
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                        bestCell = solid[i];
                    }
                }
                targetBodyXZ = new Vector2(bestCell.x + 0.5f, bestCell.z + 0.5f);
            }
            else if (targetRig != null
                && targetRig.Footprint.hitCells.Count > 0
                && !GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, target))
            {
                float bestSq = float.MaxValue;
                IntVec3 bestCell = target.Position;
                List<HitCellEntry> hits = targetRig.Footprint.hitCells;
                for (int i = 0; i < hits.Count; i++)
                {
                    float dx = (hits[i].cell.x + 0.5f) - simPosXZ.x;
                    float dz = (hits[i].cell.z + 0.5f) - simPosXZ.y;
                    float dSq = dx * dx + dz * dz;
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                        bestCell = hits[i].cell;
                    }
                }
                targetBodyXZ = new Vector2(bestCell.x + 0.5f, bestCell.z + 0.5f);
            }
            else
            {
                Vector3 tp = target is Pawn p ? CompGiantRig.GetSimTickPos(p) : target.DrawPos;
                targetBodyXZ = new Vector2(tp.x, tp.z);
            }

            Vector2 toTarget = targetBodyXZ - simPosXZ;
            float centerDistToTargetBody = toTarget.magnitude;
            float bearingToTargetDeg = toTarget.sqrMagnitude > 0.01f
                ? Mathf.Atan2(toTarget.x, toTarget.y) * Mathf.Rad2Deg
                : rigComp.CurrentHeadingDeg;
            optimalHullHeadingDeg = bearingToTargetDeg;

            if (GiantFastLookup.TryGetCarrierHangar(caster) is CompCarrierHangar hangar && hangar.MaxAllowedInterceptors > 0)
            {
                float d = caster.Position.DistanceTo(target.Position);
                currentFirepowerDps = d <= hangar.Props.engagementRange ? 30f : 0f;
                optimalFirepowerDps = 30f;
                requiresCloserApproach = d > hangar.Props.engagementRange;
                if (groups.Length == 0)
                {
                    return true;
                }
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(caster);
            bool hasPrimary = HasAnyIntactNonIndependentAttack(cockpit);
            bool isForcedTargetMatch = HasValidForcedTarget() && forcedTarget.HasThing && forcedTarget.Thing == target;
            bool forceMeleeMode = isForcedTargetMatch && forcedAttackMode == GiantForcedAttackMode.Melee;
            bool forceRangedMode = isForcedTargetMatch && forcedAttackMode == GiantForcedAttackMode.Ranged;
            bool hasIntactRanged = false;
            bool hasIntactMelee = false;
            float totalIntactPrimaryDps = 0f;
            float totalIntactMeleeDps = 0f;
            float totalIntactRangedDps = 0f;
            float maxRangedRange = 0f;
            float maxSingleRangedGroupDps = 0f;

            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
                {
                    continue;
                }
                float grpBestNow = 0f;
                float grpBestMax = 0f;
                float grpBestMeleeMax = 0f;
                float grpBestRangedMax = 0f;
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    GiantPartAttackDef atk = grp.Attacks[a];
                    if (hasPrimary && atk.independentOverlay)
                    {
                        continue;
                    }
                    if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                    {
                        continue;
                    }
                    if (forceMeleeMode && atk.kind != GiantPartAttackKind.Melee)
                    {
                        continue;
                    }
                    if (forceRangedMode && atk.kind == GiantPartAttackKind.Melee)
                    {
                        continue;
                    }
                    if (atk.kind == GiantPartAttackKind.Melee)
                    {
                        if (GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, target) ||
                            !GiantFlightUtility.CanAirborneAttackerMeleeTarget(rigComp, atk, target))
                        {
                            continue;
                        }
                        hasIntactMelee = true;
                    }
                    else
                    {
                        hasIntactRanged = true;
                        if (atk.range > maxRangedRange)
                        {
                            maxRangedRange = atk.range;
                        }
                    }

                    float dps = ComputeAttackTacticalDps(atk);
                    if (dps > grpBestMax)
                    {
                        grpBestMax = dps;
                    }
                    if (atk.kind == GiantPartAttackKind.Melee)
                    {
                        if (dps > grpBestMeleeMax)
                        {
                            grpBestMeleeMax = dps;
                        }
                    }
                    else if (dps > grpBestRangedMax)
                    {
                        grpBestRangedMax = dps;
                    }
                    if (CanPartAttackReachAndSeeTarget(grp, atk, target, checkSector: true) && dps > grpBestNow)
                    {
                        grpBestNow = dps;
                    }
                }
                if (grpBestRangedMax > maxSingleRangedGroupDps)
                {
                    maxSingleRangedGroupDps = grpBestRangedMax;
                }
                currentFirepowerDps += grpBestNow;
                totalIntactPrimaryDps += grpBestMax;
                totalIntactMeleeDps += grpBestMeleeMax;
                totalIntactRangedDps += grpBestRangedMax;
            }

            float bestSampleScore = float.MinValue;
            float bestSampleDps = 0f;
            float bestSampleHeading = bearingToTargetDeg;
            float curHeading = rigComp.CurrentHeadingDeg;
            float hullExtent = Mathf.Max(1.0f, rigComp.Footprint.maxCollisionHorizontalExtent);

            for (int s = 0; s < FirepowerSampleOffsetsDeg.Length; s++)
            {
                float relDeg = FirepowerSampleOffsetsDeg[s];
                float candHeading = SkeletalKinematics.NormalizeAngle360(bearingToTargetDeg - relDeg);
                float sampleDps = 0f;
                float centralityBonus = 0f;

                for (int g = 0; g < groups.Length; g++)
                {
                    BonePartWeaponGroup grp = groups[g];
                    if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
                    {
                        continue;
                    }
                    float grpSampleBest = 0f;
                    float grpCentrality = 0f;
                    for (int a = 0; a < grp.Attacks.Length; a++)
                    {
                        GiantPartAttackDef atk = grp.Attacks[a];
                        if (hasPrimary && atk.independentOverlay)
                        {
                            continue;
                        }
                        if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                        {
                            continue;
                        }
                        if (forceMeleeMode && atk.kind != GiantPartAttackKind.Melee)
                        {
                            continue;
                        }
                        if (forceRangedMode && atk.kind == GiantPartAttackKind.Melee)
                        {
                            continue;
                        }
                        if (atk.kind == GiantPartAttackKind.Melee)
                        {
                            if (GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, target) ||
                                !GiantFlightUtility.CanAirborneAttackerMeleeTarget(rigComp, atk, target))
                            {
                                continue;
                            }
                            Vector2 partOriginXZ = GetBoneOriginWorldXZ(grp, atk, out _);
                            Vector2 anchorXZ = ResolveRangeAnchorWorldXZ(partOriginXZ, atk, grp);
                            float anchorDistFromCenter = Vector2.Distance(simPosXZ, anchorXZ);
                            float effectiveMaxR = atk.range;
                            if (!atk.useSoftWhipTip &&
                                !((uint)grp.BoneIndex < (uint)bezierChainBoneMask.Length && bezierChainBoneMask[grp.BoneIndex]))
                            {
                                int anchorIdx = ResolveRangeAnchorBoneIndex(atk, grp);
                                if (anchorIdx >= 0)
                                {
                                    float span = ComputeFlatAnatomicalSpanFromAnchor(grp, atk);
                                    effectiveMaxR = Mathf.Min(effectiveMaxR, span + 1.60f + (targetRig != null ? 0.30f : 0f));
                                }
                            }
                            float maxRotReach = anchorDistFromCenter + effectiveMaxR + 0.45f;
                            if (centerDistToTargetBody > maxRotReach)
                            {
                                continue;
                            }
                            bool inBezier = (uint)grp.BoneIndex < (uint)bezierChainBoneMask.Length && bezierChainBoneMask[grp.BoneIndex];
                            bool fullSpin = atk.attackAnim != null && atk.attackAnim.coupledBodyMotionFactor >= 0.99f;
                            if (!atk.useSoftWhipTip && !inBezier && !fullSpin &&
                                (uint)a < (uint)grp.StrikeProfiles.Length && grp.StrikeProfiles[a].Valid)
                            {
                                MeleeStrikeImpactProfile prof = grp.StrikeProfiles[a];
                                Vector2 candAnchorXZ = simPosXZ + RotateLocalToHeadingXZ(prof.AnchorRestLocalXZ, candHeading);
                                Vector2 candNominalImpactXZ = simPosXZ + RotateLocalToHeadingXZ(prof.ImpactLocalXZ, candHeading);
                                Vector2 candAimVec = grp.IsLookChainDriven
                                    ? SteerVectorTowardsXZ(candNominalImpactXZ - candAnchorXZ, targetBodyXZ - candAnchorXZ, maxTurnDeg: 28f)
                                    : (candNominalImpactXZ - candAnchorXZ);
                                float candFleshGap = MeasurePointDistanceToTargetVisualFlesh(candAnchorXZ + candAimVec, target, targetRig);
                                float lungeBudget = (rigComp.RigDef?.enableDynamicMeleeContactLunge ?? true) && !grp.IsTurret && !atk.independentOverlay
                                    ? Mathf.Clamp(rigComp.RigDef?.maxDynamicMeleeContactLungeCells ?? 0.85f, 0f, 2.0f)
                                    : 0.15f;
                                if (candFleshGap > lungeBudget + 0.42f)
                                {
                                    continue;
                                }
                            }
                        }
                        else
                        {
                            float sampledMuzzleDist = ComputeSampledRangedMuzzleDistance(
                                grp,
                                atk,
                                simPosXZ,
                                curHeading,
                                candHeading,
                                targetBodyXZ,
                                target);
                            float effectiveMinDist = sampledMuzzleDist;
                            if (targetRig != null && target is Pawn tPawn)
                            {
                                Vector3 tCenter = CompGiantRig.GetSimTickPos(tPawn);
                                float cDist = Vector2.Distance(simPosXZ, new Vector2(tCenter.x, tCenter.z));
                                effectiveMinDist = ComputeEffectiveRangedMinRangeDistance(
                                    sampledMuzzleDist,
                                    cDist,
                                    targetIsGiant: true,
                                    targetIsAirborne: targetRig.IsAirborneOrTakingOff);
                            }
                            if (effectiveMinDist < atk.minRange || sampledMuzzleDist > atk.range + 0.8f)
                            {
                                continue;
                            }
                        }

                        float mountBaseDeg = grp.Bone.baseAngle;
                        float localSectorCenter = grp.IsTurret
                            ? mountBaseDeg
                            : atk.sectorCenterDeg;
                        float effectiveHalfArc = grp.IsTurret
                            ? (grp.MaxTurretAimAngleDeg >= 175f ? 180f : Mathf.Min(atk.sectorHalfAngleDeg, grp.MaxTurretAimAngleDeg))
                            : atk.sectorHalfAngleDeg;

                        float angErr = Mathf.Abs(Mathf.DeltaAngle(localSectorCenter, relDeg));
                        if (angErr > effectiveHalfArc)
                        {
                            continue;
                        }

                        float dps = ComputeAttackTacticalDps(atk);
                        if (dps > grpSampleBest)
                        {
                            grpSampleBest = dps;
                            grpCentrality = effectiveHalfArc < 175f ? (1f - angErr / Mathf.Max(10f, effectiveHalfArc)) * 0.35f : 0f;
                        }
                    }
                    sampleDps += grpSampleBest;
                    centralityBonus += grpCentrality;
                }

                float turnPenalty = Mathf.Abs(Mathf.DeltaAngle(curHeading, candHeading)) * 0.004f;
                float totalScore = sampleDps + centralityBonus - turnPenalty;
                if (totalScore > bestSampleScore)
                {
                    bestSampleScore = totalScore;
                    bestSampleDps = sampleDps;
                    bestSampleHeading = candHeading;
                }
            }

            optimalFirepowerDps = bestSampleDps;
            optimalHullHeadingDeg = bestSampleDps > 0.01f ? bestSampleHeading : bearingToTargetDeg;

            if (hasIntactRanged && !hasIntactMelee)
            {
                bool mainGunInDeadzone = false;
                for (int g = 0; g < groups.Length && !mainGunInDeadzone; g++)
                {
                    BonePartWeaponGroup grp = groups[g];
                    if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
                    {
                        continue;
                    }
                    for (int a = 0; a < grp.Attacks.Length; a++)
                    {
                        GiantPartAttackDef atk = grp.Attacks[a];
                        if ((hasPrimary && atk.independentOverlay) || atk.kind == GiantPartAttackKind.Melee)
                        {
                            continue;
                        }
                        if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                        {
                            continue;
                        }
                        float atkDps = ComputeAttackTacticalDps(atk);
                        float alignedMuzzleDist = ComputeSampledRangedMuzzleDistance(
                            grp,
                            atk,
                            simPosXZ,
                            curHeading,
                            optimalHullHeadingDeg,
                            targetBodyXZ,
                            target);
                        float effectiveAlignedMinDist = alignedMuzzleDist;
                        if (targetRig != null && target is Pawn dtPawn)
                        {
                            Vector3 dtCenter = CompGiantRig.GetSimTickPos(dtPawn);
                            float dcDist = Vector2.Distance(simPosXZ, new Vector2(dtCenter.x, dtCenter.z));
                            effectiveAlignedMinDist = ComputeEffectiveRangedMinRangeDistance(
                                alignedMuzzleDist,
                                dcDist,
                                targetIsGiant: true,
                                targetIsAirborne: targetRig.IsAirborneOrTakingOff);
                        }
                        if (IsMainGunInMinRangeDeadzone(
                                atk.range,
                                atk.minRange,
                                atkDps,
                                maxRangedRange,
                                maxSingleRangedGroupDps,
                                effectiveAlignedMinDist))
                        {
                            mainGunInDeadzone = true;
                            break;
                        }
                    }
                }

                bool huggedInsideStandoff = false;
                if (!mainGunInDeadzone && maxRangedRange >= 16f && !rigComp.IsCurrentlyAirborne)
                {
                    float minGapSq = targetRig != null && targetRig.Footprint.collisionCells != null && targetRig.Footprint.collisionCells.Count > 0
                        ? ComputeGroupHullGapSq(caster.Position, rigComp.Footprint.localCollisionOffsets, targetRig.Footprint.collisionCells, targetRig.Footprint.collisionCellSet)
                        : ComputeGroupHullGapToRectSq(caster.Position, rigComp.Footprint.localCollisionOffsets, target.OccupiedRect());
                    if (minGapSq <= 2.5f * 2.5f)
                    {
                        huggedInsideStandoff = true;
                    }
                }

                requiresCloserApproach = mainGunInDeadzone
                    || huggedInsideStandoff
                    || optimalFirepowerDps <= 0.01f
                    || (currentFirepowerDps <= 0.01f && Mathf.Abs(Mathf.DeltaAngle(curHeading, optimalHullHeadingDeg)) <= 12f);
            }
            else if (hasIntactMelee && (!hasIntactRanged || ShouldHybridPreferMeleeCloseAssault(
                         totalIntactMeleeDps,
                         totalIntactRangedDps,
                         rigComp.RigDef?.rushWhenEnemyFar ?? false)))
            {
                bool avoidAirborneBelly = ShouldAvoidAirborneTargetBelly(rigComp, target, targetRig);
                bool useCanopyPerimeter = ShouldUseVisualCanopyPerimeterForTarget(rigComp, target, targetRig);
                IReadOnlyList<IntVec3> selfStandoffOffsets = useCanopyPerimeter
                    ? rigComp.Footprint.localPawnExclusionOffsets
                    : rigComp.Footprint.localCollisionOffsets;
                float minGapSq = targetRig != null && targetRig.Footprint.collisionCells != null && targetRig.Footprint.collisionCells.Count > 0
                    ? ComputeGroupHullGapSq(caster.Position, rigComp.Footprint.localCollisionOffsets, targetRig.Footprint.collisionCells, targetRig.Footprint.collisionCellSet)
                    : (avoidAirborneBelly
                        ? ComputeHullGapToHitCellsSq(caster.Position, rigComp.Footprint.localCollisionOffsets, targetRig.Footprint.hitCells, targetRig.Footprint.hitCellSet)
                        : ComputeGroupHullGapToRectSq(caster.Position, selfStandoffOffsets, target.OccupiedRect()));
                bool underLowSkimBelly = avoidAirborneBelly &&
                    CountHullOverlapWithForbiddenSet(caster.Position, rigComp.Footprint.localCollisionOffsets, targetRig.Footprint.hitCellSet) > 0;
                float snugLimit = avoidAirborneBelly ? 1.85f : 1.45f;
                bool hullCloseEnough = !underLowSkimBelly && minGapSq <= snugLimit * snugLimit;
                if (!hullCloseEnough && !underLowSkimBelly && TryGetPrimaryMeleeStrikeProfile(out MeleeStrikeImpactProfile profClose))
                {
                    Vector2 strikeAtOpt = simPosXZ + RotateLocalToHeadingXZ(profClose.ImpactLocalXZ, optimalHullHeadingDeg);
                    float fGap = MeasurePointDistanceToTargetVisualFlesh(strikeAtOpt, target, targetRig);
                    if (fGap <= 0.40f && minGapSq <= (snugLimit + 2.15f) * (snugLimit + 2.15f))
                    {
                        hullCloseEnough = true;
                    }
                }
                float requiredMeleeDps = Mathf.Max(0.5f, totalIntactMeleeDps * 0.55f);
                if (useCanopyPerimeter && hullCloseEnough && optimalFirepowerDps > 0.01f)
                {
                    // At the visual canopy perimeter against a 1x1 pawn, stepping any closer would push the pawn away;
                    // accept the optimal reachable perimeter melee DPS without demanding tucked chest limbs to also reach past the snout.
                    requiredMeleeDps = Mathf.Min(requiredMeleeDps, Mathf.Max(0.5f, optimalFirepowerDps * 0.90f));
                }
                bool alreadyAlignedButMissing = Mathf.Abs(Mathf.DeltaAngle(curHeading, optimalHullHeadingDeg)) <= 15f
                    && currentFirepowerDps < requiredMeleeDps;

                requiresCloserApproach = underLowSkimBelly
                    || !hullCloseEnough
                    || optimalFirepowerDps < requiredMeleeDps
                    || alreadyAlignedButMissing;
            }
            else
            {
                // Ranged-dominant hybrid (e.g. hybrid whose primary melee claws were severed): satisfied if optimal ranged or fallback melee DPS is active
                requiresCloserApproach = optimalFirepowerDps <= 0.01f
                    || (currentFirepowerDps <= 0.01f && Mathf.Abs(Mathf.DeltaAngle(curHeading, optimalHullHeadingDeg)) <= 14f);
            }

            return totalIntactPrimaryDps > 0.01f || optimalFirepowerDps > 0.01f;
        }

        public static bool ShouldHybridPreferMeleeCloseAssault(
            float totalIntactMeleeDps,
            float totalIntactRangedDps,
            bool rushWhenEnemyFar)
        {
            if (totalIntactMeleeDps <= 0.01f)
            {
                return false;
            }
            if (totalIntactRangedDps <= 0.01f)
            {
                return true;
            }
            return totalIntactMeleeDps >= totalIntactRangedDps * 0.85f
                || (rushWhenEnemyFar && totalIntactMeleeDps >= totalIntactRangedDps * 0.55f);
        }

        public bool PrefersMeleeCloseAssaultAgainst(Thing target)
        {
            Pawn caster = rigComp?.Pawn;
            if (caster == null || target == null)
            {
                return false;
            }
            if (HasValidForcedTarget() && forcedTarget.HasThing && forcedTarget.Thing == target)
            {
                if (forcedAttackMode == GiantForcedAttackMode.Melee)
                {
                    return true;
                }
                if (forcedAttackMode == GiantForcedAttackMode.Ranged)
                {
                    return false;
                }
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(caster);
            bool hasPrimary = HasAnyIntactNonIndependentAttack(cockpit);
            float totalIntactMeleeDps = 0f;
            float totalIntactRangedDps = 0f;

            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
                {
                    continue;
                }
                float grpMelee = 0f;
                float grpRanged = 0f;
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    GiantPartAttackDef atk = grp.Attacks[a];
                    if (hasPrimary && atk.independentOverlay)
                    {
                        continue;
                    }
                    if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                    {
                        continue;
                    }
                    if (atk.kind == GiantPartAttackKind.Melee)
                    {
                        if (GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, target) ||
                            !GiantFlightUtility.CanAirborneAttackerMeleeTarget(rigComp, atk, target))
                        {
                            continue;
                        }
                        float dps = ComputeAttackTacticalDps(atk);
                        if (dps > grpMelee)
                        {
                            grpMelee = dps;
                        }
                    }
                    else
                    {
                        float dps = ComputeAttackTacticalDps(atk);
                        if (dps > grpRanged)
                        {
                            grpRanged = dps;
                        }
                    }
                }
                totalIntactMeleeDps += grpMelee;
                totalIntactRangedDps += grpRanged;
            }

            return ShouldHybridPreferMeleeCloseAssault(
                totalIntactMeleeDps,
                totalIntactRangedDps,
                rigComp.RigDef?.rushWhenEnemyFar ?? false);
        }

        private static float ComputeGroupHullGapSq(
            IntVec3 anchor,
            IReadOnlyList<IntVec3> attackerOffsets,
            IReadOnlyList<IntVec3> targetSolidCells,
            HashSet<IntVec3> targetSolidSet)
        {
            float minSq = float.MaxValue;
            if (attackerOffsets != null && attackerOffsets.Count > 0)
            {
                for (int i = 0; i < attackerOffsets.Count; i++)
                {
                    IntVec3 ac = anchor + attackerOffsets[i];
                    if (targetSolidSet != null && targetSolidSet.Contains(ac))
                    {
                        return 0f;
                    }
                    for (int j = 0; j < targetSolidCells.Count; j++)
                    {
                        int dx = ac.x - targetSolidCells[j].x;
                        int dz = ac.z - targetSolidCells[j].z;
                        float dSq = dx * dx + dz * dz;
                        if (dSq < minSq)
                        {
                            minSq = dSq;
                            if (minSq <= 0f)
                            {
                                return 0f;
                            }
                        }
                    }
                }
                return minSq;
            }
            for (int j = 0; j < targetSolidCells.Count; j++)
            {
                int dx = anchor.x - targetSolidCells[j].x;
                int dz = anchor.z - targetSolidCells[j].z;
                float dSq = dx * dx + dz * dz;
                if (dSq < minSq) minSq = dSq;
            }
            return minSq;
        }

        public static float ComputeGroupHullGapToRectSq(
            IntVec3 anchor,
            IReadOnlyList<IntVec3> attackerOffsets,
            CellRect targetRect)
        {
            float minSq = float.MaxValue;
            if (attackerOffsets != null && attackerOffsets.Count > 0)
            {
                for (int i = 0; i < attackerOffsets.Count; i++)
                {
                    IntVec3 ac = anchor + attackerOffsets[i];
                    int cx = Mathf.Clamp(ac.x, targetRect.minX, targetRect.maxX);
                    int cz = Mathf.Clamp(ac.z, targetRect.minZ, targetRect.maxZ);
                    float dSq = (ac.x - cx) * (ac.x - cx) + (ac.z - cz) * (ac.z - cz);
                    if (dSq < minSq)
                    {
                        minSq = dSq;
                        if (minSq <= 0f) return 0f;
                    }
                }
                return minSq;
            }
            int rx = Mathf.Clamp(anchor.x, targetRect.minX, targetRect.maxX);
            int rz = Mathf.Clamp(anchor.z, targetRect.minZ, targetRect.maxZ);
            return (anchor.x - rx) * (anchor.x - rx) + (anchor.z - rz) * (anchor.z - rz);
        }

        public static bool ShouldUseVisualCanopyPerimeterForTarget(CompGiantRig attackerRig, Thing target, CompGiantRig targetRig)
        {
            if (attackerRig == null || attackerRig.IsCurrentlyAirborne || targetRig != null)
            {
                return false;
            }
            if (attackerRig.RigDef != null && !attackerRig.RigDef.enableCollisionPawnPushOut)
            {
                return false;
            }
            if (!(target is Pawn targetPawn) || (targetPawn.health != null && GiantFootprintMapComponent.ShouldPawnIgnoreGiantCollision(targetPawn)))
            {
                return false;
            }
            return attackerRig.Footprint.localPawnExclusionOffsets.Count > 0;
        }

        public static bool IsLiveLowSkimOpponent(Thing target, CompGiantRig targetRig)
        {
            return target is Pawn tp
                && !tp.Dead
                && !tp.Downed
                && targetRig != null
                && targetRig.IsCurrentlyAirborne
                && targetRig.FlightLocomotion?.Def != null
                && targetRig.FlightLocomotion.Def.altitudeTier == FlightAltitudeTier.LowSkim
                && targetRig.Footprint.hitCells != null
                && targetRig.Footprint.hitCells.Count > 0;
        }

        public static bool IsLiveAirborneFootprintOpponent(Thing target, CompGiantRig targetRig)
        {
            return target is Pawn tp
                && !tp.Dead
                && !tp.Downed
                && targetRig != null
                && targetRig.IsCurrentlyAirborne
                && targetRig.Footprint.hitCells != null
                && targetRig.Footprint.hitCells.Count > 0;
        }

        public static bool ShouldAvoidAirborneTargetBelly(CompGiantRig attackerRig, Thing target, CompGiantRig targetRig)
        {
            if (attackerRig == null || targetRig == null ||
                (targetRig.Footprint.collisionCells != null && targetRig.Footprint.collisionCells.Count > 0))
            {
                return false;
            }
            return attackerRig.IsCurrentlyAirborne
                ? IsLiveAirborneFootprintOpponent(target, targetRig)
                : IsLiveLowSkimOpponent(target, targetRig);
        }

        public static int CountHullOverlapWithForbiddenSet(
            IntVec3 anchor,
            IReadOnlyList<IntVec3> attackerOffsets,
            HashSet<IntVec3> forbiddenSet)
        {
            if (forbiddenSet == null || forbiddenSet.Count == 0)
            {
                return 0;
            }
            if (attackerOffsets != null && attackerOffsets.Count > 0)
            {
                return GiantHullCollisionUtility.CountTranslatedHullOverlap(anchor, attackerOffsets, forbiddenSet);
            }
            return forbiddenSet.Contains(anchor) ? 1 : 0;
        }

        public static float ComputeHullGapToHitCellsSq(
            IntVec3 anchor,
            IReadOnlyList<IntVec3> attackerOffsets,
            IReadOnlyList<HitCellEntry> targetHitCells,
            HashSet<IntVec3> targetHitSet)
        {
            float minSq = float.MaxValue;
            if (targetHitCells == null || targetHitCells.Count == 0)
            {
                return minSq;
            }
            if (attackerOffsets != null && attackerOffsets.Count > 0)
            {
                for (int i = 0; i < attackerOffsets.Count; i++)
                {
                    IntVec3 ac = anchor + attackerOffsets[i];
                    if (targetHitSet != null && targetHitSet.Contains(ac))
                    {
                        return 0f;
                    }
                    for (int j = 0; j < targetHitCells.Count; j++)
                    {
                        IntVec3 hc = targetHitCells[j].cell;
                        int dx = ac.x - hc.x;
                        int dz = ac.z - hc.z;
                        float dSq = dx * dx + dz * dz;
                        if (dSq < minSq)
                        {
                            minSq = dSq;
                            if (minSq <= 1f)
                            {
                                return minSq;
                            }
                        }
                    }
                }
                return minSq;
            }
            if (targetHitSet != null && targetHitSet.Contains(anchor))
            {
                return 0f;
            }
            for (int j = 0; j < targetHitCells.Count; j++)
            {
                IntVec3 hc = targetHitCells[j].cell;
                int dx = anchor.x - hc.x;
                int dz = anchor.z - hc.z;
                float dSq = dx * dx + dz * dz;
                if (dSq < minSq)
                {
                    minSq = dSq;
                }
            }
            return minSq;
        }

        public bool IsTargetOptimallyEngagedByPrimaryParts(Thing target)
        {
            if (rigComp?.Pawn != null)
            {
                CompGiantRig tRig = GiantFastLookup.TryGetRig(target as Pawn);
                if (ShouldAvoidAirborneTargetBelly(rigComp, target, tRig) &&
                    CountHullOverlapWithForbiddenSet(rigComp.Pawn.Position, rigComp.Footprint.localCollisionOffsets, tRig.Footprint.hitCellSet) > 0)
                {
                    return false;
                }
            }
            if (!EvaluateFirepowerAlignment(
                    target,
                    out _,
                    out _,
                    out _,
                    out float optDps,
                    out bool requiresCloser))
            {
                return false;
            }
            return optDps > 0.01f && !requiresCloser;
        }

        public bool ShouldRotateHullTowardTarget(Thing target)
        {
            bool hasAnyIntactFull360Turret = false;
            bool hasAnyLimitedArcWeapon = false;
            bool isForcedMatch = target != null && HasValidForcedTarget() && forcedTarget.HasThing && forcedTarget.Thing == target;
            bool onlyRanged = isForcedMatch && forcedAttackMode == GiantForcedAttackMode.Ranged;
            bool onlyMelee = isForcedMatch && forcedAttackMode == GiantForcedAttackMode.Melee;

            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (IsGroupIndependentOverlayOnly(grp) || (rigComp != null && !rigComp.IsBoneIntact(grp.Bone.nodeId)))
                {
                    continue;
                }
                if (onlyRanged || onlyMelee)
                {
                    bool groupMatchesMode = false;
                    for (int a = 0; a < grp.Attacks.Length; a++)
                    {
                        bool isMelee = grp.Attacks[a].kind == GiantPartAttackKind.Melee;
                        if ((onlyMelee && isMelee) || (onlyRanged && !isMelee))
                        {
                            groupMatchesMode = true;
                            break;
                        }
                    }
                    if (!groupMatchesMode)
                    {
                        continue;
                    }
                }
                if (grp.IsTurret && grp.MaxTurretAimAngleDeg >= 175f)
                {
                    hasAnyIntactFull360Turret = true;
                }
                else
                {
                    hasAnyLimitedArcWeapon = true;
                }
            }

            if (rigComp?.VehicleLocomotion is VehicleLocomotionRuntime veh &&
                veh.Def.lockHullHeadingWhenParked &&
                !veh.Def.allowHullTraverseForFixedWeapons)
            {
                if (hasAnyIntactFull360Turret)
                {
                    return false;
                }
                if (target == null)
                {
                    return false;
                }
            }

            if (target != null && rigComp != null &&
                EvaluateFirepowerAlignment(target, out float optHullDeg, out _, out float curDps, out float optDps, out _))
            {
                if (optDps > curDps + 0.25f)
                {
                    return true;
                }
                if (!hasAnyIntactFull360Turret || hasAnyLimitedArcWeapon)
                {
                    return Mathf.Abs(Mathf.DeltaAngle(rigComp.CurrentHeadingDeg, optHullDeg)) > 5f;
                }
                return false;
            }

            return !hasAnyIntactFull360Turret || hasAnyLimitedArcWeapon;
        }

        public bool TryGetPrimaryCombatLookTargetXZ(out Vector2 lookTargetXZ, out bool shouldRotateHullToTarget)
        {
            return TryGetPrimaryCombatLookTargetXZ(out lookTargetXZ, out shouldRotateHullToTarget, out _);
        }

        public bool TryGetPrimaryCombatLookTargetXZ(
            out Vector2 lookTargetXZ,
            out bool shouldRotateHullToTarget,
            out float optimalHullHeadingDeg)
        {
            lookTargetXZ = Vector2.zero;
            shouldRotateHullToTarget = false;
            optimalHullHeadingDeg = rigComp?.CurrentHeadingDeg ?? 0f;

            if (HasValidForcedTarget())
            {
                if (forcedTarget.HasThing && EvaluateFirepowerAlignment(forcedTarget.Thing, out float forcedOptHdg, out Vector2 forcedBodyXZ, out _, out _, out _))
                {
                    lookTargetXZ = forcedBodyXZ;
                    optimalHullHeadingDeg = forcedOptHdg;
                    shouldRotateHullToTarget = ShouldRotateHullTowardTarget(forcedTarget.Thing);
                    return true;
                }
                Vector3 fp = forcedTarget.HasThing
                    ? (forcedTarget.Thing is Pawn tp ? CompGiantRig.GetSimTickPos(tp) : forcedTarget.Thing.DrawPos)
                    : forcedTarget.Cell.ToVector3Shifted();
                lookTargetXZ = new Vector2(fp.x, fp.z);
                if (rigComp?.Pawn != null)
                {
                    Vector3 sp = CompGiantRig.GetSimTickPos(rigComp.Pawn);
                    if ((lookTargetXZ - new Vector2(sp.x, sp.z)).sqrMagnitude > 0.04f)
                    {
                        optimalHullHeadingDeg = Mathf.Atan2(lookTargetXZ.x - sp.x, lookTargetXZ.y - sp.z) * Mathf.Rad2Deg;
                    }
                }
                shouldRotateHullToTarget = forcedTarget.HasThing
                    ? ShouldRotateHullTowardTarget(forcedTarget.Thing)
                    : ShouldRotateHullTowardTarget(null);
                return true;
            }

            // Pass 0: Active pursued / hostile target firepower alignment — breaks the out-of-sector CooldownIdle deadlock
            // so stationary giants ALWAYS pivot smoothly to bring maximum intact weapons simultaneously into sector!
            Pawn ownerPawn = rigComp?.Pawn;
            if (ownerPawn != null && ownerPawn.Spawned && ownerPawn.Map != null && !(ownerPawn.Faction != null && ownerPawn.Faction.IsPlayer && ownerPawn.Drafted && !fireAtWill))
            {
                bool huntingPrey = TryGetActivePredatorHuntPrey(ownerPawn, out Pawn activePrey);
                Thing pursued = rigComp.ResolvePursuedHostileTarget() ?? ownerPawn.mindState?.enemyTarget;
                if (pursued == null || !IsValidHostileThing(ownerPawn, pursued) || (pursued is Pawn pp && pp.Downed))
                {
                    pursued = huntingPrey ? activePrey : null;
                    if (pursued == null && fireAtWill && cachedHostileCandidates.Count > 0)
                    {
                        float bestSq = float.MaxValue;
                        for (int i = 0; i < cachedHostileCandidates.Count; i++)
                        {
                            Thing cand = cachedHostileCandidates[i];
                            if (cand == null || !IsValidHostileThing(ownerPawn, cand) || (cand is Pawn cp && cp.Downed))
                            {
                                continue;
                            }
                            float dSq = ownerPawn.Position.DistanceToSquared(cand.Position);
                            if (dSq < bestSq)
                            {
                                bestSq = dSq;
                                pursued = cand;
                            }
                        }
                    }
                }

                if (pursued != null &&
                    (!IsTargetCurrentlyEngageableByIndependentOverlay(pursued)
                     || IsTargetCurrentlyEngageableByPrimaryParts(pursued, checkSector: true)
                     || !HasAnyOtherHostileEngageableByPrimaryParts(pursued)) &&
                    EvaluateFirepowerAlignment(pursued, out float pursuedOptHdg, out Vector2 pursuedBodyXZ, out _, out _, out _))
                {
                    lookTargetXZ = pursuedBodyXZ;
                    optimalHullHeadingDeg = pursuedOptHdg;
                    shouldRotateHullToTarget = ShouldRotateHullTowardTarget(pursued);
                    return true;
                }
            }

            // Pass 1: Prioritize active LookChain-driven weapon groups (e.g. KwK 40 main turret) over secondary hull guns
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!grp.IsLookChainDriven || (rigComp != null && !rigComp.IsBoneIntact(grp.Bone.nodeId)))
                {
                    continue;
                }
                if (grp.HasTargetWorldXZ &&
                    (grp.Phase == PartWeaponPhase.WarmingUp || grp.Phase == PartWeaponPhase.FiringBurst || grp.Phase == PartWeaponPhase.FollowThrough))
                {
                    lookTargetXZ = grp.TargetWorldXZ;
                    if (ownerPawn != null)
                    {
                        Vector3 sp = CompGiantRig.GetSimTickPos(ownerPawn);
                        optimalHullHeadingDeg = Mathf.Atan2(lookTargetXZ.x - sp.x, lookTargetXZ.y - sp.z) * Mathf.Rad2Deg;
                    }
                    shouldRotateHullToTarget = !grp.IsTurret;
                    return true;
                }
            }

            // Pass 1b: Keep LookChain-driven turrets smoothly aligned with their tracked target during cooldown/reload
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!grp.IsLookChainDriven || (rigComp != null && !rigComp.IsBoneIntact(grp.Bone.nodeId)))
                {
                    continue;
                }
                if (grp.Phase == PartWeaponPhase.CooldownIdle && grp.HasLastTrackedTargetXZ)
                {
                    if (grp.LastTrackedTarget.IsValid && !IsTargetStillAliveAndInMap(grp.LastTrackedTarget, rigComp?.Pawn?.Map))
                    {
                        grp.HasLastTrackedTargetXZ = false;
                        grp.LastTrackedTarget = LocalTargetInfo.Invalid;
                        continue;
                    }
                    lookTargetXZ = grp.LastTrackedTargetXZ;
                    if (ownerPawn != null)
                    {
                        Vector3 sp = CompGiantRig.GetSimTickPos(ownerPawn);
                        optimalHullHeadingDeg = Mathf.Atan2(lookTargetXZ.x - sp.x, lookTargetXZ.y - sp.z) * Mathf.Rad2Deg;
                    }
                    shouldRotateHullToTarget = !grp.IsTurret;
                    return true;
                }
            }

            // Pass 2: Fallback to non-LookChain weapon groups when no primary turret/head weapon group is active
            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (grp.IsLookChainDriven || IsGroupIndependentOverlayOnly(grp) || (rigComp != null && !rigComp.IsBoneIntact(grp.Bone.nodeId)))
                {
                    continue;
                }
                if (grp.ActiveAttack != null && grp.ActiveAttack.independentOverlay)
                {
                    continue;
                }
                if (grp.HasTargetWorldXZ &&
                    (grp.Phase == PartWeaponPhase.WarmingUp || grp.Phase == PartWeaponPhase.FiringBurst || grp.Phase == PartWeaponPhase.FollowThrough))
                {
                    lookTargetXZ = grp.TargetWorldXZ;
                    bool isRear = Mathf.Abs(Mathf.DeltaAngle(0f, grp.ActiveAttack?.sectorCenterDeg ?? 0f)) > 115f;
                    bool lockVehHull = rigComp?.VehicleLocomotion is VehicleLocomotionRuntime vLoco
                        && vLoco.Def.lockHullHeadingWhenParked
                        && (!vLoco.Def.allowHullTraverseForFixedWeapons || rigComp.HasAnyOperationalTurretBone());
                    if (ownerPawn != null)
                    {
                        Vector3 sp = CompGiantRig.GetSimTickPos(ownerPawn);
                        optimalHullHeadingDeg = Mathf.Atan2(lookTargetXZ.x - sp.x, lookTargetXZ.y - sp.z) * Mathf.Rad2Deg;
                    }
                    shouldRotateHullToTarget = !isRear && !grp.IsTurret && !lockVehHull;
                    return true;
                }
            }

            return false;
        }
        public bool CanRangedEngageTargetFromCurrentPosition(LocalTargetInfo target, out string failStr)
        {
            failStr = "";
            Pawn pawn = rigComp?.Pawn;
            Map map = pawn?.Map;
            if (pawn == null || map == null || !pawn.Spawned || !target.IsValid)
            {
                failStr = "CannotHitTarget".Translate().CapitalizeFirst();
                return false;
            }

            Thing targetThing = target.Thing;
            if (targetThing != null && !GiantFlightUtility.CanGiantEngageTargetInCurrentDomain(pawn, rigComp, targetThing))
            {
                failStr = "CannotHitTarget".Translate().CapitalizeFirst();
                return false;
            }

            bool anyWithinMax = false;
            bool anyAboveMin = false;

            if (GiantFastLookup.TryGetCarrierHangar(pawn) is CompCarrierHangar hangar
                && hangar.MaxAllowedInterceptors > 0
                && hangar.IntactBayCount > 0)
            {
                float d = MeasureHorizontalDistanceToTargetOrHitbox(pawn.Position, target);
                if (d <= hangar.Props.engagementRange)
                {
                    return true;
                }
            }

            if (!rigComp.SolveLiveWorldPoses(livePoseScratch))
            {
                failStr = "CannotHitTarget".Translate().CapitalizeFirst();
                return false;
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(pawn);
            bool hullLocked = rigComp.VehicleLocomotion is VehicleLocomotionRuntime vLoco
                && vLoco.Def.lockHullHeadingWhenParked
                && !vLoco.Def.allowHullTraverseForFixedWeapons;
            bool isAirborne = rigComp.IsCurrentlyAirborne
                || (rigComp.FlightLocomotion != null && rigComp.FlightLocomotion.FlightState == GiantFlightState.TakingOff);
            bool blockedByThickRoof = rigComp.FlightLocomotion?.Def?.blockedByThickRoof
                ?? rigComp.RigDef?.ActiveFlightLocomotion?.blockedByThickRoof
                ?? true;

            Vector3 simPos3 = CompGiantRig.GetSimTickPos(pawn);
            Vector2 simPosXZ = new Vector2(simPos3.x, simPos3.z);
            Vector3 targWorld = targetThing != null
                ? (targetThing is Pawn tp ? CompGiantRig.GetSimTickPos(tp) : targetThing.DrawPos)
                : target.Cell.ToVector3Shifted();
            Vector2 targBodyXZ = new Vector2(targWorld.x, targWorld.z);
            Vector2 toTarget = targBodyXZ - simPosXZ;
            float bearingToTargetDeg = toTarget.sqrMagnitude > 0.01f
                ? Mathf.Atan2(toTarget.x, toTarget.y) * Mathf.Rad2Deg
                : rigComp.CurrentHeadingDeg;
            float curHeading = rigComp.CurrentHeadingDeg;

            for (int g = 0; g < groups.Length; g++)
            {
                BonePartWeaponGroup grp = groups[g];
                if (!rigComp.IsBoneIntact(grp.Bone.nodeId))
                {
                    continue;
                }
                for (int a = 0; a < grp.Attacks.Length; a++)
                {
                    GiantPartAttackDef atk = grp.Attacks[a];
                    if (atk.kind == GiantPartAttackKind.Melee)
                    {
                        continue;
                    }
                    if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                    {
                        continue;
                    }

                    Vector2 curOriginXZ = GetBoneOriginWorldXZ(grp, atk, out float refAngleDeg);
                    if (hullLocked)
                    {
                        if (grp.IsTurret && grp.MaxTurretAimAngleDeg < 175f)
                        {
                            Vector2 mountXZ = (uint)grp.BoneIndex < (uint)livePoseScratch.Length && livePoseScratch[grp.BoneIndex].visible
                                ? livePoseScratch[grp.BoneIndex].jointWorldXZ
                                : curOriginXZ;
                            Vector2 md = targBodyXZ - mountXZ;
                            if (md.sqrMagnitude > 0.04f)
                            {
                                float mBearing = Mathf.Atan2(md.x, md.y) * Mathf.Rad2Deg;
                                float mCenter = SkeletalKinematics.NormalizeAngle360(curHeading + grp.Bone.baseAngle);
                                if (Mathf.Abs(Mathf.DeltaAngle(mCenter, mBearing)) > grp.MaxTurretAimAngleDeg)
                                {
                                    continue;
                                }
                            }
                        }
                        else if (!grp.IsTurret && atk.sectorHalfAngleDeg < 179.9f)
                        {
                            float sectorCenter = SkeletalKinematics.NormalizeAngle360(refAngleDeg + atk.sectorCenterDeg);
                            if (Mathf.Abs(Mathf.DeltaAngle(sectorCenter, bearingToTargetDeg)) > atk.sectorHalfAngleDeg)
                            {
                                continue;
                            }
                        }
                    }

                    float dist;
                    float effMinDist;
                    if (targetThing != null)
                    {
                        float candHeading = hullLocked
                            ? curHeading
                            : SkeletalKinematics.NormalizeAngle360(bearingToTargetDeg - (grp.IsTurret ? grp.Bone.baseAngle : atk.sectorCenterDeg));
                        dist = ComputeSampledRangedMuzzleDistance(grp, atk, simPosXZ, curHeading, candHeading, targBodyXZ, targetThing);
                        effMinDist = ComputeEffectiveMinRangeDistanceForTarget(curOriginXZ, atk, grp, targetThing, dist);
                    }
                    else
                    {
                        dist = Vector2.Distance(curOriginXZ, targBodyXZ);
                        effMinDist = dist;
                    }

                    if (dist > atk.range + 0.5f)
                    {
                        continue;
                    }
                    anyWithinMax = true;

                    if (atk.minRange > 0.01f && effMinDist < atk.minRange)
                    {
                        continue;
                    }
                    anyAboveMin = true;

                    bool flyOverhead = atk.projectileDef?.projectile?.flyOverhead == true;
                    if (flyOverhead)
                    {
                        if (!GiantDestructionUtility.HasThickRoofAt(map, pawn.Position) &&
                            !GiantDestructionUtility.HasThickRoofAt(map, target.Cell))
                        {
                            return true;
                        }
                        continue;
                    }

                    if (isAirborne)
                    {
                        IntVec3 aimCell = targetThing is Pawn airGiant && GiantFastLookup.TryGetRig(airGiant) is CompGiantRig airRig && airRig.Footprint.hitCells.Count > 0
                            ? GiantTargetAimTracker.ResolvePreferredHitCell(airGiant, airRig, pawn.Position, pawn, null, ignoreRange: true)
                            : target.Cell;
                        if (HarmonyPatches_CombatAndHitbox.HasClearAerialSightline(map, pawn.Position, aimCell, blockedByThickRoof))
                        {
                            return true;
                        }
                    }
                    else if (targetThing != null)
                    {
                        IntVec3 muzzleCell = new Vector3(curOriginXZ.x, 0f, curOriginXZ.y).ToIntVec3();
                        if (GiantPartCombatUtility.HasLineOfSightToThingOrHitbox(pawn.Position, targetThing, map) ||
                            (muzzleCell.InBounds(map) && GiantPartCombatUtility.HasLineOfSightToThingOrHitbox(muzzleCell, targetThing, map)))
                        {
                            return true;
                        }
                    }
                    else if (GenSight.LineOfSight(pawn.Position, target.Cell, map, skipFirstCell: true))
                    {
                        return true;
                    }
                }
            }

            if (!anyWithinMax)
            {
                failStr = "OutOfRange".Translate().CapitalizeFirst();
            }
            else if (!anyAboveMin)
            {
                failStr = "TooClose".Translate().CapitalizeFirst();
            }
            else
            {
                failStr = "CannotHitTarget".Translate().CapitalizeFirst();
            }
            return false;
        }

        private static float MeasureHorizontalDistanceToTargetOrHitbox(IntVec3 fromCell, LocalTargetInfo target)
        {
            if (target.HasThing && target.Thing is Pawn giant &&
                GiantFastLookup.TryGetRig(giant) is CompGiantRig tRig &&
                tRig.Footprint.hitCells.Count > 0)
            {
                int bestSq = int.MaxValue;
                List<HitCellEntry> cells = tRig.Footprint.hitCells;
                for (int i = 0; i < cells.Count; i++)
                {
                    int dSq = fromCell.DistanceToSquared(cells[i].cell);
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                    }
                }
                return Mathf.Sqrt(bestSq);
            }
            return fromCell.DistanceTo(target.Cell);
        }

        public void DrawRangedAttackRadiusRings(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map == null)
            {
                return;
            }
            GetTacticalRangeCapabilities(out bool hasRanged, out _, out float maxRanged, out _, out _);
            if (!hasRanged || maxRanged <= 1f)
            {
                return;
            }
            Verb_CastGiantAbility.DrawLargeRadiusRing(pawn.Position, pawn.Map, maxRanged, new Color(1f, 1f, 1f, 0.7f));
            float minR = float.MaxValue;
            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(pawn);
            for (int g = 0; g < groups.Length; g++)
            {
                if (!rigComp.IsBoneIntact(groups[g].Bone.nodeId))
                {
                    continue;
                }
                GiantPartAttackDef[] atks = groups[g].Attacks;
                for (int a = 0; a < atks.Length; a++)
                {
                    GiantPartAttackDef atk = atks[a];
                    if (atk.kind == GiantPartAttackKind.Melee)
                    {
                        continue;
                    }
                    if (cockpit != null && atk.requiresPrimaryWeaponOperator && !cockpit.CanFirePrimaryWeapons)
                    {
                        continue;
                    }
                    if (atk.minRange < minR)
                    {
                        minR = atk.minRange;
                    }
                }
            }
            if (minR > 0.5f && minR < maxRanged)
            {
                Verb_CastGiantAbility.DrawLargeRadiusRing(pawn.Position, pawn.Map, minR, new Color(1f, 0.55f, 0.35f, 0.55f));
            }
        }

        public IEnumerable<Gizmo> GetPartCombatGizmos()
        {
            // Legacy custom Part Focus / Clear Focus / Part Fire at Will gizmos have been retired in favor
            // of native vanilla B (Attack), H (Melee attack), and U (Fire at will) commands emitted in Pawn.GetGizmos.
            yield break;
        }

        public void DrawSelectionOverlays()
        {
            Pawn pawn = rigComp?.Pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead)
            {
                return;
            }
            if (HasValidForcedTarget())
            {
                Vector3 a = CompGiantRig.GetSafeDrawPos(pawn);
                Vector3 b;
                if (forcedTarget.HasThing && forcedTarget.Thing is Pawn targetGiant &&
                    GiantFastLookup.TryGetRig(targetGiant) is CompGiantRig tRig &&
                    tRig.Footprint.hitCells.Count > 0)
                {
                    IntVec3 prefCell = GiantTargetAimTracker.ResolvePreferredHitCell(targetGiant, tRig, pawn.Position, pawn);
                    b = prefCell.IsValid ? prefCell.ToVector3Shifted() : targetGiant.DrawPos;
                }
                else
                {
                    b = forcedTarget.HasThing ? forcedTarget.Thing.DrawPos : forcedTarget.Cell.ToVector3Shifted();
                }
                GenDraw.DrawLineBetween(a, b);
            }
        }
    }

    /// <summary>
    /// Static helpers for pure-locomotion part-combat positioning AI, safe target acquisition
    /// (without touching vanilla CurrentEffectiveVerb), and player drafted focus-fire orders.
    /// </summary>
    public static class GiantPartCombatUtility
    {
        private static readonly List<IAttackTarget> searcherScanScratch = new List<IAttackTarget>(128);

        public static void ClearStaticScratch()
        {
            searcherScanScratch.Clear();
        }

        public static IAttackTarget FindBestAttackTargetForSearcher(
            Pawn giant,
            TargetScanFlags flags,
            Predicate<Thing> validator,
            float minDist,
            float maxDist,
            IntVec3 locus,
            float maxTravelRadiusFromLocus)
        {
            if (giant == null || !giant.Spawned || giant.Map == null)
            {
                return null;
            }

            GiantPartCombatRuntime.CollectPotentialAttackTargets(giant, searcherScanScratch);
            try
            {
                if (searcherScanScratch.Count == 0)
                {
                    return null;
                }

                IntVec3 selfPos = giant.Position;
                float minSq = minDist * minDist;
                float maxSq = maxDist * maxDist;
                bool useLocus = locus.IsValid && maxTravelRadiusFromLocus < 9000f;
                float maxLocusSq = useLocus ? maxTravelRadiusFromLocus * maxTravelRadiusFromLocus : float.MaxValue;
                bool needThreat = (flags & TargetScanFlags.NeedThreat) != 0 || (flags & TargetScanFlags.NeedActiveThreat) != 0;

                IAttackTarget best = null;
                float bestScore = float.MinValue;
                CompGiantRig searcherRig = GiantFastLookup.TryGetRig(giant);
                Vector3 searcherSimPos = searcherRig != null ? CompGiantRig.GetSimTickPos(giant) : selfPos.ToVector3Shifted();

                for (int i = 0; i < searcherScanScratch.Count; i++)
                {
                    IAttackTarget cand = searcherScanScratch[i];
                    if (cand?.Thing == null || cand.Thing == giant || cand.Thing.Destroyed || !cand.Thing.Spawned)
                    {
                        continue;
                    }
                    if (needThreat && cand.ThreatDisabled(giant))
                    {
                        continue;
                    }
                    Thing t = cand.Thing;
                    if (!GiantPartCombatRuntime.IsValidHostileThing(giant, t))
                    {
                        continue;
                    }
                    if (!GiantFlightUtility.CanGiantEngageTargetInCurrentDomain(giant, searcherRig, t))
                    {
                        continue;
                    }
                    if (validator != null && !validator(t))
                    {
                        continue;
                    }

                    float dSq = selfPos.DistanceToSquared(t.Position);
                    if (dSq < minSq || dSq > maxSq)
                    {
                        continue;
                    }
                    if (useLocus && locus.DistanceToSquared(t.Position) > maxLocusSq)
                    {
                        continue;
                    }
                    if ((flags & TargetScanFlags.NeedReachable) != 0 &&
                        !giant.CanReach(t, PathEndMode.Touch, Danger.Deadly))
                    {
                        continue;
                    }

                    float dist = Mathf.Sqrt(dSq);
                    float score = 200f - dist;
                    if (searcherRig != null)
                    {
                        Vector3 tpPos = t is Pawn pSim ? CompGiantRig.GetSimTickPos(pSim) : t.DrawPos;
                        float dx = tpPos.x - searcherSimPos.x;
                        float dz = tpPos.z - searcherSimPos.z;
                        if (dx * dx + dz * dz > 0.09f)
                        {
                            float bearing = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
                            float offBow = Mathf.Abs(Mathf.DeltaAngle(searcherRig.CurrentHeadingDeg, bearing));
                            if (offBow <= 95f)
                            {
                                score += 24f;
                            }
                        }
                    }
                    if (t is Pawn tp)
                    {
                        if (tp.Downed)
                        {
                            score -= 70f;
                        }
                        if (GiantFastLookup.TryGetRig(tp) != null)
                        {
                            score += 25f;
                        }
                    }
                    else
                    {
                        score -= 20f;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = cand;
                    }
                }

                return best;
            }
            finally
            {
                searcherScanScratch.Clear();
            }
        }

        public static void OrderGiantRangedAttack(Pawn giant, CompGiantRig rig, LocalTargetInfo target)
        {
            if (giant == null || rig?.PartCombat == null || !target.IsValid)
            {
                return;
            }

            if (target.Thing is Pawn targetGiant &&
                GiantFastLookup.TryGetRig(targetGiant) != null &&
                GiantTargetAimTracker.TryGetActiveClick(out IntVec3 clickCell, out Vector3 clickWorldPos))
            {
                GiantTargetAimTracker.RecordHitCellClick(giant, targetGiant, clickCell, clickWorldPos);
            }

            rig.PartCombat.SetForcedTarget(target, GiantForcedAttackMode.Ranged);
            giant.pather?.StopDead();
            if (rig.PartCombat.ShouldRotateHullTowardTarget(target.Thing))
            {
                SetFacingToward(giant, target.Cell);
            }

            Job hold = giant.Drafted
                ? GiantPlayerControlUtility.MakeDraftedIdleWaitJob(giant, expiryInterval: 24, checkOverrideOnExpire: true)
                : JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 24);
            hold.expiryInterval = 24;
            hold.checkOverrideOnExpire = true;
            giant.jobs?.TryTakeOrderedJob(hold, JobTag.Misc);
        }

        public static void OrderGiantMeleeAttack(Pawn giant, CompGiantRig rig, LocalTargetInfo target)
        {
            OrderGiantFocusAttack(giant, rig, target, allowApproachIfOutOfRange: true, GiantForcedAttackMode.Melee);
        }

        public static void OrderGiantFocusAttack(
            Pawn giant,
            CompGiantRig rig,
            LocalTargetInfo target,
            bool allowApproachIfOutOfRange,
            GiantForcedAttackMode mode = GiantForcedAttackMode.Auto)
        {
            if (giant == null || rig?.PartCombat == null || !target.IsValid)
            {
                return;
            }

            if (mode == GiantForcedAttackMode.Ranged)
            {
                OrderGiantRangedAttack(giant, rig, target);
                return;
            }

            if (target.Thing is Pawn targetGiant &&
                GiantFastLookup.TryGetRig(targetGiant) != null &&
                GiantTargetAimTracker.TryGetActiveClick(out IntVec3 clickCell, out Vector3 clickWorldPos))
            {
                GiantTargetAimTracker.RecordHitCellClick(giant, targetGiant, clickCell, clickWorldPos);
            }

            rig.PartCombat.SetForcedTarget(target, mode);

            if (!allowApproachIfOutOfRange || !target.HasThing)
            {
                return;
            }

            if (rig.PartCombat.IsTargetOptimallyEngagedByPrimaryParts(target.Thing))
            {
                if (rig.PartCombat.ShouldRotateHullTowardTarget(target.Thing))
                {
                    SetFacingToward(giant, target.Cell);
                }
                if (giant.pather != null && !giant.pather.Moving)
                {
                    Job hold = giant.Drafted
                        ? GiantPlayerControlUtility.MakeDraftedIdleWaitJob(giant, expiryInterval: 24, checkOverrideOnExpire: true)
                        : JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
                    hold.expiryInterval = 24;
                    hold.checkOverrideOnExpire = true;
                    giant.jobs?.TryTakeOrderedJob(hold, JobTag.Misc);
                }
                return;
            }

            Job moveJob = TryBuildOptimalPositioningGotoJob(giant, rig, target.Thing, IntVec3.Invalid, float.MaxValue);
            if (moveJob != null)
            {
                moveJob.playerForced = true;
                giant.jobs?.TryTakeOrderedJob(moveJob, JobTag.Misc);
            }
        }

        public static Job TryGivePartCombatPositioningJob(
            Pawn pawn,
            float maxAcquireRadius = 60f,
            float maxKeepRadius = 72f,
            IntVec3 defendLocus = default,
            float defendRadius = float.MaxValue)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map == null || pawn.Dead || pawn.Downed)
            {
                return null;
            }

            if (GiantMotionOverrideRegistry.HasActiveWeaponSuppressingSequence(pawn))
            {
                Job holdDisp = JobMaker.MakeJob(JobDefOf.Wait_Combat, 12);
                holdDisp.checkOverrideOnExpire = true;
                return holdDisp;
            }

            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            if (rig?.PartCombat == null)
            {
                return null;
            }

            if (rig.FlightLocomotion != null && rig.FlightLocomotion.FlightState == GiantFlightState.Landing)
            {
                IntVec3 landCell = rig.FlightLocomotion.PendingLandingCell;
                if (landCell.IsValid && landCell.InBounds(pawn.Map) && pawn.Position != landCell)
                {
                    if (pawn.CurJobDef == JobDefOf.Goto && pawn.CurJob.targetA.Cell == landCell)
                    {
                        return null;
                    }
                    Job landGoto = JobMaker.MakeJob(JobDefOf.Goto, landCell);
                    landGoto.locomotionUrgency = LocomotionUrgency.Jog;
                    landGoto.expiryInterval = 30;
                    landGoto.checkOverrideOnExpire = true;
                    landGoto.collideWithPawns = false;
                    return landGoto;
                }
                Job landWait = JobMaker.MakeJob(JobDefOf.Wait_Combat, 14);
                landWait.checkOverrideOnExpire = true;
                return landWait;
            }

            if (CompAbilityEffect_AbyssalQuake.HasPendingCrossingStrike(pawn) || rig.IsSoftWhipStrikeActive)
            {
                Job holdWhip = JobMaker.MakeJob(JobDefOf.Wait_Combat, 10);
                holdWhip.checkOverrideOnExpire = true;
                return holdWhip;
            }

            if (rig.PartCombat.HasActiveFullHullSpinClip())
            {
                if (pawn.CurJobDef == JobDefOf.Wait_MaintainPosture || pawn.CurJobDef == JobDefOf.Wait_Combat)
                {
                    return null;
                }
                Job holdSpin = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, 16);
                holdSpin.checkOverrideOnExpire = true;
                return holdSpin;
            }

            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(pawn);
            if (cockpit != null && (cockpit.ShouldNullifyThinkTree || cockpit.ShouldHoldPositionWhenPilotedUndrafted))
            {
                return null;
            }
            if (!rig.PartCombat.GetTacticalRangeCapabilities(
                    out bool hasRanged,
                    out float minStandoff,
                    out float maxRanged,
                    out bool hasMelee,
                    out float maxMelee))
            {
                return null;
            }

            float maxOffensiveAbilityRange = JobGiver_GiantAbilityFight.GetMaxOffensiveAbilityRange(pawn);
            if (defendRadius >= 9000f && maxOffensiveAbilityRange + 4f > maxAcquireRadius)
            {
                maxAcquireRadius = maxOffensiveAbilityRange + 4f;
                maxKeepRadius = Mathf.Max(maxKeepRadius, maxAcquireRadius + 12f);
            }

            // First allow ready signature Abilities to fire if applicable (never auto-cast for drafted player giants)
            bool isDraftedPlayerGiant = pawn.Faction == Faction.OfPlayerSilentFail && pawn.Drafted;
            if (!isDraftedPlayerGiant && pawn.abilities?.abilities != null && pawn.abilities.abilities.Count > 0)
            {
                Thing preTarget = ResolveOrUpdateEnemyTarget(pawn, rig, maxAcquireRadius, maxKeepRadius, defendLocus, defendRadius);
                if (preTarget != null)
                {
                    LocalTargetInfo lti = new LocalTargetInfo(preTarget);
                    float d = pawn.Position.DistanceTo(preTarget.Position);
                    for (int i = 0; i < pawn.abilities.abilities.Count; i++)
                    {
                        Ability ab = pawn.abilities.abilities[i];
                        if (ab != null && ab.def != null && ab.def.aiCanUse && (ab.def.hostile || ab.def.ai_IsOffensive))
                        {
                            Job abJob = JobGiver_GiantAbilityFight.TryBuildCastJob(pawn, ab, lti, d);
                            if (abJob != null)
                            {
                                return abJob;
                            }
                        }
                    }
                }
            }

            Thing enemy = ResolveOrUpdateEnemyTarget(pawn, rig, maxAcquireRadius, maxKeepRadius, defendLocus, defendRadius);
            if (enemy == null)
            {
                return null;
            }

            // Hold current forward facing for a rear independentOverlay target when ANOTHER hostile is already engaged in front,
            // OR when the rear tail/overlay has already committed to a short warmup swing (~0.4s) so it finishes landing its blow before turning!
            if (!rig.PartCombat.HasValidForcedTarget()
                && rig.PartCombat.IsTargetCurrentlyEngageableByIndependentOverlay(enemy)
                && !rig.PartCombat.IsTargetCurrentlyEngageableByPrimaryParts(enemy, checkSector: true)
                && (rig.PartCombat.HasAnyOtherHostileEngageableByPrimaryParts(enemy)
                    || rig.PartCombat.IsAnyIndependentOverlayWarmingUpOnTarget(enemy)))
            {
                Job tailHoldJob = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
                tailHoldJob.expiryInterval = 14;
                tailHoldJob.checkOverrideOnExpire = true;
                return tailHoldJob;
            }

            if (rig.PartCombat.IsTargetOptimallyEngagedByPrimaryParts(enemy))
            {
                if (GiantFastLookup.TryGetCarrierHangar(pawn) is CompCarrierHangar orbitHangar
                    && orbitHangar.MaxAllowedInterceptors > 0
                    && rig.IsCurrentlyAirborne
                    && rig.FlightVehicleLocomotion != null)
                {
                    Job orbitJob = TryBuildCarrierCombatOrbitGotoJob(
                        pawn,
                        rig,
                        enemy,
                        orbitHangar.Props.engagementRange,
                        defendLocus,
                        defendRadius);
                    if (orbitJob != null)
                    {
                        return orbitJob;
                    }
                }
                if (rig.PartCombat.ShouldRotateHullTowardTarget(enemy))
                {
                    SetFacingToward(pawn, enemy.Position);
                }
                Job holdJob = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
                holdJob.expiryInterval = hasRanged ? 24 : 16;
                holdJob.checkOverrideOnExpire = true;
                return holdJob;
            }

            Job gotoJob = TryBuildOptimalPositioningGotoJob(pawn, rig, enemy, defendLocus, defendRadius);
            if (gotoJob != null)
            {
                // Belt-and-suspenders guard against synchronous 0-tick Goto arrival loops (e.g. external PathEndMode hooks).
                if (gotoJob.def == JobDefOf.Goto
                    && !gotoJob.targetA.HasThing
                    && gotoJob.targetA.Cell.IsValid
                    && (gotoJob.targetA.Cell == pawn.Position || pawn.CanReachImmediate(gotoJob.targetA, PathEndMode.OnCell)))
                {
                    gotoJob = null;
                }
            }
            if (gotoJob != null)
            {
                return gotoJob;
            }

            if (rig.PartCombat.ShouldRotateHullTowardTarget(enemy))
            {
                SetFacingToward(pawn, enemy.Position);
            }
            Job fallbackHold = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
            fallbackHold.expiryInterval = 18;
            fallbackHold.checkOverrideOnExpire = true;
            return fallbackHold;
        }

        private static Thing ResolveOrUpdateEnemyTarget(
            Pawn pawn,
            CompGiantRig rig,
            float maxAcquireRadius,
            float maxKeepRadius,
            IntVec3 defendLocus,
            float defendRadius)
        {
            if (rig.PartCombat.HasValidForcedTarget() && rig.PartCombat.ForcedTarget.HasThing)
            {
                Thing ft = rig.PartCombat.ForcedTarget.Thing;
                if (pawn.mindState != null)
                {
                    pawn.mindState.enemyTarget = ft;
                }
                return ft;
            }

            Thing cur = pawn.mindState?.enemyTarget;
            if (cur != null)
            {
                bool bad = cur.Destroyed
                    || !cur.Spawned
                    || cur.Map != pawn.Map
                    || (cur is Pawn cp && (cp.Dead || cp.Downed))
                    || !GiantPartCombatRuntime.IsValidHostileThing(pawn, cur)
                    || !GiantFlightUtility.CanGiantEngageTargetInCurrentDomain(pawn, rig, cur)
                    || pawn.Position.DistanceToSquared(cur.Position) > maxKeepRadius * maxKeepRadius
                    || (defendLocus.IsValid && defendRadius < 9000f && defendLocus.DistanceToSquared(cur.Position) > (defendRadius + 28f) * (defendRadius + 28f));
                if (bad)
                {
                    cur = null;
                    if (pawn.mindState != null)
                    {
                        pawn.mindState.enemyTarget = null;
                    }
                }
            }

            if (cur != null
                && !rig.PartCombat.IsTargetOptimallyEngagedByPrimaryParts(cur)
                && rig.PartCombat.TryFindImmediateEngageablePrimaryHostile(cur, out Thing immediateHostile)
                && (!defendLocus.IsValid || defendRadius >= 9000f || defendLocus.DistanceToSquared(immediateHostile.Position) <= (defendRadius + 28f) * (defendRadius + 28f)))
            {
                cur = immediateHostile;
            }

            if (cur == null)
            {
                Thing provoked = rig.TryGetActiveProvokedTarget();
                if (provoked != null
                    && !(provoked is Pawn pp && (pp.Dead || pp.Downed))
                    && GiantPartCombatRuntime.IsValidHostileThing(pawn, provoked)
                    && GiantFlightUtility.CanGiantEngageTargetInCurrentDomain(pawn, rig, provoked)
                    && pawn.Position.DistanceToSquared(provoked.Position) <= maxKeepRadius * maxKeepRadius)
                {
                    cur = provoked;
                }
            }

            if (cur == null)
            {
                IAttackTarget found = FindBestAttackTargetForSearcher(
                    pawn,
                    TargetScanFlags.NeedThreat,
                    validator: null,
                    minDist: 0f,
                    maxDist: maxAcquireRadius,
                    locus: defendLocus,
                    maxTravelRadiusFromLocus: defendRadius < 9000f ? defendRadius + 25f : float.MaxValue);
                cur = found?.Thing;
            }

            if (pawn.mindState != null)
            {
                pawn.mindState.enemyTarget = cur;
            }

            return cur;
        }

        private static Job TryBuildCarrierCombatOrbitGotoJob(
            Pawn pawn,
            CompGiantRig rig,
            Thing target,
            float engagementRange,
            IntVec3 defendLocus,
            float defendRadius)
        {
            Map map = pawn?.Map;
            if (map == null || target == null || !target.Spawned)
            {
                return null;
            }

            Vector3 targetPos = CompGiantRig.GetSafeDrawPos(target as Pawn) != Vector3.zero && target is Pawn
                ? CompGiantRig.GetSafeDrawPos(target as Pawn)
                : target.Position.ToVector3Shifted();
            Vector3 selfPos = CompGiantRig.GetSafeDrawPos(pawn);
            Vector3 radial = selfPos - targetPos;
            radial.y = 0f;
            if (radial.sqrMagnitude < 0.25f)
            {
                radial = new Vector3(1f, 0f, 0f);
            }
            radial.Normalize();

            float orbitRadius = Mathf.Clamp(engagementRange * 0.56f, 16f, 25f);
            float orbitSign = (pawn.thingIDNumber & 1) == 0 ? 1f : -1f;
            int edgeMargin = 10;

            for (int k = 0; k < 5; k++)
            {
                float stepDeg = (34f + k * 24f) * orbitSign;
                float rad = stepDeg * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad);
                float sin = Mathf.Sin(rad);
                Vector3 nextRadial = new Vector3(
                    radial.x * cos - radial.z * sin,
                    0f,
                    radial.x * sin + radial.z * cos);
                IntVec3 cand = (targetPos + nextRadial * orbitRadius).ToIntVec3();
                cand.x = Mathf.Clamp(cand.x, edgeMargin, Mathf.Max(edgeMargin, map.Size.x - 1 - edgeMargin));
                cand.z = Mathf.Clamp(cand.z, edgeMargin, Mathf.Max(edgeMargin, map.Size.z - 1 - edgeMargin));

                if (!cand.InBounds(map) || cand.DistanceToSquared(pawn.Position) < 25)
                {
                    continue;
                }
                if (defendLocus.IsValid && defendRadius < 9000f && defendLocus.DistanceToSquared(cand) > defendRadius * defendRadius)
                {
                    continue;
                }
                if (!pawn.CanReach(cand, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }

                Job orbitGoto = JobMaker.MakeJob(JobDefOf.Goto, cand);
                orbitGoto.locomotionUrgency = LocomotionUrgency.Jog;
                orbitGoto.expiryInterval = 70;
                orbitGoto.checkOverrideOnExpire = true;
                orbitGoto.collideWithPawns = false;
                return orbitGoto;
            }

            return null;
        }

        public static Job TryBuildOptimalPositioningGotoJob(
            Pawn pawn,
            CompGiantRig rig,
            Thing target,
            IntVec3 defendLocus,
            float defendRadius)
        {
            Map map = pawn?.Map;
            if (map == null || target == null || !target.Spawned)
            {
                return null;
            }

            rig.PartCombat.GetTacticalRangeCapabilities(
                out bool hasRanged,
                out float minStandoff,
                out float maxRanged,
                out bool hasMelee,
                out float maxMelee);

            bool hasCarrier = GiantFastLookup.TryGetCarrierHangar(pawn) is CompCarrierHangar ch && ch.MaxAllowedInterceptors > 0;

            // When target is airborne and this grounded giant cannot melee airborne targets, treat melee
            // as unavailable for positioning so hybrid giants (e.g. MechScorpion) never sprint underneath
            // a flying dragon trying to melee it, and instead back out of minRange to fire ranged weapons.
            bool meleeBlockedByAirborneTarget = hasMelee
                && GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(pawn, target);
            bool effectiveHasMelee = hasMelee && !meleeBlockedByAirborneTarget;
            bool prefersMeleeCloseAssault = effectiveHasMelee
                && rig.PartCombat.PrefersMeleeCloseAssaultAgainst(target);

            bool insideRangedMinStandoff = hasRanged && effectiveHasMelee && !prefersMeleeCloseAssault && minStandoff > 0.5f &&
                pawn.Position.DistanceTo(target.Position) < minStandoff + 4.5f;

            if (hasRanged && !prefersMeleeCloseAssault && !insideRangedMinStandoff)
            {
                float curCenterDist = pawn.Position.DistanceTo(target.Position);
                float desiredDist = Mathf.Clamp(maxRanged * 0.72f, Mathf.Max(3.5f, minStandoff), Mathf.Max(4.5f, maxRanged - 2.0f));
                bool isRetreatingOutward = !effectiveHasMelee && curCenterDist < desiredDist - 1.5f;
                bool canTacticalReverse = isRetreatingOutward
                    && rig.VehicleLocomotion != null
                    && rig.VehicleLocomotion.Def.allowReverse;
                float maxRevStep = canTacticalReverse
                    ? Mathf.Clamp(rig.VehicleLocomotion.Def.maxReverseDistanceCells - 1.5f, 4.5f, 7.2f)
                    : 0f;

                Vector3 toSelf = pawn.Position.ToVector3Shifted() - target.Position.ToVector3Shifted();
                toSelf.y = 0f;
                if (toSelf.sqrMagnitude < 0.1f)
                {
                    toSelf = new Vector3(0f, 0f, -1f);
                }
                toSelf.Normalize();

                Span<float> candidateRadii = stackalloc float[5];
                if (canTacticalReverse)
                {
                    float revTargetDist = Mathf.Clamp(curCenterDist + maxRevStep, Mathf.Max(5.5f, minStandoff + 2.0f), Mathf.Max(6.5f, maxRanged - 1.5f));
                    desiredDist = revTargetDist;
                    candidateRadii[0] = revTargetDist;
                    candidateRadii[1] = Mathf.Clamp(curCenterDist + maxRevStep * 0.72f, 5.0f, Mathf.Max(6.0f, maxRanged - 1.5f));
                    candidateRadii[2] = Mathf.Clamp(maxRanged * 0.58f, 8.5f, Mathf.Max(8.5f, maxRanged - 2.0f));
                    candidateRadii[3] = Mathf.Clamp(maxRanged * 0.44f, 7.5f, Mathf.Max(7.5f, maxRanged - 3.0f));
                    candidateRadii[4] = Mathf.Min(maxRanged - 1.0f, revTargetDist + 4.0f);
                }
                else
                {
                    candidateRadii[0] = desiredDist;
                    candidateRadii[1] = Mathf.Max(6.5f, desiredDist - 4.0f);
                    candidateRadii[2] = Mathf.Min(maxRanged - 1.0f, desiredDist + 3.5f);
                    candidateRadii[3] = Mathf.Clamp(maxRanged * 0.46f, 8.5f, Mathf.Max(8.5f, desiredDist - 6.0f));
                    candidateRadii[4] = Mathf.Clamp(maxRanged * 0.32f, 7.0f, Mathf.Max(7.0f, desiredDist - 9.0f));
                }

                IntVec3 bestCell = IntVec3.Invalid;
                float bestScore = float.MinValue;
                GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);

                for (int step = 0; step < 16; step++)
                {
                    float angDeg = step == 0
                        ? 0f
                        : (step == 15 ? 180f : ((step % 2 == 1 ? 1f : -1f) * ((step + 1) / 2) * 22.5f));
                    float angOff = angDeg * Mathf.Deg2Rad;
                    float cos = Mathf.Cos(angOff);
                    float sin = Mathf.Sin(angOff);
                    Vector3 rotDir = new Vector3(toSelf.x * cos - toSelf.z * sin, 0f, toSelf.x * sin + toSelf.z * cos);

                    for (int rTry = 0; rTry < candidateRadii.Length; rTry++)
                    {
                        float r = candidateRadii[rTry];
                        if (r < 3.5f || r > maxRanged - 0.8f)
                        {
                            continue;
                        }
                        if (isRetreatingOutward && r < curCenterDist + 2.5f)
                        {
                            continue;
                        }
                        IntVec3 cand = (target.Position.ToVector3Shifted() + rotDir * r).ToIntVec3();
                        if (GiantArenaService.IsOutsideActiveArenaInnerRect(map, cand, margin: 2))
                        {
                            continue;
                        }
                        bool candPassable = rig.IsCurrentlyAirborne
                            ? GiantFlightUtility.IsCellPassableForFlight(map, rig, cand)
                            : (cand.Walkable(map) || GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(map, pawn, cand));
                        if (!cand.InBounds(map) || !candPassable)
                        {
                            continue;
                        }
                        float candDist = cand.DistanceTo(target.Position);
                        if (isRetreatingOutward && candDist < curCenterDist + 2.2f)
                        {
                            continue;
                        }
                        if (defendLocus.IsValid && defendRadius < 9000f && defendLocus.DistanceToSquared(cand) > defendRadius * defendRadius)
                        {
                            continue;
                        }
                        if (!hasCarrier && !HasLineOfSightToThingOrHitbox(cand, target, map))
                        {
                            continue;
                        }
                        if (mapComp != null && mapComp.CountGiantHullOverlapAt(rig, cand, ignoreComp: GiantFastLookup.TryGetRig(target as Pawn)) > 0)
                        {
                            continue;
                        }
                        if (!pawn.CanReach(cand, PathEndMode.OnCell, Danger.Deadly))
                        {
                            continue;
                        }

                        float travel = pawn.Position.DistanceTo(cand);
                        float retreatDirBonus = isRetreatingOutward ? cos * 12.0f : 0f;
                        if (canTacticalReverse && travel >= 2.2f && travel <= rig.VehicleLocomotion.Def.maxReverseDistanceCells - 0.35f)
                        {
                            float moveBearingDeg = Mathf.Atan2(cand.x - pawn.Position.x, cand.z - pawn.Position.z) * Mathf.Rad2Deg;
                            float absFwdErr = Mathf.Abs(Mathf.DeltaAngle(rig.CurrentHeadingDeg, moveBearingDeg));
                            if (absFwdErr >= rig.VehicleLocomotion.Def.reverseMinHeadingErrorDeg)
                            {
                                retreatDirBonus += 38.0f;
                            }
                        }
                        float score = 200f + retreatDirBonus - travel * 0.9f - Mathf.Abs(candDist - desiredDist) * 2.2f;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCell = cand;
                        }
                    }
                }

                if (bestCell.IsValid && bestCell != pawn.Position)
                {
                    Job rangedGoto = JobMaker.MakeJob(JobDefOf.Goto, bestCell);
                    rangedGoto.expiryInterval = ComputePivotAwareGotoExpiryTicks(rig, pawn.Position, bestCell, 55);
                    rangedGoto.checkOverrideOnExpire = true;
                    rangedGoto.collideWithPawns = true;
                    return rangedGoto;
                }
            }

            if (effectiveHasMelee)
            {
                if (pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly))
                {
                    if (TryFindCloseMeleeEngagementCell(pawn, rig, target, maxMelee, defendLocus, defendRadius, out IntVec3 meleeCell))
                    {
                        if (meleeCell == pawn.Position)
                        {
                            return null;
                        }
                        Job cellGoto = JobMaker.MakeJob(JobDefOf.Goto, meleeCell);
                        cellGoto.locomotionUrgency = LocomotionUrgency.Sprint;
                        cellGoto.expiryInterval = ComputePivotAwareGotoExpiryTicks(rig, pawn.Position, meleeCell, 50);
                        cellGoto.checkOverrideOnExpire = true;
                        cellGoto.collideWithPawns = true;
                        return cellGoto;
                    }

                    // Fallback approach cell if tight hull perimeter around target is congested
                    GiantFootprintMapComponent meleeMapComp = GiantFootprintMapComponent.GetFast(map);
                    CompGiantRig fbTargetRig = GiantFastLookup.TryGetRig(target as Pawn);
                    bool fbAvoidBelly = GiantPartCombatRuntime.ShouldAvoidAirborneTargetBelly(rig, target, fbTargetRig);
                    if (pawn.Position.DistanceToSquared(target.Position) > 36 &&
                        CellFinder.TryFindRandomCellNear(
                            target.Position,
                            map,
                            9,
                            c => c.InBounds(map)
                                && (rig.IsCurrentlyAirborne
                                    ? GiantFlightUtility.IsCellPassableForFlight(map, rig, c)
                                    : GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(map, pawn, c))
                                && (meleeMapComp == null || rig.IsCurrentlyAirborne || !meleeMapComp.IsBlockedForPawn(c, pawn))
                                && (!fbAvoidBelly || !fbTargetRig.Footprint.hitCellSet.Contains(c))
                                && (!defendLocus.IsValid || defendRadius >= 9000f || defendLocus.DistanceToSquared(c) <= defendRadius * defendRadius)
                                && pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly),
                            out IntVec3 fallbackApproachCell)
                        && fallbackApproachCell.IsValid
                        && fallbackApproachCell != pawn.Position)
                    {
                        Job fallbackGoto = JobMaker.MakeJob(JobDefOf.Goto, fallbackApproachCell);
                        fallbackGoto.locomotionUrgency = LocomotionUrgency.Sprint;
                        fallbackGoto.expiryInterval = 35;
                        fallbackGoto.checkOverrideOnExpire = true;
                        fallbackGoto.collideWithPawns = true;
                        return fallbackGoto;
                    }
                }
            }

            return null;
        }

        public static bool HasLineOfSightToThingOrHitbox(IntVec3 fromCell, Thing target, Map map)
        {
            if (target == null || map == null)
            {
                return false;
            }
            if (GenSight.LineOfSight(fromCell, target.Position, map, skipFirstCell: true))
            {
                return true;
            }
            CompGiantRig targetRig = GiantFastLookup.TryGetRig(target as Pawn);
            if (targetRig != null && targetRig.Footprint.hitCellPositions.Count > 0)
            {
                List<IntVec3> hits = targetRig.Footprint.hitCellPositions;
                int stride = hits.Count > 24 ? hits.Count / 16 : 1;
                for (int i = 0; i < hits.Count; i += stride)
                {
                    if (GenSight.LineOfSight(fromCell, hits[i], map, skipFirstCell: true))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        [ThreadStatic]
        private static List<IntVec3>[] rotatedHullBucketsScratch;

        [ThreadStatic]
        private static HashSet<long> rotatedHullDedupScratch;

        public static IReadOnlyList<IntVec3> GetRotatedHullOffsetsBucket(IReadOnlyList<IntVec3> baseOffsets, int octantBucket)
        {
            if (baseOffsets == null || baseOffsets.Count == 0 || (octantBucket & 7) == 0)
            {
                return baseOffsets;
            }
            int b = octantBucket & 7;
            rotatedHullBucketsScratch ??= new List<IntVec3>[8];
            List<IntVec3> list = rotatedHullBucketsScratch[b];
            if (list != null)
            {
                return list;
            }
            list = new List<IntVec3>(baseOffsets.Count);
            rotatedHullBucketsScratch[b] = list;
            return list;
        }

        public static void PopulateRotatedHullBuckets(IReadOnlyList<IntVec3> baseOffsets)
        {
            if (baseOffsets == null || baseOffsets.Count == 0)
            {
                return;
            }
            rotatedHullBucketsScratch ??= new List<IntVec3>[8];
            rotatedHullDedupScratch ??= new HashSet<long>(64);
            for (int b = 1; b < 8; b++)
            {
                List<IntVec3> list = rotatedHullBucketsScratch[b] ??= new List<IntVec3>(baseOffsets.Count);
                list.Clear();
                rotatedHullDedupScratch.Clear();
                float rad = (b * 45f) * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad);
                float sin = Mathf.Sin(rad);
                for (int i = 0; i < baseOffsets.Count; i++)
                {
                    IntVec3 o = baseOffsets[i];
                    int rx = Mathf.RoundToInt(o.x * cos + o.z * sin);
                    int rz = Mathf.RoundToInt(-o.x * sin + o.z * cos);
                    long key = ((long)rx << 32) ^ (uint)rz;
                    if (rotatedHullDedupScratch.Add(key))
                    {
                        list.Add(new IntVec3(rx, 0, rz));
                    }
                }
            }
        }

        private static bool TryFindCloseMeleeEngagementCell(
            Pawn attacker,
            CompGiantRig attackerRig,
            Thing target,
            float maxMeleeReach,
            IntVec3 defendLocus,
            float defendRadius,
            out IntVec3 bestCell)
        {
            bestCell = IntVec3.Invalid;
            Map map = attacker.Map;
            CompGiantRig targetRig = GiantFastLookup.TryGetRig(target as Pawn);
            IReadOnlyList<IntVec3> targetSolidCells = targetRig != null
                && targetRig.Footprint.collisionCells != null
                && targetRig.Footprint.collisionCells.Count > 0
                ? targetRig.Footprint.collisionCells
                : null;

            CellRect targetRect;
            if (targetSolidCells != null)
            {
                int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
                for (int i = 0; i < targetSolidCells.Count; i++)
                {
                    IntVec3 sc = targetSolidCells[i];
                    if (sc.x < minX) minX = sc.x;
                    if (sc.x > maxX) maxX = sc.x;
                    if (sc.z < minZ) minZ = sc.z;
                    if (sc.z > maxZ) maxZ = sc.z;
                }
                targetRect = new CellRect(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
            }
            else if (targetRig != null && !targetRig.Footprint.tightHitBounds.IsEmpty)
            {
                targetRect = targetRig.Footprint.tightHitBounds;
            }
            else
            {
                targetRect = target.OccupiedRect();
            }

            bool lowSkimTarget = targetSolidCells == null
                && GiantPartCombatRuntime.ShouldAvoidAirborneTargetBelly(attackerRig, target, targetRig);
            bool useCanopyPerimeter = targetSolidCells == null
                && !lowSkimTarget
                && GiantPartCombatRuntime.ShouldUseVisualCanopyPerimeterForTarget(attackerRig, target, targetRig);
            float snugTouchRadius = lowSkimTarget ? 1.85f : 1.45f;
            float fallbackTouchRadius = Mathf.Max(2.45f, attackerRig.GetEffectiveMeleeTouchRadius() + 0.45f);
            int attackerExtent = Mathf.Max(
                1,
                useCanopyPerimeter
                    ? attackerRig.Footprint.maxPawnExclusionHorizontalExtent
                    : attackerRig.Footprint.maxCollisionHorizontalExtent);
            int expand = Mathf.Clamp(Mathf.CeilToInt(fallbackTouchRadius) + attackerExtent + 1, 3, 16);
            CellRect searchRect = targetRect.ExpandedBy(expand).ClipInsideMap(map);

            GiantFootprintMapComponent mapComp = GiantFootprintMapComponent.GetFast(map);
            IReadOnlyList<IntVec3> attackerOffsets = useCanopyPerimeter
                ? attackerRig.Footprint.localPawnExclusionOffsets
                : attackerRig.Footprint.localCollisionOffsets;
            PopulateRotatedHullBuckets(attackerOffsets);

            HashSet<IntVec3> targetSolidSet = targetSolidCells != null
                ? targetRig.Footprint.collisionCellSet
                : targetRig?.Footprint.hitCellSet;
            IReadOnlyList<HitCellEntry> fallbackHits = targetSolidCells == null
                && targetRig != null
                && targetRig.Footprint.hitCells.Count > 0
                ? targetRig.Footprint.hitCells
                : null;

            MeleeStrikeImpactProfile primaryProf = default;
            bool hasStrikeProfile = attackerRig.PartCombat != null
                && attackerRig.PartCombat.TryGetPrimaryMeleeStrikeProfile(out primaryProf);
            float curHeading = attackerRig.CurrentHeadingDeg;
            Vector3 targSim = target is Pawn tpSim ? CompGiantRig.GetSimTickPos(tpSim) : target.DrawPos;
            Vector2 targetCenterXZ = new Vector2(targSim.x, targSim.z);
            float maxLungeContactGap = ((attackerRig.RigDef?.enableDynamicMeleeContactLunge ?? true)
                ? Mathf.Clamp(attackerRig.RigDef?.maxDynamicMeleeContactLungeCells ?? 0.85f, 0f, 2.0f)
                : 0.15f) + 0.42f;

            float bestSnugScore = float.MinValue;
            IntVec3 bestSnugCell = IntVec3.Invalid;
            float bestFallbackScore = float.MinValue;
            IntVec3 bestFallbackCell = IntVec3.Invalid;
            IntVec3 selfPos = attacker.Position;
            float snugSqMax = snugTouchRadius * snugTouchRadius;
            float fallbackSqMax = (fallbackTouchRadius + 0.35f) * (fallbackTouchRadius + 0.35f);

            foreach (IntVec3 c in searchRect)
            {
                if (GiantArenaService.IsOutsideActiveArenaInnerRect(map, c, margin: 1))
                {
                    continue;
                }
                bool cellPassable = attackerRig.IsCurrentlyAirborne
                    ? GiantFlightUtility.IsCellPassableForFlight(map, attackerRig, c)
                    : GiantDestructionUtility.IsCellWalkableOrBreachableForGiant(map, attacker, c);
                if (!cellPassable)
                {
                    continue;
                }
                if (defendLocus.IsValid && defendRadius < 9000f && defendLocus.DistanceToSquared(c) > defendRadius * defendRadius)
                {
                    continue;
                }

                Vector2 candRootXZ = new Vector2(c.x + 0.5f, c.z + 0.5f);
                Vector2 toTarget = targetCenterXZ - candRootXZ;
                float bearingToTarget = toTarget.sqrMagnitude > 0.01f
                    ? SkeletalKinematics.NormalizeAngle360(Mathf.Atan2(toTarget.x, toTarget.y) * Mathf.Rad2Deg)
                    : curHeading;

                float candHeading;
                float canopyBestFleshGap = 0f;
                float canopyReachableDps = 0f;
                bool hasCanopyEval = false;
                if (useCanopyPerimeter && attackerRig.PartCombat != null)
                {
                    hasCanopyEval = attackerRig.PartCombat.EvaluateCanopyPerimeterMeleeAtCell(
                        candRootXZ,
                        targetCenterXZ,
                        bearingToTarget,
                        target,
                        maxLungeContactGap,
                        out candHeading,
                        out canopyBestFleshGap,
                        out canopyReachableDps);
                }
                else
                {
                    candHeading = hasStrikeProfile
                        ? SkeletalKinematics.NormalizeAngle360(bearingToTarget - primaryProf.ImpactBearingDeg)
                        : bearingToTarget;
                }

                int octant = ((Mathf.RoundToInt(Mathf.DeltaAngle(curHeading, candHeading) / 45f) % 8) + 8) % 8;
                IReadOnlyList<IntVec3> alignedOffsets = GetRotatedHullOffsetsBucket(attackerOffsets, octant);

                bool isLiveGiantTarget = targetRig != null && target is Pawn tpLive && !tpLive.Dead && !tpLive.Downed;
                if (isLiveGiantTarget)
                {
                    if (GiantHullCollisionUtility.CountTranslatedHullOverlap(c, attackerOffsets, targetRig.Footprint.collisionCellSet) > 0 ||
                        GiantHullCollisionUtility.CountTranslatedHullOverlap(c, alignedOffsets, targetRig.Footprint.collisionCellSet) > 0)
                    {
                        continue;
                    }
                }
                if (lowSkimTarget &&
                    GiantPartCombatRuntime.CountHullOverlapWithForbiddenSet(c, alignedOffsets, targetRig.Footprint.hitCellSet) > 0)
                {
                    continue;
                }

                float minGapSq;
                if (targetSolidCells != null)
                {
                    minGapSq = ComputeMinTranslatedHullGapSq(c, alignedOffsets, targetSolidCells, targetSolidSet);
                    if (minGapSq > fallbackSqMax)
                    {
                        continue;
                    }
                }
                else if (lowSkimTarget)
                {
                    minGapSq = GiantPartCombatRuntime.ComputeHullGapToHitCellsSq(
                        c,
                        alignedOffsets,
                        targetRig.Footprint.hitCells,
                        targetRig.Footprint.hitCellSet);
                    if (minGapSq <= 0.01f || minGapSq > fallbackSqMax)
                    {
                        continue;
                    }
                }
                else
                {
                    if (!HarmonyPatches_CombatAndHitbox.IsGiantHullInTouchRangeOfCells(
                            c,
                            alignedOffsets,
                            fallbackTouchRadius,
                            fallbackHits,
                            targetRect,
                            targetSolidSet))
                    {
                        continue;
                    }
                    minGapSq = ComputeMinTranslatedHullGapToRectSq(c, alignedOffsets, targetRect);
                    if (useCanopyPerimeter && minGapSq <= 0.01f)
                    {
                        // Never choose a destination cell whose visual canopy overlaps and shoves the small pawn away!
                        continue;
                    }
                }

                if (mapComp != null && mapComp.CountGiantHullOverlapAt(attackerRig, c, ignoreComp: targetRig) > 0)
                {
                    continue;
                }
                if (c != selfPos && !attacker.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }

                float gap = Mathf.Sqrt(Mathf.Max(0f, minGapSq));
                float travel = selfPos.DistanceTo(c);
                float idealGap = lowSkimTarget ? 1.35f : 1.0f;
                float score = 300f - Mathf.Abs(gap - idealGap) * 24f - travel * 1.25f;
                if (c == selfPos)
                {
                    score += 6f;
                }

                bool strikeReachesFlesh = true;
                bool longReachNaturalContact = false;
                if (useCanopyPerimeter && attackerRig.PartCombat != null)
                {
                    strikeReachesFlesh = hasCanopyEval && canopyReachableDps > 0.01f;
                    score += Mathf.Min(10f, canopyReachableDps * 0.18f) - Mathf.Min(6f, canopyBestFleshGap) * 55f;
                }
                else if (hasStrikeProfile)
                {
                    Vector2 candStrikeXZ = candRootXZ + GiantPartCombatRuntime.RotateLocalToHeadingXZ(primaryProf.ImpactLocalXZ, candHeading);
                    float fleshGap = GiantPartCombatRuntime.MeasurePointDistanceToTargetVisualFlesh(candStrikeXZ, target, targetRig);
                    score -= fleshGap * 65f;
                    if (fleshGap > maxLungeContactGap)
                    {
                        strikeReachesFlesh = false;
                    }
                    else if (targetRig != null && toTarget.sqrMagnitude > 0.25f)
                    {
                        Vector2 dirToTarget = toTarget.normalized;
                        // Signed progress of strike point relative to opponent center: < 0 means on the front/near half of opponent;
                        // > 0 means attacker's head/weapon overshot past opponent's center onto their far back!
                        float pastCenterCells = Vector2.Dot(candStrikeXZ - targetCenterXZ, dirToTarget);
                        if (pastCenterCells > -0.35f)
                        {
                            float overshoot = pastCenterCells + 0.35f;
                            score -= overshoot * 72f;
                        }
                        if (fleshGap <= 0.35f && pastCenterCells <= 0.15f && minGapSq <= (snugTouchRadius + 2.15f) * (snugTouchRadius + 2.15f))
                        {
                            // Long-necked wyverns/dragons whose strikeUV already bites cleanly into the front half of opponent's flesh
                            // should treat this natural weapon-contact standoff as first-class snug contact rather than ramming chests together!
                            longReachNaturalContact = true;
                            score += Mathf.Abs(gap - idealGap) * 18f;
                        }
                    }
                }

                if ((minGapSq <= snugSqMax && strikeReachesFlesh) || longReachNaturalContact)
                {
                    if (score > bestSnugScore)
                    {
                        bestSnugScore = score;
                        bestSnugCell = c;
                    }
                }
                else if (score > bestFallbackScore)
                {
                    bestFallbackScore = score;
                    bestFallbackCell = c;
                }
            }

            bestCell = bestSnugCell.IsValid ? bestSnugCell : bestFallbackCell;
            return bestCell.IsValid;
        }

        private static float ComputeMinTranslatedHullGapSq(
            IntVec3 anchor,
            IReadOnlyList<IntVec3> attackerOffsets,
            IReadOnlyList<IntVec3> targetSolidCells,
            HashSet<IntVec3> targetSolidSet)
        {
            float minSq = float.MaxValue;
            if (attackerOffsets != null && attackerOffsets.Count > 0)
            {
                for (int i = 0; i < attackerOffsets.Count; i++)
                {
                    IntVec3 ac = anchor + attackerOffsets[i];
                    if (targetSolidSet != null && targetSolidSet.Contains(ac))
                    {
                        return 0f;
                    }
                    for (int j = 0; j < targetSolidCells.Count; j++)
                    {
                        int dx = ac.x - targetSolidCells[j].x;
                        int dz = ac.z - targetSolidCells[j].z;
                        float dSq = dx * dx + dz * dz;
                        if (dSq < minSq)
                        {
                            minSq = dSq;
                            if (minSq <= 0f)
                            {
                                return 0f;
                            }
                        }
                    }
                }
                return minSq;
            }

            for (int j = 0; j < targetSolidCells.Count; j++)
            {
                int dx = anchor.x - targetSolidCells[j].x;
                int dz = anchor.z - targetSolidCells[j].z;
                float dSq = dx * dx + dz * dz;
                if (dSq < minSq)
                {
                    minSq = dSq;
                }
            }
            return minSq;
        }

        public static float ComputeMinTranslatedHullGapToRectSq(
            IntVec3 anchor,
            IReadOnlyList<IntVec3> attackerOffsets,
            CellRect targetRect)
        {
            float minSq = float.MaxValue;
            if (attackerOffsets != null && attackerOffsets.Count > 0)
            {
                for (int i = 0; i < attackerOffsets.Count; i++)
                {
                    IntVec3 ac = anchor + attackerOffsets[i];
                    int cx = Mathf.Clamp(ac.x, targetRect.minX, targetRect.maxX);
                    int cz = Mathf.Clamp(ac.z, targetRect.minZ, targetRect.maxZ);
                    int dx = ac.x - cx;
                    int dz = ac.z - cz;
                    float dSq = dx * dx + dz * dz;
                    if (dSq < minSq)
                    {
                        minSq = dSq;
                        if (minSq <= 0f)
                        {
                            return 0f;
                        }
                    }
                }
                return minSq;
            }
            int rx = Mathf.Clamp(anchor.x, targetRect.minX, targetRect.maxX);
            int rz = Mathf.Clamp(anchor.z, targetRect.minZ, targetRect.maxZ);
            return (anchor.x - rx) * (anchor.x - rx) + (anchor.z - rz) * (anchor.z - rz);
        }

        public static int ComputePivotAwareGotoExpiryTicks(
            CompGiantRig rig,
            IntVec3 fromCell,
            IntVec3 destCell,
            int baseExpiryTicks)
        {
            int safeBase = Mathf.Max(25, baseExpiryTicks);
            if (rig?.RigDef == null
                || !rig.RigDef.enablePivotTurnBeforeMove
                || rig.IsCurrentlyAirborne
                || fromCell == destCell)
            {
                return safeBase;
            }
            float moveBearing = Mathf.Atan2(destCell.x - fromCell.x, destCell.z - fromCell.z) * Mathf.Rad2Deg;
            float absTurnErr = Mathf.Abs(Mathf.DeltaAngle(rig.CurrentHeadingDeg, moveBearing));
            if (absTurnErr <= rig.RigDef.pivotTurnEnterAngleDeg)
            {
                return safeBase;
            }
            int pivotTicks = Mathf.CeilToInt(absTurnErr / Mathf.Max(0.5f, rig.RigDef.turnSpeedDegPerTick)) + 20;
            return Mathf.Clamp(safeBase + pivotTicks, safeBase, 175);
        }

        internal static void SetFacingToward(Pawn pawn, IntVec3 targetCell)
        {
            IntVec3 d = targetCell - pawn.Position;
            if (d.x == 0 && d.z == 0)
            {
                return;
            }
            pawn.Rotation = Rot4.FromAngleFlat((targetCell - pawn.Position).AngleFlat);
        }

        public static bool CanOrderPlayerPawnOrGiant(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead)
            {
                return false;
            }
            if (GiantFastLookup.TryGetRig(pawn) != null)
            {
                return GiantPlayerControlUtility.CanControl(pawn);
            }
            return pawn.IsColonistPlayerControlled
                || pawn.IsColonyMechPlayerControlled
                || pawn.IsColonySubhumanPlayerControlled;
        }

        public static bool AnySelectedObjectIsPlayerGiant()
        {
            List<object> selected = Find.Selector?.SelectedObjectsListForReading;
            if (selected == null || selected.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is Pawn p && GiantPlayerControlUtility.IsPlayerGiant(p))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool SelectedSquadHasDifferentWeaponsOrGiants()
        {
            List<object> selected = Find.Selector?.SelectedObjectsListForReading;
            if (selected == null || selected.Count <= 1)
            {
                return false;
            }

            ThingDef firstWeaponOrGiantDef = null;
            bool hasFirst = false;
            for (int i = 0; i < selected.Count; i++)
            {
                if (!(selected[i] is Pawn p) || !CanOrderPlayerPawnOrGiant(p))
                {
                    continue;
                }
                ThingDef sigDef = GiantFastLookup.TryGetRig(p) != null
                    ? p.def
                    : p.equipment?.Primary?.def;
                if (!hasFirst)
                {
                    firstWeaponOrGiantDef = sigDef;
                    hasFirst = true;
                }
                else if (sigDef != firstWeaponOrGiantDef)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool SelectedSquadHasAnyRangedCapability(Pawn fallbackSinglePawn)
        {
            List<object> selected = Find.Selector?.SelectedObjectsListForReading;
            if (selected == null || selected.Count == 0)
            {
                return HasRangedCapabilityForGizmo(fallbackSinglePawn);
            }
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is Pawn p && CanOrderPlayerPawnOrGiant(p) && HasRangedCapabilityForGizmo(p))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool SelectedSquadHasAnyMeleeCapability(Pawn fallbackSinglePawn)
        {
            List<object> selected = Find.Selector?.SelectedObjectsListForReading;
            if (selected == null || selected.Count == 0)
            {
                return HasMeleeCapabilityForGizmo(fallbackSinglePawn);
            }
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is Pawn p && CanOrderPlayerPawnOrGiant(p) && HasMeleeCapabilityForGizmo(p))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasRangedCapabilityForGizmo(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (GiantFastLookup.TryGetRig(pawn) is CompGiantRig rig && rig.PartCombat != null)
            {
                return rig.PartCombat.HasAnyConfiguredRangedOrCarrier();
            }
            return pawn.equipment?.Primary?.def?.IsRangedWeapon == true;
        }

        private static bool HasMeleeCapabilityForGizmo(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (GiantFastLookup.TryGetRig(pawn) is CompGiantRig rig && rig.PartCombat != null)
            {
                return rig.PartCombat.HasAnyConfiguredMelee();
            }
            return true;
        }

        public static Command_Toggle TryBuildFireAtWillGizmo(Pawn pawn, CompGiantRig rig)
        {
            if (pawn == null || rig?.PartCombat == null || !pawn.Drafted || !rig.PartCombat.HasAnyConfiguredRangedOrCarrier())
            {
                return null;
            }
            CompGiantCockpit cockpit = GiantFastLookup.TryGetCockpit(pawn);
            if (cockpit != null && cockpit.IsDormantUnmanned)
            {
                return null;
            }
            return new Command_Toggle
            {
                hotKey = KeyBindingDefOf.Misc6,
                isActive = () => rig.PartCombat.FireAtWill,
                toggleAction = delegate
                {
                    bool next = !rig.PartCombat.FireAtWill;
                    rig.PartCombat.FireAtWill = next;
                },
                icon = TexCommand.FireAtWill,
                defaultLabel = "CommandFireAtWillLabel".CanTranslate()
                    ? "CommandFireAtWillLabel".Translate().ToString()
                    : "Fire at will",
                defaultDesc = "CommandFireAtWillDesc".CanTranslate()
                    ? "CommandFireAtWillDesc".Translate().ToString()
                    : "Toggle fire at will.",
                tutorTag = "FireAtWillToggle"
            };
        }

        public static IEnumerable<Gizmo> GetVanillaStyleAttackGizmos(Pawn pawn)
        {
            if (pawn == null || !pawn.Drafted)
            {
                yield break;
            }

            bool isMulti = (Find.Selector?.NumSelected ?? 1) > 1;
            CompGiantRig selfRig = GiantFastLookup.TryGetRig(pawn);

            bool showMelee = isMulti
                ? SelectedSquadHasAnyMeleeCapability(pawn)
                : HasMeleeCapabilityForGizmo(pawn);
            if (showMelee)
            {
                yield return BuildVanillaMeleeAttackGizmo(pawn);
            }

            bool showRanged = selfRig != null
                ? (isMulti ? SelectedSquadHasAnyRangedCapability(pawn) : HasRangedCapabilityForGizmo(pawn))
                : (isMulti && SelectedSquadHasAnyRangedCapability(pawn) && SelectedSquadHasDifferentWeaponsOrGiants());
            if (showRanged)
            {
                yield return BuildVanillaRangedAttackGizmo(pawn, isMulti);
            }
        }

        public static Command_Target BuildVanillaRangedAttackGizmo(Pawn pawn, bool isMultiSelect)
        {
            var cmd = new Command_Target
            {
                defaultLabel = "CommandSquadAttack".CanTranslate()
                    ? "CommandSquadAttack".Translate().ToString()
                    : "Attack",
                defaultDesc = "CommandSquadAttackDesc".CanTranslate()
                    ? "CommandSquadAttackDesc".Translate().ToString()
                    : "Order selected units to attack designated target.",
                hotKey = KeyBindingDefOf.Misc1,
                icon = isMultiSelect ? TexCommand.SquadAttack : (TexCommand.Attack ?? TexCommand.SquadAttack),
                targetingParams = TargetingParameters.ForAttackAny()
            };
            cmd.targetingParams.canTargetLocations = false;

            string probeFail;
            Action probeAction = GiantFastLookup.TryGetRig(pawn) != null && !isMultiSelect
                ? FloatMenuUtility.GetRangedAttackAction(pawn, LocalTargetInfo.Invalid, out probeFail)
                : FloatMenuUtility.GetAttackAction(pawn, LocalTargetInfo.Invalid, out probeFail);
            if (probeAction == null)
            {
                string reason = probeFail.NullOrEmpty()
                    ? ("Incapable".CanTranslate() ? "Incapable".Translate().ToString() : "Incapable")
                    : probeFail;
                cmd.Disable(reason.CapitalizeFirst().TrimEnd('.') + ".");
            }

            cmd.action = delegate(LocalTargetInfo target)
            {
                ExecuteSquadOrSingleAttackOrder(pawn, target, meleeOnly: false);
            };
            cmd.onUpdate = delegate
            {
                DrawSquadOrSingleRangedRadiusRings(pawn);
            };
            return cmd;
        }

        public static Command_Target BuildVanillaMeleeAttackGizmo(Pawn pawn)
        {
            var cmd = new Command_Target
            {
                defaultLabel = "CommandMeleeAttack".CanTranslate()
                    ? "CommandMeleeAttack".Translate().ToString()
                    : "Melee attack",
                defaultDesc = "CommandMeleeAttackDesc".CanTranslate()
                    ? "CommandMeleeAttackDesc".Translate().ToString()
                    : "Order selected units to attack designated target using melee attacks only.",
                hotKey = KeyBindingDefOf.Misc2,
                icon = TexCommand.AttackMelee,
                targetingParams = TargetingParameters.ForAttackAny()
            };
            cmd.targetingParams.canTargetLocations = false;

            if (FloatMenuUtility.GetMeleeAttackAction(pawn, LocalTargetInfo.Invalid, out string probeFail) == null)
            {
                string reason = probeFail.NullOrEmpty()
                    ? ("Incapable".CanTranslate() ? "Incapable".Translate().ToString() : "Incapable")
                    : probeFail;
                cmd.Disable(reason.CapitalizeFirst().TrimEnd('.') + ".");
            }

            cmd.action = delegate(LocalTargetInfo target)
            {
                ExecuteSquadOrSingleAttackOrder(pawn, target, meleeOnly: true);
            };
            return cmd;
        }

        private static void ExecuteSquadOrSingleAttackOrder(Pawn fallbackPawn, LocalTargetInfo target, bool meleeOnly)
        {
            if (!target.IsValid)
            {
                return;
            }

            bool hasClick = GiantTargetAimTracker.TryGetActiveClick(out IntVec3 clickCell, out Vector3 clickWorldPos);
            Pawn clickedGiant = target.Thing is Pawn gp && GiantFastLookup.TryGetRig(gp) != null ? gp : null;

            List<object> selected = Find.Selector?.SelectedObjectsListForReading;
            if (selected == null || selected.Count == 0)
            {
                if (fallbackPawn != null && fallbackPawn.Drafted && CanOrderPlayerPawnOrGiant(fallbackPawn))
                {
                    if (hasClick && clickedGiant != null)
                    {
                        GiantTargetAimTracker.RecordHitCellClick(fallbackPawn, clickedGiant, clickCell, clickWorldPos);
                    }
                    string singleFail;
                    Action singleAct = meleeOnly
                        ? FloatMenuUtility.GetMeleeAttackAction(fallbackPawn, target, out singleFail)
                        : (GiantFastLookup.TryGetRig(fallbackPawn) != null
                            ? FloatMenuUtility.GetRangedAttackAction(fallbackPawn, target, out singleFail)
                            : FloatMenuUtility.GetAttackAction(fallbackPawn, target, out singleFail));
                    if (singleAct != null)
                    {
                        singleAct();
                    }
                    else if (!singleFail.NullOrEmpty())
                    {
                        Messages.Message(singleFail, target.Thing, MessageTypeDefOf.RejectInput, historical: false);
                    }
                }
                return;
            }

            bool isSingleSelected = selected.Count == 1;
            for (int i = 0; i < selected.Count; i++)
            {
                if (!(selected[i] is Pawn item) || !item.Drafted || !CanOrderPlayerPawnOrGiant(item))
                {
                    continue;
                }
                if (hasClick && clickedGiant != null)
                {
                    GiantTargetAimTracker.RecordHitCellClick(item, clickedGiant, clickCell, clickWorldPos);
                }

                string failStr;
                Action attackAction = meleeOnly
                    ? FloatMenuUtility.GetMeleeAttackAction(item, target, out failStr)
                    : (isSingleSelected && GiantFastLookup.TryGetRig(item) != null
                        ? FloatMenuUtility.GetRangedAttackAction(item, target, out failStr)
                        : FloatMenuUtility.GetAttackAction(item, target, out failStr));
                if (attackAction != null)
                {
                    attackAction();
                }
                else if (!failStr.NullOrEmpty())
                {
                    Messages.Message(failStr, target.Thing, MessageTypeDefOf.RejectInput, historical: false);
                }
            }
        }

        private static void DrawSquadOrSingleRangedRadiusRings(Pawn fallbackPawn)
        {
            List<object> selected = Find.Selector?.SelectedObjectsListForReading;
            if (selected == null || selected.Count == 0)
            {
                if (fallbackPawn != null && GiantFastLookup.TryGetRig(fallbackPawn) is CompGiantRig fRig)
                {
                    fRig.PartCombat?.DrawRangedAttackRadiusRings(fallbackPawn);
                }
                return;
            }

            for (int i = 0; i < selected.Count; i++)
            {
                if (!(selected[i] is Pawn item) || !item.Drafted || !CanOrderPlayerPawnOrGiant(item))
                {
                    continue;
                }
                if (GiantFastLookup.TryGetRig(item) is CompGiantRig itemRig && itemRig.PartCombat != null)
                {
                    itemRig.PartCombat.DrawRangedAttackRadiusRings(item);
                }
                else if (item.equipment != null)
                {
                    List<ThingWithComps> eqList = item.equipment.AllEquipmentListForReading;
                    for (int e = 0; e < eqList.Count; e++)
                    {
                        ThingWithComps eq = eqList[e];
                        if (eq != null && eq.def.IsRangedWeapon && eq.TryGetComp<CompEquippable>(out CompEquippable compEq))
                        {
                            List<Verb> verbs = compEq.AllVerbs;
                            for (int v = 0; v < verbs.Count; v++)
                            {
                                if (verbs[v].verbProps.hasStandardCommand)
                                {
                                    verbs[v].verbProps.DrawRadiusRing(item.Position, verbs[v]);
                                    break;
                                }
                            }
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Pure-locomotion combat positioning ThinkNode_JobGiver for GCF giants.
    /// Replaces vanilla JobGiver_AIFightEnemies so giants never request base-Pawn AttackMelee or AttackStatic jobs.
    /// </summary>
    public class JobGiver_GiantPartCombatPositioning : ThinkNode_JobGiver
    {
        public float targetAcquireRadius = 58f;
        public float targetKeepRadius = 70f;

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            var copy = (JobGiver_GiantPartCombatPositioning)base.DeepCopy(resolve);
            copy.targetAcquireRadius = targetAcquireRadius;
            copy.targetKeepRadius = targetKeepRadius;
            return copy;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            float acq = targetAcquireRadius;
            float keep = targetKeepRadius;
            if (pawn != null)
            {
                if (pawn.InAggroMentalState
                    || pawn.MentalStateDef == MentalStateDefOf.Manhunter
                    || pawn.MentalStateDef == MentalStateDefOf.ManhunterPermanent)
                {
                    acq = Mathf.Max(acq, 9999f);
                    keep = Mathf.Max(keep, 9999f);
                }
                else if (GiantFastLookup.TryGetRig(pawn)?.HasActiveProvokedTarget == true)
                {
                    acq = Mathf.Max(acq, 120f);
                    keep = Mathf.Max(keep, 140f);
                }
            }
            return GiantPartCombatUtility.TryGivePartCombatPositioningJob(
                pawn,
                maxAcquireRadius: acq,
                maxKeepRadius: keep);
        }
    }
}