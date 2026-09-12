using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using ParsedQuery = Mailcoded.Core.Domain.Search.SearchQuery;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

public sealed record SearchRequest
{
    public string Query { get; init; } = string.Empty;
    public AccountId? AccountId { get; init; }
    public FolderId? FolderId { get; init; }
    public int Limit { get; init; } = 50;
    public string? Cursor { get; init; }
    public SearchOrder Order { get; init; } = SearchOrder.Relevance;
    public bool IncludeSnippet { get; init; } = true;

    /// <summary>Null fuses meaning in when a model is installed; false asks for words only.</summary>
    public bool? Semantic { get; init; }
}

public sealed record SearchResults
{
    public required IReadOnlyList<StoreSearchHit> Hits { get; init; }

    /// <summary>Opaque store cursor. Pass it back unchanged; never mix it with another order.</summary>
    public string? NextCursor { get; init; }

    /// <summary>True only for matches no further call can reach — the relevance offset cap.</summary>
    public bool Truncated { get; init; }

    /// <summary>True when these hits were reordered by meaning as well as by words. A fused page has
    /// no cursor: the two rankings interleave, so a later lexical page would repeat what it promoted.</summary>
    public bool Semantic { get; init; }

    public SearchRoute Route { get; init; }

    /// <summary>True when no document matched every term and these hits come from the wider retry.
    /// A caller that shows results should say so, or the user reads a near miss as an exact one.</summary>
    public bool Relaxed { get; init; }
    public IReadOnlyList<SearchParseError> Errors { get; init; } = [];

    public static readonly SearchResults Empty = new() { Hits = [], Route = SearchRoute.None };
}

public sealed record SearchServiceOptions
{
    public int DefaultLimit { get; init; } = 50;
    public int MaxLimit { get; init; } = 200;

    public static readonly SearchServiceOptions Default = new();
}

/// <summary>Parses the query language and hands the structured query to the store, which serves all of it.</summary>
public sealed class SearchService
{
    private const double RankFusionConstant = 60.0;

    private readonly SqliteStore _store;
    private readonly AgentPolicy _policy;
    private readonly AuditLog _audit;
    private readonly SearchServiceOptions _options;
    private readonly SemanticSearch? _semantic;

    public SearchService(
        SqliteStore store,
        AgentPolicy policy,
        AuditLog audit,
        SearchServiceOptions? options = null,
        SemanticSearch? semantic = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(audit);

        _store = store;
        _policy = policy;
        _audit = audit;
        _options = options ?? SearchServiceOptions.Default;
        _semantic = semantic;
    }

    /// <summary>False when no model is installed. An explicit semantic request then fails rather
    /// than quietly returning lexical hits under another name.</summary>
    public bool SemanticAvailable => _semantic is not null;

    public async Task<SearchResults> SearchAsync(SearchRequest request, CallerContext caller, CancellationToken ct)
    {
        var results = Search(request, ct);

        if (caller.IsAgentSurface)
        {
            await _audit.InfoAsync(
                "search",
                request.AccountId ?? AccountId.None,
                caller,
                AuditText.Fields(
                    ("digest", AuditText.Digest(request.Query)),
                    ("hits", AuditText.Number(results.Hits.Count)),
                    ("truncated", AuditText.Bool(results.Truncated))),
                ct).ConfigureAwait(false);
        }

        return results;
    }

    public SearchResults Search(SearchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parse = SearchQueryParser.Parse(request.Query);

        StoreSearchResult Run(ParsedQuery query) =>
            _store.Search(
                new StoreSearchRequest
                {
                    Query = query,
                    AccountId = request.AccountId,
                    FolderId = request.FolderId,
                    Limit = NormalizeLimit(request.Limit),
                    Cursor = request.Cursor,
                    Order = request.Order,
                    IncludeSnippet = request.IncludeSnippet,
                },
                ct);

        var result = Run(parse.Query);

        // Every term had to match, and none did. Ask the wider question once rather than answer
        // nothing: ranking puts a document matching all the terms above one matching a single term.
        // Not on a cursor, because page two of a relaxed search must not silently change question.
        var relaxed = false;
        if (result.Hits.Count == 0 && request.Cursor is null && parse.Query.CanRelax)
        {
            result = Run(parse.Query.Relaxed());
            relaxed = result.Hits.Count > 0;
        }

        var fused = Fuse(parse.Query, result.Hits, request, ct);

        return new SearchResults
        {
            Hits = fused ?? result.Hits,
            NextCursor = fused is null ? result.NextCursor : null,
            Truncated = fused is null && result.Truncated,
            Route = result.Route,
            Relaxed = relaxed,
            Semantic = fused is not null,
            Errors = parse.Errors,
        };
    }

    /// <summary>Reciprocal rank fusion over the two rankings. FTS5 stays primary: a document both
    /// stages found outranks one either found alone, which is the whole point of fusing.</summary>
    private IReadOnlyList<StoreSearchHit>? Fuse(
        ParsedQuery query,
        IReadOnlyList<StoreSearchHit> lexical,
        SearchRequest request,
        CancellationToken ct)
    {
        if (_semantic is null || !_semantic.IsReady || request.Semantic == false) return null;
        if (request.Cursor is not null || request.Order != SearchOrder.Relevance) return null;

        var text = QueryText(query);
        if (text.Length == 0) return null;

        var limit = NormalizeLimit(request.Limit);
        var semantic = _semantic.Rank(text, request.AccountId, request.FolderId, limit, ct);
        if (semantic.Count == 0) return null;

        var scores = new Dictionary<long, double>(lexical.Count + semantic.Count);
        var known = new Dictionary<long, StoreSearchHit>(lexical.Count + semantic.Count);

        for (var rank = 0; rank < lexical.Count; rank++)
        {
            scores[lexical[rank].Id.Value] = 1.0 / (RankFusionConstant + rank + 1);
            known[lexical[rank].Id.Value] = lexical[rank];
        }

        var missing = new List<LocalMessageId>();
        for (var rank = 0; rank < semantic.Count; rank++)
        {
            var id = semantic[rank].MessageId;
            scores[id.Value] = scores.GetValueOrDefault(id.Value) + (1.0 / (RankFusionConstant + rank + 1));
            if (!known.ContainsKey(id.Value)) missing.Add(id);
        }

        foreach (var hit in _store.SearchHitsByIds(missing, query, request.IncludeSnippet, ct))
            known[hit.Id.Value] = hit;

        var ordered = new List<long>(scores.Keys);
        ordered.Sort((left, right) =>
        {
            var byScore = scores[right].CompareTo(scores[left]);
            return byScore != 0 ? byScore : right.CompareTo(left);
        });

        var hits = new List<StoreSearchHit>(Math.Min(limit, ordered.Count));
        foreach (var id in ordered)
        {
            if (hits.Count == limit) break;
            if (known.TryGetValue(id, out var hit)) hits.Add(hit);
        }

        return hits;
    }

    /// <summary>The free text of a query, without its filters: 'tag:unread' describes no meaning.</summary>
    private static string QueryText(ParsedQuery query)
    {
        var words = new List<string>(query.Terms.Count);
        foreach (var term in query.Terms)
            if (!term.Negated && term.Text.Length > 0) words.Add(term.Text);

        return string.Join(' ', words);
    }

    /// <summary>The gated raw-SQL read. The connection is query_only; the cap is applied here.</summary>
    public async Task<RawQueryResult> ExecuteSqlAsync(
        CallerContext caller,
        string sql,
        int maxRows,
        CancellationToken ct)
    {
        var gate = _policy.EvaluateRawSql(caller, sql, maxRows);

        if (!gate.Allowed)
        {
            await _audit.WarnAsync(
                AuditEvents.SqlQuery,
                AccountId.None,
                caller,
                AuditText.Fields(
                    ("decision", "denied"),
                    ("reason", gate.Reason.ToString()),
                    ("digest", AuditText.Digest(sql))),
                ct).ConfigureAwait(false);

            gate.ThrowIfDenied();
        }

        var result = _store.ExecuteReadOnlyQuery(sql, gate.RowCap, ct);

        await _audit.InfoAsync(
            AuditEvents.SqlQuery,
            AccountId.None,
            caller,
            AuditText.Fields(
                ("decision", "allowed"),
                ("digest", AuditText.Digest(sql)),
                ("rows", AuditText.Number(result.Rows.Count)),
                ("cap", AuditText.Number(gate.RowCap)),
                ("truncated", AuditText.Bool(result.Truncated))),
            ct).ConfigureAwait(false);

        return result;
    }

    private int NormalizeLimit(int limit)
    {
        if (limit <= 0) return _options.DefaultLimit;
        return limit > _options.MaxLimit ? _options.MaxLimit : limit;
    }
}
