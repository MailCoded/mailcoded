namespace Mailcoded.Core.Domain.Primitives;

/// <summary>
/// An RFC-shaped addr-spec with the domain lower-cased. This type is the CR/LF header-injection
/// gate at compose time: a value of this type can never carry a line break into a header.
/// </summary>
public readonly record struct EmailAddress
{
    public string Value { get; }
    private EmailAddress(string value) => Value = value;

    public string LocalPart => Value[..Value.IndexOf('@')];
    public string Domain => Value[(Value.IndexOf('@') + 1)..];

    public static bool TryParse(string? raw, out EmailAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim();
        if (s.Length > 320) return false;

        foreach (var c in s)
            if (c is '\r' or '\n' or '\0' || char.IsControl(c)) return false;

        var at = s.LastIndexOf('@');
        if (at <= 0 || at == s.Length - 1) return false;

        var local = s[..at];
        var domain = s[(at + 1)..];
        if (local.Length > 64) return false;
        if (domain.Length == 0 || domain.Length > 255) return false;
        if (domain.IndexOf('.') < 0) return false;
        if (domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains("..", StringComparison.Ordinal)) return false;
        if (domain.StartsWith('-') || domain.EndsWith('-')) return false;

        foreach (var c in domain)
            if (c is not ('-' or '.') && !char.IsLetterOrDigit(c)) return false;

        if (local.Contains(' ') || local.Contains(',') || local.Contains(';')) return false;

        address = new EmailAddress(string.Concat(local, "@", domain.ToLowerInvariant()));
        return true;
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
