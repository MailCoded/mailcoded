using System.Globalization;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Moves a message to another folder on the server, and archives as a named shortcut for it.
/// </summary>
/// <remarks>
/// Moving is NOT part of the default agent posture: <c>AgentPolicy.AllowsMove</c> denies agent
/// callers, and the CLI is an agent surface by design. A human at a terminal is confirmed the same
/// way <c>account forget</c> confirms; an unattended run needs MAILCODED_ALLOW_MOVE=1, because a
/// flag alone is something an agent can pass. This does not widen the MCP surface at all.
/// </remarks>
internal static class MoveCommand
{
    public const string OverrideEnvVar = "MAILCODED_ALLOW_MOVE";

    public static Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct) =>
        ExecuteAsync(host, line, output, archive: false, ct);

    public static Task<int> RunArchiveAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct) =>
        ExecuteAsync(host, line, output, archive: true, ct);

    private static async Task<int> ExecuteAsync(
        CliHost host,
        CommandLine line,
        CliOutput output,
        bool archive,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var id = new LocalMessageId(line.RequireId(0, "a message id"));

        var envelope = host.Messages.GetEnvelope(id, ct);
        var account = host.Store.GetAccount(envelope.AccountId, ct)
            ?? throw new CliUsageException($"Message {id.Value} belongs to an account that is gone.");

        var wanted = archive ? "Archive" : line.RequireValue("folder");

        // Gate before resolving, so a caller who will be denied learns nothing about the folders
        // this account has.
        if (!Confirmed(line, id, wanted))
        {
            throw new CliUsageException(
                $"Moving mail is outside the default agent posture. Re-run from a terminal to confirm, "
                + $"or set {OverrideEnvVar}=1 and pass --yes for an unattended run.");
        }

        var target = archive
            ? ResolveArchive(host, account.Id, ct)
            : ResolveFolder(host, account.Id, wanted, ct);

        if (target.Id == envelope.FolderId)
        {
            output.Line($"Message {id.Value.ToString(CultureInfo.InvariantCulture)} is already in {target.Path.Value}.");
            return ExitCodes.Ok;
        }

        await using var provider = await host.ConnectProviderAsync(account, ct).ConfigureAwait(false);
        await host.Messages.MoveAsync(provider, id, target.Id, host.Caller, ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("id", id.Value);
            writer.WriteNumber("to_folder_id", target.Id.Value);
            writer.WriteString("to_folder", target.Path.Value);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"Moved {id.Value.ToString(CultureInfo.InvariantCulture)} to {target.Path.Value}.");
        return ExitCodes.Ok;
    }

    private static Mailcoded.Core.Store.FolderSummary ResolveArchive(CliHost host, AccountId accountId, CancellationToken ct)
    {
        foreach (var folder in host.Store.ListFolders(accountId, ct))
            if (folder.Role == FolderRole.Archive) return folder;

        throw new CliUsageException(
            "This account has no folder marked as Archive. Pass 'mailcoded move <id> --folder <name>' instead.");
    }

    private static Mailcoded.Core.Store.FolderSummary ResolveFolder(
        CliHost host,
        AccountId accountId,
        string wanted,
        CancellationToken ct)
    {
        var folders = host.Store.ListFolders(accountId, ct);

        if (long.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
        {
            foreach (var folder in folders)
                if (folder.Id.Value == numeric) return folder;
        }

        foreach (var folder in folders)
            if (string.Equals(folder.Path.Value, wanted, StringComparison.OrdinalIgnoreCase)) return folder;

        foreach (var folder in folders)
            if (string.Equals(folder.Path.LeafName, wanted, StringComparison.OrdinalIgnoreCase)) return folder;

        var known = string.Join(", ", folders.Select(f => f.Path.Value));
        throw new CliUsageException($"No folder called '{wanted}'. This account has: {known}");
    }

    private static bool Confirmed(CommandLine line, LocalMessageId id, string target)
    {
        if (line.Flag("yes"))
        {
            var allowed = Environment.GetEnvironmentVariable(OverrideEnvVar);
            return allowed is "1" or "true" or "yes";
        }

        if (!Prompt.IsInteractive) return false;

        return Prompt.Confirm(
            $"Move message {id.Value.ToString(CultureInfo.InvariantCulture)} to {target}?",
            true);
    }
}
