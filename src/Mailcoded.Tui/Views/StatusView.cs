using Mailcoded.Protocol;
using Mailcoded.Tui.Render;

namespace Mailcoded.Tui.Views;

/// <summary>What 'mailcoded stats' and 'mailcoded health' print, on one screen.</summary>
internal static class StatusView
{
    public static void Draw(TerminalWriter writer, AppState state, StatusReport report)
    {
        var width = writer.Columns;
        var top = Screen.FirstBodyRow;
        var last = top + Screen.BodyRows(writer);
        var row = top;

        void Line(SafeSpan span, TextStyle style)
        {
            if (row < last) writer.Row(row++, span, style);
        }

        var stats = report.Stats;

        Line(TerminalText.Chrome("  DAEMON", width), TextStyle.Bold);
        Line(Field("version", stats.DaemonVersion, width), TextStyle.Normal);
        Line(Field("protocol", stats.ProtocolVersion.ToString(Culture), width), TextStyle.Dim);
        Line(Field("uptime", Duration(stats.UptimeMs), width), TextStyle.Dim);
        Line(Field("memory", Bytes(stats.WorkingSetBytes), width), TextStyle.Dim);
        Line(SafeSpan.Empty, TextStyle.Normal);

        Line(TerminalText.Chrome("  ACCOUNTS", width), TextStyle.Bold);
        Line(Field("store", report.Health.StoreOk ? "ok" : "PROBLEM", width),
            report.Health.StoreOk ? TextStyle.Normal : TextStyle.Danger);

        foreach (var account in report.Health.Accounts)
        {
            Line(TerminalText.Cell($"  {account.Email}", width), TextStyle.Accent);
            Line(Field("connection", account.Connection, width), Colour(account.Connection));
            Line(Field("auth", account.Auth, width), account.Auth == "ok" ? TextStyle.Normal : TextStyle.Warn);
            Line(Field("watching", account.Watching ? "yes" : "no", width), TextStyle.Dim);
            Line(Field("outbox", $"{account.OutboxQueued} queued, {account.OutboxFailed} failed", width),
                account.OutboxFailed > 0 ? TextStyle.Warn : TextStyle.Dim);

            if (account.LastError is { Length: > 0 } error)
                Line(Field("last error", error, width), TextStyle.Danger);
        }

        foreach (var warning in report.Health.Warnings)
            Line(Field("warning", warning, width), TextStyle.Warn);

        Line(SafeSpan.Empty, TextStyle.Normal);
        Line(TerminalText.Chrome("  any key to go back", width), TextStyle.Dim);

        while (row < last) writer.Blank(row++);
    }

    private static TextStyle Colour(string connection) => connection switch
    {
        "connected" => TextStyle.Normal,
        "error" => TextStyle.Danger,
        _ => TextStyle.Warn,
    };

    private static SafeSpan Field(string label, string? value, int width)
    {
        var tag = TerminalText.Pad(SafeSpan.Chrome("    " + label + ":"), 16);
        return TerminalText.Concat(tag, TerminalText.Cell(value, Math.Max(1, width - tag.Columns)));
    }

    private static string Duration(long ms) =>
        ms < 60_000 ? $"{ms / 1000}s"
        : ms < 3_600_000 ? $"{ms / 60_000}m"
        : $"{ms / 3_600_000}h {ms % 3_600_000 / 60_000}m";

    private static string Bytes(long size) =>
        size < 1024 * 1024 ? $"{size / 1024} KB" : $"{size / (1024.0 * 1024.0):0.#} MB";

    private static System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.InvariantCulture;
}
