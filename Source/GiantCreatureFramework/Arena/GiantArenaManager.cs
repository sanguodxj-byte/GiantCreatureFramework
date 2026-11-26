using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace GiantCreatureFramework
{
    public static class GiantArenaService
    {
        private static readonly List<Pawn> sideAPawns = new List<Pawn>(8);
        private static readonly List<Pawn> sideBPawns = new List<Pawn>(8);
        private static readonly Dictionary<int, GiantArenaSide> sideByPawnId = new Dictionary<int, GiantArenaSide>(16);

        private static readonly FieldInfo CameraDriverVelocityField =
            AccessTools.Field(typeof(CameraDriver), "velocity");
        private static readonly FieldInfo CameraDriverDesiredDollyField =
            AccessTools.Field(typeof(CameraDriver), "desiredDolly");
        private static readonly FieldInfo CameraDriverDesiredDollyRawField =
            AccessTools.Field(typeof(CameraDriver), "desiredDollyRaw");

        // Continuous floating-point critically-damped camera state (eliminates integer CurrentViewRect quantization stutter)
        private static bool hasSmoothedCamera;
        private static float smoothedCamX;
        private static float smoothedCamZ;
        private static float smoothedCamSize;
        private static float smoothedCamVelX;
        private static float smoothedCamVelZ;
        private static float smoothedCamSizeVel;

        private static bool arenaActive;
        private static int matchCounter;
        private static int activeMatchId;
        private static Map activeMap;
        private static CellRect activeInnerRect = CellRect.Empty;
        private static IntVec3 activeCenter = IntVec3.Invalid;
        private static GiantArenaMatchConfig activeConfig;
        private static GiantArenaMatchStatus matchStatus = GiantArenaMatchStatus.Idle;
        private static GiantArenaSide winnerSide = GiantArenaSide.None;
        private static int matchStartTick = -1;
        private static int winnerTick = -1;
        private static Faction activeFactionA;
        private static Faction activeFactionB;
        private static int lastMaintenanceTick = -999;

        // Recording & presentation state
        private static bool cleanVideoMode;
        private static bool cinematicHudOnly;
        private static bool enableVersusHud;
        private static bool lockNoonClearWeather;

        // Remember user settings to restore cleanly on StopArena
        private static bool savedSettingsCaptured;
        private static bool savedShowColonistBar = true;
        private static bool savedDevMode;
        private static AutomaticPauseMode savedAutoPauseMode = AutomaticPauseMode.MajorThreat;

        // Process-level guard so CLI auto-launch triggers once per startup
        private static bool cliAutoLaunchAttempted;

        // Lazy optional reflection for stopping active MHWildsWeapons combos on match conclusion
        private static bool mhWeaponResetResolved;
        private static Type mhWeaponCompType;
        private static MethodInfo mhWeaponForceEscapeMethod;
        private static FieldInfo mhWeaponActiveMoveField;

        public static bool IsArenaActive => arenaActive && activeMap != null && Current.ProgramState == ProgramState.Playing;
        public static bool IsCleanVideoMode => IsArenaActive && cleanVideoMode;
        public static bool IsCinematicHudOnly => IsArenaActive && cinematicHudOnly;
        public static bool IsMatchConcluded => IsArenaActive && (matchStatus == GiantArenaMatchStatus.Finished || matchStatus == GiantArenaMatchStatus.Draw);
        public static bool ShouldRenderVersusHud(Map map) => IsArenaActive && enableVersusHud && map != null && map == activeMap;
        public static GiantArenaMatchConfig LastConfig => activeConfig?.Clone();

        public static bool ShouldSuppressSpeechBubblesOnMap(Map map)
        {
            if (!IsArenaActive || (!cleanVideoMode && !GiantArenaVideoRecorder.IsRecording))
            {
                return false;
            }
            return map == null || map == activeMap;
        }

        public static bool ShouldBlockPostMatchHostileAction(Pawn pawn)
        {
            if (!IsArenaActive || pawn == null)
            {
                return false;
            }
            bool isArenaPawn = sideByPawnId.ContainsKey(pawn.thingIDNumber) || (activeMap != null && pawn.MapHeld == activeMap);
            if (!isArenaPawn)
            {
                return false;
            }
            if (matchStatus == GiantArenaMatchStatus.Finished || matchStatus == GiantArenaMatchStatus.Draw)
            {
                return true;
            }
            if (matchStatus == GiantArenaMatchStatus.Fighting
                && (sideAPawns.Count > 0 || sideBPawns.Count > 0)
                && (CountCombatCapable(sideAPawns) == 0 || CountCombatCapable(sideBPawns) == 0))
            {
                return true;
            }
            return false;
        }

        public static bool IsHostileOrNonCalmPostMatchJob(Job job)
        {
            if (job?.def == null)
            {
                return false;
            }
            JobDef def = job.def;
            string defName = def.defName ?? string.Empty;
            if ((JobDefOf.Wait != null && def == JobDefOf.Wait)
                || (JobDefOf.Wait_MaintainPosture != null && def == JobDefOf.Wait_MaintainPosture)
                || (JobDefOf.Wait_Downed != null && def == JobDefOf.Wait_Downed)
                || (JobDefOf.LayDown != null && def == JobDefOf.LayDown)
                || string.Equals(defName, "Wait", StringComparison.Ordinal)
                || string.Equals(defName, "Wait_MaintainPosture", StringComparison.Ordinal)
                || string.Equals(defName, "Wait_Downed", StringComparison.Ordinal)
                || string.Equals(defName, "LayDown", StringComparison.Ordinal))
            {
                return false;
            }

            if ((JobDefOf.AttackMelee != null && def == JobDefOf.AttackMelee)
                || (JobDefOf.AttackStatic != null && def == JobDefOf.AttackStatic)
                || (JobDefOf.Wait_Combat != null && def == JobDefOf.Wait_Combat)
                || (JobDefOf.UseVerbOnThing != null && def == JobDefOf.UseVerbOnThing)
                || (JobDefOf.UseVerbOnThingStatic != null && def == JobDefOf.UseVerbOnThingStatic)
                || (JobDefOf.UseVerbOnThingStaticReserve != null && def == JobDefOf.UseVerbOnThingStaticReserve)
                || (JobDefOf.CastAbilityOnThing != null && def == JobDefOf.CastAbilityOnThing)
                || (JobDefOf.CastAbilityOnWorldTile != null && def == JobDefOf.CastAbilityOnWorldTile)
                || (JobDefOf.PredatorHunt != null && def == JobDefOf.PredatorHunt)
                || (JobDefOf.Goto != null && def == JobDefOf.Goto)
                || def.abilityCasting
                || job.ability != null
                || job.verbToUse != null)
            {
                return true;
            }

            return defName.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("CastAbility", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("UseVerb", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("Combat", StringComparison.OrdinalIgnoreCase) >= 0
                || defName.IndexOf("PredatorHunt", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(defName, "Goto", StringComparison.Ordinal);
        }

        public static bool IsOutsideActiveArenaInnerRect(Map map, IntVec3 cell, int margin = 0)
        {
            if (!IsArenaActive || map == null || map != activeMap || activeInnerRect.IsEmpty)
            {
                return false;
            }
            CellRect bounds = margin > 0 ? activeInnerRect.ContractedBy(margin) : activeInnerRect;
            return !bounds.IsEmpty && !bounds.Contains(cell);
        }

        public static bool IsProtectedArenaBoundaryBuilding(Building building)
        {
            return building != null && building.Spawned && IsOutsideActiveArenaInnerRect(building.Map, building.Position, margin: 0);
        }

        public static void ResetStaticState()
        {
            sideAPawns.Clear();
            sideBPawns.Clear();
            sideByPawnId.Clear();
            arenaActive = false;
            activeMap = null;
            activeInnerRect = CellRect.Empty;
            activeCenter = IntVec3.Invalid;
            matchStatus = GiantArenaMatchStatus.Idle;
            winnerSide = GiantArenaSide.None;
            matchStartTick = -1;
            winnerTick = -1;
            activeFactionA = null;
            activeFactionB = null;
            lastMaintenanceTick = -999;
            cleanVideoMode = false;
            cinematicHudOnly = false;
            enableVersusHud = false;
            lockNoonClearWeather = false;
            ResetCameraSmoothing();
            GiantBossHealthBarHUD.ManualStepMode = false;
            GiantBossHealthBarHUD.IsDrawingArenaVersusHud = false;
            GiantArenaVideoRecorder.ForceAbortForReset();
            HarmonyPatches_GiantArena.SetScreenshotModeActive(false);
            HarmonyPatches_GiantArena.EnsureUnpatched();
        }

        internal static void EnsureCleanNonArenaStateOnSessionChange()
        {
            if (arenaActive || activeMap != null || HarmonyPatches_GiantArena.IsPatched || GiantArenaVideoRecorder.IsRecording || savedSettingsCaptured)
            {
                StopArena(restoreSettings: true);
            }
        }

        public static void ResetCameraSmoothing()
        {
            hasSmoothedCamera = false;
            smoothedCamX = 0f;
            smoothedCamZ = 0f;
            smoothedCamSize = 24f;
            smoothedCamVelX = 0f;
            smoothedCamVelZ = 0f;
            smoothedCamSizeVel = 0f;
        }

        public static bool TryGetFighterSide(Pawn pawn, out GiantArenaSide side, out string customSideLabel)
        {
            side = GiantArenaSide.None;
            customSideLabel = string.Empty;
            if (!IsArenaActive || pawn == null)
            {
                return false;
            }
            if (!sideByPawnId.TryGetValue(pawn.thingIDNumber, out side) || side == GiantArenaSide.None)
            {
                return false;
            }
            if (activeConfig != null)
            {
                customSideLabel = side == GiantArenaSide.SideA ? activeConfig.SideALabel : activeConfig.SideBLabel;
            }
            return true;
        }

        public static List<GiantArenaUnitCatalogEntry> ListAvailableUnits(bool includeNonGiants = false)
        {
            var result = new List<GiantArenaUnitCatalogEntry>(16);
            List<PawnKindDef> allKinds = DefDatabase<PawnKindDef>.AllDefsListForReading;
            if (allKinds == null)
            {
                return result;
            }

            for (int i = 0; i < allKinds.Count; i++)
            {
                PawnKindDef kind = allKinds[i];
                ThingDef race = kind?.race;
                if (race?.race == null)
                {
                    continue;
                }

                CompProperties_GiantRig rigProps = race.GetCompProperties<CompProperties_GiantRig>();
                bool isGiant = rigProps?.rigDef != null;
                if (!includeNonGiants && !isGiant)
                {
                    continue;
                }

                CompProperties_GiantCockpit cockpitProps = race.GetCompProperties<CompProperties_GiantCockpit>();
                bool hasCockpit = cockpitProps != null;
                bool isVehicle = isGiant && GiantVehicleBehaviorGuard.IsVehicleDef(
                    rigProps.rigDef,
                    hasCockpit,
                    cockpitProps?.treatHostAsVehicle ?? true);

                string category;
                if (isVehicle)
                {
                    category = "Vehicle";
                }
                else if (race.race.IsMechanoid)
                {
                    category = "Mechanoid";
                }
                else if (race.race.Humanlike)
                {
                    category = "Humanlike";
                }
                else
                {
                    category = "Biological";
                }

                bool canFly = rigProps?.rigDef != null
                    && (rigProps.rigDef.ActiveFlightLocomotion != null
                        || rigProps.rigDef.ActiveFlightVehicleLocomotion != null);

                result.Add(new GiantArenaUnitCatalogEntry
                {
                    DefName = kind.defName ?? string.Empty,
                    RaceDefName = race.defName ?? string.Empty,
                    Label = GiantArenaFighterSnapshot.SanitizeLabel(
                        !string.IsNullOrWhiteSpace(kind.label)
                            ? kind.LabelCap.ToString()
                            : (!string.IsNullOrWhiteSpace(race.label) ? race.LabelCap.ToString() : kind.defName)),
                    Category = category,
                    IsGiantRig = isGiant,
                    RigDefName = rigProps?.rigDef?.defName ?? string.Empty,
                    BodySize = race.race.baseBodySize,
                    CombatPower = kind.combatPower,
                    HasCockpit = hasCockpit,
                    CanFly = canFly,
                    AbilityCount = kind.abilities?.Count ?? 0
                });
            }

            result.Sort((a, b) =>
            {
                int giantCmp = b.IsGiantRig.CompareTo(a.IsGiantRig);
                if (giantCmp != 0) return giantCmp;
                return string.Compare(a.DefName, b.DefName, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public static PawnKindDef ResolvePawnKind(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            string trimmed = query.Trim();
            PawnKindDef exact = DefDatabase<PawnKindDef>.GetNamedSilentFail(trimmed);
            if (exact != null)
            {
                return exact;
            }

            List<PawnKindDef> all = DefDatabase<PawnKindDef>.AllDefsListForReading;
            if (all == null)
            {
                return null;
            }

            // Case-insensitive defName match
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i]?.defName, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return all[i];
                }
            }

            // Match by race defName
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i]?.race?.defName, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return all[i];
                }
            }

            // Short suffix match (e.g. "Rathalos" -> "GCF_Rathalos")
            string prefixed = trimmed.StartsWith("GCF_", StringComparison.OrdinalIgnoreCase) ? trimmed : "GCF_" + trimmed;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i]?.defName, prefixed, StringComparison.OrdinalIgnoreCase))
                {
                    return all[i];
                }
            }

            return null;
        }

        public static GiantArenaMatchState SetupMatch(GiantArenaMatchConfig rawConfig, out string error)
        {
            error = string.Empty;
            Map map = Find.CurrentMap;
            if (map == null || Current.ProgramState != ProgramState.Playing)
            {
                error = "No active RimWorld map is loaded (ProgramState must be Playing).";
                return GetMatchState();
            }

            GiantArenaMatchConfig cfg = (rawConfig ?? new GiantArenaMatchConfig()).Clone();
            cfg.NormalizeInPlace();

            bool sideAIsRaid = cfg.SideARaidPoints > 0f;
            bool sideBIsRaid = cfg.SideBRaidPoints > 0f;

            PawnKindDef kindA = null;
            if (!sideAIsRaid)
            {
                kindA = ResolvePawnKind(cfg.SideAKind);
                if (kindA == null)
                {
                    error = $"Side A PawnKindDef '{cfg.SideAKind}' not found.";
                    return GetMatchState();
                }
                if (string.IsNullOrWhiteSpace(cfg.SideAFactionDef) && kindA.race?.race?.Humanlike == true)
                {
                    cfg.SideAFactionDef = ResolvePreferredHumanlikeFactionDefName(kindA);
                }
            }
            else if (string.IsNullOrWhiteSpace(cfg.SideAFactionDef))
            {
                cfg.SideAFactionDef = InferRaidFactionDefName(cfg.SideAKind);
            }

            PawnKindDef kindB = null;
            if (!sideBIsRaid)
            {
                kindB = ResolvePawnKind(cfg.SideBKind);
                if (kindB == null)
                {
                    error = $"Side B PawnKindDef '{cfg.SideBKind}' not found.";
                    return GetMatchState();
                }
                if (string.IsNullOrWhiteSpace(cfg.SideBFactionDef) && kindB.race?.race?.Humanlike == true)
                {
                    cfg.SideBFactionDef = ResolvePreferredHumanlikeFactionDefName(kindB);
                }
            }
            else if (string.IsNullOrWhiteSpace(cfg.SideBFactionDef))
            {
                cfg.SideBFactionDef = InferRaidFactionDefName(cfg.SideBKind);
            }

            if (!ResolveTwoMutuallyHostileFactions(cfg.SideAFactionDef, cfg.SideBFactionDef, out Faction facA, out Faction facB, out string facErr))
            {
                error = facErr;
                return GetMatchState();
            }

            if (!savedSettingsCaptured)
            {
                savedShowColonistBar = Find.PlaySettings?.showColonistBar ?? true;
                savedDevMode = Prefs.DevMode;
                savedAutoPauseMode = Prefs.AutomaticPauseMode;
                savedSettingsCaptured = true;
            }

            // Suppress auto-pause and clear UI clutter immediately
            Prefs.AutomaticPauseMode = AutomaticPauseMode.Never;
            Find.Selector?.ClearSelection();
            if (Find.Selector?.dragBox != null)
            {
                Find.Selector.dragBox.active = false;
            }

            // Activate arena state before wiping pawns so GameEnder never triggers GameOver with 0 player pawns
            HarmonyPatches_GiantArena.EnsurePatched();
            activeConfig = cfg;
            activeMap = map;
            activeCenter = map.Center;
            activeFactionA = facA;
            activeFactionB = facB;
            arenaActive = true;
            cleanVideoMode = cfg.CleanVideoMode;
            cinematicHudOnly = cfg.CinematicHudOnly;
            enableVersusHud = cfg.EnableVersusHud;
            lockNoonClearWeather = cfg.LockNoonClearWeather;

            if (Find.GameEnder != null)
            {
                Find.GameEnder.gameEnding = false;
            }

            // Clear any queued storyteller raids/events and all existing pawns on the map cleanly
            try { Find.Storyteller?.incidentQueue?.Clear(); } catch { }
            VanishAllMapPawns(map);

            // Reset Boss Health Bar HUD cleanly for fresh match entry animations
            map.GetComponent<GiantFootprintMapComponent>()?.BossHealthBarHud?.Clear();

            // Build preset arena terrain, architecture, and perimeter
            GiantArenaPresetId presetId = GiantArenaMatchConfig.ResolvePresetId(cfg.Preset);
            GiantArenaMapPresets.BuildPresetArena(
                map,
                presetId,
                activeCenter,
                cfg.ArenaHalfWidth,
                cfg.ArenaHalfHeight,
                out activeInnerRect);

            // Sweep again after unfogging/rebuilding arena so no Ancient Danger or casket pawns survive
            VanishAllMapPawns(map);

            // Apply lighting & weather
            if (cfg.LockNoonClearWeather)
            {
                ApplyNoonAndClearWeather(map);
            }

            // Apply recording presentation flags
            ApplyRecordingSettings(
                cfg.CleanVideoMode,
                cfg.CinematicHudOnly,
                cfg.EnableVersusHud,
                cfg.HideDevToolbar,
                disableDebugOverlays: cfg.CleanVideoMode);

            // Spawn Side A (West, facing East) and Side B (East, facing West)
            sideAPawns.Clear();
            sideBPawns.Clear();
            sideByPawnId.Clear();

            int maxHalfSep = Math.Max(6, (activeInnerRect.Width / 2) - 8);
            int halfSep = Mathf.Clamp(cfg.SpawnSeparation / 2, 6, maxHalfSep);
            int xA = Mathf.Clamp(activeCenter.x - halfSep, activeInnerRect.minX + 6, activeCenter.x - 4);
            int xB = Mathf.Clamp(activeCenter.x + halfSep, activeCenter.x + 4, activeInnerRect.maxX - 6);

            if (sideAIsRaid)
            {
                if (!SpawnRaidTeam(map, GiantArenaSide.SideA, facA, cfg.SideARaidPoints, cfg.SideAVanillaOnly, xA, activeCenter.z, headingDeg: 90f, rot: Rot4.East, sideAPawns, out string raidErrA))
                {
                    error = raidErrA;
                    return GetMatchState();
                }
            }
            else
            {
                SpawnTeam(map, GiantArenaSide.SideA, kindA, cfg.SideACount, facA, xA, activeCenter.z, headingDeg: 90f, rot: Rot4.East, cfg.BoardCrewForVehicles, sideAPawns);
            }

            if (sideBIsRaid)
            {
                if (!SpawnRaidTeam(map, GiantArenaSide.SideB, facB, cfg.SideBRaidPoints, cfg.SideBVanillaOnly, xB, activeCenter.z, headingDeg: 270f, rot: Rot4.West, sideBPawns, out string raidErrB))
                {
                    error = raidErrB;
                    return GetMatchState();
                }
            }
            else
            {
                SpawnTeam(map, GiantArenaSide.SideB, kindB, cfg.SideBCount, facB, xB, activeCenter.z, headingDeg: 270f, rot: Rot4.West, cfg.BoardCrewForVehicles, sideBPawns);
            }

            // Refresh path grids, footprints, tweeners, and attack target cache
            GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
            RefreshCombatLocks(forceJobOverride: true);

            // Clear any stray letters or messages emitted during teardown/spawn
            ClearLettersAndMessages();

            matchCounter++;
            activeMatchId = matchCounter;
            matchStartTick = Find.TickManager?.TicksGame ?? 0;
            winnerTick = -1;
            winnerSide = GiantArenaSide.None;
            matchStatus = cfg.StartPaused ? GiantArenaMatchStatus.Ready : GiantArenaMatchStatus.Fighting;
            lastMaintenanceTick = matchStartTick;

            // Frame camera immediately on both sides
            ResetCameraSmoothing();
            if (cfg.CameraRootSize > 0.1f)
            {
                Vector3 cPos = activeCenter.ToVector3Shifted();
                hasSmoothedCamera = true;
                smoothedCamX = cPos.x;
                smoothedCamZ = cPos.z;
                smoothedCamSize = cfg.CameraRootSize;
                ApplyCameraTransformClean(Find.CameraDriver, cPos, cfg.CameraRootSize);
            }
            else
            {
                FrameMatchCamera(smoothFactor: 1f, paddingCells: 10f, minRootSize: 16f, maxRootSize: 44f);
            }

            if (Find.TickManager != null)
            {
                Find.TickManager.CurTimeSpeed = cfg.StartPaused ? TimeSpeed.Paused : TimeSpeed.Normal;
            }

            GiantArenaMatchState state = GetMatchState();
            Log.Message(
                $"[GCF_ARENA_SETUP] matchId={state.MatchId} preset={state.Preset} " +
                $"sideA={cfg.SideAKind}x{sideAPawns.Count}({facA?.def?.defName}) " +
                $"sideB={cfg.SideBKind}x{sideBPawns.Count}({facB?.def?.defName}) " +
                $"mutuallyHostile={state.FactionsMutuallyHostile} paused={state.Paused} " +
                $"cam=({state.RecommendedCamera.CenterX:F1},{state.RecommendedCamera.CenterZ:F1},size={state.RecommendedCamera.RootSize:F1})");
            return state;
        }

        public static GiantArenaMatchState BindCustomMapEncounter(
            Map map,
            IntVec3 arenaCenter,
            CellRect innerRect,
            IReadOnlyList<Pawn> customSideA,
            Faction facA,
            IReadOnlyList<Pawn> customSideB,
            Faction facB,
            GiantArenaMatchConfig cfg,
            out string error)
        {
            error = string.Empty;
            if (map == null)
            {
                error = "Target map is null.";
                return GetMatchState();
            }
            if (customSideA == null || customSideA.Count == 0 || customSideB == null || customSideB.Count == 0)
            {
                error = "Both customSideA and customSideB must contain at least one spawned Pawn.";
                return GetMatchState();
            }

            cfg ??= new GiantArenaMatchConfig();
            if (!savedSettingsCaptured)
            {
                savedShowColonistBar = Find.PlaySettings?.showColonistBar ?? true;
                savedDevMode = Prefs.DevMode;
                savedAutoPauseMode = Prefs.AutomaticPauseMode;
                savedSettingsCaptured = true;
            }

            Prefs.AutomaticPauseMode = AutomaticPauseMode.Never;
            if (Current.Game != null && Current.Game.CurrentMap != map)
            {
                Current.Game.CurrentMap = map;
            }
            Find.Selector?.ClearSelection();
            if (Find.Selector?.dragBox != null)
            {
                Find.Selector.dragBox.active = false;
            }

            HarmonyPatches_GiantArena.EnsurePatched();
            activeConfig = cfg;
            activeMap = map;
            activeCenter = arenaCenter.IsValid ? arenaCenter : map.Center;
            activeInnerRect = innerRect.Area > 0 ? innerRect.ClipInsideMap(map) : CellRect.WholeMap(map).ContractedBy(4);
            activeFactionA = facA;
            activeFactionB = facB;
            arenaActive = true;
            cleanVideoMode = cfg.CleanVideoMode;
            cinematicHudOnly = cfg.CinematicHudOnly;
            enableVersusHud = cfg.EnableVersusHud;
            lockNoonClearWeather = cfg.LockNoonClearWeather;

            if (Find.GameEnder != null)
            {
                Find.GameEnder.gameEnding = false;
            }
            try { Find.Storyteller?.incidentQueue?.Clear(); } catch { }

            map.GetComponent<GiantFootprintMapComponent>()?.BossHealthBarHud?.Clear();

            sideAPawns.Clear();
            sideBPawns.Clear();
            sideByPawnId.Clear();

            for (int i = 0; i < customSideA.Count; i++)
            {
                Pawn p = customSideA[i];
                if (p != null && p.Spawned && p.Map == map)
                {
                    sideAPawns.Add(p);
                    sideByPawnId[p.thingIDNumber] = GiantArenaSide.SideA;
                }
            }
            for (int i = 0; i < customSideB.Count; i++)
            {
                Pawn p = customSideB[i];
                if (p != null && p.Spawned && p.Map == map)
                {
                    sideBPawns.Add(p);
                    sideByPawnId[p.thingIDNumber] = GiantArenaSide.SideB;
                }
            }

            VanishNonArenaMapPawns(map);

            if (cfg.LockNoonClearWeather)
            {
                ApplyNoonAndClearWeather(map);
            }

            ApplyRecordingSettings(
                cfg.CleanVideoMode,
                cfg.CinematicHudOnly,
                cfg.EnableVersusHud,
                cfg.HideDevToolbar,
                disableDebugOverlays: cfg.CleanVideoMode);

            GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
            RefreshCombatLocks(forceJobOverride: true);
            ClearLettersAndMessages();

            matchCounter++;
            activeMatchId = matchCounter;
            matchStartTick = Find.TickManager?.TicksGame ?? 0;
            winnerTick = -1;
            winnerSide = GiantArenaSide.None;
            matchStatus = cfg.StartPaused ? GiantArenaMatchStatus.Ready : GiantArenaMatchStatus.Fighting;
            lastMaintenanceTick = matchStartTick;

            ResetCameraSmoothing();
            if (cfg.CameraRootSize > 0.1f)
            {
                Vector3 cPos = activeCenter.ToVector3Shifted();
                hasSmoothedCamera = true;
                smoothedCamX = cPos.x;
                smoothedCamZ = cPos.z;
                smoothedCamSize = cfg.CameraRootSize;
                ApplyCameraTransformClean(Find.CameraDriver, cPos, cfg.CameraRootSize);
            }
            else
            {
                FrameMatchCamera(smoothFactor: 1f, paddingCells: 10f, minRootSize: 15f, maxRootSize: 40f);
            }

            if (Find.TickManager != null)
            {
                Find.TickManager.CurTimeSpeed = cfg.StartPaused ? TimeSpeed.Paused : TimeSpeed.Normal;
            }

            return GetMatchState();
        }

        public static GiantArenaMatchState ResetRematch(bool swapSides, out string error)
        {
            if (activeConfig == null)
            {
                return SetupMatch(new GiantArenaMatchConfig(), out error);
            }

            GiantArenaMatchConfig next = activeConfig.Clone();
            if (swapSides)
            {
                (next.SideAKind, next.SideBKind) = (next.SideBKind, next.SideAKind);
                (next.SideACount, next.SideBCount) = (next.SideBCount, next.SideACount);
                (next.SideARaidPoints, next.SideBRaidPoints) = (next.SideBRaidPoints, next.SideARaidPoints);
                (next.SideAVanillaOnly, next.SideBVanillaOnly) = (next.SideBVanillaOnly, next.SideAVanillaOnly);
                (next.SideALabel, next.SideBLabel) = (next.SideBLabel, next.SideALabel);
                (next.SideAFactionDef, next.SideBFactionDef) = (next.SideBFactionDef, next.SideAFactionDef);
            }
            return SetupMatch(next, out error);
        }

        public static void StopArena(bool restoreSettings = true)
        {
            ResetStaticState();
            if (restoreSettings && savedSettingsCaptured)
            {
                if (Find.PlaySettings != null)
                {
                    Find.PlaySettings.showColonistBar = savedShowColonistBar;
                }
                Prefs.DevMode = savedDevMode;
                Prefs.AutomaticPauseMode = savedAutoPauseMode;
                savedSettingsCaptured = false;
            }
        }

        public static void ApplyRecordingSettings(
            bool? setCleanVideoMode = null,
            bool? setCinematicHudOnly = null,
            bool? setEnableVersusHud = null,
            bool? setHideDevToolbar = null,
            bool? disableDebugOverlays = null)
        {
            if (setCleanVideoMode.HasValue)
            {
                cleanVideoMode = setCleanVideoMode.Value;
                if (activeConfig != null) activeConfig.CleanVideoMode = cleanVideoMode;
            }
            if (setCinematicHudOnly.HasValue)
            {
                cinematicHudOnly = setCinematicHudOnly.Value;
                if (activeConfig != null) activeConfig.CinematicHudOnly = cinematicHudOnly;
            }
            if (setEnableVersusHud.HasValue)
            {
                enableVersusHud = setEnableVersusHud.Value;
                if (activeConfig != null) activeConfig.EnableVersusHud = enableVersusHud;
            }
            if (setHideDevToolbar.HasValue)
            {
                if (activeConfig != null) activeConfig.HideDevToolbar = setHideDevToolbar.Value;
                Prefs.DevMode = !setHideDevToolbar.Value;
            }

            if (cleanVideoMode && Find.PlaySettings != null)
            {
                Find.PlaySettings.showColonistBar = false;
            }

            if (disableDebugOverlays == true)
            {
                GiantCreatureFrameworkSettings s = GiantCreatureFrameworkMod.Settings;
                s.drawCollisionCells = false;
                s.drawHitCells = false;
                s.drawSkeleton = false;
                s.ApplyToRuntime();
                GiantCreatureFrameworkSettings.ClearActiveMapDebugStates();
            }

            if (cleanVideoMode)
            {
                ClearLettersAndMessages();
            }

            HarmonyPatches_GiantArena.SetScreenshotModeActive(IsCinematicHudOnly);
            if (IsCinematicHudOnly)
            {
                CloseAllOpenWindows();
            }
        }

        public static void CloseAllOpenWindows()
        {
            try
            {
                EditWindow_Log.wantsToOpen = false;
            }
            catch
            {
            }
            try
            {
                WindowStack stack = Find.WindowStack;
                if (stack?.Windows != null && stack.Windows.Count > 0)
                {
                    var copy = new List<Window>(stack.Windows);
                    for (int i = copy.Count - 1; i >= 0; i--)
                    {
                        Window w = copy[i];
                        if (w != null)
                        {
                            stack.TryRemove(w, doCloseSound: false);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public static GiantArenaCameraRecommendation ComputeRecommendedCamera(
            float paddingCells = 10f,
            float minRootSize = 15f,
            float maxRootSize = 46f)
        {
            float fallbackX = activeCenter.IsValid ? activeCenter.x + 0.5f : (Find.CurrentMap != null ? Find.CurrentMap.Center.x + 0.5f : 100f);
            float fallbackZ = activeCenter.IsValid ? activeCenter.z + 0.5f : (Find.CurrentMap != null ? Find.CurrentMap.Center.z + 0.5f : 100f);

            var living = new List<Pawn>(sideAPawns.Count + sideBPawns.Count);
            var fallbackAll = new List<Pawn>(sideAPawns.Count + sideBPawns.Count);

            CollectCameraTargets(sideAPawns, living, fallbackAll);
            CollectCameraTargets(sideBPawns, living, fallbackAll);

            bool matchConcluded = matchStatus == GiantArenaMatchStatus.Finished || matchStatus == GiantArenaMatchStatus.Draw;
            List<Pawn> source = (matchConcluded && fallbackAll.Count > 0)
                ? fallbackAll
                : (living.Count > 0 ? living : fallbackAll);
            if (source.Count == 0)
            {
                return new GiantArenaCameraRecommendation
                {
                    CenterX = fallbackX,
                    CenterZ = fallbackZ,
                    RootSize = Mathf.Clamp(24f, minRootSize, maxRootSize),
                    SpanX = 0f,
                    SpanZ = 0f
                };
            }

            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minZ = float.MaxValue;
            float maxZ = float.MinValue;

            for (int i = 0; i < source.Count; i++)
            {
                Pawn p = source[i];
                Vector3 pos = GiantMotionOverrideRegistry.TryGetArenaCameraAnchorXZ(p, out Vector2 overrideCamXZ)
                    ? new Vector3(overrideCamXZ.x, 0f, overrideCamXZ.y)
                    : ResolveWorldDrawPos(p);
                float bodyRadius = Mathf.Clamp(Mathf.Sqrt(Mathf.Max(1f, p.BodySize)) * 1.45f, 1.5f, 6.5f);
                if (pos.x - bodyRadius < minX) minX = pos.x - bodyRadius;
                if (pos.x + bodyRadius > maxX) maxX = pos.x + bodyRadius;
                if (pos.z - bodyRadius < minZ) minZ = pos.z - bodyRadius;
                if (pos.z + bodyRadius > maxZ) maxZ = pos.z + bodyRadius;
            }

            return ComputeCameraFromBounds(minX, maxX, minZ, maxZ, paddingCells, minRootSize, maxRootSize);
        }

        public static GiantArenaCameraRecommendation ComputeCameraFromBounds(
            float minX,
            float maxX,
            float minZ,
            float maxZ,
            float paddingCells = 10f,
            float minRootSize = 15f,
            float maxRootSize = 46f,
            float aspectRatio = 16f / 9f)
        {
            float cx = (minX + maxX) * 0.5f;
            float cz = (minZ + maxZ) * 0.5f;
            float spanX = Mathf.Max(0f, maxX - minX);
            float spanZ = Mathf.Max(0f, maxZ - minZ);

            float safeAspect = Mathf.Clamp(aspectRatio > 0.2f ? aspectRatio : (16f / 9f), 1.0f, 3.0f);
            float reqHalfHFromZ = (spanZ + paddingCells * 2f) * 0.5f;
            float reqHalfHFromX = ((spanX + paddingCells * 2f) * 0.5f) / safeAspect;
            // RimWorld CameraDriver rootSize corresponds closely to visible vertical half-extent in cells
            float desiredRootSize = Mathf.Max(reqHalfHFromZ, reqHalfHFromX) * 1.12f;
            float clampedRootSize = Mathf.Clamp(desiredRootSize, minRootSize, maxRootSize);

            return new GiantArenaCameraRecommendation
            {
                CenterX = cx,
                CenterZ = cz,
                RootSize = clampedRootSize,
                SpanX = spanX,
                SpanZ = spanZ
            };
        }

        public static void StepCriticallyDamped(
            ref float current,
            float target,
            ref float velocity,
            float smoothTime,
            float maxSpeed,
            float dt)
        {
            if (dt <= 1e-6f)
            {
                return;
            }
            float safeSmooth = Mathf.Max(0.001f, smoothTime);
            float omega = 2f / safeSmooth;
            float x = omega * dt;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float origTarget = target;

            float maxChange = maxSpeed * safeSmooth;
            change = Mathf.Clamp(change, -maxChange, maxChange);
            float clampedTarget = current - change;

            float temp = (velocity + omega * change) * dt;
            velocity = (velocity - omega * temp) * exp;
            float output = clampedTarget + (change + temp) * exp;

            // Prevent numerical overshoot past target
            if ((origTarget - current > 0f) == (output > origTarget))
            {
                output = origTarget;
                velocity = (output - origTarget) / dt;
            }
            current = output;
        }

        public static void StepArenaCameraSmoothing(
            float targetX,
            float targetZ,
            float targetSize,
            float smoothFactor,
            float dtSeconds,
            ref bool hasState,
            ref float curX,
            ref float curZ,
            ref float curSize,
            ref float velX,
            ref float velZ,
            ref float velSize)
        {
            float t = Mathf.Clamp01(smoothFactor);
            if (!hasState || t >= 0.999f || dtSeconds <= 1e-6f)
            {
                curX = targetX;
                curZ = targetZ;
                curSize = targetSize;
                velX = 0f;
                velZ = 0f;
                velSize = 0f;
                hasState = true;
                return;
            }

            // Convert legacy per-frame smoothFactor (e.g. 0.07 at 60fps) into a C1-smooth critically-damped time constant
            float posSmoothTime = Mathf.Clamp(0.0385f / Mathf.Max(0.01f, t), 0.14f, 1.80f);
            float dt = Mathf.Clamp(dtSeconds, 1f / 240f, 0.1f);

            StepCriticallyDamped(ref curX, targetX, ref velX, posSmoothTime, maxSpeed: 65f, dt);
            StepCriticallyDamped(ref curZ, targetZ, ref velZ, posSmoothTime, maxSpeed: 65f, dt);

            // Asymmetric smooth zoom with gentle deadzone so breathing/stepping never causes zoom pumping
            float sizeDelta = targetSize - curSize;
            float effectiveTargetSize = Mathf.Abs(sizeDelta) < 0.25f ? curSize : targetSize;
            float sizeSmoothTime = sizeDelta >= 0f ? (posSmoothTime * 1.15f) : (posSmoothTime * 1.65f);
            StepCriticallyDamped(ref curSize, effectiveTargetSize, ref velSize, sizeSmoothTime, maxSpeed: 24f, dt);
        }

        private static void ApplyCameraTransformClean(CameraDriver cam, Vector3 rootPos, float rootSize)
        {
            if (cam == null)
            {
                return;
            }
            try
            {
                CameraDriverVelocityField?.SetValue(cam, Vector3.zero);
                CameraDriverDesiredDollyField?.SetValue(cam, Vector2.zero);
                CameraDriverDesiredDollyRawField?.SetValue(cam, Vector2.zero);
            }
            catch
            {
            }
            cam.SetRootPosAndSize(rootPos, rootSize);
            try
            {
                if (Current.SubcameraDriver != null && cam.MyCamera != null)
                {
                    Current.SubcameraDriver.UpdatePositions(cam.MyCamera);
                }
            }
            catch
            {
            }
        }

        public static void SyncPawnTweenersForLockstepFrame(Map map, int ticksStepped = 1)
        {
            if (map?.mapPawns?.AllPawnsSpawned == null)
            {
                return;
            }
            int safeTicks = Math.Max(1, ticksStepped);
            CellRect fullMapRect = CellRect.WholeMap(map);
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || !p.Spawned)
                {
                    continue;
                }
                if (GiantFastLookup.TryGetRig(p) != null)
                {
                    p.Drawer?.tweener?.ResetTweenedPosToRoot();
                    p.rotationTracker?.ProcessPostTickVisuals(safeTicks);
                }
                else
                {
                    // RimWorld 1.7 skips PostTickVisuals when TickManager.Paused == true (TicksThisFrame == 0).
                    // Explicitly advance both smooth sub-cell Drawer tweening and Pawn_RotationTracker.UpdateRotation()!
                    p.ProcessPostTickVisuals(safeTicks, fullMapRect);
                }
            }
        }

        public static GiantArenaCameraRecommendation FrameMatchCamera(
            float smoothFactor = 1f,
            float paddingCells = 10f,
            float minRootSize = 15f,
            float maxRootSize = 46f,
            float dtSeconds = 1f / 60f)
        {
            float aspect = (UI.screenHeight > 10 && UI.screenWidth > 10)
                ? ((float)UI.screenWidth / UI.screenHeight)
                : (16f / 9f);

            GiantArenaCameraRecommendation rec = ComputeRecommendedCamera(paddingCells, minRootSize, maxRootSize);
            // Re-evaluate with exact live viewport aspect ratio
            if (Mathf.Abs(aspect - (16f / 9f)) > 0.02f)
            {
                rec = ComputeCameraFromBounds(
                    rec.CenterX - rec.SpanX * 0.5f,
                    rec.CenterX + rec.SpanX * 0.5f,
                    rec.CenterZ - rec.SpanZ * 0.5f,
                    rec.CenterZ + rec.SpanZ * 0.5f,
                    paddingCells,
                    minRootSize,
                    maxRootSize,
                    aspect);
            }

            StepArenaCameraSmoothing(
                rec.CenterX,
                rec.CenterZ,
                rec.RootSize,
                smoothFactor,
                dtSeconds,
                ref hasSmoothedCamera,
                ref smoothedCamX,
                ref smoothedCamZ,
                ref smoothedCamSize,
                ref smoothedCamVelX,
                ref smoothedCamVelZ,
                ref smoothedCamSizeVel);

            CameraDriver cam = Find.CameraDriver;
            if (cam != null)
            {
                ApplyCameraTransformClean(cam, new Vector3(smoothedCamX, 0f, smoothedCamZ), smoothedCamSize);
            }

            return rec;
        }

        public static GiantArenaMatchState GetMatchState()
        {
            int curTick = Find.TickManager?.TicksGame ?? 0;
            bool paused = Find.TickManager?.Paused ?? false;
            var state = new GiantArenaMatchState
            {
                Active = IsArenaActive,
                MatchId = activeMatchId,
                Preset = activeConfig?.Preset ?? string.Empty,
                Status = matchStatus.ToString(),
                WinnerSide = winnerSide.ToString(),
                StartTick = matchStartTick,
                CurrentTick = curTick,
                ElapsedTicks = matchStartTick >= 0 ? Math.Max(0, curTick - matchStartTick) : 0,
                ElapsedSeconds = matchStartTick >= 0 ? Math.Max(0, curTick - matchStartTick) / 60f : 0f,
                WinnerTick = winnerTick,
                Paused = paused,
                ArenaCenterX = activeCenter.IsValid ? activeCenter.x : 0,
                ArenaCenterZ = activeCenter.IsValid ? activeCenter.z : 0,
                ArenaMinX = activeInnerRect.IsEmpty ? 0 : activeInnerRect.minX,
                ArenaMaxX = activeInnerRect.IsEmpty ? 0 : activeInnerRect.maxX,
                ArenaMinZ = activeInnerRect.IsEmpty ? 0 : activeInnerRect.minZ,
                ArenaMaxZ = activeInnerRect.IsEmpty ? 0 : activeInnerRect.maxZ,
                SideAFaction = activeFactionA?.def?.defName ?? string.Empty,
                SideBFaction = activeFactionB?.def?.defName ?? string.Empty,
                FactionsMutuallyHostile = activeFactionA != null && activeFactionB != null
                    && activeFactionA != activeFactionB
                    && activeFactionA.HostileTo(activeFactionB)
                    && activeFactionB.HostileTo(activeFactionA)
            };

            float sumHpA = 0f;
            int aliveA = 0;
            for (int i = 0; i < sideAPawns.Count; i++)
            {
                GiantArenaFighterSnapshot snap = BuildFighterSnapshot(sideAPawns[i], GiantArenaSide.SideA);
                state.Fighters.Add(snap);
                sumHpA += snap.HealthPercent;
                if (!snap.Dead && !snap.Downed && snap.Spawned) aliveA++;
            }

            float sumHpB = 0f;
            int aliveB = 0;
            for (int i = 0; i < sideBPawns.Count; i++)
            {
                GiantArenaFighterSnapshot snap = BuildFighterSnapshot(sideBPawns[i], GiantArenaSide.SideB);
                state.Fighters.Add(snap);
                sumHpB += snap.HealthPercent;
                if (!snap.Dead && !snap.Downed && snap.Spawned) aliveB++;
            }

            state.SideAAlive = aliveA;
            state.SideBAlive = aliveB;
            state.SideATotalHealthPercent = ShouldUseArmyAggregateBar(GiantArenaSide.SideA)
                ? ComputeCombatPowerWeightedHealth(sideAPawns)
                : (sideAPawns.Count > 0 ? sumHpA / sideAPawns.Count : 0f);
            state.SideBTotalHealthPercent = ShouldUseArmyAggregateBar(GiantArenaSide.SideB)
                ? ComputeCombatPowerWeightedHealth(sideBPawns)
                : (sideBPawns.Count > 0 ? sumHpB / sideBPawns.Count : 0f);
            state.MinCrossSideDistance = ComputeMinCrossSideDistance();
            state.RecommendedCamera = ComputeRecommendedCamera();
            return state;
        }

        public static bool ShouldUseArmyAggregateBar(GiantArenaSide side)
        {
            if (!IsArenaActive || activeConfig == null)
            {
                return false;
            }
            if (side == GiantArenaSide.SideA)
            {
                if (activeConfig.SideARaidPoints > 0f || sideAPawns.Count > 4) return true;
                return ContainsNonGiantPawn(sideAPawns);
            }
            if (side == GiantArenaSide.SideB)
            {
                if (activeConfig.SideBRaidPoints > 0f || sideBPawns.Count > 4) return true;
                return ContainsNonGiantPawn(sideBPawns);
            }
            return false;
        }

        private static bool ContainsNonGiantPawn(List<Pawn> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p != null && GiantFastLookup.TryGetRig(p) == null)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool TryGetArmyBarSummary(
            GiantArenaSide side,
            out string title,
            out float healthPct,
            out int aliveCount,
            out int totalCount,
            out Pawn primarySinglePawn)
        {
            title = string.Empty;
            healthPct = 0f;
            aliveCount = 0;
            totalCount = 0;
            primarySinglePawn = null;
            if (!IsArenaActive)
            {
                return false;
            }

            List<Pawn> list = side == GiantArenaSide.SideA ? sideAPawns : sideBPawns;
            totalCount = list.Count;
            if (totalCount == 0)
            {
                return false;
            }

            aliveCount = CountCombatCapable(list);
            healthPct = ComputeCombatPowerWeightedHealth(list);

            float raidPts = side == GiantArenaSide.SideA
                ? (activeConfig?.SideARaidPoints ?? 0f)
                : (activeConfig?.SideBRaidPoints ?? 0f);
            bool isSingleHero = totalCount == 1 && raidPts <= 0f;
            if (isSingleHero)
            {
                primarySinglePawn = list[0];
            }

            bool allSameKind = raidPts <= 0f && list[0]?.kindDef != null;
            if (allSameKind)
            {
                PawnKindDef firstKind = list[0].kindDef;
                for (int i = 1; i < list.Count; i++)
                {
                    if (list[i]?.kindDef != firstKind)
                    {
                        allSameKind = false;
                        break;
                    }
                }
            }

            string customLabel = side == GiantArenaSide.SideA ? activeConfig?.SideALabel : activeConfig?.SideBLabel;
            if (!string.IsNullOrWhiteSpace(customLabel))
            {
                title = GiantArenaFighterSnapshot.SanitizeLabel(customLabel);
            }
            else if (allSameKind && list[0] != null)
            {
                string kindLbl = list[0].kindDef?.LabelCap.ToString();
                string rawTitle = !string.IsNullOrWhiteSpace(kindLbl)
                    ? kindLbl
                    : (list[0].LabelShortCap ?? list[0].def?.LabelCap.ToString() ?? side.ToString());
                title = GiantArenaFighterSnapshot.SanitizeLabel(rawTitle);
            }
            else
            {
                Faction fac = side == GiantArenaSide.SideA ? activeFactionA : activeFactionB;
                bool vanillaOnly = side == GiantArenaSide.SideA
                    ? (activeConfig?.SideAVanillaOnly ?? false)
                    : (activeConfig?.SideBVanillaOnly ?? false);
                string facLabel = GiantArenaFighterSnapshot.SanitizeLabel(
                    fac?.def?.LabelCap.ToString() ?? fac?.Name ?? side.ToString());
                bool isChinese = LanguageDatabase.activeLanguage?.folderName?.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0;
                if (raidPts > 0f)
                {
                    int ptsInt = Mathf.RoundToInt(raidPts);
                    title = isChinese
                        ? (vanillaOnly ? $"原版{facLabel} ({ptsInt}点)" : $"{facLabel} ({ptsInt}点)")
                        : (vanillaOnly ? $"Vanilla {facLabel} ({ptsInt} pts)" : $"{facLabel} ({ptsInt} pts)");
                }
                else
                {
                    title = facLabel;
                }
            }
            return true;
        }

        public static float ComputeCombatPowerWeightedHealth(List<Pawn> pawns)
        {
            if (pawns == null || pawns.Count == 0)
            {
                return 0f;
            }
            float sumCur = 0f;
            float sumMax = 0f;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null)
                {
                    continue;
                }
                float w = Mathf.Max(20f, p.kindDef?.combatPower ?? 50f);
                sumMax += w;
                if (p.Spawned && !p.Destroyed && !p.Dead && !p.Downed)
                {
                    CompGiantRig rig = GiantFastLookup.TryGetRig(p);
                    float hp = rig != null
                        ? GiantBossHealthBarHUD.ComputeSynchronizedGiantHealthPercent(p, rig)
                        : Mathf.Clamp01(p.health?.summaryHealth?.SummaryHealthPercent ?? 1f);
                    sumCur += w * hp;
                }
            }
            return sumMax > 0.001f ? Mathf.Clamp01(sumCur / sumMax) : 0f;
        }

        internal static void TryRunPendingAutoLaunch()
        {
            if (cliAutoLaunchAttempted || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
            {
                return;
            }
            cliAutoLaunchAttempted = true;
            string[] cmdArgs = Environment.GetCommandLineArgs();
            string envSpec = Environment.GetEnvironmentVariable("GCF_ARENA_CONFIG");
            if (GiantArenaMatchConfig.TryExtractFromCommandLine(cmdArgs, envSpec, out GiantArenaMatchConfig autoCfg, out string src, out string parseErr))
            {
                Log.Message($"[GCF_ARENA_AUTOLAUNCH] Detected {src} arena spec: {autoCfg.SideAKind} vs {autoCfg.SideBKind} ({autoCfg.Preset})");
                SetupMatch(autoCfg, out string setupErr);
                if (!string.IsNullOrEmpty(setupErr))
                {
                    Log.Error($"[GCF_ARENA_AUTOLAUNCH] Failed: {setupErr}");
                }
            }
            else if (!string.IsNullOrEmpty(parseErr))
            {
                Log.Error($"[GCF_ARENA_AUTOLAUNCH] Invalid arena spec ({src}): {parseErr}");
            }
        }

        internal static void TickActiveArena()
        {
            TryRunPendingAutoLaunch();

            if (!IsArenaActive)
            {
                return;
            }

            if (activeMap == null || Find.Maps == null || !Find.Maps.Contains(activeMap))
            {
                ResetStaticState();
                return;
            }

            int nowTick = Find.TickManager?.TicksGame ?? 0;

            if (matchStatus == GiantArenaMatchStatus.Ready && nowTick > matchStartTick)
            {
                matchStatus = GiantArenaMatchStatus.Fighting;
            }

            // Evaluate victory / draw conditions
            if (matchStatus == GiantArenaMatchStatus.Fighting || matchStatus == GiantArenaMatchStatus.Ready)
            {
                int aliveA = CountCombatCapable(sideAPawns);
                int aliveB = CountCombatCapable(sideBPawns);
                if (aliveA == 0 && aliveB == 0)
                {
                    matchStatus = GiantArenaMatchStatus.Draw;
                    winnerSide = GiantArenaSide.None;
                    winnerTick = nowTick;
                    EnforcePostMatchPeace(isTransition: true);
                    Log.Message($"[GCF_ARENA_RESULT] matchId={activeMatchId} status=Draw tick={nowTick} elapsed={nowTick - matchStartTick}");
                }
                else if (aliveA == 0 || aliveB == 0)
                {
                    matchStatus = GiantArenaMatchStatus.Finished;
                    winnerSide = aliveA > 0 ? GiantArenaSide.SideA : GiantArenaSide.SideB;
                    winnerTick = nowTick;
                    EnforcePostMatchPeace(isTransition: true);
                    Log.Message($"[GCF_ARENA_RESULT] matchId={activeMatchId} status=Finished winner={winnerSide} tick={nowTick} elapsed={nowTick - matchStartTick}");
                }
            }
            else if (matchStatus == GiantArenaMatchStatus.Finished || matchStatus == GiantArenaMatchStatus.Draw)
            {
                EnforcePostMatchPeace(isTransition: false);
            }

            // Periodic maintenance: keep combat lock tight, purge stray non-arena spawns, and lock lighting/weather
            if (nowTick - lastMaintenanceTick >= 60)
            {
                lastMaintenanceTick = nowTick;
                VanishNonArenaMapPawns(activeMap);
                if (matchStatus == GiantArenaMatchStatus.Fighting)
                {
                    RefreshCombatLocks(forceJobOverride: false);
                }
                if (lockNoonClearWeather)
                {
                    KeepNoonAndClearWeatherSoft(activeMap);
                }
                if (Find.GameEnder != null && Find.GameEnder.gameEnding)
                {
                    Find.GameEnder.gameEnding = false;
                }
            }
        }

        private static int CountCombatCapable(List<Pawn> list)
        {
            int count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p != null && p.Spawned && !p.Destroyed && !p.Dead && !p.Downed)
                {
                    count++;
                }
            }
            return count;
        }

        private static string ResolvePreferredHumanlikeFactionDefName(PawnKindDef kind)
        {
            if (kind?.defaultFactionDef != null && kind.defaultFactionDef.humanlikeFaction && !kind.defaultFactionDef.isPlayer)
            {
                return kind.defaultFactionDef.defName;
            }
            if (DefDatabase<FactionDef>.GetNamedSilentFail("Pirate") != null)
            {
                return "Pirate";
            }
            return "AncientsHostile";
        }

        private static string InferRaidFactionDefName(string kindOrFactionHint)
        {
            if (!string.IsNullOrWhiteSpace(kindOrFactionHint))
            {
                string trimmed = kindOrFactionHint.Trim();
                if (DefDatabase<FactionDef>.GetNamedSilentFail(trimmed) != null)
                {
                    return trimmed;
                }
                if (trimmed.IndexOf("insect", StringComparison.OrdinalIgnoreCase) >= 0) return "Insect";
                if (trimmed.IndexOf("pirate", StringComparison.OrdinalIgnoreCase) >= 0) return "Pirate";
                if (trimmed.IndexOf("mech", StringComparison.OrdinalIgnoreCase) >= 0) return "Mechanoid";
            }
            return "Mechanoid";
        }

        private static bool IsOfficialVanillaCombatOption(PawnGenOption opt)
        {
            if (opt?.kind?.race == null)
            {
                return false;
            }
            if (opt.kind.race.GetCompProperties<CompProperties_GiantRig>() != null)
            {
                return false;
            }
            ModContentPack mod = opt.kind.modContentPack;
            return mod == null || mod.IsOfficialMod;
        }

        private static int ClassifyRaidUnitRank(Pawn p)
        {
            if (p == null) return 1;
            Verb atkVerb = p.TryGetAttackVerb(null, allowManualCastWeapons: false);
            bool hasRanged = atkVerb != null && !atkVerb.IsMeleeAttack;
            if (!hasRanged)
            {
                return 0; // Frontline melee (Scythers, etc.)
            }
            float power = p.kindDef?.combatPower ?? 100f;
            float range = atkVerb.verbProps?.range ?? 15f;
            if (power >= 280f || range >= 30f)
            {
                return 2; // Heavy fire support / long-range (Centipedes, Pikemen, Warqueen, Diabolus)
            }
            return 1; // Mid-rank ranged skirmishers (Lancers, Militors, Scorchers, Tesserons)
        }

        private static bool SpawnRaidTeam(
            Map map,
            GiantArenaSide side,
            Faction faction,
            float raidPoints,
            bool vanillaOnly,
            int spawnX,
            int centerZ,
            float headingDeg,
            Rot4 rot,
            List<Pawn> destList,
            out string error)
        {
            error = string.Empty;
            if (faction?.def?.pawnGroupMakers == null || faction.def.pawnGroupMakers.Count == 0)
            {
                error = $"Faction '{faction?.def?.defName ?? "null"}' has no PawnGroupMakers for raid generation.";
                return false;
            }

            var parms = new PawnGroupMakerParms
            {
                groupKind = PawnGroupKindDefOf.Combat,
                faction = faction,
                points = raidPoints,
                tile = map.Tile,
                raidStrategy = RaidStrategyDefOf.ImmediateAttack,
                generateFightersOnly = true,
                dontUseSingleUseRocketLaunchers = false
            };

            // Collect eligible Combat PawnGroupMakers and filter options if vanillaOnly is requested
            var mixedCandidates = new List<(PawnGroupMaker gm, List<PawnGenOption> opts)>();
            var fallbackCandidates = new List<(PawnGroupMaker gm, List<PawnGenOption> opts)>();

            for (int i = 0; i < faction.def.pawnGroupMakers.Count; i++)
            {
                PawnGroupMaker gm = faction.def.pawnGroupMakers[i];
                if (gm == null || gm.kindDef != PawnGroupKindDefOf.Combat || gm.options == null || gm.options.Count == 0)
                {
                    continue;
                }
                var filtered = new List<PawnGenOption>(gm.options.Count);
                for (int o = 0; o < gm.options.Count; o++)
                {
                    PawnGenOption opt = gm.options[o];
                    if (opt?.kind == null) continue;
                    if (vanillaOnly && !IsOfficialVanillaCombatOption(opt)) continue;
                    filtered.Add(opt);
                }
                if (filtered.Count == 0)
                {
                    continue;
                }
                fallbackCandidates.Add((gm, filtered));
                // Prefer mixed multi-archetype combat groups (commonality >= 50 and >= 4 unit types) over single-type swarms
                if (filtered.Count >= 4 && gm.commonality >= 50f)
                {
                    mixedCandidates.Add((gm, filtered));
                }
            }

            List<(PawnGroupMaker gm, List<PawnGenOption> opts)> pool = mixedCandidates.Count > 0 ? mixedCandidates : fallbackCandidates;
            if (pool.Count == 0)
            {
                error = $"No usable Combat PawnGroupMakers found for faction '{faction.def.defName}' (vanillaOnly={vanillaOnly}).";
                return false;
            }

            // Pick highest-commonality mixed group deterministically-weighted so standard combined-arms armies spawn reliably
            (PawnGroupMaker gm, List<PawnGenOption> opts) picked = pool[0];
            float bestCommonality = picked.gm.commonality;
            for (int i = 1; i < pool.Count; i++)
            {
                if (pool[i].gm.commonality > bestCommonality)
                {
                    bestCommonality = pool[i].gm.commonality;
                    picked = pool[i];
                }
            }

            List<PawnGenOptionWithXenotype> chosen = new List<PawnGenOptionWithXenotype>(
                PawnGroupMakerUtility.ChoosePawnGenOptionsByPoints(raidPoints, picked.opts, parms));

            if (chosen.Count == 0)
            {
                error = $"PawnGroupMakerUtility generated 0 pawns for faction '{faction.def.defName}' at {raidPoints:F0} points.";
                return false;
            }

            var generated = new List<Pawn>(chosen.Count);
            float totalPowerSpent = 0f;
            var countByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < chosen.Count; i++)
            {
                PawnKindDef k = chosen[i].Option?.kind;
                if (k == null) continue;
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    k,
                    faction,
                    PawnGenerationContext.NonPlayer,
                    map.Tile,
                    forceGenerateNewPawn: true,
                    allowDowned: false,
                    canGeneratePawnRelations: false,
                    mustBeCapableOfViolence: true));
                if (pawn == null) continue;
                generated.Add(pawn);
                totalPowerSpent += k.combatPower;
                string kName = k.defName ?? "Unknown";
                countByKind[kName] = countByKind.TryGetValue(kName, out int prev) ? prev + 1 : 1;
            }

            if (generated.Count == 0)
            {
                error = "Failed to instantiate any raid pawns.";
                return false;
            }

            // Sort into tactical battle ranks: melee front (rank 0), mid ranged (rank 1), heavy support rear (rank 2)
            generated.Sort((a, b) =>
            {
                int rCmp = ClassifyRaidUnitRank(a).CompareTo(ClassifyRaidUnitRank(b));
                if (rCmp != 0) return rCmp;
                return (a.kindDef?.combatPower ?? 0f).CompareTo(b.kindDef?.combatPower ?? 0f);
            });

            int maxRows = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(generated.Count * 1.45f)), 4, 11);
            int rowStep = 3;
            int colStep = 3;
            int dirBackX = side == GiantArenaSide.SideA ? -1 : 1;

            var spawned = new List<Pawn>(generated.Count);
            for (int i = 0; i < generated.Count; i++)
            {
                Pawn pawn = generated[i];
                int col = i / maxRows;
                int row = i % maxRows;
                int rowsInThisCol = Math.Min(maxRows, generated.Count - col * maxRows);
                int startZ = centerZ - ((rowsInThisCol - 1) * rowStep) / 2;

                int cx = Mathf.Clamp(spawnX + dirBackX * (col * colStep), activeInnerRect.minX + 3, activeInnerRect.maxX - 3);
                int cz = Mathf.Clamp(startZ + row * rowStep, activeInnerRect.minZ + 3, activeInnerRect.maxZ - 3);
                IntVec3 desired = new IntVec3(cx, 0, cz).ClampInsideMap(map);
                IntVec3 cell = desired;
                if (!cell.Standable(map) || cell.GetFirstPawn(map) != null)
                {
                    if (!CellFinder.TryFindRandomCellNear(desired, map, 5, c => activeInnerRect.Contains(c) && c.Standable(map) && c.GetFirstPawn(map) == null, out cell))
                    {
                        cell = desired;
                    }
                }

                GenSpawn.Spawn(pawn, cell, map, rot, WipeMode.Vanish);
                pawn.Rotation = rot;
                CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
                if (rig != null)
                {
                    GiantCombatMechanicsUtility.EnsureGiantDeathOnDownedSuppressed(pawn, rig);
                    rig.ForceSetHeadingImmediate(headingDeg);
                    rig.RebuildFootprintNow(notifyMap: true);
                }
                pawn.Drawer?.tweener?.ResetTweenedPosToRoot();
                map.attackTargetsCache?.UpdateTarget(pawn);

                destList.Add(pawn);
                spawned.Add(pawn);
                sideByPawnId[pawn.thingIDNumber] = side;
            }

            // Assign a relentless non-fleeing AssaultColony Lord so all raid units actively engage enemies across the arena
            var lordJob = new LordJob_AssaultColony(
                faction,
                canKidnap: false,
                canTimeoutOrFlee: false,
                sappers: false,
                useAvoidGridSmart: true,
                canSteal: false,
                breachers: false);
            LordMaker.MakeNewLord(faction, lordJob, map, spawned);

            var summaryParts = new List<string>(countByKind.Count);
            foreach (KeyValuePair<string, int> kv in countByKind)
            {
                summaryParts.Add($"{kv.Key}x{kv.Value}");
            }
            summaryParts.Sort(StringComparer.Ordinal);
            Log.Message(
                $"[GCF_ARENA_RAID] Spawned {side} raid ({faction.def.defName}, vanillaOnly={vanillaOnly}, " +
                $"budget={raidPoints:F0}, spent={totalPowerSpent:F0}, count={spawned.Count}): {string.Join(", ", summaryParts)}");
            return true;
        }

        private static void SpawnTeam(
            Map map,
            GiantArenaSide side,
            PawnKindDef kind,
            int count,
            Faction faction,
            int spawnX,
            int centerZ,
            float headingDeg,
            Rot4 rot,
            bool boardCrewForVehicles,
            List<Pawn> destList)
        {
            float bodySize = kind.race?.race?.baseBodySize ?? 4f;
            int verticalStep = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(Mathf.Max(1f, bodySize)) * 3.8f), 7, 14);
            int totalSpan = (count - 1) * verticalStep;
            int startZ = centerZ - totalSpan / 2;
            var nonGiantSpawned = new List<Pawn>(count);

            for (int i = 0; i < count; i++)
            {
                int z = Mathf.Clamp(startZ + i * verticalStep, activeInnerRect.minZ + 6, activeInnerRect.maxZ - 6);
                IntVec3 cell = new IntVec3(spawnX, 0, z).ClampInsideMap(map);

                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    kind,
                    faction,
                    PawnGenerationContext.NonPlayer,
                    map.Tile,
                    forceGenerateNewPawn: true,
                    allowDowned: false,
                    canGeneratePawnRelations: false,
                    mustBeCapableOfViolence: true));

                GenSpawn.Spawn(pawn, cell, map, rot, WipeMode.Vanish);
                pawn.Rotation = rot;
                if (pawn.mindState != null)
                {
                    pawn.mindState.canFleeIndividual = false;
                }

                CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
                if (rig != null)
                {
                    GiantCombatMechanicsUtility.EnsureGiantDeathOnDownedSuppressed(pawn, rig);
                    rig.ForceSetHeadingImmediate(headingDeg);
                }
                else
                {
                    nonGiantSpawned.Add(pawn);
                }

                if (boardCrewForVehicles && GiantFastLookup.TryGetCockpit(pawn) is CompGiantCockpit cockpit)
                {
                    TryBoardArenaCrew(map, pawn, cockpit, faction);
                }

                pawn.Drawer?.tweener?.ResetTweenedPosToRoot();
                rig?.RebuildFootprintNow(notifyMap: true);
                map.attackTargetsCache?.UpdateTarget(pawn);

                destList.Add(pawn);
                sideByPawnId[pawn.thingIDNumber] = side;
            }

            if (nonGiantSpawned.Count > 0 && faction != null)
            {
                var lordJob = new LordJob_AssaultColony(
                    faction,
                    canKidnap: false,
                    canTimeoutOrFlee: false,
                    sappers: false,
                    useAvoidGridSmart: true,
                    canSteal: false,
                    breachers: false);
                LordMaker.MakeNewLord(faction, lordJob, map, nonGiantSpawned);
            }
        }

        private static void TryBoardArenaCrew(Map map, Pawn vehicle, CompGiantCockpit cockpit, Faction faction)
        {
            if (cockpit == null || cockpit.SeatCount <= 0)
            {
                return;
            }
            PawnKindDef crewKind = PawnKindDefOf.SpaceRefugee ?? PawnKindDefOf.Colonist;
            for (int s = 0; s < cockpit.SeatCount; s++)
            {
                Pawn crew = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    crewKind,
                    faction,
                    PawnGenerationContext.NonPlayer,
                    map.Tile,
                    forceGenerateNewPawn: true,
                    mustBeCapableOfViolence: true,
                    allowDowned: false,
                    canGeneratePawnRelations: false));
                if (crew == null)
                {
                    continue;
                }
                if (crew.skills != null)
                {
                    SkillRecord shoot = crew.skills.GetSkill(SkillDefOf.Shooting);
                    if (shoot != null && !shoot.TotallyDisabled) shoot.Level = 14;
                    SkillRecord melee = crew.skills.GetSkill(SkillDefOf.Melee);
                    if (melee != null && !melee.TotallyDisabled) melee.Level = 14;
                }
                if (!cockpit.TryAcceptPawn(crew, preferredSeatIndex: s))
                {
                    crew.Destroy(DestroyMode.Vanish);
                }
            }
        }

        private static void RefreshCombatLocks(bool forceJobOverride)
        {
            RefreshSideTargets(sideAPawns, sideBPawns, forceJobOverride);
            RefreshSideTargets(sideBPawns, sideAPawns, forceJobOverride);
        }

        private static void RefreshSideTargets(List<Pawn> attackers, List<Pawn> enemies, bool forceJobOverride)
        {
            for (int i = 0; i < attackers.Count; i++)
            {
                Pawn attacker = attackers[i];
                if (attacker == null || !attacker.Spawned || attacker.Dead || attacker.Downed)
                {
                    continue;
                }

                Pawn nearestEnemy = FindNearestLivingEnemy(attacker, enemies);
                if (nearestEnemy == null)
                {
                    continue;
                }

                CompGiantRig rig = GiantFastLookup.TryGetRig(attacker);
                if (rig != null)
                {
                    for (int e = 0; e < enemies.Count; e++)
                    {
                        Pawn enemy = enemies[e];
                        if (enemy != null && enemy.Spawned && !enemy.Dead && !enemy.Downed)
                        {
                            rig.ForceProvokeTarget(enemy, durationTicks: 30000);
                        }
                    }
                }

                if (attacker.mindState != null)
                {
                    Thing cur = attacker.mindState.enemyTarget;
                    if (cur == null || cur.Destroyed || !cur.Spawned || (cur is Pawn cp && (cp.Dead || cp.Downed)))
                    {
                        attacker.mindState.enemyTarget = nearestEnemy;
                    }
                }

                if (attacker.jobs != null)
                {
                    if (attacker.Faction != null && attacker.Faction.IsPlayer)
                    {
                        if (attacker.playerSettings != null)
                        {
                            attacker.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
                        }
                        if (attacker.drafter != null && !attacker.drafter.Drafted)
                        {
                            attacker.drafter.Drafted = true;
                        }
                        bool alreadyAttacking = attacker.CurJobDef == JobDefOf.AttackMelee || attacker.CurJobDef == JobDefOf.AttackStatic;
                        if (!alreadyAttacking && nearestEnemy != null && !GiantFlightUtility.ShouldBlockMeleeAgainstAirborneTarget(attacker, nearestEnemy))
                        {
                            Verb atkVerb = attacker.TryGetAttackVerb(nearestEnemy, allowManualCastWeapons: false);
                            Job forcedAtk = JobMaker.MakeJob(
                                (atkVerb != null && !atkVerb.IsMeleeAttack) ? JobDefOf.AttackStatic : JobDefOf.AttackMelee,
                                nearestEnemy);
                            forcedAtk.playerForced = true;
                            forcedAtk.expiryInterval = 60000;
                            forcedAtk.checkOverrideOnExpire = false;
                            attacker.jobs.StartJob(forcedAtk, JobCondition.InterruptForced);
                        }
                    }
                    else
                    {
                        bool isIdleNonGiant = rig == null && (
                            attacker.CurJobDef == null
                            || attacker.CurJobDef == JobDefOf.Wait
                            || attacker.CurJobDef == JobDefOf.Wait_Wander
                            || attacker.CurJobDef == JobDefOf.GotoWander);
                        if (forceJobOverride || isIdleNonGiant)
                        {
                            attacker.jobs.CheckForJobOverride();
                        }
                    }
                }
            }
        }

        private static Pawn FindNearestLivingEnemy(Pawn attacker, List<Pawn> enemies)
        {
            Pawn best = null;
            float bestDistSq = float.MaxValue;
            Vector3 aPos = ResolveWorldDrawPos(attacker);
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn cand = enemies[i];
                if (cand == null || !cand.Spawned || cand.Dead || cand.Downed)
                {
                    continue;
                }
                Vector3 cPos = ResolveWorldDrawPos(cand);
                float dx = cPos.x - aPos.x;
                float dz = cPos.z - aPos.z;
                float dSq = dx * dx + dz * dz;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = cand;
                }
            }
            return best;
        }

        private static bool ResolveTwoMutuallyHostileFactions(
            string preferredADefName,
            string preferredBDefName,
            out Faction factionA,
            out Faction factionB,
            out string error)
        {
            factionA = null;
            factionB = null;
            error = string.Empty;

            Faction playerFac = Faction.OfPlayer;
            var candidates = new List<Faction>(16);

            void TryAddCandidate(Faction f)
            {
                if (f != null && !f.IsPlayer && !candidates.Contains(f))
                {
                    candidates.Add(f);
                }
            }

            if (!string.IsNullOrWhiteSpace(preferredADefName))
            {
                TryAddCandidate(ResolveOrCreateFaction(preferredADefName));
            }
            if (!string.IsNullOrWhiteSpace(preferredBDefName))
            {
                TryAddCandidate(ResolveOrCreateFaction(preferredBDefName));
            }

            // Prefer inherently aggressive, player-hostile factions that work identically for mechs, creatures, and vehicles
            TryAddCandidate(Faction.OfMechanoids);
            TryAddCandidate(Faction.OfEntities);
            TryAddCandidate(Find.FactionManager?.FirstFactionOfDef(FactionDefOf.Pirate));
            TryAddCandidate(Find.FactionManager?.FirstFactionOfDef(FactionDefOf.AncientsHostile));
            TryAddCandidate(Faction.OfInsects);

            List<Faction> allWorldFactions = Find.FactionManager?.AllFactionsListForReading;
            if (allWorldFactions != null)
            {
                for (int i = 0; i < allWorldFactions.Count; i++)
                {
                    Faction f = allWorldFactions[i];
                    if (f != null && !f.IsPlayer && !f.defeated && !f.temporary)
                    {
                        TryAddCandidate(f);
                    }
                }
            }

            // Try to pick explicit preferred factions first if supplied
            Faction prefA = !string.IsNullOrWhiteSpace(preferredADefName) ? ResolveOrCreateFaction(preferredADefName) : null;
            Faction prefB = !string.IsNullOrWhiteSpace(preferredBDefName) ? ResolveOrCreateFaction(preferredBDefName) : null;
            if (prefA != null && prefB != null && prefA != prefB && TryMakeMutuallyHostilePair(prefA, prefB, playerFac))
            {
                factionA = prefA;
                factionB = prefB;
                return true;
            }

            // Scan candidate pairs, preferring pairs where both are also hostile to player (so vehicles run autonomously & both show boss bars)
            for (int pass = 0; pass < 2; pass++)
            {
                bool requirePlayerHostile = pass == 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Faction a = candidates[i];
                    if (prefA != null && a != prefA && i == 0) continue;
                    for (int j = i + 1; j < candidates.Count; j++)
                    {
                        Faction b = candidates[j];
                        if (a == b) continue;
                        if (TryMakeMutuallyHostilePair(a, b, playerFac))
                        {
                            bool bothHostileToPlayer = playerFac == null || (a.HostileTo(playerFac) && b.HostileTo(playerFac));
                            if (!requirePlayerHostile || bothHostileToPlayer)
                            {
                                factionA = a;
                                factionB = b;
                                return true;
                            }
                        }
                    }
                }
            }

            // Last resort: generate fresh Pirate / AncientsHostile factions if the world was stripped
            Faction genA = factionA ?? ResolveOrCreateFaction("Mechanoid") ?? ResolveOrCreateFaction("Pirate");
            Faction genB = ResolveOrCreateFaction("AncientsHostile") ?? ResolveOrCreateFaction("Pirate");
            if (genA != null && genB != null && genA != genB && TryMakeMutuallyHostilePair(genA, genB, playerFac))
            {
                factionA = genA;
                factionB = genB;
                return true;
            }

            error = "Could not resolve two mutually hostile non-player factions on the current world.";
            return false;
        }

        private static Faction ResolveOrCreateFaction(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName) || Find.FactionManager == null)
            {
                return null;
            }
            FactionDef fDef = DefDatabase<FactionDef>.GetNamedSilentFail(defName.Trim());
            if (fDef == null)
            {
                return null;
            }
            Faction existing = Find.FactionManager.FirstFactionOfDef(fDef);
            if (existing != null && !existing.IsPlayer)
            {
                return existing;
            }
            try
            {
                Faction created = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(fDef));
                if (created != null)
                {
                    Find.FactionManager.Add(created);
                    return created;
                }
            }
            catch
            {
            }
            return null;
        }

        private static bool TryMakeMutuallyHostilePair(Faction a, Faction b, Faction playerFac)
        {
            if (a == null || b == null || a == b || a.IsPlayer || b.IsPlayer)
            {
                return false;
            }

            EnsureHostileRelation(a, b);
            EnsureHostileRelation(b, a);
            if (playerFac != null)
            {
                EnsureHostileRelation(a, playerFac);
                EnsureHostileRelation(b, playerFac);
            }

            return a.HostileTo(b) && b.HostileTo(a);
        }

        private static void EnsureHostileRelation(Faction a, Faction b)
        {
            if (a == null || b == null || a == b || a.HostileTo(b))
            {
                return;
            }
            try
            {
                if (a.HasGoodwill && b.HasGoodwill)
                {
                    a.TryAffectGoodwillWith(b, -200, canSendMessage: false, canSendHostilityLetter: false);
                }
                else
                {
                    a.SetRelationDirect(b, FactionRelationKind.Hostile, canSendHostilityLetter: false);
                }
            }
            catch
            {
                // Some permanent-relation factions disallow direct mutation; caller checks HostileTo afterwards.
            }
        }

        private static void VanishAllMapPawns(Map map)
        {
            var oldPawns = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
            for (int i = 0; i < oldPawns.Count; i++)
            {
                Pawn old = oldPawns[i];
                if (old == null || old.Destroyed)
                {
                    continue;
                }
                try { old.GetLord()?.Notify_PawnLost(old, Verse.AI.Group.PawnLostCondition.Vanished); } catch { }
                try { old.jobs?.StopAll(ifLayingKeepLaying: false); } catch { }
                try { old.stances?.SetStance(new Stance_Mobile()); } catch { }
                try { old.Destroy(DestroyMode.Vanish); } catch { }
                try
                {
                    if (!old.Discarded && (Find.WorldPawns == null || !Find.WorldPawns.Contains(old)))
                    {
                        old.Discard(silentlyRemoveReferences: true);
                    }
                }
                catch { }
            }
        }

        private static void VanishNonArenaMapPawns(Map map)
        {
            if (map?.mapPawns == null)
            {
                return;
            }
            var spawned = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
            for (int i = 0; i < spawned.Count; i++)
            {
                Pawn old = spawned[i];
                if (old == null || old.Destroyed || sideByPawnId.ContainsKey(old.thingIDNumber))
                {
                    continue;
                }
                try { old.GetLord()?.Notify_PawnLost(old, Verse.AI.Group.PawnLostCondition.Vanished); } catch { }
                try { old.jobs?.StopAll(ifLayingKeepLaying: false); } catch { }
                try { old.stances?.SetStance(new Stance_Mobile()); } catch { }
                try { old.Destroy(DestroyMode.Vanish); } catch { }
                try
                {
                    if (!old.Discarded && (Find.WorldPawns == null || !Find.WorldPawns.Contains(old)))
                    {
                        old.Discard(silentlyRemoveReferences: true);
                    }
                }
                catch { }
            }
        }

        private static void ApplyNoonAndClearWeather(Map map)
        {
            if (Find.TickManager != null)
            {
                float curHour = GenLocalDate.HourFloat(map);
                int deltaToNoon = Mathf.RoundToInt((12f - curHour) * GenDate.TicksPerHour);
                int targetTick = Find.TickManager.TicksGame + deltaToNoon;
                while (targetTick < GenDate.TicksPerDay)
                {
                    targetTick += GenDate.TicksPerDay;
                }
                Find.TickManager.DebugSetTicksGame(targetTick);
            }
            KeepNoonAndClearWeatherSoft(map);
        }

        private static void KeepNoonAndClearWeatherSoft(Map map)
        {
            if (map == null)
            {
                return;
            }
            if (map.weatherManager != null && WeatherDefOf.Clear != null)
            {
                map.weatherManager.curWeather = WeatherDefOf.Clear;
                map.weatherManager.lastWeather = WeatherDefOf.Clear;
                map.weatherManager.curWeatherAge = 4000;
            }
            map.skyManager?.ForceSetCurSkyGlow(1f);
        }

        private static void ClearLettersAndMessages()
        {
            try
            {
                if (Find.LetterStack?.LettersListForReading != null)
                {
                    var copy = new List<Letter>(Find.LetterStack.LettersListForReading);
                    for (int i = 0; i < copy.Count; i++)
                    {
                        Find.LetterStack.RemoveLetter(copy[i]);
                    }
                }
            }
            catch { }

            if (activeMap != null && (cleanVideoMode || GiantArenaVideoRecorder.IsRecording))
            {
                DestroyExistingSpeechBubbles(activeMap);
            }
        }

        internal static void PurgeArenaSpeechBubblesNow()
        {
            if (activeMap != null)
            {
                DestroyExistingSpeechBubbles(activeMap);
            }
        }

        private static void DestroyExistingSpeechBubbles(Map map)
        {
            if (map?.listerThings == null)
            {
                return;
            }
            try
            {
                List<Thing> allThings = map.listerThings.AllThings;
                if (allThings == null || allThings.Count == 0)
                {
                    return;
                }
                for (int i = allThings.Count - 1; i >= 0; i--)
                {
                    if (allThings[i] is MoteBubble bubble && !bubble.Destroyed)
                    {
                        bubble.Destroy(DestroyMode.Vanish);
                    }
                }
            }
            catch
            {
            }
        }

        private static void EnforcePostMatchPeace(bool isTransition)
        {
            CalmSideFightersPostMatch(sideAPawns, isTransition);
            CalmSideFightersPostMatch(sideBPawns, isTransition);
        }

        private static void CalmSideFightersPostMatch(List<Pawn> fighters, bool isTransition)
        {
            if (fighters == null)
            {
                return;
            }
            for (int i = 0; i < fighters.Count; i++)
            {
                CalmFighterPostMatch(fighters[i], isTransition);
            }
        }

        private static void CalmFighterPostMatch(Pawn pawn, bool isTransition)
        {
            if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Dead)
            {
                return;
            }

            if (pawn.mindState != null)
            {
                pawn.mindState.enemyTarget = null;
                pawn.mindState.meleeThreat = null;
                pawn.mindState.lastMeleeThreatHarmTick = -99999;
                pawn.mindState.anyCloseHostilesRecently = false;
            }

            if (pawn.Downed)
            {
                return;
            }

            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            if (rig?.PartCombat != null)
            {
                rig.PartCombat.ClearForcedTarget();
                rig.PartCombat.FireAtWill = false;
                if (isTransition)
                {
                    IReadOnlyList<BonePartWeaponGroup> groups = rig.PartCombat.Groups;
                    for (int g = 0; g < groups.Count; g++)
                    {
                        groups[g].AbortActiveCycle();
                        groups[g].ClearTrackedTarget();
                    }
                }
            }

            TryStopActiveWeaponCombo(pawn, forceAlways: isTransition);
            ResetActiveBurstingVerbs(pawn);
            pawn.stances?.CancelBusyStanceSoft();

            if (pawn.pather != null && pawn.pather.Moving)
            {
                pawn.pather.StopDead();
            }

            if (pawn.jobs != null)
            {
                Job curJob = pawn.CurJob;
                bool needsCalmWait = isTransition
                    ? (curJob == null || curJob.def != JobDefOf.Wait)
                    : (curJob == null || IsHostileOrNonCalmPostMatchJob(curJob));
                if (needsCalmWait)
                {
                    pawn.jobs.ClearQueuedJobs();
                    if (curJob != null)
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: true);
                    }
                    if (pawn.CurJob == null || pawn.CurJob.def != JobDefOf.Wait)
                    {
                        Job calmWait = JobMaker.MakeJob(JobDefOf.Wait, 600);
                        calmWait.expiryInterval = 600;
                        calmWait.checkOverrideOnExpire = false;
                        pawn.jobs.StartJob(
                            calmWait,
                            JobCondition.InterruptForced,
                            cancelBusyStances: true,
                            addToJobsThisTick: false);
                    }
                }
            }
        }

        private static void ResetActiveBurstingVerbs(Pawn pawn)
        {
            List<Verb> nativeVerbs = pawn.verbTracker?.AllVerbs;
            if (nativeVerbs != null)
            {
                for (int i = 0; i < nativeVerbs.Count; i++)
                {
                    Verb v = nativeVerbs[i];
                    if (v != null && v.Bursting)
                    {
                        v.Reset();
                    }
                }
            }

            List<ThingWithComps> eqList = pawn.equipment?.AllEquipmentListForReading;
            if (eqList != null)
            {
                for (int i = 0; i < eqList.Count; i++)
                {
                    List<Verb> eqVerbs = eqList[i]?.GetComp<CompEquippable>()?.AllVerbs;
                    if (eqVerbs == null)
                    {
                        continue;
                    }
                    for (int j = 0; j < eqVerbs.Count; j++)
                    {
                        Verb v = eqVerbs[j];
                        if (v != null && v.Bursting)
                        {
                            v.Reset();
                        }
                    }
                }
            }
        }

        private static void TryStopActiveWeaponCombo(Pawn pawn, bool forceAlways)
        {
            List<ThingComp> comps = pawn?.equipment?.Primary?.AllComps;
            if (comps == null || comps.Count == 0)
            {
                return;
            }

            if (!mhWeaponResetResolved)
            {
                mhWeaponResetResolved = true;
                mhWeaponCompType = GenTypes.GetTypeInAnyAssembly("MHWildsWeapons.CompMHWeapon")
                    ?? AccessTools.TypeByName("MHWildsWeapons.CompMHWeapon");
                if (mhWeaponCompType != null)
                {
                    mhWeaponForceEscapeMethod = AccessTools.Method(
                        mhWeaponCompType,
                        "ForceEscapeAndResetStance",
                        new[] { typeof(bool) });
                    mhWeaponActiveMoveField = AccessTools.Field(mhWeaponCompType, "ActiveMoveDefName");
                }
            }

            if (mhWeaponCompType == null || mhWeaponForceEscapeMethod == null)
            {
                return;
            }

            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp comp = comps[i];
                if (comp == null || !mhWeaponCompType.IsInstanceOfType(comp))
                {
                    continue;
                }
                try
                {
                    bool hasActiveMove = forceAlways
                        || (mhWeaponActiveMoveField != null && !string.IsNullOrEmpty(mhWeaponActiveMoveField.GetValue(comp) as string));
                    if (hasActiveMove)
                    {
                        mhWeaponForceEscapeMethod.Invoke(comp, new object[] { true });
                    }
                }
                catch
                {
                }
            }
        }

        private static void CollectCameraTargets(List<Pawn> list, List<Pawn> living, List<Pawn> fallbackAll)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p == null || p.Destroyed)
                {
                    continue;
                }
                if (p.Spawned || (p.Corpse != null && p.Corpse.Spawned))
                {
                    fallbackAll.Add(p);
                    if (!p.Dead && !p.Downed && p.Spawned)
                    {
                        living.Add(p);
                    }
                }
            }
        }

        private static Vector3 ResolveWorldDrawPos(Pawn p)
        {
            if (p == null) return Vector3.zero;
            if (p.Spawned)
            {
                Vector3 pos = CompGiantRig.GetSimTickPos(p);
                CompGiantRig rig = GiantFastLookup.TryGetRig(p);
                if (rig != null && !p.Dead && !p.Downed && rig.FlightLocomotion != null && rig.FlightLocomotion.CurrentAltitude01 > 0.005f)
                {
                    Vector2 airOff = rig.FlightLocomotion.ComputeVisualOffsetXZ();
                    pos.x += airOff.x;
                    pos.z += airOff.y;
                }
                return pos;
            }
            if (p.Corpse != null && p.Corpse.Spawned) return p.Corpse.DrawPos;
            return p.PositionHeld.ToVector3Shifted();
        }

        private static float ComputeMinCrossSideDistance()
        {
            float best = float.MaxValue;
            for (int i = 0; i < sideAPawns.Count; i++)
            {
                Pawn a = sideAPawns[i];
                if (a == null || a.Dead || !a.Spawned) continue;
                Vector3 pa = a.DrawPos;
                for (int j = 0; j < sideBPawns.Count; j++)
                {
                    Pawn b = sideBPawns[j];
                    if (b == null || b.Dead || !b.Spawned) continue;
                    Vector3 pb = b.DrawPos;
                    float dx = pa.x - pb.x;
                    float dz = pa.z - pb.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist < best) best = dist;
                }
            }
            return best < float.MaxValue * 0.5f ? best : -1f;
        }

        private static GiantArenaFighterSnapshot BuildFighterSnapshot(Pawn pawn, GiantArenaSide side)
        {
            var snap = new GiantArenaFighterSnapshot
            {
                Side = side == GiantArenaSide.SideA ? "A" : "B"
            };
            if (pawn == null)
            {
                return snap;
            }

            CompGiantRig rig = GiantFastLookup.TryGetRig(pawn);
            Vector3 dp = ResolveWorldDrawPos(pawn);
            float hpPct = pawn.Dead
                ? 0f
                : (rig != null
                    ? GiantBossHealthBarHUD.ComputeSynchronizedGiantHealthPercent(pawn, rig)
                    : (pawn.health?.summaryHealth?.SummaryHealthPercent ?? 0f));

            snap.PawnThingId = pawn.thingIDNumber;
            snap.KindDefName = pawn.kindDef?.defName ?? pawn.def?.defName ?? string.Empty;
            string rawLabel = rig != null
                ? GiantBossHealthBarHUD.ResolveLocalizedBossTitle(pawn, rig.RigDef)
                : (pawn.LabelCap ?? pawn.def?.label ?? string.Empty);
            snap.Label = GiantArenaFighterSnapshot.SanitizeLabel(rawLabel);
            snap.FactionDefName = pawn.Faction?.def?.defName ?? string.Empty;
            snap.FactionName = GiantArenaFighterSnapshot.SanitizeLabel(pawn.Faction?.Name);
            snap.Spawned = pawn.Spawned;
            snap.Dead = pawn.Dead;
            snap.Downed = pawn.Downed;
            snap.HealthPercent = Mathf.Clamp01(hpPct);
            snap.WorldX = dp.x;
            snap.WorldZ = dp.z;
            snap.HeadingDeg = rig != null ? rig.CurrentHeadingDeg : pawn.Rotation.AsAngle;
            snap.BodySize = pawn.BodySize;
            snap.AnimState = rig != null ? rig.CurrentAnimState.ToString() : string.Empty;
            snap.ActiveClip = rig?.ActiveClip?.defName ?? string.Empty;
            snap.CurJobDef = pawn.CurJobDef?.defName ?? string.Empty;
            snap.IsFlying = rig != null && rig.IsAirborneOrTakingOff;

            if (pawn.health?.hediffSet?.GetMissingPartsCommonAncestors() is List<Hediff_MissingPart> missing)
            {
                snap.MissingPartCount = missing.Count;
                for (int i = 0; i < missing.Count; i++)
                {
                    BodyPartRecord part = missing[i]?.Part;
                    if (part != null)
                    {
                        snap.MissingParts.Add(GiantArenaFighterSnapshot.SanitizeLabel(part.LabelCap));
                    }
                }
            }

            return snap;
        }

        private static void DebugSetupQuickMatch()
        {
            SetupMatch(new GiantArenaMatchConfig
            {
                Preset = nameof(GiantArenaPresetId.Colosseum),
                SideAKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("GCF_Rathalos") != null ? "GCF_Rathalos" : "GCF_MechScorpion",
                SideBKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("GCF_Valstrax") != null ? "GCF_Valstrax" : "GCF_MechScorpion",
                StartPaused = false,
                CleanVideoMode = true,
                CinematicHudOnly = false,
                HideDevToolbar = false
            }, out string err);
            if (!string.IsNullOrEmpty(err))
            {
                Log.Error("[GiantArena] " + err);
            }
        }

        private static void DebugResetRematch()
        {
            ResetRematch(swapSides: false, out string err);
            if (!string.IsNullOrEmpty(err))
            {
                Log.Error("[GiantArena] " + err);
            }
        }

        private static void DebugDumpMatchState()
        {
            string json = JsonSerializer.Serialize(GetMatchState());
            Log.Message("[GCF_ARENA_STATE] " + json);
        }
    }

    public sealed class GameComponent_GiantArena : GameComponent
    {
        public GameComponent_GiantArena(Game game)
        {
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            GiantArenaService.EnsureCleanNonArenaStateOnSessionChange();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            GiantArenaService.EnsureCleanNonArenaStateOnSessionChange();
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            GiantArenaService.TryRunPendingAutoLaunch();
            if (!GiantArenaService.IsArenaActive)
            {
                if (HarmonyPatches_GiantArena.IsPatched)
                {
                    GiantArenaService.EnsureCleanNonArenaStateOnSessionChange();
                }
                return;
            }
            if (GiantArenaService.IsCinematicHudOnly)
            {
                GiantArenaService.CloseAllOpenWindows();
                if (!HarmonyPatches_GiantArena.IsScreenshotModeActive())
                {
                    HarmonyPatches_GiantArena.SetScreenshotModeActive(true);
                }
            }
            GiantArenaVideoRecorder.OnUpdateFrame();
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            GiantArenaService.TickActiveArena();
        }
    }
}
