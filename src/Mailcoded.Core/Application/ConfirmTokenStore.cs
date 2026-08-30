using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

/// <summary>What <see cref="ConfirmTokenStore.IssueAsync"/> handed out. The token is never stored.</summary>
public readonly record struct ConfirmTokenGrant(string Token, DateTimeOffset ExpiresUtc);

/// <summary>One-time send tokens, persisted as a salted hash so the one-shot CLI can redeem in a
/// second process what the first one minted. Bound to the draft, single use, constant-time compared.</summary>
public sealed class ConfirmTokenStore
{
    public const int TokenByteLength = 32;
    public const int SaltByteLength = 16;
    public const int DefaultMaxOutstanding = 64;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private readonly IClock _clock;
    private readonly long _lifetimeMs;
    private readonly int _maxOutstanding;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, long> _monotonicDeadlines = [];
    private SqliteStore? _store;

    public ConfirmTokenStore(
        IClock clock,
        TimeSpan? lifetime = null,
        int maxOutstanding = DefaultMaxOutstanding,
        SqliteStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (maxOutstanding < 1) throw new ArgumentOutOfRangeException(nameof(maxOutstanding));

        _clock = clock;
        var span = lifetime ?? DefaultLifetime;
        if (span <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        _lifetimeMs = (long)span.TotalMilliseconds;
        _maxOutstanding = maxOutstanding;
        _store = store;
    }

    public TimeSpan Lifetime => TimeSpan.FromMilliseconds(_lifetimeMs);

    public int Count => Store.CountConfirmTokens(_clock.UtcNow);

    /// <summary>Mints the single token that authorizes this outbox row.</summary>
    public async Task<ConfirmTokenGrant> IssueAsync(
        long outboxId,
        MessageId messageId,
        string draftDigest,
        CancellationToken ct)
    {
        if (outboxId <= 0) throw new ArgumentOutOfRangeException(nameof(outboxId));
        ArgumentException.ThrowIfNullOrEmpty(draftDigest);
        if (string.IsNullOrEmpty(messageId.Value))
            throw new ArgumentException("A confirm token must bind to a pre-assigned Message-ID.", nameof(messageId));

        var raw = RandomNumberGenerator.GetBytes(TokenByteLength);
        var salt = RandomNumberGenerator.GetBytes(SaltByteLength);
        var hash = Bind(salt, raw, outboxId, messageId, draftDigest);

        var issuedUtc = _clock.UtcNow;
        var expiresUtc = issuedUtc + Lifetime;
        var token = Encode(raw);
        CryptographicOperations.ZeroMemory(raw);

        await Store
            .IssueConfirmTokenAsync(outboxId, salt, hash, issuedUtc, expiresUtc, _maxOutstanding, ct)
            .ConfigureAwait(false);

        lock (_gate)
        {
            TrimDeadlines();
            _monotonicDeadlines[outboxId] = _clock.Ticks + _lifetimeMs;
        }

        return new ConfirmTokenGrant(token, expiresUtc);
    }

    /// <summary>True exactly once per issued token, and only for the draft it was bound to.</summary>
    public async Task<bool> TryConsumeAsync(
        string? token,
        long outboxId,
        MessageId messageId,
        string draftDigest,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(draftDigest)) return false;
        if (!TryDecode(token, out var candidate)) return false;

        // A restart only leaves the absolute expiry behind, but wall clock can be stepped: a token
        // minted in this process is held to the monotonic deadline as well, whichever fires first.
        if (MonotonicallyExpired(outboxId))
        {
            await RevokeAsync(outboxId, ct).ConfigureAwait(false);
            return false;
        }

        var consumed = await Store.TryConsumeConfirmTokenAsync(
            outboxId,
            _clock.UtcNow,
            (salt, stored) => CryptographicOperations.FixedTimeEquals(
                stored,
                Bind(salt, candidate, outboxId, messageId, draftDigest)),
            ct).ConfigureAwait(false);

        CryptographicOperations.ZeroMemory(candidate);
        if (consumed) Forget(outboxId);
        return consumed;
    }

    public bool IsOutstanding(long outboxId) =>
        !MonotonicallyExpired(outboxId) && Store.HasConfirmToken(outboxId, _clock.UtcNow);

    public Task RevokeAsync(long outboxId, CancellationToken ct)
    {
        Forget(outboxId);
        return Store.RevokeConfirmTokenAsync(outboxId, ct);
    }

    /// <summary>Blocks on the writer thread; the one-shot CLI teardown path has nothing to await into.</summary>
    public void Revoke(long outboxId) => RevokeAsync(outboxId, CancellationToken.None).GetAwaiter().GetResult();

    public Task PurgeAsync(CancellationToken ct) => Store.PurgeConfirmTokensAsync(_clock.UtcNow, ct);

    public void Purge() => PurgeAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Called by <see cref="SendService"/> so a hand-wired host cannot forget the backing store.</summary>
    internal void AttachStore(SqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_gate) _store ??= store;
    }

    private SqliteStore Store
    {
        get
        {
            lock (_gate)
            {
                return _store ?? throw new InvalidOperationException(
                    "This ConfirmTokenStore has no store attached, so a token could not survive the process "
                    + "that minted it. Pass the SqliteStore to the constructor or to a SendService.");
            }
        }
    }

    private bool MonotonicallyExpired(long outboxId)
    {
        lock (_gate)
        {
            if (!_monotonicDeadlines.TryGetValue(outboxId, out var deadline)) return false;
            return _clock.Ticks - deadline >= 0;
        }
    }

    private void Forget(long outboxId)
    {
        lock (_gate) _monotonicDeadlines.Remove(outboxId);
    }

    private void TrimDeadlines()
    {
        var now = _clock.Ticks;
        List<long>? drop = null;

        foreach (var pair in _monotonicDeadlines)
        {
            if (now - pair.Value < 0) continue;
            drop ??= [];
            drop.Add(pair.Key);
        }

        if (drop is not null)
            foreach (var id in drop) _monotonicDeadlines.Remove(id);

        while (_monotonicDeadlines.Count >= _maxOutstanding)
        {
            var oldestId = 0L;
            var oldestDeadline = long.MaxValue;

            foreach (var pair in _monotonicDeadlines)
            {
                if (pair.Value >= oldestDeadline) continue;
                oldestDeadline = pair.Value;
                oldestId = pair.Key;
            }

            if (!_monotonicDeadlines.Remove(oldestId)) return;
        }
    }

    private static byte[] Bind(byte[] salt, byte[] token, long outboxId, MessageId messageId, string draftDigest)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(salt);
        sha.AppendData(token);
        sha.AppendData(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{outboxId}\n{messageId.Value}\n{draftDigest}")));
        return sha.GetHashAndReset();
    }

    private static string Encode(byte[] raw) =>
        Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string token, out byte[] bytes)
    {
        bytes = [];
        if (token.Length is < 4 or > 128) return false;

        var normalized = token.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding == 1) return false;
        if (padding != 0) normalized += new string('=', 4 - padding);

        var buffer = new byte[((normalized.Length / 4) * 3) + 3];
        if (!Convert.TryFromBase64String(normalized, buffer, out var written)) return false;

        bytes = buffer[..written];
        return true;
    }
}
