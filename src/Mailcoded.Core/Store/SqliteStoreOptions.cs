namespace Mailcoded.Core.Store;

/// <summary>Open-time configuration for <see cref="SqliteStore"/>. Values default to RELIABILITY §14.3.</summary>
public sealed record SqliteStoreOptions
{
    /// <summary>Full path to store.db. When null it is derived from <see cref="DataDirectory"/>.</summary>
    public string? DatabasePath { get; init; }

    /// <summary>Store root. When null the per-OS default from SPEC §5.3 is used.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Where externalized blobs land. When null it is &lt;DataDirectory&gt;/blobs.</summary>
    public string? BlobDirectory { get; init; }

    public int ReaderPoolSize { get; init; } = 4;

    /// <summary>Blobs at or below this size are stored inline; larger ones go to a content-addressed file.</summary>
    public long InlineBlobThresholdBytes { get; init; } = 512 * 1024;

    public int BusyTimeoutMs { get; init; } = 5000;

    /// <summary>Negative KiB form of PRAGMA cache_size; 16 MB by default.</summary>
    public int CacheSizeKiB { get; init; } = 16384;

    /// <summary>PRAGMA mmap_size. Set to 0 on a network filesystem — never mmap one.</summary>
    public long MmapSizeBytes { get; init; } = 268_435_456;

    public int WalAutoCheckpointPages { get; init; } = 1000;

    public long JournalSizeLimitBytes { get; init; } = 67_108_864;

    /// <summary>Applied only when the database file is being created; null leaves the SQLite default.</summary>
    public int? PageSize { get; init; }

    /// <summary>Cache size used only inside a bulk-ingest window (PERFORMANCE §15.5), ~256 MB.</summary>
    public int BulkCacheSizeKiB { get; init; } = 262_144;

    /// <summary>Rows between periodic commits during bulk ingest — caps WAL growth, not a speed knob.</summary>
    public int BulkCommitInterval { get; init; } = 8000;

    /// <summary>Deepest page a relevance-ordered search may reach before it reports truncation.</summary>
    public int MaxSearchOffset { get; init; } = 1000;
}
