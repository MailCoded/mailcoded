using Mailcoded.Core.Domain.Correspondents;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const string UpsertCorrespondent =
        "INSERT INTO correspondents (account_id, address, display_name, use_count, last_used_utc) "
        + "VALUES ($account,$address,$name,1,$used) "
        + "ON CONFLICT(account_id, address) DO UPDATE SET use_count = use_count + 1, "
        + "display_name = CASE WHEN excluded.display_name IS NOT NULL "
        + "AND (display_name IS NULL OR excluded.last_used_utc >= last_used_utc) THEN excluded.display_name ELSE display_name END, "
        + "last_used_utc = MAX(last_used_utc, excluded.last_used_utc)";

    private const int BackfillBatch = 1000;

    /// <summary>Everyone the Account's outgoing mail has addressed, unranked. Ranking is the caller's, because it needs a clock.</summary>
    public IReadOnlyList<Correspondent> ListCorrespondents(AccountId accountId, CancellationToken ct = default) =>
        Read(session =>
        {
            var rows = new List<Correspondent>();
            using var reader = session
                .Prepare(
                    "SELECT address, display_name, use_count, last_used_utc FROM correspondents WHERE account_id = $account",
                    "$account")
                .SetInt(0, accountId.Value)
                .ExecuteReader();

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(new Correspondent
                {
                    Address = reader.GetString(0),
                    DisplayName = Db.Str(reader, 1),
                    Count = (int)Math.Min(int.MaxValue, Db.Int(reader, 2)),
                    LastUsedUtc = FromUnixMs(Db.Int(reader, 3)),
                });
            }

            return (IReadOnlyList<Correspondent>)rows;
        }, ct);

    /// <summary>Counts one use of each address with no filtering; the filters live in <see cref="CorrespondentLearning"/> and run before this.</summary>
    internal Task RecordCorrespondentsAsync(
        AccountId accountId,
        IReadOnlyList<NamedAddress> addresses,
        DateTimeOffset usedUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        return WriteAsync(context =>
        {
            RecordCorrespondents(context.Session, accountId.Value, addresses, ToUnixMs(usedUtc));
        }, ct);
    }

    private static void RecordCorrespondents(DbSession session, long accountId, IReadOnlyList<NamedAddress> addresses, long usedMs)
    {
        foreach (var entry in addresses)
        {
            session
                .Prepare(UpsertCorrespondent, "$account", "$address", "$name", "$used")
                .SetInt(0, accountId)
                .SetText(1, entry.Key)
                .SetText(2, entry.DisplayName)
                .SetInt(3, usedMs)
                .Execute();
        }
    }

    private static IReadOnlySet<string> LoadOwnKeys(DbSession session, long accountId)
    {
        using var reader = session
            .Prepare(SelectAccount + " WHERE id = $id", "$id")
            .SetInt(0, accountId)
            .ExecuteReader();

        if (!reader.Read()) return new HashSet<string>(StringComparer.Ordinal);

        var account = MapAccount(reader);
        return CorrespondentLearning.OwnKeys(account.Email, account.Identities);
    }

    private static void LearnFromSentOutbox(DbSession session, long accountId, OutboxEnvelope envelope, DateTimeOffset nowUtc)
    {
        var to = new List<NamedAddress>(envelope.To.Count);
        foreach (var address in envelope.To) to.Add(new NamedAddress(address, null));

        var cc = new List<NamedAddress>(envelope.Cc.Count);
        foreach (var address in envelope.Cc) cc.Add(new NamedAddress(address, null));

        var chosen = CorrespondentLearning.Select(to, cc, LoadOwnKeys(session, accountId));
        if (chosen.Count > 0) RecordCorrespondents(session, accountId, chosen, ToUnixMs(nowUtc));
    }

    /// <summary>Runs once per database, before anything else can write: counting the Sent mail that predates the table.
    /// Sent outbox rows with no copy in Sent yet are counted too; a copy that arrives later is skipped because the outbox row owns it.</summary>
    private static void BackfillCorrespondents(DbSession session, IClock clock)
    {
        var done = session.ExecScalarInt64("SELECT COUNT(*) FROM correspondent_backfill");
        if (done > 0) return;

        var now = clock.UtcNow;
        var ownByAccount = new Dictionary<long, IReadOnlySet<string>>();

        session.BeginImmediate();
        try
        {
            long last = 0;
            while (true)
            {
                var batch = new List<(long Id, long Account, string? From, string? To, string? Cc, long Date)>(BackfillBatch);

                using (var reader = session
                    .Prepare(
                        "SELECT m.id, m.account_id, m.from_addr, m.to_addrs, m.cc_addrs, COALESCE(m.date_utc, 0) "
                        + "FROM messages m JOIN folders f ON f.id = m.folder_id "
                        + "WHERE f.role = 'sent' AND m.id > $last ORDER BY m.id LIMIT $batch",
                        "$last", "$batch")
                    .SetInt(0, last)
                    .SetInt(1, BackfillBatch)
                    .ExecuteReader())
                {
                    while (reader.Read())
                        batch.Add((reader.GetInt64(0), reader.GetInt64(1), Db.Str(reader, 2), Db.Str(reader, 3), Db.Str(reader, 4), reader.GetInt64(5)));
                }

                if (batch.Count == 0) break;

                foreach (var row in batch)
                {
                    last = row.Id;
                    if (!ownByAccount.TryGetValue(row.Account, out var own))
                        ownByAccount[row.Account] = own = LoadOwnKeys(session, row.Account);

                    var used = Math.Min(row.Date, ToUnixMs(now));
                    LearnFromHeaders(session, row.Account, own, row.From, row.To, row.Cc, used);
                }
            }

            var pending = new List<(long Account, string Envelope, long Used)>();
            using (var reader = session
                .Prepare(
                    "SELECT o.account_id, o.envelope_json, COALESCE(o.last_attempt_utc, o.created_utc) FROM outbox o "
                    + "WHERE o.state = 'sent' AND o.envelope_json IS NOT NULL AND NOT EXISTS ("
                    + "SELECT 1 FROM messages m JOIN folders f ON f.id = m.folder_id "
                    + "WHERE f.role = 'sent' AND m.account_id = o.account_id AND m.message_id = o.message_id)")
                .ExecuteReader())
            {
                while (reader.Read()) pending.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
            }

            foreach (var row in pending)
            {
                if (OutboxEnvelopeJson.Read(row.Envelope) is not { } envelope) continue;
                LearnFromSentOutbox(session, row.Account, envelope, FromUnixMs(Math.Min(row.Used, ToUnixMs(now))));
            }

            session
                .Prepare("INSERT INTO correspondent_backfill (id, completed_utc) VALUES (1, $at)", "$at")
                .SetInt(0, ToUnixMs(now))
                .Execute();

            session.Commit();
        }
        catch
        {
            session.Rollback();
            throw;
        }
    }

    private static void LearnFromHeaders(
        DbSession session,
        long accountId,
        IReadOnlySet<string> own,
        string? from,
        string? to,
        string? cc,
        long usedMs)
    {
        if (string.IsNullOrEmpty(to) && string.IsNullOrEmpty(cc)) return;
        if (!CorrespondentLearning.IsAuthoredBy(RecipientList.Parse(from), own)) return;

        var chosen = CorrespondentLearning.Select(RecipientList.Parse(to), RecipientList.Parse(cc), own);
        if (chosen.Count > 0) RecordCorrespondents(session, accountId, chosen, usedMs);
    }

    /// <summary>The per-batch state for learning from a Sent-role Folder: the Account's own addresses, read once.</summary>
    internal sealed class SentFolderLearner
    {
        private readonly long _accountId;
        private readonly IReadOnlySet<string> _own;
        private readonly long _nowMs;

        private SentFolderLearner(long accountId, IReadOnlySet<string> own, long nowMs)
        {
            _accountId = accountId;
            _own = own;
            _nowMs = nowMs;
        }

        public static SentFolderLearner? For(DbSession session, long accountId, long folderId, IClock clock)
        {
            var role = session
                .Prepare("SELECT role FROM folders WHERE id = $id", "$id")
                .SetInt(0, folderId)
                .ExecuteString();

            return role == "sent" ? new SentFolderLearner(accountId, LoadOwnKeys(session, accountId), ToUnixMs(clock.UtcNow)) : null;
        }

        /// <summary>A message mailcoded sent is counted when its outbox row is marked sent, so its copy in Sent is not counted again.</summary>
        public void Learn(DbSession session, RemoteEnvelope envelope)
        {
            if (string.IsNullOrEmpty(envelope.To) && string.IsNullOrEmpty(envelope.Cc)) return;

            if (envelope.MessageIdHeader is { } messageId
                && session
                    .Prepare("SELECT 1 FROM outbox WHERE account_id = $account AND message_id = $msgid LIMIT 1", "$account", "$msgid")
                    .SetInt(0, _accountId)
                    .SetText(1, messageId)
                    .ExecuteNullableInt64() is not null)
                return;

            LearnFromHeaders(session, _accountId, _own, envelope.From, envelope.To, envelope.Cc, Math.Min(ToUnixMs(envelope.DateUtc), _nowMs));
        }
    }
}
