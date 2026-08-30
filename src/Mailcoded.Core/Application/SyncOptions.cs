using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;

namespace Mailcoded.Core.Application;

/// <summary>Knobs for the imperative shell. None of them change what the pure planner decides.</summary>
public sealed record SyncOptions
{
    /// <summary>How many times one folder may re-plan for a quirk or a UIDVALIDITY flip.</summary>
    public int MaxReplans { get; init; } = 4;

    /// <summary>Safety valve for a provider that streams without ever checkpointing.</summary>
    public int MaxEventsPerFlush { get; init; } = 2_000;

    /// <summary>Backfill windows one call will walk before leaving the rest for the next sync.</summary>
    public int MaxBackfillWindows { get; init; } = 200;

    /// <summary>Sync opens folders read-only; a writable SELECT is never needed to read state.</summary>
    public bool OpenWritable { get; init; }

    /// <summary>Push locally-known custom tags back as IMAP keywords when the server accepts them.</summary>
    public bool PushTagDivergence { get; init; }

    public bool ReconcileFolders { get; init; } = true;

    public static readonly SyncOptions Default = new();
}

/// <summary>Progress for the initial-sync UI. Counts are cumulative within the current run.</summary>
public sealed record SyncProgress
{
    public required string Phase { get; init; }
    public FolderId FolderId { get; init; } = FolderId.None;
    public string FolderPath { get; init; } = string.Empty;
    public int FoldersDone { get; init; }
    public int FolderCount { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Expunged { get; init; }
}

public sealed record FolderSyncReport
{
    public required FolderId FolderId { get; init; }
    public required string Path { get; init; }
    public string Plan { get; init; } = "up-to-date";
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Expunged { get; init; }
    public int Batches { get; init; }
    public bool Degraded { get; init; }
    public ServerQuirks LatchedQuirks { get; init; } = ServerQuirks.None;
}

/// <summary>What the <c>sync</c> RPC reports back.</summary>
public sealed record SyncReport
{
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Expunged { get; init; }
    public int Batches { get; init; }
    public int Folders { get; init; }
    public long DurationMs { get; init; }
    public bool Degraded { get; init; }
    public ServerQuirks LatchedQuirks { get; init; } = ServerQuirks.None;
    public IReadOnlyList<FolderSyncReport> FolderReports { get; init; } = [];

    public static readonly SyncReport Empty = new();
}

/// <summary>Result of folding a provider LIST into the folders table (edge case 20).</summary>
public sealed record FolderReconcileReport
{
    public int Listed { get; init; }
    public int Known { get; init; }

    /// <summary>Folders the server no longer lists. Reported and logged; never removed here.</summary>
    public IReadOnlyList<string> OrphanedPaths { get; init; } = [];
}
