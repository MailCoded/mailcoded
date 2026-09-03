using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Builds a reply draft from an existing message: recipients, Re: subject, the quoted original,
/// and the In-Reply-To/References chain that keeps the conversation threaded for the recipient.
/// Queues it as a draft — sending is still the separate two-phase flow.
/// </summary>
internal static class ReplyCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var id = new Mailcoded.Core.Domain.Primitives.LocalMessageId(line.RequireId(0, "a message id"));

        var envelope = host.Messages.GetEnvelope(id, ct);
        var account = host.Store.GetAccount(envelope.AccountId, ct)
            ?? throw new CliUsageException($"Message {id.Value} belongs to an account that is gone.");

        IMailProvider? provider = null;
        if (!envelope.BodyFetched && !line.Flag("no-fetch"))
        {
            try
            {
                provider = await host.ConnectProviderAsync(account, ct).ConfigureAwait(false);
            }
            catch (ProviderException ex)
            {
                throw new CliUsageException(
                    $"The original body is not cached and the server is unreachable ({ex.Category}). "
                    + "Run 'mailcoded sync' first, or pass --no-fetch to reply without a quote.");
            }
        }

        var self = Mailcoded.Core.Domain.Primitives.EmailAddress.TryParse(account.Email, out var me)
            ? me
            : (Mailcoded.Core.Domain.Primitives.EmailAddress?)null;

        var context = await host.Messages
            .BuildReplyAsync(provider, id, line.Flag("all"), self, host.Caller, ct)
            .ConfigureAwait(false);

        if (context.To.Count == 0)
            throw new CliUsageException("The original message has no usable reply address.");

        var body = ComposeBody(line, context);

        var preview = await host.Send.PreviewAsync(
            host.Caller,
            account.Id,
            new DraftRequest
            {
                To = context.To,
                Cc = context.Cc,
                Subject = context.Subject,
                BodyText = body,
                InReplyTo = context.InReplyTo,
                References = context.References,
            },
            ct).ConfigureAwait(false);

        // A reply is a draft, not an authorization to send.
        host.Tokens.Revoke(preview.OutboxId);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("draft_id", preview.OutboxId);
            writer.WriteNumber("in_reply_to_message", id.Value);
            writer.WriteString("subject", context.Subject);
            JsonFields.WriteAddresses(writer, "to", context.To);
            JsonFields.WriteAddresses(writer, "cc", context.Cc);
            writer.WriteNumber("quoted_chars", context.QuotedBody.Length);
            if (context.RejectedAddresses.Count > 0)
            {
                writer.WriteStartArray("dropped_addresses");
                foreach (var bad in context.RejectedAddresses) writer.WriteStringValue(bad);
                writer.WriteEndArray();
            }

            writer.WriteString("next", "mailcoded send-preview " + preview.OutboxId.ToString(CultureInfo.InvariantCulture));
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"draft {preview.OutboxId.ToString(CultureInfo.InvariantCulture)} — {context.Subject}");
        output.Line($"  to   {string.Join(", ", context.To)}");
        if (context.Cc.Count > 0) output.Line($"  cc   {string.Join(", ", context.Cc)}");
        if (context.RejectedAddresses.Count > 0)
            output.Line($"  dropped unusable addresses: {string.Join(", ", context.RejectedAddresses)}");
        output.Blank();
        output.Line($"Edit the body before sending, or send as-is:");
        output.Line($"  mailcoded send-preview {preview.OutboxId.ToString(CultureInfo.InvariantCulture)}");
        return ExitCodes.Ok;
    }

    private static string ComposeBody(CommandLine line, ReplyContext context)
    {
        var lead = line.Value("body");
        if (lead is null && line.Value("body-file") is { } path)
        {
            if (!File.Exists(path)) throw new CliUsageException($"No such file: {path}");
            lead = File.ReadAllText(path);
        }

        var quote = line.Flag("no-quote") ? string.Empty : context.QuotedBody;
        if (string.IsNullOrEmpty(lead)) return quote;
        return quote.Length == 0 ? lead : lead.TrimEnd() + "\n\n" + quote;
    }
}
