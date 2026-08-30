using System.Reflection;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>The one aggregate: legal transitions, illegal ones throwing, and the retry schedule.</summary>
public sealed class OutboxMessageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly AccountId Account = new(1);
    private static readonly MessageId Mid = MessageId.Parse("2f1c@mailcoded.local");

    private sealed record TransitionCase(
        string Name,
        string Rule,
        Func<OutboxMessage> Setup,
        Action<OutboxMessage> Invoke,
        bool Legal,
        Action<OutboxMessage>? Verify = null);

    private static readonly TransitionCase[] Transitions =
    [
        new TransitionCase(
            "legal/queued-to-sending",
            "BeginSending is the only way to leave Queued. It burns one attempt BEFORE the network call, so a crash "
            + "mid-dispatch can never be replayed for free.",
            Queued,
            m => m.BeginSending(T0),
            Legal: true,
            m =>
            {
                Assert.Equal(OutboxState.Sending, m.State);
                Assert.Equal(1, m.Attempts);
                Assert.Equal(T0, m.LastAttemptUtc);
                Assert.Null(m.NextAttemptUtc);
            }),

        new TransitionCase(
            "legal/sending-to-sent",
            "A 2xx reply closes the row for good.",
            Sending,
            m => m.MarkSent(SmtpResult.Accepted("250 2.0.0 OK")),
            Legal: true,
            m =>
            {
                Assert.Equal(OutboxState.Sent, m.State);
                Assert.True(m.IsTerminal);
                Assert.False(m.CanRetry);
                Assert.Null(m.NextAttemptUtc);
            }),

        new TransitionCase(
            "legal/sending-to-failed-transiently",
            "§14.5 case 28: a 4xx is transient. The row stays retryable and gets the next slot on the schedule.",
            Sending,
            m => m.MarkFailed(SmtpResult.FromResponse(451, "451 4.7.1 greylisted, try later"), T0),
            Legal: true,
            m =>
            {
                Assert.Equal(OutboxState.Failed, m.State);
                Assert.False(m.PermanentlyFailed);
                Assert.True(m.CanRetry);
                Assert.Equal(T0 + TimeSpan.FromMinutes(1), m.NextAttemptUtc);
            }),

        new TransitionCase(
            "legal/sending-to-failed-permanently",
            "§14.5 case 28: a 5xx is permanent. Retrying it only burns reputation, so the row goes terminal and "
            + "surfaces to the user instead.",
            Sending,
            m => m.MarkFailed(SmtpResult.FromResponse(550, "550 5.1.1 no such user"), T0),
            Legal: true,
            m =>
            {
                Assert.True(m.PermanentlyFailed);
                Assert.True(m.IsTerminal);
                Assert.False(m.CanRetry);
                Assert.Null(m.NextAttemptUtc);
            }),

        new TransitionCase(
            "legal/failed-to-queued",
            "Failed -> Queued is the only re-entry, and it is what makes the scheduler idempotent.",
            Failed,
            m => m.Retry(T0 + TimeSpan.FromMinutes(1)),
            Legal: true,
            m =>
            {
                Assert.Equal(OutboxState.Queued, m.State);
                Assert.Equal(T0 + TimeSpan.FromMinutes(1), m.NextAttemptUtc);
                Assert.Equal(1, m.Attempts);
            }),

        new TransitionCase(
            "legal/permanently-failed-to-queued-by-a-human",
            "A human may override a permanent failure. Attempts are kept so the history stays honest; only the "
            + "budget moves.",
            PermanentlyFailed,
            m => m.RetryByUser(T0, additionalAttempts: 2),
            Legal: true,
            m =>
            {
                Assert.Equal(OutboxState.Queued, m.State);
                Assert.False(m.PermanentlyFailed);
                Assert.Equal(1, m.Attempts);
                Assert.True(m.MaxAttempts >= 3);
            }),

        new TransitionCase(
            "legal/sending-to-sent-by-reconciliation",
            "Reconciliation closes a crash-window row without a second dispatch — the whole point of the rule.",
            Sending,
            m => m.MarkSentByReconciliation("found in Sent by Message-ID"),
            Legal: true,
            m =>
            {
                Assert.Equal(OutboxState.Sent, m.State);
                Assert.False(m.PermanentlyFailed);
            }),

        new TransitionCase(
            "illegal/sending-to-sending",
            "Two dispatches of one row is the double-send this state machine exists to prevent.",
            Sending,
            m => m.BeginSending(T0),
            Legal: false),

        new TransitionCase(
            "illegal/sent-to-sending",
            "A sent message is terminal; re-dispatching it would deliver twice.",
            Sent,
            m => m.BeginSending(T0),
            Legal: false),

        new TransitionCase(
            "illegal/failed-to-sending",
            "A failed row must be requeued through Retry, which re-checks the budget and the permanence flag.",
            Failed,
            m => m.BeginSending(T0),
            Legal: false),

        new TransitionCase(
            "illegal/queued-to-sent",
            "Nothing may be recorded as sent that was never dispatched.",
            Queued,
            m => m.MarkSent(SmtpResult.Accepted("250 OK")),
            Legal: false),

        new TransitionCase(
            "illegal/sent-to-sent",
            "Sent is terminal; a second success would double-count the send.",
            Sent,
            m => m.MarkSent(SmtpResult.Accepted("250 OK")),
            Legal: false),

        new TransitionCase(
            "illegal/failed-to-sent",
            "A failed row cannot become sent without another attempt.",
            Failed,
            m => m.MarkSent(SmtpResult.Accepted("250 OK")),
            Legal: false),

        new TransitionCase(
            "illegal/queued-to-failed",
            "There is no attempt to fail yet.",
            Queued,
            m => m.MarkFailed(SmtpResult.FromResponse(451, "451 later"), T0),
            Legal: false),

        new TransitionCase(
            "illegal/sent-to-failed",
            "A delivered message cannot retroactively fail.",
            Sent,
            m => m.MarkFailed(SmtpResult.FromResponse(451, "451 later"), T0),
            Legal: false),

        new TransitionCase(
            "illegal/failed-to-failed",
            "Recording a second failure without a second attempt would corrupt the backoff schedule.",
            Failed,
            m => m.MarkFailed(SmtpResult.FromResponse(451, "451 later"), T0),
            Legal: false),

        new TransitionCase(
            "illegal/success-reported-through-markfailed",
            "A 2xx is not a failure. Accepting it here would schedule a retry for a message that already left.",
            Sending,
            m => m.MarkFailed(SmtpResult.Accepted("250 OK"), T0),
            Legal: false),

        new TransitionCase(
            "illegal/queued-to-queued-by-retry",
            "Retry re-enters from Failed only; from Queued it would reset the schedule of a live row.",
            Queued,
            m => m.Retry(T0),
            Legal: false),

        new TransitionCase(
            "illegal/sending-to-queued-by-retry",
            "Requeueing a row that is mid-dispatch is exactly how a message gets sent twice.",
            Sending,
            m => m.Retry(T0),
            Legal: false),

        new TransitionCase(
            "illegal/sent-to-queued-by-retry",
            "Sent is terminal.",
            Sent,
            m => m.Retry(T0),
            Legal: false),

        new TransitionCase(
            "illegal/permanently-failed-to-queued-by-retry",
            "Only an explicit human retry may requeue a permanent failure; the scheduler must not.",
            PermanentlyFailed,
            m => m.Retry(T0),
            Legal: false),

        new TransitionCase(
            "illegal/exhausted-budget-to-queued-by-retry",
            "The retry budget is a hard stop. Without it a permanently broken recipient retries forever.",
            ExhaustedBudget,
            m => m.Retry(T0),
            Legal: false),

        new TransitionCase(
            "illegal/queued-to-queued-by-user-retry",
            "A user retry also re-enters from Failed only.",
            Queued,
            m => m.RetryByUser(T0),
            Legal: false),

        new TransitionCase(
            "illegal/sending-to-queued-by-user-retry",
            "A row mid-dispatch has an unknown outcome; reconciliation decides it, not the user.",
            Sending,
            m => m.RetryByUser(T0),
            Legal: false),

        new TransitionCase(
            "illegal/sent-to-queued-by-user-retry",
            "Sent is terminal.",
            Sent,
            m => m.RetryByUser(T0),
            Legal: false),

        new TransitionCase(
            "illegal/queued-marked-sent-by-reconciliation",
            "Reconciliation applies to the crash window only, which is the Sending state.",
            Queued,
            m => m.MarkSentByReconciliation("found in Sent"),
            Legal: false),
    ];

    public static TheoryData<string> TransitionRows
    {
        get
        {
            var rows = new TheoryData<string>();
            foreach (var row in Transitions) rows.Add(row.Name);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(TransitionRows))]
    public void The_outbox_state_machine_allows_exactly_the_legal_transitions(string name)
    {
        var row = Transitions.Single(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        var message = row.Setup();

        if (row.Legal)
        {
            row.Invoke(message);
            row.Verify?.Invoke(message);
            return;
        }

        var before = message.State;
        Assert.Throws<InvalidOperationException>(() => row.Invoke(message));

        Assert.True(
            message.State == before,
            $"[{row.Name}] {row.Rule} A rejected transition must also leave the aggregate untouched; it moved from "
            + $"{before} to {message.State}.");
    }

    [Fact]
    public void Every_transition_row_has_a_unique_name()
    {
        var duplicates = Transitions.GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void A_new_message_starts_queued_and_due_immediately()
    {
        var message = OutboxMessage.Create(Account, Mid, T0);

        Assert.Equal(OutboxState.Queued, message.State);
        Assert.Equal(0, message.Attempts);
        Assert.Equal(OutboxMessage.DefaultMaxAttempts, message.MaxAttempts);
        Assert.Equal(T0, message.CreatedUtc);
        Assert.Equal(T0, message.NextAttemptUtc);
        Assert.Null(message.LastAttemptUtc);
        Assert.False(message.PermanentlyFailed);
        Assert.False(message.IsTerminal);
        Assert.Equal(0L, message.Id);
    }

    [Fact]
    public void Creation_refuses_a_message_that_could_not_be_deduplicated_later()
    {
        Assert.Throws<ArgumentException>(() => OutboxMessage.Create(AccountId.None, Mid, T0));
        Assert.Throws<ArgumentException>(() => OutboxMessage.Create(Account, default, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutboxMessage.Create(Account, Mid, T0, maxAttempts: 0));
    }

    [Fact]
    public void The_message_id_is_assigned_at_creation_and_can_never_be_written_again()
    {
        var property = typeof(OutboxMessage).GetProperty(nameof(OutboxMessage.MessageId));

        Assert.True(property is not null, "OutboxMessage.MessageId must remain a public, readable property.");
        Assert.False(
            property!.CanWrite,
            "MessageId is the idempotency key the crash window turns on. If it could be reassigned — even privately "
            + "at send time — a retry could ship a second copy that Sent-folder de-duplication would never catch.");
        Assert.Null(property!.SetMethod);
    }

    [Fact]
    public void The_message_id_survives_every_transition()
    {
        var message = OutboxMessage.Create(Account, Mid, T0);

        message.BeginSending(T0);
        Assert.Equal(Mid, message.MessageId);

        message.MarkFailed(SmtpResult.FromResponse(451, "451 later"), T0);
        Assert.Equal(Mid, message.MessageId);

        message.Retry(T0);
        Assert.Equal(Mid, message.MessageId);

        message.BeginSending(T0);
        message.MarkSent(SmtpResult.Accepted("250 OK"));
        Assert.Equal(Mid, message.MessageId);
    }

    [Fact]
    public void The_store_id_is_assigned_once()
    {
        var message = OutboxMessage.Create(Account, Mid, T0);

        message.AssignStoreId(17);
        Assert.Equal(17L, message.Id);

        Assert.Throws<InvalidOperationException>(() => message.AssignStoreId(18));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutboxMessage.Create(Account, Mid, T0).AssignStoreId(0));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 30)]
    [InlineData(5, 60)]
    [InlineData(6, 120)]
    [InlineData(7, 240)]
    [InlineData(8, 480)]
    [InlineData(9, 960)]
    [InlineData(10, 1440)]
    [InlineData(11, 1440)]
    [InlineData(50, 1440)]
    [InlineData(int.MaxValue, 1440)]
    public void The_greylist_schedule_is_one_five_fifteen_thirty_then_doubling_capped_at_a_day(
        int attempt,
        int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), OutboxMessage.RetryDelayFor(attempt));
    }

    [Fact]
    public void The_retry_delay_never_shrinks_and_never_exceeds_the_cap()
    {
        Assert.Equal(TimeSpan.FromHours(24), OutboxMessage.MaxRetryDelay);

        var previous = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var delay = OutboxMessage.RetryDelayFor(attempt);

            Assert.True(
                delay >= previous,
                $"Attempt {attempt} backs off to {delay}, less than attempt {attempt - 1}'s {previous}. A schedule "
                + "that shrinks hammers a server that is already asking us to slow down.");
            Assert.True(delay <= OutboxMessage.MaxRetryDelay, $"Attempt {attempt} exceeded the 24 h cap: {delay}.");

            previous = delay;
        }
    }

    [Fact]
    public void A_transient_failure_schedules_the_slot_matching_the_attempt_number()
    {
        var message = OutboxMessage.Create(Account, Mid, T0);

        message.BeginSending(T0);
        var first = message.MarkFailed(SmtpResult.FromResponse(451, "451 greylisted"), T0);

        message.Retry(T0);
        message.BeginSending(T0);
        var second = message.MarkFailed(SmtpResult.FromResponse(451, "451 greylisted"), T0);

        Assert.Equal(TimeSpan.FromMinutes(1), first.RetryAfter);
        Assert.Equal(TimeSpan.FromMinutes(5), second.RetryAfter);
        Assert.True(first.WillRetry);
        Assert.True(second.WillRetry);
        Assert.False(first.Permanent);
        Assert.False(first.BudgetExhausted);
    }

    [Fact]
    public void A_five_hundred_reply_is_permanent_and_stops_the_schedule()
    {
        var message = OutboxMessage.Create(Account, Mid, T0);
        message.BeginSending(T0);

        var decision = message.MarkFailed(SmtpResult.FromResponse(550, "550 5.1.1 no such user"), T0);

        Assert.True(decision.Permanent);
        Assert.False(decision.WillRetry);
        Assert.Null(decision.RetryAfter);
        Assert.Null(decision.NextAttemptUtc);
        Assert.Equal("5.1.1", message.EnhancedStatusCode);
    }

    [Fact]
    public void A_network_failure_with_no_reply_at_all_is_transient()
    {
        var message = OutboxMessage.Create(Account, Mid, T0);
        message.BeginSending(T0);

        var decision = message.MarkFailed(SmtpResult.NetworkFailure("connection reset"), T0);

        Assert.False(
            decision.Permanent,
            "§14.5 case 30: a dropped socket or a TLS failure says nothing about the recipient. Treating it as "
            + "permanent turns a captive portal into a bounced message.");
        Assert.True(decision.WillRetry);
    }

    [Fact]
    public void Exhausting_the_retry_budget_ends_the_row_without_calling_it_permanent()
    {
        var message = OutboxMessage.Create(Account, Mid, T0, maxAttempts: 1);
        message.BeginSending(T0);

        var decision = message.MarkFailed(SmtpResult.FromResponse(451, "451 later"), T0);

        Assert.False(decision.Permanent);
        Assert.True(decision.BudgetExhausted);
        Assert.False(decision.WillRetry);
        Assert.True(message.PermanentlyFailed);
        Assert.True(message.IsTerminal);
    }

    [Fact]
    public void A_failed_message_becomes_due_only_once_its_backoff_has_elapsed()
    {
        var message = OutboxMessage.Create(Account, Mid, T0);
        message.BeginSending(T0);
        message.MarkFailed(SmtpResult.FromResponse(451, "451 later"), T0);

        Assert.False(message.IsDue(T0));
        Assert.False(message.IsDue(T0 + TimeSpan.FromSeconds(59)));
        Assert.True(message.IsDue(T0 + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_permanently_failed_message_is_never_due()
    {
        var message = PermanentlyFailed();

        Assert.False(message.IsDue(T0 + TimeSpan.FromDays(365)));
    }

    [Fact]
    public void Rehydration_restores_a_row_without_replaying_transitions()
    {
        var message = OutboxMessage.Rehydrate(
            id: 9,
            accountId: Account,
            messageId: Mid,
            state: OutboxState.Failed,
            attempts: 3,
            createdUtc: T0,
            nextAttemptUtc: T0 + TimeSpan.FromMinutes(15),
            lastAttemptUtc: T0,
            smtpResponse: "451 4.7.1 greylisted",
            enhancedStatusCode: "4.7.1",
            permanentlyFailed: false,
            maxAttempts: 12);

        Assert.Equal(9L, message.Id);
        Assert.Equal(3, message.Attempts);
        Assert.True(message.CanRetry);
        Assert.True(message.IsDue(T0 + TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Rehydration_refuses_a_row_that_lost_its_message_id()
    {
        Assert.Throws<ArgumentException>(() =>
            OutboxMessage.Rehydrate(1, Account, default, OutboxState.Queued, 0, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutboxMessage.Rehydrate(1, Account, Mid, OutboxState.Queued, -1, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutboxMessage.Rehydrate(1, Account, Mid, OutboxState.Queued, 0, T0, maxAttempts: 0));
    }

    [Fact]
    public void The_outbox_state_names_round_trip_through_the_wire()
    {
        foreach (var state in Enum.GetValues<OutboxState>())
            Assert.Equal(state, OutboxStateExtensions.FromWireValue(state.ToWireValue()));

        Assert.Throws<ArgumentOutOfRangeException>(() => OutboxStateExtensions.FromWireValue("deleted"));
    }

    private static OutboxMessage Queued() => OutboxMessage.Create(Account, Mid, T0);

    private static OutboxMessage Sending()
    {
        var message = Queued();
        message.BeginSending(T0);
        return message;
    }

    private static OutboxMessage Sent()
    {
        var message = Sending();
        message.MarkSent(SmtpResult.Accepted("250 2.0.0 OK"));
        return message;
    }

    private static OutboxMessage Failed()
    {
        var message = Sending();
        message.MarkFailed(SmtpResult.FromResponse(451, "451 4.7.1 greylisted"), T0);
        return message;
    }

    private static OutboxMessage PermanentlyFailed()
    {
        var message = Sending();
        message.MarkFailed(SmtpResult.FromResponse(550, "550 5.1.1 no such user"), T0);
        return message;
    }

    private static OutboxMessage ExhaustedBudget() =>
        OutboxMessage.Rehydrate(
            id: 1,
            accountId: Account,
            messageId: Mid,
            state: OutboxState.Failed,
            attempts: 5,
            createdUtc: T0,
            maxAttempts: 5);
}
