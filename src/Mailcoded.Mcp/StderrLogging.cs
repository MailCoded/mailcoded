using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Mailcoded.Mcp;

/// <summary>
/// Every log line goes to stderr: stdout carries MCP protocol frames only (AGENT-INTERFACE §13.5).
/// </summary>
internal sealed class StderrLoggerFactory : ILoggerFactory
{
    public const string LevelEnvVar = "MAILCODED_MCP_LOG";

    private readonly LogLevel _minimum;

    public StderrLoggerFactory(LogLevel minimum) => _minimum = minimum;

    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName, _minimum);

    public void AddProvider(ILoggerProvider provider)
    {
        // Only stderr is a legal sink for this host, so additional providers are ignored.
    }

    public void Dispose()
    {
    }

    public static LogLevel LevelFromEnvironment()
    {
        string? raw;
        try
        {
            raw = Environment.GetEnvironmentVariable(LevelEnvVar);
        }
        catch (Exception)
        {
            return LogLevel.Warning;
        }

        if (string.IsNullOrWhiteSpace(raw)) return LogLevel.Warning;

        return raw.Trim().ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "none" or "off" => LogLevel.None,
            _ => LogLevel.Warning,
        };
    }
}

internal sealed class StderrLogger : ILogger
{
    private readonly string _category;
    private readonly LogLevel _minimum;

    public StderrLogger(string category, LogLevel minimum)
    {
        _category = category;
        _minimum = minimum;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimum;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null) return;

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"[{Abbreviate(logLevel)}] {_category}: {message}");

        Console.Error.WriteLine(line);
        if (exception is not null) Console.Error.WriteLine(exception.ToString());
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}
