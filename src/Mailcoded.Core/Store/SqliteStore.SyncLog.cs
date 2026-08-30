using Mailcoded.Core.Domain.Primitives;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const string SelectSyncLog =
        "SELECT id, ts, account_id, level, event, detail, interface, agent_host FROM sync_log";

    private const string InsertSyncLog =
        "INSERT INTO sync_log (ts, account_id, level, event, detail, interface, agent_host) "
        + "VALUES ($ts,$account,$level,$event,$detail,$interface,$host) RETURNING id";

    /// <summary>
    /// Appends an audit row. Callers are responsible for keeping credentials and message bodies
    /// out of <see cref="SyncLogEntry.Detail"/> — this table is never rewritten or pruned.
    /// </summary>
    public Task<long> AppendSyncLogAsync(SyncLogEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return WriteAsync(context =>
        {
            var timestamp = entry.TimestampUtc == default ? context.Clock.UtcNow : entry.TimestampUtc;
            return AppendSyncLogCore(context.Session, entry, timestamp);
        }, ct);
    }

    /// <summary>Appends several audit rows in one transaction.</summary>
    public Task AppendSyncLogAsync(IReadOnlyList<SyncLogEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return WriteAsync(context =>
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var timestamp = entry.TimestampUtc == default ? context.Clock.UtcNow : entry.TimestampUtc;
                AppendSyncLogCore(context.Session, entry, timestamp);
            }
        }, ct);
    }

    /// <summary>Newest-first audit tail, keyset-paged on the row id.</summary>
    public StorePage<SyncLogEntry> ReadSyncLog(
        AccountId? accountId = null,
        int limit = 100,
        long? beforeId = null,
        CancellationToken ct = default)
    {
        var pageSize = NormalizeLimit(limit, 1000);

        return Read(session =>
        {
            var statement = accountId is { } account
                ? session
                    .Prepare(
                        SelectSyncLog + " WHERE account_id = $account AND id < $before ORDER BY id DESC LIMIT $limit",
                        "$account", "$before", "$limit")
                    .SetInt(0, account.Value)
                    .SetInt(1, beforeId ?? long.MaxValue)
                    .SetInt(2, pageSize + 1)
                : session
                    .Prepare(SelectSyncLog + " WHERE id < $before ORDER BY id DESC LIMIT $limit", "$before", "$limit")
                    .SetInt(0, beforeId ?? long.MaxValue)
                    .SetInt(1, pageSize + 1);

            var items = new List<SyncLogEntry>(pageSize);
            var lastId = 0L;
            var more = false;

            using (var reader = statement.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (items.Count == pageSize) { more = true; break; }

                    var entry = MapSyncLog(reader);
                    lastId = entry.Id;
                    items.Add(entry);
                }
            }

            return new StorePage<SyncLogEntry>
            {
                Items = items,
                NextCursor = more ? lastId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                Truncated = more,
            };
        }, ct);
    }

    private static long AppendSyncLogCore(DbSession session, SyncLogEntry entry, DateTimeOffset timestamp) =>
        session
            .Prepare(InsertSyncLog, "$ts", "$account", "$level", "$event", "$detail", "$interface", "$host")
            .SetInt(0, ToUnixMs(timestamp))
            .SetIntOrNull(1, entry.AccountId.IsNone ? null : (long?)entry.AccountId.Value)
            .SetText(2, entry.Level)
            .SetText(3, entry.Event)
            .SetText(4, entry.Detail)
            .SetText(5, entry.Interface)
            .SetText(6, entry.AgentHost)
            .ExecuteInt64();

    private static SyncLogEntry MapSyncLog(SqliteDataReader reader)
    {
        var accountId = Db.IntOrNull(reader, 2);
        return new SyncLogEntry
        {
            Id = reader.GetInt64(0),
            TimestampUtc = FromUnixMs(Db.Int(reader, 1)),
            AccountId = accountId is { } value ? new AccountId(value) : AccountId.None,
            Level = Db.Str(reader, 3) ?? "info",
            Event = Db.Str(reader, 4) ?? string.Empty,
            Detail = Db.Str(reader, 5),
            Interface = Db.Str(reader, 6),
            AgentHost = Db.Str(reader, 7),
        };
    }
}
