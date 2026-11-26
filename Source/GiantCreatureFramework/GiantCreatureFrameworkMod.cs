using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public class GiantCreatureFrameworkSettings : ModSettings
    {
        public bool drawCollisionCells = false;
        public bool drawHitCells = false;
        public bool drawSkeleton = false;
        public bool enableDebugPanel = false;
        public bool enableAuthoringTerminal = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref drawCollisionCells, "gcf_drawCollisionCells", defaultValue: false);
            Scribe_Values.Look(ref drawHitCells, "gcf_drawHitCells", defaultValue: false);
            Scribe_Values.Look(ref drawSkeleton, "gcf_drawSkeleton", defaultValue: false);
            Scribe_Values.Look(ref enableDebugPanel, "gcf_enableDebugPanel", defaultValue: false);
            Scribe_Values.Look(ref enableAuthoringTerminal, "gcf_enableAuthoringTerminal", defaultValue: false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                ApplyToRuntime();
            }
        }

        public void ResetAllToDefault()
        {
            drawCollisionCells = false;
            drawHitCells = false;
            drawSkeleton = false;
            enableDebugPanel = false;
            enableAuthoringTerminal = false;
            ApplyToRuntime();
            ClearActiveMapDebugStates();
        }

        public void ApplyToRuntime()
        {
            GiantFootprintMapComponent.GlobalDebugDrawCollisionCells = drawCollisionCells;
            GiantFootprintMapComponent.GlobalDebugDrawHitCells = drawHitCells;
            GiantFootprintMapComponent.GlobalDebugDrawFootprints = drawCollisionCells && drawHitCells;
            GiantFootprintMapComponent.GlobalDebugDrawSkeleton = drawSkeleton;
            GiantCreatureFrameworkMod.NotifyDebugPanelState(enableDebugPanel);
            GiantCreatureFrameworkMod.NotifyAuthoringTerminalState(enableAuthoringTerminal);
        }

        public static void ClearActiveMapDebugStates()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
            {
                return;
            }

            List<Map> maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                GiantFootprintMapComponent mapComp = maps[m]?.GetComponent<GiantFootprintMapComponent>();
                if (mapComp?.ActiveGiants == null)
                {
                    continue;
                }
                IReadOnlyList<CompGiantRig> giants = mapComp.ActiveGiants;
                for (int i = 0; i < giants.Count; i++)
                {
                    CompGiantRig comp = giants[i];
                    if (comp == null)
                    {
                        continue;
                    }
                    comp.DebugDrawFootprint = false;
                    comp.DebugDrawSkeleton = false;
                    comp.DebugDrawOnSelect = false;
                }
            }
        }
    }

    public class GiantCreatureFrameworkMod : Mod
    {
        public const string HarmonyId = "sanguo.giantcreatureframework";

        private static GiantCreatureFrameworkSettings settings;
        private static bool? lastNotifiedDebugPanel;
        private static bool? lastNotifiedAuthoringTerminal;
        public static GiantCreatureFrameworkSettings Settings => settings ??= new GiantCreatureFrameworkSettings();
        public static event Action<bool> DebugPanelChanged;
        public static event Action<bool> AuthoringTerminalChanged;

        public static void NotifyDebugPanelState(bool enabled, bool force = false)
        {
            if (!force && lastNotifiedDebugPanel.HasValue && lastNotifiedDebugPanel.Value == enabled)
            {
                return;
            }
            lastNotifiedDebugPanel = enabled;
            if (!enabled)
            {
                Dialog_GiantDebugPanel.HandlePanelDisabled();
            }
            DebugPanelChanged?.Invoke(enabled);
        }

        public static void NotifyAuthoringTerminalState(bool enabled, bool force = false)
        {
            if (!force && lastNotifiedAuthoringTerminal.HasValue && lastNotifiedAuthoringTerminal.Value == enabled)
            {
                return;
            }
            lastNotifiedAuthoringTerminal = enabled;
            AuthoringTerminalChanged?.Invoke(enabled);
        }

        public GiantCreatureFrameworkMod(ModContentPack content) : base(content)
        {
            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(GiantCreatureFrameworkMod).Assembly);
            settings = GetSettings<GiantCreatureFrameworkSettings>();
            settings.ApplyToRuntime();
        }

        public override string SettingsCategory()
        {
            return "GCF_SettingsCategory".CanTranslate()
                ? "GCF_SettingsCategory".Translate().ToString()
                : "Giant Creature Framework";
        }

        private Vector2 settingsScrollPos = Vector2.zero;
        private Vector2 loadedGiantsScrollPos = Vector2.zero;
        private float lastSettingsContentHeight = 760f;
        private float nextLoadedGiantsRefreshTime = -1f;
        private List<LoadedGiantEntry> cachedLoadedGiants;

        public enum GiantSourceKind
        {
            Custom = 0,
            ExternalMod = 1,
            BuiltIn = 2
        }

        public readonly struct LoadedGiantEntry
        {
            public readonly string DisplayName;
            public readonly string SourceLabel;
            public readonly GiantSourceKind SourceKind;

            public LoadedGiantEntry(string displayName, string sourceLabel, GiantSourceKind sourceKind)
            {
                DisplayName = displayName;
                SourceLabel = sourceLabel;
                SourceKind = sourceKind;
            }
        }

        public static string CreaturesFolderPath =>
            System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "GiantCreatureFramework", "Creatures");

        public override void DoSettingsWindowContents(Rect inRect)
        {
            GiantCreatureFrameworkSettings s = Settings;

            float viewHeight = Mathf.Max(inRect.height - 4f, lastSettingsContentHeight);
            Rect viewRect = new Rect(0f, 0f, inRect.width - 20f, viewHeight);
            Widgets.BeginScrollView(inRect, ref settingsScrollPos, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(0f, 0f, viewRect.width, 99999f));

            listing.Label("GCF_SettingsSection_Debug".Translate());
            listing.GapLine(6f);

            listing.CheckboxLabeled(
                "GCF_Setting_DrawCollisionCells".Translate(),
                ref s.drawCollisionCells,
                "GCF_Setting_DrawCollisionCells_Desc".Translate());
            listing.CheckboxLabeled(
                "GCF_Setting_DrawHitCells".Translate(),
                ref s.drawHitCells,
                "GCF_Setting_DrawHitCells_Desc".Translate());
            listing.CheckboxLabeled(
                "GCF_Setting_DrawSkeleton".Translate(),
                ref s.drawSkeleton,
                "GCF_Setting_DrawSkeleton_Desc".Translate());
            listing.CheckboxLabeled(
                "GCF_Setting_EnableDebugPanel".Translate(),
                ref s.enableDebugPanel,
                "GCF_Setting_EnableDebugPanel_Desc".Translate());
            if (s.enableDebugPanel && Current.ProgramState == ProgramState.Playing)
            {
                if (listing.ButtonText("GCF_Setting_OpenDebugPanel".Translate()))
                {
                    Dialog_GiantDebugPanel.OpenWindow();
                }
            }

            listing.Gap(14f);
            listing.Label("GCF_SettingsSection_Authoring".Translate());
            listing.GapLine(6f);
            listing.CheckboxLabeled(
                "GCF_Setting_EnableAuthoringTerminal".Translate(),
                ref s.enableAuthoringTerminal,
                "GCF_Setting_EnableAuthoringTerminal_Desc".Translate());

            DrawLoadedGiantsSection(listing);

            s.ApplyToRuntime();
            if (listing.CurHeight > 100f)
            {
                lastSettingsContentHeight = listing.CurHeight + 16f;
            }
            listing.End();
            Widgets.EndScrollView();
        }

        private void DrawLoadedGiantsSection(Listing_Standard listing)
        {
            float now = Time.realtimeSinceStartup;
            if (cachedLoadedGiants == null || now >= nextLoadedGiantsRefreshTime)
            {
                cachedLoadedGiants = CollectLoadedGiants(Content);
                nextLoadedGiantsRefreshTime = now + 0.5f;
            }

            List<LoadedGiantEntry> entries = cachedLoadedGiants;
            listing.Gap(8f);
            listing.Label("GCF_Setting_LoadedGiantsHeader".Translate(entries.Count));

            const float boxHeight = 180f;
            const float rowHeight = 24f;
            Rect outerBox = listing.GetRect(boxHeight);
            Widgets.DrawMenuSection(outerBox);

            Rect innerBox = outerBox.ContractedBy(6f);
            float contentHeight = Mathf.Max(innerBox.height, entries.Count * rowHeight);
            bool hasVerticalScrollbar = contentHeight > innerBox.height + 0.5f;
            Rect scrollView = new Rect(0f, 0f, innerBox.width - (hasVerticalScrollbar ? 16f : 0f), contentHeight);

            Widgets.BeginScrollView(innerBox, ref loadedGiantsScrollPos, scrollView);
            bool prevWordWrap = Text.WordWrap;
            TextAnchor prevAnchor = Text.Anchor;
            Color prevColor = GUI.color;
            try
            {
                if (entries.Count == 0)
                {
                    Text.Anchor = TextAnchor.MiddleCenter;
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(0f, 0f, scrollView.width, innerBox.height), "GCF_Setting_LoadedGiantsEmpty".Translate());
                }
                else
                {
                    Text.WordWrap = false;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        Rect rowRect = new Rect(0f, i * rowHeight, scrollView.width, rowHeight);
                        if (i % 2 == 1)
                        {
                            Widgets.DrawLightHighlight(rowRect);
                        }
                        if (Mouse.IsOver(rowRect))
                        {
                            Widgets.DrawHighlight(rowRect);
                        }

                        LoadedGiantEntry item = entries[i];
                        float splitX = Mathf.Floor(rowRect.width * 0.60f);
                        Rect leftRect = new Rect(rowRect.x + 8f, rowRect.y, splitX - 12f, rowHeight);
                        Rect rightRect = new Rect(rowRect.x + splitX, rowRect.y, rowRect.width - splitX - 8f, rowHeight);

                        GUI.color = Color.white;
                        Text.Anchor = TextAnchor.MiddleLeft;
                        Widgets.Label(leftRect, item.DisplayName);

                        GUI.color = item.SourceKind switch
                        {
                            GiantSourceKind.Custom => new Color(0.95f, 0.82f, 0.45f),
                            GiantSourceKind.ExternalMod => new Color(0.62f, 0.86f, 0.96f),
                            _ => new Color(0.75f, 0.78f, 0.82f)
                        };
                        Text.Anchor = TextAnchor.MiddleRight;
                        Widgets.Label(rightRect, item.SourceLabel);
                    }
                }
            }
            finally
            {
                Text.WordWrap = prevWordWrap;
                Text.Anchor = prevAnchor;
                GUI.color = prevColor;
            }
            Widgets.EndScrollView();

            listing.Gap(6f);
            if (listing.ButtonText("GCF_Setting_OpenCreaturesFolder".Translate()))
            {
                OpenCreaturesFolder();
            }
        }

        public static List<LoadedGiantEntry> CollectLoadedGiants(ModContentPack selfContent = null)
        {
            var result = new List<LoadedGiantEntry>();
            List<ThingDef> allDefs = DefDatabase<ThingDef>.AllDefsListForReading;
            if (allDefs == null)
            {
                return result;
            }

            for (int i = 0; i < allDefs.Count; i++)
            {
                ThingDef def = allDefs[i];
                if (def?.race == null || def.IsCorpse || def.comps == null)
                {
                    continue;
                }

                bool hasRig = false;
                for (int c = 0; c < def.comps.Count; c++)
                {
                    CompProperties cp = def.comps[c];
                    if (cp is CompProperties_GiantRig || (cp?.compClass != null && typeof(CompGiantRig).IsAssignableFrom(cp.compClass)))
                    {
                        hasRig = true;
                        break;
                    }
                }
                if (!hasRig)
                {
                    continue;
                }

                (GiantSourceKind sourceKind, string sourceLabel) = ResolveGiantSource(def, selfContent);
                string displayName = ResolveCreatureDisplayName(def);
                result.Add(new LoadedGiantEntry(displayName, sourceLabel, sourceKind));
            }

            result.Sort(static (a, b) =>
            {
                int cmp = ((int)a.SourceKind).CompareTo((int)b.SourceKind);
                return cmp != 0 ? cmp : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });

            return result;
        }

        public static (GiantSourceKind Kind, string Label) ResolveGiantSource(ThingDef def, ModContentPack selfContent = null)
        {
            if (def != null && GiantCreatureFramework.Agent.UI.AuthoringTerminalHost.IsCustomAuthoredDef(def))
            {
                return (GiantSourceKind.Custom, TranslateSafe("GCF_GiantSource_Custom", "Custom"));
            }

            if (IsFrameworkBuiltInPack(def?.modContentPack, selfContent))
            {
                return (GiantSourceKind.BuiltIn, TranslateSafe("GCF_GiantSource_BuiltIn", "Built-in"));
            }

            string modDisplayName = ResolveExternalModDisplayName(def?.modContentPack);
            string formatted = LanguageDatabase.activeLanguage != null && "GCF_GiantSource_ExternalMod".CanTranslate()
                ? "GCF_GiantSource_ExternalMod".Translate(modDisplayName).ToString()
                : modDisplayName;
            return (GiantSourceKind.ExternalMod, formatted);
        }

        private static string ResolveCreatureDisplayName(ThingDef def)
        {
            if (def == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(def.label))
            {
                return GenText.CapitalizeFirst(def.label.Trim());
            }

            List<PawnKindDef> kinds = DefDatabase<PawnKindDef>.AllDefsListForReading;
            if (kinds != null)
            {
                for (int i = 0; i < kinds.Count; i++)
                {
                    PawnKindDef k = kinds[i];
                    if (k != null && ReferenceEquals(k.race, def) && !string.IsNullOrWhiteSpace(k.label))
                    {
                        return GenText.CapitalizeFirst(k.label.Trim());
                    }
                }
            }

            return def.defName ?? string.Empty;
        }

        private static bool IsFrameworkBuiltInPack(ModContentPack pack, ModContentPack selfContent)
        {
            if (pack == null)
            {
                return false;
            }
            if (selfContent != null && ReferenceEquals(pack, selfContent))
            {
                return true;
            }
            string pkg = pack.PackageIdPlayerFacing ?? pack.PackageId ?? string.Empty;
            return string.Equals(pkg, "sanguo.giantcreatureframework", StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveExternalModDisplayName(ModContentPack pack)
        {
            if (pack != null)
            {
                string metaName = pack.ModMetaData?.Name;
                if (IsUsableModDisplayName(metaName, pack))
                {
                    return metaName.Trim();
                }

                string lookupId = !string.IsNullOrWhiteSpace(pack.PackageIdPlayerFacing)
                    ? pack.PackageIdPlayerFacing
                    : pack.PackageId;
                if (!string.IsNullOrWhiteSpace(lookupId))
                {
                    try
                    {
                        ModMetaData listed = ModLister.GetActiveModWithIdentifier(lookupId, ignorePostfix: true)
                                          ?? ModLister.GetModWithIdentifier(lookupId, ignorePostfix: true);
                        if (IsUsableModDisplayName(listed?.Name, pack))
                        {
                            return listed.Name.Trim();
                        }
                    }
                    catch
                    {
                    }
                }

                if (!string.IsNullOrWhiteSpace(pack.RootDir))
                {
                    string aboutPath = System.IO.Path.Combine(pack.RootDir, "About", "About.xml");
                    if (System.IO.File.Exists(aboutPath))
                    {
                        try
                        {
                            string xmlName = System.Xml.Linq.XDocument.Load(aboutPath).Root?.Element("name")?.Value;
                            if (IsUsableModDisplayName(xmlName, pack))
                            {
                                return xmlName.Trim();
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                if (IsUsableModDisplayName(pack.Name, pack) && !IsDigitsOnly(pack.Name))
                {
                    return pack.Name.Trim();
                }

                if (IsUsableModDisplayName(pack.FolderName, pack) && !IsDigitsOnly(pack.FolderName))
                {
                    return pack.FolderName.Trim();
                }
            }

            return TranslateSafe("GCF_GiantSource_UnknownMod", "External Mod");
        }

        private static bool IsUsableModDisplayName(string candidate, ModContentPack pack)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }
            string trimmed = candidate.Trim();
            if (!string.IsNullOrWhiteSpace(pack.PackageId) &&
                string.Equals(trimmed, pack.PackageId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(pack.PackageIdPlayerFacing) &&
                string.Equals(trimmed, pack.PackageIdPlayerFacing.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        private static bool IsDigitsOnly(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            string trimmed = text.Trim();
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (!char.IsDigit(trimmed[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static string TranslateSafe(string key, string fallback)
        {
            return LanguageDatabase.activeLanguage != null && key.CanTranslate()
                ? key.Translate().ToString()
                : fallback;
        }

        private static void OpenCreaturesFolder()
        {
            string dir = CreaturesFolderPath;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Warning($"[GiantCreatureFramework] Failed to open creatures folder '{dir}': {ex.Message}");
            }
        }

        public override void WriteSettings()
        {
            Settings.ApplyToRuntime();
            base.WriteSettings();
        }
    }

    /// <summary>
    /// Lock-free O(1) fast lookup for CompGiantRig across 500+ mod colonies where normal Pawns carry 30-40 ThingComps.
    /// Rejects non-giant Pawns via an 8 KB shortHash bitset in L1 cache (~1 ns) and resolves live/corpse giants via ID cache.
    /// </summary>
    public static class GiantFastLookup
    {
        private static readonly ulong[] GiantDefShortHashBits = new ulong[1024];
        private static readonly ulong[] CockpitDefShortHashBits = new ulong[1024];
        private static readonly ulong[] PhaseTransformDefShortHashBits = new ulong[1024];
        private static readonly ulong[] CarrierHangarDefShortHashBits = new ulong[1024];
        private static readonly ulong[] BioferriteCoreDefShortHashBits = new ulong[1024];
        private static volatile bool shortHashTableInitialized;
        private static int activeRigCount;
        private static readonly ConcurrentDictionary<int, CompGiantRig> RigByPawnThingId = new ConcurrentDictionary<int, CompGiantRig>();
        public static bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => System.Threading.Volatile.Read(ref activeRigCount) <= 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool MightBeGiant(Pawn pawn)
        {
            if (pawn?.def == null)
            {
                return false;
            }
            ushort h = pawn.def.shortHash;
            return !shortHashTableInitialized || h == 0 || (GiantDefShortHashBits[h >> 6] & (1UL << (h & 63))) != 0UL;
        }
        private static readonly ConcurrentDictionary<int, CompGiantCockpit> CockpitByPawnThingId = new ConcurrentDictionary<int, CompGiantCockpit>();
        private static readonly ConcurrentDictionary<int, CompGiantPhaseTransform> PhaseTransformByPawnThingId = new ConcurrentDictionary<int, CompGiantPhaseTransform>();
        private static readonly ConcurrentDictionary<int, CompCarrierHangar> CarrierHangarByPawnThingId = new ConcurrentDictionary<int, CompCarrierHangar>();

        public static void BuildDefShortHashTable()
        {
            Array.Clear(GiantDefShortHashBits, 0, GiantDefShortHashBits.Length);
            Array.Clear(CockpitDefShortHashBits, 0, CockpitDefShortHashBits.Length);
            Array.Clear(PhaseTransformDefShortHashBits, 0, PhaseTransformDefShortHashBits.Length);
            Array.Clear(CarrierHangarDefShortHashBits, 0, CarrierHangarDefShortHashBits.Length);
            Array.Clear(BioferriteCoreDefShortHashBits, 0, BioferriteCoreDefShortHashBits.Length);
            List<ThingDef> allDefs = DefDatabase<ThingDef>.AllDefsListForReading;
            if (allDefs != null)
            {
                for (int i = 0; i < allDefs.Count; i++)
                {
                    ThingDef def = allDefs[i];
                    if (def?.comps == null)
                    {
                        continue;
                    }
                    ushort h = def.shortHash;
                    for (int c = 0; c < def.comps.Count; c++)
                    {
                        CompProperties cp = def.comps[c];
                        Type cc = cp?.compClass;
                        if (cp is CompProperties_GiantRig || (cc != null && typeof(CompGiantRig).IsAssignableFrom(cc)))
                        {
                            GiantDefShortHashBits[h >> 6] |= 1UL << (h & 63);
                        }
                        if (cp is CompProperties_GiantCockpit || (cc != null && typeof(CompGiantCockpit).IsAssignableFrom(cc)))
                        {
                            CockpitDefShortHashBits[h >> 6] |= 1UL << (h & 63);
                        }
                        if (cp is CompProperties_GiantPhaseTransform || (cc != null && typeof(CompGiantPhaseTransform).IsAssignableFrom(cc)))
                        {
                            PhaseTransformDefShortHashBits[h >> 6] |= 1UL << (h & 63);
                        }
                        if (cp is CompProperties_CarrierHangar || (cc != null && typeof(CompCarrierHangar).IsAssignableFrom(cc)))
                        {
                            CarrierHangarDefShortHashBits[h >> 6] |= 1UL << (h & 63);
                        }
                        if (cc != null && typeof(CompGiantBioferriteCore).IsAssignableFrom(cc))
                        {
                            BioferriteCoreDefShortHashBits[h >> 6] |= 1UL << (h & 63);
                        }
                    }
                }
            }
            shortHashTableInitialized = true;
        }

        public static void ClearAll()
        {
            if (UnityData.IsInMainThread)
            {
                foreach (var kv in RigByPawnThingId)
                {
                    kv.Value?.ReleaseAllDynamicRenderMeshes();
                }
            }
            RigByPawnThingId.Clear();
            CockpitByPawnThingId.Clear();
            PhaseTransformByPawnThingId.Clear();
            CarrierHangarByPawnThingId.Clear();
            System.Threading.Interlocked.Exchange(ref activeRigCount, 0);
        }

        private static bool IsStaleLookupOwner(Thing parent)
        {
            if (parent == null || parent.Destroyed || parent.Discarded)
            {
                return true;
            }
            if (parent is Pawn p && p.Dead && (p.Corpse == null || p.Corpse.Destroyed || p.Corpse.Discarded))
            {
                return true;
            }
            return false;
        }

        public static void PruneStaleEntries()
        {
            foreach (var kv in RigByPawnThingId)
            {
                if (IsStaleLookupOwner(kv.Value?.parent))
                {
                    if (RigByPawnThingId.TryRemove(kv.Key, out CompGiantRig removed))
                    {
                        if (UnityData.IsInMainThread)
                        {
                            removed?.ReleaseAllDynamicRenderMeshes();
                        }
                        System.Threading.Interlocked.Decrement(ref activeRigCount);
                    }
                }
            }
            if (RigByPawnThingId.IsEmpty)
            {
                System.Threading.Interlocked.Exchange(ref activeRigCount, 0);
            }
            foreach (var kv in CockpitByPawnThingId)
            {
                if (IsStaleLookupOwner(kv.Value?.parent))
                {
                    CockpitByPawnThingId.TryRemove(kv.Key, out _);
                }
            }
            foreach (var kv in PhaseTransformByPawnThingId)
            {
                if (IsStaleLookupOwner(kv.Value?.parent))
                {
                    PhaseTransformByPawnThingId.TryRemove(kv.Key, out _);
                }
            }
            foreach (var kv in CarrierHangarByPawnThingId)
            {
                if (IsStaleLookupOwner(kv.Value?.parent))
                {
                    CarrierHangarByPawnThingId.TryRemove(kv.Key, out _);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RegisterPhaseTransform(Pawn pawn, CompGiantPhaseTransform comp)
        {
            if (pawn == null || comp == null) return;
            if (pawn.def != null)
            {
                ushort h = pawn.def.shortHash;
                PhaseTransformDefShortHashBits[h >> 6] |= 1UL << (h & 63);
            }
            int id = pawn.thingIDNumber;
            if (id != 0) PhaseTransformByPawnThingId[id] = comp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnregisterPhaseTransform(Pawn pawn)
        {
            if (pawn != null && pawn.thingIDNumber != 0)
            {
                PhaseTransformByPawnThingId.TryRemove(pawn.thingIDNumber, out _);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompGiantPhaseTransform TryGetPhaseTransform(Pawn pawn)
        {
            if (pawn?.def == null) return null;
            ushort h = pawn.def.shortHash;
            if (shortHashTableInitialized && h != 0 && (PhaseTransformDefShortHashBits[h >> 6] & (1UL << (h & 63))) == 0UL)
            {
                return null;
            }
            int id = pawn.thingIDNumber;
            if (id != 0 && PhaseTransformByPawnThingId.TryGetValue(id, out CompGiantPhaseTransform cached) && cached?.parent == pawn)
            {
                return cached;
            }
            CompGiantPhaseTransform comp = pawn.TryGetComp<CompGiantPhaseTransform>();
            if (comp != null && id != 0)
            {
                PhaseTransformByPawnThingId[id] = comp;
            }
            return comp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RegisterCarrierHangar(Pawn pawn, CompCarrierHangar comp)
        {
            if (pawn == null || comp == null) return;
            if (pawn.def != null)
            {
                ushort h = pawn.def.shortHash;
                CarrierHangarDefShortHashBits[h >> 6] |= 1UL << (h & 63);
            }
            int id = pawn.thingIDNumber;
            if (id != 0) CarrierHangarByPawnThingId[id] = comp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnregisterCarrierHangar(Pawn pawn)
        {
            if (pawn != null && pawn.thingIDNumber != 0)
            {
                CarrierHangarByPawnThingId.TryRemove(pawn.thingIDNumber, out _);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompCarrierHangar TryGetCarrierHangar(Pawn pawn)
        {
            if (pawn?.def == null) return null;
            ushort h = pawn.def.shortHash;
            if (shortHashTableInitialized && h != 0 && (CarrierHangarDefShortHashBits[h >> 6] & (1UL << (h & 63))) == 0UL)
            {
                return null;
            }
            int id = pawn.thingIDNumber;
            if (id != 0 && CarrierHangarByPawnThingId.TryGetValue(id, out CompCarrierHangar cached) && cached?.parent == pawn)
            {
                return cached;
            }
            CompCarrierHangar comp = pawn.TryGetComp<CompCarrierHangar>();
            if (comp != null && id != 0)
            {
                CarrierHangarByPawnThingId[id] = comp;
            }
            return comp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasBioferriteCore(Pawn pawn)
        {
            if (pawn?.def == null) return false;
            ushort h = pawn.def.shortHash;
            if (shortHashTableInitialized && h != 0)
            {
                return (BioferriteCoreDefShortHashBits[h >> 6] & (1UL << (h & 63))) != 0UL;
            }
            return pawn.TryGetComp<CompGiantBioferriteCore>() != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompGiantRig TryGetRigFromThing(Thing t)
        {
            if (t is Pawn p) return TryGetRig(p);
            if (t is Corpse c && c.InnerPawn != null) return TryGetRig(c.InnerPawn);
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RegisterCockpit(Pawn pawn, CompGiantCockpit comp)
        {
            if (pawn == null || comp == null)
            {
                return;
            }
            if (pawn.def != null)
            {
                ushort h = pawn.def.shortHash;
                CockpitDefShortHashBits[h >> 6] |= 1UL << (h & 63);
            }
            int id = pawn.thingIDNumber;
            if (id != 0)
            {
                CockpitByPawnThingId[id] = comp;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UnregisterCockpit(Pawn pawn)
        {
            if (pawn != null && pawn.thingIDNumber != 0)
            {
                CockpitByPawnThingId.TryRemove(pawn.thingIDNumber, out _);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompGiantCockpit TryGetCockpit(Pawn pawn)
        {
            if (pawn?.def == null)
            {
                return null;
            }
            ushort h = pawn.def.shortHash;
            if (shortHashTableInitialized && h != 0 && (CockpitDefShortHashBits[h >> 6] & (1UL << (h & 63))) == 0UL)
            {
                return null;
            }
            int id = pawn.thingIDNumber;
            if (id != 0 && CockpitByPawnThingId.TryGetValue(id, out CompGiantCockpit cached) && cached?.parent == pawn)
            {
                return cached;
            }
            CompGiantCockpit comp = pawn.TryGetComp<CompGiantCockpit>();
            if (comp != null && id != 0)
            {
                CockpitByPawnThingId[id] = comp;
            }
            return comp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Register(Pawn pawn, CompGiantRig comp)
        {
            if (pawn == null || comp == null)
            {
                return;
            }
            if (pawn.def != null)
            {
                ushort h = pawn.def.shortHash;
                GiantDefShortHashBits[h >> 6] |= 1UL << (h & 63);
            }
            int id = pawn.thingIDNumber;
            if (id != 0)
            {
                if (RigByPawnThingId.TryAdd(id, comp))
                {
                    System.Threading.Interlocked.Increment(ref activeRigCount);
                }
                else
                {
                    RigByPawnThingId[id] = comp;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Unregister(Pawn pawn)
        {
            if (pawn != null && pawn.thingIDNumber != 0)
            {
                if (RigByPawnThingId.TryRemove(pawn.thingIDNumber, out _))
                {
                    System.Threading.Interlocked.Decrement(ref activeRigCount);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static CompGiantRig TryGetRig(Pawn pawn)
        {
            if (pawn?.def == null)
            {
                return null;
            }
            ushort h = pawn.def.shortHash;
            if (shortHashTableInitialized && h != 0 && (GiantDefShortHashBits[h >> 6] & (1UL << (h & 63))) == 0UL)
            {
                return null;
            }
            int id = pawn.thingIDNumber;
            if (id != 0 && RigByPawnThingId.TryGetValue(id, out CompGiantRig cached) && cached?.parent == pawn)
            {
                return cached;
            }
            CompGiantRig comp = pawn.TryGetComp<CompGiantRig>();
            if (comp != null && id != 0)
            {
                if (RigByPawnThingId.TryAdd(id, comp))
                {
                    System.Threading.Interlocked.Increment(ref activeRigCount);
                }
                else
                {
                    RigByPawnThingId[id] = comp;
                }
            }
            return comp;
        }
    }

    public sealed class GameComponent_GiantCacheLifecycle : GameComponent
    {
        private const int PruneIntervalTicks = 600;
        private int pruneTickCounter;

        public static event Action CrossSaveCacheCleared;

        public GameComponent_GiantCacheLifecycle(Game game)
        {
        }

        public static void ClearAllCrossSaveCaches()
        {
            GiantFootprintMapComponent.DisposeAllStaticMapCaches();
            GiantFastLookup.ClearAll();
            GiantVfxSystem.ClearAllProceduralVfx();
            GiantSkinnedSeveredTextureCache.InvalidateAll();
            GiantWorldRenderer.ClearAllWorldMaterialCaches();
            WorldObject_Giant.ClearStaticState();
            GiantTargetAimTracker.ClearAll();
            GiantDormantLockRegistry.ClearAll();
            CompAbilityEffect_AbyssalQuake.ClearAllPendingCrossings();
            GiantArenaService.ResetStaticState();
            HarmonyPatches_Rendering.ClearThreadStaticRenderContext();
            CompGiantRig.ClearThreadStaticPoseCache();
            GiantPartCombatUtility.ClearStaticScratch();
            GiantCombatMechanicsUtility.ClearStaticCaches();
            Dialog_GiantDebugPanel.ClearCrossSaveState();
            DebugActions_GiantCreature.ClearAllStaticDebugRefs();
            CrossSaveCacheCleared?.Invoke();
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            if (++pruneTickCounter >= PruneIntervalTicks)
            {
                pruneTickCounter = 0;
                GiantFastLookup.PruneStaleEntries();
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            pruneTickCounter = 0;
            GiantFastLookup.BuildDefShortHashTable();
            GiantFastLookup.PruneStaleEntries();
        }
    }

    [HarmonyPatch]
    internal static class HarmonyPatches_CrossSaveLifecycle
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Verse.Profile.MemoryUtility), nameof(Verse.Profile.MemoryUtility.ClearAllMapsAndWorld))]
        public static void MemoryUtility_ClearAllMapsAndWorld_Prefix()
        {
            GameComponent_GiantCacheLifecycle.ClearAllCrossSaveCaches();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Game), nameof(Game.ClearCaches))]
        public static void Game_ClearCaches_Postfix()
        {
            GameComponent_GiantCacheLifecycle.ClearAllCrossSaveCaches();
        }
    }

    [StaticConstructorOnStartup]
    public static class GiantCreatureFrameworkBootstrap
    {
        static GiantCreatureFrameworkBootstrap()
        {
            GiantFastLookup.BuildDefShortHashTable();
            GiantCreatureFrameworkMod.Settings.ApplyToRuntime();
            AttachCockpitInspectorTabs();
            GiantRenderIsolationGuard.InitializeCleanRenderStub();
            int rigCount = 0;
            int maskCount = 0;
            foreach (GiantRigDef rig in DefDatabase<GiantRigDef>.AllDefsListForReading)
            {
                rig.BuildTopology();
                rigCount++;
                foreach (BoneNodeDef node in rig.SortedNodes)
                {
                    if (!string.IsNullOrEmpty(node.texPath))
                    {
                        GiantMaterialCache.WarmOnMainThread(node.texPath, UnityEngine.Color.white);
                        TextureAlphaMask mask = GiantAlphaMaskCache.GetOrLoad(node.texPath, node.alphaThreshold);
                        if (mask != null)
                        {
                            maskCount++;
                        }
                    }
                    if (!string.IsNullOrEmpty(node.glowTexPath))
                    {
                        GiantMaterialCache.WarmOnMainThread(node.glowTexPath, node.glowColor, ShaderDatabase.TransparentPostLight);
                    }
                    if (!string.IsNullOrEmpty(node.silhouetteTexPath))
                    {
                        GiantMaterialCache.WarmOnMainThread(node.silhouetteTexPath, UnityEngine.Color.white, ShaderDatabase.Cutout);
                    }
                }
                if (rig.skinnedMeshes != null)
                {
                    foreach (GiantSkinnedMeshDef sm in rig.skinnedMeshes)
                    {
                        if (sm == null) continue;
                        if (!string.IsNullOrEmpty(sm.texPath))
                        {
                            GiantMaterialCache.WarmOnMainThread(sm.texPath, UnityEngine.Color.white, sm.shaderTypeDef?.Shader);
                            if (GiantAlphaMaskCache.GetOrLoad(sm.texPath, sm.alphaThreshold) != null) maskCount++;
                        }
                        if (!string.IsNullOrEmpty(sm.glowTexPath))
                        {
                            GiantMaterialCache.WarmOnMainThread(sm.glowTexPath, sm.glowColor, ShaderDatabase.TransparentPostLight);
                        }
                        if (!string.IsNullOrEmpty(sm.silhouetteTexPath))
                        {
                            GiantMaterialCache.WarmOnMainThread(sm.silhouetteTexPath, UnityEngine.Color.white, ShaderDatabase.Cutout);
                        }
                    }
                }
            }

            Log.Message($"[GiantCreatureFramework] Initialized {rigCount} GiantRigDef(s) and precomputed {maskCount} 512x512 alpha mask(s).");
        }

        public static void AttachCockpitInspectorTabs()
        {
            List<ThingDef> allDefs = DefDatabase<ThingDef>.AllDefsListForReading;
            if (allDefs == null)
            {
                return;
            }
            InspectTabBase sharedTab = null;
            for (int i = 0; i < allDefs.Count; i++)
            {
                ThingDef def = allDefs[i];
                if (def == null || !def.HasComp(typeof(CompGiantCockpit)))
                {
                    continue;
                }
                def.inspectorTabs ??= new List<Type>();
                if (!def.inspectorTabs.Contains(typeof(ITab_GiantCockpit)))
                {
                    def.inspectorTabs.Add(typeof(ITab_GiantCockpit));
                }
                def.inspectorTabsResolved ??= new List<InspectTabBase>();
                sharedTab ??= InspectTabManager.GetSharedInstance(typeof(ITab_GiantCockpit));
                if (sharedTab != null && !def.inspectorTabsResolved.Contains(sharedTab))
                {
                    def.inspectorTabsResolved.Add(sharedTab);
                }
            }
        }
    }
}
