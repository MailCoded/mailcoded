namespace Mailcoded.Core.Domain.Primitives;

/// <summary>
/// An RFC-shaped addr-spec with the domain lower-cased. This type is the CR/LF header-injection
/// gate at compose time: a value of this type can never carry a line break into a header.
/// </summary>
public readonly record struct EmailAddress
{
    /// <summary>RFC 5321 §4.5.3.1: the whole path, plus the two part limits it is not the sum of.</summary>
    public const int MaxLength = 254;

    public const int MaxLocalPartLength = 64;
    public const int MaxDomainLength = 255;

    public string Value { get; }
    private EmailAddress(string value) => Value = value;

    public string LocalPart => Value[..Value.LastIndexOf('@')];
    public string Domain => Value[(Value.LastIndexOf('@') + 1)..];

    public static bool TryParse(string? raw, out EmailAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim();
        if (s.Length > MaxLength) return false;

        foreach (var c in s)
            if (c is '\r' or '\n' or '\0' || char.IsControl(c)) return false;

        var at = SingleUnquotedAt(s);
        if (at <= 0 || at == s.Length - 1) return false;

        var local = s[..at];
        var domain = s[(at + 1)..];
        if (local.Length > MaxLocalPartLength) return false;
        if (domain.Length == 0 || domain.Length > MaxDomainLength) return false;
        if (domain.IndexOf('.') < 0) return false;
        if (domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains("..", StringComparison.Ordinal)) return false;
        if (domain.StartsWith('-') || domain.EndsWith('-')) return false;

        foreach (var c in domain)
            if (c is not ('-' or '.') && !char.IsLetterOrDigit(c)) return false;

        if (local.Contains(' ') || local.Contains(',') || local.Contains(';')) return false;

        address = new EmailAddress(string.Concat(local, "@", domain.ToLowerInvariant()));
        return true;
    }

    /// <summary>The index of the one separator, or -1 when the string carries none or several.</summary>
    private static int SingleUnquotedAt(string s)
    {
        var at = -1;
        var quoted = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quoted && c == '\\')
            {
                i++;
                continue;
            }

            if (c == '"') quoted = !quoted;
            else if (c == '@' && !quoted)
            {
                if (at >= 0) return -1;
                at = i;
            }
        }

        return quoted ? -1 : at;
    }

    public static EmailAddress Parse(string raw) =>
        TryParse(raw, out var a) ? a : throw new FormatException($"Not a valid email address: '{raw}'.");

    /// <summary>True when the address needs SMTPUTF8 (any non-ASCII octet).</summary>
    public bool RequiresSmtpUtf8()
    {
        foreach (var c in Value) if (c > 127) return true;
        return false;
    }

    public override string ToString() => Value;
}
