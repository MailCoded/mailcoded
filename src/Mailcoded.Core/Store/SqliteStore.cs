using System.Globalization;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

/// <summary>
/// The one SQLite adapter. All SQL in the product lives behind this class (ARCHITECTURE §12.1);
/// there is deliberately no repository interface over it (CLAUDE invariant 12).
/// </summary>
/// <remarks>
/// Concurrency model, per RELIABILITY §14.3: every mutation is serialized through one writer
/// thread, and reads run on a small pool of query-only connections that WAL lets run alongside it.
/// Mutations are therefore <c>Task</c>-returning because they genuinely cross a thread boundary;
/// reads are synchronous because SQLite's async API is a fiction that only allocates.
/// </remarks>
public sealed partial class SqliteStore : IDisposable
{
    private readonly SqliteStoreOptions _options;
    private readonly IClock _clock;
    private readonly string _writerConnectionString;
    private readonly string _readerConnectionString;
    private readonly WriterQueue _writer;
    private readonly ReaderPool _readers;
    private bool _disposed;

    static SqliteStore() => SQLitePCL.Batteries_V2.Init();

    public SqliteStore(SqliteStoreOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _options = options;
        _clock = clock;

        DatabasePath = options.DatabasePath
            ?? Path.Combine(options.DataDirectory ?? StorePaths.DefaultDataDirectory(), StorePaths.DatabaseFileName);
        DatabasePath = Path.GetFullPath(DatabasePath);

        DataDirectory = options.DataDirectory
            ?? Path.GetDirectoryName(DatabasePath)
            ?? StorePaths.DefaultDataDirectory();

        BlobDirectory = options.BlobDirectory ?? StorePaths.BlobDirectoryFor(DataDirectory);

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BlobDirectory);

        var isNewDatabase = !File.Exists(DatabasePath);

        _writerConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Never Cache=Shared with WAL (PERFORMANCE §15.1).
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

        _readerConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

        DbSession writerSession;
        try
        {
            writerSession = OpenWriterSession(isNewDatabase);
        }
        catch (SqliteException ex)
        {
            throw Db.Translate(ex);
        }

        try
        {
            SchemaVersion = Migrator.Apply(writerSession);
        }
        catch (SqliteException ex)
        {
            writerSession.Dispose();
            throw Db.Translate(ex);
        }
        catch
        {
            writerSession.Dispose();
            throw;
        }

        _writer = new WriterQueue(writerSession, "mailcoded-store-writer");
        _readers = new ReaderPool(options.ReaderPoolSize, OpenReaderSession);
    }

    /// <summary>Opens the store at the per-OS default location from SPEC §5.3.</summary>
    public static SqliteStore Open(string? databasePath = null, IClock? clock = null) =>
        new(new SqliteStoreOptions { DatabasePath = databasePath }, clock ?? SystemClock.Instance);

    public string DatabasePath { get; }

    public string DataDirectory { get; }

    public string BlobDirectory { get; }

    public int SchemaVersion { get; }

    public IClock Clock => _clock;

    // ---- connection setup -------------------------------------------------

    private DbSession OpenWriterSession(bool isNewDatabase)
    {
        var connection = new SqliteConnection(_writerConnectionString);
        connection.Open();
        var session = new DbSession(connection);

        session.Exec(Pragma("busy_timeout", _options.BusyTimeoutMs));

        if (isNewDatabase && _options.PageSize is { } pageSize)
            session.Exec(Pragma("page_size", pageSize));

        // Chunked reclaim beats a full VACUUM (2x disk + exclusive lock). Only latches on a new file.
        session.Exec("PRAGMA auto_vacuum = INCREMENTAL");

        var journalMode = session.ExecScalar("PRAGMA journal_mode = WAL") as string;
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            session.Dispose();
            throw new StoreException(
                FailureCategory.Unsupported,
                $"The store could not enter WAL mode (journal_mode is '{journalMode ?? "unknown"}'). A network filesystem cannot host the mail store.");
        }

        session.Exec("PRAGMA synchronous = NORMAL");
        session.Exec(Pragma("cache_size", -_options.CacheSizeKiB));
        session.Exec("PRAGMA temp_store = MEMORY");
        session.Exec(Pragma("mmap_size", _options.MmapSizeBytes));
        session.Exec("PRAGMA foreign_keys = ON");
        session.Exec(Pragma("wal_autocheckpoint", _options.WalAutoCheckpointPages));
        session.Exec(Pragma("journal_size_limit", _options.JournalSizeLimitBytes));

        AssertFts5(session);
        return session;
    }

    private DbSession OpenReaderSession()
    {
        var connection = new SqliteConnection(_readerConnectionString);
        connection.Open();
        var session = new DbSession(connection);

        session.Exec(Pragma("busy_timeout", _options.BusyTimeoutMs));
        session.Exec(Pragma("cache_size", -_options.CacheSizeKiB));
        session.Exec("PRAGMA temp_store = MEMORY");
        session.Exec(Pragma("mmap_size", _options.MmapSizeBytes));
        session.Exec("PRAGMA foreign_keys = ON");

        // A reader must never be able to write, whatever SQL reaches it (AGENT-INTERFACE §13.2).
        session.Exec("PRAGMA query_only = 1");
        return session;
    }

    private static string Pragma(string name, long value) =>
        string.Create(CultureInfo.InvariantCulture, $"PRAGMA {name} = {value}");

    private static string PragmaCall(string name, long value) =>
        string.Create(CultureInfo.InvariantCulture, $"PRAGMA {name}({value})");

    /// <summary>
    /// SPEC §5.1: FTS5 is a hard requirement, so a build without it must fail loudly at startup
    /// rather than degrade search silently.
    /// </summary>
    private static void AssertFts5(DbSession session)
    {
        foreach (var option in session.ExecStrings("PRAGMA compile_options"))
            if (option.Equals("ENABLE_FTS5", StringComparison.Ordinal))
                return;

        var version = session.ExecScalar("SELECT sqlite_version()") as string ?? "unknown";
        session.Dispose();
        throw new StoreException(
            FailureCategory.Unsupported,
            $"The SQLite library in this process was built without ENABLE_FTS5 (sqlite_version {version}). "
            + "mailcoded requires FTS5 for search. Ensure the SQLitePCLRaw.bundle_e_sqlite3 native library "
            + "is the one being loaded, not a system libsqlite3 compiled without FTS5.");
    }

    // ---- execution helpers ------------------------------------------------

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Runs <paramref name="work"/> on the writer thread inside one transaction.</summary>
    private Task<T> WriteAsync<T>(Func<WriteContext, T> work, CancellationToken ct)
    {
        ThrowIfDisposed();
        return _writer.EnqueueAsync(session =>
        {
            var context = new WriteContext(session, _clock);
            session.BeginImmediate();
            try
            {
                var result = work(context);
                context.Flush();
                session.Commit();
                return result;
            }
            catch (SqliteException ex)
            {
                session.Rollback();
                throw Db.Translate(ex);
            }
            catch
            {
                session.Rollback();
                throw;
            }
        }, ct);
    }

    private Task WriteAsync(Action<WriteContext> work, CancellationToken ct) =>
        WriteAsync(context => { work(context); return true; }, ct);

    /// <summary>Runs <paramref name="work"/> on the writer thread with no enclosing transaction.</summary>
    private Task<T> WriteUnwrappedAsync<T>(Func<DbSession, T> work, CancellationToken ct)
    {
        ThrowIfDisposed();
        return _writer.EnqueueAsync(session =>
        {
            try { return work(session); }
            catch (SqliteException ex) { throw Db.Translate(ex); }
        }, ct);
    }

    private T Read<T>(Func<DbSession, T> work, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var lease = _readers.Rent(ct);
        try
        {
            return work(lease.Session);
        }
        catch (SqliteException ex)
        {
            throw Db.Translate(ex);
        }
    }

    internal static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    /// <summary>Clamps out-of-range stored timestamps rather than throwing (edge case 18).</summary>
    internal static DateTimeOffset FromUnixMs(long milliseconds)
    {
        const long min = -62135596800000L;
        const long max = 253402300799999L;
        if (milliseconds < min) milliseconds = min;
        if (milliseconds > max) milliseconds = max;
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readers.Dispose();
        _writer.Dispose();
    }
}

/// <summary>
/// One writer transaction. Folder counter deltas accumulate here and are applied before the
/// commit, so <c>folders.unread_count</c> is never out of step with <c>messages</c> (PERFORMANCE §15.4).
/// </summary>
internal sealed class WriteContext
{
    private const string UpdateCounts =
        "UPDATE folders SET total_count = MAX(0, total_count + $total), unread_count = MAX(0, unread_count + $unread) WHERE id = $id";

    private Dictionary<long, Counters>? _folderDeltas;

    public WriteContext(DbSession session, IClock clock)
    {
        Session = session;
        Clock = clock;
    }

    public DbSession Session { get; }

    public IClock Clock { get; }

    public void AddFolderDelta(long folderId, int totalDelta, int unreadDelta)
    {
        if (totalDelta == 0 && unreadDelta == 0) return;

        _folderDeltas ??= new Dictionary<long, Counters>();
        _folderDeltas.TryGetValue(folderId, out var current);
        _folderDeltas[folderId] = new Counters(current.Total + totalDelta, current.Unread + unreadDelta);
    }

    public void Flush()
    {
        if (_folderDeltas is null || _folderDeltas.Count == 0) return;

        var statement = Session.Prepare(UpdateCounts, "$total", "$unread", "$id");
        foreach (var (folderId, counters) in _folderDeltas)
        {
            statement
                .SetInt(0, counters.Total)
                .SetInt(1, counters.Unread)
                .SetInt(2, folderId)
                .Execute();
        }

        _folderDeltas.Clear();
    }

    private readonly record struct Counters(int Total, int Unread);
}
