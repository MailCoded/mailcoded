using Mailcoded.Cli;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Cli;

public sealed class SyncProgressLineTests
{
    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void Progress_is_drawn_only_for_a_human_at_a_terminal(bool json, bool quiet, bool tty, bool expected)
    {
        Assert.Equal(expected, SyncProgressLine.Enabled(json, quiet, tty));
    }

    [Fact]
    public void The_line_shows_the_folder_walk_then_the_folder()
    {
        var line = SyncProgressLine.Format([Walk(3, 12), Envelopes("INBOX", 12000, 48000)], 80);

        Assert.Equal("sync 3/12 folders, INBOX 12000/48000", line);
    }

    [Fact]
    public void A_count_without_a_total_is_shown_bare()
    {
        var line = SyncProgressLine.Format([Flags("Archive", 3000, null)], 80);

        Assert.Equal("sync Archive flags 3000", line);
    }

    [Fact]
    public void Nothing_open_draws_nothing()
    {
        Assert.Equal(string.Empty, SyncProgressLine.Format([], 80));
    }

    [Fact]
    public void A_hostile_folder_name_never_reaches_the_terminal()
    {
        var line = SyncProgressLine.Format([Envelopes("\u001b[2J‮INBOX\u0007\r\n", 1, 2)], 80);

        Assert.DoesNotContain('\u001b', line);
        Assert.DoesNotContain('‮', line);
        Assert.DoesNotContain('\u0007', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("INBOX", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_stops_short_of_the_last_column()
    {
        var line = SyncProgressLine.Format([Envelopes(new string('a', 100), 1, 2)], 30);

        Assert.True(line.Length <= 29);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var name = "ab" + string.Concat(Enumerable.Repeat("\U0001F4E5", 30));

        for (var width = 20; width < 50; width++)
        {
            var line = SyncProgressLine.Format([Envelopes(name, 1, 2)], width);

            Assert.True(line.Length <= width - 1);
            Assert.False(line.Length > 0 && char.IsHighSurrogate(line[^1]), $"width {width} cut a pair in half");
        }
    }

    [Fact]
    public void A_narrow_terminal_gets_no_line()
    {
        Assert.Equal(string.Empty, SyncProgressLine.Format([Walk(1, 2)], 19));
    }

    [Fact]
    public void Finish_leaves_the_cursor_at_column_zero_of_a_blank_line()
    {
        var error = new StringWriter();
        var drawing = new SyncProgressLine(new TestClock(), error, 80);

        drawing.Progress.Report(Walk(1, 3));
        drawing.Progress.Report(Envelopes("INBOX", 10, 40));
        drawing.Finish();

        Assert.Equal("\rsync 1/3 folders" + "\r" + new string(' ', "sync 1/3 folders".Length) + "\r", error.ToString());
    }

    [Fact]
    public void A_shorter_line_blanks_what_the_longer_one_left_behind()
    {
        var clock = new TestClock();
        var error = new StringWriter();
        var drawing = new SyncProgressLine(clock, error, 80);

        drawing.Progress.Report(Walk(1, 3));
        clock.Advance(TimeSpan.FromSeconds(1));
        drawing.Progress.Report(Envelopes("INBOX", 10, 40));
        var longer = "sync 1/3 folders, INBOX 10/40";
        var shorter = "sync INBOX 10/40";
        drawing.Progress.Report(Walk(3, 3, final: true));

        Assert.EndsWith("\r" + shorter + new string(' ', longer.Length - shorter.Length), error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_sync_that_reported_nothing_writes_nothing()
    {
        var error = new StringWriter();
        var drawing = new SyncProgressLine(new TestClock(), error, 80);

        drawing.Finish();

        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void A_failing_terminal_write_never_fails_the_sync()
    {
        var drawing = new SyncProgressLine(new TestClock(), new ThrowingWriter(), 80);

        drawing.Progress.Report(Walk(1, 3));
        drawing.Progress.Report(Walk(2, 3, final: true));
        drawing.Finish();
    }

    [Fact]
    public void A_final_removes_its_part_of_the_line()
    {
        var error = new StringWriter();
        var drawing = new SyncProgressLine(new TestClock(), error, 80);

        drawing.Progress.Report(Walk(1, 3));
        drawing.Progress.Report(Envelopes("INBOX", 10, 40, final: false));
        drawing.Progress.Report(Envelopes("INBOX", 40, 40, final: true));
        drawing.Finish();

        Assert.DoesNotContain("INBOX 40/40", error.ToString(), StringComparison.Ordinal);
    }

    private static SyncProgress Walk(int done, int total, bool final = false) => new()
    {
        AccountId = new AccountId(1),
        Phase = SyncPhase.Folders,
        Done = done,
        Total = total,
        Final = final,
    };

    private static SyncProgress Envelopes(string path, int done, int? total, bool final = false) => new()
    {
        AccountId = new AccountId(1),
        Phase = SyncPhase.Envelopes,
        FolderId = new FolderId(12),
        FolderPath = path,
        Done = done,
        Total = total,
        Final = final,
    };

    private static SyncProgress Flags(string path, int done, int? total) =>
        Envelopes(path, done, total) with { Phase = SyncPhase.Flags };

    private sealed class ThrowingWriter : StringWriter
    {
        public override void Write(string? value) => throw new IOException("closed");

        public override void Flush() => throw new IOException("closed");
    }
}
