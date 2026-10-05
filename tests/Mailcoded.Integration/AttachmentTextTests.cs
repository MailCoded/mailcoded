using System.IO.Compression;
using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Store;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>A message with a .docx and a CSV is APPENDed to a real server, synced, fetched lazily, and found through attachment:.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.AttachmentText)]
public sealed class AttachmentTextTests : IClassFixture<MailStackFixture>
{
    private readonly MailStackFixture _stack;

    public AttachmentTextTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task A_fetched_body_makes_its_docx_and_csv_searchable_and_older_mail_is_picked_up_by_the_backfill()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("attachment-text");
        await using var harness = MailHarness.Open(workspace, new InMemorySecretStore(), _stack);

        await _stack.AppendAsync(
            FolderPath.Inbox,
            [
                Message("Lighting plan", "<att-1@example.com>", ("lighting.docx", Docx("Warehouse lighting replacement"), DocxType)),
                Message("Totals", "<att-2@example.com>", ("totals.csv", Encoding.UTF8.GetBytes("region,total\nnorth,1200\n"), "text/csv")),
                Message("Plain", "<att-3@example.com>"),
            ],
            ct);

        var accountId = await harness.AddAccountAsync(ct);
        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        var report = await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
        Assert.Equal(3, report.Added);

        Assert.Empty(Subjects(harness, "attachment:lighting", ct));

        var inbox = harness.RequireFolder(accountId, "INBOX", ct);
        var items = harness.Store.ListEnvelopes(inbox.Id, null, 50, ct).Items;

        var fetched = 0;
        foreach (var item in items)
        {
            if (item.Subject == "Totals") continue;
            await harness.Messages.GetAsync(provider, item.Id, MessageBodyFormat.Text, fetchIfMissing: true, CallerContext.Rpc, ct);
            fetched++;
        }

        Assert.Equal(2, fetched);
        Assert.Equal(["Lighting plan"], Subjects(harness, "attachment:lighting", ct));
        Assert.Empty(Subjects(harness, "attachment:north", ct));

        var totals = items.First(item => item.Subject == "Totals");
        await harness.Messages.GetAsync(provider, totals.Id, MessageBodyFormat.Text, fetchIfMissing: true, CallerContext.Rpc, ct);
        Assert.Equal(["Totals"], Subjects(harness, "attachment:north", ct));

        await harness.Store.SetAttachmentTextsAsync(totals.Id, [], 0, ct);
        Assert.Empty(Subjects(harness, "attachment:north", ct));

        var backfill = new AttachmentTextBackfill(harness.Store, harness.Parser);
        Assert.Equal(0, await backfill.RunWithinAsync(TimeSpan.FromSeconds(30), ct));
        Assert.Equal(["Totals"], Subjects(harness, "attachment:north", ct));
    }

    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static string[] Subjects(MailHarness harness, string query, CancellationToken ct)
    {
        var hits = harness.Store.Search(
            new StoreSearchRequest { Query = SearchQueryParser.Parse(query).Query, Order = SearchOrder.Date, Limit = 50 },
            ct).Hits;

        var subjects = new List<string>();
        foreach (var hit in hits) subjects.Add(hit.Subject ?? string.Empty);
        subjects.Sort(StringComparer.Ordinal);
        return [.. subjects];
    }

    private static byte[] Docx(string paragraph)
    {
        var xml = "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>"
            + paragraph + "</w:t></w:r></w:p></w:body></w:document>";

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("word/document.xml").Open();
            var bytes = Encoding.UTF8.GetBytes(xml);
            entry.Write(bytes, 0, bytes.Length);
        }

        return stream.ToArray();
    }

    private static byte[] Message(string subject, string messageId, params (string Name, byte[] Data, string Type)[] attachments)
    {
        var builder = new StringBuilder();
        builder.Append("From: Sender <sender@example.com>\r\nTo: recipient@example.org\r\n");
        builder.Append("Subject: ").Append(subject).Append("\r\nMessage-ID: ").Append(messageId);
        builder.Append("\r\nDate: Mon, 05 Jan 2026 09:00:00 +0000\r\nMIME-Version: 1.0\r\n");
        builder.Append("Content-Type: multipart/mixed; boundary=\"b\"\r\n\r\n");
        builder.Append("--b\r\nContent-Type: text/plain\r\n\r\nSee attached.\r\n");

        foreach (var (name, data, type) in attachments)
        {
            builder.Append("--b\r\nContent-Type: ").Append(type).Append("\r\n");
            builder.Append("Content-Disposition: attachment; filename=\"").Append(name).Append("\"\r\n");
            builder.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            builder.Append(Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
        }

        builder.Append("--b--\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
