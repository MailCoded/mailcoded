using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>Edge case 1: the folder is invalidated and re-enumerated, and blobs re-link by sha256.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.UidValidity)]
public sealed class UidValidityTests : IClassFixture<MailStackFixture>
{
    private const int SeedCount = 8;

    private readonly MailStackFixture _stack;

    public UidValidityTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task A_uidvalidity_change_re_enumerates_the_folder_without_duplicating_blobs()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("uidvalidity");
        var secrets = new InMemorySecretStore();
        await using var harness = MailHarness.Open(workspace, secrets, _stack);

        await _stack.AppendAsync(
            FolderPath.Inbox,
            SeedMail.Corpus(_stack.Credentials.Mailbox, SeedCount, "kestrel"),
            ct);

        var accountId = await harness.AddAccountAsync(ct);
        FolderId inboxId;
        UidValidity before;

        await using (var provider = await harness.ConnectProviderAsync(accountId, ct))
        {
            var report = await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
            Assert.Equal(SeedCount, report.Added);

            var inbox = harness.RequireFolder(accountId, FolderPath.Inbox, ct);
            inboxId = inbox.Id;
            before = inbox.UidValidity;
            Assert.False(before.IsUnknown);

            await FetchAllBodiesAsync(harness, provider, inboxId, ct);
        }

        var blobsBefore = BlobCount(harness);
        Assert.Equal(SeedCount, (int)blobsBefore);

        await _stack.Imap.ForceUidValidityChangeAsync(ct);

        var serverValidity = await _stack.ReadUidValidityAsync(FolderPath.Inbox, ct);
        Assert.NotEqual(before, serverValidity);

        await using (var provider = await harness.ConnectProviderAsync(accountId, ct))
        {
            await harness.Sync.SyncFolderAsync(provider, inboxId, null, ct);

            var recovered = harness.RequireFolder(accountId, FolderPath.Inbox, ct);
            Assert.Equal(serverValidity, recovered.UidValidity);
            Assert.Equal(SeedCount, recovered.TotalCount);

            await FetchAllBodiesAsync(harness, provider, inboxId, ct);
        }

        Assert.Equal(SeedCount, (int)harness.Store.GetStats(ct).TableCounts["messages"]);
        Assert.Equal(blobsBefore, BlobCount(harness));
    }

    private static long BlobCount(MailHarness harness) => harness.Store.GetStats().TableCounts["blobs"];

    private static async Task FetchAllBodiesAsync(
        MailHarness harness,
        IMailProvider provider,
        FolderId folderId,
        CancellationToken ct)
    {
        var page = harness.Store.ListEnvelopes(folderId, null, 500, ct);
        foreach (var item in page.Items)
        {
            await harness.Messages
                .GetAsync(provider, item.Id, MessageBodyFormat.Text, fetchIfMissing: true, CallerContext.Rpc, ct)
                .ConfigureAwait(false);
        }
    }
}
