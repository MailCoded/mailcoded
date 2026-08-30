using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// The result of parsing raw RFC822 bytes. Deliberately free of MimeKit types: this record is
/// the boundary that keeps <c>MimeMessage</c> inside <c>Parsing</c> (SPEC invariant 8).
/// </summary>
public sealed record ParsedMessage
{
    public MessageId? MessageId { get; init; }
    public IReadOnlyList<MessageId> References { get; init; } = [];
    public MessageId? InReplyTo { get; init; }

    public string? Subject { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Cc { get; init; }

    /// <summary>Present only on a locally-composed message; inbound mail never carries Bcc.</summary>
    public string? Bcc { get; init; }
    public string? ReplyTo { get; init; }
    public string? Sender { get; init; }

    /// <summary>Header Date, or INTERNALDATE when the header is missing or absurd (edge case 18).</summary>
    public DateTimeOffset DateUtc { get; init; }

    /// <summary>Plaintext extraction used for FTS. Never null; empty for a body-less message.</summary>
    public string BodyText { get; init; } = string.Empty;

    /// <summary>Raw HTML alternative, if present. Untrusted — the client sanitizes, never the daemon.</summary>
    public string? BodyHtml { get; init; }

    public IReadOnlyList<ParsedAttachment> Attachments { get; init; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Notes about anything non-conforming we recovered from; surfaced in diagnostics.</summary>
    public IReadOnlyList<string> ParseWarnings { get; init; } = [];
}

public sealed record ParsedAttachment
{
    public required int Index { get; init; }
    public required string MimeType { get; init; }
    public string? FileName { get; init; }
    public string? ContentId { get; init; }
    public long Size { get; init; }
    public bool IsInline { get; init; }
}

/// <summary>Recipients extracted for a send, already validated through <see cref="EmailAddress"/>.</summary>
public sealed record ParsedRecipients
{
    public required EmailAddress From { get; init; }
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];

    public IReadOnlyList<EmailAddress> AllRecipients() => [.. To, .. Cc, .. Bcc];
}
