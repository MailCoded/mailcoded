using System.Globalization;

namespace Mailcoded.Bench.Corpus;

/// <summary>Everything that determines the corpus. Two equal option sets produce equal corpora.</summary>
public sealed record CorpusOptions
{
    public const int DefaultSeed = 20260101;
    public const int FullSize = 500_000;
    public const int SmokeSize = 10_000;

    public const string SeedEnvVar = "MAILCODED_BENCH_SEED";
    public const string SizeEnvVar = "MAILCODED_BENCH_SIZE";
    public const string CacheEnvVar = "MAILCODED_BENCH_CORPUS_DIR";

    public int Seed { get; init; } = DefaultSeed;

    public int Size { get; init; } = FullSize;

    /// <summary>Share of messages whose subject and body are CJK; PERFORMANCE §15.8 asks for ~10%.</summary>
    public double CjkFraction { get; init; } = 0.10;

    public double ReplyFraction { get; init; } = 0.35;

    public string Mailbox { get; init; } = "owner@mailcoded.test";

    public DateTimeOffset Start { get; init; } = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Mean gap between consecutive messages; 62 s spreads 500k over about a year.</summary>
    public int MeanGapMs { get; init; } = 62_000;

    public string CacheDirectory { get; init; } = DefaultCacheDirectory();

    public static CorpusOptions FromEnvironment() => new()
    {
        Seed = ReadInt(SeedEnvVar, DefaultSeed),
        Size = ReadInt(SizeEnvVar, FullSize),
        CacheDirectory = Environment.GetEnvironmentVariable(CacheEnvVar) is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : DefaultCacheDirectory(),
    };

    /// <summary>Stable identity of one corpus; the cache and the baseline both key on it.</summary>
    public string Key =>
        string.Create(CultureInfo.InvariantCulture, $"seed{Seed}-n{Size}-cjk{(int)(CjkFraction * 100)}");

    public string Directory => Path.Combine(CacheDirectory, Key);

    public string DatabasePath => Path.Combine(Directory, "store.db");

    public string ManifestPath => Path.Combine(Directory, "manifest.json");

    private static string DefaultCacheDirectory() =>
        Path.Combine(Path.GetTempPath(), "mailcoded-bench-corpus");

    private static int ReadInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}
