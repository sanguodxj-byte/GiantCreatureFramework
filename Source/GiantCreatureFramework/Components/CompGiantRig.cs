using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    public class CompProperties_GiantRig : CompProperties
    {
        public GiantRigDef rigDef;

        public CompProperties_GiantRig()
        {
            compClass = typeof(CompGiantRig);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string err in base.ConfigErrors(parentDef))
            {
                yield return err;
            }
            if (rigDef == null)
            {
                yield return $"CompProperties_GiantRig on '{parentDef?.defName}' has null rigDef.";
            }
            else if (parentDef == null || !parentDef.HasComp(typeof(CompGiantPhaseTransform)))
            {
                foreach (string combatErr in rigDef.ValidateThingDefCombatAnimations(parentDef))
                {
                    yield return combatErr;
                }
            }
        }
    }

    public enum GiantAnimState
    {
        Idle,
        Walk,
        Run,
        AttackOnce,
        MorphLocked,
        Downed,
        ManualPreview,
        Death,
        FlightEntry,
        TurnInPlace
    }

    /// <summary>
    /// Immutable snapshot published from main thread so ParallelPreDraw worker threads can compute
    /// exact interpolated world matrices with zero locking and zero Unity API access.
    /// </summary>
    public sealed class GiantRenderSnapshot
    {
        public uint version;
        public float headingDeg;
        public BoneLocalPose[] localPoses;
        public Vector2[] turretRecoilOffsets;
        public bool[] missingMask;
        public float[] partHealthPct;
        public GiantBoneViewSector[] activeSectors;
        public SoftWhipRibbonSnapshot[] softWhips;
        public float[] trackScrollCells;
        public bool[] skinnedMeshMissingMask;
        public bool hasMechShaderParams;
        public Color phaseGlowColor;
        public Vector4 mechShaderParams;
        public bool isDead;
        public bool isDowned;
        public bool isFlyingEntry;
        public Vector2 flightVisualOffsetXZ;
        public float flightAltitudeBoost;
    }

    [StaticConstructorOnStartup]
    internal static class GiantRigGizmoAssets
    {
        public static readonly Texture2D GizmoIcon =
            ContentFinder<Texture2D>.Get("UI/Icons/GCF_DebugGizmo", reportFailure: false) ?? BaseContent.BadTex;
    }

    /// <summary>
    /// Owns shared rig state and coordinates the ThingComp lifecycle and per-tick subsystem order.
    /// Domain implementations live in CompGiantRig.*.cs; see Docs/CompGiantRig_Architecture.md.
    /// </summary>
    public partial class CompGiantRig : ThingComp
    {        public CompProperties_GiantRig Props => (CompProperties_GiantRig)props;
        private GiantRigDef overrideRigDef;
        public GiantRigDef RigDef => overrideRigDef ?? Props.rigDef;
        public Pawn Pawn => parent as Pawn;
        public GiantRenderSnapshot LatestSnapshot => latestSnapshot;
        private uint snapshotPublishVersion;

        private float currentHeadingDeg;
        private float targetHeadingDeg;
        private bool headingInitialized;

        private GiantAnimState animState = GiantAnimState.Idle;
        private GiantAnimDef activeOneShotAnim;
        private float oneShotElapsedTicks;
        private bool isWarmupWindup;
        private int lastOneShotActivateTick = -999999;
        private float warmupPeakTicksLeft;
        private float walkCyclePhaseTicks;
        private float turnCyclePhaseTicks;
        private float lastStepHeadingDeltaDeg;
        private int turnInPlaceGraceTicks;
        private int lastTurnDirectionSign;
        private float continuousSpinTicks;
        private CompGiantPhaseTransform cachedPhaseComp;
        private IGiantShaderSnapshotComp cachedExtraShaderComp;
        private bool phaseCompResolved;
        private Vector3 lastDrawPos;
        private bool hasLastDrawPos;
        private bool hasAttackTargetWorldXZ;
        private Vector2 attackTargetWorldXZ;

        // Cross-fade blending state
        private GiantAnimDef currentClip;
        private BoneLocalPose[] blendFromPoses;
        private float blendElapsedTicks;
        private float blendDurationTicks;
        private bool deathAnimCompleted;
        private bool deathTickZeroEventsEvaluated;
        private int deathStartedGameTick = -999;
        private int postDeathSettleTicks;

        // Flight entry state (swooping in from aloft and folding wings on touchdown)
        private bool flightEntryActive;
        private bool flightEntryCompleted;
        private int flightEntryElapsedTicks;
        private int flightEntryDurationTicks;
        private Vector2 flightStartXZ;
        private Vector2 flightEndXZ;
        private float flightStartAltitudeOffset;
        private float flightCurrentHeightOffset;
        private float flightHeadingDeg;
        private Vector2 flightCurrentGroundXZ;
        private float flightBlendRemainingTicks;
        private bool lastObservedDrafted;
        private bool draftedStateInitialized;

        public bool IsFlyingEntry => flightEntryActive;
        public int FlightEntryElapsedTicks => flightEntryElapsedTicks;
        public GiantAnimState AnimState => animState;
        public string ActiveOneShotAnimDefName => activeOneShotAnim?.defName;

        // Manual overrides for Inspector window & independent subsystem bone offsets (e.g. per-bay hangar shutters)
        public bool ManualOverrideActive;
        public float[] ManualBoneAngleOffsets;
        private float[] proceduralBoneAngleOffsets;
        private TurretRecoilState[] turretRecoilStates;
        private Vector2[] currentTurretRecoilOffsets;
        private bool anyTurretRecoilActive;

        // Working buffers
        private BoneLocalPose[] evalPoses;
        private BoneLocalPose[] turnBlendPoses;
        private BoneLocalPose[] finalLocalPoses;
        private BoneLocalPose[] preSteeringLocalPoses;
        private BoneWorldPose[] worldPoses;
        private bool[] partMissingMask;
        private float[] partHealthPct;
        private BodyPartRecord[] linkedBodyParts;
        private HashSet<BodyPartRecord> allLinkedBodyPartSet;
        private TextureAlphaMask[] alphaMasks;
        private TextureAlphaMask[] alphaMasksEast;
        private TextureAlphaMask[] alphaMasksWest;
        private TextureAlphaMask[] activeAlphaMasks;
        private TextureAlphaMask[] silhouetteAlphaMasks;
        private GiantBoneViewSector[] activeBoneSectors;
        private bool sectorsInitialized;
        private bool anySectorChangedPendingFootprint;
        private sealed class CachedRigRenderSubtree
        {
            public PawnRenderTree Tree;
            public List<PawnRenderNode> NodesList;
            public PawnRenderNode[] ChildrenArray;
            public List<PawnRenderNode>[] AncestorChains;
            public PawnRenderNode BodyTagNode;
            public List<PawnRenderNode_GiantSoftWhip> SoftWhipNodes;
            public List<PawnRenderNode_GiantRollingTrack> RollingTrackNodes;
            public List<PawnRenderNode_GiantTrackWheel> TrackWheelNodes;
            public List<PawnRenderNode_GiantSkinnedMesh> SkinnedMeshPrimaryNodes;
            public List<PawnRenderNode_GiantSkinnedMesh> AllSkinnedMeshNodes;
            public List<int> AllSkinnedMeshIndices;
            public bool InitializedOnMainThread;
        }

        private static readonly System.Reflection.FieldInfo RenderTreeNodeAncestorsField =
            HarmonyLib.AccessTools.Field(typeof(PawnRenderTree), "nodeAncestors");

        private Dictionary<GiantRigDef, CachedRigRenderSubtree> cachedRigSubtrees;
        public int InPlaceSubtreeSwapCount { get; private set; }
        public int CachedStageSubtreeCount => cachedRigSubtrees?.Count ?? 0;

        private List<PawnRenderNode> createdRenderNodes;
        private DampedBezierChainRuntime[] bezierRuntimes;
        private SoftWhipRuntime[] softWhipRuntimes;
        private SoftWhipRibbonSnapshot[] softWhipSnapshots;
        private TextureAlphaMask[] softWhipAlphaMasks;
        private BodyPartRecord[] linkedSoftWhipTendrilParts;
        private bool[] softWhipTendrilMissingMask;
        private List<PawnRenderNode_GiantSoftWhip> createdSoftWhipNodes;
        private float[] trackScrollCells;
        private float trackPrevHeadingDeg;
        private float trackPrevRootOffsetY;
        private bool trackKinematicsInitialized;
        private float pendingTrackWorldFwdDelta;
        private List<PawnRenderNode_GiantRollingTrack> createdRollingTrackNodes;
        private List<PawnRenderNode_GiantTrackWheel> createdTrackWheelNodes;
        private SkinnedMeshRuntime[] skinnedMeshRuntimes;
        internal ReadOnlySpan<SkinnedMeshRuntime> SkinnedMeshRuntimesSpan => skinnedMeshRuntimes;
        internal float CurrentFlightAltitude01 => flightEntryActive
            ? Mathf.Clamp01(flightCurrentHeightOffset / Mathf.Max(0.1f, flightStartAltitudeOffset))
            : (flightLocomotion != null && Pawn != null && !Pawn.Downed ? flightLocomotion.CurrentAltitude01 : 0f);
        private bool[] prevMissingMaskScratch;
        private BoneWorldPose[] preSeverPosesScratch;
        private readonly List<int> newlyMissingBonesScratch = new List<int>(12);
        private TextureAlphaMask[] skinnedMeshAlphaMasks;
        private BodyPartRecord[] linkedSkinnedMeshParts;
        private bool[] skinnedMeshMissingMask;
        private List<PawnRenderNode_GiantSkinnedMesh> createdSkinnedMeshPrimaryNodes;
        internal IReadOnlyList<PawnRenderNode_GiantSkinnedMesh> CreatedSkinnedMeshPrimaryNodes => createdSkinnedMeshPrimaryNodes;
        private uint lastSkinnedMeshMissingBits = 0U;
        private int[] emitterBarrelCursors;
        private readonly List<GiantMuzzleShotInfo> tempEmitterShots = new List<GiantMuzzleShotInfo>(8);

        public const int PartDamageFlashTicksTotal = 16;
        private int[] boneLastDamageTick;
        private int[] softWhipLastDamageTick;
        private float[] softWhipHealthPct;
        private int pendingRoutedHitBoneIndex = -1;
        private volatile int latestPublishedGameTick;

        private const int SnapshotRingSize = 4;
        private GiantRenderSnapshot[] snapshotRing;
        private int snapshotRingCursor;

        private volatile GiantRenderSnapshot latestSnapshot;
        private readonly GiantFootprintData footprint = new GiantFootprintData();
        private BoneWorldPose[] shadowPoseScratch;

        private bool healthStateDirty = true;
        private int lastCheckedHediffCount = -1;
        private int lastHealthCheckTick = -999;
        private readonly List<GiantWoundDecalSpec> cachedWoundDecalSpecs = new List<GiantWoundDecalSpec>(24);
        private ulong lastWoundHediffSignature = ulong.MaxValue;
        private float cachedMaxHpHealthScale = -1f;
        private float[] cachedBoneMaxHp;
        private float[] cachedWhipMaxHp;
        private float[] lastUploadedTrackScroll;
        private GiantBoneViewSector[] lastUploadedTrackSector;
        private bool pendingDynamicMeshUpload;
        private bool initialDynamicMeshUploaded;
        private uint localPoseSerial = 1u;
        private uint worldPosesSolvedSerial;
        private Vector2 worldPosesSolvedRootXZ;
        private float worldPosesSolvedHeading = float.NaN;
        private GiantRigDef worldPosesSolvedRigDef;
        private bool anyDamagedBoneBelow65;
        private bool alphaMasksLoaded;
        private GiantRigDef alphaMasksLoadedRigDef;
        private GiantRigDef buffersInitializedRigDef;

        private IntVec3 lastFootprintCell = IntVec3.Invalid;
        private Vector2 lastFootprintWorldXZ = new Vector2(float.NaN, float.NaN);
        private float lastFootprintHeading = -999f;
        private int lastFootprintTick = -999;
        private bool[] lastMissingMaskSnapshot;
        private uint lastSoftWhipMissingBits = 0U;
        private ulong lastHealthBucketHash = 0UL;
        private bool missingMaskInitialized = false;
        private bool suppressCascadeReentry = false;
        private Map lastSpawnedMap;
        private bool rushActive;
        private int lastCrushPulseTick = -999;

        private GiantSteeringRuntime steeringRuntime;
        private GiantHitReactionRuntime hitReactionRuntime;
        private VehicleLocomotionRuntime vehicleLocomotion;
        private GiantFlightRuntime flightLocomotion;
        private FlightVehicleLocomotionRuntime flightVehicleLocomotion;
        private GiantAquaticRuntime aquaticLocomotion;
        private GiantPartCombatRuntime partCombat;
        private GiantSteeringContext currentSteeringCtx;
        private float continuousSteeringTicks;
        private float lastFootprintSpineBend;
        private float lastFootprintSleepCurl;
        private float sleepCurlProgress01;
        private float sleepStartHeadingDeg;
        private float sleepCurlSideSign = 1f;
        private float sleepIdleClockTicks;
        private float sleepBreathTicks;
        private bool wasBiologicalSleeping;
        private float dailyIngestWeight01;
        private float dailySocialWeight01;
        private float dailyActivityPhaseTicks;
        private GiantDailyActivityKind currentDailyActivity;
        private int socialAffectionLingerUntilTick = -99999;
        private int lastNuzzleArmedTick = -99999;
        private Pawn lastSocialFocusPawn;
        private int nextExternalTrainerScanTick = -99999;
        private Pawn cachedExternalTrainerPawn;
        private int movingPauseGraceTicks;
        private bool isPivotTurningInPlace;
        private bool isTurnInPlaceAnimLatched;
        private bool hasPivotDesiredBearing;
        private float pivotDesiredBearingDeg;
        private float lastSampledPathRemainingCells;
        private Thing lockedLookTargetRef;
        private int lockedLookTargetUntilTick = -999;
        private float smoothedDesiredLookDeg;
        private bool smoothedLookInitialized;
        private float idleVigilanceOffsetDeg;
        private float idleVigilanceRemainingTicks = 120f;
        private bool idleVigilanceInGlance;
        private int idleVigilanceStepCounter;

        public const int ProvokedRetaliationDurationTicks = 2500;
        private readonly HashSet<int> provokedInstigatorIds = new HashSet<int>();
        private Thing lastProvokedInstigator;
        private int provokedUntilTick = -99999;
        public bool DebugDrawFootprint = false;
        public bool DebugDrawSkeleton = false;
        public bool DebugDrawOnSelect = false;

        public GiantFootprintData Footprint => footprint;
        public Map FootprintMap => Pawn?.Map ?? Pawn?.Corpse?.Map ?? lastSpawnedMap;
        public float CurrentHeadingDeg => currentHeadingDeg;
        public bool HeadingInitialized => headingInitialized;
        public float LastStepHeadingDeltaDeg => lastStepHeadingDeltaDeg;
        public float TurnCyclePhaseTicks => turnCyclePhaseTicks;
        public GiantAnimState CurrentAnimState => animState;
        public GiantAnimDef ActiveClip => currentClip;
        public bool IsRushing => rushActive;
        public float CurrentIdleVigilanceOffsetDeg => idleVigilanceOffsetDeg;
        public float SleepCurlProgress01 => sleepCurlProgress01;
        public float SleepCurlSideSign => sleepCurlSideSign;
        public bool IsBiologicalSleepCurling => wasBiologicalSleeping || sleepCurlProgress01 > 0.001f;
        public float DailyIngestWeight01 => dailyIngestWeight01;
        public float DailySocialWeight01 => dailySocialWeight01;
        public float DailyActivityPhaseTicks => dailyActivityPhaseTicks;
        public GiantDailyActivityKind CurrentDailyActivity => currentDailyActivity;
        public bool HasAnyActiveTurretRecoil => anyTurretRecoilActive;
        public ReadOnlySpan<TurretRecoilState> TurretRecoilStatesSpan => turretRecoilStates ?? Array.Empty<TurretRecoilState>();
        public GiantSteeringRuntime SteeringRuntime => steeringRuntime;
        public VehicleLocomotionRuntime VehicleLocomotion => vehicleLocomotion;
        public GiantFlightRuntime FlightLocomotion => flightLocomotion;
        public FlightVehicleLocomotionRuntime FlightVehicleLocomotion => flightVehicleLocomotion;
        public GiantAquaticRuntime AquaticLocomotion => aquaticLocomotion;
        public bool IsCurrentlyInWater =>
            aquaticLocomotion != null
            && Pawn != null
            && !IsCurrentlyAirborne
            && aquaticLocomotion.IsCurrentlyInWater;
        public bool IsWaterOnlySurfaceBoundNow =>
            aquaticLocomotion != null
            && aquaticLocomotion.Def.IsWaterOnly
            && !IsAirborneOrTakingOff;
        public float GetEffectiveAquaticSpeedMultiplier() =>
            aquaticLocomotion != null && !IsCurrentlyAirborne
                ? aquaticLocomotion.ComputeEffectiveSurfaceSpeedMultiplier()
                : 1f;
        public bool IsCurrentlyAirborne =>
            flightLocomotion != null
            && Pawn != null
            && (Pawn.health == null || (!Pawn.Dead && !Pawn.Downed))
            && flightLocomotion.IsCurrentlyAirborne;
        public bool IsAirborneOrTakingOff =>
            flightLocomotion != null
            && Pawn != null
            && (Pawn.health == null || (!Pawn.Dead && !Pawn.Downed))
            && flightLocomotion.IsAirborneOrTakingOff;
        public bool IsImmuneToGroundMeleeNow =>
            flightLocomotion != null
            && Pawn != null
            && (Pawn.health == null || (!Pawn.Dead && !Pawn.Downed))
            && flightLocomotion.IsImmuneToGroundMelee;
        public Vector2 CurrentFlightVisualOffsetXZ =>
            flightLocomotion != null ? flightLocomotion.ComputeVisualOffsetXZ() : Vector2.zero;
        public float GetEffectiveFlightSpeedMultiplier() =>
            flightLocomotion != null ? flightLocomotion.EffectiveFlightSpeedMultiplier : 1f;
        public void GetLivePartMaskAndHealth(out ReadOnlySpan<bool> missingSpan, out ReadOnlySpan<float> healthSpan)
        {
            missingSpan = partMissingMask != null ? partMissingMask : ReadOnlySpan<bool>.Empty;
            healthSpan = partHealthPct != null ? partHealthPct : ReadOnlySpan<float>.Empty;
        }
        public bool HasAnyOperationalTurretBone() =>
            RigDef != null && RigDef.HasAnyIntactTurretBone(IsBoneIntact, out _);
        public GiantPartCombatRuntime PartCombat
        {
            get
            {
                if (partCombat == null && RigDef != null)
                {
                    partCombat = new GiantPartCombatRuntime(this);
                }
                return partCombat;
            }
        }

        public bool TryGetActiveActionClipAndTick(out string clipDefName, out float sampleTick)
        {
            string oneShot = ActiveOneShotAnimDefName;
            if (!string.IsNullOrEmpty(oneShot))
            {
                clipDefName = oneShot;
                sampleTick = ActiveOneShotElapsedTicks;
                return true;
            }
            if (partCombat != null && partCombat.TryGetActiveClipAndSampleTick(out clipDefName, out sampleTick))
            {
                return true;
            }
            clipDefName = string.Empty;
            sampleTick = 0f;
            return false;
        }
        public int DeathStartedGameTick => deathStartedGameTick;
        public bool IsDeathAnimPlaying => animState == GiantAnimState.Death && !deathAnimCompleted && currentClip != null;
        public bool IsDeathAnimCompleted => deathAnimCompleted;
        public float DeathAnimElapsedTicks => animState == GiantAnimState.Death ? oneShotElapsedTicks : 0f;
        public float DeathAnimNormalizedProgress =>
            animState == GiantAnimState.Death && currentClip != null && currentClip.durationTicks > 0.01f
                ? Mathf.Clamp01(oneShotElapsedTicks / currentClip.durationTicks)
                : (deathAnimCompleted ? 1f : 0f);

        public string GetSteeringDebugSummary()
        {
            if (steeringRuntime == null || !steeringRuntime.HasAnySteeringParts)
            {
                return "steer=none";
            }
            return $"steer[lead={steeringRuntime.PrimaryLookLeadDeg:F1},bend={steeringRuntime.PrimarySpineBendDeg:F1},yaw={steeringRuntime.SmoothedYawRateDegPerTick:F2}]";
        }
        public GiantHitReactionRuntime HitReactionRuntime => hitReactionRuntime;
        public bool IsAnimalHitReactionEligible
        {
            get
            {
                if (RigDef == null || Pawn == null)
                {
                    return false;
                }
                if (RigDef.hitReaction != null && !RigDef.hitReaction.enabled)
                {
                    return false;
                }
                if (RigDef.hitReaction != null && RigDef.hitReaction.forceEnable)
                {
                    return true;
                }
                if (RigDef.ActiveVehicleLocomotion != null || RigDef.ActiveFlightVehicleLocomotion != null)
                {
                    return false;
                }
                if (GiantVehicleBehaviorGuard.IsVehiclePawn(Pawn, out _, out _))
                {
                    return false;
                }
                return GiantWoundBloodSystem.IsOrganicFleshGiant(Pawn);
            }
        }
        public bool IsPivotTurningInPlace => isPivotTurningInPlace;
        public bool IsActivelyLocomoting =>
            Pawn != null
            && Pawn.Spawned
            && !isPivotTurningInPlace
            && Pawn.pather != null
            && Pawn.pather.MovingNow
            && (Pawn.stances == null || !Pawn.stances.FullBodyBusy);
        private bool IsPatherMovingNotBusy =>
            Pawn != null
            && Pawn.Spawned
            && !Pawn.Downed
            && !Pawn.Dead
            && Pawn.pather != null
            && Pawn.pather.Moving
            && (Pawn.stances == null || !Pawn.stances.FullBodyBusy);
        private bool IsLocomotingWithGrace =>
            !isPivotTurningInPlace && (IsActivelyLocomoting || (IsPatherMovingNotBusy && movingPauseGraceTicks > 0));
        public bool RushWhenEnemyFarRuntimeOverrideDisable;
        public bool IsDistantRushAllowed =>
            RigDef != null
            && RigDef.rushWhenEnemyFar
            && !RushWhenEnemyFarRuntimeOverrideDisable
            && (vehicleLocomotion == null || !vehicleLocomotion.Def.suppressBiologicalGait)
            && (flightVehicleLocomotion == null || !flightVehicleLocomotion.Def.suppressBiologicalGait);
        public ReadOnlySpan<TextureAlphaMask> AlphaMasksSpan => activeAlphaMasks ?? alphaMasks ?? Array.Empty<TextureAlphaMask>();
        public ReadOnlySpan<TextureAlphaMask> SilhouetteAlphaMasksSpan => silhouetteAlphaMasks ?? Array.Empty<TextureAlphaMask>();
        public ReadOnlySpan<TextureAlphaMask> SoftWhipAlphaMasksSpan => softWhipAlphaMasks ?? Array.Empty<TextureAlphaMask>();

        public string GetSoftWhipDebugSummary()
        {
            if (RigDef?.softWhips == null || RigDef.softWhips.Count == 0)
            {
                return "none";
            }

            var sb = new System.Text.StringBuilder(128);
            for (int s = 0; s < RigDef.softWhips.Count; s++)
            {
                if (s > 0)
                {
                    sb.Append(';');
                }
                SoftWhipDef wDef = RigDef.softWhips[s];
                string whipId = wDef?.whipId ?? $"whip{s}";
                bool snapVis = softWhipSnapshots != null && s < softWhipSnapshots.Length && softWhipSnapshots[s] != null && softWhipSnapshots[s].visible;
                bool hasMesh = createdSoftWhipNodes != null && s < createdSoftWhipNodes.Count && createdSoftWhipNodes[s] != null && createdSoftWhipNodes[s].HasValidMesh;
                string tipStr = "uninit";
                SoftWhipRuntime rt = softWhipRuntimes != null && s < softWhipRuntimes.Length ? softWhipRuntimes[s] : null;
                if (rt != null && rt.IsInitialized)
                {
                    ReadOnlySpan<Vector2> pts = rt.CenterlineWorldXZ;
                    if (pts.Length > 0)
                    {
                        Vector2 tip = pts[pts.Length - 1];
                        tipStr = $"({tip.x:F1},{tip.y:F1})";
                    }
                }
                sb.Append($"{whipId}[vis={snapVis},mesh={hasMesh},tip={tipStr}]");
            }
            return sb.ToString();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Defs.Look(ref overrideRigDef, "gcf_overrideRigDef");
            EnsureBuffersInitialized();
            Scribe_Values.Look(ref currentHeadingDeg, "gcf_headingDeg", 0f);
            Scribe_Values.Look(ref targetHeadingDeg, "gcf_targetHeadingDeg", 0f);
            Scribe_Values.Look(ref headingInitialized, "gcf_headingInit", defaultValue: false);
            Scribe_Values.Look(ref walkCyclePhaseTicks, "gcf_walkPhase", 0f);
            Scribe_Values.Look(ref continuousSpinTicks, "gcf_spinTicks", 0f);
            Scribe_Values.Look(ref rushActive, "gcf_rushActive", defaultValue: false);
            Scribe_Values.Look(ref animState, "gcf_animState", GiantAnimState.Idle);
            Scribe_Values.Look(ref oneShotElapsedTicks, "gcf_oneShotElapsed", 0f);
            Scribe_Values.Look(ref deathAnimCompleted, "gcf_deathAnimDone", defaultValue: false);
            Scribe_Values.Look(ref flightEntryActive, "gcf_flightEntryActive", defaultValue: false);
            Scribe_Values.Look(ref flightEntryCompleted, "gcf_flightEntryDone", defaultValue: false);
            Scribe_Values.Look(ref flightEntryElapsedTicks, "gcf_flightEntryElapsed", 0);
            Scribe_Values.Look(ref flightEntryDurationTicks, "gcf_flightEntryDur", 0);
            Scribe_Values.Look(ref flightStartXZ, "gcf_flightStartXZ", Vector2.zero);
            Scribe_Values.Look(ref flightEndXZ, "gcf_flightEndXZ", Vector2.zero);
            Scribe_Values.Look(ref flightStartAltitudeOffset, "gcf_flightStartAlt", 0f);
            Scribe_Values.Look(ref flightCurrentHeightOffset, "gcf_flightCurAlt", 0f);
            Scribe_Values.Look(ref flightHeadingDeg, "gcf_flightHeading", 0f);
            Scribe_Values.Look(ref flightCurrentGroundXZ, "gcf_flightCurGroundXZ", Vector2.zero);
            Scribe_Values.Look(ref sleepCurlProgress01, "gcf_sleepCurl01", 0f);
            Scribe_Values.Look(ref sleepStartHeadingDeg, "gcf_sleepStartHeading", 0f);
            Scribe_Values.Look(ref sleepCurlSideSign, "gcf_sleepCurlSign", 1f);
            Scribe_Values.Look(ref wasBiologicalSleeping, "gcf_wasBioSleeping", defaultValue: false);
            Scribe_Values.Look(ref dailyIngestWeight01, "gcf_dailyIngest01", 0f);
            Scribe_Values.Look(ref dailySocialWeight01, "gcf_dailySocial01", 0f);
            Scribe_Values.Look(ref dailyActivityPhaseTicks, "gcf_dailyPhaseTicks", 0f);
            PartCombat?.ExposeData();
            if (RigDef?.ActiveVehicleLocomotion != null)
            {
                EnsureBuffersInitialized();
                vehicleLocomotion?.ExposeData();
            }
            if (RigDef?.ActiveFlightLocomotion != null)
            {
                EnsureBuffersInitialized();
                flightLocomotion?.ExposeData();
            }
            if (RigDef?.ActiveFlightVehicleLocomotion != null)
            {
                EnsureBuffersInitialized();
                flightVehicleLocomotion?.ExposeData();
            }
            if (RigDef?.ActiveAquaticLocomotion != null)
            {
                EnsureBuffersInitialized();
                aquaticLocomotion?.ExposeData();
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit && Pawn != null && !Pawn.Dead)
            {
                GiantCombatMechanicsUtility.EnsureGiantDeathOnDownedSuppressed(Pawn, this);
                if (flightEntryActive && RigDef?.flightEntry != null)
                {
                    EnsureBuffersInitialized();
                    currentClip = RigDef.flightEntry.flightAnim;
                    animState = GiantAnimState.FlightEntry;
                }
                else if ((animState == GiantAnimState.ManualPreview || animState == GiantAnimState.AttackOnce) && activeOneShotAnim == null)
                {
                    animState = GiantAnimState.Idle;
                    oneShotElapsedTicks = 0f;
                    currentClip = RigDef?.idleAnim;
                }
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit && Pawn != null && Pawn.Dead && RigDef != null)
            {
                EnsureBuffersInitialized();
                GiantAnimDef deathClip = RigDef.deathAnim ?? RigDef.downedAnim;
                currentClip = deathClip;
                activeOneShotAnim = deathClip;
                animState = GiantAnimState.Death;
                blendDurationTicks = 0f;
                blendElapsedTicks = 0f;
                if (deathClip == null)
                {
                    deathAnimCompleted = true;
                }
                else if (deathAnimCompleted || oneShotElapsedTicks >= deathClip.durationTicks - 1e-4f)
                {
                    oneShotElapsedTicks = deathClip.durationTicks;
                    deathAnimCompleted = true;
                }
            }
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            GiantFastLookup.Register(Pawn, this);
            EnsureBuffersInitialized();
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            worldSimulationActive = false;
            GiantFastLookup.Register(Pawn, this);
            GiantPlayerControlUtility.EnsureController(Pawn);
            EnsureBuffersInitialized();
            ResolveBodyPartLinks();
            GiantCombatMechanicsUtility.EnsureGiantDeathOnDownedSuppressed(Pawn, this);
            if (Pawn?.equipment != null && Pawn.equipment.HasAnything())
            {
                Pawn.equipment.DestroyAllEquipment();
            }

            if (!headingInitialized && Pawn != null)
            {
                currentHeadingDeg = Pawn.Rotation.AsAngle;
                targetHeadingDeg = currentHeadingDeg;
                headingInitialized = true;
            }
            if (vehicleLocomotion != null && Pawn != null && !respawningAfterLoad)
            {
                Vector3 sp = Pawn.Position.ToVector3Shifted();
                vehicleLocomotion.Reset(currentHeadingDeg, new Vector2(sp.x, sp.z), hasValidPos: true);
            }
            if (flightLocomotion != null && Pawn != null && !respawningAfterLoad)
            {
                flightLocomotion.ResetForSpawn(Pawn.thingIDNumber);
            }
            if (flightVehicleLocomotion != null && Pawn != null && !respawningAfterLoad)
            {
                Vector3 sp = Pawn.Position.ToVector3Shifted();
                bool startAir = flightLocomotion == null || flightLocomotion.IsCurrentlyAirborne;
                flightVehicleLocomotion.Reset(currentHeadingDeg, new Vector2(sp.x, sp.z), hasValidPos: true, startAirborne: startAir);
            }
            if (aquaticLocomotion != null && Pawn != null)
            {
                if (!respawningAfterLoad)
                {
                    aquaticLocomotion.ResetForSpawn(Pawn);
                }
                else
                {
                    GetLivePartMaskAndHealth(out ReadOnlySpan<bool> initMiss, out ReadOnlySpan<float> initHp);
                    aquaticLocomotion.RefreshEnvironmentAndPropulsion(Pawn, initMiss, initHp);
                }
            }

            if (UnityData.IsInMainThread)
            {
                WarmTexturesAndMasks();
            }

            if (bezierRuntimes != null)
            {
                for (int i = 0; i < bezierRuntimes.Length; i++)
                {
                    bezierRuntimes[i]?.Reset();
                }
            }
            if (softWhipRuntimes != null)
            {
                for (int i = 0; i < softWhipRuntimes.Length; i++)
                {
                    softWhipRuntimes[i]?.Reset();
                }
            }

            if (Pawn != null)
            {
                lastSpawnedMap = Pawn.Map;
                lastDrawPos = GetSimTickPos(Pawn);
                hasLastDrawPos = true;
                continuousSteeringTicks = (Pawn.thingIDNumber & 1023) * 17f;
            }

            if (Pawn != null && !Pawn.Dead && (animState == GiantAnimState.Death || deathAnimCompleted))
            {
                animState = GiantAnimState.Idle;
                currentClip = null;
                activeOneShotAnim = null;
                oneShotElapsedTicks = 0f;
                deathAnimCompleted = false;
                deathTickZeroEventsEvaluated = false;
                postDeathSettleTicks = 0;
            }

            missingMaskInitialized = false;
            healthStateDirty = true;
            lastCheckedHediffCount = -1;
            lastHealthCheckTick = -999;
            UpdateMissingPartsState(out _, force: true);
            missingMaskInitialized = true;

            steeringRuntime?.Reset(currentHeadingDeg);
            hitReactionRuntime?.Reset();
            trackKinematicsInitialized = false;
            pendingTrackWorldFwdDelta = 0f;
            ClearAllTurretRecoils();
            movingPauseGraceTicks = 0;
            isPivotTurningInPlace = false;
            isTurnInPlaceAnimLatched = false;
            hasPivotDesiredBearing = false;
            lastSampledPathRemainingCells = 0f;
            lockedLookTargetRef = null;
            lockedLookTargetUntilTick = -999;
            smoothedDesiredLookDeg = currentHeadingDeg;
            smoothedLookInitialized = true;
            currentSteeringCtx = new GiantSteeringContext
            {
                DesiredBodyHeadingDeg = currentHeadingDeg,
                DesiredLookHeadingDeg = currentHeadingDeg,
                HasExplicitLookTarget = false,
                IsCombatTarget = false,
                IsMoving = false,
                MovedDistanceCells = 0f,
                AnimState = animState,
                ContinuousTicks = continuousSteeringTicks,
                SteeringEnabled = RigDef?.steering?.enabled ?? true,
                IntensityScale = Mathf.Clamp(RigDef?.steering?.intensityScale ?? 1.0f, 0.25f, 2.0f),
                EnableNeckIdleAndGaitArticulation = RigDef?.steering?.enableNeckIdleAndGaitArticulation ?? true
            };

            if (!respawningAfterLoad
                && !flightEntryCompleted
                && RigDef?.flightEntry != null
                && RigDef.flightEntry.enabled
                && Pawn != null
                && Pawn.Spawned
                && !Pawn.Dead
                && !Pawn.Downed
                && Pawn.Map != null
                && !Pawn.InContainerEnclosed)
            {
                BeginFlightEntry();
            }

            EvaluateCurrentPoseAndPublish(0f);
            RebuildFootprintNow(notifyMap: false);
            if (RigDef != null && Pawn != null && Current.Game?.tickManager != null)
            {
                int interval = Mathf.Max(1, RigDef.footprintUpdateIntervalTicks);
                lastFootprintTick = Current.Game.tickManager.TicksGame - ((Pawn.thingIDNumber & 0x7FFFFFFF) % interval);
            }
            GiantFootprintMapComponent.GetFast(Pawn?.Map)?.RegisterGiant(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            lockedLookTargetRef = null;
            lockedLookTargetUntilTick = -999;
            GiantTargetAimTracker.ClearForGiant(Pawn);
            GiantFootprintMapComponent.GetFast(map)?.UnregisterGiant(this);
            base.PostDeSpawn(map, mode);
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            lockedLookTargetRef = null;
            lockedLookTargetUntilTick = -999;
            ReleaseAllDynamicRenderMeshes();
            cachedRigSubtrees?.Clear();
            GiantTargetAimTracker.ClearForGiant(Pawn);
            GiantFootprintMapComponent.GetFast(previousMap)?.UnregisterGiant(this);
            if (Pawn == null || Pawn.Corpse == null || Pawn.Discarded)
            {
                GiantFastLookup.Unregister(Pawn);
            }
            base.PostDestroy(mode, previousMap);
        }

        public override void CompTick()
        {
            base.CompTick();
            if (Pawn == null || !Pawn.Spawned || RigDef == null)
            {
                return;
            }

            if (!flightEntryActive)
            {
                GiantPlayerControlUtility.SynchronizeDraftedJobState(Pawn, ref lastObservedDrafted, ref draftedStateInitialized);
            }

            UpdateMissingPartsState(out bool missingChanged);
            vehicleLocomotion?.EnsureSteppedAndSyncPosition(Pawn, this);
            flightLocomotion?.StepTick(this, lastStepHeadingDeltaDeg);
            if (flightEntryActive)
            {
                StepFlightEntry();
            }
            if (flightBlendRemainingTicks > 0f)
            {
                flightBlendRemainingTicks = Mathf.Max(0f, flightBlendRemainingTicks - 1f);
            }
            float movedDist = ConsumeMovedDistanceCells(out float signedFwdDelta);
            aquaticLocomotion?.StepTick(this, movedDist);
            pendingTrackWorldFwdDelta += signedFwdDelta;
            if (IsActivelyLocomoting)
            {
                movingPauseGraceTicks = 8;
            }
            else if (IsPatherMovingNotBusy && movingPauseGraceTicks > 0)
            {
                movingPauseGraceTicks--;
            }
            else
            {
                movingPauseGraceTicks = 0;
            }
            continuousSteeringTicks += 1f;
            if (!Pawn.Dead &&
                (IsCurrentlyAirborne || !(GiantFastLookup.TryGetCockpit(Pawn) is CompGiantCockpit spinCockpit && spinCockpit.IsDormantUnmanned)))
            {
                continuousSpinTicks += 1f;
            }
            bool anyPartAnimating = false;
            if (!Pawn.Dead && !flightEntryActive && partCombat != null)
            {
                int preTick = Find.TickManager?.TicksGame ?? 0;
                partCombat.TickPartCombat(preTick);
                IReadOnlyList<BonePartWeaponGroup> pGroups = partCombat.Groups;
                for (int pg = 0; pg < pGroups.Count; pg++)
                {
                    if (pGroups[pg].HasActiveVisualOverlay)
                    {
                        anyPartAnimating = true;
                        break;
                    }
                }
            }

            UpdateHeadingAndSteering(1f, movedDist);
            UpdateAnimationClock(1f, movedDist);
            currentSteeringCtx.AnimState = animState;
            EvaluateCurrentPoseAndPublish(1f);

            int now = Find.TickManager.TicksGame;
            bool cellChanged = Pawn.Position != lastFootprintCell;
            bool rotChanged = Mathf.Abs(Mathf.DeltaAngle(currentHeadingDeg, lastFootprintHeading)) >= 3.2f;
            bool steerBendChanged = Mathf.Abs((steeringRuntime?.PrimarySpineBendDeg ?? 0f) - lastFootprintSpineBend) >= 3.8f;
            bool bezierSettling = false;
            if (bezierRuntimes != null)
            {
                for (int c = 0; c < bezierRuntimes.Length; c++)
                {
                    if (bezierRuntimes[c] != null && bezierRuntimes[c].IsSettling)
                    {
                        bezierSettling = true;
                        break;
                    }
                }
            }
            bool softWhipFootprintMoving = false;
            if (softWhipRuntimes != null)
            {
                for (int s = 0; s < softWhipRuntimes.Length; s++)
                {
                    if (softWhipRuntimes[s] != null && softWhipRuntimes[s].TipSpeedCellsPerTick > 0.08f)
                    {
                        softWhipFootprintMoving = true;
                        break;
                    }
                }
            }
            bool steeringSettling = steeringRuntime != null && steeringRuntime.IsSettling;
            bool sleepCurlAnimating = sleepCurlProgress01 > 0.001f && sleepCurlProgress01 < 0.999f;
            bool sleepCurlFootprintDirty = Mathf.Abs(sleepCurlProgress01 - lastFootprintSleepCurl) >= 0.08f
                || (lastFootprintSleepCurl > 0f && sleepCurlProgress01 <= 0.001f)
                || (lastFootprintSleepCurl < 0.999f && sleepCurlProgress01 >= 0.999f);
            bool animActive = animState == GiantAnimState.Walk
                || animState == GiantAnimState.Run
                || animState == GiantAnimState.TurnInPlace
                || animState == GiantAnimState.AttackOnce
                || animState == GiantAnimState.MorphLocked
                || anyPartAnimating
                || ManualOverrideActive
                || blendElapsedTicks < blendDurationTicks
                || bezierSettling
                || softWhipFootprintMoving
                || steeringSettling
                || (hitReactionRuntime != null && hitReactionRuntime.IsActive)
                || anyTurretRecoilActive
                || sleepCurlAnimating;
            Vector3 curFootprintRootPos = (Pawn.Spawned && Pawn.Drawer != null) ? GetSafeDrawPos(Pawn) : GetSimTickPos(Pawn);
            Vector2 curFootprintXZ = new Vector2(curFootprintRootPos.x, curFootprintRootPos.z);
            bool drawPosShifted = float.IsNaN(lastFootprintWorldXZ.x) || (curFootprintXZ - lastFootprintWorldXZ).sqrMagnitude >= 0.12f;
            int minSubCellInterval = Mathf.Max(1, RigDef.footprintUpdateIntervalTicks);
            bool intervalDue = animActive && (now - lastFootprintTick >= minSubCellInterval);
            bool immediateFootprint = cellChanged || missingChanged || anySectorChangedPendingFootprint || lastFootprintTick < 0;
            bool minCadenceMet = now - lastFootprintTick >= Mathf.Max(2, minSubCellInterval / 2);

            if (immediateFootprint)
            {
                RebuildFootprintNow(notifyMap: true);
            }
            else if (minCadenceMet && (drawPosShifted || rotChanged || steerBendChanged || sleepCurlFootprintDirty || intervalDue))
            {
                bool overdue = now - lastFootprintTick >= minSubCellInterval + 4;
                GiantFootprintMapComponent fpMap = Pawn.Map != null ? GiantFootprintMapComponent.GetFast(Pawn.Map) : null;
                if (overdue || fpMap == null || fpMap.TryConsumeSubCellFootprintBudget(now, maxPerTick: 2))
                {
                    RebuildFootprintNow(notifyMap: true);
                }
            }

            bool activelyMoving = IsActivelyLocomoting;
            if (activelyMoving
                && !flightEntryActive
                && !Pawn.Downed
                && !Pawn.Dead
                && GiantDestructionUtility.CanGiantCrushAnything(this, out _, out _, out _)
                && (cellChanged || (now - lastCrushPulseTick >= Mathf.Max(1, RigDef.crushIntervalTicks))))
            {
                lastCrushPulseTick = now;
                GiantDestructionUtility.CrushObstaclesInCollisionCells(this);
            }

            if (!Pawn.Dead)
            {
                TickPersistentBoneAuras(now);
                GiantBloodFilthUtility.TickGroundedBleedingDrip(Pawn, this, now);
            }
        }

    }
}
