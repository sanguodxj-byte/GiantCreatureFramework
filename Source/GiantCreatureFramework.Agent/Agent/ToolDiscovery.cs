using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent;

/// <summary>
/// Shows a brief catalog read from 1.7/AgentSkills/tools.json, then discloses definitions
/// only for exact tool names selected by the model.
/// </summary>
public sealed class ToolDiscovery
{
    public const string ToolName = "discover_tools";

    private readonly Func<JsonArray> _availableTools;
    private readonly string _instructions;
    private readonly IReadOnlyList<CapabilityGroup> _capabilities;
    private readonly IReadOnlyDictionary<string, string> _groupsByTool;
    private readonly IReadOnlyDictionary<string, string> _purposes;
    private readonly HashSet<string> _tools = new(StringComparer.Ordinal);

    public ToolDiscovery(
        Func<JsonArray> availableTools,
        string instructions,
        IReadOnlyList<CapabilityGroup>? capabilities = null,
        IReadOnlyDictionary<string, string>? purposes = null)
    {
        _availableTools = availableTools;
        _instructions = instructions;
        _capabilities = capabilities ?? AgentSkillStore.Current.Capabilities;
        _purposes = purposes ?? AgentSkillStore.Current.ToolPurposes;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cap in _capabilities)
        {
            foreach (var tool in cap.Tools)
            {
                map[tool] = cap.Id;
            }
        }
        _groupsByTool = map;
    }

    public JsonArray BuildSchema()
    {
        var available = _availableTools();
        var result = new JsonArray(DiscoverySchema(available));
        foreach (var tool in available.OfType<JsonObject>())
        {
            var name = Name(tool);
            if (_tools.Contains(name)) result.Add(tool.DeepClone());
        }
        return result;
    }

    public string Select(string arguments)
    {
        var available = _availableTools();
        using var parsed = JsonDocument.Parse(arguments);
        ToolArgumentValidation.Validate(DiscoverySchema(available)["function"]!["parameters"]!, parsed.RootElement);
        var args = parsed.RootElement;
        var groups = ReadNames(args, "groups");
        var tools = ReadNames(args, "tools");
        var names = available.OfType<JsonObject>().Select(Name).ToHashSet(StringComparer.Ordinal);
        var unknown = tools.Where(t => !names.Contains(t) && !_groupsByTool.ContainsKey(t)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException(AgentProtocol.Get("Discovery_UnknownToolsPrefix") + string.Join(", ", unknown));
        var hasSelection = HasValue(args, "tools");
        if (hasSelection)
        {
            var replace = !HasValue(args, "replace") || args.GetProperty("replace").GetBoolean();
            if (replace) _tools.Clear();
            _tools.UnionWith(tools);
        }
        var selected = names.Where(n => _tools.Contains(n)).Order(StringComparer.Ordinal).ToArray();
        var result = new JsonObject
        {
            ["status"] = "tool_catalog",
            ["tool"] = ToolName,
            ["selectedTools"] = Strings(_tools.Order(StringComparer.Ordinal)),
            ["activeTools"] = Strings(selected),
            ["unavailableTools"] = Strings(_tools.Except(names).Order(StringComparer.Ordinal))
        };
        if (groups.Length > 0 || !hasSelection)
            result["capabilities"] = Catalog(available, groups.Length == 0 ? null : groups.ToHashSet(StringComparer.Ordinal));
        return result.ToJsonString(Evidence.UnescapedJsonOptions);
    }

    public void RememberUse(string name)
    {
        if (_availableTools().OfType<JsonObject>().Any(t => Name(t) == name)) _tools.Add(name);
    }

    public void Restore(JsonArray messages)
    {
        var ids = messages.OfType<JsonObject>().SelectMany(m => m["tool_calls"]?.AsArray().OfType<JsonObject>() ?? [])
            .Where(c => c["function"]?["name"]?.ToString() == ToolName)
            .Select(c => c["id"]?.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var message in messages.OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool" && ids.Contains(m["tool_call_id"]?.ToString())))
        {
            using var parsed = JsonDocument.Parse(message["content"]!.ToString());
            var result = parsed.RootElement;
            if (!result.TryGetProperty("status", out var status) || status.GetString() != "tool_catalog") continue;
            _tools.Clear();
            _tools.UnionWith(ReadNames(result, "selectedTools"));
        }
    }

    private JsonObject DiscoverySchema(JsonArray available)
    {
        var catalog = Catalog(available);
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = ToolName,
                ["description"] = _instructions,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["groups"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = Strings(catalog.Select(c => c!["id"]!.ToString()))
                            },
                            ["description"] = AgentProtocol.Get("Discovery_GroupsPrefix") + string.Join("；", catalog.Select(c => c!["id"] + "=" + c["purpose"]))
                        },
                        ["tools"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = _tools.Count == 0
                                ? string.Join("；", catalog.SelectMany(c => c!["tools"]!.AsArray()).Select(t => t!["name"] + "=" + t["purpose"]))
                                : AgentProtocol.Get("Discovery_SelectedToolsDesc")
                        },
                        ["replace"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = AgentProtocol.Get("Discovery_ReplaceDesc")
                        }
                    }
                }
            }
        };
    }

    private JsonArray Catalog(JsonArray available, IReadOnlySet<string>? groups = null)
    {
        var tools = available.OfType<JsonObject>().ToArray();
        var result = new JsonArray();
        foreach (var capability in _capabilities)
        {
            if (groups != null && !groups.Contains(capability.Id)) continue;
            var members = tools.Where(t => Group(Name(t)) == capability.Id).ToArray();
            if (members.Length > 0)
                result.Add(new JsonObject { ["id"] = capability.Id, ["purpose"] = capability.Purpose, ["tools"] = Listings(members) });
        }
        var other = tools.Where(t => Group(Name(t)) == "other").ToArray();
        if (other.Length > 0 && (groups == null || groups.Contains("other")))
            result.Add(new JsonObject { ["id"] = "other", ["purpose"] = AgentProtocol.Get("Discovery_OtherGroupPurpose"), ["tools"] = Listings(other) });
        return result;
    }

    private JsonArray Listings(IEnumerable<JsonObject> tools) => new(tools.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = Name(t),
        ["purpose"] = _purposes.GetValueOrDefault(Name(t))
            ?? (t["function"]?["description"]?.ToString() ?? "").Split(['。', '\n'], 2)[0]
    }).ToArray());

    private static string Name(JsonObject tool) => tool["function"]!["name"]!.ToString();
    private string Group(string tool) => _groupsByTool.GetValueOrDefault(tool, "other");
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private static bool HasValue(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
    private static string[] ReadNames(JsonElement args, string name) => HasValue(args, name)
        ? args.GetProperty(name).EnumerateArray().Select(v => v.GetString()!).ToArray() : [];
}
