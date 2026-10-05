using Mailcoded.Core.Domain.Correspondents;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class SentRecipientsParsingTests
{
    private const string Fixture = "053-sent-recipients.eml";

    private static readonly DateTimeOffset InternalDate = new(2025, 2, 3, 9, 31, 0, TimeSpan.Zero);

    private static ParsedMessage Parse() =>
        MessageParser.Default.Parse(Fixtures.Read(Fixture), InternalDate, TestContext.Current.CancellationToken);

    [Fact]
    public void The_stored_To_and_Cc_text_reads_back_with_decoded_names_and_the_group_flattened()
    {
        var parsed = Parse();

        Assert.Equal(
            [("Example, Ada", "ada@example.org"), ("Zöe Müller", "zoe@example.net"), (null, "bob@example.net"), (null, "noreply@example.org")],
            RecipientList.Parse(parsed.To).Select(a => (a.DisplayName, a.Address.Value)));

        Assert.Equal(
            [(null, "carol@example.org"), ("Dan \"D\" Example", "dan@example.org"), (null, "tester@example.com")],
            RecipientList.Parse(parsed.Cc).Select(a => (a.DisplayName, a.Address.Value)));
    }

    [Fact]
    public void Learning_from_the_message_skips_the_robot_and_the_account_and_never_sees_the_Bcc()
    {
        var parsed = Parse();
        var own = CorrespondentLearning.OwnKeys("tester@example.com", []);

        Assert.True(CorrespondentLearning.IsAuthoredBy(RecipientList.Parse(parsed.From), own));

        var chosen = CorrespondentLearning.Select(RecipientList.Parse(parsed.To), RecipientList.Parse(parsed.Cc), own);

        Assert.Equal(
            ["ada@example.org", "zoe@example.net", "bob@example.net", "carol@example.org", "dan@example.org"],
            chosen.Select(a => a.Key));
        Assert.DoesNotContain(chosen, a => a.Key.Contains("hidden", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ingesting_it_into_Sent_teaches_the_same_people()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var parsed = Parse();
        var account = await StoreSeed.AccountAsync(temp.Store, ct, "tester@example.com");
        var sent = await StoreSeed.FolderAsync(temp.Store, account, "Sent", FolderRole.Sent, ct);

        await temp.Store.IngestEnvelopesAsync(
            sent,
            [new RemoteEnvelope
            {
                Uid = new Uid(1),
                Flags = MessageFlags.None,
                MessageIdHeader = "053-sent-recipients@example.com",
                From = parsed.From,
                To = parsed.To,
                Cc = parsed.Cc,
                DateUtc = parsed.DateUtc,
            }],
            null,
            ct);

        var learned = temp.Store.ListCorrespondents(account, ct).ToDictionary(c => c.Address);
        Assert.Equal(
            ["ada@example.org", "bob@example.net", "carol@example.org", "dan@example.org", "zoe@example.net"],
            learned.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Zöe Müller", learned["zoe@example.net"].DisplayName);
        Assert.Equal(new DateTimeOffset(2025, 2, 3, 9, 30, 0, TimeSpan.Zero), learned["ada@example.org"].LastUsedUtc);
    }
}
