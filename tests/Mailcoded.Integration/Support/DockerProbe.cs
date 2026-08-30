namespace Mailcoded.Integration.Support;

/// <summary>Reachability of a container runtime; a missing one must SKIP loudly, never pass.</summary>
public static class DockerProbe
{
    public const string SkipEnvVar = "MAILCODED_SKIP_INTEGRATION";
    public const string DockerHostEnvVar = "DOCKER_HOST";

    private static readonly string[] UnixSocketCandidates =
    [
        "/var/run/docker.sock",
        "/run/docker.sock",
        "/run/podman/podman.sock",
        "/var/run/podman/podman.sock",
    ];

    /// <summary>Null when containers can be started; otherwise the reason a test must report.</summary>
    public static string? UnavailableReason()
    {
        if (IsTruthy(Read(SkipEnvVar)))
            return $"Integration tests were skipped because {SkipEnvVar} is set.";

        var host = Read(DockerHostEnvVar);
        if (!string.IsNullOrWhiteSpace(host)) return null;

        foreach (var candidate in EnumerateSocketCandidates())
        {
            if (SocketExists(candidate)) return null;
        }

        if (OperatingSystem.IsWindows() && NamedPipeExists("docker_engine")) return null;

        return "Integration tests were SKIPPED: no Docker or Podman endpoint was found "
            + $"(no {DockerHostEnvVar}, no engine socket). Dovecot and smtp4dev could not be started, "
            + "so the end-to-end path was NOT verified.";
    }

    public static bool IsAvailable => UnavailableReason() is null;

    private static IEnumerable<string> EnumerateSocketCandidates()
    {
        foreach (var path in UnixSocketCandidates) yield return path;

        var runtimeDir = Read("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDir))
        {
            yield return Path.Combine(runtimeDir, "docker.sock");
            yield return Path.Combine(runtimeDir, "podman", "podman.sock");
        }

        string home;
        try
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch (Exception)
        {
            yield break;
        }

        if (string.IsNullOrEmpty(home)) yield break;

        yield return Path.Combine(home, ".docker", "run", "docker.sock");
        yield return Path.Combine(home, ".docker", "desktop", "docker.sock");
        yield return Path.Combine(home, ".colima", "default", "docker.sock");
        yield return Path.Combine(home, ".rd", "docker.sock");
    }

    private static bool SocketExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool NamedPipeExists(string name)
    {
        try
        {
            foreach (var pipe in Directory.GetFiles(@"\\.\pipe\"))
            {
                if (pipe.EndsWith(name, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    private static string? Read(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        return v.Equals("1", StringComparison.Ordinal)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
