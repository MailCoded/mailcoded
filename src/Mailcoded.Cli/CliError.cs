using Mailcoded.Core.Application;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Cli;

/// <summary>
/// The exit-code classes an agent branches on. Values are a contract: never renumber one.
/// There is deliberately no code for a delete failure, because no delete verb exists.
/// </summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Internal = 1;
    public const int Validation = 2;
    public const int NotFound = 3;
    public const int Forbidden = 4;
    public const int RateLimited = 5;
    public const int ConfirmRequired = 6;
    public const int Auth = 7;
    public const int Network = 8;
    public const int Store = 9;
    public const int Unsupported = 10;
    public const int Cancelled = 130;
}

/// <summary>A bad invocation. Always maps to <see cref="ExitCodes.Validation"/>.</summary>
internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message)
    {
    }
}

/// <summary>The stderr error object: an RPC numeric code, its slug, and the exit code it produced.</summary>
internal sealed record CliError
{
    public required RpcErrorCode Code { get; init; }
    public required string Name { get; init; }
    public required int ExitCode { get; init; }
    public required string Message { get; init; }
    public long? RetryAfterMs { get; init; }
    public string? Hint { get; init; }

    public static CliError From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            OperationCanceledException => Make(
                RpcErrorCode.InternalError, "cancelled", ExitCodes.Cancelled, "The command was cancelled."),

            CliUsageException usage => Make(
                RpcErrorCode.InvalidParams, "invalid_params", ExitCodes.Validation, usage.Message),

            ConfirmRequiredException confirm => Make(
                RpcErrorCode.ConfirmRequired, "confirm_required", ExitCodes.ConfirmRequired, confirm.Message,
                hint: "Run 'mailcoded send-preview <draftId>' and pass the token it prints to --confirm-token."),

            PolicyDeniedException policy => FromPolicy(policy),

            SecretStoreException secret => Make(
                RpcErrorCode.Auth, "auth", ExitCodes.Auth, secret.Message),

            SmtpDeliveryException smtp => Make(
                CategoryCode(smtp.Category), CategoryName(smtp.Category), CategoryExit(smtp.Category), smtp.Message,
                retryAfterMs: smtp.RetryAfter is { } retry ? (long)retry.TotalMilliseconds : null),

            StoreException store => FromStore(store),

            ProviderException provider => Make(
                CategoryCode(provider.Category), CategoryName(provider.Category), CategoryExit(provider.Category),
                provider.Message),

            ArgumentException or FormatException or OverflowException => Make(
                RpcErrorCode.InvalidParams, "invalid_params", ExitCodes.Validation, exception.Message),

            FileNotFoundException or DirectoryNotFoundException => Make(
                RpcErrorCode.NotFound, "not_found", ExitCodes.NotFound, exception.Message),

            UnauthorizedAccessException => Make(
                RpcErrorCode.Forbidden, "forbidden", ExitCodes.Forbidden, exception.Message),

            IOException io => Make(
                RpcErrorCode.InternalError, "io_error", ExitCodes.Internal, io.Message),

            _ => Make(
                RpcErrorCode.InternalError, "internal_error", ExitCodes.Internal, exception.Message),
        };
    }

    private static CliError FromPolicy(PolicyDeniedException policy) => policy.Reason switch
    {
        PolicyDenialReason.RateLimited => Make(
            RpcErrorCode.RateLimited, "rate_limited", ExitCodes.RateLimited, policy.Message,
            retryAfterMs: policy.RetryAfter is { } retry ? (long)retry.TotalMilliseconds : null),

        PolicyDenialReason.SendDisabled => Make(
            RpcErrorCode.Forbidden, "forbidden", ExitCodes.Forbidden, policy.Message,
            hint: "Set MAILCODED_SEND=1 and MAILCODED_APPROVED_RECIPIENTS before sending from an agent."),

        PolicyDenialReason.RawSqlDisabled => Make(
            RpcErrorCode.Forbidden, "forbidden", ExitCodes.Forbidden, policy.Message,
            hint: "Set MAILCODED_ENABLE_SQL=1, or use the search/read/thread verbs instead."),

        _ => Make(RpcErrorCode.Forbidden, "forbidden", ExitCodes.Forbidden, policy.Message),
    };

    private static CliError FromStore(StoreException store) => store.Category switch
    {
        FailureCategory.NotFound => Make(
            RpcErrorCode.NotFound, "not_found", ExitCodes.NotFound, store.Message),
        FailureCategory.Full => Make(
            RpcErrorCode.StoreFull, "store_full", ExitCodes.Store, store.Message),
        FailureCategory.Busy => Make(
            RpcErrorCode.RateLimited, "rate_limited", ExitCodes.RateLimited, store.Message),
        FailureCategory.Auth => Make(
            RpcErrorCode.Auth, "auth", ExitCodes.Auth, store.Message),
        FailureCategory.Unsupported => Make(
            RpcErrorCode.Unsupported, "unsupported", ExitCodes.Unsupported, store.Message),
        FailureCategory.Network => Make(
            RpcErrorCode.Network, "network", ExitCodes.Network, store.Message),
        _ => Make(RpcErrorCode.StoreCorrupt, "store_corrupt", ExitCodes.Store, store.Message),
    };

    private static RpcErrorCode CategoryCode(FailureCategory category) => category switch
    {
        FailureCategory.Auth => RpcErrorCode.Auth,
        FailureCategory.NotFound => RpcErrorCode.NotFound,
        FailureCategory.Busy => RpcErrorCode.RateLimited,
        FailureCategory.Full => RpcErrorCode.StoreFull,
        FailureCategory.Unsupported or FailureCategory.Permanent => RpcErrorCode.Unsupported,
        _ => RpcErrorCode.Network,
    };

    private static string CategoryName(FailureCategory category) => category switch
    {
        FailureCategory.Auth => "auth",
        FailureCategory.NotFound => "not_found",
        FailureCategory.Busy => "rate_limited",
        FailureCategory.Full => "store_full",
        FailureCategory.Unsupported => "unsupported",
        FailureCategory.Permanent => "permanent",
        _ => "network",
    };

    private static int CategoryExit(FailureCategory category) => category switch
    {
        FailureCategory.Auth => ExitCodes.Auth,
        FailureCategory.NotFound => ExitCodes.NotFound,
        FailureCategory.Busy => ExitCodes.RateLimited,
        FailureCategory.Full => ExitCodes.Store,
        FailureCategory.Unsupported or FailureCategory.Permanent => ExitCodes.Unsupported,
        _ => ExitCodes.Network,
    };

    private static CliError Make(
        RpcErrorCode code,
        string name,
        int exitCode,
        string? message,
        long? retryAfterMs = null,
        string? hint = null) =>
        new()
        {
            Code = code,
            Name = name,
            ExitCode = exitCode,
            Message = SafeText.Line(message, 480) is { Length: > 0 } clean ? clean : "The command failed.",
            RetryAfterMs = retryAfterMs,
            Hint = hint,
        };
}
