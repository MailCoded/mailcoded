using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    /// <summary>
    /// The lazy-body upgrade from SPEC §5.5 step 7: stores the plaintext extraction, links the raw
    /// blob, flips <c>body_fetched</c>, and rewrites both FTS rows — all in one transaction.
    /// </summary>
    public Task SetBodyTextAsync(
        LocalMessageId messageId,
        string bodyText,
        BlobId? blobId = null,
        bool? hasAttachments = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bodyText);

        return WriteAsync(context =>
        {
            var session = context.Session;

            string? subject;
            string? from;
            string? to;
            string oldBody;

            using (var reader = session
                       .Prepare(
                           "SELECT m.subject, m.from_addr, m.to_addrs, COALESCE(b.text, '') "
                           + "FROM messages m LEFT JOIN body_text b ON b.message_id = m.id WHERE m.id = $id",
                           "$id")
                       .SetInt(0, messageId.Value)
                       .ExecuteReader())
            {
                if (!reader.Read()) return false;
                subject = Db.Str(reader, 0);
                from = Db.Str(reader, 1);
                to = Db.Str(reader, 2);
                oldBody = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            }

            session
                .Prepare(
                    "INSERT INTO body_text (message_id, text) VALUES ($id,$text) "
                    + "ON CONFLICT(message_id) DO UPDATE SET text = excluded.text",
                    "$id", "$text")
                .SetInt(0, messageId.Value)
                .SetText(1, bodyText)
                .Execute();

            session
                .Prepare(
                    "UPDATE messages SET body_fetched = 1, blob_id = COALESCE($blob, blob_id), "
                    + "has_attachments = COALESCE($hasatt, has_attachments) WHERE id = $id",
                    "$blob", "$hasatt", "$id")
                .SetIntOrNull(0, blobId is { } blob ? (long?)blob.Value : null)
                .SetIntOrNull(1, hasAttachments is { } attachments ? (long?)(attachments ? 1 : 0) : null)
                .SetInt(2, messageId.Value)
                .Execute();

            if (!string.Equals(oldBody, bodyText, StringComparison.Ordinal))
                Fts.Replace(session, messageId.Value, subject, oldBody, from, to, subject, bodyText, from, to);

            return true;
        }, ct);
    }

    public string? GetBodyText(LocalMessageId messageId, CancellationToken ct = default) =>
        Read<string?>(session => session
            .Prepare("SELECT text FROM body_text WHERE message_id = $id", "$id")
            .SetInt(0, messageId.Value)
            .ExecuteString(), ct);

    public bool IsBodyFetched(LocalMessageId messageId, CancellationToken ct = default) =>
        Read(session => session
            .Prepare("SELECT body_fetched FROM messages WHERE id = $id", "$id")
            .SetInt(0, messageId.Value)
            .ExecuteInt64() != 0, ct);
}
