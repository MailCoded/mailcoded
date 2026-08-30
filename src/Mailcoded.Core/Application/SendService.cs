using System.Runtime.ExceptionServices;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

/// <summary>A message a client wants sent. The Message-ID is assigned by the outbox, never here.</summary>
public sealed record DraftRequest
{
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];
    public required string Subject { get; init; }
    public required string BodyText { get; init; }
    public MessageId? InReplyTo { get; init; }
    public IReadOnlyList<MessageId> References { get; init; } = [];

    /// <summary>Overrides the account address; must still be an address the account may send as.</summary>
    public EmailAddress? From { get; init; }

    public string? FromDisplayName { get; init; }
}

public sealed record SendOptions
{
    /// <summary>True for servers that copy a submission into Sent themselves, making APPEND a duplicate.</summary>
    public bool ServerAutoSavesToSent { get; init; }

    public bool AppendToSent { get; init; } = true;

    public int BodyPreviewChars { get; init; } = 2_000;

    public static readonly SendOptions Default = new();
}

public sealed record SendPreview
{
    public required long OutboxId { get; init; }
    public required MessageId MessageId { get; init; }
    public required string ConfirmToken { get; init; }
    public long TokenLifetimeMs { get; init; }
    public required EmailAddress From { get; init; }
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];
    public string Subject { get; init; } = string.Empty;
    public string BodyPreview { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool RequiresSmtpUtf8 { get; init; }
    public required string Digest { get; init; }

    /// <summary>What the send gate would decide right now, so a caller learns before it burns the token.</summary>
    public required SendGateDecision Gate { get; init; }
}

public sealed record SendResult
{
    public required long OutboxId { get; init; }
    public required MessageId MessageId { get; init; }
    public required OutboxState State { get; init; }
    public int StatusCode { get; init; }
    public string? SmtpResponse { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public bool AppendedToSent { get; init; }
    public bool RequiresReconnect { get; init; }
    public bool PermanentlyFailed { get; init; }
}

public sealed record OutboxReconcileOutcome
{
    public required long OutboxId { get; init; }
    public required MessageId MessageId { get; init; }
    public required SendReconciliation Decision { get; init; }
    public required OutboxState State { get; init; }
}

public sealed record OutboxReconcileReport
{
    public IReadOnlyList<OutboxReconcileOutcome> Outcomes { get; init; } = [];
    public int MarkedSent { get; init; }
    public int Requeued { get; init; }
    public int NeedsInvestigation { get; init; }

    public static readonly OutboxReconcileReport Empty = new();
}

/// <summary>Two-phase send, the safety gates, Sent reconciliation, and the retry schedule.</summary>
public sealed class SendService
{
    private readonly SqliteStore _store;
    private readonly MessageParser _parser;
    private readonly IClock _clock;
    private readonly AuditLog _audit;
    private readonly AgentPolicy _policy;
    private readonly ConfirmTokenStore _tokens;
    private readonly SendOptions _defaults;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, SendEnvelope> _envelopes = [];

    public SendService(
        SqliteStore store,
        MessageParser parser,
        IClock clock,
        AuditLog audit,
        AgentPolicy policy,
        ConfirmTokenStore tokens,
        SendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(tokens);

        _store = store;
        _parser = parser;
        _clock = clock;
        _audit = audit;
        _policy = policy;
        _tokens = tokens;
        _defaults = options ?? SendOptions.Default;
    }

    public IReadOnlyList<OutboxRecord> ListOutbox(OutboxState? state = null, CancellationToken ct = default) =>
        _store.ListOutbox(state, ct);

    /// <summary>Builds the message, creates the outbox row, and mints the one-time confirm token.</summary>
    public async Task<SendPreview> PreviewAsync(
        CallerContext caller,
        AccountId accountId,
        DraftRequest draft,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var account = _store.GetAccount(accountId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No account with id {accountId.Value}.");

        var from = draft.From ?? ParseAccountAddress(account);
        var messageId = MessageId.NewForDomain(from.Domain, Guid.NewGuid());

        var spec = new DraftSpec
        {
            From = from,
            FromDisplayName = draft.FromDisplayName ?? account.DisplayName,
            To = draft.To,
            Cc = draft.Cc,
            Bcc = draft.Bcc,
            Subject = draft.Subject,
            BodyText = draft.BodyText,
            InReplyTo = draft.InReplyTo,
            References = draft.References,
            MessageId = messageId,
            DateUtc = _clock.UtcNow,
        };

        var raw = MessageBuilder.Build(spec, ct);
        var recipients = RecipientExtractor.FromDraft(spec).AllRecipients();

        var outboxId = await _store.EnqueueOutboxAsync(
            new OutboxRecord
            {
                AccountId = accountId,
                MessageId = messageId,
                State = OutboxState.Queued,
                Raw = raw,
                Attempts = 0,
                CreatedUtc = _clock.UtcNow,
            },
            ct).ConfigureAwait(false);

        var digest = AuditText.Digest(raw);
        var token = _tokens.Issue(outboxId, messageId, digest);
        Remember(outboxId, new SendEnvelope(from, recipients));

        var gate = _policy.EvaluateSend(caller, recipients);

        await _audit.SendAsync(
            new SendAuditRecord
            {
                AccountId = accountId,
                Method = AuditEvents.SendPreview,
                Decision = gate.Label,
                ArgsDigest = digest,
                Caller = caller,
                MessageId = messageId,
                OutboxId = outboxId,
                RecipientCount = recipients.Count,
                TokenConsumed = false,
                Reason = gate.Allowed ? null : gate.Reason.ToString(),
            },
            ct).ConfigureAwait(false);

        return new SendPreview
        {
            OutboxId = outboxId,
            MessageId = messageId,
            ConfirmToken = token,
            TokenLifetimeMs = (long)_tokens.Lifetime.TotalMilliseconds,
            From = from,
            To = draft.To,
            Cc = draft.Cc,
            Bcc = draft.Bcc,
            Subject = draft.Subject,
            BodyPreview = Truncate(draft.BodyText, _defaults.BodyPreviewChars),
            SizeBytes = raw.LongLength,
            RequiresSmtpUtf8 = MessageBuilder.RequiresSmtpUtf8(spec),
            Digest = digest,
            Gate = gate,
        };
    }

    /// <summary>Rejects without a valid token, then applies the agent gates, then dispatches once.</summary>
    public async Task<SendResult> SendAsync(
        CallerContext caller,
        IMailSender sender,
        IMailProvider? provider,
        long outboxId,
        string? confirmToken,
        SendOptions? options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sender);

        if (caller.Kind == CallerKind.Internal)
        {
            throw new ArgumentException(
                "The internal caller is reserved for the daemon's own retry loop.",
                nameof(caller));
        }

        var record = _store.GetOutbox(outboxId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No outbox row with id {outboxId}.");

        var settings = options ?? _defaults;
        var digest = AuditText.Digest(record.Raw);
        var envelope = await ResolveEnvelopeAsync(record, ct).ConfigureAwait(false);

        // The agent gates run before the token is consumed so a denied call never burns it.
        var gate = _policy.EvaluateSend(caller, envelope.Recipients);
        if (!gate.Allowed)
        {
            await AuditAttemptAsync(caller, record, digest, gate.Label, envelope.Recipients.Count, false, gate.Reason.ToString(), AuditLog.LevelWarn, ct)
                .ConfigureAwait(false);
            gate.ThrowIfDenied();
        }

        if (!_tokens.TryConsume(confirmToken, outboxId, record.MessageId, digest))
        {
            await AuditAttemptAsync(caller, record, digest, "denied", envelope.Recipients.Count, false, "confirm-required", AuditLog.LevelWarn, ct)
                .ConfigureAwait(false);

            throw new ConfirmRequiredException(
                "A valid one-time confirm token from send.preview is required before a message is sent.");
        }

        _policy.RecordSend(caller);

        await AuditAttemptAsync(caller, record, digest, "allowed", envelope.Recipients.Count, true, null, AuditLog.LevelInfo, ct)
            .ConfigureAwait(false);

        var outcome = await DispatchAsync(caller, sender, provider, record, envelope, settings, ct).ConfigureAwait(false);
        if (outcome.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();

        return outcome.Result;
    }

    /// <summary>Retries whatever the schedule has made due. Failures are reported, never thrown.</summary>
    public async Task<IReadOnlyList<SendResult>> ProcessDueAsync(
        IMailSender sender,
        IMailProvider? provider,
        SendOptions? options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sender);

        var settings = options ?? _defaults;
        var due = _store.ListDueOutbox(_clock.UtcNow, ct);
        var results = new List<SendResult>();

        foreach (var row in due)
        {
            ct.ThrowIfCancellationRequested();

            // A row still marked sending is the crash window; only reconciliation may touch it.
            if (row.State != OutboxState.Queued) continue;

            var record = _store.GetOutbox(row.Id, ct);
            if (record is null) continue;

            var envelope = await ResolveEnvelopeAsync(record, ct).ConfigureAwait(false);
            var outcome = await DispatchAsync(CallerContext.Internal, sender, provider, record, envelope, settings, ct)
                .ConfigureAwait(false);

            results.Add(outcome.Result);
            if (outcome.Result.RequiresReconnect) break;
        }

        return results;
    }

    /// <summary>Resolves every row stuck in sending against Sent before any retry. Never double-sends.</summary>
    public async Task<OutboxReconcileReport> ReconcileStuckSendsAsync(
        SyncEngine? syncEngine,
        IMailProvider? provider,
        SendOptions? options,
        CancellationToken ct)
    {
        var settings = options ?? _defaults;
        var stuck = _store.ListOutbox(OutboxState.Sending, ct);
        if (stuck.Count == 0) return OutboxReconcileReport.Empty;

        var searched = new Dictionary<long, bool>();
        var outcomes = new List<OutboxReconcileOutcome>(stuck.Count);
        var markedSent = 0;
        var requeued = 0;
        var investigate = 0;

        foreach (var row in stuck)
        {
            ct.ThrowIfCancellationRequested();

            var sent = FindSentFolder(row.AccountId, ct);

            if (!searched.TryGetValue(row.AccountId.Value, out var refreshed))
            {
                refreshed = false;
                if (sent is not null && syncEngine is not null && provider is not null)
                {
                    await syncEngine.SyncFolderAsync(provider, sent.Id, null, ct).ConfigureAwait(false);
                    refreshed = true;
                }

                searched[row.AccountId.Value] = refreshed;
            }

            var found = sent is not null && IsInFolder(sent.Id, row.MessageId, ct);

            var decision = OutboxReconciler.Decide(new OutboxReconcileInput
            {
                State = OutboxState.Sending,
                DispatchStarted = true,
                SmtpAccepted = false,
                SentFolderSearched = refreshed,
                FoundInSent = found,
                ServerAutoSavesToSent = settings.ServerAutoSavesToSent,
            });

            var message = Rehydrate(row);
            var nowUtc = _clock.UtcNow;

            switch (decision)
            {
                case SendReconciliation.MarkSent:
                    message.MarkSentByReconciliation("reconciled-from-sent");
                    await PersistAsync(message, row, ct).ConfigureAwait(false);
                    markedSent++;
                    break;

                case SendReconciliation.Resend:
                    message.MarkFailed(SmtpResult.NetworkFailure("interrupted before a reply was recorded"), nowUtc);
                    if (message.CanRetry)
                    {
                        message.Retry(nowUtc);
                        requeued++;
                    }

                    await PersistAsync(message, row, ct).ConfigureAwait(false);
                    break;

                default:
                    investigate++;
                    break;
            }

            await _audit.WriteAsync(
                new AuditEntry
                {
                    Event = decision == SendReconciliation.Investigate
                        ? AuditEvents.OutboxInvestigate
                        : AuditEvents.OutboxReconciled,
                    AccountId = row.AccountId,
                    Level = decision == SendReconciliation.Investigate ? AuditLog.LevelWarn : AuditLog.LevelInfo,
                    Caller = CallerContext.Internal,
                    Detail = AuditText.Fields(
                        ("outbox", AuditText.Number(row.Id)),
                        ("decision", decision.ToString()),
                        ("searched", AuditText.Bool(refreshed)),
                        ("found", AuditText.Bool(found)),
                        ("state", message.State.ToWireValue())),
                },
                ct).ConfigureAwait(false);

            outcomes.Add(new OutboxReconcileOutcome
            {
                OutboxId = row.Id,
                MessageId = row.MessageId,
                Decision = decision,
                State = message.State,
            });
        }

        return new OutboxReconcileReport
        {
            Outcomes = outcomes,
            MarkedSent = markedSent,
            Requeued = requeued,
            NeedsInvestigation = investigate,
        };
    }

    private async Task<DispatchOutcome> DispatchAsync(
        CallerContext caller,
        IMailSender sender,
        IMailProvider? provider,
        OutboxRecord record,
        SendEnvelope envelope,
        SendOptions settings,
        CancellationToken ct)
    {
        var message = Rehydrate(record);

        if (message.State == OutboxState.Sent)
        {
            return new DispatchOutcome(ToResult(message, 250, false, false), null);
        }

        if (message.State == OutboxState.Sending)
        {
            return new DispatchOutcome(
                ToResult(message, 0, false, false),
                new ProviderException(
                    FailureCategory.Busy,
                    "This message is mid-dispatch; reconcile it against Sent before another attempt."));
        }

        if (message.State == OutboxState.Failed)
        {
            if (!message.CanRetry)
            {
                return new DispatchOutcome(
                    ToResult(message, 0, false, false),
                    new ProviderException(FailureCategory.Unsupported, "This message will not be retried automatically."));
            }

            message.Retry(_clock.UtcNow);
        }

        message.BeginSending(_clock.UtcNow);
        await PersistAsync(message, record, ct).ConfigureAwait(false);

        string response;
        try
        {
            response = await sender.SendAsync(record.Raw, envelope.From, envelope.Recipients, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SmtpDeliveryException ex)
        {
            var result = SmtpResult.FromResponse(ex.StatusCode, ex.Message);
            return new DispatchOutcome(await FailAsync(caller, message, record, result, ex.RequiresReconnect, ct).ConfigureAwait(false), ex);
        }
        catch (ProviderException ex)
        {
            var result = SmtpResult.NetworkFailure(ex.Message);
            return new DispatchOutcome(await FailAsync(caller, message, record, result, false, ct).ConfigureAwait(false), ex);
        }

        var accepted = SmtpResult.Accepted(response);
        message.MarkSent(accepted);
        await PersistAsync(message, record, ct).ConfigureAwait(false);
        Forget(record.Id);

        var appended = false;
        if (settings.AppendToSent)
            appended = await AppendToSentAsync(provider, record, settings, ct).ConfigureAwait(false);

        await _audit.SendAsync(
            new SendAuditRecord
            {
                AccountId = record.AccountId,
                Method = AuditEvents.SendResult,
                Decision = "allowed",
                ArgsDigest = AuditText.Digest(record.Raw),
                Caller = caller,
                MessageId = record.MessageId,
                OutboxId = record.Id,
                RecipientCount = envelope.Recipients.Count,
                TokenConsumed = false,
                Reason = accepted.Code.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            ct).ConfigureAwait(false);

        return new DispatchOutcome(ToResult(message, accepted.Code, appended, false), null);
    }

    private async Task<SendResult> FailAsync(
        CallerContext caller,
        OutboxMessage message,
        OutboxRecord record,
        SmtpResult result,
        bool requiresReconnect,
        CancellationToken ct)
    {
        var nowUtc = _clock.UtcNow;
        var decision = message.MarkFailed(result, nowUtc);

        // ListDueOutbox only surfaces queued rows, so a retryable failure is requeued at its due time.
        if (decision.WillRetry && decision.NextAttemptUtc is { } next && message.CanRetry) message.Retry(next);

        await PersistAsync(message, record, ct).ConfigureAwait(false);

        await _audit.SendAsync(
            new SendAuditRecord
            {
                AccountId = record.AccountId,
                Method = AuditEvents.SendResult,
                Decision = "failed",
                ArgsDigest = AuditText.Digest(record.Raw),
                Caller = caller,
                MessageId = record.MessageId,
                OutboxId = record.Id,
                TokenConsumed = false,
                Level = decision.Permanent ? AuditLog.LevelError : AuditLog.LevelWarn,
                Reason = AuditText.Fields(
                    ("code", result.Code.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("permanent", AuditText.Bool(decision.Permanent))),
            },
            ct).ConfigureAwait(false);

        return ToResult(message, result.Code, false, requiresReconnect);
    }

    private async Task<bool> AppendToSentAsync(
        IMailProvider? provider,
        OutboxRecord record,
        SendOptions settings,
        CancellationToken ct)
    {
        if (provider is null || settings.ServerAutoSavesToSent) return false;

        var sent = FindSentFolder(record.AccountId, ct);
        if (sent is null) return false;

        // 26: the server may have raced us into Sent; the Message-ID is the dedupe key.
        if (IsInFolder(sent.Id, record.MessageId, ct)) return false;

        try
        {
            await provider.AppendAsync(
                new FolderRef(sent.Id, sent.Path),
                record.Raw,
                MessageFlags.None,
                _clock.UtcNow,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException ex)
        {
            await _audit.WarnAsync(
                AuditEvents.SentAppendFailed,
                record.AccountId,
                CallerContext.Internal,
                AuditText.Fields(("outbox", AuditText.Number(record.Id)), ("category", ex.Category.ToString())),
                ct).ConfigureAwait(false);
            return false;
        }

        await _audit.InfoAsync(
            AuditEvents.SentAppend,
            record.AccountId,
            CallerContext.Internal,
            AuditText.Fields(("outbox", AuditText.Number(record.Id)), ("folder", sent.Path.Value)),
            ct).ConfigureAwait(false);

        return true;
    }

    private FolderSummary? FindSentFolder(AccountId accountId, CancellationToken ct)
    {
        foreach (var folder in _store.ListFolders(accountId, ct))
            if (folder.Role == FolderRole.Sent) return folder;

        return null;
    }

    private bool IsInFolder(FolderId folderId, MessageId messageId, CancellationToken ct)
    {
        foreach (var row in _store.FindByMessageId(messageId, ct))
            if (row.FolderId == folderId) return true;

        return false;
    }

    private async Task<SendEnvelope> ResolveEnvelopeAsync(OutboxRecord record, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_envelopes.TryGetValue(record.Id, out var cached)) return cached;
        }

        var parsed = _parser.Parse(record.Raw, record.CreatedUtc, ct);
        if (!RecipientExtractor.TryExtract(parsed, out var recipients, out _))
        {
            throw new ProviderException(
                FailureCategory.Protocol,
                $"The queued message {record.Id} carries no usable recipient addresses.");
        }

        var envelope = new SendEnvelope(recipients.From, recipients.AllRecipients());
        Remember(record.Id, envelope);

        // Bcc is not recoverable from a parsed message, so a resend after a restart reaches To and Cc only.
        await _audit.WarnAsync(
            AuditEvents.OutboxRecipientsRecovered,
            record.AccountId,
            CallerContext.Internal,
            AuditText.Fields(
                ("outbox", AuditText.Number(record.Id)),
                ("recipients", AuditText.Number(envelope.Recipients.Count))),
            ct).ConfigureAwait(false);

        return envelope;
    }

    private Task AuditAttemptAsync(
        CallerContext caller,
        OutboxRecord record,
        string digest,
        string decision,
        int recipientCount,
        bool tokenConsumed,
        string? reason,
        string level,
        CancellationToken ct) =>
        _audit.SendAsync(
            new SendAuditRecord
            {
                AccountId = record.AccountId,
                Method = AuditEvents.SendAttempt,
                Decision = decision,
                ArgsDigest = digest,
                Caller = caller,
                MessageId = record.MessageId,
                OutboxId = record.Id,
                RecipientCount = recipientCount,
                TokenConsumed = tokenConsumed,
                Reason = reason,
                Level = level,
            },
            ct);

    private void Remember(long outboxId, SendEnvelope envelope)
    {
        lock (_gate)
        {
            if (_envelopes.Count > 256) _envelopes.Clear();
            _envelopes[outboxId] = envelope;
        }
    }

    private void Forget(long outboxId)
    {
        lock (_gate) _envelopes.Remove(outboxId);
    }

    private Task PersistAsync(OutboxMessage message, OutboxRecord record, CancellationToken ct) =>
        _store.SaveOutboxAsync(
            new OutboxRecord
            {
                Id = record.Id,
                AccountId = message.AccountId,
                MessageId = message.MessageId,
                State = message.State,
                SmtpResponse = message.SmtpResponse,
                Attempts = message.Attempts,
                NextAttemptUtc = message.NextAttemptUtc,
                CreatedUtc = message.CreatedUtc,
            },
            ct);

    private static OutboxMessage Rehydrate(OutboxRecord record) =>
        OutboxMessage.Rehydrate(
            record.Id,
            record.AccountId,
            record.MessageId,
            record.State,
            record.Attempts,
            record.CreatedUtc,
            record.NextAttemptUtc,
            null,
            record.SmtpResponse,
            null,
            record.State == OutboxState.Failed && record.NextAttemptUtc is null);

    private static SendResult ToResult(OutboxMessage message, int statusCode, bool appended, bool requiresReconnect) =>
        new()
        {
            OutboxId = message.Id,
            MessageId = message.MessageId,
            State = message.State,
            StatusCode = statusCode,
            SmtpResponse = message.SmtpResponse,
            Attempts = message.Attempts,
            NextAttemptUtc = message.NextAttemptUtc,
            AppendedToSent = appended,
            RequiresReconnect = requiresReconnect,
            PermanentlyFailed = message.PermanentlyFailed,
        };

    private static EmailAddress ParseAccountAddress(AccountConfig account) =>
        EmailAddress.TryParse(account.Email, out var address)
            ? address
            : throw new StoreException(FailureCategory.Protocol, "The stored account address is not a valid address.");

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (maxChars <= 0) return string.Empty;
        return value.Length <= maxChars ? value : value[..maxChars];
    }

    private readonly record struct SendEnvelope(EmailAddress From, IReadOnlyList<EmailAddress> Recipients);

    private readonly record struct DispatchOutcome(SendResult Result, Exception? Failure);
}
