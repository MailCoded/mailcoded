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

        // A NIL delimiter (flat namespace) is legal; leave the path untouched in that case.
        if (delimiter != '\0' && delimiter != '/')
            s = s.Replace(delimiter, '/');

        s = s.Trim('/');
        if (s.Length == 0) return false;

        if (string.Equals(s, Inbox, StringComparison.OrdinalIgnoreCase)) s = Inbox;

        path = new FolderPath(s, delimiter);
        return true;
    }

    public static FolderPath Create(string raw, char delimiter = '/') =>
        TryCreate(raw, delimiter, out var p) ? p : throw new ArgumentException($"Not a valid folder path: '{raw}'.", nameof(raw));

    public bool IsInbox => string.Equals(Value, Inbox, StringComparison.OrdinalIgnoreCase);

    public string LeafName
    {
        get
        {
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

    public override string ToString() => Value;
}
