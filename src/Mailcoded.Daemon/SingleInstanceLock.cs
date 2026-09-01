using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Mailcoded.Daemon;

/// <summary>
/// The store-wide single-instance guard from RELIABILITY §14.4. Five VS Code windows must not open
/// five IMAP watch sets against one account — Gmail caps a mailbox at roughly fifteen connections.
/// </summary>
/// <remarks>
/// v0.1 is stdio-only, so there is no rendezvous transport to hand a secondary process off to the
/// owner. A secondary therefore still serves RPC from the shared store but never opens a watch
/// connection, and <c>health</c> says so.
/// </remarks>
internal sealed class SingleInstanceLock : IDisposable
{
    public const string LockFileName = "daemon.lock";

    /// <summary>Diagnostics only. The lock is the OS lock on <see cref="LockFileName"/>, never this.</summary>
    public const string OwnerFileName = "daemon.owner";

    private readonly FileStream? handle;

    private SingleInstanceLock(FileStream? handle, string path, bool isPrimary, int ownerPid, string? detail)
    {
        this.handle = handle;
        Path = path;
        IsPrimary = isPrimary;
        OwnerPid = ownerPid;
        Detail = detail;
    }

    public string Path { get; }

    /// <summary>True when this process owns the store's watch connections.</summary>
    public bool IsPrimary { get; }

    /// <summary>PID of the live owner when this process is a secondary; 0 when it is unknown.</summary>
    public int OwnerPid { get; }

    /// <summary>Why primary ownership was refused, for the health report. Never a path a client owns.</summary>
    public string? Detail { get; }

    public static SingleInstanceLock Acquire(string dataDirectory, StderrLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(log);

        var path = System.IO.Path.Combine(dataDirectory, LockFileName);
        var ownerPath = System.IO.Path.Combine(dataDirectory, OwnerFileName);

        FileStream stream;
        try
        {
            // FileShare.None is the only share mode .NET maps to an *exclusive* advisory lock on
            // Unix; FileShare.Read maps to a shared one, which excludes nothing. The OS releases it
            // when the handle closes or the process dies, so a stale lock cannot outlive its owner
            // and no liveness guess is needed.
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            var owner = ReadOwnerPid(ownerPath);
            if (owner > 0 && !IsAlive(owner)) owner = 0;

            log.Warn(owner > 0
                ? $"Another live daemon (pid {owner.ToString(CultureInfo.InvariantCulture)}) owns this store; watch connections stay closed here."
                : "Another live daemon owns this store; watch connections stay closed here.");

            return new SingleInstanceLock(null, path, isPrimary: false, ownerPid: owner, detail: "another-daemon-owns-store");
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warn($"The store lock file could not be opened: {ex.Message}");
            return new SingleInstanceLock(null, path, isPrimary: false, ownerPid: 0, detail: "lock-file-unavailable");
        }

        Stamp(ownerPath, log);
        log.Info($"This instance owns the store lock at {LockFileName}.");
        return new SingleInstanceLock(stream, path, isPrimary: true, ownerPid: Environment.ProcessId, detail: null);
    }

    /// <summary>Best effort: losing the diagnostic stamp must never cost this process the lock.</summary>
    private static void Stamp(string ownerPath, StderrLog log)
    {
        var text = new StringBuilder(96)
            .Append("pid=").Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("started=").Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append('\n')
            .Append("version=").Append(DaemonInfo.Version).Append('\n')
            .ToString();

        try
        {
            using var stream = new FileStream(
                ownerPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 1024,
                FileOptions.None);

            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: false);
        }
        catch (IOException ex)
        {
            log.Debug($"The owner stamp could not be written: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Debug($"The owner stamp could not be written: {ex.Message}");
        }
    }

    private static int ReadOwnerPid(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith("pid=", StringComparison.Ordinal)) continue;
                return int.TryParse(line.AsSpan(4).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    ? pid
                    : 0;
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        return 0;
    }

    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose() => handle?.Dispose();
}
