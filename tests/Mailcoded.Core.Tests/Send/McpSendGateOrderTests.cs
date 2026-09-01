using System.Text;
using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

/// <summary>An agent behind a closed send gate must not be able to drive mail-server logins at a
/// cadence of its choosing: the gate runs before anything connects, as it does on the CLI.</summary>
public sealed class McpSendGateOrderTests
{
    [Fact]
    public async Task SendDraft_IsDeniedByTheGateBeforeAnythingAuthenticates()
    {
        var ct = TestContext.Current.CancellationToken;

        using var environment = EnvironmentScope.Set(
            (AgentPolicyOptions.SendEnvVar, null),
            (AgentPolicyOptions.ApprovedRecipientsEnvVar, null));

        using var workspace = TempWorkspace.Create("mcp-send-gate");
        await using var host = McpHost.Create(workspace.DatabasePath);

        var account = await StoreSeed.AccountAsync(host.Store, ct);
        var draftId = await SeedDraftAsync(host.Store, account, ct);

        var tools = new MailTools(host);
        var args = Args($"{{\"draft_id\":{draftId},\"confirm_token\":\"not-a-real-token\"}}");

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(() => tools.SendDraftAsync(args, ct));

        Assert.Equal(PolicyDenialReason.SendDisabled, denial.Reason);
        Assert.Equal(OutboxState.Queued, host.Store.GetOutbox(draftId, ct)!.State);
    }

    private static Task<long> SeedDraftAsync(SqliteStore store, AccountId account, CancellationToken ct) =>
        store.EnqueueOutboxAsync(
            new OutboxRecord
            {
                AccountId = account,
                MessageId = MessageId.Parse("mcp-gate-order@example.com"),
                State = OutboxState.Queued,
                Raw = Encoding.ASCII.GetBytes(
                    "From: tester@example.com\r\nTo: ally@example.test\r\nSubject: Gate\r\n\r\nbody"),
                CreatedUtc = StoreSeed.BaseDate,
                Envelope = new OutboxEnvelope
                {
                    From = EmailAddress.Parse("tester@example.com"),
                    To = [EmailAddress.Parse("ally@example.test")],
                },
            },
            ct);

    private static ToolArgs Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
            values[property.Name] = property.Value.Clone();

        return new ToolArgs(values);
    }
}
