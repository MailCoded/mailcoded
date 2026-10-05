using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Folders;

public readonly record struct FolderEntry(long Id, FolderPath Path, FolderRole Role);

/// <summary>One line of the folder tree; <see cref="Id"/> is null for a parent that only a child's name implies.</summary>
public sealed record FolderTreeRow
{
    public long? Id { get; init; }

    /// <summary>The nearest ancestor that is itself a listed folder.</summary>
    public long? ParentId { get; init; }

    public required string Path { get; init; }

    public required string Leaf { get; init; }

    public int Depth { get; init; }

    public bool Listed => Id is not null;
}

/// <summary>Arranges folders depth first; siblings go special-use first (inbox, drafts, sent, trash, junk, archive, all), then by name.</summary>
public static class FolderHierarchy
{
    private static readonly FolderRole[] RoleOrder =
    [
        FolderRole.Inbox, FolderRole.Drafts, FolderRole.Sent, FolderRole.Trash,
        FolderRole.Junk, FolderRole.Archive, FolderRole.All,
    ];

    public static IReadOnlyList<FolderTreeRow> Arrange(IReadOnlyList<FolderEntry> folders) =>
        Arrange(folders, OperatingSystem.IsWindows());

    /// <param name="ignoreCase">Windows compares folder names case-insensitively (invariant 7).</param>
    public static IReadOnlyList<FolderTreeRow> Arrange(IReadOnlyList<FolderEntry> folders, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var index = new Dictionary<string, Node>(comparer);
        var roots = new List<Node>();

        foreach (var folder in folders)
        {
            var segments = folder.Path.Segments();
            var key = folder.Path.IsFlat ? "\0" + folder.Path.Value : string.Empty;
            Node? parent = null;
            Node? node = null;

            for (var i = 0; i < segments.Length; i++)
            {
                if (!folder.Path.IsFlat)
                {
                    var segment = i == 0 && segments[0].Equals(FolderPath.Inbox, StringComparison.OrdinalIgnoreCase)
                        ? FolderPath.Inbox
                        : segments[i];
                    key = i == 0 ? segment : key + "/" + segment;
                }

                if (!index.TryGetValue(key, out node))
                {
                    node = new Node(key, segments[i], i, parent);
                    index[key] = node;
                    (parent?.Children ?? roots).Add(node);
                }

                parent = node;
            }

            node!.Entries.Add(folder);
        }

        var rows = new List<FolderTreeRow>(folders.Count);
        Emit(roots, rows);
        return rows;
    }

    private static void Emit(List<Node> nodes, List<FolderTreeRow> rows)
    {
        nodes.Sort(Compare);

        foreach (var node in nodes)
        {
            if (node.Entries.Count == 0)
            {
                rows.Add(new FolderTreeRow
                {
                    ParentId = node.NearestListedAncestor(),
                    Path = node.Key,
                    Leaf = node.Label,
                    Depth = node.Depth,
                });
            }

            foreach (var entry in node.Entries)
            {
                rows.Add(new FolderTreeRow
                {
                    Id = entry.Id,
                    ParentId = node.NearestListedAncestor(),
                    Path = entry.Path.Value,
                    Leaf = entry.Path.IsFlat ? entry.Path.Value : LastSegment(entry.Path),
                    Depth = node.Depth,
                });
            }

            Emit(node.Children, rows);
        }
    }

    private static string LastSegment(FolderPath path)
    {
        var segments = path.Segments();
        return segments[^1];
    }

    private static int Compare(Node left, Node right)
    {
        var byRole = left.Rank.CompareTo(right.Rank);
        if (byRole != 0) return byRole;

        var byName = string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
        if (byName != 0) return byName;

        byName = string.CompareOrdinal(left.Label, right.Label);
        if (byName != 0) return byName;

        return left.FirstId.CompareTo(right.FirstId);
    }

    private sealed class Node(string key, string label, int depth, Node? parent)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public int Depth { get; } = depth;
        public Node? Parent { get; } = parent;
        public List<Node> Children { get; } = [];
        public List<FolderEntry> Entries { get; } = [];

        public int Rank
        {
            get
            {
                if (Entries.Count == 0) return RoleOrder.Length;

                var at = Array.IndexOf(RoleOrder, Entries[0].Role);
                return at < 0 ? RoleOrder.Length : at;
            }
        }

        public long FirstId => Entries.Count == 0 ? long.MaxValue : Entries[0].Id;

        public long? NearestListedAncestor()
        {
            for (var up = Parent; up is not null; up = up.Parent)
                if (up.Entries.Count > 0) return up.Entries[0].Id;

            return null;
        }
    }
}
