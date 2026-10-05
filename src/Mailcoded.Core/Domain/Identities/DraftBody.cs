using System.Text;

namespace Mailcoded.Core.Domain.Identities;

/// <summary>Lays a draft body out: new text, then the signature, then the quoted original.</summary>
public static class DraftBody
{
    /// <summary>The RFC 3676 section 4.3 line that starts a signature; the trailing space is part of it.</summary>
    public const string Separator = "-- ";

    public static string Compose(string newText, string? signature, string? quoted)
    {
        ArgumentNullException.ThrowIfNull(newText);

        var hasQuote = !string.IsNullOrEmpty(quoted);
        if (signature is null && !hasQuote) return newText;

        var builder = new StringBuilder(newText.Length + (signature?.Length ?? 0) + (quoted?.Length ?? 0) + 8);
        builder.Append(newText.TrimEnd());

        if (signature is not null)
        {
            if (builder.Length > 0) builder.Append("\n\n");
            builder.Append(Separator).Append('\n').Append(signature).Append('\n');
        }

        if (hasQuote)
        {
            if (builder.Length > 0) builder.Append(signature is null ? "\n\n" : "\n");
            builder.Append(quoted);
        }

        return builder.ToString();
    }

    public static bool HasSeparator(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var start = 0;
        while (start <= text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;

            var line = text.AsSpan(start, end - start);
            if (line.EndsWith("\r".AsSpan())) line = line[..^1];
            if (line.SequenceEqual(Separator)) return true;

            start = end + 1;
        }

        return false;
    }
}
