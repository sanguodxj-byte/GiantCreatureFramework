using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace GiantCreatureFramework
{
    public enum GiantSleepBoneRole : byte
    {
        None = 0,
        Root = 1,
        AnteriorNeckHead = 2,
        PosteriorTorso = 3,
        Tail = 4,
        Wing = 5,
        Leg = 6,
        RigidOrPassive = 7
    }

    /// <summary>
    /// Pre-computed per-GiantRigDef kinematic profile for biological giant sleep curl-up ("旋转身体蜷缩").
    /// Automatically derives C-coil spinal/cervical/caudal bend angles, aligned jaw & dorsal wing tucking,
    /// sagittal-only limb folding (strictly 0 deg horizontal leg yaw), sleep-spot ring centering, and
    /// unified top-arch (+Z North) / bottom-gap (-Z South) target world headings.
    /// </summary>
    public sealed class GiantSleepCurlProfile
    {
        public readonly int NodeCount;
        public readonly GiantSleepBoneRole[] Roles;
        public readonly bool[] IsBezierChainBone;
        public readonly int[] RoleDepth;
        public readonly int[] RoleChainLength;
        public readonly float[] BindRelZ;

        /// <summary>
        /// Signed local angle delta (degrees) when curlSign == +1.
        /// Anterior (+Z) bones curl clockwise (+angle -> +X flank);
        /// Posterior/Tail (-Z) bones curl counter-clockwise (-angle -> same +X flank) to form a C-ring;
        /// Wing bones sweep symmetrically aftward (independent of curlSign);
        /// Leg bones are strictly 0 deg.
        /// </summary>
        public readonly float[] CurlAnglePlusSignDeg;
        public readonly bool[] AngleUsesCurlSign;
        public readonly float[] BreathAngleAmplitudeDeg;

        public readonly bool[] HasSagittalTarget;
        public readonly float[] TargetSagittalPitchDeg;

        public readonly bool[] HasCoronalFlapTarget;
        public readonly float[] WingOrLimbSideSign;
        public readonly float[] TargetEffCoronalFlapInnerDeg;
        public readonly float[] TargetEffCoronalFlapOuterDeg;

        /// <summary>
        /// Root local offset for curlSign == +1 that keeps the coiled body ring centered on Pawn.DrawPos.
        /// For curlSign == -1, X is mirrored (-X, +Y).
        /// </summary>
        public readonly Vector2 RootCenterOffsetPlusSign;

        /// <summary>
        /// Target world headings (degrees, 0 = North/+Z, 90 = East/+X, 180 = South/-Z) for curlSign == +1 and -1
        /// that orient the coiled dorsal body arch strictly upward (North/+Z, 12 o'clock) and the C-ring gap
        /// strictly downward (South/-Z, 6 o'clock).
        /// </summary>
        public readonly float TargetHeadingPlusSignDeg;
        public readonly float TargetHeadingMinusSignDeg;

        public GiantSleepCurlProfile(int count)
        {
            NodeCount = count;
            Roles = new GiantSleepBoneRole[count];
            IsBezierChainBone = new bool[count];
            RoleDepth = new int[count];
            RoleChainLength = new int[count];
            BindRelZ = new float[count];
            CurlAnglePlusSignDeg = new float[count];
            AngleUsesCurlSign = new bool[count];
            BreathAngleAmplitudeDeg = new float[count];
            HasSagittalTarget = new bool[count];
            TargetSagittalPitchDeg = new float[count];
            HasCoronalFlapTarget = new bool[count];
            WingOrLimbSideSign = new float[count];
            TargetEffCoronalFlapInnerDeg = new float[count];
            TargetEffCoronalFlapOuterDeg = new float[count];
            RootCenterOffsetPlusSign = Vector2.zero;
            TargetHeadingPlusSignDeg = 90f;
            TargetHeadingMinusSignDeg = 270f;
        }

        internal GiantSleepCurlProfile(
            int count,
            GiantSleepBoneRole[] roles,
            bool[] isBezier,
            int[] roleDepth,
            int[] roleChainLength,
            float[] bindRelZ,
            float[] curlAngle,
            bool[] angleUsesSign,
            float[] breathAmp,
            bool[] hasSag,
            float[] targetSag,
            bool[] hasFlap,
            float[] sideSign,
            float[] flapInner,
            float[] flapOuter,
            Vector2 rootCenterOffsetPlusSign,
            float targetHeadingPlusSignDeg = 90f,
            float targetHeadingMinusSignDeg = 270f)
        {
            NodeCount = count;
            Roles = roles;
            IsBezierChainBone = isBezier;
            RoleDepth = roleDepth;
            RoleChainLength = roleChainLength;
            BindRelZ = bindRelZ;
            CurlAnglePlusSignDeg = curlAngle;
            AngleUsesCurlSign = angleUsesSign;
            BreathAngleAmplitudeDeg = breathAmp;
            HasSagittalTarget = hasSag;
            TargetSagittalPitchDeg = targetSag;
            HasCoronalFlapTarget = hasFlap;
            WingOrLimbSideSign = sideSign;
            TargetEffCoronalFlapInnerDeg = flapInner;
            TargetEffCoronalFlapOuterDeg = flapOuter;
            RootCenterOffsetPlusSign = rootCenterOffsetPlusSign;
            TargetHeadingPlusSignDeg = targetHeadingPlusSignDeg;
            TargetHeadingMinusSignDeg = targetHeadingMinusSignDeg;
        }
    }

    public static class GiantSleepCurlSolver
    {
        public const float CurlInDurationTicks = 60f;
        public const float UncurlDurationTicks = 24f;
        public const float BodyTurnTotalDeg = 90f;
        public const float DeepSleepBreathPeriodTicks = 192f;
        public const float DeepSleepIdleRateMultiplier = 0.30f;

        private static readonly ConditionalWeakTable<GiantRigDef, GiantSleepCurlProfile> ProfileCache =
            new ConditionalWeakTable<GiantRigDef, GiantSleepCurlProfile>();

        public static float SmoothStep01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        public static float EaseInOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f
                ? 4f * t * t * t
                : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
        }

        public static GiantSleepCurlProfile GetOrBuildProfile(GiantRigDef rig)
        {
            if (rig == null)
            {
                return null;
            }
            return ProfileCache.GetValue(rig, BuildProfile);
        }

        /// <summary>
        /// Returns the deterministic target world heading (degrees) that places the coiled body arch at North (+Z, top)
        /// and the C-ring gap at South (-Z, bottom) for the given curlSideSign (+1 or -1).
        /// </summary>
        public static float GetTargetSleepHeadingDeg(GiantRigDef rig, float curlSideSign)
        {
            GiantSleepCurlProfile profile = GetOrBuildProfile(rig);
            if (profile == null || profile.NodeCount <= 0)
            {
                return curlSideSign >= 0f ? 90f : 270f;
            }
            return curlSideSign >= 0f ? profile.TargetHeadingPlusSignDeg : profile.TargetHeadingMinusSignDeg;
        }

        /// <summary>
        /// Chooses +1 or -1 curlSideSign via shortest angular turn from startHeadingDeg so the giant always settles
        /// into the unified top-body (+Z) / bottom-gap (-Z) C-ring without unnecessary >95-deg spins.
        /// Near-symmetric north/south starts deterministically favor +1 for visual consistency.
        /// </summary>
        public static float ChooseBestSleepSideSign(GiantRigDef rig, float startHeadingDeg)
        {
            GiantSleepCurlProfile profile = GetOrBuildProfile(rig);
            float hPlus = profile != null && profile.NodeCount > 0 ? profile.TargetHeadingPlusSignDeg : 90f;
            float hMinus = profile != null && profile.NodeCount > 0 ? profile.TargetHeadingMinusSignDeg : 270f;
            float dPlus = Mathf.Abs(Mathf.DeltaAngle(startHeadingDeg, hPlus));
            float dMinus = Mathf.Abs(Mathf.DeltaAngle(startHeadingDeg, hMinus));
            if (Mathf.Abs(dPlus - dMinus) <= 12f)
            {
                return 1f;
            }
            return dPlus <= dMinus ? 1f : -1f;
        }

        public static void ApplySleepCurl(
            GiantRigDef rig,
            BoneLocalPose[] inoutLocalPoses,
            ReadOnlySpan<bool> partMissingMask,
            float sleepProgress01,
            float curlSideSign,
            float breathTicks,
            bool includeBezierBones = true)
        {
            if (rig == null || inoutLocalPoses == null || sleepProgress01 <= 1e-4f)
            {
                return;
            }

            GiantSleepCurlProfile profile = GetOrBuildProfile(rig);
            if (profile == null || profile.NodeCount <= 0)
            {
                return;
            }
            ApplySleepCurlWithProfile(profile, rig, inoutLocalPoses, partMissingMask, sleepProgress01, curlSideSign, breathTicks, includeBezierBones);
        }

        internal static void ApplySleepCurlWithProfile(
            GiantSleepCurlProfile profile,
            GiantRigDef rig,
            BoneLocalPose[] inoutLocalPoses,
            ReadOnlySpan<bool> partMissingMask,
            float sleepProgress01,
            float curlSideSign,
            float breathTicks,
            bool includeBezierBones)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = Mathf.Min(nodes.Length, Mathf.Min(inoutLocalPoses.Length, profile.NodeCount));
            float sign = curlSideSign >= 0f ? 1f : -1f;

            // Coil ramps in smoothly alongside the body turn to top-arch / bottom-gap heading
            float wCoil = SmoothStep01(Mathf.Clamp01((sleepProgress01 - 0.04f) / 0.96f));
            float breathPhase = (2f * Mathf.PI * breathTicks) / DeepSleepBreathPeriodTicks;
            float breathSin = Mathf.Sin(breathPhase) * wCoil;

            // Center the coiled C-ring directly over Pawn.DrawPos
            if (count > 0 && !nodes[0].isRigid)
            {
                Vector2 targetRootOff = new Vector2(
                    profile.RootCenterOffsetPlusSign.x * sign,
                    profile.RootCenterOffsetPlusSign.y);
                inoutLocalPoses[0].offset = Vector2.LerpUnclamped(inoutLocalPoses[0].offset, targetRootOff, wCoil);
            }

            for (int i = 0; i < count; i++)
            {
                if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                {
                    inoutLocalPoses[i] = BoneLocalPose.Identity;
                    continue;
                }
                if (!partMissingMask.IsEmpty && i < partMissingMask.Length && partMissingMask[i] && nodes[i].hideWhenMissing)
                {
                    continue;
                }
                if (!includeBezierBones && profile.IsBezierChainBone[i])
                {
                    continue;
                }

                GiantSleepBoneRole role = profile.Roles[i];
                BoneLocalPose pose = inoutLocalPoses[i];

                if (role == GiantSleepBoneRole.Leg)
                {
                    // Smoothly blend any pre-existing horizontal leg angle toward 0 deg as wCoil ramps in
                    // (at full sleep curl wCoil == 1, top-down limb bones remain strictly at 0 deg).
                    pose.angle = Mathf.Lerp(pose.angle, 0f, wCoil);
                    if (profile.HasSagittalTarget[i])
                    {
                        pose.sagittalPitch = Mathf.LerpUnclamped(pose.sagittalPitch, profile.TargetSagittalPitchDeg[i], wCoil);
                    }
                    if (profile.HasCoronalFlapTarget[i])
                    {
                        float targetAnimFlap = profile.TargetEffCoronalFlapInnerDeg[i] - nodes[i].baseCoronalFlap;
                        pose.coronalFlap = Mathf.LerpUnclamped(pose.coronalFlap, targetAnimFlap, wCoil);
                    }
                    inoutLocalPoses[i] = pose;
                    continue;
                }

                if (role == GiantSleepBoneRole.RigidOrPassive)
                {
                    // Passive attachments (jaw, horns, crests, pods) stay cleanly aligned with their parent bone
                    pose.angle += Mathf.DeltaAngle(pose.angle, 0f) * wCoil;
                    inoutLocalPoses[i] = pose;
                    continue;
                }

                float baseCurlDeg = profile.CurlAnglePlusSignDeg[i];
                if (Mathf.Abs(baseCurlDeg) > 1e-4f || role is GiantSleepBoneRole.AnteriorNeckHead or GiantSleepBoneRole.PosteriorTorso or GiantSleepBoneRole.Tail or GiantSleepBoneRole.Wing)
                {
                    float signedTargetDeg = profile.AngleUsesCurlSign[i] ? (baseCurlDeg * sign) : baseCurlDeg;
                    float breathDeg = profile.BreathAngleAmplitudeDeg[i] * (profile.AngleUsesCurlSign[i] ? sign : 1f) * breathSin;
                    float desiredAngle = signedTargetDeg + breathDeg;
                    pose.angle = pose.angle + Mathf.DeltaAngle(pose.angle, desiredAngle) * wCoil;
                }

                if (profile.HasSagittalTarget[i])
                {
                    float sagBreath = role == GiantSleepBoneRole.AnteriorNeckHead ? (-1.2f * breathSin) : 0f;
                    pose.sagittalPitch = Mathf.LerpUnclamped(pose.sagittalPitch, profile.TargetSagittalPitchDeg[i] + sagBreath, wCoil);
                }

                if (profile.HasCoronalFlapTarget[i])
                {
                    bool isInnerSide = (profile.WingOrLimbSideSign[i] * sign) > 0f;
                    float targetEffFlap = isInnerSide
                        ? profile.TargetEffCoronalFlapInnerDeg[i]
                        : profile.TargetEffCoronalFlapOuterDeg[i];
                    // Subtle deep-sleep ribcage/wing cloak rise and fall
                    float flapBreath = -2.0f * breathSin;
                    float targetAnimFlap = (targetEffFlap + flapBreath) - nodes[i].baseCoronalFlap;
                    pose.coronalFlap = Mathf.LerpUnclamped(pose.coronalFlap, targetAnimFlap, wCoil);
                }

                inoutLocalPoses[i] = pose;
            }
        }

        public static void ApplySleepCurlToBezierBones(
            GiantRigDef rig,
            BoneLocalPose[] inoutLocalPoses,
            ReadOnlySpan<bool> partMissingMask,
            float sleepProgress01,
            float curlSideSign,
            float breathTicks)
        {
            if (rig == null || inoutLocalPoses == null || sleepProgress01 <= 1e-4f)
            {
                return;
            }

            GiantSleepCurlProfile profile = GetOrBuildProfile(rig);
            if (profile == null || profile.NodeCount <= 0)
            {
                return;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = Mathf.Min(nodes.Length, Mathf.Min(inoutLocalPoses.Length, profile.NodeCount));
            float sign = curlSideSign >= 0f ? 1f : -1f;
            float wCoil = SmoothStep01(Mathf.Clamp01((sleepProgress01 - 0.04f) / 0.96f));
            float breathSin = Mathf.Sin((2f * Mathf.PI * breathTicks) / DeepSleepBreathPeriodTicks) * wCoil;

            for (int i = 0; i < count; i++)
            {
                if (!profile.IsBezierChainBone[i])
                {
                    continue;
                }
                if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                {
                    inoutLocalPoses[i] = BoneLocalPose.Identity;
                    continue;
                }
                if (!partMissingMask.IsEmpty && i < partMissingMask.Length && partMissingMask[i] && nodes[i].hideWhenMissing)
                {
                    continue;
                }

                BoneLocalPose pose = inoutLocalPoses[i];
                if (profile.Roles[i] == GiantSleepBoneRole.Leg)
                {
                    pose.angle = Mathf.Lerp(pose.angle, 0f, wCoil);
                    inoutLocalPoses[i] = pose;
                    continue;
                }
                if (profile.Roles[i] == GiantSleepBoneRole.RigidOrPassive)
                {
                    pose.angle += Mathf.DeltaAngle(pose.angle, 0f) * wCoil;
                    inoutLocalPoses[i] = pose;
                    continue;
                }

                float signedTargetDeg = profile.AngleUsesCurlSign[i]
                    ? (profile.CurlAnglePlusSignDeg[i] * sign)
                    : profile.CurlAnglePlusSignDeg[i];
                float breathDeg = profile.BreathAngleAmplitudeDeg[i] * (profile.AngleUsesCurlSign[i] ? sign : 1f) * breathSin;
                float desiredAngle = signedTargetDeg + breathDeg;
                pose.angle = pose.angle + Mathf.DeltaAngle(pose.angle, desiredAngle) * wCoil;
                inoutLocalPoses[i] = pose;
            }
        }

        private static GiantSleepCurlProfile BuildProfile(GiantRigDef rig)
        {
            BoneNodeDef[] nodes = rig.SortedNodes ?? Array.Empty<BoneNodeDef>();
            int count = nodes.Length;
            if (count == 0)
            {
                return new GiantSleepCurlProfile(0);
            }

            var roles = new GiantSleepBoneRole[count];
            var isBezier = new bool[count];
            var curlAngle = new float[count];
            var angleUsesSign = new bool[count];
            var breathAmp = new float[count];
            var hasSag = new bool[count];
            var targetSag = new float[count];
            var hasFlap = new bool[count];
            var sideSign = new float[count];
            var flapInner = new float[count];
            var flapOuter = new float[count];

            if (rig.bezierChains != null)
            {
                for (int c = 0; c < rig.bezierChains.Count; c++)
                {
                    int[] indices = rig.bezierChains[c]?.resolvedBoneIndices;
                    if (indices == null) continue;
                    for (int k = 0; k < indices.Length; k++)
                    {
                        int bIdx = indices[k];
                        if ((uint)bIdx < (uint)count)
                        {
                            isBezier[bIdx] = true;
                        }
                    }
                }
            }

            var bindPoses = new BoneWorldPose[count];
            SkeletalKinematics.SolveFlatBindPoses(rig, bindPoses);
            Vector2 rootJointXZ = bindPoses[0].flatJointWorldXZ;

            // Pass 1: Topological semantic + geometric bone role classification
            for (int i = 0; i < count; i++)
            {
                BoneNodeDef n = nodes[i];
                int pIdx = n.parentRuntimeIndex;
                if (i == 0 || pIdx < 0)
                {
                    roles[i] = GiantSleepBoneRole.Root;
                    continue;
                }
                if (n.isRigid || n.isTurret)
                {
                    roles[i] = GiantSleepBoneRole.RigidOrPassive;
                    continue;
                }

                GiantSleepBoneRole pRole = roles[pIdx];
                string id = (n.nodeId ?? string.Empty).ToLowerInvariant();
                string part = (n.linkedBodyPart?.defName ?? string.Empty).ToLowerInvariant();

                if (pRole == GiantSleepBoneRole.Wing || id.Contains("wing") || part.Contains("wing"))
                {
                    roles[i] = GiantSleepBoneRole.Wing;
                    continue;
                }
                if (pRole == GiantSleepBoneRole.Tail || id.StartsWith("tail") || id.Contains("_tail") || part.Contains("tail"))
                {
                    roles[i] = GiantSleepBoneRole.Tail;
                    continue;
                }
                if (pRole == GiantSleepBoneRole.Leg || IsLegIdentifier(id, part))
                {
                    roles[i] = GiantSleepBoneRole.Leg;
                    continue;
                }
                if (IsPassiveAttachmentIdentifier(id, part))
                {
                    roles[i] = GiantSleepBoneRole.RigidOrPassive;
                    continue;
                }

                Vector2 relXZ = bindPoses[i].flatJointWorldXZ - rootJointXZ;
                bool nearSpineAxis = Mathf.Abs(relXZ.x) < 0.90f;

                if (IsAnteriorNeckHeadIdentifier(id, part) ||
                    (nearSpineAxis && relXZ.y > 0.30f && (pRole == GiantSleepBoneRole.Root || pRole == GiantSleepBoneRole.AnteriorNeckHead)))
                {
                    roles[i] = GiantSleepBoneRole.AnteriorNeckHead;
                    continue;
                }

                if (IsPosteriorTorsoIdentifier(id, part) ||
                    (nearSpineAxis && relXZ.y < -0.30f && (pRole == GiantSleepBoneRole.Root || pRole == GiantSleepBoneRole.PosteriorTorso)))
                {
                    roles[i] = GiantSleepBoneRole.PosteriorTorso;
                    continue;
                }

                roles[i] = GiantSleepBoneRole.RigidOrPassive;
            }

            // Compute first child index per bone within the same role for chain depth & distal direction
            var roleDepth = new int[count];
            var roleMaxSubtreeDepth = new int[count];
            var firstAnyChild = new int[count];
            Array.Fill(firstAnyChild, -1);
            for (int i = 1; i < count; i++)
            {
                int pIdx = nodes[i].parentRuntimeIndex;
                if (pIdx >= 0 && firstAnyChild[pIdx] < 0 && !nodes[i].isRigid)
                {
                    firstAnyChild[pIdx] = i;
                }
                if (pIdx >= 0 && roles[pIdx] == roles[i])
                {
                    roleDepth[i] = roleDepth[pIdx] + 1;
                }
            }
            for (int i = count - 1; i >= 1; i--)
            {
                int m = Mathf.Max(roleDepth[i], roleMaxSubtreeDepth[i]);
                roleMaxSubtreeDepth[i] = m;
                int pIdx = nodes[i].parentRuntimeIndex;
                if (pIdx >= 0 && roles[pIdx] == roles[i] && m > roleMaxSubtreeDepth[pIdx])
                {
                    roleMaxSubtreeDepth[pIdx] = m;
                }
            }

            // Pass 2: Anterior Neck + Head inward curl (+curlSign -> +X flank, curving down to form one arm of the C-gap)
            const float TotalAnteriorCurlDeg = 114f;
            for (int i = 1; i < count; i++)
            {
                if (roles[i] != GiantSleepBoneRole.AnteriorNeckHead) continue;
                int chainLen = roleMaxSubtreeDepth[i] + 1;
                int d = roleDepth[i];
                float u = chainLen > 1 ? d / (float)(chainLen - 1) : 0.5f;
                float weightBias = Mathf.Lerp(0.82f, 1.18f, u);
                float rawDeg = (TotalAnteriorCurlDeg / Mathf.Max(1, chainLen)) * weightBias;
                curlAngle[i] = Mathf.Clamp(rawDeg, 14f, 38f);
                angleUsesSign[i] = true;
                breathAmp[i] = 1.05f;
                hasSag[i] = nodes[i].sagittalDropCells > 1e-4f;
                targetSag[i] = -7.5f;
            }

            // Pass 3: Posterior Torso + Tail inward C-coil (-curlSign -> same +X flank, leaving a clean open C-gap opposite the dorsal arch)
            float posteriorTorsoSum = 0f;
            int posteriorCount = 0;
            int tailCount = 0;
            for (int i = 1; i < count; i++)
            {
                if (roles[i] == GiantSleepBoneRole.PosteriorTorso) posteriorCount++;
                else if (roles[i] == GiantSleepBoneRole.Tail) tailCount++;
            }
            if (posteriorCount > 0)
            {
                float targetPostSum = Mathf.Min(54f, posteriorCount * 18f);
                for (int i = 1; i < count; i++)
                {
                    if (roles[i] != GiantSleepBoneRole.PosteriorTorso) continue;
                    int chainLen = roleMaxSubtreeDepth[i] + 1;
                    int d = roleDepth[i];
                    float u = chainLen > 1 ? d / (float)(chainLen - 1) : 0.5f;
                    float deg = Mathf.Clamp((targetPostSum / Mathf.Max(1, chainLen)) * Mathf.Lerp(0.88f, 1.12f, u), 8f, 19f);
                    curlAngle[i] = -deg;
                    angleUsesSign[i] = true;
                    breathAmp[i] = -0.75f;
                    posteriorTorsoSum += deg;
                }
            }

            const float TargetPosteriorAndTailWrapDeg = 192f;
            float remainingTailWrapDeg = Mathf.Max(90f, TargetPosteriorAndTailWrapDeg - posteriorTorsoSum);
            float actualTailSum = 0f;
            for (int i = 1; i < count; i++)
            {
                if (roles[i] != GiantSleepBoneRole.Tail) continue;
                int chainLen = roleMaxSubtreeDepth[i] + 1;
                int d = roleDepth[i];
                float u = chainLen > 1 ? d / (float)(chainLen - 1) : 0.5f;
                float weightBias = Mathf.Lerp(0.88f, 1.12f, u);
                float deg = Mathf.Clamp((remainingTailWrapDeg / Mathf.Max(1, chainLen)) * weightBias, 9f, 25f);
                curlAngle[i] = -deg;
                angleUsesSign[i] = true;
                breathAmp[i] = -0.90f;
                actualTailSum += deg;
            }
            // Normalize tail wrap so multi-segment tails consistently hit ~-192 deg C-ring wrap (leaving a clear ~54 deg bottom C-gap)
            if (tailCount >= 3 && actualTailSum > 1e-3f)
            {
                float desiredTailSum = Mathf.Max(125f, TargetPosteriorAndTailWrapDeg - posteriorTorsoSum);
                float scale = Mathf.Clamp(desiredTailSum / actualTailSum, 0.78f, 1.22f);
                for (int i = 1; i < count; i++)
                {
                    if (roles[i] == GiantSleepBoneRole.Tail)
                    {
                        curlAngle[i] = Mathf.Clamp(curlAngle[i] * scale, -27f, -8f);
                    }
                }
            }

            // Detect which wing bones participate in a 2D multi-bone skinned mesh so LBS axilla webs stay crease-free
            var isSkinnedWingBone = new bool[count];
            if (rig.skinnedMeshes != null)
            {
                var idToIdx = new Dictionary<string, int>(count, StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    if (!string.IsNullOrEmpty(nodes[i].nodeId))
                    {
                        idToIdx[nodes[i].nodeId] = i;
                    }
                }
                for (int m = 0; m < rig.skinnedMeshes.Count; m++)
                {
                    GiantSkinnedMeshDef sm = rig.skinnedMeshes[m];
                    if (sm == null) continue;
                    if (sm.memberBoneIds != null)
                    {
                        for (int k = 0; k < sm.memberBoneIds.Count; k++)
                        {
                            if (sm.memberBoneIds[k] != null && idToIdx.TryGetValue(sm.memberBoneIds[k], out int idx))
                            {
                                isSkinnedWingBone[idx] = true;
                            }
                        }
                    }
                    if (sm.capsules != null)
                    {
                        for (int k = 0; k < sm.capsules.Count; k++)
                        {
                            string bid = sm.capsules[k]?.boneId;
                            if (bid != null && idToIdx.TryGetValue(bid, out int idx) && roles[idx] == GiantSleepBoneRole.Wing)
                            {
                                isSkinnedWingBone[idx] = true;
                            }
                        }
                    }
                }
            }

            // Pass 4: Wings (aligned symmetric dorsal fold + coronal foreshortening) & Legs (sagittal-only tuck, 0 deg yaw)
            var isArticulatedFoldWingChain = new bool[count];
            for (int i = 1; i < count; i++)
            {
                if (roles[i] != GiantSleepBoneRole.Wing || isSkinnedWingBone[i]) continue;
                int pIdx = nodes[i].parentRuntimeIndex;
                if (roleDepth[i] == 1 && Mathf.Abs(nodes[i].baseAngle) >= 20f && pIdx >= 0)
                {
                    isArticulatedFoldWingChain[pIdx] = true;
                    isArticulatedFoldWingChain[i] = true;
                }
                else if (pIdx >= 0 && isArticulatedFoldWingChain[pIdx])
                {
                    isArticulatedFoldWingChain[i] = true;
                }
            }

            var accumWingSweep = new float[count];
            for (int i = 1; i < count; i++)
            {
                BoneNodeDef n = nodes[i];
                int pIdx = n.parentRuntimeIndex;
                GiantSleepBoneRole role = roles[i];

                if (role == GiantSleepBoneRole.Wing)
                {
                    float sSign = bindPoses[i].flatQuadCenterWorldXZ.x < -0.02f
                        ? -1f
                        : (bindPoses[i].flatQuadCenterWorldXZ.x > 0.02f
                            ? 1f
                            : (pIdx >= 0 ? sideSign[pIdx] : 1f));
                    sideSign[i] = sSign;

                    float parentSweep = (pIdx >= 0 && roles[pIdx] == GiantSleepBoneRole.Wing) ? accumWingSweep[pIdx] : 0f;
                    Vector2 jointXZ = bindPoses[i].flatJointWorldXZ;
                    Vector2 distalXZ = firstAnyChild[i] >= 0
                        ? bindPoses[firstAnyChild[i]].flatJointWorldXZ
                        : (bindPoses[i].flatQuadCenterWorldXZ + (bindPoses[i].flatQuadCenterWorldXZ - jointXZ));
                    Vector2 segVec = distalXZ - jointXZ;

                    float localSweep = 0f;
                    bool skinnedWing = isSkinnedWingBone[i];
                    bool isFusedCollinearWingSubBone = !skinnedWing && roleDepth[i] >= 2 && Mathf.Abs(n.baseAngle) <= 0.5f;
                    if (isFusedCollinearWingSubBone)
                    {
                        // Fused outer sub-bone of a 2-segment wing (e.g. wing_*_pinion at 0 deg on wing_*_fore):
                        // strictly 0 deg relative curl & 0 deg breathing so the continuous outer wing never splits.
                        localSweep = 0f;
                    }
                    else if (isArticulatedFoldWingChain[i])
                    {
                        // Preserve clean 2-segment wrist-fold wing posture without reverse-pinion wrenching or seam splitting
                        if (roleDepth[i] == 0)
                        {
                            float bindWorldDeg = Mathf.DeltaAngle(0f, bindPoses[i].worldAngleDeg);
                            float targetArmWorldDeg = sSign < 0f ? -106f : 106f;
                            localSweep = Mathf.Clamp(Mathf.DeltaAngle(bindWorldDeg + parentSweep, targetArmWorldDeg), -18f, 18f);
                        }
                        else if (roleDepth[i] == 1)
                        {
                            // Keep wrist folded caudolaterally inside safe anti-reverse-pinion clearance (~-144 deg world)
                            localSweep = -sSign * 4f;
                        }
                    }
                    else if (segVec.sqrMagnitude > 0.04f)
                    {
                        float bindDirDeg = Mathf.Atan2(segVec.x, segVec.y) * Mathf.Rad2Deg;
                        float aftFlankDeg = sSign < 0f ? -168f : 168f;
                        float remainToAft = Mathf.DeltaAngle(bindDirDeg + parentSweep, aftFlankDeg);
                        if (skinnedWing)
                        {
                            // Sweeps both wings cleanly aftward along the dorsal arch while keeping LBS axilla crease-free
                            float gain = roleDepth[i] == 0 ? 0.24f : (roleDepth[i] == 1 ? 0.28f : 0.22f);
                            localSweep = Mathf.Clamp(remainToAft * gain, -18f, 18f);
                        }
                        else
                        {
                            float gain = roleDepth[i] == 0 ? 0.48f : (roleDepth[i] == 1 ? 0.54f : 0.46f);
                            localSweep = Mathf.Clamp(remainToAft * gain, -32f, 32f);
                        }
                    }

                    curlAngle[i] = localSweep;
                    angleUsesSign[i] = false;
                    breathAmp[i] = isFusedCollinearWingSubBone ? 0f : (-sSign * 0.55f);
                    accumWingSweep[i] = parentSweep + localSweep;

                    if (n.isCoronalFlapHinge)
                    {
                        hasFlap[i] = true;
                        // Symmetric tight dorsal tuck on skinned wings prevents the ventral-side wingtip from poking across
                        // the snout/gap and prevents the dorsal-arch wing from fanning wide outward.
                        float innerBase = skinnedWing ? 75f : 72f;
                        float outerBase = skinnedWing ? 73f : 68f;
                        flapInner[i] = Mathf.Max(n.baseCoronalFlap + 18f, innerBase);
                        flapOuter[i] = Mathf.Max(n.baseCoronalFlap + 16f, outerBase);
                    }
                }
                else if (role == GiantSleepBoneRole.Leg)
                {
                    // Zero horizontal rotation strictly enforced
                    curlAngle[i] = 0f;
                    angleUsesSign[i] = false;
                    breathAmp[i] = 0f;

                    float sSign = bindPoses[i].flatQuadCenterWorldXZ.x < 0f ? -1f : 1f;
                    sideSign[i] = sSign;

                    Vector2 jointXZ = bindPoses[i].flatJointWorldXZ;
                    Vector2 distalXZ = firstAnyChild[i] >= 0
                        ? bindPoses[firstAnyChild[i]].flatJointWorldXZ
                        : bindPoses[i].flatQuadCenterWorldXZ;
                    bool extendsBackward = (distalXZ.y - jointXZ.y) < -0.05f;
                    int d = roleDepth[i];

                    if (n.sagittalDropCells > 1e-4f)
                    {
                        hasSag[i] = true;
                        if (extendsBackward)
                        {
                            targetSag[i] = d == 0 ? 22f : (d == 1 ? 30f : 15f);
                        }
                        else
                        {
                            targetSag[i] = d == 0 ? -34f : (d == 1 ? -30f : -22f);
                        }
                    }

                    if (n.isCoronalFlapHinge)
                    {
                        hasFlap[i] = true;
                        flapInner[i] = Mathf.Max(n.baseCoronalFlap + 12f, 60f);
                        flapOuter[i] = flapInner[i];
                    }
                }
            }

            // Pass 5: Measure coiled ring bounding-box midpoint, compute root centering offset,
            // and solve exact TargetHeadingPlusSignDeg / TargetHeadingMinusSignDeg so the dorsal body arch
            // points strictly North (+Z, 12 o'clock) and the C-ring gap points strictly South (-Z, 6 o'clock).
            var roleChainLength = new int[count];
            var bindRelZ = new float[count];
            for (int i = 0; i < count; i++)
            {
                roleChainLength[i] = roleMaxSubtreeDepth[i] + 1;
                bindRelZ[i] = bindPoses[i].flatJointWorldXZ.y - rootJointXZ.y;
            }

            var tempProfile = new GiantSleepCurlProfile(
                count, roles, isBezier, roleDepth, roleChainLength, bindRelZ, curlAngle, angleUsesSign, breathAmp,
                hasSag, targetSag, hasFlap, sideSign, flapInner, flapOuter, Vector2.zero);

            var sampleLocal = new BoneLocalPose[count];
            for (int i = 0; i < count; i++)
            {
                sampleLocal[i] = BoneLocalPose.Identity;
            }
            ApplySleepCurlWithProfile(tempProfile, rig, sampleLocal, ReadOnlySpan<bool>.Empty, 1f, 1f, 0f, includeBezierBones: true);

            var curledWorld = new BoneWorldPose[count];
            SkeletalKinematics.SolveWorldPoses(rig, Vector2.zero, 0f, sampleLocal, ReadOnlySpan<bool>.Empty, curledWorld, 1f);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            int coreSamples = 0;
            for (int i = 0; i < count; i++)
            {
                GiantSleepBoneRole r = roles[i];
                if (r is GiantSleepBoneRole.Root or GiantSleepBoneRole.AnteriorNeckHead or GiantSleepBoneRole.PosteriorTorso or GiantSleepBoneRole.Tail)
                {
                    Vector2 j = curledWorld[i].jointWorldXZ;
                    if (j.x < minX) minX = j.x;
                    if (j.x > maxX) maxX = j.x;
                    if (j.y < minZ) minZ = j.y;
                    if (j.y > maxZ) maxZ = j.y;
                    coreSamples++;
                }
            }

            Vector2 centerOffset = Vector2.zero;
            if (coreSamples >= 2)
            {
                Vector2 midXZ = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
                centerOffset = Vector2.ClampMagnitude(-midXZ * 0.85f, 3.6f);
            }

            // Re-evaluate centered curled world poses at heading=0, sign=+1 to determine exact dorsal-arch -> C-gap axis
            var centeredProfile = new GiantSleepCurlProfile(
                count, roles, isBezier, roleDepth, roleChainLength, bindRelZ, curlAngle, angleUsesSign, breathAmp,
                hasSag, targetSag, hasFlap, sideSign, flapInner, flapOuter, centerOffset);
            for (int i = 0; i < count; i++)
            {
                sampleLocal[i] = BoneLocalPose.Identity;
            }
            ApplySleepCurlWithProfile(centeredProfile, rig, sampleLocal, ReadOnlySpan<bool>.Empty, 1f, 1f, 0f, includeBezierBones: true);
            SkeletalKinematics.SolveWorldPoses(rig, Vector2.zero, 0f, sampleLocal, ReadOnlySpan<bool>.Empty, curledWorld, 1f);

            Vector2 dorsalSum = Vector2.zero;
            int dorsalCount = 0;
            int distalHeadIdx = -1;
            int distalTailIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (roles[i] == GiantSleepBoneRole.Root || roles[i] == GiantSleepBoneRole.PosteriorTorso)
                {
                    dorsalSum += curledWorld[i].jointWorldXZ;
                    dorsalCount++;
                }
                else if (roles[i] == GiantSleepBoneRole.AnteriorNeckHead)
                {
                    if (roleDepth[i] == 0)
                    {
                        dorsalSum += curledWorld[i].jointWorldXZ;
                        dorsalCount++;
                    }
                    distalHeadIdx = i;
                }
                else if (roles[i] == GiantSleepBoneRole.Tail)
                {
                    distalTailIdx = i;
                }
            }

            float targetHeadingPlus = 90f;
            float targetHeadingMinus = 270f;
            if (dorsalCount > 0 && distalHeadIdx >= 0 && distalTailIdx >= 0)
            {
                Vector2 dorsalArchXZ = dorsalSum / dorsalCount;
                Vector2 snoutEndXZ = curledWorld[distalHeadIdx].quadCenterWorldXZ;
                Vector2 tailEndXZ = curledWorld[distalTailIdx].quadCenterWorldXZ;
                Vector2 gapMidXZ = 0.5f * (snoutEndXZ + tailEndXZ);
                Vector2 bodyToGapXZ = gapMidXZ - dorsalArchXZ;
                if (bodyToGapXZ.sqrMagnitude > 0.04f)
                {
                    float gapBearingAtZeroDeg = Mathf.Atan2(bodyToGapXZ.x, bodyToGapXZ.y) * Mathf.Rad2Deg;
                    // RotateCW(H) shifts bearing by +H; setting gapBearingAtZeroDeg + H == 180 deg points the gap due South (-Z)
                    // and the dorsal body arch due North (+Z).
                    targetHeadingPlus = SkeletalKinematics.NormalizeAngle360(180f - gapBearingAtZeroDeg);
                    targetHeadingMinus = SkeletalKinematics.NormalizeAngle360(180f + gapBearingAtZeroDeg);
                }
            }

            return new GiantSleepCurlProfile(
                count, roles, isBezier, roleDepth, roleChainLength, bindRelZ, curlAngle, angleUsesSign, breathAmp,
                hasSag, targetSag, hasFlap, sideSign, flapInner, flapOuter, centerOffset,
                targetHeadingPlus, targetHeadingMinus);
        }

        private static bool IsLegIdentifier(string id, string part)
        {
            return id.StartsWith("leg_", StringComparison.Ordinal)
                || id.Contains("_leg")
                || id.Contains("thigh")
                || id.Contains("femur")
                || id.Contains("shin")
                || id.Contains("tibia")
                || id.Contains("ankle")
                || id.Contains("hind_paw")
                || id.Contains("fore_paw")
                || id.EndsWith("_paw", StringComparison.Ordinal)
                || id == "paw"
                || id.Contains("hoof")
                || part.Contains("leg")
                || part.Contains("femur")
                || part.Contains("tibia")
                || part.Contains("hindtalon")
                || part.Contains("siegepaw");
        }

        private static bool IsPassiveAttachmentIdentifier(string id, string part)
        {
            return id.Contains("jaw")
                || id.Contains("mandible")
                || id.Contains("chin")
                || id.Contains("beak")
                || id.Contains("horn")
                || id.Contains("antler")
                || id.Contains("ear")
                || id.Contains("crest")
                || id.Contains("whip")
                || id.Contains("tentacle")
                || id.Contains("turret")
                || id.Contains("cannon")
                || id.Contains("pod")
                || part.Contains("jaw")
                || part.Contains("mandible")
                || part.Contains("horn")
                || part.Contains("whip");
        }

        private static bool IsAnteriorNeckHeadIdentifier(string id, string part)
        {
            return id.StartsWith("neck", StringComparison.Ordinal)
                || id.Contains("_neck")
                || id == "skull"
                || id.EndsWith("_skull", StringComparison.Ordinal)
                || id == "head"
                || id.EndsWith("_head", StringComparison.Ordinal)
                || id.Contains("snout")
                || part.Contains("neck")
                || part.Contains("cephalon")
                || part.Contains("skull");
        }

        private static bool IsPosteriorTorsoIdentifier(string id, string part)
        {
            return id.Contains("rear")
                || id.Contains("lumbar")
                || id.Contains("abdomen")
                || id.Contains("pelvis")
                || id.Contains("sacrum")
                || id.Contains("spine")
                || id.Contains("waist")
                || part.Contains("abdomen")
                || part.Contains("pelvis");
        }
    }
}
