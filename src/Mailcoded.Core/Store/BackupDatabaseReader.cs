using System.Globalization;
using System.Text;
using Mailcoded.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

/// <summary>What a backup's database file says about itself, read without ever writing to it.</summary>
public sealed record BackupDatabaseFacts
{
    public int UserVersion { get; init; }

    public IReadOnlyList<string> IntegrityMessages { get; init; } = [];

    public required IReadOnlyDictionary<string, long> TableCounts { get; init; }

    public required IReadOnlyList<string> ExternalBlobHashes { get; init; }
}

/// <summary>Opens a copy read-only and immutable, so no -wal or -shm appears. The file may be hostile.</summary>
public static class BackupDatabaseReader
{
    public static string ImmutableUri(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        var text = fullPath.Replace('\\', '/');
        var builder = new StringBuilder("file:", text.Length + 24);

        var start = 0;
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
        {
            builder.Append('/').Append(text[0]).Append(':');
            start = 2;
        }

        var rest = text[start..];
        if (rest.Length > 0 && rest[0] != '/') builder.Append('/');

        foreach (var c in rest)
        {
            if (c == '/' || char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~') builder.Append(c);
            else foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.Append("?mode=ro&immutable=1").ToString();
    }

    public static BackupDatabaseFacts Read(string fullPath, bool fullIntegrityCheck, CancellationToken ct)
    {
        try
        {
            return ReadCore(fullPath, fullIntegrityCheck, ct);
        }
        catch (SqliteException ex)
        {
            throw new StoreException(
                FailureCategory.Protocol,
                string.Create(CultureInfo.InvariantCulture, $"The database could not be read (SQLite error {ex.SqliteErrorCode})."),
                ex);
        }
    }

    private static BackupDatabaseFacts ReadCore(string fullPath, bool fullIntegrityCheck, CancellationToken ct)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ImmutableUri(fullPath),
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();

        Execute(connection, "PRAGMA trusted_schema = OFF");
        Execute(connection, "PRAGMA query_only = 1");

        var userVersion = (int)ScalarLong(connection, "PRAGMA user_version");

        var integrity = new List<string>();
        if (fullIntegrityCheck)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check(20)";
            using var reader = check.ExecuteReader();
            while (reader.Read()) integrity.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in CountedTables(connection))
        {
            ct.ThrowIfCancellationRequested();
            counts[name] = ScalarLong(connection, "SELECT COUNT(*) FROM \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        }

        var hashes = new List<string>();
        if (counts.ContainsKey("blobs"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT sha256 FROM blobs WHERE ext_path IS NOT NULL ORDER BY sha256";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                hashes.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }
        }

        return new BackupDatabaseFacts
        {
            UserVersion = userVersion,
            IntegrityMessages = integrity,
            TableCounts = counts,
            ExternalBlobHashes = hashes,
        };
    }

    private static List<string> CountedTables(SqliteConnection connection)
    {
        var tables = new List<(string Name, bool Virtual)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var sql = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                tables.Add((name, sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)));
            }
        }

        var virtualNames = new List<string>();
        foreach (var table in tables)
            if (table.Virtual) virtualNames.Add(table.Name);

        var counted = new List<string>();
        foreach (var (name, isVirtual) in tables)
        {
            if (isVirtual || name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
            if (virtualNames.Exists(v => name.StartsWith(v + "_", StringComparison.Ordinal))) continue;
            counted.Add(name);
        }

        return counted;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
