using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Signals;

namespace Mailcoded.Cli.Commands;

internal static class AuthservOptions
{
    public static IReadOnlyList<string> Read(CommandLine line, string option)
    {
        ArgumentNullException.ThrowIfNull(line);

        var ids = new List<string>();

        foreach (var raw in line.Values(option))
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new CliUsageException($"--{option} needs a value.");

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!AuthservTrust.TryNormalize(part, out var pattern))
                {
                    throw new CliUsageException(
                        $"--{option} '{SafeText.Line(part, 60)}' is not a hostname or a '*.suffix' pattern. "
                        + "Use the name that appears right after 'Authentication-Results:', for example mx.example.net.");
                }

                if (!ids.Contains(pattern)) ids.Add(pattern);
            }
        }

        if (ids.Count > AuthservTrust.MaxPatterns)
            throw new CliUsageException($"At most {AuthservTrust.MaxPatterns} authserv-ids can be trusted for one account.");

        return ids;
    }

    public static bool Confirmed(CommandLine line, string subject, IReadOnlyList<string> requested)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Flag("yes"))
        {
            var allowed = Environment.GetEnvironmentVariable(AccountTrustCommand.OverrideEnvVar);
            return allowed is "1" or "true" or "yes";
        }

        if (!Prompt.IsInteractive) return false;

        Prompt.Say();
        Prompt.Say($"About to trust {(requested.Count == 0 ? "no authserv-id" : string.Join(", ", requested))} for {subject}");
        Prompt.Say("  Mail whose Authentication-Results names one of these will be able to show as verified.");
        Prompt.Say("  Only list names your own mail server writes and strips from incoming mail.");
        Prompt.Say();

        return Prompt.Confirm("Apply it?", false);
    }

    public static void RequireConfirmed(CommandLine line, string subject, IReadOnlyList<string> requested)
    {
        if (requested.Count == 0 || Confirmed(line, subject, requested)) return;

        throw new CliUsageException(
            "Trusting an authserv-id changes what 'verified' means. Re-run from a terminal to confirm, or set "
            + $"{AccountTrustCommand.OverrideEnvVar}=1 and pass --yes for an unattended run.");
    }

    public static void NoteIfIgnored(CliHost host, AccountId account, IReadOnlyList<string> requested, CancellationToken ct)
    {
        if (requested.Count == 0) return;

        var stored = host.Store.GetAccount(account, ct);
        if (stored is null || stored.TrustedAuthservIds.SequenceEqual(requested, StringComparer.Ordinal)) return;

        Prompt.Say("note: this account already existed, so its trusted authserv-ids were left as they were. "
            + "Change them with 'mailcoded account trust'.");
    }
}
