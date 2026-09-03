using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mailcoded.Core.Application;
using Mailcoded.Core.Auth;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Protocol;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;
using AppSendResult = Mailcoded.Core.Application.SendResult;

namespace Mailcoded.Daemon;

/// <summary>
/// The whole RPC surface as one explicit switch (CLAUDE invariant 12: eighteen methods need a
/// switch, not a mediator). Every handler is a thin delegation to an Application service — no gate
/// and no policy decision is re-implemented here (CLAUDE invariant 8).
/// </summary>
internal sealed class RpcDispatcher
{
    private static readonly string[] SupportedMethods =
    [
        RpcMethods.Initialize, RpcMethods.SecretSet, RpcMethods.AccountAdd, RpcMethods.AccountList,
        RpcMethods.FolderList, RpcMethods.Sync, RpcMethods.Search, RpcMethods.ThreadGet,
        RpcMethods.MessageGet, RpcMethods.AttachmentGet, RpcMethods.TagsSet, RpcMethods.MessageMove,
        RpcMethods.SendPreview, RpcMethods.Send, RpcMethods.WatchSubscribe, RpcMethods.Stats,
        RpcMethods.Health, RpcMethods.AccountTest, RpcMethods.OutboxList, RpcMethods.Shutdown,
    ];

    /// <summary>Before initialize the caller is the most restricted kind, never the permissive 'rpc' one:
    /// skipping the handshake must not buy more capability (no HTML bodies, no move, gated send).</summary>
    private const CallerKind UninitializedKind = CallerKind.Mcp;

    /// <summary>The store's own thread-page cap, which no request may exceed.</summary>
    private const int ThreadGetMaxLimit = 2000;

    private static readonly string[] SupportedNotifications =
        [RpcNotifications.MailAdded, RpcNotifications.FolderUpdated, RpcNotifications.SyncError];

    private readonly DaemonHost host;
    private readonly StderrLog log;
    private readonly Lock callerGate = new();
    private CallerContext caller;
    private int shutdownRequested;

    public RpcDispatcher(DaemonHost host, StderrLog log)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(log);

        this.host = host;
        this.log = log;
        caller = CallerContext.For(UninitializedKind, CallerContext.DetectAgentHost());
    }

    public bool ShutdownRequested => Volatile.Read(ref shutdownRequested) != 0;

    public CallerContext Caller
    {
        get { lock (callerGate) return caller; }
        private set { lock (callerGate) caller = value; }
    }

    /// <summary>Returns the serialized JSON <em>value</em> of the result, ready to embed in a response.</summary>
    public async Task<byte[]> DispatchAsync(string method, JsonElement? parameters, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        return method switch
        {
            RpcMethods.Initialize => await InitializeAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.SecretSet => await SecretSetAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.AccountAdd => await AccountAddAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.AccountList => AccountList(ct),
            RpcMethods.FolderList => FolderList(parameters, ct),
            RpcMethods.Sync => await SyncAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.Search => await SearchAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.ThreadGet => ThreadGet(parameters, ct),
            RpcMethods.MessageGet => await MessageGetAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.AttachmentGet => await AttachmentGetAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.TagsSet => await TagsSetAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.MessageMove => await MessageMoveAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.SendPreview => await SendPreviewAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.Send => await SendAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.WatchSubscribe => await WatchSubscribeAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.Stats => Stats(ct),
            RpcMethods.Health => Health(ct),
            RpcMethods.AccountTest => await AccountTestAsync(parameters, ct).ConfigureAwait(false),
            RpcMethods.OutboxList => OutboxList(parameters, ct),
            RpcMethods.Shutdown => Shutdown(),
            _ => throw new MethodNotFoundException(method),
        };
    }

    // ---- session --------------------------------------------------------

    private Task<byte[]> InitializeAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.InitializeParams, RpcMethods.Initialize);

        if (request.ProtocolVersion != ProtocolConstants.Version)
        {
            log.Warn(
                $"Client '{AuditText.Sanitize(request.ClientName, 64)}' negotiated protocol {request.ProtocolVersion}; this daemon speaks {ProtocolConstants.Version}.");
        }

        Caller = CallerContext.For(ToCallerKind(request.Interface), request.AgentHost ?? CallerContext.DetectAgentHost());
        ct.ThrowIfCancellationRequested();

        var kind = Caller.Kind;
        var result = new InitializeResult
        {
            DaemonVersion = DaemonInfo.Version,
            ProtocolVersion = ProtocolConstants.Version,
            Capabilities = new CapabilitiesDto
            {
                Methods = SupportedMethods,
                Notifications = SupportedNotifications,
                Providers = [ProviderKinds.Imap],
                Search = true,
                Threading = true,
                Attachments = true,
                Watch = host.Watch.WatchEnabled,
                Send = host.Policy.IsEnabled(AgentCapability.Send, kind),
                RawSql = host.Policy.IsEnabled(AgentCapability.RawSql, kind),
                HtmlBodies = host.Policy.AllowsHtmlBody(kind),
                MaxSearchLimit = SearchServiceOptions.Default.MaxLimit,
                SecretBackend = host.Accounts.SecretBackendName,
            },
        };

        return Task.FromResult(RpcPayloads.Value(result, ProtocolJsonContext.Default.InitializeResult));
    }

    /// <summary>The credential goes straight to the secret store; nothing else ever sees the value.</summary>
    private async Task<byte[]> SecretSetAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.SecretSetParams, RpcMethods.SecretSet);

        await host.Secrets.SetAsync(request.Ref, request.Value, ct).ConfigureAwait(false);
        log.Info($"Stored a credential under {SecretRedactor.SafeRef(request.Ref)} in {host.Secrets.BackendName}.");

        return RpcPayloads.Value(SecretSetResult.Instance, ProtocolJsonContext.Default.SecretSetResult);
    }

    /// <summary>Connects both legs and reports each. Nothing is sent and nothing is written; the
    /// point is to tell a stored credential that still works from one that does not.</summary>
    private async Task<byte[]> AccountTestAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.AccountTestParams, RpcMethods.AccountTest);
        var accountId = RequireAccount(request.AccountId, ct);

        var folders = 0;
        string imap;
        string? imapDetail = null;

        try
        {
            var provider = await host.Providers.GetProviderAsync(accountId, ct, retryAuthNow: true)
                .ConfigureAwait(false);

            folders = (await provider.ListFoldersAsync(ct).ConfigureAwait(false)).Count;
            imap = "ok";
        }
        catch (ReauthorizationRequiredException ex)
        {
            imap = "auth";
            imapDetail = ex.Message;
        }
        catch (ProviderException ex)
        {
            imap = ex.Category == FailureCategory.Auth ? "auth" : "network";
            imapDetail = ex.Message;
        }

        var config = host.Store.GetAccount(accountId, ct);
        string smtp;
        string? smtpDetail = null;

        if (config?.Smtp is null)
        {
            smtp = "unsupported";
            smtpDetail = "No SMTP configuration for this account.";
        }
        else
        {
            try
            {
                await host.Providers.GetSenderAsync(accountId, ct).ConfigureAwait(false);
                smtp = "ok";
            }
            catch (ReauthorizationRequiredException ex)
            {
                smtp = "auth";
                smtpDetail = ex.Message;
            }
            catch (ProviderException ex)
            {
                smtp = ex.Category == FailureCategory.Auth ? "auth" : "network";
                smtpDetail = ex.Message;
            }
        }

        return RpcPayloads.Value(
            new AccountTestResult
            {
                Imap = imap,
                Smtp = smtp,
                Folders = folders,
                ImapDetail = imapDetail,
                SmtpDetail = smtpDetail,
            },
            ProtocolJsonContext.Default.AccountTestResult);
    }

    private byte[] OutboxList(JsonElement? parameters, CancellationToken ct)
    {
        var request = parameters is null
            ? new OutboxListParams()
            : Require(parameters, ProtocolJsonContext.Default.OutboxListParams, RpcMethods.OutboxList);

        var state = WireMapper.ToOutboxState(request.State);
        var rows = host.Send.ListOutbox(state, ct);
        var limit = Math.Clamp(request.Limit ?? 200, 1, 500);

        var entries = new List<OutboxEntryDto>(Math.Min(rows.Count, limit));

        foreach (var row in rows)
        {
            if (entries.Count == limit) break;
            if (request.AccountId is { } wanted && row.AccountId.Value != wanted) continue;

            entries.Add(WireMapper.ToDto(row));
        }

        return RpcPayloads.Value(
            new OutboxListResult { Entries = entries },
            ProtocolJsonContext.Default.OutboxListResult);
    }

    private byte[] Shutdown()
    {
        Volatile.Write(ref shutdownRequested, 1);
        log.Info("shutdown requested by the client.");
        return RpcPayloads.Value(ShutdownResult.Instance, ProtocolJsonContext.Default.ShutdownResult);
    }

    // ---- accounts and folders -------------------------------------------

    private async Task<byte[]> AccountAddAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.AccountAddParams, RpcMethods.AccountAdd);

        if (string.IsNullOrWhiteSpace(request.SecretRef))
            throw new ArgumentException("secretRef is required; register the credential with secret.set first.");

        var accountId = await host.Accounts.AddAsync(
            new AddAccountRequest
            {
                Email = request.Email,
                DisplayName = request.DisplayName,
                Provider = WireMapper.ToProviderKind(request.Provider),
                Imap = WireMapper.ToImapConfig(request.Imap),
                Smtp = WireMapper.ToSmtpConfig(request.Smtp),
                Auth = WireMapper.ToAuthKind(request.Auth?.Kind),
                SecretRef = request.SecretRef,
            },
            Caller,
            ct).ConfigureAwait(false);

        return RpcPayloads.Value(
            new AccountAddResult { AccountId = accountId.Value },
            ProtocolJsonContext.Default.AccountAddResult);
    }

    private byte[] AccountList(CancellationToken ct)
    {
        var accounts = host.Store.ListAccounts(ct);
        var dtos = new List<AccountDto>(accounts.Count);
        foreach (var account in accounts) dtos.Add(WireMapper.ToDto(account));

        return RpcPayloads.Value(
            new AccountListResult { Accounts = dtos },
            ProtocolJsonContext.Default.AccountListResult);
    }

    private byte[] FolderList(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.FolderListParams, RpcMethods.FolderList);
        var accountId = RequireAccount(request.AccountId, ct);

        var folders = host.Store.ListFolders(accountId, ct);
        var dtos = new List<FolderDto>(folders.Count);
        foreach (var folder in folders) dtos.Add(WireMapper.ToDto(folder));

        return RpcPayloads.Value(
            new FolderListResult { Folders = dtos },
            ProtocolJsonContext.Default.FolderListResult);
    }

    private async Task<byte[]> SyncAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.SyncParams, RpcMethods.Sync);
        var accountId = RequireAccount(request.AccountId, ct);

        var provider = await host.Providers
            .GetProviderAsync(accountId, ct, retryAuthNow: true)
            .ConfigureAwait(false);

        SyncReport report;
        if (request.FolderId is { } folderId)
        {
            var folder = host.Store.GetFolder(new FolderId(folderId), ct);
            if (folder is null || folder.AccountId != accountId)
                throw new StoreException(FailureCategory.NotFound, $"No folder with id {folderId} on account {accountId.Value}.");

            report = await host.Sync.SyncFolderAsync(provider, folder.Id, null, ct).ConfigureAwait(false);
        }
        else
        {
            report = await host.Sync.SyncAccountAsync(provider, accountId, null, ct).ConfigureAwait(false);
        }

        return RpcPayloads.Value(
            new SyncResult
            {
                Added = report.Added,
                Updated = report.Updated,
                Expunged = report.Expunged,
                DurationMs = DaemonInfo.Measured(report.DurationMs),
            },
            ProtocolJsonContext.Default.SyncResult);
    }

    // ---- reading --------------------------------------------------------

    private async Task<byte[]> SearchAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.SearchParams, RpcMethods.Search);

        var results = await host.Search.SearchAsync(
            new SearchRequest
            {
                Query = request.Query,
                Limit = request.Limit ?? 0,
                Cursor = request.Cursor,
                AccountId = request.AccountId is { } account ? new AccountId(account) : null,
                FolderId = request.FolderId is { } folder ? new FolderId(folder) : null,
                Order = WireMapper.ToOrder(request.Order),
                IncludeSnippet = request.IncludeSnippet,
            },
            Caller,
            ct).ConfigureAwait(false);

        foreach (var error in results.Errors)
            log.Debug($"search parse: {error.Kind} at {error.Position}");

        var ids = new List<LocalMessageId>(results.Hits.Count);
        foreach (var hit in results.Hits) ids.Add(hit.Id);

        var tags = host.Store.GetTagsFor(ids, ct);
        var owners = new Dictionary<long, AccountId>();
        var hits = new List<EnvelopeDto>(results.Hits.Count);

        foreach (var hit in results.Hits)
        {
            if (!owners.TryGetValue(hit.FolderId.Value, out var owner))
            {
                owner = host.Store.GetFolder(hit.FolderId, ct)?.AccountId ?? AccountId.None;
                owners[hit.FolderId.Value] = owner;
            }

            hits.Add(WireMapper.ToDto(hit, owner, Combine(hit.Flags, tags, hit.Id)));
        }

        return RpcPayloads.Value(
            new SearchResult { Hits = hits, NextCursor = results.NextCursor, Truncated = results.Truncated },
            ProtocolJsonContext.Default.SearchResult);
    }

    private byte[] ThreadGet(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.ThreadGetParams, RpcMethods.ThreadGet);

        if (!ThreadKey.TryCreate(request.ThreadKey, out var threadKey))
            throw new ArgumentException("threadKey must be a non-empty string.");

        // truncated has to be read against the limit that was actually applied, so the clamp Core and
        // the store would apply anyway happens here, where the answer is still knowable.
        var requested = request.Limit ?? 0;
        var limit = requested <= 0
            ? MessageServiceOptions.Default.ThreadPageSize
            : Math.Min(requested, ThreadGetMaxLimit);
        var view = host.Messages.GetThread(threadKey, limit, ct);

        var messages = new List<EnvelopeDto>(view.Messages.Count);
        foreach (var row in view.Messages)
            messages.Add(WireMapper.ToDto(row, Combine(row.Flags, view.Tags, row.Id)));

        return RpcPayloads.Value(
            new ThreadGetResult { Messages = messages, Truncated = messages.Count >= limit },
            ProtocolJsonContext.Default.ThreadGetResult);
    }

    /// <summary><c>bodyHtml</c> is returned RAW. Sanitization is the client's job, never the daemon's.</summary>
    private async Task<byte[]> MessageGetAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.MessageGetParams, RpcMethods.MessageGet);

        var id = new LocalMessageId(request.MessageId);
        var format = WireMapper.ToFormat(request.Format);
        var envelope = host.Messages.GetEnvelope(id, ct);

        IMailProvider? provider = null;
        if (!envelope.BodyFetched && request.FetchIfMissing)
            provider = await host.Providers.GetProviderAsync(envelope.AccountId, ct).ConfigureAwait(false);

        var view = await host.Messages
            .GetAsync(provider, id, format, request.FetchIfMissing, Caller, ct)
            .ConfigureAwait(false);

        return RpcPayloads.Value(
            new MessageGetResult
            {
                Envelope = WireMapper.ToDto(view.Envelope, view.Tags),
                BodyText = view.BodyText,
                BodyHtml = view.BodyHtml,
                Raw = format == MessageBodyFormat.Raw && view.Raw is { } raw ? Convert.ToBase64String(raw) : null,
                Attachments = WireMapper.ToDtos(view.Attachments),
                BodyFetched = view.BodyFetched,
                ParseWarnings = view.ParseWarnings,
            },
            ProtocolJsonContext.Default.MessageGetResult);
    }

    private async Task<byte[]> AttachmentGetAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.AttachmentGetParams, RpcMethods.AttachmentGet);

        var id = new LocalMessageId(request.MessageId);
        var envelope = host.Messages.GetEnvelope(id, ct);

        IMailProvider? provider = null;
        if (!envelope.BodyFetched)
            provider = await host.Providers.GetProviderAsync(envelope.AccountId, ct).ConfigureAwait(false);

        var content = await host.Messages
            .GetAttachmentAsync(provider, id, request.Index, Caller, ct)
            .ConfigureAwait(false);

        return RpcPayloads.Value(
            new AttachmentGetResult
            {
                Filename = content.FileName,
                Mime = content.MimeType,
                Base64 = Convert.ToBase64String(content.Content),
                Size = content.Size,
            },
            ProtocolJsonContext.Default.AttachmentGetResult);
    }

    // ---- mutation -------------------------------------------------------

    private async Task<byte[]> TagsSetAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.TagsSetParams, RpcMethods.TagsSet);

        var id = new LocalMessageId(request.MessageId);
        var envelope = host.Messages.GetEnvelope(id, ct);

        var delta = new TagDelta
        {
            Add = WireMapper.RequireTags(request.Add, "add"),
            Remove = WireMapper.RequireTags(request.Remove, "remove"),
        };

        // A flag projection has to reach the server or the next sync silently reverts it
        // (invariant 9), so there a connect failure fails the call. On custom tags local wins, so
        // the keyword mirror is best effort: still attempted, but never fatal.
        var mustReach = host.Messages.RequiresServerPush(id, delta, ct);
        IMailProvider? provider = null;

        try
        {
            provider = await host.Providers.GetProviderAsync(envelope.AccountId, ct).ConfigureAwait(false);
        }
        catch (ProviderException) when (!mustReach)
        {
        }

        var result = await host.Messages.SetTagsAsync(provider, id, delta, Caller, ct).ConfigureAwait(false);

        return RpcPayloads.Value(
            new Mailcoded.Protocol.TagsSetResult { Tags = WireMapper.ToTagNames(result.Tags) },
            ProtocolJsonContext.Default.TagsSetResult);
    }

    private async Task<byte[]> MessageMoveAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.MessageMoveParams, RpcMethods.MessageMove);

        var id = new LocalMessageId(request.MessageId);
        var envelope = host.Messages.GetEnvelope(id, ct);
        var provider = await host.Providers.GetProviderAsync(envelope.AccountId, ct).ConfigureAwait(false);

        await host.Messages
            .MoveAsync(provider, id, new FolderId(request.ToFolderId), Caller, ct)
            .ConfigureAwait(false);

        return RpcPayloads.Value(MessageMoveResult.Instance, ProtocolJsonContext.Default.MessageMoveResult);
    }

    // ---- send -----------------------------------------------------------

    private async Task<byte[]> SendPreviewAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.SendPreviewParams, RpcMethods.SendPreview);
        var accountId = RequireAccount(request.AccountId, ct);

        var draft = request.Draft;
        ArgumentNullException.ThrowIfNull(draft, "draft");

        var subject = draft.Subject ?? string.Empty;
        var bodyText = draft.BodyText ?? string.Empty;

        var to = WireMapper.RequireAddresses(draft.To, "draft.to");
        var cc = WireMapper.RequireAddresses(draft.Cc, "draft.cc");
        var bcc = WireMapper.RequireAddresses(draft.Bcc, "draft.bcc");
        if (to.Count + cc.Count + bcc.Count == 0)
            throw new ArgumentException("draft needs at least one recipient.");

        var references = WireMapper.RequireMessageIds(draft.References, "draft.references");
        MessageId? inReplyTo = null;

        if (draft.ReplyToMessageId is { } replyTo)
        {
            var original = host.Messages.GetEnvelope(new LocalMessageId(replyTo), ct);
            inReplyTo = original.MessageId;
            if (original.MessageId is { } parent) references = Append(references, parent);
        }
        else if (!string.IsNullOrWhiteSpace(draft.InReplyTo))
        {
            if (!MessageId.TryParse(draft.InReplyTo, out var parsed))
                throw new ArgumentException("draft.inReplyTo is not a valid Message-ID.");

            inReplyTo = parsed;
        }

        var preview = await host.Send.PreviewAsync(
            Caller,
            accountId,
            new DraftRequest
            {
                From = string.IsNullOrWhiteSpace(draft.From) ? null : WireMapper.RequireAddress(draft.From, "draft.from"),
                To = to,
                Cc = cc,
                Bcc = bcc,
                Subject = subject,
                BodyText = bodyText,
                InReplyTo = inReplyTo,
                References = references,
            },
            ct).ConfigureAwait(false);

        var truncated = bodyText.Length > preview.BodyPreview.Length;

        return RpcPayloads.Value(
            new SendPreviewResult
            {
                DraftId = preview.OutboxId,
                Preview = WireMapper.ToDto(preview, truncated),
                ConfirmToken = preview.ConfirmToken,
                ConfirmTokenExpiresUtc = DaemonInfo.Deterministic
                    ? null
                    : IsoTime.ToWire(host.Clock.UtcNow.AddMilliseconds(preview.TokenLifetimeMs)),
            },
            ProtocolJsonContext.Default.SendPreviewResult);
    }

    private async Task<byte[]> SendAsync(JsonElement? parameters, CancellationToken ct)
    {
        // A missing or blank token never reaches the DTO's required-property check: SPEC §5.6 says
        // send rejects it with 1003, not with an invalid-params error.
        RequireConfirmToken(parameters);

        var request = Require(parameters, ProtocolJsonContext.Default.SendParams, RpcMethods.Send);
        var accountId = RequireAccount(request.AccountId, ct);

        var sender = await host.Providers.GetSenderAsync(accountId, ct).ConfigureAwait(false);
        var provider = await host.Providers.TryGetProviderAsync(accountId, ct).ConfigureAwait(false);

        AppSendResult result = await host.Send
            .SendAsync(Caller, sender, provider, request.DraftId, request.ConfirmToken, null, ct)
            .ConfigureAwait(false);

        return RpcPayloads.Value(
            new Mailcoded.Protocol.SendResult
            {
                MessageId = result.MessageId.Value,
                State = result.State.ToWireValue(),
                OutboxId = result.OutboxId,
                SmtpResponse = AuditText.Sanitize(result.SmtpResponse, 200),
            },
            ProtocolJsonContext.Default.SendResult);
    }

    // ---- watch, stats, health -------------------------------------------

    private async Task<byte[]> WatchSubscribeAsync(JsonElement? parameters, CancellationToken ct)
    {
        var request = Require(parameters, ProtocolJsonContext.Default.WatchSubscribeParams, RpcMethods.WatchSubscribe);
        var accountId = RequireAccount(request.AccountId, ct);

        await host.Watch.SubscribeAsync(accountId, request.FolderIds, ct).ConfigureAwait(false);

        return RpcPayloads.Value(
            WatchSubscribeResult.Instance,
            ProtocolJsonContext.Default.WatchSubscribeResult);
    }

    private byte[] Stats(CancellationToken ct)
    {
        var stats = host.Health.Collect(ct);

        var folders = new List<FolderSyncStatDto>();
        foreach (var account in host.Store.ListAccounts(ct))
            foreach (var folder in host.Store.ListFolders(account.Id, ct))
                folders.Add(WireMapper.ToStatDto(folder));

        return RpcPayloads.Value(
            new StatsResult { Stats = WireMapper.ToDto(stats, folders) },
            ProtocolJsonContext.Default.StatsResult);
    }

    private byte[] Health(CancellationToken ct)
    {
        var report = host.Health.CheckHealth(ct);
        var queued = CountByAccount(host.Send.ListOutbox(OutboxState.Queued, ct, confirmedOnly: true));
        var failed = CountByAccount(host.Send.ListOutbox(OutboxState.Failed, ct));

        var accounts = new List<AccountHealthDto>(report.Accounts.Count);
        var warnings = new List<string>(4);
        var anyAuth = false;
        var anyError = false;

        foreach (var account in report.Accounts)
        {
            anyAuth |= account.AuthRequired;
            anyError |= account.ImapState == ConnectionState.Error;

            DateTimeOffset? lastSync = null;
            foreach (var folder in host.Store.ListFolders(account.AccountId, ct))
                if (folder.LastSyncUtc is { } stamp && (lastSync is null || stamp > lastSync)) lastSync = stamp;

            accounts.Add(new AccountHealthDto
            {
                AccountId = account.AccountId.Value,
                Email = account.Email,
                Connection = ConnectionName(account.ImapState),
                Auth = account.AuthRequired
                    ? HealthStates.AuthRequired
                    : account.ImapState == ConnectionState.Connected ? HealthStates.AuthOk : HealthStates.AuthUnknown,
                Watching = host.Watch.IsWatching(account.AccountId),
                LastError = account.LastDetail,
                LastSyncUtc = WireMapper.InstantOrNull(lastSync),
                OutboxQueued = Count(queued, account.AccountId),
                OutboxFailed = Count(failed, account.AccountId),
            });
        }

        if (report.StuckSends > 0) warnings.Add("outbox-stuck-sending");
        if (anyAuth) warnings.Add("auth-required");
        if (!host.IsPrimaryInstance) warnings.Add("secondary-instance");
        if (!host.Watch.WatchEnabled) warnings.Add("watch-disabled-not-store-owner");

        var status = anyAuth || anyError
            ? HealthStates.Error
            : report.Ok && warnings.Count == 0 ? HealthStates.Ok : HealthStates.Degraded;

        return RpcPayloads.Value(
            new HealthResult
            {
                Health = new HealthDto
                {
                    Status = status,
                    StoreOk = true,
                    Accounts = accounts,
                    Warnings = warnings,
                },
            },
            ProtocolJsonContext.Default.HealthResult);
    }

    // ---- helpers --------------------------------------------------------

    private static string ConnectionName(ConnectionState state) => state switch
    {
        ConnectionState.Connected => HealthStates.Connected,
        ConnectionState.Connecting => HealthStates.Connecting,
        ConnectionState.AuthRequired or ConnectionState.Error => HealthStates.Error,
        _ => HealthStates.Disconnected,
    };

    private static Dictionary<long, int> CountByAccount(IReadOnlyList<OutboxRecord> rows)
    {
        var counts = new Dictionary<long, int>();
        foreach (var row in rows)
        {
            counts.TryGetValue(row.AccountId.Value, out var current);
            counts[row.AccountId.Value] = current + 1;
        }

        return counts;
    }

    private static int Count(Dictionary<long, int> counts, AccountId accountId) =>
        counts.TryGetValue(accountId.Value, out var value) ? value : 0;

    private static IReadOnlyList<Tag> Combine(
        MessageFlags flags,
        IReadOnlyDictionary<LocalMessageId, IReadOnlyList<Tag>> custom,
        LocalMessageId id)
    {
        if (!custom.TryGetValue(id, out var tags) || tags.Count == 0)
            return TagFlagMap.ToTags(flags, null);

        var keywords = new List<string>(tags.Count);
        foreach (var tag in tags) keywords.Add(tag.Value);
        return TagFlagMap.ToTags(flags, keywords);
    }

    private static IReadOnlyList<MessageId> Append(IReadOnlyList<MessageId> references, MessageId parent)
    {
        foreach (var reference in references)
            if (reference == parent) return references;

        return [.. references, parent];
    }

    private AccountId RequireAccount(long accountId, CancellationToken ct)
    {
        var id = new AccountId(accountId);
        return host.Store.GetAccount(id, ct) is not null
            ? id
            : throw new StoreException(FailureCategory.NotFound, $"No account with id {accountId}.");
    }

    private static void RequireConfirmToken(JsonElement? parameters)
    {
        if (parameters is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("confirmToken", out var token)
            && token.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(token.GetString()))
        {
            return;
        }

        throw new ConfirmRequiredException(
            "A valid one-time confirm token from send.preview is required before a message is sent.");
    }

    private static T Require<T>(JsonElement? parameters, JsonTypeInfo<T> typeInfo, string method)
        where T : class
    {
        if (parameters is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new ArgumentException($"'{method}' requires a params object.");

        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"'{method}' params must be a JSON object.");

        return JsonSerializer.Deserialize(element, typeInfo)
               ?? throw new ArgumentException($"'{method}' params could not be read.");
    }

    private static CallerKind ToCallerKind(string? name) => name switch
    {
        null or "" or "rpc" => CallerKind.Rpc,
        "cli" => CallerKind.Cli,
        "mcp" => CallerKind.Mcp,
        // 'internal' is the daemon's own retry loop; a client may never claim it.
        _ => throw new ArgumentException("interface must be one of 'rpc', 'cli', 'mcp'."),
    };
}

/// <summary>Raised for an unknown method so the server can answer with -32601 rather than -32603.</summary>
internal sealed class MethodNotFoundException : Exception
{
    public MethodNotFoundException(string method)
        : base($"No method named '{AuditText.Sanitize(method, 64)}' exists on protocol version {Mailcoded.Protocol.ProtocolConstants.Version}.")
    {
    }
}
