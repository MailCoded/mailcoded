using System.Globalization;
using System.Text;

namespace Mailcoded.Tui.Render;

/// <summary>The only route from untrusted mail text to a SafeSpan. Allowlists scalars; never
/// parses escape sequences, because a stripper is a parser of the attacker's grammar.</summary>
public static class TerminalText
{
    public const char Replacement = '�';

    private const int MaxScan = 128 * 1024;
    private const int MaxClusterRunes = 8;
    private const int TabStop = 8;

    public static SafeSpan Cell(string? value, int columns)
    {
        if (columns <= 0 || string.IsNullOrEmpty(value)) return SafeSpan.Empty;

        var builder = new StringBuilder(Math.Min(columns * 2, 512));
        var width = 0;
        var pendingSpace = false;
        var first = true;

        foreach (var cluster in Clusters(value))
        {
            if (IsWhitespaceCluster(cluster))
            {
                pendingSpace = !first;
                continue;
            }

            var safe = Sanitise(cluster);
            var advance = DisplayWidth.OfCluster(safe) + (pendingSpace ? 1 : 0);
            if (width + advance > columns) return new SafeSpan(builder.ToString(), width);

            if (pendingSpace) builder.Append(' ');
            builder.Append(safe);
            width += advance;
            pendingSpace = false;
            first = false;
        }

        return new SafeSpan(builder.ToString(), width);
    }

    public static IReadOnlyList<SafeSpan> Wrap(string? value, int columns, int maxLines)
    {
        var lines = new List<SafeSpan>();
        if (columns <= 0 || maxLines <= 0 || string.IsNullOrEmpty(value)) return lines;

        var builder = new StringBuilder(columns * 2);
        var width = 0;
        var wordStart = -1;
        var wordWidth = 0;

        void Break()
        {
            var trimmed = TrimEnd(builder, ref width);
            lines.Add(new SafeSpan(trimmed, width));
            builder.Clear();
            width = 0;
            wordStart = -1;
            wordWidth = 0;
        }

        foreach (var cluster in Clusters(value))
        {
            if (lines.Count >= maxLines) return lines;

            if (cluster is "\n")
            {
                Break();
                continue;
            }

            if (IsWhitespaceCluster(cluster))
            {
                var spaces = cluster is "\t" ? TabStop - (width % TabStop) : 1;
                if (width == 0) continue;
                if (width + spaces > columns) { Break(); continue; }

                builder.Append(' ', spaces);
                width += spaces;
                wordStart = -1;
                wordWidth = 0;
                continue;
            }

            var safe = Sanitise(cluster);
            var advance = DisplayWidth.OfCluster(safe);

            if (width + advance > columns)
            {
                if (wordStart > 0 && wordWidth + advance <= columns)
                {
                    var carried = builder.ToString(wordStart, builder.Length - wordStart);
                    builder.Length = wordStart;
                    width -= wordWidth;
                    Break();
                    if (lines.Count >= maxLines) return lines;

                    builder.Append(carried);
                    width = wordWidth;
                    wordStart = 0;
                }
                else
                {
                    Break();
                    if (lines.Count >= maxLines) return lines;
                }
            }

            if (wordStart < 0)
            {
                wordStart = builder.Length;
                wordWidth = 0;
            }

            builder.Append(safe);
            width += advance;
            wordWidth += advance;
        }

        if (builder.Length > 0 && lines.Count < maxLines)
        {
            var trimmed = TrimEnd(builder, ref width);
            if (trimmed.Length > 0) lines.Add(new SafeSpan(trimmed, width));
        }

        return lines;
    }

    public static SafeSpan Clip(SafeSpan span, int columns)
    {
        if (columns <= 0) return SafeSpan.Empty;
        if (span.Columns <= columns) return span;

        var width = 0;
        var taken = 0;
        foreach (var cluster in Clusters(span.Text))
        {
            var advance = DisplayWidth.OfCluster(cluster);
            if (width + advance > columns) break;
            width += advance;
            taken += cluster.Length;
        }

        return new SafeSpan(span.Text[..taken], width);
    }

    /// <summary>Chrome keeps its own spacing: unlike Cell it does not collapse runs, because a
    /// column layout authored here is not attacker input and its padding is load-bearing.</summary>
    public static SafeSpan Chrome(string literal, int columns)
    {
        ArgumentNullException.ThrowIfNull(literal);

        foreach (var c in literal)
        {
            if (c is < ' ' or > '~')
                throw new ArgumentException($"Chrome takes printable ASCII; U+{(int)c:X4} is not.", nameof(literal));
        }

        var width = Math.Min(literal.Length, Math.Max(columns, 0));
        return new SafeSpan(literal[..width], width);
    }

    public static SafeSpan Concat(params ReadOnlySpan<SafeSpan> parts)
    {
        var builder = new StringBuilder();
        var width = 0;

        foreach (var part in parts)
        {
            builder.Append(part.Text);
            width += part.Columns;
        }

        return new SafeSpan(builder.ToString(), width);
    }

    public static SafeSpan Pad(SafeSpan span, int columns)
    {
        var clipped = Clip(span, columns);
        return clipped.Columns >= columns
            ? clipped
            : new SafeSpan(clipped.Text + new string(' ', columns - clipped.Columns), columns);
    }

    private static string TrimEnd(StringBuilder builder, ref int width)
    {
        var end = builder.Length;
        while (end > 0 && builder[end - 1] == ' ') { end--; width--; }
        return builder.ToString(0, end);
    }

    internal static IEnumerable<string> Clusters(string value)
    {
        var limit = Math.Min(value.Length, MaxScan);
        var index = 0;

        while (index < limit)
        {
            if (value[index] == '\r')
            {
                index += index + 1 < limit && value[index + 1] == '\n' ? 2 : 1;
                yield return "\n";
                continue;
            }

            var length = StringInfo.GetNextTextElementLength(value.AsSpan(index, limit - index));
            if (length <= 0) length = 1;
            yield return value.Substring(index, length);
            index += length;
        }
    }

    private static bool IsWhitespaceCluster(string cluster)
    {
        if (cluster.Length == 1 && cluster[0] is ' ' or '\t' or '\n') return true;
        return Rune.TryGetRuneAt(cluster, 0, out var rune) && cluster.Length == rune.Utf16SequenceLength
            && Rune.IsWhiteSpace(rune);
    }

    private static string Sanitise(string cluster)
    {
        var runes = 0;
        var index = 0;

        while (index < cluster.Length)
        {
            if (!Rune.TryGetRuneAt(cluster, index, out var rune)) return Replacement.ToString();
            if (++runes > MaxClusterRunes) return Replacement.ToString();

            var category = Rune.GetUnicodeCategory(rune);
            var allowed = category is not (UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator) && rune.Value != 0x00AD;

            if (!allowed) return Replacement.ToString();

            // A cluster opening on a mark composes onto whatever chrome precedes it.
            if (index == 0 && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.SpacingCombiningMark)
            {
                return Replacement.ToString();
            }

            index += rune.Utf16SequenceLength;
        }

        return runes == 0 ? Replacement.ToString() : cluster;
    }
}
