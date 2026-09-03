using Mailcoded.Tui;
using Mailcoded.Tui.Views;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

public sealed class LayoutTests
{
    [Fact]
    public void A_wide_terminal_gets_three_panes()
    {
        var layout = Layout.For(140, wantPreview: true);

        Assert.True(layout.HasPreview);
        Assert.True(layout.FolderWidth >= Screen.MinFolderWidth);
        Assert.True(layout.ListWidth >= Layout.MinListWidth);
        Assert.True(layout.PreviewWidth >= Layout.MinPreviewWidth);
    }

    /// <summary>A list too narrow to show a subject is worse than no preview.</summary>
    [Theory]
    [InlineData(80)]
    [InlineData(99)]
    public void A_narrow_terminal_drops_the_preview_rather_than_squeezing_the_list(int columns)
    {
        var layout = Layout.For(columns, wantPreview: true);

        Assert.False(layout.HasPreview);
        Assert.Equal(columns, layout.ListLeft + layout.ListWidth);
    }

    [Fact]
    public void Turning_the_preview_off_gives_its_columns_to_the_list()
    {
        var on = Layout.For(140, wantPreview: true);
        var off = Layout.For(140, wantPreview: false);

        Assert.True(off.ListWidth > on.ListWidth);
        Assert.False(off.HasPreview);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(160)]
    [InlineData(240)]
    [InlineData(400)]
    public void No_pane_ever_overruns_the_terminal(int columns)
    {
        foreach (var want in new[] { true, false })
        {
            var layout = Layout.For(columns, want);

            Assert.True(layout.FolderWidth < layout.ListLeft);
            Assert.True(layout.ListLeft + layout.ListWidth <= columns);

            if (!layout.HasPreview) continue;

            Assert.True(layout.PreviewLeft > layout.ListLeft + layout.ListWidth - 1);
            Assert.True(
                layout.PreviewLeft + layout.PreviewWidth <= columns,
                $"the preview runs to {layout.PreviewLeft + layout.PreviewWidth} of {columns}");
        }
    }

    [Fact]
    public void A_column_maps_to_the_pane_drawn_there()
    {
        var layout = Layout.For(140, wantPreview: true);

        Assert.Equal(Pane.Folders, layout.PaneAt(0));
        Assert.Equal(Pane.Folders, layout.PaneAt(layout.FolderWidth - 1));
        Assert.Equal(Pane.Messages, layout.PaneAt(layout.ListLeft));
        Assert.Equal(Pane.Messages, layout.PaneAt(layout.ListLeft + layout.ListWidth - 1));
        Assert.Equal(Pane.Reader, layout.PaneAt(layout.PreviewLeft));
        Assert.Equal(Pane.Reader, layout.PaneAt(layout.PreviewLeft + layout.PreviewWidth - 1));
    }

    /// <summary>A click on a rule column should do what it looks like, not nothing.</summary>
    [Fact]
    public void A_rule_column_belongs_to_the_pane_on_its_left()
    {
        var layout = Layout.For(140, wantPreview: true);

        Assert.Equal(Pane.Folders, layout.PaneAt(layout.FolderWidth));
        Assert.Equal(Pane.Messages, layout.PaneAt(layout.PreviewLeft - 1));
    }

    [Fact]
    public void With_no_preview_the_far_right_is_still_the_list()
    {
        var layout = Layout.For(80, wantPreview: true);

        Assert.Equal(Pane.Messages, layout.PaneAt(79));
    }

    [Fact]
    public void The_panes_are_separated_by_exactly_one_rule_column_each()
    {
        var layout = Layout.For(140, wantPreview: true);

        Assert.Equal(layout.FolderWidth + 1, layout.ListLeft);
        Assert.Equal(layout.ListLeft + layout.ListWidth + 1, layout.PreviewLeft);
    }
}
