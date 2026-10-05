namespace Mailcoded.Core.Tests.Performance;

public enum CostShape
{
    /// <summary>An index seek: the cost does not depend on how many rows the store holds.</summary>
    Flat,

    /// <summary>A scan: the cost grows with the rows it has to visit.</summary>
    Linear,
}

/// <summary>A p95 ceiling derived from a PERFORMANCE §15.2 target, which is stated at 500k messages.</summary>
public static class PerfCeiling
{
    public const int TargetCorpusSize = 500_000;

    /// <summary>Headroom for a shared development machine, a cold page cache and a noisy neighbour.</summary>
    public const double SafetyFactor = 10.0;

    public const double FloorMs = 10.0;

    public static double Scale(CostShape shape, int corpusSize)
    {
        if (corpusSize <= 0) throw new ArgumentOutOfRangeException(nameof(corpusSize));

        return shape switch
        {
            CostShape.Flat => 1.0,
            CostShape.Linear => Math.Min(1.0, (double)corpusSize / TargetCorpusSize),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    public static double ScaledTargetMs(double targetMs, CostShape shape, int corpusSize)
    {
        if (targetMs <= 0) throw new ArgumentOutOfRangeException(nameof(targetMs));

        return targetMs * Scale(shape, corpusSize);
    }

    /// <param name="knownGap">A named multiple for a path that is measured above its target today; 1 for a path that meets it.</param>
    public static double CeilingMs(double targetMs, CostShape shape, int corpusSize, double knownGap = 1.0)
    {
        if (knownGap < 1.0) throw new ArgumentOutOfRangeException(nameof(knownGap));

        return Math.Max(FloorMs, ScaledTargetMs(targetMs, shape, corpusSize) * SafetyFactor * knownGap);
    }
}
