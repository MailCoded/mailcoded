namespace Mailcoded.Core.Auth;

/// <summary>Which Microsoft surface a token is being requested for.</summary>
public enum MicrosoftScopeSet
{
    /// <summary>IMAP and SMTP against outlook.office.com.</summary>
    MailProtocols,

    /// <summary>Microsoft Graph mail.</summary>
    Graph,
}

/// <summary>Client identity and endpoints for Microsoft sign-in.</summary>
public sealed record OAuthOptions
{
    /// <summary>
    /// PLACEHOLDER CLIENT ID — Thunderbird's public registration, reused because mailcoded has no
    /// registration of its own yet. It works only for as long as Microsoft and Mozilla allow it,
    /// it makes the consent screen name Thunderbird rather than mailcoded, and it can be revoked
    /// without warning. Replace it before any public release: register a public-client app,
    /// enable device code flow, and set <c>MAILCODED_OAUTH_CLIENT_ID</c> or pass --client-id.
    /// </summary>
    public const string PlaceholderClientId = "9e5f94bc-e8a4-4e73-b8be-63364c29d753";

    public const string ClientIdEnvVar = "MAILCODED_OAUTH_CLIENT_ID";
    public const string TenantEnvVar = "MAILCODED_OAUTH_TENANT";

    /// <summary>
    /// <c>common</c> accepts both personal and work accounts. <c>consumers</c> is personal-only,
    /// <c>organizations</c> work-only, or a tenant GUID for one organization.
    /// </summary>
    public string Tenant { get; init; } = "common";

    public string ClientId { get; init; } = PlaceholderClientId;

    public bool UsesPlaceholderClientId => string.Equals(ClientId, PlaceholderClientId, StringComparison.Ordinal);

    public string Authority => $"https://login.microsoftonline.com/{Tenant}";

    public static OAuthOptions FromEnvironment()
    {
        var clientId = Environment.GetEnvironmentVariable(ClientIdEnvVar);
        var tenant = Environment.GetEnvironmentVariable(TenantEnvVar);

        return new OAuthOptions
        {
            ClientId = string.IsNullOrWhiteSpace(clientId) ? PlaceholderClientId : clientId.Trim(),
            Tenant = string.IsNullOrWhiteSpace(tenant) ? "common" : tenant.Trim(),
        };
    }

    public static IReadOnlyList<string> ScopesFor(MicrosoftScopeSet set) => set switch
    {
        MicrosoftScopeSet.MailProtocols =>
        [
            "https://outlook.office.com/IMAP.AccessAsUser.All",
            "https://outlook.office.com/SMTP.Send",
        ],
        MicrosoftScopeSet.Graph =>
        [
            "https://graph.microsoft.com/Mail.ReadWrite",
            "https://graph.microsoft.com/Mail.Send",
            "https://graph.microsoft.com/User.Read",
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(set)),
    };

    /// <summary>The secret-store reference holding the MSAL token cache for an account.</summary>
    public static string CacheRefFor(string secretRef) => secretRef + ":oauth-cache";
}
