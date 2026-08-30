using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const string SelectOutboxMeta =
        "SELECT id, account_id, message_id, state, smtp_response, attempts, next_attempt_utc, created_utc FROM outbox";

    private const string SelectOutboxFull =
        "SELECT id, account_id, message_id, state, smtp_response, attempts, next_attempt_utc, created_utc, raw FROM outbox";

    /// <summary>
    /// Persists a queued message. The pre-assigned Message-ID is the idempotency key: enqueueing
    /// the same one twice returns the existing row rather than creating a second send.
    /// </summary>
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
                    "INSERT INTO outbox (account_id, message_id, state, raw, smtp_response, attempts, next_attempt_utc, created_utc) "
                    + "VALUES ($account,$msgid,$state,$raw,$response,$attempts,$next,$created) RETURNING id",
                    "$account", "$msgid", "$state", "$raw", "$response", "$attempts", "$next", "$created")
                .SetInt(0, record.AccountId.Value)
                .SetText(1, record.MessageId.Value)
                .SetText(2, record.State.ToWireValue())
                .SetBlob(3, record.Raw)
                .SetText(4, record.SmtpResponse)
                .SetInt(5, record.Attempts)
                .SetIntOrNull(6, record.NextAttemptUtc is { } next ? (long?)ToUnixMs(next) : null)
                .SetInt(7, ToUnixMs(created))
                .ExecuteInt64();
        }, ct);
    }

    /// <summary>Persists a state transition. The aggregate owns which transitions are legal.</summary>
    public Task SaveOutboxAsync(OutboxRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        return WriteAsync(context =>
        {
            var affected = context.Session
                .Prepare(
                    "UPDATE outbox SET state = $state, smtp_response = $response, attempts = $attempts, "
                    + "next_attempt_utc = $next WHERE id = $id",
                    "$state", "$response", "$attempts", "$next", "$id")
                .SetText(0, record.State.ToWireValue())
                .SetText(1, record.SmtpResponse)
                .SetInt(2, record.Attempts)
                .SetIntOrNull(3, record.NextAttemptUtc is { } next ? (long?)ToUnixMs(next) : null)
                .SetInt(4, record.Id)
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

    /// <summary>
    /// Rows whose retry window has opened, plus anything still marked <c>sending</c> — the crash
    /// window from RELIABILITY §14.4 that must be reconciled against Sent before any re-send.
    /// </summary>
    public IReadOnlyList<OutboxRecord> ListDueOutbox(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Read(session =>
        {
            var rows = new List<OutboxRecord>();
            using var reader = session
                .Prepare(
                    SelectOutboxMeta + " WHERE state = 'sending' OR (state = 'queued' AND "
                    + "(next_attempt_utc IS NULL OR next_attempt_utc <= $now)) ORDER BY id",
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
            Raw = includeRaw ? Db.Blob(reader, 8) ?? Array.Empty<byte>() : Array.Empty<byte>(),
        };
    }
}
