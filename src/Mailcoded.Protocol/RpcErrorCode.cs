namespace Mailcoded.Protocol;

/// <summary>
/// Stable numeric JSON-RPC error codes (SPEC §5.6). Values are a wire contract: never renumber
/// one, never reuse a retired number. Document every addition in <c>docs/rpc.md</c>.
/// </summary>
public enum RpcErrorCode
{
    /// <summary>Malformed JSON was received. Standard JSON-RPC 2.0 code.</summary>
    ParseError = -32700,

    /// <summary>The payload was valid JSON but not a valid JSON-RPC request object.</summary>
    InvalidRequest = -32600,

    /// <summary>No method with that name exists on this protocol version.</summary>
    MethodNotFound = -32601,

    /// <summary>Params were missing, of the wrong type, or failed validation.</summary>
    InvalidParams = -32602,

    /// <summary>An unhandled failure inside the daemon. The message never carries mail content.</summary>
    InternalError = -32603,

    /// <summary>Authentication to the mail server failed or the stored credential is gone; the user must act.</summary>
    Auth = 1000,

    /// <summary>A transient network or TLS failure. Safe to retry after backoff.</summary>
    Network = 1001,

    /// <summary>The requested account, folder, message, attachment, thread, or draft does not exist.</summary>
    NotFound = 1002,

    /// <summary>A send was attempted without a valid one-time confirm token, or the token expired.</summary>
    ConfirmRequired = 1003,

    /// <summary>The local store failed an integrity check; re-sync from the server is the recovery path.</summary>
    StoreCorrupt = 1004,

    /// <summary>A rate limit or busy-writer backpressure rejected the call. Retry after the hinted delay.</summary>
    RateLimited = 1005,

    /// <summary>A Core safety gate refused the call: send disabled, recipient not allowlisted, raw SQL off.</summary>
    Forbidden = 1006,

    /// <summary>The disk or the database is full (SQLITE_FULL); sync is paused rather than risking corruption.</summary>
    StoreFull = 1007,

    /// <summary>The server or provider lacks a capability the call requires, e.g. SMTPUTF8 for an EAI recipient.</summary>
    Unsupported = 1008,
}
