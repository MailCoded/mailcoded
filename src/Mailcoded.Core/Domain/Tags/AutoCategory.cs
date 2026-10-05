using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Domain.Tags;

/// <summary>The built-in sender categories a Message may be given once, when it first arrives.</summary>
public enum AutoCategory
{
    Social,
    News,
    Marketing,
    Notifications,
}

/// <summary>The reserved Tag names for <see cref="AutoCategory"/>. They are local-only: never pushed as IMAP keywords.</summary>
public static class AutoCategoryTags
{
    public const string Prefix = "auto.";

    private static readonly Tag SocialTag = Tag.Parse("auto.social");
    private static readonly Tag NewsTag = Tag.Parse("auto.news");
    private static readonly Tag MarketingTag = Tag.Parse("auto.marketing");
    private static readonly Tag NotificationsTag = Tag.Parse("auto.notifications");

    public static IReadOnlyList<Tag> All { get; } = [SocialTag, NewsTag, MarketingTag, NotificationsTag];

    public static Tag TagFor(AutoCategory category) => category switch
    {
        AutoCategory.Social => SocialTag,
        AutoCategory.News => NewsTag,
        AutoCategory.Marketing => MarketingTag,
        AutoCategory.Notifications => NotificationsTag,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown category."),
    };

    public static bool IsReserved(Tag tag) => tag.Value.StartsWith(Prefix, StringComparison.Ordinal);
}
