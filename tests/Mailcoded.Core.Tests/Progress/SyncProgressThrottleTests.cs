using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Progress;

public sealed class SyncProgressThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(SyncProgressThrottle.DefaultIntervalMs);

    [Fact]
    public void The_first_report_for_an_account_is_published_at_once()
    {
        var (run, published, _) = Rig();

        run.Report(Envelopes(1, 10, 100));

        Assert.Equal([10], published.Select(p => p.Done));
    }

    [Fact]
    public void A_report_inside_the_interval_is_held_back()
    {
        var (run, published, clock) = Rig();

        run.Report(Envelopes(1, 10, 100));
        clock.Advance(Interval - TimeSpan.FromMilliseconds(1));
        run.Report(Envelopes(1, 20, 100));

        Assert.Equal([10], published.Select(p => p.Done));
    }

    [Fact]
    public void The_next_admitted_report_also_publishes_every_indicator_held_back_meanwhile()
    {
        var (run, published, clock) = Rig();

        run.Report(Walk(1, 0, 3));
        run.Report(Envelopes(1, 10, 100));
        run.Report(Walk(1, 1, 3));
        clock.Advance(Interval);
        run.Report(Envelopes(1, 30, 100));

        Assert.Equal(
            [(SyncPhase.Folders, 0), (SyncPhase.Folders, 1), (SyncPhase.Envelopes, 30)],
            published.Select(p => (p.Phase, p.Done)));
    }

    [Fact]
    public void A_final_is_published_at_once_even_inside_the_interval()
    {
        var (run, published, _) = Rig();

        run.Report(Envelopes(1, 10, 100));
        run.Report(Envelopes(1, 100, 100, final: true));

        Assert.Equal([false, true], published.Select(p => p.Final));
    }

    [Fact]
    public void A_final_for_an_indicator_never_shown_is_not_published()
    {
        var (run, published, _) = Rig();

        run.Report(Walk(1, 0, 3));
        run.Report(Envelopes(1, 10, 100));
        run.Report(Envelopes(1, 100, 100, final: true));

        Assert.Equal([SyncPhase.Folders], published.Select(p => p.Phase));
    }

    [Fact]
    public void A_final_with_no_report_before_it_is_ignored()
    {
        var (run, published, _) = Rig();

        run.Report(Envelopes(1, 100, 100, final: true));

        Assert.Empty(published);
    }

    [Fact]
    public void Complete_closes_every_shown_indicator_with_its_latest_snapshot()
    {
        var (run, published, clock) = Rig();

        run.Report(Envelopes(1, 10, 100));
        clock.Advance(Interval - TimeSpan.FromMilliseconds(1));
        run.Report(Envelopes(1, 40, 100));
        run.Complete();

        var last = published[^1];
        Assert.True(last.Final);
        Assert.Equal(40, last.Done);
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public void Complete_does_not_close_an_indicator_the_client_never_saw()
    {
        var (run, published, _) = Rig();

        run.Report(Walk(1, 0, 3));
        run.Report(Envelopes(1, 10, 100));
        run.Complete();

        Assert.Equal([SyncPhase.Folders, SyncPhase.Folders], published.Select(p => p.Phase));
        Assert.Equal([false, true], published.Select(p => p.Final));
    }

    [Fact]
    public void Complete_after_every_final_publishes_nothing()
    {
        var (run, published, _) = Rig();

        run.Report(Envelopes(1, 10, 100));
        run.Report(Envelopes(1, 100, 100, final: true));
        var before = published.Count;
        run.Complete();

        Assert.Equal(before, published.Count);
    }

    [Fact]
    public void Complete_twice_publishes_once()
    {
        var (run, published, _) = Rig();

        run.Report(Envelopes(1, 10, 100));
        run.Complete();
        run.Complete();

        Assert.Single(published, p => p.Final);
    }

    [Fact]
    public void Two_runs_on_one_account_share_one_interval()
    {
        var clock = new TestClock();
        var throttle = new SyncProgressThrottle(clock);
        var first = new List<SyncProgress>();
        var second = new List<SyncProgress>();
        var a = throttle.Begin(first.Add);
        var b = throttle.Begin(second.Add);

        a.Report(Envelopes(1, 10, 100));
        b.Report(Envelopes(1, 5, 50));

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void Accounts_do_not_throttle_each_other()
    {
        var (run, published, _) = Rig();

        run.Report(Envelopes(1, 10, 100));
        run.Report(Envelopes(2, 5, 50));

        Assert.Equal([1L, 2L], published.Select(p => p.AccountId.Value));
    }

    [Fact]
    public void A_wall_clock_jump_neither_opens_nor_closes_the_interval()
    {
        var (run, published, clock) = Rig();

        run.Report(Envelopes(1, 10, 100));
        clock.UtcNow += TimeSpan.FromHours(1);
        run.Report(Envelopes(1, 20, 100));
        Assert.Single(published);

        clock.UtcNow -= TimeSpan.FromHours(2);
        run.Report(Envelopes(1, 30, 100));
        Assert.Single(published);

        clock.Advance(Interval);
        run.Report(Envelopes(1, 40, 100));
        Assert.Equal([10, 40], published.Select(p => p.Done));
    }

    [Fact]
    public void The_folder_walk_and_a_folder_are_separate_indicators()
    {
        var (run, published, _) = Rig();

        run.Report(Walk(1, 0, 3));
        run.Report(Envelopes(1, 10, 100));
        run.Report(Walk(1, 3, 3, final: true));
        run.Complete();

        Assert.Equal(
            [(SyncPhase.Folders, false), (SyncPhase.Folders, true)],
            published.Select(p => (p.Phase, p.Final)));
    }

    [Fact]
    public void A_negative_interval_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyncProgressThrottle(new TestClock(), -1));
    }

    private static (SyncProgressRun Run, List<SyncProgress> Published, TestClock Clock) Rig()
    {
        var clock = new TestClock();
        var published = new List<SyncProgress>();
        return (new SyncProgressThrottle(clock).Begin(published.Add), published, clock);
    }

    private static SyncProgress Envelopes(long account, int done, int total, bool final = false) => new()
    {
        AccountId = new AccountId(account),
        Phase = SyncPhase.Envelopes,
        FolderId = new FolderId(12),
        FolderPath = "INBOX",
        Done = done,
        Total = total,
        Final = final,
    };

    private static SyncProgress Walk(long account, int done, int total, bool final = false) => new()
    {
        AccountId = new AccountId(account),
        Phase = SyncPhase.Folders,
        Done = done,
        Total = total,
        Final = final,
    };
}
