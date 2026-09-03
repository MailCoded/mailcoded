using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Core.Auth;

/// <summary>The token source an OAuth account needs to connect at all. Without one, the provider
/// reads the account's own secret ref, which for OAuth holds nothing.</summary>
public static class AccountTokenSources
{
    /// <summary>Null for a password account, which needs none. No device-code callback: a refresh
    /// is silent, and a grant that cannot be refreshed must say so rather than prompt where
    /// nobody is watching.</summary>
    /// <remarks>Options come from the environment, because AccountConfig does not persist the
    /// client id a --client-id sign-in used. Such an account needs MAILCODED_OAUTH_CLIENT_ID set
    /// wherever it runs, or its cached grant will not be found.</remarks>
    public static IAccessTokenSource? For(AccountConfig account, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(secrets);

        return account.Auth == AuthKind.OAuth2
            ? new MicrosoftOAuth(OAuthOptions.FromEnvironment(), secrets)
            : null;
    }
}
