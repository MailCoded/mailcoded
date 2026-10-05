using Mailcoded.Core.Domain.Correspondents;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class CorrespondentLearningTests
{
    private static readonly IReadOnlySet<string> Own = CorrespondentLearning.OwnKeys("me@example.com", []);

    private static NamedAddress Named(string address, string? name = null) => new(EmailAddress.Parse(address), name);

    private static string[] Keys(IReadOnlyList<NamedAddress> chosen) => chosen.Select(a => a.Key).ToArray();

    [Fact]
    public void To_then_Cc_in_order_and_nothing_else()
    {
        var chosen = CorrespondentLearning.Select(
            [Named("a@example.org", "A")],
            [Named("b@example.net")],
            Own);

        Assert.Equal(["a@example.org", "b@example.net"], Keys(chosen));
        Assert.Equal("A", chosen[0].DisplayName);
    }

    [Fact]
    public void Own_addresses_and_identities_are_never_learned()
    {
        Assert.True(SenderIdentity.TryCreate("Sales@Example.com", null, null, out var identity, out var error), error);
        var own = CorrespondentLearning.OwnKeys("Me@Example.com", [identity]);

        var chosen = CorrespondentLearning.Select(
            [Named("me@example.com"), Named("sales@example.com"), Named("friend@example.org")],
            [],
            own);

        Assert.Equal(["friend@example.org"], Keys(chosen));
    }

    [Theory]
    [InlineData("noreply@example.org")]
    [InlineData("No-Reply@example.org")]
    [InlineData("do-not-reply@example.org")]
    [InlineData("mailer-daemon@example.org")]
    [InlineData("postmaster@example.org")]
    [InlineData("bounces+abc123@example.org")]
    [InlineData("noreply-billing@example.org")]
    public void Robot_addresses_are_skipped(string robot)
    {
        var chosen = CorrespondentLearning.Select([Named(robot), Named("person@example.org")], [], Own);

        Assert.Equal(["person@example.org"], Keys(chosen));
    }

    [Theory]
    [InlineData("noreplyfan@example.org")]
    [InlineData("bouncer@example.org")]
    public void A_name_that_merely_starts_like_a_robot_is_kept(string address)
    {
        Assert.Equal([address], Keys(CorrespondentLearning.Select([Named(address)], [], Own)));
    }

    [Fact]
    public void A_repeated_address_counts_once_and_keeps_the_first_name_found()
    {
        var chosen = CorrespondentLearning.Select(
            [Named("A@example.org"), Named("a@EXAMPLE.org", "Late Name")],
            [Named("a@example.org", "Other")],
            Own);

        var only = Assert.Single(chosen);
        Assert.Equal("a@example.org", only.Key);
        Assert.Equal("Late Name", only.DisplayName);
    }

    [Fact]
    public void A_message_to_more_than_the_cap_teaches_nothing_and_exactly_the_cap_teaches_everyone()
    {
        var cap = CorrespondentLearning.MaxRecipientsPerMessage;
        var exactly = Enumerable.Range(0, cap).Select(i => Named($"u{i}@example.org")).ToArray();
        var over = Enumerable.Range(0, cap + 1).Select(i => Named($"u{i}@example.org")).ToArray();

        Assert.Equal(cap, CorrespondentLearning.Select(exactly, [], Own).Count);
        Assert.Empty(CorrespondentLearning.Select(over, [], Own));
        Assert.Empty(CorrespondentLearning.Select(exactly, [Named("one-more@example.org")], Own));
    }

    [Fact]
    public void The_cap_counts_distinct_addresses_so_repeats_cannot_trip_it()
    {
        var many = Enumerable.Range(0, 100).Select(_ => Named("same@example.org")).ToArray();

        Assert.Single(CorrespondentLearning.Select(many, [], Own));
    }

    [Fact]
    public void Own_and_robot_addresses_still_count_toward_the_broadcast_cap()
    {
        var cap = CorrespondentLearning.MaxRecipientsPerMessage;
        var to = Enumerable.Range(0, cap).Select(i => Named($"u{i}@example.org")).ToList();
        to.Add(Named("me@example.com"));

        Assert.Empty(CorrespondentLearning.Select(to, [], Own));
    }

    [Fact]
    public void Authorship_requires_one_of_the_own_addresses_in_From()
    {
        Assert.True(CorrespondentLearning.IsAuthoredBy([Named("ME@example.com")], Own));
        Assert.True(CorrespondentLearning.IsAuthoredBy([Named("x@example.org"), Named("me@example.com")], Own));
        Assert.False(CorrespondentLearning.IsAuthoredBy([Named("x@example.org")], Own));
        Assert.False(CorrespondentLearning.IsAuthoredBy([], Own));
    }

    [Fact]
    public void Keys_are_the_whole_address_lower_cased()
    {
        Assert.Equal("ada.x+tag@example.com", Named("Ada.X+Tag@EXAMPLE.com").Key);
    }
}
