using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

/// <summary>Stable <c>sync_log.event</c> names. Append here; never rename or reuse one.</summary>
public static class AuditEvents
{
    public const string AccountAdded = "account_added";
    public const string AccountUpdated = "account_updated";
    public const string SyncStarted = "sync_started";
    public const string SyncCompleted = "sync_completed";
    public const string SyncFailed = "sync_failed";
    public const string DegradedSync = "degraded_sync";
    public const string QuirkLatched = "quirk_latched";
    public const string UidValidityChanged = "uidvalidity_changed";
    public const string FolderReconciled = "folder_reconciled";
    public const string FolderOrphaned = "folder_orphaned";
    public const string BodyFetched = "body_fetched";
    public const string TagsSet = "tags_set";
    public const string MessageMoved = "message_moved";
    public const string AttachmentRead = "attachment_read";
    public const string SendPreview = "send_preview";
    public const string SendAttempt = "send_attempt";
    public const string SendResult = "send_result";
    public const string SentAppend = "sent_append";
    public const string SentAppendFailed = "sent_append_failed";
    public const string OutboxReconciled = "outbox_reconciled";
    public const string OutboxInvestigate = "outbox_investigate";
    public const string SqlQuery = "sql_query";
    public const string Metrics = "metrics";
}

/// <summary>One audit row before it becomes a <see cref="SyncLogEntry"/>.</summary>
public sealed record AuditEntry
{
    public required string Event { get; init; }
    public AccountId AccountId { get; init; } = AccountId.None;
    public string Level { get; init; } = AuditLog.LevelInfo;
    public string? Detail { get; init; }
    public CallerContext Caller { get; init; } = CallerContext.Internal;
}

/// <summary>Everything the send audit trail records. The body never appears, only its digest.</summary>
public sealed record SendAuditRecord
{
    public required AccountId AccountId { get; init; }
    public required string Method { get; init; }
    public required string Decision { get; init; }
    public required string ArgsDigest { get; init; }
    public CallerContext Caller { get; init; } = CallerContext.Internal;
    public MessageId? MessageId { get; init; }
    public long OutboxId { get; init; }
    public int RecipientCount { get; init; }
    public bool TokenConsumed { get; init; }
    public string? Reason { get; init; }
    public string Level { get; init; } = AuditLog.LevelInfo;
}

/// <summary>Writes the append-only audit rows invariant 5 requires. Never rewrites or deletes one.</summary>
public sealed class AuditLog
{
    public const string LevelInfo = "info";
    public const string LevelWarn = "warn";
    public const string LevelError = "error";

    private readonly SqliteStore _store;
    private readonly IClock _clock;

    public AuditLog(SqliteStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _clock = clock;
    }

    public Task<long> WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _store.AppendSyncLogAsync(ToRow(entry), ct);
    }

    public Task<long> InfoAsync(string @event, AccountId accountId, CallerContext caller, string? detail, CancellationToken ct) =>
        WriteAsync(new AuditEntry { Event = @event, AccountId = accountId, Caller = caller, Detail = detail }, ct);

    public Task<long> WarnAsync(string @event, AccountId accountId, CallerContext caller, string? detail, CancellationToken ct) =>
        WriteAsync(new AuditEntry
        {
            Event = @event,
            AccountId = accountId,
            Caller = caller,
            Detail = detail,
            Level = LevelWarn,
        }, ct);

    public Task<long> ErrorAsync(string @event, AccountId accountId, CallerContext caller, string? detail, CancellationToken ct) =>
        WriteAsync(new AuditEntry
        {
            Event = @event,
            AccountId = accountId,
            Caller = caller,
            Detail = detail,
            Level = LevelError,
        }, ct);

    /// <summary>The per-attempt send row: method, args digest, decision, token-consumed.</summary>
    public Task<long> SendAsync(SendAuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var detail = AuditText.Fields(
            ("method", record.Method),
            ("decision", record.Decision),
            ("digest", record.ArgsDigest),
            ("outbox", record.OutboxId > 0 ? AuditText.Number(record.OutboxId) : null),
            ("msgid", record.MessageId is { } id ? AuditText.Digest(id.Value) : null),
            ("recipients", AuditText.Number(record.RecipientCount)),
            ("token", record.TokenConsumed ? "consumed" : "none"),
            ("reason", record.Reason));

        return WriteAsync(new AuditEntry
        {
            Event = record.Method,
            AccountId = record.AccountId,
            Caller = record.Caller,
            Detail = detail,
            Level = record.Level,
        }, ct);
    }

    /// <summary>Invariant 5: every tag mutation leaves a row, whatever surface asked for it.</summary>
    public Task<long> TagsAsync(
        AccountId accountId,
        CallerContext caller,
        LocalMessageId messageId,
        TagDelta delta,
        IReadOnlyList<Tag> resulting,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(resulting);

        var detail = AuditText.Fields(
            ("message", AuditText.Number(messageId.Value)),
            ("add", Join(delta.Add)),
            ("remove", Join(delta.Remove)),
            ("tags", Join(resulting)));

        return InfoAsync(AuditEvents.TagsSet, accountId, caller, detail, ct);
    }

    public Task<long> DegradedSyncAsync(AccountId accountId, FolderPath path, string reason, CancellationToken ct) =>
        WarnAsync(
            AuditEvents.DegradedSync,
            accountId,
            CallerContext.Internal,
            AuditText.Fields(("folder", path.Value), ("reason", reason)),
            ct);

    public Task AppendManyAsync(IReadOnlyList<AuditEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return Task.CompletedTask;

        var rows = new List<SyncLogEntry>(entries.Count);
        foreach (var entry in entries) rows.Add(ToRow(entry));
        return _store.AppendSyncLogAsync(rows, ct);
    }

    private SyncLogEntry ToRow(AuditEntry entry) => new()
    {
        TimestampUtc = _clock.UtcNow,
        AccountId = entry.AccountId,
        Level = entry.Level,
        Event = entry.Event,
        Detail = AuditText.Sanitize(entry.Detail),
        Interface = entry.Caller.InterfaceName,
        AgentHost = entry.Caller.AgentHost,
    };

    private static string? Join(IReadOnlyList<Tag> tags)
    {
        if (tags.Count == 0) return null;

        var builder = new System.Text.StringBuilder(64);
        foreach (var tag in tags)
        {
            if (builder.Length > 0) builder.Append(',');
            if (builder.Length > 96) { builder.Append('…'); break; }
            builder.Append(tag.Value);
        }

        return builder.ToString();
    }
}
