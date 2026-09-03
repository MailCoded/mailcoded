using Mailcoded.Protocol;
using Mailcoded.Tui.Views;

namespace Mailcoded.Tui;

internal sealed partial class App
{
    /// <summary>Returns false to leave the loop.</summary>
    /// <remarks>Printable commands dispatch on KeyChar: a terminal with no terminfo entry for '?'
    /// reports ConsoleKey.None, so keying off ConsoleKey loses the binding.</remarks>
    private bool HandleKey(ConsoleKeyInfo key)
    {
        if (_state.Prompt is not null)
        {
            if (ReadPrompt(key)) Submit();
            return true;
        }

        if (_state.ChoosingDestination) return ChooseDestination(key);
        if (_state.Focus == Pane.Confirm) return Confirm(key);
        if (_state.Focus == Pane.Compose) return Edit(key);

        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            switch (key.Key)
            {
                case ConsoleKey.C: return false;
                case ConsoleKey.L: _writer.Measure(); _state.BodyLines = null; return true;
                case ConsoleKey.D: Scroll(Screen.BodyRows(_writer) / 2); return true;
                case ConsoleKey.U: Scroll(-Screen.BodyRows(_writer) / 2); return true;
            }
        }

        if (_state.Focus == Pane.Help)
        {
            _state.Focus = _state.Open is null ? Pane.Messages : Pane.Reader;
            return true;
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape: Escape(); return true;
            case ConsoleKey.Enter: Activate(); return true;
            case ConsoleKey.Tab: Swap(); return true;
            case ConsoleKey.DownArrow: Scroll(1); return true;
            case ConsoleKey.UpArrow: Scroll(-1); return true;
            case ConsoleKey.LeftArrow when _state.Focus == Pane.Folders: Fold(open: false); return true;
            case ConsoleKey.RightArrow when _state.Focus == Pane.Folders: Fold(open: true); return true;
            case ConsoleKey.LeftArrow when _state.Open is null: _state.Focus = Pane.Folders; return true;
            case ConsoleKey.RightArrow when _state.Open is null: _state.Focus = Pane.Messages; return true;
            case ConsoleKey.PageDown: Scroll(Screen.BodyRows(_writer) - 1); return true;
            case ConsoleKey.PageUp: Scroll(-(Screen.BodyRows(_writer) - 1)); return true;
            case ConsoleKey.Home: Jump(toEnd: false); return true;
            case ConsoleKey.End: Jump(toEnd: true); return true;
        }

        switch (key.KeyChar)
        {
            case 'q':
                if (_state.Open is null) return false;
                CloseReader();
                return true;

            case '?': _state.Focus = Pane.Help; return true;
            case '/': Ask("search: "); return true;
            case 't': Ask("tags: "); return true;

            case 'j': Scroll(1); return true;
            case 'k': Scroll(-1); return true;
            case ' ': Scroll(Screen.BodyRows(_writer) - 1); return true;
            case 'h' when _state.Focus == Pane.Folders: Fold(open: false); return true;
            case 'l' when _state.Focus == Pane.Folders: Fold(open: true); return true;
            case 'h' when _state.Open is null: _state.Focus = Pane.Folders; return true;
            case 'l' when _state.Open is null: _state.Focus = Pane.Messages; return true;
            case 'g': Jump(toEnd: false); return true;
            case 'G': Jump(toEnd: true); return true;

            case 'n' when _state.Open is null && _state.NextCursor is not null:
                LoadMessages(reset: false);
                return true;

            case 'r' when _state.Open is null: Resync(); return true;
            case 'c' when _state.Open is null: ComposeNew(); return true;
            case 'r' when _state.Open is not null: ComposeReply(all: false); return true;
            case 'R' when _state.Open is not null: ComposeReply(all: true); return true;
            case 'u': ToggleTag(FlagNames.Unread); return true;
            case 'f': ToggleTag(FlagNames.Flagged); return true;
            case 'a': Archive(); return true;

            case 'p':
                _state.ShowPreview = !_state.ShowPreview;
                _state.PreviewLines = null;
                _state.Say(_state.ShowPreview ? "preview on" : "preview off");
                return true;

            case 'm':
                if (Current() is null) return true;
                _state.ChoosingDestination = true;
                _state.Focus = Pane.Folders;
                _state.Say("move where? enter to confirm, esc to cancel");
                return true;

            default:
                return true;
        }
    }

    /// <summary>Y and only Y sends. Enter is the compose-buffer key, so it must never confirm.</summary>
    private bool Confirm(ConsoleKeyInfo key)
    {
        if (key.KeyChar == ConfirmSend.ConfirmKey) ConfirmedSend();
        else CancelConfirmation();

        return true;
    }

    private bool Edit(ConsoleKeyInfo key)
    {
        if (_state.Draft is not { } draft) { _state.Focus = Pane.Messages; return true; }

        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            switch (key.Key)
            {
                case ConsoleKey.S: PreviewSend(); return true;
                case ConsoleKey.C: Ask("discard the draft? type yes: "); return true;
            }

            return true;
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape: Ask("discard the draft? type yes: "); return true;
            case ConsoleKey.Tab:
                draft.NextField(key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);
                return true;

            case ConsoleKey.Enter:
                if (draft.Field == DraftField.Body) draft.NewLine();
                else draft.NextField(1);
                return true;

            case ConsoleKey.Backspace: draft.Backspace(); return true;
            case ConsoleKey.LeftArrow: draft.MoveCaret(-1, 0); return true;
            case ConsoleKey.RightArrow: draft.MoveCaret(1, 0); return true;
            case ConsoleKey.UpArrow: draft.MoveCaret(0, -1); return true;
            case ConsoleKey.DownArrow: draft.MoveCaret(0, 1); return true;
            case ConsoleKey.Home: draft.MoveCaret(-int.MaxValue / 2, 0); return true;
            case ConsoleKey.End: draft.MoveCaret(int.MaxValue / 2, 0); return true;
        }

        if (!char.IsControl(key.KeyChar)) draft.Insert(key.KeyChar);
        return true;
    }

    private void Ask(string prompt)
    {
        _state.Prompt = prompt;
        _state.PromptInput = string.Empty;
    }

    /// <summary>Escape undoes the innermost thing first: a running call, then the reader, then a search.</summary>
    private void Escape()
    {
        if (Busy) { Cancel(); return; }
        if (_state.Open is not null) { CloseReader(); return; }

        if (_state.Query is not null)
        {
            _state.Query = null;
            LoadMessages(reset: true);
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

    private void Activate()
    {
        if (_state.Open is not null) return;

        if (_state.Focus == Pane.Folders)
        {
            // An account row and a parent with no mail of its own are containers, not destinations.
            if (_state.Row is { IsSelectable: false }) { ToggleFold(); return; }

            _state.Query = null;
            _state.Focus = Pane.Messages;
            LoadMessages(reset: true);
            return;
        }

        OpenSelected();
    }

    /// <summary>Returns true when the line was submitted.</summary>
    private bool ReadPrompt(ConsoleKeyInfo key)
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

    private void Submit()
    {
        var line = _state.PromptInput.Trim();
        var prompt = _state.Prompt;

        _state.Prompt = null;
        _state.PromptInput = string.Empty;

        if (prompt is "discard the draft? type yes: ")
        {
            if (string.Equals(line, "yes", StringComparison.OrdinalIgnoreCase)) Discard();
            return;
        }

        if (prompt is "tags: ")
        {
            PromptedTags(line);
            return;
        }

        _state.Query = line.Length == 0 ? null : line;
        _state.Focus = Pane.Messages;
        LoadMessages(reset: true);
    }

    private bool ChooseDestination(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _state.ChoosingDestination = false;
                _state.Focus = _state.Open is null ? Pane.Messages : Pane.Reader;
                _state.Say(string.Empty);
                return true;

            case ConsoleKey.Enter:
                _state.ChoosingDestination = false;
                if (_state.Folder is { } destination) Move(destination);
                _state.Focus = Pane.Messages;
                return true;

            case ConsoleKey.DownArrow: StepFolder(1); return true;
            case ConsoleKey.UpArrow: StepFolder(-1); return true;
        }

        switch (key.KeyChar)
        {
            case 'j': StepFolder(1); return true;
            case 'k': StepFolder(-1); return true;
            default: return true;
        }
    }

    private void StepFolder(int delta)
    {
        if (_state.Nav.Count == 0) return;
        _state.NavIndex = Math.Clamp(_state.NavIndex + delta, 0, _state.Nav.Count - 1);
    }

    /// <summary>Collapse where a tree collapses and expand where it expands; anything else on those
    /// keys moves to the parent or into the first child, which is what a folder tree does.</summary>
    private void Fold(bool open)
    {
        if (_state.Row is not { } row) return;

        if (open)
        {
            if (row.HasChildren && !row.Expanded) { _state.Collapsed.Remove(row.Key); _state.Rebuild(); }
            else if (row.HasChildren) StepFolder(1);
            return;
        }

        if (row.HasChildren && row.Expanded)
        {
            _state.Collapsed.Add(row.Key);
            _state.Rebuild();
            return;
        }

        for (var i = _state.NavIndex - 1; i >= 0; i--)
        {
            if (_state.Nav[i].Depth >= row.Depth) continue;
            _state.NavIndex = i;
            return;
        }
    }

    private void ToggleFold()
    {
        if (_state.Row is not { HasChildren: true } row) return;

        if (!_state.Collapsed.Remove(row.Key)) _state.Collapsed.Add(row.Key);
        _state.Rebuild();
    }

    private void Scroll(int delta)
    {
        if (_state.Open is not null)
        {
            _state.BodyScroll = Math.Max(0, _state.BodyScroll + delta);
            return;
        }

        if (_state.Focus == Pane.Folders)
        {
            StepFolder(delta);
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
            _state.NavIndex = toEnd ? Math.Max(0, _state.Nav.Count - 1) : 0;
            return;
        }

        _state.MessageIndex = toEnd ? Math.Max(0, _state.Messages.Count - 1) : 0;
    }
}
