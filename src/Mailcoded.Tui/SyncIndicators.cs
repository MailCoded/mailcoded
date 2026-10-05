using System.Globalization;
using Mailcoded.Protocol;

namespace Mailcoded.Tui;

/// <summary>Open <c>notify.sync.progress</c> indicators, closed by their <c>final</c> or by silence.</summary>
internal sealed class SyncIndicators
{
    public const long StaleAfterMs = 60_000;

    private readonly List<SyncProgressNotification> _open = [];
    private readonly Dictionary<long, long> _heardTicks = [];

    public void Apply(SyncProgressNotification progress, long nowTicks)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var index = _open.FindIndex(o => Same(o, progress));

        if (progress.Final)
        {
            if (index >= 0) _open.RemoveAt(index);
            Heard(progress.AccountId, nowTicks);
            return;
        }

        if (index >= 0) _open[index] = progress;
        else _open.Add(progress);

        Heard(progress.AccountId, nowTicks);
    }

    public bool Expire(long nowTicks)
    {
        var dropped = false;

        foreach (var (accountId, heard) in _heardTicks.ToArray())
        {
            if (nowTicks - heard < StaleAfterMs) continue;

            dropped |= _open.RemoveAll(o => o.AccountId == accountId) > 0;
            _heardTicks.Remove(accountId);
        }

        return dropped;
    }

    public string? Describe()
    {
        if (_open.Count == 0) return null;

        var parts = new string[_open.Count];
        for (var i = 0; i < parts.Length; i++) parts[i] = Short(_open[i]);

        return "sync " + string.Join(", ", parts);
    }

    internal static string Short(SyncProgressNotification progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var count = progress.Total is { } total
            ? string.Create(CultureInfo.InvariantCulture, $"{progress.Done}/{total}")
            : progress.Done.ToString(CultureInfo.InvariantCulture);

        var name = progress.FolderName ?? "?";

        return progress.Phase switch
        {
            SyncPhases.Folders => $"{count} folders",
            SyncPhases.Flags => $"{name} flags {count}",
            _ => $"{name} {count}",
        };
    }

    private static bool Same(SyncProgressNotification a, SyncProgressNotification b) =>
        a.AccountId == b.AccountId && a.FolderId == b.FolderId && string.Equals(a.Phase, b.Phase, StringComparison.Ordinal);

    private void Heard(long accountId, long nowTicks) => _heardTicks[accountId] = nowTicks;
}
