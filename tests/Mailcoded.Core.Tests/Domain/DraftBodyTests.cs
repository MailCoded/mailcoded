using Mailcoded.Core.Domain.Identities;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class DraftBodyTests
{
    private const string Quote = "On Mon, 1 Jan 2024 00:00:00 UTC, Ada wrote:\n\n> Hello\n";

    [Fact]
    public void With_nothing_to_add_the_body_is_returned_untouched()
    {
        Assert.Equal("Hi  \n\n", DraftBody.Compose("Hi  \n\n", null, null));
        Assert.Equal("Hi", DraftBody.Compose("Hi", null, string.Empty));
        Assert.Equal(string.Empty, DraftBody.Compose(string.Empty, null, null));
    }

    [Fact]
    public void A_new_message_gets_the_separator_line_then_the_signature()
    {
        Assert.Equal("Hi there\n\n-- \nAlex\nExample Pty\n", DraftBody.Compose("Hi there", "Alex\nExample Pty", null));
    }

    [Fact]
    public void The_separator_is_dash_dash_space_exactly()
    {
        Assert.Equal("-- ", DraftBody.Separator);
        Assert.Contains("\n-- \n", DraftBody.Compose("Hi", "Alex", null), StringComparison.Ordinal);
    }

    [Fact]
    public void Trailing_blank_lines_of_the_new_text_do_not_pile_up_before_the_separator()
    {
        Assert.Equal("Hi\n\n-- \nAlex\n", DraftBody.Compose("Hi\n\n\n  \n", "Alex", null));
    }

    [Fact]
    public void In_a_reply_the_signature_sits_between_the_new_text_and_the_quote()
    {
        var body = DraftBody.Compose("Thanks, done.", "Alex", Quote);

        Assert.Equal("Thanks, done.\n\n-- \nAlex\n\n" + Quote, body);
        Assert.True(body.IndexOf("-- \n", StringComparison.Ordinal) < body.IndexOf("On Mon", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reply_without_a_signature_is_the_text_then_the_quote()
    {
        Assert.Equal("Thanks.\n\n" + Quote, DraftBody.Compose("Thanks.\n", null, Quote));
        Assert.Equal(Quote, DraftBody.Compose(string.Empty, null, Quote));
    }

    [Fact]
    public void An_empty_new_text_starts_with_the_signature_block()
    {
        Assert.Equal("-- \nAlex\n", DraftBody.Compose(string.Empty, "Alex", null));
        Assert.Equal("-- \nAlex\n\n" + Quote, DraftBody.Compose("  \n", "Alex", Quote));
    }

    [Theory]
    [InlineData("Hi\n-- \nAlex", true)]
    [InlineData("Hi\r\n-- \r\nAlex", true)]
    [InlineData("-- \nAlex", true)]
    [InlineData("Hi\n--\nAlex", false)]
    [InlineData("Hi\n--- \nAlex", false)]
    [InlineData("Hi\n -- \nAlex", false)]
    [InlineData("Hi -- there", false)]
    [InlineData("", false)]
    public void A_signature_the_author_already_wrote_is_recognised(string text, bool expected)
    {
        Assert.Equal(expected, DraftBody.HasSeparator(text));
    }
}
