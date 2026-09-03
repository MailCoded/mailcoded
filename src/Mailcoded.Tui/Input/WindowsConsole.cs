using System.Runtime.InteropServices;

namespace Mailcoded.Tui.Input;

internal static class WindowsConsole
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>Without this every escape this program writes is printed literally on conhost.</summary>
    public static bool TryEnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return true;

        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        if (!GetConsoleMode(handle, out var mode)) return false;
        if ((mode & EnableVirtualTerminalProcessing) != 0) return true;

        return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
