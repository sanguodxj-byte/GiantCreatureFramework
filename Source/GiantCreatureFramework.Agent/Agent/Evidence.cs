using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GiantCreatureFramework.Agent;

/// <summary>Evidence is data, including explicit failure, truncation and acquisition identity.</summary>
public static class Evidence
{
    public const int DefaultMaxResultChars = 0;
    public static int MaxResultChars { get; set; } = DefaultMaxResultChars;

    /// <summary>
    /// Prevents System.Text.Json from escaping CJK characters into 6-byte \uXXXX sequences.
    /// </summary>
    public static readonly JsonSerializerOptions UnescapedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex Credential = new(
        @"api.?key|p2.?key|(?:^|(?:access|auth|refresh|bearer|session|api|client|secret|private|security)_?)token$|password|passwd|secret|authorization|credential|connectionstring",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSensitiveName(string name) => Credential.IsMatch(name);

    public static string Redact(string text, string secret)
        => string.IsNullOrEmpty(secret) ? text : text.Replace(secret, AgentProtocol.Get("Evidence_Redacted"), StringComparison.Ordinal);

    public static string Wrap(string tool, string raw, string scope, int mapId, int tick, string[] targetIds, long duration,
        string secret, string? failure = null, int? maxResultCharsOverride = null)
    {
        var configured = maxResultCharsOverride ?? MaxResultChars;
        var limit = configured > 0 ? Math.Clamp(configured, 1_000, 200_000) : int.MaxValue;
        var node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { MaxDepth = 48 });
        Scrub(node, secret);
        var error = failure ?? (node is JsonObject obj && obj["error"] != null ? obj["error"]!.ToString() : null);
        var serialized = node?.ToJsonString(UnescapedJsonOptions) ?? "null";
        var truncated = serialized.Length > limit;
        if (truncated)
        {
            Reduce(node);
            serialized = node?.ToJsonString(UnescapedJsonOptions) ?? "null";
            if (serialized.Length > limit)
                node = new JsonObject { ["preview"] = serialized[..limit], ["note"] = AgentProtocol.Get("Evidence_BudgetTruncatedNote") };
        }
        return new JsonObject
        {
            ["evidenceId"] = "ev_" + Guid.NewGuid().ToString("N"),
            ["tool"] = tool, ["status"] = error == null ? "ok" : "error", ["error"] = error,
            ["scopeId"] = scope, ["mapId"] = mapId, ["gameTick"] = tick,
            ["selectedObjectIds"] = JsonSerializer.SerializeToNode(targetIds, UnescapedJsonOptions),
            ["capturedAtUtc"] = DateTime.UtcNow.ToString("O"), ["durationMs"] = duration,
            ["truncated"] = truncated, ["data"] = node
        }.ToJsonString(UnescapedJsonOptions);
    }

    private static void Reduce(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var keys = obj.Select(p => p.Key).ToArray();
            foreach (var key in keys.Take(80))
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 1000)
                    obj[key] = text[..1000] + AgentProtocol.Get("Evidence_OmittedSuffix");
                else Reduce(obj[key]);
            }
            if (keys.Length > 80)
            {
                foreach (var key in keys.Skip(80)) obj.Remove(key);
                obj["_omittedFieldCount"] = keys.Length - 80;
            }
        }
        else if (node is JsonArray array)
        {
            var count = array.Count;
            while (array.Count > 12) array.RemoveAt(array.Count - 1);
            foreach (var child in array) Reduce(child);
            if (count > 12) array.Add(new JsonObject { ["omitted"] = true, ["count"] = count - 12 });
        }
    }

    private static void Scrub(JsonNode? node, string secret)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
                if (IsSensitiveName(key)) obj[key] = AgentProtocol.Get("Evidence_Redacted");
                else if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) obj[key] = Redact(text, secret);
                else Scrub(obj[key], secret);
        }
        else if (node is JsonArray arr)
            for (var i = 0; i < arr.Count; i++)
                if (arr[i] is JsonValue value && value.TryGetValue<string>(out var text)) arr[i] = Redact(text, secret);
                else Scrub(arr[i], secret);
    }
}
