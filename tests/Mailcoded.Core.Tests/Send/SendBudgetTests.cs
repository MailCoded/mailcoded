using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

/// <summary>`mailcoded send-draft` is one-shot: it builds a fresh AgentPolicy and exits. An hourly
/// budget counted in process memory is therefore no budget at all, and two concurrent sends must
/// not both pass a window with one slot left.</summary>
public sealed class SendBudgetTests
{
    private const string Recipient = "ally@example.test";

    [Fact]
    public async Task TheBudgetIsSpentAcrossProcessesNotJustWithinOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        for (var i = 0; i < AgentPolicyOptions.DefaultMaxSendsPerHour; i++)
        {
            var invocation = new AgentProcess(temp);
            var draft = await invocation.PreviewAsync(account, "Budget " + i, ct);
            await invocation.SendAsync(draft, ct);

            Assert.Single(invocation.Sender.Sends);
            temp.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var sixth = new AgentProcess(temp);
        Assert.Equal(0, sixth.Policy.RemainingSendsInWindow());

        var spent = await sixth.PreviewAsync(account, "Budget 5", ct);
        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(() => sixth.SendAsync(spent, ct));

        Assert.Equal(PolicyDenialReason.RateLimited, denial.Reason);
        Assert.Empty(sixth.Sender.Sends);
        Assert.True(sixth.Tokens.IsOutstanding(spent.OutboxId), "a rate-limited attempt must not burn the token");
    }

    [Fact]
    public async Task TwoConcurrentSendsWithOneSlotLeftProduceExactlyOneSend()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        for (var i = 0; i < AgentPolicyOptions.DefaultMaxSendsPerHour - 1; i++)
        {
            var invocation = new AgentProcess(temp);
            var used = await invocation.PreviewAsync(account, "Used " + i, ct);
            await invocation.SendAsync(used, ct);
        }

        var left = new AgentProcess(temp);
        var right = new AgentProcess(temp);
        var first = await left.PreviewAsync(account, "Racing left", ct);
        var second = await right.PreviewAsync(account, "Racing right", ct);

        var outcomes = await Task.WhenAll(
            Task.Run(() => Outcome(left, first, ct), ct),
            Task.Run(() => Outcome(right, second, ct), ct));

        var sent = 0;
        var limited = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome is null) sent++;
            else if (outcome.Reason == PolicyDenialReason.RateLimited) limited++;
            else Assert.Fail($"unexpected denial: {outcome.Reason}");
        }

        Assert.Equal(1, sent);
        Assert.Equal(1, limited);
        Assert.Equal(1, left.Sender.Sends.Count + right.Sender.Sends.Count);
        Assert.Equal(0, left.Policy.RemainingSendsInWindow());
        Assert.Equal(AgentPolicyOptions.DefaultMaxSendsPerHour, temp.Store.ReadSendBudget(0, ct).Used);
    }

    [Fact]
    public async Task AWallClockStepBackwardsDoesNotEraseTheBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        for (var i = 0; i < AgentPolicyOptions.DefaultMaxSendsPerHour; i++)
        {
            var invocation = new AgentProcess(temp);
            var used = await invocation.PreviewAsync(account, "Used " + i, ct);
            await invocation.SendAsync(used, ct);
        }

        temp.Clock.UtcNow -= TimeSpan.FromHours(3);

        var next = new AgentProcess(temp);
        Assert.Equal(0, next.Policy.RemainingSendsInWindow());

        var draft = await next.PreviewAsync(account, "After the step", ct);
        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(() => next.SendAsync(draft, ct));

        Assert.Equal(PolicyDenialReason.RateLimited, denial.Reason);
        Assert.Empty(next.Sender.Sends);
    }

    [Fact]
    public async Task SlotsOlderThanTheWindowArePrunedRatherThanAccumulated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        for (var i = 0; i < AgentPolicyOptions.DefaultMaxSendsPerHour; i++)
        {
            var invocation = new AgentProcess(temp);
            var used = await invocation.PreviewAsync(account, "Used " + i, ct);
            await invocation.SendAsync(used, ct);
        }

        Assert.Equal(AgentPolicyOptions.DefaultMaxSendsPerHour, temp.Store.ReadSendBudget(0, ct).Used);

        temp.Clock.Advance(TimeSpan.FromMinutes(61));

        var later = new AgentProcess(temp);
        var draft = await later.PreviewAsync(account, "A new window", ct);
        await later.SendAsync(draft, ct);

        Assert.Single(later.Sender.Sends);
        Assert.Equal(1, temp.Store.ReadSendBudget(0, ct).Used);
    }

    [Fact]
    public async Task ARejectedConfirmTokenReleasesTheReservedSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var invocation = new AgentProcess(temp);
        var draft = await invocation.PreviewAsync(account, "Wrong token", ct);

        await Assert.ThrowsAsync<ConfirmRequiredException>(
            () => invocation.SendAsync(draft, ct, "not-the-token-that-was-issued"));

        Assert.Empty(invocation.Sender.Sends);
        Assert.Equal(0, temp.Store.ReadSendBudget(0, ct).Used);
        Assert.Equal(AgentPolicyOptions.DefaultMaxSendsPerHour, invocation.Policy.RemainingSendsInWindow());
    }

    [Fact]
    public async Task ANonAgentCallerSpendsNoBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var account = await StoreSeed.AccountAsync(temp.Store, ct);

        var invocation = new AgentProcess(temp);

        for (var i = 0; i < AgentPolicyOptions.DefaultMaxSendsPerHour + 3; i++)
        {
            var draft = await invocation.PreviewAsync(account, "Rpc " + i, ct, CallerContext.Rpc);
            await invocation.SendAsync(draft, ct, caller: CallerContext.Rpc);
        }

        Assert.Equal(AgentPolicyOptions.DefaultMaxSendsPerHour + 3, invocation.Sender.Sends.Count);
        Assert.Equal(0, temp.Store.ReadSendBudget(0, ct).Used);
    }

    private static async Task<PolicyDeniedException?> Outcome(
        AgentProcess process,
        SendPreview draft,
        CancellationToken ct)
    {
        try
        {
            await process.SendAsync(draft, ct);
            return null;
        }
        catch (PolicyDeniedException denied)
        {
            return denied;
        }
    }

    /// <summary>One `mailcoded` invocation: fresh policy and token store, the same database.</summary>
    private sealed class AgentProcess
    {
        public AgentProcess(TempStore temp)
        {
            Policy = new AgentPolicy(
                new AgentPolicyOptions { SendEnabled = true, ApprovedRecipients = [Recipient] },
                temp.Clock,
                temp.Store);
            Tokens = new ConfirmTokenStore(temp.Clock, store: temp.Store);
            Send = new SendService(
                temp.Store,
                MessageParser.Default,
                temp.Clock,
                new AuditLog(temp.Store, temp.Clock),
                Policy,
                Tokens);
        }

        public AgentPolicy Policy { get; }

        public ConfirmTokenStore Tokens { get; }

        public SendService Send { get; }

        public CapturingSender Sender { get; } = new();

        public Task<SendPreview> PreviewAsync(
            AccountId account,
            string subject,
            CancellationToken ct,
            CallerContext? caller = null) =>
            Send.PreviewAsync(
                caller ?? Caller,
                account,
                new DraftRequest
                {
                    To = [EmailAddress.Parse(Recipient)],
                    Subject = subject,
                    BodyText = "The quarterly numbers are attached.",
                },
                ct);

        public Task<SendResult> SendAsync(
            SendPreview draft,
            CancellationToken ct,
            string? token = null,
            CallerContext? caller = null) =>
            Send.SendAsync(
                caller ?? Caller,
                Sender,
                null,
                draft.OutboxId,
                token ?? draft.ConfirmToken,
                new SendOptions { AppendToSent = false },
                ct);

        private static CallerContext Caller => CallerContext.For(CallerKind.Cli, "xunit");
    }
}
