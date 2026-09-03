using Mailcoded.Protocol;
using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

internal static class Screen
{
    public const int MinFolderWidth = 16;

    public static void Draw(TerminalWriter writer, AppState state) => Draw(writer, state, DateTimeOffset.UtcNow);

    public static void Draw(TerminalWriter writer, AppState state, DateTimeOffset now)
    {
        writer.BeginFrame();

        Header(writer, state);

        var caret = (Row: -1, Column: 0);

        if (state.Focus == Pane.Help)
        {
            Help.Draw(writer, state);
        }
        else if (state.Focus == Pane.Confirm && state.Pending is { } pending)
        {
            ConfirmSend.Draw(writer, state, pending, now);
        }
        else if (state.Focus == Pane.Compose && state.Draft is { } draft)
        {
            Composer.Draw(writer, state, draft);
            caret = Composer.Caret(writer, draft);
        }
        else if (state.Open is { } open)
        {
            Reader.Draw(writer, state, open);
        }
        else
        {
            Panes(writer, state);
        }

        StatusBar(writer, state);

        if (state.Prompt is not null) writer.EndFrame(writer.Rows - 1, PromptCaret(writer, state));
        else writer.EndFrame(caret.Row, caret.Column);
    }

    private static int PromptCaret(TerminalWriter writer, AppState state)
    {
        var typed = (state.Prompt ?? string.Empty) + state.PromptInput;
        return Math.Min(writer.Columns - 1, TerminalText.Cell(typed, writer.Columns).Columns);
    }

    public static int FolderWidth(TerminalWriter writer) =>
        Layout.For(writer.Columns, wantPreview: false).FolderWidth;

    public static int FirstBodyRow => 1;

    public static int BodyRows(TerminalWriter writer) => Math.Max(1, writer.Rows - 2);

    private static void Header(TerminalWriter writer, AppState state)
    {
        var account = state.Account;
        var name = account is null ? "no account" : account.Email;
        var suffix = state.Accounts.Count > 1
            ? $"   ({state.Accounts.Count} accounts)"
            : string.Empty;

        var room = Math.Max(1, writer.Columns - 12 - suffix.Length);

        var left = TerminalText.Concat(
            SafeSpan.Chrome("mailcoded   "),
            TerminalText.Cell(name, room),
            SafeSpan.Chrome(suffix));

        writer.Row(0, TerminalText.Pad(left, writer.Columns), TextStyle.Inverse);
    }

    private static void Panes(TerminalWriter writer, AppState state)
    {
        var layout = Layout.For(writer.Columns, state.ShowPreview);

        FolderPane.Draw(writer, state, layout.FolderWidth);
        MessageList.Draw(writer, state, layout.ListLeft, layout.ListWidth);

        Rule(writer, layout.FolderWidth);

        if (!layout.HasPreview) return;

        Rule(writer, layout.PreviewLeft - 1);
        PreviewPane.Draw(writer, state, layout.PreviewLeft, layout.PreviewWidth);
    }

    private static void Rule(TerminalWriter writer, int column)
    {
        var rule = SafeSpan.Chrome("|");
        for (var row = FirstBodyRow; row < FirstBodyRow + BodyRows(writer); row++)
            writer.At(row, column, rule, TextStyle.Dim);
    }

    private static void StatusBar(TerminalWriter writer, AppState state)
    {
        var row = writer.Rows - 1;

        if (state.Prompt is { } prompt)
        {
            var typed = TerminalText.Cell(prompt + state.PromptInput, writer.Columns);
            writer.Row(row, TerminalText.Pad(typed, writer.Columns), TextStyle.Inverse);
            return;
        }

        // Status text embeds folder names and daemon messages, so it takes the untrusted path.
        var span = state.Status.Length > 0
            ? TerminalText.Cell(state.Status, writer.Columns)
            : TerminalText.Chrome(Hint(state), writer.Columns);

        writer.Row(row, TerminalText.Pad(span, writer.Columns),
            state.StatusIsError ? TextStyle.Danger : TextStyle.Inverse);
    }

    internal static string Hint(AppState state) => state.Focus switch
    {
        Pane.Reader => "j/k scroll   r reply   R reply-all   q back   ? help",
        Pane.Compose => "tab field   ctrl-s preview   esc discard",
        Pane.Confirm => "Y sends   anything else goes back",
        Pane.Help => "any key to close",
        Pane.Folders => "j/k move   h/l fold   enter open   c compose   / search   ? help   q quit",
        _ => "j/k move   enter read   p preview   c compose   / search   ? help   q back",
    };

    public static SafeSpan Flags(EnvelopeDto envelope)
    {
        var unread = envelope.Flags.Contains(FlagNames.Unread, StringComparer.Ordinal) ? "*" : " ";
        var flagged = envelope.Flags.Contains(FlagNames.Flagged, StringComparer.Ordinal) ? "!" : " ";
        var attached = envelope.HasAttachments ? "@" : " ";

        return SafeSpan.Chrome(unread + flagged + attached);
    }

    public static string ShortDate(string iso)
    {
        if (!DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var date))
            return "??? ??";

        var local = date.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : local.ToString("MMM dd", System.Globalization.CultureInfo.InvariantCulture);
    }
}
