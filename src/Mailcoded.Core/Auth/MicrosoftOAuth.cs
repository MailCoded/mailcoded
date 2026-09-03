using Microsoft.Identity.Client;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Core.Auth;

/// <summary>
/// Microsoft sign-in over MSAL's device-code flow, with the refresh grant kept in the secret
/// store rather than on disk. Access tokens are short-lived, so every call refreshes silently
/// and only falls back to a human sign-in when the grant itself is gone.
/// </summary>
public sealed class MicrosoftOAuth : IAccessTokenSource
{
    private readonly OAuthOptions _options;
    private readonly ISecretStore _secrets;
    private readonly MicrosoftScopeSet _scopeSet;
    private readonly Func<DeviceCodePrompt, CancellationToken, Task>? _onDeviceCode;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MicrosoftOAuth(
        OAuthOptions options,
        ISecretStore secrets,
        MicrosoftScopeSet scopeSet = MicrosoftScopeSet.MailProtocols,
        Func<DeviceCodePrompt, CancellationToken, Task>? onDeviceCode = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);

        _options = options;
        _secrets = secrets;
        _scopeSet = scopeSet;
        _onDeviceCode = onDeviceCode;
    }

    /// <summary>True when a human sign-in is possible; without it only silent refresh can work.</summary>
    public bool CanPromptForSignIn => _onDeviceCode is not null;

    public async Task<string> GetAccessTokenAsync(AccountConfig account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        // MSAL's cache is not safe for concurrent use, and the sync engine and the send path can
        // both want a token at once.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = await BuildAsync(account.SecretRef, ct).ConfigureAwait(false);
            var scopes = OAuthOptions.ScopesFor(_scopeSet);

            var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
            var cached = accounts.FirstOrDefault();

            if (cached is not null)
            {
                try
                {
                    var silent = await app.AcquireTokenSilent(scopes, cached)
                        .ExecuteAsync(ct).ConfigureAwait(false);
                    await PersistAsync(app, account.SecretRef, ct).ConfigureAwait(false);
                    return silent.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // Falls through to a fresh sign-in below.
                }
            }

            if (_onDeviceCode is null)
            {
                throw new ReauthorizationRequiredException(
                    $"{account.Email} needs an interactive Microsoft sign-in. Run 'mailcoded setup' "
                    + "again, or 'mailcoded account reauth', from a terminal.");
            }

            var result = await SignInAsync(app, scopes, ct).ConfigureAwait(false);
            await PersistAsync(app, account.SecretRef, ct).ConfigureAwait(false);
            return result.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs a device-code sign-in and stores the resulting grant. Used by onboarding.</summary>
    public async Task<string> SignInAsync(string secretRef, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        if (_onDeviceCode is null)
            throw new ReauthorizationRequiredException("Signing in needs an interactive terminal.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var app = await BuildAsync(secretRef, ct).ConfigureAwait(false);
            var result = await SignInAsync(app, OAuthOptions.ScopesFor(_scopeSet), ct).ConfigureAwait(false);
            await PersistAsync(app, secretRef, ct).ConfigureAwait(false);
            return result.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AuthenticationResult> SignInAsync(
        IPublicClientApplication app,
        IReadOnlyList<string> scopes,
        CancellationToken ct)
    {
        try
        {
            return await app.AcquireTokenWithDeviceCode(scopes, async code =>
            {
                await _onDeviceCode!(
                    new DeviceCodePrompt
                    {
                        VerificationUrl = code.VerificationUrl,
                        UserCode = code.UserCode,
                        Message = code.Message,
                        ExpiresUtc = code.ExpiresOn,
                    },
                    ct).ConfigureAwait(false);
            }).ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (MsalServiceException ex) when (IsClientRejected(ex))
        {
            throw new ReauthorizationRequiredException(
                "Microsoft rejected the OAuth client id"
                + (_options.UsesPlaceholderClientId
                    ? $" ({ex.ErrorCode}). mailcoded is using a placeholder client id that is not its own, "
                      + "which Microsoft can refuse or revoke at any time. Register a public-client app "
                      + $"with device code flow enabled and set {OAuthOptions.ClientIdEnvVar}."
                    : $" ({ex.ErrorCode}). Check that the app allows public client flows and that device "
                      + "code flow is enabled."),
                ex);
        }
    }

    private static bool IsClientRejected(MsalServiceException ex) =>
        ex.ErrorCode is "invalid_client" or "unauthorized_client" or "invalid_request"
        || ex.ErrorCode == "AADSTS7000218"
        || ex.Message.Contains("AADSTS700016", StringComparison.Ordinal)
        || ex.Message.Contains("AADSTS7000218", StringComparison.Ordinal);

    private Task<IPublicClientApplication> BuildAsync(string secretRef, CancellationToken ct)
    {
        var app = PublicClientApplicationBuilder
            .Create(_options.ClientId)
            .WithAuthority(_options.Authority)
            .WithClientName("mailcoded")
            .Build();

        var cacheRef = OAuthOptions.CacheRefFor(secretRef);

        // The grant lives in the secret store, never on disk: MSAL's own file cache would put a
        // refresh token in a plain file, which invariant 3 forbids.
        app.UserTokenCache.SetBeforeAccessAsync(async args =>
        {
            var stored = await _secrets.GetAsync(cacheRef, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(stored)) return;

            try
            {
                args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(stored));
            }
            catch (FormatException)
            {
                // A corrupt cache is not fatal: sign in again rather than refusing to start.
            }
            catch (MsalClientException)
            {
            }
        });

        app.UserTokenCache.SetAfterAccessAsync(async args =>
        {
            if (!args.HasStateChanged) return;

            var bytes = args.TokenCache.SerializeMsalV3();
            await _secrets
                .SetAsync(cacheRef, Convert.ToBase64String(bytes), ct)
                .ConfigureAwait(false);
        });

        return Task.FromResult<IPublicClientApplication>(app);
    }

    /// <summary>The cache callbacks already persisted anything that changed.</summary>
    private static Task PersistAsync(IPublicClientApplication app, string secretRef, CancellationToken ct) =>
        Task.CompletedTask;
}
