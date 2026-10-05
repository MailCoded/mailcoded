using Mailcoded.Tui;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

public sealed class RecipientCompletionTests
{
    [Theory]
    [InlineData("", 0, 0, 0, "")]
    [InlineData("ad", 2, 0, 2, "ad")]
    [InlineData("ada@example.org, bo", 19, 17, 2, "bo")]
    [InlineData("ada@example.org,bo", 18, 16, 2, "bo")]
    [InlineData("ada@example.org;  bo", 20, 18, 2, "bo")]
    [InlineData("ada@example.org, ", 17, 17, 0, "")]
    [InlineData("ada, bob@example.org", 3, 0, 3, "ada")]
    public void The_token_is_what_sits_between_separators_around_the_caret(string header, int caret, int start, int length, string prefix)
    {
        var token = RecipientCompletion.TokenAt(header, caret);

        Assert.Equal(start, token.Start);
        Assert.Equal(length, token.Length);
        Assert.Equal(prefix, token.Prefix);
    }

    [Fact]
    public void A_caret_inside_an_address_completes_the_whole_address_from_what_is_before_it()
    {
        var token = RecipientCompletion.TokenAt("a@x.org, ada@example.org, c@x.org", 11);

        Assert.Equal(9, token.Start);
        Assert.Equal(15, token.Length);
        Assert.Equal("ad", token.Prefix);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(500)]
    public void A_caret_outside_the_text_is_clamped(int caret)
    {
        var token = RecipientCompletion.TokenAt("ada", caret);

        Assert.InRange(token.Start, 0, 3);
        Assert.InRange(token.Start + token.Length, 0, 3);
    }

    [Fact]
    public void Candidates_already_in_the_field_are_left_out_in_any_case_and_order_is_kept()
    {
        var header = "ada@example.org, BOB@example.org, ca";
        var token = RecipientCompletion.TokenAt(header, header.Length);

        var kept = RecipientCompletion.Unlisted(header, token, ["bob@example.org", "cal@example.org", "ada@example.org", "cat@example.org"]);

        Assert.Equal(["cal@example.org", "cat@example.org"], kept);
    }

    [Fact]
    public void The_token_being_completed_does_not_hide_a_candidate_that_equals_it()
    {
        var header = "ada@example.org";
        var token = RecipientCompletion.TokenAt(header, header.Length);

        Assert.Equal(["ada@example.org"], RecipientCompletion.Unlisted(header, token, ["ada@example.org"]));
    }

    [Fact]
    public void Replacing_a_token_puts_the_caret_after_the_new_address_and_leaves_the_rest()
    {
        var draft = new DraftBuffer { To = "a@x.org, ad, c@x.org", Field = DraftField.To };

        Assert.True(draft.ReplaceInHeader(DraftField.To, 9, 2, "ada@example.org"));

        Assert.Equal("a@x.org, ada@example.org, c@x.org", draft.To);
        Assert.Equal(9 + "ada@example.org".Length, draft.HeaderCaret);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 99)]
    [InlineData(99, 0)]
    public void A_stale_range_replaces_nothing(int start, int length)
    {
        var draft = new DraftBuffer { To = "abc" };

        Assert.False(draft.ReplaceInHeader(DraftField.To, start, length, "x@example.org"));
        Assert.Equal("abc", draft.To);
    }

    [Fact]
    public void The_body_is_never_a_replacement_target_and_the_header_cap_holds()
    {
        var draft = new DraftBuffer { To = new string('a', 990) };

        Assert.False(draft.ReplaceInHeader(DraftField.Body, 0, 0, "x"));
        Assert.False(draft.ReplaceInHeader(DraftField.To, 0, 1, "long-address-over-the-cap@example.org"));
        Assert.Equal(990, draft.To.Length);
    }
}
