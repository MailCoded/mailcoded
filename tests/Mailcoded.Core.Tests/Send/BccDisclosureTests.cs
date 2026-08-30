using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

/// <summary>A Bcc header in the transmitted bytes discloses every blind recipient to all the others.</summary>
public sealed class BccDisclosureTests
{
    private const string Blind = "secret@hidden.example";
    private const string SecondBlind = "quiet@hidden.example";

    [Fact]
    public async Task TheTransmittedBytesCarryNoBccHeaderButEveryBccAddressStillGetsRcptTo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await SendPathHarness.CreateAsync(ct, "@example.org", "@hidden.example");

        var preview = await harness.PreviewAsync(
            new DraftRequest
            {
                To = [EmailAddress.Parse("bob@example.org")],
                Cc = [EmailAddress.Parse("carol@example.org")],
                Bcc = [EmailAddress.Parse(Blind), EmailAddress.Parse(SecondBlind)],
                Subject = "Quarterly numbers",
                BodyText = "Numbers attached in the next one.",
            },
            ct);

        var result = await harness.SendDraftAsync(preview.OutboxId, preview.ConfirmToken, ct);
        Assert.Equal(OutboxState.Sent, result.State);

        var submission = Assert.Single(harness.Sender.Sends);
        var transmitted = Encoding.UTF8.GetString(submission.Raw);

        Assert.DoesNotContain("Bcc:", transmitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden.example", transmitted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bob@example.org", transmitted, StringComparison.Ordinal);
        Assert.Contains("carol@example.org", transmitted, StringComparison.Ordinal);

        var stored = Require.Ref(harness.Store.GetOutbox(preview.OutboxId, ct), "the outbox row");
        Assert.DoesNotContain("Bcc:", Encoding.UTF8.GetString(stored.Raw), StringComparison.OrdinalIgnoreCase);

        var envelope = Addresses(submission.Recipients);
        Assert.Equal(4, envelope.Count);
        Assert.Contains(Blind, envelope, StringComparer.Ordinal);
        Assert.Contains(SecondBlind, envelope, StringComparer.Ordinal);
        Assert.Contains("bob@example.org", envelope, StringComparer.Ordinal);
        Assert.Contains("carol@example.org", envelope, StringComparer.Ordinal);
    }

    [Fact]
    public void ADraftAddressedOnlyToBlindRecipientsStillBuildsAHeaderlessRecipientList()
    {
        var ct = TestContext.Current.CancellationToken;

        var spec = new DraftSpec
        {
            From = EmailAddress.Parse("alice@example.com"),
            To = [],
            Cc = [],
            Bcc = [EmailAddress.Parse(Blind), EmailAddress.Parse(SecondBlind)],
            Subject = "Quarterly numbers",
            BodyText = "Numbers attached in the next one.",
            MessageId = MessageId.Parse("draft-outbox-9@example.com"),
            DateUtc = StoreSeed.BaseDate,
        };

        var built = Encoding.UTF8.GetString(MessageBuilder.Build(spec, ct));

        Assert.DoesNotContain("Bcc:", built, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden.example", built, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("undisclosed-recipients", built, StringComparison.Ordinal);
    }

    private static List<string> Addresses(IReadOnlyList<EmailAddress> addresses)
    {
        var values = new List<string>(addresses.Count);
        foreach (var address in addresses) values.Add(address.Value);
        return values;
    }
}
