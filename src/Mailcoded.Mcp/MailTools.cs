using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Store;

namespace Mailcoded.Mcp;

/// <summary>Argument validation, one call into Core, and result shaping. Gates all live in Core.</summary>
internal sealed class MailTools
{
    private const int DefaultThreadLimit = 200;
    private const int MaxThreadLimit = 500;
    private const int BodyPreviewChars = 2_000;

    private readonly McpHost _host;

    public MailTools(McpHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public async Task<string> SearchAsync(ToolArgs args, CancellationToken ct)
    {
        var request = new SearchRequest
        {
            Query = args.RequireString("query"),
            Limit = args.OptionalInt32("limit", 0),
            Cursor = args.OptionalString("cursor"),
            AccountId = args.OptionalInt64("account_id") is { } account ? new AccountId(account) : null,
            FolderId = args.OptionalInt64("folder_id") is { } folder ? new FolderId(folder) : null,
            Order = ParseOrder(args.OptionalString("order")),
            IncludeSnippet = args.OptionalBool("include_snippet", true),
        };

        var results = await _host.Search.SearchAsync(request, _host.Caller, ct).ConfigureAwait(false);

        var hits = new List<SearchHitDto>(results.Hits.Count);
        foreach (var hit in results.Hits)
        {
            hits.Add(new SearchHitDto
            {
                Id = hit.Id.Value,
                FolderId = hit.FolderId.Value,
                Subject = hit.Subject,
                From = hit.From,
                Date = Wire.Date(hit.DateUtc),
                Flags = Wire.Flags(hit.Flags),
                Snippet = hit.Snippet,
            });
        }

        var errors = new List<SearchParseErrorDto>(results.Errors.Count);
        foreach (var error in results.Errors)
        {
            errors.Add(new SearchParseErrorDto
            {
                Kind = error.Kind.ToString(),
                Position = error.Position,
                Message = error.Message,
            });
        }

        return Json(
            new SearchResultDto
            {
                SchemaVersion = ProtocolConstants.Version,
                Hits = hits,
                NextCursor = results.NextCursor,
                Truncated = results.Truncated,
                Route = Wire.Route(results.Route),
                QueryErrors = errors,
            },
            McpJsonContext.Default.SearchResultDto);
    }

    public async Task<string> ReadAsync(ToolArgs args, CancellationToken ct)
    {
        var id = new LocalMessageId(args.RequirePositiveInt64("id"));
        var fetchIfMissing = args.OptionalBool("fetch_if_missing", true);
        var envelope = _host.Messages.GetEnvelope(id, ct);

        IMailProvider? provider = null;
        if (fetchIfMissing && !envelope.BodyFetched)
            provider = await _host.Connections.ProviderAsync(envelope.AccountId, ct).ConfigureAwait(false);

        var view = await _host.Messages
            .GetAsync(provider, id, MessageBodyFormat.Text, fetchIfMissing, _host.Caller, ct)
            .ConfigureAwait(false);

        var row = view.Envelope;

        return Json(
            new ReadResultDto
            {
                SchemaVersion = ProtocolConstants.Version,
                Id = row.Id.Value,
                AccountId = row.AccountId.Value,
                FolderId = row.FolderId.Value,
                ThreadKey = row.ThreadKey?.Value,
                MessageId = row.MessageId?.Value,
                Subject = row.Subject,
                From = row.From,
                To = row.To,
                Cc = row.Cc,
                Date = Wire.Date(row.DateUtc),
                Flags = Wire.Flags(row.Flags),
                Tags = Wire.Tags(view.Tags),
                HasAttachments = row.HasAttachments,
                Size = row.Size,
                BodyFetched = view.BodyFetched,
                BodyText = view.BodyText,
                ParseWarnings = view.ParseWarnings,
            },
            McpJsonContext.Default.ReadResultDto);
    }

    public Task<string> ThreadAsync(ToolArgs args, CancellationToken ct)
    {
        ThreadKey key;
        var keyArgument = args.OptionalString("thread_key");

        if (!string.IsNullOrWhiteSpace(keyArgument))
        {
            if (!ThreadKey.TryCreate(keyArgument, out key))
                throw new ToolFailureException(ToolErrorCodes.InvalidParams, "'thread_key' must be a non-empty string.");
        }
        else
        {
            var id = new LocalMessageId(args.RequirePositiveInt64("id"));
            var envelope = _host.Messages.GetEnvelope(id, ct);
            key = envelope.ThreadKey
                ?? throw new ToolFailureException(
                    ToolErrorCodes.NotFound,
                    $"Message {id.Value} is not part of a conversation.");
        }

        var limit = args.OptionalInt32("limit", DefaultThreadLimit);
        if (limit is < 1 or > MaxThreadLimit)
            throw new ToolFailureException(ToolErrorCodes.InvalidParams, $"'limit' must be 1..{MaxThreadLimit}.");

        var view = _host.Messages.GetThread(key, limit, ct);
        var messages = new List<ThreadMessageDto>(view.Messages.Count);

        foreach (var message in view.Messages)
        {
            IReadOnlyList<string> tags = view.Tags.TryGetValue(message.Id, out var stored)
                ? Wire.Tags(stored)
                : [];

            messages.Add(new ThreadMessageDto
            {
                Id = message.Id.Value,
                FolderId = message.FolderId.Value,
                Subject = message.Subject,
                From = message.From,
                Date = Wire.Date(message.DateUtc),
                Flags = Wire.Flags(message.Flags),
                Tags = tags,
                BodyFetched = message.BodyFetched,
            });
        }

        return Task.FromResult(Json(
            new ThreadResultDto
            {
                SchemaVersion = ProtocolConstants.Version,
                ThreadKey = key.Value,
                Messages = messages,
                Truncated = messages.Count >= limit,
            },
            McpJsonContext.Default.ThreadResultDto));
    }

    public async Task<string> TagAsync(ToolArgs args, CancellationToken ct)
    {
        var id = new LocalMessageId(args.RequirePositiveInt64("id"));
        var delta = new TagDelta
        {
            Add = ParseTags(args.StringArray("add"), "add"),
            Remove = ParseTags(args.StringArray("remove"), "remove"),
        };

        if (delta.IsEmpty)
            throw new ToolFailureException(ToolErrorCodes.InvalidParams, "Provide at least one tag in 'add' or 'remove'.");

        var envelope = _host.Messages.GetEnvelope(id, ct);
        var provider = await _host.Connections.TryProviderAsync(envelope.AccountId, ct).ConfigureAwait(false);
        var result = await _host.Messages.SetTagsAsync(provider, id, delta, _host.Caller, ct).ConfigureAwait(false);

        return Json(
            new TagResultDto
            {
                SchemaVersion = ProtocolConstants.Version,
                Id = id.Value,
                Tags = Wire.Tags(result.Tags),
                Flags = Wire.Flags(result.Flags),
                PushedToServer = result.PushedToServer,
            },
            McpJsonContext.Default.TagResultDto);
    }

    public Task<string> DraftAsync(ToolArgs args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var accountId = args.RequirePositiveInt64("account_id");
        var draft = ReadDraft(args);

        var recipients = new List<EmailAddress>(draft.To.Count + draft.Cc.Count + draft.Bcc.Count);
        recipients.AddRange(draft.To);
        recipients.AddRange(draft.Cc);
        recipients.AddRange(draft.Bcc);

        var gate = _host.Policy.EvaluateSend(_host.Caller, recipients);

        return Task.FromResult(Json(
            new DraftResultDto
            {
                SchemaVersion = ProtocolConstants.Version,
                AccountId = accountId,
                From = draft.From?.Value,
                To = Wire.Addresses(draft.To),
                Cc = Wire.Addresses(draft.Cc),
                Bcc = Wire.Addresses(draft.Bcc),
                Subject = draft.Subject,
                BodyPreview = Wire.Truncate(draft.BodyText, BodyPreviewChars),
                BodyChars = draft.BodyText.Length,
                InReplyTo = draft.InReplyTo?.Value,
                Persisted = false,
                SendGate = ToGate(gate),
                NextStep = "Show this to the human. Call send_preview with the same arguments only when they ask you to send.",
            },
            McpJsonContext.Default.DraftResultDto));
    }

    public async Task<string> SendPreviewAsync(ToolArgs args, CancellationToken ct)
    {
        var accountId = new AccountId(args.RequirePositiveInt64("account_id"));
        var draft = ReadDraft(args);

        var preview = await _host.Send.PreviewAsync(_host.Caller, accountId, draft, ct).ConfigureAwait(false);

        return Json(
            new SendPreviewResultDto
            {
                SchemaVersion = ProtocolConstants.Version,
                DraftId = preview.OutboxId,
                MessageId = preview.MessageId.Value,
                ConfirmToken = preview.ConfirmToken,
                ConfirmTokenLifetimeMs = preview.TokenLifetimeMs,
                From = preview.From.Value,
                To = Wire.Addresses(preview.To),
                Cc = Wire.Addresses(preview.Cc),
                Bcc = Wire.Addresses(preview.Bcc),
                Subject = preview.Subject,
                BodyPreview = preview.BodyPreview,
                SizeBytes = preview.SizeBytes,
                RequiresSmtpUtf8 = preview.RequiresSmtpUtf8,
                Digest = preview.Digest,
                SendGate = ToGate(preview.Gate),
                NextStep = "Nothing has been sent. Give this preview and confirm_token to the human; only they may authorize send_draft.",
            },
            McpJsonContext.Default.SendPreviewResultDto);
    }

    public async Task<string> SendDraftAsync(ToolArgs args, CancellationToken ct)
    {
        var draftId = args.RequirePositiveInt64("draft_id");
        var confirmToken = args.RequireString("confirm_token");

        var record = FindDraft(draftId, ct)
            ?? throw new ToolFailureException(ToolErrorCodes.NotFound, $"No draft with id {draftId}.");

        var sender = await _host.Connections.SenderAsync(record.AccountId, ct).ConfigureAwait(false);
        var provider = await _host.Connections.TryProviderAsync(record.AccountId, ct).ConfigureAwait(false);

        var result = await _host.Send
            .SendAsync(_host.Caller, sender, provider, draftId, confirmToken, null, ct)
            .ConfigureAwait(false);

        return Json(
            new SendDraftResultDto
            {
                SchemaVersion = ProtocolConstants.Version,
                DraftId = result.OutboxId,
                MessageId = result.MessageId.Value,
                State = result.State.ToWireValue(),
                StatusCode = result.StatusCode,
                SmtpResponse = result.SmtpResponse,
                EnhancedStatusCode = result.EnhancedStatusCode,
                Attempts = result.Attempts,
                NextAttemptUtc = Wire.DateOrNull(result.NextAttemptUtc),
                AppendedToSent = result.AppendedToSent,
                PermanentlyFailed = result.PermanentlyFailed,
            },
            McpJsonContext.Default.SendDraftResultDto);
    }

    public Task<string> StatsAsync(ToolArgs args, CancellationToken ct)
    {
        _ = args;
        var stats = _host.Health.Collect(ct);
        var health = _host.Health.CheckHealth(ct);

        var folders = new List<StatsFolderDto>(stats.Folders.Count);
        foreach (var folder in stats.Folders)
        {
            folders.Add(new StatsFolderDto
            {
                FolderId = folder.FolderId.Value,
                Path = folder.Path,
                Role = folder.Role.ToWireValue(),
                Unread = folder.UnreadCount,
                Total = folder.TotalCount,
                LastSyncUtc = Wire.DateOrNull(folder.LastSyncUtc),
            });
        }

        var accounts = new List<StatsAccountDto>(health.Accounts.Count);
        foreach (var account in health.Accounts)
        {
            accounts.Add(new StatsAccountDto
            {
                AccountId = account.AccountId.Value,
                Email = account.Email,
                Imap = Wire.Connection(account.ImapState),
                Smtp = Wire.Connection(account.SmtpState),
                AuthRequired = account.AuthRequired,
                Folders = account.FolderCount,
                Unread = account.UnreadCount,
            });
        }

        return Task.FromResult(Json(
            new StatsResultDto
            {
                SchemaVersion = ProtocolConstants.Version,
                Ok = health.Ok,
                SecretBackend = health.SecretBackend,
                UptimeMs = stats.Process.UptimeMs,
                Store = new StatsStoreDto
                {
                    Path = stats.Store.DatabasePath,
                    SchemaVersion = stats.Store.SchemaVersion,
                    SizeBytes = stats.Store.DatabaseSizeBytes,
                    WalBytes = stats.Store.WalSizeBytes,
                    BlobBytes = stats.Store.BlobDirectorySizeBytes,
                    TableCounts = stats.Store.TableCounts,
                },
                Outbox = new StatsOutboxDto
                {
                    Queued = stats.QueuedOutbox,
                    Failed = stats.FailedOutbox,
                    Stuck = health.StuckSends,
                },
                Agent = new StatsAgentDto
                {
                    Interface = _host.Caller.InterfaceName,
                    SendEnabled = _host.Policy.Options.SendEnabled,
                    ApprovedRecipientPatterns = _host.Policy.Options.ApprovedRecipients.Count,
                    RemainingSendsThisHour = stats.RemainingAgentSends,
                    OutstandingConfirmTokens = stats.OutstandingConfirmTokens,
                    HtmlBodies = _host.Policy.AllowsHtmlBody(_host.Caller.Kind),
                },
                Accounts = accounts,
                Folders = folders,
            },
            McpJsonContext.Default.StatsResultDto));
    }

    private OutboxRecord? FindDraft(long draftId, CancellationToken ct)
    {
        foreach (var record in _host.Send.ListOutbox(null, ct))
            if (record.Id == draftId) return record;

        return null;
    }

    private static DraftRequest ReadDraft(ToolArgs args)
    {
        var to = ParseAddresses(args.StringArray("to"), "to");
        if (to.Count == 0)
            throw new ToolFailureException(ToolErrorCodes.InvalidParams, "'to' must contain at least one address.");

        var subject = args.OptionalString("subject")
            ?? throw new ToolFailureException(ToolErrorCodes.InvalidParams, "'subject' must be a string.");
        var body = args.OptionalString("body")
            ?? throw new ToolFailureException(ToolErrorCodes.InvalidParams, "'body' must be a string.");

        EmailAddress? from = null;
        if (args.OptionalString("from") is { } fromArgument)
        {
            if (!EmailAddress.TryParse(fromArgument, out var parsed))
                throw new ToolFailureException(ToolErrorCodes.InvalidParams, "'from' is not a valid email address.");
            from = parsed;
        }

        MessageId? inReplyTo = null;
        if (args.OptionalString("in_reply_to") is { } replyArgument)
        {
            if (!MessageId.TryParse(replyArgument, out var parsed))
                throw new ToolFailureException(ToolErrorCodes.InvalidParams, "'in_reply_to' is not a valid Message-ID.");
            inReplyTo = parsed;
        }

        return new DraftRequest
        {
            To = to,
            Cc = ParseAddresses(args.StringArray("cc"), "cc"),
            Bcc = ParseAddresses(args.StringArray("bcc"), "bcc"),
            Subject = subject,
            BodyText = body,
            From = from,
            InReplyTo = inReplyTo,
            References = ParseMessageIds(args.StringArray("references"), "references"),
        };
    }

    private static IReadOnlyList<EmailAddress> ParseAddresses(IReadOnlyList<string> raw, string name)
    {
        if (raw.Count == 0) return [];

        var addresses = new List<EmailAddress>(raw.Count);
        foreach (var value in raw)
        {
            if (!EmailAddress.TryParse(value, out var address))
            {
                throw new ToolFailureException(
                    ToolErrorCodes.InvalidParams,
                    $"'{name}' contains a value that is not a valid email address.");
            }

            addresses.Add(address);
        }

        return addresses;
    }

    private static IReadOnlyList<MessageId> ParseMessageIds(IReadOnlyList<string> raw, string name)
    {
        if (raw.Count == 0) return [];

        var ids = new List<MessageId>(raw.Count);
        foreach (var value in raw)
        {
            if (!MessageId.TryParse(value, out var id))
                throw new ToolFailureException(ToolErrorCodes.InvalidParams, $"'{name}' contains an invalid Message-ID.");

            ids.Add(id);
        }

        return ids;
    }

    private static IReadOnlyList<Tag> ParseTags(IReadOnlyList<string> raw, string name)
    {
        if (raw.Count == 0) return [];

        var tags = new List<Tag>(raw.Count);
        foreach (var value in raw)
        {
            if (!Tag.TryParse(value, out var tag))
                throw new ToolFailureException(ToolErrorCodes.InvalidParams, $"'{name}' contains a value that is not a valid tag.");

            tags.Add(tag);
        }

        return tags;
    }

    private static SearchOrder ParseOrder(string? value) => value switch
    {
        null or "" or SearchOrders.Relevance => SearchOrder.Relevance,
        SearchOrders.Date => SearchOrder.Date,
        _ => throw new ToolFailureException(ToolErrorCodes.InvalidParams, "'order' must be 'relevance' or 'date'."),
    };

    private static SendGateDto ToGate(SendGateDecision gate) => new()
    {
        Allowed = gate.Allowed,
        Decision = gate.Label,
        Reason = gate.Allowed ? null : ToolErrors.ToSlug(gate.Reason),
        Detail = gate.Detail,
        RetryAfterMs = gate.RetryAfter is { } retry ? (long)retry.TotalMilliseconds : null,
    };

    private static string Json<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);
}
