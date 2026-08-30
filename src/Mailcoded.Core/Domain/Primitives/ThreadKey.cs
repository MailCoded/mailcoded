namespace Mailcoded.Core.Domain.Primitives;

/// <summary>Stable grouping key for a conversation. Derivation rules live in Domain/Threading.</summary>
public readonly record struct ThreadKey
{
    public string Value { get; }
    private ThreadKey(string value) => Value = value;

    public static bool TryCreate(string? raw, out ThreadKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        key = new ThreadKey(raw.Trim());
        return true;
    }

    public static ThreadKey Create(string raw) =>
        TryCreate(raw, out var k) ? k : throw new ArgumentException("Thread key must be non-empty.", nameof(raw));

    public override string ToString() => Value;
}
