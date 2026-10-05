namespace Mailcoded.Core.Domain.Signals;

/// <summary>The authserv-ids an Account trusts to have written Authentication-Results: exact hostnames or <c>*.suffix</c>.</summary>
public sealed class AuthservTrust
{
    public const int MaxPatterns = 16;
    public const int MaxPatternLength = 253;

    private readonly string[] _exact;
    private readonly string[] _suffixes;

    private AuthservTrust(string[] patterns)
    {
        Patterns = patterns;
        _exact = [.. patterns.Where(p => p[0] != '*')];
        _suffixes = [.. patterns.Where(p => p[0] == '*').Select(p => p[1..])];
    }

    public static AuthservTrust None { get; } = new([]);

    public IReadOnlyList<string> Patterns { get; }

    public bool IsEmpty => Patterns.Count == 0;

    public static AuthservTrust From(IEnumerable<string>? patterns)
    {
        if (patterns is null) return None;

        var kept = new List<string>();
        foreach (var raw in patterns)
        {
            if (kept.Count >= MaxPatterns) break;
            if (TryNormalize(raw, out var pattern) && !kept.Contains(pattern)) kept.Add(pattern);
        }

        return kept.Count == 0 ? None : new AuthservTrust([.. kept]);
    }

    public AuthservTrust Union(AuthservTrust other) => From(Patterns.Concat(other.Patterns));

    public static bool TryNormalize(string? raw, out string pattern)
    {
        pattern = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (text.Length == 0 || text.Length > MaxPatternLength) return false;

        var wildcard = text.StartsWith("*.", StringComparison.Ordinal);
        var host = wildcard ? text[2..] : text;

        if (!IsHostname(host)) return false;
        if (wildcard && host.IndexOf('.') < 0) return false;

        pattern = text;
        return true;
    }

    public bool Matches(string? authservId)
    {
        if (IsEmpty || authservId is null) return false;

        var id = authservId.Trim().TrimEnd('.').ToLowerInvariant();
        if (id.Length == 0 || !IsHostname(id)) return false;

        foreach (var exact in _exact)
            if (id == exact) return true;

        foreach (var suffix in _suffixes)
            if (id.Length > suffix.Length && id.EndsWith(suffix, StringComparison.Ordinal)) return true;

        return false;
    }

    private static bool IsHostname(string text)
    {
        if (text.Length == 0 || text[0] == '.' || text[^1] == '.') return false;

        var previousDot = false;
        foreach (var c in text)
        {
            var dot = c == '.';
            if (dot && previousDot) return false;
            previousDot = dot;

            var ok = dot || c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return false;
        }

        return true;
    }
}
