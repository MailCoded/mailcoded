using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Cli.Commands;

internal static class FoldersCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var account = host.RequireAccount(line, ct);
        var folders = host.Store.ListFolders(account.Id, ct);

        await host.Audit.InfoAsync(
            CliAuditEvents.Folders,
            account.Id,
            host.Caller,
            AuditText.Fields(("folders", AuditText.Number(folders.Count))),
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("email", account.Email);
            writer.WriteNumber("count", folders.Count);
            JsonFields.WritePaging(writer, null, false);

            writer.WriteStartArray("folders");
            foreach (var folder in folders)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", folder.Id.Value);
                writer.WriteString("name", folder.Path.Value);
                JsonFields.WriteText(writer, "role", folder.Role.ToWireValue());
                writer.WriteNumber("unread", folder.UnreadCount);
                writer.WriteNumber("total", folder.TotalCount);
                writer.WriteNumber("uidvalidity", folder.UidValidity.Value);
                writer.WriteString("highest_modseq", folder.HighestModSeq.Value.ToString(CultureInfo.InvariantCulture));
                JsonFields.WriteDate(writer, "last_sync_utc", folder.LastSyncUtc);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        foreach (var folder in folders)
        {
            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"{folder.Id.Value,6}  {folder.UnreadCount,6} unread  {folder.TotalCount,7} total  {SafeText.Line(folder.Path.Value, 60)}"));
        }

        return ExitCodes.Ok;
    }
}
