using System.Runtime.CompilerServices;

namespace Mailcoded.Core.Providers;

/// <summary>
/// Serializes every command issued against one IMAP client. MailKit permits exactly one command
/// in flight per client, so an on-demand fetch must never overlap a sync that is still streaming.
/// </summary>
public sealed class ImapCommandQueue : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await command(ct).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    public async Task RunAsync(Func<CancellationToken, Task> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await command(ct).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Holds the queue for the whole enumeration, which is what a streaming sync needs.</summary>
    public async IAsyncEnumerable<T> RunStreamAsync<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> command,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var item in command(ct).WithCancellation(ct).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            gate.Dispose();
    }

    private void Release()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
