using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent;

public static class EvidenceQuality
{
    public static bool HasUsableResult(string json)
    {
        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null || node["error"] != null || node["modelNotes"] != null || node["deduplicated"]?.ToString() == "true" || node["archived"]?.ToString() == "true") return false;
            if (node["historicalSnapshot"]?.ToString() == "true")
                return node["evidence"] != null && HasUsableResult(node["evidence"]!.ToJsonString());
            var status = node["status"]?.ToString();
            return status == "ok" || (status == null && node.Count > 0);
        }
        catch (JsonException) { return false; }
    }

    public static bool HasHistoricalEvidence(JsonArray messages) => messages.OfType<JsonObject>()
        .Any(m => m["role"]?.ToString() == "tool" && HasUsableResult(m["content"]?.ToString() ?? ""));
}
