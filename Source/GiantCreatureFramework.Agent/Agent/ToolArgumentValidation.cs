using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiantCreatureFramework.Agent;

/// <summary>Validate the parameter types declared by the public tool schema, including batch arrays.</summary>
public static class ToolArgumentValidation
{
    public static void Validate(JsonNode parameters, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(AgentSkillStore.Protocol("Validation_ArgsMustBeObject"));
        var required = parameters["required"]?.AsArray().Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var name in required)
            if (!arguments.TryGetProperty(name, out _))
                throw new ArgumentException(AgentSkillStore.Protocol("Validation_MissingParamPrefix") + name);
        foreach (var arg in arguments.EnumerateObject())
        {
            var property = parameters["properties"]?[arg.Name];
            if (property == null)
                throw new ArgumentException(AgentSkillStore.Protocol("Validation_UnknownParamPrefix") + arg.Name);
            if (arg.Value.ValueKind == JsonValueKind.Null && !required.Contains(arg.Name)) continue;
            ValidateValue(property, arg.Value, arg.Name);
        }
    }

    private static void ValidateValue(JsonNode property, JsonElement value, string path)
    {
        var valid = property["type"]?.ToString() switch
        {
            "string" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= (property["maxLength"]?.GetValue<int>() ?? 512),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                && number >= (property["minimum"]?.GetValue<int>() ?? 1) && number <= (property["maximum"]?.GetValue<int>() ?? 100000),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var dbl) && double.IsFinite(dbl)
                && (!TryReadSchemaDouble(property["minimum"], out var minVal) || dbl >= minVal)
                && (!TryReadSchemaDouble(property["maximum"], out var maxVal) || dbl <= maxVal),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= (property["minItems"]?.GetValue<int>() ?? 0)
                && value.GetArrayLength() <= (property["maxItems"]?.GetValue<int>() ?? int.MaxValue),
            _ => false
        };
        if (!valid)
            throw new ArgumentException(AgentSkillStore.Protocol("Validation_InvalidTypeOrRangePrefix") + path);
        if (property["enum"] is JsonArray allowed && !allowed.Any(v => JsonNode.DeepEquals(v, JsonNode.Parse(value.GetRawText()))))
            throw new ArgumentException(AgentSkillStore.Protocol("Validation_InvalidEnumPrefix") + path);
        if (value.ValueKind == JsonValueKind.Array)
        {
            var itemSchema = property["items"]
                ?? throw new InvalidDataException(AgentSkillStore.Protocol("Validation_MissingArrayItemsPrefix") + path);
            var index = 0;
            foreach (var item in value.EnumerateArray()) ValidateValue(itemSchema, item, path + "[" + index++ + "]");
        }
    }

    private static bool TryReadSchemaDouble(JsonNode? node, out double result)
    {
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out result)) return double.IsFinite(result);
            if (jv.TryGetValue<int>(out var i)) { result = i; return true; }
            if (jv.TryGetValue<long>(out var l)) { result = l; return true; }
            if (jv.TryGetValue<decimal>(out var m)) { result = (double)m; return double.IsFinite(result); }
            if (jv.TryGetValue<float>(out var f)) { result = f; return double.IsFinite(result); }
        }
        result = 0;
        return false;
    }
}
