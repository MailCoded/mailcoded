using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Phase one of the two-phase send: show the human exactly who would receive the message, then
/// mint the single-use token phase two consumes. Every gate stays in Core.
/// </summary>
internal static class SendPreviewCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var draftId = line.RequireId(0, "a draft id");

        var record = host.Store.GetOutbox(draftId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No draft with id {draftId}.");

        if (record.State is not (OutboxState.Queued or OutboxState.Failed))
        {
            throw new CliUsageException(
                $"Draft {draftId} is '{record.State.ToWireValue()}'; only a queued or failed draft can be previewed.");
        }

        var envelope = record.Envelope;
        if (envelope is null || envelope.IsEmpty)
        {
            throw new StoreException(
                FailureCategory.NotFound,
                $"Draft {draftId} carries no stored recipients; compose it again with 'mailcoded draft'.");
        }

        var recipients = envelope.AllRecipients();
        var gate = host.Policy.EvaluateSend(host.Caller, recipients);
        var digest = AuditText.Digest(record.Raw);
        var grant = await host.Tokens.IssueAsync(record.Id, record.MessageId, digest, ct).ConfigureAwait(false);
        var expiresUtc = grant.ExpiresUtc;

        await host.Audit.SendAsync(
            new SendAuditRecord
            {
                AccountId = record.AccountId,
                Method = AuditEvents.SendPreview,
                Decision = gate.Label,
                ArgsDigest = digest,
                Caller = host.Caller,
                MessageId = record.MessageId,
                OutboxId = record.Id,
                RecipientCount = recipients.Count,
                TokenConsumed = false,
                Reason = gate.Allowed ? null : gate.Reason.ToString(),
                Level = gate.Allowed ? AuditLog.LevelInfo : AuditLog.LevelWarn,
            },
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("draft_id", record.Id);
            writer.WriteNumber("account_id", record.AccountId.Value);
            writer.WriteString("message_id", record.MessageId.Value);
            writer.WriteString("state", record.State.ToWireValue());
            writer.WriteString("from", envelope.From.Value);
            JsonFields.WriteAddresses(writer, "to", envelope.To);
            JsonFields.WriteAddresses(writer, "cc", envelope.Cc);
            JsonFields.WriteAddresses(writer, "bcc", envelope.Bcc);
            writer.WriteNumber("recipient_count", recipients.Count);
            writer.WriteNumber("size_bytes", record.Raw.LongLength);
            JsonFields.WriteDate(writer, "created_utc", record.CreatedUtc);
            writer.WriteString("confirm_token", grant.Token);
            JsonFields.WriteDate(writer, "confirm_token_expires_utc", expiresUtc);
            writer.WriteString("confirm_token_scope", "store");

            writer.WriteStartObject("send");
            writer.WriteBoolean("enabled", gate.Allowed);
            writer.WriteString("decision", gate.Label);
            JsonFields.WriteText(writer, "reason", gate.Allowed ? null : gate.Reason.ToString());
            writer.WriteNumber("remaining_in_window", host.Policy.RemainingSendsInWindow());
            writer.WriteEndObject();

            JsonFields.WriteStrings(writer, "notes", Notes(gate.Allowed));
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"draft_id:      {record.Id}");
        output.Line($"message_id:    {record.MessageId.Value}");
        output.Line($"from:          {envelope.From.Value}");
        output.Line($"to:            {Join(envelope.To)}");
        if (envelope.Cc.Count > 0) output.Line($"cc:            {Join(envelope.Cc)}");
        if (envelope.Bcc.Count > 0) output.Line($"bcc:           {Join(envelope.Bcc)}");
        output.Line($"size:          {record.Raw.LongLength} bytes");
        output.Line($"send gate:     {gate.Label}");
        output.Line($"confirm_token: {grant.Token}");
        output.Line($"expires:       {JsonFields.Iso(expiresUtc)}");
        foreach (var note in Notes(gate.Allowed)) output.Line("note: " + note);
        return ExitCodes.Ok;
    }

    private static IReadOnlyList<string> Notes(bool allowed)
    {
        var notes = new List<string>(3)
        {
            "Show this preview to the human and let the human decide before running send-draft.",
            "The token is single use, expires at the time shown, and is bound to these exact bytes: "
            + "editing the draft or sending it twice is refused with error 1003.",
        };

        if (!allowed)
        {
            notes.Add(
                "The send gate is closed right now: send-draft will fail until MAILCODED_SEND=1 and "
                + "every recipient matches MAILCODED_APPROVED_RECIPIENTS.");
        }

        return notes;
    }

    private static string Join(IReadOnlyList<EmailAddress> addresses)
    {
        var parts = new List<string>(addresses.Count);
        foreach (var address in addresses) parts.Add(address.Value);
        return string.Join(", ", parts);
    }
}
