using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent;

public sealed record AgentToolExecutionContext(
    string ScopeId,
    int MapId,
    int GameTick,
    string[] SelectedIds,
    string SecretToRedact);

public interface IAgentToolProvider
{
    IReadOnlyList<JsonObject> GetSchemas();
    bool CanExecute(string toolName);
    string? GetDisplayLabelKey(string toolName);
    int? GetMaxResultCharsOverride(string toolName) => null;
    Task<string> ExecuteAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Pluggable tool provider registry replacing RimCopilot's monolithic switch.
/// Validates provider registrations, rejects duplicate/malformed schemas, and wraps results in Evidence.
/// </summary>
public sealed class AgentToolProviderRegistry
{
    private readonly object _gate = new();
    private readonly List<IAgentToolProvider> _providers = new();
    private readonly Dictionary<string, (IAgentToolProvider Provider, JsonObject Schema)> _tools = new(StringComparer.Ordinal);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        ToolDiscovery.ToolName,
        AnswerContract.ToolName,
        "repair_answer",
        "read_evidence",
        "fold_evidence"
    };

    public void Register(IAgentToolProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var schemas = provider.GetSchemas() ?? throw new ArgumentException("Tool provider returned null schema list.", nameof(provider));
        if (schemas.Count == 0)
        {
            throw new ArgumentException("Tool provider must expose at least one tool schema.", nameof(provider));
        }

        var staged = new List<(string Name, JsonObject Schema)>(schemas.Count);
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in schemas)
        {
            if (raw == null || raw["type"]?.ToString() != "function" || raw["function"] is not JsonObject fn)
            {
                throw new ArgumentException("Each tool schema must have type='function' and a 'function' object.", nameof(provider));
            }
            var name = fn["name"]?.GetValue<string>()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Tool schema name cannot be empty.", nameof(provider));
            }
            if (ReservedNames.Contains(name))
            {
                throw new InvalidOperationException($"Tool name '{name}' is reserved by the protocol.");
            }
            if (!localNames.Add(name))
            {
                throw new InvalidOperationException($"Duplicate tool name '{name}' within the same provider.");
            }
            if (fn["parameters"] is not JsonObject paramObj || paramObj["type"]?.ToString() != "object")
            {
                throw new ArgumentException($"Tool '{name}' must declare an object parameters schema.", nameof(provider));
            }
            if (!provider.CanExecute(name))
            {
                throw new InvalidOperationException($"Provider declared schema for '{name}' but CanExecute('{name}') returned false.");
            }
            staged.Add((name, (JsonObject)raw.DeepClone()));
        }

        lock (_gate)
        {
            foreach (var (name, _) in staged)
            {
                if (_tools.ContainsKey(name))
                {
                    throw new InvalidOperationException($"Tool '{name}' is already registered by another provider.");
                }
            }

            _providers.Add(provider);
            foreach (var (name, schema) in staged)
            {
                _tools[name] = (provider, schema);
            }
        }
    }

    public JsonArray BuildOpenAiToolsSchema()
    {
        lock (_gate)
        {
            var result = new JsonArray();
            foreach (var (_, schema) in _tools.Values)
            {
                result.Add(schema.DeepClone());
            }
            return result;
        }
    }

    public string GetToolDisplayLabel(string toolName, string argumentsJson)
    {
        string? labelKey = null;
        lock (_gate)
        {
            if (_tools.TryGetValue(toolName, out var entry))
            {
                labelKey = entry.Provider.GetDisplayLabelKey(toolName);
            }
        }

        var baseLabel = toolName switch
        {
            ToolDiscovery.ToolName => UiText.Get("GCFAgent_ToolLabel_SelectInvestigation"),
            "read_evidence" => UiText.Get("GCFAgent_ToolLabel_ReadEvidence"),
            "fold_evidence" => UiText.Get("GCFAgent_ToolLabel_FoldEvidence"),
            _ => !string.IsNullOrWhiteSpace(labelKey) ? UiText.Get(labelKey!) : toolName
        };

        var summary = ExtractFirstArgPreview(argumentsJson);
        return string.IsNullOrWhiteSpace(summary) ? baseLabel : UiText.Format("GCFAgent_ToolLabelWithSummary", baseLabel, summary);
    }

    public async Task<(string ResultJson, long DurationMs)> ExecuteAsync(
        string toolName,
        string argumentsJson,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        IAgentToolProvider? provider = null;
        JsonObject? schema = null;
        lock (_gate)
        {
            if (_tools.TryGetValue(toolName, out var found))
            {
                provider = found.Provider;
                schema = found.Schema;
            }
        }

        try
        {
            if (provider == null || schema == null)
            {
                throw new ArgumentException(AgentProtocol.Get("Registry_UnknownToolPrefix") + toolName);
            }

            using var doc = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(argumentsJson);
            ToolArgumentValidation.Validate(schema["function"]!["parameters"]!, doc.RootElement);

            var raw = await provider.ExecuteAsync(toolName, doc.RootElement, cancellationToken).ConfigureAwait(false);
            string? attachedUrl = null;
            string? attachedLabel = null;
            if (JsonNode.Parse(raw) is JsonObject rawObj && rawObj["_attachedImageDataUrl"] != null)
            {
                attachedUrl = rawObj["_attachedImageDataUrl"]?.GetValue<string>();
                attachedLabel = rawObj["_attachedImageLabel"]?.GetValue<string>();
                rawObj.Remove("_attachedImageDataUrl");
                rawObj.Remove("_attachedImageLabel");
                raw = rawObj.ToJsonString(Evidence.UnescapedJsonOptions);
            }

            var wrapped = Evidence.Wrap(
                toolName,
                raw,
                context.ScopeId,
                context.MapId,
                context.GameTick,
                context.SelectedIds,
                watch.ElapsedMilliseconds,
                context.SecretToRedact,
                maxResultCharsOverride: provider.GetMaxResultCharsOverride(toolName));

            if (!string.IsNullOrEmpty(attachedUrl) && JsonNode.Parse(wrapped) is JsonObject wrappedObj)
            {
                wrappedObj["_attachedImageDataUrl"] = attachedUrl;
                wrappedObj["_attachedImageLabel"] = attachedLabel ?? toolName;
                wrapped = wrappedObj.ToJsonString(Evidence.UnescapedJsonOptions);
            }

            return (wrapped, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errRaw = JsonSerializer.Serialize(new { error = ex.Message }, Evidence.UnescapedJsonOptions);
            return (Evidence.Wrap(
                toolName,
                errRaw,
                context.ScopeId,
                context.MapId,
                context.GameTick,
                context.SelectedIds,
                watch.ElapsedMilliseconds,
                context.SecretToRedact), watch.ElapsedMilliseconds);
        }
    }

    private static string ExtractFirstArgPreview(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name is "xmlContent" or "prompt") continue;
                    var val = prop.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(val)) parts.Add($"{prop.Name}={val}");
                    if (parts.Count >= 2) break;
                }
                var joined = string.Join(", ", parts);
                return joined.Length > 48 ? joined[..48] + "..." : joined;
            }
        }
        catch
        {
            // Ignore preview parse failures
        }
        return string.Empty;
    }
}

public static class AgentToolRegistry
{
    public static AgentToolProviderRegistry Default { get; private set; } = new();

    public static void ResetForTesting(AgentToolProviderRegistry? replacement = null) =>
        Default = replacement ?? new AgentToolProviderRegistry();

    public static void RegisterProvider(IAgentToolProvider provider) => Default.Register(provider);

    public static JsonArray BuildOpenAiToolsSchema() => Default.BuildOpenAiToolsSchema();

    public static string GetToolDisplayLabel(string toolName, string argumentsJson) =>
        Default.GetToolDisplayLabel(toolName, argumentsJson);

    public static Task<(string ResultJson, long DurationMs)> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken) =>
        Default.ExecuteAsync(toolName, argumentsJson, context, cancellationToken);
}
