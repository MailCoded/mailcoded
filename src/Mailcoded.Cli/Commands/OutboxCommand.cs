using System.Globalization;
using Mailcoded.Core.Domain.Outbox;

namespace Mailcoded.Cli.Commands;

/// <summary>What is queued, sending, sent or stuck. The first place to look when a send misbehaves.</summary>
internal static class OutboxCommand
{
    public static Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);

        OutboxState? filter = line.Value("state") switch
        {
            null or "" or "all" => null,
            "queued" => OutboxState.Queued,
            "sending" => OutboxState.Sending,
            "sent" => OutboxState.Sent,
            "failed" => OutboxState.Failed,
            var other => throw new CliUsageException(
                $"--state must be queued, sending, sent, failed or all — not '{other}'."),
        };

        var rows = host.Send.ListOutbox(filter, ct);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("count", rows.Count);
            writer.WriteStartArray("outbox");
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", row.Id);
                writer.WriteNumber("account_id", row.AccountId.Value);
                writer.WriteString("state", row.State.ToWireValue());
                writer.WriteString("message_id", row.MessageId.Value);
                writer.WriteNumber("attempts", row.Attempts);
                writer.WriteBoolean("permanently_failed", row.PermanentlyFailed);
                JsonFields.WriteText(writer, "smtp_response", SafeText.Line(row.SmtpResponse, 240));
                JsonFields.WriteText(writer, "enhanced_status", row.EnhancedStatusCode);
                JsonFields.WriteDate(writer, "created_utc", row.CreatedUtc);
                JsonFields.WriteDate(writer, "next_attempt_utc", row.NextAttemptUtc);
                if (row.Envelope is { } envelope)
                {
                    JsonFields.WriteAddresses(writer, "to", envelope.To);
                    writer.WriteNumber("recipients", envelope.AllRecipients().Count);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return Task.FromResult(ExitCodes.Ok);
        }

        if (rows.Count == 0)
        {
            output.Line(filter is null ? "The outbox is empty." : $"Nothing in state '{line.Value("state")}'.");
            return Task.FromResult(ExitCodes.Ok);
        }

        foreach (var row in rows)
        {
            var recipients = row.Envelope is { } e ? string.Join(", ", e.To) : "(recipients not recorded)";
            output.Line($"{row.Id.ToString(CultureInfo.InvariantCulture)}  {row.State.ToWireValue(),-8} {recipients}");

            if (row.Attempts > 0)
            {
                var next = row.NextAttemptUtc is { } at
                    ? at.ToString("u", CultureInfo.InvariantCulture)
                    : row.PermanentlyFailed ? "never — permanent failure" : "not scheduled";
                output.Line($"      attempts {row.Attempts.ToString(CultureInfo.InvariantCulture)}, next {next}");
            }

            if (!string.IsNullOrEmpty(row.SmtpResponse))
                output.Line($"      server: {SafeText.Line(row.SmtpResponse, 160)}");
        }

        return Task.FromResult(ExitCodes.Ok);
    }
}
