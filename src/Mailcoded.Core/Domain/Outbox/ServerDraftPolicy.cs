namespace Mailcoded.Core.Domain.Outbox;

public sealed record ServerDraftPlanInput
{
    public bool Connected { get; init; }
    public bool SupportsMove { get; init; }
    public bool AppendToSent { get; init; }
    public bool ServerSavesSent { get; init; }
    public bool SentFolderKnown { get; init; }
    public bool DraftsFolderKnown { get; init; }
}

public readonly record struct ServerDraftPlan(bool Move, ServerDraftReason Reason);

/// <summary>What becomes of the server draft once a send has been accepted, and whether a Sent copy is still owed.</summary>
public readonly record struct ServerDraftSettlement(ServerDraftState State, ServerDraftReason Reason, bool SentCopyStillNeeded);

/// <summary>The after-send rule for a server draft: one atomic MOVE or leave it alone; no outcome removes a message.</summary>
public static class ServerDraftPolicy
{
    public static ServerDraftPlan Plan(ServerDraftPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.Connected) return Leave(ServerDraftReason.NotConnected);
        if (input.ServerSavesSent) return Leave(ServerDraftReason.ServerSavesSent);
        if (!input.AppendToSent) return Leave(ServerDraftReason.SentCopyNotWanted);
        if (!input.SentFolderKnown) return Leave(ServerDraftReason.NoSentFolder);
        if (!input.DraftsFolderKnown) return Leave(ServerDraftReason.NoDraftsFolder);
        if (!input.SupportsMove) return Leave(ServerDraftReason.NoMoveCapability);

        return new ServerDraftPlan(true, ServerDraftReason.None);
    }

    public static ServerDraftSettlement SettleMove(Sync.AtomicMoveStatus status) => status switch
    {
        Sync.AtomicMoveStatus.Moved => new(ServerDraftState.Moved, ServerDraftReason.None, false),
        Sync.AtomicMoveStatus.EpochChanged => new(ServerDraftState.Left, ServerDraftReason.UidValidityChanged, true),
        Sync.AtomicMoveStatus.SourceGone => new(ServerDraftState.Left, ServerDraftReason.DraftGone, true),
        Sync.AtomicMoveStatus.Unsupported => new(ServerDraftState.Left, ServerDraftReason.NoMoveCapability, true),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>A command the server answered with a refusal changed nothing; a dropped connection may have moved it.</summary>
    public static ServerDraftSettlement SettleFailure(bool outcomeUnknown) =>
        outcomeUnknown
            ? new(ServerDraftState.Left, ServerDraftReason.MoveUnconfirmed, false)
            : new(ServerDraftState.Left, ServerDraftReason.MoveRefused, true);

    private static ServerDraftPlan Leave(ServerDraftReason reason) => new(false, reason);
}
