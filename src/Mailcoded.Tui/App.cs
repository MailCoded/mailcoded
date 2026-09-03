using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Mailcoded.Protocol;
using Mailcoded.Protocol.Client;
using Mailcoded.Tui.Input;
using Mailcoded.Tui.Render;
using Mailcoded.Tui.Views;

namespace Mailcoded.Tui;

internal enum EventKind
{
    Key,
    Notification,
    Completed,
    Tick,
}

internal readonly record struct AppEvent(EventKind Kind, ConsoleKeyInfo Key);

internal sealed partial class App
{
    private const int PageSize = 100;

    private readonly MailcodedClient _client;
    private readonly TerminalWriter _writer;
    private readonly AppState _state = new();
    private readonly ConcurrentQueue<Action> _applies = new();
    private readonly Channel<AppEvent> _events =
        Channel.CreateBounded<AppEvent>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });

    private CancellationToken _lifetime;
    private Task _work = Task.CompletedTask;
    private CancellationTokenSource? _workCancellation;

    private App(MailcodedClient client, TerminalWriter writer)
    {
        _client = client;
        _writer = writer;
    }

    private bool Busy => !_work.IsCompleted;

    public static async Task<int> RunAsync(MailcodedClient client, TerminalWriter writer, CancellationToken ct)
    {
        var app = new App(client, writer);
        return await app.LoopAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> LoopAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _lifetime = lifetime.Token;

        var keys = KeyReader.Start();
        var pumps = Task.WhenAll(
            PumpKeysAsync(keys, lifetime.Token),
            PumpNotificationsAsync(lifetime.Token),
            PumpTicksAsync(lifetime.Token));

        try
        {
            await StartAsync(lifetime.Token).ConfigureAwait(false);
            Draw();

            while (!lifetime.Token.IsCancellationRequested)
            {
                var next = await _events.Reader.ReadAsync(lifetime.Token).ConfigureAwait(false);
                while (_applies.TryDequeue(out var apply)) apply();

                if (_client.FaultReason is { } fault)
                {
                    _state.Complain($"The daemon is gone: {fault}");
                    Draw();
                    await Task.Delay(1500, CancellationToken.None).ConfigureAwait(false);
                    return 1;
                }

                if (next.Kind == EventKind.Key && !HandleKey(next.Key)) return 0;

                if (next.Kind == EventKind.Tick && !_writer.Measure() && !Busy) continue;

                Draw();
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(pumps, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <summary>An RPC can take the daemon's whole timeout, so none is awaited on the loop:
    /// a tags.set waiting 30s on a dead server must not take the quit key with it.</summary>
    private void Start(string label, Func<CancellationToken, Task<Action>> work)
    {
        if (Busy)
        {
            _state.Complain($"Still {_state.BusyLabel}. Press esc to give up on it.");
            return;
        }

        _state.BusyLabel = label;
        _state.Say(label);
        _work = Run(label, work, exclusive: true);
    }

    private void Detach(string label, Func<CancellationToken, Task<Action>> work) =>
        Run(label, work, exclusive: false);

    private Task Run(string label, Func<CancellationToken, Task<Action>> work, bool exclusive)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        if (exclusive) _workCancellation = cancellation;

        return Task.Run(
            async () =>
            {
                Action apply;

                try
                {
                    apply = await work(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    apply = () => _state.Say($"Gave up on {label}.");
                }
                catch (RpcException ex)
                {
                    var message = Explain(ex);
                    apply = () => _state.Complain(message);
                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    apply = () => _state.Complain($"{label}: {message}");
                }
                finally
                {
                    cancellation.Dispose();
                }

                _applies.Enqueue(apply);
                _events.Writer.TryWrite(new AppEvent(EventKind.Completed, default));
            },
            CancellationToken.None);
    }

    private void Cancel()
    {
        if (!Busy) return;
        _workCancellation?.Cancel();
        _state.Say("Giving up...");
    }

    /// <summary>A half-written frame leaves the cursor mid-screen, so a render fault degrades to
    /// one status line rather than an empty terminal.</summary>
    private void Draw()
    {
        try
        {
            Screen.Draw(_writer, _state);
        }
        catch (ArgumentException ex)
        {
            _writer.BeginFrame();
            _writer.Row(0, TerminalText.Cell("render fault: " + ex.Message, _writer.Columns), TextStyle.Danger);
            _writer.EndFrame();
        }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        var accounts = await _client.ListAccountsAsync(ct).ConfigureAwait(false);
        _state.Accounts = accounts.Accounts;

        if (_state.Account is not { } account)
        {
            _state.Complain("No account is configured. Run 'mailcoded setup' first.");
            return;
        }

        foreach (var each in _state.Accounts)
        {
            var folders = await _client.ListFoldersAsync(each.Id, ct).ConfigureAwait(false);
            _state.FoldersByAccount[each.Id] = folders.Folders;
        }

        _state.Rebuild();
        SelectFirstInbox(account.Id);

        LoadMessages(reset: true);

        foreach (var each in _state.Accounts) Subscribe(each.Id);
    }

    /// <summary>Opens on the inbox of the account that owns it, the way a mail client should.</summary>
    private void SelectFirstInbox(long preferredAccountId)
    {
        foreach (var candidate in new[] { preferredAccountId, -1L })
        {
            for (var i = 0; i < _state.Nav.Count; i++)
            {
                var row = _state.Nav[i];
                if (row.Folder?.Role is not "inbox") continue;
                if (candidate >= 0 && row.AccountId != candidate) continue;

                _state.NavIndex = i;
                return;
            }
        }

        for (var i = 0; i < _state.Nav.Count; i++)
        {
            if (!_state.Nav[i].IsSelectable) continue;
            _state.NavIndex = i;
            return;
        }
    }

    /// <summary>watch.subscribe opens an IMAP connection, so it never holds up the first frame.</summary>
    private void Subscribe(long accountId)
    {
        if (!_client.Supports(RpcMethods.WatchSubscribe)) return;

        Detach("watching", async token =>
        {
            await _client.CallAsync(
                RpcMethods.WatchSubscribe,
                Json.Serialize(
                    new WatchSubscribeParams { AccountId = accountId },
                    ProtocolJsonContext.Default.WatchSubscribeParams),
                token).ConfigureAwait(false);

            return static () => { };
        });
    }

    private void LoadMessages(bool reset)
    {
        if (_state.Folder is not { } folder) return;

        var query = _state.Query;
        var cursor = reset ? null : _state.NextCursor;

        Start(reset ? "loading" : "loading more", async token =>
        {
            var result = await _client.SearchAsync(
                new SearchParams
                {
                    Query = query ?? string.Empty,
                    AccountId = folder.AccountId,
                    FolderId = query is null ? folder.Id : null,
                    Limit = PageSize,
                    Cursor = cursor,
                    Order = query is null ? SearchOrders.Date : null,
                    IncludeSnippet = false,
                },
                token).ConfigureAwait(false);

            return () =>
            {
                if (reset)
                {
                    _state.Messages.Clear();
                    _state.MessageIndex = 0;
                }

                _state.Messages.AddRange(result.Hits);
                _state.NextCursor = result.NextCursor;
                _state.Truncated = result.Truncated;

                if (_state.MessageIndex >= _state.Messages.Count)
                    _state.MessageIndex = Math.Max(0, _state.Messages.Count - 1);

                _state.Say(Summary(result, query, folder.Name));
            };
        });
    }

    private string Summary(SearchResult result, string? query, string folderName)
    {
        var scope = query is { Length: > 0 } text ? $"'{text}'" : folderName;
        var more = result.NextCursor is not null ? "  n for more" : string.Empty;

        return result.Truncated
            ? $"{_state.Messages.Count} in {scope} - matches were dropped that no cursor reaches"
            : $"{_state.Messages.Count} in {scope}{more}";
    }

    private void OpenSelected()
    {
        if (_state.Selected is not { } envelope) return;

        Start("opening", async token =>
        {
            var message = await _client
                .GetMessageAsync(envelope.Id, fetchIfMissing: true, token)
                .ConfigureAwait(false);

            return () =>
            {
                _state.Open = message;
                _state.Focus = Pane.Reader;
                _state.BodyScroll = 0;
                _state.BodyLines = null;
                _state.Say(string.Empty);

                if (envelope.Flags.Contains(FlagNames.Unread, StringComparer.Ordinal))
                    ApplyTags(envelope.Id, [], [FlagNames.Unread], "marking read");
            };
        });
    }

    private void Resync()
    {
        if (_state.Folder is not { } folder || !_client.Supports(RpcMethods.Sync)) return;

        Start($"syncing {folder.Name}", async token =>
        {
            var raw = await _client.CallAsync(
                RpcMethods.Sync,
                Json.Serialize(
                    new SyncParams { AccountId = folder.AccountId, FolderId = folder.Id },
                    ProtocolJsonContext.Default.SyncParams),
                token).ConfigureAwait(false);

            var result = raw.Deserialize(ProtocolJsonContext.Default.SyncResult) ?? new SyncResult();

            return () =>
            {
                _state.Say($"+{result.Added} ~{result.Updated} -{result.Expunged} in {result.DurationMs} ms");
                LoadMessages(reset: true);
            };
        });
    }

    private static string Explain(RpcException ex) => ex.Code switch
    {
        (int)RpcErrorCode.Auth => $"{ex.Message}  Run 'mailcoded account reauth'.",
        (int)RpcErrorCode.RateLimited when ex.RetryAfterMs is { } wait => $"{ex.Message}  Retry in {wait / 1000}s.",
        (int)RpcErrorCode.Forbidden => $"{ex.Message}  That gate is closed on purpose.",
        _ => ex.Message,
    };

    private async Task PumpKeysAsync(ChannelReader<ConsoleKeyInfo> keys, CancellationToken ct)
    {
        try
        {
            while (await keys.WaitToReadAsync(ct).ConfigureAwait(false))
                while (keys.TryRead(out var key))
                    await _events.Writer.WriteAsync(new AppEvent(EventKind.Key, key), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PumpNotificationsAsync(CancellationToken ct)
    {
        try
        {
            while (await _client.Notifications.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_client.Notifications.TryRead(out var document))
                {
                    using (document) Absorb(document);
                    await _events.Writer.WriteAsync(new AppEvent(EventKind.Notification, default), ct)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PumpTicksAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await _events.Writer.WriteAsync(new AppEvent(EventKind.Tick, default), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Absorb(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("method", out var method)) return;

        switch (method.GetString())
        {
            case RpcNotifications.MailAdded when document.RootElement.TryGetProperty("params", out var added):
                if (added.Deserialize(ProtocolJsonContext.Default.MailAddedNotification) is { } mail)
                    _applies.Enqueue(() => Announce(mail));
                break;

            case RpcNotifications.FolderUpdated when document.RootElement.TryGetProperty("params", out var updated):
                if (updated.Deserialize(ProtocolJsonContext.Default.FolderUpdatedNotification) is { } folder)
                    _applies.Enqueue(() => Replace(folder.Folder));
                break;

            case RpcNotifications.SyncError when document.RootElement.TryGetProperty("params", out var failed):
                if (failed.Deserialize(ProtocolJsonContext.Default.SyncErrorNotification) is { } error)
                    _applies.Enqueue(() => _state.Complain($"sync: {error.Message}"));
                break;
        }
    }

    private void Announce(MailAddedNotification mail) =>
        _state.Say($"{mail.Count} new in {mail.FolderName} - r to refresh");

    private void Replace(FolderDto folder)
    {
        var folders = _state.FoldersOf(folder.AccountId).ToList();
        var index = folders.FindIndex(f => f.Id == folder.Id);

        if (index < 0) return;

        folders[index] = folder;
        _state.FoldersByAccount[folder.AccountId] = folders;
        _state.Rebuild();
    }
}
