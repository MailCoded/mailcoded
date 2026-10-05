using Mailcoded.Core.Providers;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Providers;

/// <summary>Real command queue as the sync connection, fake lanes as the second connection.</summary>
public sealed class InteractiveLaneRouterTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Blink = TimeSpan.FromMilliseconds(250);

    private sealed class FakeLane(int id)
    {
        public int Id { get; } = id;
        public bool Usable { get; set; } = true;
        public bool Closed { get; set; }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public TestClock Clock { get; } = new();
        public ImapCommandQueue Queue { get; } = new();
        public bool Allowed { get; set; } = true;
        public int Opened { get; private set; }
        public List<FakeLane> Lanes { get; } = [];
        public Func<CancellationToken, Task>? OpenGate { get; set; }
        public Exception? OpenFailure { get; set; }
        public InteractiveLaneRouter<FakeLane> Router { get; }

        public Rig(int idleRecycleMs = 120_000)
        {
            Router = new InteractiveLaneRouter<FakeLane>(
                Clock,
                new ReconnectPolicy(Clock, 7),
                primaryBusy: () => Queue.IsBusy,
                allowed: () => Allowed,
                open: OpenAsync,
                isUsable: lane => lane.Usable,
                close: lane =>
                {
                    lane.Closed = true;
                    return ValueTask.CompletedTask;
                },
                idleRecycleMs);
        }

        private async Task<FakeLane> OpenAsync(CancellationToken ct)
        {
            Opened++;
            if (OpenGate is not null) await OpenGate(ct);
            if (OpenFailure is not null) throw OpenFailure;

            var lane = new FakeLane(Opened);
            Lanes.Add(lane);
            return lane;
        }

        public Task<string> CommandAsync(CancellationToken ct) =>
            Router.RunAsync(
                token => Queue.RunAsync(_ => Task.FromResult("primary"), token),
                (lane, _) => Task.FromResult($"lane-{lane.Id}"),
                ct);

        public (TaskCompletionSource Release, Task Held) HoldPrimary()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = Queue.RunAsync(_ => release.Task, CancellationToken.None);
            Assert.True(Queue.IsBusy);
            return (release, held);
        }

        public async ValueTask DisposeAsync()
        {
            await Router.DisposeAsync();
            Queue.Dispose();
        }
    }

    [Fact]
    public async Task An_idle_primary_serves_the_command_and_no_second_connection_opens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();

        Assert.Equal("primary", await rig.CommandAsync(ct));

        Assert.Equal(0, rig.Opened);
        Assert.False(rig.Router.HasLane);
    }

    [Fact]
    public async Task A_command_completes_on_the_lane_while_a_long_sync_holds_the_primary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        var (release, held) = rig.HoldPrimary();

        var result = await rig.CommandAsync(ct).WaitAsync(Patience, ct);

        Assert.Equal("lane-1", result);
        Assert.False(held.IsCompleted);

        release.SetResult();
        await held.WaitAsync(Patience, ct);
    }

    [Fact]
    public async Task A_streaming_sync_that_never_ends_does_not_block_the_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<int> Endless([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            var n = 0;
            while (!stop.Task.IsCompleted)
            {
                yield return n++;
                await Task.Delay(5, token);
            }
        }

        var sync = Task.Run(async () =>
        {
            await foreach (var _ in rig.Queue.RunStreamAsync(Endless, CancellationToken.None))
                consumed.TrySetResult();
        }, ct);

        await consumed.Task.WaitAsync(Patience, ct);

        Assert.Equal("lane-1", await rig.CommandAsync(ct).WaitAsync(Patience, ct));

        stop.SetResult();
        await sync.WaitAsync(Patience, ct);
    }

    [Fact]
    public async Task Without_the_lane_the_same_command_waits_behind_the_sync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        rig.Allowed = false;
        var (release, held) = rig.HoldPrimary();

        var command = rig.CommandAsync(ct);

        Assert.NotSame(command, await Task.WhenAny(command, Task.Delay(Blink, ct)));
        Assert.Equal(0, rig.Opened);

        release.SetResult();
        Assert.Equal("primary", await command.WaitAsync(Patience, ct));
        await held;
    }

    [Fact]
    public async Task The_lane_is_reused_by_later_commands()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        var (release, held) = rig.HoldPrimary();

        Assert.Equal("lane-1", await rig.CommandAsync(ct));
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("lane-1", await rig.CommandAsync(ct));

        Assert.Equal(1, rig.Opened);
        release.SetResult();
        await held;
    }

    [Fact]
    public async Task Concurrent_commands_open_one_lane_between_them()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        var (release, held) = rig.HoldPrimary();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => rig.CommandAsync(ct), ct)));

        Assert.Equal(1, rig.Opened);
        Assert.All(results, r => Assert.Equal("lane-1", r));
        release.SetResult();
        await held;
    }

    [Fact]
    public async Task A_refused_second_connection_falls_back_to_the_primary_and_backs_off()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        rig.OpenFailure = new ProviderException(FailureCategory.Busy, "too many simultaneous connections");
        var (release, held) = rig.HoldPrimary();

        var first = rig.CommandAsync(ct);
        release.SetResult();
        await held;
        Assert.Equal("primary", await first.WaitAsync(Patience, ct));
        Assert.True(rig.Router.IsCoolingDown);
        Assert.Equal(1, rig.Opened);

        var (release2, held2) = rig.HoldPrimary();
        var second = rig.CommandAsync(ct);
        release2.SetResult();
        await held2;
        Assert.Equal("primary", await second.WaitAsync(Patience, ct));
        Assert.Equal(1, rig.Opened);
    }

    [Fact]
    public async Task The_lane_is_tried_again_once_the_backoff_has_passed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        rig.OpenFailure = new ProviderException(FailureCategory.Network, "connection refused");
        var (release, held) = rig.HoldPrimary();

        var first = rig.CommandAsync(ct);
        release.SetResult();
        await held;
        await first.WaitAsync(Patience, ct);

        rig.OpenFailure = null;
        rig.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(rig.Router.IsCoolingDown);

        var (release2, held2) = rig.HoldPrimary();
        Assert.Equal("lane-2", await rig.CommandAsync(ct).WaitAsync(Patience, ct));
        release2.SetResult();
        await held2;
    }

    [Fact]
    public async Task A_failed_login_on_the_lane_still_falls_back_and_waits_out_the_longer_auth_backoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        rig.OpenFailure = new ProviderException(FailureCategory.Auth, "login refused");
        var (release, held) = rig.HoldPrimary();

        var first = rig.CommandAsync(ct);
        release.SetResult();
        await held;
        Assert.Equal("primary", await first.WaitAsync(Patience, ct));

        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(rig.Router.IsCoolingDown);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(rig.Router.IsCoolingDown);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_provider_error_is_treated_as_a_network_fault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        rig.OpenFailure = new IOException("reset");
        var (release, held) = rig.HoldPrimary();

        var first = rig.CommandAsync(ct);
        release.SetResult();
        await held;

        Assert.Equal("primary", await first.WaitAsync(Patience, ct));
        Assert.True(rig.Router.IsCoolingDown);
    }

    [Fact]
    public async Task Cancelling_while_the_lane_opens_propagates_and_does_not_start_a_backoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        rig.OpenGate = token => Task.Delay(Timeout.Infinite, token);
        var (release, held) = rig.HoldPrimary();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var command = rig.CommandAsync(cts.Token);
        await Task.Delay(Blink, ct);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        Assert.False(rig.Router.IsCoolingDown);

        release.SetResult();
        await held;
    }

    [Fact]
    public async Task A_lane_idle_past_the_limit_is_closed_and_replaced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig(idleRecycleMs: 60_000);
        var (release, held) = rig.HoldPrimary();

        Assert.Equal("lane-1", await rig.CommandAsync(ct));
        rig.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("lane-2", await rig.CommandAsync(ct));

        Assert.True(rig.Lanes[0].Closed);
        Assert.False(rig.Lanes[1].Closed);
        release.SetResult();
        await held;
    }

    [Fact]
    public async Task A_dead_lane_is_replaced_rather_than_handed_out()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        var (release, held) = rig.HoldPrimary();

        Assert.Equal("lane-1", await rig.CommandAsync(ct));
        rig.Lanes[0].Usable = false;
        Assert.Equal("lane-2", await rig.CommandAsync(ct));

        Assert.True(rig.Lanes[0].Closed);
        release.SetResult();
        await held;
    }

    [Fact]
    public async Task A_lane_with_a_command_still_running_is_never_recycled_for_being_quiet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig(idleRecycleMs: 60_000);
        var (release, held) = rig.HoldPrimary();
        var longRunning = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attachment = rig.Router.RunAsync(
            token => Task.FromResult("primary"),
            async (lane, _) =>
            {
                started.SetResult();
                return $"{await longRunning.Task}-{lane.Id}";
            },
            ct);
        await started.Task.WaitAsync(Patience, ct);

        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("lane-1", await rig.CommandAsync(ct).WaitAsync(Patience, ct));
        Assert.False(rig.Lanes[0].Closed);

        longRunning.SetResult("done");
        Assert.Equal("done-1", await attachment.WaitAsync(Patience, ct));
        release.SetResult();
        await held;
    }

    [Fact]
    public async Task A_dead_lane_under_a_running_command_sends_new_commands_to_the_primary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        var (release, held) = rig.HoldPrimary();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = rig.Router.RunAsync(
            token => Task.FromResult(0),
            async (_, _) =>
            {
                started.SetResult();
                await finish.Task;
                return 1;
            },
            ct);
        await started.Task.WaitAsync(Patience, ct);
        rig.Lanes[0].Usable = false;

        release.SetResult();
        await held;
        var (release2, held2) = rig.HoldPrimary();
        var next = rig.CommandAsync(ct);
        release2.SetResult();
        await held2;

        Assert.Equal("primary", await next.WaitAsync(Patience, ct));
        Assert.False(rig.Lanes[0].Closed);

        finish.SetResult();
        Assert.Equal(1, await running.WaitAsync(Patience, ct));
    }

    [Fact]
    public async Task Reset_closes_an_idle_lane_and_clears_a_backoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        var (release, held) = rig.HoldPrimary();
        await rig.CommandAsync(ct);

        await rig.Router.ResetAsync();

        Assert.True(rig.Lanes[0].Closed);
        Assert.False(rig.Router.HasLane);

        rig.OpenFailure = new ProviderException(FailureCategory.Network, "down");
        var fallback = rig.CommandAsync(ct);
        release.SetResult();
        await held;
        await fallback.WaitAsync(Patience, ct);
        Assert.True(rig.Router.IsCoolingDown);

        await rig.Router.ResetAsync();
        Assert.False(rig.Router.IsCoolingDown);
    }

    [Fact]
    public async Task Reset_leaves_a_lane_alone_while_a_command_is_running_on_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig();
        var (release, held) = rig.HoldPrimary();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = rig.Router.RunAsync(
            token => Task.FromResult(0),
            async (_, _) =>
            {
                started.SetResult();
                await finish.Task;
                return 1;
            },
            ct);
        await started.Task.WaitAsync(Patience, ct);

        await rig.Router.ResetAsync();

        Assert.False(rig.Lanes[0].Closed);
        finish.SetResult();
        await running.WaitAsync(Patience, ct);
        release.SetResult();
        await held;
    }

    [Fact]
    public async Task Disposing_closes_the_lane_and_later_commands_use_the_primary()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var (release, held) = rig.HoldPrimary();
        await rig.CommandAsync(ct);

        await rig.Router.DisposeAsync();

        Assert.True(rig.Lanes[0].Closed);
        release.SetResult();
        await held;
        Assert.Equal("primary", await rig.CommandAsync(ct));
        rig.Queue.Dispose();
    }

    [Fact]
    public async Task A_failing_command_on_the_lane_releases_it_for_the_next()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig(idleRecycleMs: 60_000);
        var (release, held) = rig.HoldPrimary();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Router.RunAsync<int>(
            token => Task.FromResult(0),
            (_, _) => throw new InvalidOperationException("boom"),
            ct));

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("lane-2", await rig.CommandAsync(ct));
        Assert.True(rig.Lanes[0].Closed);

        release.SetResult();
        await held;
    }

    [Fact]
    public async Task The_queue_reports_busy_only_while_it_holds_a_command()
    {
        var ct = TestContext.Current.CancellationToken;
        using var queue = new ImapCommandQueue();
        Assert.False(queue.IsBusy);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = queue.RunAsync(_ => release.Task, ct);
        Assert.True(queue.IsBusy);

        release.SetResult();
        await running;
        Assert.False(queue.IsBusy);
    }
}
