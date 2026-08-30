namespace Mailcoded.Core.Domain.Outbox;

/// <summary>
/// The outbox state machine. Legal transitions only:
/// Queued → Sending → Sent | Failed, and Failed → Queued on an explicit retry.
/// </summary>
public enum OutboxState
{
    Queued,
    Sending,
    Sent,
    Failed,
}

public static class OutboxStateExtensions
{
    public static string ToWireValue(this OutboxState state) => state switch
    {
        OutboxState.Queued => "queued",
        OutboxState.Sending => "sending",
        OutboxState.Sent => "sent",
        OutboxState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static OutboxState FromWireValue(string value) => value switch
    {
        "queued" => OutboxState.Queued,
        "sending" => OutboxState.Sending,
        "sent" => OutboxState.Sent,
        "failed" => OutboxState.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown outbox state '{value}'."),
    };
}
