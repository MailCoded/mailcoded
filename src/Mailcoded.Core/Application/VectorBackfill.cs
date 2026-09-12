using System.Diagnostics;
using Mailcoded.Core.Embedding;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

public sealed record VectorBackfillOptions
{
    /// <summary>Rows per transaction. Larger commits less often; a kill loses at most this much work.</summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>How long to wait after finding nothing to do.</summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait while a bulk-ingest window holds the writer.</summary>
    public TimeSpan BusyDelay { get; init; } = TimeSpan.FromSeconds(5);

    public static readonly VectorBackfillOptions Default = new();
}

public sealed record VectorBackfillReport
{
    public int Embedded { get; init; }

    public long Remaining { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Fills in the vectors for messages that already have a body. The queue is the absence of an
/// <c>msg_vec</c> row, so this is resumable across restarts with no state of its own.
/// </summary>
/// <remarks>
/// Encoding happens between the read and the write, never inside either, so the SQLite writer thread
/// is never held for the tens of milliseconds a message costs to encode.
/// </remarks>
public sealed class VectorBackfill
{
    private readonly SqliteStore _store;
    private readonly TextEmbedder _embedder;
    private readonly VectorBackfillOptions _options;
    private int _modelId;

    public VectorBackfill(SqliteStore store, TextEmbedder embedder, VectorBackfillOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);

        _store = store;
        _embedder = embedder;
        _options = options ?? VectorBackfillOptions.Default;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.BatchSize);
    }

    /// <summary>Registers the model and returns its row id. Every other call needs this first.</summary>
    public async Task<int> PrepareAsync(CancellationToken ct = default)
    {
        if (_modelId != 0) return _modelId;

        _modelId = await _store.EnsureVectorModelAsync(
            new VectorModel
            {
                Fingerprint = _embedder.Fingerprint,
                Name = _embedder.Name,
                Dimensions = _embedder.Dimensions,
                Pooling = _embedder.Pooling,
            },
            ct).ConfigureAwait(false);

        return _modelId;
    }

    public async Task<long> RemainingAsync(CancellationToken ct = default)
    {
        var modelId = await PrepareAsync(ct).ConfigureAwait(false);
        return _store.CountPendingVectors(modelId, ct);
    }

    /// <summary>Embeds one batch. Returns a report whose <c>Embedded</c> is zero when there was
    /// nothing to do or the store is busy ingesting.</summary>
    public async Task<VectorBackfillReport> RunBatchAsync(CancellationToken ct = default)
    {
        var elapsed = Stopwatch.StartNew();

        // Before any write, not after: a write issued inside a bulk window joins that window's
        // transaction rather than its own, and is discarded if the window rolls back.
        if (_store.IsBulkIngesting)
        {
            return new VectorBackfillReport
            {
                Remaining = _modelId == 0 ? 0 : _store.CountPendingVectors(_modelId, ct),
                Elapsed = elapsed.Elapsed,
            };
        }

        var modelId = await PrepareAsync(ct).ConfigureAwait(false);
        var work = _store.NextVectorWork(modelId, _options.BatchSize, ct);
        if (work.Count == 0) return new VectorBackfillReport { Elapsed = elapsed.Elapsed };

        var vectors = new List<MessageVector>(work.Count);
        var quantized = new sbyte[_embedder.Dimensions];

        foreach (var pending in work)
        {
            ct.ThrowIfCancellationRequested();

            var scale = _embedder.Embed(TextEmbedder.Compose(pending.Subject, pending.BodyText), quantized);
            vectors.Add(new MessageVector(pending.MessageId, scale, Quantizer.ToBytes(quantized)));
        }

        await _store.SetMessageVectorsAsync(modelId, vectors, ct).ConfigureAwait(false);

        return new VectorBackfillReport
        {
            Embedded = vectors.Count,
            Remaining = _store.CountPendingVectors(modelId, ct),
            Elapsed = elapsed.Elapsed,
        };
    }

    /// <summary>Runs until cancelled, waiting rather than spinning when there is nothing to do.</summary>
    public async Task RunAsync(IProgress<VectorBackfillReport>? progress = null, CancellationToken ct = default)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var busy = _store.IsBulkIngesting;
                var report = await RunBatchAsync(ct).ConfigureAwait(false);
                progress?.Report(report);

                if (report.Embedded > 0) continue;
                await Task.Delay(busy ? _options.BusyDelay : _options.IdleDelay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
