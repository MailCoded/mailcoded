using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class FolderTreeTests
{
    [Fact]
    public void Implied_parents_have_no_folder_and_listed_rows_carry_their_stored_folder()
    {
        var nodes = FolderTree.Arrange(
        [
            Summary(1, "INBOX", FolderRole.Inbox),
            Summary(2, "Clients/Acme"),
        ]);

        Assert.Equal(["INBOX", "Clients", "Acme"], nodes.Select(n => n.Row.Leaf));
        Assert.Equal([1L, null, 2L], nodes.Select(n => n.Folder?.Id.Value));
    }

    [Fact]
    public void Listed_leaves_out_the_implied_parents_and_keeps_the_tree_order()
    {
        var nodes = FolderTree.Listed([Summary(2, "Clients/Acme"), Summary(1, "INBOX", FolderRole.Inbox)]);

        Assert.Equal([1L, 2L], nodes.Select(n => n.Folder!.Id.Value));
    }

    private static FolderSummary Summary(long id, string name, FolderRole role = FolderRole.None) =>
        new() { Id = new FolderId(id), AccountId = new AccountId(1), Path = FolderPath.Create(name), Role = role };
}
