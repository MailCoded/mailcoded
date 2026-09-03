using System.Text;

namespace Mailcoded.Tui.Input;

/// <summary>Raw terminal bytes to keys and mouse reports. Incremental: a sequence split across two
/// reads is held until complete, or its bytes would land in a compose buffer as typed text.</summary>
public sealed class InputDecoder
{
    private const int MaxPending = 64;

    private readonly List<byte> _pending = [];

    public IReadOnlyList<InputEvent> Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) _pending.Add(b);

        var events = new List<InputEvent>();

        while (_pending.Count > 0)
        {
            var consumed = Decode(events);

            if (consumed == 0)
            {
                // Incomplete, unless it has grown past anything a real sequence needs.
                if (_pending.Count > MaxPending) _pending.RemoveAt(0);
                else break;

                continue;
            }

            _pending.RemoveRange(0, consumed);
        }

        return events;
    }

    private int Decode(List<InputEvent> events)
    {
        var first = _pending[0];

        if (first != 0x1b)
        {
            var used = Plain(first, events);
            return used;
        }

        if (_pending.Count == 1) return 0;

        if (_pending[1] == '[') return Csi(events);

        if (_pending[1] == 'O' && _pending.Count >= 3) return Ss3(events);

        // ESC followed by anything else is Alt+key; the app has no Alt bindings, so the key wins.
        events.Add(InputEvent.FromKey(Key(ConsoleKey.Escape, '\u001b')));
        return 1;
    }

    private int Plain(byte first, List<InputEvent> events)
    {
        if (first == '\r' || first == '\n')
        {
            events.Add(InputEvent.FromKey(Key(ConsoleKey.Enter, '\r')));
            return 1;
        }

        if (first == '\t')
        {
            events.Add(InputEvent.FromKey(Key(ConsoleKey.Tab, '\t')));
            return 1;
        }

        if (first == 0x7f || first == 0x08)
        {
            events.Add(InputEvent.FromKey(Key(ConsoleKey.Backspace, '\b')));
            return 1;
        }

        if (first < 0x20)
        {
            var letter = (char)('A' + first - 1);
            var known = Enum.TryParse<ConsoleKey>(letter.ToString(), out var parsed) ? parsed : ConsoleKey.None;
            events.Add(InputEvent.FromKey(new ConsoleKeyInfo((char)first, known, false, false, true)));
            return 1;
        }

        var length = Utf8Length(first);
        if (_pending.Count < length) return 0;

        var text = Encoding.UTF8.GetString(_pending.GetRange(0, length).ToArray());
        if (text.Length == 0) return length;

        events.Add(InputEvent.FromKey(new ConsoleKeyInfo(text[0], ConsoleKey.None, false, false, false)));
        return length;
    }

    private int Csi(List<InputEvent> events)
    {
        if (_pending.Count >= 3 && _pending[2] == '<') return SgrMouse(events);

        var end = -1;
        for (var i = 2; i < _pending.Count; i++)
        {
            var c = (char)_pending[i];
            if (c is >= '@' and <= '~') { end = i; break; }
        }

        if (end < 0) return 0;

        var body = Ascii(2, end - 2);
        var final = (char)_pending[end];
        var length = end + 1;

        var key = final switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            'Z' => ConsoleKey.Tab,
            '~' => Tilde(body),
            _ => ConsoleKey.None,
        };

        if (key != ConsoleKey.None)
        {
            var shift = final == 'Z' || body.EndsWith(";2", StringComparison.Ordinal);
            events.Add(InputEvent.FromKey(new ConsoleKeyInfo('\0', key, shift, false, false)));
        }

        return length;
    }

    private int Ss3(List<InputEvent> events)
    {
        var key = (char)_pending[2] switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            _ => ConsoleKey.None,
        };

        if (key != ConsoleKey.None) events.Add(InputEvent.FromKey(Key(key, '\0')));
        return 3;
    }

    /// <summary>SGR encoding (DECSET 1006): CSI &lt; b ; x ; y M|m. The older X10 form caps at
    /// column 223, which a wide terminal exceeds.</summary>
    private int SgrMouse(List<InputEvent> events)
    {
        var end = -1;
        for (var i = 3; i < _pending.Count; i++)
        {
            if (_pending[i] is (byte)'M' or (byte)'m') { end = i; break; }
        }

        if (end < 0) return 0;

        var parts = Ascii(3, end - 3).Split(';');
        var length = end + 1;

        if (parts.Length != 3
            || !int.TryParse(parts[0], out var button)
            || !int.TryParse(parts[1], out var column)
            || !int.TryParse(parts[2], out var row))
        {
            return length;
        }

        var released = _pending[end] == 'm';

        var action = (button & 64) != 0
            ? ((button & 1) == 0 ? MouseAction.WheelUp : MouseAction.WheelDown)
            : released ? MouseAction.Release : MouseAction.Press;

        // A drag report repeats the pressed button with bit 32 set; the app has no drag gesture.
        if ((button & 32) != 0 && (button & 64) == 0) return length;

        events.Add(InputEvent.FromMouse(new MouseEvent
        {
            Action = action,
            Row = Math.Max(0, row - 1),
            Column = Math.Max(0, column - 1),
            Button = button & 3,
        }));

        return length;
    }

    private static ConsoleKey Tilde(string body) => body.Split(';')[0] switch
    {
        "1" or "7" => ConsoleKey.Home,
        "4" or "8" => ConsoleKey.End,
        "3" => ConsoleKey.Delete,
        "5" => ConsoleKey.PageUp,
        "6" => ConsoleKey.PageDown,
        _ => ConsoleKey.None,
    };

    private string Ascii(int start, int count)
    {
        var builder = new StringBuilder(count);
        for (var i = start; i < start + count && i < _pending.Count; i++) builder.Append((char)_pending[i]);
        return builder.ToString();
    }

    private static int Utf8Length(byte first) =>
        first < 0x80 ? 1 : first < 0xE0 ? 2 : first < 0xF0 ? 3 : 4;

    private static ConsoleKeyInfo Key(ConsoleKey key, char c) => new(c, key, false, false, false);
}
