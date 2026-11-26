using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent;

public sealed record ModelConnection(
    string Endpoint,
    string ApiKey,
    string Model,
    float Temperature,
    int MaxRounds = 0,
    int TimeoutSeconds = 180,
    int IdleSeconds = 60,
    AIProvider Provider = AIProvider.Custom,
    int MaxOutputTokens = 0,
    int MaxContextChars = 0,
    int ContextReserveChars = 0,
    int MaxTotalToolCalls = 0,
    int MaxToolCallsPerRound = 0,
    int MaxToolResultChars = 12_000,
    int HistoricalToolCompactChars = 2_000,
    int MaxResponseChars = 0,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null);

public sealed record ModelToolCall(string Id, string Name, string Arguments);
public sealed record ModelReply(string Text, string FinishReason, IReadOnlyList<ModelToolCall> Tools, TokenUsage? Usage = null, string ResponseModel = "");

public interface IChatTransport
{
    Task<ModelReply> SendAsync(ModelConnection connection, JsonObject payload, Action<string> onText,
        Action<string> onReasoning, CancellationToken cancellationToken);
}

/// <summary>Chat Completions transport with bounded responses and a separate stream inactivity timeout.</summary>
public sealed class HttpChatTransport : IChatTransport
{
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    public const int DefaultMaxResponseChars = 128_000;
    public static int MaxResponseChars { get; set; } = DefaultMaxResponseChars;

    public async Task<ModelReply> SendAsync(ModelConnection connection, JsonObject payload, Action<string> onText,
        Action<string> onReasoning, CancellationToken cancellationToken)
    {
        var effectiveEndpoint = connection.Endpoint;
        var effectiveKey = connection.ApiKey;

        if (connection.Provider == AIProvider.Player2)
        {
            var resolved = await Player2Support.ResolvePlayer2ConnectionAsync(connection.ApiKey, cancellationToken).ConfigureAwait(false);
            if (resolved != null)
            {
                effectiveEndpoint = resolved.Value.EndpointUrl;
                effectiveKey = resolved.Value.ApiKey;
            }
        }

        var payloadJson = payload.ToJsonString(Evidence.UnescapedJsonOptions);
        var idleTimeoutSec = Math.Clamp(connection.IdleSeconds > 0 ? connection.IdleSeconds : 60, 1, 600);
        var maxRespChars = connection.MaxResponseChars > 0 ? Math.Clamp(connection.MaxResponseChars, 8_000, 1_000_000) : int.MaxValue;
        var maxCallsPerRound = connection.MaxToolCallsPerRound > 0 ? Math.Clamp(connection.MaxToolCallsPerRound, 1, 32) : 0;

        HttpRequestMessage BuildRequest(string targetEndpoint, string targetKey, bool closeConn = false)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, targetEndpoint)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (closeConn) req.Headers.ConnectionClose = true;
            if (!string.IsNullOrWhiteSpace(targetKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetKey);

            var extraHeaders = connection.ExtraHeaders ?? AIProviderRegistry.GetExtraHeaders(connection.Provider);
            if (extraHeaders != null)
            {
                foreach (var kv in extraHeaders)
                {
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }
            return req;
        }

        using var request = BuildRequest(effectiveEndpoint, effectiveKey);
        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersTimeout.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSec));
        HttpResponseMessage response;
        try { response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when ((ex is HttpRequestException or IOException) && !cancellationToken.IsCancellationRequested)
        {
            using var retryTransportReq = BuildRequest(effectiveEndpoint, effectiveKey, closeConn: true);
            using var retryTransportTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            retryTransportTimeout.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSec));
            try
            {
                response = await Client.SendAsync(retryTransportReq, HttpCompletionOption.ResponseHeadersRead, retryTransportTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(UiText.Get("GCFAgent_TransportResponseTimeout"));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(UiText.Get("GCFAgent_TransportResponseTimeout")); }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && connection.Provider == AIProvider.Player2)
        {
            response.Dispose();
            Player2Support.InvalidateLocalKeyCache();
            var refreshed = await Player2Support.TryGetLocalConnectionAsync(cancellationToken, forceRefreshKey: true).ConfigureAwait(false);
            if (refreshed.HasValue)
            {
                effectiveEndpoint = refreshed.Value.EndpointUrl;
                effectiveKey = refreshed.Value.ApiKey;
            }
            using var retryReq = BuildRequest(effectiveEndpoint, effectiveKey);
            using var retryHeadersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            retryHeadersTimeout.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSec));
            try { response = await Client.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, retryHeadersTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException(UiText.Get("GCFAgent_TransportResponseTimeout")); }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(UiText.Format("GCFAgent_TransportHttpError", ((int)response.StatusCode).ToString()), null, response.StatusCode);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (!(response.Content.Headers.ContentType?.MediaType ?? "").Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var body = new StringBuilder();
                var buffer = new char[4096];
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSec));
                    int count;
                    try { count = await reader.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { throw new TimeoutException(UiText.Get("GCFAgent_TransportIdleNoData")); }
                    if (count == 0) break;
                    body.Append(buffer, 0, count);
                    if (body.Length > maxRespChars) throw new InvalidDataException(UiText.Get("GCFAgent_TransportResponseTooLong"));
                }
                var nonStreamReply = ParseJson(body.ToString(), onText, onReasoning, maxCallsPerRound);
                return EnsureUsagePopulated(nonStreamReply, payloadJson);
            }

            var parser = new ChatStreamParser(onText, onReasoning, maxCallsPerRound);
            var eventData = new StringBuilder();
            var total = 0;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSec));
                string? line;
                try { line = await ReadBoundedLineAsync(reader, idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new TimeoutException(UiText.Get("GCFAgent_TransportStreamIdle")); }
                if (line == null)
                {
                    if (eventData.Length > 0) parser.Accept(eventData.ToString());
                    break;
                }
                total += line.Length;
                if (total > maxRespChars) throw new InvalidDataException(UiText.Get("GCFAgent_TransportStreamTooLong"));
                if (line.Length == 0)
                {
                    if (eventData.Length == 0) continue;
                    parser.Accept(eventData.ToString());
                    eventData.Clear();
                    if (parser.Done) break;
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (eventData.Length > 0) eventData.Append('\n');
                    eventData.Append(line.AsSpan(5).TrimStart());
                }
            }
            var streamReply = parser.Complete();
            return EnsureUsagePopulated(streamReply, payloadJson);
        }
    }

    private static ModelReply EnsureUsagePopulated(ModelReply reply, string requestPayloadJson)
    {
        if (reply.Usage != null && reply.Usage.TotalTokens > 0)
        {
            return reply;
        }

        var promptTokens = TokenEstimator.EstimateTokens(requestPayloadJson);
        var completionTokens = TokenEstimator.EstimateTokens(reply.Text);
        foreach (var call in reply.Tools)
        {
            completionTokens += TokenEstimator.EstimateTokens(call.Name) + TokenEstimator.EstimateTokens(call.Arguments) + 4;
        }

        return reply with
        {
            Usage = new TokenUsage(
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                TotalTokens: promptTokens + completionTokens,
                IsEstimated: true,
                CachedPromptTokens: 0,
                PeakContextTokens: promptTokens,
                LlmCallCount: 1)
        };
    }

    public const int MaxSseEventLineChars = 2_097_152;

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        var one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            line.Append(one[0]);
            if (line.Length > MaxSseEventLineChars) throw new InvalidDataException(UiText.Get("GCFAgent_TransportEventLineTooLong"));
        }
        return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
    }

    public static ModelReply ParseJson(string json, Action<string> onText, Action<string> onReasoning, int maxCallsPerRound = 0)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var parser = new ChatStreamParser(onText, onReasoning, maxCallsPerRound);
        parser.ReadUsage(root);
        parser.ReadMessage(choice.GetProperty("message"), streaming: false);
        parser.FinishReason = choice.GetProperty("finish_reason").GetString() ?? "";
        return parser.Complete();
    }
}

/// <summary>Malformed or incomplete responses are failures, never successful partial answers.</summary>
public sealed class ChatStreamParser(Action<string> onText, Action<string> onReasoning, int maxCallsPerRound = 0)
{
    private sealed class Call { public string Id = ""; public string Name = ""; public StringBuilder Arguments = new(); }
    private readonly SortedDictionary<int, Call> _calls = new();
    private readonly Dictionary<int, int> _streamIndexRemap = new();
    private readonly StringBuilder _text = new();
    private readonly int _maxCallsLimit = maxCallsPerRound > 0 ? Math.Clamp(maxCallsPerRound, 1, 32) : int.MaxValue;
    private TokenUsage? _usage;
    private string _responseModel = "";
    public string FinishReason { get; set; } = "";
    public bool Done { get; private set; }

    public void Accept(string data)
    {
        if (data.Trim() == "[DONE]") { Done = true; return; }
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _)) throw new InvalidDataException(UiText.Get("GCFAgent_TransportStreamError"));
        ReadUsage(root);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind == JsonValueKind.Null) return;
        if (choices.ValueKind != JsonValueKind.Array) throw new InvalidDataException(UiText.Get("GCFAgent_TransportChoicesMustBeArray"));
        if (choices.GetArrayLength() == 0) return;
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            FinishReason = finish.GetString() ?? "";
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object) ReadMessage(delta, streaming: true);
    }

    public void ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(model.GetString()))
            _responseModel = model.GetString()!;
        if (!root.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var prompt = TryGetInt(usageEl, "prompt_tokens") ?? TryGetInt(usageEl, "input_tokens") ?? 0;
        var completion = TryGetInt(usageEl, "completion_tokens") ?? TryGetInt(usageEl, "output_tokens") ?? 0;
        var total = TryGetInt(usageEl, "total_tokens") ?? (prompt + completion);

        // Extract cached prompt tokens across OpenAI, Gemini, DeepSeek, Anthropic, OpenRouter & CLIProxyAPI formats
        var cached = 0;
        if (usageEl.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            cached = TryGetInt(details, "cached_tokens") ?? TryGetInt(details, "cache_read_tokens") ?? 0;
        }
        if (cached <= 0)
        {
            cached = TryGetInt(usageEl, "cached_tokens")
                     ?? TryGetInt(usageEl, "prompt_cache_hit_tokens")
                     ?? TryGetInt(usageEl, "cache_read_input_tokens")
                     ?? TryGetInt(usageEl, "cache_read_tokens")
                     ?? 0;
        }

        if (total > 0 || prompt > 0 || completion > 0)
        {
            var prevPrompt = _usage?.PromptTokens ?? 0;
            var prevComp = _usage?.CompletionTokens ?? 0;
            var prevCached = _usage?.CachedPromptTokens ?? 0;

            var finalPrompt = prompt > 0 ? prompt : prevPrompt;
            var finalComp = completion > 0 ? completion : prevComp;
            var finalCached = Math.Clamp(cached > 0 ? cached : prevCached, 0, Math.Max(0, finalPrompt));
            var finalTotal = total > 0 ? total : (finalPrompt + finalComp);

            _usage = new TokenUsage(
                PromptTokens: finalPrompt,
                CompletionTokens: finalComp,
                TotalTokens: finalTotal,
                IsEstimated: false,
                CachedPromptTokens: finalCached,
                PeakContextTokens: finalPrompt,
                LlmCallCount: 1);
        }
    }

    private static int? TryGetInt(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var val) ? val : null;

    public void ReadMessage(JsonElement message, bool streaming)
    {
        if (message.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
        { var chunk = text.GetString()!; _text.Append(chunk); onText(chunk); }
        if (message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            onReasoning(reasoning.GetString()!);
        if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind == JsonValueKind.Null) return;
        if (calls.ValueKind != JsonValueKind.Array) throw new InvalidDataException(UiText.Get("GCFAgent_TransportToolCallsMustBeArray"));
        var position = 0;
        foreach (var value in calls.EnumerateArray())
        {
            var rawIndex = streaming && value.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number && idxEl.TryGetInt32(out var parsedIdx)
                ? parsedIdx
                : position++;
            if (rawIndex < 0) rawIndex = 0;

            var incomingId = value.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? (idEl.GetString() ?? "").Trim()
                : "";
            var incomingName = "";
            var incomingArgs = "";
            if (value.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
            {
                if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    incomingName = nameEl.GetString() ?? "";
                if (fn.TryGetProperty("arguments", out var argsEl))
                {
                    if (argsEl.ValueKind == JsonValueKind.String)
                        incomingArgs = argsEl.GetString() ?? "";
                    else if (argsEl.ValueKind == JsonValueKind.Object)
                        incomingArgs = argsEl.GetRawText();
                }
            }

            if (!_streamIndexRemap.TryGetValue(rawIndex, out var mappedSlot))
            {
                mappedSlot = rawIndex;
            }

            // Stable IDs take precedence. A function name is allowed to arrive in fragments.
            var known = incomingId.Length > 0
                ? _calls.FirstOrDefault(kv => string.Equals(kv.Value.Id, incomingId, StringComparison.Ordinal))
                : default;
            if (known.Value != null)
            {
                mappedSlot = known.Key;
                _streamIndexRemap[rawIndex] = mappedSlot;
            }
            else if (_calls.TryGetValue(mappedSlot, out var existingCall))
            {
                var differentId = incomingId.Length > 0 && existingCall.Id.Length > 0 &&
                    !string.Equals(existingCall.Id, incomingId, StringComparison.Ordinal);
                // Shared-index gateways begin another function with a new ID/name, or after complete arguments.
                var startsNext = (differentId && incomingName.Length > 0) ||
                    (incomingName.Length > 0 && IsCompleteJsonObject(existingCall.Arguments) &&
                     !string.Equals(existingCall.Name, incomingName, StringComparison.Ordinal));
                if (startsNext)
                {
                    mappedSlot = _calls.Keys.Max() + 1;
                    _streamIndexRemap[rawIndex] = mappedSlot;
                }
            }

            if (!_calls.TryGetValue(mappedSlot, out var call))
            {
                if (_calls.Count >= _maxCallsLimit || mappedSlot >= _maxCallsLimit)
                    throw new InvalidDataException(UiText.Format("GCFAgent_TransportTooManyToolCalls", _maxCallsLimit.ToString()));
                _calls[mappedSlot] = call = new();
                _streamIndexRemap[rawIndex] = mappedSlot;
            }

            if (incomingId.Length > 0 && call.Id.Length == 0)
            {
                call.Id = incomingId;
            }

            if (incomingName.Length > 0)
            {
                if (!(call.Name.Length > 0 && call.Arguments.Length == 0 && string.Equals(call.Name, incomingName, StringComparison.Ordinal)))
                {
                    call.Name += incomingName;
                }
            }

            if (incomingArgs.Length > 0)
            {
                if (call.Arguments.ToString().Trim() == "{}" && incomingArgs.TrimStart().StartsWith('{'))
                {
                    call.Arguments.Clear();
                }
                call.Arguments.Append(incomingArgs);
            }
        }
    }

    private static bool IsCompleteJsonObject(StringBuilder sb)
    {
        if (sb.Length < 2) return false;
        var s = sb.ToString().Trim();
        if (!s.StartsWith('{') || !s.EndsWith('}')) return false;
        try
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    public ModelReply Complete()
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var builtCalls = new List<ModelToolCall>(_calls.Count);
        var seq = 1;
        foreach (var c in _calls.Values)
        {
            if (string.IsNullOrWhiteSpace(c.Name) && c.Arguments.Length == 0 && string.IsNullOrWhiteSpace(c.Id))
                continue;
            var id = string.IsNullOrWhiteSpace(c.Id) ? $"call_{seq}" : c.Id.Trim();
            while (!usedIds.Add(id))
            {
                seq++;
                id = $"{(string.IsNullOrWhiteSpace(c.Id) ? "call" : c.Id.Trim())}_{seq}";
            }
            seq++;
            var rawArgs = c.Arguments.ToString().Trim();
            if (string.IsNullOrEmpty(rawArgs))
                rawArgs = "{}";
            builtCalls.Add(new ModelToolCall(id, c.Name.Trim(), rawArgs));
        }
        var calls = builtCalls.ToArray();

        if (calls.Length > 0 && (FinishReason is "stop" or "end_turn" or "tool_calls" or "function_call" or "tool_call" || (Done && string.IsNullOrEmpty(FinishReason))))
        {
            FinishReason = "tool_calls";
        }
        else if (calls.Length == 0 && (FinishReason is "end_turn" or "stop_sequence" || (Done && string.IsNullOrEmpty(FinishReason) && _text.Length > 0)))
        {
            FinishReason = "stop";
        }

        if (FinishReason is not ("stop" or "tool_calls"))
            throw new InvalidDataException(FinishReason == "length" ? UiText.Get("GCFAgent_TransportLengthTruncated") : UiText.Get("GCFAgent_TransportAbnormalFinish"));
        if ((FinishReason == "tool_calls") != (calls.Length > 0)) throw new InvalidDataException(UiText.Get("GCFAgent_TransportFinishMismatch"));
        foreach (var call in calls)
        {
            if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name)) throw new InvalidDataException(UiText.Get("GCFAgent_TransportToolMissingIdOrName"));
            using var args = JsonDocument.Parse(call.Arguments);
            if (args.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException(UiText.Get("GCFAgent_TransportArgsMustBeObject"));
        }
        if (calls.Select(c => c.Id).Distinct().Count() != calls.Length) throw new InvalidDataException(UiText.Get("GCFAgent_TransportDuplicateToolCallId"));
        return new ModelReply(_text.ToString(), FinishReason, calls, _usage, _responseModel);
    }
}
