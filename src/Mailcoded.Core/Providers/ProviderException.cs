namespace Mailcoded.Core.Providers;

/// <summary>
/// Category of an adapter failure. Drives the reconnect and backoff rules in RELIABILITY §14.4 —
/// in particular, <see cref="FailureCategory.Auth"/> must never be retried in a tight loop.
/// </summary>
public enum FailureCategory
{
    Network,
    Protocol,
    Auth,
    Busy,
    Full,
    NotFound,
    Unsupported,

    /// <summary>Terminal by the protocol's own rules — an SMTP 5xx, a rejected recipient. Never retried.</summary>
    Permanent,
}

/// <summary>Wraps a failure from a remote mail service. Never carries a credential in its message.</summary>
public class ProviderException : Exception
{
    public FailureCategory Category { get; }

    public ProviderException(FailureCategory category, string message) : base(message) => Category = category;

    public ProviderException(FailureCategory category, string message, Exception inner) : base(message, inner) => Category = category;

    public bool IsTransient => Category is FailureCategory.Network or FailureCategory.Busy or FailureCategory.Protocol;

    public bool IsPermanent => Category is FailureCategory.Permanent or FailureCategory.Unsupported;
}

/// <summary>Wraps a failure from the local store.</summary>
public class StoreException : Exception
{
    public FailureCategory Category { get; }

    public StoreException(FailureCategory category, string message) : base(message) => Category = category;

    public StoreException(FailureCategory category, string message, Exception inner) : base(message, inner) => Category = category;
}
