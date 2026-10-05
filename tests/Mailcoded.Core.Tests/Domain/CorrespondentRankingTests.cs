using Mailcoded.Core.Domain.Correspondents;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class CorrespondentRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static Correspondent Row(string address, int count, int daysAgo, string? name = null) => new()
    {
        Address = address,
        DisplayName = name,
        Count = count,
        LastUsedUtc = Now.AddDays(-daysAgo),
    };

    private static string[] Order(IEnumerable<Correspondent> rows, string? prefix = null, int limit = 10) =>
        CorrespondentRanking.Suggest(rows, prefix, limit, Now).Select(r => r.Address).ToArray();

    [Fact]
    public void More_uses_score_higher_at_the_same_age_and_with_diminishing_returns()
    {
        var one = CorrespondentRanking.Score(1, Now, Now);
        var two = CorrespondentRanking.Score(2, Now, Now);
        var hundred = CorrespondentRanking.Score(100, Now, Now);

        Assert.True(two > one);
        Assert.True(hundred > two);
        Assert.True(hundred < 100 * one);
    }

    [Fact]
    public void A_recent_use_scores_higher_than_an_old_one_and_decay_stops_at_a_floor()
    {
        var today = CorrespondentRanking.Score(5, Now, Now);
        var quarter = CorrespondentRanking.Score(5, Now.AddDays(-90), Now);
        var ancient = CorrespondentRanking.Score(5, Now.AddDays(-5000), Now);
        var older = CorrespondentRanking.Score(5, Now.AddDays(-9000), Now);

        Assert.True(today > quarter);
        Assert.True(quarter > ancient);
        Assert.True(ancient > 0);
        Assert.Equal(ancient, older, precision: 6);
    }

    [Fact]
    public void A_use_dated_in_the_future_is_treated_as_now_not_better_than_now()
    {
        Assert.Equal(
            CorrespondentRanking.Score(3, Now, Now),
            CorrespondentRanking.Score(3, Now.AddDays(400), Now),
            precision: 9);
    }

    [Fact]
    public void A_long_standing_contact_outranks_one_stray_recent_name()
    {
        var rows = new[] { Row("stray@example.org", 1, 1), Row("steady@example.org", 200, 120) };

        Assert.Equal(["steady@example.org", "stray@example.org"], Order(rows));
    }

    [Fact]
    public void A_recent_contact_outranks_the_same_count_used_long_ago()
    {
        var rows = new[] { Row("old@example.org", 10, 700), Row("new@example.org", 10, 3) };

        Assert.Equal(["new@example.org", "old@example.org"], Order(rows));
    }

    [Fact]
    public void An_exact_address_match_comes_first_whatever_the_score()
    {
        var rows = new[] { Row("ada@example.com.au", 500, 1), Row("ada@example.com", 1, 900) };

        Assert.Equal(["ada@example.com", "ada@example.com.au"], Order(rows, "ADA@example.com"));
    }

    [Theory]
    [InlineData("ad", true)]
    [InlineData("AD", true)]
    [InlineData("ada@", true)]
    [InlineData("lov", true)]
    [InlineData("lovelace", true)]
    [InlineData("ada l", true)]
    [InlineData("a.l", false)]
    [InlineData("velace", false)]
    [InlineData("example", false)]
    [InlineData("bob", false)]
    public void A_prefix_matches_the_address_the_whole_name_or_the_start_of_any_name_word(string prefix, bool expected)
    {
        var row = Row("ada@example.com", 1, 1, "Ada Lovelace-King");

        Assert.Equal(expected, CorrespondentRanking.Matches(row, CorrespondentRanking.NormalizePrefix(prefix)));
    }

    [Fact]
    public void A_word_after_punctuation_is_a_word()
    {
        var row = Row("x@example.com", 1, 1, "Smith, Jo (Acme)");

        Assert.True(CorrespondentRanking.Matches(row, "acme"));
        Assert.True(CorrespondentRanking.Matches(row, "jo"));
    }

    [Fact]
    public void Matching_folds_case_for_non_ascii_names()
    {
        Assert.True(CorrespondentRanking.Matches(Row("z@example.com", 1, 1, "Émile Zola"), "émi"));
        Assert.True(CorrespondentRanking.Matches(Row("z@example.com", 1, 1, "émile Zola"), "ÉMI"));
    }

    [Fact]
    public void An_empty_or_blank_prefix_lists_everyone_best_first()
    {
        var rows = new[] { Row("c@example.org", 1, 10), Row("a@example.org", 9, 10), Row("b@example.org", 3, 10) };

        Assert.Equal(["a@example.org", "b@example.org", "c@example.org"], Order(rows, null));
        Assert.Equal(["a@example.org", "b@example.org", "c@example.org"], Order(rows, "   "));
    }

    [Fact]
    public void The_limit_cuts_the_list_and_zero_returns_nothing()
    {
        var rows = Enumerable.Range(1, 30).Select(i => Row($"u{i:00}@example.org", i, 5)).ToArray();

        Assert.Equal(5, Order(rows, null, 5).Length);
        Assert.Equal("u30@example.org", Order(rows, null, 5)[0]);
        Assert.Empty(Order(rows, null, 0));
        Assert.Empty(Order(rows, null, -4));
    }

    [Fact]
    public void Ties_fall_to_count_then_recency_then_address_so_the_order_never_wobbles()
    {
        var rows = new[]
        {
            Row("b@example.org", 2, 400),
            Row("a@example.org", 2, 400),
            Row("c@example.org", 2, 400),
        };

        Assert.Equal(["a@example.org", "b@example.org", "c@example.org"], Order(rows));
        Assert.Equal(["a@example.org", "b@example.org", "c@example.org"], Order(rows.Reverse()));
    }

    [Fact]
    public void A_prefix_beyond_the_maximum_length_is_cut_and_never_throws()
    {
        var huge = new string('a', 10_000);

        Assert.Equal(CorrespondentRanking.MaxPrefixLength, CorrespondentRanking.NormalizePrefix(huge).Length);
        Assert.Empty(Order([Row("ada@example.com", 1, 1)], huge));
    }

    [Fact]
    public void A_prefix_with_control_characters_matches_nothing_it_should_not()
    {
        Assert.Empty(Order([Row("ada@example.com", 1, 1, "Ada")], "ad\0\r\n"));
    }

    [Fact]
    public void A_count_of_zero_or_less_does_not_break_the_score()
    {
        Assert.True(double.IsFinite(CorrespondentRanking.Score(0, Now, Now)));
        Assert.True(double.IsFinite(CorrespondentRanking.Score(-5, Now, Now)));
    }
}
