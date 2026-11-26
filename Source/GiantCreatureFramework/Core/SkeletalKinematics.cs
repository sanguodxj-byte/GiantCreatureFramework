using System;
using UnityEngine;

namespace GiantCreatureFramework
{
    public struct BoneLocalPose
    {
        public Vector2 offset;
        public float angle;
        public Vector2 scale;
        public float sagittalPitch;
        public float coronalFlap;

        public static BoneLocalPose Identity => new BoneLocalPose(Vector2.zero, 0f, Vector2.one, 0f, 0f);

        public BoneLocalPose(Vector2 offset, float angle, Vector2 scale, float sagittalPitch = 0f, float coronalFlap = 0f)
        {
            this.offset = offset;
            this.angle = angle;
            this.scale = scale;
            this.sagittalPitch = sagittalPitch;
            this.coronalFlap = coronalFlap;
        }

        public static BoneLocalPose Lerp(in BoneLocalPose a, in BoneLocalPose b, float t)
        {
            t = Mathf.Clamp01(t);
            return new BoneLocalPose(
                Vector2.LerpUnclamped(a.offset, b.offset, t),
                a.angle + Mathf.DeltaAngle(a.angle, b.angle) * t,
                Vector2.one,
                Mathf.LerpUnclamped(a.sagittalPitch, b.sagittalPitch, t),
                Mathf.LerpUnclamped(a.coronalFlap, b.coronalFlap, t));
        }
    }

    public struct BoneWorldPose
    {
        public int nodeIndex;
        public bool visible;

        /// <summary>
        /// World XZ position of this bone's pivot joint.
        /// </summary>
        public Vector2 jointWorldXZ;

        /// <summary>
        /// World XZ center of the rendered 1x1 unit quad (MeshPool.plane10).
        /// </summary>
        public Vector2 quadCenterWorldXZ;

        /// <summary>
        /// Clockwise world rotation in degrees around +Y (0 = North/+Z, 90 = East/+X).
        /// </summary>
        public float worldAngleDeg;

        /// <summary>
        /// Effective world width (X) and height/length (Z) of the bone's quad in map cells.
        /// </summary>
        public Vector2 worldSize;

        public Vector2 pivotUV;
        public float layerOffset;
        public GiantBoneViewSector activeSector;

        // Precomputed cos/sin of worldAngleDeg for ultra-fast inverse UV mapping
        public float cosTheta;
        public float sinTheta;

        // Optional 3D coronal-plane wingbeat/hip projection around a longitudinal hinge
        // and coupled 3D vertical parasagittal limb projection along body heading
        public bool hasCoronalFlap;
        public bool hasSagittalShear;
        public Vector2 flatJointWorldXZ;
        public Vector2 flatQuadCenterWorldXZ;
        public Vector2 flapHingeXZ;
        public Vector2 flapNormalXZ;
        public float flapCosPsi;
        public Vector2 sagGradXZ;
        public Vector2 sagForwardAxisXZ;
        public float sagDet;
        public float ownSagittalForwardCells;
        public float rootHeadingRad;

        /// <summary>
        /// Maps a world XZ point into this bone's normalized [0..1, 0..1] quad UV space.
        /// Identical inverse to BuildRenderMatrix(altitudeY) * (u - 0.5, 0, v - 0.5).
        /// </summary>
        public bool TryWorldToUV(float wx, float wz, out float u, out float v)
        {
            if (!visible || worldSize.x <= 1e-5f || worldSize.y <= 1e-5f)
            {
                u = 0f;
                v = 0f;
                return false;
            }

            float evalX = wx;
            float evalZ = wz;
            bool hasCoronal = hasCoronalFlap && Mathf.Abs(flapCosPsi - 1f) > 1e-5f;
            Vector2 refJoint = (hasCoronal || hasSagittalShear) ? flatJointWorldXZ : jointWorldXZ;
            if (hasCoronal)
            {
                float safeCos = Mathf.Max(0.05f, flapCosPsi);
                float dLatProj = (evalX - flapHingeXZ.x) * flapNormalXZ.x + (evalZ - flapHingeXZ.y) * flapNormalXZ.y;
                float invShift = ((1f / safeCos) - 1f) * dLatProj;
                evalX += invShift * flapNormalXZ.x;
                evalZ += invShift * flapNormalXZ.y;
            }
            if (hasSagittalShear)
            {
                float relX = evalX - flatJointWorldXZ.x;
                float relZ = evalZ - flatJointWorldXZ.y;
                float gDotRel = sagGradXZ.x * relX + sagGradXZ.y * relZ;
                float invSag = gDotRel / Mathf.Max(0.05f, sagDet);
                evalX -= invSag * sagForwardAxisXZ.x;
                evalZ -= invSag * sagForwardAxisXZ.y;
            }

            float dx = evalX - refJoint.x;
            float dz = evalZ - refJoint.y;

            // Inverse of RotateCW(theta): RotateCW(-theta)
            float localX = dx * cosTheta - dz * sinTheta;
            float localZ = dx * sinTheta + dz * cosTheta;

            u = pivotUV.x + localX / worldSize.x;
            v = pivotUV.y + localZ / worldSize.y;
            return u >= 0f && u < 1f && v >= 0f && v < 1f;
        }

        /// <summary>
        /// Computes the exact affine mapping from world (wx, wz) to bone sprite (u, v) centered at (originX, originZ):
        ///   u(wx, wz) = u0 + (wx - originX) * duDx + (wz - originZ) * duDz
        ///   v(wx, wz) = v0 + (wx - originX) * dvDx + (wz - originZ) * dvDz
        /// Algebraically identical to TryWorldToUV for both rigid and coronal-flap / sagittal-shear projected bones.
        /// </summary>
        public void ComputeWorldToUVAffineAt(
            float originX,
            float originZ,
            out float u0,
            out float v0,
            out float duDx,
            out float dvDx,
            out float duDz,
            out float dvDz)
        {
            float invW = 1f / worldSize.x;
            float invH = 1f / worldSize.y;
            bool hasCoronal = hasCoronalFlap && Mathf.Abs(flapCosPsi - 1f) > 1e-5f;

            if (!hasCoronal && !hasSagittalShear)
            {
                float dx = originX - jointWorldXZ.x;
                float dz = originZ - jointWorldXZ.y;
                u0 = pivotUV.x + (dx * cosTheta - dz * sinTheta) * invW;
                v0 = pivotUV.y + (dx * sinTheta + dz * cosTheta) * invH;
                duDx = cosTheta * invW;
                dvDx = sinTheta * invH;
                duDz = -sinTheta * invW;
                dvDz = cosTheta * invH;
                return;
            }

            // Linearized basis vectors for +1 in world X (ex) and +1 in world Z (ez), plus origin point (evalX, evalZ)
            float evalX = originX;
            float evalZ = originZ;
            float exX = 1f, exZ = 0f;
            float ezX = 0f, ezZ = 1f;

            if (hasCoronal)
            {
                float kC = (1f / Mathf.Max(0.05f, flapCosPsi)) - 1f;
                float nx = flapNormalXZ.x;
                float nz = flapNormalXZ.y;

                float dLat = (evalX - flapHingeXZ.x) * nx + (evalZ - flapHingeXZ.y) * nz;
                evalX += kC * dLat * nx;
                evalZ += kC * dLat * nz;

                float px = exX * nx + exZ * nz;
                exX += kC * px * nx;
                exZ += kC * px * nz;

                float pz = ezX * nx + ezZ * nz;
                ezX += kC * pz * nx;
                ezZ += kC * pz * nz;
            }

            if (hasSagittalShear)
            {
                float invDet = 1f / Mathf.Max(0.05f, sagDet);
                float gx = sagGradXZ.x;
                float gz = sagGradXZ.y;
                float fx = sagForwardAxisXZ.x;
                float fz = sagForwardAxisXZ.y;

                float gDot0 = gx * (evalX - flatJointWorldXZ.x) + gz * (evalZ - flatJointWorldXZ.y);
                evalX -= (gDot0 * invDet) * fx;
                evalZ -= (gDot0 * invDet) * fz;

                float gDotX = gx * exX + gz * exZ;
                exX -= (gDotX * invDet) * fx;
                exZ -= (gDotX * invDet) * fz;

                float gDotZ = gx * ezX + gz * ezZ;
                ezX -= (gDotZ * invDet) * fx;
                ezZ -= (gDotZ * invDet) * fz;
            }

            float dx0 = evalX - flatJointWorldXZ.x;
            float dz0 = evalZ - flatJointWorldXZ.y;
            u0 = pivotUV.x + (dx0 * cosTheta - dz0 * sinTheta) * invW;
            v0 = pivotUV.y + (dx0 * sinTheta + dz0 * cosTheta) * invH;
            duDx = (exX * cosTheta - exZ * sinTheta) * invW;
            dvDx = (exX * sinTheta + exZ * cosTheta) * invH;
            duDz = (ezX * cosTheta - ezZ * sinTheta) * invW;
            dvDz = (ezX * sinTheta + ezZ * cosTheta) * invH;
        }

        /// <summary>
        /// Maps normalized UV (u, v) on this bone's sprite to unprojected flat world XZ coordinates.
        /// </summary>
        public Vector2 UVToWorldXZFlat(float u, float v)
        {
            bool hasProj = (hasCoronalFlap && Mathf.Abs(flapCosPsi - 1f) > 1e-5f) || hasSagittalShear;
            Vector2 refJoint = hasProj ? flatJointWorldXZ : jointWorldXZ;
            float lx = (u - pivotUV.x) * worldSize.x;
            float lz = (v - pivotUV.y) * worldSize.y;
            float wx = refJoint.x + (lx * cosTheta + lz * sinTheta);
            float wz = refJoint.y + (-lx * sinTheta + lz * cosTheta);
            return new Vector2(wx, wz);
        }

        /// <summary>
        /// Maps normalized UV (u, v) on this bone's sprite to world XZ coordinates
        /// (including coupled 3D vertical sagittal pitch and 3D coronal foreshortening).
        /// </summary>
        public Vector2 UVToWorldXZ(float u, float v)
        {
            Vector2 pt = UVToWorldXZFlat(u, v);
            if (hasSagittalShear)
            {
                float dSag = sagGradXZ.x * (pt.x - flatJointWorldXZ.x) + sagGradXZ.y * (pt.y - flatJointWorldXZ.y);
                pt.x += dSag * sagForwardAxisXZ.x;
                pt.y += dSag * sagForwardAxisXZ.y;
            }
            if (!hasCoronalFlap || Mathf.Abs(flapCosPsi - 1f) <= 1e-5f)
            {
                return pt;
            }
            float dLat = (pt.x - flapHingeXZ.x) * flapNormalXZ.x + (pt.y - flapHingeXZ.y) * flapNormalXZ.y;
            float shift = (flapCosPsi - 1f) * dLat;
            return new Vector2(pt.x + shift * flapNormalXZ.x, pt.y + shift * flapNormalXZ.y);
        }

        public Matrix4x4 BuildRenderMatrix(float worldAltitudeY)
        {
            bool hasCoronal = hasCoronalFlap && Mathf.Abs(flapCosPsi - 1f) > 1e-5f;
            Vector2 center = (hasCoronal || hasSagittalShear) ? flatQuadCenterWorldXZ : quadCenterWorldXZ;
            Vector3 pos = new Vector3(center.x, worldAltitudeY, center.y);
            Quaternion rot = Quaternion.AngleAxis(worldAngleDeg, Vector3.up);
            Vector3 scale = new Vector3(worldSize.x, 1f, worldSize.y);
            Matrix4x4 flatM = Matrix4x4.TRS(pos, rot, scale);
            if (hasSagittalShear)
            {
                float gx = sagGradXZ.x;
                float gz = sagGradXZ.y;
                float hx = sagForwardAxisXZ.x;
                float hz = sagForwardAxisXZ.y;
                float jDotG = flatJointWorldXZ.x * gx + flatJointWorldXZ.y * gz;
                Matrix4x4 sagM = Matrix4x4.identity;
                sagM.m00 = 1f + hx * gx;
                sagM.m02 = hx * gz;
                sagM.m03 = -hx * jDotG;
                sagM.m20 = hz * gx;
                sagM.m22 = 1f + hz * gz;
                sagM.m23 = -hz * jDotG;
                flatM = sagM * flatM;
            }
            if (!hasCoronal)
            {
                return flatM;
            }

            float k = flapCosPsi - 1f;
            float nx = flapNormalXZ.x;
            float nz = flapNormalXZ.y;
            float hDotN = flapHingeXZ.x * nx + flapHingeXZ.y * nz;
            Matrix4x4 proj = Matrix4x4.identity;
            proj.m00 = 1f + k * nx * nx;
            proj.m02 = k * nx * nz;
            proj.m03 = -k * nx * hDotN;
            proj.m20 = k * nz * nx;
            proj.m22 = 1f + k * nz * nz;
            proj.m23 = -k * nz * hDotN;
            return proj * flatM;
        }

        /// <summary>
        /// Builds a 360-degree render matrix for a sub-quad decal anchored at normalized (u, v) on this bone's sprite,
        /// preserving exact 3D sagittal pitch shear and 3D coronal wingbeat foreshortening so decals never overflow edges.
        /// </summary>
        public Matrix4x4 BuildSubQuadRenderMatrix(
            float u,
            float v,
            float localAngleOffsetDeg,
            Vector2 decalWorldSize,
            float worldAltitudeY)
        {
            bool hasCoronal = hasCoronalFlap && Mathf.Abs(flapCosPsi - 1f) > 1e-5f;
            Vector2 center = UVToWorldXZFlat(u, v);
            Vector3 pos = new Vector3(center.x, worldAltitudeY, center.y);
            Quaternion rot = Quaternion.AngleAxis(worldAngleDeg + localAngleOffsetDeg, Vector3.up);
            Vector3 scale = new Vector3(decalWorldSize.x, 1f, decalWorldSize.y);
            Matrix4x4 flatM = Matrix4x4.TRS(pos, rot, scale);
            if (hasSagittalShear)
            {
                float gx = sagGradXZ.x;
                float gz = sagGradXZ.y;
                float hx = sagForwardAxisXZ.x;
                float hz = sagForwardAxisXZ.y;
                float jDotG = flatJointWorldXZ.x * gx + flatJointWorldXZ.y * gz;
                Matrix4x4 sagM = Matrix4x4.identity;
                sagM.m00 = 1f + hx * gx;
                sagM.m02 = hx * gz;
                sagM.m03 = -hx * jDotG;
                sagM.m20 = hz * gx;
                sagM.m22 = 1f + hz * gz;
                sagM.m23 = -hz * jDotG;
                flatM = sagM * flatM;
            }
            if (!hasCoronal)
            {
                return flatM;
            }

            float k = flapCosPsi - 1f;
            float nx = flapNormalXZ.x;
            float nz = flapNormalXZ.y;
            float hDotN = flapHingeXZ.x * nx + flapHingeXZ.y * nz;
            Matrix4x4 proj = Matrix4x4.identity;
            proj.m00 = 1f + k * nx * nx;
            proj.m02 = k * nx * nz;
            proj.m03 = -k * nx * hDotN;
            proj.m20 = k * nz * nx;
            proj.m22 = 1f + k * nz * nz;
            proj.m23 = -k * nz * hDotN;
            return proj * flatM;
        }
    }

    /// <summary>
    /// Runtime per-bone turret recoil recuperator state supporting arbitrary firing directions (-fireDir),
    /// monotonic zero-bounce recovery, and capped rapid-burst accumulation.
    /// </summary>
    public struct TurretRecoilState
    {
        public bool active;
        public bool hasPendingImpulse;
        public float pendingFireWorldAngleDeg;
        public float currentDisplacementCells;
        public float startDisplacementCells;
        public float peakDisplacementCells;
        public float elapsedTicks;
        public int kickTicks;
        public int recoveryTicks;
        public Vector2 localRecoilDir;
        public int totalImpulses;
        public float maxObservedDisplacementCells;

        public void QueueShotImpulse(GiantTurretRecoilDef def, float fireWorldAngleDeg, float scale = 1f)
        {
            if (def == null || scale <= 0.001f)
            {
                return;
            }
            float addDist = def.distanceCells * scale;
            if (addDist <= 1e-4f)
            {
                return;
            }

            float maxCap = def.EffectiveMaxAccumulatedCells * Mathf.Max(1f, scale);
            startDisplacementCells = active ? currentDisplacementCells : 0f;
            peakDisplacementCells = Mathf.Min(maxCap, startDisplacementCells + addDist);
            kickTicks = Mathf.Clamp(def.kickTicks, 0, 30);
            recoveryTicks = Mathf.Clamp(def.recoveryTicks, 1, 180);
            elapsedTicks = 0f;
            currentDisplacementCells = kickTicks <= 0
                ? peakDisplacementCells
                : Mathf.Max(startDisplacementCells, Mathf.Lerp(startDisplacementCells, peakDisplacementCells, 0.65f));
            if (currentDisplacementCells > maxObservedDisplacementCells)
            {
                maxObservedDisplacementCells = currentDisplacementCells;
            }
            pendingFireWorldAngleDeg = fireWorldAngleDeg;
            hasPendingImpulse = true;
            active = true;
            totalImpulses++;
        }

        public Vector2 StepAndEvaluate(float deltaTicks, float currentBoneWorldAngleDeg)
        {
            if (!active)
            {
                return Vector2.zero;
            }

            if (hasPendingImpulse)
            {
                float deltaDeg = Mathf.DeltaAngle(currentBoneWorldAngleDeg, pendingFireWorldAngleDeg);
                float rad = deltaDeg * Mathf.Deg2Rad;
                localRecoilDir = new Vector2(-Mathf.Sin(rad), -Mathf.Cos(rad));
                hasPendingImpulse = false;
            }

            if (deltaTicks > 0f)
            {
                elapsedTicks += deltaTicks;
                if (kickTicks > 0 && elapsedTicks < kickTicks)
                {
                    float u = Mathf.Clamp01(elapsedTicks / kickTicks);
                    float easeOut = 1f - (1f - u) * (1f - u) * (1f - u);
                    currentDisplacementCells = Mathf.Max(
                        currentDisplacementCells,
                        Mathf.LerpUnclamped(startDisplacementCells, peakDisplacementCells, easeOut));
                }
                else
                {
                    float recElapsed = elapsedTicks - kickTicks;
                    if (recElapsed >= recoveryTicks)
                    {
                        active = false;
                        currentDisplacementCells = 0f;
                        return Vector2.zero;
                    }
                    float v = Mathf.Clamp01(recElapsed / recoveryTicks);
                    float smooth = v * v * (3f - 2f * v);
                    currentDisplacementCells = peakDisplacementCells * (1f - smooth);
                }

                if (currentDisplacementCells > maxObservedDisplacementCells)
                {
                    maxObservedDisplacementCells = currentDisplacementCells;
                }
            }

            return localRecoilDir * currentDisplacementCells;
        }

        public void ClearActiveMotion()
        {
            active = false;
            hasPendingImpulse = false;
            currentDisplacementCells = 0f;
            startDisplacementCells = 0f;
            peakDisplacementCells = 0f;
            elapsedTicks = 0f;
        }
    }

    public static class SkeletalKinematics
    {
        /// <summary>
        /// Rotates (vx, vz) clockwise by angleDeg in Unity's top-down XZ plane (+Z = North, +X = East).
        /// Matches Quaternion.AngleAxis(angleDeg, Vector3.up).
        /// </summary>
        public static Vector2 RotateCW(Vector2 v, float angleDeg)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad);
            float s = Mathf.Sin(rad);
            return new Vector2(v.x * c + v.y * s, -v.x * s + v.y * c);
        }

        /// <summary>
        /// Evaluates full hierarchical Forward Kinematics for all topologically sorted nodes in a GiantRigDef.
        /// </summary>
        public static void SolveWorldPoses(
            GiantRigDef rig,
            Vector2 rootWorldXZ,
            float rootHeadingDeg,
            ReadOnlySpan<BoneLocalPose> localAnimPoses,
            ReadOnlySpan<bool> partMissingMask,
            Span<BoneWorldPose> outWorldPoses,
            float globalScale = 1f,
            ReadOnlySpan<GiantBoneViewSector> sectorOverrides = default,
            int inclusiveMaxIndex = -1,
            ReadOnlySpan<Vector2> turretRecoilLocalOffsets = default)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            int totalNodes = nodes.Length;
            int count = inclusiveMaxIndex >= 0 && inclusiveMaxIndex < totalNodes
                ? inclusiveMaxIndex + 1
                : totalNodes;
            if (outWorldPoses.Length < count)
            {
                throw new ArgumentException("outWorldPoses buffer is smaller than rig node count.");
            }

            float rootRad = rootHeadingDeg * Mathf.Deg2Rad;
            float rootCos = Mathf.Cos(rootRad);
            float rootSin = Mathf.Sin(rootRad);
            float hullRad = rootRad;
            float hullCos = rootCos;
            float hullSin = rootSin;

            for (int i = 0; i < count; i++)
            {
                BoneNodeDef def = nodes[i];
                BoneLocalPose anim = i < localAnimPoses.Length ? localAnimPoses[i] : BoneLocalPose.Identity;
                bool selfMissing = i < partMissingMask.Length && partMissingMask[i] && def.hideWhenMissing;

                int pIdx = def.parentRuntimeIndex;
                bool parentVisible = pIdx < 0 || outWorldPoses[pIdx].visible;
                bool visible = parentVisible && !selfMissing;

                Vector2 jointWorld;
                float worldAngle;

                float ownSagForward = 0f;
                if (def.sagittalDropCells > 1e-5f && Mathf.Abs(anim.sagittalPitch) > 1e-4f)
                {
                    ownSagForward = def.sagittalDropCells * globalScale * Mathf.Sin(anim.sagittalPitch * Mathf.Deg2Rad);
                }

                if (pIdx < 0)
                {
                    Vector2 rootOffset = (def.localOffset + anim.offset) * globalScale;
                    jointWorld = new Vector2(
                        rootWorldXZ.x + (rootOffset.x * rootCos + rootOffset.y * rootSin),
                        rootWorldXZ.y + (-rootOffset.x * rootSin + rootOffset.y * rootCos));
                    worldAngle = NormalizeAngle360(rootHeadingDeg + def.baseAngle + anim.angle);
                }
                else
                {
                    ref readonly BoneWorldPose parentPose = ref outWorldPoses[pIdx];
                    Vector2 parentSocketUV = def.GetResolvedParentSocketUV(parentPose.activeSector);
                    Vector2 socketWorld = parentPose.UVToWorldXZFlat(parentSocketUV.x, parentSocketUV.y);
                    if (def.localOffset.sqrMagnitude > 1e-8f)
                    {
                        Vector2 staticOff = def.localOffset * globalScale;
                        socketWorld.x += staticOff.x * parentPose.cosTheta + staticOff.y * parentPose.sinTheta;
                        socketWorld.y += -staticOff.x * parentPose.sinTheta + staticOff.y * parentPose.cosTheta;
                    }
                    if (rig.useCoupledSagittalKinematics)
                    {
                        // Coupled sagittal projection: parent bone's sagittalPitch moves parent's distal socket,
                        // keeping proximal hip/shoulder/neck roots 100% locked inside their parent sockets.
                        if (Mathf.Abs(parentPose.ownSagittalForwardCells) > 1e-5f)
                        {
                            socketWorld.x += parentPose.ownSagittalForwardCells * hullSin;
                            socketWorld.y += parentPose.ownSagittalForwardCells * hullCos;
                        }
                    }
                    else if (Mathf.Abs(ownSagForward) > 1e-5f)
                    {
                        // Legacy sagittal projection
                        socketWorld.x += ownSagForward * hullSin;
                        socketWorld.y += ownSagForward * hullCos;
                    }
                    jointWorld = socketWorld;
                    float relAngle = def.isRigid ? 0f : anim.angle;
                    worldAngle = NormalizeAngle360(parentPose.worldAngleDeg + def.baseAngle + relAngle);
                }

                GiantBoneViewSector selfSector;
                if (!sectorOverrides.IsEmpty && i < sectorOverrides.Length)
                {
                    selfSector = sectorOverrides[i];
                }
                else if (def.HasDirectionalVariants)
                {
                    int srcIdx = def.sectorSourceRuntimeIndex;
                    float srcAngle = (uint)srcIdx < (uint)i ? outWorldPoses[srcIdx].worldAngleDeg : worldAngle;
                    selfSector = rig.EvaluateSector(srcAngle);
                }
                else
                {
                    selfSector = GiantBoneViewSector.Dorsal;
                }

                def.GetResolvedSelfParams(selfSector, out Vector2 resolvedDrawSize, out Vector2 pivotUV, out float resolvedLayerOffset);
                Vector2 effScale = def.baseScale * globalScale;
                Vector2 worldSize = new Vector2(
                    Mathf.Max(0.001f, resolvedDrawSize.x * effScale.x),
                    Mathf.Max(0.001f, resolvedDrawSize.y * effScale.y));

                float rad = worldAngle * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad);
                float sin = Mathf.Sin(rad);
                if (pIdx < 0)
                {
                    hullRad = rad;
                    hullCos = cos;
                    hullSin = sin;
                }

                if (pIdx >= 0 && !turretRecoilLocalOffsets.IsEmpty && i < turretRecoilLocalOffsets.Length)
                {
                    Vector2 recOff = turretRecoilLocalOffsets[i];
                    if (recOff.sqrMagnitude > 1e-10f)
                    {
                        Vector2 dynOff = recOff * globalScale;
                        jointWorld.x += dynOff.x * cos + dynOff.y * sin;
                        jointWorld.y += -dynOff.x * sin + dynOff.y * cos;
                    }
                }

                float qOffLocalX = (0.5f - pivotUV.x) * worldSize.x;
                float qOffLocalZ = (0.5f - pivotUV.y) * worldSize.y;
                Vector2 quadCenter = new Vector2(
                    jointWorld.x + (qOffLocalX * cos + qOffLocalZ * sin),
                    jointWorld.y + (-qOffLocalX * sin + qOffLocalZ * cos));

                bool hasSagShear = false;
                Vector2 sagGrad = Vector2.zero;
                Vector2 sagFwdAxis = new Vector2(hullSin, hullCos);
                float sagDetVal = 1f;
                if (rig.useCoupledSagittalKinematics && !def.isRenderedBySkinnedMesh && Mathf.Abs(ownSagForward) > 1e-5f)
                {
                    int childIdx = def.firstArticulatedChildIndex;
                    Vector2 targetUV = (uint)childIdx < (uint)totalNodes
                        ? nodes[childIdx].GetResolvedParentSocketUV(selfSector)
                        : new Vector2(0.5f, 0.88f);
                    float dLx = (targetUV.x - pivotUV.x) * worldSize.x;
                    float dLz = (targetUV.y - pivotUV.y) * worldSize.y;
                    float dSx = dLx * cos + dLz * sin;
                    float dSz = -dLx * sin + dLz * cos;
                    float segLenSq = dSx * dSx + dSz * dSz;
                    if (segLenSq > 1e-5f)
                    {
                        float ch = (dSx * hullSin + dSz * hullCos) / segLenSq;
                        if (Mathf.Abs(ch) > 1e-5f)
                        {
                            float rawDet = 1f + ownSagForward * ch;
                            if (rawDet < 0.30f)
                            {
                                ownSagForward = (0.30f - 1f) / ch;
                            }
                            else if (rawDet > 2.40f)
                            {
                                ownSagForward = (2.40f - 1f) / ch;
                            }
                        }
                        if (Mathf.Abs(ownSagForward) > 1e-5f)
                        {
                            float invLenSq = ownSagForward / segLenSq;
                            sagGrad = new Vector2(dSx * invLenSq, dSz * invLenSq);
                            sagDetVal = Mathf.Max(0.05f, 1f + sagGrad.x * hullSin + sagGrad.y * hullCos);
                            hasSagShear = true;
                        }
                    }
                }

                bool hasFlap = false;
                Vector2 flapHinge = Vector2.zero;
                Vector2 flapNorm = Vector2.zero;
                float flapCos = 1f;
                if (pIdx >= 0 && outWorldPoses[pIdx].hasCoronalFlap)
                {
                    hasFlap = true;
                    flapHinge = outWorldPoses[pIdx].flapHingeXZ;
                    flapNorm = outWorldPoses[pIdx].flapNormalXZ;
                    flapCos = outWorldPoses[pIdx].flapCosPsi;
                }
                float effCoronalFlap = def.baseCoronalFlap + (def.isRigid && pIdx >= 0 ? 0f : anim.coronalFlap);
                if (def.isCoronalFlapHinge && Mathf.Abs(effCoronalFlap) > 1e-4f)
                {
                    float cosPsi = Mathf.Clamp(Mathf.Cos(effCoronalFlap * Mathf.Deg2Rad), 0.25f, 1f);
                    if (!hasFlap)
                    {
                        hasFlap = true;
                        flapHinge = jointWorld;
                        flapNorm = new Vector2(hullCos, -hullSin);
                        flapCos = cosPsi;
                    }
                    else
                    {
                        // Compound nested coronal hinge around the parent's projected joint position so the child's
                        // projected pivot stays locked onto parentPose.UVToWorldXZ(parentSocketUV) with zero detachment.
                        float parentCos = flapCos;
                        float combinedCos = Mathf.Clamp(parentCos * cosPsi, 0.25f, 1f);
                        float denom = 1f - combinedCos;
                        if (denom > 1e-5f)
                        {
                            float parentLatFromHinge = (jointWorld.x - flapHinge.x) * flapNorm.x
                                + (jointWorld.y - flapHinge.y) * flapNorm.y;
                            float parentShiftLat = (parentCos - 1f) * parentLatFromHinge;
                            float hingeOffsetLat = parentShiftLat / denom;
                            flapHinge = new Vector2(
                                jointWorld.x + hingeOffsetLat * flapNorm.x,
                                jointWorld.y + hingeOffsetLat * flapNorm.y);
                        }
                        else
                        {
                            flapHinge = jointWorld;
                        }
                        flapCos = combinedCos;
                    }
                }

                Vector2 projJoint = jointWorld;
                Vector2 projQuadCenter = quadCenter;
                if (hasSagShear)
                {
                    float dSagQ = sagGrad.x * (projQuadCenter.x - jointWorld.x) + sagGrad.y * (projQuadCenter.y - jointWorld.y);
                    projQuadCenter.x += dSagQ * sagFwdAxis.x;
                    projQuadCenter.y += dSagQ * sagFwdAxis.y;
                }
                if (hasFlap && Mathf.Abs(flapCos - 1f) > 1e-5f)
                {
                    float dLatJ = (jointWorld.x - flapHinge.x) * flapNorm.x + (jointWorld.y - flapHinge.y) * flapNorm.y;
                    float shiftJ = (flapCos - 1f) * dLatJ;
                    projJoint = new Vector2(jointWorld.x + shiftJ * flapNorm.x, jointWorld.y + shiftJ * flapNorm.y);

                    float dLatQ = (projQuadCenter.x - flapHinge.x) * flapNorm.x + (projQuadCenter.y - flapHinge.y) * flapNorm.y;
                    float shiftQ = (flapCos - 1f) * dLatQ;
                    projQuadCenter = new Vector2(projQuadCenter.x + shiftQ * flapNorm.x, projQuadCenter.y + shiftQ * flapNorm.y);
                }

                outWorldPoses[i] = new BoneWorldPose
                {
                    nodeIndex = i,
                    visible = visible,
                    jointWorldXZ = projJoint,
                    quadCenterWorldXZ = projQuadCenter,
                    worldAngleDeg = worldAngle,
                    worldSize = worldSize,
                    pivotUV = pivotUV,
                    layerOffset = resolvedLayerOffset,
                    activeSector = selfSector,
                    cosTheta = cos,
                    sinTheta = sin,
                    hasCoronalFlap = hasFlap,
                    hasSagittalShear = hasSagShear,
                    flatJointWorldXZ = jointWorld,
                    flatQuadCenterWorldXZ = quadCenter,
                    flapHingeXZ = flapHinge,
                    flapNormalXZ = flapNorm,
                    flapCosPsi = flapCos,
                    sagGradXZ = sagGrad,
                    sagForwardAxisXZ = sagFwdAxis,
                    sagDet = sagDetVal,
                    ownSagittalForwardCells = ownSagForward,
                    rootHeadingRad = hullRad
                };
            }
        }

        /// <summary>
        /// Solves flat bind poses at root=(0,0), heading=0 deg, and coronalFlap suppressed (flapCosPsi == 1)
        /// so multi-bone 2D skinned layer atlases map 1:1 from flat bind XZ to live world XZ.
        /// </summary>
        public static void SolveFlatBindPoses(GiantRigDef rig, Span<BoneWorldPose> outBindPoses)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = nodes.Length;
            if (outBindPoses.Length < count)
            {
                throw new ArgumentException("outBindPoses buffer is smaller than rig node count.");
            }

            for (int i = 0; i < count; i++)
            {
                BoneNodeDef def = nodes[i];
                int pIdx = def.parentRuntimeIndex;

                Vector2 jointWorld;
                float worldAngle;
                if (pIdx < 0)
                {
                    jointWorld = def.localOffset;
                    worldAngle = NormalizeAngle360(def.baseAngle);
                }
                else
                {
                    ref readonly BoneWorldPose parentPose = ref outBindPoses[pIdx];
                    Vector2 parentSocketUV = def.GetResolvedParentSocketUV(GiantBoneViewSector.Dorsal);
                    jointWorld = parentPose.UVToWorldXZFlat(parentSocketUV.x, parentSocketUV.y);
                    if (def.localOffset.sqrMagnitude > 1e-8f)
                    {
                        jointWorld.x += def.localOffset.x * parentPose.cosTheta + def.localOffset.y * parentPose.sinTheta;
                        jointWorld.y += -def.localOffset.x * parentPose.sinTheta + def.localOffset.y * parentPose.cosTheta;
                    }
                    worldAngle = NormalizeAngle360(parentPose.worldAngleDeg + def.baseAngle);
                }

                def.GetResolvedSelfParams(GiantBoneViewSector.Dorsal, out Vector2 resolvedDrawSize, out Vector2 pivotUV, out float resolvedLayerOffset);
                Vector2 worldSize = new Vector2(
                    Mathf.Max(0.001f, resolvedDrawSize.x * def.baseScale.x),
                    Mathf.Max(0.001f, resolvedDrawSize.y * def.baseScale.y));
                float rad = worldAngle * Mathf.Deg2Rad;
                float cos = Mathf.Cos(rad);
                float sin = Mathf.Sin(rad);
                float qOffLocalX = (0.5f - pivotUV.x) * worldSize.x;
                float qOffLocalZ = (0.5f - pivotUV.y) * worldSize.y;
                Vector2 quadCenter = new Vector2(
                    jointWorld.x + (qOffLocalX * cos + qOffLocalZ * sin),
                    jointWorld.y + (-qOffLocalX * sin + qOffLocalZ * cos));

                outBindPoses[i] = new BoneWorldPose
                {
                    nodeIndex = i,
                    visible = true,
                    jointWorldXZ = jointWorld,
                    quadCenterWorldXZ = quadCenter,
                    worldAngleDeg = worldAngle,
                    worldSize = worldSize,
                    pivotUV = pivotUV,
                    layerOffset = resolvedLayerOffset,
                    activeSector = GiantBoneViewSector.Dorsal,
                    cosTheta = cos,
                    sinTheta = sin,
                    hasCoronalFlap = false,
                    flatJointWorldXZ = jointWorld,
                    flatQuadCenterWorldXZ = quadCenter,
                    flapHingeXZ = Vector2.zero,
                    flapNormalXZ = Vector2.zero,
                    flapCosPsi = 1f,
                    ownSagittalForwardCells = 0f,
                    rootHeadingRad = 0f
                };
            }
        }

        /// <summary>
        /// Computes 2x3 Bind-to-Live affine skinning matrices for every bone in rig:
        ///   P_live = M * P_bind
        /// Guarantees exact joint locking, coupled sagittal axial foreshortening with strict zero lateral drift,
        /// and 3D coronal wingbeat projection across all 360-degree headings.
        /// </summary>
        public static void ComputeBoneSkinningMatrices2x3(
            GiantRigDef rig,
            ReadOnlySpan<BoneWorldPose> flatBindPoses,
            ReadOnlySpan<BoneWorldPose> liveWorldPoses,
            Span<BoneMatrix2x3> outMatrices)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = nodes.Length;
            bool useSag = rig.useCoupledSagittalKinematics;
            float hx = 0f, hz = 1f;
            bool headingSynced = false;

            for (int i = 0; i < count; i++)
            {
                ComputeSingleBoneSkinningMatrix2x3(
                    nodes, count, useSag, flatBindPoses, liveWorldPoses, i,
                    0f, 0f, ref headingSynced, ref hx, ref hz, out outMatrices[i]);
            }
        }

        /// <summary>
        /// Computes 2x3 Bind-to-Live affine skinning matrices ONLY for the active subset of bones referenced by
        /// a skinned mesh, subtracting (rootOffsetX, rootOffsetZ) directly into the translation column.
        /// </summary>
        public static void ComputeBoneSkinningMatrices2x3Subset(
            GiantRigDef rig,
            ReadOnlySpan<BoneWorldPose> flatBindPoses,
            ReadOnlySpan<BoneWorldPose> liveWorldPoses,
            Span<BoneMatrix2x3> outMatrices,
            ReadOnlySpan<int> boneIndices,
            float rootOffsetX = 0f,
            float rootOffsetZ = 0f)
        {
            BoneNodeDef[] nodes = rig.SortedNodes;
            int count = nodes.Length;
            bool useSag = rig.useCoupledSagittalKinematics;
            float hx = 0f, hz = 1f;
            bool headingSynced = false;

            for (int k = 0; k < boneIndices.Length; k++)
            {
                int i = boneIndices[k];
                if ((uint)i >= (uint)count)
                {
                    continue;
                }
                ComputeSingleBoneSkinningMatrix2x3(
                    nodes, count, useSag, flatBindPoses, liveWorldPoses, i,
                    rootOffsetX, rootOffsetZ, ref headingSynced, ref hx, ref hz, out outMatrices[i]);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void ComputeSingleBoneSkinningMatrix2x3(
            BoneNodeDef[] nodes,
            int count,
            bool useCoupledSagittal,
            ReadOnlySpan<BoneWorldPose> flatBindPoses,
            ReadOnlySpan<BoneWorldPose> liveWorldPoses,
            int i,
            float rootOffsetX,
            float rootOffsetZ,
            ref bool headingSynced,
            ref float hx,
            ref float hz,
            out BoneMatrix2x3 result)
        {
            ref readonly BoneWorldPose bp = ref flatBindPoses[i];
            ref readonly BoneWorldPose lp = ref liveWorldPoses[i];

            Vector2 j0 = bp.flatJointWorldXZ;
            Vector2 j1 = lp.flatJointWorldXZ;

            // Exact trig identity for dAng = lp.worldAngleDeg - bp.worldAngleDeg using precomputed pose sin/cos (zero trig calls).
            float ca = lp.cosTheta * bp.cosTheta + lp.sinTheta * bp.sinTheta;
            float sa = lp.sinTheta * bp.cosTheta - lp.cosTheta * bp.sinTheta;

            // RotateCW(dAng): [ca, sa; -sa, ca]
            float r00 = ca, r01 = sa;
            float r10 = -sa, r11 = ca;
            float tx = j1.x - (r00 * j0.x + r01 * j0.y);
            float tz = j1.y - (r10 * j0.x + r11 * j0.y);

            if (useCoupledSagittal && Mathf.Abs(lp.ownSagittalForwardCells) > 1e-5f)
            {
                if (!headingSynced)
                {
                    hx = Mathf.Sin(lp.rootHeadingRad);
                    hz = Mathf.Cos(lp.rootHeadingRad);
                    headingSynced = true;
                }
                int primChild = nodes[i].primarySagittalChildIndex;
                float uH;
                if ((uint)primChild < (uint)count)
                {
                    Vector2 u0 = flatBindPoses[primChild].flatJointWorldXZ - j0;
                    float urx = r00 * u0.x + r01 * u0.y;
                    float urz = r10 * u0.x + r11 * u0.y;
                    uH = urx * hx + urz * hz;
                }
                else
                {
                    uH = j0.y < -1.0f ? -0.75f : (j0.y > 1.0f ? +0.95f : -0.70f);
                }

                if (Mathf.Abs(uH) > 0.08f)
                {
                    float kSag = Mathf.Clamp(lp.ownSagittalForwardCells / uH, -0.34f, 0.34f);
                    // S_sag = I + kSag * outer(h, h) applied around j1
                    float s00 = 1f + kSag * hx * hx;
                    float s01 = kSag * hx * hz;
                    float s10 = kSag * hz * hx;
                    float s11 = 1f + kSag * hz * hz;

                    float dtx = tx - j1.x;
                    float dtz = tz - j1.y;
                    tx = j1.x + (s00 * dtx + s01 * dtz);
                    tz = j1.y + (s10 * dtx + s11 * dtz);

                    float nr00 = s00 * r00 + s01 * r10;
                    float nr01 = s00 * r01 + s01 * r11;
                    float nr10 = s10 * r00 + s11 * r10;
                    float nr11 = s10 * r01 + s11 * r11;
                    r00 = nr00; r01 = nr01; r10 = nr10; r11 = nr11;
                }
            }

            if (lp.hasCoronalFlap && Mathf.Abs(lp.flapCosPsi - 1f) > 1e-5f)
            {
                float nx = lp.flapNormalXZ.x;
                float nz = lp.flapNormalXZ.y;
                Vector2 hinge = lp.flapHingeXZ;
                float kFlap = lp.flapCosPsi - 1f;

                float p00 = 1f + kFlap * nx * nx;
                float p01 = kFlap * nx * nz;
                float p10 = kFlap * nz * nx;
                float p11 = 1f + kFlap * nz * nz;

                float dtx = tx - hinge.x;
                float dtz = tz - hinge.y;
                tx = hinge.x + (p00 * dtx + p01 * dtz);
                tz = hinge.y + (p10 * dtx + p11 * dtz);

                float nr00 = p00 * r00 + p01 * r10;
                float nr01 = p00 * r01 + p01 * r11;
                float nr10 = p10 * r00 + p11 * r10;
                float nr11 = p10 * r01 + p11 * r11;
                r00 = nr00; r01 = nr01; r10 = nr10; r11 = nr11;
            }

            result = new BoneMatrix2x3
            {
                m00 = r00,
                m01 = r01,
                m02 = tx - rootOffsetX,
                m10 = r10,
                m11 = r11,
                m12 = tz - rootOffsetZ
            };
        }

        public static float NormalizeAngle360(float deg)
        {
            deg %= 360f;
            if (deg < 0f)
            {
                deg += 360f;
            }
            return deg;
        }
    }

    public struct BoneMatrix2x3
    {
        public float m00, m01, m02;
        public float m10, m11, m12;

        public Vector2 TransformPoint(Vector2 p)
        {
            return new Vector2(
                m00 * p.x + m01 * p.y + m02,
                m10 * p.x + m11 * p.y + m12);
        }
    }
}
