using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Bench.Corpus;

public sealed record CorpusManifest
{
    public required int Seed { get; init; }
    public required int Size { get; init; }
    public required string Fingerprint { get; init; }
    public required int SchemaVersion { get; init; }
    public required long DatabaseBytes { get; init; }
    public required double BuildSeconds { get; init; }
}

/// <summary>Materializes the generated corpus into a store, and caches it keyed by seed and size.</summary>
public static class CorpusBuilder
{
    public const int IngestBatchSize = 5_000;

    public static CorpusManifest EnsureBuilt(CorpusOptions options, TextWriter log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        var expected = new SyntheticMailbox(options).Fingerprint();
        var cached = ReadManifest(options);

        if (cached is not null
            && cached.Size == options.Size
            && cached.Seed == options.Seed
            && string.Equals(cached.Fingerprint, expected, StringComparison.Ordinal)
            && File.Exists(options.DatabasePath))
        {
            log.WriteLine($"corpus: reusing {options.Key} ({expected[..12]}) at {options.DatabasePath}");
            return cached;
        }

        log.WriteLine($"corpus: building {options.Key} ({expected[..12]}) at {options.DatabasePath}");
        return Build(options, expected, log, ct);
    }

    public static SqliteStore OpenExisting(CorpusOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(options.DatabasePath))
            throw new InvalidOperationException($"No corpus at '{options.DatabasePath}'. Run with --generate first.");

        return new SqliteStore(
            new SqliteStoreOptions { DatabasePath = options.DatabasePath, DataDirectory = options.Directory },
            SystemClock.Instance);
    }

    private static CorpusManifest Build(CorpusOptions options, string fingerprint, TextWriter log, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        if (Directory.Exists(options.Directory)) Directory.Delete(options.Directory, recursive: true);
        Directory.CreateDirectory(options.Directory);

        using var store = new SqliteStore(
            new SqliteStoreOptions
            {
                DatabasePath = options.DatabasePath,
                DataDirectory = options.Directory,
                PageSize = 8192,
            },
            SystemClock.Instance);

        var folders = CreateFoldersAsync(store, options, ct).GetAwaiter().GetResult();
        IngestEnvelopes(store, options, folders, log, ct);
        WriteBodies(store, options, folders, log, ct);

        store.RunMaintenanceAsync(
                new MaintenanceOptions { OptimizeFts = true, Analyze = true, CheckpointWal = true, TruncateWal = true },
                ct)
            .GetAwaiter()
            .GetResult();

        stopwatch.Stop();

        var manifest = new CorpusManifest
        {
            Seed = options.Seed,
            Size = options.Size,
            Fingerprint = fingerprint,
            SchemaVersion = store.SchemaVersion,
            DatabaseBytes = new FileInfo(options.DatabasePath).Length,
            BuildSeconds = stopwatch.Elapsed.TotalSeconds,
        };

        WriteManifest(options, manifest);
        log.WriteLine(
            $"corpus: built {options.Size.ToString("N0", CultureInfo.InvariantCulture)} envelopes in "
            + $"{manifest.BuildSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");

        return manifest;
    }

    private static async Task<IReadOnlyList<FolderId>> CreateFoldersAsync(
        SqliteStore store,
        CorpusOptions options,
        CancellationToken ct)
    {
        var accountId = await store.AddAccountAsync(
            new AccountConfig
            {
                Email = options.Mailbox,
                DisplayName = "bench",
                Provider = ProviderKind.Imap,
                Auth = AuthKind.Password,
                SecretRef = "bench:no-credential",
                Imap = new ImapConfig { Host = "bench.invalid", Port = 143, Security = SecureSocket.None },
            },
            ct).ConfigureAwait(false);

        var remote = new List<RemoteFolder>(SyntheticMailbox.FolderPaths.Count);
        foreach (var path in SyntheticMailbox.FolderPaths)
        {
            var folderPath = FolderPath.Create(path);
            remote.Add(new RemoteFolder { Path = folderPath, Role = RoleFor(folderPath) });
        }

        return await store.UpsertFoldersAsync(accountId, remote, ct).ConfigureAwait(false);
    }

    private static void IngestEnvelopes(
        SqliteStore store,
        CorpusOptions options,
        IReadOnlyList<FolderId> folders,
        TextWriter log,
        CancellationToken ct)
    {
        var session = store.BeginBulkIngestAsync(ReferencesThreader.Instance, ct).GetAwaiter().GetResult();
        var batches = new List<RemoteEnvelope>[folders.Count];
        for (var i = 0; i < batches.Length; i++) batches[i] = new List<RemoteEnvelope>(IngestBatchSize);

        try
        {
            var done = 0;
            foreach (var message in new SyntheticMailbox(options).Generate())
            {
                ct.ThrowIfCancellationRequested();

                var batch = batches[message.FolderIndex];
                batch.Add(ToEnvelope(message));

                if (batch.Count < IngestBatchSize) continue;

                session.AddAsync(folders[message.FolderIndex], batch, ct).GetAwaiter().GetResult();
                done += batch.Count;
                batch.Clear();

                if (done % 100_000 == 0) log.WriteLine($"corpus: {done.ToString("N0", CultureInfo.InvariantCulture)} envelopes");
            }

            for (var i = 0; i < batches.Length; i++)
            {
                if (batches[i].Count == 0) continue;
                session.AddAsync(folders[i], batches[i], ct).GetAwaiter().GetResult();
                batches[i].Clear();
            }

            session.CompleteAsync(ct).GetAwaiter().GetResult();
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>One transaction per body: the bulk window exposes no way to supply body text, and the
    /// FTS gates are meaningless without it. This dominates the cost of a 500k build.</summary>
    private static void WriteBodies(
        SqliteStore store,
        CorpusOptions options,
        IReadOnlyList<FolderId> folders,
        TextWriter log,
        CancellationToken ct)
    {
        var written = 0;

        foreach (var message in new SyntheticMailbox(options).Generate())
        {
            ct.ThrowIfCancellationRequested();

            var id = store.FindMessage(folders[message.FolderIndex], new Uid(message.Uid), ct);
            if (id is not { } messageId) continue;

            store.SetBodyTextAsync(messageId, message.Body, null, message.HasAttachment, ct).GetAwaiter().GetResult();
            written++;

            if (written % 50_000 == 0)
                log.WriteLine($"corpus: {written.ToString("N0", CultureInfo.InvariantCulture)} bodies");
        }
    }

    private static FolderRole RoleFor(FolderPath path) => path.LeafName switch
    {
        "INBOX" => FolderRole.Inbox,
        "Sent" => FolderRole.Sent,
        "Drafts" => FolderRole.Drafts,
        "Trash" => FolderRole.Trash,
        "Archive" => FolderRole.Archive,
        "Junk" => FolderRole.Junk,
        _ => FolderRole.None,
    };

    private static RemoteEnvelope ToEnvelope(SyntheticMessage message) => new()
    {
        Uid = new Uid(message.Uid),
        Flags = message.Flags,
        ModSeq = new ModSeq((ulong)message.Index + 1),
        MessageIdHeader = message.MessageId,
        InReplyTo = message.InReplyTo,
        References = message.InReplyTo is { } parent ? new[] { parent } : Array.Empty<string>(),
        Subject = message.Subject,
        From = message.From,
        To = message.To,
        DateUtc = message.DateUtc,
        Size = message.Size,
        HasAttachments = message.HasAttachment,
    };

    private static CorpusManifest? ReadManifest(CorpusOptions options)
    {
        if (!File.Exists(options.ManifestPath)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(options.ManifestPath));
            var root = document.RootElement;

            return new CorpusManifest
            {
                Seed = root.GetProperty("seed").GetInt32(),
                Size = root.GetProperty("size").GetInt32(),
                Fingerprint = root.GetProperty("fingerprint").GetString() ?? string.Empty,
                SchemaVersion = root.GetProperty("schemaVersion").GetInt32(),
                DatabaseBytes = root.GetProperty("databaseBytes").GetInt64(),
                BuildSeconds = root.GetProperty("buildSeconds").GetDouble(),
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static void WriteManifest(CorpusOptions options, CorpusManifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seed", manifest.Seed);
            writer.WriteNumber("size", manifest.Size);
            writer.WriteString("fingerprint", manifest.Fingerprint);
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteNumber("databaseBytes", manifest.DatabaseBytes);
            writer.WriteNumber("buildSeconds", manifest.BuildSeconds);
            writer.WriteEndObject();
        }

        File.WriteAllText(options.ManifestPath, Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
