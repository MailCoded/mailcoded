using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class ReplyOwnAddressesTests
{
    private static readonly IReadOnlyList<EmailAddress> Own =
        [EmailAddress.Parse("me@example.com"), EmailAddress.Parse("sales@example.com")];

    private static ParsedMessage Original(string to, string? cc = null) => new()
    {
        MessageId = MessageId.Parse("orig-1@example.net"),
        From = "Ada <ada@example.net>",
        To = to,
        Cc = cc,
        Subject = "Question",
        BodyText = "Hello",
        DateUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Reply_all_leaves_every_own_address_out_of_cc()
    {
        var context = ReplyBuilder.Create(
            Original("me@example.com, sales@example.com, friend@example.org", "Carol <carol@example.org>"),
            Own,
            replyAll: true);

        Assert.Equal(["ada@example.net"], context.To.Select(a => a.Value));
        Assert.Equal(["friend@example.org", "carol@example.org"], context.Cc.Select(a => a.Value));
    }

    [Fact]
    public void Own_addresses_are_matched_without_regard_to_case()
    {
        var context = ReplyBuilder.Create(Original("ME@Example.com, SALES@EXAMPLE.COM, x@example.org"), Own, replyAll: true);

        Assert.Equal(["x@example.org"], context.Cc.Select(a => a.Value));
    }

    [Fact]
    public void A_recipient_listed_twice_in_different_case_is_kept_once()
    {
        var context = ReplyBuilder.Create(Original("x@example.org, X@example.org", "ADA@example.net"), Own, replyAll: true);

        Assert.Equal(["x@example.org"], context.Cc.Select(a => a.Value));
    }

    [Fact]
    public void No_own_addresses_means_nothing_is_dropped()
    {
        var context = ReplyBuilder.Create(Original("me@example.com"), null, replyAll: true);

        Assert.Equal(["me@example.com"], context.Cc.Select(a => a.Value));
    }
}
