namespace Mailcoded.Daemon;

/// <summary>
/// Build identity and the deterministic transcript switch the golden-file tests drive.
/// Transcript mode is opt-in only: it must never latch during normal operation.
/// </summary>
internal static class DaemonInfo
{
    public const string TranscriptEnvVar = "MAILCODED_TRANSCRIPT";
    public const string ParentPidEnvVar = "MAILCODED_PARENT_PID";

    public const string ProductVersion = "0.1.0";
    public const string TranscriptVersion = "0.0.0-transcript";

    /// <summary>The one wall-clock value transcript mode ever emits, so a golden file stays byte-stable.</summary>
    public const string TranscriptInstant = "2000-01-01T00:00:00.000Z";

    private static bool deterministic;

    public static bool Deterministic => deterministic;

    public static string Version => deterministic ? TranscriptVersion : ProductVersion;

    public static void EnableTranscriptMode() => deterministic = true;

    public static bool TranscriptRequestedByEnvironment()
    {
        string? raw;
        try
        {
            raw = Environment.GetEnvironmentVariable(TranscriptEnvVar);
        }
        catch (Exception)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(raw)) return false;
        var value = raw.Trim();
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Zeroed in transcript mode so a measured duration cannot reach a golden file.</summary>
    public static long Measured(long value) => deterministic ? 0 : value;

    public static int Measured(int value) => deterministic ? 0 : value;
}
