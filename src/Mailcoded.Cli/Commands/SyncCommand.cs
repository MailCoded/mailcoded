using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Cli.Commands;

internal static class SyncCommand
{
    private static readonly TimeSpan AttachmentIndexBudget = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var account = host.RequireAccount(line, ct);
        var folderId = line.Long("folder", 1, long.MaxValue);

        var provider = await host.ConnectProviderAsync(account, ct).ConfigureAwait(false);

        // SyncEngine writes the sync_started / sync_completed audit rows itself.
        var progress = SyncProgressLine.Start(host.Clock, output.Json, line.Flag("quiet"));
        SyncReport report;
        try
        {
            report = folderId is { } folder
                ? await host.Sync.SyncFolderAsync(provider, new FolderId(folder), progress?.Progress, ct).ConfigureAwait(false)
                : await host.Accounts.InitialSyncAsync(provider, account.Id, progress?.Progress, ct).ConfigureAwait(false);
        }
        finally
        {
            progress?.Finish();
        }

        var pending = await new AttachmentTextBackfill(host.Store, host.Parser)
            .RunWithinAsync(AttachmentIndexBudget, ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteNumber("attachment_text_pending", pending);
            writer.WriteNumber("added", report.Added);
            writer.WriteNumber("updated", report.Updated);
            writer.WriteNumber("expunged", report.Expunged);
            writer.WriteNumber("batches", report.Batches);
            writer.WriteNumber("folders", report.Folders);
            writer.WriteNumber("duration_ms", report.DurationMs);
            writer.WriteBoolean("degraded", report.Degraded);
            writer.WriteString("latched_quirks", report.LatchedQuirks.ToString());

            writer.WriteStartArray("folder_reports");
            foreach (var folderReport in report.FolderReports)
            {
                writer.WriteStartObject();
                writer.WriteNumber("folder_id", folderReport.FolderId.Value);
                writer.WriteString("name", folderReport.Path);
                writer.WriteString("plan", folderReport.Plan);
                writer.WriteNumber("added", folderReport.Added);
                writer.WriteNumber("updated", folderReport.Updated);
                writer.WriteNumber("expunged", folderReport.Expunged);
                writer.WriteNumber("batches", folderReport.Batches);
                writer.WriteBoolean("degraded", folderReport.Degraded);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("unsyncable_folders");
            foreach (var name in report.UnsyncableFolders) writer.WriteStringValue(SafeText.Line(name, 128));
            writer.WriteEndArray();
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"synced {report.Folders} folder(s) in {report.DurationMs} ms: +{report.Added} added, {report.Updated} updated, {report.Expunged} expunged"));

        foreach (var folderReport in report.FolderReports)
        {
            if (folderReport.Added == 0 && folderReport.Updated == 0 && folderReport.Expunged == 0) continue;
            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"  {SafeText.Line(folderReport.Path, 48),-48} {folderReport.Plan,-16} +{folderReport.Added} ~{folderReport.Updated} -{folderReport.Expunged}"));
        }

        if (report.Degraded) output.Line("warning: at least one folder synced in degraded mode");
        if (report.UnsyncableFolders.Count > 0)
        {
            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"warning: {report.UnsyncableFolders.Count} folder(s) not synced, their names cannot be told apart from the server's hierarchy:"));
            foreach (var name in report.UnsyncableFolders.Take(10)) output.Line($"  {SafeText.Line(name, 128)}");
        }

        if (pending > 0)
            output.Line(string.Create(CultureInfo.InvariantCulture, $"attachment text: {pending} fetched message(s) still to read; sync again to continue"));
        return ExitCodes.Ok;
    }
}
