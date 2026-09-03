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
    Tick,
}

internal readonly record struct AppEvent(EventKind Kind, ConsoleKeyInfo Key);

internal sealed partial class App
{
    private const int PageSize = 100;

    private readonly MailcodedClient _client;
    private readonly TerminalWriter _writer;
    private readonly AppState _state = new();
    private readonly Channel<AppEvent> _events =
        Channel.CreateBounded<AppEvent>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });

    private App(MailcodedClient client, TerminalWriter writer)
    {
        _client = client;
        _writer = writer;
    }

    public static async Task<int> RunAsync(MailcodedClient client, TerminalWriter writer, CancellationToken ct)
    {
        var app = new App(client, writer);
        return await app.LoopAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> LoopAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

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

                if (_client.FaultReason is { } fault)
                {
                    _state.Complain($"The daemon is gone: {fault}");
                    Draw();
                    await Task.Delay(1500, CancellationToken.None).ConfigureAwait(false);
                    return 1;
                }

                if (next.Kind == EventKind.Key && !await HandleKeyAsync(next.Key, lifetime.Token).ConfigureAwait(false))
                    return 0;

                if (next.Kind == EventKind.Tick && !_writer.Measure() && _state.Status.Length == 0) continue;

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

        await LoadFoldersAsync(account.Id, ct).ConfigureAwait(false);
        await LoadMessagesAsync(reset: true, ct).ConfigureAwait(false);
        await SubscribeAsync(account.Id, ct).ConfigureAwait(false);
    }

    private async Task LoadFoldersAsync(long accountId, CancellationToken ct)
    {
        var folders = await _client.ListFoldersAsync(accountId, ct).ConfigureAwait(false);
        _state.Folders = folders.Folders;

        var inbox = _state.Folders.ToList().FindIndex(f => f.Role is "inbox");
        _state.FolderIndex = inbox >= 0 ? inbox : 0;
    }

    private async Task SubscribeAsync(long accountId, CancellationToken ct)
    {
        if (!_client.Supports(RpcMethods.WatchSubscribe)) return;

        try
        {
            await _client.CallAsync(
                RpcMethods.WatchSubscribe,
                Json.Serialize(
                    new WatchSubscribeParams { AccountId = accountId },
                    ProtocolJsonContext.Default.WatchSubscribeParams),
                ct).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            _state.Complain($"Live updates are off: {ex.Message}");
        }
    }

    private async Task LoadMessagesAsync(bool reset, CancellationToken ct)
    {
        if (_state.Folder is not { } folder) return;

        _state.Busy = true;
        Draw();

        try
        {
            var result = await _client.SearchAsync(
                new SearchParams
                {
                    Query = _state.Query ?? string.Empty,
                    AccountId = folder.AccountId,
                    FolderId = _state.Query is null ? folder.Id : null,
                    Limit = PageSize,
                    Cursor = reset ? null : _state.NextCursor,
                    Order = _state.Query is null ? SearchOrders.Date : null,
                    IncludeSnippet = false,
                },
                ct).ConfigureAwait(false);

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

            _state.Say(Summary(result));
        }
        catch (RpcException ex)
        {
            _state.Complain(Explain(ex));
        }
        finally
        {
            _state.Busy = false;
        }
    }

    private string Summary(SearchResult result)
    {
        var scope = _state.Query is { Length: > 0 } query ? $"'{query}'" : _state.Folder?.Name ?? "folder";
        var more = result.NextCursor is not null ? "  n for more" : string.Empty;

        return result.Truncated
            ? $"{_state.Messages.Count} in {scope} — matches were dropped that no cursor reaches"
            : $"{_state.Messages.Count} in {scope}{more}";
    }

    private async Task OpenSelectedAsync(CancellationToken ct)
    {
        if (_state.Selected is not { } envelope) return;

        _state.Busy = true;
        Draw();

        try
        {
            _state.Open = await _client.GetMessageAsync(envelope.Id, fetchIfMissing: true, ct).ConfigureAwait(false);
            _state.Focus = Pane.Reader;
            _state.BodyScroll = 0;
            _state.BodyLines = null;
            _state.Say(string.Empty);
        }
        catch (RpcException ex)
        {
            _state.Complain(Explain(ex));
        }
        finally
        {
            _state.Busy = false;
        }
    }

    private async Task ResyncAsync(CancellationToken ct)
    {
        if (_state.Folder is not { } folder || !_client.Supports(RpcMethods.Sync)) return;

        _state.Say($"Syncing {folder.Name}...");
        Draw();

        try
        {
            var raw = await _client.CallAsync(
                RpcMethods.Sync,
                Json.Serialize(
                    new SyncParams { AccountId = folder.AccountId, FolderId = folder.Id },
                    ProtocolJsonContext.Default.SyncParams),
                ct).ConfigureAwait(false);

            var result = raw.Deserialize(ProtocolJsonContext.Default.SyncResult) ?? new SyncResult();
            await LoadMessagesAsync(reset: true, ct).ConfigureAwait(false);
            _state.Say($"+{result.Added} ~{result.Updated} -{result.Expunged} in {result.DurationMs} ms");
        }
        catch (RpcException ex)
        {
            _state.Complain(Explain(ex));
        }
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
                var mail = added.Deserialize(ProtocolJsonContext.Default.MailAddedNotification);
                if (mail is not null && mail.FolderId == _state.Folder?.Id)
                    _state.Say($"{mail.Count} new in {mail.FolderName} — r to refresh");
                break;

            case RpcNotifications.FolderUpdated when document.RootElement.TryGetProperty("params", out var updated):
                var folder = updated.Deserialize(ProtocolJsonContext.Default.FolderUpdatedNotification);
                if (folder is not null) Replace(folder.Folder);
                break;

            case RpcNotifications.SyncError when document.RootElement.TryGetProperty("params", out var failed):
                var error = failed.Deserialize(ProtocolJsonContext.Default.SyncErrorNotification);
                if (error is not null) _state.Complain($"sync: {error.Message}");
                break;
        }
    }

    private void Replace(FolderDto folder)
    {
        var folders = _state.Folders.ToList();
        var index = folders.FindIndex(f => f.Id == folder.Id);

        if (index < 0) return;
        folders[index] = folder;
        _state.Folders = folders;
    }
}
