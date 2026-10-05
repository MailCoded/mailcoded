namespace Mailcoded.Core.Domain.Correspondents;

/// <summary>Someone the user has written to, as learned from outgoing mail only.</summary>
public sealed record Correspondent
{
    public required string Address { get; init; }

    public string? DisplayName { get; init; }

    public required int Count { get; init; }

    public required DateTimeOffset LastUsedUtc { get; init; }
}
