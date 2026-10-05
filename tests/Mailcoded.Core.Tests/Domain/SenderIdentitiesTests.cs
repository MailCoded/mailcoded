using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class SenderIdentitiesTests
{
    private static readonly EmailAddress Account = EmailAddress.Parse("me@example.com");

    private static SenderIdentity Identity(string address, string? name = null, string? replyTo = null)
    {
        Assert.True(SenderIdentity.TryCreate(address, name, replyTo, out var identity, out var error), error);
        return identity;
    }

    private static EmailAddress[] Addresses(params string[] values) => values.Select(EmailAddress.Parse).ToArray();

    [Fact]
    public void No_requested_address_means_the_account_address_with_the_account_display_name()
    {
        Assert.True(SenderIdentities.TryResolve(Account, "Me", [], null, out var sender));

        Assert.Equal(Account, sender.Address);
        Assert.Equal("Me", sender.DisplayName);
        Assert.Null(sender.ReplyTo);
    }

    [Theory]
    [InlineData("me@example.com")]
    [InlineData("ME@example.com")]
    [InlineData("Me@EXAMPLE.COM")]
    public void The_account_address_is_allowed_in_any_case_and_goes_out_as_configured(string requested)
    {
        Assert.True(SenderIdentities.TryResolve(Account, null, [], EmailAddress.Parse(requested), out var sender));
        Assert.Equal("me@example.com", sender.Address.Value);
    }

    [Fact]
    public void An_identity_is_allowed_case_insensitively_and_keeps_its_configured_spelling()
    {
        var identities = new[] { Identity("Sales@Example.com", "Sales", "help@example.com") };

        Assert.True(SenderIdentities.TryResolve(Account, "Me", identities, EmailAddress.Parse("SALES@example.COM"), out var sender));

        Assert.Equal("Sales@example.com", sender.Address.Value);
        Assert.Equal("Sales", sender.DisplayName);
        Assert.Equal("help@example.com", sender.ReplyTo?.Value);
    }

    [Fact]
    public void An_identity_without_a_name_borrows_the_account_name()
    {
        var identities = new[] { Identity("alias@example.com") };

        Assert.True(SenderIdentities.TryResolve(Account, "Me", identities, EmailAddress.Parse("alias@example.com"), out var sender));
        Assert.Equal("Me", sender.DisplayName);
    }

    [Fact]
    public void An_identity_for_the_account_address_can_set_its_name_and_reply_to()
    {
        var identities = new[] { Identity("me@example.com", "Me At Work", "team@example.com") };

        Assert.True(SenderIdentities.TryResolve(Account, "Me", identities, null, out var sender));
        Assert.Equal("Me At Work", sender.DisplayName);
        Assert.Equal("team@example.com", sender.ReplyTo?.Value);
    }

    [Theory]
    [InlineData("boss@example.com")]
    [InlineData("me@example.org")]
    [InlineData("me@examp1e.com")]
    [InlineData("me@mail.example.com")]
    [InlineData("me+tag@example.com")]
    [InlineData("m.e@example.com")]
    [InlineData("\"me\"@example.com")]
    public void Anything_else_is_refused_including_lookalikes_and_quoted_forms(string requested)
    {
        var identities = new[] { Identity("alias@example.com") };

        Assert.False(SenderIdentities.TryResolve(Account, "Me", identities, EmailAddress.Parse(requested), out var sender));
        Assert.Equal(default, sender);
    }

    [Fact]
    public void A_forgotten_identity_is_refused_again()
    {
        var identities = new[] { Identity("alias@example.com") };
        var without = SenderIdentities.Without(identities, EmailAddress.Parse("ALIAS@example.com"), out var removed);

        Assert.True(removed);
        Assert.False(SenderIdentities.TryResolve(Account, null, without, EmailAddress.Parse("alias@example.com"), out _));
    }

    [Fact]
    public void Reply_picks_the_first_recipient_the_account_owns_in_original_order()
    {
        var identities = new[] { Identity("sales@example.com"), Identity("help@example.com") };

        var picked = SenderIdentities.PickForReply(Account, identities, Addresses("list@example.net", "help@example.com", "sales@example.com"));

        Assert.Equal("help@example.com", picked.Value);
    }

    [Fact]
    public void Reply_prefers_the_account_address_when_it_comes_first()
    {
        var identities = new[] { Identity("sales@example.com") };

        Assert.Equal(Account, SenderIdentities.PickForReply(Account, identities, Addresses("ME@example.com", "sales@example.com")));
    }

    [Fact]
    public void Reply_matches_case_insensitively_and_answers_with_the_configured_spelling()
    {
        var identities = new[] { Identity("Sales@example.com") };

        Assert.Equal("Sales@example.com", SenderIdentities.PickForReply(Account, identities, Addresses("SALES@EXAMPLE.COM")).Value);
    }

    [Fact]
    public void Reply_falls_back_to_the_account_address_for_a_blind_copy_or_an_unrelated_list()
    {
        var identities = new[] { Identity("sales@example.com") };

        Assert.Equal(Account, SenderIdentities.PickForReply(Account, identities, []));
        Assert.Equal(Account, SenderIdentities.PickForReply(Account, identities, Addresses("friend@example.net", "other@example.org")));
    }

    [Fact]
    public void Own_addresses_lists_the_account_once_whatever_the_identities_say()
    {
        var identities = new[] { Identity("ME@example.com"), Identity("alias@example.com") };

        Assert.Equal(["me@example.com", "alias@example.com"], SenderIdentities.OwnAddresses(Account, identities).Select(a => a.Value));
    }

    [Fact]
    public void Adding_an_address_twice_replaces_it_and_forgetting_an_unknown_one_changes_nothing()
    {
        var first = SenderIdentities.With([], Identity("alias@example.com", "One"));
        var second = SenderIdentities.With(first, Identity("ALIAS@example.com", "Two"));

        Assert.Equal("Two", Assert.Single(second).DisplayName);

        var same = SenderIdentities.Without(second, EmailAddress.Parse("other@example.com"), out var removed);
        Assert.False(removed);
        Assert.Single(same);
    }

    [Fact]
    public void The_list_is_capped()
    {
        var identities = new List<SenderIdentity>();
        for (var i = 0; i < SenderIdentities.MaxIdentities; i++)
            identities = [.. SenderIdentities.With(identities, Identity($"a{i}@example.com"))];

        Assert.Throws<ArgumentException>(() => SenderIdentities.With(identities, Identity("one-more@example.com")));
        Assert.Equal(SenderIdentities.MaxIdentities, SenderIdentities.With(identities, Identity("A0@example.com", "Renamed")).Count);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("Alice <alice@example.com>")]
    [InlineData("alice@example.com\r\nBcc: victim@example.org")]
    [InlineData("alice@example.com,bob@example.com")]
    [InlineData("a@b@example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void A_malformed_or_injecting_address_is_not_an_identity(string? address)
    {
        Assert.False(SenderIdentity.TryCreate(address, null, null, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("Alice\r\nBcc: victim@example.org")]
    [InlineData("Alice\nX: y")]
    [InlineData("Ali\u0000ce")]
    [InlineData("Alice‮evil")]
    [InlineData("Ali​ce")]
    [InlineData("Alice\u2028Bcc: x")]
    public void A_display_name_cannot_carry_a_line_break_or_an_invisible_character(string name)
    {
        Assert.False(SenderIdentity.TryCreate("alice@example.com", name, null, out _, out _));
    }

    [Fact]
    public void A_display_name_is_trimmed_capped_and_may_be_blank()
    {
        Assert.True(SenderIdentity.TryCreate("alice@example.com", "  Alice  ", null, out var named, out _));
        Assert.Equal("Alice", named.DisplayName);

        Assert.True(SenderIdentity.TryCreate("alice@example.com", "   ", null, out var blank, out _));
        Assert.Null(blank.DisplayName);

        Assert.True(SenderIdentity.TryCreate("alice@example.com", new string('x', SenderIdentity.MaxDisplayNameLength), null, out _, out _));
        Assert.False(SenderIdentity.TryCreate("alice@example.com", new string('x', SenderIdentity.MaxDisplayNameLength + 1), null, out _, out _));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("a@b.example\r\nX: y")]
    public void A_bad_reply_to_is_refused(string replyTo)
    {
        Assert.False(SenderIdentity.TryCreate("alice@example.com", null, replyTo, out _, out _));
    }
}
