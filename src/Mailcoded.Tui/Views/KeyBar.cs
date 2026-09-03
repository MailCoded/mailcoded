using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

/// <summary>One binding as the bar shows it. <see cref="Press"/> is null when the hint stands for a
/// pair of keys, so clicking it would have to guess which.</summary>
internal readonly record struct KeyHint(string Key, string Label, ConsoleKeyInfo? Press = null);

internal readonly record struct KeyBarSegment(KeyHint Hint, int Column, int Width);

/// <summary>The bindings that apply right now, so nothing has to be remembered.</summary>
internal static class KeyBar
{
    private const int Gap = 2;

    public static IReadOnlyList<KeyHint> Entries(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Prompt is not null)
            return [Hint("enter", "accept", '\r', ConsoleKey.Enter), Escape("cancel")];

        if (state.Focus == Pane.Confirm)
            return [Hint("Y", "send", 'Y'), Escape("back")];

        if (state.Focus == Pane.Compose)
        {
            return
            [
                Hint("tab", "next field", '\t', ConsoleKey.Tab),
                new KeyHint("ctrl-s", "preview", new ConsoleKeyInfo('\u0013', ConsoleKey.S, false, false, true)),
                Escape("discard"),
            ];
        }

        if (state.Focus is Pane.Help or Pane.Status or Pane.Outbox)
            return [Escape("back")];

        if (state.ChoosingDestination)
            return [Pair("j/k", "pick"), Hint("enter", "move here", '\r', ConsoleKey.Enter), Escape("cancel")];

        if (state.Open is not null)
        {
            return
            [
                Pair("j/k", "scroll"),
                Hint("r", "reply", 'r'),
                Hint("R", "reply all", 'R'),
                Hint("f", "flag", 'f'),
                Hint("a", "archive", 'a'),
                Hint("m", "move", 'm'),
                Hint("T", "thread", 'T'),
                Hint("s", "save file", 's'),
                Hint("?", "keys", '?'),
                Hint("q", "back", 'q'),
            ];
        }

        if (state.Focus == Pane.Folders)
        {
            return
            [
                Pair("j/k", "move"),
                Pair("h/l", "fold"),
                Hint("enter", "open", '\r', ConsoleKey.Enter),
                Hint("c", "compose", 'c'),
                Hint("/", "search", '/'),
                Hint("r", "sync", 'r'),
                Hint("S", "status", 'S'),
                Hint("?", "keys", '?'),
                Hint("q", "quit", 'q'),
            ];
        }

        return
        [
            Pair("j/k", "move"),
            Hint("enter", "read", '\r', ConsoleKey.Enter),
            Hint("c", "compose", 'c'),
            Hint("/", "search", '/'),
            Hint("t", "tags", 't'),
            Hint("f", "flag", 'f'),
            Hint("a", "archive", 'a'),
            Hint("m", "move", 'm'),
            Hint("T", "thread", 'T'),
            Hint("p", "preview", 'p'),
            Hint("o", "outbox", 'o'),
            Hint("?", "keys", '?'),
        ];
    }

    /// <summary>Lays hints out left to right, dropping whole hints that will not fit rather than
    /// clipping one into something unreadable.</summary>
    public static IReadOnlyList<KeyBarSegment> Place(IReadOnlyList<KeyHint> hints, int width)
    {
        ArgumentNullException.ThrowIfNull(hints);

        var segments = new List<KeyBarSegment>(hints.Count);
        var column = 1;

        foreach (var hint in hints)
        {
            var size = Width(hint);
            if (column + size > width) break;

            segments.Add(new KeyBarSegment(hint, column, size));
            column += size + Gap;
        }

        return segments;
    }

    public static void Draw(TerminalWriter writer, AppState state, int row)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Row(row, TerminalText.Pad(SafeSpan.Empty, writer.Columns), TextStyle.Normal);

        foreach (var segment in Place(Entries(state), writer.Columns))
        {
            writer.At(row, segment.Column, SafeSpan.Chrome(segment.Hint.Key), TextStyle.Inverse);
            writer.At(row, segment.Column + segment.Hint.Key.Length + 1, SafeSpan.Chrome(segment.Hint.Label), TextStyle.Dim);
        }
    }

    public static KeyHint? At(AppState state, int columns, int column)
    {
        foreach (var segment in Place(Entries(state), columns))
        {
            if (column >= segment.Column && column < segment.Column + segment.Width) return segment.Hint;
        }

        return null;
    }

    private static int Width(KeyHint hint) => hint.Key.Length + 1 + hint.Label.Length;

    private static KeyHint Hint(string key, string label, char c, ConsoleKey known = ConsoleKey.None) =>
        new(key, label, new ConsoleKeyInfo(c, known, false, false, false));

    private static KeyHint Pair(string key, string label) => new(key, label);

    private static KeyHint Escape(string label) =>
        new("esc", label, new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false));
}
