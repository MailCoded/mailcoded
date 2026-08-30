using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Application;

public enum ConnectionRole
{
    Imap,
    ImapWatch,
    Smtp,
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,

    /// <summary>A hard authentication failure: surfaced to the user, never retried in a tight loop.</summary>
    AuthRequired,

    Error,
}

public sealed record ConnectionStatus
{
    public required AccountId AccountId { get; init; }
    public required ConnectionRole Role { get; init; }
    public required ConnectionState State { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    public bool IsOpen => State == ConnectionState.Connected;
}

/// <summary>What the composition root reports about live connections so health can answer without I/O.</summary>
public sealed class ConnectionRegistry
{
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<(long Account, ConnectionRole Role), ConnectionStatus> _statuses = [];

    public ConnectionRegistry(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public void Observe(AccountId accountId, ConnectionRole role, ConnectionState state, string? detail = null)
    {
        var status = new ConnectionStatus
        {
            AccountId = accountId,
            Role = role,
            State = state,
            Detail = AuditText.Sanitize(detail, 128),
            UpdatedUtc = _clock.UtcNow,
        };

        lock (_gate) _statuses[(accountId.Value, role)] = status;
    }

    public void Forget(AccountId accountId)
    {
        lock (_gate)
        {
            foreach (var role in AllRoles) _statuses.Remove((accountId.Value, role));
        }
    }

    private static readonly ConnectionRole[] AllRoles =
        [ConnectionRole.Imap, ConnectionRole.ImapWatch, ConnectionRole.Smtp];

    public ConnectionStatus? Get(AccountId accountId, ConnectionRole role)
    {
        lock (_gate) return _statuses.TryGetValue((accountId.Value, role), out var status) ? status : null;
    }

    public IReadOnlyList<ConnectionStatus> Snapshot()
    {
        lock (_gate) return new List<ConnectionStatus>(_statuses.Values);
    }

    public int OpenConnections
    {
        get
        {
            lock (_gate)
            {
                var open = 0;
                foreach (var status in _statuses.Values)
                    if (status.IsOpen) open++;
                return open;
            }
        }
    }
}
