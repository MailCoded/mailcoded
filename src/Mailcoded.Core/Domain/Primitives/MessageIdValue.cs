namespace Mailcoded.Core.Domain.Primitives;

/// <summary>A normalized RFC 5322 Message-ID, stored without the angle brackets. Load-bearing for
/// outbox idempotency and Sent-folder de-duplication.</summary>
public readonly record struct MessageId
{
    private const int MaxLength = 998;

    public string Value { get; }

    private MessageId(string value) => Value = value;

    public static bool TryParse(string? raw, out MessageId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim();
        if (s.StartsWith('<') && s.EndsWith('>')) s = s[1..^1];
        s = s.Trim();
        if (s.Length == 0 || s.Length > MaxLength) return false;
        foreach (var c in s)
        {
            if (c is '<' or '>' or '\r' or '\n' or ' ' or '\t') return false;
            if (char.IsControl(c)) return false;
        }

        if (!IsUsableMsgId(s)) return false;

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

    /// <summary>Rejects what an RFC 5322 msg-id parser cannot read back. A server passes through ids
    /// it never validated (<c>a@mail..example.com</c>); one that reaches a reply's In-Reply-To throws
    /// there instead, leaving the message permanently unreplyable.</summary>
    private static bool IsUsableMsgId(ReadOnlySpan<char> s)
    {
        var at = s.LastIndexOf('@');
        if (at < 0) return IsLocalPart(s);
        if (at == 0 || at == s.Length - 1) return false;

        return IsLocalPart(s[..at]) && IsDomain(s[(at + 1)..]);
    }

    private static bool IsLocalPart(ReadOnlySpan<char> s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"'
            ? IsQuotedString(s[1..^1])
            : IsDotAtom(s);

    private static bool IsDomain(ReadOnlySpan<char> s) =>
        s.Length >= 2 && s[0] == '[' && s[^1] == ']'
            ? IsDomainLiteral(s[1..^1])
            : IsDotAtom(s);

    private static bool IsDotAtom(ReadOnlySpan<char> s)
    {
        if (s.Length == 0 || s[0] == '.' || s[^1] == '.') return false;

        var afterDot = false;
        foreach (var c in s)
        {
            if (c == '.')
            {
                if (afterDot) return false;
                afterDot = true;
                continue;
            }

            if (!IsAtext(c)) return false;
            afterDot = false;
        }

        return true;
    }

    private static bool IsQuotedString(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (c is '"' or '\\')
                return false;

        return s.Length > 0;
    }

    private static bool IsDomainLiteral(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (c is '[' or ']' or '\\' || c > '~')
                return false;

        return s.Length > 0;
    }

    private static bool IsAtext(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c > '\u007F'
        || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '/' or '='
             or '?' or '^' or '_' or '`' or '{' or '|' or '}' or '~';
}
