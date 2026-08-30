namespace Mailcoded.Core.Application;

/// <summary>Why a gate refused. Every value maps to a stable RPC code at the daemon boundary.</summary>
public enum PolicyDenialReason
{
    None,

    /// <summary>MAILCODED_SEND is not set, so the agent surface may not send.</summary>
    SendDisabled,

    /// <summary>A recipient did not match MAILCODED_APPROVED_RECIPIENTS.</summary>
    RecipientNotApproved,

    /// <summary>The agent send budget for the sliding hour is spent.</summary>
    RateLimited,

    /// <summary>MAILCODED_ENABLE_SQL is not set.</summary>
    RawSqlDisabled,

    /// <summary>The statement is not a single read-only query.</summary>
    RawSqlNotReadOnly,

    /// <summary>Agents receive plaintext bodies only.</summary>
    HtmlBodyDenied,

    /// <summary>The operation is not part of this caller's surface.</summary>
    OperationNotAvailable,
}

/// <summary>Send was attempted without a valid one-time confirm token. Maps to RPC code 1003.</summary>
public sealed class ConfirmRequiredException : Exception
{
    public ConfirmRequiredException(string message) : base(message)
    {
    }
}

/// <summary>A safety gate refused the call. Maps to RPC code 1005.</summary>
public sealed class PolicyDeniedException : Exception
{
    public PolicyDeniedException(PolicyDenialReason reason, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        Reason = reason;
        RetryAfter = retryAfter;
    }

    public PolicyDenialReason Reason { get; }

    /// <summary>How long until the same call could succeed, when the refusal was a rate limit.</summary>
    public TimeSpan? RetryAfter { get; }
}
