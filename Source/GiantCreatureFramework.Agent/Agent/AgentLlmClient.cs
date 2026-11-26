using System.Text.Json;
using System.Text.Json.Nodes;
using GiantCreatureFramework.Agent.UI;
using Verse;

namespace GiantCreatureFramework.Agent;

/// <summary>Main-thread entry point; strictly gated by GiantCreatureFrameworkMod.Settings.enableAuthoringTerminal.</summary>
public static class AgentLlmClient
{
    public static int NetworkTurnRequestsStarted { get; private set; }

    public static void ResetNetworkCounterForVerification() => NetworkTurnRequestsStarted = 0;

    public static bool StartTurn(
        AgentSession session,
        string userPrompt,
        string? imageDataUrl = null,
        object? previewTexture = null)
    {
        bool AbortEarly()
        {
            AgentSessionManager.ReleasePreviewTexture(previewTexture);
            return false;
        }

        // Hard gate: when the authoring terminal toggle is off, never start a turn or send any network request.
        if (!GiantCreatureAgentMod.IsTerminalEnabled)
        {
            return AbortEarly();
        }

        var hasImage = !string.IsNullOrWhiteSpace(imageDataUrl);
        if (string.IsNullOrWhiteSpace(userPrompt) && !hasImage) return AbortEarly();
        if (!MainThreadDispatcher.IsMainThread)
        {
            AgentSessionManager.ReleasePreviewTexture(previewTexture);
            throw new InvalidOperationException("StartTurn requires main thread.");
        }
        AgentSessionManager.RefreshScope();
        if (session.ScopeId != AgentSessionManager.ScopeId) return AbortEarly();

        var settings = GiantCreatureAgentMod.Settings;
        var prompts = settings.CapturePrompts();
        var prompt = string.IsNullOrWhiteSpace(userPrompt)
            ? prompts[AgentPromptSection.ImageQuestion]
            : userPrompt.Trim();
        if (settings.MaxPromptChars > 0)
        {
            var maxPromptChars = Math.Clamp(settings.MaxPromptChars, 500, 64_000);
            if (prompt.Length > maxPromptChars)
            {
                session.SetStatus(UiText.Format("GCFAgent_StatusPromptTooLong", maxPromptChars.ToString()), isError: true);
                return AbortEarly();
            }
        }

        session.RecalculateTokenTotals();
        if (settings.SessionTokenBudget > 0 && session.EffectiveNetTokens >= settings.SessionTokenBudget)
        {
            session.SetStatus(UiText.Format("GCFAgent_StatusSessionBudgetReached", session.EffectiveNetTokens.ToString("N0"), settings.SessionTokenBudget.ToString("N0")), isError: false);
            return AbortEarly();
        }

        var turn = session.TryBeginTurn();
        if (turn == null) return AbortEarly();
        if (UnityData.IsInMainThread)
        {
            try { Prefs.RunInBackground = true; } catch { }
        }
        NetworkTurnRequestsStarted++;
        var assistant = new ChatMessage { Role = ChatRole.Assistant, IsStreaming = true, UsageSchemaVersion = 2 };
        var connectionChain = AttachSessionHeaders(settings.CaptureConnectionChain(),
            session.UseConversationHistory == false ? turn.Id : session.Id);
        var primaryConnection = connectionChain[0];
        assistant.ModelIdentity = new(primaryConnection.Provider, primaryConnection.Model);

        var selectedIds = Current.ProgramState == ProgramState.Playing
            ? Find.Selector?.SelectedObjects?.OfType<Thing>().Take(10).Select(t => t.ThingID).ToArray() ?? Array.Empty<string>()
            : Array.Empty<string>();

        if (!hasImage && previewTexture != null)
        {
            AgentSessionManager.ReleasePreviewTexture(previewTexture);
            previewTexture = null;
        }

        lock (session.SyncRoot)
        {
            if (session.Messages.Count == 0) session.Title = prompt.Length > 14 ? prompt[..14] + "..." : prompt;
            var estUserTok = TokenEstimator.EstimateTokens(prompt) + (hasImage ? 512 : 0);
            var userMsg = new ChatMessage
            {
                Role = ChatRole.User,
                Content = Evidence.Redact(prompt, primaryConnection.ApiKey),
                HasScreenshot = hasImage,
                ImageDataUrl = hasImage ? imageDataUrl : null,
                PreviewTexture = hasImage ? previewTexture : null,
                PromptTokens = estUserTok,
                PeakContextTokens = estUserTok,
                IsEstimatedTokens = true
            };
            session.Messages.Add(userMsg);
            session.Messages.Add(assistant);
            session.SetStatus(hasImage ? UiText.Get("GCFAgent_StatusUploadingScreenshot") : UiText.Get("GCFAgent_StatusPreparingQuery"), isError: false);
        }
        AgentSessionManager.Trim(session);
        try
        {
            var context = BuildConversation(session, assistant, settings);
            var tools = AgentToolRegistry.BuildOpenAiToolsSchema();
            _ = Task.Run(() => RunAsync(session, turn, assistant, connectionChain, selectedIds, context, tools, settings, prompts));
        }
        catch (Exception ex)
        {
            assistant.Content = UiText.Format("GCFAgent_NoticePrepareFailed", Evidence.Redact(ex.Message, primaryConnection.ApiKey));
            assistant.IsError = true;
            assistant.IsStreaming = false;
            session.Complete(turn, UiText.Get(UiText.KeySessionStatusRequestFailed), isError: true);
            turn.Dispose();
        }
        return true;
    }

    public static void CancelActiveTurn(AgentSession session) => session.Cancel();

    private static async Task RunAsync(
        AgentSession session,
        AgentTurn turn,
        ChatMessage assistant,
        IReadOnlyList<ModelConnection> connectionChain,
        string[] selectedIds,
        JsonArray messages,
        JsonArray tools,
        AgentSettings settings,
        AgentPromptSnapshot prompts)
    {
        var status = UiText.KeySessionStatusReady;
        var statusIsError = false;
        var activeConnection = connectionChain[0];
        var maxAnswerChars = settings.MaxAnswerChars > 0 ? Math.Clamp(settings.MaxAnswerChars, 2_000, 128_000) : int.MaxValue;
        var maxReasoningChars = settings.MaxReasoningChars > 0 ? Math.Clamp(settings.MaxReasoningChars, 500, 32_000) : int.MaxValue;
        var runner = new AgentTurnRunner(new HttpChatTransport());
        var priorAttemptsUsage = TokenUsage.Zero;

        void Update(Action action) { lock (session.SyncRoot) { if (session.Owns(turn)) action(); } }
        try
        {
            Exception? lastError = null;
            for (var connIndex = 0; connIndex < connectionChain.Count; connIndex++)
            {
                if (!GiantCreatureAgentMod.IsTerminalEnabled)
                {
                    turn.Cancellation.Cancel();
                }
                turn.Cancellation.Token.ThrowIfCancellationRequested();

                activeConnection = connectionChain[connIndex];
                Update(() => assistant.ModelIdentity = new(activeConnection.Provider, activeConnection.Model, IsFallback: connIndex > 0));
                var attemptMessages = (JsonArray)messages.DeepClone();
                try
                {
                    AgentSettings.Validate(activeConnection);
                    if (connIndex > 0)
                    {
                        Update(() =>
                        {
                            session.SetStatus(UiText.Format("GCFAgent_StatusFallbackConnection", activeConnection.Provider.ShortLabel(), activeConnection.Model), isError: false);
                            assistant.Content = "";
                            assistant.ReasoningContent = "";
                        });
                    }

                    try
                    {
                        await ExecuteRunnerOnceAsync(attemptMessages).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException &&
                                               assistant.ToolCalls.Count == 0 &&
                                               TryDowngradeMultimodalMessagesToText(attemptMessages))
                    {
                        Update(() =>
                        {
                            session.SetStatus(UiText.Get("GCFAgent_StatusMultimodalDowngraded"), isError: false);
                            assistant.Content = "";
                            assistant.ReasoningContent = "";
                        });
                        await ExecuteRunnerOnceAsync(attemptMessages).ConfigureAwait(false);
                    }

                    async Task ExecuteRunnerOnceAsync(JsonArray targetMessages)
                    {
                        try
                        {
                            await runner.RunAsync(activeConnection, targetMessages, tools, async (call, token) =>
                            {
                                var record = new ToolCallRecord
                                {
                                    CallId = call.Id,
                                    ToolName = call.Name,
                                    DisplayTitle = AgentToolRegistry.GetToolDisplayLabel(call.Name, call.Arguments),
                                    ArgumentsJson = Evidence.Redact(call.Arguments, activeConnection.ApiKey)
                                };
                                Update(() => assistant.ToolCalls.Add(record));
                                var (mapId, tick) = await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                                    (Find.CurrentMap?.uniqueID ?? -1, Current.Game?.tickManager?.TicksGame ?? -1), token).ConfigureAwait(false);
                                var execCtx = new AgentToolExecutionContext(session.ScopeId, mapId, tick, selectedIds, activeConnection.ApiKey);
                                var (result, duration) = await AgentToolRegistry.ExecuteToolAsync(call.Name, call.Arguments, execCtx, token).ConfigureAwait(false);
                                var uiResult = StripTransientAttachments(result);
                                Update(() =>
                                {
                                    record.ResultJson = uiResult;
                                    record.DurationMs = duration;
                                    record.IsCompleted = true;
                                    using var doc = JsonDocument.Parse(uiResult);
                                    record.IsError = doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "error";
                                });
                                return result;
                            }, () => Update(() => { assistant.Content = ""; assistant.ReasoningContent = ""; }),
                            chunk => Update(() => assistant.Content = Evidence.Redact(Limit(assistant.Content + chunk, maxAnswerChars), activeConnection.ApiKey)),
                            chunk => Update(() => assistant.ReasoningContent = Evidence.Redact(Limit(assistant.ReasoningContent + chunk, maxReasoningChars), activeConnection.ApiKey)),
                            value => Update(() => session.SetStatus(value, isError: false)),
                            turn.Cancellation.Token,
                            (call, result) => Update(() => assistant.ToolCalls.Add(new ToolCallRecord
                            {
                                CallId = call.Id,
                                ToolName = call.Name,
                                DisplayTitle = AgentToolRegistry.GetToolDisplayLabel(call.Name, call.Arguments),
                                ArgumentsJson = call.Arguments,
                                ResultJson = result,
                                IsCompleted = true,
                                IsError = JsonNode.Parse(result)?["status"]?.ToString() == "error"
                            })),
                            model => Update(() => assistant.ModelIdentity = new(activeConnection.Provider, activeConnection.Model, model, connIndex > 0)),
                            prompts,
                            AgentToolRegistry.BuildOpenAiToolsSchema).ConfigureAwait(false);
                        }
                        finally
                        {
                            Update(() => assistant.AnswerAttempts.AddRange(runner.AnswerAttempts.Select(a => new AnswerAttemptRecord
                            {
                                ToolName = a.ToolName,
                                ArgumentsJson = Evidence.Redact(a.ArgumentsJson, activeConnection.ApiKey),
                                Error = Evidence.Redact(a.Error, activeConnection.ApiKey),
                                Accepted = a.Accepted
                            })));
                        }
                    }

                    lastError = null;
                    break;
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TimeoutException && connIndex + 1 < connectionChain.Count)
                {
                    lastError = ex;
                    priorAttemptsUsage = priorAttemptsUsage.Add(runner.LastTurnUsage);
                    AgentTurnRunner.RepairIncompleteToolTurnTail(attemptMessages);
                    messages = attemptMessages;
                }
            }

            if (lastError != null)
            {
                throw lastError;
            }
        }
        catch (OperationCanceledException)
        {
            status = turn.Cancellation.IsCancellationRequested ? UiText.Get(UiText.KeySessionStatusAborted) : UiText.Get(UiText.KeySessionStatusTimeout);
            statusIsError = !turn.Cancellation.IsCancellationRequested;
            Update(() =>
            {
                assistant.IsError = true;
                var notice = UiText.Format("GCFAgent_NoticeTurnIncomplete", status);
                assistant.Content = string.IsNullOrWhiteSpace(assistant.Content)
                    ? notice
                    : notice + "\n\n" + assistant.Content;
            });
        }
        catch (Exception ex)
        {
            status = UiText.Get(UiText.KeySessionStatusRequestFailed);
            statusIsError = true;
            Update(() =>
            {
                assistant.IsError = true;
                var errLine = UiText.Format("GCFAgent_NoticeRequestFailed", UiText.Render(Evidence.Redact(ex.Message, activeConnection.ApiKey)));
                assistant.Content = string.IsNullOrWhiteSpace(assistant.Content)
                    ? errLine
                    : errLine + "\n\n" + assistant.Content;
            });
        }
        finally
        {
            Update(() =>
            {
                assistant.IsStreaming = false;
                assistant.AnswerKind = runner.LastAnswerKind;
                assistant.CitedEvidenceIds = runner.LastCitedEvidenceIds.ToList();
                if (!assistant.IsError && runner.LastAnswerKind == "insufficient")
                {
                    assistant.LacksRuntimeEvidence = true;
                    assistant.EvidenceNotice = UiText.Get("GCFAgent_NoticeInsufficientEvidence");
                }
                var usage = priorAttemptsUsage.Add(runner.LastTurnUsage);
                if (usage.TotalTokens > 0)
                {
                    assistant.PromptTokens = usage.PromptTokens;
                    assistant.CachedPromptTokens = usage.CachedPromptTokens;
                    assistant.PeakContextTokens = usage.PeakContextTokens > 0 ? usage.PeakContextTokens : usage.PromptTokens;
                    assistant.CompletionTokens = usage.CompletionTokens;
                    assistant.TotalTokens = usage.TotalTokens;
                    assistant.LlmRounds = Math.Max(1, usage.LlmCallCount);
                    assistant.IsEstimatedTokens = usage.IsEstimated;
                }
                else if (!string.IsNullOrEmpty(assistant.Content))
                {
                    var estOut = TokenEstimator.EstimateTokens(assistant.Content);
                    assistant.CompletionTokens = estOut;
                    assistant.TotalTokens = assistant.PromptTokens + estOut;
                    assistant.LlmRounds = 1;
                    assistant.IsEstimatedTokens = true;
                }

                var abortedJson = JsonSerializer.Serialize(new { status = "error", error = AgentProtocol.Get("LlmClient_AbortedToolResult") }, Evidence.UnescapedJsonOptions);
                foreach (var call in assistant.ToolCalls.Where(c => !c.IsCompleted))
                {
                    call.IsCompleted = true;
                    call.IsError = true;
                    call.ResultJson = abortedJson;
                }
                session.RecalculateTokenTotals();
                if (status == UiText.KeySessionStatusReady && assistant.TotalTokens > 0)
                {
                    var approx = assistant.IsEstimatedTokens ? "≈" : "";
                    status = UiText.Format("GCFAgent_StatusReadyWithUsage", approx,
                        assistant.TotalTokens.ToString("N0"), assistant.PromptTokens.ToString("N0"), assistant.CompletionTokens.ToString("N0"));
                }
            });
            var wasCancelled = turn.Cancellation.IsCancellationRequested;
            session.Complete(turn, UiText.Render(status), statusIsError);
            turn.Dispose();
            await MainThreadDispatcher.RunOnMainThreadAsync(() =>
            {
                if (session.ScopeId == AgentSessionManager.ScopeId)
                {
                    AgentSessionManager.QueueSave();
                    if (!wasCancelled && GiantCreatureAgentMod.IsTerminalEnabled)
                    {
                        Dialog_AgentChat.NotifyReplyCompletedOnMainThread(session, assistant);
                    }
                }
                return true;
            }).ConfigureAwait(false);
        }
    }

    private static string StripTransientAttachments(string resultJson)
    {
        if (!resultJson.Contains("_attachedImageDataUrl", StringComparison.Ordinal)) return resultJson;
        try
        {
            if (JsonNode.Parse(resultJson) is JsonObject obj)
            {
                obj.Remove("_attachedImageDataUrl");
                obj.Remove("_attachedImageLabel");
                return obj.ToJsonString(Evidence.UnescapedJsonOptions);
            }
        }
        catch
        {
            // Ignore
        }
        return resultJson;
    }

    private static JsonArray BuildConversation(AgentSession session, ChatMessage assistant, AgentSettings settings)
    {
        var messages = ConversationHistory.Build(session, assistant, settings.GetEffectiveSystemPrompt(), settings.MaxHistoryEvidenceChars);
        if (settings.MaxContextChars > 0)
            AgentTurnRunner.Compact(messages, settings.MaxContextChars, settings.HistoricalToolCompactChars);
        return messages;
    }

    private static bool TryDowngradeMultimodalMessagesToText(JsonArray messages)
    {
        var downgraded = false;
        foreach (var node in messages.OfType<JsonObject>())
        {
            if (node["content"] is not JsonArray parts) continue;
            var textPart = parts
                .OfType<JsonObject>()
                .FirstOrDefault(p => p["type"]?.ToString() == "text")?["text"]?.ToString()
                ?? AgentProtocol.Get("LlmClient_VisionFallbackPrompt");
            node["content"] = AgentProtocol.Get("LlmClient_VisionFallbackNotice") + "\n" + textPart;
            downgraded = true;
        }
        return downgraded;
    }

    private static IReadOnlyList<ModelConnection> AttachSessionHeaders(IReadOnlyList<ModelConnection> connections, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || connections.Count == 0) return connections;
        var result = new List<ModelConnection>(connections.Count);
        foreach (var conn in connections)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var baseHeaders = conn.ExtraHeaders ?? AIProviderRegistry.GetExtraHeaders(conn.Provider);
            if (baseHeaders != null)
            {
                foreach (var kv in baseHeaders) headers[kv.Key] = kv.Value;
            }
            headers["X-Session-ID"] = sessionId;
            headers["Session-Id"] = sessionId;
            result.Add(conn with { ExtraHeaders = headers });
        }
        return result;
    }

    private static string Limit(string value, int max) =>
        max >= int.MaxValue || value.Length <= max ? value : value[..max] + "\n" + UiText.Get("GCFAgent_NoticeContentTruncated");
}
