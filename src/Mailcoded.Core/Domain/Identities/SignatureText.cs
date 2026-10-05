using System.Text;

namespace Mailcoded.Core.Domain.Identities;

/// <summary>The rules for one plaintext signature: what text is allowed and what a name looks like.</summary>
public static class SignatureText
{
    public const int MaxChars = 2_000;
    public const int MaxLines = 20;
    public const int MaxNameLength = 32;

    private const int MaxRawChars = 8_192;

    /// <summary>Cleans and checks a signature. Stripping is silent; exceeding a cap is an error, never a truncation.</summary>
    public static bool TryNormalize(string? raw, out string text, out string error)
    {
        text = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "The signature is empty.";
            return false;
        }

        if (raw.Length > MaxRawChars)
        {
            error = $"The signature is far longer than the {MaxChars} character limit.";
            return false;
        }

        var cleaned = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            if (c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n') continue;
            if (TextSafety.IsLineBreak(c)) cleaned.Append('\n');
            else if (c == '\t' || (!char.IsControl(c) && !TextSafety.IsInvisibleFormat(c))) cleaned.Append(c);
        }

        var lines = new List<string>(cleaned.ToString().Split('\n'));
        for (var i = 0; i < lines.Count; i++) lines[i] = lines[i].TrimEnd(' ', '\t');

        TrimBlankEdges(lines);
        if (lines.Count > 0 && lines[0] == "--")
        {
            lines.RemoveAt(0);
            TrimBlankEdges(lines);
        }

        if (lines.Count == 0)
        {
            error = "The signature is empty.";
            return false;
        }

        if (lines.Count > MaxLines)
        {
            error = $"The signature has more than {MaxLines} lines.";
            return false;
        }

        var joined = string.Join('\n', lines);
        if (joined.Length > MaxChars)
        {
            error = $"The signature is longer than {MaxChars} characters.";
            return false;
        }

        text = joined;
        error = string.Empty;
        return true;
    }

    public static bool TryNormalizeName(string? raw, out string name)
    {
        name = string.Empty;
        if (raw is null) return false;

        var trimmed = raw.Trim();
        if (trimmed.Length is 0 or > MaxNameLength) return false;

        foreach (var c in trimmed)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-';
            if (!ok) return false;
        }

        name = trimmed.ToLowerInvariant();
        return true;
    }

    private static void TrimBlankEdges(List<string> lines)
    {
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
    }
}
