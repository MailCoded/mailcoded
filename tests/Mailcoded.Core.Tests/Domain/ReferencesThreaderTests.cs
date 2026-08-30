using System.Diagnostics;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Threading;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>Threading precedence, fallbacks, determinism, and termination on hostile subjects.</summary>
public sealed class ReferencesThreaderTests
{
    private static readonly Func<MessageId, ThreadKey?> NoLookup = _ => null;

    private static readonly ReferencesThreader Threader = ReferencesThreader.Instance;

    [Fact]
    public void A_native_thread_id_outranks_everything_else()
    {
        var key = Threader.Resolve(
            new ThreadCandidate
            {
                NativeThreadId = "17a3b9c0deadbeef",
                MessageId = Id("own@example.com"),
                References = [Id("root@example.com")],
                InReplyTo = Id("parent@example.com"),
                Subject = "Re: anything",
            },
            NoLookup);

        Assert.Equal(ReferencesThreader.NativePrefix + "17a3b9c0deadbeef", key.Value);
    }

    [Theory]
    [InlineData("has a space")]
    [InlineData("semi;colon")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unusable_native_thread_id_falls_through_to_normal_threading(string native)
    {
        var key = Threader.Resolve(
            new ThreadCandidate { NativeThreadId = native, References = [Id("root@example.com")] },
            NoLookup);

        Assert.Equal(ReferencesThreader.RootPrefix + "root@example.com", key.Value);
    }

    [Fact]
    public void An_over_long_native_thread_id_falls_through_rather_than_being_truncated()
    {
        var key = Threader.Resolve(
            new ThreadCandidate { NativeThreadId = new string('a', 129), References = [Id("root@example.com")] },
            NoLookup);

        Assert.StartsWith(ReferencesThreader.RootPrefix, key.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_references_chain_threads_on_its_root()
    {
        var key = Threader.Resolve(
            new ThreadCandidate
            {
                MessageId = Id("fourth@example.com"),
                References = [Id("root@example.com"), Id("second@example.com"), Id("third@example.com")],
                InReplyTo = Id("third@example.com"),
            },
            NoLookup);

        Assert.Equal(ReferencesThreader.RootPrefix + "root@example.com", key.Value);
    }

    [Fact]
    public void A_known_ancestor_wins_over_minting_a_new_key()
    {
        var known = ThreadKey.Create("m:root@example.com");

        var key = Threader.Resolve(
            new ThreadCandidate
            {
                MessageId = Id("fourth@example.com"),
                References = [Id("unseen@example.com"), Id("second@example.com")],
                InReplyTo = Id("third@example.com"),
            },
            id => id == Id("second@example.com") ? known : null);

        Assert.True(
            key == known,
            "A reply must join the thread its ancestor already belongs to. Minting a fresh key from the References "
            + $"root instead would split the conversation in two. Got: {key}");
    }

    [Fact]
    public void In_reply_to_alone_is_enough_to_thread()
    {
        var key = Threader.Resolve(
            new ThreadCandidate { MessageId = Id("own@example.com"), InReplyTo = Id("parent@example.com") },
            NoLookup);

        Assert.Equal(ReferencesThreader.RootPrefix + "parent@example.com", key.Value);
    }

    [Fact]
    public void A_message_with_no_ancestors_starts_a_thread_on_its_own_message_id()
    {
        var key = Threader.Resolve(new ThreadCandidate { MessageId = Id("own@example.com") }, NoLookup);

        Assert.Equal(ReferencesThreader.RootPrefix + "own@example.com", key.Value);
    }

    [Fact]
    public void A_duplicate_message_id_lands_on_the_same_thread()
    {
        var first = Threader.Resolve(
            new ThreadCandidate { MessageId = Id("dup@example.com"), Subject = "Invoice" },
            NoLookup);
        var second = Threader.Resolve(
            new ThreadCandidate { MessageId = Id("dup@example.com"), Subject = "Completely different subject" },
            NoLookup);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_missing_message_id_falls_back_to_subject_and_participant()
    {
        var key = Threader.Resolve(Orphan("Re: Quarterly report", "Alice <alice@example.com>"), NoLookup);

        Assert.StartsWith(ReferencesThreader.SubjectPrefix, key.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_subject_fallback_ignores_reply_prefixes_and_case()
    {
        var original = Threader.Resolve(Orphan("Quarterly report", "Alice <alice@example.com>"), NoLookup);
        var reply = Threader.Resolve(Orphan("RE: quarterly REPORT", "alice@example.com"), NoLookup);
        var forward = Threader.Resolve(Orphan("Fwd:  Quarterly   report ", "Alice <ALICE@example.com>"), NoLookup);

        Assert.Equal(original, reply);
        Assert.Equal(original, forward);
    }

    [Fact]
    public void The_subject_fallback_separates_different_participants()
    {
        var alice = Threader.Resolve(Orphan("Quarterly report", "alice@example.com"), NoLookup);
        var bob = Threader.Resolve(Orphan("Quarterly report", "bob@example.com"), NoLookup);

        Assert.NotEqual(alice, bob);
    }

    [Fact]
    public void A_message_with_no_id_no_ancestors_and_no_subject_still_gets_a_stable_key()
    {
        var at = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var first = Threader.Resolve(new ThreadCandidate { FromAddress = "alice@example.com", DateUtc = at }, NoLookup);
        var again = Threader.Resolve(new ThreadCandidate { FromAddress = "alice@example.com", DateUtc = at }, NoLookup);
        var later = Threader.Resolve(
            new ThreadCandidate { FromAddress = "alice@example.com", DateUtc = at.AddSeconds(1) },
            NoLookup);

        Assert.StartsWith(ReferencesThreader.SyntheticPrefix, first.Value, StringComparison.Ordinal);
        Assert.Equal(first, again);
        Assert.NotEqual(first, later);
    }

    [Fact]
    public void An_absurdly_long_ancestor_id_is_clamped_not_stored_whole()
    {
        var huge = new string('a', 600) + "@example.com";

        var key = Threader.Resolve(new ThreadCandidate { References = [Id(huge)] }, NoLookup);

        Assert.True(
            key.Value.Length == ReferencesThreader.RootPrefix.Length + 512,
            $"A thread key is an index key: it must be bounded whatever the header carried. Length was {key.Value.Length}.");
    }

    [Fact]
    public void The_same_input_always_produces_the_same_thread_key()
    {
        var candidates = new[]
        {
            new ThreadCandidate { NativeThreadId = "abc123" },
            new ThreadCandidate { References = [Id("root@example.com")] },
            new ThreadCandidate { MessageId = Id("own@example.com") },
            Orphan("Quarterly report", "alice@example.com"),
            new ThreadCandidate { FromAddress = "alice@example.com", DateUtc = DateTimeOffset.UnixEpoch },
        };

        foreach (var candidate in candidates)
        {
            var expected = Threader.Resolve(candidate, NoLookup);

            for (var i = 0; i < 25; i++)
            {
                Assert.True(
                    Threader.Resolve(candidate, NoLookup) == expected,
                    "Threading must be deterministic. A key that varies between runs re-parents every message in "
                    + "the conversation on the next sync.");
            }
        }
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public void A_subject_of_nothing_but_reply_prefixes_terminates(int repeats)
    {
        var subject = string.Concat(Enumerable.Repeat("Re:", repeats));

        var elapsed = Stopwatch.StartNew();
        var normalized = SubjectNormalizer.Normalize(subject);
        var key = Threader.Resolve(Orphan(subject, "alice@example.com"), NoLookup);
        elapsed.Stop();

        Assert.Equal(string.Empty, normalized);
        Assert.StartsWith(ReferencesThreader.SyntheticPrefix, key.Value, StringComparison.Ordinal);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"Stripping {repeats} reply prefixes took {elapsed.Elapsed}. Subject normalization must stay linear: "
            + "a subject is attacker-controlled input, and a quadratic scan here stalls the whole sync.");
    }

    [Theory]
    [InlineData("Re[2]:")]
    [InlineData("Re: Fwd: ")]
    [InlineData("回复:")]
    [InlineData("  Re:\u200b")]
    public void Repeated_prefix_shapes_terminate(string unit)
    {
        var subject = string.Concat(Enumerable.Repeat(unit, 5_000));

        var elapsed = Stopwatch.StartNew();
        var normalized = SubjectNormalizer.Normalize(subject);
        elapsed.Stop();

        Assert.Equal(string.Empty, normalized);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"'{unit}' x5000 took {elapsed.Elapsed}.");
    }

    [Fact]
    public void A_subject_that_is_not_a_prefix_at_all_terminates()
    {
        var elapsed = Stopwatch.StartNew();
        var normalized = SubjectNormalizer.Normalize(new string('R', 200_000));
        elapsed.Stop();

        Assert.Equal(200_000, normalized.Length);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Took {elapsed.Elapsed}.");
    }

    [Fact]
    public void Subject_normalization_collapses_whitespace_and_drops_controls()
    {
        Assert.Equal("hello world", SubjectNormalizer.Normalize("  hello \t\r\n  world  "));
        Assert.Equal(string.Empty, SubjectNormalizer.Normalize(null));
        Assert.Equal(string.Empty, SubjectNormalizer.Normalize("   "));
        Assert.Equal("Quarterly report", SubjectNormalizer.Normalize("Re: Quarterly report"));
    }

    [Fact]
    public void Resolve_rejects_missing_inputs_rather_than_guessing()
    {
        Assert.Throws<ArgumentNullException>(() => Threader.Resolve(null!, NoLookup));
        Assert.Throws<ArgumentNullException>(() => Threader.Resolve(new ThreadCandidate(), null!));
    }

    private static MessageId Id(string raw) => MessageId.Parse(raw);

    private static ThreadCandidate Orphan(string subject, string from) =>
        new() { Subject = subject, FromAddress = from, DateUtc = DateTimeOffset.UnixEpoch };
}
