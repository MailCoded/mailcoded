namespace Mailcoded.Tui.Input;

public enum MouseAction
{
    Press,
    Release,
    WheelUp,
    WheelDown,
}

public readonly record struct MouseEvent
{
    public required MouseAction Action { get; init; }

    /// <summary>Zero-based, already converted from the terminal's one-based report.</summary>
    public required int Row { get; init; }

    public required int Column { get; init; }

    public int Button { get; init; }
}

/// <summary>Either a key or a mouse report. Both arrive on the same stdin, so one decoder owns both.</summary>
public readonly record struct InputEvent
{
    public ConsoleKeyInfo? Key { get; init; }

    public MouseEvent? Mouse { get; init; }

    public static InputEvent FromKey(ConsoleKeyInfo key) => new() { Key = key };

    public static InputEvent FromMouse(MouseEvent mouse) => new() { Mouse = mouse };
}
