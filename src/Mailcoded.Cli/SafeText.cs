using System.Text;

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

        foreach (var rune in value)
        {
            if (builder.Length >= maxLength) break;

            if (char.IsControl(rune) || char.IsWhiteSpace(rune))
            {
                if (builder.Length > 0) gap = true;
                continue;
            }

            if (gap)
            {
                builder.Append(' ');
                gap = false;
                if (builder.Length >= maxLength) break;
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

        foreach (var rune in value)
        {
            if (builder.Length >= maxLength) break;

            if (rune is '\n' or '\t')
            {
                builder.Append(rune);
                continue;
            }

            if (rune == '\r') continue;
            if (char.IsControl(rune)) continue;

            builder.Append(rune);
        }

        return builder.ToString();
    }
}
