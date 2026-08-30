using System.Collections.Concurrent;
using Mailcoded.Core.Secrets;

namespace Mailcoded.Integration.Support;

/// <summary>Process-lifetime secret store: a test credential must never reach a disk vault.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public string BackendName => "in-memory-test";

    public bool IsAvailable => true;

    public Task SetAsync(string secretRef, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        ArgumentException.ThrowIfNullOrEmpty(value);
        ct.ThrowIfCancellationRequested();

        _values[secretRef] = value;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string secretRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_values.TryGetValue(secretRef, out var value) ? value : null);
    }

    public Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _values.TryRemove(secretRef, out _);
        return Task.CompletedTask;
    }
}
