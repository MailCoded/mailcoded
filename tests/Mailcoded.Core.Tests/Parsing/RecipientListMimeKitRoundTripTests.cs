using Mailcoded.Core.Domain.Correspondents;
using MimeKit;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class RecipientListMimeKitRoundTripTests
{
    [Fact]
    public void The_text_the_providers_store_reads_back_to_the_same_names_and_addresses()
    {
        var list = new InternetAddressList
        {
            new MailboxAddress("Doe, Jane", "jane@example.com"),
            new MailboxAddress("Zoë \"Zed\" Müller", "zoe@example.org"),
            new MailboxAddress(null, "plain@example.net"),
            new MailboxAddress("", "empty-name@example.net"),
            new MailboxAddress("田中 太郎", "taro@example.jp"),
            new GroupAddress("Team", [new MailboxAddress("Pat", "pat@example.com"), new MailboxAddress(null, "sam@example.com")]),
        };

        var found = RecipientList.Parse(list.ToString());

        Assert.Equal(
            [
                ("Doe, Jane", "jane@example.com"),
                ("Zoë \"Zed\" Müller", "zoe@example.org"),
                (null, "plain@example.net"),
                (null, "empty-name@example.net"),
                ("田中 太郎", "taro@example.jp"),
                ("Pat", "pat@example.com"),
                (null, "sam@example.com"),
            ],
            found.Select(a => (a.DisplayName, a.Address.Value)));
    }

    [Fact]
    public void An_encoded_form_of_the_same_list_also_reads_back_to_the_addresses()
    {
        var list = new InternetAddressList
        {
            new MailboxAddress("Zoë Müller", "zoe@example.org"),
            new MailboxAddress("Doe, Jane", "jane@example.com"),
        };

        var found = RecipientList.Parse(list.ToString(encode: true));

        Assert.Equal(["zoe@example.org", "jane@example.com"], found.Select(a => a.Address.Value));
    }

    [Fact]
    public void The_text_mimekit_itself_parses_and_this_reader_agree_on_the_addresses()
    {
        string[] samples =
        [
            "Ada <ada@example.com>, \"Lovelace, A.\" <al@example.org>",
            "undisclosed-recipients:;",
            "A <a@example.com>, B <b@example.org> (comment)",
            "\"Quoted\\\\ Name\" <q@example.com>",
            "x@example.com,y@example.com;z@example.com",
        ];

        foreach (var sample in samples)
        {
            var expected = InternetAddressList.TryParse(sample, out var list)
                ? list.Mailboxes.Select(m => m.Address.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray()
                : [];

            var actual = RecipientList.Parse(sample).Select(a => a.Address.Value.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();

            Assert.True(
                actual.All(a => expected.Contains(a) || expected.Length == 0),
                $"'{sample}': reader found {string.Join(",", actual)}, MimeKit found {string.Join(",", expected)}");
        }
    }
}
