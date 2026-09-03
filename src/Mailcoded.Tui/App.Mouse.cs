using Mailcoded.Tui.Input;
using Mailcoded.Tui.Views;

namespace Mailcoded.Tui;

internal sealed partial class App
{
    /// <summary>Two clicks on the same row within this window open it, the way a list does.</summary>
    private static readonly TimeSpan DoubleClick = TimeSpan.FromMilliseconds(400);

    private long _lastClickTicks;
    private int _lastClickRow = -1;
    private int _lastClickColumn = -1;

    private bool HandleMouse(MouseEvent mouse)
    {
        // The key bar is clickable in every mode, including the ones that ignore the body: it is
        // the thing standing in for remembering the bindings.
        if (mouse.Action == MouseAction.Press
            && mouse.Button == 0
            && mouse.Row == Screen.KeyBarRow(_writer))
        {
            if (KeyBar.At(_state, _writer.Columns, mouse.Column)?.Press is { } press) return HandleKey(press);
            return true;
        }

        if (_state.Focus is Pane.Help or Pane.Status or Pane.Outbox or Pane.Confirm or Pane.Compose) return true;

        var top = Screen.FirstBodyRow;
        var rows = Screen.BodyRows(_writer);

        if (mouse.Row < top || mouse.Row >= top + rows) return true;

        var layout = Layout.For(_writer.Columns, _state.ShowPreview);
        var line = mouse.Row - top;

        if (mouse.Action is MouseAction.WheelUp or MouseAction.WheelDown)
        {
            Wheel(mouse, layout, mouse.Action == MouseAction.WheelDown ? 3 : -3);
            return true;
        }

        if (mouse.Action != MouseAction.Press || mouse.Button != 0) return true;

        switch (layout.PaneAt(mouse.Column))
        {
            case Pane.Folders: ClickFolder(line); break;
            case Pane.Messages: ClickMessage(line, mouse); break;
            default: _state.Focus = Pane.Messages; break;
        }

        return true;
    }

    private void Wheel(MouseEvent mouse, Layout layout, int delta)
    {
        if (_state.Open is not null)
        {
            _state.BodyScroll = Math.Max(0, _state.BodyScroll + delta);
            return;
        }

        if (layout.PaneAt(mouse.Column) == Pane.Folders)
        {
            StepFolder(delta);
            return;
        }

        if (_state.Messages.Count == 0) return;
        _state.MessageIndex = Math.Clamp(_state.MessageIndex + delta, 0, _state.Messages.Count - 1);
    }

    private void ClickFolder(int line)
    {
        var index = _state.NavScroll + line;
        if (index < 0 || index >= _state.Nav.Count) return;

        _state.NavIndex = index;
        _state.Focus = Pane.Folders;

        if (_state.ChoosingDestination) return;

        // A container has nothing to open, so the click that lands on one folds it instead.
        if (_state.Row is { IsSelectable: false }) { ToggleFold(); return; }

        _state.Query = null;
        _state.Focus = Pane.Messages;
        LoadMessages(reset: true);
    }

    private void ClickMessage(int line, MouseEvent mouse)
    {
        var index = _state.MessageScroll + line;
        if (index < 0 || index >= _state.Messages.Count) return;

        _state.Focus = Pane.Messages;
        _state.MessageIndex = index;

        if (IsDoubleClick(mouse)) OpenSelected();
    }

    private bool IsDoubleClick(MouseEvent mouse)
    {
        var now = Environment.TickCount64;
        var repeat = _lastClickRow == mouse.Row
            && _lastClickColumn == mouse.Column
            && now - _lastClickTicks <= (long)DoubleClick.TotalMilliseconds;

        _lastClickTicks = now;
        _lastClickRow = mouse.Row;
        _lastClickColumn = mouse.Column;

        return repeat;
    }
}
