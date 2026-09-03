using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

/// <summary>The human between mint and consume. It lists every recipient, and it never shows the
/// confirm token, which this view has no access to.</summary>
internal static class ConfirmSend
{
    private const int LabelWidth = 11;
    public const char ConfirmKey = 'Y';

    public static void Draw(TerminalWriter writer, AppState state, PendingSend pending, DateTimeOffset now)
    {
        var preview = pending.Preview;
        var width = writer.Columns;
        var top = Screen.FirstBodyRow;
        var last = top + Screen.BodyRows(writer);
        var row = top;

        void Line(SafeSpan span, TextStyle style)
        {
            if (row < last) writer.Row(row++, span, style);
        }

        Line(TerminalText.Chrome("  SEND THIS MESSAGE?", width), TextStyle.Bold);
        Line(SafeSpan.Empty, TextStyle.Normal);

        Line(Field("From", preview.From, width), TextStyle.Normal);

        foreach (var to in preview.To) Line(Field("To", to, width), TextStyle.Danger);
        foreach (var cc in preview.Cc) Line(Field("Cc", cc, width), TextStyle.Danger);
        foreach (var bcc in preview.Bcc) Line(Field("Bcc", bcc, width), TextStyle.Danger);

        Line(Field("Subject", preview.Subject, width), TextStyle.Bold);
        Line(Field("Size", $"{preview.SizeBytes} bytes", width), TextStyle.Dim);

        if (preview.RequiresSmtpUtf8) Line(Field("Encoding", "needs SMTPUTF8", width), TextStyle.Warn);
        foreach (var warning in preview.Warnings) Line(Field("Warning", warning, width), TextStyle.Warn);

        Line(SafeSpan.Empty, TextStyle.Normal);

        foreach (var line in TerminalText.Wrap(preview.BodyPreview, Math.Min(width - 2, 96), 8))
            Line(TerminalText.Concat(SafeSpan.Chrome("  "), line), TextStyle.Normal);

        if (preview.BodyTruncated) Line(TerminalText.Chrome("  [body truncated for preview]", width), TextStyle.Dim);

        Line(SafeSpan.Empty, TextStyle.Normal);
        Line(TerminalText.Chrome(Countdown(pending.ExpiresUtc, now), width), TextStyle.Dim);
        Line(SafeSpan.Empty, TextStyle.Normal);
        Line(
            TerminalText.Chrome($"  press {ConfirmKey} to send, anything else to go back", width),
            TextStyle.Inverse);

        while (row < last) writer.Blank(row++);
        _ = state;
    }

    public static string Countdown(DateTimeOffset? expires, DateTimeOffset now)
    {
        if (expires is not { } deadline) return "  This confirmation does not expire.";

        var left = deadline - now;
        return left <= TimeSpan.Zero
            ? "  This confirmation has expired; go back and preview again."
            : $"  This confirmation expires in {(int)left.TotalSeconds}s.";
    }

    private static SafeSpan Field(string label, string? value, int width) =>
        TerminalText.Concat(
            TerminalText.Pad(SafeSpan.Chrome("  " + label + ":"), LabelWidth),
            TerminalText.Cell(value, Math.Max(1, width - LabelWidth)));
}
