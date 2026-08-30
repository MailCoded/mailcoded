using System.Globalization;
using System.Text.Json;

namespace Mailcoded.Mcp;

/// <summary>A tool call the adapter refuses, carrying the stable code agents branch on.</summary>
internal sealed class ToolFailureException : Exception
{
    public ToolFailureException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>Reads and validates the untyped MCP argument bag. Messages never echo an argument value.</summary>
internal readonly struct ToolArgs
{
    private readonly IDictionary<string, JsonElement>? _values;

    public ToolArgs(IDictionary<string, JsonElement>? values) => _values = values;

    public string RequireString(string name)
    {
        var value = OptionalString(name);
        if (string.IsNullOrWhiteSpace(value)) throw Invalid(name, "a non-empty string");
        return value;
    }

    public string? OptionalString(string name)
    {
        if (!TryGet(name, out var element)) return null;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null => null,
            _ => throw Invalid(name, "a string"),
        };
    }

    public long RequireInt64(string name) =>
        OptionalInt64(name) ?? throw Invalid(name, "an integer");

    public long? OptionalInt64(string name)
    {
        if (!TryGet(name, out var element)) return null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number when element.TryGetInt64(out var number):
                return number;
            case JsonValueKind.String
                when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                throw Invalid(name, "an integer");
        }
    }

    public long RequirePositiveInt64(string name)
    {
        var value = RequireInt64(name);
        if (value <= 0) throw Invalid(name, "a positive integer");
        return value;
    }

    public int OptionalInt32(string name, int fallback)
    {
        var value = OptionalInt64(name);
        if (value is null) return fallback;
        if (value.Value is < int.MinValue or > int.MaxValue) throw Invalid(name, "a 32-bit integer");
        return (int)value.Value;
    }

    public bool OptionalBool(string name, bool fallback)
    {
        if (!TryGet(name, out var element)) return fallback;

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => fallback,
            _ => throw Invalid(name, "a boolean"),
        };
    }

    public IReadOnlyList<string> StringArray(string name)
    {
        if (!TryGet(name, out var element)) return [];
        if (element.ValueKind == JsonValueKind.Null) return [];

        if (element.ValueKind == JsonValueKind.String)
        {
            var single = element.GetString();
            return string.IsNullOrWhiteSpace(single) ? [] : [single];
        }

        if (element.ValueKind != JsonValueKind.Array) throw Invalid(name, "an array of strings");

        var values = new List<string>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw Invalid(name, "an array of strings");
            var text = item.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            values.Add(text);
        }

        return values;
    }

    private bool TryGet(string name, out JsonElement element)
    {
        if (_values is not null && _values.TryGetValue(name, out element)) return true;
        element = default;
        return false;
    }

    private static ToolFailureException Invalid(string name, string expected) =>
        new(ToolErrorCodes.InvalidParams, $"'{name}' must be {expected}.");
}
