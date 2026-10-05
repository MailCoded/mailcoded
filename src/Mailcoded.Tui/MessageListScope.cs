using System.Text;
using Mailcoded.Protocol;

namespace Mailcoded.Tui;

internal static class MessageListScope
{
    /// <summary>A search covers the whole account, except from inside a Folder the daemon leaves out of
    /// account-wide results: there it stays in that Folder, unless the query itself names a Folder or says
    /// <c>in:anywhere</c>, in which case the query decides.</summary>
    public static long? FolderFilter(FolderDto folder, string? query) =>
        query is null || (folder.Role is "trash" or "junk" && !NamesFolderScope(query)) ? folder.Id : null;

    /// <summary>Mirrors the engine's rule that a positive <c>in:</c>/<c>folder:</c> token names a Folder and a
    /// negated one only rules a Folder out; the Tui cannot reference the engine's parser.</summary>
    internal static bool NamesFolderScope(string query)
    {
        var pendingNegate = false;
        var i = 0;

        while (i < query.Length)
        {
            if (char.IsWhiteSpace(query[i]))
            {
                i++;
                continue;
            }

            var negated = pendingNegate;
            pendingNegate = false;

            if (query[i] == '-' && i + 1 < query.Length && !char.IsWhiteSpace(query[i + 1]))
            {
                negated = !negated;
                i++;
            }

            var word = ReadWord(query, ref i, out var quoted, out var colon);

            if (!quoted && colon < 0 && word.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                pendingNegate = !negated;
                continue;
            }

            if (!negated && colon > 0 && colon + 1 < word.Length && IsFolderField(word.AsSpan(0, colon))) return true;
        }

        return false;
    }

    private static bool IsFolderField(ReadOnlySpan<char> name) =>
        name.Equals("in", StringComparison.OrdinalIgnoreCase) || name.Equals("folder", StringComparison.OrdinalIgnoreCase);

    // Quotes group characters and hide a colon from field detection, as in the engine's word reader.
    private static string ReadWord(string s, ref int i, out bool quoted, out int colon)
    {
        var sb = new StringBuilder();
        quoted = false;
        colon = -1;

        while (i < s.Length && !char.IsWhiteSpace(s[i]))
        {
            var c = s[i];
            if (c == '"')
            {
                quoted = true;
                i++;
                while (i < s.Length && s[i] != '"') sb.Append(s[i++]);
                if (i < s.Length) i++;
                continue;
            }

            if (c == ':' && colon < 0) colon = sb.Length;
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
