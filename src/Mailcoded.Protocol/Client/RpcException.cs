namespace Mailcoded.Protocol.Client;

public sealed class RpcException : Exception
{
    public RpcException(int code, string message, string? category, int? retryAfterMs, bool requiresUserAction)
        : base(message)
    {
        Code = code;
        Category = category;
        RetryAfterMs = retryAfterMs;
        RequiresUserAction = requiresUserAction;
    }

    public int Code { get; }
    public string? Category { get; }
    public int? RetryAfterMs { get; }
    public bool RequiresUserAction { get; }

    public bool IsTransient => Code is (int)RpcErrorCode.Network or (int)RpcErrorCode.RateLimited;

    /// <summary>No amount of retrying opens a gate, and there is no RPC that opens one.</summary>
    public bool IsGate => Code is (int)RpcErrorCode.Forbidden or (int)RpcErrorCode.ConfirmRequired;
}

/// <summary>Stream unusable: a malformed frame stops the daemon reading, so only a respawn recovers.</summary>
public sealed class DaemonDisconnectedException : Exception
{
    public DaemonDisconnectedException(string message) : base(message) { }
}
