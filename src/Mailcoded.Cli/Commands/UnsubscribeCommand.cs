using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Turns a message's mailto unsubscribe target into an ordinary draft. It never sends, never opens a URL and never
/// performs an RFC 8058 one-click POST: the address and the links are sender-controlled.
/// </summary>
internal static class UnsubscribeCommand
{
    private const string DefaultSubject = "unsubscribe";
    private const string DefaultBody = "unsubscribe";

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var id = new LocalMessageId(line.RequireId(0, "a message id"));

        var envelope = host.Messages.GetEnvelope(id, ct);
        if (envelope.Unsubscribe is null) envelope = await ResolveUnknownAsync(host, line, envelope, ct).ConfigureAwait(false);

        var options = UnsubscribeStorage.Decode(envelope.Unsubscribe);
        if (options is null)
        {
            throw new CliUsageException(
                "The unsubscribe headers of this message are not known yet and the body is not cached. "
                + "Run 'mailcoded sync' or 'mailcoded read' first (without --no-fetch).");
        }

        if (options.IsEmpty)
            throw new StoreException(FailureCategory.NotFound, $"Message {id.Value} offers no usable unsubscribe option.");

        var account = host.Store.GetAccount(envelope.AccountId, ct)
            ?? throw new CliUsageException($"Message {id.Value} belongs to an account that is gone.");

        SendPreview? preview = null;
        if (options.Mailto is { } mailto)
        {
            preview = await host.Send.PreviewAsync(
                host.Caller,
                account.Id,
                new DraftRequest
                {
                    To = [EmailAddress.Parse(mailto.Address)],
                    Subject = mailto.Subject ?? DefaultSubject,
                    BodyText = mailto.Body ?? DefaultBody,
                },
                ct).ConfigureAwait(false);

            // Phase one of the send is the human's to run; this draft authorizes nothing.
            host.Tokens.Revoke(preview.OutboxId);
        }

        await host.Audit.InfoAsync(
            CliAuditEvents.Unsubscribe,
            envelope.AccountId,
            host.Caller,
            AuditText.Fields(
                ("message", AuditText.Number(id.Value)),
                ("drafted", AuditText.Bool(preview is not null)),
                ("web_links", AuditText.Number(options.Web.Count))),
            ct).ConfigureAwait(false);

        if (output.Json) return WriteJson(output, id, envelope, options, preview);

        WriteText(output, id, envelope, options, preview);
        return ExitCodes.Ok;
    }

    private static async Task<EnvelopeRow> ResolveUnknownAsync(CliHost host, CommandLine line, EnvelopeRow envelope, CancellationToken ct)
    {
        var offline = line.Flag("no-fetch");

        IMailProvider? provider = null;
        if (!envelope.BodyFetched && !offline)
        {
            var account = host.Store.GetAccount(envelope.AccountId, ct)
                ?? throw new CliUsageException($"Message {envelope.Id.Value} belongs to an account that is gone.");
            provider = await host.ConnectProviderAsync(account, ct).ConfigureAwait(false);
        }

        var view = await host.Messages
            .GetAsync(provider, envelope.Id, MessageBodyFormat.Text, !offline, host.Caller, ct)
            .ConfigureAwait(false);

        return view.Envelope;
    }

    private static int WriteJson(CliOutput output, LocalMessageId id, EnvelopeRow envelope, UnsubscribeOptions options, SendPreview? preview)
    {
        var writer = output.BeginJson();
        writer.WriteNumber("id", id.Value);
        writer.WriteNumber("account_id", envelope.AccountId.Value);
        JsonFields.WriteText(writer, "from", envelope.From);
        JsonFields.WriteText(writer, "dmarc", envelope.Dmarc?.ToWire());
        writer.WriteBoolean("drafted", preview is not null);
        writer.WriteBoolean("sent", false);
        JsonFields.WriteUnsubscribe(writer, "unsubscribe", options);

        if (preview is null)
        {
            writer.WriteNull("draft_id");
            writer.WriteString("next", "No mailto target: nothing was drafted. A human may open a web link from a browser; mailcoded never does.");
        }
        else
        {
            writer.WriteNumber("draft_id", preview.OutboxId);
            JsonFields.WriteAddresses(writer, "to", preview.To);
            writer.WriteString("subject", preview.Subject);
            writer.WriteStartObject("send");
            writer.WriteBoolean("enabled", preview.Gate.Allowed);
            writer.WriteString("decision", preview.Gate.Label);
            writer.WriteEndObject();
            writer.WriteString("next", "mailcoded send-preview " + preview.OutboxId.ToString(CultureInfo.InvariantCulture));
        }

        output.EndJson(writer);
        return ExitCodes.Ok;
    }

    private static void WriteText(CliOutput output, LocalMessageId id, EnvelopeRow envelope, UnsubscribeOptions options, SendPreview? preview)
    {
        output.Line($"message {id.Value.ToString(CultureInfo.InvariantCulture)} from {SafeText.Line(envelope.From, 200)}");
        output.Line($"  sender authentication: {envelope.Dmarc?.ToWire() ?? "not evaluated"}");

        if (preview is not null)
        {
            var draft = preview.OutboxId.ToString(CultureInfo.InvariantCulture);
            output.Blank();
            output.Line($"draft {draft} to {SafeText.Line(string.Join(", ", preview.To), 300)}");
            output.Line($"  subject: {SafeText.Line(preview.Subject, 300)}");
            output.Line($"  send gate: {preview.Gate.Label}");
            output.Line("Nothing has been sent. The address came from the message's own headers, and sending confirms to the");
            output.Line("sender that your address is live; unsubscribe only from senders you recognise.");
            output.Blank();
            output.Line("Next, the human reviews and authorizes:");
            output.Line($"  mailcoded send-preview {draft}");
            output.Line($"  mailcoded send-draft {draft} --confirm-token <token from send-preview>");
        }
        else
        {
            output.Blank();
            output.Line("This message offers no mailto unsubscribe address, so no draft was made.");
        }

        if (options.Web.Count == 0) return;

        output.Blank();
        output.Line("Web links (NOT opened; mailcoded never fetches them):");
        foreach (var url in options.Web) output.Line($"  {SafeText.Line(url, ListUnsubscribeParser.MaxUriChars)}");
        if (options.OneClick)
            output.Line("The sender advertises one-click POST; mailcoded refuses it. Open a link yourself if you trust the sender.");
    }
}
