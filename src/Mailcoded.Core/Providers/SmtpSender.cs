using Mailcoded.Core.Auth;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Core.Providers;

/// <summary>
/// The SMTP adapter. Reports the server's SIZE and SMTPUTF8 support so the outbox can reject a
/// submission before it is attempted, and classifies every refusal into the retry policy.
/// </summary>
public sealed class SmtpSender : IMailSender
{
    private readonly IClock clock;
    private readonly MailTransportOptions options;
    private readonly ImapCommandQueue queue = new();

    private AccountConfig? config;
    private ISecretStore? secrets;
    private SmtpClient? client;
    private bool submissionReady;
    private long lastActivityTicks;
    private int disposed;

    private readonly IAccessTokenSource? tokens;

    public SmtpSender(
        IClock? clock = null,
        MailTransportOptions? options = null,
        IAccessTokenSource? tokens = null)
    {
        this.clock = clock ?? SystemClock.Instance;
        this.options = options ?? MailTransportOptions.Default;
        this.tokens = tokens;
    }

    public long? MaxMessageSize { get; private set; }

    public bool SupportsSmtpUtf8 { get; private set; }

    /// <summary>An unauthenticated relay that never advertises AUTH is a legitimate submission
    /// path, so readiness is "auth was not required, or it was required and it succeeded".</summary>
    public bool IsConnected => submissionReady && client is { IsConnected: true };

    public Task ConnectAsync(AccountConfig cfg, ISecretStore secretStore, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(secretStore);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var smtp = cfg.Smtp
            ?? throw new ProviderException(FailureCategory.Unsupported, "This account has no SMTP configuration.");

        config = cfg;
        secrets = secretStore;

        return queue.RunAsync(token => ConnectCoreAsync(cfg, smtp, secretStore, token), ct);
    }

    private async Task ConnectCoreAsync(
        AccountConfig cfg,
        SmtpConfig smtp,
        ISecretStore secretStore,
        CancellationToken ct)
    {
        await CloseAsync().ConfigureAwait(false);

        var next = new SmtpClient
        {
            Timeout = options.CommandTimeoutMs,
            ServerCertificateValidationCallback = (_, certificate, _, errors) =>
                CertificateIsAcceptable(cfg, smtp.Host, certificate, errors),
        };

        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // 31: a host that advertises AAAA over a broken IPv6 path stalls rather than refusing.
                connectCts.CancelAfter(options.ConnectTimeoutMs);

                try
                {
                    await next.ConnectAsync(
                        smtp.Host,
                        smtp.Port,
                        ImapCapabilityMap.ToSocketOptions(smtp.Security),
                        connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ProviderException(FailureCategory.Network, $"Timed out connecting to {smtp.Host}:{smtp.Port}.");
                }
            }

            var authenticationRequired = await AuthenticateAsync(next, cfg, smtp, secretStore, ct, tokens).ConfigureAwait(false);
            if (authenticationRequired && !next.IsAuthenticated)
            {
                throw new ProviderException(
                    FailureCategory.Auth,
                    "The server advertised AUTH but the session did not authenticate.");
            }
        }
        catch (ProviderException)
        {
            next.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            next.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            next.Dispose();
            throw ProviderErrors.Smtp(ex, "connect");
        }

        client = next;
        submissionReady = true;
        lastActivityTicks = clock.Ticks;
        MaxMessageSize = next.Capabilities.HasFlag(SmtpCapabilities.Size) && next.MaxSize > 0 ? (long)next.MaxSize : null;
        SupportsSmtpUtf8 = next.Capabilities.HasFlag(SmtpCapabilities.UTF8);
    }

    /// <summary>Every command is queued: MailKit allows one in flight per client, and two RPC
    /// requests share this sender, so an overlapping submission would desync the reply stream.</summary>
    public Task<string> SendAsync(
        byte[] raw,
        EmailAddress from,
        IReadOnlyList<EmailAddress> recipients,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(recipients);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (raw.Length == 0) throw new ArgumentException("Cannot send an empty message.", nameof(raw));
        if (recipients.Count == 0) throw new ArgumentException("A message needs at least one recipient.", nameof(recipients));

        return queue.RunAsync<string>(token => SendCoreAsync(raw, from, recipients, token), ct);
    }

    private async Task<string> SendCoreAsync(
        byte[] raw,
        EmailAddress from,
        IReadOnlyList<EmailAddress> recipients,
        CancellationToken ct)
    {
        var live = client ?? throw new ProviderException(FailureCategory.Network, "The SMTP connection has not been established.");
        if (!live.IsConnected || !submissionReady)
            throw new ProviderException(FailureCategory.Network, "The SMTP connection is not usable; a reconnect is required.");

        if (MaxMessageSize is { } limit && raw.LongLength > limit)
        {
            throw new ProviderException(
                FailureCategory.Full,
                $"Message is {raw.LongLength} bytes; the server SIZE limit is {limit} bytes.");
        }

        var needsUtf8 = RequiresSmtpUtf8(from, recipients);

        // 29: an EAI recipient on a server without SMTPUTF8 must fail loudly. Down-converting the
        // address would silently deliver to the wrong mailbox or to nobody at all.
        if (needsUtf8 && !SupportsSmtpUtf8)
        {
            throw new ProviderException(
                FailureCategory.Unsupported,
                "A recipient address needs SMTPUTF8 but the server does not advertise it.");
        }

        MimeMessage message;
        using (var source = new MemoryStream(raw, writable: false))
        {
            try
            {
                message = await MimeMessage.LoadAsync(source, ct).ConfigureAwait(false);
            }
            catch (FormatException ex)
            {
                throw new ProviderException(FailureCategory.Protocol, "The queued message could not be parsed before submission.", ex);
            }
        }

        var format = FormatOptions.Default.Clone();
        format.International = needsUtf8;

        var sender = new MailboxAddress(string.Empty, from.Value);
        var envelopeRecipients = new List<MailboxAddress>(recipients.Count);
        foreach (var recipient in recipients)
            envelopeRecipients.Add(new MailboxAddress(string.Empty, recipient.Value));

        await ProbeAsync(live, ct).ConfigureAwait(false);

        try
        {
            var response = await live.SendAsync(format, message, sender, envelopeRecipients, ct).ConfigureAwait(false);
            lastActivityTicks = clock.Ticks;
            return ProviderErrors.SanitizeDetail(response, 512);
        }
        catch (SmtpCommandException ex)
        {
            throw Classify(ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ProviderErrors.Smtp(ex, "send");
        }
    }

    /// <summary>
    /// IsConnected is a cached socket flag, so a connection that has gone quiet is NOOP-probed
    /// before a submission rather than discovering the drop half-way through DATA.
    /// </summary>
    private async Task ProbeAsync(SmtpClient live, CancellationToken ct)
    {
        if (clock.Ticks - lastActivityTicks < options.LivenessProbeIntervalMs) return;

        try
        {
            await live.NoOpAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ProviderErrors.Smtp(ex, "liveness probe");
        }

        lastActivityTicks = clock.Ticks;
    }

    /// <summary>Drops and rebuilds the connection, which is what a 421 reply requires.</summary>
    public async Task ReconnectAsync(CancellationToken ct)
    {
        var cfg = config ?? throw new ProviderException(FailureCategory.Network, "Connect before reconnecting.");
        var store = secrets ?? throw new ProviderException(FailureCategory.Network, "Connect before reconnecting.");

        await ConnectAsync(cfg, store, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        await CloseAsync().ConfigureAwait(false);
        queue.Dispose();
    }

    private static SmtpDeliveryException Classify(SmtpCommandException ex)
    {
        var status = (int)ex.StatusCode;
        var enhanced = ExtractEnhancedStatus(ex.Message);
        var detail = ProviderErrors.SanitizeDetail(ex.Message, 256);
        var prefix = enhanced is null ? string.Empty : $"{enhanced} ";

        return SmtpDeliveryException.Create(status, $"SMTP {status} {prefix}{detail}".TrimEnd(), enhanced, ex);
    }

    /// <summary>Pulls an RFC 3463 <c>x.y.z</c> status out of the reply text, if the server sent one.</summary>
    internal static string? ExtractEnhancedStatus(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;

        foreach (var token in message.Split(' ', '\t', '\r', '\n'))
        {
            var parts = token.Split('.');
            if (parts.Length != 3) continue;
            if (parts[0].Length != 1 || parts[0][0] is not ('2' or '4' or '5')) continue;
            if (!IsShortNumber(parts[1]) || !IsShortNumber(parts[2])) continue;

            return token;
        }

        return null;
    }

    private static bool IsShortNumber(string value)
    {
        if (value.Length is 0 or > 3) return false;

        foreach (var c in value)
            if (!char.IsAsciiDigit(c)) return false;

        return true;
    }

    private static bool RequiresSmtpUtf8(EmailAddress from, IReadOnlyList<EmailAddress> recipients)
    {
        if (from.RequiresSmtpUtf8()) return true;

        foreach (var recipient in recipients)
            if (recipient.RequiresSmtpUtf8()) return true;

        return false;
    }

    /// <summary>True when the server advertised AUTH, so the caller must verify it succeeded.</summary>
    private static async Task<bool> AuthenticateAsync(
        SmtpClient target,
        AccountConfig cfg,
        SmtpConfig smtp,
        ISecretStore secrets,
        CancellationToken ct,
        IAccessTokenSource? tokens)
    {
        if (!target.Capabilities.HasFlag(SmtpCapabilities.Authentication)) return false;

        var user = smtp.Username;
        if (string.IsNullOrWhiteSpace(user)) user = cfg.Email;

        try
        {
            // Access tokens expire; ask the source for a live one rather than reusing a stored copy.
            var secret = cfg.Auth == AuthKind.OAuth2 && tokens is not null
                ? await tokens.GetAccessTokenAsync(cfg, ct).ConfigureAwait(false)
                : await secrets.GetAsync(cfg.SecretRef, ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(secret))
            {
                throw new ProviderException(
                    FailureCategory.Auth,
                    cfg.Auth == AuthKind.OAuth2
                        ? "No OAuth2 access token is stored for this account."
                        : "No password is stored for this account.");
            }

            if (cfg.Auth == AuthKind.OAuth2)
                await target.AuthenticateAsync(new SaslMechanismOAuth2(user, secret), ct).ConfigureAwait(false);
            else
                await target.AuthenticateAsync(user, secret, ct).ConfigureAwait(false);

            return true;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SecretStoreException ex)
        {
            throw new ProviderException(FailureCategory.Auth, "The credential could not be read from the secret store.", ex);
        }
        catch (Exception ex)
        {
            throw ProviderErrors.Smtp(ex, "authenticate");
        }
    }

    private static bool CertificateIsAcceptable(AccountConfig cfg, string host, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;

        // 10: only a user-pinned certificate on a loopback bridge may bypass validation.
        var pinned = cfg.Quirks.PinnedCertificateSha256;
        if (string.IsNullOrWhiteSpace(pinned) || certificate is null) return false;
        if (!ImapCapabilityMap.IsLoopbackHost(host)) return false;

        var expected = pinned.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task CloseAsync()
    {
        var current = client;
        client = null;
        submissionReady = false;
        MaxMessageSize = null;
        SupportsSmtpUtf8 = false;

        if (current is null) return;

        try
        {
            if (current.IsConnected)
                await current.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort QUIT; the socket is being torn down either way.
        }
        finally
        {
            current.Dispose();
        }
    }
}
