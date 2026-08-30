using System.Globalization;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Tests.Support;

/// <summary>Reads raw rows through the store's own gated read-only surface, never a second connection.</summary>
public static class StoreQuery
{
    private const int MaxRows = 5000;

    public static IReadOnlyList<string> Strings(SqliteStore store, string sql, CancellationToken ct)
    {
        var result = store.ExecuteReadOnlyQuery(sql, MaxRows, ct);
        var values = new List<string>(result.Rows.Count);

        foreach (var row in result.Rows)
            values.Add(row.Count == 0 || row[0] is null
                ? string.Empty
                : Convert.ToString(row[0], CultureInfo.InvariantCulture) ?? string.Empty);

        return values;
    }

    public static long Scalar(SqliteStore store, string sql, CancellationToken ct)
    {
        var result = store.ExecuteReadOnlyQuery(sql, 1, ct);
        if (result.Rows.Count == 0 || result.Rows[0].Count == 0)
            throw new InvalidOperationException($"'{sql}' returned no rows.");

        var value = result.Rows[0][0] ?? throw new InvalidOperationException($"'{sql}' returned NULL.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> ColumnNames(SqliteStore store, string table, CancellationToken ct) =>
        Strings(store, $"SELECT name FROM pragma_table_info('{table}') ORDER BY name", ct);

    public static string SchemaSignature(SqliteStore store, CancellationToken ct)
    {
        var rows = Strings(
            store,
            // sqlite_stat1 and friends are statistics, not schema: PRAGMA optimize creates them on
            // session close, so including them would make any reopen look like a schema change.
            "SELECT type || '|' || name || '|' || COALESCE(sql, '') FROM sqlite_master "
            + "WHERE name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY type, name",
            ct);

        return string.Join("\n", rows);
    }
}
