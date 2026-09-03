using System.Globalization;
using Mailcoded.Core.Auth;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Proves an account's settings and credential still work, changing nothing. The first thing to
/// reach for when sync starts failing, because it separates "wrong host" from "dead credential".
/// </summary>
internal static class AccountTestCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var account = host.RequireAccount(line, ct);
        var checkSmtp = !line.Flag("no-smtp") && account.Smtp is not null;

        var imap = await ProbeImapAsync(host, account, ct).ConfigureAwait(false);
        var smtp = checkSmtp ? await ProbeSmtpAsync(host, account, ct).ConfigureAwait(false) : null;

        var ok = imap.Ok && (smtp?.Ok ?? true);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("email", account.Email);
            writer.WriteBoolean("ok", ok);
            WriteProbe(writer, "imap", imap);
            if (smtp is { } s) WriteProbe(writer, "smtp", s);
            else writer.WriteNull("smtp");
            output.EndJson(writer);
            return ok ? ExitCodes.Ok : imap.ExitCode;
        }

        output.Line($"account {account.Id.Value.ToString(CultureInfo.InvariantCulture)}  {account.Email}");
        output.Line($"  imap  {Describe(imap)}");
        if (smtp is { } line2) output.Line($"  smtp  {Describe(line2)}");
        else if (account.Smtp is null) output.Line("  smtp  not configured");

        if (!ok)
        {
            output.Blank();
            output.Line(imap.Ok ? smtp!.Detail : imap.Detail);
        }

        return ok ? ExitCodes.Ok : imap.Ok ? smtp!.ExitCode : imap.ExitCode;
    }

    private static async Task<Probe> ProbeImapAsync(CliHost host, AccountConfig account, CancellationToken ct)
    {
        try
        {
            await using var provider = await host.ConnectProviderAsync(account, ct).ConfigureAwait(false);
            var folders = await provider.ListFoldersAsync(ct).ConfigureAwait(false);
            return new Probe(
                true,
                $"ok — {folders.Count.ToString(CultureInfo.InvariantCulture)} folders, "
                    + $"{Capabilities(provider.Capabilities)}",
                string.Empty,
                ExitCodes.Ok);
        }
        catch (ReauthorizationRequiredException ex)
        {
            return new Probe(false, "sign-in expired", ex.Message, ExitCodes.Auth);
        }
        catch (ProviderException ex)
        {
            return new Probe(false, $"failed ({ex.Category.ToString().ToLowerInvariant()})", ex.Message, MapExit(ex));
        }
    }

    private static async Task<Probe> ProbeSmtpAsync(CliHost host, AccountConfig account, CancellationToken ct)
    {
        try
        {
            await using var sender = await host.ConnectSenderAsync(account, ct).ConfigureAwait(false);
            var size = sender.MaxMessageSize is { } max
                ? $", max message {(max / 1024 / 1024).ToString(CultureInfo.InvariantCulture)} MB"
                : string.Empty;
            return new Probe(
                true,
                $"ok — SMTPUTF8 {(sender.SupportsSmtpUtf8 ? "yes" : "no")}{size}",
                string.Empty,
                ExitCodes.Ok);
        }
        catch (ReauthorizationRequiredException ex)
        {
            return new Probe(false, "sign-in expired", ex.Message, ExitCodes.Auth);
        }
        catch (ProviderException ex)
        {
            return new Probe(false, $"failed ({ex.Category.ToString().ToLowerInvariant()})", ex.Message, MapExit(ex));
        }
    }

    private static string Capabilities(Mailcoded.Core.Domain.Sync.ServerCaps caps)
    {
        var parts = new List<string>(4);
        if (caps.Qresync) parts.Add("QRESYNC");
        else if (caps.Condstore) parts.Add("CONDSTORE");
        else parts.Add("no delta extension");
        if (caps.Idle) parts.Add("IDLE");
        if (caps.Move) parts.Add("MOVE");
        return string.Join(", ", parts);
    }

    private static int MapExit(ProviderException ex) => ex.Category switch
    {
        FailureCategory.Auth => ExitCodes.Auth,
        FailureCategory.Network => ExitCodes.Network,
        FailureCategory.NotFound => ExitCodes.NotFound,
        FailureCategory.Unsupported => ExitCodes.Unsupported,
        _ => ExitCodes.Internal,
    };

    private static string Describe(Probe probe) => probe.Summary;

    private static void WriteProbe(System.Text.Json.Utf8JsonWriter writer, string name, Probe probe)
    {
        writer.WriteStartObject(name);
        writer.WriteBoolean("ok", probe.Ok);
        writer.WriteString("summary", probe.Summary);
        JsonFields.WriteText(writer, "detail", probe.Detail.Length == 0 ? null : probe.Detail);
        writer.WriteEndObject();
    }

    private sealed record Probe(bool Ok, string Summary, string Detail, int ExitCode);
}
