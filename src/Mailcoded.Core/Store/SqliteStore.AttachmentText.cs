using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    /// <summary>An empty list is meaningful: it marks the message as read at <paramref name="version"/>.</summary>
    public Task<bool> SetAttachmentTextsAsync(
        LocalMessageId messageId,
        IReadOnlyList<AttachmentTextEntry> entries,
        int version,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return WriteAsync(context =>
        {
            var session = context.Session;

            var exists = session
                .Prepare("SELECT 1 FROM messages WHERE id = $id", "$id")
                .SetInt(0, messageId.Value)
                .ExecuteNullableInt64() is not null;
            if (!exists) return false;

            AttachmentIndex.Replace(session, messageId.Value, entries, version);
            return true;
        }, ct);
    }

    public IReadOnlyList<AttachmentTextEntry> GetAttachmentTexts(LocalMessageId messageId, CancellationToken ct = default) =>
        Read<IReadOnlyList<AttachmentTextEntry>>(session =>
        {
            var rows = new List<AttachmentTextEntry>();
            using var reader = session
                .Prepare(
                    "SELECT idx, mime_type, file_name, text, truncated FROM attachment_text WHERE message_id = $id ORDER BY idx",
                    "$id")
                .SetInt(0, messageId.Value)
                .ExecuteReader();

            while (reader.Read())
            {
                rows.Add(new AttachmentTextEntry
                {
                    Index = (int)reader.GetInt64(0),
                    MimeType = reader.GetString(1),
                    FileName = Db.Str(reader, 2),
                    Text = reader.GetString(3),
                    Truncated = Db.Bool(reader, 4),
                });
            }

            return rows;
        }, ct);

    public IReadOnlyList<AttachmentIndexWork> NextAttachmentIndexWork(int version, int limit, CancellationToken ct = default) =>
        Read<IReadOnlyList<AttachmentIndexWork>>(session =>
        {
            var work = new List<AttachmentIndexWork>(Math.Max(limit, 0));

            using var reader = session
                .Prepare(PendingSql("m.id, m.blob_id, m.date_utc") + " ORDER BY m.id LIMIT $limit", "$version", "$limit")
                .SetInt(0, version)
                .SetInt(1, Math.Max(limit, 0))
                .ExecuteReader();

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                work.Add(new AttachmentIndexWork
                {
                    MessageId = new LocalMessageId(reader.GetInt64(0)),
                    BlobId = new BlobId(reader.GetInt64(1)),
                    DateUtc = FromUnixMs(Db.Int(reader, 2)),
                });
            }

            return work;
        }, ct);

    public long CountPendingAttachmentIndex(int version, CancellationToken ct = default) =>
        Read(session => session
            .Prepare(PendingSql("COUNT(*)"), "$version")
            .SetInt(0, version)
            .ExecuteInt64(), ct);

    private static string PendingSql(string columns) =>
        "SELECT " + columns + " FROM messages m LEFT JOIN attachment_index i ON i.message_id = m.id "
        + "WHERE m.body_fetched = 1 AND m.has_attachments = 1 AND m.blob_id IS NOT NULL "
        + "AND (i.version IS NULL OR i.version < $version)";
}
