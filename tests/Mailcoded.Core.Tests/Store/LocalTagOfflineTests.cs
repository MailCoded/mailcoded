using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>CLAUDE invariant 9 reads both ways: the server wins on flags, local wins on tags.</summary>
public sealed class LocalTagOfflineTests
{
    [Theory]
    [InlineData("unread", true)]
    [InlineData("flagged", true)]
    [InlineData("replied", true)]
    [InlineData("project", false)]
    [InlineData("waiting-on-finance", false)]
    public async Task Only_a_system_flag_obliges_the_call_to_reach_the_server(string tag, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seeded = await Seed(ct);

        var delta = new TagDelta { Add = [Tag.Parse(tag)] };

        Assert.Equal(expected, seeded.Messages.RequiresServerPush(seeded.MessageId, delta, ct));
    }

    [Fact]
    public async Task A_custom_tag_lands_locally_with_no_provider_at_all()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seeded = await Seed(ct);

        var result = await seeded.Messages.SetTagsAsync(
            null,
            seeded.MessageId,
            new TagDelta { Add = [Tag.Parse("project")] },
            CallerContext.Rpc,
            ct);

        Assert.Contains(result.Tags, t => t.Value == "project");
    }

    private static async Task<Seeded> Seed(CancellationToken ct)
    {
        var workspace = TempWorkspace.Create("local-tag-offline");

        try
        {
            var clock = new TestClock();
            var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = workspace.DatabasePath }, clock);
            var messages = new MessageService(
                store,
                MessageParser.Default,
                new AuditLog(store, clock),
                new AgentPolicy(new AgentPolicyOptions(), clock));

            var accountId = await StoreSeed.AccountAsync(store, ct).ConfigureAwait(false);
            var folderId = await StoreSeed
                .FolderAsync(store, accountId, "INBOX", FolderRole.Inbox, ct)
                .ConfigureAwait(false);

            var messageId = await StoreSeed
                .MessageAsync(store, folderId, StoreSeed.Envelope(1), ct, bodyText: "hello")
                .ConfigureAwait(false);

            return new Seeded(workspace, store, messages, messageId);
        }
        catch (Exception)
        {
            workspace.Dispose();
            throw;
        }
    }

    private sealed record Seeded(
        TempWorkspace Workspace,
        SqliteStore Store,
        MessageService Messages,
        LocalMessageId MessageId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            Workspace.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
