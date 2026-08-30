using System.Text.Json.Serialization;

namespace Mailcoded.Mcp;

internal sealed record ToolErrorDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("error_code")] public string ErrorCode { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("retry_after_ms")] public long? RetryAfterMs { get; init; }
}

internal sealed record SearchHitDto
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("folder_id")] public long FolderId { get; init; }
    [JsonPropertyName("subject")] public string? Subject { get; init; }
    [JsonPropertyName("from")] public string? From { get; init; }
    [JsonPropertyName("date")] public string Date { get; init; } = string.Empty;
    [JsonPropertyName("flags")] public IReadOnlyList<string> Flags { get; init; } = [];
    [JsonPropertyName("snippet")] public string? Snippet { get; init; }
}

internal sealed record SearchParseErrorDto
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("position")] public int Position { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
}

internal sealed record SearchResultDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("hits")] public IReadOnlyList<SearchHitDto> Hits { get; init; } = [];
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; init; }
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
    [JsonPropertyName("route")] public string Route { get; init; } = string.Empty;
    [JsonPropertyName("query_errors")] public IReadOnlyList<SearchParseErrorDto> QueryErrors { get; init; } = [];
}

internal sealed record ReadResultDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("account_id")] public long AccountId { get; init; }
    [JsonPropertyName("folder_id")] public long FolderId { get; init; }
    [JsonPropertyName("thread_key")] public string? ThreadKey { get; init; }
    [JsonPropertyName("message_id")] public string? MessageId { get; init; }
    [JsonPropertyName("subject")] public string? Subject { get; init; }
    [JsonPropertyName("from")] public string? From { get; init; }
    [JsonPropertyName("to")] public string? To { get; init; }
    [JsonPropertyName("cc")] public string? Cc { get; init; }
    [JsonPropertyName("date")] public string Date { get; init; } = string.Empty;
    [JsonPropertyName("flags")] public IReadOnlyList<string> Flags { get; init; } = [];
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("has_attachments")] public bool HasAttachments { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("body_fetched")] public bool BodyFetched { get; init; }
    [JsonPropertyName("body_format")] public string BodyFormat { get; init; } = "text";
    [JsonPropertyName("body_text")] public string? BodyText { get; init; }
    [JsonPropertyName("parse_warnings")] public IReadOnlyList<string> ParseWarnings { get; init; } = [];
}

internal sealed record ThreadMessageDto
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("folder_id")] public long FolderId { get; init; }
    [JsonPropertyName("subject")] public string? Subject { get; init; }
    [JsonPropertyName("from")] public string? From { get; init; }
    [JsonPropertyName("date")] public string Date { get; init; } = string.Empty;
    [JsonPropertyName("flags")] public IReadOnlyList<string> Flags { get; init; } = [];
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("body_fetched")] public bool BodyFetched { get; init; }
}

internal sealed record ThreadResultDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("thread_key")] public string ThreadKey { get; init; } = string.Empty;
    [JsonPropertyName("messages")] public IReadOnlyList<ThreadMessageDto> Messages { get; init; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; init; }
}

internal sealed record TagResultDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("flags")] public IReadOnlyList<string> Flags { get; init; } = [];
    [JsonPropertyName("pushed_to_server")] public bool PushedToServer { get; init; }
}

internal sealed record SendGateDto
{
    [JsonPropertyName("allowed")] public bool Allowed { get; init; }
    [JsonPropertyName("decision")] public string Decision { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("retry_after_ms")] public long? RetryAfterMs { get; init; }
}

internal sealed record DraftResultDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("account_id")] public long AccountId { get; init; }
    [JsonPropertyName("from")] public string? From { get; init; }
    [JsonPropertyName("to")] public IReadOnlyList<string> To { get; init; } = [];
    [JsonPropertyName("cc")] public IReadOnlyList<string> Cc { get; init; } = [];
    [JsonPropertyName("bcc")] public IReadOnlyList<string> Bcc { get; init; } = [];
    [JsonPropertyName("subject")] public string Subject { get; init; } = string.Empty;
    [JsonPropertyName("body_preview")] public string BodyPreview { get; init; } = string.Empty;
    [JsonPropertyName("body_chars")] public int BodyChars { get; init; }
    [JsonPropertyName("in_reply_to")] public string? InReplyTo { get; init; }
    [JsonPropertyName("persisted")] public bool Persisted { get; init; }
    [JsonPropertyName("send_gate")] public SendGateDto SendGate { get; init; } = new();
    [JsonPropertyName("next_step")] public string NextStep { get; init; } = string.Empty;
}

internal sealed record SendPreviewResultDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("draft_id")] public long DraftId { get; init; }
    [JsonPropertyName("message_id")] public string MessageId { get; init; } = string.Empty;
    [JsonPropertyName("confirm_token")] public string ConfirmToken { get; init; } = string.Empty;
    [JsonPropertyName("confirm_token_lifetime_ms")] public long ConfirmTokenLifetimeMs { get; init; }
    [JsonPropertyName("from")] public string From { get; init; } = string.Empty;
    [JsonPropertyName("to")] public IReadOnlyList<string> To { get; init; } = [];
    [JsonPropertyName("cc")] public IReadOnlyList<string> Cc { get; init; } = [];
    [JsonPropertyName("bcc")] public IReadOnlyList<string> Bcc { get; init; } = [];
    [JsonPropertyName("subject")] public string Subject { get; init; } = string.Empty;
    [JsonPropertyName("body_preview")] public string BodyPreview { get; init; } = string.Empty;
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("requires_smtputf8")] public bool RequiresSmtpUtf8 { get; init; }
    [JsonPropertyName("digest")] public string Digest { get; init; } = string.Empty;
    [JsonPropertyName("send_gate")] public SendGateDto SendGate { get; init; } = new();
    [JsonPropertyName("next_step")] public string NextStep { get; init; } = string.Empty;
}

internal sealed record SendDraftResultDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("draft_id")] public long DraftId { get; init; }
    [JsonPropertyName("message_id")] public string MessageId { get; init; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
    [JsonPropertyName("status_code")] public int StatusCode { get; init; }
    [JsonPropertyName("smtp_response")] public string? SmtpResponse { get; init; }
    [JsonPropertyName("enhanced_status_code")] public string? EnhancedStatusCode { get; init; }
    [JsonPropertyName("attempts")] public int Attempts { get; init; }
    [JsonPropertyName("next_attempt_utc")] public string? NextAttemptUtc { get; init; }
    [JsonPropertyName("appended_to_sent")] public bool AppendedToSent { get; init; }
    [JsonPropertyName("permanently_failed")] public bool PermanentlyFailed { get; init; }
}

internal sealed record StatsFolderDto
{
    [JsonPropertyName("folder_id")] public long FolderId { get; init; }
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("unread")] public int Unread { get; init; }
    [JsonPropertyName("total")] public int Total { get; init; }
    [JsonPropertyName("last_sync_utc")] public string? LastSyncUtc { get; init; }
}

internal sealed record StatsAccountDto
{
    [JsonPropertyName("account_id")] public long AccountId { get; init; }
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("imap")] public string Imap { get; init; } = string.Empty;
    [JsonPropertyName("smtp")] public string Smtp { get; init; } = string.Empty;
    [JsonPropertyName("auth_required")] public bool AuthRequired { get; init; }
    [JsonPropertyName("folders")] public int Folders { get; init; }
    [JsonPropertyName("unread")] public int Unread { get; init; }
}

internal sealed record StatsStoreDto
{
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("wal_bytes")] public long WalBytes { get; init; }
    [JsonPropertyName("blob_bytes")] public long BlobBytes { get; init; }
    [JsonPropertyName("table_counts")] public IReadOnlyDictionary<string, long> TableCounts { get; init; } =
        new Dictionary<string, long>();
}

internal sealed record StatsOutboxDto
{
    [JsonPropertyName("queued")] public int Queued { get; init; }
    [JsonPropertyName("failed")] public int Failed { get; init; }
    [JsonPropertyName("stuck")] public int Stuck { get; init; }
}

internal sealed record StatsAgentDto
{
    [JsonPropertyName("interface")] public string Interface { get; init; } = string.Empty;
    [JsonPropertyName("send_enabled")] public bool SendEnabled { get; init; }
    [JsonPropertyName("approved_recipient_patterns")] public int ApprovedRecipientPatterns { get; init; }
    [JsonPropertyName("remaining_sends_this_hour")] public int RemainingSendsThisHour { get; init; }
    [JsonPropertyName("outstanding_confirm_tokens")] public int OutstandingConfirmTokens { get; init; }
    [JsonPropertyName("html_bodies")] public bool HtmlBodies { get; init; }
}

internal sealed record StatsResultDto
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("secret_backend")] public string SecretBackend { get; init; } = string.Empty;
    [JsonPropertyName("uptime_ms")] public long UptimeMs { get; init; }
    [JsonPropertyName("store")] public StatsStoreDto Store { get; init; } = new();
    [JsonPropertyName("outbox")] public StatsOutboxDto Outbox { get; init; } = new();
    [JsonPropertyName("agent")] public StatsAgentDto Agent { get; init; } = new();
    [JsonPropertyName("accounts")] public IReadOnlyList<StatsAccountDto> Accounts { get; init; } = [];
    [JsonPropertyName("folders")] public IReadOnlyList<StatsFolderDto> Folders { get; init; } = [];
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Serialization,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolErrorDto))]
[JsonSerializable(typeof(SearchResultDto))]
[JsonSerializable(typeof(ReadResultDto))]
[JsonSerializable(typeof(ThreadResultDto))]
[JsonSerializable(typeof(TagResultDto))]
[JsonSerializable(typeof(DraftResultDto))]
[JsonSerializable(typeof(SendPreviewResultDto))]
[JsonSerializable(typeof(SendDraftResultDto))]
[JsonSerializable(typeof(StatsResultDto))]
internal partial class McpJsonContext : JsonSerializerContext
{
}
