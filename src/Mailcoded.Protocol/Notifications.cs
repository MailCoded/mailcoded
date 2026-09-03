namespace Mailcoded.Protocol;

/// <summary><c>notify.mail.added</c> — new messages landed in a watched folder.</summary>
public sealed record MailAddedNotification
{
    public required long AccountId { get; init; }

    public required long FolderId { get; init; }

    public required string FolderName { get; init; }

    /// <summary>How many arrived; may exceed <see cref="Messages"/> when the batch was coalesced.</summary>
    public required int Count { get; init; }

    /// <summary>Newest arrivals, capped so an IDLE storm cannot flood the client (edge case 27).</summary>
    public IReadOnlyList<EnvelopeDto> Messages { get; init; } = [];
}

/// <summary><c>notify.folder.updated</c> — counts or state changed; refresh the badge.</summary>
public sealed record FolderUpdatedNotification
{
    public required long AccountId { get; init; }

    public required FolderDto Folder { get; init; }
}

/// <summary><c>notify.sync.error</c> — a background sync or watch failed. Carries no mail content.</summary>
public sealed record SyncErrorNotification
{
    public required long AccountId { get; init; }

    public long? FolderId { get; init; }

    /// <summary>The same stable numeric code the equivalent request error would carry.</summary>
    public required int Code { get; init; }

    /// <summary>Short and redacted: never a credential, never a header or body.</summary>
    public required string Message { get; init; }

    /// <summary><c>network|protocol|auth|busy|full|notFound|unsupported</c>.</summary>
    public string? Category { get; init; }

    /// <summary>True when backoff alone will not recover; the user must re-authenticate or fix settings.</summary>
    public bool RequiresUserAction { get; init; }

    public int? RetryAfterMs { get; init; }

    /// <summary>ISO 8601 UTC.</summary>
    public required string AtUtc { get; init; }
}
