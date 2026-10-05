using System.Diagnostics;
using System.Globalization;
using Mailcoded.Bench.Corpus;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Store;
using Xunit;

namespace Mailcoded.Core.Tests.Performance;

/// <summary>PERFORMANCE §15.2 read paths on 100,000 Messages seeded by the Bench corpus generator through the
/// bulk ingest path. Opt in with MAILCODED_TEST_PERF=1; the ceilings are regression tripwires, not the targets.</summary>
public sealed class LargeCorpusPerformanceTests(Xunit.ITestOutputHelper output)
{
    private const int CorpusSize = 100_000;
    private const int PageSize = 50;
    private const int Warmup = 5;
    private const int Cursors = 20;

    // Paths above their scaled target today: the multiple widens the ceiling, never the target.
    private const double FtsKnownGap = 3.0;
    private const double ThreadPageKnownGap = 6.0;

    [Fact]
    public void Read_paths_stay_under_their_ceilings_at_100k_messages()
    {
        Assert.SkipUnless(PerfGate.Enabled, PerfGate.SkipReason);

        var root = Path.Combine(Path.GetTempPath(), "mailcoded-test-perf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var options = new CorpusOptions { Size = CorpusSize, CacheDirectory = root };
            var seeding = Stopwatch.StartNew();
            var manifest = CorpusBuilder.EnsureBuilt(options, new TestOutputWriter(output), TestContext.Current.CancellationToken);
            seeding.Stop();

            using var store = CorpusBuilder.OpenExisting(options);

            var ct = TestContext.Current.CancellationToken;
            var account = Assert.Single(store.ListAccounts(ct));
            var folders = store.ListFolders(account.Id, ct);
            Assert.Equal(CorpusSize, folders.Sum(f => f.TotalCount));

            Report($"machine: {Environment.ProcessorCount} logical CPUs, {RuntimeInformationText()}");
            Report($"seed: {CorpusSize.ToString("N0", CultureInfo.InvariantCulture)} Messages in "
                + $"{seeding.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s "
                + $"(seed {options.Seed}, fingerprint {manifest.Fingerprint[..12]}, db {manifest.DatabaseBytes / 1_048_576} MiB)");

            var top = folders.OrderByDescending(f => f.TotalCount).First();
            var results = MeasureEnvelopePages(store, top.Id, top.TotalCount)
                .Concat(MeasureSearch(store, account.Id))
                .Concat(MeasureUnreadBadge(store, account.Id))
                .Concat(MeasureThreads(store, top.Id))
                .ToList();

            foreach (var result in results) Report(result.Line);

            var failures = results.Where(r => r.P95Ms > r.CeilingMs).Select(r => r.Line).ToList();
            Assert.True(failures.Count == 0, "Over the p95 ceiling:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static List<Measurement> MeasureEnvelopePages(SqliteStore store, FolderId folder, int rows)
    {
        var cursors = new List<string?> { null };
        var deepCursors = new List<string?>();
        string? cursor = null;
        var reached = 0;
        var deepFrom = rows / 2;

        while (reached < rows - PageSize)
        {
            var page = store.ListEnvelopes(folder, cursor, 500);
            reached += page.Items.Count;
            cursor = page.NextCursor;
            if (cursor is null) break;
            if (reached >= deepFrom && deepCursors.Count < Cursors) deepCursors.Add(cursor);
        }

        Assert.NotEmpty(deepCursors);
        Assert.Equal(PageSize, store.ListEnvelopes(folder, deepCursors[^1], PageSize).Items.Count);

        return
        [
            Measure("envelope page, first (50 rows)", 200, 10.0, CostShape.Flat, new string?[] { null },
                c => store.ListEnvelopes(folder, c, PageSize).Items.Count),
            Measure($"envelope page, deep (50 rows, after row {deepFrom:N0}+)", 200, 10.0, CostShape.Flat, deepCursors,
                c => store.ListEnvelopes(folder, c, PageSize).Items.Count),
        ];
    }

    private static List<Measurement> MeasureSearch(SqliteStore store, AccountId account)
    {
        string[] queries =
        [
            CorpusVocabulary.CommonTerm,
            "\"" + CorpusVocabulary.Phrase + "\"",
            "latency",
            "subject:budget",
            "請求書",
        ];

        var requests = queries.Select(q => new StoreSearchRequest
        {
            Query = SearchQueryParser.Parse(q).Query,
            AccountId = account,
            Limit = PageSize,
            IncludeSnippet = true,
        }).ToArray();

        foreach (var (query, request) in queries.Zip(requests))
            Assert.True(store.Search(request).Hits.Count > 0, $"'{query}' matched nothing; the corpus did not seed bodies.");

        return [Measure("FTS search (5 queries, incl. phrase and CJK)", 100, 100.0, CostShape.Linear, requests, r => store.Search(r).Hits.Count, FtsKnownGap)];
    }

    private static List<Measurement> MeasureUnreadBadge(SqliteStore store, AccountId account)
    {
        Assert.True(store.ListFolders(account).Sum(f => f.UnreadCount) > 0, "No unread Messages; the badge would time a no-op.");

        return [Measure("unread badge (all Folders)", 500, 5.0, CostShape.Flat, new[] { 0 }, _ => store.ListFolders(account).Sum(f => f.UnreadCount))];
    }

    private static List<Measurement> MeasureThreads(SqliteStore store, FolderId folder)
    {
        var first = store.ListThreads(folder, null, PageSize);
        Assert.Equal(PageSize, first.Items.Count);

        var keys = first.Items.Select(t => t.ThreadKey).OfType<ThreadKey>().Take(Cursors).ToArray();
        Assert.NotEmpty(keys);

        return
        [
            Measure("thread view, page of 50 threads", 100, 20.0, CostShape.Linear, new[] { 0 }, _ => store.ListThreads(folder, null, PageSize).Items.Count, ThreadPageKnownGap),
            Measure("thread view, one thread's Messages", 200, 20.0, CostShape.Flat, keys, k => store.ListThreadMessages(k).Count),
        ];
    }

    private static Measurement Measure<T>(string name, int iterations, double targetMs, CostShape shape, IReadOnlyList<T> inputs, Func<T, int> run, double knownGap = 1.0)
    {
        for (var i = 0; i < Warmup; i++) run(inputs[i % inputs.Count]);

        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var input = inputs[i % inputs.Count];
            var start = Stopwatch.GetTimestamp();
            run(input);
            samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        var summary = LatencySummary.From(samples);
        var scaled = PerfCeiling.ScaledTargetMs(targetMs, shape, CorpusSize);
        var ceiling = PerfCeiling.CeilingMs(targetMs, shape, CorpusSize, knownGap);
        var verdict = summary.P95Ms <= scaled ? "within scaled target" : "ABOVE scaled target";

        return new Measurement(
            summary.P95Ms,
            ceiling,
            string.Create(
                CultureInfo.InvariantCulture,
                $"perf: {name,-52} {summary}  target {targetMs:F0} ms @500k = {scaled:F1} ms scaled, ceiling {ceiling:F0} ms, {verdict}"));
    }

    private void Report(string line) => output.WriteLine(line);

    private static string RuntimeInformationText() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, "
        + System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    private sealed record Measurement(double P95Ms, double CeilingMs, string Line);

    private sealed class TestOutputWriter(Xunit.ITestOutputHelper output) : StringWriter
    {
        public override void WriteLine(string? value) => output.WriteLine(value ?? string.Empty);
    }
}
