using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public class SoftWhipDef
    {
        public string whipId;
        public string texPath;
        public string rootBoneId;

        /// <summary>
        /// Normalized [0..1, 0..1] exit socket on the root bone's UV quad (0,0 = bottom-left, 0.5,1 = top-center).
        /// Overridden during ResolveCoordinates if exitPixel is provided.
        /// </summary>
        public Vector2 exitUV = new Vector2(0.5f, 0.78125f);
        public Vector2 exitPixel = new Vector2(-9999f, -9999f);

        // Optional linked anatomical body part representing the soft whip tendril itself
        // (resolved without placing it in allLinkedBodyPartSet so hits onto rootBoneId roll down into it).
        public BodyPartDef tendrilBodyPart;
        public string tendrilBodyPartCustomLabel;

        // Torso ellipse collision anchor (thorax-local XZ)
        public string torsoBoneId = "abyssal_thorax";
        public Vector2 colliderCenterLocal = new Vector2(0f, -0.40f);
        public Vector2 colliderRadii = new Vector2(2.05f, 3.45f);

        // Soft continuum discretization & mass profile: m[0] = inf, m[i] = (1 - massTaper*(i/N))^2 + minEndMass
        public int particleCount = 29;
        public float totalLength = 14.3f;
        public float baseWidth = 1.15f;
        public float tipWidthFactor = 0.84f;
        public float harpoonWidthBoost = 0.34f;
        public float layerOffset = 0.76f;
        public int substeps = 6;
        public int constraintIterations = 3;
        public float massTaper = 0.80f;
        public float minEndMass = 0.05f;

        // Lateral orientation (-1 left, +1 right)
        public float lateralSideSign = -1f;

        // Anisotropic damping
        public float restNormalDrag = 0.16f;
        public float fastNormalDrag = 0.025f;
        public float postSnapNormalDrag = 0.23f;
        public float tangentialDrag = 0.014f;
        public float strikeDragSpeedRef = 0.40f;

        // Graduated proximal muscular belly coupling near docking socket (decays strictly to zero before distal whip)
        public float restProximalCutoff = 2.20f;
        public float restProximalGain = 0.75f;
        public float strikeProximalCutoff = 1.42f;
        public float strikeProximalGain = 2.80f;

        // Bullwhip collar drive parameters during strike animations
        public List<string> strikeAnimDefs = new List<string>();
        public float windupEndNorm = 0.27f;
        public float snapEndNorm = 0.45f;
        public float checkBackEndNorm = 0.58f;
        public float defaultStrikeAzimuthDeg = 18f;
        public float defaultStrikeRangeFactor = 0.78f;

        // Hitbox & footprint participation
        public bool contributesToHitbox = true;
        public bool contributesToCollision = false;
        public byte alphaThreshold = 32;

        [Unsaved]
        public int resolvedRootBoneIndex = -1;

        [Unsaved]
        public int resolvedTorsoBoneIndex = -1;

        [Unsaved]
        public Vector2 resolvedExitUV = new Vector2(0.5f, 0.78125f);

        [Unsaved]
        public int runtimeIndex = -1;

        public void ResolveCoordinates(int rootTexRes)
        {
            if (exitPixel.x > -9000f)
            {
                float res = Mathf.Max(1f, rootTexRes);
                resolvedExitUV = new Vector2(exitPixel.x / res, 1f - (exitPixel.y / res));
            }
            else
            {
                resolvedExitUV = exitUV;
            }
        }

        public bool IsStrikeAnim(string animDefName)
        {
            if (string.IsNullOrEmpty(animDefName) || strikeAnimDefs == null)
            {
                return false;
            }
            for (int i = 0; i < strikeAnimDefs.Count; i++)
            {
                if (string.Equals(strikeAnimDefs[i], animDefName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public sealed class SoftWhipRibbonSnapshot
    {
        public bool visible;
        public Vector2 rootWorldXZ;
        public Vector2[] stripLeftLocalXZ;
        public Vector2[] stripRightLocalXZ;
        public Vector2[] centerlineWorldXZ;
        public float[] vCoords;
        public Vector2[] physicsParticlesWorldXZ;
        public float tipSpeedCellsPerTick;
        public float settleEnergy;
    }

    public sealed class SoftWhipRuntime
    {
        private readonly SoftWhipDef def;
        private readonly int particleCount;
        private readonly int stripSampleCount;

        private readonly Vector2[] pos;
        private readonly Vector2[] prevPos;
        private readonly float[] invMass;
        private readonly float[] strikeAlphaTildeCells;
        private readonly float[] lambdaBend;

        private readonly Vector2[] curvePts;
        private readonly Vector2[] stripLeftWorldXZ;
        private readonly Vector2[] stripRightWorldXZ;
        private readonly Vector2[] stripLeftLocalXZ;
        private readonly Vector2[] stripRightLocalXZ;
        private readonly float[] vCoords;
        private readonly float[] baseHalfWidths;

        private BoneWorldPose[] tempWorldPoses = Array.Empty<BoneWorldPose>();
        private bool initialized;
        private Vector2 prevExitXZ;
        private Vector2 prevExitDir;
        private Vector2 prevRootWorldXZ;
        private float prevTorsoAngleDeg;
        private float prevStrikeNorm;
        private bool wasStrikePrev;

        // Continuum strike & bilateral impact/exhaustion state
        private bool strikeIsQuake;
        private bool partnerWhipAvailable;
        private float strikeTargetForwardCells;
        private float strikeTargetAzimuthDeg;
        private bool strikeImpactLatched;
        private bool strikeReboundWaveFired;
        private float strikeImpactNormTime;
        private int strikeImpactNode = 21;
        private float strikeClosingSpeedSaved = 6.0f;
        private float strikeHitCollarMag = -25f;
        private float liveCollarMagDeg;

        private float lastTipSpeed;
        private float lastSubTerminalSpeed;
        private float lastSettleEnergy;

        /// <summary>
        /// Optional hint set by CompGiantRig indicating whether any sibling soft whip on the same rig has an intact tendril.
        /// Defaults to true when not overridden.
        /// </summary>
        public bool PartnerTendrilIntact { get; set; } = true;

        public SoftWhipDef Def => def;
        public bool IsInitialized => initialized;
        public bool IsSettling => initialized && lastSettleEnergy > 0.002f;
        public float TipSpeedCellsPerTick => lastTipSpeed;
        public float SubTerminalSpeedCellsPerTick => lastSubTerminalSpeed;
        public bool HasLatchedStrikeImpact => strikeImpactLatched;
        public ReadOnlySpan<Vector2> PhysicsParticlesWorldXZ => pos;
        public ReadOnlySpan<Vector2> CenterlineWorldXZ => curvePts;
        public ReadOnlySpan<Vector2> StripLeftLocalXZ => stripLeftLocalXZ;
        public ReadOnlySpan<Vector2> StripRightLocalXZ => stripRightLocalXZ;
        public ReadOnlySpan<float> VCoords => vCoords;

        public SoftWhipRuntime(SoftWhipDef def)
        {
            this.def = def ?? throw new ArgumentNullException(nameof(def));
            particleCount = Mathf.Clamp(def.particleCount, 8, 96);
            stripSampleCount = (particleCount - 1) * 2 + 1;

            pos = new Vector2[particleCount];
            prevPos = new Vector2[particleCount];
            invMass = new float[particleCount];
            strikeAlphaTildeCells = new float[particleCount];
            lambdaBend = new float[particleCount];

            curvePts = new Vector2[stripSampleCount];
            stripLeftWorldXZ = new Vector2[stripSampleCount];
            stripRightWorldXZ = new Vector2[stripSampleCount];
            stripLeftLocalXZ = new Vector2[stripSampleCount];
            stripRightLocalXZ = new Vector2[stripSampleCount];
            vCoords = new float[stripSampleCount];
            baseHalfWidths = new float[stripSampleCount];

            // Exact dimensional scaling from validated continuum rod solver (segLen ratio 9.8^2 = 96.04).
            // Pure Goriely bullwhip mass taper + monotonically increasing XPBD compliance all the way to s = 1.0:
            // ZERO front-end rigid hook, ZERO distal mass club -- the tip is the softest, most supple part of the whip.
            const float continuumScaleSq = 96.04f;
            invMass[0] = 0f; // Root pinned to docking collar exit socket
            int segCount = particleCount - 1;
            strikeImpactNode = Mathf.Clamp(Mathf.RoundToInt(segCount * 0.75f), 1, segCount);
            for (int i = 1; i < particleCount; i++)
            {
                float s = i / (float)segCount;
                float r = 0.90f * Mathf.Pow(1f - s, 1.30f) + 0.14f;
                invMass[i] = 1f / (r * r);

                float ramp = SmoothRamp(0.10f, 0.98f, s);
                strikeAlphaTildeCells[i] = continuumScaleSq * (0.014f + 2.25f * Mathf.Pow(ramp, 1.85f));
            }

            for (int j = 0; j < stripSampleCount; j++)
            {
                float t = j / (float)(stripSampleCount - 1);
                vCoords[j] = t;
                // Smooth continuous organic taper from muscular root to supple soft tip (no rigid bulb discontinuity)
                float widthScale = Mathf.Lerp(1.04f, Mathf.Min(def.tipWidthFactor, 0.72f), Mathf.Pow(t, 0.88f));
                baseHalfWidths[j] = 0.5f * def.baseWidth * widthScale;
            }
        }

        public void Reset()
        {
            initialized = false;
            wasStrikePrev = false;
            prevStrikeNorm = 0f;
            prevTorsoAngleDeg = 0f;
            prevRootWorldXZ = Vector2.zero;
            strikeIsQuake = false;
            partnerWhipAvailable = false;
            strikeTargetForwardCells = 10.6f;
            strikeTargetAzimuthDeg = 0f;
            strikeImpactLatched = false;
            strikeReboundWaveFired = false;
            strikeImpactNormTime = -1f;
            strikeImpactNode = Mathf.Clamp(Mathf.RoundToInt((particleCount - 1) * 0.75f), 1, particleCount - 1);
            strikeClosingSpeedSaved = 6.0f;
            strikeHitCollarMag = -25f;
            liveCollarMagDeg = 146f;
            lastTipSpeed = 0f;
            lastSubTerminalSpeed = 0f;
            lastSettleEnergy = 0f;
            Array.Clear(pos, 0, pos.Length);
            Array.Clear(prevPos, 0, prevPos.Length);
            Array.Clear(lambdaBend, 0, lambdaBend.Length);
            Array.Clear(curvePts, 0, curvePts.Length);
            Array.Clear(stripLeftLocalXZ, 0, stripLeftLocalXZ.Length);
            Array.Clear(stripRightLocalXZ, 0, stripRightLocalXZ.Length);
        }

        public void AddWorldImpulse(
            Vector2 worldVelocityDeltaCellsPerTick,
            float centerNormalizedU = 0.52f,
            float spread = 0.36f)
        {
            if (!initialized || particleCount <= 1 || worldVelocityDeltaCellsPerTick.sqrMagnitude < 1e-8f)
            {
                return;
            }
            const float maxDisp = 0.38f;
            float mag = worldVelocityDeltaCellsPerTick.magnitude;
            Vector2 disp = mag > maxDisp
                ? worldVelocityDeltaCellsPerTick * (maxDisp / mag)
                : worldVelocityDeltaCellsPerTick;

            float inv2Sig2 = 1f / Mathf.Max(0.02f, 2f * spread * spread);
            int segCount = particleCount - 1;
            for (int i = 1; i < particleCount; i++)
            {
                float u = i / (float)segCount;
                float du = u - centerNormalizedU;
                float weight = Mathf.Exp(-du * du * inv2Sig2) * Mathf.Clamp01(u * 3.5f);
                prevPos[i] -= disp * weight;
            }
            lastSettleEnergy = Mathf.Max(lastSettleEnergy, 0.05f);
        }

        public bool Step(
            GiantRigDef rig,
            Vector2 rootWorldXZ,
            float bodyHeadingDeg,
            BoneLocalPose[] inOutLocalPoses,
            ReadOnlySpan<bool> partMissingMask,
            bool tendrilMissing,
            float deltaTicks,
            in BezierChainStepContext ctx)
        {
            if (rig == null || inOutLocalPoses == null)
            {
                return false;
            }

            int rootIdx = def.resolvedRootBoneIndex;
            if (rootIdx < 0 || rootIdx >= rig.SortedNodes.Length || rootIdx >= inOutLocalPoses.Length)
            {
                return false;
            }

            if ((uint)rootIdx < (uint)partMissingMask.Length && partMissingMask[rootIdx])
            {
                Reset();
                return false;
            }
            if (tendrilMissing)
            {
                Reset();
                return false;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            if (tempWorldPoses.Length != nodes.Length)
            {
                tempWorldPoses = new BoneWorldPose[nodes.Length];
            }

            int torsoIdx = def.resolvedTorsoBoneIndex >= 0 && def.resolvedTorsoBoneIndex < nodes.Length
                ? def.resolvedTorsoBoneIndex
                : nodes[rootIdx].parentRuntimeIndex;
            int maxSolveIdx = Mathf.Max(rootIdx, torsoIdx >= 0 ? torsoIdx : 0);

            SkeletalKinematics.SolveWorldPoses(
                rig,
                rootWorldXZ,
                bodyHeadingDeg,
                inOutLocalPoses,
                partMissingMask,
                tempWorldPoses,
                1f,
                default,
                maxSolveIdx);
            if (!tempWorldPoses[rootIdx].visible)
            {
                Reset();
                return false;
            }

            Vector2 torsoCenterWorldXZ = rootWorldXZ;
            float torsoAngleDeg = bodyHeadingDeg;
            if (torsoIdx >= 0 && torsoIdx < tempWorldPoses.Length && tempWorldPoses[torsoIdx].visible)
            {
                torsoCenterWorldXZ = tempWorldPoses[torsoIdx].jointWorldXZ;
                torsoAngleDeg = tempWorldPoses[torsoIdx].worldAngleDeg;
            }
            torsoCenterWorldXZ += SkeletalKinematics.RotateCW(def.colliderCenterLocal, torsoAngleDeg);

            bool isStrike = def.IsStrikeAnim(ctx.ActiveClipDefName);
            float uNorm = Mathf.Clamp01(ctx.ActiveClipNormalizedTime);

            if (isStrike)
            {
                if (!wasStrikePrev || uNorm < prevStrikeNorm - 0.2f)
                {
                    strikeImpactLatched = false;
                    strikeReboundWaveFired = false;
                    strikeImpactNormTime = -1f;
                }

                strikeIsQuake = !string.IsNullOrEmpty(ctx.ActiveClipDefName) &&
                    ctx.ActiveClipDefName.IndexOf("QuakeSlam", StringComparison.OrdinalIgnoreCase) >= 0;
                partnerWhipAvailable = ResolvePartnerWhipIntact(rig, partMissingMask);
                ResolveStrikeTargetGeometry(rootWorldXZ, bodyHeadingDeg, tempWorldPoses[rootIdx].jointWorldXZ, torsoCenterWorldXZ, torsoAngleDeg, strikeIsQuake, in ctx);

                if (!initialized || deltaTicks > 1e-5f)
                {
                    liveCollarMagDeg = EvalStrikeCollarMag(uNorm);
                }
                DriveStrikeCollarPose(nodes, rootIdx, inOutLocalPoses, uNorm, liveCollarMagDeg);
                SkeletalKinematics.SolveWorldPoses(
                    rig,
                    rootWorldXZ,
                    bodyHeadingDeg,
                    inOutLocalPoses,
                    partMissingMask,
                    tempWorldPoses,
                    1f,
                    default,
                    maxSolveIdx);

                torsoCenterWorldXZ = rootWorldXZ;
                torsoAngleDeg = bodyHeadingDeg;
                if (torsoIdx >= 0 && torsoIdx < tempWorldPoses.Length && tempWorldPoses[torsoIdx].visible)
                {
                    torsoCenterWorldXZ = tempWorldPoses[torsoIdx].jointWorldXZ;
                    torsoAngleDeg = tempWorldPoses[torsoIdx].worldAngleDeg;
                }
                torsoCenterWorldXZ += SkeletalKinematics.RotateCW(def.colliderCenterLocal, torsoAngleDeg);
            }
            else
            {
                wasStrikePrev = false;
                strikeImpactLatched = false;
                strikeReboundWaveFired = false;
                strikeImpactNormTime = -1f;
            }

            ref readonly BoneWorldPose rootPose = ref tempWorldPoses[rootIdx];
            Vector2 exitXZ = rootPose.UVToWorldXZ(def.resolvedExitUV.x, def.resolvedExitUV.y);
            Vector2 exitDir = SkeletalKinematics.RotateCW(Vector2.up, rootPose.worldAngleDeg);
            if (exitDir.sqrMagnitude < 1e-6f)
            {
                exitDir = Vector2.down;
            }
            else
            {
                exitDir.Normalize();
            }

            int segCount = particleCount - 1;
            float segLen = Mathf.Max(0.05f, def.totalLength / segCount);
            float sleepCurl01 = !isStrike ? Mathf.Clamp01(ctx.SleepCurlProgress01) : 0f;
            float sleepCurlSideSign = ctx.SleepCurlSideSign >= 0f ? 1f : -1f;
            bool isDeadOrDowned = !isStrike && ctx.IsDeadOrDowned;

            bool teleportedOrSnapped = initialized && (
                (exitXZ - prevExitXZ).sqrMagnitude > 36f ||
                (deltaTicks <= 1e-4f && !isStrike && Mathf.Abs(Mathf.DeltaAngle(prevTorsoAngleDeg, torsoAngleDeg)) > 65f));

            if (!initialized || teleportedOrSnapped)
            {
                InitNaturalPosterolateralDrape(exitXZ, segLen, torsoCenterWorldXZ, torsoAngleDeg, sleepCurl01, sleepCurlSideSign, isDeadOrDowned);
                prevExitXZ = exitXZ;
                prevExitDir = exitDir;
                prevRootWorldXZ = rootWorldXZ;
                prevTorsoAngleDeg = torsoAngleDeg;
                initialized = true;
                lastTipSpeed = 0f;
                lastSubTerminalSpeed = 0f;
                lastSettleEnergy = 0f;
                if (isStrike)
                {
                    prevStrikeNorm = uNorm;
                    wasStrikePrev = true;
                }
            }
            else
            {
                float maxAllowedDt = isStrike ? 16f : 3.5f;
                float dt = Mathf.Clamp(deltaTicks, 0f, maxAllowedDt);
                if (dt > 1e-5f)
                {
                    if (isStrike && (!wasStrikePrev || uNorm < prevStrikeNorm - 0.2f))
                    {
                        prevStrikeNorm = Mathf.Max(0f, uNorm - dt / 46f);
                    }
                    int macroSlices = (isStrike && dt > 1.05f) ? Mathf.Clamp(Mathf.CeilToInt(dt), 1, 16) : 1;
                    if (macroSlices <= 1)
                    {
                        StepVerletPhysics(
                            dt,
                            rootWorldXZ,
                            exitXZ,
                            exitDir,
                            segLen,
                            torsoCenterWorldXZ,
                            torsoAngleDeg,
                            isStrike,
                            prevStrikeNorm,
                            uNorm,
                            sleepCurl01,
                            sleepCurlSideSign,
                            isDeadOrDowned);
                    }
                    else
                    {
                        Vector2 rStart = prevRootWorldXZ;
                        Vector2 eStart = prevExitXZ;
                        Vector2 dStart = prevExitDir;
                        float angStart = prevTorsoAngleDeg;
                        float uStart = prevStrikeNorm;
                        float sliceDt = dt / macroSlices;
                        for (int m = 0; m < macroSlices; m++)
                        {
                            float f0 = m / (float)macroSlices;
                            float f1 = (m + 1) / (float)macroSlices;
                            prevRootWorldXZ = Vector2.Lerp(rStart, rootWorldXZ, f0);
                            prevExitXZ = Vector2.Lerp(eStart, exitXZ, f0);
                            prevExitDir = Vector2.Lerp(dStart, exitDir, f0).normalized;
                            prevTorsoAngleDeg = angStart + Mathf.DeltaAngle(angStart, torsoAngleDeg) * f0;
                            Vector2 rEnd = Vector2.Lerp(rStart, rootWorldXZ, f1);
                            Vector2 eEnd = Vector2.Lerp(eStart, exitXZ, f1);
                            Vector2 dEnd = Vector2.Lerp(dStart, exitDir, f1).normalized;
                            float angEnd = angStart + Mathf.DeltaAngle(angStart, torsoAngleDeg) * f1;
                            float u0 = Mathf.Lerp(uStart, uNorm, f0);
                            float u1 = Mathf.Lerp(uStart, uNorm, f1);
                            bool wasLatchedBeforeSlice = strikeImpactLatched;
                            StepVerletPhysics(
                                sliceDt,
                                rEnd,
                                eEnd,
                                dEnd,
                                segLen,
                                torsoCenterWorldXZ,
                                angEnd,
                                true,
                                u0,
                                u1,
                                0f,
                                1f,
                                false);
                            if (!wasLatchedBeforeSlice && strikeImpactLatched)
                            {
                                // Publish the exact sub-terminal broadside collision pose for 1 frame even under 3x/4x multi-tick frames
                                // so read-only TryDetectDualWhipStrikeCrossing never misses the stationary detonation instant!
                                break;
                            }
                        }
                    }
                }
                else if (!isStrike)
                {
                    float idleMinChord = (sleepCurl01 > 0.01f || isDeadOrDowned) ? 1.55f : 1.88f;
                    float idleBendStiff = isDeadOrDowned ? 0.12f : 0.34f;
                    SolveIdleConstraintsAndCollider(exitXZ, segLen, torsoCenterWorldXZ, torsoAngleDeg, idleMinChord, idleBendStiff);
                }
                else
                {
                    // Preserve active continuum strike state across dt == 0 paused/high-refresh render frames!
                    Vector2 rootShift = exitXZ - pos[0];
                    if (rootShift.sqrMagnitude > 1e-10f)
                    {
                        for (int i = 0; i < particleCount; i++)
                        {
                            pos[i] += rootShift;
                            prevPos[i] += rootShift;
                        }
                    }
                }

                prevExitXZ = exitXZ;
                prevExitDir = exitDir;
                prevRootWorldXZ = rootWorldXZ;
                prevTorsoAngleDeg = torsoAngleDeg;
                if (isStrike)
                {
                    prevStrikeNorm = uNorm;
                    wasStrikePrev = true;
                }
            }

            BuildSmoothRibbonStrip(rootWorldXZ);
            return true;
        }

        private bool ResolvePartnerWhipIntact(GiantRigDef rig, ReadOnlySpan<bool> partMissingMask)
        {
            if (!PartnerTendrilIntact || rig?.softWhips == null || rig.softWhips.Count < 2)
            {
                return false;
            }
            for (int i = 0; i < rig.softWhips.Count; i++)
            {
                SoftWhipDef other = rig.softWhips[i];
                if (other == null || ReferenceEquals(other, def))
                {
                    continue;
                }
                int rIdx = other.resolvedRootBoneIndex;
                if (rIdx >= 0 && ((uint)rIdx >= (uint)partMissingMask.Length || !partMissingMask[rIdx]))
                {
                    return true;
                }
            }
            return false;
        }

        private void InitNaturalPosterolateralDrape(
            Vector2 exitXZ,
            float segLen,
            Vector2 torsoCenterWorldXZ,
            float torsoAngleDeg,
            float sleepCurl01 = 0f,
            float sleepCurlSideSign = 1f,
            bool isDeadOrDowned = false)
        {
            float side = def.lateralSideSign < 0f ? -1f : 1f;
            int segCount = particleCount - 1;
            float sleepW = sleepCurl01 > 0.001f ? SmoothStep(Mathf.Clamp01((sleepCurl01 - 0.04f) / 0.96f)) : 0f;
            float endCoilDeg = (side * sleepCurlSideSign > 0f) ? 315f : 275f;

            // Exits muscular shoulder socket at +/-146 deg (clearing folded wings & thorax collider),
            // then streams sleekly posterolaterally rearward (~+/-175.5 deg) when awake,
            // or coils around the sleeping ring / relaxes into a slack ground curve when dead or downed.
            pos[0] = exitXZ;
            prevPos[0] = exitXZ;
            for (int i = 1; i < particleCount; i++)
            {
                float s = i / (float)segCount;
                float awakeDeg = Mathf.Lerp(146f, 175.5f, SmoothStep(Mathf.Clamp01(s / 0.46f)));
                float drapeDeg;
                if (sleepW > 0.001f)
                {
                    float coilDeg = Mathf.Lerp(140f, endCoilDeg, SmoothStep(s));
                    drapeDeg = Mathf.Lerp(awakeDeg, coilDeg, sleepW);
                }
                else if (isDeadOrDowned)
                {
                    drapeDeg = Mathf.Lerp(145f, 248f, SmoothStep(s));
                }
                else
                {
                    drapeDeg = awakeDeg;
                }
                float curAngDeg = torsoAngleDeg + side * drapeDeg;
                Vector2 step = SkeletalKinematics.RotateCW(Vector2.up * segLen, curAngDeg);
                pos[i] = pos[i - 1] + step;
                prevPos[i] = pos[i];
            }

            float initMinChord = (sleepW > 0.001f || isDeadOrDowned) ? 1.55f : 1.88f;
            float initBendStiff = isDeadOrDowned ? 0.12f : 0.28f;
            for (int iter = 0; iter < 6; iter++)
            {
                SolveIdleConstraintsAndCollider(exitXZ, segLen, torsoCenterWorldXZ, torsoAngleDeg, initMinChord, initBendStiff);
            }
            for (int i = 0; i < particleCount; i++)
            {
                prevPos[i] = pos[i];
            }
        }

        private void ResolveStrikeTargetGeometry(
            Vector2 rootWorldXZ,
            float bodyHeadingDeg,
            Vector2 collarJointXZ,
            Vector2 torsoCenterWorldXZ,
            float torsoAngleDeg,
            bool isQuake,
            in BezierChainStepContext ctx)
        {
            float side = def.lateralSideSign < 0f ? -1f : 1f;
            Vector2 fwdDir = SkeletalKinematics.RotateCW(Vector2.up, bodyHeadingDeg);

            Vector2 rawTarget;
            if (ctx.HasAttackTarget)
            {
                rawTarget = ctx.AttackTargetWorldXZ;
            }
            else if (isQuake)
            {
                rawTarget = rootWorldXZ + fwdDir * (def.totalLength * 0.76f);
            }
            else
            {
                float defaultAzDeg = side * def.defaultStrikeAzimuthDeg;
                Vector2 dir = SkeletalKinematics.RotateCW(Vector2.up, torsoAngleDeg + defaultAzDeg);
                rawTarget = collarJointXZ + dir * (def.totalLength * def.defaultStrikeRangeFactor);
            }

            rawTarget = ClampOutsideEllipse(
                rawTarget,
                torsoCenterWorldXZ,
                torsoAngleDeg,
                Mathf.Max(0.25f, def.colliderRadii.x) * 1.06f,
                Mathf.Max(0.25f, def.colliderRadii.y) * 1.06f);

            Vector2 relFromRoot = rawTarget - rootWorldXZ;
            if (relFromRoot.sqrMagnitude < 0.25f)
            {
                relFromRoot = fwdDir * 10.6f;
            }

            float dist = relFromRoot.magnitude;
            strikeTargetForwardCells = Mathf.Clamp(dist, 6.6f, 12.2f);
            float worldAz = Mathf.Atan2(relFromRoot.x, relFromRoot.y) * Mathf.Rad2Deg;
            strikeTargetAzimuthDeg = Mathf.Clamp(Mathf.DeltaAngle(bodyHeadingDeg, worldAz), -38f, 38f);
        }

        private float EvalStrikeCollarMag(float u)
        {
            const float restMag = 146f;
            const float coilMag = 112f;
            const float windEnd = 0.295f;
            const float snapSpan = 0.165f;

            // Pure single-node rotational drive (ZERO internal whip muscle):
            // Handle accelerates strongly in the first half of the snap and smoothly arrests before impact,
            // launching a traveling flexural loop so distal whip-crack peaks AFTER root angular velocity!
            float reachT = Mathf.Clamp01(Mathf.InverseLerp(6.8f, 11.8f, strikeTargetForwardCells));
            float snapTargetMag = Mathf.Lerp(-58.0f, -25.0f, Mathf.Pow(reachT, 1.30f));

            if (!strikeImpactLatched)
            {
                if (u < windEnd)
                {
                    float w = SmoothStep(Mathf.Clamp01(u / windEnd));
                    return Mathf.Lerp(restMag, coilMag, w);
                }

                float uBase = Mathf.Clamp01((u - windEnd) / snapSpan);
                float sSnap = SmoothStep(Mathf.Pow(uBase, 0.76f));
                return Mathf.Lerp(coilMag, snapTargetMag, sSnap);
            }

            // Post-impact instant limpness + 16.7ms viscoelastic contact dwell + natural posterolateral flank recovery:
            float du = Mathf.Max(0f, u - strikeImpactNormTime);
            const float dwellNorm = 0.023f;
            if (du < dwellNorm)
            {
                return strikeHitCollarMag;
            }

            float duPost = du - dwellNorm;
            float rem = Mathf.Max(0.20f, 1.0f - strikeImpactNormTime - dwellNorm);
            const float recoilSpan = 0.115f;
            const float bounceMag = 52f;
            if (duPost < recoilSpan)
            {
                return Mathf.Lerp(strikeHitCollarMag, bounceMag, SmoothStep(duPost / recoilSpan));
            }

            float pRec = Mathf.Clamp01((duPost - recoilSpan) / Mathf.Max(0.14f, rem - recoilSpan));
            if (pRec < 0.76f)
            {
                return Mathf.Lerp(bounceMag, 174f, SmoothStep(pRec / 0.76f));
            }
            return Mathf.Lerp(174f, restMag, SmoothStep((pRec - 0.76f) / 0.24f));
        }

        private void DriveStrikeCollarPose(
            BoneNodeDef[] nodes,
            int rootIdx,
            BoneLocalPose[] inOutLocalPoses,
            float u,
            float collarMagDeg)
        {
            BoneNodeDef rootNode = nodes[rootIdx];
            float side = def.lateralSideSign < 0f ? -1f : 1f;
            float aimScale = 1f;
            if (strikeImpactLatched)
            {
                float du = Mathf.Max(0f, u - strikeImpactNormTime - 0.023f);
                aimScale = 1f - SmoothStep(Mathf.Clamp01(du / 0.12f));
            }
            float aimBias = strikeIsQuake ? (strikeTargetAzimuthDeg * aimScale) : 0f;
            float baseMag = Mathf.Abs(rootNode.baseAngle) > 1f ? Mathf.Abs(rootNode.baseAngle) : 146f;
            float blend = u < 0.06f
                ? SmoothStep(u / 0.06f)
                : (u > 0.95f ? 1f - SmoothStep((u - 0.95f) / 0.05f) : 1f);
            float desiredSigned = Mathf.LerpAngle(side * baseMag, aimBias + side * collarMagDeg, blend);
            inOutLocalPoses[rootIdx].angle = Mathf.DeltaAngle(rootNode.baseAngle, desiredSigned);
            inOutLocalPoses[rootIdx].offset = Vector2.zero;
        }

        private void StepVerletPhysics(
            float dt,
            Vector2 rootWorldXZ,
            Vector2 exitXZ,
            Vector2 exitDir,
            float segLen,
            Vector2 torsoCenterWorldXZ,
            float torsoAngleDeg,
            bool isStrike,
            float prevStrikeNormTime,
            float strikeNormTime,
            float sleepCurl01,
            float sleepCurlSideSign,
            bool isDeadOrDowned)
        {
            int segCount = particleCount - 1;
            int subTermIdx = Mathf.Clamp(Mathf.RoundToInt(segCount * 0.78f), 1, segCount);
            int midCheckIdx = Mathf.Clamp(Mathf.RoundToInt(segCount * (16f / 28f)), 1, segCount);
            int contactStartIdx = Mathf.Clamp(Mathf.RoundToInt(segCount * (8f / 28f)), 1, segCount);
            int proximalWideEndIdx = Mathf.Clamp(Mathf.RoundToInt(segCount * 0.5f), 1, segCount);
            int latchStartIdx = Mathf.Clamp(Mathf.RoundToInt(segCount * (20f / 28f)), 1, segCount);
            int latchEndIdx = Mathf.Clamp(Mathf.RoundToInt(segCount * (24f / 28f)), latchStartIdx, segCount);
            int defaultImpactNode = Mathf.Clamp(Mathf.RoundToInt(segCount * 0.75f), 1, segCount);
            Vector2 prevLastBefore = pos[segCount];
            Vector2 prevSubTermBefore = pos[subTermIdx];
            float collarSpeed = (exitXZ - prevExitXZ).magnitude / Mathf.Max(1e-4f, dt);

            bool calmRuntimeTrail = Current.Game != null
                && !isStrike
                && !wasStrikePrev
                && lastSettleEnergy < 0.14f
                && collarSpeed < 0.28f;

            int substeps = isStrike
                ? 14
                : (calmRuntimeTrail ? Mathf.Clamp(def.substeps >> 1, 2, 8) : Mathf.Clamp(def.substeps, 1, 16));
            int solverIters = isStrike
                ? 12
                : (calmRuntimeTrail ? 5 : Mathf.Max(def.constraintIterations * 3, 10));

            float dtSub = dt / substeps;
            float invDtSubSafe = 1f / Mathf.Max(1e-5f, dtSub);
            float side = def.lateralSideSign < 0f ? -1f : 1f;

            // Strike medial plane aligned with target azimuth so left & right whips meet squarely at target bearing
            bool doBilateralContact = isStrike && strikeIsQuake && partnerWhipAvailable;
            float strikePlaneHeadingDeg = torsoAngleDeg + (strikeIsQuake ? strikeTargetAzimuthDeg : 0f);
            Vector2 strikeFwdDir = SkeletalKinematics.RotateCW(Vector2.up, strikePlaneHeadingDeg);
            Vector2 strikeRightDir = new Vector2(strikeFwdDir.y, -strikeFwdDir.x);

            bool touchedSubTerminalThisTick = false;
            int touchedNodeThisTick = defaultImpactNode;
            float touchedClosingSpdThisTick = 0f;
            float hitFreezeCollarMag = -25f;
            Span<float> vLatBefore = stackalloc float[particleCount];
            Span<Vector2> relaxTargets = stackalloc Vector2[particleCount];
            float sleepW = (!isStrike && sleepCurl01 > 0.001f)
                ? SmoothStep(Mathf.Clamp01((sleepCurl01 - 0.04f) / 0.96f))
                : 0f;
            float endCoilDeg = (side * sleepCurlSideSign > 0f) ? 315f : 275f;

            for (int s = 0; s < substeps; s++)
            {
                float frac = (s + 1) / (float)substeps;
                Vector2 rootSubXZ = Vector2.Lerp(prevRootWorldXZ, rootWorldXZ, frac);
                Vector2 p0 = Vector2.Lerp(prevExitXZ, exitXZ, frac);
                Vector2 d0 = Vector2.Lerp(prevExitDir, exitDir, frac);
                if (d0.sqrMagnitude < 1e-6f) d0 = exitDir;
                else d0.Normalize();

                float uSub = Mathf.Clamp01(Mathf.Lerp(prevStrikeNormTime, strikeNormTime, frac));
                float torsoAngleSub = prevTorsoAngleDeg + Mathf.DeltaAngle(prevTorsoAngleDeg, torsoAngleDeg) * frac;
                float torsoRad = torsoAngleSub * Mathf.Deg2Rad;
                float torsoCos = Mathf.Cos(torsoRad);
                float torsoSin = Mathf.Sin(torsoRad);

                if (isStrike)
                {
                    if (touchedSubTerminalThisTick && !strikeImpactLatched)
                    {
                        pos[0] = p0;
                        float freezeWorldDeg = torsoAngleSub + (strikeIsQuake ? strikeTargetAzimuthDeg : 0f) + side * hitFreezeCollarMag;
                        Vector2 freezeDir = SkeletalKinematics.RotateCW(Vector2.up, freezeWorldDeg);
                        pos[1] = Vector2.Lerp(pos[1], p0 + freezeDir * segLen, 0.88f);
                        SolveDistanceSweep(0, segCount, 1, segLen);
                        SolveDistanceSweep(segCount - 1, -1, -1, segLen);
                        pos[0] = p0;
                        for (int k = 1; k < particleCount; k++)
                        {
                            prevPos[k] = pos[k];
                        }
                        continue;
                    }

                    // Pure passive continuum whip driven ONLY by fixed-socket rotation:
                    // ZERO internal muscle curvature (restDTheta == 0 everywhere), ZERO rigid front hook!
                    const float windEnd = 0.295f;
                    const float dwellNorm = 0.023f;
                    const float recoilSpan = 0.115f;
                    float collarMagSub = EvalStrikeCollarMag(uSub);
                    float aimScaleSub = 1f;
                    if (strikeImpactLatched)
                    {
                        float duAim = Mathf.Max(0f, uSub - strikeImpactNormTime - dwellNorm);
                        aimScaleSub = 1f - SmoothStep(Mathf.Clamp01(duAim / 0.12f));
                    }
                    float rootWorldDeg = torsoAngleSub + (strikeIsQuake ? strikeTargetAzimuthDeg * aimScaleSub : 0f) + side * collarMagSub;
                    Vector2 rootDirSub = SkeletalKinematics.RotateCW(Vector2.up, rootWorldDeg);
                    Vector2 rootSocketP1 = p0 + rootDirSub * segLen;

                    bool isExhaustedLimp = strikeImpactLatched;
                    float socketStrength;
                    float bendStiffnessScale;
                    if (!isExhaustedLimp)
                    {
                        socketStrength = uSub < windEnd ? 0.94f : 0.96f;
                        bendStiffnessScale = uSub < windEnd ? 1.35f : 1.45f;
                    }
                    else
                    {
                        float du = Mathf.Max(0f, uSub - strikeImpactNormTime);
                        float rem = Mathf.Max(0.20f, 1.0f - strikeImpactNormTime);
                        if (du < dwellNorm + recoilSpan)
                        {
                            // Instant post-impact limpness (撞后瞬间泄劲脱力): root torque drops 95%, shaft relaxes for elastic bounce!
                            socketStrength = 0.05f;
                            bendStiffnessScale = 0.34f;
                        }
                        else
                        {
                            float pRec = Mathf.Clamp01((du - dwellNorm - recoilSpan) / Mathf.Max(0.14f, rem - dwellNorm - recoilSpan));
                            socketStrength = 0.34f + 0.62f * pRec;
                            bendStiffnessScale = 0.64f + 0.55f * pRec;
                        }
                    }

                    // Unleash coupled transverse-arc viscoelastic rebound wave right after 16.7ms impact compression (du >= 0.023)
                    if (strikeImpactLatched && !strikeReboundWaveFired && (uSub - strikeImpactNormTime) >= dwellNorm)
                    {
                        strikeReboundWaveFired = true;
                        float vc = Mathf.Clamp(strikeClosingSpeedSaved, 5.2f, 8.4f);
                        int hi = Mathf.Clamp(strikeImpactNode, 1, segCount);
                        float nodeSpreadDenom = Mathf.Max(1f, segCount * (7.5f / 28f));
                        for (int j = 2; j < particleCount; j++)
                        {
                            Vector2 rVec = pos[j] - p0;
                            float rLen = rVec.magnitude;
                            if (rLen < 1e-4f) continue;
                            // Outward circular arc tangent around fixed socket p0 (preserves radial inextensibility without buckling!)
                            Vector2 arcTanWorld = new Vector2(side * (rVec.y / rLen), -side * (rVec.x / rLen));
                            float dNode = (j - hi) / nodeSpreadDenom;
                            float wc = 0.42f + 0.58f * Mathf.Exp(-0.5f * dNode * dNode);
                            float arcSpd = vc * 0.44f * wc * (rLen / 10.5f);
                            prevPos[j] = pos[j] - arcTanWorld * (arcSpd * dtSub);
                        }
                    }

                    // Sample pre-projection inward lateral velocities so PBD clamping never swallows elastic bounce speed
                    for (int i = 0; i < particleCount; i++)
                    {
                        Vector2 vBefore = (pos[i] - prevPos[i]) * invDtSubSafe;
                        vLatBefore[i] = Vector2.Dot(vBefore, strikeRightDir) * side;
                    }

                    // 1. Verlet step + passive aerodynamic drag (on ultra-light soft tip s > 0.80 during snap, or smooth air/ground trail during recovery)
                    for (int i = 1; i < particleCount; i++)
                    {
                        float sFrac = i / (float)segCount;
                        Vector2 stepDelta = pos[i] - prevPos[i];
                        float maxStep = 18f * dtSub;
                        float stepMag = stepDelta.magnitude;
                        if (stepMag > maxStep && stepMag > 1e-6f)
                        {
                            stepDelta *= maxStep / stepMag;
                        }
                        prevPos[i] = pos[i];

                        float damp;
                        if (!isExhaustedLimp)
                        {
                            if (uSub < windEnd)
                            {
                                damp = Mathf.Pow(0.985f, dtSub * 14f);
                            }
                            else
                            {
                                damp = Mathf.Pow(0.9992f, dtSub * 14f);
                                // Passive cross-flow air resistance ONLY on soft distal tip (s > 0.80) so u~0.75 belly hits first while soft tip lags!
                                if (sFrac > 0.80f)
                                {
                                    float vLat = Vector2.Dot(stepDelta, strikeRightDir);
                                    if (side * vLat < 0f)
                                    {
                                        float dc = 0.18f * Mathf.Pow((sFrac - 0.80f) / 0.20f, 1.25f) * Mathf.Clamp01(dtSub * 14f);
                                        stepDelta -= strikeRightDir * (vLat * dc);
                                    }
                                }
                            }
                        }
                        else
                        {
                            float du = Mathf.Max(0f, uSub - strikeImpactNormTime);
                            if (du < dwellNorm)
                            {
                                damp = Mathf.Pow(0.55f, dtSub * 14f);
                            }
                            else if (du < 0.115f)
                            {
                                damp = Mathf.Pow(0.988f, dtSub * 14f);
                            }
                            else if (uSub < 0.84f)
                            {
                                damp = Mathf.Pow(0.955f, dtSub * 14f);
                            }
                            else
                            {
                                float pSettle = Mathf.Clamp01((uSub - 0.84f) / 0.16f);
                                damp = Mathf.Pow(0.86f - 0.18f * pSettle, dtSub * 14f);
                            }
                        }

                        pos[i] += stepDelta * damp;
                    }

                    // 2. Pure passive continuum PBD rod solver: ZERO restDTheta (tip is softest, bk tapers monotonically down to 0.028!)
                    pos[0] = p0;
                    float rx = Mathf.Max(0.25f, def.colliderRadii.x);
                    float rz = Mathf.Max(0.25f, def.colliderRadii.y);
                    float invRx = 1f / rx;
                    float invRz = 1f / rz;
                    float maxR = rx > rz ? rx : rz;
                    float maxR2 = maxR * maxR;

                    int pbdIters = 16;
                    for (int iter = 0; iter < pbdIters; iter++)
                    {
                        pos[0] = p0;
                        if (socketStrength > 0f)
                        {
                            pos[1] = Vector2.Lerp(pos[1], rootSocketP1, 0.94f * socketStrength);
                        }

                        // Zero-rest-curvature passive continuum bending: monotonically softer from base (0.66) to soft tip (0.028)!
                        for (int i = 1; i < segCount; i++)
                        {
                            float sFrac = i / (float)segCount;
                            float bk = Mathf.Min(0.92f, (0.028f + 0.66f * Mathf.Pow(1f - sFrac, 1.65f)) * bendStiffnessScale);
                            Vector2 uPrev = pos[i] - pos[i - 1];
                            float uLenSq = uPrev.x * uPrev.x + uPrev.y * uPrev.y;
                            if (uLenSq > 1e-12f)
                            {
                                float invULen = 1f / Mathf.Sqrt(uLenSq);
                                Vector2 exB = (pos[i] + uPrev * (segLen * invULen)) - pos[i + 1];
                                float wc = invMass[i];
                                float wn = invMass[i + 1];
                                float sf = (bk * 0.48f) / (wc + wn);
                                pos[i] -= exB * (wc * sf);
                                pos[i + 1] += exB * (wn * sf);
                            }
                        }

                        // Multi-stride purely local continuum flexural chord guard (translation/rotation invariant, zero rest pre-bend!)
                        SolveMinimumBendChordStride(segLen, 1, 0.985f, 0.60f);
                        SolveMinimumBendChordStride(segLen, 4, 0.935f, 0.48f);
                        SolveMinimumBendChordStride(segLen, 9, 0.885f, 0.44f);
                        SolveMinimumBendChordStride(segLen, 13, 0.835f, 0.42f);

                        SolveDistanceSweep(0, segCount, 1, segLen);
                        PushOutOfThoraxColliderFast(torsoCenterWorldXZ, torsoCos, torsoSin, invRx, invRz, maxR2);

                        if (doBilateralContact && uSub >= 0.36f)
                        {
                            float slMid = Vector2.Dot(pos[midCheckIdx] - rootSubXZ, strikeRightDir) * side;
                            bool allowCloseLatch = !doBilateralContact || strikeTargetForwardCells >= 10.2f || slMid <= 0.19f || uSub >= 0.465f;
                            for (int i = contactStartIdx; i < particleCount; i++)
                            {
                                Vector2 rel = pos[i] - rootSubXZ;
                                float fwdProj = Vector2.Dot(rel, strikeFwdDir);
                                float signedLat = Vector2.Dot(rel, strikeRightDir) * side;
                                if (fwdProj >= 1.5f)
                                {
                                    float contactHalfGapCells = (i <= proximalWideEndIdx && fwdProj < 6.0f) ? 0.28f : 0.142f;
                                    if (signedLat < contactHalfGapCells)
                                    {
                                        float pen = contactHalfGapCells - signedLat;
                                        pos[i] += strikeRightDir * (side * pen);
                                        if (!strikeImpactLatched && allowCloseLatch && i >= latchStartIdx && i <= latchEndIdx && fwdProj >= 5.5f)
                                        {
                                            float closingSpd = Mathf.Max(0f, -vLatBefore[i]);
                                            if (!touchedSubTerminalThisTick || closingSpd > touchedClosingSpdThisTick)
                                            {
                                                touchedSubTerminalThisTick = true;
                                                touchedNodeThisTick = i;
                                                touchedClosingSpdThisTick = closingSpd;
                                                hitFreezeCollarMag = collarMagSub;
                                            }
                                        }
                                    }
                                }
                                else if (strikeImpactLatched && fwdProj < -1.0f)
                                {
                                    const float rearFlankMinLat = 1.60f;
                                    if (signedLat < rearFlankMinLat)
                                    {
                                        pos[i] += strikeRightDir * (side * (rearFlankMinLat - signedLat));
                                    }
                                }
                            }
                        }
                    }

                    SolveDistanceSweep(segCount - 1, -1, -1, segLen);
                    PushOutOfThoraxColliderFast(torsoCenterWorldXZ, torsoCos, torsoSin, invRx, invRz, maxR2);
                    pos[0] = p0;
                    SolveDistanceSweep(0, segCount, 1, segLen);
                    pos[0] = p0;

                    // Velocity-neutral inextensibility guard ONLY if centrifugal stretch exceeds 1.022*L
                    float maxSegAllowed = segLen * 1.022f;
                    for (int i = 0; i < segCount; i++)
                    {
                        Vector2 dSeg = pos[i + 1] - pos[i];
                        float lSeg = dSeg.magnitude;
                        if (lSeg > maxSegAllowed)
                        {
                            Vector2 newNext = pos[i] + dSeg * (maxSegAllowed / lSeg);
                            Vector2 shift = newNext - pos[i + 1];
                            for (int j = i + 1; j <= segCount; j++)
                            {
                                pos[j] += shift;
                                prevPos[j] += shift * 0.80f;
                            }
                        }
                    }
                }
                else
                {
                    // Calm / locomotion passive Verlet trail
                    float dragExpScale = dtSub * 6f;
                    float tDamp = Mathf.Pow(Mathf.Clamp01(1f - def.tangentialDrag), dragExpScale);
                    float slowNDamp = Mathf.Pow(Mathf.Clamp01(1f - def.restNormalDrag), dragExpScale);
                    float fastNDamp = Mathf.Pow(Mathf.Clamp01(1f - def.fastNormalDrag), dragExpScale);
                    float invStrikeDragRef = 1f / Mathf.Max(0.05f, def.strikeDragSpeedRef);

                    Vector2 bodyAftDir = -SkeletalKinematics.RotateCW(Vector2.up, torsoAngleSub);
                    Vector2 bodyOutSide = SkeletalKinematics.RotateCW(new Vector2(side, 0f), torsoAngleSub);
                    Vector2 restTrailDir = (bodyAftDir * 0.94f + bodyOutSide * 0.34f).normalized;

                    bool useSleepCurl = sleepW > 0.001f;
                    bool useDeadRelax = !useSleepCurl && isDeadOrDowned;
                    if (useSleepCurl || useDeadRelax)
                    {
                        relaxTargets[0] = p0;
                        for (int i = 1; i < particleCount; i++)
                        {
                            float sFrac = i / (float)segCount;
                            float targetDrapeDeg = useSleepCurl
                                ? Mathf.Lerp(140f, endCoilDeg, SmoothStep(sFrac))
                                : Mathf.Lerp(145f, 248f, SmoothStep(sFrac));
                            float curAng = torsoAngleSub + side * targetDrapeDeg;
                            relaxTargets[i] = relaxTargets[i - 1] + SkeletalKinematics.RotateCW(Vector2.up * segLen, curAng);
                        }
                    }
                    float deadGroundDamp = useDeadRelax ? Mathf.Pow(0.84f, dragExpScale) : 1f;

                    for (int i = 1; i < particleCount; i++)
                    {
                        Vector2 velPerTick = (pos[i] - prevPos[i]) * invDtSubSafe;
                        float speed = velPerTick.magnitude;
                        if (speed > 14f)
                        {
                            velPerTick *= 14f / speed;
                            speed = 14f;
                        }

                        Vector2 cur = pos[i];
                        Vector2 tan = pos[Mathf.Min(i + 1, segCount)] - pos[Mathf.Max(i - 1, 0)];
                        float tanSq = tan.sqrMagnitude;
                        if (tanSq < 1e-8f) tan = d0;
                        else tan *= 1f / Mathf.Sqrt(tanSq);
                        Vector2 nrm = new Vector2(-tan.y, tan.x);

                        float vT = velPerTick.x * tan.x + velPerTick.y * tan.y;
                        float vN = velPerTick.x * nrm.x + velPerTick.y * nrm.y;

                        float fastAlpha = Mathf.Clamp01(speed * invStrikeDragRef);
                        float nDamp = slowNDamp + (fastNDamp - slowNDamp) * fastAlpha;
                        Vector2 dampedVel = (tan * (vT * tDamp) + nrm * (vN * nDamp)) * deadGroundDamp;

                        prevPos[i] = cur;
                        Vector2 next = cur + dampedVel * dtSub;

                        float u = i / (float)segCount;
                        float awakeWeight = useDeadRelax ? 0f : (1f - sleepW);
                        float bellyW = 1f - u * def.restProximalCutoff;
                        if (awakeWeight > 0.001f && bellyW > 0f)
                        {
                            Vector2 bellyTarget = p0 + d0 * (i * segLen);
                            float pull = Mathf.Clamp01(bellyW * bellyW * def.restProximalGain * awakeWeight * dtSub);
                            next += (bellyTarget - next) * pull;
                        }
                        else if (awakeWeight > 0.001f && collarSpeed >= 0.02f && collarSpeed <= 0.25f)
                        {
                            // Passive oncoming wake streamlining during forward walk + symmetric AC undulation
                            Vector2 collarStep = exitXZ - prevExitXZ;
                            float fwdWalkSpd = Vector2.Dot(collarStep, -bodyAftDir) / Mathf.Max(0.2f, dt);
                            if (fwdWalkSpd > 0.02f && u > 0.18f)
                            {
                                Vector2 wakeStreamDir = (bodyAftDir * 0.991f + bodyOutSide * 0.13f).normalized;
                                Vector2 wakeTarget = p0 + wakeStreamDir * (i * segLen);
                                float wakePull = Mathf.Clamp01(0.038f * u * awakeWeight * dtSub);
                                next += (wakeTarget - next) * wakePull;
                            }
                            float acOmegaLat = Vector2.Dot(exitDir - prevExitDir, bodyOutSide) / Mathf.Max(0.2f, dt);
                            next += bodyOutSide * (acOmegaLat * 0.95f * (1f - 0.30f * u) * awakeWeight * dtSub);
                        }
                        else if (awakeWeight > 0.001f && collarSpeed < 0.015f && speed < 0.06f && u > 0.22f)
                        {
                            Vector2 restTarget = p0 + restTrailDir * (i * segLen);
                            float restPull = Mathf.Clamp01(0.026f * u * awakeWeight * dtSub);
                            next += (restTarget - next) * restPull;
                        }

                        if (useSleepCurl)
                        {
                            float sleepPull = Mathf.Clamp01((0.06f + 0.10f * u) * sleepW * dtSub);
                            next += (relaxTargets[i] - next) * sleepPull;
                        }
                        else if (useDeadRelax)
                        {
                            float deadPull = Mathf.Clamp01((0.032f + 0.048f * u) * dtSub);
                            next += (relaxTargets[i] - next) * deadPull;
                        }

                        pos[i] = next;
                    }

                    pos[0] = p0;
                    prevPos[0] = p0;
                    float idleMinChord = useSleepCurl
                        ? Mathf.Lerp(1.80f, 1.55f, sleepW)
                        : (useDeadRelax ? 1.55f : 1.80f);
                    float idleBendStiff = useSleepCurl
                        ? Mathf.Lerp(0.18f, 0.12f, sleepW)
                        : (useDeadRelax ? 0.08f : 0.18f);
                    for (int it = 0; it < solverIters; it++)
                    {
                        SolveIdleConstraintsAndColliderFast(p0, segLen, torsoCenterWorldXZ, torsoCos, torsoSin, idleMinChord, idleBendStiff);
                    }
                }
            }

            // Latch sub-terminal belly impact on the first contact tick with 16.7ms viscoelastic compression hold,
            // queuing full transverse-arc rebound wave on du >= 0.023f!
            if (isStrike && !strikeImpactLatched && touchedSubTerminalThisTick)
            {
                strikeImpactLatched = true;
                strikeImpactNormTime = strikeNormTime;
                strikeImpactNode = touchedNodeThisTick;
                strikeClosingSpeedSaved = Mathf.Max(touchedClosingSpdThisTick, 5.2f);
                strikeHitCollarMag = hitFreezeCollarMag;
                for (int i = 1; i < particleCount; i++)
                {
                    prevPos[i] = pos[i];
                }
            }

            float energy = 0f;
            for (int i = 1; i < particleCount; i++)
            {
                energy += (pos[i] - prevPos[i]).magnitude * invDtSubSafe;
            }
            lastSettleEnergy = energy / segCount;
            float invDt = 1f / Mathf.Max(1e-5f, dt);
            lastTipSpeed = (pos[segCount] - prevLastBefore).magnitude * invDt;
            lastSubTerminalSpeed = (pos[subTermIdx] - prevSubTermBefore).magnitude * invDt;
        }

        private void SolveDistanceSweepStrike(float segLen)
        {
            int segCount = particleCount - 1;
            for (int i = 0; i < segCount; i++)
            {
                Vector2 delta = pos[i + 1] - pos[i];
                float len = delta.magnitude;
                if (len < 1e-6f)
                {
                    continue;
                }
                float w0 = i == 0 ? 0f : (i == 1 ? invMass[1] * 0.12f : invMass[i]);
                float w1 = invMass[i + 1];
                float wSum = w0 + w1;
                if (wSum <= 0f)
                {
                    continue;
                }
                float diff = (len - segLen) / (len * wSum);
                Vector2 corr = delta * diff;
                if (w0 > 0f)
                {
                    pos[i] += corr * w0;
                }
                pos[i + 1] -= corr * w1;
            }
        }

        private void SolveIdleConstraintsAndCollider(
            Vector2 rootAnchorXZ,
            float segLen,
            Vector2 torsoCenterWorldXZ,
            float torsoAngleDeg,
            float minBendChordFactor,
            float bendStiffness)
        {
            float rad = torsoAngleDeg * Mathf.Deg2Rad;
            SolveIdleConstraintsAndColliderFast(
                rootAnchorXZ,
                segLen,
                torsoCenterWorldXZ,
                Mathf.Cos(rad),
                Mathf.Sin(rad),
                minBendChordFactor,
                bendStiffness);
        }

        private void SolveIdleConstraintsAndColliderFast(
            Vector2 rootAnchorXZ,
            float segLen,
            Vector2 torsoCenterWorldXZ,
            float torsoCos,
            float torsoSin,
            float minBendChordFactor,
            float bendStiffness)
        {
            pos[0] = rootAnchorXZ;
            float rx = Mathf.Max(0.25f, def.colliderRadii.x);
            float rz = Mathf.Max(0.25f, def.colliderRadii.y);
            float invRx = 1f / rx;
            float invRz = 1f / rz;
            float maxR = rx > rz ? rx : rz;
            float maxR2 = maxR * maxR;

            SolveDistanceSweep(0, particleCount - 1, 1, segLen);
            SolveMinimumBendChord(segLen, minBendChordFactor, bendStiffness);
            PushOutOfThoraxColliderFast(torsoCenterWorldXZ, torsoCos, torsoSin, invRx, invRz, maxR2);
            SolveDistanceSweep(particleCount - 2, -1, -1, segLen);
            PushOutOfThoraxColliderFast(torsoCenterWorldXZ, torsoCos, torsoSin, invRx, invRz, maxR2);
            pos[0] = rootAnchorXZ;
        }

        private static Vector2 ClampOutsideEllipse(
            Vector2 ptWorldXZ,
            Vector2 centerWorldXZ,
            float angleDeg,
            float rx,
            float rz)
        {
            Vector2 relLocal = SkeletalKinematics.RotateCW(ptWorldXZ - centerWorldXZ, -angleDeg);
            float nx = relLocal.x / rx;
            float nz = relLocal.y / rz;
            float normSq = nx * nx + nz * nz;
            if (normSq < 1f && normSq > 1e-8f)
            {
                float invNorm = 1f / Mathf.Sqrt(normSq);
                Vector2 pushedLocal = new Vector2(relLocal.x * invNorm * 1.005f, relLocal.y * invNorm * 1.005f);
                return centerWorldXZ + SkeletalKinematics.RotateCW(pushedLocal, angleDeg);
            }
            return ptWorldXZ;
        }

        private void SolveMinimumBendChord(float segLen, float minChordFactor, float stiffness)
        {
            float minChord = Mathf.Clamp(minChordFactor, 1.0f, 1.98f) * segLen;
            float minChordSq = minChord * minChord;
            for (int i = 1; i < particleCount - 1; i++)
            {
                Vector2 span = pos[i + 1] - pos[i - 1];
                float chordSq = span.x * span.x + span.y * span.y;
                if (chordSq >= minChordSq)
                {
                    continue;
                }
                float chordLen = chordSq > 1e-12f ? Mathf.Sqrt(chordSq) : 0f;

                Vector2 dir = chordLen > 1e-5f
                    ? span / chordLen
                    : (pos[i] - pos[i - 1]).sqrMagnitude > 1e-6f
                        ? (pos[i] - pos[i - 1]).normalized
                        : new Vector2(0f, -1f);
                float correction = (minChord - chordLen) * stiffness;
                if (i == 1)
                {
                    pos[i + 1] += dir * correction;
                }
                else
                {
                    pos[i - 1] -= dir * (0.5f * correction);
                    pos[i + 1] += dir * (0.5f * correction);
                }
            }
        }

        private void SolveMinimumBendChordStride(float segLen, int stride, float minRatio, float stiffness)
        {
            float minChord = minRatio * (2 * stride * segLen);
            float minChordSq = minChord * minChord;
            int end = particleCount - stride;
            for (int i = stride; i < end; i++)
            {
                Vector2 span = pos[i + stride] - pos[i - stride];
                float chordSq = span.x * span.x + span.y * span.y;
                if (chordSq >= minChordSq)
                {
                    continue;
                }
                float chordLen = chordSq > 1e-12f ? Mathf.Sqrt(chordSq) : 0f;
                Vector2 dir = chordLen > 1e-5f ? span / chordLen : new Vector2(0f, -1f);
                float correction = (minChord - chordLen) * stiffness;
                float wA = invMass[i - stride];
                float wB = invMass[i + stride];
                float wSum = wA + wB;
                if (wSum <= 1e-8f)
                {
                    continue;
                }
                if (wA > 0f)
                {
                    pos[i - stride] -= dir * (correction * (wA / wSum));
                }
                pos[i + stride] += dir * (correction * (wB / wSum));
            }
        }

        private void SolveDistanceSweep(int startIdx, int endExclusive, int step, float segLen)
        {
            for (int i = startIdx; i != endExclusive; i += step)
            {
                Vector2 delta = pos[i + 1] - pos[i];
                float len = delta.magnitude;
                if (len < 1e-6f)
                {
                    continue;
                }
                float w0 = invMass[i];
                float w1 = invMass[i + 1];
                float wSum = w0 + w1;
                if (wSum <= 0f)
                {
                    continue;
                }
                float diff = (len - segLen) / (len * wSum);
                Vector2 corr = delta * diff;
                if (w0 > 0f)
                {
                    pos[i] += corr * w0;
                }
                pos[i + 1] -= corr * w1;
            }
        }

        private void PushOutOfThoraxColliderFast(
            Vector2 torsoCenterWorldXZ,
            float torsoCos,
            float torsoSin,
            float invRx,
            float invRz,
            float maxR2)
        {
            for (int i = 1; i < particleCount; i++)
            {
                float dx = pos[i].x - torsoCenterWorldXZ.x;
                float dz = pos[i].y - torsoCenterWorldXZ.y;
                float distSq = dx * dx + dz * dz;
                if (distSq >= maxR2)
                {
                    continue;
                }

                float localX = dx * torsoCos - dz * torsoSin;
                float localZ = dx * torsoSin + dz * torsoCos;
                float nx = localX * invRx;
                float nz = localZ * invRz;
                float normSq = nx * nx + nz * nz;
                if (normSq < 1f && normSq > 1e-8f)
                {
                    float scale = 1.005f / Mathf.Sqrt(normSq);
                    float pushedX = localX * scale;
                    float pushedZ = localZ * scale;
                    pos[i] = new Vector2(
                        torsoCenterWorldXZ.x + (pushedX * torsoCos + pushedZ * torsoSin),
                        torsoCenterWorldXZ.y + (-pushedX * torsoSin + pushedZ * torsoCos));
                }
            }
        }

        private void BuildSmoothRibbonStrip(Vector2 rootWorldXZ)
        {
            int segCount = particleCount - 1;
            for (int i = 0; i < segCount; i++)
            {
                Vector2 p0 = pos[i > 0 ? i - 1 : 0];
                Vector2 p1 = pos[i];
                Vector2 p2 = pos[i + 1];
                Vector2 p3 = pos[i + 2 <= segCount ? i + 2 : segCount];

                int dstEven = i * 2;
                curvePts[dstEven] = p1;
                curvePts[dstEven + 1] = new Vector2(
                    0.5f * ((-0.125f * p0.x) + (1.125f * p1.x) + (1.125f * p2.x) - (0.125f * p3.x)),
                    0.5f * ((-0.125f * p0.y) + (1.125f * p1.y) + (1.125f * p2.y) - (0.125f * p3.y)));
            }
            curvePts[stripSampleCount - 1] = pos[segCount];

            for (int j = 0; j < stripSampleCount; j++)
            {
                Vector2 prev = curvePts[j > 0 ? j - 1 : 0];
                Vector2 next = curvePts[j + 1 < stripSampleCount ? j + 1 : stripSampleCount - 1];
                Vector2 tan = next - prev;
                float tanSq = tan.x * tan.x + tan.y * tan.y;
                if (tanSq < 1e-8f)
                {
                    tan = Vector2.up;
                }
                else
                {
                    tan *= 1f / Mathf.Sqrt(tanSq);
                }

                Vector2 rightAxis = new Vector2(tan.y, -tan.x);
                float halfW = baseHalfWidths[j];

                if (j > 0 && j < stripSampleCount - 1)
                {
                    Vector2 tPrev = curvePts[j] - curvePts[j - 1];
                    Vector2 tNext = curvePts[j + 1] - curvePts[j];
                    float lenPrevSq = tPrev.x * tPrev.x + tPrev.y * tPrev.y;
                    float lenNextSq = tNext.x * tNext.x + tNext.y * tNext.y;
                    if (lenPrevSq > 1e-8f && lenNextSq > 1e-8f)
                    {
                        float lenPrev = Mathf.Sqrt(lenPrevSq);
                        float lenNext = Mathf.Sqrt(lenNextSq);
                        float ds = 0.5f * (lenPrev + lenNext);
                        if (ds > 1e-4f)
                        {
                            float dot = (tPrev.x * tNext.x + tPrev.y * tNext.y) / (lenPrev * lenNext);
                            if (dot < 0.995f)
                            {
                                dot = Mathf.Clamp(dot, -0.99f, 1f);
                                float halfTurnAngle = 0.5f * Mathf.Acos(dot);
                                if (halfTurnAngle > 0.05f)
                                {
                                    float radiusEstimate = ds / Mathf.Max(0.05f, 2f * halfTurnAngle);
                                    halfW = Mathf.Min(halfW, Mathf.Max(0.22f, radiusEstimate * 0.92f));
                                }
                            }
                        }
                    }
                }

                Vector2 leftW = curvePts[j] - rightAxis * halfW;
                Vector2 rightW = curvePts[j] + rightAxis * halfW;

                stripLeftWorldXZ[j] = leftW;
                stripRightWorldXZ[j] = rightW;
                stripLeftLocalXZ[j] = leftW - rootWorldXZ;
                stripRightLocalXZ[j] = rightW - rootWorldXZ;
            }
        }

        public void FillSnapshot(Vector2 rootWorldXZ, SoftWhipRibbonSnapshot target)
        {
            if (target.stripLeftLocalXZ == null || target.stripLeftLocalXZ.Length != stripSampleCount)
            {
                target.stripLeftLocalXZ = new Vector2[stripSampleCount];
                target.stripRightLocalXZ = new Vector2[stripSampleCount];
                target.centerlineWorldXZ = new Vector2[stripSampleCount];
                target.physicsParticlesWorldXZ = new Vector2[particleCount];
            }
            target.vCoords = vCoords;
            Array.Copy(stripLeftLocalXZ, target.stripLeftLocalXZ, stripSampleCount);
            Array.Copy(stripRightLocalXZ, target.stripRightLocalXZ, stripSampleCount);
            Array.Copy(curvePts, target.centerlineWorldXZ, stripSampleCount);
            Array.Copy(pos, target.physicsParticlesWorldXZ, particleCount);
            target.visible = initialized;
            target.rootWorldXZ = rootWorldXZ;
            target.tipSpeedCellsPerTick = lastTipSpeed;
            target.settleEnergy = lastSettleEnergy;
        }

        public SoftWhipRibbonSnapshot CreateSnapshot(Vector2 rootWorldXZ)
        {
            var snap = new SoftWhipRibbonSnapshot();
            FillSnapshot(rootWorldXZ, snap);
            return snap;
        }

        public void CopyWorldRibbonEdges(Vector2[] outLeftWorld, Vector2[] outRightWorld, out int count)
        {
            count = stripSampleCount;
            if (!initialized || outLeftWorld == null || outRightWorld == null)
            {
                count = 0;
                return;
            }
            Array.Copy(stripLeftWorldXZ, outLeftWorld, stripSampleCount);
            Array.Copy(stripRightWorldXZ, outRightWorld, stripSampleCount);
        }

        private static float SmoothRamp(float edge0, float edge1, float x)
        {
            return SmoothStep((x - edge0) / Mathf.Max(1e-5f, edge1 - edge0));
        }

        private static float EaseInOutSine(float x)
        {
            x = Mathf.Clamp01(x);
            return 0.5f - 0.5f * Mathf.Cos(x * Mathf.PI);
        }

        private static float SmoothStep(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }
    }
}
