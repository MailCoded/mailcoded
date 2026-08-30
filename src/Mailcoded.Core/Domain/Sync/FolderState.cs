using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Sync;

/// <summary>
/// Everything the planner needs to know about the local side of one folder. Pure data —
/// it is loaded from the store, handed to <see cref="SyncPlanner"/>, and persisted back.
/// </summary>
public sealed record FolderState
{
    public required FolderId FolderId { get; init; }
    public required AccountId AccountId { get; init; }
    public required FolderPath Path { get; init; }
    public UidValidity UidValidity { get; init; } = UidValidity.Unknown;
    public ModSeq HighestModSeq { get; init; } = ModSeq.Zero;

    /// <summary>Highest UID we have locally, or null when the folder has never been synced.</summary>
    public Uid? HighestKnownUid { get; init; }

    public int KnownMessageCount { get; init; }

    /// <summary>
    /// UIDs we already hold, populated only for the FullDiff path — the planner needs them to
    /// compute the set difference. Left null on the delta paths so a 500k folder is never
    /// materialized for a no-op resync.
    /// </summary>
    public IReadOnlyList<Uid>? KnownUids { get; init; }

    /// <summary>Resumable backfill cursor: UIDs below this have not been fetched yet (edge case 32).</summary>
    public Uid? BackfillCursor { get; init; }

    /// <summary>Server-side flag vocabulary. Absence of <c>\*</c> means keywords are local-only (edge case 22).</summary>
    public bool ServerAcceptsCustomKeywords { get; init; } = true;

    /// <summary>
    /// When the last full UID diff ran. CONDSTORE reports flag changes but never expunges, so a
    /// CONDSTORE-only server hides deletions until a full diff forces them into view.
    /// </summary>
    public DateTimeOffset? LastFullDiffUtc { get; init; }

    public bool HasEverSynced => !UidValidity.IsUnknown;
}

/// <summary>What the server told us when we opened the folder.</summary>
public sealed record ServerFolderInfo
{
    public required FolderPath Path { get; init; }
    public required UidValidity UidValidity { get; init; }
    public ModSeq HighestModSeq { get; init; } = ModSeq.Zero;
    public Uid? UidNext { get; init; }
    public int TotalCount { get; init; }
    public int UnreadCount { get; init; }
    public FolderRole Role { get; init; } = FolderRole.None;

    /// <summary>EXAMINE-only folders must never receive a flag write (edge case 23).</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>PERMANENTFLAGS advertised <c>\*</c>, so custom keywords will persist (edge case 22).</summary>
    public bool PermanentFlagsAllowCustomKeywords { get; init; } = true;
}
