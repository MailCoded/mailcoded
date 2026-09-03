using System.Text.Json.Serialization;

namespace Mailcoded.Protocol;

/// <summary>
/// The one serializer context for the RPC surface. Native AOT forbids reflection-based
/// serialization, so every type that crosses the wire is registered here — a type that is not
/// listed will fail at runtime, not at build time.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]

// JSON-RPC envelope
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(RpcErrorData))]

// Shared DTOs
[JsonSerializable(typeof(EnvelopeDto))]
[JsonSerializable(typeof(FolderDto))]
[JsonSerializable(typeof(AccountDto))]
[JsonSerializable(typeof(ImapConfigDto))]
[JsonSerializable(typeof(SmtpConfigDto))]
[JsonSerializable(typeof(AuthDto))]
[JsonSerializable(typeof(AttachmentDto))]
[JsonSerializable(typeof(DraftDto))]
[JsonSerializable(typeof(StatsDto))]
[JsonSerializable(typeof(FolderSyncStatDto))]
[JsonSerializable(typeof(HealthDto))]
[JsonSerializable(typeof(AccountHealthDto))]
[JsonSerializable(typeof(SendPreviewDto))]
[JsonSerializable(typeof(CapabilitiesDto))]

// Methods
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(SecretSetParams))]
[JsonSerializable(typeof(SecretSetResult))]
[JsonSerializable(typeof(AccountAddParams))]
[JsonSerializable(typeof(AccountAddResult))]
[JsonSerializable(typeof(AccountListParams))]
[JsonSerializable(typeof(AccountListResult))]
[JsonSerializable(typeof(FolderListParams))]
[JsonSerializable(typeof(FolderListResult))]
[JsonSerializable(typeof(SyncParams))]
[JsonSerializable(typeof(SyncResult))]
[JsonSerializable(typeof(SearchParams))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(ThreadGetParams))]
[JsonSerializable(typeof(ThreadGetResult))]
[JsonSerializable(typeof(MessageGetParams))]
[JsonSerializable(typeof(MessageGetResult))]
[JsonSerializable(typeof(AttachmentGetParams))]
[JsonSerializable(typeof(AttachmentGetResult))]
[JsonSerializable(typeof(TagsSetParams))]
[JsonSerializable(typeof(TagsSetResult))]
[JsonSerializable(typeof(MessageMoveParams))]
[JsonSerializable(typeof(MessageMoveResult))]
[JsonSerializable(typeof(SendPreviewParams))]
[JsonSerializable(typeof(SendPreviewResult))]
[JsonSerializable(typeof(SendParams))]
[JsonSerializable(typeof(SendResult))]
[JsonSerializable(typeof(WatchSubscribeParams))]
[JsonSerializable(typeof(WatchSubscribeResult))]
[JsonSerializable(typeof(StatsParams))]
[JsonSerializable(typeof(StatsResult))]
[JsonSerializable(typeof(HealthParams))]
[JsonSerializable(typeof(HealthResult))]
[JsonSerializable(typeof(ShutdownParams))]
[JsonSerializable(typeof(ShutdownResult))]

// Notifications
[JsonSerializable(typeof(MailAddedNotification))]
[JsonSerializable(typeof(FolderUpdatedNotification))]
[JsonSerializable(typeof(SyncErrorNotification))]

// Collection forms used on the wire
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<long>))]
[JsonSerializable(typeof(IReadOnlyList<EnvelopeDto>))]
[JsonSerializable(typeof(IReadOnlyList<FolderDto>))]
[JsonSerializable(typeof(IReadOnlyList<AccountDto>))]
[JsonSerializable(typeof(IReadOnlyList<AttachmentDto>))]
[JsonSerializable(typeof(IReadOnlyList<FolderSyncStatDto>))]
[JsonSerializable(typeof(IReadOnlyList<AccountHealthDto>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, long>))]
public partial class ProtocolJsonContext : JsonSerializerContext
{
}
