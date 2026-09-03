using Mailcoded.Tui;
using Mailcoded.Tui.Render;
using Mailcoded.Tui.Views;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

/// <summary>Chrome throws on anything but printable ASCII, so a stray dash blanks a frame.</summary>
public sealed class ChromeLiteralTests
{
    [Fact]
    public void Every_help_line_is_something_the_chrome_path_will_accept()
    {
        foreach (var line in Help.Lines)
            Assert.Equal(line, TerminalText.Chrome(line, line.Length).Text);
    }

    [Fact]
    public void Every_status_hint_is_something_the_chrome_path_will_accept()
    {
        foreach (var pane in Enum.GetValues<Pane>())
        {
            var hint = Screen.Hint(new AppState { Focus = pane });
            Assert.Equal(hint, TerminalText.Chrome(hint, hint.Length).Text);
        }
    }

    [Fact]
    public void Chrome_refuses_the_dash_that_looks_like_a_dash()
    {
        Assert.Throws<ArgumentException>(() => TerminalText.Chrome("a — b", 40));
    }

    [Fact]
    public void Chrome_keeps_the_padding_a_column_layout_depends_on()
    {
        var span = TerminalText.Chrome("*  ", 3);

        Assert.Equal("*  ", span.Text);
        Assert.Equal(3, span.Columns);
    }
}
