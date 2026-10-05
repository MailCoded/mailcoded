using System.Text;
using Mailcoded.Core.Domain.Backup;
using Xunit;

namespace Mailcoded.Core.Tests.Domain;

public sealed class BackupManifestTests
{
    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);

    private static BackupManifest Sample() => new()
    {
        MailcodedVersion = "0.1.0",
        SchemaVersion = 11,
        CreatedUtc = new DateTimeOffset(2026, 10, 4, 1, 2, 3, 456, TimeSpan.Zero),
        DatabaseSha256 = new string('d', 64),
        DatabaseBytes = 12_288,
        Tables = [new BackupTableCount("messages", 7), new BackupTableCount("accounts", 1)],
        Blobs = [new BackupBlobEntry(ShaB, 200), new BackupBlobEntry(ShaA, 100)],
    };

    private static string Json(string tables = "{\"messages\":1}", string files = "[{\"sha256\":\"" + "a" + "\",\"bytes\":1}]", string extra = "") =>
        "{\"format\":\"mailcoded-backup\",\"format_version\":1,\"mailcoded_version\":\"0.1.0\",\"schema_version\":11,"
        + "\"created_utc\":\"2026-10-04T01:02:03.456Z\",\"database\":{\"file\":\"store.db\",\"sha256\":\"" + new string('d', 64) + "\",\"bytes\":10},"
        + "\"tables\":" + tables + ",\"external_blobs\":{\"count\":0,\"bytes\":0,\"files\":[]}" + extra + "}";

    private static BackupManifestParse Parse(string json) => BackupManifest.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void A_manifest_survives_a_write_and_a_parse()
    {
        var parsed = BackupManifest.Parse(Sample().ToJson());

        Assert.True(parsed.IsValid, parsed.Error);
        var manifest = parsed.Manifest!;
        Assert.Equal(11, manifest.SchemaVersion);
        Assert.Equal("0.1.0", manifest.MailcodedVersion);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 1, 2, 3, 456, TimeSpan.Zero), manifest.CreatedUtc);
        Assert.Equal(12_288, manifest.DatabaseBytes);
        Assert.Equal(300, manifest.BlobBytes);
        Assert.Equal(["accounts", "messages"], manifest.Tables.Select(static t => t.Name).ToArray());
        Assert.Equal([ShaA, ShaB], manifest.Blobs.Select(static b => b.Sha256).ToArray());
    }

    [Fact]
    public void Writing_is_deterministic_whatever_the_input_order()
    {
        var one = Sample();
        var two = one with { Tables = one.Tables.Reverse().ToList(), Blobs = one.Blobs.Reverse().ToList() };

        Assert.Equal(one.ToJson(), two.ToJson());
    }

    [Fact]
    public void The_blob_path_is_derived_from_the_hash_alone()
    {
        Assert.Equal("blobs/aa/" + ShaA + ".blob", BackupManifest.BlobRelativePath(ShaA));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg", false)]
    [InlineData("../../../../../../../../../../../../../../../../../../../../../../", false)]
    public void Only_lower_case_sha256_hex_is_accepted(string value, bool expected) =>
        Assert.Equal(expected, BackupManifest.IsSha256Hex(value));

    [Fact]
    public void A_sha256_of_the_right_shape_is_accepted()
    {
        Assert.True(BackupManifest.IsSha256Hex(ShaA));
        Assert.False(BackupManifest.IsSha256Hex(null));
        Assert.False(BackupManifest.IsSha256Hex(ShaA + "0"));
    }

    [Fact]
    public void Garbage_and_the_wrong_json_shape_are_invalid_not_exceptions()
    {
        foreach (var text in new[] { "", "   ", "not json", "[]", "null", "42", "\"x\"", "{", "{}", "{\"format\":1}", "\uFEFF{}" })
        {
            var result = Parse(text);
            Assert.False(result.IsValid, text);
            Assert.Equal(BackupManifestParseStatus.Invalid, result.Status);
        }
    }

    [Fact]
    public void A_manifest_for_a_different_format_is_not_ours()
    {
        Assert.False(Parse(Json().Replace("mailcoded-backup", "other-backup", StringComparison.Ordinal)).IsValid);
    }

    [Fact]
    public void A_newer_format_version_is_reported_as_unsupported_before_anything_else_is_read()
    {
        var result = Parse("{\"format\":\"mailcoded-backup\",\"format_version\":2,\"anything\":\"goes\"}");

        Assert.Equal(BackupManifestParseStatus.UnsupportedFormat, result.Status);
        Assert.Equal(2, result.FormatVersion);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("99999999999999999999")]
    public void A_format_version_that_is_not_a_positive_integer_is_invalid(string version)
    {
        var result = Parse(Json().Replace("\"format_version\":1", "\"format_version\":" + version, StringComparison.Ordinal));

        Assert.False(result.IsValid);
        Assert.Equal(BackupManifestParseStatus.Invalid, result.Status);
    }

    [Fact]
    public void Duplicate_members_are_rejected_at_every_level()
    {
        Assert.False(Parse(Json().Replace("\"schema_version\":11,", "\"schema_version\":11,\"schema_version\":3,", StringComparison.Ordinal)).IsValid);
        Assert.False(Parse(Json(tables: "{\"messages\":1,\"messages\":2}")).IsValid);
        Assert.False(Parse(Json().Replace("\"file\":\"store.db\",", "\"file\":\"store.db\",\"file\":\"x\",", StringComparison.Ordinal)).IsValid);
    }

    [Theory]
    [InlineData("\"file\":\"store.db\"", "\"file\":\"../store.db\"")]
    [InlineData("\"file\":\"store.db\"", "\"file\":\"other.db\"")]
    [InlineData("\"file\":\"store.db\"", "\"file\":\"/etc/passwd\"")]
    public void The_database_file_name_is_fixed_and_never_taken_from_the_manifest(string from, string to) =>
        Assert.False(Parse(Json().Replace(from, to, StringComparison.Ordinal)).IsValid);

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"7\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("9223372036854775808")]
    public void A_row_count_must_be_a_non_negative_integer(string value) =>
        Assert.False(Parse(Json(tables: "{\"messages\":" + value + "}")).IsValid);

    [Fact]
    public void A_table_name_must_be_short_and_free_of_control_characters()
    {
        Assert.False(Parse(Json(tables: "{\"\":1}")).IsValid);
        Assert.False(Parse(Json(tables: "{\"" + new string('t', 129) + "\":1}")).IsValid);
        Assert.False(Parse(Json(tables: "{\"a\\u0000b\":1}")).IsValid);
        Assert.False(Parse(Json(tables: "{\"a\\nb\":1}")).IsValid);
        Assert.True(Parse(Json(tables: "{\"" + new string('t', 128) + "\":1}")).IsValid);
    }

    [Fact]
    public void Too_many_tables_are_rejected()
    {
        var tables = "{" + string.Join(",", Enumerable.Range(0, BackupManifest.MaxTables + 1).Select(i => $"\"t{i}\":1")) + "}";

        Assert.False(Parse(Json(tables: tables)).IsValid);
    }

    [Theory]
    [InlineData("\"sha256\":\"zz\"", "2")]
    [InlineData("\"sha256\":\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\"", "2")]
    [InlineData("\"sha256\":\"../../x\"", "2")]
    [InlineData("\"sha256\":null", "2")]
    public void A_blob_hash_that_is_not_lower_case_hex_is_rejected(string hash, string _)
    {
        var json = BlobJson("{" + hash + ",\"bytes\":5}", count: 1, bytes: 5);

        Assert.False(Parse(json).IsValid);
    }

    [Fact]
    public void The_blob_count_and_total_must_agree_with_the_list()
    {
        Assert.True(Parse(BlobJson("{\"sha256\":\"" + ShaA + "\",\"bytes\":5}", 1, 5)).IsValid);
        Assert.False(Parse(BlobJson("{\"sha256\":\"" + ShaA + "\",\"bytes\":5}", 2, 5)).IsValid);
        Assert.False(Parse(BlobJson("{\"sha256\":\"" + ShaA + "\",\"bytes\":5}", 1, 6)).IsValid);
        Assert.False(Parse(BlobJson("{\"sha256\":\"" + ShaA + "\",\"bytes\":5}", 0, 0)).IsValid);
    }

    [Fact]
    public void One_blob_listed_twice_is_rejected()
    {
        var entry = "{\"sha256\":\"" + ShaA + "\",\"bytes\":5}";

        Assert.False(Parse(BlobJson(entry + "," + entry, 2, 10)).IsValid);
    }

    [Fact]
    public void Sizes_that_overflow_when_added_are_rejected()
    {
        var one = "{\"sha256\":\"" + ShaA + "\",\"bytes\":9223372036854775807}";
        var two = "{\"sha256\":\"" + ShaB + "\",\"bytes\":9223372036854775807}";

        Assert.False(Parse(BlobJson(one + "," + two, 2, long.MaxValue)).IsValid);
    }

    [Fact]
    public void Blob_entries_that_are_not_objects_are_rejected()
    {
        Assert.False(Parse(BlobJson("\"x\"", 1, 0)).IsValid);
        Assert.False(Parse(BlobJson("[]", 1, 0)).IsValid);
        Assert.False(Parse(BlobJson("null", 1, 0)).IsValid);
    }

    [Fact]
    public void Deeply_nested_input_is_refused_not_stack_overflowed()
    {
        var nest = new string('[', 5_000) + new string(']', 5_000);

        Assert.False(Parse(Json(extra: ",\"x\":" + nest)).IsValid);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_not_json_and_are_refused()
    {
        Assert.False(Parse(Json().Replace("\"schema_version\":11", "\"schema_version\":11/*x*/", StringComparison.Ordinal)).IsValid);
        Assert.False(Parse(Json().TrimEnd('}') + ",}").IsValid);
    }

    [Fact]
    public void An_oversized_manifest_is_refused_without_parsing()
    {
        var bytes = new byte[BackupManifest.MaxManifestBytes + 1];

        Assert.False(BackupManifest.Parse(bytes).IsValid);
    }

    [Fact]
    public void A_timestamp_that_is_not_the_documented_form_is_rejected()
    {
        foreach (var bad in new[] { "2026-10-04", "2026-10-04T01:02:03Z", "2026-10-04T01:02:03.456+13:00", "yesterday", "2026-13-04T01:02:03.456Z" })
            Assert.False(Parse(Json().Replace("2026-10-04T01:02:03.456Z", bad, StringComparison.Ordinal)).IsValid, bad);
    }

    [Fact]
    public void A_schema_version_outside_a_sane_range_is_rejected()
    {
        Assert.False(Parse(Json().Replace("\"schema_version\":11", "\"schema_version\":-1", StringComparison.Ordinal)).IsValid);
        Assert.False(Parse(Json().Replace("\"schema_version\":11", "\"schema_version\":100001", StringComparison.Ordinal)).IsValid);
    }

    [Fact]
    public void Unknown_members_are_ignored_so_a_later_minor_addition_still_reads()
    {
        var result = Parse(Json(extra: ",\"note\":\"hello\"") );

        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void Error_text_never_echoes_attacker_chosen_content()
    {
        var result = Parse(Json(tables: "{\"evil\\u001b[2Jname\":-1}"));

        Assert.False(result.IsValid);
        Assert.DoesNotContain("evil", result.Error!, StringComparison.Ordinal);
    }

    private static string BlobJson(string files, long count, long bytes) =>
        "{\"format\":\"mailcoded-backup\",\"format_version\":1,\"mailcoded_version\":\"0.1.0\",\"schema_version\":11,"
        + "\"created_utc\":\"2026-10-04T01:02:03.456Z\",\"database\":{\"file\":\"store.db\",\"sha256\":\"" + new string('d', 64) + "\",\"bytes\":10},"
        + "\"tables\":{},\"external_blobs\":{\"count\":" + count + ",\"bytes\":" + bytes + ",\"files\":[" + files + "]}}";
}
