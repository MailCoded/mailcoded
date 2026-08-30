using System.Globalization;

namespace Mailcoded.Core.Application;

/// <summary>The environment-derived inputs to <see cref="AgentPolicy"/>, parsed once and testable.</summary>
public sealed record AgentPolicyOptions
{
    public const string SendEnvVar = "MAILCODED_SEND";
    public const string ApprovedRecipientsEnvVar = "MAILCODED_APPROVED_RECIPIENTS";
    public const string EnableSqlEnvVar = "MAILCODED_ENABLE_SQL";

    public const int DefaultMaxSendsPerHour = 5;
    public const int DefaultSqlRowCap = 200;
    public const int MaxSqlRowCap = 1000;

    /// <summary>Off unless the environment says otherwise: send is default-deny on the agent surface.</summary>
    public bool SendEnabled { get; init; }

    /// <summary>Exact addresses and <c>@domain</c> patterns. Empty means no recipient is approved.</summary>
    public IReadOnlyList<string> ApprovedRecipients { get; init; } = [];

    public bool RawSqlEnabled { get; init; }

    public int MaxSendsPerHour { get; init; } = DefaultMaxSendsPerHour;

    public int SqlRowCap { get; init; } = DefaultSqlRowCap;

    public int SqlRowHardCap { get; init; } = MaxSqlRowCap;

    public static readonly AgentPolicyOptions Locked = new();

    public static AgentPolicyOptions FromEnvironment()
    {
        return new AgentPolicyOptions
        {
            SendEnabled = IsTruthy(Read(SendEnvVar)),
            RawSqlEnabled = IsTruthy(Read(EnableSqlEnvVar)),
            ApprovedRecipients = ParseRecipientPatterns(Read(ApprovedRecipientsEnvVar)),
        };
    }

    /// <summary>Splits the allowlist on commas, semicolons, and whitespace; lower-cases every entry.</summary>
    public static IReadOnlyList<string> ParseRecipientPatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var patterns = new List<string>();
        foreach (var part in raw.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = part.Trim().ToLowerInvariant();
            if (value.Length == 0 || value.Length > 320) continue;
            if (patterns.Contains(value, StringComparer.Ordinal)) continue;
            patterns.Add(value);
        }

        return patterns;
    }

    private static string? Read(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        return string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
    }

    internal string Describe() =>
        "send=" + (SendEnabled ? "on" : "off")
        + " allowlist=" + ApprovedRecipients.Count.ToString(CultureInfo.InvariantCulture)
        + " sql=" + (RawSqlEnabled ? "on" : "off");
}
