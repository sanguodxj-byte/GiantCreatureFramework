using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GiantCreatureFramework
{
    public struct GiantHealthBarPartRowSpec
    {
        public bool IsSoftWhip;
        public int Index;
        public int Depth;
        public bool IsCore;
        public string LocalizedLabel;
    }

    public sealed class GiantBossHealthBarHUD
    {
        private enum BarLifecycleState
        {
            Entering,
            Active,
            Exiting
        }

        private sealed class TrackedGiantBarEntry
        {
            public int PawnThingId;
            public Pawn Pawn;
            public CompGiantRig RigComp;
            public GiantRigDef CachedRigDef;
            public GiantHealthBarDef StyleDef;
            public string CachedTitle = string.Empty;
            public BarLifecycleState State = BarLifecycleState.Entering;
            public float Presence01 = 0f;
            public bool Expanded = false;
            public float ExpandAnim01 = 0f;
            public float MainHealthPct = 1f;
            public float MainLagPct = 1f;
            public Vector2 ScrollPos = Vector2.zero;
            public List<GiantHealthBarPartRowSpec> RowSpecs = new List<GiantHealthBarPartRowSpec>();
            public float[] RowHealthPct = Array.Empty<float>();
            public float[] RowLagPct = Array.Empty<float>();
            public float[] RowMaxHp = Array.Empty<float>();
            public float CachedCoreMaxHp = 1f;
            public float CachedMaxHpScale = -1f;
            public int LastHealthSyncTick = -999;
            public int LastHediffCount = -1;
            public float LastSummaryHealthPct = -1f;
            public bool HudHooksResolved = false;
            public IGiantBossHudStateProvider HudStateProvider;
            public IGiantBossHudOverlayDrawer HudOverlayDrawer;
        }

        private const int MaxSimultaneousRenderedBars = 8;

        private static readonly GiantHealthBarDef DefaultFallbackStyleDef = new GiantHealthBarDef
        {
            defName = "GCF_HealthBar_FallbackDefault",
            label = "default boss health bar"
        };

        private static GiantHealthBarDef ResolveStyleDef(CompGiantRig comp)
        {
            if (comp?.RigDef == null)
            {
                return null;
            }
            if (comp.RigDef.healthBarDef != null)
            {
                return comp.RigDef.healthBarDef;
            }
            if (DefDatabase<GiantHealthBarDef>.DefCount > 0)
            {
                GiantHealthBarDef named = DefDatabase<GiantHealthBarDef>.GetNamedSilentFail("GCF_HealthBar_TopBossClean");
                if (named != null)
                {
                    return named;
                }
            }
            return DefaultFallbackStyleDef;
        }

        public static bool ShouldRenderOnMap(Map currentMap, Map componentMap, bool worldRendered)
        {
            return componentMap != null && currentMap == componentMap && !worldRendered;
        }

        private static readonly string[] PercentStrings = BuildPercentStrings();

        private static string[] BuildPercentStrings()
        {
            var arr = new string[101];
            for (int i = 0; i <= 100; i++)
            {
                arr[i] = i.ToString() + "%";
            }
            return arr;
        }

        private static string FormatPercentCached(float pct01)
        {
            int idx = Mathf.Clamp(Mathf.RoundToInt(pct01 * 100f), 0, 100);
            return PercentStrings[idx];
        }

        private readonly List<TrackedGiantBarEntry> entries = new List<TrackedGiantBarEntry>();
        private readonly List<TrackedGiantBarEntry> renderSortScratch = new List<TrackedGiantBarEntry>(8);
        private readonly Dictionary<int, TrackedGiantBarEntry> entriesByPawnId = new Dictionary<int, TrackedGiantBarEntry>();
        private Selector activeSortSelector;
        private readonly Comparison<TrackedGiantBarEntry> renderSortComparison;
        private float lastRealtimeStamp = -1f;
        private float pendingManualDt = 0f;
        private float arenaArmyPresence01 = 0f;
        private float arenaArmyLagA = 1f;
        private float arenaArmyLagB = 1f;

        public GiantBossHealthBarHUD()
        {
            renderSortComparison = CompareBarEntriesForRender;
        }

        private int ComputeBarRenderPriorityScore(TrackedGiantBarEntry e)
        {
            int s = 0;
            if (activeSortSelector != null && e.Pawn != null && activeSortSelector.IsSelected(e.Pawn)) s += 1000;
            if (e.Expanded) s += 500;
            if (e.State == BarLifecycleState.Exiting) s += 250;
            if (e.MainHealthPct < 0.999f) s += 100 + Mathf.RoundToInt((1f - e.MainHealthPct) * 80f);
            return s;
        }

        private int CompareBarEntriesForRender(TrackedGiantBarEntry a, TrackedGiantBarEntry b)
        {
            return ComputeBarRenderPriorityScore(b).CompareTo(ComputeBarRenderPriorityScore(a));
        }

        public static bool ManualStepMode = false;
        internal static bool IsDrawingArenaVersusHud = false;
        public bool HasVisibleEntries => entries.Count > 0;

        public void QueueManualStepSeconds(float dtSeconds)
        {
            pendingManualDt += Mathf.Max(0f, dtSeconds);
        }

        public void SetAllExpanded(bool expanded)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].State != BarLifecycleState.Exiting)
                {
                    entries[i].Expanded = expanded;
                }
            }
        }

        public static float EaseOutCubic(float t)
        {
            float u = 1f - Mathf.Clamp01(t);
            return 1f - u * u * u;
        }

        public static float ComputeWeightedBossHealthPercent(
            float coreHpPct,
            float sumPartsHp,
            float sumPartsMaxHp,
            bool isDead)
        {
            if (isDead || coreHpPct <= 0f)
            {
                return 0f;
            }
            float clampedCore = Mathf.Clamp01(coreHpPct);
            float totalPct = sumPartsMaxHp > 0.001f ? Mathf.Clamp01(sumPartsHp / sumPartsMaxHp) : clampedCore;
            return Mathf.Clamp01(clampedCore * 0.45f + totalPct * 0.55f);
        }

        public static float ReconcileBossHealthPercent(
            float weightedRigPct,
            float allBodyPartsPct,
            float lethalIntegrityPct,
            bool preventPrematureLethalDeath,
            bool isDead)
        {
            if (isDead || weightedRigPct <= 0f)
            {
                return 0f;
            }
            float anatomicalPct = Mathf.Min(Mathf.Clamp01(weightedRigPct), Mathf.Clamp01(allBodyPartsPct));
            float clampedLethal = Mathf.Clamp01(lethalIntegrityPct);
            float effectiveLethalCap = preventPrematureLethalDeath
                ? Mathf.Lerp(0.20f, 1f, clampedLethal)
                : Mathf.Max(0.01f, clampedLethal);
            return Mathf.Clamp01(Mathf.Min(anatomicalPct, effectiveLethalCap));
        }

        public static float ComputeSynchronizedGiantHealthPercent(Pawn pawn, CompGiantRig comp)
        {
            if (pawn == null || pawn.Dead || pawn.health?.hediffSet == null)
            {
                return 0f;
            }

            HediffSet hediffSet = pawn.health.hediffSet;
            if (comp != null &&
                GiantCombatMechanicsUtility.HasDestroyedLethalCoreBone(pawn, comp) &&
                !(GiantFastLookup.TryGetPhaseTransform(pawn) is CompGiantPhaseTransform phaseComp && phaseComp.IsTransforming))
            {
                return 0f;
            }

            List<Hediff> hediffs = hediffSet.hediffs;
            if (hediffs == null || hediffs.Count == 0)
            {
                return 1f;
            }

            BodyPartRecord coreRec = comp?.GetLinkedBodyPartRecord(0) ?? pawn.RaceProps?.body?.corePart;
            float corePct = 1f;
            if (coreRec != null)
            {
                if (hediffSet.PartIsMissing(coreRec))
                {
                    return 0f;
                }
                float coreMax = Mathf.Max(1f, coreRec.def.GetMaxHealth(pawn));
                corePct = Mathf.Clamp01(hediffSet.GetPartHealth(coreRec) / coreMax);
            }

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
                    float maxHp = Mathf.Max(1f, rec.def.GetMaxHealth(pawn));
                    bool missing = (!comp.IsBoneVisibleThreadSafe(i) && nodes[i].hideWhenMissing) || hediffSet.PartIsMissing(rec);
                    sumRigHp += missing ? 0f : Mathf.Clamp(hediffSet.GetPartHealth(rec), 0f, maxHp);
                    sumRigMax += maxHp;
                }
            }

            float weightedRigPct = ComputeWeightedBossHealthPercent(corePct, sumRigHp, sumRigMax, pawn.Dead);

            float sumBodyHp = 0f;
            float sumBodyMax = 0f;
            List<BodyPartRecord> allParts = pawn.RaceProps?.body?.AllParts;
            if (allParts != null && allParts.Count > 0)
            {
                for (int i = 0; i < allParts.Count; i++)
                {
                    BodyPartRecord p = allParts[i];
                    if (p?.def == null)
                    {
                        continue;
                    }
                    float pMax = Mathf.Max(1f, p.def.GetMaxHealth(pawn));
                    sumBodyHp += hediffSet.PartIsMissing(p) ? 0f : Mathf.Clamp(hediffSet.GetPartHealth(p), 0f, pMax);
                    sumBodyMax += pMax;
                }
            }
            float allBodyPct = sumBodyMax > 0.001f ? Mathf.Clamp01(sumBodyHp / sumBodyMax) : weightedRigPct;

            float totalInjurySeverity = 0f;
            for (int i = 0; i < hediffs.Count; i++)
            {
                if (hediffs[i] is Hediff_Injury inj && inj.Severity > 0f)
                {
                    totalInjurySeverity += inj.Severity;
                }
            }
            float lethalThresh = Mathf.Max(1f, pawn.health.LethalDamageThreshold);
            float lethalIntegrityPct = (comp?.RigDef != null && comp.RigDef.onlyDieWhenCoreDestroyed)
                ? 1f
                : Mathf.Clamp01(1f - totalInjurySeverity / lethalThresh);
            bool preventPremature = comp != null && GiantCombatMechanicsUtility.ShouldPreventPrematureLethalThresholdDeath(pawn, comp);

            return ReconcileBossHealthPercent(weightedRigPct, allBodyPct, lethalIntegrityPct, preventPremature, pawn.Dead);
        }

        private static string SafeCapitalizeLabel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }
            string trimmed = raw.Trim();
            char first = trimmed[0];
            if (char.IsLower(first))
            {
                return char.ToUpperInvariant(first) + trimmed.Substring(1);
            }
            return trimmed;
        }

        /// <summary>
        /// Resolves the localized unit title from Def definitions (never exposing defName or internal field names).
        /// </summary>
        public static string ResolveLocalizedBossTitle(Pawn pawn, GiantRigDef rigDef)
        {
            if (!string.IsNullOrWhiteSpace(rigDef?.healthBarTitleOverride))
            {
                return SafeCapitalizeLabel(rigDef.healthBarTitleOverride);
            }
            if (pawn?.def != null && !string.IsNullOrWhiteSpace(pawn.def.label))
            {
                return SafeCapitalizeLabel(pawn.def.label);
            }
            if (pawn?.kindDef != null && !string.IsNullOrWhiteSpace(pawn.kindDef.label))
            {
                return SafeCapitalizeLabel(pawn.kindDef.label);
            }
            if (rigDef != null && !string.IsNullOrWhiteSpace(rigDef.label))
            {
                return SafeCapitalizeLabel(rigDef.label);
            }
            return string.Empty;
        }

        /// <summary>
        /// Resolves the localized anatomical BodyPartRecord label (never exposing nodeId or BodyPartDef.defName).
        /// </summary>
        public static string ResolveLocalizedPartRowLabel(
            BodyPartRecord part,
            BoneNodeDef boneDef,
            SoftWhipDef whipDef = null)
        {
            if (part != null)
            {
                if (!string.IsNullOrWhiteSpace(part.customLabel))
                {
                    return SafeCapitalizeLabel(part.customLabel);
                }
                if (part.def != null && !string.IsNullOrWhiteSpace(part.def.label))
                {
                    return SafeCapitalizeLabel(part.def.label);
                }
            }
            if (boneDef != null && !string.IsNullOrWhiteSpace(boneDef.label))
            {
                return SafeCapitalizeLabel(boneDef.label);
            }
            if (whipDef != null && !string.IsNullOrWhiteSpace(whipDef.tendrilBodyPartCustomLabel))
            {
                return SafeCapitalizeLabel(whipDef.tendrilBodyPartCustomLabel);
            }
            if (whipDef?.tendrilBodyPart != null && !string.IsNullOrWhiteSpace(whipDef.tendrilBodyPart.label))
            {
                return SafeCapitalizeLabel(whipDef.tendrilBodyPart.label);
            }
            return string.Empty;
        }

        public static List<GiantHealthBarPartRowSpec> BuildPartTreeRowSpecs(
            GiantRigDef rigDef,
            BodyPartRecord[] boneParts,
            BodyPartRecord[] whipParts)
        {
            var rows = new List<GiantHealthBarPartRowSpec>();
            if (rigDef?.SortedNodes == null || rigDef.SortedNodes.Length == 0)
            {
                return rows;
            }

            BoneNodeDef[] nodes = rigDef.SortedNodes;
            int count = nodes.Length;
            int[] boneDepths = new int[count];
            var seenParts = new HashSet<BodyPartRecord>();

            for (int i = 0; i < count; i++)
            {
                BoneNodeDef node = nodes[i];
                int pIdx = node.parentRuntimeIndex;
                boneDepths[i] = pIdx >= 0 ? boneDepths[pIdx] + 1 : 0;

                BodyPartRecord rec = (boneParts != null && i < boneParts.Length) ? boneParts[i] : null;
                if (rec == null && node.linkedBodyPart == null)
                {
                    continue;
                }

                // Deduplicate non-severable structural backing plates (e.g. lower_hull_tub sharing hull_chassis part)
                if (rec != null && !node.hideWhenMissing && !seenParts.Add(rec))
                {
                    boneDepths[i] = pIdx >= 0 ? boneDepths[pIdx] : 0;
                    continue;
                }
                if (rec != null)
                {
                    seenParts.Add(rec);
                }

                string label = ResolveLocalizedPartRowLabel(rec, node, null);
                if (string.IsNullOrEmpty(label))
                {
                    continue;
                }

                rows.Add(new GiantHealthBarPartRowSpec
                {
                    IsSoftWhip = false,
                    Index = i,
                    Depth = Mathf.Clamp(boneDepths[i], 0, 5),
                    IsCore = i == 0 || !node.hideWhenMissing || node.lethalWhenDestroyed,
                    LocalizedLabel = label
                });

                // Insert any continuum soft-whip tendrils anchored directly to this bone right beneath it
                if (rigDef.softWhips != null)
                {
                    for (int s = 0; s < rigDef.softWhips.Count; s++)
                    {
                        SoftWhipDef whip = rigDef.softWhips[s];
                        if (whip == null || whip.resolvedRootBoneIndex != i)
                        {
                            continue;
                        }
                        BodyPartRecord wRec = (whipParts != null && s < whipParts.Length) ? whipParts[s] : null;
                        string wLabel = ResolveLocalizedPartRowLabel(wRec, null, whip);
                        if (string.IsNullOrEmpty(wLabel))
                        {
                            continue;
                        }
                        rows.Add(new GiantHealthBarPartRowSpec
                        {
                            IsSoftWhip = true,
                            Index = s,
                            Depth = Mathf.Clamp(boneDepths[i] + 1, 1, 5),
                            IsCore = false,
                            LocalizedLabel = wLabel
                        });
                    }
                }
            }

            return rows;
        }

        private static bool IsPawnConcealedFromBossHud(Pawn pawn)
        {
            if (pawn == null)
            {
                return true;
            }
            Map map = pawn.Map;
            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            bool airborneAboveFog = rig != null && (rig.IsCurrentlyAirborne || rig.IsFlyingEntry);
            if (!airborneAboveFog && map?.fogGrid != null && pawn.Position.InBounds(map) && pawn.Position.Fogged(map))
            {
                return true;
            }
            if (Current.Game?.World?.factionManager != null && pawn.health?.hediffSet?.hediffs != null && pawn.IsHiddenFromPlayer())
            {
                return true;
            }
            if (GiantDormantLockRegistry.IsLocked(pawn))
            {
                return true;
            }
            return false;
        }

        public static bool ShouldDisplayBossHealthBar(Pawn pawn, CompGiantRig comp)
        {
            if (pawn == null || comp == null || pawn.Dead || pawn.Destroyed)
            {
                return false;
            }
            if (GiantVehicleBehaviorGuard.IsPlayerFaction(pawn.Faction))
            {
                return false;
            }
            if (IsPawnConcealedFromBossHud(pawn))
            {
                return false;
            }
            return ResolveStyleDef(comp) != null;
        }

        public void NotifyGiantRegistered(CompGiantRig comp)
        {
            if (comp?.Pawn == null || !comp.Pawn.Spawned || !ShouldDisplayBossHealthBar(comp.Pawn, comp))
            {
                return;
            }
            GiantHealthBarDef style = ResolveStyleDef(comp);
            if (style == null)
            {
                return;
            }
            EnsureTrackedEntry(comp, style);
        }

        public void NotifyGiantUnregistered(CompGiantRig comp)
        {
            if (comp?.Pawn == null)
            {
                return;
            }
            if (entriesByPawnId.TryGetValue(comp.Pawn.thingIDNumber, out TrackedGiantBarEntry entry) &&
                entry.State != BarLifecycleState.Exiting)
            {
                entry.Expanded = false;
                entry.State = BarLifecycleState.Exiting;
            }
        }

        public void Clear()
        {
            entries.Clear();
            entriesByPawnId.Clear();
            lastRealtimeStamp = -1f;
            arenaArmyPresence01 = 0f;
            arenaArmyLagA = 1f;
            arenaArmyLagB = 1f;
        }

        private TrackedGiantBarEntry EnsureTrackedEntry(CompGiantRig comp, GiantHealthBarDef style)
        {
            Pawn pawn = comp.Pawn;
            int id = pawn.thingIDNumber;
            if (entriesByPawnId.TryGetValue(id, out TrackedGiantBarEntry existing))
            {
                existing.Pawn = pawn;
                existing.RigComp = comp;
                existing.StyleDef = style;
                if (existing.State == BarLifecycleState.Exiting && !pawn.Dead && pawn.Spawned)
                {
                    existing.State = BarLifecycleState.Entering;
                }
                if (existing.CachedRigDef != comp.RigDef)
                {
                    RebuildEntryRows(existing, comp);
                }
                return existing;
            }

            var created = new TrackedGiantBarEntry
            {
                PawnThingId = id,
                Pawn = pawn,
                RigComp = comp,
                StyleDef = style,
                State = BarLifecycleState.Entering,
                Presence01 = 0f,
                Expanded = style.defaultExpanded,
                ExpandAnim01 = style.defaultExpanded ? 1f : 0f
            };
            RebuildEntryRows(created, comp);
            UpdateEntryHealthSnapshot(created, force: true);
            created.MainLagPct = created.MainHealthPct;
            for (int i = 0; i < created.RowLagPct.Length; i++)
            {
                created.RowLagPct[i] = created.RowHealthPct[i];
            }
            entries.Add(created);
            entriesByPawnId[id] = created;
            return created;
        }

        private static void RebuildEntryRows(TrackedGiantBarEntry entry, CompGiantRig comp)
        {
            entry.CachedRigDef = comp.RigDef;
            entry.CachedTitle = ResolveLocalizedBossTitle(comp.Pawn, comp.RigDef);
            entry.RowSpecs = BuildPartTreeRowSpecs(comp.RigDef, comp.LinkedBodyPartsArray, comp.LinkedSoftWhipPartsArray);
            int rowCount = entry.RowSpecs.Count;
            entry.RowHealthPct = new float[rowCount];
            entry.RowLagPct = new float[rowCount];
            entry.RowMaxHp = new float[rowCount];
            entry.CachedMaxHpScale = -1f;
            entry.LastHealthSyncTick = -999;
            entry.LastHediffCount = -1;
            entry.LastSummaryHealthPct = -1f;
            Array.Fill(entry.RowHealthPct, 1f);
            Array.Fill(entry.RowLagPct, 1f);
        }

        private static void EnsureCachedRowMaxHp(TrackedGiantBarEntry entry, Pawn pawn, CompGiantRig comp)
        {
            float scale = pawn.HealthScale;
            if (Mathf.Approximately(scale, entry.CachedMaxHpScale) && entry.RowMaxHp.Length == entry.RowSpecs.Count)
            {
                return;
            }
            BodyPartRecord coreRec = comp.GetLinkedBodyPartRecord(0) ?? pawn.RaceProps?.body?.corePart;
            entry.CachedCoreMaxHp = coreRec != null ? Mathf.Max(1f, coreRec.def.GetMaxHealth(pawn)) : 1f;
            if (entry.RowMaxHp.Length != entry.RowSpecs.Count)
            {
                entry.RowMaxHp = new float[entry.RowSpecs.Count];
            }
            for (int r = 0; r < entry.RowSpecs.Count; r++)
            {
                GiantHealthBarPartRowSpec spec = entry.RowSpecs[r];
                BodyPartRecord rec = spec.IsSoftWhip
                    ? comp.GetLinkedSoftWhipBodyPartRecord(spec.Index)
                    : comp.GetLinkedBodyPartRecord(spec.Index);
                entry.RowMaxHp[r] = rec != null ? Mathf.Max(1f, rec.def.GetMaxHealth(pawn)) : 1f;
            }
            entry.CachedMaxHpScale = scale;
        }

        private static void UpdateEntryHealthSnapshot(TrackedGiantBarEntry entry, bool force = false)
        {
            Pawn pawn = entry.Pawn;
            CompGiantRig comp = entry.RigComp;
            if (pawn == null || comp?.RigDef == null || pawn.Dead || pawn.health?.hediffSet == null)
            {
                entry.MainHealthPct = 0f;
                if (entry.RowHealthPct != null)
                {
                    Array.Clear(entry.RowHealthPct, 0, entry.RowHealthPct.Length);
                }
                entry.LastHediffCount = -2;
                return;
            }

            bool rigChanged = entry.CachedRigDef != comp.RigDef;
            if (rigChanged)
            {
                RebuildEntryRows(entry, comp);
            }
            else if (string.IsNullOrEmpty(entry.CachedTitle))
            {
                entry.CachedTitle = ResolveLocalizedBossTitle(pawn, comp.RigDef);
            }

            int curTick = Current.Game?.tickManager?.TicksGame ?? 0;
            int hediffCount = pawn.health.hediffSet.hediffs?.Count ?? 0;
            float summaryPct = pawn.health.summaryHealth?.SummaryHealthPercent ?? 1f;

            if (!force
                && !ManualStepMode
                && !rigChanged
                && curTick == entry.LastHealthSyncTick
                && hediffCount == entry.LastHediffCount
                && Mathf.Approximately(summaryPct, entry.LastSummaryHealthPct))
            {
                return;
            }

            entry.LastHealthSyncTick = curTick;
            entry.LastHediffCount = hediffCount;
            entry.LastSummaryHealthPct = summaryPct;

            if (hediffCount == 0)
            {
                entry.MainHealthPct = 1f;
                for (int r = 0; r < entry.RowHealthPct.Length; r++)
                {
                    entry.RowHealthPct[r] = 1f;
                }
                return;
            }

            EnsureCachedRowMaxHp(entry, pawn, comp);

            float corePct = 1f;
            BodyPartRecord coreRec = comp.GetLinkedBodyPartRecord(0) ?? pawn.RaceProps?.body?.corePart;
            if (coreRec != null)
            {
                if (pawn.health.hediffSet.PartIsMissing(coreRec))
                {
                    corePct = 0f;
                }
                else
                {
                    corePct = Mathf.Clamp01(pawn.health.hediffSet.GetPartHealth(coreRec) / entry.CachedCoreMaxHp);
                }
            }

            float sumHp = 0f;
            float sumMax = 0f;
            for (int r = 0; r < entry.RowSpecs.Count; r++)
            {
                GiantHealthBarPartRowSpec spec = entry.RowSpecs[r];
                BodyPartRecord rec = spec.IsSoftWhip
                    ? comp.GetLinkedSoftWhipBodyPartRecord(spec.Index)
                    : comp.GetLinkedBodyPartRecord(spec.Index);
                float rowPct;
                if (rec != null)
                {
                    float maxHp = entry.RowMaxHp[r];
                    bool missing = spec.IsSoftWhip
                        ? !comp.IsSoftWhipVisibleThreadSafe(spec.Index) || pawn.health.hediffSet.PartIsMissing(rec)
                        : (!comp.IsBoneVisibleThreadSafe(spec.Index) && comp.RigDef.SortedNodes[spec.Index].hideWhenMissing)
                            || pawn.health.hediffSet.PartIsMissing(rec);
                    float curHp = missing ? 0f : Mathf.Clamp(pawn.health.hediffSet.GetPartHealth(rec), 0f, maxHp);
                    rowPct = curHp / maxHp;
                    sumHp += curHp;
                    sumMax += maxHp;
                }
                else
                {
                    rowPct = spec.IsSoftWhip
                        ? comp.GetSoftWhipHealthPercentThreadSafe(spec.Index)
                        : comp.GetBoneHealthPercentThreadSafe(spec.Index);
                }
                entry.RowHealthPct[r] = Mathf.Clamp01(rowPct);
            }

            float weightedPct = ComputeWeightedBossHealthPercent(corePct, sumHp, sumMax, pawn.Dead);
            float syncedPct = ComputeSynchronizedGiantHealthPercent(pawn, comp);
            entry.MainHealthPct = Mathf.Min(weightedPct, syncedPct);
        }

        public static float ComputeSafeTopScreenYForMode(float baseColonistBottomY, float extraTopMargin, bool devModeActive)
        {
            float baseY = devModeActive ? 42f : 18f;
            if (baseColonistBottomY > baseY)
            {
                baseY = baseColonistBottomY;
            }
            return baseY + Mathf.Max(4f, extraTopMargin);
        }

        private static float ComputeSafeTopScreenY(float extraTopMargin)
        {
            float maxColonistBottom = 0f;
            if (Find.PlaySettings != null && Find.PlaySettings.showColonistBar && Find.ColonistBar != null)
            {
                List<ColonistBar.Entry> cbEntries = Find.ColonistBar.Entries;
                List<Vector2> locs = Find.ColonistBar.DrawLocs;
                if (cbEntries != null && locs != null && cbEntries.Count > 0 && locs.Count == cbEntries.Count)
                {
                    float slotH = Find.ColonistBar.Size.y;
                    for (int i = 0; i < locs.Count; i++)
                    {
                        float bottom = locs[i].y + slotH + 18f;
                        if (bottom > maxColonistBottom)
                        {
                            maxColonistBottom = bottom;
                        }
                    }
                }
            }
            return ComputeSafeTopScreenYForMode(maxColonistBottom, extraTopMargin, Prefs.DevMode);
        }

        public void DrawOnGUI(Map map, IReadOnlyList<CompGiantRig> activeGiants)
        {
            if (Event.current == null || !ShouldRenderOnMap(Find.CurrentMap, map, WorldRendererUtility.WorldRendered))
            {
                return;
            }

            // Synchronize all active non-player spawned giants on the map (player-faction giants never show boss health bars)
            if (activeGiants != null)
            {
                for (int i = 0; i < activeGiants.Count; i++)
                {
                    CompGiantRig comp = activeGiants[i];
                    if (comp?.Pawn == null || !comp.Pawn.Spawned || comp.Pawn.Map != map || !ShouldDisplayBossHealthBar(comp.Pawn, comp))
                    {
                        continue;
                    }
                    GiantHealthBarDef style = ResolveStyleDef(comp);
                    if (style != null)
                    {
                        EnsureTrackedEntry(comp, style);
                    }
                }
            }

            bool renderArenaVersus = GiantArenaService.ShouldRenderVersusHud(map);
            bool hasArmyBar = renderArenaVersus && (
                GiantArenaService.ShouldUseArmyAggregateBar(GiantArenaSide.SideA)
                || GiantArenaService.ShouldUseArmyAggregateBar(GiantArenaSide.SideB));

            if (entries.Count == 0 && !hasArmyBar)
            {
                lastRealtimeStamp = Time.realtimeSinceStartup;
                return;
            }

            float now = Time.realtimeSinceStartup;
            float dt;
            if (ManualStepMode)
            {
                dt = Event.current.type == EventType.Repaint ? pendingManualDt : 0f;
                if (Event.current.type == EventType.Repaint)
                {
                    pendingManualDt = 0f;
                    lastRealtimeStamp = now;
                }
            }
            else
            {
                dt = Event.current.type == EventType.Repaint
                    ? (lastRealtimeStamp >= 0f ? Mathf.Clamp(now - lastRealtimeStamp, 0.001f, 0.08f) : 0.016f)
                    : 0f;
                if (Event.current.type == EventType.Repaint)
                {
                    lastRealtimeStamp = now;
                }
            }

            GiantHealthBarDef primaryStyle = entries.Count > 0 ? (entries[0].StyleDef ?? DefaultFallbackStyleDef) : DefaultFallbackStyleDef;
            float curY = ComputeSafeTopScreenY(primaryStyle.topScreenMargin);
            float screenCenterX = UI.screenWidth * 0.5f;

            if (hasArmyBar && dt > 0f)
            {
                float enterDur = Mathf.Max(0.05f, primaryStyle.enterExpandSeconds);
                arenaArmyPresence01 = Mathf.Min(1f, arenaArmyPresence01 + dt / enterDur);
                float armyLagStep = Mathf.Max(0.10f, primaryStyle.damageLagDecayPerSecond) * dt;
                if (GiantArenaService.TryGetArmyBarSummary(GiantArenaSide.SideA, out _, out float pctA, out _, out _, out _))
                {
                    arenaArmyLagA = arenaArmyLagA > pctA ? Mathf.Max(pctA, arenaArmyLagA - armyLagStep) : pctA;
                }
                if (GiantArenaService.TryGetArmyBarSummary(GiantArenaSide.SideB, out _, out float pctB, out _, out _, out _))
                {
                    arenaArmyLagB = arenaArmyLagB > pctB ? Mathf.Max(pctB, arenaArmyLagB - armyLagStep) : pctB;
                }
            }

            GameFont prevFont = Text.Font;
            TextAnchor prevAnchor = Text.Anchor;
            bool prevWordWrap = Text.WordWrap;
            Color prevGuiColor = GUI.color;

            try
            {
                Text.WordWrap = false;
                renderSortScratch.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    TrackedGiantBarEntry entry = entries[i];
                    GiantHealthBarDef def = entry.StyleDef;
                    if (def == null
                        || (entry.Pawn != null && (GiantVehicleBehaviorGuard.IsPlayerFaction(entry.Pawn.Faction) || IsPawnConcealedFromBossHud(entry.Pawn))))
                    {
                        entriesByPawnId.Remove(entry.PawnThingId);
                        entries.RemoveAt(i--);
                        continue;
                    }

                    bool stillPresent = entry.Pawn != null
                        && entry.Pawn.Spawned
                        && entry.Pawn.Map == map
                        && ShouldDisplayBossHealthBar(entry.Pawn, entry.RigComp);

                    if (!stillPresent && entry.State != BarLifecycleState.Exiting)
                    {
                        entry.Expanded = false;
                        entry.State = BarLifecycleState.Exiting;
                    }

                    UpdateEntryHealthSnapshot(entry);

                    if (dt > 0f)
                    {
                        if (entry.State == BarLifecycleState.Entering)
                        {
                            float enterDur = Mathf.Max(0.05f, def.enterExpandSeconds);
                            entry.Presence01 = Mathf.Min(1f, entry.Presence01 + dt / enterDur);
                            if (entry.Presence01 >= 1f)
                            {
                                entry.State = BarLifecycleState.Active;
                            }
                        }
                        else if (entry.State == BarLifecycleState.Exiting)
                        {
                            float exitDur = Mathf.Max(0.05f, def.exitCollapseSeconds);
                            entry.Presence01 = Mathf.Max(0f, entry.Presence01 - dt / exitDur);
                        }

                        float lagStep = Mathf.Max(0.10f, def.damageLagDecayPerSecond) * dt;
                        entry.MainLagPct = entry.MainLagPct > entry.MainHealthPct
                            ? Mathf.Max(entry.MainHealthPct, entry.MainLagPct - lagStep)
                            : entry.MainHealthPct;

                        if (entry.ExpandAnim01 > 0.005f || entry.Expanded)
                        {
                            for (int r = 0; r < entry.RowLagPct.Length && r < entry.RowHealthPct.Length; r++)
                            {
                                entry.RowLagPct[r] = entry.RowLagPct[r] > entry.RowHealthPct[r]
                                    ? Mathf.Max(entry.RowHealthPct[r], entry.RowLagPct[r] - lagStep * 1.15f)
                                    : entry.RowHealthPct[r];
                            }
                        }

                        float targetDrawer = (entry.Expanded && entry.State == BarLifecycleState.Active) ? 1f : 0f;
                        entry.ExpandAnim01 = Mathf.MoveTowards(entry.ExpandAnim01, targetDrawer, Mathf.Max(2f, def.drawerToggleSpeed) * dt * 0.45f);
                    }

                    if (entry.State == BarLifecycleState.Exiting && entry.Presence01 <= 0.001f)
                    {
                        entriesByPawnId.Remove(entry.PawnThingId);
                        entries.RemoveAt(i--);
                        continue;
                    }

                    renderSortScratch.Add(entry);
                }

                if (renderSortScratch.Count > MaxSimultaneousRenderedBars)
                {
                    activeSortSelector = Find.Selector;
                    renderSortScratch.Sort(renderSortComparison);
                    activeSortSelector = null;
                }

                int drawLimit = Math.Min(renderSortScratch.Count, MaxSimultaneousRenderedBars);
                if (renderArenaVersus && (drawLimit >= 1 || hasArmyBar))
                {
                    DrawArenaVersusLayout(
                        renderSortScratch,
                        drawLimit,
                        screenCenterX,
                        curY,
                        primaryStyle,
                        arenaArmyPresence01,
                        arenaArmyLagA,
                        arenaArmyLagB);
                }
                else if (drawLimit >= 3)
                {
                    float colGap = 36f;
                    float colW = Mathf.Clamp((UI.screenWidth - 240f) * 0.36f, 200f, 320f);
                    float leftCenterX = screenCenterX - (colW + colGap) * 0.5f;
                    float rightCenterX = screenCenterX + (colW + colGap) * 0.5f;
                    float minMultiColTopY = Prefs.DevMode ? 42f : 10f;
                    float rowY = Mathf.Max(minMultiColTopY, curY - 12f);
                    for (int i = 0; i < drawLimit; i += 2)
                    {
                        TrackedGiantBarEntry leftEntry = renderSortScratch[i];
                        float hLeft = DrawSingleBossBar(leftEntry, leftEntry.StyleDef, leftCenterX, rowY, compactWidthOverride: colW);
                        float hRight = 0f;
                        if (i + 1 < drawLimit)
                        {
                            TrackedGiantBarEntry rightEntry = renderSortScratch[i + 1];
                            hRight = DrawSingleBossBar(rightEntry, rightEntry.StyleDef, rightCenterX, rowY, compactWidthOverride: colW);
                        }
                        rowY += Mathf.Max(hLeft, hRight) + 4f;
                    }
                }
                else
                {
                    for (int i = 0; i < drawLimit; i++)
                    {
                        TrackedGiantBarEntry entry = renderSortScratch[i];
                        curY += DrawSingleBossBar(entry, entry.StyleDef, screenCenterX, curY) + entry.StyleDef.stackSpacing;
                    }
                }
            }
            finally
            {
                GUI.color = prevGuiColor;
                Text.Font = prevFont;
                Text.Anchor = prevAnchor;
                Text.WordWrap = prevWordWrap;
            }
        }

        private static void DrawArenaVersusLayout(
            List<TrackedGiantBarEntry> items,
            int drawLimit,
            float screenCenterX,
            float startY,
            GiantHealthBarDef fallbackStyle,
            float armyPresence01,
            float armyLagA,
            float armyLagB)
        {
            bool leftIsArmy = GiantArenaService.ShouldUseArmyAggregateBar(GiantArenaSide.SideA);
            bool rightIsArmy = GiantArenaService.ShouldUseArmyAggregateBar(GiantArenaSide.SideB);

            var leftSide = new List<TrackedGiantBarEntry>(4);
            var rightSide = new List<TrackedGiantBarEntry>(4);
            for (int i = 0; i < drawLimit; i++)
            {
                TrackedGiantBarEntry e = items[i];
                if (GiantArenaService.TryGetFighterSide(e.Pawn, out GiantArenaSide side, out _))
                {
                    if (side == GiantArenaSide.SideB)
                    {
                        if (!rightIsArmy) rightSide.Add(e);
                    }
                    else
                    {
                        if (!leftIsArmy) leftSide.Add(e);
                    }
                }
                else
                {
                    if (!leftIsArmy && leftSide.Count <= rightSide.Count) leftSide.Add(e);
                    else if (!rightIsArmy) rightSide.Add(e);
                }
            }

            float vsBadgeW = 52f;
            float colGap = GiantArenaService.IsCinematicHudOnly ? 14f : 38f;
            float maxSideW = Mathf.Clamp((UI.screenWidth - vsBadgeW - colGap * 2f - 100f) * 0.48f, 220f, 440f);
            float leftCenterX = screenCenterX - vsBadgeW * 0.5f - colGap - maxSideW * 0.5f;
            float rightCenterX = screenCenterX + vsBadgeW * 0.5f + colGap + maxSideW * 0.5f;
            float leftBarLeftX = Mathf.Round(leftCenterX - maxSideW * 0.5f);
            float rightBarLeftX = Mathf.Round(rightCenterX - maxSideW * 0.5f);
            float topY = Prefs.DevMode ? Mathf.Max(42f, startY) : Mathf.Max(14f, startY - 6f);
            int leftRows = leftIsArmy ? 1 : leftSide.Count;
            int rightRows = rightIsArmy ? 1 : rightSide.Count;
            bool useCompactRows = leftRows > 1 || rightRows > 1;

            Color crimsonAccent = new Color(0.86f, 0.25f, 0.24f, 0.96f);
            Color azureAccent = new Color(0.22f, 0.56f, 0.92f, 0.96f);

            // Center VS medallion aligned with the first row's bar track
            float badgeH = useCompactRows ? 26f : 34f;
            float badgeY = topY + (useCompactRows ? 10f : 22f);
            Rect vsRect = new Rect(Mathf.Round(screenCenterX - vsBadgeW * 0.5f), Mathf.Round(badgeY), vsBadgeW, badgeH);
            DrawSolidBoxWithBorder(
                vsRect,
                new Color(0.08f, 0.10f, 0.14f, 0.92f),
                new Color(0.68f, 0.72f, 0.78f, 0.85f));
            Text.Font = useCompactRows ? GameFont.Small : GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.96f, 0.92f, 0.78f, 0.98f);
            Widgets.Label(vsRect, "VS");

            int rows = Math.Max(leftRows, rightRows);
            float rowY = topY;
            for (int r = 0; r < rows; r++)
            {
                float hL = 0f;
                float hR = 0f;
                if (leftIsArmy && r == 0)
                {
                    if (GiantArenaService.TryGetArmyBarSummary(GiantArenaSide.SideA, out string titleA, out float pctA, out int aliveA, out int totalA, out Pawn singleHeroA))
                    {
                        hL = DrawArenaArmyBar(
                            fallbackStyle ?? DefaultFallbackStyleDef,
                            leftCenterX,
                            rowY,
                            maxSideW,
                            useCompactRows,
                            crimsonAccent,
                            titleA,
                            pctA,
                            armyLagA,
                            aliveA,
                            totalA,
                            armyPresence01);
                        float abAlphaL = Mathf.Clamp01((armyPresence01 - 0.30f) / 0.70f);
                        if (singleHeroA != null && abAlphaL > 0.01f)
                        {
                            float abHL = DrawArenaAbilityBar(
                                singleHeroA,
                                leftBarLeftX,
                                maxSideW,
                                rowY + hL + 6f,
                                alignRight: false,
                                compactMode: useCompactRows,
                                alpha: abAlphaL,
                                teamAccent: crimsonAccent);
                            if (abHL > 0f)
                            {
                                hL += 6f + abHL;
                            }
                        }
                    }
                }
                else if (!leftIsArmy && r < leftSide.Count)
                {
                    TrackedGiantBarEntry le = leftSide[r];
                    GiantArenaService.TryGetFighterSide(le.Pawn, out _, out string customL);
                    hL = DrawSingleBossBar(
                        le,
                        le.StyleDef,
                        leftCenterX,
                        rowY,
                        compactWidthOverride: useCompactRows ? maxSideW : -1f,
                        explicitWidthOverride: maxSideW,
                        accentColorOverride: crimsonAccent,
                        titleOverride: customL);
                    float abAlphaL = Mathf.Clamp01((le.Presence01 - 0.30f) / 0.70f);
                    if (abAlphaL > 0.01f)
                    {
                        float abHL = DrawArenaAbilityBar(
                            le.Pawn,
                            leftBarLeftX,
                            maxSideW,
                            rowY + hL + 6f,
                            alignRight: false,
                            compactMode: useCompactRows,
                            alpha: abAlphaL,
                            teamAccent: crimsonAccent);
                        if (abHL > 0f)
                        {
                            hL += 6f + abHL;
                        }
                    }
                }
                if (rightIsArmy && r == 0)
                {
                    if (GiantArenaService.TryGetArmyBarSummary(GiantArenaSide.SideB, out string titleB, out float pctB, out int aliveB, out int totalB, out Pawn singleHeroB))
                    {
                        hR = DrawArenaArmyBar(
                            fallbackStyle ?? DefaultFallbackStyleDef,
                            rightCenterX,
                            rowY,
                            maxSideW,
                            useCompactRows,
                            azureAccent,
                            titleB,
                            pctB,
                            armyLagB,
                            aliveB,
                            totalB,
                            armyPresence01);
                        float abAlphaR = Mathf.Clamp01((armyPresence01 - 0.30f) / 0.70f);
                        if (singleHeroB != null && abAlphaR > 0.01f)
                        {
                            float abHR = DrawArenaAbilityBar(
                                singleHeroB,
                                rightBarLeftX,
                                maxSideW,
                                rowY + hR + 6f,
                                alignRight: true,
                                compactMode: useCompactRows,
                                alpha: abAlphaR,
                                teamAccent: azureAccent);
                            if (abHR > 0f)
                            {
                                hR += 6f + abHR;
                            }
                        }
                    }
                }
                else if (!rightIsArmy && r < rightSide.Count)
                {
                    TrackedGiantBarEntry re = rightSide[r];
                    GiantArenaService.TryGetFighterSide(re.Pawn, out _, out string customR);
                    hR = DrawSingleBossBar(
                        re,
                        re.StyleDef,
                        rightCenterX,
                        rowY,
                        compactWidthOverride: useCompactRows ? maxSideW : -1f,
                        explicitWidthOverride: maxSideW,
                        accentColorOverride: azureAccent,
                        titleOverride: customR);
                    float abAlphaR = Mathf.Clamp01((re.Presence01 - 0.30f) / 0.70f);
                    if (abAlphaR > 0.01f)
                    {
                        float abHR = DrawArenaAbilityBar(
                            re.Pawn,
                            rightBarLeftX,
                            maxSideW,
                            rowY + hR + 6f,
                            alignRight: true,
                            compactMode: useCompactRows,
                            alpha: abAlphaR,
                            teamAccent: azureAccent);
                        if (abHR > 0f)
                        {
                            hR += 6f + abHR;
                        }
                    }
                }
                rowY += Mathf.Max(hL, hR) + 6f;
            }
        }

        private static float DrawArenaArmyBar(
            GiantHealthBarDef def,
            float centerX,
            float startY,
            float totalW,
            bool compactMode,
            Color accentColor,
            string title,
            float healthPct,
            float lagPct,
            int aliveCount,
            int totalCount,
            float presence01)
        {
            float unfold = EaseOutCubic(presence01);
            float alpha = Mathf.Clamp01(presence01 * 1.25f);
            float fillReveal = presence01 < 0.999f
                ? EaseOutCubic(Mathf.Clamp01((presence01 - 0.16f) / 0.84f))
                : 1f;

            float barH = compactMode ? Mathf.Min(14f, def.mainBarHeight) : def.mainBarHeight;
            ComputeSymmetricBossBarRects(
                centerX,
                startY,
                totalW,
                barH,
                unfold,
                hasExpandButton: false,
                compactMode,
                out Rect titleRect,
                out Rect trackRect,
                out _,
                out _);

            float axisX = trackRect.center.x;
            float barY = trackRect.y;
            float curTrackW = trackRect.width;
            Color brightTrimColor = Color.Lerp(accentColor, new Color(1f, 0.94f, 0.78f, 1f), 0.38f);

            if (!string.IsNullOrEmpty(title))
            {
                Text.Font = compactMode ? GameFont.Tiny : (barH >= 22f ? GameFont.Medium : GameFont.Small);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = new Color(0f, 0f, 0f, 0.84f * alpha);
                Widgets.Label(new Rect(titleRect.x + 1f, titleRect.y + 1f, titleRect.width, titleRect.height), title);
                GUI.color = new Color(def.textColor.r, def.textColor.g, def.textColor.b, def.textColor.a * alpha);
                Widgets.Label(titleRect, title);
                DrawSymmetricTitleDivider(axisX, barY - (compactMode ? 2f : 3f), curTrackW, compactMode, accentColor, brightTrimColor, alpha);
            }

            DrawArmoredBossBarCasing(trackRect, def.backgroundColor, accentColor, brightTrimColor, compactMode, alpha);

            float inset = compactMode ? 2f : 3f;
            float innerW = Mathf.Max(0f, curTrackW - inset * 2f);
            float innerH = Mathf.Max(2f, barH - inset * 2f);
            Rect wellRect = new Rect(trackRect.x + inset, barY + inset, innerW, innerH);

            float dispPct = Mathf.Clamp01(healthPct * fillReveal);
            float dispLag = Mathf.Clamp01(lagPct * fillReveal);
            float lagW = Mathf.Round(innerW * dispLag);
            float curW = Mathf.Round(innerW * dispPct);

            if (lagW > curW && lagW > 0.5f)
            {
                Widgets.DrawBoxSolid(new Rect(wellRect.x, wellRect.y, lagW, innerH), WithAlpha(def.lagBarColor, alpha));
            }
            if (curW > 0.5f)
            {
                Color fillCol = Color.Lerp(accentColor, def.EvaluateFillColor(healthPct, 1f), 0.22f);
                DrawLayeredHealthFill(new Rect(wellRect.x, wellRect.y, curW, innerH), fillCol, dispPct, alpha);
            }

            DrawSymmetricScaleNotches(trackRect, wellRect, axisX, accentColor, brightTrimColor, compactMode, alpha);

            if (unfold > 0.45f)
            {
                Text.Font = barH >= 22f ? GameFont.Small : GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                string centerLabel = totalCount > 1
                    ? $"{FormatPercentCached(dispPct)}  ({aliveCount}/{totalCount})"
                    : FormatPercentCached(dispPct);
                GUI.color = new Color(0f, 0f, 0f, 0.85f * alpha);
                Widgets.Label(new Rect(trackRect.x + 1f, trackRect.y + 1f, trackRect.width, trackRect.height), centerLabel);
                GUI.color = new Color(def.textColor.r, def.textColor.g, def.textColor.b, alpha);
                Widgets.Label(trackRect, centerLabel);
            }

            GUI.color = Color.white;
            return (barY - startY) + barH;
        }

        [StaticConstructorOnStartup]
        private static class ArenaCooldownBarTextures
        {
            public static Texture2D CachedVanillaCooldownBarTex;
        }

        private static readonly List<Ability> ArenaAbilityScratch = new List<Ability>(8);
        private static readonly Comparison<Ability> ArenaAbilityOrderComparison = CompareAbilitiesForVanillaOrder;

        private static Texture2D GetVanillaCooldownBarTex()
        {
            if (ArenaCooldownBarTextures.CachedVanillaCooldownBarTex == null)
            {
                // Exact vanilla RimWorld Command_Ability.cooldownBarTex tint
                ArenaCooldownBarTextures.CachedVanillaCooldownBarTex = SolidColorMaterials.NewSolidColorTexture(new Color32(9, 203, 4, 64));
            }
            return ArenaCooldownBarTextures.CachedVanillaCooldownBarTex;
        }

        private static float ComputeVanillaAbilityOrder(Ability ab)
        {
            if (ab?.def == null)
            {
                return 0f;
            }
            float catOrder = ab.def.category != null ? ab.def.category.displayOrder : 0f;
            return 5f + catOrder / 100f + ab.def.displayOrder / 1000f + ab.def.level / 10000f;
        }

        private static int CompareAbilitiesForVanillaOrder(Ability a, Ability b)
        {
            int cmp = ComputeVanillaAbilityOrder(a).CompareTo(ComputeVanillaAbilityOrder(b));
            if (cmp != 0)
            {
                return cmp;
            }
            return (a?.Id ?? 0).CompareTo(b?.Id ?? 0);
        }

        private static bool IsAbilityCompDisabledForArena(Ability ability)
        {
            if (ability?.comps == null)
            {
                return false;
            }
            for (int i = 0; i < ability.comps.Count; i++)
            {
                AbilityComp comp = ability.comps[i];
                if (comp != null && (!comp.CanCast || comp.GizmoDisabled(out _)))
                {
                    return true;
                }
            }
            return false;
        }

        public static Rect ComputeArenaAbilitySlotRect(
            float barLeftX,
            float barWidth,
            float startY,
            bool alignRight,
            int slotIndex,
            int totalCount,
            float slotSize,
            float slotGap,
            float rowExtraVerticalGap,
            out int maxPerRow)
        {
            float safeSize = Mathf.Max(16f, slotSize);
            float safeGap = Mathf.Max(0f, slotGap);
            maxPerRow = Mathf.Max(1, Mathf.FloorToInt((barWidth + safeGap) / (safeSize + safeGap)));
            int row = slotIndex / maxPerRow;
            int col = slotIndex % maxPerRow;
            int countInRow = Mathf.Min(maxPerRow, Math.Max(0, totalCount - row * maxPerRow));
            float rowWidth = countInRow * safeSize + Math.Max(0, countInRow - 1) * safeGap;
            float rowStartX = alignRight ? (barLeftX + barWidth - rowWidth) : barLeftX;
            float x = Mathf.Round(rowStartX + col * (safeSize + safeGap));
            float y = Mathf.Round(startY + row * (safeSize + rowExtraVerticalGap));
            return new Rect(x, y, safeSize, safeSize);
        }

        private static float DrawArenaAbilityBar(
            Pawn pawn,
            float barLeftX,
            float barWidth,
            float startY,
            bool alignRight,
            bool compactMode,
            float alpha,
            Color teamAccent)
        {
            if (pawn?.abilities?.abilities == null || pawn.abilities.abilities.Count == 0 || barWidth <= 20f)
            {
                return 0f;
            }

            ArenaAbilityScratch.Clear();
            List<Ability> rawAbilities = pawn.abilities.abilities;
            for (int i = 0; i < rawAbilities.Count; i++)
            {
                Ability ab = rawAbilities[i];
                if (ab?.def != null && ab.GizmosVisible())
                {
                    ArenaAbilityScratch.Add(ab);
                }
            }
            if (ArenaAbilityScratch.Count == 0)
            {
                return 0f;
            }
            if (ArenaAbilityScratch.Count > 1)
            {
                ArenaAbilityScratch.Sort(ArenaAbilityOrderComparison);
            }

            float slotGap = compactMode ? 4f : 6f;
            float slotSize = compactMode
                ? 42f
                : Mathf.Clamp(Mathf.Floor((barWidth - 24f) / 6f), 44f, 64f);
            bool showBottomLabel = !compactMode && slotSize >= 50f;
            float bottomLabelOverhang = showBottomLabel ? 10f : 0f;
            float rowExtraVerticalGap = showBottomLabel ? 16f : 6f;

            Texture2D bgTex = (compactMode ? Command_Ability.BGTexShrunk : Command_Ability.BGTex) ?? Command.BGTex;
            Texture2D cdBarTex = GetVanillaCooldownBarTex();

            int totalCount = ArenaAbilityScratch.Count;
            int maxPerRow = 1;
            float maxBottomY = startY;

            for (int i = 0; i < totalCount; i++)
            {
                Ability ab = ArenaAbilityScratch[i];
                Rect slotRect = ComputeArenaAbilitySlotRect(
                    barLeftX,
                    barWidth,
                    startY,
                    alignRight,
                    i,
                    totalCount,
                    slotSize,
                    slotGap,
                    rowExtraVerticalGap,
                    out maxPerRow);

                int cdRem = ab.CooldownTicksRemaining;
                int cdTot = ab.CooldownTicksTotal;
                bool onCooldown = cdRem > 0;
                bool cooldownBlocks = onCooldown && (!ab.def.cooldownPerCharge || ab.RemainingCharges <= 0);
                bool noCharges = ab.UsesCharges && ab.RemainingCharges <= 0;
                bool compDisabled = IsAbilityCompDisabledForArena(ab);
                bool lowLight = pawn.Dead || pawn.Downed || cooldownBlocks || noCharges || compDisabled;
                bool casting = ab.Casting;

                Material mat = lowLight ? TexUI.GrayscaleGUI : null;

                // 1. Vanilla AbilityButBG frame
                if (bgTex != null)
                {
                    GUI.color = WithAlpha(lowLight ? Command.LowLightBgColor : Color.white, alpha);
                    GenUI.DrawTextureWithMaterial(slotRect, bgTex, mat);
                }
                else
                {
                    DrawSolidBoxWithBorder(
                        slotRect,
                        new Color(0.10f, 0.12f, 0.16f, 0.90f * alpha),
                        WithAlpha(teamAccent, 0.65f * alpha));
                }

                // 2. Vanilla ability icon fitted inside the frame (0.85 scale, grayscale + dimmed when disabled/on CD)
                Texture iconTex = ab.def.uiIcon != null ? (Texture)ab.def.uiIcon : BaseContent.BadTex;
                Color iconCol = lowLight
                    ? new Color(1f, 1f, 1f, Command.LowLightIconAlpha * alpha)
                    : new Color(1f, 1f, 1f, alpha);
                GUI.color = iconCol;
                Widgets.DrawTextureFitted(
                    slotRect,
                    iconTex,
                    0.85f,
                    Vector2.one,
                    new Rect(0f, 0f, 1f, 1f),
                    0f,
                    mat);

                // 3. Active casting highlight border
                if (casting)
                {
                    Color castBorder = new Color(
                        Mathf.Lerp(teamAccent.r, 1f, 0.55f),
                        Mathf.Lerp(teamAccent.g, 0.95f, 0.55f),
                        Mathf.Lerp(teamAccent.b, 0.65f, 0.55f),
                        0.95f * alpha);
                    Widgets.DrawBoxSolid(new Rect(slotRect.x, slotRect.y, slotRect.width, 2f), castBorder);
                    Widgets.DrawBoxSolid(new Rect(slotRect.x, slotRect.yMax - 2f, slotRect.width, 2f), castBorder);
                    Widgets.DrawBoxSolid(new Rect(slotRect.x, slotRect.y, 2f, slotRect.height), castBorder);
                    Widgets.DrawBoxSolid(new Rect(slotRect.xMax - 2f, slotRect.y, 2f, slotRect.height), castBorder);
                }

                // 4. Vanilla translucent green cooldown fill progress sweep + top-center cooldown timer pill
                if (onCooldown)
                {
                    float fill01 = cdTot > 0 ? Mathf.Clamp01(Mathf.InverseLerp(cdTot, 0f, cdRem)) : 0f;
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    Widgets.FillableBar(slotRect, fill01, cdBarTex, null, doBorder: false);

                    Text.Font = GameFont.Tiny;
                    Text.WordWrap = false;
                    string cdText = cdRem.ToStringTicksToPeriod();
                    Vector2 cdSize = Text.CalcSize(cdText);
                    cdSize.x += 2f;
                    Rect cdRect = new Rect(
                        slotRect.x + (slotRect.width - cdSize.x) * 0.5f,
                        slotRect.y + 1f,
                        cdSize.x,
                        cdSize.y);
                    Rect cdBgRect = cdRect.ExpandedBy(6f, 0f);
                    cdBgRect.xMin = Mathf.Max(slotRect.x, cdBgRect.xMin);
                    cdBgRect.xMax = Mathf.Min(slotRect.xMax, cdBgRect.xMax);
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.DrawTexture(cdBgRect, TexUI.GrayTextBG);
                    Text.Anchor = TextAnchor.UpperCenter;
                    Widgets.Label(cdRect, cdText);
                }

                // 5. Vanilla top-right charge counter (e.g. "2 / 3") when ability uses charges
                string extraLabel = ab.GizmoExtraLabel;
                if (!extraLabel.NullOrEmpty())
                {
                    Text.Font = GameFont.Tiny;
                    Text.WordWrap = false;
                    Vector2 exSize = Text.CalcSize(extraLabel);
                    Rect exRect = new Rect(slotRect.xMax - exSize.x - 2f, slotRect.y + 2f, exSize.x, exSize.y);
                    Rect exBg = new Rect(exRect.x - 2f, exRect.y, exRect.width + 3f, exRect.height);
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.DrawTexture(exBg, TexUI.GrayTextBG);
                    Text.Anchor = TextAnchor.UpperRight;
                    Widgets.Label(exRect, extraLabel);
                }

                // 6. Vanilla bottom localized skill label strip (non-compact mode)
                if (showBottomLabel)
                {
                    string labelCap = ab.def.LabelCap;
                    if (!labelCap.NullOrEmpty())
                    {
                        Text.Font = GameFont.Tiny;
                        Text.WordWrap = true;
                        float labelH = Mathf.Min(Text.CalcHeight(labelCap, slotRect.width + 0.1f), Text.LineHeight * 2f);
                        Rect labelRect = new Rect(
                            slotRect.x,
                            slotRect.yMax - labelH + bottomLabelOverhang,
                            slotRect.width,
                            labelH);
                        GUI.color = new Color(1f, 1f, 1f, alpha);
                        GUI.DrawTexture(labelRect, TexUI.GrayTextBG);
                        GUI.color = WithAlpha(lowLight ? Command.LowLightLabelColor : Color.white, alpha);
                        Text.Anchor = TextAnchor.UpperCenter;
                        Widgets.Label(labelRect, labelCap);
                        Text.WordWrap = false;
                    }
                }

                float slotBottom = slotRect.yMax + bottomLabelOverhang;
                if (slotBottom > maxBottomY)
                {
                    maxBottomY = slotBottom;
                }
            }

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = false;
            return Mathf.Max(0f, maxBottomY - startY);
        }

        public static float ComputeBossTitleWidth(float totalW, bool compactMode)
        {
            return compactMode ? Mathf.Max(0f, totalW - 4f) : (totalW + 24f);
        }

        public static void ComputeSymmetricBossBarRects(
            float centerX,
            float startY,
            float totalW,
            float barH,
            float unfold01,
            bool hasExpandButton,
            bool compactMode,
            out Rect titleRect,
            out Rect trackRect,
            out Rect expandButtonRect,
            out bool showButtonNow)
        {
            float axisX = Mathf.Round(centerX);
            float titleH = compactMode ? 15f : (barH >= 22f ? 26f : 19f);
            float titleW = Mathf.Round(ComputeBossTitleWidth(totalW, compactMode));
            if (((int)titleW & 1) != 0)
            {
                titleW += 1f;
            }
            titleRect = new Rect(axisX - titleW * 0.5f, startY, titleW, titleH);

            float headerRuleGap = compactMode ? 4f : 6f;
            float barY = startY + titleH + headerRuleGap;

            float curTrackW = Mathf.Max(32f, Mathf.Round(totalW * Mathf.Clamp01(unfold01)));
            if (((int)curTrackW & 1) != 0)
            {
                curTrackW += 1f;
            }
            trackRect = new Rect(axisX - curTrackW * 0.5f, barY, curTrackW, barH);

            showButtonNow = hasExpandButton && unfold01 > 0.85f;
            if (hasExpandButton)
            {
                float btnW = compactMode ? 18f : Mathf.Max(26f, barH);
                float btnGap = compactMode ? 5f : 7f;
                expandButtonRect = new Rect(trackRect.xMax + btnGap, barY, btnW, barH);
            }
            else
            {
                expandButtonRect = Rect.zero;
            }
        }

        public static void ComputeSubPartRowLayout(
            float totalW,
            bool compactMode,
            bool needsScroll,
            float rowH,
            float defSubMeterWidth,
            int depth,
            out float labelX,
            out float nameW,
            out float meterX,
            out float meterW,
            out float pctLabelW)
        {
            float listOuterW = Mathf.Max(0f, totalW - 6f);
            float viewW = needsScroll ? Mathf.Max(0f, listOuterW - 16f) : listOuterW;
            pctLabelW = compactMode ? 36f : (rowH >= 22f ? 46f : 38f);
            float meterRatio = compactMode ? 0.30f : 0.38f;
            meterW = Mathf.Min(defSubMeterWidth, viewW * meterRatio);
            meterX = viewW - pctLabelW - meterW - 6f;
            float baseLeftX = compactMode ? 6f : 10f;
            float indentStep = compactMode ? 6f : 12f;
            labelX = baseLeftX + Mathf.Max(0, depth) * indentStep;
            nameW = Mathf.Max(0f, meterX - labelX - 4f);
        }

        private static Color ResolveBossThemeAccentColor(
            Pawn pawn,
            GiantHealthBarDef def,
            Color? accentColorOverride,
            bool isSelected,
            float activeBlend01 = 0f)
        {
            Color baseBorder;
            if (accentColorOverride.HasValue)
            {
                baseBorder = accentColorOverride.Value;
            }
            else if (def != null && def.HasCustomSkinning)
            {
                baseBorder = def.borderColor;
                if (def.activeBorderColor.a > 0.01f && activeBlend01 > 0.001f)
                {
                    baseBorder = Color.Lerp(baseBorder, def.activeBorderColor, Mathf.Clamp01(activeBlend01));
                }
            }
            else
            {
                baseBorder = def != null ? def.borderColor : new Color(0.58f, 0.56f, 0.52f, 1.00f);
            }

            if (isSelected)
            {
                return new Color(
                    Mathf.Min(1f, baseBorder.r + 0.22f),
                    Mathf.Min(1f, baseBorder.g + 0.22f),
                    Mathf.Min(1f, baseBorder.b + 0.22f),
                    baseBorder.a);
            }
            return baseBorder;
        }

        private static void EnsureEntryHudHooksResolved(TrackedGiantBarEntry entry)
        {
            if (entry == null || entry.HudHooksResolved || entry.Pawn == null)
            {
                return;
            }
            entry.HudHooksResolved = true;
            List<ThingComp> comps = entry.Pawn.AllComps;
            if (comps == null)
            {
                return;
            }
            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp comp = comps[i];
                if (entry.HudStateProvider == null && comp is IGiantBossHudStateProvider sp)
                {
                    entry.HudStateProvider = sp;
                }
                if (entry.HudOverlayDrawer == null && comp is IGiantBossHudOverlayDrawer od)
                {
                    entry.HudOverlayDrawer = od;
                }
            }
        }

        private static string ResolveBossSubtitleText(GiantHealthBarDef def)
        {
            if (def == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(def.subtitleKey))
            {
                if (LanguageDatabase.activeLanguage != null && def.subtitleKey.CanTranslate())
                {
                    return def.subtitleKey.Translate().ToString();
                }
            }
            return string.IsNullOrWhiteSpace(def.subtitleText) ? null : def.subtitleText;
        }

        private static float DrawSingleBossBar(
            TrackedGiantBarEntry entry,
            GiantHealthBarDef def,
            float centerX,
            float startY,
            float compactWidthOverride = -1f,
            float explicitWidthOverride = -1f,
            Color? accentColorOverride = null,
            string titleOverride = null)
        {
            float unfold = EaseOutCubic(entry.Presence01);
            float alpha = Mathf.Clamp01(entry.Presence01 * 1.25f);
            float fillReveal = entry.State == BarLifecycleState.Entering
                ? EaseOutCubic(Mathf.Clamp01((entry.Presence01 - 0.16f) / 0.84f))
                : 1f;

            bool compactMode = compactWidthOverride > 0f;
            float totalW = explicitWidthOverride > 0f
                ? Mathf.Min(explicitWidthOverride, UI.screenWidth * 0.46f)
                : (compactMode
                    ? Mathf.Min(compactWidthOverride, UI.screenWidth * 0.46f)
                    : Mathf.Min(def.barWidth, UI.screenWidth - 100f));
            totalW = Mathf.Round(totalW);
            if (((int)totalW & 1) != 0)
            {
                totalW += 1f;
            }

            float barH = compactMode ? Mathf.Min(14f, def.mainBarHeight) : def.mainBarHeight;
            bool hasBtn = def.showExpandButton && entry.RowSpecs.Count > 0 && !GiantArenaService.IsCinematicHudOnly;
            bool isSelected = entry.Pawn != null && (Find.Selector?.IsSelected(entry.Pawn) ?? false);
            string displayTitle = !string.IsNullOrWhiteSpace(titleOverride) ? titleOverride : entry.CachedTitle;

            var customCtx = new GiantBossBarRenderContext
            {
                Pawn = entry.Pawn,
                RigComp = entry.RigComp,
                StyleDef = def,
                CenterX = centerX,
                StartY = startY,
                TotalWidth = totalW,
                BarHeight = barH,
                Unfold01 = unfold,
                FillReveal01 = fillReveal,
                Alpha = alpha,
                CompactMode = compactMode,
                IsSelected = isSelected,
                HasExpandButton = hasBtn,
                Expanded = entry.Expanded,
                ExpandAnim01 = entry.ExpandAnim01,
                MainHealthPct = entry.MainHealthPct,
                MainLagPct = entry.MainLagPct,
                DisplayTitle = displayTitle,
                AccentColorOverride = accentColorOverride,
                RowSpecs = entry.RowSpecs,
                RowHealthPct = entry.RowHealthPct,
                RowLagPct = entry.RowLagPct,
                ScrollPos = entry.ScrollPos
            };
            if (GiantCustomBossBarRegistry.TryDraw(in customCtx, out GiantBossBarRenderResult customRes))
            {
                entry.ScrollPos = customRes.UpdatedScrollPos;
                if (customRes.SelectAndJumpClicked &&
                    def.clickBarToSelectAndJump &&
                    entry.State == BarLifecycleState.Active &&
                    entry.Pawn != null &&
                    entry.Pawn.Spawned &&
                    !IsPawnConcealedFromBossHud(entry.Pawn))
                {
                    Find.Selector?.ClearSelection();
                    Find.Selector?.Select(entry.Pawn, playSound: true, forceDesignatorDeselect: false);
                    CameraJumper.TryJump(entry.Pawn);
                }
                if (customRes.ToggleExpandClicked && hasBtn)
                {
                    entry.Expanded = !entry.Expanded;
                    SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                }
                GUI.color = Color.white;
                return customRes.UsedHeight;
            }

            EnsureEntryHudHooksResolved(entry);
            GiantBossHudTelemetry telemetry = default;
            entry.HudStateProvider?.FillBossHudTelemetry(ref telemetry);
            float activeBlend = telemetry.HasCustomState ? Mathf.Clamp01(telemetry.ActiveBlend01) : 0f;
            float glowPulse = telemetry.HasCustomState ? Mathf.Clamp01(telemetry.GlowPulse01) : 0f;

            bool showSideOrnaments = !compactMode &&
                def.HasSideOrnaments &&
                (def.LeftOrnamentTex != null || def.RightOrnamentTex != null ||
                 def.LeftOrnamentActiveTex != null || def.RightOrnamentActiveTex != null);
            float ornScale = explicitWidthOverride > 0f ? 0.56f : 1.0f;
            float ornW = showSideOrnaments ? Mathf.Round(def.ornamentSize.x * ornScale) : 0f;
            float ornH = showSideOrnaments ? Mathf.Round(def.ornamentSize.y * ornScale) : 0f;
            float ornOverlap = showSideOrnaments ? Mathf.Round(def.ornamentOverlapX * ornScale) : 0f;
            float ornProtrusion = showSideOrnaments ? Mathf.Max(0f, ornW - ornOverlap) : 0f;

            float effectiveTotalW = totalW;
            if (showSideOrnaments)
            {
                effectiveTotalW = explicitWidthOverride > 0f
                    ? Mathf.Max(140f, totalW - ornProtrusion * 2f)
                    : Mathf.Min(totalW, Mathf.Max(180f, UI.screenWidth - ornProtrusion * 2f - 80f));
                effectiveTotalW = Mathf.Round(effectiveTotalW);
                if (((int)effectiveTotalW & 1) != 0)
                {
                    effectiveTotalW += 1f;
                }
            }

            bool showCenterCrest = !compactMode &&
                def.HasCenterCrest &&
                (def.CenterCrestTex != null || def.CenterCrestActiveTex != null);
            float crestScale = explicitWidthOverride > 0f ? 0.76f : 1.0f;
            float crestW = showCenterCrest ? Mathf.Round(def.centerCrestSize.x * crestScale) : 0f;
            float crestH = showCenterCrest ? Mathf.Round(def.centerCrestSize.y * crestScale) : 0f;
            float crestTopReserve = showCenterCrest
                ? Mathf.Max(0f, Mathf.Round(crestH * 0.62f + def.centerCrestOffsetY * crestScale))
                : 0f;

            string subtitleText = !compactMode ? ResolveBossSubtitleText(def) : null;
            bool hasSubtitle = !string.IsNullOrEmpty(subtitleText);
            float subtitleBandH = hasSubtitle ? 14f : 0f;

            ComputeSymmetricBossBarRects(
                centerX,
                startY + crestTopReserve,
                effectiveTotalW,
                barH,
                unfold,
                hasBtn,
                compactMode,
                out Rect titleRect,
                out Rect trackRect,
                out Rect btnRect,
                out bool showBtnNow);

            if (subtitleBandH > 0f)
            {
                trackRect.y += subtitleBandH;
                btnRect.y += subtitleBandH;
            }
            if (showSideOrnaments && hasBtn)
            {
                float rightWingTipX = trackRect.xMax + ornProtrusion;
                btnRect.x = Mathf.Min(UI.screenWidth - btnRect.width - 6f, rightWingTipX + 6f);
            }

            float axisX = trackRect.center.x;
            float barY = trackRect.y;
            float curTrackW = trackRect.width;
            Color activeBorderColor = ResolveBossThemeAccentColor(entry.Pawn, def, accentColorOverride, isSelected, activeBlend);
            Color brightTrimColor = Color.Lerp(activeBorderColor, new Color(1f, 0.94f, 0.78f, 1f), 0.38f);
            Color titleTextCol = (def.activeTextColor.a > 0.01f && activeBlend > 0.001f)
                ? Color.Lerp(def.textColor, def.activeTextColor, activeBlend)
                : def.textColor;

            // 0. Optional Top-Center Crest Medallion rising behind/above the title plaque
            if (showCenterCrest && unfold > 0.20f)
            {
                float crestAlpha = Mathf.Clamp01((unfold - 0.20f) / 0.80f) * alpha;
                Rect crestRect = new Rect(
                    Mathf.Round(axisX - crestW * 0.5f),
                    Mathf.Round(startY + Mathf.Max(0f, -def.centerCrestOffsetY * crestScale)),
                    crestW,
                    crestH);
                DrawLayeredOrnamentTextures(
                    crestRect,
                    def.CenterCrestTex,
                    def.CenterCrestActiveTex,
                    def.CenterCrestGlowTex,
                    activeBlend,
                    glowPulse,
                    crestAlpha);
            }

            // 1. Localized Def label, optional epithet subtitle & symmetrical central-axis divider above the bar
            if (!string.IsNullOrEmpty(displayTitle))
            {
                Text.Font = compactMode ? GameFont.Tiny : (barH >= 22f ? GameFont.Medium : GameFont.Small);
                Text.Anchor = TextAnchor.MiddleCenter;

                float plaqueHalfW = Mathf.Min(curTrackW * 0.40f, Mathf.Max(68f, Text.CalcSize(displayTitle).x * 0.5f + 24f));
                plaqueHalfW = Mathf.Round(plaqueHalfW);
                float plaqueH = Mathf.Max(12f, titleRect.height - 2f) + subtitleBandH;
                Rect titlePlaqueRect = new Rect(axisX - plaqueHalfW, titleRect.y + 1f, plaqueHalfW * 2f, plaqueH);
                Widgets.DrawBoxSolid(titlePlaqueRect, new Color(0.045f, 0.050f, 0.068f, 0.68f * alpha));

                GUI.color = new Color(0f, 0f, 0f, 0.86f * alpha);
                Widgets.Label(new Rect(titleRect.x + 1f, titleRect.y + 1f, titleRect.width, titleRect.height), displayTitle);

                GUI.color = new Color(titleTextCol.r, titleTextCol.g, titleTextCol.b, titleTextCol.a * alpha);
                Widgets.Label(titleRect, displayTitle);

                if (hasSubtitle)
                {
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Rect subRect = new Rect(titleRect.x, titleRect.yMax - 2f, titleRect.width, subtitleBandH + 2f);
                    Color subCol = Color.Lerp(brightTrimColor, titleTextCol, 0.45f);
                    GUI.color = new Color(0f, 0f, 0f, 0.85f * alpha);
                    Widgets.Label(new Rect(subRect.x + 1f, subRect.y + 1f, subRect.width, subRect.height), subtitleText);
                    GUI.color = new Color(subCol.r, subCol.g, subCol.b, 0.92f * alpha);
                    Widgets.Label(subRect, subtitleText);
                }

                DrawSymmetricTitleDivider(axisX, barY - (compactMode ? 2f : 3f), curTrackW, compactMode, activeBorderColor, brightTrimColor, alpha);
            }

            // 2. Custom double-framed armored Boss Health Bar centered strictly on axisX
            DrawArmoredBossBarCasing(trackRect, def.backgroundColor, activeBorderColor, brightTrimColor, compactMode, alpha);
            if (def.BarFrameTex != null)
            {
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.DrawTexture(trackRect, def.BarFrameTex, ScaleMode.StretchToFill, true);
            }

            float inset = compactMode ? 2f : 3f;
            float innerW = Mathf.Max(0f, curTrackW - inset * 2f);
            float innerH = Mathf.Max(2f, barH - inset * 2f);
            Rect wellRect = new Rect(trackRect.x + inset, barY + inset, innerW, innerH);

            float dispPct = Mathf.Clamp01(entry.MainHealthPct * fillReveal);
            float dispLag = Mathf.Clamp01(entry.MainLagPct * fillReveal);
            float lagW = Mathf.Round(innerW * dispLag);
            float curW = Mathf.Round(innerW * dispPct);

            if (lagW > curW && lagW > 0.5f)
            {
                Rect lagRect = new Rect(wellRect.x, wellRect.y, lagW, innerH);
                Widgets.DrawBoxSolid(lagRect, WithAlpha(def.lagBarColor, alpha));
                Widgets.DrawBoxSolid(new Rect(lagRect.x, lagRect.y, lagRect.width, Mathf.Max(1f, Mathf.Round(innerH * 0.30f))),
                    WithAlpha(Color.Lerp(def.lagBarColor, Color.white, 0.35f), alpha));
            }
            if (curW > 0.5f)
            {
                Rect fillRect = new Rect(wellRect.x, wellRect.y, curW, innerH);
                if (def.BarFillTex != null || def.BarFillActiveTex != null)
                {
                    DrawCustomTexturedHealthFill(
                        fillRect,
                        def.BarFillTex,
                        def.BarFillActiveTex,
                        dispPct,
                        activeBlend,
                        glowPulse,
                        telemetry.IsOverdrive,
                        alpha);
                }
                else
                {
                    Color fillCol = accentColorOverride.HasValue
                        ? Color.Lerp(accentColorOverride.Value, def.EvaluateFillColor(entry.MainHealthPct, 1f), 0.22f)
                        : def.EvaluateFillColor(entry.MainHealthPct, 1f);
                    DrawLayeredHealthFill(fillRect, fillCol, dispPct, alpha);
                }
            }

            DrawSymmetricScaleNotches(trackRect, wellRect, axisX, activeBorderColor, brightTrimColor, compactMode, alpha);

            // Optional creature-specific procedural overlay hook on top of bar well (e.g. comet chevrons / lightning arcs)
            if (entry.HudOverlayDrawer != null && unfold > 0.35f)
            {
                entry.HudOverlayDrawer.DrawBossBarOverlay(trackRect, wellRect, in telemetry, unfold, alpha);
                GUI.color = Color.white;
            }

            // Single clean integer percentage centered strictly on axisX inside trackRect
            if (unfold > 0.45f)
            {
                Text.Font = barH >= 22f ? GameFont.Small : GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                string pctText = FormatPercentCached(dispPct);
                GUI.color = new Color(0f, 0f, 0f, 0.88f * alpha);
                Widgets.Label(new Rect(trackRect.x + 1f, trackRect.y + 1f, trackRect.width, trackRect.height), pctText);
                GUI.color = new Color(titleTextCol.r, titleTextCol.g, titleTextCol.b, alpha);
                Widgets.Label(trackRect, pctText);
            }

            // 2b. Optional Flanking Left & Right Wing/Weapon Ornaments clamped onto trackRect outer sockets
            if (showSideOrnaments && unfold > 0.25f)
            {
                float ornUnfold = EaseOutCubic(Mathf.Clamp01((unfold - 0.25f) / 0.75f));
                float ornAlpha = ornUnfold * alpha;
                float slideOffset = (1f - ornUnfold) * 14f;
                float ornY = Mathf.Round(trackRect.center.y - ornH * 0.5f + def.ornamentOffsetY * ornScale);

                Rect leftOrnRect = new Rect(
                    Mathf.Round(trackRect.x - ornW + ornOverlap + slideOffset),
                    ornY,
                    ornW,
                    ornH);
                Rect rightOrnRect = new Rect(
                    Mathf.Round(trackRect.xMax - ornOverlap - slideOffset),
                    ornY,
                    ornW,
                    ornH);

                DrawLayeredOrnamentTextures(
                    leftOrnRect,
                    def.LeftOrnamentTex,
                    def.LeftOrnamentActiveTex,
                    def.LeftOrnamentGlowTex,
                    activeBlend,
                    glowPulse,
                    ornAlpha);
                DrawLayeredOrnamentTextures(
                    rightOrnRect,
                    def.RightOrnamentTex,
                    def.RightOrnamentActiveTex,
                    def.RightOrnamentGlowTex,
                    activeBlend,
                    glowPulse,
                    ornAlpha);
            }

            if (def.clickBarToSelectAndJump &&
                entry.State == BarLifecycleState.Active &&
                entry.Pawn != null &&
                entry.Pawn.Spawned &&
                !IsPawnConcealedFromBossHud(entry.Pawn) &&
                Widgets.ButtonInvisible(trackRect))
            {
                Find.Selector?.ClearSelection();
                Find.Selector?.Select(entry.Pawn, playSound: true, forceDesignatorDeselect: false);
                CameraJumper.TryJump(entry.Pawn);
            }

            // 3. Extra right-area [+] / [-] toggle button (strictly outside trackRect and right ornament)
            if (showBtnNow)
            {
                float btnAlpha = Mathf.Clamp01((unfold - 0.85f) / 0.15f) * alpha;
                bool hovered = Mouse.IsOver(btnRect);
                DrawArmoredExpandButton(btnRect, entry.Expanded, hovered, def.backgroundColor, activeBorderColor, brightTrimColor, barH, btnAlpha);

                if (Widgets.ButtonInvisible(btnRect))
                {
                    entry.Expanded = !entry.Expanded;
                    SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                }
            }

            float usedHeight = (barY - startY) + barH;

            // 3b. Optional Secondary Power / Phase Sub-Bar & Breakable Organ Telemetry Deck
            float secDeckOffset = 0f;
            bool showSecDeck = !compactMode &&
                unfold > 0.55f &&
                (def.showSecondaryStateBar || (telemetry.HasCustomState && telemetry.ShowSecondaryBar));
            if (showSecDeck)
            {
                float deckAlpha = Mathf.Clamp01((unfold - 0.55f) / 0.45f) * alpha;
                float secH = DrawBossSecondaryTelemetryDeck(
                    axisX,
                    barY + barH + 3f,
                    curTrackW,
                    def,
                    in telemetry,
                    activeBorderColor,
                    brightTrimColor,
                    deckAlpha);
                if (secH > 0f)
                {
                    secDeckOffset = 3f + secH;
                    usedHeight += secDeckOffset;
                }
            }

            // 4. Expandable localized anatomical part tree drawer (symmetric about axisX, aligned 1:1 with effectiveTotalW)
            if (entry.ExpandAnim01 > 0.005f && entry.State == BarLifecycleState.Active && entry.RowSpecs.Count > 0)
            {
                int visibleRowCap = Mathf.Max(3, def.maxVisibleSubRows);
                float rowH = def.subRowHeight;
                float padV = 6f;
                float contentH = entry.RowSpecs.Count * rowH;
                bool needsScroll = entry.RowSpecs.Count > visibleRowCap;
                float maxDrawerInnerH = Mathf.Min(contentH, visibleRowCap * rowH);
                float fullDrawerH = padV * 2f + maxDrawerInnerH;
                float curDrawerH = Mathf.Round(fullDrawerH * EaseOutCubic(entry.ExpandAnim01));

                float panelX = axisX - effectiveTotalW * 0.5f;
                float drawerY = barY + barH + secDeckOffset + 4f;
                Rect clipRect = new Rect(panelX, drawerY, effectiveTotalW, curDrawerH);

                GUI.BeginGroup(clipRect);
                try
                {
                    Rect localPanelRect = new Rect(0f, 0f, effectiveTotalW, fullDrawerH);
                    DrawSolidBoxWithBorder(localPanelRect, def.drawerBackgroundColor, activeBorderColor);
                    DrawSolidBoxOutline(localPanelRect.ContractedBy(1f), new Color(0.03f, 0.035f, 0.045f, 0.92f));
                    DrawCornerBrackets(localPanelRect, brightTrimColor, len: compactMode ? 6f : 9f, thickness: 2f);

                    Rect listOuter = new Rect(4f, padV, Mathf.Max(0f, effectiveTotalW - 8f), maxDrawerInnerH);
                    float viewW = needsScroll ? Mathf.Max(0f, listOuter.width - 16f) : listOuter.width;
                    Rect viewRect = new Rect(0f, 0f, viewW, contentH);

                    if (needsScroll)
                    {
                        Widgets.BeginScrollView(listOuter, ref entry.ScrollPos, viewRect);
                    }
                    else
                    {
                        GUI.BeginGroup(listOuter);
                    }
                    try
                    {
                        Text.Font = (!compactMode && rowH >= 22f) ? GameFont.Small : GameFont.Tiny;
                        ComputeSubPartRowLayout(
                            effectiveTotalW, compactMode, needsScroll, rowH, def.subMeterWidth, depth: 0,
                            out _, out _, out float meterX, out float meterW, out float pctLabelW);
                        float subBarH = rowH >= 22f ? 11f : 7f;
                        float subInnerH = subBarH - 2f;
                        float elbowOffset = compactMode ? 4f : 7f;
                        float elbowTickW = compactMode ? 3f : 5f;

                        for (int r = 0; r < entry.RowSpecs.Count; r++)
                        {
                            GiantHealthBarPartRowSpec spec = entry.RowSpecs[r];
                            float pPct = r < entry.RowHealthPct.Length ? entry.RowHealthPct[r] : 1f;
                            float pLag = r < entry.RowLagPct.Length ? entry.RowLagPct[r] : pPct;
                            bool severed = pPct <= 0.001f;

                            float ry = r * rowH;
                            if ((r & 1) == 1)
                            {
                                Widgets.DrawBoxSolid(new Rect(0f, ry, viewW, rowH), new Color(1f, 1f, 1f, 0.025f));
                            }

                            ComputeSubPartRowLayout(
                                effectiveTotalW, compactMode, needsScroll, rowH, def.subMeterWidth, spec.Depth,
                                out float labelX, out float nameW, out _, out _, out _);

                            if (def.drawHierarchyLines && spec.Depth > 0)
                            {
                                float elbowX = labelX - elbowOffset;
                                float midY = ry + rowH * 0.5f;
                                Color lineCol = severed
                                    ? new Color(0.55f, 0.36f, 0.36f, 0.42f)
                                    : WithAlpha(activeBorderColor, 0.58f);
                                Widgets.DrawBoxSolid(new Rect(elbowX, ry + 2f, 1f, rowH * 0.5f - 1f), lineCol);
                                Widgets.DrawBoxSolid(new Rect(elbowX, midY, elbowTickW, 1f), lineCol);
                            }

                            Text.Anchor = TextAnchor.MiddleLeft;
                            GUI.color = severed
                                ? new Color(0.60f, 0.63f, 0.66f, 0.46f)
                                : (spec.IsCore ? new Color(0.98f, 0.94f, 0.84f, 0.98f) : new Color(0.90f, 0.92f, 0.94f, 0.94f));
                            if (nameW > 1f)
                            {
                                Rect nameRect = new Rect(labelX, ry, nameW, rowH);
                                Widgets.Label(nameRect, spec.LocalizedLabel);
                            }

                            float my = Mathf.Round(ry + (rowH - subBarH) * 0.5f);
                            Rect subTrack = new Rect(meterX, my, meterW, subBarH);
                            DrawSolidBoxWithBorder(
                                subTrack,
                                new Color(0.11f, 0.125f, 0.15f, 0.96f),
                                severed ? new Color(0.51f, 0.27f, 0.27f, 0.52f) : WithAlpha(activeBorderColor, 0.72f));

                            if (!severed)
                            {
                                float subInnerW = Mathf.Max(0f, meterW - 2f);
                                float subLagW = Mathf.Round(subInnerW * Mathf.Clamp01(pLag));
                                float subCurW = Mathf.Max(1f, Mathf.Round(subInnerW * Mathf.Clamp01(pPct)));
                                if (subLagW > subCurW)
                                {
                                    Widgets.DrawBoxSolid(new Rect(meterX + 1f, my + 1f, subLagW, subInnerH), def.lagBarColor);
                                }
                                DrawLayeredHealthFill(new Rect(meterX + 1f, my + 1f, subCurW, subInnerH), def.EvaluateFillColor(pPct), pPct, 1f);
                            }

                            Text.Anchor = TextAnchor.MiddleRight;
                            GUI.color = severed
                                ? new Color(0.81f, 0.38f, 0.36f, 0.86f)
                                : new Color(0.88f, 0.89f, 0.92f, 0.92f);
                            Rect pctRect = new Rect(meterX + meterW + 2f, ry, Mathf.Max(0f, pctLabelW - 4f), rowH);
                            Widgets.Label(pctRect, FormatPercentCached(pPct));
                        }
                    }
                    finally
                    {
                        if (needsScroll)
                        {
                            Widgets.EndScrollView();
                        }
                        else
                        {
                            GUI.EndGroup();
                        }
                    }
                }
                finally
                {
                    GUI.EndGroup();
                }

                usedHeight += 4f + curDrawerH;
            }

            GUI.color = Color.white;
            return usedHeight;
        }

        private static void DrawLayeredOrnamentTextures(
            Rect rect,
            Texture2D calmTex,
            Texture2D activeTex,
            Texture2D glowTex,
            float activeBlend01,
            float glowPulse01,
            float alpha)
        {
            if (rect.width <= 1f || rect.height <= 1f || alpha <= 0.005f)
            {
                return;
            }
            if (calmTex != null)
            {
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.DrawTexture(rect, calmTex, ScaleMode.StretchToFill, true);
            }
            if (activeTex != null && (calmTex == null || activeBlend01 > 0.01f))
            {
                float a = calmTex == null ? alpha : (alpha * Mathf.Clamp01(activeBlend01));
                GUI.color = new Color(1f, 1f, 1f, a);
                GUI.DrawTexture(rect, activeTex, ScaleMode.StretchToFill, true);
            }
            if (glowTex != null && glowPulse01 > 0.01f)
            {
                GUI.color = new Color(1f, 1f, 1f, alpha * Mathf.Clamp01(glowPulse01));
                GUI.DrawTexture(rect, glowTex, ScaleMode.StretchToFill, true);
            }
        }

        private static void DrawCustomTexturedHealthFill(
            Rect fillRect,
            Texture2D calmFillTex,
            Texture2D activeFillTex,
            float pct01,
            float activeBlend01,
            float glowPulse01,
            bool isOverdrive,
            float alpha)
        {
            if (fillRect.width <= 0.5f || fillRect.height <= 0.5f || alpha <= 0.005f)
            {
                return;
            }

            float clampedUvW = Mathf.Clamp01(pct01);
            Rect uvRect = new Rect(0f, 0f, Mathf.Max(0.002f, clampedUvW), 1f);

            if (calmFillTex != null)
            {
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.DrawTextureWithTexCoords(fillRect, calmFillTex, uvRect, true);
            }
            if (activeFillTex != null && (calmFillTex == null || activeBlend01 > 0.01f))
            {
                float actAlpha = calmFillTex == null ? alpha : (alpha * Mathf.Clamp01(activeBlend01));
                GUI.color = new Color(1f, 1f, 1f, actAlpha);
                GUI.DrawTextureWithTexCoords(fillRect, activeFillTex, uvRect, true);
            }

            float h = fillRect.height;
            if (h >= 5f)
            {
                Color topGloss = isOverdrive
                    ? new Color(1f, 0.92f, 0.88f, (0.36f + 0.18f * glowPulse01) * alpha)
                    : new Color(1f, 1f, 1f, (0.20f + 0.10f * activeBlend01) * alpha);
                Widgets.DrawBoxSolid(new Rect(fillRect.x, fillRect.y, fillRect.width, 1f), topGloss);
                Widgets.DrawBoxSolid(new Rect(fillRect.x, fillRect.yMax - 2f, fillRect.width, 2f), new Color(0f, 0f, 0f, 0.28f * alpha));
            }

            if (isOverdrive && fillRect.width >= 6f)
            {
                float coreBandH = Mathf.Max(2f, Mathf.Round(h * 0.26f));
                float coreY = Mathf.Round(fillRect.center.y - coreBandH * 0.5f);
                Widgets.DrawBoxSolid(
                    new Rect(fillRect.x, coreY, fillRect.width, coreBandH),
                    new Color(1f, 0.86f, 0.82f, (0.14f + 0.16f * glowPulse01) * alpha));
            }

            if (pct01 > 0.01f && pct01 < 0.995f && fillRect.width >= 3f)
            {
                Color cutterCol = Color.Lerp(
                    new Color(0.92f, 0.96f, 1.00f, 1f),
                    new Color(1.00f, 0.52f, 0.56f, 1f),
                    Mathf.Clamp01(activeBlend01 * 0.7f));
                float cutterW = isOverdrive ? 3f : 2f;
                Widgets.DrawBoxSolid(new Rect(fillRect.xMax - cutterW, fillRect.y, cutterW, h), WithAlpha(cutterCol, 0.95f * alpha));
            }
        }

        private static float DrawBossSecondaryTelemetryDeck(
            float axisX,
            float topY,
            float curTrackW,
            GiantHealthBarDef def,
            in GiantBossHudTelemetry telemetry,
            Color borderCol,
            Color brightTrimCol,
            float alpha)
        {
            if (curTrackW < 110f || alpha <= 0.01f)
            {
                return 0f;
            }

            float railW = Mathf.Round(curTrackW * 0.94f);
            if (((int)railW & 1) != 0)
            {
                railW += 1f;
            }
            float railX = axisX - railW * 0.5f;
            float curY = topY;

            // 1. Slim power / phase / backfire sub-bar
            float secBarH = Mathf.Clamp(def.secondaryBarHeight, 4f, 12f);
            if (secBarH >= 4f && (def.showSecondaryStateBar || telemetry.ShowSecondaryBar))
            {
                Rect secRect = new Rect(railX, curY, railW, secBarH);
                Color secBg = WithAlpha(def.secondaryBarBgColor, alpha);
                Color secBorder = WithAlpha(Color.Lerp(borderCol, Color.black, 0.22f), 0.90f * alpha);
                DrawSolidBoxWithBorder(secRect, secBg, secBorder);

                float fill01 = telemetry.HasCustomState ? Mathf.Clamp01(telemetry.SecondaryFill01) : 0f;
                float innerMaxW = Mathf.Max(0f, railW - 2f);
                float fillW = Mathf.Round(innerMaxW * fill01);
                if (fillW > 0.5f)
                {
                    Color secFill = (telemetry.HasCustomState && telemetry.SecondaryBarColor.a > 0.01f)
                        ? telemetry.SecondaryBarColor
                        : def.secondaryBarFillColor;
                    Rect fillRect = new Rect(secRect.x + 1f, secRect.y + 1f, fillW, Mathf.Max(1f, secBarH - 2f));
                    Widgets.DrawBoxSolid(fillRect, WithAlpha(secFill, alpha));
                    if (fillRect.height >= 3f)
                    {
                        Widgets.DrawBoxSolid(
                            new Rect(fillRect.x, fillRect.y, fillRect.width, 1f),
                            WithAlpha(Color.Lerp(secFill, Color.white, 0.38f), 0.92f * alpha));
                    }
                    if (fill01 > 0.01f && fill01 < 0.995f && fillRect.width >= 2f)
                    {
                        Widgets.DrawBoxSolid(
                            new Rect(fillRect.xMax - 2f, fillRect.y, 2f, fillRect.height),
                            new Color(1f, 0.95f, 0.90f, 0.92f * alpha));
                    }
                }

                float marker01 = telemetry.HasCustomState ? telemetry.SecondaryThresholdMarker01 : 0f;
                if (marker01 > 0.01f && marker01 < 0.995f && innerMaxW > 12f)
                {
                    float markerX = Mathf.Round(secRect.x + 1f + innerMaxW * marker01);
                    Widgets.DrawBoxSolid(
                        new Rect(markerX - 1f, secRect.y - 1f, 2f, secBarH + 2f),
                        WithAlpha(brightTrimCol, 0.96f * alpha));
                }

                curY += secBarH + 2f;
            }

            // 2. Status readout & breakable organ pills row
            bool hasStatus = telemetry.HasCustomState && !string.IsNullOrEmpty(telemetry.StatusReadout);
            int badgeCount = (telemetry.HasCustomState && telemetry.PartBadges != null)
                ? Mathf.Min(telemetry.PartBadgeCount, telemetry.PartBadges.Length)
                : 0;

            if ((hasStatus || badgeCount > 0) && railW >= 180f)
            {
                float rowH = 16f;
                Text.Font = GameFont.Tiny;
                Text.WordWrap = false;

                float rightCursorX = railX + railW;
                if (badgeCount > 0 && railW >= 260f)
                {
                    for (int i = badgeCount - 1; i >= 0; i--)
                    {
                        GiantBossHudPartBadge badge = telemetry.PartBadges[i];
                        if (string.IsNullOrEmpty(badge.Label))
                        {
                            continue;
                        }
                        string pillText = badge.IsDestroyed ? ("× " + badge.Label) : badge.Label;
                        float textW = Text.CalcSize(pillText).x;
                        float pillW = Mathf.Round(textW + 10f);
                        float pillX = rightCursorX - pillW;
                        if (pillX < railX + railW * 0.42f)
                        {
                            break;
                        }

                        Rect pillRect = new Rect(pillX, curY, pillW, rowH - 1f);
                        Color pillBg;
                        Color pillBorder;
                        Color pillTextCol;
                        if (badge.IsDestroyed || badge.HealthPct01 <= 0.001f)
                        {
                            pillBg = new Color(0.20f, 0.05f, 0.07f, 0.88f * alpha);
                            pillBorder = new Color(0.76f, 0.22f, 0.26f, 0.85f * alpha);
                            pillTextCol = new Color(0.95f, 0.46f, 0.48f, 0.95f * alpha);
                        }
                        else if (badge.HealthPct01 < 0.72f)
                        {
                            pillBg = new Color(0.18f, 0.13f, 0.05f, 0.86f * alpha);
                            pillBorder = new Color(0.84f, 0.62f, 0.24f, 0.85f * alpha);
                            pillTextCol = new Color(0.98f, 0.84f, 0.52f, 0.96f * alpha);
                        }
                        else
                        {
                            pillBg = new Color(0.07f, 0.085f, 0.115f, 0.84f * alpha);
                            pillBorder = WithAlpha(borderCol, 0.72f * alpha);
                            pillTextCol = new Color(0.88f, 0.91f, 0.96f, 0.94f * alpha);
                        }

                        DrawSolidBoxWithBorder(pillRect, pillBg, pillBorder);
                        Text.Anchor = TextAnchor.MiddleCenter;
                        GUI.color = pillTextCol;
                        Widgets.Label(pillRect, pillText);

                        rightCursorX = pillX - 4f;
                    }
                }

                if (hasStatus)
                {
                    float statusMaxW = Mathf.Max(60f, rightCursorX - railX - 6f);
                    Rect statusRect = new Rect(railX + 2f, curY - 1f, statusMaxW, rowH + 1f);
                    Color stCol = telemetry.StatusTextColor.a > 0.01f
                        ? telemetry.StatusTextColor
                        : new Color(0.90f, 0.92f, 0.96f, 1f);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    GUI.color = new Color(0f, 0f, 0f, 0.86f * alpha);
                    Widgets.Label(new Rect(statusRect.x + 1f, statusRect.y + 1f, statusRect.width, statusRect.height), telemetry.StatusReadout);
                    GUI.color = WithAlpha(stCol, 0.96f * alpha);
                    Widgets.Label(statusRect, telemetry.StatusReadout);
                }

                Text.Anchor = TextAnchor.UpperLeft;
                curY += rowH;
            }

            GUI.color = Color.white;
            return Mathf.Max(0f, curY - topY);
        }

        private static void DrawSymmetricTitleDivider(
            float axisX,
            float ruleY,
            float curTrackW,
            bool compactMode,
            Color baseBorderCol,
            Color brightTrimCol,
            float alpha)
        {
            float outerHalfW = Mathf.Round(curTrackW * 0.42f);
            float innerHalfW = Mathf.Round(curTrackW * 0.22f);
            if (outerHalfW < 16f)
            {
                return;
            }

            Widgets.DrawBoxSolid(
                new Rect(axisX - outerHalfW, ruleY, outerHalfW * 2f, 1f),
                WithAlpha(baseBorderCol, 0.62f * alpha));
            Widgets.DrawBoxSolid(
                new Rect(axisX - innerHalfW, ruleY, innerHalfW * 2f, 1f),
                WithAlpha(brightTrimCol, 0.92f * alpha));

            // Symmetrical outer wing finials
            float finialW = compactMode ? 3f : 4f;
            Widgets.DrawBoxSolid(new Rect(axisX - outerHalfW, ruleY - 1f, finialW, 2f), WithAlpha(brightTrimCol, 0.82f * alpha));
            Widgets.DrawBoxSolid(new Rect(axisX + outerHalfW - finialW, ruleY - 1f, finialW, 2f), WithAlpha(brightTrimCol, 0.82f * alpha));

            // Central axis keystone node locking name center to health bar center
            float keyHalf = compactMode ? 2f : 3f;
            Widgets.DrawBoxSolid(new Rect(axisX - keyHalf, ruleY - 1f, keyHalf * 2f, 3f), WithAlpha(brightTrimCol, 0.96f * alpha));
        }

        private static void DrawArmoredBossBarCasing(
            Rect trackRect,
            Color bgCol,
            Color borderCol,
            Color brightTrimCol,
            bool compactMode,
            float alpha)
        {
            // Symmetrical outer side end-caps flanking left and right edges of trackRect
            if (!compactMode && trackRect.height >= 20f)
            {
                float capH = Mathf.Max(8f, trackRect.height - 8f);
                float capY = Mathf.Round(trackRect.y + (trackRect.height - capH) * 0.5f);
                Color capFill = WithAlpha(Color.Lerp(bgCol, borderCol, 0.35f), alpha);
                Color capBorder = WithAlpha(brightTrimCol, 0.88f * alpha);
                DrawSolidBoxWithBorder(new Rect(trackRect.x - 3f, capY, 3f, capH), capFill, capBorder);
                DrawSolidBoxWithBorder(new Rect(trackRect.xMax, capY, 3f, capH), capFill, capBorder);
            }

            DrawSolidBoxWithBorder(trackRect, WithAlpha(bgCol, alpha), WithAlpha(borderCol, alpha));

            if (!compactMode && trackRect.height >= 20f)
            {
                DrawSolidBoxOutline(trackRect.ContractedBy(1f), new Color(0.03f, 0.035f, 0.045f, 0.92f * alpha));
                DrawSolidBoxOutline(trackRect.ContractedBy(2f), WithAlpha(Color.Lerp(bgCol, borderCol, 0.42f), 0.72f * alpha));
                Rect wellBg = trackRect.ContractedBy(3f);
                Widgets.DrawBoxSolid(wellBg, new Color(0.045f, 0.050f, 0.062f, 0.95f * alpha));
                Widgets.DrawBoxSolid(
                    new Rect(wellBg.x, Mathf.Round(wellBg.center.y), wellBg.width, 1f),
                    new Color(1f, 1f, 1f, 0.04f * alpha));
            }

            DrawCornerBrackets(
                trackRect,
                WithAlpha(brightTrimCol, 0.95f * alpha),
                len: compactMode ? 5f : 9f,
                thickness: compactMode ? 1f : 2f);
        }

        private static void DrawLayeredHealthFill(Rect fillRect, Color baseFillCol, float pct01, float alpha)
        {
            if (fillRect.width <= 0.5f || fillRect.height <= 0.5f)
            {
                return;
            }

            Widgets.DrawBoxSolid(fillRect, WithAlpha(baseFillCol, alpha));

            float h = fillRect.height;
            if (h >= 5f)
            {
                float topBandH = Mathf.Max(2f, Mathf.Round(h * 0.34f));
                Color hiCol = Color.Lerp(baseFillCol, Color.white, 0.25f);
                Widgets.DrawBoxSolid(new Rect(fillRect.x, fillRect.y, fillRect.width, topBandH), WithAlpha(hiCol, alpha));

                Color glossCol = Color.Lerp(baseFillCol, Color.white, 0.44f);
                Widgets.DrawBoxSolid(new Rect(fillRect.x, fillRect.y, fillRect.width, 1f), WithAlpha(glossCol, 0.88f * alpha));

                float botBandH = Mathf.Max(1f, Mathf.Round(h * 0.26f));
                Color shCol = Color.Lerp(baseFillCol, Color.black, 0.30f);
                Widgets.DrawBoxSolid(new Rect(fillRect.x, fillRect.yMax - botBandH, fillRect.width, botBandH), WithAlpha(shCol, alpha));
            }

            if (pct01 > 0.01f && pct01 < 0.995f && fillRect.width >= 3f)
            {
                Color edgeGlow = Color.Lerp(baseFillCol, new Color(1f, 0.96f, 0.82f, 1f), 0.52f);
                Widgets.DrawBoxSolid(new Rect(fillRect.xMax - 2f, fillRect.y, 2f, h), WithAlpha(edgeGlow, 0.92f * alpha));
            }
        }

        private static void DrawSymmetricScaleNotches(
            Rect trackRect,
            Rect wellRect,
            float axisX,
            Color borderCol,
            Color brightTrimCol,
            bool compactMode,
            float alpha)
        {
            if (wellRect.width < 80f)
            {
                return;
            }

            // Central-axis (50%) keystone notches on top and bottom frame edges
            float centerTickH = compactMode ? 2f : 3f;
            Color centerCol = WithAlpha(brightTrimCol, 0.92f * alpha);
            Widgets.DrawBoxSolid(new Rect(axisX - 1f, trackRect.y, 2f, centerTickH), centerCol);
            Widgets.DrawBoxSolid(new Rect(axisX - 1f, trackRect.yMax - centerTickH, 2f, centerTickH), centerCol);
            Widgets.DrawBoxSolid(new Rect(axisX, wellRect.y, 1f, wellRect.height), new Color(0f, 0f, 0f, 0.24f * alpha));

            // Symmetrical 25% and 75% quarter graduation notches mirrored across axisX
            if (!compactMode && wellRect.width >= 180f)
            {
                float qOffset = Mathf.Round(wellRect.width * 0.25f);
                float leftQX = axisX - qOffset;
                float rightQX = axisX + qOffset;
                Color qTickCol = WithAlpha(borderCol, 0.80f * alpha);
                Color qLineCol = new Color(0f, 0f, 0f, 0.18f * alpha);

                Widgets.DrawBoxSolid(new Rect(leftQX, wellRect.y, 1f, 2f), qTickCol);
                Widgets.DrawBoxSolid(new Rect(leftQX, wellRect.yMax - 2f, 1f, 2f), qTickCol);
                Widgets.DrawBoxSolid(new Rect(leftQX, wellRect.y + 2f, 1f, wellRect.height - 4f), qLineCol);

                Widgets.DrawBoxSolid(new Rect(rightQX, wellRect.y, 1f, 2f), qTickCol);
                Widgets.DrawBoxSolid(new Rect(rightQX, wellRect.yMax - 2f, 1f, 2f), qTickCol);
                Widgets.DrawBoxSolid(new Rect(rightQX, wellRect.y + 2f, 1f, wellRect.height - 4f), qLineCol);
            }
        }

        private static void DrawArmoredExpandButton(
            Rect btnRect,
            bool expanded,
            bool hovered,
            Color baseBgCol,
            Color borderCol,
            Color brightTrimCol,
            float barH,
            float btnAlpha)
        {
            Color btnBg = expanded
                ? new Color(0.18f, 0.19f, 0.23f, 0.96f * btnAlpha)
                : (hovered
                    ? new Color(0.14f, 0.16f, 0.20f, 0.96f * btnAlpha)
                    : WithAlpha(baseBgCol, btnAlpha));
            Color outerBorder = expanded || hovered
                ? WithAlpha(brightTrimCol, btnAlpha)
                : WithAlpha(borderCol, btnAlpha);

            DrawSolidBoxWithBorder(btnRect, btnBg, outerBorder);
            if (btnRect.width >= 22f)
            {
                DrawSolidBoxOutline(btnRect.ContractedBy(1f), new Color(0.03f, 0.035f, 0.045f, 0.90f * btnAlpha));
                DrawCornerBrackets(btnRect, WithAlpha(brightTrimCol, 0.92f * btnAlpha), len: 4f, thickness: 1f);
            }

            Color glyphCol = expanded || hovered
                ? new Color(1.00f, 0.96f, 0.84f, btnAlpha)
                : new Color(0.92f, 0.93f, 0.95f, btnAlpha);
            float gcx = Mathf.Round(btnRect.center.x);
            float gcy = Mathf.Round(btnRect.center.y);
            float armHalf = barH >= 22f ? 5f : 3.5f;
            float stroke = barH >= 22f ? 2f : 1.4f;
            Widgets.DrawBoxSolid(new Rect(gcx - armHalf, gcy - stroke * 0.5f, armHalf * 2f, stroke), glyphCol);
            if (!expanded)
            {
                Widgets.DrawBoxSolid(new Rect(gcx - stroke * 0.5f, gcy - armHalf, stroke, armHalf * 2f), glyphCol);
            }
        }

        private static void DrawSolidBoxOutline(Rect rect, Color border)
        {
            if (rect.width <= 1f || rect.height <= 1f)
            {
                return;
            }
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width, 1f), border);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), border);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 1f, rect.height), border);
            Widgets.DrawBoxSolid(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), border);
        }

        private static void DrawCornerBrackets(Rect rect, Color col, float len, float thickness)
        {
            if (rect.width < len * 2f + 2f || rect.height < len * 2f + 2f)
            {
                return;
            }
            // Top-Left
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, len, thickness), col);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, thickness, len), col);
            // Top-Right
            Widgets.DrawBoxSolid(new Rect(rect.xMax - len, rect.y, len, thickness), col);
            Widgets.DrawBoxSolid(new Rect(rect.xMax - thickness, rect.y, thickness, len), col);
            // Bottom-Left
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - thickness, len, thickness), col);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - len, thickness, len), col);
            // Bottom-Right
            Widgets.DrawBoxSolid(new Rect(rect.xMax - len, rect.yMax - thickness, len, thickness), col);
            Widgets.DrawBoxSolid(new Rect(rect.xMax - thickness, rect.yMax - len, thickness, len), col);
        }

        private static Color WithAlpha(Color c, float alphaMul)
        {
            return new Color(c.r, c.g, c.b, c.a * alphaMul);
        }

        private static void DrawSolidBoxWithBorder(Rect rect, Color fill, Color border)
        {
            Widgets.DrawBoxSolid(rect, fill);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width, 1f), border);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), border);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 1f, rect.height), border);
            Widgets.DrawBoxSolid(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), border);
        }
    }
}
