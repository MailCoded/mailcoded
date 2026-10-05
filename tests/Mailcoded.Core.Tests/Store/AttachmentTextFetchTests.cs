using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

/// <summary>The lazy body fetch is where attachment text is first read, so it must index in the same pass.</summary>
public sealed class AttachmentTextFetchTests
{
    private static readonly CallerContext Caller = CallerContext.For(CallerKind.Cli, "xunit");

    [Fact]
    public async Task Fetching_a_body_makes_its_attachments_searchable_and_marks_the_message_read()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var messages = new MessageService(
            temp.Store, MessageParser.Default, new AuditLog(temp.Store, temp.Clock), new AgentPolicy(new AgentPolicyOptions(), temp.Clock));

        var raw = DocxKit.Message(
            "Site notes",
            DocxKit.DocxPart(DocxKit.Paragraphs("Scaffolding inspection passed")),
            DocxKit.Text("text/csv", "readings.csv", "gauge,value\nnorth,17"));

        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        await temp.Store.IngestEnvelopesAsync(folder, [StoreSeed.Envelope(1, hasAttachments: true)], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        Assert.Empty(Find(temp, "attachment:scaffolding", ct).Hits);

        await using var provider = new FetchingProvider(raw);
        await messages.GetAsync(provider, id, MessageBodyFormat.Text, fetchIfMissing: true, Caller, ct);

        var hit = Assert.Single(Find(temp, "attachment:scaffolding", ct).Hits);
        Assert.Equal("notes.docx", hit.AttachmentMatch?.FileName);
        Assert.Single(Find(temp, "attachment:gauge", ct).Hits);
        Assert.Equal(2, temp.Store.GetAttachmentTexts(id, ct).Count);
        Assert.Equal(0L, temp.Store.CountPendingAttachmentIndex(AttachmentTextIndexing.ExtractorVersion, ct));
    }

    [Fact]
    public async Task A_body_without_attachments_writes_no_attachment_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var messages = new MessageService(
            temp.Store, MessageParser.Default, new AuditLog(temp.Store, temp.Clock), new AgentPolicy(new AgentPolicyOptions(), temp.Clock));

        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        await temp.Store.IngestEnvelopesAsync(folder, [StoreSeed.Envelope(1)], ReferencesThreader.Instance, ct);
        var id = Require.Value(temp.Store.FindMessage(folder, new Uid(1), ct), "the message id");

        await using var provider = new FetchingProvider(Fixtures.Read("001-plain-simple.eml"));
        await messages.GetAsync(provider, id, MessageBodyFormat.Text, fetchIfMissing: true, Caller, ct);

        Assert.Equal(0L, StoreQuery.Scalar(temp.Store, "SELECT COUNT(*) FROM attachment_index", ct));
    }

    [Fact]
    public async Task Reading_attachment_metadata_does_not_pay_for_extraction()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var messages = new MessageService(
            temp.Store, MessageParser.Default, new AuditLog(temp.Store, temp.Clock), new AgentPolicy(new AgentPolicyOptions(), temp.Clock));

        var raw = DocxKit.Message("Notes", DocxKit.Text("text/plain", "n.txt", "metadata only"));
        var account = await StoreSeed.AccountAsync(temp.Store, ct);
        var folder = await StoreSeed.FolderAsync(temp.Store, account, "INBOX", FolderRole.Inbox, ct);
        var blob = await temp.Store.StoreBlobAsync(raw, ct);
        var id = await StoreSeed.MessageAsync(temp.Store, folder, StoreSeed.Envelope(1, hasAttachments: true), ct);
        await temp.Store.SetBodyTextAsync(id, "cached", blob.Id, true, ct);

        var attachments = await messages.ListAttachmentsAsync(null, id, Caller, ct);

        Assert.Single(attachments);
        Assert.Empty(temp.Store.GetAttachmentTexts(id, ct));
    }

    private static StoreSearchResult Find(TempStore temp, string query, CancellationToken ct) =>
        temp.Store.Search(new StoreSearchRequest { Query = SearchQueryParser.Parse(query).Query, Limit = 20 }, ct);

    private sealed class FetchingProvider(byte[] raw) : IMailProvider
    {
        public ServerCaps Capabilities { get; } = new();

        public bool IsConnected => true;

        public Task FetchRawMessageToAsync(FolderRef folder, Uid uid, Stream destination, CancellationToken ct) =>
            destination.WriteAsync(raw, ct).AsTask();

        public Task<byte[]> FetchRawMessageAsync(FolderRef folder, Uid uid, CancellationToken ct) => Task.FromResult(raw);

        public IAsyncEnumerable<SyncEvent> SyncFolderAsync(FolderRef folder, SyncPlan plan, CancellationToken ct) => throw Unused();

        public Task<ServerFolderInfo> OpenFolderAsync(FolderRef folder, bool writable, CancellationToken ct) => throw Unused();

        public Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken ct) => throw Unused();

        public Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task SetFlagsAsync(FolderRef folder, Uid uid, FlagDelta delta, CancellationToken ct) => throw Unused();

        public Task<Uid?> MoveAsync(FolderRef from, Uid uid, FolderRef to, CancellationToken ct) => throw Unused();

        public Task<AtomicMoveResult> MoveAtomicAsync(FolderRef from, Uid uid, UidValidity expected, FolderRef to, CancellationToken ct) => throw Unused();

        public Task<Uid?> AppendAsync(FolderRef folder, byte[] raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct) => throw Unused();

        public Task<Uid?> AppendAsync(FolderRef folder, Stream raw, MessageFlags flags, DateTimeOffset receivedUtc, CancellationToken ct) => throw Unused();

        public Task WatchAsync(FolderRef folder, Func<CancellationToken, Task> onChange, CancellationToken ct) => throw Unused();

        private static NotSupportedException Unused() => new("Not part of this test's fetch path.");
    }
}
