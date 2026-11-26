using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    public enum GiantArenaPresetId
    {
        Colosseum = 0,
        Plains = 1,
        DesertRuins = 2,
        Tundra = 3,
        VolcanicBasalt = 4
    }

    public enum GiantArenaMatchStatus
    {
        Idle = 0,
        Ready = 1,
        Fighting = 2,
        Finished = 3,
        Draw = 4
    }

    public enum GiantArenaSide
    {
        None = 0,
        SideA = 1,
        SideB = 2
    }

    public sealed class GiantArenaPresetInfo
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool HasInnerObstacles { get; set; }
        public string PrimaryTerrain { get; set; } = string.Empty;
    }

    public sealed class GiantArenaUnitCatalogEntry
    {
        public string DefName { get; set; } = string.Empty;
        public string RaceDefName { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public bool IsGiantRig { get; set; }
        public string RigDefName { get; set; } = string.Empty;
        public float BodySize { get; set; }
        public float CombatPower { get; set; }
        public bool HasCockpit { get; set; }
        public bool CanFly { get; set; }
        public int AbilityCount { get; set; }
    }

    public sealed class GiantArenaMatchConfig
    {
        public string Preset { get; set; } = nameof(GiantArenaPresetId.Colosseum);
        public int ArenaHalfWidth { get; set; } = 48;
        public int ArenaHalfHeight { get; set; } = 40;
        public int SpawnSeparation { get; set; } = 32;

        public string SideAKind { get; set; } = "GCF_MechScorpion";
        public int SideACount { get; set; } = 1;
        public float SideARaidPoints { get; set; } = 0f;
        public bool SideAVanillaOnly { get; set; } = false;
        public string SideALabel { get; set; } = string.Empty;
        public string SideAFactionDef { get; set; } = string.Empty;

        public string SideBKind { get; set; } = "GCF_MechScorpion";
        public int SideBCount { get; set; } = 1;
        public float SideBRaidPoints { get; set; } = 0f;
        public bool SideBVanillaOnly { get; set; } = false;
        public string SideBLabel { get; set; } = string.Empty;
        public string SideBFactionDef { get; set; } = string.Empty;

        public bool StartPaused { get; set; } = true;
        public bool LockNoonClearWeather { get; set; } = true;
        public bool CleanVideoMode { get; set; } = true;
        public bool CinematicHudOnly { get; set; } = true;
        public bool EnableVersusHud { get; set; } = true;
        public bool HideDevToolbar { get; set; } = true;
        public bool BoardCrewForVehicles { get; set; } = false;
        public float CameraRootSize { get; set; } = 0f;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public GiantArenaMatchConfig Clone()
        {
            return new GiantArenaMatchConfig
            {
                Preset = Preset,
                ArenaHalfWidth = ArenaHalfWidth,
                ArenaHalfHeight = ArenaHalfHeight,
                SpawnSeparation = SpawnSeparation,
                SideAKind = SideAKind,
                SideACount = SideACount,
                SideARaidPoints = SideARaidPoints,
                SideAVanillaOnly = SideAVanillaOnly,
                SideALabel = SideALabel,
                SideAFactionDef = SideAFactionDef,
                SideBKind = SideBKind,
                SideBCount = SideBCount,
                SideBRaidPoints = SideBRaidPoints,
                SideBVanillaOnly = SideBVanillaOnly,
                SideBLabel = SideBLabel,
                SideBFactionDef = SideBFactionDef,
                StartPaused = StartPaused,
                LockNoonClearWeather = LockNoonClearWeather,
                CleanVideoMode = CleanVideoMode,
                CinematicHudOnly = CinematicHudOnly,
                EnableVersusHud = EnableVersusHud,
                HideDevToolbar = HideDevToolbar,
                BoardCrewForVehicles = BoardCrewForVehicles,
                CameraRootSize = CameraRootSize
            };
        }

        public void NormalizeInPlace()
        {
            Preset = ResolvePresetId(Preset).ToString();
            ArenaHalfWidth = Mathf.Clamp(ArenaHalfWidth, 24, 96);
            ArenaHalfHeight = Mathf.Clamp(ArenaHalfHeight, 20, 84);
            int maxSep = Math.Max(14, (ArenaHalfWidth - 10) * 2);
            SpawnSeparation = Mathf.Clamp(SpawnSeparation, 12, maxSep);
            SideACount = Mathf.Clamp(SideACount, 1, 8);
            SideBCount = Mathf.Clamp(SideBCount, 1, 8);
            SideARaidPoints = Mathf.Clamp(SideARaidPoints, 0f, 50000f);
            SideBRaidPoints = Mathf.Clamp(SideBRaidPoints, 0f, 50000f);
            if (string.IsNullOrWhiteSpace(SideAKind) && SideARaidPoints <= 0f)
            {
                SideAKind = "GCF_MechScorpion";
            }
            if (string.IsNullOrWhiteSpace(SideBKind) && SideBRaidPoints <= 0f)
            {
                SideBKind = "GCF_MechScorpion";
            }
            SideAKind = SideAKind?.Trim() ?? string.Empty;
            SideBKind = SideBKind?.Trim() ?? string.Empty;
            SideALabel = SideALabel?.Trim() ?? string.Empty;
            SideBLabel = SideBLabel?.Trim() ?? string.Empty;
            SideAFactionDef = SideAFactionDef?.Trim() ?? string.Empty;
            SideBFactionDef = SideBFactionDef?.Trim() ?? string.Empty;
            if (CameraRootSize < 0f)
            {
                CameraRootSize = 0f;
            }
        }

        public static GiantArenaPresetId ResolvePresetId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return GiantArenaPresetId.Colosseum;
            }
            string key = raw.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
            if (Enum.TryParse<GiantArenaPresetId>(key, ignoreCase: true, out GiantArenaPresetId parsed))
            {
                return parsed;
            }
            switch (key.ToLowerInvariant())
            {
                case "stone":
                case "arena":
                case "stadium":
                    return GiantArenaPresetId.Colosseum;
                case "grass":
                case "meadow":
                case "flat":
                case "soil":
                    return GiantArenaPresetId.Plains;
                case "desert":
                case "ruins":
                case "sand":
                    return GiantArenaPresetId.DesertRuins;
                case "snow":
                case "ice":
                case "frost":
                    return GiantArenaPresetId.Tundra;
                case "volcanic":
                case "basalt":
                case "dark":
                case "caldera":
                    return GiantArenaPresetId.VolcanicBasalt;
                default:
                    return GiantArenaPresetId.Colosseum;
            }
        }

        /// <summary>
        /// Parses either a JSON config file path or a compact CLI spec:
        ///   KindA:KindB[:Preset[:CountAxCountB[:Separation[:StartPaused]]]]
        /// Example:
        ///   GCF_MechScorpion:GCF_MechScorpion:Colosseum:1x1:34:false
        /// </summary>
        public static bool TryParseSpecOrFile(string rawSpec, out GiantArenaMatchConfig config, out string error)
        {
            config = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(rawSpec))
            {
                error = "Empty arena spec.";
                return false;
            }

            string trimmed = rawSpec.Trim().Trim('"');
            if (File.Exists(trimmed))
            {
                try
                {
                    string json = File.ReadAllText(trimmed);
                    config = JsonSerializer.Deserialize<GiantArenaMatchConfig>(json, JsonOptions);
                    if (config == null)
                    {
                        error = $"Failed to deserialize arena config JSON from '{trimmed}'.";
                        return false;
                    }
                    config.NormalizeInPlace();
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"Failed reading arena config JSON '{trimmed}': {ex.Message}";
                    return false;
                }
            }

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    config = JsonSerializer.Deserialize<GiantArenaMatchConfig>(trimmed, JsonOptions);
                    if (config == null)
                    {
                        error = "Inline JSON arena config resolved to null.";
                        return false;
                    }
                    config.NormalizeInPlace();
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"Failed parsing inline arena config JSON: {ex.Message}";
                    return false;
                }
            }

            string[] parts = trimmed.Split(':');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                error = $"Invalid arena spec '{trimmed}'. Expected JSON path or 'KindA:KindB[:Preset[:CountAxCountB[:Separation[:StartPaused]]]]'.";
                return false;
            }

            var parsed = new GiantArenaMatchConfig
            {
                SideAKind = parts[0].Trim(),
                SideBKind = parts[1].Trim()
            };

            if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
            {
                parsed.Preset = parts[2].Trim();
            }

            if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]))
            {
                string countSpec = parts[3].Trim().ToLowerInvariant();
                string[] ab = countSpec.Split('x');
                if (ab.Length == 2
                    && int.TryParse(ab[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ca)
                    && int.TryParse(ab[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cb))
                {
                    parsed.SideACount = ca;
                    parsed.SideBCount = cb;
                }
                else if (int.TryParse(countSpec, NumberStyles.Integer, CultureInfo.InvariantCulture, out int both))
                {
                    parsed.SideACount = both;
                    parsed.SideBCount = both;
                }
            }

            if (parts.Length >= 5 && int.TryParse(parts[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sep))
            {
                parsed.SpawnSeparation = sep;
            }

            if (parts.Length >= 6 && bool.TryParse(parts[5].Trim(), out bool startPaused))
            {
                parsed.StartPaused = startPaused;
            }

            parsed.NormalizeInPlace();
            config = parsed;
            return true;
        }

        public static bool TryExtractFromCommandLine(string[] args, string envSpec, out GiantArenaMatchConfig config, out string source, out string error)
        {
            config = null;
            source = string.Empty;
            error = string.Empty;

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (string.IsNullOrWhiteSpace(a))
                    {
                        continue;
                    }
                    if (a.StartsWith("-gcfarena=", StringComparison.OrdinalIgnoreCase)
                        || a.StartsWith("--gcfarena=", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = a.IndexOf('=');
                        string val = eq >= 0 ? a.Substring(eq + 1) : string.Empty;
                        source = "cli";
                        return TryParseSpecOrFile(val, out config, out error);
                    }
                    if ((string.Equals(a, "-gcfarena", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a, "--gcfarena", StringComparison.OrdinalIgnoreCase))
                        && i + 1 < args.Length)
                    {
                        source = "cli";
                        return TryParseSpecOrFile(args[i + 1], out config, out error);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(envSpec))
            {
                source = "env";
                return TryParseSpecOrFile(envSpec, out config, out error);
            }

            return false;
        }
    }

    public sealed class GiantArenaFighterSnapshot
    {
        public string Side { get; set; } = string.Empty;
        public int PawnThingId { get; set; }
        public string KindDefName { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string FactionDefName { get; set; } = string.Empty;
        public string FactionName { get; set; } = string.Empty;
        public bool Spawned { get; set; }
        public bool Dead { get; set; }
        public bool Downed { get; set; }
        public float HealthPercent { get; set; }
        public float WorldX { get; set; }
        public float WorldZ { get; set; }
        public float HeadingDeg { get; set; }
        public float BodySize { get; set; }
        public string AnimState { get; set; } = string.Empty;
        public string ActiveClip { get; set; } = string.Empty;
        public string CurJobDef { get; set; } = string.Empty;
        public bool IsFlying { get; set; }
        public int MissingPartCount { get; set; }
        public List<string> MissingParts { get; set; } = new List<string>();

        public static string SanitizeLabel(string rawLabel)
        {
            if (string.IsNullOrWhiteSpace(rawLabel))
            {
                return string.Empty;
            }
            return rawLabel.StripTags().Trim();
        }
    }

    public sealed class GiantArenaCameraRecommendation
    {
        public float CenterX { get; set; }
        public float CenterZ { get; set; }
        public float RootSize { get; set; }
        public float SpanX { get; set; }
        public float SpanZ { get; set; }
    }

    public sealed class GiantArenaMatchState
    {
        public bool Active { get; set; }
        public int MatchId { get; set; }
        public string Preset { get; set; } = string.Empty;
        public string Status { get; set; } = nameof(GiantArenaMatchStatus.Idle);
        public string WinnerSide { get; set; } = nameof(GiantArenaSide.None);
        public int StartTick { get; set; }
        public int CurrentTick { get; set; }
        public int ElapsedTicks { get; set; }
        public float ElapsedSeconds { get; set; }
        public int WinnerTick { get; set; } = -1;
        public bool Paused { get; set; }
        public int ArenaCenterX { get; set; }
        public int ArenaCenterZ { get; set; }
        public int ArenaMinX { get; set; }
        public int ArenaMaxX { get; set; }
        public int ArenaMinZ { get; set; }
        public int ArenaMaxZ { get; set; }
        public string SideAFaction { get; set; } = string.Empty;
        public string SideBFaction { get; set; } = string.Empty;
        public bool FactionsMutuallyHostile { get; set; }
        public int SideAAlive { get; set; }
        public int SideBAlive { get; set; }
        public float SideATotalHealthPercent { get; set; }
        public float SideBTotalHealthPercent { get; set; }
        public float MinCrossSideDistance { get; set; } = -1f;
        public List<GiantArenaFighterSnapshot> Fighters { get; set; } = new List<GiantArenaFighterSnapshot>();
        public GiantArenaCameraRecommendation RecommendedCamera { get; set; } = new GiantArenaCameraRecommendation();
    }
}
