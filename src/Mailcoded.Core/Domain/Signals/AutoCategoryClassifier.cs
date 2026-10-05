using Mailcoded.Core.Domain.Tags;

namespace Mailcoded.Core.Domain.Signals;

/// <summary>
/// Fixed, built-in heuristics over plain values. The From address is unauthenticated, so a category
/// says how a message presents itself, not who sent it. At most one category; nothing is configurable.
/// </summary>
public static class AutoCategoryClassifier
{
    public const int MaxSubjectChars = 300;

    private static readonly string[] SocialDomains =
    [
        "facebookmail.com", "facebook.com", "linkedin.com", "twitter.com", "x.com", "instagram.com",
        "pinterest.com", "reddit.com", "redditmail.com", "tiktok.com", "snapchat.com", "discord.com",
        "quora.com", "tumblr.com", "nextdoor.com", "threads.net", "bsky.app", "mastodon.social",
    ];

    private static readonly string[] NewsletterDomains =
    [
        "substack.com", "beehiiv.com", "buttondown.email", "ghost.io", "medium.com", "convertkit.com",
    ];

    private static readonly string[] MarketingDomains =
    [
        "mailchimp.com", "mailchimpapp.net", "mcsv.net", "campaign-archive.com", "list-manage.com",
        "constantcontact.com", "createsend.com", "klaviyo.com", "klaviyomail.com", "sendinblue.com",
        "brevo.com", "hubspotemail.net", "mailerlite.com", "getresponse.com", "aweber.com",
        "activecampaign.com", "omnisend.com", "campaignmonitor.com",
    ];

    private static readonly string[] NotificationDomains =
    [
        "github.com", "gitlab.com", "bitbucket.org", "atlassian.net", "atlassian.com", "slack.com",
        "asana.com", "trello.com", "linear.app", "pagerduty.com", "sentry.io", "circleci.com",
    ];

    private static readonly string[] NotificationLocalParts = ["notification", "notifications", "notify", "alert", "alerts"];

    private static readonly string[] MarketingHints =
    [
        "% off", "sale", "discount", "coupon", "promo code", "free shipping", "limited time", "special offer",
        "exclusive offer", "last chance", "flash sale", "black friday", "cyber monday", "save up to",
    ];

    private static readonly string[] NewsHints = ["newsletter", "digest", "roundup", "round-up"];

    private static readonly string[] ListIdNewsTokens = ["newsletter", "newsletters", "news", "digest"];

    /// <summary>Domain lists decide first; subject hints and notification-style senders apply only to
    /// automated mail, so a person's "sale of my car" is never categorised.</summary>
    public static AutoCategory? Classify(string? from, string? listId, bool? automated, string? subject)
    {
        var domain = ValidDomain(MessageSignals.FirstDomain(from));
        var listDomain = ListIdDomain(listId);

        if (domain is not null)
        {
            if (InDomains(domain, SocialDomains)) return AutoCategory.Social;
            if (InDomains(domain, NewsletterDomains)) return AutoCategory.News;
            if (InDomains(domain, MarketingDomains)) return AutoCategory.Marketing;
            if (InDomains(domain, NotificationDomains)) return AutoCategory.Notifications;
        }

        if (listDomain is not null)
        {
            if (InDomains(listDomain, NewsletterDomains)) return AutoCategory.News;
            if (InDomains(listDomain, MarketingDomains)) return AutoCategory.Marketing;
        }

        if (automated != true) return null;

        var text = Lower(subject);
        if (ContainsAny(text, MarketingHints)) return AutoCategory.Marketing;
        if (ContainsAny(text, NewsHints) || ListIdSaysNews(listId)) return AutoCategory.News;

        var local = MessageSignals.FirstLocalPart(from);
        if (local is not null && MessageSignals.HasLocalPartPrefix(local, NotificationLocalParts))
            return AutoCategory.Notifications;

        return null;
    }

    private static string Lower(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return string.Empty;
        var bounded = subject.Length > MaxSubjectChars ? subject[..MaxSubjectChars] : subject;
        return bounded.ToLowerInvariant();
    }

    private static bool InDomains(string domain, string[] list)
    {
        foreach (var entry in list)
        {
            if (!domain.EndsWith(entry, StringComparison.Ordinal)) continue;
            if (domain.Length == entry.Length || domain[domain.Length - entry.Length - 1] == '.') return true;
        }

        return false;
    }

    private static string? ValidDomain(string? domain)
    {
        if (domain is null || domain.Length > 253) return null;

        foreach (var c in domain)
        {
            var ok = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_';
            if (!ok) return null;
        }

        return domain;
    }

    private static string? ListIdDomain(string? listId)
    {
        if (string.IsNullOrWhiteSpace(listId)) return null;

        var open = listId.LastIndexOf('<');
        var close = listId.LastIndexOf('>');
        var body = open >= 0 && close > open ? listId[(open + 1)..close] : listId;

        var trimmed = body.Trim().TrimEnd('.').ToLowerInvariant();
        return ValidDomain(trimmed.Length == 0 ? null : trimmed);
    }

    private static bool ListIdSaysNews(string? listId)
    {
        if (string.IsNullOrWhiteSpace(listId)) return false;

        var bounded = listId.Length > MaxSubjectChars ? listId[..MaxSubjectChars] : listId;
        var text = bounded.ToLowerInvariant();

        foreach (var token in ListIdNewsTokens)
            if (ContainsPhrase(text, token)) return true;

        return false;
    }

    private static bool ContainsAny(string text, string[] phrases)
    {
        if (text.Length == 0) return false;

        foreach (var phrase in phrases)
            if (ContainsPhrase(text, phrase)) return true;

        return false;
    }

    /// <summary>Case-folded substring match that needs a non-alphanumeric neighbour wherever the phrase itself starts or ends in a letter or digit.</summary>
    private static bool ContainsPhrase(string text, string phrase)
    {
        var from = 0;
        while (from <= text.Length - phrase.Length)
        {
            var at = text.IndexOf(phrase, from, StringComparison.Ordinal);
            if (at < 0) return false;

            var end = at + phrase.Length;
            var leftOk = !char.IsLetterOrDigit(phrase[0]) || at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var rightOk = !char.IsLetterOrDigit(phrase[^1]) || end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftOk && rightOk) return true;

            from = at + 1;
        }

        return false;
    }
}
