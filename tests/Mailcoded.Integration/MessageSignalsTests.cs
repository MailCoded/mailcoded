using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Store;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>The envelope FETCH asks for the signal headers, and BODYSTRUCTURE reveals the encrypted shapes, against a real server.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.Signals)]
public sealed class MessageSignalsTests : IClassFixture<MailStackFixture>
{
    private readonly MailStackFixture _stack;

    public MessageSignalsTests(MailStackFixture stack) => _stack = stack;

    [Fact]
    public async Task Sync_derives_automated_and_encrypted_from_the_envelope_fetch_alone()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("signals");
        await using var harness = MailHarness.Open(workspace, new InMemorySecretStore(), _stack);

        await _stack.AppendAsync(
            FolderPath.Inbox,
            [
                Raw("Personal note", ["Message-ID: <sig-plain@example.com>", "Content-Type: text/plain"]),
                Raw("List mail", ["Message-ID: <sig-list@example.com>", "List-Id: <announce.example.net>", "Content-Type: text/plain"]),
                Raw("Auto reply", ["Message-ID: <sig-auto@example.com>", "Auto-Submitted: auto-replied", "Content-Type: text/plain"]),
                Raw(
                    "Sealed",
                    ["Message-ID: <sig-pgp@example.com>", "Content-Type: multipart/encrypted; protocol=\"application/pgp-encrypted\"; boundary=\"b\""],
                    "--b\r\nContent-Type: application/pgp-encrypted\r\n\r\nVersion: 1\r\n--b\r\nContent-Type: application/octet-stream\r\n\r\nxx\r\n--b--"),
                Raw(
                    "Enveloped",
                    ["Message-ID: <sig-smime@example.com>", "Content-Type: application/pkcs7-mime; smime-type=enveloped-data; name=smime.p7m", "Content-Transfer-Encoding: base64"],
                    "AAAA"),
            ],
            ct);

        var accountId = await harness.AddAccountAsync(ct);
        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        var report = await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
        Assert.Equal(5, report.Added);

        Assert.Equal(["Auto reply", "List mail"], Subjects(harness, "is:automated", ct));
        Assert.Equal(["Enveloped", "Sealed"], Subjects(harness, "is:encrypted", ct));
        Assert.Equal(["Personal note"], Subjects(harness, "-is:automated -is:encrypted", ct));
    }

    [Fact]
    public async Task Sync_verifies_only_through_an_authserv_id_the_account_trusts()
    {
        _stack.SkipUnlessAvailable();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        using var workspace = TestWorkspace.Create("verified");
        await using var harness = MailHarness.Open(workspace, new InMemorySecretStore(), _stack);

        await _stack.AppendAsync(
            FolderPath.Inbox,
            [
                Raw("Passing", ["Message-ID: <ver-pass@example.com>", "Authentication-Results: mx.example.net; dmarc=pass header.from=example.com"]),
                Raw(
                    "Forged below a fail",
                    [
                        "Message-ID: <ver-fail@example.com>",
                        "Authentication-Results: mx.example.net; dmarc=fail header.from=example.com",
                        "Authentication-Results: mx.example.net; dmarc=pass header.from=example.com",
                    ]),
                Raw("Foreign id", ["Message-ID: <ver-foreign@example.com>", "Authentication-Results: mx.attacker.example.org; dmarc=pass header.from=example.com"]),
                Raw("Comment trick", ["Message-ID: <ver-comment@example.com>", "Authentication-Results: mx.example.net (dmarc=pass header.from=example.com)"]),
                Raw("No header", ["Message-ID: <ver-none@example.com>"]),
            ],
            ct);

        var accountId = await harness.AddAccountAsync(ct);
        await harness.Store.SetTrustedAuthservIdsAsync(accountId, ["mx.example.net"], ct);

        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        var report = await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
        Assert.Equal(5, report.Added);

        Assert.Equal(["Passing"], Subjects(harness, "is:verified", ct));
        Assert.Equal(["Comment trick", "Forged below a fail", "Foreign id", "No header"], Subjects(harness, "-is:verified", ct));
    }

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
