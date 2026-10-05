using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Domain.Tags;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class AutoCategoryClassifierTests
{
    public static TheoryData<string?, string?, bool?, string?, AutoCategory?> Rows => new()
    {
        { "Alerts <notification@facebookmail.com>", null, true, "You have a new friend request", AutoCategory.Social },
        { "notifications@mail.linkedin.com", null, null, "Ada viewed your profile", AutoCategory.Social },
        { "no-reply@x.com", null, false, "Something", AutoCategory.Social },
        { "noreply@redditmail.com", null, true, "Top posts today", AutoCategory.Social },

        { "Writer <writer@substack.com>", null, true, "A post", AutoCategory.News },
        { "author@mail.beehiiv.com", null, null, "Issue 12", AutoCategory.News },
        { "Digest <digest@lists.example.net>", "Announcements <announce.lists.example.net>", true, "[announce] Weekly digest", AutoCategory.News },
        { "editor@example.com", "<news.example.com>", true, "Headlines", AutoCategory.News },
        { "editor@example.com", "<abc.substack.com>", true, "Headlines", AutoCategory.News },
        { "editor@example.com", null, true, "The Friday Newsletter", AutoCategory.News },

        { "news@mailchimp.com", null, null, "Anything", AutoCategory.Marketing },
        { "hello@e.klaviyo.com", null, true, "Anything", AutoCategory.Marketing },
        { "deals@shop.example.com", "<abc.list-manage.com>", true, "Welcome", AutoCategory.Marketing },
        { "deals@shop.example.com", null, true, "Flash sale: 20% off everything", AutoCategory.Marketing },
        { "deals@shop.example.com", null, true, "Save up to 40 percent", AutoCategory.Marketing },
        { "deals@shop.example.com", null, true, "FREE SHIPPING this weekend", AutoCategory.Marketing },
        { "deals@shop.example.com", null, true, "Newsletter: sale ends today", AutoCategory.Marketing },

        { "notifications@github.com", null, true, "[repo] Issue opened", AutoCategory.Notifications },
        { "someone@gitlab.com", null, null, "Pipeline failed", AutoCategory.Notifications },
        { "alerts@example.com", null, true, "Disk usage high", AutoCategory.Notifications },
        { "Notifications+abc@example.com", null, true, "Your order", AutoCategory.Notifications },
        { "notify-me@example.com", null, true, "Your order", AutoCategory.Notifications },

        { "noreply@example.com", null, true, "Your receipt", null },
        { "alice@example.com", null, false, "Hello", null },
        { "alice@example.com", null, null, "Hello", null },
        { "alice@example.com", null, false, "Flash sale: 20% off", null },
        { "alice@example.com", null, null, "Weekly digest of my thoughts", null },
        { "alice@example.com", null, false, "Newsletter draft", null },
        { "alerts@example.com", null, false, "Disk usage", null },
        { "alerts@example.com", null, null, "Disk usage", null },
        { "alice@example.com", "<dev.example.org>", true, "Re: patch", null },
        { null, null, null, null, null },
        { "", "", true, "", null },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Classifies_by_domain_then_by_hints_only_for_automated_mail(
        string? from, string? listId, bool? automated, string? subject, AutoCategory? expected) =>
        Assert.Equal(expected, AutoCategoryClassifier.Classify(from, listId, automated, subject));

    [Theory]
    [InlineData("a@evilfacebookmail.com")]
    [InlineData("a@facebookmail.com.attacker.example.org")]
    [InlineData("a@notfacebook.com")]
    [InlineData("a@xx.com")]
    [InlineData("a@mailchimp.com.example.net")]
    [InlineData("a@github.com.example.net")]
    [InlineData("a@-substack.com")]
    public void A_domain_must_match_on_a_label_boundary(string from) =>
        Assert.Null(AutoCategoryClassifier.Classify(from, null, false, "Hello"));

    [Theory]
    [InlineData("Alice <a@example.com>", null)]
    [InlineData("\"facebookmail.com <x@facebookmail.com>\" <a@example.com>", null)]
    [InlineData("\"a@facebookmail.com\" <b@example.com>", null)]
    [InlineData("a@example.com, b@facebookmail.com", null)]
    [InlineData("b@facebookmail.com, a@example.com", AutoCategory.Social)]
    [InlineData("A@FACEBOOKMAIL.COM", AutoCategory.Social)]
    [InlineData("<a@facebookmail.com.>", AutoCategory.Social)]
    public void Only_the_first_address_and_never_the_display_name_decides(string from, AutoCategory? expected) =>
        Assert.Equal(expected, AutoCategoryClassifier.Classify(from, null, false, "Hello"));

    [Theory]
    [InlineData("a@face book.com")]
    [InlineData("a@facebookmail.com>")]
    [InlineData("a@facebook\nmail.com")]
    [InlineData("<a@facebookmail.com")]
    [InlineData("\"unterminated <a@facebookmail.com>")]
    [InlineData("a@")]
    [InlineData("@facebookmail.com")]
    [InlineData("a@‮facebookmail.com")]
    [InlineData("a@facebookmail.com\u0000.example.org")]
    public void Malformed_senders_are_not_categorised_by_domain(string from) =>
        Assert.Null(AutoCategoryClassifier.Classify(from, null, false, "Hello"));

    [Theory]
    [InlineData("Wholesale prices")]
    [InlineData("Sales report")]
    [InlineData("Resale")]
    [InlineData("Digestive health")]
    [InlineData("Discounted")]
    [InlineData("100%offer")]
    public void Hints_need_word_boundaries(string subject) =>
        Assert.Null(AutoCategoryClassifier.Classify("a@example.com", null, true, subject));

    [Fact]
    public void A_list_id_token_needs_a_boundary_too()
    {
        Assert.Null(AutoCategoryClassifier.Classify("a@example.com", "<newsroom.example.com>", true, "Hello"));
        Assert.Null(AutoCategoryClassifier.Classify("a@example.com", "<digestible.example.com>", true, "Hello"));
        Assert.Equal(AutoCategory.News, AutoCategoryClassifier.Classify("a@example.com", "<linux-news.example.com>", true, "Hello"));
    }

    [Fact]
    public void A_hint_after_the_scanned_prefix_is_ignored_and_a_huge_subject_is_cheap()
    {
        var padding = new string('x', AutoCategoryClassifier.MaxSubjectChars);
        Assert.Null(AutoCategoryClassifier.Classify("a@example.com", null, true, padding + " sale"));

        var hostile = new string('s', 1_000_000) + "ale";
        Assert.Null(AutoCategoryClassifier.Classify("a@example.com", null, true, hostile));

        var overlapping = string.Concat(Enumerable.Repeat("sale", 100));
        Assert.Null(AutoCategoryClassifier.Classify("a@example.com", null, true, overlapping));
    }

    [Fact]
    public void A_huge_or_hostile_list_id_and_sender_never_throw()
    {
        var huge = "<" + new string('a', 100_000) + ">";
        Assert.Null(AutoCategoryClassifier.Classify("a@example.com", huge, true, "Hello"));
        Assert.Null(AutoCategoryClassifier.Classify("a@" + new string('b', 100_000) + ".facebookmail.com", null, true, "Hello"));
        Assert.Null(AutoCategoryClassifier.Classify("<<<>>>", "<<<>>>", true, "((("));
    }

    [Fact]
    public void Random_hostile_input_never_throws_and_only_yields_a_defined_category()
    {
        var random = new Random(20250129);
        const string Alphabet = "ab. @<>\",;:\\()%-_+=‮\u0000\n\tsalenewsletterdigestfacebookmail.com";

        string Next()
        {
            var length = random.Next(0, 80);
            var chars = new char[length];
            for (var i = 0; i < length; i++) chars[i] = Alphabet[random.Next(Alphabet.Length)];
            return new string(chars);
        }

        for (var i = 0; i < 4_000; i++)
        {
            bool? automated = random.Next(3) switch { 0 => true, 1 => false, _ => null };
            var category = AutoCategoryClassifier.Classify(Next(), Next(), automated, Next());
            if (category is { } value) Assert.True(Enum.IsDefined(value));
        }
    }

    [Fact]
    public void The_tag_names_are_fixed_and_reserved()
    {
        Assert.Equal(
            ["auto.social", "auto.news", "auto.marketing", "auto.notifications"],
            AutoCategoryTags.All.Select(t => t.Value));

        foreach (var category in Enum.GetValues<AutoCategory>())
        {
            var tag = AutoCategoryTags.TagFor(category);
            Assert.True(AutoCategoryTags.IsReserved(tag));
            Assert.True(TagFlagMap.IsLocalOnly(tag));
            Assert.Empty(TagFlagMap.ToKeywords([tag]));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => AutoCategoryTags.TagFor((AutoCategory)99));
    }
}
