namespace Mailcoded.Protocol;

/// <summary>
/// The message summary every list, thread, search hit, and notification renders.
/// Ids are local row ids; <see cref="EnvelopeDto.Date"/> is an ISO 8601 UTC string so no client has to guess
/// a timezone. <see cref="EnvelopeDto.Flags"/> are server IMAP flags, <see cref="EnvelopeDto.Tags"/> are local Tags —
/// the two vocabularies are distinct and never merged.
/// </summary>
public sealed record EnvelopeDto
{
    public required long Id { get; init; }

    public long AccountId { get; init; }

    public required long FolderId { get; init; }

    public string? ThreadKey { get; init; }

    /// <summary>RFC 5322 Message-ID without angle brackets, or null when the message had none.</summary>
    public string? MessageId { get; init; }

    public string? Subject { get; init; }

    /// <summary>Display form of the From header, already decoded. Untrusted attacker input.</summary>
    public string? From { get; init; }

    public string? To { get; init; }

    public string? Cc { get; init; }

    /// <summary>ISO 8601 UTC, e.g. <c>2026-08-30T12:34:56.000Z</c>.</summary>
    public required string Date { get; init; }

    /// <summary>Server flags: <c>unread|flagged|answered|draft|deleted|recent</c>.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>Local notmuch-style tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public bool HasAttachments { get; init; }

    public long Size { get; init; }

    /// <summary>Machine-generated mail (list, bulk, auto-reply, report, noreply sender); absent means unknown, not false. Sender-controlled, so not a trust signal.</summary>
    public bool? Automated { get; init; }

    /// <summary>PGP/MIME, S/MIME envelope or inline PGP armor (never decrypted); inline armor is seen only after the body is fetched. Absent means unknown.</summary>
    public bool? Encrypted { get; init; }

    /// <summary>DMARC result a trusted receiving server reported for the From domain: pass|fail|none|unknown. Verified is not safe. Absent means not evaluated.</summary>
    public string? Dmarc { get; init; }

    /// <summary>True once the body has been fetched and indexed; false means <c>message.get</c> will hit the network.</summary>
    public bool BodyFetched { get; init; }

    /// <summary>Search-result excerpt, present only on hits from <c>search</c> when snippets were requested.</summary>
    public string? Snippet { get; init; }
}

/// <summary>A folder as the client renders it. <see cref="FolderDto.Name"/> has '/' between levels whatever the server's delimiter is.</summary>
public sealed record FolderDto
{
    public required long Id { get; init; }

    public required long AccountId { get; init; }

    public required string Name { get; init; }

    /// <summary><c>inbox|sent|drafts|trash|archive|junk|all</c>, or null when the folder has no special use.</summary>
    public string? Role { get; init; }

    public int Unread { get; init; }

    public int Total { get; init; }

    /// <summary>The server's hierarchy delimiter for this folder, or null when the server has none (a flat namespace).</summary>
    public string? Delimiter { get; init; }

    /// <summary>The last hierarchy level of <see cref="Name"/>. Absent when the daemon sends no hierarchy fields.</summary>
    public string? Leaf { get; init; }

    /// <summary>Hierarchy levels above this folder: 0 for a top-level folder and for every folder of a flat namespace.</summary>
    public int Depth { get; init; }

    /// <summary>The nearest ancestor that is itself a folder; null at the top and when no ancestor is a listed folder.</summary>
    public long? ParentId { get; init; }
}

/// <summary>An account without any credential. <see cref="AccountDto.SecretRef"/> is an opaque handle, never a secret.</summary>
public sealed record AccountDto
{
    public required long Id { get; init; }

    public required string Email { get; init; }

    public string? DisplayName { get; init; }

    /// <summary><c>imap|graph|jmap|gmail</c>.</summary>
    public required string Provider { get; init; }

    /// <summary><c>password|oauth2</c>.</summary>
    public required string Auth { get; init; }

    public ImapConfigDto? Imap { get; init; }

    public SmtpConfigDto? Smtp { get; init; }

    /// <summary>Handle into the OS keyring or encrypted-file vault. Resolving it is the daemon's job alone.</summary>
    public string? SecretRef { get; init; }

    /// <summary>Latched server quirk names, for diagnostics.</summary>
    public IReadOnlyList<string> Quirks { get; init; } = [];

    /// <summary>Extra From addresses this account may send as, besides <see cref="Email"/>, which is always allowed.</summary>
    public IReadOnlyList<IdentityDto> Identities { get; init; } = [];

    public IReadOnlyList<SignatureDto> Signatures { get; init; } = [];

    /// <summary>Name of the signature a new message starts with, when one is set.</summary>
    public string? DefaultSignatureNew { get; init; }

    /// <summary>Name of the signature a reply starts with, when one is set.</summary>
    public string? DefaultSignatureReply { get; init; }

    /// <summary>True when a draft is also appended to the server's Drafts Folder. Off unless the user turned it on.</summary>
    public bool SaveDraftsToServer { get; init; }
}

public sealed record IdentityDto
{
    public required string Address { get; init; }

    public string? DisplayName { get; init; }

    public string? ReplyTo { get; init; }
}

/// <summary>A plaintext signature. <see cref="Text"/> has no separator line; the RFC 3676 <c>-- </c> line goes before it when it is inserted.</summary>
public sealed record SignatureDto
{
    public required string Name { get; init; }

    public required string Text { get; init; }
}

public sealed record ImapConfigDto
{
    public required string Host { get; init; }

    public int Port { get; init; } = 993;

    /// <summary><c>none|sslOnConnect|startTls|startTlsWhenAvailable</c>.</summary>
    public string Security { get; init; } = SecurityModes.SslOnConnect;

    public string? Username { get; init; }

    /// <summary>Folders to hold an IDLE connection on. Empty means INBOX only.</summary>
    public IReadOnlyList<string> WatchFolders { get; init; } = [];
}

public sealed record SmtpConfigDto
{
    public required string Host { get; init; }

    public int Port { get; init; } = 587;

    /// <summary><c>none|sslOnConnect|startTls|startTlsWhenAvailable</c>.</summary>
    public string Security { get; init; } = SecurityModes.StartTls;

    public string? Username { get; init; }
}

/// <summary>One MIME attachment, described but not transferred. Content comes from <c>attachment.get</c>.</summary>
public sealed record AttachmentDto
{
    /// <summary>Stable within one message; the index <c>attachment.get</c> takes.</summary>
    public required int Index { get; init; }

    /// <summary>Already sanitized for use as a save-as name; still never pass it to a shell.</summary>
    public string? Filename { get; init; }

    public required string Mime { get; init; }

    public long Size { get; init; }

    public bool IsInline { get; init; }

    public string? ContentId { get; init; }
}

/// <summary>
/// A message a client wants sent. Plaintext body only — HTML compose and attachment upload are
/// out of scope for v0.1. Addresses are validated into <c>EmailAddress</c> before anything is built.
/// </summary>
public sealed record DraftDto
{
    /// <summary>The account address or one of its identities, else -32602 with reason <c>sender_not_allowed</c>. Omitted, a reply uses the identity the original was sent to.</summary>
    public string? From { get; init; }

    public IReadOnlyList<string> To { get; init; } = [];

    public IReadOnlyList<string> Cc { get; init; } = [];

    public IReadOnlyList<string> Bcc { get; init; } = [];

    public required string Subject { get; init; }

    public required string BodyText { get; init; }

    /// <summary>Message-ID being replied to, without angle brackets.</summary>
    public string? InReplyTo { get; init; }

    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>Local id of the message being replied to; the daemon derives References from it when set.</summary>
    public long? ReplyToMessageId { get; init; }

    /// <summary>The quoted original of a reply. Send it here, not inside <c>bodyText</c>, and a signature lands above it.</summary>
    public string? QuotedText { get; init; }

    /// <summary>Name of one of the account's signatures to insert. Unknown names are rejected with -32602.</summary>
    public string? Signature { get; init; }

    /// <summary>Insert the account's default signature for a new message or a reply. Ignored when <c>signature</c> is set.</summary>
    public bool? UseDefaultSignature { get; init; }
}

/// <summary>Daemon metrics (RELIABILITY §14.6). Nothing here identifies a message or a person.</summary>
public sealed record StatsDto
{
    public required string DaemonVersion { get; init; }

    public int ProtocolVersion { get; init; } = ProtocolConstants.Version;

    public long UptimeMs { get; init; }

    public long WorkingSetBytes { get; init; }

    public long GcHeapBytes { get; init; }

    public long GcCommittedBytes { get; init; }

    public long GcTotalAllocatedBytes { get; init; }

    public int Gen0Collections { get; init; }

    public int Gen1Collections { get; init; }

    public int Gen2Collections { get; init; }

    public int ThreadCount { get; init; }

    public int HandleCount { get; init; }

    /// <summary>IMAP connections currently open across all accounts.</summary>
    public int OpenConnections { get; init; }

    public int SchemaVersion { get; init; }

    public long DatabaseSizeBytes { get; init; }

    public long WalSizeBytes { get; init; }

    public long BlobDirectorySizeBytes { get; init; }

    public IReadOnlyDictionary<string, long> TableCounts { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);

    public IReadOnlyList<FolderSyncStatDto> Folders { get; init; } = [];
}

/// <summary>Per-folder sync position. MODSEQ is a string because it exceeds JavaScript's safe integer range.</summary>
public sealed record FolderSyncStatDto
{
    public required long FolderId { get; init; }

    public required long AccountId { get; init; }

    public required string Name { get; init; }

    /// <summary>Decimal MODSEQ as a string; "0" means the server never reported one.</summary>
    public string HighestModSeq { get; init; } = "0";

    public long? UidNext { get; init; }

    public int Unread { get; init; }

    public int Total { get; init; }

    /// <summary>ISO 8601 UTC of the last successful sync, or null if never synced.</summary>
    public string? LastSyncUtc { get; init; }
}

/// <summary>Liveness and per-account connection/auth state (RELIABILITY §14.6).</summary>
public sealed record HealthDto
{
    /// <summary><c>ok|degraded|error</c>.</summary>
    public required string Status { get; init; }

    public bool StoreOk { get; init; } = true;

    /// <summary>Result of the last <c>PRAGMA quick_check</c>, when one has run.</summary>
    public string? StoreDetail { get; init; }

    public IReadOnlyList<AccountHealthDto> Accounts { get; init; } = [];

    /// <summary>Stable warning slugs, e.g. <c>degraded-sync</c>, <c>wal-growth</c>. Safe to log.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record AccountHealthDto
{
    public required long AccountId { get; init; }

    public required string Email { get; init; }

    /// <summary><c>connected|connecting|disconnected|error</c>.</summary>
    public required string Connection { get; init; }

    /// <summary><c>ok|auth-required|unknown</c>.</summary>
    public required string Auth { get; init; }

    public bool Watching { get; init; }

    public int ConsecutiveFailures { get; init; }

    /// <summary>Last failure, redacted. Never a credential, never mail content.</summary>
    public string? LastError { get; init; }

    /// <summary>ISO 8601 UTC.</summary>
    public string? LastSyncUtc { get; init; }

    public int OutboxQueued { get; init; }

    public int OutboxFailed { get; init; }
}

/// <summary>What the human confirms before a send. The one place recipients are shown in full.</summary>
public sealed record SendPreviewDto
{
    public required string From { get; init; }

    public IReadOnlyList<string> To { get; init; } = [];

    public IReadOnlyList<string> Cc { get; init; } = [];

    public IReadOnlyList<string> Bcc { get; init; } = [];

    public required string Subject { get; init; }

    /// <summary>Plaintext excerpt of the body, truncated for display.</summary>
    public required string BodyPreview { get; init; }

    public bool BodyTruncated { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Pre-assigned at outbox creation and immutable; the idempotency key for the send.</summary>
    public required string MessageId { get; init; }

    public bool RequiresSmtpUtf8 { get; init; }

    /// <summary>Stable slugs, e.g. <c>smtputf8-unsupported</c>, <c>exceeds-server-size-limit</c>.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>What the daemon can do in this session. Absent capabilities are absent features, not toggles.</summary>
public sealed record CapabilitiesDto
{
    public IReadOnlyList<string> Methods { get; init; } = [];

    public IReadOnlyList<string> Notifications { get; init; } = [];

    /// <summary>Provider kinds this build can connect to.</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    public bool Search { get; init; } = true;

    public bool Threading { get; init; } = true;

    public bool Attachments { get; init; } = true;

    public bool Watch { get; init; } = true;

    /// <summary>False unless every send gate is open; a false here means <c>send</c> will return <c>1006</c>.</summary>
    public bool Send { get; init; }

    /// <summary>Read-only, row-capped SQL, off unless MAILCODED_ENABLE_SQL=1.</summary>
    public bool RawSql { get; init; }

    /// <summary>Whether this connection may call <c>store.backup</c> and <c>store.backupVerify</c>.</summary>
    public bool Backup { get; init; }

    /// <summary>False when no embedding model is installed; <c>search</c> with <c>semantic: true</c>
    /// then returns <c>1008</c> rather than quietly answering with lexical hits.</summary>
    public bool Semantic { get; init; }

    /// <summary>True when <c>message.get</c> may return <c>bodyHtml</c>; agent surfaces get plaintext only.</summary>
    public bool HtmlBodies { get; init; } = true;

    public int MaxSearchLimit { get; init; } = 200;

    /// <summary>Which secret backend is active, e.g. <c>libsecret</c>. A backend name, never a secret.</summary>
    public string? SecretBackend { get; init; }
}
