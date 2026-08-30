using System.Collections.Concurrent;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Daemon;

/// <summary>
/// Turns IMAP IDLE into <c>notify.mail.added</c>, <c>notify.folder.updated</c>, and
/// <c>notify.sync.error</c> on the stdio channel. A bulk move of ten thousand messages produces one
/// notification per folder, not ten thousand (edge case 27): while a folder's sync is running,
/// further IDLE signals only raise a dirty bit that schedules exactly one follow-up pass.
/// </summary>
internal sealed class WatchCoordinator : IAsyncDisposable
{
    /// <summary>
    /// Extra quiet window an IDLE signal waits out before the sync runs. The provider's own
    /// notifier already debounces; this folds a second burst that lands while the pass is starting.
    /// </summary>
    public const int CoalesceDelayMs = 250;

    private readonly SqliteStore store;
    private readonly SyncEngine sync;
    private readonly ProviderPool providers;
    private readonly ConnectionRegistry connections;
    private readonly RpcChannel channel;
    private readonly IClock clock;
    private readonly StderrLog log;
    private readonly bool watchEnabled;
    private readonly int maxWatchedFolders;

    private readonly ConcurrentDictionary<(long Account, long Folder), Watch> watches = new();
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    public WatchCoordinator(
        SqliteStore store,
        SyncEngine sync,
        ProviderPool providers,
        ConnectionRegistry connections,
        RpcChannel channel,
        IClock clock,
        StderrLog log,
        bool watchEnabled,
        int maxWatchedFolders)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        this.store = store;
        this.sync = sync;
        this.providers = providers;
        this.connections = connections;
        this.channel = channel;
        this.clock = clock;
        this.log = log;
        this.watchEnabled = watchEnabled;
        this.maxWatchedFolders = Math.Max(1, maxWatchedFolders);
    }

    /// <summary>False on a secondary instance: another live daemon already owns this store's IDLE set.</summary>
    public bool WatchEnabled => watchEnabled;

    public bool IsWatching(AccountId accountId)
    {
        foreach (var key in watches.Keys)
            if (key.Account == accountId.Value) return true;

        return false;
    }

    public Task SubscribeAsync(AccountId accountId, IReadOnlyList<long> folderIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folderIds);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var targets = ResolveFolders(accountId, folderIds, ct);

        if (!watchEnabled)
        {
            log.Warn($"watch.subscribe for account {accountId.Value} is inert: another daemon owns this store.");
            PublishError(
                accountId,
                null,
                new SyncErrorInfo(
                    (int)RpcErrorCode.Unsupported,
                    "unsupported",
                    false,
                    "Another daemon instance owns this store, so this connection will not receive watch notifications."));
            return Task.CompletedTask;
        }

        foreach (var folder in targets)
        {
            var key = (accountId.Value, folder.Id.Value);
            if (watches.ContainsKey(key)) continue;

            var watch = new Watch(CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
            if (!watches.TryAdd(key, watch))
            {
                watch.Cancellation.Dispose();
                continue;
            }

            watch.Loop = Task.Run(() => RunAsync(accountId, folder, watch, watch.Cancellation.Token), CancellationToken.None);
            log.Info($"Watching folder {folder.Id.Value} of account {accountId.Value}.");
        }

        return Task.CompletedTask;
    }

    /// <summary>Empty means the account's configured watch set; that in turn defaults to INBOX.</summary>
    private List<FolderSummary> ResolveFolders(AccountId accountId, IReadOnlyList<long> folderIds, CancellationToken ct)
    {
        var folders = store.ListFolders(accountId, ct);
        var chosen = new List<FolderSummary>(Math.Min(folders.Count, maxWatchedFolders));

        if (folderIds.Count > 0)
        {
            foreach (var id in folderIds)
            {
                var match = Find(folders, f => f.Id.Value == id)
                    ?? throw new StoreException(FailureCategory.NotFound, $"No folder with id {id} on account {accountId.Value}.");

                if (!chosen.Contains(match)) chosen.Add(match);
                if (chosen.Count >= maxWatchedFolders) break;
            }

            return chosen;
        }

        var account = store.GetAccount(accountId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No account with id {accountId.Value}.");

        foreach (var name in account.Imap.WatchFolders)
        {
            var match = Find(folders, f => string.Equals(f.Path.Value, name, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            if (!chosen.Contains(match)) chosen.Add(match);
            if (chosen.Count >= maxWatchedFolders) break;
        }

        if (chosen.Count == 0)
        {
            var inbox = Find(folders, f => f.Role == FolderRole.Inbox) ?? Find(folders, f => f.Path.IsInbox);
            if (inbox is not null) chosen.Add(inbox);
        }

        return chosen;
    }

    private static FolderSummary? Find(IReadOnlyList<FolderSummary> folders, Func<FolderSummary, bool> predicate)
    {
        foreach (var folder in folders)
            if (predicate(folder)) return folder;

        return null;
    }

    private async Task RunAsync(AccountId accountId, FolderSummary folder, Watch watch, CancellationToken ct)
    {
        try
        {
            var provider = await providers.GetProviderAsync(accountId, ct).ConfigureAwait(false);
            connections.Observe(accountId, ConnectionRole.ImapWatch, ConnectionState.Connected, folder.Path.Value);

            await provider.WatchAsync(
                new FolderRef(folder.Id, folder.Path),
                token => OnChangeAsync(accountId, folder.Id, watch, token),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown or unsubscribe.
        }
        catch (Exception ex)
        {
            connections.Observe(accountId, ConnectionRole.ImapWatch, ConnectionState.Error, ex.Message);
            log.Exception($"The watch on folder {folder.Id.Value} stopped.", ex);
            PublishError(accountId, folder.Id, RpcErrorMapper.Classify(ex));
        }
        finally
        {
            connections.Observe(accountId, ConnectionRole.ImapWatch, ConnectionState.Disconnected);
            watches.TryRemove((accountId.Value, folder.Id.Value), out _);
        }
    }

    /// <summary>Debounces the IDLE burst, then runs at most one sync per folder at a time.</summary>
    private async Task OnChangeAsync(AccountId accountId, FolderId folderId, Watch watch, CancellationToken ct)
    {
        if (!watch.Coalescer.TryEnter()) return;

        try
        {
            do
            {
                await Task.Delay(CoalesceDelayMs, ct).ConfigureAwait(false);
                await SyncAndPublishAsync(accountId, folderId, ct).ConfigureAwait(false);
            }
            while (watch.Coalescer.TryContinue());
        }
        catch (OperationCanceledException)
        {
            watch.Coalescer.Abandon();
        }
        catch (Exception ex)
        {
            watch.Coalescer.Abandon();
            log.Exception($"The change handler for folder {folderId.Value} failed.", ex);
        }
    }

    private async Task SyncAndPublishAsync(AccountId accountId, FolderId folderId, CancellationToken ct)
    {
        try
        {
            var provider = await providers.GetProviderAsync(accountId, ct).ConfigureAwait(false);
            var report = await sync.SyncFolderAsync(provider, folderId, null, ct).ConfigureAwait(false);

            var folder = store.GetFolder(folderId, ct);
            if (folder is null) return;

            if (report.Added > 0)
            {
                var take = Math.Min(report.Added, WireMapper.MaxNotificationEnvelopes);
                var page = store.ListEnvelopes(folderId, null, take, ct);
                var messages = new List<EnvelopeDto>(page.Items.Count);
                foreach (var item in page.Items) messages.Add(WireMapper.ToDto(item, accountId, folderId));

                channel.TryEnqueue(RpcPayloads.Notification(
                    RpcNotifications.MailAdded,
                    new MailAddedNotification
                    {
                        AccountId = accountId.Value,
                        FolderId = folderId.Value,
                        FolderName = folder.Path.Value,
                        Count = report.Added,
                        Messages = messages,
                    },
                    ProtocolJsonContext.Default.MailAddedNotification));
            }

            channel.TryEnqueue(RpcPayloads.Notification(
                RpcNotifications.FolderUpdated,
                new FolderUpdatedNotification { AccountId = accountId.Value, Folder = WireMapper.ToDto(folder) },
                ProtocolJsonContext.Default.FolderUpdatedNotification));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Exception($"Watch-driven sync of folder {folderId.Value} failed.", ex);
            PublishError(accountId, folderId, RpcErrorMapper.Classify(ex));
        }
    }

    private void PublishError(AccountId accountId, FolderId? folderId, SyncErrorInfo info)
    {
        channel.TryEnqueue(RpcPayloads.Notification(
            RpcNotifications.SyncError,
            new SyncErrorNotification
            {
                AccountId = accountId.Value,
                FolderId = folderId is { } id ? id.Value : null,
                Code = info.Code,
                Message = info.Message,
                Category = info.Category,
                RequiresUserAction = info.RequiresUserAction,
                AtUtc = WireMapper.Instant(clock.UtcNow),
            },
            ProtocolJsonContext.Default.SyncErrorNotification));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        // Snapshot first: each loop removes its own entry as it unwinds.
        var pending = new List<Watch>(watches.Count);
        foreach (var (_, entry) in watches) pending.Add(entry);

        await lifetime.CancelAsync().ConfigureAwait(false);

        foreach (var entry in pending)
        {
            if (entry.Loop is not { } loop) continue;

            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A watcher that will not stop must not block shutdown; the process is exiting.
            }
        }

        foreach (var entry in pending) entry.Cancellation.Dispose();

        watches.Clear();
        lifetime.Dispose();
    }

    private sealed class Watch
    {
        public Watch(CancellationTokenSource cancellation) => Cancellation = cancellation;

        public CancellationTokenSource Cancellation { get; }

        public Task? Loop { get; set; }

        public Coalescer Coalescer { get; } = new();
    }

    /// <summary>One in-flight pass per folder plus a single dirty bit — a storm cannot queue work.</summary>
    private sealed class Coalescer
    {
        private readonly Lock gate = new();
        private bool running;
        private bool pending;

        /// <summary>True when the caller now owns the pass; false when it only queued a rerun.</summary>
        public bool TryEnter()
        {
            lock (gate)
            {
                if (running)
                {
                    pending = true;
                    return false;
                }

                running = true;
                return true;
            }
        }

        /// <summary>
        /// Consumes a queued rerun, or releases ownership when there is none. Releasing inside the
        /// same lock as the check is what stops a signal arriving mid-release from being dropped.
        /// </summary>
        public bool TryContinue()
        {
            lock (gate)
            {
                if (!pending)
                {
                    running = false;
                    return false;
                }

                pending = false;
                return true;
            }
        }

        /// <summary>Gives up ownership after a failure; the queued rerun goes with it.</summary>
        public void Abandon()
        {
            lock (gate)
            {
                running = false;
                pending = false;
            }
        }
    }
}
