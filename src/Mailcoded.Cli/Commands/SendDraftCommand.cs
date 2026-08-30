using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Phase two. The token check, the recipient allowlist, the hourly budget and the audit row all
/// live in SendService; this command only supplies the connections and renders the outcome.
/// </summary>
internal static class SendDraftCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(1);
        var draftId = line.RequireId(0, "a draft id");
        var token = line.RequireValue("confirm-token");
        var append = !line.Flag("no-append");

        var record = host.Store.GetOutbox(draftId, ct)
            ?? throw new StoreException(FailureCategory.NotFound, $"No draft with id {draftId}.");

        // The gate runs before anything opens a connection or reads account configuration, so a
        // denied caller learns only that send is closed.
        host.Policy
            .EvaluateSend(host.Caller, record.Envelope?.AllRecipients() ?? [])
            .ThrowIfDenied();

        var account = host.Store.GetAccount(record.AccountId, ct)
            ?? throw new StoreException(
                FailureCategory.NotFound,
                $"Draft {draftId} belongs to account {record.AccountId.Value}, which is gone.");

        var sender = await host.ConnectSenderAsync(account, ct).ConfigureAwait(false);
        var provider = append ? await host.ConnectProviderAsync(account, ct).ConfigureAwait(false) : null;

        var result = await host.Send.SendAsync(
            host.Caller,
            sender,
            provider,
            draftId,
            token,
            new SendOptions { AppendToSent = append },
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("draft_id", result.OutboxId);
            writer.WriteString("message_id", result.MessageId.Value);
            writer.WriteString("state", result.State.ToWireValue());
            writer.WriteNumber("status_code", result.StatusCode);
            JsonFields.WriteText(writer, "smtp_response", SafeText.Line(result.SmtpResponse, 240));
            JsonFields.WriteText(writer, "enhanced_status_code", result.EnhancedStatusCode);
            writer.WriteNumber("attempts", result.Attempts);
            JsonFields.WriteDate(writer, "next_attempt_utc", result.NextAttemptUtc);
            writer.WriteBoolean("appended_to_sent", result.AppendedToSent);
            writer.WriteBoolean("permanently_failed", result.PermanentlyFailed);
            writer.WriteNumber("remaining_sends_in_window", host.Policy.RemainingSendsInWindow());
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"message_id: {result.MessageId.Value}");
        output.Line($"state:      {result.State.ToWireValue()}");
        output.Line($"smtp:       {result.StatusCode} {SafeText.Line(result.SmtpResponse, 200)}");
        output.Line($"sent copy:  {(result.AppendedToSent ? "appended to Sent" : "not appended")}");
        return ExitCodes.Ok;
    }
}
