using System.Text.Json;
using System.Text.Json.Nodes;
using GiantCreatureFramework.Agent.Authoring;

namespace GiantCreatureFramework.Agent;

/// <summary>Executes model-selected calls without interpreting user questions.</summary>
public sealed class AgentTurnRunner(IChatTransport transport, TimeProvider? timeProvider = null)
{
    public const int DefaultMaxContextChars = 100_000;
    public const int DefaultMaxRounds = int.MaxValue;
    public const int DefaultMaxCalls = int.MaxValue;
    public static int MaxContextChars { get; set; } = DefaultMaxContextChars;
    public static int MaxCalls { get; set; } = DefaultMaxCalls;
    public TokenUsage LastTurnUsage { get; private set; } = TokenUsage.Zero;
    public int LastTurnSuccessfulEvidence { get; private set; }
    public int LastTurnCallsMade { get; private set; }
    public bool LastTurnHasUsableEvidence { get; private set; }
    public string LastAnswerKind { get; private set; } = "";
    public IReadOnlyList<string> LastCitedEvidenceIds { get; private set; } = Array.Empty<string>();
    public List<AnswerAttemptRecord> AnswerAttempts { get; } = new();

    public async Task RunAsync(ModelConnection connection, JsonArray messages, JsonArray tools,
        Func<ModelToolCall, CancellationToken, Task<string>> execute,
        Action resetText, Action<string> onText, Action<string> onReasoning, Action<string> onStatus,
        CancellationToken cancellationToken, Action<ModelToolCall, string>? onLocalTool = null, Action<string>? onResponseModel = null,
        AgentPromptSnapshot? prompts = null, Func<JsonArray>? refreshTools = null,
        IReadOnlyList<CapabilityGroup>? capabilitiesOverride = null, IReadOnlyDictionary<string, string>? purposesOverride = null)
    {
        prompts ??= new AgentPromptSnapshot();
        LastTurnUsage = TokenUsage.Zero;
        LastTurnSuccessfulEvidence = 0;
        LastTurnCallsMade = 0;
        LastTurnHasUsableEvidence = false;
        LastAnswerKind = "";
        LastCitedEvidenceIds = Array.Empty<string>();
        AnswerAttempts.Clear();
        var maxRounds = connection.MaxRounds > 0 ? Math.Clamp(connection.MaxRounds, 1, 100) : DefaultMaxRounds;
        var maxCalls = connection.MaxTotalToolCalls > 0 ? Math.Clamp(connection.MaxTotalToolCalls, 1, 500) : DefaultMaxCalls;
        var requestTimeout = TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds > 0 ? connection.TimeoutSeconds : 180, 15, 600));
        using var totalDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var trustedToolPreviews = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        var turnSecurity = UserImg2ImgGuard.InspectAndSanitizeTurnMessages(messages, trustedToolPreviews);
        using var securityScope = UserImg2ImgGuard.EnterTurnScope(turnSecurity);

        var evidence = new EvidenceContext();
        evidence.Observe(messages);
        SubmittedAnswer? pendingAnswer = null;

        JsonArray AvailableTools()
        {
            var rawTools = (JsonArray)(refreshTools?.Invoke() ?? tools).DeepClone();
            var result = new JsonArray();
            foreach (var node in rawTools)
            {
                var fnName = (node as JsonObject)?["function"]?["name"]?.GetValue<string>() ?? "";
                if (turnSecurity.IsImg2ImgViolation &&
                    fnName is "generate_image" or "extract_sheet" or "warp_bone" or "write_creature_xml" or "inject_creature" or "spawn_creature")
                {
                    continue;
                }
                if (turnSecurity.HasUserImageAttachment && fnName == "generate_image")
                {
                    continue;
                }
                result.Add(node?.DeepClone());
            }
            result.Add(EvidenceContext.ReadSchema());
            result.Add(EvidenceContext.FoldSchema());
            return result;
        }

        var discovery = new ToolDiscovery(AvailableTools, prompts[AgentPromptSection.ToolDiscovery], capabilitiesOverride, purposesOverride);
        discovery.Restore(messages);
        var instructionIndex = messages.TakeWhile(m => m?["role"]?.ToString() == "system").Count();
        var immutableGuardText = AgentProtocol.Get("Security_ImmutableNoUserImg2Img");
        var firstMsg = messages.FirstOrDefault() as JsonObject;
        if (firstMsg?["role"]?.ToString() != "system" ||
            !(firstMsg["content"]?.ToString()?.Contains(immutableGuardText, StringComparison.Ordinal) ?? false))
        {
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = immutableGuardText });
            instructionIndex++;
        }
        foreach (var section in new[] { AgentPromptSection.Submission, AgentPromptSection.Evidence })
        {
            if (!string.IsNullOrWhiteSpace(prompts[section]))
                messages.Insert(instructionIndex++, new JsonObject { ["role"] = "system", ["content"] = prompts[section] });
        }
        if (turnSecurity.IsImg2ImgViolation)
        {
            messages.Insert(instructionIndex++, new JsonObject
            {
                ["role"] = "system",
                ["content"] = AgentProtocol.Get("Authoring_UserImg2ImgForbidden")
            });
        }

        void Complete(SubmittedAnswer answer)
        {
            answer = UserImg2ImgGuard.EnforceRefusalAnswerIfImg2ImgTurn(answer);
            LastAnswerKind = answer.Kind;
            LastCitedEvidenceIds = answer.EvidenceIds;
            LastTurnHasUsableEvidence = answer.EvidenceIds.Count > 0;
            resetText();
            onText(answer.Text);
        }

        async Task<ModelReply> Request(bool mayInvestigate, CancellationToken token)
        {
            resetText();
            var requestTools = mayInvestigate ? discovery.BuildSchema() : new JsonArray();
            requestTools.Add(AnswerContract.Schema());
            if (pendingAnswer != null) requestTools.Add(AnswerContract.RepairSchema());
            var wireMessages = evidence.Project(messages);
            using var timer = new CancellationTokenSource(requestTimeout, timeProvider ?? TimeProvider.System);
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
            ModelReply reply;
            try
            {
                reply = await transport.SendAsync(connection, Payload(connection, wireMessages, requestTools), _ => { }, onReasoning, requestCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timer.IsCancellationRequested)
            {
                throw new TimeoutException(UiText.Get("GCFAgent_TurnRequestTimeout"));
            }
            if (!string.IsNullOrWhiteSpace(reply.ResponseModel)) onResponseModel?.Invoke(reply.ResponseModel);
            LastTurnUsage = LastTurnUsage.Add(DetermineEffectiveUsage(reply, wireMessages, requestTools));
            return reply;
        }

        async Task<SubmittedAnswer?> Process(ModelReply reply, bool mayInvestigate, CancellationToken token)
        {
            if (reply.Tools.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(reply.Text))
                    throw new InvalidDataException(UiText.Get("GCFAgent_TurnNoReplyOrToolCall"));
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Text });
                if (!string.IsNullOrWhiteSpace(prompts[AgentPromptSection.MissingSubmission]))
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompts[AgentPromptSection.MissingSubmission] });
                return null;
            }

            var calls = new JsonArray();
            foreach (var call in reply.Tools)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments }
                });
            }
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.Text, ["tool_calls"] = calls });

            var pendingImages = new List<(string Label, string DataUrl)>();
            var mutatingTools = new HashSet<string>(StringComparer.Ordinal)
            {
                "write_creature_xml",
                "generate_locomotion_anim",
                "generate_image",
                "extract_sheet",
                "warp_bone",
                "preview_sheet",
                "preview_anim_strip",
                "validate_creature",
                "inject_creature",
                "spawn_creature"
            };

            foreach (var call in reply.Tools)
            {
                token.ThrowIfCancellationRequested();
                string result;
                if (call.Name is AnswerContract.ToolName or "repair_answer")
                {
                    var attempt = new AnswerAttemptRecord { ToolName = call.Name, ArgumentsJson = call.Arguments };
                    AnswerAttempts.Add(attempt);
                    try
                    {
                        if (reply.Tools.Count != 1) throw new InvalidDataException(AgentProtocol.Get("TurnRunner_SubmitAlone"));
                        if (call.Name == AnswerContract.ToolName)
                        {
                            pendingAnswer = null;
                            pendingAnswer = AnswerContract.Parse(call.Arguments);
                        }
                        else
                        {
                            if (pendingAnswer == null) throw new InvalidDataException(AgentProtocol.Get("TurnRunner_NoPendingDraft"));
                            var args = JsonNode.Parse(call.Arguments) as JsonObject ?? throw new InvalidDataException(AgentProtocol.Get("TurnRunner_RepairArgsObject"));
                            if (args["evidenceIds"] is not JsonArray ids) throw new InvalidDataException(AgentProtocol.Get("TurnRunner_EvidenceIdsArray"));
                            pendingAnswer = pendingAnswer with { EvidenceIds = ids.Select(n => n?.GetValue<string>() ?? "").ToArray() };
                        }
                        var validated = evidence.Validate(pendingAnswer);
                        attempt.Accepted = true;
                        return validated;
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
                    {
                        attempt.Error = ex.Message;
                        result = new JsonObject
                        {
                            ["status"] = "error",
                            ["error"] = ex.Message,
                            ["draftRetained"] = pendingAnswer != null,
                            ["nextStep"] = prompts[AgentPromptSection.CitationRepair],
                            ["availableEvidence"] = evidence.Catalog()
                        }.ToJsonString(Evidence.UnescapedJsonOptions);
                    }
                }
                else if (call.Name == ToolDiscovery.ToolName)
                {
                    try
                    {
                        if (!mayInvestigate) throw new InvalidOperationException(AgentProtocol.Get("TurnRunner_SynthesisOnly"));
                        result = discovery.Select(call.Arguments);
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                    {
                        result = JsonSerializer.Serialize(new { status = "error", error = ex.Message }, Evidence.UnescapedJsonOptions);
                    }
                    onLocalTool?.Invoke(call, result);
                }
                else if (call.Name is "read_evidence" or "fold_evidence")
                {
                    discovery.RememberUse(call.Name);
                    try { result = call.Name == "read_evidence" ? evidence.Read(call.Arguments) : evidence.Fold(call.Arguments, messages); }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
                    { result = JsonSerializer.Serialize(new { status = "error", error = ex.Message }, Evidence.UnescapedJsonOptions); }
                    onLocalTool?.Invoke(call, result);
                    evidence.Register(result, call.Id, call.Name, call.Arguments);
                }
                else if (!mayInvestigate || LastTurnCallsMade >= maxCalls)
                {
                    result = JsonSerializer.Serialize(new { status = "error", error = AgentProtocol.Get("TurnRunner_BudgetExhausted") }, Evidence.UnescapedJsonOptions);
                }
                else
                {
                    discovery.RememberUse(call.Name);
                    onStatus(UiText.Get("GCFAgent_StatusReadingEvidence"));
                    var isMutating = mutatingTools.Contains(call.Name);
                    if (isMutating) cache.Clear();
                    var signature = NormalizeToolSignature(call.Name, call.Arguments);
                    if (isMutating || !cache.TryGetValue(signature, out result!))
                    {
                        UserImg2ImgGuard.EnforceToolExecutionGate(call.Name);
                        LastTurnCallsMade++;
                        var raw = await execute(call, token).ConfigureAwait(false);
                        if (JsonNode.Parse(raw) is JsonObject obj)
                        {
                            obj["evidenceId"] ??= call.Id;
                            if (call.Name == "load_manual")
                            {
                                obj["exemptFromCompact"] = true;
                            }
                            if (obj["_attachedImageDataUrl"]?.GetValue<string>() is { Length: > 0 } imgUrl)
                            {
                                var imgLabel = obj["_attachedImageLabel"]?.GetValue<string>() ?? call.Name;
                                pendingImages.Add((imgLabel, imgUrl));
                                obj.Remove("_attachedImageDataUrl");
                                obj.Remove("_attachedImageLabel");
                            }
                            result = obj.ToJsonString(Evidence.UnescapedJsonOptions);
                        }
                        else
                        {
                            result = raw;
                        }
                        if (!isMutating) cache[signature] = result;
                        if (EvidenceQuality.HasUsableResult(result)) LastTurnSuccessfulEvidence++;
                    }
                    evidence.Register(result, call.Id, call.Name, call.Arguments);
                }
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = result });
            }

            if (pendingImages.Count > 0)
            {
                var parts = new JsonArray();
                foreach (var (label, dataUrl) in pendingImages)
                {
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = AgentProtocol.Format("TurnRunner_AttachedImageIntro", label) });
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } });
                }
                var previewMsg = new JsonObject { ["role"] = "user", ["content"] = parts };
                trustedToolPreviews.Add(previewMsg);
                messages.Add(previewMsg);
            }

            return null;
        }

        var synthesisReason = AgentProtocol.Get("TurnRunner_SynthesisReasonLimit");
        try
        {
            for (var round = 0; round < maxRounds && LastTurnCallsMade < maxCalls; round++)
            {
                totalDeadline.Token.ThrowIfCancellationRequested();
                onStatus(UiText.Format("GCFAgent_StatusInvestigatingRound", (round + 1).ToString()));
                using var exploration = CancellationTokenSource.CreateLinkedTokenSource(totalDeadline.Token);
                var reply = await Request(true, exploration.Token).ConfigureAwait(false);
                var answer = await Process(reply, true, exploration.Token).ConfigureAwait(false);
                if (answer != null) { Complete(answer); return; }
            }
        }
        catch (Exception ex) when (!totalDeadline.IsCancellationRequested && ex is TimeoutException or OperationCanceledException)
        {
            synthesisReason = AgentProtocol.Get("TurnRunner_SynthesisReasonTimeout");
            RepairIncompleteToolTurnTail(messages);
        }
        RepairIncompleteToolTurnTail(messages);
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = synthesisReason + prompts[AgentPromptSection.Synthesis] });
        onStatus(UiText.Get("GCFAgent_StatusSynthesizing"));
        const int maxSynthesisAttempts = 4;
        for (var attempt = 0; attempt < maxSynthesisAttempts && !totalDeadline.IsCancellationRequested; attempt++)
        {
            var reply = await Request(false, totalDeadline.Token).ConfigureAwait(false);
            var answer = await Process(reply, false, totalDeadline.Token).ConfigureAwait(false);
            if (answer != null) { Complete(answer); return; }
        }
        throw new InvalidDataException(UiText.Get("GCFAgent_TurnNoFinalSubmission"));
    }

    private static string NormalizeToolSignature(string toolName, string argumentsJson)
    {
        var name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(argumentsJson)) return name + ":{}";
        try
        {
            var node = JsonNode.Parse(argumentsJson);
            if (node is JsonObject obj)
            {
                var sorted = new JsonObject();
                foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    sorted[kv.Key] = kv.Value?.DeepClone();
                }
                return name + ":" + sorted.ToJsonString(Evidence.UnescapedJsonOptions);
            }
        }
        catch
        {
            // Fallback
        }
        return name + ":" + argumentsJson.Trim();
    }

    public static void RepairIncompleteToolTurnTail(JsonArray messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject msg) continue;
            var role = msg["role"]?.ToString();
            if (role == "user") break;
            if (role != "assistant" || msg["tool_calls"] is not JsonArray tcArr || tcArr.Count == 0) continue;

            var answeredIds = new HashSet<string>(StringComparer.Ordinal);
            for (var j = i + 1; j < messages.Count; j++)
            {
                if (messages[j] is JsonObject toolMsg && toolMsg["role"]?.ToString() == "tool")
                {
                    var id = toolMsg["tool_call_id"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) answeredIds.Add(id!);
                }
            }

            foreach (var tc in tcArr.OfType<JsonObject>())
            {
                var id = tc["id"]?.ToString();
                if (!string.IsNullOrEmpty(id) && !answeredIds.Contains(id!))
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["content"] = JsonSerializer.Serialize(new { status = "error", error = AgentProtocol.Get("TurnRunner_IncompleteTailError") }, Evidence.UnescapedJsonOptions)
                    });
                }
            }
            break;
        }
    }

    private static TokenUsage DetermineEffectiveUsage(ModelReply reply, JsonArray messages, JsonArray? tools)
    {
        if (reply.Usage != null && reply.Usage.TotalTokens > 0)
        {
            return reply.Usage;
        }

        var promptChars = messages.ToJsonString(Evidence.UnescapedJsonOptions);
        var promptTokens = TokenEstimator.EstimateTokens(promptChars);
        if (tools != null)
        {
            promptTokens += TokenEstimator.EstimateTokens(tools.ToJsonString(Evidence.UnescapedJsonOptions));
        }

        var completionTokens = TokenEstimator.EstimateTokens(reply.Text);
        foreach (var call in reply.Tools)
        {
            completionTokens += TokenEstimator.EstimateTokens(call.Name) + TokenEstimator.EstimateTokens(call.Arguments) + 4;
        }

        return new TokenUsage(
            PromptTokens: Math.Max(1, promptTokens),
            CompletionTokens: Math.Max(1, completionTokens),
            TotalTokens: Math.Max(2, promptTokens + completionTokens),
            IsEstimated: true,
            CachedPromptTokens: 0,
            PeakContextTokens: Math.Max(1, promptTokens),
            LlmCallCount: 1);
    }

    /// <summary>Builds OpenAI-compatible payload without any max_tokens / output-length cap.</summary>
    public static JsonObject Payload(ModelConnection connection, JsonArray messages, JsonArray? tools, bool allowToolCalls = true)
    {
        if (connection.MaxContextChars > 0)
        {
            Compact(messages, connection.MaxContextChars, connection.HistoricalToolCompactChars);
        }

        var payload = new JsonObject
        {
            ["messages"] = messages.DeepClone(),
            ["temperature"] = connection.Temperature,
            ["stream"] = true
        };

        if (connection.Provider != AIProvider.Player2)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        var omitModel = connection.Provider == AIProvider.Player2 &&
                        (string.IsNullOrWhiteSpace(connection.Model) ||
                         string.Equals(connection.Model, "Default", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(connection.Model, "Player2", StringComparison.OrdinalIgnoreCase));
        if (!omitModel)
        {
            payload["model"] = connection.Model;
        }

        if (allowToolCalls && tools != null)
        {
            payload["tools"] = tools.DeepClone();
            payload["tool_choice"] = "auto";
        }
        else if (!allowToolCalls && tools != null && tools.Count > 0)
        {
            payload["tools"] = tools.DeepClone();
            payload["tool_choice"] = "none";
        }

        return payload;
    }

    public static void Compact(JsonArray messages, int maxContextChars = 0, int compactThresholdChars = 0)
    {
        var budget = maxContextChars > 0 ? maxContextChars : MaxContextChars;
        var toolThreshold = compactThresholdChars > 0 ? compactThresholdChars : 2000;
        var previewLen = Math.Clamp((int)(toolThreshold * 0.8), Math.Min(200, toolThreshold), toolThreshold);

        // Identify tool_call_ids for load_manual so manual results are exempt from truncation.
        var manualCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in messages.OfType<JsonObject>())
        {
            if (msg["role"]?.ToString() != "assistant" || msg["tool_calls"] is not JsonArray calls) continue;
            foreach (var tc in calls.OfType<JsonObject>())
            {
                if (tc["function"]?["name"]?.ToString() == "load_manual" && tc["id"]?.ToString() is { Length: > 0 } id)
                {
                    manualCallIds.Add(id);
                }
            }
        }

        // 1. Shrink non-exempt tool bodies first, preserving every call/result pair and the newest question.
        if (messages.ToJsonString(Evidence.UnescapedJsonOptions).Length <= budget) return;
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["role"]?.ToString() != "tool") continue;
            var callId = message["tool_call_id"]?.ToString() ?? "";
            var content = message["content"]?.ToString() ?? "";
            if (manualCallIds.Contains(callId) || IsExemptToolPayload(content)) continue;
            if (content.Length > toolThreshold)
            {
                message["content"] = new JsonObject
                {
                    ["truncated"] = true,
                    ["historicalPreview"] = content[..Math.Min(content.Length, previewLen)],
                    ["note"] = AgentProtocol.Get("TurnRunner_CompactNote")
                }.ToJsonString(Evidence.UnescapedJsonOptions);
            }
        }

        // 2. Shrink historical evidence blocks embedded in assistant messages before dropping turns.
        if (messages.ToJsonString(Evidence.UnescapedJsonOptions).Length <= budget) return;
        var historyMarker = AgentProtocol.Get("TurnRunner_HistoryEvidenceMarker");
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["role"]?.ToString() != "assistant") continue;
            var content = message["content"]?.ToString() ?? "";
            var markerIdx = content.IndexOf(historyMarker, StringComparison.Ordinal);
            if (markerIdx < 0) continue;
            var evidencePart = content[(markerIdx + historyMarker.Length)..];
            if (evidencePart.Length > previewLen)
            {
                message["content"] = content[..(markerIdx + historyMarker.Length)]
                    + evidencePart[..previewLen]
                    + AgentProtocol.Get("TurnRunner_HistoryEvidenceFolded");
            }
        }

        // 3. Remove whole previous user turns while preserving all leading system messages; never orphan tool messages.
        var firstNonSystem = messages.TakeWhile(m => m?["role"]?.ToString() == "system").Count();
        while (messages.ToJsonString(Evidence.UnescapedJsonOptions).Length > budget)
        {
            var nextUser = -1;
            for (var i = firstNonSystem + 1; i < messages.Count; i++)
            {
                if (messages[i] is JsonObject msg &&
                    msg["role"]?.ToString() == "user" &&
                    !UserImg2ImgGuard.IsSyntheticUserMessage(msg))
                {
                    nextUser = i;
                    break;
                }
            }
            if (nextUser < 0) throw new InvalidDataException(UiText.Get("GCFAgent_TurnContextBudgetExceeded"));
            for (var i = firstNonSystem; i < nextUser; i++) messages.RemoveAt(firstNonSystem);
        }
    }

    private static bool IsExemptToolPayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || (!content.Contains("load_manual", StringComparison.Ordinal) && !content.Contains("exemptFromCompact", StringComparison.Ordinal)))
        {
            return false;
        }
        try
        {
            if (JsonNode.Parse(content) is JsonObject obj)
            {
                if (obj["tool"]?.ToString() == "load_manual" || obj["exemptFromCompact"]?.GetValue<bool>() == true)
                {
                    return true;
                }
                if (obj["historicalSnapshot"]?.ToString() == "true" && obj["evidence"] is JsonObject ev)
                {
                    return ev["tool"]?.ToString() == "load_manual" || ev["exemptFromCompact"]?.GetValue<bool>() == true;
                }
            }
        }
        catch
        {
            // Ignore non-JSON tool payloads
        }
        return false;
    }
}
