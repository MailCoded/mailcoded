using System.Globalization;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>Shows or changes an Account's trusted authserv-ids. Changing them is gated like account forget, so an injected instruction cannot widen what verified means.</summary>
internal static class AccountTrustCommand
{
    public const string OverrideEnvVar = "MAILCODED_ALLOW_TRUST_CHANGE";

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var account = host.RequireAccount(line, ct);

        var requested = AuthservOptions.Read(line, "authserv-id");
        var clear = line.Flag("none");

        if (clear && requested.Count > 0)
            throw new CliUsageException("Pass either --authserv-id or --none, not both.");

        var changing = clear || requested.Count > 0;
        var changed = false;

        if (changing)
        {
            if (!AuthservOptions.Confirmed(line, $"account {account.Id.Value}: {account.Email}", requested))
            {
                throw new CliUsageException(
                    "Changing the trusted authserv-ids changes what 'verified' means. Re-run from a terminal to "
                    + $"confirm, or set {OverrideEnvVar}=1 and pass --yes for an unattended run.");
            }

            changed = await host.Accounts.SetTrustedAuthservIdsAsync(account.Id, requested, host.Caller, ct).ConfigureAwait(false);

            account = host.Store.GetAccount(account.Id, ct) ?? account;
        }

        var preset = ProviderPresets.TrustedAuthservIdsForImapHost(account.Imap.Host).Patterns;
        var effective = account.EffectiveAuthservTrust().Patterns;

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("email", account.Email);
            WriteList(writer, "configured", account.TrustedAuthservIds);
            WriteList(writer, "from_provider", preset);
            WriteList(writer, "effective", effective);
            writer.WriteBoolean("changed", changed);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"account {account.Id.Value.ToString(CultureInfo.InvariantCulture)}  {account.Email}");
        output.Line($"  configured     {Describe(account.TrustedAuthservIds)}");
        output.Line($"  from provider  {Describe(preset)}");
        output.Line($"  trusted        {Describe(effective)}");

        if (changed)
        {
            output.Blank();
            output.Line("Recorded DMARC results for this account were cleared. A message whose body is cached gets a");
            output.Line("result again when it is next read; other mail gets one when it is synced or opened.");
        }
        else if (changing)
        {
            output.Blank();
            output.Line("The list was already set to this; nothing changed.");
        }
        else if (effective.Count == 0)
        {
            output.Blank();
            output.Line("No authserv-id is trusted, so no message can be verified. Find your server's id in the");
            output.Line("first word after 'Authentication-Results:' in a message you trust, then pass --authserv-id.");
        }

        return ExitCodes.Ok;
    }

    private static string Describe(IReadOnlyList<string> ids) => ids.Count == 0 ? "(none)" : string.Join(", ", ids);

    private static void WriteList(System.Text.Json.Utf8JsonWriter writer, string name, IReadOnlyList<string> ids)
    {
        writer.WriteStartArray(name);
        foreach (var id in ids) writer.WriteStringValue(id);
        writer.WriteEndArray();
    }
}
