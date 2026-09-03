namespace Mailcoded.Tui.Render;

/// <summary>The only type TerminalWriter accepts, so no string reaches the screen unfiltered.</summary>
public readonly struct SafeSpan
{
    internal SafeSpan(string text, int columns)
    {
        Text = text;
        Columns = columns;
    }

    public string Text { get; }

    public int Columns { get; }

    public static SafeSpan Empty { get; } = new(string.Empty, 0);

    /// <summary>Literal authored in this repository. Never call this with mail-derived text.</summary>
    internal static SafeSpan Chrome(string literal) => TerminalText.Chrome(literal, literal.Length);

    public override string ToString() => Text;
}
