using System.Text;

namespace Mailcoded.Core.Store;

/// <summary>
/// Query-side text handling for FTS5. Two jobs: pick the index per PERFORMANCE §15.3, and turn
/// attacker-influenced free text into an FTS5 expression that can never be a syntax error or an
/// injected operator.
/// </summary>
internal static class SearchText
{
    /// <summary>The trigram tokenizer cannot match fewer than three characters.</summary>
    public const int TrigramFloor = 3;

    public static SearchRoute Route(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return SearchRoute.None;

        var hasCjk = false;
        var hasOther = false;
        var run = 0;
        var longestRun = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune.Value))
            {
                hasCjk = true;
                run++;
                if (run > longestRun) longestRun = run;
            }
            else
            {
                run = 0;
                if (!Rune.IsWhiteSpace(rune)) hasOther = true;
            }
        }

        if (!hasCjk) return hasOther ? SearchRoute.Fts : SearchRoute.None;
        if (longestRun >= TrigramFloor) return SearchRoute.Cjk;

        // 1-2 CJK runes are below the trigram floor: LIKE is the only correct answer (edge case 17).
        return SearchRoute.Like;
    }

    private static bool IsCjk(int cp) =>
        (cp >= 0x2E80 && cp <= 0x2FFF)      // radicals, Kangxi
        || (cp >= 0x3040 && cp <= 0x30FF)   // Hiragana, Katakana
        || (cp >= 0x3130 && cp <= 0x318F)   // Hangul compatibility jamo
        || (cp >= 0x3400 && cp <= 0x4DBF)   // CJK extension A
        || (cp >= 0x4E00 && cp <= 0x9FFF)   // CJK unified ideographs
        || (cp >= 0xA960 && cp <= 0xA97F)   // Hangul jamo extended-A
        || (cp >= 0xAC00 && cp <= 0xD7AF)   // Hangul syllables
        || (cp >= 0xF900 && cp <= 0xFAFF)   // CJK compatibility ideographs
        || (cp >= 0xFF66 && cp <= 0xFF9F)   // halfwidth Katakana
        || (cp >= 0x20000 && cp <= 0x3FFFF);// CJK extensions B and beyond

    /// <summary>
    /// Every token becomes a quoted FTS5 string, so no character in a subject line can ever be
    /// read as an operator. A trailing '*' is preserved as a prefix query.
    /// </summary>
    public static string ToMatchExpression(string text)
    {
        var builder = new StringBuilder(text.Length + 8);

        foreach (var range in Tokenize(text))
        {
            var token = text.AsSpan(range.Start, range.Length);
            var prefix = token.Length > 1 && token[^1] == '*';
            if (prefix) token = token[..^1];
            if (token.Length == 0) continue;

            if (builder.Length > 0) builder.Append(' ');
            builder.Append('"');
            foreach (var c in token)
            {
                if (c == '"') builder.Append('"');
                builder.Append(c);
            }
            builder.Append('"');
            if (prefix) builder.Append('*');
        }

        return builder.ToString();
    }

    private static List<(int Start, int Length)> Tokenize(string text)
    {
        var tokens = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (start >= 0) { tokens.Add((start, i - start)); start = -1; }
            }
            else if (start < 0)
            {
                start = i;
            }
        }
        if (start >= 0) tokens.Add((start, text.Length - start));
        return tokens;
    }

    /// <summary>Builds a LIKE pattern with the wildcard characters neutralized.</summary>
    public static string ToLikePattern(string text)
    {
        var builder = new StringBuilder(text.Length + 4);
        builder.Append('%');
        foreach (var c in text.Trim())
        {
            if (c is '%' or '_' or '\\') builder.Append('\\');
            builder.Append(c);
        }
        builder.Append('%');
        return builder.ToString();
    }
}
