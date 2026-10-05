using System.Globalization;
using System.Text.Json;

namespace Mailcoded.Core.Domain.Backup;

public sealed record BackupTableCount(string Name, long Rows);

public sealed record BackupBlobEntry(string Sha256, long Bytes);

/// <summary>What a backup directory claims to contain. It detects corruption, not a forger: it is unsigned.</summary>
public sealed record BackupManifest
{
    public const string FormatName = "mailcoded-backup";
    public const int CurrentFormatVersion = 1;

    public const string ManifestFileName = "manifest.json";
    public const string DatabaseFileName = "store.db";
    public const string BlobDirectoryName = "blobs";

    public const int MaxManifestBytes = 32 * 1024 * 1024;
    public const int MaxBlobEntries = 500_000;
    public const int MaxTables = 256;
    public const int MaxNameChars = 128;
    public const int MaxVersionChars = 64;

    public const string CreatedFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required string MailcodedVersion { get; init; }

    public required int SchemaVersion { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required string DatabaseSha256 { get; init; }

    public required long DatabaseBytes { get; init; }

    public required IReadOnlyList<BackupTableCount> Tables { get; init; }

    public required IReadOnlyList<BackupBlobEntry> Blobs { get; init; }

    public long BlobBytes
    {
        get
        {
            long total = 0;
            foreach (var blob in Blobs) total += blob.Bytes;
            return total;
        }
    }

    public static string BlobRelativePath(string sha256) =>
        BlobDirectoryName + "/" + sha256[..2] + "/" + sha256 + ".blob";

    public static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64) return false;

        foreach (var c in value)
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f')) return false;

        return true;
    }

    public byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("format_version", FormatVersion);
            writer.WriteString("mailcoded_version", MailcodedVersion);
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteString("created_utc", CreatedUtc.ToUniversalTime().ToString(CreatedFormat, CultureInfo.InvariantCulture));

            writer.WriteStartObject("database");
            writer.WriteString("file", DatabaseFileName);
            writer.WriteString("sha256", DatabaseSha256);
            writer.WriteNumber("bytes", DatabaseBytes);
            writer.WriteEndObject();

            writer.WriteStartObject("tables");
            foreach (var table in Tables.OrderBy(static t => t.Name, StringComparer.Ordinal))
                writer.WriteNumber(table.Name, table.Rows);
            writer.WriteEndObject();

            writer.WriteStartObject("external_blobs");
            writer.WriteNumber("count", Blobs.Count);
            writer.WriteNumber("bytes", BlobBytes);
            writer.WriteStartArray("files");
            foreach (var blob in Blobs.OrderBy(static b => b.Sha256, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("sha256", blob.Sha256);
                writer.WriteNumber("bytes", blob.Bytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    public static BackupManifestParse Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxManifestBytes) return BackupManifestParse.Invalid("The manifest is larger than any valid manifest.");

        try
        {
            using var document = JsonDocument.Parse(
                json.ToArray(),
                new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });

            return ReadRoot(document.RootElement);
        }
        catch (JsonException)
        {
            return BackupManifestParse.Invalid("The manifest is not valid JSON.");
        }
    }

    private static BackupManifestParse ReadRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return BackupManifestParse.Invalid("The manifest is not a JSON object.");
        if (!TryIndex(root, out var members, out var duplicate))
            return BackupManifestParse.Invalid($"The manifest repeats the member '{duplicate}'.");

        if (!members.TryGetValue("format", out var format) || format.ValueKind != JsonValueKind.String
            || !string.Equals(format.GetString(), FormatName, StringComparison.Ordinal))
            return BackupManifestParse.Invalid("The manifest is not a mailcoded backup manifest.");

        if (!members.TryGetValue("format_version", out var versionElement) || !TryInt(versionElement, out var formatVersion) || formatVersion < 1)
            return BackupManifestParse.Invalid("The manifest has no valid format_version.");

        if (formatVersion != CurrentFormatVersion)
            return BackupManifestParse.Unsupported(formatVersion);

        if (!TryString(members, "mailcoded_version", MaxVersionChars, out var mailcodedVersion))
            return BackupManifestParse.Invalid("The manifest has no valid mailcoded_version.");

        if (!members.TryGetValue("schema_version", out var schemaElement) || !TryInt(schemaElement, out var schema) || schema < 0 || schema > 100_000)
            return BackupManifestParse.Invalid("The manifest has no valid schema_version.");

        if (!TryString(members, "created_utc", 40, out var createdText)
            || !DateTimeOffset.TryParseExact(
                createdText, CreatedFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created))
            return BackupManifestParse.Invalid("The manifest has no valid created_utc.");

        if (!members.TryGetValue("database", out var database) || database.ValueKind != JsonValueKind.Object)
            return BackupManifestParse.Invalid("The manifest has no database object.");

        if (!TryIndex(database, out var databaseMembers, out duplicate))
            return BackupManifestParse.Invalid($"The database object repeats the member '{duplicate}'.");

        if (!TryString(databaseMembers, "file", 64, out var file) || !string.Equals(file, DatabaseFileName, StringComparison.Ordinal))
            return BackupManifestParse.Invalid("The database file name must be store.db.");

        if (!TryString(databaseMembers, "sha256", 64, out var databaseSha) || !IsSha256Hex(databaseSha))
            return BackupManifestParse.Invalid("The database sha256 is not 64 lower-case hex digits.");

        if (!databaseMembers.TryGetValue("bytes", out var bytesElement) || !TryLong(bytesElement, out var databaseBytes) || databaseBytes < 0)
            return BackupManifestParse.Invalid("The database size is not a non-negative integer.");

        if (!members.TryGetValue("tables", out var tablesElement) || tablesElement.ValueKind != JsonValueKind.Object)
            return BackupManifestParse.Invalid("The manifest has no tables object.");

        var tables = ReadTables(tablesElement, out var tablesError);
        if (tables is null) return BackupManifestParse.Invalid(tablesError!);

        if (!members.TryGetValue("external_blobs", out var blobsElement) || blobsElement.ValueKind != JsonValueKind.Object)
            return BackupManifestParse.Invalid("The manifest has no external_blobs object.");

        var blobs = ReadBlobs(blobsElement, out var blobsError);
        if (blobs is null) return BackupManifestParse.Invalid(blobsError!);

        return BackupManifestParse.Valid(new BackupManifest
        {
            FormatVersion = formatVersion,
            MailcodedVersion = mailcodedVersion,
            SchemaVersion = schema,
            CreatedUtc = created,
            DatabaseSha256 = databaseSha,
            DatabaseBytes = databaseBytes,
            Tables = tables,
            Blobs = blobs,
        });
    }

    private static List<BackupTableCount>? ReadTables(JsonElement element, out string? error)
    {
        error = null;
        var tables = new List<BackupTableCount>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (tables.Count >= MaxTables)
            {
                error = "The manifest lists too many tables.";
                return null;
            }

            var name = property.Name;
            if (name.Length == 0 || name.Length > MaxNameChars || HasControl(name))
            {
                error = "The manifest has a table with an invalid name.";
                return null;
            }

            if (!seen.Add(name))
            {
                error = "The manifest repeats a table name.";
                return null;
            }

            if (!TryLong(property.Value, out var rows) || rows < 0)
            {
                error = "The manifest has a row count that is not a non-negative integer.";
                return null;
            }

            tables.Add(new BackupTableCount(name, rows));
        }

        return tables;
    }

    private static List<BackupBlobEntry>? ReadBlobs(JsonElement element, out string? error)
    {
        error = null;
        if (!TryIndex(element, out var members, out var duplicate))
        {
            error = $"The external_blobs object repeats the member '{duplicate}'.";
            return null;
        }

        if (!members.TryGetValue("count", out var countElement) || !TryLong(countElement, out var count) || count < 0)
        {
            error = "The external blob count is not a non-negative integer.";
            return null;
        }

        if (!members.TryGetValue("bytes", out var bytesElement) || !TryLong(bytesElement, out var totalBytes) || totalBytes < 0)
        {
            error = "The external blob byte total is not a non-negative integer.";
            return null;
        }

        if (!members.TryGetValue("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            error = "The manifest has no external blob list.";
            return null;
        }

        var blobs = new List<BackupBlobEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long sum = 0;

        foreach (var item in files.EnumerateArray())
        {
            if (blobs.Count >= MaxBlobEntries)
            {
                error = "The manifest lists too many external blobs.";
                return null;
            }

            if (item.ValueKind != JsonValueKind.Object || !TryIndex(item, out var fields, out _))
            {
                error = "An external blob entry is not a well-formed object.";
                return null;
            }

            if (!TryString(fields, "sha256", 64, out var sha) || !IsSha256Hex(sha))
            {
                error = "An external blob hash is not 64 lower-case hex digits.";
                return null;
            }

            if (!fields.TryGetValue("bytes", out var size) || !TryLong(size, out var bytes) || bytes < 0)
            {
                error = "An external blob size is not a non-negative integer.";
                return null;
            }

            if (!seen.Add(sha))
            {
                error = "The manifest lists one external blob twice.";
                return null;
            }

            if (bytes > long.MaxValue - sum)
            {
                error = "The external blob sizes overflow.";
                return null;
            }

            sum += bytes;
            blobs.Add(new BackupBlobEntry(sha, bytes));
        }

        if (count != blobs.Count || totalBytes != sum)
        {
            error = "The external blob count or byte total disagrees with the list.";
            return null;
        }

        return blobs;
    }

    private static bool TryIndex(JsonElement element, out Dictionary<string, JsonElement> members, out string? duplicate)
    {
        members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        duplicate = null;

        foreach (var property in element.EnumerateObject())
        {
            if (members.TryAdd(property.Name, property.Value)) continue;

            duplicate = property.Name.Length <= 32 && !HasControl(property.Name) ? property.Name : "?";
            return false;
        }

        return true;
    }

    private static bool TryString(Dictionary<string, JsonElement> members, string name, int maxChars, out string value)
    {
        value = string.Empty;
        if (!members.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String) return false;

        var text = element.GetString();
        if (string.IsNullOrEmpty(text) || text.Length > maxChars || HasControl(text)) return false;

        value = text;
        return true;
    }

    private static bool TryInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool TryLong(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static bool HasControl(string text)
    {
        foreach (var c in text)
            if (char.IsControl(c) || char.IsSurrogate(c)) return true;

        return false;
    }
}

public enum BackupManifestParseStatus
{
    Valid,
    Invalid,
    UnsupportedFormat,
}

public sealed record BackupManifestParse
{
    public required BackupManifestParseStatus Status { get; init; }

    public BackupManifest? Manifest { get; init; }

    public string? Error { get; init; }

    public int? FormatVersion { get; init; }

    public bool IsValid => Status == BackupManifestParseStatus.Valid;

    public static BackupManifestParse Valid(BackupManifest manifest) =>
        new() { Status = BackupManifestParseStatus.Valid, Manifest = manifest };

    public static BackupManifestParse Invalid(string error) =>
        new() { Status = BackupManifestParseStatus.Invalid, Error = error };

    public static BackupManifestParse Unsupported(int version) =>
        new()
        {
            Status = BackupManifestParseStatus.UnsupportedFormat,
            FormatVersion = version,
            Error = string.Create(
                CultureInfo.InvariantCulture,
                $"This backup uses manifest format {version}; this build reads format {BackupManifest.CurrentFormatVersion}."),
        };
}
