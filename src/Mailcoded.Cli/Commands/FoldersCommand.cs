using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Cli.Commands;

internal static class FoldersCommand
{
    private const int MaxIndentLevels = 12;
    private const int NameColumns = 60;
    private const int CountColumns = 38;

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var account = host.RequireAccount(line, ct);
        var folders = host.Store.ListFolders(account.Id, ct);
        var tree = FolderTree.Arrange(folders);

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
            foreach (var node in tree)
            {
                if (node.Folder is not { } folder) continue;

                writer.WriteStartObject();
                writer.WriteNumber("id", folder.Id.Value);
                writer.WriteString("name", folder.Path.Value);
                writer.WriteString("leaf", node.Row.Leaf);
                writer.WriteNumber("depth", node.Row.Depth);
                JsonFields.WriteText(writer, "delimiter", folder.Path.IsFlat ? null : folder.Path.Delimiter.ToString());
                if (node.Row.ParentId is { } parent) writer.WriteNumber("parent_id", parent);
                else writer.WriteNull("parent_id");
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

        foreach (var node in tree)
        {
            var indent = new string(' ', Math.Min(node.Row.Depth, MaxIndentLevels) * 2);
            var name = indent + SafeText.Line(node.Row.Leaf, NameColumns - indent.Length);

            if (node.Folder is not { } folder)
            {
                output.Line(string.Empty.PadLeft(CountColumns) + name);
                continue;
            }

            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"{folder.Id.Value,6}  {folder.UnreadCount,6} unread  {folder.TotalCount,7} total  {name}"));
        }

        return ExitCodes.Ok;
    }
}
