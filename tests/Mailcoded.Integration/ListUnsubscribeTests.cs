using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Store;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>The envelope FETCH already asks for List-Unsubscribe, so sync stores it with no body and no extra round trip.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.Signals)]
public sealed class ListUnsubscribeTests : IClassFixture<MailStackFixture>
{
    private readonly MailStackFixture _stack;

    public ListUnsubscribeTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task Sync_stores_the_unsubscribe_headers_and_records_none_for_mail_without_them()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("unsubscribe");
        await using var harness = MailHarness.Open(workspace, new InMemorySecretStore(), _stack);

        await _stack.AppendAsync(
            FolderPath.Inbox,
            [
                Raw(
                    "Newsletter",
                    [
                        "Message-ID: <unsub-list@example.com>",
                        "List-Unsubscribe: <mailto:leave@example.net?subject=unsubscribe>,\r\n <https://lists.example.net/u/abc>",
                        "List-Unsubscribe-Post: List-Unsubscribe=One-Click",
                    ]),
                Raw("Personal note", ["Message-ID: <unsub-plain@example.com>"]),
            ],
            ct);

        var accountId = await harness.AddAccountAsync(ct);
        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        var report = await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
        Assert.Equal(2, report.Added);

        var newsletter = Options(harness, "Newsletter", ct);
        Assert.Equal("leave@example.net", newsletter.Mailto?.Address);
        Assert.Equal(["https://lists.example.net/u/abc"], newsletter.Web);
        Assert.True(newsletter.OneClick);

        Assert.True(Options(harness, "Personal note", ct).IsEmpty);
    }

    private static UnsubscribeOptions Options(MailHarness harness, string subject, CancellationToken ct)
    {
        var hits = harness.Store.Search(
            new StoreSearchRequest { Query = SearchQueryParser.Parse("subject:\"" + subject + "\"").Query, Limit = 10 },
            ct).Hits;

        var row = harness.Store.GetEnvelope(hits[0].Id, ct) ?? throw new InvalidOperationException("The synced row is missing.");
        return UnsubscribeStorage.Decode(row.Unsubscribe) ?? throw new InvalidOperationException("The unsubscribe headers were not stored at sync.");
    }

    private static byte[] Raw(string subject, string[] headers, string body = "body")
    {
        var builder = new StringBuilder();
        builder.Append("From: Sender <sender@example.com>\r\nTo: recipient@example.org\r\n");
        builder.Append("Subject: ").Append(subject).Append("\r\nDate: Mon, 05 Jan 2026 09:00:00 +0000\r\nMIME-Version: 1.0\r\n");
        foreach (var header in headers) builder.Append(header).Append("\r\n");
        builder.Append("\r\n").Append(body).Append("\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
