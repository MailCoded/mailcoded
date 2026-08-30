using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Outbox;

/// <summary>The one aggregate. State changes happen only through these methods; illegal ones throw.</summary>
public sealed class OutboxMessage
{
    public const int DefaultMaxAttempts = 12;

    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(24);

    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
    ];

    private OutboxMessage(
        long id,
        AccountId accountId,
        MessageId messageId,
        OutboxState state,
        int attempts,
        DateTimeOffset createdUtc,
        DateTimeOffset? nextAttemptUtc,
        DateTimeOffset? lastAttemptUtc,
        string? smtpResponse,
        string? enhancedStatusCode,
        bool permanentlyFailed,
        int maxAttempts)
    {
        Id = id;
        AccountId = accountId;
        MessageId = messageId;
        State = state;
        Attempts = attempts;
        CreatedUtc = createdUtc;
        NextAttemptUtc = nextAttemptUtc;
        LastAttemptUtc = lastAttemptUtc;
        SmtpResponse = smtpResponse;
        EnhancedStatusCode = enhancedStatusCode;
        PermanentlyFailed = permanentlyFailed;
        MaxAttempts = maxAttempts;
    }

    /// <summary>Store row id; 0 until the insert assigns one.</summary>
    public long Id { get; private set; }

    public AccountId AccountId { get; }

    /// <summary>Assigned at creation and immutable: this is the idempotency key the crash window turns on.</summary>
    public MessageId MessageId { get; }

    public OutboxState State { get; private set; }
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset? NextAttemptUtc { get; private set; }
    public DateTimeOffset? LastAttemptUtc { get; private set; }
    public string? SmtpResponse { get; private set; }
    public string? EnhancedStatusCode { get; private set; }
    public bool PermanentlyFailed { get; private set; }

    public bool IsTerminal => State is OutboxState.Sent || (State is OutboxState.Failed && PermanentlyFailed);
    public bool CanRetry => State is OutboxState.Failed && !PermanentlyFailed && Attempts < MaxAttempts;

    public bool IsDue(DateTimeOffset nowUtc) =>
        CanRetry && (NextAttemptUtc is not { } next || next <= nowUtc);

    public static OutboxMessage Create(
        AccountId accountId,
        MessageId messageId,
        DateTimeOffset createdUtc,
        int maxAttempts = DefaultMaxAttempts)
    {
        if (accountId.IsNone)
            throw new ArgumentException("An outbox message needs an account.", nameof(accountId));
        if (string.IsNullOrWhiteSpace(messageId.Value))
            throw new ArgumentException("Message-ID must be assigned at creation, never at send time.", nameof(messageId));
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Retry budget must be at least one attempt.");

        return new OutboxMessage(
            0, accountId, messageId, OutboxState.Queued, 0, createdUtc,
            createdUtc, null, null, null, false, maxAttempts);
    }

    /// <summary>Rebuilds an aggregate from a persisted row without replaying transitions.</summary>
    public static OutboxMessage Rehydrate(
        long id,
        AccountId accountId,
        MessageId messageId,
        OutboxState state,
        int attempts,
        DateTimeOffset createdUtc,
        DateTimeOffset? nextAttemptUtc = null,
        DateTimeOffset? lastAttemptUtc = null,
        string? smtpResponse = null,
        string? enhancedStatusCode = null,
        bool permanentlyFailed = false,
        int maxAttempts = DefaultMaxAttempts)
    {
        if (string.IsNullOrWhiteSpace(messageId.Value))
            throw new ArgumentException("A persisted outbox row must carry its Message-ID.", nameof(messageId));
        if (attempts < 0)
            throw new ArgumentOutOfRangeException(nameof(attempts));
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));

        return new OutboxMessage(
            id, accountId, messageId, state, attempts, createdUtc,
            nextAttemptUtc, lastAttemptUtc, smtpResponse, enhancedStatusCode, permanentlyFailed, maxAttempts);
    }

    public void AssignStoreId(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (Id != 0) throw new InvalidOperationException("The outbox row id is already assigned.");
        Id = id;
    }

    public void BeginSending(DateTimeOffset nowUtc)
    {
        Require(OutboxState.Queued, nameof(BeginSending));

        State = OutboxState.Sending;
        Attempts++;
        LastAttemptUtc = nowUtc;
        NextAttemptUtc = null;
    }

    public void MarkSent(SmtpResult result)
    {
        Require(OutboxState.Sending, nameof(MarkSent));

        State = OutboxState.Sent;
        SmtpResponse = result.Response;
        EnhancedStatusCode = result.EnhancedStatusCode;
        NextAttemptUtc = null;
        PermanentlyFailed = false;
    }

    /// <summary>Records the terminal reply and schedules the next attempt. A 5xx is permanent.</summary>
    public OutboxRetryDecision MarkFailed(SmtpResult result, DateTimeOffset nowUtc)
    {
        Require(OutboxState.Sending, nameof(MarkFailed));
        if (result.IsSuccess)
            throw new InvalidOperationException("A 2xx reply is not a failure; call MarkSent instead.");

        State = OutboxState.Failed;
        SmtpResponse = result.Response;
        EnhancedStatusCode = result.EnhancedStatusCode;

        var permanent = result.IsPermanent;
        var exhausted = Attempts >= MaxAttempts;
        PermanentlyFailed = permanent || exhausted;

        var retryAfter = PermanentlyFailed ? (TimeSpan?)null : RetryDelayFor(Attempts);
        NextAttemptUtc = retryAfter is { } delay ? nowUtc + delay : null;

        return new OutboxRetryDecision(permanent, exhausted, retryAfter, NextAttemptUtc);
    }

    public void Retry(DateTimeOffset nowUtc)
    {
        Require(OutboxState.Failed, nameof(Retry));
        if (PermanentlyFailed)
            throw new InvalidOperationException("This message failed permanently; only an explicit user retry may requeue it.");
        if (Attempts >= MaxAttempts)
            throw new InvalidOperationException("The retry budget is exhausted.");

        State = OutboxState.Queued;
        NextAttemptUtc = nowUtc;
    }

    /// <summary>A human overriding a permanent failure. Attempts are kept; the budget is extended.</summary>
    public void RetryByUser(DateTimeOffset nowUtc, int additionalAttempts = 1)
    {
        Require(OutboxState.Failed, nameof(RetryByUser));
        if (additionalAttempts < 1) throw new ArgumentOutOfRangeException(nameof(additionalAttempts));

        PermanentlyFailed = false;
        MaxAttempts = Math.Max(MaxAttempts, Attempts + additionalAttempts);
        State = OutboxState.Queued;
        NextAttemptUtc = nowUtc;
    }

    /// <summary>Reconciliation concluded the message really did leave; close it without another dispatch.</summary>
    public void MarkSentByReconciliation(string detail)
    {
        Require(OutboxState.Sending, nameof(MarkSentByReconciliation));
        MarkSent(SmtpResult.Accepted(detail));
    }

    /// <summary>Greylisting schedule: 1, 5, 15, 30 minutes then doubling, capped at 24 hours.</summary>
    public static TimeSpan RetryDelayFor(int attempt)
    {
        if (attempt < 1) attempt = 1;
        if (attempt <= Schedule.Length) return Schedule[attempt - 1];

        var minutes = Schedule[^1].TotalMinutes * Math.Pow(2, attempt - Schedule.Length);
        return TimeSpan.FromMinutes(Math.Min(minutes, MaxRetryDelay.TotalMinutes));
    }

    private void Require(OutboxState expected, string operation)
    {
        if (State == expected) return;
        throw new InvalidOperationException(
            $"Outbox transition '{operation}' requires state '{expected.ToWireValue()}' but the message is '{State.ToWireValue()}'.");
    }
}

/// <summary>The outcome of a failed attempt. RetryAfter is null when nothing more will happen.</summary>
public sealed record OutboxRetryDecision(
    bool Permanent,
    bool BudgetExhausted,
    TimeSpan? RetryAfter,
    DateTimeOffset? NextAttemptUtc)
{
    public bool WillRetry => RetryAfter is not null;
}
