namespace Mailcoded.Core.Domain.Primitives;

/// <summary>
/// The single ambient-time port. Domain code never reads the clock directly; a caller injects
/// the value it needs as a parameter (SPEC invariant 13).
/// </summary>
/// <remarks>
/// <see cref="UtcNow"/> is wall clock and is <em>display and record-keeping only</em>.
/// Every interval, timeout, and backoff computation must use <see cref="Ticks"/>, which is
/// monotonic and therefore immune to NTP steps, DST, and suspend/resume (SPEC invariant 16).
/// </remarks>
public interface IClock
{
    /// <summary>Wall-clock UTC. Display and persistence only — never for measuring an interval.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic milliseconds since an arbitrary origin. The only legal basis for intervals.</summary>
    long Ticks { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long Ticks => Environment.TickCount64;
}
