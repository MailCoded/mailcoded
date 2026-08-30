using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mailcoded.Bench.Gates;

public sealed record BaselineEntry
{
    /// <summary>Recorded p95 in nanoseconds, or 0 when this machine has never been baselined.</summary>
    public double P95Ns { get; init; }

    /// <summary>Absolute budget from PERFORMANCE §15.2, enforced only on the baseline machine.</summary>
    public double? BudgetNs { get; init; }

    public double? MinRowsPerSecond { get; init; }

    public int Rows { get; init; }

    public bool HasBaseline => P95Ns > 0;
}

public sealed record Baseline
{
    public const string FileName = "baseline.json";
    public const double DefaultTolerance = 0.15;

    public int SchemaVersion { get; init; } = 1;
    public string MachineId { get; init; } = string.Empty;
    public string MachineDescription { get; init; } = string.Empty;
    public string GeneratedUtc { get; init; } = string.Empty;
    public int CorpusSeed { get; init; }
    public int CorpusSize { get; init; }
    public string CorpusFingerprint { get; init; } = string.Empty;
    public double RegressionTolerance { get; init; } = DefaultTolerance;
    public IReadOnlyDictionary<string, BaselineEntry> Benchmarks { get; init; } =
        new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static Baseline Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            throw new InvalidOperationException($"No baseline at '{path}'. The gate cannot run without one.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var entries = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);
        if (root.TryGetProperty("benchmarks", out var benchmarks) && benchmarks.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in benchmarks.EnumerateObject())
            {
                entries[property.Name] = new BaselineEntry
                {
                    P95Ns = Number(property.Value, "p95Ns") ?? 0,
                    BudgetNs = Number(property.Value, "budgetNs"),
                    MinRowsPerSecond = Number(property.Value, "minRowsPerSecond"),
                    Rows = (int)(Number(property.Value, "rows") ?? 0),
                };
            }
        }

        var machine = root.TryGetProperty("machine", out var m) ? m : default;

        return new Baseline
        {
            SchemaVersion = (int)(Number(root, "schemaVersion") ?? 1),
            MachineId = Text(machine, "id") ?? string.Empty,
            MachineDescription = Text(machine, "description") ?? string.Empty,
            GeneratedUtc = Text(root, "generatedUtc") ?? string.Empty,
            CorpusSeed = (int)(Number(Child(root, "corpus"), "seed") ?? 0),
            CorpusSize = (int)(Number(Child(root, "corpus"), "size") ?? 0),
            CorpusFingerprint = Text(Child(root, "corpus"), "fingerprint") ?? string.Empty,
            RegressionTolerance = Number(root, "regressionTolerance") ?? DefaultTolerance,
            Benchmarks = entries,
        };
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("generatedUtc", GeneratedUtc);

            writer.WriteStartObject("machine");
            writer.WriteString("id", MachineId);
            writer.WriteString("description", MachineDescription);
            writer.WriteEndObject();

            writer.WriteStartObject("corpus");
            writer.WriteNumber("seed", CorpusSeed);
            writer.WriteNumber("size", CorpusSize);
            writer.WriteString("fingerprint", CorpusFingerprint);
            writer.WriteEndObject();

            writer.WriteNumber("regressionTolerance", RegressionTolerance);

            writer.WriteStartObject("benchmarks");
            foreach (var name in Sorted(Benchmarks.Keys))
            {
                var entry = Benchmarks[name];
                writer.WriteStartObject(name);
                writer.WriteNumber("p95Ns", Math.Round(entry.P95Ns, 1));
                if (entry.BudgetNs is { } budget) writer.WriteNumber("budgetNs", budget);
                if (entry.MinRowsPerSecond is { } floor) writer.WriteNumber("minRowsPerSecond", floor);
                if (entry.Rows > 0) writer.WriteNumber("rows", entry.Rows);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        File.WriteAllText(path, Encoding.UTF8.GetString(buffer.ToArray()) + Environment.NewLine);
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> names)
    {
        var list = new List<string>(names);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string Timestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
