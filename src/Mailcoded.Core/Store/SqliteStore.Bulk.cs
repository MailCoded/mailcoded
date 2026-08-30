using System.Diagnostics;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    /// <summary>Kept in step with the index list in Migrations/001_initial.sql.</summary>
    private static readonly string[] SecondaryIndexNames =
    [
        "ix_msg_folder_date",
        "ix_msg_unread",
        "ix_msg_thread",
        "ix_msg_folder_uid",
        "ix_msg_message_id",
    ];

    private static readonly string[] SecondaryIndexDdl =
    [
        "CREATE INDEX IF NOT EXISTS ix_msg_folder_date ON messages(folder_id, date_utc DESC, id, subject, from_addr, flags)",
        "CREATE INDEX IF NOT EXISTS ix_msg_unread ON messages(folder_id) WHERE (flags & 1) = 0",
        "CREATE INDEX IF NOT EXISTS ix_msg_thread ON messages(thread_key, date_utc DESC, id)",
        "CREATE UNIQUE INDEX IF NOT EXISTS ix_msg_folder_uid ON messages(folder_id, uid)",
        "CREATE INDEX IF NOT EXISTS ix_msg_message_id ON messages(message_id)",
    ];

    private int _bulkSessions;

    /// <summary>
    /// Opens the PERFORMANCE §15.5 backfill window: <c>synchronous=OFF</c>, a large page cache,
    /// dropped secondary indexes and deferred FTS population, with a periodic commit that caps WAL
    /// growth. Explicitly scoped — disposing the session always restores steady-state settings.
    /// </summary>
    /// <remarks>
    /// <c>synchronous=OFF</c> survives an application crash but not a power cut. That is acceptable
    /// only here, because IMAP is the source of truth and the store can be rebuilt.
    /// </remarks>
    public Task<BulkIngestSession> BeginBulkIngestAsync(IThreader? threader, CancellationToken ct)
    {
        ThrowIfDisposed();

        if (Interlocked.CompareExchange(ref _bulkSessions, 1, 0) != 0)
            throw new StoreException(FailureCategory.Busy, "A bulk-ingest window is already open on this store.");

        try
        {
            return BeginBulkIngestCoreAsync(threader, ct);
        }
        catch
        {
            Interlocked.Exchange(ref _bulkSessions, 0);
            throw;
        }
    }

    private async Task<BulkIngestSession> BeginBulkIngestCoreAsync(IThreader? threader, CancellationToken ct)
    {
        try
        {
            await WriteUnwrappedAsync(session =>
            {
                session.Exec("PRAGMA synchronous = OFF");
                session.Exec(Pragma("cache_size", -_options.BulkCacheSizeKiB));
                session.Exec("PRAGMA temp_store = MEMORY");

                foreach (var index in SecondaryIndexNames)
                    session.Exec("DROP INDEX IF EXISTS " + index);

                session.Exec("CREATE TEMP TABLE IF NOT EXISTS bulk_pending (id INTEGER PRIMARY KEY)");
                session.Exec("DELETE FROM bulk_pending");
                session.BeginImmediate();
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            await RestoreSteadyStateAsync(CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _bulkSessions, 0);
            throw;
        }

        return new BulkIngestSession(this, threader);
    }

    internal Task<IngestResult> BulkAddAsync(
        FolderId folderId,
        IReadOnlyList<RemoteEnvelope> envelopes,
        IThreader? threader,
        int commitInterval,
        BulkIngestSession owner,
        CancellationToken ct) =>
        WriteUnwrappedAsync(session =>
        {
            var context = new WriteContext(session, _clock);
            var accountId = RequireAccountForFolder(session, folderId.Value);
            var inserted = 0;
            var updated = 0;

            foreach (var envelope in envelopes)
            {
                ct.ThrowIfCancellationRequested();
                if (UpsertEnvelope(context, accountId, folderId.Value, envelope, threader, deferFts: true)) inserted++;
                else updated++;
            }

            context.Flush();

            // Commit periodically to cap WAL growth, never for throughput (PERFORMANCE §15.5).
            if (owner.AdvanceCommitCounter(inserted + updated, commitInterval))
            {
                session.Commit();
                session.BeginImmediate();
            }

            return new IngestResult(inserted, updated);
        }, ct);

    internal Task<int> BulkFinishAsync(CancellationToken ct) =>
        WriteUnwrappedAsync(session =>
        {
            var ftsRows = 0;
            try
            {
                session.Commit();
                session.BeginImmediate();

                foreach (var ddl in SecondaryIndexDdl) session.Exec(ddl);

                ftsRows = session.Exec(
                    "INSERT INTO msg_fts(rowid, subject, body_text, from_addr, to_addr) "
                    + "SELECT m.id, COALESCE(m.subject, ''), COALESCE(b.text, ''), COALESCE(m.from_addr, ''), COALESCE(m.to_addrs, '') "
                    + "FROM messages m JOIN bulk_pending p ON p.id = m.id "
                    + "LEFT JOIN body_text b ON b.message_id = m.id");

                session.Exec(
                    "INSERT INTO msg_fts_cjk(rowid, subject, body_text) "
                    + "SELECT m.id, COALESCE(m.subject, ''), COALESCE(b.text, '') "
                    + "FROM messages m JOIN bulk_pending p ON p.id = m.id "
                    + "LEFT JOIN body_text b ON b.message_id = m.id");

                session.Exec("DELETE FROM bulk_pending");
                session.Commit();

                Fts.Optimize(session);
                session.Exec("ANALYZE");
                session.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
            }
            catch
            {
                session.Rollback();
                throw;
            }
            finally
            {
                RestoreSteadyStatePragmas(session);
                Interlocked.Exchange(ref _bulkSessions, 0);
            }

            return ftsRows;
        }, ct);

    internal Task BulkAbortAsync(CancellationToken ct) =>
        WriteUnwrappedAsync(session =>
        {
            try
            {
                session.Rollback();
                foreach (var ddl in SecondaryIndexDdl) session.Exec(ddl);
                session.Exec("DELETE FROM bulk_pending");
            }
            finally
            {
                RestoreSteadyStatePragmas(session);
                Interlocked.Exchange(ref _bulkSessions, 0);
            }

            return true;
        }, ct);

    private Task RestoreSteadyStateAsync(CancellationToken ct) =>
        WriteUnwrappedAsync(session =>
        {
            RestoreSteadyStatePragmas(session);
            return true;
        }, ct);

    private void RestoreSteadyStatePragmas(DbSession session)
    {
        session.Exec("PRAGMA synchronous = NORMAL");
        session.Exec(Pragma("cache_size", -_options.CacheSizeKiB));
    }

    internal int BulkCommitInterval => _options.BulkCommitInterval;
}

/// <summary>
/// The scoped backfill window. Disposing without calling <see cref="CompleteAsync"/> rolls the
/// window back and always restores <c>synchronous=NORMAL</c> and the secondary indexes.
/// </summary>
public sealed class BulkIngestSession : IAsyncDisposable, IDisposable
{
    private readonly SqliteStore _store;
    private readonly IThreader? _threader;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _sinceCommit;
    private int _inserted;
    private int _updated;
    private int _ftsRows;
    private bool _finished;

    internal BulkIngestSession(SqliteStore store, IThreader? threader)
    {
        _store = store;
        _threader = threader;
    }

    public int Inserted => _inserted;

    public int Updated => _updated;

    public async Task<IngestResult> AddAsync(
        FolderId folderId,
        IReadOnlyList<RemoteEnvelope> envelopes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        ObjectDisposedException.ThrowIf(_finished, this);
        if (envelopes.Count == 0) return new IngestResult(0, 0);

        var result = await _store
            .BulkAddAsync(folderId, envelopes, _threader, _store.BulkCommitInterval, this, ct)
            .ConfigureAwait(false);

        _inserted += result.Inserted;
        _updated += result.Updated;
        return result;
    }

    /// <summary>Commits, rebuilds the indexes, populates FTS in bulk, optimizes, ANALYZEs, checkpoints.</summary>
    public async Task<BulkIngestReport> CompleteAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;

        _ftsRows = await _store.BulkFinishAsync(ct).ConfigureAwait(false);
        _stopwatch.Stop();

        return new BulkIngestReport
        {
            Inserted = _inserted,
            Updated = _updated,
            FtsRowsPopulated = _ftsRows,
            Elapsed = _stopwatch.Elapsed,
        };
    }

    internal bool AdvanceCommitCounter(int rows, int interval)
    {
        _sinceCommit += rows;
        if (_sinceCommit < interval) return false;
        _sinceCommit = 0;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_finished) return;
        _finished = true;
        await _store.BulkAbortAsync(CancellationToken.None).ConfigureAwait(false);
        _stopwatch.Stop();
    }

    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        _store.BulkAbortAsync(CancellationToken.None).GetAwaiter().GetResult();
        _stopwatch.Stop();
    }
}
