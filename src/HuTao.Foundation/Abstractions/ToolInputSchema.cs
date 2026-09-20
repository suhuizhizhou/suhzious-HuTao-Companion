using System.Text.Json;

namespace HuTao.Foundation.Abstractions;

public enum ToolArgumentType { String, Number, Integer, Boolean }
public sealed record ToolArgument(string Name, ToolArgumentType Type, bool Required = true,
    int MaxLength = 8192, double Minimum = -1e12, double Maximum = 1e12,
    IReadOnlyList<string>? Enum = null, string Description = "");

/// <summary>A closed, flat JSON Schema subset. Unknown and duplicate keys are rejected.</summary>
public sealed class ToolInputSchema(params ToolArgument[] arguments)
{
    public static ToolInputSchema Empty { get; } = new();
    public static ToolInputSchema Legacy { get; } = new(new ToolArgument("input", ToolArgumentType.String, false));
    public IReadOnlyList<ToolArgument> Arguments { get; } = arguments;
    public static ToolInputSchema FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("type").GetString() != "object" ||
            root.EnumerateObject().Any(p => p.Name is not ("type" or "properties" or "required" or "additionalProperties" or "description" or "title" or "$schema")))
            throw new ArgumentException("unsupported_schema");
        if (root.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind != JsonValueKind.False)
            throw new ArgumentException("open_schema_not_supported");
        var required = root.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        var rules = new List<ToolArgument>();
        foreach (var property in root.GetProperty("properties").EnumerateObject())
        {
            var value = property.Value;
            if (value.EnumerateObject().Any(p => p.Name is not ("type" or "description" or "title" or "enum" or "maxLength" or "minimum" or "maximum")))
                throw new ArgumentException("unsupported_schema_keyword");
            var type = value.GetProperty("type").GetString() switch
            {
                "string" => ToolArgumentType.String, "number" => ToolArgumentType.Number,
                "integer" => ToolArgumentType.Integer, "boolean" => ToolArgumentType.Boolean,
                _ => throw new ArgumentException("unsupported_schema_type")
            };
            var choices = value.TryGetProperty("enum", out var e) ? e.EnumerateArray().Select(x => x.GetString()!).ToArray() : null;
            if (choices is not null && (type != ToolArgumentType.String || choices.Length == 0 || choices.Any(x => x is null)))
                throw new ArgumentException("unsupported_schema_enum");
            rules.Add(new(property.Name, type, required.Contains(property.Name),
                value.TryGetProperty("maxLength", out var length) ? Math.Min(12000, length.GetInt32()) : 8192,
                value.TryGetProperty("minimum", out var min) ? min.GetDouble() : -1e12,
                value.TryGetProperty("maximum", out var max) ? max.GetDouble() : 1e12, choices));
        }
        if (required.Except(rules.Select(r => r.Name)).Any() || rules.Select(r => r.Name).Distinct().Count() != rules.Count)
            throw new ArgumentException("invalid_schema_properties");
        return new(rules.ToArray());
    }
    public string ToJson() => JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false,
        required = Arguments.Where(a => a.Required).Select(a => a.Name),
        properties = Arguments.ToDictionary(a => a.Name, a => (object)new
        {
            type = a.Type.ToString().ToLowerInvariant(), description = a.Description,
            maxLength = a.Type == ToolArgumentType.String ? (int?)a.MaxLength : null,
            minimum = a.Type is ToolArgumentType.Number or ToolArgumentType.Integer ? (double?)a.Minimum : null,
            maximum = a.Type is ToolArgumentType.Number or ToolArgumentType.Integer ? (double?)a.Maximum : null,
            @enum = a.Enum
        })
    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    public string? Validate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "arguments_must_be_object";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return "duplicate_argument:" + property.Name;
            var rule = Arguments.FirstOrDefault(a => a.Name == property.Name);
            if (rule is null) return "unknown_argument:" + property.Name;
            var v = property.Value;
            var valid = rule.Type switch
            {
                ToolArgumentType.String => v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= rule.MaxLength &&
                    (rule.Enum is null || rule.Enum.Contains(v.GetString(), StringComparer.Ordinal)),
                ToolArgumentType.Number => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var number) &&
                    double.IsFinite(number) && number >= rule.Minimum && number <= rule.Maximum,
                ToolArgumentType.Integer => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var integer) &&
                    integer >= rule.Minimum && integer <= rule.Maximum,
                ToolArgumentType.Boolean => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false
            };
            if (!valid) return "invalid_argument:" + property.Name;
        }
        return Arguments.FirstOrDefault(a => a.Required && !seen.Contains(a.Name)) is { } missing
            ? "missing_argument:" + missing.Name : null;
    }
}
