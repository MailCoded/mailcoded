using System.Globalization;
using System.Text.Json;

namespace Mailcoded.Bench.Gates;

public sealed record BenchmarkMeasurement(string Name, double P95Ns, double MeanNs, int Samples);

public sealed record BenchmarkRun
{
    public required string MachineId { get; init; }
    public required IReadOnlyList<BenchmarkMeasurement> Measurements { get; init; }
    public required IReadOnlyList<string> SourceFiles { get; init; }
}

/// <summary>Reads BenchmarkDotNet's full JSON export; times in that file are nanoseconds.</summary>
public static class BenchmarkReport
{
    public const string DefaultArtifactsDirectory = "BenchmarkDotNet.Artifacts";
    public const string ReportSuffix = "-report-full.json";

    public static BenchmarkRun Load(string artifactsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsDirectory);

        if (!Directory.Exists(artifactsDirectory))
            throw new InvalidOperationException($"No benchmark artifacts at '{artifactsDirectory}'. Run the suite first.");

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(artifactsDirectory, "*" + ReportSuffix, SearchOption.AllDirectories))
            files.Add(file);

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"No '*{ReportSuffix}' under '{artifactsDirectory}'. The JSON exporter did not run.");
        }

        files.Sort(StringComparer.Ordinal);

        var measurements = new Dictionary<string, BenchmarkMeasurement>(StringComparer.Ordinal);
        var machineId = "unknown";

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;

            if (root.TryGetProperty("HostEnvironmentInfo", out var host)) machineId = DescribeMachine(host);
            if (!root.TryGetProperty("Benchmarks", out var benchmarks) || benchmarks.ValueKind != JsonValueKind.Array) continue;

            foreach (var benchmark in benchmarks.EnumerateArray())
            {
                var name = ReadName(benchmark);
                if (name is null) continue;
                if (!benchmark.TryGetProperty("Statistics", out var statistics)) continue;

                var p95 = ReadPercentile(statistics, "P95");
                var mean = statistics.TryGetProperty("Mean", out var meanValue) ? meanValue.GetDouble() : p95;
                var samples = statistics.TryGetProperty("N", out var n) ? n.GetInt32() : 0;

                measurements[name] = new BenchmarkMeasurement(name, p95, mean, samples);
            }
        }

        var ordered = new List<BenchmarkMeasurement>(measurements.Values);
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        return new BenchmarkRun { MachineId = machineId, Measurements = ordered, SourceFiles = files };
    }

    private static string? ReadName(JsonElement benchmark)
    {
        if (benchmark.TryGetProperty("Method", out var method) && method.ValueKind == JsonValueKind.String)
            return method.GetString();

        return benchmark.TryGetProperty("MethodTitle", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString()
            : null;
    }

    private static double ReadPercentile(JsonElement statistics, string name)
    {
        if (statistics.TryGetProperty("Percentiles", out var percentiles)
            && percentiles.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return statistics.TryGetProperty("Max", out var max) && max.ValueKind == JsonValueKind.Number
            ? max.GetDouble()
            : double.NaN;
    }

    /// <summary>The identity an absolute millisecond budget is only meaningful against.</summary>
    private static string DescribeMachine(JsonElement host)
    {
        var processor = Text(host, "ProcessorName") ?? "unknown-cpu";
        var cores = host.TryGetProperty("PhysicalCoreCount", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32().ToString(CultureInfo.InvariantCulture)
            : "?";
        var os = Text(host, "OsVersion") ?? "unknown-os";
        var runtime = Text(host, "RuntimeVersion") ?? "unknown-runtime";
        var architecture = Text(host, "Architecture") ?? "unknown-arch";

        return string.Join(" | ", processor.Trim(), cores + "C", architecture, os, runtime);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
