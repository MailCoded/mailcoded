using System.Globalization;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>Shows or switches saving drafts to the server. Turning it on is gated like account trust, because the CLI is the agent surface.</summary>
internal static class AccountServerDraftsCommand
{
    public const string OverrideEnvVar = "MAILCODED_ALLOW_SERVER_DRAFTS";

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var account = host.RequireAccount(line, ct);
        var changed = false;

        switch (line.Positional.Count > 0 ? line.Positional[0] : "show")
        {
            case "show":
                break;

            case "on":
                if (!Confirmed(line, account))
                {
                    throw new CliUsageException(
                        "Saving drafts to the server puts every new draft in the Drafts Folder your other mail clients show. "
                        + $"Re-run from a terminal to confirm, or set {OverrideEnvVar}=1 and pass --yes for an unattended run.");
                }

                changed = await host.Accounts.SetSaveDraftsToServerAsync(account.Id, true, host.Caller, ct).ConfigureAwait(false);
                break;

            case "off":
                changed = await host.Accounts.SetSaveDraftsToServerAsync(account.Id, false, host.Caller, ct).ConfigureAwait(false);
                break;

            default:
                throw new CliUsageException("'account server-drafts' takes 'on', 'off' or nothing to show the setting.");
        }

        account = host.Store.GetAccount(account.Id, ct) ?? account;

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("email", account.Email);
            writer.WriteBoolean("save_drafts_to_server", account.SaveDraftsToServer);
            writer.WriteBoolean("changed", changed);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"account {account.Id.Value.ToString(CultureInfo.InvariantCulture)}  {account.Email}");
        output.Line($"  drafts   {(account.SaveDraftsToServer ? "also saved to the server's Drafts Folder" : "kept locally only")}");
        if (changed) output.Line("Saved.");
        return ExitCodes.Ok;
    }

    private static bool Confirmed(CommandLine line, AccountConfig account)
    {
        if (line.Flag("yes"))
        {
            var allowed = Environment.GetEnvironmentVariable(OverrideEnvVar);
            return allowed is "1" or "true" or "yes";
        }

        if (!Prompt.IsInteractive) return false;

        Prompt.Say();
        Prompt.Say($"About to save drafts of account {account.Id.Value} ({account.Email}) to its server's Drafts Folder.");
        Prompt.Say("  Other mail clients will show them. After a send the copy is moved to Sent when the server");
        Prompt.Say("  can do that safely, and left in Drafts otherwise. Nothing is ever deleted.");
        Prompt.Say();

        return Prompt.Confirm("Turn it on?", false);
    }
}
