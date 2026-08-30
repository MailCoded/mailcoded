using System.Reflection;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Surface;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class PlaintextOnlyTests
{
    [Fact]
    public async Task AgentRead_ReturnsPlaintextForAMessageThatHasAnHtmlPart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadHarness.CreateAsync(ct);

        var view = await harness.Messages.GetAsync(
            null,
            harness.MessageId,
            MessageBodyFormat.Text,
            fetchIfMissing: false,
            CallerContext.For(CallerKind.Cli, "xunit"),
            ct);

        Assert.NotNull(view.BodyText);
        Assert.Contains("42 dollars", view.BodyText, StringComparison.Ordinal);
        Assert.Null(view.BodyHtml);
        Assert.DoesNotContain("<img", view.BodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example.invalid", view.BodyText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CallerKind.Cli, MessageBodyFormat.Html)]
    [InlineData(CallerKind.Cli, MessageBodyFormat.Raw)]
    [InlineData(CallerKind.Mcp, MessageBodyFormat.Html)]
    [InlineData(CallerKind.Mcp, MessageBodyFormat.Raw)]
    public async Task AgentRead_CannotAskForHtmlOrRawBytes(CallerKind caller, MessageBodyFormat format)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadHarness.CreateAsync(ct);

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(
            () => harness.Messages.GetAsync(
                null,
                harness.MessageId,
                format,
                fetchIfMissing: false,
                CallerContext.For(caller, "xunit"),
                ct));

        Assert.Equal(PolicyDenialReason.HtmlBodyDenied, denial.Reason);
    }

    [Fact]
    public async Task InteractiveClient_StillReceivesTheHtmlPart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadHarness.CreateAsync(ct);

        var view = await harness.Messages.GetAsync(
            null,
            harness.MessageId,
            MessageBodyFormat.Html,
            fetchIfMissing: false,
            CallerContext.Rpc,
            ct);

        Assert.NotNull(view.BodyHtml);
        Assert.Contains("<img", view.BodyHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Policy_AllowsHtmlOnlyForNonAgentCallers()
    {
        var policy = new AgentPolicy(new AgentPolicyOptions(), new ManualClock());

        Assert.True(policy.AllowsHtmlBody(CallerKind.Rpc));
        Assert.True(policy.AllowsHtmlBody(CallerKind.Internal));
        Assert.False(policy.AllowsHtmlBody(CallerKind.Cli));
        Assert.False(policy.AllowsHtmlBody(CallerKind.Mcp));
    }

    [Fact]
    public async Task AgentSurface_CannotMoveMail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await ReadHarness.CreateAsync(ct);

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(
            () => harness.Messages.MoveAsync(
                null,
                harness.MessageId,
                harness.FolderId,
                CallerContext.For(CallerKind.Mcp, "xunit"),
                ct));

        Assert.Equal(PolicyDenialReason.OperationNotAvailable, denial.Reason);
    }

    [Fact]
    public void McpReadResult_HasNoHtmlBearingField()
    {
        var properties = typeof(Mailcoded.Mcp.ReadResultDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(properties);
        Assert.DoesNotContain(properties, static p => p.Name.Contains("Html", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, static p => p.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(properties, static p => p.Name == "BodyText");
    }

    private sealed class ReadHarness : IAsyncDisposable
    {
        private readonly SurfaceWorkspace _workspace;

        private ReadHarness(
            SurfaceWorkspace workspace,
            SqliteStore store,
            MessageService messages,
            FolderId folderId,
            LocalMessageId messageId)
        {
            _workspace = workspace;
            Store = store;
            Messages = messages;
            FolderId = folderId;
            MessageId = messageId;
        }

        public SqliteStore Store { get; }
        public MessageService Messages { get; }
        public FolderId FolderId { get; }
        public LocalMessageId MessageId { get; }

        public static async Task<ReadHarness> CreateAsync(CancellationToken ct)
        {
            var workspace = new SurfaceWorkspace("plaintext-only");

            try
            {
                var clock = new ManualClock();
                var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = workspace.DatabasePath }, clock);
                var audit = new AuditLog(store, clock);
                var policy = new AgentPolicy(new AgentPolicyOptions(), clock);
                var messages = new MessageService(store, MessageParser.Default, audit, policy);

                var accountId = await StoreSeeder.AddAccountAsync(store, ct).ConfigureAwait(false);
                var folderId = await StoreSeeder.AddInboxAsync(store, accountId, ct).ConfigureAwait(false);

                var raw = StoreSeeder.HtmlAndTextMessage();
                var parsed = StoreSeeder.Parse(raw);
                var blob = await store.StoreBlobAsync(raw, ct).ConfigureAwait(false);

                await store.IngestEnvelopesAsync(
                    folderId,
                    [
                        new RemoteEnvelope
                        {
                            Uid = new Uid(1),
                            Flags = MessageFlags.Unread,
                            MessageIdHeader = parsed.MessageId?.Value,
                            Subject = parsed.Subject,
                            From = parsed.From,
                            To = parsed.To,
                            DateUtc = parsed.DateUtc,
                            Size = raw.LongLength,
                        },
                    ],
                    Mailcoded.Core.Domain.Threading.ReferencesThreader.Instance,
                    ct).ConfigureAwait(false);

                var messageId = store.FindMessage(folderId, new Uid(1), ct)
                    ?? throw new InvalidOperationException("The seeded HTML message was not stored.");

                await store
                    .SetBodyTextAsync(messageId, parsed.BodyText, blob.Id, parsed.HasAttachments, ct)
                    .ConfigureAwait(false);

                return new ReadHarness(workspace, store, messages, folderId, messageId);
            }
            catch (Exception)
            {
                workspace.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            _workspace.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
