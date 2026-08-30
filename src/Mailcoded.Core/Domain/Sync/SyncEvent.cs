using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Sync;

/// <summary>
/// One observable change to a folder. Providers emit these; <see cref="SyncPlanner.Apply"/>
/// folds them into the next <see cref="FolderState"/>; the store persists both in one transaction.
/// </summary>
public abstract record SyncEvent
{
    private SyncEvent() { }

    public sealed record EnvelopeAdded(RemoteEnvelope Envelope) : SyncEvent;

    public sealed record FlagsChanged(Uid Uid, MessageFlags Flags, IReadOnlyList<string> Keywords, ModSeq ModSeq) : SyncEvent;

    public sealed record MessageExpunged(Uid Uid) : SyncEvent;

    public sealed record UidValidityChanged(UidValidity Previous, UidValidity Current) : SyncEvent;

    /// <summary>The server's advertised capability lied; latch a quirk and re-plan more conservatively.</summary>
    public sealed record QuirkDetected(ServerQuirks Quirk, string Detail) : SyncEvent;

    /// <summary>Batch boundary: everything up to this point is durable once the transaction commits.</summary>
    public sealed record BatchComplete(ModSeq HighestModSeq, Uid? HighestUid, int Count) : SyncEvent;
}

/// <summary>
/// A message summary as the server reports it. Deliberately free of MailKit types — the
/// provider adapter translates before this crosses into Domain.
/// </summary>
public sealed record RemoteEnvelope
{
    public required Uid Uid { get; init; }
    public required MessageFlags Flags { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public ModSeq ModSeq { get; init; } = ModSeq.Zero;

    public string? MessageIdHeader { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    public string? InReplyTo { get; init; }

    public string? Subject { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Cc { get; init; }

    /// <summary>Header Date, already falling back to INTERNALDATE when absent or absurd (edge case 18).</summary>
    public DateTimeOffset DateUtc { get; init; }

    public long Size { get; init; }
    public bool HasAttachments { get; init; }

    /// <summary>Gmail's cross-folder identity, when the server offers X-GM-MSGID (edge case 5).</summary>
    public ulong? GmailMessageId { get; init; }
    public ulong? GmailThreadId { get; init; }
}
