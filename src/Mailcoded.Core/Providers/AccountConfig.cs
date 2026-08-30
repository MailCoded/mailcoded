using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Providers;

/// <summary>
/// Everything needed to reach an account <em>except</em> the credential itself, which is
/// referenced by <see cref="SecretRef"/> and lives only in an <see cref="Secrets.ISecretStore"/>.
/// This record is serialized into <c>accounts.config_json</c>, so adding a secret-bearing
/// field to it would breach SPEC invariant 3.
/// </summary>
public sealed record AccountConfig
{
    public AccountId Id { get; init; } = AccountId.None;
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    public required ProviderKind Provider { get; init; }

    public required ImapConfig Imap { get; init; }
    public SmtpConfig? Smtp { get; init; }

    public required AuthKind Auth { get; init; }

    /// <summary>Opaque handle into the secret store. Never a credential.</summary>
    public required string SecretRef { get; init; }

    /// <summary>Latched per-server workarounds, learned at runtime and persisted.</summary>
    public ServerQuirksConfig Quirks { get; init; } = new();
}

public sealed record ImapConfig
{
    public required string Host { get; init; }
    public int Port { get; init; } = 993;
    public SecureSocket Security { get; init; } = SecureSocket.SslOnConnect;
    public string? Username { get; init; }

    /// <summary>Folders to hold an IDLE connection on. Empty means INBOX only.</summary>
    public IReadOnlyList<string> WatchFolders { get; init; } = [];
}

public sealed record SmtpConfig
{
    public required string Host { get; init; }
    public int Port { get; init; } = 587;
    public SecureSocket Security { get; init; } = SecureSocket.StartTls;
    public string? Username { get; init; }
}

public sealed record ServerQuirksConfig
{
    public Domain.Sync.ServerQuirks Latched { get; init; } = Domain.Sync.ServerQuirks.None;

    /// <summary>SHA-256 of a self-signed certificate the user pinned for a localhost bridge (edge case 10).</summary>
    public string? PinnedCertificateSha256 { get; init; }
}

public enum ProviderKind { Imap, Graph, Jmap, Gmail }

public enum AuthKind { Password, OAuth2 }

public enum SecureSocket { None, SslOnConnect, StartTls, StartTlsWhenAvailable }

public static class ProviderKindExtensions
{
    public static string ToWireValue(this ProviderKind kind) => kind switch
    {
        ProviderKind.Imap => "imap",
        ProviderKind.Graph => "graph",
        ProviderKind.Jmap => "jmap",
        ProviderKind.Gmail => "gmail",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static ProviderKind FromWireValue(string value) => value switch
    {
        "imap" => ProviderKind.Imap,
        "graph" => ProviderKind.Graph,
        "jmap" => ProviderKind.Jmap,
        "gmail" => ProviderKind.Gmail,
        _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown provider '{value}'."),
    };
}
