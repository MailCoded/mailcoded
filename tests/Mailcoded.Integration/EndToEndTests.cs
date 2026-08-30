using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>SPEC §8: add account → sync → search → tag → flag on the server → send → APPEND to Sent.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.EndToEnd)]
public sealed class EndToEndTests : IClassFixture<MailStackFixture>
{
    private const int SeedCount = 12;
    private const string Needle = "zephyrine";

    private readonly MailStackFixture _stack;

    public EndToEndTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task Add_account_sync_search_tag_and_send_round_trip_through_the_server()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("e2e");
        var secrets = new InMemorySecretStore();
        await using var harness = MailHarness.Open(workspace, secrets, _stack);

        await _stack.AppendAsync(
            FolderPath.Inbox,
            SeedMail.Corpus(_stack.Credentials.Mailbox, SeedCount, Needle),
            ct);

        var accountId = await harness.AddAccountAsync(ct);
        await using var provider = await harness.ConnectProviderAsync(accountId, ct);

        var report = await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
        Assert.Equal(SeedCount, report.Added);

        var inbox = harness.RequireFolder(accountId, FolderPath.Inbox, ct);
        Assert.Equal(SeedCount, inbox.TotalCount);

        var results = harness.Search.Search(new SearchRequest { Query = Needle, AccountId = accountId }, ct);
        Assert.Single(results.Hits);

        var hit = results.Hits[0];
        var envelope = harness.Store.GetEnvelope(hit.Id, ct)
            ?? throw new InvalidOperationException("The search hit has no envelope row.");
        var uid = envelope.Uid ?? throw new InvalidOperationException("The synced envelope carries no server UID.");

        var tagged = await harness.Messages.SetTagsAsync(
            provider,
            hit.Id,
            new TagDelta { Add = [Tag.Flagged] },
            CallerContext.Rpc,
            ct);

        Assert.True(tagged.PushedToServer, "The tag was never projected onto a server flag.");
        Assert.Contains(Tag.Flagged, tagged.Tags);

        var remote = FindByUid(await _stack.ReadServerFolderAsync(FolderPath.Inbox, ct), uid);
        Assert.True(remote.Flags.HasFlag(MessageFlags.Flagged), "The \\Flagged flag never reached the IMAP server.");

        var subject = "mailcoded outbound " + Guid.NewGuid().ToString("N");
        var preview = await harness.Send.PreviewAsync(
            CallerContext.Rpc,
            accountId,
            new DraftRequest
            {
                To = [EmailAddress.Parse("downstream@sink.test")],
                Subject = subject,
                BodyText = "Sent through the smtp4dev sink by the mailcoded integration suite.",
            },
            ct);

        await using var sender = await harness.ConnectSenderAsync(accountId, ct);
        var sent = await harness.Send.SendAsync(
            CallerContext.Rpc,
            sender,
            provider,
            preview.OutboxId,
            preview.ConfirmToken,
            null,
            ct);

        Assert.Equal(OutboxState.Sent, sent.State);
        Assert.True(sent.AppendedToSent, "The sent copy was not APPENDed to the Sent folder.");

        var delivered = await _stack.Smtp.WaitForSubjectAsync(subject, 1, TimeSpan.FromSeconds(30), ct);
        Assert.Equal(1, delivered);

        var sentFolder = await _stack.ReadServerFolderAsync("Sent", ct);
        Assert.Contains(sentFolder, e => string.Equals(e.Subject, subject, StringComparison.Ordinal));
    }

    private static RemoteEnvelope FindByUid(IReadOnlyList<RemoteEnvelope> envelopes, Uid uid)
    {
        foreach (var envelope in envelopes)
        {
            if (envelope.Uid == uid) return envelope;
        }

        throw new InvalidOperationException($"UID {uid} is no longer on the server.");
    }
}
