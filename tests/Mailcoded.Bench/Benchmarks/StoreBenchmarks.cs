using BenchmarkDotNet.Attributes;
using Mailcoded.Bench.Corpus;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Store;

namespace Mailcoded.Bench.Benchmarks;

/// <summary>The read-side gates of PERFORMANCE §15.8, measured on the seeded corpus.</summary>
[MemoryDiagnoser]
[RunOncePerIteration]
public class StoreBenchmarks
{
    public const int DeepPageNumber = 5_000;
    public const int PageSize = 50;

    private CorpusOptions _options = new();
    private SqliteStore? _store;
    private AccountId _accountId;
    private FolderId _topFolder;
    private string? _deepCursor;

    private StoreSearchRequest _commonTerm = new() { Query = SearchQuery.Empty };
    private StoreSearchRequest _phrase = new() { Query = SearchQuery.Empty };
    private StoreSearchRequest _cjk = new() { Query = SearchQuery.Empty };

    /// <summary>How deep the cursor actually reached; a smoke corpus cannot hold 5,000 pages.</summary>
    public int DeepPageRowsReached { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        _options = CorpusOptions.FromEnvironment();
        CorpusBuilder.EnsureBuilt(_options, Console.Out, CancellationToken.None);

        _store = CorpusBuilder.OpenExisting(_options);

        var accounts = _store.ListAccounts();
        if (accounts.Count == 0) throw new InvalidOperationException("The corpus store holds no account.");
        _accountId = accounts[0].Id;

        _topFolder = TopFolder(_store, _accountId);
        _deepCursor = SeekDeepCursor(_store, _topFolder, DeepPageNumber * PageSize, out var reached);
        DeepPageRowsReached = reached;

        _commonTerm = Request(CorpusVocabulary.CommonTerm);
        _phrase = Request("\"" + CorpusVocabulary.Phrase + "\"");
        _cjk = Request("請求書");

        // A query that matches nothing would time a no-op and report a green gate.
        RequireHits(nameof(FtsSearch_CommonTerm), _commonTerm);
        RequireHits(nameof(FtsSearch_Phrase), _phrase);
        RequireHits(nameof(FtsSearch_Cjk), _cjk);

        if (reached == 0) throw new InvalidOperationException("The corpus folder is empty; there is no page to seek to.");

        Console.Out.WriteLine(
            $"bench: corpus={_options.Key} deepCursorRows={reached} topFolder={_topFolder.Value}");
    }

    private void RequireHits(string name, StoreSearchRequest request)
    {
        var result = Store.Search(request);
        if (result.Hits.Count == 0)
            throw new InvalidOperationException($"{name} matched nothing in the corpus (route {result.Route}).");
    }

    [GlobalCleanup]
    public void Cleanup() => _store?.Dispose();

    [Benchmark(Description = "EnvelopePage_Deep")]
    public int EnvelopePage_Deep() =>
        Store.ListEnvelopes(_topFolder, _deepCursor, PageSize).Items.Count;

    [Benchmark(Description = "FtsSearch_CommonTerm")]
    public int FtsSearch_CommonTerm() => Store.Search(_commonTerm).Hits.Count;

    [Benchmark(Description = "FtsSearch_Phrase")]
    public int FtsSearch_Phrase() => Store.Search(_phrase).Hits.Count;

    [Benchmark(Description = "FtsSearch_Cjk")]
    public int FtsSearch_Cjk() => Store.Search(_cjk).Hits.Count;

    [Benchmark(Description = "UnreadBadge_AllFolders")]
    public int UnreadBadge_AllFolders()
    {
        var total = 0;
        foreach (var folder in Store.ListFolders(_accountId)) total += folder.UnreadCount;
        return total;
    }

    [Benchmark(Description = "ThreadView_TopFolder")]
    public int ThreadView_TopFolder() => Store.ListThreads(_topFolder, null, PageSize).Items.Count;

    private SqliteStore Store =>
        _store ?? throw new InvalidOperationException("The corpus store was not opened; GlobalSetup did not run.");

    private StoreSearchRequest Request(string query) => new()
    {
        Query = SearchQueryParser.Parse(query).Query,
        AccountId = _accountId,
        Limit = PageSize,
        IncludeSnippet = true,
    };

    private static FolderId TopFolder(SqliteStore store, AccountId accountId)
    {
        var best = FolderId.None;
        var most = -1;

        foreach (var folder in store.ListFolders(accountId))
        {
            if (folder.TotalCount <= most) continue;
            most = folder.TotalCount;
            best = folder.Id;
        }

        if (best.IsNone) throw new InvalidOperationException("The corpus store holds no folder.");
        return best;
    }

    /// <summary>Walks to the requested depth once in setup, so the benchmark measures a single keyset
    /// seek — the operation PERFORMANCE §15.4 says stays flat at any depth.</summary>
    private static string? SeekDeepCursor(SqliteStore store, FolderId folderId, int targetRows, out int reached)
    {
        const int stride = 500;

        string? cursor = null;
        reached = 0;

        while (reached < targetRows)
        {
            var page = store.ListEnvelopes(folderId, cursor, Math.Min(stride, targetRows - reached));
            reached += page.Items.Count;
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        return cursor;
    }
}
