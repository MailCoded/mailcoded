using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Outbox;

/// <summary>Where a server copy of an outbox draft stands. Nothing in this lifecycle removes a message.</summary>
public enum ServerDraftState
{
    Saved,

    /// <summary>Written before the MOVE is issued, so a crash leaves a state that is never retried.</summary>
    Moving,
    Moved,
    Left,
}

public enum ServerDraftReason
{
    None,
    NoMoveCapability,
    SentCopyNotWanted,
    ServerSavesSent,
    NoSentFolder,
    NoDraftsFolder,
    NotConnected,
    AppendFailed,
    UidUnknown,
    UidValidityChanged,
    DraftGone,
    MoveRefused,
    MoveUnconfirmed,
    Interrupted,
}

public static class ServerDraftWire
{
    public static string ToWireValue(this ServerDraftState state) => state switch
    {
        ServerDraftState.Saved => "saved",
        ServerDraftState.Moving => "moving",
        ServerDraftState.Moved => "moved",
        ServerDraftState.Left => "left",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static bool TryParseState(string? value, out ServerDraftState state)
    {
        switch (value)
        {
            case "saved": state = ServerDraftState.Saved; return true;
            case "moving": state = ServerDraftState.Moving; return true;
            case "moved": state = ServerDraftState.Moved; return true;
            case "left": state = ServerDraftState.Left; return true;
            default: state = default; return false;
        }
    }

    public static string? ToWireValue(this ServerDraftReason reason) => reason switch
    {
        ServerDraftReason.None => null,
        ServerDraftReason.NoMoveCapability => "no-move-capability",
        ServerDraftReason.SentCopyNotWanted => "sent-copy-not-wanted",
        ServerDraftReason.ServerSavesSent => "server-saves-sent",
        ServerDraftReason.NoSentFolder => "no-sent-folder",
        ServerDraftReason.NoDraftsFolder => "no-drafts-folder",
        ServerDraftReason.NotConnected => "not-connected",
        ServerDraftReason.AppendFailed => "append-failed",
        ServerDraftReason.UidUnknown => "uid-unknown",
        ServerDraftReason.UidValidityChanged => "uidvalidity-changed",
        ServerDraftReason.DraftGone => "draft-gone",
        ServerDraftReason.MoveRefused => "move-refused",
        ServerDraftReason.MoveUnconfirmed => "move-unconfirmed",
        ServerDraftReason.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    public static ServerDraftReason ParseReason(string? value) => value switch
    {
        "no-move-capability" => ServerDraftReason.NoMoveCapability,
        "sent-copy-not-wanted" => ServerDraftReason.SentCopyNotWanted,
        "server-saves-sent" => ServerDraftReason.ServerSavesSent,
        "no-sent-folder" => ServerDraftReason.NoSentFolder,
        "no-drafts-folder" => ServerDraftReason.NoDraftsFolder,
        "not-connected" => ServerDraftReason.NotConnected,
        "append-failed" => ServerDraftReason.AppendFailed,
        "uid-unknown" => ServerDraftReason.UidUnknown,
        "uidvalidity-changed" => ServerDraftReason.UidValidityChanged,
        "draft-gone" => ServerDraftReason.DraftGone,
        "move-refused" => ServerDraftReason.MoveRefused,
        "move-unconfirmed" => ServerDraftReason.MoveUnconfirmed,
        "interrupted" => ServerDraftReason.Interrupted,
        _ => ServerDraftReason.None,
    };
}

/// <summary>The server copy of an outbox draft: the identity it was given and what has been done with it.</summary>
public sealed record ServerDraft
{
    public required string FolderPath { get; init; }
    public required UidValidity UidValidity { get; init; }

    /// <summary>Null when the server did not report the UID of the appended message.</summary>
    public Uid? Uid { get; init; }

    public required ServerDraftState State { get; init; }
    public ServerDraftReason Reason { get; init; }
}
