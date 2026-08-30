using MailKit;
using MailKit.Net.Imap;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Core.Providers;

public sealed partial class ImapProvider
{
    public async Task WatchAsync(FolderRef folder, Func<CancellationToken, Task> onChange, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var cfg = config ?? throw new ProviderException(FailureCategory.Network, "Connect before watching a folder.");
        var store = secrets ?? throw new ProviderException(FailureCategory.Network, "Connect before watching a folder.");

        var cap = EffectiveWatchCap();
        if (Interlocked.Increment(ref activeWatchers) > cap)
        {
            Interlocked.Decrement(ref activeWatchers);
            throw new ProviderException(FailureCategory.Busy, $"This account allows at most {cap} watched folders.");
        }

        try
        {
            await WatchLoopAsync(cfg, store, folder, onChange, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref activeWatchers);
        }
    }

    /// <summary>11: consumer servers such as Yahoo and Gmail cap simultaneous connections hard.</summary>
    private int EffectiveWatchCap() =>
        quirks.HasFlag(ServerQuirks.LowConnectionLimit)
            ? Math.Max(1, Math.Min(options.MaxWatchedFolders, options.LowConnectionWatchCap))
            : Math.Max(1, options.MaxWatchedFolders);

    private async Task WatchLoopAsync(
        AccountConfig cfg,
        ISecretStore store,
        FolderRef folder,
        Func<CancellationToken, Task> onChange,
        CancellationToken ct)
    {
        var policy = new ReconnectPolicy(clock, options.CreateRandom());

        while (!ct.IsCancellationRequested)
        {
            ImapConnection? session = null;
            var notifier = new IdleNotifier(clock, options.IdleQuietMs, options.IdleMaxCoalesceMs);

            try
            {
                // A dedicated client per watched folder: a connection parked in IDLE cannot also
                // serve on-demand commands.
                session = await ImapConnectionFactory.ConnectAsync(cfg, store, options, ct).ConfigureAwait(false);

                var watched = await ResolveWatchFolderAsync(session.Client, folder.Path, ct).ConfigureAwait(false);
                await watched.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
                notifier.Attach(watched);

                policy.RecordSuccess();
                await IdleLoopAsync(session.Client, notifier, onChange, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var category = ex is ProviderException provider ? provider.Category : ProviderErrors.CategorizeImap(ex);
                var decision = policy.OnFailure(category);

                if (!decision.ShouldRetry)
                {
                    if (decision.RequiresUserAction)
                    {
                        throw new ProviderException(
                            FailureCategory.Auth,
                            $"Re-authorization is required for this account ({RetryDecision.AuthRequired}).",
                            ex);
                    }

                    throw ex as ProviderException ?? ProviderErrors.Imap(ex, "watch");
                }

                await DelayAsync(decision.Delay, ct).ConfigureAwait(false);
            }
            finally
            {
                notifier.Detach();
                if (session is not null) await ImapConnectionFactory.CloseAsync(session.Client).ConfigureAwait(false);
            }
        }
    }

    private async Task IdleLoopAsync(
        ImapClient client,
        IdleNotifier notifier,
        Func<CancellationToken, Task> onChange,
        CancellationToken ct)
    {
        var lastWall = clock.UtcNow;
        var lastTicks = clock.Ticks;

        while (!ct.IsCancellationRequested)
        {
            if (client.Capabilities.HasFlag(ImapCapabilities.Idle))
            {
                // Re-issued every 9 minutes; Gmail drops an idle connection around 10.
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                notifier.BeginCycle(cycle);
                try
                {
                    cycle.CancelAfter(options.IdleReissueMs);
                    await client.IdleAsync(cycle.Token, ct).ConfigureAwait(false);
                }
                finally
                {
                    notifier.EndCycle();
                }
            }
            else
            {
                await Task.Delay(options.PollIntervalMs, ct).ConfigureAwait(false);
                await client.NoOpAsync(ct).ConfigureAwait(false);
            }

            var wall = clock.UtcNow;
            var ticks = clock.Ticks;
            var wallDelta = (long)(wall - lastWall).TotalMilliseconds;
            var tickDelta = ticks - lastTicks;
            lastWall = wall;
            lastTicks = ticks;

            // 19: monotonic and wall time diverging means the machine slept. The socket is dead
            // even though IsConnected still reads true, so tear it down instead of trusting it.
            if (Math.Abs(wallDelta - tickDelta) > options.ClockDivergenceMs)
                throw new ProviderException(FailureCategory.Network, "A suspend or resume was detected; reconnecting.");

            if (notifier.TakePending())
                await onChange(ct).ConfigureAwait(false);
        }
    }

    private static async Task<IMailFolder> ResolveWatchFolderAsync(ImapClient client, FolderPath path, CancellationToken ct)
    {
        if (path.IsInbox) return client.Inbox;

        try
        {
            return await client.GetFolderAsync(ToServerPath(path), ct).ConfigureAwait(false);
        }
        catch (FolderNotFoundException ex)
        {
            throw new ProviderException(FailureCategory.NotFound, $"Folder '{FolderLabel(path)}' does not exist on the server.", ex);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Collects MailKit folder notifications during one IDLE cycle and coalesces a storm into a
    /// single wake-up. Every subscription is removed in <see cref="Detach"/> (leak guard, §14.4).
    /// </summary>
    private sealed class IdleNotifier
    {
        private readonly object gate = new();
        private readonly IClock clock;
        private readonly int quietMs;
        private readonly int maxCoalesceMs;

        private IMailFolder? folder;
        private CancellationTokenSource? cycle;
        private long firstSignalTicks;
        private bool armed;
        private bool pending;

        public IdleNotifier(IClock clock, int quietMs, int maxCoalesceMs)
        {
            this.clock = clock;
            this.quietMs = Math.Max(1, quietMs);
            this.maxCoalesceMs = Math.Max(this.quietMs, maxCoalesceMs);
        }

        public void Attach(IMailFolder target)
        {
            lock (gate)
            {
                if (folder is not null) return;
                folder = target;
            }

            target.CountChanged += OnFolderEvent;
            target.RecentChanged += OnFolderEvent;
            target.UidValidityChanged += OnFolderEvent;
            target.MessageExpunged += OnMessageEvent;
            target.MessagesVanished += OnVanished;
            target.MessageFlagsChanged += OnFlagsChanged;
        }

        public void Detach()
        {
            IMailFolder? target;
            lock (gate)
            {
                target = folder;
                folder = null;
                cycle = null;
            }

            if (target is null) return;

            target.CountChanged -= OnFolderEvent;
            target.RecentChanged -= OnFolderEvent;
            target.UidValidityChanged -= OnFolderEvent;
            target.MessageExpunged -= OnMessageEvent;
            target.MessagesVanished -= OnVanished;
            target.MessageFlagsChanged -= OnFlagsChanged;
        }

        public void BeginCycle(CancellationTokenSource source)
        {
            lock (gate)
            {
                cycle = source;
                armed = false;
            }
        }

        public void EndCycle()
        {
            lock (gate)
            {
                cycle = null;
                armed = false;
            }
        }

        public bool TakePending()
        {
            lock (gate)
            {
                var result = pending;
                pending = false;
                return result;
            }
        }

        private void OnFolderEvent(object? sender, EventArgs e) => Signal();

        private void OnMessageEvent(object? sender, MessageEventArgs e) => Signal();

        private void OnVanished(object? sender, MessagesVanishedEventArgs e) => Signal();

        private void OnFlagsChanged(object? sender, MessageFlagsChangedEventArgs e) => Signal();

        private void Signal()
        {
            lock (gate)
            {
                pending = true;

                var source = cycle;
                if (source is null) return;

                var now = clock.Ticks;
                if (!armed)
                {
                    armed = true;
                    firstSignalTicks = now;
                }

                try
                {
                    // 27: a bulk move of 10k messages floods EXISTS/VANISHED. Re-arm a short quiet
                    // window per notification, but never stall past the coalesce ceiling.
                    var elapsed = now - firstSignalTicks;
                    source.CancelAfter(elapsed >= maxCoalesceMs ? 1 : quietMs);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}
