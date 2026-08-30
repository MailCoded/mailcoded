using System.Text;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const int SnippetContextChars = 48;
    private const int SnippetSourceLimit = 4000;

    /// <summary>
    /// Routes a query to the right index per PERFORMANCE §15.3: Latin to <c>msg_fts</c> with bm25
    /// ranking, CJK of three runes or more to the trigram index, shorter CJK to a LIKE scan.
    /// </summary>
    /// <remarks>
    /// Snippets are built in managed code over the returned page only. The two FTS5 tables are
    /// contentless, so SQLite's own <c>snippet()</c> has no text to work from.
    /// </remarks>
    public SearchResult Search(SearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var route = SearchText.Route(query.Text);
        if (route == SearchRoute.None) return SearchResult.Empty(SearchRoute.None);

        // Trigram and LIKE results have no meaningful relevance order, so they always page by date.
        var order = route == SearchRoute.Fts ? query.Order : SearchOrder.Date;
        var limit = NormalizeLimit(query.Limit);

        var matchExpression = string.Empty;
        var likePattern = string.Empty;

        if (route is SearchRoute.Fts or SearchRoute.Cjk)
        {
            matchExpression = SearchText.ToMatchExpression(query.Text);
            if (matchExpression.Length == 0) return SearchResult.Empty(route);
        }
        else
        {
            likePattern = SearchText.ToLikePattern(query.Text);
        }

        var offset = 0;
        var seeking = false;
        long cursorDate = 0;
        long cursorId = 0;

        if (order == SearchOrder.Relevance)
        {
            Cursors.TryDecodeOffset(query.Cursor, out offset);
            if (offset >= _options.MaxSearchOffset)
                return new SearchResult { Hits = [], Route = route, Truncated = true };
        }
        else
        {
            seeking = Cursors.TryDecodeKeyset(query.Cursor, out cursorDate, out cursorId);
        }

        var withSnippet = query.IncludeSnippet;
        var names = new List<string>(8);
        var sql = BuildSearchSql(
            route, withSnippet,
            query.AccountId is not null, query.FolderId is not null, query.Tag is not null,
            order == SearchOrder.Relevance, seeking, names);

        return Read(session =>
        {
            var statement = session.Prepare(sql, names.ToArray());
            var ordinal = 0;

            statement.SetText(ordinal++, route == SearchRoute.Like ? likePattern : matchExpression);
            if (query.AccountId is { } accountId) statement.SetInt(ordinal++, accountId.Value);
            if (query.FolderId is { } folderId) statement.SetInt(ordinal++, folderId.Value);
            if (query.Tag is { } tag) statement.SetText(ordinal++, tag.Value);
            if (seeking)
            {
                statement.SetInt(ordinal++, cursorDate);
                statement.SetInt(ordinal++, cursorId);
            }
            statement.SetInt(ordinal++, limit + 1);
            if (order == SearchOrder.Relevance) statement.SetInt(ordinal, offset);

            var hits = new List<SearchHit>(limit);
            var lastDate = 0L;
            var lastId = 0L;
            var more = false;

            using (var reader = statement.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (hits.Count == limit) { more = true; break; }

                    lastId = reader.GetInt64(0);
                    lastDate = Db.Int(reader, 4);
                    var subject = Db.Str(reader, 2);

                    hits.Add(new SearchHit
                    {
                        Id = new LocalMessageId(lastId),
                        FolderId = new FolderId(reader.GetInt64(1)),
                        Subject = subject,
                        From = Db.Str(reader, 3),
                        DateUtc = FromUnixMs(lastDate),
                        Flags = (MessageFlags)(int)Db.Int(reader, 5),
                        Snippet = withSnippet
                            ? BuildSnippet(Db.Str(reader, 6) ?? subject, query.Text)
                            : null,
                    });
                }
            }

            string? nextCursor = null;
            if (more)
            {
                if (order == SearchOrder.Relevance)
                {
                    var nextOffset = offset + limit;
                    if (nextOffset < _options.MaxSearchOffset) nextCursor = Cursors.EncodeOffset(nextOffset);
                }
                else
                {
                    nextCursor = Cursors.EncodeKeyset(lastDate, lastId);
                }
            }

            return new SearchResult
            {
                Hits = hits,
                NextCursor = nextCursor,
                Truncated = more,
                Route = route,
            };
        }, ct);
    }

    /// <summary>
    /// Assembles the query from constant fragments only. Nothing from the user's text ever reaches
    /// the SQL string — it is always a bound parameter.
    /// </summary>
    private static string BuildSearchSql(
        SearchRoute route,
        bool withSnippet,
        bool byAccount,
        bool byFolder,
        bool byTag,
        bool relevance,
        bool seeking,
        List<string> names)
    {
        var sql = new StringBuilder(512);
        sql.Append("SELECT m.id, m.folder_id, m.subject, m.from_addr, m.date_utc, m.flags");
        sql.Append(withSnippet ? ", substr(COALESCE(b.text, ''), 1, 4000)" : ", NULL");

        switch (route)
        {
            case SearchRoute.Fts:
                sql.Append(" FROM msg_fts JOIN messages m ON m.id = msg_fts.rowid");
                break;
            case SearchRoute.Cjk:
                sql.Append(" FROM msg_fts_cjk JOIN messages m ON m.id = msg_fts_cjk.rowid");
                break;
            default:
                sql.Append(" FROM messages m");
                break;
        }

        if (withSnippet || route == SearchRoute.Like)
            sql.Append(" LEFT JOIN body_text b ON b.message_id = m.id");

        sql.Append(" WHERE ");
        names.Add("$needle");
        switch (route)
        {
            case SearchRoute.Fts:
                sql.Append("msg_fts MATCH $needle");
                break;
            case SearchRoute.Cjk:
                sql.Append("msg_fts_cjk MATCH $needle");
                break;
            default:
                sql.Append("(m.subject LIKE $needle ESCAPE '\\' OR b.text LIKE $needle ESCAPE '\\')");
                break;
        }

        if (byAccount)
        {
            sql.Append(" AND m.account_id = $account");
            names.Add("$account");
        }

        if (byFolder)
        {
            sql.Append(" AND m.folder_id = $folder");
            names.Add("$folder");
        }

        if (byTag)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM tags t WHERE t.message_id = m.id AND t.tag = $tag)");
            names.Add("$tag");
        }

        if (seeking)
        {
            sql.Append(" AND (m.date_utc, m.id) < ($cursorDate, $cursorId)");
            names.Add("$cursorDate");
            names.Add("$cursorId");
        }

        sql.Append(relevance ? " ORDER BY rank" : " ORDER BY m.date_utc DESC, m.id DESC");
        sql.Append(" LIMIT $limit");
        names.Add("$limit");

        if (relevance)
        {
            sql.Append(" OFFSET $offset");
            names.Add("$offset");
        }

        return sql.ToString();
    }

    /// <summary>
    /// Builds a one-line excerpt around the first matching token. Runs over the returned page only,
    /// never across the full result set.
    /// </summary>
    private static string? BuildSnippet(string? source, string queryText)
    {
        if (string.IsNullOrEmpty(source)) return null;

        var text = source.Length > SnippetSourceLimit ? source[..SnippetSourceLimit] : source;
        var index = -1;
        var matchLength = 0;

        foreach (var token in queryText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var needle = token.TrimEnd('*');
            if (needle.Length == 0) continue;

            var found = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (found < 0) continue;
            if (index < 0 || found < index)
            {
                index = found;
                matchLength = needle.Length;
            }
        }

        int start;
        int length;
        if (index < 0)
        {
            start = 0;
            length = Math.Min(text.Length, SnippetContextChars * 2);
        }
        else
        {
            start = Math.Max(0, index - SnippetContextChars);
            var end = Math.Min(text.Length, index + matchLength + SnippetContextChars);
            length = end - start;
        }

        var excerpt = new StringBuilder(length + 8);
        if (start > 0) excerpt.Append('…');

        var lastWasSpace = false;
        for (var i = start; i < start + length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
                excerpt.Append(' ');
                continue;
            }
            lastWasSpace = false;
            excerpt.Append(c);
        }

        if (start + length < text.Length) excerpt.Append('…');
        return excerpt.ToString().Trim();
    }

    /// <summary>
    /// The gated read-only SQL surface from AGENT-INTERFACE §13.2. The connection is
    /// <c>query_only</c> and the row cap is enforced here; the environment gate lives in Application.
    /// </summary>
    public RawQueryResult ExecuteReadOnlyQuery(string sql, int maxRows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var cap = maxRows <= 0 ? 200 : Math.Min(maxRows, 5000);

        return Read(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();
            var columns = new List<string>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));

            var rows = new List<IReadOnlyList<object?>>();
            var truncated = false;

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (rows.Count >= cap) { truncated = true; break; }

                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(values);
            }

            return new RawQueryResult { Columns = columns, Rows = rows, Truncated = truncated };
        }, ct);
    }
}
