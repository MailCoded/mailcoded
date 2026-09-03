using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

internal static class FolderPane
{
    public static void Draw(TerminalWriter writer, AppState state, int width)
    {
        var rows = Screen.BodyRows(writer);
        var focused = state.Focus == Pane.Folders;

        state.NavScroll = Scroll(state.NavIndex, rows, state.Nav.Count);

        // Sized to what is actually on screen: a fixed column steals room from the longest address.
        var counts = 0;
        for (var line = 0; line < rows && state.NavScroll + line < state.Nav.Count; line++)
            counts = Math.Max(counts, Counts(state.Nav[state.NavScroll + line]).Length);

        for (var line = 0; line < rows; line++)
        {
            var row = Screen.FirstBodyRow + line;
            var index = state.NavScroll + line;

            if (index >= state.Nav.Count)
            {
                writer.At(row, 0, TerminalText.Pad(SafeSpan.Empty, width));
                continue;
            }

            var nav = state.Nav[index];
            var selected = index == state.NavIndex;

            writer.At(row, 0, Line(nav, width, counts), Style(nav, selected, focused, state.ChoosingDestination));
        }
    }

    private static SafeSpan Line(NavRow nav, int width, int countWidth)
    {
        var countSpan = SafeSpan.Chrome(Counts(nav).PadLeft(countWidth));
        var indent = SafeSpan.Chrome(new string(' ', Math.Min(nav.Depth * 2, Math.Max(0, width - 8))) + Twisty(nav));
        var room = Math.Max(1, width - indent.Columns - countSpan.Columns - 1);

        return TerminalText.Pad(
            TerminalText.Concat(indent, TerminalText.Pad(TerminalText.Cell(nav.Label, room), room), countSpan),
            width);
    }

    /// <summary>An account row totals its folders; a folder with no unread shows only its size.</summary>
    private static string Counts(NavRow nav)
    {
        if (nav.Kind == NavKind.Folder && nav.Folder is null) return string.Empty;
        if (nav.Unread > 0) return $"{nav.Unread}/{nav.Total}";
        return nav.Total > 0 ? nav.Total.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string Twisty(NavRow nav) =>
        nav.HasChildren ? (nav.Expanded ? "- " : "+ ") : (nav.Kind == NavKind.Account ? "  " : "  ");

    private static TextStyle Style(NavRow nav, bool selected, bool focused, bool choosing)
    {
        if (selected) return focused ? TextStyle.Inverse : TextStyle.Bold;
        if (nav.Kind == NavKind.Account) return TextStyle.Accent;
        if (choosing) return TextStyle.Normal;
        return nav.Unread > 0 ? TextStyle.Normal : TextStyle.Dim;
    }

    public static int Scroll(int index, int rows, int count)
    {
        if (count <= rows) return 0;
        var top = index - (rows / 2);
        return Math.Clamp(top, 0, count - rows);
    }
}
