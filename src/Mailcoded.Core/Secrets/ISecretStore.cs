namespace Mailcoded.Core.Secrets;

/// <summary>
/// The only place a credential may live. Values never reach the database, config JSON, logs,
/// RPC responses, or exception messages (SPEC invariant 3).
/// </summary>
public interface ISecretStore
{
    /// <summary>Human-readable name of the backing store, for diagnostics. Never contains a secret.</summary>
    string BackendName { get; }

    /// <summary>True when this store can operate on the current machine and session.</summary>
    bool IsAvailable { get; }

    Task SetAsync(string secretRef, string value, CancellationToken ct);

    /// <summary>Returns null when the reference is unknown. Never throws with the value in the message.</summary>
    Task<string?> GetAsync(string secretRef, CancellationToken ct);

    Task DeleteAsync(string secretRef, CancellationToken ct);
}

/// <summary>Thrown when a secret operation fails. The message must never echo the secret value.</summary>
public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string message) : base(message) { }
    public SecretStoreException(string message, Exception inner) : base(message, inner) { }
}
