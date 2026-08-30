using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailcoded.Core.Protocol;

/// <summary>
/// A JSON-RPC 2.0 request. <see cref="JsonRpcRequest.Id"/> is absent on a notification and may be a number or a
/// string, so it stays an untyped <see cref="JsonElement"/> and is echoed back verbatim.
/// </summary>
public sealed record JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = ProtocolConstants.JsonRpcVersion;

    public JsonElement? Id { get; init; }

    public required string Method { get; init; }

    /// <summary>Left undecoded so the dispatcher can pick the source-generated type for the method.</summary>
    public JsonElement? Params { get; init; }

    [JsonIgnore]
    public bool IsNotification => Id is null;
}

/// <summary>
/// A JSON-RPC 2.0 response. Exactly one of <see cref="JsonRpcResponse.Result"/> and <see cref="JsonRpcResponse.Error"/> is written;
/// hosts that care about allocation should stream the result with <c>Utf8JsonWriter</c> instead.
/// </summary>
public sealed record JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = ProtocolConstants.JsonRpcVersion;

    public JsonElement? Id { get; init; }

    public JsonElement? Result { get; init; }

    public JsonRpcError? Error { get; init; }
}

/// <summary>A JSON-RPC 2.0 error object. Neither field ever carries a secret or mail content.</summary>
public sealed record JsonRpcError
{
    public required int Code { get; init; }

    /// <summary>Short, stable, human-readable. Never interpolate a header, body, or credential into it.</summary>
    public required string Message { get; init; }

    public RpcErrorData? Data { get; init; }
}

/// <summary>Machine-readable detail attached to an error so a client can branch without parsing prose.</summary>
public sealed record RpcErrorData
{
    /// <summary>Adapter failure category: <c>network|protocol|auth|busy|full|notFound|unsupported</c>.</summary>
    public string? Category { get; init; }

    /// <summary>Extra context that is safe to display. Never mail content, never a credential.</summary>
    public string? Detail { get; init; }

    /// <summary>How long to wait before retrying, when the daemon can estimate it.</summary>
    public int? RetryAfterMs { get; init; }

    /// <summary>True when no retry will help until the user re-authenticates or changes settings.</summary>
    public bool? RequiresUserAction { get; init; }

    public long? AccountId { get; init; }

    public long? FolderId { get; init; }
}

/// <summary>A JSON-RPC 2.0 notification: no id, no response.</summary>
public sealed record JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = ProtocolConstants.JsonRpcVersion;

    public required string Method { get; init; }

    public JsonElement? Params { get; init; }
}
