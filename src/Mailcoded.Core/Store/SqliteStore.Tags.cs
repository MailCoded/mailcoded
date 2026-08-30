using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Providers;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    /// <summary>
    /// Applies a tag delta and returns the resulting tag set. Tags are local state: unlike flags,
    /// the local side wins (invariant 9), so nothing here consults the server.
    /// </summary>
    public Task<IReadOnlyList<Tag>> ApplyTagsAsync(LocalMessageId messageId, TagDelta delta, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delta);

        return WriteAsync(context =>
        {
            var session = context.Session;

            var exists = session
                .Prepare("SELECT 1 FROM messages WHERE id = $id", "$id")
                .SetInt(0, messageId.Value)
                .ExecuteNullableInt64();
            if (exists is null)
                throw new StoreException(FailureCategory.NotFound, $"No message with id {messageId.Value}.");

            foreach (var tag in delta.Remove)
            {
                session
                    .Prepare("DELETE FROM tags WHERE message_id = $id AND tag = $tag", "$id", "$tag")
                    .SetInt(0, messageId.Value)
                    .SetText(1, tag.Value)
                    .Execute();
            }

            foreach (var tag in delta.Add)
            {
                session
                    .Prepare("INSERT INTO tags (message_id, tag) VALUES ($id,$tag) ON CONFLICT DO NOTHING", "$id", "$tag")
                    .SetInt(0, messageId.Value)
                    .SetText(1, tag.Value)
                    .Execute();
            }

            return ReadTagsOn(session, messageId.Value);
        }, ct);
    }

    /// <summary>Replaces the whole tag set for one message.</summary>
    public Task<IReadOnlyList<Tag>> SetTagsAsync(LocalMessageId messageId, IReadOnlyList<Tag> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);

        return WriteAsync(context =>
        {
            var session = context.Session;

            session
                .Prepare("DELETE FROM tags WHERE message_id = $id", "$id")
                .SetInt(0, messageId.Value)
                .Execute();

            foreach (var tag in tags)
            {
                session
                    .Prepare("INSERT INTO tags (message_id, tag) VALUES ($id,$tag) ON CONFLICT DO NOTHING", "$id", "$tag")
                    .SetInt(0, messageId.Value)
                    .SetText(1, tag.Value)
                    .Execute();
            }

            return ReadTagsOn(session, messageId.Value);
        }, ct);
    }

    public IReadOnlyList<Tag> GetTags(LocalMessageId messageId, CancellationToken ct = default) =>
        Read(session => ReadTagsOn(session, messageId.Value), ct);

    /// <summary>Tags for a page of messages, so a list render costs one query rather than fifty.</summary>
    public IReadOnlyDictionary<LocalMessageId, IReadOnlyList<Tag>> GetTagsFor(
        IReadOnlyList<LocalMessageId> messageIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var result = new Dictionary<LocalMessageId, IReadOnlyList<Tag>>();
        if (messageIds.Count == 0) return result;

        return Read(session =>
        {
            var statement = session.Prepare(
                "SELECT tag FROM tags WHERE message_id = $id ORDER BY tag", "$id");

            foreach (var id in messageIds)
            {
                ct.ThrowIfCancellationRequested();
                var tags = new List<Tag>();
                using (var reader = statement.SetInt(0, id.Value).ExecuteReader())
                {
                    while (reader.Read())
                        if (Tag.TryParse(reader.GetString(0), out var tag))
                            tags.Add(tag);
                }
                result[id] = tags;
            }

            return (IReadOnlyDictionary<LocalMessageId, IReadOnlyList<Tag>>)result;
        }, ct);
    }

    /// <summary>Ids carrying a tag, newest first. Feeds saved searches such as <c>tag:unread</c>.</summary>
    public IReadOnlyList<LocalMessageId> FindByTag(Tag tag, int limit = 200, CancellationToken ct = default)
    {
        var pageSize = NormalizeLimit(limit, 5000);

        return Read(session =>
        {
            var ids = new List<LocalMessageId>();
            using var reader = session
                .Prepare(
                    "SELECT t.message_id FROM tags t JOIN messages m ON m.id = t.message_id "
                    + "WHERE t.tag = $tag ORDER BY m.date_utc DESC, m.id DESC LIMIT $limit",
                    "$tag", "$limit")
                .SetText(0, tag.Value)
                .SetInt(1, pageSize)
                .ExecuteReader();

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                ids.Add(new LocalMessageId(reader.GetInt64(0)));
            }
            return (IReadOnlyList<LocalMessageId>)ids;
        }, ct);
    }

    private static IReadOnlyList<Tag> ReadTagsOn(DbSession session, long messageId)
    {
        var tags = new List<Tag>();
        using var reader = session
            .Prepare("SELECT tag FROM tags WHERE message_id = $id ORDER BY tag", "$id")
            .SetInt(0, messageId)
            .ExecuteReader();

        while (reader.Read())
            if (Tag.TryParse(reader.GetString(0), out var tag))
                tags.Add(tag);

        return tags;
    }
}
