namespace Mailcoded.Daemon;

internal enum DaemonMode
{
    Server,
    OneShot,
    Version,
    Help,
}

/// <summary>Parsed argv. Nothing here is ever a credential: <c>secret.set</c> is the only inbound path.</summary>
internal sealed record DaemonOptions
{
    public DaemonMode Mode { get; init; } = DaemonMode.Server;

    public string? StorePath { get; init; }

    public DaemonLogLevel LogLevel { get; init; } = DaemonLogLevel.Info;

    public string? Method { get; init; }

    /// <summary>Raw JSON for the one-shot request. Untrusted; parsed through the Protocol context only.</summary>
    public string? Params { get; init; }

    public bool Transcript { get; init; }

    /// <summary>Set when argv could not be understood; the caller prints it and exits non-zero.</summary>
    public string? UsageError { get; init; }

    public static DaemonOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var mode = DaemonMode.Server;
        string? storePath = null;
        string? method = null;
        string? parameters = null;
        var level = DaemonLogLevel.Info;
        var transcript = DaemonInfo.TranscriptRequestedByEnvironment();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--version":
                    return new DaemonOptions { Mode = DaemonMode.Version, Transcript = transcript };

                case "--help" or "-h":
                    return new DaemonOptions { Mode = DaemonMode.Help, Transcript = transcript };

                case "--transcript":
                    transcript = true;
                    break;

                case "--store":
                    if (!TryTake(args, ref i, out var store)) return Fail("--store needs a path.");
                    storePath = store;
                    break;

                case "--log-level":
                    if (!TryTake(args, ref i, out var raw)) return Fail("--log-level needs a value.");
                    if (!StderrLog.TryParseLevel(raw, out level))
                        return Fail("--log-level must be one of off, error, warn, info, debug, trace.");
                    break;

                case "--one-shot":
                    if (!TryTake(args, ref i, out var name)) return Fail("--one-shot needs a method name.");
                    mode = DaemonMode.OneShot;
                    method = name;
                    break;

                case "--params":
                    if (!TryTake(args, ref i, out var json)) return Fail("--params needs a JSON object.");
                    parameters = json;
                    break;

                default:
                    return Fail($"Unrecognized argument '{Describe(arg)}'.");
            }
        }

        if (mode == DaemonMode.OneShot && string.IsNullOrWhiteSpace(method))
            return Fail("--one-shot needs a method name.");

        if (mode != DaemonMode.OneShot && parameters is not null)
            return Fail("--params is only valid together with --one-shot.");

        return new DaemonOptions
        {
            Mode = mode,
            StorePath = storePath,
            LogLevel = level,
            Method = method,
            Params = parameters,
            Transcript = transcript,
        };
    }

    private static bool TryTake(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static DaemonOptions Fail(string message) => new() { UsageError = message };

    /// <summary>argv is caller-controlled: cap and strip it before it reaches a diagnostic line.</summary>
    private static string Describe(string value)
    {
        var clean = Mailcoded.Core.Application.AuditText.Sanitize(value, 64);
        return string.IsNullOrEmpty(clean) ? "?" : clean;
    }

    public const string HelpText = """
        mailcoded-daemon — local-first email engine, JSON-RPC 2.0 over stdio.

        Usage:
          mailcoded-daemon [--store <path>] [--log-level <level>]
          mailcoded-daemon --one-shot <method> [--params <json>] [--store <path>]
          mailcoded-daemon --version
          mailcoded-daemon --help

        Options:
          --store <path>       Path to store.db. Defaults to the per-OS data directory.
          --log-level <level>  off | error | warn | info | debug | trace. Default: info.
          --one-shot <method>  Serve one request from argv and exit.
          --params <json>      JSON params object for --one-shot.
          --transcript         Deterministic transcript mode for golden-file tests.

        Default mode speaks Content-Length framed JSON-RPC on stdin/stdout.
        Every log line goes to stderr; stdout carries protocol frames only.
        """;
}
