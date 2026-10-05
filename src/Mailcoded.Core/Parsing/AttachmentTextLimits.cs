namespace Mailcoded.Core.Parsing;

/// <summary>Every bound is enforced against bytes actually read, never a size the sender declared.</summary>
public sealed record AttachmentTextLimits
{
    public static AttachmentTextLimits Default { get; } = new();

    public int MaxAttachments { get; init; } = 16;

    public int MaxTextBytes { get; init; } = 1_048_576;

    public int MaxDocxBytes { get; init; } = 20 * 1_048_576;

    public int MaxZipEntries { get; init; } = 4_096;

    public int MaxXmlPartBytes { get; init; } = 16 * 1_048_576;

    public int MaxInflatedBytes { get; init; } = 24 * 1_048_576;

    public int MaxCompressionRatio { get; init; } = 200;

    public int MaxCharsPerAttachment { get; init; } = 256_000;

    public int MaxCharsPerMessage { get; init; } = 512_000;

    public long MaxDecodedBytesPerMessage { get; init; } = 64L * 1_048_576;

    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(5);
}
