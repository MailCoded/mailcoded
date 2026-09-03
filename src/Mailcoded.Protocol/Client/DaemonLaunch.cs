using System.Diagnostics;

namespace Mailcoded.Protocol.Client;

/// <summary>How to start a daemon: an executable, its arguments, and where its store lives.</summary>
public sealed record DaemonLaunch
{
    public const string ExecutableEnvVar = "MAILCODED_DAEMON";
    public const string ExecutableName = "mailcoded-daemon";

    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? StorePath { get; init; }

    /// <summary>Env override, then a sibling of this process, then PATH.</summary>
    /// <remarks>Sibling beats PATH so an installed pair wins over an unrelated earlier match.</remarks>
    public static DaemonLaunch Discover(string? storePath = null)
    {
        var explicitPath = Environment.GetEnvironmentVariable(ExecutableEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return new DaemonLaunch { FileName = explicitPath.Trim(), StorePath = storePath };

        if (Sibling() is { } sibling)
            return new DaemonLaunch { FileName = sibling, StorePath = storePath };

        return new DaemonLaunch { FileName = ExecutableName, StorePath = storePath };
    }

    internal ProcessStartInfo ToStartInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in Arguments) info.ArgumentList.Add(argument);

        if (!string.IsNullOrWhiteSpace(StorePath))
        {
            info.ArgumentList.Add("--store");
            info.ArgumentList.Add(StorePath);
        }

        return info;
    }

    private static string? Sibling()
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(directory)) return null;

        foreach (var candidate in new[] { ExecutableName, ExecutableName + ".exe" })
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path)) return path;
        }

        return null;
    }
}
