using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent;

/// <summary>Maps concise model-facing references and exact call IDs to locally retained evidence. Never guesses a source.</summary>
public sealed class EvidenceContext
{
    private sealed record Entry(string Id, string Handle, JsonObject Result, string Tool, string Arguments, bool Historical);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _aliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foldedCalls = new(StringComparer.Ordinal);

    public void Observe(JsonArray messages)
    {
        var calls = messages.OfType<JsonObject>().Where(m => m["tool_calls"] is JsonArray)
            .SelectMany(m => m["tool_calls"]!.AsArray().OfType<JsonObject>()).ToArray();
        foreach (var message in messages.OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool"))
        {
            var callId = message["tool_call_id"]?.ToString() ?? "";
            var call = calls.LastOrDefault(c => c["id"]?.ToString() == callId);
            Register(message["content"]?.ToString() ?? "{}", callId,
                call?["function"]?["name"]?.ToString() ?? "", call?["function"]?["arguments"]?.ToString() ?? "{}");
        }
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i] as JsonObject;
            if (message?["role"]?.ToString() != "tool") continue;
            var body = JsonNode.Parse(message["content"]?.ToString() ?? "{}");
            if (body?["tool"]?.ToString() == "fold_evidence" && body["status"]?.ToString() == "ok" && body["foldedEvidenceIds"] is JsonArray ids)
                MarkFolded(messages.Take(i), ids.Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal));
        }
    }

    public void Register(string json, string callId, string tool, string arguments)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root == null || !EvidenceQuality.HasUsableResult(json)) return;
        var historical = root["historicalSnapshot"]?.ToString() == "true";
        var obj = historical ? root["evidence"] as JsonObject : root;
        if (obj == null) return;
        if (obj["tool"]?.ToString() == "fold_evidence") return;
        var id = obj["evidenceId"]?.ToString() ?? callId;
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!_entries.TryGetValue(id, out var entry))
        {
            entry = new Entry(id, "E" + (_entries.Count + 1), (JsonObject)obj.DeepClone(), tool, arguments, historical);
            _entries.Add(id, entry);
        }
        Alias(id, id); Alias(entry.Handle, id); Alias(callId, id);
        if (root["originalCallId"]?.ToString() is { Length: > 0 } original) Alias(original, id);
    }

    private void Alias(string alias, string id)
    {
        if (string.IsNullOrWhiteSpace(alias)) return;
        if (!_aliases.TryGetValue(alias, out var values)) _aliases[alias] = values = new(StringComparer.Ordinal);
        values.Add(id);
    }

    private string? Resolve(string reference)
    {
        var value = reference.Trim();
        return _aliases.TryGetValue(value, out var ids) && ids.Count == 1 ? ids.Single() : null;
    }

    public SubmittedAnswer Validate(SubmittedAnswer draft)
    {
        var ids = draft.EvidenceIds.Select(id => Resolve(id) ?? id).Distinct(StringComparer.Ordinal).ToArray();
        return AnswerContract.Validate(JsonSerializer.Serialize(new { answer = draft.Text, kind = draft.Kind, evidenceIds = ids }),
            _entries.Keys.ToHashSet(StringComparer.Ordinal));
    }

    public JsonArray Catalog() => new(_entries.Values.Select(e => (JsonNode)new JsonObject
        { ["evidenceId"] = e.Handle, ["tool"] = e.Tool, ["arguments"] = JsonNode.Parse(e.Arguments), ["historicalSnapshot"] = e.Historical }).ToArray());

    public static JsonObject ReadSchema() => new()
    {
        ["type"] = "function", ["function"] = new JsonObject
        {
            ["name"] = "read_evidence", ["description"] = AgentProtocol.Get("Tool_ReadEvidence_Desc"),
            ["parameters"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject {
                    ["evidenceId"] = new JsonObject { ["type"] = "string" },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = AgentProtocol.Get("Tool_ReadEvidence_PathDesc") },
                    ["paths"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = AgentProtocol.Get("Tool_ReadEvidence_PathsDesc") },
                    ["startIndex"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = AgentProtocol.Get("Tool_ReadEvidence_StartIndexDesc") },
                    ["maxItems"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = AgentProtocol.Get("Tool_ReadEvidence_MaxItemsDesc") },
                    ["startLine"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                    ["maxLines"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 } },
                ["required"] = new JsonArray("evidenceId") }
        }
    };

    public static JsonObject FoldSchema() => new()
    {
        ["type"] = "function", ["function"] = new JsonObject
        {
            ["name"] = "fold_evidence", ["description"] = AgentProtocol.Get("Tool_FoldEvidence_Desc"),
            ["parameters"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["evidenceIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    ["notes"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("evidenceIds", "notes") }
        }
    };

    public string Fold(string arguments, JsonArray messages)
    {
        var args = JsonNode.Parse(arguments) as JsonObject ?? throw new InvalidDataException(AgentProtocol.Get("Evidence_ArgsMustBeObject"));
        var notes = args["notes"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(notes) || args["evidenceIds"] is not JsonArray ids || ids.Count == 0)
            throw new InvalidDataException(AgentProtocol.Get("Evidence_FoldRequiresNotesAndIds"));
        var canonical = ids.Select(n => Resolve(n?.GetValue<string>() ?? "") ?? throw new InvalidDataException(AgentProtocol.Get("Evidence_FoldInvalidId"))).ToHashSet(StringComparer.Ordinal);
        MarkFolded(messages, canonical);
        return new JsonObject { ["status"] = "ok", ["tool"] = "fold_evidence", ["modelNotes"] = true,
            ["notes"] = notes, ["evidenceIds"] = new JsonArray(canonical.Select(id => (JsonNode?)JsonValue.Create(_entries[id].Handle)).ToArray()),
            ["foldedEvidenceIds"] = new JsonArray(canonical.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["originalsRetained"] = true }.ToJsonString(Evidence.UnescapedJsonOptions);
    }

    private void MarkFolded(IEnumerable<JsonNode?> messages, IReadOnlySet<string> ids)
    {
        foreach (var message in messages.OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool"))
        {
            var body = JsonNode.Parse(message["content"]?.ToString() ?? "{}");
            var obj = body?["historicalSnapshot"]?.ToString() == "true" ? body["evidence"] : body;
            var id = Resolve(obj?["evidenceId"]?.ToString() ?? "");
            if (id != null && ids.Contains(id)) _foldedCalls.Add(message["tool_call_id"]?.ToString() + "\n" + message["content"]?.ToString());
        }
    }

    public string Read(string arguments)
    {
        var args = JsonNode.Parse(arguments) as JsonObject ?? throw new InvalidDataException(AgentProtocol.Get("Evidence_ArgsMustBeObject"));
        var id = Resolve(args["evidenceId"]?.GetValue<string>() ?? "");
        if (id == null || !_entries.TryGetValue(id, out var entry)) throw new InvalidDataException(AgentProtocol.Get("Evidence_ReadNotFound"));
        var path = args["path"]?.GetValue<string>() ?? "";
        if (args["paths"] is JsonArray paths)
        {
            if (path.Length > 0 || paths.Count == 0) throw new InvalidDataException(AgentProtocol.Get("Evidence_PathOrPathsExclusive"));
            var pages = new JsonArray();
            foreach (var item in paths)
            {
                var pointer = item?.GetValue<string>() ?? throw new InvalidDataException(AgentProtocol.Get("Evidence_PathsMustBeStringArray"));
                var selection = (JsonObject)args.DeepClone(); selection.Remove("paths"); selection["path"] = pointer;
                try { pages.Add(JsonNode.Parse(Read(selection.ToJsonString(Evidence.UnescapedJsonOptions)))!["data"]!.DeepClone()); }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
                { pages.Add(new JsonObject { ["path"] = pointer, ["error"] = ex.Message }); }
            }
            var success = pages.Any(p => p?["error"] == null);
            var response = new JsonObject { ["status"] = success ? "ok" : "error", ["evidenceId"] = entry.Id, ["tool"] = "read_evidence",
                ["historicalSource"] = entry.Historical, ["data"] = new JsonObject { ["pages"] = pages } };
            if (!success) response["error"] = AgentProtocol.Get("Evidence_AllPathsFailed");
            return response.ToJsonString(Evidence.UnescapedJsonOptions);
        }
        JsonNode? node = entry.Result;
        if (path.Length > 0)
        {
            if (!path.StartsWith('/')) throw new InvalidDataException(AgentProtocol.Get("Evidence_PathMustBePointer"));
            foreach (var raw in path[1..].Split('/'))
            {
                var key = raw.Replace("~1", "/").Replace("~0", "~");
                if (node is JsonObject obj && obj.TryGetPropertyValue(key, out var value)) node = value;
                else if (node is JsonArray array && int.TryParse(key, out var index) && index >= 0 && index < array.Count) node = array[index];
                else throw new InvalidDataException(AgentProtocol.Get("Evidence_PathMissing"));
            }
        }
        int? totalItems = null, nextIndex = null;
        if (args.ContainsKey("startIndex") || args.ContainsKey("maxItems"))
        {
            if (node is not JsonArray items) throw new InvalidDataException(AgentProtocol.Get("Evidence_ArrayPagingOnlyOnArray"));
            var offset = args["startIndex"]?.GetValue<int>() ?? 0;
            var take = args["maxItems"]?.GetValue<int>() ?? 0;
            if (offset < 0 || offset > items.Count || take < 0) throw new InvalidDataException(AgentProtocol.Get("Evidence_ArrayPagingOutOfRange"));
            totalItems = items.Count;
            var countItems = take == 0 ? items.Count - offset : Math.Min(take, items.Count - offset);
            nextIndex = offset + countItems < items.Count ? offset + countItems : null;
            node = new JsonArray(items.Skip(offset).Take(countItems).Select(n => n?.DeepClone()).ToArray());
        }
        var text = node is JsonValue scalar && scalar.TryGetValue<string>(out var str) ? str
            : node?.ToJsonString(new JsonSerializerOptions(Evidence.UnescapedJsonOptions) { WriteIndented = true }) ?? "null";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = args["startLine"]?.GetValue<int>() ?? 1;
        var count = args["maxLines"]?.GetValue<int>() ?? (totalItems.HasValue ? 0 : 120);
        if (start < 1 || count < 0 || start > lines.Length) throw new InvalidDataException(AgentProtocol.Get("Evidence_LinePagingOutOfRange"));
        var returned = count == 0 ? lines.Length - start + 1 : Math.Min(count, lines.Length - start + 1);
        var page = new JsonObject { ["path"] = path, ["startLine"] = start,
                ["totalLines"] = lines.Length, ["returnedLines"] = returned,
                ["nextLine"] = start + returned <= lines.Length ? start + returned : null,
                ["text"] = string.Join("\n", lines.Skip(start - 1).Take(returned)) };
        if (totalItems.HasValue)
        { page["totalItems"] = totalItems.Value; page["startIndex"] = args["startIndex"]?.DeepClone() ?? JsonValue.Create(0); page["nextIndex"] = nextIndex; }
        return new JsonObject { ["status"] = "ok", ["evidenceId"] = entry.Id, ["tool"] = "read_evidence",
            ["historicalSource"] = entry.Historical, ["data"] = page }.ToJsonString(Evidence.UnescapedJsonOptions);
    }

    public JsonArray Project(JsonArray messages)
    {
        var projected = (JsonArray)messages.DeepClone();
        var seen = new Dictionary<string, (string Id, string Path)>(StringComparer.Ordinal);
        var confirmedNotes = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var message in projected.OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool"))
        {
            var root = JsonNode.Parse(message["content"]?.ToString() ?? "{}") as JsonObject;
            var body = root?["historicalSnapshot"]?.ToString() == "true" ? root["evidence"] as JsonObject : root;
            if (body?["tool"]?.ToString() == "fold_evidence" && body["status"]?.ToString() == "ok" && body["notes"] is { } notes)
                confirmedNotes[message["tool_call_id"]!.ToString()] = notes;
        }
        foreach (var message in projected.OfType<JsonObject>())
        {
            if (message["role"]?.ToString() == "assistant" && message["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls.OfType<JsonObject>().Where(c => c["function"]?["name"]?.ToString() == "fold_evidence"))
                {
                    if (!confirmedNotes.TryGetValue(call["id"]!.ToString(), out var notes)) continue;
                    try
                    {
                        var args = JsonNode.Parse(call["function"]!["arguments"]!.ToString()) as JsonObject;
                        if (args != null && JsonNode.DeepEquals(args["notes"], notes))
                        {
                            args["notes"] = AgentProtocol.Get("Evidence_FoldedArgsMarker");
                            call["function"]!["arguments"] = args.ToJsonString(Evidence.UnescapedJsonOptions);
                        }
                    }
                    catch (JsonException) { }
                }
                foreach (var call in calls.OfType<JsonObject>().Where(c => c["function"]?["name"]?.ToString() == AnswerContract.ToolName))
                {
                    try
                    {
                        var args = JsonNode.Parse(call["function"]!["arguments"]!.ToString()) as JsonObject;
                        if (args != null && args.ContainsKey("answer"))
                        {
                            args["answer"] = AgentProtocol.Get("Evidence_DraftRetainedMarker");
                            call["function"]!["arguments"] = args.ToJsonString(Evidence.UnescapedJsonOptions);
                        }
                    }
                    catch (JsonException) { }
                }
            }
            if (message["role"]?.ToString() != "tool") continue;
            var json = message["content"]?.ToString() ?? "{}";
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null) continue;
            var historical = root["historicalSnapshot"]?.ToString() == "true";
            var obj = historical ? root["evidence"] as JsonObject : root;
            if (obj == null) continue;
            if (obj["tool"]?.ToString() == "fold_evidence")
            {
                var notes = (JsonObject)obj.DeepClone(); notes.Remove("foldedEvidenceIds");
                if (notes["evidenceIds"] is JsonArray)
                {
                    if (obj["foldedEvidenceIds"] is JsonArray canonical)
                        notes["evidenceIds"] = new JsonArray(canonical.Select(n => Resolve(n!.ToString()) is { } id ? (JsonNode?)JsonValue.Create(_entries[id].Handle) : n.DeepClone()).ToArray());
                }
                notes.Remove("evidenceId");
                message["content"] = historical ? new JsonObject { ["historicalSnapshot"] = true, ["modelNotes"] = notes }.ToJsonString(Evidence.UnescapedJsonOptions)
                    : notes.ToJsonString(Evidence.UnescapedJsonOptions);
                continue;
            }
            var original = obj["evidenceId"]?.ToString() ?? message["tool_call_id"]?.ToString() ?? "";
            var id = Resolve(original);
            if (id == null || !_entries.TryGetValue(id, out var entry)) continue;
            var result = new JsonObject { ["evidenceId"] = entry.Handle, ["status"] = "ok", ["tool"] = entry.Tool };
            var folded = _foldedCalls.Contains(message["tool_call_id"]?.ToString() + "\n" + json);
            if (historical || folded)
            {
                result["arguments"] = JsonNode.Parse(entry.Arguments);
                if (!folded) result["summary"] = ShallowSummary(entry.Result["data"]);
                result["contentStoredLocally"] = true;
                result["readWith"] = "read_evidence";
                if (folded) result["foldedByAgent"] = true;
            }
            else
            {
                foreach (var pair in obj)
                {
                    if (pair.Key is "evidenceId" or "tool" or "durationMs" or "selectedObjectIds" or "capturedAtUtc") continue;
                    result[pair.Key] = pair.Value?.DeepClone();
                }
                if (result["data"] is { } data && entry.Tool != "read_evidence" && obj["tool"]?.ToString() != "read_evidence")
                {
                    var reduced = Deduplicate(data, entry.Handle, "/data", seen);
                    var view = IsExplicitRead(entry.Tool, entry.Arguments)
                        ? reduced.DeepClone() : EvidencePreview.Render(reduced.DeepClone(), entry.Handle, "/data");
                    result["data"] = PackTables(view, "/data");
                }
            }
            if (historical)
                result = new JsonObject { ["historicalSnapshot"] = true, ["capturedAt"] = root["capturedAt"]?.DeepClone(), ["evidence"] = result };
            message["content"] = result.ToJsonString(Evidence.UnescapedJsonOptions);
        }
        return CollapseHistoricalPairs(projected);
    }

    private static JsonArray CollapseHistoricalPairs(JsonArray messages)
    {
        var historical = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var message in messages.OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool"))
        {
            var body = JsonNode.Parse(message["content"]?.ToString() ?? "{}") as JsonObject;
            if (body?["historicalSnapshot"]?.ToString() == "true") historical[message["tool_call_id"]!.ToString()] = body;
        }
        var collapsed = new JsonArray();
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["role"]?.ToString() == "assistant" && message["tool_calls"] is JsonArray calls && calls.Count > 0
                && calls.All(c => historical.ContainsKey(c!["id"]!.ToString())))
            {
                var entries = new JsonArray();
                JsonNode? captured = null;
                foreach (var call in calls)
                {
                    var id = call!["id"]!.ToString(); var body = historical[id]; removed.Add(id);
                    captured ??= body["capturedAt"]?.DeepClone();
                    if (body["modelNotes"] is { } notes) { entries.Add(new JsonObject { ["modelNotes"] = notes.DeepClone() }); continue; }
                    var evidence = body["evidence"];
                    var status = evidence?["status"]?.ToString() ?? "error";
                    var entry = new JsonObject { ["tool"] = call["function"]!["name"]!.ToString(),
                        ["arguments"] = JsonNode.Parse(call["function"]!["arguments"]!.ToString()), ["status"] = status };
                    if (status == "ok") entry["evidenceId"] = evidence?["evidenceId"]?.DeepClone();
                    else entry["error"] = evidence?["error"]?.DeepClone() ?? evidence?["note"]?.DeepClone();
                    entries.Add(entry);
                }
                collapsed.Add(new JsonObject { ["role"] = "assistant", ["content"] = new JsonObject {
                    ["historicalEvidenceIndex"] = entries, ["capturedAt"] = captured,
                    ["readWith"] = "read_evidence", ["note"] = AgentProtocol.Get("Evidence_HistoricalIndexNote") }.ToJsonString(Evidence.UnescapedJsonOptions) });
            }
            else if (message["role"]?.ToString() != "tool" || !removed.Contains(message["tool_call_id"]?.ToString() ?? ""))
                collapsed.Add(message.DeepClone());
        }
        return collapsed;
    }

    private static bool IsExplicitRead(string tool, string arguments)
    {
        if (tool is "read_mod_file" or "read_creature_xml" or "load_manual") return true;
        var args = JsonNode.Parse(arguments) as JsonObject;
        return tool switch
        {
            "get_def_details" => !string.IsNullOrWhiteSpace(args?["fields"]?.ToString()),
            "inspect_method_patches" => args?["targetQuery"]?.ToString() is { Length: > 0 } query && !query.Contains('*'),
            _ => false
        };
    }

    private static JsonNode? ShallowSummary(JsonNode? data)
    {
        if (data is not JsonObject obj) return JsonValue.Create(data is JsonArray a ? $"array ({a.Count})" : AgentProtocol.Get("Evidence_ShallowSummaryFull"));
        var summary = new JsonObject();
        foreach (var pair in obj)
            if (pair.Value is JsonValue value && value.ToJsonString(Evidence.UnescapedJsonOptions).Length <= 160)
                summary[pair.Key] = value.DeepClone();
        return summary;
    }

    private static JsonNode PackTables(JsonNode node, string path)
    {
        if (node is JsonObject reference && reference.ContainsKey("$evidenceRef")) return node;
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
                if (obj[key] is { } child)
                {
                    var packed = PackTables(child, path + "/" + key.Replace("~", "~0").Replace("/", "~1"));
                    if (!ReferenceEquals(packed, child)) obj[key] = packed;
                }
            return obj;
        }
        if (node is not JsonArray array) return node;
        for (var i = 0; i < array.Count; i++)
            if (array[i] is { } child)
            {
                var packed = PackTables(child, path + "/" + i);
                if (!ReferenceEquals(packed, child)) array[i] = packed;
            }
        if (array.Count < 2 || array[0] is not JsonObject first || first.Count == 0) return array;
        var keys = first.Select(p => p.Key).ToArray();
        if (array.Any(n => n is not JsonObject row || row.Count != keys.Length || keys.Any(k => !row.ContainsKey(k)))) return array;
        var shared = new JsonObject();
        var columns = new List<string>();
        foreach (var key in keys)
        {
            if (array.Skip(1).All(row => JsonNode.DeepEquals(first[key], row![key]))) shared[key] = first[key]?.DeepClone();
            else columns.Add(key);
        }
        var rows = new JsonArray(array.Select(row => (JsonNode)new JsonArray(columns.Select(k => row![k]?.DeepClone()).ToArray())).ToArray());
        var table = new JsonObject { ["$table"] = true, ["originalPath"] = path,
            ["columns"] = new JsonArray(columns.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["rows"] = rows };
        if (shared.Count > 0) table["shared"] = shared;
        return table.ToJsonString(Evidence.UnescapedJsonOptions).Length < array.ToJsonString(Evidence.UnescapedJsonOptions).Length ? table : array;
    }

    private static JsonNode Deduplicate(JsonNode data, string id, string path, Dictionary<string, (string Id, string Path)> seen)
    {
        var serialized = data.ToJsonString(Evidence.UnescapedJsonOptions);
        if (serialized.Length >= 256)
        {
            if (seen.TryGetValue(serialized, out var previous)) return new JsonObject { ["$evidenceRef"] = previous.Id, ["path"] = previous.Path };
            seen[serialized] = (id, path);
        }
        if (data is JsonObject obj)
            foreach (var key in obj.Select(p => p.Key).ToArray())
                if (obj[key] is { } child)
                {
                    var reduced = Deduplicate(child, id, path + "/" + key.Replace("~", "~0").Replace("/", "~1"), seen);
                    if (!ReferenceEquals(reduced, child)) obj[key] = reduced;
                }
        if (data is JsonArray array)
            for (var i = 0; i < array.Count; i++) if (array[i] is { } child)
            {
                var reduced = Deduplicate(child, id, path + "/" + i, seen);
                if (!ReferenceEquals(reduced, child)) array[i] = reduced;
            }
        return data;
    }
}
