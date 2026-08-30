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
            // A record read from a listing carries no envelope; COALESCE keeps Bcc from being erased.
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
}
