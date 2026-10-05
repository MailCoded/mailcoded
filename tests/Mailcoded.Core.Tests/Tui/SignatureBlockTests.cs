using Mailcoded.Protocol;
using Mailcoded.Tui;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

public sealed class SignatureBlockTests
{
    private static AccountDto Account(string? forNew = "work", string? forReply = "short") => new()
    {
        Id = 1,
        Email = "me@example.com",
        Provider = "imap",
        Auth = "password",
        Signatures =
        [
            new SignatureDto { Name = "work", Text = "Alex Example\nExample Pty" },
            new SignatureDto { Name = "short", Text = "Alex" },
        ],
        DefaultSignatureNew = forNew,
        DefaultSignatureReply = forReply,
    };

    [Fact]
    public void A_new_message_and_a_reply_start_with_their_own_defaults()
    {
        Assert.Equal("Alex Example\nExample Pty", SignatureBlock.DefaultText(Account(), reply: false));
        Assert.Equal("Alex", SignatureBlock.DefaultText(Account(), reply: true));
    }

    [Fact]
    public void No_default_unknown_default_or_no_account_means_no_signature()
    {
        Assert.Null(SignatureBlock.DefaultText(Account(forNew: null), reply: false));
        Assert.Null(SignatureBlock.DefaultText(Account(forNew: "gone"), reply: false));
        Assert.Null(SignatureBlock.DefaultText(null, reply: false));
        Assert.Null(SignatureBlock.DefaultText(Account() with { Signatures = [] }, reply: true));
    }

    [Fact]
    public void An_older_daemon_that_sends_no_signature_fields_changes_nothing()
    {
        var older = new AccountDto { Id = 1, Email = "me@example.com", Provider = "imap", Auth = "password" };

        Assert.Null(SignatureBlock.DefaultText(older, reply: false));
        Assert.Equal("> quoted", SignatureBlock.Body(null, "> quoted"));
        Assert.Equal(string.Empty, SignatureBlock.Body(null, null));
    }

    [Fact]
    public void The_body_leaves_room_to_type_above_the_separator_and_puts_the_quote_below_the_signature()
    {
        Assert.Equal("\n\n-- \nAlex", SignatureBlock.Body("Alex", null));
        Assert.Equal("\n\n-- \nAlex\n\n> quoted", SignatureBlock.Body("Alex", "> quoted"));

        var draft = new DraftBuffer();
        draft.SetBody(SignatureBlock.Body("Alex Example\nExample Pty", "> quoted"));

        Assert.Equal(["", "", "-- ", "Alex Example", "Example Pty", "", "> quoted"], draft.Body);
    }
}
