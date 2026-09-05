using Mailcoded.Core.Auth;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Core.Providers;

/// <summary>Proves settings and a credential work, without writing anything: connect, list folders,
/// disconnect. The one place both onboarding paths verify from, so they cannot drift apart.</summary>
public static class ImapProbe
{
    /// <summary>Returns the folder count on success; throws <see cref="ProviderException"/> otherwise,
    /// so the caller's usual error mapping applies unchanged.</summary>
    public static async Task<int> VerifyAsync(
        AccountConfig config,
        ISecretStore secrets,
        IClock? clock = null,
        MailTransportOptions? options = null,
        IAccessTokenSource? tokens = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);

        await using var provider = new ImapProvider(clock, options, tokens);

        await provider.ConnectAsync(config, secrets, ct).ConfigureAwait(false);
        var folders = await provider.ListFoldersAsync(ct).ConfigureAwait(false);

        return folders.Count;
    }
}
