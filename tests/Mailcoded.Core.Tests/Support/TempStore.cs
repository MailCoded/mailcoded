using Mailcoded.Core.Store;

namespace Mailcoded.Core.Tests.Support;

/// <summary>A fresh store on a fresh temp directory; both go away on dispose.</summary>
public sealed class TempStore : IDisposable
{
    private bool _disposed;

    private TempStore(TempWorkspace workspace, SqliteStore store, TestClock clock)
    {
        Workspace = workspace;
        Store = store;
        Clock = clock;
    }

    public TempWorkspace Workspace { get; }

    public SqliteStore Store { get; }

    public TestClock Clock { get; }

    public static TempStore Create(
        TestClock? clock = null,
        Func<SqliteStoreOptions, SqliteStoreOptions>? configure = null)
    {
        var workspace = TempWorkspace.Create();
        var testClock = clock ?? new TestClock();

        try
        {
            return new TempStore(workspace, workspace.OpenStore(testClock, configure), testClock);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Store.Dispose();
        Workspace.Dispose();
    }
}
