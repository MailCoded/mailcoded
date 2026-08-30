using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mailcoded.Core.Tests.Daemon;

/// <summary>Canonical JSON for golden comparison: keys sorted, machine-dependent values replaced.</summary>
internal static class GoldenJson
{
    public const string Placeholder = "<normalized>";

    private static readonly HashSet<string> VolatileKeys = new(StringComparer.Ordinal)
    {
        "secretBackend",
        "nextCursor",
        "confirmToken",
        "confirmTokenExpiresUtc",
        "durationMs",
        "uptimeMs",
        "workingSetBytes",
        "gcHeapBytes",
        "gcCommittedBytes",
        "gcTotalAllocatedBytes",
        "gen0Collections",
        "gen1Collections",
        "gen2Collections",
        "threadCount",
        "handleCount",
        "databaseSizeBytes",
        "walSizeBytes",
        "blobDirectorySizeBytes",
    };

    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Canonicalize(string json, string? volatilePathPrefix = null)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            Write(writer, document.RootElement, volatilePathPrefix, insideVolatileKey: false);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).ReplaceLineEndings("\n");
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, string? path, bool insideVolatileKey)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in Sorted(element))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, path, VolatileKeys.Contains(property.Name));
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item, path, insideVolatileKey);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(insideVolatileKey ? Placeholder : Scrub(element.GetString(), path));
                break;

            case JsonValueKind.Number:
                if (insideVolatileKey) writer.WriteStringValue(Placeholder);
                else writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            default:
                if (insideVolatileKey) writer.WriteStringValue(Placeholder);
                else element.WriteTo(writer);
                break;
        }
    }

    private static IEnumerable<JsonProperty> Sorted(JsonElement element)
    {
        var properties = new List<JsonProperty>();
        foreach (var property in element.EnumerateObject()) properties.Add(property);
        properties.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return properties;
    }

    private static string? Scrub(string? value, string? path)
    {
        if (value is null) return null;
        if (string.IsNullOrEmpty(path)) return value;
        return value.Replace(path, "<store>", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A line-oriented diff, because a one-line "strings differ" message is unreadable here.</summary>
    public static string Describe(string name, string expected, string actual)
    {
        var left = expected.Split('\n');
        var right = actual.Split('\n');
        var report = new StringBuilder();

        report.Append("Golden transcript '").Append(name).Append("' does not match.\n");
        report.Append("  - expected (golden file)   + actual (daemon)\n");

        var max = Math.Max(left.Length, right.Length);
        var shown = 0;

        for (var i = 0; i < max && shown < 40; i++)
        {
            var l = i < left.Length ? left[i] : null;
            var r = i < right.Length ? right[i] : null;
            if (string.Equals(l, r, StringComparison.Ordinal)) continue;

            report.Append("  line ").Append(i + 1).Append('\n');
            report.Append("    - ").Append(l ?? "<missing>").Append('\n');
            report.Append("    + ").Append(r ?? "<missing>").Append('\n');
            shown++;
        }

        if (shown == 0) report.Append("  (the documents differ only in trailing content)\n");

        report.Append("\nRegenerate with MAILCODED_GOLDEN_UPDATE=1 after reviewing the change.\n");
        return report.ToString();
    }
}
