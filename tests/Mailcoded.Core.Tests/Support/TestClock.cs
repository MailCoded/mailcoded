using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Tests.Support;

/// <summary>A deterministic clock; wall time and monotonic ticks advance together on demand.</summary>
public sealed class TestClock : IClock
{
    public static readonly DateTimeOffset DefaultStart =
        new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private long _ticks;

    public TestClock(DateTimeOffset? start = null) => UtcNow = start ?? DefaultStart;

    public DateTimeOffset UtcNow { get; set; }

    public long Ticks => _ticks;

    public void Advance(TimeSpan delta)
    {
        UtcNow += delta;
        _ticks += (long)delta.TotalMilliseconds;
    }
}
