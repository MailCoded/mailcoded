using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Integration.Support;
using Xunit;

namespace Mailcoded.Integration;

/// <summary>Verification trusts only the authserv-id the account names; a second class so it gets a mailbox of its own.</summary>
[Collection(MailStackCollection.Name)]
[Trait(IntegrationTraits.Category, IntegrationTraits.Docker)]
[Trait(IntegrationTraits.Scenario, IntegrationTraits.Signals)]
public sealed class SenderVerificationTests : IClassFixture<MailStackFixture>
{
    private readonly MailStackFixture _stack;

    public SenderVerificationTests(MailStackFixture stack) => _stack = stack;

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
                SignalMail.Raw("Passing", ["Message-ID: <ver-pass@example.com>", "Authentication-Results: mx.example.net; dmarc=pass header.from=example.com"]),
                SignalMail.Raw(
                    "Forged below a fail",
                    [
                        "Message-ID: <ver-fail@example.com>",
                        "Authentication-Results: mx.example.net; dmarc=fail header.from=example.com",
                        "Authentication-Results: mx.example.net; dmarc=pass header.from=example.com",
                    ]),
                SignalMail.Raw("Foreign id", ["Message-ID: <ver-foreign@example.com>", "Authentication-Results: mx.attacker.example.org; dmarc=pass header.from=example.com"]),
                SignalMail.Raw("Comment trick", ["Message-ID: <ver-comment@example.com>", "Authentication-Results: mx.example.net (dmarc=pass header.from=example.com)"]),
                SignalMail.Raw("No header", ["Message-ID: <ver-none@example.com>"]),
            ],
            ct);

        var accountId = await harness.AddAccountAsync(ct);
        await harness.Store.SetTrustedAuthservIdsAsync(accountId, ["mx.example.net"], ct);

        await using var provider = await harness.ConnectProviderAsync(accountId, ct);
        var report = await harness.Accounts.InitialSyncAsync(provider, accountId, null, ct);
        Assert.Equal(5, report.Added);

        Assert.Equal(["Passing"], SignalMail.Subjects(harness, "is:verified", ct));
        Assert.Equal(["Comment trick", "Forged below a fail", "Foreign id", "No header"], SignalMail.Subjects(harness, "-is:verified", ct));
    }
}
