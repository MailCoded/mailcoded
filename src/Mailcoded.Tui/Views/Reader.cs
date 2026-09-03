using Mailcoded.Protocol;
using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

internal static class Reader
{
    public static void Draw(TerminalWriter writer, AppState state, MessageGetResult message)
    {
        var rows = Screen.BodyRows(writer);
        var width = writer.Columns;
        var row = Screen.FirstBodyRow;

        foreach (var header in Headers(message, width))
        {
            if (row >= Screen.FirstBodyRow + rows) return;
            writer.Row(row++, header.Span, header.Style);
        }

        if (row < Screen.FirstBodyRow + rows) writer.Blank(row++);

        var remaining = Screen.FirstBodyRow + rows - row;
        if (remaining <= 0) return;

        var lines = Body(state, message, width);
        var scroll = Math.Clamp(state.BodyScroll, 0, Math.Max(0, lines.Count - remaining));
        state.BodyScroll = scroll;

        for (var line = 0; line < remaining; line++)
        {
            var index = scroll + line;
            writer.Row(row + line, index < lines.Count ? lines[index] : SafeSpan.Empty);
        }
    }

    private static IEnumerable<(SafeSpan Span, TextStyle Style)> Headers(MessageGetResult message, int width)
    {
        var envelope = message.Envelope;

        yield return (Field("Subject", envelope.Subject, width), TextStyle.Bold);
        yield return (Field("From", envelope.From, width), TextStyle.Normal);
        yield return (Field("To", envelope.To, width), TextStyle.Dim);

        if (!string.IsNullOrWhiteSpace(envelope.Cc)) yield return (Field("Cc", envelope.Cc, width), TextStyle.Dim);

        yield return (Field("Date", Iso(envelope.Date), width), TextStyle.Dim);

        if (envelope.Tags.Count > 0)
            yield return (Field("Tags", string.Join(' ', envelope.Tags), width), TextStyle.Accent);

        foreach (var attachment in message.Attachments)
        {
            var label = $"{attachment.Index}  {attachment.Filename}  {attachment.Mime}  {Bytes(attachment.Size)}";
            yield return (Field("Attach", label, width), TextStyle.Accent);
        }

        if (message.ParseWarnings.Count > 0)
            yield return (Field("Warn", string.Join(' ', message.ParseWarnings), width), TextStyle.Warn);
    }

    private static SafeSpan Field(string label, string? value, int width)
    {
        var tag = TerminalText.Pad(SafeSpan.Chrome(label + ":"), 9);
        return TerminalText.Concat(tag, TerminalText.Cell(value, Math.Max(1, width - tag.Columns)));
    }

    /// <summary>Wrapping a long body costs more than a keystroke should, so it is cached per width.</summary>
    private static IReadOnlyList<SafeSpan> Body(AppState state, MessageGetResult message, int width)
    {
        if (state.BodyLines is { } cached && state.BodyWidth == width) return cached;

        var lines = message.BodyText is { Length: > 0 } text
            ? TerminalText.Wrap(text, Math.Min(width, 100), 5000)
            : [TerminalText.Cell(
                message.BodyFetched ? "(this message has no plaintext part)" : "(body not fetched)", width)];

        state.BodyLines = lines;
        state.BodyWidth = width;
        return lines;
    }

    private static string Iso(string date) =>
        DateTimeOffset.TryParse(date, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : date;

    private static string Bytes(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024.0:0.#} KB",
        _ => $"{size / (1024.0 * 1024.0):0.#} MB",
    };
}
