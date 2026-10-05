namespace Mailcoded.Protocol;

/// <summary><c>correspondent.suggest</c> — people the Account's user has written to, best first.
/// Learned from outgoing mail only; received mail never adds anyone.</summary>
public sealed record CorrespondentSuggestParams
{
    public required long AccountId { get; init; }

    /// <summary>Matches an address, a display name, or any word of one, case-insensitively. Absent or empty lists the best overall.</summary>
    public string? Prefix { get; init; }

    /// <summary><c>1..100</c>, default 10.</summary>
    public int? Limit { get; init; }
}

public sealed record CorrespondentSuggestResult
{
    public IReadOnlyList<CorrespondentDto> Correspondents { get; init; } = [];
}

public sealed record CorrespondentDto
{
    /// <summary>The whole address, lower-cased.</summary>
    public required string Address { get; init; }

    /// <summary>The name on the most recent message that carried one. Untrusted text: render as plain text.</summary>
    public string? DisplayName { get; init; }

    /// <summary>How many outgoing messages named this address in To or Cc.</summary>
    public required int Count { get; init; }

    public required string LastUsedUtc { get; init; }
}
