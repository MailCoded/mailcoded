using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Search;

/// <summary>Which Folders a search covers when nothing says otherwise.</summary>
public static class SearchFolderScope
{
    /// <summary>The <c>in:</c>/<c>folder:</c> value that lifts the default and searches every Folder.</summary>
    public const string AnywhereKeyword = "anywhere";

    public static IReadOnlyList<FolderRole> HiddenByDefault { get; } = [FolderRole.Trash, FolderRole.Junk];

    /// <summary>Mail the user already discarded or that was filed as junk stays out of results until the
    /// search asks for it by naming a Folder, scoping to one, or saying <c>in:anywhere</c>.</summary>
    public static bool HidesByDefault(SearchQuery query, bool scopedToFolder)
    {
        ArgumentNullException.ThrowIfNull(query);
        return !scopedToFolder && !query.NamesFolderScope;
    }
}
