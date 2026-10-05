namespace Mailcoded.Protocol;

/// <summary>
/// <c>send.preview</c> — phase one of the two-phase send. Builds the message, stores it in the
/// outbox as a draft, and returns the one-time token phase two requires.
/// </summary>
public sealed record SendPreviewParams
{
    public required long AccountId { get; init; }

    public required DraftDto Draft { get; init; }
}

public sealed record SendPreviewResult
{
    /// <summary>Outbox row id; <c>send</c> takes this as <c>draftId</c>.</summary>
    public required long DraftId { get; init; }

    public required SendPreviewDto Preview { get; init; }

    /// <summary>Single-use, bound to this draft. Not a secret in the keyring sense, but never log it.</summary>
    public required string ConfirmToken { get; init; }

    /// <summary>ISO 8601 UTC after which the token is rejected with error 1003.</summary>
    public string? ConfirmTokenExpiresUtc { get; init; }

    /// <summary><c>off|saved|failed</c>: whether the draft was also appended to the server's Drafts Folder. A failure never blocks the draft.</summary>
    public string ServerDraft { get; init; } = "off";

    /// <summary>Why <see cref="ServerDraft"/> is <c>failed</c>: <c>no-drafts-folder|not-connected|append-failed</c>.</summary>
    public string? ServerDraftReason { get; init; }

    /// <summary>Redacted so a logged result cannot hand the token to whoever reads the log.</summary>
    public override string ToString() =>
        $"SendPreviewResult {{ DraftId = {DraftId}, ConfirmToken = [redacted], Expires = {ConfirmTokenExpiresUtc} }}";
}

/// <summary>
/// <c>send</c> — phase two. Rejected with 1003 without a valid, unexpired, unconsumed token,
/// and with 1006 when a Core safety gate is closed. Every attempt writes to <c>sync_log</c>.
/// </summary>
public sealed record SendParams
{
    public required long AccountId { get; init; }

    public required long DraftId { get; init; }

    public required string ConfirmToken { get; init; }
}

public sealed record SendResult
{
    /// <summary>The pre-assigned RFC 5322 Message-ID, without angle brackets.</summary>
    public required string MessageId { get; init; }

    /// <summary>Outbox state after the attempt: <c>queued|sending|sent|failed</c>.</summary>
    public required string State { get; init; }

    public long? OutboxId { get; init; }

    /// <summary>Sanitized, length-capped SMTP reply line for the audit record.</summary>
    public string? SmtpResponse { get; init; }

    /// <summary>True when mailcoded appended a separate copy to Sent. False when the server draft was moved there instead.</summary>
    public bool AppendedToSent { get; init; }

    /// <summary><c>none|saved|moving|moved|left</c>: what became of the server draft. <c>moved</c> is now the Sent copy; <c>left</c> still sits in Drafts.</summary>
    public string ServerDraft { get; init; } = "none";

    /// <summary>Why the server draft was left; the slugs are listed under <c>send</c> in docs/rpc.md.</summary>
    public string? ServerDraftReason { get; init; }
}
