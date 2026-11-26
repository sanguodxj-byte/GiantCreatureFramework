using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public enum AnimEasing
    {
        Linear,
        SmoothStep,
        EaseInQuad,
        EaseOutQuad,
        EaseInCubic,
        EaseOutCubic,
        EaseOutBack,
        Sine,
        CatmullRom
    }

    public enum GiantAnimVfxPreset
    {
        None = 0,
        FootstepDust = 1,
        HeavyGroundSlam = 2,
        MuzzleBlast = 3,
        SlashArc = 4,
        WhipCrackSnap = 5,
        PlasmaBurst = 6,
        RoarShockwave = 7
    }

    public class GiantAnimEventDef
    {
        public float tick;
        public string boneId;
        public string socketId;
        public string emitterId;
        public bool useBoneMuzzle = false;
        public float angleOffset = 0f;
        public Vector2 uv = new Vector2(0.5f, 0.5f);
        public Vector2 pixel = new Vector2(-9999f, -9999f);
        public GiantAnimVfxPreset vfxPreset = GiantAnimVfxPreset.None;
        public FleckDef fleckDef;
        public ThingDef moteDef;
        public EffecterDef effecterDef;
        public SoundDef soundDef;
        public float scale = 1f;
        public Color color = Color.white;
        public float cameraShake = 0f;

        public Vector2 ResolveUV(int textureResolution)
        {
            if (pixel.x > -9000f)
            {
                float res = Mathf.Max(1f, textureResolution);
                return new Vector2(pixel.x / res, 1f - (pixel.y / res));
            }
            return uv;
        }

        public void ResolveBonePoint(BoneNodeDef boneDef, out Vector2 resolvedUV, out float resolvedAngleOffsetDeg)
        {
            ResolveBonePoint(boneDef, GiantBoneViewSector.Dorsal, out resolvedUV, out resolvedAngleOffsetDeg);
        }

        public void ResolveBonePoint(BoneNodeDef boneDef, GiantBoneViewSector sector, out Vector2 resolvedUV, out float resolvedAngleOffsetDeg)
        {
            resolvedAngleOffsetDeg = angleOffset;
            if (boneDef == null)
            {
                resolvedUV = uv;
                return;
            }

            if (pixel.x > -9000f)
            {
                resolvedUV = ResolveUV(boneDef.textureResolution);
                return;
            }

            if (!string.IsNullOrEmpty(socketId) && boneDef.TryGetEmitterSocket(socketId, sector, out GiantBoneEmitterSocketDef sock))
            {
                resolvedUV = sock.resolvedUV;
                resolvedAngleOffsetDeg = sock.angleOffset + angleOffset;
                return;
            }

            if (useBoneMuzzle)
            {
                BoneDirectionalVariantDef variant = boneDef.GetVariant(sector);
                resolvedUV = variant != null ? variant.resolvedMuzzleUV : boneDef.resolvedMuzzleUV;
                resolvedAngleOffsetDeg = (variant != null ? variant.resolvedMuzzleAngleOffset : boneDef.muzzleAngleOffset) + angleOffset;
                return;
            }

            resolvedUV = uv;
        }
    }

    public class BoneKeyframe
    {
        public float tick;
        public float angle = 0f;
        public float sagittalPitch = 0f;
        public float coronalFlap = 0f;
        public Vector2 offset = Vector2.zero;
        public Vector2 scale = Vector2.one;
        public AnimEasing easing = AnimEasing.SmoothStep;
    }

    public class BoneAnimTrack
    {
        public string nodeId;
        public List<BoneKeyframe> keyframes = new List<BoneKeyframe>();

        [Unsaved]
        private bool sorted;

        public void EnsureSorted()
        {
            if (sorted || keyframes == null)
            {
                return;
            }
            keyframes.Sort((a, b) => a.tick.CompareTo(b.tick));
            sorted = true;
        }

        public BoneLocalPose SampleAt(float tick, float durationTicks, bool loop)
        {
            EnsureSorted();
            if (keyframes == null || keyframes.Count == 0)
            {
                return BoneLocalPose.Identity;
            }
            if (keyframes.Count == 1)
            {
                BoneKeyframe single = keyframes[0];
                return new BoneLocalPose(single.offset, single.angle, Vector2.one, single.sagittalPitch, single.coronalFlap);
            }

            float t = tick;
            if (durationTicks > 0f)
            {
                if (loop)
                {
                    t = tick % durationTicks;
                    if (t < 0f)
                    {
                        t += durationTicks;
                    }
                }
                else
                {
                    t = Mathf.Clamp(tick, 0f, durationTicks);
                }
            }

            int lastIdx = keyframes.Count - 1;
            if (t <= keyframes[0].tick)
            {
                if (loop && durationTicks > keyframes[lastIdx].tick && t < keyframes[0].tick)
                {
                    BoneKeyframe a = keyframes[lastIdx];
                    BoneKeyframe b = keyframes[0];
                    float span = (durationTicks - a.tick) + b.tick;
                    float elapsed = (durationTicks - a.tick) + t;
                    float frac = span > 1e-4f ? elapsed / span : 1f;
                    return b.easing == AnimEasing.CatmullRom
                        ? InterpolateCatmullRom(lastIdx, 0, span, frac, durationTicks, loop: true)
                        : Interpolate(a, b, frac);
                }
                BoneKeyframe first = keyframes[0];
                return new BoneLocalPose(first.offset, first.angle, Vector2.one, first.sagittalPitch, first.coronalFlap);
            }

            if (t >= keyframes[lastIdx].tick)
            {
                if (loop && durationTicks > keyframes[lastIdx].tick)
                {
                    BoneKeyframe a = keyframes[lastIdx];
                    BoneKeyframe b = keyframes[0];
                    float span = (durationTicks - a.tick) + b.tick;
                    float frac = span > 1e-4f ? (t - a.tick) / span : 1f;
                    return b.easing == AnimEasing.CatmullRom
                        ? InterpolateCatmullRom(lastIdx, 0, span, frac, durationTicks, loop: true)
                        : Interpolate(a, b, frac);
                }
                BoneKeyframe last = keyframes[lastIdx];
                return new BoneLocalPose(last.offset, last.angle, Vector2.one, last.sagittalPitch, last.coronalFlap);
            }

            for (int i = 0; i < lastIdx; i++)
            {
                BoneKeyframe k0 = keyframes[i];
                BoneKeyframe k1 = keyframes[i + 1];
                if (t >= k0.tick && t <= k1.tick)
                {
                    float span = k1.tick - k0.tick;
                    float frac = span > 1e-4f ? (t - k0.tick) / span : 1f;
                    return k1.easing == AnimEasing.CatmullRom
                        ? InterpolateCatmullRom(i, i + 1, span, frac, durationTicks, loop)
                        : Interpolate(k0, k1, frac);
                }
            }

            BoneKeyframe fallback = keyframes[lastIdx];
            return new BoneLocalPose(fallback.offset, fallback.angle, Vector2.one, fallback.sagittalPitch, fallback.coronalFlap);
        }

        private BoneLocalPose InterpolateCatmullRom(
            int idxA,
            int idxB,
            float spanAB,
            float rawT,
            float durationTicks,
            bool loop)
        {
            float u = Mathf.Clamp01(rawT);
            int count = keyframes.Count;
            int lastIdx = count - 1;
            float dt = Mathf.Max(1e-3f, spanAB);

            BoneKeyframe kA = keyframes[idxA];
            BoneKeyframe kB = keyframes[idxB];
            if (count < 3)
            {
                return Interpolate(kA, kB, u);
            }

            bool hasDuplicateLoopSeam = loop
                && durationTicks > 0f
                && Mathf.Abs((durationTicks - keyframes[lastIdx].tick) + keyframes[0].tick) < 1e-3f;

            BoneKeyframe kPrev;
            float dtPrev;
            if (idxA > 0)
            {
                kPrev = keyframes[idxA - 1];
                dtPrev = Mathf.Max(1e-3f, kA.tick - kPrev.tick);
            }
            else if (loop)
            {
                if (hasDuplicateLoopSeam && lastIdx >= 1)
                {
                    kPrev = keyframes[lastIdx - 1];
                    dtPrev = Mathf.Max(1e-3f, keyframes[lastIdx].tick - kPrev.tick);
                }
                else
                {
                    kPrev = keyframes[lastIdx];
                    dtPrev = Mathf.Max(1e-3f, (durationTicks - kPrev.tick) + kA.tick);
                }
            }
            else
            {
                kPrev = null;
                dtPrev = dt;
            }

            BoneKeyframe kNext;
            float dtNext;
            if (idxB < lastIdx)
            {
                kNext = keyframes[idxB + 1];
                dtNext = Mathf.Max(1e-3f, kNext.tick - kB.tick);
            }
            else if (loop)
            {
                if (hasDuplicateLoopSeam && count >= 2)
                {
                    kNext = keyframes[1];
                    dtNext = Mathf.Max(1e-3f, kNext.tick - keyframes[0].tick);
                }
                else
                {
                    kNext = keyframes[0];
                    dtNext = Mathf.Max(1e-3f, (durationTicks - kB.tick) + kNext.tick);
                }
            }
            else
            {
                kNext = null;
                dtNext = dt;
            }

            float a1 = kA.angle;
            float a2 = a1 + Mathf.DeltaAngle(a1, kB.angle);
            float a0 = kPrev != null ? (a1 - Mathf.DeltaAngle(kPrev.angle, a1)) : (a1 - (a2 - a1));
            float a3 = kNext != null ? (a2 + Mathf.DeltaAngle(a2, kNext.angle)) : (a2 + (a2 - a1));

            float m1Ang = ((a2 - a0) / (dtPrev + dt)) * dt;
            float m2Ang = ((a3 - a1) / (dt + dtNext)) * dt;

            Vector2 o1 = kA.offset;
            Vector2 o2 = kB.offset;
            Vector2 o0 = kPrev != null ? kPrev.offset : (o1 - (o2 - o1));
            Vector2 o3 = kNext != null ? kNext.offset : (o2 + (o2 - o1));

            Vector2 m1Off = ((o2 - o0) / (dtPrev + dt)) * dt;
            Vector2 m2Off = ((o3 - o1) / (dt + dtNext)) * dt;

            float p1 = kA.sagittalPitch;
            float p2 = kB.sagittalPitch;
            float p0 = kPrev != null ? kPrev.sagittalPitch : (p1 - (p2 - p1));
            float p3 = kNext != null ? kNext.sagittalPitch : (p2 + (p2 - p1));
            float m1Pit = ((p2 - p0) / (dtPrev + dt)) * dt;
            float m2Pit = ((p3 - p1) / (dt + dtNext)) * dt;

            float f1 = kA.coronalFlap;
            float f2 = kB.coronalFlap;
            float f0 = kPrev != null ? kPrev.coronalFlap : (f1 - (f2 - f1));
            float f3 = kNext != null ? kNext.coronalFlap : (f2 + (f2 - f1));
            float m1Flap = ((f2 - f0) / (dtPrev + dt)) * dt;
            float m2Flap = ((f3 - f1) / (dt + dtNext)) * dt;

            float u2 = u * u;
            float u3 = u2 * u;
            float h00 = 2f * u3 - 3f * u2 + 1f;
            float h10 = u3 - 2f * u2 + u;
            float h01 = -2f * u3 + 3f * u2;
            float h11 = u3 - u2;

            float outAngle = h00 * a1 + h10 * m1Ang + h01 * a2 + h11 * m2Ang;
            Vector2 outOffset = h00 * o1 + h10 * m1Off + h01 * o2 + h11 * m2Off;
            float outPitch = h00 * p1 + h10 * m1Pit + h01 * p2 + h11 * m2Pit;
            float outFlap = h00 * f1 + h10 * m1Flap + h01 * f2 + h11 * m2Flap;
            return new BoneLocalPose(outOffset, outAngle, Vector2.one, outPitch, outFlap);
        }

        private static BoneLocalPose Interpolate(BoneKeyframe a, BoneKeyframe b, float rawT)
        {
            float t = ApplyEasing(Mathf.Clamp01(rawT), b.easing);
            Vector2 offset = Vector2.LerpUnclamped(a.offset, b.offset, t);
            float angle = a.angle + Mathf.DeltaAngle(a.angle, b.angle) * t;
            float pitch = Mathf.LerpUnclamped(a.sagittalPitch, b.sagittalPitch, t);
            float flap = Mathf.LerpUnclamped(a.coronalFlap, b.coronalFlap, t);
            return new BoneLocalPose(offset, angle, Vector2.one, pitch, flap);
        }

        public static float ApplyEasing(float t, AnimEasing easing)
        {
            switch (easing)
            {
                case AnimEasing.SmoothStep:
                    return t * t * (3f - 2f * t);
                case AnimEasing.EaseInQuad:
                    return t * t;
                case AnimEasing.EaseOutQuad:
                    return 1f - (1f - t) * (1f - t);
                case AnimEasing.EaseInCubic:
                    return t * t * t;
                case AnimEasing.EaseOutCubic:
                {
                    float inv = 1f - t;
                    return 1f - inv * inv * inv;
                }
                case AnimEasing.EaseOutBack:
                {
                    const float c1 = 1.70158f;
                    const float c3 = c1 + 1f;
                    float u = t - 1f;
                    return 1f + c3 * u * u * u + c1 * u * u;
                }
                case AnimEasing.Sine:
                    return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
                case AnimEasing.CatmullRom:
                default:
                    return t;
            }
        }
    }

    public class GiantAnimDef : Def
    {
        public float durationTicks = 60f;
        public bool loop = true;
        public int blendInTicks = 8;

        /// <summary>
        /// Optional tag matched against Verb tool label / linked nodeId for automatic melee attack animation selection.
        /// </summary>
        public string triggerToolTag;

        /// <summary>
        /// Optional subtree root bone nodeId (e.g. "tail_seg1") enforcing 100% anatomical isolation:
        /// all tracks in this clip must target isolatedSubtreeRootBoneId or its descendants, leaving all other body parts untouched.
        /// </summary>
        public string isolatedSubtreeRootBoneId;

        /// <summary>
        /// Optional primary acting body-part subtree root bone nodeId (e.g. "neck_base" for a bite, "wing_shoulder_left" for a wing slash).
        /// Tracks inside this subtree play at 100% amplitude, while coupled body motions outside this subtree are scaled by
        /// GiantRigDef.ResolvePartAnimBodyCoupling(pawnBodySize, coupledBodyMotionFactor).
        /// </summary>
        public string actingSubtreeRootBoneId;

        /// <summary>
        /// Optional per-clip override [0..1] for non-acting body motion coupling; negative (-1) defers to GiantRigDef.partAnimBodyCoupling / BodySize scaling.
        /// </summary>
        public float coupledBodyMotionFactor = -1f;

        /// <summary>
        /// Strike peak tick within [0, durationTicks] synchronized to warmup windup and melee impact handoff.
        /// Negative means derive automatically from impact events or 0.45 * durationTicks.
        /// </summary>
        public float impactTick = -1f;

        public List<BoneAnimTrack> tracks = new List<BoneAnimTrack>();
        public List<GiantAnimEventDef> events = new List<GiantAnimEventDef>();

        /// <summary>
        /// Resolves the tick within [1, durationTicks] at which warmup windup reaches strike release.
        /// </summary>
        public float ResolveImpactTick()
        {
            float dur = Mathf.Max(1f, durationTicks);
            if (impactTick >= 0f)
            {
                return Mathf.Clamp(impactTick, 1f, dur);
            }

            if (events != null)
            {
                float bestTick = -1f;
                for (int i = 0; i < events.Count; i++)
                {
                    GiantAnimEventDef ev = events[i];
                    if (ev == null)
                    {
                        continue;
                    }
                    if (ev.vfxPreset == GiantAnimVfxPreset.SlashArc ||
                        ev.vfxPreset == GiantAnimVfxPreset.HeavyGroundSlam ||
                        ev.vfxPreset == GiantAnimVfxPreset.WhipCrackSnap ||
                        ev.vfxPreset == GiantAnimVfxPreset.PlasmaBurst)
                    {
                        if (ev.tick > 0f && (bestTick < 0f || ev.tick < bestTick))
                        {
                            bestTick = ev.tick;
                        }
                    }
                }
                if (bestTick >= 0f)
                {
                    return Mathf.Clamp(bestTick, 1f, dur);
                }
            }

            return Mathf.Clamp(dur * 0.45f, 1f, dur);
        }

        /// <summary>
        /// Returns true when at least one bone track exhibits meaningful rotational swing or offset travel,
        /// rejecting static or micro-twitch clips that produce invisible silent damage.
        /// </summary>
        public bool HasNonTrivialMotion(float minAngleSpanDeg = 8f, float minRootOffsetCells = 0.25f)
        {
            if (tracks == null || tracks.Count == 0)
            {
                return false;
            }

            float maxAngleSpan = 0f;
            float maxOffsetTravel = 0f;

            for (int t = 0; t < tracks.Count; t++)
            {
                BoneAnimTrack track = tracks[t];
                if (track == null || track.keyframes == null || track.keyframes.Count == 0)
                {
                    continue;
                }

                float minAngle = float.MaxValue;
                float maxAngle = float.MinValue;
                float minPitch = float.MaxValue;
                float maxPitch = float.MinValue;
                float minFlap = float.MaxValue;
                float maxFlap = float.MinValue;
                float minX = float.MaxValue;
                float maxX = float.MinValue;
                float minZ = float.MaxValue;
                float maxZ = float.MinValue;
                float maxSingleOffset = 0f;

                for (int k = 0; k < track.keyframes.Count; k++)
                {
                    BoneKeyframe kf = track.keyframes[k];
                    if (kf == null)
                    {
                        continue;
                    }
                    if (kf.angle < minAngle) minAngle = kf.angle;
                    if (kf.angle > maxAngle) maxAngle = kf.angle;
                    if (kf.sagittalPitch < minPitch) minPitch = kf.sagittalPitch;
                    if (kf.sagittalPitch > maxPitch) maxPitch = kf.sagittalPitch;
                    if (kf.coronalFlap < minFlap) minFlap = kf.coronalFlap;
                    if (kf.coronalFlap > maxFlap) maxFlap = kf.coronalFlap;
                    if (kf.offset.x < minX) minX = kf.offset.x;
                    if (kf.offset.x > maxX) maxX = kf.offset.x;
                    if (kf.offset.y < minZ) minZ = kf.offset.y;
                    if (kf.offset.y > maxZ) maxZ = kf.offset.y;
                    float mag = kf.offset.magnitude;
                    if (mag > maxSingleOffset) maxSingleOffset = mag;
                }

                if (maxAngle >= minAngle)
                {
                    float span = maxAngle - minAngle;
                    if (span > maxAngleSpan) maxAngleSpan = span;
                }
                if (maxPitch >= minPitch)
                {
                    float pSpan = maxPitch - minPitch;
                    if (pSpan > maxAngleSpan) maxAngleSpan = pSpan;
                }
                if (maxFlap >= minFlap)
                {
                    float fSpan = maxFlap - minFlap;
                    if (fSpan > maxAngleSpan) maxAngleSpan = fSpan;
                }

                if (maxX >= minX && maxZ >= minZ)
                {
                    float offsetSpan = Mathf.Sqrt((maxX - minX) * (maxX - minX) + (maxZ - minZ) * (maxZ - minZ));
                    float bestOff = Mathf.Max(offsetSpan, maxSingleOffset);
                    if (bestOff > maxOffsetTravel) maxOffsetTravel = bestOff;
                }
            }

            return maxAngleSpan >= minAngleSpanDeg || maxOffsetTravel >= minRootOffsetCells;
        }

        [Unsaved]
        private Dictionary<string, BoneAnimTrack> trackByNodeId;

        [Unsaved]
        private GiantRigDef cachedEvalRig;

        [Unsaved]
        private BoneAnimTrack[] cachedEvalTracks;

        [Unsaved]
        private GiantRigDef[] cachedEvalMultiRigs;

        [Unsaved]
        private BoneAnimTrack[][] cachedEvalMultiTracks;

        [Unsaved]
        private int cachedEvalMultiCount;

        [Unsaved]
        private readonly object lookupLock = new object();

        [Unsaved]
        private GiantAnimDef cachedTimeReversedClip;

        /// <summary>
        /// Synthesizes and caches an exact time-reversed non-looping runtime counterpart of this clip
        /// so multi-stage rigs can retract (Stage k -&gt; Stage k-1) with zero boundary pop-in.
        /// </summary>
        public GiantAnimDef GetOrCreateTimeReversed()
        {
            if (cachedTimeReversedClip != null)
            {
                return cachedTimeReversedClip;
            }

            lock (lookupLock)
            {
                if (cachedTimeReversedClip != null)
                {
                    return cachedTimeReversedClip;
                }

                float dur = Mathf.Max(1f, durationTicks);
                var rev = new GiantAnimDef
                {
                    defName = (defName ?? "GCF_RuntimeClip") + "_TimeReversed",
                    label = (label ?? defName ?? "clip") + " (reversed)",
                    durationTicks = dur,
                    loop = false,
                    blendInTicks = blendInTicks,
                    isolatedSubtreeRootBoneId = isolatedSubtreeRootBoneId,
                    actingSubtreeRootBoneId = actingSubtreeRootBoneId,
                    coupledBodyMotionFactor = coupledBodyMotionFactor,
                    impactTick = impactTick >= 0f ? Mathf.Clamp(dur - impactTick, 0f, dur) : -1f
                };

                if (tracks != null)
                {
                    for (int t = 0; t < tracks.Count; t++)
                    {
                        BoneAnimTrack srcTrack = tracks[t];
                        if (srcTrack?.keyframes == null || srcTrack.keyframes.Count == 0)
                        {
                            continue;
                        }
                        srcTrack.EnsureSorted();
                        int n = srcTrack.keyframes.Count;
                        var dstTrack = new BoneAnimTrack
                        {
                            nodeId = srcTrack.nodeId,
                            keyframes = new List<BoneKeyframe>(n)
                        };
                        for (int i = n - 1; i >= 0; i--)
                        {
                            BoneKeyframe cur = srcTrack.keyframes[i];
                            // Segment [i, i+1] in forward clip uses keyframes[i+1].easing; in reversed order that segment ends at index i
                            AnimEasing segEasing = i < n - 1 ? InvertEasing(srcTrack.keyframes[i + 1].easing) : AnimEasing.Linear;
                            dstTrack.keyframes.Add(new BoneKeyframe
                            {
                                tick = Mathf.Clamp(dur - cur.tick, 0f, dur),
                                angle = cur.angle,
                                sagittalPitch = cur.sagittalPitch,
                                coronalFlap = cur.coronalFlap,
                                offset = cur.offset,
                                scale = cur.scale,
                                easing = segEasing
                            });
                        }
                        dstTrack.EnsureSorted();
                        rev.tracks.Add(dstTrack);
                    }
                }

                cachedTimeReversedClip = rev;
                return rev;
            }
        }

        private static AnimEasing InvertEasing(AnimEasing easing)
        {
            return easing switch
            {
                AnimEasing.EaseInQuad => AnimEasing.EaseOutQuad,
                AnimEasing.EaseOutQuad => AnimEasing.EaseInQuad,
                AnimEasing.EaseInCubic => AnimEasing.EaseOutCubic,
                AnimEasing.EaseOutCubic => AnimEasing.EaseInCubic,
                _ => easing
            };
        }

        public static bool DidCrossEventTick(float prevTick, float curTick, float eventTick, float durationTicks, bool loop)
        {
            if (durationTicks <= 1e-4f || Mathf.Abs(curTick - prevTick) <= 1e-5f)
            {
                return false;
            }

            if (!loop)
            {
                float p = Mathf.Clamp(prevTick, 0f, durationTicks);
                float c = Mathf.Clamp(curTick, 0f, durationTicks);
                if (c < p)
                {
                    return false;
                }
                return (p < eventTick && c >= eventTick) || (p <= 0f && eventTick <= 0f && c > 0f);
            }

            if (curTick < prevTick)
            {
                // Allow legitimate pre-wrapped loop rollover (e.g. 38 -> 3 mod 40) ONLY when both values
                // lie in [0, durationTicks) and the forward wrap step is strictly shorter than half a cycle;
                // reject backward scrubbing (e.g. 25 -> 10 mod 60).
                bool validPreWrappedRollover = prevTick >= 0f
                    && prevTick < durationTicks
                    && curTick >= 0f
                    && curTick < durationTicks
                    && (prevTick - curTick) > (durationTicks * 0.5f);
                if (!validPreWrappedRollover)
                {
                    return false;
                }
            }
            else if ((curTick - prevTick) >= durationTicks)
            {
                return true;
            }

            float pMod = prevTick % durationTicks;
            if (pMod < 0f) pMod += durationTicks;
            float cMod = curTick % durationTicks;
            if (cMod < 0f) cMod += durationTicks;
            if (Mathf.Abs(cMod - pMod) <= 1e-5f)
            {
                return false;
            }

            float eMod = Mathf.Clamp(eventTick, 0f, durationTicks);
            if (Mathf.Approximately(eMod, durationTicks))
            {
                eMod = 0f;
            }

            if (pMod < cMod)
            {
                if (pMod <= 1e-5f && eMod <= 1e-5f && cMod > 1e-5f)
                {
                    return true;
                }
                return pMod < eMod && cMod >= eMod;
            }

            if (eMod <= 1e-5f)
            {
                // Defer exact wrap-to-zero landing (cMod == 0) to the next forward step (0 -> +dt)
                // so tick-0 events fire once per loop cycle rather than twice.
                return cMod > 1e-5f;
            }
            return pMod < eMod || cMod >= eMod;
        }

        public void EvaluateInto(GiantRigDef rig, float animTick, BoneLocalPose[] outPoses)
        {
            EvaluateInto(rig, animTick, outPoses, -1f);
        }

        public void EvaluateInto(GiantRigDef rig, float animTick, BoneLocalPose[] outPoses, float pawnBodySize)
        {
            if (rig == null || outPoses == null)
            {
                return;
            }
            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = Mathf.Min(nodes.Length, outPoses.Length);
            int actingRootIdx = -1;
            bool hasActingSubtree = !string.IsNullOrWhiteSpace(actingSubtreeRootBoneId)
                && rig.TryGetNodeIndex(actingSubtreeRootBoneId, out actingRootIdx);
            float bodyCoupling = hasActingSubtree
                ? rig.ResolvePartAnimBodyCoupling(pawnBodySize, coupledBodyMotionFactor)
                : 1f;

            BoneAnimTrack[] rigTracks = ResolveTracksForRig(rig, nodes);
            if (rigTracks != null && rigTracks.Length >= count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                    {
                        outPoses[i] = BoneLocalPose.Identity;
                        continue;
                    }
                    BoneAnimTrack track = rigTracks[i];
                    BoneLocalPose pose = track != null
                        ? track.SampleAt(animTick, durationTicks, loop)
                        : BoneLocalPose.Identity;
                    if (hasActingSubtree && !rig.IsBoneInSubtree(i, actingRootIdx))
                    {
                        pose.offset *= bodyCoupling;
                        pose.angle *= bodyCoupling;
                        pose.sagittalPitch *= bodyCoupling;
                        pose.coronalFlap *= bodyCoupling;
                    }
                    outPoses[i] = pose;
                }
                return;
            }

            EnsureLookup();
            for (int i = 0; i < count; i++)
            {
                if (nodes[i].isRigid && nodes[i].parentRuntimeIndex >= 0)
                {
                    outPoses[i] = BoneLocalPose.Identity;
                    continue;
                }
                string nid = nodes[i].nodeId;
                if (trackByNodeId != null && trackByNodeId.TryGetValue(nid, out BoneAnimTrack track))
                {
                    BoneLocalPose pose = track.SampleAt(animTick, durationTicks, loop);
                    if (hasActingSubtree && !rig.IsBoneInSubtree(i, actingRootIdx))
                    {
                        pose.offset *= bodyCoupling;
                        pose.angle *= bodyCoupling;
                        pose.sagittalPitch *= bodyCoupling;
                        pose.coronalFlap *= bodyCoupling;
                    }
                    outPoses[i] = pose;
                }
                else
                {
                    outPoses[i] = BoneLocalPose.Identity;
                }
            }
        }

        private BoneAnimTrack[] ResolveTracksForRig(GiantRigDef rig, BoneNodeDef[] nodes)
        {
            if (ReferenceEquals(cachedEvalRig, rig))
            {
                BoneAnimTrack[] existing = cachedEvalTracks;
                if (existing != null && existing.Length == nodes.Length)
                {
                    return existing;
                }
            }

            // Lock-free multi-rig fast lookup (eliminates GC cache thrashing when shared clips evaluate across multiple rigs)
            GiantRigDef[] multiRigs = cachedEvalMultiRigs;
            BoneAnimTrack[][] multiTracks = cachedEvalMultiTracks;
            int mCount = cachedEvalMultiCount;
            if (multiRigs != null && multiTracks != null)
            {
                int limit = Math.Min(mCount, Math.Min(multiRigs.Length, multiTracks.Length));
                for (int k = 0; k < limit; k++)
                {
                    if (ReferenceEquals(multiRigs[k], rig))
                    {
                        BoneAnimTrack[] tr = multiTracks[k];
                        if (tr != null && tr.Length == nodes.Length)
                        {
                            return tr;
                        }
                    }
                }
            }

            lock (lookupLock)
            {
                EnsureLookupUnsafe();
                if (ReferenceEquals(cachedEvalRig, rig) && cachedEvalTracks != null && cachedEvalTracks.Length == nodes.Length)
                {
                    return cachedEvalTracks;
                }
                if (cachedEvalMultiRigs != null && cachedEvalMultiTracks != null)
                {
                    for (int k = 0; k < cachedEvalMultiCount; k++)
                    {
                        if (ReferenceEquals(cachedEvalMultiRigs[k], rig)
                            && cachedEvalMultiTracks[k] != null
                            && cachedEvalMultiTracks[k].Length == nodes.Length)
                        {
                            return cachedEvalMultiTracks[k];
                        }
                    }
                }

                var mapped = new BoneAnimTrack[nodes.Length];
                if (trackByNodeId != null)
                {
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        string nid = nodes[i]?.nodeId;
                        if (!string.IsNullOrEmpty(nid) && trackByNodeId.TryGetValue(nid, out BoneAnimTrack tr))
                        {
                            mapped[i] = tr;
                        }
                    }
                }

                if (cachedEvalRig == null)
                {
                    cachedEvalTracks = mapped;
                    cachedEvalRig = rig;
                }
                else
                {
                    if (cachedEvalMultiRigs == null)
                    {
                        cachedEvalMultiRigs = new GiantRigDef[8];
                        cachedEvalMultiTracks = new BoneAnimTrack[8][];
                    }
                    int slot = cachedEvalMultiCount < cachedEvalMultiRigs.Length
                        ? cachedEvalMultiCount
                        : (cachedEvalMultiRigs.Length - 1);
                    cachedEvalMultiTracks[slot] = mapped;
                    cachedEvalMultiRigs[slot] = rig;
                    if (cachedEvalMultiCount < cachedEvalMultiRigs.Length)
                    {
                        cachedEvalMultiCount++;
                    }
                }
                return mapped;
            }
        }

        private void EnsureLookup()
        {
            if (trackByNodeId != null)
            {
                return;
            }
            lock (lookupLock)
            {
                EnsureLookupUnsafe();
            }
        }

        private void EnsureLookupUnsafe()
        {
            if (trackByNodeId != null)
            {
                return;
            }
            var dict = new Dictionary<string, BoneAnimTrack>(StringComparer.Ordinal);
            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    BoneAnimTrack tr = tracks[i];
                    if (tr != null && !string.IsNullOrEmpty(tr.nodeId))
                    {
                        tr.EnsureSorted();
                        dict[tr.nodeId] = tr;
                    }
                }
            }
            trackByNodeId = dict;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string err in base.ConfigErrors())
            {
                yield return err;
            }

            if (tracks == null)
            {
                yield break;
            }

            for (int i = 0; i < tracks.Count; i++)
            {
                BoneAnimTrack tr = tracks[i];
                if (tr?.keyframes == null)
                {
                    continue;
                }
                for (int k = 0; k < tr.keyframes.Count; k++)
                {
                    BoneKeyframe kf = tr.keyframes[k];
                    if (kf != null && (kf.scale - Vector2.one).sqrMagnitude > 1e-5f)
                    {
                        yield return $"GiantAnimDef '{defName}' track '{tr.nodeId}' keyframe@{kf.tick} sets non-unit scale {kf.scale}. Scale/breathing animations are globally prohibited in GiantCreatureFramework; use pure skeletal joint rotation.";
                    }
                }
            }
        }
    }
}
