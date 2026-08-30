using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Providers;

/// <summary>What the caller should do after one failure.</summary>
public readonly record struct RetryDecision(bool ShouldRetry, TimeSpan Delay, bool RequiresUserAction, string? Reason)
{
    public const string AuthRequired = "auth-required";
}

/// <summary>
/// Exponential backoff with full jitter for reconnects (RELIABILITY §14.4): 1s, 2s, 4s, 8s …
/// capped at 5 minutes and reset once the connection has been stable for 10 minutes.
/// Auth failures are counted separately and stop retrying instead of looping into a lockout.
/// </summary>
/// <remarks>
/// Intervals are measured with <see cref="IClock.Ticks"/> only, and the jitter source is injected,
/// so the whole policy is deterministic under test.
/// </remarks>
public sealed class ReconnectPolicy
{
    public const int DefaultInitialDelayMs = 1_000;
    public const int DefaultMaxDelayMs = 300_000;
    public const int DefaultStableResetMs = 600_000;
    public const int DefaultAuthRetryDelayMs = 30_000;

    /// <summary>A zero jitter draw would otherwise turn the backoff into a tight reconnect loop.</summary>
    private const int MinDelayMs = 100;

    private readonly IClock clock;
    private readonly Random random;
    private readonly int initialDelayMs;
    private readonly int maxDelayMs;
    private readonly int stableResetMs;
    private readonly int authRetryBudget;
    private readonly int authRetryDelayMs;

    private int networkFailures;
    private int authFailures;
    private long lastFailureTicks;
    private bool hasFailed;

    public ReconnectPolicy(
        IClock clock,
        Random random,
        int initialDelayMs = DefaultInitialDelayMs,
        int maxDelayMs = DefaultMaxDelayMs,
        int stableResetMs = DefaultStableResetMs,
        int authRetryBudget = 1,
        int authRetryDelayMs = DefaultAuthRetryDelayMs)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialDelayMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelayMs, initialDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(stableResetMs);
        ArgumentOutOfRangeException.ThrowIfNegative(authRetryBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(authRetryDelayMs);

        this.clock = clock;
        this.random = random;
        this.initialDelayMs = initialDelayMs;
        this.maxDelayMs = maxDelayMs;
        this.stableResetMs = stableResetMs;
        this.authRetryBudget = authRetryBudget;
        this.authRetryDelayMs = authRetryDelayMs;
    }

    public ReconnectPolicy(IClock clock, int seed) : this(clock, new Random(seed)) { }

    public int ConsecutiveNetworkFailures => networkFailures;

    public int ConsecutiveAuthFailures => authFailures;

    public bool RequiresUserAction => authFailures > authRetryBudget;

    /// <summary>The un-jittered ceiling for the given 1-based attempt number.</summary>
    public int CeilingMs(int attempt)
    {
        if (attempt <= 1) return initialDelayMs;
        var shift = Math.Min(attempt - 1, 30);
        var ceiling = (long)initialDelayMs << shift;
        return ceiling >= maxDelayMs ? maxDelayMs : (int)ceiling;
    }

    /// <summary>Records that the connection is healthy; counters only clear once it has been stable.</summary>
    public void RecordSuccess()
    {
        if (!hasFailed) return;
        if (clock.Ticks - lastFailureTicks < stableResetMs) return;

        networkFailures = 0;
        authFailures = 0;
        hasFailed = false;
    }

    public RetryDecision OnFailure(FailureCategory category)
    {
        var now = clock.Ticks;
        if (hasFailed && now - lastFailureTicks >= stableResetMs)
        {
            networkFailures = 0;
            authFailures = 0;
        }

        hasFailed = true;
        lastFailureTicks = now;

        if (category == FailureCategory.Auth)
        {
            authFailures++;
            return authFailures > authRetryBudget
                ? new RetryDecision(false, TimeSpan.Zero, true, RetryDecision.AuthRequired)
                : new RetryDecision(true, TimeSpan.FromMilliseconds(authRetryDelayMs), false, null);
        }

        networkFailures++;
        var ceiling = CeilingMs(networkFailures);
        var jittered = (int)(random.NextDouble() * ceiling);
        return new RetryDecision(true, TimeSpan.FromMilliseconds(Math.Max(MinDelayMs, jittered)), false, null);
    }
}
