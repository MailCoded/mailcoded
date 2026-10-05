using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Providers;

/// <summary>Opens a second connection only while the first is busy; if it cannot, commands wait on the first.</summary>
internal sealed class InteractiveLaneRouter<TLane> : IAsyncDisposable
    where TLane : class
{
    private readonly IClock clock;
    private readonly ReconnectPolicy policy;
    private readonly Func<bool> primaryBusy;
    private readonly Func<bool> allowed;
    private readonly Func<CancellationToken, Task<TLane>> open;
    private readonly Func<TLane, bool> isUsable;
    private readonly Func<TLane, ValueTask> close;
    private readonly int idleRecycleMs;
    private readonly SemaphoreSlim gate = new(1, 1);

    private TLane? lane;
    private int active;
    private long lastUseTicks;
    private long cooldownUntilTicks;
    private int disposed;

    public InteractiveLaneRouter(
        IClock clock,
        ReconnectPolicy policy,
        Func<bool> primaryBusy,
        Func<bool> allowed,
        Func<CancellationToken, Task<TLane>> open,
        Func<TLane, bool> isUsable,
        Func<TLane, ValueTask> close,
        int idleRecycleMs)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(primaryBusy);
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(isUsable);
        ArgumentNullException.ThrowIfNull(close);

        this.clock = clock;
        this.policy = policy;
        this.primaryBusy = primaryBusy;
        this.allowed = allowed;
        this.open = open;
        this.isUsable = isUsable;
        this.close = close;
        this.idleRecycleMs = Math.Max(0, idleRecycleMs);
    }

    public bool HasLane => Volatile.Read(ref lane) is not null;

    public bool IsCoolingDown => clock.Ticks < Volatile.Read(ref cooldownUntilTicks);

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> onPrimary,
        Func<TLane, CancellationToken, Task<T>> onLane,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onPrimary);
        ArgumentNullException.ThrowIfNull(onLane);

        if (Volatile.Read(ref disposed) != 0 || !primaryBusy() || !allowed())
            return await onPrimary(ct).ConfigureAwait(false);

        var acquired = await AcquireAsync(ct).ConfigureAwait(false);
        if (acquired is null) return await onPrimary(ct).ConfigureAwait(false);

        try
        {
            return await onLane(acquired, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref lastUseTicks, clock.Ticks);
            Interlocked.Decrement(ref active);
        }
    }

    /// <summary>Drops an idle lane and clears the cooldown after the primary reconnected.</summary>
    public async Task ResetAsync()
    {
        if (Volatile.Read(ref disposed) != 0) return;

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Volatile.Write(ref cooldownUntilTicks, 0);
            if (Volatile.Read(ref active) == 0) await CloseLaneAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseLaneAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TLane?> AcquireAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0) return null;

            var now = clock.Ticks;

            if (lane is not null)
            {
                var busy = Volatile.Read(ref active) > 0;
                var idleFor = now - Volatile.Read(ref lastUseTicks);

                if (isUsable(lane) && (busy || idleFor < idleRecycleMs)) return Take(lane, now);

                // A dead socket under a command that is still running is that command's to report.
                if (busy) return null;

                await CloseLaneAsync().ConfigureAwait(false);
            }

            if (now < Volatile.Read(ref cooldownUntilTicks)) return null;

            try
            {
                var opened = await open(ct).ConfigureAwait(false);
                lane = opened;
                policy.RecordSuccess();
                return Take(opened, clock.Ticks);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var category = ex is ProviderException provider ? provider.Category : FailureCategory.Network;
                var decision = policy.OnFailure(category);
                var delayMs = decision.ShouldRetry
                    ? (long)decision.Delay.TotalMilliseconds
                    : ReconnectPolicy.DefaultMaxDelayMs;

                Volatile.Write(ref cooldownUntilTicks, clock.Ticks + delayMs);
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private TLane Take(TLane target, long now)
    {
        Interlocked.Increment(ref active);
        Volatile.Write(ref lastUseTicks, now);
        return target;
    }

    private async Task CloseLaneAsync()
    {
        var closing = lane;
        lane = null;
        if (closing is null) return;

        try
        {
            await close(closing).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The lane is already unreferenced; a failed LOGOUT leaves nothing to recover.
        }
    }
}
