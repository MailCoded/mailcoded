namespace Mailcoded.Protocol;

/// <summary><c>account.add</c> — registers an account. The credential must already be in the secret store.</summary>
public sealed record AccountAddParams
{
    public required string Email { get; init; }

    public string? DisplayName { get; init; }

    /// <summary><c>imap|graph|jmap|gmail</c>.</summary>
    public string Provider { get; init; } = ProviderKinds.Imap;

    public required ImapConfigDto Imap { get; init; }

    public SmtpConfigDto? Smtp { get; init; }

    public AuthDto Auth { get; init; } = new();

    /// <summary>Handle previously registered through <c>secret.set</c>. Never the credential itself.</summary>
    public required string SecretRef { get; init; }
}

public sealed record AuthDto
{
    /// <summary><c>password|oauth2</c>.</summary>
    public string Kind { get; init; } = AuthKinds.Password;
}

public sealed record AccountAddResult
{
    public required long AccountId { get; init; }
}

/// <summary><c>account.list</c>.</summary>
public sealed record AccountListParams
{
    public static readonly AccountListParams Instance = new();
}

public sealed record AccountListResult
{
    public IReadOnlyList<AccountDto> Accounts { get; init; } = [];
}

/// <summary><c>folder.list</c>.</summary>
public sealed record FolderListParams
{
    public required long AccountId { get; init; }
}

public sealed record FolderListResult
{
    public IReadOnlyList<FolderDto> Folders { get; init; } = [];
}

/// <summary><c>sync</c> — one folder when <see cref="SyncParams.FolderId"/> is set, otherwise the whole account.</summary>
public sealed record SyncParams
{
    public required long AccountId { get; init; }

    public long? FolderId { get; init; }
}

public sealed record SyncResult
{
    public int Added { get; init; }

    public int Updated { get; init; }

    public int Expunged { get; init; }

    public long DurationMs { get; init; }
}

/// <summary><c>watch.subscribe</c> — start delivering notifications for an account.</summary>
public sealed record WatchSubscribeParams
{
    public required long AccountId { get; init; }

    /// <summary>Folders to hold IDLE on. Empty means the account's configured watch set.</summary>
    public IReadOnlyList<long> FolderIds { get; init; } = [];
}

public sealed record WatchSubscribeResult
{
    public static readonly WatchSubscribeResult Instance = new();
}
