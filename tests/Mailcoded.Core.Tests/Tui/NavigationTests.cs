using Mailcoded.Protocol;
using Mailcoded.Tui;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

public sealed class NavigationTests
{
    private static readonly HashSet<string> NothingCollapsed = new(StringComparer.Ordinal);

    [Fact]
    public void Every_configured_account_gets_a_row()
    {
        var rows = Navigation.Build(
            [Account(1, "one@example.test"), Account(2, "two@example.test")],
            new Dictionary<long, IReadOnlyList<FolderDto>>
            {
                [1] = [Folder(10, 1, "INBOX", "inbox")],
                [2] = [Folder(20, 2, "INBOX", "inbox")],
            },
            NothingCollapsed);

        Assert.Equal(
            ["one@example.test", "INBOX", "two@example.test", "INBOX"],
            rows.Select(r => r.Label));
    }

    [Fact]
    public void Special_use_folders_come_first_in_the_order_a_mail_client_uses()
    {
        var rows = Navigation.Build(
            [Account(1, "me@example.test")],
            Owned(1,
                Folder(1, 1, "Notes", null),
                Folder(2, 1, "Archive", "archive"),
                Folder(3, 1, "Junk Email", "junk"),
                Folder(4, 1, "Deleted Items", "trash"),
                Folder(5, 1, "Sent Items", "sent"),
                Folder(6, 1, "Drafts", "drafts"),
                Folder(7, 1, "Inbox", "inbox"),
                Folder(8, 1, "Aardvark", null)),
            NothingCollapsed);

        Assert.Equal(
            ["Inbox", "Drafts", "Sent Items", "Deleted Items", "Junk Email", "Archive", "Aardvark", "Notes"],
            rows.Where(r => r.Kind == NavKind.Folder).Select(r => r.Label));
    }

    /// <summary>The role is what orders a folder, so a localised name still lands in the right slot.</summary>
    [Fact]
    public void A_localised_trash_folder_still_sorts_as_trash()
    {
        var rows = Navigation.Build(
            [Account(1, "me@example.test")],
            Owned(1,
                Folder(1, 1, "Archiv", "archive"),
                Folder(2, 1, "Papierkorb", "trash"),
                Folder(3, 1, "Posteingang", "inbox")),
            NothingCollapsed);

        Assert.Equal(
            ["Posteingang", "Papierkorb", "Archiv"],
            rows.Where(r => r.Kind == NavKind.Folder).Select(r => r.Label));
    }

    [Fact]
    public void A_path_nests_on_the_delimiter_and_shows_only_its_last_segment()
    {
        var rows = Navigation.Build(
            [Account(1, "me@example.test")],
            Owned(1,
                Folder(1, 1, "INBOX", "inbox"),
                Folder(2, 1, "Projects", null),
                Folder(3, 1, "Projects/Alpha", null),
                Folder(4, 1, "Projects/Beta", null)),
            NothingCollapsed);

        var folders = rows.Where(r => r.Kind == NavKind.Folder).ToList();

        Assert.Equal(["INBOX", "Projects", "Alpha", "Beta"], folders.Select(f => f.Label));
        Assert.Equal([1, 1, 2, 2], folders.Select(f => f.Depth));
    }

    /// <summary>Servers list children without listing the parent; the parent is still a row.</summary>
    [Fact]
    public void A_parent_the_server_never_listed_is_still_a_row_but_not_a_destination()
    {
        var rows = Navigation.Build(
            [Account(1, "me@example.test")],
            Owned(1, Folder(1, 1, "Clients/Acme/2026", null)),
            NothingCollapsed);

        var folders = rows.Where(r => r.Kind == NavKind.Folder).ToList();

        Assert.Equal(["Clients", "Acme", "2026"], folders.Select(f => f.Label));
        Assert.False(folders[0].IsSelectable);
        Assert.False(folders[1].IsSelectable);
        Assert.True(folders[2].IsSelectable);
    }

    [Fact]
    public void Collapsing_an_account_hides_its_folders_and_nothing_else()
    {
        var accounts = new[] { Account(1, "one@example.test"), Account(2, "two@example.test") };
        var folders = new Dictionary<long, IReadOnlyList<FolderDto>>
        {
            [1] = [Folder(10, 1, "INBOX", "inbox")],
            [2] = [Folder(20, 2, "INBOX", "inbox")],
        };

        var rows = Navigation.Build(accounts, folders, new HashSet<string>(StringComparer.Ordinal)
        {
            Navigation.AccountKey(1),
        });

        Assert.Equal(["one@example.test", "two@example.test", "INBOX"], rows.Select(r => r.Label));
        Assert.False(rows[0].Expanded);
        Assert.True(rows[1].Expanded);
    }

    [Fact]
    public void Collapsing_a_parent_hides_the_whole_subtree()
    {
        var rows = Navigation.Build(
            [Account(1, "me@example.test")],
            Owned(1,
                Folder(1, 1, "Projects", null),
                Folder(2, 1, "Projects/Alpha", null),
                Folder(3, 1, "Projects/Alpha/Old", null),
                Folder(4, 1, "Zebra", null)),
            new HashSet<string>(StringComparer.Ordinal) { Navigation.FolderKey(1, "Projects") });

        Assert.Equal(["me@example.test", "Projects", "Zebra"], rows.Select(r => r.Label));
    }

    [Fact]
    public void An_account_row_totals_the_unread_of_its_folders()
    {
        var rows = Navigation.Build(
            [Account(1, "me@example.test")],
            Owned(1,
                Folder(1, 1, "INBOX", "inbox", unread: 3, total: 40),
                Folder(2, 1, "Junk Email", "junk", unread: 7, total: 9)),
            NothingCollapsed);

        Assert.Equal(10, rows[0].Unread);
        Assert.Equal(49, rows[0].Total);
    }

    [Fact]
    public void An_account_with_no_folders_yet_is_still_listed()
    {
        var rows = Navigation.Build(
            [Account(1, "unsynced@example.test")],
            new Dictionary<long, IReadOnlyList<FolderDto>>(),
            NothingCollapsed);

        var only = Assert.Single(rows);

        Assert.Equal("unsynced@example.test", only.Label);
        Assert.False(only.HasChildren);
    }

    private static Dictionary<long, IReadOnlyList<FolderDto>> Owned(long accountId, params FolderDto[] folders) =>
        new() { [accountId] = folders };

    private static AccountDto Account(long id, string email) =>
        new() { Id = id, Email = email, Provider = "imap", Auth = "password" };

    private static FolderDto Folder(long id, long accountId, string name, string? role, int unread = 0, int total = 0) =>
        new() { Id = id, AccountId = accountId, Name = name, Role = role, Unread = unread, Total = total };
}
