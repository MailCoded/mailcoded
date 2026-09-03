using System.Globalization;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Interactive onboarding. Resolves provider settings from the address, explains what credential
/// the provider actually wants, proves the settings work, and only then writes anything down.
/// </summary>
internal static class SetupCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);

        if (!Prompt.IsInteractive)
        {
            throw new CliUsageException(
                "'setup' is interactive and needs a terminal. For scripts use 'mailcoded account add' "
                + "with explicit options and --password-stdin.");
        }

        Prompt.Heading("mailcoded setup");
        Prompt.Say("Adds a mail account to this machine. Nothing is saved until a real login succeeds.");
        Prompt.Say("Your mail stays here; the credential goes to the OS keyring, never to the database.");

        var email = ReadEmail(line);
        var preset = ProviderPresets.ForEmail(email.Value);

        Prompt.Heading($"Provider: {preset.DisplayName}");

        if (preset.Discouraged is { } warning)
        {
            Prompt.Say(warning);
            Prompt.Say();
            if (!Prompt.Confirm("Try anyway with a password or app password?", false))
            {
                Prompt.Say("Nothing was saved. OAuth sign-in for this provider is tracked in ROADMAP.md.");
                return ExitCodes.Unsupported;
            }

            Prompt.Say();
        }

        if (preset.IsGuess) Prompt.Say(preset.Advice ?? string.Empty);
        else Prompt.Say("Settings are built in, so there is nothing to look up.");

        var settings = ConfirmSettings(preset, line);

        Prompt.Heading("Credential");
        if (preset.Credential == CredentialStyle.AppPassword)
        {
            Prompt.Say(preset.Advice ?? "This provider requires an app password.");
            if (preset.CredentialUrl is { } url) Prompt.Say($"Create one at: {url}");
            Prompt.Say();
        }
        else if (preset.Advice is { } advice && !preset.IsGuess)
        {
            Prompt.Say(advice);
            Prompt.Say();
        }

        Prompt.Say("The credential is not echoed and never becomes a command-line argument.");
        var secret = Prompt.AskSecret("Password");
        if (secret.Length == 0) throw new CliUsageException("An empty credential cannot be verified.");

        Prompt.Heading("Verifying");
        var verified = await VerifyAsync(host, email, settings, secret, ct).ConfigureAwait(false);
        if (verified is { } failure)
        {
            Prompt.Say(failure.Headline);
            Prompt.Say();
            foreach (var hint in failure.Hints) Prompt.Say("  - " + hint);
            Prompt.Say();
            Prompt.Say("Nothing was saved. Fix the above and run 'mailcoded setup' again.");
            return failure.ExitCode;
        }

        Prompt.Say("IMAP login succeeded.");

        var accountId = await host.Accounts.AddAsync(
            new AddAccountRequest
            {
                Email = email.Value,
                DisplayName = line.Value("display-name"),
                Provider = ProviderKind.Imap,
                Imap = settings.Imap,
                Smtp = settings.Smtp,
                Auth = AuthKind.Password,
                SecretRef = null,
                Secret = secret,
            },
            host.Caller,
            ct).ConfigureAwait(false);

        Prompt.Heading("Done");
        Prompt.Say($"Account {accountId.Value} added, credential stored in {host.Accounts.SecretBackendName}.");

        var sync = Prompt.Confirm("Sync now? The first sync of a large mailbox takes a while", true);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", accountId.Value);
            writer.WriteString("email", email.Value);
            writer.WriteString("provider", preset.DisplayName);
            writer.WriteString("imap_host", settings.Imap.Host);
            writer.WriteNumber("imap_port", settings.Imap.Port);
            JsonFields.WriteText(writer, "smtp_host", settings.Smtp?.Host);
            writer.WriteBoolean("verified", true);
            writer.WriteBoolean("credential_stored", true);
            writer.WriteString("secret_backend", host.Accounts.SecretBackendName);
            output.EndJson(writer);
        }

        if (!sync)
        {
            Prompt.Say($"Run 'mailcoded sync --account {accountId.Value}' when you are ready.");
            return ExitCodes.Ok;
        }

        Prompt.Say("Syncing...");
        var account = host.Store.GetAccount(accountId, ct)
            ?? throw new CliUsageException("The account vanished immediately after being added.");

        var provider = await host.ConnectProviderAsync(account, ct).ConfigureAwait(false);
        var report = await host.Sync.SyncAccountAsync(provider, accountId, null, ct).ConfigureAwait(false);

        Prompt.Say($"Synced {report.Added} new, {report.Updated} updated, across {report.Folders} folders.");
        Prompt.Say();
        Prompt.Say("Try:  mailcoded search 'is:unread' --json");
        return ExitCodes.Ok;
    }

    private static EmailAddress ReadEmail(CommandLine line)
    {
        var supplied = line.Value("email");
        while (true)
        {
            var raw = supplied ?? Prompt.Ask("Email address");
            supplied = null;

            if (EmailAddress.TryParse(raw, out var parsed)) return parsed;
            Prompt.Say("  That is not a usable address. Try again.");
        }
    }

    private static ConnectionSettings ConfirmSettings(ProviderPreset preset, CommandLine line)
    {
        var imapHost = line.Value("imap-host") ?? preset.ImapHost;
        var imapPort = line.Int("imap-port", 1, 65535) ?? preset.ImapPort;
        var imapSecurity = preset.ImapSecurity;
        var smtpHost = line.Value("smtp-host") ?? preset.SmtpHost;
        var smtpPort = line.Int("smtp-port", 1, 65535) ?? preset.SmtpPort;
        var smtpSecurity = preset.SmtpSecurity;

        Prompt.Say();
        Prompt.Say($"  IMAP  {imapHost}:{imapPort.ToString(CultureInfo.InvariantCulture)} ({Describe(imapSecurity)})");
        Prompt.Say($"  SMTP  {smtpHost}:{smtpPort.ToString(CultureInfo.InvariantCulture)} ({Describe(smtpSecurity)})");
        Prompt.Say();

        if (!Prompt.Confirm("Use these settings?", !preset.IsGuess))
        {
            imapHost = Prompt.Ask("IMAP host", imapHost);
            imapPort = AskPort("IMAP port", imapPort);
            imapSecurity = AskSecurity("IMAP", imapSecurity);
            smtpHost = Prompt.Ask("SMTP host", smtpHost);
            smtpPort = AskPort("SMTP port", smtpPort);
            smtpSecurity = AskSecurity("SMTP", smtpSecurity);
        }

        return new ConnectionSettings(
            new ImapConfig { Host = imapHost, Port = imapPort, Security = imapSecurity },
            new SmtpConfig { Host = smtpHost, Port = smtpPort, Security = smtpSecurity });
    }

    private static int AskPort(string label, int fallback)
    {
        while (true)
        {
            var raw = Prompt.Ask(label, fallback.ToString(CultureInfo.InvariantCulture));
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port is >= 1 and <= 65535)
            {
                return port;
            }

            Prompt.Say("  A port is a number from 1 to 65535.");
        }
    }

    private static SecureSocket AskSecurity(string label, SecureSocket fallback)
    {
        SecureSocket[] modes = [SecureSocket.SslOnConnect, SecureSocket.StartTls, SecureSocket.StartTlsWhenAvailable, SecureSocket.None];
        var index = Array.IndexOf(modes, fallback);
        var chosen = Prompt.Choose(
            $"{label} encryption",
            [.. modes.Select(Describe)],
            index < 0 ? 0 : index);

        return modes[chosen];
    }

    private static string Describe(SecureSocket mode) => mode switch
    {
        SecureSocket.SslOnConnect => "TLS on connect — the usual choice for port 993 and 465",
        SecureSocket.StartTls => "STARTTLS — the usual choice for port 587 and 143",
        SecureSocket.StartTlsWhenAvailable => "STARTTLS if offered, otherwise plaintext",
        SecureSocket.None => "no encryption — only sane for localhost",
        _ => mode.ToString(),
    };

    /// <summary>
    /// Opens a real IMAP session with a throwaway secret store, so a failed attempt leaves no
    /// account behind and no credential in the keyring.
    /// </summary>
    private static async Task<VerificationFailure?> VerifyAsync(
        CliHost host,
        EmailAddress email,
        ConnectionSettings settings,
        string secret,
        CancellationToken ct)
    {
        const string probeRef = "setup-probe";
        var config = new AccountConfig
        {
            Email = email.Value,
            Provider = ProviderKind.Imap,
            Imap = settings.Imap with { Username = email.Value },
            Smtp = settings.Smtp with { Username = email.Value },
            Auth = AuthKind.Password,
            SecretRef = probeRef,
        };

        var scratch = new EphemeralSecretStore(probeRef, secret);
        await using var provider = new ImapProvider(host.Clock, MailTransportOptions.Default);

        try
        {
            await provider.ConnectAsync(config, scratch, ct).ConfigureAwait(false);
            await provider.ListFoldersAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (ProviderException ex)
        {
            return Explain(ex, settings);
        }
    }

    private static VerificationFailure Explain(ProviderException ex, ConnectionSettings settings) => ex.Category switch
    {
        FailureCategory.Auth => new VerificationFailure(
            "The server refused that credential.",
            [
                "If your provider requires an app password, an ordinary account password is always rejected.",
                "Check the username: some providers want the full address, others the part before the @.",
                "Confirm IMAP is switched on in your provider's web settings — several disable it by default.",
            ],
            ExitCodes.Auth),

        FailureCategory.Network => new VerificationFailure(
            $"Could not reach {settings.Imap.Host}:{settings.Imap.Port.ToString(CultureInfo.InvariantCulture)}.",
            [
                "Check the host name for a typo.",
                "Check that the port matches the encryption mode: 993 with TLS on connect, 143 with STARTTLS.",
                "If you are behind a VPN, proxy or captive portal, that will block this too.",
            ],
            ExitCodes.Network),

        _ => new VerificationFailure(
            $"The server rejected the connection: {ex.Message}",
            ["Re-run 'mailcoded setup' and correct the settings when it asks."],
            ExitCodes.Internal),
    };

    private sealed record ConnectionSettings(ImapConfig Imap, SmtpConfig Smtp);

    private sealed record VerificationFailure(string Headline, string[] Hints, int ExitCode);

    /// <summary>
    /// Holds one credential in memory for the duration of a probe. Never writes anything.
    /// Implemented explicitly so none of it is callable except through <see cref="ISecretStore"/>.
    /// </summary>
    private sealed class EphemeralSecretStore(string reference, string value) : ISecretStore
    {
        string ISecretStore.BackendName => "ephemeral";

        bool ISecretStore.IsAvailable => true;

        Task<string?> ISecretStore.GetAsync(string secretRef, CancellationToken ct) =>
            Task.FromResult(string.Equals(secretRef, reference, StringComparison.Ordinal) ? value : null);

        Task ISecretStore.SetAsync(string secretRef, string secretValue, CancellationToken ct) => Task.CompletedTask;

        Task ISecretStore.DeleteAsync(string secretRef, CancellationToken ct) => Task.CompletedTask;
    }
}
