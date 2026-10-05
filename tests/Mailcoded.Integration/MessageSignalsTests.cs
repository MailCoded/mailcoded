using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
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
                SignalMail.Raw("Personal note", ["Message-ID: <sig-plain@example.com>", "Content-Type: text/plain"]),
                SignalMail.Raw("List mail", ["Message-ID: <sig-list@example.com>", "List-Id: <announce.example.net>", "Content-Type: text/plain"]),
                SignalMail.Raw("Auto reply", ["Message-ID: <sig-auto@example.com>", "Auto-Submitted: auto-replied", "Content-Type: text/plain"]),
                SignalMail.Raw(
                    "Sealed",
                    ["Message-ID: <sig-pgp@example.com>", "Content-Type: multipart/encrypted; protocol=\"application/pgp-encrypted\"; boundary=\"b\""],
                    "--b\r\nContent-Type: application/pgp-encrypted\r\n\r\nVersion: 1\r\n--b\r\nContent-Type: application/octet-stream\r\n\r\nxx\r\n--b--"),
                SignalMail.Raw(
                    "Enveloped",
                    ["Message-ID: <sig-smime@example.com>", "Content-Type: application/pkcs7-mime; smime-type=enveloped-data; name=smime.p7m", "Content-Transfer-Encoding: base64"],
                    "AAAA"),
            ],
            ct);

        var accountId = await harness.AddAccountAsync(ct);
        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        var report = await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
        Assert.Equal(5, report.Added);

        Assert.Equal(["Auto reply", "List mail"], SignalMail.Subjects(harness, "is:automated", ct));
        Assert.Equal(["Enveloped", "Sealed"], SignalMail.Subjects(harness, "is:encrypted", ct));
        Assert.Equal(["Enveloped", "Personal note", "Sealed"], SignalMail.Subjects(harness, "-is:automated", ct));
        Assert.Empty(SignalMail.Subjects(harness, "-is:encrypted", ct));
    }
}
