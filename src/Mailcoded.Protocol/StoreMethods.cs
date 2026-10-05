namespace Mailcoded.Protocol;

/// <summary><c>store.backup</c> — write a verifiable copy of the store into a new or empty directory.</summary>
public sealed record StoreBackupParams
{
    /// <summary>Absolute path. Must not exist or must be empty, and must lie outside the live store and every workspace folder.</summary>
    public required string Directory { get; init; }
}

public sealed record StoreBackupResult
{
    public required BackupDto Backup { get; init; }

    /// <summary>Always false: secrets are never copied, so a restore needs every account signed in again.</summary>
    public bool SecretsIncluded { get; init; }
}

/// <summary>What the manifest of a backup records.</summary>
public sealed record BackupDto
{
    public required string Directory { get; init; }

    public required int FormatVersion { get; init; }

    public required string MailcodedVersion { get; init; }

    public required int SchemaVersion { get; init; }

    public required string CreatedUtc { get; init; }

    public required long DatabaseBytes { get; init; }

    public required string DatabaseSha256 { get; init; }

    public required long ExternalBlobCount { get; init; }

    public required long ExternalBlobBytes { get; init; }

    public IReadOnlyDictionary<string, long> TableCounts { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);
}

/// <summary><c>store.backupVerify</c> — re-read a backup and compare it with its manifest.</summary>
public sealed record StoreBackupVerifyParams
{
    public required string Directory { get; init; }
}

public sealed record StoreBackupVerifyResult
{
    /// <summary>True only when no problem was found. A mismatch is a result, not an error, so every problem is reported.</summary>
    public required bool Ok { get; init; }

    public required string Directory { get; init; }

    public int? FormatVersion { get; init; }

    public int? SchemaVersion { get; init; }

    public string? CreatedUtc { get; init; }

    /// <summary><c>ok</c>, the integrity_check messages, or <c>not run</c> when no database could be read.</summary>
    public required string Integrity { get; init; }

    public long BlobFilesChecked { get; init; }

    public long BytesChecked { get; init; }

    public int ProblemCount { get; init; }

    public bool ProblemsTruncated { get; init; }

    public IReadOnlyList<BackupProblemDto> Problems { get; init; } = [];
}

public sealed record BackupProblemDto
{
    /// <summary>A stable slug such as <c>database_hash</c>, <c>blob_missing</c> or <c>row_count</c>.</summary>
    public required string Kind { get; init; }

    public string? Subject { get; init; }

    public required string Message { get; init; }
}
