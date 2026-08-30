using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Registers an account. The credential arrives on stdin and goes straight to the secret store:
/// it is never an argument, never persisted here, and never echoed back.
/// </summary>
internal static class AccountAddCommand
{
    private const int MaxSecretLength = 1024;

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);

        var email = line.RequireValue("email");
        var imapHost = line.RequireValue("imap-host");
        var secret = await ReadSecretAsync(line, ct).ConfigureAwait(false);

        var imap = new ImapConfig
        {
            Host = imapHost,
            Port = line.Int("imap-port", 1, 65535) ?? 993,
            Security = ParseSecurity(line.Value("imap-security"), SecureSocket.SslOnConnect, "--imap-security"),
            Username = line.Value("imap-user") ?? email,
        };

        SmtpConfig? smtp = null;
        if (line.Value("smtp-host") is { } smtpHost)
        {
            smtp = new SmtpConfig
            {
                Host = smtpHost,
                Port = line.Int("smtp-port", 1, 65535) ?? 587,
                Security = ParseSecurity(line.Value("smtp-security"), SecureSocket.StartTls, "--smtp-security"),
                Username = line.Value("smtp-user") ?? email,
            };
        }

        var accountId = await host.Accounts.AddAsync(
            new AddAccountRequest
            {
                Email = email,
                DisplayName = line.Value("display-name"),
                Provider = ProviderKind.Imap,
                Imap = imap,
                Smtp = smtp,
                Auth = AuthKind.Password,
                SecretRef = line.Value("secret-ref"),
                Secret = secret,
            },
            host.Caller,
            ct).ConfigureAwait(false);

        var stored = host.Store.GetAccount(accountId, ct);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", accountId.Value);
            writer.WriteString("email", stored?.Email ?? email);
            writer.WriteString("provider", ProviderKinds.Imap);
            writer.WriteString("auth", AuthKinds.Password);
            writer.WriteString("imap_host", imap.Host);
            writer.WriteNumber("imap_port", imap.Port);
            JsonFields.WriteText(writer, "smtp_host", smtp?.Host);
            if (smtp is { } configured) writer.WriteNumber("smtp_port", configured.Port);
            else writer.WriteNull("smtp_port");
            writer.WriteBoolean("credential_stored", secret is not null);
            writer.WriteString("secret_backend", host.Accounts.SecretBackendName);
            writer.WriteString("next", "mailcoded sync --account " + accountId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"account_id: {accountId.Value}");
        output.Line($"email:      {stored?.Email ?? email}");
        output.Line($"imap:       {imap.Host}:{imap.Port}");
        if (smtp is { } smtpConfig) output.Line($"smtp:       {smtpConfig.Host}:{smtpConfig.Port}");
        output.Line($"secrets:    {host.Accounts.SecretBackendName} ({(secret is null ? "no credential stored" : "credential stored")})");
        output.Line($"next:       mailcoded sync --account {accountId.Value}");
        return ExitCodes.Ok;
    }

    private static async Task<string?> ReadSecretAsync(CommandLine line, CancellationToken ct)
    {
        var fromStdin = line.Flag("password-stdin");
        var none = line.Flag("no-password");

        if (fromStdin && none)
            throw new CliUsageException("Pass either --password-stdin or --no-password, not both.");

        if (none) return null;

        if (!fromStdin)
        {
            throw new CliUsageException(
                "A credential is required: pipe it in with --password-stdin, or pass --no-password to "
                + "register the account and store the credential later. A password is never accepted "
                + "as a command-line argument.");
        }

        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var value = raw.TrimEnd('\r', '\n');

        if (value.Length == 0) throw new CliUsageException("--password-stdin read an empty credential.");
        if (value.Length > MaxSecretLength)
            throw new CliUsageException("The credential read from stdin is implausibly long; check the pipe.");

        return value;
    }

    private static SecureSocket ParseSecurity(string? value, SecureSocket fallback, string option) => value switch
    {
        null or "" => fallback,
        SecurityModes.None => SecureSocket.None,
        SecurityModes.SslOnConnect => SecureSocket.SslOnConnect,
        SecurityModes.StartTls => SecureSocket.StartTls,
        SecurityModes.StartTlsWhenAvailable => SecureSocket.StartTlsWhenAvailable,
        _ => throw new CliUsageException(
            $"{option} must be one of none, sslOnConnect, startTls, startTlsWhenAvailable — not '{value}'."),
    };
}
