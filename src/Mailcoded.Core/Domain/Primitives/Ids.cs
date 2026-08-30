namespace Mailcoded.Core.Domain.Primitives;

/// <summary>Local database row id for an account.</summary>
public readonly record struct AccountId(long Value)
{
    public static readonly AccountId None = new(0);
    public bool IsNone => Value == 0;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Local database row id for a folder.</summary>
public readonly record struct FolderId(long Value)
{
    public static readonly FolderId None = new(0);
    public bool IsNone => Value == 0;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Local database row id for a message. Never interchangeable with a server <see cref="Uid"/>.</summary>
public readonly record struct LocalMessageId(long Value)
{
    public static readonly LocalMessageId None = new(0);
    public bool IsNone => Value == 0;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Local database row id for a blob.</summary>
public readonly record struct BlobId(long Value)
{
    public static readonly BlobId None = new(0);
    public bool IsNone => Value == 0;
}
