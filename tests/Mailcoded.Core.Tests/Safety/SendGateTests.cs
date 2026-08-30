using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Daemon;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class SendGateTests
{
    private const string Recipient = "ally@example.test";
    private const string BodyMarker = "TOPSECRET-BODY-MARKER";
    private const string FakeCredential = "hunter2-not-a-real-password";

    private static readonly AgentPolicyOptions Unlocked = new()
    {
        SendEnabled = true,
        ApprovedRecipients = [Recipient],
    };

    [Fact]
    public async Task Send_WithoutAToken_IsRejectedWith1003()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SendHarness.CreateAsync(Unlocked, ct);
        var draft = await harness.DraftAsync(ct);

        var denial = await Assert.ThrowsAsync<ConfirmRequiredException>(
            () => harness.SendAsync(draft.OutboxId, null, ct));

        Assert.Equal((int)RpcErrorCode.ConfirmRequired, RpcErrorMapper.Map(denial, harness.Log).Code);
        Assert.Empty(harness.Sender.Sends);
    }

    [Fact]
    public async Task Send_WithAWrongToken_IsRejectedWith1003()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SendHarness.CreateAsync(Unlocked, ct);
        var draft = await harness.DraftAsync(ct);

        await Assert.ThrowsAsync<ConfirmRequiredException>(
            () => harness.SendAsync(draft.OutboxId, "not-the-token-that-was-issued", ct));

        Assert.Empty(harness.Sender.Sends);
        Assert.True(harness.Tokens.IsOutstanding(draft.OutboxId), "a rejected attempt must not burn the token");
    }

    [Fact]
    public async Task Send_WithATokenMintedForAnotherDraft_IsRejectedWith1003()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SendHarness.CreateAsync(Unlocked, ct);

        var first = await harness.DraftAsync(ct);
        var second = await harness.DraftAsync(ct, subject: "A different draft");

        Assert.NotEqual(first.OutboxId, second.OutboxId);

        await Assert.ThrowsAsync<ConfirmRequiredException>(
            () => harness.SendAsync(first.OutboxId, second.ConfirmToken, ct));

        Assert.Empty(harness.Sender.Sends);
    }

    [Fact]
    public async Task Send_TokenIsSingleUse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SendHarness.CreateAsync(Unlocked, ct);
        var draft = await harness.DraftAsync(ct);

        var result = await harness.SendAsync(draft.OutboxId, draft.ConfirmToken, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Single(harness.Sender.Sends);
        Assert.False(harness.Tokens.IsOutstanding(draft.OutboxId));

        await Assert.ThrowsAsync<ConfirmRequiredException>(
            () => harness.SendAsync(draft.OutboxId, draft.ConfirmToken, ct));

        Assert.Single(harness.Sender.Sends);
    }

    [Fact]
    public async Task Send_FromAnAgentIsDeniedWithoutTheSendEnvironmentVariable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SendHarness.CreateAsync(
            new AgentPolicyOptions { SendEnabled = false, ApprovedRecipients = [Recipient] },
            ct);

        var draft = await harness.DraftAsync(ct);

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(
            () => harness.SendAsync(draft.OutboxId, draft.ConfirmToken, ct));

        Assert.Equal(PolicyDenialReason.SendDisabled, denial.Reason);
        Assert.Empty(harness.Sender.Sends);
        Assert.True(harness.Tokens.IsOutstanding(draft.OutboxId), "a closed gate must run before the token is spent");
        Assert.Equal((int)RpcErrorCode.Forbidden, RpcErrorMapper.Map(denial, harness.Log).Code);
    }

    [Fact]
    public void SendPosture_IsReadFromTheEnvironmentAndDefaultsToDeny()
    {
        using (EnvironmentScope.Set((AgentPolicyOptions.SendEnvVar, null)))
        {
            Assert.False(AgentPolicyOptions.FromEnvironment().SendEnabled);
        }

        using (EnvironmentScope.Set((AgentPolicyOptions.SendEnvVar, "1")))
        {
            Assert.True(AgentPolicyOptions.FromEnvironment().SendEnabled);
        }

        using (EnvironmentScope.Set((AgentPolicyOptions.SendEnvVar, "0")))
        {
            Assert.False(AgentPolicyOptions.FromEnvironment().SendEnabled);
        }
    }

    [Fact]
    public void Posture_TreatsRpcAsANonAgentSurfaceAndCliAndMcpAsAgents()
    {
        var policy = new AgentPolicy(new AgentPolicyOptions(), new ManualClock());

        Assert.True(policy.IsEnabled(AgentCapability.Send, CallerKind.Rpc));
        Assert.False(policy.IsEnabled(AgentCapability.Send, CallerKind.Cli));
        Assert.False(policy.IsEnabled(AgentCapability.Send, CallerKind.Mcp));

        Assert.True(policy.IsEnabled(AgentCapability.Read, CallerKind.Cli));
        Assert.True(policy.IsEnabled(AgentCapability.Search, CallerKind.Mcp));
        Assert.True(policy.IsEnabled(AgentCapability.Tag, CallerKind.Cli));
        Assert.True(policy.IsEnabled(AgentCapability.Draft, CallerKind.Mcp));
    }

    [Fact]
    public async Task Send_ToAnUnapprovedRecipientIsDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SendHarness.CreateAsync(Unlocked, ct);
        var draft = await harness.DraftAsync(ct, recipient: "stranger@elsewhere.test");

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(
            () => harness.SendAsync(draft.OutboxId, draft.ConfirmToken, ct));

        Assert.Equal(PolicyDenialReason.RecipientNotApproved, denial.Reason);
        Assert.Empty(harness.Sender.Sends);
    }

    [Fact]
    public async Task Send_ToADomainPatternRecipientIsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SendHarness.CreateAsync(
            new AgentPolicyOptions { SendEnabled = true, ApprovedRecipients = ["@example.test"] },
            ct);

        var draft = await harness.DraftAsync(ct, recipient: "anyone@example.test");
        var result = await harness.SendAsync(draft.OutboxId, draft.ConfirmToken, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Single(harness.Sender.Sends);
    }

    [Theory]
    [InlineData("ally@example.test", true)]
    [InlineData("ALLY@EXAMPLE.TEST", true)]
    [InlineData("other@example.test", false)]
    public void RecipientAllowlist_MatchesExactAddressesCaseInsensitively(string address, bool approved) =>
        Assert.Equal(approved, AgentPolicy.IsRecipientApproved(EmailAddress.Parse(address), ["ally@example.test"]));

    [Theory]
    [InlineData("@example.test", "anyone@example.test", true)]
    [InlineData("*@example.test", "anyone@example.test", true)]
    [InlineData("@example.test", "anyone@other.test", false)]
    [InlineData("@example.test", "anyone@sub.example.test", false)]
    public void RecipientAllowlist_SupportsDomainPatterns(string pattern, string address, bool approved) =>
        Assert.Equal(approved, AgentPolicy.IsRecipientApproved(EmailAddress.Parse(address), [pattern]));

    [Fact]
    public void RecipientAllowlist_ApprovesNothingWhenEmpty() =>
        Assert.False(AgentPolicy.IsRecipientApproved(EmailAddress.Parse(Recipient), []));

    [Fact]
    public async Task Send_TheSixthAttemptInOneHourIsRateLimitedOnMonotonicTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SendHarness.CreateAsync(Unlocked, ct);

        for (var i = 0; i < AgentPolicyOptions.DefaultMaxSendsPerHour; i++)
        {
            var allowed = await harness.DraftAsync(ct, subject: "Budget " + i);
            await harness.SendAsync(allowed.OutboxId, allowed.ConfirmToken, ct);
            harness.Clock.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(AgentPolicyOptions.DefaultMaxSendsPerHour, harness.Sender.Sends.Count);
        Assert.Equal(0, harness.Policy.RemainingSendsInWindow());

        var sixth = await harness.DraftAsync(ct, subject: "Budget 5");
        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(
            () => harness.SendAsync(sixth.OutboxId, sixth.ConfirmToken, ct));

        Assert.Equal(PolicyDenialReason.RateLimited, denial.Reason);
        Assert.Equal((int)RpcErrorCode.RateLimited, RpcErrorMapper.Map(denial, harness.Log).Code);

        // A wall-clock jump of two hours must not buy budget: the window is monotonic.
        harness.Clock.StepWallClock(TimeSpan.FromHours(2));
        await Assert.ThrowsAsync<PolicyDeniedException>(
            () => harness.SendAsync(sixth.OutboxId, sixth.ConfirmToken, ct));

        harness.Clock.AdvanceMonotonic(TimeSpan.FromHours(1));
        var seventh = await harness.DraftAsync(ct, subject: "Budget 6");
        var result = await harness.SendAsync(seventh.OutboxId, seventh.ConfirmToken, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Equal(AgentPolicyOptions.DefaultMaxSendsPerHour + 1, harness.Sender.Sends.Count);
    }

    [Fact]
    public async Task EverySendAttemptWritesAnAuditRowWithoutTheBodyOrACredential()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SendHarness.CreateAsync(Unlocked, ct);

        var denied = await harness.DraftAsync(ct);
        await Assert.ThrowsAsync<ConfirmRequiredException>(() => harness.SendAsync(denied.OutboxId, null, ct));

        var allowed = await harness.DraftAsync(ct, subject: "Second draft");
        await harness.SendAsync(allowed.OutboxId, allowed.ConfirmToken, ct);

        var log = harness.Store.ReadSyncLog(limit: 200, ct: ct).Items;
        var attempts = log.Where(static row => row.Event == AuditEvents.SendAttempt).ToList();

        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, row => Assert.Equal("cli", row.Interface));
        Assert.Contains(attempts, static row => row.Detail!.Contains("decision=denied", StringComparison.Ordinal));
        Assert.Contains(attempts, static row => row.Detail!.Contains("decision=allowed", StringComparison.Ordinal));
        Assert.Contains(attempts, static row => row.Detail!.Contains("token=consumed", StringComparison.Ordinal));

        foreach (var row in attempts)
        {
            Assert.NotNull(row.Detail);
            Assert.Contains("method=send_attempt", row.Detail, StringComparison.Ordinal);

            var digest = Field(row.Detail, "digest=");
            Assert.Equal(AuditText.DigestHexLength, digest.Length);
            Assert.True(digest.All(Uri.IsHexDigit), "the args digest was not hexadecimal");
        }

        foreach (var row in log)
        {
            Assert.DoesNotContain(BodyMarker, row.Detail ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeCredential, row.Detail ?? string.Empty, StringComparison.Ordinal);
        }
    }

    private static string Field(string detail, string key)
    {
        var at = detail.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return string.Empty;

        var start = at + key.Length;
        var end = detail.IndexOf(' ', start);
        return end < 0 ? detail[start..] : detail[start..end];
    }

    private readonly record struct SeededDraft(long OutboxId, string ConfirmToken);

    private sealed class SendHarness : IAsyncDisposable
    {
        private readonly SurfaceWorkspace _workspace;

        private SendHarness(
            SurfaceWorkspace workspace,
            SqliteStore store,
            ManualClock clock,
            AgentPolicy policy,
            ConfirmTokenStore tokens,
            SendService send,
            AccountId accountId)
        {
            _workspace = workspace;
            Store = store;
            Clock = clock;
            Policy = policy;
            Tokens = tokens;
            Send = send;
            AccountId = accountId;
        }

        public SqliteStore Store { get; }
        public ManualClock Clock { get; }
        public AgentPolicy Policy { get; }
        public ConfirmTokenStore Tokens { get; }
        public SendService Send { get; }
        public AccountId AccountId { get; }
        public RecordingMailSender Sender { get; } = new();
        public StderrLog Log { get; } = new(TextWriter.Null, DaemonLogLevel.Off, timestamps: false);

        public static CallerContext Caller => CallerContext.For(CallerKind.Cli, "xunit");

        public static async Task<SendHarness> CreateAsync(AgentPolicyOptions options, CancellationToken ct)
        {
            var workspace = new SurfaceWorkspace("send-gate");

            try
            {
                var clock = new ManualClock();
                var store = new SqliteStore(
                    new SqliteStoreOptions { DatabasePath = workspace.DatabasePath },
                    clock);

                var audit = new AuditLog(store, clock);
                var policy = new AgentPolicy(options, clock);
                var tokens = new ConfirmTokenStore(clock);
                var send = new SendService(store, MessageParser.Default, clock, audit, policy, tokens);
                var accountId = await StoreSeeder.AddAccountAsync(store, ct, withSmtp: true).ConfigureAwait(false);

                return new SendHarness(workspace, store, clock, policy, tokens, send, accountId);
            }
            catch (Exception)
            {
                workspace.Dispose();
                throw;
            }
        }

        public async Task<SeededDraft> DraftAsync(
            CancellationToken ct,
            string recipient = Recipient,
            string subject = "Invoice follow-up")
        {
            var preview = await Send.PreviewAsync(
                Caller,
                AccountId,
                new DraftRequest
                {
                    To = [EmailAddress.Parse(recipient)],
                    Subject = subject,
                    BodyText = $"{BodyMarker} the vault password is {FakeCredential}.",
                },
                ct).ConfigureAwait(false);

            return new SeededDraft(preview.OutboxId, preview.ConfirmToken);
        }

        public Task<Mailcoded.Core.Application.SendResult> SendAsync(long outboxId, string? token, CancellationToken ct) =>
            Send.SendAsync(Caller, Sender, null, outboxId, token, new SendOptions { AppendToSent = false }, ct);

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            _workspace.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
