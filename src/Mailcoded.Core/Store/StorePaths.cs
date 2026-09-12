namespace Mailcoded.Core.Store;

/// <summary>
/// Resolves the per-OS store root from SPEC §5.3. The root deliberately never lives inside a
/// workspace folder, and stays short on Windows because of MAX_PATH.
/// </summary>
public static class StorePaths
{
    public const string ApplicationFolderName = "mailcoded";
    public const string DatabaseFileName = "store.db";
    public const string BlobFolderName = "blobs";
    public const string ModelFolderName = "model";

    public static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
                localAppData = Path.Combine(HomeDirectory(), "AppData", "Local");
            return Path.Combine(localAppData, ApplicationFolderName);
        }

        if (OperatingSystem.IsMacOS())
            return Path.Combine(HomeDirectory(), "Library", "Application Support", ApplicationFolderName);

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(xdg) || !Path.IsPathRooted(xdg))
            xdg = Path.Combine(HomeDirectory(), ".local", "share");

        return Path.Combine(xdg, ApplicationFolderName);
    }

    public static string DefaultDatabasePath() => Path.Combine(DefaultDataDirectory(), DatabaseFileName);

    public static string BlobDirectoryFor(string dataDirectory) => Path.Combine(dataDirectory, BlobFolderName);

    /// <summary>Where an installed embedding model lives. Nothing in the product ever writes here;
    /// only the installer puts a model there, and only when asked.</summary>
    public static string ModelDirectoryFor(string dataDirectory) => Path.Combine(dataDirectory, ModelFolderName);

    /// <summary>
    /// Content-addressed location for an externalized blob. The name is hex only, so it is legal
    /// on NTFS as well as POSIX (never a Maildir-style ':' separator).
    /// </summary>
    public static string ExternalBlobPath(string blobDirectory, string sha256Hex) =>
        Path.Combine(blobDirectory, sha256Hex[..2], sha256Hex + ".blob");

    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) return home;

        home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrEmpty(home) ? Directory.GetCurrentDirectory() : home;
    }
}
