using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Rotating wheel / sprocket / idler overlay attached to a near-side lateral track assembly.
    /// Pixel coordinates are authored on the parent track's East-facing 512x512 sprite and mirrored automatically for West.
    /// </summary>
    public class GiantTrackWheelDef
    {
        public string wheelId;
        public string texPath;
        public Vector2 centerPixel = new Vector2(256f, 256f);
        public float diameterCells = 0.72f;
        public float layerOffset = 1.10f;
    }

    /// <summary>
    /// Declarative rolling caterpillar track belt and synchronous wheel configuration attached to a track BoneNodeDef.
    /// Supports both closed-loop UV circulation on near-side 3/4 orthographic views and longitudinal UV tread scrolling on Dorsal and far-side views.
    /// </summary>
    public class GiantRollingTrackDef
    {
        public string trackId;
        public string parentBoneId;

        /// <summary>
        /// -1 for port/left track (Near in West view), +1 for starboard/right track (Near in East view).
        /// </summary>
        public float sideSign = 1f;

        /// <summary>
        /// Effective half-gauge distance (in cells) used for differential track travel during yaw rotation:
        /// deltaS = forwardDelta + (-sideSign) * gaugeHalfWidthCells * deltaHeadingRad.
        /// </summary>
        public float gaugeHalfWidthCells = 1.92f;

        public string dorsalRibbonTexPath;
        public string lateralRibbonTexPath;
        public float tileLengthCells = 2.60f;

        // Dorsal strip endpoints (stern -> bow in pixels on parent Dorsal sprite)
        public Vector2 dorsalStartPixel = new Vector2(256f, 480f);
        public Vector2 dorsalEndPixel = new Vector2(256f, 32f);
        public float dorsalWidthCells = 0.64f;
        public float dorsalLayerOffset = -1.05f;

        // Near lateral closed-loop control polygon (ordered CW in East Near 512x512 pixels; automatically X-mirrored in West Near view)
        public List<Vector2> nearLoopPixels = new List<Vector2>();
        public float nearWidthCells = 0.30f;
        public float nearLayerOffset = 1.45f;

        // Far lateral open strip endpoints (stern -> bow in East Far 512x512 pixels; automatically X-mirrored in West Far view)
        public Vector2 farStartPixel = new Vector2(256f, 470f);
        public Vector2 farEndPixel = new Vector2(256f, 42f);
        public float farWidthCells = 0.24f;
        public float farLayerOffset = -1.08f;

        // Synchronously spinning wheel overlays shown in Near lateral view (authored in East Near pixels; X-mirrored for West)
        public List<GiantTrackWheelDef> wheels = new List<GiantTrackWheelDef>();

        [Unsaved]
        public int runtimeIndex = -1;

        [Unsaved]
        public int resolvedParentBoneIndex = -1;

        public bool IsNearSector(GiantBoneViewSector parentSector)
        {
            return (sideSign >= 0f && parentSector == GiantBoneViewSector.East)
                || (sideSign < 0f && parentSector == GiantBoneViewSector.West);
        }

        public float GetBeltLayerOffset(GiantBoneViewSector parentSector)
        {
            if (parentSector == GiantBoneViewSector.Dorsal)
            {
                return dorsalLayerOffset;
            }
            return IsNearSector(parentSector) ? nearLayerOffset : farLayerOffset;
        }
    }

    public static class GiantRollingTrackKinematics
    {
        /// <summary>
        /// Computes left and right caterpillar track belt travel deltas (in cells) from hull forward displacement
        /// and clockwise heading change.
        /// Positive delta = tracks roll forward (ground moves backward underneath).
        /// </summary>
        public static void ComputeDifferentialStep(
            float forwardDeltaCells,
            float deltaHeadingCwDeg,
            float gaugeHalfWidthCells,
            out float leftDeltaCells,
            out float rightDeltaCells)
        {
            float yawCells = Mathf.Max(0.1f, gaugeHalfWidthCells) * (deltaHeadingCwDeg * Mathf.Deg2Rad);
            leftDeltaCells = forwardDeltaCells + yawCells;
            rightDeltaCells = forwardDeltaCells - yawCells;
        }

        /// <summary>
        /// Converts cumulative track belt travel (cells) into clockwise texture spin angle (degrees) for a Near-side wheel.
        /// In East lateral view (forward = Up/+Z, ground = Right/+X), forward roll spins wheels clockwise (+).
        /// In West lateral view (forward = Up/+Z, ground = Left/-X), forward roll spins wheels counter-clockwise (-).
        /// </summary>
        public static float ComputeWheelSpinAngleDeg(
            float scrollCells,
            float diameterCells,
            GiantBoneViewSector parentSector)
        {
            float radius = Mathf.Max(0.08f, 0.5f * diameterCells);
            float dirSign = parentSector == GiantBoneViewSector.West ? -1f : 1f;
            float deg = dirSign * (scrollCells / radius) * Mathf.Rad2Deg;
            return Mathf.Repeat(deg, 360f);
        }
    }

    /// <summary>
    /// Pure geometric builder producing parent-bone quad-local XZ quad-strip meshes with seamless repeat UV scrolling.
    /// </summary>
    public static class GiantRollingTrackMeshBuilder
    {
        public static Vector2 PixelToBoneLocalXZ(
            Vector2 pixel,
            int texRes,
            Vector2 boneDrawSize,
            bool mirrorX)
        {
            float res = Mathf.Max(1f, texRes);
            float px = mirrorX ? (res - 1f - pixel.x) : pixel.x;
            float u = px / res;
            float v = 1f - (pixel.y / res);
            return new Vector2((u - 0.5f) * boneDrawSize.x, (v - 0.5f) * boneDrawSize.y);
        }

        public static bool BuildOpenStrip(
            Vector2 startPixel,
            Vector2 endPixel,
            float stripWidthCells,
            float tileLengthCells,
            int texRes,
            Vector2 boneDrawSize,
            bool mirrorX,
            float scrollCells,
            List<Vector3> outVerts,
            List<Vector2> outUvs,
            List<int> outTris,
            int segments = 12)
        {
            outVerts.Clear();
            outUvs.Clear();
            outTris.Clear();

            Vector2 p0 = PixelToBoneLocalXZ(startPixel, texRes, boneDrawSize, mirrorX);
            Vector2 p1 = PixelToBoneLocalXZ(endPixel, texRes, boneDrawSize, mirrorX);
            Vector2 delta = p1 - p0;
            float len = delta.magnitude;
            if (len <= 1e-4f || stripWidthCells <= 1e-4f)
            {
                return false;
            }

            Vector2 tangent = delta / len;
            // Right-hand side of forward vector in XZ looking from +Y downward: (tz, -tx)
            Vector2 rightPerp = new Vector2(tangent.y, -tangent.x);
            float halfW = 0.5f * stripWidthCells;
            float tileLen = Mathf.Max(0.25f, tileLengthCells);
            float wrapped = Mathf.Repeat(scrollCells, tileLen);
            int segCount = Mathf.Max(2, segments);

            for (int i = 0; i <= segCount; i++)
            {
                float t = i / (float)segCount;
                Vector2 c = p0 + delta * t;
                float edgeTaper = (i == 0 || i == segCount) ? 0.84f : 1.0f;
                Vector2 l = c - rightPerp * (halfW * edgeTaper);
                Vector2 r = c + rightPerp * (halfW * edgeTaper);

                float s = len * t;
                float vCoord = (s - wrapped) / tileLen;

                outVerts.Add(new Vector3(l.x, 0f, l.y));
                outVerts.Add(new Vector3(r.x, 0f, r.y));
                outUvs.Add(new Vector2(0f, vCoord));
                outUvs.Add(new Vector2(1f, vCoord));
            }

            AppendStripTriangles(segCount, outVerts, outTris);
            return true;
        }

        public static bool BuildClosedLoopStrip(
            IReadOnlyList<Vector2> loopPixels,
            float stripWidthCells,
            float tileLengthCells,
            int texRes,
            Vector2 boneDrawSize,
            bool mirrorX,
            float scrollCells,
            List<Vector3> outVerts,
            List<Vector2> outUvs,
            List<int> outTris)
        {
            outVerts.Clear();
            outUvs.Clear();
            outTris.Clear();

            if (loopPixels == null || loopPixels.Count < 4 || stripWidthCells <= 1e-4f)
            {
                return false;
            }

            int rawCount = loopPixels.Count;
            Span<Vector2> pts = rawCount <= 128 ? stackalloc Vector2[rawCount] : new Vector2[rawCount];
            for (int i = 0; i < rawCount; i++)
            {
                pts[i] = PixelToBoneLocalXZ(loopPixels[i], texRes, boneDrawSize, mirrorX);
            }

            // Compute signed shoelace area to determine CW vs CCW orientation so outward normal never inverts under mirrorX.
            float twiceArea = 0f;
            for (int i = 0; i < rawCount; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[(i + 1) % rawCount];
                twiceArea += (a.x * b.y - b.x * a.y);
            }
            bool isCW = twiceArea < 0f;

            // Compute cumulative perimeter and per-edge lengths (zero-GC stackalloc for typical loops <= 128)
            Span<float> cumS = rawCount < 128 ? stackalloc float[rawCount + 1] : new float[rawCount + 1];
            cumS[0] = 0f;
            for (int i = 0; i < rawCount; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[(i + 1) % rawCount];
                cumS[i + 1] = cumS[i] + Vector2.Distance(a, b);
            }
            float perimeter = cumS[rawCount];
            if (perimeter <= 1e-3f)
            {
                return false;
            }

            // Quantize repeats around closed loop to an integer so vertex N == vertex 0 has zero UV seam!
            int repeats = Mathf.Max(1, Mathf.RoundToInt(perimeter / Mathf.Max(0.25f, tileLengthCells)));
            float effectiveTile = perimeter / repeats;
            float wrapped = Mathf.Repeat(scrollCells, effectiveTile);
            float halfW = 0.5f * stripWidthCells;

            Vector2 ComputeOutwardNormal(Vector2 t)
            {
                // For CW in (x, z), outward normal is (-tz, tx); for CCW (mirrored West), outward normal is (tz, -tx).
                return isCW ? new Vector2(-t.y, t.x) : new Vector2(t.y, -t.x);
            }

            for (int i = 0; i <= rawCount; i++)
            {
                int idx = i % rawCount;
                int prev = (idx - 1 + rawCount) % rawCount;
                int next = (idx + 1) % rawCount;

                Vector2 ePrev = pts[idx] - pts[prev];
                Vector2 eNext = pts[next] - pts[idx];
                Vector2 tPrev = ePrev.sqrMagnitude > 1e-8f ? ePrev.normalized : Vector2.up;
                Vector2 tNext = eNext.sqrMagnitude > 1e-8f ? eNext.normalized : tPrev;

                Vector2 nPrev = ComputeOutwardNormal(tPrev);
                Vector2 nNext = ComputeOutwardNormal(tNext);
                Vector2 avgN = nPrev + nNext;
                if (avgN.sqrMagnitude <= 1e-6f)
                {
                    avgN = nNext;
                }
                else
                {
                    avgN.Normalize();
                }

                // Exact miter clamping so sharp sprocket/idler corners never pinch or spike
                float dot = Mathf.Max(0.45f, Vector2.Dot(avgN, nNext));
                float miterHalfW = halfW / dot;

                Vector2 outer = pts[idx] + avgN * miterHalfW;
                Vector2 inner = pts[idx] - avgN * miterHalfW;
                float vCoord = (cumS[i] - wrapped) / effectiveTile;

                outVerts.Add(new Vector3(outer.x, 0f, outer.y));
                outVerts.Add(new Vector3(inner.x, 0f, inner.y));
                outUvs.Add(new Vector2(0f, vCoord));
                outUvs.Add(new Vector2(1f, vCoord));
            }

            AppendStripTriangles(rawCount, outVerts, outTris);
            return true;
        }

        private static void AppendStripTriangles(int segmentCount, List<Vector3> verts, List<int> outTris)
        {
            for (int j = 0; j < segmentCount; j++)
            {
                int i0 = j * 2;
                int i1 = i0 + 1;
                int i2 = i0 + 2;
                int i3 = i0 + 3;

                // Ensure CW winding viewed from +Y looking down onto XZ plane regardless of loop/mirror handedness.
                Vector3 v0 = verts[i0];
                Vector3 v2 = verts[i2];
                Vector3 v3 = verts[i3];
                float fwdX = v2.x - v0.x;
                float fwdZ = v2.z - v0.z;
                float acrossX = v3.x - v2.x;
                float acrossZ = v3.z - v2.z;
                float cross2D = fwdX * acrossZ - fwdZ * acrossX;

                if (cross2D <= 0f)
                {
                    outTris.Add(i0);
                    outTris.Add(i2);
                    outTris.Add(i3);

                    outTris.Add(i0);
                    outTris.Add(i3);
                    outTris.Add(i1);
                }
                else
                {
                    outTris.Add(i0);
                    outTris.Add(i3);
                    outTris.Add(i2);

                    outTris.Add(i0);
                    outTris.Add(i1);
                    outTris.Add(i3);
                }
            }
        }
    }
}
