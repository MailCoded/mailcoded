using Mailcoded.Cli.Commands;

namespace Mailcoded.Cli;

/// <summary>
/// One shot per invocation: parse, run against Mailcoded.Core in process, print, exit. No daemon
/// is spawned and no business logic lives here — every gate is enforced inside Core.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += onCancel;
        using var output = new CliOutput(LooksLikeJson(args));

        try
        {
            return await RunAsync(args, output, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return output.WriteError(CliError.From(exception));
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            output.Flush();
        }
    }

    private static async Task<int> RunAsync(string[] args, CliOutput output, CancellationToken ct)
    {
        var tokens = new List<string>(args);
        if (tokens.Count == 0)
        {
            output.WriteHelp(HelpText.Overview);
            return ExitCodes.Ok;
        }

        var hoisted = new List<string>();
        HoistGlobals(tokens, hoisted);

        if (tokens.Count == 0 || tokens[0] is "help" or "--help" or "-h")
        {
            var topic = tokens.Count > 1 ? tokens[1] : null;
            output.WriteHelp(HelpText.For(NormalizeTopic(topic)));
            return ExitCodes.Ok;
        }

        var verb = tokens[0];
        tokens.RemoveAt(0);
        if (verb is "--version" or "-v") verb = "version";

        if (verb == "account")
        {
            if (tokens.Count == 0)
                throw new CliUsageException("'account' needs a subcommand; the only one is 'account add'.");

            var sub = tokens[0];
            tokens.RemoveAt(0);

            if (sub is "-h" or "--help")
            {
                output.WriteHelp(HelpText.For("account"));
                return ExitCodes.Ok;
            }

            if (sub is not ("add" or "forget" or "list" or "test" or "reauth"))
            {
                throw new CliUsageException(
                    $"'account {sub}' does not exist. Subcommands: list, add, test, reauth, forget.");
            }

            verb = "account " + sub;
        }

        output.Configure(verb, output.Json);
        tokens.AddRange(hoisted);

        if (verb == "version") return VersionCommand.Run(output);

        var command = CommandTable.Find(verb)
            ?? throw new CliUsageException(
                $"'{verb}' is not a mailcoded verb. Available: {CommandTable.Names()}. "
                + "There is no delete, expunge, trash or purge verb, and no flag adds one. "
                + "Run 'mailcoded help'.");

        var line = CommandLine.Parse(verb, tokens, command.Spec);
        output.Configure(verb, line.Flag("json"));

        if (line.Flag("help"))
        {
            output.WriteHelp(HelpText.For(verb));
            return ExitCodes.Ok;
        }

        await using var host = CliHost.Create(line);
        return await command.Run(host, line, output, ct).ConfigureAwait(false);
    }

    /// <summary>Accepts <c>mailcoded --json search ...</c> as well as the documented trailing form.</summary>
    private static void HoistGlobals(List<string> tokens, List<string> hoisted)
    {
        while (tokens.Count > 0 && tokens[0].StartsWith("--", StringComparison.Ordinal))
        {
            var name = tokens[0][2..];
            var equals = name.IndexOf('=');
            var bare = equals >= 0 ? name[..equals] : name;

            if (bare is "json" or "quiet")
            {
                hoisted.Add(tokens[0]);
                tokens.RemoveAt(0);
                continue;
            }

            if (bare is "data-dir" or "db")
            {
                hoisted.Add(tokens[0]);
                tokens.RemoveAt(0);
                if (equals < 0 && tokens.Count > 0)
                {
                    hoisted.Add(tokens[0]);
                    tokens.RemoveAt(0);
                }

                continue;
            }

            break;
        }
    }

    private static string? NormalizeTopic(string? topic) => topic == "account" ? "account add" : topic;

    private static bool LooksLikeJson(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg is "--json" or "--json=true" or "--json=1") return true;
        }

        return false;
    }
}
