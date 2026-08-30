using System.Globalization;

namespace Mailcoded.Daemon;

/// <summary>
/// RELIABILITY §14.4: stdin EOF is the normal exit signal, but an editor that dies without closing
/// the pipe would leave an orphan holding IMAP connections. This checks the parent on a monotonic
/// ~30 s cadence and cancels the daemon when it is gone.
/// </summary>
internal sealed class ParentWatchdog
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly int parentPid;
    private readonly TimeSpan interval;
    private readonly StderrLog log;

    private ParentWatchdog(int parentPid, TimeSpan interval, StderrLog log)
    {
        this.parentPid = parentPid;
        this.interval = interval;
        this.log = log;
    }

    /// <summary>Null when the parent cannot be identified; stdin EOF is then the only exit signal.</summary>
    public static ParentWatchdog? Create(StderrLog log, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        var pid = ResolveParentPid();
        if (pid is not { } parent || parent <= 0)
        {
            log.Debug("No parent pid could be resolved; the orphan watchdog is inactive.");
            return null;
        }

        return new ParentWatchdog(parent, interval ?? DefaultInterval, log);
    }

    public int ParentPid => parentPid;

    /// <summary>Returns once the parent has exited or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (SingleInstanceLock.IsAlive(parentPid)) continue;

                log.Warn($"Parent process {parentPid.ToString(CultureInfo.InvariantCulture)} exited; shutting down.");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private static int? ResolveParentPid()
    {
        string? raw;
        try
        {
            raw = Environment.GetEnvironmentVariable(DaemonInfo.ParentPidEnvVar);
        }
        catch (Exception)
        {
            raw = null;
        }

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var configured) && configured > 0)
            return configured;

        return OperatingSystem.IsLinux() ? ReadLinuxParentPid() : null;
    }

    /// <summary>/proc/self/stat field 4. The comm field can contain spaces, so scan past its ')'.</summary>
    private static int? ReadLinuxParentPid()
    {
        string stat;
        try
        {
            stat = File.ReadAllText("/proc/self/stat");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var close = stat.LastIndexOf(')');
        if (close < 0 || close + 2 >= stat.Length) return null;

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2) return null;

        return int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;
    }
}
