using System.Text.Json;
using Verse;

namespace GiantCreatureFramework.Agent;

/// <summary>Save-stable conversation identity; no credentials are stored in game saves.</summary>
public sealed class AuthoringSessionGameComponent : GameComponent
{
    private string? _scopeId = Guid.NewGuid().ToString("N");
    public AuthoringSessionGameComponent(Game game) { }
    public string ScopeId => _scopeId ??= Guid.NewGuid().ToString("N");
    public override void ExposeData()
    {
        Scribe_Values.Look(ref _scopeId, "gcfAuthoringConversationScope");
        if (Scribe.mode == LoadSaveMode.PostLoadInit && string.IsNullOrEmpty(_scopeId))
            _scopeId = Guid.NewGuid().ToString("N");
    }
}

/// <summary>One bounded conversation collection per save, stored under Config/GiantCreatureFramework/Sessions.</summary>
public static class AgentSessionManager
{
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private static readonly object Gate = new();
    private static List<AgentSession> _sessions = new();
    private static Game? _game;
    private static bool _initialized;
    private static string _directory = "";
    private static Task _pendingSave = Task.CompletedTask;
    private static bool _loading;
    private static readonly HashSet<string> ReadFailedScopes = new(StringComparer.Ordinal);
    public static Task PendingSave => _pendingSave;
    public static string ScopeId { get; private set; } = "menu";
    public static string PersistenceError { get; private set; } = "";
    public static AgentSession? CurrentSession { get; set; }
    public static List<AgentSession> Sessions => _sessions;

    private static int MaxSessionsLimit => (GiantCreatureAgentMod.Settings?.MaxSessionsPerSave ?? 0) > 0
        ? Math.Clamp(GiantCreatureAgentMod.Settings!.MaxSessionsPerSave, 1, 50)
        : int.MaxValue;
    private static int MaxMessagesLimit => (GiantCreatureAgentMod.Settings?.MaxSessionMessages ?? 0) > 0
        ? Math.Clamp(GiantCreatureAgentMod.Settings!.MaxSessionMessages, 6, 200)
        : int.MaxValue;
    private static int MaxRetentionCharsLimit => (GiantCreatureAgentMod.Settings?.MaxSessionRetentionChars ?? 0) > 0
        ? Math.Clamp(GiantCreatureAgentMod.Settings!.MaxSessionRetentionChars, 10_000, 500_000)
        : int.MaxValue;
    private static int RecentFullEvidenceLimit => (GiantCreatureAgentMod.Settings?.RecentFullEvidenceMessages ?? 0) > 0
        ? Math.Clamp(GiantCreatureAgentMod.Settings!.RecentFullEvidenceMessages, 2, 30)
        : int.MaxValue;

    public static void CancelAllActiveTurns()
    {
        foreach (var session in _sessions.ToArray())
        {
            session.Cancel();
        }
    }

    internal static void ReleasePreviewTexture(object? texture)
    {
        if (texture is not UnityEngine.Object unityObj || unityObj == null)
        {
            return;
        }

        if (UnityData.IsInMainThread || MainThreadDispatcher.IsMainThread)
        {
            UnityEngine.Object.Destroy(unityObj);
            return;
        }

        _ = MainThreadDispatcher.RunOnMainThreadAsync(() =>
        {
            if (unityObj != null)
            {
                UnityEngine.Object.Destroy(unityObj);
            }
            return true;
        });
    }

    internal static void ReleaseSessionTextures(AgentSession? session)
    {
        if (session == null) return;
        List<object?>? textures = null;
        lock (session.SyncRoot)
        {
            foreach (var msg in session.Messages)
            {
                if (msg.PreviewTexture != null)
                {
                    textures ??= new List<object?>();
                    textures.Add(msg.PreviewTexture);
                    msg.PreviewTexture = null;
                }
            }
        }
        if (textures != null)
        {
            foreach (var tex in textures)
            {
                ReleasePreviewTexture(tex);
            }
        }
    }

    public static void RefreshScope()
    {
        if (!MainThreadDispatcher.IsMainThread) throw new InvalidOperationException("Session scope requires main thread.");
        var game = Current.ProgramState == ProgramState.Playing ? Current.Game : null;
        if (_initialized && ReferenceEquals(game, _game)) return;
        var previousSessions = _sessions;
        foreach (var session in previousSessions) session.Cancel();
        if (_initialized) QueueSave();
        foreach (var session in previousSessions) ReleaseSessionTextures(session);
        _directory = Path.Combine(GenFilePaths.ConfigFolderPath, "GiantCreatureFramework", "Sessions");
        _game = game;
        _initialized = true;
        ScopeId = game?.GetComponent<AuthoringSessionGameComponent>()?.ScopeId ?? "menu";
        _sessions = new List<AgentSession>();
        CurrentSession = null;
        var scope = ScopeId;
        var directory = _directory;
        var generation = game;
        _loading = true;
        _ = LoadAsync(scope, directory, generation);
        EnsureDefault();
    }

    private static async Task LoadAsync(string scope, string directory, Game? generation)
    {
        try
        {
            await _pendingSave.ConfigureAwait(false);
            var path = Path.Combine(directory, scope + ".json");
            if (!File.Exists(path)) return;
            var restored = await SessionArchive.ReadAsync(path).ConfigureAwait(false);
            await MainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                if (!ReferenceEquals(_game, generation) || ScopeId != scope) return false;
                var maxSessions = MaxSessionsLimit;
                var previousSessions = _sessions;
                var fresh = previousSessions.Where(s => s.Messages.Count > 0).ToList();
                fresh.AddRange(restored.Where(s => s.ScopeId == scope && s.Messages != null).Take(maxSessions - Math.Min(fresh.Count, maxSessions)));
                var kept = fresh.Take(maxSessions).ToList();
                var keptSet = new HashSet<AgentSession>(kept, ReferenceEqualityComparer.Instance);
                foreach (var dropped in previousSessions.Concat(fresh).Where(s => !keptSet.Contains(s)))
                {
                    ReleaseSessionTextures(dropped);
                }
                _sessions = kept;
                foreach (var session in _sessions)
                {
                    foreach (var message in session.Messages)
                        if (message.UsageSchemaVersion < 2) { message.CachedPromptTokens = 0; message.UsageSchemaVersion = 2; }
                    Trim(session);
                    session.RecalculateTokenTotals();
                }
                CurrentSession = _sessions.FirstOrDefault();
                EnsureDefault();
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (Gate) ReadFailedScopes.Add(scope);
            PersistenceError = UiText.Format("GCFAgent_SessionReadFailed", UiText.Render(ex.Message));
        }
        finally
        {
            await MainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                if (ReferenceEquals(_game, generation) && ScopeId == scope) _loading = false;
                return true;
            }).ConfigureAwait(false);
        }
    }

    public static AgentSession Create()
    {
        EnsureDefault();
        var maxSessions = MaxSessionsLimit;
        if (_sessions.Count >= maxSessions)
        {
            var removable = _sessions.LastOrDefault(s => !s.IsBusy);
            if (removable == null) throw new InvalidOperationException(UiText.Get("GCFAgent_SessionLimitReached"));
            _sessions.Remove(removable);
            ReleaseSessionTextures(removable);
        }
        var session = new AgentSession { ScopeId = ScopeId };
        _sessions.Insert(0, session);
        CurrentSession = session;
        QueueSave();
        return session;
    }

    public static void Delete(AgentSession session)
    {
        session.Cancel();
        _sessions.Remove(session);
        ReleaseSessionTextures(session);
        if (ReferenceEquals(CurrentSession, session)) CurrentSession = _sessions.FirstOrDefault();
        EnsureDefault();
        QueueSave();
    }

    public static void EnsureDefault()
    {
        if (_sessions.Count == 0) _sessions.Add(new AgentSession { ScopeId = ScopeId });
        CurrentSession ??= _sessions[0];
    }

    public static void Trim(AgentSession session)
    {
        var maxMsgs = MaxMessagesLimit;
        var maxRetainChars = MaxRetentionCharsLimit;
        var recentFullMsgs = RecentFullEvidenceLimit;
        if (maxMsgs == int.MaxValue && maxRetainChars == int.MaxValue && recentFullMsgs == int.MaxValue)
        {
            return;
        }

        List<object?>? removedTextures = null;
        void RemoveAtWithTexture(int index)
        {
            var msg = session.Messages[index];
            if (msg.PreviewTexture != null)
            {
                removedTextures ??= new List<object?>();
                removedTextures.Add(msg.PreviewTexture);
                msg.PreviewTexture = null;
            }
            session.Messages.RemoveAt(index);
        }

        lock (session.SyncRoot)
        {
            while (session.Messages.Count > maxMsgs)
            {
                RemoveAtWithTexture(0);
                while (session.Messages.Count > 0 && session.Messages[0].Role != ChatRole.User) RemoveAtWithTexture(0);
            }
            if (recentFullMsgs < int.MaxValue)
            {
                foreach (var message in session.Messages.Take(Math.Max(0, session.Messages.Count - recentFullMsgs)))
                    foreach (var record in message.ToolCalls)
                        if (record.ResultJson.Length > 600 && record.ToolName != "load_manual")
                            record.ResultJson = JsonSerializer.Serialize(
                                new { archived = true, tool = record.ToolName, note = AgentProtocol.Get("History_SessionArchiveNote") },
                                Evidence.UnescapedJsonOptions);
            }
            if (maxRetainChars < int.MaxValue)
            {
                var evidenceChars = 0;
                foreach (var record in session.Messages.AsEnumerable().Reverse().SelectMany(m => m.ToolCalls.AsEnumerable().Reverse()))
                {
                    if (record.ToolName == "load_manual") continue;
                    evidenceChars += record.ResultJson.Length;
                    if (evidenceChars > maxRetainChars)
                        record.ResultJson = JsonSerializer.Serialize(new { archived = true, note = AgentProtocol.Get("History_SessionBudgetArchivedNote") }, Evidence.UnescapedJsonOptions);
                }
                while (session.Messages.Count > 2 && session.Messages.Sum(m => m.Content.Length) > maxRetainChars)
                {
                    RemoveAtWithTexture(0);
                    while (session.Messages.Count > 0 && session.Messages[0].Role != ChatRole.User) RemoveAtWithTexture(0);
                }
            }
        }

        if (removedTextures != null)
        {
            foreach (var tex in removedTextures)
            {
                ReleasePreviewTexture(tex);
            }
        }
    }

    public static void QueueSave()
    {
        lock (Gate)
        {
            if (!_initialized || _directory.Length == 0 || _loading || ReadFailedScopes.Contains(ScopeId)) return;
            var scope = ScopeId;
            var path = Path.Combine(_directory, scope + ".json");
            var snapshot = new List<AgentSession>();
            var maxSessions = MaxSessionsLimit;
            foreach (var session in _sessions.Take(maxSessions))
            {
                Trim(session);
                lock (session.SyncRoot)
                {
                    session.RecalculateTokenTotals();
                    snapshot.Add(new AgentSession
                    {
                        Id = session.Id, ScopeId = session.ScopeId, Title = session.Title, CreatedAt = session.CreatedAt,
                        UseConversationHistory = session.UseConversationHistory,
                        TotalPromptTokens = session.TotalPromptTokens,
                        TotalCachedPromptTokens = session.TotalCachedPromptTokens,
                        PeakContextTokens = session.PeakContextTokens,
                        TotalCompletionTokens = session.TotalCompletionTokens,
                        TotalTokens = session.TotalTokens,
                        Messages = session.Messages.Select(m => new ChatMessage
                        {
                            AnswerKind = m.AnswerKind,
                            ModelIdentity = m.ModelIdentity,
                            CitedEvidenceIds = m.CitedEvidenceIds.ToList(),
                            AnswerAttempts = m.AnswerAttempts.Select(a => new AnswerAttemptRecord
                            { ToolName = a.ToolName, ArgumentsJson = a.ArgumentsJson, Error = a.Error, Accepted = a.Accepted }).ToList(),
                            UsageSchemaVersion = m.UsageSchemaVersion,
                            HasScreenshot = m.HasScreenshot,
                            Role = m.Role,
                            Content = m.IsStreaming ? UiText.Get("GCFAgent_SaveStreamingIncomplete") : m.Content,
                            IsError = m.IsError || m.IsStreaming,
                            LacksRuntimeEvidence = m.LacksRuntimeEvidence,
                            EvidenceNotice = m.EvidenceNotice,
                            Timestamp = m.Timestamp,
                            PromptTokens = m.PromptTokens,
                            CachedPromptTokens = m.CachedPromptTokens,
                            PeakContextTokens = m.PeakContextTokens,
                            CompletionTokens = m.CompletionTokens,
                            TotalTokens = m.TotalTokens,
                            LlmRounds = m.LlmRounds,
                            IsEstimatedTokens = m.IsEstimatedTokens,
                            ToolCalls = m.ToolCalls.Select(c => new ToolCallRecord
                            {
                                CallId = c.CallId, ToolName = c.ToolName, DisplayTitle = c.DisplayTitle,
                                ArgumentsJson = c.ArgumentsJson, ResultJson = c.ResultJson, DurationMs = c.DurationMs,
                                IsCompleted = c.IsCompleted, IsError = c.IsError
                            }).ToList()
                        }).ToList()
                    });
                }
            }
            var chatSecret = GiantCreatureAgentMod.Settings.ApiKey;
            var imageSecret = GiantCreatureAgentMod.Settings.ImageApiKey;
            static string ScrubSecrets(string s, string k1, string k2) =>
                Evidence.Redact(Evidence.Redact(s, k1), k2);

            var prior = _pendingSave;
            _pendingSave = Task.Run(async () =>
            {
                await prior.ConfigureAwait(false);
                await FileGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var savedSession in snapshot)
                    {
                        savedSession.Title = ScrubSecrets(savedSession.Title, chatSecret, imageSecret);
                        foreach (var message in savedSession.Messages)
                        {
                            message.Content = ScrubSecrets(message.Content, chatSecret, imageSecret);
                            foreach (var attempt in message.AnswerAttempts)
                            {
                                attempt.ArgumentsJson = ScrubSecrets(attempt.ArgumentsJson, chatSecret, imageSecret);
                                attempt.Error = ScrubSecrets(attempt.Error, chatSecret, imageSecret);
                            }
                            foreach (var call in message.ToolCalls)
                            {
                                call.ArgumentsJson = ScrubSecrets(call.ArgumentsJson, chatSecret, imageSecret);
                                call.ResultJson = ScrubSecrets(call.ResultJson, chatSecret, imageSecret);
                            }
                        }
                    }
                    await SessionArchive.WriteAsync(path, snapshot).ConfigureAwait(false);
                    PersistenceError = "";
                }
                catch (Exception ex)
                {
                    PersistenceError = UiText.Format("GCFAgent_SessionSaveFailed", UiText.Render(ScrubSecrets(ex.Message, chatSecret, imageSecret)));
                }
                finally
                {
                    FileGate.Release();
                }
            });
        }
    }
}
