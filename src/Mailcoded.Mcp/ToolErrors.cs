using Mailcoded.Core.Application;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Protocol;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Mcp;

/// <summary>Stable error slugs mirroring the RPC codes in SPEC §5.6, so agents can branch.</summary>
internal static class ToolErrorCodes
{
    public const string InvalidParams = "invalid_params";
    public const string NotFound = "not_found";
    public const string ConfirmRequired = "confirm_required";
    public const string Forbidden = "forbidden";
    public const string RateLimited = "rate_limited";
    public const string Auth = "auth";
    public const string Network = "network";
    public const string Unsupported = "unsupported";
    public const string StoreFull = "store_full";
    public const string StoreError = "store_error";
    public const string SendFailed = "send_failed";
    public const string InternalError = "internal_error";
}

internal static class ToolErrors
{
    public static ToolErrorDto Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            ToolFailureException failure => Error(failure.Code, failure.Message),
            ConfirmRequiredException => Error(ToolErrorCodes.ConfirmRequired, exception.Message),
            PolicyDeniedException denied => FromPolicy(denied),
            SmtpDeliveryException smtp => Error(ToolErrorCodes.SendFailed, smtp.Message),
            StoreException store => Error(CodeFor(store.Category, ToolErrorCodes.StoreError), store.Message),
            ProviderException provider => Error(CodeFor(provider.Category, ToolErrorCodes.Network), provider.Message),
            SecretStoreException => Error(ToolErrorCodes.Auth, "The stored credential could not be read."),
            ArgumentException => Error(ToolErrorCodes.InvalidParams, exception.Message),
            _ => Error(ToolErrorCodes.InternalError, "The tool call failed inside the mailcoded adapter."),
        };
    }

    private static ToolErrorDto FromPolicy(PolicyDeniedException denied) => new()
    {
        SchemaVersion = ProtocolConstants.Version,
        Ok = false,
        ErrorCode = denied.Reason == PolicyDenialReason.RateLimited ? ToolErrorCodes.RateLimited : ToolErrorCodes.Forbidden,
        Message = denied.Message,
        Reason = ToSlug(denied.Reason),
        RetryAfterMs = denied.RetryAfter is { } retry ? (long)retry.TotalMilliseconds : null,
    };

    private static string CodeFor(FailureCategory category, string fallback) => category switch
    {
        FailureCategory.NotFound => ToolErrorCodes.NotFound,
        FailureCategory.Auth => ToolErrorCodes.Auth,
        FailureCategory.Network or FailureCategory.Busy => ToolErrorCodes.Network,
        FailureCategory.Unsupported => ToolErrorCodes.Unsupported,
        FailureCategory.Full => ToolErrorCodes.StoreFull,
        _ => fallback,
    };

    public static string ToSlug(PolicyDenialReason reason) => reason switch
    {
        PolicyDenialReason.SendDisabled => "send_disabled",
        PolicyDenialReason.RecipientNotApproved => "recipient_not_approved",
        PolicyDenialReason.RateLimited => "rate_limited",
        PolicyDenialReason.RawSqlDisabled => "raw_sql_disabled",
        PolicyDenialReason.HtmlBodyDenied => "html_body_denied",
        PolicyDenialReason.OperationNotAvailable => "operation_not_available",
        _ => "none",
    };

    private static ToolErrorDto Error(string code, string message) => new()
    {
        SchemaVersion = ProtocolConstants.Version,
        Ok = false,
        ErrorCode = code,
        Message = message,
    };
}
