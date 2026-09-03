using Mailcoded.Tui.Views;

namespace Mailcoded.Tui;

internal sealed partial class App
{
    /// <summary>Returns false to leave the loop.</summary>
    /// <remarks>Printable commands dispatch on KeyChar: a terminal that has no terminfo entry for
    /// '?' reports ConsoleKey.None, so keying off ConsoleKey loses the binding.</remarks>
    private async Task<bool> HandleKeyAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_state.Prompt is not null)
        {
            if (HandlePrompt(key)) await SubmitAsync(ct).ConfigureAwait(false);
            return true;
        }

        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            switch (key.Key)
            {
                case ConsoleKey.C: return false;
                case ConsoleKey.L: _writer.Measure(); _state.BodyLines = null; return true;
                case ConsoleKey.D: Move(Screen.BodyRows(_writer) / 2); return true;
                case ConsoleKey.U: Move(-Screen.BodyRows(_writer) / 2); return true;
            }
        }

        if (_state.Focus == Pane.Help)
        {
            _state.Focus = _state.Open is null ? Pane.Messages : Pane.Reader;
            return true;
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape: await BackAsync(ct).ConfigureAwait(false); return true;
            case ConsoleKey.Enter: await ActivateAsync(ct).ConfigureAwait(false); return true;
            case ConsoleKey.Tab: Swap(); return true;
            case ConsoleKey.DownArrow: Move(1); return true;
            case ConsoleKey.UpArrow: Move(-1); return true;
            case ConsoleKey.LeftArrow when _state.Open is null: _state.Focus = Pane.Folders; return true;
            case ConsoleKey.RightArrow when _state.Open is null: _state.Focus = Pane.Messages; return true;
            case ConsoleKey.PageDown: Move(Screen.BodyRows(_writer) - 1); return true;
            case ConsoleKey.PageUp: Move(-(Screen.BodyRows(_writer) - 1)); return true;
            case ConsoleKey.Home: Jump(toEnd: false); return true;
            case ConsoleKey.End: Jump(toEnd: true); return true;
        }

        switch (key.KeyChar)
        {
            case 'q':
                if (_state.Open is null) return false;
                CloseReader();
                return true;

            case '?':
                _state.Focus = Pane.Help;
                return true;

            case '/':
                _state.Prompt = "search: ";
                _state.PromptInput = string.Empty;
                return true;

            case 'j': Move(1); return true;
            case 'k': Move(-1); return true;
            case ' ': Move(Screen.BodyRows(_writer) - 1); return true;
            case 'h' when _state.Open is null: _state.Focus = Pane.Folders; return true;
            case 'l' when _state.Open is null: _state.Focus = Pane.Messages; return true;
            case 'g': Jump(toEnd: false); return true;
            case 'G': Jump(toEnd: true); return true;

            case 'n' when _state.Open is null && _state.NextCursor is not null:
                await LoadMessagesAsync(reset: false, ct).ConfigureAwait(false);
                return true;

            case 'r' when _state.Open is null:
                await ResyncAsync(ct).ConfigureAwait(false);
                return true;

            default:
                return true;
        }
    }

    private async Task BackAsync(CancellationToken ct)
    {
        if (_state.Open is not null)
        {
            CloseReader();
            return;
        }

        if (_state.Query is not null)
        {
            _state.Query = null;
            await LoadMessagesAsync(reset: true, ct).ConfigureAwait(false);
        }
    }

    private void CloseReader()
    {
        _state.Open = null;
        _state.BodyLines = null;
        _state.Focus = Pane.Messages;
        _state.Say(string.Empty);
    }

    private void Swap() => _state.Focus = _state.Focus == Pane.Folders ? Pane.Messages : Pane.Folders;

    private async Task ActivateAsync(CancellationToken ct)
    {
        if (_state.Open is not null) return;

        if (_state.Focus == Pane.Folders)
        {
            _state.Query = null;
            _state.Focus = Pane.Messages;
            await LoadMessagesAsync(reset: true, ct).ConfigureAwait(false);
            return;
        }

        await OpenSelectedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns true when the line was submitted.</summary>
    private bool HandlePrompt(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _state.Prompt = null;
                _state.PromptInput = string.Empty;
                return false;

            case ConsoleKey.Enter:
                return true;

            case ConsoleKey.Backspace:
                if (_state.PromptInput.Length > 0) _state.PromptInput = _state.PromptInput[..^1];
                return false;

            default:
                if (!char.IsControl(key.KeyChar) && _state.PromptInput.Length < 256)
                    _state.PromptInput += key.KeyChar;
                return false;
        }
    }

    private async Task SubmitAsync(CancellationToken ct)
    {
        var query = _state.PromptInput.Trim();
        _state.Prompt = null;
        _state.PromptInput = string.Empty;

        _state.Query = query.Length == 0 ? null : query;
        _state.Focus = Pane.Messages;
        await LoadMessagesAsync(reset: true, ct).ConfigureAwait(false);
    }

    private void Move(int delta)
    {
        if (_state.Open is not null)
        {
            _state.BodyScroll = Math.Max(0, _state.BodyScroll + delta);
            return;
        }

        if (_state.Focus == Pane.Folders)
        {
            if (_state.Folders.Count == 0) return;
            _state.FolderIndex = Math.Clamp(_state.FolderIndex + delta, 0, _state.Folders.Count - 1);
            return;
        }

        if (_state.Messages.Count == 0) return;
        _state.MessageIndex = Math.Clamp(_state.MessageIndex + delta, 0, _state.Messages.Count - 1);
    }

    private void Jump(bool toEnd)
    {
        if (_state.Open is not null)
        {
            _state.BodyScroll = toEnd ? int.MaxValue / 2 : 0;
            return;
        }

        if (_state.Focus == Pane.Folders)
        {
            _state.FolderIndex = toEnd ? Math.Max(0, _state.Folders.Count - 1) : 0;
            return;
        }

        _state.MessageIndex = toEnd ? Math.Max(0, _state.Messages.Count - 1) : 0;
    }
}
