using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Tags;

/// <summary>A set of server flag/keyword changes to push to IMAP. Empty means "nothing to do".</summary>
public sealed record FlagDelta
{
    public MessageFlags Add { get; init; } = MessageFlags.None;
    public MessageFlags Remove { get; init; } = MessageFlags.None;
    public IReadOnlyList<string> AddKeywords { get; init; } = [];
    public IReadOnlyList<string> RemoveKeywords { get; init; } = [];

    public static readonly FlagDelta Empty = new();

    public bool IsEmpty =>
        Add == MessageFlags.None && Remove == MessageFlags.None
        && AddKeywords.Count == 0 && RemoveKeywords.Count == 0;
}

/// <summary>A set of local tag changes requested by a client, already validated into <see cref="Tag"/>s.</summary>
public sealed record TagDelta
{
    public IReadOnlyList<Tag> Add { get; init; } = [];
    public IReadOnlyList<Tag> Remove { get; init; } = [];

    public static readonly TagDelta Empty = new();
    public bool IsEmpty => Add.Count == 0 && Remove.Count == 0;
}
