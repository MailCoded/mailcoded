using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

internal static class FolderPane
{
    public static void Draw(TerminalWriter writer, AppState state, int width)
    {
        var rows = Screen.BodyRows(writer);
        var focused = state.Focus == Pane.Folders;
        var top = Scroll(state.FolderIndex, rows, state.Folders.Count);

        for (var line = 0; line < rows; line++)
        {
            var row = Screen.FirstBodyRow + line;
            var index = top + line;

            if (index >= state.Folders.Count)
            {
                writer.At(row, 0, TerminalText.Pad(SafeSpan.Empty, width));
                continue;
            }

            var folder = state.Folders[index];
            var selected = index == state.FolderIndex;

            var counts = folder.Unread > 0 ? $" {folder.Unread}/{folder.Total}" : $" {folder.Total}";
            var countSpan = TerminalText.Cell(counts, Math.Max(0, width - 3));
            var nameRoom = Math.Max(1, width - 1 - countSpan.Columns);
            var name = TerminalText.Cell(folder.Name, nameRoom);

            var padded = TerminalText.Pad(name, nameRoom);
            var line0 = TerminalText.Pad(TerminalText.Concat(padded, countSpan), width);

            var style = selected
                ? (focused ? TextStyle.Inverse : TextStyle.Bold)
                : (folder.Unread > 0 ? TextStyle.Normal : TextStyle.Dim);

            writer.At(row, 0, line0, style);
        }
    }

    public static int Scroll(int index, int rows, int count)
    {
        if (count <= rows) return 0;
        var top = index - (rows / 2);
        return Math.Clamp(top, 0, count - rows);
    }
}
