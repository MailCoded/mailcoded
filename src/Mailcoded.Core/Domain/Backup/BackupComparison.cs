using System.Globalization;
using System.Text;

namespace Mailcoded.Core.Domain.Backup;

public static class BackupProblemKinds
{
    public const string ManifestMissing = "manifest_missing";
    public const string ManifestInvalid = "manifest_invalid";
    public const string UnsupportedFormat = "unsupported_format";
    public const string DatabaseMissing = "database_missing";
    public const string DatabaseSize = "database_size";
    public const string DatabaseHash = "database_hash";
    public const string DatabaseUnreadable = "database_unreadable";
    public const string Integrity = "integrity";
    public const string SchemaVersion = "schema_version";
    public const string SchemaNewer = "schema_newer";
    public const string TableMissing = "table_missing";
    public const string TableUnlisted = "table_unlisted";
    public const string RowCount = "row_count";
    public const string BlobMissing = "blob_missing";
    public const string BlobSize = "blob_size";
    public const string BlobHash = "blob_hash";
    public const string BlobUnlisted = "blob_unlisted";
    public const string BlobUnreferenced = "blob_unreferenced";
    public const string UnexpectedFile = "unexpected_file";
    public const string LinkRefused = "link_refused";
    public const string TooManyFiles = "too_many_files";
    public const string DirectoryMissing = "directory_missing";
}

public sealed record BackupProblem(string Kind, string? Subject, string Message);

/// <summary>A file the verifier looked at. <c>Present</c> is false for a missing file or a link.</summary>
public sealed record ObservedFile(bool Present, long Bytes, string? Sha256);

/// <summary>Everything the verifier measured about a backup directory, with no judgement applied.</summary>
public sealed record BackupObservation
{
    public ObservedFile Database { get; init; } = new(false, 0, null);

    public string? DatabaseError { get; init; }

    public int UserVersion { get; init; }

    public IReadOnlyList<string> IntegrityMessages { get; init; } = [];

    public IReadOnlyDictionary<string, long>? TableCounts { get; init; }

    public IReadOnlyCollection<string>? DatabaseBlobHashes { get; init; }

    public IReadOnlyDictionary<string, ObservedFile> BlobFiles { get; init; } = new Dictionary<string, ObservedFile>(StringComparer.Ordinal);

    public IReadOnlyList<string> UnexpectedFiles { get; init; } = [];

    public IReadOnlyList<string> LinkEntries { get; init; } = [];

    public bool TooManyFiles { get; init; }
}

public static class BackupComparison
{
    public const int MaxIntegrityMessages = 20;

    public static bool IsExpectedPath(BackupManifest manifest, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.Equals(relativePath, BackupManifest.ManifestFileName, StringComparison.Ordinal)) return true;
        if (string.Equals(relativePath, BackupManifest.DatabaseFileName, StringComparison.Ordinal)) return true;

        foreach (var blob in manifest.Blobs)
            if (string.Equals(relativePath, BackupManifest.BlobRelativePath(blob.Sha256), StringComparison.Ordinal)) return true;

        return false;
    }

    public static List<BackupProblem> Compare(BackupManifest manifest, BackupObservation observed, int latestSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(observed);

        var problems = new List<BackupProblem>();
        CompareDatabase(manifest, observed, problems);
        CompareSchema(manifest, observed, latestSchemaVersion, problems);
        CompareIntegrity(observed, problems);
        CompareTables(manifest, observed, problems);
        CompareBlobs(manifest, observed, problems);

        if (observed.TooManyFiles)
            problems.Add(new BackupProblem(BackupProblemKinds.TooManyFiles, null, "The directory holds more entries than any backup can."));

        foreach (var link in observed.LinkEntries)
            problems.Add(new BackupProblem(BackupProblemKinds.LinkRefused, Clean(link), "A link inside a backup is never followed."));

        foreach (var file in observed.UnexpectedFiles)
            problems.Add(new BackupProblem(BackupProblemKinds.UnexpectedFile, Clean(file), "The manifest does not account for this file."));

        return problems;
    }

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(Math.Min(text.Length, 120));
        foreach (var c in text)
        {
            if (builder.Length >= 120) break;
            builder.Append(char.IsControl(c) || char.IsSurrogate(c) ? '?' : c);
        }

        return builder.ToString();
    }

    private static void CompareDatabase(BackupManifest manifest, BackupObservation observed, List<BackupProblem> problems)
    {
        var file = BackupManifest.DatabaseFileName;

        if (!observed.Database.Present)
        {
            problems.Add(new BackupProblem(BackupProblemKinds.DatabaseMissing, file, "store.db is missing or is not a regular file."));
            return;
        }

        if (observed.Database.Bytes != manifest.DatabaseBytes)
        {
            problems.Add(new BackupProblem(
                BackupProblemKinds.DatabaseSize,
                file,
                string.Create(CultureInfo.InvariantCulture, $"The manifest records {manifest.DatabaseBytes} bytes; the file has {observed.Database.Bytes}.")));
        }

        if (!string.Equals(observed.Database.Sha256, manifest.DatabaseSha256, StringComparison.Ordinal))
        {
            problems.Add(new BackupProblem(
                BackupProblemKinds.DatabaseHash,
                file,
                $"The manifest records sha256 {manifest.DatabaseSha256}; the file hashes to {observed.Database.Sha256}."));
        }

        if (observed.DatabaseError is { } error)
            problems.Add(new BackupProblem(BackupProblemKinds.DatabaseUnreadable, file, Clean(error)));
    }

    private static void CompareSchema(BackupManifest manifest, BackupObservation observed, int latest, List<BackupProblem> problems)
    {
        if (manifest.SchemaVersion > latest)
        {
            problems.Add(new BackupProblem(
                BackupProblemKinds.SchemaNewer,
                null,
                string.Create(CultureInfo.InvariantCulture, $"The backup is schema {manifest.SchemaVersion}; this build knows up to {latest}. Use the mailcoded that made it.")));
        }

        if (!observed.Database.Present || observed.DatabaseError is not null) return;

        if (observed.UserVersion != manifest.SchemaVersion)
        {
            problems.Add(new BackupProblem(
                BackupProblemKinds.SchemaVersion,
                null,
                string.Create(CultureInfo.InvariantCulture, $"The manifest records schema {manifest.SchemaVersion}; the database is schema {observed.UserVersion}.")));
        }
    }

    private static void CompareIntegrity(BackupObservation observed, List<BackupProblem> problems)
    {
        var shown = 0;
        foreach (var message in observed.IntegrityMessages)
        {
            if (string.Equals(message, "ok", StringComparison.Ordinal)) continue;
            if (shown++ >= MaxIntegrityMessages) break;

            problems.Add(new BackupProblem(BackupProblemKinds.Integrity, null, Clean(message)));
        }
    }

    private static void CompareTables(BackupManifest manifest, BackupObservation observed, List<BackupProblem> problems)
    {
        if (observed.TableCounts is not { } counts) return;

        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in manifest.Tables)
        {
            listed.Add(table.Name);

            if (!counts.TryGetValue(table.Name, out var rows))
            {
                problems.Add(new BackupProblem(BackupProblemKinds.TableMissing, table.Name, "The manifest lists a table the database does not have."));
                continue;
            }

            if (rows != table.Rows)
            {
                problems.Add(new BackupProblem(
                    BackupProblemKinds.RowCount,
                    table.Name,
                    string.Create(CultureInfo.InvariantCulture, $"The manifest records {table.Rows} rows; the table has {rows}.")));
            }
        }

        foreach (var name in counts.Keys.OrderBy(static n => n, StringComparer.Ordinal))
        {
            if (listed.Contains(name)) continue;
            problems.Add(new BackupProblem(BackupProblemKinds.TableUnlisted, Clean(name), "The database has a table the manifest does not list."));
        }
    }

    private static void CompareBlobs(BackupManifest manifest, BackupObservation observed, List<BackupProblem> problems)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var blob in manifest.Blobs)
        {
            listed.Add(blob.Sha256);
            var subject = BackupManifest.BlobRelativePath(blob.Sha256);

            if (!observed.BlobFiles.TryGetValue(blob.Sha256, out var file) || !file.Present)
            {
                problems.Add(new BackupProblem(BackupProblemKinds.BlobMissing, subject, "The file is missing or is not a regular file."));
                continue;
            }

            if (file.Bytes != blob.Bytes)
            {
                problems.Add(new BackupProblem(
                    BackupProblemKinds.BlobSize,
                    subject,
                    string.Create(CultureInfo.InvariantCulture, $"The manifest records {blob.Bytes} bytes; the file has {file.Bytes}.")));
            }

            if (!string.Equals(file.Sha256, blob.Sha256, StringComparison.Ordinal))
            {
                problems.Add(new BackupProblem(
                    BackupProblemKinds.BlobHash,
                    subject,
                    $"The file hashes to {file.Sha256}, not to the name it is filed under."));
            }
        }

        if (observed.DatabaseBlobHashes is not { } referenced) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sha in referenced.OrderBy(static s => s, StringComparer.Ordinal))
        {
            seen.Add(sha);
            if (listed.Contains(sha)) continue;

            problems.Add(new BackupProblem(
                BackupProblemKinds.BlobUnlisted,
                Clean(sha),
                "The database points at an external blob the manifest does not list."));
        }

        foreach (var blob in manifest.Blobs)
        {
            if (seen.Contains(blob.Sha256)) continue;

            problems.Add(new BackupProblem(
                BackupProblemKinds.BlobUnreferenced,
                BackupManifest.BlobRelativePath(blob.Sha256),
                "The manifest lists an external blob the database does not point at."));
        }
    }
}
