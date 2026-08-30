using BenchmarkDotNet.Attributes;
using Mailcoded.Bench.Corpus;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Domain.Threading;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Bench.Benchmarks;

/// <summary>The §15.8 ingest gate: 100k envelopes into a fresh store, network mocked out entirely.</summary>
[MemoryDiagnoser]
[ShortRunJob]
[RunOncePerIteration]
public class IngestBenchmarks
{
    public const int Rows = 100_000;
    public const int BatchSize = 5_000;

    private readonly List<RemoteEnvelope[]> _batches = [];

    private string _root = string.Empty;
    private SqliteStore? _store;
    private FolderId _folderId;

    [GlobalSetup]
    public void Setup()
    {
        var options = CorpusOptions.FromEnvironment() with { Size = Rows };
        var batch = new List<RemoteEnvelope>(BatchSize);
        uint uid = 0;

        foreach (var message in new SyntheticMailbox(options).Generate())
        {
            batch.Add(new RemoteEnvelope
            {
                Uid = new Uid(++uid),
                Flags = message.Flags,
                ModSeq = new ModSeq(uid),
                MessageIdHeader = message.MessageId,
                InReplyTo = message.InReplyTo,
                References = message.InReplyTo is { } parent ? new[] { parent } : Array.Empty<string>(),
                Subject = message.Subject,
                From = message.From,
                To = message.To,
                DateUtc = message.DateUtc,
                Size = message.Size,
                HasAttachments = message.HasAttachment,
            });

            if (batch.Count < BatchSize) continue;
            _batches.Add(batch.ToArray());
            batch.Clear();
        }

        if (batch.Count > 0) _batches.Add(batch.ToArray());
    }

    [IterationSetup]
    public void OpenScratchStore()
    {
        _root = Path.Combine(Path.GetTempPath(), "mailcoded-bench-ingest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _store = new SqliteStore(
            new SqliteStoreOptions
            {
                DatabasePath = Path.Combine(_root, "store.db"),
                DataDirectory = _root,
                PageSize = 8192,
            },
            SystemClock.Instance);

        var accountId = _store.AddAccountAsync(
                new AccountConfig
                {
                    Email = "ingest@mailcoded.test",
                    Provider = ProviderKind.Imap,
                    Auth = AuthKind.Password,
                    SecretRef = "bench:no-credential",
                    Imap = new ImapConfig { Host = "bench.invalid", Port = 143, Security = SecureSocket.None },
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var ids = _store.UpsertFoldersAsync(
                accountId,
                [new RemoteFolder { Path = FolderPath.Create(FolderPath.Inbox), Role = FolderRole.Inbox }],
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        _folderId = ids[0];
    }

    [IterationCleanup]
    public void DropScratchStore()
    {
        _store?.Dispose();
        _store = null;

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark(Description = "Ingest_100k")]
    public int Ingest_100k()
    {
        var store = _store ?? throw new InvalidOperationException("The scratch store was not created.");
        var session = store.BeginBulkIngestAsync(ReferencesThreader.Instance, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        try
        {
            foreach (var batch in _batches)
                session.AddAsync(_folderId, batch, CancellationToken.None).GetAwaiter().GetResult();

            var report = session.CompleteAsync(CancellationToken.None).GetAwaiter().GetResult();
            return report.Inserted;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}
