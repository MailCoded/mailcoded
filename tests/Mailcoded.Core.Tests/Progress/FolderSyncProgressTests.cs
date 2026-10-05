using System.Reflection;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Progress;

public sealed class FolderSyncProgressTests
{
    private static readonly FolderSummary Inbox = new()
    {
        Id = new FolderId(12),
        AccountId = new AccountId(1),
        Path = FolderPath.Create("INBOX"),
    };

    private static readonly SyncPlan Initial = new SyncPlan.Backfill(new Uid(1), new Uid(1));

    [Fact]
    public void Envelopes_report_what_the_folder_holds_against_what_the_server_holds()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(0, 1200, Initial);
        tracker.Flushed(new SyncCounts(500, 0, 0), new SyncCounts(500, 0, 0));

        var report = Assert.Single(recorder.Reports);
        Assert.Equal(SyncPhase.Envelopes, report.Phase);
        Assert.Equal(500, report.Done);
        Assert.Equal(1200, report.Total);
        Assert.False(report.Final);
        Assert.Equal(new FolderId(12), report.FolderId);
        Assert.Equal("INBOX", report.FolderPath);
    }

    [Fact]
    public void Total_is_never_below_done()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(0, 10, Initial);
        tracker.Flushed(new SyncCounts(12, 0, 0), new SyncCounts(12, 0, 0));

        Assert.Equal((12, 12), (recorder.Reports[0].Done, recorder.Reports[0].Total));
    }

    [Fact]
    public void Envelopes_already_held_do_not_move_done()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(300, 300, Initial);
        tracker.Flushed(new SyncCounts(500, 0, 0), new SyncCounts(0, 500, 0));

        var report = Assert.Single(recorder.Reports);
        Assert.Equal(300, report.Done);
    }

    [Fact]
    public void Removed_messages_lower_done()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(100, 100, Initial);
        tracker.Flushed(new SyncCounts(1, 0, 0), new SyncCounts(1, 0, 0));
        tracker.Flushed(new SyncCounts(1, 0, 5), new SyncCounts(1, 0, 5));

        Assert.Equal([101, 97], recorder.Reports.Select(r => r.Done));
    }

    [Fact]
    public void Done_never_goes_below_zero()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(0, 0, Initial);
        tracker.Flushed(new SyncCounts(1, 0, 0), new SyncCounts(0, 0, 9));

        Assert.Equal(0, recorder.Reports[0].Done);
    }

    [Fact]
    public void A_flush_without_envelope_events_reports_no_envelopes()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(10, 10, Initial);
        tracker.Flushed(new SyncCounts(0, 3, 1), new SyncCounts(0, 3, 1));

        Assert.DoesNotContain(recorder.Reports, r => r.Phase == SyncPhase.Envelopes);
    }

    [Fact]
    public void Flags_have_a_total_for_a_full_diff()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(3, 3, SyncPlan.Full([new Uid(1), new Uid(2), new Uid(3)]));
        tracker.Flushed(new SyncCounts(0, 2, 0), new SyncCounts(0, 2, 0));

        var report = Assert.Single(recorder.Reports);
        Assert.Equal((SyncPhase.Flags, 2, 3), (report.Phase, report.Done, report.Total));
    }

    [Theory]
    [InlineData("qresync")]
    [InlineData("condstore")]
    public void Flags_have_no_total_on_a_delta_plan(string kind)
    {
        var (tracker, recorder) = Rig();
        SyncPlan plan = kind == "qresync"
            ? SyncPlan.Qresync(new ModSeq(5), new UidValidity(1), new Uid(1))
            : SyncPlan.Condstore(new ModSeq(5), new Uid(2));

        tracker.Begin(3, 3, plan);
        tracker.Flushed(new SyncCounts(0, 2, 0), new SyncCounts(0, 2, 0));

        Assert.Null(recorder.Reports[0].Total);
        Assert.Equal(2, recorder.Reports[0].Done);
    }

    [Fact]
    public void Flags_count_accumulates_across_flushes_and_plans()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(5, 5, SyncPlan.Full([new Uid(1), new Uid(2), new Uid(3), new Uid(4)]));
        tracker.Flushed(new SyncCounts(0, 2, 0), new SyncCounts(0, 2, 0));
        tracker.Flushed(new SyncCounts(0, 1, 0), new SyncCounts(0, 1, 0));
        tracker.Begin(5, 5, SyncPlan.Full([new Uid(1)]));
        tracker.Flushed(new SyncCounts(0, 2, 0), new SyncCounts(0, 2, 0));

        Assert.Equal([2, 3, 5], recorder.Reports.Select(r => r.Done));
        Assert.Equal<int?>([4, 4, 5], recorder.Reports.Select(r => r.Total));
    }

    [Fact]
    public void Finish_closes_only_the_phases_that_reported()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(0, 5, Initial);
        tracker.Flushed(new SyncCounts(5, 0, 0), new SyncCounts(5, 0, 0));
        recorder.Reports.Clear();

        tracker.Finish();

        var closing = Assert.Single(recorder.Reports);
        Assert.True(closing.Final);
        Assert.Equal(SyncPhase.Envelopes, closing.Phase);
        Assert.Equal(5, closing.Done);
    }

    [Fact]
    public void Finish_with_nothing_reported_closes_nothing()
    {
        var (tracker, recorder) = Rig();

        tracker.Begin(5, 5, Initial);
        tracker.Finish();

        Assert.Empty(recorder.Reports);
    }

    [Fact]
    public void Without_a_sink_nothing_is_reported_and_nothing_throws()
    {
        var tracker = new FolderSyncProgress(null, Inbox.AccountId, Inbox);

        tracker.Begin(0, 10, Initial);
        tracker.Flushed(new SyncCounts(3, 2, 0), new SyncCounts(3, 0, 0));
        tracker.Finish();
    }

    [Fact]
    public void A_report_carries_identifiers_and_counts_only()
    {
        var names = typeof(SyncProgress)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            new[] { "AccountId", "Done", "Final", "FolderId", "FolderPath", "Phase", "Total" },
            names);
    }

    private static (FolderSyncProgress Tracker, ProgressRecorder Recorder) Rig()
    {
        var recorder = new ProgressRecorder();
        return (new FolderSyncProgress(recorder, Inbox.AccountId, Inbox), recorder);
    }
}
