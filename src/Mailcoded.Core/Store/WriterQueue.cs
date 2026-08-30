using System.Collections.Concurrent;
using Mailcoded.Core.Providers;

namespace Mailcoded.Core.Store;

/// <summary>
/// The single-writer model from RELIABILITY §14.3: every mutation runs on one dedicated thread
/// against one long-lived connection, so SQLITE_BUSY between our own writers is structurally
/// impossible and prepared statements stay hot.
/// </summary>
internal sealed class WriterQueue : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentQueue<WorkItem>());
    private readonly DbSession _session;
    private readonly Thread _thread;
    private bool _disposed;

    public WriterQueue(DbSession session, string threadName)
    {
        _session = session;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName,
        };
        _thread.Start();
    }

    /// <summary>True when the caller is already on the writer thread, so re-entry must run inline.</summary>
    public bool IsWriterThread => Thread.CurrentThread == _thread;

    public Task<T> EnqueueAsync<T>(Func<DbSession, T> work, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);

        if (IsWriterThread)
        {
            try { return Task.FromResult(work(_session)); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(session =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            try
            {
                tcs.TrySetResult(work(session));
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        try
        {
            _queue.Add(item, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            throw new StoreException(FailureCategory.Unsupported, "The store writer has shut down.", ex);
        }

        return tcs.Task;
    }

    public Task EnqueueAsync(Action<DbSession> work, CancellationToken ct) =>
        EnqueueAsync(session => { work(session); return true; }, ct);

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            // The delegate owns its own completion source, so a throw here is already observed.
            try { item.Run(_session); }
            catch (Exception) { /* observed by the caller's task */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();
        if (!IsWriterThread) _thread.Join(TimeSpan.FromSeconds(15));
        _session.Dispose();
        _queue.Dispose();
    }

    private sealed class WorkItem(Action<DbSession> run)
    {
        public Action<DbSession> Run { get; } = run;
    }
}
