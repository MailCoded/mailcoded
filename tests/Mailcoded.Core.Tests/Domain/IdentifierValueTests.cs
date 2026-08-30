using System.Reflection;
using Mailcoded.Core.Domain.Primitives;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>Uid, UidValidity, ModSeq, the row ids, MessageId and ThreadKey.</summary>
public sealed class IdentifierValueTests
{
    [Fact]
    public void A_uid_of_zero_cannot_exist()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Uid(0));

        Assert.False(
            Uid.TryCreate(0, out _),
            "IMAP UIDs start at 1, so 0 is the value a missing or unparsed UID degrades into. Letting it through "
            + "would make 'no UID' indistinguishable from 'the first message'.");
        Assert.False(Uid.TryParse("0", out _));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("4294967295", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("+1", false)]
    [InlineData(" 1", false)]
    [InlineData("1 ", false)]
    [InlineData("1.0", false)]
    [InlineData("4294967296", false)]
    [InlineData("", false)]
    [InlineData((string?)null, false)]
    [InlineData("0x1", false)]
    public void Uid_parsing_accepts_only_a_bare_positive_integer(string? raw, bool expected)
    {
        Assert.Equal(expected, Uid.TryParse(raw, out _));
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(1L, true)]
    [InlineData(4294967295L, true)]
    [InlineData(4294967296L, false)]
    public void Uid_creation_from_a_wider_integer_is_range_checked(long value, bool expected)
    {
        Assert.Equal(expected, Uid.TryCreate(value, out _));
    }

    [Fact]
    public void Uids_order_within_one_epoch()
    {
        var low = new Uid(1);
        var high = new Uid(2);

        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low <= new Uid(1));
        Assert.True(high >= new Uid(2));
        Assert.True(low.CompareTo(high) < 0);
        Assert.Equal("42", new Uid(42).ToString());
    }

    [Fact]
    public void UidValidity_is_equality_only_by_design()
    {
        var type = typeof(UidValidity);

        Assert.Null(type.GetMethod("op_LessThan", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(type.GetMethod("op_GreaterThan", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(type.GetMethod("CompareTo", BindingFlags.Public | BindingFlags.Instance));

        Assert.DoesNotContain(typeof(IComparable), type.GetInterfaces());
        Assert.DoesNotContain(typeof(IComparable<UidValidity>), type.GetInterfaces());
    }

    [Fact]
    public void UidValidity_compares_only_for_equality()
    {
        Assert.Equal(new UidValidity(7), new UidValidity(7));
        Assert.NotEqual(new UidValidity(7), new UidValidity(8));
        Assert.True(UidValidity.Unknown.IsUnknown);
        Assert.Equal(new UidValidity(0), UidValidity.Unknown);
        Assert.False(new UidValidity(1).IsUnknown);
        Assert.Equal("7", new UidValidity(7).ToString());
    }

    [Fact]
    public void ModSeq_zero_means_unknown_and_the_maximum_is_monotonic()
    {
        Assert.True(ModSeq.Zero.IsUnknown);
        Assert.False(new ModSeq(1).IsUnknown);

        Assert.True(new ModSeq(1) < new ModSeq(2));
        Assert.True(new ModSeq(2) > new ModSeq(1));
        Assert.True(new ModSeq(2) >= new ModSeq(2));
        Assert.True(new ModSeq(2) <= new ModSeq(2));

        Assert.Equal(new ModSeq(9), ModSeq.Max(new ModSeq(9), new ModSeq(4)));
        Assert.Equal(new ModSeq(9), ModSeq.Max(new ModSeq(4), new ModSeq(9)));
        Assert.Equal("18446744073709551615", new ModSeq(ulong.MaxValue).ToString());
    }

    [Fact]
    public void The_row_ids_treat_zero_as_absent_and_never_mix()
    {
        Assert.True(AccountId.None.IsNone);
        Assert.True(FolderId.None.IsNone);
        Assert.True(LocalMessageId.None.IsNone);
        Assert.True(BlobId.None.IsNone);

        Assert.False(new AccountId(1).IsNone);
        Assert.Equal("1", new AccountId(1).ToString());
        Assert.Equal("2", new FolderId(2).ToString());
        Assert.Equal("3", new LocalMessageId(3).ToString());

        Assert.NotEqual(typeof(LocalMessageId), typeof(Uid));
    }

    [Theory]
    [InlineData("<abc@example.com>", "abc@example.com")]
    [InlineData("abc@example.com", "abc@example.com")]
    [InlineData("  <Abc@EXAMPLE.COM>  ", "Abc@example.com")]
    [InlineData("<CaseKept@Example.Com>", "CaseKept@example.com")]
    [InlineData("no-domain-part", "no-domain-part")]
    public void A_message_id_is_stored_without_brackets_and_with_a_lower_cased_domain(string raw, string expected)
    {
        Assert.True(MessageId.TryParse(raw, out var id));
        Assert.Equal(expected, id.Value);
    }

    [Theory]
    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>")]
    [InlineData("< >")]
    [InlineData("a b@example.com")]
    [InlineData("a\u0009b@example.com")]
    [InlineData("a\rb@example.com")]
    [InlineData("a\nb@example.com")]
    [InlineData("a<b@example.com")]
    [InlineData("a>b@example.com")]
    [InlineData("<<abc@example.com>>")]
    [InlineData("a\u0000b@example.com")]
    public void A_malformed_message_id_is_rejected(string? raw)
    {
        Assert.False(
            MessageId.TryParse(raw, out _),
            "The Message-ID is the outbox idempotency key and the Sent-folder de-duplication key. A value carrying "
            + "whitespace or brackets would compare unequal to the same id read back from a header.");
    }

    [Fact]
    public void A_message_id_round_trips_through_its_header_form()
    {
        var id = MessageId.Parse("<Abc.123@Example.COM>");

        Assert.Equal("<Abc.123@example.com>", id.ToHeaderValue());
        Assert.Equal(id, MessageId.Parse(id.ToHeaderValue()));
        Assert.Equal(id, MessageId.Parse(id.Value));
        Assert.Equal(id.Value, id.ToString());
    }

    [Fact]
    public void A_minted_message_id_is_deterministic_in_its_seed_and_domain()
    {
        var seed = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var first = MessageId.NewForDomain("Example.COM", seed);
        var second = MessageId.NewForDomain("example.com", seed);

        Assert.Equal("00000000000000000000000000000001@example.com", first.Value);
        Assert.Equal(first, second);
        Assert.NotEqual(first, MessageId.NewForDomain("example.com", Guid.Parse("00000000-0000-0000-0000-000000000002")));
    }

    [Fact]
    public void A_minted_message_id_falls_back_to_a_local_domain()
    {
        var id = MessageId.NewForDomain("   ", Guid.Empty);

        Assert.EndsWith("@mailcoded.local", id.Value, StringComparison.Ordinal);
        Assert.True(MessageId.TryParse(id.ToHeaderValue(), out var reparsed));
        Assert.Equal(id, reparsed);
    }

    [Fact]
    public void Message_id_parse_throws_where_TryParse_returns_false()
    {
        Assert.Throws<FormatException>(() => MessageId.Parse("<>"));
    }

    [Fact]
    public void A_thread_key_must_be_non_empty_and_is_trimmed()
    {
        Assert.False(ThreadKey.TryCreate(null, out _));
        Assert.False(ThreadKey.TryCreate("", out _));
        Assert.False(ThreadKey.TryCreate("   ", out _));

        Assert.True(ThreadKey.TryCreate("  m:root@example.com  ", out var key));
        Assert.Equal("m:root@example.com", key.Value);
        Assert.Equal(key.Value, key.ToString());

        Assert.Throws<ArgumentException>(() => ThreadKey.Create("  "));
    }

    [Fact]
    public void The_message_flag_bits_are_a_stable_storage_contract()
    {
        Assert.Equal(0, (int)MessageFlags.None);
        Assert.Equal(1, (int)MessageFlags.Unread);
        Assert.Equal(2, (int)MessageFlags.Flagged);
        Assert.Equal(4, (int)MessageFlags.Answered);
        Assert.Equal(8, (int)MessageFlags.Draft);
        Assert.Equal(16, (int)MessageFlags.Deleted);
        Assert.Equal(32, (int)MessageFlags.Recent);
    }

    [Fact]
    public void Folder_roles_round_trip_through_the_wire()
    {
        foreach (var role in Enum.GetValues<FolderRole>())
        {
            var wire = role.ToWireValue();

            if (role == FolderRole.None)
            {
                Assert.Null(wire);
                continue;
            }

            Assert.Equal(role, FolderRoleExtensions.FromWireValue(wire));
        }

        Assert.Equal(FolderRole.None, FolderRoleExtensions.FromWireValue(null));
        Assert.Equal(FolderRole.None, FolderRoleExtensions.FromWireValue("not-a-role"));
    }
}
