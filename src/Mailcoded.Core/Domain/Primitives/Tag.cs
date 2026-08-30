namespace Mailcoded.Core.Domain.Primitives;

/// <summary>
/// A local, notmuch-style marker owned by this machine. Distinct from a server <see cref="MessageFlags">Flag</see> —
/// the vocabulary split is normative (SPEC §12.6): never call a Tag a "label".
/// </summary>
public readonly record struct Tag
{
    public string Value { get; }
    private Tag(string value) => Value = value;

    // Reserved tags that mirror IMAP system flags. Round-tripped by TagFlagMap.
    public static readonly Tag Unread = new("unread");
    public static readonly Tag Flagged = new("flagged");
    public static readonly Tag Replied = new("replied");
    public static readonly Tag Draft = new("draft");
    public static readonly Tag Inbox = new("inbox");

    public bool IsSystemTag =>
        Value is "unread" or "flagged" or "replied" or "draft";

    /// <summary>
    /// notmuch-safe charset: printable ASCII minus whitespace and the query metacharacters
    /// that would otherwise need quoting in a search expression.
    /// </summary>
    public static bool TryParse(string? raw, out Tag tag)
    {
        tag = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim();
        if (s.Length > 128) return false;

        foreach (var c in s)
        {
            if (c <= 0x20 || c >= 0x7f) return false;
            if (c is '"' or '\'' or '(' or ')' or '*' or ':' or ';' or ',' or '\\' or '/') return false;
        }

        tag = new Tag(s.ToLowerInvariant());
        return true;
    }

    public static Tag Parse(string raw) =>
        TryParse(raw, out var t) ? t : throw new FormatException($"Not a valid tag: '{raw}'.");

    public override string ToString() => Value;
}
