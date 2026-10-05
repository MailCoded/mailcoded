using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Protocol;

namespace Mailcoded.Daemon;

/// <summary>One per daemon, so the per-Account throttle spans watch passes and requests alike.</summary>
internal sealed class SyncProgressPublisher
{
    private readonly RpcChannel channel;
    private readonly IClock clock;
    private readonly SyncProgressThrottle throttle;

    public SyncProgressPublisher(RpcChannel channel, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(clock);

        this.channel = channel;
        this.clock = clock;
        throttle = new SyncProgressThrottle(clock);
    }

    public SyncProgressRun Begin() => throttle.Begin(Publish);

    private void Publish(SyncProgress progress) =>
        channel.TryEnqueue(RpcPayloads.Notification(
            RpcNotifications.SyncProgress,
            WireMapper.ToNotification(progress, clock.UtcNow),
            ProtocolJsonContext.Default.SyncProgressNotification));
}
