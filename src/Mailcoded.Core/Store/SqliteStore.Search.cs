using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using ParsedQuery = Mailcoded.Core.Domain.Search.SearchQuery;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const int SnippetContextChars = 48;
    private const int SnippetSourceLimit = 4000;

    /// <summary>Runs a parsed query as one statement; a metadata-only query keysets to any depth.</summary>
    public StoreSearchResult Search(StoreSearchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = request.Query;
        var route = PrimaryRoute(query);

        // Only bm25 over msg_fts gives a meaningful rank; trigram and LIKE always page by date.
        var relevance = route == SearchRoute.Fts && request.Order == SearchOrder.Relevance;
        var limit = NormalizeLimit(request.Limit);

        var offset = 0;
        if (relevance)
        {
            Cursors.TryDecodeOffset(request.Cursor, out offset);
            if (offset >= _options.MaxSearchOffset)
                return new StoreSearchResult { Hits = [], Route = route, Truncated = true };
        }

        var builder = new SearchSqlBuilder();

        if (request.AccountId is { } accountId) builder.AndIntCompare("m.account_id", "=", accountId.Value);
        if (request.FolderId is { } folderId) builder.AndIntCompare("m.folder_id", "=", folderId.Value);

        foreach (var predicate in query.Predicates) AppendPredicate(builder, predicate);
        AppendText(builder, query, route);

        if (!relevance && Cursors.TryDecodeKeyset(request.Cursor, out var cursorDate, out var cursorId))
        {
            var date = builder.AddInt(cursorDate);
            var id = builder.AddInt(cursorId);
            builder.And(string.Concat("(m.date_utc, m.id) < (", date, ", ", id, ")"));
        }

        var limitName = builder.AddInt(limit + 1);
        var offsetName = relevance ? builder.AddInt(offset) : null;

        var sql = BuildSearchSql(route, request.IncludeSnippet, builder.Where, relevance, limitName, offsetName);
        var needles = SnippetNeedles(query);
        var withSnippet = request.IncludeSnippet;

        return Read(session =>
        {
            var statement = session.Prepare(sql, builder.Names());
            builder.Bind(statement);

            var hits = new List<StoreSearchHit>(limit);
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

                    hits.Add(new StoreSearchHit
                    {
                        Id = new LocalMessageId(lastId),
                        FolderId = new FolderId(reader.GetInt64(1)),
                        Subject = subject,
                        From = Db.Str(reader, 3),
                        DateUtc = FromUnixMs(lastDate),
                        Flags = (MessageFlags)(int)Db.Int(reader, 5),
                        Snippet = withSnippet ? BuildSnippet(Db.Str(reader, 6) ?? subject, needles) : null,
                    });
                }
            }

            string? nextCursor = null;
            var truncated = false;

            if (more)
            {
                if (relevance)
                {
                    var nextOffset = offset + limit;
                    if (nextOffset < _options.MaxSearchOffset) nextCursor = Cursors.EncodeOffset(nextOffset);
                    else truncated = true;
                }
                else
                {
                    nextCursor = Cursors.EncodeKeyset(lastDate, lastId);
                }
            }

            return new StoreSearchResult
            {
                Hits = hits,
                NextCursor = nextCursor,
                Truncated = truncated,
                Route = route,
            };
        }, ct);
    }

    /// <summary>The index the residual text is ranked and joined through; None for a metadata-only query.</summary>
    private static SearchRoute PrimaryRoute(ParsedQuery query)
    {
        if (query.LatinMatchExpression is not null) return SearchRoute.Fts;
        if (query.CjkMatchExpression is not null) return SearchRoute.Cjk;
        return query.LikePatterns.Count > 0 ? SearchRoute.Like : SearchRoute.None;
    }

    private static void AppendPredicate(SearchSqlBuilder builder, SearchPredicate predicate)
    {
        var negated = predicate.Negated;

        switch (predicate)
        {
            case SearchPredicate.From fromPredicate:
                builder.AndContains("COALESCE(m.from_addr, '')", fromPredicate.Value, negated);
                break;
            case SearchPredicate.To toPredicate:
                builder.AndContains("COALESCE(m.to_addrs, '')", toPredicate.Value, negated);
                break;
            case SearchPredicate.Cc ccPredicate:
                builder.AndContains("COALESCE(m.cc_addrs, '')", ccPredicate.Value, negated);
                break;
            case SearchPredicate.Subject subjectPredicate:
                builder.AndContains("COALESCE(m.subject, '')", subjectPredicate.Value, negated);
                break;
            case SearchPredicate.HasTag tagPredicate:
                builder.AndTag(tagPredicate.Value, negated);
                break;
            case SearchPredicate.InFolder folderPredicate:
                builder.AndFolderName(folderPredicate.Folder, negated);
                break;
            case SearchPredicate.IsUnread:
                builder.And(SearchSqlBuilder.FlagClause(MessageFlags.Unread, negated));
                break;
            case SearchPredicate.IsFlagged:
                builder.And(SearchSqlBuilder.FlagClause(MessageFlags.Flagged, negated));
                break;
            case SearchPredicate.IsDraft:
                builder.And(SearchSqlBuilder.FlagClause(MessageFlags.Draft, negated));
                break;
            case SearchPredicate.IsReplied:
                builder.And(SearchSqlBuilder.FlagClause(MessageFlags.Answered, negated));
                break;
            case SearchPredicate.HasAttachment:
                builder.And(negated ? "m.has_attachments = 0" : "m.has_attachments = 1");
                break;
            case SearchPredicate.BeforeDate beforePredicate:
                builder.AndIntCompare("m.date_utc", negated ? ">=" : "<", ToUnixMs(beforePredicate.DateUtc));
                break;
            case SearchPredicate.AfterDate afterPredicate:
                builder.AndIntCompare("m.date_utc", negated ? "<" : ">=", ToUnixMs(afterPredicate.DateUtc));
                break;
            default:
                break;
        }
    }

    private static void AppendText(SearchSqlBuilder builder, ParsedQuery query, SearchRoute route)
    {
        if (route == SearchRoute.Fts && query.LatinMatchExpression is { } latin)
            builder.AndFtsMatch("msg_fts", latin);

        if (query.CjkMatchExpression is { } cjk)
        {
            if (route == SearchRoute.Cjk) builder.AndFtsMatch("msg_fts_cjk", cjk);
            else builder.AndFtsSubquery("msg_fts_cjk", cjk, negated: false);
        }

        foreach (var pattern in query.LikePatterns) builder.AndLikeFallback(pattern, negated: false);

        if (query.NegatedLatinMatchExpression is { } notLatin)
            builder.AndFtsSubquery("msg_fts", notLatin, negated: true);

        if (query.NegatedCjkMatchExpression is { } notCjk)
            builder.AndFtsSubquery("msg_fts_cjk", notCjk, negated: true);

        foreach (var pattern in query.NegatedLikePatterns) builder.AndLikeFallback(pattern, negated: true);
    }

    private static string BuildSearchSql(
        SearchRoute route,
        bool withSnippet,
        string where,
        bool relevance,
        string limitName,
        string? offsetName)
    {
        var sql = new StringBuilder(512);
        sql.Append("SELECT m.id, m.folder_id, m.subject, m.from_addr, m.date_utc, m.flags");
        sql.Append(withSnippet ? ", substr(COALESCE(b.text, ''), 1, 4000)" : ", NULL");

        sql.Append(route switch
        {
            SearchRoute.Fts => " FROM msg_fts JOIN messages m ON m.id = msg_fts.rowid",
            SearchRoute.Cjk => " FROM msg_fts_cjk JOIN messages m ON m.id = msg_fts_cjk.rowid",
            _ => " FROM messages m",
        });

        if (withSnippet) sql.Append(" LEFT JOIN body_text b ON b.message_id = m.id");
        if (where.Length > 0) sql.Append(" WHERE ").Append(where);

        sql.Append(relevance ? " ORDER BY rank" : " ORDER BY m.date_utc DESC, m.id DESC");
        sql.Append(" LIMIT ").Append(limitName);
        if (offsetName is not null) sql.Append(" OFFSET ").Append(offsetName);

        return sql.ToString();
    }

    private static IReadOnlyList<string> SnippetNeedles(ParsedQuery query)
    {
        var needles = new List<string>(query.Terms.Count);
        foreach (var term in query.Terms)
            if (!term.Negated && term.Text.Length > 0)
                needles.Add(term.Text);
        return needles;
    }

    // The FTS5 tables are contentless, so SQLite's own snippet() has no text to work from.
    private static string? BuildSnippet(string? source, IReadOnlyList<string> needles)
    {
        if (string.IsNullOrEmpty(source)) return null;

        var text = source.Length > SnippetSourceLimit ? source[..SnippetSourceLimit] : source;
        var index = -1;
        var matchLength = 0;

        foreach (var needle in needles)
        {
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

    /// <summary>The gated read-only SQL surface (AGENT-INTERFACE §13.2); the reader is query_only.</summary>
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
