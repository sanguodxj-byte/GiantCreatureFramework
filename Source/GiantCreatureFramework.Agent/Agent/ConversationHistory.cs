using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent;

/// <summary>Keep acquired evidence independently of whether the final model request succeeded.</summary>
public static class ConversationHistory
{
    public static JsonArray Build(AgentSession session, ChatMessage currentAssistant, string systemPrompt, int evidenceBudget = 0)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };
        lock (session.SyncRoot)
        {
            var history = session.Messages.TakeWhile(m => !ReferenceEquals(m, currentAssistant)).ToArray();
            var activeUser = history.LastOrDefault(m => m.Role == ChatRole.User);
            if (session.UseConversationHistory == false)
                history = activeUser == null ? Array.Empty<ChatMessage>() : new[] { activeUser };
            var remaining = evidenceBudget > 0 ? Math.Clamp(evidenceBudget, 4_000, 200_000) : int.MaxValue;
            var retained = new Dictionary<ToolCallRecord, string>();
            var candidates = history.Reverse().Where(m => m.Role == ChatRole.Assistant)
                .SelectMany(m => m.ToolCalls.AsEnumerable().Reverse())
                .Where(c => c.IsCompleted && !c.IsError && EvidenceQuality.HasUsableResult(c.ResultJson))
                .OrderByDescending(c => c.ToolName is "load_manual" or "read_mod_file" or "read_creature_xml" or "get_def_details" ? 1 : 0);
            foreach (var call in candidates)
            {
                if (remaining <= 0) break;
                var text = call.ResultJson;
                if (text.Length <= remaining) { retained[call] = text; remaining -= text.Length; }
            }

            for (var index = 0; index < history.Length; index++)
            {
                var message = history[index];
                if (message.Role == ChatRole.User)
                {
                    JsonNode content = JsonValue.Create(message.HasScreenshot ? AgentProtocol.Get("History_ScreenshotPrefix") + message.Content : message.Content)!;
                    if (ReferenceEquals(message, activeUser) && !string.IsNullOrWhiteSpace(message.ImageDataUrl))
                        content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message.Content },
                            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = message.ImageDataUrl } });
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    continue;
                }
                if (message.Role != ChatRole.Assistant || message.IsStreaming) continue;
                var historyCalls = message.ToolCalls.Where(c => c.ToolName != ToolDiscovery.ToolName).ToArray();
                if (historyCalls.Length > 0)
                {
                    var calls = new JsonArray();
                    for (var i = 0; i < historyCalls.Length; i++)
                    {
                        var call = historyCalls[i];
                        calls.Add(new JsonObject { ["id"] = $"hist_{index}_{i}", ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = call.ToolName, ["arguments"] = ValidArguments(call.ArgumentsJson) } });
                    }
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = AgentProtocol.Get("History_AssistantCallsLabel"), ["tool_calls"] = calls });
                    for (var i = 0; i < historyCalls.Length; i++)
                    {
                        var call = historyCalls[i];
                        var body = call.ToolName == "fold_evidence" && call.IsCompleted && !call.IsError
                            ? JsonNode.Parse(call.ResultJson)!
                            : retained.TryGetValue(call, out var result)
                            ? JsonNode.Parse(result)!
                            : new JsonObject { ["status"] = call.IsError || !call.IsCompleted ? "error" : "archived",
                                ["tool"] = call.ToolName, ["arguments"] = call.ArgumentsJson,
                                ["note"] = call.IsError || !call.IsCompleted ? AgentProtocol.Get("History_CallFailedNote") : AgentProtocol.Get("History_CallArchivedNote") };
                        if (body is JsonObject evidence && evidence["evidenceId"] == null)
                            evidence["evidenceId"] = $"hist_{index}_{i}";
                        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = $"hist_{index}_{i}",
                            ["content"] = new JsonObject { ["historicalSnapshot"] = true, ["originalCallId"] = call.CallId, ["capturedAt"] = message.Timestamp.ToString("O"),
                                ["note"] = AgentProtocol.Get("History_SnapshotNote"), ["evidence"] = body }.ToJsonString(Evidence.UnescapedJsonOptions) });
                    }
                }
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = message.IsError
                    ? AgentProtocol.Get("History_IncompleteTurnNotice")
                    : message.LacksRuntimeEvidence ? AgentProtocol.Get("History_LacksRuntimeEvidenceNotice") : message.Content });
            }
        }
        return messages;
    }

    private static string ValidArguments(string json)
    {
        try { return JsonNode.Parse(json) is JsonObject obj ? obj.ToJsonString() : "{}"; }
        catch (JsonException) { return "{}"; }
    }
}
