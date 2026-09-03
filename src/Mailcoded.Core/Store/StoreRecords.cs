using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using ParsedQuery = Mailcoded.Core.Domain.Search.SearchQuery;

namespace Mailcoded.Core.Store;

/// <summary>A folder row with its denormalized counters, as the sidebar needs it.</summary>
public sealed record FolderSummary
{
    public required FolderId Id { get; init; }
    public required AccountId AccountId { get; init; }
    public required FolderPath Path { get; init; }
    public FolderRole Role { get; init; } = FolderRole.None;
    public UidValidity UidValidity { get; init; } = UidValidity.Unknown;
    public Uid? UidNext { get; init; }
    public ModSeq HighestModSeq { get; init; } = ModSeq.Zero;
    public string? DeltaToken { get; init; }
    public int UnreadCount { get; init; }
    public int TotalCount { get; init; }

    /// <summary>Last successful sync of this folder, or null when it has never synced.</summary>
    public DateTimeOffset? LastSyncUtc { get; init; }
}

/// <summary>
/// The columns carried by the <c>ix_msg_folder_date</c> covering index, and nothing more:
/// widening this record takes the envelope page off the index-only path.
/// </summary>
public sealed record EnvelopeSummary
{
    public required LocalMessageId Id { get; init; }
    public string? Subject { get; init; }
    public string? From { get; init; }
    public DateTimeOffset DateUtc { get; init; }
    public MessageFlags Flags { get; init; }
}

/// <summary>The newest message of one conversation, from the ROW_NUMBER query in PERFORMANCE §15.4.</summary>
public sealed record ThreadSummary
{
    public required LocalMessageId Id { get; init; }
    public ThreadKey? ThreadKey { get; init; }
    public string? Subject { get; init; }
    public string? From { get; init; }
    public DateTimeOffset DateUtc { get; init; }
    public MessageFlags Flags { get; init; }
}

/// <summary>A full envelope row.</summary>
public sealed record EnvelopeRow
{
    public required LocalMessageId Id { get; init; }
    public required AccountId AccountId { get; init; }
    public required FolderId FolderId { get; init; }
    public Uid? Uid { get; init; }
    public MessageId? MessageId { get; init; }
    public ThreadKey? ThreadKey { get; init; }
    public DateTimeOffset DateUtc { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Cc { get; init; }
    public string? Subject { get; init; }
    public MessageFlags Flags { get; init; }
    public ModSeq ModSeq { get; init; } = ModSeq.Zero;
    public long Size { get; init; }
    public bool HasAttachments { get; init; }
    public bool BodyFetched { get; init; }
    public BlobId? BlobId { get; init; }
}

/// <summary>One keyset page. Nothing is lost: every match is reachable by following NextCursor.</summary>
public sealed record StorePage<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Opaque cursor for the next page, or null when no further page can be served.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Another page exists; always exactly <c>NextCursor is not null</c>. Never means rows were dropped.</summary>
    public bool Truncated { get; init; }

    public static StorePage<T> Empty { get; } = new() { Items = [] };
}

public readonly record struct IngestResult(int Inserted, int Updated)
{
    public int Total => Inserted + Updated;
}

/// <summary>One server flag change to fold into the store. Server always wins on flags (invariant 9).</summary>
public readonly record struct FlagUpdate(Uid Uid, MessageFlags Flags, ModSeq ModSeq);

/// <summary>
/// Everything one provider batch produced, persisted in a single transaction so a mid-batch crash
/// replays idempotently (SPEC §5.5 step 8).
/// </summary>
public sealed record SyncBatch
{
    public required FolderId FolderId { get; init; }
    public IReadOnlyList<RemoteEnvelope> Added { get; init; } = [];
    public IReadOnlyList<FlagUpdate> FlagChanges { get; init; } = [];
    public IReadOnlyList<Uid> Expunged { get; init; } = [];

    /// <summary>Folder state to persist with the batch; null leaves the stored state untouched.</summary>
    public FolderState? NextState { get; init; }

    public ServerFolderInfo? ServerInfo { get; init; }
}

/// <summary>A blob as stored: inline bytes at or below the threshold, an external file above it.</summary>
public sealed record BlobRef
{
    public required BlobId Id { get; init; }
    public required string Sha256 { get; init; }
    public long Size { get; init; }

    /// <summary>Absolute path when the blob was externalized, null when it lives inline.</summary>
    public string? ExternalPath { get; init; }

    public bool IsExternal => ExternalPath is not null;
}

public sealed record OutboxRecord
{
    public long Id { get; init; }
    public required AccountId AccountId { get; init; }
    public required MessageId MessageId { get; init; }
    public required OutboxState State { get; init; }

    /// <summary>The RFC822 bytes. Empty on rows returned by a listing, which never loads them.</summary>
    public byte[] Raw { get; init; } = [];

    public string? SmtpResponse { get; init; }

    /// <summary>RFC 3463 enhanced status code from the last reply, when the server sent one.</summary>
    public string? EnhancedStatusCode { get; init; }

    public int Attempts { get; init; }

    /// <summary>Retry budget. Null on a row written before this was persisted.</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>True when nothing further will be attempted without an explicit human retry.</summary>
    public bool PermanentlyFailed { get; init; }

    public DateTimeOffset? NextAttemptUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Recipients captured at preview time; a parse of Raw can never recover Bcc.</summary>
    public OutboxEnvelope? Envelope { get; init; }
}

/// <summary>The addressed envelope of a queued send, including the Bcc list the raw bytes omit.</summary>
public sealed record OutboxEnvelope
{
    public EmailAddress From { get; init; }
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];

    public bool IsEmpty => To.Count == 0 && Cc.Count == 0 && Bcc.Count == 0 && string.IsNullOrEmpty(From.Value);

    /// <summary>Every RCPT TO for this send, in header order.</summary>
    public IReadOnlyList<EmailAddress> AllRecipients() => [.. To, .. Cc, .. Bcc];
}

/// <summary>An append-only audit row. Never rewritten, never deleted.</summary>
public sealed record SyncLogEntry
{
    public long Id { get; init; }

    /// <summary>Wall clock, for the record only. Never used to measure an interval.</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    public AccountId AccountId { get; init; } = AccountId.None;
    public string Level { get; init; } = "info";
    public required string Event { get; init; }

    /// <summary>Free-form context. Callers must never put a credential or a raw body in here.</summary>
    public string? Detail { get; init; }

    /// <summary>cli | mcp | rpc | internal</summary>
    public string? Interface { get; init; }

    public string? AgentHost { get; init; }
}

public enum SearchRoute
{
    /// <summary>Latin text through msg_fts with bm25 ranking.</summary>
    Fts,

    /// <summary>CJK of three runes or more through the trigram index.</summary>
    Cjk,

    /// <summary>One or two CJK runes: below the trigram floor, so LIKE is the only correct answer.</summary>
    Like,

    /// <summary>The query held nothing indexable.</summary>
    None,
}

public enum SearchOrder
{
    /// <summary>bm25 rank. Pages shallowly and reports truncation past the offset cap.</summary>
    Relevance,

    /// <summary>Newest first with a true keyset cursor; pages to any depth.</summary>
    Date,
}

/// <summary>
/// A search as the store executes it: the structured query <c>SearchQueryParser</c> produced,
/// plus scope and paging. Every predicate in it is served by SQL — nothing is left for
/// a caller to re-filter in managed code.
/// </summary>
public sealed record StoreSearchRequest
{
    public required ParsedQuery Query { get; init; }

    /// <summary>Scope filters applied on top of the query's own <c>folder:</c>/account predicates.</summary>
    public AccountId? AccountId { get; init; }

    public FolderId? FolderId { get; init; }
    public int Limit { get; init; } = 50;
    public string? Cursor { get; init; }

    /// <summary>Ignored unless the query has Latin full-text terms; every other shape pages by date.</summary>
    public SearchOrder Order { get; init; } = SearchOrder.Relevance;

    public bool IncludeSnippet { get; init; } = true;
}

public sealed record StoreSearchHit
{
    public required LocalMessageId Id { get; init; }
    public required FolderId FolderId { get; init; }
    public string? Subject { get; init; }
    public string? From { get; init; }
    public DateTimeOffset DateUtc { get; init; }
    public MessageFlags Flags { get; init; }
    public bool HasAttachments { get; init; }
    public bool BodyFetched { get; init; }
    public long Size { get; init; }

    /// <summary>Computed only for the returned page, never across the whole result set.</summary>
    public string? Snippet { get; init; }
}

public sealed record StoreSearchResult
{
    public required IReadOnlyList<StoreSearchHit> Hits { get; init; }

    /// <summary>Cursor for the next page, or null when this page is the last one servable.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Matches were DROPPED that no further call can reach (relevance offset cap) — stronger
    /// than <c>StorePage.Truncated</c>. Date order sets this false; its cursor reaches any depth.</summary>
    public bool Truncated { get; init; }

    public SearchRoute Route { get; init; }

    public static StoreSearchResult Empty(SearchRoute route) => new() { Hits = [], Route = route };
}

public sealed record StoreStats
{
    public required string DatabasePath { get; init; }
    public int SchemaVersion { get; init; }
    public long DatabaseSizeBytes { get; init; }
    public long WalSizeBytes { get; init; }
    public long PageCount { get; init; }
    public long PageSizeBytes { get; init; }
    public long FreelistPages { get; init; }
    public long BlobDirectorySizeBytes { get; init; }
    public required IReadOnlyDictionary<string, long> TableCounts { get; init; }
}

public sealed record MaintenanceOptions
{
    public bool IncrementalVacuum { get; init; } = true;
    public int IncrementalVacuumPages { get; init; } = 256;
    public bool OptimizeFts { get; init; } = true;
    public bool Analyze { get; init; }
    public bool CheckpointWal { get; init; } = true;
    public bool TruncateWal { get; init; } = true;
    public bool QuickCheck { get; init; }
}

public sealed record MaintenanceReport
{
    public bool Ok { get; init; } = true;

    /// <summary>Output of PRAGMA quick_check, or null when it was not run.</summary>
    public string? IntegrityResult { get; init; }

    public long WalPagesCheckpointed { get; init; }
    public long ReclaimedPages { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed record BulkIngestReport
{
    public int Inserted { get; init; }
    public int Updated { get; init; }
    public int FtsRowsPopulated { get; init; }
    public TimeSpan Elapsed { get; init; }
    public double RowsPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : (Inserted + Updated) / Elapsed.TotalSeconds;
}

/// <summary>Result of the gated read-only SQL surface (AGENT-INTERFACE §13.2). Always row-capped.</summary>
public sealed record RawQueryResult
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public bool Truncated { get; init; }
}
