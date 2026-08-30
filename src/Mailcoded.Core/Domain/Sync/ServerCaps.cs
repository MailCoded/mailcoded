namespace Mailcoded.Core.Domain.Sync;

/// <summary>Capabilities advertised by the server, plus the quirks we have learned not to trust them.</summary>
public sealed record ServerCaps
{
    public bool Condstore { get; init; }
    public bool Qresync { get; init; }
    public bool Utf8Accept { get; init; }
    public bool SpecialUse { get; init; }
    public bool Move { get; init; }
    public bool Idle { get; init; }
    public bool GmailExtensions { get; init; }
    public ServerQuirks Quirks { get; init; } = ServerQuirks.None;

    public static readonly ServerCaps None = new();
}

/// <summary>
/// Per-server workarounds, latched once observed. A quirk always makes the planner
/// <em>more</em> conservative — it never enables a faster path.
/// </summary>
[Flags]
public enum ServerQuirks
{
    None = 0,

    /// <summary>Advertises QRESYNC but the extension misbehaves — iCloud throws on open, some servers omit VANISHED.</summary>
    QresyncBroken = 1 << 0,

    /// <summary>MODSEQ values were observed non-monotonic, so CONDSTORE deltas cannot be trusted.</summary>
    CondstoreBroken = 1 << 1,

    /// <summary>Requires an IMAP ID command before it will accept other commands (Yahoo).</summary>
    RequiresId = 1 << 2,

    /// <summary>No real <c>\Deleted</c> semantics; archive means removing a label (Gmail).</summary>
    NoDeletedFlag = 1 << 3,

    /// <summary>Low simultaneous-connection cap; watch fewer folders.</summary>
    LowConnectionLimit = 1 << 4,

    /// <summary>Localhost bridge with a self-signed certificate the user has pinned (ProtonBridge).</summary>
    SelfSignedLocalhost = 1 << 5,
}
