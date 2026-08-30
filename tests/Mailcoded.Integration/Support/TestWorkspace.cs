namespace Mailcoded.Integration.Support;

/// <summary>A throwaway store root. Kept when <c>MAILCODED_IT_KEEP_WORKSPACE</c> is set.</summary>
public sealed class TestWorkspace : IDisposable
{
    public const string KeepEnvVar = "MAILCODED_IT_KEEP_WORKSPACE";

    private TestWorkspace(string root) => Root = root;

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, "store.db");

    public static TestWorkspace Create(string label)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mailcoded-it",
            label + "-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(root);
        return new TestWorkspace(root);
    }

    public void Dispose()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeepEnvVar))) return;

        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
