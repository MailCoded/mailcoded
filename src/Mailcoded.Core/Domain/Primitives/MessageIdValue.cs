namespace Mailcoded.Core.Domain.Primitives;

/// <summary>
/// A normalized RFC 5322 Message-ID, stored without the angle brackets.
/// Load-bearing for outbox idempotency and Sent-folder de-duplication.
/// </summary>
public readonly record struct MessageId
{
    public string Value { get; }

    private MessageId(string value) => Value = value;

    public static bool TryParse(string? raw, out MessageId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim();
        if (s.StartsWith('<') && s.EndsWith('>')) s = s[1..^1];
        s = s.Trim();
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (c is '<' or '>' or '\r' or '\n' or ' ' or '\t') return false;
            if (char.IsControl(c)) return false;
        }

        var at = s.LastIndexOf('@');
        if (at > 0 && at < s.Length - 1)
            s = string.Concat(s.AsSpan(0, at + 1), s.AsSpan(at + 1).ToString().ToLowerInvariant());

        id = new MessageId(s);
        return true;
    }

    public static MessageId Parse(string raw) =>
        TryParse(raw, out var id) ? id : throw new FormatException($"Not a valid Message-ID: '{raw}'.");

    /// <summary>Deterministically mints a new Message-ID for an outgoing message.</summary>
    public static MessageId NewForDomain(string domain, Guid seed)
    {
        var d = string.IsNullOrWhiteSpace(domain) ? "mailcoded.local" : domain.Trim().ToLowerInvariant();
        return new MessageId($"{seed:N}@{d}");
    }

    /// <summary>The wire form, angle brackets included.</summary>
    public string ToHeaderValue() => $"<{Value}>";
    public override string ToString() => Value;
}
