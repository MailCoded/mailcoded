using Mailcoded.Protocol;
using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

internal static class PreviewPane
{
    public static void Draw(TerminalWriter writer, AppState state, int left, int width)
    {
        var top = Screen.FirstBodyRow;
        var rows = Screen.BodyRows(writer);
        var row = top;

        void Line(SafeSpan span, TextStyle style)
        {
            if (row < top + rows) writer.At(row++, left, TerminalText.Pad(span, width), style);
        }

        if (state.Selected is not { } envelope)
        {
            Line(TerminalText.Chrome(" nothing selected", width), TextStyle.Dim);
            while (row < top + rows) writer.At(row++, left, TerminalText.Pad(SafeSpan.Empty, width));
            return;
        }

        Line(Field("From", envelope.From, width), TextStyle.Normal);
        Line(Field("Subject", envelope.Subject, width), TextStyle.Bold);
        Line(Field("Date", Screen.ShortDate(envelope.Date), width), TextStyle.Dim);
        Line(TerminalText.Chrome(new string('-', Math.Min(width, 200)), width), TextStyle.Dim);

        foreach (var line in Body(state, envelope, width, top + rows - row))
            Line(line.Span, line.Style);

        while (row < top + rows) writer.At(row++, left, TerminalText.Pad(SafeSpan.Empty, width));
    }

    private static IEnumerable<(SafeSpan Span, TextStyle Style)> Body(
        AppState state,
        EnvelopeDto envelope,
        int width,
        int rows)
    {
        if (state.PreviewError is { } failed)
        {
            yield return (TerminalText.Cell(" " + failed, width), TextStyle.Danger);
            yield break;
        }

        if (state.Preview is not { } preview || preview.Envelope.Id != envelope.Id)
        {
            yield return (
                TerminalText.Chrome(state.PreviewBusy ? " fetching..." : " ", width),
                TextStyle.Dim);
            yield break;
        }

        if (!preview.BodyFetched && preview.BodyText is not { Length: > 0 })
        {
            yield return (TerminalText.Chrome(" this message has no plaintext part", width), TextStyle.Dim);
            yield break;
        }

        foreach (var attachment in preview.Attachments)
        {
            yield return (
                Field("Attach", $"{attachment.Filename}  {attachment.Mime}", width),
                TextStyle.Accent);
        }

        var body = state.PreviewLines ?? [];
        for (var i = 0; i < Math.Min(body.Count, Math.Max(0, rows)); i++)
            yield return (body[i], TextStyle.Normal);
    }

    private static SafeSpan Field(string label, string? value, int width)
    {
        var tag = TerminalText.Pad(SafeSpan.Chrome(" " + label + ":"), 9);
        return TerminalText.Concat(tag, TerminalText.Cell(value, Math.Max(1, width - tag.Columns)));
    }
}
