using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
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
}

public sealed record SearchResults
{
    public required IReadOnlyList<StoreSearchHit> Hits { get; init; }

    /// <summary>Opaque store cursor. Pass it back unchanged; never mix it with another order.</summary>
    public string? NextCursor { get; init; }

    /// <summary>True only for matches no further call can reach — the relevance offset cap.</summary>
    public bool Truncated { get; init; }

    public SearchRoute Route { get; init; }
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
    private readonly SqliteStore _store;
    private readonly AgentPolicy _policy;
    private readonly AuditLog _audit;
    private readonly SearchServiceOptions _options;

    public SearchService(SqliteStore store, AgentPolicy policy, AuditLog audit, SearchServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(audit);

        _store = store;
        _policy = policy;
        _audit = audit;
        _options = options ?? SearchServiceOptions.Default;
    }

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

        var result = _store.Search(
            new StoreSearchRequest
            {
                Query = parse.Query,
                AccountId = request.AccountId,
                FolderId = request.FolderId,
                Limit = NormalizeLimit(request.Limit),
                Cursor = request.Cursor,
                Order = request.Order,
                IncludeSnippet = request.IncludeSnippet,
            },
            ct);

        return new SearchResults
        {
            Hits = result.Hits,
            NextCursor = result.NextCursor,
            Truncated = result.Truncated,
            Route = result.Route,
            Errors = parse.Errors,
        };
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
