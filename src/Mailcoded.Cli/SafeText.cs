using System.Text;
using Mailcoded.Core.Parsing;

namespace Mailcoded.Cli;

/// <summary>
/// Email content is attacker-controlled. Every untrusted string that reaches a terminal passes
/// through here first, so an escape sequence in a subject can never redraw the operator's screen.
/// </summary>
internal static class SafeText
{
    public static string Line(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0) return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var gap = false;

        // Runes, not chars: clipping mid-surrogate emits a lone surrogate, and the astral tag
        // block is invisible smuggling material a char-typed scan cannot even see.
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maxLength) break;

            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) || PlainText.IsInvisible(rune))
            {
                if (builder.Length > 0) gap = true;
                continue;
            }

            if (!PlainText.IsRenderable(rune)) continue;

            if (gap)
            {
                if (builder.Length + 1 + rune.Utf16SequenceLength > maxLength) break;
                builder.Append(' ');
                gap = false;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }

    /// <summary>Multi-line form for a body: newlines and tabs survive, every other control does not.</summary>
    public static string Block(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0) return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));

        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maxLength) break;

            if (rune.Value is '\n' or '\t')
            {
                builder.Append(rune);
                continue;
            }

            if (rune.Value == '\r') continue;
            if (!PlainText.IsRenderable(rune)) continue;

            builder.Append(rune);
        }

        return builder.ToString();
    }
}
