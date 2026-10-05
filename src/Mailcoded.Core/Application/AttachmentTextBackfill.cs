using System.Diagnostics;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

public sealed record AttachmentTextBackfillOptions
{
    public int BatchSize { get; init; } = 16;

    public long MaxRawBytes { get; init; } = 64L * 1024 * 1024;

    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan BusyDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan PacingDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public static readonly AttachmentTextBackfillOptions Default = new();
}

public sealed record AttachmentTextBackfillReport
{
    public int Processed { get; init; }

    public int WithText { get; init; }

    public long Remaining { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>Reads the stored raw message of already-fetched mail to index its attachments; the queue is the absence of a current <c>attachment_index</c> row.</summary>
public sealed class AttachmentTextBackfill
{
    private readonly SqliteStore _store;
    private readonly MessageParser _parser;
    private readonly AttachmentTextBackfillOptions _options;

    public AttachmentTextBackfill(SqliteStore store, MessageParser parser, AttachmentTextBackfillOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parser);

        _store = store;
        _parser = parser;
        _options = options ?? AttachmentTextBackfillOptions.Default;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.BatchSize);
    }

    public long Remaining(CancellationToken ct = default) =>
        _store.CountPendingAttachmentIndex(AttachmentTextIndexing.ExtractorVersion, ct);

    // A write inside a bulk-ingest window would join that window's transaction, so a busy store reports zero processed.
    public async Task<AttachmentTextBackfillReport> RunBatchAsync(CancellationToken ct = default)
    {
        var elapsed = Stopwatch.StartNew();

        if (_store.IsBulkIngesting) return new AttachmentTextBackfillReport { Remaining = Remaining(ct), Elapsed = elapsed.Elapsed };

        var work = _store.NextAttachmentIndexWork(AttachmentTextIndexing.ExtractorVersion, _options.BatchSize, ct);
        var withText = 0;

        foreach (var item in work)
        {
            ct.ThrowIfCancellationRequested();

            var entries = Read(item, ct);
            await _store
                .SetAttachmentTextsAsync(item.MessageId, entries, AttachmentTextIndexing.ExtractorVersion, ct)
                .ConfigureAwait(false);

            if (entries.Count > 0) withText++;
        }

        return new AttachmentTextBackfillReport
        {
            Processed = work.Count,
            WithText = withText,
            Remaining = work.Count == 0 ? 0 : Remaining(ct),
            Elapsed = elapsed.Elapsed,
        };
    }

    private IReadOnlyList<AttachmentTextEntry> Read(AttachmentIndexWork item, CancellationToken ct)
    {
        try
        {
            if (_store.GetBlob(item.BlobId, ct) is not { } reference || reference.Size > _options.MaxRawBytes) return [];

            using var raw = _store.OpenBlob(item.BlobId, ct);
            if (raw is null) return [];

            return AttachmentTextIndexing.EntriesOf(_parser.Parse(raw, item.DateUtc, ct));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is StoreException or FormatException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Returns how many messages are still queued when the budget ends.</summary>
    public async Task<long> RunWithinAsync(TimeSpan budget, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < budget)
        {
            var report = await RunBatchAsync(ct).ConfigureAwait(false);
            if (report.Processed == 0) return report.Remaining;
        }

        return Remaining(ct);
    }

    public async Task RunAsync(IProgress<AttachmentTextBackfillReport>? progress = null, CancellationToken ct = default)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var busy = _store.IsBulkIngesting;
                var report = await RunBatchAsync(ct).ConfigureAwait(false);
                progress?.Report(report);

                var delay = report.Processed > 0 ? _options.PacingDelay : busy ? _options.BusyDelay : _options.IdleDelay;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
