using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Cli.Commands;

internal static class StatsCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var stats = host.Health.Collect(ct);

        await host.Audit.InfoAsync(
            CliAuditEvents.Stats,
            AccountId.None,
            host.Caller,
            AuditText.Fields(("folders", AuditText.Number(stats.Folders.Count))),
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("schema_version_store", stats.Store.SchemaVersion);
            writer.WriteNumber("protocol_version", Mailcoded.Protocol.ProtocolConstants.Version);

            writer.WriteStartObject("process");
            writer.WriteNumber("uptime_ms", stats.Process.UptimeMs);
            writer.WriteNumber("working_set_bytes", stats.Process.WorkingSetBytes);
            writer.WriteNumber("gc_heap_bytes", stats.Process.GcHeapBytes);
            writer.WriteNumber("gc_committed_bytes", stats.Process.GcCommittedBytes);
            writer.WriteNumber("gc_total_allocated_bytes", stats.Process.TotalAllocatedBytes);
            writer.WriteNumber("gen0_collections", stats.Process.Gen0Collections);
            writer.WriteNumber("gen1_collections", stats.Process.Gen1Collections);
            writer.WriteNumber("gen2_collections", stats.Process.Gen2Collections);
            writer.WriteNumber("thread_count", stats.Process.ThreadCount);
            writer.WriteNumber("handle_count", stats.Process.HandleCount);
            writer.WriteEndObject();

            writer.WriteStartObject("store");
            writer.WriteNumber("database_size_bytes", stats.Store.DatabaseSizeBytes);
            writer.WriteNumber("wal_size_bytes", stats.Store.WalSizeBytes);
            writer.WriteNumber("page_count", stats.Store.PageCount);
            writer.WriteNumber("page_size_bytes", stats.Store.PageSizeBytes);
            writer.WriteNumber("freelist_pages", stats.Store.FreelistPages);
            writer.WriteNumber("blob_directory_size_bytes", stats.Store.BlobDirectorySizeBytes);
            writer.WriteStartObject("table_counts");
            foreach (var pair in stats.Store.TableCounts) writer.WriteNumber(pair.Key, pair.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteStartObject("agent");
            writer.WriteNumber("open_connections", stats.OpenConnections);
            writer.WriteNumber("outstanding_confirm_tokens", stats.OutstandingConfirmTokens);
            writer.WriteNumber("remaining_sends_in_window", stats.RemainingAgentSends);
            writer.WriteNumber("outbox_queued", stats.QueuedOutbox);
            writer.WriteNumber("outbox_failed", stats.FailedOutbox);
            writer.WriteEndObject();

            writer.WriteNumber("folder_count", stats.Folders.Count);
            writer.WriteStartArray("folders");
            foreach (var folder in stats.Folders)
            {
                writer.WriteStartObject();
                writer.WriteNumber("folder_id", folder.FolderId.Value);
                writer.WriteString("name", folder.Path);
                JsonFields.WriteText(writer, "role", folder.Role.ToWireValue());
                writer.WriteString("highest_modseq", folder.HighestModSeq.ToString(CultureInfo.InvariantCulture));
                writer.WriteNumber("uidvalidity", folder.UidValidity);
                writer.WriteNumber("unread", folder.UnreadCount);
                writer.WriteNumber("total", folder.TotalCount);
                JsonFields.WriteDate(writer, "last_sync_utc", folder.LastSyncUtc);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"schema:  v{stats.Store.SchemaVersion}");
        output.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"store:   {stats.Store.DatabaseSizeBytes} bytes db, {stats.Store.WalSizeBytes} bytes wal, {stats.Store.BlobDirectorySizeBytes} bytes blobs"));
        output.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"memory:  {stats.Process.WorkingSetBytes} rss, {stats.Process.GcHeapBytes} heap, {stats.Process.ThreadCount} threads"));
        output.Line(string.Create(
            CultureInfo.InvariantCulture,
            $"outbox:  {stats.QueuedOutbox} queued, {stats.FailedOutbox} failed, {stats.RemainingAgentSends} sends left this hour"));

        foreach (var folder in stats.Folders)
        {
            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"{folder.FolderId.Value,6}  {folder.UnreadCount,6} unread  {folder.TotalCount,7} total  {SafeText.Line(folder.Path, 60)}"));
        }

        return ExitCodes.Ok;
    }
}
