using System.Globalization;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using Mailcoded.Bench.Corpus;
using Mailcoded.Bench.Gates;

namespace Mailcoded.Bench;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitGateFailed = 1;
    private const int ExitUsage = 2;

    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var verb = args.Length > 0 ? args[0] : string.Empty;

        return verb switch
        {
            "--help" or "-h" => Help(),
            "--generate" => Generate(args),
            "--fingerprint" => Fingerprint(args),
            "--gate" => Gate(args, rebase: false),
            "--update-baseline" => Gate(args, rebase: true),
            _ => RunBenchmarks(args),
        };
    }

    private static int Help()
    {
        Console.Out.WriteLine(
            """
            mailcoded benchmarks — PERFORMANCE §15.8 corpus, benchmarks and gates.

            Usage:
              dotnet run -c Release --project tests/Mailcoded.Bench
                  Build the corpus, run every benchmark, then evaluate the gates.

              --generate [--seed N] [--size N] [--verify]
                  Build (or reuse) the synthetic corpus only. --verify regenerates and compares digests.

              --fingerprint [--seed N] [--size N]
                  Print the corpus digest without touching the disk.

              --gate [--artifacts DIR] [--baseline PATH]
                  Evaluate an existing benchmark run against the committed baseline.

              --update-baseline [--artifacts DIR] [--baseline PATH]
                  Rewrite the baseline from the last run. Needs explicit PR approval; see README.md.

              Any other argument set is handed to BenchmarkDotNet (--filter, --list, --job, ...).
            """);

        return ExitOk;
    }

    private static int Generate(string[] args)
    {
        var options = ReadOptions(args);
        var manifest = CorpusBuilder.EnsureBuilt(options, Console.Out, CancellationToken.None);

        Console.Out.WriteLine($"seed        : {options.Seed.ToString(CultureInfo.InvariantCulture)}");
        Console.Out.WriteLine($"size        : {options.Size.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.Out.WriteLine($"fingerprint : {manifest.Fingerprint}");
        Console.Out.WriteLine($"database    : {options.DatabasePath}");
        Console.Out.WriteLine(
            $"db bytes    : {manifest.DatabaseBytes.ToString("N0", CultureInfo.InvariantCulture)}");

        if (!Has(args, "--verify")) return ExitOk;

        var again = new SyntheticMailbox(options).Fingerprint();
        var identical = string.Equals(again, manifest.Fingerprint, StringComparison.Ordinal);

        Console.Out.WriteLine(identical
            ? "verify      : PASS — the seed reproduces an identical corpus"
            : $"verify      : FAIL — regenerating produced {again}");

        return identical ? ExitOk : ExitGateFailed;
    }

    private static int Fingerprint(string[] args)
    {
        var options = ReadOptions(args);
        Console.Out.WriteLine(new SyntheticMailbox(options).Fingerprint());
        return ExitOk;
    }

    private static int RunBenchmarks(string[] args)
    {
        var options = ReadOptions(args);
        CorpusBuilder.EnsureBuilt(options, Console.Out, CancellationToken.None);

        var artifacts = ArtifactsPath(args);
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddExporter(JsonExporter.Full)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator)
            .WithArtifactsPath(artifacts);

        string[] forwarded = args.Length == 0 ? ["--filter", "*"] : args;
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(forwarded, config);

        return args.Length == 0
            ? EvaluateGates(artifacts, ResolveBaselinePath(args), options, rebase: false)
            : ExitOk;
    }

    private static int Gate(string[] args, bool rebase)
    {
        var options = ReadOptions(args);
        return EvaluateGates(ArtifactsPath(args), ResolveBaselinePath(args), options, rebase);
    }

    private static int EvaluateGates(string artifacts, string baselinePath, CorpusOptions options, bool rebase)
    {
        BenchmarkRun run;
        Baseline baseline;

        try
        {
            run = BenchmarkReport.Load(artifacts);
            baseline = Baseline.Load(baselinePath);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine("Gate evaluation could not run: " + ex.Message);
            return ExitUsage;
        }

        if (rebase)
        {
            var updated = GateRunner.Rebase(
                run,
                baseline,
                options.Seed,
                options.Size,
                new SyntheticMailbox(options).Fingerprint());

            updated.Save(baselinePath);

            Console.Out.WriteLine("Baseline rewritten at " + baselinePath);
            Console.Out.WriteLine(
                "Re-baselining moves the gate. It needs explicit PR approval and a note saying why "
                + "the previous numbers no longer apply (README.md).");
            return ExitOk;
        }

        var results = GateRunner.Evaluate(run, baseline);
        return GateRunner.Report(results, run, baseline, Console.Out);
    }

    private static CorpusOptions ReadOptions(string[] args)
    {
        var options = CorpusOptions.FromEnvironment();

        if (TryReadInt(args, "--seed", out var seed)) options = options with { Seed = seed };
        if (TryReadInt(args, "--size", out var size)) options = options with { Size = size };
        if (TryReadString(args, "--corpus-dir", out var dir)) options = options with { CacheDirectory = Path.GetFullPath(dir) };

        return options;
    }

    private static string ArtifactsPath(string[] args) =>
        TryReadString(args, "--artifacts", out var path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(BenchmarkReport.DefaultArtifactsDirectory);

    private static string ResolveBaselinePath(string[] args)
    {
        if (TryReadString(args, "--baseline", out var explicitPath)) return Path.GetFullPath(explicitPath);

        var source = FindSourceBaseline();
        return source ?? Baseline.DefaultPath;
    }

    /// <summary>Prefers the committed file over the build copy, so a rebase lands in the repository.</summary>
    private static string? FindSourceBaseline()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Mailcoded.Bench", Baseline.FileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private static bool Has(string[] args, string name)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, name, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool TryReadString(string[] args, string name, out string value)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal)) continue;
            value = args[i + 1];
            return value.Length > 0;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadInt(string[] args, string name, out int value)
    {
        value = 0;
        return TryReadString(args, name, out var raw)
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0;
    }
}
