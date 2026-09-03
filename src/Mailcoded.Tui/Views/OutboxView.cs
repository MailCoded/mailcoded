using Mailcoded.Protocol;
using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

internal static class OutboxView
{
    public static void Draw(TerminalWriter writer, AppState state)
    {
        var width = writer.Columns;
        var top = Screen.FirstBodyRow;
        var last = top + Screen.BodyRows(writer);
        var row = top;

        void Line(SafeSpan span, TextStyle style)
        {
            if (row < last) writer.Row(row++, span, style);
        }

        Line(TerminalText.Chrome("  OUTBOX", width), TextStyle.Bold);
        Line(SafeSpan.Empty, TextStyle.Normal);

        if (state.Outbox.Count == 0)
            Line(TerminalText.Chrome("  nothing queued", width), TextStyle.Dim);

        foreach (var entry in state.Outbox)
        {
            var to = entry.To.Count > 0 ? string.Join(", ", entry.To) : "(no recipient)";

            Line(
                TerminalText.Concat(
                    TerminalText.Pad(SafeSpan.Chrome("  " + Describe(entry)), 22),
                    TerminalText.Cell(to, Math.Max(1, width - 22))),
                Colour(entry));

            if (entry.SmtpResponse is { Length: > 0 } reply)
                Line(TerminalText.Cell("        " + reply, width), TextStyle.Dim);
        }

        Line(SafeSpan.Empty, TextStyle.Normal);
        Line(TerminalText.Chrome("  any key to go back", width), TextStyle.Dim);

        while (row < last) writer.Blank(row++);
    }

    /// <summary>A queued row nobody confirmed is a draft; calling it queued implies it is going out.</summary>
    private static string Describe(OutboxEntryDto entry) =>
        entry.State == OutboxStates.Queued && !entry.Confirmed
            ? "draft"
            : entry.Attempts > 0 ? $"{entry.State} ({entry.Attempts})" : entry.State;

    private static TextStyle Colour(OutboxEntryDto entry) => entry switch
    {
        { PermanentlyFailed: true } => TextStyle.Danger,
        { State: "failed" } => TextStyle.Warn,
        { State: "sent" } => TextStyle.Dim,
        { Confirmed: false } => TextStyle.Dim,
        _ => TextStyle.Normal,
    };
}
