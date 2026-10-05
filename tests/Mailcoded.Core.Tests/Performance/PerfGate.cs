namespace Mailcoded.Core.Tests.Performance;

/// <summary>The opt-in switch for the 100k-message performance test; off, the test reports a skip and says why.</summary>
public static class PerfGate
{
    public const string EnvVar = "MAILCODED_TEST_PERF";

    public static bool Enabled => IsTruthy(Environment.GetEnvironmentVariable(EnvVar));

    public static string SkipReason =>
        $"Performance test SKIPPED: set {EnvVar}=1 to seed 100,000 Messages and measure them. "
        + "Nothing was measured, so no latency ceiling was checked.";

    public static bool IsTruthy(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
