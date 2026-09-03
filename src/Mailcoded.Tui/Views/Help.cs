using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

internal static class Help
{
    internal static readonly string[] Lines =
    [
        "  mailcoded-tui - a reference client over JSON-RPC",
        "",
        "  The sidebar lists every configured account and all of its folders.",
        "  Special-use folders come first; the rest nest on '/'.",
        "",
        "  j / down      next            k / up        previous",
        "  g / G         first / last    n             next page",
        "  tab           other pane      h / l         collapse / expand",
        "                                                (or left / right pane)",
        "  enter         open            q             back, then quit",
        "  /             search          esc           clear search",
        "  r             resync folder   ctrl-l        redraw",
        "  ?             this help",
        "",
        "  Mouse: click a folder or a message, double-click to open, wheel to",
        "  scroll. Your terminal's own text selection needs shift held down",
        "  while mouse reporting is on.",
        "",
        "  u             toggle unread   f             toggle flagged",
        "  t             edit tags       a             archive",
        "  m             move to folder  p             preview pane on/off",
        "  T             this thread     s + digit     save an attachment",
        "  S             daemon status   o             outbox",
        "  A             test this account's connection",
        "",
        "  c             compose         r / R         reply / reply-all",
        "  tab           next field      ctrl-s        preview the send",
        "  Y             sends, and only from the confirmation screen",
        "",
        "  Opening a message marks it read. There is no delete key, in this",
        "  client or in the protocol: mail leaves only by send, and moves only",
        "  by message.move.",
        "",
        "  This client asks for plaintext only. It never requests format:html,",
        "  because a terminal has no sandbox and no content security policy.",
    ];

    public static void Draw(TerminalWriter writer, AppState state)
    {
        _ = state;
        var rows = Screen.BodyRows(writer);

        for (var line = 0; line < rows; line++)
        {
            var row = Screen.FirstBodyRow + line;
            if (line >= Lines.Length)
            {
                writer.Blank(row);
                continue;
            }

            writer.Row(row, TerminalText.Chrome(Lines[line], writer.Columns), Style(line));
        }
    }

    private static TextStyle Style(int line) =>
        line == 0 ? TextStyle.Bold
        : Lines[line].StartsWith("  Opening", StringComparison.Ordinal)
            || Lines[line].StartsWith("  client or", StringComparison.Ordinal)
            || Lines[line].StartsWith("  by message", StringComparison.Ordinal)
            || Lines[line].StartsWith("  This client", StringComparison.Ordinal)
            || Lines[line].StartsWith("  because", StringComparison.Ordinal)
                ? TextStyle.Warn
                : TextStyle.Normal;
}
