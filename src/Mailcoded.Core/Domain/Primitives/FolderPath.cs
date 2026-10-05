namespace Mailcoded.Core.Domain.Primitives;

/// <summary>
/// A server folder path with a normalized hierarchy delimiter ('/'), preserving case.
/// Comparison is ordinal everywhere except INBOX, which RFC 3501 defines as case-insensitive.
/// </summary>
public readonly record struct FolderPath
{
    public string Value { get; }
    public char Delimiter { get; }

    private FolderPath(string value, char delimiter)
    {
        Value = value;
        Delimiter = delimiter;
    }

    public const string Inbox = "INBOX";

    public static bool TryCreate(string? raw, char delimiter, out FolderPath path)
    {
        path = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim();
        foreach (var c in s)
            if (c is '\r' or '\n' or '\0') return false;

        if (!IsPlausibleDelimiter(delimiter)) return false;

        // A NIL delimiter (flat namespace) is legal; leave the path untouched in that case.
        if (delimiter != '\0' && delimiter != '/')
        {
            // A literal '/' inside a segment cannot survive normalization to '/', and the server
            // name could not be rebuilt from it.
            if (s.Contains('/')) return false;
            s = s.Replace(delimiter, '/');
        }

        s = s.Trim('/');
        if (s.Length == 0) return false;

        if (string.Equals(s, Inbox, StringComparison.OrdinalIgnoreCase)) s = Inbox;

        path = new FolderPath(s, delimiter);
        return true;
    }

    /// <summary>Rebuilds a path from a value this type already normalized, with the delimiter it came with.</summary>
    public static bool TryCreateNormalized(string? value, char delimiter, out FolderPath path)
    {
        path = default;
        if (string.IsNullOrWhiteSpace(value) || !IsPlausibleDelimiter(delimiter)) return false;

        var s = value.Trim();
        foreach (var c in s)
            if (c is '\r' or '\n' or '\0') return false;

        if (delimiter != '\0') s = s.Trim('/');
        if (s.Length == 0) return false;

        if (string.Equals(s, Inbox, StringComparison.OrdinalIgnoreCase)) s = Inbox;

        path = new FolderPath(s, delimiter);
        return true;
    }

    /// <summary>NIL (<c>'\0'</c>) or a printable ASCII character other than a space; anything else is a server bug.</summary>
    public static bool IsPlausibleDelimiter(char delimiter) => delimiter == '\0' || delimiter is > ' ' and < '\u007f';

    /// <summary>The delimiter as stored: empty for a flat namespace, otherwise the single character.</summary>
    public string DelimiterText => Delimiter == '\0' ? string.Empty : Delimiter.ToString();

    /// <summary>Inverse of <see cref="DelimiterText"/>; null (not listed yet) reads as '/'.</summary>
    public static char DelimiterFromStored(string? text)
    {
        if (text is null) return '/';
        if (text.Length == 0) return '\0';
        return text.Length == 1 && IsPlausibleDelimiter(text[0]) ? text[0] : '/';
    }

    public static FolderPath Create(string raw, char delimiter = '/') =>
        TryCreate(raw, delimiter, out var p) ? p : throw new ArgumentException($"Not a valid folder path: '{raw}'.", nameof(raw));

    /// <summary>The name to send the server: <see cref="Value"/> with the server's own delimiter back in.</summary>
    public string ServerName => Delimiter is '/' or '\0' ? Value : Value.Replace('/', Delimiter);

    /// <summary>True when the server has no hierarchy delimiter, so no name is a child of another.</summary>
    public bool IsFlat => Delimiter == '\0';

    public bool IsInbox => string.Equals(Value, Inbox, StringComparison.OrdinalIgnoreCase);

    public string LeafName
    {
        get
        {
            if (IsFlat) return Value;

            var i = Value.LastIndexOf('/');
            return i < 0 ? Value : Value[(i + 1)..];
        }
    }

    /// <summary>
    /// Case-sensitive except for INBOX (RFC 3501) — and case-insensitive throughout on Windows,
    /// where the store path derived from a folder name would otherwise collide.
    /// </summary>
    public bool NameEquals(FolderPath other) =>
        (IsInbox && other.IsInbox)
        || string.Equals(Value, other.Value,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Non-empty levels, at most <see cref="MaxSegments"/>: a hostile name keeps its tail in the last one.</summary>
    public string[] Segments() =>
        IsFlat ? [Value] : Value.Split('/', MaxSegments, StringSplitOptions.RemoveEmptyEntries);

    public const int MaxSegments = 32;

    public override string ToString() => Value;
}
