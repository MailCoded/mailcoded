using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class AutoCategoryTagFlagTests
{
    private static readonly Tag News = Tag.Parse("auto.news");
    private static readonly Tag Work = Tag.Parse("work");

    [Fact]
    public void Category_tags_are_local_only_and_never_become_keywords()
    {
        Assert.True(TagFlagMap.IsLocalOnly(News));
        Assert.True(TagFlagMap.IsLocalOnly(Tag.Parse("auto.anything")));
        Assert.False(TagFlagMap.IsLocalOnly(Tag.Parse("autonomous")));
        Assert.False(TagFlagMap.IsLocalOnly(Tag.Parse("auto")));

        Assert.Equal(["work"], TagFlagMap.ToKeywords([News, Work]));
    }

    [Fact]
    public void Adding_or_removing_a_category_tag_pushes_nothing()
    {
        var add = TagFlagMap.Project(new TagDelta { Add = [News, Work] }, serverAcceptsCustomKeywords: true);
        Assert.Equal(["work"], add.AddKeywords);

        var remove = TagFlagMap.Project(new TagDelta { Remove = [News] }, serverAcceptsCustomKeywords: true);
        Assert.True(remove.IsEmpty);
    }

    [Fact]
    public void A_server_keyword_with_the_reserved_prefix_never_becomes_a_tag_on_a_known_message()
    {
        var merge = TagFlagMap.Merge([Work], MessageFlags.None, MessageFlags.None, ["work", "auto.news", "AUTO.SOCIAL"]);

        Assert.Equal([Work], merge.CustomTags);
        Assert.True(merge.Push.IsEmpty);
        Assert.False(merge.TagsChanged);
    }

    [Fact]
    public void A_server_keyword_with_the_reserved_prefix_is_ignored_on_first_observation()
    {
        var merge = TagFlagMap.Merge(new TagMergeInput
        {
            LocalTags = [],
            LocalTagsKnown = false,
            ServerKeywords = ["work", "auto.news"],
        });

        Assert.Equal([Work], merge.CustomTags);
    }

    [Fact]
    public void First_observation_keeps_the_category_tag_the_ingest_assigned_and_still_takes_server_keywords()
    {
        var merge = TagFlagMap.Merge(new TagMergeInput
        {
            LocalTags = [News, Work],
            LocalTagsKnown = false,
            ServerKeywords = ["billing"],
        });

        Assert.Equal([Tag.Parse("auto.news"), Tag.Parse("billing")], merge.CustomTags);
        Assert.True(merge.Push.IsEmpty);
    }

    [Fact]
    public void A_known_message_that_lost_its_category_tag_does_not_get_it_back_from_a_merge()
    {
        var merge = TagFlagMap.Merge([Work], MessageFlags.None, MessageFlags.None, ["work"]);

        Assert.DoesNotContain(News, merge.CustomTags);
    }

    [Fact]
    public void A_category_tag_survives_a_server_that_drops_custom_keywords()
    {
        var merge = TagFlagMap.Merge([News, Work], MessageFlags.None, MessageFlags.None, [], serverAcceptsCustomKeywords: false);

        Assert.Equal([News, Work], merge.CustomTags);
        Assert.True(merge.Push.IsEmpty);
    }

    [Fact]
    public void A_local_category_tag_is_not_pushed_when_the_server_accepts_keywords()
    {
        var merge = TagFlagMap.Merge([News, Work], MessageFlags.None, MessageFlags.None, []);

        Assert.Equal(["work"], merge.Push.AddKeywords);
        Assert.Contains(News, merge.CustomTags);
    }
}
