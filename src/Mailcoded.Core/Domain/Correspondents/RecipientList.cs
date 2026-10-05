using System.Text;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Correspondents;

/// <summary>
/// Reads the address-list text a stored header holds (RFC 5322 3.4) into validated addresses and names.
/// The text is attacker-shaped, so anything that is not a whole, valid address is dropped rather than repaired.
/// </summary>
public static class RecipientList
{
    public const int MaxInputLength = 32_768;
    public const int MaxEntries = 256;
    public const int MaxDisplayNameLength = 100;

    public static IReadOnlyList<NamedAddress> Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return [];

        var text = header.Length > MaxInputLength ? header[..MaxInputLength] : header;
        var found = new List<NamedAddress>();
        var segment = new Segment();
        var quoted = false;
        var comment = 0;
        var inAngle = false;

        for (var i = 0; i < text.Length && found.Count < MaxEntries; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    segment.Append(text[i], inAngle, name: false);
                    segment.Append(text[++i], inAngle, name: true);
                }
                else
                {
                    if (c == '"') quoted = false;
                    segment.Append(c, inAngle, name: c != '"');
                }

                continue;
            }

            if (comment > 0)
            {
                if (c == '\\') i++;
                else if (c == '(') comment++;
                else if (c == ')') comment--;
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    segment.Append(c, inAngle, name: false);
                    break;

                case '(':
                    comment = 1;
                    break;

                case '<':
                    if (inAngle || segment.AngleCount > 0) segment.Broken = true;
                    inAngle = true;
                    segment.AngleCount++;
                    break;

                case '>':
                    if (!inAngle) segment.Broken = true;
                    inAngle = false;
                    break;

                case ',' or ';' when !inAngle:
                    Flush(segment, found);
                    segment = new Segment();
                    break;

                case ':' when !inAngle && segment.AngleCount == 0 && !segment.HasAt:
                    segment = new Segment();
                    break;

                default:
                    segment.Append(c, inAngle, name: true);
                    break;
            }
        }

        if (!quoted && comment == 0 && !inAngle) Flush(segment, found);

        return found;
    }

    /// <summary>One display name made fit to show: single spaces, no control or invisible characters, no address look-alike.</summary>
    public static string? CleanDisplayName(string? raw, EmailAddress? address = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;

        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c) || TextSafety.IsLineBreak(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(c) || TextSafety.IsInvisibleFormat(c)) continue;

            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);

            if (builder.Length > MaxDisplayNameLength) return null;
        }

        if (builder.Length == 0) return null;

        var name = builder.ToString();
        if (name.AsSpan().IndexOfAny('@', '<', '>') >= 0) return null;
        if (address is { } own && string.Equals(name, own.Value, StringComparison.OrdinalIgnoreCase)) return null;

        return name;
    }

    private static void Flush(Segment segment, List<NamedAddress> found)
    {
        if (segment.Broken) return;

        var addressText = segment.AngleCount > 0 ? segment.Angle.ToString().Trim() : segment.Raw.ToString().Trim();
        if (!EmailAddress.TryParse(addressText, out var address)) return;

        var name = segment.AngleCount > 0 ? CleanDisplayName(segment.Name.ToString(), address) : null;
        found.Add(new NamedAddress(address, name));
    }

    private sealed class Segment
    {
        public StringBuilder Raw { get; } = new();

        public StringBuilder Name { get; } = new();

        public StringBuilder Angle { get; } = new();

        public int AngleCount { get; set; }

        public bool Broken { get; set; }

        public bool HasAt { get; private set; }

        public void Append(char c, bool inAngle, bool name)
        {
            if (inAngle)
            {
                Angle.Append(c);
                return;
            }

            if (AngleCount > 0 && !char.IsWhiteSpace(c)) Broken = true;

            Raw.Append(c);
            if (c == '@') HasAt = true;
            if (name) Name.Append(c);
        }
    }
}
