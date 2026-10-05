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

    public static void NoteIfIgnored(CliHost host, AccountId account, IReadOnlyList<string> requested, CancellationToken ct)
    {
        if (requested.Count == 0) return;

        var stored = host.Store.GetAccount(account, ct);
        if (stored is null || stored.TrustedAuthservIds.SequenceEqual(requested, StringComparer.Ordinal)) return;

        Prompt.Say("note: this account already existed, so its trusted authserv-ids were left as they were. "
            + "Change them with 'mailcoded account trust'.");
    }
}
