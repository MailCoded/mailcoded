using Mailcoded.Core.Providers;

namespace Mailcoded.Core.Auth;

/// <summary>
/// Supplies a currently-valid OAuth2 access token for an account.
/// A port because an access token expires — roughly hourly on Microsoft — so a provider cannot
/// simply read one out of the secret store and keep using it (RELIABILITY edge case 7).
/// </summary>
public interface IAccessTokenSource
{
    /// <summary>
    /// Returns a token that is valid now, refreshing silently if the cached one has expired.
    /// Throws <see cref="ReauthorizationRequiredException"/> when only a human can fix it.
    /// </summary>
    Task<string> GetAccessTokenAsync(AccountConfig account, CancellationToken ct);
}

/// <summary>
/// The stored grant can no longer be refreshed — revoked, expired, or the consent was withdrawn.
/// Distinct from an auth failure because retrying cannot help; a human must sign in again.
/// </summary>
public sealed class ReauthorizationRequiredException : Exception
{
    public ReauthorizationRequiredException(string message) : base(message) { }
    public ReauthorizationRequiredException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>What a client shows the human to complete a device-code sign-in.</summary>
public sealed record DeviceCodePrompt
{
    public required string VerificationUrl { get; init; }
    public required string UserCode { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
}
