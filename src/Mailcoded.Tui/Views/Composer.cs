using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

internal static class Composer
{
    private const int LabelWidth = 9;

    private static readonly (DraftField Field, string Label)[] Headers =
    [
        (DraftField.To, "To:"),
        (DraftField.Cc, "Cc:"),
        (DraftField.Bcc, "Bcc:"),
        (DraftField.Subject, "Subject:"),
    ];

    public static void Draw(TerminalWriter writer, AppState state, DraftBuffer draft)
    {
        var top = Screen.FirstBodyRow;
        var rows = Screen.BodyRows(writer);
        var width = writer.Columns;

        for (var i = 0; i < Headers.Length; i++)
        {
            var (field, label) = Headers[i];
            var focused = draft.Field == field;

            var span = TerminalText.Concat(
                TerminalText.Pad(SafeSpan.Chrome(label), LabelWidth),
                TerminalText.Cell(draft.Header(field), Math.Max(1, width - LabelWidth)));

            writer.Row(top + i, TerminalText.Pad(span, width), focused ? TextStyle.Inverse : TextStyle.Normal);
        }

        var ruleRow = top + Headers.Length;
        writer.Row(ruleRow, TerminalText.Chrome(new string('-', Math.Min(width, 200)), width), TextStyle.Dim);

        var bodyTop = ruleRow + 1;
        var bodyRows = Math.Max(1, top + rows - bodyTop);
        var first = Math.Max(0, draft.BodyLine - bodyRows + 1);

        for (var line = 0; line < bodyRows; line++)
        {
            var index = first + line;
            var text = index < draft.Body.Count ? draft.Body[index] : string.Empty;
            writer.Row(bodyTop + line, TerminalText.Cell(text, width));
        }

        _ = state;
    }

    /// <summary>Where the terminal cursor belongs, so typing lands where the user is looking.</summary>
    public static (int Row, int Column) Caret(TerminalWriter writer, DraftBuffer draft)
    {
        var top = Screen.FirstBodyRow;

        if (draft.Field != DraftField.Body)
        {
            var index = Array.FindIndex(Headers, h => h.Field == draft.Field);
            var typed = draft.Header(draft.Field);
            var caret = Math.Clamp(draft.HeaderCaret, 0, typed.Length);
            var columns = TerminalText.Cell(typed[..caret], writer.Columns).Columns;

            return (top + index, Math.Min(writer.Columns - 1, LabelWidth + columns));
        }

        var bodyTop = top + Headers.Length + 1;
        var bodyRows = Math.Max(1, top + Screen.BodyRows(writer) - bodyTop);
        var first = Math.Max(0, draft.BodyLine - bodyRows + 1);

        var lineText = draft.Body[Math.Clamp(draft.BodyLine, 0, draft.Body.Count - 1)];
        var column = Math.Clamp(draft.BodyColumn, 0, lineText.Length);

        return (
            bodyTop + (draft.BodyLine - first),
            Math.Min(writer.Columns - 1, TerminalText.Cell(lineText[..column], writer.Columns).Columns));
    }
}
