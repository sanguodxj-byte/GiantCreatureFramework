using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public enum GiantVfxPaletteTheme
    {
        Auto = 0,
        ProtossShieldPlasma = 1
    }

    public enum GiantHitVfxKind
    {
        Auto = 0,
        Sparks = 1,
        FleshBlood = 2,
        BioferriteDark = 3,
        PlasmaFlash = 4,
        None = 5
    }

    public enum GiantSeverVfxKind
    {
        Auto = 0,
        MechExplosion = 1,
        FleshSever = 2,
        VoidRupture = 3,
        None = 4
    }

    public enum GiantBoneAuraVfxKind
    {
        None = 0,
        ReactorCoreGlow = 1,
        VoidCrucibleMist = 2,
        BioferritePulse = 3,
        PlasmaVent = 4
    }

    public enum GiantBoneViewSector : byte
    {
        Dorsal = 0,
        East = 1,
        West = 2
    }

    public enum BoneCollisionRole : byte
    {
        Auto = 0,
        None = 1,
        Canopy = 2,
        CoreHull = 3
    }

    /// <summary>
    /// Optional directional sprite and socket override used when a bone is viewed in an oblique lateral sector
    /// (typically East [45, 135] deg or West [225, 315] deg). Uses -9999 sentinels for unset fields to match BoneNodeDef.
    /// </summary>
    public class BoneDirectionalVariantDef
    {
        public string texPath;
        public string glowTexPath;
        public string silhouetteTexPath;
        public Vector2 drawSize = new Vector2(-9999f, -9999f);
        public Vector2 pivotUV = new Vector2(-9999f, -9999f);
        public Vector2 pivotPixel = new Vector2(-9999f, -9999f);
        public Vector2 parentSocketUV = new Vector2(-9999f, -9999f);
        public Vector2 parentSocketPixel = new Vector2(-9999f, -9999f);
        public float layerOffset = -9999f;
        public Vector2 muzzleUV = new Vector2(-9999f, -9999f);
        public Vector2 muzzlePixel = new Vector2(-9999f, -9999f);
        public float muzzleAngleOffset = -9999f;
        public Vector2 persistentVfxUV = new Vector2(-9999f, -9999f);
        public Vector2 persistentVfxPixel = new Vector2(-9999f, -9999f);
        public List<GiantBoneEmitterSocketDef> emitterSockets = new List<GiantBoneEmitterSocketDef>();

        [Unsaved]
        public Vector2 resolvedDrawSize = Vector2.one;

        [Unsaved]
        public Vector2 resolvedPivotUV = new Vector2(0.5f, 0.5f);

        [Unsaved]
        public Vector2 resolvedParentSocketUV = new Vector2(0.5f, 0.5f);

        [Unsaved]
        public float resolvedLayerOffset = 0f;

        [Unsaved]
        public Vector2 resolvedMuzzleUV = new Vector2(0.5f, 0.88f);

        [Unsaved]
        public float resolvedMuzzleAngleOffset = 0f;

        [Unsaved]
        public Vector2 resolvedPersistentVfxUV = new Vector2(0.5f, 0.5f);

        public bool HasCustomDrawSize => drawSize.x > -9000f;
        public bool HasCustomPivotPixel => pivotPixel.x > -9000f;
        public bool HasCustomPivotUV => pivotUV.x > -9000f;
        public bool HasCustomParentSocketPixel => parentSocketPixel.x > -9000f;
        public bool HasCustomParentSocketUV => parentSocketUV.x > -9000f;
        public bool HasCustomLayerOffset => layerOffset > -9000f;
        public bool HasCustomMuzzlePixel => muzzlePixel.x > -9000f;
        public bool HasCustomMuzzleUV => muzzleUV.x > -9000f;
        public bool HasCustomMuzzleAngleOffset => muzzleAngleOffset > -9000f;
        public bool HasCustomPersistentVfxPixel => persistentVfxPixel.x > -9000f;
        public bool HasCustomPersistentVfxUV => persistentVfxUV.x > -9000f;

        public bool HasSelfOverride =>
            !string.IsNullOrEmpty(texPath) ||
            !string.IsNullOrEmpty(glowTexPath) ||
            !string.IsNullOrEmpty(silhouetteTexPath) ||
            HasCustomDrawSize ||
            HasCustomPivotPixel ||
            HasCustomPivotUV ||
            HasCustomLayerOffset ||
            HasCustomMuzzlePixel ||
            HasCustomMuzzleUV ||
            HasCustomMuzzleAngleOffset ||
            HasCustomPersistentVfxPixel ||
            HasCustomPersistentVfxUV ||
            (emitterSockets != null && emitterSockets.Count > 0);

        public void ResolveCoordinates(BoneNodeDef owner, int parentTexRes)
        {
            float res = Mathf.Max(1f, owner.textureResolution);
            float pRes = Mathf.Max(1f, parentTexRes);

            resolvedDrawSize = HasCustomDrawSize ? drawSize : owner.drawSize;

            resolvedPivotUV = HasCustomPivotPixel
                ? new Vector2(pivotPixel.x / res, 1f - (pivotPixel.y / res))
                : (HasCustomPivotUV ? pivotUV : owner.resolvedPivotUV);

            resolvedParentSocketUV = HasCustomParentSocketPixel
                ? new Vector2(parentSocketPixel.x / pRes, 1f - (parentSocketPixel.y / pRes))
                : (HasCustomParentSocketUV ? parentSocketUV : owner.resolvedParentSocketUV);

            resolvedLayerOffset = HasCustomLayerOffset ? layerOffset : owner.layerOffset;

            resolvedMuzzleUV = HasCustomMuzzlePixel
                ? new Vector2(muzzlePixel.x / res, 1f - (muzzlePixel.y / res))
                : (HasCustomMuzzleUV ? muzzleUV : owner.resolvedMuzzleUV);

            resolvedMuzzleAngleOffset = HasCustomMuzzleAngleOffset ? muzzleAngleOffset : owner.muzzleAngleOffset;

            resolvedPersistentVfxUV = HasCustomPersistentVfxPixel
                ? new Vector2(persistentVfxPixel.x / res, 1f - (persistentVfxPixel.y / res))
                : (HasCustomPersistentVfxUV ? persistentVfxUV : owner.resolvedPersistentVfxUV);

            if (emitterSockets != null)
            {
                for (int i = 0; i < emitterSockets.Count; i++)
                {
                    emitterSockets[i]?.ResolveCoordinates(owner.textureResolution);
                }
            }
        }
    }

    public class GiantFlightEntryDef
    {
        public bool enabled = true;
        public GiantAnimDef flightAnim;
        public int durationTicks = 150;
        public float startOffsetCells = 18f;
        public float startAltitudeOffset = 6.5f;
        public int wingFoldTransitionTicks = 32;
        public float landingDustScale = 3.2f;
        public float landingCameraShake = 0.35f;
        public SoundDef landingSoundDef;
    }

    /// <summary>
    /// Declarative hydraulic/recuperator recoil profile for turret parts (&lt;isTurret&gt;true&lt;/isTurret&gt; or turret barrel sub-parts).
    /// Translates the target turret or barrel bone strictly opposite to the live world firing direction (-fireDir) upon
    /// projectile or ability discharge, followed by smooth monotonic recuperation back to rest socket.
    /// </summary>
    public class GiantTurretRecoilDef
    {
        public float distanceCells = 0.28f;
        public int kickTicks = 2;
        public int recoveryTicks = 12;
        public float maxAccumulatedCells = 0f;
        public string recoilBoneId;

        [Unsaved]
        public int resolvedRecoilBoneIndex = -1;

        public float EffectiveMaxAccumulatedCells =>
            maxAccumulatedCells > 0.001f ? Mathf.Max(distanceCells, maxAccumulatedCells) : distanceCells * 1.35f;
    }

    /// <summary>
    /// Declarative crew seat mount point on a BoneNodeDef (&lt;crewSeats&gt;).
    /// Binds a named seatId from CompProperties_GiantCockpit / CompProperties_GiantCrew to an exact
    /// anatomical or mechanical bone mount so boarded occupants can be rendered completely above the part
    /// and follow 100% of bone FK motion, root/gait offsets, steering bend, 360-degree heading, and flight lift.
    /// </summary>
    public class GiantBoneCrewSeatDef
    {
        public string seatId;

        /// <summary>
        /// Optional normalized [0..1, 0..1] mount UV on the owner bone's canvas.
        /// Negative sentinel (-9999, -9999) falls back to the owner bone's resolved pivotUV (jointWorldXZ).
        /// </summary>
        public Vector2 mountUV = new Vector2(-9999f, -9999f);

        /// <summary>
        /// Optional pixel coordinate (0..textureResolution, origin at top-left of the PNG) for artist-friendly authoring.
        /// Overrides mountUV when non-negative.
        /// </summary>
        public Vector2 mountPixel = new Vector2(-9999f, -9999f);

        /// <summary>
        /// Additional local offset in the owner bone's XZ world-scaled coordinate frame (+X right, +Y/+Z forward along bone heading).
        /// </summary>
        public Vector2 localOffset = Vector2.zero;

        /// <summary>
        /// Optional screen-space XZ offset in map cells applied after bone rotation (e.g. (0, 0.12) to align colonist hips on saddle).
        /// </summary>
        public Vector2 screenOffset = Vector2.zero;

        /// <summary>
        /// Clockwise facing offset in degrees added to the owner bone's worldAngleDeg when computing occupant facing.
        /// </summary>
        public float facingOffsetDeg = 0f;

        /// <summary>
        /// Intra-giant layerOffset for occupant altitude calculation. When renderAboveAllParts is true,
        /// runtime automatically clamps effective layerOffset above the rig's highest authored part/mesh layer.
        /// </summary>
        public float layerOffset = 8.0f;

        /// <summary>
        /// Uniform visual scale multiplier applied to the rendered occupant PawnRenderTree (default 1.0).
        /// </summary>
        public float drawScale = 1.0f;

        /// <summary>
        /// Whether an occupant in this seat should be visually rendered when CompProperties_GiantCockpit.renderCrewOnRig is enabled.
        /// </summary>
        public bool renderOccupant = true;

        /// <summary>
        /// Ensures the occupant's entire PawnRenderTree (including -10 layer back/pack nodes) renders strictly above all dorsal parts of the giant.
        /// </summary>
        public bool renderAboveAllParts = true;

        /// <summary>
        /// When false (default), renders RimWorld's upright 2.5D Pawn sprite snapped to 4-way Rot4 facing from worldFacingDeg.
        /// When true, additionally rotates the Pawn sprite quad continuously by worldFacingDeg.
        /// </summary>
        public bool rotateSpriteWithBone = false;

        /// <summary>
        /// Whether to draw the occupant's primary equipped weapon while seated (when available).
        /// </summary>
        public bool renderOccupantWeapon = false;

        [Unsaved]
        public Vector2 resolvedMountUV = new Vector2(0.5f, 0.5f);

        [Unsaved]
        public int resolvedBoneIndex = -1;

        [Unsaved]
        public string resolvedBoneId;

        public bool HasCustomMountPixel => mountPixel.x >= 0f && mountPixel.y >= 0f;
        public bool HasCustomMountUV => mountUV.x > -999f && mountUV.y > -999f;

        public void ResolveCoordinates(BoneNodeDef owner)
        {
            float res = Mathf.Max(1f, owner?.textureResolution ?? 512);
            if (HasCustomMountPixel)
            {
                resolvedMountUV = new Vector2(mountPixel.x / res, 1f - (mountPixel.y / res));
            }
            else if (HasCustomMountUV)
            {
                resolvedMountUV = mountUV;
            }
            else
            {
                resolvedMountUV = owner != null ? owner.resolvedPivotUV : new Vector2(0.5f, 0.5f);
            }
        }
    }

    public class BoneNodeDef
    {
        public string nodeId;
        public string label;
        public string parentId;
        public string texPath;

        public Vector2 drawSize = Vector2.one;

        /// <summary>
        /// Normalized [0..1, 0..1] pivot in Unity quad UV space (0,0 = bottom-left, 0.5,0.5 = center, 0.5,1.0 = top-center).
        /// Overridden automatically during Resolve if pivotPixel is specified.
        /// </summary>
        public Vector2 pivotUV = new Vector2(0.5f, 0.5f);

        /// <summary>
        /// Optional pixel coordinate (0..textureResolution, origin at top-left of the 512x512 PNG) for artist-friendly authoring.
        /// Negative sentinel (-9999, -9999) indicates unset, avoiding DirectXmlToObject Nullable&lt;Vector2&gt; quirks.
        /// </summary>
        public Vector2 pivotPixel = new Vector2(-9999f, -9999f);

        /// <summary>
        /// Normalized [0..1, 0..1] attachment socket on parent bone's UV canvas.
        /// Overridden automatically during Resolve if parentSocketPixel is specified.
        /// </summary>
        public Vector2 parentSocketUV = new Vector2(0.5f, 0.5f);

        /// <summary>
        /// Optional pixel coordinate (0..textureResolution, origin at top-left of parent's 512x512 PNG).
        /// </summary>
        public Vector2 parentSocketPixel = new Vector2(-9999f, -9999f);

        /// <summary>
        /// Additional local offset in parent bone's XZ world-scaled coordinate frame.
        /// </summary>
        public Vector2 localOffset = Vector2.zero;

        /// <summary>
        /// Rest-pose clockwise angle in degrees relative to parent bone.
        /// </summary>
        public float baseAngle = 0f;

        /// <summary>
        /// Optional vertical limb-segment drop length (in map cells along the unseen vertical camera axis)
        /// used to project 3D vertical sagittal-plane joint pitch (&lt;sagittalPitch&gt;) cleanly onto top-down forward motion
        /// without non-uniform 2D sprite shearing or flat horizontal rotation wiggling.
        /// </summary>
        public float sagittalDropCells = 0f;

        /// <summary>
        /// When true, marks this bone's joint as a longitudinal 3D coronal-plane flap hinge (e.g. wing_shoulder_left/right),
        /// projecting the wing subtree smoothly across top-down XZ by cos(baseCoronalFlap + coronalFlap) without breaking child pin-mounts.
        /// </summary>
        public bool isCoronalFlapHinge = false;
        public float baseCoronalFlap = 0f;

        public Vector2 baseScale = Vector2.one;

        /// <summary>
        /// Render layer offset relative to root PawnRenderNode (negative = underneath carapace, positive = above).
        /// </summary>
        public float layerOffset = 0f;

        public int textureResolution = 512;

        public BodyPartDef linkedBodyPart;
        public string linkedBodyPartCustomLabel;
        public int linkedBodyPartIndex = -1;
        public bool hideWhenMissing = true;

        /// <summary>
        /// When true on a child bone, locks this node as a welded/bolted rigid-body sub-part of its parent bone
        /// (0 deg relative rotation and (0,0) local offset across all animations, steering solvers, and procedural offsets),
        /// while preserving independent 1:1 BodyPart hitbox routing, armor multipliers, per-part hit flash, and severing.
        /// </summary>
        public bool isRigid = false;

        /// <summary>
        /// Marks this bone (and its child barrel/shield subtree) as an independently traversing turret part.
        /// Turret semantics belong strictly to individual parts rather than the whole giant, allowing a single
        /// creature or vehicle to combine rotating turrets with hull-fixed or biological limb/tail weapons.
        /// </summary>
        public bool isTurret = false;
        public float maxTurretAimAngleDeg = 180f;
        public GiantTurretRecoilDef turretRecoil;

        /// <summary>
        /// Marks this bone as a ground-vehicle drive module (e.g. port/starboard caterpillar track pod or wheel bogie).
        /// Strictly decoupled from turret and weapon bones: severing drive bones degrades or immobilizes chassis movement
        /// without affecting turret rotation or weapon operation.
        /// </summary>
        public bool isLocomotionDrivePart = false;
        public float locomotionSideSign = 0f;

        /// <summary>
        /// Marks this bone as a flight lift or antigravity propulsion module (e.g. left/right wyvern wing or carrier thruster/core).
        /// Strictly decoupled from weapon and turret parts: losing lift bones degrades flight speed or triggers forced landing
        /// without disarming intact jaws, tails, turrets, or carrier hangars.
        /// </summary>
        public bool isFlightLiftPart = false;

        /// <summary>
        /// Marks this bone as an aquatic propulsion organ or naval drive module (e.g. caudal fin, flipper, waterjet, ship screw/propeller, paddlewheel).
        /// Losing aquatic propulsion bones degrades water cruising speed (or immobilizes in water when configured) without disarming turrets or other limbs.
        /// </summary>
        public bool isAquaticPropulsionPart = false;

        public bool contributesToHitbox = true;
        public bool contributesToCollision = true;
        public BoneCollisionRole collisionRole = BoneCollisionRole.Auto;
        public byte alphaThreshold = 32;

        [Unsaved]
        public BoneCollisionRole resolvedCollisionRole = BoneCollisionRole.CoreHull;

        public BoneCollisionRole ResolveEffectiveCollisionRole()
        {
            if (collisionRole != BoneCollisionRole.Auto)
            {
                return collisionRole;
            }
            if (contributesToCollision)
            {
                return BoneCollisionRole.CoreHull;
            }
            return contributesToHitbox ? BoneCollisionRole.Canopy : BoneCollisionRole.None;
        }

        /// <summary>
        /// Optional [0..1] procedural tracking weight toward current combat target.
        /// </summary>
        public float aimAtTargetWeight = 0f;
        public float aimMaxAngle = 35f;
        public float aimReferenceAngleOffset = 0f;

        /// <summary>
        /// Optional custom ShaderTypeDef applied to this bone's graphic; defaults to Cutout when null.
        /// </summary>
        public ShaderTypeDef shaderTypeDef;

        /// <summary>
        /// Continuous additive local spin speed in clockwise degrees per tick for turbine rotors.
        /// </summary>
        public float continuousSpinDegPerTick = 0f;

        /// <summary>
        /// Boosts reactor conduit emission multiplier on core turbine and blast shield nodes.
        /// </summary>
        public bool isReactorCoreBone = false;

        /// <summary>
        /// Marks this bone's linked BodyPartRecord as an instant-kill lethal core weakpoint.
        /// Destroying it kills the giant immediately regardless of remaining total HP
        /// (or advances CompGiantPhaseTransform to the next stage when HasNextStage is true).
        /// </summary>
        public bool lethalWhenDestroyed = false;

        /// <summary>
        /// Muzzle anchor UV/pixel on this bone for ranged projectile launch and muzzle flash placement.
        /// </summary>
        public Vector2 muzzleUV = new Vector2(0.5f, 0.88f);
        public Vector2 muzzlePixel = new Vector2(-9999f, -9999f);

        /// <summary>
        /// Clockwise local angle offset in degrees added to this bone's worldAngleDeg when firing from its muzzle
        /// (e.g. 180 for downward-authored scorpion tail stingers that arch -180 deg overhead to aim forward).
        /// </summary>
        public float muzzleAngleOffset = 0f;
        public GiantAnimVfxPreset muzzleVfxPreset = GiantAnimVfxPreset.MuzzleBlast;
        public float muzzleVfxScale = 1.0f;
        public Color muzzleVfxColor = new Color(0.38f, 0.92f, 1f, 0.9f);

        /// <summary>
        /// Optional named projectile / VFX emitter sockets authored on this bone's 512x512 sprite canvas.
        /// </summary>
        public List<GiantBoneEmitterSocketDef> emitterSockets = new List<GiantBoneEmitterSocketDef>();

        /// <summary>
        /// Declarative part-driven ranged, melee, and carrier catapult attack modules mounted on this bone.
        /// </summary>
        public List<GiantPartAttackDef> partAttacks = new List<GiantPartAttackDef>();

        /// <summary>
        /// Optional unlit emissive overlay texture rendered at the exact bone world pose.
        /// </summary>
        public string glowTexPath;
        public Color glowColor = Color.white;
        public float glowLayerOffset = 0.05f;

        /// <summary>
        /// Optional dark outer-silhouette underlay texture merged beneath all hull parts to form a clean union outline without internal seams.
        /// </summary>
        public string silhouetteTexPath;
        public float silhouetteLayerOffset = -9.2f;

        /// <summary>
        /// Per-bone impact, severing, and persistent ambient VFX configuration.
        /// </summary>
        public GiantHitVfxKind hitVfxPreset = GiantHitVfxKind.Auto;
        public GiantSeverVfxKind severVfxPreset = GiantSeverVfxKind.Auto;
        public float severVfxScale = 1.2f;
        public GiantBoneAuraVfxKind persistentVfxPreset = GiantBoneAuraVfxKind.None;
        public Vector2 persistentVfxUV = new Vector2(0.5f, 0.5f);
        public Vector2 persistentVfxPixel = new Vector2(-9999f, -9999f);
        public int persistentVfxIntervalTicks = 24;
        public float persistentVfxScale = 1.0f;

        /// <summary>
        /// Per-bone combat damage multiplier (< 1 heavy armor, > 1 weakpoint), armor multiplier, and ricochet deflect probability.
        /// </summary>
        public float damageMultiplier = 1f;
        public float armorMultiplier = 1f;
        public float deflectChance = 0f;

        /// <summary>
        /// Optional nodeId whose world angle selects this bone's view sector (e.g. hull_chassis for fixed skirts/tracks,
        /// turret_body for gun barrel). Defaults to this bone's own worldAngleDeg when empty.
        /// </summary>
        public string sectorSourceBoneId;
        public BoneDirectionalVariantDef eastVariant;
        public BoneDirectionalVariantDef westVariant;

        /// <summary>
        /// Optional named crew seat mount points (&lt;crewSeats&gt;) anchored to this bone part.
        /// A single part may host one or multiple crew seats (e.g. tandem dorsal saddle or multi-seat deck).
        /// </summary>
        public List<GiantBoneCrewSeatDef> crewSeats = new List<GiantBoneCrewSeatDef>();

        [Unsaved]
        public int runtimeIndex = -1;

        [Unsaved]
        public int parentRuntimeIndex = -1;

        [Unsaved]
        public int sectorSourceRuntimeIndex = -1;

        [Unsaved]
        public int primarySagittalChildIndex = -1;

        [Unsaved]
        public int firstArticulatedChildIndex = -1;

        [Unsaved]
        public bool isRenderedBySkinnedMesh = false;

        [Unsaved]
        public float skinnedMeshSurfaceLayerOffset = float.NegativeInfinity;

        [Unsaved]
        public Vector2 resolvedPivotUV = new Vector2(0.5f, 0.5f);

        [Unsaved]
        public Vector2 resolvedParentSocketUV = new Vector2(0.5f, 0.5f);

        [Unsaved]
        public Vector2 resolvedMuzzleUV = new Vector2(0.5f, 0.88f);

        [Unsaved]
        public Vector2 resolvedPersistentVfxUV = new Vector2(0.5f, 0.5f);

        public bool HasCustomPivotPixel => pivotPixel.x > -9000f;
        public bool HasCustomParentSocketPixel => parentSocketPixel.x > -9000f;
        public bool HasCustomMuzzlePixel => muzzlePixel.x > -9000f;
        public bool HasCustomPersistentVfxPixel => persistentVfxPixel.x > -9000f;

        public void ResolveCoordinates(int parentTexRes)
        {
            float res = Mathf.Max(1f, textureResolution);
            if (HasCustomPivotPixel)
            {
                resolvedPivotUV = new Vector2(
                    pivotPixel.x / res,
                    1f - (pivotPixel.y / res));
            }
            else
            {
                resolvedPivotUV = pivotUV;
            }

            float pRes = Mathf.Max(1f, parentTexRes);
            if (HasCustomParentSocketPixel)
            {
                resolvedParentSocketUV = new Vector2(
                    parentSocketPixel.x / pRes,
                    1f - (parentSocketPixel.y / pRes));
            }
            else
            {
                resolvedParentSocketUV = parentSocketUV;
            }

            if (HasCustomMuzzlePixel)
            {
                resolvedMuzzleUV = new Vector2(
                    muzzlePixel.x / res,
                    1f - (muzzlePixel.y / res));
            }
            else
            {
                resolvedMuzzleUV = muzzleUV;
            }

            if (HasCustomPersistentVfxPixel)
            {
                resolvedPersistentVfxUV = new Vector2(
                    persistentVfxPixel.x / res,
                    1f - (persistentVfxPixel.y / res));
            }
            else
            {
                resolvedPersistentVfxUV = persistentVfxUV;
            }

            if (emitterSockets != null)
            {
                for (int i = 0; i < emitterSockets.Count; i++)
                {
                    emitterSockets[i]?.ResolveCoordinates(textureResolution);
                }
            }

            if (crewSeats != null)
            {
                for (int i = 0; i < crewSeats.Count; i++)
                {
                    crewSeats[i]?.ResolveCoordinates(this);
                }
            }

            eastVariant?.ResolveCoordinates(this, parentTexRes);
            westVariant?.ResolveCoordinates(this, parentTexRes);
        }

        public bool HasDirectionalVariants =>
            (eastVariant != null && eastVariant.HasSelfOverride) ||
            (westVariant != null && westVariant.HasSelfOverride);

        public BoneDirectionalVariantDef GetVariant(GiantBoneViewSector sector)
        {
            return sector switch
            {
                GiantBoneViewSector.East => eastVariant,
                GiantBoneViewSector.West => westVariant,
                _ => null
            };
        }

        public string GetTexPath(GiantBoneViewSector sector)
        {
            string variantPath = GetVariant(sector)?.texPath;
            return !string.IsNullOrEmpty(variantPath) ? variantPath : texPath;
        }

        public string GetGlowTexPath(GiantBoneViewSector sector)
        {
            string variantPath = GetVariant(sector)?.glowTexPath;
            return !string.IsNullOrEmpty(variantPath) ? variantPath : glowTexPath;
        }

        public string GetSilhouetteTexPath(GiantBoneViewSector sector)
        {
            string variantPath = GetVariant(sector)?.silhouetteTexPath;
            return !string.IsNullOrEmpty(variantPath) ? variantPath : silhouetteTexPath;
        }

        public float GetLayerOffset(GiantBoneViewSector sector)
        {
            BoneDirectionalVariantDef variant = GetVariant(sector);
            return variant != null ? variant.resolvedLayerOffset : layerOffset;
        }

        public float GetRenderedSurfaceLayerOffset(GiantBoneViewSector sector)
        {
            if (isRenderedBySkinnedMesh && !float.IsNegativeInfinity(skinnedMeshSurfaceLayerOffset))
            {
                return skinnedMeshSurfaceLayerOffset;
            }
            return GetLayerOffset(sector);
        }

        public Vector2 GetResolvedParentSocketUV(GiantBoneViewSector parentSector)
        {
            BoneDirectionalVariantDef variant = GetVariant(parentSector);
            return variant != null ? variant.resolvedParentSocketUV : resolvedParentSocketUV;
        }

        public void GetResolvedSelfParams(
            GiantBoneViewSector sector,
            out Vector2 outDrawSize,
            out Vector2 outPivotUV,
            out float outLayerOffset)
        {
            BoneDirectionalVariantDef variant = GetVariant(sector);
            if (variant != null)
            {
                outDrawSize = variant.resolvedDrawSize;
                outPivotUV = variant.resolvedPivotUV;
                outLayerOffset = variant.resolvedLayerOffset;
                return;
            }
            outDrawSize = drawSize;
            outPivotUV = resolvedPivotUV;
            outLayerOffset = layerOffset;
        }

        public Vector2 GetResolvedPersistentVfxUV(GiantBoneViewSector sector)
        {
            BoneDirectionalVariantDef variant = GetVariant(sector);
            return variant != null ? variant.resolvedPersistentVfxUV : resolvedPersistentVfxUV;
        }

        public bool TryGetEmitterSocket(string socketId, out GiantBoneEmitterSocketDef socketDef)
        {
            return TryGetEmitterSocket(socketId, GiantBoneViewSector.Dorsal, out socketDef);
        }

        public bool TryGetEmitterSocket(string socketId, GiantBoneViewSector sector, out GiantBoneEmitterSocketDef socketDef)
        {
            if (!string.IsNullOrEmpty(socketId))
            {
                List<GiantBoneEmitterSocketDef> variantSockets = GetVariant(sector)?.emitterSockets;
                if (variantSockets != null)
                {
                    for (int i = 0; i < variantSockets.Count; i++)
                    {
                        GiantBoneEmitterSocketDef cand = variantSockets[i];
                        if (cand != null && string.Equals(cand.socketId, socketId, StringComparison.OrdinalIgnoreCase))
                        {
                            socketDef = cand;
                            return true;
                        }
                    }
                }

                if (emitterSockets != null)
                {
                    for (int i = 0; i < emitterSockets.Count; i++)
                    {
                        GiantBoneEmitterSocketDef cand = emitterSockets[i];
                        if (cand != null && string.Equals(cand.socketId, socketId, StringComparison.OrdinalIgnoreCase))
                        {
                            socketDef = cand;
                            return true;
                        }
                    }
                }
            }
            socketDef = null;
            return false;
        }

        public void ResolveEmitterSocketOrMuzzle(
            string socketId,
            out Vector2 uv,
            out float angleOffsetDeg,
            out GiantAnimVfxPreset vfxPreset,
            out float vfxScale,
            out Color vfxColor)
        {
            ResolveEmitterSocketOrMuzzle(
                socketId,
                GiantBoneViewSector.Dorsal,
                out uv,
                out angleOffsetDeg,
                out vfxPreset,
                out vfxScale,
                out vfxColor);
        }

        public void ResolveEmitterSocketOrMuzzle(
            string socketId,
            GiantBoneViewSector sector,
            out Vector2 uv,
            out float angleOffsetDeg,
            out GiantAnimVfxPreset vfxPreset,
            out float vfxScale,
            out Color vfxColor)
        {
            if (TryGetEmitterSocket(socketId, sector, out GiantBoneEmitterSocketDef sock))
            {
                uv = sock.resolvedUV;
                angleOffsetDeg = sock.angleOffset;
                vfxPreset = sock.vfxPreset;
                vfxScale = sock.vfxScale;
                vfxColor = sock.vfxColor;
                return;
            }

            BoneDirectionalVariantDef variant = GetVariant(sector);
            uv = variant != null ? variant.resolvedMuzzleUV : resolvedMuzzleUV;
            angleOffsetDeg = variant != null ? variant.resolvedMuzzleAngleOffset : muzzleAngleOffset;
            vfxPreset = muzzleVfxPreset;
            vfxScale = muzzleVfxScale;
            vfxColor = muzzleVfxColor;
        }
    }

    public class GiantRigDef : Def
    {
        public List<BoneNodeDef> nodes = new List<BoneNodeDef>();

        public float turnSpeedDegPerTick = 2.25f;
        public bool enablePivotTurnBeforeMove = true;
        public float pivotTurnEnterAngleDeg = 32f;
        public float pivotTurnExitAngleDeg = 9f;
        public float movingTurnSpeedFactor = 0.65f;
        public float hitCoverageThreshold = 0.08f;
        public float pawnExclusionCoverageThreshold = 0.16f;
        public float collisionCoverageThreshold = 0.45f;
        public int collisionPathCost = 10000;
        public int pawnCanopyPathCost = 1800;
        public int coreClearanceRadius = 1;
        public int footprintUpdateIntervalTicks = 2;
        public float walkCycleDistance = 2.6f;
        public float runCycleDistance = 3.2f;
        public float portraitScaleFactor = 0.34f;
        public bool useCoupledSagittalKinematics = false;

        /// <summary>
        /// Controls how strongly a single-part bone animation (&lt;actingSubtreeRootBoneId&gt;) drags along non-acting body parts [0..1].
        /// Negative (-1) means auto-derive inversely from pawn BodySize via ComputeSizeScaledBodyCoupling (larger giants -&gt; smaller coupled body motion).
        /// </summary>
        public float partAnimBodyCoupling = -1f;
        public float bodyCouplingRefBodySize = 2.0f;
        public float bodyCouplingSizeExponent = 0.85f;
        public float minAutoBodyCoupling = 0.08f;

        public static float ComputeSizeScaledBodyCoupling(
            float bodySize,
            float refBodySize = 2.0f,
            float sizeExponent = 0.85f,
            float minFactor = 0.08f)
        {
            float safeRef = Mathf.Max(0.1f, refBodySize);
            if (bodySize <= 0f)
            {
                return 1f;
            }
            float ratio = safeRef / Mathf.Max(safeRef, bodySize);
            return Mathf.Clamp(Mathf.Pow(ratio, Mathf.Max(0.05f, sizeExponent)), Mathf.Clamp01(minFactor), 1f);
        }

        public float ResolvePartAnimBodyCoupling(float pawnBodySize = -1f, float clipOverride = -1f)
        {
            if (clipOverride >= 0f)
            {
                return Mathf.Clamp01(clipOverride);
            }
            if (partAnimBodyCoupling >= 0f)
            {
                return Mathf.Clamp01(partAnimBodyCoupling);
            }
            if (pawnBodySize > 0f)
            {
                return ComputeSizeScaledBodyCoupling(pawnBodySize, bodyCouplingRefBodySize, bodyCouplingSizeExponent, minAutoBodyCoupling);
            }
            return 1f;
        }

        // Distant-enemy rush configuration
        public bool rushWhenEnemyFar = true;
        public float rushStartDistance = 12f;
        public float rushStopDistance = 7f;
        public float rushMoveSpeedMultiplier = 1.65f;

        // Collision-cell building / rock / terrain destruction & hull collision configuration
        public bool crushBuildingsAndTerrain = true;
        public bool canCrushNaturalRock = true;
        public bool canCrushConstructedBuildings = true;
        public bool canCrushDestructibleTerrain = true;
        public bool protectAlliedStructures = true;
        public int crushDamagePerStep = 450;
        public int crushIntervalTicks = 12;
        public bool enableCollisionPawnPushOut = true;
        public bool enablePawnOcclusionSilhouette = true;
        public bool enableGiantToGiantCollision = true;
        public bool enableDynamicMeleeContactLunge = true;
        public float maxDynamicMeleeContactLungeCells = 0.85f;

        // Combat reach, limb damage routing, armor/weakpoints, explosions & corpse footprint
        public float meleeTouchRadius = 0f;
        public bool useDirectionalMeleeTools = true;
        public bool enableLimbPartDamageRouting = true;
        public bool enableBoneArmorAndWeakpoints = true;
        public bool enableMultiCellExplosionRouting = true;
        public bool enableCorpseFootprint = true;

        // Configurable pain sensitivity, pain-shock structural gates, and core-only lethality
        public bool painImmune = false;
        public float painScaleFactor = 1.0f;
        public float painShockThresholdOverride = -1f;
        public int minSeveredBonesForPainShock = 1;
        public float unseveredPainShockMaxHealthPct = 0.25f;
        public bool onlyDieWhenCoreDestroyed = false;

        // Multi-cell melee cleave and knockback configuration
        public bool enableMeleeCleave = true;
        public float cleaveRadius = 2.6f;
        public float cleaveArcDeg = 110f;
        public float cleaveDamageFactor = 0.55f;
        public int cleaveMaxTargets = 4;
        public bool enableMeleeKnockback = true;
        public float meleeKnockbackDistance = 2.2f;

        // Skeletal VFX, footstep impact, and death collapse configuration
        public bool enableGiantVfx = true;
        public bool autoFootstepVfx = true;
        public GiantVfxPaletteTheme vfxPaletteTheme = GiantVfxPaletteTheme.Auto;
        public float footstepCameraShake = 0.04f;
        public bool spawnDeathCollapseVfx = true;

        public GiantAnimDef idleAnim;
        public GiantAnimDef walkAnim;
        public GiantAnimDef runAnim;
        public GiantAnimDef turnLeftAnim;
        public GiantAnimDef turnRightAnim;
        public float turnCycleAngleDeg = 42f;
        public float turnCorneringBlendMax = 0.42f;
        public List<GiantAnimDef> meleeAttackAnims = new List<GiantAnimDef>();
        public GiantAnimDef rangedAttackAnim;
        public List<GiantAnimDef> abilityAnims = new List<GiantAnimDef>();
        public string rangedWeaponBoneId;
        public List<GiantWeaponEmitterDef> weaponEmitters = new List<GiantWeaponEmitterDef>();
        public GiantAnimDef downedAnim;
        public GiantAnimDef deathAnim;
        public GiantFlightEntryDef flightEntry;
        public List<BezierChainDef> bezierChains = new List<BezierChainDef>();
        public List<SoftWhipDef> softWhips = new List<SoftWhipDef>();
        public List<GiantSkinnedMeshDef> skinnedMeshes = new List<GiantSkinnedMeshDef>();

        // Optional top-center expandable Boss Health Bar configuration
        public GiantHealthBarDef healthBarDef;
        [MustTranslate]
        public string healthBarTitleOverride;

        // Optional dedicated ground shadow texture & soft-penumbra projection configuration
        public string shadowTexPath;
        public Vector2 shadowDrawSize = Vector2.zero;
        public float shadowAlpha = 0.42f;
        public Vector2 shadowOffsetXZ = new Vector2(0.36f, -0.42f);
        public bool renderGroundShadowWhenGrounded = false;

        public List<GiantRollingTrackDef> rollingTracks = new List<GiantRollingTrackDef>();
        public SteeringConfigDef steering;
        public GiantHitReactionDef hitReaction;
        public VehicleLocomotionDef vehicleLocomotion;
        public GiantFlightDef flightLocomotion;
        public FlightVehicleLocomotionDef flightVehicleLocomotion;
        public GiantAquaticDef aquaticLocomotion;

        public VehicleLocomotionDef ActiveVehicleLocomotion =>
            vehicleLocomotion != null && vehicleLocomotion.IsActive
                ? vehicleLocomotion
                : (steering?.vehicleLocomotion != null && steering.vehicleLocomotion.IsActive
                    ? steering.vehicleLocomotion
                    : null);

        public GiantFlightDef ActiveFlightLocomotion =>
            flightLocomotion != null && flightLocomotion.IsActive
                ? flightLocomotion
                : (steering?.flightLocomotion != null && steering.flightLocomotion.IsActive
                    ? steering.flightLocomotion
                    : null);

        public FlightVehicleLocomotionDef ActiveFlightVehicleLocomotion =>
            flightVehicleLocomotion != null && flightVehicleLocomotion.IsActive
                ? flightVehicleLocomotion
                : (steering?.flightVehicleLocomotion != null && steering.flightVehicleLocomotion.IsActive
                    ? steering.flightVehicleLocomotion
                    : null);

        public GiantAquaticDef ActiveAquaticLocomotion =>
            aquaticLocomotion != null && aquaticLocomotion.IsActive
                ? aquaticLocomotion
                : (steering?.aquaticLocomotion != null && steering.aquaticLocomotion.IsActive
                    ? steering.aquaticLocomotion
                    : null);

        public bool IsVehicleRig => ActiveVehicleLocomotion != null || ActiveFlightVehicleLocomotion != null;

        /// <summary>
        /// Half-angle (deg) centered on 90 deg (East) and 270 deg (West) that activates lateral 3/4 view variants.
        /// 45 deg means [45, 135] -> East and [225, 315] -> West.
        /// </summary>
        public float lateralViewHalfAngleDeg = 45f;

        /// <summary>
        /// Angular hysteresis margin (deg) preventing sprite sector flickering when hovering near +/- 45 deg boundaries.
        /// </summary>
        public float lateralViewHysteresisDeg = 6f;

        [Unsaved]
        public bool hasAnyDirectionalVariants;

        [Unsaved]
        public int[] persistentAuraBoneIndices = Array.Empty<int>();

        [Unsaved]
        private readonly object topologySyncLock = new object();

        [Unsaved]
        private BoneNodeDef[] sortedNodes;

        [Unsaved]
        private Dictionary<string, int> nodeIndexById;

        [Unsaved]
        private Dictionary<string, GiantBoneCrewSeatDef> crewSeatById;

        [Unsaved]
        private GiantBoneCrewSeatDef[] resolvedCrewSeats = Array.Empty<GiantBoneCrewSeatDef>();

        [Unsaved]
        public float maxAuthoredLayerOffset = 4f;

        public IReadOnlyList<GiantBoneCrewSeatDef> CrewSeatMounts
        {
            get
            {
                EnsureTopologyBuilt();
                return resolvedCrewSeats;
            }
        }

        [Unsaved]
        private int[] lethalCoreNodeIndices = Array.Empty<int>();

        public int[] LethalCoreNodeIndices
        {
            get
            {
                EnsureTopologyBuilt();
                return lethalCoreNodeIndices;
            }
        }

        public bool TryGetCrewSeatMount(string seatId, out int boneIndex, out GiantBoneCrewSeatDef seatMount)
        {
            EnsureTopologyBuilt();
            Dictionary<string, GiantBoneCrewSeatDef> map = crewSeatById;
            if (!string.IsNullOrEmpty(seatId) && map != null && map.TryGetValue(seatId, out seatMount) && seatMount != null)
            {
                boneIndex = seatMount.resolvedBoneIndex;
                return (uint)boneIndex < (uint)SortedNodes.Length;
            }
            boneIndex = -1;
            seatMount = null;
            return false;
        }

        public GiantBoneViewSector EvaluateSector(
            float worldAngleDeg,
            GiantBoneViewSector currentSector = GiantBoneViewSector.Dorsal,
            bool applyHysteresis = false)
        {
            float half = Mathf.Clamp(lateralViewHalfAngleDeg, 5f, 80f);
            float hyst = applyHysteresis ? Mathf.Clamp(lateralViewHysteresisDeg, 0f, Mathf.Min(20f, half * 0.5f)) : 0f;
            float distEast = Mathf.Abs(Mathf.DeltaAngle(worldAngleDeg, 90f));
            float distWest = Mathf.Abs(Mathf.DeltaAngle(worldAngleDeg, 270f));

            switch (currentSector)
            {
                case GiantBoneViewSector.East:
                    if (distEast <= half + hyst)
                    {
                        return GiantBoneViewSector.East;
                    }
                    return distWest <= half - hyst ? GiantBoneViewSector.West : GiantBoneViewSector.Dorsal;

                case GiantBoneViewSector.West:
                    if (distWest <= half + hyst)
                    {
                        return GiantBoneViewSector.West;
                    }
                    return distEast <= half - hyst ? GiantBoneViewSector.East : GiantBoneViewSector.Dorsal;

                default:
                    if (distEast <= half - hyst)
                    {
                        return GiantBoneViewSector.East;
                    }
                    if (distWest <= half - hyst)
                    {
                        return GiantBoneViewSector.West;
                    }
                    return GiantBoneViewSector.Dorsal;
            }
        }

        public BoneNodeDef[] SortedNodes
        {
            get
            {
                BoneNodeDef[] snap = sortedNodes;
                if (snap == null)
                {
                    BuildTopology();
                    snap = sortedNodes;
                }
                return snap;
            }
        }

        public bool TryGetNodeIndex(string nodeId, out int index)
        {
            Dictionary<string, int> map = nodeIndexById;
            if (map == null)
            {
                BuildTopology();
                map = nodeIndexById;
            }
            if (!string.IsNullOrEmpty(nodeId) && map != null && map.TryGetValue(nodeId, out index))
            {
                return true;
            }
            index = -1;
            return false;
        }

        public bool IsBoneInSubtree(int boneIndex, int subtreeRootIndex)
        {
            BoneNodeDef[] ordered = SortedNodes;
            if ((uint)boneIndex >= (uint)ordered.Length || (uint)subtreeRootIndex >= (uint)ordered.Length)
            {
                return false;
            }
            int cur = boneIndex;
            while (cur >= 0 && cur < ordered.Length)
            {
                if (cur == subtreeRootIndex)
                {
                    return true;
                }
                cur = ordered[cur].parentRuntimeIndex;
            }
            return false;
        }

        public bool IsBoneInTurretSubtree(int runtimeBoneIndex, out float maxTurretAimDeg)
        {
            maxTurretAimDeg = 0f;
            EnsureTopologyBuilt();
            BoneNodeDef[] ordered = sortedNodes;
            if (ordered == null || (uint)runtimeBoneIndex >= (uint)ordered.Length)
            {
                return false;
            }

            int cur = runtimeBoneIndex;
            int guard = 0;
            while (cur >= 0 && cur < ordered.Length && guard++ < ordered.Length)
            {
                if (ordered[cur].isTurret)
                {
                    maxTurretAimDeg = Mathf.Clamp(ordered[cur].maxTurretAimAngleDeg, 0f, 180f);
                    return true;
                }
                cur = ordered[cur].parentRuntimeIndex;
            }
            return false;
        }

        public bool TryResolveTurretRecoilForFiringBone(
            int firingBoneIndex,
            out GiantTurretRecoilDef recoilDef,
            out int targetRecoilBoneIndex)
        {
            recoilDef = null;
            targetRecoilBoneIndex = -1;
            EnsureTopologyBuilt();
            BoneNodeDef[] ordered = sortedNodes;
            if (ordered == null || (uint)firingBoneIndex >= (uint)ordered.Length || firingBoneIndex <= 0)
            {
                return false;
            }
            if (!IsBoneInTurretSubtree(firingBoneIndex, out _))
            {
                return false;
            }

            int cur = firingBoneIndex;
            int guard = 0;
            while (cur > 0 && cur < ordered.Length && guard++ < ordered.Length)
            {
                if (!IsBoneInTurretSubtree(cur, out _))
                {
                    break;
                }
                GiantTurretRecoilDef cand = ordered[cur].turretRecoil;
                if (cand != null)
                {
                    int rIdx = cand.resolvedRecoilBoneIndex >= 0 ? cand.resolvedRecoilBoneIndex : cur;
                    if ((uint)rIdx < (uint)ordered.Length && rIdx > 0)
                    {
                        recoilDef = cand;
                        targetRecoilBoneIndex = rIdx;
                        return true;
                    }
                }
                cur = ordered[cur].parentRuntimeIndex;
            }
            return false;
        }

        public bool HasAnyIntactTurretBone(Func<string, bool> isBoneIntact, out float maxTurretArcDeg)
        {
            maxTurretArcDeg = 0f;
            EnsureTopologyBuilt();
            BoneNodeDef[] ordered = sortedNodes;
            if (ordered == null)
            {
                return false;
            }

            bool found = false;
            for (int i = 0; i < ordered.Length; i++)
            {
                BoneNodeDef n = ordered[i];
                if (n == null || !n.isTurret)
                {
                    continue;
                }
                if (isBoneIntact != null && !isBoneIntact(n.nodeId))
                {
                    continue;
                }
                found = true;
                float arc = Mathf.Clamp(n.maxTurretAimAngleDeg, 0f, 180f);
                if (arc > maxTurretArcDeg)
                {
                    maxTurretArcDeg = arc;
                }
            }
            return found;
        }

        public void EnsureTopologyBuilt()
        {
            if (sortedNodes == null || nodeIndexById == null)
            {
                BuildTopology();
            }
        }

        public override void ResolveReferences()
        {
            base.ResolveReferences();
            BuildTopology();
        }

        public void BuildTopology()
        {
            lock (topologySyncLock)
            {
                var localIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
                if (nodes == null || nodes.Count == 0)
                {
                    nodeIndexById = localIndexById;
                    sortedNodes = Array.Empty<BoneNodeDef>();
                    crewSeatById = new Dictionary<string, GiantBoneCrewSeatDef>(StringComparer.OrdinalIgnoreCase);
                    resolvedCrewSeats = Array.Empty<GiantBoneCrewSeatDef>();
                    maxAuthoredLayerOffset = 4f;
                    return;
                }

                var rawById = new Dictionary<string, BoneNodeDef>(StringComparer.Ordinal);
                for (int i = 0; i < nodes.Count; i++)
                {
                    BoneNodeDef n = nodes[i];
                    if (n != null && !string.IsNullOrEmpty(n.nodeId))
                    {
                        rawById[n.nodeId] = n;
                    }
                }

                var ordered = new List<BoneNodeDef>(nodes.Count);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var visiting = new HashSet<string>(StringComparer.Ordinal);

                void Visit(BoneNodeDef node)
                {
                    if (node == null || string.IsNullOrEmpty(node.nodeId) || visited.Contains(node.nodeId))
                    {
                        return;
                    }
                    if (!visiting.Add(node.nodeId))
                    {
                        Log.Error($"[GiantCreatureFramework] Cycle detected in GiantRigDef '{defName}' at bone '{node.nodeId}'.");
                        return;
                    }
                    if (!string.IsNullOrEmpty(node.parentId) && rawById.TryGetValue(node.parentId, out BoneNodeDef parentNode))
                    {
                        Visit(parentNode);
                    }
                    visiting.Remove(node.nodeId);
                    visited.Add(node.nodeId);
                    ordered.Add(node);
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    Visit(nodes[i]);
                }

                BoneNodeDef[] localSorted = ordered.ToArray();
                for (int i = 0; i < localSorted.Length; i++)
                {
                    localSorted[i].runtimeIndex = i;
                    localSorted[i].primarySagittalChildIndex = -1;
                    localSorted[i].firstArticulatedChildIndex = -1;
                    localSorted[i].isRenderedBySkinnedMesh = false;
                    localSorted[i].skinnedMeshSurfaceLayerOffset = float.NegativeInfinity;
                    localIndexById[localSorted[i].nodeId] = i;
                }

                bool localHasVariants = false;
                for (int i = 0; i < localSorted.Length; i++)
                {
                    BoneNodeDef node = localSorted[i];
                    int parentRes = node.textureResolution;
                    if (!string.IsNullOrEmpty(node.parentId) && localIndexById.TryGetValue(node.parentId, out int pIdx))
                    {
                        if (pIdx < i)
                        {
                            node.parentRuntimeIndex = pIdx;
                            parentRes = localSorted[pIdx].textureResolution;
                            if (localSorted[pIdx].primarySagittalChildIndex < 0 && node.contributesToCollision && !node.isRigid)
                            {
                                localSorted[pIdx].primarySagittalChildIndex = i;
                            }
                            if (localSorted[pIdx].firstArticulatedChildIndex < 0 && !node.isRigid)
                            {
                                localSorted[pIdx].firstArticulatedChildIndex = i;
                            }
                        }
                        else
                        {
                            Log.Error($"[GiantCreatureFramework] Breaking cyclic or non-topological parent link in GiantRigDef '{defName}': bone '{node.nodeId}' (index {i}) cannot reference parent '{node.parentId}' (index {pIdx}). Detaching to root.");
                            node.parentRuntimeIndex = -1;
                        }
                    }
                    else
                    {
                        node.parentRuntimeIndex = -1;
                    }
                    node.sectorSourceRuntimeIndex = !string.IsNullOrEmpty(node.sectorSourceBoneId)
                        && localIndexById.TryGetValue(node.sectorSourceBoneId, out int sIdx)
                        ? sIdx
                        : -1;
                    if (node.HasDirectionalVariants)
                    {
                        localHasVariants = true;
                    }
                    if (node.turretRecoil != null)
                    {
                        node.turretRecoil.resolvedRecoilBoneIndex = !string.IsNullOrEmpty(node.turretRecoil.recoilBoneId)
                            && localIndexById.TryGetValue(node.turretRecoil.recoilBoneId, out int recIdx)
                            ? recIdx
                            : i;
                    }
                    node.ResolveCoordinates(parentRes);
                    node.resolvedCollisionRole = node.ResolveEffectiveCollisionRole();
                }

                if (bezierChains != null)
                {
                    for (int c = 0; c < bezierChains.Count; c++)
                    {
                        BezierChainDef chain = bezierChains[c];
                        if (chain?.boneIds == null || chain.boneIds.Count == 0)
                        {
                            if (chain != null)
                            {
                                chain.resolvedBoneIndices = Array.Empty<int>();
                            }
                            continue;
                        }
                        int[] resolved = new int[chain.boneIds.Count];
                        for (int k = 0; k < chain.boneIds.Count; k++)
                        {
                            resolved[k] = !string.IsNullOrEmpty(chain.boneIds[k]) && localIndexById.TryGetValue(chain.boneIds[k], out int bIdx)
                                ? bIdx
                                : -1;
                        }
                        chain.resolvedBoneIndices = resolved;
                    }
                }

                if (softWhips != null)
                {
                    for (int s = 0; s < softWhips.Count; s++)
                    {
                        SoftWhipDef whip = softWhips[s];
                        if (whip == null)
                        {
                            continue;
                        }
                        whip.runtimeIndex = s;
                        whip.resolvedRootBoneIndex = !string.IsNullOrEmpty(whip.rootBoneId) && localIndexById.TryGetValue(whip.rootBoneId, out int rIdx)
                            ? rIdx
                            : -1;
                        whip.resolvedTorsoBoneIndex = !string.IsNullOrEmpty(whip.torsoBoneId) && localIndexById.TryGetValue(whip.torsoBoneId, out int tIdx)
                            ? tIdx
                            : (whip.resolvedRootBoneIndex >= 0 ? localSorted[whip.resolvedRootBoneIndex].parentRuntimeIndex : -1);
                        int rootRes = whip.resolvedRootBoneIndex >= 0 ? localSorted[whip.resolvedRootBoneIndex].textureResolution : 512;
                        whip.ResolveCoordinates(rootRes);
                    }
                }

                if (rollingTracks != null)
                {
                    for (int t = 0; t < rollingTracks.Count; t++)
                    {
                        GiantRollingTrackDef track = rollingTracks[t];
                        if (track == null)
                        {
                            continue;
                        }
                        track.runtimeIndex = t;
                        track.resolvedParentBoneIndex = !string.IsNullOrEmpty(track.parentBoneId) && localIndexById.TryGetValue(track.parentBoneId, out int bIdx)
                            ? bIdx
                            : -1;
                    }
                }

                if (skinnedMeshes != null)
                {
                    var meshIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int m = 0; m < skinnedMeshes.Count; m++)
                    {
                        GiantSkinnedMeshDef smPre = skinnedMeshes[m];
                        if (smPre != null && !string.IsNullOrEmpty(smPre.meshId))
                        {
                            meshIndexById[smPre.meshId] = m;
                        }
                    }

                    for (int m = 0; m < skinnedMeshes.Count; m++)
                    {
                        GiantSkinnedMeshDef sm = skinnedMeshes[m];
                        if (sm == null)
                        {
                            continue;
                        }
                        sm.runtimeIndex = m;
                        if (sm.HasWorldBoundsXZ)
                        {
                            sm.drawSize = new Vector2(sm.worldMaxXZ.x - sm.worldMinXZ.x, sm.worldMaxXZ.y - sm.worldMinXZ.y);
                        }

                        sm.resolvedRigidPinBoneIndex = !string.IsNullOrEmpty(sm.rigidPinBoneId) && localIndexById.TryGetValue(sm.rigidPinBoneId, out int rpIdx)
                            ? rpIdx
                            : -1;
                        sm.resolvedPinTargetMeshIndex = !string.IsNullOrEmpty(sm.pinToSkinnedMeshId) && meshIndexById.TryGetValue(sm.pinToSkinnedMeshId, out int pmIdx)
                            ? pmIdx
                            : -1;

                        if (sm.memberBoneIds != null && sm.memberBoneIds.Count > 0)
                        {
                            var memList = new List<int>(sm.memberBoneIds.Count);
                            for (int mb = 0; mb < sm.memberBoneIds.Count; mb++)
                            {
                                string mbId = sm.memberBoneIds[mb];
                                if (!string.IsNullOrEmpty(mbId) && localIndexById.TryGetValue(mbId, out int mbIdx))
                                {
                                    memList.Add(mbIdx);
                                    if (sm.suppressMemberBoneSprites)
                                    {
                                        localSorted[mbIdx].isRenderedBySkinnedMesh = true;
                                        if (sm.layerOffset > localSorted[mbIdx].skinnedMeshSurfaceLayerOffset)
                                        {
                                            localSorted[mbIdx].skinnedMeshSurfaceLayerOffset = sm.layerOffset;
                                        }
                                    }
                                }
                            }
                            sm.resolvedMemberBoneIndices = memList.ToArray();
                        }
                        else
                        {
                            sm.resolvedMemberBoneIndices = Array.Empty<int>();
                        }

                        if (sm.capsules != null)
                        {
                            for (int c = 0; c < sm.capsules.Count; c++)
                            {
                                GiantSkinCapsuleDef cap = sm.capsules[c];
                                if (cap != null)
                                {
                                    cap.resolvedBoneIndex = !string.IsNullOrEmpty(cap.boneId) && localIndexById.TryGetValue(cap.boneId, out int cbIdx)
                                        ? cbIdx
                                        : -1;
                                }
                            }
                        }

                        var validIndices = new List<int>(16);
                        var seenBoneIdx = new HashSet<int>();
                        if (sm.boneIds != null)
                        {
                            for (int b = 0; b < sm.boneIds.Count; b++)
                            {
                                string bid = sm.boneIds[b];
                                if (!string.IsNullOrEmpty(bid) && localIndexById.TryGetValue(bid, out int bIdx) && seenBoneIdx.Add(bIdx))
                                {
                                    validIndices.Add(bIdx);
                                }
                            }
                        }
                        if (sm.capsules != null)
                        {
                            for (int c = 0; c < sm.capsules.Count; c++)
                            {
                                int cbIdx = sm.capsules[c]?.resolvedBoneIndex ?? -1;
                                if (cbIdx >= 0 && seenBoneIdx.Add(cbIdx))
                                {
                                    validIndices.Add(cbIdx);
                                }
                            }
                        }
                        for (int mb = 0; mb < sm.resolvedMemberBoneIndices.Length; mb++)
                        {
                            int mbIdx = sm.resolvedMemberBoneIndices[mb];
                            if (mbIdx >= 0 && seenBoneIdx.Add(mbIdx))
                            {
                                validIndices.Add(mbIdx);
                            }
                        }
                        if (sm.resolvedRigidPinBoneIndex >= 0 && seenBoneIdx.Add(sm.resolvedRigidPinBoneIndex))
                        {
                            validIndices.Add(sm.resolvedRigidPinBoneIndex);
                        }
                        sm.resolvedBoneIndices = validIndices.Count > 0 ? validIndices.ToArray() : Array.Empty<int>();

                        string anchorId = !string.IsNullOrEmpty(sm.anchorBoneId)
                            ? sm.anchorBoneId
                            : (sm.boneIds != null && sm.boneIds.Count > 0 ? sm.boneIds[0] : null);
                        sm.resolvedAnchorBoneIndex = !string.IsNullOrEmpty(anchorId) && localIndexById.TryGetValue(anchorId, out int aIdx)
                            ? aIdx
                            : (sm.resolvedBoneIndices.Length > 0 ? sm.resolvedBoneIndices[0] : -1);

                        float res = Mathf.Max(1, sm.textureResolution);
                        sm.resolvedPivotUV = (sm.pivotPixel.x >= 0f && sm.pivotPixel.y >= 0f)
                            ? new Vector2(sm.pivotPixel.x / res, 1f - (sm.pivotPixel.y / res))
                            : sm.pivotUV;
                        float anchorRes = sm.resolvedAnchorBoneIndex >= 0
                            ? Mathf.Max(1, localSorted[sm.resolvedAnchorBoneIndex].textureResolution)
                            : res;
                        sm.resolvedParentSocketUV = (sm.parentSocketPixel.x >= 0f && sm.parentSocketPixel.y >= 0f)
                            ? new Vector2(sm.parentSocketPixel.x / anchorRes, 1f - (sm.parentSocketPixel.y / anchorRes))
                            : sm.parentSocketUV;
                    }
                }

                ActiveVehicleLocomotion?.ResolveTopology(localSorted, rollingTracks);

                if (steering != null)
                {
                    if (steering.lookChains != null)
                    {
                        for (int c = 0; c < steering.lookChains.Count; c++)
                        {
                            SteeringLookChainDef chain = steering.lookChains[c];
                            if (chain == null)
                            {
                                continue;
                            }
                            if (chain.boneIds == null || chain.boneIds.Count == 0)
                            {
                                chain.resolvedBoneIndices = Array.Empty<int>();
                                chain.normalizedWeights = Array.Empty<float>();
                                continue;
                            }
                            int count = chain.boneIds.Count;
                            int[] resolved = new int[count];
                            float[] weights = new float[count];
                            float wSum = 0f;
                            bool hasCustomWeights = chain.weights != null && chain.weights.Count == count;
                            for (int k = 0; k < count; k++)
                            {
                                resolved[k] = !string.IsNullOrEmpty(chain.boneIds[k]) && localIndexById.TryGetValue(chain.boneIds[k], out int bIdx)
                                    ? bIdx
                                    : -1;
                                float w = hasCustomWeights ? Mathf.Max(0f, chain.weights[k]) : (k + 1.4f);
                                weights[k] = w;
                                wSum += w;
                            }
                            if (wSum <= 1e-5f)
                            {
                                wSum = 0f;
                                for (int k = 0; k < count; k++)
                                {
                                    weights[k] = k + 1.4f;
                                    wSum += weights[k];
                                }
                            }
                            for (int k = 0; k < count; k++)
                            {
                                weights[k] /= wSum;
                            }
                            chain.resolvedBoneIndices = resolved;
                            chain.normalizedWeights = weights;
                        }
                    }

                    if (steering.lagBones != null)
                    {
                        for (int i = 0; i < steering.lagBones.Count; i++)
                        {
                            SteeringLagBoneDef lag = steering.lagBones[i];
                            if (lag == null)
                            {
                                continue;
                            }
                            lag.resolvedBoneIndex = !string.IsNullOrEmpty(lag.boneId) && localIndexById.TryGetValue(lag.boneId, out int bIdx)
                                ? bIdx
                                : -1;
                        }
                    }

                    if (steering.yawBankBones != null)
                    {
                        for (int i = 0; i < steering.yawBankBones.Count; i++)
                        {
                            SteeringYawBankDef bank = steering.yawBankBones[i];
                            if (bank == null)
                            {
                                continue;
                            }
                            bank.resolvedBoneIndex = !string.IsNullOrEmpty(bank.boneId) && localIndexById.TryGetValue(bank.boneId, out int bIdx)
                                ? bIdx
                                : -1;
                        }
                    }
                }

                var auraIdxList = new List<int>();
                var localCrewById = new Dictionary<string, GiantBoneCrewSeatDef>(StringComparer.OrdinalIgnoreCase);
                var localCrewList = new List<GiantBoneCrewSeatDef>();
                float localMaxLayer = 0f;
                for (int i = 0; i < localSorted.Length; i++)
                {
                    BoneNodeDef curNode = localSorted[i];
                    if (curNode == null)
                    {
                        continue;
                    }
                    if (curNode.persistentVfxPreset != GiantBoneAuraVfxKind.None)
                    {
                        auraIdxList.Add(i);
                    }

                    float dLayer = curNode.GetLayerOffset(GiantBoneViewSector.Dorsal);
                    if (dLayer > localMaxLayer) localMaxLayer = dLayer;
                    float eLayer = curNode.GetLayerOffset(GiantBoneViewSector.East);
                    if (eLayer > localMaxLayer) localMaxLayer = eLayer;
                    float wLayer = curNode.GetLayerOffset(GiantBoneViewSector.West);
                    if (wLayer > localMaxLayer) localMaxLayer = wLayer;

                    if (curNode.crewSeats != null && curNode.crewSeats.Count > 0)
                    {
                        for (int s = 0; s < curNode.crewSeats.Count; s++)
                        {
                            GiantBoneCrewSeatDef cs = curNode.crewSeats[s];
                            if (cs == null)
                            {
                                continue;
                            }
                            cs.ResolveCoordinates(curNode);
                            cs.resolvedBoneIndex = i;
                            cs.resolvedBoneId = curNode.nodeId;
                            localCrewList.Add(cs);
                            if (!string.IsNullOrWhiteSpace(cs.seatId) && !localCrewById.ContainsKey(cs.seatId))
                            {
                                localCrewById[cs.seatId] = cs;
                            }
                        }
                    }
                }

                if (skinnedMeshes != null)
                {
                    for (int m = 0; m < skinnedMeshes.Count; m++)
                    {
                        GiantSkinnedMeshDef sm = skinnedMeshes[m];
                        if (sm != null)
                        {
                            float smTop = sm.layerOffset + (!string.IsNullOrEmpty(sm.glowTexPath) ? Mathf.Max(0f, sm.glowLayerOffset) : 0f);
                            if (smTop > localMaxLayer) localMaxLayer = smTop;
                        }
                    }
                }
                if (softWhips != null)
                {
                    for (int s = 0; s < softWhips.Count; s++)
                    {
                        if (softWhips[s] != null && softWhips[s].layerOffset > localMaxLayer)
                        {
                            localMaxLayer = softWhips[s].layerOffset;
                        }
                    }
                }

                var lethalIdxList = new List<int>();
                for (int i = 0; i < localSorted.Length; i++)
                {
                    if (localSorted[i].lethalWhenDestroyed)
                    {
                        lethalIdxList.Add(i);
                    }
                }

                hasAnyDirectionalVariants = localHasVariants;
                persistentAuraBoneIndices = auraIdxList.Count > 0 ? auraIdxList.ToArray() : Array.Empty<int>();
                lethalCoreNodeIndices = lethalIdxList.Count > 0 ? lethalIdxList.ToArray() : Array.Empty<int>();
                crewSeatById = localCrewById;
                resolvedCrewSeats = localCrewList.Count > 0 ? localCrewList.ToArray() : Array.Empty<GiantBoneCrewSeatDef>();
                maxAuthoredLayerOffset = localMaxLayer;
                nodeIndexById = localIndexById;
                sortedNodes = localSorted;
                ActiveVehicleLocomotion?.ResolveTopology(localSorted, rollingTracks);
                ActiveFlightLocomotion?.ResolveTopology(localSorted);
                ActiveFlightVehicleLocomotion?.ResolveTopology(localSorted);
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string err in base.ConfigErrors())
            {
                yield return err;
            }

            if (nodes == null || nodes.Count == 0)
            {
                yield return $"GiantRigDef '{defName}' has no BoneNodeDefs.";
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int rootCount = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                BoneNodeDef n = nodes[i];
                if (n == null || string.IsNullOrEmpty(n.nodeId))
                {
                    yield return $"GiantRigDef '{defName}' has a null or empty nodeId at index {i}.";
                    continue;
                }
                if (!seen.Add(n.nodeId))
                {
                    yield return $"GiantRigDef '{defName}' has duplicate nodeId '{n.nodeId}'.";
                }
                if (string.IsNullOrEmpty(n.parentId))
                {
                    rootCount++;
                }
            }

            if (rootCount == 0)
            {
                yield return $"GiantRigDef '{defName}' must have at least one root bone (empty parentId).";
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                BoneNodeDef n = nodes[i];
                if (n != null && !string.IsNullOrEmpty(n.parentId) && !seen.Contains(n.parentId))
                {
                    yield return $"Bone '{n.nodeId}' in GiantRigDef '{defName}' references unknown parentId '{n.parentId}'.";
                }
                if (n != null && !string.IsNullOrEmpty(n.sectorSourceBoneId) && !seen.Contains(n.sectorSourceBoneId))
                {
                    yield return $"Bone '{n.nodeId}' in GiantRigDef '{defName}' references unknown sectorSourceBoneId '{n.sectorSourceBoneId}'.";
                }
            }

            EnsureTopologyBuilt();

            if (painScaleFactor < 0f)
            {
                yield return $"GiantRigDef '{defName}' has negative painScaleFactor ({painScaleFactor:F2}).";
            }
            if (painShockThresholdOverride > 0f && (painShockThresholdOverride < 0.05f || painShockThresholdOverride > 1.0f))
            {
                yield return $"GiantRigDef '{defName}' painShockThresholdOverride ({painShockThresholdOverride:F2}) must be in [0.05, 1.0] when enabled.";
            }
            if (unseveredPainShockMaxHealthPct < 0f || unseveredPainShockMaxHealthPct > 1.0f)
            {
                yield return $"GiantRigDef '{defName}' unseveredPainShockMaxHealthPct ({unseveredPainShockMaxHealthPct:F2}) must be in [0, 1].";
            }
            if (pawnExclusionCoverageThreshold < 0.02f || pawnExclusionCoverageThreshold > 1.0f)
            {
                yield return $"GiantRigDef '{defName}' pawnExclusionCoverageThreshold ({pawnExclusionCoverageThreshold:F2}) must be in [0.02, 1.0].";
            }
            if (maxDynamicMeleeContactLungeCells < 0f || maxDynamicMeleeContactLungeCells > 2.5f)
            {
                yield return $"GiantRigDef '{defName}' maxDynamicMeleeContactLungeCells ({maxDynamicMeleeContactLungeCells:F2}) must be in [0, 2.5].";
            }

            for (int i = 0; i < SortedNodes.Length; i++)
            {
                BoneNodeDef lNode = SortedNodes[i];
                if (lNode.lethalWhenDestroyed && lNode.linkedBodyPart == null)
                {
                    yield return $"Bone '{lNode.nodeId}' in GiantRigDef '{defName}' sets <lethalWhenDestroyed>true</lethalWhenDestroyed> without a <linkedBodyPart>.";
                }
            }

            for (int i = 0; i < SortedNodes.Length; i++)
            {
                BoneNodeDef rNode = SortedNodes[i];
                if (!rNode.isRigid)
                {
                    continue;
                }
                if (rNode.parentRuntimeIndex < 0)
                {
                    yield return $"Bone '{rNode.nodeId}' in GiantRigDef '{defName}' sets <isRigid>true</isRigid> on a root bone. Only child sub-parts welded/bolted to a parent hull or turret should set <isRigid>true</isRigid>.";
                }
                if (rNode.aimAtTargetWeight > 1e-4f)
                {
                    yield return $"Rigid bone '{rNode.nodeId}' in GiantRigDef '{defName}' (<isRigid>true</isRigid>) cannot set aimAtTargetWeight ({rNode.aimAtTargetWeight:F2}).";
                }
                if (Mathf.Abs(rNode.continuousSpinDegPerTick) > 1e-4f)
                {
                    yield return $"Rigid bone '{rNode.nodeId}' in GiantRigDef '{defName}' (<isRigid>true</isRigid>) cannot set continuousSpinDegPerTick ({rNode.continuousSpinDegPerTick:F2}).";
                }
            }

            for (int i = 0; i < SortedNodes.Length; i++)
            {
                BoneNodeDef tNode = SortedNodes[i];
                GiantTurretRecoilDef tr = tNode.turretRecoil;
                if (tr == null)
                {
                    continue;
                }
                if (i == 0 || !IsBoneInTurretSubtree(i, out _))
                {
                    yield return $"Bone '{tNode.nodeId}' in GiantRigDef '{defName}' defines <turretRecoil>, which is only valid on turret parts (<isTurret>true</isTurret> or turret barrel sub-parts).";
                }
                if (tr.distanceCells <= 0f || tr.distanceCells > 2.5f)
                {
                    yield return $"Bone '{tNode.nodeId}' in GiantRigDef '{defName}' <turretRecoil> distanceCells ({tr.distanceCells:F3}) must lie in (0, 2.5].";
                }
                if (tr.kickTicks < 0 || tr.kickTicks > 30)
                {
                    yield return $"Bone '{tNode.nodeId}' in GiantRigDef '{defName}' <turretRecoil> kickTicks ({tr.kickTicks}) must lie in [0, 30].";
                }
                if (tr.recoveryTicks < 1 || tr.recoveryTicks > 180)
                {
                    yield return $"Bone '{tNode.nodeId}' in GiantRigDef '{defName}' <turretRecoil> recoveryTicks ({tr.recoveryTicks}) must lie in [1, 180].";
                }
                if (!string.IsNullOrEmpty(tr.recoilBoneId))
                {
                    if (!TryGetNodeIndex(tr.recoilBoneId, out int rbIdx) || rbIdx <= 0 || !IsBoneInSubtree(rbIdx, i))
                    {
                        yield return $"Bone '{tNode.nodeId}' in GiantRigDef '{defName}' <turretRecoil> references invalid or non-descendant recoilBoneId '{tr.recoilBoneId}'.";
                    }
                }
            }

            foreach (string layerErr in ValidatePhysicalLayerOrdering(0.5f))
            {
                yield return layerErr;
            }

            if (!string.IsNullOrEmpty(rangedWeaponBoneId) && !TryGetNodeIndex(rangedWeaponBoneId, out _))
            {
                yield return $"GiantRigDef '{defName}' specifies unknown rangedWeaponBoneId '{rangedWeaponBoneId}'.";
            }

            if (!string.IsNullOrEmpty(shadowTexPath))
            {
                if (shadowDrawSize.x <= 0.1f || shadowDrawSize.y <= 0.1f)
                {
                    yield return $"GiantRigDef '{defName}' defines shadowTexPath '{shadowTexPath}' with invalid shadowDrawSize ({shadowDrawSize.x:F2}, {shadowDrawSize.y:F2}); both dimensions must exceed 0.1.";
                }
                if (shadowAlpha <= 0f || shadowAlpha > 1f)
                {
                    yield return $"GiantRigDef '{defName}' defines shadowTexPath '{shadowTexPath}' with shadowAlpha ({shadowAlpha:F2}) outside (0, 1].";
                }
            }

            if (ActiveVehicleLocomotion != null)
            {
                foreach (string vehErr in ActiveVehicleLocomotion.ValidateConfig(defName))
                {
                    yield return vehErr;
                }
            }

            if (ActiveFlightLocomotion != null)
            {
                foreach (string fltErr in ActiveFlightLocomotion.ValidateConfig(defName))
                {
                    yield return fltErr;
                }
            }

            if (ActiveFlightVehicleLocomotion != null)
            {
                if (ActiveVehicleLocomotion != null)
                {
                    yield return $"GiantRigDef '{defName}' cannot activate both ground <vehicleLocomotion> and aerial <flightVehicleLocomotion> simultaneously.";
                }
                foreach (string fvehErr in ActiveFlightVehicleLocomotion.ValidateConfig(defName))
                {
                    yield return fvehErr;
                }
            }

            if (ActiveAquaticLocomotion != null)
            {
                foreach (string aqErr in ActiveAquaticLocomotion.ConfigErrors(defName, this))
                {
                    yield return aqErr;
                }
            }

            if (skinnedMeshes != null && skinnedMeshes.Count > 0)
            {
                var seenMeshIds = new HashSet<string>(StringComparer.Ordinal);
                for (int m = 0; m < skinnedMeshes.Count; m++)
                {
                    GiantSkinnedMeshDef sm = skinnedMeshes[m];
                    if (sm == null)
                    {
                        yield return $"GiantRigDef '{defName}' has a null entry in skinnedMeshes[{m}].";
                        continue;
                    }
                    if (!string.IsNullOrEmpty(sm.meshId) && !seenMeshIds.Add(sm.meshId))
                    {
                        yield return $"GiantRigDef '{defName}' has duplicate skinnedMesh meshId '{sm.meshId}'.";
                    }
                    foreach (string smErr in sm.ConfigErrors(defName, nodeIndexById))
                    {
                        yield return smErr;
                    }
                }
            }

            var seenCrewSeatIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < SortedNodes.Length; i++)
            {
                BoneNodeDef bNode = SortedNodes[i];
                if (bNode.crewSeats != null && bNode.crewSeats.Count > 0)
                {
                    for (int s = 0; s < bNode.crewSeats.Count; s++)
                    {
                        GiantBoneCrewSeatDef cs = bNode.crewSeats[s];
                        if (cs == null || string.IsNullOrWhiteSpace(cs.seatId))
                        {
                            yield return $"Bone '{bNode.nodeId}' in GiantRigDef '{defName}' has a null or empty seatId in crewSeats[{s}].";
                            continue;
                        }
                        if (!seenCrewSeatIds.Add(cs.seatId))
                        {
                            yield return $"Bone '{bNode.nodeId}' in GiantRigDef '{defName}' has duplicate crew seatId '{cs.seatId}'.";
                        }
                        if (cs.drawScale <= 0.1f || cs.drawScale > 4.0f)
                        {
                            yield return $"Bone '{bNode.nodeId}' crewSeat '{cs.seatId}' in GiantRigDef '{defName}' has drawScale ({cs.drawScale:F2}) outside (0.1, 4.0].";
                        }
                    }
                }

                foreach ((string variantName, List<GiantBoneEmitterSocketDef> sockList) in new[]
                {
                    ("dorsal", bNode.emitterSockets),
                    ("eastVariant", bNode.eastVariant?.emitterSockets),
                    ("westVariant", bNode.westVariant?.emitterSockets)
                })
                {
                    if (sockList == null || sockList.Count == 0)
                    {
                        continue;
                    }
                    var seenSockets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int s = 0; s < sockList.Count; s++)
                    {
                        GiantBoneEmitterSocketDef sock = sockList[s];
                        if (sock == null || string.IsNullOrWhiteSpace(sock.socketId))
                        {
                            yield return $"Bone '{bNode.nodeId}' ({variantName}) in GiantRigDef '{defName}' has a null or empty socketId in emitterSockets[{s}].";
                            continue;
                        }
                        if (!seenSockets.Add(sock.socketId))
                        {
                            yield return $"Bone '{bNode.nodeId}' ({variantName}) in GiantRigDef '{defName}' has duplicate emitter socketId '{sock.socketId}'.";
                        }
                    }
                }
            }

            if (weaponEmitters != null)
            {
                var seenEmitters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int e = 0; e < weaponEmitters.Count; e++)
                {
                    GiantWeaponEmitterDef em = weaponEmitters[e];
                    if (em == null)
                    {
                        yield return $"GiantRigDef '{defName}' has a null entry in weaponEmitters[{e}].";
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(em.emitterId) && !seenEmitters.Add(em.emitterId))
                    {
                        yield return $"GiantRigDef '{defName}' has duplicate weaponEmitter emitterId '{em.emitterId}'.";
                    }
                    if (em.barrels == null || em.barrels.Count == 0)
                    {
                        yield return $"GiantWeaponEmitterDef '{em.emitterId ?? e.ToString()}' in GiantRigDef '{defName}' must define at least one barrel.";
                        continue;
                    }
                    for (int b = 0; b < em.barrels.Count; b++)
                    {
                        GiantEmitterBarrelDef barrel = em.barrels[b];
                        if (barrel == null || string.IsNullOrEmpty(barrel.boneId) || !TryGetNodeIndex(barrel.boneId, out int bIdx))
                        {
                            yield return $"GiantWeaponEmitterDef '{em.emitterId ?? e.ToString()}' in GiantRigDef '{defName}' references unknown barrel boneId '{barrel?.boneId}'.";
                            continue;
                        }
                        if (!string.IsNullOrEmpty(barrel.socketId))
                        {
                            BoneNodeDef bDef = SortedNodes[bIdx];
                            bool foundInAnySector = bDef.TryGetEmitterSocket(barrel.socketId, GiantBoneViewSector.Dorsal, out _)
                                || bDef.TryGetEmitterSocket(barrel.socketId, GiantBoneViewSector.East, out _)
                                || bDef.TryGetEmitterSocket(barrel.socketId, GiantBoneViewSector.West, out _);
                            if (!foundInAnySector)
                            {
                                yield return $"GiantWeaponEmitterDef '{em.emitterId ?? e.ToString()}' barrel on bone '{barrel.boneId}' in GiantRigDef '{defName}' references unknown socketId '{barrel.socketId}'.";
                            }
                        }
                    }
                }
            }

            IEnumerable<GiantAnimDef> EnumerateClips()
            {
                if (idleAnim != null) yield return idleAnim;
                if (walkAnim != null) yield return walkAnim;
                if (runAnim != null) yield return runAnim;
                if (turnLeftAnim != null) yield return turnLeftAnim;
                if (turnRightAnim != null) yield return turnRightAnim;
                if (rangedAttackAnim != null) yield return rangedAttackAnim;
                if (downedAnim != null) yield return downedAnim;
                if (deathAnim != null) yield return deathAnim;
                if (flightEntry?.flightAnim != null) yield return flightEntry.flightAnim;
                if (ActiveFlightLocomotion?.flightLocomotionAnim != null) yield return ActiveFlightLocomotion.flightLocomotionAnim;
                if (ActiveAquaticLocomotion?.swimIdleAnim != null) yield return ActiveAquaticLocomotion.swimIdleAnim;
                if (ActiveAquaticLocomotion?.swimAnim != null) yield return ActiveAquaticLocomotion.swimAnim;
                if (ActiveAquaticLocomotion?.swimFastAnim != null) yield return ActiveAquaticLocomotion.swimFastAnim;
                if (meleeAttackAnims != null)
                {
                    for (int m = 0; m < meleeAttackAnims.Count; m++)
                    {
                        if (meleeAttackAnims[m] != null)
                        {
                            yield return meleeAttackAnims[m];
                        }
                    }
                }
                if (abilityAnims != null)
                {
                    for (int a = 0; a < abilityAnims.Count; a++)
                    {
                        if (abilityAnims[a] != null)
                        {
                            yield return abilityAnims[a];
                        }
                    }
                }
            }

            foreach (GiantAnimDef clip in EnumerateClips())
            {
                if (clip.tracks == null)
                {
                    continue;
                }
                for (int t = 0; t < clip.tracks.Count; t++)
                {
                    BoneAnimTrack tr = clip.tracks[t];
                    if (tr == null || !TryGetNodeIndex(tr.nodeId, out int idx) || tr.keyframes == null)
                    {
                        continue;
                    }
                    if (ActiveVehicleLocomotion != null && !ReferenceEquals(clip, deathAnim))
                    {
                        for (int k = 0; k < tr.keyframes.Count; k++)
                        {
                            BoneKeyframe kf = tr.keyframes[k];
                            if (kf != null && kf.offset.sqrMagnitude > 1e-5f)
                            {
                                yield return $"GiantAnimDef '{clip.defName}' referenced by ground vehicle GiantRigDef '{defName}' sets non-zero offset {kf.offset} on bone '{tr.nodeId}'. Ground vehicle hulls must remain unshifted; weapon recoil must use <turretRecoil> on the barrel.";
                            }
                        }
                    }
                    if (SortedNodes[idx].parentRuntimeIndex < 0)
                    {
                        continue;
                    }
                    for (int k = 0; k < tr.keyframes.Count; k++)
                    {
                        BoneKeyframe kf = tr.keyframes[k];
                        if (kf == null)
                        {
                            continue;
                        }
                        if (kf.offset.sqrMagnitude > 1e-5f)
                        {
                            yield return $"GiantAnimDef '{clip.defName}' referenced by GiantRigDef '{defName}' sets non-zero offset {kf.offset} on child bone '{tr.nodeId}'. Child bone offsets are prohibited; child joints must remain fixed on their parent torso sockets.";
                        }
                        if (SortedNodes[idx].isRigid && (Mathf.Abs(kf.angle) > 1e-4f || Mathf.Abs(kf.sagittalPitch) > 1e-4f))
                        {
                            yield return $"GiantAnimDef '{clip.defName}' referenced by GiantRigDef '{defName}' sets non-zero angle {kf.angle:F2} or sagittalPitch {kf.sagittalPitch:F2} on rigid bone '{tr.nodeId}' (<isRigid>true</isRigid>). Rigid vehicle/hull sub-parts must remain locked at 0 deg relative to their parent.";
                        }
                    }
                }
            }

            if (meleeAttackAnims != null)
            {
                if (IsVehicleRig && meleeAttackAnims.Count > 0)
                {
                    yield return $"GiantRigDef '{defName}' is a vehicle rig (<vehicleLocomotion> or <flightVehicleLocomotion>) and cannot define <meleeAttackAnims> (found {meleeAttackAnims.Count}); vehicle rigs strictly forbid melee actions.";
                }
                for (int i = 0; i < meleeAttackAnims.Count; i++)
                {
                    GiantAnimDef clip = meleeAttackAnims[i];
                    if (clip == null)
                    {
                        yield return $"GiantRigDef '{defName}' has a null entry in meleeAttackAnims.";
                        continue;
                    }
                    if (clip.loop)
                    {
                        yield return $"GiantRigDef '{defName}' melee attack clip '{clip.defName}' must have <loop>false</loop> so melee strikes play a finite visible one-shot.";
                    }
                    if (string.IsNullOrWhiteSpace(clip.triggerToolTag))
                    {
                        yield return $"GiantRigDef '{defName}' melee attack clip '{clip.defName}' must specify non-empty <triggerToolTag>.";
                    }
                }
            }

            if (idleAnim != null && !idleAnim.loop)
            {
                yield return $"GiantRigDef '{defName}' idleAnim '{idleAnim.defName}' must have <loop>true</loop>.";
            }
            if (turnLeftAnim != null && !turnLeftAnim.loop)
            {
                yield return $"GiantRigDef '{defName}' turnLeftAnim '{turnLeftAnim.defName}' must have <loop>true</loop>.";
            }
            if (turnRightAnim != null && !turnRightAnim.loop)
            {
                yield return $"GiantRigDef '{defName}' turnRightAnim '{turnRightAnim.defName}' must have <loop>true</loop>.";
            }

            if (rangedAttackAnim != null)
            {
                if (rangedAttackAnim.loop)
                {
                    yield return $"GiantRigDef '{defName}' rangedAttackAnim '{rangedAttackAnim.defName}' must have <loop>false</loop>.";
                }
            }

            if (deathAnim == null)
            {
                yield return $"GiantRigDef '{defName}' is missing mandatory <deathAnim>. Every giant rig must define a dedicated non-looping skeletal death animation.";
            }
            else
            {
                if (deathAnim.loop)
                {
                    yield return $"GiantRigDef '{defName}' deathAnim '{deathAnim.defName}' must have <loop>false</loop> so the death collapse plays once and settles into its final corpse pose.";
                }
                if (deathAnim.durationTicks < 20f)
                {
                    yield return $"GiantRigDef '{defName}' deathAnim '{deathAnim.defName}' durationTicks ({deathAnim.durationTicks:F1}) must be >= 20 ticks for a visible collapse sequence.";
                }
                if (deathAnim.tracks == null || deathAnim.tracks.Count == 0)
                {
                    yield return $"GiantRigDef '{defName}' deathAnim '{deathAnim.defName}' has no bone tracks.";
                }
                else
                {
                    bool hasValidMultiKeyframeTrack = false;
                    for (int t = 0; t < deathAnim.tracks.Count; t++)
                    {
                        BoneAnimTrack tr = deathAnim.tracks[t];
                        if (tr == null || string.IsNullOrEmpty(tr.nodeId))
                        {
                            continue;
                        }
                        if (!TryGetNodeIndex(tr.nodeId, out _))
                        {
                            yield return $"GiantRigDef '{defName}' deathAnim '{deathAnim.defName}' references unknown nodeId '{tr.nodeId}'.";
                            continue;
                        }
                        if (tr.keyframes != null && tr.keyframes.Count >= 2)
                        {
                            hasValidMultiKeyframeTrack = true;
                        }
                    }
                    if (!hasValidMultiKeyframeTrack)
                    {
                        yield return $"GiantRigDef '{defName}' deathAnim '{deathAnim.defName}' must animate at least one valid rig bone across >= 2 keyframes (start -> collapse).";
                    }
                    if (!deathAnim.HasNonTrivialMotion(minAngleSpanDeg: 8f, minRootOffsetCells: 0.25f))
                    {
                        yield return $"GiantRigDef '{defName}' deathAnim '{deathAnim.defName}' lacks non-trivial skeletal motion (requires >= 8 deg bone rotation span or >= 0.25 cells root collapse offset).";
                    }
                }
            }

            if (abilityAnims != null)
            {
                for (int i = 0; i < abilityAnims.Count; i++)
                {
                    GiantAnimDef clip = abilityAnims[i];
                    if (clip == null)
                    {
                        yield return $"GiantRigDef '{defName}' has a null entry in abilityAnims.";
                        continue;
                    }
                    if (clip.loop)
                    {
                        yield return $"GiantRigDef '{defName}' ability clip '{clip.defName}' must have <loop>false</loop>.";
                    }
                    if (string.IsNullOrWhiteSpace(clip.triggerToolTag) && string.IsNullOrWhiteSpace(clip.defName))
                    {
                        yield return $"GiantRigDef '{defName}' ability clip '{clip.defName}' must have non-empty triggerToolTag or defName.";
                    }
                }
            }

            BoneNodeDef[] sortedForAttacks = SortedNodes;
            var seenAttackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int n = 0; n < sortedForAttacks.Length; n++)
            {
                BoneNodeDef bNode = sortedForAttacks[n];
                if (bNode?.partAttacks == null || bNode.partAttacks.Count == 0)
                {
                    continue;
                }
                for (int a = 0; a < bNode.partAttacks.Count; a++)
                {
                    GiantPartAttackDef pAtk = bNode.partAttacks[a];
                    if (pAtk == null)
                    {
                        yield return $"GiantRigDef '{defName}' bone '{bNode.nodeId}' contains null GiantPartAttackDef at index {a}.";
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(pAtk.attackId) && !seenAttackIds.Add(pAtk.attackId))
                    {
                        yield return $"GiantRigDef '{defName}' has duplicate GiantPartAttackDef attackId '{pAtk.attackId}'.";
                    }
                    if (IsVehicleRig && pAtk.kind == GiantPartAttackKind.Melee)
                    {
                        yield return $"GiantRigDef '{defName}' is a vehicle rig and bone '{bNode.nodeId}' partAttack '{pAtk.attackId}' cannot use <kind>Melee</kind>; vehicle rigs strictly forbid melee attacks.";
                    }
                    foreach (string atkErr in pAtk.ConfigErrors(defName, bNode))
                    {
                        yield return atkErr;
                    }
                    if (!string.IsNullOrEmpty(pAtk.rangeOriginBoneId) && !TryGetNodeIndex(pAtk.rangeOriginBoneId, out _))
                    {
                        yield return $"GiantRigDef '{defName}' bone '{bNode.nodeId}' partAttack '{pAtk.attackId}' references unknown rangeOriginBoneId '{pAtk.rangeOriginBoneId}'.";
                    }
                    if (pAtk.attackAnim != null)
                    {
                        if (ActiveVehicleLocomotion != null && pAtk.attackAnim.tracks != null)
                        {
                            for (int t = 0; t < pAtk.attackAnim.tracks.Count; t++)
                            {
                                BoneAnimTrack tr = pAtk.attackAnim.tracks[t];
                                if (tr?.keyframes == null) continue;
                                for (int k = 0; k < tr.keyframes.Count; k++)
                                {
                                    BoneKeyframe kf = tr.keyframes[k];
                                    if (kf != null && kf.offset.sqrMagnitude > 1e-5f)
                                    {
                                        yield return $"GiantAnimDef '{pAtk.attackAnim.defName}' on ground vehicle GiantRigDef '{defName}' bone '{bNode.nodeId}' partAttack '{pAtk.attackId}' sets non-zero offset {kf.offset} on bone '{tr.nodeId}'. Ground vehicle hulls must remain unshifted; weapon recoil must use <turretRecoil> on the barrel.";
                                    }
                                }
                            }
                        }
                        foreach (string clipErr in ValidateExtraClipRigidAndChildOffsets(pAtk.attackAnim, $"bone '{bNode.nodeId}' partAttack '{pAtk.attackId}'"))
                        {
                            yield return clipErr;
                        }
                        if (!string.IsNullOrWhiteSpace(pAtk.attackAnim.isolatedSubtreeRootBoneId))
                        {
                            if (!TryGetNodeIndex(pAtk.attackAnim.isolatedSubtreeRootBoneId, out int isoRootIdx))
                            {
                                yield return $"GiantAnimDef '{pAtk.attackAnim.defName}' on GiantRigDef '{defName}' references unknown isolatedSubtreeRootBoneId '{pAtk.attackAnim.isolatedSubtreeRootBoneId}'.";
                            }
                            else if (pAtk.attackAnim.tracks != null)
                            {
                                for (int t = 0; t < pAtk.attackAnim.tracks.Count; t++)
                                {
                                    BoneAnimTrack tr = pAtk.attackAnim.tracks[t];
                                    if (tr == null || string.IsNullOrEmpty(tr.nodeId))
                                    {
                                        continue;
                                    }
                                    if (!TryGetNodeIndex(tr.nodeId, out int trNodeIdx) || !IsBoneInSubtree(trNodeIdx, isoRootIdx))
                                    {
                                        yield return $"GiantAnimDef '{pAtk.attackAnim.defName}' declares isolatedSubtreeRootBoneId='{pAtk.attackAnim.isolatedSubtreeRootBoneId}', so track on non-descendant bone '{tr.nodeId}' is prohibited.";
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (bezierChains != null)
            {
                for (int c = 0; c < bezierChains.Count; c++)
                {
                    BezierChainDef chain = bezierChains[c];
                    if (chain == null)
                    {
                        yield return $"GiantRigDef '{defName}' contains null BezierChainDef at index {c}.";
                        continue;
                    }
                    if (chain.boneIds == null || chain.boneIds.Count < 2)
                    {
                        yield return $"BezierChainDef '{chain.chainId}' in GiantRigDef '{defName}' must contain at least 2 boneIds.";
                        continue;
                    }
                    for (int k = 0; k < chain.boneIds.Count; k++)
                    {
                        string bid = chain.boneIds[k];
                        if (!TryGetNodeIndex(bid, out int bIdx))
                        {
                            yield return $"BezierChainDef '{chain.chainId}' in GiantRigDef '{defName}' references unknown boneId '{bid}'.";
                            break;
                        }
                        if (SortedNodes[bIdx].isRigid)
                        {
                            yield return $"BezierChainDef '{chain.chainId}' in GiantRigDef '{defName}' cannot include rigid bone '{bid}' (<isRigid>true</isRigid>).";
                        }
                        if (k > 0 && !string.Equals(SortedNodes[bIdx].parentId, chain.boneIds[k - 1], StringComparison.Ordinal))
                        {
                            yield return $"BezierChainDef '{chain.chainId}' in GiantRigDef '{defName}' must form a contiguous parent chain: bone '{bid}' has parentId '{SortedNodes[bIdx].parentId}' instead of '{chain.boneIds[k - 1]}'.";
                        }
                    }
                }
            }

            if (softWhips != null)
            {
                for (int s = 0; s < softWhips.Count; s++)
                {
                    SoftWhipDef whip = softWhips[s];
                    if (whip == null)
                    {
                        yield return $"GiantRigDef '{defName}' contains null SoftWhipDef at index {s}.";
                        continue;
                    }
                    if (string.IsNullOrEmpty(whip.rootBoneId) || !TryGetNodeIndex(whip.rootBoneId, out _))
                    {
                        yield return $"SoftWhipDef '{whip.whipId}' in GiantRigDef '{defName}' references unknown rootBoneId '{whip.rootBoneId}'.";
                    }
                    if (string.IsNullOrEmpty(whip.texPath))
                    {
                        yield return $"SoftWhipDef '{whip.whipId}' in GiantRigDef '{defName}' has empty texPath.";
                    }
                }
            }

            if (rollingTracks != null)
            {
                var seenTracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int t = 0; t < rollingTracks.Count; t++)
                {
                    GiantRollingTrackDef track = rollingTracks[t];
                    if (track == null)
                    {
                        yield return $"GiantRigDef '{defName}' contains null GiantRollingTrackDef at index {t}.";
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(track.trackId) && !seenTracks.Add(track.trackId))
                    {
                        yield return $"GiantRigDef '{defName}' has duplicate rollingTrack trackId '{track.trackId}'.";
                    }
                    if (string.IsNullOrEmpty(track.parentBoneId) || !TryGetNodeIndex(track.parentBoneId, out _))
                    {
                        yield return $"GiantRollingTrackDef '{track.trackId}' in GiantRigDef '{defName}' references unknown parentBoneId '{track.parentBoneId}'.";
                    }
                    if (string.IsNullOrEmpty(track.dorsalRibbonTexPath) && string.IsNullOrEmpty(track.lateralRibbonTexPath))
                    {
                        yield return $"GiantRollingTrackDef '{track.trackId}' in GiantRigDef '{defName}' must specify dorsalRibbonTexPath or lateralRibbonTexPath.";
                    }
                    if (track.nearLoopPixels != null && track.nearLoopPixels.Count > 0 && track.nearLoopPixels.Count < 4)
                    {
                        yield return $"GiantRollingTrackDef '{track.trackId}' nearLoopPixels in GiantRigDef '{defName}' must contain at least 4 points when set.";
                    }
                    if (track.wheels != null)
                    {
                        for (int w = 0; w < track.wheels.Count; w++)
                        {
                            GiantTrackWheelDef wheel = track.wheels[w];
                            if (wheel == null || string.IsNullOrEmpty(wheel.texPath) || wheel.diameterCells <= 0.05f)
                            {
                                yield return $"GiantRollingTrackDef '{track.trackId}' wheel[{w}] in GiantRigDef '{defName}' has invalid texPath or diameterCells.";
                            }
                        }
                    }
                }
            }

            if (steering != null)
            {
                if (steering.lookChains != null)
                {
                    for (int c = 0; c < steering.lookChains.Count; c++)
                    {
                        SteeringLookChainDef chain = steering.lookChains[c];
                        if (chain == null)
                        {
                            yield return $"GiantRigDef '{defName}' contains null SteeringLookChainDef at index {c}.";
                            continue;
                        }
                        if (chain.boneIds == null || chain.boneIds.Count == 0)
                        {
                            yield return $"SteeringLookChainDef '{chain.chainId}' in GiantRigDef '{defName}' must specify at least 1 boneId.";
                            continue;
                        }
                        if (chain.weights != null && chain.weights.Count > 0 && chain.weights.Count != chain.boneIds.Count)
                        {
                            yield return $"SteeringLookChainDef '{chain.chainId}' in GiantRigDef '{defName}' has {chain.weights.Count} weights for {chain.boneIds.Count} boneIds.";
                        }
                        if (chain.maxChainAngleDeg <= 0f || chain.maxBoneAngleDeg <= 0f)
                        {
                            yield return $"SteeringLookChainDef '{chain.chainId}' in GiantRigDef '{defName}' must have positive maxChainAngleDeg and maxBoneAngleDeg.";
                        }
                        for (int k = 0; k < chain.boneIds.Count; k++)
                        {
                            string bid = chain.boneIds[k];
                            if (!TryGetNodeIndex(bid, out int lbIdx))
                            {
                                yield return $"SteeringLookChainDef '{chain.chainId}' in GiantRigDef '{defName}' references unknown boneId '{bid}'.";
                            }
                            else if (SortedNodes[lbIdx].isRigid)
                            {
                                yield return $"SteeringLookChainDef '{chain.chainId}' in GiantRigDef '{defName}' cannot target rigid bone '{bid}' (<isRigid>true</isRigid>).";
                            }
                        }
                    }
                }

                if (steering.lagBones != null)
                {
                    for (int i = 0; i < steering.lagBones.Count; i++)
                    {
                        SteeringLagBoneDef lag = steering.lagBones[i];
                        if (lag == null)
                        {
                            yield return $"GiantRigDef '{defName}' contains null SteeringLagBoneDef at index {i}.";
                            continue;
                        }
                        if (string.IsNullOrEmpty(lag.boneId) || !TryGetNodeIndex(lag.boneId, out int bIdx))
                        {
                            yield return $"SteeringLagBoneDef in GiantRigDef '{defName}' references unknown boneId '{lag.boneId}'.";
                            continue;
                        }
                        if (SortedNodes[bIdx].parentRuntimeIndex < 0)
                        {
                            yield return $"SteeringLagBoneDef '{lag.boneId}' in GiantRigDef '{defName}' cannot target root bone.";
                        }
                        if (SortedNodes[bIdx].isRigid)
                        {
                            yield return $"SteeringLagBoneDef '{lag.boneId}' in GiantRigDef '{defName}' cannot target rigid bone (<isRigid>true</isRigid>).";
                        }
                        if (lag.maxBendAngleDeg <= 0f)
                        {
                            yield return $"SteeringLagBoneDef '{lag.boneId}' in GiantRigDef '{defName}' must have positive maxBendAngleDeg.";
                        }
                    }
                }

                if (steering.yawBankBones != null)
                {
                    for (int i = 0; i < steering.yawBankBones.Count; i++)
                    {
                        SteeringYawBankDef bank = steering.yawBankBones[i];
                        if (bank == null)
                        {
                            yield return $"GiantRigDef '{defName}' contains null SteeringYawBankDef at index {i}.";
                            continue;
                        }
                        if (string.IsNullOrEmpty(bank.boneId) || !TryGetNodeIndex(bank.boneId, out int ybIdx))
                        {
                            yield return $"SteeringYawBankDef in GiantRigDef '{defName}' references unknown boneId '{bank.boneId}'.";
                        }
                        else if (SortedNodes[ybIdx].isRigid)
                        {
                            yield return $"SteeringYawBankDef '{bank.boneId}' in GiantRigDef '{defName}' cannot target rigid bone (<isRigid>true</isRigid>).";
                        }
                    }
                }
            }
        }

        public static string BuildToolMatchTag(Tool tool)
        {
            if (tool == null)
            {
                return string.Empty;
            }
            return $"{tool.untranslatedLabel}|{tool.linkedBodyPartsGroup?.defName}|{tool.label}|{tool.id}";
        }

        public static bool MatchesToolTag(string candidateTagOrLabel, string clipTriggerTag)
        {
            if (string.IsNullOrEmpty(candidateTagOrLabel) || string.IsNullOrWhiteSpace(clipTriggerTag))
            {
                return false;
            }
            return candidateTagOrLabel.IndexOf(clipTriggerTag.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public GiantAnimDef ResolveMeleeAttackAnim(string toolTagOrLabel)
        {
            if (string.IsNullOrEmpty(toolTagOrLabel) || meleeAttackAnims == null || meleeAttackAnims.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < meleeAttackAnims.Count; i++)
            {
                GiantAnimDef cand = meleeAttackAnims[i];
                if (cand == null || cand.loop || string.IsNullOrWhiteSpace(cand.triggerToolTag))
                {
                    continue;
                }
                if (MatchesToolTag(toolTagOrLabel, cand.triggerToolTag))
                {
                    return cand;
                }
            }

            return null;
        }

        public GiantAnimDef ResolveAbilityAnim(string tagOrDefName)
        {
            if (string.IsNullOrEmpty(tagOrDefName) || abilityAnims == null || abilityAnims.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < abilityAnims.Count; i++)
            {
                GiantAnimDef cand = abilityAnims[i];
                if (cand == null || cand.loop)
                {
                    continue;
                }
                bool matches = string.Equals(cand.defName, tagOrDefName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(cand.triggerToolTag) &&
                     (tagOrDefName.IndexOf(cand.triggerToolTag.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 ||
                      cand.triggerToolTag.Trim().IndexOf(tagOrDefName, StringComparison.OrdinalIgnoreCase) >= 0));
                if (matches)
                {
                    return cand;
                }
            }

            return null;
        }

        public IEnumerable<string> ValidateExtraClipRigidAndChildOffsets(GiantAnimDef clip, string clipRole = null)
        {
            if (clip?.tracks == null)
            {
                yield break;
            }
            EnsureTopologyBuilt();
            string roleSuffix = string.IsNullOrEmpty(clipRole) ? string.Empty : $" ({clipRole})";
            for (int t = 0; t < clip.tracks.Count; t++)
            {
                BoneAnimTrack tr = clip.tracks[t];
                if (tr == null || !TryGetNodeIndex(tr.nodeId, out int idx) || SortedNodes[idx].parentRuntimeIndex < 0 || tr.keyframes == null)
                {
                    continue;
                }
                for (int k = 0; k < tr.keyframes.Count; k++)
                {
                    BoneKeyframe kf = tr.keyframes[k];
                    if (kf == null)
                    {
                        continue;
                    }
                    if (kf.offset.sqrMagnitude > 1e-5f)
                    {
                        yield return $"GiantAnimDef '{clip.defName}'{roleSuffix} on GiantRigDef '{defName}' sets non-zero offset {kf.offset} on child bone '{tr.nodeId}'.";
                    }
                    if (SortedNodes[idx].isRigid && (Mathf.Abs(kf.angle) > 1e-4f || Mathf.Abs(kf.sagittalPitch) > 1e-4f || Mathf.Abs(kf.coronalFlap) > 1e-4f))
                    {
                        yield return $"GiantAnimDef '{clip.defName}'{roleSuffix} on GiantRigDef '{defName}' sets non-zero angle {kf.angle:F2}, sagittalPitch {kf.sagittalPitch:F2}, or coronalFlap {kf.coronalFlap:F2} on rigid bone '{tr.nodeId}' (<isRigid>true</isRigid>).";
                    }
                }
            }
        }

        public IEnumerable<string> ValidateThingDefCombatAnimations(
            ThingDef parentDef,
            List<RimWorld.AbilityDef> extraAbilities = null,
            ThingDef rangedWeaponDef = null,
            string contextLabel = null)
        {
            string scope = string.IsNullOrEmpty(contextLabel)
                ? $"ThingDef '{parentDef?.defName}'"
                : $"ThingDef '{parentDef?.defName}' ({contextLabel})";

            bool hasPartAttacks = false;
            if (nodes != null)
            {
                for (int n = 0; n < nodes.Count; n++)
                {
                    if (nodes[n]?.partAttacks != null && nodes[n].partAttacks.Count > 0)
                    {
                        hasPartAttacks = true;
                        break;
                    }
                }
            }

            if (hasPartAttacks)
            {
                if (parentDef?.tools != null && parentDef.tools.Count > 0)
                {
                    yield return $"{scope} defines {parentDef.tools.Count} base-Pawn <tools>; GCF giants with <partAttacks> strictly forbid local Pawn attacks.";
                }

                if (rangedWeaponDef != null)
                {
                    yield return $"{scope} defines equipped weapon '{rangedWeaponDef.defName}'; GCF giants with <partAttacks> strictly forbid local Pawn equipment weapons.";
                }
            }
            else if (parentDef?.tools != null && parentDef.tools.Count > 0 && (meleeAttackAnims == null || meleeAttackAnims.Count == 0))
            {
                yield return $"{scope} defines {parentDef.tools.Count} <tools> without any <meleeAttackAnims> on rig '{defName}'.";
            }

            if (rangedAttackAnim != null && rangedAttackAnim.loop)
            {
                yield return $"{scope} rangedAttackAnim '{rangedAttackAnim.defName}' on rig '{defName}' must have <loop>false</loop>.";
            }

            if (deathAnim == null)
            {
                yield return $"{scope} rig '{defName}' is missing mandatory <deathAnim>.";
            }
            else if (deathAnim.loop)
            {
                yield return $"{scope} deathAnim '{deathAnim.defName}' on rig '{defName}' must have <loop>false</loop>.";
            }

            EnsureTopologyBuilt();
            var abilitiesToCheck = new List<RimWorld.AbilityDef>();
            if (extraAbilities != null)
            {
                abilitiesToCheck.AddRange(extraAbilities);
            }
            for (int i = 0; i < abilitiesToCheck.Count; i++)
            {
                RimWorld.AbilityDef ab = abilitiesToCheck[i];
                if (ab == null || ab.comps == null)
                {
                    continue;
                }
                CompProperties_AbilityRequiresGiantBone reqBone = null;
                for (int c = 0; c < ab.comps.Count; c++)
                {
                    if (ab.comps[c] is CompProperties_AbilityRequiresGiantBone rb)
                    {
                        reqBone = rb;
                        break;
                    }
                }
                if (reqBone == null)
                {
                    if (hasPartAttacks && ab.comps.Count > 0)
                    {
                        yield return $"{scope} ability '{ab.defName}' on rig '{defName}' must declare CompProperties_AbilityRequiresGiantBone binding it to its executing BoneNodeDef(s) (or root hull bone).";
                    }
                }
                else if (reqBone.requiredBoneIds == null || reqBone.requiredBoneIds.Count == 0)
                {
                    yield return $"{scope} ability '{ab.defName}' on rig '{defName}' declares CompProperties_AbilityRequiresGiantBone with empty requiredBoneIds.";
                }
                else
                {
                    for (int b = 0; b < reqBone.requiredBoneIds.Count; b++)
                    {
                        string bid = reqBone.requiredBoneIds[b];
                        if (string.IsNullOrWhiteSpace(bid) || !TryGetNodeIndex(bid, out _))
                        {
                            yield return $"{scope} ability '{ab.defName}' references unknown requiredBoneId '{bid}' on rig '{defName}'.";
                        }
                    }
                }
            }

            if (parentDef?.race?.body != null)
            {
                foreach (string bodyErr in ValidateAnatomicalTreeAlignment(parentDef.race.body, scope))
                {
                    yield return bodyErr;
                }
            }
        }

        private static IReadOnlyList<BodyPartRecord> GetAllBodyPartRecords(BodyDef bodyDef)
        {
            if (bodyDef == null)
            {
                return Array.Empty<BodyPartRecord>();
            }
            if (bodyDef.AllParts != null && bodyDef.AllParts.Count > 0)
            {
                return bodyDef.AllParts;
            }
            if (bodyDef.corePart == null)
            {
                return Array.Empty<BodyPartRecord>();
            }
            var list = new List<BodyPartRecord>();
            void Walk(BodyPartRecord rec)
            {
                if (rec == null) return;
                list.Add(rec);
                if (rec.parts == null) return;
                for (int i = 0; i < rec.parts.Count; i++)
                {
                    Walk(rec.parts[i]);
                }
            }
            Walk(bodyDef.corePart);
            return list;
        }

        public static BodyPartRecord ResolveLinkedBodyPartRecord(
            BodyDef bodyDef,
            BodyPartDef linkedPartDef,
            string customLabel = null,
            int partIndex = -1)
        {
            if (bodyDef == null || linkedPartDef == null)
            {
                return null;
            }

            IReadOnlyList<BodyPartRecord> allParts = GetAllBodyPartRecords(bodyDef);
            int matchCounter = 0;
            for (int p = 0; p < allParts.Count; p++)
            {
                BodyPartRecord rec = allParts[p];
                if (rec?.def == null)
                {
                    continue;
                }
                bool sameDef = rec.def == linkedPartDef
                    || (!string.IsNullOrEmpty(linkedPartDef.defName)
                        && string.Equals(rec.def.defName, linkedPartDef.defName, StringComparison.Ordinal));
                if (!sameDef)
                {
                    continue;
                }
                string recCustomLabel = !string.IsNullOrEmpty(rec.untranslatedCustomLabel)
                    ? rec.untranslatedCustomLabel
                    : rec.customLabel;
                if (!string.IsNullOrEmpty(customLabel) &&
                    !string.Equals(recCustomLabel, customLabel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (partIndex >= 0 && matchCounter != partIndex)
                {
                    matchCounter++;
                    continue;
                }
                return rec;
            }
            return null;
        }

        public static bool IsBodyPartStrictDescendant(BodyPartRecord candidateDescendant, BodyPartRecord ancestor)
        {
            if (candidateDescendant == null || ancestor == null || candidateDescendant == ancestor)
            {
                return false;
            }
            BodyPartRecord cur = candidateDescendant.parent;
            while (cur != null)
            {
                if (cur == ancestor)
                {
                    return true;
                }
                cur = cur.parent;
            }
            return false;
        }

        /// <summary>
        /// Validates 1-to-1 alignment and parent-child hierarchical nesting between GiantRigDef bones and BodyDef parts.
        /// Ensures severable bones do not share the same BodyPartRecord and child bones map to anatomical descendants
        /// of their parent bones so severing a proximal bone natively cascades down the entire limb/wing/turret subtree.
        /// </summary>
        public IEnumerable<string> ValidateAnatomicalTreeAlignment(BodyDef bodyDef, string contextLabel = null)
        {
            IReadOnlyList<BodyPartRecord> allParts = GetAllBodyPartRecords(bodyDef);
            if (allParts.Count == 0)
            {
                yield break;
            }

            EnsureTopologyBuilt();
            string scope = string.IsNullOrEmpty(contextLabel)
                ? $"GiantRigDef '{defName}' vs BodyDef '{bodyDef.defName}'"
                : $"{contextLabel} [rig='{defName}', body='{bodyDef.defName}']";

            BoneNodeDef[] ordered = SortedNodes;
            var resolvedParts = new BodyPartRecord[ordered.Length];
            var severableOwnerByPart = new Dictionary<BodyPartRecord, string>();

            for (int i = 0; i < ordered.Length; i++)
            {
                BoneNodeDef node = ordered[i];
                if (node.linkedBodyPart == null)
                {
                    continue;
                }

                BodyPartRecord matched = ResolveLinkedBodyPartRecord(
                    bodyDef,
                    node.linkedBodyPart,
                    node.linkedBodyPartCustomLabel,
                    node.linkedBodyPartIndex);
                if (matched == null)
                {
                    yield return $"{scope}: bone '{node.nodeId}' failed to resolve linkedBodyPart='{node.linkedBodyPart.defName}' (customLabel='{node.linkedBodyPartCustomLabel}').";
                    continue;
                }

                resolvedParts[i] = matched;
                if (node.hideWhenMissing)
                {
                    if (severableOwnerByPart.TryGetValue(matched, out string prevOwner))
                    {
                        yield return $"{scope}: severable bones '{prevOwner}' and '{node.nodeId}' share the same BodyPartRecord '{matched.LabelShort} ({matched.def.defName})'. Give each severable bone its own 1-to-1 nested BodyPartRecord.";
                    }
                    else
                    {
                        severableOwnerByPart[matched] = node.nodeId;
                    }
                }
            }

            for (int i = 0; i < ordered.Length; i++)
            {
                BoneNodeDef node = ordered[i];
                BodyPartRecord childPart = resolvedParts[i];
                if (!node.hideWhenMissing || childPart == null || node.parentRuntimeIndex < 0)
                {
                    continue;
                }

                int pIdx = node.parentRuntimeIndex;
                BodyPartRecord ancestorPart = null;
                string ancestorBoneId = null;
                while (pIdx >= 0)
                {
                    if (resolvedParts[pIdx] != null)
                    {
                        ancestorPart = resolvedParts[pIdx];
                        ancestorBoneId = ordered[pIdx].nodeId;
                        break;
                    }
                    pIdx = ordered[pIdx].parentRuntimeIndex;
                }

                if (ancestorPart != null && !IsBodyPartStrictDescendant(childPart, ancestorPart))
                {
                    yield return $"{scope}: child bone '{node.nodeId}' (part '{childPart.def.defName}') is parented to bone '{ancestorBoneId}' (part '{ancestorPart.def.defName}'), but '{childPart.def.defName}' is NOT nested inside '{ancestorPart.def.defName}' <parts> in BodyDef '{bodyDef.defName}'.";
                }
            }

            if (softWhips != null)
            {
                for (int s = 0; s < softWhips.Count; s++)
                {
                    SoftWhipDef whip = softWhips[s];
                    if (whip?.tendrilBodyPart == null)
                    {
                        continue;
                    }
                    BodyPartRecord tendrilPart = ResolveLinkedBodyPartRecord(bodyDef, whip.tendrilBodyPart, whip.tendrilBodyPartCustomLabel);
                    if (tendrilPart == null)
                    {
                        yield return $"{scope}: SoftWhip '{whip.whipId}' failed to resolve tendrilBodyPart='{whip.tendrilBodyPart.defName}' (customLabel='{whip.tendrilBodyPartCustomLabel}').";
                        continue;
                    }
                    if (severableOwnerByPart.TryGetValue(tendrilPart, out string collidingOwner))
                    {
                        yield return $"{scope}: SoftWhip '{whip.whipId}' collides with bone '{collidingOwner}' on BodyPartRecord '{tendrilPart.def.defName}'.";
                    }
                    else
                    {
                        severableOwnerByPart[tendrilPart] = $"softWhip:{whip.whipId}";
                    }

                    if (whip.resolvedRootBoneIndex >= 0 && resolvedParts[whip.resolvedRootBoneIndex] is BodyPartRecord rootPart &&
                        !IsBodyPartStrictDescendant(tendrilPart, rootPart))
                    {
                        yield return $"{scope}: SoftWhip '{whip.whipId}' tendril part '{tendrilPart.def.defName}' must be nested under root bone '{whip.rootBoneId}' part '{rootPart.def.defName}'.";
                    }
                }
            }
        }

        private static readonly (string LeftToken, string RightToken)[] BilateralIdTokens =
        {
            ("_left", "_right"),
            ("_Left", "_Right"),
            ("_LEFT", "_RIGHT"),
            ("_l_", "_r_"),
            ("_L_", "_R_"),
            ("_fl_", "_fr_"),
            ("_rl_", "_rr_"),
            ("_l1", "_r1"),
            ("_l2", "_r2"),
            ("_l3", "_r3"),
            ("_l4", "_r4"),
            ("_l5", "_r5"),
            ("_l6", "_r6"),
            ("_l7", "_r7"),
            ("_l8", "_r8"),
        };

        public static bool TryResolveRightCounterpartId(
            string candidateLeftId,
            Func<string, bool> idExists,
            out string rightId)
        {
            rightId = null;
            if (string.IsNullOrEmpty(candidateLeftId) || idExists == null)
            {
                return false;
            }

            for (int i = 0; i < BilateralIdTokens.Length; i++)
            {
                (string lTok, string rTok) = BilateralIdTokens[i];
                int pos = candidateLeftId.IndexOf(lTok, StringComparison.Ordinal);
                if (pos >= 0)
                {
                    string mapped = candidateLeftId.Substring(0, pos) + rTok + candidateLeftId.Substring(pos + lTok.Length);
                    if (!string.Equals(mapped, candidateLeftId, StringComparison.Ordinal) && idExists(mapped))
                    {
                        rightId = mapped;
                        return true;
                    }
                }
            }

            if (!candidateLeftId.EndsWith("_right", StringComparison.OrdinalIgnoreCase) &&
                !candidateLeftId.EndsWith("_left", StringComparison.OrdinalIgnoreCase))
            {
                string suffixed = candidateLeftId + "_right";
                if (idExists(suffixed))
                {
                    rightId = suffixed;
                    return true;
                }
            }

            return false;
        }

        private static bool IsGroundedWalkingLimbId(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return false;
            }
            return nodeId.StartsWith("leg_", StringComparison.OrdinalIgnoreCase)
                || nodeId.StartsWith("thigh_", StringComparison.OrdinalIgnoreCase)
                || nodeId.StartsWith("shin_", StringComparison.OrdinalIgnoreCase)
                || nodeId.StartsWith("ankle_", StringComparison.OrdinalIgnoreCase)
                || nodeId.StartsWith("hind_paw_", StringComparison.OrdinalIgnoreCase)
                || nodeId.StartsWith("paw_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Enforces universal physical layer invariants across every GiantRigDef:
        /// 1. Parent-child minimum layer separation (&gt;= minParentChildDelta) across all active view sectors.
        /// 2. Strict bilateral left/right symmetry across bones, SkinnedMeshes, SoftWhips, and RollingTracks
        ///    (Dorsal L == R, East/West antisymmetric L.East == R.West and L.West == R.East).
        /// 3. Active dorsal turret &amp; weapon elevation: on rigs with active turrets, all deployed turrets,
        ///    gimbals, and deck weapons render strictly above all structural hull/deck/sponson/wing/skirt plates,
        ///    turret child barrels render strictly above their turret mounts, and 360-degree long-barrel dorsal
        ///    turrets render above limited-arc central bridges so rotating across the hull never inverts occlusion.
        /// 4. Grounded locomotion &amp; walking limb ordering: drive tracks and walking leg chains sit strictly
        ///    underneath their chassis/torso attachment and descend monotonically from proximal to distal.
        /// 5. Global silhouette underlay floor &amp; SkinnedMesh glow ordering: all silhouette underlays sit strictly
        ///    below the entire rig's minimum structural/limb layer, and glow overlays remain strictly below higher meshes.
        /// </summary>
        public IEnumerable<string> ValidatePhysicalLayerOrdering(float minParentChildDelta = 0.5f)
        {
            EnsureTopologyBuilt();
            BoneNodeDef[] ordered = SortedNodes;
            if (ordered == null || ordered.Length == 0)
            {
                yield break;
            }

            GiantBoneViewSector[] sectors = hasAnyDirectionalVariants
                ? new[] { GiantBoneViewSector.Dorsal, GiantBoneViewSector.East, GiantBoneViewSector.West }
                : new[] { GiantBoneViewSector.Dorsal };

            // 1. Parent-child minimum layer separation across active sectors
            for (int i = 0; i < ordered.Length; i++)
            {
                BoneNodeDef child = ordered[i];
                if (child.parentRuntimeIndex < 0)
                {
                    continue;
                }
                BoneNodeDef parent = ordered[child.parentRuntimeIndex];
                bool lockedToParent = !child.HasDirectionalVariants
                    || string.Equals(child.sectorSourceBoneId, parent.nodeId, StringComparison.Ordinal);

                for (int ps = 0; ps < sectors.Length; ps++)
                {
                    GiantBoneViewSector pSec = sectors[ps];
                    float pLayer = parent.GetLayerOffset(pSec);
                    if (lockedToParent)
                    {
                        float cLayer = child.GetLayerOffset(pSec);
                        float delta = Mathf.Abs(pLayer - cLayer);
                        if (delta + 1e-4f < minParentChildDelta)
                        {
                            yield return $"GiantRigDef '{defName}' parent-child layer delta too small on '{parent.nodeId}'[{pSec}]({pLayer:F2}) -> '{child.nodeId}'[{pSec}]({cLayer:F2}): |delta|={delta:F2} < {minParentChildDelta:F2}.";
                        }
                    }
                    else
                    {
                        for (int cs = 0; cs < sectors.Length; cs++)
                        {
                            GiantBoneViewSector cSec = sectors[cs];
                            float cLayer = child.GetLayerOffset(cSec);
                            float delta = Mathf.Abs(pLayer - cLayer);
                            if (delta + 1e-4f < minParentChildDelta)
                            {
                                yield return $"GiantRigDef '{defName}' parent-child layer delta too small on '{parent.nodeId}'[{pSec}]({pLayer:F2}) -> '{child.nodeId}'[{cSec}]({cLayer:F2}): |delta|={delta:F2} < {minParentChildDelta:F2}.";
                            }
                        }
                    }
                }
            }

            // 2. Bilateral left/right symmetry across bones, SkinnedMeshes, SoftWhips, and RollingTracks
            for (int i = 0; i < ordered.Length; i++)
            {
                BoneNodeDef leftBone = ordered[i];
                if (!TryResolveRightCounterpartId(leftBone.nodeId, id => TryGetNodeIndex(id, out _), out string rightBoneId) ||
                    !TryGetNodeIndex(rightBoneId, out int rIdx))
                {
                    continue;
                }
                BoneNodeDef rightBone = ordered[rIdx];
                float lDorsal = leftBone.GetLayerOffset(GiantBoneViewSector.Dorsal);
                float rDorsal = rightBone.GetLayerOffset(GiantBoneViewSector.Dorsal);
                if (Mathf.Abs(lDorsal - rDorsal) > 1e-4f)
                {
                    yield return $"GiantRigDef '{defName}' bilateral Dorsal layerOffset asymmetry between '{leftBone.nodeId}' ({lDorsal:F2}) and '{rightBone.nodeId}' ({rDorsal:F2}).";
                }
                if (hasAnyDirectionalVariants)
                {
                    float lEast = leftBone.GetLayerOffset(GiantBoneViewSector.East);
                    float rWest = rightBone.GetLayerOffset(GiantBoneViewSector.West);
                    float lWest = leftBone.GetLayerOffset(GiantBoneViewSector.West);
                    float rEast = rightBone.GetLayerOffset(GiantBoneViewSector.East);
                    if (Mathf.Abs(lEast - rWest) > 1e-4f || Mathf.Abs(lWest - rEast) > 1e-4f)
                    {
                        yield return $"GiantRigDef '{defName}' lateral East/West layerOffset mirror mismatch between '{leftBone.nodeId}' (E={lEast:F2}, W={lWest:F2}) and '{rightBone.nodeId}' (E={rEast:F2}, W={rWest:F2}).";
                    }
                }
                if (!string.IsNullOrEmpty(leftBone.silhouetteTexPath) && !string.IsNullOrEmpty(rightBone.silhouetteTexPath) &&
                    Mathf.Abs(leftBone.silhouetteLayerOffset - rightBone.silhouetteLayerOffset) > 1e-4f)
                {
                    yield return $"GiantRigDef '{defName}' bilateral silhouetteLayerOffset asymmetry between '{leftBone.nodeId}' ({leftBone.silhouetteLayerOffset:F2}) and '{rightBone.nodeId}' ({rightBone.silhouetteLayerOffset:F2}).";
                }
            }

            if (skinnedMeshes != null && skinnedMeshes.Count > 1)
            {
                var smById = new Dictionary<string, GiantSkinnedMeshDef>(StringComparer.Ordinal);
                for (int m = 0; m < skinnedMeshes.Count; m++)
                {
                    if (skinnedMeshes[m] != null && !string.IsNullOrEmpty(skinnedMeshes[m].meshId))
                    {
                        smById[skinnedMeshes[m].meshId] = skinnedMeshes[m];
                    }
                }
                for (int m = 0; m < skinnedMeshes.Count; m++)
                {
                    GiantSkinnedMeshDef lSm = skinnedMeshes[m];
                    if (lSm == null || !TryResolveRightCounterpartId(lSm.meshId, smById.ContainsKey, out string rMeshId))
                    {
                        continue;
                    }
                    GiantSkinnedMeshDef rSm = smById[rMeshId];
                    if (Mathf.Abs(lSm.layerOffset - rSm.layerOffset) > 1e-4f ||
                        Mathf.Abs(lSm.silhouetteLayerOffset - rSm.silhouetteLayerOffset) > 1e-4f ||
                        Mathf.Abs(lSm.glowLayerOffset - rSm.glowLayerOffset) > 1e-4f)
                    {
                        yield return $"GiantRigDef '{defName}' bilateral SkinnedMesh layer asymmetry between '{lSm.meshId}' and '{rSm.meshId}'.";
                    }
                }
            }

            if (softWhips != null && softWhips.Count > 1)
            {
                var swById = new Dictionary<string, SoftWhipDef>(StringComparer.Ordinal);
                for (int s = 0; s < softWhips.Count; s++)
                {
                    if (softWhips[s] != null && !string.IsNullOrEmpty(softWhips[s].whipId))
                    {
                        swById[softWhips[s].whipId] = softWhips[s];
                    }
                }
                for (int s = 0; s < softWhips.Count; s++)
                {
                    SoftWhipDef lSw = softWhips[s];
                    if (lSw == null || !TryResolveRightCounterpartId(lSw.whipId, swById.ContainsKey, out string rWhipId))
                    {
                        continue;
                    }
                    SoftWhipDef rSw = swById[rWhipId];
                    if (Mathf.Abs(lSw.layerOffset - rSw.layerOffset) > 1e-4f)
                    {
                        yield return $"GiantRigDef '{defName}' bilateral SoftWhip layerOffset asymmetry between '{lSw.whipId}' ({lSw.layerOffset:F2}) and '{rSw.whipId}' ({rSw.layerOffset:F2}).";
                    }
                }
            }

            if (rollingTracks != null && rollingTracks.Count > 1)
            {
                var trById = new Dictionary<string, GiantRollingTrackDef>(StringComparer.Ordinal);
                for (int t = 0; t < rollingTracks.Count; t++)
                {
                    if (rollingTracks[t] != null && !string.IsNullOrEmpty(rollingTracks[t].trackId))
                    {
                        trById[rollingTracks[t].trackId] = rollingTracks[t];
                    }
                }
                for (int t = 0; t < rollingTracks.Count; t++)
                {
                    GiantRollingTrackDef lTr = rollingTracks[t];
                    if (lTr == null || !TryResolveRightCounterpartId(lTr.trackId, trById.ContainsKey, out string rTrackId))
                    {
                        continue;
                    }
                    GiantRollingTrackDef rTr = trById[rTrackId];
                    float lDorsalBelt = lTr.GetBeltLayerOffset(GiantBoneViewSector.Dorsal);
                    float rDorsalBelt = rTr.GetBeltLayerOffset(GiantBoneViewSector.Dorsal);
                    float lEastBelt = lTr.GetBeltLayerOffset(GiantBoneViewSector.East);
                    float rWestBelt = rTr.GetBeltLayerOffset(GiantBoneViewSector.West);
                    float lWestBelt = lTr.GetBeltLayerOffset(GiantBoneViewSector.West);
                    float rEastBelt = rTr.GetBeltLayerOffset(GiantBoneViewSector.East);
                    if (Mathf.Abs(lDorsalBelt - rDorsalBelt) > 1e-4f ||
                        Mathf.Abs(lEastBelt - rWestBelt) > 1e-4f ||
                        Mathf.Abs(lWestBelt - rEastBelt) > 1e-4f)
                    {
                        yield return $"GiantRigDef '{defName}' bilateral RollingTrack layerOffset asymmetry between '{lTr.trackId}' and '{rTr.trackId}'.";
                    }
                }
            }

            // 3. Active dorsal turret, weapon gimbal & barrel physical elevation rules
            bool hasAnyActiveTurret = false;
            var isWeaponOrTurretAssembly = new bool[ordered.Length];
            for (int i = ordered.Length - 1; i >= 0; i--)
            {
                BoneNodeDef b = ordered[i];
                if (!b.contributesToHitbox)
                {
                    continue;
                }
                if (b.isTurret)
                {
                    hasAnyActiveTurret = true;
                }
                bool inActiveTurret = false;
                for (int cur = i, guard = 0; cur >= 0 && cur < ordered.Length && guard < 64; cur = ordered[cur].parentRuntimeIndex, guard++)
                {
                    if (ordered[cur].isTurret && ordered[cur].contributesToHitbox)
                    {
                        inActiveTurret = true;
                        break;
                    }
                }
                bool hasDirectWeaponMuzzle = b.muzzlePixel.x >= 0f
                    || (b.emitterSockets != null && b.emitterSockets.Count > 0)
                    || (b.partAttacks != null && b.partAttacks.Count > 0);

                bool isDedicatedWeaponGimbal = false;
                if (i > 0 && string.IsNullOrEmpty(b.silhouetteTexPath))
                {
                    bool hasDeployedChild = false;
                    bool allDeployedChildrenWeapon = true;
                    for (int c = i + 1; c < ordered.Length; c++)
                    {
                        if (ordered[c].parentRuntimeIndex == i && ordered[c].contributesToHitbox)
                        {
                            hasDeployedChild = true;
                            if (!isWeaponOrTurretAssembly[c])
                            {
                                allDeployedChildrenWeapon = false;
                                break;
                            }
                        }
                    }
                    isDedicatedWeaponGimbal = hasDeployedChild && allDeployedChildrenWeapon;
                }

                isWeaponOrTurretAssembly[i] = inActiveTurret || hasDirectWeaponMuzzle || isDedicatedWeaponGimbal;
            }

            bool hasAnyOverheadRotor = false;
            for (int i = 0; i < ordered.Length; i++)
            {
                BoneNodeDef bCheck = ordered[i];
                if (bCheck.contributesToHitbox && Mathf.Abs(bCheck.continuousSpinDegPerTick) > 0.01f && !bCheck.isReactorCoreBone)
                {
                    hasAnyOverheadRotor = true;
                    break;
                }
            }

            if (hasAnyActiveTurret || hasAnyOverheadRotor)
            {
                float maxStructuralDeckLayer = float.MinValue;
                string maxStructuralBoneId = null;
                float maxLimitedArcTurretLayer = float.MinValue;
                string maxLimitedArcTurretId = null;
                float maxWeaponOrTurretLayer = float.MinValue;
                string maxWeaponOrTurretId = null;

                for (int i = 0; i < ordered.Length; i++)
                {
                    BoneNodeDef b = ordered[i];
                    if (!b.contributesToHitbox)
                    {
                        continue;
                    }
                    bool isOverheadRotor = Mathf.Abs(b.continuousSpinDegPerTick) > 0.01f && !b.isReactorCoreBone;
                    float dLayer = b.GetLayerOffset(GiantBoneViewSector.Dorsal);
                    if (!isWeaponOrTurretAssembly[i] && !isOverheadRotor && dLayer > maxStructuralDeckLayer)
                    {
                        maxStructuralDeckLayer = dLayer;
                        maxStructuralBoneId = b.nodeId;
                    }
                    if (isWeaponOrTurretAssembly[i] && !isOverheadRotor && dLayer > maxWeaponOrTurretLayer)
                    {
                        maxWeaponOrTurretLayer = dLayer;
                        maxWeaponOrTurretId = b.nodeId;
                    }
                    if (b.isTurret && b.maxTurretAimAngleDeg < 175f)
                    {
                        bool checkVentral = b.parentRuntimeIndex >= 0 &&
                                            dLayer <= ordered[b.parentRuntimeIndex].GetLayerOffset(GiantBoneViewSector.Dorsal) - minParentChildDelta + 1e-4f;
                        if (!checkVentral && dLayer > maxLimitedArcTurretLayer)
                        {
                            maxLimitedArcTurretLayer = dLayer;
                            maxLimitedArcTurretId = b.nodeId;
                        }
                    }
                }

                for (int i = 0; i < ordered.Length; i++)
                {
                    BoneNodeDef b = ordered[i];
                    if (!b.contributesToHitbox)
                    {
                        continue;
                    }
                    bool isOverheadRotor = Mathf.Abs(b.continuousSpinDegPerTick) > 0.01f && !b.isReactorCoreBone;
                    float dLayer = b.GetLayerOffset(GiantBoneViewSector.Dorsal);

                    if (isOverheadRotor)
                    {
                        if (maxStructuralBoneId != null && dLayer < maxStructuralDeckLayer + minParentChildDelta - 1e-4f)
                        {
                            yield return $"GiantRigDef '{defName}' overhead spinning rotor '{b.nodeId}' Dorsal layerOffset ({dLayer:F2}) must sit >= {minParentChildDelta:F2} above structural hull/wing '{maxStructuralBoneId}' ({maxStructuralDeckLayer:F2}).";
                        }
                        if (maxWeaponOrTurretId != null && dLayer < maxWeaponOrTurretLayer + minParentChildDelta - 1e-4f)
                        {
                            yield return $"GiantRigDef '{defName}' overhead spinning rotor '{b.nodeId}' Dorsal layerOffset ({dLayer:F2}) must sit >= {minParentChildDelta:F2} above turret/weapon '{maxWeaponOrTurretId}' ({maxWeaponOrTurretLayer:F2}).";
                        }
                        continue;
                    }

                    bool isVentralTurret = b.isTurret &&
                                           b.parentRuntimeIndex >= 0 &&
                                           dLayer <= ordered[b.parentRuntimeIndex].GetLayerOffset(GiantBoneViewSector.Dorsal) - minParentChildDelta + 1e-4f;

                    if (b.isTurret && !isVentralTurret && maxStructuralBoneId != null && dLayer < maxStructuralDeckLayer + minParentChildDelta - 1e-4f)
                    {
                        yield return $"GiantRigDef '{defName}' active dorsal turret '{b.nodeId}' Dorsal layerOffset ({dLayer:F2}) must sit >= {minParentChildDelta:F2} above highest structural hull/deck bone '{maxStructuralBoneId}' ({maxStructuralDeckLayer:F2}) so rotating across the hull never plunges beneath deck armor.";
                    }
                    else if (!b.isTurret && isWeaponOrTurretAssembly[i] && b.parentRuntimeIndex > 0 && !ordered[b.parentRuntimeIndex].isTurret &&
                             dLayer > ordered[b.parentRuntimeIndex].GetLayerOffset(GiantBoneViewSector.Dorsal) &&
                             maxStructuralBoneId != null && dLayer < maxStructuralDeckLayer + minParentChildDelta - 1e-4f)
                    {
                        yield return $"GiantRigDef '{defName}' deployed deck weapon/gimbal '{b.nodeId}' Dorsal layerOffset ({dLayer:F2}) must sit >= {minParentChildDelta:F2} above highest structural hull/deck bone '{maxStructuralBoneId}' ({maxStructuralDeckLayer:F2}).";
                    }

                    if (b.parentRuntimeIndex >= 0 && ordered[b.parentRuntimeIndex].isTurret)
                    {
                        BoneNodeDef tMount = ordered[b.parentRuntimeIndex];
                        float mLayer = tMount.GetLayerOffset(GiantBoneViewSector.Dorsal);
                        bool mountIsVentral = tMount.parentRuntimeIndex >= 0 &&
                                              mLayer <= ordered[tMount.parentRuntimeIndex].GetLayerOffset(GiantBoneViewSector.Dorsal) - minParentChildDelta + 1e-4f;
                        if (mountIsVentral)
                        {
                            float hullFloor = ordered[tMount.parentRuntimeIndex].GetLayerOffset(GiantBoneViewSector.Dorsal) - minParentChildDelta + 1e-4f;
                            if (dLayer > hullFloor)
                            {
                                yield return $"GiantRigDef '{defName}' ventral turret child '{b.nodeId}' Dorsal layerOffset ({dLayer:F2}) must remain strictly below parent hull '{ordered[tMount.parentRuntimeIndex].nodeId}' ({hullFloor:F2}).";
                            }
                        }
                        else
                        {
                            if (dLayer < mLayer + minParentChildDelta - 1e-4f)
                            {
                                yield return $"GiantRigDef '{defName}' turret child '{b.nodeId}' Dorsal layerOffset ({dLayer:F2}) must sit strictly above its dorsal turret mount '{tMount.nodeId}' ({mLayer:F2} + {minParentChildDelta:F2}).";
                            }

                            if (tMount.maxTurretAimAngleDeg >= 175f && b.drawSize.y >= 3.5f &&
                                maxLimitedArcTurretId != null && dLayer <= maxLimitedArcTurretLayer + 1e-4f)
                            {
                                yield return $"GiantRigDef '{defName}' 360-degree long barrel '{b.nodeId}' ({dLayer:F2}) must sit strictly above limited-arc superstructure '{maxLimitedArcTurretId}' ({maxLimitedArcTurretLayer:F2}) to prevent asymmetric barrel-under-bridge clipping.";
                            }
                        }
                    }
                }
            }

            // 4. Grounded locomotion drive tracks & walking limb proximal-over-distal physical ordering
            for (int i = 0; i < ordered.Length; i++)
            {
                BoneNodeDef b = ordered[i];
                if (b.parentRuntimeIndex < 0)
                {
                    continue;
                }
                BoneNodeDef parent = ordered[b.parentRuntimeIndex];
                float bLayer = b.GetLayerOffset(GiantBoneViewSector.Dorsal);
                float pLayer = parent.GetLayerOffset(GiantBoneViewSector.Dorsal);

                if (b.isLocomotionDrivePart && !parent.isLocomotionDrivePart && bLayer > pLayer - minParentChildDelta + 1e-4f)
                {
                    yield return $"GiantRigDef '{defName}' locomotion drive part '{b.nodeId}' Dorsal layerOffset ({bLayer:F2}) must sit strictly below chassis '{parent.nodeId}' ({pLayer:F2} - {minParentChildDelta:F2}).";
                }
                if (IsGroundedWalkingLimbId(b.nodeId) && bLayer > pLayer - minParentChildDelta + 1e-4f)
                {
                    yield return $"GiantRigDef '{defName}' walking limb bone '{b.nodeId}' Dorsal layerOffset ({bLayer:F2}) must descend strictly below parent '{parent.nodeId}' ({pLayer:F2} - {minParentChildDelta:F2}).";
                }
            }

            // 5. Global silhouette underlay floor & SkinnedMesh glow ordering
            float minRenderedLayer = float.MaxValue;
            string minRenderedId = null;
            for (int i = 0; i < ordered.Length; i++)
            {
                if (ordered[i].isRenderedBySkinnedMesh)
                {
                    continue;
                }
                float dLayer = ordered[i].GetLayerOffset(GiantBoneViewSector.Dorsal);
                if (dLayer < minRenderedLayer)
                {
                    minRenderedLayer = dLayer;
                    minRenderedId = ordered[i].nodeId;
                }
            }
            if (skinnedMeshes != null)
            {
                for (int m = 0; m < skinnedMeshes.Count; m++)
                {
                    GiantSkinnedMeshDef sm = skinnedMeshes[m];
                    if (sm != null && sm.layerOffset < minRenderedLayer)
                    {
                        minRenderedLayer = sm.layerOffset;
                        minRenderedId = sm.meshId;
                    }
                }
            }

            if (minRenderedId != null)
            {
                float maxAllowedSilhouette = minRenderedLayer - (minParentChildDelta - 0.01f);
                for (int i = 0; i < ordered.Length; i++)
                {
                    BoneNodeDef sNode = ordered[i];
                    if (!string.IsNullOrEmpty(sNode.silhouetteTexPath) && sNode.silhouetteLayerOffset > maxAllowedSilhouette)
                    {
                        yield return $"Bone '{sNode.nodeId}' in GiantRigDef '{defName}' defines silhouetteTexPath '{sNode.silhouetteTexPath}' with silhouetteLayerOffset ({sNode.silhouetteLayerOffset:F2}) > global floor {maxAllowedSilhouette:F2} (lowest rendered part '{minRenderedId}'={minRenderedLayer:F2}). Silhouette underlays must sit strictly below the entire rig's lowest structural/limb layer.";
                    }
                }
                if (skinnedMeshes != null)
                {
                    for (int m = 0; m < skinnedMeshes.Count; m++)
                    {
                        GiantSkinnedMeshDef sm = skinnedMeshes[m];
                        if (sm == null)
                        {
                            continue;
                        }
                        if (!string.IsNullOrEmpty(sm.silhouetteTexPath) && sm.silhouetteLayerOffset > maxAllowedSilhouette)
                        {
                            yield return $"SkinnedMesh '{sm.meshId}' in GiantRigDef '{defName}' defines silhouetteTexPath with silhouetteLayerOffset ({sm.silhouetteLayerOffset:F2}) > global floor {maxAllowedSilhouette:F2} (lowest rendered part '{minRenderedId}'={minRenderedLayer:F2}).";
                        }
                        if (!string.IsNullOrEmpty(sm.glowTexPath))
                        {
                            if (sm.glowLayerOffset <= 0.005f)
                            {
                                yield return $"SkinnedMesh '{sm.meshId}' in GiantRigDef '{defName}' defines glowTexPath with non-positive glowLayerOffset ({sm.glowLayerOffset:F3}).";
                            }
                            float glowWorldLayer = sm.layerOffset + sm.glowLayerOffset;
                            for (int k = 0; k < skinnedMeshes.Count; k++)
                            {
                                GiantSkinnedMeshDef other = skinnedMeshes[k];
                                if (other != null && other.layerOffset > sm.layerOffset + 1e-4f && glowWorldLayer >= other.layerOffset - 0.01f)
                                {
                                    yield return $"SkinnedMesh '{sm.meshId}' glow overlay ({glowWorldLayer:F2}) bleeds into higher SkinnedMesh '{other.meshId}' ({other.layerOffset:F2}).";
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
