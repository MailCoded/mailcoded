using System.Globalization;
using System.Security.Cryptography;
using Mailcoded.Core.Domain.Backup;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

public sealed record BackupResult
{
    public required string Directory { get; init; }

    public required BackupManifest Manifest { get; init; }
}

public sealed record BackupVerification
{
    public const int MaxProblems = 200;

    public required string Directory { get; init; }

    public BackupManifest? Manifest { get; init; }

    public required IReadOnlyList<BackupProblem> Problems { get; init; }

    public int ProblemCount { get; init; }

    public string Integrity { get; init; } = "not run";

    public long BlobFilesChecked { get; init; }

    public long BytesChecked { get; init; }

    public bool Ok => ProblemCount == 0;
}

/// <summary>Writes and verifies backups. Only store.db and the blob files it points at are copied, never a secret.</summary>
public sealed class BackupService
{
    private const int CopyBufferSize = 81_920;

    private readonly SqliteStore _store;
    private readonly AgentPolicy _policy;
    private readonly AuditLog _audit;
    private readonly IClock _clock;
    private readonly string _productVersion;
    private readonly string? _homeDirectory;
    private readonly SemaphoreSlim _running = new(1, 1);

    public BackupService(
        SqliteStore store,
        AgentPolicy policy,
        AuditLog audit,
        IClock clock,
        string productVersion,
        string? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);

        _store = store;
        _policy = policy;
        _audit = audit;
        _clock = clock;
        _productVersion = productVersion;
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public async Task<BackupResult> CreateAsync(CallerContext caller, string? directory, CancellationToken ct)
    {
        await RequireAsync(caller, directory, ct).ConfigureAwait(false);

        if (!_running.Wait(0))
            throw new StoreException(FailureCategory.Busy, "Another backup is already running in this process.");

        try
        {
            return await CreateCoreAsync(caller, directory, ct).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    public async Task<BackupVerification> VerifyAsync(CallerContext caller, string? directory, CancellationToken ct)
    {
        await RequireAsync(caller, directory, ct).ConfigureAwait(false);

        var rule = BackupPathRules.Check(directory, OperatingSystem.IsWindows());
        if (rule is not null) throw new ArgumentException(rule);
        if (!Path.IsPathFullyQualified(directory!)) throw new ArgumentException("The backup directory must be an absolute path.");

        var full = Path.GetFullPath(directory!);
        var result = await VerifyCoreAsync(full, ct).ConfigureAwait(false);

        var detail = AuditText.Fields(
            ("dest", full),
            ("problems", AuditText.Number(result.ProblemCount)),
            ("first", result.Problems.Count > 0 ? result.Problems[0].Kind : null));

        if (result.Ok) await _audit.InfoAsync(AuditEvents.BackupVerified, AccountId.None, caller, detail, ct).ConfigureAwait(false);
        else await _audit.WarnAsync(AuditEvents.BackupVerifyFailed, AccountId.None, caller, detail, ct).ConfigureAwait(false);

        return result;
    }

    private async Task RequireAsync(CallerContext caller, string? directory, CancellationToken ct)
    {
        try
        {
            _policy.RequireCapability(AgentCapability.Backup, caller);
        }
        catch (PolicyDeniedException)
        {
            await _audit.WarnAsync(
                AuditEvents.BackupDenied,
                AccountId.None,
                caller,
                AuditText.Fields(("dest", directory)),
                ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<BackupResult> CreateCoreAsync(CallerContext caller, string? directory, CancellationToken ct)
    {
        string full;
        try
        {
            full = BackupDestination.Resolve(directory, LiveRoots(), _homeDirectory);
        }
        catch (ArgumentException ex)
        {
            await _audit.WarnAsync(
                AuditEvents.BackupFailed,
                AccountId.None,
                caller,
                AuditText.Fields(("dest", directory), ("reason", ex.Message)),
                ct).ConfigureAwait(false);
            throw;
        }

        var written = new WrittenFiles(full, createdRoot: !System.IO.Directory.Exists(full));

        try
        {
            await _audit.InfoAsync(AuditEvents.BackupStarted, AccountId.None, caller, AuditText.Fields(("dest", full)), ct)
                .ConfigureAwait(false);

            var result = await WriteBackupAsync(full, written, ct).ConfigureAwait(false);

            await _audit.InfoAsync(
                AuditEvents.BackupCompleted,
                AccountId.None,
                caller,
                AuditText.Fields(
                    ("dest", full),
                    ("schema", AuditText.Number(result.Manifest.SchemaVersion)),
                    ("db_bytes", AuditText.Number(result.Manifest.DatabaseBytes)),
                    ("blobs", AuditText.Number(result.Manifest.Blobs.Count)),
                    ("blob_bytes", AuditText.Number(result.Manifest.BlobBytes))),
                ct).ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            written.Discard();

            var reason = ex is OperationCanceledException ? "cancelled" : ex.Message;
            try
            {
                await _audit.WarnAsync(
                    AuditEvents.BackupFailed,
                    AccountId.None,
                    caller,
                    AuditText.Fields(("dest", full), ("reason", reason)),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception audit) when (audit is StoreException or ObjectDisposedException)
            {
                // The original failure is the one the caller needs to see.
            }

            if (ex is IOException or UnauthorizedAccessException)
                throw new StoreException(FailureCategory.Permanent, "The backup could not be written: " + AuditText.Sanitize(ex.Message, 200), ex);

            throw;
        }
    }

    private async Task<BackupResult> WriteBackupAsync(string full, WrittenFiles written, CancellationToken ct)
    {
        CreateDirectory(full, written);

        var databasePath = Path.Combine(full, BackupManifest.DatabaseFileName);
        await CreateFileAsync(databasePath, written, ct).ConfigureAwait(false);
        await _store.BackupToAsync(databasePath, ct).ConfigureAwait(false);
        Restrict(databasePath);

        var facts = BackupDatabaseReader.Read(databasePath, fullIntegrityCheck: false, ct);
        var databaseSha = await HashFileAsync(databasePath, ct).ConfigureAwait(false);
        var databaseBytes = new FileInfo(databasePath).Length;

        foreach (var sha in facts.ExternalBlobHashes)
        {
            if (!BackupManifest.IsSha256Hex(sha))
                throw new StoreException(FailureCategory.Protocol, "The store holds an external blob row with a malformed hash.");
        }

        RequireSourcesPresent(facts.ExternalBlobHashes);

        var blobs = new List<BackupBlobEntry>(facts.ExternalBlobHashes.Count);
        foreach (var sha in facts.ExternalBlobHashes)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await CopyBlobAsync(sha, full, written, ct).ConfigureAwait(false);
            blobs.Add(new BackupBlobEntry(sha, bytes));
        }

        var tables = new List<BackupTableCount>(facts.TableCounts.Count);
        foreach (var (name, rows) in facts.TableCounts) tables.Add(new BackupTableCount(name, rows));

        var manifest = new BackupManifest
        {
            MailcodedVersion = _productVersion,
            SchemaVersion = facts.UserVersion,
            CreatedUtc = _clock.UtcNow,
            DatabaseSha256 = databaseSha,
            DatabaseBytes = databaseBytes,
            Tables = tables,
            Blobs = blobs,
        };

        var manifestPath = Path.Combine(full, BackupManifest.ManifestFileName);
        await using (var stream = OpenNew(manifestPath, written))
        {
            await stream.WriteAsync(manifest.ToJson(), ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        return new BackupResult { Directory = full, Manifest = manifest };
    }

    private void RequireSourcesPresent(IReadOnlyList<string> hashes)
    {
        var missing = 0;
        string? first = null;

        foreach (var sha in hashes)
        {
            if (File.Exists(StorePaths.ExternalBlobPath(_store.BlobDirectory, sha))) continue;
            missing++;
            first ??= sha[..12];
        }

        if (missing == 0) return;

        throw new StoreException(
            FailureCategory.Protocol,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{missing} attachment file(s) the store refers to are missing from its blob folder (first: {first}). A backup that could not be verified was not written."));
    }

    private async Task<long> CopyBlobAsync(string sha, string full, WrittenFiles written, CancellationToken ct)
    {
        var source = StorePaths.ExternalBlobPath(_store.BlobDirectory, sha);
        var target = Path.Combine(full, BackupManifest.BlobDirectoryName, sha[..2], sha + ".blob");

        var folder = Path.GetDirectoryName(target)!;
        CreateDirectory(Path.GetDirectoryName(folder)!, written);
        CreateDirectory(folder, written);

        var buffer = new byte[CopyBufferSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;

        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
        await using (var output = OpenNew(target, written))
        {
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                size += read;
            }

            output.Flush(flushToDisk: true);
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, sha, StringComparison.Ordinal))
        {
            throw new StoreException(
                FailureCategory.Protocol,
                $"An attachment file on disk does not match its recorded hash (blob {sha[..12]}); the store holds damaged data and the backup was not written.");
        }

        return size;
    }

    private async Task<BackupVerification> VerifyCoreAsync(string full, CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(full))
        {
            return Failed(full, null, new BackupProblem(BackupProblemKinds.DirectoryMissing, null, "There is no directory at that path."));
        }

        var manifestPath = Path.Combine(full, BackupManifest.ManifestFileName);
        var manifestInfo = new FileInfo(manifestPath);

        if (!IsReadableRegularFile(manifestInfo))
        {
            return Failed(
                full,
                null,
                new BackupProblem(BackupProblemKinds.ManifestMissing, BackupManifest.ManifestFileName, "manifest.json is missing or is not a regular file. A backup without one was never completed."));
        }

        if (manifestInfo.Length > BackupManifest.MaxManifestBytes)
        {
            return Failed(
                full,
                null,
                new BackupProblem(BackupProblemKinds.ManifestInvalid, BackupManifest.ManifestFileName, "The manifest is larger than any valid manifest."));
        }

        var parsed = BackupManifest.Parse(await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false));
        if (parsed.Manifest is not { } manifest)
        {
            var kind = parsed.Status == BackupManifestParseStatus.UnsupportedFormat
                ? BackupProblemKinds.UnsupportedFormat
                : BackupProblemKinds.ManifestInvalid;

            return Failed(full, null, new BackupProblem(kind, BackupManifest.ManifestFileName, parsed.Error ?? "The manifest is not valid."));
        }

        var observation = await ObserveAsync(full, manifest, ct).ConfigureAwait(false);
        var problems = BackupComparison.Compare(manifest, observation.Observation, SqliteStore.LatestSchemaVersion);

        return new BackupVerification
        {
            Directory = full,
            Manifest = manifest,
            Problems = problems.Count > BackupVerification.MaxProblems ? problems.GetRange(0, BackupVerification.MaxProblems) : problems,
            ProblemCount = problems.Count,
            Integrity = observation.Integrity,
            BlobFilesChecked = observation.BlobFilesChecked,
            BytesChecked = observation.BytesChecked,
        };
    }

    private static BackupVerification Failed(string full, BackupManifest? manifest, BackupProblem problem) =>
        new() { Directory = full, Manifest = manifest, Problems = [problem], ProblemCount = 1 };

    private static async Task<(BackupObservation Observation, string Integrity, long BlobFilesChecked, long BytesChecked)> ObserveAsync(
        string full,
        BackupManifest manifest,
        CancellationToken ct)
    {
        var (unexpected, links, tooMany) = Walk(full, manifest);

        long bytesChecked = 0;
        var database = new ObservedFile(false, 0, null);
        string? databaseError = null;
        BackupDatabaseFacts? facts = null;
        var integrity = "not run";

        var databasePath = Path.Combine(full, BackupManifest.DatabaseFileName);
        var databaseInfo = new FileInfo(databasePath);
        if (IsReadableRegularFile(databaseInfo))
        {
            var sha = await HashFileAsync(databasePath, ct).ConfigureAwait(false);
            database = new ObservedFile(true, databaseInfo.Length, sha);
            bytesChecked += databaseInfo.Length;

            try
            {
                facts = BackupDatabaseReader.Read(databasePath, fullIntegrityCheck: true, ct);
                integrity = facts.IntegrityMessages.Count == 0 ? "not run" : string.Join("; ", facts.IntegrityMessages.Take(BackupComparison.MaxIntegrityMessages));
            }
            catch (StoreException ex)
            {
                databaseError = ex.Message;
                integrity = "failed";
            }
        }

        var blobFiles = new Dictionary<string, ObservedFile>(StringComparer.Ordinal);
        foreach (var blob in manifest.Blobs)
        {
            ct.ThrowIfCancellationRequested();

            var path = Path.Combine(full, BackupManifest.BlobDirectoryName, blob.Sha256[..2], blob.Sha256 + ".blob");
            var info = new FileInfo(path);
            if (!IsReadableRegularFile(info))
            {
                blobFiles[blob.Sha256] = new ObservedFile(false, 0, null);
                continue;
            }

            var sha = await HashFileAsync(path, ct).ConfigureAwait(false);
            blobFiles[blob.Sha256] = new ObservedFile(true, info.Length, sha);
            bytesChecked += info.Length;
        }

        var observation = new BackupObservation
        {
            Database = database,
            DatabaseError = databaseError,
            UserVersion = facts?.UserVersion ?? 0,
            IntegrityMessages = facts?.IntegrityMessages ?? [],
            TableCounts = facts?.TableCounts,
            DatabaseBlobHashes = facts?.ExternalBlobHashes,
            BlobFiles = blobFiles,
            UnexpectedFiles = unexpected,
            LinkEntries = links,
            TooManyFiles = tooMany,
        };

        if (integrity == "not run" && facts is not null) integrity = "ok";
        return (observation, integrity, blobFiles.Values.Count(static f => f.Present), bytesChecked);
    }

    private static (List<string> Unexpected, List<string> Links, bool TooMany) Walk(string root, BackupManifest manifest)
    {
        var unexpected = new List<string>();
        var links = new List<string>();
        var limit = manifest.Blobs.Count + 1_000;
        var seen = 0;
        var pending = new Stack<string>();
        pending.Push(string.Empty);

        while (pending.Count > 0)
        {
            var relativeDirectory = pending.Pop();
            var directory = relativeDirectory.Length == 0 ? new DirectoryInfo(root) : new DirectoryInfo(Path.Combine(root, relativeDirectory));

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (++seen > limit) return (unexpected, links, true);

                var relative = relativeDirectory.Length == 0 ? entry.Name : relativeDirectory + "/" + entry.Name;

                if (entry.LinkTarget is not null)
                {
                    links.Add(relative);
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    pending.Push(relative);
                    continue;
                }

                if (!BackupComparison.IsExpectedPath(manifest, relative)) unexpected.Add(relative);
            }
        }

        unexpected.Sort(StringComparer.Ordinal);
        links.Sort(StringComparer.Ordinal);
        return (unexpected, links, false);
    }

    // FileInfo cannot tell a FIFO from a regular file and opening one blocks forever. A FIFO always reports length 0, and no valid manifest, database or blob is empty.
    private static bool IsReadableRegularFile(FileInfo info) => info.Exists && info.LinkTarget is null && info.Length > 0;

    private List<string> LiveRoots() =>
        [_store.DataDirectory, _store.BlobDirectory, Path.GetDirectoryName(_store.DatabasePath) ?? _store.DataDirectory];

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static void CreateDirectory(string path, WrittenFiles written)
    {
        if (System.IO.Directory.Exists(path)) return;

        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(path);
        else System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        written.Directories.Add(path);
    }

    private static async Task CreateFileAsync(string path, WrittenFiles written, CancellationToken ct)
    {
        await using var stream = OpenNew(path, written);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static FileStream OpenNew(string path, WrittenFiles written)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = CopyBufferSize,
            Options = FileOptions.Asynchronous,
        };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var stream = new FileStream(path, options);
        written.Files.Add(path);
        return stream;
    }

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed class WrittenFiles
    {
        private readonly string _root;
        private readonly bool _createdRoot;

        public WrittenFiles(string root, bool createdRoot)
        {
            _root = root;
            _createdRoot = createdRoot;
        }

        public List<string> Files { get; } = [];

        public List<string> Directories { get; } = [];

        public void Discard()
        {
            foreach (var file in Files)
            {
                TryRemove(file);
                TryRemove(file + "-journal");
                TryRemove(file + "-wal");
                TryRemove(file + "-shm");
            }

            for (var i = Directories.Count - 1; i >= 0; i--) TryRemoveDirectory(Directories[i]);
            if (_createdRoot) TryRemoveDirectory(_root);
        }

        private static void TryRemove(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the caller already has the real failure.
            }
        }

        private static void TryRemoveDirectory(string path)
        {
            try
            {
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; a non-empty folder is left for the user.
            }
        }
    }
}
