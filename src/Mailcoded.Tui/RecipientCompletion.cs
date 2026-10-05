namespace Mailcoded.Tui;

/// <summary>The part of a recipient field being completed: where it starts, how long it is, and what was typed before the caret.</summary>
internal readonly record struct RecipientToken(int Start, int Length, string Prefix);

/// <summary>Pure text rules for completing the recipient under the caret. No I/O, no terminal.</summary>
internal static class RecipientCompletion
{
    public const int MaxSuggestions = 8;

    /// <summary>The address the caret is in: from after the last separator before it, to the next separator after it.</summary>
    public static RecipientToken TokenAt(string header, int caret)
    {
        ArgumentNullException.ThrowIfNull(header);

        caret = Math.Clamp(caret, 0, header.Length);

        var start = caret;
        while (start > 0 && header[start - 1] is not (',' or ';')) start--;
        while (start < caret && char.IsWhiteSpace(header[start])) start++;

        var end = caret;
        while (end < header.Length && header[end] is not (',' or ';')) end++;
        while (end > caret && char.IsWhiteSpace(header[end - 1])) end--;

        return new RecipientToken(start, end - start, header[start..caret]);
    }

    /// <summary>Keeps the candidates the field does not already hold, in the order given.</summary>
    public static IReadOnlyList<string> Unlisted(string header, RecipientToken token, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(candidates);

        var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var before = header[..Math.Min(token.Start, header.Length)];
        var afterAt = Math.Min(token.Start + token.Length, header.Length);

        foreach (var entry in DraftBuffer.SplitAddresses(before + "," + header[afterAt..])) others.Add(entry);

        var kept = new List<string>();
        foreach (var candidate in candidates)
            if (!others.Contains(candidate)) kept.Add(candidate);

        return kept;
    }
}
