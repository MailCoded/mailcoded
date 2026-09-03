using System.Buffers;
using System.Text.Json;

namespace Mailcoded.Protocol.Client;

/// <summary>Typed client over a spawned daemon.</summary>
/// <remarks>Identifies as rpc: a human client is not an agent surface, and claiming cli would
/// lie in sync_log and close message.move.</remarks>
public sealed class MailcodedClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly DaemonConnection _connection;

    private MailcodedClient(DaemonConnection connection, CapabilitiesDto capabilities, string daemonVersion)
    {
        _connection = connection;
        Capabilities = capabilities;
        DaemonVersion = daemonVersion;
    }

    public CapabilitiesDto Capabilities { get; }

    public string DaemonVersion { get; }

    public System.Threading.Channels.ChannelReader<JsonDocument> Notifications => _connection.Notifications;

    public string? FaultReason => _connection.FaultReason;

    public string Diagnostics => _connection.Diagnostics;

    public static async Task<MailcodedClient> ConnectAsync(
        DaemonLaunch launch,
        string clientName,
        string clientVersion,
        CancellationToken ct)
    {
        var connection = DaemonConnection.Start(launch);

        try
        {
            var result = await connection.RequestAsync(
                RpcMethods.Initialize,
                Serialize(new InitializeParams
                {
                    ClientName = clientName,
                    ClientVersion = clientVersion,
                    ProtocolVersion = ProtocolConstants.Version,
                    Interface = "rpc",
                }, ProtocolJsonContext.Default.InitializeParams),
                DefaultTimeout,
                ct).ConfigureAwait(false);

            var initialize = result.Deserialize(ProtocolJsonContext.Default.InitializeResult)
                ?? throw new DaemonDisconnectedException("The daemon sent an unreadable initialize result.");

            // Refuse rather than guess: docs/rpc.md §8.
            if (initialize.ProtocolVersion != ProtocolConstants.Version)
            {
                throw new DaemonDisconnectedException(
                    $"This daemon speaks protocol {initialize.ProtocolVersion}; this client speaks "
                    + $"{ProtocolConstants.Version}. Upgrade whichever is older.");
            }

            return new MailcodedClient(connection, initialize.Capabilities, initialize.DaemonVersion);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool Supports(string method) => Capabilities.Methods.Contains(method, StringComparer.Ordinal);

    public Task<JsonElement> CallAsync(string method, ReadOnlyMemory<byte> parameters, CancellationToken ct)
    {
        if (!Supports(method))
        {
            throw new RpcException(
                (int)RpcErrorCode.Unsupported,
                $"This daemon does not advertise '{method}'. An absent capability is an absent feature.",
                "unsupported",
                null,
                false);
        }

        return _connection.RequestAsync(method, parameters, DefaultTimeout, ct);
    }

    public async Task<AccountListResult> ListAccountsAsync(CancellationToken ct)
    {
        var result = await CallAsync(RpcMethods.AccountList, default, ct).ConfigureAwait(false);
        return result.Deserialize(ProtocolJsonContext.Default.AccountListResult) ?? new AccountListResult();
    }

    public async Task<FolderListResult> ListFoldersAsync(long accountId, CancellationToken ct)
    {
        var result = await CallAsync(
            RpcMethods.FolderList,
            Serialize(new FolderListParams { AccountId = accountId }, ProtocolJsonContext.Default.FolderListParams),
            ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.FolderListResult) ?? new FolderListResult();
    }

    /// <summary>Always sends a limit; docs/rpc.md §8 forbids an unbounded search.</summary>
    public async Task<SearchResult> SearchAsync(SearchParams request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Limit is null or <= 0)
            request = request with { Limit = Math.Min(50, Capabilities.MaxSearchLimit) };

        var result = await CallAsync(
            RpcMethods.Search,
            Serialize(request, ProtocolJsonContext.Default.SearchParams),
            ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.SearchResult) ?? new SearchResult();
    }

    /// <summary>Text only. A terminal has no sandbox, so bodyHtml is never requested.</summary>
    public async Task<MessageGetResult> GetMessageAsync(long messageId, bool fetchIfMissing, CancellationToken ct)
    {
        var result = await CallAsync(
            RpcMethods.MessageGet,
            Serialize(
                new MessageGetParams
                {
                    MessageId = messageId,
                    Format = MessageFormats.Text,
                    FetchIfMissing = fetchIfMissing,
                },
                ProtocolJsonContext.Default.MessageGetParams),
            ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.MessageGetResult)
            ?? throw new RpcException(-32603, "message.get returned an unreadable result.", null, null, false);
    }

    public async Task<TagsSetResult> SetTagsAsync(
        long messageId,
        IReadOnlyList<string> add,
        IReadOnlyList<string> remove,
        CancellationToken ct)
    {
        var result = await CallAsync(
            RpcMethods.TagsSet,
            Serialize(
                new TagsSetParams { MessageId = messageId, Add = add ?? [], Remove = remove ?? [] },
                ProtocolJsonContext.Default.TagsSetParams),
            ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.TagsSetResult) ?? new TagsSetResult();
    }

    /// <summary>Granted to an rpc caller without MAILCODED_ALLOW_MOVE; there is no delete counterpart.</summary>
    public async Task MoveAsync(long messageId, long toFolderId, CancellationToken ct)
    {
        await CallAsync(
            RpcMethods.MessageMove,
            Serialize(
                new MessageMoveParams { MessageId = messageId, ToFolderId = toFolderId },
                ProtocolJsonContext.Default.MessageMoveParams),
            ct).ConfigureAwait(false);
    }

    /// <summary>Phase one. The result carries a one-time token: hold it in memory, never log it,
    /// and never mint one without a human between the two calls.</summary>
    public async Task<SendPreviewResult> PreviewSendAsync(long accountId, DraftDto draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var result = await CallAsync(
            RpcMethods.SendPreview,
            Serialize(
                new SendPreviewParams { AccountId = accountId, Draft = draft },
                ProtocolJsonContext.Default.SendPreviewParams),
            ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.SendPreviewResult)
            ?? throw new RpcException(-32603, "send.preview returned an unreadable result.", null, null, false);
    }

    public async Task<SendResult> SendAsync(long accountId, long draftId, string confirmToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmToken);

        var result = await CallAsync(
            RpcMethods.Send,
            Serialize(
                new SendParams { AccountId = accountId, DraftId = draftId, ConfirmToken = confirmToken },
                ProtocolJsonContext.Default.SendParams),
            ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.SendResult)
            ?? throw new RpcException(-32603, "send returned an unreadable result.", null, null, false);
    }

    public async Task<ThreadGetResult> GetThreadAsync(string threadKey, int limit, CancellationToken ct)
    {
        var result = await CallAsync(
            RpcMethods.ThreadGet,
            Serialize(
                new ThreadGetParams { ThreadKey = threadKey, Limit = limit },
                ProtocolJsonContext.Default.ThreadGetParams),
            ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.ThreadGetResult) ?? new ThreadGetResult();
    }

    /// <summary>Base64 on the wire; the caller decodes and decides where, if anywhere, it lands.</summary>
    public async Task<AttachmentGetResult> GetAttachmentAsync(long messageId, int index, CancellationToken ct)
    {
        var result = await CallAsync(
            RpcMethods.AttachmentGet,
            Serialize(
                new AttachmentGetParams { MessageId = messageId, Index = index },
                ProtocolJsonContext.Default.AttachmentGetParams),
            ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.AttachmentGetResult)
            ?? throw new RpcException(-32603, "attachment.get returned an unreadable result.", null, null, false);
    }

    public async Task<StatsDto> GetStatsAsync(CancellationToken ct)
    {
        var result = await CallAsync(RpcMethods.Stats, default, ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.StatsResult)?.Stats
            ?? throw new RpcException(-32603, "stats returned an unreadable result.", null, null, false);
    }

    public async Task<HealthDto> GetHealthAsync(CancellationToken ct)
    {
        var result = await CallAsync(RpcMethods.Health, default, ct).ConfigureAwait(false);

        return result.Deserialize(ProtocolJsonContext.Default.HealthResult)?.Health
            ?? throw new RpcException(-32603, "health returned an unreadable result.", null, null, false);
    }

    private static ReadOnlyMemory<byte> Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, value, info);
        writer.Flush();
        return buffer.WrittenMemory;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
