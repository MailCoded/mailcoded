using System.Globalization;
using Mailcoded.Core.Auth;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Removes one account from this machine: its folders, its cached mail, its search index entries
/// and its stored credential. Nothing is removed from the server.
/// </summary>
/// <remarks>
/// Deliberately NOT named delete/expunge/trash/purge and deliberately absent from the MCP tool
/// list. CLAUDE invariant 5 is about mail an agent could destroy; this is account administration,
/// and it is kept behind a human confirmation so it cannot become a way around that.
/// </remarks>
internal static class AccountForgetCommand
{
    public const string OverrideEnvVar = "MAILCODED_ALLOW_FORGET";

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var account = host.RequireAccount(line, ct);

        var counts = host.Store.ListFolders(account.Id, ct);
        var messages = 0;
        foreach (var folder in counts) messages += folder.TotalCount;

        if (!Confirmed(line, account, messages))
        {
            throw new CliUsageException(
                $"Forgetting an account removes its local mail. Re-run from a terminal to confirm, "
                + $"or set {OverrideEnvVar}=1 and pass --yes for an unattended run.");
        }

        var removed = await host.Store.ForgetAccountAsync(account.Id, ct).ConfigureAwait(false);

        if (!removed.Existed)
            throw new CliUsageException($"Account {account.Id.Value} was already gone.");

        // The credential outlives the row unless it is cleared too, and an orphaned grant in the
        // keyring is exactly the kind of thing nobody remembers to clean up later.
        var secrets = 0;
        foreach (var reference in new[] { account.SecretRef, OAuthOptions.CacheRefFor(account.SecretRef) })
        {
            if (string.IsNullOrEmpty(reference)) continue;
            try
            {
                await host.Secrets.DeleteAsync(reference, ct).ConfigureAwait(false);
                secrets++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output.Line($"warning: the credential {reference} could not be cleared: {ex.Message}");
            }
        }

        await host.Audit.WarnAsync(
            "account_forgotten",
            account.Id,
            host.Caller,
            $"email={account.Email} folders={removed.Folders} messages={removed.Messages}",
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("email", account.Email);
            writer.WriteNumber("folders_removed", removed.Folders);
            writer.WriteNumber("messages_removed", removed.Messages);
            writer.WriteNumber("blobs_removed", removed.Blobs);
            writer.WriteNumber("credentials_cleared", secrets);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"Forgot account {account.Id.Value} ({account.Email}).");
        output.Line($"  folders  {removed.Folders.ToString(CultureInfo.InvariantCulture)}");
        output.Line($"  messages {removed.Messages.ToString(CultureInfo.InvariantCulture)}");
        output.Line($"  blobs    {removed.Blobs.ToString(CultureInfo.InvariantCulture)}");
        output.Line("Nothing was removed from the server; re-add the account to sync it again.");
        return ExitCodes.Ok;
    }

    private static bool Confirmed(CommandLine line, AccountConfig account, int messages)
    {
        if (line.Flag("yes"))
        {
            // --yes alone is not enough: an agent can pass a flag, so an unattended run also needs
            // an environment change a human had to make.
            var allowed = Environment.GetEnvironmentVariable(OverrideEnvVar);
            return allowed is "1" or "true" or "yes";
        }

        if (!Prompt.IsInteractive) return false;

        Prompt.Say();
        Prompt.Say($"About to forget account {account.Id.Value}: {account.Email}");
        Prompt.Say($"  {messages.ToString(CultureInfo.InvariantCulture)} locally cached messages and its stored credential will be removed.");
        Prompt.Say("  Nothing is removed from the mail server.");
        Prompt.Say();

        return Prompt.Confirm("Forget it?", false);
    }
}
