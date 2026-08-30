namespace Mailcoded.Core.Secrets;

/// <summary>
/// Tries each store in order, skipping any that reports itself unavailable. Writes go to the first
/// available store; reads fall through the chain so a credential written before the keyring
/// appeared (or disappeared) is still found. Availability is re-checked per call because a keyring
/// can come and go with the desktop session.
/// </summary>
public sealed class ChainedSecretStore : ISecretStore
{
    private readonly IReadOnlyList<ISecretStore> _stores;

    public ChainedSecretStore(params ISecretStore[] stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        if (stores.Length == 0)
            throw new SecretStoreException("A secret store chain needs at least one store.");
        _stores = stores;
    }

    public string BackendName => "chain(" + string.Join(",", _stores.Select(static s => s.BackendName)) + ")";

    public bool IsAvailable => _stores.Any(static s => s.IsAvailable);

    /// <summary>The store a write would land in right now, or null when nothing is available.</summary>
    public ISecretStore? Primary => _stores.FirstOrDefault(static s => s.IsAvailable);

    public Task SetAsync(string secretRef, string value, CancellationToken ct)
    {
        var target = Primary ?? throw NoBackend(secretRef);
        return target.SetAsync(secretRef, value, ct);
    }

    public async Task<string?> GetAsync(string secretRef, CancellationToken ct)
    {
        var probed = false;
        foreach (var store in _stores)
        {
            if (!store.IsAvailable) continue;
            probed = true;
            var value = await store.GetAsync(secretRef, ct).ConfigureAwait(false);
            if (value is not null) return value;
        }
        if (!probed) throw NoBackend(secretRef);
        return null;
    }

    public async Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        var probed = false;
        foreach (var store in _stores)
        {
            if (!store.IsAvailable) continue;
            probed = true;
            await store.DeleteAsync(secretRef, ct).ConfigureAwait(false);
        }
        if (!probed) throw NoBackend(secretRef);
    }

    private static SecretStoreException NoBackend(string secretRef) =>
        new($"No secret backend is available for '{SecretRedactor.SafeRef(secretRef)}'.");
}
