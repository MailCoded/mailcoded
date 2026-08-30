using System.Collections.Concurrent;

namespace Mailcoded.Core.Store;

/// <summary>
/// A small pool of read-only connections. WAL lets these run concurrently with the writer, so
/// queries never block a sync batch — provided every read transaction stays short (§14.3).
/// </summary>
internal sealed class ReaderPool : IDisposable
{
    private readonly ConcurrentBag<DbSession> _idle = new();
    private readonly List<DbSession> _all = new();
    private readonly SemaphoreSlim _slots;
    private readonly Func<DbSession> _factory;
    private readonly object _gate = new();
    private volatile bool _disposed;

    public ReaderPool(int size, Func<DbSession> factory)
    {
        if (size < 1) size = 1;
        _slots = new SemaphoreSlim(size, size);
        _factory = factory;
    }

    public Lease Rent(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _slots.Wait(ct);

        DbSession session;
        try
        {
            if (!_idle.TryTake(out var pooled) || pooled is null)
            {
                session = _factory();
                lock (_gate) _all.Add(session);
            }
            else
            {
                session = pooled;
            }
        }
        catch
        {
            _slots.Release();
            throw;
        }

        return new Lease(this, session);
    }

    private void Return(DbSession session)
    {
        if (_disposed) return;
        _idle.Add(session);
        _slots.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            foreach (var session in _all) session.Dispose();
            _all.Clear();
        }

        _idle.Clear();
        _slots.Dispose();
    }

    internal readonly struct Lease : IDisposable
    {
        private readonly ReaderPool _pool;

        internal Lease(ReaderPool pool, DbSession session)
        {
            _pool = pool;
            Session = session;
        }

        public DbSession Session { get; }

        public void Dispose() => _pool.Return(Session);
    }
}
