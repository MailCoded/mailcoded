using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>Tag/Flag mapping in both directions plus CLAUDE invariant 9 as an explicit table.</summary>
public sealed class TagFlagMapTests
{
    [Fact]
    public void MessageFlags_bit_zero_is_unread_and_is_the_inverse_of_the_seen_flag()
    {
        Assert.Equal(1, (int)MessageFlags.Unread);

        Assert.DoesNotContain("Seen", Enum.GetNames<MessageFlags>());

        Assert.True(
            TagFlagMap.SystemTagsOf(MessageFlags.None).Count == 0,
            "A zero bitfield must read as 'seen, nothing else'. If bit 0 ever becomes \\Seen instead of Unread, "
            + "every stored row silently flips its read state and the (flags & 1) = 1 unread predicate inverts.");
    }

    [Fact]
    public void Adding_the_unread_tag_clears_seen_and_removing_it_sets_seen()
    {
        var (setForUnread, clearForUnread) = TagFlagMap.ToServerFlagNames(new FlagDelta { Add = MessageFlags.Unread });
        var (setForRead, clearForRead) = TagFlagMap.ToServerFlagNames(new FlagDelta { Remove = MessageFlags.Unread });

        Assert.Contains(TagFlagMap.SeenFlag, clearForUnread);
        Assert.DoesNotContain(TagFlagMap.SeenFlag, setForUnread);

        Assert.Contains(TagFlagMap.SeenFlag, setForRead);
        Assert.DoesNotContain(TagFlagMap.SeenFlag, clearForRead);
    }

    [Fact]
    public void Every_other_system_flag_maps_straight_through_without_inversion()
    {
        var (set, clear) = TagFlagMap.ToServerFlagNames(new FlagDelta
        {
            Add = MessageFlags.Flagged | MessageFlags.Draft,
            Remove = MessageFlags.Answered | MessageFlags.Deleted,
        });

        Assert.Contains(TagFlagMap.FlaggedFlag, set);
        Assert.Contains(TagFlagMap.DraftFlag, set);
        Assert.Contains(TagFlagMap.AnsweredFlag, clear);
        Assert.Contains(TagFlagMap.DeletedFlag, clear);
    }

    [Fact]
    public void Keywords_ride_along_with_the_flag_names()
    {
        var (set, clear) = TagFlagMap.ToServerFlagNames(new FlagDelta
        {
            AddKeywords = ["project-x"],
            RemoveKeywords = ["old"],
        });

        Assert.Contains("project-x", set);
        Assert.Contains("old", clear);
    }

    [Fact]
    public void Flags_become_the_system_tags_they_stand_for()
    {
        var tags = TagFlagMap.ToTags(
            MessageFlags.Unread | MessageFlags.Flagged | MessageFlags.Answered | MessageFlags.Draft,
            null);

        Assert.Equal(
            new[] { Tag.Draft, Tag.Flagged, Tag.Replied, Tag.Unread },
            tags.OrderBy(t => t.Value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Unmapped_flags_produce_no_tag()
    {
        Assert.Empty(TagFlagMap.ToTags(MessageFlags.Deleted | MessageFlags.Recent, null));
    }

    [Fact]
    public void Server_keywords_become_tags_but_system_flag_names_never_do()
    {
        var tags = TagFlagMap.ToTags(MessageFlags.Unread, ["Work", "\\Seen", "unread", "  ", "bad tag"]);

        Assert.Equal(new[] { Tag.Parse("unread"), Tag.Parse("work") }, tags.ToArray());
    }

    [Fact]
    public void Tags_become_the_flags_they_stand_for_and_custom_tags_contribute_nothing()
    {
        var flags = TagFlagMap.ToFlags([Tag.Unread, Tag.Replied, Tag.Parse("work"), Tag.Inbox]);

        Assert.Equal(MessageFlags.Unread | MessageFlags.Answered, flags);
    }

    [Fact]
    public void Custom_keywords_exclude_system_and_local_only_tags_and_come_back_sorted()
    {
        var keywords = TagFlagMap.ToKeywords([Tag.Parse("zebra"), Tag.Unread, Tag.Inbox, Tag.Parse("alpha")]);

        Assert.Equal(new[] { "alpha", "zebra" }, keywords.ToArray());
    }

    [Fact]
    public void The_flag_to_tag_to_flag_round_trip_is_the_identity_for_system_tags()
    {
        var random = new Random(12345);

        for (var i = 0; i < 1_000; i++)
        {
            var flags = (MessageFlags)random.Next(0, 64);

            var tags = TagFlagMap.ToTags(flags, null);
            var back = TagFlagMap.ToFlags(tags);

            Assert.True(
                back == (flags & TagFlagMap.MappedFlags),
                $"Round trip lost or invented a flag for {flags}: got {back}, expected {flags & TagFlagMap.MappedFlags}. "
                + "Only the four mapped bits round trip; \\Deleted and \\Recent are folder and session state and "
                + "deliberately have no tag.");
        }
    }

    [Fact]
    public void Custom_keywords_never_leak_into_the_flag_bitfield()
    {
        var random = new Random(12345);
        string[] pool = ["work", "urgent", "project-x", "$MDNSent", "receipts", "todo"];

        for (var i = 0; i < 1_000; i++)
        {
            var flags = (MessageFlags)random.Next(0, 64);

            var keywords = new List<string>();
            for (var k = 0; k < pool.Length; k++)
                if (random.Next(2) == 1) keywords.Add(pool[k]);

            var back = TagFlagMap.ToFlags(TagFlagMap.ToTags(flags, keywords));

            Assert.Equal(flags & TagFlagMap.MappedFlags, back);
        }
    }

    [Fact]
    public void A_keyword_is_recognised_only_when_it_is_not_a_system_flag()
    {
        Assert.True(TagFlagMap.TryTagForKeyword("Work", out var work));
        Assert.Equal("work", work.Value);

        Assert.False(TagFlagMap.TryTagForKeyword("\\Seen", out _));
        Assert.False(TagFlagMap.TryTagForKeyword("\\Deleted", out _));
        Assert.False(TagFlagMap.TryTagForKeyword("unread", out _));
        Assert.False(TagFlagMap.TryTagForKeyword("flagged", out _));
        Assert.False(TagFlagMap.TryTagForKeyword("replied", out _));
        Assert.False(TagFlagMap.TryTagForKeyword("draft", out _));
        Assert.False(TagFlagMap.TryTagForKeyword("bad tag", out _));
        Assert.False(TagFlagMap.TryTagForKeyword("   ", out _));
        Assert.False(TagFlagMap.TryTagForKeyword(null, out _));
    }

    [Fact]
    public void The_inbox_tag_is_local_only()
    {
        Assert.True(TagFlagMap.IsLocalOnly(Tag.Inbox));
        Assert.False(TagFlagMap.IsLocalOnly(Tag.Parse("work")));
        Assert.Contains(Tag.Inbox, TagFlagMap.LocalOnlyTags);
    }

    public static TheoryData<string> ProjectRows
    {
        get
        {
            var rows = new TheoryData<string>();
            foreach (var row in ProjectMatrix) rows.Add(row.Name);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(ProjectRows))]
    public void Project_turns_a_tag_request_into_a_server_flag_delta(string name)
    {
        var row = ProjectMatrix.Single(r => string.Equals(r.Name, name, StringComparison.Ordinal));

        var delta = TagFlagMap.Project(row.Delta, row.ServerAcceptsCustomKeywords);

        Assert.True(row.Matches(delta), $"[{row.Name}] {row.Rule}{Environment.NewLine}Projected: {Describe(delta)}");
    }

    public static TheoryData<string> MergeRows
    {
        get
        {
            var rows = new TheoryData<string>();
            foreach (var row in MergeMatrix) rows.Add(row.Name);
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(MergeRows))]
    public void Merge_resolves_a_flag_and_tag_conflict(string name)
    {
        var row = MergeMatrix.Single(r => string.Equals(r.Name, name, StringComparison.Ordinal));

        var result = TagFlagMap.Merge(row.Input);

        Assert.True(
            row.Matches(result),
            $"[{row.Name}] {row.Rule}{Environment.NewLine}Merged: flags={result.Flags} tags=[{Join(result.Tags)}] "
            + $"custom=[{Join(result.CustomTags)}] push={Describe(result.Push)} "
            + $"flagsChanged={result.FlagsChanged} tagsChanged={result.TagsChanged}");
    }

    [Fact]
    public void A_merge_never_pushes_a_system_flag_back_to_the_server()
    {
        foreach (var row in MergeMatrix)
        {
            var result = TagFlagMap.Merge(row.Input);

            Assert.True(
                result.Push.Add == MessageFlags.None && result.Push.Remove == MessageFlags.None,
                $"[{row.Name}] CLAUDE invariant 9: the server wins on flags, so a merge has nothing to push back "
                + "about them. Only custom keywords can ever appear in the push delta.");
        }
    }

    [Fact]
    public void A_server_without_permanentflags_star_receives_no_keywords_at_all()
    {
        var result = TagFlagMap.Merge(new TagMergeInput
        {
            LocalTags = [Tag.Parse("work"), Tag.Parse("urgent")],
            LocalFlags = MessageFlags.Unread,
            ServerFlags = MessageFlags.None,
            ServerKeywords = ["stale"],
            ServerAcceptsCustomKeywords = false,
        });

        Assert.True(
            result.Push.IsEmpty,
            "§14.5 case 22: PERMANENTFLAGS without \\* means keywords do not persist. Emitting a keyword delta here "
            + "makes every sync re-push the same keywords forever, which is the loop the edge case exists to stop.");
        Assert.Empty(result.Push.AddKeywords);
        Assert.Empty(result.Push.RemoveKeywords);
        Assert.Equal(new[] { Tag.Parse("urgent"), Tag.Parse("work") }, result.CustomTags.ToArray());
    }

    [Fact]
    public void A_projection_for_a_server_without_permanentflags_star_still_carries_the_system_flags()
    {
        var delta = TagFlagMap.Project(
            new TagDelta { Add = [Tag.Flagged, Tag.Parse("work")] },
            serverAcceptsCustomKeywords: false);

        Assert.Equal(MessageFlags.Flagged, delta.Add);
        Assert.Empty(delta.AddKeywords);
        Assert.False(delta.IsEmpty);
    }

    [Fact]
    public void Applying_a_tag_delta_lets_remove_win()
    {
        var result = TagFlagMap.Apply(
            [Tag.Parse("alpha"), Tag.Parse("beta")],
            new TagDelta { Add = [Tag.Parse("gamma"), Tag.Parse("beta")], Remove = [Tag.Parse("alpha"), Tag.Parse("beta")] });

        Assert.Equal(new[] { Tag.Parse("gamma") }, result.ToArray());
    }

    [Fact]
    public void An_empty_delta_is_empty_in_both_directions()
    {
        Assert.True(FlagDelta.Empty.IsEmpty);
        Assert.True(TagDelta.Empty.IsEmpty);
        Assert.True(TagFlagMap.Project(TagDelta.Empty, serverAcceptsCustomKeywords: true).IsEmpty);
    }

    [Fact]
    public void The_mapping_rejects_missing_inputs_rather_than_guessing()
    {
        Assert.Throws<ArgumentNullException>(() => TagFlagMap.ToFlags(null!));
        Assert.Throws<ArgumentNullException>(() => TagFlagMap.ToKeywords(null!));
        Assert.Throws<ArgumentNullException>(() => TagFlagMap.Project(null!, true));
        Assert.Throws<ArgumentNullException>(() => TagFlagMap.Apply(null!, TagDelta.Empty));
        Assert.Throws<ArgumentNullException>(() => TagFlagMap.Apply([], null!));
        Assert.Throws<ArgumentNullException>(() => TagFlagMap.Merge((TagMergeInput)null!));
        Assert.Throws<ArgumentNullException>(() => TagFlagMap.Merge(null!, MessageFlags.None, MessageFlags.None, []));
        Assert.Throws<ArgumentNullException>(() => TagFlagMap.Merge([], MessageFlags.None, MessageFlags.None, null!));
    }

    private sealed record ProjectCase(
        string Name,
        string Rule,
        TagDelta Delta,
        bool ServerAcceptsCustomKeywords,
        Func<FlagDelta, bool> Matches);

    private static readonly ProjectCase[] ProjectMatrix =
    [
        new ProjectCase(
            "system/flagged",
            "A system tag becomes the flag bit it stands for, never a keyword.",
            new TagDelta { Add = [Tag.Flagged] },
            true,
            d => d.Add == MessageFlags.Flagged && d.AddKeywords.Count == 0),

        new ProjectCase(
            "system/unread-adds-the-bit-not-the-seen-flag",
            "Unread is bit 0; turning it into the \\Seen wire name happens later, in ToServerFlagNames.",
            new TagDelta { Add = [Tag.Unread] },
            true,
            d => d.Add == MessageFlags.Unread && d.Remove == MessageFlags.None),

        new ProjectCase(
            "custom/keyword-is-pushed",
            "A custom tag becomes a lower-cased IMAP keyword.",
            new TagDelta { Add = [Tag.Parse("Work")] },
            true,
            d => d.AddKeywords.Count == 1 && d.AddKeywords[0] == "work"),

        new ProjectCase(
            "custom/keyword-is-dropped-without-permanentflags-star",
            "§14.5 case 22: a server that does not persist keywords must receive none.",
            new TagDelta { Add = [Tag.Parse("work")] },
            false,
            d => d.IsEmpty),

        new ProjectCase(
            "custom/local-only-tags-are-never-pushed",
            "'inbox' describes local structure; pushing it as a keyword would litter the server.",
            new TagDelta { Add = [Tag.Inbox] },
            true,
            d => d.IsEmpty),

        new ProjectCase(
            "conflict/remove-wins-for-a-keyword",
            "A tag named in both lists resolves to Remove: the destructive reading is the safe one.",
            new TagDelta { Add = [Tag.Parse("work")], Remove = [Tag.Parse("work")] },
            true,
            d => d.AddKeywords.Count == 0 && d.RemoveKeywords.Count == 1 && d.RemoveKeywords[0] == "work"),

        new ProjectCase(
            "conflict/remove-wins-for-a-system-flag",
            "The same rule applies to the bitfield: the flag ends up only in Remove.",
            new TagDelta { Add = [Tag.Unread], Remove = [Tag.Unread] },
            true,
            d => d.Add == MessageFlags.None && d.Remove == MessageFlags.Unread),

        new ProjectCase(
            "shape/duplicates-collapse-and-keywords-sort",
            "The delta is a set in a stable order, so an identical request always produces identical IMAP traffic.",
            new TagDelta { Add = [Tag.Parse("zebra"), Tag.Parse("alpha"), Tag.Parse("zebra")] },
            true,
            d => d.AddKeywords.Count == 2 && d.AddKeywords[0] == "alpha" && d.AddKeywords[1] == "zebra"),
    ];

    private sealed record MergeCase(string Name, string Rule, TagMergeInput Input, Func<TagMergeResult, bool> Matches);

    private static readonly MergeCase[] MergeMatrix =
    [
        new MergeCase(
            "flags/server-wins-when-it-marked-the-message-read",
            "CLAUDE invariant 9: the server wins on IMAP flags. We read it as unread offline; the server says seen; "
            + "the server's answer is the resolved one.",
            new TagMergeInput { LocalFlags = MessageFlags.Unread, ServerFlags = MessageFlags.None },
            r => r.Flags == MessageFlags.None && r.FlagsChanged && r.Tags.Count == 0),

        new MergeCase(
            "flags/server-wins-when-it-flagged-the-message",
            "CLAUDE invariant 9: the same rule in the other direction — a flag the server added appears locally.",
            new TagMergeInput { LocalFlags = MessageFlags.None, ServerFlags = MessageFlags.Flagged },
            r => r.Flags == MessageFlags.Flagged && r.FlagsChanged && r.Tags.Contains(Tag.Flagged)),

        new MergeCase(
            "flags/no-change-reported-when-they-already-agree",
            "FlagsChanged drives a write; it must stay false when nothing moved.",
            new TagMergeInput { LocalFlags = MessageFlags.Flagged, ServerFlags = MessageFlags.Flagged },
            r => !r.FlagsChanged),

        new MergeCase(
            "tags/local-wins-and-the-server-is-told-to-catch-up",
            "CLAUDE invariant 9: local wins on custom tags. The local tag survives and the delta pushes it up.",
            new TagMergeInput { LocalTags = [Tag.Parse("work")], ServerKeywords = [] },
            r => r.CustomTags.Count == 1
                 && r.CustomTags[0] == Tag.Parse("work")
                 && r.Push.AddKeywords.Count == 1
                 && r.Push.AddKeywords[0] == "work"
                 && !r.TagsChanged),

        new MergeCase(
            "tags/local-wins-over-a-keyword-the-server-still-holds",
            "CLAUDE invariant 9: a tag removed locally is removed on the server too, not resurrected by the sync.",
            new TagMergeInput { LocalTags = [], ServerKeywords = ["urgent"] },
            r => r.CustomTags.Count == 0
                 && r.Push.RemoveKeywords.Count == 1
                 && r.Push.RemoveKeywords[0] == "urgent"
                 && !r.TagsChanged),

        new MergeCase(
            "tags/first-observation-adopts-the-server-instead-of-erasing-it",
            "On first observation an empty local tag set is ignorance, not intent. Treating it as intent would "
            + "delete every keyword on the server the first time we see a folder.",
            new TagMergeInput { LocalTags = [], ServerKeywords = ["urgent"], LocalTagsKnown = false },
            r => r.CustomTags.Count == 1 && r.CustomTags[0] == Tag.Parse("urgent") && r.Push.IsEmpty && r.TagsChanged),

        new MergeCase(
            "tags/keywords-that-are-not-persistable-are-kept-locally-and-pushed-nowhere",
            "§14.5 case 22: without PERMANENTFLAGS \\* the local tags stand, and the push delta stays empty.",
            new TagMergeInput
            {
                LocalTags = [Tag.Parse("work")],
                ServerKeywords = ["urgent"],
                ServerAcceptsCustomKeywords = false,
            },
            r => r.CustomTags.Count == 1 && r.CustomTags[0] == Tag.Parse("work") && r.Push.IsEmpty),

        new MergeCase(
            "tags/local-only-tags-are-never-pushed",
            "'inbox' is local structure: it stays in the tag set and never becomes IMAP traffic.",
            new TagMergeInput { LocalTags = [Tag.Inbox, Tag.Parse("work")], ServerKeywords = [] },
            r => r.CustomTags.Count == 2
                 && r.Push.AddKeywords.Count == 1
                 && r.Push.AddKeywords[0] == "work"),

        new MergeCase(
            "tags/system-tags-come-from-the-flags-not-from-the-tag-table",
            "A system tag is a projection of the bitfield, so it must never be stored or pushed as a keyword.",
            new TagMergeInput
            {
                LocalTags = [Tag.Unread, Tag.Parse("work")],
                LocalFlags = MessageFlags.Unread,
                ServerFlags = MessageFlags.None,
                ServerKeywords = [],
            },
            r => r.CustomTags.Count == 1
                 && r.CustomTags[0] == Tag.Parse("work")
                 && !r.Tags.Contains(Tag.Unread)
                 && r.Push.AddKeywords.Count == 1),

        new MergeCase(
            "shape/tags-are-the-union-of-system-and-custom",
            "The full tag view a client sees is the flags plus the custom tags, in one stable ordinal order.",
            new TagMergeInput
            {
                LocalTags = [Tag.Parse("work")],
                ServerFlags = MessageFlags.Unread | MessageFlags.Flagged,
                ServerKeywords = ["work"],
            },
            r => r.Tags.Count == 3
                 && r.Tags[0] == Tag.Flagged
                 && r.Tags[1] == Tag.Unread
                 && r.Tags[2] == Tag.Parse("work")),
    ];

    private static string Join(IReadOnlyList<Tag> tags) => string.Join(",", tags.Select(t => t.Value));

    private static string Describe(FlagDelta delta) =>
        $"+{delta.Add} -{delta.Remove} +[{string.Join(",", delta.AddKeywords)}] -[{string.Join(",", delta.RemoveKeywords)}]";
}
