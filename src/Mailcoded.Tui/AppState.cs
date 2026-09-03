using Mailcoded.Protocol;
using Mailcoded.Tui.Render;

namespace Mailcoded.Tui;

internal enum Pane
{
    Folders,
    Messages,
    Reader,
    Help,
    Compose,
    Confirm,
}

/// <summary>What a confirmation screen may show. The one-time token is deliberately absent:
/// it lives on App alone, so no view can render or log it.</summary>
internal sealed record PendingSend
{
    public required long AccountId { get; init; }
    public required long DraftId { get; init; }
    public required SendPreviewDto Preview { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
}

internal sealed class AppState
{
    public IReadOnlyList<AccountDto> Accounts { get; set; } = [];

    public Dictionary<long, IReadOnlyList<FolderDto>> FoldersByAccount { get; } = [];

    public HashSet<string> Collapsed { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<NavRow> Nav { get; private set; } = [];

    public int NavIndex { get; set; }

    public int NavScroll { get; set; }

    public List<EnvelopeDto> Messages { get; } = [];

    public int MessageIndex { get; set; }

    public int MessageScroll { get; set; }

    public string? NextCursor { get; set; }

    public bool Truncated { get; set; }

    public Pane Focus { get; set; } = Pane.Folders;

    public MessageGetResult? Open { get; set; }

    public int BodyScroll { get; set; }

    public IReadOnlyList<SafeSpan>? BodyLines { get; set; }

    public int BodyWidth { get; set; }

    public string Status { get; set; } = string.Empty;

    public bool StatusIsError { get; set; }

    public string? Query { get; set; }

    public string? Prompt { get; set; }

    public string PromptInput { get; set; } = string.Empty;

    public string BusyLabel { get; set; } = string.Empty;

    public bool ChoosingDestination { get; set; }

    public DraftBuffer? Draft { get; set; }

    public PendingSend? Pending { get; set; }

    public NavRow? Row => NavIndex >= 0 && NavIndex < Nav.Count ? Nav[NavIndex] : null;

    /// <summary>The account the sidebar cursor is inside, not merely the first one configured.</summary>
    public AccountDto? Account
    {
        get
        {
            if (Row is { } row)
                foreach (var account in Accounts)
                    if (account.Id == row.AccountId) return account;

            return Accounts.Count > 0 ? Accounts[0] : null;
        }
    }

    public FolderDto? Folder => Row?.Folder;

    public IReadOnlyList<FolderDto> FoldersOf(long accountId) =>
        FoldersByAccount.TryGetValue(accountId, out var list) ? list : [];

    /// <summary>Rebuilds the sidebar, keeping the cursor on whatever row it was on.</summary>
    public void Rebuild()
    {
        var wanted = Row?.Key;
        Nav = Navigation.Build(Accounts, FoldersByAccount, Collapsed);

        if (wanted is not null)
        {
            for (var i = 0; i < Nav.Count; i++)
            {
                if (!string.Equals(Nav[i].Key, wanted, StringComparison.Ordinal)) continue;
                NavIndex = i;
                return;
            }
        }

        NavIndex = Math.Clamp(NavIndex, 0, Math.Max(0, Nav.Count - 1));
    }

    public bool SelectKey(string key)
    {
        for (var i = 0; i < Nav.Count; i++)
        {
            if (!string.Equals(Nav[i].Key, key, StringComparison.Ordinal)) continue;
            NavIndex = i;
            return true;
        }

        return false;
    }

    public EnvelopeDto? Selected =>
        MessageIndex >= 0 && MessageIndex < Messages.Count ? Messages[MessageIndex] : null;

    public void Say(string message)
    {
        Status = message;
        StatusIsError = false;
    }

    public void Complain(string message)
    {
        Status = message;
        StatusIsError = true;
    }
}
