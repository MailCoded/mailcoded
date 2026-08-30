using System.Security.Cryptography;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Application;

/// <summary>One-time send tokens: bound to one draft, monotonic expiry, constant-time compare.</summary>
public sealed class ConfirmTokenStore
{
    public const int TokenByteLength = 32;
    public const int DefaultMaxOutstanding = 64;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public required byte[] Token { get; init; }
        public required string MessageId { get; init; }
        public required string Digest { get; init; }
        public required long ExpiresAtTicks { get; init; }
        public required long IssuedAtTicks { get; init; }
    }

    private readonly IClock _clock;
    private readonly long _lifetimeMs;
    private readonly int _maxOutstanding;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];

    public ConfirmTokenStore(IClock clock, TimeSpan? lifetime = null, int maxOutstanding = DefaultMaxOutstanding)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (maxOutstanding < 1) throw new ArgumentOutOfRangeException(nameof(maxOutstanding));

        _clock = clock;
        var span = lifetime ?? DefaultLifetime;
        if (span <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        _lifetimeMs = (long)span.TotalMilliseconds;
        _maxOutstanding = maxOutstanding;
    }

    public TimeSpan Lifetime => TimeSpan.FromMilliseconds(_lifetimeMs);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                PurgeCore();
                return _entries.Count;
            }
        }
    }

    /// <summary>Mints the single token that authorizes this outbox row.</summary>
    public string Issue(long outboxId, MessageId messageId, string draftDigest)
    {
        if (outboxId <= 0) throw new ArgumentOutOfRangeException(nameof(outboxId));
        ArgumentException.ThrowIfNullOrEmpty(draftDigest);
        if (string.IsNullOrEmpty(messageId.Value))
            throw new ArgumentException("A confirm token must bind to a pre-assigned Message-ID.", nameof(messageId));

        var raw = RandomNumberGenerator.GetBytes(TokenByteLength);
        var now = _clock.Ticks;

        lock (_gate)
        {
            PurgeCore();
            EvictOldest();

            _entries[outboxId] = new Entry
            {
                Token = raw,
                MessageId = messageId.Value,
                Digest = draftDigest,
                ExpiresAtTicks = now + _lifetimeMs,
                IssuedAtTicks = now,
            };
        }

        return Encode(raw);
    }

    /// <summary>True exactly once per issued token, and only for the draft it was bound to.</summary>
    public bool TryConsume(string? token, long outboxId, MessageId messageId, string draftDigest)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!TryDecode(token, out var candidate)) return false;

        lock (_gate)
        {
            PurgeCore();
            if (!_entries.TryGetValue(outboxId, out var entry)) return false;

            var tokenMatches = CryptographicOperations.FixedTimeEquals(entry.Token, candidate);
            if (!tokenMatches) return false;
            if (!string.Equals(entry.MessageId, messageId.Value, StringComparison.Ordinal)) return false;
            if (!string.Equals(entry.Digest, draftDigest, StringComparison.Ordinal)) return false;

            _entries.Remove(outboxId);
            CryptographicOperations.ZeroMemory(entry.Token);
            return true;
        }
    }

    public bool IsOutstanding(long outboxId)
    {
        lock (_gate)
        {
            PurgeCore();
            return _entries.ContainsKey(outboxId);
        }
    }

    public void Revoke(long outboxId)
    {
        lock (_gate)
        {
            if (_entries.Remove(outboxId, out var entry)) CryptographicOperations.ZeroMemory(entry.Token);
        }
    }

    public void Purge()
    {
        lock (_gate) PurgeCore();
    }

    private void PurgeCore()
    {
        var now = _clock.Ticks;
        List<long>? expired = null;

        foreach (var pair in _entries)
        {
            if (now - pair.Value.ExpiresAtTicks < 0) continue;
            expired ??= [];
            expired.Add(pair.Key);
        }

        if (expired is null) return;
        foreach (var id in expired)
        {
            if (_entries.Remove(id, out var entry)) CryptographicOperations.ZeroMemory(entry.Token);
        }
    }

    private void EvictOldest()
    {
        while (_entries.Count >= _maxOutstanding)
        {
            var oldestId = 0L;
            var oldestTicks = long.MaxValue;

            foreach (var pair in _entries)
            {
                if (pair.Value.IssuedAtTicks >= oldestTicks) continue;
                oldestTicks = pair.Value.IssuedAtTicks;
                oldestId = pair.Key;
            }

            if (oldestId == 0) return;
            if (_entries.Remove(oldestId, out var entry)) CryptographicOperations.ZeroMemory(entry.Token);
        }
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
