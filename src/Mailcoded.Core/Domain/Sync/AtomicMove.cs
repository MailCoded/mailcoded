using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Sync;

/// <summary>What a move that is allowed to be nothing but a single atomic server command reports.</summary>
public enum AtomicMoveStatus
{
    Moved,

    /// <summary>The Folder's UIDVALIDITY is not the one the caller recorded, so the UID may name another message.</summary>
    EpochChanged,

    /// <summary>No message with that UID is in the source Folder any more.</summary>
    SourceGone,

    /// <summary>The server has no MOVE, or the source Folder is read-only. Nothing was touched.</summary>
    Unsupported,
}

public readonly record struct AtomicMoveResult(AtomicMoveStatus Status, Uid? NewUid)
{
    public static AtomicMoveResult Of(AtomicMoveStatus status) => new(status, null);
}
