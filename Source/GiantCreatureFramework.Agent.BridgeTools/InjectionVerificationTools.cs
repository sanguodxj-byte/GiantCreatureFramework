using System.Text.Json;
using System.Text.Json.Nodes;
using GiantCreatureFramework;
using GiantCreatureFramework.Agent;
using GiantCreatureFramework.Agent.Authoring;
using GiantCreatureFramework.Agent.Injection;
using GiantCreatureFramework.Agent.UI;
using RimBridgeServer.Sdk;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GiantCreatureFramework.Agent.BridgeTools;

/// <summary>
/// Development-only bridge tools that drive the runtime authoring terminal, tool providers,
/// Def injector, Scribe save/load, and world-state verification in the isolated GABS instance.
/// </summary>
public sealed partial class InjectionVerificationTools
{
    [Tool("gcfagent/content_root", Description = "Report the agent content root, its owner mod and what the startup load read from it.")]
    public Task<object> ContentRoot(IRimBridgeContext ctx, CancellationToken cancellationToken) =>
        ctx.MainThread.InvokeAsync<object>(() => new
        {
            rootPath = AgentContentRoot.RootPath,
            owner = AgentContentRoot.Owner?.PackageIdPlayerFacing,
            loadFolderRegistered = AgentContentRoot.Owner?.foldersToLoadDescendingOrder.Contains(AgentContentRoot.RootPath) == true,
            creatureDirs = AgentContentRoot.CreatureDirectories().Select(Path.GetFileName).ToArray(),
            defFilesOnDisk = AgentContentRoot.DefFiles().Count(),
            startupDefFiles = AgentContentRoot.StartupFileHashes.Count,
            enableAuthoringTerminal = GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal,
            isTerminalEnabled = GiantCreatureAgentMod.IsTerminalEnabled,
            programState = Current.ProgramState.ToString(),
            map = Find.CurrentMap?.uniqueID ?? -1
        }, cancellationToken);

    [Tool("gcfagent/set_terminal_enabled", Description = "Set enableAuthoringTerminal on GiantCreatureFrameworkMod.Settings and report UI/network gate state.")]
    public Task<object> SetTerminalEnabled(IRimBridgeContext ctx, bool enabled, bool openWindow = false, CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal = enabled;
            GiantCreatureFrameworkMod.Settings.ApplyToRuntime();
            ModWriteHelper.WriteGcfSettingsSafe();
            if (openWindow)
            {
                Dialog_AgentChat.OpenWindow();
            }
            var worker = new MainButtonWorker_AuthoringTerminal();
            return new
            {
                enableAuthoringTerminal = GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal,
                isTerminalEnabled = GiantCreatureAgentMod.IsTerminalEnabled,
                mainButtonVisible = worker.Visible,
                chatWindowOpen = Dialog_AgentChat.IsWindowOpen,
                networkTurnsStarted = AgentLlmClient.NetworkTurnRequestsStarted
            };
        }, cancellationToken);

    [Tool("gcfagent/verify_offline_gate", Description = "Verify that when enableAuthoringTerminal is false, opening UI or calling StartTurn starts 0 network requests, and toggling off mid-turn aborts immediately.")]
    public async Task<object> VerifyOfflineGate(IRimBridgeContext ctx, CancellationToken cancellationToken = default)
    {
        return await ctx.MainThread.InvokeAsync<object>(() =>
        {
            MainThreadDispatcher.MarkMainThread();
            AgentLlmClient.ResetNetworkCounterForVerification();

            GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal = false;
            GiantCreatureFrameworkMod.Settings.ApplyToRuntime();
            var workerOff = new MainButtonWorker_AuthoringTerminal();
            var visibleWhenOff = workerOff.Visible;

            Dialog_AgentChat.OpenWindow();
            var windowOpenedWhenOff = Dialog_AgentChat.IsWindowOpen;

            AgentSessionManager.RefreshScope();
            var session = AgentSessionManager.CurrentSession!;
            AgentLlmClient.StartTurn(session, "should be blocked while terminal disabled");
            var turnsStartedWhenOff = AgentLlmClient.NetworkTurnRequestsStarted;
            var busyWhenOff = session.IsBusy;

            GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal = true;
            GiantCreatureFrameworkMod.Settings.ApplyToRuntime();
            var visibleWhenOn = workerOff.Visible;
            Dialog_AgentChat.OpenWindow();
            var windowOpenedWhenOn = Dialog_AgentChat.IsWindowOpen;

            using var simulatedTurn = session.TryBeginTurn();
            var wasBusyBeforeToggleOff = session.IsBusy;
            GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal = false;
            GiantCreatureFrameworkMod.Settings.ApplyToRuntime();
            var cancelledOnToggleOff = simulatedTurn?.Cancellation.IsCancellationRequested ?? false;
            var windowClosedOnToggleOff = !Dialog_AgentChat.IsWindowOpen;
            var hiddenAfterToggleOff = !workerOff.Visible;
            if (simulatedTurn != null)
            {
                session.Complete(simulatedTurn, "aborted");
            }

            return new
            {
                visibleWhenOff,
                windowOpenedWhenOff,
                turnsStartedWhenOff,
                busyWhenOff,
                visibleWhenOn,
                windowOpenedWhenOn,
                wasBusyBeforeToggleOff,
                cancelledOnToggleOff,
                windowClosedOnToggleOff,
                hiddenAfterToggleOff
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    [Tool("gcfagent/configure_endpoints", Description = "Configure chat and image endpoints on GiantCreatureAgentMod.Settings.")]
    public Task<object> ConfigureEndpoints(
        IRimBridgeContext ctx,
        string chatEndpoint,
        string chatModel,
        string chatApiKey = "local",
        string imageEndpoint = "http://127.0.0.1:2048/v1",
        string imageModel = "gemini-3-pro-image",
        string imageApiKey = "",
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var s = GiantCreatureAgentMod.Settings;
            s.ApiConfigs.Clear();
            s.ApiConfigs.Add(new ApiConfigEntry
            {
                IsEnabled = true,
                Provider = AIProvider.Custom,
                CustomUrl = chatEndpoint,
                ApiKey = chatApiKey,
                SelectedModel = chatModel,
                CustomModelName = chatModel
            });
            s.SyncActiveConfigToPrimaryFields();
            s.ImageApiEndpoint = imageEndpoint;
            s.ImageModelName = imageModel;
            s.ImageApiKey = imageApiKey;
            s.RequestTimeoutSeconds = Math.Max(s.RequestTimeoutSeconds, 360);
            s.StreamIdleTimeoutSeconds = Math.Max(s.StreamIdleTimeoutSeconds, 180);
            s.ImageRequestTimeoutSeconds = Math.Max(s.ImageRequestTimeoutSeconds, 240);
            s.AskConversationHistory = false;
            s.DefaultConversationHistory = true;
            s.SaveToUserConfig();
            try { Prefs.RunInBackground = true; } catch { }
            return new
            {
                chatEndpoint = s.ApiEndpoint,
                chatModel = s.ModelName,
                imageEndpoint = s.ImageApiEndpoint,
                imageModel = s.ImageModelName
            };
        }, cancellationToken);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<object>> AsyncJobs = new();

    [Tool("gcfagent/start_tool", Description = "Start any registered AgentToolRegistry tool on a background thread using a local IPC JSON file and return a jobId immediately.")]
    public Task<object> StartTool(IRimBridgeContext ctx, string toolName, string ipcFile)
    {
        var jobId = "job_" + Guid.NewGuid().ToString("N");
        AsyncJobs[jobId] = Task.Run<object>(async () =>
        {
            var argumentsJson = File.Exists(ipcFile) ? await File.ReadAllTextAsync(ipcFile, System.Text.Encoding.UTF8).ConfigureAwait(false) : "{}";
            var execCtx = new AgentToolExecutionContext("e2e", 0, 0, Array.Empty<string>(), "");
            var (wrappedJson, durationMs) = await AgentToolRegistry.ExecuteToolAsync(toolName, argumentsJson, execCtx, CancellationToken.None).ConfigureAwait(false);
            var node = JsonNode.Parse(wrappedJson) as JsonObject;
            var hasAttachedImg = node?["_attachedImageDataUrl"] != null;
            if (node != null)
            {
                node.Remove("_attachedImageDataUrl");
            }
            var outText = node?.ToJsonString(Evidence.UnescapedJsonOptions) ?? wrappedJson;
            await File.WriteAllTextAsync(ipcFile + ".out.json", outText, System.Text.Encoding.UTF8).ConfigureAwait(false);
            return new
            {
                done = true,
                durationMs,
                hasAttachedImage = hasAttachedImg
            };
        });
        return Task.FromResult<object>(new { jobId });
    }

    [Tool("gcfagent/poll_job", Description = "Poll a background job started by start_tool or start_chat_turn.")]
    public async Task<object> PollJob(IRimBridgeContext ctx, string jobId, CancellationToken cancellationToken = default)
    {
        if (!AsyncJobs.TryGetValue(jobId, out var task))
        {
            return new { done = true, faulted = true, error = "unknown_job" };
        }
        if (!task.IsCompleted)
        {
            await ctx.MainThread.InvokeAsync(() =>
            {
                MainThreadDispatcher.MarkMainThread();
                MainThreadDispatcher.Pump();
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return new { done = false };
        }
        AsyncJobs.TryRemove(jobId, out _);
        if (task.IsFaulted)
        {
            return new { done = true, faulted = true, error = task.Exception?.GetBaseException().Message ?? "faulted" };
        }
        return await task.ConfigureAwait(false);
    }

    [Tool("gcfagent/start_chat_turn", Description = "Start a real multi-round LLM turn via AgentLlmClient.StartTurn using a local IPC prompt file (supporting multi-turn sessionId reuse) and return a jobId immediately.")]
    public async Task<object> StartChatTurn(IRimBridgeContext ctx, string ipcFile, int timeoutSec = 420, string sessionId = "", CancellationToken cancellationToken = default)
    {
        var prompt = await File.ReadAllTextAsync(ipcFile, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var (session, beforeCount) = await ctx.MainThread.InvokeAsync(() =>
        {
            MainThreadDispatcher.MarkMainThread();
            try { Prefs.RunInBackground = true; } catch { }
            GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal = true;
            GiantCreatureFrameworkMod.Settings.ApplyToRuntime();
            AgentSessionManager.RefreshScope();

            AgentSession s;
            if (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, "new", StringComparison.OrdinalIgnoreCase))
            {
                s = AgentSessionManager.Create();
            }
            else if (string.Equals(sessionId, "current", StringComparison.OrdinalIgnoreCase))
            {
                s = AgentSessionManager.CurrentSession ?? AgentSessionManager.Create();
            }
            else
            {
                s = AgentSessionManager.Sessions.FirstOrDefault(x => string.Equals(x.Id, sessionId, StringComparison.Ordinal))
                    ?? AgentSessionManager.Create();
            }

            AgentSessionManager.CurrentSession = s;
            s.UseConversationHistory = true;
            var msgCountBefore = s.Messages.Count;
            AgentLlmClient.StartTurn(s, prompt);
            return (s, msgCountBefore);
        }, cancellationToken).ConfigureAwait(false);

        var jobId = "chat_" + Guid.NewGuid().ToString("N");
        AsyncJobs[jobId] = Task.Run<object>(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSec, 10, 900));
            while (DateTime.UtcNow < deadline)
            {
                if (!session.IsBusy) break;
                await Task.Delay(250).ConfigureAwait(false);
            }
            var assistantTurnIndex = session.Messages.Count(m => m.Role == ChatRole.Assistant);
            var last = session.Messages.Skip(beforeCount).LastOrDefault(m => m.Role == ChatRole.Assistant)
                       ?? session.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
            var outObj = new
            {
                done = true,
                completed = !session.IsBusy,
                sessionId = session.Id,
                assistantTurnIndex,
                messageCount = session.Messages.Count,
                status = session.StatusText,
                isError = last?.IsError ?? true,
                answerKind = last?.AnswerKind ?? "",
                content = last?.Content ?? "",
                citedEvidenceIds = last?.CitedEvidenceIds.ToArray() ?? Array.Empty<string>(),
                promptTokens = last?.PromptTokens ?? 0,
                cachedPromptTokens = last?.CachedPromptTokens ?? 0,
                uncachedPromptTokens = last?.UncachedPromptTokens ?? 0,
                completionTokens = last?.CompletionTokens ?? 0,
                totalTokens = last?.TotalTokens ?? 0,
                peakContextTokens = last?.PeakContextTokens ?? 0,
                llmRounds = last?.LlmRounds ?? 0,
                isEstimatedTokens = last?.IsEstimatedTokens ?? true,
                sessionTotals = new
                {
                    totalPromptTokens = session.TotalPromptTokens,
                    totalCachedPromptTokens = session.TotalCachedPromptTokens,
                    totalUncachedPromptTokens = session.TotalUncachedPromptTokens,
                    totalCompletionTokens = session.TotalCompletionTokens,
                    totalTokens = session.TotalTokens,
                    effectiveNetTokens = session.EffectiveNetTokens,
                    peakContextTokens = session.PeakContextTokens,
                    cacheHitRate = session.CacheHitRate
                },
                toolCalls = last?.ToolCalls.Select(tc => new
                {
                    tc.CallId,
                    tc.ToolName,
                    tc.IsCompleted,
                    tc.IsError,
                    tc.DurationMs,
                    argumentsPreview = tc.ArgumentsJson.Length > 600 ? tc.ArgumentsJson[..600] + "..." : tc.ArgumentsJson,
                    resultPreview = tc.ResultJson.Length > 800 ? tc.ResultJson[..800] + "..." : tc.ResultJson
                }).ToArray() ?? Array.Empty<object>()
            };
            var json = JsonSerializer.Serialize(outObj, Evidence.UnescapedJsonOptions);
            await File.WriteAllTextAsync(ipcFile + ".out.json", json, System.Text.Encoding.UTF8).ConfigureAwait(false);
            return new
            {
                done = true,
                completed = outObj.completed,
                sessionId = outObj.sessionId,
                assistantTurnIndex = outObj.assistantTurnIndex,
                isError = outObj.isError,
                llmRounds = outObj.llmRounds,
                promptTokens = outObj.promptTokens,
                cachedPromptTokens = outObj.cachedPromptTokens,
                completionTokens = outObj.completionTokens,
                totalTokens = outObj.totalTokens,
                isEstimatedTokens = outObj.isEstimatedTokens
            };
        });
        return new { jobId };
    }

    [Tool("gcfagent/inject", Description = "Run the Def injector over the agent content root (or one creature) and return its report.")]
    public async Task<object> Inject(IRimBridgeContext ctx, bool dryRun = false, string creatureName = "", CancellationToken cancellationToken = default)
    {
        var target = string.IsNullOrWhiteSpace(creatureName) ? null : creatureName.Trim();
        var pending = await ctx.MainThread.InvokeAsync(() => GcfDefInjector.InjectAsync(dryRun, target), cancellationToken).ConfigureAwait(false);
        var report = await pending.WaitAsync(TimeSpan.FromSeconds(180), cancellationToken).ConfigureAwait(false);
        return new
        {
            committed = report.Committed, dryRun = report.DryRun, rolledBack = report.RolledBack,
            added = report.Added, replaced = report.Replaced, implied = report.Implied, textures = report.Textures,
            errors = report.Errors.Select(e => new { e.Code, e.Subject, e.Detail }).ToArray(),
            warnings = report.Warnings.Select(e => new { e.Code, e.Subject, e.Detail }).ToArray()
        };
    }

    [Tool("gcfagent/resign_workspace", Description = "Re-sign self-generated PNG provenance inside an isolated creature workspace across RimWorld restarts.")]
    public Task<object> ResignWorkspace(IRimBridgeContext ctx, string creatureName, CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var cDir = Path.Combine(AgentContentRoot.RootPath, creatureName);
            int signedCount = 0;
            if (Directory.Exists(cDir))
            {
                foreach (var png in Directory.GetFiles(cDir, "*.png", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(cDir, png).Replace('\\', '/');
                    if (rel.EndsWith("validate_preview.png", StringComparison.OrdinalIgnoreCase)) continue;
                    string stage = rel.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
                        ? "warped_bone"
                        : (rel.Contains("_parts/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("Work/parts/", StringComparison.OrdinalIgnoreCase)
                            ? "extracted_part"
                            : "generations");
                    UserImg2ImgGuard.RecordSelfGeneratedPng(cDir, rel, File.ReadAllBytes(png), stage);
                    signedCount++;
                }
            }
            return new { ok = true, creatureName, signedCount };
        }, cancellationToken);

    [Tool("gcfagent/verify_gunship_live", Description = "Re-sign TwinRotorGunship provenance inside RimWorld, run RigJointAuditor + live Def injection, and empirically test mechanical, flight, symmetry, ventral turret, 6-layer ground-melee immunity, and zero-lift crash-landing attributes.")]
    public async Task<object> VerifyGunshipLive(IRimBridgeContext ctx, string creatureName = "TwinRotorGunship", string raceDefName = "Custom_TwinRotorGunship", CancellationToken cancellationToken = default)
    {
        // 1. Re-sign PNG provenance under RimWorld's AppContext.BaseDirectory and run RigJointAuditor
        var cDir = Path.Combine(AgentContentRoot.RootPath, creatureName);
        int signedCount = 0;
        if (Directory.Exists(cDir))
        {
            foreach (var png in Directory.GetFiles(cDir, "*.png", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(cDir, png).Replace('\\', '/');
                if (rel.EndsWith("validate_preview.png", StringComparison.OrdinalIgnoreCase)) continue;
                string stage = rel.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
                    ? "warped_bone"
                    : (rel.StartsWith("Work/parts/", StringComparison.OrdinalIgnoreCase) ? "extracted_part" : "generations");
                UserImg2ImgGuard.RecordSelfGeneratedPng(cDir, rel, File.ReadAllBytes(png), stage);
                signedCount++;
            }
        }

        var rigReports = RigJointAuditor.AuditCreatureWorkspace(cDir, minOutlinePx: 6);
        var pendingInject = await ctx.MainThread.InvokeAsync(() =>
        {
            MainThreadDispatcher.MarkMainThread();
            return GcfDefInjector.InjectAsync(dryRun: false, creatureName);
        }, cancellationToken).ConfigureAwait(false);
        var injectReport = await pendingInject.WaitAsync(TimeSpan.FromSeconds(180), cancellationToken).ConfigureAwait(false);

        // 2. Spawn test entities on current map and verify static + live properties
        var phase1 = await ctx.MainThread.InvokeAsync(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("No active map.");
            var race = DefDatabase<ThingDef>.GetNamed(raceDefName);
            var kind = DefDatabase<PawnKindDef>.GetNamed(raceDefName);
            var rigDef = race.comps.OfType<CompProperties_GiantRig>().First().rigDef;
            var bulletDef = DefDatabase<ThingDef>.GetNamed("Bullet_Custom_TRG_HydraRocket");

            var configErrors = new List<string>();
            foreach (var e in race.ConfigErrors()) configErrors.Add($"ThingDef({race.defName}): {e}");
            foreach (var e in kind.ConfigErrors()) configErrors.Add($"PawnKindDef({kind.defName}): {e}");
            foreach (var e in race.race.body.ConfigErrors()) configErrors.Add($"BodyDef({race.race.body.defName}): {e}");
            foreach (var e in rigDef.ConfigErrors()) configErrors.Add($"GiantRigDef({rigDef.defName}): {e}");
            foreach (var e in bulletDef.ConfigErrors()) configErrors.Add($"Projectile({bulletDef.defName}): {e}");

            var center = map.Center;
            var testOrigin = new IntVec3(Mathf.Clamp(center.x - 18, 12, map.Size.x - 12), 0, Mathf.Clamp(center.z - 18, 12, map.Size.z - 12));
            var wallCell = testOrigin + new IntVec3(3, 0, 0);
            var waterCell = testOrigin + new IntVec3(0, 0, 3);
            var prevTerrain = map.terrainGrid.TerrainAt(waterCell);

            // Clear temporary cells
            foreach (var c in new[] { testOrigin, wallCell, waterCell, testOrigin + new IntVec3(-2, 0, 0), testOrigin + new IntVec3(-4, 0, 0) })
            {
                map.roofGrid?.SetRoof(c, null);
                foreach (var t in c.GetThingList(map).ToList())
                {
                    if (t is not Pawn && t.def.destroyable && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
                }
            }

            var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
            GenSpawn.Spawn(wall, wallCell, map, Rot4.North, WipeMode.Vanish);
            map.terrainGrid.SetTerrain(waterCell, TerrainDefOf.WaterDeep);

            var hostileFac = Faction.OfMechanoids ?? Find.FactionManager.FirstFactionOfDef(FactionDefOf.AncientsHostile);
            var gunship = PawnGenerator.GeneratePawn(kind, hostileFac);
            GenSpawn.Spawn(gunship, testOrigin, map, Rot4.South, WipeMode.Vanish);
            var rig = GiantFastLookup.TryGetRig(gunship)!;
            rig.RebuildFootprintNow(notifyMap: true);

            // Drafted melee-only colonist (guaranteed capable of violence)
            var meleePawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, mustBeCapableOfViolence: true));
            GenSpawn.Spawn(meleePawn, testOrigin + new IntVec3(-1, 0, -1), map, Rot4.North, WipeMode.Vanish);
            meleePawn.equipment?.DestroyAllEquipment();
            var swordDef = DefDatabase<ThingDef>.GetNamedSilentFail("MeleeWeapon_MonoSword") ?? DefDatabase<ThingDef>.GetNamed("MeleeWeapon_LongSword");
            var sword = (ThingWithComps)ThingMaker.MakeThing(swordDef, swordDef.MadeFromStuff ? ThingDefOf.Plasteel : null);
            meleePawn.equipment?.AddEquipment(sword);
            if (meleePawn.drafter != null) meleePawn.drafter.Drafted = true;

            // Drafted ranged colonist (guaranteed capable of violence; also carries weapon capable of melee bash unless blocked!)
            var rangedPawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, mustBeCapableOfViolence: true));
            GenSpawn.Spawn(rangedPawn, testOrigin + new IntVec3(-4, 0, -4), map, Rot4.North, WipeMode.Vanish);
            rangedPawn.equipment?.DestroyAllEquipment();
            var rifleDef = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_ChargeRifle") ?? DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle");
            var rifle = (ThingWithComps)ThingMaker.MakeThing(rifleDef);
            rangedPawn.equipment?.AddEquipment(rifle);
            if (rangedPawn.drafter != null) rangedPawn.drafter.Drafted = true;

            // 1) FloatMenu right-click checks
            var meleeOnlyAction = FloatMenuUtility.GetMeleeAttackAction(meleePawn, gunship, out string meleeFailStr);
            var rangedPawnMeleeAction = FloatMenuUtility.GetMeleeAttackAction(rangedPawn, gunship, out string rangedMeleeFailStr);
            var rangedPawnShootAction = FloatMenuUtility.GetRangedAttackAction(rangedPawn, gunship, out string rangedShootFailStr);

            // 2) Reachability & AI target finder checks
            bool meleeCanReachImmediate = meleePawn.CanReachImmediate(gunship, PathEndMode.Touch);
            var aiMeleeBestTarget = AttackTargetFinder.BestAttackTarget(
                meleePawn,
                TargetScanFlags.NeedReachableIfCantHitFromMyPos | TargetScanFlags.NeedThreat,
                x => x == gunship,
                0f, 40f);
            var aiRangedBestTarget = AttackTargetFinder.BestAttackTarget(
                rangedPawn,
                TargetScanFlags.NeedReachableIfCantHitFromMyPos | TargetScanFlags.NeedThreat,
                x => x == gunship,
                0f, 40f);

            // 3) Forced JobDriver_AttackMelee reservation gate check
            var forcedMeleeJob = JobMaker.MakeJob(JobDefOf.AttackMelee, gunship);
            bool forcedMeleeStarted = meleePawn.jobs?.TryTakeOrderedJob(forcedMeleeJob, JobTag.Misc) ?? false;
            bool forcedMeleeJobActive = meleePawn.CurJobDef == JobDefOf.AttackMelee;
            if (forcedMeleeJobActive)
            {
                meleePawn.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
            }
            bool forcedMeleeReservationAllowed = forcedMeleeStarted && forcedMeleeJobActive;

            // 4) Direct TakeDamage melee vs ranged check
            float hpBeforeMelee = gunship.health.summaryHealth.SummaryHealthPercent;
            var meleeDmgRes = gunship.TakeDamage(new DamageInfo(DamageDefOf.Cut, 60f, 2.0f, -1f, meleePawn, null, swordDef));
            float hpAfterMelee = gunship.health.summaryHealth.SummaryHealthPercent;

            var bulletDmgRes = gunship.TakeDamage(new DamageInfo(DamageDefOf.Bullet, 35f, 2.0f, -1f, rangedPawn, null, rifleDef));
            float hpAfterBullet = gunship.health.summaryHealth.SummaryHealthPercent;

            // 5) Obstacle overflight passability check
            bool wallPassableForFlight = GiantFlightUtility.IsCellPassableForFlight(map, rig, wallCell);
            bool waterPassableForFlight = GiantFlightUtility.IsCellPassableForFlight(map, rig, waterCell);
            bool wallWalkableGround = wallCell.WalkableBy(map, meleePawn);
            bool waterWalkableGround = waterCell.WalkableBy(map, meleePawn);

            // 6) Layer & symmetry inspection on live RigDef
            var nodes = rigDef.SortedNodes.ToDictionary(n => n.nodeId, StringComparer.Ordinal);
            var fuselageNode = nodes["fuselage"];
            var noseNode = nodes["nose_cannon"];
            var wingLNode = nodes["wing_left"];
            var wingRNode = nodes["wing_right"];
            var rotorLNode = nodes["rotor_left"];
            var rotorRNode = nodes["rotor_right"];

            bool airborneBeforeSever = rig.IsCurrentlyAirborne;
            float liftBeforeSever = rig.FlightLocomotion?.LiftEfficiency ?? -1f;
            var mechanoidInfo = new
            {
                isMechanoid = gunship.RaceProps?.IsMechanoid ?? false,
                fleshType = gunship.RaceProps?.FleshType?.defName ?? "null",
                bloodDef = gunship.RaceProps?.BloodDef?.defName ?? "null",
                meatDef = gunship.RaceProps?.meatDef?.defName ?? "null",
                butcherProducts = gunship.def?.butcherProducts?.Select(bp => $"{bp.thingDef?.defName}:{bp.count}").ToArray() ?? Array.Empty<string>(),
                bleedRateTotal = gunship.health?.hediffSet?.BleedRateTotal ?? -1f,
                flammability = gunship.GetStatValue(StatDefOf.Flammability),
                canMeleeAttackKind = gunship.kindDef?.canMeleeAttack ?? true,
                allPartsSolidNonLiving = gunship.RaceProps?.body?.AllParts?.All(p => p?.def != null && !p.def.alive && p.def.IsSolid(p, gunship.health!.hediffSet.hediffs)) ?? false
            };

            // 7) Sever BOTH rotors to test crashOnZeroLift over the next ticks
            var rotorParts = gunship.health!.hediffSet.GetNotMissingParts()
                .Where(p => p.def?.defName == "Custom_TRG_TiltRotorAssembly")
                .ToList();
            foreach (var rp in rotorParts)
            {
                gunship.health.AddHediff(HediffDefOf.MissingBodyPart, rp);
            }
            rig.RebuildFootprintNow(notifyMap: true);

            return new
            {
                gunship,
                rig,
                meleePawn,
                rangedPawn,
                wall,
                waterCell,
                prevTerrain,
                configErrors,
                mechanoid = mechanoidInfo,
                flightAndVehicle = new
                {
                    airborneBeforeSever,
                    liftBeforeSever,
                    flightMode = rigDef.ActiveFlightLocomotion?.flightMode.ToString(),
                    altitudeTier = rigDef.ActiveFlightLocomotion?.altitudeTier.ToString(),
                    airframeType = rigDef.flightVehicleLocomotion?.airframeType.ToString(),
                    crushBuildingsAndTerrain = rigDef.crushBuildingsAndTerrain,
                    rushWhenEnemyFar = rigDef.rushWhenEnemyFar,
                    autoFootstepVfx = rigDef.autoFootstepVfx,
                    canFlyOverWalls = rigDef.ActiveFlightLocomotion?.canFlyOverWalls ?? false,
                    canFlyOverWaterAndLava = rigDef.ActiveFlightLocomotion?.canFlyOverWaterAndLava ?? false,
                    canHoverOverImpassable = rigDef.ActiveFlightLocomotion?.canHoverOverImpassable ?? false,
                    crashOnZeroLift = rigDef.ActiveFlightLocomotion?.crashOnZeroLift ?? false,
                    wallPassableForFlight,
                    wallWalkableGround,
                    waterPassableForFlight,
                    waterWalkableGround
                },
                rigGeometry = new
                {
                    fuselageLayer = fuselageNode.layerOffset,
                    noseCannonLayer = noseNode.layerOffset,
                    noseCannonBelowFuselage = noseNode.layerOffset <= fuselageNode.layerOffset - 0.5f,
                    noseCannonIsTurret = noseNode.isTurret,
                    wingLayers = new[] { wingLNode.layerOffset, wingRNode.layerOffset },
                    wingsRigid = new[] { wingLNode.isRigid, wingRNode.isRigid },
                    wingPivotsSumX = wingLNode.pivotPixel.x + wingRNode.pivotPixel.x,
                    wingParentSocketsSumX = wingLNode.parentSocketPixel.x + wingRNode.parentSocketPixel.x,
                    rotorLayers = new[] { rotorLNode.layerOffset, rotorRNode.layerOffset },
                    rotorParentSocketsSumX = rotorLNode.parentSocketPixel.x + rotorRNode.parentSocketPixel.x,
                    rotorSpins = new[] { rotorLNode.continuousSpinDegPerTick, rotorRNode.continuousSpinDegPerTick }
                },
                groundMeleeImmunity = new
                {
                    meleeOnlyFloatMenuActionBlocked = meleeOnlyAction == null,
                    meleeFailReason = meleeFailStr ?? "",
                    rangedPawnMeleeFloatMenuActionBlocked = rangedPawnMeleeAction == null,
                    rangedPawnMeleeFailReason = rangedMeleeFailStr ?? "",
                    rangedPawnShootActionAvailable = rangedPawnShootAction != null,
                    rangedShootFailReason = rangedShootFailStr ?? "",
                    meleeCanReachImmediate,
                    aiMeleeTargetNull = aiMeleeBestTarget == null,
                    aiRangedTargetFound = aiRangedBestTarget != null,
                    forcedMeleeReservationBlocked = !forcedMeleeReservationAllowed,
                    meleeResultNonNull = meleeDmgRes != null,
                    groundMeleeDamageDealt = meleeDmgRes?.totalDamageDealt ?? 0f,
                    hpBeforeMelee,
                    hpAfterMelee,
                    rangedBulletDamageDealt = bulletDmgRes?.totalDamageDealt ?? 0f,
                    hpAfterBullet
                },
                severedRotorCount = rotorParts.Count
            };
        }, cancellationToken).ConfigureAwait(false);

        // 3. Run 30 ticks so ForcedCrashLanding completes touchdown, then inspect & clean up
        await ctx.Game.RunForTicksAsync(30, null, cancellationToken).ConfigureAwait(false);

        var crashCheck = await ctx.MainThread.InvokeAsync(() =>
        {
            var map = Find.CurrentMap!;
            var fl = phase1.rig.FlightLocomotion;
            var res = new
            {
                severedRotorCount = phase1.severedRotorCount,
                liftAfterSever = fl?.LiftEfficiency ?? -1f,
                canMaintainLiftAfterSever = fl?.CanMaintainLift ?? true,
                forcedCrashCount = fl?.ForcedCrashCount ?? 0,
                flightStateAfterCrash = fl?.FlightState.ToString() ?? "null",
                altitude01AfterCrash = fl?.CurrentAltitude01 ?? -1f,
                isCurrentlyAirborneAfterCrash = phase1.rig.IsCurrentlyAirborne,
                isImmuneToGroundMeleeAfterCrash = phase1.rig.IsImmuneToGroundMeleeNow
            };

            // Clean up temporary verification entities
            if (phase1.gunship.Spawned) phase1.gunship.DeSpawn(DestroyMode.Vanish);
            if (phase1.meleePawn.Spawned) phase1.meleePawn.DeSpawn(DestroyMode.Vanish);
            if (phase1.rangedPawn.Spawned) phase1.rangedPawn.DeSpawn(DestroyMode.Vanish);
            if (!phase1.wall.Destroyed) phase1.wall.Destroy(DestroyMode.Vanish);
            if (phase1.prevTerrain != null) map.terrainGrid.SetTerrain(phase1.waterCell, phase1.prevTerrain);
            GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
            return res;
        }, cancellationToken).ConfigureAwait(false);

        return new
        {
            signedPngCount = signedCount,
            rigAuditorPassed = rigReports.Count > 0 && rigReports.All(r => r.Passed),
            rigAuditorFailures = rigReports.SelectMany(r => r.Failures).ToArray(),
            injectCommitted = injectReport.Committed,
            injectErrors = injectReport.Errors.Select(e => new { e.Code, e.Subject, e.Detail }).ToArray(),
            configErrors = phase1.configErrors,
            mechanoid = phase1.mechanoid,
            flightAndVehicle = phase1.flightAndVehicle,
            rigGeometry = phase1.rigGeometry,
            groundMeleeImmunity = phase1.groundMeleeImmunity,
            zeroLiftCrashLanding = crashCheck
        };
    }

    [Tool("gcfagent/def_state", Description = "Read database state of an injected creature: race, kind, body, rig, clips, corpse, textures.")]
    public Task<object> DefState(IRimBridgeContext ctx, string raceDefName, CancellationToken cancellationToken) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var race = DefDatabase<ThingDef>.GetNamedSilentFail(raceDefName);
            var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(raceDefName);
            var corpse = DefDatabase<ThingDef>.GetNamedSilentFail("Corpse_" + raceDefName);
            var rig = race?.comps?.OfType<CompProperties_GiantRig>().FirstOrDefault()?.rigDef;
            var things = DefDatabase<ThingDef>.AllDefsListForReading;
            object Describe(Def? def) => def == null ? new { exists = false } : new
            {
                exists = true, def.defName, def.shortHash, def.index, def.generated,
                pack = def.modContentPack?.PackageIdPlayerFacing, file = def.fileName
            };
            return new
            {
                race = Describe(race),
                raceShortHashOwners = race == null ? 0 : things.Count(d => d.shortHash == race.shortHash),
                raceFoundByShortHash = race != null && ReferenceEquals(DefDatabase<ThingDef>.GetByShortHash(race.shortHash), race),
                raceIndexValid = race != null && race.index < things.Count && ReferenceEquals(things[race.index], race),
                kind = Describe(kind),
                kindRaceLinked = kind != null && ReferenceEquals(kind.race, race),
                body = Describe(race?.race?.body),
                bodyParts = race?.race?.body?.AllParts.Count ?? 0,
                meat = race?.race?.meatDef?.defName,
                corpse = Describe(corpse),
                corpseLinked = corpse != null && ReferenceEquals(race?.race?.corpseDef, corpse) && ReferenceEquals(corpse.race, race?.race),
                corpseShortHashOwners = corpse == null ? 0 : things.Count(d => d.shortHash == corpse.shortHash),
                corpseCategories = corpse?.thingCategories?.Select(c => new
                {
                    c.defName, listed = c.childThingDefs.Contains(corpse), cached = c.ContainedInThisOrDescendant(corpse)
                }).ToArray(),
                corpseUnderRoot = corpse != null && ThingCategoryDefOf.Root.ContainedInThisOrDescendant(corpse),
                rig = Describe(rig),
                rigNodes = rig?.SortedNodes.Length ?? 0,
                clips = rig == null ? null : new
                {
                    walk = rig.walkAnim?.defName, downed = rig.downedAnim?.defName, death = rig.deathAnim?.defName,
                    melee = rig.meleeAttackAnims?.Select(c => new { c.defName, c.shortHash }).ToArray()
                },
                textures = rig?.SortedNodes.Where(n => !string.IsNullOrEmpty(n.texPath)).Select(n => new
                {
                    n.nodeId, n.texPath,
                    found = ContentFinder<Texture2D>.Get(n.texPath, false) != null,
                    mask = GiantAlphaMaskCache.GetOrLoad(n.texPath, n.alphaThreshold) != null
                }).ToArray(),
                icon = race?.uiIcon != null && race.uiIcon != BaseContent.BadTex,
                spawnedPawns = Find.CurrentMap?.mapPawns.AllPawnsSpawned
                    .Where(p => string.Equals(p.def?.defName, raceDefName, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.ThingID)
                    .ToArray() ?? Array.Empty<string>()
            };
        }, cancellationToken);

    [Tool("gcfagent/spawn", Description = "Spawn one pawn of a kind on the current map and return its id and rig state.")]
    public Task<object> Spawn(IRimBridgeContext ctx, string kindDefName, int x, int z, CancellationToken cancellationToken) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var cell = (x >= 0 && z >= 0) ? new IntVec3(x, 0, z) : Find.CurrentMap.Center;
            var pawn = InjectedCreatureSpawner.Spawn(kindDefName, cell);
            return pawn == null ? new { spawned = false } : State(pawn);
        }, cancellationToken);

    [Tool("gcfagent/pawn_state", Description = "Read the live state of a spawned creature or of its corpse.")]
    public Task<object> PawnState(IRimBridgeContext ctx, string thingId, CancellationToken cancellationToken) =>
        ctx.MainThread.InvokeAsync(() => Locate(thingId), cancellationToken);

    [Tool("gcfagent/pawn_action", Description = "Apply one action (goto, attack, sever, damage_part, damage, kill) to a spawned creature, let the game run, and return its state.")]
    public async Task<object> PawnAction(IRimBridgeContext ctx, string thingId, string action, string argument = "", int x = 0, int z = 0,
        int ticks = 0, CancellationToken cancellationToken = default)
    {
        var applied = await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var pawn = FindPawn(thingId);
            if (pawn == null) return new { error = "pawn_not_found" };
            var comp = GiantFastLookup.TryGetRig(pawn);
            switch (action)
            {
                case "goto":
                    pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(x, 0, z)), JobCondition.InterruptForced);
                    return new { started = pawn.CurJobDef?.defName };
                case "attack":
                    return new { triggered = comp?.TriggerAttackAnimation(argument) ?? false, clip = comp?.ActiveClip?.defName };
                case "damage_part":
                {
                    var dPart = pawn.health.hediffSet.GetNotMissingParts().FirstOrDefault(p =>
                        string.Equals(p.untranslatedCustomLabel, argument, StringComparison.OrdinalIgnoreCase) || p.def.defName == argument);
                    if (dPart == null) return new { error = "part_not_found" };
                    var injury = HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed("Cut"), pawn, dPart);
                    injury.Severity = 4f;
                    pawn.health.AddHediff(injury, dPart);
                    return new { damagedPart = dPart.Label, partIndex = dPart.Index };
                }
                case "sever":
                    var part = pawn.health.hediffSet.GetNotMissingParts().FirstOrDefault(p =>
                        string.Equals(p.untranslatedCustomLabel, argument, StringComparison.OrdinalIgnoreCase) || p.def.defName == argument);
                    if (part == null) return new { error = "part_not_found" };
                    pawn.health.AddHediff(HediffDefOf.MissingBodyPart, part);
                    comp?.RebuildFootprintNow(notifyMap: true);
                    return new { severed = part.Label };
                case "damage":
                    var result = pawn.TakeDamage(new DamageInfo(DamageDefOf.Bullet, x > 0 ? x : 30));
                    return new { dealt = result.totalDamageDealt };
                case "kill":
                    pawn.Kill(null);
                    return new { dead = pawn.Dead };
                default:
                    return new { error = "unknown_action" };
            }
        }, cancellationToken).ConfigureAwait(false);

        if (ticks > 0)
        {
            await ctx.Game.RunForTicksAsync(ticks, null, cancellationToken).ConfigureAwait(false);
        }
        var state = await ctx.MainThread.InvokeAsync(() => Locate(thingId), cancellationToken).ConfigureAwait(false);
        return new { applied, state };
    }

    [Tool("gcfagent/setup_heli_showcase", Description = "Stage an active combat & flight showcase around a spawned helicopter for GIF recording.")]
    public Task<object> SetupHeliShowcase(IRimBridgeContext ctx, string heliThingId, CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("No active map.");
            var heli = FindPawn(heliThingId) ?? throw new InvalidOperationException($"Heli not found: {heliThingId}");
            map.weatherManager?.TransitionTo(WeatherDefOf.Clear);
            CleanseUiForCapture(hideHud: true);

            var origin = new IntVec3(
                Mathf.Clamp(heli.Position.x - 12, 35, map.Size.x - 45),
                0,
                Mathf.Clamp(heli.Position.z, 35, map.Size.z - 35));

            // Clear a clean open sunlit desert battlefield rectangle so no mountain wall/roof hides targets or projectiles!
            for (int dx = -14; dx <= 28; dx++)
            {
                for (int dz = -16; dz <= 16; dz++)
                {
                    var cell = new IntVec3(origin.x + dx, 0, origin.z + dz);
                    if (!cell.InBounds(map)) continue;
                    map.roofGrid?.SetRoof(cell, null);
                    map.fogGrid?.Unfog(cell);
                    foreach (var t in cell.GetThingList(map).ToList())
                    {
                        if (t is not Pawn && !t.Destroyed && (t.def.building?.isNaturalRock == true || t.def.passability == Traversability.Impassable))
                        {
                            t.Destroy(DestroyMode.Vanish);
                        }
                    }
                }
            }

            heli.Position = origin;
            heli.Notify_Teleported(true, true);
            GiantFastLookup.TryGetRig(heli)?.RebuildFootprintNow(notifyMap: true);

            var hostFac = Faction.OfMechanoids ?? Find.FactionManager.FirstFactionOfDef(FactionDefOf.AncientsHostile);
            var spawnedTargets = new List<string>();
            var offsets = new[]
            {
                new IntVec3(13, 0, -2),
                new IntVec3(15, 0, -6),
                new IntVec3(16, 0, 2),
                new IntVec3(11, 0, -5)
            };
            var kinds = new[] { "Mech_CentipedeBlaster", "Mech_Lancer", "Mech_Pikeman", "Mech_Scyther" };
            for (int i = 0; i < offsets.Length; i++)
            {
                var c = new IntVec3(
                    Mathf.Clamp(origin.x + offsets[i].x, 15, map.Size.x - 15),
                    0,
                    Mathf.Clamp(origin.z + offsets[i].z, 15, map.Size.z - 15));
                var kd = DefDatabase<PawnKindDef>.GetNamedSilentFail(kinds[i]) ?? PawnKindDefOf.Thrumbo;
                var p = PawnGenerator.GeneratePawn(kd, hostFac, map.Tile);
                GenSpawn.Spawn(p, c, map, Rot4.West, WipeMode.Vanish);
                if (hostFac == null)
                {
                    p.mindState?.mentalStateHandler?.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true);
                }
                if (p.mindState != null) p.mindState.enemyTarget = heli;
                spawnedTargets.Add(p.ThingID);
            }

            // Issue a flanking flight waypoint so the helicopter banks, flies across the open desert, and fires rockets + 360 nose turret simultaneously!
            var wp = new IntVec3(Mathf.Clamp(origin.x + 8, 20, map.Size.x - 20), 0, Mathf.Clamp(origin.z + 8, 20, map.Size.z - 20));
            heli.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, wp), JobCondition.InterruptForced);

            return new
            {
                ok = true,
                heliId = heli.ThingID,
                heliStart = origin.ToString(),
                waypoint = wp.ToString(),
                targets = spawnedTargets.ToArray()
            };
        }, cancellationToken);

    private static int CleanseUiForCapture(bool hideHud)
    {
        int closed = 0;
        try
        {
            LudeonTK.EditWindow_Log.wantsToOpen = false;
        }
        catch { }
        try
        {
            Prefs.AutomaticPauseMode = AutomaticPauseMode.Never;
        }
        catch { }
        if (Find.WindowStack != null)
        {
            var targets = Find.WindowStack.Windows
                .Where(w => w != null && (
                    w is LudeonTK.EditWindow_Log ||
                    w is Dialog_AgentChat ||
                    w.GetType().Name.StartsWith("EditWindow_", StringComparison.Ordinal) ||
                    w.GetType().Name.StartsWith("Dialog_", StringComparison.Ordinal)))
                .ToList();
            foreach (var w in targets)
            {
                if (Find.WindowStack.TryRemove(w, doCloseSound: false))
                {
                    closed++;
                }
            }
        }
        if (Find.UIRoot?.screenshotMode != null)
        {
            Find.UIRoot.screenshotMode.Active = hideHud;
        }
        Find.Selector?.ClearSelection();
        return closed;
    }

    [Tool("gcfagent/prepare_clean_capture", Description = "Close EditWindow_Log and dialogs, suppress debug log auto-open, and toggle HUD screenshotMode.")]
    public Task<object> PrepareCleanCapture(IRimBridgeContext ctx, bool hideHud = true, CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            int closed = CleanseUiForCapture(hideHud);
            return new
            {
                ok = true,
                closedWindows = closed,
                hideHud = Find.UIRoot?.screenshotMode?.Active ?? false
            };
        }, cancellationToken);

    [Tool("gcfagent/setup_combat_arena", Description = "Prepare a sunlit clear combat arena around map center with armed defenders and two hostile custom giants for live combat GIF capture.")]
    public Task<object> SetupCombatArena(IRimBridgeContext ctx, string kindDefName, CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap;
            if (map == null) return new { ok = false, error = "no_map" };
            var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(kindDefName);
            if (kind == null) return new { ok = false, error = "kind_not_found" };

            CleanseUiForCapture(hideHud: false);
            if (Find.TickManager != null)
            {
                float curHour = GenLocalDate.HourFloat(map);
                int deltaToNoon = Mathf.RoundToInt((12f - curHour) * GenDate.TicksPerHour);
                int targetTick = Find.TickManager.TicksGame + deltaToNoon;
                while (targetTick < GenDate.TicksPerDay) targetTick += GenDate.TicksPerDay;
                Find.TickManager.DebugSetTicksGame(targetTick);
            }
            if (map.weatherManager != null && WeatherDefOf.Clear != null)
            {
                map.weatherManager.curWeather = WeatherDefOf.Clear;
                map.weatherManager.lastWeather = WeatherDefOf.Clear;
                map.weatherManager.curWeatherAge = 4000;
            }
            map.skyManager?.ForceSetCurSkyGlow(1f);
            Find.Selector?.ClearSelection();

            var oldPawns = map.mapPawns.AllPawnsSpawned.ToList();
            var center = map.Center;
            int cx = center.x;
            int cz = center.z;

            var rect = CellRect.CenteredOn(center, 44, 44).ClipInsideMap(map);
            var scratch = new List<Thing>(16);
            foreach (var c in rect)
            {
                map.roofGrid?.SetRoof(c, null);
                scratch.Clear();
                scratch.AddRange(c.GetThingList(map));
                for (int i = 0; i < scratch.Count; i++)
                {
                    if (scratch[i] is not Pawn && !scratch[i].Destroyed)
                    {
                        if (scratch[i].def.destroyable)
                        {
                            scratch[i].Destroy(DestroyMode.Vanish);
                        }
                        else if (scratch[i].Spawned)
                        {
                            scratch[i].DeSpawn(DestroyMode.Vanish);
                        }
                    }
                }
                if (map.terrainGrid != null)
                {
                    if (map.terrainGrid.TempTerrainAt(c) != null)
                        map.terrainGrid.RemoveTempTerrain(c, doLeavings: false);
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.fogGrid?.Unfog(c);
            }

            // Build sandbag cover line at cz + 8
            var sandbagsDef = ThingDefOf.Sandbags;
            var sandbagStuff = sandbagsDef != null && sandbagsDef.MadeFromStuff ? GenStuff.DefaultStuffFor(sandbagsDef) : null;
            if (sandbagsDef != null)
            {
                for (int x = cx - 9; x <= cx + 9; x++)
                {
                    if (x % 4 == 0) continue;
                    var sc = new IntVec3(x, 0, cz + 8);
                    if (sc.InBounds(map))
                    {
                        var sb = ThingMaker.MakeThing(sandbagsDef, sandbagStuff);
                        GenSpawn.Spawn(sb, sc, map, Rot4.North, WipeMode.Vanish);
                    }
                }
            }

            var chargeRifleDef = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_ChargeRifle")
                                 ?? DefDatabase<ThingDef>.GetNamedSilentFail("Gun_AssaultRifle");
            var monoSwordDef = DefDatabase<ThingDef>.GetNamedSilentFail("MeleeWeapon_MonoSword")
                               ?? DefDatabase<ThingDef>.GetNamedSilentFail("MeleeWeapon_LongSword");
            var marineArmorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_PowerArmor");

            var defenders = new List<Pawn>();
            int[] gunnerX = [cx - 8, cx - 5, cx - 2, cx + 1, cx + 4, cx + 7];
            foreach (var gx in gunnerX)
            {
                var p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, new IntVec3(gx, 0, cz + 9), map, Rot4.South, WipeMode.Vanish);
                p.equipment?.DestroyAllEquipment();
                if (chargeRifleDef != null)
                {
                    var gun = (ThingWithComps)ThingMaker.MakeThing(chargeRifleDef);
                    p.equipment?.AddEquipment(gun);
                }
                if (marineArmorDef != null && p.apparel != null)
                {
                    var armor = (Apparel)ThingMaker.MakeThing(marineArmorDef);
                    p.apparel.Wear(armor, dropReplacedApparel: false);
                }
                if (p.skills != null)
                {
                    p.skills.GetSkill(SkillDefOf.Shooting).Level = 15;
                }
                if (p.playerSettings != null) p.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
                if (p.drafter != null)
                {
                    p.drafter.Drafted = true;
                    p.drafter.FireAtWill = true;
                }
                defenders.Add(p);
            }

            int[] meleeX = [cx - 3, cx + 1, cx + 5];
            foreach (var mx in meleeX)
            {
                var mp = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(mp, new IntVec3(mx, 0, cz + 4), map, Rot4.South, WipeMode.Vanish);
                mp.equipment?.DestroyAllEquipment();
                if (monoSwordDef != null)
                {
                    var swStuff = monoSwordDef.MadeFromStuff ? ThingDefOf.Plasteel : null;
                    var sw = (ThingWithComps)ThingMaker.MakeThing(monoSwordDef, swStuff);
                    mp.equipment?.AddEquipment(sw);
                }
                if (marineArmorDef != null && mp.apparel != null)
                {
                    var armor = (Apparel)ThingMaker.MakeThing(marineArmorDef);
                    mp.apparel.Wear(armor, dropReplacedApparel: false);
                }
                if (mp.skills != null) mp.skills.GetSkill(SkillDefOf.Melee).Level = 16;
                if (mp.playerSettings != null) mp.playerSettings.hostilityResponse = HostilityResponseMode.Attack;
                if (mp.drafter != null) mp.drafter.Drafted = false;
                defenders.Add(mp);
            }

            foreach (var old in oldPawns)
            {
                if (old != null && old.Spawned && !old.Destroyed)
                {
                    old.DeSpawn(DestroyMode.Vanish);
                }
            }

            var hostileFac = (kind.race?.race?.IsMechanoid == true ? Faction.OfMechanoids : null)
                             ?? Find.FactionManager.FirstFactionOfDef(FactionDefOf.AncientsHostile);
            Pawn SpawnGiantAt(IntVec3 pos)
            {
                var gp = PawnGenerator.GeneratePawn(kind, hostileFac);
                GenSpawn.Spawn(gp, pos, map, Rot4.North, WipeMode.Vanish);
                gp.Rotation = Rot4.North;
                if (!gp.RaceProps.IsMechanoid)
                {
                    gp.mindState?.mentalStateHandler?.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forceWake: true);
                }
                GiantFastLookup.TryGetRig(gp)?.RebuildFootprintNow(notifyMap: true);
                return gp;
            }

            var g1 = SpawnGiantAt(new IntVec3(cx - 5, 0, cz - 7));
            var g2 = SpawnGiantAt(new IntVec3(cx + 5, 0, cz - 6));
            GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
            CleanseUiForCapture(hideHud: true);

            return new
            {
                ok = true,
                centerX = cx,
                centerZ = cz,
                primaryId = g1.ThingID,
                secondaryId = g2.ThingID,
                defenderCount = defenders.Count
            };
        }, cancellationToken);

    [Tool("gcfagent/stage_rathalos_live_test", Description = "Stage a wide sunlit Soil arena with a 3-rider player-faction GCF_Rathalos and long-range hostile target clusters for live aerial fireball, dragon charge, heading, silhouette, and health-bar verification.")]
    public Task<object> StageRathalosLiveTest(IRimBridgeContext ctx, CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap;
            if (map == null) return new { ok = false, error = "no_map" };
            var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("GCF_Rathalos");
            var race = DefDatabase<ThingDef>.GetNamedSilentFail("GCF_Rathalos");
            if (kind == null || race == null) return new { ok = false, error = "rathalos_not_found" };

            var rigDef = race.comps?.OfType<CompProperties_GiantRig>().FirstOrDefault()?.rigDef;
            var configErrors = new List<string>();
            foreach (var e in race.ConfigErrors()) configErrors.Add($"ThingDef(GCF_Rathalos): {e}");
            foreach (var e in kind.ConfigErrors()) configErrors.Add($"PawnKindDef(GCF_Rathalos): {e}");
            if (rigDef != null)
            {
                foreach (var e in rigDef.ConfigErrors()) configErrors.Add($"GiantRigDef({rigDef.defName}): {e}");
            }
            string[] abDefs =
            [
                "GCF_Ability_Rathalos_DragonCharge",
                "GCF_Ability_Rathalos_FireballBreath",
                "GCF_Ability_Rathalos_FireballLiftoff",
                "GCF_Ability_Rathalos_TalonStrike",
                "GCF_Ability_Rathalos_ApexRoar"
            ];
            foreach (var abName in abDefs)
            {
                var abDef = DefDatabase<AbilityDef>.GetNamedSilentFail(abName);
                if (abDef == null)
                {
                    configErrors.Add($"Missing AbilityDef: {abName}");
                }
                else
                {
                    foreach (var e in abDef.ConfigErrors()) configErrors.Add($"AbilityDef({abName}): {e}");
                }
            }

            CleanseUiForCapture(hideHud: true);
            if (Find.TickManager != null)
            {
                float curHour = GenLocalDate.HourFloat(map);
                int deltaToNoon = Mathf.RoundToInt((12f - curHour) * GenDate.TicksPerHour);
                int targetTick = Find.TickManager.TicksGame + deltaToNoon;
                while (targetTick < GenDate.TicksPerDay) targetTick += GenDate.TicksPerDay;
                Find.TickManager.DebugSetTicksGame(targetTick);
            }
            if (map.weatherManager != null && WeatherDefOf.Clear != null)
            {
                map.weatherManager.curWeather = WeatherDefOf.Clear;
                map.weatherManager.lastWeather = WeatherDefOf.Clear;
                map.weatherManager.curWeatherAge = 4000;
            }
            map.skyManager?.ForceSetCurSkyGlow(1f);

            var oldPawns = map.mapPawns.AllPawnsSpawned.ToList();
            var center = map.Center;
            int cx = center.x;
            int cz = center.z;
            var arenaRect = CellRect.CenteredOn(center, 116, 56).ClipInsideMap(map);
            var scratch = new List<Thing>(16);
            foreach (var c in arenaRect)
            {
                map.roofGrid?.SetRoof(c, null);
                scratch.Clear();
                scratch.AddRange(c.GetThingList(map));
                for (int i = 0; i < scratch.Count; i++)
                {
                    if (scratch[i] is not Pawn && !scratch[i].Destroyed)
                    {
                        if (scratch[i].def.destroyable) scratch[i].Destroy(DestroyMode.Vanish);
                        else if (scratch[i].Spawned) scratch[i].DeSpawn(DestroyMode.Vanish);
                    }
                }
                if (map.terrainGrid != null)
                {
                    if (map.terrainGrid.TempTerrainAt(c) != null)
                        map.terrainGrid.RemoveTempTerrain(c, doLeavings: false);
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.fogGrid?.Unfog(c);
            }
            foreach (var old in oldPawns)
            {
                if (old != null && old.Spawned && !old.Destroyed)
                {
                    old.DeSpawn(DestroyMode.Vanish);
                }
            }

            var rathalosPos = new IntVec3(cx - 24, 0, cz);
            var rathalos = PawnGenerator.GeneratePawn(kind, Faction.OfPlayer);
            GenSpawn.Spawn(rathalos, rathalosPos, map, Rot4.East, WipeMode.Vanish);
            rathalos.Rotation = Rot4.East;
            var rig = GiantFastLookup.TryGetRig(rathalos)!;
            rig.RebuildFootprintNow(notifyMap: true);

            var crewComp = GiantFastLookup.TryGetCockpit(rathalos);
            var riderIds = new List<string>();
            Color[] riderColors =
            [
                new Color(0.92f, 0.78f, 0.28f),
                new Color(0.26f, 0.62f, 0.88f),
                new Color(0.86f, 0.34f, 0.30f)
            ];
            var armorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_PowerArmor")
                           ?? DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_FlakJacket");
            for (int seatIdx = 0; seatIdx < 3; seatIdx++)
            {
                var rider = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, mustBeCapableOfViolence: true));
                GenSpawn.Spawn(rider, rathalosPos + new IntVec3(-2, 0, seatIdx - 1), map, Rot4.East, WipeMode.Vanish);
                if (armorDef != null && rider.apparel != null)
                {
                    var armor = (Apparel)ThingMaker.MakeThing(armorDef);
                    armor.SetColor(riderColors[seatIdx], reportFailure: false);
                    rider.apparel.Wear(armor, dropReplacedApparel: false);
                }
                bool boarded = crewComp != null && crewComp.TryAcceptPawn(rider, seatIdx);
                riderIds.Add($"{rider.ThingID}:seat{seatIdx}:{boarded}");
            }

            if (rathalos.drafter != null)
            {
                rathalos.drafter.Drafted = true;
            }

            var hostileFac = Find.FactionManager.FirstFactionOfDef(FactionDefOf.AncientsHostile) ?? Faction.OfMechanoids;
            var sandbagsDef = ThingDefOf.Sandbags;
            var sandbagStuff = sandbagsDef != null && sandbagsDef.MadeFromStuff ? GenStuff.DefaultStuffFor(sandbagsDef) : null;

            // Cluster A: 48 cells East of Rathalos (cx + 24, cz) for 3x-range Aerial Fireball test (old max range was 24.5!)
            var fireballTargetCell = new IntVec3(cx + 24, 0, cz);
            if (sandbagsDef != null)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    var sbCell = new IntVec3(fireballTargetCell.x - 2, 0, fireballTargetCell.z + dz);
                    if (sbCell.InBounds(map))
                    {
                        GenSpawn.Spawn(ThingMaker.MakeThing(sandbagsDef, sandbagStuff), sbCell, map, Rot4.West, WipeMode.Vanish);
                    }
                }
            }
            var fireballTargetIds = new List<string>();
            foreach (var off in new[] { new IntVec3(0, 0, -1), new IntVec3(0, 0, 0), new IntVec3(0, 0, 1), new IntVec3(1, 0, 0) })
            {
                var hp = PawnGenerator.GeneratePawn(PawnKindDefOf.AncientSoldier, hostileFac);
                GenSpawn.Spawn(hp, fireballTargetCell + off, map, Rot4.West, WipeMode.Vanish);
                hp.equipment?.DestroyAllEquipment();
                hp.jobs?.StartJob(JobMaker.MakeJob(JobDefOf.Wait_Combat, 12000), JobCondition.InterruptForced);
                fireballTargetIds.Add(hp.ThingID);
            }

            // Cluster B: 36 cells along charge corridor for 3x-range Dragon Charge test (old max range was 17.9!)
            var chargeTargetIds = new List<string>();
            foreach (var off in new[] { new IntVec3(14, 0, -7), new IntVec3(20, 0, -7), new IntVec3(26, 0, -8), new IntVec3(32, 0, -8) })
            {
                var cp = PawnGenerator.GeneratePawn(PawnKindDefOf.AncientSoldier, hostileFac);
                var cPos = new IntVec3(cx - 18, 0, cz) + off;
                GenSpawn.Spawn(cp, cPos, map, Rot4.West, WipeMode.Vanish);
                cp.equipment?.DestroyAllEquipment();
                cp.jobs?.StartJob(JobMaker.MakeJob(JobDefOf.Wait_Combat, 12000), JobCondition.InterruptForced);
                chargeTargetIds.Add(cp.ThingID);
            }

            bool playerBossBarShown = GiantBossHealthBarHUD.ShouldDisplayBossHealthBar(rathalos, rig);

            // Temporary hostile check pawn (immediately despawned) to confirm hostile boss bar still returns true
            var tmpHostile = PawnGenerator.GeneratePawn(kind, hostileFac);
            GenSpawn.Spawn(tmpHostile, new IntVec3(cx, 0, cz + 20), map, Rot4.South, WipeMode.Vanish);
            var tmpHostileRig = GiantFastLookup.TryGetRig(tmpHostile)!;
            bool hostileBossBarShown = GiantBossHealthBarHUD.ShouldDisplayBossHealthBar(tmpHostile, tmpHostileRig);
            tmpHostile.DeSpawn(DestroyMode.Vanish);

            GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
            CleanseUiForCapture(hideHud: true);

            var dragonChargeDef = DefDatabase<AbilityDef>.GetNamed("GCF_Ability_Rathalos_DragonCharge");
            var fireballDef = DefDatabase<AbilityDef>.GetNamed("GCF_Ability_Rathalos_FireballBreath");
            float dcSpeed = dragonChargeDef.comps
                .Select(c => c.GetType().GetField("chargeSpeedCellsPerSecond")?.GetValue(c) as float?)
                .FirstOrDefault(v => v.HasValue) ?? 0f;
            float fbSpeed = fireballDef.comps
                .Select(c => c.GetType().GetField("fireballSpeedCellsPerSecond")?.GetValue(c) as float?)
                .FirstOrDefault(v => v.HasValue) ?? 0f;

            return new
            {
                ok = true,
                centerX = cx,
                centerZ = cz,
                rathalosId = rathalos.ThingID,
                rathalosStartX = rathalosPos.x,
                rathalosStartZ = rathalosPos.z,
                fireballTargetX = fireballTargetCell.x,
                fireballTargetZ = fireballTargetCell.z,
                crewCount = crewComp?.CrewCount ?? 0,
                seatCount = crewComp?.SeatCount ?? 0,
                riders = riderIds.ToArray(),
                playerBossBarShown,
                hostileBossBarShown,
                configErrors = configErrors.ToArray(),
                dragonChargeRange = dragonChargeDef.verbProperties.range,
                dragonChargeSpeed = dcSpeed,
                fireballRange = fireballDef.verbProperties.range,
                fireballSpeed = fbSpeed,
                fireballTargetIds = fireballTargetIds.ToArray(),
                chargeTargetIds = chargeTargetIds.ToArray()
            };
        }, cancellationToken);

    [Tool("gcfagent/giant_live_op", Description = "Execute live operations (stage_clean_arena, takeoff, land, draft_goto, cast_ability, inspect) on any spawned giant creature.")]
    public Task<object> GiantLiveOp(
        IRimBridgeContext ctx,
        string op,
        string thingId = "",
        string abilityDefName = "",
        int x = 0,
        int z = 0,
        bool hideHud = true,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap;
            if (map == null) return new { ok = false, error = "no_map" };

            if (string.Equals(op, "stage_clean_arena", StringComparison.OrdinalIgnoreCase))
            {
                CleanseUiForCapture(hideHud);
                if (Find.TickManager != null)
                {
                    float curHour = GenLocalDate.HourFloat(map);
                    int deltaToNoon = Mathf.RoundToInt((12f - curHour) * GenDate.TicksPerHour);
                    int targetTick = Find.TickManager.TicksGame + deltaToNoon;
                    while (targetTick < GenDate.TicksPerDay) targetTick += GenDate.TicksPerDay;
                    Find.TickManager.DebugSetTicksGame(targetTick);
                }
                if (map.weatherManager != null && WeatherDefOf.Clear != null)
                {
                    map.weatherManager.curWeather = WeatherDefOf.Clear;
                    map.weatherManager.lastWeather = WeatherDefOf.Clear;
                    map.weatherManager.curWeatherAge = 4000;
                }
                map.skyManager?.ForceSetCurSkyGlow(1f);

                var oldPawns = map.mapPawns.AllPawnsSpawned.ToList();
                var center = map.Center;
                var rect = CellRect.CenteredOn(center, 90, 54).ClipInsideMap(map);
                var scratch = new List<Thing>(16);
                foreach (var c in rect)
                {
                    map.roofGrid?.SetRoof(c, null);
                    scratch.Clear();
                    scratch.AddRange(c.GetThingList(map));
                    for (int i = 0; i < scratch.Count; i++)
                    {
                        if (scratch[i] is not Pawn && !scratch[i].Destroyed)
                        {
                            if (scratch[i].def.destroyable) scratch[i].Destroy(DestroyMode.Vanish);
                            else if (scratch[i].Spawned) scratch[i].DeSpawn(DestroyMode.Vanish);
                        }
                    }
                    if (map.terrainGrid != null)
                    {
                        if (map.terrainGrid.TempTerrainAt(c) != null)
                            map.terrainGrid.RemoveTempTerrain(c, doLeavings: false);
                        map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                    }
                    map.fogGrid?.Unfog(c);
                }
                foreach (var old in oldPawns)
                {
                    if (old != null && old.Spawned && !old.Destroyed) old.DeSpawn(DestroyMode.Vanish);
                }
                GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
                CleanseUiForCapture(hideHud);
                return new { ok = true, centerX = center.x, centerZ = center.z };
            }

            var pawn = FindPawn(thingId);
            if (pawn == null) return new { ok = false, error = "pawn_not_found", thingId };
            var rig = GiantFastLookup.TryGetRig(pawn);
            var fl = rig?.FlightLocomotion;
            var crew = GiantFastLookup.TryGetCockpit(pawn);

            object BuildInspect() => new
            {
                ok = true,
                thingId = pawn.ThingID,
                def = pawn.def.defName,
                posX = pawn.Position.x,
                posZ = pawn.Position.z,
                drawX = pawn.DrawPos.x,
                drawZ = pawn.DrawPos.z,
                rotation = pawn.Rotation.AsInt,
                headingDeg = rig?.CurrentHeadingDeg ?? 0f,
                moving = pawn.pather?.Moving ?? false,
                animState = rig?.CurrentAnimState.ToString() ?? "None",
                activeClip = rig?.ActiveClip?.defName ?? "null",
                isAirborne = rig?.IsCurrentlyAirborne ?? false,
                flightState = fl?.FlightState.ToString() ?? "None",
                altitude01 = fl?.CurrentAltitude01 ?? 0f,
                hasDisplacement = GiantMotionOverrideRegistry.HasActiveDisplacement(pawn),
                crewCount = crew?.CrewCount ?? 0,
                curJob = pawn.CurJobDef?.defName ?? "null"
            };

            switch (op.ToLowerInvariant())
            {
                case "inspect":
                    CleanseUiForCapture(hideHud);
                    return BuildInspect();

                case "takeoff":
                {
                    string reason = "";
                    bool started = fl != null && fl.TryCommandTakeoff(rig!, out reason);
                    CleanseUiForCapture(hideHud);
                    return new { ok = started, reason, state = BuildInspect() };
                }

                case "land":
                {
                    string reason = "";
                    bool started = fl != null && fl.TryCommandLanding(rig!, out reason);
                    CleanseUiForCapture(hideHud);
                    return new { ok = started, reason, state = BuildInspect() };
                }

                case "draft_goto":
                {
                    if (pawn.drafter != null && !pawn.drafter.Drafted)
                    {
                        pawn.drafter.Drafted = true;
                    }
                    var job = JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(x, 0, z));
                    bool accepted = pawn.jobs?.TryTakeOrderedJob(job, JobTag.Misc) ?? false;
                    if (!accepted)
                    {
                        pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
                        accepted = true;
                    }
                    CleanseUiForCapture(hideHud);
                    return new { ok = accepted, targetX = x, targetZ = z, state = BuildInspect() };
                }

                case "cast_ability":
                {
                    var abDef = DefDatabase<AbilityDef>.GetNamedSilentFail(abilityDefName);
                    var ab = abDef != null ? pawn.abilities?.GetAbility(abDef) : null;
                    if (ab == null)
                    {
                        return new { ok = false, error = "ability_not_found", abilityDefName };
                    }
                    ab.ResetCooldown();
                    bool gizmoDisabled = ab.GizmoDisabled(out string disableReason);
                    var targetCell = new IntVec3(x, 0, z);
                    Pawn? targetPawn = targetCell.InBounds(map) ? targetCell.GetFirstPawn(map) : null;
                    LocalTargetInfo lti = targetPawn != null ? new LocalTargetInfo(targetPawn) : new LocalTargetInfo(targetCell);
                    bool verbCanHit = ab.verb.CanHitTarget(lti);
                    float dist = pawn.Position.DistanceTo(targetCell);
                    ab.QueueCastingJob(lti, LocalTargetInfo.Invalid);
                    CleanseUiForCapture(hideHud);
                    return new
                    {
                        ok = !gizmoDisabled && verbCanHit,
                        ability = ab.def.defName,
                        distanceCells = dist,
                        gizmoDisabled,
                        disableReason = disableReason ?? "",
                        verbCanHit,
                        targetHasPawn = targetPawn != null,
                        state = BuildInspect()
                    };
                }

                default:
                    return new { ok = false, error = "unknown_op", op };
            }
        }, cancellationToken);

    [Tool("gcfagent/stage_duel_or_flight_test", Description = "Stage or query live Rathalos vs Thrumbo (no-fireball) duel and combat flight cap verification.")]
    public Task<object> StageDuelOrFlightTest(
        IRimBridgeContext ctx,
        string mode,
        int thrumboCount = 1,
        string rathalosId = "",
        bool hideHud = true,
        CancellationToken cancellationToken = default) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap;
            if (map == null) return new { ok = false, error = "no_map" };

            if (string.Equals(mode, "duel_status", StringComparison.OrdinalIgnoreCase))
            {
                CleanseUiForCapture(hideHud);
                var rath = FindPawn(rathalosId)
                    ?? map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.def.defName == "GCF_Rathalos");
                if (rath == null)
                {
                    return new { ok = false, error = "rathalos_not_found" };
                }
                var rig = GiantFastLookup.TryGetRig(rath);
                var fl = rig?.FlightLocomotion;
                var pc = rig?.PartCombat;
                string cdReason = "";
                bool lockedOut = fl != null && fl.IsCombatTakeoffLockedOut(rig!, out cdReason);
                var liftoffAb = rath.abilities?.GetAbility(DefDatabase<AbilityDef>.GetNamedSilentFail("GCF_Ability_Rathalos_FireballLiftoff"));
                bool liftoffGizmoDisabled = liftoffAb != null && liftoffAb.GizmoDisabled(out _);

                var grpStats = pc?.Groups.Select(g => new
                {
                    boneId = g.Bone.nodeId,
                    strikes = g.TotalShotsOrStrikesFired,
                    phase = g.Phase.ToString()
                }).ToArray() ?? Array.Empty<object>();

                var opponents = map.mapPawns.AllPawnsSpawned
                    .Where(p => p != rath && p.Faction != Faction.OfPlayerSilentFail)
                    .Select(p => new
                    {
                        id = p.ThingID,
                        def = p.def.defName,
                        dead = p.Dead,
                        downed = p.Downed,
                        hpPct = p.health?.summaryHealth?.SummaryHealthPercent ?? 0f,
                        x = p.Position.x,
                        z = p.Position.z,
                        mental = p.MentalStateDef?.defName ?? "null",
                        job = p.CurJobDef?.defName ?? "null"
                    }).ToArray();

                var corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>()
                    .Where(c => c.InnerPawn != null && c.InnerPawn.def.defName != "GCF_Rathalos")
                    .Select(c => new { id = c.InnerPawn!.ThingID, def = c.InnerPawn.def.defName })
                    .ToArray();

                return new
                {
                    ok = true,
                    tick = Find.TickManager?.TicksGame ?? 0,
                    rathalosId = rath.ThingID,
                    rathalosDead = rath.Dead,
                    rathalosDowned = rath.Downed,
                    rathalosHpPct = rath.health?.summaryHealth?.SummaryHealthPercent ?? 0f,
                    posX = rath.Position.x,
                    posZ = rath.Position.z,
                    headingDeg = rig?.CurrentHeadingDeg ?? 0f,
                    flightState = fl?.FlightState.ToString() ?? "None",
                    altitude01 = fl?.CurrentAltitude01 ?? 0f,
                    combatAirborneTicks = fl?.CombatAirborneTicks ?? 0,
                    postLandLockoutTicks = fl?.PostLandTakeoffLockoutTicks ?? 0,
                    isCombatTakeoffLockedOut = lockedOut,
                    lockoutReason = cdReason ?? "",
                    liftoffGizmoDisabled,
                    inspectLine = fl?.GetInspectStringExtra(rig) ?? "",
                    weaponGroups = grpStats,
                    opponents,
                    corpses
                };
            }

            // Reset clean sunlit arena
            CleanseUiForCapture(hideHud);
            if (Find.TickManager != null)
            {
                float curHour = GenLocalDate.HourFloat(map);
                int deltaToNoon = Mathf.RoundToInt((12f - curHour) * GenDate.TicksPerHour);
                int targetTick = Find.TickManager.TicksGame + deltaToNoon;
                while (targetTick < GenDate.TicksPerDay) targetTick += GenDate.TicksPerDay;
                Find.TickManager.DebugSetTicksGame(targetTick);
            }
            if (map.weatherManager != null && WeatherDefOf.Clear != null)
            {
                map.weatherManager.curWeather = WeatherDefOf.Clear;
                map.weatherManager.lastWeather = WeatherDefOf.Clear;
                map.weatherManager.curWeatherAge = 4000;
            }
            map.skyManager?.ForceSetCurSkyGlow(1f);

            foreach (var old in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (old != null && old.Spawned && !old.Destroyed) old.DeSpawn(DestroyMode.Vanish);
            }
            var center = map.Center;
            var rect = CellRect.CenteredOn(center, 96, 60).ClipInsideMap(map);
            var scratch = new List<Thing>(16);
            foreach (var c in rect)
            {
                map.roofGrid?.SetRoof(c, null);
                scratch.Clear();
                scratch.AddRange(c.GetThingList(map));
                for (int i = 0; i < scratch.Count; i++)
                {
                    if (scratch[i] is not Pawn && !scratch[i].Destroyed)
                    {
                        if (scratch[i].def.destroyable) scratch[i].Destroy(DestroyMode.Vanish);
                        else if (scratch[i].Spawned) scratch[i].DeSpawn(DestroyMode.Vanish);
                    }
                }
                if (map.terrainGrid != null)
                {
                    if (map.terrainGrid.TempTerrainAt(c) != null)
                        map.terrainGrid.RemoveTempTerrain(c, doLeavings: false);
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Soil);
                }
                map.fogGrid?.Unfog(c);
            }
            foreach (var c in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).ToList())
            {
                if (!c.Destroyed) c.Destroy(DestroyMode.Vanish);
            }

            var rathKind = DefDatabase<PawnKindDef>.GetNamed("GCF_Rathalos");
            var rathPawn = PawnGenerator.GeneratePawn(rathKind, Faction.OfPlayer);
            var rathPos = new IntVec3(center.x - 10, 0, center.z);
            GenSpawn.Spawn(rathPawn, rathPos, map, Rot4.East, WipeMode.Vanish);

            if (string.Equals(mode, "no_fireball_thrumbo_duel", StringComparison.OrdinalIgnoreCase))
            {
                if (rathPawn.abilities != null)
                {
                    rathPawn.abilities.RemoveAbility(DefDatabase<AbilityDef>.GetNamed("GCF_Ability_Rathalos_FireballBreath"));
                    rathPawn.abilities.RemoveAbility(DefDatabase<AbilityDef>.GetNamed("GCF_Ability_Rathalos_FireballLiftoff"));
                }
                var rathRig = GiantFastLookup.TryGetRig(rathPawn);
                if (rathRig?.FlightLocomotion != null)
                {
                    // Keep Rathalos purely grounded for the strict pure-melee/charge vs Thrumbo test
                    rathRig.FlightLocomotion.Def.autoTakeoffInCombat = false;
                }

                var thrumboKind = DefDatabase<PawnKindDef>.GetNamed("Thrumbo");
                var thrumboIds = new List<string>();
                int count = Mathf.Clamp(thrumboCount, 1, 4);
                for (int i = 0; i < count; i++)
                {
                    var th = PawnGenerator.GeneratePawn(thrumboKind, null);
                    int zOff = (i - (count - 1) / 2) * 4;
                    var thPos = new IntVec3(center.x + 8, 0, center.z + zOff);
                    GenSpawn.Spawn(th, thPos, map, Rot4.West, WipeMode.Vanish);
                    th.mindState?.mentalStateHandler?.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true);
                    if (th.mindState != null) th.mindState.enemyTarget = rathPawn;
                    thrumboIds.Add(th.ThingID);
                }
                GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
                CleanseUiForCapture(hideHud);
                return new
                {
                    ok = true,
                    mode,
                    centerX = center.x,
                    centerZ = center.z,
                    rathalosId = rathPawn.ThingID,
                    remainingAbilities = rathPawn.abilities?.abilities.Select(a => a.def.defName).ToArray() ?? Array.Empty<string>(),
                    thrumboIds = thrumboIds.ToArray()
                };
            }

            if (string.Equals(mode, "combat_flight_cap_test", StringComparison.OrdinalIgnoreCase))
            {
                var rathRig = GiantFastLookup.TryGetRig(rathPawn);
                if (rathRig?.FlightLocomotion != null)
                {
                    rathRig.FlightLocomotion.Def.autoTakeoffInCombat = true;
                }
                var thrumboKind = DefDatabase<PawnKindDef>.GetNamed("Thrumbo");
                var th1 = PawnGenerator.GeneratePawn(thrumboKind, null);
                var th2 = PawnGenerator.GeneratePawn(thrumboKind, null);
                // Place one close (triggers FireballLiftoff!) and one further east so combat lasts > 12s across takeoff -> 9s cap -> auto-land -> grounded brawl!
                GenSpawn.Spawn(th1, new IntVec3(rathPos.x + 6, 0, rathPos.z + 1), map, Rot4.West, WipeMode.Vanish);
                GenSpawn.Spawn(th2, new IntVec3(rathPos.x + 22, 0, rathPos.z - 2), map, Rot4.West, WipeMode.Vanish);
                th1.mindState?.mentalStateHandler?.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true);
                th2.mindState?.mentalStateHandler?.TryStartMentalState(MentalStateDefOf.ManhunterPermanent, forced: true);
                if (th1.mindState != null) th1.mindState.enemyTarget = rathPawn;
                if (th2.mindState != null) th2.mindState.enemyTarget = rathPawn;

                GiantFootprintMapComponent.MarkActiveMapsPathGridsDirty();
                CleanseUiForCapture(hideHud);
                return new
                {
                    ok = true,
                    mode,
                    centerX = center.x,
                    centerZ = center.z,
                    rathalosId = rathPawn.ThingID,
                    maxCombatAirborneTicks = rathRig?.FlightLocomotion?.Def.maxCombatAirborneTicks ?? -1,
                    postLandCooldownTicks = rathRig?.FlightLocomotion?.Def.postLandTakeoffCooldownTicks ?? -1,
                    thrumboIds = new[] { th1.ThingID, th2.ThingID }
                };
            }

            return new { ok = false, error = "unknown_mode", mode };
        }, cancellationToken);

    [Tool("gcfagent/save_game", Description = "Save the current RimWorld game under the specified save name.")]
    public Task<object> SaveGame(IRimBridgeContext ctx, string saveName, CancellationToken cancellationToken) =>
        ctx.MainThread.InvokeAsync<object>(() =>
        {
            GameDataSaveLoader.SaveGame(saveName);
            var file = GenFilePaths.FilePathForSavedGame(saveName);
            return new { saved = File.Exists(file), path = file, bytes = File.Exists(file) ? new FileInfo(file).Length : 0L };
        }, cancellationToken);

    [Tool("gcfagent/load_game", Description = "Queue loading a saved RimWorld game and wait until playing state is ready.")]
    public async Task<object> LoadGame(IRimBridgeContext ctx, string saveName, CancellationToken cancellationToken)
    {
        await ctx.MainThread.InvokeAsync(() =>
        {
            GameDataSaveLoader.LoadGame(saveName);
        }, cancellationToken).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var ready = await ctx.MainThread.InvokeAsync(() =>
                !LongEventHandler.AnyEventNowOrWaiting && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null,
                cancellationToken).ConfigureAwait(false);
            if (ready) break;
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return await ctx.MainThread.InvokeAsync<object>(() => new
        {
            programState = Current.ProgramState.ToString(),
            mapId = Find.CurrentMap?.uniqueID ?? -1,
            spawnedPawns = Find.CurrentMap?.mapPawns.AllPawnsSpawned.Select(p => new { p.ThingID, def = p.def.defName }).ToArray(),
            corpses = Find.CurrentMap?.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>()
                .Select(c => new { corpseDef = c.def.defName, innerId = c.InnerPawn?.ThingID }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Pawn? FindPawn(string thingId) =>
        Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned).FirstOrDefault(p => p.ThingID == thingId);

    private static object Locate(string thingId)
    {
        var pawn = FindPawn(thingId);
        if (pawn != null) return State(pawn);
        var corpse = Find.Maps.SelectMany(m => m.listerThings.ThingsInGroup(ThingRequestGroup.Corpse)).OfType<Corpse>()
            .FirstOrDefault(c => c.InnerPawn?.ThingID == thingId);
        if (corpse == null) return new { found = false };
        var corpseComp = GiantFastLookup.TryGetRig(corpse.InnerPawn);
        return new
        {
            found = true, isCorpse = true, corpseDef = corpse.def.defName, corpseSpawned = corpse.Spawned,
            position = corpse.Position.ToString(), innerDead = corpse.InnerPawn?.Dead ?? true,
            animState = corpseComp?.CurrentAnimState.ToString(),
            clip = corpseComp?.ActiveClip?.defName,
            deathAnimProgress = corpseComp?.DeathAnimNormalizedProgress ?? 0f,
            deathAnimCompleted = corpseComp?.IsDeathAnimCompleted ?? false,
            listedByDef = corpse.Map.listerThings.ThingsOfDef(corpse.def).Count,
            storable = corpse.def.EverStorable(false)
        };
    }

    private static object State(Pawn pawn)
    {
        var comp = GiantFastLookup.TryGetRig(pawn);
        var footprint = comp?.Footprint;
        var bodyParts = pawn.RaceProps?.body?.AllParts;
        return new
        {
            found = true, isCorpse = false, thingId = pawn.ThingID, def = pawn.def.defName, kind = pawn.kindDef?.defName,
            spawned = pawn.Spawned, dead = pawn.Dead, downed = pawn.Downed, position = pawn.Position.ToString(),
            hasRig = comp != null, rig = comp?.RigDef?.defName,
            animState = comp?.CurrentAnimState.ToString(), clip = comp?.ActiveClip?.defName,
            heading = comp?.CurrentHeadingDeg ?? 0f, moving = pawn.pather?.Moving ?? false,
            hitCells = footprint?.hitCells.Count ?? 0, collisionCells = footprint?.collisionCells.Count ?? 0,
            masks = comp == null ? 0 : comp.AlphaMasksSpan.Length,
            missingParts = pawn.health.hediffSet.GetMissingPartsCommonAncestors().Select(h => h.Part?.Label).ToArray(),
            hediffs = pawn.health.hediffSet.hediffs
                .Where(h => h.Part != null)
                .Select(h => new
                {
                    hediff = h.def.defName,
                    partLabel = h.Part!.Label,
                    partCustomLabel = h.Part.untranslatedCustomLabel,
                    belongsToCurrentBody = bodyParts != null && bodyParts.Contains(h.Part)
                }).ToArray(),
            healthSummary = pawn.health.summaryHealth.SummaryHealthPercent,
            listedByDef = pawn.Map?.listerThings.ThingsOfDef(pawn.def).Count ?? 0,
            job = pawn.CurJobDef?.defName
        };
    }
}

internal static class ModWriteHelper
{
    public static void WriteGcfSettingsSafe()
    {
        var mod = LoadedModManager.GetMod<GiantCreatureFrameworkMod>();
        mod?.WriteSettings();
    }
}
