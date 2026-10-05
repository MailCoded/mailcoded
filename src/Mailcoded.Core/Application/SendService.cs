using System.Runtime.ExceptionServices;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
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

    /// <summary>The quoted original of a reply, kept apart so a signature lands between the new text and the quote.</summary>
    public string? QuotedText { get; init; }

    public SignatureChoice Signature { get; init; }

    public MessageId? InReplyTo { get; init; }
    public IReadOnlyList<MessageId> References { get; init; } = [];

    /// <summary>Overrides the account address; must be the account address or one of its identities.</summary>
    public EmailAddress? From { get; init; }

    public string? FromDisplayName { get; init; }
}

/// <summary>A draft after its sender was checked and its signature placed. This is what gets built and previewed.</summary>
public sealed record PreparedDraft
{
    public required EmailAddress From { get; init; }
    public string? FromDisplayName { get; init; }
    public EmailAddress? ReplyTo { get; init; }
    public required string BodyText { get; init; }

    /// <summary>The name of the signature that was inserted, or null when none was.</summary>
    public string? SignatureName { get; init; }
}

public sealed record SendOptions
{
    /// <summary>True for servers that copy a submission into Sent themselves, making APPEND a duplicate.</summary>
    public bool ServerAutoSavesToSent { get; init; }

    public bool AppendToSent { get; init; } = true;

    public int BodyPreviewChars { get; init; } = 2_000;

    public static readonly SendOptions Default = new();
}

/// <summary>Whether the draft also went to the server's Drafts Folder.</summary>
public enum ServerDraftSave
{
    Off,
    Saved,
    Failed,
}

public static class ServerDraftSaveExtensions
{
    public static string ToWireValue(this ServerDraftSave save) => save switch
    {
        ServerDraftSave.Off => "off",
        ServerDraftSave.Saved => "saved",
        ServerDraftSave.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(save)),
    };
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
    public bool BodyTruncated { get; init; }
    public string? SignatureName { get; init; }
    public long SizeBytes { get; init; }
    public bool RequiresSmtpUtf8 { get; init; }
    public required string Digest { get; init; }

    /// <summary>What the send gate would decide right now, so a caller learns before it burns the token.</summary>
    public required SendGateDecision Gate { get; init; }

    public ServerDraftSave ServerDraft { get; init; }

    /// <summary>Why the server copy was not made; <see cref="ServerDraftReason.None"/> unless it failed.</summary>
    public ServerDraftReason ServerDraftReason { get; init; }
}

public sealed record SendResult
{
    public required long OutboxId { get; init; }
    public required MessageId MessageId { get; init; }
    public required OutboxState State { get; init; }
    public int StatusCode { get; init; }
    public string? SmtpResponse { get; init; }

    /// <summary>RFC 3463 status from the last reply, when the server sent one.</summary>
    public string? EnhancedStatusCode { get; init; }

    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public bool AppendedToSent { get; init; }
    public bool RequiresReconnect { get; init; }
    public bool PermanentlyFailed { get; init; }

    /// <summary>The server draft's state after this attempt; null when the draft was never saved to the server.</summary>
    public ServerDraftState? ServerDraft { get; init; }

    public ServerDraftReason ServerDraftReason { get; init; }
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

    /// <summary>Server drafts whose send finished but whose move did not; they are left where they are, never retried.</summary>
    public int ServerDraftsLeft { get; init; }

    public static readonly OutboxReconcileReport Empty = new();
}

/// <summary>Two-phase send, the safety gates, Sent reconciliation, and the retry schedule.</summary>
public sealed class SendService
{
    private const int GenericPermanentCode = 554;

    private readonly SqliteStore _store;
    private readonly MessageParser _parser;
    private readonly IClock _clock;
    private readonly AuditLog _audit;
    private readonly AgentPolicy _policy;
    private readonly ConfirmTokenStore _tokens;
    private readonly SendOptions _defaults;

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

        // A confirm token and the hourly send budget both have to outlive the one-shot CLI process.
        tokens.AttachStore(store);
        policy.AttachStore(store);
    }

    public IReadOnlyList<OutboxRecord> ListOutbox(
        OutboxState? state = null,
        CancellationToken ct = default,
        bool confirmedOnly = false) =>
        _store.ListOutbox(state, ct, confirmedOnly);

    public Task<SendPreview> PreviewAsync(
        CallerContext caller,
        AccountId accountId,
        DraftRequest draft,
        CancellationToken ct) =>
        PreviewAsync(caller, accountId, draft, null, ct);

    /// <summary>Builds the message, creates the outbox row and mints the confirm token. <paramref name="connect"/> is called only if the Account saves drafts to the server.</summary>
    public async Task<SendPreview> PreviewAsync(
        CallerContext caller,
        AccountId accountId,
        DraftRequest draft,
        Func<CancellationToken, Task<IMailProvider?>>? connect,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var prepared = await PrepareDraftAsync(caller, accountId, draft, ct).ConfigureAwait(false);
        var from = prepared.From;
        var messageId = MessageId.NewForDomain(from.Domain, Guid.NewGuid());

        var spec = new DraftSpec
        {
            From = from,
            FromDisplayName = prepared.FromDisplayName,
            ReplyTo = prepared.ReplyTo,
            To = draft.To,
            Cc = draft.Cc,
            Bcc = draft.Bcc,
            Subject = draft.Subject,
            BodyText = prepared.BodyText,
            InReplyTo = draft.InReplyTo,
            References = draft.References,
            MessageId = messageId,
            DateUtc = _clock.UtcNow,
        };

        var raw = MessageBuilder.Build(spec, ct);
        var addressed = RecipientExtractor.FromDraft(spec);
        var envelope = new OutboxEnvelope
        {
            From = addressed.From,
            To = addressed.To,
            Cc = addressed.Cc,
            Bcc = addressed.Bcc,
        };

        var recipients = envelope.AllRecipients();

        var outboxId = await _store.EnqueueOutboxAsync(
            new OutboxRecord
            {
                AccountId = accountId,
                MessageId = messageId,
                State = OutboxState.Queued,
                Raw = raw,
                Attempts = 0,
                MaxAttempts = OutboxMessage.DefaultMaxAttempts,
                CreatedUtc = _clock.UtcNow,
                Envelope = envelope,
            },
            ct).ConfigureAwait(false);

        var digest = AuditText.Digest(raw);
        var grant = await _tokens.IssueAsync(outboxId, messageId, digest, ct).ConfigureAwait(false);

        var gate = _policy.EvaluateSend(caller, recipients);
        var (saved, savedReason) = await SaveServerDraftAsync(accountId, outboxId, raw, connect, ct).ConfigureAwait(false);

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
            ConfirmToken = grant.Token,
            TokenLifetimeMs = (long)_tokens.Lifetime.TotalMilliseconds,
            From = from,
            To = draft.To,
            Cc = draft.Cc,
            Bcc = draft.Bcc,
            Subject = draft.Subject,
            BodyPreview = Truncate(prepared.BodyText, _defaults.BodyPreviewChars),
            BodyTruncated = prepared.BodyText.Length > _defaults.BodyPreviewChars,
            SignatureName = prepared.SignatureName,
            SizeBytes = raw.LongLength,
            RequiresSmtpUtf8 = MessageBuilder.RequiresSmtpUtf8(spec),
            Digest = digest,
            Gate = gate,
            ServerDraft = saved,
            ServerDraftReason = savedReason,
        };
    }

    private async Task<(ServerDraftSave Save, ServerDraftReason Reason)> SaveServerDraftAsync(
        AccountId accountId,
        long outboxId,
        byte[] raw,
        Func<CancellationToken, Task<IMailProvider?>>? connect,
        CancellationToken ct)
    {
        if (connect is null || _store.GetAccount(accountId, ct) is not { SaveDraftsToServer: true })
            return (ServerDraftSave.Off, ServerDraftReason.None);

        var drafts = FindFolder(accountId, FolderRole.Drafts, ct);
        if (drafts is null) return await SaveFailedAsync(accountId, outboxId, ServerDraftReason.NoDraftsFolder, null, ct).ConfigureAwait(false);

        var target = new FolderRef(drafts.Id, drafts.Path);
        Uid? uid;
        IMailProvider provider;

        try
        {
            if (await connect(ct).ConfigureAwait(false) is not { } connected)
                return await SaveFailedAsync(accountId, outboxId, ServerDraftReason.NotConnected, null, ct).ConfigureAwait(false);

            provider = connected;
            uid = await provider.AppendAsync(target, raw, MessageFlags.Draft, _clock.UtcNow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ProviderException or SecretStoreException)
        {
            return await SaveFailedAsync(accountId, outboxId, ServerDraftReason.AppendFailed, ex, ct).ConfigureAwait(false);
        }

        // The copy exists on the server from here on, so it must be on record even if the UID or UIDVALIDITY is unknown.
        UidValidity validity;
        try
        {
            validity = (await provider.OpenFolderAsync(target, true, ct).ConfigureAwait(false)).UidValidity;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException)
        {
            validity = UidValidity.Unknown;
        }

        var trackable = uid is not null && !validity.IsUnknown;

        await _store.RecordServerDraftAsync(
            outboxId,
            new ServerDraft
            {
                FolderPath = drafts.Path.Value,
                UidValidity = validity,
                Uid = uid,
                State = trackable ? ServerDraftState.Saved : ServerDraftState.Left,
                Reason = trackable ? ServerDraftReason.None : ServerDraftReason.UidUnknown,
            },
            ct).ConfigureAwait(false);

        await _audit.InfoAsync(
            AuditEvents.ServerDraftSaved,
            accountId,
            CallerContext.Internal,
            AuditText.Fields(
                ("outbox", AuditText.Number(outboxId)),
                ("folder", drafts.Path.Value),
                ("tracked", AuditText.Bool(trackable))),
            ct).ConfigureAwait(false);

        return (ServerDraftSave.Saved, ServerDraftReason.None);
    }

    private async Task<(ServerDraftSave Save, ServerDraftReason Reason)> SaveFailedAsync(
        AccountId accountId,
        long outboxId,
        ServerDraftReason reason,
        Exception? error,
        CancellationToken ct)
    {
        await _audit.WarnAsync(
            AuditEvents.ServerDraftSaveFailed,
            accountId,
            CallerContext.Internal,
            AuditText.Fields(
                ("outbox", AuditText.Number(outboxId)),
                ("reason", reason.ToWireValue()),
                ("category", error is ProviderException provider ? provider.Category.ToString() : null)),
            ct).ConfigureAwait(false);

        return (ServerDraftSave.Failed, reason);
    }

    /// <summary>Decides a draft's sender and places its signature; the one place that happens, so no adapter can skip it.</summary>
    public async Task<PreparedDraft> PrepareDraftAsync(
        CallerContext caller,
        AccountId accountId,
        DraftRequest draft,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var account = _store.GetAccount(accountId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No account with id {accountId.Value}.");

        var own = ParseAccountAddress(account);
        var requested = draft.From ?? DefaultFromForReply(account, own, draft, ct);

        if (!SenderIdentities.TryResolve(own, account.DisplayName, account.Identities, requested, out var sender))
        {
            await _audit.WarnAsync(
                AuditEvents.SenderDenied,
                accountId,
                caller,
                AuditText.Fields(("from", draft.From?.Value)),
                ct).ConfigureAwait(false);

            throw new SenderNotAllowedException();
        }

        var chosen = draft.Signature;
        var use = draft.InReplyTo is null ? SignatureUse.New : SignatureUse.Reply;

        // An implicit default would sign twice when the author already wrote a signature of their own.
        if (chosen.Kind == SignatureChoiceKind.Default && DraftBody.HasSeparator(draft.BodyText))
            chosen = SignatureChoice.None;

        var signature = account.Signatures.Select(chosen, use);

        return new PreparedDraft
        {
            From = sender.Address,
            FromDisplayName = draft.FromDisplayName ?? sender.DisplayName,
            ReplyTo = sender.ReplyTo,
            BodyText = DraftBody.Compose(draft.BodyText, signature?.Text, draft.QuotedText),
            SignatureName = signature?.Name,
        };
    }

    private EmailAddress DefaultFromForReply(AccountConfig account, EmailAddress own, DraftRequest draft, CancellationToken ct)
    {
        if (account.Identities.Count == 0 || draft.InReplyTo is not { } parent) return own;

        foreach (var row in _store.FindByMessageId(parent, ct))
        {
            if (row.AccountId != account.Id) continue;

            var recipients = new List<EmailAddress>();
            recipients.AddRange(RecipientExtractor.ParseAddressList(row.To, out _));
            recipients.AddRange(RecipientExtractor.ParseAddressList(row.Cc, out _));
            return SenderIdentities.PickForReply(own, account.Identities, recipients);
        }

        return own;
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
        var envelope = ResolveEnvelope(record, ct);

        // The agent gates run and the budget slot is taken before the token is consumed, so neither
        // a denial nor a spent budget burns it.
        var reservation = await _policy.TryReserveSendAsync(caller, envelope.Recipients, ct).ConfigureAwait(false);
        if (!reservation.Granted)
        {
            var gate = reservation.Decision;
            await AuditAttemptAsync(caller, record, digest, gate.Label, envelope.Recipients.Count, false, gate.Reason.ToString(), AuditLog.LevelWarn, ct)
                .ConfigureAwait(false);
            gate.ThrowIfDenied();
        }

        var consumed = await _tokens
            .TryConsumeAsync(confirmToken, outboxId, record.MessageId, digest, ct)
            .ConfigureAwait(false);

        if (!consumed)
        {
            await _policy.ReleaseSendAsync(reservation, ct).ConfigureAwait(false);
            await AuditAttemptAsync(caller, record, digest, "denied", envelope.Recipients.Count, false, "confirm-required", AuditLog.LevelWarn, ct)
                .ConfigureAwait(false);

            throw new ConfirmRequiredException(
                "A valid one-time confirm token from send.preview is required before a message is sent.");
        }

        // The token is spent, so a human has agreed: from here the row is real work the retry loop
        // may pick up if this attempt does not finish.
        await _store.MarkOutboxConfirmedAsync(outboxId, _clock.UtcNow, ct).ConfigureAwait(false);

        await AuditAttemptAsync(caller, record, digest, "allowed", envelope.Recipients.Count, true, null, AuditLog.LevelInfo, ct)
            .ConfigureAwait(false);

        var outcome = await DispatchAsync(caller, sender, provider, record, envelope, settings, ct).ConfigureAwait(false);

        // Nothing reached the wire, so the slot was never spent.
        if (!outcome.Dispatched) await _policy.ReleaseSendAsync(reservation, ct).ConfigureAwait(false);

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
            // A retryable failed row is due work: DispatchAsync requeues it before it dispatches.
            if (row.State == OutboxState.Sending) continue;

            var record = _store.GetOutbox(row.Id, ct);
            if (record is null) continue;

            var envelope = ResolveEnvelope(record, ct);
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
        if (stuck.Count == 0)
        {
            var idle = await LeaveInterruptedServerDraftsAsync(ct).ConfigureAwait(false);
            return idle == 0 ? OutboxReconcileReport.Empty : new OutboxReconcileReport { ServerDraftsLeft = idle };
        }

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
            ServerDraftsLeft = await LeaveInterruptedServerDraftsAsync(ct).ConfigureAwait(false),
        };
    }

    private async Task<int> LeaveInterruptedServerDraftsAsync(CancellationToken ct)
    {
        var count = await _store.LeaveInterruptedServerDraftsAsync(ct).ConfigureAwait(false);

        if (count > 0)
        {
            await _audit.WarnAsync(
                AuditEvents.ServerDraftLeft,
                AccountId.None,
                CallerContext.Internal,
                AuditText.Fields(("count", AuditText.Number(count)), ("reason", ServerDraftReason.Interrupted.ToWireValue())),
                ct).ConfigureAwait(false);
        }

        return count;
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
            return new DispatchOutcome(ToResult(message, 250, false, false, record.ServerDraft), null, false);
        }

        if (message.State == OutboxState.Sending)
        {
            return new DispatchOutcome(
                ToResult(message, 0, false, false, record.ServerDraft),
                new ProviderException(
                    FailureCategory.Busy,
                    "This message is mid-dispatch; reconcile it against Sent before another attempt."),
                false);
        }

        if (message.State == OutboxState.Failed)
        {
            if (!message.CanRetry)
            {
                return new DispatchOutcome(
                    ToResult(message, 0, false, false, record.ServerDraft),
                    new ProviderException(FailureCategory.Permanent, "This message will not be retried automatically."),
                    false);
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
            return new DispatchOutcome(await FailAsync(caller, message, record, result, ex.RequiresReconnect, ct).ConfigureAwait(false), ex, true);
        }
        catch (ProviderException ex)
        {
            // The aggregate reads permanence off the reply code, so a terminal failure that never
            // produced one is recorded as the generic 554.
            var result = ex.IsPermanent
                ? SmtpResult.FromResponse(GenericPermanentCode, ex.Message)
                : SmtpResult.NetworkFailure(ex.Message);
            return new DispatchOutcome(await FailAsync(caller, message, record, result, false, ct).ConfigureAwait(false), ex, true);
        }

        var accepted = SmtpResult.Accepted(response);
        message.MarkSent(accepted);
        await PersistAsync(message, record, ct).ConfigureAwait(false);

        var settled = await SettleServerDraftAsync(provider, record, settings, ct).ConfigureAwait(false);

        var appended = false;
        if (settings.AppendToSent && settled.SentCopyStillNeeded)
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

        return new DispatchOutcome(
            ToResult(message, accepted.Code, appended, false, settled.State, settled.Reason),
            null,
            true);
    }

    /// <summary>One atomic MOVE of the server draft into Sent, or nothing. The state is written as moving first, so a crash never causes a second move or copy.</summary>
    private async Task<SettledDraft> SettleServerDraftAsync(
        IMailProvider? provider,
        OutboxRecord record,
        SendOptions settings,
        CancellationToken ct)
    {
        if (record.ServerDraft is not { } draft) return new SettledDraft(null, ServerDraftReason.None, true);
        if (draft.State != ServerDraftState.Saved) return Describe(draft.State, draft.Reason);

        var folders = _store.ListFolders(record.AccountId, ct);
        FolderSummary? drafts = null;
        foreach (var folder in folders)
        {
            if (string.Equals(folder.Path.Value, draft.FolderPath, StringComparison.Ordinal)) drafts = folder;
        }

        var sent = FindSentFolder(record.AccountId, ct);

        var plan = ServerDraftPolicy.Plan(new ServerDraftPlanInput
        {
            Connected = provider is not null,
            SupportsMove = provider?.Capabilities.Move == true,
            AppendToSent = settings.AppendToSent,
            ServerSavesSent = settings.ServerAutoSavesToSent || provider?.Capabilities.GmailExtensions == true,
            SentFolderKnown = sent is not null,
            DraftsFolderKnown = drafts is not null,
        });

        if (!plan.Move || provider is null || sent is null || drafts is null || draft.Uid is not { } uid)
        {
            var reason = plan.Move ? ServerDraftReason.UidUnknown : plan.Reason;
            return await LeaveServerDraftAsync(record, ServerDraftState.Saved, reason, true, ct).ConfigureAwait(false);
        }

        if (!await _store.TransitionServerDraftAsync(record.Id, ServerDraftState.Saved, ServerDraftState.Moving, ServerDraftReason.None, ct).ConfigureAwait(false))
        {
            var current = _store.GetOutbox(record.Id, ct)?.ServerDraft;
            return current is null ? new SettledDraft(null, ServerDraftReason.None, true) : Describe(current.State, current.Reason);
        }

        var sentRef = new FolderRef(sent.Id, sent.Path);
        ServerDraftSettlement settlement;
        Uid? movedTo = null;

        try
        {
            var result = await provider
                .MoveAtomicAsync(new FolderRef(drafts.Id, drafts.Path), uid, draft.UidValidity, sentRef, ct)
                .ConfigureAwait(false);

            settlement = ServerDraftPolicy.SettleMove(result.Status);
            movedTo = result.NewUid;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException ex)
        {
            settlement = ServerDraftPolicy.SettleFailure(ex.Category == FailureCategory.Network);
        }

        await _store.TransitionServerDraftAsync(record.Id, ServerDraftState.Moving, settlement.State, settlement.Reason, ct).ConfigureAwait(false);

        if (settlement.State == ServerDraftState.Moved)
        {
            var cleared = await ClearDraftFlagAsync(provider, sentRef, movedTo, ct).ConfigureAwait(false);
            await _audit.InfoAsync(
                AuditEvents.ServerDraftMoved,
                record.AccountId,
                CallerContext.Internal,
                AuditText.Fields(
                    ("outbox", AuditText.Number(record.Id)),
                    ("folder", sent.Path.Value),
                    ("flag_cleared", AuditText.Bool(cleared))),
                ct).ConfigureAwait(false);
        }
        else
        {
            await AuditLeftAsync(record, settlement.Reason, ct).ConfigureAwait(false);
        }

        return new SettledDraft(settlement.State, settlement.Reason, settlement.SentCopyStillNeeded);
    }

    private async Task<SettledDraft> LeaveServerDraftAsync(
        OutboxRecord record,
        ServerDraftState from,
        ServerDraftReason reason,
        bool sentCopyStillNeeded,
        CancellationToken ct)
    {
        await _store.TransitionServerDraftAsync(record.Id, from, ServerDraftState.Left, reason, ct).ConfigureAwait(false);
        await AuditLeftAsync(record, reason, ct).ConfigureAwait(false);
        return new SettledDraft(ServerDraftState.Left, reason, sentCopyStillNeeded);
    }

    private Task AuditLeftAsync(OutboxRecord record, ServerDraftReason reason, CancellationToken ct) =>
        _audit.WarnAsync(
            AuditEvents.ServerDraftLeft,
            record.AccountId,
            CallerContext.Internal,
            AuditText.Fields(("outbox", AuditText.Number(record.Id)), ("reason", reason.ToWireValue())),
            ct);

    /// <summary>A moved message would otherwise keep showing as a draft in Sent; failing to clear it costs nothing else.</summary>
    private static async Task<bool> ClearDraftFlagAsync(IMailProvider provider, FolderRef sent, Uid? movedTo, CancellationToken ct)
    {
        if (movedTo is not { } uid) return false;

        try
        {
            await provider.SetFlagsAsync(sent, uid, new FlagDelta { Remove = MessageFlags.Draft }, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException)
        {
            return false;
        }
    }

    private static SettledDraft Describe(ServerDraftState state, ServerDraftReason reason) =>
        new(state, reason, state == ServerDraftState.Left && reason != ServerDraftReason.MoveUnconfirmed);

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

        // A retryable failure is requeued at its own due time; a permanent one stays failed.
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

        return ToResult(message, result.Code, false, requiresReconnect, record.ServerDraft);
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

    private FolderSummary? FindSentFolder(AccountId accountId, CancellationToken ct) =>
        FindFolder(accountId, FolderRole.Sent, ct);

    private FolderSummary? FindFolder(AccountId accountId, FolderRole role, CancellationToken ct)
    {
        foreach (var folder in _store.ListFolders(accountId, ct))
            if (folder.Role == role) return folder;

        return null;
    }

    private bool IsInFolder(FolderId folderId, MessageId messageId, CancellationToken ct)
    {
        foreach (var row in _store.FindByMessageId(messageId, ct))
            if (row.FolderId == folderId) return true;

        return false;
    }

    /// <summary>The stored envelope is authoritative; parsing the raw bytes can never recover Bcc.</summary>
    private SendEnvelope ResolveEnvelope(OutboxRecord record, CancellationToken ct)
    {
        if (record.Envelope is { IsEmpty: false } stored)
            return new SendEnvelope(stored.From, stored.AllRecipients());

        var parsed = _parser.WithoutAttachmentText().Parse(record.Raw, record.CreatedUtc, ct);
        if (!RecipientExtractor.TryExtract(parsed, out var recipients, out _))
        {
            throw new ProviderException(
                FailureCategory.Protocol,
                $"The queued message {record.Id} carries no usable recipient addresses.");
        }

        return new SendEnvelope(recipients.From, recipients.AllRecipients());
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

    private Task PersistAsync(OutboxMessage message, OutboxRecord record, CancellationToken ct) =>
        _store.SaveOutboxAsync(
            new OutboxRecord
            {
                Id = record.Id,
                AccountId = message.AccountId,
                MessageId = message.MessageId,
                State = message.State,
                SmtpResponse = message.SmtpResponse,
                EnhancedStatusCode = message.EnhancedStatusCode,
                Attempts = message.Attempts,
                MaxAttempts = message.MaxAttempts,
                PermanentlyFailed = message.PermanentlyFailed,
                NextAttemptUtc = message.NextAttemptUtc,
                LastAttemptUtc = message.LastAttemptUtc,
                CreatedUtc = message.CreatedUtc,
                Envelope = record.Envelope,
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
            record.LastAttemptUtc,
            record.SmtpResponse,
            record.EnhancedStatusCode,
            record.PermanentlyFailed,
            record.MaxAttempts is { } budget && budget > 0 ? budget : OutboxMessage.DefaultMaxAttempts);

    private static SendResult ToResult(OutboxMessage message, int statusCode, bool appended, bool requiresReconnect, ServerDraft? draft) =>
        ToResult(message, statusCode, appended, requiresReconnect, draft?.State, draft?.Reason ?? ServerDraftReason.None);

    private static SendResult ToResult(
        OutboxMessage message,
        int statusCode,
        bool appended,
        bool requiresReconnect,
        ServerDraftState? serverDraft,
        ServerDraftReason serverDraftReason) =>
        new()
        {
            OutboxId = message.Id,
            MessageId = message.MessageId,
            State = message.State,
            StatusCode = statusCode,
            SmtpResponse = message.SmtpResponse,
            EnhancedStatusCode = message.EnhancedStatusCode,
            Attempts = message.Attempts,
            NextAttemptUtc = message.NextAttemptUtc,
            AppendedToSent = appended,
            RequiresReconnect = requiresReconnect,
            PermanentlyFailed = message.PermanentlyFailed,
            ServerDraft = serverDraft,
            ServerDraftReason = serverDraftReason,
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

    private readonly record struct SettledDraft(ServerDraftState? State, ServerDraftReason Reason, bool SentCopyStillNeeded);

    private readonly record struct DispatchOutcome(SendResult Result, Exception? Failure, bool Dispatched);
}
