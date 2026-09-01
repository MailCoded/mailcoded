using Mailcoded.Core.Domain.Primitives;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

/// <summary>§14.5 case 16: this type is the CR/LF header-injection gate at compose time.</summary>
public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("victim@example.com\r\nBcc: attacker@evil.com")]
    [InlineData("victim@example.com\nBcc: attacker@evil.com")]
    [InlineData("victim@example.com\rBcc: attacker@evil.com")]
    [InlineData("victim\r\n@example.com")]
    [InlineData("\"quoted\r\nname\"@example.com")]
    [InlineData("victim@example.com\r\n\r\nDATA")]
    [InlineData("victim@exa\r\nmple.com")]
    [InlineData("victim@example.com\u0000")]
    [InlineData("victim\u0000@example.com")]
    [InlineData("vic\u0007tim@example.com")]
    [InlineData("vic\u000btim@example.com")]
    [InlineData("vic\u001btim@example.com")]
    public void Every_header_injection_shape_is_rejected(string raw)
    {
        Assert.False(
            EmailAddress.TryParse(raw, out _),
            "§14.5 case 16 and CLAUDE invariant 4: an address is untrusted attacker input. A value of this type "
            + "can never carry a line break, a NUL or a control character into a header, because the only way to "
            + "construct one is through this parser.");
    }

    [Fact]
    public void A_parsed_address_never_contains_a_control_character()
    {
        var random = new Random(12345);
        var alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_+\r\n\t\0 ,;@<>\"'".ToCharArray();

        for (var i = 0; i < 2_000; i++)
        {
            var length = random.Next(1, 40);
            var chars = new char[length];
            for (var c = 0; c < length; c++) chars[c] = alphabet[random.Next(alphabet.Length)];

            if (!EmailAddress.TryParse(new string(chars), out var address)) continue;

            foreach (var ch in address.Value)
            {
                Assert.True(
                    ch is not ('\r' or '\n' or '\0') && !char.IsControl(ch),
                    $"Parsed '{address.Value}' still carries U+{(int)ch:X4}. Anything this parser accepts is written "
                    + "straight into a header, so a single escape here is a header-injection vulnerability.");
            }
        }
    }

    [Fact]
    public void Trailing_line_breaks_are_trimmed_rather_than_smuggled_through()
    {
        Assert.True(EmailAddress.TryParse("user@example.com\r\n", out var address));
        Assert.Equal("user@example.com", address.Value);
    }

    [Theory]
    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@localhost")]
    [InlineData("user@.example.com")]
    [InlineData("user@example.com.")]
    [InlineData("user@exa..mple.com")]
    [InlineData("user@-example.com")]
    [InlineData("user@example.com-")]
    [InlineData("user@exa mple.com")]
    [InlineData("user@exa_mple.com")]
    [InlineData("user name@example.com")]
    [InlineData("user,other@example.com")]
    [InlineData("user;other@example.com")]
    public void Malformed_addresses_are_rejected(string? raw)
    {
        Assert.False(EmailAddress.TryParse(raw, out _));
    }

    [Fact]
    public void Over_long_addresses_are_rejected()
    {
        Assert.False(EmailAddress.TryParse(new string('a', 65) + "@example.com", out _));
        Assert.False(EmailAddress.TryParse("user@" + new string('a', 260) + ".com", out _));
        Assert.False(EmailAddress.TryParse(new string('a', 400) + "@example.com", out _));

        Assert.False(
            EmailAddress.TryParse(new string('a', 64) + "@" + new string('b', 251) + ".com", out _),
            "Both parts sit at their own limits and the address is still 320 characters, which no SMTP path may "
            + "carry. The total length is its own rule, not the sum of the 64 and 255 part limits.");
    }

    [Fact]
    public void The_longest_legal_address_still_parses()
    {
        var longest = new string('a', 64) + "@" + new string('b', 185) + ".com";

        Assert.Equal(EmailAddress.MaxLength, longest.Length);
        Assert.True(EmailAddress.TryParse(longest, out _));
    }

    [Theory]
    [InlineData("a@b@c.com")]
    [InlineData("victim@example.com%0d%0aBcc:attacker@evil.com")]
    [InlineData("user@@example.com")]
    [InlineData("@user@example.com")]
    [InlineData("user@example.com@")]
    public void An_address_with_more_than_one_unquoted_at_sign_is_rejected(string raw)
    {
        Assert.False(
            EmailAddress.TryParse(raw, out _),
            "The parser split on the LAST '@' while LocalPart and Domain split on the first, so an accepted value "
            + "reported parts that were never the ones validated. An addr-spec has exactly one separator.");
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last+tag@sub.example.co.nz")]
    [InlineData("\"quoted@local\"@example.com")]
    [InlineData("a@b.co")]
    public void The_accessors_report_the_parts_the_parser_validated(string raw)
    {
        var address = EmailAddress.Parse(raw);

        Assert.Equal(address.Value, string.Concat(address.LocalPart, "@", address.Domain));
        Assert.False(address.Domain.Contains('@'), "the domain swallowed the separator");
        Assert.True(address.LocalPart.Length is > 0 and <= EmailAddress.MaxLocalPartLength);
        Assert.True(address.Domain.Length is > 0 and <= EmailAddress.MaxDomainLength);
    }

    [Fact]
    public void The_domain_is_lower_cased_and_the_local_part_is_left_alone()
    {
        var address = EmailAddress.Parse("  First.Last@EXAMPLE.CO.NZ  ");

        Assert.Equal("First.Last@example.co.nz", address.Value);
        Assert.Equal("First.Last", address.LocalPart);
        Assert.Equal("example.co.nz", address.Domain);
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last+tag@sub.example.co.nz")]
    [InlineData("a@b.co")]
    [InlineData("user_name-1@example-host.com")]
    public void A_well_formed_address_splits_back_into_exactly_what_it_holds(string raw)
    {
        var address = EmailAddress.Parse(raw);

        Assert.Equal(address.Value, $"{address.LocalPart}@{address.Domain}");
    }

    [Fact]
    public void Equality_is_by_normalized_value()
    {
        Assert.Equal(EmailAddress.Parse("User@Example.com"), EmailAddress.Parse("User@EXAMPLE.COM"));
        Assert.NotEqual(EmailAddress.Parse("user@example.com"), EmailAddress.Parse("User@example.com"));
    }

    [Fact]
    public void Smtputf8_is_required_only_for_a_non_ascii_address()
    {
        Assert.False(EmailAddress.Parse("user@example.com").RequiresSmtpUtf8());
        Assert.True(
            EmailAddress.Parse("usér@example.com").RequiresSmtpUtf8(),
            "§14.5 case 29: an EAI address must be detected before submission so the sender can fail loudly when "
            + "the server has no SMTPUTF8, instead of silently mangling the recipient.");
    }

    [Fact]
    public void Parse_throws_where_TryParse_returns_false()
    {
        Assert.Throws<FormatException>(() => EmailAddress.Parse("user@example.com\r\nBcc: x@y.com"));
        Assert.Throws<FormatException>(() => EmailAddress.Parse(string.Empty));
    }
}
