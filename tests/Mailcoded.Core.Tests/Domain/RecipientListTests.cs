using Mailcoded.Core.Domain.Correspondents;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class RecipientListTests
{
    private static string[] Pairs(string? header) =>
        RecipientList.Parse(header).Select(a => (a.DisplayName ?? "-") + "|" + a.Address.Value).ToArray();

    [Fact]
    public void Plain_and_named_addresses_are_read_in_order()
    {
        Assert.Equal(
            ["Ada Example|ada@example.com", "-|bob@example.org", "Cy|cy@example.net"],
            Pairs("Ada Example <ada@example.com>, bob@example.org, \"Cy\" <cy@example.net>"));
    }

    [Fact]
    public void A_quoted_name_may_hold_commas_and_escaped_quotes()
    {
        Assert.Equal(
            ["Example, Ada|ada@example.com", "Say \"hi\"|bob@example.org"],
            Pairs("\"Example, Ada\" <ada@example.com>, \"Say \\\"hi\\\"\" <bob@example.org>"));
    }

    [Fact]
    public void A_name_holding_angle_brackets_is_not_kept_but_its_address_is()
    {
        Assert.Equal(["-|bob@example.org"], Pairs("\"Say <x>\" <bob@example.org>"));
    }

    [Fact]
    public void A_comma_inside_a_quoted_local_part_never_splits_the_entry()
    {
        var found = RecipientList.Parse("<\"a,b\"@example.com>, c@example.org");

        Assert.Equal(["c@example.org"], found.Select(a => a.Address.Value));
    }

    [Fact]
    public void Comments_are_dropped_and_do_not_become_names()
    {
        Assert.Equal(
            ["Ada|ada@example.com", "-|bob@example.org"],
            Pairs("Ada (the first one) <ada@example.com>, bob@example.org (Bob, the second)"));
    }

    [Fact]
    public void A_group_yields_its_members_and_never_the_group_name()
    {
        Assert.Equal(
            ["-|a@example.com", "B|b@example.org", "-|c@example.net"],
            Pairs("Team: a@example.com, B <b@example.org>;, c@example.net"));
    }

    [Fact]
    public void A_colon_after_the_at_sign_is_not_a_group_start()
    {
        Assert.Empty(RecipientList.Parse("a@example.com:b"));
    }

    [Fact]
    public void An_empty_group_and_an_empty_header_yield_nothing()
    {
        Assert.Empty(RecipientList.Parse("undisclosed-recipients:;"));
        Assert.Empty(RecipientList.Parse(null));
        Assert.Empty(RecipientList.Parse(""));
        Assert.Empty(RecipientList.Parse("   "));
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("a@b")]
    [InlineData("@example.com")]
    [InlineData("a@@example.com")]
    [InlineData("<>")]
    [InlineData("Name <>")]
    [InlineData("a b@example.com")]
    [InlineData("<a@example.com")]
    [InlineData("a@example.com>")]
    [InlineData("\"unterminated <a@example.com>")]
    [InlineData("Name (unterminated comment <a@example.com>")]
    [InlineData("<<a@example.com>>")]
    [InlineData("A <a@example.com> <b@example.com>")]
    [InlineData("A <a@example.com> trailing words")]
    public void Anything_that_is_not_one_whole_valid_address_is_dropped(string header)
    {
        Assert.Empty(RecipientList.Parse(header));
    }

    [Fact]
    public void A_bad_entry_does_not_hide_the_good_ones_around_it()
    {
        Assert.Equal(
            ["-|a@example.com", "-|c@example.org"],
            Pairs("a@example.com, garbage, c@example.org"));
    }

    [Theory]
    [InlineData("a@example.com\r\nBcc: spy@example.org")]
    [InlineData("a@example.com\nBcc: spy@example.org")]
    public void Header_injection_never_produces_the_injected_address(string header)
    {
        var found = RecipientList.Parse(header);

        Assert.DoesNotContain(found, a => a.Address.Value.Contains("spy", StringComparison.Ordinal));
        Assert.DoesNotContain(found, a => a.Address.Value.Contains('\n') || a.Address.Value.Contains('\r'));
    }

    [Fact]
    public void A_name_is_made_safe_to_show()
    {
        var found = RecipientList.Parse("\"Ada‮\u0007\r\nEvil​  Name\" <ada@example.com>");

        var name = Assert.Single(found).DisplayName;
        Assert.Equal("Ada Evil Name", name);
    }

    [Theory]
    [InlineData("\"ada@example.com\" <ada@example.com>")]
    [InlineData("\"Ada <x@y.z>\" <ada@example.com>")]
    [InlineData("\"Boss @ Corp\" <ada@example.com>")]
    public void A_name_that_looks_like_an_address_is_not_kept_but_the_address_is(string header)
    {
        var entry = Assert.Single(RecipientList.Parse(header));

        Assert.Equal("ada@example.com", entry.Address.Value);
        Assert.Null(entry.DisplayName);
    }

    [Fact]
    public void An_overlong_name_is_dropped_not_cut()
    {
        var name = new string('n', RecipientList.MaxDisplayNameLength + 1);

        Assert.Null(Assert.Single(RecipientList.Parse($"\"{name}\" <a@example.com>")).DisplayName);
        Assert.Equal(
            new string('n', RecipientList.MaxDisplayNameLength),
            Assert.Single(RecipientList.Parse($"\"{new string('n', RecipientList.MaxDisplayNameLength)}\" <a@example.com>")).DisplayName);
    }

    [Fact]
    public void A_name_made_only_of_invisible_characters_is_no_name()
    {
        Assert.Null(Assert.Single(RecipientList.Parse("\"​‮ \" <a@example.com>")).DisplayName);
    }

    [Fact]
    public void Entries_are_capped_so_one_header_cannot_flood_the_caller()
    {
        var header = string.Join(", ", Enumerable.Range(0, 5000).Select(i => $"u{i}@example.com"));

        Assert.Equal(RecipientList.MaxEntries, RecipientList.Parse(header).Count);
    }

    [Fact]
    public void An_enormous_header_is_read_only_up_to_the_cap_and_finishes()
    {
        var header = "a@example.com, " + new string('x', 10 * 1024 * 1024) + ", z@example.com";

        var found = RecipientList.Parse(header);

        Assert.Equal(["a@example.com"], found.Select(a => a.Address.Value));
    }

    [Fact]
    public void Deeply_nested_comments_and_angles_terminate_without_a_result()
    {
        var comments = new string('(', 50_000) + "a@example.com" + new string(')', 50_000);
        var angles = new string('<', 50_000) + "a@example.com" + new string('>', 50_000);

        Assert.Empty(RecipientList.Parse(comments));
        Assert.Empty(RecipientList.Parse(angles));
    }

    [Fact]
    public void A_backslash_at_the_end_of_input_is_survivable()
    {
        Assert.Empty(RecipientList.Parse("\"abc\\"));
        Assert.Empty(RecipientList.Parse("(abc\\"));
    }

    [Fact]
    public void The_domain_is_lower_cased_and_the_local_part_is_kept()
    {
        Assert.Equal("Ada@example.com", Assert.Single(RecipientList.Parse("Ada@EXAMPLE.com")).Address.Value);
    }

    [Fact]
    public void Non_ascii_names_survive()
    {
        Assert.Equal("Zoë Müller", Assert.Single(RecipientList.Parse("Zoë Müller <zoe@example.com>")).DisplayName);
    }
}
