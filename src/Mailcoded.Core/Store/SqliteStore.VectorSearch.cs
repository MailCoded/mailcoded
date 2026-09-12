using System.Globalization;
using System.Text;
using Mailcoded.Core.Domain.Primitives;
using ParsedQuery = Mailcoded.Core.Domain.Search.SearchQuery;

namespace Mailcoded.Core.Store;

/// <summary>Receives one stored vector. The span is only valid for the call: copy what you keep.</summary>
public delegate void VectorVisitor(LocalMessageId messageId, float scale, ReadOnlySpan<byte> vector);

public sealed partial class SqliteStore
{
    /// <summary>Walks every vector for one model, newest first. At 500k messages the set is ~185 MiB,
    /// which is why it arrives one row at a time rather than as a list.</summary>
    public int ScanVectors(
        int modelId,
        int dimensions,
        AccountId? accountId,
        FolderId? folderId,
        VectorVisitor visit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentNullException.ThrowIfNull(visit);

        return Read(session =>
        {
            var sql = new StringBuilder(256);
            sql.Append("SELECT v.message_id, v.scale, v.vec FROM msg_vec v ");
            sql.Append("JOIN messages m ON m.id = v.message_id ");
            if (accountId is not null) sql.Append("JOIN folders f ON f.id = m.folder_id ");
            sql.Append("WHERE v.model_id = $model");
            if (accountId is not null) sql.Append(" AND f.account_id = $account");
            if (folderId is not null) sql.Append(" AND m.folder_id = $folder");
            sql.Append(" ORDER BY m.date_utc DESC, m.id DESC");

            var names = new List<string>(3) { "$model" };
            if (accountId is not null) names.Add("$account");
            if (folderId is not null) names.Add("$folder");

            var statement = session.Prepare(sql.ToString(), [.. names]);
            var ordinal = 0;
            statement.SetInt(ordinal++, modelId);
            if (accountId is { } account) statement.SetInt(ordinal++, account.Value);
            if (folderId is { } folder) statement.SetInt(ordinal, folder.Value);

            var buffer = new byte[dimensions];
            var seen = 0;

            using var reader = statement.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.IsDBNull(2)) continue;

                // A row of another width cannot be compared with anything; skipping it beats
                // throwing, which would take down a search over every other message.
                var read = (int)reader.GetBytes(2, 0, buffer, 0, dimensions);
                if (read != dimensions) continue;

                visit(new LocalMessageId(reader.GetInt64(0)), (float)reader.GetDouble(1), buffer);
                seen++;
            }

            return seen;
        }, ct);
    }

    /// <summary>Hydrates hits for ids the semantic stage chose, in that order. <c>message_id</c> is
    /// the rowid alias, so each of these is a point lookup.</summary>
    public IReadOnlyList<StoreSearchHit> SearchHitsByIds(
        IReadOnlyList<LocalMessageId> ids,
        ParsedQuery? query = null,
        bool includeSnippet = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];

        return Read<IReadOnlyList<StoreSearchHit>>(session =>
        {
            var needles = query is null ? [] : SnippetNeedles(query);

            var sql = new StringBuilder(320);
            sql.Append("SELECT m.id, m.folder_id, m.subject, m.from_addr, m.date_utc, m.flags");
            sql.Append(includeSnippet ? ", substr(COALESCE(b.text, ''), 1, 4000)" : ", NULL");
            sql.Append(", m.has_attachments, m.body_fetched, m.size FROM messages m");
            if (includeSnippet) sql.Append(" LEFT JOIN body_text b ON b.message_id = m.id");
            sql.Append(" WHERE m.id IN (");

            var names = new string[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                names[i] = string.Create(CultureInfo.InvariantCulture, $"$id{i}");
                if (i > 0) sql.Append(',');
                sql.Append(names[i]);
            }

            sql.Append(')');

            var statement = session.Prepare(sql.ToString(), names);
            for (var i = 0; i < ids.Count; i++) statement.SetInt(i, ids[i].Value);

            var found = new Dictionary<long, StoreSearchHit>(ids.Count);

            using (var reader = statement.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();

                    var id = reader.GetInt64(0);
                    var subject = Db.Str(reader, 2);

                    found[id] = new StoreSearchHit
                    {
                        Id = new LocalMessageId(id),
                        FolderId = new FolderId(reader.GetInt64(1)),
                        Subject = subject,
                        From = Db.Str(reader, 3),
                        DateUtc = FromUnixMs(Db.Int(reader, 4)),
                        Flags = (MessageFlags)(int)Db.Int(reader, 5),
                        Snippet = includeSnippet ? BuildSnippet(Db.Str(reader, 6) ?? subject, needles) : null,
                        HasAttachments = Db.Int(reader, 7) != 0,
                        BodyFetched = Db.Int(reader, 8) != 0,
                        Size = Db.Int(reader, 9),
                    };
                }
            }

            // The caller's order is the ranking; SQL returns rows in whatever order it likes.
            var hits = new List<StoreSearchHit>(found.Count);
            foreach (var id in ids)
                if (found.TryGetValue(id.Value, out var hit)) hits.Add(hit);

            return hits;
        }, ct);
    }
}
