using Xunit;

namespace Mailcoded.Core.Tests.Performance;

public sealed class PerfStatsTests
{
    [Fact]
    public void Percentiles_use_nearest_rank()
    {
        var samples = Enumerable.Range(1, 100).Select(i => (double)i).Reverse().ToArray();

        var summary = LatencySummary.From(samples);

        Assert.Equal(100, summary.Count);
        Assert.Equal(50, summary.P50Ms);
        Assert.Equal(95, summary.P95Ms);
        Assert.Equal(100, summary.MaxMs);
    }

    [Fact]
    public void One_sample_is_every_percentile()
    {
        var summary = LatencySummary.From([4.5]);

        Assert.Equal(4.5, summary.P50Ms);
        Assert.Equal(4.5, summary.P95Ms);
        Assert.Equal(4.5, summary.MaxMs);
    }

    [Fact]
    public void An_outlier_moves_the_max_but_not_the_median()
    {
        var summary = LatencySummary.From([1, 1, 1, 1, 1, 1, 1, 1, 1, 900]);

        Assert.Equal(1, summary.P50Ms);
        Assert.Equal(900, summary.MaxMs);
    }

    [Fact]
    public void No_samples_is_refused()
    {
        Assert.Throws<ArgumentException>(() => LatencySummary.From([]));
    }

    [Fact]
    public void The_summary_prints_both_percentiles()
    {
        var text = LatencySummary.From([1.0, 2.0]).ToString();

        Assert.Contains("p50=1.000 ms", text, StringComparison.Ordinal);
        Assert.Contains("p95=2.000 ms", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_seek_target_is_not_scaled_down_with_the_corpus()
    {
        Assert.Equal(1.0, PerfCeiling.Scale(CostShape.Flat, 100_000));
        Assert.Equal(100.0, PerfCeiling.CeilingMs(10, CostShape.Flat, 100_000));
    }

    [Fact]
    public void A_scan_target_scales_with_the_corpus()
    {
        Assert.Equal(0.2, PerfCeiling.Scale(CostShape.Linear, 100_000), 10);
        Assert.Equal(200.0, PerfCeiling.CeilingMs(100, CostShape.Linear, 100_000), 10);
    }

    [Fact]
    public void A_known_gap_widens_only_the_ceiling_never_the_target()
    {
        Assert.Equal(20.0, PerfCeiling.ScaledTargetMs(100, CostShape.Linear, 100_000), 10);
        Assert.Equal(1000.0, PerfCeiling.CeilingMs(100, CostShape.Linear, 100_000, knownGap: 5), 10);
    }

    [Fact]
    public void A_known_gap_cannot_tighten_a_ceiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PerfCeiling.CeilingMs(100, CostShape.Flat, 100_000, knownGap: 0.5));
    }

    [Fact]
    public void A_corpus_larger_than_the_target_size_is_never_scaled_up_by_more_than_the_target()
    {
        Assert.Equal(1.0, PerfCeiling.Scale(CostShape.Linear, 2_000_000));
    }

    [Fact]
    public void A_tiny_target_still_gets_the_noise_floor()
    {
        Assert.Equal(PerfCeiling.FloorMs, PerfCeiling.CeilingMs(0.5, CostShape.Flat, 100_000));
    }

    [Fact]
    public void An_empty_corpus_has_no_scale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PerfCeiling.Scale(CostShape.Linear, 0));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("on", false)]
    public void Only_an_explicit_yes_enables_the_test(string? value, bool expected)
    {
        Assert.Equal(expected, PerfGate.IsTruthy(value));
    }

    [Fact]
    public void The_skip_reason_names_the_switch_and_says_nothing_was_checked()
    {
        Assert.Contains(PerfGate.EnvVar, PerfGate.SkipReason, StringComparison.Ordinal);
        Assert.Contains("Nothing was measured", PerfGate.SkipReason, StringComparison.Ordinal);
    }
}
