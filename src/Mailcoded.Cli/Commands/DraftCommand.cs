using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Composes a draft. Drafting is always on; this command never sends and never prints the
/// confirm token — the token belongs to send-preview, which the human sees.
/// </summary>
internal static class DraftCommand
{
    public const long MaxBodyBytes = 1024 * 1024;

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);

        var account = host.RequireAccount(line, ct);
        var to = ParseAddresses(line.Values("to"), "--to");
        var cc = ParseAddresses(line.Values("cc"), "--cc");
        var bcc = ParseAddresses(line.Values("bcc"), "--bcc");

        if (to.Count == 0 && cc.Count == 0 && bcc.Count == 0)
            throw new CliUsageException("A draft needs at least one recipient: --to, --cc or --bcc.");

        var subject = CleanSubject(line.RequireValue("subject"));
        var body = await ReadBodyAsync(line, ct).ConfigureAwait(false);

        EmailAddress? from = null;
        if (line.Value("from") is { } fromRaw)
        {
            if (!EmailAddress.TryParse(fromRaw, out var parsed))
                throw new CliUsageException($"--from '{fromRaw}' is not a valid address.");
            from = parsed;
        }

        var (inReplyTo, references) = ResolveReply(host, line, ct);

        var preview = await host.Send.PreviewAsync(
            host.Caller,
            account.Id,
            new DraftRequest
            {
                To = to,
                Cc = cc,
                Bcc = bcc,
                Subject = subject,
                BodyText = body,
                From = from,
                InReplyTo = inReplyTo,
                References = references,
            },
            ct).ConfigureAwait(false);

        // The token this phase minted is not part of the draft contract; drop it so nothing
        // authorizes a send that no human previewed.
        host.Tokens.Revoke(preview.OutboxId);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("draft_id", preview.OutboxId);
            writer.WriteString("message_id", preview.MessageId.Value);
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("from", preview.From.Value);
            JsonFields.WriteAddresses(writer, "to", preview.To);
            JsonFields.WriteAddresses(writer, "cc", preview.Cc);
            JsonFields.WriteAddresses(writer, "bcc", preview.Bcc);
            writer.WriteString("subject", preview.Subject);
            writer.WriteString("body_preview", preview.BodyPreview);
            writer.WriteBoolean("body_truncated", preview.BodyPreview.Length < body.Length);
            writer.WriteNumber("size_bytes", preview.SizeBytes);
            writer.WriteBoolean("requires_smtputf8", preview.RequiresSmtpUtf8);
            writer.WriteBoolean("confirm_token_issued", false);

            writer.WriteStartObject("send");
            writer.WriteBoolean("enabled", preview.Gate.Allowed);
            writer.WriteString("decision", preview.Gate.Label);
            JsonFields.WriteText(writer, "reason", preview.Gate.Allowed ? null : preview.Gate.Reason.ToString());
            writer.WriteEndObject();

            writer.WriteString("next", "mailcoded send-preview " + preview.OutboxId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"draft_id:   {preview.OutboxId}");
        output.Line($"message_id: {preview.MessageId.Value}");
        output.Line($"from:       {preview.From.Value}");
        output.Line($"to:         {Join(preview.To)}");
        if (preview.Cc.Count > 0) output.Line($"cc:         {Join(preview.Cc)}");
        if (preview.Bcc.Count > 0) output.Line($"bcc:        {Join(preview.Bcc)}");
        output.Line($"subject:    {SafeText.Line(preview.Subject, 200)}");
        output.Line($"size:       {preview.SizeBytes} bytes");
        output.Line($"send gate:  {preview.Gate.Label}");
        output.Line($"next:       mailcoded send-preview {preview.OutboxId}");
        return ExitCodes.Ok;
    }

    private static (MessageId? InReplyTo, IReadOnlyList<MessageId> References) ResolveReply(
        CliHost host,
        CommandLine line,
        CancellationToken ct)
    {
        MessageId? parent = null;

        if (line.Value("in-reply-to") is { } raw)
        {
            if (!MessageId.TryParse(raw, out var parsed))
                throw new CliUsageException($"--in-reply-to '{raw}' is not a valid Message-ID.");
            parent = parsed;
        }

        if (line.Value("reply-to") is { } replyTo)
        {
            if (!long.TryParse(replyTo, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                throw new CliUsageException($"--reply-to must be a local message id, not '{replyTo}'.");
            }

            var envelope = host.Messages.GetEnvelope(new LocalMessageId(id), ct);
            parent = envelope.MessageId
                ?? throw new CliUsageException($"Message {id} carries no Message-ID, so it cannot be replied to.");
        }

        if (parent is not { } value) return (null, Array.Empty<MessageId>());

        IReadOnlyList<MessageId> references = [value];
        return (value, references);
    }

    private static async Task<string> ReadBodyAsync(CommandLine line, CancellationToken ct)
    {
        var path = line.Value("body-file");
        var stdin = line.Flag("body-stdin");

        if (path is not null && stdin)
            throw new CliUsageException("Pass either --body-file or --body-stdin, not both.");

        if (path is not null)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException($"No body file at '{path}'.", path);
            if (info.Length > MaxBodyBytes)
                throw new CliUsageException($"--body-file is {info.Length} bytes; the limit is {MaxBodyBytes}.");

            return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        }

        if (!stdin)
            throw new CliUsageException("A draft needs a body: pass --body-file <path> or --body-stdin.");

        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (text.Length > MaxBodyBytes)
            throw new CliUsageException($"The body read from stdin exceeds {MaxBodyBytes} characters.");

        return text;
    }

    private static IReadOnlyList<EmailAddress> ParseAddresses(IReadOnlyList<string> values, string option)
    {
        if (values.Count == 0) return Array.Empty<EmailAddress>();

        var addresses = new List<EmailAddress>(values.Count);
        foreach (var value in values)
        {
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!EmailAddress.TryParse(part, out var address))
                    throw new CliUsageException($"{option} '{part}' is not a valid email address.");
                addresses.Add(address);
            }
        }

        return addresses;
    }

    private static string CleanSubject(string subject)
    {
        foreach (var c in subject)
        {
            if (char.IsControl(c) && c is not '\t')
                throw new CliUsageException("--subject must not contain control characters or line breaks.");
        }

        return subject.Length <= 998 ? subject : subject[..998];
    }

    private static string Join(IReadOnlyList<EmailAddress> addresses)
    {
        var parts = new List<string>(addresses.Count);
        foreach (var address in addresses) parts.Add(address.Value);
        return string.Join(", ", parts);
    }
}
