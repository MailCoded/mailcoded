using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Tests.Support;

/// <summary>A private temp directory holding one store; disposing it removes the whole tree.</summary>
public sealed class TempWorkspace : IDisposable
{
    private bool _disposed;

    private TempWorkspace(string root) => Root = root;

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, "store.db");

    public string BlobDirectory => Path.Combine(Root, "blobs");

    public static TempWorkspace Create(string label = "mailcoded")
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mailcoded-tests",
            label + "-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);
        return new TempWorkspace(root);
    }

    /// <summary>A path inside the workspace for a scratch file. The file is not created.</summary>
    public string PathFor(string fileName) => Path.Combine(Root, fileName);

    public SqliteStore OpenStore(
        IClock? clock = null,
        Func<SqliteStoreOptions, SqliteStoreOptions>? configure = null)
    {
        var options = new SqliteStoreOptions
        {
            DatabasePath = DatabasePath,
            DataDirectory = Root,
            BlobDirectory = BlobDirectory,
            ReaderPoolSize = 2,
        };

        if (configure is not null) options = configure(options);

        RefuseRealStore(options);
        return new SqliteStore(options, clock ?? new TestClock());
    }

    /// <summary>Makes "no test touches the real store" structural rather than a convention.</summary>
    private void RefuseRealStore(SqliteStoreOptions options)
    {
        var path = options.DatabasePath
            ?? throw new InvalidOperationException("A test store must set DatabasePath explicitly.");

        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Root);

        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"A test tried to open a store outside its workspace ('{full}'). The real store is never valid in a test.");

        var real = Path.GetFullPath(StorePaths.DefaultDataDirectory());
        if (full.StartsWith(real, StringComparison.Ordinal))
            throw new InvalidOperationException($"A test tried to open the real user store at '{full}'.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
    }
}
