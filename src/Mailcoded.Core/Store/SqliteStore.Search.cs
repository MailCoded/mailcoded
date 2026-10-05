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
        var (route, relevance, limit, offset, builder, sql) = Compile(request);

        if (relevance && offset >= _options.MaxSearchOffset)
            return new StoreSearchResult { Hits = [], Route = route, Truncated = true };

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
                        HasAttachments = Db.Int(reader, 7) != 0,
                        BodyFetched = Db.Int(reader, 8) != 0,
                        Size = Db.Int(reader, 9),
                        Automated = Db.BoolOrNull(reader, 10),
                        Encrypted = Db.BoolOrNull(reader, 11),
                        Dmarc = Db.DmarcOrNull(reader, 12),
                    });
                }
            }

            if (query.PrimaryAttachmentTerm is { } attachmentTerm)
                AttachMatches(session, hits, attachmentTerm, withSnippet, ct);

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

    internal static string CompiledSearchSql(StoreSearchRequest request) => Compile(request).Sql;

    private readonly record struct CompiledSearch(
        SearchRoute Route,
        bool Relevance,
        int Limit,
        int Offset,
        SearchSqlBuilder Builder,
        string Sql);

    private static CompiledSearch Compile(StoreSearchRequest request)
    {
        var query = request.Query;
        var route = PrimaryRoute(query);

        // Only bm25 over msg_fts gives a meaningful rank; trigram and LIKE always page by date.
        var relevance = route == SearchRoute.Fts && request.Order == SearchOrder.Relevance;
        var limit = NormalizeLimit(request.Limit);

        var offset = 0;
        if (relevance) Cursors.TryDecodeOffset(request.Cursor, out offset);

        var builder = new SearchSqlBuilder();
        AppendScope(builder, request);
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
        return new CompiledSearch(route, relevance, limit, offset, builder, sql);
    }

    private static void AttachMatches(DbSession session, List<StoreSearchHit> hits, FtsTerm term, bool withSnippet, CancellationToken ct)
    {
        var sql = term.Script switch
        {
            ScriptKind.ShortCjk => AttachmentMatchSql("a.text LIKE $expr ESCAPE '\\'", withSnippet),
            ScriptKind.Cjk => AttachmentMatchSql(
                "EXISTS (SELECT 1 FROM att_fts_cjk WHERE att_fts_cjk MATCH $expr AND rowid = a.id)", withSnippet),
            _ => AttachmentMatchSql("EXISTS (SELECT 1 FROM att_fts WHERE att_fts MATCH $expr AND rowid = a.id)", withSnippet),
        };

        var expression = term.Script == ScriptKind.ShortCjk ? term.LikePattern : term.MatchExpression;
        var needles = new[] { term.Text };

        for (var i = 0; i < hits.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var reader = session
                .Prepare(sql, "$id", "$expr", "$needle")
                .SetInt(0, hits[i].Id.Value)
                .SetText(1, expression)
                .SetText(2, term.Text)
                .ExecuteReader();

            if (!reader.Read()) continue;

            hits[i] = hits[i] with
            {
                AttachmentMatch = new StoreAttachmentMatch
                {
                    Index = (int)reader.GetInt64(0),
                    MimeType = reader.GetString(1),
                    FileName = Db.Str(reader, 2),
                    Snippet = withSnippet ? BuildSnippet(Db.Str(reader, 3), needles) : null,
                },
            };
        }
    }

    // The excerpt window is cut in SQL: attachment text can be 256,000 characters and the match sits anywhere in it.
    private static string AttachmentMatchSql(string condition, bool withSnippet) =>
        "SELECT a.idx, a.mime_type, a.file_name, "
        + (withSnippet ? "substr(a.text, MAX(1, instr(lower(a.text), lower($needle)) - 200), 600) " : "NULL ")
        + "FROM attachment_text a WHERE a.message_id = $id AND " + condition + " ORDER BY a.idx LIMIT 1";

    /// <summary>The index the residual text is ranked and joined through; None for a metadata-only query.</summary>
    private static SearchRoute PrimaryRoute(ParsedQuery query)
    {
        if (query.LatinMatchExpression is not null) return SearchRoute.Fts;
        if (query.CjkMatchExpression is not null) return SearchRoute.Cjk;
        return query.LikePatterns.Count > 0 ? SearchRoute.Like : SearchRoute.None;
    }

    private static void AppendScope(SearchSqlBuilder builder, StoreSearchRequest request)
    {
        if (request.AccountId is { } accountId) builder.AndIntCompare("m.account_id", "=", accountId.Value);
        if (request.FolderId is { } folderId) builder.AndIntCompare("m.folder_id", "=", folderId.Value);
        if (SearchFolderScope.HidesByDefault(request.Query, request.FolderId is not null))
            builder.AndOutsideFolderRoles(SearchFolderScope.HiddenByDefault);

        foreach (var predicate in request.Query.Predicates) AppendPredicate(builder, predicate);
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
            case SearchPredicate.InAnyFolder:
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
            case SearchPredicate.AttachmentText attachmentPredicate:
                builder.AndAttachmentText(attachmentPredicate.Term, negated);
                break;
            case SearchPredicate.IsAutomated:
                builder.And(negated ? "m.automated = 0" : "m.automated = 1");
                break;
            case SearchPredicate.IsEncrypted:
                builder.And(negated ? "m.encrypted = 0" : "m.encrypted = 1");
                break;
            case SearchPredicate.IsVerified:
                builder.And(negated ? "m.dmarc IS NOT 'pass'" : "m.dmarc = 'pass'");
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

        AppendNegatedText(builder, query);
    }

    private static void AppendNegatedText(SearchSqlBuilder builder, ParsedQuery query)
    {
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
        sql.Append(", m.has_attachments, m.body_fetched, m.size, m.automated, m.encrypted, m.dmarc");

        sql.Append(route switch
        {
            SearchRoute.Fts => " FROM msg_fts JOIN messages m ON m.id = msg_fts.rowid",
            SearchRoute.Cjk => " FROM msg_fts_cjk JOIN messages m ON m.id = msg_fts_cjk.rowid",
            _ => " FROM messages m",
        });

        if (withSnippet) sql.Append(" LEFT JOIN body_text b ON b.message_id = m.id");
        if (where.Length > 0) sql.Append(" WHERE ").Append(where);

        sql.Append(relevance ? RelevanceOrder(route) : " ORDER BY m.date_utc DESC, m.id DESC");
        sql.Append(" LIMIT ").Append(limitName);
        if (offsetName is not null) sql.Append(" OFFSET ").Append(offsetName);

        return sql.ToString();
    }

    /// <summary>
    /// Field weights for bm25, in the column order the FTS tables declare. Bare <c>ORDER BY rank</c>
    /// weights every column at 1.0, so a term in a signature footer counted as much as the same term
    /// in the subject line. bm25 returns a negative score where more negative is a better match, so
    /// ascending order still puts the best hit first, exactly as <c>rank</c> did.
    /// </summary>
    /// Measured rather than chosen by taste, on a 43-document corpus where 3 documents match: with
    /// the subject at 10 a body repeating the term twenty times still edged out a subject naming it
    /// (-4.995 against -4.947). Twenty is where the principle holds — a subject that names the topic
    /// outranks a body that merely repeats the word — and a body mentioning it once stays last.
    private const string FtsWeights = "20.0, 1.0, 3.0, 1.0";  // subject, body_text, from_addr, to_addr

    private const string CjkWeights = "20.0, 1.0";            // subject, body_text

    private static string RelevanceOrder(SearchRoute route) => route switch
    {
        SearchRoute.Fts => $" ORDER BY bm25(msg_fts, {FtsWeights})",
        SearchRoute.Cjk => $" ORDER BY bm25(msg_fts_cjk, {CjkWeights})",
        _ => " ORDER BY m.date_utc DESC, m.id DESC",
    };

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
