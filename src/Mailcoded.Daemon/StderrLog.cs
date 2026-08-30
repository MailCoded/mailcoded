using System.Globalization;
using Mailcoded.Core.Application;

namespace Mailcoded.Daemon;

internal enum DaemonLogLevel
{
    Off = 0,
    Error,
    Warn,
    Info,
    Debug,
    Trace,
}

/// <summary>
/// Every diagnostic line in the process goes here. stdout is reserved for protocol frames, so this
/// writer only ever touches stderr.
/// </summary>
internal sealed class StderrLog
{
    private readonly Lock gate = new();
    private readonly TextWriter writer;
    private readonly DaemonLogLevel level;
    private readonly bool timestamps;

    public StderrLog(TextWriter writer, DaemonLogLevel level, bool timestamps)
    {
        ArgumentNullException.ThrowIfNull(writer);

        this.writer = writer;
        this.level = level;
        this.timestamps = timestamps;
    }

    public DaemonLogLevel Level => level;

    public bool IsEnabled(DaemonLogLevel value) => value <= level && level != DaemonLogLevel.Off;

    public void Error(string message) => Write(DaemonLogLevel.Error, message);

    public void Warn(string message) => Write(DaemonLogLevel.Warn, message);

    public void Info(string message) => Write(DaemonLogLevel.Info, message);

    public void Debug(string message) => Write(DaemonLogLevel.Debug, message);

    public void Trace(string message) => Write(DaemonLogLevel.Trace, message);

    /// <summary>The only place a stack trace is emitted. It reaches stderr and never an RPC response.</summary>
    public void Exception(string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!IsEnabled(DaemonLogLevel.Error)) return;

        lock (gate)
        {
            WriteLine(DaemonLogLevel.Error, message);
            writer.WriteLine(exception.ToString());
            writer.Flush();
        }
    }

    private void Write(DaemonLogLevel value, string message)
    {
        if (!IsEnabled(value)) return;

        lock (gate)
        {
            WriteLine(value, message);
            writer.Flush();
        }
    }

    private void WriteLine(DaemonLogLevel value, string message)
    {
        if (timestamps)
        {
            writer.Write(DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            writer.Write(' ');
        }

        writer.Write('[');
        writer.Write(Label(value));
        writer.Write("] ");
        writer.WriteLine(AuditText.Sanitize(message, 1024) ?? string.Empty);
    }

    private static string Label(DaemonLogLevel value) => value switch
    {
        DaemonLogLevel.Error => "error",
        DaemonLogLevel.Warn => "warn",
        DaemonLogLevel.Info => "info",
        DaemonLogLevel.Debug => "debug",
        _ => "trace",
    };

    public static bool TryParseLevel(string? raw, out DaemonLogLevel value)
    {
        value = DaemonLogLevel.Info;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "off" or "none" or "silent": value = DaemonLogLevel.Off; return true;
            case "error": value = DaemonLogLevel.Error; return true;
            case "warn" or "warning": value = DaemonLogLevel.Warn; return true;
            case "info": value = DaemonLogLevel.Info; return true;
            case "debug": value = DaemonLogLevel.Debug; return true;
            case "trace" or "verbose": value = DaemonLogLevel.Trace; return true;
            default: return false;
        }
    }
}
