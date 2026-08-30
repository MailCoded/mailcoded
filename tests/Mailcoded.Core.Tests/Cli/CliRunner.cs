using System.Reflection;
using System.Text;
using Mailcoded.Cli;
using Mailcoded.Cli.Commands;

namespace Mailcoded.Core.Tests.Cli;

internal readonly record struct CliRun(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs a CLI verb in process; the binary may not exist yet when the tests run.</summary>
internal static class CliRunner
{
    public static async Task<CliRun> RunAsync(
        string databasePath,
        string verb,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        bool json = true)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var tokens = new List<string>(arguments) { "--db", databasePath };
        if (json) tokens.Add("--json");

        using var capture = new CapturedOutput(json);

        try
        {
            var command = CommandTable.Find(verb)
                ?? throw new CliUsageException($"'{verb}' is not a mailcoded verb.");

            var line = CommandLine.Parse(verb, tokens, command.Spec);
            capture.Output.Configure(verb, line.Flag("json"));

            await using var host = CliHost.Create(line);
            var exitCode = await command.Run(host, line, capture.Output, ct).ConfigureAwait(false);

            capture.Output.Flush();
            return new CliRun(exitCode, capture.StandardOutput, capture.StandardError);
        }
        catch (Exception exception)
        {
            var exitCode = capture.Output.WriteError(CliError.From(exception));
            capture.Output.Flush();
            return new CliRun(exitCode, capture.StandardOutput, capture.StandardError);
        }
    }

    /// <summary>CliOutput binds the process stdout handle, which no managed API can redirect.</summary>
    private sealed class CapturedOutput : IDisposable
    {
        private readonly MemoryStream _stdout = new();
        private readonly MemoryStream _stderr = new();

        public CapturedOutput(bool json)
        {
            Output = new CliOutput(json);
            Redirect("_stdout", "_text", _stdout);
            Redirect("_stderr", "_errorText", _stderr);
        }

        public CliOutput Output { get; }

        public string StandardOutput => Encoding.UTF8.GetString(_stdout.ToArray());

        public string StandardError => Encoding.UTF8.GetString(_stderr.ToArray());

        private void Redirect(string streamField, string writerField, MemoryStream target)
        {
            Field(streamField).SetValue(Output, target);
            Field(writerField).SetValue(
                Output,
                new StreamWriter(target, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = false,
                });
        }

        private static FieldInfo Field(string name) =>
            typeof(CliOutput).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"CliOutput.{name} no longer exists; update the in-process CLI capture helper.");

        public void Dispose() => Output.Dispose();
    }
}
