using Mailcoded.Protocol;

namespace Mailcoded.Tui;

internal enum NavKind
{
    Account,
    Folder,
}

/// <summary>One line of the sidebar. <see cref="Key"/> is stable across reloads so a folder stays
/// selected and stays expanded when counts change underneath it.</summary>
internal sealed record NavRow
{
    public required NavKind Kind { get; init; }
    public required long AccountId { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public int Depth { get; init; }
    public FolderDto? Folder { get; init; }
    public bool HasChildren { get; init; }
    public bool Expanded { get; init; }
    public int Unread { get; init; }
    public int Total { get; init; }

    public bool IsSelectable => Kind == NavKind.Folder && Folder is not null;
}

/// <summary>Flattens accounts and folders into sidebar order: special-use first in the familiar
/// order, the rest alphabetical, nested on the '/' the protocol puts between levels of a name.</summary>
internal static class Navigation
{
    private const int MaxLevels = 32;

    /// <summary>Inbox, Drafts, Sent, Deleted, Junk, Archive, then everything else. Roles come from
    /// the server's special-use flags, so this order survives a folder called "Papierkorb".</summary>
    private static readonly string[] RoleOrder = ["inbox", "drafts", "sent", "trash", "junk", "archive", "all"];

    public static IReadOnlyList<NavRow> Build(
        IReadOnlyList<AccountDto> accounts,
        IReadOnlyDictionary<long, IReadOnlyList<FolderDto>> folders,
        IReadOnlySet<string> collapsed)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(collapsed);

        var rows = new List<NavRow>();

        foreach (var account in accounts)
        {
            var owned = folders.TryGetValue(account.Id, out var list) ? list : [];
            var tree = BuildTree(owned);
            var accountKey = AccountKey(account.Id);
            var open = !collapsed.Contains(accountKey);

            rows.Add(new NavRow
            {
                Kind = NavKind.Account,
                AccountId = account.Id,
                Key = accountKey,
                Label = account.Email,
                Depth = 0,
                HasChildren = tree.Count > 0,
                Expanded = open,
                Unread = Sum(owned, f => f.Unread),
                Total = Sum(owned, f => f.Total),
            });

            if (open) Emit(rows, tree, account.Id, collapsed, depth: 1);
        }

        return rows;
    }

    public static string AccountKey(long accountId) => "a:" + accountId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string FolderKey(long accountId, string path) => AccountKey(accountId) + "/" + path;

    private static int Sum(IReadOnlyList<FolderDto> folders, Func<FolderDto, int> pick)
    {
        var total = 0;
        foreach (var folder in folders) total += pick(folder);
        return total;
    }

    private static void Emit(
        List<NavRow> rows,
        List<Node> nodes,
        long accountId,
        IReadOnlySet<string> collapsed,
        int depth)
    {
        foreach (var node in nodes)
        {
            var key = FolderKey(accountId, node.Path);
            var open = !collapsed.Contains(key);

            rows.Add(new NavRow
            {
                Kind = NavKind.Folder,
                AccountId = accountId,
                Key = key,
                Label = node.Label,
                Depth = depth,
                Folder = node.Folder,
                HasChildren = node.Children.Count > 0,
                Expanded = open,
                Unread = open ? node.Folder?.Unread ?? 0 : node.Unread,
                Total = open ? node.Folder?.Total ?? 0 : node.Total,
            });

            if (open) Emit(rows, node.Children, accountId, collapsed, depth + 1);
        }
    }

    private static List<Node> BuildTree(IReadOnlyList<FolderDto> folders)
    {
        var roots = new List<Node>();
        var index = new Dictionary<string, Node>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            var segments = Segments(folder);
            if (segments.Length == 0) continue;

            var path = string.Empty;
            Node? parent = null;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = i == 0 && segments[0].Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? "INBOX" : segments[i];
                path = i == 0 ? segment : path + "/" + segment;

                if (!index.TryGetValue(path, out var node))
                {
                    // A server can list "Projects/Alpha" without listing "Projects"; the parent is
                    // still a row, just one with nothing to open.
                    node = new Node(path, segments[i]);
                    index[path] = node;
                    (parent?.Children ?? roots).Add(node);
                }

                parent = node;
            }

            parent!.Folder = folder;
        }

        Sort(roots);
        return roots;
    }

    /// <summary>Depth tells how many levels a name has, so a '/' in a flat-namespace name stays a letter; no leaf means the daemon sent no hierarchy.</summary>
    private static string[] Segments(FolderDto folder)
    {
        if (folder.Leaf is null) return folder.Name.Split('/', MaxLevels, StringSplitOptions.RemoveEmptyEntries);
        if (folder.Depth <= 0) return [folder.Name];

        return folder.Name.Split('/', Math.Min(folder.Depth + 1, MaxLevels), StringSplitOptions.RemoveEmptyEntries);
    }

    private static void Sort(List<Node> nodes)
    {
        nodes.Sort(Compare);
        foreach (var node in nodes) Sort(node.Children);
    }

    private static int Compare(Node left, Node right)
    {
        var byRole = Rank(left.Folder?.Role).CompareTo(Rank(right.Folder?.Role));
        return byRole != 0 ? byRole : string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(string? role)
    {
        if (role is null) return RoleOrder.Length;

        var at = Array.IndexOf(RoleOrder, role);
        return at < 0 ? RoleOrder.Length : at;
    }

    private sealed class Node(string path, string label)
    {
        public string Path { get; } = path;
        public string Label { get; } = label;
        public FolderDto? Folder { get; set; }
        public List<Node> Children { get; } = [];

        /// <summary>This folder and everything under it: what a collapsed row shows so nothing unread hides.</summary>
        public int Unread => (Folder?.Unread ?? 0) + Children.Sum(c => c.Unread);

        public int Total => (Folder?.Total ?? 0) + Children.Sum(c => c.Total);
    }
}
