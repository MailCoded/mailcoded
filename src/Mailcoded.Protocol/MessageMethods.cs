namespace Mailcoded.Protocol;

/// <summary><c>search</c> — FTS5 query with keyset paging. Always pass a limit.</summary>
public sealed record SearchParams
{
    /// <summary>Query in the <c>from:/to:/subject:/tag:/is:/before:/after:</c> syntax. Untrusted text.</summary>
    public required string Query { get; init; }

    public int? Limit { get; init; }

    /// <summary>Opaque keyset cursor from a previous <see cref="SearchResult.NextCursor"/>.</summary>
    public string? Cursor { get; init; }

    public long? AccountId { get; init; }

    public long? FolderId { get; init; }

    /// <summary><c>relevance|date</c>. Defaults to relevance for text queries.</summary>
    public string? Order { get; init; }

    public bool IncludeSnippet { get; init; } = true;
}

public sealed record SearchResult
{
    public IReadOnlyList<EnvelopeDto> Hits { get; init; } = [];

    public string? NextCursor { get; init; }

    /// <summary>True when results were cut short; follow <see cref="NextCursor"/> rather than assuming completeness.</summary>
    public bool Truncated { get; init; }
}

/// <summary><c>thread.get</c> — every message sharing a thread key, oldest first.</summary>
public sealed record ThreadGetParams
{
    public required string ThreadKey { get; init; }

    public int? Limit { get; init; }
}

public sealed record ThreadGetResult
{
    public IReadOnlyList<EnvelopeDto> Messages { get; init; } = [];

    public bool Truncated { get; init; }
}

/// <summary><c>message.get</c> — envelope plus body. Fetches from the server when the body is not local.</summary>
public sealed record MessageGetParams
{
    public required long MessageId { get; init; }

    /// <summary><c>text|html|raw</c>.</summary>
    public string Format { get; init; } = MessageFormats.Text;

    public bool FetchIfMissing { get; init; } = true;
}

public sealed record MessageGetResult
{
    public required EnvelopeDto Envelope { get; init; }

    /// <summary>Plaintext extraction, present for format <c>text</c> and <c>html</c>.</summary>
    public string? BodyText { get; init; }

    /// <summary>RAW, UNSANITIZED attacker-controlled HTML — the client sanitizes before rendering, never the daemon.</summary>
    public string? BodyHtml { get; init; }

    /// <summary>Base64 RFC822 bytes, present only for format <c>raw</c>.</summary>
    public string? Raw { get; init; }

    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];

    public bool BodyFetched { get; init; }

    /// <summary>Stable parser warning slugs, e.g. <c>missing-date</c>. Never echo mail content.</summary>
    public IReadOnlyList<string> ParseWarnings { get; init; } = [];
}

/// <summary><c>attachment.get</c> — one attachment's bytes, base64 encoded.</summary>
public sealed record AttachmentGetParams
{
    public required long MessageId { get; init; }

    public required int Index { get; init; }
}

public sealed record AttachmentGetResult
{
    public required string Filename { get; init; }

    public required string Mime { get; init; }

    /// <summary>Decoded attachment bytes, base64 encoded. Size is the decoded length.</summary>
    public required string Base64 { get; init; }

    public long Size { get; init; }
}

/// <summary><c>tags.set</c> — apply a tag delta; the server flag projection follows in the same transaction.</summary>
public sealed record TagsSetParams
{
    public required long MessageId { get; init; }

    public IReadOnlyList<string> Add { get; init; } = [];

    public IReadOnlyList<string> Remove { get; init; } = [];
}

public sealed record TagsSetResult
{
    /// <summary>The message's complete tag set after the delta, sorted ordinal.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary><c>message.move</c> — move to another folder. There is no delete or expunge method.</summary>
public sealed record MessageMoveParams
{
    public required long MessageId { get; init; }

    public required long ToFolderId { get; init; }
}

public sealed record MessageMoveResult
{
    public static readonly MessageMoveResult Instance = new();
}
