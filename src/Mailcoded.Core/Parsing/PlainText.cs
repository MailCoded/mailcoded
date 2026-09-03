using System.Globalization;
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
        Rune.TryCreate(c, out var rune) && IsInvisible(rune);

    /// <summary>
    /// Category-based rather than a range list, so the astral tag block U+E0000-U+E007F is covered.
    /// A char-typed predicate structurally cannot see it, which is how invisible text smuggles.
    /// </summary>
    public static bool IsInvisible(Rune rune) =>
        rune.Value == 0x00AD || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;

    /// <summary>False for every scalar that must never occupy a terminal cell.</summary>
    public static bool IsRenderable(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => false,
        _ => rune.Value != 0x00AD,
    };
}
