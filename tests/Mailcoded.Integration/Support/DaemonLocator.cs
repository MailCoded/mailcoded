namespace Mailcoded.Integration.Support;

public sealed record DaemonLaunch(string FileName, IReadOnlyList<string> Arguments);

/// <summary>Finds the built daemon so a test can drive the real stdio surface, not a stand-in.</summary>
public static class DaemonLocator
{
    public const string PathEnvVar = "MAILCODED_DAEMON";
    public const string AssemblyName = "Mailcoded.Daemon";

    public static DaemonLaunch? TryLocate(out string reason)
    {
        var configured = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(configured.Trim());
            if (!File.Exists(path))
            {
                reason = $"{PathEnvVar} points at '{path}', which does not exist.";
                return null;
            }

            reason = string.Empty;
            return Launch(path);
        }

        var root = FindRepositoryRoot();
        if (root is null)
        {
            reason =
                $"The daemon was not found: no Mailcoded.slnx above '{AppContext.BaseDirectory}' and {PathEnvVar} is unset.";
            return null;
        }

        var binRoot = Path.Combine(root, "src", AssemblyName, "bin");
        if (!Directory.Exists(binRoot))
        {
            reason = $"The daemon was not found: '{binRoot}' does not exist. Build the solution first.";
            return null;
        }

        FileInfo? newest = null;
        foreach (var candidate in Directory.EnumerateFiles(binRoot, AssemblyName + ".dll", SearchOption.AllDirectories))
        {
            var info = new FileInfo(candidate);
            if (newest is null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = info;
        }

        if (newest is null)
        {
            reason = $"The daemon was not found: no {AssemblyName}.dll under '{binRoot}'. Build the solution first.";
            return null;
        }

        reason = string.Empty;
        return Launch(newest.FullName);
    }

    /// <summary>Prefers the apphost so the test does not depend on the SDK being on PATH.</summary>
    private static DaemonLaunch Launch(string path)
    {
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return new DaemonLaunch(path, []);

        var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        var appHost = Path.Combine(directory, AssemblyName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        if (File.Exists(appHost)) return new DaemonLaunch(appHost, []);

        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return new DaemonLaunch(string.IsNullOrWhiteSpace(host) ? "dotnet" : host, [path]);
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mailcoded.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }
}
