using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>A bool has no "unknown": a search hit that omits a field still asserts a value.</summary>
public sealed class EnvelopeFidelityTests
{
    [Fact]
    public async Task A_search_hit_reports_the_attachment_the_message_actually_has()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seeded = await Seed(ct);

        var hit = Assert.Single(seeded.Store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse(string.Empty).Query, Limit = 10 }, ct).Hits);

        Assert.True(hit.HasAttachments, "search reported no attachment for a message that has one.");
        Assert.True(hit.BodyFetched, "search reported an unfetched body for a message whose body is stored.");
        Assert.True(hit.Size > 0, "search reported size 0 for a message with bytes.");
    }

    [Fact]
    public async Task A_plaintext_read_still_lists_what_is_attached()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seeded = await Seed(ct);

        var view = await seeded.Messages.GetAsync(
            null,
            seeded.MessageId,
            MessageBodyFormat.Text,
            fetchIfMissing: false,
            CallerContext.For(CallerKind.Cli, "xunit"),
            ct);

        var attachment = Assert.Single(view.Attachments);

        Assert.Equal("invoice-4471.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.MimeType);
        Assert.Null(view.BodyHtml);
        Assert.Null(view.Raw);
    }

    private static async Task<Seeded> Seed(CancellationToken ct)
    {
        var workspace = TempWorkspace.Create("envelope-fidelity");

        try
        {
            var clock = new TestClock();
            var store = new SqliteStore(new SqliteStoreOptions { DatabasePath = workspace.DatabasePath }, clock);
            var audit = new AuditLog(store, clock);
            var policy = new AgentPolicy(new AgentPolicyOptions(), clock);
            var messages = new MessageService(store, MessageParser.Default, audit, policy);

            var accountId = await StoreSeed.AccountAsync(store, ct).ConfigureAwait(false);
            var folderId = await StoreSeed
                .FolderAsync(store, accountId, "INBOX", FolderRole.Inbox, ct)
                .ConfigureAwait(false);

            var raw = Fixtures.Read("014-attachment-pdf.eml");
            var parsed = MessageParser.Default.Parse(new MemoryStream(raw), clock.UtcNow, ct);
            var blob = await store.StoreBlobAsync(raw, ct).ConfigureAwait(false);

            await store.IngestEnvelopesAsync(
                folderId,
                [
                    StoreSeed.Envelope(
                        1,
                        subject: parsed.Subject,
                        messageId: parsed.MessageId?.Value,
                        date: parsed.DateUtc,
                        hasAttachments: parsed.HasAttachments,
                        size: raw.LongLength),
                ],
                ReferencesThreader.Instance,
                ct).ConfigureAwait(false);

            var messageId = store.FindMessage(folderId, new Uid(1), ct)
                ?? throw new InvalidOperationException("The seeded message was not stored.");

            await store.SetBodyTextAsync(messageId, parsed.BodyText, blob.Id, parsed.HasAttachments, ct)
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
