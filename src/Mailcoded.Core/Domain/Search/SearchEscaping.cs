using System.Text;

namespace Mailcoded.Core.Domain.Search;

/// <summary>Tokenizer routing: unicode61 handles Latin, trigram needs 3+ CJK chars, the rest needs LIKE.</summary>
public enum ScriptKind
{
    Latin,
    Cjk,
    ShortCjk,
}

/// <summary>Escaping for FTS5 and LIKE. No SQL is produced here; the store parameterizes everything.</summary>
public static class SearchEscaping
{
    public const int MaxTermLength = 256;
    public const char LikeEscapeChar = '\\';

    /// <summary>Wraps a term as an FTS5 string literal, which neutralizes every query metacharacter.</summary>
    public static string ToFtsString(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var sb = new StringBuilder(term.Length + 2);
        sb.Append('"');
        foreach (var c in term)
        {
            if (char.IsControl(c)) { sb.Append(' '); continue; }
            if (c == '"') sb.Append('"');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>A contains-pattern for a LIKE bound with <c>ESCAPE '\'</c>.</summary>
    public static string ToLikePattern(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var sb = new StringBuilder(term.Length + 4);
        sb.Append('%');
        foreach (var c in term)
        {
            if (char.IsControl(c)) continue;
            if (c is '%' or '_' or LikeEscapeChar) sb.Append(LikeEscapeChar);
            sb.Append(c);
        }
        sb.Append('%');
        return sb.ToString();
    }

    /// <summary>A term with nothing tokenizable would make FTS5 raise a syntax error, so callers drop it.</summary>
    public static bool HasTokenChar(string? term)
    {
        if (string.IsNullOrEmpty(term)) return false;
        foreach (var r in term.AsSpan().EnumerateRunes())
            if (Rune.IsLetterOrDigit(r)) return true;
        return false;
    }

    public static ScriptKind Classify(string? term)
    {
        if (string.IsNullOrEmpty(term)) return ScriptKind.Latin;

        var cjk = 0;
        foreach (var r in term.AsSpan().EnumerateRunes())
            if (IsCjk(r)) cjk++;

        if (cjk == 0) return ScriptKind.Latin;
        return cjk >= 3 ? ScriptKind.Cjk : ScriptKind.ShortCjk;
    }

    public static bool IsCjk(Rune r)
    {
        var v = r.Value;
        return (v >= 0x1100 && v <= 0x11FF)
            || (v >= 0x3040 && v <= 0x30FF)
            || (v >= 0x3400 && v <= 0x4DBF)
            || (v >= 0x4E00 && v <= 0x9FFF)
            || (v >= 0xAC00 && v <= 0xD7AF)
            || (v >= 0xF900 && v <= 0xFAFF)
            || (v >= 0x20000 && v <= 0x2FA1F);
    }

    /// <summary>Collapses whitespace, drops controls, and clamps length. Email-derived text is untrusted.</summary>
    public static string NormalizeTerm(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var sb = new StringBuilder(Math.Min(raw.Length, MaxTermLength));
        var gap = false;

        foreach (var c in raw)
        {
            if (sb.Length >= MaxTermLength) break;
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (sb.Length > 0) gap = true;
                continue;
            }
            if (gap) { sb.Append(' '); gap = false; }
            sb.Append(c);
        }

        return sb.ToString();
    }
}
