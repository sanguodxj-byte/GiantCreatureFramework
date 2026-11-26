using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework
{
    public enum PhaseMorphState
    {
        None = 0,
        MorphingOut = 1,
        MorphingIn = 2
    }

    public class GiantPhaseStageDef
    {
        public string stageId;
        public string stageLabelKey;
        public GiantRigDef rigDef;
        public GiantAnimDef morphOutToNextClip;
        public GiantAnimDef morphInFromPrevClip;
        public GiantAnimDef morphOutToPrevClip;
        public GiantAnimDef morphInFromNextClip;

        public float bodySizeFactor = 1f;
        public float healthScaleFactor = 1f;
        public float moveSpeedOffset = 0f;
        public float meleeDamageFactor = 1f;
        public float armorSharpOffset = 0f;
        public float armorBluntOffset = 0f;
        public float armorHeatOffset = 0f;

        public Color reactorGlowColor = new Color(0.22f, 0.88f, 1f, 1f);
        public float baseGlowIntensity = 1.15f;

        public List<AbilityDef> stageAbilities = new List<AbilityDef>();
        public ThingDef rangedWeaponDef;

        public float transitionBlastRadius = 6.2f;
        public int transitionEmpDamage = 40;
        public int transitionBlastDamage = 26;
    }

    public class CompProperties_GiantPhaseTransform : CompProperties
    {
        public List<GiantPhaseStageDef> stages = new List<GiantPhaseStageDef>();
        public float triggerSummaryHealthThreshold = 0.04f;
        public int minStageDwellTicks = 240;
        public float finalMeltdownRadius = 8.5f;
        public int finalMeltdownDamage = 65;

        public CompProperties_GiantPhaseTransform()
        {
            compClass = typeof(CompGiantPhaseTransform);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string err in base.ConfigErrors(parentDef))
            {
                yield return err;
            }

            if (stages == null || stages.Count < 2)
            {
                yield return $"CompProperties_GiantPhaseTransform on '{parentDef?.defName}' requires at least 2 stages.";
                yield break;
            }

            for (int i = 0; i < stages.Count; i++)
            {
                GiantPhaseStageDef s = stages[i];
                if (s == null || s.rigDef == null)
                {
                    yield return $"CompProperties_GiantPhaseTransform on '{parentDef?.defName}' has null stage or rigDef at index {i}.";
                    continue;
                }
                foreach (string combatErr in s.rigDef.ValidateThingDefCombatAnimations(parentDef, s.stageAbilities, s.rangedWeaponDef, $"stage '{s.stageId ?? i.ToString()}'"))
                {
                    yield return combatErr;
                }
                foreach (string morphOutErr in s.rigDef.ValidateExtraClipRigidAndChildOffsets(s.morphOutToNextClip, $"stage '{s.stageId ?? i.ToString()}' morphOutToNextClip"))
                {
                    yield return morphOutErr;
                }
                foreach (string morphInErr in s.rigDef.ValidateExtraClipRigidAndChildOffsets(s.morphInFromPrevClip, $"stage '{s.stageId ?? i.ToString()}' morphInFromPrevClip"))
                {
                    yield return morphInErr;
                }
                foreach (string morphRevOutErr in s.rigDef.ValidateExtraClipRigidAndChildOffsets(s.morphOutToPrevClip, $"stage '{s.stageId ?? i.ToString()}' morphOutToPrevClip"))
                {
                    yield return morphRevOutErr;
                }
                foreach (string morphRevInErr in s.rigDef.ValidateExtraClipRigidAndChildOffsets(s.morphInFromNextClip, $"stage '{s.stageId ?? i.ToString()}' morphInFromNextClip"))
                {
                    yield return morphRevInErr;
                }
            }
        }
    }

    public class CompGiantPhaseTransform : ThingComp, IGiantShaderSnapshotComp
    {
        public CompProperties_GiantPhaseTransform Props => (CompProperties_GiantPhaseTransform)props;
        public Pawn Pawn => parent as Pawn;
        private Map TryGetSpawnedMap() => (Pawn != null && Pawn.Spawned && Current.Game != null) ? Pawn.Map : null;

        public const int StaggeredEmpWaveDelayTicks = 2;
        public const int StaggeredBlastWaveDelayTicks = 5;

        private int currentStageIndex = 0;
        private int pendingTargetStageIndex = -1;
        private bool isRevertingStage = false;
        private PhaseMorphState morphState = PhaseMorphState.None;
        private int morphElapsedTicks = 0;
        private int morphPhaseDurationTicks = 45;
        private bool stageLoadoutInitialized = false;
        private int transformationCount = 0;
        private int lastStageEnteredTick = -99999;
        private float shaderPulseClock = 0f;
        private bool suppressCheckReentry = false;
        internal bool IsEvaluatingRawDowned;
        private bool pendingTransitionEmpWave = false;
        private bool pendingTransitionBlastWave = false;
        private int executedEmpWaveCount = 0;
        private int executedBlastWaveCount = 0;
        private readonly HashSet<Thing> explosionIgnoredSetScratch = new HashSet<Thing>();

        public int CurrentStageIndex => Mathf.Clamp(currentStageIndex, 0, Mathf.Max(0, StageCount - 1));
        public int StageCount => Props.stages?.Count ?? 0;
        public bool HasNextStage => CurrentStageIndex + 1 < StageCount;
        public bool HasPreviousStage => CurrentStageIndex > 0;
        public bool IsTransforming => morphState != PhaseMorphState.None;
        public bool IsRevertingStage => isRevertingStage;
        public PhaseMorphState MorphState => morphState;
        public int TransformationCount => transformationCount;
        public bool HasPendingEmpWave => pendingTransitionEmpWave;
        public bool HasPendingBlastWave => pendingTransitionBlastWave;
        public int ExecutedEmpWaveCount => executedEmpWaveCount;
        public int ExecutedBlastWaveCount => executedBlastWaveCount;

        /// <summary>
        /// True strictly when the unit is alive, undowned, at 100% summary health, and free of any
        /// injury, missing body part, or bad condition (1:1 symmetric with FullRestorePawnHealth).
        /// Manual rollback to the previous stage is permitted ONLY when IsAtFullState is true;
        /// when not at full state, only advancing to the next stage is permitted.
        /// </summary>
        public bool IsAtFullState
        {
            get
            {
                if (Pawn == null || Pawn.Destroyed || Pawn.Dead || Pawn.Downed)
                {
                    return false;
                }
                Pawn_HealthTracker health = Pawn.health;
                if (health?.hediffSet == null)
                {
                    return true;
                }
                List<Hediff> hediffs = health.hediffSet.hediffs;
                if (hediffs != null)
                {
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        Hediff h = hediffs[i];
                        if (h == null)
                        {
                            continue;
                        }
                        if ((h is Hediff_Injury inj && inj.Severity > 0.0001f) ||
                            h is Hediff_MissingPart ||
                            (h.def != null && h.def.isBad))
                        {
                            return false;
                        }
                    }
                }
                if (Pawn.ageTracker != null &&
                    Pawn.RaceProps?.lifeStageAges != null &&
                    Pawn.RaceProps.lifeStageAges.Count > 0 &&
                    (health.summaryHealth?.SummaryHealthPercent ?? 1f) < 0.999f)
                {
                    return false;
                }
                return true;
            }
        }

        public GiantPhaseStageDef CurrentStage =>
            (Props.stages != null && Props.stages.Count > 0)
                ? Props.stages[CurrentStageIndex]
                : null;

        public float MorphNormalizedProgress =>
            morphPhaseDurationTicks > 0 ? Mathf.Clamp01(morphElapsedTicks / (float)morphPhaseDurationTicks) : 0f;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref currentStageIndex, "gcf_phaseStageIdx", 0);
            Scribe_Values.Look(ref pendingTargetStageIndex, "gcf_pendingStageIdx", -1);
            Scribe_Values.Look(ref isRevertingStage, "gcf_morphReverting", defaultValue: false);
            Scribe_Values.Look(ref morphState, "gcf_morphState", PhaseMorphState.None);
            Scribe_Values.Look(ref morphElapsedTicks, "gcf_morphElapsed", 0);
            Scribe_Values.Look(ref morphPhaseDurationTicks, "gcf_morphDuration", 45);
            Scribe_Values.Look(ref stageLoadoutInitialized, "gcf_stageInit", defaultValue: false);
            Scribe_Values.Look(ref transformationCount, "gcf_transformCount", 0);
            Scribe_Values.Look(ref lastStageEnteredTick, "gcf_lastStageEnteredTick", -99999);
            Scribe_Values.Look(ref shaderPulseClock, "gcf_shaderClock", 0f);
            Scribe_Values.Look(ref pendingTransitionEmpWave, "gcf_pendingEmpWave", defaultValue: false);
            Scribe_Values.Look(ref pendingTransitionBlastWave, "gcf_pendingBlastWave", defaultValue: false);
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            GiantFastLookup.RegisterPhaseTransform(Pawn, this);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            GiantFastLookup.RegisterPhaseTransform(Pawn, this);
            if (Pawn == null || StageCount == 0)
            {
                return;
            }

            CompGiantRig rig = GiantFastLookup.TryGetRig(Pawn) ?? Pawn.TryGetComp<CompGiantRig>();
            if (Props?.stages != null)
            {
                for (int s = 0; s < Props.stages.Count; s++)
                {
                    GiantRigDef stageRig = Props.stages[s]?.rigDef;
                    CompGiantRig.PrewarmRigMaterialsAndMasks(stageRig);
                    if (UnityData.IsInMainThread)
                    {
                        rig?.PrewarmStageRenderSubtree(stageRig);
                    }
                }
            }

            GiantPhaseStageDef stage = CurrentStage;
            if (stage == null)
            {
                return;
            }

            if (!stageLoadoutInitialized)
            {
                stageLoadoutInitialized = true;
                lastStageEnteredTick = Current.Game?.tickManager?.TicksGame ?? 0;
                ApplyStageLoadoutAndRig(stage, initialMorphClip: null);
            }
            else if (respawningAfterLoad)
            {
                if (rig != null && rig.RigDef != stage.rigDef)
                {
                    rig.SwitchRig(stage.rigDef, initialMorphClip: null);
                }
                if (rig != null && IsTransforming)
                {
                    GiantAnimDef activeMorphClip = null;
                    if (morphState == PhaseMorphState.MorphingOut)
                    {
                        activeMorphClip = isRevertingStage
                            ? (stage.morphOutToPrevClip ?? stage.morphInFromPrevClip?.GetOrCreateTimeReversed())
                            : stage.morphOutToNextClip;
                    }
                    else if (morphState == PhaseMorphState.MorphingIn)
                    {
                        activeMorphClip = isRevertingStage
                            ? (stage.morphInFromNextClip ?? stage.morphOutToNextClip?.GetOrCreateTimeReversed())
                            : stage.morphInFromPrevClip;
                    }
                    if (activeMorphClip != null)
                    {
                        rig.PlayForcedMorphClipAtTick(activeMorphClip, blendTicksOverride: 0f, startElapsedTicks: morphElapsedTicks);
                    }
                }
                SyncStageAbilities(stage);
                EnsureStageWeaponEquipped(stage);
                InvalidateStageStatCaches();
            }
        }

        public bool ShouldInterceptDepletionNow()
        {
            if (suppressCheckReentry || Pawn == null || Pawn.Destroyed)
            {
                return false;
            }
            if (IsTransforming)
            {
                return true;
            }
            if (!HasNextStage)
            {
                return false;
            }

            Pawn_HealthTracker health = Pawn.health;
            if (health == null)
            {
                return false;
            }

            if (health.ShouldBeDead())
            {
                return true;
            }

            int nowTick = Current.Game?.tickManager?.TicksGame ?? 0;
            bool inMinStageDwell = CurrentStageIndex > 0
                && Props.minStageDwellTicks > 0
                && lastStageEnteredTick > -90000
                && (nowTick - lastStageEnteredTick) < Props.minStageDwellTicks;

            float summaryPct = health.summaryHealth?.SummaryHealthPercent ?? 1f;
            if (!inMinStageDwell)
            {
                bool rawShouldBeDowned;
                IsEvaluatingRawDowned = true;
                try
                {
                    rawShouldBeDowned = health.ShouldBeDowned();
                }
                finally
                {
                    IsEvaluatingRawDowned = false;
                }
                if (rawShouldBeDowned)
                {
                    return true;
                }
            }

            if (!inMinStageDwell && summaryPct <= Props.triggerSummaryHealthThreshold)
            {
                return true;
            }

            return false;
        }

        public bool TryInterceptFatalOrDowned(DamageInfo? triggerDamage = null)
        {
            if (suppressCheckReentry || Pawn == null || Pawn.Destroyed)
            {
                return false;
            }

            if (IsTransforming)
            {
                FullRestorePawnHealth();
                return true;
            }

            if (!HasNextStage)
            {
                return false;
            }

            BeginNextStageTransformation(triggerDamage);
            return true;
        }

        public bool CanManuallyAdvanceToNextStage(out string reasonKey, out string fallbackReason)
        {
            if (Pawn == null || Pawn.Destroyed || (Pawn.health != null && Pawn.Dead))
            {
                reasonKey = "GCF_PhaseAdvanceDisabledUnavailable";
                fallbackReason = "Phase transform system unavailable.";
                return false;
            }
            if (IsTransforming)
            {
                reasonKey = "GCF_PhaseAdvanceDisabledTransforming";
                fallbackReason = "Reconfiguring hull.";
                return false;
            }
            if (!HasNextStage)
            {
                reasonKey = "GCF_PhaseAdvanceDisabledFinalStage";
                fallbackReason = "Already in final stage.";
                return false;
            }
            reasonKey = null;
            fallbackReason = null;
            return true;
        }

        public bool CanManuallyRevertToPreviousStage(out string reasonKey, out string fallbackReason)
        {
            if (Pawn == null || Pawn.Destroyed || (Pawn.health != null && Pawn.Dead))
            {
                reasonKey = "GCF_PhaseAdvanceDisabledUnavailable";
                fallbackReason = "Phase transform system unavailable.";
                return false;
            }
            if (IsTransforming)
            {
                reasonKey = "GCF_PhaseRevertDisabledTransforming";
                fallbackReason = "Reconfiguring hull.";
                return false;
            }
            if (!HasPreviousStage)
            {
                reasonKey = "GCF_PhaseRevertDisabledFirstStage";
                fallbackReason = "Already in initial stage.";
                return false;
            }
            if (!IsAtFullState)
            {
                reasonKey = "GCF_PhaseRevertDisabledNotFullState";
                fallbackReason = "Requires full structural integrity to revert to previous stage; can only advance to next stage while damaged.";
                return false;
            }
            reasonKey = null;
            fallbackReason = null;
            return true;
        }

        public bool TryAdvanceToNextStageManually()
        {
            if (!CanManuallyAdvanceToNextStage(out _, out _))
            {
                return false;
            }

            BeginNextStageTransformation();
            return true;
        }

        public bool TryRevertToPreviousStageManually()
        {
            if (!CanManuallyRevertToPreviousStage(out _, out _))
            {
                return false;
            }

            BeginPreviousStageTransformation();
            return true;
        }

        public void BeginNextStageTransformation(DamageInfo? triggerDamage = null)
        {
            if (Pawn == null || !HasNextStage || IsTransforming)
            {
                return;
            }

            suppressCheckReentry = true;
            try
            {
                FullRestorePawnHealth();

                GiantPhaseStageDef curStage = CurrentStage;
                isRevertingStage = false;
                pendingTransitionEmpWave = false;
                pendingTransitionBlastWave = false;
                pendingTargetStageIndex = CurrentStageIndex + 1;
                morphState = PhaseMorphState.MorphingOut;
                morphElapsedTicks = 0;
                morphPhaseDurationTicks = Mathf.Max(24, Mathf.RoundToInt(curStage?.morphOutToNextClip?.durationTicks ?? 42f));

                if (Pawn.Spawned)
                {
                    Pawn.pather?.StopDead();
                    Pawn.jobs?.StopAll(ifLayingKeepLaying: false);
                    int totalLockTicks = morphPhaseDurationTicks + 60;
                    Pawn.stances?.SetStance(new Stance_Cooldown(totalLockTicks, Pawn, null));
                }

                CompGiantRig rig = GiantFastLookup.TryGetRig(Pawn) ?? Pawn.TryGetComp<CompGiantRig>();
                if (rig != null && curStage?.morphOutToNextClip != null)
                {
                    rig.PlayForcedMorphClip(curStage.morphOutToNextClip, blendTicksOverride: 6f);
                }

                Map map = TryGetSpawnedMap();
                if (map != null)
                {
                    GiantPhaseTransformVfxRegistry.NotifyTransformStarted(Pawn, pendingTargetStageIndex, morphPhaseDurationTicks);
                    GiantVfxSystem.SpawnShockRing(map, Pawn.DrawPos, 0.45f, 3.2f + pendingTargetStageIndex * 1.1f, curStage?.reactorGlowColor ?? new Color(0.56f, 0.68f, 0.76f, 0.80f), durationTicks: 24, baseWidth: 0.48f, followThing: Pawn);
                }
            }
            finally
            {
                suppressCheckReentry = false;
            }
        }

        public void BeginPreviousStageTransformation()
        {
            if (Pawn == null || !HasPreviousStage || IsTransforming || !IsAtFullState)
            {
                return;
            }

            suppressCheckReentry = true;
            try
            {
                GiantPhaseStageDef curStage = CurrentStage;
                GiantAnimDef retractOutClip = curStage?.morphOutToPrevClip
                    ?? curStage?.morphInFromPrevClip?.GetOrCreateTimeReversed();

                isRevertingStage = true;
                pendingTransitionEmpWave = false;
                pendingTransitionBlastWave = false;
                pendingTargetStageIndex = CurrentStageIndex - 1;
                morphState = PhaseMorphState.MorphingOut;
                morphElapsedTicks = 0;
                morphPhaseDurationTicks = Mathf.Max(24, Mathf.RoundToInt(retractOutClip?.durationTicks ?? 36f));

                if (Pawn.Spawned)
                {
                    Pawn.pather?.StopDead();
                    Pawn.jobs?.StopAll(ifLayingKeepLaying: false);
                    int totalLockTicks = morphPhaseDurationTicks + 60;
                    Pawn.stances?.SetStance(new Stance_Cooldown(totalLockTicks, Pawn, null));
                }

                CompGiantRig rig = GiantFastLookup.TryGetRig(Pawn) ?? Pawn.TryGetComp<CompGiantRig>();
                if (rig != null && retractOutClip != null)
                {
                    rig.PlayForcedMorphClip(retractOutClip, blendTicksOverride: 6f);
                }

                Map map = TryGetSpawnedMap();
                if (map != null)
                {
                    GiantPhaseStageDef prevStage = Props.stages[pendingTargetStageIndex];
                    GiantVfxSystem.SpawnShockRing(
                        map,
                        Pawn.DrawPos,
                        2.8f + CurrentStageIndex * 0.9f,
                        0.55f,
                        prevStage?.reactorGlowColor ?? new Color(0.22f, 0.88f, 1f, 0.85f),
                        durationTicks: 24,
                        baseWidth: 0.42f,
                        followThing: Pawn);
                }
            }
            finally
            {
                suppressCheckReentry = false;
            }
        }

        public override void CompTick()
        {
            base.CompTick();
            if (Pawn == null || !Pawn.Spawned)
            {
                return;
            }

            if (!Pawn.Dead)
            {
                shaderPulseClock += 1f / 60f;
            }

            GiantPhaseTransformVfxRegistry.NotifyTickPawnEffects(Pawn, this);
            TickMorphState();
        }

        private void TickMorphState()
        {
            if (!IsTransforming)
            {
                if (HasNextStage && ShouldInterceptDepletionNow())
                {
                    BeginNextStageTransformation();
                }
                return;
            }

            morphElapsedTicks++;

            if (morphState == PhaseMorphState.MorphingOut)
            {
                Map morphMap = TryGetSpawnedMap();
                if (morphMap != null && morphElapsedTicks % 10 == 0)
                {
                    Vector3 puff = Pawn.DrawPos + GenRadial.RadialPattern[(morphElapsedTicks / 10) % 16 + 1].ToVector3() * 1.8f;
                    if (puff.ToIntVec3().InBounds(morphMap))
                    {
                        GiantVfxSystem.SpawnSoftGlowBurst(morphMap, puff, 1.15f + pendingTargetStageIndex * 0.35f, new Color(0.58f, 0.70f, 0.78f, 0.55f));
                    }
                }

                if (morphElapsedTicks >= morphPhaseDurationTicks)
                {
                    CompleteMorphOutAndSwitchRig();
                }
            }
            else if (morphState == PhaseMorphState.MorphingIn)
            {
                if (pendingTransitionEmpWave && morphElapsedTicks >= StaggeredEmpWaveDelayTicks)
                {
                    pendingTransitionEmpWave = false;
                    ExecuteStageTransitionEmpWave(CurrentStage);
                }
                else if (pendingTransitionBlastWave && morphElapsedTicks >= StaggeredBlastWaveDelayTicks)
                {
                    pendingTransitionBlastWave = false;
                    ExecuteStageTransitionBlastWave(CurrentStage);
                }

                if (morphElapsedTicks >= morphPhaseDurationTicks)
                {
                    FinishTransformation();
                }
            }
        }

        private void CompleteMorphOutAndSwitchRig()
        {
            suppressCheckReentry = true;
            try
            {
                bool wasReverting = isRevertingStage;
                int nextIdx = Mathf.Clamp(pendingTargetStageIndex, 0, StageCount - 1);
                currentStageIndex = nextIdx;
                pendingTargetStageIndex = -1;
                transformationCount++;

                GiantPhaseStageDef nextStage = CurrentStage;
                InvalidateStageStatCaches();
                if (!wasReverting)
                {
                    FullRestorePawnHealth();
                }

                GiantAnimDef morphInClip = wasReverting
                    ? (nextStage?.morphInFromNextClip ?? nextStage?.morphOutToNextClip?.GetOrCreateTimeReversed())
                    : nextStage?.morphInFromPrevClip;

                ApplyStageLoadoutAndRig(nextStage, morphInClip);
                if (!wasReverting)
                {
                    ScheduleStageTransitionWaves(nextStage);
                }
                else
                {
                    pendingTransitionEmpWave = false;
                    pendingTransitionBlastWave = false;
                }

                morphState = PhaseMorphState.MorphingIn;
                morphElapsedTicks = 0;
                morphPhaseDurationTicks = Mathf.Max(24, Mathf.RoundToInt(morphInClip?.durationTicks ?? 48f));

                if (Pawn.Spawned)
                {
                    Pawn.pather?.StopDead();
                    Pawn.stances?.SetStance(new Stance_Cooldown(morphPhaseDurationTicks + 4, Pawn, null));
                }
            }
            finally
            {
                suppressCheckReentry = false;
            }
        }

        private void FinishTransformation()
        {
            if (pendingTransitionEmpWave || pendingTransitionBlastWave)
            {
                GiantPhaseStageDef stage = CurrentStage;
                if (pendingTransitionEmpWave)
                {
                    pendingTransitionEmpWave = false;
                    ExecuteStageTransitionEmpWave(stage);
                }
                if (pendingTransitionBlastWave)
                {
                    pendingTransitionBlastWave = false;
                    ExecuteStageTransitionBlastWave(stage);
                }
            }

            morphState = PhaseMorphState.None;
            morphElapsedTicks = 0;
            pendingTargetStageIndex = -1;
            isRevertingStage = false;
            lastStageEnteredTick = Current.Game?.tickManager?.TicksGame ?? 0;

            if (Pawn != null && Pawn.Spawned && !Pawn.Dead)
            {
                if (Pawn.stances?.curStance is Stance_Cooldown)
                {
                    Pawn.stances.CancelBusyStanceSoft();
                }
                Pawn.jobs?.CheckForJobOverride();
                CompGiantRig rig = GiantFastLookup.TryGetRig(Pawn) ?? Pawn.TryGetComp<CompGiantRig>();
                rig?.RebuildFootprintNow(notifyMap: true);
                DisplaceOverlappingAdjacentGiants(rig);
            }
        }

        public void ForceAdvanceStageImmediateForTest()
        {
            if (!HasNextStage || Pawn == null)
            {
                return;
            }
            suppressCheckReentry = true;
            try
            {
                isRevertingStage = false;
                pendingTransitionEmpWave = false;
                pendingTransitionBlastWave = false;
                currentStageIndex = Mathf.Clamp(CurrentStageIndex + 1, 0, StageCount - 1);
                pendingTargetStageIndex = -1;
                morphState = PhaseMorphState.None;
                morphElapsedTicks = 0;
                transformationCount++;
                GiantPhaseStageDef nextStage = CurrentStage;
                InvalidateStageStatCaches();
                FullRestorePawnHealth();
                ApplyStageLoadoutAndRig(nextStage, nextStage?.morphInFromPrevClip);
                morphState = PhaseMorphState.MorphingIn;
                morphPhaseDurationTicks = Mathf.Max(24, Mathf.RoundToInt(nextStage?.morphInFromPrevClip?.durationTicks ?? 48f));
            }
            finally
            {
                suppressCheckReentry = false;
            }
        }

        public bool ForceRevertStageImmediateForTest()
        {
            if (!CanManuallyRevertToPreviousStage(out _, out _))
            {
                return false;
            }
            suppressCheckReentry = true;
            try
            {
                isRevertingStage = true;
                pendingTransitionEmpWave = false;
                pendingTransitionBlastWave = false;
                currentStageIndex = Mathf.Clamp(CurrentStageIndex - 1, 0, StageCount - 1);
                pendingTargetStageIndex = -1;
                morphState = PhaseMorphState.None;
                morphElapsedTicks = 0;
                transformationCount++;
                GiantPhaseStageDef prevStage = CurrentStage;
                InvalidateStageStatCaches();
                GiantAnimDef revInClip = prevStage?.morphInFromNextClip ?? prevStage?.morphOutToNextClip?.GetOrCreateTimeReversed();
                ApplyStageLoadoutAndRig(prevStage, revInClip);
                morphState = PhaseMorphState.MorphingIn;
                morphPhaseDurationTicks = Mathf.Max(24, Mathf.RoundToInt(revInClip?.durationTicks ?? 48f));
                return true;
            }
            finally
            {
                suppressCheckReentry = false;
            }
        }

        public void FullRestorePawnHealth()
        {
            Pawn_HealthTracker health = Pawn?.health;
            if (health?.hediffSet == null)
            {
                return;
            }

            health.isBeingKilled = false;
            health.forceDowned = false;

            BodyPartRecord core = Pawn.RaceProps?.body?.corePart;
            if (core != null && DefDatabase<PawnCapacityDef>.DefCount > 0)
            {
                health.RestorePart(core, null, checkStateChange: false);
            }

            List<Hediff> hediffs = health.hediffSet.hediffs;
            for (int i = hediffs.Count - 1; i >= 0; i--)
            {
                Hediff h = hediffs[i];
                if (h is Hediff_Injury || h is Hediff_MissingPart || (h != null && h.def != null && h.def.isBad))
                {
                    hediffs.RemoveAt(i);
                    if (DefDatabase<PawnCapacityDef>.DefCount > 0)
                    {
                        h.PostRemoved();
                    }
                }
            }

            if (DefDatabase<PawnCapacityDef>.DefCount > 0)
            {
                health.hediffSet.DirtyCache();
                health.capacities?.Notify_CapacityLevelsDirty();
                health.summaryHealth?.Notify_HealthChanged();
            }
        }

        private void ApplyStageLoadoutAndRig(GiantPhaseStageDef stage, GiantAnimDef initialMorphClip)
        {
            if (Pawn == null || stage == null)
            {
                return;
            }

            CompGiantRig rig = GiantFastLookup.TryGetRig(Pawn) ?? Pawn.TryGetComp<CompGiantRig>();
            if (rig != null && stage.rigDef != null)
            {
                rig.SwitchRig(stage.rigDef, initialMorphClip);
            }

            SyncStageAbilities(stage);
            EnsureStageWeaponEquipped(stage);
            InvalidateStageStatCaches();
            DisplaceOverlappingAdjacentGiants(rig);
        }

        private void DisplaceOverlappingAdjacentGiants(CompGiantRig selfRig)
        {
            Map map = TryGetSpawnedMap();
            if (selfRig == null || map == null)
            {
                return;
            }
            if (selfRig.Footprint == null || selfRig.Footprint.collisionCellSet.Count == 0)
            {
                selfRig.RebuildFootprintNow(notifyMap: true);
            }
            GiantFootprintMapComponent fpMap = GiantFootprintMapComponent.GetFast(map);
            IReadOnlyList<CompGiantRig> active = fpMap?.ActiveGiants;
            if (active == null)
            {
                return;
            }
            for (int i = 0; i < active.Count; i++)
            {
                CompGiantRig other = active[i];
                Pawn op = other?.Pawn;
                if (other == null || other == selfRig || op == null || !op.Spawned || op.Dead || op.Downed || other.IsCurrentlyAirborne)
                {
                    continue;
                }
                bool overlaps = false;
                foreach (IntVec3 c in other.Footprint.collisionCellSet)
                {
                    if (selfRig.Footprint.collisionCellSet.Contains(c))
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (!overlaps)
                {
                    continue;
                }
                Vector3 pushDir = op.Position.ToVector3Shifted() - Pawn.Position.ToVector3Shifted();
                pushDir.y = 0f;
                if (pushDir.sqrMagnitude < 0.01f)
                {
                    pushDir = Vector3.right;
                }
                pushDir.Normalize();
                for (int step = 2; step <= 10; step += 2)
                {
                    IntVec3 candOrigin = (op.Position.ToVector3Shifted() + pushDir * step).ToIntVec3().ClampInsideMap(map);
                    if (SafeLandingSpotResolver.TryResolveSafeLandingCell(map, op, other, candOrigin, 8, out IntVec3 clearCell) &&
                        clearCell.IsValid && clearCell != op.Position)
                    {
                        op.Position = clearCell;
                        op.Notify_Teleported(endCurrentJob: false, resetTweenedPos: false);
                        other.RebuildFootprintNow(notifyMap: true);
                        break;
                    }
                }
            }
        }

        private void SyncStageAbilities(GiantPhaseStageDef stage)
        {
            if (Pawn == null)
            {
                return;
            }
            Pawn.abilities ??= new Pawn_AbilityTracker(Pawn);

            var desired = new HashSet<AbilityDef>();
            if (stage?.stageAbilities != null)
            {
                for (int i = 0; i < stage.stageAbilities.Count; i++)
                {
                    if (stage.stageAbilities[i] != null)
                    {
                        desired.Add(stage.stageAbilities[i]);
                    }
                }
            }

            if (Props.stages != null)
            {
                for (int s = 0; s < Props.stages.Count; s++)
                {
                    List<AbilityDef> list = Props.stages[s]?.stageAbilities;
                    if (list == null)
                    {
                        continue;
                    }
                    for (int a = 0; a < list.Count; a++)
                    {
                        AbilityDef def = list[a];
                        if (def != null && !desired.Contains(def) && Pawn.abilities.GetAbility(def) != null)
                        {
                            Pawn.abilities.RemoveAbility(def);
                        }
                    }
                }
            }

            foreach (AbilityDef def in desired)
            {
                Pawn.abilities.GainAbility(def);
            }
        }

        private void EnsureStageWeaponEquipped(GiantPhaseStageDef stage)
        {
            // GCF giants forbid local Pawn equipment weapons; all stage weapons operate as BoneNodeDef partAttacks.
            if (Pawn?.equipment != null && Pawn.equipment.HasAnything())
            {
                Pawn.equipment.DestroyAllEquipment();
            }
        }

        private bool IsManagedStageWeapon(ThingDef def)
        {
            if (def == null || Props.stages == null)
            {
                return false;
            }
            for (int i = 0; i < Props.stages.Count; i++)
            {
                if (Props.stages[i]?.rangedWeaponDef == def)
                {
                    return true;
                }
            }
            return false;
        }

        private void ScheduleStageTransitionWaves(GiantPhaseStageDef stage)
        {
            if (stage == null)
            {
                pendingTransitionEmpWave = false;
                pendingTransitionBlastWave = false;
                return;
            }

            pendingTransitionEmpWave = stage.transitionEmpDamage > 0;
            pendingTransitionBlastWave = stage.transitionBlastDamage > 0;

            Map map = TryGetSpawnedMap();
            if (map != null)
            {
                GiantPhaseTransformVfxRegistry.NotifyStageTransitionBurst(
                    map,
                    Pawn,
                    Pawn.DrawPos,
                    CurrentStageIndex,
                    stage.reactorGlowColor);
            }
        }

        private List<Thing> BuildFriendlyExplosionIgnoredList(Map map, IntVec3 center, float radius)
        {
            var ignored = new List<Thing>(32) { Pawn };
            if (Pawn.Faction == null)
            {
                return ignored;
            }

            explosionIgnoredSetScratch.Clear();
            explosionIgnoredSetScratch.Add(Pawn);
            try
            {
                IReadOnlyList<Pawn> mapPawns = map.mapPawns?.AllPawnsSpawned;
                if (mapPawns != null)
                {
                    for (int i = 0; i < mapPawns.Count; i++)
                    {
                        Pawn p = mapPawns[i];
                        if (p != null && p != Pawn && p.Faction == Pawn.Faction && explosionIgnoredSetScratch.Add(p))
                        {
                            ignored.Add(p);
                        }
                    }
                }

                foreach (Thing t in GenRadial.RadialDistinctThingsAround(center, map, radius + 1.5f, useCenter: true))
                {
                    if (t != null && t.Faction == Pawn.Faction && explosionIgnoredSetScratch.Add(t))
                    {
                        ignored.Add(t);
                    }
                }
            }
            finally
            {
                explosionIgnoredSetScratch.Clear();
            }

            return ignored;
        }

        private void ExecuteStageTransitionEmpWave(GiantPhaseStageDef stage)
        {
            if (stage == null || stage.transitionEmpDamage <= 0)
            {
                return;
            }
            executedEmpWaveCount++;

            Map map = TryGetSpawnedMap();
            if (map == null || DamageDefOf.EMP == null)
            {
                return;
            }

            IntVec3 center = Pawn.Position;
            float radius = Mathf.Max(2.5f, stage.transitionBlastRadius);
            List<Thing> ignored = BuildFriendlyExplosionIgnoredList(map, center, radius);

            SoundDef empSound = DamageDefOf.EMP.soundExplosion
                ?? DefDatabase<SoundDef>.GetNamedSilentFail("Explosion_EMP")
                ?? DefDatabase<SoundDef>.GetNamedSilentFail("Explosion_Thump")
                ?? DefDatabase<SoundDef>.GetNamedSilentFail("Explosion_Bomb");
            GenExplosion.DoExplosion(
                center: center,
                map: map,
                radius: radius,
                damType: DamageDefOf.EMP,
                instigator: Pawn,
                damAmount: stage.transitionEmpDamage,
                armorPenetration: 1.0f,
                explosionSound: empSound,
                ignoredThings: ignored,
                doVisualEffects: !GiantVfxSystem.IsRigVfxEnabled(Pawn),
                doSoundEffects: empSound != null,
                screenShakeFactor: 0.9f + CurrentStageIndex * 0.45f);
        }

        private void ExecuteStageTransitionBlastWave(GiantPhaseStageDef stage)
        {
            if (stage == null || stage.transitionBlastDamage <= 0)
            {
                return;
            }
            executedBlastWaveCount++;

            Map map = TryGetSpawnedMap();
            if (map == null)
            {
                return;
            }

            IntVec3 center = Pawn.Position;
            float radius = Mathf.Max(2.5f, stage.transitionBlastRadius);
            List<Thing> ignored = BuildFriendlyExplosionIgnoredList(map, center, radius);

            DamageDef blastType = CurrentStageIndex >= 2 ? DamageDefOf.Flame : DamageDefOf.Blunt;
            SoundDef blastSound = blastType?.soundExplosion
                ?? DefDatabase<SoundDef>.GetNamedSilentFail("Explosion_Thump")
                ?? DefDatabase<SoundDef>.GetNamedSilentFail("Explosion_Bomb");
            GenExplosion.DoExplosion(
                center: center,
                map: map,
                radius: radius * 0.78f,
                damType: blastType,
                instigator: Pawn,
                damAmount: stage.transitionBlastDamage,
                armorPenetration: 0.65f,
                explosionSound: blastSound,
                ignoredThings: ignored,
                doVisualEffects: !GiantVfxSystem.IsRigVfxEnabled(Pawn),
                doSoundEffects: blastSound != null,
                screenShakeFactor: 1.15f + CurrentStageIndex * 0.4f);
        }

        public override void Notify_Killed(Map prevMap, DamageInfo? dinfo = null)
        {
            base.Notify_Killed(prevMap, dinfo);
            GiantPhaseTransformVfxRegistry.NotifyClearForPawn(Pawn);

            if (prevMap != null && Props.finalMeltdownRadius > 0.5f && Props.finalMeltdownDamage > 0)
            {
                IntVec3 pos = Pawn.PositionHeld;
                if (pos.InBounds(prevMap))
                {
                    GiantPhaseTransformVfxRegistry.NotifyFinalMeltdownBurst(
                        prevMap,
                        Pawn,
                        pos.ToVector3Shifted());

                    SoundDef meltdownSound = DamageDefOf.Bomb?.soundExplosion
                        ?? DefDatabase<SoundDef>.GetNamedSilentFail("Explosion_Bomb")
                        ?? DefDatabase<SoundDef>.GetNamedSilentFail("Explosion_Thump");
                    GenExplosion.DoExplosion(
                        center: pos,
                        map: prevMap,
                        radius: Props.finalMeltdownRadius,
                        damType: DamageDefOf.Bomb,
                        instigator: Pawn,
                        damAmount: Props.finalMeltdownDamage,
                        armorPenetration: 0.85f,
                        explosionSound: meltdownSound,
                        doVisualEffects: !GiantVfxSystem.IsRigVfxEnabled(Pawn),
                        doSoundEffects: meltdownSound != null,
                    screenShakeFactor: 1.8f);
                }
            }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            GiantPhaseTransformVfxRegistry.NotifyClearForPawn(Pawn);
            if (mode == DestroyMode.KillFinalize || mode == DestroyMode.Vanish && (Pawn == null || Pawn.Destroyed))
            {
                GiantFastLookup.UnregisterPhaseTransform(Pawn);
            }
            base.PostDeSpawn(map, mode);
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            GiantPhaseTransformVfxRegistry.NotifyClearForPawn(Pawn);
            GiantFastLookup.UnregisterPhaseTransform(Pawn);
            base.PostDestroy(mode, previousMap);
        }

        private void InvalidateStageStatCaches()
        {
            if (Pawn == null)
            {
                return;
            }
            if (DefDatabase<PawnCapacityDef>.DefCount > 0)
            {
                Pawn.health?.capacities?.Notify_CapacityLevelsDirty();
            }
            Pawn.health?.summaryHealth?.Notify_HealthChanged();
        }

        public float GetBodySizeMultiplier()
        {
            GiantPhaseStageDef s = CurrentStage;
            return s != null ? Mathf.Max(0.1f, s.bodySizeFactor) : 1f;
        }

        public float GetHealthScaleMultiplier()
        {
            GiantPhaseStageDef s = CurrentStage;
            return s != null ? Mathf.Max(0.1f, s.healthScaleFactor) : 1f;
        }

        public override float GetStatOffset(StatDef stat)
        {
            GiantPhaseStageDef s = CurrentStage;
            if (s == null || stat == null)
            {
                return 0f;
            }
            if (stat == StatDefOf.MoveSpeed)
            {
                return s.moveSpeedOffset;
            }
            if (stat == StatDefOf.ArmorRating_Sharp)
            {
                return s.armorSharpOffset;
            }
            if (stat == StatDefOf.ArmorRating_Blunt)
            {
                return s.armorBluntOffset;
            }
            if (stat == StatDefOf.ArmorRating_Heat)
            {
                return s.armorHeatOffset;
            }
            return 0f;
        }

        public override float GetStatFactor(StatDef stat)
        {
            GiantPhaseStageDef s = CurrentStage;
            if (s == null || stat == null)
            {
                return 1f;
            }
            if (stat == StatDefOf.MeleeDamageFactor)
            {
                return Mathf.Max(0.1f, s.meleeDamageFactor);
            }
            return 1f;
        }

        public void ComputeShaderSnapshot(out Color glowColor, out Vector4 shaderParams)
        {
            GiantPhaseStageDef cur = CurrentStage;
            Color baseCol = cur?.reactorGlowColor ?? new Color(0.22f, 0.88f, 1f, 1f);
            float baseGlow = cur?.baseGlowIntensity ?? 1.15f;
            float morphFlash = 0f;
            float scanline = 0f;

            if (morphState == PhaseMorphState.MorphingOut)
            {
                float t = MorphNormalizedProgress;
                GiantPhaseStageDef next = (pendingTargetStageIndex >= 0 && pendingTargetStageIndex < StageCount)
                    ? Props.stages[pendingTargetStageIndex]
                    : cur;
                Color nextCol = next?.reactorGlowColor ?? baseCol;
                baseCol = Color.Lerp(baseCol, nextCol, t * 0.75f);
                morphFlash = Mathf.Pow(t, 1.8f);
                scanline = t;
                baseGlow = Mathf.Lerp(baseGlow, 2.85f, t);
            }
            else if (morphState == PhaseMorphState.MorphingIn)
            {
                float t = MorphNormalizedProgress;
                morphFlash = Mathf.Pow(1f - t, 1.5f);
                scanline = 1f - t;
                baseGlow = Mathf.Lerp(2.85f, baseGlow, t);
            }

            glowColor = baseCol;
            shaderParams = new Vector4(shaderPulseClock, baseGlow, morphFlash, scanline);
        }

        public override string CompInspectStringExtra()
        {
            if (Pawn == null || Pawn.Dead)
            {
                return null;
            }
            GiantPhaseStageDef stage = CurrentStage;
            if (stage == null || string.IsNullOrEmpty(stage.stageLabelKey))
            {
                return null;
            }
            return stage.stageLabelKey.CanTranslate()
                ? stage.stageLabelKey.Translate().ToString()
                : null;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

        }
    }
}
