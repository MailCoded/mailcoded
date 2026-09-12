using System.Runtime.InteropServices;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Embedding;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

public readonly record struct SemanticHit(LocalMessageId MessageId, float Similarity);

/// <summary>Ranks messages by what they are about rather than which words they use. Brute force by
/// design: at 500k vectors a full int8 scan measured 11.8 ms, well inside the search budget.</summary>
public sealed class SemanticSearch
{
    private readonly SqliteStore _store;
    private readonly TextEmbedder _embedder;
    private int _modelId;

    public SemanticSearch(SqliteStore store, TextEmbedder embedder)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);

        _store = store;
        _embedder = embedder;
    }

    public int Dimensions => _embedder.Dimensions;

    public string Fingerprint => _embedder.Fingerprint;

    /// <summary>False until <see cref="PrepareAsync"/> has registered the model, which needs a write
    /// and so cannot happen in a constructor.</summary>
    public bool IsReady => _modelId != 0;

    /// <summary>Joins a model another process already registered, without writing. Returns false when
    /// none is registered, which also means no vectors exist to search.</summary>
    public bool TryAttach(CancellationToken ct = default)
    {
        if (_modelId != 0) return true;

        _modelId = _store.FindVectorModel(_embedder.Fingerprint, _embedder.Dimensions, ct) ?? 0;
        return _modelId != 0;
    }

    public Task<int> PrepareAsync(CancellationToken ct = default)
    {
        if (_modelId != 0) return Task.FromResult(_modelId);

        return Register(ct);
    }

    /// <summary>The closest messages to <paramref name="text"/>, best first. Empty until the backfill
    /// has written vectors, which is the honest answer rather than a worse ranking.</summary>
    public IReadOnlyList<SemanticHit> Rank(
        string text,
        AccountId? accountId,
        FolderId? folderId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (_modelId == 0) throw new InvalidOperationException("PrepareAsync must run before a semantic search.");
        if (string.IsNullOrWhiteSpace(text)) return [];

        var query = new sbyte[_embedder.Dimensions];
        var queryScale = _embedder.Embed(text, query);

        var best = new List<SemanticHit>(limit + 1);
        var queryVector = query;

        _store.ScanVectors(
            _modelId,
            _embedder.Dimensions,
            accountId,
            folderId,
            (id, scale, vector) =>
            {
                var similarity = Quantizer.Similarity(queryVector, queryScale, MemoryMarshal.Cast<byte, sbyte>(vector), scale);
                if (best.Count == limit && similarity <= best[^1].Similarity) return;

                var at = best.Count;
                while (at > 0 && best[at - 1].Similarity < similarity) at--;

                best.Insert(at, new SemanticHit(id, similarity));
                if (best.Count > limit) best.RemoveAt(best.Count - 1);
            },
            ct);

        return best;
    }

    private async Task<int> Register(CancellationToken ct)
    {
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
}
