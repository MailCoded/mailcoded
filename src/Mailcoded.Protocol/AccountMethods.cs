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

/// <summary><c>account.test</c> — prove the stored settings and credential still work.</summary>
public sealed record AccountTestParams
{
    public required long AccountId { get; init; }
}

public sealed record AccountTestResult
{
    /// <summary><c>ok|auth|network|unsupported</c> for each leg.</summary>
    public required string Imap { get; init; }

    public required string Smtp { get; init; }

    public int Folders { get; init; }

    /// <summary>Short and redacted; never a credential.</summary>
    public string? ImapDetail { get; init; }

    public string? SmtpDetail { get; init; }
}

/// <summary><c>outbox.list</c> — what is queued, sending, sent or stuck. Carries no message body.</summary>
public sealed record OutboxListParams
{
    public long? AccountId { get; init; }

    /// <summary><c>queued|sending|sent|failed</c>, or null for everything.</summary>
    public string? State { get; init; }

    public int? Limit { get; init; }
}

public sealed record OutboxListResult
{
    public IReadOnlyList<OutboxEntryDto> Entries { get; init; } = [];
}

/// <summary>One outbox row as a client renders it. No raw bytes and no body.</summary>
public sealed record OutboxEntryDto
{
    public required long Id { get; init; }
    public required long AccountId { get; init; }
    public required string State { get; init; }
    public required string MessageId { get; init; }
    public IReadOnlyList<string> To { get; init; } = [];
    public int Attempts { get; init; }
    public bool PermanentlyFailed { get; init; }
    public string? SmtpResponse { get; init; }

    /// <summary>ISO 8601 UTC.</summary>
    public required string CreatedUtc { get; init; }

    public string? NextAttemptUtc { get; init; }

    /// <summary>False until a confirm token was consumed; such a row is a draft, never sent.</summary>
    public bool Confirmed { get; init; }
}
