using System;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public enum GiantHitReactionTier : byte
    {
        None = 0,
        Micro = 1,
        LocalFlinch = 2,
        HeavyFlinch = 3,
        PartBreakStagger = 4
    }

    public enum GiantHitDamageCategory : byte
    {
        Standard = 0,
        BluntOrExplosive = 1,
        SharpOrBallistic = 2,
        ThermalOrBurn = 3
    }

    public class GiantHitReactionDef
    {
        public bool enabled = true;
        public bool forceEnable = false;
        public float intensityScale = 1.0f;
        public float massScale = 1.0f;

        public float microThresholdPartRatio = 0.015f;
        public float heavyThresholdPartRatio = 0.14f;
        public float heavyThresholdTotalRatio = 0.045f;
        public float poiseThresholdTotalRatio = 0.085f;
        public float poiseDecayPerTick = 0.00028f;

        public int heavyCooldownTicks = 34;
        public int partBreakCooldownTicks = 44;

        public float attackAttenuation = 0.42f;
        public float rapidFireSuppression = 0.58f;
        public float bezierImpulseScale = 1.0f;
        public float softWhipImpulseScale = 1.0f;
    }

    public struct GiantHitImpulseInput
    {
        public int HitBoneIndex;
        public int HitSoftWhipIndex;
        public Vector2 WorldPushDirXZ;
        public float BodyHeadingDeg;
        public float DamageAmount;
        public float HitPartMaxHp;
        public float TotalBodyMaxHp;
        public float PawnBodySize;
        public GiantHitDamageCategory DamageCategory;
        public int GameTick;
        public bool IsAirborne;
        public bool IsAquatic;
        public bool IsDowned;
        public bool IsSleeping;
        public bool ForceTierOverride;
        public GiantHitReactionTier ForcedTier;
    }

    public struct GiantHitChainImpulseOutput
    {
        public GiantHitReactionTier Tier;
        public Vector2 BezierWorldImpulseXZ;
        public Vector2 SoftWhipWorldImpulseXZ;
        public int TargetSoftWhipIndex;
        public float SleepWakeImpulse01;
        public float VisualIntensity01;
    }

    /// <summary>
    /// Zero-allocation damped second-order spring solver for procedural biological giant hit reactions.
    /// Applies additive local offset, yaw, sagittal pitch, and coronal wing-flare impulses to BoneLocalPose
    /// without altering bone scale or moving isRigid child attachments.
    /// </summary>
    public sealed class GiantHitReactionRuntime
    {
        private const float MaxRootOffsetCells = 0.52f;
        private const float MaxRootAngleDeg = 22f;
        private const float MaxBoneAngleDeg = 34f;
        private const float MaxBoneSagittalDeg = 26f;
        private const float MaxBoneCoronalFlapDeg = 32f;
        private const float ActiveEnergyEpsSq = 1e-5f;

        private readonly int nodeCount;
        private readonly float[] boneAngle;
        private readonly float[] boneAngleVel;
        private readonly float[] boneSag;
        private readonly float[] boneSagVel;
        private readonly float[] boneFlap;
        private readonly float[] boneFlapVel;

        private Vector2 rootOffset;
        private Vector2 rootOffsetVel;
        private float rootAngle;
        private float rootAngleVel;

        private float poiseRatioAccum;
        private float rapidHitHeat;
        private int lastHitTick = -99999;
        private int lastHeavyTick = -99999;
        private int lastPartBreakTick = -99999;
        private GiantHitReactionTier lastTier = GiantHitReactionTier.None;
        private bool active;

        public int NodeCount => nodeCount;
        public bool IsActive => active;
        public GiantHitReactionTier LastTriggeredTier => lastTier;
        public Vector2 CurrentRootOffsetLocal => rootOffset;
        public float CurrentRootAngleDeg => rootAngle;
        public float CurrentPoiseRatio => poiseRatioAccum;
        public float CurrentRapidHitHeat => rapidHitHeat;

        public GiantHitReactionRuntime(int nodeCount)
        {
            this.nodeCount = Math.Max(1, nodeCount);
            boneAngle = new float[this.nodeCount];
            boneAngleVel = new float[this.nodeCount];
            boneSag = new float[this.nodeCount];
            boneSagVel = new float[this.nodeCount];
            boneFlap = new float[this.nodeCount];
            boneFlapVel = new float[this.nodeCount];
        }

        public void Reset()
        {
            Array.Clear(boneAngle, 0, boneAngle.Length);
            Array.Clear(boneAngleVel, 0, boneAngleVel.Length);
            Array.Clear(boneSag, 0, boneSag.Length);
            Array.Clear(boneSagVel, 0, boneSagVel.Length);
            Array.Clear(boneFlap, 0, boneFlap.Length);
            Array.Clear(boneFlapVel, 0, boneFlapVel.Length);
            rootOffset = Vector2.zero;
            rootOffsetVel = Vector2.zero;
            rootAngle = 0f;
            rootAngleVel = 0f;
            poiseRatioAccum = 0f;
            rapidHitHeat = 0f;
            lastHitTick = -99999;
            lastHeavyTick = -99999;
            lastPartBreakTick = -99999;
            lastTier = GiantHitReactionTier.None;
            active = false;
        }

        public float GetBoneAngleOffsetDeg(int boneIndex)
        {
            return (uint)boneIndex < (uint)boneAngle.Length ? boneAngle[boneIndex] : 0f;
        }

        public float GetBoneSagittalOffsetDeg(int boneIndex)
        {
            return (uint)boneIndex < (uint)boneSag.Length ? boneSag[boneIndex] : 0f;
        }

        public float GetBoneCoronalFlapOffsetDeg(int boneIndex)
        {
            return (uint)boneIndex < (uint)boneFlap.Length ? boneFlap[boneIndex] : 0f;
        }

        public static GiantHitDamageCategory ClassifyDamageDef(DamageDef def)
        {
            if (def == null)
            {
                return GiantHitDamageCategory.Standard;
            }
            string name = def.defName ?? string.Empty;
            if (name.IndexOf("Flame", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Burn", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Acid", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Frost", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Vaporize", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GiantHitDamageCategory.ThermalOrBurn;
            }
            if (def.isExplosive
                || name.IndexOf("Blunt", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Bomb", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Crush", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Demolish", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Thump", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Stun", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GiantHitDamageCategory.BluntOrExplosive;
            }
            if (def.isRanged
                || name.IndexOf("Bullet", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Cut", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Stab", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Scratch", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Bite", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Arrow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return GiantHitDamageCategory.SharpOrBallistic;
            }
            return GiantHitDamageCategory.Standard;
        }

        public GiantHitChainImpulseOutput TriggerHit(
            GiantRigDef rig,
            in GiantHitImpulseInput input,
            ReadOnlySpan<bool> partMissingMask)
        {
            GiantHitChainImpulseOutput output = default;
            output.TargetSoftWhipIndex = input.HitSoftWhipIndex;
            if (rig == null || rig.SortedNodes == null || rig.SortedNodes.Length == 0)
            {
                return output;
            }

            GiantHitReactionDef cfg = rig.hitReaction;
            if (cfg != null && !cfg.enabled)
            {
                return output;
            }

            float intensityScale = Mathf.Clamp(cfg?.intensityScale ?? 1.0f, 0.1f, 2.5f);
            float massScale = Mathf.Clamp(cfg?.massScale ?? 1.0f, 0.4f, 3.0f);
            float microRatio = Mathf.Clamp(cfg?.microThresholdPartRatio ?? 0.015f, 0.001f, 0.15f);
            float heavyPartRatio = Mathf.Clamp(cfg?.heavyThresholdPartRatio ?? 0.14f, 0.03f, 0.80f);
            float heavyTotalRatio = Mathf.Clamp(cfg?.heavyThresholdTotalRatio ?? 0.045f, 0.01f, 0.50f);
            float poiseThreshold = Mathf.Clamp(cfg?.poiseThresholdTotalRatio ?? 0.085f, 0.04f, 1.0f);
            int heavyCooldown = Math.Max(8, cfg?.heavyCooldownTicks ?? 34);
            int partBreakCooldown = Math.Max(12, cfg?.partBreakCooldownTicks ?? 44);
            float rapidSuppress = Mathf.Clamp01(cfg?.rapidFireSuppression ?? 0.58f);

            float safePartMax = Mathf.Max(10f, input.HitPartMaxHp);
            float safeTotalMax = Mathf.Max(80f, input.TotalBodyMaxHp);
            float partDmgRatio = Mathf.Clamp01(input.DamageAmount / safePartMax);
            float totalDmgRatio = Mathf.Clamp01(input.DamageAmount / safeTotalMax);

            // Damage category weighting shapes physical impact feel (blunt/explosive > sharp/ballistic > thermal).
            float categoryScale = input.DamageCategory switch
            {
                GiantHitDamageCategory.BluntOrExplosive => 1.32f,
                GiantHitDamageCategory.SharpOrBallistic => 0.96f,
                GiantHitDamageCategory.ThermalOrBurn => 0.78f,
                _ => 1.0f
            };
            float effectivePartRatio = partDmgRatio * categoryScale;
            float effectiveTotalRatio = totalDmgRatio * categoryScale;

            int tickDelta = input.GameTick - lastHitTick;
            if (tickDelta >= 0 && tickDelta <= 12)
            {
                rapidHitHeat = Mathf.Clamp01(rapidHitHeat + 0.34f);
            }
            else if (tickDelta > 12)
            {
                rapidHitHeat = Mathf.Max(0f, rapidHitHeat - tickDelta * 0.045f);
            }
            lastHitTick = input.GameTick;

            poiseRatioAccum = Mathf.Clamp01(poiseRatioAccum + effectiveTotalRatio);

            GiantHitReactionTier tier;
            if (input.ForceTierOverride && input.ForcedTier != GiantHitReactionTier.None)
            {
                tier = input.ForcedTier;
                if (tier == GiantHitReactionTier.HeavyFlinch)
                {
                    lastHeavyTick = input.GameTick;
                    poiseRatioAccum = 0f;
                }
                else if (tier == GiantHitReactionTier.PartBreakStagger)
                {
                    lastPartBreakTick = input.GameTick;
                    lastHeavyTick = input.GameTick;
                    poiseRatioAccum = 0f;
                }
            }
            else
            {
                bool heavyReady = (input.GameTick - lastHeavyTick) >= heavyCooldown;
                if (heavyReady && (effectivePartRatio >= heavyPartRatio || effectiveTotalRatio >= heavyTotalRatio || poiseRatioAccum >= poiseThreshold))
                {
                    tier = GiantHitReactionTier.HeavyFlinch;
                    lastHeavyTick = input.GameTick;
                    poiseRatioAccum = 0f;
                }
                else if (effectivePartRatio < microRatio && effectiveTotalRatio < microRatio * 0.45f)
                {
                    tier = GiantHitReactionTier.Micro;
                }
                else
                {
                    tier = GiantHitReactionTier.LocalFlinch;
                }
            }

            if (tier == GiantHitReactionTier.PartBreakStagger && !input.ForceTierOverride)
            {
                if ((input.GameTick - lastPartBreakTick) < partBreakCooldown)
                {
                    tier = GiantHitReactionTier.HeavyFlinch;
                }
                else
                {
                    lastPartBreakTick = input.GameTick;
                }
            }

            // Downed creatures still twitch subtly when struck, without large root slide.
            float downedScale = input.IsDowned ? 0.38f : 1.0f;

            // Rapid-fire damping preserves first impact punch while preventing machine-gun jitter.
            float rapidFactor = tier >= GiantHitReactionTier.HeavyFlinch
                ? Mathf.Lerp(1.0f, 0.78f, rapidHitHeat)
                : Mathf.Lerp(1.0f, 1.0f - rapidSuppress, rapidHitHeat);

            // Larger body mass slightly tempers raw displacement amplitude while preserving visual weight.
            float bodySizeRef = Mathf.Clamp(input.PawnBodySize > 0.1f ? input.PawnBodySize : 4.5f, 1.0f, 30.0f);
            float sizeGain = Mathf.Clamp(Mathf.Pow(5.0f / bodySizeRef, 0.14f) / Mathf.Sqrt(massScale), 0.68f, 1.25f);

            float tierBaseStrength = tier switch
            {
                GiantHitReactionTier.Micro => 0.42f,
                GiantHitReactionTier.LocalFlinch => Mathf.Lerp(0.82f, 1.18f, Mathf.Clamp01(effectivePartRatio / Mathf.Max(0.05f, heavyPartRatio))),
                GiantHitReactionTier.HeavyFlinch => 1.55f,
                GiantHitReactionTier.PartBreakStagger => 1.95f,
                _ => 0f
            };

            float amp = tierBaseStrength * intensityScale * sizeGain * rapidFactor * downedScale;
            if (amp <= 1e-4f)
            {
                return output;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            int len = Math.Min(nodeCount, nodes.Length);
            GiantSleepCurlProfile profile = GiantSleepCurlSolver.GetOrBuildProfile(rig);

            Vector2 worldPush = input.WorldPushDirXZ;
            if (worldPush.sqrMagnitude < 1e-6f)
            {
                worldPush = -SkeletalKinematics.RotateCW(Vector2.up, input.BodyHeadingDeg);
            }
            else
            {
                worldPush.Normalize();
            }

            Vector2 bodyForward = SkeletalKinematics.RotateCW(Vector2.up, input.BodyHeadingDeg);
            Vector2 bodyRight = SkeletalKinematics.RotateCW(Vector2.right, input.BodyHeadingDeg);
            float localPushRight = Vector2.Dot(worldPush, bodyRight);
            float localPushFwd = Vector2.Dot(worldPush, bodyForward);

            int hitBone = (uint)input.HitBoneIndex < (uint)len ? input.HitBoneIndex : 0;
            GiantSleepBoneRole hitRole = (profile != null && hitBone < profile.Roles.Length)
                ? profile.Roles[hitBone]
                : GiantSleepBoneRole.Root;

            float hitRelZ = (profile != null && hitBone < profile.BindRelZ.Length) ? profile.BindRelZ[hitBone] : 0f;
            float hitSide = (profile != null && hitBone < profile.WingOrLimbSideSign.Length) ? profile.WingOrLimbSideSign[hitBone] : 0f;

            // Clockwise torque sign in top-down XZ: tauCW = rz * fx - rx * fz.
            float leverZ = Mathf.Clamp(hitRelZ, -2.5f, 2.5f);
            float leverX = hitSide * 1.45f;
            float torqueCW = leverZ * localPushRight - leverX * localPushFwd;
            if (Mathf.Abs(torqueCW) < 0.18f)
            {
                float fallbackSign = Mathf.Abs(localPushRight) > 0.08f
                    ? Mathf.Sign(localPushRight)
                    : (Mathf.Abs(hitSide) > 0.01f ? -hitSide : (((input.GameTick & 1) == 0) ? 1f : -1f));
                torqueCW = fallbackSign * 0.45f;
            }
            float twistSign = Mathf.Sign(torqueCW);
            float torqueMag = Mathf.Clamp(Mathf.Abs(torqueCW), 0.35f, 1.65f);

            // 1. Root chassis recoil (offset along local push direction + subtle torsional twist).
            float rootOffsetGain = tier switch
            {
                GiantHitReactionTier.Micro => 0.10f,
                GiantHitReactionTier.LocalFlinch => 0.32f,
                GiantHitReactionTier.HeavyFlinch => 0.72f,
                GiantHitReactionTier.PartBreakStagger => 1.02f,
                _ => 0f
            };
            if (input.IsDowned)
            {
                rootOffsetGain *= 0.25f;
            }
            else if (input.IsAirborne)
            {
                rootOffsetGain *= 1.18f;
            }
            else if (input.IsAquatic)
            {
                rootOffsetGain *= 0.90f;
            }

            Vector2 rootImpulse = new Vector2(localPushRight, localPushFwd) * (0.28f * rootOffsetGain * amp);
            rootOffsetVel += rootImpulse;

            float rootTwistGain = tier switch
            {
                GiantHitReactionTier.Micro => 0.25f,
                GiantHitReactionTier.LocalFlinch => 0.75f,
                GiantHitReactionTier.HeavyFlinch => 1.55f,
                GiantHitReactionTier.PartBreakStagger => 2.25f,
                _ => 0f
            };
            rootAngleVel += twistSign * torqueMag * 2.2f * rootTwistGain * amp;

            // 2. Direct hit bone & immediate parent/child articulated flinch.
            bool thermalRecoil = input.DamageCategory == GiantHitDamageCategory.ThermalOrBurn;
            ApplyHitBoneTreeImpulse(
                nodes,
                profile,
                partMissingMask,
                hitBone,
                hitRole,
                twistSign,
                localPushRight,
                localPushFwd,
                amp,
                tier,
                thermalRecoil);

            // 3. Sympathetic whole-body reaction (head/neck snap, wing flare, leg brace, tail whip).
            ApplySympatheticBodyImpulse(
                nodes,
                profile,
                partMissingMask,
                hitBone,
                hitRole,
                twistSign,
                localPushRight,
                localPushFwd,
                amp,
                tier,
                input.IsAirborne,
                thermalRecoil);

            // 4. Chain impulse hints for Bezier tails and SoftWhips.
            float bezierScale = Mathf.Clamp(cfg?.bezierImpulseScale ?? 1.0f, 0f, 2.5f);
            float whipScale = Mathf.Clamp(cfg?.softWhipImpulseScale ?? 1.0f, 0f, 2.5f);
            float chainTierGain = tier switch
            {
                GiantHitReactionTier.Micro => 0.22f,
                GiantHitReactionTier.LocalFlinch => (hitRole == GiantSleepBoneRole.Tail || input.HitSoftWhipIndex >= 0) ? 0.92f : 0.45f,
                GiantHitReactionTier.HeavyFlinch => 1.15f,
                GiantHitReactionTier.PartBreakStagger => 1.65f,
                _ => 0f
            };

            // Add a perpendicular lash component so rear/frontal blows also create lively S-wave whip motion.
            Vector2 worldPerp = new Vector2(-worldPush.y, worldPush.x) * twistSign;
            Vector2 lashDir = (worldPush * 0.72f + worldPerp * 0.68f).normalized;

            output.Tier = tier;
            output.BezierWorldImpulseXZ = lashDir * (0.28f * chainTierGain * amp * bezierScale);
            output.SoftWhipWorldImpulseXZ = lashDir * (0.22f * chainTierGain * amp * whipScale);
            output.SleepWakeImpulse01 = input.IsSleeping
                ? Mathf.Clamp01(tier >= GiantHitReactionTier.HeavyFlinch ? 0.85f : 0.42f * amp)
                : 0f;
            output.VisualIntensity01 = Mathf.Clamp01(amp * 0.62f);

            lastTier = tier;
            active = true;
            return output;
        }

        private void ApplyHitBoneTreeImpulse(
            BoneNodeDef[] nodes,
            GiantSleepCurlProfile profile,
            ReadOnlySpan<bool> partMissingMask,
            int hitBone,
            GiantSleepBoneRole hitRole,
            float twistSign,
            float localPushRight,
            float localPushFwd,
            float amp,
            GiantHitReactionTier tier,
            bool thermalRecoil)
        {
            int len = Math.Min(nodeCount, nodes.Length);
            if ((uint)hitBone >= (uint)len)
            {
                return;
            }

            // If a welded/rigid child bone was struck, redirect the physical impulse to its nearest movable parent.
            int targetBone = hitBone;
            while (targetBone >= 0 && nodes[targetBone].isRigid && nodes[targetBone].parentRuntimeIndex >= 0)
            {
                targetBone = nodes[targetBone].parentRuntimeIndex;
            }
            if (targetBone < 0 || IsBoneMissing(partMissingMask, targetBone))
            {
                return;
            }

            float tierKick = tier switch
            {
                GiantHitReactionTier.Micro => 1.6f,
                GiantHitReactionTier.LocalFlinch => 4.2f,
                GiantHitReactionTier.HeavyFlinch => 6.4f,
                GiantHitReactionTier.PartBreakStagger => 8.2f,
                _ => 0f
            };

            float sideSign = (profile != null && targetBone < profile.WingOrLimbSideSign.Length)
                ? profile.WingOrLimbSideSign[targetBone]
                : 0f;

            switch (hitRole)
            {
                case GiantSleepBoneRole.AnteriorNeckHead:
                    // Head/neck snaps away from impact direction and jerks slightly upward/back in sagittal plane.
                    float headDir = Mathf.Abs(localPushRight) > 0.12f ? Mathf.Sign(localPushRight) : twistSign;
                    AddBoneVelocity(nodes, targetBone,
                        angleVel: headDir * tierKick * 1.45f * amp,
                        sagVel: (localPushFwd < 0f ? -1f : 0.6f) * tierKick * 0.75f * amp,
                        flapVel: 0f);
                    break;

                case GiantSleepBoneRole.Wing:
                    // Wing sweeps backward/inward and flares in coronal plane when hit.
                    float wingSweepSign = sideSign != 0f ? sideSign : twistSign;
                    AddBoneVelocity(nodes, targetBone,
                        angleVel: wingSweepSign * tierKick * 1.35f * amp,
                        sagVel: 0f,
                        flapVel: (thermalRecoil ? 1.25f : 1.0f) * tierKick * 1.65f * amp);
                    break;

                case GiantSleepBoneRole.Leg:
                    // Legs primarily react in sagittal pitch (retract/brace) with minimal horizontal yaw.
                    float sagDir = localPushFwd < -0.1f ? -1.1f : 0.9f;
                    AddBoneVelocity(nodes, targetBone,
                        angleVel: twistSign * tierKick * 0.25f * amp,
                        sagVel: sagDir * tierKick * 1.35f * amp,
                        flapVel: 0f);
                    break;

                case GiantSleepBoneRole.Tail:
                    // Tail lashes strongly away from blow.
                    float tailSign = Mathf.Abs(localPushRight) > 0.10f ? -Mathf.Sign(localPushRight) : twistSign;
                    AddBoneVelocity(nodes, targetBone,
                        angleVel: tailSign * tierKick * 1.55f * amp,
                        sagVel: 0f,
                        flapVel: 0f);
                    break;

                default:
                    // Torso/posterior segment: torsional snap along impact torque.
                    AddBoneVelocity(nodes, targetBone,
                        angleVel: twistSign * tierKick * 0.95f * amp,
                        sagVel: -tierKick * 0.35f * amp,
                        flapVel: 0f);
                    break;
            }

            // Propagate 55% to immediate movable parent and 65% to direct non-rigid children so the whole limb whips cohesively.
            int pIdx = nodes[targetBone].parentRuntimeIndex;
            if (pIdx > 0 && pIdx < len && !nodes[pIdx].isRigid && !IsBoneMissing(partMissingMask, pIdx))
            {
                AddBoneVelocity(nodes, pIdx,
                    angleVel: twistSign * tierKick * 0.55f * amp,
                    sagVel: (hitRole == GiantSleepBoneRole.Leg ? -0.5f : 0f) * tierKick * amp,
                    flapVel: hitRole == GiantSleepBoneRole.Wing ? tierKick * 0.75f * amp : 0f);
            }

            for (int i = targetBone + 1; i < len; i++)
            {
                if (nodes[i].parentRuntimeIndex != targetBone || nodes[i].isRigid || IsBoneMissing(partMissingMask, i))
                {
                    continue;
                }
                GiantSleepBoneRole childRole = (profile != null && i < profile.Roles.Length)
                    ? profile.Roles[i]
                    : GiantSleepBoneRole.None;
                if (childRole == GiantSleepBoneRole.RigidOrPassive)
                {
                    continue;
                }
                float childSign = hitRole == GiantSleepBoneRole.Wing && sideSign != 0f ? sideSign : twistSign;
                AddBoneVelocity(nodes, i,
                    angleVel: childSign * tierKick * 0.72f * amp,
                    sagVel: childRole == GiantSleepBoneRole.Leg ? -tierKick * 0.65f * amp : 0f,
                    flapVel: childRole == GiantSleepBoneRole.Wing ? tierKick * 0.85f * amp : 0f);
            }
        }

        private void ApplySympatheticBodyImpulse(
            BoneNodeDef[] nodes,
            GiantSleepCurlProfile profile,
            ReadOnlySpan<bool> partMissingMask,
            int hitBone,
            GiantSleepBoneRole hitRole,
            float twistSign,
            float localPushRight,
            float localPushFwd,
            float amp,
            GiantHitReactionTier tier,
            bool isAirborne,
            bool thermalRecoil)
        {
            if (profile == null)
            {
                return;
            }

            float sympatheticScale = tier switch
            {
                GiantHitReactionTier.Micro => 0.14f,
                GiantHitReactionTier.LocalFlinch => 0.48f,
                GiantHitReactionTier.HeavyFlinch => 1.05f,
                GiantHitReactionTier.PartBreakStagger => 1.55f,
                _ => 0f
            };
            if (sympatheticScale <= 1e-4f)
            {
                return;
            }

            int len = Math.Min(nodeCount, Math.Min(nodes.Length, profile.Roles.Length));
            for (int i = 1; i < len; i++)
            {
                if (i == hitBone || nodes[i].isRigid || IsBoneMissing(partMissingMask, i))
                {
                    continue;
                }
                if (profile.IsBezierChainBone[i])
                {
                    continue;
                }

                GiantSleepBoneRole role = profile.Roles[i];
                float chainLen = Math.Max(1, profile.RoleChainLength[i]);
                float depth01 = (profile.RoleDepth[i] + 1f) / chainLen;
                float side = profile.WingOrLimbSideSign[i];

                switch (role)
                {
                    case GiantSleepBoneRole.AnteriorNeckHead:
                        // Head/neck whips back in pain/alertness (stronger at distal head).
                        float headSign = Mathf.Abs(localPushRight) > 0.12f ? Mathf.Sign(localPushRight) : twistSign;
                        float headGain = (hitRole == GiantSleepBoneRole.AnteriorNeckHead ? 0.45f : 1.0f) * (0.65f + 0.55f * depth01);
                        float headRoarSag = tier == GiantHitReactionTier.PartBreakStagger ? -4.8f : -2.2f;
                        AddBoneVelocity(nodes, i,
                            angleVel: headSign * 3.2f * headGain * sympatheticScale * amp,
                            sagVel: headRoarSag * headGain * sympatheticScale * amp,
                            flapVel: 0f);
                        break;

                    case GiantSleepBoneRole.PosteriorTorso:
                        // Spine curves defensively away from the hit side.
                        AddBoneVelocity(nodes, i,
                            angleVel: -twistSign * 1.9f * sympatheticScale * amp,
                            sagVel: 0f,
                            flapVel: 0f);
                        break;

                    case GiantSleepBoneRole.Wing:
                        // Defensive/startled wing flare (stronger when airborne or on part-break).
                        float wingGain = isAirborne ? 1.35f : 1.0f;
                        if (thermalRecoil)
                        {
                            wingGain *= 1.18f;
                        }
                        float spreadDir = side != 0f ? -side : twistSign;
                        AddBoneVelocity(nodes, i,
                            angleVel: spreadDir * 2.1f * wingGain * sympatheticScale * amp,
                            sagVel: 0f,
                            flapVel: 4.4f * wingGain * sympatheticScale * amp);
                        break;

                    case GiantSleepBoneRole.Leg:
                        // Bracing crouch on heavy hits / stagger (sagittal pitch, almost zero yaw).
                        if (tier >= GiantHitReactionTier.HeavyFlinch)
                        {
                            float braceDir = profile.BindRelZ[i] >= 0f ? -1f : 1f;
                            AddBoneVelocity(nodes, i,
                                angleVel: 0f,
                                sagVel: braceDir * 2.8f * sympatheticScale * amp,
                                flapVel: 0f);
                        }
                        break;

                    case GiantSleepBoneRole.Tail:
                        // Non-Bezier articulated tail counter-whips for balance.
                        float tailGain = 0.55f + 0.70f * depth01;
                        AddBoneVelocity(nodes, i,
                            angleVel: -twistSign * 3.1f * tailGain * sympatheticScale * amp,
                            sagVel: 0f,
                            flapVel: 0f);
                        break;
                }
            }
        }

        private void AddBoneVelocity(BoneNodeDef[] nodes, int idx, float angleVel, float sagVel, float flapVel)
        {
            if ((uint)idx >= (uint)nodeCount)
            {
                return;
            }
            boneAngleVel[idx] = Mathf.Clamp(boneAngleVel[idx] + angleVel, -24f, 24f);
            if (Mathf.Abs(nodes[idx].sagittalDropCells) > 1e-4f)
            {
                boneSagVel[idx] = Mathf.Clamp(boneSagVel[idx] + sagVel, -20f, 20f);
            }
            if (nodes[idx].isCoronalFlapHinge)
            {
                boneFlapVel[idx] = Mathf.Clamp(boneFlapVel[idx] + flapVel, -26f, 26f);
            }
        }

        public void StepAndApply(
            GiantRigDef rig,
            float deltaTicks,
            BoneLocalPose[] inoutLocalPoses,
            ReadOnlySpan<bool> partMissingMask,
            bool isFullBodyAttacking,
            bool isDowned,
            ReadOnlySpan<bool> attackProtectedBones = default)
        {
            if (rig == null || inoutLocalPoses == null)
            {
                return;
            }

            GiantHitReactionDef cfg = rig.hitReaction;
            float poiseDecay = Mathf.Clamp(cfg?.poiseDecayPerTick ?? 0.00028f, 0.00005f, 0.05f);
            float dt = Mathf.Clamp(deltaTicks, 0f, 6f);
            if (dt > 0f)
            {
                poiseRatioAccum = Mathf.Max(0f, poiseRatioAccum - poiseDecay * dt);
                rapidHitHeat = Mathf.Max(0f, rapidHitHeat - 0.045f * dt);
            }

            if (!active)
            {
                return;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            int len = Math.Min(nodeCount, Math.Min(nodes.Length, inoutLocalPoses.Length));
            if (len <= 0)
            {
                active = false;
                return;
            }

            float massScale = Mathf.Clamp(cfg?.massScale ?? 1.0f, 0.4f, 3.0f);
            float freqScale = 1f / Mathf.Sqrt(massScale);

            // Second-order spring-damper parameters (omega in rad/tick, zeta in [0.54..0.66] for weighty damped overshoot).
            float rootOmega = 0.19f * freqScale;
            float rootZeta = 0.64f;
            float boneOmega = 0.23f * freqScale;
            float boneZeta = 0.54f;

            if (dt > 1e-5f)
            {
                int substeps = Mathf.Clamp(Mathf.CeilToInt(dt * 2f), 1, 12);
                float h = dt / substeps;
                float rW2 = rootOmega * rootOmega;
                float r2ZW = 2f * rootZeta * rootOmega;
                float bW2 = boneOmega * boneOmega;
                float b2ZW = 2f * boneZeta * boneOmega;

                for (int s = 0; s < substeps; s++)
                {
                    // Root offset spring
                    Vector2 accOff = -rW2 * rootOffset - r2ZW * rootOffsetVel;
                    rootOffsetVel += accOff * h;
                    rootOffset += rootOffsetVel * h;
                    float offSq = rootOffset.sqrMagnitude;
                    if (offSq > MaxRootOffsetCells * MaxRootOffsetCells)
                    {
                        rootOffset = rootOffset.normalized * MaxRootOffsetCells;
                        rootOffsetVel *= 0.45f;
                    }

                    // Root twist spring
                    float accAng = -rW2 * rootAngle - r2ZW * rootAngleVel;
                    rootAngleVel += accAng * h;
                    rootAngle = Mathf.Clamp(rootAngle + rootAngleVel * h, -MaxRootAngleDeg, MaxRootAngleDeg);

                    // Per-bone springs
                    for (int i = 0; i < len; i++)
                    {
                        float a = boneAngle[i];
                        float av = boneAngleVel[i];
                        if (a != 0f || av != 0f)
                        {
                            av += (-bW2 * a - b2ZW * av) * h;
                            a = Mathf.Clamp(a + av * h, -MaxBoneAngleDeg, MaxBoneAngleDeg);
                            boneAngle[i] = a;
                            boneAngleVel[i] = av;
                        }

                        float sg = boneSag[i];
                        float sv = boneSagVel[i];
                        if (sg != 0f || sv != 0f)
                        {
                            sv += (-bW2 * sg - b2ZW * sv) * h;
                            sg = Mathf.Clamp(sg + sv * h, -MaxBoneSagittalDeg, MaxBoneSagittalDeg);
                            boneSag[i] = sg;
                            boneSagVel[i] = sv;
                        }

                        float fl = boneFlap[i];
                        float fv = boneFlapVel[i];
                        if (fl != 0f || fv != 0f)
                        {
                            fv += (-bW2 * fl - b2ZW * fv) * h;
                            fl = Mathf.Clamp(fl + fv * h, -MaxBoneCoronalFlapDeg, MaxBoneCoronalFlapDeg);
                            boneFlap[i] = fl;
                            boneFlapVel[i] = fv;
                        }
                    }
                }
            }

            float attackAtten = Mathf.Clamp(cfg?.attackAttenuation ?? 0.42f, 0.1f, 1.0f);
            float rootScale = isFullBodyAttacking ? Mathf.Lerp(attackAtten, 1.0f, 0.45f) : 1.0f;
            if (isDowned)
            {
                rootScale *= 0.35f;
            }

            // Apply root recoil to bone 0 (unless missing).
            if (!IsBoneMissing(partMissingMask, 0))
            {
                inoutLocalPoses[0].offset += rootOffset * rootScale;
                inoutLocalPoses[0].angle += rootAngle * rootScale;
            }

            float energySq = rootOffset.sqrMagnitude * 16f
                + rootOffsetVel.sqrMagnitude * 16f
                + rootAngle * rootAngle * 0.02f
                + rootAngleVel * rootAngleVel * 0.05f;

            for (int i = 0; i < len; i++)
            {
                if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                {
                    boneAngle[i] = 0f;
                    boneAngleVel[i] = 0f;
                    boneSag[i] = 0f;
                    boneSagVel[i] = 0f;
                    boneFlap[i] = 0f;
                    boneFlapVel[i] = 0f;
                    continue;
                }
                if (IsBoneMissing(partMissingMask, i))
                {
                    boneAngle[i] = 0f;
                    boneAngleVel[i] = 0f;
                    boneSag[i] = 0f;
                    boneSagVel[i] = 0f;
                    boneFlap[i] = 0f;
                    boneFlapVel[i] = 0f;
                    continue;
                }

                float a = boneAngle[i];
                float sg = boneSag[i];
                float fl = boneFlap[i];

                float boneScale = 1.0f;
                if (isFullBodyAttacking)
                {
                    boneScale = attackAtten;
                }
                else if (i < attackProtectedBones.Length && attackProtectedBones[i])
                {
                    boneScale = attackAtten;
                }

                if (a != 0f)
                {
                    inoutLocalPoses[i].angle += a * boneScale;
                }
                if (sg != 0f)
                {
                    inoutLocalPoses[i].sagittalPitch += sg * boneScale;
                }
                if (fl != 0f && nodes[i].isCoronalFlapHinge)
                {
                    inoutLocalPoses[i].coronalFlap += fl * boneScale;
                }

                energySq += a * a * 0.02f + boneAngleVel[i] * boneAngleVel[i] * 0.05f
                          + sg * sg * 0.02f + boneSagVel[i] * boneSagVel[i] * 0.05f
                          + fl * fl * 0.02f + boneFlapVel[i] * boneFlapVel[i] * 0.05f;
            }

            if (energySq < ActiveEnergyEpsSq)
            {
                Array.Clear(boneAngle, 0, boneAngle.Length);
                Array.Clear(boneAngleVel, 0, boneAngleVel.Length);
                Array.Clear(boneSag, 0, boneSag.Length);
                Array.Clear(boneSagVel, 0, boneSagVel.Length);
                Array.Clear(boneFlap, 0, boneFlap.Length);
                Array.Clear(boneFlapVel, 0, boneFlapVel.Length);
                rootOffset = Vector2.zero;
                rootOffsetVel = Vector2.zero;
                rootAngle = 0f;
                rootAngleVel = 0f;
                active = false;
            }
        }

        private static bool IsBoneMissing(ReadOnlySpan<bool> partMissingMask, int idx)
        {
            return (uint)idx < (uint)partMissingMask.Length && partMissingMask[idx];
        }
    }
}
