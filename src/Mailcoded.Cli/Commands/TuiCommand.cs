using System.Diagnostics;
using Mailcoded.Protocol;

namespace Mailcoded.Cli.Commands;

/// <summary>Hands over to the mailcoded-tui binary rather than linking it: the TUI compiles
/// against Mailcoded.Protocol alone, and this assembly is on the wrong side of that line.</summary>
internal static class TuiCommand
{
    public const string ExecutableName = "mailcoded-tui";
    private const string DaemonName = "mailcoded-daemon";

    public static int Run(IReadOnlyList<string> tokens, CliOutput output)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(output);

        if (Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            return output.WriteError(new CliError
            {
                Code = RpcErrorCode.Unsupported,
                Name = "unsupported",
                ExitCode = ExitCodes.Unsupported,
                Message = "The TUI needs a terminal; stdin or stdout is redirected.",
                Hint = "For scripted and agent use the one-shot verbs are the interface: "
                    + "search, read, thread, folders.",
            });
        }

        if (Locate(ExecutableName) is not { } tui)
        {
            return output.WriteError(new CliError
            {
                Code = RpcErrorCode.NotFound,
                Name = "not_found",
                ExitCode = ExitCodes.NotFound,
                Message = $"'{ExecutableName}' was not found beside this binary or on PATH.",
                Hint = "Install it with scripts/install.sh, or run the build output directly.",
            });
        }

        var info = new ProcessStartInfo { FileName = tui, UseShellExecute = false };

        // The TUI finds its own daemon, but a sibling of the CLI is the one the user installed.
        if (Locate(DaemonName) is { } daemon) info.Environment["MAILCODED_DAEMON"] = daemon;

        if (Value(tokens, "data-dir") is { } directory) info.Environment["MAILCODED_DATA_DIR"] = directory;

        if (Value(tokens, "db") is { } database)
        {
            info.ArgumentList.Add("--store");
            info.ArgumentList.Add(database);
        }

        output.Flush();

        using var process = Process.Start(info);
        if (process is null) return ExitCodes.Internal;

        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>A sibling first: an installed set belongs together, and PATH may hold an older one.</summary>
    private static string? Locate(string name)
    {
        if (Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } directory)
        {
            foreach (var candidate in new[] { name, name + ".exe" })
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path)) return path;
            }
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in new[] { name, name + ".exe" })
            {
                var path = Path.Combine(entry.Trim(), candidate);
                if (File.Exists(path)) return path;
            }
        }

        return null;
    }

    private static string? Value(IReadOnlyList<string> tokens, string name)
    {
        var prefix = "--" + name;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].StartsWith(prefix + "=", StringComparison.Ordinal))
                return tokens[i][(prefix.Length + 1)..];

            if (string.Equals(tokens[i], prefix, StringComparison.Ordinal) && i + 1 < tokens.Count)
                return tokens[i + 1];
        }

        return null;
    }
}
