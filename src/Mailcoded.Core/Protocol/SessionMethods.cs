namespace Mailcoded.Core.Protocol;

/// <summary><c>initialize</c> — the first call on every connection.</summary>
public sealed record InitializeParams
{
    public required string ClientName { get; init; }

    public required string ClientVersion { get; init; }

    public int ProtocolVersion { get; init; } = ProtocolConstants.Version;

    /// <summary>Audit origin for <c>sync_log.interface</c>: <c>cli|mcp|rpc|internal</c>.</summary>
    public string? Interface { get; init; }

    /// <summary>Best-effort agent host name for <c>sync_log.agent_host</c>.</summary>
    public string? AgentHost { get; init; }
}

public sealed record InitializeResult
{
    public required string DaemonVersion { get; init; }

    public int ProtocolVersion { get; init; } = ProtocolConstants.Version;

    public required CapabilitiesDto Capabilities { get; init; }
}

/// <summary>
/// <c>secret.set</c> — the only DTO that ever holds a credential, and only inbound.
/// The value goes straight to the secret store; it is never logged, echoed, or persisted elsewhere.
/// </summary>
public sealed record SecretSetParams
{
    /// <summary>The handle an account's <c>secretRef</c> will point at.</summary>
    public required string Ref { get; init; }

    /// <summary>The credential. Redacted from <see cref="ToString"/> so it cannot leak through a log line.</summary>
    public required string Value { get; init; }

    public override string ToString() => $"SecretSetParams {{ Ref = {Ref}, Value = [redacted] }}";
}

public sealed record SecretSetResult
{
    public static readonly SecretSetResult Instance = new();
}

/// <summary><c>stats</c> — daemon metrics.</summary>
public sealed record StatsParams
{
    public static readonly StatsParams Instance = new();
}

public sealed record StatsResult
{
    public required StatsDto Stats { get; init; }
}

/// <summary><c>health</c> — per-account connection and auth state.</summary>
public sealed record HealthParams
{
    public static readonly HealthParams Instance = new();
}

public sealed record HealthResult
{
    public required HealthDto Health { get; init; }
}

/// <summary><c>shutdown</c> — drain and exit.</summary>
public sealed record ShutdownParams
{
    public static readonly ShutdownParams Instance = new();
}

public sealed record ShutdownResult
{
    public static readonly ShutdownResult Instance = new();
}
