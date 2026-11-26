using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace GiantCreatureFramework.Arena.BridgeTools;

/// <summary>
/// RimBridgeServer companion tools exposing deterministic giant-vs-giant arena setup,
/// dynamic camera framing, match telemetry, and recording presentation controls to GABS/AI agents.
/// </summary>
public sealed class GiantArenaBridgeTools
{
    [Tool(
        "gcf_arena/list_giants",
        Description = "List available giant creature PawnKindDefs (and optionally non-giant units) that can be spawned in the Giant Arena.",
        ResultDescription = "Array of available unit entries with defName, label, category, bodySize, combatPower, and capability flags.")]
    public Task<object> ListGiants(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Include non-giant RimWorld PawnKindDefs as well (default false).")] bool includeNonGiants = false,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var units = GiantArenaService.ListAvailableUnits(includeNonGiants);
            return new
            {
                success = true,
                count = units.Count,
                units
            };
        }, cancellationToken);

    [Tool(
        "gcf_arena/list_presets",
        Description = "List built-in preset arena maps available for giant creature versus matches.",
        ResultDescription = "Array of preset arena descriptors (Colosseum, Plains, DesertRuins, Tundra, VolcanicBasalt).")]
    public Task<object> ListPresets(
        IRimBridgeContext ctx,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var presets = GiantArenaMapPresets.ListPresets();
            return new
            {
                success = true,
                count = presets.Count,
                presets
            };
        }, cancellationToken);

    [Tool(
        "gcf_arena/setup_match",
        Description = "Build a preset arena map on the active RimWorld map, clear non-combatant clutter, resolve two mutually hostile non-player factions, spawn two opposing giant creatures/teams facing each other, and frame the camera for recording.",
        ResultDescription = "Match setup status and initial GiantArenaMatchState snapshot.")]
    public Task<object> SetupMatch(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "PawnKindDef defName (or short suffix like MechScorpion) for Side A (West / Red).")] string sideAKind = "GCF_MechScorpion",
        [ToolParameter(Description = "PawnKindDef defName (or short suffix like MechScorpion) for Side B (East / Blue).")] string sideBKind = "GCF_MechScorpion",
        [ToolParameter(Description = "Preset arena name: Colosseum, Plains, DesertRuins, Tundra, or VolcanicBasalt.")] string preset = "Colosseum",
        [ToolParameter(Description = "Number of Side A units to spawn (1-8, ignored when sideARaidPoints > 0).")] int sideACount = 1,
        [ToolParameter(Description = "Number of Side B units to spawn (1-8, ignored when sideBRaidPoints > 0).")] int sideBCount = 1,
        [ToolParameter(Description = "Optional RimWorld raid points budget for Side A (e.g. 5000 for a 5000-point Mechanoid raid). 0 = spawn sideAKind x sideACount.")] float sideARaidPoints = 0f,
        [ToolParameter(Description = "Optional RimWorld raid points budget for Side B. 0 = spawn sideBKind x sideBCount.")] float sideBRaidPoints = 0f,
        [ToolParameter(Description = "When sideARaidPoints > 0, restrict generated units strictly to official vanilla/DLC content (no modded units).")] bool sideAVanillaOnly = false,
        [ToolParameter(Description = "When sideBRaidPoints > 0, restrict generated units strictly to official vanilla/DLC content (no modded units).")] bool sideBVanillaOnly = false,
        [ToolParameter(Description = "Horizontal separation in cells between Side A and Side B at spawn.")] int spawnSeparation = 32,
        [ToolParameter(Description = "Half-width of the inner arena in cells (24-96).")] int arenaHalfWidth = 48,
        [ToolParameter(Description = "Half-height of the inner arena in cells (20-84).")] int arenaHalfHeight = 40,
        [ToolParameter(Description = "Optional custom title override for Side A on the Versus Boss Health Bar HUD.")] string? sideALabel = null,
        [ToolParameter(Description = "Optional custom title override for Side B on the Versus Boss Health Bar HUD.")] string? sideBLabel = null,
        [ToolParameter(Description = "Optional FactionDef defName for Side A (auto-resolved to a mutually hostile non-player faction when omitted).")] string? sideAFactionDef = null,
        [ToolParameter(Description = "Optional FactionDef defName for Side B (auto-resolved to a mutually hostile non-player faction when omitted).")] string? sideBFactionDef = null,
        [ToolParameter(Description = "Keep the game paused immediately after setup so the caller can verify framing before stepping/unpausing.")] bool startPaused = true,
        [ToolParameter(Description = "Lock map lighting to 12:00 noon and clear weather.")] bool lockNoonClearWeather = true,
        [ToolParameter(Description = "Suppress colonist bar, alerts, letters, messages, and debug overlays.")] bool cleanVideoMode = true,
        [ToolParameter(Description = "Hide bottom RimWorld main tabs, resource readout, and global controls so only the battlefield and Versus HUD remain.")] bool cinematicHudOnly = true,
        [ToolParameter(Description = "Render side-by-side Red vs Blue top Boss Health Bar HUD.")] bool enableVersusHud = true,
        [ToolParameter(Description = "Hide the top DevMode toolbar during recording.")] bool hideDevToolbar = true,
        [ToolParameter(Description = "Board AI crew pawns into vehicle cockpit seats instead of relying on hostile autonomous fallback.")] bool boardCrewForVehicles = false,
        [ToolParameter(Description = "Explicit initial camera root size (0 = auto-frame both sides).")] float cameraRootSize = 0f,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var cfg = new GiantArenaMatchConfig
            {
                SideAKind = sideAKind,
                SideBKind = sideBKind,
                Preset = preset,
                SideACount = sideACount,
                SideBCount = sideBCount,
                SideARaidPoints = sideARaidPoints,
                SideBRaidPoints = sideBRaidPoints,
                SideAVanillaOnly = sideAVanillaOnly,
                SideBVanillaOnly = sideBVanillaOnly,
                SpawnSeparation = spawnSeparation,
                ArenaHalfWidth = arenaHalfWidth,
                ArenaHalfHeight = arenaHalfHeight,
                SideALabel = sideALabel ?? string.Empty,
                SideBLabel = sideBLabel ?? string.Empty,
                SideAFactionDef = sideAFactionDef ?? string.Empty,
                SideBFactionDef = sideBFactionDef ?? string.Empty,
                StartPaused = startPaused,
                LockNoonClearWeather = lockNoonClearWeather,
                CleanVideoMode = cleanVideoMode,
                CinematicHudOnly = cinematicHudOnly,
                EnableVersusHud = enableVersusHud,
                HideDevToolbar = hideDevToolbar,
                BoardCrewForVehicles = boardCrewForVehicles,
                CameraRootSize = cameraRootSize
            };

            GiantArenaMatchState state = GiantArenaService.SetupMatch(cfg, out string error);
            return new
            {
                success = string.IsNullOrEmpty(error),
                error = string.IsNullOrEmpty(error) ? null : error,
                state
            };
        }, cancellationToken);

    [Tool(
        "gcf_arena/get_match_state",
        Description = "Read live battle telemetry for the active Giant Arena match: status, winner, elapsed ticks, per-fighter HP/position/severed parts/animation, and recommended camera framing.",
        ResultDescription = "Current GiantArenaMatchState payload.")]
    public Task<object> GetMatchState(
        IRimBridgeContext ctx,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GiantArenaMatchState state = GiantArenaService.GetMatchState();
            return new
            {
                success = true,
                state
            };
        }, cancellationToken);

    [Tool(
        "gcf_arena/frame_camera",
        Description = "Frame the camera smoothly (or immediately) around all surviving fighters in the active Giant Arena match.",
        ResultDescription = "Applied camera recommendation (centerX, centerZ, rootSize, spanX, spanZ).")]
    public Task<object> FrameCamera(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Lerp factor in (0, 1]. Use 1.0 for instant snap or 0.18-0.35 for smooth cinematic tracking between frames.")] float smoothFactor = 1.0f,
        [ToolParameter(Description = "Margin cells around the outer bounds of all active fighters.")] float paddingCells = 10.0f,
        [ToolParameter(Description = "Minimum camera rootSize (closest zoom).")] float minRootSize = 15.0f,
        [ToolParameter(Description = "Maximum camera rootSize (widest zoom).")] float maxRootSize = 46.0f,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GiantArenaCameraRecommendation cam = GiantArenaService.FrameMatchCamera(
                smoothFactor,
                paddingCells,
                minRootSize,
                maxRootSize);
            return new
            {
                success = true,
                camera = cam
            };
        }, cancellationToken);

    [Tool(
        "gcf_arena/reset_rematch",
        Description = "Instantly clear and restart the current Giant Arena matchup without restarting RimWorld, optionally swapping Side A and Side B.",
        ResultDescription = "Match setup status and fresh GiantArenaMatchState snapshot.")]
    public Task<object> ResetRematch(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Swap Side A and Side B starting positions/factions for the rematch.")] bool swapSides = false,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GiantArenaMatchState state = GiantArenaService.ResetRematch(swapSides, out string error);
            return new
            {
                success = string.IsNullOrEmpty(error),
                error = string.IsNullOrEmpty(error) ? null : error,
                state
            };
        }, cancellationToken);

    [Tool(
        "gcf_arena/set_recording_mode",
        Description = "Update video recording presentation flags (cleanVideoMode, cinematicHudOnly, enableVersusHud, hideDevToolbar, disableDebugOverlays) on the active arena.",
        ResultDescription = "Updated presentation flags.")]
    public Task<object> SetRecordingMode(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Suppress colonist bar, alerts, letters, and messages.")] bool? cleanVideoMode = null,
        [ToolParameter(Description = "Hide RimWorld bottom main buttons, resource readout, and global controls.")] bool? cinematicHudOnly = null,
        [ToolParameter(Description = "Render side-by-side Red vs Blue top Boss Health Bar HUD.")] bool? enableVersusHud = null,
        [ToolParameter(Description = "Hide top DevMode toolbar.")] bool? hideDevToolbar = null,
        [ToolParameter(Description = "Turn off GCF collision/hit/skeleton debug overlays.")] bool? disableDebugOverlays = null,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GiantArenaService.ApplyRecordingSettings(
                cleanVideoMode,
                cinematicHudOnly,
                enableVersusHud,
                hideDevToolbar,
                disableDebugOverlays);
            return new
            {
                success = true,
                arenaActive = GiantArenaService.IsArenaActive,
                cleanVideoMode = GiantArenaService.IsCleanVideoMode,
                cinematicHudOnly = GiantArenaService.IsCinematicHudOnly,
                devMode = Prefs.DevMode
            };
        }, cancellationToken);

    [Tool(
        "gcf_arena/stop_arena",
        Description = "Exit Giant Arena isolation mode and optionally restore prior RimWorld UI/DevMode settings.",
        ResultDescription = "Confirmation flag.")]
    public Task<object> StopArena(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Restore saved DevMode / colonist bar / auto-pause preferences.")] bool restoreSettings = true,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GiantArenaService.StopArena(restoreSettings);
            return new
            {
                success = true,
                arenaActive = GiantArenaService.IsArenaActive
            };
        }, cancellationToken);

    [Tool(
        "gcf_arena/start_native_recording",
        Description = "Start native 60fps in-process Vulkan-to-FFmpeg lockstep recording of the active Giant Arena match (1.00x real-time when fps*ticksPerFrame==60, with smooth camera tracking and automatic post-KO hold).",
        ResultDescription = "Start confirmation and initial recording status.")]
    public Task<object> StartNativeRecording(
        IRimBridgeContext ctx,
        [ToolParameter(Description = "Absolute output .mp4 file path.")] string outputPath,
        [ToolParameter(Description = "Optional path or command name for ffmpeg (defaults to GCF_FFMPEG_PATH / FFMPEG_PATH / PATH).")] string ffmpegPath = "",
        [ToolParameter(Description = "Output framerate (default 60 fps).")] int fps = 60,
        [ToolParameter(Description = "RimWorld simulation ticks stepped per rendered frame (default 1 => 60fps * 1 = 1.00x real-time).")] int ticksPerFrame = 1,
        [ToolParameter(Description = "Opening face-off hold in seconds while Boss Health Bars unfold.")] float introSeconds = 1.5f,
        [ToolParameter(Description = "Maximum battle recording duration in seconds before auto-stop.")] float maxMatchSeconds = 180f,
        [ToolParameter(Description = "Seconds to keep recording after KO so death animation and victor aftermath play out cleanly.")] float postKoSeconds = 5.0f,
        [ToolParameter(Description = "Enable smooth 60fps in-engine camera tracking.")] bool trackCamera = true,
        [ToolParameter(Description = "Per-frame camera tracking exponential smoothing factor (default 0.07 at 60fps).")] float cameraSmooth = 0.07f,
        [ToolParameter(Description = "Camera margin cells around active fighters.")] float cameraPadding = 10.5f,
        [ToolParameter(Description = "Closest camera rootSize.")] float cameraMinSize = 16.0f,
        [ToolParameter(Description = "Widest camera rootSize.")] float cameraMaxSize = 44.0f,
        [ToolParameter(Description = "FFmpeg libx264 CRF quality (lower = higher quality, default 17).")] int crf = 17,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var req = new GiantArenaRecordingRequest
            {
                OutputPath = outputPath,
                FfmpegPath = ffmpegPath,
                Fps = fps,
                TicksPerFrame = ticksPerFrame,
                IntroSeconds = introSeconds,
                MaxMatchSeconds = maxMatchSeconds,
                PostKoSeconds = postKoSeconds,
                TrackCamera = trackCamera,
                CameraSmooth = cameraSmooth,
                CameraPadding = cameraPadding,
                CameraMinSize = cameraMinSize,
                CameraMaxSize = cameraMaxSize,
                Crf = crf
            };
            bool started = GiantArenaVideoRecorder.StartRecording(req, out string error);
            return new
            {
                success = started,
                error = string.IsNullOrEmpty(error) ? null : error,
                status = GiantArenaVideoRecorder.GetStatus()
            };
        }, cancellationToken);

    [Tool(
        "gcf_arena/get_recording_status",
        Description = "Poll progress and completion status of an active or finished native Giant Arena recording.",
        ResultDescription = "Current GiantArenaRecordingStatus snapshot.")]
    public Task<object> GetRecordingStatus(
        IRimBridgeContext ctx,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() => new
        {
            success = true,
            status = GiantArenaVideoRecorder.GetStatus()
        }, cancellationToken);

    [Tool(
        "gcf_arena/stop_native_recording",
        Description = "Stop an active native Giant Arena recording early and flush the MP4 file cleanly.",
        ResultDescription = "Final GiantArenaRecordingStatus snapshot.")]
    public Task<object> StopNativeRecording(
        IRimBridgeContext ctx,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GiantArenaVideoRecorder.StopRecording(waitForFlush: true);
            return new
            {
                success = true,
                status = GiantArenaVideoRecorder.GetStatus()
            };
        }, cancellationToken);
}
