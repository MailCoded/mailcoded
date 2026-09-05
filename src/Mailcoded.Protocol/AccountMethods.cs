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

    /// <summary>Opaque handle into the secret store; <c>secret.set</c> must have registered it first.
    /// Never the credential itself.</summary>
    public required string SecretRef { get; init; }

    /// <summary>Prove the credential with one IMAP login before writing anything. On failure the call
    /// answers 1000 or 1001 and no account is created, so a bad password leaves nothing behind.</summary>
    public bool Verify { get; init; }
}

public sealed record AuthDto
{
    /// <summary><c>password|oauth2</c>.</summary>
    public string Kind { get; init; } = AuthKinds.Password;
}

public sealed record AccountAddResult
{
    public required long AccountId { get; init; }

    /// <summary>True when a real login proved the credential, which only happens with <c>verify</c>.</summary>
    public bool Verified { get; init; }

    /// <summary>Folders the probe saw, when one ran.</summary>
    public int? Folders { get; init; }
}

/// <summary><c>provider.detect</c> — settings and credential guidance for an address, so a client can
/// pre-fill onboarding. A table lookup: no network, nothing stored.</summary>
public sealed record ProviderDetectParams
{
    public required string Email { get; init; }
}

public sealed record ProviderDetectResult
{
    public required ProviderPresetDto Preset { get; init; }
}

/// <summary>What a client shows on its onboarding screen. <see cref="Imap"/> and <see cref="Smtp"/>
/// are shaped so they can be handed to <c>account.add</c> once the human has confirmed them.</summary>
public sealed record ProviderPresetDto
{
    public required string DisplayName { get; init; }

    public required ImapConfigDto Imap { get; init; }

    public required SmtpConfigDto Smtp { get; init; }

    /// <summary><c>password|appPassword|oauthOnly</c>.</summary>
    public required string Credential { get; init; }

    /// <summary>Where the human creates the credential. Open it with the platform's external opener;
    /// never render it as HTML.</summary>
    public string? CredentialUrl { get; init; }

    /// <summary>One line the human should read before typing a credential.</summary>
    public string? Advice { get; init; }

    /// <summary>True when the hosts were guessed from the domain rather than known, so a client must
    /// show them as editable.</summary>
    public bool IsGuess { get; init; }

    /// <summary>Set when the provider usually refuses an ordinary password. A warning to display, not
    /// a reason to block the attempt.</summary>
    public string? Discouraged { get; init; }
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
