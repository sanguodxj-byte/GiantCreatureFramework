using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent;

/// <summary>A reversible presentation of large fields. Full data remains available through read_evidence.</summary>
public static class EvidencePreview
{
    public static JsonNode Render(JsonNode data, string evidenceId, string path)
    {
        if (data is JsonObject reference && reference.ContainsKey("$evidenceRef")) return data;
        JsonObject Reference(string type) => new()
        {
            ["$evidenceRef"] = evidenceId, ["path"] = path, ["view"] = "preview", ["complete"] = false, ["type"] = type
        };
        if (data is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length > 800)
        {
            var result = Reference("string");
            result["characters"] = text.Length;
            result["lines"] = text.Count(c => c == '\n') + 1;
            var length = 240;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            result["preview"] = text[..length];
            return result;
        }
        if (data is JsonArray array)
        {
            var large = array.Count > 8;
            var count = large ? 4 : array.Count;
            var shown = new JsonArray();
            for (var i = 0; i < count; i++) shown.Add(array[i] is { } child ? Render(child.DeepClone(), evidenceId, path + "/" + i) : null);
            if (!large) return shown;
            var result = Reference("array");
            result["totalItems"] = array.Count; result["preview"] = shown; result["nextIndex"] = count;
            return result;
        }
        if (data is JsonObject obj)
        {
            var large = obj.Count > 16;
            var shown = new JsonObject();
            foreach (var pair in large ? obj.Take(8) : obj)
                shown[pair.Key] = pair.Value is { } child ? Render(child.DeepClone(), evidenceId,
                    path + "/" + pair.Key.Replace("~", "~0").Replace("/", "~1")) : null;
            if (!large) return shown;
            var result = Reference("object");
            result["fields"] = new JsonArray(obj.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
            result["preview"] = shown;
            return result;
        }
        return data;
    }
}
