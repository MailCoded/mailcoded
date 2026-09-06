using Mailcoded.Core.Auth;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Imap;
using MailKit.Security;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Core.Providers;

/// <summary>One connected, authenticated IMAP client plus what the handshake taught us about it.</summary>
internal sealed class ImapConnection
{
    public required ImapClient Client { get; init; }
    public required ServerCaps Caps { get; init; }
    public required ServerQuirks Quirks { get; init; }
    public required bool QuickResyncEnabled { get; init; }
}

/// <summary>
/// Builds IMAP connections: TLS policy, credential retrieval, IMAP ID, and capability detection.
/// The credential is read from <see cref="ISecretStore"/> at the last moment and never stored,
/// logged, or echoed into an exception.
/// </summary>
internal static class ImapConnectionFactory
{
    public static async Task<ImapConnection> ConnectAsync(
        AccountConfig cfg,
        ISecretStore secrets,
        MailTransportOptions options,
        CancellationToken ct,
        IAccessTokenSource? tokens = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);

        // Without a credential the session can only end at AUTHENTICATE, so the wait is spent
        // before the socket rather than after a full connect timeout.
        if (!await AccountCredentials.ExistsAsync(cfg, secrets, ct).ConfigureAwait(false))
        {
            throw new ProviderException(
                FailureCategory.Auth,
                $"No stored credential for {cfg.Email}. Add one with 'mailcoded account reauth'.");
        }

        var client = new ImapClient
        {
            Timeout = options.CommandTimeoutMs,
            ServerCertificateValidationCallback = (_, certificate, _, errors) =>
                CertificateIsAcceptable(cfg, certificate, errors),
        };

        try
        {
            await OpenSocketAsync(client, cfg, options, ct).ConfigureAwait(false);
            await AuthenticateAsync(client, cfg, secrets, ct, tokens).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var quirks = ImapCapabilityMap.DetectQuirks(cfg, client.Capabilities);

        try
        {
            // 11: Yahoo refuses further commands until the client identifies itself.
            if (quirks.HasFlag(ServerQuirks.RequiresId) && client.Capabilities.HasFlag(ImapCapabilities.Id))
                await IdentifyAsync(client, options, ct).ConfigureAwait(false);

            // 21: UTF8=ACCEPT lets non-ASCII folder names arrive verbatim instead of as mUTF-7.
            if (client.Capabilities.HasFlag(ImapCapabilities.UTF8Accept))
                await TryEnableUtf8Async(client, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw ProviderErrors.Imap(ex, "session setup");
        }

        var quickResync = false;
        if (client.Capabilities.HasFlag(ImapCapabilities.QuickResync) && !quirks.HasFlag(ServerQuirks.QresyncBroken))
        {
            try
            {
                // ENABLE is only legal in the authenticated state, so QRESYNC is negotiated here
                // rather than lazily at the first SELECT.
                await client.EnableQuickResyncAsync(ct).ConfigureAwait(false);
                quickResync = true;
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                throw;
            }
            catch (Exception)
            {
                quirks |= ServerQuirks.QresyncBroken;
            }
        }

        return new ImapConnection
        {
            Client = client,
            Caps = ImapCapabilityMap.ToServerCaps(client.Capabilities, quirks),
            Quirks = quirks,
            QuickResyncEnabled = quickResync,
        };
    }

    public static async Task CloseAsync(ImapClient client)
    {
        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A best-effort LOGOUT; the socket is being torn down either way.
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task OpenSocketAsync(ImapClient client, AccountConfig cfg, MailTransportOptions options, CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 31: a host that advertises AAAA over a broken IPv6 path stalls rather than refusing.
        connectCts.CancelAfter(options.ConnectTimeoutMs);

        try
        {
            var socket = await DualStackConnector
                .ConnectAsync(cfg.Imap.Host, cfg.Imap.Port, TimeSpan.FromMilliseconds(options.ConnectTimeoutMs), connectCts.Token)
                .ConfigureAwait(false);

            await client.ConnectAsync(
                socket,
                cfg.Imap.Host,
                cfg.Imap.Port,
                ImapCapabilityMap.ToSocketOptions(cfg.Imap.Security),
                connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(
                FailureCategory.Network,
                $"Timed out connecting to {cfg.Imap.Host}:{cfg.Imap.Port}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException)
        {
            // The connector already classified this; re-mapping it would call a refusal a protocol error.
            throw;
        }
        catch (Exception ex)
        {
            throw ProviderErrors.Imap(ex, "connect");
        }
    }

    private static async Task AuthenticateAsync(
        ImapClient client,
        AccountConfig cfg,
        ISecretStore secrets,
        CancellationToken ct,
        IAccessTokenSource? tokens = null)
    {
        var user = cfg.Imap.Username;
        if (string.IsNullOrWhiteSpace(user)) user = cfg.Email;

        try
        {
            // An access token expires roughly hourly, so an OAuth account asks the token source
            // for a fresh one rather than reusing whatever was last written to the secret store.
            var secret = cfg.Auth == AuthKind.OAuth2 && tokens is not null
                ? await tokens.GetAccessTokenAsync(cfg, ct).ConfigureAwait(false)
                : await secrets.GetAsync(cfg.SecretRef, ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(secret))
            {
                throw new ProviderException(
                    FailureCategory.Auth,
                    cfg.Auth == AuthKind.OAuth2
                        ? (tokens is null
                            ? "This account signs in with Microsoft, but this process was built "
                              + "without a token source, so it cannot present one."
                            : "Microsoft would not renew the sign-in for this account. Run "
                              + "'mailcoded account reauth'.")
                        : "No password is stored for this account.");
            }

            if (cfg.Auth == AuthKind.OAuth2)
                await client.AuthenticateAsync(new SaslMechanismOAuth2(user, secret), ct).ConfigureAwait(false);
            else
                await client.AuthenticateAsync(user, secret, ct).ConfigureAwait(false);
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
            throw ProviderErrors.Imap(ex, "authenticate");
        }
    }

    private static async Task IdentifyAsync(ImapClient client, MailTransportOptions options, CancellationToken ct)
    {
        var implementation = new ImapImplementation
        {
            Name = options.ClientName,
            Version = options.ClientVersion,
        };

        try
        {
            await client.IdentifyAsync(implementation, ct).ConfigureAwait(false);
        }
        catch (ImapCommandException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static async Task TryEnableUtf8Async(ImapClient client, CancellationToken ct)
    {
        try
        {
            await client.EnableUTF8Async(ct).ConfigureAwait(false);
        }
        catch (ImapCommandException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static bool CertificateIsAcceptable(AccountConfig cfg, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;

        // 10: a ProtonBridge-style loopback listener presents a self-signed certificate. Accept it
        // only when the user pinned that exact certificate and only on a loopback host.
        var pinned = cfg.Quirks.PinnedCertificateSha256;
        if (string.IsNullOrWhiteSpace(pinned) || certificate is null) return false;
        if (!ImapCapabilityMap.IsLoopbackHost(cfg.Imap.Host)) return false;

        var expected = pinned.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
