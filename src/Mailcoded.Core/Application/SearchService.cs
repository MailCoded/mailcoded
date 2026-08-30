using System.Globalization;
using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Domain.Tags;
using Mailcoded.Core.Store;
using ParsedQuery = Mailcoded.Core.Domain.Search.SearchQuery;
using StoreQuery = Mailcoded.Core.Store.SearchQuery;

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
    public required IReadOnlyList<SearchHit> Hits { get; init; }
    public string? NextCursor { get; init; }
    public bool Truncated { get; init; }
    public SearchRoute Route { get; init; }
    public IReadOnlyList<SearchParseError> Errors { get; init; } = [];

    /// <summary>Rows read before residual predicates were applied; useful for diagnosing a slow query.</summary>
    public int Scanned { get; init; }

    public static readonly SearchResults Empty = new() { Hits = [], Route = SearchRoute.None };
}

public sealed record SearchServiceOptions
{
    public int DefaultLimit { get; init; } = 50;
    public int MaxLimit { get; init; } = 200;

    /// <summary>Store pages one call may read while looking for the first surviving hit.</summary>
    public int MaxPagesScanned { get; init; } = 8;

    public static readonly SearchServiceOptions Default = new();
}

/// <summary>Parses the query language, executes it through the store, and pages with one cursor.</summary>
public sealed class SearchService
{
    private const char TextCursorPrefix = 't';
    private const char WalkCursorPrefix = 'w';

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
        var limit = NormalizeLimit(request.Limit);
        var plan = BuildPlan(parse.Query, request, ct);

        var results = plan.Text.Length > 0
            ? RunTextSearch(plan, request, limit, ct)
            : RunFolderWalk(plan, request, limit, ct);

        return results with { Errors = parse.Errors };
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

    private SearchResults RunTextSearch(SearchPlan plan, SearchRequest request, int limit, CancellationToken ct)
    {
        var cursor = DecodeTextCursor(request.Cursor);
        var hits = new List<SearchHit>(limit);
        var scanned = 0;
        var truncated = false;
        string? nextCursor = null;
        var route = SearchRoute.None;

        for (var page = 0; page < _options.MaxPagesScanned; page++)
        {
            ct.ThrowIfCancellationRequested();

            var result = _store.Search(
                new StoreQuery
                {
                    Text = plan.Text,
                    AccountId = request.AccountId,
                    FolderId = plan.FolderId,
                    Tag = plan.Tag,
                    Limit = limit,
                    Cursor = cursor,
                    Order = request.Order,
                    IncludeSnippet = request.IncludeSnippet,
                },
                ct);

            route = result.Route;
            scanned += result.Hits.Count;

            foreach (var hit in result.Hits)
            {
                if (!Survives(hit, plan, ct)) continue;
                hits.Add(hit);
            }

            cursor = result.NextCursor;
            truncated = result.Truncated;
            nextCursor = cursor is null ? null : EncodeTextCursor(cursor);

            if (hits.Count > 0 || cursor is null) break;
        }

        return new SearchResults
        {
            Hits = hits,
            NextCursor = nextCursor,
            Truncated = truncated,
            Route = route,
            Scanned = scanned,
        };
    }

    private SearchResults RunFolderWalk(SearchPlan plan, SearchRequest request, int limit, CancellationToken ct)
    {
        var folders = plan.WalkFolders;
        if (folders.Count == 0) return SearchResults.Empty;

        DecodeWalkCursor(request.Cursor, out var index, out var inner);
        if (index < 0) index = 0;

        var hits = new List<SearchHit>(limit);
        var scanned = 0;
        string? nextCursor = null;

        for (var page = 0; page < _options.MaxPagesScanned && index < folders.Count; page++)
        {
            ct.ThrowIfCancellationRequested();

            var folder = folders[index];
            var result = _store.ListEnvelopes(folder.Id, inner, limit, ct);
            scanned += result.Items.Count;

            foreach (var item in result.Items)
            {
                var hit = new SearchHit
                {
                    Id = item.Id,
                    FolderId = folder.Id,
                    Subject = item.Subject,
                    From = item.From,
                    DateUtc = item.DateUtc,
                    Flags = item.Flags,
                };

                if (!Survives(hit, plan, ct)) continue;
                hits.Add(hit);
            }

            if (result.NextCursor is { } next)
            {
                inner = next;
            }
            else
            {
                index++;
                inner = null;
            }

            nextCursor = index < folders.Count ? EncodeWalkCursor(index, inner) : null;
            if (hits.Count > 0) break;
        }

        return new SearchResults
        {
            Hits = hits,
            NextCursor = nextCursor,
            Truncated = nextCursor is not null,
            Route = SearchRoute.None,
            Scanned = scanned,
        };
    }

    private SearchPlan BuildPlan(ParsedQuery query, SearchRequest request, CancellationToken ct)
    {
        var residual = new List<SearchPredicate>();
        FolderId? folderId = request.FolderId;
        Tag? storeTag = null;

        foreach (var predicate in query.Predicates)
        {
            switch (predicate)
            {
                case SearchPredicate.InFolder folder when !folder.Negated && folderId is null:
                {
                    var resolved = ResolveFolder(request.AccountId, folder.Folder, ct);
                    if (resolved is { } id) folderId = id;
                    else residual.Add(predicate);
                    break;
                }

                case SearchPredicate.HasTag tag
                    when !tag.Negated && storeTag is null && TagFlagMap.SystemFlagFor(tag.Value) is null:
                    storeTag = tag.Value;
                    break;

                default:
                    residual.Add(predicate);
                    break;
            }
        }

        var text = new StringBuilder(64);
        var negatedText = new List<string>();

        foreach (var term in query.Terms)
        {
            if (term.Negated)
            {
                negatedText.Add(term.Text);
                continue;
            }

            if (text.Length > 0) text.Append(' ');
            text.Append(term.Text);
        }

        IReadOnlyList<FolderSummary> walkFolders = text.Length > 0 ? [] : ResolveWalkFolders(request, folderId, ct);

        return new SearchPlan(text.ToString(), folderId, storeTag, residual, negatedText, walkFolders);
    }

    private IReadOnlyList<FolderSummary> ResolveWalkFolders(SearchRequest request, FolderId? folderId, CancellationToken ct)
    {
        if (folderId is { } id)
        {
            var folder = _store.GetFolder(id, ct);
            if (folder is null) return [];
            return [folder];
        }

        if (request.AccountId is { } accountId) return _store.ListFolders(accountId, ct);

        var all = new List<FolderSummary>();
        foreach (var account in _store.ListAccounts(ct))
        {
            if (account.Id.IsNone) continue;
            all.AddRange(_store.ListFolders(account.Id, ct));
        }

        return all;
    }

    private FolderId? ResolveFolder(AccountId? accountId, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var accounts = new List<AccountId>();
        if (accountId is { } single) accounts.Add(single);
        else
        {
            foreach (var account in _store.ListAccounts(ct))
                if (!account.Id.IsNone) accounts.Add(account.Id);
        }

        var role = FolderRoleExtensions.FromWireValue(name.ToLowerInvariant());

        foreach (var id in accounts)
        {
            foreach (var folder in _store.ListFolders(id, ct))
            {
                if (string.Equals(folder.Path.Value, name, StringComparison.OrdinalIgnoreCase)) return folder.Id;
                if (string.Equals(folder.Path.LeafName, name, StringComparison.OrdinalIgnoreCase)) return folder.Id;
                if (role != FolderRole.None && folder.Role == role) return folder.Id;
            }
        }

        return null;
    }

    private bool Survives(SearchHit hit, SearchPlan plan, CancellationToken ct)
    {
        if (plan.Residual.Count == 0 && plan.NegatedText.Count == 0) return true;

        var context = new HitContext(_store, hit);

        foreach (var predicate in plan.Residual)
        {
            var value = Evaluate(predicate, hit, context, ct);
            if (predicate.Negated) value = !value;
            if (!value) return false;
        }

        foreach (var needle in plan.NegatedText)
        {
            if (Contains(hit.Subject, needle)) return false;
            if (Contains(context.BodyText(ct), needle)) return false;
        }

        return true;
    }

    private bool Evaluate(SearchPredicate predicate, SearchHit hit, HitContext context, CancellationToken ct) => predicate switch
    {
        SearchPredicate.From from => Contains(hit.From, from.Value),
        SearchPredicate.To to => Contains(context.Row(ct)?.To, to.Value),
        SearchPredicate.Cc cc => Contains(context.Row(ct)?.Cc, cc.Value),
        SearchPredicate.Subject subject => Contains(hit.Subject, subject.Value),
        SearchPredicate.HasTag tag => HasTag(hit, context, tag.Value, ct),
        SearchPredicate.InFolder folder => InFolder(hit, folder.Folder, ct),
        SearchPredicate.IsUnread => (hit.Flags & MessageFlags.Unread) != 0,
        SearchPredicate.IsFlagged => (hit.Flags & MessageFlags.Flagged) != 0,
        SearchPredicate.IsDraft => (hit.Flags & MessageFlags.Draft) != 0,
        SearchPredicate.IsReplied => (hit.Flags & MessageFlags.Answered) != 0,
        SearchPredicate.HasAttachment => context.Row(ct)?.HasAttachments ?? false,
        SearchPredicate.BeforeDate before => hit.DateUtc < before.DateUtc,
        SearchPredicate.AfterDate after => hit.DateUtc >= after.DateUtc,
        _ => true,
    };

    private static bool HasTag(SearchHit hit, HitContext context, Tag tag, CancellationToken ct)
    {
        if (TagFlagMap.SystemFlagFor(tag) is { } flag) return (hit.Flags & flag) != 0;

        foreach (var known in context.Tags(ct))
            if (known == tag) return true;

        return false;
    }

    private bool InFolder(SearchHit hit, string name, CancellationToken ct)
    {
        var folder = _store.GetFolder(hit.FolderId, ct);
        if (folder is null) return false;

        if (string.Equals(folder.Path.Value, name, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(folder.Path.LeafName, name, StringComparison.OrdinalIgnoreCase)) return true;

        var role = FolderRoleExtensions.FromWireValue(name.ToLowerInvariant());
        return role != FolderRole.None && folder.Role == role;
    }

    private static bool Contains(string? haystack, string? needle)
    {
        if (string.IsNullOrEmpty(needle)) return true;
        if (string.IsNullOrEmpty(haystack)) return false;
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private int NormalizeLimit(int limit)
    {
        if (limit <= 0) return _options.DefaultLimit;
        return limit > _options.MaxLimit ? _options.MaxLimit : limit;
    }

    private static string EncodeTextCursor(string inner) => string.Concat(TextCursorPrefix.ToString(), inner);

    private static string? DecodeTextCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || cursor[0] != TextCursorPrefix) return null;
        var inner = cursor[1..];
        return inner.Length == 0 ? null : inner;
    }

    private static string EncodeWalkCursor(int index, string? inner) =>
        string.Concat(
            WalkCursorPrefix.ToString(),
            index.ToString(CultureInfo.InvariantCulture),
            ":",
            inner ?? string.Empty);

    private static void DecodeWalkCursor(string? cursor, out int index, out string? inner)
    {
        index = 0;
        inner = null;
        if (string.IsNullOrEmpty(cursor) || cursor[0] != WalkCursorPrefix) return;

        var body = cursor.AsSpan(1);
        var colon = body.IndexOf(':');
        if (colon <= 0) return;

        if (!int.TryParse(body[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return;

        index = parsed;
        var rest = body[(colon + 1)..];
        inner = rest.Length == 0 ? null : rest.ToString();
    }

    private sealed record SearchPlan(
        string Text,
        FolderId? FolderId,
        Tag? Tag,
        IReadOnlyList<SearchPredicate> Residual,
        IReadOnlyList<string> NegatedText,
        IReadOnlyList<FolderSummary> WalkFolders);

    private sealed class HitContext
    {
        private readonly SqliteStore _store;
        private readonly SearchHit _hit;
        private EnvelopeRow? _row;
        private bool _rowLoaded;
        private IReadOnlyList<Tag>? _tags;
        private string? _body;
        private bool _bodyLoaded;

        public HitContext(SqliteStore store, SearchHit hit)
        {
            _store = store;
            _hit = hit;
        }

        public EnvelopeRow? Row(CancellationToken ct)
        {
            if (_rowLoaded) return _row;
            _row = _store.GetEnvelope(_hit.Id, ct);
            _rowLoaded = true;
            return _row;
        }

        public IReadOnlyList<Tag> Tags(CancellationToken ct) => _tags ??= _store.GetTags(_hit.Id, ct);

        public string? BodyText(CancellationToken ct)
        {
            if (_bodyLoaded) return _body;
            _body = _store.GetBodyText(_hit.Id, ct);
            _bodyLoaded = true;
            return _body;
        }
    }
}
