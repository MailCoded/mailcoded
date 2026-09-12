using System.Globalization;
using Microsoft.Data.Sqlite;
using Mailcoded.Core.Providers;

namespace Mailcoded.Core.Store;

/// <summary>
/// One open SQLite connection plus its prepared-statement cache. Never shared between threads:
/// a session belongs either to the writer thread or to whoever currently holds the reader lease.
/// </summary>
internal sealed class DbSession : IDisposable
{
    private readonly Dictionary<string, SqliteCommand> _prepared = new(StringComparer.Ordinal);
    private bool _disposed;

    public DbSession(SqliteConnection connection) => Connection = connection;

    public SqliteConnection Connection { get; }

    public int TransactionDepth { get; private set; }

    /// <summary>
    /// Returns a cached, prepared command for <paramref name="sql"/>. Parameters are created once
    /// with the given names and rebound by ordinal on every execution.
    /// </summary>
    public Stmt Prepare(string sql, params string[] parameterNames)
    {
        if (!_prepared.TryGetValue(sql, out var cmd))
        {
            cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var name in parameterNames)
                cmd.Parameters.Add(new SqliteParameter(name, DBNull.Value));
            cmd.Prepare();
            _prepared.Add(sql, cmd);
        }

        return new Stmt(cmd);
    }

    /// <summary>Runs SQL that is not worth caching (DDL, PRAGMAs, migration scripts).</summary>
    public int Exec(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    public object? ExecScalar(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public long ExecScalarInt64(string sql, long fallback = 0)
    {
        var value = ExecScalar(sql);
        return value is null or DBNull ? fallback : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public List<string> ExecStrings(string sql)
    {
        var result = new List<string>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        return result;
    }

    /// <summary>
    /// Raw BEGIN/COMMIT rather than <see cref="SqliteConnection.BeginTransaction()"/>: a cached
    /// command carries its own Transaction reference and would be rejected by the provider.
    /// </summary>
    public void BeginImmediate()
    {
        if (TransactionDepth++ > 0) return;
        Exec("BEGIN IMMEDIATE");
    }

    public void Commit()
    {
        if (TransactionDepth == 0) return;
        if (--TransactionDepth > 0) return;
        Exec("COMMIT");
    }

    public void Rollback()
    {
        if (TransactionDepth == 0) return;
        TransactionDepth = 0;
        try { Exec("ROLLBACK"); }
        catch (SqliteException) { /* nothing to roll back */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var cmd in _prepared.Values) cmd.Dispose();
        _prepared.Clear();

        try { Exec("PRAGMA optimize"); }
        catch (SqliteException) { /* best effort on close */ }

        Connection.Dispose();
    }
}

/// <summary>A prepared command with ordinal parameter binding. Rebind, execute, repeat.</summary>
internal readonly struct Stmt
{
    private readonly SqliteCommand _cmd;

    public Stmt(SqliteCommand cmd) => _cmd = cmd;

    public SqliteCommand Command => _cmd;

    public Stmt SetInt(int ordinal, long value)
    {
        _cmd.Parameters[ordinal].Value = value;
        return this;
    }

    public Stmt SetIntOrNull(int ordinal, long? value)
    {
        _cmd.Parameters[ordinal].Value = value.HasValue ? value.Value : DBNull.Value;
        return this;
    }

    public Stmt SetBool(int ordinal, bool value)
    {
        _cmd.Parameters[ordinal].Value = value ? 1L : 0L;
        return this;
    }

    public Stmt SetText(int ordinal, string? value)
    {
        _cmd.Parameters[ordinal].Value = value is null ? DBNull.Value : value;
        return this;
    }

    public Stmt SetBlob(int ordinal, byte[]? value)
    {
        _cmd.Parameters[ordinal].Value = value is null ? DBNull.Value : value;
        return this;
    }

    public Stmt SetReal(int ordinal, double value)
    {
        _cmd.Parameters[ordinal].Value = value;
        return this;
    }

    public int Execute() => _cmd.ExecuteNonQuery();

    public SqliteDataReader ExecuteReader() => _cmd.ExecuteReader();

    public long ExecuteInt64(long fallback = 0)
    {
        var value = _cmd.ExecuteScalar();
        return value is null or DBNull ? fallback : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public long? ExecuteNullableInt64()
    {
        var value = _cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public string? ExecuteString()
    {
        var value = _cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}

internal static class Db
{
    public static string? Str(SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    public static long Int(SqliteDataReader r, int ordinal, long fallback = 0) =>
        r.IsDBNull(ordinal) ? fallback : r.GetInt64(ordinal);

    public static long? IntOrNull(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetInt64(ordinal);

    public static bool Bool(SqliteDataReader r, int ordinal) => !r.IsDBNull(ordinal) && r.GetInt64(ordinal) != 0;

    public static byte[]? Blob(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : (byte[])r.GetValue(ordinal);

    public static double Real(SqliteDataReader r, int ordinal, double fallback = 0) =>
        r.IsDBNull(ordinal) ? fallback : r.GetDouble(ordinal);

    /// <summary>
    /// Maps a SQLite error onto the category vocabulary the reconnect/backoff rules use.
    /// Never surfaces the SQL text, which could contain message content.
    /// </summary>
    public static StoreException Translate(SqliteException ex) => ex.SqliteErrorCode switch
    {
        5 or 6 => new StoreException(FailureCategory.Busy, "The local store is busy; another writer holds the lock.", ex),
        8 or 776 => new StoreException(FailureCategory.Unsupported, "The local store is read-only.", ex),
        11 or 267 => new StoreException(FailureCategory.Protocol, "The local store is corrupt. Delete it and re-sync; back up tags and the outbox first.", ex),
        13 => new StoreException(FailureCategory.Full, "The disk holding the local store is full. Sync is paused.", ex),
        10 => new StoreException(FailureCategory.Network, "The local store could not be read from disk.", ex),
        19 => new StoreException(FailureCategory.Protocol, "The write violates a store constraint.", ex),
        _ => new StoreException(FailureCategory.Protocol, $"The local store rejected an operation (SQLite error {ex.SqliteErrorCode}).", ex),
    };
}
