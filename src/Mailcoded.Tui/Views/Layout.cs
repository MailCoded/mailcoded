namespace Mailcoded.Tui.Views;

/// <summary>Column geometry for the three panes. Below <see cref="MinPreviewColumns"/> the preview
/// is dropped rather than squeezed: a list too narrow to show a subject helps nobody.</summary>
internal readonly record struct Layout
{
    public const int MinPreviewColumns = 100;
    public const int MinListWidth = 34;
    public const int MinPreviewWidth = 30;

    public required int FolderWidth { get; init; }
    public required int ListLeft { get; init; }
    public required int ListWidth { get; init; }
    public int PreviewLeft { get; init; }
    public int PreviewWidth { get; init; }

    public bool HasPreview => PreviewWidth > 0;

    public static Layout For(int columns, bool wantPreview)
    {
        var folders = Math.Max(Screen.MinFolderWidth, Math.Min(30, columns / 5));
        var listLeft = folders + 1;
        var rest = Math.Max(1, columns - listLeft);

        if (!wantPreview || columns < MinPreviewColumns)
            return new Layout { FolderWidth = folders, ListLeft = listLeft, ListWidth = rest };

        var preview = Math.Max(MinPreviewWidth, rest * 45 / 100);
        var list = rest - preview - 1;

        if (list < MinListWidth)
            return new Layout { FolderWidth = folders, ListLeft = listLeft, ListWidth = rest };

        return new Layout
        {
            FolderWidth = folders,
            ListLeft = listLeft,
            ListWidth = list,
            PreviewLeft = listLeft + list + 1,
            PreviewWidth = preview,
        };
    }
}
