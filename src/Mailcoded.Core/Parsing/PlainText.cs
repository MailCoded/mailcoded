using System.Text;

namespace Mailcoded.Core.Parsing;

/// <summary>Whitespace and control-character normalization for text destined for FTS or a header column.</summary>
public static class PlainText
{
    /// <summary>
    /// Collapses whitespace, normalizes line endings, and removes control, zero-width and
    /// bidirectional-override characters. Bounded: output never exceeds <paramref name="maxChars"/>.
    /// </summary>
    public static string Normalize(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0) return string.Empty;

        var sb = new StringBuilder(Math.Min(text.Length, maxChars));
        var pendingNewlines = 0;
        var pendingSpace = false;
        var wroteAny = false;

        for (var i = 0; i < text.Length && sb.Length < maxChars; i++)
        {
            var c = text[i];

            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                c = '\n';
            }

            if (c == '\n')
            {
                if (wroteAny && pendingNewlines < 2) pendingNewlines++;
                pendingSpace = false;
                continue;
            }

            if (IsInvisible(c)) continue;

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (wroteAny) pendingSpace = true;
                continue;
            }

            while (pendingNewlines > 0 && sb.Length < maxChars)
            {
                sb.Append('\n');
                pendingNewlines--;
            }

            if (pendingNewlines == 0 && pendingSpace && sb.Length < maxChars) sb.Append(' ');
            pendingSpace = false;

            if (sb.Length >= maxChars) break;
            sb.Append(c);
            wroteAny = true;
        }

        TrimDanglingSurrogate(sb);
        return sb.ToString();
    }

    /// <summary>Single-line form for a header value: no line breaks at all.</summary>
    public static string? NormalizeHeader(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 0) return null;

        var sb = new StringBuilder(Math.Min(text.Length, maxChars));
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (sb.Length >= maxChars) break;
            if (IsInvisible(c)) continue;

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
                if (sb.Length >= maxChars) break;
            }

            sb.Append(c);
        }

        TrimDanglingSurrogate(sb);
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static void TrimDanglingSurrogate(StringBuilder sb)
    {
        if (sb.Length > 0 && char.IsHighSurrogate(sb[sb.Length - 1])) sb.Length--;
    }

    /// <summary>Zero-width, bidi-override and byte-order marks: invisible spoofing material, never content.</summary>
    internal static bool IsInvisible(char c) =>
        c is '\u00AD' or '\uFEFF'
        || (c >= '\u200B' && c <= '\u200F')
        || (c >= '\u202A' && c <= '\u202E')
        || (c >= '\u2060' && c <= '\u2064')
        || (c >= '\u2066' && c <= '\u2069');
}
