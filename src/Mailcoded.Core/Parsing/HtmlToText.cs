using System.Text;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// Tolerant HTML-to-text reduction for FTS when a message carries no text/plain alternative.
/// It only ever copies characters: nothing is executed, resolved, or fetched. Every scan advances
/// the cursor monotonically, so malformed input terminates in O(n).
/// </summary>
public static class HtmlToText
{
    public static string Convert(string? html, int maxChars)
    {
        if (string.IsNullOrEmpty(html) || maxChars <= 0) return string.Empty;

        var budget = (long)maxChars * 2 + 4096;
        var sb = new StringBuilder(Math.Min(html.Length, maxChars) + 16);
        var i = 0;
        var n = html.Length;

        while (i < n && sb.Length < budget)
        {
            var c = html[i];

            if (c == '&')
            {
                i = AppendEntity(html, i, sb);
                continue;
            }

            if (c != '<')
            {
                sb.Append(c);
                i++;
                continue;
            }

            var j = i + 1;

            if (j < n && html[j] == '!')
            {
                if (j + 2 < n && html[j + 1] == '-' && html[j + 2] == '-')
                {
                    var end = html.IndexOf("-->", j + 3, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                }
                else
                {
                    var end = html.IndexOf('>', j);
                    i = end < 0 ? n : end + 1;
                }

                continue;
            }

            if (j < n && html[j] == '?')
            {
                var end = html.IndexOf('>', j);
                i = end < 0 ? n : end + 1;
                continue;
            }

            var closing = j < n && html[j] == '/';
            if (closing) j++;

            // HTML5 tag-open: only ASCII alpha starts a tag name, so '<3' is text a reader sees and
            // dropping it here would hide body content from FTS that the HTML view still shows.
            if (j >= n || !char.IsAsciiLetter(html[j]))
            {
                if (closing)
                {
                    var bogus = html.IndexOf('>', j);
                    i = bogus < 0 ? n : bogus + 1;
                }
                else
                {
                    sb.Append('<');
                    i++;
                }

                continue;
            }

            var nameStart = j;
            while (j < n && (char.IsAsciiLetterOrDigit(html[j]) || html[j] == '-' || html[j] == ':')) j++;

            var name = html.AsSpan(nameStart, j - nameStart);
            var tagEnd = FindTagEnd(html, j);
            var afterTag = tagEnd >= n ? n : tagEnd + 1;

            if (!closing && IsSkippedContainer(name))
            {
                var afterClose = SkipToClosingTag(html, afterTag, name);
                AppendBreak(sb);
                i = afterClose < 0 ? afterTag : afterClose;
                continue;
            }

            if (IsCellBoundary(name)) AppendSpace(sb);
            else if (IsBlock(name)) AppendBreak(sb);

            i = afterTag;
        }

        return PlainText.Normalize(sb.ToString(), maxChars);
    }

    private static void AppendBreak(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
    }

    private static void AppendSpace(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] != ' ' && sb[sb.Length - 1] != '\n') sb.Append(' ');
    }

    private static int FindTagEnd(string s, int i)
    {
        var quote = '\0';
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                continue;
            }

            if (c == '>') return i;
        }

        return s.Length;
    }

    /// <summary>Index just past the matching close tag, or -1 when the document never closes it.</summary>
    private static int SkipToClosingTag(string s, int from, ReadOnlySpan<char> name)
    {
        var i = from;
        while (i < s.Length)
        {
            var lt = s.IndexOf('<', i);
            if (lt < 0) return -1;

            var j = lt + 1;
            if (j < s.Length && s[j] == '/')
            {
                j++;
                if (j + name.Length <= s.Length &&
                    s.AsSpan(j, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var after = j + name.Length;
                    if (after >= s.Length || !char.IsAsciiLetterOrDigit(s[after]))
                    {
                        var end = FindTagEnd(s, after);
                        return end >= s.Length ? s.Length : end + 1;
                    }
                }
            }

            i = lt + 1;
        }

        return -1;
    }

    private static bool IsSkippedContainer(ReadOnlySpan<char> name) =>
        name.Equals("script", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("style", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("title", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("noscript", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("template", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("svg", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("math", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("iframe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("object", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("applet", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("canvas", StringComparison.OrdinalIgnoreCase);

    private static bool IsCellBoundary(ReadOnlySpan<char> name) =>
        name.Equals("td", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("th", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlock(ReadOnlySpan<char> name)
    {
        if (name.Length == 2 && (name[0] == 'h' || name[0] == 'H') && name[1] >= '1' && name[1] <= '6') return true;

        return name.Equals("br", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("p", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("div", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("tr", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("li", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ol", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("dl", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("dt", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("dd", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("hr", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pre", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("table", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("thead", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("tbody", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("tfoot", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("blockquote", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("section", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("article", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("aside", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("header", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("footer", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("nav", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("figure", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("figcaption", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("address", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("center", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("fieldset", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("form", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("body", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("html", StringComparison.OrdinalIgnoreCase);
    }

    private static int AppendEntity(string s, int i, StringBuilder sb)
    {
        var max = Math.Min(s.Length, i + 34);
        var semi = -1;
        for (var k = i + 1; k < max; k++)
        {
            var c = s[k];
            if (c == ';')
            {
                semi = k;
                break;
            }

            if (!char.IsAsciiLetterOrDigit(c) && c != '#') break;
        }

        if (semi < 0 || semi == i + 1)
        {
            sb.Append('&');
            return i + 1;
        }

        var body = s.AsSpan(i + 1, semi - i - 1);

        if (body[0] == '#')
        {
            var cp = ParseNumeric(body[1..]);
            if (cp > 0) AppendCodePoint(sb, cp);
            return semi + 1;
        }

        var named = LookupNamed(body);
        if (named > 0)
        {
            AppendCodePoint(sb, named);
            return semi + 1;
        }

        sb.Append('&').Append(body).Append(';');
        return semi + 1;
    }

    private static int ParseNumeric(ReadOnlySpan<char> digits)
    {
        if (digits.Length == 0 || digits.Length > 8) return -1;

        var hex = digits[0] is 'x' or 'X';
        if (hex) digits = digits[1..];
        if (digits.Length == 0) return -1;

        var value = 0;
        foreach (var c in digits)
        {
            int d;
            if (char.IsAsciiDigit(c)) d = c - '0';
            else if (hex && c is >= 'a' and <= 'f') d = c - 'a' + 10;
            else if (hex && c is >= 'A' and <= 'F') d = c - 'A' + 10;
            else return -1;

            value = value * (hex ? 16 : 10) + d;
            if (value > 0x10FFFF) return -1;
        }

        if (value <= 0 || (value >= 0xD800 && value <= 0xDFFF)) return -1;
        return value;
    }

    private static void AppendCodePoint(StringBuilder sb, int cp)
    {
        if (cp <= 0xFFFF) sb.Append((char)cp);
        else sb.Append(char.ConvertFromUtf32(cp));
    }

    private static int LookupNamed(ReadOnlySpan<char> body)
    {
        if (body.Length > 10) return -1;

        return body.ToString().ToLowerInvariant() switch
        {
            "amp" => 0x0026,
            "lt" => 0x003C,
            "gt" => 0x003E,
            "quot" => 0x0022,
            "apos" => 0x0027,
            "nbsp" => 0x0020,
            "ensp" or "emsp" or "thinsp" => 0x0020,
            "shy" => 0x00AD,
            "copy" => 0x00A9,
            "reg" => 0x00AE,
            "trade" => 0x2122,
            "deg" => 0x00B0,
            "plusmn" => 0x00B1,
            "middot" => 0x00B7,
            "bull" => 0x2022,
            "hellip" => 0x2026,
            "ndash" => 0x2013,
            "mdash" => 0x2014,
            "lsquo" => 0x2018,
            "rsquo" => 0x2019,
            "sbquo" => 0x201A,
            "ldquo" => 0x201C,
            "rdquo" => 0x201D,
            "bdquo" => 0x201E,
            "dagger" => 0x2020,
            "laquo" => 0x00AB,
            "raquo" => 0x00BB,
            "euro" => 0x20AC,
            "pound" => 0x00A3,
            "yen" => 0x00A5,
            "cent" => 0x00A2,
            "sect" => 0x00A7,
            "para" => 0x00B6,
            "times" => 0x00D7,
            "divide" => 0x00F7,
            "frac12" => 0x00BD,
            "frac14" => 0x00BC,
            "sup2" => 0x00B2,
            "sup3" => 0x00B3,
            "larr" => 0x2190,
            "rarr" => 0x2192,
            "harr" => 0x2194,
            "infin" => 0x221E,
            "ne" => 0x2260,
            "le" => 0x2264,
            "ge" => 0x2265,
            "alpha" => 0x03B1,
            "beta" => 0x03B2,
            "micro" => 0x00B5,
            "iexcl" => 0x00A1,
            "iquest" => 0x00BF,
            "aacute" => 0x00E1,
            "eacute" => 0x00E9,
            "iacute" => 0x00ED,
            "oacute" => 0x00F3,
            "uacute" => 0x00FA,
            "agrave" => 0x00E0,
            "egrave" => 0x00E8,
            "ugrave" => 0x00F9,
            "auml" => 0x00E4,
            "ouml" => 0x00F6,
            "uuml" => 0x00FC,
            "szlig" => 0x00DF,
            "ntilde" => 0x00F1,
            "ccedil" => 0x00E7,
            "aring" => 0x00E5,
            "oslash" => 0x00F8,
            "aelig" => 0x00E6,
            _ => -1,
        };
    }
}
