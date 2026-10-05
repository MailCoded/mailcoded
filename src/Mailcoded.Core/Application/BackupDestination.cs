using Mailcoded.Core.Domain.Backup;

namespace Mailcoded.Core.Application;

/// <summary>Decides whether a path may receive a backup. Every refusal is an <see cref="ArgumentException"/>.</summary>
public static class BackupDestination
{
    private const int MaxLinkDepth = 16;

    private static readonly string[] WorkspaceDirectories = [".git", ".vscode", ".obsidian", ".idea"];

    public static string Resolve(string? requested, IReadOnlyList<string> liveRoots, string? homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(liveRoots);

        var windows = OperatingSystem.IsWindows();
        if (BackupPathRules.Check(requested, windows) is { } rule) throw new ArgumentException(rule);
        if (!Path.IsPathFullyQualified(requested!))
            throw new ArgumentException("The backup destination must be an absolute path.");

        var full = TrimSeparators(Path.GetFullPath(requested!));
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || string.Equals(TrimSeparators(root), full, StringComparison.Ordinal))
            throw new ArgumentException("A filesystem root cannot be a backup destination.");

        var ignoreCase = windows || OperatingSystem.IsMacOS();
        var real = ResolveReal(full);

        var info = new FileInfo(full);
        if (info.LinkTarget is not null)
            throw new ArgumentException("The backup destination is a symbolic link; name the real directory instead.");

        if (File.Exists(full))
            throw new ArgumentException("The backup destination is a file, not a directory.");

        if (Directory.Exists(full))
        {
            using var entries = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
            if (entries.MoveNext()) throw new ArgumentException("The backup destination is not empty.");
        }
        else
        {
            var parent = Path.GetDirectoryName(full);
            if (parent is null || !Directory.Exists(parent))
                throw new ArgumentException("The parent of the backup destination does not exist; create it first.");
        }

        foreach (var liveRoot in liveRoots)
        {
            if (string.IsNullOrWhiteSpace(liveRoot)) continue;

            var liveReal = ResolveReal(Path.GetFullPath(liveRoot));
            if (BackupPathRules.IsSameOrInside(real, liveReal, ignoreCase) || BackupPathRules.IsSameOrInside(liveReal, real, ignoreCase))
                throw new ArgumentException("The backup destination overlaps the live mail store.");
        }

        if (FindWorkspaceMarker(real, homeDirectory, ignoreCase) is { } marker)
        {
            throw new ArgumentException(
                $"The backup destination sits inside a workspace folder (it holds '{marker}'). "
                + "A backup is a full copy of the mail; choose a folder outside every workspace.");
        }

        return full;
    }

    public static string? FindWorkspaceMarker(string real, string? homeDirectory, bool ignoreCase)
    {
        var home = string.IsNullOrWhiteSpace(homeDirectory) ? null : TrimSeparators(ResolveReal(Path.GetFullPath(homeDirectory)));
        var current = Path.GetDirectoryName(real);

        while (!string.IsNullOrEmpty(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;

            if (home is not null && string.Equals(TrimSeparators(current), home, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;

            if (MarkerIn(current) is { } marker) return marker;
            current = parent;
        }

        return null;
    }

    private static string? MarkerIn(string directory)
    {
        try
        {
            foreach (var name in WorkspaceDirectories)
            {
                var candidate = Path.Combine(directory, name);
                if (Directory.Exists(candidate) || File.Exists(candidate)) return name;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.code-workspace"))
                return Path.GetFileName(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string ResolveReal(string fullPath, int depth = 0)
    {
        if (depth > MaxLinkDepth) throw new ArgumentException("Too many symbolic links in the path.");

        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;

        foreach (var part in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            var link = new FileInfo(next);

            if (link.LinkTarget is null)
            {
                current = next;
                continue;
            }

            try
            {
                var target = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? Path.GetFullPath(link.LinkTarget, current);
                current = ResolveReal(Path.GetFullPath(target), depth + 1);
            }
            catch (IOException)
            {
                throw new ArgumentException("A symbolic link in the destination path cannot be resolved.");
            }
        }

        return current;
    }

    private static string TrimSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var end = path.Length;
        while (end > root.Length && (path[end - 1] == Path.DirectorySeparatorChar || path[end - 1] == Path.AltDirectorySeparatorChar)) end--;
        return path[..end];
    }
}
