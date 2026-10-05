using Mailcoded.Core.Domain.Folders;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

/// <summary>A tree row with the stored folder behind it; <see cref="Folder"/> is null for an implied parent.</summary>
public sealed record FolderTreeNode(FolderTreeRow Row, FolderSummary? Folder);

public static class FolderTree
{
    /// <summary>Every row of the tree, implied parents included, in display order.</summary>
    public static IReadOnlyList<FolderTreeNode> Arrange(IReadOnlyList<FolderSummary> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var entries = new List<FolderEntry>(folders.Count);
        var byId = new Dictionary<long, FolderSummary>(folders.Count);
        foreach (var folder in folders)
        {
            entries.Add(new FolderEntry(folder.Id.Value, folder.Path, folder.Role));
            byId[folder.Id.Value] = folder;
        }

        var rows = FolderHierarchy.Arrange(entries);
        var nodes = new List<FolderTreeNode>(rows.Count);
        foreach (var row in rows)
            nodes.Add(new FolderTreeNode(row, row.Id is { } id ? byId[id] : null));

        return nodes;
    }

    /// <summary>Only the folders, in tree order.</summary>
    public static IReadOnlyList<FolderTreeNode> Listed(IReadOnlyList<FolderSummary> folders)
    {
        var all = Arrange(folders);
        var listed = new List<FolderTreeNode>(folders.Count);
        foreach (var node in all)
            if (node.Folder is not null) listed.Add(node);

        return listed;
    }
}
