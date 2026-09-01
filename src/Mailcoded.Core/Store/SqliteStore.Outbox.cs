using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const string OutboxColumns =
        "id, account_id, message_id, state, smtp_response, attempts, next_attempt_utc, created_utc, "
        + "permanently_failed, enhanced_status, last_attempt_utc, max_attempts, envelope_json";

    private const string SelectOutboxMeta = "SELECT " + OutboxColumns + " FROM outbox";

    private const string SelectOutboxFull = "SELECT " + OutboxColumns + ", raw FROM outbox";

    /// <summary>The pre-assigned Message-ID is the idempotency key: a second enqueue returns the first row.</summary>
    public Task<long> EnqueueOutboxAsync(OutboxRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Raw.Length == 0)
            throw new ArgumentException("An outbox row must carry the raw message bytes.", nameof(record));

        return WriteAsync(context =>
        {
            var existing = context.Session
                .Prepare("SELECT id FROM outbox WHERE message_id = $msgid", "$msgid")
                .SetText(0, record.MessageId.Value)
                .ExecuteNullableInt64();

            if (existing is { } found) return found;

            var created = record.CreatedUtc == default ? context.Clock.UtcNow : record.CreatedUtc;

            return context.Session
                .Prepare(
                    "INSERT INTO outbox (account_id, message_id, state, raw, smtp_response, attempts, next_attempt_utc, "
                    + "created_utc, permanently_failed, enhanced_status, last_attempt_utc, max_attempts, envelope_json) "
                    + "VALUES ($account,$msgid,$state,$raw,$response,$attempts,$next,$created,$permanent,$enhanced,$last,$max,$envelope) "
                    + "RETURNING id",
                    "$account", "$msgid", "$state", "$raw", "$response", "$attempts", "$next", "$created",
                    "$permanent", "$enhanced", "$last", "$max", "$envelope")
                .SetInt(0, record.AccountId.Value)
                .SetText(1, record.MessageId.Value)
                .SetText(2, record.State.ToWireValue())
                .SetBlob(3, record.Raw)
                .SetText(4, record.SmtpResponse)
                .SetInt(5, record.Attempts)
                .SetIntOrNull(6, record.NextAttemptUtc is { } next ? (long?)ToUnixMs(next) : null)
                .SetInt(7, ToUnixMs(created))
                .SetBool(8, record.PermanentlyFailed)
                .SetText(9, record.EnhancedStatusCode)
                .SetIntOrNull(10, record.LastAttemptUtc is { } last ? (long?)ToUnixMs(last) : null)
                .SetIntOrNull(11, record.MaxAttempts)
                .SetText(12, record.Envelope is { } envelope ? OutboxEnvelopeJson.Write(envelope) : null)
                .ExecuteInt64();
        }, ct);
    }

    /// <summary>Persists a state transition. The aggregate owns which transitions are legal.</summary>
    public Task SaveOutboxAsync(OutboxRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        return WriteAsync(context =>
        {
            // COALESCE so a record whose Envelope is null cannot erase the stored Bcc.
            var affected = context.Session
                .Prepare(
                    "UPDATE outbox SET state = $state, smtp_response = $response, attempts = $attempts, "
                    + "next_attempt_utc = $next, permanently_failed = $permanent, enhanced_status = $enhanced, "
                    + "last_attempt_utc = $last, max_attempts = $max, "
                    + "envelope_json = COALESCE($envelope, envelope_json) WHERE id = $id",
                    "$state", "$response", "$attempts", "$next", "$permanent", "$enhanced", "$last", "$max",
                    "$envelope", "$id")
                .SetText(0, record.State.ToWireValue())
                .SetText(1, record.SmtpResponse)
                .SetInt(2, record.Attempts)
                .SetIntOrNull(3, record.NextAttemptUtc is { } next ? (long?)ToUnixMs(next) : null)
                .SetBool(4, record.PermanentlyFailed)
                .SetText(5, record.EnhancedStatusCode)
                .SetIntOrNull(6, record.LastAttemptUtc is { } last ? (long?)ToUnixMs(last) : null)
                .SetIntOrNull(7, record.MaxAttempts)
                .SetText(8, record.Envelope is { } envelope ? OutboxEnvelopeJson.Write(envelope) : null)
                .SetInt(9, record.Id)
                .Execute();

            if (affected == 0)
                throw new StoreException(FailureCategory.NotFound, $"No outbox row with id {record.Id}.");
        }, ct);
    }

    /// <summary>Loads one row including its RFC822 bytes.</summary>
    public OutboxRecord? GetOutbox(long id, CancellationToken ct = default) =>
        Read<OutboxRecord?>(session =>
        {
            using var reader = session
                .Prepare(SelectOutboxFull + " WHERE id = $id", "$id")
                .SetInt(0, id)
                .ExecuteReader();
            return reader.Read() ? MapOutbox(reader, includeRaw: true) : null;
        }, ct);

    public OutboxRecord? FindOutboxByMessageId(MessageId messageId, CancellationToken ct = default) =>
        Read<OutboxRecord?>(session =>
        {
            using var reader = session
                .Prepare(SelectOutboxFull + " WHERE message_id = $msgid", "$msgid")
                .SetText(0, messageId.Value)
                .ExecuteReader();
            return reader.Read() ? MapOutbox(reader, includeRaw: true) : null;
        }, ct);

    /// <summary>Listing never loads the raw bytes; fetch those with <see cref="GetOutbox"/>.</summary>
    public IReadOnlyList<OutboxRecord> ListOutbox(OutboxState? state = null, CancellationToken ct = default) =>
        Read(session =>
        {
            var statement = state is { } wanted
                ? session
                    .Prepare(SelectOutboxMeta + " WHERE state = $state ORDER BY id", "$state")
                    .SetText(0, wanted.ToWireValue())
                : session.Prepare(SelectOutboxMeta + " ORDER BY id");

            var rows = new List<OutboxRecord>();
            using var reader = statement.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(MapOutbox(reader, includeRaw: false));
            }
            return (IReadOnlyList<OutboxRecord>)rows;
        }, ct);

    /// <summary>Queued or retryable-failed rows whose window has opened, plus the RELIABILITY §14.4 crash window.</summary>
    public IReadOnlyList<OutboxRecord> ListDueOutbox(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Read(session =>
        {
            var rows = new List<OutboxRecord>();
            using var reader = session
                .Prepare(
                    SelectOutboxMeta + " WHERE state = 'sending' "
                    + "OR (state = 'queued' AND (next_attempt_utc IS NULL OR next_attempt_utc <= $now)) "
                    + "OR (state = 'failed' AND permanently_failed = 0 AND next_attempt_utc IS NOT NULL "
                    + "AND next_attempt_utc <= $now AND (max_attempts IS NULL OR attempts < max_attempts)) "
                    + "ORDER BY id",
                    "$now")
                .SetInt(0, ToUnixMs(nowUtc))
                .ExecuteReader();

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(MapOutbox(reader, includeRaw: false));
            }
            return (IReadOnlyList<OutboxRecord>)rows;
        }, ct);

    private static OutboxRecord MapOutbox(SqliteDataReader reader, bool includeRaw)
    {
        var messageIdValue = reader.GetString(2);
        var messageId = MessageId.TryParse(messageIdValue, out var parsed)
            ? parsed
            : throw new StoreException(FailureCategory.Protocol, "An outbox row carries an unusable Message-ID.");

        var nextAttempt = Db.IntOrNull(reader, 6);
        var lastAttempt = Db.IntOrNull(reader, 10);
        var maxAttempts = Db.IntOrNull(reader, 11);

        return new OutboxRecord
        {
            Id = reader.GetInt64(0),
            AccountId = new AccountId(reader.GetInt64(1)),
            MessageId = messageId,
            State = OutboxStateExtensions.FromWireValue(reader.GetString(3)),
            SmtpResponse = Db.Str(reader, 4),
            Attempts = (int)Db.Int(reader, 5),
            NextAttemptUtc = nextAttempt is { } value ? (DateTimeOffset?)FromUnixMs(value) : null,
            CreatedUtc = FromUnixMs(Db.Int(reader, 7)),
            PermanentlyFailed = Db.Bool(reader, 8),
            EnhancedStatusCode = Db.Str(reader, 9),
            LastAttemptUtc = lastAttempt is { } attempted ? (DateTimeOffset?)FromUnixMs(attempted) : null,
            MaxAttempts = maxAttempts is { } budget ? (int?)budget : null,
            Envelope = OutboxEnvelopeJson.Read(Db.Str(reader, 12)),
            Raw = includeRaw ? Db.Blob(reader, 13) ?? Array.Empty<byte>() : Array.Empty<byte>(),
        };
    }

    private const string SelectConfirmToken =
        "SELECT salt, token_hash, expires_utc FROM confirm_tokens WHERE outbox_id = $id";

    private const string DeleteConfirmToken = "DELETE FROM confirm_tokens WHERE outbox_id = $id";

    /// <summary>Replaces any outstanding grant for this draft; only the caller's salted hash is stored.</summary>
    public Task IssueConfirmTokenAsync(
        long outboxId,
        byte[] salt,
        byte[] tokenHash,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        int maxOutstanding,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (outboxId <= 0) throw new ArgumentOutOfRangeException(nameof(outboxId));
        if (maxOutstanding < 1) throw new ArgumentOutOfRangeException(nameof(maxOutstanding));

        return WriteAsync(context =>
        {
            PurgeConfirmTokens(context.Session, issuedUtc);

            context.Session
                .Prepare(
                    "INSERT INTO confirm_tokens (outbox_id, salt, token_hash, issued_utc, expires_utc) "
                    + "VALUES ($id,$salt,$hash,$issued,$expires) "
                    + "ON CONFLICT(outbox_id) DO UPDATE SET salt = excluded.salt, token_hash = excluded.token_hash, "
                    + "issued_utc = excluded.issued_utc, expires_utc = excluded.expires_utc",
                    "$id", "$salt", "$hash", "$issued", "$expires")
                .SetInt(0, outboxId)
                .SetBlob(1, salt)
                .SetBlob(2, tokenHash)
                .SetInt(3, ToUnixMs(issuedUtc))
                .SetInt(4, ToUnixMs(expiresUtc))
                .Execute();

            context.Session
                .Prepare(
                    "DELETE FROM confirm_tokens WHERE outbox_id NOT IN "
                    + "(SELECT outbox_id FROM confirm_tokens ORDER BY issued_utc DESC, outbox_id DESC LIMIT $max)",
                    "$max")
                .SetInt(0, maxOutstanding)
                .Execute();
        }, ct);
    }

    /// <summary>Verifies and spends one grant inside the writer transaction, so two racing
    /// send-drafts can never both be authorized. <paramref name="verify"/> gets the salt and hash.</summary>
    public Task<bool> TryConsumeConfirmTokenAsync(
        long outboxId,
        DateTimeOffset nowUtc,
        Func<byte[], byte[], bool> verify,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verify);

        return WriteAsync(context =>
        {
            byte[]? salt;
            byte[]? hash;
            long expiresUtc;

            using (var reader = context.Session
                .Prepare(SelectConfirmToken, "$id")
                .SetInt(0, outboxId)
                .ExecuteReader())
            {
                if (!reader.Read()) return false;
                salt = Db.Blob(reader, 0);
                hash = Db.Blob(reader, 1);
                expiresUtc = Db.Int(reader, 2);
            }

            if (salt is null || hash is null) return false;

            if (ToUnixMs(nowUtc) >= expiresUtc)
            {
                DeleteConfirmTokenRow(context.Session, outboxId);
                return false;
            }

            if (!verify(salt, hash)) return false;

            return DeleteConfirmTokenRow(context.Session, outboxId) == 1;
        }, ct);
    }

    public Task RevokeConfirmTokenAsync(long outboxId, CancellationToken ct) =>
        WriteAsync(context => { DeleteConfirmTokenRow(context.Session, outboxId); }, ct);

    public Task PurgeConfirmTokensAsync(DateTimeOffset nowUtc, CancellationToken ct) =>
        WriteAsync(context => { PurgeConfirmTokens(context.Session, nowUtc); }, ct);

    public bool HasConfirmToken(long outboxId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Read(session => session
            .Prepare("SELECT 1 FROM confirm_tokens WHERE outbox_id = $id AND expires_utc > $now", "$id", "$now")
            .SetInt(0, outboxId)
            .SetInt(1, ToUnixMs(nowUtc))
            .ExecuteNullableInt64() is not null, ct);

    public int CountConfirmTokens(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Read(session => (int)session
            .Prepare("SELECT COUNT(*) FROM confirm_tokens WHERE expires_utc > $now", "$now")
            .SetInt(0, ToUnixMs(nowUtc))
            .ExecuteInt64(), ct);

    private static int DeleteConfirmTokenRow(DbSession session, long outboxId) =>
        session.Prepare(DeleteConfirmToken, "$id").SetInt(0, outboxId).Execute();

    private static int PurgeConfirmTokens(DbSession session, DateTimeOffset nowUtc) =>
        session
            .Prepare("DELETE FROM confirm_tokens WHERE expires_utc <= $now", "$now")
            .SetInt(0, ToUnixMs(nowUtc))
            .Execute();

    private const string CountSendBudgetSql =
        "SELECT COUNT(*), COALESCE(MIN(sent_utc), 0) FROM send_budget WHERE sent_utc > $floor";

    /// <summary>How much of the hourly agent send budget the window still counts.</summary>
    public SendBudgetUsage ReadSendBudget(long windowFloorMs, CancellationToken ct = default) =>
        Read(session =>
        {
            using var reader = session
                .Prepare(CountSendBudgetSql, "$floor")
                .SetInt(0, windowFloorMs)
                .ExecuteReader();

            if (!reader.Read()) return new SendBudgetUsage(0, 0);
            return new SendBudgetUsage((int)Db.Int(reader, 0), Db.Int(reader, 1));
        }, ct);

    /// <summary>Counts the window and reserves a slot in one writer transaction, so two racing
    /// sends can never share the last one. The refused caller learns when a slot frees up.</summary>
    public Task<SendBudgetReservation> TryReserveSendBudgetAsync(
        string callerInterface,
        int recipientCount,
        DateTimeOffset sentUtc,
        long windowFloorMs,
        int maxSends,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(callerInterface);
        if (recipientCount < 0) throw new ArgumentOutOfRangeException(nameof(recipientCount));
        if (maxSends < 0) throw new ArgumentOutOfRangeException(nameof(maxSends));

        return WriteAsync(context =>
        {
            // Pruning uses the very floor the count uses, so it can never drop a row that still counts.
            context.Session
                .Prepare("DELETE FROM send_budget WHERE sent_utc <= $floor", "$floor")
                .SetInt(0, windowFloorMs)
                .Execute();

            int used;
            long oldest;

            using (var reader = context.Session
                .Prepare(CountSendBudgetSql, "$floor")
                .SetInt(0, windowFloorMs)
                .ExecuteReader())
            {
                if (!reader.Read()) return SendBudgetReservation.Refused(0);
                used = (int)Db.Int(reader, 0);
                oldest = Db.Int(reader, 1);
            }

            if (used >= maxSends) return SendBudgetReservation.Refused(oldest - windowFloorMs);

            var id = context.Session
                .Prepare(
                    "INSERT INTO send_budget (sent_utc, interface, recipients) "
                    + "VALUES ($sent,$interface,$recipients) RETURNING id",
                    "$sent", "$interface", "$recipients")
                .SetInt(0, ToUnixMs(sentUtc))
                .SetText(1, callerInterface)
                .SetInt(2, recipientCount)
                .ExecuteInt64();

            return SendBudgetReservation.Granted(id);
        }, ct);
    }

    /// <summary>Hands a reserved slot back when the send it was reserved for never reached the wire.</summary>
    public Task ReleaseSendBudgetAsync(long reservationId, CancellationToken ct)
    {
        if (reservationId <= 0) return Task.CompletedTask;

        return WriteAsync(context =>
        {
            context.Session
                .Prepare("DELETE FROM send_budget WHERE id = $id", "$id")
                .SetInt(0, reservationId)
                .Execute();
        }, ct);
    }
}

/// <summary>The agent send budget as the window sees it: slots used, and the oldest one's wall clock.</summary>
public readonly record struct SendBudgetUsage(int Used, long OldestSentUtcMs);

/// <summary>A reserved slot of the agent send budget, or the refusal that replaced it.</summary>
public readonly record struct SendBudgetReservation(bool IsGranted, long Id, long RetryAfterMs)
{
    public static SendBudgetReservation Granted(long id) => new(true, id, 0);

    public static SendBudgetReservation Refused(long retryAfterMs) =>
        new(false, 0, retryAfterMs < 0 ? 0 : retryAfterMs);
}
