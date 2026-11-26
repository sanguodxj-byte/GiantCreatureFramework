using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public static class GiantCombatMechanicsUtility
    {
        [ThreadStatic]
        private static bool isExecutingCleave;

        [ThreadStatic]
        private static bool isEvaluatingGiantPain;

        [ThreadStatic]
        private static bool isEvaluatingPrematureLethalGuard;

        private static readonly ConditionalWeakTable<Explosion, Dictionary<int, HashSet<int>>> explosionStruckBonesTable =
            new ConditionalWeakTable<Explosion, Dictionary<int, HashSet<int>>>();

        private const int MaxDistinctLimbBonesPerExplosion = 3;
        private const float SecondaryLimbShrapnelDamageFactor = 0.52f;

        public static void ClearStaticCaches()
        {
            explosionStruckBonesTable.Clear();
        }

        private static readonly MethodInfo ExplosionDamageThingMethod =
            AccessTools.Method(typeof(DamageWorker), "ExplosionDamageThing", new[]
            {
                typeof(Explosion), typeof(Thing), typeof(List<Thing>), typeof(List<Thing>), typeof(IntVec3)
            });

        private static readonly Action<DamageWorker, Explosion, Thing, List<Thing>, List<Thing>, IntVec3> ExplosionDamageThingFast =
            CreateExplosionDamageThingDelegate();

        private static Action<DamageWorker, Explosion, Thing, List<Thing>, List<Thing>, IntVec3> CreateExplosionDamageThingDelegate()
        {
            if (ExplosionDamageThingMethod == null)
            {
                return null;
            }
            try
            {
                return (Action<DamageWorker, Explosion, Thing, List<Thing>, List<Thing>, IntVec3>)Delegate.CreateDelegate(
                    typeof(Action<DamageWorker, Explosion, Thing, List<Thing>, List<Thing>, IntVec3>),
                    ExplosionDamageThingMethod);
            }
            catch
            {
                return null;
            }
        }

        public static bool IsPainImmuneGiantConfig(
            GiantRigDef rigDef,
            bool isVehicle,
            bool isFlesh,
            bool isMechanoid)
        {
            if (rigDef != null && rigDef.painImmune)
            {
                return true;
            }
            if (isVehicle || !isFlesh || isMechanoid)
            {
                return true;
            }
            return false;
        }

        public static bool IsPainImmuneGiant(Pawn pawn, CompGiantRig comp)
        {
            if (comp?.RigDef != null && comp.RigDef.painImmune)
            {
                return true;
            }
            if (pawn == null)
            {
                return false;
            }
            if (GiantVehicleBehaviorGuard.IsVehiclePawn(pawn, out _, out _))
            {
                return true;
            }
            RaceProperties rp = pawn.RaceProps;
            if (rp?.FleshType != null && (!rp.IsFlesh || rp.IsMechanoid))
            {
                return true;
            }
            return false;
        }

        public static float ResolveEffectivePainShockThreshold(Pawn pawn, GiantRigDef rigDef)
        {
            if (rigDef != null && rigDef.painShockThresholdOverride > 0f)
            {
                return Mathf.Clamp(rigDef.painShockThresholdOverride, 0.05f, 1.0f);
            }
            if (pawn != null && StatDefOf.PainShockThreshold != null && DefDatabase<StatDef>.DefCount > 0)
            {
                return Mathf.Clamp(pawn.GetStatValue(StatDefOf.PainShockThreshold), 0.05f, 1.0f);
            }
            if (pawn?.def?.statBases != null)
            {
                for (int i = 0; i < pawn.def.statBases.Count; i++)
                {
                    StatModifier sm = pawn.def.statBases[i];
                    if (sm != null && (sm.stat == StatDefOf.PainShockThreshold || sm.stat?.defName == "PainShockThreshold"))
                    {
                        return Mathf.Clamp(sm.value, 0.05f, 1.0f);
                    }
                }
            }
            return 0.80f;
        }

        public static float ComputeGiantPainNormalizationDivisor(CompGiantRig comp, float corePartBaseHp)
        {
            float lethalMult = ComputeGiantLethalThresholdMultiplier(comp);
            float coreScale = Mathf.Clamp(Mathf.Max(40f, corePartBaseHp) / 70f, 1.0f, 2.5f);
            return Mathf.Max(1.0f, lethalMult * coreScale);
        }

        public static float EvaluateGiantNormalizedPain(
            GiantRigDef rigDef,
            bool isPainImmune,
            float rawInjuryPainOffsetSum,
            float nonInjuryPainOffsetSum,
            float combinedPainFactorProduct,
            float painNormalizationDivisor,
            float effectivePainShockThreshold,
            int severedBoneCount,
            float synchronizedHealthPct)
        {
            if (isPainImmune)
            {
                return 0f;
            }

            float scale = rigDef != null ? Mathf.Max(0f, rigDef.painScaleFactor) : 1f;
            float scaledInjuryPain = (Mathf.Max(0f, rawInjuryPainOffsetSum) / Mathf.Max(1f, painNormalizationDivisor)) * scale;

            int reqSevers = rigDef != null ? Mathf.Max(0, rigDef.minSeveredBonesForPainShock) : 1;
            float hpGate = rigDef != null ? Mathf.Clamp01(rigDef.unseveredPainShockMaxHealthPct) : 0.25f;
            bool blockWoundShock = reqSevers > 0
                && severedBoneCount < reqSevers
                && synchronizedHealthPct > hpGate;

            if (blockWoundShock)
            {
                float maxAllowedInjuryPain = Mathf.Max(0.02f, effectivePainShockThreshold - 0.05f);
                if (scaledInjuryPain > maxAllowedInjuryPain)
                {
                    scaledInjuryPain = maxAllowedInjuryPain;
                }
            }

            float total = (scaledInjuryPain + nonInjuryPainOffsetSum) * Mathf.Max(0f, combinedPainFactorProduct);
            if (blockWoundShock && nonInjuryPainOffsetSum <= 0.0001f)
            {
                total = Mathf.Min(total, Mathf.Max(0.02f, effectivePainShockThreshold - 0.04f));
            }

            return Mathf.Clamp01(total);
        }

        public static float CalculateGiantPain(Pawn pawn, HediffSet hediffSet, CompGiantRig comp)
        {
            if (pawn == null || hediffSet == null || pawn.Dead)
            {
                return 0f;
            }
            if (IsPainImmuneGiant(pawn, comp))
            {
                return 0f;
            }

            List<Hediff> hediffs = hediffSet.hediffs;
            if (hediffs == null || hediffs.Count == 0)
            {
                return 0f;
            }

            float rawInjuryPain = 0f;
            float nonInjuryPain = 0f;
            float painFactorProduct = 1f;

            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff h = hediffs[i];
                if (h == null)
                {
                    continue;
                }
                float pOff = h.PainOffset;
                if (h is Hediff_Injury || h is Hediff_MissingPart)
                {
                    if (pOff > 0f)
                    {
                        rawInjuryPain += pOff;
                    }
                }
                else
                {
                    nonInjuryPain += pOff;
                }
                painFactorProduct *= h.PainFactor;
            }

            if (pawn.genes != null)
            {
                nonInjuryPain += pawn.genes.PainOffset;
                painFactorProduct *= pawn.genes.PainFactor;
            }

            BodyPartRecord core = comp?.GetLinkedBodyPartRecord(0) ?? pawn.RaceProps?.body?.corePart;
            float coreBaseHp = core?.def != null ? Mathf.Max(40f, core.def.hitPoints) : 40f;
            float divisor = ComputeGiantPainNormalizationDivisor(comp, coreBaseHp);
            float effectiveThreshold = ResolveEffectivePainShockThreshold(pawn, comp?.RigDef);
            int severedCount = comp != null ? comp.CountSeveredBones() : 0;

            float syncedHpPct = 1f;
            if (!isEvaluatingGiantPain && comp != null)
            {
                isEvaluatingGiantPain = true;
                try
                {
                    syncedHpPct = GiantBossHealthBarHUD.ComputeSynchronizedGiantHealthPercent(pawn, comp);
                }
                finally
                {
                    isEvaluatingGiantPain = false;
                }
            }

            return EvaluateGiantNormalizedPain(
                comp?.RigDef,
                isPainImmune: false,
                rawInjuryPain,
                nonInjuryPain,
                painFactorProduct,
                divisor,
                effectiveThreshold,
                severedCount,
                syncedHpPct);
        }

        /// <summary>
        /// Normalizes vanilla <c>HediffSet.CalculateBleedRate()</c> (which only divides by <c>Pawn.HealthScale</c>)
        /// by the multi-bone skeletal divisor so distributed wounds across 11-46 parts do not cause premature
        /// <c>BloodLoss &gt;= 0.60</c> coma while Boss HP is still high.
        /// </summary>
        public static float NormalizeGiantBleedRate(Pawn pawn, CompGiantRig comp, float rawBleedRate)
        {
            if (rawBleedRate <= 0f || comp?.RigDef == null || !comp.RigDef.enableLimbPartDamageRouting)
            {
                return Mathf.Max(0f, rawBleedRate);
            }
            if (comp.RigDef.onlyDieWhenCoreDestroyed && IsPainImmuneGiant(pawn, comp))
            {
                return 0f;
            }
            BodyPartRecord core = comp.GetLinkedBodyPartRecord(0) ?? pawn?.RaceProps?.body?.corePart;
            float coreBaseHp = core?.def != null ? Mathf.Max(40f, core.def.hitPoints) : 40f;
            float divisor = Mathf.Max(1f, ComputeGiantPainNormalizationDivisor(comp, coreBaseHp));
            return rawBleedRate / divisor;
        }

        /// <summary>
        /// Returns true when a giant should disable vanilla RimWorld's 50% non-player animal <c>DeathOnDowned</c>
        /// coin-flip so reaching <c>ShouldBeDowned()</c> reliably plays <c>downedAnim</c> instead of instant death.
        /// </summary>
        public static bool ShouldSuppressRandomDeathOnDowned(GiantRigDef rigDef)
        {
            return rigDef != null && (rigDef.downedAnim != null || !rigDef.onlyDieWhenCoreDestroyed);
        }

        public static void EnsureGiantDeathOnDownedSuppressed(Pawn pawn, CompGiantRig comp)
        {
            if (pawn?.health == null || comp == null || !ShouldSuppressRandomDeathOnDowned(comp.RigDef))
            {
                return;
            }
            if (pawn.health.overrideDeathOnDownedChance < 0f)
            {
                pawn.health.overrideDeathOnDownedChance = 0f;
            }
        }

        public static bool HasDestroyedLethalCoreBone(GiantRigDef rigDef, ReadOnlySpan<bool> boneMissingMask)
        {
            if (rigDef == null)
            {
                return false;
            }
            int[] lethalIndices = rigDef.LethalCoreNodeIndices;
            for (int i = 0; i < lethalIndices.Length; i++)
            {
                int idx = lethalIndices[i];
                if ((uint)idx < (uint)boneMissingMask.Length && boneMissingMask[idx])
                {
                    return true;
                }
            }
            return false;
        }

        public static bool HasDestroyedLethalCoreBone(Pawn pawn, CompGiantRig comp)
        {
            if (pawn?.health?.hediffSet == null || comp?.RigDef == null)
            {
                return false;
            }
            int[] lethalIndices = comp.RigDef.LethalCoreNodeIndices;
            if (lethalIndices.Length == 0)
            {
                return false;
            }
            HediffSet hediffSet = pawn.health.hediffSet;
            for (int i = 0; i < lethalIndices.Length; i++)
            {
                int idx = lethalIndices[i];
                BodyPartRecord rec = comp.GetLinkedBodyPartRaw(idx);
                if (rec != null && hediffSet.PartIsMissing(rec))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool ShouldSuppressNonCoreDowned(Pawn pawn, CompGiantRig comp)
        {
            if (pawn == null || pawn.Dead || comp?.RigDef == null)
            {
                return false;
            }
            if (!comp.RigDef.onlyDieWhenCoreDestroyed || !IsPainImmuneGiant(pawn, comp))
            {
                return false;
            }
            BodyPartRecord core = pawn.RaceProps?.body?.corePart;
            if (core != null && pawn.health?.hediffSet != null && pawn.health.hediffSet.PartIsMissing(core))
            {
                return false;
            }
            if (HasDestroyedLethalCoreBone(pawn, comp))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Multiplies vanilla Pawn_HealthTracker.LethalDamageThreshold (150 * HealthScale) for multi-module giants
        /// so distributed scratches across 11-34 exterior parts never kill a giant with all limbs intact.
        /// </summary>
        public static float ComputeGiantLethalThresholdMultiplier(CompGiantRig comp)
        {
            int nodeCount = comp?.RigDef?.SortedNodes?.Length ?? 10;
            return Mathf.Clamp(2.0f + 0.14f * nodeCount, 3.2f, 6.0f);
        }

        /// <summary>
        /// Guards against cumulative-injury threshold death when a giant still has a healthy core hull (> 18% HP)
        /// and fewer than 2 exterior modules severed, forcing attackers to visibly break armor/limbs or breach the core.
        /// </summary>
        public static bool ShouldPreventPrematureLethalThresholdDeath(
            float corePartHpPct,
            int severedBoneCount,
            int totalSeverableBones)
        {
            if (corePartHpPct <= 0.18f)
            {
                return false;
            }
            int minRequiredSevers = Mathf.Clamp(totalSeverableBones >= 4 ? 2 : 1, 1, 2);
            return severedBoneCount < minRequiredSevers;
        }

        /// <summary>
        /// Extended premature lethal-threshold guard for multi-bone bosses: even after 2+ fragile peripheral
        /// appendages (e.g. wing lances, crests, talons) break, suppresses vanilla <c>LethalDamageThreshold</c>
        /// instant death while both the core shell and synchronized overall boss health remain above 18%.
        /// </summary>
        public static bool ShouldPreventPrematureLethalThresholdDeath(
            float corePartHpPct,
            int severedBoneCount,
            int totalSeverableBones,
            float synchronizedHealthPct)
        {
            if (corePartHpPct <= 0.18f)
            {
                return false;
            }
            int minRequiredSevers = Mathf.Clamp(totalSeverableBones >= 4 ? 2 : 1, 1, 2);
            if (severedBoneCount < minRequiredSevers)
            {
                return true;
            }
            return synchronizedHealthPct > 0.18f;
        }

        private static void ComputeSafePartMaxAndCurrentHealth(
            Pawn pawn,
            HediffSet hediffSet,
            BodyPartRecord part,
            out float maxHp,
            out float curHp)
        {
            if (part?.def == null)
            {
                maxHp = 1f;
                curHp = 0f;
                return;
            }

            bool hasLiveAgeTracker = pawn?.ageTracker?.CurLifeStage != null;
            maxHp = Mathf.Max(
                1f,
                hasLiveAgeTracker
                    ? part.def.GetMaxHealth(pawn)
                    : Mathf.CeilToInt(part.def.hitPoints * Mathf.Max(0.01f, pawn?.RaceProps?.baseHealthScale ?? 1f)));

            if (hediffSet == null || hediffSet.PartIsMissing(part))
            {
                curHp = 0f;
                return;
            }

            if (hasLiveAgeTracker)
            {
                curHp = Mathf.Clamp(hediffSet.GetPartHealth(part), 0f, maxHp);
                return;
            }

            curHp = maxHp;
            List<Hediff> hediffs = hediffSet.hediffs;
            if (hediffs != null)
            {
                for (int i = 0; i < hediffs.Count; i++)
                {
                    if (hediffs[i] is Hediff_MissingPart && hediffs[i].Part == part)
                    {
                        curHp = 0f;
                        return;
                    }
                    if (hediffs[i] is Hediff_Injury inj && inj.Part == part)
                    {
                        curHp = !inj.destroysBodyParts
                            ? Mathf.Max(curHp - inj.Severity, 1f)
                            : Mathf.Max(curHp - inj.Severity, 0f);
                    }
                }
            }
            curHp = Mathf.Clamp(curHp, 0f, maxHp);
        }

        private static float ComputeAnatomicalGiantHealthPercentNonRecursive(
            Pawn pawn,
            CompGiantRig comp,
            HediffSet hediffSet,
            float corePct)
        {
            float sumRigHp = 0f;
            float sumRigMax = 0f;
            if (comp?.RigDef?.SortedNodes != null)
            {
                BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
                for (int i = 0; i < nodes.Length; i++)
                {
                    BodyPartRecord rec = comp.GetLinkedBodyPartRecord(i);
                    if (rec == null)
                    {
                        continue;
                    }
                    ComputeSafePartMaxAndCurrentHealth(pawn, hediffSet, rec, out float maxHp, out float curHp);
                    bool missing = (!comp.IsBoneVisibleThreadSafe(i) && nodes[i].hideWhenMissing) || hediffSet.PartIsMissing(rec);
                    sumRigHp += missing ? 0f : curHp;
                    sumRigMax += maxHp;
                }
            }

            float weightedRigPct = GiantBossHealthBarHUD.ComputeWeightedBossHealthPercent(
                corePct, sumRigHp, sumRigMax, pawn != null && pawn.Dead);

            float sumBodyHp = 0f;
            float sumBodyMax = 0f;
            List<BodyPartRecord> allParts = pawn?.RaceProps?.body?.AllParts;
            if (allParts != null && allParts.Count > 0)
            {
                for (int i = 0; i < allParts.Count; i++)
                {
                    BodyPartRecord p = allParts[i];
                    if (p?.def == null)
                    {
                        continue;
                    }
                    ComputeSafePartMaxAndCurrentHealth(pawn, hediffSet, p, out float pMax, out float pCur);
                    sumBodyHp += pCur;
                    sumBodyMax += pMax;
                }
            }

            float allBodyPct = sumBodyMax > 0.001f ? Mathf.Clamp01(sumBodyHp / sumBodyMax) : weightedRigPct;
            return Mathf.Min(Mathf.Clamp01(weightedRigPct), Mathf.Clamp01(allBodyPct));
        }

        public static bool ShouldPreventPrematureLethalThresholdDeath(Pawn pawn, CompGiantRig comp)
        {
            if (pawn?.health?.hediffSet == null || comp?.RigDef == null || !comp.RigDef.enableLimbPartDamageRouting)
            {
                return false;
            }
            HediffSet hediffSet = pawn.health.hediffSet;
            BodyPartRecord core = pawn.RaceProps?.body?.corePart;
            if (core == null || hediffSet.PartIsMissing(core))
            {
                return false;
            }
            if (HasDestroyedLethalCoreBone(pawn, comp))
            {
                return false;
            }
            if (comp.RigDef.onlyDieWhenCoreDestroyed)
            {
                return true;
            }

            ComputeSafePartMaxAndCurrentHealth(pawn, hediffSet, core, out float coreMax, out float coreCur);
            float corePct = Mathf.Clamp01(coreCur / coreMax);
            if (corePct <= 0.18f)
            {
                return false;
            }

            int totalSeverable = 0;
            BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].hideWhenMissing && nodes[i].linkedBodyPart != null)
                {
                    totalSeverable++;
                }
            }
            if (totalSeverable == 0)
            {
                return false;
            }

            int severedCount = comp.CountSeveredBones();
            int minRequiredSevers = Mathf.Clamp(totalSeverable >= 4 ? 2 : 1, 1, 2);
            if (severedCount < minRequiredSevers)
            {
                return true;
            }

            if (isEvaluatingPrematureLethalGuard || pawn.ageTracker?.CurLifeStage == null)
            {
                float anatomicalHpPct = ComputeAnatomicalGiantHealthPercentNonRecursive(pawn, comp, hediffSet, corePct);
                return ShouldPreventPrematureLethalThresholdDeath(corePct, severedCount, totalSeverable, anatomicalHpPct);
            }

            float syncedHpPct;
            isEvaluatingPrematureLethalGuard = true;
            try
            {
                syncedHpPct = GiantBossHealthBarHUD.ComputeSynchronizedGiantHealthPercent(pawn, comp);
            }
            finally
            {
                isEvaluatingPrematureLethalGuard = false;
            }

            return ShouldPreventPrematureLethalThresholdDeath(corePct, severedCount, totalSeverable, syncedHpPct);
        }

        /// <summary>
        /// Walks upward from an <see cref="BodyPartDepth.Inside"/> organ through <c>parent</c> links to find its
        /// enclosing <see cref="BodyPartDepth.Outside"/> armor shell.
        /// </summary>
        public static BodyPartRecord FindEnclosingOutsideShellPart(BodyPartRecord part)
        {
            if (part == null || part.depth != BodyPartDepth.Inside)
            {
                return null;
            }

            BodyPartRecord cur = part.parent;
            int guard = 16;
            while (cur != null && guard-- > 0)
            {
                if (cur.depth == BodyPartDepth.Outside)
                {
                    return cur;
                }
                cur = cur.parent;
            }
            return null;
        }

        /// <summary>
        /// Returns true when damage explicitly targets an internal organ (e.g., <c>dinfo.Depth == BodyPartDepth.Inside</c>,
        /// internal self-detonation such as <c>CompValstraxDragonEnergy</c> chest backfire, non-propagating unattributed
        /// internal injuries, or non-external environmental/medical effects).
        /// </summary>
        public static bool IsExplicitInternalOrSelfBurstDamage(Pawn pawn, BodyPartRecord targetOrgan, in DamageInfo dinfo)
        {
            if (targetOrgan == null || targetOrgan.depth != BodyPartDepth.Inside)
            {
                return false;
            }
            if (dinfo.Depth == BodyPartDepth.Inside)
            {
                return true;
            }

            bool selfOrUnattributed = dinfo.Instigator == null || (pawn != null && dinfo.Instigator == pawn);
            if (selfOrUnattributed)
            {
                bool isBombDef = dinfo.Def != null
                    && (dinfo.Def == DamageDefOf.Bomb || string.Equals(dinfo.Def.defName, "Bomb", StringComparison.Ordinal));
                if (isBombDef || !dinfo.AllowDamagePropagation)
                {
                    return true;
                }
            }

            if (!dinfo.AllowDamagePropagation
                && dinfo.Def != null
                && (dinfo.Def.execution
                    || string.Equals(dinfo.Def.defName, "SurgicalCut", StringComparison.Ordinal)
                    || (pawn?.RaceProps != null && !dinfo.Def.ExternalViolenceFor(pawn))))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether an <see cref="BodyPartDepth.Inside"/> organ injury created by vanilla cleave/stab/blunt
        /// secondary part selection (e.g. <c>DamageWorker_Cut.ApplySpecialEffectsToPart</c> or <c>DamageWorker_Stab</c>)
        /// must be redirected onto its intact enclosing <see cref="BodyPartDepth.Outside"/> armor shell.
        /// </summary>
        public static bool ShouldRedirectInternalOrganInjuryToOuterShell(
            Pawn pawn,
            GiantRigDef rigDef,
            BodyPartRecord injuryPart,
            BodyPartRecord enclosingShellPart,
            bool shellPartIsMissing,
            float shellHpPct,
            in DamageInfo dinfo)
        {
            if (rigDef != null && !rigDef.enableLimbPartDamageRouting)
            {
                return false;
            }
            if (injuryPart == null || injuryPart.depth != BodyPartDepth.Inside)
            {
                return false;
            }
            if (enclosingShellPart == null || enclosingShellPart.depth != BodyPartDepth.Outside || shellPartIsMissing)
            {
                return false;
            }
            if (IsExplicitInternalOrSelfBurstDamage(pawn, injuryPart, in dinfo))
            {
                return false;
            }

            bool isSecondaryCleaveOrSplash = dinfo.HitPart != injuryPart
                || dinfo.AllowDamagePropagation
                || shellHpPct >= 0.30f;
            if (!isSecondaryCleaveOrSplash)
            {
                return false;
            }

            return CompGiantRig.ShouldBlockChildOrganRoll(
                enclosingShellPart,
                shellHpPct,
                injuryPart,
                dinfo,
                breachRoll01: 1f);
        }

        public static bool TryRedirectGiantInternalInjuryToOuterShell(
            Pawn pawn,
            CompGiantRig comp,
            Hediff_Injury injury,
            ref DamageInfo dinfo,
            out BodyPartRecord redirectedShellPart)
        {
            redirectedShellPart = null;
            if (pawn?.health?.hediffSet == null || comp == null || injury?.Part == null)
            {
                return false;
            }
            if (comp.RigDef != null && !comp.RigDef.enableLimbPartDamageRouting)
            {
                return false;
            }
            if (injury.Part.depth != BodyPartDepth.Inside)
            {
                return false;
            }

            BodyPartRecord shellPart = FindEnclosingOutsideShellPart(injury.Part);
            if (shellPart == null)
            {
                return false;
            }

            HediffSet hediffSet = pawn.health.hediffSet;
            if (hediffSet.PartIsMissing(shellPart))
            {
                return false;
            }

            float shellMaxHp = shellPart.def != null
                ? Mathf.Max(
                    1f,
                    pawn.ageTracker != null
                        ? shellPart.def.GetMaxHealth(pawn)
                        : Mathf.CeilToInt(shellPart.def.hitPoints * (pawn.RaceProps?.baseHealthScale ?? 1f)))
                : 1f;
            float shellCurHp;
            if (pawn.ageTracker != null)
            {
                shellCurHp = hediffSet.GetPartHealth(shellPart);
            }
            else
            {
                shellCurHp = shellMaxHp;
                List<Hediff> hediffs = hediffSet.hediffs;
                if (hediffs != null)
                {
                    for (int i = 0; i < hediffs.Count; i++)
                    {
                        if (hediffs[i] is Hediff_MissingPart && hediffs[i].Part == shellPart)
                        {
                            shellCurHp = 0f;
                            break;
                        }
                        if (hediffs[i] is Hediff_Injury inj && inj.Part == shellPart)
                        {
                            shellCurHp = !inj.destroysBodyParts
                                ? Mathf.Max(shellCurHp - inj.Severity, 1f)
                                : Mathf.Max(shellCurHp - inj.Severity, 0f);
                        }
                    }
                }
            }
            float shellHpPct = Mathf.Clamp01(shellCurHp / shellMaxHp);

            if (!ShouldRedirectInternalOrganInjuryToOuterShell(
                    pawn,
                    comp.RigDef,
                    injury.Part,
                    shellPart,
                    shellPartIsMissing: false,
                    shellHpPct,
                    in dinfo))
            {
                return false;
            }

            injury.Part = shellPart;
            dinfo.SetHitPart(shellPart);
            redirectedShellPart = shellPart;
            return true;
        }

        public static float ComputeEffectiveBoneDamageMultiplier(BoneNodeDef boneDef)
        {
            if (boneDef == null)
            {
                return 1f;
            }
            float dmgMult = Mathf.Clamp(boneDef.damageMultiplier, 0.05f, 5.0f);
            float armorMult = Mathf.Clamp(boneDef.armorMultiplier, 0.2f, 4.0f);
            float armorFactor = Mathf.Lerp(1f, 1f / armorMult, 0.5f);
            return Mathf.Clamp(dmgMult * armorFactor, 0.05f, 5.0f);
        }

        public static void ApplyBoneArmorAndWeakpointModifiers(
            Pawn giant,
            CompGiantRig comp,
            int boneIndex,
            ref DamageInfo dinfo,
            Vector3? hitWorldPos,
            out bool deflected)
        {
            deflected = false;
            if (giant == null || comp?.RigDef == null || dinfo.Amount <= 0f)
            {
                return;
            }

            BoneNodeDef[] nodes = comp.RigDef.SortedNodes;
            if ((uint)boneIndex >= (uint)nodes.Length)
            {
                return;
            }

            BoneNodeDef bDef = nodes[boneIndex];
            Vector3 vfxPos = hitWorldPos ?? CompGiantRig.GetSafeDrawPos(giant);
            if (!hitWorldPos.HasValue && comp.TryGetBoneUVWorldPos(boneIndex, new Vector2(0.5f, 0.5f), out Vector3 boneCenter, out _))
            {
                vfxPos = boneCenter;
            }

            if (comp.RigDef.enableBoneArmorAndWeakpoints)
            {
                if (bDef.deflectChance > 0.001f
                    && dinfo.Def != null
                    && dinfo.Def.ExternalViolenceFor(giant)
                    && dinfo.Def != DamageDefOf.Flame
                    && dinfo.Def != DamageDefOf.EMP
                    && Rand.Chance(Mathf.Clamp(bDef.deflectChance, 0f, 0.90f)))
                {
                    dinfo.SetAmount(0f);
                    deflected = true;
                    GiantVfxSystem.SpawnBoneHitVfx(giant, comp, boneIndex, vfxPos, isWeakpointHit: false, isDeflected: true);
                    return;
                }

                float effMult = ComputeEffectiveBoneDamageMultiplier(bDef);
                if (Mathf.Abs(effMult - 1f) > 0.005f)
                {
                    dinfo.SetAmount(Mathf.Max(1f, dinfo.Amount * effMult));
                }
            }

            bool isWeakpoint = bDef.damageMultiplier >= 1.25f;
            GiantVfxSystem.SpawnBoneHitVfx(giant, comp, boneIndex, vfxPos, isWeakpointHit: isWeakpoint, isDeflected: false);
        }

        public static void ExecuteMeleeCleaveAndKnockback(Verb_MeleeAttack verb, CompGiantRig casterComp)
        {
            if (isExecutingCleave || verb == null || casterComp?.Pawn == null || casterComp.RigDef == null)
            {
                return;
            }

            Pawn caster = casterComp.Pawn;
            Map map = caster.Map;
            LocalTargetInfo primaryTarget = verb.CurrentTarget;
            if (map == null || !caster.Spawned || !primaryTarget.IsValid)
            {
                return;
            }

            Vector3 casterPos = CompGiantRig.GetSafeDrawPos(caster);
            Vector3 targetPos = primaryTarget.HasThing
                ? (primaryTarget.Thing is Pawn tp ? CompGiantRig.GetSafeDrawPos(tp) : primaryTarget.Thing.DrawPos)
                : primaryTarget.Cell.ToVector3Shifted();
            Vector3 dir = targetPos - casterPos;
            dir.y = 0f;
            float bearingDeg = dir.sqrMagnitude > 0.01f
                ? Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg
                : casterComp.CurrentHeadingDeg;

            GiantRigDef rig = casterComp.RigDef;
            float arcRadius = Mathf.Max(1.6f, rig.cleaveRadius);
            float arcSweep = Mathf.Clamp(rig.cleaveArcDeg, 45f, 180f);
            // Regular melee attacks intentionally emit zero VFX (pure skeletal motion & hit physics only).

            if (rig.enableMeleeKnockback
                && rig.meleeKnockbackDistance > 0.1f
                && primaryTarget.Thing is Pawn primaryPawn
                && primaryPawn.Spawned
                && !primaryPawn.Dead)
            {
                TryKnockbackPawn(casterComp, primaryPawn, casterPos, rig.meleeKnockbackDistance);
            }

            if (!rig.enableMeleeCleave || rig.cleaveMaxTargets <= 0)
            {
                return;
            }

            DamageDef damDef = verb.verbProps?.meleeDamageDef ?? DamageDefOf.Cut;
            float basePower = verb.verbProps?.AdjustedMeleeDamageAmount(verb, caster) ?? 20f;
            float baseAp = verb.verbProps?.AdjustedArmorPenetration(verb, caster) ?? 0.35f;
            float cleaveDmg = Mathf.Max(2f, basePower * Mathf.Clamp(rig.cleaveDamageFactor, 0.1f, 1.5f));
            float halfArc = arcSweep * 0.5f;

            isExecutingCleave = true;
            try
            {
                int hitCount = 0;
                IntVec3 searchCenter = primaryTarget.Cell.IsValid ? primaryTarget.Cell : caster.Position;
                foreach (Thing cand in GenRadial.RadialDistinctThingsAround(searchCenter, map, arcRadius, useCenter: true))
                {
                    if (hitCount >= rig.cleaveMaxTargets)
                    {
                        break;
                    }
                    if (!(cand is Pawn victim) || victim == caster || victim == primaryTarget.Thing || victim.Dead || !victim.Spawned)
                    {
                        continue;
                    }
                    if (!caster.HostileTo(victim))
                    {
                        continue;
                    }
                    if (GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(caster, victim))
                    {
                        continue;
                    }

                    Vector3 toVictim = CompGiantRig.GetSafeDrawPos(victim) - casterPos;
                    toVictim.y = 0f;
                    if (toVictim.sqrMagnitude > 0.04f)
                    {
                        float victimBearing = Mathf.Atan2(toVictim.x, toVictim.z) * Mathf.Rad2Deg;
                        if (Mathf.Abs(Mathf.DeltaAngle(bearingDeg, victimBearing)) > halfArc)
                        {
                            continue;
                        }
                    }

                    if (!GiantPartCombatRuntime.RollVanillaMeleeHit(caster, victim, 1f, map, null, out _))
                    {
                        continue;
                    }

                    DamageInfo cleaveDinfo = new DamageInfo(
                        damDef,
                        cleaveDmg,
                        baseAp,
                        bearingDeg,
                        caster,
                        weapon: verb.EquipmentSource?.def);
                    victim.TakeDamage(cleaveDinfo);
                    hitCount++;

                    if (rig.enableMeleeKnockback
                        && rig.meleeKnockbackDistance > 0.1f
                        && victim.Spawned
                        && !victim.Dead)
                    {
                        TryKnockbackPawn(casterComp, victim, casterPos, rig.meleeKnockbackDistance * 0.75f);
                    }
                }
            }
            finally
            {
                isExecutingCleave = false;
            }
        }

        public static bool TryKnockbackPawn(CompGiantRig attackerComp, Pawn victim, Vector3 pushOriginWorld, float maxDistance)
        {
            if (victim == null || !victim.Spawned || victim.Dead || victim.Flying || victim.Map == null || maxDistance <= 0.1f)
            {
                return false;
            }
            if (victim.TryGetComp<CompGiantRig>() != null)
            {
                return false;
            }

            Map map = victim.Map;
            GiantFootprintMapComponent mapComp = map.GetComponent<GiantFootprintMapComponent>();
            Vector3 victimOrigin = victim.Position.ToVector3Shifted();
            Vector3 dir = victimOrigin - pushOriginWorld;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f)
            {
                float rad = (attackerComp?.CurrentHeadingDeg ?? 0f) * Mathf.Deg2Rad;
                dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            }
            dir.Normalize();

            District startDistrict = victim.Position.GetDistrict(map);
            IntVec3 bestCell = IntVec3.Invalid;
            IntVec3 lastCheckedCell = victim.Position;
            float clampedMax = Mathf.Clamp(maxDistance, 0.25f, 6f);
            int steps = Mathf.Clamp(Mathf.CeilToInt(clampedMax * 2f), 1, 12);
            for (int s = 1; s <= steps; s++)
            {
                float dist = (s / (float)steps) * clampedMax;
                IntVec3 cand = (victimOrigin + dir * dist).ToIntVec3();
                if (cand == lastCheckedCell)
                {
                    continue;
                }
                lastCheckedCell = cand;

                if (!cand.InBounds(map) || cand.Fogged(map) || !cand.Standable(map) || !cand.WalkableBy(map, victim))
                {
                    break;
                }
                if (cand.GetDoor(map) is Building_Door door && !door.Open)
                {
                    break;
                }
                if (startDistrict != null && cand.GetDistrict(map) != startDistrict)
                {
                    break;
                }
                if (!GenSight.LineOfSight(victim.Position, cand, map, skipFirstCell: true))
                {
                    break;
                }
                if ((mapComp != null && mapComp.IsBlockedForPawn(cand, victim)) || !cand.StandableBy(map, victim))
                {
                    continue;
                }
                bestCell = cand;
            }

            if (!bestCell.IsValid || bestCell == victim.Position)
            {
                return false;
            }

            victim.Position = bestCell;
            victim.Notify_Teleported(endCurrentJob: false, resetTweenedPos: false);
            return true;
        }

        public static void AffectGiantsInExplosionCell(
            DamageWorker worker,
            Explosion explosion,
            IntVec3 cell,
            List<Thing> damagedThings,
            List<Thing> ignoredThings)
        {
            if (worker == null || explosion?.Map == null || damagedThings == null)
            {
                return;
            }

            GiantFootprintMapComponent mapComp = explosion.Map.GetComponent<GiantFootprintMapComponent>();
            if (mapComp == null || !mapComp.HasAnyGiants)
            {
                return;
            }

            List<CompGiantRig> giants = mapComp.GetHitGiantsAt(cell);
            if (giants == null || giants.Count == 0)
            {
                return;
            }

            Dictionary<int, HashSet<int>> struckBonesByGiantId = explosionStruckBonesTable.GetOrCreateValue(explosion);

            for (int i = 0; i < giants.Count; i++)
            {
                CompGiantRig comp = giants[i];
                Pawn giant = comp?.Pawn;
                if (giant == null || !giant.Spawned || giant.Dead ||
                    (comp.RigDef != null && !comp.RigDef.enableMultiCellExplosionRouting))
                {
                    continue;
                }
                if (ignoredThings != null && ignoredThings.Contains(giant))
                {
                    continue;
                }

                int boneIdx = comp.Footprint.hitEntryByCell.TryGetValue(cell, out HitCellEntry entry)
                    ? entry.dominantBoneIndex
                    : comp.Footprint.FindClosestHitBoneToCell(explosion.Position);

                int giantId = giant.thingIDNumber;
                if (!struckBonesByGiantId.TryGetValue(giantId, out HashSet<int> struckBones))
                {
                    struckBones = new HashSet<int>();
                    struckBonesByGiantId[giantId] = struckBones;
                }

                if (!damagedThings.Contains(giant))
                {
                    if (boneIdx >= 0)
                    {
                        struckBones.Add(boneIdx);
                    }
                    BodyPartRecord part = comp.GetBodyPartForBone(boneIdx);
                    PendingBoneHitContext.ScopeState hitScope = PendingBoneHitContext.Push(giant, comp, boneIdx, part, cell.ToVector3Shifted());
                    try
                    {
                        if (ExplosionDamageThingFast != null)
                        {
                            ExplosionDamageThingFast(worker, explosion, giant, damagedThings, ignoredThings, cell);
                        }
                        else
                        {
                            ExplosionDamageThingMethod?.Invoke(worker, new object[] { explosion, giant, damagedThings, ignoredThings, cell });
                        }
                    }
                    finally
                    {
                        PendingBoneHitContext.Pop(in hitScope);
                    }
                    continue;
                }

                // Multi-limb shrapnel distribution: when a blast wave sweeps across additional exposed outer limb
                // bones on the same giant, deal localized secondary blast damage to up to 2 extra distinct limbs.
                if (explosion.damType == null || !explosion.damType.harmsHealth)
                {
                    continue;
                }
                int baseAmt = explosion.GetDamageAmountAt(cell);
                if (baseAmt <= 0)
                {
                    continue;
                }

                BoneNodeDef[] nodes = comp.RigDef?.SortedNodes;
                if (nodes == null ||
                    (uint)boneIdx >= (uint)nodes.Length ||
                    boneIdx == 0 ||
                    !nodes[boneIdx].hideWhenMissing ||
                    struckBones.Count >= MaxDistinctLimbBonesPerExplosion ||
                    !struckBones.Add(boneIdx))
                {
                    continue;
                }

                BodyPartRecord limbPart = comp.GetBodyPartForBone(boneIdx);
                if (limbPart == null || limbPart.coverageAbs <= 0f)
                {
                    continue;
                }

                int shrapnelAmt = Mathf.Max(1, Mathf.RoundToInt(baseAmt * SecondaryLimbShrapnelDamageFactor));

                float angle = (cell - explosion.Position).ToVector3().AngleFlat();
                DamageInfo limbBlastDinfo = new DamageInfo(
                    explosion.damType,
                    shrapnelAmt,
                    explosion.GetArmorPenetrationAt(cell),
                    angle,
                    explosion.instigator,
                    hitPart: limbPart,
                    weapon: explosion.weapon,
                    intendedTarget: explosion.intendedTarget);
                limbBlastDinfo.SetAllowDamagePropagation(false);

                ApplyBoneArmorAndWeakpointModifiers(
                    giant,
                    comp,
                    boneIdx,
                    ref limbBlastDinfo,
                    cell.ToVector3Shifted(),
                    out bool limbDeflected);
                if (!limbDeflected && limbBlastDinfo.Amount > 0f)
                {
                    comp.Notify_PreRoutedBoneHit(boneIdx);
                    giant.TakeDamage(limbBlastDinfo);
                }
            }
        }
    }
}
