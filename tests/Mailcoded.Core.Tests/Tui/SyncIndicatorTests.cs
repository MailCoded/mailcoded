using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Mailcoded.Protocol;
using Mailcoded.Tui;
using Mailcoded.Tui.Render;
using Mailcoded.Tui.Views;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

public sealed class SyncIndicatorTests
{
    private const long T0 = 5_000;

    [Fact]
    public void An_indicator_opens_on_progress_and_closes_on_its_final()
    {
        var indicators = new SyncIndicators();

        indicators.Apply(Envelopes(1, 12, "INBOX", 10, 40), T0);
        Assert.Equal("sync INBOX 10/40", indicators.Describe());

        indicators.Apply(Envelopes(1, 12, "INBOX", 40, 40, final: true), T0 + 1);
        Assert.Null(indicators.Describe());
    }

    [Fact]
    public void A_final_for_an_indicator_never_opened_changes_nothing()
    {
        var indicators = new SyncIndicators();

        indicators.Apply(Envelopes(1, 12, "INBOX", 10, 40), T0);
        indicators.Apply(Envelopes(1, 13, "Archive", 5, 5, final: true), T0);

        Assert.Equal("sync INBOX 10/40", indicators.Describe());
    }

    [Fact]
    public void The_same_folder_and_phase_replace_rather_than_stack()
    {
        var indicators = new SyncIndicators();

        indicators.Apply(Envelopes(1, 12, "INBOX", 10, 40), T0);
        indicators.Apply(Envelopes(1, 12, "INBOX", 20, 40), T0);

        Assert.Equal("sync INBOX 20/40", indicators.Describe());
    }

    [Fact]
    public void The_folder_walk_and_a_folder_are_separate_indicators()
    {
        var indicators = new SyncIndicators();

        indicators.Apply(Walk(1, 3, 12), T0);
        indicators.Apply(Envelopes(1, 12, "INBOX", 1200, 4800), T0);
        Assert.Equal("sync 3/12 folders, INBOX 1200/4800", indicators.Describe());

        indicators.Apply(Walk(1, 12, 12, final: true), T0);
        Assert.Equal("sync INBOX 1200/4800", indicators.Describe());
    }

    [Fact]
    public void The_same_phase_of_two_folders_are_two_indicators()
    {
        var indicators = new SyncIndicators();

        indicators.Apply(Envelopes(1, 12, "INBOX", 1, 2), T0);
        indicators.Apply(Envelopes(1, 13, "Archive", 3, 4), T0);

        Assert.Equal("sync INBOX 1/2, Archive 3/4", indicators.Describe());
    }

    [Fact]
    public void An_account_silent_for_a_minute_loses_its_indicators()
    {
        var indicators = new SyncIndicators();
        indicators.Apply(Envelopes(1, 12, "INBOX", 10, 40), T0);

        Assert.False(indicators.Expire(T0 + SyncIndicators.StaleAfterMs - 1));
        Assert.NotNull(indicators.Describe());

        Assert.True(indicators.Expire(T0 + SyncIndicators.StaleAfterMs));
        Assert.Null(indicators.Describe());
        Assert.False(indicators.Expire(T0 + SyncIndicators.StaleAfterMs * 2));
    }

    [Fact]
    public void Activity_on_one_account_does_not_keep_another_alive()
    {
        var indicators = new SyncIndicators();
        indicators.Apply(Envelopes(1, 12, "INBOX", 10, 40), T0);
        indicators.Apply(Envelopes(2, 22, "Sent", 1, 9), T0 + 50_000);

        Assert.True(indicators.Expire(T0 + SyncIndicators.StaleAfterMs));

        Assert.Equal("sync Sent 1/9", indicators.Describe());
    }

    [Fact]
    public void A_count_with_no_total_is_shown_bare()
    {
        var indicators = new SyncIndicators();

        indicators.Apply(Flags(1, 12, "Archive", 3000, null), T0);

        Assert.Equal("sync Archive flags 3000", indicators.Describe());
    }

    [Fact]
    public void An_unknown_phase_is_shown_rather_than_dropped()
    {
        var indicators = new SyncIndicators();

        indicators.Apply(Envelopes(1, 12, "INBOX", 5, 10) with { Phase = "vectors" }, T0);

        Assert.Equal("sync INBOX 5/10", indicators.Describe());
    }

    [Fact]
    public void A_missing_folder_name_does_not_throw()
    {
        var indicators = new SyncIndicators();

        indicators.Apply(Envelopes(1, 12, "INBOX", 5, 10) with { FolderName = null }, T0);

        Assert.Equal("sync ? 5/10", indicators.Describe());
    }

    [Fact]
    public void The_status_line_keeps_activity_at_the_right_edge()
    {
        var message = TerminalText.Cell("37 in INBOX", 80);

        var line = Screen.StatusLine(message, "sync INBOX 10/40", 80);

        Assert.Equal(80, line.Columns);
        Assert.EndsWith("sync INBOX 10/40", line.Text, StringComparison.Ordinal);
        Assert.StartsWith("37 in INBOX", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_activity_is_clipped_to_its_share_of_the_row()
    {
        var message = TerminalText.Cell("37 in INBOX", 60);
        var activity = "sync " + new string('x', 200);

        var line = Screen.StatusLine(message, activity, 60);

        Assert.Equal(60, line.Columns);
        Assert.StartsWith("37 in INBOX", line.Text, StringComparison.Ordinal);
        Assert.True(line.Text.TrimEnd().Length <= 60);
    }

    [Fact]
    public void A_narrow_terminal_drops_the_activity_not_the_message()
    {
        var message = TerminalText.Cell("37 in INBOX", 39);

        var line = Screen.StatusLine(message, "sync INBOX 10/40", Screen.ActivityMinColumns - 1);

        Assert.Equal(TerminalText.Pad(message, Screen.ActivityMinColumns - 1).Text, line.Text);
    }

    [Fact]
    public void No_activity_leaves_the_row_as_it_was()
    {
        var message = TerminalText.Cell("37 in INBOX", 80);

        Assert.Equal(TerminalText.Pad(message, 80).Text, Screen.StatusLine(message, null, 80).Text);
    }

    [Fact]
    public void A_hostile_folder_name_reaches_the_status_line_defanged()
    {
        var indicators = new SyncIndicators();
        indicators.Apply(Envelopes(1, 12, "\u001b[2J‮\u0007INBOX\r\n", 1, 2), T0);

        var line = Screen.StatusLine(SafeSpan.Empty, indicators.Describe(), 80);

        Assert.DoesNotContain('\u001b', line.Text);
        Assert.DoesNotContain('‮', line.Text);
        Assert.DoesNotContain('\u0007', line.Text);
        Assert.DoesNotContain('\r', line.Text);
        Assert.DoesNotContain('\n', line.Text);
        Assert.Equal(80, line.Columns);
    }

    [Fact]
    public void The_app_turns_a_progress_notification_into_an_indicator()
    {
        var app = NewApp();
        using var document = JsonDocument.Parse(
            """{"jsonrpc":"2.0","method":"notify.sync.progress","params":{"accountId":1,"folderId":12,"folderName":"INBOX","phase":"envelopes","done":5,"total":10,"final":false,"atUtc":"2026-09-04T00:00:00.000Z"}}""");

        AppType.GetMethod("Absorb", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(app, [document]);
        var applies = (ConcurrentQueue<Action>)Field("_applies").GetValue(app)!;
        while (applies.TryDequeue(out var apply)) apply();

        var state = (AppState)Field("_state").GetValue(app)!;
        Assert.Contains("INBOX 5/10", state.Syncing.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_sync_request_can_ask_for_progress_and_a_plain_one_does_not()
    {
        var asked = JsonSerializer.Deserialize(
            """{"accountId":1,"folderId":2,"progress":true}""", ProtocolJsonContext.Default.SyncParams)!;
        var plain = JsonSerializer.Deserialize(
            """{"accountId":1}""", ProtocolJsonContext.Default.SyncParams)!;

        Assert.True(asked.Progress);
        Assert.False(plain.Progress);
    }

    private static SyncProgressNotification Envelopes(
        long account, long folder, string name, int done, int? total, bool final = false) => new()
    {
        AccountId = account,
        FolderId = folder,
        FolderName = name,
        Phase = SyncPhases.Envelopes,
        Done = done,
        Total = total,
        Final = final,
        AtUtc = "2026-09-04T00:00:00.000Z",
    };

    private static SyncProgressNotification Flags(long account, long folder, string name, int done, int? total) =>
        Envelopes(account, folder, name, done, total) with { Phase = SyncPhases.Flags };

    private static SyncProgressNotification Walk(long account, int done, int total, bool final = false) => new()
    {
        AccountId = account,
        Phase = SyncPhases.Folders,
        Done = done,
        Total = total,
        Final = final,
        AtUtc = "2026-09-04T00:00:00.000Z",
    };

    private static object NewApp()
    {
        var app = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(AppType);
        Field("_state").SetValue(app, new AppState());
        Field("_applies").SetValue(app, new ConcurrentQueue<Action>());
        Field("_notices").SetValue(app, new HashSet<string>(StringComparer.Ordinal));
        return app;
    }

    private static FieldInfo Field(string name) =>
        AppType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"App.{name} moved; this test asserts nothing.");

    private static readonly Type AppType =
        typeof(AppState).Assembly.GetType("Mailcoded.Tui.App", throwOnError: true)!;
}
