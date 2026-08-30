using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Threading;

/// <summary>
/// Derives the conversation key for a message. A port because v0.2 may thread natively
/// (Gmail X-GM-THRID, Graph conversationId) rather than from References.
/// </summary>
public interface IThreader
{
    /// <summary>
    /// Resolves a thread key. <paramref name="lookup"/> answers "do we already know a thread for
    /// this Message-ID?" so the threader stays pure — it never touches the store itself.
    /// </summary>
    ThreadKey Resolve(ThreadCandidate candidate, Func<MessageId, ThreadKey?> lookup);
}

/// <summary>The threading inputs for one message, free of any storage or MIME type.</summary>
public sealed record ThreadCandidate
{
    public MessageId? MessageId { get; init; }
    public IReadOnlyList<MessageId> References { get; init; } = [];
    public MessageId? InReplyTo { get; init; }
    public string? Subject { get; init; }
    public string? FromAddress { get; init; }
    public DateTimeOffset DateUtc { get; init; }

    /// <summary>Server-supplied thread identity, when the provider has one (Gmail X-GM-THRID).</summary>
    public string? NativeThreadId { get; init; }
}
