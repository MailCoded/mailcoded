using System.Globalization;

namespace Mailcoded.Core.Tests.Performance;

/// <summary>Nearest-rank percentiles over timing samples, in milliseconds.</summary>
public sealed record LatencySummary(int Count, double P50Ms, double P95Ms, double MaxMs)
{
    public static LatencySummary From(IReadOnlyCollection<double> samplesMs)
    {
        ArgumentNullException.ThrowIfNull(samplesMs);
        if (samplesMs.Count == 0) throw new ArgumentException("At least one sample is required.", nameof(samplesMs));

        var sorted = samplesMs.ToArray();
        Array.Sort(sorted);

        return new LatencySummary(sorted.Length, NearestRank(sorted, 0.50), NearestRank(sorted, 0.95), sorted[^1]);
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"n={Count} p50={P50Ms:F3} ms p95={P95Ms:F3} ms max={MaxMs:F3} ms");

    private static double NearestRank(double[] sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
