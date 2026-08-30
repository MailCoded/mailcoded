using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Daemon;

/// <summary>
/// The one exception-to-error mapper at the process boundary (ARCHITECTURE §12.5). Codes are the
/// stable table in SPEC §5.6. No message it produces may carry a credential or mail content.
/// </summary>
internal static class RpcErrorMapper
{
    private const int MaxDetailLength = 240;

    private const string GenericInternal =
        "The daemon failed to complete the request. See the daemon's stderr log for the stack trace.";

    /// <summary>The category slug clients branch on, mirroring <see cref="FailureCategory"/>.</summary>
    public static string CategoryOf(FailureCategory category) => category switch
    {
        FailureCategory.Network => "network",
        FailureCategory.Protocol => "protocol",
        FailureCategory.Auth => "auth",
        FailureCategory.Busy => "busy",
        FailureCategory.Full => "full",
        FailureCategory.NotFound => "notFound",
        FailureCategory.Unsupported => "unsupported",
        _ => "permanent",
    };

    public static JsonRpcError Map(Exception exception, StderrLog log)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(log);

        switch (exception)
        {
            case MethodNotFoundException notFound:
                return Build(RpcErrorCode.MethodNotFound, Safe(notFound.Message), "unsupported");

            case ConfirmRequiredException confirm:
                return Build(RpcErrorCode.ConfirmRequired, Safe(confirm.Message), "protocol", requiresUserAction: true);

            case PolicyDeniedException policy when policy.Reason == PolicyDenialReason.RateLimited:
                return Build(
                    RpcErrorCode.RateLimited,
                    Safe(policy.Message),
                    "busy",
                    retryAfterMs: policy.RetryAfter is { } wait ? (int)Math.Clamp(wait.TotalMilliseconds, 0, int.MaxValue) : null);

            case PolicyDeniedException policy:
                return Build(RpcErrorCode.Forbidden, Safe(policy.Message), "unsupported", requiresUserAction: true);

            case ProviderException provider:
                return Build(
                    ProviderCode(provider.Category),
                    Safe(provider.Message),
                    CategoryOf(provider.Category),
                    requiresUserAction: provider.Category == FailureCategory.Auth);

            case StoreException store:
                return Build(
                    StoreCode(store.Category),
                    Safe(store.Message),
                    CategoryOf(store.Category),
                    requiresUserAction: store.Category is FailureCategory.Full or FailureCategory.Protocol);

            // The value never reaches here: SecretStoreException is documented never to echo it.
            case SecretStoreException secret:
                return Build(RpcErrorCode.Auth, Safe(secret.Message), "auth", requiresUserAction: true);

            case JsonException json:
                return Build(RpcErrorCode.InvalidParams, Safe(json.Message), "protocol");

            case ArgumentException or FormatException or OverflowException or NotSupportedException:
                return Build(RpcErrorCode.InvalidParams, Safe(exception.Message), "protocol");

            case OperationCanceledException:
                return Build(RpcErrorCode.InternalError, "The request was cancelled.", "busy");

            default:
                log.Exception("Unhandled exception while serving a request.", exception);
                return Build(RpcErrorCode.InternalError, GenericInternal, "protocol");
        }
    }

    /// <summary>Classifies a background failure for <c>notify.sync.error</c>, which carries the same codes.</summary>
    public static SyncErrorInfo Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            ProviderException provider => new SyncErrorInfo(
                (int)ProviderCode(provider.Category),
                CategoryOf(provider.Category),
                provider.Category == FailureCategory.Auth,
                Safe(provider.Message)),

            StoreException store => new SyncErrorInfo(
                (int)StoreCode(store.Category),
                CategoryOf(store.Category),
                store.Category is FailureCategory.Full or FailureCategory.Protocol,
                Safe(store.Message)),

            SecretStoreException secret => new SyncErrorInfo(
                (int)RpcErrorCode.Auth, "auth", true, Safe(secret.Message)),

            _ => new SyncErrorInfo((int)RpcErrorCode.InternalError, "protocol", false, GenericInternal),
        };
    }

    /// <summary>Provider failures are remote: Protocol and Permanent are network-shaped, not store-shaped.</summary>
    private static RpcErrorCode ProviderCode(FailureCategory category) => category switch
    {
        FailureCategory.Auth => RpcErrorCode.Auth,
        FailureCategory.Network or FailureCategory.Protocol => RpcErrorCode.Network,
        FailureCategory.NotFound => RpcErrorCode.NotFound,
        FailureCategory.Busy => RpcErrorCode.RateLimited,
        FailureCategory.Full => RpcErrorCode.StoreFull,
        FailureCategory.Unsupported or FailureCategory.Permanent => RpcErrorCode.Unsupported,
        _ => RpcErrorCode.Network,
    };

    /// <summary>A store that answers Protocol has produced something the schema forbids: treat it as corrupt.</summary>
    private static RpcErrorCode StoreCode(FailureCategory category) => category switch
    {
        FailureCategory.NotFound => RpcErrorCode.NotFound,
        FailureCategory.Full => RpcErrorCode.StoreFull,
        FailureCategory.Busy => RpcErrorCode.RateLimited,
        FailureCategory.Auth => RpcErrorCode.Auth,
        FailureCategory.Network => RpcErrorCode.Network,
        FailureCategory.Unsupported => RpcErrorCode.Unsupported,
        _ => RpcErrorCode.StoreCorrupt,
    };

    public static JsonRpcError Build(
        RpcErrorCode code,
        string message,
        string? category = null,
        int? retryAfterMs = null,
        bool? requiresUserAction = null,
        long? accountId = null,
        long? folderId = null) =>
        new()
        {
            Code = (int)code,
            Message = message,
            Data = category is null && retryAfterMs is null && requiresUserAction is null
                   && accountId is null && folderId is null
                ? null
                : new RpcErrorData
                {
                    Category = category,
                    RetryAfterMs = retryAfterMs,
                    RequiresUserAction = requiresUserAction,
                    AccountId = accountId,
                    FolderId = folderId,
                },
        };

    /// <summary>Strips control characters and caps length so nothing structured leaks into a message.</summary>
    private static string Safe(string? message)
    {
        var clean = AuditText.Sanitize(message, MaxDetailLength);
        return string.IsNullOrEmpty(clean) ? GenericInternal : clean;
    }
}

internal readonly record struct SyncErrorInfo(int Code, string Category, bool RequiresUserAction, string Message);
