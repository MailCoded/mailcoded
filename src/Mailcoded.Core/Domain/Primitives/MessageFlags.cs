namespace Mailcoded.Core.Domain.Primitives;

/// <summary>
/// Server-owned IMAP system flags, stored as the <c>messages.flags</c> bitfield.
/// Bit 0 is <see cref="Unread"/> — the inverse of <c>\Seen</c> — so that the common
/// "unread" predicate is <c>(flags &amp; 1) = 1</c> and a zero row reads as "seen, nothing else".
/// </summary>
[Flags]
public enum MessageFlags
{
    None = 0,
    Unread = 1 << 0,
    Flagged = 1 << 1,
    Answered = 1 << 2,
    Draft = 1 << 3,
    Deleted = 1 << 4,
    Recent = 1 << 5,
}

/// <summary>A folder's well-known purpose, from IMAP SPECIAL-USE or a name heuristic.</summary>
public enum FolderRole
{
    None = 0,
    Inbox,
    Sent,
    Drafts,
    Trash,
    Archive,
    Junk,
    All,
}

public static class FolderRoleExtensions
{
    public static string? ToWireValue(this FolderRole role) => role switch
    {
        FolderRole.None => null,
        FolderRole.Inbox => "inbox",
        FolderRole.Sent => "sent",
        FolderRole.Drafts => "drafts",
        FolderRole.Trash => "trash",
        FolderRole.Archive => "archive",
        FolderRole.Junk => "junk",
        FolderRole.All => "all",
        _ => null,
    };

    public static FolderRole FromWireValue(string? value) => value switch
    {
        "inbox" => FolderRole.Inbox,
        "sent" => FolderRole.Sent,
        "drafts" => FolderRole.Drafts,
        "trash" => FolderRole.Trash,
        "archive" => FolderRole.Archive,
        "junk" => FolderRole.Junk,
        "all" => FolderRole.All,
        _ => FolderRole.None,
    };
}
