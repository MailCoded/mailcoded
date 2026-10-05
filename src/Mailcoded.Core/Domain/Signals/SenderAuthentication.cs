namespace Mailcoded.Core.Domain.Signals;

/// <summary>Imported is stored for a supplied file and reads as unknown everywhere; no trust change may re-evaluate it.</summary>
public enum DmarcResult { Unknown, Pass, Fail, None, Imported }

public static class DmarcResults
{
    public static string ToWire(this DmarcResult result) => result switch
    {
        DmarcResult.Pass => "pass",
        DmarcResult.Fail => "fail",
        DmarcResult.None => "none",
        _ => "unknown",
    };

    public static string ToStored(this DmarcResult result) => result == DmarcResult.Imported ? StoredImported : result.ToWire();

    public static DmarcResult? FromStored(string? value) => value switch
    {
        "pass" => DmarcResult.Pass,
        "fail" => DmarcResult.Fail,
        "none" => DmarcResult.None,
        "unknown" => DmarcResult.Unknown,
        StoredImported => DmarcResult.Imported,
        _ => null,
    };

    public const string StoredImported = "imported";
}

/// <summary>
/// What a trusted receiving server reported about DMARC for the From domain. Anything missing,
/// unreadable, untrusted or contradictory is <see cref="DmarcResult.Unknown"/>, never a pass.
/// </summary>
public static class SenderAuthentication
{
    /// <summary>Null means not evaluated: no headers were seen or no authserv-id is trusted, so nothing can be said.</summary>
    public static DmarcResult? Dmarc(SignalHeaders? headers, string? from, AuthservTrust trust)
    {
        ArgumentNullException.ThrowIfNull(trust);

        return headers is null || trust.IsEmpty ? null : Evaluate(headers.AuthenticationResults, from, trust);
    }

    public static DmarcResult Evaluate(IReadOnlyList<string> instances, string? from, AuthservTrust trust)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(trust);

        var domain = FromDomain(from);
        if (trust.IsEmpty || domain is null) return DmarcResult.Unknown;

        // Instances are topmost first and the final receiving server writes the top one: it decides, and a
        // lower trusted instance is never consulted, so a header the sender planted below cannot fill a gap.
        foreach (var instance in instances)
        {
            var id = AuthenticationResultsParser.ReadAuthservId(instance);
            if (id is null) return DmarcResult.Unknown;
            if (!trust.Matches(id)) continue;

            return Decide(instance, domain);
        }

        return DmarcResult.Unknown;
    }

    private static DmarcResult Decide(string instance, string fromDomain)
    {
        var parsed = AuthenticationResultsParser.Parse(instance);
        if (parsed is null) return DmarcResult.Unknown;

        DmarcResult? agreed = null;

        foreach (var result in parsed.Results)
        {
            if (!result.Method.Equals("dmarc", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.MethodVersion is not null && result.MethodVersion != "1") continue;
            if (!NamesDomain(result, fromDomain)) continue;

            var mapped = Map(result.Result);
            if (agreed is not null && mapped != agreed) return DmarcResult.Unknown;

            agreed = mapped;
        }

        return agreed ?? DmarcResult.Unknown;
    }

    private static DmarcResult Map(string result) => result.ToLowerInvariant() switch
    {
        "pass" => DmarcResult.Pass,
        "fail" => DmarcResult.Fail,
        "none" => DmarcResult.None,
        _ => DmarcResult.Unknown,
    };

    private static bool NamesDomain(AuthenticationResult result, string fromDomain)
    {
        var named = false;

        foreach (var property in result.Properties)
        {
            if (!property.Ptype.Equals("header", StringComparison.OrdinalIgnoreCase)
                || !property.Property.Equals("from", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Canonical(property.Value) != fromDomain) return false;
            named = true;
        }

        return named;
    }

    /// <summary>The domain of the only address in a From display string; null for none, several, or an unreadable one.</summary>
    public static string? FromDomain(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;

        var quoted = false;
        var angle = false;
        var open = -1;
        var close = -1;

        for (var i = 0; i < from.Length; i++)
        {
            var c = from[i];

            if (quoted)
            {
                if (c == '\\') i++;
                else if (c == '"') quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (angle)
            {
                if (c == '>') { angle = false; close = i; }
                else if (c == '<') return null;
            }
            else if (c == '<')
            {
                if (open >= 0) return null;
                angle = true;
                open = i;
            }
            else if (c is ',' or ';' or ':') return null;
        }

        if (quoted || angle) return null;

        var spec = open >= 0 ? from[(open + 1)..close] : from;
        return DomainOf(spec.Trim());
    }

    private static string? DomainOf(string spec)
    {
        var at = spec.LastIndexOf('@');
        if (at <= 0 || at == spec.Length - 1) return null;

        var domain = spec[(at + 1)..].Trim().TrimEnd('.');
        if (domain.Length == 0 || domain[0] == '.') return null;

        foreach (var c in domain)
            if (!(char.IsLetterOrDigit(c) || c is '-' or '.' or '_')) return null;

        return Canonical(domain);
    }

    // A server writes header.from as it received it (punycode or Unicode); compare in one form.
    private static string Canonical(string domain)
    {
        var trimmed = domain.Trim().TrimEnd('.').ToLowerInvariant();

        try
        {
            return trimmed.Length == 0 ? trimmed : Idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
    }

    private static readonly System.Globalization.IdnMapping Idn = new();
}
