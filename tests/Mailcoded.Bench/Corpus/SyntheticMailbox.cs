using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Bench.Corpus;

public sealed record SyntheticMessage
{
    public required int Index { get; init; }
    public required int FolderIndex { get; init; }
    public required uint Uid { get; init; }
    public required DateTimeOffset DateUtc { get; init; }
    public required string MessageId { get; init; }
    public string? InReplyTo { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required MessageFlags Flags { get; init; }
    public required long Size { get; init; }
    public required bool HasAttachment { get; init; }
    public required bool IsCjk { get; init; }
}

/// <summary>The seeded generator from PERFORMANCE §15.8: single-threaded and side-effect free, so
/// one seed always yields the same sequence and therefore the same <see cref="Fingerprint"/>.</summary>
public sealed class SyntheticMailbox
{
    private readonly CorpusOptions _options;

    public SyntheticMailbox(CorpusOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public static IReadOnlyList<string> FolderPaths { get; } = BuildFolderPaths();

    public IEnumerable<SyntheticMessage> Generate()
    {
        var random = new DeterministicRandom(_options.Seed);
        var folderUids = new uint[CorpusVocabulary.Folders.Length];
        var recent = new List<string>[CorpusVocabulary.Folders.Length];
        for (var i = 0; i < recent.Length; i++) recent[i] = new List<string>(64);

        var timestamp = _options.Start;

        for (var index = 0; index < _options.Size; index++)
        {
            timestamp = timestamp.AddMilliseconds(random.Next(1, _options.MeanGapMs * 2));

            var folderIndex = PickFolder(random);
            var uid = ++folderUids[folderIndex];
            var isCjk = random.Chance(_options.CjkFraction);

            var messageId = string.Create(
                CultureInfo.InvariantCulture,
                $"{index}.{folderIndex}@{random.Pick(CorpusVocabulary.Domains)}");

            string? inReplyTo = null;
            var window = recent[folderIndex];
            if (window.Count > 0 && random.Chance(_options.ReplyFraction)) inReplyTo = random.Pick(window);

            var subject = isCjk ? CjkSubject(random) : LatinSubject(random, inReplyTo is not null);
            var body = isCjk ? CjkBody(random) : LatinBody(random, index);
            var person = random.Pick(CorpusVocabulary.People);
            var domain = random.Pick(CorpusVocabulary.Domains);

            var flags = MessageFlags.None;
            if (random.Chance(0.22)) flags |= MessageFlags.Unread;
            if (random.Chance(0.05)) flags |= MessageFlags.Flagged;
            if (inReplyTo is not null && random.Chance(0.40)) flags |= MessageFlags.Answered;
            if (string.Equals(CorpusVocabulary.Folders[folderIndex].Path, "Drafts", StringComparison.Ordinal))
                flags |= MessageFlags.Draft;

            var hasAttachment = random.Chance(0.12);

            window.Add(messageId);
            if (window.Count > 64) window.RemoveAt(0);

            yield return new SyntheticMessage
            {
                Index = index,
                FolderIndex = folderIndex,
                Uid = uid,
                DateUtc = timestamp,
                MessageId = messageId,
                InReplyTo = inReplyTo,
                Subject = subject,
                Body = body,
                From = person + "@" + domain,
                To = _options.Mailbox,
                Flags = flags,
                Size = (body.Length * 2L) + 2_048 + (hasAttachment ? 180_000 : 0),
                HasAttachment = hasAttachment,
                IsCjk = isCjk,
            };
        }
    }

    /// <summary>A hash of the canonical record stream, so the reproducibility claim is checkable.</summary>
    public string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new StringBuilder(512);

        foreach (var message in Generate())
        {
            buffer.Clear();
            Field(buffer, message.Index.ToString(CultureInfo.InvariantCulture));
            Field(buffer, message.FolderIndex.ToString(CultureInfo.InvariantCulture));
            Field(buffer, message.Uid.ToString(CultureInfo.InvariantCulture));
            Field(buffer, message.DateUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            Field(buffer, message.MessageId);
            Field(buffer, message.InReplyTo ?? string.Empty);
            Field(buffer, message.Subject);
            Field(buffer, message.Body);
            Field(buffer, message.From);
            Field(buffer, message.To);
            Field(buffer, ((int)message.Flags).ToString(CultureInfo.InvariantCulture));
            Field(buffer, message.Size.ToString(CultureInfo.InvariantCulture));
            Field(buffer, message.HasAttachment ? "1" : "0");

            hash.AppendData(Encoding.UTF8.GetBytes(buffer.ToString()));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Field(StringBuilder builder, string value) => builder.Append(value).Append('\u001f');

    private static int PickFolder(DeterministicRandom random)
    {
        var roll = random.NextDouble();
        var cumulative = 0.0;

        for (var i = 0; i < CorpusVocabulary.Folders.Length; i++)
        {
            cumulative += CorpusVocabulary.Folders[i].Share;
            if (roll < cumulative) return i;
        }

        return CorpusVocabulary.Folders.Length - 1;
    }

    private static string LatinSubject(DeterministicRandom random, bool isReply)
    {
        var words = random.Next(3, 10);
        var builder = new StringBuilder(96);
        if (isReply) builder.Append("Re: ");

        for (var i = 0; i < words; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(random.Pick(CorpusVocabulary.SubjectWords));
        }

        return builder.ToString();
    }

    private static string CjkSubject(DeterministicRandom random)
    {
        var words = random.Next(2, 5);
        var builder = new StringBuilder(48);
        for (var i = 0; i < words; i++) builder.Append(random.Pick(CorpusVocabulary.CjkSubjectWords));
        return builder.ToString();
    }

    /// <summary>Bodies run 40–400 words; every twelfth ends with the gated phrase.</summary>
    private static string LatinBody(DeterministicRandom random, int index)
    {
        var words = 40 + random.Next(361);
        var builder = new StringBuilder(words * 7);

        for (var i = 0; i < words; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(random.Pick(CorpusVocabulary.BodyWords));
        }

        if (index % 12 == 0) builder.Append(' ').Append(CorpusVocabulary.Phrase);
        return builder.ToString();
    }

    private static string CjkBody(DeterministicRandom random)
    {
        var sentences = random.Next(6, 25);
        var builder = new StringBuilder(sentences * 24);

        for (var i = 0; i < sentences; i++)
        {
            builder.Append(random.Pick(CorpusVocabulary.CjkBodyWords));
            builder.Append('。');
        }

        return builder.ToString();
    }

    private static string[] BuildFolderPaths()
    {
        var paths = new string[CorpusVocabulary.Folders.Length];
        for (var i = 0; i < paths.Length; i++) paths[i] = CorpusVocabulary.Folders[i].Path;
        return paths;
    }
}
