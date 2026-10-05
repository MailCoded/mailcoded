using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Application;

/// <summary>Per-Account rate limit for sync progress on monotonic ticks; one per process so concurrent syncs share it.</summary>
public sealed class SyncProgressThrottle
{
    public const int DefaultIntervalMs = 250;

    private readonly IClock _clock;
    private readonly int _intervalMs;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, long> _nextAdmitTicks = [];

    public SyncProgressThrottle(IClock clock, int intervalMs = DefaultIntervalMs)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegative(intervalMs);

        _clock = clock;
        _intervalMs = intervalMs;
    }

    public SyncProgressRun Begin(Action<SyncProgress> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);

        return new SyncProgressRun(this, publish);
    }

    internal bool TryAdmit(AccountId accountId)
    {
        lock (_gate)
        {
            var now = _clock.Ticks;
            if (_nextAdmitTicks.TryGetValue(accountId.Value, out var next) && now < next) return false;

            _nextAdmitTicks[accountId.Value] = now + _intervalMs;
            return true;
        }
    }
}

/// <summary>One sync's indicators. Not thread-safe: a sync reports from one flow at a time.</summary>
public sealed class SyncProgressRun : IProgress<SyncProgress>
{
    private readonly SyncProgressThrottle _throttle;
    private readonly Action<SyncProgress> _publish;
    private readonly List<Indicator> _indicators = [];

    internal SyncProgressRun(SyncProgressThrottle throttle, Action<SyncProgress> publish)
    {
        _throttle = throttle;
        _publish = publish;
    }

    public void Report(SyncProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var index = IndexOf(value.FolderId, value.Phase);

        if (value.Final)
        {
            if (index < 0) return;

            var shown = _indicators[index].Shown;
            _indicators.RemoveAt(index);
            if (shown) _publish(value);
            return;
        }

        if (index < 0)
        {
            _indicators.Add(new Indicator(value));
        }
        else
        {
            _indicators[index].Latest = value;
            _indicators[index].Dirty = true;
        }

        if (!_throttle.TryAdmit(value.AccountId)) return;

        foreach (var indicator in _indicators)
        {
            if (!indicator.Dirty) continue;

            indicator.Dirty = false;
            indicator.Shown = true;
            _publish(indicator.Latest);
        }
    }

    /// <summary>Closes what a failed or cancelled sync left open; call it in a finally. Idempotent.</summary>
    public void Complete()
    {
        foreach (var indicator in _indicators)
        {
            if (indicator.Shown) _publish(indicator.Latest with { Final = true });
        }

        _indicators.Clear();
    }

    private int IndexOf(FolderId folderId, SyncPhase phase)
    {
        for (var i = 0; i < _indicators.Count; i++)
        {
            if (_indicators[i].Latest.FolderId == folderId && _indicators[i].Latest.Phase == phase) return i;
        }

        return -1;
    }

    private sealed class Indicator(SyncProgress latest)
    {
        public SyncProgress Latest { get; set; } = latest;

        public bool Shown { get; set; }

        public bool Dirty { get; set; } = true;
    }
}
