using System.Text;

namespace Mailcoded.Tui;

internal enum DraftField
{
    To,
    Cc,
    Bcc,
    Subject,
    Body,
}

/// <summary>A composed message being edited. Plain text only: v0.1 has no HTML compose.</summary>
internal sealed class DraftBuffer
{
    private const int MaxHeaderChars = 998;
    private const int MaxBodyChars = 256 * 1024;

    private readonly string[] _headers = ["", "", "", ""];

    public List<string> Body { get; } = [string.Empty];

    public DraftField Field { get; set; } = DraftField.To;

    public int HeaderCaret { get; set; }

    public int BodyLine { get; set; }

    public int BodyColumn { get; set; }

    public long? ReplyToMessageId { get; init; }

    public string? InReplyTo { get; init; }

    public IReadOnlyList<string> References { get; init; } = [];

    public string To { get => _headers[0]; set => _headers[0] = value; }

    public string Cc { get => _headers[1]; set => _headers[1] = value; }

    public string Bcc { get => _headers[2]; set => _headers[2] = value; }

    public string Subject { get => _headers[3]; set => _headers[3] = value; }

    public string Header(DraftField field) => field == DraftField.Body ? string.Empty : _headers[(int)field];

    public string BodyText => string.Join('\n', Body);

    public int TotalChars => _headers.Sum(h => h.Length) + BodyText.Length;

    public static IReadOnlyList<string> SplitAddresses(string raw) =>
        raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Insert(char c)
    {
        if (Field == DraftField.Body)
        {
            if (BodyText.Length >= MaxBodyChars) return;

            var line = Body[BodyLine];
            var at = Math.Clamp(BodyColumn, 0, line.Length);
            Body[BodyLine] = line.Insert(at, c.ToString());
            BodyColumn = at + 1;
            return;
        }

        var index = (int)Field;
        if (_headers[index].Length >= MaxHeaderChars) return;

        var caret = Math.Clamp(HeaderCaret, 0, _headers[index].Length);
        _headers[index] = _headers[index].Insert(caret, c.ToString());
        HeaderCaret = caret + 1;
    }

    public void Backspace()
    {
        if (Field != DraftField.Body)
        {
            var index = (int)Field;
            var caret = Math.Clamp(HeaderCaret, 0, _headers[index].Length);
            if (caret == 0) return;

            _headers[index] = _headers[index].Remove(caret - 1, 1);
            HeaderCaret = caret - 1;
            return;
        }

        if (BodyColumn > 0)
        {
            var line = Body[BodyLine];
            Body[BodyLine] = line.Remove(BodyColumn - 1, 1);
            BodyColumn--;
            return;
        }

        if (BodyLine == 0) return;

        var previous = Body[BodyLine - 1];
        BodyColumn = previous.Length;
        Body[BodyLine - 1] = previous + Body[BodyLine];
        Body.RemoveAt(BodyLine);
        BodyLine--;
    }

    public void NewLine()
    {
        if (Field != DraftField.Body || BodyText.Length >= MaxBodyChars) return;

        var line = Body[BodyLine];
        var at = Math.Clamp(BodyColumn, 0, line.Length);

        Body[BodyLine] = line[..at];
        Body.Insert(BodyLine + 1, line[at..]);
        BodyLine++;
        BodyColumn = 0;
    }

    public void MoveCaret(int columns, int lines)
    {
        if (Field != DraftField.Body)
        {
            HeaderCaret = Math.Clamp(HeaderCaret + columns, 0, Header(Field).Length);
            return;
        }

        if (lines != 0)
        {
            BodyLine = Math.Clamp(BodyLine + lines, 0, Body.Count - 1);
            BodyColumn = Math.Min(BodyColumn, Body[BodyLine].Length);
            return;
        }

        BodyColumn = Math.Clamp(BodyColumn + columns, 0, Body[BodyLine].Length);
    }

    public void NextField(int delta)
    {
        var next = (int)Field + delta;
        Field = (DraftField)Math.Clamp(next, 0, (int)DraftField.Body);
        HeaderCaret = Header(Field).Length;
    }

    public void SetBody(string text)
    {
        Body.Clear();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) Body.Add(line);
        if (Body.Count == 0) Body.Add(string.Empty);
    }

    /// <summary>The quoted form of a message being replied to, capped so a thread cannot grow unbounded.</summary>
    public static string Quote(string? author, string? body, int maxLines)
    {
        var builder = new StringBuilder();
        builder.Append('\n').Append('\n');

        if (!string.IsNullOrWhiteSpace(author)) builder.Append("On an earlier message, ").Append(author).Append(" wrote:\n");

        var lines = 0;
        foreach (var line in (body ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (lines++ >= maxLines) { builder.Append("> [...]\n"); break; }
            builder.Append("> ").Append(line).Append('\n');
        }

        return builder.ToString();
    }
}
