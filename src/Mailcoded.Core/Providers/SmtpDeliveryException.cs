namespace Mailcoded.Core.Providers;

/// <summary>
/// An SMTP submission the server refused. The category carries permanence (a 5xx is
/// <see cref="FailureCategory.Permanent"/>); these members carry the rest of edge case 28.
/// </summary>
public sealed class SmtpDeliveryException : ProviderException
{
    private SmtpDeliveryException(FailureCategory category, string message)
        : base(category, message)
    {
    }

    private SmtpDeliveryException(FailureCategory category, string message, Exception inner)
        : base(category, message, inner)
    {
    }

    /// <summary>The three-digit SMTP reply code, or 0 when the failure was not a reply.</summary>
    public int StatusCode { get; private set; }

    /// <summary>RFC 3463 status such as <c>4.7.1</c>, when the server sent one.</summary>
    public string? EnhancedStatusCode { get; private set; }

    /// <summary>True for 421: the server is closing the channel and the client must reconnect.</summary>
    public bool RequiresReconnect { get; private set; }

    /// <summary>Hint for the first retry of a greylisted (4xx) submission.</summary>
    public TimeSpan? RetryAfter { get; private set; }

    public static SmtpDeliveryException Create(
        int statusCode,
        string message,
        string? enhancedStatusCode = null,
        Exception? inner = null)
    {
        var category = ProviderErrors.CategorizeSmtpStatus(statusCode);

        var result = inner is null
            ? new SmtpDeliveryException(category, message)
            : new SmtpDeliveryException(category, message, inner);

        result.StatusCode = statusCode;
        result.EnhancedStatusCode = enhancedStatusCode;
        result.RequiresReconnect = statusCode == 421;

        // 451/4xx greylisting: the outbox escalates 1, 5, 15, 30 minutes from this first hint.
        result.RetryAfter = statusCode is >= 400 and < 500 ? TimeSpan.FromMinutes(1) : null;

        return result;
    }
}
