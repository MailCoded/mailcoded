using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const string SelectFolder =
        "SELECT id, account_id, name, role, uidvalidity, uidnext, highestmodseq, delta_token, unread_count, total_count FROM folders";

    /// <summary>
    /// Reconciles a provider LIST into the folders table. Denormalized counters are never taken
    /// from the server: they describe what is locally stored, and only the writer maintains them.
    /// </summary>
    public Task<IReadOnlyList<FolderId>> UpsertFoldersAsync(
        AccountId accountId,
        IReadOnlyList<RemoteFolder> folders,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folders);

        return WriteAsync(context =>
        {
            var ids = new List<FolderId>(folders.Count);
            var select = context.Session.Prepare(
                "SELECT id FROM folders WHERE account_id = $account AND name = $name", "$account", "$name");
            var update = context.Session.Prepare(
                "UPDATE folders SET role = COALESCE($role, role) WHERE id = $id", "$role", "$id");
            var insert = context.Session.Prepare(
                "INSERT INTO folders (account_id, name, role) VALUES ($account,$name,$role) RETURNING id",
                "$account", "$name", "$role");

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();

                var existing = select
                    .SetInt(0, accountId.Value)
                    .SetText(1, folder.Path.Value)
                    .ExecuteNullableInt64();

                if (existing is { } id)
                {
                    update.SetText(0, folder.Role.ToWireValue()).SetInt(1, id).Execute();
                    ids.Add(new FolderId(id));
                    continue;
                }

                var inserted = insert
                    .SetInt(0, accountId.Value)
                    .SetText(1, folder.Path.Value)
                    .SetText(2, folder.Role.ToWireValue())
                    .ExecuteInt64();
                ids.Add(new FolderId(inserted));
            }

            return (IReadOnlyList<FolderId>)ids;
        }, ct);
    }

    public IReadOnlyList<FolderSummary> ListFolders(AccountId accountId, CancellationToken ct = default) =>
        Read(session =>
        {
            var folders = new List<FolderSummary>();
            using var reader = session
                .Prepare(SelectFolder + " WHERE account_id = $account ORDER BY name", "$account")
                .SetInt(0, accountId.Value)
                .ExecuteReader();

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (MapFolder(reader) is { } folder) folders.Add(folder);
            }
            return (IReadOnlyList<FolderSummary>)folders;
        }, ct);

    public FolderSummary? GetFolder(FolderId id, CancellationToken ct = default) =>
        Read<FolderSummary?>(session =>
        {
            using var reader = session
                .Prepare(SelectFolder + " WHERE id = $id", "$id")
                .SetInt(0, id.Value)
                .ExecuteReader();
            return reader.Read() ? MapFolder(reader) : null;
        }, ct);

    public FolderSummary? FindFolder(AccountId accountId, FolderPath path, CancellationToken ct = default) =>
        Read<FolderSummary?>(session =>
        {
            using var reader = session
                .Prepare(SelectFolder + " WHERE account_id = $account AND name = $name", "$account", "$name")
                .SetInt(0, accountId.Value)
                .SetText(1, path.Value)
                .ExecuteReader();
            return reader.Read() ? MapFolder(reader) : null;
        }, ct);

    /// <summary>
    /// Loads what <see cref="SyncPlanner"/> needs. <paramref name="includeKnownUids"/> is only
    /// worth paying for on the FullDiff path — it materializes every UID in the folder.
    /// </summary>
    public FolderState? LoadFolderState(FolderId id, bool includeKnownUids, CancellationToken ct = default) =>
        Read<FolderState?>(session =>
        {
            FolderState state;

            using (var reader = session.Prepare(
                       "SELECT f.id, f.account_id, f.name, f.uidvalidity, f.highestmodseq, f.total_count, "
                       + "s.backfill_cursor, s.accepts_custom_keywords "
                       + "FROM folders f LEFT JOIN folder_sync_state s ON s.folder_id = f.id WHERE f.id = $id",
                       "$id")
                   .SetInt(0, id.Value)
                   .ExecuteReader())
            {
                if (!reader.Read()) return null;
                if (!FolderPath.TryCreate(reader.GetString(2), '/', out var path)) return null;

                var backfill = Db.IntOrNull(reader, 6);
                state = new FolderState
                {
                    FolderId = new FolderId(reader.GetInt64(0)),
                    AccountId = new AccountId(reader.GetInt64(1)),
                    Path = path,
                    UidValidity = new UidValidity((uint)Db.Int(reader, 3)),
                    HighestModSeq = new ModSeq((ulong)Db.Int(reader, 4)),
                    KnownMessageCount = (int)Db.Int(reader, 5),
                    BackfillCursor = backfill is { } cursor && Uid.TryCreate(cursor, out var cursorUid) ? (Uid?)cursorUid : null,
                    ServerAcceptsCustomKeywords = reader.IsDBNull(7) || reader.GetInt64(7) != 0,
                };
            }

            var highest = session
                .Prepare("SELECT MAX(uid) FROM messages WHERE folder_id = $id", "$id")
                .SetInt(0, id.Value)
                .ExecuteNullableInt64();

            if (highest is { } highestUid && Uid.TryCreate(highestUid, out var known))
                state = state with { HighestKnownUid = known };

            if (!includeKnownUids) return state;

            var uids = new List<Uid>(state.KnownMessageCount);
            using (var reader = session
                       .Prepare("SELECT uid FROM messages WHERE folder_id = $id AND uid IS NOT NULL ORDER BY uid", "$id")
                       .SetInt(0, id.Value)
                       .ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (Uid.TryCreate(reader.GetInt64(0), out var uid)) uids.Add(uid);
                }
            }

            return state with { KnownUids = uids };
        }, ct);

    public Task SaveFolderStateAsync(FolderState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        return WriteAsync(context => SaveFolderStateCore(context, state), ct);
    }

    /// <summary>Records what the server said when the folder was opened, without touching counters.</summary>
    public Task SaveServerFolderInfoAsync(FolderId id, ServerFolderInfo info, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);

        return WriteAsync(context =>
        {
            context.Session
                .Prepare(
                    "UPDATE folders SET uidvalidity = $validity, uidnext = $uidnext, highestmodseq = $modseq, "
                    + "role = COALESCE($role, role) WHERE id = $id",
                    "$validity", "$uidnext", "$modseq", "$role", "$id")
                .SetInt(0, info.UidValidity.Value)
                .SetIntOrNull(1, info.UidNext is { } next ? (long?)next.Value : null)
                .SetInt(2, (long)info.HighestModSeq.Value)
                .SetText(3, info.Role.ToWireValue())
                .SetInt(4, id.Value)
                .Execute();

            SavePermanentFlagsObservation(context, id.Value, info.PermanentFlagsAllowCustomKeywords);
        }, ct);
    }

    public Task SetDeltaTokenAsync(FolderId id, string? deltaToken, CancellationToken ct) =>
        WriteAsync(context => context.Session
            .Prepare("UPDATE folders SET delta_token = $token WHERE id = $id", "$token", "$id")
            .SetText(0, deltaToken)
            .SetInt(1, id.Value)
            .Execute(), ct);

    /// <summary>Rebuilds the denormalized counters from the message rows. Repair path, not a hot path.</summary>
    public Task RecountFolderAsync(FolderId id, CancellationToken ct) =>
        WriteAsync(context => context.Session
            .Prepare(
                "UPDATE folders SET total_count = (SELECT COUNT(*) FROM messages WHERE folder_id = folders.id), "
                + "unread_count = (SELECT COUNT(*) FROM messages WHERE folder_id = folders.id AND (flags & 1) = 1) "
                + "WHERE id = $id",
                "$id")
            .SetInt(0, id.Value)
            .Execute(), ct);

    public Task RecountAllFoldersAsync(CancellationToken ct) =>
        WriteAsync(context => context.Session.Exec(
            "UPDATE folders SET total_count = (SELECT COUNT(*) FROM messages WHERE folder_id = folders.id), "
            + "unread_count = (SELECT COUNT(*) FROM messages WHERE folder_id = folders.id AND (flags & 1) = 1)"), ct);

    /// <summary>
    /// Drops a folder the server no longer lists (edge case 20), taking its messages with it.
    /// Store-level reconciliation only — no delete verb is exposed to the CLI or MCP surface.
    /// </summary>
    public Task RemoveFolderAsync(FolderId id, CancellationToken ct) =>
        WriteAsync(context =>
        {
            RemoveFolderFtsRows(context.Session, id.Value);
            context.Session
                .Prepare("DELETE FROM folders WHERE id = $id", "$id")
                .SetInt(0, id.Value)
                .Execute();
        }, ct);

    private static bool SaveFolderStateCore(WriteContext context, FolderState state)
    {
        context.Session
            .Prepare(
                "UPDATE folders SET uidvalidity = $validity, highestmodseq = $modseq WHERE id = $id",
                "$validity", "$modseq", "$id")
            .SetInt(0, state.UidValidity.Value)
            .SetInt(1, (long)state.HighestModSeq.Value)
            .SetInt(2, state.FolderId.Value)
            .Execute();

        UpsertFolderSyncState(
            context,
            state.FolderId.Value,
            state.BackfillCursor is { } cursor ? (long?)cursor.Value : null,
            state.ServerAcceptsCustomKeywords);
        return true;
    }

    private static void UpsertFolderSyncState(WriteContext context, long folderId, long? backfillCursor, bool acceptsCustomKeywords)
    {
        context.Session
            .Prepare(
                "INSERT INTO folder_sync_state (folder_id, backfill_cursor, accepts_custom_keywords, last_sync_utc) "
                + "VALUES ($id,$cursor,$keywords,$ts) "
                + "ON CONFLICT(folder_id) DO UPDATE SET backfill_cursor = excluded.backfill_cursor, "
                + "accepts_custom_keywords = excluded.accepts_custom_keywords, last_sync_utc = excluded.last_sync_utc",
                "$id", "$cursor", "$keywords", "$ts")
            .SetInt(0, folderId)
            .SetIntOrNull(1, backfillCursor)
            .SetBool(2, acceptsCustomKeywords)
            .SetInt(3, ToUnixMs(context.Clock.UtcNow))
            .Execute();
    }

    private static void SavePermanentFlagsObservation(WriteContext context, long folderId, bool acceptsCustomKeywords)
    {
        context.Session
            .Prepare(
                "INSERT INTO folder_sync_state (folder_id, accepts_custom_keywords, last_sync_utc) VALUES ($id,$keywords,$ts) "
                + "ON CONFLICT(folder_id) DO UPDATE SET accepts_custom_keywords = excluded.accepts_custom_keywords, "
                + "last_sync_utc = excluded.last_sync_utc",
                "$id", "$keywords", "$ts")
            .SetInt(0, folderId)
            .SetBool(1, acceptsCustomKeywords)
            .SetInt(2, ToUnixMs(context.Clock.UtcNow))
            .Execute();
    }

    private static FolderSummary? MapFolder(SqliteDataReader reader)
    {
        if (!FolderPath.TryCreate(reader.GetString(2), '/', out var path)) return null;

        var uidNext = Db.IntOrNull(reader, 5);
        return new FolderSummary
        {
            Id = new FolderId(reader.GetInt64(0)),
            AccountId = new AccountId(reader.GetInt64(1)),
            Path = path,
            Role = FolderRoleExtensions.FromWireValue(Db.Str(reader, 3)),
            UidValidity = new UidValidity((uint)Db.Int(reader, 4)),
            UidNext = uidNext is { } next && Uid.TryCreate(next, out var uid) ? (Uid?)uid : null,
            HighestModSeq = new ModSeq((ulong)Db.Int(reader, 6)),
            DeltaToken = Db.Str(reader, 7),
            UnreadCount = (int)Db.Int(reader, 8),
            TotalCount = (int)Db.Int(reader, 9),
        };
    }
}
