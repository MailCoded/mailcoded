using System.Text;

namespace Mailcoded.Tui.Render;

/// <summary>Absolute row addressing with auto-wrap off, so a width miscount corrupts one row
/// rather than shearing the frame. Accepts SafeSpan only; there is no Write(string).</summary>
public sealed class TerminalWriter
{
    internal const string Csi = "\u001b[";

    private readonly TextWriter _output;
    private readonly StringBuilder _frame = new(8 * 1024);

    private TextStyle _style = TextStyle.Normal;
    private bool _fullScreen;
    private bool _mouse;

    public TerminalWriter(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        Measure();
    }

    public int Rows { get; private set; } = 24;

    public int Columns { get; private set; } = 80;

    public bool Measure()
    {
        int rows, columns;
        try
        {
            rows = Console.WindowHeight;
            columns = Console.WindowWidth;
        }
        catch (IOException)
        {
            return false;
        }

        if (rows <= 0) rows = 24;
        if (columns <= 0) columns = 80;
        rows = Math.Clamp(rows, 4, 500);
        columns = Math.Clamp(columns, 20, 1000);

        var changed = rows != Rows || columns != Columns;
        Rows = rows;
        Columns = columns;
        return changed;
    }

    public void EnterFullScreen(bool mouse = false)
    {
        if (_fullScreen) return;
        _fullScreen = true;
        _mouse = mouse;

        _output.Write(Csi + "?1049h");
        _output.Write(Csi + "?7l");
        _output.Write(Csi + "?25l");

        // 1000 reports press and release, 1006 encodes them in decimal so a wide terminal is not
        // capped at column 223. Drag reporting is deliberately not enabled.
        if (mouse) _output.Write(Csi + "?1000h" + Csi + "?1006h");

        _output.Flush();
    }

    public void LeaveFullScreen()
    {
        if (!_fullScreen) return;
        _fullScreen = false;

        if (_mouse) _output.Write(Csi + "?1006l" + Csi + "?1000l");

        _output.Write(Csi + "0m");
        _output.Write(Csi + "?25h");
        _output.Write(Csi + "?7h");
        _output.Write(Csi + "?1049l");
        _output.Flush();
    }

    public void BeginFrame()
    {
        _frame.Clear();
        _style = TextStyle.Normal;
        _frame.Append(Csi).Append("0m");
    }

    public void Row(int row, SafeSpan span, TextStyle style = TextStyle.Normal)
    {
        if (row < 0 || row >= Rows) return;

        MoveTo(row, 0);
        Apply(style);
        _frame.Append(TerminalText.Clip(span, Columns).Text);
        Apply(TextStyle.Normal);
        _frame.Append(Csi).Append('K');
    }

    public void At(int row, int column, SafeSpan span, TextStyle style = TextStyle.Normal)
    {
        if (row < 0 || row >= Rows || column < 0 || column >= Columns) return;

        MoveTo(row, column);
        Apply(style);
        _frame.Append(TerminalText.Clip(span, Columns - column).Text);
        Apply(TextStyle.Normal);
    }

    public void Blank(int row) => Row(row, SafeSpan.Empty);

    public void EndFrame(int cursorRow = -1, int cursorColumn = 0)
    {
        if (cursorRow >= 0 && cursorRow < Rows)
        {
            MoveTo(cursorRow, Math.Clamp(cursorColumn, 0, Columns - 1));
            _frame.Append(Csi).Append("?25h");
        }
        else
        {
            _frame.Append(Csi).Append("?25l");
        }

        _output.Write(_frame.ToString());
        _output.Flush();
        _frame.Clear();
    }

    private void MoveTo(int row, int column) =>
        _frame.Append(Csi).Append(row + 1).Append(';').Append(column + 1).Append('H');

    private void Apply(TextStyle style)
    {
        if (style == _style) return;
        _style = style;
        _frame.Append(Csi).Append(Sgr(style));
    }

    private static string Sgr(TextStyle style) => style switch
    {
        TextStyle.Dim => "0;2m",
        TextStyle.Bold => "0;1m",
        TextStyle.Inverse => "0;7m",
        TextStyle.Accent => "0;36m",
        TextStyle.Warn => "0;33m",
        TextStyle.Danger => "0;31m",
        _ => "0m",
    };
}
