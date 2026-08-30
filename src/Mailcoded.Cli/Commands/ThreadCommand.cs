using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Cli.Commands;

internal static class ThreadCommand
{
    private const int DefaultLimit = 200;

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var raw = line.RequirePositional(0, "a message id or a thread key");
        var limit = line.Int("limit", 1, 1000) ?? DefaultLimit;

        var threadKey = Resolve(host, raw, ct);
        var view = host.Messages.GetThread(threadKey, limit, ct);
        var truncated = view.Messages.Count >= limit;

        var accountId = view.Messages.Count > 0 ? view.Messages[0].AccountId : AccountId.None;
        await host.Audit.InfoAsync(
            CliAuditEvents.Thread,
            accountId,
            host.Caller,
            AuditText.Fields(
                ("thread", AuditText.Digest(threadKey.Value)),
                ("messages", AuditText.Number(view.Messages.Count)),
                ("truncated", AuditText.Bool(truncated))),
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteString("thread_key", threadKey.Value);
            writer.WriteNumber("count", view.Messages.Count);
            JsonFields.WritePaging(writer, null, truncated);

            writer.WriteStartArray("messages");
            foreach (var message in view.Messages)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", message.Id.Value);
                writer.WriteNumber("account_id", message.AccountId.Value);
                writer.WriteNumber("folder_id", message.FolderId.Value);
                JsonFields.WriteText(writer, "message_id", message.MessageId?.Value);
                JsonFields.WriteDate(writer, "date", message.DateUtc);
                JsonFields.WriteText(writer, "from", message.From);
                JsonFields.WriteText(writer, "to", message.To);
                JsonFields.WriteText(writer, "cc", message.Cc);
                JsonFields.WriteText(writer, "subject", message.Subject);
                JsonFields.WriteFlags(writer, "flags", message.Flags);
                var tags = view.Tags.TryGetValue(message.Id, out var found) ? found : Array.Empty<Tag>();
                JsonFields.WriteTags(writer, "tags", tags);
                writer.WriteBoolean("has_attachments", message.HasAttachments);
                writer.WriteBoolean("body_fetched", message.BodyFetched);
                writer.WriteNumber("size", message.Size);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"thread {threadKey.Value}");
        foreach (var message in view.Messages)
        {
            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"{message.Id.Value,8}  {JsonFields.Iso(message.DateUtc)}  [{JsonFields.Flags(message.Flags)}]  {SafeText.Line(message.From, 48)}"));
            output.Line("          " + SafeText.Line(message.Subject, 96));
        }

        if (truncated) output.Line($"[truncated at --limit {limit}]");
        return ExitCodes.Ok;
    }

    private static ThreadKey Resolve(CliHost host, string raw, CancellationToken ct)
    {
        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            var envelope = host.Messages.GetEnvelope(new LocalMessageId(id), ct);
            return envelope.ThreadKey
                ?? throw new CliUsageException($"Message {id} has no thread key yet; sync the folder first.");
        }

        if (ThreadKey.TryCreate(raw, out var key)) return key;
        throw new CliUsageException("A thread argument must be a message id or a non-empty thread key.");
    }
}
