namespace Mailcoded.Core.Domain.Outbox;

public enum SendReconciliation
{
    Resend,
    MarkSent,
    Investigate,
}

/// <summary>Everything known about a row stuck in Sending after a crash.</summary>
public sealed record OutboxReconcileInput
{
    public required OutboxState State { get; init; }

    /// <summary>False when the bytes were never handed to SMTP, which makes a resend provably safe.</summary>
    public bool DispatchStarted { get; init; } = true;

    /// <summary>True when a 2xx reply was durably recorded before the crash.</summary>
    public bool SmtpAccepted { get; init; }

    /// <summary>False when Sent could not be searched at all — offline, missing folder, read error.</summary>
    public bool SentFolderSearched { get; init; }

    /// <summary>True when a message with this Message-ID exists in Sent.</summary>
    public bool FoundInSent { get; init; }

    /// <summary>True only for servers that copy submissions into Sent themselves, making absence conclusive.</summary>
    public bool ServerAutoSavesToSent { get; init; }
}

/// <summary>The never-double-send rule as a pure decision table.</summary>
public static class OutboxReconciler
{
    public static SendReconciliation Decide(OutboxReconcileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.State != OutboxState.Sending)
            throw new ArgumentException(
                $"Reconciliation applies only to a message stuck in '{OutboxState.Sending.ToWireValue()}'.", nameof(input));

        if (input.FoundInSent) return SendReconciliation.MarkSent;
        if (input.SmtpAccepted) return SendReconciliation.MarkSent;
        if (!input.DispatchStarted) return SendReconciliation.Resend;
        if (!input.SentFolderSearched) return SendReconciliation.Investigate;

        // Absence from Sent only disproves delivery on a server that saves the copy itself.
        return input.ServerAutoSavesToSent ? SendReconciliation.Resend : SendReconciliation.Investigate;
    }

    public static SendReconciliation Decide(bool sentFolderSearched, bool foundInSent) =>
        Decide(new OutboxReconcileInput
        {
            State = OutboxState.Sending,
            SentFolderSearched = sentFolderSearched,
            FoundInSent = foundInSent,
        });
}
