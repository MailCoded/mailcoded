using System.Globalization;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>Lists, adds and forgets the extra From addresses an Account may send as. Adding is gated like account trust: an identity widens who a message can claim to be from.</summary>
internal static class AccountIdentityCommand
{
    public const string OverrideEnvVar = "MAILCODED_ALLOW_IDENTITY_CHANGE";

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        var action = line.RequirePositional(0, "an action: list, add or forget");
        var account = host.RequireAccount(line, ct);
        var changed = false;

        switch (action)
        {
            case "list":
                line.RejectExtraPositional(1);
                break;

            case "add":
            {
                line.RejectExtraPositional(2);
                var address = line.RequirePositional(1, "the address to send as");

                if (!Confirmed(line, account, address))
                {
                    throw new CliUsageException(
                        "Adding an identity widens which addresses mail can be sent as. Re-run from a terminal to "
                        + $"confirm, or set {OverrideEnvVar}=1 and pass --yes for an unattended run.");
                }

                changed = await host.Accounts
                    .AddIdentityAsync(account.Id, address, line.Value("name"), line.Value("reply-to"), host.Caller, ct)
                    .ConfigureAwait(false);
                break;
            }

            case "forget":
                line.RejectExtraPositional(2);
                changed = await host.Accounts
                    .ForgetIdentityAsync(account.Id, line.RequirePositional(1, "the address to forget"), host.Caller, ct)
                    .ConfigureAwait(false);
                break;

            default:
                throw new CliUsageException($"'account identity {SafeText.Line(action, 40)}' does not exist. Actions: list, add, forget.");
        }

        account = host.Store.GetAccount(account.Id, ct) ?? account;
        return Write(output, account, action, changed);
    }

    private static int Write(CliOutput output, AccountConfig account, string action, bool changed)
    {
        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("email", account.Email);
            writer.WriteStartArray("identities");
            foreach (var identity in account.Identities)
            {
                writer.WriteStartObject();
                writer.WriteString("address", identity.Address.Value);
                JsonFields.WriteText(writer, "display_name", identity.DisplayName);
                JsonFields.WriteText(writer, "reply_to", identity.ReplyTo?.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("changed", changed);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"account {account.Id.Value.ToString(CultureInfo.InvariantCulture)}  {account.Email}");
        output.Line($"  always     {account.Email}");
        foreach (var identity in account.Identities)
        {
            var extras = new List<string>();
            if (identity.DisplayName is { } name) extras.Add($"name \"{SafeText.Line(name, 100)}\"");
            if (identity.ReplyTo is { } reply) extras.Add($"reply-to {reply.Value}");
            output.Line($"  identity   {identity.Address.Value}{(extras.Count > 0 ? "  (" + string.Join(", ", extras) + ")" : string.Empty)}");
        }

        if (action != "list")
        {
            output.Blank();
            output.Line(changed ? "Saved." : "Nothing changed.");
        }
        else if (account.Identities.Count == 0)
        {
            output.Blank();
            output.Line("No extra identities. Add one with: mailcoded account identity add <address> [--name <text>] [--reply-to <address>]");
        }

        return ExitCodes.Ok;
    }

    private static bool Confirmed(CommandLine line, AccountConfig account, string address)
    {
        if (line.Flag("yes"))
        {
            var allowed = Environment.GetEnvironmentVariable(OverrideEnvVar);
            return allowed is "1" or "true" or "yes";
        }

        if (!Prompt.IsInteractive) return false;

        Prompt.Say();
        Prompt.Say($"About to let account {account.Id.Value} ({account.Email}) send as: {SafeText.Line(address, 254)}");
        Prompt.Say("  Drafts may then use this address in From. Your mail server still decides whether it accepts it.");
        Prompt.Say();

        return Prompt.Confirm("Apply it?", false);
    }
}
