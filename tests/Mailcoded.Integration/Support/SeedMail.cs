using System.Globalization;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Parsing;

namespace Mailcoded.Integration.Support;

/// <summary>Deterministic RFC822 test messages, built through the product's own composer.</summary>
public static class SeedMail
{
    public static readonly DateTimeOffset Epoch = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public static byte[] Build(string mailbox, string subject, string body, int index)
    {
        var recipient = EmailAddress.Parse(mailbox);
        var sender = EmailAddress.Parse("sender" + index.ToString(CultureInfo.InvariantCulture) + "@remote.test");

        var spec = new DraftSpec
        {
            From = sender,
            FromDisplayName = "Remote Sender " + index.ToString(CultureInfo.InvariantCulture),
            To = [recipient],
            Subject = subject,
            BodyText = body,
            MessageId = MessageId.Parse($"seed-{index.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}@remote.test"),
            DateUtc = Epoch.AddMinutes(index),
        };

        return MessageBuilder.Build(spec);
    }

    /// <summary>A small corpus where exactly one message carries <paramref name="needle"/>.</summary>
    public static IReadOnlyList<byte[]> Corpus(string mailbox, int count, string needle)
    {
        var messages = new List<byte[]>(count);

        for (var i = 0; i < count; i++)
        {
            var isNeedle = i == count / 2;
            var subject = isNeedle
                ? "Quarterly report " + needle
                : "Routine notice " + i.ToString(CultureInfo.InvariantCulture);
            var body = isNeedle
                ? $"The {needle} appears exactly once in this corpus so a search can assert on it."
                : "Filler body for message " + i.ToString(CultureInfo.InvariantCulture) + ".";

            messages.Add(Build(mailbox, subject, body, i));
        }

        return messages;
    }
}
