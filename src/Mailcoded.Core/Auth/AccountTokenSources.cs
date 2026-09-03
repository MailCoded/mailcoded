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
    public static IAccessTokenSource? For(AccountConfig account, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(secrets);

        return account.Auth == AuthKind.OAuth2
            ? new MicrosoftOAuth(OAuthOptions.FromEnvironment(), secrets)
            : null;
    }
}
