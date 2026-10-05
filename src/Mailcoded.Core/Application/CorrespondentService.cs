using Mailcoded.Core.Domain.Correspondents;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

/// <summary>Compose-time suggestions drawn from the people the user has written to, never from received mail.</summary>
public sealed class CorrespondentService
{
    private readonly SqliteStore _store;
    private readonly IClock _clock;

    public CorrespondentService(SqliteStore store, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _clock = clock;
    }

    /// <summary>Throws <see cref="ArgumentException"/> for a limit outside 1 to <see cref="CorrespondentRanking.MaxLimit"/>.</summary>
    public IReadOnlyList<Correspondent> Suggest(AccountId accountId, string? prefix, int? limit, CancellationToken ct) =>
        SuggestPage(accountId, prefix, limit, ct).Items;

    /// <summary>As <see cref="Suggest"/>, and says whether matches were left out by the limit.</summary>
    public (IReadOnlyList<Correspondent> Items, bool More) SuggestPage(AccountId accountId, string? prefix, int? limit, CancellationToken ct)
    {
        var take = limit ?? CorrespondentRanking.DefaultLimit;
        if (take is < 1 or > CorrespondentRanking.MaxLimit)
        {
            throw new ArgumentException($"limit must be between 1 and {CorrespondentRanking.MaxLimit}.");
        }

        return CorrespondentRanking.SuggestPage(_store.ListCorrespondents(accountId, ct), prefix, take, _clock.UtcNow);
    }
}
