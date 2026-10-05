namespace Mailcoded.Core.Domain.Sync;

/// <summary>Divides an Account's IMAP connection ceiling between sync, the interactive lane and IDLE watchers.</summary>
public readonly record struct ConnectionBudget
{
    public const int MinTotal = 2;

    /// <summary>Sync + interactive + one watcher: below this the interactive lane would starve IDLE.</summary>
    public const int MinTotalForInteractiveLane = 3;

    public ConnectionBudget(int total, bool interactiveLaneEnabled)
    {
        Total = Math.Max(MinTotal, total);
        InteractiveLaneAllowed = interactiveLaneEnabled && Total >= MinTotalForInteractiveLane;
    }

    public int Total { get; }

    public bool InteractiveLaneAllowed { get; }

    public int WatcherSlots => Total - (InteractiveLaneAllowed ? 2 : 1);

    public int WatcherCap(int configuredCap) => Math.Max(1, Math.Min(configuredCap, WatcherSlots));

    public static ConnectionBudget For(bool lowConnectionLimit, int maxConnections, int lowConnectionMax, bool interactiveLaneEnabled) =>
        new(lowConnectionLimit ? Math.Min(maxConnections, lowConnectionMax) : maxConnections, interactiveLaneEnabled);
}
