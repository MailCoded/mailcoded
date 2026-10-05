using Mailcoded.Core.Domain.Folders;
using Mailcoded.Core.Domain.Primitives;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class FolderHierarchyTests
{
    [Fact]
    public void A_flat_list_of_top_level_folders_has_no_parents()
    {
        var rows = Arrange(false, E(1, "Notes"), E(2, "Ideas"));

        Assert.Equal(["Ideas", "Notes"], rows.Select(r => r.Leaf));
        Assert.All(rows, r =>
        {
            Assert.Equal(0, r.Depth);
            Assert.Null(r.ParentId);
        });
    }

    [Fact]
    public void A_listed_parent_is_the_parent_of_its_children_and_they_follow_it()
    {
        var rows = Arrange(false,
            E(3, "Projects/Beta"),
            E(1, "Projects"),
            E(2, "Projects/Alpha"),
            E(4, "Zebra"));

        Assert.Equal(["Projects", "Alpha", "Beta", "Zebra"], rows.Select(r => r.Leaf));
        Assert.Equal([0, 1, 1, 0], rows.Select(r => r.Depth));
        Assert.Equal([null, 1L, 1L, null], rows.Select(r => r.ParentId));
        Assert.All(rows, r => Assert.True(r.Listed));
    }

    [Fact]
    public void A_parent_the_server_never_listed_is_an_implied_row_with_no_id()
    {
        var rows = Arrange(false, E(1, "Clients/Acme/2026"));

        Assert.Equal(["Clients", "Acme", "2026"], rows.Select(r => r.Leaf));
        Assert.Equal([null, null, 1L], rows.Select(r => r.Id));
        Assert.Equal(["Clients", "Clients/Acme", "Clients/Acme/2026"], rows.Select(r => r.Path));
        Assert.Equal([0, 1, 2], rows.Select(r => r.Depth));
        Assert.Equal([false, false, true], rows.Select(r => r.Listed));
        Assert.Null(rows[2].ParentId);
    }

    [Fact]
    public void The_parent_id_skips_implied_levels_to_the_nearest_listed_ancestor()
    {
        var rows = Arrange(false, E(1, "Archive"), E(2, "Archive/2024/Q1"));

        Assert.Equal(["Archive", "2024", "Q1"], rows.Select(r => r.Leaf));
        Assert.Equal(1L, rows[2].ParentId);
        Assert.Equal(1L, rows[1].ParentId);
        Assert.Equal(2, rows[2].Depth);
    }

    [Fact]
    public void Special_use_folders_lead_in_the_familiar_order_and_the_rest_sort_by_name()
    {
        var rows = Arrange(false,
            E(1, "Notes"),
            E(2, "Archive", FolderRole.Archive),
            E(3, "Junk Email", FolderRole.Junk),
            E(4, "Deleted Items", FolderRole.Trash),
            E(5, "Sent Items", FolderRole.Sent),
            E(6, "Drafts", FolderRole.Drafts),
            E(7, "INBOX", FolderRole.Inbox),
            E(8, "aardvark"));

        Assert.Equal(
            ["INBOX", "Drafts", "Sent Items", "Deleted Items", "Junk Email", "Archive", "aardvark", "Notes"],
            rows.Select(r => r.Leaf));
    }

    [Fact]
    public void A_localised_special_use_name_still_sorts_by_its_role()
    {
        var rows = Arrange(false, E(1, "Archiv", FolderRole.Archive), E(2, "Papierkorb", FolderRole.Trash), E(3, "Posteingang", FolderRole.Inbox));

        Assert.Equal(["Posteingang", "Papierkorb", "Archiv"], rows.Select(r => r.Leaf));
    }

    [Fact]
    public void A_flat_namespace_has_no_hierarchy_even_when_a_name_contains_a_slash()
    {
        var rows = Arrange(false, F(1, "a/b"), F(2, "a"), F(3, "INBOX"));

        Assert.Equal(["a", "a/b", "INBOX"], rows.Select(r => r.Leaf));
        Assert.All(rows, r =>
        {
            Assert.Equal(0, r.Depth);
            Assert.Null(r.ParentId);
            Assert.True(r.Listed);
        });
    }

    [Fact]
    public void A_dot_delimited_server_arranges_exactly_like_a_slash_delimited_one()
    {
        var dotted = Arrange(false,
            new FolderEntry(1, FolderPath.Create("INBOX", '.'), FolderRole.Inbox),
            new FolderEntry(2, FolderPath.Create("INBOX.Work", '.'), FolderRole.None),
            new FolderEntry(3, FolderPath.Create("INBOX.Work.Q1", '.'), FolderRole.None));

        Assert.Equal(["INBOX", "Work", "Q1"], dotted.Select(r => r.Leaf));
        Assert.Equal([0, 1, 2], dotted.Select(r => r.Depth));
        Assert.Equal([null, 1L, 2L], dotted.Select(r => r.ParentId));
    }

    [Fact]
    public void Inbox_children_attach_to_inbox_whatever_case_the_server_used()
    {
        var rows = Arrange(false, E(1, "INBOX", FolderRole.Inbox), E(2, "inbox/Receipts"));

        Assert.Equal(1L, rows[1].ParentId);
        Assert.Equal(1, rows[1].Depth);
    }

    [Fact]
    public void Two_names_that_differ_only_by_case_are_two_folders_unless_the_platform_folds_case()
    {
        var sensitive = Arrange(false, E(1, "Work"), E(2, "work"), E(3, "work/Sub"));

        Assert.Equal(["Work", "work", "Sub"], sensitive.Select(r => r.Leaf));
        Assert.Equal(2L, sensitive[2].ParentId);

        var folded = Arrange(true, E(1, "Work"), E(2, "work"), E(3, "work/Sub"));

        Assert.Equal(["Work", "work", "Sub"], folded.Select(r => r.Leaf));
        Assert.Equal(1L, folded[2].ParentId);
        Assert.Equal([1L, 2L, 3L], folded.Select(r => r.Id));
    }

    [Fact]
    public void Empty_segments_do_not_create_levels()
    {
        var rows = Arrange(false, E(1, "a//b"), E(2, "a"));

        Assert.Equal(["a", "b"], rows.Select(r => r.Leaf));
        Assert.Equal(1, rows[1].Depth);
        Assert.Equal(2L, rows[1].ParentId);
    }

    [Fact]
    public void The_order_does_not_depend_on_the_order_the_folders_were_listed_in()
    {
        FolderEntry[] folders =
        [
            E(1, "INBOX", FolderRole.Inbox), E(2, "Work"), E(3, "Work/A"), E(4, "Work/B/C"), E(5, "Sent", FolderRole.Sent), E(6, "Misc/x"),
        ];

        var forward = Arrange(false, folders);
        var reversed = Arrange(false, folders.Reverse().ToArray());

        Assert.Equal(forward.Select(r => (r.Id, r.Path, r.Depth, r.ParentId)), reversed.Select(r => (r.Id, r.Path, r.Depth, r.ParentId)));
    }

    [Fact]
    public void An_empty_account_has_an_empty_tree()
    {
        Assert.Empty(Arrange(false));
    }

    [Fact]
    public void A_hostile_name_with_thousands_of_levels_is_bounded_not_quadratic()
    {
        var name = string.Join('/', Enumerable.Repeat("x", 50_000));

        var rows = Arrange(false, E(1, name));

        Assert.Equal(FolderPath.MaxSegments, rows.Count);
        Assert.Equal(FolderPath.MaxSegments - 1, rows[^1].Depth);
        Assert.Equal(1L, rows[^1].Id);
    }

    [Fact]
    public void Control_and_bidi_characters_pass_through_untouched_for_the_renderer_to_defang()
    {
        var rows = Arrange(false, E(1, "Work/\u202Eevil\u001b[2J"));

        Assert.Equal("\u202Eevil\u001b[2J", rows[1].Leaf);
    }

    private static FolderEntry E(long id, string name, FolderRole role = FolderRole.None) =>
        new(id, FolderPath.Create(name), role);

    private static FolderEntry F(long id, string name) =>
        new(id, FolderPath.Create(name, '\0'), FolderRole.None);

    private static IReadOnlyList<FolderTreeRow> Arrange(bool ignoreCase, params FolderEntry[] folders) =>
        FolderHierarchy.Arrange(folders, ignoreCase);
}
