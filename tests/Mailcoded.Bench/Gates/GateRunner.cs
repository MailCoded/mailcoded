using System.Globalization;

namespace Mailcoded.Bench.Gates;

public enum GateVerdict
{
    Pass,
    Fail,
    NotBaselined,
    Missing,
}

public sealed record GateResult
{
    public required string Name { get; init; }
    public required GateVerdict Verdict { get; init; }
    public required string Detail { get; init; }
}

/// <summary>Compares a run against the committed baseline: the 15% regression rule is the hard gate,
/// and absolute millisecond budgets bind only on the machine the baseline was recorded on.</summary>
public static class GateRunner
{
    public static IReadOnlyList<GateResult> Evaluate(BenchmarkRun run, Baseline baseline)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(baseline);

        var sameMachine =
            baseline.MachineId.Length > 0
            && string.Equals(baseline.MachineId, run.MachineId, StringComparison.Ordinal);

        var observed = new Dictionary<string, BenchmarkMeasurement>(StringComparer.Ordinal);
        foreach (var measurement in run.Measurements) observed[measurement.Name] = measurement;

        var results = new List<GateResult>(baseline.Benchmarks.Count);

        foreach (var name in OrderedNames(baseline))
        {
            var entry = baseline.Benchmarks[name];

            if (!observed.TryGetValue(name, out var measured))
            {
                results.Add(new GateResult
                {
                    Name = name,
                    Verdict = GateVerdict.Missing,
                    Detail = "the run produced no measurement for this benchmark",
                });
                continue;
            }

            results.Add(Evaluate(name, entry, measured, baseline.RegressionTolerance, sameMachine));
        }

        foreach (var measurement in run.Measurements)
        {
            if (baseline.Benchmarks.ContainsKey(measurement.Name)) continue;
            results.Add(new GateResult
            {
                Name = measurement.Name,
                Verdict = GateVerdict.NotBaselined,
                Detail = $"observed p95 {Ms(measurement.P95Ns)}, not in the baseline (ungated)",
            });
        }

        return results;
    }

    private static GateResult Evaluate(
        string name,
        BaselineEntry entry,
        BenchmarkMeasurement measured,
        double tolerance,
        bool sameMachine)
    {
        var parts = new List<string> { "p95 " + Ms(measured.P95Ns) };

        if (entry.MinRowsPerSecond is { } floor && entry.Rows > 0)
        {
            var rowsPerSecond = entry.Rows / (measured.P95Ns / 1_000_000_000d);
            parts.Add(
                "throughput "
                + rowsPerSecond.ToString("N0", CultureInfo.InvariantCulture)
                + " rows/s (floor " + floor.ToString("N0", CultureInfo.InvariantCulture) + ")");

            if (rowsPerSecond < floor)
            {
                return new GateResult
                {
                    Name = name,
                    Verdict = GateVerdict.Fail,
                    Detail = string.Join("; ", parts) + " — below the §15.8 ingest floor",
                };
            }
        }

        if (entry.BudgetNs is { } budget)
        {
            var withinBudget = measured.P95Ns <= budget;
            parts.Add(
                "budget " + Ms(budget)
                + (sameMachine
                    ? withinBudget ? " met" : " EXCEEDED"
                    : " (informational: hardware-dependent, recorded on a different machine)"));

            if (sameMachine && !withinBudget)
            {
                return new GateResult
                {
                    Name = name,
                    Verdict = GateVerdict.Fail,
                    Detail = string.Join("; ", parts),
                };
            }
        }

        if (!entry.HasBaseline)
        {
            return new GateResult
            {
                Name = name,
                Verdict = GateVerdict.NotBaselined,
                Detail = string.Join("; ", parts) + "; no committed baseline to compare against",
            };
        }

        var ratio = measured.P95Ns / entry.P95Ns;
        var drift = (ratio - 1) * 100;
        parts.Add(
            "baseline " + Ms(entry.P95Ns)
            + " (" + drift.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%)");

        return new GateResult
        {
            Name = name,
            Verdict = ratio > 1 + tolerance ? GateVerdict.Fail : GateVerdict.Pass,
            Detail = string.Join("; ", parts),
        };
    }

    public static int Report(IReadOnlyList<GateResult> results, BenchmarkRun run, Baseline baseline, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(output);

        var sameMachine = string.Equals(baseline.MachineId, run.MachineId, StringComparison.Ordinal);

        output.WriteLine("M-perf gates");
        output.WriteLine("  machine (run)      : " + run.MachineId);
        output.WriteLine("  machine (baseline) : " + (baseline.MachineId.Length == 0 ? "<none recorded>" : baseline.MachineId));
        output.WriteLine(
            "  regression rule    : fail above +"
            + (baseline.RegressionTolerance * 100).ToString("0.#", CultureInfo.InvariantCulture)
            + "% of the committed p95");

        output.WriteLine(sameMachine
            ? "  absolute budgets   : ENFORCED (this is the baseline machine)"
            : "  absolute budgets   : informational only — the millisecond figures in PERFORMANCE §15.2 are "
              + "hardware-dependent and this is not the machine the baseline was recorded on");

        output.WriteLine();

        var failures = 0;
        foreach (var result in results)
        {
            if (result.Verdict == GateVerdict.Fail) failures++;
            output.WriteLine($"  [{Label(result.Verdict)}] {result.Name}: {result.Detail}");
        }

        output.WriteLine();
        output.WriteLine(failures == 0
            ? "All gates passed."
            : failures.ToString(CultureInfo.InvariantCulture) + " gate(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    public static Baseline Rebase(BenchmarkRun run, Baseline previous, int corpusSeed, int corpusSize, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(previous);

        var entries = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);

        foreach (var pair in previous.Benchmarks) entries[pair.Key] = pair.Value;

        foreach (var measurement in run.Measurements)
        {
            entries.TryGetValue(measurement.Name, out var existing);
            entries[measurement.Name] = new BaselineEntry
            {
                P95Ns = measurement.P95Ns,
                BudgetNs = existing?.BudgetNs,
                MinRowsPerSecond = existing?.MinRowsPerSecond,
                Rows = existing?.Rows ?? 0,
            };
        }

        return previous with
        {
            MachineId = run.MachineId,
            GeneratedUtc = Baseline.Timestamp(),
            CorpusSeed = corpusSeed,
            CorpusSize = corpusSize,
            CorpusFingerprint = fingerprint,
            Benchmarks = entries,
        };
    }

    private static IReadOnlyList<string> OrderedNames(Baseline baseline)
    {
        var names = new List<string>(baseline.Benchmarks.Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string Label(GateVerdict verdict) => verdict switch
    {
        GateVerdict.Pass => "PASS",
        GateVerdict.Fail => "FAIL",
        GateVerdict.Missing => "MISS",
        _ => "----",
    };

    private static string Ms(double nanoseconds) =>
        (nanoseconds / 1_000_000d).ToString("F3", CultureInfo.InvariantCulture) + " ms";
}
