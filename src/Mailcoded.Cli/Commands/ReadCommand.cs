using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Plaintext only. There is no HTML path here under any flag — the plaintext-only rule is what
/// removes the markdown and remote-image exfiltration vectors from the agent surface.
/// </summary>
internal static class ReadCommand
{
    private const int DefaultMaxChars = 20_000;

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var id = new LocalMessageId(line.RequireId(0, "a message id"));
        var maxChars = line.Int("max-chars", 1, 1_000_000) ?? DefaultMaxChars;
        var skipChars = line.Int("skip-chars", 0, int.MaxValue) ?? 0;
        var offline = line.Flag("no-fetch");

        var envelope = host.Messages.GetEnvelope(id, ct);

        IMailProvider? provider = null;
        if (!envelope.BodyFetched && !offline)
        {
            var account = host.Store.GetAccount(envelope.AccountId, ct)
                ?? throw new StoreException(
                    FailureCategory.NotFound,
                    $"Message {id.Value} belongs to account {envelope.AccountId.Value}, which is gone.");

            provider = await host.ConnectProviderAsync(account, ct).ConfigureAwait(false);
        }

        var view = await host.Messages
            .GetAsync(provider, id, MessageBodyFormat.Text, !offline, host.Caller, ct)
            .ConfigureAwait(false);

        var body = view.BodyText ?? string.Empty;
        var total = body.Length;
        var start = Math.Min(skipChars, total);
        var slice = body.Substring(start, Math.Min(maxChars, total - start));
        var more = start + slice.Length < total;
        var nextCursor = more
            ? (start + slice.Length).ToString(CultureInfo.InvariantCulture)
            : null;

        await host.Audit.InfoAsync(
            CliAuditEvents.Read,
            envelope.AccountId,
            host.Caller,
            AuditText.Fields(
                ("message", AuditText.Number(id.Value)),
                ("fetched", AuditText.Bool(view.FetchedNow)),
                ("chars", AuditText.Number(slice.Length)),
                ("truncated", AuditText.Bool(more))),
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("id", view.Envelope.Id.Value);
            writer.WriteNumber("account_id", view.Envelope.AccountId.Value);
            writer.WriteNumber("folder_id", view.Envelope.FolderId.Value);
            JsonFields.WriteText(writer, "thread_key", view.Envelope.ThreadKey?.Value);
            JsonFields.WriteText(writer, "message_id", view.Envelope.MessageId?.Value);
            JsonFields.WriteDate(writer, "date", view.Envelope.DateUtc);
            JsonFields.WriteText(writer, "from", view.Envelope.From);
            JsonFields.WriteText(writer, "to", view.Envelope.To);
            JsonFields.WriteText(writer, "cc", view.Envelope.Cc);
            JsonFields.WriteText(writer, "subject", view.Envelope.Subject);
            JsonFields.WriteFlags(writer, "flags", view.Envelope.Flags);
            JsonFields.WriteTags(writer, "tags", view.Tags);
            writer.WriteBoolean("has_attachments", view.Envelope.HasAttachments);
            writer.WriteNumber("size", view.Envelope.Size);
            writer.WriteBoolean("body_fetched", view.BodyFetched);
            writer.WriteBoolean("fetched_now", view.FetchedNow);
            writer.WriteString("body_format", "plaintext");
            writer.WriteNumber("body_chars", slice.Length);
            writer.WriteNumber("body_total_chars", total);
            writer.WriteNumber("body_offset", start);
            writer.WriteString("body_text", slice);
            JsonFields.WriteStrings(writer, "parse_warnings", view.ParseWarnings);
            JsonFields.WritePaging(writer, nextCursor, more);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"id:      {view.Envelope.Id.Value}");
        output.Line($"date:    {JsonFields.Iso(view.Envelope.DateUtc)}");
        output.Line($"from:    {SafeText.Line(view.Envelope.From, 200)}");
        output.Line($"to:      {SafeText.Line(view.Envelope.To, 200)}");
        if (view.Envelope.Cc is { Length: > 0 }) output.Line($"cc:      {SafeText.Line(view.Envelope.Cc, 200)}");
        output.Line($"subject: {SafeText.Line(view.Envelope.Subject, 200)}");
        output.Line($"flags:   {JsonFields.Flags(view.Envelope.Flags)}");
        output.Line($"tags:    {JsonFields.Tags(view.Tags)}");
        if (view.Envelope.HasAttachments) output.Line("attachments: yes (bytes are not exposed to the CLI)");
        output.Blank();
        output.Line(SafeText.Block(slice, maxChars));

        if (more)
        {
            output.Blank();
            output.Line(string.Create(
                CultureInfo.InvariantCulture,
                $"[truncated at {start + slice.Length} of {total} characters; continue with --skip-chars {start + slice.Length}]"));
        }

        return ExitCodes.Ok;
    }
}
