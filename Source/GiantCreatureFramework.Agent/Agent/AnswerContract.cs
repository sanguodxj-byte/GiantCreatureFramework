using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent;

public sealed record SubmittedAnswer(string Text, string Kind, IReadOnlyList<string> EvidenceIds);

/// <summary>The model chooses the authoring/investigation steps and answer category; the host validates provenance only.</summary>
public static class AnswerContract
{
    public const string ToolName = "submit_answer";
    public static JsonObject Schema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = ToolName,
            ["description"] = AgentSkillStore.Protocol("Contract_SubmitDesc"),
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["answer"] = new JsonObject { ["type"] = "string" },
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("grounded", "general", "insufficient") },
                    ["evidenceIds"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = AgentSkillStore.Protocol("Contract_EvidenceIdsDesc")
                    }
                },
                ["required"] = new JsonArray("answer", "kind", "evidenceIds")
            }
        }
    };

    public static SubmittedAnswer Parse(string arguments)
    {
        var obj = JsonNode.Parse(arguments) as JsonObject ?? throw new InvalidDataException(AgentSkillStore.Protocol("Contract_ArgsMustBeObject"));
        var text = obj["answer"]?.GetValue<string>()?.Trim() ?? "";
        var kind = obj["kind"]?.GetValue<string>() ?? "";
        if (text.Length == 0) throw new InvalidDataException(AgentSkillStore.Protocol("Contract_EmptyAnswer"));
        if (kind is not ("grounded" or "general" or "insufficient")) throw new InvalidDataException(AgentSkillStore.Protocol("Contract_InvalidKind"));
        if (obj["evidenceIds"] is not JsonArray array) throw new InvalidDataException(AgentSkillStore.Protocol("Contract_EvidenceIdsArray"));
        var ids = array.Select(n => n?.GetValue<string>() ?? "").Distinct(StringComparer.Ordinal).ToArray();
        return new(text, kind, ids);
    }

    public static SubmittedAnswer Validate(string arguments, IReadOnlySet<string> availableEvidence)
    {
        var answer = Parse(arguments);
        if (answer.Kind == "grounded" && answer.EvidenceIds.Count == 0)
            throw new InvalidDataException(AgentSkillStore.Protocol("Contract_GroundedRequiresEvidence"));
        var invalid = answer.EvidenceIds.Where(id => !availableEvidence.Contains(id)).ToArray();
        if (invalid.Length > 0)
            throw new InvalidDataException(AgentSkillStore.Protocol("Contract_InvalidEvidencePrefix") + string.Join(", ", invalid));
        return answer;
    }

    public static JsonObject RepairSchema() => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = "repair_answer",
            ["description"] = AgentSkillStore.Protocol("Contract_RepairDesc"),
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["evidenceIds"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                },
                ["required"] = new JsonArray("evidenceIds")
            }
        }
    };

    public static void CollectEvidence(string json, ISet<string> destination, string? fallbackId = null)
    {
        try
        {
            var obj = JsonNode.Parse(json) as JsonObject;
            if (obj == null) return;
            if (obj["historicalSnapshot"]?.GetValue<bool>() == true)
            {
                if (obj["evidence"] != null) CollectEvidence(obj["evidence"]!.ToJsonString(), destination, fallbackId);
                return;
            }
            if (!EvidenceQuality.HasUsableResult(json)) return;
            var id = obj["evidenceId"]?.GetValue<string>() ?? fallbackId;
            if (!string.IsNullOrWhiteSpace(id)) destination.Add(id);
        }
        catch (JsonException) { }
    }
}
