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

    public IReadOnlyList<FolderDto> Folders { get; set; } = [];

    public int FolderIndex { get; set; }

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

    public AccountDto? Account => Accounts.Count > 0 ? Accounts[0] : null;

    public FolderDto? Folder =>
        FolderIndex >= 0 && FolderIndex < Folders.Count ? Folders[FolderIndex] : null;

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
