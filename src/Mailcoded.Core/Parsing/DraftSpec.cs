using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// A message a client wants built. Every address field is a validated <see cref="EmailAddress"/>,
/// so header injection (edge case 16) is impossible by the time this record exists.
/// </summary>
public sealed record DraftSpec
{
    public required EmailAddress From { get; init; }
    public string? FromDisplayName { get; init; }
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];

    /// <summary>Sanitized at construction: CR/LF is rejected, not stripped.</summary>
    public required string Subject { get; init; }

    public required string BodyText { get; init; }

    /// <summary>Message-ID of the message being replied to, which becomes In-Reply-To.</summary>
    public MessageId? InReplyTo { get; init; }
    public IReadOnlyList<MessageId> References { get; init; } = [];

    /// <summary>Assigned by the outbox at creation, never at send time (RELIABILITY §14.4).</summary>
    public required MessageId MessageId { get; init; }

    public DateTimeOffset DateUtc { get; init; }
}
