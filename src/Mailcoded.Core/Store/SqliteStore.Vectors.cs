using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    /// <summary>True while a bulk-ingest window is open. The backfill worker reads this and stands
    /// down: that window drops indexes and runs <c>synchronous=OFF</c>, and a second writer competing
    /// for the same thread would stretch it for no gain.</summary>
    public bool IsBulkIngesting => Volatile.Read(ref _bulkSessions) != 0;

    /// <summary>Resolves a model fingerprint to its row id, registering it the first time it is seen.
    /// Two models are the same model only if their fingerprints match.</summary>
    public Task<int> EnsureVectorModelAsync(VectorModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Pooling);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(model.Dimensions);

        return WriteAsync(context =>
        {
            var session = context.Session;

            var existing = session
                .Prepare("SELECT id, dim, pooling FROM vec_model WHERE fingerprint = $fp", "$fp")
                .SetText(0, model.Fingerprint);

            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    var id = (int)reader.GetInt64(0);
                    var dimensions = (int)reader.GetInt64(1);
                    var pooling = reader.GetString(2);

                    // The fingerprint is the identity, so a mismatch here means two different models
                    // were given the same one — every vector already stored under it is suspect.
                    if (dimensions != model.Dimensions || !string.Equals(pooling, model.Pooling, StringComparison.Ordinal))
                    {
                        throw new StoreException(
                            FailureCategory.Protocol,
                            "A different model is already registered under this fingerprint.");
                    }

                    return id;
                }
            }

            session
                .Prepare(
                    "INSERT INTO vec_model (fingerprint, name, dim, pooling, created_utc) "
                    + "VALUES ($fp,$name,$dim,$pooling,$now)",
                    "$fp", "$name", "$dim", "$pooling", "$now")
                .SetText(0, model.Fingerprint)
                .SetText(1, model.Name)
                .SetInt(2, model.Dimensions)
                .SetText(3, model.Pooling)
                .SetInt(4, ToUnixMs(_clock.UtcNow))
                .Execute();

            return (int)session.ExecScalarInt64("SELECT last_insert_rowid()");
        }, ct);
    }

    /// <summary>The id of an already-registered model, or null. Read-only, so a short-lived process
    /// can join in without writing.</summary>
    public int? FindVectorModel(string fingerprint, int dimensions, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        return Read<int?>(session =>
        {
            using var reader = session
                .Prepare("SELECT id, dim FROM vec_model WHERE fingerprint = $fp", "$fp")
                .SetText(0, fingerprint)
                .ExecuteReader();

            if (!reader.Read()) return null;
            return (int)reader.GetInt64(1) == dimensions ? (int)reader.GetInt64(0) : null;
        }, ct);
    }

    /// <summary>The next messages with a body but no vector for this model. The absence of a row is
    /// the queue, so there is no pending table to drift out of step with reality.</summary>
    public IReadOnlyList<PendingVector> NextVectorWork(int modelId, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return Read<IReadOnlyList<PendingVector>>(session =>
        {
            var work = new List<PendingVector>(Math.Min(limit, 1024));

            using var reader = session
                .Prepare(
                    "SELECT b.message_id, COALESCE(m.subject, ''), b.text "
                    + "FROM body_text b "
                    + "JOIN messages m ON m.id = b.message_id "
                    + "LEFT JOIN msg_vec v ON v.message_id = b.message_id AND v.model_id = $model "
                    + "WHERE v.message_id IS NULL "
                    + "ORDER BY b.message_id DESC LIMIT $limit",
                    "$model", "$limit")
                .SetInt(0, modelId)
                .SetInt(1, limit)
                .ExecuteReader();

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                work.Add(new PendingVector(
                    new LocalMessageId(reader.GetInt64(0)),
                    reader.GetString(1),
                    reader.GetString(2)));
            }

            return work;
        }, ct);
    }

    public long CountPendingVectors(int modelId, CancellationToken ct = default) =>
        Read(session => session
            .Prepare(
                "SELECT COUNT(*) FROM body_text b "
                + "LEFT JOIN msg_vec v ON v.message_id = b.message_id AND v.model_id = $model "
                + "WHERE v.message_id IS NULL",
                "$model")
            .SetInt(0, modelId)
            .ExecuteInt64(), ct);

    /// <summary>Writes one batch of vectors in a single transaction. Replaces by message id, so a
    /// row left behind by a superseded model is overwritten rather than accumulating beside it.</summary>
    public Task<int> SetMessageVectorsAsync(
        int modelId,
        IReadOnlyList<MessageVector> vectors,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0) return Task.FromResult(0);

        return WriteAsync(context =>
        {
            var session = context.Session;
            var written = 0;

            foreach (var vector in vectors)
            {
                ct.ThrowIfCancellationRequested();
                if (vector.Vector.Length == 0) throw new ArgumentException("A vector cannot be empty.", nameof(vectors));

                written += session
                    .Prepare(
                        "INSERT INTO msg_vec (message_id, model_id, scale, vec) VALUES ($id,$model,$scale,$vec) "
                        + "ON CONFLICT(message_id) DO UPDATE SET "
                        + "model_id = excluded.model_id, scale = excluded.scale, vec = excluded.vec",
                        "$id", "$model", "$scale", "$vec")
                    .SetInt(0, vector.MessageId.Value)
                    .SetInt(1, modelId)
                    .SetReal(2, vector.Scale)
                    .SetBlob(3, vector.Vector)
                    .Execute();
            }

            return written;
        }, ct);
    }

    /// <summary>Every stored vector for one model, for the brute-force scan. Measured at 11.8 ms p95
    /// over 500k int8 vectors, which is why there is no approximate index here.</summary>
    public IReadOnlyList<StoredVector> ReadVectors(int modelId, int dimensions, CancellationToken ct = default)
    {
        var vectors = new List<StoredVector>();
        ScanVectors(
            modelId,
            dimensions,
            null,
            null,
            (id, scale, vector) => vectors.Add(new StoredVector(id, scale, vector.ToArray())),
            ct);

        return vectors;
    }

    /// <summary>Forgets every vector, for a model change or a plain reclaim. Bodies are untouched,
    /// so the backfill can rebuild all of it.</summary>
    public Task<int> ClearMessageVectorsAsync(int? modelId = null, CancellationToken ct = default) =>
        WriteAsync(context =>
        {
            var session = context.Session;
            return modelId is { } id
                ? session.Prepare("DELETE FROM msg_vec WHERE model_id = $model", "$model").SetInt(0, id).Execute()
                : session.Prepare("DELETE FROM msg_vec").Execute();
        }, ct);
}
