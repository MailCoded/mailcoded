using System.Globalization;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const string SelectEnvelope =
        "SELECT id, account_id, folder_id, uid, message_id, thread_key, date_utc, from_addr, to_addrs, cc_addrs, "
        + "subject, flags, modseq, size, has_attachments, blob_id, body_fetched FROM messages";

    private const string ExistingMessageColumns =
        "id, flags, subject, from_addr, to_addrs, body_fetched, thread_key, message_id";

    private const string SelectExistingMessage =
        "SELECT " + ExistingMessageColumns + " FROM messages WHERE folder_id = $folder AND uid = $uid";

    /// <summary>A row a local move left in this folder without a server UID, keyed by Message-ID:
    /// the server's copy of the same message must adopt it instead of inserting a duplicate.</summary>
    private const string SelectMovedInMessage =
        "SELECT " + ExistingMessageColumns + " FROM messages "
        + "WHERE folder_id = $folder AND uid IS NULL AND message_id = $msgid ORDER BY id LIMIT 1";

    private const string InsertMessage =
        "INSERT INTO messages (account_id, folder_id, uid, message_id, thread_key, date_utc, from_addr, to_addrs, "
        + "cc_addrs, subject, flags, modseq, size, has_attachments, body_fetched) "
        + "VALUES ($account,$folder,$uid,$msgid,$thread,$date,$from,$to,$cc,$subject,$flags,$modseq,$size,$hasatt,0) "
        + "RETURNING id";

    private const string UpdateMessage =
        "UPDATE messages SET message_id = $msgid, thread_key = COALESCE(thread_key, $thread), date_utc = $date, "
        + "from_addr = $from, to_addrs = $to, cc_addrs = $cc, subject = $subject, flags = $flags, "
        + "modseq = MAX(COALESCE(modseq, 0), $modseq), size = $size, has_attachments = $hasatt WHERE id = $id";

    private const string SelectBodyForFts =
        "SELECT COALESCE(text, '') FROM body_text WHERE message_id = $id";

    /// <summary>
    /// Persists one provider batch — new envelopes, flag changes, expunges and the next folder
    /// state — in a single transaction, so replaying the batch after a crash is a no-op (SPEC §5.5).
    /// </summary>
    public Task<SyncCounts> ApplySyncBatchAsync(SyncBatch batch, IThreader? threader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        return WriteAsync(context =>
        {
            var accountId = RequireAccountForFolder(context.Session, batch.FolderId.Value);
            var adoptUnlinked = HasUnlinkedRows(context.Session, batch.FolderId.Value);
            var added = 0;
            var updated = 0;
            var expunged = 0;

            foreach (var envelope in batch.Added)
            {
                ct.ThrowIfCancellationRequested();
                if (UpsertEnvelope(context, accountId, batch.FolderId.Value, envelope, threader, adoptUnlinked: adoptUnlinked)) added++;
                else updated++;
            }

            foreach (var change in batch.FlagChanges)
            {
                ct.ThrowIfCancellationRequested();
                if (ApplyFlagCore(context, batch.FolderId.Value, change)) updated++;
            }

            foreach (var uid in batch.Expunged)
            {
                ct.ThrowIfCancellationRequested();
                if (ExpungeCore(context, batch.FolderId.Value, uid)) expunged++;
            }

            if (batch.ServerInfo is { } info)
            {
                context.Session
                    .Prepare(
                        "UPDATE folders SET uidvalidity = $validity, uidnext = $uidnext, highestmodseq = $modseq WHERE id = $id",
                        "$validity", "$uidnext", "$modseq", "$id")
                    .SetInt(0, info.UidValidity.Value)
                    .SetIntOrNull(1, info.UidNext is { } next ? (long?)next.Value : null)
                    .SetInt(2, (long)info.HighestModSeq.Value)
                    .SetInt(3, batch.FolderId.Value)
                    .Execute();
            }

            if (batch.NextState is { } state) SaveFolderStateCore(context, state);

            return new SyncCounts(added, updated, expunged);
        }, ct);
    }

    /// <summary>Inserts or refreshes envelopes for one folder. Idempotent on (folder_id, uid).</summary>
    public Task<IngestResult> IngestEnvelopesAsync(
        FolderId folderId,
        IReadOnlyList<RemoteEnvelope> envelopes,
        IThreader? threader,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        return WriteAsync(context =>
        {
            var accountId = RequireAccountForFolder(context.Session, folderId.Value);
            var adoptUnlinked = HasUnlinkedRows(context.Session, folderId.Value);
            var inserted = 0;
            var updated = 0;

            foreach (var envelope in envelopes)
            {
                ct.ThrowIfCancellationRequested();
                if (UpsertEnvelope(context, accountId, folderId.Value, envelope, threader, adoptUnlinked: adoptUnlinked)) inserted++;
                else updated++;
            }

            return new IngestResult(inserted, updated);
        }, ct);
    }

    /// <summary>Server wins on flags (invariant 9): the stored bitfield is replaced, not merged.</summary>
    public Task<int> ApplyFlagChangesAsync(FolderId folderId, IReadOnlyList<FlagUpdate> changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return WriteAsync(context =>
        {
            var applied = 0;
            foreach (var change in changes)
            {
                ct.ThrowIfCancellationRequested();
                if (ApplyFlagCore(context, folderId.Value, change)) applied++;
            }
            return applied;
        }, ct);
    }

    public Task<int> ExpungeAsync(FolderId folderId, IReadOnlyList<Uid> uids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uids);

        return WriteAsync(context =>
        {
            var removed = 0;
            foreach (var uid in uids)
            {
                ct.ThrowIfCancellationRequested();
                if (ExpungeCore(context, folderId.Value, uid)) removed++;
            }
            return removed;
        }, ct);
    }

    /// <summary>
    /// Drops every UID in a folder after a UIDVALIDITY flip (edge case 1). Blobs are untouched so
    /// the re-fetch re-links them by sha256 instead of storing a second copy.
    /// </summary>
    public Task<int> InvalidateFolderUidsAsync(FolderId folderId, UidValidity newValidity, CancellationToken ct) =>
        WriteAsync(context =>
        {
            RemoveFolderFtsRows(context.Session, folderId.Value);

            var removed = context.Session
                .Prepare("DELETE FROM messages WHERE folder_id = $folder", "$folder")
                .SetInt(0, folderId.Value)
                .Execute();

            context.Session
                .Prepare(
                    "UPDATE folders SET uidvalidity = $validity, highestmodseq = 0, unread_count = 0, total_count = 0 WHERE id = $id",
                    "$validity", "$id")
                .SetInt(0, newValidity.Value)
                .SetInt(1, folderId.Value)
                .Execute();

            context.Session
                .Prepare("UPDATE folder_sync_state SET backfill_cursor = NULL WHERE folder_id = $id", "$id")
                .SetInt(0, folderId.Value)
                .Execute();

            return removed;
        }, ct);

    public Task MoveMessageAsync(LocalMessageId id, FolderId toFolderId, Uid? newUid, CancellationToken ct) =>
        WriteAsync(context =>
        {
            long fromFolder;
            long flags;
            using (var reader = context.Session
                       .Prepare("SELECT folder_id, flags, account_id FROM messages WHERE id = $id", "$id")
                       .SetInt(0, id.Value)
                       .ExecuteReader())
            {
                if (!reader.Read())
                    throw new StoreException(FailureCategory.NotFound, $"No message with id {id.Value}.");
                fromFolder = reader.GetInt64(0);
                flags = reader.GetInt64(1);
            }

            var targetAccount = RequireAccountForFolder(context.Session, toFolderId.Value);

            context.Session
                .Prepare(
                    "UPDATE messages SET folder_id = $folder, uid = $uid, account_id = $account WHERE id = $id",
                    "$folder", "$uid", "$account", "$id")
                .SetInt(0, toFolderId.Value)
                .SetIntOrNull(1, newUid is { } uid ? (long?)uid.Value : null)
                .SetInt(2, targetAccount)
                .SetInt(3, id.Value)
                .Execute();

            var unread = (flags & 1) != 0 ? 1 : 0;
            if (fromFolder != toFolderId.Value)
            {
                context.AddFolderDelta(fromFolder, -1, -unread);
                context.AddFolderDelta(toFolderId.Value, 1, unread);
            }
        }, ct);

    // ---- reads ------------------------------------------------------------

    public EnvelopeRow? GetEnvelope(LocalMessageId id, CancellationToken ct = default) =>
        Read<EnvelopeRow?>(session =>
        {
            using var reader = session
                .Prepare(SelectEnvelope + " WHERE id = $id", "$id")
                .SetInt(0, id.Value)
                .ExecuteReader();
            return reader.Read() ? MapEnvelope(reader) : null;
        }, ct);

    public LocalMessageId? FindMessage(FolderId folderId, Uid uid, CancellationToken ct = default) =>
        Read<LocalMessageId?>(session =>
        {
            var id = session
                .Prepare("SELECT id FROM messages WHERE folder_id = $folder AND uid = $uid", "$folder", "$uid")
                .SetInt(0, folderId.Value)
                .SetInt(1, uid.Value)
                .ExecuteNullableInt64();
            return id is { } value ? new LocalMessageId(value) : null;
        }, ct);

    /// <summary>All copies of one Message-ID, which is how a Sent duplicate is detected (edge case 26).</summary>
    public IReadOnlyList<EnvelopeRow> FindByMessageId(MessageId messageId, CancellationToken ct = default) =>
        Read(session =>
        {
            var rows = new List<EnvelopeRow>();
            using var reader = session
                .Prepare(SelectEnvelope + " WHERE message_id = $msgid", "$msgid")
                .SetText(0, messageId.Value)
                .ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(MapEnvelope(reader));
            }
            return (IReadOnlyList<EnvelopeRow>)rows;
        }, ct);

    /// <summary>Keyset-paged envelope listing. Index-only through <c>ix_msg_folder_date</c>.</summary>
    public StorePage<EnvelopeSummary> ListEnvelopes(
        FolderId folderId,
        string? cursor = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var pageSize = NormalizeLimit(limit);
        var seeking = Cursors.TryDecodeKeyset(cursor, out var cursorDate, out var cursorId);

        return Read(session =>
        {
            var statement = seeking
                ? session.Prepare(
                        "SELECT id, subject, from_addr, date_utc, flags FROM messages "
                        + "WHERE folder_id = $folder AND (date_utc, id) < ($date, $cursorId) "
                        + "ORDER BY date_utc DESC, id DESC LIMIT $limit",
                        "$folder", "$date", "$cursorId", "$limit")
                    .SetInt(0, folderId.Value)
                    .SetInt(1, cursorDate)
                    .SetInt(2, cursorId)
                    .SetInt(3, pageSize + 1)
                : session.Prepare(
                        "SELECT id, subject, from_addr, date_utc, flags FROM messages WHERE folder_id = $folder "
                        + "ORDER BY date_utc DESC, id DESC LIMIT $limit",
                        "$folder", "$limit")
                    .SetInt(0, folderId.Value)
                    .SetInt(1, pageSize + 1);

            var items = new List<EnvelopeSummary>(pageSize);
            var lastDate = 0L;
            var lastId = 0L;
            var more = false;

            using (var reader = statement.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (items.Count == pageSize) { more = true; break; }

                    lastDate = Db.Int(reader, 3);
                    lastId = reader.GetInt64(0);
                    items.Add(new EnvelopeSummary
                    {
                        Id = new LocalMessageId(lastId),
                        Subject = Db.Str(reader, 1),
                        From = Db.Str(reader, 2),
                        DateUtc = FromUnixMs(lastDate),
                        Flags = (MessageFlags)(int)Db.Int(reader, 4),
                    });
                }
            }

            return new StorePage<EnvelopeSummary>
            {
                Items = items,
                NextCursor = more ? Cursors.EncodeKeyset(lastDate, lastId) : null,
                Truncated = more,
            };
        }, ct);
    }

    /// <summary>Latest message per conversation, via the ROW_NUMBER shape in PERFORMANCE §15.4.</summary>
    public StorePage<ThreadSummary> ListThreads(
        FolderId folderId,
        string? cursor = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var pageSize = NormalizeLimit(limit);
        var seeking = Cursors.TryDecodeKeyset(cursor, out var cursorDate, out var cursorId);

        // COALESCE keeps a NULL thread_key from collapsing unrelated messages into one partition.
        const string inner =
            "SELECT id, thread_key, subject, from_addr, date_utc, flags, "
            + "ROW_NUMBER() OVER (PARTITION BY COALESCE(thread_key, 'id:' || id) ORDER BY date_utc DESC, id DESC) AS rn "
            + "FROM messages WHERE folder_id = $folder";

        return Read(session =>
        {
            var statement = seeking
                ? session.Prepare(
                        "SELECT id, thread_key, subject, from_addr, date_utc, flags FROM (" + inner + ") "
                        + "WHERE rn = 1 AND (date_utc, id) < ($date, $cursorId) ORDER BY date_utc DESC, id DESC LIMIT $limit",
                        "$folder", "$date", "$cursorId", "$limit")
                    .SetInt(0, folderId.Value)
                    .SetInt(1, cursorDate)
                    .SetInt(2, cursorId)
                    .SetInt(3, pageSize + 1)
                : session.Prepare(
                        "SELECT id, thread_key, subject, from_addr, date_utc, flags FROM (" + inner + ") "
                        + "WHERE rn = 1 ORDER BY date_utc DESC, id DESC LIMIT $limit",
                        "$folder", "$limit")
                    .SetInt(0, folderId.Value)
                    .SetInt(1, pageSize + 1);

            var items = new List<ThreadSummary>(pageSize);
            var lastDate = 0L;
            var lastId = 0L;
            var more = false;

            using (var reader = statement.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (items.Count == pageSize) { more = true; break; }

                    lastDate = Db.Int(reader, 4);
                    lastId = reader.GetInt64(0);
                    var threadValue = Db.Str(reader, 1);
                    items.Add(new ThreadSummary
                    {
                        Id = new LocalMessageId(lastId),
                        ThreadKey = threadValue is not null && ThreadKey.TryCreate(threadValue, out var key) ? (ThreadKey?)key : null,
                        Subject = Db.Str(reader, 2),
                        From = Db.Str(reader, 3),
                        DateUtc = FromUnixMs(lastDate),
                        Flags = (MessageFlags)(int)Db.Int(reader, 5),
                    });
                }
            }

            return new StorePage<ThreadSummary>
            {
                Items = items,
                NextCursor = more ? Cursors.EncodeKeyset(lastDate, lastId) : null,
                Truncated = more,
            };
        }, ct);
    }

    /// <summary>Every message in one conversation, oldest first.</summary>
    public IReadOnlyList<EnvelopeRow> ListThreadMessages(ThreadKey threadKey, int limit = 500, CancellationToken ct = default)
    {
        var pageSize = NormalizeLimit(limit, 2000);

        return Read(session =>
        {
            var rows = new List<EnvelopeRow>();
            using var reader = session
                .Prepare(SelectEnvelope + " WHERE thread_key = $key ORDER BY date_utc ASC, id ASC LIMIT $limit", "$key", "$limit")
                .SetText(0, threadKey.Value)
                .SetInt(1, pageSize)
                .ExecuteReader();

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(MapEnvelope(reader));
            }
            return (IReadOnlyList<EnvelopeRow>)rows;
        }, ct);
    }

    public ThreadKey? LookupThreadKey(MessageId messageId, CancellationToken ct = default) =>
        Read<ThreadKey?>(session =>
        {
            var value = session
                .Prepare(
                    "SELECT thread_key FROM messages WHERE message_id = $msgid AND thread_key IS NOT NULL LIMIT 1",
                    "$msgid")
                .SetText(0, messageId.Value)
                .ExecuteString();
            return value is not null && ThreadKey.TryCreate(value, out var key) ? (ThreadKey?)key : null;
        }, ct);

    // ---- shared write primitives -----------------------------------------

    private static long RequireAccountForFolder(DbSession session, long folderId)
    {
        var accountId = session
            .Prepare("SELECT account_id FROM folders WHERE id = $id", "$id")
            .SetInt(0, folderId)
            .ExecuteNullableInt64();

        return accountId ?? throw new StoreException(FailureCategory.NotFound, $"No folder with id {folderId}.");
    }

    /// <summary>Returns true when the envelope was newly inserted, false when an existing row was refreshed.
    /// <paramref name="adoptUnlinked"/> is the caller's batch-level answer to "could a local move have left a
    /// uid-NULL row here?" — the bulk backfill window never fetches the high UIDs a move produces.</summary>
    private static bool UpsertEnvelope(
        WriteContext context,
        long accountId,
        long folderId,
        RemoteEnvelope envelope,
        IThreader? threader,
        bool deferFts = false,
        bool adoptUnlinked = false)
    {
        var session = context.Session;
        var flags = (long)(int)envelope.Flags;
        var unread = (flags & 1) != 0 ? 1 : 0;
        var subject = envelope.Subject;
        var from = envelope.From;
        var to = envelope.To;

        long existingId = 0;
        var found = false;
        var adopted = false;
        long oldFlags = 0;
        string? oldSubject = null;
        string? oldFrom = null;
        string? oldTo = null;
        string? oldThreadKey = null;
        string? oldMessageId = null;

        using (var reader = session.Prepare(SelectExistingMessage, "$folder", "$uid")
                   .SetInt(0, folderId)
                   .SetInt(1, envelope.Uid.Value)
                   .ExecuteReader())
        {
            if (reader.Read())
            {
                found = true;
                existingId = reader.GetInt64(0);
                oldFlags = Db.Int(reader, 1);
                oldSubject = Db.Str(reader, 2);
                oldFrom = Db.Str(reader, 3);
                oldTo = Db.Str(reader, 4);
                oldThreadKey = Db.Str(reader, 6);
                oldMessageId = Db.Str(reader, 7);
            }
        }

        if (!found && adoptUnlinked && envelope.MessageIdHeader is { } incoming)
        {
            using var reader = session.Prepare(SelectMovedInMessage, "$folder", "$msgid")
                .SetInt(0, folderId)
                .SetText(1, incoming)
                .ExecuteReader();

            if (reader.Read())
            {
                found = true;
                adopted = true;
                existingId = reader.GetInt64(0);
                oldFlags = Db.Int(reader, 1);
                oldSubject = Db.Str(reader, 2);
                oldFrom = Db.Str(reader, 3);
                oldTo = Db.Str(reader, 4);
                oldThreadKey = Db.Str(reader, 6);
                oldMessageId = Db.Str(reader, 7);
            }
        }

        // Edge case 3: the same UID now holds a different message, so nothing the old one owned —
        // thread key, body, blob, FTS rows — describes this row any more.
        var reusedUid = found
            && !adopted
            && oldMessageId is not null
            && envelope.MessageIdHeader is not null
            && !string.Equals(oldMessageId, envelope.MessageIdHeader, StringComparison.Ordinal);

        // A settled thread key is never recomputed: the resolve costs a lookup per envelope and
        // the UPDATE below would COALESCE it away anyway.
        var threadKey = reusedUid || oldThreadKey is null
            ? ResolveThreadKey(session, threader, envelope, folderId)
            : oldThreadKey;

        if (found)
        {
            var ftsWritten = !(deferFts && IsFtsDeferred(session, existingId));

            if (reusedUid)
            {
                InvalidateReusedUid(
                    session, accountId, folderId, existingId, envelope.Uid,
                    oldSubject, oldFrom, oldTo, ftsWritten, context.Clock.UtcNow);
            }

            session.Prepare(UpdateMessage,
                    "$msgid", "$thread", "$date", "$from", "$to", "$cc", "$subject", "$flags", "$modseq", "$size", "$hasatt", "$id")
                .SetText(0, envelope.MessageIdHeader)
                .SetText(1, threadKey)
                .SetInt(2, ToUnixMs(envelope.DateUtc))
                .SetText(3, from)
                .SetText(4, to)
                .SetText(5, envelope.Cc)
                .SetText(6, subject)
                .SetInt(7, flags)
                .SetInt(8, (long)envelope.ModSeq.Value)
                .SetInt(9, envelope.Size)
                .SetBool(10, envelope.HasAttachments)
                .SetInt(11, existingId)
                .Execute();

            if (adopted)
            {
                session
                    .Prepare("UPDATE messages SET uid = $uid WHERE id = $id", "$uid", "$id")
                    .SetInt(0, envelope.Uid.Value)
                    .SetInt(1, existingId)
                    .Execute();
            }

            var oldUnread = (oldFlags & 1) != 0 ? 1 : 0;
            context.AddFolderDelta(folderId, 0, unread - oldUnread);

            // Only pay for an FTS rewrite when an indexed column actually moved, and never for a
            // row whose FTS entry this bulk window has not written yet.
            var indexedColumnsMoved =
                !string.Equals(oldSubject, subject, StringComparison.Ordinal)
                || !string.Equals(oldFrom, from, StringComparison.Ordinal)
                || !string.Equals(oldTo, to, StringComparison.Ordinal);

            if (reusedUid)
            {
                if (ftsWritten) Fts.Insert(session, existingId, subject, string.Empty, from, to);
            }
            else if (indexedColumnsMoved && ftsWritten)
            {
                var body = ReadBodyTextForFts(session, existingId);
                Fts.Replace(session, existingId, oldSubject, body, oldFrom, oldTo, subject, body, from, to);
            }

            return false;
        }

        var insertedId = session.Prepare(InsertMessage,
                "$account", "$folder", "$uid", "$msgid", "$thread", "$date", "$from", "$to", "$cc", "$subject",
                "$flags", "$modseq", "$size", "$hasatt")
            .SetInt(0, accountId)
            .SetInt(1, folderId)
            .SetInt(2, envelope.Uid.Value)
            .SetText(3, envelope.MessageIdHeader)
            .SetText(4, threadKey)
            .SetInt(5, ToUnixMs(envelope.DateUtc))
            .SetText(6, from)
            .SetText(7, to)
            .SetText(8, envelope.Cc)
            .SetText(9, subject)
            .SetInt(10, flags)
            .SetInt(11, (long)envelope.ModSeq.Value)
            .SetInt(12, envelope.Size)
            .SetBool(13, envelope.HasAttachments)
            .ExecuteInt64();

        if (deferFts) MarkFtsDeferred(session, insertedId);
        else Fts.Insert(session, insertedId, subject, string.Empty, from, to);

        context.AddFolderDelta(folderId, 1, unread);
        return true;
    }

    /// <summary>Whether this folder holds a row a local move left without a server UID. One probe per
    /// batch keeps the adoption lookup out of the per-envelope path when there is nothing to adopt.</summary>
    private static bool HasUnlinkedRows(DbSession session, long folderId) =>
        session
            .Prepare("SELECT 1 FROM messages WHERE folder_id = $folder AND uid IS NULL LIMIT 1", "$folder")
            .SetInt(0, folderId)
            .ExecuteNullableInt64() is not null;

    /// <summary>Edge case 3: whatever the previous occupant of this UID owned — body, blob, thread key,
    /// FTS text — describes a different message and must not be served under the new headers.</summary>
    private static void InvalidateReusedUid(
        DbSession session,
        long accountId,
        long folderId,
        long messageId,
        Uid uid,
        string? oldSubject,
        string? oldFrom,
        string? oldTo,
        bool ftsWritten,
        DateTimeOffset nowUtc)
    {
        if (ftsWritten)
            Fts.Delete(session, messageId, oldSubject, ReadBodyTextForFts(session, messageId), oldFrom, oldTo);

        session
            .Prepare("DELETE FROM body_text WHERE message_id = $id", "$id")
            .SetInt(0, messageId)
            .Execute();

        session
            .Prepare("UPDATE messages SET blob_id = NULL, body_fetched = 0, thread_key = NULL WHERE id = $id", "$id")
            .SetInt(0, messageId)
            .Execute();

        AppendSyncLogCore(
            session,
            new SyncLogEntry
            {
                Event = "uid_reused",
                Level = "warn",
                AccountId = new AccountId(accountId),
                Detail = string.Create(CultureInfo.InvariantCulture, $"folder={folderId} uid={uid.Value}"),
                Interface = "internal",
            },
            nowUtc);
    }

    private static bool ApplyFlagCore(WriteContext context, long folderId, FlagUpdate change)
    {
        var session = context.Session;
        long id;
        long oldFlags;

        using (var reader = session
                   .Prepare("SELECT id, flags FROM messages WHERE folder_id = $folder AND uid = $uid", "$folder", "$uid")
                   .SetInt(0, folderId)
                   .SetInt(1, change.Uid.Value)
                   .ExecuteReader())
        {
            if (!reader.Read()) return false;
            id = reader.GetInt64(0);
            oldFlags = Db.Int(reader, 1);
        }

        var newFlags = (long)(int)change.Flags;
        session
            .Prepare("UPDATE messages SET flags = $flags, modseq = MAX(COALESCE(modseq, 0), $modseq) WHERE id = $id",
                "$flags", "$modseq", "$id")
            .SetInt(0, newFlags)
            .SetInt(1, (long)change.ModSeq.Value)
            .SetInt(2, id)
            .Execute();

        context.AddFolderDelta(folderId, 0, ((newFlags & 1) != 0 ? 1 : 0) - ((oldFlags & 1) != 0 ? 1 : 0));
        return true;
    }

    private static bool ExpungeCore(WriteContext context, long folderId, Uid uid)
    {
        var session = context.Session;
        long id;
        long flags;
        string? subject;
        string? from;
        string? to;

        using (var reader = session.Prepare(SelectExistingMessage, "$folder", "$uid")
                   .SetInt(0, folderId)
                   .SetInt(1, uid.Value)
                   .ExecuteReader())
        {
            if (!reader.Read()) return false;
            id = reader.GetInt64(0);
            flags = Db.Int(reader, 1);
            subject = Db.Str(reader, 2);
            from = Db.Str(reader, 3);
            to = Db.Str(reader, 4);
        }

        Fts.Delete(session, id, subject, ReadBodyTextForFts(session, id), from, to);

        session.Prepare("DELETE FROM messages WHERE id = $id", "$id").SetInt(0, id).Execute();
        context.AddFolderDelta(folderId, -1, (flags & 1) != 0 ? -1 : 0);
        return true;
    }

    private static void MarkFtsDeferred(DbSession session, long messageId) =>
        session
            .Prepare("INSERT INTO bulk_pending (id) VALUES ($id) ON CONFLICT DO NOTHING", "$id")
            .SetInt(0, messageId)
            .Execute();

    private static bool IsFtsDeferred(DbSession session, long messageId) =>
        session
            .Prepare("SELECT 1 FROM bulk_pending WHERE id = $id", "$id")
            .SetInt(0, messageId)
            .ExecuteNullableInt64() is not null;

    private static string ReadBodyTextForFts(DbSession session, long messageId) =>
        session.Prepare(SelectBodyForFts, "$id").SetInt(0, messageId).ExecuteString() ?? string.Empty;

    private static void RemoveFolderFtsRows(DbSession session, long folderId)
    {
        using var reader = session
            .Prepare(
                "SELECT m.id, m.subject, m.from_addr, m.to_addrs, COALESCE(b.text, '') "
                + "FROM messages m LEFT JOIN body_text b ON b.message_id = m.id WHERE m.folder_id = $folder",
                "$folder")
            .SetInt(0, folderId)
            .ExecuteReader();

        while (reader.Read())
        {
            Fts.Delete(
                session,
                reader.GetInt64(0),
                Db.Str(reader, 1),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Db.Str(reader, 2),
                Db.Str(reader, 3));
        }
    }

    private static string? ResolveThreadKey(DbSession session, IThreader? threader, RemoteEnvelope envelope, long folderId)
    {
        if (threader is not null)
        {
            var candidate = new ThreadCandidate
            {
                MessageId = MessageId.TryParse(envelope.MessageIdHeader, out var parsed) ? (MessageId?)parsed : null,
                References = ParseMessageIds(envelope.References),
                InReplyTo = MessageId.TryParse(envelope.InReplyTo, out var inReplyTo) ? (MessageId?)inReplyTo : null,
                Subject = envelope.Subject,
                FromAddress = envelope.From,
                DateUtc = envelope.DateUtc,
                NativeThreadId = envelope.GmailThreadId?.ToString(CultureInfo.InvariantCulture),
            };

            return threader.Resolve(candidate, mid => LookupThreadKeyOn(session, mid)).Value;
        }

        if (envelope.GmailThreadId is { } gmailThreadId)
            return "gm:" + gmailThreadId.ToString(CultureInfo.InvariantCulture);

        if (MessageId.TryParse(envelope.MessageIdHeader, out var messageId))
            return "mid:" + messageId.Value;

        // 12: no usable Message-ID. A synthetic key keeps the row out of everyone else's thread.
        return string.Create(CultureInfo.InvariantCulture, $"syn:{folderId}:{envelope.Uid.Value}");
    }

    private static ThreadKey? LookupThreadKeyOn(DbSession session, MessageId messageId)
    {
        var value = session
            .Prepare(
                "SELECT thread_key FROM messages WHERE message_id = $msgid AND thread_key IS NOT NULL LIMIT 1",
                "$msgid")
            .SetText(0, messageId.Value)
            .ExecuteString();
        return value is not null && ThreadKey.TryCreate(value, out var key) ? (ThreadKey?)key : null;
    }

    private static IReadOnlyList<MessageId> ParseMessageIds(IReadOnlyList<string> raw)
    {
        if (raw.Count == 0) return [];
        var ids = new List<MessageId>(raw.Count);
        foreach (var value in raw)
            if (MessageId.TryParse(value, out var id))
                ids.Add(id);
        return ids;
    }

    private static int NormalizeLimit(int limit, int max = 500)
    {
        if (limit <= 0) return 50;
        return limit > max ? max : limit;
    }

    private static EnvelopeRow MapEnvelope(SqliteDataReader reader)
    {
        var uid = Db.IntOrNull(reader, 3);
        var messageIdValue = Db.Str(reader, 4);
        var threadValue = Db.Str(reader, 5);
        var blobId = Db.IntOrNull(reader, 15);

        return new EnvelopeRow
        {
            Id = new LocalMessageId(reader.GetInt64(0)),
            AccountId = new AccountId(reader.GetInt64(1)),
            FolderId = new FolderId(reader.GetInt64(2)),
            Uid = uid is { } uidValue && Uid.TryCreate(uidValue, out var parsedUid) ? (Uid?)parsedUid : null,
            MessageId = messageIdValue is not null && MessageId.TryParse(messageIdValue, out var parsedId) ? (MessageId?)parsedId : null,
            ThreadKey = threadValue is not null && ThreadKey.TryCreate(threadValue, out var parsedKey) ? (ThreadKey?)parsedKey : null,
            DateUtc = FromUnixMs(Db.Int(reader, 6)),
            From = Db.Str(reader, 7),
            To = Db.Str(reader, 8),
            Cc = Db.Str(reader, 9),
            Subject = Db.Str(reader, 10),
            Flags = (MessageFlags)(int)Db.Int(reader, 11),
            ModSeq = new ModSeq((ulong)Db.Int(reader, 12)),
            Size = Db.Int(reader, 13),
            HasAttachments = Db.Bool(reader, 14),
            BlobId = blobId is { } blob ? (BlobId?)new BlobId(blob) : null,
            BodyFetched = Db.Bool(reader, 16),
        };
    }
}
