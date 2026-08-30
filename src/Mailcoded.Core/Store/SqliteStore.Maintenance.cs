using System.Diagnostics;
using Mailcoded.Core.Providers;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private static readonly string[] CountedTables =
    [
        "accounts", "folders", "messages", "blobs", "tags", "body_text", "outbox", "sync_log",
    ];

    /// <summary>
    /// The periodic health pass from RELIABILITY §14.3: chunked reclaim, FTS optimize after churn,
    /// and a WAL truncate so a long-lived reader cannot starve the checkpointer forever.
    /// </summary>
    public Task<MaintenanceReport> RunMaintenanceAsync(MaintenanceOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        return WriteUnwrappedAsync(session =>
        {
            var started = Stopwatch.GetTimestamp();
            string? integrity = null;
            long checkpointed = 0;
            long reclaimed = 0;
            var ok = true;

            if (options.QuickCheck)
            {
                integrity = string.Join("; ", session.ExecStrings("PRAGMA quick_check"));
                ok = integrity.Equals("ok", StringComparison.Ordinal);
            }

            if (options.OptimizeFts) Fts.Optimize(session);

            if (options.IncrementalVacuum)
            {
                var before = session.ExecScalarInt64("PRAGMA freelist_count");
                session.Exec(PragmaCall("incremental_vacuum", options.IncrementalVacuumPages));
                var after = session.ExecScalarInt64("PRAGMA freelist_count");
                reclaimed = before - after;
            }

            if (options.Analyze) session.Exec("ANALYZE");

            if (options.CheckpointWal)
                checkpointed = CheckpointCore(session, options.TruncateWal);

            return new MaintenanceReport
            {
                Ok = ok,
                IntegrityResult = integrity,
                WalPagesCheckpointed = checkpointed,
                ReclaimedPages = reclaimed,
                Elapsed = Stopwatch.GetElapsedTime(started),
            };
        }, ct);
    }

    public Task<string> QuickCheckAsync(CancellationToken ct) =>
        WriteUnwrappedAsync(session => string.Join("; ", session.ExecStrings("PRAGMA quick_check")), ct);

    public Task<long> CheckpointWalAsync(bool truncate, CancellationToken ct) =>
        WriteUnwrappedAsync(session => CheckpointCore(session, truncate), ct);

    public Task<long> IncrementalVacuumAsync(int pages, CancellationToken ct) =>
        WriteUnwrappedAsync(session =>
        {
            var before = session.ExecScalarInt64("PRAGMA freelist_count");
            session.Exec(PragmaCall("incremental_vacuum", pages <= 0 ? 256 : pages));
            return before - session.ExecScalarInt64("PRAGMA freelist_count");
        }, ct);

    public Task OptimizeFtsAsync(CancellationToken ct) =>
        WriteUnwrappedAsync(session => { Fts.Optimize(session); return true; }, ct);

    public Task AnalyzeAsync(CancellationToken ct) =>
        WriteUnwrappedAsync(session => session.Exec("ANALYZE"), ct);

    /// <summary>
    /// Consistent hot backup via <c>VACUUM INTO</c> — the documented answer to "user copied the DB
    /// while it was running" (edge case 34). The destination is a bound parameter, never concatenated.
    /// </summary>
    public Task BackupToAsync(string destinationPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullPath = Path.GetFullPath(destinationPath);

        if (File.Exists(fullPath))
            throw new StoreException(FailureCategory.Protocol, "The backup destination already exists.");

        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null) Directory.CreateDirectory(directory);

        return WriteUnwrappedAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "VACUUM INTO $destination";
            command.Parameters.AddWithValue("$destination", fullPath);
            command.ExecuteNonQuery();
            return true;
        }, ct);
    }

    /// <summary>Numbers for the <c>stats</c> RPC (RELIABILITY §14.6).</summary>
    public StoreStats GetStats(CancellationToken ct = default) =>
        Read(session =>
        {
            var pageSize = session.ExecScalarInt64("PRAGMA page_size");
            var pageCount = session.ExecScalarInt64("PRAGMA page_count");
            var freelist = session.ExecScalarInt64("PRAGMA freelist_count");

            var counts = new Dictionary<string, long>(CountedTables.Length, StringComparer.Ordinal);
            foreach (var table in CountedTables)
            {
                ct.ThrowIfCancellationRequested();
                // Table names come from a private constant array, never from a caller.
                counts[table] = session.ExecScalarInt64("SELECT COUNT(*) FROM " + table);
            }

            return new StoreStats
            {
                DatabasePath = DatabasePath,
                SchemaVersion = SchemaVersion,
                DatabaseSizeBytes = FileLength(DatabasePath),
                WalSizeBytes = FileLength(DatabasePath + "-wal"),
                PageCount = pageCount,
                PageSizeBytes = pageSize,
                FreelistPages = freelist,
                BlobDirectorySizeBytes = DirectorySize(BlobDirectory),
                TableCounts = counts,
            };
        }, ct);

    private static long CheckpointCore(DbSession session, bool truncate)
    {
        var mode = truncate ? "PRAGMA wal_checkpoint(TRUNCATE)" : "PRAGMA wal_checkpoint(PASSIVE)";
        using var command = session.Connection.CreateCommand();
        command.CommandText = mode;
        using var reader = command.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(2) ? reader.GetInt64(2) : 0;
    }

    private static long FileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                total += FileLength(file);
        }
        catch (IOException)
        {
            return total;
        }
        catch (UnauthorizedAccessException)
        {
            return total;
        }

        return total;
    }
}
