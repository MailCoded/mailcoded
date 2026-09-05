using System.Diagnostics;

namespace Mailcoded.Tui.Input;

/// <summary>Raw mode, so escape sequences reach the decoder rather than the line discipline. Via
/// stty because .NET exposes no termios and its struct layout differs per platform.</summary>
internal sealed class RawMode : IDisposable
{
    private readonly string? _saved;
    private bool _restored;

    private RawMode(string? saved) => _saved = saved;

    public bool IsRaw => _saved is not null;

    public static RawMode Enter()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return new RawMode(null);

        var saved = Stty("-g");
        if (string.IsNullOrWhiteSpace(saved)) return new RawMode(null);

        // min 0 time 1: a read returns empty after 100ms of quiet, which is how a lone Escape is
        // told apart from the start of a cursor sequence.
        return Stty("raw -echo min 0 time 1") is null ? new RawMode(null) : new RawMode(saved.Trim());
    }

    public void Dispose()
    {
        if (_restored || _saved is null) return;
        _restored = true;
        Stty(_saved);
    }

    private static string? Stty(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "stty",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            // Every failure here means the same thing: no raw mode, so no mouse. Naming
            // Win32Exception would pull Microsoft.Win32.Primitives into a client that must
            // reference Mailcoded.Protocol and the BCL alone.
            return null;
        }
    }
}
