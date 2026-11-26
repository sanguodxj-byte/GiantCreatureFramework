using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public class BezierChainDef
    {
        public string chainId;
        public List<string> boneIds = new List<string>();
        public Vector2 tipUV = new Vector2(0.5f, 0.88f);

        public float p1Frequency = 0.28f;
        public float p1Damping = 0.58f;
        public float p2Frequency = 0.16f;
        public float p2Damping = 0.36f;
        public float p3Frequency = 0.095f;
        public float p3Damping = 0.25f;

        public float whipCrackGain = 0.42f;
        public float waveLagCoupling = 0.45f;
        public float maxJointBendDeg = 58f;

        public float idleWaveAmplitudeDeg = 0f;
        public float idleWavePeriodTicks = 60f;

        // World-space velocity trailing & locomotion wake sway
        public float velocityTrailingGain = 0f;
        public float trailingMaxOffsetCells = 7.5f;
        public float trailingLateralSign = 0f;
        public float trailingLateralRatio = 0.22f;
        public float moveSwayAmplitudeCells = 0f;
        public float moveSwayPeriodTicks = 54f;
        public float moveRefSpeedCellsPerTick = 0.05f;

        // Target-directed whip strike during listed animations
        public List<string> strikeAnimDefs = new List<string>();
        public float strikeWindupEndNorm = 0.26f;
        public float strikeSnapEndNorm = 0.62f;
        public float strikeFreqBoost = 2.75f;
        public float defaultStrikeRangeFactor = 0.82f;
        public float defaultStrikeSideCells = 0f;

        [Unsaved]
        public int[] resolvedBoneIndices = Array.Empty<int>();

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

    public struct BezierChainStepContext
    {
        public float BodyWorldAngleDeg;
        public string ActiveClipDefName;
        public float ActiveClipNormalizedTime;
        public bool HasAttackTarget;
        public Vector2 AttackTargetWorldXZ;
        public float SleepCurlProgress01;
        public float SleepCurlSideSign;
        public bool IsDeadOrDowned;
    }

    public sealed class DampedBezierChainRuntime
    {
        public const int CurveSamples = 40;

        private readonly BezierChainDef def;
        private readonly Vector2[] curvePts = new Vector2[CurveSamples + 1];

        private Vector2 p0;
        private Vector2 p1;
        private Vector2 p2;
        private Vector2 p3;
        private Vector2 effP1;
        private Vector2 effP2;
        private Vector2 v1;
        private Vector2 v2;
        private Vector2 v3;
        private Vector2 prevP0;
        private Vector2 prevBowNormal;
        private Vector2 smoothedBowP1;
        private Vector2 smoothedBowP2;
        private bool hasPrevRoot;
        private Vector2 prevRootXZ;
        private Vector2 smoothedRootVelXZ;

        private float elapsedTicks;
        private int lastActiveCount;
        private bool initialized;

        public BezierChainDef Def => def;
        public bool IsInitialized => initialized;
        public int ActiveBoneCount => lastActiveCount;
        public Vector2 P0 => p0;
        public Vector2 P1 => effP1;
        public Vector2 P2 => effP2;
        public Vector2 P3 => p3;
        public Vector2 RawP1 => p1;
        public Vector2 RawP2 => p2;
        public Vector2 CurrentTipWorldXZ => p3;
        public Vector2 SmoothedRootVelocityXZ => smoothedRootVelXZ;
        public bool IsSettling => initialized && (v1.sqrMagnitude + v2.sqrMagnitude + v3.sqrMagnitude) > 4e-4f;
        public ReadOnlySpan<Vector2> CurvePoints => curvePts;

        public DampedBezierChainRuntime(BezierChainDef def)
        {
            this.def = def ?? throw new ArgumentNullException(nameof(def));
        }

        public void Reset()
        {
            initialized = false;
            lastActiveCount = 0;
            v1 = Vector2.zero;
            v2 = Vector2.zero;
            v3 = Vector2.zero;
            prevBowNormal = Vector2.zero;
            smoothedBowP1 = Vector2.zero;
            smoothedBowP2 = Vector2.zero;
            hasPrevRoot = false;
            prevRootXZ = Vector2.zero;
            smoothedRootVelXZ = Vector2.zero;
        }

        public void AddWorldImpulse(
            Vector2 worldVelocityDeltaCellsPerTick,
            float proximalFactor = 0.45f,
            float midFactor = 0.85f,
            float tipFactor = 1.0f)
        {
            if (!initialized || worldVelocityDeltaCellsPerTick.sqrMagnitude < 1e-8f)
            {
                return;
            }
            const float maxImpulseSpeed = 1.25f;
            float mag = worldVelocityDeltaCellsPerTick.magnitude;
            Vector2 impulse = mag > maxImpulseSpeed
                ? worldVelocityDeltaCellsPerTick * (maxImpulseSpeed / mag)
                : worldVelocityDeltaCellsPerTick;

            v1 = Vector2.ClampMagnitude(v1 + impulse * proximalFactor, 1.8f);
            v2 = Vector2.ClampMagnitude(v2 + impulse * midFactor, 2.4f);
            v3 = Vector2.ClampMagnitude(v3 + impulse * tipFactor, 3.0f);
        }

        public static Vector2 EvalCubicBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float u)
        {
            float inv = 1f - u;
            float inv2 = inv * inv;
            float u2 = u * u;
            return (inv2 * inv) * p0
                 + (3f * inv2 * u) * p1
                 + (3f * inv * u2) * p2
                 + (u2 * u) * p3;
        }

        public void SampleCurvePoints(Span<Vector2> outPoints)
        {
            int count = Mathf.Min(outPoints.Length, curvePts.Length);
            for (int i = 0; i < count; i++)
            {
                outPoints[i] = curvePts[i];
            }
        }

        public void Step(
            GiantRigDef rig,
            Vector2 rootWorldXZ,
            float headingDeg,
            BoneLocalPose[] inoutLocalPoses,
            ReadOnlySpan<bool> missingMask,
            float deltaTicks,
            float globalScale = 1f)
        {
            var ctx = new BezierChainStepContext
            {
                BodyWorldAngleDeg = headingDeg
            };
            Step(rig, rootWorldXZ, headingDeg, inoutLocalPoses, missingMask, deltaTicks, in ctx, globalScale);
        }

        public void Step(
            GiantRigDef rig,
            Vector2 rootWorldXZ,
            float headingDeg,
            BoneLocalPose[] inoutLocalPoses,
            ReadOnlySpan<bool> missingMask,
            float deltaTicks,
            in BezierChainStepContext ctx,
            float globalScale = 1f)
        {
            if (rig == null || inoutLocalPoses == null || def.resolvedBoneIndices == null || def.resolvedBoneIndices.Length < 2)
            {
                initialized = false;
                lastActiveCount = 0;
                hasPrevRoot = false;
                smoothedRootVelXZ = Vector2.zero;
                return;
            }

            BoneNodeDef[] nodes = rig.SortedNodes;
            int[] indices = def.resolvedBoneIndices;
            int kCount = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                int bIdx = indices[i];
                if ((uint)bIdx >= (uint)nodes.Length || IsBoneOrAncestorMissing(nodes, bIdx, missingMask))
                {
                    break;
                }
                kCount++;
            }

            if (kCount < 2)
            {
                initialized = false;
                lastActiveCount = kCount;
                hasPrevRoot = false;
                smoothedRootVelXZ = Vector2.zero;
                return;
            }

            elapsedTicks += Mathf.Max(0f, deltaTicks);

            if (!initialized || !hasPrevRoot || (rootWorldXZ - prevRootXZ).sqrMagnitude > 9.0f)
            {
                prevRootXZ = rootWorldXZ;
                smoothedRootVelXZ = Vector2.zero;
                hasPrevRoot = true;
            }
            else if (deltaTicks >= 0.02f)
            {
                Vector2 rawVel = (rootWorldXZ - prevRootXZ) / deltaTicks;
                prevRootXZ = rootWorldXZ;
                float alpha = 1f - Mathf.Exp(-0.20f * Mathf.Clamp(deltaTicks, 0.02f, 6f));
                smoothedRootVelXZ = Vector2.Lerp(smoothedRootVelXZ, rawVel, alpha);
            }

            int totalNodes = nodes.Length;
            Span<BoneLocalPose> evalLocal = totalNodes <= 64
                ? stackalloc BoneLocalPose[totalNodes]
                : new BoneLocalPose[totalNodes];
            int copyCount = Mathf.Min(inoutLocalPoses.Length, totalNodes);
            for (int i = 0; i < copyCount; i++)
            {
                evalLocal[i] = inoutLocalPoses[i];
            }

            if (def.idleWaveAmplitudeDeg > 0.01f)
            {
                float period = Mathf.Max(1f, def.idleWavePeriodTicks);
                float omegaWave = (2f * Mathf.PI) / period;
                for (int k = 0; k < kCount; k++)
                {
                    int bIdx = indices[k];
                    evalLocal[bIdx].angle += Mathf.Sin(omegaWave * elapsedTicks - k * 0.7f) * def.idleWaveAmplitudeDeg;
                }
            }

            Span<BoneWorldPose> targetWorld = totalNodes <= 64
                ? stackalloc BoneWorldPose[totalNodes]
                : new BoneWorldPose[totalNodes];
            SkeletalKinematics.SolveWorldPoses(rig, rootWorldXZ, headingDeg, evalLocal, missingMask, targetWorld, globalScale);

            p0 = targetWorld[indices[0]].jointWorldXZ;
            int baseParentIdx = nodes[indices[0]].parentRuntimeIndex;
            float parentWorldDeg = baseParentIdx >= 0
                ? targetWorld[baseParentIdx].worldAngleDeg
                : SkeletalKinematics.NormalizeAngle360(headingDeg);

            Span<float> boneLengths = stackalloc float[kCount];
            Span<Vector2> qPts = stackalloc Vector2[kCount + 1];
            qPts[0] = p0;
            float lTotal = 0f;
            for (int k = 0; k < kCount - 1; k++)
            {
                Vector2 nextJoint = targetWorld[indices[k + 1]].jointWorldXZ;
                qPts[k + 1] = nextJoint;
                float segLen = Mathf.Max(0.05f, Vector2.Distance(qPts[k], nextJoint));
                boneLengths[k] = segLen;
                lTotal += segLen;
            }
            Vector2 tipWorld = targetWorld[indices[kCount - 1]].UVToWorldXZ(def.tipUV.x, def.tipUV.y);
            qPts[kCount] = tipWorld;
            float lastLen = Mathf.Max(0.05f, Vector2.Distance(qPts[kCount - 1], tipWorld));
            boneLengths[kCount - 1] = lastLen;
            lTotal += lastLen;

            SamplePolylineThirds(qPts, boneLengths, kCount, lTotal, out Vector2 qThird, out Vector2 qTwoThirds, out Vector2 qEnd);
            Vector2 p1Target = (-5f * p0 + 18f * qThird - 9f * qTwoThirds + 2f * qEnd) / 6f;
            Vector2 p2Target = ( 2f * p0 - 9f * qThird + 18f * qTwoThirds - 5f * qEnd) / 6f;
            Vector2 p3Target = qEnd;

            Vector2 bodyForward = SkeletalKinematics.RotateCW(new Vector2(0f, 1f), ctx.BodyWorldAngleDeg);
            Vector2 bodyRight = SkeletalKinematics.RotateCW(new Vector2(1f, 0f), ctx.BodyWorldAngleDeg);
            Vector2 restChordInit = qEnd - p0;
            bool isRearRestChain = restChordInit.sqrMagnitude > 1e-4f &&
                Vector2.Dot(restChordInit.normalized, bodyForward) < -0.35f;

            float speed = smoothedRootVelXZ.magnitude;
            if (speed > 1e-4f)
            {
                Vector2 backDir = -smoothedRootVelXZ / speed;
                float rearWakeGate = 1f;
                if (isRearRestChain)
                {
                    // Prevent reverse/backpedal motion from flipping a rear-mounted tail forward over the dorsal carapace
                    rearWakeGate = Mathf.Clamp01((Vector2.Dot(backDir, -bodyForward) + 0.10f) / 0.85f);
                }
                if (def.velocityTrailingGain > 0f && rearWakeGate > 0.001f)
                {
                    float speed01 = Mathf.Clamp01(speed / Mathf.Max(0.005f, def.moveRefSpeedCellsPerTick));
                    float streamBlend = Mathf.Clamp01(0.78f * speed01) * rearWakeGate;
                    Vector2 flankDir = bodyRight * def.trailingLateralSign;
                    Vector2 wakeRay = (backDir + flankDir * def.trailingLateralRatio).normalized;
                    Vector2 wakeP1 = p0 + wakeRay * (0.33f * lTotal);
                    Vector2 wakeP2 = p0 + wakeRay * (0.66f * lTotal);
                    Vector2 wakeP3 = p0 + wakeRay * (0.988f * lTotal);
                    p1Target = Vector2.Lerp(p1Target, wakeP1, streamBlend * 0.65f);
                    p2Target = Vector2.Lerp(p2Target, wakeP2, streamBlend * 0.85f);
                    p3Target = Vector2.Lerp(p3Target, wakeP3, streamBlend);
                    ClampHandleDistance(p0, ref p1Target, 0.58f * lTotal);
                    ClampHandleDistance(p0, ref p2Target, 0.88f * lTotal);
                    ClampHandleDistance(p0, ref p3Target, 0.992f * lTotal);
                }

                if (def.moveSwayAmplitudeCells > 0.001f)
                {
                    float speed01 = Mathf.Clamp01(speed / Mathf.Max(0.005f, def.moveRefSpeedCellsPerTick));
                    Vector2 refWakeDir = isRearRestChain && rearWakeGate < 0.25f ? -bodyForward : backDir;
                    Vector2 swayPerp = new Vector2(-refWakeDir.y, refWakeDir.x);
                    float phase = (2f * Mathf.PI * elapsedTicks / Mathf.Max(10f, def.moveSwayPeriodTicks))
                                + (def.trailingLateralSign < 0f ? 0f : Mathf.PI * 0.65f);
                    float amp = def.moveSwayAmplitudeCells * speed01;
                    p1Target += swayPerp * (amp * 0.20f * Mathf.Sin(phase));
                    p2Target += swayPerp * (amp * 0.55f * Mathf.Sin(phase - 1.15f));
                    p3Target += swayPerp * (amp * 1.00f * Mathf.Sin(phase - 2.30f));
                    ClampHandleDistance(p0, ref p3Target, 0.992f * lTotal);
                }
            }

            float freqScale = 1f;
            float minDampBoost = 0f;
            float effectiveLagCoupling = def.waveLagCoupling;
            bool isStrike = def.IsStrikeAnim(ctx.ActiveClipDefName);

            if (isStrike)
            {
                float u = Mathf.Clamp01(ctx.ActiveClipNormalizedTime);
                Vector2 restChord = qEnd - p0;
                bool isRearChain = restChord.sqrMagnitude > 1e-4f &&
                    Vector2.Dot(restChord.normalized, bodyForward) < -0.35f;

                float windupEnd = Mathf.Clamp(def.strikeWindupEndNorm, 0.05f, 0.45f);
                float snapEnd = Mathf.Clamp(def.strikeSnapEndNorm, windupEnd + 0.12f, 0.90f);

                if (isRearChain)
                {
                    Vector2 bodyAft = -bodyForward;
                    Vector2 aftSide = bodyRight;
                    Vector2 toTarg = ctx.HasAttackTarget
                        ? (ctx.AttackTargetWorldXZ - p0)
                        : bodyAft * (lTotal * def.defaultStrikeRangeFactor);
                    float dist = toTarg.magnitude;
                    Vector2 strikeDir = dist > 0.05f ? (toTarg / dist) : bodyAft;
                    bool targetAstern = Vector2.Dot(strikeDir, bodyAft) > 0.10f;
                    float thetaHit = targetAstern
                        ? Mathf.Clamp(Mathf.Atan2(Vector2.Dot(strikeDir, aftSide), Vector2.Dot(strikeDir, bodyAft)), -1.15f, 1.15f)
                        : 0f;
                    float hitRadius = targetAstern
                        ? Mathf.Clamp(dist, 0.45f * lTotal, 0.96f * lTotal)
                        : 0.88f * lTotal;

                    float coilSign = thetaHit >= 0f ? -1f : 1f;
                    float th1Coil = coilSign * 0.95f, r1Coil = 0.35f * lTotal;
                    float th2Coil = coilSign * 1.10f, r2Coil = 0.68f * lTotal;
                    float th3Coil = coilSign * 1.18f, r3Coil = 0.90f * lTotal;
                    float th3Follow = -coilSign * 0.98f;
                    float th1Follow = Mathf.Lerp(thetaHit, th3Follow, 0.45f);
                    float th2Follow = Mathf.Lerp(thetaHit, th3Follow, 0.72f);

                    Vector2 p1RestVec = p1Target - p0;
                    Vector2 p2RestVec = p2Target - p0;
                    Vector2 p3RestVec = p3Target - p0;
                    float r1Rest = Mathf.Max(0.20f * lTotal, p1RestVec.magnitude);
                    float r2Rest = Mathf.Max(0.45f * lTotal, p2RestVec.magnitude);
                    float r3Rest = Mathf.Max(0.75f * lTotal, p3RestVec.magnitude);
                    float th1Rest = Mathf.Atan2(Vector2.Dot(p1RestVec, aftSide), Vector2.Dot(p1RestVec, bodyAft));
                    float th2Rest = Mathf.Atan2(Vector2.Dot(p2RestVec, aftSide), Vector2.Dot(p2RestVec, bodyAft));
                    float th3Rest = Mathf.Atan2(Vector2.Dot(p3RestVec, aftSide), Vector2.Dot(p3RestVec, bodyAft));

                    if (u <= windupEnd)
                    {
                        float w = SmoothStep01(u / windupEnd);
                        p1Target = PolarFlank(p0, bodyAft, aftSide, Mathf.Lerp(th1Rest, th1Coil, w), Mathf.Lerp(r1Rest, r1Coil, w));
                        p2Target = PolarFlank(p0, bodyAft, aftSide, Mathf.Lerp(th2Rest, th2Coil, w), Mathf.Lerp(r2Rest, r2Coil, w));
                        p3Target = PolarFlank(p0, bodyAft, aftSide, Mathf.Lerp(th3Rest, th3Coil, w), Mathf.Lerp(r3Rest, r3Coil, w));
                        freqScale = Mathf.Lerp(1f, def.strikeFreqBoost * 0.78f, w);
                    }
                    else if (u <= snapEnd)
                    {
                        float s = Mathf.Clamp01((u - windupEnd) / (snapEnd - windupEnd));
                        float s1 = SmoothStep01(Mathf.Clamp01(s / 0.50f));
                        float s2 = SmoothStep01(Mathf.Clamp01((s - 0.08f) / 0.60f));
                        float s3 = SmoothStep01(Mathf.Clamp01((s - 0.14f) / 0.68f));

                        p1Target = PolarFlank(p0, bodyAft, aftSide, Mathf.Lerp(th1Coil, th1Follow, s1), Mathf.Lerp(r1Coil, 0.34f * hitRadius, s1));
                        p2Target = PolarFlank(p0, bodyAft, aftSide, Mathf.Lerp(th2Coil, th2Follow, s2), Mathf.Lerp(r2Coil, 0.68f * hitRadius, s2));
                        p3Target = PolarFlank(p0, bodyAft, aftSide, Mathf.Lerp(th3Coil, th3Follow, s3), Mathf.Lerp(r3Coil, hitRadius, s3));

                        freqScale = def.strikeFreqBoost;
                        minDampBoost = Mathf.Lerp(0f, 0.28f, s3);
                        effectiveLagCoupling = def.waveLagCoupling * Mathf.Lerp(1f, 0.15f, s3);
                    }
                    else
                    {
                        float r = SmoothStep01((u - snapEnd) / Mathf.Max(0.05f, 1f - snapEnd));
                        float r1 = SmoothStep01(Mathf.Clamp01(r / 0.75f));
                        float r2 = SmoothStep01(Mathf.Clamp01((r - 0.08f) / 0.82f));
                        float r3 = SmoothStep01(Mathf.Clamp01((r - 0.14f) / 0.86f));
                        p1Target = PolarFlank(p0, bodyAft, aftSide, Mathf.Lerp(th1Follow, th1Rest, r1), Mathf.Lerp(0.34f * hitRadius, r1Rest, r1));
                        p2Target = PolarFlank(p0, bodyAft, aftSide, Mathf.Lerp(th2Follow, th2Rest, r2), Mathf.Lerp(0.68f * hitRadius, r2Rest, r2));
                        p3Target = PolarFlank(p0, bodyAft, aftSide, Mathf.Lerp(th3Follow, th3Rest, r3), Mathf.Lerp(hitRadius, r3Rest, r3));
                        freqScale = Mathf.Lerp(def.strikeFreqBoost, 1.15f, r);
                        minDampBoost = Mathf.Lerp(0.28f, 0.08f, r);
                    }
                }
                else
                {
                    Vector2 rawTargetXZ = ctx.HasAttackTarget
                        ? ctx.AttackTargetWorldXZ
                        : (p0 + bodyForward * (lTotal * def.defaultStrikeRangeFactor) + bodyRight * def.defaultStrikeSideCells);

                    Vector2 toTarg = rawTargetXZ - p0;
                    float dist = toTarg.magnitude;
                    Vector2 strikeDir = dist > 0.05f ? (toTarg / dist) : bodyForward;
                    float clampedDist = Mathf.Clamp(dist, 0.28f * lTotal, 0.96f * lTotal);
                    Vector2 clampedTargetXZ = p0 + strikeDir * clampedDist;

                    Vector2 flankPref = Mathf.Abs(def.trailingLateralSign) > 0.01f
                        ? (bodyRight * def.trailingLateralSign)
                        : bodyRight;

                    float thetaTarg = Mathf.Atan2(Vector2.Dot(strikeDir, flankPref), Vector2.Dot(strikeDir, bodyForward));
                    if (thetaTarg < -Mathf.PI * 0.5f)
                    {
                        thetaTarg += 2f * Mathf.PI;
                    }

                    Vector2 p1RestVec = p1Target - p0;
                    Vector2 p2RestVec = p2Target - p0;
                    Vector2 p3RestVec = p3Target - p0;
                    float r1Rest = Mathf.Max(0.20f * lTotal, p1RestVec.magnitude);
                    float r2Rest = Mathf.Max(0.45f * lTotal, p2RestVec.magnitude);
                    float r3Rest = Mathf.Max(0.75f * lTotal, p3RestVec.magnitude);
                    float th1Rest = Mathf.Atan2(Vector2.Dot(p1RestVec, flankPref), Vector2.Dot(p1RestVec, bodyForward));
                    if (th1Rest < 0f) th1Rest += 2f * Mathf.PI;
                    float th2Rest = Mathf.Atan2(Vector2.Dot(p2RestVec, flankPref), Vector2.Dot(p2RestVec, bodyForward));
                    if (th2Rest < 0f) th2Rest += 2f * Mathf.PI;
                    float th3Rest = Mathf.Atan2(Vector2.Dot(p3RestVec, flankPref), Vector2.Dot(p3RestVec, bodyForward));
                    if (th3Rest < 0f) th3Rest += 2f * Mathf.PI;

                    float th1Coil = 2.02f, r1Coil = 0.36f * lTotal;
                    float th2Coil = 2.28f, r2Coil = 0.68f * lTotal;
                    float th3Coil = 2.52f, r3Coil = 0.86f * lTotal;

                    float slack = Mathf.Max(0f, lTotal - clampedDist);
                    float archAngle = Mathf.Clamp(0.42f * Mathf.Sqrt(slack / Mathf.Max(1f, lTotal)) + 0.08f, 0.08f, 0.50f);

                    if (u <= windupEnd)
                    {
                        float w = SmoothStep01(u / windupEnd);
                        p1Target = PolarFlank(p0, bodyForward, flankPref, Mathf.Lerp(th1Rest, th1Coil, w), Mathf.Lerp(r1Rest, r1Coil, w));
                        p2Target = PolarFlank(p0, bodyForward, flankPref, Mathf.Lerp(th2Rest, th2Coil, w), Mathf.Lerp(r2Rest, r2Coil, w));
                        p3Target = PolarFlank(p0, bodyForward, flankPref, Mathf.Lerp(th3Rest, th3Coil, w), Mathf.Lerp(r3Rest, r3Coil, w));
                        freqScale = Mathf.Lerp(1f, def.strikeFreqBoost * 0.75f, w);
                    }
                    else if (u <= snapEnd)
                    {
                        float s = Mathf.Clamp01((u - windupEnd) / (snapEnd - windupEnd));
                        float s1 = SmoothStep01(Mathf.Clamp01(s / 0.52f));
                        float s2 = SmoothStep01(Mathf.Clamp01((s - 0.08f) / 0.62f));
                        float s3 = SmoothStep01(Mathf.Clamp01((s - 0.15f) / 0.65f));

                        float th1Hit = thetaTarg + archAngle * (1f - 0.45f * s1);
                        float th2Hit = thetaTarg + archAngle * 0.55f * (1f - 0.55f * s2);
                        p1Target = PolarFlank(p0, bodyForward, flankPref, Mathf.Lerp(th1Coil, th1Hit, s1), Mathf.Lerp(r1Coil, 0.34f * clampedDist, s1));
                        p2Target = PolarFlank(p0, bodyForward, flankPref, Mathf.Lerp(th2Coil, th2Hit, s2), Mathf.Lerp(r2Coil, 0.68f * clampedDist, s2));
                        Vector2 p3Arc = PolarFlank(p0, bodyForward, flankPref, Mathf.Lerp(th3Coil, thetaTarg, s3), Mathf.Lerp(r3Coil, clampedDist, s3));
                        p3Target = Vector2.Lerp(p3Arc, clampedTargetXZ, s3 * s3);

                        freqScale = def.strikeFreqBoost;
                        minDampBoost = Mathf.Lerp(0f, 0.36f, s3);
                        effectiveLagCoupling = def.waveLagCoupling * Mathf.Lerp(1f, 0.18f, s3);
                    }
                    else
                    {
                        float r = SmoothStep01((u - snapEnd) / Mathf.Max(0.05f, 1f - snapEnd));
                        float r1 = SmoothStep01(Mathf.Clamp01(r / 0.75f));
                        float r2 = SmoothStep01(Mathf.Clamp01((r - 0.08f) / 0.82f));
                        float r3 = SmoothStep01(Mathf.Clamp01((r - 0.14f) / 0.86f));
                        float th1Hit = thetaTarg + archAngle * 0.55f;
                        float th2Hit = thetaTarg + archAngle * 0.25f;
                        p1Target = PolarFlank(p0, bodyForward, flankPref, Mathf.Lerp(th1Hit, th1Rest, r1), Mathf.Lerp(0.34f * clampedDist, r1Rest, r1));
                        p2Target = PolarFlank(p0, bodyForward, flankPref, Mathf.Lerp(th2Hit, th2Rest, r2), Mathf.Lerp(0.68f * clampedDist, r2Rest, r2));
                        p3Target = PolarFlank(p0, bodyForward, flankPref, Mathf.Lerp(thetaTarg, th3Rest, r3), Mathf.Lerp(clampedDist, r3Rest, r3));
                        freqScale = Mathf.Lerp(def.strikeFreqBoost, 1.15f, r);
                        minDampBoost = Mathf.Lerp(0.34f, 0.08f, r);
                    }
                }
            }

            bool needsInit = !initialized || lastActiveCount != kCount;
            if (needsInit)
            {
                p1 = p1Target;
                p2 = p2Target;
                p3 = p3Target;
                prevP0 = p0;
                v1 = Vector2.zero;
                v2 = Vector2.zero;
                v3 = Vector2.zero;
                prevBowNormal = Vector2.zero;
                smoothedBowP1 = Vector2.zero;
                smoothedBowP2 = Vector2.zero;
                initialized = true;
                lastActiveCount = kCount;
            }
            else if (deltaTicks <= 0f)
            {
                Vector2 p0Shift = p0 - prevP0;
                if (p0Shift.sqrMagnitude > 1e-10f)
                {
                    p1 += p0Shift;
                    p2 += p0Shift;
                    p3 += p0Shift;
                    prevP0 = p0;
                }
            }
            else
            {
                float dt = Mathf.Clamp(deltaTicks, 0.001f, 6f);
                float maxFreq = Mathf.Max(def.p1Frequency, Mathf.Max(def.p2Frequency, def.p3Frequency)) * Mathf.Max(1f, freqScale);
                float maxOmega = 2f * Mathf.PI * Mathf.Max(0.01f, maxFreq);
                float maxSafeStep = Mathf.Min(0.5f, 0.40f / Mathf.Max(0.5f, maxOmega));
                int steps = Mathf.Clamp(Mathf.CeilToInt(dt / maxSafeStep), 1, 48);
                float h = dt / steps;
                Vector2 dp0Step = (p0 - prevP0) / steps;

                float w1 = 2f * Mathf.PI * Mathf.Max(0.01f, def.p1Frequency * freqScale);
                float w2 = 2f * Mathf.PI * Mathf.Max(0.01f, def.p2Frequency * freqScale);
                float w3 = 2f * Mathf.PI * Mathf.Max(0.01f, def.p3Frequency * freqScale);
                float z1 = Mathf.Max(0.05f, def.p1Damping + minDampBoost);
                float z2 = Mathf.Max(0.05f, def.p2Damping + minDampBoost);
                float z3 = Mathf.Max(0.05f, def.p3Damping + minDampBoost);

                for (int s = 0; s < steps; s++)
                {
                    p1 += 0.35f * dp0Step;
                    p2 += 0.10f * dp0Step;

                    Vector2 a1 = w1 * w1 * (p1Target - p1) - 2f * z1 * w1 * v1;
                    v1 += a1 * h;
                    p1 += v1 * h;
                    ClampHandleDistance(p0, ref p1, 0.62f * lTotal);

                    Vector2 t2 = p2Target + effectiveLagCoupling * (p1 - p1Target);
                    Vector2 a2 = w2 * w2 * (t2 - p2) - 2f * z2 * w2 * v2 + 0.35f * def.whipCrackGain * w2 * (v1 - v2);
                    v2 += a2 * h;
                    p2 += v2 * h;
                    ClampHandleDistance(p0, ref p2, 0.90f * lTotal);

                    Vector2 t3 = p3Target + effectiveLagCoupling * (p2 - p2Target);
                    Vector2 a3 = w3 * w3 * (t3 - p3) - 2f * z3 * w3 * v3 + def.whipCrackGain * w3 * v2;
                    v3 += a3 * h;
                    p3 += v3 * h;

                    Vector2 tipVec = p3 - p0;
                    float tipDist = tipVec.magnitude;
                    if (tipDist > lTotal && tipDist > 1e-5f)
                    {
                        Vector2 rHat = tipVec / tipDist;
                        p3 = p0 + rHat * lTotal;
                        float vr = Vector2.Dot(v3, rHat);
                        if (vr > 0f)
                        {
                            v3 -= vr * rHat;
                        }
                    }
                }
                prevP0 = p0;
            }

            float baseArcLen = PopulateAndMeasureCurve(p0, p1, p2, p3);
            Vector2 desiredBowP1 = Vector2.zero;
            Vector2 desiredBowP2 = Vector2.zero;
            float bowThreshold = (isStrike ? 0.995f : 0.982f) * lTotal;
            if (baseArcLen < bowThreshold && lTotal > 1e-4f)
            {
                Vector2 chord = p3 - p0;
                float rootAxisOffDeg = ResolveBoneDistalAxisOffsetDeg(nodes, indices, 0, kCount);
                Vector2 chordDir = chord.sqrMagnitude > 1e-6f
                    ? chord.normalized
                    : SkeletalKinematics.RotateCW(Vector2.up, parentWorldDeg + nodes[indices[0]].baseAngle + rootAxisOffDeg);
                Vector2 nHat = new Vector2(-chordDir.y, chordDir.x);

                Vector2 midCtrl = 0.5f * (p1 + p2) - 0.5f * (p0 + p3);
                float side = Vector2.Dot(midCtrl, nHat);
                if (isStrike)
                {
                    Vector2 flankPref = Mathf.Abs(def.trailingLateralSign) > 0.01f
                        ? (bodyRight * def.trailingLateralSign)
                        : bodyRight;
                    if (Vector2.Dot(nHat, flankPref) < 0f)
                    {
                        nHat = -nHat;
                    }
                }
                else
                {
                    float hysteresis = baseArcLen < 0.92f * lTotal ? 0.35f * lTotal : 0.12f * lTotal;
                    if (prevBowNormal.sqrMagnitude > 0.5f && (baseArcLen < 0.88f * lTotal || Mathf.Abs(side) < hysteresis))
                    {
                        if (Vector2.Dot(nHat, prevBowNormal) < 0f)
                        {
                            nHat = -nHat;
                        }
                    }
                    else
                    {
                        if (Mathf.Abs(side) < 1e-3f)
                        {
                            side = Mathf.Abs(def.trailingLateralSign) > 0.01f
                                ? Vector2.Dot(bodyRight * def.trailingLateralSign, nHat)
                                : Vector2.Dot(p0 - rootWorldXZ, nHat);
                        }
                        if (side < 0f)
                        {
                            nHat = -nHat;
                        }
                    }
                }
                prevBowNormal = nHat;

                float p2Sign = isStrike ? 0.45f : -0.38f;
                float targetLen = (isStrike ? 1.005f : 0.992f) * lTotal;
                float lo = 0f;
                float hi = lTotal * 0.9f;
                for (int it = 0; it < 8; it++)
                {
                    float mid = 0.5f * (lo + hi);
                    float len = PopulateAndMeasureCurve(p0, p1 + nHat * mid, p2 + nHat * (p2Sign * mid), p3);
                    if (len < targetLen)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
                desiredBowP1 = nHat * hi;
                desiredBowP2 = nHat * (p2Sign * hi);
            }
            else if (baseArcLen > 0.996f * lTotal)
            {
                prevBowNormal = Vector2.zero;
            }

            if (needsInit)
            {
                smoothedBowP1 = desiredBowP1;
                smoothedBowP2 = desiredBowP2;
            }
            else if (deltaTicks > 0f)
            {
                float bowRate = isStrike ? 0.18f : 0.032f;
                float maxBowStep = bowRate * lTotal * Mathf.Clamp(deltaTicks, 0.05f, 6f);
                smoothedBowP1 = Vector2.MoveTowards(smoothedBowP1, desiredBowP1, maxBowStep);
                smoothedBowP2 = Vector2.MoveTowards(smoothedBowP2, desiredBowP2, maxBowStep);
            }

            effP1 = p1 + smoothedBowP1;
            effP2 = p2 + smoothedBowP2;
            PopulateAndMeasureCurve(p0, effP1, effP2, p3);

            MarchBonesAlongCurve(nodes, indices, kCount, boneLengths, parentWorldDeg, inoutLocalPoses);
        }

        private static float SmoothStep01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static Vector2 PolarFlank(Vector2 origin, Vector2 fwd, Vector2 flankOut, float thetaRad, float radius)
        {
            return origin + (fwd * Mathf.Cos(thetaRad) + flankOut * Mathf.Sin(thetaRad)) * radius;
        }

        private float PopulateAndMeasureCurve(Vector2 cp0, Vector2 cp1, Vector2 cp2, Vector2 cp3)
        {
            float total = 0f;
            curvePts[0] = cp0;
            for (int i = 1; i <= CurveSamples; i++)
            {
                float u = i / (float)CurveSamples;
                Vector2 pt = EvalCubicBezier(cp0, cp1, cp2, cp3, u);
                curvePts[i] = pt;
                total += Vector2.Distance(curvePts[i - 1], pt);
            }
            return total;
        }

        private float ResolveBoneDistalAxisOffsetDeg(BoneNodeDef[] nodes, int[] indices, int k, int kCount)
        {
            int bIdx = indices[k];
            nodes[bIdx].GetResolvedSelfParams(GiantBoneViewSector.Dorsal, out Vector2 drawSize, out Vector2 pivotUV, out _);
            Vector2 targetUV = k < kCount - 1
                ? nodes[indices[k + 1]].GetResolvedParentSocketUV(GiantBoneViewSector.Dorsal)
                : def.tipUV;
            float lx = (targetUV.x - pivotUV.x) * drawSize.x;
            float lz = (targetUV.y - pivotUV.y) * drawSize.y;
            return (lx * lx + lz * lz) > 1e-8f ? Mathf.Atan2(lx, lz) * Mathf.Rad2Deg : 0f;
        }

        private void MarchBonesAlongCurve(
            BoneNodeDef[] nodes,
            int[] indices,
            int kCount,
            Span<float> boneLengths,
            float baseParentWorldDeg,
            BoneLocalPose[] inoutLocalPoses)
        {
            Vector2 curJoint = p0;
            int segIdx = 0;
            float segT = 0f;
            float prevBoneWorldDeg = baseParentWorldDeg;
            float prevChordWorldDeg = baseParentWorldDeg;

            for (int k = 0; k < kCount; k++)
            {
                int bIdx = indices[k];
                float axisOffsetDeg = ResolveBoneDistalAxisOffsetDeg(nodes, indices, k, kCount);
                float radius = boneLengths[k];
                Vector2 nextJoint = FindNextChordIntersection(curJoint, radius, ref segIdx, ref segT);
                Vector2 delta = nextJoint - curJoint;
                float desiredChordWorldDeg = delta.sqrMagnitude > 1e-8f
                    ? Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg
                    : (k == 0
                        ? baseParentWorldDeg + nodes[bIdx].baseAngle + axisOffsetDeg
                        : prevChordWorldDeg);

                if (k > 0)
                {
                    float bend = Mathf.DeltaAngle(prevChordWorldDeg, desiredChordWorldDeg);
                    float maxBend = Mathf.Max(5f, def.maxJointBendDeg);
                    if (Mathf.Abs(bend) > maxBend)
                    {
                        desiredChordWorldDeg = prevChordWorldDeg + Mathf.Sign(bend) * maxBend;
                        nextJoint = curJoint + SkeletalKinematics.RotateCW(new Vector2(0f, radius), desiredChordWorldDeg);
                    }
                }

                float desiredBoneWorldDeg = desiredChordWorldDeg - axisOffsetDeg;
                float actualParentWorldDeg = k == 0 ? baseParentWorldDeg : prevBoneWorldDeg;
                inoutLocalPoses[bIdx].angle = Mathf.DeltaAngle(actualParentWorldDeg + nodes[bIdx].baseAngle, desiredBoneWorldDeg);

                prevBoneWorldDeg = SkeletalKinematics.NormalizeAngle360(desiredBoneWorldDeg);
                prevChordWorldDeg = SkeletalKinematics.NormalizeAngle360(desiredChordWorldDeg);
                curJoint = nextJoint;
            }
        }

        private Vector2 FindNextChordIntersection(Vector2 origin, float radius, ref int segIdx, ref float segT)
        {
            float rSq = radius * radius;
            while (segIdx < CurveSamples)
            {
                Vector2 a = curvePts[segIdx];
                Vector2 b = curvePts[segIdx + 1];
                Vector2 ab = b - a;
                Vector2 ao = a - origin;

                float endDistSq = (b - origin).sqrMagnitude;
                if (endDistSq >= rSq)
                {
                    float qa = Vector2.Dot(ab, ab);
                    if (qa > 1e-9f)
                    {
                        float qb = 2f * Vector2.Dot(ao, ab);
                        float qc = Vector2.Dot(ao, ao) - rSq;
                        float disc = qb * qb - 4f * qa * qc;
                        if (disc >= 0f)
                        {
                            float sqrtD = Mathf.Sqrt(disc);
                            float t1 = (-qb - sqrtD) / (2f * qa);
                            float t2 = (-qb + sqrtD) / (2f * qa);
                            float chosen = -1f;
                            if (t1 >= segT - 1e-4f && t1 <= 1f + 1e-4f)
                            {
                                chosen = Mathf.Clamp01(Mathf.Max(segT, t1));
                            }
                            else if (t2 >= segT - 1e-4f && t2 <= 1f + 1e-4f)
                            {
                                chosen = Mathf.Clamp01(Mathf.Max(segT, t2));
                            }

                            if (chosen >= 0f)
                            {
                                segT = chosen;
                                return a + ab * chosen;
                            }
                        }
                    }
                }

                segIdx++;
                segT = 0f;
            }

            Vector2 endPt = curvePts[CurveSamples];
            Vector2 endTan = curvePts[CurveSamples] - curvePts[CurveSamples - 1];
            Vector2 dir = endTan.sqrMagnitude > 1e-8f
                ? endTan.normalized
                : (endPt - origin).sqrMagnitude > 1e-8f ? (endPt - origin).normalized : Vector2.up;
            float along = Vector2.Dot(origin - endPt, dir);
            float step = Mathf.Sqrt(Mathf.Max(0f, rSq - (origin - endPt).sqrMagnitude + along * along)) - along;
            return endPt + dir * Mathf.Max(0f, step);
        }

        private static void ClampHandleDistance(Vector2 anchor, ref Vector2 handle, float maxDist)
        {
            Vector2 d = handle - anchor;
            float mag = d.magnitude;
            if (mag > maxDist && mag > 1e-5f)
            {
                handle = anchor + (d / mag) * maxDist;
            }
        }

        private static void SamplePolylineThirds(
            ReadOnlySpan<Vector2> qPts,
            ReadOnlySpan<float> segLens,
            int kCount,
            float lTotal,
            out Vector2 qOneThird,
            out Vector2 qTwoThirds,
            out Vector2 qEnd)
        {
            qEnd = qPts[kCount];
            if (lTotal <= 1e-5f)
            {
                qOneThird = qPts[0];
                qTwoThirds = qEnd;
                return;
            }

            qOneThird = SampleAtArcLength(qPts, segLens, kCount, lTotal / 3f);
            qTwoThirds = SampleAtArcLength(qPts, segLens, kCount, (2f * lTotal) / 3f);
        }

        private static Vector2 SampleAtArcLength(ReadOnlySpan<Vector2> qPts, ReadOnlySpan<float> segLens, int kCount, float targetS)
        {
            float accum = 0f;
            for (int k = 0; k < kCount; k++)
            {
                float len = segLens[k];
                if (accum + len >= targetS && len > 1e-5f)
                {
                    float frac = Mathf.Clamp01((targetS - accum) / len);
                    return Vector2.LerpUnclamped(qPts[k], qPts[k + 1], frac);
                }
                accum += len;
            }
            return qPts[kCount];
        }

        private static bool IsBoneOrAncestorMissing(BoneNodeDef[] nodes, int idx, ReadOnlySpan<bool> missingMask)
        {
            int cur = idx;
            while (cur >= 0 && cur < nodes.Length)
            {
                if (cur < missingMask.Length && missingMask[cur] && nodes[cur].hideWhenMissing)
                {
                    return true;
                }
                cur = nodes[cur].parentRuntimeIndex;
            }
            return false;
        }
    }
}
