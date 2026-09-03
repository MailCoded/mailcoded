using Mailcoded.Protocol;
using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

internal static class MessageList
{
    private const int DateWidth = 6;

    public static void Draw(TerminalWriter writer, AppState state, int left, int width)
    {
        var rows = Screen.BodyRows(writer);
        width = Math.Max(1, width);
        var focused = state.Focus == Pane.Messages;

        state.MessageScroll = FolderPane.Scroll(state.MessageIndex, rows, state.Messages.Count);

        var space = SafeSpan.Chrome(" ");
        var senderRoom = Math.Clamp(width / 4, 8, 24);
        var subjectRoom = Math.Max(1, width - 4 - senderRoom - 1 - DateWidth - 1);

        for (var line = 0; line < rows; line++)
        {
            var row = Screen.FirstBodyRow + line;
            var index = state.MessageScroll + line;

            if (index >= state.Messages.Count)
            {
                var filler = index == 0
                    ? TerminalText.Cell(state.BusyLabel.Length > 0 ? "working" : "nothing here", width)
                    : SafeSpan.Empty;

                writer.At(row, left, TerminalText.Pad(filler, width), TextStyle.Dim);
                continue;
            }

            var envelope = state.Messages[index];
            var selected = index == state.MessageIndex;
            var unread = envelope.Flags.Contains(FlagNames.Unread, StringComparer.Ordinal);

            var span = TerminalText.Pad(
                TerminalText.Concat(
                    Screen.Flags(envelope),
                    space,
                    TerminalText.Pad(TerminalText.Cell(envelope.From, senderRoom), senderRoom),
                    space,
                    TerminalText.Pad(TerminalText.Cell(envelope.Subject, subjectRoom), subjectRoom),
                    space,
                    TerminalText.Cell(Screen.ShortDate(envelope.Date), DateWidth)),
                width);

            var style = selected
                ? (focused ? TextStyle.Inverse : TextStyle.Bold)
                : (unread ? TextStyle.Normal : TextStyle.Dim);

            writer.At(row, left, span, style);
        }
    }
}
