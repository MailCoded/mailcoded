using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Core.Auth;

/// <summary>Where an account's credential lives. An OAuth account holds no password: its credential is
/// the token cache beside the ref, so looking only at the ref reports every signed-in account as bare.</summary>
public static class AccountCredentials
{
    public static string RefFor(AccountConfig account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return account.Auth == AuthKind.OAuth2 ? OAuthOptions.CacheRefFor(account.SecretRef) : account.SecretRef;
    }

    public static async Task<bool> ExistsAsync(AccountConfig account, ISecretStore secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(secrets);

        var value = await secrets.GetAsync(RefFor(account), ct).ConfigureAwait(false);
        return !string.IsNullOrEmpty(value);
    }
}
