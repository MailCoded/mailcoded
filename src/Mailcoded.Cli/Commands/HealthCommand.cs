using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Cli.Commands;

internal static class HealthCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var report = host.Health.CheckHealth(ct);

        await host.Audit.InfoAsync(
            CliAuditEvents.Health,
            AccountId.None,
            host.Caller,
            AuditText.Fields(
                ("ok", AuditText.Bool(report.Ok)),
                ("accounts", AuditText.Number(report.Accounts.Count)),
                ("stuck", AuditText.Number(report.StuckSends))),
            ct).ConfigureAwait(false);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteString("status", report.Ok ? "ok" : "degraded");
            writer.WriteBoolean("store_ok", true);
            JsonFields.WriteText(writer, "store_path", report.StorePath);
            writer.WriteNumber("store_schema_version", report.SchemaVersion);
            writer.WriteString("secret_backend", report.SecretBackend);
            writer.WriteNumber("stuck_sends", report.StuckSends);
            writer.WriteBoolean("send_enabled", host.Policy.Options.SendEnabled);
            writer.WriteNumber("approved_recipient_patterns", host.Policy.Options.ApprovedRecipients.Count);
            writer.WriteBoolean("raw_sql_enabled", host.Policy.Options.RawSqlEnabled);

            writer.WriteStartArray("accounts");
            foreach (var account in report.Accounts)
            {
                writer.WriteStartObject();
                writer.WriteNumber("account_id", account.AccountId.Value);
                writer.WriteString("email", account.Email);
                writer.WriteString("imap", Wire(account.ImapState));
                writer.WriteString("smtp", Wire(account.SmtpState));
                writer.WriteString("auth", account.AuthRequired ? "auth-required" : "unknown");
                writer.WriteNumber("folder_count", account.FolderCount);
                writer.WriteNumber("unread", account.UnreadCount);
                JsonFields.WriteText(writer, "last_detail", SafeText.Line(account.LastDetail, 200));
                JsonFields.WriteDate(writer, "last_change_utc", account.LastChangeUtc);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"status:  {(report.Ok ? "ok" : "degraded")}");
        output.Line($"store:   v{report.SchemaVersion} at {report.StorePath}");
        output.Line($"secrets: {report.SecretBackend}");
        output.Line($"gates:   send={(host.Policy.Options.SendEnabled ? "on" : "off")} sql={(host.Policy.Options.RawSqlEnabled ? "on" : "off")}");
        if (report.StuckSends > 0) output.Line($"warning: {report.StuckSends} send(s) stuck mid-dispatch");

        foreach (var account in report.Accounts)
        {
            output.Line(
                $"{account.AccountId.Value,4}  {account.Email}  imap={Wire(account.ImapState)}  "
                + $"smtp={Wire(account.SmtpState)}  unread={account.UnreadCount}");
        }

        return ExitCodes.Ok;
    }

    /// <summary>A one-shot invocation holds no connections, so a disconnected reading is expected.</summary>
    private static string Wire(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "connected",
        ConnectionState.Connecting => "connecting",
        ConnectionState.AuthRequired => "auth-required",
        ConnectionState.Error => "error",
        _ => "disconnected",
    };
}
