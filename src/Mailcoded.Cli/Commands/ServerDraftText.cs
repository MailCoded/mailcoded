using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;

namespace Mailcoded.Cli.Commands;

internal static class ServerDraftText
{
    public static string Describe(ServerDraftSave save, ServerDraftReason reason) => save switch
    {
        ServerDraftSave.Saved => "saved to the server's Drafts Folder",
        ServerDraftSave.Failed => "not saved: " + Why(reason) + "; the draft exists locally",
        _ => "off",
    };

    public static string Describe(ServerDraftState? state, ServerDraftReason reason) => state switch
    {
        null => "none",
        ServerDraftState.Saved => "still in Drafts",
        ServerDraftState.Moving => "being moved to Sent",
        ServerDraftState.Moved => "moved from Drafts to Sent",
        _ => "left in Drafts (" + Why(reason) + ")",
    };

    public static string Why(ServerDraftReason reason) => reason switch
    {
        ServerDraftReason.NoMoveCapability => "the server has no MOVE",
        ServerDraftReason.SentCopyNotWanted => "no copy was asked for in Sent",
        ServerDraftReason.ServerSavesSent => "the server saves sent mail itself",
        ServerDraftReason.NoSentFolder => "no Sent Folder is known",
        ServerDraftReason.NoDraftsFolder => "no Drafts Folder is known",
        ServerDraftReason.NotConnected => "the server could not be reached",
        ServerDraftReason.AppendFailed => "the server refused the draft",
        ServerDraftReason.UidUnknown => "the server did not report where it landed",
        ServerDraftReason.UidValidityChanged => "the Folder was renumbered since the draft was saved",
        ServerDraftReason.DraftGone => "the draft is no longer in Drafts",
        ServerDraftReason.MoveRefused => "the server refused the move",
        ServerDraftReason.MoveUnconfirmed => "the move was interrupted and may have happened",
        ServerDraftReason.Interrupted => "the send was interrupted before the move",
        _ => "unspecified",
    };
}
