using System.Text;

namespace Mailcoded.Core.Parsing;

/// <summary>
/// Charset resolution for MIME decoding. Legacy mail carries code pages the BCL does not ship by
/// default, so <see cref="CodePagesEncodingProvider"/> must be registered before the first decode.
/// </summary>
public static class MimeCharsets
{
    private static readonly Lazy<Encoding> Registration =
        new(Register, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Used when a part declares no charset, or declares one nothing can resolve.</summary>
    public static Encoding FallbackEncoding => Registration.Value;

    public static void EnsureRegistered() => _ = Registration.Value;

    /// <summary>Never throws: an unresolvable charset degrades to <see cref="FallbackEncoding"/>.</summary>
    public static Encoding Resolve(string? charset)
    {
        var fallback = Registration.Value;
        if (string.IsNullOrWhiteSpace(charset)) return fallback;

        var name = charset.Trim();
        var semi = name.IndexOf(';');
        if (semi >= 0) name = name[..semi];
        name = name.Trim().Trim('"', '\'').Trim();
        if (name.Length == 0 || name.Length > 64) return fallback;

        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':' or '(' or ')'))
                return fallback;

        var canonical = Canonicalize(name);

        try
        {
            return Encoding.GetEncoding(canonical);
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return fallback;
    }

    private static string Canonicalize(string name) => name.ToLowerInvariant() switch
    {
        "utf8" or "utf_8" or "unicode-1-1-utf-8" or "x-utf-8" => "utf-8",
        "ascii" or "us_ascii" or "usascii" or "ansi_x3.4-1968" or "ansi_x3.4-1986" or "646" or "default" or "unknown-8bit" or "x-unknown" or "x-user-defined" => "us-ascii",
        "latin1" or "latin-1" or "latin_1" or "iso8859-1" or "iso_8859-1" or "iso-8859-1:1987" or "8859-1" or "l1" => "iso-8859-1",
        "latin2" or "iso8859-2" or "iso_8859-2" => "iso-8859-2",
        "latin9" or "iso8859-15" or "iso_8859-15" => "iso-8859-15",
        "cp1250" or "win-1250" or "windows1250" => "windows-1250",
        "cp1251" or "win-1251" or "windows1251" => "windows-1251",
        "cp1252" or "win-1252" or "windows1252" or "ansi" => "windows-1252",
        "cp1253" or "windows1253" => "windows-1253",
        "cp1254" or "windows1254" => "windows-1254",
        "cp1255" or "windows1255" => "windows-1255",
        "cp1256" or "windows1256" => "windows-1256",
        "cp1257" or "windows1257" => "windows-1257",
        "cp1258" or "windows1258" => "windows-1258",
        "ksc5601" or "ks_c_5601" or "ks_c_5601-1989" or "korean" or "cp949" or "uhc" => "ks_c_5601-1987",
        "gb_2312-80" or "chinese" or "csgb2312" => "gb2312",
        "gb18030-2000" => "gb18030",
        "cp936" or "ms936" => "gbk",
        "cp932" or "ms932" or "shift-jis" or "sjis" or "x-sjis" => "shift_jis",
        "cp950" or "ms950" => "big5",
        "koi8" => "koi8-r",
        "utf16" or "utf_16" => "utf-16",
        "utf-7" or "utf7" => "us-ascii",
        _ => name,
    };

    private static Encoding Register()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(1252);
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return Encoding.Latin1;
    }
}
