using System;
using System.Collections.Generic;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public enum GiantDebugActionCategory
    {
        Spawn = 0,
        Tools = 1
    }

    public sealed class GiantDebugActionEntry
    {
        public string Id { get; }
        public string LabelKey { get; }
        public string LabelFormatArg { get; }
        public GiantDebugActionCategory Category { get; }
        public bool IsMapTool { get; }
        public bool RequiresMap { get; }

        private readonly Action action;

        public GiantDebugActionEntry(
            string id,
            string labelKey,
            GiantDebugActionCategory category,
            bool isMapTool,
            bool requiresMap,
            Action action)
            : this(id, labelKey, category, isMapTool, requiresMap, action, labelFormatArg: null)
        {
        }

        public GiantDebugActionEntry(
            string id,
            string labelKey,
            GiantDebugActionCategory category,
            bool isMapTool,
            bool requiresMap,
            Action action,
            string labelFormatArg)
        {
            Id = id;
            LabelKey = labelKey;
            LabelFormatArg = labelFormatArg;
            Category = category;
            IsMapTool = isMapTool;
            RequiresMap = requiresMap;
            this.action = action;
        }

        public string GetDisplayLabel()
        {
            string text;
            bool canTranslate = LanguageDatabase.activeLanguage != null && !string.IsNullOrEmpty(LabelKey) && LabelKey.CanTranslate();
            if (!string.IsNullOrEmpty(LabelFormatArg))
            {
                if (canTranslate)
                {
                    string tmpl = LabelKey.Translate().ToString();
                    text = tmpl.IndexOf("{0}", StringComparison.Ordinal) >= 0
                        ? string.Format(tmpl, LabelFormatArg)
                        : LabelKey.Translate(LabelFormatArg).ToString();
                }
                else
                {
                    text = string.Equals(LabelKey, "GCF_DebugAction_SpawnDynamicPlayer", StringComparison.Ordinal)
                        ? LabelFormatArg + " - Player"
                        : (string.Equals(LabelKey, "GCF_DebugAction_SpawnDynamicHostile", StringComparison.Ordinal)
                            ? LabelFormatArg + " - Hostile"
                            : Id);
                }
            }
            else
            {
                text = canTranslate ? LabelKey.Translate().ToString() : Id;
            }
            return IsMapTool ? "T: " + text : text;
        }

        public bool IsAllowedNow()
        {
            if (Current.ProgramState != ProgramState.Playing)
            {
                return false;
            }
            if (RequiresMap && (WorldRendererUtility.WorldSelected || Find.CurrentMap == null))
            {
                return false;
            }
            return true;
        }

        public void Invoke()
        {
            action();
        }
    }

    public static class GiantDebugActionCatalog
    {
        private static readonly HashSet<string> BuiltInGiantKindDefNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "GCF_MechScorpion",
        };

        private static readonly List<GiantDebugActionEntry> extraRegisteredEntries = new List<GiantDebugActionEntry>();

        private static readonly IReadOnlyList<GiantDebugActionEntry> builtInEntries = new GiantDebugActionEntry[]
        {
            new GiantDebugActionEntry(
                "SpawnMechScorpionPlayer",
                "GCF_DebugAction_SpawnScorpionPlayer",
                GiantDebugActionCategory.Spawn,
                isMapTool: true,
                requiresMap: true,
                static () => DebugActions_GiantCreature.SpawnMechScorpion(usePlayerFaction: true)),
            new GiantDebugActionEntry(
                "SpawnMechScorpionHostile",
                "GCF_DebugAction_SpawnScorpionHostile",
                GiantDebugActionCategory.Spawn,
                isMapTool: true,
                requiresMap: true,
                static () => DebugActions_GiantCreature.SpawnMechScorpion(usePlayerFaction: false)),
            new GiantDebugActionEntry(
                "DestroyBoneAtMouse",
                "GCF_DebugAction_DestroyBone",
                GiantDebugActionCategory.Tools,
                isMapTool: true,
                requiresMap: true,
                DebugActions_GiantCreature.DestroyBoneAtMouse),
            new GiantDebugActionEntry(
                "LogHitCellAimingInfo",
                "GCF_DebugAction_LogHitCell",
                GiantDebugActionCategory.Tools,
                isMapTool: true,
                requiresMap: true,
                DebugActions_GiantCreature.LogHitCellAimingInfo),
            new GiantDebugActionEntry(
                "StartLiveFpsTelemetryWindow",
                "GCF_DebugAction_StartFpsTelemetry",
                GiantDebugActionCategory.Tools,
                isMapTool: false,
                requiresMap: true,
                DebugActions_GiantCreature.StartLiveFpsTelemetryWindow),
            new GiantDebugActionEntry(
                "StopAndLogLiveFpsTelemetryWindow",
                "GCF_DebugAction_StopFpsTelemetry",
                GiantDebugActionCategory.Tools,
                isMapTool: false,
                requiresMap: true,
                DebugActions_GiantCreature.StopAndLogLiveFpsTelemetryWindow),
        };

        public static void RegisterExtraAction(GiantDebugActionEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            for (int i = 0; i < extraRegisteredEntries.Count; i++)
            {
                if (string.Equals(extraRegisteredEntries[i].Id, entry.Id, StringComparison.Ordinal))
                {
                    extraRegisteredEntries[i] = entry;
                    InvalidateDynamicCache();
                    return;
                }
            }
            extraRegisteredEntries.Add(entry);
            InvalidateDynamicCache();
        }

        private static int cachedPawnKindCount = -1;
        private static PawnKindDef cachedLastPawnKind;
        private static IReadOnlyList<GiantDebugActionEntry> cachedAllActions;

        public static IReadOnlyList<GiantDebugActionEntry> BuiltInActions => builtInEntries;

        public static void InvalidateDynamicCache()
        {
            cachedPawnKindCount = -1;
            cachedLastPawnKind = null;
            cachedAllActions = null;
        }

        public static IReadOnlyList<GiantDebugActionEntry> AllActions
        {
            get
            {
                List<PawnKindDef> allKinds;
                try
                {
                    allKinds = DefDatabase<PawnKindDef>.AllDefsListForReading;
                }
                catch
                {
                    allKinds = null;
                }

                int curCount = allKinds?.Count ?? 0;
                PawnKindDef curLast = curCount > 0 ? allKinds[curCount - 1] : null;
                if (cachedAllActions != null
                    && cachedPawnKindCount == curCount
                    && ReferenceEquals(cachedLastPawnKind, curLast))
                {
                    return cachedAllActions;
                }

                List<PawnKindDef> dynamicKinds = null;
                for (int i = 0; i < curCount; i++)
                {
                    PawnKindDef kind = allKinds[i];
                    if (kind == null || string.IsNullOrEmpty(kind.defName) || BuiltInGiantKindDefNames.Contains(kind.defName))
                    {
                        continue;
                    }
                    if (HasGiantRigComp(kind))
                    {
                        dynamicKinds ??= new List<PawnKindDef>(8);
                        dynamicKinds.Add(kind);
                    }
                }

                if ((dynamicKinds == null || dynamicKinds.Count == 0) && extraRegisteredEntries.Count == 0)
                {
                    cachedPawnKindCount = curCount;
                    cachedLastPawnKind = curLast;
                    cachedAllActions = builtInEntries;
                    return builtInEntries;
                }

                dynamicKinds?.Sort(static (a, b) => string.CompareOrdinal(a.defName, b.defName));

                var combined = new List<GiantDebugActionEntry>(builtInEntries.Count + extraRegisteredEntries.Count + (dynamicKinds?.Count ?? 0) * 2);
                for (int i = 0; i < builtInEntries.Count; i++)
                {
                    combined.Add(builtInEntries[i]);
                }
                for (int i = 0; i < extraRegisteredEntries.Count; i++)
                {
                    combined.Add(extraRegisteredEntries[i]);
                }

                if (dynamicKinds != null)
                {
                    for (int i = 0; i < dynamicKinds.Count; i++)
                    {
                        PawnKindDef capturedKind = dynamicKinds[i];
                        string displayLabel = ResolveKindDisplayLabel(capturedKind);
                        combined.Add(new GiantDebugActionEntry(
                            "SpawnDynamicPlayer_" + capturedKind.defName,
                            "GCF_DebugAction_SpawnDynamicPlayer",
                            GiantDebugActionCategory.Spawn,
                            isMapTool: true,
                            requiresMap: true,
                            () => DebugActions_GiantCreature.SpawnGiantKindAtMouse(capturedKind, usePlayerFaction: true),
                            displayLabel));
                        combined.Add(new GiantDebugActionEntry(
                            "SpawnDynamicHostile_" + capturedKind.defName,
                            "GCF_DebugAction_SpawnDynamicHostile",
                            GiantDebugActionCategory.Spawn,
                            isMapTool: true,
                            requiresMap: true,
                            () => DebugActions_GiantCreature.SpawnGiantKindAtMouse(capturedKind, usePlayerFaction: false),
                            displayLabel));
                    }
                }

                cachedPawnKindCount = curCount;
                cachedLastPawnKind = curLast;
                cachedAllActions = combined;
                return combined;
            }
        }

        private static bool HasGiantRigComp(PawnKindDef kind)
        {
            ThingDef race = kind?.race;
            if (race?.comps == null)
            {
                return false;
            }
            for (int i = 0; i < race.comps.Count; i++)
            {
                if (race.comps[i] is CompProperties_GiantRig)
                {
                    return true;
                }
            }
            return false;
        }

        private static string ResolveKindDisplayLabel(PawnKindDef kind)
        {
            if (kind == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrEmpty(kind.label))
            {
                if (LanguageDatabase.activeLanguage != null)
                {
                    try
                    {
                        string cap = kind.LabelCap.ToString();
                        if (!string.IsNullOrEmpty(cap))
                        {
                            return cap;
                        }
                    }
                    catch
                    {
                    }
                }
                char first = kind.label[0];
                return char.IsLower(first)
                    ? char.ToUpperInvariant(first) + kind.label.Substring(1)
                    : kind.label;
            }
            return kind.defName ?? string.Empty;
        }
    }

    public class Dialog_GiantDebugPanel : Window
    {
        private Vector2 scrollPos;

        private static string activeToolName = string.Empty;
        private static DebugTool activeOwnedTool;

        public override Vector2 InitialSize => new Vector2(
            Mathf.Min(560f, UI.screenWidth - 40f),
            Mathf.Min(456f, UI.screenHeight - 60f));

        public Dialog_GiantDebugPanel()
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            closeOnClickedOutside = false;
            closeOnCancel = true;
            onlyOneOfTypeAllowed = true;
        }

        public static bool IsPanelOpen =>
            Find.WindowStack != null && Find.WindowStack.IsOpen<Dialog_GiantDebugPanel>();

        public static void OpenWindow()
        {
            if (!GiantCreatureFrameworkMod.Settings.enableDebugPanel || Find.WindowStack == null)
            {
                return;
            }

            if (!IsPanelOpen)
            {
                Find.WindowStack.Add(new Dialog_GiantDebugPanel());
            }
        }

        public static void ToggleWindow()
        {
            if (!GiantCreatureFrameworkMod.Settings.enableDebugPanel || Find.WindowStack == null)
            {
                return;
            }

            if (IsPanelOpen)
            {
                CloseIfOpen();
            }
            else
            {
                OpenWindow();
            }
        }

        public static void CloseIfOpen()
        {
            Find.WindowStack?.TryRemove(typeof(Dialog_GiantDebugPanel), doCloseSound: false);
        }

        public static void ClearCrossSaveState()
        {
            if (activeOwnedTool != null && ReferenceEquals(DebugTools.curTool, activeOwnedTool))
            {
                DebugTools.curTool = null;
            }
            activeOwnedTool = null;
            activeToolName = string.Empty;
            GiantDebugActionCatalog.InvalidateDynamicCache();
        }

        public static void HandlePanelDisabled()
        {
            ClearCrossSaveState();
            CloseIfOpen();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (!GiantCreatureFrameworkMod.Settings.enableDebugPanel)
            {
                Close(doCloseSound: false);
                return;
            }

            float y = inRect.y;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, inRect.width - 36f, 30f), "GCF_DebugPanelTitle".Translate());
            Text.Font = GameFont.Small;
            y += 34f;

            GiantCreatureFrameworkSettings settings = GiantCreatureFrameworkMod.Settings;
            bool prevCol = settings.drawCollisionCells;
            bool prevHit = settings.drawHitCells;
            bool prevSkel = settings.drawSkeleton;

            float toggleW = (inRect.width - 16f) / 3f;
            Widgets.CheckboxLabeled(new Rect(inRect.x, y, toggleW, 24f), "GCF_Setting_DrawCollisionCells".Translate(), ref settings.drawCollisionCells);
            Widgets.CheckboxLabeled(new Rect(inRect.x + toggleW + 8f, y, toggleW, 24f), "GCF_Setting_DrawHitCells".Translate(), ref settings.drawHitCells);
            Widgets.CheckboxLabeled(new Rect(inRect.x + (toggleW + 8f) * 2f, y, toggleW, 24f), "GCF_Setting_DrawSkeleton".Translate(), ref settings.drawSkeleton);
            if (settings.drawCollisionCells != prevCol || settings.drawHitCells != prevHit || settings.drawSkeleton != prevSkel)
            {
                settings.ApplyToRuntime();
            }
            y += 28f;

            if (activeOwnedTool != null && ReferenceEquals(DebugTools.curTool, activeOwnedTool))
            {
                string activeToolText = string.Format("GCF_DebugPanelActiveTool".Translate(), activeToolName);
                Widgets.Label(new Rect(inRect.x, y + 2f, inRect.width - 126f, 24f), activeToolText);
                if (Widgets.ButtonText(new Rect(inRect.xMax - 118f, y, 118f, 24f), "GCF_DebugPanelCancelTool".Translate()))
                {
                    ClearCrossSaveState();
                }
                y += 28f;
            }

            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            y += 8f;

            Rect outRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(60f, inRect.yMax - y));
            float viewW = Mathf.Max(40f, outRect.width - 18f);
            float contentH = CalculateContentHeight();
            Rect viewRect = new Rect(0f, 0f, viewW, Mathf.Max(contentH, outRect.height));

            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);
            try
            {
                float sy = 0f;
                sy = DrawCategorySection(viewRect.width, sy, "GCF_DebugCat_Spawn".Translate(), GiantDebugActionCategory.Spawn);
                sy += 8f;
                DrawCategorySection(viewRect.width, sy, "GCF_DebugCat_Tools".Translate(), GiantDebugActionCategory.Tools);
            }
            finally
            {
                Widgets.EndScrollView();
            }
        }

        private float CalculateContentHeight()
        {
            int spawnCount = 0;
            int toolCount = 0;
            IReadOnlyList<GiantDebugActionEntry> all = GiantDebugActionCatalog.AllActions;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Category == GiantDebugActionCategory.Spawn)
                {
                    spawnCount++;
                }
                else
                {
                    toolCount++;
                }
            }

            const float headerH = 26f;
            const float rowStride = 34f;
            int spawnRows = (spawnCount + 1) / 2;
            int toolRows = (toolCount + 1) / 2;
            return headerH + spawnRows * rowStride + 8f + headerH + toolRows * rowStride + 4f;
        }

        private float DrawCategorySection(float width, float y, string heading, GiantDebugActionCategory category)
        {
            Widgets.Label(new Rect(0f, y, width, 22f), heading);
            y += 24f;

            const float rowH = 28f;
            const float rowGap = 6f;
            const float colGap = 10f;
            float colW = (width - colGap) * 0.5f;

            int visibleIdx = 0;
            IReadOnlyList<GiantDebugActionEntry> all = GiantDebugActionCatalog.AllActions;
            for (int i = 0; i < all.Count; i++)
            {
                GiantDebugActionEntry entry = all[i];
                if (entry.Category != category)
                {
                    continue;
                }

                int row = visibleIdx / 2;
                int col = visibleIdx % 2;
                Rect btnRect = new Rect(col * (colW + colGap), y + row * (rowH + rowGap), colW, rowH);
                bool allowed = entry.IsAllowedNow();

                Color prevColor = GUI.color;
                if (!allowed)
                {
                    GUI.color = new Color(0.6f, 0.6f, 0.6f, 0.7f);
                }

                if (Widgets.ButtonText(btnRect, entry.GetDisplayLabel(), drawBackground: true, doMouseoverSound: true, active: allowed))
                {
                    if (allowed)
                    {
                        ExecuteEntry(entry);
                    }
                }

                GUI.color = prevColor;
                visibleIdx++;
            }

            int rows = (visibleIdx + 1) / 2;
            return y + rows * (rowH + rowGap);
        }

        private void ExecuteEntry(GiantDebugActionEntry entry)
        {
            if (entry.IsMapTool)
            {
                string label = entry.GetDisplayLabel();
                activeToolName = label;
                DebugTool createdTool = null;
                createdTool = new DebugTool(label, () =>
                {
                    if (!entry.IsAllowedNow())
                    {
                        if (ReferenceEquals(DebugTools.curTool, createdTool))
                        {
                            DebugTools.curTool = null;
                        }
                        if (ReferenceEquals(activeOwnedTool, createdTool))
                        {
                            activeOwnedTool = null;
                            activeToolName = string.Empty;
                        }
                        return;
                    }
                    InvokeSafe(entry);
                });
                activeOwnedTool = createdTool;
                DebugTools.curTool = activeOwnedTool;
                return;
            }

            InvokeSafe(entry);
        }

        private static void InvokeSafe(GiantDebugActionEntry entry)
        {
            try
            {
                entry.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error($"[GiantCreatureFramework] Debug panel action '{entry.Id}' failed: {ex}");
            }
        }
    }

    [HarmonyPatch]
    public static class GiantDebugPanelLauncher
    {
        private const int LauncherWindowId = 84729103;
        private const float ButtonWidth = 112f;
        private const float ButtonHeight = 26f;

        [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
        [HarmonyPrefix]
        public static void Prefix()
        {
            if (!GiantCreatureFrameworkMod.Settings.enableDebugPanel)
            {
                return;
            }

            if (Current.ProgramState != ProgramState.Playing || Find.WindowStack == null)
            {
                return;
            }

            if (Find.UIRoot?.screenshotMode?.Active == true)
            {
                return;
            }

            float x = Mathf.Clamp(UI.screenWidth - 310f, 8f, UI.screenWidth - ButtonWidth - 8f);
            Rect launcherRect = new Rect(x, 4f, ButtonWidth, ButtonHeight);

            Find.WindowStack.ImmediateWindow(
                LauncherWindowId,
                launcherRect,
                WindowLayer.GameUI,
                static () =>
                {
                    Rect btnRect = new Rect(0f, 0f, ButtonWidth, ButtonHeight);
                    Text.Font = GameFont.Tiny;
                    if (Widgets.ButtonText(btnRect, "GCF_DebugPanelLauncherBtn".Translate()))
                    {
                        Dialog_GiantDebugPanel.ToggleWindow();
                    }
                    Text.Font = GameFont.Small;
                },
                doBackground: false,
                absorbInputAroundWindow: false,
                shadowAlpha: 0f);
        }
    }
}
